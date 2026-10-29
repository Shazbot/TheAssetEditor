using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Shared.Core.PackFiles.Models;

namespace Editors.PackOptimizer.Core
{
    public sealed partial class PackTextureAtlasBatchService
    {
        // Opt-in "switch opaque alpha shaders": a material on shaders\{rigid|weighted2|weighted4}_character_alpha
        // whose base colour texture is opaque (alpha >= 250 at every texel of every mip level) is moved to
        // the same path without "_alpha". Alpha testing turns early-Z off and costs more in the shadow pass,
        // and the twin shader reads only rgb of that slot, so nothing the texture holds is lost. Texture
        // compaction then sees a shader that does not read the alpha and can drop it (BC3 to BC1).
        //
        // Only the material file's shader text changes. Independent of the asset-check and LOD-audit
        // passes (those are skipped when ReportDiagnostics is off); it reuses their material reading and
        // shader assessment helpers. Every doubt skips the material.
        private const string OpaqueAlphaTextureNotFound = "alpha texture not found";

        private static readonly Regex OpaqueAlphaShaderNode = new(
            @"<shader>(?<before>\s*)(?<name>[^<]*?)(?<after>\s*)</shader>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex OpaqueAlphaSuffix = new(
            @"_alpha(?=\.xml\.shader$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private sealed record OpaqueAlphaEntry(
            string Path,
            string OldShader,
            string NewShader,
            string AlphaTexture,
            int MinAlpha,
            bool Generated);

        private sealed class OpaqueAlphaResult
        {
            public int MaterialsScanned { get; set; }
            public int AlphaShaderMaterials { get; set; }
            public int TexturesMeasured { get; set; }
            public TimeSpan Elapsed { get; set; }
            public string? Failure { get; set; }
            public string? ChannelTableError { get; set; }
            public List<OpaqueAlphaEntry> Switched { get; } = [];
            public Dictionary<string, int> Skipped { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, List<string>> SkippedExamples { get; } = new(StringComparer.Ordinal);
        }

        private void SwitchOpaqueAlphaShaders(
            BatchState state,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var result = new OpaqueAlphaResult();
            state.OpaqueAlpha = result;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                ReportProgress(progress, "Switching opaque alpha shaders");
                RunOpaqueAlphaSwitch(state, result, cancellationToken, progress);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Each rewrite was verified before it was written; the rest stays as it is.
                result.Failure = $"{ex.GetType().Name}: {ex.Message}";
            }

            result.Elapsed = stopwatch.Elapsed;
        }

        private static void SkipOpaqueAlpha(OpaqueAlphaResult result, string reason, string path)
        {
            result.Skipped[reason] = result.Skipped.GetValueOrDefault(reason) + 1;
            if (!result.SkippedExamples.TryGetValue(reason, out var examples))
            {
                examples = [];
                result.SkippedExamples[reason] = examples;
            }

            if (examples.Count < 3)
                examples.Add(path);
        }

