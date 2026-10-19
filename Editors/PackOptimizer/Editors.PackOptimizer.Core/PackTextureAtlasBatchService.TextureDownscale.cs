using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using DirectXTexNet;
using Editors.ImportExport.Dds;
using Shared.Core.PackFiles.Models;
using Shared.GameFormats.RigidModel;

namespace Editors.PackOptimizer.Core
{
    public sealed partial class PackTextureAtlasBatchService
    {
        // Standalone "downscale oversized textures". It decides per texture FILE how far
        // the file may shrink, using the same texel-density reference, outlier multiplier,
        // clamps and 0.5 / 0.25 buckets the atlas stage applies to atlas placements
        // (CalculateTexelDensityResolutionScale), and rewrites the file in place: same
        // path, same format, so no material changes.
        //
        // The analysis reads the OUTPUT pack after atlasing, merging and pruning. A
        // texture is assessed against the materials that still reference it there. Mesh
        // parts are taken from the SOURCE WSModels and rigids (a part atlased in this run
        // no longer uses its source material and is dropped; a merged part is judged by
        // its original pieces, which is never less strict than the merged average).
        private const double TextureDownscaleBudgetSeconds = 45;
        private const int TextureDownscaleListLimit = 60;
        private const int TextureDownscaleExampleLimit = 3;
        private const double TextureDownscaleLargestScale = 0.5;
        private const int TextureDownscaleHeaderPeekBytes = 148;

        private const string TextureDownscaleReasonNotOwned =
            "texture is not owned by the source pack (vanilla or unresolved)";
        private const string TextureDownscaleReasonOverridesVanilla =
            "texture path also exists in vanilla (source pack overrides it)";
        private const string TextureDownscaleReasonBadDds =
            "DDS header unreadable or layout unsupported (uncompressed, cube map, array)";
        private const string TextureDownscaleReasonSlot =
            "also used in a slot that is not atlasable (legacy s_*, blood, dither, ...)";
        private const string TextureDownscaleReasonNonMaterial =
            "referenced by a non-material file (VMD, WSModel, text, direct rigid)";
        private const string TextureDownscaleReasonUnreadableReference =
            "may be referenced by an unreadable material or file";
        private const string TextureDownscaleReasonGeneratedMaterial =
            "referenced by an atlas-generated material (UV0 remapped, user not assessable)";
        private const string TextureDownscaleReasonRepackMaterial =
            "referenced by a shared-texture-repack material (UVs remapped, user not assessable)";
        private const string TextureDownscaleReasonMaterialOverridesVanilla =
            "a referencing material overrides a vanilla material (vanilla users unknown)";
        private const string TextureDownscaleReasonNoUser =
            "a referencing material has no known mesh-part user";
        private const string TextureDownscaleReasonUnassessableUser =
            "a mesh part using it cannot be assessed";
        private const string TextureDownscaleReasonNoReference =
            "no pack reference density (fewer than 2 assessed parts)";
        private const string TextureDownscaleReasonFormat =
            "format cannot be written (no complete mip chain to slice, not BC1/BC3 re-encodable)";
        private const string TextureDownscaleReasonMinimumDimension =
            "would go below the minimum dimension";
        private const string TextureDownscaleReasonCharacter =
            "used by a lord or hero (single-character unit)";
        private const string TextureDownscaleReasonCharacterUnclassified =
            "used by a gameplay root that could not be classified as lord/hero or rank-and-file";

        private static readonly Regex TextureDownscaleTextureTokenPattern =
            new(@"[^\s<>""'=,;|()]+\.dds", RegexOptions.Compiled);

        private enum TextureDownscaleOutcome
        {
            Candidate,
            NeedsFullResolution,
            BelowMinimumDimension,
        }

        private enum TextureDownscaleRootKind
        {
            RankAndFile,
            Character,
            Unclassified,
        }

        // What a source WSModel is known to be used for in gameplay: reachable from a
        // lord/hero root, or from a root that could not be classified. A WSModel that is
        // reachable only from rank-and-file roots (or from no root) has no link.
        private sealed record TextureDownscaleGameplayLink(
            TextureDownscaleRootKind Kind,
            string Detail);

        private readonly record struct TextureDownscaleDecision(
            TextureDownscaleOutcome Outcome,
            double Scale,
            int NewWidth,
            int NewHeight,
            int LimitingIndex);

        // Header facts of a block-compressed 2D DDS, derived through the residency
        // estimator so sizes agree with the rest of the report.
        private sealed record DdsLayout(
            int Width,
            int Height,
            int MipCount,
            string Format,
            int BytesPerBlock,
            bool IsDx10,
            uint DxgiFormat,
            bool IsSrgb,
            int HeaderSize,
            long ChainBytes,
            byte[] Header)
        {
            public string FormatDescription
                => $"{Format}{(IsSrgb ? "_SRGB" : string.Empty)} " +
                   $"({(IsDx10 ? "DX10 header" : "legacy header")}, {MipCount} mip(s))";

            public long LevelBytes(int level)
                => GetDdsLevelBytes(
                    Math.Max(1, Width >> level),
                    Math.Max(1, Height >> level),
                    BytesPerBlock);
        }

        private sealed record TextureDownscaleMaterial(
            string Path,
            bool Readable,
            bool Generated,
            bool GeneratedByRepack,
            bool OverridesVanilla,
            IReadOnlyList<(string Slot, string Texture)> Textures);

        // One mesh part (rigid, LOD, part) that a WSModel binds to a material.
        private sealed record TextureDownscaleUse(
            string WsModelPath,
            string RigidPath,
            int LodIndex,
            int PartIndex,
            double UvPerWorld,
            string UnassessableReason)
        {
            public bool IsAssessable => UnassessableReason.Length == 0;
            public string Description => $"{WsModelPath} [{RigidPath} lod {LodIndex}, part {PartIndex}]";
        }

        private sealed record TextureDownscaleFile(
            string Path,
            DdsLayout? Layout,
            string Problem);

        private sealed record TextureDownscaleCandidate(
            string Path,
            int Width,
            int Height,
            int NewWidth,
            int NewHeight,
            double Scale,
            string Format,
            string FormatDescription,
            bool IsSrgb,
            bool IsDx10,
            bool UseSlicing,
            int SkipLevels,
            long BytesBefore,
            long BytesAfter,
            int MaterialCount,
            int PartCount,
            string LimitingUser,
            double LimitingRelativeDensity)
        {
            public long PixelsBefore => (long)Width * Height;
            public long PixelsAfter => (long)NewWidth * NewHeight;
            public long BytesSaved => BytesBefore - BytesAfter;
        }

        private sealed class TextureDownscaleInputs
        {
            public bool AtlasActive { get; set; }

