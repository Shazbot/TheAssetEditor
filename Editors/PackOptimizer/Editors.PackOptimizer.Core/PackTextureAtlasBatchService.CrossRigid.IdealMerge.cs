﻿using System.Diagnostics;
using System.Xml;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.MaterialHeaders;
using Shared.GameFormats.RigidModel.Vertex;

namespace Editors.PackOptimizer.Core
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
            RecordCrossRigidIdealMergePayloadAccounting(
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

            var parts = BuildCrossRigidIdealMergeParts(state, instances);

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

        // Parts of all instances at their source LODs, with tier identities.
        private static List<CrossRigidIdealMergePart>
            BuildCrossRigidIdealMergeParts(
                BatchState state,
                IReadOnlyList<(CrossRigidAnalysisComponent Component,
                    int SourceLodIndex)> instances)
        {
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

            return parts;
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
            if (state.Consumers?.IsVanillaReplaced(normalizedMaterialPath) == true ||
                GetMaterialMergeDiagnosticSnapshot(
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

            lines.AddRange(
                BuildCrossRigidIdealMergePayloadReportLines(
                    state,
                    baselineTotal));
            return lines.ToArray();
        }

        // ---- Realisability estimates (report-only) ----
        //
        // Linearity: CalculateExpectedCrossRigidArmyDrawWeight computes, per
        // culture, sum over scenario LODs of lodProbability * slotCount *
        // (sum over units of constant factors * DrawsPerEntity) / unitCount,
        // and CombineBattleDrawSavings is a normalised weighted average of
        // the per-culture values. For non-negative per-LOD draws it is
        // therefore exactly linear in the DrawsPerEntity vector (the only
        // non-linear step is skipping a LOD whose draws are <= 0). So
        // weight(draws) = sum_k unitWeight_k * draws_k, with
        // unitWeight_k = weight of draws 1.0 at LOD k only, and
        // baseline - T1 decomposes exactly over configurations.

        private static readonly long[] CrossRigidIdealMergeGrowthCapsMiB =
            [64, 128, 256, 512, 1024];
        private static readonly int[] CrossRigidIdealMergeConfigurationBuckets =
            [16, 64, 512, 4096];
        private static readonly string[] CrossRigidIdealMergeBucketLabels =
            ["<=16", "<=64", "<=512", "<=4096", ">4096"];
        private const double CrossRigidIdealMergeProbabilitySumTolerance = 1e-6;

        // One hypothetical payload: a distinct sorted WSModel list.
        private sealed class CrossRigidIdealMergePayload(
            string key,
            long geometryBytes,
            int[] savedDrawsByScenarioLod,
            string[][] qualifyingMaterialGroups)
        {
            public string Key { get; } = key;
            // Distinct material-path groups (sorted) of T1 identity groups
            // with >= 2 strict identities, over the scenario LODs.
            public string[][] QualifyingMaterialGroups { get; } =
                qualifyingMaterialGroups;
            public long GeometryBytes { get; } = geometryBytes;
            public int[] SavedDrawsByScenarioLod { get; } =
                savedDrawsByScenarioLod;
            public bool IsPayload { get; } =
                savedDrawsByScenarioLod.Any(saved => saved > 0);
            public double Saving { get; set; }
        }

        private sealed record CrossRigidIdealMergeContext(
            string VmdPath,
            string AttachmentIdentity,
            int ConfigurationCount,
            double T1Saving,
            CrossRigidIdealMergePayload[] Payloads)
        {
            public long PayloadGeometryBytes =>
                Payloads.Sum(payload => payload.GeometryBytes);
        }

        private sealed class CrossRigidIdealMergeAccounting
        {
            public Dictionary<string, CrossRigidIdealMergePayload> Payloads
            { get; } = new(StringComparer.Ordinal);
            public List<CrossRigidIdealMergeContext> Contexts { get; } = [];
            public Dictionary<string, long> WsModelGeometryBytes { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<(string WsModelPath, int LodIndex), int>
                InstanceDraws { get; } = [];
            public Dictionary<string, CrossRigidIdealMergeMaterialTextures>
                MaterialTextures { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, CrossRigidIdealMergeTextureInfo?>
                TextureInfos { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, double> MeanEntityGeometryBytesByVmd
            { get; } = new(StringComparer.OrdinalIgnoreCase);
            public int ProbabilitySumContextCount { get; set; }
            public int ProbabilitySumOutsideToleranceCount { get; set; }
            public double ProbabilitySumMin { get; set; } =
                double.PositiveInfinity;
            public double ProbabilitySumMax { get; set; } =
                double.NegativeInfinity;
            public int ExpansionFailureCount { get; set; }
            public int ExpansionLimitFailureCount { get; set; }

            public void Clear()
            {
                Payloads.Clear();
                Contexts.Clear();
                WsModelGeometryBytes.Clear();
                MaterialTextures.Clear();
                TextureInfos.Clear();
                InstanceDraws.Clear();
                MeanEntityGeometryBytesByVmd.Clear();
                ProbabilitySumContextCount = 0;
                ProbabilitySumOutsideToleranceCount = 0;
                ProbabilitySumMin = double.PositiveInfinity;
                ProbabilitySumMax = double.NegativeInfinity;
                ExpansionFailureCount = 0;
                ExpansionLimitFailureCount = 0;
            }
        }

        // An expansion that fails is dropped as a whole: the root gets no
        // baseline and no tier values (see RecordCrossRigidAnalysisBlocker).
        private static void RecordCrossRigidIdealMergeExpansionFailure(
            BatchState state,
            string reason)
        {
            var accounting = state.CrossRigidIdealMergeAccounting;
            accounting.ExpansionFailureCount++;
            if (reason.StartsWith(
                    "Cross-rigid attachment-local configuration limit exceeded",
                    StringComparison.Ordinal))
            {
                accounting.ExpansionLimitFailureCount++;
            }
        }

        // Geometry of one WSModel's rigid over ALL its LODs, with the same
        // formula as BuildCrossRigidSourceGeometryComponent (vertex size by
        // format/version * vertex count + sizeof(ushort) * index count).
        // Unknown vertex formats are skipped instead of throwing.
        private static long GetCrossRigidIdealMergeWsModelGeometryBytes(
            BatchState state,
            CrossRigidAnalysisComponent component)
        {
            var cache = state.CrossRigidIdealMergeAccounting
                .WsModelGeometryBytes;
            if (cache.TryGetValue(component.WsModelPath, out var cached))
                return cached;

            long geometryBytes = 0;
            var vertexFactory = VertexFactory.Create();
            foreach (var model in component.Rigid.ModelList.SelectMany(
                         lod => lod))
            {
                if (!vertexFactory.IsKnownVertex(
                        model.Material.BinaryVertexFormat))
                {
                    continue;
                }

                geometryBytes += checked(
                    (long)vertexFactory.GetVertexSize(
                        model.Material.BinaryVertexFormat,
                        component.Rigid.Header.Version) *
                    model.Mesh.VertexList.Length);
                geometryBytes += checked(
                    (long)sizeof(ushort) *
                    (model.Mesh.IndexList?.Length ?? 0));
            }

            cache[component.WsModelPath] = geometryBytes;
            return geometryBytes;
        }

        private static int GetCrossRigidIdealMergeInstanceDraws(
            BatchState state,
            CrossRigidAnalysisComponent component,
            int sourceLodIndex)
        {
            var cache = state.CrossRigidIdealMergeAccounting.InstanceDraws;
            var key = (component.WsModelPath, sourceLodIndex);
            if (!cache.TryGetValue(key, out var draws))
            {
                draws = CountCrossRigidInstanceInputDraws(
                    state,
                    component,
                    sourceLodIndex);
                cache[key] = draws;
            }

            return draws;
        }

        // Per root VMD: probability-sum statistics (all contexts), then for
        // VMDs with army weight the per-context / per-payload accounting.
        // Reads the same expansion and components as the draw calculation.
        private static void RecordCrossRigidIdealMergePayloadAccounting(
            BatchState state,
            string vmdPath,
            CrossRigidAttachmentLocalExpansionResult expansion,
            Func<string, CrossRigidAnalysisComponent?> resolveComponent)
        {
            var accounting = state.CrossRigidIdealMergeAccounting;
            foreach (var configurations in
                     expansion.ConfigurationsByAttachment.Values)
            {
                var probabilitySum = configurations
                    .Where(configuration => configuration.Probability > 0)
                    .Sum(configuration => configuration.Probability);
                accounting.ProbabilitySumContextCount++;
                accounting.ProbabilitySumMin = Math.Min(
                    accounting.ProbabilitySumMin,
                    probabilitySum);
                accounting.ProbabilitySumMax = Math.Max(
                    accounting.ProbabilitySumMax,
                    probabilitySum);
                if (Math.Abs(probabilitySum - 1.0) >
                    CrossRigidIdealMergeProbabilitySumTolerance)
                {
                    accounting.ProbabilitySumOutsideToleranceCount++;
                }
            }

            var model = state.ArmyResidencyModel;
            if (model == null ||
                GetRelevantArmyUnitIds(
                        model,
                        Array.Empty<string>(),
                        [vmdPath])
                    .Count == 0)
            {
                return;
            }

            var scenarioLods = model.Scenario.LodDistribution
                .Where(entry => entry.Value > 0)
                .OrderBy(entry => entry.Key)
                .Select(entry => (LodIndex: entry.Key, Probability: entry.Value))
                .ToArray();
            if (scenarioLods.Length == 0)
                return;

            var unitWeights = scenarioLods
                .Select(lod =>
                    CalculateExpectedCrossRigidArmyDrawWeight(
                        state,
                        vmdPath,
                        1.0,
                        [(lod.Probability, 1.0)]))
                .ToArray();

            double meanEntityGeometryBytes = 0;
            foreach (var (attachmentIdentity, configurations) in
                     expansion.ConfigurationsByAttachment)
            {
                var configurationCount = 0;
                double probabilitySum = 0;
                double contextGeometryBytes = 0;
                double contextSaving = 0;
                var contextPayloads =
                    new HashSet<CrossRigidIdealMergePayload>();
                foreach (var configuration in configurations)
                {
                    if (configuration.Probability <= 0)
                        continue;

                    configurationCount++;
                    probabilitySum += configuration.Probability;
                    var components =
                        new List<CrossRigidAnalysisComponent>();
                    foreach (var instance in configuration.Instances)
                    {
                        var component =
                            resolveComponent(instance.WsModelPath);
                        if (component == null ||
                            component.Rigid.ModelList.Length == 0)
                        {
                            continue;
                        }

                        components.Add(component);
                    }

                    if (components.Count == 0)
                        continue;

                    contextGeometryBytes +=
                        configuration.Probability *
                        components.Sum(component =>
                            GetCrossRigidIdealMergeWsModelGeometryBytes(
                                state,
                                component));

                    var key = string.Join(
                        "\u001e",
                        components
                            .Select(component => component.WsModelPath)
                            .OrderBy(path => path, StringComparer.Ordinal));
                    if (!accounting.Payloads.TryGetValue(key, out var payload))
                    {
                        payload = BuildCrossRigidIdealMergePayload(
                            state,
                            key,
                            components,
                            scenarioLods.Select(lod => lod.LodIndex)
                                .ToArray());
                        accounting.Payloads[key] = payload;
                    }

                    if (!payload.IsPayload)
                        continue;

                    double weightedSaved = 0;
                    for (var index = 0; index < unitWeights.Length; index++)
                    {
                        weightedSaved +=
                            unitWeights[index] *
                            payload.SavedDrawsByScenarioLod[index];
                    }

                    var saving = configuration.Probability * weightedSaved;
                    payload.Saving += saving;
                    contextSaving += saving;
                    contextPayloads.Add(payload);
                }

                // Normalise by the context's probability sum only when it
                // exceeds 1; otherwise use the sum as is.
                meanEntityGeometryBytes += probabilitySum > 1.0
                    ? contextGeometryBytes / probabilitySum
                    : contextGeometryBytes;
                accounting.Contexts.Add(new CrossRigidIdealMergeContext(
                    vmdPath,
                    attachmentIdentity,
                    configurationCount,
                    contextSaving,
                    contextPayloads.ToArray()));
            }

            accounting.MeanEntityGeometryBytesByVmd[vmdPath] =
                meanEntityGeometryBytes;
        }

        private static CrossRigidIdealMergePayload
            BuildCrossRigidIdealMergePayload(
                BatchState state,
                string key,
                IReadOnlyList<CrossRigidAnalysisComponent> components,
                IReadOnlyList<int> scenarioLodIndices)
        {
            var saved = new int[scenarioLodIndices.Count];
            var qualifyingGroups = new Dictionary<string, string[]>(
                StringComparer.Ordinal);
            for (var index = 0; index < saved.Length; index++)
            {
                var instances =
                    new List<(CrossRigidAnalysisComponent Component,
                        int SourceLodIndex)>();
                var perInstanceDraws = 0;
                foreach (var component in components)
                {
                    var sourceLodIndex = Math.Min(
                        scenarioLodIndices[index],
                        component.Rigid.ModelList.Length - 1);
                    instances.Add((component, sourceLodIndex));
                    perInstanceDraws +=
                        GetCrossRigidIdealMergeInstanceDraws(
                            state,
                            component,
                            sourceLodIndex);
                }

                saved[index] = perInstanceDraws -
                               GetCrossRigidIdealMergeConfigurationDraws(
                                   state,
                                   instances)[1];
                foreach (var group in GetCrossRigidIdealMergeQualifyingGroups(
                             BuildCrossRigidIdealMergeParts(state, instances)))
                {
                    qualifyingGroups[string.Join("\u001e", group)] = group;
                }
            }

            return new CrossRigidIdealMergePayload(
                key,
                components.Sum(component =>
                    GetCrossRigidIdealMergeWsModelGeometryBytes(
                        state,
                        component)),
                saved,
                qualifyingGroups.OrderBy(entry => entry.Key,
                        StringComparer.Ordinal)
                    .Select(entry => entry.Value)
                    .ToArray());
        }

        // T1 identity groups holding >= 2 strict (T0) identities: the
        // materials in them would have to share an atlas. Same rule as
        // RecordCrossRigidIdealMergeSharedAtlasMaterials; sorted distinct
        // material paths per group.
        private static IEnumerable<string[]>
            GetCrossRigidIdealMergeQualifyingGroups(
                IReadOnlyList<CrossRigidIdealMergePart> parts)
        {
            foreach (var group in parts
                         .Where(part => part.MaterialPath.Length != 0)
                         .GroupBy(part => part.IdentityIds[1]))
            {
                if (group.Select(part => part.IdentityIds[0])
                        .Distinct()
                        .Count() < 2)
                {
                    continue;
                }

                var materialPaths = group
                    .Select(part => part.MaterialPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();
                if (materialPaths.Length >= 2)
                    yield return materialPaths;
            }
        }

        // Greedy payload selection under the geometry budgets: by saving per
        // byte descending, i.e. bytes per saving ascending (ties by key,
        // ordinal); a payload whose own
        // bytes/saving exceeds the per-payload ceiling is skipped; a payload
        // is accepted when cumulative bytes stay within the growth cap
        // (null = unlimited), otherwise skipped and later ones are still
        // tried. Pure, so it is testable with injected (saving, bytes).
        private static (int Accepted, double Saving, long Bytes)
            SelectCrossRigidIdealMergePayloadsGreedy(
                IReadOnlyList<(string Key, double Saving, long Bytes)> payloads,
                double perPayloadCeilingBytesPerDraw,
                long? growthCapBytes)
        {
            var accepted = SelectCrossRigidIdealMergePayloadKeysGreedy(
                payloads,
                perPayloadCeilingBytesPerDraw,
                growthCapBytes);
            return (
                accepted.Count,
                accepted.Sum(item => item.Saving),
                accepted.Sum(item => item.Bytes));
        }

        // Same selection, returning the accepted payloads in acceptance
        // order.
        private static List<(string Key, double Saving, long Bytes)>
            SelectCrossRigidIdealMergePayloadKeysGreedy(
                IReadOnlyList<(string Key, double Saving, long Bytes)> payloads,
                double perPayloadCeilingBytesPerDraw,
                long? growthCapBytes)
        {
            var accepted = new List<(string Key, double Saving, long Bytes)>();
            long bytes = 0;
            foreach (var payload in payloads
                         .OrderBy(item =>
                             item.Saving > 0
                                 ? item.Bytes / item.Saving
                                 : double.PositiveInfinity)
                         .ThenBy(item => item.Key, StringComparer.Ordinal))
            {
                if (payload.Saving <= AtlasValueGateExpectedDrawEpsilon ||
                    payload.Bytes / payload.Saving >
                    perPayloadCeilingBytesPerDraw)
                {
                    continue;
                }

                if (growthCapBytes.HasValue &&
                    bytes + payload.Bytes > growthCapBytes.Value)
                {
                    continue;
                }

                accepted.Add(payload);
                bytes += payload.Bytes;
            }

            return accepted;
        }

        private static int GetCrossRigidIdealMergeConfigurationBucket(
            int configurationCount)
        {
            for (var bucket = 0;
                 bucket < CrossRigidIdealMergeConfigurationBuckets.Length;
                 bucket++)
            {
                if (configurationCount <=
                    CrossRigidIdealMergeConfigurationBuckets[bucket])
                {
                    return bucket;
                }
            }

            return CrossRigidIdealMergeConfigurationBuckets.Length;
        }

        private static List<string> BuildCrossRigidIdealMergePayloadReportLines(
            BatchState state,
            double baselineTotal)
        {
            var accounting = state.CrossRigidIdealMergeAccounting;
            var lines = new List<string>();
            double Share(double value, double total)
                => total > AtlasValueGateExpectedDrawEpsilon
                    ? value / total
                    : 0;

            var t1Saving = state.CrossRigidIdealMergeExpectedDrawsByVmd
                .Sum(entry =>
                    state.CrossRigidBaselineExpectedDrawsByVmd
                        .GetValueOrDefault(entry.Key) -
                    (entry.Value.Length > 1 ? entry.Value[1] : 0));
            var payloads = accounting.Payloads.Values
                .Where(payload => payload.IsPayload)
                .ToArray();
            var payloadSaving = payloads.Sum(payload => payload.Saving);

            lines.Add(
                "  T1 payload accounting (exact decomposition: the army " +
                "weighting is linear in per-LOD draws):");
            lines.Add(
                $"    T1 hypothetical payloads: {payloads.Length:N0} " +
                $"distinct configuration(s), total geometry " +
                $"{FormatMiB(payloads.Sum(payload => payload.GeometryBytes))}, " +
                $"Σ saving {payloadSaving:0.###}");
            lines.Add(
                $"    Σ payload savings = {payloadSaving:0.######} vs " +
                $"T1 saving = {t1Saving:0.######}");
            lines.Add(
                "    Greedy payload selection by saving per byte (per-payload " +
                $"ceiling {FormatMiB(CrossRigidPerPayloadCostCeilingMultiplier * MaxCrossRigidGeneratedGeometryBytesPerExpectedArmyDraw, 2)} " +
                "per saved draw; ignores the resident-geometry budget, " +
                "writer/state-cap limits and texture cost):");
            var injected = payloads
                .Select(payload =>
                    (payload.Key, payload.Saving, payload.GeometryBytes))
                .ToArray();
            string[]? previousAtlasKeys = null;
            foreach (long? capMiB in CrossRigidIdealMergeGrowthCapsMiB
                         .Select(cap => (long?)cap)
                         .Append(null))
            {
                var acceptedPayloads =
                    SelectCrossRigidIdealMergePayloadKeysGreedy(
                        injected,
                        CrossRigidPerPayloadCostCeilingMultiplier *
                        MaxCrossRigidGeneratedGeometryBytesPerExpectedArmyDraw,
                        capMiB.HasValue ? capMiB.Value * 1024 * 1024 : null);
                var accepted = acceptedPayloads.Count;
                var saving = acceptedPayloads.Sum(item => item.Saving);
                var bytes = acceptedPayloads.Sum(item => item.Bytes);
                var capLabel = capMiB.HasValue
                    ? $"{capMiB.Value:N0} MiB" +
                      (capMiB.Value ==
                       MaxCrossRigidGeneratedGeometryGrowthBytes / (1024 * 1024)
                          ? " (today's)"
                          : string.Empty)
                    : "unlimited";
                lines.Add(
                    $"      growth cap {capLabel}: {accepted:N0} accepted, " +
                    $"saved {saving:0.###} draws " +
                    $"({Share(saving, baselineTotal):P1} of baseline), " +
                    $"geometry {FormatMiB(bytes)}");
                if (capMiB.HasValue &&
                    !CrossRigidIdealMergeCapsWithAtlasMiB.Contains(
                        (int)capMiB.Value))
                {
                    continue;
                }

                var atlasKeys = acceptedPayloads
                    .Select(item => item.Key)
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToArray();
                var isTodaysCap = capMiB ==
                                  MaxCrossRigidGeneratedGeometryGrowthBytes /
                                  (1024 * 1024);
                if (!isTodaysCap &&
                    previousAtlasKeys != null &&
                    atlasKeys.SequenceEqual(
                        previousAtlasKeys,
                        StringComparer.Ordinal))
                {
                    lines.Add(
                        "        atlas inputs: same accepted set as the " +
                        "previous cap");
                    continue;
                }

                previousAtlasKeys = atlasKeys;
                lines.AddRange(
                    BuildCrossRigidIdealMergeAtlasLines(
                        AnalyzeCrossRigidIdealMergeAtlas(
                            state,
                            atlasKeys
                                .Select(key => accounting.Payloads[key])
                                .ToArray()),
                        baselineTotal,
                        isTodaysCap));
            }

            lines.Add(
                "  Expansion exactness and state-cap proxy (contexts of " +
                "root VMDs used by at least one army unit):");
            lines.Add(
                $"    Exact expansions: " +
                $"{state.CrossRigidIdealMergeExpectedDrawsByVmd.Count:N0} " +
                $"root VMD(s); fallback (expansion failed, root skipped " +
                $"entirely, absent from the baseline and every tier): " +
                $"{accounting.ExpansionFailureCount:N0}, of which " +
                $"{accounting.ExpansionLimitFailureCount:N0} exceeded the " +
                $"{MaxExactVisualConfigurations:N0} unique-configuration limit");
            lines.Add(
                "    Contexts by configuration count (T1 saving per bucket):");
            var bucketContexts =
                new int[CrossRigidIdealMergeBucketLabels.Length];
            var bucketWithSaving =
                new int[CrossRigidIdealMergeBucketLabels.Length];
            var bucketSaving =
                new double[CrossRigidIdealMergeBucketLabels.Length];
            foreach (var context in accounting.Contexts)
            {
                var bucket = GetCrossRigidIdealMergeConfigurationBucket(
                    context.ConfigurationCount);
                bucketContexts[bucket]++;
                if (context.T1Saving > AtlasValueGateExpectedDrawEpsilon)
                    bucketWithSaving[bucket]++;
                bucketSaving[bucket] += context.T1Saving;
            }

            for (var bucket = 0;
                 bucket < CrossRigidIdealMergeBucketLabels.Length;
                 bucket++)
            {
                lines.Add(
                    $"      {CrossRigidIdealMergeBucketLabels[bucket]} " +
                    $"configuration(s): {bucketContexts[bucket]:N0} " +
                    $"context(s) ({bucketWithSaving[bucket]:N0} with " +
                    $"saving), T1 saving {bucketSaving[bucket]:0.###}");
            }

            var underCap = bucketSaving.Take(
                CrossRigidIdealMergeConfigurationBuckets.Length).Sum();
            lines.Add(
                $"    T1 saving in contexts with <=" +
                $"{MaxCrossRigidJointStateCombinations:N0} configurations " +
                $"(state-cap proxy, not the weighted Cartesian state " +
                $"count): {underCap:0.###} " +
                $"({Share(underCap, t1Saving):P1} of T1)");
            lines.Add(
                accounting.ProbabilitySumContextCount == 0
                    ? "    Per-context configuration probability sum: no contexts"
                    : $"    Per-context configuration probability sum: " +
                      $"min={accounting.ProbabilitySumMin:0.######}, " +
                      $"max={accounting.ProbabilitySumMax:0.######} over " +
                      $"{accounting.ProbabilitySumContextCount:N0} " +
                      $"context(s); {accounting.ProbabilitySumOutsideToleranceCount:N0} " +
                      $"outside 1 +/- {CrossRigidIdealMergeProbabilitySumTolerance:0.#######}");
            lines.Add("    Top 10 contexts by T1 saving:");
            foreach (var context in accounting.Contexts
                         .Where(context =>
                             context.T1Saving >
                             AtlasValueGateExpectedDrawEpsilon)
                         .OrderByDescending(context => context.T1Saving)
                         .ThenBy(context => context.VmdPath, StringComparer.Ordinal)
                         .ThenBy(context => context.AttachmentIdentity,
                             StringComparer.Ordinal)
                         .Take(10))
            {
                var attachment = context.AttachmentIdentity.Length > 80
                    ? context.AttachmentIdentity[..80]
                    : context.AttachmentIdentity;
                lines.Add(
                    $"      {context.VmdPath} | {attachment}: " +
                    $"configurations={context.ConfigurationCount:N0}, " +
                    $"payloads={context.Payloads.Length:N0}, " +
                    $"T1 saving={context.T1Saving:0.###}, " +
                    $"geometry={FormatMiB(context.PayloadGeometryBytes)}");
            }

            // Baked-variant estimate (option 2).
            var savingByVmd = state.CrossRigidIdealMergeExpectedDrawsByVmd
                .Select(entry => (
                    VmdPath: entry.Key,
                    Saving: state.CrossRigidBaselineExpectedDrawsByVmd
                                .GetValueOrDefault(entry.Key) -
                            (entry.Value.Length > 1 ? entry.Value[1] : 0)))
                .Where(entry =>
                    entry.Saving > AtlasValueGateExpectedDrawEpsilon)
                .OrderByDescending(entry => entry.Saving)
                .ThenBy(entry => entry.VmdPath, StringComparer.Ordinal)
                .ToArray();
            lines.Add(
                "  Baked-variant estimate (whole-entity variants per root; " +
                "saving approaches T1 as N grows, at the cost of " +
                "approximating appearance probabilities):");
            foreach (var count in new[] { int.MaxValue, 25, 10 })
            {
                var roots = savingByVmd.Take(count).ToArray();
                var meanTotalBytes = roots.Sum(entry =>
                    accounting.MeanEntityGeometryBytesByVmd
                        .GetValueOrDefault(entry.VmdPath));
                var scope = count == int.MaxValue
                    ? "all roots with T1 saving"
                    : $"{count} roots with the largest T1 saving";
                lines.Add(
                    $"    {scope}: {roots.Length:N0} VMD root(s), T1 saving " +
                    $"{roots.Sum(entry => entry.Saving):0.###} draws; mean " +
                    $"entity geometry total = {FormatMiB(meanTotalBytes)} " +
                    $"(per root {FormatMiB(roots.Length == 0 ? 0 : meanTotalBytes / roots.Length, 2)}); " +
                    $"N variants per root would generate N x total: " +
                    $"8 -> {FormatMiB(8 * meanTotalBytes)}, " +
                    $"16 -> {FormatMiB(16 * meanTotalBytes)}, " +
                    $"32 -> {FormatMiB(32 * meanTotalBytes)}");
            }

            return lines;
        }

        // ---- Atlas inputs and components of the accepted payloads ----

        private const long CrossRigidIdealMergeAtlasPagePixels =
            (long)PackAtlasMaxSize * PackAtlasMaxSize;
        private static readonly int[] CrossRigidIdealMergeCapsWithAtlasMiB =
            [64, 128, 256, 512];
        private static readonly string[] CrossRigidIdealMergePageFitLabels =
            ["fit one page at full resolution", "fit only at 0.5x linear scale",
                "do not fit even at 0.5x"];

        private sealed record CrossRigidIdealMergeMaterialTextures(
            string BaseColourPath,
            string[] AtlasableTexturePaths);

        private sealed record CrossRigidIdealMergeTextureInfo(
            long BcnBytes,
            int Width,
            int Height);

        private sealed record CrossRigidIdealMergeAtlasComponent(
            string[] Materials,
            int BaseColourTextureCount,
            int UnsizedBaseColourTextureCount,
            long BaseColourPixels,
            int AtlasableTextureCount,
            long AtlasableBcnBytes,
            int PayloadCount,
            int FitClass);

        private sealed record CrossRigidIdealMergeAtlasSummary(
            int MaterialCount,
            int TextureCount,
            int SizedTextureCount,
            long BcnBytes,
            CrossRigidIdealMergeAtlasComponent[] Components,
            double[] SavingByFitClass,
            double SavingWithoutAtlas);

        // Union-find over the materials of the qualifying T1 groups: all
        // materials of a group must share an atlas, transitively. Groups with
        // fewer than two distinct materials add nothing (singleton exclusion).
        // Components are returned largest first, each with sorted materials.
        private static List<string[]> BuildCrossRigidIdealMergeAtlasComponents(
            IEnumerable<IReadOnlyList<string>> groups)
        {
            var parent = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

            string Find(string item)
            {
                var root = item;
                while (parent[root] != root)
                    root = parent[root];
                while (parent[item] != root)
                {
                    var next = parent[item];
                    parent[item] = root;
                    item = next;
                }

                return root;
            }

            foreach (var group in groups)
            {
                var members = group
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (members.Length < 2)
                    continue;

                foreach (var member in members)
                    parent.TryAdd(member, member);
                var first = Find(members[0]);
                for (var index = 1; index < members.Length; index++)
                {
                    var other = Find(members[index]);
                    if (!string.Equals(
                            first,
                            other,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        parent[other] = first;
                    }
                }
            }

            return parent.Keys
                .GroupBy(Find, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray())
                .OrderByDescending(component => component.Length)
                .ThenBy(component => component[0], StringComparer.Ordinal)
                .ToList();
        }

        // 0: base-colour pixel area fits one 4096x4096 page at full
        // resolution; 1: fits only at 0.5x linear scale (area / 4); 2: does
        // not fit even then. Ignores packing waste.
        private static int ClassifyCrossRigidIdealMergeAtlasPageFit(
            long pixelArea)
        {
            if (pixelArea <= CrossRigidIdealMergeAtlasPagePixels)
                return 0;
            return pixelArea <= 4 * CrossRigidIdealMergeAtlasPagePixels
                ? 1
                : 2;
        }

        // A payload is as bad as the worst component any of its qualifying
        // groups lies in; -1 when it has no qualifying group (no atlas
        // needed).
        private static int GetCrossRigidIdealMergeWorstFitClass(
            IEnumerable<IReadOnlyList<string>> payloadGroups,
            IReadOnlyDictionary<string, int> fitClassByMaterial)
        {
            var worst = -1;
            foreach (var group in payloadGroups)
            {
                foreach (var material in group)
                {
                    if (fitClassByMaterial.TryGetValue(material, out var fit))
                        worst = Math.Max(worst, fit);
                }
            }

            return worst;
        }

        private static CrossRigidIdealMergeMaterialTextures
            GetCrossRigidIdealMergeMaterialTextures(
                BatchState state,
                string materialPath)
        {
            var cache = state.CrossRigidIdealMergeAccounting.MaterialTextures;
            if (cache.TryGetValue(materialPath, out var cached))
                return cached;

            var baseColour = string.Empty;
            var atlasable = new List<string>();
            if (state.MaterialDocuments.TryGetValue(
                    Normalize(materialPath),
                    out var document))
            {
                foreach (var channel in AtlasChannels)
                {
                    var texturePath = GetTexturePath(document, channel.Slot);
                    if (texturePath.Length == 0)
                        continue;
                    atlasable.Add(texturePath);
                    if (channel.Slot.Equals(
                            "t_xml_base_colour",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        baseColour = texturePath;
                    }
                }
            }

            var result = new CrossRigidIdealMergeMaterialTextures(
                baseColour,
                atlasable.ToArray());
            cache[materialPath] = result;
            return result;
        }

        // Header-only BCn size and dimensions; null when the texture cannot
        // be found or is not an estimable BCn DDS.
        private static CrossRigidIdealMergeTextureInfo?
            GetCrossRigidIdealMergeTextureInfo(
                BatchState state,
                string texturePath)
        {
            var cache = state.CrossRigidIdealMergeAccounting.TextureInfos;
            if (cache.TryGetValue(texturePath, out var cached))
                return cached;

            CrossRigidIdealMergeTextureInfo? info = null;
            try
            {
                var file = FindForReadStatic(state, texturePath);
                if (file != null &&
                    DdsBcnResidencyEstimator.TryEstimate(
                        file.DataSource.PeekData(148),
                        out var estimate))
                {
                    info = new CrossRigidIdealMergeTextureInfo(
                        estimate.Bytes,
                        estimate.Width,
                        estimate.Height);
                }
            }
            catch
            {
                // Unreadable header: stays unsized.
            }

            cache[texturePath] = info;
            return info;
        }

        // Texture involvement and shared-atlas components of one accepted
        // payload set (report-only). Components come from the qualifying
        // T1 groups of the accepted payloads, united transitively.
        private static CrossRigidIdealMergeAtlasSummary
            AnalyzeCrossRigidIdealMergeAtlas(
                BatchState state,
                IReadOnlyList<CrossRigidIdealMergePayload> acceptedPayloads)
        {
            var groups = acceptedPayloads
                .SelectMany(payload => payload.QualifyingMaterialGroups)
                .ToArray();
            var components = BuildCrossRigidIdealMergeAtlasComponents(groups);
            var materials = components.SelectMany(component => component)
                .ToArray();

            var textures = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var material in materials)
            {
                foreach (var texture in GetCrossRigidIdealMergeMaterialTextures(
                                 state,
                                 material)
                             .AtlasableTexturePaths)
                {
                    textures.Add(texture);
                }
            }

            var sizedTextureCount = 0;
            long bcnBytes = 0;
            foreach (var texture in textures)
            {
                var info = GetCrossRigidIdealMergeTextureInfo(state, texture);
                if (info == null)
                    continue;
                sizedTextureCount++;
                bcnBytes += info.BcnBytes;
            }

            var fitClassByMaterial = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);
            var componentIndexByMaterial = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);
            var payloadCounts = new int[components.Count];
            var analysed = new List<CrossRigidIdealMergeAtlasComponent>();
            for (var index = 0; index < components.Count; index++)
            {
                var baseTextures = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                var atlasTextures = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var material in components[index])
                {
                    componentIndexByMaterial[material] = index;
                    var materialTextures =
                        GetCrossRigidIdealMergeMaterialTextures(
                            state,
                            material);
                    if (materialTextures.BaseColourPath.Length != 0)
                        baseTextures.Add(materialTextures.BaseColourPath);
                    atlasTextures.UnionWith(
                        materialTextures.AtlasableTexturePaths);
                }

                long pixels = 0;
                var unsized = 0;
                foreach (var texture in baseTextures)
                {
                    var info = GetCrossRigidIdealMergeTextureInfo(
                        state,
                        texture);
                    if (info == null)
                        unsized++;
                    else
                        pixels += (long)info.Width * info.Height;
                }

                var fitClass = ClassifyCrossRigidIdealMergeAtlasPageFit(pixels);
                foreach (var material in components[index])
                    fitClassByMaterial[material] = fitClass;
                analysed.Add(new CrossRigidIdealMergeAtlasComponent(
                    components[index],
                    baseTextures.Count,
                    unsized,
                    pixels,
                    atlasTextures.Count,
                    atlasTextures.Sum(texture =>
                        GetCrossRigidIdealMergeTextureInfo(state, texture)
                            ?.BcnBytes ?? 0),
                    0,
                    fitClass));
            }

            var savingByFitClass = new double[3];
            double savingWithoutAtlas = 0;
            foreach (var payload in acceptedPayloads)
            {
                var worst = GetCrossRigidIdealMergeWorstFitClass(
                    payload.QualifyingMaterialGroups,
                    fitClassByMaterial);
                if (worst < 0)
                {
                    savingWithoutAtlas += payload.Saving;
                    continue;
                }

                savingByFitClass[worst] += payload.Saving;
                foreach (var componentIndex in payload.QualifyingMaterialGroups
                             .SelectMany(group => group)
                             .Select(material =>
                                 componentIndexByMaterial.GetValueOrDefault(
                                     material,
                                     -1))
                             .Where(componentIndex => componentIndex >= 0)
                             .Distinct())
                {
                    payloadCounts[componentIndex]++;
                }
            }

            return new CrossRigidIdealMergeAtlasSummary(
                materials.Length,
                textures.Count,
                sizedTextureCount,
                bcnBytes,
                analysed
                    .Select((component, index) =>
                        component with { PayloadCount = payloadCounts[index] })
                    .ToArray(),
                savingByFitClass,
                savingWithoutAtlas);
        }

        private static List<string> BuildCrossRigidIdealMergeAtlasLines(
            CrossRigidIdealMergeAtlasSummary summary,
            double baselineTotal,
            bool listLargestComponents)
        {
            var lines = new List<string>();
            var sizable = summary.SizedTextureCount == summary.TextureCount
                ? string.Empty
                : $" ({summary.SizedTextureCount:N0} of " +
                  $"{summary.TextureCount:N0} sizable)";
            lines.Add(
                $"        atlas inputs: {summary.MaterialCount:N0} " +
                $"material(s), {summary.TextureCount:N0} texture(s), " +
                $"{FormatMiB(summary.BcnBytes)} BCn{sizable}");

            var sizes = summary.Components.Select(c => c.Materials.Length)
                .ToArray();
            lines.Add(
                $"        atlas components (minimum shared atlases): " +
                $"{sizes.Length:N0}, largest {(sizes.Length == 0 ? 0 : sizes.Max()):N0} " +
                $"material(s); 2-4: {sizes.Count(size => size <= 4):N0}, " +
                $"5-16: {sizes.Count(size => size is > 4 and <= 16):N0}, " +
                $"17-64: {sizes.Count(size => size is > 16 and <= 64):N0}, " +
                $">64: {sizes.Count(size => size > 64):N0}");

            var unsizedComponents = summary.Components.Count(component =>
                component.UnsizedBaseColourTextureCount != 0);
            var unsizedTextures = summary.Components.Sum(component =>
                component.UnsizedBaseColourTextureCount);
            var fitCounts = Enumerable.Range(0, 3)
                .Select(fit => summary.Components.Count(component =>
                    component.FitClass == fit))
                .ToArray();
            lines.Add(
                "        page fit by summed distinct base-colour pixel area " +
                $"({PackAtlasMaxSize}x{PackAtlasMaxSize} pages, packing " +
                "waste ignored): " +
                $"{fitCounts[0]:N0} {CrossRigidIdealMergePageFitLabels[0]}, " +
                $"{fitCounts[1]:N0} {CrossRigidIdealMergePageFitLabels[1]}, " +
                $"{fitCounts[2]:N0} {CrossRigidIdealMergePageFitLabels[2]}; " +
                $"{unsizedTextures:N0} unsized base-colour texture(s) in " +
                $"{unsizedComponents:N0} component(s) counted as 0 pixels");
            double Share(double value)
                => baselineTotal > AtlasValueGateExpectedDrawEpsilon
                    ? value / baselineTotal
                    : 0;
            lines.Add(
                "        saved draws by worst component class of the " +
                $"payload: full resolution {summary.SavingByFitClass[0]:0.###} " +
                $"({Share(summary.SavingByFitClass[0]):P1} of baseline), " +
                $"only at 0.5x {summary.SavingByFitClass[1]:0.###} " +
                $"({Share(summary.SavingByFitClass[1]):P1}), " +
                $"does not fit {summary.SavingByFitClass[2]:0.###} " +
                $"({Share(summary.SavingByFitClass[2]):P1}); " +
                $"payloads needing no shared atlas (strict merges only) " +
                $"{summary.SavingWithoutAtlas:0.###} " +
                $"({Share(summary.SavingWithoutAtlas):P1})");

            if (listLargestComponents)
            {
                lines.Add("        5 largest components:");
                foreach (var component in summary.Components
                             .OrderByDescending(item => item.Materials.Length)
                             .ThenByDescending(item => item.BaseColourPixels)
                             .ThenBy(item => item.Materials[0],
                                 StringComparer.Ordinal)
                             .Take(5))
                {
                    lines.Add(
                        $"          {component.Materials.Length:N0} " +
                        $"material(s), {component.BaseColourTextureCount:N0} " +
                        $"distinct base-colour texture(s), " +
                        $"{component.BaseColourPixels / 1_000_000.0:0.#} MP " +
                        $"base colour ({component.UnsizedBaseColourTextureCount:N0} unsized), " +
                        $"{FormatMiB(component.AtlasableBcnBytes)} atlasable BCn " +
                        $"({component.AtlasableTextureCount:N0} texture(s)), " +
                        $"{component.PayloadCount:N0} accepted payload(s), " +
                        $"e.g. {string.Join("; ", component.Materials.Take(3))}");
                }
            }

            return lines;
        }
    }
}
