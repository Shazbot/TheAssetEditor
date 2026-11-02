using System.Xml;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.Vertex;

namespace Editors.PackOptimizer.Core
{
    public sealed partial class PackTextureAtlasBatchService
    {
        // REPORT-ONLY "no-atlas rewrite" tiers of the ideal cross-rigid merge
        // analysis. Each tier starts from T0 (today's strict identity: RMV
        // identity + full material identity, every texture path still has to
        // match) and applies cheap rewrites that need no texture atlas:
        //   W  weighted2 / weighted4 unified (same transform as T3: shader
        //      "weighted2" -> "weighted4", RMV Cinematic vertices treated as
        //      Weighted).
        //   A  alpha variant unified (same transform as T4: "_alpha" shader
        //      suffix and the RMV render flag dropped).
        // and their combination WA. (A duplicate-texture tier D was measured
        // and removed: it gained nothing, see CROSS_RIGID_FINDINGS.md.) Pooling, payload accounting
        // and the greedy selection are the existing machinery; nothing here
        // feeds selection, gating or emission.

        private const int CrossRigidNoAtlasFirstTier = 5;
        private const int CrossRigidNoAtlasWeighted = 1;
        private const int CrossRigidNoAtlasAlpha = 2;

        // Flags of tiers 5..7: W, A, WA.
        private static readonly int[] CrossRigidNoAtlasTierFlagsByTier =
            [1, 2, 3];
        private static readonly string[] CrossRigidNoAtlasTierNames =
            ["W", "A", "WA"];
        private static readonly long[] CrossRigidNoAtlasGrowthCapsMiB =
            [64, 128, 256];
        private const string CrossRigidNoAtlasAlphaSuffix =
            "_alpha.xml.shader";

        private static readonly Lazy<VertexFactory> CrossRigidNoAtlasVertexFactory =
            new(() => VertexFactory.Create());

        private static int GetCrossRigidNoAtlasTierFlags(int tier)
            => tier >= CrossRigidNoAtlasFirstTier
                ? CrossRigidNoAtlasTierFlagsByTier[
                    tier - CrossRigidNoAtlasFirstTier]
                : 0;

        // Tiers reported by the payload tables: T0 (reference) and 5..7.
        private static IEnumerable<int> GetCrossRigidNoAtlasReportedTiers()
        {
            yield return 0;
            for (var tier = CrossRigidNoAtlasFirstTier;
                 tier < CrossRigidIdealMergeTierCount;
                 tier++)
            {
                yield return tier;
            }
        }

        private static string GetCrossRigidNoAtlasTierName(int tier)
            => tier == 0
                ? "T0"
                : CrossRigidNoAtlasTierNames[tier - CrossRigidNoAtlasFirstTier];

        // Identities of tiers 5..7 for one part.
        private static string[] GetCrossRigidNoAtlasPartIdentities(
            BatchState state,
            RmvModel model,
            string strictRmvIdentity,
            string weightedUnifiedRmvIdentity,
            string weightedAlphaUnifiedRmvIdentity,
            string normalizedMaterialPath)
        {
            string? alphaOnlyRmvIdentity = null;
            var identities = new string[CrossRigidNoAtlasTierFlagsByTier.Length];
            for (var index = 0; index < identities.Length; index++)
            {
                var flags = CrossRigidNoAtlasTierFlagsByTier[index];
                var weighted = (flags & CrossRigidNoAtlasWeighted) != 0;
                var alpha = (flags & CrossRigidNoAtlasAlpha) != 0;
                string rmvIdentity;
                if (weighted)
                {
                    rmvIdentity = alpha
                        ? weightedAlphaUnifiedRmvIdentity
                        : weightedUnifiedRmvIdentity;
                }
                else if (alpha)
                {
                    alphaOnlyRmvIdentity ??= GetCrossRigidNoAtlasRmvIdentity(
                        model,
                        unifyWeighted: false,
                        ignoreRenderFlag: true);
                    rmvIdentity = alphaOnlyRmvIdentity;
                }
                else
                {
                    rmvIdentity = strictRmvIdentity;
                }

                identities[index] = string.Join(
                    "\u001e",
                    rmvIdentity,
                    GetCrossRigidIdealMergeMaterialIdentity(
                        state,
                        normalizedMaterialPath,
                        CrossRigidNoAtlasFirstTier + index));
            }

            return identities;
        }

