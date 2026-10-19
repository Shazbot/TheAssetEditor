using System.Buffers.Binary;
using System.IO;
using System.Xml;
using DirectXTexNet;
using Editors.ImportExport.Importing.Importers.PngToDds.Helpers;
using Editors.ImportExport.TextureAtlas;
using Pfim;
using PfimImageFormat = Pfim.ImageFormat;
using Shared.Core.Settings;
using Shared.GameFormats.RigidModel.Types;

namespace Editors.PackOptimizer.Core
{
    public sealed partial class PackTextureAtlasBatchService
    {
        // The WH3 BaseColour and MaterialMap atlases are BC1_UNORM_SRGB, which has no alpha
        // channel beyond a 1-bit cut: a source texel with alpha < 128 would be encoded as
        // transparent black. Per source texture in such a channel:
        //  * BC1 source, or every texel opaque: nothing changes (BC1 atlas);
        //  * non-opaque alpha that no shader using it reads: the source is added to the
        //    force-opaque set, so its alpha is 255 before the BC1 encode;
        //  * non-opaque alpha that a shader reads, or an unknown shader/slot/format: the material
        //    gets a keep-alpha flag for the slot. Candidates are batched by their keep-alpha
        //    slots, and a batch with any such member encodes that channel as BC3_UNORM_SRGB
        //    with every source's alpha kept as is.
        internal enum TextureAlphaState
        {
            Opaque,
            HasAlpha,
            Unknown,
        }

        private static bool IsBc1AtlasTarget(TextureType type)
            => DDSFormatHelper.GetDDSFormat(GameTypeEnum.Warhammer3, type) == DXGI_FORMAT.BC1_UNORM_SRGB;

