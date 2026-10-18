using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml;
using Shared.Core.PackFiles.Models;
using Shared.GameFormats.RigidModel;

namespace Editors.KitbasherEditor.Services
{
    public sealed partial class PackTextureAtlasBatchService
    {
        // Opt-in "exact texture compaction": lossless rewrites of textures this pack owns, run
        // last on the bytes in the output. Two transformations, both verified texel for texel
        // (every level, RGBA) before anything is written:
        //  * a texture whose every texel of every mip level is the same colour becomes 4x4 with a
        //    full chain, one source block copied verbatim into each level;
        //  * a BC2/BC3 texture whose alpha is 255 everywhere becomes BC1 with the same colour
        //    blocks (BC2/BC3 colour blocks decode in 4-colour mode, so a block with c0 < c1 has its
        //    endpoints swapped and its indices remapped, one with c0 == c1 gets indices 0).
        // Textures read by anything but a material slot are left alone. Textures the atlas or the
        // shared repack wrote are compacted like source textures (they are in the output, exact
        // and verified the same way); they are not source files, so the ownership and
        // vanilla-override checks do not apply to them.
        private const int TextureCompactionListLimit = 40;
        private const int TextureCompactionExampleLimit = 3;

        private const string TextureCompactionReasonDownscaled = "downscaled in this run";
        private const string TextureCompactionReasonNotOwned = "not a texture of the source pack";
        private const string TextureCompactionReasonNonMaterial =
            "referenced by a non-material file (UI, VMD, WSModel or other text)";
        private const string TextureCompactionReasonUnreferenced =
            "not referenced by any material (xml material or rigid-embedded)";
        private const string TextureCompactionReasonUnreadable = "unreadable or unsupported DDS layout";
        private const string TextureCompactionReasonTruncated = "shorter than its header declares";
        private const string TextureCompactionReasonNoSaving =
            "nothing exact to gain (not uniform, and not an opaque BC2/BC3)";
        private const string TextureCompactionReasonNoSmaller = "uniform, but not larger than its replacement";

        private sealed class TextureCompactionEntry
        {
            public string Path { get; init; } = string.Empty;
            public bool IsUniform { get; init; }
            public DdsLayout Source { get; init; } = null!;
            public int NewWidth { get; init; }
            public int NewHeight { get; init; }
            public int NewMipCount { get; init; }
            public string NewFormat { get; init; } = string.Empty;
            public long BytesBefore { get; init; }
            public long BytesAfter { get; init; }
            // The one colour of a uniform texture (R, G, B, A packed from the low byte up).
            public uint Colour { get; init; }
            // The bytes a transcode started from, for the validation pass.
            public byte[]? SourceBytes { get; set; }
            // Only a rigid's embedded material names it, no xml material does.
            public bool RigidEmbeddedOnly { get; set; }
            // BC1 transcode of a texture whose alpha is not 255 but that no shader reads, and
            // who reads which slot ("t_xml_base_colour by weighted2_character").
            // Written by the atlas or the shared repack in this run, not a source texture.
            public bool Generated { get; init; }
            public bool AlphaDropped { get; init; }
            public string AlphaUses { get; init; } = string.Empty;
            public long Saved => BytesBefore - BytesAfter;
        }

        private sealed class TextureCompactionResult
        {
            public bool Requested { get; set; }
            public string? Failure { get; set; }
            public TimeSpan Elapsed { get; set; }
            public int Considered { get; set; }
            // Of the DDS files in the output: named by an xml material slot, by a rigid's
            // embedded material, and by the latter only.
            public int XmlReferenced { get; set; }
            public int RigidReferenced { get; set; }
            public int RigidOnlyReferenced { get; set; }
            public int RigidsRead { get; set; }
            public int RigidsUnreadable { get; set; }
            // The shader channel table the drop-unread-alpha rule used (null: not loaded).
            public string? ChannelTableSource { get; set; }
            public string? ChannelTableError { get; set; }
            public List<TextureCompactionEntry> Compacted { get; } = [];
            public List<(string Path, string Reason)> Failed { get; } = [];
            public Dictionary<string, int> Skipped { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, List<string>> SkippedExamples { get; } = new(StringComparer.Ordinal);
        }