        // Pure material XML -> identity function of the no-atlas tiers: the
        // strict rendering identity (name dropped, paths normalized) with the
        // shader rewritten (flags W / A).
        private static string BuildCrossRigidNoAtlasMaterialIdentityCore(
            XmlDocument material,
            int flags)
        {
            var clone = new XmlDocument();
            clone.LoadXml(material.OuterXml);

            if ((flags & (CrossRigidNoAtlasWeighted | CrossRigidNoAtlasAlpha)) != 0)
            {
                var shaderNode = clone.SelectSingleNode("/material/shader");
                if (shaderNode != null)
                {
                    shaderNode.InnerText = NormalizeCrossRigidNoAtlasShader(
                        shaderNode.InnerText,
                        (flags & CrossRigidNoAtlasWeighted) != 0,
                        (flags & CrossRigidNoAtlasAlpha) != 0);
                }
            }

            return GetMaterialRenderingIdentity(clone);
        }

        private static string NormalizeCrossRigidNoAtlasShader(
            string? shader,
            bool weighted,
            bool alpha)
        {
            var normalized = Normalize(shader);
            if (weighted)
            {
                normalized = normalized.Replace(
                    "weighted2",
                    "weighted4",
                    StringComparison.Ordinal);
            }

            if (alpha &&
                normalized.EndsWith(
                    CrossRigidNoAtlasAlphaSuffix,
                    StringComparison.Ordinal))
            {
                normalized = normalized[..^CrossRigidNoAtlasAlphaSuffix.Length] +
                             ".xml.shader";
            }

            return normalized;
        }

        // Normalized shader of a material ("" when unreadable).
        private static string GetCrossRigidNoAtlasMaterialShader(
            BatchState state,
            string normalizedMaterialPath)
        {
            var cache = state.CrossRigidNoAtlasMaterialShaders;
            if (cache.TryGetValue(normalizedMaterialPath, out var cached))
                return cached;

            string shader;
            try
            {
                shader = Normalize(
                    GetMaterialDocument(state, normalizedMaterialPath)
                        .SelectSingleNode("/material/shader")?.InnerText);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                shader = string.Empty;
            }

            cache[normalizedMaterialPath] = shader;
            return shader;
        }

        // Extra vertex bytes of a weighted2 (Weighted) part when stored in
        // the weighted4 (Cinematic) layout, with the RMV vertex sizes of the
        // rigid's version; 0 for any other format or unknown sizes.
        private static long GetCrossRigidNoAtlasWeighted4ExtraBytes(
            RmvModel model,
            RmvVersionEnum version)
        {
            if (model.Material.BinaryVertexFormat != VertexFormat.Weighted)
                return 0;

            var factory = CrossRigidNoAtlasVertexFactory.Value;
            if (!factory.IsKnownVertex(VertexFormat.Weighted) ||
                !factory.IsKnownVertex(VertexFormat.Cinematic))
            {
                return 0;
            }

            var delta = (long)factory.GetVertexSize(VertexFormat.Cinematic, version) -
                        factory.GetVertexSize(VertexFormat.Weighted, version);
            return Math.Max(0, delta) * model.Mesh.VertexList.Length;
        }

