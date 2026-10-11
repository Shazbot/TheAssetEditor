﻿using System.Diagnostics;
using System.Xml;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.MaterialHeaders;

namespace Editors.KitbasherEditor.Services
{
    public sealed partial class PackTextureAtlasBatchService
    {
        // REPORT-ONLY diagnostic. Nothing in this file feeds selection,
        // gating, emission or atlas logic: it reads the same expansion /
        // component data as the baseline expected-draw metric and only fills
        // the CrossRigidIdealMerge* fields of BatchState for the report.
        //
        // For every root VMD, the baseline's expected battle draws are
        // recomputed under several "ideal merge" tiers. Within ONE visual
        // configuration of ONE attachment context, all parts of all its
        // instances are pooled and merged by a tier-specific identity (then
        // vertex-limited), instead of per instance. Each tier is strictly
        // more permissive than the previous one:
        //   T0 strict: GetCrossRigidSourcePartMergeIdentity (today's).
        //   T1 shared atlas: RMV identity + material identity with the four
        //      atlasable texture slots removed.
        //   T2 T1 + the legacy s_* texture slots removed.
        //   T3 T2 + weighted2/weighted4 unified (approximation).
        //   T4 T3 + _alpha shader variants unified (approximation).
        private const int CrossRigidIdealMergeTierCount = 5;
        private const double CrossRigidIdealMergeSelfCheckTolerance = 1e-9;

        private static readonly string[] CrossRigidIdealMergeTierLabels =
        [
            "T0 strict (today's identity)",
            "T1 shared atlas",
            "T2 shared atlas, legacy slots ignored",
            "T3 T2 + weighted2/weighted4 unified (approximation)",
            "T4 T3 + alpha variant unified (approximation)",
        ];

        private static readonly string[] CrossRigidLegacyTextureSlots =
        [
            "s_diffuse",
            "s_gloss",
            "s_specular",
            "s_normal",
            "s_mask",
        ];

        private readonly record struct CrossRigidIdealMergePart(
            int[] IdentityIds,
            int VertexCount,
            string MaterialPath);

        private static void RecordCrossRigidIdealMergeExpectedDraws(
            BatchState state,
            string vmdPath,
            CrossRigidAttachmentLocalExpansionResult expansion,
            Func<string, CrossRigidAnalysisComponent?> resolveComponent)
        {
            var timer = Stopwatch.GetTimestamp();
            state.CrossRigidIdealMergeExpectedDrawsByVmd[vmdPath] =
                CalculateCrossRigidIdealMergeExpectedDraws(
                    state,
                    vmdPath,
                    expansion,
                    resolveComponent);
            AddCrossRigidTimingDuration(
                state,
                "Analyze: ideal merge tiers",
                Stopwatch.GetElapsedTime(timer));
        }

        // Mirrors CalculateCrossRigidBaselineExpectedDraws (same early
        // return, scenario LODs, configuration skipping, component
        // resolution, LOD clamp and army weighting) so the result per tier is
        // directly comparable with the baseline. Only the per-configuration
        // draw count differs: pooled across instances by tier identity.
        private static double[] CalculateCrossRigidIdealMergeExpectedDraws(
            BatchState state,
            string vmdPath,
            CrossRigidAttachmentLocalExpansionResult expansion,
            Func<string, CrossRigidAnalysisComponent?> resolveComponent)
        {
            var result = new double[CrossRigidIdealMergeTierCount];
            var model = state.ArmyResidencyModel;
            if (model == null ||
                GetRelevantArmyUnitIds(
                        model,
                        Array.Empty<string>(),
                        [vmdPath])
                    .Count == 0)
            {
                return result;
            }

            var lodDrawsByTier =
                new List<(double LodProbability, double DrawsPerEntity)>
                    [CrossRigidIdealMergeTierCount];
            for (var tier = 0; tier < CrossRigidIdealMergeTierCount; tier++)
                lodDrawsByTier[tier] = [];

            var instances =
                new List<(CrossRigidAnalysisComponent Component,
                    int SourceLodIndex)>();
            foreach (var (scenarioLodIndex, lodProbability) in model
                         .Scenario.LodDistribution
                         .Where(entry => entry.Value > 0)
                         .OrderBy(entry => entry.Key))
            {
                var drawsPerEntity = new double[CrossRigidIdealMergeTierCount];
                foreach (var configurations in
                         expansion.ConfigurationsByAttachment.Values)
                {
                    foreach (var configuration in configurations)
                    {
                        if (configuration.Probability <= 0)
                            continue;

                        instances.Clear();
                        foreach (var instance in configuration.Instances)
                        {
                            var component =
                                resolveComponent(instance.WsModelPath);
                            if (component == null ||
                                component.Rigid.ModelList.Length == 0)
                            {
                                continue;
                            }

                            instances.Add((
                                component,
                                Math.Min(
                                    scenarioLodIndex,
                                    component.Rigid.ModelList.Length - 1)));
                        }

                        if (instances.Count == 0)
                            continue;

                        var configurationDraws =
                            GetCrossRigidIdealMergeConfigurationDraws(
                                state,
                                instances);
                        for (var tier = 0;
                             tier < CrossRigidIdealMergeTierCount;
                             tier++)
                        {
                            drawsPerEntity[tier] +=
                                configuration.Probability *
                                configurationDraws[tier];
                        }
                    }
                }

                for (var tier = 0; tier < CrossRigidIdealMergeTierCount; tier++)
                {
                    if (drawsPerEntity[tier] > 0)
                        lodDrawsByTier[tier].Add(
                            (lodProbability, drawsPerEntity[tier]));
                }
            }

            for (var tier = 0; tier < CrossRigidIdealMergeTierCount; tier++)
            {
                result[tier] = CalculateExpectedCrossRigidArmyDrawWeight(
                    state,
                    vmdPath,
                    1.0,
                    lodDrawsByTier[tier]);
            }

            return result;
        }