        // The channels each unit shader samples per texture slot, generated from the game's
        // compiled pixel shaders (Tools/ShaderChannels/extract_shader_channels.py) and embedded.
        private sealed class Wh3ShaderChannelTable
        {
            public string Source { get; init; } = string.Empty;
            public Dictionary<string, Dictionary<string, string>> Shaders { get; init; } =
                new(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly Lazy<(Wh3ShaderChannelTable? Table, string? Error)> Wh3ShaderChannels =
            new(LoadWh3ShaderChannelTable);

        private static (Wh3ShaderChannelTable? Table, string? Error) LoadWh3ShaderChannelTable()
        {
            try
            {
                var assembly = typeof(PackTextureAtlasBatchService).Assembly;
                var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(name =>
                    name.EndsWith("Wh3ShaderTextureChannels.json", StringComparison.OrdinalIgnoreCase));
                if (resourceName == null)
                    return (null, "the embedded shader channel table was not found");

                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                    return (null, "the embedded shader channel table could not be opened");

                using var document = JsonDocument.Parse(stream);
                var table = new Wh3ShaderChannelTable
                {
                    Source = document.RootElement.TryGetProperty("source", out var source)
                        ? source.GetString() ?? string.Empty
                        : string.Empty,
                };
                foreach (var shader in document.RootElement.GetProperty("shaders").EnumerateObject())
                {
                    var slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var slot in shader.Value.EnumerateObject())
                        slots[slot.Name] = slot.Value.GetString() ?? string.Empty;
                    table.Shaders[shader.Name] = slots;
                }

                return table.Shaders.Count == 0
                    ? (null, "the embedded shader channel table is empty")
                    : (table, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (null, $"the embedded shader channel table could not be read ({ex.GetType().Name}: {ex.Message})");
            }
        }

        // "shaders\\weighted2_character.xml.shader" -> "weighted2_character".
        private static string? GetCompactionShaderName(string? shader)
        {
            if (string.IsNullOrWhiteSpace(shader))
                return null;

            var name = Normalize(shader);
            if (name.StartsWith("shaders\\", StringComparison.Ordinal))
                name = name["shaders\\".Length..];
            if (name.EndsWith(".xml.shader", StringComparison.Ordinal))
                name = name[..^".xml.shader".Length];
            return name.Length == 0 ? null : name;
        }

        // ---------------------------------------------------------------------------
        // Exact block decoding (BC1, BC2, BC3). Texels are packed R | G << 8 | B << 16 | A << 24.
        // ---------------------------------------------------------------------------

        private static uint ExpandCompactionColour(int packed565, int alpha)
        {
            var r = (packed565 >> 11) & 31;
            var g = (packed565 >> 5) & 63;
            var b = packed565 & 31;
            return PackCompactionColour((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2), alpha);
        }

        private static uint PackCompactionColour(int r, int g, int b, int a)
            => (uint)(r | (g << 8) | (b << 16) | (a << 24));

        private static uint BlendCompactionColour(uint first, int firstWeight, uint second, int secondWeight)
        {
            var total = firstWeight + secondWeight;
            int Channel(int shift)
                => ((int)((first >> shift) & 255) * firstWeight + (int)((second >> shift) & 255) * secondWeight) / total;
            return PackCompactionColour(Channel(0), Channel(8), Channel(16), 255);
        }

        // The 8-byte colour block. BC1 switches to 3-colour mode (index 3 transparent) when
        // c0 <= c1; the colour block of BC2 and BC3 always decodes in 4-colour mode.
        private static void DecodeCompactionColour(ReadOnlySpan<byte> block, bool bc1, Span<uint> texels)
        {
            int c0 = block[0] | (block[1] << 8);
            int c1 = block[2] | (block[3] << 8);
            Span<uint> palette = stackalloc uint[4];
            palette[0] = ExpandCompactionColour(c0, 255);
            palette[1] = ExpandCompactionColour(c1, 255);
            if (!bc1 || c0 > c1)
            {
                palette[2] = BlendCompactionColour(palette[0], 2, palette[1], 1);
                palette[3] = BlendCompactionColour(palette[0], 1, palette[1], 2);
            }
            else
            {
                palette[2] = BlendCompactionColour(palette[0], 1, palette[1], 1);
                palette[3] = 0;
            }

            var indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
            for (var texel = 0; texel < 16; texel++)
                texels[texel] = palette[(int)((indices >> (2 * texel)) & 3)];
        }

        // The 8-byte BC3 alpha block: 8 alphas when a0 > a1, else 6 with 0 and 255 as the last two.
        private static void DecodeCompactionAlpha(ReadOnlySpan<byte> block, Span<byte> alpha)
        {
            int a0 = block[0];
            int a1 = block[1];
            Span<int> palette = stackalloc int[8];
            palette[0] = a0;
            palette[1] = a1;
            if (a0 > a1)
            {
                for (var step = 1; step <= 6; step++)
                    palette[1 + step] = ((7 - step) * a0 + step * a1) / 7;
            }
            else
            {
                for (var step = 1; step <= 4; step++)
                    palette[1 + step] = ((5 - step) * a0 + step * a1) / 5;
                palette[6] = 0;
                palette[7] = 255;
            }

            ulong bits = 0;
            for (var index = 0; index < 6; index++)
                bits |= (ulong)block[2 + index] << (8 * index);
            for (var texel = 0; texel < 16; texel++)
                alpha[texel] = (byte)palette[(int)((bits >> (3 * texel)) & 7)];
        }

        private static void DecodeCompactionBlock(string format, ReadOnlySpan<byte> block, Span<uint> texels)
        {
            if (format == "BC1")
            {
                DecodeCompactionColour(block, true, texels);
                return;
            }

            DecodeCompactionColour(block[8..], false, texels);
            Span<byte> alpha = stackalloc byte[16];
            if (format == "BC2")
            {
                var bits = BinaryPrimitives.ReadUInt64LittleEndian(block);
                for (var texel = 0; texel < 16; texel++)
                    alpha[texel] = (byte)(((bits >> (4 * texel)) & 15) * 17);
            }
            else
            {
                DecodeCompactionAlpha(block, alpha);
            }

            for (var texel = 0; texel < 16; texel++)
                texels[texel] = (texels[texel] & 0x00FFFFFFu) | ((uint)alpha[texel] << 24);
        }

        private static IEnumerable<(int Width, int Height, long Offset)> EnumerateCompactionLevels(DdsLayout layout)
        {
            long offset = layout.HeaderSize;
            for (var level = 0; level < layout.MipCount; level++)
            {
                var width = Math.Max(1, layout.Width >> level);
                var height = Math.Max(1, layout.Height >> level);
                yield return (width, height, offset);
                offset += GetDdsLevelBytes(width, height, layout.BytesPerBlock);
            }
        }

        // Every texel of every level is the same RGBA. Texels outside a level (the padding of
        // a block at the edge of a level smaller than 4 pixels) are not part of the image.
        private static bool TryFindUniformColour(byte[] dds, DdsLayout layout, out uint colour)
        {
            colour = 0;
            var have = false;
            Span<uint> texels = stackalloc uint[16];
            var bytesPerBlock = layout.BytesPerBlock;
            var lastUniform = new byte[bytesPerBlock];
            var haveLast = false;
            foreach (var (width, height, offset) in EnumerateCompactionLevels(layout))
            {
                var blocksWide = (width + 3) / 4;
                var blocksHigh = (height + 3) / 4;
                for (var by = 0; by < blocksHigh; by++)
                {
                    for (var bx = 0; bx < blocksWide; bx++)
                    {
                        var block = dds.AsSpan(checked((int)(offset + ((long)by * blocksWide + bx) * bytesPerBlock)), bytesPerBlock);
                        var columns = Math.Min(4, width - bx * 4);
                        var rows = Math.Min(4, height - by * 4);
                        var full = columns == 4 && rows == 4;
                        if (full && haveLast && block.SequenceEqual(lastUniform))
                            continue;

                        DecodeCompactionBlock(layout.Format, block, texels);
                        for (var y = 0; y < rows; y++)
                        {
                            for (var x = 0; x < columns; x++)
                            {
                                var texel = texels[y * 4 + x];
                                if (!have)
                                {
                                    colour = texel;
                                    have = true;
                                }
                                else if (texel != colour)
                                {
                                    return false;
                                }
                            }
                        }

                        if (full)
                        {
                            block.CopyTo(lastUniform);
                            haveLast = true;
                        }
                    }
                }
            }

            return have;
        }

        // Every texel of every level has alpha 255 (BC2: nibble 15; BC3: both alpha modes, so
        // index 6 of the 6-alpha mode, alpha 0, counts).
        private static bool AreAllTexelsOpaque(byte[] dds, DdsLayout layout)
        {
            Span<uint> texels = stackalloc uint[16];
            var bytesPerBlock = layout.BytesPerBlock;
            var lastOpaque = new byte[bytesPerBlock];
            var haveLast = false;
            foreach (var (width, height, offset) in EnumerateCompactionLevels(layout))
            {
                var blocksWide = (width + 3) / 4;
                var blocksHigh = (height + 3) / 4;
                for (var by = 0; by < blocksHigh; by++)
                {
                    for (var bx = 0; bx < blocksWide; bx++)
                    {
                        var block = dds.AsSpan(checked((int)(offset + ((long)by * blocksWide + bx) * bytesPerBlock)), bytesPerBlock);
                        var columns = Math.Min(4, width - bx * 4);
                        var rows = Math.Min(4, height - by * 4);
                        var full = columns == 4 && rows == 4;
                        if (full && haveLast && block.SequenceEqual(lastOpaque))
                            continue;

                        DecodeCompactionBlock(layout.Format, block, texels);
                        for (var y = 0; y < rows; y++)
                        {
                            for (var x = 0; x < columns; x++)
                            {
                                if ((texels[y * 4 + x] >> 24) != 255)
                                    return false;
                            }
                        }

                        if (full)
                        {
                            block.CopyTo(lastOpaque);
                            haveLast = true;
                        }
                    }
                }
            }

            return true;
        }

        // ---------------------------------------------------------------------------
        // Building and verifying the replacements.
        // ---------------------------------------------------------------------------

        // One 4x4 block of the source repeated for a 4x4, 2x2 and 1x1 level.
        private static byte[] BuildUniformCompaction(byte[] dds, DdsLayout layout)
        {
            var header = BuildDdsHeader(layout, 4, 4, 3);
            var block = dds.AsSpan(layout.HeaderSize, layout.BytesPerBlock);
            var result = new byte[header.Length + 3 * layout.BytesPerBlock];
            header.CopyTo(result, 0);
            for (var level = 0; level < 3; level++)
                block.CopyTo(result.AsSpan(header.Length + level * layout.BytesPerBlock));
            return result;
        }

        private static bool TryGetCompactionBc1Dxgi(uint dxgi, out uint bc1)
        {
            bc1 = dxgi switch
            {
                73 or 76 => 70,
                74 or 77 => 71,
                75 or 78 => 72,
                _ => 0u,
            };
            return bc1 != 0;
        }

        private static bool TryBuildOpaqueBc1(byte[] dds, DdsLayout layout, out byte[] result)
        {
            result = [];
            var header = (byte[])layout.Header.Clone();
            if (layout.IsDx10)
            {
                if (!TryGetCompactionBc1Dxgi(layout.DxgiFormat, out var dxgi))
                    return false;
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(128, 4), dxgi);
            }
            else
            {
                const uint dxt1 = 0x31545844;
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(84, 4), dxt1);
            }