        // Extra bytes if EVERY weighted2 part of every LOD row of the rigid
        // were stored as weighted4.
        private static long GetCrossRigidNoAtlasComponentWeighted2ExtraBytes(
            BatchState state,
            CrossRigidAnalysisComponent component)
        {
            var cache = state.CrossRigidIdealMergeAccounting
                .WsModelWeighted2ExtraBytes;
            if (cache.TryGetValue(component.WsModelPath, out var cached))
                return cached;

            long bytes = 0;
            foreach (var row in component.Rigid.ModelList)
            {
                foreach (var model in row)
                {
                    bytes += GetCrossRigidNoAtlasWeighted4ExtraBytes(
                        model,
                        component.Rigid.Header.Version);
                }
            }

            cache[component.WsModelPath] = bytes;
            return bytes;
        }

        // ---- Per payload data ----

        // Per tier arrays are indexed by tier (null for tiers not reported).
        private sealed class CrossRigidNoAtlasPayloadData
        {
            public CrossRigidNoAtlasPayloadData(int lodCount)
            {
                foreach (var tier in GetCrossRigidNoAtlasReportedTiers())
                {
                    SavedByLod[tier] = new int[lodCount];
                    SavingByLod[tier] = new double[lodCount];
                    var flags = GetCrossRigidNoAtlasTierFlags(tier);
                    if ((flags & CrossRigidNoAtlasWeighted) != 0)
                    {
                        ConvertedPartKeys[tier] = new HashSet<string>(
                            StringComparer.Ordinal);
                        ConvertedMaterials[tier] = new HashSet<string>(
                            StringComparer.OrdinalIgnoreCase);
                    }

                    if ((flags & CrossRigidNoAtlasAlpha) != 0)
                    {
                        OpaqueByShaderLod[tier] = new Dictionary<string, int>[lodCount];
                        for (var lod = 0; lod < lodCount; lod++)
                            OpaqueByShaderLod[tier]![lod] = new(StringComparer.Ordinal);
                        OpaqueWeightedByShader[tier] = new(StringComparer.Ordinal);
                    }
                }
            }

            public int[]?[] SavedByLod { get; } =
                new int[CrossRigidIdealMergeTierCount][];
            // Army-weighted like CrossRigidIdealMergePayload.Saving, summed
            // over every configuration (and VMD root) using the payload.
            public double[]?[] SavingByLod { get; } =
                new double[CrossRigidIdealMergeTierCount][];
            public double Saving(int tier) => SavingByLod[tier]!.Sum();

            // W: extra bytes of the weighted2 parts that sit in a pooled
            // group with weighted4 parts (each source part counted once over
            // all LODs), the distinct materials of those parts, and the extra
            // bytes if every weighted2 part of the payload were converted.
            public long[] ConvertedExtraBytes { get; } =
                new long[CrossRigidIdealMergeTierCount];
            public HashSet<string>?[] ConvertedPartKeys { get; } =
                new HashSet<string>[CrossRigidIdealMergeTierCount];
            public HashSet<string>?[] ConvertedMaterials { get; } =
                new HashSet<string>[CrossRigidIdealMergeTierCount];
            public long AllWeighted2ExtraBytes { get; set; }

            // A: opaque-shader parts that sit in a pooled group with an
            // _alpha shader part, per scenario LOD and shader, and the
            // army-weighted instances per shader.
            public Dictionary<string, int>[]?[] OpaqueByShaderLod { get; } =
                new Dictionary<string, int>[CrossRigidIdealMergeTierCount][];
            public Dictionary<string, double>?[] OpaqueWeightedByShader { get; } =
                new Dictionary<string, double>[CrossRigidIdealMergeTierCount];
        }

        private static bool IsCrossRigidNoAtlasAlphaShader(string shader)
            => shader.EndsWith(
                CrossRigidNoAtlasAlphaSuffix,
                StringComparison.Ordinal);