        // Pooled draws of one configuration at one set of source LODs, one
        // entry per tier. Cached by the sorted (WSModel, source LOD) list:
        // configurations repeat heavily across LODs and VMD roots.
        private static int[] GetCrossRigidIdealMergeConfigurationDraws(
            BatchState state,
            IReadOnlyList<(CrossRigidAnalysisComponent Component,
                int SourceLodIndex)> instances)
        {
            var cacheKey = string.Join(
                "\u001e",
                instances
                    .Select(instance =>
                        $"{instance.Component.WsModelPath}@{instance.SourceLodIndex}")
                    .OrderBy(item => item, StringComparer.Ordinal));
            if (state.CrossRigidIdealMergeConfigurationDrawCache.TryGetValue(
                    cacheKey,
                    out var cached))
            {
                return cached;
            }

            var parts = new List<CrossRigidIdealMergePart>();
            foreach (var (component, sourceLodIndex) in instances)
            {
                // Same bounds guards as CountCrossRigidInstanceInputDraws.
                if (sourceLodIndex < 0 ||
                    sourceLodIndex >= component.Rigid.ModelList.Length ||
                    sourceLodIndex >= component.MaterialAssignments.Length)
                {
                    continue;
                }

                var models = component.Rigid.ModelList[sourceLodIndex];
                var assignments = component.MaterialAssignments[sourceLodIndex];
                for (var partIndex = 0;
                     partIndex < models.Length &&
                     partIndex < assignments.Length;
                     partIndex++)
                {
                    parts.Add(new CrossRigidIdealMergePart(
                        GetCrossRigidIdealMergePartIdentityIds(
                            state,
                            component.RigidPath,
                            sourceLodIndex,
                            partIndex,
                            models[partIndex],
                            assignments[partIndex]),
                        models[partIndex].Mesh.VertexList.Length,
                        Normalize(assignments[partIndex])));
                }
            }

            var draws = new int[CrossRigidIdealMergeTierCount];
            for (var tier = 0; tier < CrossRigidIdealMergeTierCount; tier++)
            {
                var capturedTier = tier;
                draws[tier] = CountCrossRigidIdealMergeDraws(
                    parts.Select(part =>
                        (part.IdentityIds[capturedTier], part.VertexCount)));
            }

            RecordCrossRigidIdealMergeSharedAtlasMaterials(state, parts);
            state.CrossRigidIdealMergeConfigurationDrawCache[cacheKey] = draws;
            return draws;
        }

        // Pooling rule: group by identity, then vertex-limit each group with
        // the same bin counter the baseline uses. Pure, so it is testable
        // with injected identities.
        private static int CountCrossRigidIdealMergeDraws(
            IEnumerable<(int IdentityId, int VertexCount)> parts)
        {
            var vertexCountsByIdentity = new Dictionary<int, List<int>>();
            foreach (var (identityId, vertexCount) in parts)
            {
                if (!vertexCountsByIdentity.TryGetValue(
                        identityId,
                        out var vertexCounts))
                {
                    vertexCounts = [];
                    vertexCountsByIdentity[identityId] = vertexCounts;
                }

                vertexCounts.Add(vertexCount);
            }

            return vertexCountsByIdentity.Values.Sum(
                CountCrossRigidVertexLimitedMergeBins);
        }