            const uint linearSizeFlag = 0x80000;
            if ((BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4)) & linearSizeFlag) != 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    header.AsSpan(20, 4),
                    checked((uint)GetDdsLevelBytes(layout.Width, layout.Height, 8)));
            }

            long dataBytes = 0;
            foreach (var (width, height, _) in EnumerateCompactionLevels(layout))
                dataBytes += GetDdsLevelBytes(width, height, 8);

            result = new byte[header.Length + dataBytes];
            header.CopyTo(result, 0);
            var target = header.Length;
            var bytesPerBlock = layout.BytesPerBlock;
            foreach (var (width, height, offset) in EnumerateCompactionLevels(layout))
            {
                var blocks = ((width + 3L) / 4) * ((height + 3L) / 4);
                for (long block = 0; block < blocks; block++)
                {
                    var colour = dds.AsSpan(checked((int)(offset + block * bytesPerBlock + 8)), 8);
                    var output = result.AsSpan(target, 8);
                    int c0 = colour[0] | (colour[1] << 8);
                    int c1 = colour[2] | (colour[3] << 8);
                    var indices = BinaryPrimitives.ReadUInt32LittleEndian(colour[4..]);
                    if (c0 > c1)
                    {
                        colour.CopyTo(output);
                    }
                    else if (c0 < c1)
                    {
                        // Swapped endpoints: the 4-colour palette reads in the opposite order,
                        // so index 0 <-> 1 and 2 <-> 3.
                        BinaryPrimitives.WriteUInt16LittleEndian(output, (ushort)c1);
                        BinaryPrimitives.WriteUInt16LittleEndian(output[2..], (ushort)c0);
                        BinaryPrimitives.WriteUInt32LittleEndian(output[4..], indices ^ 0x55555555u);
                    }
                    else
                    {
                        // BC1 would read c0 == c1 as 3-colour mode (index 3 transparent); every
                        // palette entry of the source is c0, so index 0 everywhere is the same.
                        BinaryPrimitives.WriteUInt16LittleEndian(output, (ushort)c0);
                        BinaryPrimitives.WriteUInt16LittleEndian(output[2..], (ushort)c1);
                        BinaryPrimitives.WriteUInt32LittleEndian(output[4..], 0);
                    }

                    target += 8;
                }
            }