        // Called per scenario LOD of a payload with the pooled parts of all
        // its components at that LOD and the per-tier pooled draws.
        private static void RecordCrossRigidNoAtlasLodData(
            CrossRigidNoAtlasPayloadData data,
            int lodIndex,
            int perInstanceDraws,
            int[] tierDraws,
            IReadOnlyList<CrossRigidIdealMergePart> parts)
        {
            foreach (var tier in GetCrossRigidNoAtlasReportedTiers())
            {
                data.SavedByLod[tier]![lodIndex] =
                    perInstanceDraws - tierDraws[tier];
                var flags = GetCrossRigidNoAtlasTierFlags(tier);
                if ((flags & (CrossRigidNoAtlasWeighted |
                              CrossRigidNoAtlasAlpha)) == 0)
                {
                    continue;
                }

                foreach (var group in parts
                             .Where(part => part.MaterialPath.Length != 0)
                             .GroupBy(part => part.IdentityIds[tier]))
                {
                    var members = group.ToArray();
                    if ((flags & CrossRigidNoAtlasWeighted) != 0 &&
                        members.Any(part => part.IsWeighted2) &&
                        members.Any(part => !part.IsWeighted2))
                    {
                        foreach (var part in members.Where(part => part.IsWeighted2))
                        {
                            if (!data.ConvertedPartKeys[tier]!.Add(part.PartKey))
                                continue;

                            data.ConvertedExtraBytes[tier] +=
                                part.Weighted4ExtraBytes;
                            data.ConvertedMaterials[tier]!.Add(part.MaterialPath);
                        }
                    }

                    if ((flags & CrossRigidNoAtlasAlpha) != 0 &&
                        members.Any(part => IsCrossRigidNoAtlasAlphaShader(part.Shader)))
                    {
                        var byShader = data.OpaqueByShaderLod[tier]![lodIndex];
                        foreach (var part in members.Where(part =>
                                     part.Shader.Length != 0 &&
                                     !IsCrossRigidNoAtlasAlphaShader(part.Shader)))
                        {
                            byShader[part.Shader] =
                                byShader.GetValueOrDefault(part.Shader) + 1;
                        }
                    }
                }
            }
        }

        // One configuration of the payload, probability p, per-LOD army
        // weights of draws 1.0.
        private static void AccumulateCrossRigidNoAtlasSaving(
            CrossRigidIdealMergePayload payload,
            double probability,
            IReadOnlyList<double> unitWeights)
        {
            var data = payload.NoAtlas;
            foreach (var tier in GetCrossRigidNoAtlasReportedTiers())
            {
                var saved = data.SavedByLod[tier]!;
                var saving = data.SavingByLod[tier]!;
                for (var lod = 0; lod < unitWeights.Count && lod < saved.Length; lod++)
                    saving[lod] += probability * unitWeights[lod] * saved[lod];

                if (data.OpaqueByShaderLod[tier] is not { } opaque)
                    continue;

                var weighted = data.OpaqueWeightedByShader[tier]!;
                for (var lod = 0; lod < unitWeights.Count && lod < opaque.Length; lod++)
                {
                    foreach (var (shader, count) in opaque[lod])
                    {
                        weighted[shader] =
                            weighted.GetValueOrDefault(shader) +
                            probability * unitWeights[lod] * count;
                    }
                }
            }
        }

        // ---- Self-check ----

        // Roots (with the full tier vector) violating: tiers 5..7 <= T0,
        // WA <= W and A, T3 <= W and T4 <= WA.
        private static int CountCrossRigidNoAtlasInvariantViolations(
            BatchState state)
        {
            // Tier numbers of W, A, WA.
            const int w = 5, a = 6, wa = 7;
            (int Smaller, int Larger)[] relations =
            [
                (w, 0), (a, 0), (wa, w), (wa, a), (3, w), (4, wa),
            ];
            var violations = 0;
            foreach (var tiers in
                     state.CrossRigidIdealMergeExpectedDrawsByVmd.Values)
            {
                if (tiers.Length != CrossRigidIdealMergeTierCount)
                    continue;

                var violated = tiers
                    .Skip(CrossRigidNoAtlasFirstTier)
                    .Any(value => value < -CrossRigidIdealMergeSelfCheckTolerance);
                foreach (var (smaller, larger) in relations)
                {
                    if (tiers[smaller] > tiers[larger] +
                        CrossRigidIdealMergeSelfCheckTolerance)
                    {
                        violated = true;
                    }
                }

                if (violated)
                    violations++;
            }

            return violations;
        }