            // Output-pack materials, by normalized path.
            public Dictionary<string, TextureDownscaleMaterial> Materials { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            // Source-WSModel bindings (atlased parts removed), by material path.
            public Dictionary<string, List<TextureDownscaleUse>> UsesByMaterial { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            // Bindings dropped because the part was atlased in this run, by source
            // material path, plus the textures those source materials reference.
            public Dictionary<string, int> ExcludedUsesByMaterial { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, HashSet<string>> ExcludedMaterialTextures { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> UnreadableReferencedTextures { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            // Texture path -> first non-material file that mentions it.
            public Dictionary<string, string> NonMaterialReferences { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, TextureDownscaleFile> Files { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> SourceTextureFiles { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> OutputTextureFiles { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> GeneratedTextures { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            // The part of GeneratedTextures the shared texture repack wrote.
            public HashSet<string> RepackGeneratedTextures { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> VanillaOverrideTextures { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            // Source WSModel path -> its lord/hero or unclassified gameplay link. When the
            // gameplay resolution is unavailable no link can be trusted and the reason is
            // set instead (null means the character rule was applied).
            public Dictionary<string, TextureDownscaleGameplayLink> GameplayLinksByWsModel { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public string? CharacterRuleUnavailableReason { get; set; }

            // Texel densities per UV unit already multiplied by the base-colour size.
            public List<double> PopulationDensities { get; } = [];
            public double? AtlasReferenceDensity { get; set; }
            public int AtlasReferenceCount { get; set; }

            public int MaterialsScanned { get; set; }
            public int UnreadableMaterials { get; set; }
            public int WsModelsScanned { get; set; }
            public int UnreadableWsModels { get; set; }
            public int RigidsRead { get; set; }
            public int RigidsUnavailable { get; set; }
            public int RigidsSkippedForTime { get; set; }
            public int PartsAssessed { get; set; }
            public int PartsUnassessable { get; set; }
            public int TextFilesScanned { get; set; }
            public bool BudgetExhausted { get; set; }
            public TimeSpan Budget { get; set; }
        }

        private sealed class TextureDownscalePlan
        {
            public bool AtlasActive { get; set; }
            public double? ReferenceDensity { get; set; }
            public string ReferenceOrigin { get; set; } = string.Empty;
            public int ReferenceSamples { get; set; }

            public int TexturesAssessed { get; set; }
            public int AtlasRemovedAllUsers { get; set; }
            public int AssessedWithReducedUserSet { get; set; }
            public int GeneratedUntouched { get; set; }
            public int RepackGeneratedUntouched { get; set; }
            public int Unreferenced { get; set; }
            public int NeedsFullResolution { get; set; }
            public int TexturesConsidered { get; set; }
            public int CharacterWsModels { get; set; }
            public int UnclassifiedWsModels { get; set; }
            public string? CharacterRuleUnavailableReason { get; set; }
            public List<TextureDownscaleCandidate> Candidates { get; } = [];
            public Dictionary<string, int> IneligibleReasons { get; } =
                new(StringComparer.Ordinal);
            public Dictionary<string, List<string>> IneligibleExamples { get; } =
                new(StringComparer.Ordinal);

            // Copied from the inputs for the report.
            public int MaterialsScanned { get; set; }
            public int UnreadableMaterials { get; set; }
            public int WsModelsScanned { get; set; }
            public int UnreadableWsModels { get; set; }
            public int RigidsRead { get; set; }
            public int RigidsUnavailable { get; set; }
            public int RigidsSkippedForTime { get; set; }
            public int PartsAssessed { get; set; }
            public int PartsUnassessable { get; set; }
            public int TextFilesScanned { get; set; }
            public bool BudgetExhausted { get; set; }
            public TimeSpan Budget { get; set; }
        }

        private sealed class TextureDownscaleResult
        {
            public bool Requested { get; set; }
            public bool Applied { get; set; }
            public TextureDownscalePlan? Plan { get; set; }
            public TimeSpan AnalysisElapsed { get; set; }
            public TimeSpan RewriteElapsed { get; set; }
            public string? Failure { get; set; }
            public List<TextureDownscaleCandidate> Rewritten { get; } = [];
            public List<string> RewriteFailures { get; } = [];
            public int Sliced { get; set; }
            public int Reencoded { get; set; }
            // Textures an earlier stage sized with the early estimate whose late decision differs.
            public List<string> EarlyMismatches { get; } = [];
            public int EarlyComparedTextures { get; set; }
        }

        // ---------------------------------------------------------------------------
        // Scale decision (pure).
        // ---------------------------------------------------------------------------

        private static bool IsTextureDownscaleSlot(string slot)
        {
            // The atlas density code only reads the four atlas channels (t_xml_*); the
            // legacy s_* slots are not handled there, so they are not atlasable here.
            foreach (var channel in AtlasChannels)
            {
                if (channel.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        // The texture needs the largest scale any of its users needs. Each user density is
        // measured in texels per world unit at this texture's own size. Candidate only if
        // that largest scale is at most one half; the result must also keep both sides at
        // or above the atlas minimum dimension (0.25 steps down to 0.5 when needed).
        private static TextureDownscaleDecision ChooseTextureDownscale(
            IReadOnlyList<double> userDensities,
            double reference,
            int width,
            int height)
        {
            var scale = 0.0;
            var limiting = -1;
            for (var index = 0; index < userDensities.Count; index++)
            {
                var density = userDensities[index];
                if (!double.IsFinite(density) || density <= 0)
                    return new TextureDownscaleDecision(
                        TextureDownscaleOutcome.NeedsFullResolution, 1.0, width, height, index);

                // int.MaxValue disables the atlas's size clamp; the minimum dimension is
                // applied below on both sides instead of on the longer side only.
                var userScale = CalculateTexelDensityResolutionScale(
                    density,
                    reference,
                    int.MaxValue);
                if (limiting < 0 ||
                    userScale > scale ||
                    (userScale == scale && density > userDensities[limiting]))
                {
                    scale = userScale;
                    limiting = index;
                }
            }

            if (limiting < 0 || scale > TextureDownscaleLargestScale)
            {
                return new TextureDownscaleDecision(
                    TextureDownscaleOutcome.NeedsFullResolution, 1.0, width, height, limiting);
            }

            var attempts = scale < TextureDownscaleLargestScale
                ? new[] { scale, TextureDownscaleLargestScale }
                : new[] { scale };
            foreach (var attempt in attempts)
            {
                var newWidth = Math.Max(1, (int)Math.Ceiling(width * attempt));
                var newHeight = Math.Max(1, (int)Math.Ceiling(height * attempt));
                if (Math.Min(newWidth, newHeight) >= MinAtlasResolutionScaleDimension)
                {
                    return new TextureDownscaleDecision(
                        TextureDownscaleOutcome.Candidate, attempt, newWidth, newHeight, limiting);
                }
            }

            return new TextureDownscaleDecision(
                TextureDownscaleOutcome.BelowMinimumDimension, scale, width, height, limiting);
        }

        // Both axes of a part's UV-to-world ratio: sqrt(sum of UV triangle area / sum of
        // world triangle area), skipping degenerate triangles like TryCalculateTexelDensity.
        // Multiplied by sqrt(width * height) it is that function's texel density.
        private static bool TryCalculateUvPerWorld(RmvModel model, out double uvPerWorld)
        {
            uvPerWorld = 0;
            var vertices = model.Mesh?.VertexList;
            var indices = model.Mesh?.IndexList;
            if (vertices == null || indices == null ||
                indices.Length < 3 || indices.Length % 3 != 0)
            {
                return false;
            }

            double worldArea = 0;
            double uvArea = 0;
            for (var index = 0; index < indices.Length; index += 3)
            {
                var ia = indices[index];
                var ib = indices[index + 1];
                var ic = indices[index + 2];
                if (ia >= vertices.Length || ib >= vertices.Length || ic >= vertices.Length)
                    return false;

                var a = vertices[ia];
                var b = vertices[ib];
                var c = vertices[ic];
                var ab = b.GetPosistionAsVec3() - a.GetPosistionAsVec3();
                var ac = c.GetPosistionAsVec3() - a.GetPosistionAsVec3();
                var triangleWorldArea =
                    System.Numerics.Vector3.Cross(ab, ac).Length() * 0.5;
                if (triangleWorldArea <= 0)
                    continue;

                var triangleUvArea = Math.Abs(
                    (b.Uv.X - a.Uv.X) * (c.Uv.Y - a.Uv.Y) -
                    (b.Uv.Y - a.Uv.Y) * (c.Uv.X - a.Uv.X)) * 0.5;
                if (triangleUvArea <= 0)
                    continue;

                worldArea += triangleWorldArea;
                uvArea += triangleUvArea;
            }

            if (worldArea <= double.Epsilon || uvArea <= double.Epsilon)
                return false;

            uvPerWorld = Math.Sqrt(uvArea / worldArea);
            return double.IsFinite(uvPerWorld) && uvPerWorld > 0;
        }

        // ---------------------------------------------------------------------------
        // DDS container helpers.
        // ---------------------------------------------------------------------------

        private static long GetDdsLevelBytes(int width, int height, int bytesPerBlock)
            => Math.Max(1L, (width + 3L) / 4L) *
               Math.Max(1L, (height + 3L) / 4L) *
               bytesPerBlock;

        private static int GetFullMipChainLength(int width, int height)
        {
            var longest = Math.Max(1, Math.Max(width, height));
            return 1 + (int)Math.Floor(Math.Log2(longest));
        }

        private static bool TryReadDdsLayout(byte[] header, out DdsLayout layout)
        {
            layout = null!;
            if (!DdsBcnResidencyEstimator.TryEstimate(header, out var estimate))
                return false;

            const uint dx10 = 0x30315844;
            var isDx10 = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84, 4)) == dx10;
            var headerSize = isDx10 ? 148 : 128;
            if (header.Length < headerSize)
                return false;

            var dxgi = isDx10
                ? BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(128, 4))
                : 0u;
            // BC1 / BC2 / BC3 / BC7 sRGB variants.
            var srgb = isDx10 && (dxgi == 72 || dxgi == 75 || dxgi == 78 || dxgi == 99);
            layout = new DdsLayout(
                estimate.Width,
                estimate.Height,
                estimate.MipCount,
                estimate.Format,
                estimate.Format is "BC1" or "BC4" ? 8 : 16,
                isDx10,
                dxgi,
                srgb,
                headerSize,
                estimate.Bytes,
                header.AsSpan(0, headerSize).ToArray());
            return true;
        }

        private static TextureDownscaleFile ReadTextureDownscaleHeader(
            string path,
            PackFile? file)
        {
            if (file == null)
                return new TextureDownscaleFile(path, null, "file not found");

            byte[] header;
            try
            {
                try
                {
                    header = file.DataSource.PeekData(TextureDownscaleHeaderPeekBytes);
                }
                catch (Exception)
                {
                    header = file.DataSource.PeekData(128);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new TextureDownscaleFile(path, null, $"header unreadable ({ex.GetType().Name})");
            }

            return TryReadDdsLayout(header, out var layout)
                ? new TextureDownscaleFile(path, layout, string.Empty)
                : new TextureDownscaleFile(path, null, "unsupported DDS layout");
        }

        // The source header with new dimensions and mip count, as a standalone header the
        // estimator and the writer can use.
        private static byte[] BuildDdsHeader(
            DdsLayout source,
            int width,
            int height,
            int mipCount)
        {
            var header = (byte[])source.Header.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), height);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), width);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28, 4), mipCount);

            const uint mipMapCountFlag = 0x20000;
            const uint linearSizeFlag = 0x80000;
            const uint complexCaps = 0x8;
            const uint mipMapCaps = 0x400000;
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
            var caps = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(108, 4));
            if (mipCount > 1)
            {
                flags |= mipMapCountFlag;
                caps |= complexCaps | mipMapCaps;
            }
            else
            {
                flags &= ~mipMapCountFlag;
                caps &= ~(complexCaps | mipMapCaps);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), flags);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(108, 4), caps);
            if ((flags & linearSizeFlag) != 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    header.AsSpan(20, 4),
                    checked((uint)GetDdsLevelBytes(width, height, source.BytesPerBlock)));
            }

            return header;
        }

        private static long EstimateDdsChainBytes(
            DdsLayout source,
            int width,
            int height,
            int mipCount)
            => DdsBcnResidencyEstimator.TryEstimate(
                BuildDdsHeader(source, width, height, mipCount),
                out var estimate)
                ? estimate.Bytes
                : 0;

        // Drops the first `skipLevels` mip levels byte for byte. The compressed blocks of
        // the remaining levels are the author's own data, untouched.
        private static bool TrySliceDdsMips(
            byte[] dds,
            DdsLayout layout,
            int skipLevels,
            out byte[] result,
            out string error)
        {
            result = [];
            error = string.Empty;
            if (skipLevels < 1 || skipLevels >= layout.MipCount)
            {
                error = "source has no mip level to slice to";
                return false;
            }

            if (dds.LongLength < layout.HeaderSize + layout.ChainBytes)
            {
                error = "source is shorter than its header declares";
                return false;
            }

            var newWidth = Math.Max(1, layout.Width >> skipLevels);
            var newHeight = Math.Max(1, layout.Height >> skipLevels);
            var newMipCount = layout.MipCount - skipLevels;
            long offset = layout.HeaderSize;
            for (var level = 0; level < skipLevels; level++)
                offset += layout.LevelBytes(level);

            var dataLength = layout.HeaderSize + layout.ChainBytes - offset;
            var header = BuildDdsHeader(layout, newWidth, newHeight, newMipCount);
            result = new byte[header.Length + dataLength];
            header.CopyTo(result, 0);
            Buffer.BlockCopy(dds, checked((int)offset), result, header.Length, checked((int)dataLength));
            return true;
        }

        private static bool TryDecodeDdsTopLevel(
            byte[] dds,
            out byte[] bgra,
            out int width,
            out int height,
            out string error)
        {
            bgra = [];
            width = height = 0;
            error = string.Empty;
            try
            {
                using var stream = new MemoryStream(dds);
                using var image = Pfim.Pfimage.FromStream(stream);
                width = image.Width;
                height = image.Height;
                var channels = image.Format switch
                {
                    Pfim.ImageFormat.Rgba32 => 4,
                    Pfim.ImageFormat.Rgb24 => 3,
                    _ => 0,
                };
                if (channels == 0)
                {
                    error = $"decoded pixel format {image.Format} is not supported";
                    return false;
                }

                bgra = new byte[checked(width * height * 4)];
                for (var y = 0; y < height; y++)
                {
                    var source = y * image.Stride;
                    var target = y * width * 4;
                    for (var x = 0; x < width; x++)
                    {
                        bgra[target + x * 4] = image.Data[source + x * channels];
                        bgra[target + x * 4 + 1] = image.Data[source + x * channels + 1];
                        bgra[target + x * 4 + 2] = image.Data[source + x * channels + 2];
                        bgra[target + x * 4 + 3] = channels == 4
                            ? image.Data[source + x * channels + 3]
                            : (byte)255;
                    }
                }

                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = $"decode failed ({ex.GetType().Name}: {ex.Message})";
                return false;
            }
        }

        // Decode the full-resolution image, shrink it with an area filter in the colour
        // space the format implies, build a complete mip chain and compress to the
        // source's own block format and header kind. Only BC1 and BC3 are re-encoded (the
        // formats the atlas writer produces); normals and masks are filtered linearly and,
        // like the atlas builder, are not renormalised.
        private static bool TryReencodeDds(
            byte[] dds,
            DdsLayout layout,
            int newWidth,
            int newHeight,
            bool srgb,
            out byte[] result,
            out string error)
        {
            result = [];
            error = string.Empty;
            var uncompressed = srgb
                ? DXGI_FORMAT.B8G8R8A8_UNORM_SRGB
                : DXGI_FORMAT.B8G8R8A8_UNORM;
            var compressed = layout.Format switch
            {
                "BC1" => srgb ? DXGI_FORMAT.BC1_UNORM_SRGB : DXGI_FORMAT.BC1_UNORM,
                "BC3" => srgb ? DXGI_FORMAT.BC3_UNORM_SRGB : DXGI_FORMAT.BC3_UNORM,
                _ => DXGI_FORMAT.UNKNOWN,
            };
            if (compressed == DXGI_FORMAT.UNKNOWN)
            {
                error = $"{layout.Format} cannot be re-encoded";
                return false;
            }

            if (!TryDecodeDdsTopLevel(dds, out var bgra, out var width, out var height, out error))
                return false;

            using var full = TexHelper.Instance.Initialize2D(
                uncompressed, width, height, 1, 1, CP_FLAGS.NONE);
            var image = full.GetImage(0, 0, 0);
            var rowBytes = width * 4;
            var pitch = checked((int)image.RowPitch);
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(
                    bgra,
                    y * rowBytes,
                    IntPtr.Add(image.Pixels, y * pitch),
                    rowBytes);
            }

            using var resized = full.Resize(newWidth, newHeight, TEX_FILTER_FLAGS.BOX);
            using var withMips = resized.GenerateMipMaps(TEX_FILTER_FLAGS.DEFAULT, 0);
            if (DdsEncodeCapture.Enabled)
            {
                DdsEncodeCapture.Record(
                    "TryReencodeDds", full, resized, withMips, compressed,
                    TEX_COMPRESS_FLAGS.DEFAULT, 0.5f,
                    "Resize(BOX) then GenerateMipMaps(DEFAULT, levels=0)");
            }

            using var packed = withMips.Compress(compressed, TEX_COMPRESS_FLAGS.DEFAULT, 0.5f);
            // Keep a DX10 header DX10 even for UNORM formats, so the header kind survives.
            using var stream = packed.SaveToDDSMemory(
                layout.IsDx10 ? DDS_FLAGS.FORCE_DX10_EXT : DDS_FLAGS.NONE);
            result = new byte[stream.Length];
            stream.ReadExactly(result, 0, result.Length);
            return true;
        }

        // Structure (and for BC1-BC3 a decode) of a rewritten texture against its plan.
        private static string? CheckDownscaledDds(
            byte[] dds,
            TextureDownscaleCandidate expected)
        {
            byte[] header;
            try
            {
                header = dds.AsSpan(0, Math.Min(dds.Length, TextureDownscaleHeaderPeekBytes)).ToArray();
            }
            catch (Exception ex)
            {
                return $"unreadable ({ex.GetType().Name})";
            }

            if (!TryReadDdsLayout(header, out var layout))
                return "DDS header is unreadable or unsupported";
            if (layout.Width != expected.NewWidth || layout.Height != expected.NewHeight)
            {
                return $"is {layout.Width}x{layout.Height}, planned " +
                       $"{expected.NewWidth}x{expected.NewHeight}";
            }

            if (!layout.Format.Equals(expected.Format, StringComparison.Ordinal) ||
                layout.IsSrgb != expected.IsSrgb ||
                layout.IsDx10 != expected.IsDx10)
            {
                return $"format is {layout.Format}{(layout.IsSrgb ? "_SRGB" : string.Empty)}, " +
                       $"planned {expected.Format}{(expected.IsSrgb ? "_SRGB" : string.Empty)}" +
                       $" with a {(expected.IsDx10 ? "DX10" : "legacy")} header";
            }

            var fullChain = GetFullMipChainLength(expected.NewWidth, expected.NewHeight);
            if (layout.MipCount != fullChain)
                return $"has {layout.MipCount} mip level(s), a complete chain has {fullChain}";
            if (dds.LongLength != layout.HeaderSize + layout.ChainBytes)
            {
                return $"is {dds.LongLength:N0} bytes, header and mip chain need " +
                       $"{layout.HeaderSize + layout.ChainBytes:N0}";
            }

            if (layout.Format is "BC1" or "BC2" or "BC3")
            {
                if (!TryDecodeDdsTopLevel(dds, out _, out var width, out var height, out var decodeError))
                    return decodeError;
                if (width != expected.NewWidth || height != expected.NewHeight)
                    return $"decodes to {width}x{height}";
            }

            return null;
        }

        // ---------------------------------------------------------------------------
        // Input collection: the only part that reads the packs.
        // ---------------------------------------------------------------------------

        private static bool IsTextureDownscaleTextFile(string path)
            => path.EndsWith(".wsmodel", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".variantmeshdefinition", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".fx_fragment", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        // A material slot value that is not a texture file path (the "mask_path" /
        // "base_colour_path" placeholders, an empty value, anything without a .dds
        // extension) is not a texture: the atlas discovery skips the placeholder and
        // treats the rest as unresolved, so none of them is counted or assessed here.
        private static bool IsTextureDownscaleTexturePath(string? path)
        {
            var normalized = Normalize(path);
            return normalized.Length != 0 &&
                   !IsTexturePlaceholder(normalized) &&
                   normalized.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> ExtractDownscaleTextureTokens(PackFile file)
        {
            var text = Encoding.UTF8.GetString(file.DataSource.ReadData());
            var tokens = new List<string>();
            foreach (Match match in TextureDownscaleTextureTokenPattern.Matches(text))
            {
                var token = Normalize(match.Value);
                if (token.Length != 0)
                    tokens.Add(token);
            }

            return tokens;
        }

        private static Dictionary<string, List<(string Material, string Slot)>>
            BuildDownscaleReferenceIndex(
                IReadOnlyDictionary<string, TextureDownscaleMaterial> materials)
        {
            var index = new Dictionary<string, List<(string Material, string Slot)>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var material in materials.Values.Where(item => item.Readable))
            {
                foreach (var (slot, texture) in material.Textures)
                {
                    if (!index.TryGetValue(texture, out var references))
                    {
                        references = [];
                        index[texture] = references;
                    }

                    references.Add((material.Path, slot));
                }
            }

            return index;
        }

        private static bool ShouldTreatEmbeddedTexturesAsReferences(
            BatchState state,
            string rigidPath)
            => HasDirectGameplayRigidConsumer(state, rigidPath) ||
               !IsGameplayConsumerDiscoverySafeForAsset(state, rigidPath);

        private static void AddEmbeddedRigidReferences(
            TextureDownscaleInputs inputs,
            string rigidPath,
            RmvFile rigid)
        {
            foreach (var lod in rigid.ModelList ?? [])
            {
                foreach (var model in lod)
                {
                    try
                    {
                        foreach (var texture in model.Material?.GetAllTextures() ?? [])
                        {
                            var path = Normalize(texture.Path);
                            if (IsTextureDownscaleTexturePath(path))
                                inputs.NonMaterialReferences.TryAdd(path, $"embedded in {rigidPath}");
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A part with an unreadable embedded material is covered by the
                        // part itself being unassessable or by the next rigid pass.
                    }
                }
            }
        }

        private static RmvFile? LoadTextureDownscaleRigid(BatchState state, string path)
        {
            // Always a fresh read of the original bytes: the cached models in
            // state.RigidModels carry atlas-remapped UVs and merged parts.
            var file = ResolveGameplayTraversalContainer(
                    state.Source,
                    GetGameplayTraversalContainers(state),
                    path)?
                .FindFile(path);
            if (file == null)
                return null;

            try
            {
                return ModelFactory.Create().Load(file.DataSource.ReadData());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }

        private enum SourceWsModelBindingKind
        {
            Complete,
            // The binding has no geometry, LOD or part index.
            Incomplete,
            // The WSModel could not be parsed; the material came from a text scan.
            UnreadableWsModel,
        }

        private sealed record SourceWsModelBinding(
            string WsModelPath,
            string Geometry,
            int Lod,
            int Part,
            string Material,
            SourceWsModelBindingKind Kind);

        private sealed record SourceWsModelBindingScan(
            List<SourceWsModelBinding> Bindings,
            int WsModelsScanned,
            int UnreadableWsModels);

        // Every (WSModel, rigid, LOD, part) -> material binding of the given source files, in
        // file order. Shared by the texture downscale analysis and the vanilla texture
        // coverage report; callers decide what to do with incomplete and unreadable ones.
        private static SourceWsModelBindingScan EnumerateSourceWsModelBindings(
            IEnumerable<KeyValuePair<string, PackFile>> sourceFiles,
            CancellationToken cancellationToken)
        {
            var bindings = new List<SourceWsModelBinding>();
            var scanned = 0;
            var unreadable = 0;
            foreach (var (rawPath, file) in sourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Normalize(rawPath);
                if (!path.EndsWith(".wsmodel", StringComparison.OrdinalIgnoreCase))
                    continue;

                scanned++;
                XmlDocument document;
                try
                {
                    document = LoadXml(file);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    unreadable++;
                    var text = Encoding.UTF8.GetString(file.DataSource.ReadData());
                    foreach (Match match in Regex.Matches(
                                 text,
                                 @"[^\s<>""'=,;|()]+\.xml\.material",
                                 RegexOptions.IgnoreCase))
                    {
                        bindings.Add(new SourceWsModelBinding(
                            path, string.Empty, -1, -1, Normalize(match.Value),
                            SourceWsModelBindingKind.UnreadableWsModel));
                    }

                    continue;
                }

                var geometry = Normalize(document.SelectSingleNode("/model/geometry")?.InnerText);
                var materialNodes = document.SelectNodes("/model/materials/material");
                if (materialNodes == null)
                    continue;

                foreach (XmlNode node in materialNodes)
                {
                    var material = Normalize(node.InnerText);
                    if (material.Length == 0)
                        continue;

                    if (geometry.Length == 0 ||
                        !TryParseIndex(node, "lod_index", out var lod) ||
                        !TryParseIndex(node, "part_index", out var part))
                    {
                        bindings.Add(new SourceWsModelBinding(
                            path, geometry, -1, -1, material,
                            SourceWsModelBindingKind.Incomplete));
                        continue;
                    }

                    bindings.Add(new SourceWsModelBinding(
                        path, geometry, lod, part, material,
                        SourceWsModelBindingKind.Complete));
                }
            }

            return new SourceWsModelBindingScan(bindings, scanned, unreadable);
        }

        private static TextureDownscaleInputs CollectTextureDownscaleInputs(
            BatchState state,
            Func<string, RmvFile?> loadRigid,
            TimeSpan? budget,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var inputs = new TextureDownscaleInputs
            {
                AtlasActive = state.TextureAtlasingActive,
                Budget = budget ?? TimeSpan.Zero,
            };
            var timer = Stopwatch.StartNew();
            var containers = GetGameplayTraversalContainers(state);
            var vanillaCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            bool OverridesVanilla(string path)
            {
                if (vanillaCache.TryGetValue(path, out var cached))
                    return cached;

                var result = false;
                foreach (var container in containers)
                {
                    if (container.IsCaPackFile &&
                        !ReferenceEquals(container, state.Source) &&
                        container.ContainsFile(path))
                    {
                        result = true;
                        break;
                    }
                }

                vanillaCache[path] = result;
                return result;
            }

            void AddUse(string materialPath, TextureDownscaleUse use)
            {
                if (!inputs.UsesByMaterial.TryGetValue(materialPath, out var uses))
                {
                    uses = [];
                    inputs.UsesByMaterial[materialPath] = uses;
                }

                uses.Add(use);
            }

            // 1. The output pack: materials (pack-wide), texture files, text references.
            var generatedMaterials = state.GeneratedMaterialPaths
                .Concat(state.SharedRepackMaterialPaths)
                .Select(Normalize)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            inputs.GeneratedTextures.UnionWith(state.GeneratedTexturePaths.Select(Normalize));
            inputs.GeneratedTextures.UnionWith(state.SharedRepackTexturePaths.Select(Normalize));
            inputs.RepackGeneratedTextures.UnionWith(state.SharedRepackTexturePaths.Select(Normalize));
            var repackMaterials = state.SharedRepackMaterialPaths
                .Select(Normalize)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (rawPath, file) in state.Output.GetAllFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Normalize(rawPath);
                if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    inputs.OutputTextureFiles.Add(path);
                    continue;
                }

                if (path.EndsWith(".xml.material", StringComparison.OrdinalIgnoreCase))
                {
                    inputs.MaterialsScanned++;
                    var generated = generatedMaterials.Contains(path);
                    var generatedByRepack = repackMaterials.Contains(path);
                    var overridesVanilla = OverridesVanilla(path);
                    try
                    {
                        var document = LoadXml(file);
                        var textures = new List<(string Slot, string Texture)>();
                        var nodes = document.SelectNodes("/material/textures/texture");
                        if (nodes != null)
                        {
                            foreach (XmlNode node in nodes)
                            {
                                var texture = Normalize(
                                    node.SelectSingleNode("source")?.InnerText ?? node.InnerText);
                                if (!IsTextureDownscaleTexturePath(texture))
                                    continue;

                                textures.Add((GetTextureSlot(node), texture));
                            }
                        }

                        inputs.Materials[path] = new TextureDownscaleMaterial(
                            path, true, generated, generatedByRepack, overridesVanilla, textures);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        inputs.UnreadableMaterials++;
                        inputs.Materials[path] = new TextureDownscaleMaterial(
                            path, false, generated, generatedByRepack, overridesVanilla, []);
                        inputs.UnreadableReferencedTextures.UnionWith(
                            ExtractDownscaleTextureTokens(file));
                    }

                    continue;
                }

                if (IsTextureDownscaleTextFile(path))
                {
                    inputs.TextFilesScanned++;
                    foreach (var token in ExtractDownscaleTextureTokens(file))
                        inputs.NonMaterialReferences.TryAdd(token, path);
                }
            }

            // 2. Source-pack textures and the WSModel bindings (the parts that use materials).
            var sourceFiles = state.Source.GetAllFiles();
            foreach (var rawPath in sourceFiles.Keys)
            {
                var path = Normalize(rawPath);
                if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                    inputs.SourceTextureFiles.Add(path);
            }

            var bindingScan = EnumerateSourceWsModelBindings(sourceFiles, cancellationToken);
            inputs.WsModelsScanned = bindingScan.WsModelsScanned;
            inputs.UnreadableWsModels = bindingScan.UnreadableWsModels;
            var pending = new List<(string Ws, string Rigid, int Lod, int Part, string Material)>();
            foreach (var binding in bindingScan.Bindings)
            {
                switch (binding.Kind)
                {
                    case SourceWsModelBindingKind.UnreadableWsModel:
                        AddUse(
                            binding.Material,
                            new TextureDownscaleUse(
                                binding.WsModelPath, string.Empty, -1, -1, double.NaN,
                                "WSModel is unreadable"));
                        continue;

                    case SourceWsModelBindingKind.Incomplete:
                        AddUse(
                            binding.Material,
                            new TextureDownscaleUse(
                                binding.WsModelPath,
                                binding.Geometry,
                                -1,
                                -1,
                                double.NaN,
                                "WSModel binding has no geometry, LOD or part index"));
                        continue;
                }

                if (state.ProcessedMeshes.Contains(
                        new MeshKey(binding.Geometry, binding.Lod, binding.Part)) ||
                    state.SharedRepackParts.Contains(
                        new MeshKey(binding.Geometry, binding.Lod, binding.Part)))
                {
                    // Atlased or repacked in this run: the output WSModel points at a
                    // generated material, so this part no longer uses the source material.
                    inputs.ExcludedUsesByMaterial[binding.Material] =
                        inputs.ExcludedUsesByMaterial.GetValueOrDefault(binding.Material) + 1;
                    continue;
                }

                pending.Add((
                    binding.WsModelPath, binding.Geometry, binding.Lod, binding.Part,
                    binding.Material));
            }

            foreach (var material in inputs.ExcludedUsesByMaterial.Keys)
            {
                var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var file = state.Source.FindFile(material);
                    var nodes = file == null
                        ? null
                        : LoadXml(file).SelectNodes("/material/textures/texture");
                    if (nodes != null)
                    {
                        foreach (XmlNode node in nodes)
                        {
                            var texture = Normalize(
                                node.SelectSingleNode("source")?.InnerText ?? node.InnerText);
                            if (IsTextureDownscaleTexturePath(texture))
                                textures.Add(texture);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The reduced-user-set count is informational only.
                }

                inputs.ExcludedMaterialTextures[material] = textures;
            }

            // 3. Geometry: one fresh read per rigid, keeping only per-part ratios.
            var ratioByPart = new Dictionary<(string Rigid, int Lod, int Part), double>();
            var materialsByPart =
                new Dictionary<(string Rigid, int Lod, int Part), HashSet<string>>();
            var unassessableParts = new HashSet<(string Rigid, int Lod, int Part)>();
            var loadedRigids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rigidGroups = pending
                .GroupBy(binding => binding.Rigid, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            for (var groupIndex = 0; groupIndex < rigidGroups.Count; groupIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var group = rigidGroups[groupIndex];
                if (groupIndex % 10 == 0 || groupIndex == rigidGroups.Count - 1)
                {
                    ReportProgress(
                        progress,
                        "Texture downscale analysis",
                        groupIndex + 1,
                        rigidGroups.Count,
                        group.Key);
                }

                void MarkAllUnassessable(string reason)
                {
                    foreach (var binding in group)
                    {
                        unassessableParts.Add((binding.Rigid, binding.Lod, binding.Part));
                        AddUse(
                            binding.Material,
                            new TextureDownscaleUse(
                                binding.Ws,
                                binding.Rigid,
                                binding.Lod,
                                binding.Part,
                                double.NaN,
                                reason));
                    }
                }

                if (budget.HasValue && timer.Elapsed > budget.Value)
                {
                    inputs.BudgetExhausted = true;
                    inputs.RigidsSkippedForTime++;
                    MarkAllUnassessable("analysis time budget exhausted");
                    continue;
                }

                RmvFile? rigid = null;
                try
                {
                    rigid = loadRigid(group.Key);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    rigid = null;
                }

                if (rigid?.ModelList == null)
                {
                    inputs.RigidsUnavailable++;
                    MarkAllUnassessable("rigid not found or unreadable");
                    continue;
                }

                inputs.RigidsRead++;
                loadedRigids.Add(group.Key);
                if (ShouldTreatEmbeddedTexturesAsReferences(state, group.Key))
                    AddEmbeddedRigidReferences(inputs, group.Key, rigid);

                foreach (var binding in group)
                {
                    var key = (binding.Rigid, binding.Lod, binding.Part);
                    if (binding.Lod < 0 || binding.Lod >= rigid.ModelList.Length ||
                        binding.Part < 0 || binding.Part >= rigid.ModelList[binding.Lod].Length)
                    {
                        unassessableParts.Add(key);
                        AddUse(
                            binding.Material,
                            new TextureDownscaleUse(
                                binding.Ws, binding.Rigid, binding.Lod, binding.Part,
                                double.NaN, "part index is outside the rigid"));
                        continue;
                    }

                    if (!ratioByPart.TryGetValue(key, out var ratio) &&
                        !unassessableParts.Contains(key))
                    {
                        if (TryCalculateUvPerWorld(rigid.ModelList[binding.Lod][binding.Part], out ratio))
                            ratioByPart[key] = ratio;
                        else
                            unassessableParts.Add(key);
                    }

                    if (unassessableParts.Contains(key))
                    {
                        AddUse(
                            binding.Material,
                            new TextureDownscaleUse(
                                binding.Ws, binding.Rigid, binding.Lod, binding.Part,
                                double.NaN, "part has no usable UVs or surface area"));
                        continue;
                    }

                    if (!materialsByPart.TryGetValue(key, out var partMaterials))
                    {
                        partMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        materialsByPart[key] = partMaterials;
                    }

                    partMaterials.Add(binding.Material);
                    AddUse(
                        binding.Material,
                        new TextureDownscaleUse(
                            binding.Ws, binding.Rigid, binding.Lod, binding.Part, ratio, string.Empty));
                }
            }

            inputs.PartsAssessed = ratioByPart.Count;
            inputs.PartsUnassessable = unassessableParts.Count;
            ClassifyTextureDownscaleGameplayLinks(state, inputs);

            // Rigids used directly (not through a WSModel) sample their embedded textures.
            var directRigids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (state.GameplayMeshDependencyIndex != null)
            {
                directRigids.UnionWith(state.GameplayMeshDependencyIndex.DirectConsumersByRigid
                    .Where(entry => entry.Value.Count != 0)
                    .Select(entry => entry.Key));
            }

            directRigids.UnionWith(state.DirectRigidMeshMergeConsumersByRigid
                .Where(entry => entry.Value.Count != 0)
                .Select(entry => entry.Key));
            foreach (var rigidPath in directRigids.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (loadedRigids.Contains(rigidPath) || !state.Source.ContainsFile(rigidPath))
                    continue;

                var rigid = loadRigid(rigidPath);
                if (rigid != null)
                    AddEmbeddedRigidReferences(inputs, rigidPath, rigid);
            }

            // 4. Texture headers for everything that could become a candidate.
            TextureDownscaleFile GetFile(string path, PackFile? file)
            {
                if (!inputs.Files.TryGetValue(path, out var cached))
                {
                    cached = ReadTextureDownscaleHeader(path, file);
                    inputs.Files[path] = cached;
                }

                return cached;
            }

            var referenceIndex = BuildDownscaleReferenceIndex(inputs.Materials);
            foreach (var texture in referenceIndex.Keys
                         .Concat(inputs.NonMaterialReferences.Keys)
                         .Concat(inputs.UnreadableReferencedTextures)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!inputs.SourceTextureFiles.Contains(texture) ||
                    !inputs.OutputTextureFiles.Contains(texture) ||
                    inputs.GeneratedTextures.Contains(texture))
                {
                    continue;
                }

                GetFile(texture, state.Output.FindFile(texture));
                if (OverridesVanilla(texture))
                    inputs.VanillaOverrideTextures.Add(texture);
            }

            // 5. Reference density. The atlas stage's own global value wins when this run
            // had exactly one; otherwise it is derived from the assessed parts here.
            inputs.AtlasReferenceCount = state.TexelDensityGlobalReferences.Count;
            if (state.TexelDensityGlobalReferences.Count == 1)
                inputs.AtlasReferenceDensity = state.TexelDensityGlobalReferences[0];

            foreach (var (key, ratio) in ratioByPart)
            {
                // Like the atlas discovery, a part shared by several materials is not a sample.
                if (!materialsByPart.TryGetValue(key, out var partMaterials) ||
                    partMaterials.Count != 1 ||
                    !inputs.Materials.TryGetValue(partMaterials.Single(), out var material) ||
                    !material.Readable)
                {
                    continue;
                }

                var baseColour = material.Textures
                    .FirstOrDefault(entry => entry.Slot.Equals(
                        "t_xml_base_colour",
                        StringComparison.OrdinalIgnoreCase))
                    .Texture;
                if (string.IsNullOrEmpty(baseColour))
                    continue;

                var layout = GetFile(baseColour, FindForReadStatic(state, baseColour)).Layout;
                if (layout == null)
                    continue;

                inputs.PopulationDensities.Add(ratio * Math.Sqrt((double)layout.Width * layout.Height));
            }

            return inputs;
        }

        // ---------------------------------------------------------------------------
        // Lord / hero classification (the "character rule").
        // ---------------------------------------------------------------------------

        private static bool IsTextureDownscaleCharacterCategory(Wh3ArmyUnitCategory category)
            => category is Wh3ArmyUnitCategory.Lord or Wh3ArmyUnitCategory.Hero;

        // One resolved gameplay consumer of a VMD is a character when any of these holds.
        // They are independent on purpose: the first two come from the DB chain, the third
        // from the unit row, and any one is enough (the rule errs towards excluding).
        //  - it was reached through a character link (agent uniform / campaign character
        //    art set / battle agent art set, or the custom battle general uniform);
        //  - its unit category is lord or hero (ui_unit_groupings, then caste);
        //  - it is a single-entity unit (num_men 1) that is not a monster, cavalry or war
        //    machine row. A unit row without a readable num_men also reads as 1, which
        //    only makes the rule broader.
        // The mount of such a unit carries the same unit data, so a lord's mount VMD is a
        // character VMD, while a cavalry mount (many men, cavalry category) is not.
        private static bool IsTextureDownscaleCharacterUsage(
            Wh3UnitCategoryUsage usage,
            out string signal)
        {
            signal = string.Empty;
            if (usage.ConsumerType is
                Wh3VmdConsumerType.CustomBattleGeneralUniform or
                Wh3VmdConsumerType.BattleAgentArtSet)
            {
                signal = usage.ConsumerType == Wh3VmdConsumerType.BattleAgentArtSet
                    ? "battle agent art set"
                    : "custom battle general uniform";
                return true;
            }

            if (IsTextureDownscaleCharacterCategory(usage.Category))
            {
                signal = usage.Category.ToString().ToLowerInvariant();
                return true;
            }

            if (usage.NumMen <= 1 &&
                usage.Category is not (
                    Wh3ArmyUnitCategory.CavalryChariot or
                    Wh3ArmyUnitCategory.MonsterBeast or
                    Wh3ArmyUnitCategory.ArtilleryWarMachine))
            {
                signal = "single entity";
                return true;
            }

            return false;
        }

        // A root is a character when any consumer of it is; it is unclassified when it is
        // not a character but part of its consumer picture is missing (unresolved
        // consumers, or a failed traversal below it); otherwise it is rank-and-file.
        // A root with no consumer at all has no gameplay link and is rank-and-file.
        private static TextureDownscaleRootKind ClassifyTextureDownscaleRoot(
            IReadOnlyList<Wh3UnitCategoryUsage> usages,
            IReadOnlyList<Wh3UnresolvedVmdConsumer> unresolvedConsumers,
            IReadOnlyList<Wh3ResolvedUnitVisual> rosterUnits,
            IReadOnlyList<Wh3UnitDirectAssetUsage> directUsages,
            bool traversalIncomplete,
            out string detail)
        {
            detail = string.Empty;
            foreach (var usage in usages)
            {
                if (IsTextureDownscaleCharacterUsage(usage, out var signal))
                {
                    detail = $"{DescribeTextureDownscaleUnit(usage.MainUnitKey, usage.LandUnitKey)} ({signal})";
                    return TextureDownscaleRootKind.Character;
                }
            }

            foreach (var consumer in unresolvedConsumers)
            {
                if (IsTextureDownscaleCharacterCategory(consumer.Category))
                {
                    detail = $"{DescribeTextureDownscaleUnit(consumer.MainUnitKey, consumer.LandUnitKey)} " +
                             $"({consumer.Category.ToString().ToLowerInvariant()}, unresolved consumer)";
                    return TextureDownscaleRootKind.Character;
                }
            }

            foreach (var unit in rosterUnits)
            {
                if (IsTextureDownscaleCharacterCategory(unit.Category))
                {
                    detail = $"{DescribeTextureDownscaleUnit(unit.MainUnitKey, unit.LandUnitKey)} " +
                             $"({unit.Category.ToString().ToLowerInvariant()})";
                    return TextureDownscaleRootKind.Character;
                }
            }

            foreach (var direct in directUsages)
            {
                if (IsTextureDownscaleCharacterCategory(direct.Category))
                {
                    detail = $"{DescribeTextureDownscaleUnit(direct.MainUnitKey, direct.LandUnitKey)} " +
                             $"({direct.Category.ToString().ToLowerInvariant()})";
                    return TextureDownscaleRootKind.Character;
                }
            }

            if (unresolvedConsumers.Count != 0)
            {
                detail = $"{unresolvedConsumers.Count:N0} unresolved gameplay consumer(s)";
                return TextureDownscaleRootKind.Unclassified;
            }

            if (traversalIncomplete)
            {
                detail = "its asset traversal was incomplete";
                return TextureDownscaleRootKind.Unclassified;
            }

            return TextureDownscaleRootKind.RankAndFile;
        }

        private static string DescribeTextureDownscaleUnit(string mainUnitKey, string landUnitKey)
            => mainUnitKey.Length != 0
                ? mainUnitKey
                : landUnitKey.Length != 0
                    ? landUnitKey
                    : "unknown unit";

        private static string? GetTextureDownscaleCharacterRuleUnavailableReason(BatchState state)
        {
            var resolution = state.UnitCategoryResolution;
            if (resolution == null)
                return "the WH3 unit/DB resolution did not run";
            if (!resolution.IsGameplayResolutionHealthy)
            {
                return "the WH3 unit/DB resolution is unhealthy" +
                       (string.IsNullOrWhiteSpace(resolution.GameplayResolutionHealthMessage)
                           ? string.Empty
                           : $" ({resolution.GameplayResolutionHealthMessage.Trim()})");
            }

            return state.GameplayMeshDependencyIndex == null
                ? "the gameplay mesh dependency index was not built"
                : null;
        }

        // Classifies a gameplay root as rank-and-file, character or unclassified (cached per
        // root). Only valid when GetTextureDownscaleCharacterRuleUnavailableReason is null.
        private static Func<string, (TextureDownscaleRootKind Kind, string Detail)>
            BuildTextureDownscaleRootClassifier(BatchState state)
        {
            var resolution = state.UnitCategoryResolution!;
            var index = state.GameplayMeshDependencyIndex!;
            var rosterByVmd = new Dictionary<string, List<Wh3ResolvedUnitVisual>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var unit in resolution.RosterUnits)
            {
                foreach (var component in unit.Components.Where(item => item.IsVariantMeshDefinition))
                {
                    var key = Normalize(component.AssetPath).TrimStart('\\');
                    if (!rosterByVmd.TryGetValue(key, out var units))
                    {
                        units = [];
                        rosterByVmd[key] = units;
                    }

                    units.Add(unit);
                }
            }

            var directByPath = resolution.DirectAssetUsagesByPath
                .GroupBy(entry => Normalize(entry.Key).TrimStart('\\'), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<Wh3UnitDirectAssetUsage>)group
                        .SelectMany(entry => entry.Value)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);
            var unresolvedByVmd = resolution.UnresolvedConsumersByVmd
                .GroupBy(entry => Normalize(entry.Key).TrimStart('\\'), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<Wh3UnresolvedVmdConsumer>)group
                        .SelectMany(entry => entry.Value)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);

            var rootCache = new Dictionary<string, (TextureDownscaleRootKind Kind, string Detail)>(
                StringComparer.OrdinalIgnoreCase);
            (TextureDownscaleRootKind Kind, string Detail) ClassifyRoot(string root)
            {
                if (rootCache.TryGetValue(root, out var cached))
                    return cached;

                var key = root.TrimStart('\\');
                var kind = ClassifyTextureDownscaleRoot(
                    resolution.GetUsages(root),
                    unresolvedByVmd.GetValueOrDefault(key) ?? Array.Empty<Wh3UnresolvedVmdConsumer>(),
                    rosterByVmd.GetValueOrDefault(key) ?? new List<Wh3ResolvedUnitVisual>(),
                    directByPath.GetValueOrDefault(key) ?? Array.Empty<Wh3UnitDirectAssetUsage>(),
                    index.IncompleteRoots.Contains(root),
                    out var detail);
                cached = (kind, detail);
                rootCache[root] = cached;
                return cached;
            }

            return ClassifyRoot;
        }

        // Maps every source WSModel a mesh part is bound through to the gameplay roots that
        // reach it. Reachability is the gameplay mesh dependency index the atlas stages
        // already built (root VMD -> child VMDs -> WSModels -> rigids), so this adds no
        // traversal of its own. A WSModel no root reaches has no gameplay link.
        private static void ClassifyTextureDownscaleGameplayLinks(
            BatchState state,
            TextureDownscaleInputs inputs)
        {
            inputs.GameplayLinksByWsModel.Clear();
            inputs.CharacterRuleUnavailableReason =
                GetTextureDownscaleCharacterRuleUnavailableReason(state);
            if (inputs.CharacterRuleUnavailableReason != null)
                return;

            var index = state.GameplayMeshDependencyIndex!;
            var classifyRoot = BuildTextureDownscaleRootClassifier(state);

            var wsModels = inputs.UsesByMaterial.Values
                .SelectMany(uses => uses)
                .Where(use => use.IsAssessable)
                .Select(use => Normalize(use.WsModelPath))
                .Where(path => path.Length != 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
            foreach (var wsModel in wsModels)
            {
                var roots = index.GetRootsForAsset(wsModel)
                    .Concat(index.GameplayRoots.Contains(wsModel)
                        ? new[] { wsModel }
                        : Array.Empty<string>())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                TextureDownscaleGameplayLink? link = null;
                foreach (var root in roots)
                {
                    var (kind, detail) = classifyRoot(root);
                    if (kind == TextureDownscaleRootKind.RankAndFile)
                        continue;

                    var candidate = new TextureDownscaleGameplayLink(
                        kind,
                        $"{wsModel} via {root}: {detail}");
                    // A character root wins over an unclassified one.
                    if (link == null ||
                        (kind == TextureDownscaleRootKind.Character &&
                         link.Kind != TextureDownscaleRootKind.Character))
                    {
                        link = candidate;
                    }
                }

                if (link != null)
                    inputs.GameplayLinksByWsModel[wsModel] = link;
            }
        }

        // The first reason a texture's users keep it at full resolution: a lord/hero user
        // wins over an unclassifiable one. With the gameplay resolution unavailable no
        // user can be cleared, so every texture is held back with the reason why.
        private static TextureDownscaleGameplayLink? FindTextureDownscaleGameplayBlock(
            TextureDownscaleInputs inputs,
            IEnumerable<TextureDownscaleUse> uses)
        {
            if (inputs.CharacterRuleUnavailableReason != null)
            {
                return new TextureDownscaleGameplayLink(
                    TextureDownscaleRootKind.Unclassified,
                    $"character rule unavailable: {inputs.CharacterRuleUnavailableReason}");
            }

            TextureDownscaleGameplayLink? unclassified = null;
            foreach (var wsModel in uses
                         .Select(use => Normalize(use.WsModelPath))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (!inputs.GameplayLinksByWsModel.TryGetValue(wsModel, out var link))
                    continue;
                if (link.Kind == TextureDownscaleRootKind.Character)
                    return link;

                unclassified ??= link;
            }

            return unclassified;
        }

        // ---------------------------------------------------------------------------
        // Planning (pure over the collected inputs).
        // ---------------------------------------------------------------------------

        private static void AddDownscaleIneligible(
            TextureDownscalePlan plan,
            string reason,
            string path,
            string detail = "")
        {
            plan.IneligibleReasons[reason] = plan.IneligibleReasons.GetValueOrDefault(reason) + 1;
            if (!plan.IneligibleExamples.TryGetValue(reason, out var examples))
            {
                examples = [];
                plan.IneligibleExamples[reason] = examples;
            }

            if (examples.Count < TextureDownscaleExampleLimit)
                examples.Add(detail.Length == 0 ? path : $"{path} ({detail})");
        }

        private static TextureDownscalePlan PlanTextureDownscale(TextureDownscaleInputs inputs)
        {
            var plan = new TextureDownscalePlan
            {
                AtlasActive = inputs.AtlasActive,
                MaterialsScanned = inputs.MaterialsScanned,
                UnreadableMaterials = inputs.UnreadableMaterials,
                WsModelsScanned = inputs.WsModelsScanned,
                UnreadableWsModels = inputs.UnreadableWsModels,
                RigidsRead = inputs.RigidsRead,
                RigidsUnavailable = inputs.RigidsUnavailable,
                RigidsSkippedForTime = inputs.RigidsSkippedForTime,
                PartsAssessed = inputs.PartsAssessed,
                PartsUnassessable = inputs.PartsUnassessable,
                TextFilesScanned = inputs.TextFilesScanned,
                BudgetExhausted = inputs.BudgetExhausted,
                Budget = inputs.Budget,
                GeneratedUntouched = inputs.GeneratedTextures.Count(
                    texture => !inputs.RepackGeneratedTextures.Contains(texture)),
                RepackGeneratedUntouched = inputs.GeneratedTextures.Count(
                    texture => inputs.RepackGeneratedTextures.Contains(texture)),
                CharacterRuleUnavailableReason = inputs.CharacterRuleUnavailableReason,
                CharacterWsModels = inputs.GameplayLinksByWsModel.Values
                    .Count(link => link.Kind == TextureDownscaleRootKind.Character),
                UnclassifiedWsModels = inputs.GameplayLinksByWsModel.Values
                    .Count(link => link.Kind == TextureDownscaleRootKind.Unclassified),
            };

            if (inputs.AtlasReferenceDensity.HasValue)
            {
                plan.ReferenceDensity = inputs.AtlasReferenceDensity;
                plan.ReferenceOrigin =
                    "taken from the atlas stage of this run (its global value; the atlas " +
                    "stage used a per-root value for roots with at least 4 parts)";
            }
            else if (inputs.PopulationDensities.Count >= 2)
            {
                var sorted = inputs.PopulationDensities.OrderBy(value => value).ToArray();
                plan.ReferenceDensity = Percentile(sorted, TexelDensityReferencePercentile);
                plan.ReferenceSamples = sorted.Length;
                plan.ReferenceOrigin = inputs.AtlasReferenceCount > 1
                    ? $"derived here ({inputs.AtlasReferenceCount} atlas reference values existed " +
                      "in this run, one per atlas scope, so none was reused)"
                    : "derived here";
            }

            if (inputs.AtlasReferenceDensity.HasValue)
                plan.ReferenceSamples = inputs.PopulationDensities.Count;

            var referenceIndex = BuildDownscaleReferenceIndex(inputs.Materials);
            var reducedTextures = new HashSet<string>(
                inputs.ExcludedMaterialTextures.Values.SelectMany(textures => textures),
                StringComparer.OrdinalIgnoreCase);
            var universe = new HashSet<string>(inputs.SourceTextureFiles, StringComparer.OrdinalIgnoreCase);
            universe.UnionWith(referenceIndex.Keys);
            universe.ExceptWith(inputs.GeneratedTextures);
            plan.TexturesConsidered = universe.Count;

            foreach (var texture in universe.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var references = referenceIndex.GetValueOrDefault(texture) ?? [];
                var nonMaterial = inputs.NonMaterialReferences.TryGetValue(texture, out var mentionedBy);
                var unreadable = inputs.UnreadableReferencedTextures.Contains(texture);
                var referenced = references.Count != 0 || nonMaterial || unreadable;
                var inSource = inputs.SourceTextureFiles.Contains(texture);
                var inOutput = inputs.OutputTextureFiles.Contains(texture);

                if (!inSource)
                {
                    if (referenced)
                        AddDownscaleIneligible(plan, TextureDownscaleReasonNotOwned, texture);
                    continue;
                }

                if (!inOutput)
                {
                    plan.AtlasRemovedAllUsers++;
                    continue;
                }

                if (!referenced)
                {
                    if (reducedTextures.Contains(texture))
                        plan.AtlasRemovedAllUsers++;
                    else
                        plan.Unreferenced++;
                    continue;
                }

                plan.TexturesAssessed++;
                if (reducedTextures.Contains(texture))
                    plan.AssessedWithReducedUserSet++;

                if (inputs.VanillaOverrideTextures.Contains(texture))
                {
                    AddDownscaleIneligible(plan, TextureDownscaleReasonOverridesVanilla, texture);
                    continue;
                }

                var badSlot = references
                    .FirstOrDefault(reference => !IsTextureDownscaleSlot(reference.Slot));
                if (badSlot.Slot != null)
                {
                    AddDownscaleIneligible(
                        plan, TextureDownscaleReasonSlot, texture, $"{badSlot.Slot} in {badSlot.Material}");
                    continue;
                }

                if (nonMaterial)
                {
                    AddDownscaleIneligible(
                        plan, TextureDownscaleReasonNonMaterial, texture, mentionedBy ?? string.Empty);
                    continue;
                }

                if (unreadable)
                {
                    AddDownscaleIneligible(plan, TextureDownscaleReasonUnreadableReference, texture);
                    continue;
                }

                var materialPaths = references
                    .Select(reference => reference.Material)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                string? scopeReason = null;
                string scopeDetail = string.Empty;
                var uses = new List<TextureDownscaleUse>();
                foreach (var materialPath in materialPaths)
                {
                    var material = inputs.Materials[materialPath];
                    var materialUses = inputs.UsesByMaterial.GetValueOrDefault(materialPath) ?? [];
                    string? reason = null;
                    var detail = materialPath;
                    if (material.GeneratedByRepack)
                    {
                        reason = TextureDownscaleReasonRepackMaterial;
                    }
                    else if (material.Generated)
                    {
                        reason = TextureDownscaleReasonGeneratedMaterial;
                    }
                    else if (material.OverridesVanilla)
                    {
                        reason = TextureDownscaleReasonMaterialOverridesVanilla;
                    }
                    else if (materialUses.Count == 0)
                    {
                        reason = TextureDownscaleReasonNoUser;
                    }
                    else if (materialUses.Any(use => !use.IsAssessable))
                    {
                        var failed = materialUses.First(use => !use.IsAssessable);
                        reason = TextureDownscaleReasonUnassessableUser;
                        detail = $"{materialPath}: {failed.UnassessableReason}";
                    }

                    if (reason != null)
                    {
                        // Keep the most specific reason in a fixed priority.
                        if (scopeReason == null ||
                            GetDownscaleScopeReasonRank(reason) < GetDownscaleScopeReasonRank(scopeReason))
                        {
                            scopeReason = reason;
                            scopeDetail = detail;
                        }

                        continue;
                    }

                    uses.AddRange(materialUses);
                }

                if (scopeReason != null)
                {
                    AddDownscaleIneligible(plan, scopeReason, texture, scopeDetail);
                    continue;
                }

                var layout = inputs.Files.GetValueOrDefault(texture)?.Layout;
                if (layout == null)
                {
                    AddDownscaleIneligible(
                        plan,
                        TextureDownscaleReasonBadDds,
                        texture,
                        inputs.Files.GetValueOrDefault(texture)?.Problem ?? string.Empty);
                    continue;
                }

                if (!plan.ReferenceDensity.HasValue)
                {
                    AddDownscaleIneligible(plan, TextureDownscaleReasonNoReference, texture);
                    continue;
                }

                var reference = plan.ReferenceDensity.Value;
                var size = Math.Sqrt((double)layout.Width * layout.Height);
                var densities = uses.Select(use => use.UvPerWorld * size).ToList();
                var decision = ChooseTextureDownscale(
                    densities, reference, layout.Width, layout.Height);
                if (decision.Outcome == TextureDownscaleOutcome.NeedsFullResolution)
                {
                    plan.NeedsFullResolution++;
                    continue;
                }

                if (decision.Outcome == TextureDownscaleOutcome.BelowMinimumDimension)
                {
                    AddDownscaleIneligible(
                        plan,
                        TextureDownscaleReasonMinimumDimension,
                        texture,
                        $"{layout.Width}x{layout.Height}");
                    continue;
                }

                var skip = (int)Math.Round(Math.Log2(1.0 / decision.Scale));
                var canSlice =
                    layout.MipCount > skip &&
                    layout.MipCount == GetFullMipChainLength(layout.Width, layout.Height) &&
                    decision.NewWidth == Math.Max(1, layout.Width >> skip) &&
                    decision.NewHeight == Math.Max(1, layout.Height >> skip);
                // A legacy header cannot say which colour space the data is in, so only
                // textures used purely as normal/mask (linear) may be re-encoded then.
                var slots = references.Select(item => item.Slot).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var colourSpaceKnown = layout.IsDx10 ||
                    slots.All(slot =>
                        slot.Equals("t_xml_normal", StringComparison.OrdinalIgnoreCase) ||
                        slot.Equals("t_xml_mask", StringComparison.OrdinalIgnoreCase));
                var canReencode = layout.Format is "BC1" or "BC3" && colourSpaceKnown;
                if (!canSlice && !canReencode)
                {
                    AddDownscaleIneligible(
                        plan,
                        TextureDownscaleReasonFormat,
                        texture,
                        $"{layout.FormatDescription}");
                    continue;
                }

                // Last, so that only a texture the size rules would have downscaled is
                // reported as kept by the character rule (the count then is what it cost).
                var gameplayBlock = FindTextureDownscaleGameplayBlock(inputs, uses);
                if (gameplayBlock != null)
                {
                    AddDownscaleIneligible(
                        plan,
                        gameplayBlock.Kind == TextureDownscaleRootKind.Character
                            ? TextureDownscaleReasonCharacter
                            : TextureDownscaleReasonCharacterUnclassified,
                        texture,
                        gameplayBlock.Detail);
                    continue;
                }

                var limiting = uses[decision.LimitingIndex];
                var newMips = GetFullMipChainLength(decision.NewWidth, decision.NewHeight);
                plan.Candidates.Add(new TextureDownscaleCandidate(
                    texture,
                    layout.Width,
                    layout.Height,
                    decision.NewWidth,
                    decision.NewHeight,
                    decision.Scale,
                    layout.Format,
                    layout.FormatDescription,
                    layout.IsSrgb,
                    layout.IsDx10,
                    canSlice,
                    skip,
                    layout.ChainBytes,
                    EstimateDdsChainBytes(layout, decision.NewWidth, decision.NewHeight, newMips),
                    materialPaths.Count,
                    uses.Select(use => (use.RigidPath, use.LodIndex, use.PartIndex)).Distinct().Count(),
                    limiting.Description,
                    densities[decision.LimitingIndex] / reference));
            }

            return plan;
        }

        private static int GetDownscaleScopeReasonRank(string reason)
            => reason switch
            {
                TextureDownscaleReasonGeneratedMaterial => 0,
                TextureDownscaleReasonRepackMaterial => 0,
                TextureDownscaleReasonMaterialOverridesVanilla => 1,
                TextureDownscaleReasonNoUser => 2,
                _ => 3,
            };

        // ---------------------------------------------------------------------------
        // Stage entry, rewrite and validation.
        // ---------------------------------------------------------------------------

        private void RunTextureDownscale(
            BatchState state,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress,
            Func<string, RmvFile?>? loadRigid = null)
        {
            var result = new TextureDownscaleResult { Requested = state.DownscaleTextures };
            state.TextureDownscale = result;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                ReportProgress(progress, "Texture downscale analysis");
                // Report-only analysis is bounded like the other audits; an explicit
                // rewrite is not, because a time-dependent result would be worse.
                var inputs = CollectTextureDownscaleInputs(
                    state,
                    loadRigid ?? (path => LoadTextureDownscaleRigid(state, path)),
                    state.DownscaleTextures
                        ? null
                        : TimeSpan.FromSeconds(TextureDownscaleBudgetSeconds),
                    cancellationToken,
                    progress);
                result.Plan = PlanTextureDownscale(inputs);
                CompareEarlyDownscaleEstimate(state, result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Diagnostic stage: a failure never fails the run or changes the output.
                result.Failure = $"{ex.GetType().Name}: {ex.Message}";
            }

            result.AnalysisElapsed = stopwatch.Elapsed;
            state.PhaseDurations["Texture downscale analysis"] = result.AnalysisElapsed;

            if (!state.DownscaleTextures || result.Plan == null)
                return;

            stopwatch.Restart();
            ApplyTextureDownscale(state, result, cancellationToken, progress);
            result.RewriteElapsed = stopwatch.Elapsed;
            state.PhaseDurations["Texture downscale rewrite"] = result.RewriteElapsed;
        }

        // Analysis on the source state, before atlasing (see BatchState.TextureDownscaleEarly).
        // Same inputs and rules as the late run; the only differences are that nothing has been
        // atlased, merged or pruned yet and that no atlas stage density reference exists, so the
        // reference is derived from the assessed parts. Failures never fail the run: the stages
        // that would use the estimate then size textures at full resolution.
        private void RunEarlyTextureDownscale(
            BatchState state,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress,
            Func<string, RmvFile?>? loadRigid = null)
        {
            var result = new TextureDownscaleResult { Requested = true };
            state.TextureDownscaleEarly = result;
            state.DownscaleEarlyCandidates.Clear();
            state.DownscaleTargetScale.Clear();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                ReportProgress(progress, "Texture downscale early estimate");
                var inputs = CollectTextureDownscaleInputs(
                    state,
                    loadRigid ?? (path => LoadTextureDownscaleRigid(state, path)),
                    null,
                    cancellationToken,
                    progress);
                result.Plan = PlanTextureDownscale(inputs);
                foreach (var candidate in result.Plan.Candidates)
                {
                    var path = Normalize(candidate.Path);
                    state.DownscaleEarlyCandidates[path] = candidate;
                    state.DownscaleTargetScale[path] = candidate.Scale;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Failure = $"{ex.GetType().Name}: {ex.Message}";
                state.DownscaleEarlyCandidates.Clear();
                state.DownscaleTargetScale.Clear();
            }

            result.AnalysisElapsed = stopwatch.Elapsed;
            state.PhaseDurations["Texture downscale early estimate"] = result.AnalysisElapsed;
        }

        // The late decision against the early estimate, for the textures an earlier stage sized
        // with it. A texture the repack replaced is not compared: the late run cannot judge it.
        private static void CompareEarlyDownscaleEstimate(BatchState state, TextureDownscaleResult late)
        {
            if (state.TextureDownscaleEarly?.Plan == null || late.Plan == null)
                return;

            var lateScale = late.Plan.Candidates.ToDictionary(
                candidate => Normalize(candidate.Path),
                candidate => candidate.Scale,
                StringComparer.OrdinalIgnoreCase);
            foreach (var path in state.DownscaleEarlyUsedByGate
                         .Except(state.DownscaleEarlyUsedByRepack, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                late.EarlyComparedTextures++;
                var earlyValue = state.DownscaleTargetScale.GetValueOrDefault(path, 1.0);
                var lateValue = lateScale.GetValueOrDefault(path, 1.0);
                if (Math.Abs(earlyValue - lateValue) > 1e-9)
                {
                    late.EarlyMismatches.Add(
                        $"{path}: early estimate {FormatDownscaleScale(earlyValue)}, late decision " +
                        $"{FormatDownscaleScale(lateValue)}");
                }
            }
        }

        private static string FormatDownscaleScale(double scale)
            => scale >= 1.0 ? "full size" : scale.ToString("0.##", CultureInfo.InvariantCulture) + "x";

        private void ApplyTextureDownscale(
            BatchState state,
            TextureDownscaleResult result,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var candidates = result.Plan!.Candidates;
            for (var index = 0; index < candidates.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = candidates[index];
                ReportProgress(
                    progress,
                    "Downscaling textures",
                    index + 1,
                    candidates.Count,
                    candidate.Path);
                try
                {
                    var file = state.Output.FindFile(candidate.Path);
                    if (file == null)
                    {
                        result.RewriteFailures.Add($"{candidate.Path}: not found in the output");
                        continue;
                    }

                    var bytes = file.DataSource.ReadData();
                    if (!TryReadDdsLayout(
                            bytes.AsSpan(0, Math.Min(bytes.Length, TextureDownscaleHeaderPeekBytes)).ToArray(),
                            out var layout))
                    {
                        result.RewriteFailures.Add($"{candidate.Path}: header is no longer readable");
                        continue;
                    }

                    byte[] rewritten;
                    string error;
                    using var captureScope = DdsEncodeCapture.Scope("downscale", candidate.Path);
                    var ok = candidate.UseSlicing
                        ? TrySliceDdsMips(bytes, layout, candidate.SkipLevels, out rewritten, out error)
                        : TryReencodeDds(
                            bytes, layout, candidate.NewWidth, candidate.NewHeight,
                            candidate.IsSrgb, out rewritten, out error);
                    if (ok)
                    {
                        var problem = CheckDownscaledDds(rewritten, candidate);
                        if (problem != null)
                        {
                            ok = false;
                            error = $"result {problem}";
                        }
                    }

                    if (!ok)
                    {
                        result.RewriteFailures.Add($"{candidate.Path}: {error}");
                        continue;
                    }

                    WriteFile(state.Output, candidate.Path, rewritten);
                    result.Rewritten.Add(candidate);
                    if (candidate.UseSlicing)
                        result.Sliced++;
                    else
                        result.Reencoded++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.RewriteFailures.Add($"{candidate.Path}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            result.Applied = true;
        }

        // Re-reads every rewritten texture from the output and checks it against its plan.
        private static void ValidateDownscaledTextures(
            BatchState state,
            List<string> errors,
            CancellationToken cancellationToken)
        {
            var result = state.TextureDownscale;
            if (result is not { Applied: true })
                return;

            var errorsBefore = errors.Count;
            foreach (var candidate in result.Rewritten)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = state.Output.FindFile(candidate.Path);
                if (file == null)
                {
                    errors.Add($"Downscaled texture is missing from output: {candidate.Path}");
                    continue;
                }

                var problem = CheckDownscaledDds(file.DataSource.ReadData(), candidate);
                if (problem != null)
                    errors.Add($"Downscaled texture {candidate.Path} {problem}");
            }

            if (errors.Count == errorsBefore)
            {
                state.ValidationMessages.Add(
                    $"PASS: {result.Rewritten.Count} downscaled texture(s) decode with the planned " +
                    "size, format and a complete mip chain.");
            }
        }

        // ---------------------------------------------------------------------------
        // Report.
        // ---------------------------------------------------------------------------

        private static void AppendTextureDownscaleSection(StringBuilder sb, BatchState state)
        {
            sb.AppendLine("Texture downscale candidates");
            sb.AppendLine("----------------------------");
            var result = state.TextureDownscale;
            if (result == null)
            {
                sb.AppendLine("Texture downscale analysis did not run.");
                sb.AppendLine();
                return;
            }

            if (result.Plan == null)
            {
                sb.AppendLine(
                    $"Texture downscale analysis failed and was skipped: {result.Failure}");
                sb.AppendLine(
                    result.Requested
                        ? "No texture was changed although downscaling was requested."
                        : "No texture was changed.");
                sb.AppendLine();
                return;
            }

            var plan = result.Plan;
            var candidates = plan.Candidates;
            if (!result.Requested)
            {
                sb.AppendLine(
                    "Mode: LISTED ONLY. The 'Downscale oversized textures' option is off; no " +
                    "texture file was changed.");
            }
            else if (result.Applied)
            {
                sb.AppendLine(
                    $"Mode: APPLIED. {result.Rewritten.Count:N0} of {candidates.Count:N0} " +
                    $"candidate(s) were rewritten in the output pack (mip slicing " +
                    $"{result.Sliced:N0}, re-encode {result.Reencoded:N0}, not rewritten " +
                    $"{result.RewriteFailures.Count:N0}).");
            }
            else
            {
                sb.AppendLine("Mode: requested but not applied.");
            }

            sb.AppendLine(
                "Scope: pack-wide scan of the output pack's materials, with mesh parts taken " +
                "from the source pack's WSModels and rigids (every LOD; a part atlased in this " +
                "run no longer counts as a user of its source material).");
            sb.AppendLine(
                $"Scanned: {plan.MaterialsScanned:N0} material(s) (unreadable " +
                $"{plan.UnreadableMaterials:N0}), {plan.WsModelsScanned:N0} source WSModel(s) " +
                $"(unreadable {plan.UnreadableWsModels:N0}), {plan.RigidsRead:N0} rigid(s) read " +
                $"(unavailable {plan.RigidsUnavailable:N0}), {plan.TextFilesScanned:N0} text file(s) " +
                $"for direct texture references; parts assessed {plan.PartsAssessed:N0}, " +
                $"not assessable {plan.PartsUnassessable:N0}");
            sb.AppendLine(
                $"Analysis time: {result.AnalysisElapsed.TotalMilliseconds:N0} ms" +
                (plan.Budget > TimeSpan.Zero
                    ? $" of a {plan.Budget.TotalSeconds:N0} s budget (report-only runs are bounded)"
                    : " (no budget: the option is on)"));
            if (plan.BudgetExhausted)
            {
                sb.AppendLine(
                    $"The time budget ran out: {plan.RigidsSkippedForTime:N0} rigid(s) were not " +
                    "read and every texture that depends on them is listed as not assessable. " +
                    "The candidate list below is incomplete.");
            }

            var early = state.TextureDownscaleEarly;
            if (early != null)
            {
                sb.AppendLine(
                    early.Plan == null
                        ? $"Early estimate (before atlasing, used by the atlas value gate and the shared texture repack): failed ({early.Failure}); those stages sized textures at full resolution."
                        : $"Early estimate (before atlasing, used by the atlas value gate and the shared texture repack): {early.Plan.Candidates.Count:N0} candidate(s) in {early.AnalysisElapsed.TotalMilliseconds:N0} ms; " +
                          $"{state.DownscaleEarlyUsedByGate.Count:N0} texture(s) sized by the gate and {state.DownscaleEarlyUsedByRepack.Count:N0} by the repack. " +
                          $"This late run on the output is authoritative; of the {result.EarlyComparedTextures:N0} gate-sized texture(s) compared, " +
                          $"{result.EarlyMismatches.Count:N0} differ.");
                foreach (var mismatch in result.EarlyMismatches.Take(TextureDownscaleListLimit))
                    sb.AppendLine("  early/late mismatch: " + mismatch);
            }

            if (plan.ReferenceDensity.HasValue)
            {
                sb.AppendLine(
                    $"Pack reference density: {plan.ReferenceDensity.Value:N2} texels per world " +
                    $"unit ({plan.ReferenceOrigin}); the {TexelDensityReferencePercentile:P0} " +
                    $"percentile of part texel densities (sqrt(UV area x texture pixels / world " +
                    $"surface area) at the part's base-colour size) over {plan.ReferenceSamples:N0} " +
                    "sample(s) of parts with one material");
            }
            else
            {
                sb.AppendLine(
                    "Pack reference density: none (fewer than 2 assessed parts), so no texture " +
                    "can be judged oversized.");
            }

            sb.AppendLine(
                $"Rule: a texture user is oversized when its density exceeds " +
                $"{TexelDensityOutlierMultiplier:0.0#} x the reference; scales are the atlas " +
                $"buckets 0,5x and 0,25x, a texture takes the largest scale any user needs and " +
                $"is a candidate only if that is at most 0,5x and both sides stay at least " +
                $"{MinAtlasResolutionScaleDimension} px.");
            sb.AppendLine(
                "Character rule: a texture used by a lord or hero (agent link, lord/hero unit " +
                "category or single-entity unit, including its mounts and child VMDs) is never " +
                "downscaled; so is one used by a root that could not be classified.");
            if (plan.CharacterRuleUnavailableReason != null)
            {
                sb.AppendLine(
                    $"Character rule status: NOT APPLIED PER ROOT because " +
                    $"{plan.CharacterRuleUnavailableReason}. Every texture the size rules " +
                    "would have downscaled is kept and listed as unclassified instead.");
            }
            else
            {
                sb.AppendLine(
                    $"Character rule status: applied; {plan.CharacterWsModels:N0} source " +
                    $"WSModel(s) are reachable from a lord/hero root, " +
                    $"{plan.UnclassifiedWsModels:N0} only from roots that could not be classified.");
            }

            var notOwned = plan.IneligibleReasons.GetValueOrDefault(TextureDownscaleReasonNotOwned);
            var ineligibleTotal = plan.IneligibleReasons.Values.Sum();
            var assessedIneligible = ineligibleTotal - notOwned;
            var byScale = candidates
                .GroupBy(candidate => candidate.Scale)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key:0.##}x={group.Count():N0}")
                .ToList();
            sb.AppendLine(
                $"Textures considered: {plan.TexturesConsidered:N0} (every distinct .dds path " +
                "that the scanned materials reference or the source pack contains; generated " +
                "atlas textures excluded). They split exactly into the four lines below:");
            sb.AppendLine(
                $"  Not owned by the source pack (vanilla or unresolved): {notOwned:N0}");
            sb.AppendLine(
                $"  Removed by earlier stages (file pruned, or every user atlased away): " +
                $"{plan.AtlasRemovedAllUsers:N0}");
            sb.AppendLine(
                $"  In the source pack but referenced by nothing found (no material, no text " +
                $"file, no unreadable file mentions them): {plan.Unreferenced:N0}");
            sb.AppendLine(
                $"  Assessed: {plan.TexturesAssessed:N0}" +
                (plan.AtlasActive
                    ? $" (against a reduced user set after atlasing: " +
                      $"{plan.AssessedWithReducedUserSet:N0})"
                    : string.Empty) +
                ", which split exactly into:");
            sb.AppendLine(
                $"    Eligible candidates: {candidates.Count:N0}" +
                (byScale.Count == 0 ? string.Empty : $" ({string.Join(", ", byScale)})"));
            sb.AppendLine(
                $"    Not oversized (a user needs more than half the resolution): " +
                $"{plan.NeedsFullResolution:N0}");
            sb.AppendLine($"    Not eligible: {assessedIneligible:N0}");
            sb.AppendLine(
                $"Generated atlas textures (never touched, not counted above): " +
                $"{plan.GeneratedUntouched:N0}");
            sb.AppendLine(
                $"Shared-repack textures (written by the repack, never touched, not counted above): " +
                $"{plan.RepackGeneratedUntouched:N0}");
            sb.AppendLine("Reasons a texture is not eligible (not-owned textures included):");
            foreach (var reason in plan.IneligibleReasons
                         .OrderByDescending(entry => entry.Value)
                         .ThenBy(entry => entry.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"  {reason.Value:N0} x {reason.Key}");
                foreach (var example in plan.IneligibleExamples[reason.Key])
                    sb.AppendLine($"      e.g. {example}");
            }

            var pixelsBefore = candidates.Sum(candidate => candidate.PixelsBefore);
            var pixelsAfter = candidates.Sum(candidate => candidate.PixelsAfter);
            var bytesBefore = candidates.Sum(candidate => candidate.BytesBefore);
            var bytesAfter = candidates.Sum(candidate => candidate.BytesAfter);
            sb.AppendLine(
                $"Eligible set, level-0 pixels: {pixelsBefore:N0} -> {pixelsAfter:N0}");
            sb.AppendLine(
                $"Eligible set, BCn bytes with the whole mip chain: {bytesBefore:N0} -> " +
                $"{bytesAfter:N0} (saves {bytesBefore - bytesAfter:N0})");
            var slicing = candidates.Count(candidate => candidate.UseSlicing);
            sb.AppendLine(
                $"Method {(result.Applied ? "used" : "that would be used")}: mip slicing " +
                $"{slicing:N0} (the author's own lower mips, no recompression), re-encode " +
                $"{candidates.Count - slicing:N0} (decode, area filter, recompress)");
            if (result.RewriteFailures.Count != 0)
            {
                sb.AppendLine($"Not rewritten ({result.RewriteFailures.Count:N0}):");
                foreach (var failure in result.RewriteFailures.Take(10))
                    sb.AppendLine($"  {failure}");
            }

            if (candidates.Count != 0)
            {
                var ordered = candidates
                    .OrderByDescending(candidate => candidate.BytesSaved)
                    .ThenBy(candidate => candidate.Path, StringComparer.Ordinal)
                    .ToList();
                sb.AppendLine(
                    ordered.Count > TextureDownscaleListLimit
                        ? $"Top {TextureDownscaleListLimit} by bytes saved:"
                        : "All candidates by bytes saved:");
                foreach (var candidate in ordered.Take(TextureDownscaleListLimit))
                {
                    sb.AppendLine(
                        $"  {candidate.Path}: {candidate.Width}x{candidate.Height} -> " +
                        $"{candidate.NewWidth}x{candidate.NewHeight}, {candidate.FormatDescription}, " +
                        $"{candidate.BytesBefore:N0} -> {candidate.BytesAfter:N0} bytes, " +
                        $"{candidate.MaterialCount:N0} material(s), {candidate.PartCount:N0} part(s), " +
                        $"{(candidate.UseSlicing ? "mip slicing" : "re-encode")}");
                    sb.AppendLine(
                        $"      limiting user: {candidate.LimitingUser} at " +
                        $"{candidate.LimitingRelativeDensity:0.00}x the reference density");
                }

                if (ordered.Count > TextureDownscaleListLimit)
                    sb.AppendLine($"  ... and {ordered.Count - TextureDownscaleListLimit:N0} more");
            }

            sb.AppendLine();
        }
    }
}
