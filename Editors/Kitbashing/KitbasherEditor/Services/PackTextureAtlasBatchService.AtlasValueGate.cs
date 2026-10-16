using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Editors.ImportExport.Importing.Importers.PngToDds;
using Editors.ImportExport.Importing.Importers.PngToDds.Helpers;
using Editors.ImportExport.TextureAtlas;
using Shared.Core.ErrorHandling;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Utility;
using Shared.Core.Services;
using Shared.Core.Settings;
using Shared.GameFormats;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.LodHeader;
using Shared.GameFormats.RigidModel.MaterialHeaders;
using Shared.GameFormats.RigidModel.Types;
using Shared.GameFormats.RigidModel.Vertex;
using Shared.GameFormats.Vmd;
using static Shared.GameFormats.Vmd.VariantMeshDefinition;

namespace Editors.KitbasherEditor.Services
{
    public sealed partial class PackTextureAtlasBatchService
    {
        private static List<AtlasValueGateGroupPlan> BuildAtlasValueGateGroupPlans(
            BatchState state,
            IReadOnlyList<MergeAffinityGroup> contributingGroups,
            IReadOnlyDictionary<MeshKey, AtlasCandidate> candidateByKey,
            IReadOnlySet<MeshKey> batchKeys)
        {
            var plans = new List<AtlasValueGateGroupPlan>();
            var atomicFamilies = state.LodAwareAtlasFamilies
                .Where(family =>
                    family.Count >= 2 &&
                    family.IsSubsetOf(batchKeys) &&
                    family.All(candidateByKey.ContainsKey))
                .Select(family => family.ToHashSet())
                .OrderBy(
                    family => family.Min(key => key.ToString()),
                    StringComparer.Ordinal)
                .ToList();
            var remainingGroupIndexes = new HashSet<int>(
                Enumerable.Range(0, contributingGroups.Count));

            while (remainingGroupIndexes.Count != 0)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                var firstGroupIndex = remainingGroupIndexes
                    .OrderBy(index => index)
                    .First();
                remainingGroupIndexes.Remove(firstGroupIndex);

                var componentGroupIndexes = new List<int> { firstGroupIndex };
                var componentKeys = contributingGroups[firstGroupIndex].Meshes
                    .Where(batchKeys.Contains)
                    .ToHashSet();
                var componentFamilyIndexes = new HashSet<int>();
                var changed = true;
                while (changed)
                {
                    changed = false;
                    for (var familyIndex = 0;
                         familyIndex < atomicFamilies.Count;
                         familyIndex++)
                    {
                        state.CancellationToken.ThrowIfCancellationRequested();
                        if (componentFamilyIndexes.Contains(familyIndex) ||
                            !atomicFamilies[familyIndex].Overlaps(componentKeys))
                        {
                            continue;
                        }

                        componentFamilyIndexes.Add(familyIndex);
                        componentKeys.UnionWith(atomicFamilies[familyIndex]);
                        changed = true;
                    }

                    foreach (var groupIndex in remainingGroupIndexes.ToArray())
                    {
                        if (!contributingGroups[groupIndex].Meshes
                                .Any(componentKeys.Contains))
                        {
                            continue;
                        }

                        remainingGroupIndexes.Remove(groupIndex);
                        componentGroupIndexes.Add(groupIndex);
                        componentKeys.UnionWith(contributingGroups[groupIndex].Meshes);
                        changed = true;
                    }
                }

                var planGroups = componentGroupIndexes
                    .Select(index => FilterMergeAffinityGroup(
                        contributingGroups[index],
                        componentKeys))
                    .Where(group => group.Meshes.Length >= 2)
                    .ToList();
                var candidates = componentKeys
                    .Where(candidateByKey.ContainsKey)
                    .Select(key => candidateByKey[key])
                    .OrderBy(candidate => BuildAtlasPlanningOrderKey(candidate), StringComparer.Ordinal)
                    .ThenBy(candidate => candidate.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(candidate => candidate.Key.LodIndex)
                    .ThenBy(candidate => candidate.Key.PartIndex)
                    .ToList();
                if (candidates.Count < 2)
                    continue;

                var componentAtomicFamilies = componentFamilyIndexes
                    .OrderBy(index => index)
                    .Select(index => atomicFamilies[index]
                        .OrderBy(key => key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(key => key.LodIndex)
                        .ThenBy(key => key.PartIndex)
                        .ToArray())
                    .ToArray();
                plans.Add(
                    new AtlasValueGateGroupPlan(
                        planGroups[0],
                        candidates,
                        componentAtomicFamilies,
                        planGroups));
            }

            return plans;
        }

        private static int GetAtlasValueGatePlanProspectiveTextureMergeDraws(
            AtlasValueGateGroupPlan plan)
            => CalculateProspectiveTextureMergeDraws(plan.AffinityGroups);

        private static List<MergeAffinityGroup> FilterAtlasValueGatePlanGroups(
            AtlasValueGateGroupPlan plan,
            IReadOnlySet<MeshKey> candidateKeys)
            => plan.AffinityGroups
                .Select(group => FilterMergeAffinityGroup(group, candidateKeys))
                .Where(group => group.Meshes.Length >= 2)
                .ToList();

        private static AtlasValueGateGroupPlan CreateAtlasValueGateSubPlan(
            AtlasValueGateGroupPlan source,
            IReadOnlyList<AtlasCandidate> candidates,
            IReadOnlySet<MeshKey> candidateKeys,
            IReadOnlyList<MeshKey[]>? atomicFamilies = null)
        {
            var groups = FilterAtlasValueGatePlanGroups(source, candidateKeys);
            var representative = groups.FirstOrDefault() ??
                FilterMergeAffinityGroup(source.Group, candidateKeys);
            return new AtlasValueGateGroupPlan(
                representative,
                candidates.ToList(),
                atomicFamilies ??
                GetCompleteAtlasFamilySubsets(source.AtomicFamilies, candidateKeys),
                groups);
        }

        private static string GetPreAtlasMaterialIdentity(
            MergeAffinityCandidateInfo info)
            => info.MaterialSnapshot?.RenderingIdentity ??
               $"strict:{info.StrictMaterialIdentity}";

        private static int CalculateProspectiveTextureMergeDraws(
            IReadOnlyList<string> preAtlasMaterialIdentities)
        {
            if (preAtlasMaterialIdentities.Count < 2)
                return 0;

            // Every MergeAffinityGroup shares one strict post-atlas material identity. The
            // delta is therefore the number of strict pre-atlas material groups that the
            // texture-compatible grouping collapses beyond the first group.
            var strictPreAtlasGroupCount = preAtlasMaterialIdentities
                .Distinct(StringComparer.Ordinal)
                .Count();
            return Math.Max(0, strictPreAtlasGroupCount - 1);
        }

        private static int CalculateProspectiveTextureMergeDraws(
            IReadOnlyList<MergeAffinityGroup> groups)
            => groups.Sum(group => group.ProspectiveTextureMergeDraws);

        private static bool AreCounterfactuallyMergeCompatible(
            BatchState state,
            MergeAffinityCandidateInfo left,
            MergeAffinityCandidateInfo right)
        {
            if (left.MaterialSnapshot == null ||
                right.MaterialSnapshot == null ||
                !left.MaterialSnapshot.Shader.Equals(
                    right.MaterialSnapshot.Shader,
                    StringComparison.OrdinalIgnoreCase) ||
                !left.MaterialSnapshot.NonTextureIdentity.Equals(
                    right.MaterialSnapshot.NonTextureIdentity,
                    StringComparison.Ordinal))
            {
                return false;
            }

            var differingSlots = GetDifferingMaterialTextureSlots(
                    state,
                    left.MaterialSnapshot,
                    right.MaterialSnapshot).ToList();
            if (differingSlots.Count == 0)
                return true;

            var classification = ClassifyCounterfactualTexturePair(
                state,
                left.MaterialSnapshot,
                right.MaterialSnapshot,
                differingSlots,
                out _,
                allowDifferentUniformMaskConstants:
                    (left.Candidate.RequiresMaskAtlas || right.Candidate.RequiresMaskAtlas));
            return classification.Equals(
                       "Atlasable differing texture channels",
                       StringComparison.Ordinal) ||
                   classification.Equals(
                       "Uniform-constant equivalence",
                       StringComparison.Ordinal);
        }

        private static string BuildMergeAffinityMaterialIdentity(
            BatchState state,
            AtlasCandidate candidate)
        {
            var material = new XmlDocument();
            material.LoadXml(candidate.MaterialDocument.OuterXml);

            foreach (var channel in AtlasChannels)
            {
                if (IsCandidateIgnoredTextureSlot(candidate, channel.Slot))
                {
                    RemoveTextureSlot(material, channel.Slot);
                    continue;
                }

                // Both resolved and uniform-constant channels can receive the same final
                // atlas path when this candidate shares a batch with a resolved source.
                // Treating constants as preserved here admits a false prospective merge:
                // the planner sees a shared material identity, but ProcessBatch gives the
                // two candidates different generated-material identities. Constant-only
                // batches are safe as well because ProcessBatch canonicalizes equal
                // uniform constants to the same source path.
                if (candidate.ResolvedChannels.Contains(channel.Slot) ||
                    candidate.ConstantChannels.ContainsKey(channel.Slot))
                {
                    SetTexturePath(
                        material,
                        channel.Slot,
                        $"__asset_editor_shared_atlas_{channel.Slot}__");
                }
            }

            return GetMaterialRenderingIdentity(material);
        }

        private static Dictionary<MeshKey, int> BuildBatchIndexByMesh(
            IReadOnlyList<List<AtlasCandidate>> batches)
        {
            var result = new Dictionary<MeshKey, int>();
            for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                foreach (var candidate in batches[batchIndex])
                    result[candidate.Key] = batchIndex;
            }
            return result;
        }

        private static int CalculateMergeAffinityContribution(
            MergeAffinityGroup group,
            IReadOnlyDictionary<MeshKey, int> batchByMesh)
        {
            var score = 0;
            foreach (var batch in group.Meshes
                         .Where(batchByMesh.ContainsKey)
                         .GroupBy(mesh => batchByMesh[mesh]))
            {
                var meshes = batch.ToArray();
                if (meshes.Length < 2)
                    continue;

                // Structural merges already collapse each connected pre-atlas component.
                // Only the remaining component-to-component savings are attributable to
                // atlas placement.
                score += Math.Max(
                    0,
                    CountStructuralMergeComponents(
                        meshes,
                        group.PreExistingStructuralMergePairs) - 1);
            }

            return score;
        }

        private static int CountStructuralMergeComponents(
            IReadOnlyList<MeshKey> meshes,
            IReadOnlyCollection<string> preExistingPairs)
        {
            if (meshes.Count == 0)
                return 0;

            var labels = BuildStructuralMergeComponentLabels(meshes, preExistingPairs);
            return labels.Distinct().Count();
        }

        private static int[] BuildStructuralMergeComponentLabels(
            IReadOnlyList<MeshKey> meshes,
            IReadOnlyCollection<string> preExistingPairs)
        {
            var parent = Enumerable.Range(0, meshes.Count).ToArray();

            int Find(int index)
            {
                while (parent[index] != index)
                {
                    parent[index] = parent[parent[index]];
                    index = parent[index];
                }

                return index;
            }

            void Union(int left, int right)
            {
                var leftRoot = Find(left);
                var rightRoot = Find(right);
                if (leftRoot != rightRoot)
                    parent[rightRoot] = leftRoot;
            }

            for (var left = 0; left < meshes.Count; left++)
            {
                for (var right = left + 1; right < meshes.Count; right++)
                {
                    if (preExistingPairs.Contains(
                            BuildStructuralMergePairKey(
                                meshes[left].GeometryPath,
                                meshes[left].LodIndex,
                                meshes[left].PartIndex,
                                meshes[right].PartIndex)))
                    {
                        Union(left, right);
                    }
                }
            }

            var componentByRoot = new Dictionary<int, int>();
            var labels = new int[meshes.Count];
            for (var index = 0; index < meshes.Count; index++)
            {
                var root = Find(index);
                if (!componentByRoot.TryGetValue(root, out var component))
                {
                    component = componentByRoot.Count;
                    componentByRoot[root] = component;
                }

                labels[index] = component;
            }

            return labels;
        }

        private static int[] CollapseStructuralMergeCounts(
            IReadOnlyList<MeshKey> meshes,
            IReadOnlyList<int> counts,
            IReadOnlyCollection<string> preExistingPairs)
        {
            var labels = BuildStructuralMergeComponentLabels(meshes, preExistingPairs);
            var collapsed = new int[labels.Distinct().Count()];
            for (var index = 0; index < labels.Length; index++)
                collapsed[labels[index]] = Math.Max(collapsed[labels[index]], counts[index]);

            return collapsed;
        }

        private static double[] CollapseStructuralMergeCounts(
            IReadOnlyList<MeshKey> meshes,
            IReadOnlyList<double> counts,
            IReadOnlyCollection<string> preExistingPairs)
        {
            var labels = BuildStructuralMergeComponentLabels(meshes, preExistingPairs);
            var collapsed = new double[labels.Distinct().Count()];
            for (var index = 0; index < labels.Length; index++)
                collapsed[labels[index]] = Math.Max(collapsed[labels[index]], counts[index]);

            return collapsed;
        }

        private static int CalculateMergeAffinityScore(
            IReadOnlyList<List<AtlasCandidate>> batches,
            IReadOnlyList<MergeAffinityGroup> affinityGroups)
        {
            var batchByMesh = BuildBatchIndexByMesh(batches);
            var score = 0;

            foreach (var group in affinityGroups)
                score += CalculateMergeAffinityContribution(group, batchByMesh);

            return score;
        }

        private static Dictionary<
            MeshKey,
            Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>>
            BuildExpectedArmyEntitiesByMesh(
                ArmyResidencyModel? model,
                IEnumerable<AtlasCandidate> candidates,
                IReadOnlyDictionary<MeshKey, HashSet<string>> rootsByMesh,
                CancellationToken cancellationToken = default)
        {
            var result = new Dictionary<
                MeshKey,
                Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>>();
            if (model == null)
                return result;

            foreach (var candidate in candidates
                         .GroupBy(candidate => candidate.Key)
                         .Select(group => group.First()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wsModels = candidate.Usages
                    .Select(usage => Normalize(usage.AssetPath))
                    .Where(path => path.Length != 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (wsModels.Length == 0)
                    continue;

                var fallbackRoots = rootsByMesh.TryGetValue(
                        candidate.Key,
                        out var candidateRoots)
                    ? candidateRoots
                        .Select(Normalize)
                        .Where(path => path.Length != 0)
                        .ToArray()
                    : Array.Empty<string>();
                var relevantUnitIds = GetRelevantArmyUnitIds(
                    model,
                    wsModels,
                    fallbackRoots);
                if (relevantUnitIds.Count == 0)
                    continue;

                Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>? byCategory = null;
                foreach (var unitId in relevantUnitIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!model.CategoryByUnit.TryGetValue(unitId, out var category))
                        continue;

                    double expectedRenderedEntities = 0;
                    if (model.ExpectedWsModelOccurrencesByUnit.TryGetValue(
                            unitId,
                            out var expectedOccurrences))
                    {
                        foreach (var wsModelPath in wsModels)
                        {
                            if (!expectedOccurrences.ByWsModel.TryGetValue(
                                    wsModelPath,
                                    out var occurrencesByRole))
                            {
                                continue;
                            }

                            foreach (var (role, expectedOccurrencesPerEntity) in occurrencesByRole)
                            {
                                var entityCount = Math.Max(
                                    1,
                                    model.EntityCountByUnitAndRole
                                        .GetValueOrDefault(unitId)?
                                        .GetValueOrDefault(role, 1) ?? 1);
                                expectedRenderedEntities +=
                                    Math.Clamp(expectedOccurrencesPerEntity, 0.0, 1.0) *
                                    entityCount;
                            }
                        }
                    }
                    else
                    {
                        // Preserve the old conservative behavior when probability data could
                        // not be built for a resolved unit.
                        foreach (var root in fallbackRoots)
                        {
                            if (model.UnitsByVmd.TryGetValue(root, out var unitsForVmd) &&
                                unitsForVmd.TryGetValue(category, out var fallbackUnits) &&
                                fallbackUnits.Contains(unitId))
                            {
                                expectedRenderedEntities = Math.Max(
                                    1,
                                    model.EntityCountByUnit.GetValueOrDefault(unitId, 1));
                                break;
                            }
                        }
                    }

                    if (expectedRenderedEntities <= 0)
                        continue;

                    byCategory ??=
                        new Dictionary<
                            Wh3ArmyUnitCategory,
                            Dictionary<string, double>>();
                    if (!byCategory.TryGetValue(category, out var expectedByUnit))
                    {
                        expectedByUnit =
                            new Dictionary<string, double>(
                                StringComparer.OrdinalIgnoreCase);
                        byCategory[category] = expectedByUnit;
                    }

                    expectedByUnit[unitId] = expectedRenderedEntities;
                }

                if (byCategory != null)
                    result[candidate.Key] = byCategory;
            }

            return result;
        }

        private static double CalculateExpectedConfigurationMergeDrawSavings(
            double[] probabilities,
            int[][] coRenderedCountsByConfiguration,
            int entityCount)
        {
            if (entityCount <= 0 ||
                probabilities.Length == 0 ||
                coRenderedCountsByConfiguration.Length == 0)
            {
                return 0;
            }

            var configurationCount = Math.Min(
                probabilities.Length,
                coRenderedCountsByConfiguration.Length);
            double total = 0;
            for (var index = 0; index < configurationCount; index++)
            {
                var probability = probabilities[index];
                if (probability <= 0)
                    continue;

                var positiveCounts = coRenderedCountsByConfiguration[index]
                    .Where(count => count > 0)
                    .ToArray();
                if (positiveCounts.Length < 2)
                    continue;

                total += probability *
                    entityCount *
                    positiveCounts.Min() *
                    (positiveCounts.Length - 1);
            }

            return total;
        }

        private static double CalculateExpectedArmyDrawCallsEliminated(
            BatchState state,
            IReadOnlyDictionary<MeshKey, int> batchByMesh,
            IReadOnlyList<MergeAffinityGroup> affinityGroups,
            IReadOnlyDictionary<
                MeshKey,
                Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>>
                expectedEntitiesByMesh)
        {
            var model = state.ArmyResidencyModel;
            if (model == null)
                return 0;

            state.CancellationToken.ThrowIfCancellationRequested();

            var drawsByCulture = model.UnitsByCultureAndCategory.Keys.ToDictionary(
                culture => culture,
                _ => 0.0,
                StringComparer.OrdinalIgnoreCase);

            foreach (var group in affinityGroups)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                foreach (var colocatedMeshes in group.Meshes
                             .Where(batchByMesh.ContainsKey)
                             .GroupBy(mesh => batchByMesh[mesh]))
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    var meshes = colocatedMeshes.ToArray();
                    if (meshes.Length < 2)
                        continue;

                    var structuralComponentCount =
                        CountStructuralMergeComponents(
                            meshes,
                            group.PreExistingStructuralMergePairs);
                    if (structuralComponentCount < 2)
                        continue;

                    var lodIndex = meshes[0].LodIndex;
                    if (model.Scenario.GetLodProbability(lodIndex) <= 0)
                        continue;

                    var assetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var mesh in meshes)
                    {
                        if (!state.Usages.TryGetValue(mesh, out var usages))
                            continue;

                        foreach (var assetPath in usages
                                     .Select(usage => Normalize(usage.AssetPath))
                                     .Where(path => path.Length != 0))
                        {
                            assetPaths.Add(assetPath);
                        }
                    }

                    var relevantUnitIds = GetRelevantArmyUnitIds(
                        model,
                        assetPaths,
                        Array.Empty<string>());
                    // Keep the expected-entity fallback conservative if a candidate's asset
                    // path was not present in the resolved dependency index.
                    foreach (var mesh in meshes)
                    {
                        if (!expectedEntitiesByMesh.TryGetValue(mesh, out var byCategory))
                            continue;

                        foreach (var expectedByUnit in byCategory.Values)
                            relevantUnitIds.UnionWith(expectedByUnit.Keys);
                    }

                    var relevantCultures = GetRelevantArmyCultures(
                        model,
                        assetPaths,
                        Array.Empty<string>(),
                        relevantUnitIds);
                    if (relevantUnitIds.Count == 0 || relevantCultures.Count == 0)
                        continue;

                    foreach (var culture in relevantCultures)
                    {
                        state.CancellationToken.ThrowIfCancellationRequested();
                        if (!model.UnitsByCultureAndCategory.TryGetValue(
                                culture,
                                out var unitsByCategory))
                        {
                            continue;
                        }

                        foreach (var (category, slotCount) in model.Scenario.ArmySlotTemplate)
                        {
                            state.CancellationToken.ThrowIfCancellationRequested();
                            if (!unitsByCategory.TryGetValue(category, out var cultureUnits))
                                continue;

                            var population = cultureUnits.Count;
                            if (population == 0)
                                continue;

                            double eliminatedDrawsAcrossResolvedUnits = 0;
                            foreach (var unitId in EnumerateRelevantArmyUnitIds(
                                         cultureUnits,
                                         relevantUnitIds))
                            {
                                state.CancellationToken.ThrowIfCancellationRequested();
                                var usedExactConfigurations = false;
                                if (model.VisualConfigurationsByUnitAndRole.TryGetValue(
                                        unitId,
                                        out var configurationsByRole) &&
                                    configurationsByRole.Count != 0)
                                {
                                    usedExactConfigurations = true;
                                    foreach (var (role, configurations) in configurationsByRole)
                                    {
                                        state.CancellationToken.ThrowIfCancellationRequested();
                                        var entityCount = Math.Max(
                                            1,
                                            model.EntityCountByUnitAndRole
                                                .GetValueOrDefault(unitId)?
                                                .GetValueOrDefault(role, 1) ?? 1);

                                        var configurationProbabilities = new List<double>();
                                        var coRenderedCountsByConfiguration = new List<int[]>();
                                        foreach (var configuration in configurations)
                                        {
                                            state.CancellationToken.ThrowIfCancellationRequested();
                                            if (configuration.Probability <= 0)
                                                continue;

                                            var coRenderedCounts = new int[meshes.Length];
                                            for (var meshIndex = 0; meshIndex < meshes.Length; meshIndex++)
                                            {
                                                var mesh = meshes[meshIndex];
                                                if (!state.Usages.TryGetValue(mesh, out var usages))
                                                    continue;

                                                coRenderedCounts[meshIndex] = usages
                                                    .Select(usage => Normalize(usage.AssetPath))
                                                    .Where(path => path.Length != 0)
                                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                                    .Sum(path => configuration.WsModelOccurrences
                                                        .GetValueOrDefault(path));
                                            }

                                            var componentCounts =
                                                CollapseStructuralMergeCounts(
                                                    meshes,
                                                    coRenderedCounts,
                                                    group.PreExistingStructuralMergePairs);

                                            configurationProbabilities.Add(configuration.Probability);
                                            coRenderedCountsByConfiguration.Add(componentCounts);
                                        }

                                        eliminatedDrawsAcrossResolvedUnits +=
                                            CalculateExpectedConfigurationMergeDrawSavings(
                                                configurationProbabilities.ToArray(),
                                                coRenderedCountsByConfiguration.ToArray(),
                                                entityCount);
                                    }
                                }

                                if (usedExactConfigurations)
                                    continue;

                                var expectedCountsByMesh = new double[meshes.Length];
                                for (var meshIndex = 0; meshIndex < meshes.Length; meshIndex++)
                                {
                                    var mesh = meshes[meshIndex];
                                    if (!expectedEntitiesByMesh.TryGetValue(mesh, out var meshUnits) ||
                                        !meshUnits.TryGetValue(category, out var expectedByUnit) ||
                                        !expectedByUnit.TryGetValue(unitId, out var expectedEntities) ||
                                        expectedEntities <= 0)
                                    {
                                        continue;
                                    }

                                    expectedCountsByMesh[meshIndex] = expectedEntities;
                                }

                                var expectedCounts = CollapseStructuralMergeCounts(
                                    meshes,
                                    expectedCountsByMesh,
                                    group.PreExistingStructuralMergePairs)
                                    .Where(count => count > 0)
                                    .ToList();

                                if (expectedCounts.Count >= 2)
                                {
                                    eliminatedDrawsAcrossResolvedUnits +=
                                        expectedCounts.Min() * (expectedCounts.Count - 1);
                                }
                            }

                            if (eliminatedDrawsAcrossResolvedUnits <= 0)
                                continue;

                            drawsByCulture[culture] +=
                                model.Scenario.GetLodWeightedDrawSavings(
                                    lodIndex,
                                    slotCount *
                                    (eliminatedDrawsAcrossResolvedUnits / population));
                        }
                    }
                }
            }