        // ---- Report ----

        private static List<string> BuildCrossRigidNoAtlasReportLines(
            BatchState state,
            double baselineTotal)
        {
            var accounting = state.CrossRigidIdealMergeAccounting;
            var lines = new List<string>
            {
                "  No-atlas rewrite tiers (report only; T0 plus a cheap " +
                "rewrite, every texture path still has to match, no atlas " +
                "and no texture cost; greedy on full-rigid geometry, same " +
                "per-payload ceiling as above):",
            };
            double Share(double value)
                => baselineTotal > AtlasValueGateExpectedDrawEpsilon
                    ? value / baselineTotal
                    : 0;

            var payloads = accounting.Payloads.Values
                .Where(payload => payload.NoAtlas != null)
                .ToArray();
            var ceiling = CrossRigidPerPayloadCostCeilingMultiplier *
                          MaxCrossRigidGeneratedGeometryBytesPerExpectedArmyDraw;
            var shaderTable = Wh3ShaderChannels.Value.Table;
            foreach (var tier in GetCrossRigidNoAtlasReportedTiers())
            {
                var flags = GetCrossRigidNoAtlasTierFlags(tier);
                var name = GetCrossRigidNoAtlasTierName(tier);
                var candidates = payloads
                    .Select(payload => (
                        payload.Key,
                        Saving: payload.NoAtlas.Saving(tier),
                        Bytes: payload.GeometryBytes))
                    .Where(item => item.Saving > AtlasValueGateExpectedDrawEpsilon)
                    .ToArray();
                lines.Add(
                    $"    {name}: {candidates.Length:N0} payload candidate(s) " +
                    $"with saving, Σ {candidates.Sum(item => item.Saving):0.###}");
                foreach (var capMiB in CrossRigidNoAtlasGrowthCapsMiB)
                {
                    var accepted = SelectCrossRigidIdealMergePayloadKeysGreedy(
                        candidates,
                        ceiling,
                        capMiB * 1024 * 1024);
                    var acceptedPayloads = accepted
                        .Select(item => accounting.Payloads[item.Key])
                        .ToArray();
                    var saving = accepted.Sum(item => item.Saving);
                    var bytes = accepted.Sum(item => item.Bytes);
                    var t0Saving = acceptedPayloads.Sum(
                        payload => payload.NoAtlas.Saving(0));
                    var lodIndices = acceptedPayloads.Length != 0
                        ? acceptedPayloads[0].ScenarioLodIndices
                        : payloads.Length != 0
                            ? payloads[0].ScenarioLodIndices
                            : [];
                    var byLod = SumCrossRigidSavingByLod(
                        lodIndices.Length,
                        acceptedPayloads.Select(p => p.NoAtlas.SavingByLod[tier]!));
                    var t0ByLod = SumCrossRigidSavingByLod(
                        lodIndices.Length,
                        acceptedPayloads.Select(p => p.NoAtlas.SavingByLod[0]!));
                    string Part(Func<int, double> value)
                        => string.Join(" / ", lodIndices.Select((lod, index) =>
                            $"L{lod} {value(index):0.###}"));
                    lines.Add(
                        $"      {name} cap {capMiB:N0} MiB: {accepted.Count:N0} " +
                        $"accepted, saved {saving:0.###} draws " +
                        $"({Share(saving):P1}), above T0 " +
                        $"{saving - t0Saving:0.###}, geometry {FormatMiB(bytes)}" +
                        (saving > AtlasValueGateExpectedDrawEpsilon
                            ? $" ({FormatMiB(bytes / saving, 2)} per draw)"
                            : string.Empty) +
                        $"; LOD split {Part(i => byLod[i])}; above T0 " +
                        Part(i => Math.Max(0, byLod[i] - t0ByLod[i])));

                    if ((flags & CrossRigidNoAtlasWeighted) != 0)
                    {
                        var materials = new HashSet<string>(
                            StringComparer.OrdinalIgnoreCase);
                        foreach (var payload in acceptedPayloads)
                            materials.UnionWith(payload.NoAtlas.ConvertedMaterials[tier]!);
                        var partCount = acceptedPayloads.Sum(
                            payload => payload.NoAtlas.ConvertedPartKeys[tier]!.Count);
                        lines.Add(
                            $"        {name} weighted2->weighted4 cost: extra vertex " +
                            $"bytes {FormatMiB(acceptedPayloads.Sum(p => p.NoAtlas.ConvertedExtraBytes[tier]), 2)} " +
                            $"for the {partCount:N0} weighted2 part(s) pooled with weighted4 " +
                            $"(all weighted2 parts of these payloads: " +
                            $"{FormatMiB(acceptedPayloads.Sum(p => p.NoAtlas.AllWeighted2ExtraBytes), 2)}); " +
                            $"distinct materials converted {materials.Count:N0}");
                    }

                    if ((flags & CrossRigidNoAtlasAlpha) != 0)
                    {
                        var byShader = new Dictionary<string, double>(
                            StringComparer.Ordinal);
                        foreach (var payload in acceptedPayloads)
                        {
                            foreach (var (shader, weighted) in
                                     payload.NoAtlas.OpaqueWeightedByShader[tier]!)
                            {
                                byShader[shader] =
                                    byShader.GetValueOrDefault(shader) + weighted;
                            }
                        }

                        var missing = byShader
                            .Where(entry => !CrossRigidNoAtlasAlphaTwinExists(
                                shaderTable,
                                entry.Key,
                                (flags & CrossRigidNoAtlasWeighted) != 0))
                            .OrderByDescending(entry => entry.Value)
                            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                            .ToArray();
                        var total = byShader.Values.Sum();
                        lines.Add(
                            $"        {name} alpha cost: opaque part instances rendered " +
                            $"alpha-tested after merge {total:0.###} " +
                            $"({Share(total):P1} of baseline draws, army-weighted); " +
                            $"{byShader.Count:N0} opaque shader(s) involved, " +
                            (shaderTable == null
                                ? "shader table unavailable (twins not checked)"
                                : $"{missing.Length:N0} without an _alpha twin in the " +
                                  $"vanilla shader table ({missing.Sum(e => e.Value):0.###} instances" +
                                  (missing.Length == 0
                                      ? string.Empty
                                      : ": " + string.Join(", ", missing.Take(3).Select(e => e.Key)) +
                                        (missing.Length > 3 ? ", ..." : string.Empty)) +
                                  ")"));
                    }
                }
            }

            return lines;
        }

        // Same table lookup SwitchOpaqueAlphaShaders uses for twins
        // (Wh3ShaderChannels, GetCompactionShaderName), in the other
        // direction: the _alpha shader a merged opaque part has to use.
        private static bool CrossRigidNoAtlasAlphaTwinExists(
            Wh3ShaderChannelTable? table,
            string opaqueShader,
            bool weightedUnified)
        {
            if (table == null)
                return false;

            var shader = weightedUnified
                ? opaqueShader.Replace(
                    "weighted2",
                    "weighted4",
                    StringComparison.Ordinal)
                : opaqueShader;
            if (!shader.EndsWith(".xml.shader", StringComparison.Ordinal))
                return false;

            var twin = shader[..^".xml.shader".Length] +
                       CrossRigidNoAtlasAlphaSuffix;
            var name = GetCompactionShaderName(twin);
            return name != null && table.Shaders.ContainsKey(name);
        }

    }
}