        // Per-part tier identities (as interned ids), cached by the
        // part-defining key. Index 0 hashes the existing strict identity;
        // 1..4 are the ideal-merge identities defined in this file.
        private static int[] GetCrossRigidIdealMergePartIdentityIds(
            BatchState state,
            string rigidPath,
            int lodIndex,
            int partIndex,
            RmvModel model,
            string materialPath)
        {
            var normalizedRigidPath = Normalize(rigidPath);
            var normalizedMaterialPath = Normalize(materialPath);
            var cacheKey = (
                normalizedRigidPath,
                lodIndex,
                partIndex,
                normalizedMaterialPath);
            if (state.CrossRigidIdealMergePartIdentityCache.TryGetValue(
                    cacheKey,
                    out var cached))
            {
                return cached;
            }

            var strictIdentity = GetCrossRigidSourcePartMergeIdentity(
                state,
                rigidPath,
                lodIndex,
                partIndex,
                model,
                materialPath);
            // GetCrossRigidSourcePartMergeIdentity has just cached the RMV
            // identity for this mesh (GetRmvMergeIdentityForMerge(model,
            // false) == GetRmvMergeIdentity(model)); reuse it for T1/T2.
            var rmvIdentity =
                state.CrossRigidSourceMeshMergeIdentityCache.TryGetValue(
                    new MeshKey(normalizedRigidPath, lodIndex, partIndex),
                    out var cachedRmvIdentity)
                    ? cachedRmvIdentity
                    : GetRmvMergeIdentity(model);
            var weightedUnifiedRmvIdentity =
                GetCrossRigidIdealMergeRmvIdentity(
                    model,
                    ignoreRenderFlag: false);
            var alphaUnifiedRmvIdentity =
                GetCrossRigidIdealMergeRmvIdentity(
                    model,
                    ignoreRenderFlag: true);

            string[] identities =
            [
                strictIdentity,
                string.Join(
                    "\u001e",
                    rmvIdentity,
                    GetCrossRigidIdealMergeMaterialIdentity(
                        state,
                        normalizedMaterialPath,
                        1)),
                string.Join(
                    "\u001e",
                    rmvIdentity,
                    GetCrossRigidIdealMergeMaterialIdentity(
                        state,
                        normalizedMaterialPath,
                        2)),
                string.Join(
                    "\u001e",
                    weightedUnifiedRmvIdentity,
                    GetCrossRigidIdealMergeMaterialIdentity(
                        state,
                        normalizedMaterialPath,
                        3)),
                string.Join(
                    "\u001e",
                    alphaUnifiedRmvIdentity,
                    GetCrossRigidIdealMergeMaterialIdentity(
                        state,
                        normalizedMaterialPath,
                        4)),
            ];
            var ids = new int[identities.Length];
            for (var tier = 0; tier < identities.Length; tier++)
            {
                // Intern the (possibly large) strict identity as its hash.
                var key = tier == 0
                    ? ContentHash(identities[tier])
                    : identities[tier];
                if (!state.CrossRigidIdealMergeIdentityIds.TryGetValue(
                        key,
                        out var id))
                {
                    id = state.CrossRigidIdealMergeIdentityIds.Count;
                    state.CrossRigidIdealMergeIdentityIds[key] = id;
                }

                ids[tier] = id;
            }

            state.CrossRigidIdealMergePartIdentityCache[cacheKey] = ids;
            return ids;
        }