            // Draw savings are additive across the two sides. Unrelated opponent cultures have
            // zero savings for culture-local pack assets; mirror/shared-asset opponents add only
            // when their own culture actually uses the affected meshes.
            return CombineBattleDrawSavings(
                drawsByCulture,
                model.PlayerCultureWeights,
                model.OpponentCultureWeights);
        }

        private static double CalculateExpectedArmyDrawCallsEliminatedForBatches(
            BatchState state,
            IReadOnlyList<List<AtlasCandidate>> batches)
        {
            if (!state.MergeCompatibleMeshesEnabled ||
                state.ArmyResidencyModel == null ||
                batches.Count == 0)
            {
                return 0;
            }

            var candidates = batches
                .SelectMany(batch => batch)
                .GroupBy(candidate => candidate.Key)
                .Select(group => group.First())
                .ToList();
            if (candidates.Count == 0)
                return 0;

            // This is a final accounting pass, not another planning pass. Suppress the
            // prospective-affinity diagnostics so the report does not count the same groups
            // a second time merely because the post-value-gate total is being measured.
            var affinityGroups = BuildMergeAffinityGroups(
                state,
                candidates,
                recordDiagnostics: false);
            if (affinityGroups.Count == 0)
                return 0;

            var rootsByMesh = BuildCandidateRootVmdPaths(state, candidates);
            var expectedArmyEntitiesByMesh = BuildExpectedArmyEntitiesByMesh(
                state.ArmyResidencyModel,
                candidates,
                rootsByMesh,
                state.CancellationToken);
            return CalculateExpectedArmyDrawCallsEliminated(
                state,
                BuildBatchIndexByMesh(batches),
                affinityGroups,
                expectedArmyEntitiesByMesh);
        }

        private static double CombineBattleDrawSavings(
            IReadOnlyDictionary<string, double> drawsByCulture,
            IReadOnlyDictionary<string, double> playerCultureWeights,
            IReadOnlyDictionary<string, double> opponentCultureWeights)
            => GetWeightedCultureAverage(drawsByCulture, playerCultureWeights) +
               GetWeightedCultureAverage(drawsByCulture, opponentCultureWeights);

        private static AtlasBatchCoverage CalculateAtlasBatchCoverage(
            IReadOnlyList<AtlasCandidate> candidates,
            IReadOnlyDictionary<
                MeshKey,
                Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>>
                expectedEntitiesByMesh)
        {
            var affectedUnits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            double expectedRenderedEntities = 0;

            foreach (var candidate in candidates
                         .GroupBy(candidate => candidate.Key)
                         .Select(group => group.First()))
            {
                if (!expectedEntitiesByMesh.TryGetValue(
                        candidate.Key,
                        out var expectedByCategory))
                {
                    continue;
                }

                foreach (var (category, expectedByUnit) in expectedByCategory)
                {
                    foreach (var (unitId, expectedEntities) in expectedByUnit)
                    {
                        if (expectedEntities <= 0)
                            continue;

                        // Include the category in the identity. The same DB unit key can
                        // legitimately appear in more than one visual slot in malformed or
                        // synthetic data, and those are separate residency consumers.
                        affectedUnits.Add($"{category}\u001f{unitId}");
                        expectedRenderedEntities += expectedEntities;
                    }
                }
            }

            return new AtlasBatchCoverage(
                affectedUnits,
                expectedRenderedEntities);
        }

        private static List<List<AtlasCandidate>> OrderBatchesByCoverageEfficiency(
            BatchState state,
            IReadOnlyList<List<AtlasCandidate>> batches,
            IReadOnlyList<MergeAffinityGroup> affinityGroups,
            IReadOnlyDictionary<
                MeshKey,
                Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>>
                expectedEntitiesByMesh)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            return batches
                .Select((batch, originalIndex) =>
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    var coverage = CalculateAtlasBatchCoverage(
                        batch,
                        expectedEntitiesByMesh);
                    var affinity = CalculateMergeAffinityScore(
                        [batch],
                        affinityGroups);
                    var scenarioResolved = IsAtlasValueBatchScenarioResolved(state, batch);
                    var scenarioAllowed = IsAtlasValueCandidateAllowedForMode(
                        state.AtlasAllVmdsEnabled,
                        scenarioResolved);
                    var expectedNetBcnBytes = double.PositiveInfinity;
                    if (scenarioAllowed &&
                        TryEstimateIncrementalAtlasResidency(
                            state,
                            batch,
                            out var residency))
                    {
                        expectedNetBcnBytes = residency.ExpectedArmyNetBcnBytes;
                    }

                    var coveragePerScenarioMiB =
                        GetAffectedUnitsPerScenarioMiB(
                            coverage.AffectedUnitCount,
                            expectedNetBcnBytes);
                    var stableKey = string.Join(
                        "\u001f",
                        batch
                            .Select(candidate => candidate.Key.ToString())
                            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
                    return (
                        Batch: batch,
                        Coverage: coverage,
                        ScenarioAllowed: scenarioAllowed,
                        CoveragePerScenarioMiB: coveragePerScenarioMiB,
                        Affinity: affinity,
                        StableKey: stableKey,
                        OriginalIndex: originalIndex);
                })
                // Greedily spend the value-gate budget on the widest safe unit coverage per
                // scenario-estimated net BCn MiB. This is intentionally a priority heuristic,
                // not a relaxed acceptance rule: every selected batch still passes the full
                // draw/value/memory gate below.
                .OrderByDescending(entry => entry.ScenarioAllowed)
                .ThenByDescending(entry => entry.CoveragePerScenarioMiB)
                .ThenByDescending(entry => entry.Coverage.AffectedUnitCount)
                .ThenByDescending(entry => entry.Coverage.ExpectedRenderedEntities)
                .ThenByDescending(entry => entry.Affinity)
                .ThenBy(entry => entry.StableKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.OriginalIndex)
                .Select(entry => entry.Batch)
                .ToList();
        }

        private static double GetAffectedUnitsPerScenarioMiB(
            int affectedUnitCount,
            double expectedNetBcnBytes)
        {
            if (affectedUnitCount <= 0 ||
                double.IsPositiveInfinity(expectedNetBcnBytes) ||
                double.IsNaN(expectedNetBcnBytes))
            {
                return 0;
            }

            var expectedNetMiB = Math.Max(
                0.0,
                expectedNetBcnBytes / (1024.0 * 1024.0));
            return expectedNetMiB <= AtlasValueGateExpectedDrawEpsilon
                ? double.MaxValue
                : affectedUnitCount / expectedNetMiB;
        }

        private static string FormatAffectedUnitsPerScenarioMiB(
            int affectedUnitCount,
            double expectedNetBcnBytes)
        {
            if (affectedUnitCount <= 0)
                return "0";

            if (double.IsPositiveInfinity(expectedNetBcnBytes) ||
                double.IsNaN(expectedNetBcnBytes))
            {
                return "N/A";
            }

            var expectedNetMiB = expectedNetBcnBytes / (1024.0 * 1024.0);
            return expectedNetMiB <= AtlasValueGateExpectedDrawEpsilon
                ? "retiring"
                : $"{affectedUnitCount / expectedNetMiB:N3}";
        }

        private static HashSet<string> GetAtlasValueGateCandidateTexturePaths(
            IEnumerable<AtlasCandidate> candidates)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                foreach (var channel in AtlasChannels)
                {
                    if (!candidate.ResolvedChannels.Contains(channel.Slot) &&
                        !candidate.ConstantChannels.ContainsKey(channel.Slot))
                    {
                        continue;
                    }

                    var texturePath = Normalize(
                        GetTexturePath(candidate.MaterialDocument, channel.Slot));
                    if (texturePath.Length != 0 && !IsTexturePlaceholder(texturePath))
                        result.Add(texturePath);
                }
            }

            return result;
        }

        private static List<List<AtlasCandidate>> FilterBatchesForMergeValue(
            BatchState state,
            IReadOnlyList<List<AtlasCandidate>> batches)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            if (batches.Count == 0)
                return [];

            var allCandidates = batches
                .SelectMany(batch => batch)
                .GroupBy(candidate => candidate.Key)
                .Select(group => group.First())
                .ToList();
            var candidateTexturePaths = state.AtlasValueGateCandidateTexturePathsInitialized
                ? state.AtlasValueGateCandidateTexturePaths
                : GetAtlasValueGateCandidateTexturePaths(allCandidates);

            ReportProgress(
                state.Progress,
                "Evaluating atlas value",
                item: "Indexing source texture consumers");
            state.AtlasValueGateSourceTextureIndex ??=
                BuildAtlasValueGateSourceTextureIndex(state, candidateTexturePaths);

            HashSet<MeshKey> nonAtomicStructuralCandidates;
            if (state.LodAwareAtlasPlanningEnabled)
            {
                nonAtomicStructuralCandidates =
                    GetNonAtomicStructuralCandidateKeys(state, batches);
                foreach (var candidate in allCandidates
                             .Where(candidate => nonAtomicStructuralCandidates.Contains(candidate.Key)))
                {
                    RecordSkip(
                        state,
                        candidate.RootVmdPath,
                        candidate.Key,
                        candidate.Usages.FirstOrDefault()?.AssetPath ?? string.Empty,
                        "LOD-aware atlas planning left a structural merge component unchanged " +
                        "because its complete component was not present in one atlas family or " +
                        "its post-atlas material variant was not atomic.");
                }
            }
            else
            {
                // Preserve the pre-existing opt-out behavior for callers that explicitly use
                // lodAwareAtlasPlanning=false: structural candidates remain untouched and are
                // merged later by the structural pass.
                var protectedStructuralCandidates = allCandidates
                    .Where(candidate => state.PreAtlasStructuralMergeMeshes.Contains(candidate.Key))
                    .ToList();
                foreach (var candidate in protectedStructuralCandidates)
                {
                    RecordSkip(
                        state,
                        candidate.RootVmdPath,
                        candidate.Key,
                        candidate.Usages.FirstOrDefault()?.AssetPath ?? string.Empty,
                        "Pre-atlas structural merge opportunity protected from atlas rewriting.");
                }

                state.AtlasProtectedStructuralMeshCount +=
                    protectedStructuralCandidates.Count;
                nonAtomicStructuralCandidates = protectedStructuralCandidates
                    .Select(candidate => candidate.Key)
                    .ToHashSet();
            }

            var eligibleBatches = batches
                .Select(batch => batch
                    .Where(candidate =>
                        !nonAtomicStructuralCandidates.Contains(candidate.Key))
                    .ToList())
                .Where(batch => batch.Count != 0)
                .ToList();
            allCandidates = eligibleBatches
                .SelectMany(batch => batch)
                .GroupBy(candidate => candidate.Key)
                .Select(group => group.First())
                .ToList();
            var candidateByKey = allCandidates.ToDictionary(candidate => candidate.Key);
            var affinityGroups = BuildMergeAffinityGroups(state, allCandidates);

            var rootsByMesh = BuildCandidateRootVmdPaths(state, allCandidates);
            var expectedEntitiesByMesh = BuildExpectedArmyEntitiesByMesh(
                state.ArmyResidencyModel,
                allCandidates,
                rootsByMesh,
                state.CancellationToken);
            var candidateCoverage = CalculateAtlasBatchCoverage(
                allCandidates,
                expectedEntitiesByMesh);
            state.AtlasValueGateCandidateAffectedUnitCount =
                candidateCoverage.AffectedUnitCount;
            var orderedBatches = OrderBatchesByCoverageEfficiency(
                state,
                eligibleBatches,
                affinityGroups,
                expectedEntitiesByMesh);
            state.AtlasValueGateCoverageOrderedBatchCount = orderedBatches.Count;
            var result = new List<List<AtlasCandidate>>();
            var pendingTextureMergeAttachments =
                new List<AtlasValueGatePendingTextureMergeAttachment>();
            var acceptedEconomicsStartIndex =
                state.AtlasValueGateAcceptedBatchEconomics.Count;

            for (var orderedBatchIndex = 0;
                 orderedBatchIndex < orderedBatches.Count;
                 orderedBatchIndex++)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                ReportProgress(
                    state.Progress,
                    "Evaluating atlas value",
                    orderedBatchIndex + 1,
                    orderedBatches.Count,
                    $"Value gate batch {orderedBatchIndex + 1}");
                var originalBatch = orderedBatches[orderedBatchIndex];
                var batch = new List<AtlasCandidate>(originalBatch.Count);
                foreach (var candidate in originalBatch)
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    var scenarioResolved =
                        IsAtlasValueCandidateScenarioResolved(state, candidate);
                    if (!IsAtlasValueCandidateAllowedForMode(
                            state.AtlasAllVmdsEnabled,
                            scenarioResolved))
                    {
                        RecordSkip(
                            state,
                            candidate.RootVmdPath,
                            candidate.Key,
                            candidate.Usages.FirstOrDefault()?.AssetPath ?? string.Empty,
                            "Atlas value gate: gameplay/scenario relevance could not be " +
                            "resolved safely; gameplay-used mode leaves this asset unchanged.");
                        state.AtlasValueGateCandidatesRejected++;
                        state.AtlasValueGateUnresolvedCandidatesSkipped++;
                        continue;
                    }

                    batch.Add(candidate);
                }

                if (batch.Count < 2)
                    continue;

                var batchKeys = batch.Select(candidate => candidate.Key).ToHashSet();
                var contributingGroups = affinityGroups
                    .Select(group => FilterMergeAffinityGroup(group, batchKeys))
                    .Where(group => group.Meshes.Length >= 2)
                    .ToList();
                // A closed LOD family is one execution unit. In particular, a member that
                // contributes no same-LOD draw merge must not fall into the texture-only path
                // and be emitted as a second physical atlas batch. Expand the merge host plan
                // with all complete family members before any value-gate split occurs.
                var groupPlans = BuildAtlasValueGateGroupPlans(
                    state,
                    contributingGroups,
                    candidateByKey,
                    batchKeys);
                var contributingKeys = groupPlans
                    .SelectMany(plan => plan.Candidates)
                    .Select(candidate => candidate.Key)
                    .ToHashSet();

                var textureOnlyCandidates = batch
                    .Where(candidate => !contributingKeys.Contains(candidate.Key))
                    .ToList();
                if (TryAcceptTextureOnlyAtlasBatch(
                        state,
                        textureOnlyCandidates,
                        out var textureOnlyResidency,
                        out var textureOnlyRejectionReason))
                {
                    result.Add(textureOnlyCandidates);
                    RecordAtlasValueGateAccepted(
                        state,
                        textureOnlyCandidates,
                        textureOnlyResidency,
                        rawDrawsEliminated: 0,
                        expectedArmyDrawsEliminated: 0,
                        expectedEntitiesByMesh,
                        acceptanceKind: AtlasValueGateAcceptanceKind.TextureOnlyRetirement);
                }
                else
                {
                    var textureConsolidationAcceptances =
                        SelectTextureConsolidationCohorts(
                            state,
                            textureOnlyCandidates,
                            expectedEntitiesByMesh,
                            out var textureConsolidationRejectionReasons);
                    foreach (var acceptance in textureConsolidationAcceptances)
                    {
                        state.CancellationToken.ThrowIfCancellationRequested();
                        result.Add(acceptance.Candidates);
                    }

                    var acceptedTextureConsolidationKeys =
                        textureConsolidationAcceptances
                            .SelectMany(acceptance => acceptance.Candidates)
                            .Select(candidate => candidate.Key)
                            .ToHashSet();
                    foreach (var candidate in textureOnlyCandidates)
                    {
                        state.CancellationToken.ThrowIfCancellationRequested();
                        if (acceptedTextureConsolidationKeys.Contains(candidate.Key))
                            continue;

                        var rejectionReason =
                            textureConsolidationRejectionReasons.GetValueOrDefault(
                                candidate.Key,
                                "no viable texture-consolidation cohort remained.");
                        RecordSkip(
                            state,
                            candidate.RootVmdPath,
                            candidate.Key,
                            candidate.Usages.FirstOrDefault()?.AssetPath ?? string.Empty,
                            "Atlas value gate: this mesh does not contribute to a compatible " +
                            $"mesh merge; texture-only fallback rejected: {textureOnlyRejectionReason}; " +
                            $"texture-consolidation fallback rejected: {rejectionReason}");
                        state.AtlasValueGateCandidatesRejected++;
                    }
                }

                var trimmed = batch
                    .Where(candidate => contributingKeys.Contains(candidate.Key))
                    .ToList();
                if (trimmed.Count < 2)
                    continue;

                if (groupPlans.Count == 0)
                    continue;

                var standaloneAccepted = new List<AtlasValueGateAcceptedGroup>();
                var standaloneRejected = new List<AtlasValueGateRejectedGroup>();

                foreach (var plan in groupPlans)
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    if (TryAcceptAtlasValueBatch(
                            state,
                            plan.Candidates,
                            plan.AffinityGroups,
                            expectedEntitiesByMesh,
                            out var residency,
                            out var rawDraws,
                            out var expectedDraws,
                            out var rejectionReason))
                    {
                        standaloneAccepted.Add(
                            new AtlasValueGateAcceptedGroup(
                                plan,
                                residency,
                                rawDraws,
                                expectedDraws,
                                WasMarginalOnly: false,
                                AcceptanceKind: AtlasValueGateAcceptanceKind.DrawMerge));
                    }
                    else
                    {
                        var candidateResidency = residency;
                        var candidateRejectionReason = rejectionReason;
                        if (GetAtlasValueGatePlanProspectiveTextureMergeDraws(plan) > 0)
                        {
                            var textureMergeAccepted =
                                TryAcceptTextureOnlyMergeAtlasBatch(
                                    state,
                                    plan,
                                    expectedEntitiesByMesh,
                                    out var textureMergeResidency,
                                    out var textureMergeRawDraws,
                                    out var textureMergeExpectedDraws,
                                    out var textureMergeRejectionReason);
                            if (textureMergeAccepted)
                            {
                                standaloneAccepted.Add(
                                    new AtlasValueGateAcceptedGroup(
                                        plan,
                                        textureMergeResidency,
                                        textureMergeRawDraws,
                                        textureMergeExpectedDraws,
                                        WasMarginalOnly: false,
                                        AcceptanceKind:
                                            AtlasValueGateAcceptanceKind.TextureOnlyMerge));
                                continue;
                            }

                            candidateResidency = textureMergeResidency;
                            candidateRejectionReason =
                                $"{rejectionReason}; texture-only merge credit rejected: " +
                                textureMergeRejectionReason;
                        }

                        standaloneRejected.Add(
                            new AtlasValueGateRejectedGroup(
                                plan,
                                candidateResidency,
                                candidateRejectionReason));
                    }
                }

                if (standaloneAccepted.Count == 0)
                {
                    if (groupPlans.Count > 1)
                        state.AtlasValueGateBroadBatchesSplit++;

                    foreach (var rejected in standaloneRejected)
                    {
                        state.CancellationToken.ThrowIfCancellationRequested();
                    if (GetAtlasValueGatePlanProspectiveTextureMergeDraws(rejected.Plan) > 0)
                        {
                            pendingTextureMergeAttachments.Add(
                                new AtlasValueGatePendingTextureMergeAttachment(
                                    rejected.Plan,
                                    rejected.Residency,
                                    rejected.RejectionReason));
                        }
                        else
                        {
                            RejectAtlasValueGateGroup(
                                state,
                                rejected.Plan,
                                rejected.Residency,
                                rejected.RejectionReason);
                        }
                    }

                    continue;
                }

                // A standalone texture-only merge has a deliberately separate budget because
                // its source textures may remain reachable through unrelated materials. Keep
                // standalone texture-merge groups isolated from normal groups; marginal
                // attachments are handled later against the already accepted atlas cost.
                if (standaloneAccepted.Any(group =>
                        group.AcceptanceKind == AtlasValueGateAcceptanceKind.TextureOnlyMerge))
                {
                    if (groupPlans.Count > 1)
                        state.AtlasValueGateBroadBatchesSplit++;

                    foreach (var accepted in standaloneAccepted)
                    {
                        state.CancellationToken.ThrowIfCancellationRequested();
                        if (TryRecheckStandaloneAcceptedAtlasValueGateGroup(
                                state,
                                accepted,
                                expectedEntitiesByMesh,
                                out var sequentialResidency,
                                out var sequentialRawDraws,
                                out var sequentialExpectedDraws,
                                out var sequentialRejectionReason))
                        {
                            result.Add(accepted.Plan.Candidates);
                            RecordAtlasValueGateAccepted(
                                state,
                                accepted.Plan.Candidates,
                                sequentialResidency,
                                sequentialRawDraws,
                                sequentialExpectedDraws,
                                expectedEntitiesByMesh,
                                accepted.AcceptanceKind);
                            continue;
                        }

                        // The initial standalone estimates were all made against the same
                        // pre-acceptance reference set. Rechecking here makes a later group
                        // pay for the consumer union left after earlier groups were accepted.
                        // A failed texture-only recheck remains eligible for the normal
                        // cross-batch attachment pass; a draw merge is rejected outright.
                        if (GetAtlasValueGatePlanProspectiveTextureMergeDraws(accepted.Plan) > 0)
                        {
                            pendingTextureMergeAttachments.Add(
                                new AtlasValueGatePendingTextureMergeAttachment(
                                    accepted.Plan,
                                    sequentialResidency,
                                    "sequential standalone recheck: " +
                                    sequentialRejectionReason));
                        }
                        else
                        {
                            RejectAtlasValueGateGroup(
                                state,
                                accepted.Plan,
                                sequentialResidency,
                                "sequential standalone recheck: " +
                                sequentialRejectionReason);
                        }
                    }

                    foreach (var rejected in standaloneRejected)
                    {
                        if (GetAtlasValueGatePlanProspectiveTextureMergeDraws(rejected.Plan) > 0)
                        {
                            pendingTextureMergeAttachments.Add(
                                new AtlasValueGatePendingTextureMergeAttachment(
                                    rejected.Plan,
                                    rejected.Residency,
                                    rejected.RejectionReason));
                        }
                        else
                        {
                            RejectAtlasValueGateGroup(
                                state,
                                rejected.Plan,
                                rejected.Residency,
                                rejected.RejectionReason);
                        }
                    }

                    continue;
                }

                var workingAccepted = new List<AtlasValueGateAcceptedGroup>(
                    standaloneAccepted);
                var workingCandidates = standaloneAccepted
                    .SelectMany(group => group.Plan.Candidates)
                    .DistinctBy(candidate => candidate.Key)
                    .ToList();
                var workingGroups = standaloneAccepted
                    .SelectMany(group => group.Plan.AffinityGroups)
                    .ToList();

                foreach (var rejected in standaloneRejected)
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    var marginalRawDraws = 0;
                    var marginalExpectedDraws = 0.0;
                    string marginalReason;
                    AtlasValueGateAcceptanceKind marginalAcceptanceKind;
                    var marginalAccepted = false;
                    if (GetAtlasValueGatePlanProspectiveTextureMergeDraws(rejected.Plan) > 0)
                    {
                        marginalAcceptanceKind =
                            AtlasValueGateAcceptanceKind.TextureOnlyMerge;
                        marginalAccepted = TryAcceptTextureOnlyMergeMarginalAddition(
                            state,
                            workingCandidates,
                            rejected.Plan.Candidates,
                            workingGroups,
                            rejected.Plan.AffinityGroups,
                            expectedEntitiesByMesh,
                            out marginalRawDraws,
                            out marginalExpectedDraws,
                            out marginalReason);
                    }
                    else
                    {
                        marginalAcceptanceKind =
                            AtlasValueGateAcceptanceKind.DrawMerge;
                        marginalAccepted = TryAcceptAtlasValueMarginalAddition(
                            state,
                            workingCandidates,
                            rejected.Plan.Candidates,
                            workingGroups,
                            rejected.Plan.AffinityGroups,
                            expectedEntitiesByMesh,
                            out marginalReason);
                    }

                    if (marginalAccepted)
                    {
                        workingAccepted.Add(
                            new AtlasValueGateAcceptedGroup(
                                rejected.Plan,
                                rejected.Residency,
                                marginalRawDraws,
                                marginalExpectedDraws,
                                WasMarginalOnly: true,
                                AcceptanceKind: marginalAcceptanceKind));
                        workingCandidates.AddRange(
                            rejected.Plan.Candidates.Where(candidate =>
                                workingCandidates.All(existing =>
                                    existing.Key != candidate.Key)));
                        workingGroups.AddRange(rejected.Plan.AffinityGroups);
                        state.AtlasValueGateMarginalGroupsAccepted++;
                    }
                    else
                    {
                        if (GetAtlasValueGatePlanProspectiveTextureMergeDraws(rejected.Plan) > 0)
                        {
                            pendingTextureMergeAttachments.Add(
                                new AtlasValueGatePendingTextureMergeAttachment(
                                    rejected.Plan,
                                    rejected.Residency,
                                    $"mixed-batch marginal check: {marginalReason}"));
                        }
                        else
                        {
                            RejectAtlasValueGateGroup(
                                state,
                                rejected.Plan,
                                rejected.Residency,
                                $"mixed-batch marginal check: {marginalReason}");
                            state.AtlasValueGateMixedGroupsRejected++;
                        }
                    }
                }

                if (workingAccepted.Count == 1)
                {
                    var accepted = workingAccepted[0];
                    result.Add(accepted.Plan.Candidates);
                    RecordAtlasValueGateAccepted(
                        state,
                        accepted.Plan.Candidates,
                        accepted.Residency,
                        accepted.RawDrawsEliminated,
                        accepted.ExpectedArmyDrawsEliminated,
                        expectedEntitiesByMesh,
                        accepted.AcceptanceKind);
                    continue;
                }

                if (TryAcceptAtlasValueBatch(
                        state,
                        workingCandidates,
                        workingGroups,
                        expectedEntitiesByMesh,
                        out var combinedResidency,
                        out var combinedRawDraws,
                        out var combinedExpectedDraws,
                        out var combinedRejectionReason))
                {
                    result.Add(workingCandidates);
                    RecordAtlasValueGateAccepted(
                        state,
                        workingCandidates,
                        combinedResidency,
                        combinedRawDraws,
                        combinedExpectedDraws,
                        expectedEntitiesByMesh);
                    continue;
                }

                var textureOnlyMarginalGroupCount = workingAccepted.Count(
                    group => group.WasMarginalOnly &&
                             group.AcceptanceKind ==
                             AtlasValueGateAcceptanceKind.TextureOnlyMerge);
                var combinedScenarioResolved =
                    IsAtlasValueBatchScenarioResolved(state, workingCandidates);
                var combinedGrowthWithinCap = true;
                if (textureOnlyMarginalGroupCount > 0)
                {
                    var sourceBcnBytes = state.SourceBcnResidency?.BcnBytes ?? 0;
                    if (sourceBcnBytes > 0)
                    {
                        var projectedNetGrowth =
                            state.AtlasValueGateNetBcnBytesAccepted +
                            (double)combinedResidency.NetBcnBytes;
                        combinedGrowthWithinCap = projectedNetGrowth <=
                            sourceBcnBytes * MaxReachableBcnGrowthRatio;
                    }
                }

                // A texture-only marginal attachment has already been checked against the
                // incremental cost of the current accepted atlas. The ordinary aggregate gate
                // can still reject the mixed batch because it prices the entire batch as a
                // standalone draw-merge decision. Preserve the sequential marginal decisions
                // when the combined plan is valid, has real draw benefit, and remains under the
                // cumulative growth cap.
                if (textureOnlyMarginalGroupCount > 0 &&
                    combinedScenarioResolved &&
                    combinedRawDraws > 0 &&
                    combinedExpectedDraws > AtlasValueGateExpectedDrawEpsilon &&
                    combinedGrowthWithinCap)
                {
                    result.Add(workingCandidates);
                    state.AtlasValueGateTextureOnlyMergeMarginalGroupsAccepted +=
                        textureOnlyMarginalGroupCount;
                    RecordAtlasValueGateAccepted(
                        state,
                        workingCandidates,
                        combinedResidency,
                        combinedRawDraws,
                        combinedExpectedDraws,
                        expectedEntitiesByMesh);
                    continue;
                }

                // Even economically sound groups can make a combined atlas worse because of
                // packing/layout effects. Fall back to the groups that passed standalone.
                state.AtlasValueGateBroadBatchesSplit++;
                foreach (var accepted in standaloneAccepted)
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    if (TryAcceptAtlasValueBatch(
                            state,
                            accepted.Plan.Candidates,
                            accepted.Plan.AffinityGroups,
                            expectedEntitiesByMesh,
                            out var standaloneResidency,
                            out var standaloneRawDraws,
                            out var standaloneExpectedDraws,
                            out var standaloneRejectionReason))
                    {
                        result.Add(accepted.Plan.Candidates);
                        RecordAtlasValueGateAccepted(
                            state,
                            accepted.Plan.Candidates,
                            standaloneResidency,
                            standaloneRawDraws,
                            standaloneExpectedDraws,
                            expectedEntitiesByMesh,
                            accepted.AcceptanceKind);
                    }
                    else
                    {
                        RejectAtlasValueGateGroup(
                            state,
                            accepted.Plan,
                            standaloneResidency,
                            $"standalone recheck after broad-batch rejection: " +
                            standaloneRejectionReason);
                    }
                }

                foreach (var marginalOnly in workingAccepted.Where(
                             group => group.WasMarginalOnly))
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    if (marginalOnly.AcceptanceKind ==
                        AtlasValueGateAcceptanceKind.TextureOnlyMerge)
                    {
                        pendingTextureMergeAttachments.Add(
                            new AtlasValueGatePendingTextureMergeAttachment(
                                marginalOnly.Plan,
                                marginalOnly.Residency,
                                "marginal sharing required a broad batch that was rejected: " +
                                combinedRejectionReason));
                    }
                    else
                    {
                        RejectAtlasValueGateGroup(
                            state,
                            marginalOnly.Plan,
                            marginalOnly.Residency,
                            $"marginal sharing required a broad batch that was rejected: " +
                            combinedRejectionReason);
                        state.AtlasValueGateMixedGroupsRejected++;
                    }
                }
            }

            AttachPendingTextureOnlyMergeGroups(
                state,
                result,
                affinityGroups,
                expectedEntitiesByMesh,
                pendingTextureMergeAttachments,
                acceptedEconomicsStartIndex);

            return result;
        }

        private static bool TryRecheckStandaloneAcceptedAtlasValueGateGroup(
            BatchState state,
            AtlasValueGateAcceptedGroup accepted,
            IReadOnlyDictionary<
                MeshKey,
                Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>>
                expectedEntitiesByMesh,
            out AtlasValueGateResidencyEstimate residency,
            out int rawDrawsEliminated,
            out double expectedArmyDrawsEliminated,
            out string rejectionReason)
        {
            if (accepted.AcceptanceKind == AtlasValueGateAcceptanceKind.TextureOnlyMerge)
            {
                return TryAcceptTextureOnlyMergeAtlasBatch(
                    state,
                    accepted.Plan,
                    expectedEntitiesByMesh,
                    out residency,
                    out rawDrawsEliminated,
                    out expectedArmyDrawsEliminated,
                    out rejectionReason);
            }

            return TryAcceptAtlasValueBatch(
                state,
                accepted.Plan.Candidates,
                accepted.Plan.AffinityGroups,
                expectedEntitiesByMesh,
                out residency,
                out rawDrawsEliminated,
                out expectedArmyDrawsEliminated,
                out rejectionReason);
        }

        private static void RejectAtlasValueGateGroup(
            BatchState state,
            AtlasValueGateGroupPlan plan,
            AtlasValueGateResidencyEstimate residency,
            string rejectionReason)
        {
            state.AtlasValueGateBatchesRejected++;
            state.AtlasValueGateCandidatesRejected += plan.Candidates.Count;
            RecordAtlasValueGateRejected(state, residency);

            foreach (var candidate in plan.Candidates)
            {
                RecordSkip(
                    state,
                    candidate.RootVmdPath,
                    candidate.Key,
                    candidate.Usages.FirstOrDefault()?.AssetPath ?? string.Empty,
                    $"Atlas value gate: {rejectionReason}");
            }
        }

        private static void RecordAtlasValueGateSourceTextureResidencyDiagnostics(
            BatchState state,
            IReadOnlyList<AtlasValueGateSourceTextureResidencyDiagnostic> diagnostics)
        {
            foreach (var diagnostic in diagnostics)
            {
                var texturePath = Normalize(diagnostic.TexturePath);
                if (texturePath.Length == 0)
                    continue;

                if (!state.AtlasValueGateSourceTextureResidencyDiagnostics.TryGetValue(
                        texturePath,
                        out var existing))
                {
                    state.AtlasValueGateSourceTextureResidencyDiagnostics[texturePath] =
                        diagnostic with { TexturePath = texturePath };
                    continue;
                }

                // A texture can participate in multiple accepted batches. Preserve the
                // first accepted evaluation as Pbefore and advance only the post-rewrite
                // side, while accumulating only the actual incremental credit from each
                // accepted transition. This keeps an earlier conservative/unresolved
                // transition from being reconstructed as credit by a later resolved one.
                var accumulatedExpectedRetirement =
                    AccumulateAtlasValueGateScenarioRetirement(
                        existing.BcnBytes,
                        existing.ExpectedRetiredBcnBytes,
                        diagnostic.ExpectedRetiredBcnBytes);
                state.AtlasValueGateSourceTextureResidencyDiagnostics[texturePath] =
                    existing with
                    {
                        RewrittenReferenceCountAfter =
                            diagnostic.RewrittenReferenceCountAfter,
                        ConsumerMappingResolved =
                            existing.ConsumerMappingResolved &&
                            diagnostic.ConsumerMappingResolved,
                        ProposedResidentProbability =
                            diagnostic.ProposedResidentProbability,
                        ProposedExpectedResidentBcnBytes =
                            diagnostic.ProposedExpectedResidentBcnBytes,
                        ExpectedRetiredBcnBytes =
                            accumulatedExpectedRetirement,
                        ProposedConsumerWsModels =
                            diagnostic.ProposedConsumerWsModels,
                        ProposedScenarioProjection =
                            diagnostic.ProposedScenarioProjection,
                    };
            }
        }

        private static double AccumulateAtlasValueGateScenarioRetirement(
            long bcnBytes,
            double accumulatedRetiredBytes,
            double incrementalRetiredBytes)
        {
            if (bcnBytes <= 0)
                return 0;

            var accumulated = double.IsFinite(accumulatedRetiredBytes)
                ? Math.Max(0.0, accumulatedRetiredBytes)
                : 0.0;
            var incremental = double.IsFinite(incrementalRetiredBytes)
                ? Math.Max(0.0, incrementalRetiredBytes)
                : 0.0;
            return Math.Min((double)bcnBytes, accumulated + incremental);
        }

        private static void RecordAtlasValueGateAccepted(
            BatchState state,
            IReadOnlyList<AtlasCandidate> batch,
            AtlasValueGateResidencyEstimate residency,
            int rawDrawsEliminated,
            double expectedArmyDrawsEliminated,
            IReadOnlyDictionary<
                MeshKey,
                Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>>
                expectedEntitiesByMesh,
            AtlasValueGateAcceptanceKind acceptanceKind = AtlasValueGateAcceptanceKind.DrawMerge,
            int consolidatedTextureAssignments = 0)
        {
            state.AtlasValueGateBatchesAccepted++;
            state.AtlasValueGateCandidatesAccepted += batch.Count;
            state.AtlasValueGateGeneratedBcnBytesAccepted = checked(
                state.AtlasValueGateGeneratedBcnBytesAccepted + residency.GeneratedBcnBytes);
            state.AtlasValueGateRetiredBcnBytesAccepted = checked(
                state.AtlasValueGateRetiredBcnBytesAccepted + residency.RetiredSourceBcnBytes);
            state.AtlasValueGateNetBcnBytesAccepted = checked(
                state.AtlasValueGateNetBcnBytesAccepted + residency.NetBcnBytes);
            state.AtlasValueGateExpectedArmyGeneratedBcnBytesAccepted +=
                residency.ExpectedArmyGeneratedBcnBytes;
            state.AtlasValueGateExpectedArmyRetiredBcnBytesAccepted +=
                residency.ExpectedArmyRetiredSourceBcnBytes;
            state.AtlasValueGateExpectedArmyNetBcnBytesAccepted +=
                residency.ExpectedArmyNetBcnBytes;
            state.AtlasValueGateExpectedDrawsAccepted += expectedArmyDrawsEliminated;
            RecordAtlasValueGateSourceTextureResidencyDiagnostics(
                state,
                residency.SourceTextureResidencyDiagnostics);
            if (acceptanceKind == AtlasValueGateAcceptanceKind.TextureOnlyMerge)
            {
                state.AtlasValueGateTextureOnlyMergeBatchesAccepted++;
                state.AtlasValueGateTextureOnlyMergeCandidatesAccepted += batch.Count;
            }
            else if (acceptanceKind == AtlasValueGateAcceptanceKind.TextureOnlyRetirement)
            {
                state.AtlasValueGateTextureOnlyBatchesAccepted++;
                state.AtlasValueGateTextureOnlyCandidatesAccepted += batch.Count;
                state.AtlasValueGateTextureOnlyPackReferenceRetiredSourceTextureCount +=
                    residency.PackReferenceRetiredSourceTextureCount;
                state.AtlasValueGateTextureOnlyRetiredSourceTextureCount +=
                    residency.RetiredSourceTextureCount;
                state.AtlasValueGateTextureOnlyScenarioDisplacedSourceTextureCount +=
                    residency.ScenarioDisplacedSourceTextureCount;
                state.AtlasValueGateTextureOnlyExpectedRetiredSourceTextureCount +=
                    residency.ExpectedArmyRetiredSourceTextureCount;
                state.AtlasValueGateTextureOnlyRetiredBcnBytesAccepted = checked(
                    state.AtlasValueGateTextureOnlyRetiredBcnBytesAccepted +
                    residency.RetiredSourceBcnBytes);
                state.AtlasValueGateTextureOnlyExpectedRetiredBcnBytesAccepted +=
                    residency.ExpectedArmyRetiredSourceBcnBytes;
            }
            else if (acceptanceKind == AtlasValueGateAcceptanceKind.TextureConsolidation)
            {
                state.AtlasValueGateTextureConsolidationBatchesAccepted++;
                state.AtlasValueGateTextureConsolidationCandidatesAccepted += batch.Count;
                state.AtlasValueGateTextureConsolidatedAssignments = checked(
                    state.AtlasValueGateTextureConsolidatedAssignments +
                    consolidatedTextureAssignments);
            }

            var coverage = CalculateAtlasBatchCoverage(batch, expectedEntitiesByMesh);
            state.AtlasValueGateAcceptedAffectedUnits.UnionWith(
                coverage.AffectedUnits);

            var roots = batch
                .Select(candidate => Normalize(candidate.RootVmdPath))
                .Where(path => path.Length != 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            state.AtlasValueGateAcceptedBatchEconomics.Add(
                new AtlasValueGateBatchEconomics(
                    state.AtlasValueGateBatchesAccepted,
                    batch.Count,
                    roots,
                    acceptanceKind,
                    coverage.AffectedUnitCount,
                    coverage.ExpectedRenderedEntities,
                    residency.GeneratedBcnBytes,
                    residency.RetiredSourceBcnBytes,
                    residency.NetBcnBytes,
                    residency.ExpectedArmyGeneratedBcnBytes,
                    residency.ExpectedArmyRetiredSourceBcnBytes,
                    residency.ExpectedArmyNetBcnBytes,
                    rawDrawsEliminated,
                    expectedArmyDrawsEliminated));

            AddAtlasValueGateRewrittenReferences(
                state,
                residency.RewrittenReferences);
        }

        private static void RecordAtlasValueGateRejected(
            BatchState state,
            AtlasValueGateResidencyEstimate residency)
        {
            state.AtlasValueGateGeneratedBcnBytesRejected = checked(
                state.AtlasValueGateGeneratedBcnBytesRejected + residency.GeneratedBcnBytes);
            state.AtlasValueGateRetiredBcnBytesRejected = checked(
                state.AtlasValueGateRetiredBcnBytesRejected + residency.RetiredSourceBcnBytes);
            state.AtlasValueGateNetBcnBytesRejected = checked(
                state.AtlasValueGateNetBcnBytesRejected + residency.NetBcnBytes);
            state.AtlasValueGateExpectedArmyGeneratedBcnBytesRejected +=
                residency.ExpectedArmyGeneratedBcnBytes;
            state.AtlasValueGateExpectedArmyRetiredBcnBytesRejected +=
                residency.ExpectedArmyRetiredSourceBcnBytes;
            state.AtlasValueGateExpectedArmyNetBcnBytesRejected +=
                residency.ExpectedArmyNetBcnBytes;
        }

        private enum AtlasValueGateAcceptanceKind
        {
            DrawMerge,
            TextureOnlyMerge,
            TextureOnlyRetirement,
            TextureConsolidation,
        }

        private enum AtlasValueGateBudgetDecision
        {
            Accept,
            ScenarioResolvedZeroBenefit,
            FallbackBudgetExceeded,
            ScenarioBudgetExceeded,
            TextureOnlyMergeBudgetExceeded,
            GlobalGrowthCapExceeded,
        }

        private static AtlasValueGateBudgetDecision EvaluateAtlasValueGateBudget(
            bool scenarioResolved,
            int rawDrawsEliminated,
            double expectedArmyDrawsEliminated,
            double globalCostBytes,
            double expectedCostBytes,
            double acceptedNetBcnBytes,
            double proposedNetBcnBytes,
            double sourceBcnBytes)
        {
            if (scenarioResolved)
            {
                if (expectedArmyDrawsEliminated <= AtlasValueGateExpectedDrawEpsilon)
                {
                    if (globalCostBytes > 0)
                        return AtlasValueGateBudgetDecision.ScenarioResolvedZeroBenefit;
                }
                else
                {
                    var expectedBudget =
                        expectedArmyDrawsEliminated * MaxNetBcnBytesPerExpectedArmyDraw;
                    if (expectedCostBytes > expectedBudget)
                        return AtlasValueGateBudgetDecision.ScenarioBudgetExceeded;
                }
            }
            else
            {
                var fallbackBudget =
                    Math.Max(rawDrawsEliminated, 0) *
                    (double)MaxNetBcnBytesPerFallbackDraw;
                if (globalCostBytes > fallbackBudget)
                    return AtlasValueGateBudgetDecision.FallbackBudgetExceeded;
            }

            if (sourceBcnBytes > 0)
            {
                var projectedNetGrowth = acceptedNetBcnBytes + proposedNetBcnBytes;
                var globalGrowthCap = sourceBcnBytes * MaxReachableBcnGrowthRatio;
                if (projectedNetGrowth > globalGrowthCap)
                    return AtlasValueGateBudgetDecision.GlobalGrowthCapExceeded;
            }

            return AtlasValueGateBudgetDecision.Accept;
        }

        private static AtlasValueGateBudgetDecision
            EvaluateTextureOnlyMergeAtlasValueGateBudget(
                bool scenarioResolved,
                int prospectiveTextureMergeDraws,
                double expectedArmyDrawsEliminated,
                double globalCostBytes,
                double expectedCostBytes,
                double packLocalRetirementCreditBytes,
                double acceptedNetBcnBytes,
                double proposedNetBcnBytes,
                double sourceBcnBytes)
        {
            if (!scenarioResolved ||
                prospectiveTextureMergeDraws <= 0 ||
                expectedArmyDrawsEliminated <= AtlasValueGateExpectedDrawEpsilon)
            {
                return AtlasValueGateBudgetDecision.ScenarioResolvedZeroBenefit;
            }

            var structuralBudget = prospectiveTextureMergeDraws *
                                    (double)MaxNetBcnBytesPerTextureOnlyMergeDraw;
            var scenarioDrawBudget = expectedArmyDrawsEliminated *
                                     MaxNetBcnBytesPerTextureOnlyMergeExpectedArmyDraw;
            // Pack-local retirement of an external/vanilla source texture is not physical
            // pack-byte retirement, so it must not increase the global growth budget.
            // It does remove this pack's demand for that source texture, however, and the
            // ordinary texture-only gate already treats that as a bounded value signal.
            // Reuse the same per-retired-texture allowance here, while excluding physically
            // retired textures because their bytes are already subtracted from the costs.
            var effectiveScenarioBudget =
                scenarioDrawBudget + Math.Max(0.0, packLocalRetirementCreditBytes);
            var effectiveGlobalBudget = Math.Max(structuralBudget, scenarioDrawBudget);
            if (globalCostBytes > effectiveGlobalBudget ||
                expectedCostBytes > effectiveScenarioBudget)
            {
                return AtlasValueGateBudgetDecision.TextureOnlyMergeBudgetExceeded;
            }

            if (sourceBcnBytes > 0)
            {
                var projectedNetGrowth = acceptedNetBcnBytes + proposedNetBcnBytes;
                var globalGrowthCap = sourceBcnBytes * MaxReachableBcnGrowthRatio;
                if (projectedNetGrowth > globalGrowthCap)
                    return AtlasValueGateBudgetDecision.GlobalGrowthCapExceeded;
            }

            return AtlasValueGateBudgetDecision.Accept;
        }

        private static double GetPackLocalOnlyRetirementCreditBytes(
            int packReferenceRetiredSourceTextureCount,
            int physicallyRetiredSourceTextureCount)
        {
            var packLocalOnlyRetirements = Math.Max(
                0,
                packReferenceRetiredSourceTextureCount -
                physicallyRetiredSourceTextureCount);
            return packLocalOnlyRetirements *
                   (double)MaxNetBcnBytesPerRetiredSourceTexture;
        }

        private static bool IsAtlasValueCandidateAllowedForMode(
            bool atlasAllVmdsEnabled,
            bool scenarioResolved)
            => atlasAllVmdsEnabled || scenarioResolved;

        private static bool IsAtlasValueCandidateScenarioResolved(
            BatchState state,
            AtlasCandidate candidate)
        {
            var resolution = state.UnitCategoryResolution;
            if (state.ArmyResidencyModel == null ||
                resolution == null ||
                !resolution.IsGameplayResolutionHealthy)
            {
                return false;
            }

            var root = Normalize(candidate.RootVmdPath);
            if (root.Length != 0)
            {
                if (!resolution.IsVmdUsageComplete(root))
                    return false;

                if (resolution.UsagesByVmd.TryGetValue(root, out var usages) &&
                    usages.Count != 0)
                {
                    return true;
                }
            }

            foreach (var usage in candidate.Usages)
            {
                var assetPath = Normalize(usage.AssetPath);
                if (assetPath.Length != 0 &&
                    resolution.DirectAssetUsagesByPath.TryGetValue(
                        assetPath,
                        out var directUsages) &&
                    directUsages.Count != 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsAtlasValueBatchScenarioResolved(
            BatchState state,
            IReadOnlyList<AtlasCandidate> batch)
            => batch.All(candidate =>
                IsAtlasValueCandidateScenarioResolved(state, candidate));


        private static AtlasValueGateBudgetDecision EvaluateMarginalAtlasValueGateBudget(
            bool scenarioResolved,
            int baseRawDrawsEliminated,
            int combinedRawDrawsEliminated,
            double baseExpectedArmyDrawsEliminated,
            double combinedExpectedArmyDrawsEliminated,
            double baseGeneratedBcnBytes,
            double baseRetiredSourceBcnBytes,
            double combinedGeneratedBcnBytes,
            double combinedRetiredSourceBcnBytes,
            double baseExpectedGeneratedBcnBytes,
            double baseExpectedRetiredSourceBcnBytes,
            double combinedExpectedGeneratedBcnBytes,
            double combinedExpectedRetiredSourceBcnBytes,
            double acceptedNetBcnBytes,
            double sourceBcnBytes)
        {
            var marginalRawDraws = Math.Max(
                0,
                combinedRawDrawsEliminated - baseRawDrawsEliminated);
            var marginalExpectedDraws = Math.Max(
                0,
                combinedExpectedArmyDrawsEliminated -
                baseExpectedArmyDrawsEliminated);
            var marginalGenerated = combinedGeneratedBcnBytes - baseGeneratedBcnBytes;
            var marginalRetired =
                combinedRetiredSourceBcnBytes - baseRetiredSourceBcnBytes;
            var marginalExpectedGenerated =
                combinedExpectedGeneratedBcnBytes - baseExpectedGeneratedBcnBytes;
            var marginalExpectedRetired =
                combinedExpectedRetiredSourceBcnBytes -
                baseExpectedRetiredSourceBcnBytes;
            var marginalGlobalCost = GetChargeableAtlasValueGateBytes(
                marginalGenerated,
                marginalRetired);
            var marginalExpectedCost = GetChargeableAtlasValueGateBytes(
                marginalExpectedGenerated,
                marginalExpectedRetired);
            var baseNet = baseGeneratedBcnBytes - baseRetiredSourceBcnBytes;
            var combinedNet =
                combinedGeneratedBcnBytes - combinedRetiredSourceBcnBytes;
            var marginalNet = combinedNet - baseNet;

            return EvaluateAtlasValueGateBudget(
                scenarioResolved,
                marginalRawDraws,
                marginalExpectedDraws,
                marginalGlobalCost,
                marginalExpectedCost,
                acceptedNetBcnBytes + baseNet,
                marginalNet,
                sourceBcnBytes);
        }

        private static (int Raw, double Expected) CalculateAtlasBatchDrawSavings(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates,
            IReadOnlyList<MergeAffinityGroup> affinityGroups,
            IReadOnlyDictionary<
                MeshKey,
                Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>>
                expectedEntitiesByMesh)
        {
            var batches = new List<List<AtlasCandidate>>
            {
                candidates.ToList(),
            };
            var batchByMesh = BuildBatchIndexByMesh(batches);
            return (
                CalculateMergeAffinityScore(batches, affinityGroups),
                CalculateExpectedArmyDrawCallsEliminated(
                    state,
                    batchByMesh,
                    affinityGroups,
                    expectedEntitiesByMesh));
        }

        private static List<MergeAffinityGroup> GetMergeAffinityGroupsForCandidates(
            IReadOnlyList<AtlasCandidate> candidates,
            IReadOnlyList<MergeAffinityGroup> affinityGroups)
        {
            var candidateKeys = candidates
                .Select(candidate => candidate.Key)
                .ToHashSet();
            return affinityGroups
                .Select(group => FilterMergeAffinityGroup(group, candidateKeys))
                .Where(group => group.Meshes.Length >= 2)
                .ToList();
        }

        private static bool TryAcceptTextureOnlyMergeMarginalAddition(
            BatchState state,
            IReadOnlyList<AtlasCandidate> baseCandidates,
            IReadOnlyList<AtlasCandidate> addedCandidates,
            IReadOnlyList<MergeAffinityGroup> baseGroups,
            IReadOnlyList<MergeAffinityGroup> addedGroups,
            IReadOnlyDictionary<
                MeshKey,
                Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>>
                expectedEntitiesByMesh,
            out int marginalRawDrawsEliminated,
            out double marginalExpectedArmyDrawsEliminated,
            out string rejectionReason)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            if (CalculateProspectiveTextureMergeDraws(addedGroups) <= 0)
            {
                marginalRawDrawsEliminated = 0;
                marginalExpectedArmyDrawsEliminated = 0;
                rejectionReason =
                    "marginal texture-only attachment requires a prospective texture merge.";
                return false;
            }

            var combinedGroups = baseGroups
                .Concat(addedGroups)
                .ToList();
            var accepted = TryEvaluateTextureOnlyMergeMarginalAddition(
                state,
                baseCandidates,
                addedCandidates,
                baseGroups,
                combinedGroups,
                expectedEntitiesByMesh,
                baseAlreadyAccepted: false,
                out var evaluation,
                out rejectionReason);
            marginalRawDrawsEliminated =
                evaluation?.MarginalRawDrawsEliminated ?? 0;
            marginalExpectedArmyDrawsEliminated =
                evaluation?.MarginalExpectedArmyDrawsEliminated ?? 0;
            return accepted;
        }

        private static bool TryEvaluateTextureOnlyMergeMarginalAddition(
            BatchState state,
            IReadOnlyList<AtlasCandidate> baseCandidates,
            IReadOnlyList<AtlasCandidate> addedCandidates,
            IReadOnlyList<MergeAffinityGroup> baseGroups,
            IReadOnlyList<MergeAffinityGroup> combinedGroups,
            IReadOnlyDictionary<
                MeshKey,
                Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>>
                expectedEntitiesByMesh,
            bool baseAlreadyAccepted,
            out TextureOnlyMergeMarginalEvaluation? evaluation,
            out string rejectionReason)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            evaluation = null;
            rejectionReason = string.Empty;

            if (baseCandidates.Count == 0 || addedCandidates.Count < 2)
            {
                rejectionReason =
                    "marginal texture-only attachment requires an accepted atlas and " +
                    "at least two added candidates.";
                return false;
            }

            var combinedCandidates = baseCandidates
                .Concat(addedCandidates)
                .DistinctBy(candidate => candidate.Key)
                .ToList();
            if (!TryEstimateIncrementalAtlasResidency(
                    state,
                    baseCandidates,
                    out var baseResidency) ||
                !TryEstimateIncrementalAtlasResidency(
                    state,
                    combinedCandidates,
                    out var combinedResidency))
            {
                rejectionReason =
                    "incremental BCn residency could not be estimated safely.";
                return false;
            }

            var baseSavings = CalculateAtlasBatchDrawSavings(
                state,
                baseCandidates,
                baseGroups,
                expectedEntitiesByMesh);
            var combinedSavings = CalculateAtlasBatchDrawSavings(
                state,
                combinedCandidates,
                combinedGroups,
                expectedEntitiesByMesh);
            var marginalRawDraws = Math.Max(
                0,
                combinedSavings.Raw - baseSavings.Raw);
            var marginalExpectedDraws = Math.Max(
                0,
                combinedSavings.Expected - baseSavings.Expected);
            var marginalProspectiveTextureMergeDraws = Math.Max(
                0,
                CalculateProspectiveTextureMergeDraws(combinedGroups) -
                CalculateProspectiveTextureMergeDraws(baseGroups));
            var marginalGenerated =
                combinedResidency.GeneratedBcnBytes - baseResidency.GeneratedBcnBytes;
            var marginalRetired =
                combinedResidency.RetiredSourceBcnBytes -
                baseResidency.RetiredSourceBcnBytes;
            var marginalExpectedGenerated =
                combinedResidency.ExpectedArmyGeneratedBcnBytes -
                baseResidency.ExpectedArmyGeneratedBcnBytes;
            var marginalExpectedRetired =
                combinedResidency.ExpectedArmyRetiredSourceBcnBytes -
                baseResidency.ExpectedArmyRetiredSourceBcnBytes;
            var marginalGlobalCost = GetChargeableAtlasValueGateBytes(
                marginalGenerated,
                marginalRetired);
            var marginalExpectedCost = GetChargeableAtlasValueGateBytes(
                marginalExpectedGenerated,
                marginalExpectedRetired);
            var marginalNet =
                combinedResidency.NetBcnBytes - baseResidency.NetBcnBytes;
            var marginalPackReferenceRetiredSourceTextureCount = Math.Max(
                0,
                combinedResidency.PackReferenceRetiredSourceTextureCount -
                baseResidency.PackReferenceRetiredSourceTextureCount);
            var marginalPhysicallyRetiredSourceTextureCount = Math.Max(
                0,
                combinedResidency.RetiredSourceTextureCount -
                baseResidency.RetiredSourceTextureCount);
            var marginalPackLocalRetirementCreditBytes =
                GetPackLocalOnlyRetirementCreditBytes(
                    marginalPackReferenceRetiredSourceTextureCount,
                    marginalPhysicallyRetiredSourceTextureCount);

            evaluation = new TextureOnlyMergeMarginalEvaluation(
                combinedCandidates,
                baseResidency,
                combinedResidency,
                baseSavings.Raw,
                combinedSavings.Raw,
                baseSavings.Expected,
                combinedSavings.Expected,
                marginalProspectiveTextureMergeDraws,
                marginalRawDraws,
                marginalExpectedDraws,
                marginalGlobalCost,
                marginalExpectedCost,
                marginalPackLocalRetirementCreditBytes,
                marginalNet);

            var scenarioResolved = IsAtlasValueBatchScenarioResolved(
                state,
                addedCandidates);
            if (!IsAtlasValueCandidateAllowedForMode(
                    state.AtlasAllVmdsEnabled,
                    scenarioResolved))
            {
                rejectionReason =
                    "gameplay/scenario relevance could not be resolved safely.";
                return false;
            }

            var sourceBcnBytes = state.SourceBcnResidency?.BcnBytes ?? 0;
            var acceptedNetBcnBytes = state.AtlasValueGateNetBcnBytesAccepted;
            if (!baseAlreadyAccepted)
                acceptedNetBcnBytes += baseResidency.NetBcnBytes;

            var budgetDecision = EvaluateTextureOnlyMergeAtlasValueGateBudget(
                scenarioResolved,
                marginalProspectiveTextureMergeDraws,
                marginalExpectedDraws,
                marginalGlobalCost,
                marginalExpectedCost,
                marginalPackLocalRetirementCreditBytes,
                acceptedNetBcnBytes,
                marginalNet,
                sourceBcnBytes);
            if (budgetDecision == AtlasValueGateBudgetDecision.Accept)
                return true;

            rejectionReason =
                $"marginal texture-only economics rejected ({budgetDecision}); " +
                $"incremental global cost {FormatMiB(marginalGlobalCost)}, " +
                $"scenario cost {FormatMiB(marginalExpectedCost)}, pack-local retirement " +
                $"credit {FormatMiB(marginalPackLocalRetirementCreditBytes)}, raw draws " +
                $"+{marginalRawDraws:N0}, scenario draws +{marginalExpectedDraws:N3}.";
            return false;
        }

        private static void AttachPendingTextureOnlyMergeGroups(
            BatchState state,
            List<List<AtlasCandidate>> acceptedBatches,
            IReadOnlyList<MergeAffinityGroup> affinityGroups,
            IReadOnlyDictionary<
                MeshKey,
                Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>>
                expectedEntitiesByMesh,
            IReadOnlyList<AtlasValueGatePendingTextureMergeAttachment> pendingAttachments,
            int acceptedEconomicsStartIndex)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            if (pendingAttachments.Count == 0)
                return;

            var orderedAttachments = pendingAttachments
                .OrderByDescending(attachment => attachment.Plan.Candidates.Count)
                .ThenBy(
                    attachment => string.Join(
                        "\u001f",
                        attachment.Plan.Candidates
                            .Select(candidate => candidate.Key.ToString())
                            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)),
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var pending in orderedAttachments)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                state.AtlasValueGateTextureOnlyMergeCrossBatchGroupsEvaluated++;
                state.AtlasValueGateTextureOnlyMergeSubsetGroupsConsidered++;
                var subsetPlans = BuildTextureOnlyMergeSubsetPlans(pending.Plan);
                var pendingCandidateCount = pending.Plan.Candidates
                    .DistinctBy(candidate => candidate.Key)
                    .Count();
                state.AtlasValueGateTextureOnlyMergeMaximumSubsetGroupCandidateCount =
                    Math.Max(
                        state.AtlasValueGateTextureOnlyMergeMaximumSubsetGroupCandidateCount,
                        pendingCandidateCount);
                state.AtlasValueGateTextureOnlyMergeFourWayOptionsGenerated +=
                    subsetPlans.Count(plan => plan.Candidates.Count == 4);
                if (subsetPlans.Count >= TextureOnlyMergeSubsetOptionLimit)
                    state.AtlasValueGateTextureOnlyMergeSubsetOptionGroupsTruncated++;
                var attachedKeys = new HashSet<MeshKey>();
                var bestRejectionReason = string.Empty;

                while (true)
                {
                    var availableCandidates = pending.Plan.Candidates
                        .Where(candidate => !attachedKeys.Contains(candidate.Key))
                        .ToList();
                    if (availableCandidates.Count < 2)
                        break;

                    var availableKeys = availableCandidates
                        .Select(candidate => candidate.Key)
                        .ToHashSet();
                    AtlasValueGateGroupPlan? bestPlan = null;
                    TextureOnlyMergeMarginalEvaluation? bestEvaluation = null;
                    var bestHostIndex = -1;

                    foreach (var subsetPlan in subsetPlans)
                    {
                        state.CancellationToken.ThrowIfCancellationRequested();
                        var subsetKeys = subsetPlan.Candidates
                            .Select(candidate => candidate.Key)
                            .ToHashSet();
                        if (subsetPlan.Candidates.Count < 2 ||
                            !subsetKeys.IsSubsetOf(availableKeys))
                        {
                            continue;
                        }

                        state.AtlasValueGateTextureOnlyMergeSubsetOptionsEvaluated++;
                        if (subsetPlan.Candidates.Count == 4)
                            state.AtlasValueGateTextureOnlyMergeFourWayOptionsEvaluated++;
                        for (var hostIndex = 0;
                             hostIndex < acceptedBatches.Count;
                             hostIndex++)
                        {
                            var host = acceptedBatches[hostIndex];
                            if (host.Any(candidate => subsetKeys.Contains(candidate.Key)))
                                continue;

                            var baseGroups = GetMergeAffinityGroupsForCandidates(
                                host,
                                affinityGroups);
                            var combinedCandidates = host
                                .Concat(subsetPlan.Candidates)
                                .DistinctBy(candidate => candidate.Key)
                                .ToList();
                            var combinedGroups = GetMergeAffinityGroupsForCandidates(
                                combinedCandidates,
                                affinityGroups);
                            foreach (var subsetGroup in GetMergeAffinityGroupsForCandidates(
                                         subsetPlan.Candidates,
                                         subsetPlan.AffinityGroups))
                            {
                                if (!combinedGroups.Any(group =>
                                        group.Meshes.SequenceEqual(subsetGroup.Meshes)))
                                {
                                    combinedGroups.Add(subsetGroup);
                                }
                            }

                            if (!TryEvaluateTextureOnlyMergeMarginalAddition(
                                    state,
                                    host,
                                    subsetPlan.Candidates,
                                    baseGroups,
                                    combinedGroups,
                                    expectedEntitiesByMesh,
                                    baseAlreadyAccepted: true,
                                    out var evaluation,
                                    out var rejectionReason))
                            {
                                if (evaluation != null)
                                {
                                    RecordTextureOnlyMergeNearMiss(
                                        state,
                                        subsetPlan,
                                        host,
                                        evaluation,
                                        rejectionReason);
                                }

                                if (bestRejectionReason.Length == 0)
                                    bestRejectionReason = rejectionReason;
                                continue;
                            }

                            if (evaluation == null)
                                continue;

                            if (bestEvaluation == null ||
                                IsBetterTextureOnlyMergeAttachment(
                                    evaluation,
                                    bestEvaluation))
                            {
                                bestPlan = subsetPlan;
                                bestEvaluation = evaluation;
                                bestHostIndex = hostIndex;
                            }
                        }
                    }

                    if (bestPlan == null ||
                        bestEvaluation == null ||
                        bestHostIndex < 0)
                    {
                        break;
                    }

                    var economicsIndex = acceptedEconomicsStartIndex + bestHostIndex;
                    if (economicsIndex < 0 ||
                        economicsIndex >= state.AtlasValueGateAcceptedBatchEconomics.Count)
                    {
                        bestRejectionReason =
                            "accepted atlas economics bookkeeping was not aligned with " +
                            "the planned batches.";
                        break;
                    }

                    acceptedBatches[bestHostIndex] = bestEvaluation.CombinedCandidates;
                    ApplyTextureOnlyMergeAttachmentAccounting(
                        state,
                        acceptedBatches[bestHostIndex],
                        bestEvaluation,
                        expectedEntitiesByMesh,
                        economicsIndex);
                    foreach (var candidate in bestPlan.Candidates)
                        attachedKeys.Add(candidate.Key);
                    if (bestPlan.Candidates.Count == 4)
                        state.AtlasValueGateTextureOnlyMergeFourWayGroupsAccepted++;
                    state.AtlasValueGateMarginalGroupsAccepted++;
                    state.AtlasValueGateTextureOnlyMergeMarginalGroupsAccepted++;
                    state.AtlasValueGateTextureOnlyMergeCrossBatchGroupsAccepted++;
                }

                var remainderCandidates = pending.Plan.Candidates
                    .Where(candidate => !attachedKeys.Contains(candidate.Key))
                    .ToList();
                if (remainderCandidates.Count == 0)
                    continue;

                var remainderKeys = remainderCandidates
                    .Select(candidate => candidate.Key)
                    .ToHashSet();
                var remainderPlan = CreateAtlasValueGateSubPlan(
                    pending.Plan,
                    remainderCandidates,
                    remainderKeys,
                    GetCompleteAtlasFamilySubsets(
                        pending.Plan.AtomicFamilies,
                        remainderKeys));
                var rejectionResidency = pending.Residency;
                if (TryEstimateIncrementalAtlasResidency(
                        state,
                        remainderCandidates,
                        out var remainderResidency))
                {
                    rejectionResidency = remainderResidency;
                }

                RejectAtlasValueGateGroup(
                    state,
                    remainderPlan,
                    rejectionResidency,
                    "cross-batch texture-only attachment rejected: " +
                    (bestRejectionReason.Length == 0
                        ? "no compatible accepted atlas batch passed the incremental gate."
                        : bestRejectionReason) +
                    $" Original rejection: {pending.RejectionReason}");
            }
        }

        private static List<AtlasValueGateGroupPlan> BuildTextureOnlyMergeSubsetPlans(
            AtlasValueGateGroupPlan plan)
        {
            var candidates = plan.Candidates
                .DistinctBy(candidate => candidate.Key)
                .OrderBy(BuildAtlasPlanningOrderKey, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Key.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (candidates.Count <= 2)
            {
                var candidateKeys = candidates
                    .Select(candidate => candidate.Key)
                    .ToHashSet();
                var candidatePlan = CreateAtlasValueGateSubPlan(
                    plan,
                    candidates,
                    candidateKeys,
                    GetCompleteAtlasFamilySubsets(plan.AtomicFamilies, candidateKeys));
                return GetAtlasValueGatePlanProspectiveTextureMergeDraws(candidatePlan) > 0 &&
                       IsCompleteAtlasFamilySubset(plan.AtomicFamilies, candidateKeys)
                    ? [candidatePlan]
                    : [];
            }

            var pool = candidates
                .Take(TextureOnlyMergeSubsetCandidateLimit)
                .ToList();
            var subsetPlans = new List<AtlasValueGateGroupPlan>();
            var subsetOptionLimit = Math.Max(1, TextureOnlyMergeSubsetOptionLimit - 1);
            var fourWayOptionReserve = pool.Count > 4
                ? Math.Min(TextureOnlyMergeFourWaySubsetOptionReserve, subsetOptionLimit)
                : 0;
            var activeSubsetOptionLimit = subsetOptionLimit - fourWayOptionReserve;

            void AddSubset(IReadOnlyList<AtlasCandidate> subset)
            {
                if (subsetPlans.Count >= activeSubsetOptionLimit)
                    return;

                var subsetKeys = subset
                    .Select(candidate => candidate.Key)
                    .ToHashSet();
                if (!IsCompleteAtlasFamilySubset(plan.AtomicFamilies, subsetKeys))
                    return;
                var subsetPlan = CreateAtlasValueGateSubPlan(
                    plan,
                    subset,
                    subsetKeys,
                    GetCompleteAtlasFamilySubsets(plan.AtomicFamilies, subsetKeys));
                if (GetAtlasValueGatePlanProspectiveTextureMergeDraws(subsetPlan) <= 0)
                    return;

                subsetPlans.Add(subsetPlan);
            }

            for (var left = 0;
                 left < pool.Count && subsetPlans.Count < activeSubsetOptionLimit;
                 left++)
            {
                for (var right = left + 1;
                     right < pool.Count && subsetPlans.Count < activeSubsetOptionLimit;
                     right++)
                {
                    AddSubset([pool[left], pool[right]]);
                }
            }

            if (pool.Count > 3)
            {
                for (var left = 0;
                     left < pool.Count && subsetPlans.Count < activeSubsetOptionLimit;
                     left++)
                {
                    for (var middle = left + 1;
                         middle < pool.Count && subsetPlans.Count < activeSubsetOptionLimit;
                         middle++)
                    {
                        for (var right = middle + 1;
                             right < pool.Count && subsetPlans.Count < activeSubsetOptionLimit;
                             right++)
                        {
                            AddSubset([pool[left], pool[middle], pool[right]]);
                        }
                    }
                }
            }

            // Four-way subsets are intentionally searched only after the pair/triple pass,
            // and only from the bounded planning pool. The reserved option budget prevents
            // the much larger triple search from consuming every slot before four-way
            // combinations get a chance. A group containing exactly four candidates is
            // already covered by the original full-group option below.
            if (pool.Count > 4)
            {
                activeSubsetOptionLimit = subsetOptionLimit;
                for (var first = 0;
                     first < pool.Count && subsetPlans.Count < activeSubsetOptionLimit;
                     first++)
                {
                    for (var second = first + 1;
                         second < pool.Count && subsetPlans.Count < activeSubsetOptionLimit;
                         second++)
                    {
                        for (var third = second + 1;
                             third < pool.Count && subsetPlans.Count < activeSubsetOptionLimit;
                             third++)
                        {
                            for (var fourth = third + 1;
                                 fourth < pool.Count &&
                                 subsetPlans.Count < activeSubsetOptionLimit;
                                 fourth++)
                            {
                                AddSubset(
                                    [
                                        pool[first],
                                        pool[second],
                                        pool[third],
                                        pool[fourth],
                                    ]);
                            }
                        }
                    }
                }
            }

            // Retain the original group as a final option. This preserves a full-group win
            // when the combined atlas happens to be cheaper than its smaller alternatives.
            if (GetAtlasValueGatePlanProspectiveTextureMergeDraws(plan) > 0)
                subsetPlans.Add(plan);
            return subsetPlans;
        }

        private static bool IsCompleteAtlasFamilySubset(
            IReadOnlyList<MeshKey[]>? atomicFamilies,
            IReadOnlySet<MeshKey> candidateKeys)
            => atomicFamilies == null || atomicFamilies.All(family =>
                !family.Any(candidateKeys.Contains) ||
                family.All(candidateKeys.Contains));

        private static IReadOnlyList<MeshKey[]> GetCompleteAtlasFamilySubsets(
            IReadOnlyList<MeshKey[]>? atomicFamilies,
            IReadOnlySet<MeshKey> candidateKeys)
            => atomicFamilies == null
                ? Array.Empty<MeshKey[]>()
                : atomicFamilies
                    .Where(family => family.All(candidateKeys.Contains))
                    .ToArray();

        private static void RecordTextureOnlyMergeNearMiss(
            BatchState state,
            AtlasValueGateGroupPlan plan,
            IReadOnlyList<AtlasCandidate> host,
            TextureOnlyMergeMarginalEvaluation evaluation,
            string rejectionReason)
        {
            if (evaluation.MarginalExpectedArmyDrawsEliminated <=
                AtlasValueGateExpectedDrawEpsilon)
            {
                return;
            }

            var structuralBudget = evaluation.MarginalProspectiveTextureMergeDraws *
                (double)MaxNetBcnBytesPerTextureOnlyMergeDraw;
            var scenarioDrawBudget = evaluation.MarginalExpectedArmyDrawsEliminated *
                                     MaxNetBcnBytesPerTextureOnlyMergeExpectedArmyDraw;
            var effectiveScenarioBudget =
                scenarioDrawBudget + evaluation.MarginalPackLocalRetirementCreditBytes;
            var effectiveGlobalBudget = Math.Max(structuralBudget, scenarioDrawBudget);
            var globalPressure = effectiveGlobalBudget > 0
                ? evaluation.MarginalGlobalCostBytes / effectiveGlobalBudget
                : double.PositiveInfinity;
            var scenarioPressure = effectiveScenarioBudget > 0
                ? evaluation.MarginalExpectedCostBytes / effectiveScenarioBudget
                : double.PositiveInfinity;
            var budgetPressure = Math.Max(globalPressure, scenarioPressure);
            if (double.IsNaN(budgetPressure))
                return;

            var candidateGroup = string.Join(
                ", ",
                plan.Candidates
                    .Select(candidate => candidate.Key.ToString())
                    .OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
            var hostBatch = string.Join(
                ", ",
                host
                    .Select(candidate => Normalize(candidate.RootVmdPath))
                    .Where(path => path.Length != 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(4));
            state.AtlasValueGateTextureOnlyMergeNearMisses.Add(
                new AtlasValueGateTextureOnlyMergeNearMiss(
                    candidateGroup,
                    hostBatch.Length == 0 ? "<none>" : hostBatch,
                    plan.Candidates.Count,
                    evaluation.MarginalGlobalCostBytes,
                    evaluation.MarginalExpectedCostBytes,
                    evaluation.MarginalRawDrawsEliminated,
                    evaluation.MarginalExpectedArmyDrawsEliminated,
                    budgetPressure,
                    rejectionReason));

            while (state.AtlasValueGateTextureOnlyMergeNearMisses.Count >
                   TextureOnlyMergeNearMissLimit)
            {
                var worst = state.AtlasValueGateTextureOnlyMergeNearMisses
                    .OrderByDescending(entry => entry.BudgetPressure)
                    .ThenByDescending(entry => entry.ExpectedArmyDrawsEliminated)
                    .First();
                state.AtlasValueGateTextureOnlyMergeNearMisses.Remove(worst);
            }
        }

        private static bool IsBetterTextureOnlyMergeAttachment(
            TextureOnlyMergeMarginalEvaluation candidate,
            TextureOnlyMergeMarginalEvaluation incumbent)
        {
            var candidateExpectedCostPerDraw = GetAtlasValueGateMiBPerDraw(
                Math.Max(
                    0.0,
                    candidate.MarginalExpectedCostBytes -
                    candidate.MarginalPackLocalRetirementCreditBytes),
                candidate.MarginalExpectedArmyDrawsEliminated);
            var incumbentExpectedCostPerDraw = GetAtlasValueGateMiBPerDraw(
                Math.Max(
                    0.0,
                    incumbent.MarginalExpectedCostBytes -
                    incumbent.MarginalPackLocalRetirementCreditBytes),
                incumbent.MarginalExpectedArmyDrawsEliminated);
            const double epsilon = 0.000001;
            if (candidateExpectedCostPerDraw <
                incumbentExpectedCostPerDraw - epsilon)
            {
                return true;
            }

            if (Math.Abs(
                    candidateExpectedCostPerDraw - incumbentExpectedCostPerDraw) >
                epsilon)
            {
                return false;
            }

            if (candidate.MarginalExpectedArmyDrawsEliminated >
                incumbent.MarginalExpectedArmyDrawsEliminated + epsilon)
            {
                return true;
            }

            if (Math.Abs(
                    candidate.MarginalExpectedArmyDrawsEliminated -
                    incumbent.MarginalExpectedArmyDrawsEliminated) > epsilon)
            {
                return false;
            }

            var candidateGlobalCostPerDraw = GetAtlasValueGateMiBPerDraw(
                candidate.MarginalGlobalCostBytes,
                candidate.MarginalRawDrawsEliminated);
            var incumbentGlobalCostPerDraw = GetAtlasValueGateMiBPerDraw(
                incumbent.MarginalGlobalCostBytes,
                incumbent.MarginalRawDrawsEliminated);
            return candidateGlobalCostPerDraw <
                   incumbentGlobalCostPerDraw - epsilon;
        }

        private static void ApplyTextureOnlyMergeAttachmentAccounting(
            BatchState state,
            IReadOnlyList<AtlasCandidate> combinedBatch,
            TextureOnlyMergeMarginalEvaluation evaluation,
            IReadOnlyDictionary<
                MeshKey,
                Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>>
                expectedEntitiesByMesh,
            int economicsIndex)
        {
            var generatedDelta = evaluation.CombinedResidency.GeneratedBcnBytes -
                                  evaluation.BaseResidency.GeneratedBcnBytes;
            var retiredDelta = evaluation.CombinedResidency.RetiredSourceBcnBytes -
                               evaluation.BaseResidency.RetiredSourceBcnBytes;
            var netDelta = evaluation.CombinedResidency.NetBcnBytes -
                           evaluation.BaseResidency.NetBcnBytes;
            var expectedGeneratedDelta =
                evaluation.CombinedResidency.ExpectedArmyGeneratedBcnBytes -
                evaluation.BaseResidency.ExpectedArmyGeneratedBcnBytes;
            var expectedRetiredDelta =
                evaluation.CombinedResidency.ExpectedArmyRetiredSourceBcnBytes -
                evaluation.BaseResidency.ExpectedArmyRetiredSourceBcnBytes;
            var expectedNetDelta =
                evaluation.CombinedResidency.ExpectedArmyNetBcnBytes -
                evaluation.BaseResidency.ExpectedArmyNetBcnBytes;

            state.AtlasValueGateCandidatesAccepted = checked(
                state.AtlasValueGateCandidatesAccepted +
                combinedBatch.Count -
                state.AtlasValueGateAcceptedBatchEconomics[economicsIndex].CandidateCount);
            state.AtlasValueGateGeneratedBcnBytesAccepted = checked(
                state.AtlasValueGateGeneratedBcnBytesAccepted + generatedDelta);
            state.AtlasValueGateRetiredBcnBytesAccepted = checked(
                state.AtlasValueGateRetiredBcnBytesAccepted + retiredDelta);
            state.AtlasValueGateNetBcnBytesAccepted = checked(
                state.AtlasValueGateNetBcnBytesAccepted + netDelta);
            state.AtlasValueGateExpectedArmyGeneratedBcnBytesAccepted +=
                expectedGeneratedDelta;
            state.AtlasValueGateExpectedArmyRetiredBcnBytesAccepted +=
                expectedRetiredDelta;
            state.AtlasValueGateExpectedArmyNetBcnBytesAccepted +=
                expectedNetDelta;
            state.AtlasValueGateExpectedDrawsAccepted +=
                evaluation.CombinedExpectedArmyDrawsEliminated -
                evaluation.BaseExpectedArmyDrawsEliminated;
            RecordAtlasValueGateSourceTextureResidencyDiagnostics(
                state,
                evaluation.CombinedResidency.SourceTextureResidencyDiagnostics);
            state.AtlasValueGateAcceptedAffectedUnits.UnionWith(
                CalculateAtlasBatchCoverage(combinedBatch, expectedEntitiesByMesh)
                    .AffectedUnits);
            AddAtlasValueGateRewrittenReferences(
                state,
                evaluation.CombinedResidency.RewrittenReferences);

            var entry = state.AtlasValueGateAcceptedBatchEconomics[economicsIndex];
            var coverage = CalculateAtlasBatchCoverage(
                combinedBatch,
                expectedEntitiesByMesh);
            var roots = combinedBatch
                .Select(candidate => Normalize(candidate.RootVmdPath))
                .Where(path => path.Length != 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            state.AtlasValueGateAcceptedBatchEconomics[economicsIndex] = entry with
            {
                CandidateCount = combinedBatch.Count,
                RootVmdPaths = roots,
                AffectedUnitCount = coverage.AffectedUnitCount,
                ExpectedRenderedEntities = coverage.ExpectedRenderedEntities,
                GeneratedBcnBytes = evaluation.CombinedResidency.GeneratedBcnBytes,
                RetiredSourceBcnBytes = evaluation.CombinedResidency.RetiredSourceBcnBytes,
                NetBcnBytes = evaluation.CombinedResidency.NetBcnBytes,
                ExpectedArmyGeneratedBcnBytes =
                    evaluation.CombinedResidency.ExpectedArmyGeneratedBcnBytes,
                ExpectedArmyRetiredSourceBcnBytes =
                    evaluation.CombinedResidency.ExpectedArmyRetiredSourceBcnBytes,
                ExpectedArmyNetBcnBytes =
                    evaluation.CombinedResidency.ExpectedArmyNetBcnBytes,
                RawDrawsEliminated = evaluation.CombinedRawDrawsEliminated,
                ExpectedArmyDrawsEliminated =
                    evaluation.CombinedExpectedArmyDrawsEliminated,
            };
        }

        private static bool TryAcceptAtlasValueMarginalAddition(
            BatchState state,
            IReadOnlyList<AtlasCandidate> baseCandidates,
            IReadOnlyList<AtlasCandidate> addedCandidates,
            IReadOnlyList<MergeAffinityGroup> baseGroups,
            IReadOnlyList<MergeAffinityGroup> addedGroups,
            IReadOnlyDictionary<
                MeshKey,
                Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>>
                expectedEntitiesByMesh,
            out string rejectionReason)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            rejectionReason = string.Empty;
            if (baseCandidates.Count == 0 || addedCandidates.Count == 0)
            {
                rejectionReason = "marginal comparison requires a non-empty base and addition.";
                return false;
            }

            var combinedCandidates = baseCandidates
                .Concat(addedCandidates)
                .DistinctBy(candidate => candidate.Key)
                .ToList();
            var combinedGroups = baseGroups
                .Concat(addedGroups)
                .ToList();

            if (!TryEstimateIncrementalAtlasResidency(
                    state,
                    baseCandidates,
                    out var baseResidency) ||
                !TryEstimateIncrementalAtlasResidency(
                    state,
                    combinedCandidates,
                    out var combinedResidency))
            {
                rejectionReason =
                    "incremental BCn residency could not be estimated safely.";
                return false;
            }

            var baseSavings = CalculateAtlasBatchDrawSavings(
                state,
                baseCandidates,
                baseGroups,
                expectedEntitiesByMesh);
            var combinedSavings = CalculateAtlasBatchDrawSavings(
                state,
                combinedCandidates,
                combinedGroups,
                expectedEntitiesByMesh);
            var scenarioResolved =
                IsAtlasValueBatchScenarioResolved(state, addedCandidates);
            if (!IsAtlasValueCandidateAllowedForMode(
                    state.AtlasAllVmdsEnabled,
                    scenarioResolved))
            {
                rejectionReason =
                    "gameplay/scenario relevance could not be resolved safely.";
                return false;
            }

            var sourceBcnBytes = state.SourceBcnResidency?.BcnBytes ?? 0;
            var decision = EvaluateMarginalAtlasValueGateBudget(
                scenarioResolved,
                baseSavings.Raw,
                combinedSavings.Raw,
                baseSavings.Expected,
                combinedSavings.Expected,
                baseResidency.GeneratedBcnBytes,
                baseResidency.RetiredSourceBcnBytes,
                combinedResidency.GeneratedBcnBytes,
                combinedResidency.RetiredSourceBcnBytes,
                baseResidency.ExpectedArmyGeneratedBcnBytes,
                baseResidency.ExpectedArmyRetiredSourceBcnBytes,
                combinedResidency.ExpectedArmyGeneratedBcnBytes,
                combinedResidency.ExpectedArmyRetiredSourceBcnBytes,
                state.AtlasValueGateNetBcnBytesAccepted,
                sourceBcnBytes);

            if (decision == AtlasValueGateBudgetDecision.Accept)
                return true;

            rejectionReason =
                $"marginal economics rejected the group ({decision}); " +
                $"draws raw {baseSavings.Raw:N0}->{combinedSavings.Raw:N0}, " +
                $"scenario {baseSavings.Expected:N3}->{combinedSavings.Expected:N3}.";
            return false;
        }

        private static bool TryAcceptAtlasValueBatch(
            BatchState state,
            IReadOnlyList<AtlasCandidate> batch,
            IReadOnlyList<MergeAffinityGroup> affinityGroups,
            IReadOnlyDictionary<
                MeshKey,
                Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>>
                expectedEntitiesByMesh,
            out AtlasValueGateResidencyEstimate residency,
            out int rawDrawsEliminated,
            out double expectedArmyDrawsEliminated,
            out string rejectionReason)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            residency = AtlasValueGateResidencyEstimate.Empty;
            rawDrawsEliminated = 0;
            expectedArmyDrawsEliminated = 0;
            rejectionReason = string.Empty;

            if (!TryEstimateIncrementalAtlasResidency(
                    state,
                    batch,
                    out residency))
            {
                rejectionReason = "incremental BCn residency could not be estimated safely.";
                return false;
            }

            var singletonBatches = new List<List<AtlasCandidate>>
            {
                batch.ToList()
            };
            var batchByMesh = BuildBatchIndexByMesh(singletonBatches);
            rawDrawsEliminated = CalculateMergeAffinityScore(
                singletonBatches,
                affinityGroups);
            if (rawDrawsEliminated <= 0)
            {
                rejectionReason = "no compatible mesh draw would be eliminated.";
                return false;
            }

            expectedArmyDrawsEliminated = CalculateExpectedArmyDrawCallsEliminated(
                state,
                batchByMesh,
                affinityGroups,
                expectedEntitiesByMesh);

            // Scenario-resolved batches must justify their BCn cost using scenario draw
            // savings. The much looser raw-draw fallback is reserved for assets whose
            // scenario relevance genuinely could not be resolved. A cumulative growth cap
            // prevents either path from quietly bloating the pack's total reachable BCn.
            var scenarioResolved = IsAtlasValueBatchScenarioResolved(state, batch);
            var globalCost = GetChargeableAtlasValueGateBytes(
                residency.GeneratedBcnBytes,
                residency.RetiredSourceBcnBytes);
            var expectedCost = GetChargeableAtlasValueGateBytes(
                residency.ExpectedArmyGeneratedBcnBytes,
                residency.ExpectedArmyRetiredSourceBcnBytes);
            var sourceBcnBytes = state.SourceBcnResidency?.BcnBytes ?? 0;
            var budgetDecision = EvaluateAtlasValueGateBudget(
                scenarioResolved,
                rawDrawsEliminated,
                expectedArmyDrawsEliminated,
                globalCost,
                expectedCost,
                state.AtlasValueGateNetBcnBytesAccepted,
                residency.NetBcnBytes,
                sourceBcnBytes);

            switch (budgetDecision)
            {
                case AtlasValueGateBudgetDecision.ScenarioResolvedZeroBenefit:
                    rejectionReason =
                        $"scenario-resolved batch has no modeled draw benefit but adds " +
                        $"{FormatMiB(globalCost)} chargeable BCn; the raw-draw fallback is " +
                        $"reserved for scenario-unresolved assets.";
                    return false;

                case AtlasValueGateBudgetDecision.FallbackBudgetExceeded:
                    var fallbackBytesPerDraw =
                        globalCost / Math.Max(rawDrawsEliminated, 1);
                    rejectionReason =
                        $"scenario relevance is unresolved; estimated incremental BCn generated " +
                        $"{FormatMiB(residency.GeneratedBcnBytes)}, retires " +
                        $"{FormatMiB(residency.RetiredSourceBcnBytes)}, net " +
                        $"{FormatMiB(residency.NetBcnBytes)}; net cost is " +
                        $"{FormatMiB(fallbackBytesPerDraw)} per raw draw eliminated, above the " +
                        $"{FormatMiB(MaxNetBcnBytesPerFallbackDraw, 2)} fallback budget.";
                    return false;

                case AtlasValueGateBudgetDecision.ScenarioBudgetExceeded:
                    var scenarioBytesPerDraw =
                        expectedCost /
                        Math.Max(
                            expectedArmyDrawsEliminated,
                            AtlasValueGateExpectedDrawEpsilon);
                    rejectionReason =
                        $"scenario-estimated incremental BCn resource payload generated " +
                        $"{FormatMiB(residency.ExpectedArmyGeneratedBcnBytes)}, retires " +
                        $"{FormatMiB(residency.ExpectedArmyRetiredSourceBcnBytes)}, net " +
                        $"{FormatMiB(residency.ExpectedArmyNetBcnBytes)}; net cost is " +
                        $"{FormatMiB(scenarioBytesPerDraw)} per scenario-estimated draw eliminated, above the " +
                        $"{FormatMiB(MaxNetBcnBytesPerExpectedArmyDraw, 2)} budget.";
                    return false;

                case AtlasValueGateBudgetDecision.GlobalGrowthCapExceeded:
                    var projectedNetGrowth =
                        state.AtlasValueGateNetBcnBytesAccepted +
                        (double)residency.NetBcnBytes;
                    var globalGrowthCap =
                        sourceBcnBytes * MaxReachableBcnGrowthRatio;
                    rejectionReason =
                        $"projected accepted net BCn growth {FormatMiB(projectedNetGrowth)} " +
                        $"would exceed the {MaxReachableBcnGrowthRatio:P0} source-payload cap " +
                        $"({FormatMiB(globalGrowthCap)}).";
                    return false;
            }

            return true;
        }

        private static bool TryAcceptTextureOnlyMergeAtlasBatch(
            BatchState state,
            AtlasValueGateGroupPlan plan,
            IReadOnlyDictionary<
                MeshKey,
                Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>>
                expectedEntitiesByMesh,
            out AtlasValueGateResidencyEstimate residency,
            out int rawDrawsEliminated,
            out double expectedArmyDrawsEliminated,
            out string rejectionReason)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            residency = AtlasValueGateResidencyEstimate.Empty;
            rawDrawsEliminated = 0;
            expectedArmyDrawsEliminated = 0;
            rejectionReason = string.Empty;
            var prospectiveTextureMergeDraws =
                GetAtlasValueGatePlanProspectiveTextureMergeDraws(plan);

            if (prospectiveTextureMergeDraws <= 0)
            {
                rejectionReason = "the merge group has no prospective texture-only compatibility.";
                return false;
            }

            var batch = plan.Candidates;
            if (batch.Count < 2 ||
                !TryEstimateIncrementalAtlasResidency(
                    state,
                    batch,
                    out residency))
            {
                rejectionReason =
                    batch.Count < 2
                        ? "fewer than two texture-compatible mesh candidates were available."
                        : "incremental BCn residency could not be estimated safely.";
                return false;
            }

            var singletonBatches = new List<List<AtlasCandidate>>
            {
                batch.ToList()
            };
            var batchByMesh = BuildBatchIndexByMesh(singletonBatches);
            var affinityGroups = plan.AffinityGroups;
            rawDrawsEliminated = CalculateMergeAffinityScore(
                singletonBatches,
                affinityGroups);
            expectedArmyDrawsEliminated = CalculateExpectedArmyDrawCallsEliminated(
                state,
                batchByMesh,
                affinityGroups,
                expectedEntitiesByMesh);

            var scenarioResolved = IsAtlasValueBatchScenarioResolved(state, batch);
            if (!IsAtlasValueCandidateAllowedForMode(
                    state.AtlasAllVmdsEnabled,
                    scenarioResolved))
            {
                rejectionReason =
                    "gameplay/scenario relevance could not be resolved safely.";
                return false;
            }

            var globalCost = GetChargeableAtlasValueGateBytes(
                residency.GeneratedBcnBytes,
                residency.RetiredSourceBcnBytes);
            var expectedCost = GetChargeableAtlasValueGateBytes(
                residency.ExpectedArmyGeneratedBcnBytes,
                residency.ExpectedArmyRetiredSourceBcnBytes);
            var packLocalRetirementCreditBytes =
                GetPackLocalOnlyRetirementCreditBytes(
                    residency.PackReferenceRetiredSourceTextureCount,
                    residency.RetiredSourceTextureCount);
            var sourceBcnBytes = state.SourceBcnResidency?.BcnBytes ?? 0;
            var budgetDecision = EvaluateTextureOnlyMergeAtlasValueGateBudget(
                scenarioResolved,
                prospectiveTextureMergeDraws,
                expectedArmyDrawsEliminated,
                globalCost,
                expectedCost,
                packLocalRetirementCreditBytes,
                state.AtlasValueGateNetBcnBytesAccepted,
                residency.NetBcnBytes,
                sourceBcnBytes);

            switch (budgetDecision)
            {
                case AtlasValueGateBudgetDecision.ScenarioResolvedZeroBenefit:
                    rejectionReason =
                        "texture-only merge has no modeled scenario draw benefit.";
                    return false;

                case AtlasValueGateBudgetDecision.TextureOnlyMergeBudgetExceeded:
                    var structuralBudgetBytes = prospectiveTextureMergeDraws *
                                                (double)MaxNetBcnBytesPerTextureOnlyMergeDraw;
                    var scenarioDrawBudgetBytes = expectedArmyDrawsEliminated *
                                                  MaxNetBcnBytesPerTextureOnlyMergeExpectedArmyDraw;
                    var effectiveScenarioBudgetBytes =
                        scenarioDrawBudgetBytes + packLocalRetirementCreditBytes;
                    var effectiveGlobalBudgetBytes = Math.Max(
                        structuralBudgetBytes,
                        scenarioDrawBudgetBytes);
                    rejectionReason =
                        $"texture-only merge credit exceeded: generated " +
                        $"{FormatMiB(residency.GeneratedBcnBytes)}, retires " +
                        $"{FormatMiB(residency.RetiredSourceBcnBytes)}, net " +
                        $"{FormatMiB(residency.NetBcnBytes)}; global chargeable cost is " +
                        $"{FormatMiB(globalCost)}, scenario chargeable cost is " +
                        $"{FormatMiB(expectedCost)}; structural credit is " +
                        $"{FormatMiB(structuralBudgetBytes, 2)}, scenario draw credit is " +
                        $"{FormatMiB(scenarioDrawBudgetBytes, 2)}, pack-local retirement " +
                        $"credit is {FormatMiB(packLocalRetirementCreditBytes, 2)}, effective " +
                        $"scenario credit is {FormatMiB(effectiveScenarioBudgetBytes, 2)}, and " +
                        $"the effective global credit is {FormatMiB(effectiveGlobalBudgetBytes, 2)}.";
                    return false;

                case AtlasValueGateBudgetDecision.GlobalGrowthCapExceeded:
                    var projectedNetGrowth =
                        state.AtlasValueGateNetBcnBytesAccepted +
                        (double)residency.NetBcnBytes;
                    var globalGrowthCap =
                        sourceBcnBytes * MaxReachableBcnGrowthRatio;
                    rejectionReason =
                        $"projected accepted net BCn growth {FormatMiB(projectedNetGrowth)} " +
                        $"would exceed the {MaxReachableBcnGrowthRatio:P0} source-payload cap " +
                        $"({FormatMiB(globalGrowthCap)}).";
                    return false;
            }

            return true;
        }

        private static bool TryAcceptTextureOnlyAtlasBatch(
            BatchState state,
            IReadOnlyList<AtlasCandidate> batch,
            out AtlasValueGateResidencyEstimate residency,
            out string rejectionReason)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            residency = AtlasValueGateResidencyEstimate.Empty;
            rejectionReason = string.Empty;

            if (batch.Count < 2)
            {
                rejectionReason = "fewer than two non-merge candidates were available.";
                return false;
            }

            if (state.ArmyResidencyModel == null ||
                !IsAtlasValueBatchScenarioResolved(state, batch))
            {
                rejectionReason =
                    "texture-only retirement requires resolved gameplay/scenario relevance.";
                return false;
            }

            if (!TryEstimateIncrementalAtlasResidency(
                    state,
                    batch,
                    out residency))
            {
                rejectionReason =
                    "incremental BCn residency could not be estimated safely.";
                return false;
            }

            if (!HasSufficientTextureOnlyRetirement(
                    residency.PackReferenceRetiredSourceTextureCount,
                    residency.RetiredSourceTextureCount,
                    residency.RetiredSourceBcnBytes,
                    residency.ScenarioDisplacedSourceTextureCount,
                    residency.ExpectedArmyRetiredSourceTextureCount,
                    residency.ExpectedArmyRetiredSourceBcnBytes))
            {
                rejectionReason =
                    $"fewer than {MinimumRetiredSourceTexturesForTextureOnlyAtlas} " +
                    "source textures would have all source-pack references retired or be " +
                    "displaced from modeled battle residency.";
                return false;
            }

            var globalCost = GetChargeableAtlasValueGateBytes(
                residency.GeneratedBcnBytes,
                residency.RetiredSourceBcnBytes);
            var expectedCost = GetChargeableAtlasValueGateBytes(
                residency.ExpectedArmyGeneratedBcnBytes,
                residency.ExpectedArmyRetiredSourceBcnBytes);
            var globalBudgetTextureEquivalent = Math.Max(
                residency.PackReferenceRetiredSourceTextureCount,
                residency.ExpectedArmyRetiredSourceTextureCount);
            var globalBudget =
                globalBudgetTextureEquivalent *
                MaxNetBcnBytesPerRetiredSourceTexture;
            var packLocalRetirementCreditBytes =
                GetPackLocalOnlyRetirementCreditBytes(
                    residency.PackReferenceRetiredSourceTextureCount,
                    residency.RetiredSourceTextureCount);
            var expectedBudget =
                residency.ExpectedArmyRetiredSourceTextureCount *
                    MaxNetBcnBytesPerRetiredSourceTexture +
                packLocalRetirementCreditBytes;

            if (globalCost > globalBudget)
            {
                rejectionReason =
                    $"texture-only net cost {FormatMiB(globalCost)} exceeds " +
                    $"{FormatMiB(MaxNetBcnBytesPerRetiredSourceTexture, 2)} per " +
                    "retired source texture.";
                return false;
            }

            if (expectedCost > expectedBudget)
            {
                rejectionReason =
                    $"scenario-estimated texture-only net cost {FormatMiB(expectedCost)} " +
                    $"exceeds the effective retirement budget {FormatMiB(expectedBudget)} " +
                    $"(runtime displacement plus {FormatMiB(packLocalRetirementCreditBytes)} " +
                    "pack-local-only retirement credit).";
                return false;
            }

            var sourceBcnBytes = state.SourceBcnResidency?.BcnBytes ?? 0;
            if (sourceBcnBytes > 0)
            {
                var projectedNetGrowth =
                    state.AtlasValueGateNetBcnBytesAccepted +
                    (double)residency.NetBcnBytes;
                var globalGrowthCap = sourceBcnBytes * MaxReachableBcnGrowthRatio;
                if (projectedNetGrowth > globalGrowthCap)
                {
                    rejectionReason =
                        $"projected accepted net BCn growth {FormatMiB(projectedNetGrowth)} " +
                        $"would exceed the {MaxReachableBcnGrowthRatio:P0} source-payload cap " +
                        $"({FormatMiB(globalGrowthCap)}).";
                    return false;
                }
            }

            return true;
        }

        private static bool HasSufficientTextureOnlyRetirement(
            int packReferenceRetiredTextureCount,
            int physicallyRetiredTextureCount,
            long physicallyRetiredBcnBytes,
            int scenarioDisplacedTextureCount,
            double expectedRetiredTextureEquivalents,
            double expectedRetiredBcnBytes)
        {
            var hasPackReferenceRetirement =
                packReferenceRetiredTextureCount >=
                MinimumRetiredSourceTexturesForTextureOnlyAtlas;
            var hasPhysicalRetirement =
                physicallyRetiredTextureCount >=
                    MinimumRetiredSourceTexturesForTextureOnlyAtlas &&
                physicallyRetiredBcnBytes > 0;
            var hasScenarioDisplacement =
                scenarioDisplacedTextureCount >=
                    MinimumRetiredSourceTexturesForTextureOnlyAtlas &&
                expectedRetiredTextureEquivalents >= 1.0 &&
                expectedRetiredBcnBytes > 0;
            return hasPackReferenceRetirement ||
                   hasPhysicalRetirement ||
                   hasScenarioDisplacement;
        }

        private static List<TextureConsolidationCohortAcceptance>
            SelectTextureConsolidationCohorts(
                BatchState state,
                IReadOnlyList<AtlasCandidate> candidates,
                IReadOnlyDictionary<
                    MeshKey,
                    Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>>
                    expectedEntitiesByMesh,
                out Dictionary<MeshKey, string> rejectionReasons)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            rejectionReasons = new Dictionary<MeshKey, string>();
            if (candidates.Count == 0)
                return [];

            var orderedCandidates = candidates
                .OrderBy(candidate => (long)candidate.Width * candidate.Height)
                .ThenBy(candidate => candidate.AtlasResolutionScale)
                .ThenBy(
                    candidate => BuildAtlasPlanningOrderKey(candidate),
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(
                    candidate => candidate.Key.ToString(),
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
            var pending = new Queue<List<AtlasCandidate>>();
            for (var offset = 0;
                 offset < orderedCandidates.Count;
                 offset += TextureConsolidationInitialCohortCandidateLimit)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                pending.Enqueue(
                    orderedCandidates
                        .Skip(offset)
                        .Take(TextureConsolidationInitialCohortCandidateLimit)
                        .ToList());
            }

            var accepted = new List<TextureConsolidationCohortAcceptance>();
            while (pending.Count != 0)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                var cohort = pending.Dequeue();
                state.AtlasValueGateTextureConsolidationCohortsEvaluated++;
                if (TryAcceptTextureConsolidationAtlasBatch(
                        state,
                        cohort,
                        out var residency,
                        out var consolidatedTextureAssignments,
                        out var rejectionReason))
                {
                    var acceptance = new TextureConsolidationCohortAcceptance(
                        cohort,
                        residency,
                        consolidatedTextureAssignments);
                    accepted.Add(acceptance);

                    // Commit each winning cohort before evaluating the next one. The
                    // consolidation gate depends on the accumulated generated BCn cost and
                    // rewritten source references, so evaluating all cohorts first can let
                    // independent cohorts jointly exceed the global growth cap or claim the
                    // same source-texture retirement.
                    RecordAtlasValueGateAccepted(
                        state,
                        acceptance.Candidates,
                        acceptance.Residency,
                        rawDrawsEliminated: 0,
                        expectedArmyDrawsEliminated: 0,
                        expectedEntitiesByMesh,
                        acceptanceKind: AtlasValueGateAcceptanceKind.TextureConsolidation,
                        consolidatedTextureAssignments:
                            acceptance.ConsolidatedTextureAssignments);
                    continue;
                }

                if (ShouldSplitTextureConsolidationCohort(
                        cohort,
                        rejectionReason))
                {
                    var splitIndex = cohort.Count / 2;
                    pending.Enqueue(cohort.Take(splitIndex).ToList());
                    pending.Enqueue(cohort.Skip(splitIndex).ToList());
                    state.AtlasValueGateTextureConsolidationCohortsSplit++;
                    continue;
                }

                foreach (var candidate in cohort)
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    rejectionReasons[candidate.Key] = rejectionReason;
                }
            }

            return accepted;
        }

        private static bool ShouldSplitTextureConsolidationCohort(
            IReadOnlyList<AtlasCandidate> cohort,
            string rejectionReason)
        {
            if (cohort.Count <= 2)
                return false;

            return rejectionReason.Contains(
                       "net cost",
                       StringComparison.OrdinalIgnoreCase) ||
                   rejectionReason.Contains(
                       "source-payload cap",
                       StringComparison.OrdinalIgnoreCase) ||
                   rejectionReason.Contains(
                       "residency",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryAcceptTextureConsolidationAtlasBatch(
            BatchState state,
            IReadOnlyList<AtlasCandidate> batch,
            out AtlasValueGateResidencyEstimate residency,
            out int consolidatedTextureAssignments,
            out string rejectionReason)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            residency = AtlasValueGateResidencyEstimate.Empty;
            consolidatedTextureAssignments = 0;
            rejectionReason = string.Empty;

            if (batch.Count < 2)
            {
                rejectionReason = "fewer than two non-merge candidates were available.";
                return false;
            }

            if (state.ArmyResidencyModel == null ||
                !IsAtlasValueBatchScenarioResolved(state, batch))
            {
                rejectionReason =
                    "texture consolidation requires resolved gameplay/scenario relevance.";
                return false;
            }

            if (!TryGetGeneratedAtlasBcnCost(
                    state,
                    batch,
                    out var generatedBcnBytes,
                    out var generatedBcnBytesBySlot) ||
                !TryEstimateIncrementalAtlasResidency(
                    state,
                    batch,
                    generatedBcnBytes,
                    generatedBcnBytesBySlot,
                    out residency))
            {
                rejectionReason =
                    "incremental BCn residency could not be estimated safely.";
                return false;
            }

            foreach (var channel in AtlasChannels)
            {
                if (!generatedBcnBytesBySlot.ContainsKey(channel.Slot))
                    continue;

                var distinctSourceAssignments = batch
                    .Where(candidate => candidate.ResolvedChannels.Contains(channel.Slot))
                    .Select(candidate => Normalize(
                        GetTexturePath(candidate.MaterialDocument, channel.Slot)))
                    .Where(path => path.Length != 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                consolidatedTextureAssignments +=
                    Math.Max(0, distinctSourceAssignments - 1);
            }

            if (consolidatedTextureAssignments <
                MinimumConsolidatedTextureAssignmentsForAtlas)
            {
                rejectionReason =
                    "fewer than " +
                    MinimumConsolidatedTextureAssignmentsForAtlas +
                    " distinct texture assignments would be consolidated.";
                return false;
            }

            var globalCost = GetChargeableAtlasValueGateBytes(
                residency.GeneratedBcnBytes,
                residency.RetiredSourceBcnBytes);
            var expectedCost = GetChargeableAtlasValueGateBytes(
                residency.ExpectedArmyGeneratedBcnBytes,
                residency.ExpectedArmyRetiredSourceBcnBytes);
            var consolidationBudget =
                consolidatedTextureAssignments *
                (double)MaxNetBcnBytesPerConsolidatedTextureAssignment;

            if (globalCost > consolidationBudget)
            {
                rejectionReason =
                    "texture-consolidation net cost " +
                    FormatMiB(globalCost) +
                    " exceeds " +
                    FormatMiB(
                        MaxNetBcnBytesPerConsolidatedTextureAssignment,
                        2) +
                    " per consolidated texture assignment.";
                return false;
            }

            if (expectedCost > consolidationBudget)
            {
                rejectionReason =
                    "scenario-estimated texture-consolidation net cost " +
                    FormatMiB(expectedCost) +
                    " exceeds the per-assignment budget.";
                return false;
            }

            var sourceBcnBytes = state.SourceBcnResidency?.BcnBytes ?? 0;
            if (sourceBcnBytes > 0)
            {
                var projectedNetGrowth =
                    state.AtlasValueGateNetBcnBytesAccepted +
                    (double)residency.NetBcnBytes;
                var globalGrowthCap = sourceBcnBytes * MaxReachableBcnGrowthRatio;
                if (projectedNetGrowth > globalGrowthCap)
                {
                    rejectionReason =
                        "projected accepted net BCn growth " +
                        FormatMiB(projectedNetGrowth) +
                        " would exceed the " +
                        MaxReachableBcnGrowthRatio.ToString("P0") +
                        " source-payload cap (" +
                        FormatMiB(globalGrowthCap) +
                        ").";
                    return false;
                }
            }

            return true;
        }

        private static double GetChargeableAtlasValueGateBytes(
            double generatedBytes,
            double retiredSourceBytes)
            => Math.Max(0.0, generatedBytes - retiredSourceBytes);

        private static bool TryEstimateIncrementalAtlasResidency(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates,
            out AtlasValueGateResidencyEstimate estimate)
        {
            estimate = AtlasValueGateResidencyEstimate.Empty;
            if (!TryGetGeneratedAtlasBcnCost(
                    state,
                    candidates,
                    out var generatedBcnBytes,
                    out var generatedBcnBytesBySlot))
            {
                return false;
            }

            return TryEstimateIncrementalAtlasResidency(
                state,
                candidates,
                generatedBcnBytes,
                generatedBcnBytesBySlot,
                out estimate);
        }

        private static bool TryEstimateIncrementalAtlasResidency(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates,
            long generatedBcnBytes,
            IReadOnlyDictionary<string, long> generatedBcnBytesBySlot,
            out AtlasValueGateResidencyEstimate estimate)
        {
            estimate = AtlasValueGateResidencyEstimate.Empty;
            state.CancellationToken.ThrowIfCancellationRequested();
            var sourceIndex = state.AtlasValueGateSourceTextureIndex ??
                BuildAtlasValueGateSourceTextureIndex(
                    state,
                    GetAtlasValueGateCandidateTexturePaths(candidates));
            state.AtlasValueGateSourceTextureIndex = sourceIndex;
            if (state.AtlasValueGateSourceTexturesByReference == null)
            {
                BuildAtlasValueGateSourceTextureReverseIndex(state, sourceIndex);
            }

            var proposedRewrites = new HashSet<AtlasValueGateSourceReference>();
            foreach (var candidate in candidates)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                foreach (var slot in generatedBcnBytesBySlot.Keys)
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    if (!candidate.ResolvedChannels.Contains(slot) &&
                        !candidate.ConstantChannels.ContainsKey(slot))
                    {
                        continue;
                    }

                    var sourcePath = GetTexturePath(candidate.MaterialDocument, slot);
                    if (string.IsNullOrWhiteSpace(sourcePath))
                        continue;

                    sourcePath = Normalize(sourcePath);
                    if (!sourceIndex.TryGetValue(sourcePath, out var sourceTexture))
                        continue;

                    foreach (var usage in candidate.Usages)
                    {
                        var reference = new AtlasValueGateSourceReference(
                            candidate.Key,
                            Normalize(usage.AssetPath).ToLowerInvariant(),
                            slot.ToLowerInvariant(),
                            0,
                            string.IsNullOrWhiteSpace(usage.EmbeddedRigidPath)
                                ? AtlasValueGateSourceReferenceKind.WsModelMaterial
                                : AtlasValueGateSourceReferenceKind.RigidEmbedded);
                        if (sourceTexture.References.Contains(reference))
                            proposedRewrites.Add(reference);
                    }
                }
            }

            long retiredSourceBcnBytes = 0;
            var retiredSourceTextureCount = 0;
            var packReferenceRetiredSourceTextureCount = 0;
            double expectedArmyRetiredSourceBcnBytes = 0;
            double expectedArmyRetiredSourceTextureCount = 0;
            var scenarioDisplacedSourceTextureCount = 0;
            var sourceTextureResidencyDiagnostics =
                new List<AtlasValueGateSourceTextureResidencyDiagnostic>();
            IReadOnlyList<(string Path, AtlasValueGateSourceTexture Texture)> affectedSourceTextures;
            if (state.AtlasValueGateSourceTexturesByReference != null)
            {
                var affectedTexturePaths = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var proposedReference in proposedRewrites)
                {
                    if (state.AtlasValueGateSourceTexturesByReference.TryGetValue(
                            proposedReference,
                            out var texturePaths))
                    {
                        affectedTexturePaths.UnionWith(texturePaths);
                    }
                }

                affectedSourceTextures = affectedTexturePaths
                    .Where(sourceIndex.ContainsKey)
                    .OrderBy(path => state.AtlasValueGateSourceTextureOrder.TryGetValue(
                        path,
                        out var order)
                        ? order
                        : int.MaxValue)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(path => (Path: path, Texture: sourceIndex[path]))
                    .ToArray();
            }
            else
            {
                // Keep a conservative fallback for callers that provide a prebuilt source
                // index without its reverse index.
                affectedSourceTextures = sourceIndex
                    .Where(entry => entry.Value.References.Any(proposedRewrites.Contains))
                    .Select(entry => (Path: entry.Key, Texture: entry.Value))
                    .ToArray();
            }
            ReportProgress(
                state.Progress,
                "Evaluating atlas value",
                0,
                affectedSourceTextures.Count,
                "Evaluating affected source textures");
            for (var sourceTextureIndex = 0;
                 sourceTextureIndex < affectedSourceTextures.Count;
                 sourceTextureIndex++)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                ReportPeriodicProgress(
                    state.Progress,
                    "Evaluating atlas value",
                    sourceTextureIndex + 1,
                    affectedSourceTextures.Count,
                    "Evaluating affected source textures");
                var (sourceTexturePath, sourceTexture) =
                    affectedSourceTextures[sourceTextureIndex];

                var currentlyRewrittenReferenceCount =
                    GetAtlasValueGateRewrittenReferenceCount(
                        state,
                        sourceTexturePath,
                        sourceTexture);
                var newlyProposedReferenceCount = sourceTexture.References.Count(reference =>
                    !state.AtlasValueGateRewrittenSourceReferences.Contains(reference) &&
                    proposedRewrites.Contains(reference));
                var rewrittenReferenceCountAfterProposal = checked(
                    currentlyRewrittenReferenceCount + newlyProposedReferenceCount);
                var currentlyRewrittenPackReferenceCount =
                    GetAtlasValueGateRewrittenPackReferenceCount(
                        state,
                        sourceTexturePath,
                        sourceTexture);
                var newlyProposedPackReferenceCount = sourceTexture.PackReferences.Count(
                    reference =>
                        !state.AtlasValueGateRewrittenSourceReferences.Contains(reference) &&
                        proposedRewrites.Contains(reference));
                var rewrittenPackReferenceCountAfterProposal = checked(
                    currentlyRewrittenPackReferenceCount + newlyProposedPackReferenceCount);
                var currentlyPackReferencesRetired =
                    IsAtlasValueGatePackReferenceRetired(
                        sourceTexture.HasDirectSourcePackVmdReference,
                        sourceTexture.PackReferences.Count,
                        currentlyRewrittenPackReferenceCount);
                var packReferencesRetiredAfterProposal =
                    IsAtlasValueGatePackReferenceRetired(
                        sourceTexture.HasDirectSourcePackVmdReference,
                        sourceTexture.PackReferences.Count,
                        rewrittenPackReferenceCountAfterProposal);
                if (!currentlyPackReferencesRetired && packReferencesRetiredAfterProposal)
                    packReferenceRetiredSourceTextureCount++;
                var currentlyRetired = IsAtlasValueGateSourceTextureRetired(
                    sourceTexture.IsOwnedBySourcePack,
                    sourceTexture.HasDirectVmdReference,
                    sourceTexture.References.Count,
                    currentlyRewrittenReferenceCount);
                var retiredAfterProposal = IsAtlasValueGateSourceTextureRetired(
                    sourceTexture.IsOwnedBySourcePack,
                    sourceTexture.HasDirectVmdReference,
                    sourceTexture.References.Count,
                    rewrittenReferenceCountAfterProposal);
                if (!currentlyRetired && retiredAfterProposal)
                {
                    retiredSourceBcnBytes = checked(
                        retiredSourceBcnBytes + sourceTexture.BcnBytes);
                    retiredSourceTextureCount++;
                }

                var sourceTextureResidency = EvaluateAtlasValueGateSourceTextureResidency(
                    state,
                    sourceTexturePath,
                    sourceTexture,
                    state.AtlasValueGateRewrittenSourceReferences,
                    proposedRewrites,
                    currentlyRewrittenReferenceCount,
                    rewrittenReferenceCountAfterProposal);
                sourceTextureResidencyDiagnostics.Add(sourceTextureResidency);
                var expectedRetirement = sourceTextureResidency.ExpectedRetiredBcnBytes;
                expectedArmyRetiredSourceBcnBytes += expectedRetirement;
                if (expectedRetirement > AtlasValueGateExpectedDrawEpsilon)
                {
                    scenarioDisplacedSourceTextureCount++;
                    expectedArmyRetiredSourceTextureCount += Math.Clamp(
                        expectedRetirement / sourceTexture.BcnBytes,
                        0.0,
                        1.0);
                }
            }

            double expectedArmyGeneratedBcnBytes = 0;
            var rootsByMesh = BuildCandidateRootVmdPaths(state, candidates);
            foreach (var (slot, channelBytes) in generatedBcnBytesBySlot)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                var channelCandidates = candidates
                    .Where(candidate =>
                        candidate.ResolvedChannels.Contains(slot) ||
                        candidate.ConstantChannels.ContainsKey(slot))
                    .ToList();
                if (channelCandidates.Count == 0)
                    continue;

                var targetWsModels = channelCandidates
                    .SelectMany(candidate => candidate.Usages)
                    .Select(usage => Normalize(usage.AssetPath))
                    .Where(path => path.Length != 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var roots = GetBatchRoots(channelCandidates, rootsByMesh);
                var residentProbability = GetExpectedArmyResidentProbability(
                    state,
                    targetWsModels,
                    roots);
                expectedArmyGeneratedBcnBytes +=
                    channelBytes * residentProbability;
            }

            estimate = new AtlasValueGateResidencyEstimate(
                generatedBcnBytes,
                retiredSourceBcnBytes,
                checked(generatedBcnBytes - retiredSourceBcnBytes),
                retiredSourceTextureCount,
                packReferenceRetiredSourceTextureCount,
                scenarioDisplacedSourceTextureCount,
                expectedArmyRetiredSourceTextureCount,
                expectedArmyGeneratedBcnBytes,
                expectedArmyRetiredSourceBcnBytes,
                expectedArmyGeneratedBcnBytes - expectedArmyRetiredSourceBcnBytes,
                proposedRewrites,
                sourceTextureResidencyDiagnostics);
            return true;
        }

        private static bool IsAtlasValueGatePackReferenceRetired(
            bool hasDirectSourcePackVmdReference,
            int packReferenceCount,
            int rewrittenPackReferenceCount)
            => !hasDirectSourcePackVmdReference &&
               packReferenceCount > 0 &&
               rewrittenPackReferenceCount >= packReferenceCount;

        private static bool IsAtlasValueGateSourceTextureRetired(
            bool isOwnedBySourcePack,
            bool hasDirectVmdReference,
            int referenceCount,
            int rewrittenReferenceCount)
            => isOwnedBySourcePack &&
               !hasDirectVmdReference &&
               referenceCount > 0 &&
               rewrittenReferenceCount >= referenceCount;

        private static int GetAtlasValueGateRewrittenReferenceCount(
            BatchState state,
            string texturePath,
            AtlasValueGateSourceTexture sourceTexture)
        {
            texturePath = Normalize(texturePath);
            if (state.AtlasValueGateRewrittenReferenceCountByTexture.TryGetValue(
                    texturePath,
                    out var cachedCount))
            {
                return cachedCount;
            }

            var count = sourceTexture.References.Count(
                state.AtlasValueGateRewrittenSourceReferences.Contains);
            state.AtlasValueGateRewrittenReferenceCountByTexture[texturePath] = count;
            return count;
        }

        private static int GetAtlasValueGateRewrittenPackReferenceCount(
            BatchState state,
            string texturePath,
            AtlasValueGateSourceTexture sourceTexture)
        {
            texturePath = Normalize(texturePath);
            if (state.AtlasValueGateRewrittenPackReferenceCountByTexture.TryGetValue(
                    texturePath,
                    out var cachedCount))
            {
                return cachedCount;
            }

            var count = sourceTexture.PackReferences.Count(
                state.AtlasValueGateRewrittenSourceReferences.Contains);
            state.AtlasValueGateRewrittenPackReferenceCountByTexture[texturePath] = count;
            return count;
        }

        private static void AddAtlasValueGateRewrittenReferences(
            BatchState state,
            IEnumerable<AtlasValueGateSourceReference> references)
        {
            var countBefore = state.AtlasValueGateRewrittenSourceReferences.Count;
            state.AtlasValueGateRewrittenSourceReferences.UnionWith(references);
            if (state.AtlasValueGateRewrittenSourceReferences.Count != countBefore)
            {
                state.AtlasValueGateRewrittenReferenceCountByTexture.Clear();
                state.AtlasValueGateRewrittenPackReferenceCountByTexture.Clear();
            }
        }

        private static AtlasValueGateSourceTextureResidencyDiagnostic
            EvaluateAtlasValueGateSourceTextureResidency(
                BatchState state,
                string texturePath,
                AtlasValueGateSourceTexture sourceTexture,
                IReadOnlySet<AtlasValueGateSourceReference> existingRewrites,
                IReadOnlySet<AtlasValueGateSourceReference>? proposedRewrites,
                int currentlyRewrittenReferenceCount,
                int rewrittenReferenceCountAfterProposal)
        {
            var currentProjection =
                BuildAtlasValueGateScenarioConsumerProjection(
                    state,
                    sourceTexture.References,
                    existingRewrites,
                    proposedRewrites: null);
            var proposedProjection =
                BuildAtlasValueGateScenarioConsumerProjection(
                    state,
                    sourceTexture.References,
                    existingRewrites,
                    proposedRewrites);
            var currentConsumerPaths =
                GetAtlasValueGateScenarioConsumerDisplayPaths(currentProjection);
            var proposedConsumerPaths =
                GetAtlasValueGateScenarioConsumerDisplayPaths(proposedProjection);
            var currentProjectionSummary =
                FormatAtlasValueGateScenarioConsumerProjection(currentProjection);
            var proposedProjectionSummary =
                FormatAtlasValueGateScenarioConsumerProjection(proposedProjection);

            if (sourceTexture.BcnBytes <= 0)
            {
                return new AtlasValueGateSourceTextureResidencyDiagnostic(
                    texturePath,
                    sourceTexture.BcnBytes,
                    sourceTexture.IsOwnedBySourcePack,
                    sourceTexture.HasDirectVmdReference,
                    sourceTexture.HasDirectSourcePackVmdReference,
                    sourceTexture.References.Count,
                    currentlyRewrittenReferenceCount,
                    rewrittenReferenceCountAfterProposal,
                    true,
                    0,
                    0,
                    0,
                    0,
                    0,
                    currentConsumerPaths,
                    proposedConsumerPaths)
                {
                    CurrentScenarioProjection = currentProjectionSummary,
                    ProposedScenarioProjection = proposedProjectionSummary,
                };
            }

            // Direct VMD texture references (for example decal diffuse/normal) are not
            // rewritten by material atlasing. We do not currently model their exact army
            // probability separately, so conservatively keep the full source texture
            // resident in both the current and proposed estimates. This guarantees that
            // such a reference can never create false scenario-estimated retirement credit.
            if (sourceTexture.HasDirectVmdReference)
            {
                return new AtlasValueGateSourceTextureResidencyDiagnostic(
                    texturePath,
                    sourceTexture.BcnBytes,
                    sourceTexture.IsOwnedBySourcePack,
                    sourceTexture.HasDirectVmdReference,
                    sourceTexture.HasDirectSourcePackVmdReference,
                    sourceTexture.References.Count,
                    currentlyRewrittenReferenceCount,
                    rewrittenReferenceCountAfterProposal,
                    true,
                    1.0,
                    1.0,
                    sourceTexture.BcnBytes,
                    sourceTexture.BcnBytes,
                    0,
                    currentConsumerPaths,
                    proposedConsumerPaths)
                {
                    CurrentScenarioProjection = currentProjectionSummary,
                    ProposedScenarioProjection = proposedProjectionSummary,
                };
            }

            var currentProbability =
                GetExpectedArmySourceTextureConsumerProbability(
                    state,
                    currentProjection);
            var proposedProbability =
                GetExpectedArmySourceTextureConsumerProbability(
                    state,
                    proposedProjection);
            var consumerMappingResolved =
                currentProbability.IsResolved && proposedProbability.IsResolved;
            if (!consumerMappingResolved)
            {
                // If either side contains an unresolved consumer/culture mapping, do not
                // infer that removing that reference retires any scenario residency. Keep
                // both sides at full BCn residency until the complete mapping is known.
                currentProbability = new AtlasValueGateSourceTextureConsumerProbability(
                    1.0,
                    false);
                proposedProbability = new AtlasValueGateSourceTextureConsumerProbability(
                    1.0,
                    false);
            }
            var currentExpectedResidency =
                sourceTexture.BcnBytes * currentProbability.Probability;
            var proposedExpectedResidency =
                sourceTexture.BcnBytes * proposedProbability.Probability;
            var expectedRetirement = CalculateAtlasValueGateScenarioRetirement(
                sourceTexture.BcnBytes,
                currentProbability.Probability,
                proposedProbability.Probability);

            return new AtlasValueGateSourceTextureResidencyDiagnostic(
                texturePath,
                sourceTexture.BcnBytes,
                sourceTexture.IsOwnedBySourcePack,
                sourceTexture.HasDirectVmdReference,
                sourceTexture.HasDirectSourcePackVmdReference,
                sourceTexture.References.Count,
                currentlyRewrittenReferenceCount,
                rewrittenReferenceCountAfterProposal,
                consumerMappingResolved,
                currentProbability.Probability,
                proposedProbability.Probability,
                currentExpectedResidency,
                proposedExpectedResidency,
                expectedRetirement,
                currentConsumerPaths,
                proposedConsumerPaths)
            {
                CurrentScenarioProjection = currentProjectionSummary,
                ProposedScenarioProjection = proposedProjectionSummary,
            };
        }

        private static AtlasValueGateScenarioConsumerProjection
            BuildAtlasValueGateScenarioConsumerProjection(
                BatchState state,
                IEnumerable<AtlasValueGateSourceReference> references,
                IReadOnlySet<AtlasValueGateSourceReference> existingRewrites,
                IReadOnlySet<AtlasValueGateSourceReference>? proposedRewrites)
        {
            var targetAssetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fallbackVmdRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var geometryOnlyRigidPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directRigidPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unresolvedConsumers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unresolvedReasons = new HashSet<string>(StringComparer.Ordinal);

            foreach (var reference in references)
            {
                if (existingRewrites.Contains(reference) ||
                    (proposedRewrites != null && proposedRewrites.Contains(reference)))
                {
                    continue;
                }

                var consumerPath = Normalize(reference.ConsumerAssetPath);
                if (consumerPath.Length == 0)
                {
                    unresolvedConsumers.Add("<empty>");
                    unresolvedReasons.Add("EmptyConsumerAssetPath");
                    continue;
                }

                if (reference.Kind == AtlasValueGateSourceReferenceKind.WsModelMaterial)
                {
                    targetAssetPaths.Add(consumerPath);
                    continue;
                }

                if (reference.Kind != AtlasValueGateSourceReferenceKind.RigidEmbedded)
                {
                    unresolvedConsumers.Add(consumerPath);
                    unresolvedReasons.Add(
                        $"UnknownReferenceKind:{reference.Kind}:{consumerPath}");
                    continue;
                }

                var classification = ClassifyAtlasValueGateRigidConsumer(
                    state,
                    consumerPath,
                    out var classificationReason);
                switch (classification)
                {
                    case AtlasValueGateRigidConsumerClassification.GeometryOnlyProven:
                        geometryOnlyRigidPaths.Add(consumerPath);
                        break;

                    case AtlasValueGateRigidConsumerClassification.DirectRigid:
                        directRigidPaths.Add(consumerPath);
                        if (state.GameplayMeshDependencyIndex == null ||
                            !state.GameplayMeshDependencyIndex.DirectConsumersByRigid.TryGetValue(
                                consumerPath,
                                out var directRoots) ||
                            directRoots.Count == 0)
                        {
                            unresolvedConsumers.Add(consumerPath);
                            unresolvedReasons.Add(
                                $"DirectConsumerRootsUnavailable:{consumerPath}");
                            break;
                        }

                        foreach (var root in directRoots.OrderBy(
                                     value => value,
                                     StringComparer.OrdinalIgnoreCase))
                        {
                            var normalizedRoot = Normalize(root);
                            var extension = Path.GetExtension(normalizedRoot);
                            if (extension.Equals(
                                    ".variantmeshdefinition",
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                fallbackVmdRoots.Add(normalizedRoot);
                            }
                            else if (extension.Equals(
                                         ".wsmodel",
                                         StringComparison.OrdinalIgnoreCase) ||
                                     extension.Equals(
                                         ".rigid_model_v2",
                                         StringComparison.OrdinalIgnoreCase))
                            {
                                targetAssetPaths.Add(normalizedRoot);
                            }
                            else
                            {
                                unresolvedConsumers.Add(consumerPath);
                                unresolvedReasons.Add(
                                    $"UnsupportedDirectConsumerRoot:{consumerPath}->{normalizedRoot}");
                            }
                        }

                        break;

                    default:
                        unresolvedConsumers.Add(consumerPath);
                        unresolvedReasons.Add(
                            classificationReason.Length == 0
                                ? $"UnknownRigidConsumer:{consumerPath}"
                                : classificationReason);
                        break;
                }
            }

            return new AtlasValueGateScenarioConsumerProjection(
                targetAssetPaths
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                fallbackVmdRoots
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                geometryOnlyRigidPaths
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                directRigidPaths
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                unresolvedConsumers
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                unresolvedReasons
                    .OrderBy(reason => reason, StringComparer.Ordinal)
                    .ToArray());
        }

        private static AtlasValueGateRigidConsumerClassification
            ClassifyAtlasValueGateRigidConsumer(
                BatchState state,
                string rigidPathValue,
                out string reason)
        {
            var rigidPath = Normalize(rigidPathValue);
            reason = string.Empty;
            var dependencyIndex = state.GameplayMeshDependencyIndex;
            if (dependencyIndex == null)
            {
                reason = $"GameplayDependencyIndexUnavailable:{rigidPath}";
                return AtlasValueGateRigidConsumerClassification.Unknown;
            }

            if (state.UnitCategoryResolution?.IsGameplayResolutionHealthy != true)
            {
                reason = $"GameplayResolutionUnhealthy:{rigidPath}";
                return AtlasValueGateRigidConsumerClassification.Unknown;
            }

            if (dependencyIndex.IsAssetAffectedByIncompleteRoot(rigidPath))
            {
                var incompleteRoots = dependencyIndex
                    .GetIncompleteRootsForAsset(rigidPath);
                reason = incompleteRoots.Count == 0
                    ? $"ConsumerTopologyUnobserved:{rigidPath}"
                    : $"DiscoveryIncomplete:{rigidPath}<-" +
                      string.Join(",", incompleteRoots);
                return AtlasValueGateRigidConsumerClassification.Unknown;
            }

            if (dependencyIndex.DirectConsumersByRigid.TryGetValue(
                    rigidPath,
                    out var directConsumers) &&
                directConsumers.Count != 0)
            {
                return AtlasValueGateRigidConsumerClassification.DirectRigid;
            }

            return AtlasValueGateRigidConsumerClassification.GeometryOnlyProven;
        }

        private static string[] GetAtlasValueGateScenarioConsumerDisplayPaths(
            AtlasValueGateScenarioConsumerProjection projection)
            => projection.TargetAssetPaths
                .Select(path => $"target:{path}")
                .Concat(projection.FallbackVmdRoots.Select(path => $"fallback-vmd:{path}"))
                .Concat(projection.GeometryOnlyRigidPaths.Select(
                    path => $"geometry-only-excluded:{path}"))
                .Concat(projection.DirectRigidPaths.Select(path => $"direct-rigid:{path}"))
                .Concat(projection.UnresolvedConsumers.Select(path => $"unknown:{path}"))
                .ToArray();

        private static string FormatAtlasValueGateScenarioConsumerProjection(
            AtlasValueGateScenarioConsumerProjection projection)
        {
            var summary =
                $"targets={projection.TargetAssetPaths.Count}, " +
                $"fallback-vmd-roots={projection.FallbackVmdRoots.Count}, " +
                $"geometry-only-excluded={projection.GeometryOnlyRigidPaths.Count}, " +
                $"direct-rigid={projection.DirectRigidPaths.Count}, " +
                $"unknown={projection.UnresolvedConsumers.Count}";
            if (projection.UnresolvedReasons.Count != 0)
            {
                summary += "; reasons=" + string.Join(
                    " | ",
                    projection.UnresolvedReasons.Take(4));
                if (projection.UnresolvedReasons.Count > 4)
                    summary += $" | ... (+{projection.UnresolvedReasons.Count - 4:N0})";
            }

            return summary;
        }

        private static AtlasValueGateSourceTextureConsumerProbability
            GetExpectedArmySourceTextureConsumerProbability(
                BatchState state,
                AtlasValueGateScenarioConsumerProjection projection)
        {
            if (!projection.IsResolved)
            {
                return new AtlasValueGateSourceTextureConsumerProbability(
                    1.0,
                    false);
            }

            if (projection.TargetAssetPaths.Count == 0 &&
                projection.FallbackVmdRoots.Count == 0)
            {
                return new AtlasValueGateSourceTextureConsumerProbability(0, true);
            }

            var model = state.ArmyResidencyModel;
            if (model == null || model.PlayerCultureWeights.Count == 0)
            {
                return new AtlasValueGateSourceTextureConsumerProbability(
                    1.0,
                    false);
            }

            foreach (var targetAssetPath in projection.TargetAssetPaths)
            {
                if (!model.UnitIdsByAssetPath.TryGetValue(
                        Normalize(targetAssetPath),
                        out var unitIds) ||
                    unitIds.Count == 0 ||
                    !unitIds.Any(unitId =>
                        model.CulturesByUnit.TryGetValue(unitId, out var cultures) &&
                        cultures.Count != 0))
                {
                    return new AtlasValueGateSourceTextureConsumerProbability(
                        1.0,
                        false);
                }
            }

            foreach (var fallbackRoot in projection.FallbackVmdRoots)
            {
                if (!model.UnitsByVmd.TryGetValue(
                        Normalize(fallbackRoot),
                        out var unitsByCategory) ||
                    unitsByCategory.Count == 0 ||
                    !unitsByCategory.Values
                        .SelectMany(unitIds => unitIds)
                        .Any(unitId =>
                            model.CulturesByUnit.TryGetValue(
                                unitId,
                                out var cultures) &&
                            cultures.Count != 0))
                {
                    return new AtlasValueGateSourceTextureConsumerProbability(
                        1.0,
                        false);
                }
            }

            var probability = GetExpectedArmyResidentProbability(
                state,
                projection.TargetAssetPaths,
                projection.FallbackVmdRoots);
            return new AtlasValueGateSourceTextureConsumerProbability(
                Math.Clamp(probability, 0.0, 1.0),
                true);
        }

        private static double CalculateAtlasValueGateScenarioRetirement(
            double bcnBytes,
            double currentResidentProbability,
            double proposedResidentProbability)
        {
            if (bcnBytes <= 0 ||
                !double.IsFinite(bcnBytes) ||
                !double.IsFinite(currentResidentProbability) ||
                !double.IsFinite(proposedResidentProbability))
            {
                return 0;
            }

            return bcnBytes * Math.Max(
                0.0,
                Math.Clamp(currentResidentProbability, 0.0, 1.0) -
                Math.Clamp(proposedResidentProbability, 0.0, 1.0));
        }

        private static bool TryGetWsModelConsumerEntry(
            BatchState state,
            IPackFileContainer container,
            string wsModelPath,
            PackFile file,
            out VanillaWsModelConsumerEntry entry)
        {
            wsModelPath = Normalize(wsModelPath);
            if (state.WsModelConsumerEntries.TryGetValue(wsModelPath, out entry!))
            {
                state.WsModelConsumerCacheHits++;
                state.WsModelConsumerMemoryCacheHits++;
                ReplayXmlCompatibilityRepairs(
                    state,
                    wsModelPath,
                    entry.CompatibilityRepairs);
                return true;
            }

            var cache = state.VanillaAtlasConsumerCache;
            if (IsVanillaAtlasCacheContainer(state, container) &&
                cache?.TryGetWsModel(container, wsModelPath, out entry) == true)
            {
                state.WsModelConsumerCacheHits++;
                state.WsModelConsumerVanillaCacheHits++;
                state.WsModelConsumerEntries[wsModelPath] = entry;
                ReplayXmlCompatibilityRepairs(
                    state,
                    wsModelPath,
                    entry.CompatibilityRepairs);
                return true;
            }

            state.WsModelConsumerCacheMisses++;
            if (!TryGetWsDocumentForTraversal(
                    state,
                    container,
                    wsModelPath,
                    file,
                    out var document))
            {
                entry = null!;
                return false;
            }

            entry = BuildWsModelConsumerEntry(document);
            state.WsModelConsumerEntryBuilds++;
            entry.CompatibilityRepairs =
                GetRecordedXmlCompatibilityRepairs(state, wsModelPath);
            state.WsModelConsumerEntries[wsModelPath] = entry;
            if (IsVanillaAtlasCacheContainer(state, container))
                cache?.SetWsModel(container, wsModelPath, entry);
            return true;
        }

        private static bool TryGetMaterialConsumerEntry(
            BatchState state,
            IPackFileContainer container,
            string materialPath,
            PackFile file,
            out VanillaMaterialConsumerEntry entry)
        {
            materialPath = Normalize(materialPath);
            if (state.MaterialConsumerEntries.TryGetValue(materialPath, out entry!))
            {
                state.MaterialConsumerCacheHits++;
                state.MaterialConsumerMemoryCacheHits++;
                ReplayXmlCompatibilityRepairs(
                    state,
                    materialPath,
                    entry.CompatibilityRepairs);
                return true;
            }

            var cache = state.VanillaAtlasConsumerCache;
            if (IsVanillaAtlasCacheContainer(state, container) &&
                cache?.TryGetMaterial(container, materialPath, out entry) == true)
            {
                state.MaterialConsumerCacheHits++;
                state.MaterialConsumerVanillaCacheHits++;
                state.MaterialConsumerEntries[materialPath] = entry;
                ReplayXmlCompatibilityRepairs(
                    state,
                    materialPath,
                    entry.CompatibilityRepairs);
                return true;
            }

            state.MaterialConsumerCacheMisses++;
            if (!TryGetMaterialDocumentForTraversal(
                    state,
                    container,
                    materialPath,
                    file,
                    out var document))
            {
                entry = null!;
                return false;
            }

            entry = BuildMaterialConsumerEntry(document);
            state.MaterialConsumerEntryBuilds++;
            entry.CompatibilityRepairs =
                GetRecordedXmlCompatibilityRepairs(state, materialPath);
            state.MaterialConsumerEntries[materialPath] = entry;
            if (IsVanillaAtlasCacheContainer(state, container))
                cache?.SetMaterial(container, materialPath, entry);
            return true;
        }

        private static bool TryGetVanillaCachedWsModelConsumerEntry(
            BatchState state,
            IPackFileContainer container,
            string wsModelPath,
            out VanillaWsModelConsumerEntry entry)
        {
            entry = null!;
            if (!IsVanillaAtlasCacheContainer(state, container) ||
                state.VanillaAtlasConsumerCache?.TryGetWsModel(
                    container,
                    wsModelPath,
                    out entry) != true)
            {
                return false;
            }

            state.WsModelConsumerCacheHits++;
            state.WsModelConsumerVanillaCacheHits++;
            state.WsModelConsumerEntries[Normalize(wsModelPath)] = entry;
            ReplayXmlCompatibilityRepairs(
                state,
                wsModelPath,
                entry.CompatibilityRepairs);
            return true;
        }

        private static bool TryGetVanillaCachedMaterialConsumerEntry(
            BatchState state,
            IPackFileContainer container,
            string materialPath,
            out VanillaMaterialConsumerEntry entry)
        {
            entry = null!;
            if (!IsVanillaAtlasCacheContainer(state, container) ||
                state.VanillaAtlasConsumerCache?.TryGetMaterial(
                    container,
                    materialPath,
                    out entry) != true)
            {
                return false;
            }

            state.MaterialConsumerCacheHits++;
            state.MaterialConsumerVanillaCacheHits++;
            state.MaterialConsumerEntries[Normalize(materialPath)] = entry;
            ReplayXmlCompatibilityRepairs(
                state,
                materialPath,
                entry.CompatibilityRepairs);
            return true;
        }

        private static bool TryGetRigidConsumerEntry(
            BatchState state,
            IPackFileContainer container,
            string rigidPath,
            PackFile file,
            out VanillaRigidConsumerEntry entry)
        {
            rigidPath = Normalize(rigidPath);
            var cache = state.VanillaAtlasConsumerCache;
            if (IsVanillaAtlasCacheContainer(state, container) &&
                cache?.TryGetRigid(container, rigidPath, out entry) == true)
            {
                return true;
            }

            try
            {
                var rigid = ModelFactory.Create().Load(file.DataSource.ReadData());
                entry = BuildRigidConsumerEntry(rigid);
                if (IsVanillaAtlasCacheContainer(state, container))
                    cache?.SetRigid(container, rigidPath, entry);
                return true;
            }
            catch
            {
                entry = null!;
                return false;
            }
        }

        private static bool IsVanillaAtlasCacheContainer(
            BatchState state,
            IPackFileContainer container)
            => container.IsCaPackFile && !ReferenceEquals(container, state.Source);

        private static List<string> GetRecordedXmlCompatibilityRepairs(
            BatchState state,
            string pathValue)
        {
            var path = Normalize(pathValue);
            return state.XmlCompatibilityRepairs.TryGetValue(path, out var repairs)
                ? repairs.OrderBy(repair => repair, StringComparer.Ordinal).ToList()
                : [];
        }

        private static void ReplayXmlCompatibilityRepairs(
            BatchState state,
            string pathValue,
            IReadOnlyList<string> repairs)
        {
            if (repairs.Count == 0)
                return;

            RecordXmlCompatibilityRepairs(state, pathValue, repairs);
        }

        private static VanillaWsModelConsumerEntry BuildWsModelConsumerEntry(
            XmlDocument document)
        {
            var entry = new VanillaWsModelConsumerEntry
            {
                GeometryPath = Normalize(
                    document.SelectSingleNode("/model/geometry")?.InnerText),
            };
            var materialNodes = document.SelectNodes("/model/materials/material");
            if (materialNodes == null)
                return entry;

            foreach (XmlNode materialNode in materialNodes)
            {
                var materialPath = Normalize(materialNode.InnerText);
                if (materialPath.Length == 0)
                    continue;

                entry.Materials.Add(new VanillaWsModelMaterialBinding
                {
                    MaterialPath = materialPath,
                    LodIndex = TryParseIndex(materialNode, "lod_index", out var lodIndex)
                        ? lodIndex
                        : null,
                    PartIndex = TryParseIndex(materialNode, "part_index", out var partIndex)
                        ? partIndex
                        : null,
                });
            }

            return entry;
        }

        private static VanillaMaterialConsumerEntry BuildMaterialConsumerEntry(
            XmlDocument document)
        {
            var entry = new VanillaMaterialConsumerEntry();
            var textureNodes = document.SelectNodes("/material/textures/texture");
            if (textureNodes == null)
                return entry;

            var occurrenceBySlot = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);
            foreach (XmlNode textureNode in textureNodes)
            {
                var slot = GetTextureSlot(textureNode);
                var normalizedSlot = string.IsNullOrWhiteSpace(slot)
                    ? "__unslotted__"
                    : slot.ToLowerInvariant();
                var slotOccurrence = occurrenceBySlot.GetValueOrDefault(normalizedSlot);
                occurrenceBySlot[normalizedSlot] = slotOccurrence + 1;

                var texturePath = Normalize(
                    textureNode.SelectSingleNode("source")?.InnerText ??
                    textureNode.InnerText);
                if (texturePath.Length == 0)
                    continue;

                entry.Textures.Add(new VanillaMaterialTextureReference
                {
                    TexturePath = texturePath,
                    Slot = normalizedSlot,
                    SlotOccurrence = slotOccurrence,
                });
            }

            return entry;
        }

        private static VanillaRigidConsumerEntry BuildRigidConsumerEntry(
            RmvFile rigid)
        {
            var entry = new VanillaRigidConsumerEntry();
            for (var lodIndex = 0; lodIndex < rigid.ModelList.Length; lodIndex++)
            {
                for (var partIndex = 0;
                     partIndex < rigid.ModelList[lodIndex].Length;
                     partIndex++)
                {
                    var material = BuildEmbeddedMaterialDocument(
                        rigid.ModelList[lodIndex][partIndex].Material);
                    var materialEntry = BuildMaterialConsumerEntry(material);
                    foreach (var texture in materialEntry.Textures)
                    {
                        entry.Textures.Add(new VanillaRigidTextureReference
                        {
                            TexturePath = texture.TexturePath,
                            LodIndex = lodIndex,
                            PartIndex = partIndex,
                            Slot = texture.Slot,
                            SlotOccurrence = texture.SlotOccurrence,
                        });
                    }
                }
            }

            return entry;
        }

        private static bool IsAtlasValueGateSourcePackReference(
            BatchState state,
            AtlasValueGateSourceReference reference)
        {
            var consumerPath = Normalize(reference.ConsumerAssetPath);
            return consumerPath.Length != 0 && state.Source.ContainsFile(consumerPath);
        }

        private static Dictionary<string, AtlasValueGateSourceTexture>
            BuildAtlasValueGateSourceTextureIndex(
                BatchState state,
                IReadOnlySet<string>? targetTexturePaths = null)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            var containerResolutionScansBefore =
                state.GameplayTraversalContainerResolutionScans;
            var containerResolutionCacheHitsBefore =
                state.GameplayTraversalContainerResolutionCacheHits;
            var wsModelConsumerCacheHitsBefore = state.WsModelConsumerCacheHits;
            var wsModelConsumerMemoryCacheHitsBefore = state.WsModelConsumerMemoryCacheHits;
            var wsModelConsumerVanillaCacheHitsBefore = state.WsModelConsumerVanillaCacheHits;
            var wsModelConsumerCacheMissesBefore = state.WsModelConsumerCacheMisses;
            var wsModelConsumerEntryBuildsBefore = state.WsModelConsumerEntryBuilds;
            var materialConsumerCacheHitsBefore = state.MaterialConsumerCacheHits;
            var materialConsumerMemoryCacheHitsBefore = state.MaterialConsumerMemoryCacheHits;
            var materialConsumerVanillaCacheHitsBefore = state.MaterialConsumerVanillaCacheHits;
            var materialConsumerCacheMissesBefore = state.MaterialConsumerCacheMisses;
            var materialConsumerEntryBuildsBefore = state.MaterialConsumerEntryBuilds;
            var wsModelXmlParseCountBefore = state.GameplayTraversalWsModelXmlParseCount;
            var materialXmlParseCountBefore = state.GameplayTraversalMaterialXmlParseCount;
            var wsModelIndexStopwatch = Stopwatch.StartNew();
            var loadedContainers = GetGameplayTraversalContainers(state);
            var referencesByTexture =
                new Dictionary<string, HashSet<AtlasValueGateSourceReference>>(
                    StringComparer.OrdinalIgnoreCase);
            var packReferencesByTexture =
                new Dictionary<string, HashSet<AtlasValueGateSourceReference>>(
                    StringComparer.OrdinalIgnoreCase);
            var textureFiles = new Dictionary<
                string,
                (PackFile File, bool IsOwnedBySourcePack)>(
                StringComparer.OrdinalIgnoreCase);

            bool TryResolveTexture(
                string texturePathValue,
                out string texturePath,
                out PackFile textureFile,
                out bool isOwnedBySourcePack)
            {
                texturePath = Normalize(texturePathValue);
                textureFile = null!;
                isOwnedBySourcePack = false;
                if (texturePath.Length == 0 ||
                    IsTexturePlaceholder(texturePath) ||
                    (targetTexturePaths != null &&
                     !targetTexturePaths.Contains(texturePath)))
                {
                    return false;
                }

                var textureContainer = FindGameplayTraversalContainer(
                    state,
                    state.Source,
                    loadedContainers,
                    texturePath);
                var resolvedFile = textureContainer?.FindFile(texturePath);
                if (textureContainer == null || resolvedFile == null)
                    return false;

                textureFile = resolvedFile;
                isOwnedBySourcePack = ReferenceEquals(textureContainer, state.Source);
                textureFiles[texturePath] = (textureFile, isOwnedBySourcePack);
                return true;
            }

            void AddReference(
                string texturePath,
                AtlasValueGateSourceReference reference)
            {
                if (!referencesByTexture.TryGetValue(texturePath, out var references))
                {
                    references = [];
                    referencesByTexture[texturePath] = references;
                }

                references.Add(reference);
                if (IsAtlasValueGateSourcePackReference(state, reference))
                {
                    if (!packReferencesByTexture.TryGetValue(
                            texturePath,
                            out var packReferences))
                    {
                        packReferences = [];
                        packReferencesByTexture[texturePath] = packReferences;
                    }

                    packReferences.Add(reference);
                }
            }

            // Start with the selected-pack closure, then add every WSModel/rigid that can be
            // reached by a unit in the culture-local army model. CA assets are read-only
            // consumers: they can keep a DDS resident, but are never proposed rewrites.
            var reachableWsModels = state.SourceReachableAssetFiles
                .Where(path => Path.GetExtension(path).Equals(
                    ".wsmodel",
                    StringComparison.OrdinalIgnoreCase))
                .Select(Normalize)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            reachableWsModels.UnionWith(state.ReachableWsModelsByRoot.Values
                .SelectMany(paths => paths)
                .Select(Normalize));
            if (state.ArmyResidencyModel != null)
            {
                reachableWsModels.UnionWith(
                    state.ArmyResidencyModel.ExpectedWsModelOccurrencesByUnit.Values
                        .SelectMany(occurrences => occurrences.ByWsModel.Keys)
                        .Where(path => Path.GetExtension(path).Equals(
                            ".wsmodel",
                            StringComparison.OrdinalIgnoreCase))
                        .Select(Normalize));
            }

            var reachableWsModelPaths = reachableWsModels
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            state.AtlasValueGateReachableWsModelCount = reachableWsModelPaths.Length;
            var sourceWsModelCount = 0;
            var caWsModelCount = 0;
            var unresolvedWsModelCount = 0;
            var resolvedWsModels = new List<
                (string Path, IPackFileContainer Container, PackFile File)>();
            ReportProgress(
                state.Progress,
                "Evaluating atlas value",
                0,
                reachableWsModelPaths.Length,
                "Indexing WSModel texture consumers");
            for (var wsModelIndex = 0;
                 wsModelIndex < reachableWsModelPaths.Length;
                 wsModelIndex++)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                ReportPeriodicProgress(
                    state.Progress,
                    "Evaluating atlas value",
                    wsModelIndex + 1,
                    reachableWsModelPaths.Length,
                    "Indexing WSModel texture consumers");
                var wsModelPath = reachableWsModelPaths[wsModelIndex];
                var wsContainer = FindGameplayTraversalContainer(
                    state,
                    state.Source,
                    loadedContainers,
                    wsModelPath);
                var wsFile = wsContainer?.FindFile(wsModelPath);
                if (wsContainer == null || wsFile == null)
                {
                    unresolvedWsModelCount++;
                    continue;
                }

                if (ReferenceEquals(wsContainer, state.Source))
                    sourceWsModelCount++;
                else if (wsContainer.IsCaPackFile)
                    caWsModelCount++;
                else
                    unresolvedWsModelCount++;

                resolvedWsModels.Add((wsModelPath, wsContainer, wsFile));
            }

            void AddWsModelTextureReferences(
                string wsModelPath,
                VanillaWsModelConsumerEntry wsModelEntry,
                bool sourceMaterialOnly)
            {
                foreach (var materialBinding in wsModelEntry.Materials)
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    MeshKey? mesh = wsModelEntry.GeometryPath.Length != 0 &&
                                    materialBinding.LodIndex.HasValue &&
                                    materialBinding.PartIndex.HasValue
                        ? new MeshKey(
                            wsModelEntry.GeometryPath,
                            materialBinding.LodIndex.Value,
                            materialBinding.PartIndex.Value)
                        : null;

                    var materialPath = materialBinding.MaterialPath;
                    if (materialPath.Length == 0)
                        continue;

                    var materialContainer = FindGameplayTraversalContainer(
                        state,
                        state.Source,
                        loadedContainers,
                        materialPath);
                    var materialFile = materialContainer?.FindFile(materialPath);
                    if (sourceMaterialOnly &&
                        !ReferenceEquals(materialContainer, state.Source))
                    {
                        continue;
                    }

                    if (materialContainer == null ||
                        materialFile == null ||
                        !TryGetMaterialConsumerEntry(
                            state,
                            materialContainer,
                            materialPath,
                            materialFile,
                            out var materialEntry))
                    {
                        continue;
                    }

                    foreach (var texture in materialEntry.Textures)
                    {
                        state.CancellationToken.ThrowIfCancellationRequested();
                        if (!TryResolveTexture(
                                texture.TexturePath,
                                out var texturePath,
                                out _,
                                out _))
                        {
                            continue;
                        }

                        AddReference(
                            texturePath,
                            new AtlasValueGateSourceReference(
                                mesh,
                                wsModelPath.ToLowerInvariant(),
                                texture.Slot,
                                texture.SlotOccurrence,
                                AtlasValueGateSourceReferenceKind.WsModelMaterial));
                    }
                }
            }

            var cachedCaWsModels = new List<
                (string Path, IPackFileContainer Container, VanillaWsModelConsumerEntry Entry)>();
            var useVanillaTextureReverseIndex =
                targetTexturePaths != null &&
                state.VanillaAtlasConsumerCache != null &&
                resolvedWsModels.All(resolved =>
                    ReferenceEquals(resolved.Container, state.Source) ||
                    resolved.Container.IsCaPackFile);
            var vanillaCache = state.VanillaAtlasConsumerCache;
            if (useVanillaTextureReverseIndex)
            {
                foreach (var resolved in resolvedWsModels)
                {
                    if (ReferenceEquals(resolved.Container, state.Source))
                        continue;

                    state.CancellationToken.ThrowIfCancellationRequested();
                    if (TryGetVanillaCachedWsModelConsumerEntry(
                            state,
                            resolved.Container,
                            resolved.Path,
                            out var wsModelEntry))
                    {
                        var entryIsComplete = true;
                        foreach (var materialBinding in wsModelEntry.Materials)
                        {
                            state.CancellationToken.ThrowIfCancellationRequested();
                            var materialPath = Normalize(materialBinding.MaterialPath);
                            if (materialPath.Length == 0)
                                continue;

                            var materialContainer = FindGameplayTraversalContainer(
                                state,
                                state.Source,
                                loadedContainers,
                                materialPath);
                            var materialFile = materialContainer?.FindFile(materialPath);
                            if (materialContainer == null || materialFile == null)
                            {
                                entryIsComplete = false;
                                break;
                            }

                            // A source-pack material is intentionally handled below. It is
                            // mutable and therefore cannot be represented by the vanilla
                            // reverse index.
                            if (ReferenceEquals(materialContainer, state.Source))
                                continue;

                            if (!materialContainer.IsCaPackFile ||
                                !TryGetVanillaCachedMaterialConsumerEntry(
                                    state,
                                    materialContainer,
                                    materialPath,
                                    out _))
                            {
                                entryIsComplete = false;
                                break;
                            }
                        }

                        if (entryIsComplete)
                        {
                            cachedCaWsModels.Add((
                                resolved.Path,
                                resolved.Container,
                                wsModelEntry));
                            continue;
                        }
                    }

                    useVanillaTextureReverseIndex = false;
                    break;
                }
            }

            if (useVanillaTextureReverseIndex)
            {
                var sourceWsModels = resolvedWsModels
                    .Where(resolved => ReferenceEquals(resolved.Container, state.Source))
                    .ToArray();
                ReportProgress(
                    state.Progress,
                    "Evaluating atlas value",
                    0,
                    sourceWsModels.Length,
                    "Indexing source WSModel texture consumers");
                for (var sourceIndex = 0; sourceIndex < sourceWsModels.Length; sourceIndex++)
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    ReportPeriodicProgress(
                        state.Progress,
                        "Evaluating atlas value",
                        sourceIndex + 1,
                        sourceWsModels.Length,
                        "Indexing source WSModel texture consumers");
                    var resolved = sourceWsModels[sourceIndex];
                    if (TryGetWsModelConsumerEntry(
                            state,
                            resolved.Container,
                            resolved.Path,
                            resolved.File,
                            out var wsModelEntry))
                    {
                        AddWsModelTextureReferences(
                            resolved.Path,
                            wsModelEntry,
                            sourceMaterialOnly: false);
                    }
                }

                // CA WSModels are immutable, but a selected source pack may override one of
                // their material paths. Those bindings still need the source material's
                // current texture relationships; only the unaffected CA relationships can
                // use the reverse index below.
                foreach (var cached in cachedCaWsModels)
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    AddWsModelTextureReferences(
                        cached.Path,
                        cached.Entry,
                        sourceMaterialOnly: true);
                }

                foreach (var targetTexturePath in targetTexturePaths!)
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    if (!TryResolveTexture(
                            targetTexturePath,
                            out var resolvedTargetTexturePath,
                            out _,
                            out _))
                    {
                        continue;
                    }

                    foreach (var consumer in vanillaCache!.FindTextureConsumers(
                                 resolvedTargetTexturePath,
                                 reachableWsModels))
                    {
                        state.CancellationToken.ThrowIfCancellationRequested();
                        var wsContainer = FindGameplayTraversalContainer(
                            state,
                            state.Source,
                            loadedContainers,
                            consumer.WsModelPath);
                        var materialContainer = FindGameplayTraversalContainer(
                            state,
                            state.Source,
                            loadedContainers,
                            consumer.MaterialPath);
                        var wsFile = wsContainer?.FindFile(consumer.WsModelPath);
                        var materialFile = materialContainer?.FindFile(consumer.MaterialPath);
                        if (wsContainer == null ||
                            materialContainer == null ||
                            wsFile == null ||
                            materialFile == null ||
                            !string.Equals(
                                vanillaCache.GetContainerScope(wsContainer),
                                consumer.WsModelContainerScope,
                                StringComparison.Ordinal) ||
                            !string.Equals(
                                vanillaCache.GetContainerScope(materialContainer),
                                consumer.MaterialContainerScope,
                                StringComparison.Ordinal))
                        {
                            continue;
                        }

                        MeshKey? mesh = consumer.GeometryPath.Length != 0 &&
                                        consumer.LodIndex.HasValue &&
                                        consumer.PartIndex.HasValue
                            ? new MeshKey(
                                consumer.GeometryPath,
                                consumer.LodIndex.Value,
                                consumer.PartIndex.Value)
                            : null;
                        AddReference(
                            resolvedTargetTexturePath,
                            new AtlasValueGateSourceReference(
                                mesh,
                                consumer.WsModelPath.ToLowerInvariant(),
                                consumer.Slot,
                                consumer.SlotOccurrence,
                                AtlasValueGateSourceReferenceKind.WsModelMaterial));
                    }
                }
            }
            else
            {
                ReportProgress(
                    state.Progress,
                    "Evaluating atlas value",
                    0,
                    resolvedWsModels.Count,
                    "Parsing WSModel texture consumers");
                for (var resolvedIndex = 0;
                     resolvedIndex < resolvedWsModels.Count;
                     resolvedIndex++)
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    ReportPeriodicProgress(
                        state.Progress,
                        "Evaluating atlas value",
                        resolvedIndex + 1,
                        resolvedWsModels.Count,
                        "Parsing WSModel texture consumers");
                    var resolved = resolvedWsModels[resolvedIndex];
                    if (!TryGetWsModelConsumerEntry(
                            state,
                            resolved.Container,
                            resolved.Path,
                            resolved.File,
                            out var wsModelEntry))
                    {
                        continue;
                    }

                    AddWsModelTextureReferences(
                        resolved.Path,
                        wsModelEntry,
                        sourceMaterialOnly: false);
                }
            }

            state.AtlasValueGateSourceWsModelCount = sourceWsModelCount;
            state.AtlasValueGateCaWsModelCount = caWsModelCount;
            state.AtlasValueGateUnresolvedWsModelCount = unresolvedWsModelCount;
            AddPhaseDuration(
                state,
                "Index WSModel texture consumers",
                wsModelIndexStopwatch.Elapsed);

            var directRigidIndexStopwatch = Stopwatch.StartNew();
            var directRigidPaths = state.SourceReachableAssetFiles
                .Where(path => Path.GetExtension(path).Equals(
                    ".rigid_model_v2",
                    StringComparison.OrdinalIgnoreCase))
                .Select(Normalize)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (state.ArmyResidencyModel != null)
            {
                directRigidPaths.UnionWith(
                    state.ArmyResidencyModel.ExpectedWsModelOccurrencesByUnit.Values
                        .SelectMany(occurrences => occurrences.ByWsModel.Keys)
                        .Where(path => Path.GetExtension(path).Equals(
                            ".rigid_model_v2",
                            StringComparison.OrdinalIgnoreCase))
                        .Select(Normalize));
            }

            var directRigidPathValues = directRigidPaths
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            state.AtlasValueGateDirectRigidPathCount = directRigidPathValues.Length;
            ReportProgress(
                state.Progress,
                "Evaluating atlas value",
                0,
                directRigidPathValues.Length,
                "Indexing direct rigid texture consumers");
            for (var rigidIndex = 0;
                 rigidIndex < directRigidPathValues.Length;
                 rigidIndex++)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                ReportPeriodicProgress(
                    state.Progress,
                    "Evaluating atlas value",
                    rigidIndex + 1,
                    directRigidPathValues.Length,
                    "Indexing direct rigid texture consumers");
                var rigidPath = directRigidPathValues[rigidIndex];
                var rigidContainer = FindGameplayTraversalContainer(
                    state,
                    state.Source,
                    loadedContainers,
                    rigidPath);
                var rigidFile = rigidContainer?.FindFile(rigidPath);
                if (rigidContainer == null ||
                    rigidFile == null ||
                    !TryGetRigidConsumerEntry(
                        state,
                        rigidContainer,
                        rigidPath,
                        rigidFile,
                        out var rigidEntry))
                {
                    continue;
                }

                foreach (var texture in rigidEntry.Textures)
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    if (!TryResolveTexture(
                            texture.TexturePath,
                            out var texturePath,
                            out _,
                            out _))
                    {
                        continue;
                    }

                    AddReference(
                        texturePath,
                        new AtlasValueGateSourceReference(
                            new MeshKey(
                                rigidPath,
                                texture.LodIndex,
                                texture.PartIndex),
                            rigidPath.ToLowerInvariant(),
                            texture.Slot,
                            texture.SlotOccurrence,
                            AtlasValueGateSourceReferenceKind.RigidEmbedded));
                }
            }

            AddPhaseDuration(
                state,
                "Index direct rigid texture consumers",
                directRigidIndexStopwatch.Elapsed);

            var directVmdIndexStopwatch = Stopwatch.StartNew();
            var directVmdTextures = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var directSourcePackVmdTextures = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var vmdDocuments = state.VmdDocuments.ToArray();
            state.AtlasValueGateDirectVmdPathCount = vmdDocuments.Length;
            ReportProgress(
                state.Progress,
                "Evaluating atlas value",
                0,
                vmdDocuments.Length,
                "Indexing direct VMD texture consumers");
            for (var vmdIndex = 0; vmdIndex < vmdDocuments.Length; vmdIndex++)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                ReportPeriodicProgress(
                    state.Progress,
                    "Evaluating atlas value",
                    vmdIndex + 1,
                    vmdDocuments.Length,
                    "Indexing direct VMD texture consumers");
                var vmdPath = Normalize(vmdDocuments[vmdIndex].Key);
                var vmd = vmdDocuments[vmdIndex].Value;
                var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var childVmds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectVmdReferences(vmd, models, childVmds, textures);
                var normalizedTextures = textures
                    .Select(Normalize)
                    .ToArray();
                directVmdTextures.UnionWith(normalizedTextures);

                // Direct VMD references are not rewritten by atlas processing. Keep the
                // source-pack subset separate: CA VMDs must continue to affect residency and
                // physical-retirement safety, but must not veto pack-local retirement credit.
                var vmdContainer = FindGameplayTraversalContainer(
                    state,
                    state.Source,
                    loadedContainers,
                    vmdPath);
                if (ReferenceEquals(vmdContainer, state.Source))
                    directSourcePackVmdTextures.UnionWith(normalizedTextures);
            }

            AddPhaseDuration(
                state,
                "Index direct VMD texture consumers",
                directVmdIndexStopwatch.Elapsed);

            state.AtlasValueGateRootsByWsModel ??=
                BuildArmyRootsByWsModel(
                    state,
                    state.CancellationToken,
                    state.Progress);

            var result = new Dictionary<string, AtlasValueGateSourceTexture>(
                StringComparer.OrdinalIgnoreCase);
            var textureReferences = referencesByTexture.ToArray();
            ReportProgress(
                state.Progress,
                "Evaluating atlas value",
                0,
                textureReferences.Length,
                "Estimating source texture residency");
            for (var textureIndex = 0;
                 textureIndex < textureReferences.Length;
                 textureIndex++)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                ReportPeriodicProgress(
                    state.Progress,
                    "Evaluating atlas value",
                    textureIndex + 1,
                    textureReferences.Length,
                    "Estimating source texture residency");
                var (texturePath, references) = textureReferences[textureIndex];
                if (!textureFiles.TryGetValue(texturePath, out var source))
                    continue;
                var packReferences = packReferencesByTexture.TryGetValue(
                    texturePath,
                    out var existingPackReferences)
                    ? existingPackReferences
                    : [];

                DdsBcnResidencyEstimate bcn;
                try
                {
                    if (!DdsBcnResidencyEstimator.TryEstimate(
                            source.File.DataSource.PeekData(148),
                            out bcn))
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                result[texturePath] = new AtlasValueGateSourceTexture(
                    bcn.Bytes,
                    source.IsOwnedBySourcePack,
                    directVmdTextures.Contains(texturePath),
                    directSourcePackVmdTextures.Contains(texturePath),
                    references,
                    packReferences);
            }

            state.AtlasValueGateContainerResolutionScans =
                state.GameplayTraversalContainerResolutionScans -
                containerResolutionScansBefore;
            state.AtlasValueGateContainerResolutionCacheHits =
                state.GameplayTraversalContainerResolutionCacheHits -
                containerResolutionCacheHitsBefore;
            state.AtlasValueGateWsModelConsumerCacheHits =
                state.WsModelConsumerCacheHits - wsModelConsumerCacheHitsBefore;
            state.AtlasValueGateWsModelConsumerMemoryCacheHits =
                state.WsModelConsumerMemoryCacheHits - wsModelConsumerMemoryCacheHitsBefore;
            state.AtlasValueGateWsModelConsumerVanillaCacheHits =
                state.WsModelConsumerVanillaCacheHits - wsModelConsumerVanillaCacheHitsBefore;
            state.AtlasValueGateWsModelConsumerCacheMisses =
                state.WsModelConsumerCacheMisses - wsModelConsumerCacheMissesBefore;
            state.AtlasValueGateWsModelConsumerEntryBuilds =
                state.WsModelConsumerEntryBuilds - wsModelConsumerEntryBuildsBefore;
            state.AtlasValueGateMaterialConsumerCacheHits =
                state.MaterialConsumerCacheHits - materialConsumerCacheHitsBefore;
            state.AtlasValueGateMaterialConsumerMemoryCacheHits =
                state.MaterialConsumerMemoryCacheHits - materialConsumerMemoryCacheHitsBefore;
            state.AtlasValueGateMaterialConsumerVanillaCacheHits =
                state.MaterialConsumerVanillaCacheHits - materialConsumerVanillaCacheHitsBefore;
            state.AtlasValueGateMaterialConsumerCacheMisses =
                state.MaterialConsumerCacheMisses - materialConsumerCacheMissesBefore;
            state.AtlasValueGateMaterialConsumerEntryBuilds =
                state.MaterialConsumerEntryBuilds - materialConsumerEntryBuildsBefore;
            state.AtlasValueGateGameplayTraversalWsModelXmlParseCount =
                state.GameplayTraversalWsModelXmlParseCount - wsModelXmlParseCountBefore;
            state.AtlasValueGateGameplayTraversalMaterialXmlParseCount =
                state.GameplayTraversalMaterialXmlParseCount - materialXmlParseCountBefore;

            BuildAtlasValueGateSourceTextureReverseIndex(state, result);
            state.VanillaAtlasConsumerCache?.Save();
            return result;
        }

        private static void BuildAtlasValueGateSourceTextureReverseIndex(
            BatchState state,
            IReadOnlyDictionary<string, AtlasValueGateSourceTexture> sourceIndex)
        {
            var texturesByReference =
                new Dictionary<AtlasValueGateSourceReference, List<string>>();
            state.AtlasValueGateSourceTextureOrder.Clear();

            var textureOrder = 0;
            foreach (var (texturePath, sourceTexture) in sourceIndex)
            {
                var normalizedTexturePath = Normalize(texturePath);
                state.AtlasValueGateSourceTextureOrder[normalizedTexturePath] =
                    textureOrder++;
                foreach (var reference in sourceTexture.References)
                {
                    if (!texturesByReference.TryGetValue(
                            reference,
                            out var texturePaths))
                    {
                        texturePaths = [];
                        texturesByReference[reference] = texturePaths;
                    }

                    texturePaths.Add(normalizedTexturePath);
                }
            }

            state.AtlasValueGateSourceTexturesByReference = texturesByReference;
        }

        private static bool TryGetGeneratedAtlasBcnCost(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates,
            out long bytes)
            => TryGetGeneratedAtlasBcnCost(
                state,
                candidates,
                out bytes,
                out _);

        private static bool TryGetGeneratedAtlasBcnCost(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates,
            out long bytes,
            out Dictionary<string, long> bytesBySlot)
        {
            bytes = 0;
            bytesBySlot = new Dictionary<string, long>(
                StringComparer.OrdinalIgnoreCase);
            try
            {
                // The value gate only needs a conservative residency estimate. Avoid
                // cropped-content hashing here; execution may deduplicate later and therefore
                // use the same or less BCn memory than this estimate.
                var sharedPlan = CreateSharedAtlasPlan(
                    state,
                    candidates,
                    deduplicateByContent: false);
                foreach (var channel in AtlasChannels)
                {
                    var sourceDimensions = new Dictionary<int, (int Width, int Height)>();
                    foreach (var source in sharedPlan.Batch.Sources)
                    {
                        var representative = source.Representative;
                        if (representative.ResolvedChannels.Contains(channel.Slot) &&
                            representative.ChannelDimensions.TryGetValue(
                                channel.Slot,
                                out var dimensions))
                        {
                            sourceDimensions[source.Id] = GetScaledAtlasDimensions(
                                source.Representative,
                                dimensions.Width,
                                dimensions.Height);
                        }
                    }

                    var requiresConstantOnlyAtlas =
                        RequiresConstantOnlyMaskAtlas(
                            channel.Slot,
                            sharedPlan.Batch.Sources);
                    if (sourceDimensions.Count == 0 && !requiresConstantOnlyAtlas)
                        continue;

                    var outputDimensions = TextureAtlasBuilder.CalculateOutputDimensions(
                        sharedPlan.Plan,
                        sourceDimensions,
                        PackAtlasMaxSize);
                    var bytesPerBlock = GetAtlasBytesPerBlock(
                        GetAtlasChannelFormat(candidates, channel.Slot, channel.Type));
                    var channelBytes = CalculateBcnMipChainBytes(
                        outputDimensions.Width,
                        outputDimensions.Height,
                        bytesPerBlock);
                    bytesBySlot[channel.Slot] = channelBytes;
                    bytes = checked(bytes + channelBytes);
                }

                return true;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or ArgumentException or OverflowException)
            {
                bytes = 0;
                bytesBySlot.Clear();
                return false;
            }
        }

        private static long CalculateBcnMipChainBytes(
            int width,
            int height,
            int bytesPerBlock)
        {
            long total = 0;
            var mipWidth = Math.Max(1, width);
            var mipHeight = Math.Max(1, height);
            while (true)
            {
                var blocksWide = Math.Max(1L, (mipWidth + 3L) / 4L);
                var blocksHigh = Math.Max(1L, (mipHeight + 3L) / 4L);
                total = checked(total + checked(blocksWide * blocksHigh * bytesPerBlock));

                if (mipWidth == 1 && mipHeight == 1)
                    break;
                mipWidth = Math.Max(1, mipWidth / 2);
                mipHeight = Math.Max(1, mipHeight / 2);
            }

            return total;
        }
    }
}