        // Minimum alpha over every texel of every mip level, read from the compressed blocks with the
        // exact BC1/BC2/BC3 decoder of texture compaction (BC1 punch-through counts as alpha 0). Stops
        // at the first texel below `stopBelow`. Problem is set when the format cannot be decoded exactly.
        private static (int MinAlpha, string? Problem) MeasureMinimumAlpha(byte[] dds, int stopBelow)
        {
            if (!TryReadDdsLayout(
                    dds.AsSpan(0, Math.Min(dds.Length, TextureDownscaleHeaderPeekBytes)).ToArray(),
                    out var layout))
            {
                return (-1, "unreadable DDS header");
            }

            if (layout.Format is not ("BC1" or "BC2" or "BC3"))
                return (-1, $"{layout.Format} is not decoded exactly (BC1, BC2 and BC3 only)");
            if (dds.LongLength < layout.HeaderSize + layout.ChainBytes)
                return (-1, "truncated DDS");

            var minimum = 255;
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
                        var block = dds.AsSpan(
                            checked((int)(offset + ((long)by * blocksWide + bx) * bytesPerBlock)), bytesPerBlock);
                        var columns = Math.Min(4, width - bx * 4);
                        var rows = Math.Min(4, height - by * 4);
                        var full = columns == 4 && rows == 4;
                        if (full && haveLast && block.SequenceEqual(lastOpaque))
                            continue;

                        DecodeCompactionBlock(layout.Format, block, texels);
                        var blockMinimum = 255;
                        for (var y = 0; y < rows; y++)
                        {
                            for (var x = 0; x < columns; x++)
                                blockMinimum = Math.Min(blockMinimum, (int)(texels[y * 4 + x] >> 24));
                        }

                        if (blockMinimum < minimum)
                        {
                            minimum = blockMinimum;
                            if (minimum < stopBelow)
                                return (minimum, null);
                        }

                        if (full && blockMinimum >= stopBelow)
                        {
                            block.CopyTo(lastOpaque);
                            haveLast = true;
                        }
                    }
                }
            }

            return (minimum, null);
        }

        // The file the game would read for a texture of a material of the output: the output's own,
        // else the resolved vanilla/provider file (unless another enabled mod replaces that vanilla path).
        private static PackFile? ResolveOpaqueAlphaTexture(BatchState state, string texture, out string? blocked)
        {
            blocked = null;
            var outputFile = state.Output.FindFile(texture);
            if (outputFile != null)
            {
                if (state.Source.ContainsFile(texture) && state.Consumers?.TryBlock(texture, "opaque alpha shader") == true)
                    blocked = "alpha texture is used or overridden by another mod";
                return outputFile;
            }

            if (state.Consumers?.TryBlockVanillaReplaced(texture, "opaque alpha shader") == true)
            {
                blocked = "alpha texture path is replaced by another mod";
                return null;
            }

            return FindForReadStatic(state, texture);
        }

        private static string? RewriteOpaqueAlphaShader(
            byte[] material,
            string oldShader,
            out string newShaderText,
            out byte[] rewritten)
        {
            newShaderText = string.Empty;
            rewritten = [];
            var text = Encoding.UTF8.GetString(material);
            var matches = OpaqueAlphaShaderNode.Matches(text);
            if (matches.Count != 1)
                return "the shader node is not a single plain element";

            var match = matches[0];
            var name = match.Groups["name"].Value;
            if (!Normalize(name).Equals(oldShader, StringComparison.Ordinal))
                return "the shader node text differs from the parsed shader";

            newShaderText = OpaqueAlphaSuffix.Replace(name, string.Empty);
            if (newShaderText.Equals(name, StringComparison.Ordinal))
                return "the shader has no _alpha suffix";

            rewritten = Encoding.UTF8.GetBytes(
                text[..match.Groups["name"].Index] + newShaderText + text[(match.Groups["name"].Index + name.Length)..]);
            return null;
        }

        private void RunOpaqueAlphaSwitch(
            BatchState state,
            OpaqueAlphaResult result,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var (table, tableError) = Wh3ShaderChannels.Value;
            result.ChannelTableError = tableError;
            var vanillaKnown = GetGameplayTraversalContainers(state).Any(container =>
                container.IsCaPackFile && !ReferenceEquals(container, state.Source));
            var generatedMaterials = new HashSet<string>(
                state.GeneratedMaterialPaths.Select(Normalize),
                StringComparer.OrdinalIgnoreCase);
            var materialPaths = state.Output.GetAllFiles().Keys
                .Select(Normalize)
                .Where(path => path.EndsWith(".xml.material", StringComparison.Ordinal) &&
                               (generatedMaterials.Contains(path) || state.Source.ContainsFile(path)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
            var observedShaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Minimum alpha per texture, measured once.
            var measured = new Dictionary<string, (int MinAlpha, string? Problem)>(StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < materialPaths.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = materialPaths[index];
                if (index % 100 == 0)
                    ReportProgress(progress, "Switching opaque alpha shaders", index + 1, materialPaths.Count, path);
                result.MaterialsScanned++;

                var generated = generatedMaterials.Contains(path);
                var material = new AssetCheckMaterial(path, state.Source.ContainsFile(path));
                ReadAssetCheckMaterial(state, material);
                if (material.Document == null)
                {
                    // An unreadable material is not an alpha candidate that can be told apart.
                    continue;
                }

                AssessAssetCheckMaterialShader(material, observedShaders);
                if (material.Status == AssetCheckAlphaStatus.NotAlphaShader)
                    continue;

                result.AlphaShaderMaterials++;
                if (material.Status == AssetCheckAlphaStatus.NotAssessed)
                {
                    SkipOpaqueAlpha(result, "not a plain character alpha shader: " + material.NotAssessedReason, path);
                    continue;
                }

                if (material.Status == AssetCheckAlphaStatus.NoAlphaTexture)
                {
                    SkipOpaqueAlpha(result, "no alpha source texture", path);
                    continue;
                }

                if (state.Consumers?.TryBlock(path, "opaque alpha shader") == true)
                {
                    SkipOpaqueAlpha(result, "another mod uses or overrides the material", path);
                    continue;
                }

                if (!generated)
                {
                    if (!vanillaKnown)
                    {
                        SkipOpaqueAlpha(result, "vanilla game data is not loaded (cannot rule out a vanilla override)", path);
                        continue;
                    }

                    if (OverridesVanillaPath(state, path))
                    {
                        SkipOpaqueAlpha(result, "overrides a vanilla material", path);
                        continue;
                    }
                }

                if (table == null)
                {
                    SkipOpaqueAlpha(result, "the shader channel table is unavailable", path);
                    continue;
                }

                var twinName = GetCompactionShaderName(material.TwinShader);
                if (twinName == null || !table.Shaders.ContainsKey(twinName))
                {
                    SkipOpaqueAlpha(result, "the non-alpha twin shader is not in the shader channel table", path);
                    continue;
                }

                var texture = material.AlphaTexturePath;
                var alphaSlots = AssetCheckAlphaSourceSlots
                    .Where(slot => material.Textures.TryGetValue(slot, out var slotTexture) &&
                                   slotTexture.Equals(texture, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var readsAlpha = alphaSlots.FirstOrDefault(slot => GetAlphaReadDoubt(table, twinName, slot) != null);
                if (alphaSlots.Count == 0 || readsAlpha != null)
                {
                    SkipOpaqueAlpha(result, "the twin shader may read the alpha of the texture slot", path);
                    continue;
                }

                if (!measured.TryGetValue(texture, out var alpha))
                {
                    var file = ResolveOpaqueAlphaTexture(state, texture, out var blocked);
                    if (blocked != null)
                    {
                        alpha = (-1, blocked);
                    }
                    else if (file == null)
                    {
                        alpha = (-1, OpaqueAlphaTextureNotFound);
                    }
                    else
                    {
                        try
                        {
                            alpha = MeasureMinimumAlpha(file.DataSource.ReadData(), AssetChecksOpaqueMinimumAlpha);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            alpha = (-1, $"alpha texture could not be read ({ex.GetType().Name})");
                        }

                        result.TexturesMeasured++;
                    }

                    measured[texture] = alpha;
                }

                if (alpha.Problem != null)
                {
                    SkipOpaqueAlpha(result, alpha.Problem, path);
                    continue;
                }

                if (alpha.MinAlpha < AssetChecksOpaqueMinimumAlpha)
                {
                    SkipOpaqueAlpha(result, "the alpha texture is not opaque (alpha below " + AssetChecksOpaqueMinimumAlpha + ")", path);
                    continue;
                }

                var original = state.Output.FindFile(path)?.DataSource.ReadData();
                if (original == null)
                {
                    SkipOpaqueAlpha(result, "material file not readable", path);
                    continue;
                }

                var problem = RewriteOpaqueAlphaShader(original, material.Shader, out var newShaderText, out var rewritten);
                if (problem != null)
                {
                    SkipOpaqueAlpha(result, "the shader text could not be rewritten in place: " + problem, path);
                    continue;
                }

                WriteFile(state.Output, path, rewritten);
                state.MaterialDocuments.Remove(path);
                result.Switched.Add(new OpaqueAlphaEntry(
                    path,
                    material.Shader,
                    Normalize(newShaderText),
                    texture,
                    alpha.MinAlpha,
                    generated));
            }
        }

        // Each switched material points to the twin shader (known to the channel table) and its alpha
        // texture, as the output now holds it, is still opaque at every level.
        private static void ValidateOpaqueAlpha(
            BatchState state,
            List<string> errors,
            CancellationToken cancellationToken)
        {
            var result = state.OpaqueAlpha;
            if (result == null || result.Switched.Count == 0)
                return;

            var before = errors.Count;
            var (table, _) = Wh3ShaderChannels.Value;
            foreach (var entry in result.Switched)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = state.Output.FindFile(entry.Path);
                if (file == null)
                {
                    errors.Add($"Switched material is missing from output: {entry.Path}");
                    continue;
                }

                try
                {
                    var document = LoadXml(file);
                    var shader = Normalize(document.SelectSingleNode("/material/shader")?.InnerText);
                    if (!shader.Equals(entry.NewShader, StringComparison.Ordinal) ||
                        shader.Contains("alpha", StringComparison.Ordinal))
                    {
                        errors.Add($"Switched material {entry.Path} has shader '{shader}', expected '{entry.NewShader}'");
                    }

                    var twinName = GetCompactionShaderName(shader);
                    if (table == null || twinName == null || !table.Shaders.ContainsKey(twinName))
                        errors.Add($"Switched material {entry.Path} points to a shader the channel table does not know: {shader}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"Switched material {entry.Path} could not be read ({ex.GetType().Name})");
                    continue;
                }

                var texture = ResolveOpaqueAlphaTexture(state, entry.AlphaTexture, out _);
                if (texture == null)
                {
                    errors.Add($"Alpha texture of switched material {entry.Path} is missing: {entry.AlphaTexture}");
                    continue;
                }

                var (minimum, problem) = MeasureMinimumAlpha(texture.DataSource.ReadData(), AssetChecksOpaqueMinimumAlpha);
                if (problem != null || minimum < AssetChecksOpaqueMinimumAlpha)
                {
                    errors.Add(
                        $"Alpha texture {entry.AlphaTexture} of switched material {entry.Path} is no longer opaque " +
                        (problem ?? $"(alpha {minimum})"));
                }
            }

            if (errors.Count == before)
            {
                state.ValidationMessages.Add(
                    $"PASS: {result.Switched.Count:N0} material(s) moved to the non-alpha twin shader; each twin is a known " +
                    "shader and each alpha texture is still opaque (alpha >= " + AssetChecksOpaqueMinimumAlpha + ") at every mip level.");
            }
        }

        private static void AppendOpaqueAlphaSection(StringBuilder sb, BatchState state)
        {
            var result = state.OpaqueAlpha;
            if (result == null)
                return;

            sb.AppendLine("Opaque alpha shaders");
            sb.AppendLine("--------------------");
            if (result.Failure != null)
                sb.AppendLine($"Stopped early ({result.Failure}); the materials switched before that were verified one by one.");
            if (result.ChannelTableError != null)
                sb.AppendLine($"Shader channel table: {result.ChannelTableError}");
            sb.AppendLine("Mode: APPLIED (opaque-alpha character materials moved to the twin shader without _alpha)");
            sb.AppendLine(
                $"Materials examined: {result.MaterialsScanned:N0}, on an alpha shader: {result.AlphaShaderMaterials:N0}, " +
                $"switched: {result.Switched.Count:N0}, alpha textures measured: {result.TexturesMeasured:N0}");
            sb.AppendLine("Switched materials (path: old shader -> new shader, alpha texture, minimum alpha):");
            foreach (var entry in result.Switched.OrderBy(entry => entry.Path, StringComparer.Ordinal))
            {
                sb.AppendLine(
                    $"  {entry.Path}: {entry.OldShader} -> {entry.NewShader}, {entry.AlphaTexture}, min alpha {entry.MinAlpha}" +
                    (entry.Generated ? " (generated in this run)" : string.Empty));
            }

            if (result.Switched.Count == 0)
                sb.AppendLine("  (none)");

            sb.AppendLine($"Skipped alpha-shader materials: {result.Skipped.Values.Sum():N0}");
            foreach (var (reason, count) in result.Skipped.OrderByDescending(pair => pair.Value)
                         .ThenBy(pair => pair.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"  {reason}: {count:N0} (e.g. {string.Join(", ", result.SkippedExamples[reason])})");
            }

            var textures = result.Switched.Select(entry => entry.AlphaTexture)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var dropped = (state.TextureCompaction?.Compacted ?? [])
                .Where(entry => entry.AlphaDropped && textures.Contains(Normalize(entry.Path)))
                .ToList();
            sb.AppendLine(
                $"Alpha textures of switched materials that compaction then dropped to BC1: {dropped.Count:N0}" +
                (dropped.Count == 0
                    ? string.Empty
                    : $", {FormatMiB(dropped.Sum(entry => entry.BytesBefore - entry.BytesAfter))} saved"));
            sb.AppendLine();
        }
    }
}