        // Material-side identity for tiers 1..4. Unreadable materials fall
        // back to the path, exactly like GetMeshMergeMaterialIdentity.
        private static string GetCrossRigidIdealMergeMaterialIdentity(
            BatchState state,
            string normalizedMaterialPath,
            int tier)
        {
            var cacheKey = (normalizedMaterialPath, tier);
            if (state.CrossRigidIdealMergeMaterialIdentityCache.TryGetValue(
                    cacheKey,
                    out var cached))
            {
                return cached;
            }

            string identity;
            // The snapshot call is what establishes whether the material is
            // readable (and caches its document in state.MaterialDocuments).
            if (GetMaterialMergeDiagnosticSnapshot(
                    state,
                    normalizedMaterialPath) == null)
            {
                identity = $"path:{normalizedMaterialPath}";
            }
            else
            {
                try
                {
                    identity = "ideal:" + ContentHash(
                        BuildCrossRigidIdealMergeMaterialIdentity(
                            GetMaterialDocument(state, normalizedMaterialPath),
                            tier));
                }
                catch
                {
                    identity = $"path:{normalizedMaterialPath}";
                }
            }

            state.CrossRigidIdealMergeMaterialIdentityCache[cacheKey] =
                identity;
            return identity;
        }

        // Pure material XML -> identity function for tiers 1..4. Starts from
        // the strict rendering identity (name dropped, paths normalized) and
        // removes, tier by tier:
        //   T1+: sources of t_xml_base_colour / t_xml_material_map /
        //        t_xml_normal / t_xml_mask (the whole texture node; a missing
        //        slot and any value are equal).
        //   T2+: the legacy s_diffuse / s_gloss / s_specular / s_normal /
        //        s_mask slots.
        //   T3+: shader "weighted2" -> "weighted4".
        //   T4:  shader "_alpha" before ".xml.shader".
        // Everything else (all other texture slots, params, sort_override,
        // ...) stays part of the identity. This is NOT the snapshot's
        // NonTextureIdentity, which drops the whole <textures> node.
        private static string BuildCrossRigidIdealMergeMaterialIdentity(
            XmlDocument material,
            int tier)
        {
            var clone = new XmlDocument();
            clone.LoadXml(material.OuterXml);

            if (tier >= 1)
            {
                foreach (var channel in AtlasChannels)
                    RemoveTextureSlot(clone, channel.Slot);
            }

            if (tier >= 2)
            {
                foreach (var slot in CrossRigidLegacyTextureSlots)
                    RemoveTextureSlot(clone, slot);
            }

            if (tier >= 3)
            {
                var shaderNode = clone.SelectSingleNode("/material/shader");
                if (shaderNode != null)
                {
                    shaderNode.InnerText =
                        NormalizeCrossRigidIdealMergeShader(
                            shaderNode.InnerText,
                            tier);
                }
            }

            return GetMaterialRenderingIdentity(clone);
        }

        private static string NormalizeCrossRigidIdealMergeShader(
            string? shader,
            int tier)
        {
            var normalized = Normalize(shader);
            if (tier >= 3)
            {
                normalized = normalized.Replace(
                    "weighted2",
                    "weighted4",
                    StringComparison.Ordinal);
            }

            const string alphaSuffix = "_alpha.xml.shader";
            if (tier >= 4 &&
                normalized.EndsWith(alphaSuffix, StringComparison.Ordinal))
            {
                normalized = normalized[..^alphaSuffix.Length] +
                             ".xml.shader";
            }

            return normalized;
        }

        // RMV-side identity for tiers 3 and 4. Same inputs as
        // GetRmvMergeIdentity (model type flag, render flag, vertex format,
        // RMV shader name, serialized material minus texture paths/names),
        // except:
        //   * vertex format Cinematic (weighted4 vertices) is treated as
        //     Weighted (weighted2), both in the explicit field and in the
        //     serialized material, whose first field is the vertex format.
        //     In the samples inspected this was the only RMV-side difference
        //     between weighted2 and weighted4 parts (model type, render flag
        //     and RMV shader name were identical).
        //   * "weighted2" in the RMV shader name is read as "weighted4".
        //     Defensive only: the RMV shader name is a 12-character field
        //     ("default_dry") in the inspected data.
        //   * T4 (ignoreRenderFlag): the render flag is dropped.
        // All other serialized material state (transform, matrix indices,
        // attachment points, string/float/int/vec4 params) stays strict.
        private static string GetCrossRigidIdealMergeRmvIdentity(
            RmvModel model,
            bool ignoreRenderFlag)
        {
            var vertexFormat = model.Material.BinaryVertexFormat ==
                               VertexFormat.Cinematic
                ? VertexFormat.Weighted
                : model.Material.BinaryVertexFormat;
            var material = model.Material.Clone();
            if (material is WeightedMaterial weighted)
            {
                weighted.ModelName = string.Empty;
                weighted.TextureDirectory = string.Empty;
                weighted.TexturesParams = [];
                weighted.BinaryVertexFormat = vertexFormat;
            }

            var materialBytes = MaterialFactory.Create().Save(
                model.CommonHeader.ModelTypeFlag,
                material);
            var shaderName = (model.CommonHeader.ShaderParams.ShaderName ??
                              string.Empty)
                .Replace(
                    "weighted2",
                    "weighted4",
                    StringComparison.OrdinalIgnoreCase);

            return ContentHash(
                string.Join(
                    "|",
                    model.CommonHeader.ModelTypeFlag,
                    ignoreRenderFlag
                        ? "*"
                        : model.CommonHeader.RenderFlag.ToString(),
                    vertexFormat,
                    shaderName,
                    Convert.ToHexString(materialBytes)));
        }