            return true;
        }

        private static string? VerifyCompactionUniform(byte[] dds, DdsLayout layout, uint colour)
        {
            if (dds.LongLength < layout.HeaderSize + layout.ChainBytes)
                return "is shorter than its header declares";
            if (!TryFindUniformColour(dds, layout, out var found))
                return "is not uniform";
            return found == colour
                ? null
                : $"is colour {found:X8}, the source was {colour:X8}";
        }

        // Texel for texel, every level: the new BC1 equals the source, and the source is opaque.
        private static string? VerifyCompactionTranscode(
            byte[] source,
            DdsLayout sourceLayout,
            byte[] result,
            DdsLayout resultLayout,
            bool ignoreAlpha = false)
        {
            if (resultLayout.Format != "BC1" ||
                resultLayout.Width != sourceLayout.Width ||
                resultLayout.Height != sourceLayout.Height ||
                resultLayout.MipCount != sourceLayout.MipCount ||
                resultLayout.IsSrgb != sourceLayout.IsSrgb ||
                resultLayout.IsDx10 != sourceLayout.IsDx10)
            {
                return $"has the layout {resultLayout.FormatDescription}, {resultLayout.Width}x{resultLayout.Height}";
            }

            if (result.LongLength != resultLayout.HeaderSize + resultLayout.ChainBytes ||
                source.LongLength < sourceLayout.HeaderSize + sourceLayout.ChainBytes)
            {
                return "has a size that does not match its header";
            }

            Span<uint> before = stackalloc uint[16];
            Span<uint> after = stackalloc uint[16];
            var oldLevels = EnumerateCompactionLevels(sourceLayout).ToList();
            var newLevels = EnumerateCompactionLevels(resultLayout).ToList();
            for (var level = 0; level < oldLevels.Count; level++)
            {
                var (width, height, oldOffset) = oldLevels[level];
                var newOffset = newLevels[level].Offset;
                var blocksWide = (width + 3) / 4;
                var blocksHigh = (height + 3) / 4;
                for (var by = 0; by < blocksHigh; by++)
                {
                    for (var bx = 0; bx < blocksWide; bx++)
                    {
                        var index = (long)by * blocksWide + bx;
                        DecodeCompactionBlock(
                            sourceLayout.Format,
                            source.AsSpan(checked((int)(oldOffset + index * sourceLayout.BytesPerBlock)), sourceLayout.BytesPerBlock),
                            before);
                        DecodeCompactionBlock(
                            "BC1",
                            result.AsSpan(checked((int)(newOffset + index * 8)), 8),
                            after);
                        var columns = Math.Min(4, width - bx * 4);
                        var rows = Math.Min(4, height - by * 4);
                        for (var y = 0; y < rows; y++)
                        {
                            for (var x = 0; x < columns; x++)
                            {
                                var texel = y * 4 + x;
                                if (!ignoreAlpha && (before[texel] >> 24) != 255)
                                    return $"source is not opaque at level {level}, block ({bx},{by}), texel ({x},{y})";
                                // With the alpha dropped only the colour is compared.
                                var difference = before[texel] ^ after[texel];
                                if ((ignoreAlpha ? difference & 0x00FFFFFFu : difference) != 0)
                                {
                                    return $"differs at level {level}, block ({bx},{by}), texel ({x},{y}): " +
                                           $"{before[texel]:X8} -> {after[texel]:X8}";
                                }
                            }
                        }
                    }
                }
            }

            return null;
        }

        // ---------------------------------------------------------------------------
        // Stage entry.
        // ---------------------------------------------------------------------------

        private void RunTextureCompaction(
            BatchState state,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var result = new TextureCompactionResult { Requested = state.CompactTextures };
            state.TextureCompaction = result;
            if (!state.CompactTextures)
                return;

            var stopwatch = Stopwatch.StartNew();
            try
            {
                ReportProgress(progress, "Exact texture compaction");
                CompactTextures(state, result, cancellationToken, progress);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The files changed so far were each verified; the rest stay as they are.
                result.Failure = $"{ex.GetType().Name}: {ex.Message}";
            }

            result.Elapsed = stopwatch.Elapsed;
            state.PhaseDurations["Texture compaction"] = result.Elapsed;
        }

        private void CompactTextures(
            BatchState state,
            TextureCompactionResult result,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            // References in the output pack: material slots, and everything else that names a
            // texture (an unreadable material counts as such a file).
            var materialReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var otherReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rigidReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Per texture: every xml material slot that names it, with the material's shader.
            var materialUses = new Dictionary<string, List<(string Material, string? Shader, string Slot)>>(
                StringComparer.OrdinalIgnoreCase);
            // Per texture: the output rigids whose embedded materials name it.
            var rigidsByTexture = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            Wh3ShaderChannelTable? channelTable = null;
            if (state.DropUnreadAlpha)
            {
                var (table, error) = Wh3ShaderChannels.Value;
                channelTable = table;
                result.ChannelTableSource = table?.Source;
                result.ChannelTableError = error;
            }

            var textures = new List<string>();
            foreach (var (rawPath, file) in state.Output.GetAllFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Normalize(rawPath);
                if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    textures.Add(path);
                }
                else if (path.EndsWith(".xml.material", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var document = LoadXml(file);
                        var shader = GetCompactionShaderName(document.SelectSingleNode("/material/shader")?.InnerText);
                        var nodes = document.SelectNodes("/material/textures/texture");
                        if (nodes != null)
                        {
                            foreach (XmlNode node in nodes)
                            {
                                var texture = Normalize(node.SelectSingleNode("source")?.InnerText ?? node.InnerText);
                                if (!IsTextureDownscaleTexturePath(texture))
                                    continue;

                                materialReferences.Add(texture);
                                if (state.DropUnreadAlpha)
                                {
                                    if (!materialUses.TryGetValue(texture, out var uses))
                                    {
                                        uses = [];
                                        materialUses[texture] = uses;
                                    }

                                    uses.Add((path, shader, GetTextureSlot(node)));
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        otherReferences.UnionWith(ExtractDownscaleTextureTokens(file));
                    }
                }
                else if (path.EndsWith(".rigid_model_v2", StringComparison.OrdinalIgnoreCase))
                {
                    // The embedded materials of the rigid, every texture type (the old WH2
                    // Diffuse, Specular, GlossMap slots included). A rigid that does not parse
                    // is treated like an unreadable material: whatever texture paths its bytes
                    // contain count as references outside a material.
                    try
                    {
                        var rigid = ModelFactory.Create().Load(file.DataSource.ReadData());
                        foreach (var lod in rigid.ModelList ?? [])
                        {
                            foreach (var model in lod)
                            {
                                foreach (var texture in model.Material?.GetAllTextures() ?? [])
                                {
                                    var embedded = Normalize(texture.Path);
                                    if (!IsTextureDownscaleTexturePath(embedded))
                                        continue;

                                    rigidReferences.Add(embedded);
                                    if (state.DropUnreadAlpha)
                                    {
                                        if (!rigidsByTexture.TryGetValue(embedded, out var naming))
                                        {
                                            naming = new SortedSet<string>(StringComparer.Ordinal);
                                            rigidsByTexture[embedded] = naming;
                                        }

                                        naming.Add(path);
                                    }
                                }
                            }
                        }

                        result.RigidsRead++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        result.RigidsUnreadable++;
                        otherReferences.UnionWith(ExtractDownscaleTextureTokens(file));
                    }
                }
                else if (IsTextureDownscaleTextFile(path))
                {
                    otherReferences.UnionWith(ExtractDownscaleTextureTokens(file));
                }
            }

            var generated = state.GeneratedTexturePaths
                .Concat(state.SharedRepackTexturePaths)
                .Select(Normalize)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var downscaled = (state.TextureDownscale?.Rewritten ?? [])
                .Select(item => Normalize(item.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            void Skip(string reason, string path, string? detail = null)
            {
                result.Skipped[reason] = result.Skipped.GetValueOrDefault(reason) + 1;
                if (!result.SkippedExamples.TryGetValue(reason, out var examples))
                {
                    examples = [];
                    result.SkippedExamples[reason] = examples;
                }

                if (examples.Count < TextureCompactionExampleLimit)
                    examples.Add(detail == null ? path : $"{path} ({detail})");
            }

            var containers = GetGameplayTraversalContainers(state);
            // Whether the vanilla game data is loaded at all: without it an override of a vanilla
            // texture cannot be ruled out.
            var vanillaKnown = containers.Any(container =>
                container.IsCaPackFile && !ReferenceEquals(container, state.Source));
            // Whether a rigid that names a texture is drawn only through WSModels of the source pack
            // (the game then ignores its embedded material). The WSModel bindings and the
            // referrer index are built once, and only if a candidate needs them. Anything that
            // cannot be answered blocks.
            Dictionary<string, HashSet<string>>? wsByRigid = null;
            var rigidVerdicts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            string? RigidReason(string rigidPath)
            {
                if (rigidVerdicts.TryGetValue(rigidPath, out var cached))
                    return cached;

                string? reason;
                try
                {
                    if (wsByRigid == null)
                    {
                        wsByRigid = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                        var bindings = state.VanillaTextureCoverage?.Bindings
                                       ?? EnumerateSourceWsModelBindings(
                                           state.Source.GetAllFiles(), cancellationToken).Bindings;
                        foreach (var binding in bindings)
                        {
                            if (binding.Kind == SourceWsModelBindingKind.UnreadableWsModel || binding.Geometry.Length == 0)
                                continue;

                            if (!wsByRigid.TryGetValue(binding.Geometry, out var bound))
                            {
                                bound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                wsByRigid[binding.Geometry] = bound;
                            }

                            bound.Add(binding.WsModelPath);
                        }
                    }

                    var referrers = state.RigidReferrerIndex ??=
                        IndexSharedRepackRigidReferrers(state, cancellationToken);
                    var boundWsModels = wsByRigid.GetValueOrDefault(rigidPath) ?? [];
                    reason = GetRigidNotOnlyThroughOwnedWsModelsReason(state, rigidPath, boundWsModels, referrers)
                             ?? (boundWsModels.Count == 0 ? "the rigid is not used through any WSModel" : null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    reason = $"its WSModel usage could not be determined ({ex.GetType().Name})";
                }

                rigidVerdicts[rigidPath] = reason;
                return reason;
            }

            textures.Sort(StringComparer.Ordinal);
            result.Considered = textures.Count;
            result.XmlReferenced = textures.Count(texture => materialReferences.Contains(texture));
            result.RigidReferenced = textures.Count(texture => rigidReferences.Contains(texture));
            result.RigidOnlyReferenced = textures.Count(
                texture => rigidReferences.Contains(texture) && !materialReferences.Contains(texture));
            for (var index = 0; index < textures.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = textures[index];
                var isGenerated = generated.Contains(path);
                if (downscaled.Contains(path))
                {
                    Skip(TextureCompactionReasonDownscaled, path);
                    continue;
                }

                if (!isGenerated && !state.Source.ContainsFile(path))
                {
                    Skip(TextureCompactionReasonNotOwned, path);
                    continue;
                }

                if (otherReferences.Contains(path))
                {
                    Skip(TextureCompactionReasonNonMaterial, path);
                    continue;
                }

                if (!materialReferences.Contains(path) && !rigidReferences.Contains(path))
                {
                    Skip(TextureCompactionReasonUnreferenced, path);
                    continue;
                }

                ReportProgress(progress, "Compacting textures", index + 1, textures.Count, path);
                try
                {
                    var alphaRule = channelTable == null
                        ? null
                        : new TextureCompactionAlphaRule(
                            channelTable,
                            rigidsByTexture.GetValueOrDefault(path)?.ToList() ?? [],
                            RigidReason,
                            materialUses.GetValueOrDefault(path) ?? [],
                            vanillaKnown,
                            isGenerated);
                    CompactTexture(state, result, path, !materialReferences.Contains(path), isGenerated, alphaRule, Skip);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Failed.Add((path, $"{ex.GetType().Name}: {ex.Message}"));
                }
            }
        }

        private void CompactTexture(
            BatchState state,
            TextureCompactionResult result,
            string path,
            bool rigidEmbeddedOnly,
            bool generated,
            TextureCompactionAlphaRule? alphaRule,
            TextureCompactionSkip skip)
        {
            var bytes = state.Output.FindFile(path)?.DataSource.ReadData();
            if (bytes == null ||
                !TryReadDdsLayout(
                    bytes.AsSpan(0, Math.Min(bytes.Length, TextureDownscaleHeaderPeekBytes)).ToArray(),
                    out var layout))
            {
                skip(TextureCompactionReasonUnreadable, path);
                return;
            }

            if (layout.Format is not ("BC1" or "BC2" or "BC3"))
            {
                skip($"{layout.Format} is not decoded exactly (BC1, BC2 and BC3 only)", path);
                return;
            }

            if (bytes.LongLength < layout.HeaderSize + layout.ChainBytes)
            {
                skip(TextureCompactionReasonTruncated, path);
                return;
            }

            if (TryFindUniformColour(bytes, layout, out var colour))
            {
                if (layout.Width < 4 || layout.Height < 4)
                {
                    skip(TextureCompactionReasonNoSmaller, path);
                    return;
                }

                var replacement = BuildUniformCompaction(bytes, layout);
                if (replacement.LongLength >= bytes.LongLength)
                {
                    skip(TextureCompactionReasonNoSmaller, path);
                    return;
                }

                var problem = CheckCompactionStructure(replacement, layout, 4, 4, 3, layout.Format)
                              ?? VerifyCompactionUniform(replacement, ReadCompactionLayout(replacement)!, colour);
                if (problem != null)
                {
                    result.Failed.Add((path, $"uniform replacement {problem}"));
                    return;
                }

                WriteFile(state.Output, path, replacement);
                result.Compacted.Add(new TextureCompactionEntry
                {
                    Path = path,
                    IsUniform = true,
                    Source = layout,
                    NewWidth = 4,
                    NewHeight = 4,
                    NewMipCount = 3,
                    NewFormat = layout.Format,
                    BytesBefore = bytes.LongLength,
                    BytesAfter = replacement.LongLength,
                    Colour = colour,
                    RigidEmbeddedOnly = rigidEmbeddedOnly,
                    Generated = generated,
                });
                return;
            }

            var dropAlpha = false;
            string alphaUses = string.Empty;
            if (layout.Format == "BC1" || !AreAllTexelsOpaque(bytes, layout))
            {
                // Alpha that is really there: only the opt-in rule can still shrink it, when
                // nothing that uses the texture reads its alpha.
                if (layout.Format == "BC1" || alphaRule == null)
                {
                    skip(TextureCompactionReasonNoSaving, path);
                    return;
                }

                var refusal = CheckUnreadAlpha(state, path, alphaRule, out alphaUses, out var refusalDetail);
                if (refusal != null)
                {
                    skip(refusal, path, refusalDetail);
                    return;
                }

                dropAlpha = true;
            }

            if (!TryBuildOpaqueBc1(bytes, layout, out var transcoded))
            {
                skip($"{layout.Format} with a DXGI format that has no BC1 equivalent", path);
                return;
            }

            var resultLayout = ReadCompactionLayout(transcoded);
            var transcodeProblem = resultLayout == null
                ? "has an unreadable header"
                : VerifyCompactionTranscode(bytes, layout, transcoded, resultLayout, ignoreAlpha: dropAlpha);
            if (transcodeProblem != null)
            {
                result.Failed.Add((path, $"BC1 transcode {transcodeProblem}"));
                return;
            }

            WriteFile(state.Output, path, transcoded);
            result.Compacted.Add(new TextureCompactionEntry
            {
                Path = path,
                IsUniform = false,
                Source = layout,
                NewWidth = layout.Width,
                NewHeight = layout.Height,
                NewMipCount = layout.MipCount,
                NewFormat = "BC1",
                BytesBefore = bytes.LongLength,
                BytesAfter = transcoded.LongLength,
                SourceBytes = bytes,
                RigidEmbeddedOnly = rigidEmbeddedOnly,
                Generated = generated,
                AlphaDropped = dropAlpha,
                AlphaUses = alphaUses,
            });
        }

        private delegate void TextureCompactionSkip(string reason, string path, string? detail = null);

        private const string TextureCompactionRigidPrefix =
            "named by a rigid not drawn only through owned WSModels: ";

        // The rigid check's reasons carry paths; the skip key is a fixed category and the
        // path-bearing text goes into the example.
        private static string CategoriseCompactionRigidReason(string reason)
        {
            string category =
                reason.StartsWith("the rigid is not owned", StringComparison.Ordinal) ||
                reason == "the rigid overrides a vanilla path" ||
                reason.StartsWith("the rigid is referenced directly", StringComparison.Ordinal) ||
                reason.StartsWith("gameplay consumer discovery", StringComparison.Ordinal) ||
                reason == "the rigid is not used through any WSModel"
                    ? reason
                    : reason.StartsWith("a WSModel outside the source pack", StringComparison.Ordinal)
                        ? "a WSModel outside the source pack uses the rigid"
                        : reason.Contains("refers to the rigid other than as a readable WSModel binding", StringComparison.Ordinal)
                            ? "another file refers to the rigid other than as a WSModel binding"
                            : reason.StartsWith("WSModel ", StringComparison.Ordinal)
                                ? "a bound WSModel is not owned by the source pack or overrides a vanilla path"
                                : reason.StartsWith("its WSModel usage could not be determined", StringComparison.Ordinal)
                                    ? "its WSModel usage could not be determined"
                                    : "the rigid cannot be treated as drawn only through WSModels";
            return TextureCompactionRigidPrefix + category;
        }

        private sealed record TextureCompactionAlphaRule(
            Wh3ShaderChannelTable Table,
            List<string> Rigids,
            Func<string, string?> RigidReason,
            List<(string Material, string? Shader, string Slot)> Uses,
            bool VanillaKnown,
            // An atlas or repack output: its users are the generated materials in the output (all
            // read here), and it cannot override a vanilla texture, so the vanilla doubts do not apply.
            bool Generated = false);

        // Why the alpha of `slot` may be sampled by a user with this shader (a name as
        // GetCompactionShaderName returns it), else null: the one rule the compaction's drop-unread-
        // alpha and the atlas' BC1/BC3 choice share. Callers keep their own policy and wording. Every
        // doubt counts as a read: no table, a shader the table does not list, a slot the shader does
        // not bind, or a channel set that includes alpha.
        private enum AlphaReadDoubt
        {
            TableUnavailable,
            UnknownShader,
            UnboundSlot,
            ReadsAlpha,
        }

        private static AlphaReadDoubt? GetAlphaReadDoubt(Wh3ShaderChannelTable? table, string? shader, string slot)
        {
            if (table == null)
                return AlphaReadDoubt.TableUnavailable;
            if (shader == null || !table.Shaders.TryGetValue(shader, out var slots))
                return AlphaReadDoubt.UnknownShader;
            if (!slots.TryGetValue(slot, out var channels))
                return AlphaReadDoubt.UnboundSlot;
            return channels.Contains('a', StringComparison.OrdinalIgnoreCase)
                ? AlphaReadDoubt.ReadsAlpha
                : null;
        }

        // Null when no shader of any user reads the texture's alpha; else the first reason it
        // cannot be dropped. Every doubt blocks: unknown shader, unbound slot, a rigid-embedded
        // user, a possible vanilla user.
        private static string? CheckUnreadAlpha(
            BatchState state,
            string path,
            TextureCompactionAlphaRule rule,
            out string uses,
            out string? detail)
        {
            uses = string.Empty;
            detail = null;
            // A rigid's embedded material says nothing about the shader unless the game uses it,
            // which it does not for a rigid drawn through a WSModel: those xml materials decide.
            foreach (var rigid in rule.Rigids)
            {
                var rigidReason = rule.RigidReason(rigid);
                if (rigidReason != null)
                {
                    detail = $"rigid {rigid}: {rigidReason}";
                    return CategoriseCompactionRigidReason(rigidReason);
                }
            }

            if (rule.Uses.Count == 0)
                return "no xml material uses it";
            if (!rule.Generated && !rule.VanillaKnown)
                return "the vanilla game data is not loaded (cannot tell whether it overrides a vanilla texture)";
            if (!rule.Generated && OverridesVanillaPath(state, path))
                return "overrides a vanilla texture (vanilla materials may read its alpha)";

            foreach (var (_, shader, slot) in rule.Uses)
            {
                switch (GetAlphaReadDoubt(rule.Table, shader, slot))
                {
                    case AlphaReadDoubt.TableUnavailable:
                    case AlphaReadDoubt.UnknownShader:
                        return $"shader {shader ?? "(none)"} not in the channel table";
                    case AlphaReadDoubt.UnboundSlot:
                        return $"slot {slot} is not bound by shader {shader}";
                    case AlphaReadDoubt.ReadsAlpha:
                        return $"shader {shader} reads alpha of {slot}";
                }
            }

            uses = string.Join(
                "; ",
                rule.Uses
                    .GroupBy(use => use.Slot, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => $"{group.Key} by " + string.Join(
                        ", ",
                        group.Select(use => use.Shader!).Distinct(StringComparer.Ordinal)
                            .OrderBy(shader => shader, StringComparer.Ordinal))));
            if (rule.Rigids.Count != 0)
                uses += $"; also named by {rule.Rigids.Count:N0} rigid(s) drawn only through WSModels";
            return null;
        }

        private static DdsLayout? ReadCompactionLayout(byte[] dds)
            => TryReadDdsLayout(
                dds.AsSpan(0, Math.Min(dds.Length, TextureDownscaleHeaderPeekBytes)).ToArray(), out var layout)
                ? layout
                : null;

        // Structure of a rewritten texture: size, chain, format kind and a file as long as its header says.
        private static string? CheckCompactionStructure(
            byte[] dds,
            DdsLayout source,
            int width,
            int height,
            int mipCount,
            string format)
        {
            var layout = ReadCompactionLayout(dds);
            if (layout == null)
                return "has an unreadable header";
            if (layout.Width != width || layout.Height != height || layout.MipCount != mipCount)
            {
                return $"is {layout.Width}x{layout.Height} with {layout.MipCount} level(s), planned " +
                       $"{width}x{height} with {mipCount}";
            }

            if (!layout.Format.Equals(format, StringComparison.Ordinal) ||
                layout.IsSrgb != source.IsSrgb ||
                layout.IsDx10 != source.IsDx10)
            {
                return $"has the format {layout.FormatDescription}, planned {format} like the source";
            }

            return dds.LongLength == layout.HeaderSize + layout.ChainBytes
                ? null
                : $"is {dds.LongLength:N0} bytes, header and chain need {layout.HeaderSize + layout.ChainBytes:N0}";
        }

        // ---------------------------------------------------------------------------
        // Validation and report.
        // ---------------------------------------------------------------------------

        private static void ValidateTextureCompaction(
            BatchState state,
            List<string> errors,
            CancellationToken cancellationToken)
        {
            var result = state.TextureCompaction;
            if (result is not { Requested: true } || result.Compacted.Count == 0)
                return;

            var before = errors.Count;
            foreach (var entry in result.Compacted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = state.Output.FindFile(entry.Path)?.DataSource.ReadData();
                if (bytes == null)
                {
                    errors.Add($"Compacted texture is missing from output: {entry.Path}");
                    continue;
                }

                var problem = CheckCompactionStructure(
                    bytes, entry.Source, entry.NewWidth, entry.NewHeight, entry.NewMipCount, entry.NewFormat);
                if (problem == null)
                {
                    var layout = ReadCompactionLayout(bytes)!;
                    problem = entry.IsUniform
                        ? VerifyCompactionUniform(bytes, layout, entry.Colour)
                        : entry.SourceBytes == null
                            ? "has no source bytes to compare with"
                            : VerifyCompactionTranscode(
                                entry.SourceBytes, entry.Source, bytes, layout, ignoreAlpha: entry.AlphaDropped);
                }

                if (problem != null)
                    errors.Add($"Compacted texture {entry.Path} {problem}");
            }

            if (errors.Count == before)
            {
                var uniform = result.Compacted.Count(entry => entry.IsUniform);
                var alphaDropped = result.Compacted.Count(entry => entry.AlphaDropped);
                state.ValidationMessages.Add(
                    $"PASS: {uniform:N0} uniform and {result.Compacted.Count - uniform - alphaDropped:N0} BC1-transcoded " +
                    "texture(s) decode texel for texel to the source colours at every mip level, with valid " +
                    "headers and complete chains at their original paths." +
                    (alphaDropped == 0
                        ? string.Empty
                        : $" {alphaDropped:N0} alpha-dropped texture(s) match the source RGB texel for texel at " +
                          "every level (their alpha is read by no shader of any user)."));
            }
        }

        private static string DescribeCompactionFormat(DdsLayout layout)
            => $"{layout.Format}{(layout.IsSrgb ? "_SRGB" : string.Empty)} {(layout.IsDx10 ? "DX10" : "legacy")}";

        private static void AppendTextureCompactionSection(StringBuilder sb, BatchState state)
        {
            sb.AppendLine("Exact texture compaction");
            sb.AppendLine("------------------------");
            var result = state.TextureCompaction;
            sb.AppendLine($"Exact texture compaction: {(state.CompactTextures ? "YES" : "NO")}");
            sb.AppendLine($"Drop unread alpha: {(state.DropUnreadAlpha ? "YES" : "NO")}");
            if (result == null || !result.Requested)
            {
                sb.AppendLine("Off (opt-in). No texture was changed.");
                sb.AppendLine();
                return;
            }

            sb.AppendLine(
                "Mode: REWRITES owned textures (and the atlas and repack outputs of this run) that a material references (an xml material slot or a " +
                "rigid's embedded material), last, on the output's bytes. A " +
                "texture whose every texel of every mip level is one exact RGBA becomes 4x4 with a full chain " +
                "(one source block copied into each level); a BC2/BC3 texture whose alpha is 255 at every " +
                "texel of every level becomes BC1 (colour blocks kept, endpoints swapped and indices " +
                "remapped where c0 < c1). Each result is decoded and compared texel for texel before it is " +
                "written; nothing unverified is written. Same path, same sRGB-ness and header kind.");
            if (state.DropUnreadAlpha)
            {
                sb.AppendLine(
                    result.ChannelTableError == null
                        ? "Drop unread alpha: a BC2/BC3 texture whose alpha is not 255 everywhere becomes BC1 (colour " +
                          "blocks kept bit for bit, alpha dropped) when it is only used by xml materials whose shader " +
                          "never samples the alpha of its slot, it is not named by a rigid's embedded material and it " +
                          "does not override a vanilla texture. Channels per shader and slot come from the game's " +
                          $"compiled pixel shaders: {result.ChannelTableSource}. A shader or slot the table does not " +
                          "list blocks the texture."
                        : $"Drop unread alpha is on but the rule did nothing: {result.ChannelTableError}.");
            }

            if (result.Failure != null)
                sb.AppendLine($"The stage stopped early: {result.Failure}. Files written before that were verified.");

            var uniform = result.Compacted.Where(entry => entry.IsUniform).ToList();
            var transcoded = result.Compacted.Where(entry => !entry.IsUniform && !entry.AlphaDropped).ToList();
            var dropped = result.Compacted.Where(entry => entry.AlphaDropped).ToList();
            var skipped = result.Skipped.Values.Sum();
            var accounted = uniform.Count + transcoded.Count + dropped.Count + result.Failed.Count + skipped;
            sb.AppendLine($"DDS files in the output: {result.Considered:N0}. They split exactly into:");
            sb.AppendLine($"  Uniform, shrunk to 4x4: {uniform.Count:N0}");
            sb.AppendLine($"  BC2/BC3 transcoded to BC1: {transcoded.Count:N0}");
            if (state.DropUnreadAlpha)
            {
                sb.AppendLine(
                    $"  BC2/BC3 transcoded to BC1, alpha not read by any user's shader: {dropped.Count:N0}");
            }
            var generatedCompacted = result.Compacted.Where(entry => entry.Generated).ToList();
            sb.AppendLine(
                $"    of which atlas or repack outputs: {generatedCompacted.Count:N0} (saves " +
                $"{generatedCompacted.Sum(item => item.Saved):N0} bytes; source textures: " +
                $"{result.Compacted.Count - generatedCompacted.Count:N0}, saves " +
                $"{result.Compacted.Where(entry => !entry.Generated).Sum(item => item.Saved):N0} bytes)");
            sb.AppendLine($"  Failed verification or writing (kept as they were): {result.Failed.Count:N0}");
            sb.AppendLine($"  Skipped: {skipped:N0}");
            foreach (var (reason, count) in result.Skipped
                         .OrderByDescending(item => item.Value)
                         .ThenBy(item => item.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"    {count:N0} x {reason}");
                foreach (var example in result.SkippedExamples[reason])
                    sb.AppendLine($"        e.g. {example}");
            }

            sb.AppendLine(
                $"  Check: {accounted:N0} accounted of {result.Considered:N0} " +
                (accounted == result.Considered ? "(reconciles)" : "(DOES NOT RECONCILE)"));
            sb.AppendLine(
                $"Referenced by xml material slots: {result.XmlReferenced:N0}; by rigid-embedded materials: " +
                $"{result.RigidReferenced:N0} ({result.RigidOnlyReferenced:N0} only there); rigids read: " +
                $"{result.RigidsRead:N0} (unreadable {result.RigidsUnreadable:N0}).");
            sb.AppendLine(
                $"Bytes, uniform: {uniform.Sum(item => item.BytesBefore):N0} -> {uniform.Sum(item => item.BytesAfter):N0} " +
                $"(saves {uniform.Sum(item => item.Saved):N0}); transcoded: {transcoded.Sum(item => item.BytesBefore):N0} -> " +
                $"{transcoded.Sum(item => item.BytesAfter):N0} (saves {transcoded.Sum(item => item.Saved):N0}); " +
                (state.DropUnreadAlpha
                    ? $"alpha dropped: {dropped.Sum(item => item.BytesBefore):N0} -> {dropped.Sum(item => item.BytesAfter):N0} " +
                      $"(saves {dropped.Sum(item => item.Saved):N0}); "
                    : string.Empty) +
                $"total saved {result.Compacted.Sum(item => item.Saved):N0} bytes.");
            if (result.Failed.Count != 0)
            {
                sb.AppendLine($"Not compacted ({result.Failed.Count:N0}):");
                foreach (var (path, reason) in result.Failed.Take(10))
                    sb.AppendLine($"  {path}: {reason}");
            }

            if (result.Compacted.Count != 0)
            {
                var ordered = result.Compacted
                    .OrderByDescending(item => item.Saved)
                    .ThenBy(item => item.Path, StringComparer.Ordinal)
                    .ToList();
                sb.AppendLine(
                    ordered.Count > TextureCompactionListLimit
                        ? $"Top {TextureCompactionListLimit} by bytes saved:"
                        : "All by bytes saved:");
                foreach (var entry in ordered.Take(TextureCompactionListLimit))
                {
                    sb.AppendLine(
                        $"  {entry.Path}: {(entry.IsUniform ? "uniform" : entry.AlphaDropped ? "alpha unread" : "opaque")}, " +
                        $"{entry.Source.Width}x{entry.Source.Height} {DescribeCompactionFormat(entry.Source)} -> " +
                        $"{entry.NewWidth}x{entry.NewHeight} {entry.NewFormat}, " +
                        $"{entry.BytesBefore:N0} -> {entry.BytesAfter:N0} bytes" +
                        (entry.RigidEmbeddedOnly ? " [rigid-embedded only]" : string.Empty) +
                        (entry.Generated ? " [atlas or repack output]" : string.Empty) +
                        (entry.AlphaDropped ? $" [{entry.AlphaUses}]" : string.Empty));
                }

                if (ordered.Count > TextureCompactionListLimit)
                    sb.AppendLine($"  ... and {ordered.Count - TextureCompactionListLimit:N0} more");
            }

            sb.AppendLine($"Analysis time: {result.Elapsed.TotalMilliseconds:N0} ms");
            sb.AppendLine();
        }
    }
}