        // Sources the atlas builder can decode are BC1/2/3/7 and 32-bit RGBA (it decodes through
        // Pfim into BGRA); BC4/BC5 and the formats without an alpha channel never carry alpha.
        internal static TextureAlphaState ClassifyTextureAlpha(byte[] dds)
        {
            try
            {
                var layout = ReadCompactionLayout(dds);
                if (layout != null)
                {
                    switch (layout.Format)
                    {
                        // BC1 is already 1-bit alpha at most, so re-encoding to BC1 changes nothing.
                        case "BC1":
                        case "BC4":
                        case "BC5":
                            return TextureAlphaState.Opaque;
                        case "BC2":
                        case "BC3":
                            return AreAllTexelsOpaque(dds, layout)
                                ? TextureAlphaState.Opaque
                                : TextureAlphaState.HasAlpha;
                    }
                }

                if (HasNoAlphaChannel(dds))
                    return TextureAlphaState.Opaque;

                // BC7 and uncompressed formats: the decoder the atlas builder uses.
                using var stream = new MemoryStream(dds);
                using var image = Pfimage.FromStream(stream);
                switch (image.Format)
                {
                    case PfimImageFormat.Rgba32:
                        return IsMipZeroAlphaOpaque(image)
                            ? TextureAlphaState.Opaque
                            : TextureAlphaState.HasAlpha;
                    case PfimImageFormat.Rgb8:
                    case PfimImageFormat.Rgb24:
                    case PfimImageFormat.R5g5b5:
                    case PfimImageFormat.R5g6b5:
                        return TextureAlphaState.Opaque;
                    default:
                        return TextureAlphaState.Unknown;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return TextureAlphaState.Unknown;
            }
        }

        // Uncompressed DDS whose pixel format declares no alpha bits (for example B8G8R8X8, whose
        // unused byte is not alpha), by legacy header flags or by DXGI format.
        private static bool HasNoAlphaChannel(byte[] dds)
        {
            if (dds.Length < 148 ||
                BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(0, 4)) != 0x20534444)
            {
                return false;
            }

            const uint alphaPixels = 0x1;
            const uint rgb = 0x40;
            const uint dx10 = 0x30315844;
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(80, 4));
            if (BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(84, 4)) == dx10)
            {
                var dxgi = BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(128, 4));
                return dxgi is 88; // B8G8R8X8_UNORM
            }

            return (flags & rgb) != 0 && (flags & alphaPixels) == 0;
        }

        private static bool IsMipZeroAlphaOpaque(IImage image)
        {
            for (var y = 0; y < image.Height; y++)
            {
                var row = y * image.Stride;
                for (var x = 0; x < image.Width; x++)
                {
                    if (image.Data[row + x * 4 + 3] != byte.MaxValue)
                        return false;
                }
            }

            return true;
        }

        // One decode per texture path per run.
        private static TextureAlphaState GetTextureAlphaState(
            BatchState state,
            string texturePath,
            Func<byte[]?> readBytes)
        {
            texturePath = Normalize(texturePath);
            if (state.TextureAlphaStates.TryGetValue(texturePath, out var cached))
                return cached;

            var bytes = readBytes();
            var alpha = bytes == null ? TextureAlphaState.Unknown : ClassifyTextureAlpha(bytes);
            state.TextureAlphaStates[texturePath] = alpha;
            return alpha;
        }

        // Null when a BC1 atlas is enough (the alpha is opaque, or no shader reads it and it is
        // forced opaque); else why the slot needs a BC3 atlas that keeps the alpha.
        internal static string? CheckAtlasAlphaSafety(
            TextureAlphaState alpha,
            string? shaderPath,
            string slot)
        {
            if (alpha == TextureAlphaState.Opaque)
                return null;

            if (alpha == TextureAlphaState.Unknown)
            {
                return $"{slot} has a DDS format whose alpha cannot be verified, and a BC1 " +
                       "atlas would turn any alpha below 128 into transparent black.";
            }

            var shader = GetCompactionShaderName(shaderPath);
            var (table, error) = Wh3ShaderChannels.Value;
            switch (GetAlphaReadDoubt(table, shader, slot))
            {
                case AlphaReadDoubt.TableUnavailable:
                    return $"{slot} has non-opaque alpha that the BC1 atlas cannot keep, and the shader " +
                           $"channel table is not available to prove that nothing reads it ({error}).";
                case AlphaReadDoubt.UnknownShader:
                    return $"{slot} has non-opaque alpha that the BC1 atlas cannot keep, and shader " +
                           $"{shader ?? "(none)"} is not in the channel table.";
                case AlphaReadDoubt.UnboundSlot:
                    return $"{slot} has non-opaque alpha that the BC1 atlas cannot keep, and the slot " +
                           $"is not bound by shader {shader}.";
                case AlphaReadDoubt.ReadsAlpha:
                    return $"Shader {shader} reads the alpha of {slot}, which the BC1 atlas cannot keep.";
            }

            return null;
        }

        // The BC1-target slots of a material whose alpha the atlas has to keep (so BC3).
        private static HashSet<string> GetKeepAlphaChannels(
            BatchState state,
            XmlDocument materialDoc,
            HashSet<string> resolvedChannels,
            Dictionary<string, TextureAtlasConstantColor> constantChannels)
            => GetKeepAlphaReasons(state, materialDoc, resolvedChannels, constantChannels).Keys
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The same slots with the reason each one needs its alpha kept.
        private static Dictionary<string, string> GetKeepAlphaReasons(
            BatchState state,
            XmlDocument materialDoc,
            HashSet<string> resolvedChannels,
            Dictionary<string, TextureAtlasConstantColor> constantChannels)
        {
            var keepAlpha = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var shader = materialDoc.SelectSingleNode("/material/shader")?.InnerText;
            foreach (var channel in AtlasChannels)
            {
                if (!IsBc1AtlasTarget(channel.Type))
                    continue;

                TextureAlphaState alpha;
                if (resolvedChannels.Contains(channel.Slot))
                {
                    var path = GetTexturePath(materialDoc, channel.Slot);
                    alpha = GetTextureAlphaState(
                        state,
                        path,
                        () => FindForReadStatic(state, path)?.DataSource.ReadData());
                }
                else if (constantChannels.TryGetValue(channel.Slot, out var constant))
                {
                    alpha = constant.A == byte.MaxValue
                        ? TextureAlphaState.Opaque
                        : TextureAlphaState.HasAlpha;
                }
                else
                {
                    continue;
                }

                var reason = CheckAtlasAlphaSafety(alpha, shader, channel.Slot);
                if (reason != null)
                    keepAlpha.Add(channel.Slot, reason);
            }

            return keepAlpha;
        }

        // Materials that need different atlas formats never share a batch or a page: each group
        // goes through batch planning and execution on its own.
        private static List<List<AtlasCandidate>> SplitCandidatesByKeepAlpha(
            List<AtlasCandidate> candidates)
        {
            if (candidates.All(candidate => candidate.KeepAlphaChannels.Count == 0))
                return [candidates];

            return candidates
                .GroupBy(candidate => string.Join(
                    ",",
                    candidate.KeepAlphaChannels.OrderBy(slot => slot, StringComparer.OrdinalIgnoreCase)),
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.ToList())
                .ToList();
        }

        // The value gate overwrites these two counters on every run. With several format groups in
        // one Process* call they are totalled across the groups instead (batches are disjoint;
        // units can be shared between groups, so the unit count is the largest group's, a lower
        // bound). A single group is left exactly as the gate wrote it.
        private sealed class FormatGroupGateTotals(BatchState state, int groupCount)
        {
            private int _orderedBatches;
            private int _affectedUnits;

            public void BeforeGroup()
            {
                if (groupCount <= 1)
                    return;

                state.AtlasValueGateCoverageOrderedBatchCount = 0;
                state.AtlasValueGateCandidateAffectedUnitCount = 0;
            }

            public void AfterGroup()
            {
                if (groupCount <= 1)
                    return;

                _orderedBatches += state.AtlasValueGateCoverageOrderedBatchCount;
                _affectedUnits = Math.Max(_affectedUnits, state.AtlasValueGateCandidateAffectedUnitCount);
                state.AtlasValueGateCoverageOrderedBatchCount = _orderedBatches;
                state.AtlasValueGateCandidateAffectedUnitCount = _affectedUnits;
            }
        }

        private static void CountKeepAlphaFormatGroups(BatchState state, List<List<AtlasCandidate>> groups)
        {
            if (groups.Count == 0 || groups.All(group => group.Count == 0))
                return;

            state.KeepAlphaFormatGroupsPlanned += groups.Count;
            state.KeepAlphaFormatGroupsWithAlpha += groups.Count(
                group => group.Any(candidate => candidate.KeepAlphaChannels.Count != 0));
        }

        // The first reason a member of the group needs the slot's alpha kept, else null.
        private static string? GetKeepAlphaReason(IEnumerable<AtlasCandidate> candidates, string slot)
            => candidates
                .Where(candidate => candidate.KeepAlphaChannels.Contains(slot))
                .OrderBy(candidate => candidate.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Key.LodIndex)
                .ThenBy(candidate => candidate.Key.PartIndex)
                .Select(candidate => candidate.KeepAlphaReasons.GetValueOrDefault(slot) ?? "alpha must be kept")
                .FirstOrDefault();

        // BC3 when any member needs the channel's alpha kept; else the game's format for it.
        private static DXGI_FORMAT GetAtlasChannelFormat(
            IEnumerable<AtlasCandidate> candidates,
            string slot,
            TextureType type)
        {
            var format = DDSFormatHelper.GetDDSFormat(GameTypeEnum.Warhammer3, type);
            return format == DXGI_FORMAT.BC1_UNORM_SRGB &&
                   candidates.Any(candidate => candidate.KeepAlphaChannels.Contains(slot))
                ? DXGI_FORMAT.BC3_UNORM_SRGB
                : format;
        }

        private static int GetAtlasBytesPerBlock(DXGI_FORMAT format)
            => format is DXGI_FORMAT.BC1_UNORM or DXGI_FORMAT.BC1_UNORM_SRGB ? 8 : 16;

        // Only a BC1 atlas needs its alpha flattened. Discovery already gave every material
        // whose shader could read the alpha a BC3 channel, so a source that still has alpha in a
        // BC1 batch only needs it set to 255 before the encode.
        private static HashSet<int>? GetForceOpaqueAlphaSourceIds(
            BatchState state,
            DXGI_FORMAT format,
            IReadOnlyDictionary<int, string> sourcePaths,
            IReadOnlyDictionary<int, byte[]> textureBytes,
            IReadOnlyDictionary<int, TextureAtlasConstantColor> constantSources)
        {
            if (format != DXGI_FORMAT.BC1_UNORM_SRGB)
                return null;

            HashSet<int>? forced = null;
            foreach (var (id, bytes) in textureBytes)
            {
                var alpha = sourcePaths.TryGetValue(id, out var path)
                    ? GetTextureAlphaState(state, path, () => bytes)
                    : ClassifyTextureAlpha(bytes);
                if (alpha == TextureAlphaState.HasAlpha)
                    (forced ??= []).Add(id);
            }

            foreach (var (id, color) in constantSources)
            {
                if (color.A != byte.MaxValue)
                    (forced ??= []).Add(id);
            }

            return forced;
        }
    }
}