        // Texture-cost side, T1 only: materials that sit in a T1 identity
        // group which holds more than one strict (T0) identity would have to
        // share an atlas for the merge to happen. A group whose parts all
        // share one strict identity (e.g. identical materials stored under
        // different paths) already merges today and is not counted. Called
        // when a configuration is first evaluated (the per-configuration
        // cache makes that once per distinct configuration).
        private static void RecordCrossRigidIdealMergeSharedAtlasMaterials(
            BatchState state,
            IReadOnlyList<CrossRigidIdealMergePart> parts)
        {
            foreach (var group in parts
                         .Where(part => part.MaterialPath.Length != 0)
                         .GroupBy(part => part.IdentityIds[1]))
            {
                if (group.Select(part => part.IdentityIds[0]).Distinct().Count() <
                    2)
                {
                    continue;
                }

                var materialPaths = group
                    .Select(part => part.MaterialPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                foreach (var materialPath in materialPaths)
                    state.CrossRigidIdealMergeSharedAtlasMaterialPaths.Add(
                        materialPath);
            }
        }

        // Distinct atlasable source textures of the shared-atlas materials
        // and, where the DDS header is readable, their BCn payload. Reads
        // only 148 header bytes per texture (as the value gate does) and
        // works in cross-rigid-only mode because it does not depend on the
        // atlas stages' per-texture tables.
        private static void FinalizeCrossRigidIdealMergeTextureCost(
            BatchState state)
        {
            var timer = Stopwatch.GetTimestamp();
            var texturePaths = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var materialPath in
                     state.CrossRigidIdealMergeSharedAtlasMaterialPaths)
            {
                if (!state.MaterialDocuments.TryGetValue(
                        Normalize(materialPath),
                        out var document))
                {
                    continue;
                }

                foreach (var channel in AtlasChannels)
                {
                    var texturePath = GetTexturePath(document, channel.Slot);
                    if (texturePath.Length != 0)
                        texturePaths.Add(texturePath);
                }
            }

            state.CrossRigidIdealMergeSharedAtlasTextureCount =
                texturePaths.Count;
            state.CrossRigidIdealMergeSharedAtlasSizedTextureCount = 0;
            state.CrossRigidIdealMergeSharedAtlasBcnBytes = 0;
            foreach (var texturePath in texturePaths)
            {
                try
                {
                    var file = FindForReadStatic(state, texturePath);
                    if (file == null ||
                        !DdsBcnResidencyEstimator.TryEstimate(
                            file.DataSource.PeekData(148),
                            out var estimate))
                    {
                        continue;
                    }

                    state.CrossRigidIdealMergeSharedAtlasSizedTextureCount++;
                    state.CrossRigidIdealMergeSharedAtlasBcnBytes +=
                        estimate.Bytes;
                }
                catch
                {
                    // Unreadable header: counted, but not sized.
                }
            }

            AddCrossRigidTimingDuration(
                state,
                "Analyze: ideal merge tiers",
                Stopwatch.GetElapsedTime(timer));
        }

        // Number of VMD roots violating baseline >= T0 >= T1 >= T2 >= T3 >=
        // T4 >= 0 (within tolerance). Computed, never thrown.
        private static int CountCrossRigidIdealMergeInvariantViolations(
            BatchState state)
        {
            var violations = 0;
            foreach (var (vmdPath, tiers) in
                     state.CrossRigidIdealMergeExpectedDrawsByVmd)
            {
                var previous = state.CrossRigidBaselineExpectedDrawsByVmd
                    .GetValueOrDefault(vmdPath);
                var violated = false;
                foreach (var value in tiers)
                {
                    if (value > previous + CrossRigidIdealMergeSelfCheckTolerance)
                        violated = true;
                    previous = value;
                }

                if (tiers.Length != 0 &&
                    tiers[^1] < -CrossRigidIdealMergeSelfCheckTolerance)
                {
                    violated = true;
                }

                if (violated)
                    violations++;
            }

            return violations;
        }

        private static string[] BuildCrossRigidIdealMergeReportLines(
            BatchState state,
            double selectedExpectedDraws)
        {
            var lines = new List<string>
            {
                "Ideal cross-rigid merge potential (per-configuration " +
                "pooling; upper bounds, ignores VMD writer limits and " +
                "geometry/texture cost):",
            };
            var baselineTotal =
                state.CrossRigidBaselineExpectedDrawsByVmd.Values.Sum();
            var tierTotals = new double[CrossRigidIdealMergeTierCount];
            foreach (var tiers in
                     state.CrossRigidIdealMergeExpectedDrawsByVmd.Values)
            {
                for (var tier = 0;
                     tier < CrossRigidIdealMergeTierCount &&
                     tier < tiers.Length;
                     tier++)
                {
                    tierTotals[tier] += tiers[tier];
                }
            }

            double Share(double value)
                => baselineTotal > AtlasValueGateExpectedDrawEpsilon
                    ? value / baselineTotal
                    : 0;

            for (var tier = 0; tier < CrossRigidIdealMergeTierCount; tier++)
            {
                var saved = baselineTotal - tierTotals[tier];
                lines.Add(
                    $"  {CrossRigidIdealMergeTierLabels[tier]}: " +
                    $"expected battle draws={tierTotals[tier]:0.###}, " +
                    $"saved vs baseline={saved:0.###} " +
                    $"({Share(saved):P1})");
            }

            lines.Add(
                $"  Currently selected: saved={selectedExpectedDraws:0.###} " +
                $"({Share(selectedExpectedDraws):P1} of baseline)");

            var violations =
                CountCrossRigidIdealMergeInvariantViolations(state);
            lines.Add(
                "  Self-check baseline >= T0 >= T1 >= T2 >= T3 >= T4: " +
                (violations == 0
                    ? "OK"
                    : $"{violations:N0} VMD root(s) violate it"));

            var textureCount = state.CrossRigidIdealMergeSharedAtlasTextureCount;
            var sizedCount =
                state.CrossRigidIdealMergeSharedAtlasSizedTextureCount;
            var bcn = sizedCount == 0
                ? string.Empty
                : sizedCount == textureCount
                    ? $", {FormatMiB(state.CrossRigidIdealMergeSharedAtlasBcnBytes)} BCn"
                    : $", {FormatMiB(state.CrossRigidIdealMergeSharedAtlasBcnBytes)} BCn " +
                      $"({sizedCount:N0} of {textureCount:N0} sizable)";
            lines.Add(
                $"  Shared-atlas (T1) merges would combine " +
                $"{state.CrossRigidIdealMergeSharedAtlasMaterialPaths.Count:N0} " +
                $"distinct material(s) using {textureCount:N0} distinct " +
                $"atlasable source texture(s){bcn}");

            lines.Add("  Top 10 VMD roots by T1 saving:");
            foreach (var (vmdPath, tiers) in state
                         .CrossRigidIdealMergeExpectedDrawsByVmd
                         .Select(entry => (
                             VmdPath: entry.Key,
                             Tiers: entry.Value,
                             Baseline: state.CrossRigidBaselineExpectedDrawsByVmd
                                 .GetValueOrDefault(entry.Key)))
                         .Where(entry =>
                             entry.Tiers.Length == CrossRigidIdealMergeTierCount &&
                             entry.Baseline - entry.Tiers[1] >
                             AtlasValueGateExpectedDrawEpsilon)
                         .OrderByDescending(entry =>
                             entry.Baseline - entry.Tiers[1])
                         .ThenBy(entry => entry.VmdPath, StringComparer.Ordinal)
                         .Take(10)
                         .Select(entry => (entry.VmdPath, entry.Tiers)))
            {
                var baseline = state.CrossRigidBaselineExpectedDrawsByVmd
                    .GetValueOrDefault(vmdPath);
                lines.Add(
                    $"    {vmdPath}: baseline={baseline:0.###}, " +
                    $"T0={tiers[0]:0.###}, T1={tiers[1]:0.###}, " +
                    $"T4={tiers[4]:0.###}");
            }

            return lines.ToArray();
        }
    }
}
