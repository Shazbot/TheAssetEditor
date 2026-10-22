using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Editors.ImportExport.Importing.Importers.PngToDds;
using Editors.ImportExport.Importing.Importers.PngToDds.Helpers;
using Editors.ImportExport.TextureAtlas;

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

namespace Editors.PackOptimizer.Core
{
    public sealed partial class PackTextureAtlasBatchService
    {
        private static List<List<AtlasCandidate>> CreateBatches(
            BatchState state,
            List<AtlasCandidate> candidates,
            bool packWide,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            List<AtlasCandidate> planningCandidates;
            if (packWide)
            {
                var rootsByMesh = BuildCandidateRootVmdPaths(state, candidates);
                var localitySignatureBySource = candidates
                    .GroupBy(GetAtlasPlanningSourceIdentity)
                    .ToDictionary(
                        group => group.Key,
                        group =>
                        {
                            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var candidate in group)
                            {
                                if (rootsByMesh.TryGetValue(candidate.Key, out var candidateRoots))
                                    roots.UnionWith(candidateRoots);
                                else
                                    roots.Add(Normalize(candidate.RootVmdPath));
                            }

                            return string.Join(
                                "\u001f",
                                roots.OrderBy(root => root, StringComparer.OrdinalIgnoreCase));
                        });

                planningCandidates = candidates
                    .OrderBy(
                        candidate => localitySignatureBySource[
                            GetAtlasPlanningSourceIdentity(candidate)],
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(BuildAtlasPlanningOrderKey, StringComparer.Ordinal)
                    .ThenBy(x => x.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Key.LodIndex)
                    .ThenBy(x => x.Key.PartIndex)
                    .ToList();
            }
            else
            {
                planningCandidates = candidates;
            }

            var batches = new List<List<AtlasCandidate>>();
            var current = new List<AtlasCandidate>();
            var currentPlanningRepresentatives = new List<AtlasCandidate>();
            var currentPlanningIdentities = new HashSet<AtlasPlanningSourceIdentity>();

            for (var candidateIndex = 0; candidateIndex < planningCandidates.Count; candidateIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = planningCandidates[candidateIndex];
                ReportProgress(
                    progress,
                    "Planning atlas batches",
                    candidateIndex + 1,
                    planningCandidates.Count,
                    candidate.Key.ToString());

                if (!CanCreatePlan(state, [candidate], out var singleError))
                {
                    RecordSkip(
                        state,
                        candidate.RootVmdPath,
                        candidate.Key,
                        candidate.Usages.FirstOrDefault()?.AssetPath ?? string.Empty,
                        $"Atlas planner rejected this mesh: {singleError}");
                    continue;
                }

                var planningIdentity = GetAtlasPlanningSourceIdentity(candidate);

                if (current.Count == 0)
                {
                    current.Add(candidate);
                    currentPlanningRepresentatives.Add(candidate);
                    currentPlanningIdentities.Add(planningIdentity);
                    continue;
                }

                // Exact source/crop duplicates consume no additional atlas space. Keep every
                // candidate in the execution batch, but only one representative in fit checks.
                if (currentPlanningIdentities.Contains(planningIdentity))
                {
                    current.Add(candidate);
                    continue;
                }

                var trialPlanningRepresentatives =
                    currentPlanningRepresentatives.Concat([candidate]).ToList();
                if (CanCreatePlan(state, trialPlanningRepresentatives, out _))
                {
                    current.Add(candidate);
                    currentPlanningRepresentatives.Add(candidate);
                    currentPlanningIdentities.Add(planningIdentity);
                    continue;
                }

                if (current.Count >= 2)
                {
                    batches.Add(current);
                }
                else
                {
                    var orphan = current[0];
                    RecordSkip(
                        state,
                        orphan.RootVmdPath,
                        orphan.Key,
                        orphan.Usages.FirstOrDefault()?.AssetPath ?? string.Empty,
                        "Could not form a compatible multi-mesh atlas before the atlas size/layout limit was reached.");
                }

                current = [candidate];
                currentPlanningRepresentatives = [candidate];
                currentPlanningIdentities = new HashSet<AtlasPlanningSourceIdentity>
                {
                    planningIdentity
                };
            }

            if (current.Count >= 2)
            {
                batches.Add(current);
            }
            else if (current.Count == 1)
            {
                var orphan = current[0];
                RecordSkip(
                    state,
                    orphan.RootVmdPath,
                    orphan.Key,
                    orphan.Usages.FirstOrDefault()?.AssetPath ?? string.Empty,
                    packWide
                        ? "No second compatible mesh was available in the pack-wide atlas candidate set."
                        : "No second compatible mesh was available in this VMD dependency set.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(
                progress,
                "Optimizing atlas batches",
                item: "Checking atlas size limits");
            var pixelOptimized = OptimizeMaxSizeBatchesForPixelArea(state, batches);
            if (packWide)
            {
                state.ExpectedArmyResidentPixelsBeforeLocality +=
                    CalculateExpectedArmyResidentPixels(state, pixelOptimized);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(
                progress,
                "Optimizing atlas batches",
                item: "Optimizing VMD locality");
            var localityOptimized = packWide
                ? OptimizeBatchesForVmdLocality(state, pixelOptimized)
                : pixelOptimized;

            if (packWide)
            {
                state.ExpectedArmyResidentPixelsAfterLocality +=
                    CalculateExpectedArmyResidentPixels(state, localityOptimized);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(
                progress,
                "Optimizing atlas batches",
                item: "Evaluating compatible mesh merges");
            var mergeOptimized = OptimizeBatchesForMergeAffinity(state, localityOptimized);
            var familyClosed = CloseLodAtlasFamilies(state, mergeOptimized);
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(
                progress,
                "Evaluating atlas value",
                item: "Applying residency and draw-value gates");
            var valueOptimized = state.MergeCompatibleMeshesEnabled
                ? FilterBatchesForMergeValue(state, familyClosed)
                : familyClosed;
            if (state.MergeCompatibleMeshesEnabled)
            {
                // The value gate runs after merge-aware repartitioning and can admit
                // texture-only groups that become real atlas-assisted mesh merges later in
                // the pipeline. Recompute the final scenario draw estimate from the batches
                // that actually survived the value gate; the merge-aware pass's intermediate
                // total must not be reported as the final total.
                state.ExpectedArmyDrawCallsEliminatedAfterValueGate +=
                    CalculateExpectedArmyDrawCallsEliminatedForBatches(
                        state,
                        valueOptimized);
            }
            if (packWide)
            {
                state.ExpectedArmyResidentPixelsAfterMergeAware +=
                    CalculateExpectedArmyResidentPixels(state, valueOptimized);
            }

            return valueOptimized;
        }

        private static List<List<AtlasCandidate>> CloseLodAtlasFamilies(
            BatchState state,
            IReadOnlyList<List<AtlasCandidate>> batches)
        {
            var closed = CloseStructuralAndIdentityAtlasFamilies(state, batches);
            // The source-texture sibling rule is independent of LOD-aware planning: it applies
            // whenever atlasing runs.
            return CloseSourceTextureSiblingGroups(state, closed)
                .Where(batch => batch.Count != 0)
                .ToList();
        }

        private static List<List<AtlasCandidate>> CloseStructuralAndIdentityAtlasFamilies(
            BatchState state,
            IReadOnlyList<List<AtlasCandidate>> batches)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            state.LodAwareAtlasFamilies.Clear();
            var working = batches
                .Select(batch => batch
                    .GroupBy(candidate => candidate.Key)
                    .Select(group => group.First())
                    .ToList())
                .ToList();
            if (!state.LodAwareAtlasPlanningEnabled || working.Count == 0)
                return working;

            var candidateByKey = working
                .SelectMany(batch => batch)
                .GroupBy(candidate => candidate.Key)
                .ToDictionary(group => group.Key, group => group.First());
            var structuralComponents = BuildAtlasStructuralMergeComponents(
                state,
                candidateByKey.Values);
            var mergeAffinityGroups = BuildMergeAffinityGroups(
                state,
                candidateByKey.Values,
                recordDiagnostics: false);

            // Structural components are the atomic unit of family attachment. Do this before
            // exact-placement closure so a candidate cannot be moved to a host atlas while its
            // pre-atlas merge partner is left behind in another batch.
            foreach (var component in structuralComponents
                         .OrderBy(component => component.GeometryPath, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(component => component.LodIndex)
                         .ThenBy(component => component.Members.Min(member => member.PartIndex)))
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                var members = component.Members
                    .Where(candidateByKey.ContainsKey)
                    .Select(member => candidateByKey[member])
                    .ToList();
                if (members.Count == 0)
                    continue;

                // A missing member is not safe to rewrite: the rigid material/UV state is
                // shared by every structural part, even when one part was not in the selected
                // atlas population.
                if (members.Count != component.Members.Length ||
                    !CanRewriteStructuralComponentAsOneMaterial(state, members))
                {
                    RemoveAtlasFamilyMembers(
                        state,
                        working,
                        members,
                        "LOD-aware atlas planning left a structural component unchanged because " +
                        "the complete component or one compatible post-atlas material variant " +
                        "was not available.");
                    state.LodAwareAtlasStructuralComponentsRejected++;
                    continue;
                }

                var occupiedBatchIds = GetBatchIdsForKeys(working, members.Select(x => x.Key));
                if (occupiedBatchIds.Count <= 1)
                    continue;

                if (TryMoveAtlasFamilyMembersToBestHost(
                        state,
                        working,
                        members,
                        mergeAffinityGroups,
                        out _))
                {
                    state.LodAwareAtlasStructuralComponentsReplanned++;
                    state.LodAwareAtlasFamilyClosureCandidates += members.Count;
                    if (members.Select(member => member.Key.LodIndex).Distinct().Count() > 1)
                        state.LodAwareAtlasFamiliesSpanningMultipleLods++;
                }
                else
                {
                    RemoveAtlasFamilyMembers(
                        state,
                        working,
                        members,
                        "LOD-aware atlas planning rejected a structural component because it " +
                        "could not be hosted by one atlas page without splitting the component.");
                    state.LodAwareAtlasStructuralComponentsRejected++;
                }
            }

            // Close exact placement families after structural components are atomic. The
            // planning identity intentionally excludes LOD and material shader/parameters;
            // the family key adds the rigid geometry path and requires at least two LODs so
            // unrelated VMDs that happen to share a texture region remain under the normal
            // locality planner. ProcessBatch will still generate separate material variants
            // from each original material document while the shared atlas placement is emitted
            // once.
            var identityGroups = working
                .SelectMany(batch => batch)
                .GroupBy(candidate => (
                    candidate.Key.GeometryPath,
                    Identity: GetAtlasPlanningSourceIdentity(candidate)))
                .Where(group => group
                    .Select(candidate => candidate.Key.LodIndex)
                    .Distinct()
                    .Count() >= 2)
                .OrderBy(group => BuildAtlasPlanningOrderKey(group.First()), StringComparer.Ordinal)
                .ToList();
            foreach (var identityGroup in identityGroups)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                var members = identityGroup
                    .GroupBy(candidate => candidate.Key)
                    .Select(group => group.First())
                    .ToList();
                var occupiedBatchIds = GetBatchIdsForKeys(working, members.Select(x => x.Key));
                if (occupiedBatchIds.Count <= 1)
                    continue;

                var structuralComponentByKey = structuralComponents
                    .SelectMany(component => component.Members.Select(member =>
                        (member, component)))
                    .ToDictionary(entry => entry.member, entry => entry.component);
                var handledStructuralComponents = new HashSet<string>(StringComparer.Ordinal);
                foreach (var member in members
                             .OrderBy(candidate => candidate.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(candidate => candidate.Key.LodIndex)
                             .ThenBy(candidate => candidate.Key.PartIndex))
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    if (!structuralComponentByKey.TryGetValue(member.Key, out var component))
                        continue;

                    var componentKey = string.Join(
                        "\u001f",
                        component.Members.Select(candidate => candidate.ToString()));
                    if (!handledStructuralComponents.Add(componentKey))
                        continue;

                    var moveMembers = component.Members
                        .Where(candidateByKey.ContainsKey)
                        .Select(candidateKey => candidateByKey[candidateKey])
                        .ToList();

                    var memberBatchIds = GetBatchIdsForKeys(
                        working,
                        moveMembers.Select(candidate => candidate.Key));
                    if (memberBatchIds.Count <= 1)
                        continue;

                    if (TryMoveAtlasFamilyMembersToBestHost(
                            state,
                            working,
                            moveMembers,
                            mergeAffinityGroups,
                            out _))
                    {
                        state.LodAwareAtlasFamilyClosureCandidates += moveMembers.Count;
                        if (moveMembers.Select(candidate => candidate.Key.LodIndex).Distinct().Count() > 1)
                            state.LodAwareAtlasFamiliesSpanningMultipleLods++;
                    }
                    else if (component != null)
                    {
                        RemoveAtlasFamilyMembers(
                            state,
                            working,
                            moveMembers,
                            "LOD-aware atlas planning rejected a cross-batch family attachment " +
                            "because its structural component could not share one host atlas page.");
                        state.LodAwareAtlasStructuralComponentsRejected++;
                    }
                }

                var nonStructuralMembers = members
                    .Where(member => !structuralComponentByKey.ContainsKey(member.Key))
                    .ToList();
                if (nonStructuralMembers.Count != 0 &&
                    GetBatchIdsForKeys(
                        working,
                        nonStructuralMembers.Select(candidate => candidate.Key)).Count > 1)
                {
                    if (TryMoveAtlasFamilyMembersToBestHost(
                            state,
                            working,
                            nonStructuralMembers,
                            mergeAffinityGroups,
                            out _))
                    {
                        state.LodAwareAtlasFamilyClosureCandidates += nonStructuralMembers.Count;
                        if (nonStructuralMembers.Select(candidate => candidate.Key.LodIndex).Distinct().Count() > 1)
                            state.LodAwareAtlasFamiliesSpanningMultipleLods++;
                    }
                }

                var remainingBatchIds = GetBatchIdsForKeys(
                    working,
                    members.Select(candidate => candidate.Key));
                if (remainingBatchIds.Count == 1 && members.Count > 1)
                {
                    state.LodAwareAtlasFamilyClosures++;
                    state.LodAwareAtlasFamilyClosureCandidates += members.Count;
                }
            }

            foreach (var component in structuralComponents)
            {
                var members = component.Members
                    .Where(candidateByKey.ContainsKey)
                    .Select(candidate => candidateByKey[candidate])
                    .ToList();
                if (members.Count == component.Members.Length &&
                    GetBatchIdsForKeys(working, members.Select(candidate => candidate.Key)).Count == 1)
                {
                    RegisterLodAtlasFamily(state, members.Select(candidate => candidate.Key));
                }
            }

            foreach (var identityGroup in working
                         .SelectMany(batch => batch)
                         .GroupBy(candidate => (
                             candidate.Key.GeometryPath,
                             Identity: GetAtlasPlanningSourceIdentity(candidate)))
                         .Where(group => group
                             .Select(candidate => candidate.Key.LodIndex)
                             .Distinct()
                             .Count() >= 2))
            {
                var members = identityGroup
                    .Select(candidate => candidate.Key)
                    .Distinct()
                    .ToArray();
                if (members.Length >= 2 &&
                    GetBatchIdsForKeys(working, members).Count == 1)
                {
                    RegisterLodAtlasFamily(state, members);
                }
            }

            return working
                .Where(batch => batch.Count != 0)
                .ToList();
        }

        private static bool TryMoveAtlasFamilyMembersToBestHost(
            BatchState state,
            List<List<AtlasCandidate>> batches,
            IReadOnlyList<AtlasCandidate> members,
            IReadOnlyList<MergeAffinityGroup> mergeAffinityGroups,
            out int hostBatchIndex)
        {
            hostBatchIndex = -1;
            var memberKeys = members.Select(candidate => candidate.Key).ToHashSet();
            var occupiedBatchIds = GetBatchIdsForKeys(batches, memberKeys);
            if (occupiedBatchIds.Count <= 1)
            {
                hostBatchIndex = occupiedBatchIds.FirstOrDefault(-1);
                return hostBatchIndex >= 0;
            }

            var baselineMergeAffinity = CalculateMergeAffinityScore(
                batches,
                mergeAffinityGroups);
            var bestMergeAffinity = int.MinValue;
            long bestIncrementalPixels = long.MaxValue;
            foreach (var candidateHost in occupiedBatchIds.OrderBy(index => index))
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                var proposed = batches[candidateHost]
                    .Concat(members)
                    .GroupBy(candidate => candidate.Key)
                    .Select(group => group.First())
                    .ToList();
                if (!CanCreatePlan(state, proposed, out _))
                    continue;

                var baselinePixels = TryGetGeneratedAtlasPixelCost(
                    state,
                    batches[candidateHost],
                    out var baselineCost,
                    out _,
                    deduplicateByContent: true)
                    ? baselineCost
                    : long.MaxValue;
                var proposedPixels = TryGetGeneratedAtlasPixelCost(
                    state,
                    proposed,
                    out var proposedCost,
                    out _,
                    deduplicateByContent: true)
                    ? proposedCost
                    : long.MaxValue;
                if (baselinePixels == long.MaxValue || proposedPixels == long.MaxValue)
                    continue;

                var incrementalPixels = Math.Max(0, proposedPixels - baselinePixels);
                var proposedBatches = batches
                    .Select((batch, batchIndex) => batchIndex == candidateHost
                        ? proposed
                        : batch
                            .Where(candidate => !memberKeys.Contains(candidate.Key))
                            .ToList())
                    .ToList();
                if (!PreservesMergeAffinityContributions(
                        batches,
                        proposedBatches,
                        mergeAffinityGroups))
                {
                    continue;
                }

                var mergeAffinity = CalculateMergeAffinityScore(
                    proposedBatches,
                    mergeAffinityGroups);
                if (mergeAffinity > bestMergeAffinity ||
                    (mergeAffinity == bestMergeAffinity &&
                     (incrementalPixels < bestIncrementalPixels ||
                      (incrementalPixels == bestIncrementalPixels &&
                       (hostBatchIndex < 0 || candidateHost < hostBatchIndex)))))
                {
                    bestMergeAffinity = mergeAffinity;
                    bestIncrementalPixels = incrementalPixels;
                    hostBatchIndex = candidateHost;
                }
            }

            if (hostBatchIndex < 0)
            {
                // Every single-host proposal either lost an existing merge contribution or
                // could not be represented as one atlas. Try the stronger combined-host plan
                // before giving up, then leave the original layout untouched if it is not
                // safe either.
                return TryCombineAtlasFamilyHostBatches(
                    state,
                    batches,
                    occupiedBatchIds,
                    mergeAffinityGroups,
                    baselineMergeAffinity,
                    out hostBatchIndex);
            }

            // A family closure is only allowed to preserve or improve the merge layout. If
            // every single-host proposal would split a valid same-LOD merge, try the stronger
            // but still safe alternative of combining the connected host batches as one atlas
            // plan. If that is not feasible, leave the original batches untouched.
            if (bestMergeAffinity < baselineMergeAffinity)
            {
                return TryCombineAtlasFamilyHostBatches(
                    state,
                    batches,
                    occupiedBatchIds,
                    mergeAffinityGroups,
                    baselineMergeAffinity,
                    out hostBatchIndex);
            }

            var hostMembers = batches[hostBatchIndex]
                .Concat(members)
                .GroupBy(candidate => candidate.Key)
                .Select(group => group.First())
                .OrderBy(candidate => BuildAtlasPlanningOrderKey(candidate), StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Key.LodIndex)
                .ThenBy(candidate => candidate.Key.PartIndex)
                .ToList();
            var memberKeySet = members.Select(candidate => candidate.Key).ToHashSet();
            for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                if (batchIndex == hostBatchIndex)
                    continue;

                batches[batchIndex].RemoveAll(candidate => memberKeySet.Contains(candidate.Key));
            }

            batches[hostBatchIndex] = hostMembers;
            return true;
        }

        private static bool TryCombineAtlasFamilyHostBatches(
            BatchState state,
            List<List<AtlasCandidate>> batches,
            IReadOnlyCollection<int> occupiedBatchIds,
            IReadOnlyList<MergeAffinityGroup> mergeAffinityGroups,
            int baselineMergeAffinity,
            out int hostBatchIndex)
        {
            var selectedHostBatchIndex = occupiedBatchIds
                .OrderBy(index => index)
                .FirstOrDefault(-1);
            hostBatchIndex = selectedHostBatchIndex;
            if (selectedHostBatchIndex < 0)
                return false;

            var occupied = occupiedBatchIds.ToHashSet();
            var combined = occupied
                .OrderBy(index => index)
                .SelectMany(index => batches[index])
                .GroupBy(candidate => candidate.Key)
                .Select(group => group.First())
                .OrderBy(candidate => BuildAtlasPlanningOrderKey(candidate), StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Key.LodIndex)
                .ThenBy(candidate => candidate.Key.PartIndex)
                .ToList();
            if (!CanCreatePlan(state, combined, out _))
                return false;

            var proposedBatches = batches
                .Select((batch, batchIndex) => occupied.Contains(batchIndex)
                    ? batchIndex == selectedHostBatchIndex
                        ? combined
                        : []
                    : batch)
                .ToList();
            if (CalculateMergeAffinityScore(proposedBatches, mergeAffinityGroups) <
                baselineMergeAffinity)
            {
                return false;
            }
            if (!PreservesMergeAffinityContributions(
                    batches,
                    proposedBatches,
                    mergeAffinityGroups))
            {
                return false;
            }

            foreach (var batchIndex in occupied)
            {
                if (batchIndex != selectedHostBatchIndex)
                    batches[batchIndex].Clear();
            }

            batches[selectedHostBatchIndex] = combined;
            return true;
        }

        private static bool PreservesMergeAffinityContributions(
            IReadOnlyList<List<AtlasCandidate>> baselineBatches,
            IReadOnlyList<List<AtlasCandidate>> proposedBatches,
            IReadOnlyList<MergeAffinityGroup> mergeAffinityGroups)
        {
            var baselineBatchByMesh = BuildBatchIndexByMesh(baselineBatches);
            var proposedBatchByMesh = BuildBatchIndexByMesh(proposedBatches);
            return mergeAffinityGroups.All(group =>
                CalculateMergeAffinityContribution(group, proposedBatchByMesh) >=
                CalculateMergeAffinityContribution(group, baselineBatchByMesh) &&
                PreservesMergeAffinityComponentCoLocation(
                    group,
                    baselineBatchByMesh,
                    proposedBatchByMesh));
        }

        private static bool PreservesMergeAffinityComponentCoLocation(
            MergeAffinityGroup group,
            IReadOnlyDictionary<MeshKey, int> baselineBatchByMesh,
            IReadOnlyDictionary<MeshKey, int> proposedBatchByMesh)
        {
            var componentLabels = BuildStructuralMergeComponentLabels(
                group.Meshes,
                group.PreExistingStructuralMergePairs);
            var componentIds = componentLabels
                .Distinct()
                .ToArray();
            var baselineBatchesByComponent = BuildAffinityComponentBatchSets(
                group.Meshes,
                componentLabels,
                baselineBatchByMesh);
            var proposedBatchesByComponent = BuildAffinityComponentBatchSets(
                group.Meshes,
                componentLabels,
                proposedBatchByMesh);

            for (var leftIndex = 0; leftIndex < componentIds.Length; leftIndex++)
            {
                for (var rightIndex = leftIndex + 1;
                     rightIndex < componentIds.Length;
                     rightIndex++)
                {
                    var leftComponent = componentIds[leftIndex];
                    var rightComponent = componentIds[rightIndex];
                    if (!baselineBatchesByComponent.TryGetValue(
                            leftComponent,
                            out var baselineLeftBatches) ||
                        !baselineBatchesByComponent.TryGetValue(
                            rightComponent,
                            out var baselineRightBatches) ||
                        !baselineLeftBatches.Overlaps(baselineRightBatches))
                    {
                        continue;
                    }

                    if (!proposedBatchesByComponent.TryGetValue(
                            leftComponent,
                            out var proposedLeftBatches) ||
                        !proposedBatchesByComponent.TryGetValue(
                            rightComponent,
                            out var proposedRightBatches) ||
                        !proposedLeftBatches.Overlaps(proposedRightBatches))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static Dictionary<int, HashSet<int>> BuildAffinityComponentBatchSets(
            IReadOnlyList<MeshKey> meshes,
            IReadOnlyList<int> componentLabels,
            IReadOnlyDictionary<MeshKey, int> batchByMesh)
        {
            var result = new Dictionary<int, HashSet<int>>();
            for (var index = 0; index < meshes.Count; index++)
            {
                if (!batchByMesh.TryGetValue(meshes[index], out var batchIndex))
                    continue;

                if (!result.TryGetValue(componentLabels[index], out var batchIndexes))
                {
                    batchIndexes = [];
                    result[componentLabels[index]] = batchIndexes;
                }

                batchIndexes.Add(batchIndex);
            }

            return result;
        }

        private static void RegisterLodAtlasFamily(
            BatchState state,
            IEnumerable<MeshKey> memberKeys)
        {
            var family = memberKeys.ToHashSet();
            if (family.Count < 2)
                return;

            var overlappingFamilies = state.LodAwareAtlasFamilies
                .Where(existing => existing.Overlaps(family))
                .ToList();
            foreach (var overlappingFamily in overlappingFamilies)
            {
                family.UnionWith(overlappingFamily);
                state.LodAwareAtlasFamilies.Remove(overlappingFamily);
            }

            state.LodAwareAtlasFamilies.Add(family);
        }

        private static HashSet<int> GetBatchIdsForKeys(
            IReadOnlyList<List<AtlasCandidate>> batches,
            IEnumerable<MeshKey> keys)
        {
            var keySet = keys.ToHashSet();
            var result = new HashSet<int>();
            for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                if (batches[batchIndex].Any(candidate => keySet.Contains(candidate.Key)))
                    result.Add(batchIndex);
            }

            return result;
        }

        private static void RemoveAtlasFamilyMembers(
            BatchState state,
            IReadOnlyList<List<AtlasCandidate>> batches,
            IReadOnlyList<AtlasCandidate> members,
            string reason)
        {
            var memberKeys = members.Select(candidate => candidate.Key).ToHashSet();
            foreach (var batch in batches)
            {
                foreach (var candidate in batch
                             .Where(candidate => memberKeys.Contains(candidate.Key))
                             .ToList())
                {
                    batch.Remove(candidate);
                    RecordSkip(
                        state,
                        candidate.RootVmdPath,
                        candidate.Key,
                        candidate.Usages.FirstOrDefault()?.AssetPath ?? string.Empty,
                        reason);
                }
            }
        }

        private static bool CanRewriteStructuralComponentAsOneMaterial(
            BatchState state,
            IReadOnlyList<AtlasCandidate> members)
        {
            if (members.Count < 2)
                return false;

            var first = members[0];
            var rmvIdentity = GetRmvMergeIdentity(first.Model);
            var materialIdentity = BuildMergeAffinityMaterialIdentity(state, first);
            return members.Skip(1).All(member =>
                GetRmvMergeIdentity(member.Model).Equals(rmvIdentity, StringComparison.Ordinal) &&
                BuildMergeAffinityMaterialIdentity(state, member)
                    .Equals(materialIdentity, StringComparison.Ordinal));
        }

        private static List<AtlasStructuralMergeComponent> BuildAtlasStructuralMergeComponents(
            BatchState state,
            IEnumerable<AtlasCandidate> candidates)
        {
            var candidateKeys = candidates
                .Select(candidate => candidate.Key)
                .ToHashSet();
            var result = new List<AtlasStructuralMergeComponent>();
            var recordedComponents = state.PreAtlasStructuralMergeComponents.Count != 0
                ? state.PreAtlasStructuralMergeComponents
                : BuildFallbackAtlasStructuralMergeComponents(state);
            foreach (var component in recordedComponents)
            {
                var members = component
                    .OrderBy(member => member.GeometryPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(member => member.LodIndex)
                    .ThenBy(member => member.PartIndex)
                    .ToArray();
                if (members.Length >= 2 && members.Any(candidateKeys.Contains))
                {
                    result.Add(
                        new AtlasStructuralMergeComponent(
                            members[0].GeometryPath,
                            members[0].LodIndex,
                            members));
                }
            }

            return result;
        }

        private static List<HashSet<MeshKey>> BuildFallbackAtlasStructuralMergeComponents(
            BatchState state)
        {
            var result = new List<HashSet<MeshKey>>();
            foreach (var rigidLod in state.PreAtlasStructuralMergeMeshes
                         .GroupBy(mesh => (mesh.GeometryPath, mesh.LodIndex)))
            {
                var meshes = rigidLod
                    .OrderBy(mesh => mesh.PartIndex)
                    .ToArray();
                if (meshes.Length < 2)
                    continue;

                var labels = BuildStructuralMergeComponentLabels(
                    meshes,
                    state.PreAtlasStructuralMergePairs);
                foreach (var component in labels
                             .Distinct()
                             .Select(label => meshes
                                 .Where((_, index) => labels[index] == label)
                                 .ToHashSet())
                             .Where(component => component.Count >= 2))
                {
                    result.Add(component);
                }
            }

            return result;
        }

        private static HashSet<MeshKey> GetNonAtomicStructuralCandidateKeys(
            BatchState state,
            IReadOnlyList<List<AtlasCandidate>> batches)
        {
            var candidateByKey = batches
                .SelectMany(batch => batch)
                .GroupBy(candidate => candidate.Key)
                .ToDictionary(group => group.Key, group => group.First());
            var result = new HashSet<MeshKey>();
            foreach (var component in BuildAtlasStructuralMergeComponents(
                         state,
                         candidateByKey.Values))
            {
                var members = component.Members
                    .Where(candidateByKey.ContainsKey)
                    .Select(member => candidateByKey[member])
                    .ToList();
                var batchIds = GetBatchIdsForKeys(
                    batches,
                    members.Select(candidate => candidate.Key));
                if (members.Count != component.Members.Length ||
                    batchIds.Count != 1 ||
                    !CanRewriteStructuralComponentAsOneMaterial(state, members))
                {
                    foreach (var member in members)
                        result.Add(member.Key);
                }
            }

            return result;
        }

        private static List<List<AtlasCandidate>> OptimizeMaxSizeBatchesForPixelArea(
            BatchState state,
            IReadOnlyList<List<AtlasCandidate>> batches)
        {
            var optimized = new List<List<AtlasCandidate>>();
            for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                ReportProgress(
                    state.Progress,
                    "Optimizing atlas batches",
                    batchIndex + 1,
                    batches.Count,
                    $"Size optimization batch {batchIndex + 1}");
                var batch = batches[batchIndex];
                OptimizeMaxSizeBatchForPixelArea(state, batch, optimized);
            }

            return optimized;
        }

        private static void OptimizeMaxSizeBatchForPixelArea(
            BatchState state,
            List<AtlasCandidate> batch,
            List<List<AtlasCandidate>> output)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            if (batch.Count < 4 ||
                !TryGetGeneratedAtlasPixelCost(
                    state,
                    batch,
                    out var parentPixelCost,
                    out var parentTouchesMaxSize) ||
                !parentTouchesMaxSize)
            {
                output.Add(batch);
                return;
            }

            List<AtlasCandidate>? bestLeft = null;
            List<AtlasCandidate>? bestRight = null;
            var bestCombinedPixelCost = parentPixelCost;
            var bestSplitWasNonContiguous = false;
            var validSplitIndices = new List<int>();

            for (var splitIndex = 2; splitIndex <= batch.Count - 2; splitIndex++)
            {
                // Keep identical source/crop identities together. Splitting inside one of these
                // groups only duplicates an atlas placement and cannot improve packing.
                if (GetAtlasPlanningSourceIdentity(batch[splitIndex - 1]) !=
                    GetAtlasPlanningSourceIdentity(batch[splitIndex]))
                {
                    validSplitIndices.Add(splitIndex);
                }
            }

            const int maxOrderedSplitEvaluations = 32;
            IEnumerable<int> splitIndices = validSplitIndices;
            if (validSplitIndices.Count > maxOrderedSplitEvaluations)
            {
                // This is only an optional size optimization. Exhaustively rebuilding two atlas
                // plans for every possible boundary becomes quadratic on very large packs, so
                // sample the ordered boundary set evenly and keep conversion time bounded.
                splitIndices = Enumerable.Range(0, maxOrderedSplitEvaluations)
                    .Select(i => validSplitIndices[
                        i * (validSplitIndices.Count - 1) / (maxOrderedSplitEvaluations - 1)])
                    .Distinct();
            }

            foreach (var splitIndex in splitIndices)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                state.AtlasPixelAreaSplitEvaluations++;

                var left = batch.Take(splitIndex).ToList();
                var right = batch.Skip(splitIndex).ToList();

                if (!TryGetGeneratedAtlasPixelCost(state, left, out var leftCost, out _) ||
                    !TryGetGeneratedAtlasPixelCost(state, right, out var rightCost, out _))
                {
                    continue;
                }

                var combinedCost = checked(leftCost + rightCost);
                if (combinedCost >= bestCombinedPixelCost)
                    continue;

                bestCombinedPixelCost = combinedCost;
                bestLeft = left;
                bestRight = right;
                bestSplitWasNonContiguous = false;
            }

            foreach (var proposal in CreateNonContiguousSplitProposals(batch))
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                state.AtlasPixelAreaSplitEvaluations++;
                state.AtlasNonContiguousSplitEvaluations++;

                if (!TryGetGeneratedAtlasPixelCost(state, proposal.Left, out var leftCost, out _) ||
                    !TryGetGeneratedAtlasPixelCost(state, proposal.Right, out var rightCost, out _))
                {
                    continue;
                }

                var combinedCost = checked(leftCost + rightCost);
                if (combinedCost >= bestCombinedPixelCost)
                    continue;

                bestCombinedPixelCost = combinedCost;
                bestLeft = proposal.Left;
                bestRight = proposal.Right;
                bestSplitWasNonContiguous = true;
            }

            if (bestLeft == null || bestRight == null)
            {
                output.Add(batch);
                return;
            }

            state.AtlasPixelAreaOptimizedSplits++;
            var savedPixels = checked(parentPixelCost - bestCombinedPixelCost);
            state.AtlasPixelAreaSavedByOptimizedSplits = checked(
                state.AtlasPixelAreaSavedByOptimizedSplits + savedPixels);
            if (bestSplitWasNonContiguous)
            {
                state.AtlasNonContiguousOptimizedSplits++;
                state.AtlasPixelAreaSavedByNonContiguousSplits = checked(
                    state.AtlasPixelAreaSavedByNonContiguousSplits + savedPixels);
            }

            OptimizeMaxSizeBatchForPixelArea(state, bestLeft, output);
            OptimizeMaxSizeBatchForPixelArea(state, bestRight, output);
        }

        private static List<List<AtlasCandidate>> OptimizeBatchesForVmdLocality(
            BatchState state,
            IReadOnlyList<List<AtlasCandidate>> batches)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            var working = batches.Select(batch => batch.ToList()).ToList();
            if (!state.ShareAtlasesAcrossVmdsEnabled || working.Count == 0)
                return working;

            var allCandidates = working.SelectMany(batch => batch).ToList();
            var rootsByMesh = BuildCandidateRootVmdPaths(state, allCandidates);
            var affinityGroups = BuildMergeAffinityGroups(state, allCandidates);
            var optimized = new List<List<AtlasCandidate>>();

            for (var batchIndex = 0; batchIndex < working.Count; batchIndex++)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                ReportProgress(
                    state.Progress,
                    "Optimizing atlas batches",
                    batchIndex + 1,
                    working.Count,
                    $"VMD locality batch {batchIndex + 1}");
                OptimizeBatchForVmdLocality(
                    state,
                    working[batchIndex],
                    rootsByMesh,
                    affinityGroups,
                    optimized);
            }

            return optimized;
        }

        private static Dictionary<MeshKey, HashSet<string>> BuildCandidateRootVmdPaths(
            BatchState state,
            IEnumerable<AtlasCandidate> candidates)
        {
            if (state.AtlasValueGateRootsByWsModelCache == null ||
                state.AtlasValueGateRootsByWsModelCacheVersion !=
                state.ReachableWsModelsByRootVersion)
            {
                var rootsByWsModel = new Dictionary<string, HashSet<string>>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var (rootVmdPath, reachableWsModels) in state.ReachableWsModelsByRoot)
                {
                    var normalizedRoot = Normalize(rootVmdPath);
                    foreach (var reachableWsModel in reachableWsModels)
                    {
                        var wsModelPath = Normalize(reachableWsModel);
                        if (!rootsByWsModel.TryGetValue(wsModelPath, out var roots))
                        {
                            roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            rootsByWsModel[wsModelPath] = roots;
                        }

                        roots.Add(normalizedRoot);
                    }
                }

                state.AtlasValueGateRootsByWsModelCache = rootsByWsModel;
                state.AtlasValueGateRootsByWsModelCacheVersion =
                    state.ReachableWsModelsByRootVersion;
                state.AtlasValueGateRootsByMesh.Clear();
            }

            var cachedRootsByWsModel = state.AtlasValueGateRootsByWsModelCache
                ?? throw new InvalidOperationException(
                    "Atlas root cache was not initialized.");
            var result = new Dictionary<MeshKey, HashSet<string>>();
            foreach (var candidate in candidates)
            {
                if (!state.AtlasValueGateRootsByMesh.TryGetValue(
                        candidate.Key,
                        out var cachedRoots))
                {
                    cachedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var usage in candidate.Usages)
                    {
                        if (cachedRootsByWsModel.TryGetValue(
                                Normalize(usage.AssetPath),
                                out var usageRoots))
                        {
                            cachedRoots.UnionWith(usageRoots);
                        }
                    }

                    if (cachedRoots.Count == 0)
                        cachedRoots.Add(Normalize(candidate.RootVmdPath));

                    state.AtlasValueGateRootsByMesh[candidate.Key] = cachedRoots;
                }

                // Return a private set so callers cannot mutate the per-run cache.
                result[candidate.Key] = new HashSet<string>(
                    cachedRoots,
                    StringComparer.OrdinalIgnoreCase);
            }

            return result;
        }

        private static int GetBatchRootCount(
            IReadOnlyList<AtlasCandidate> candidates,
            IReadOnlyDictionary<MeshKey, HashSet<string>> rootsByMesh)
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                if (rootsByMesh.TryGetValue(candidate.Key, out var candidateRoots))
                    roots.UnionWith(candidateRoots);
                else
                    roots.Add(Normalize(candidate.RootVmdPath));
            }

            return Math.Max(1, roots.Count);
        }

        private static long GetAtlasResidencyProxy(
            long atlasPixels,
            int rootCount)
            => checked(atlasPixels * Math.Max(1, rootCount));

        private static void SetMaximumEntityCount(
            Dictionary<Wh3UnitVisualRole, int> countsByRole,
            Wh3UnitVisualRole role,
            int count)
        {
            if (count <= 0)
                return;

            countsByRole[role] = Math.Max(
                countsByRole.GetValueOrDefault(role),
                count);
        }

        private static bool UsageMatchesScenarioRosterScope(
            Wh3UnitCategoryUsage usage,
            Wh3ResolvedUnitVisual rosterUnit,
            Wh3ArmyVisualScenario scenario)
        {
            if (rosterUnit.HasUnknownFactionScope)
                return true;

            var scopeKey = scenario.RosterScopeKey?.Trim() ?? string.Empty;
            if (scopeKey.Length == 0)
                return true;

            if (scenario.RosterScope == Wh3RosterScope.SelectedFaction)
            {
                return usage.Provenance.Count == 0 ||
                       usage.Provenance.Any(provenance =>
                           string.IsNullOrWhiteSpace(provenance.FactionKey) ||
                           provenance.FactionKey.Equals(
                               scopeKey,
                               StringComparison.OrdinalIgnoreCase));
            }

            if (scenario.RosterScope == Wh3RosterScope.SelectedCulture)
            {
                return usage.Provenance.Count == 0 ||
                       usage.Provenance.Any(provenance =>
                       {
                           var hasCultureScope =
                               !string.IsNullOrWhiteSpace(provenance.CultureKey) ||
                               !string.IsNullOrWhiteSpace(provenance.SubcultureKey);
                           return !hasCultureScope ||
                                  provenance.CultureKey.Equals(
                                      scopeKey,
                                      StringComparison.OrdinalIgnoreCase) ||
                                  provenance.SubcultureKey.Equals(
                                      scopeKey,
                                      StringComparison.OrdinalIgnoreCase);
                       });
            }

            return true;
        }

        // The army-pool keys of a unit: its subcultures (culture-level when it has none) in
        // subculture mode, its cultures in culture mode.
        private static IEnumerable<string> GetArmyScopeKeys(
            Wh3ResolvedUnitVisual unit,
            PackOptimizerArmyScope scope)
            => scope == PackOptimizerArmyScope.Subculture && unit.SubcultureKeys.Count != 0
                ? unit.SubcultureKeys
                : unit.CultureKeys;

        private static ArmyResidencyModel? BuildArmyResidencyModel(
            BatchState state,
            Wh3UnitCategoryResolution? resolution,
            bool includeAllGameCultures)
        {
            if (resolution == null)
                return null;

            var rosterByUnitId = resolution.RosterUnits
                .Select(unit => (Identity: GetArmyUnitIdentity(unit), Unit: unit))
                .Where(entry => entry.Identity.Length != 0)
                .ToDictionary(
                    entry => entry.Identity,
                    entry => entry.Unit,
                    StringComparer.OrdinalIgnoreCase);
            if (rosterByUnitId.Count == 0)
                return null;

            var packAffectedUnitIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var usage in resolution.UsagesByVmd.Values.SelectMany(usages => usages))
            {
                var identity = GetArmyUnitIdentity(usage);
                if (identity.Length != 0)
                    packAffectedUnitIds.Add(identity);
            }

            foreach (var (assetPathValue, usages) in resolution.DirectAssetUsagesByPath)
            {
                var assetPath = Normalize(assetPathValue);
                if (!state.Source.ContainsFile(assetPath))
                    continue;

                foreach (var usage in usages)
                {
                    var identity = GetArmyUnitIdentity(usage);
                    if (identity.Length != 0)
                        packAffectedUnitIds.Add(identity);
                }
            }

            var armyScope = state.ArmyScope;
            var allKnownCultures = resolution.RosterUnits
                .SelectMany(unit => GetArmyScopeKeys(unit, armyScope))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var modelCultures = new HashSet<string>(
                allKnownCultures,
                StringComparer.OrdinalIgnoreCase);
            if (modelCultures.Count == 0)
                modelCultures.Add("__unknown__");

            // Player scoring stays pack-scoped by default, but every resolved culture is
            // retained in the model as a possible opponent/read-only consumer. This avoids a
            // full culture-pair matrix: unrelated cultures have zero presence for a culture-
            // local asset and therefore cancel naturally, while mirror battles and genuine
            // cross-culture asset sharing still contribute.
            var playerCultures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (includeAllGameCultures)
            {
                playerCultures.UnionWith(modelCultures);
            }
            else
            {
                foreach (var unitId in packAffectedUnitIds)
                {
                    if (!rosterByUnitId.TryGetValue(unitId, out var unit))
                        continue;

                    playerCultures.UnionWith(
                        GetArmyScopeKeys(unit, armyScope)
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Select(value => value.Trim().ToLowerInvariant())
                            .Where(modelCultures.Contains));
                }
            }

            // If the affected culture cannot be resolved, fall back to all modeled cultures
            // rather than silently assigning zero value to the pack.
            if (playerCultures.Count == 0)
                playerCultures.UnionWith(modelCultures);

            var unitsByCategory = resolution.Scenario.ArmySlotTemplate.Keys.ToDictionary(
                category => category,
                _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            var unitsByCultureAndCategory = modelCultures.ToDictionary(
                culture => culture,
                _ => resolution.Scenario.ArmySlotTemplate.Keys.ToDictionary(
                    category => category,
                    _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);
            var unitsByVmd = new Dictionary<
                string,
                Dictionary<Wh3ArmyUnitCategory, HashSet<string>>>(
                StringComparer.OrdinalIgnoreCase);
            var culturesByUnit = new Dictionary<string, IReadOnlySet<string>>(
                StringComparer.OrdinalIgnoreCase);
            var entityCountByUnit = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var entityCountByUnitAndRole = new Dictionary<
                string,
                Dictionary<Wh3UnitVisualRole, int>>(StringComparer.OrdinalIgnoreCase);
            var categoryByUnit = new Dictionary<string, Wh3ArmyUnitCategory>(
                StringComparer.OrdinalIgnoreCase);
            var directVmdsByUnitAndRole = new Dictionary<
                string,
                Dictionary<Wh3UnitVisualRole, HashSet<string>>>(
                StringComparer.OrdinalIgnoreCase);
            var directAssetsByUnitAndRole = new Dictionary<
                string,
                Dictionary<
                    Wh3UnitVisualRole,
                    Dictionary<string, double>>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var unit in resolution.RosterUnits)
            {
                if (!resolution.Scenario.ArmySlotTemplate.ContainsKey(unit.Category))
                    continue;

                var identity = GetArmyUnitIdentity(unit);
                if (identity.Length == 0)
                    continue;

                var unitCultures = GetArmyScopeKeys(unit, armyScope)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim().ToLowerInvariant())
                    .Where(modelCultures.Contains)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Unknown culture/faction scope is a wildcard, not "matches nothing". This
                // keeps unresolved generic units available in each culture without ever mixing
                // known units from unrelated cultures into the same army population.
                if (unitCultures.Count == 0 &&
                    (unit.HasUnknownFactionScope || !GetArmyScopeKeys(unit, armyScope).Any()))
                {
                    unitCultures.UnionWith(modelCultures);
                }

                if (unitCultures.Count == 0)
                    continue;

                culturesByUnit[identity] = unitCultures;
                unitsByCategory[unit.Category].Add(identity);
                foreach (var culture in unitCultures)
                    unitsByCultureAndCategory[culture][unit.Category].Add(identity);

                categoryByUnit[identity] = unit.Category;
                var countsByRole = new Dictionary<Wh3UnitVisualRole, int>();
                SetMaximumEntityCount(countsByRole, Wh3UnitVisualRole.Men, unit.VisualCounts.Riders);
                SetMaximumEntityCount(countsByRole, Wh3UnitVisualRole.Mount, unit.VisualCounts.Mounts);
                SetMaximumEntityCount(countsByRole, Wh3UnitVisualRole.Engine, unit.VisualCounts.Engines);
                SetMaximumEntityCount(countsByRole, Wh3UnitVisualRole.Crew, unit.VisualCounts.Crew);
                entityCountByUnitAndRole[identity] = countsByRole;
                entityCountByUnit[identity] = Math.Max(
                    1,
                    countsByRole.Values.DefaultIfEmpty(1).Max());

                foreach (var component in unit.Components)
                {
                    if (component.ScenarioPresenceProbability <= 0)
                        continue;

                    var assetPath = Normalize(component.AssetPath);
                    if (assetPath.Length == 0)
                        continue;

                    if (!unitsByVmd.TryGetValue(assetPath, out var byCategory))
                    {
                        byCategory = new Dictionary<Wh3ArmyUnitCategory, HashSet<string>>();
                        unitsByVmd[assetPath] = byCategory;
                    }

                    if (!byCategory.TryGetValue(unit.Category, out var unitIds))
                    {
                        unitIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        byCategory[unit.Category] = unitIds;
                    }

                    unitIds.Add(identity);

                    if (component.IsVariantMeshDefinition)
                    {
                        if (!directVmdsByUnitAndRole.TryGetValue(identity, out var byRole))
                        {
                            byRole = new Dictionary<Wh3UnitVisualRole, HashSet<string>>();
                            directVmdsByUnitAndRole[identity] = byRole;
                        }

                        if (!byRole.TryGetValue(component.Role, out var vmds))
                        {
                            vmds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            byRole[component.Role] = vmds;
                        }

                        vmds.Add(assetPath);
                        continue;
                    }

                    if (!directAssetsByUnitAndRole.TryGetValue(identity, out var directAssetsByRole))
                    {
                        directAssetsByRole = new Dictionary<
                            Wh3UnitVisualRole,
                            Dictionary<string, double>>();
                        directAssetsByUnitAndRole[identity] = directAssetsByRole;
                    }

                    if (!directAssetsByRole.TryGetValue(component.Role, out var directAssets))
                    {
                        directAssets = new Dictionary<string, double>(
                            StringComparer.OrdinalIgnoreCase);
                        directAssetsByRole[component.Role] = directAssets;
                    }

                    directAssets[assetPath] = Math.Clamp(
                        directAssets.GetValueOrDefault(assetPath) +
                        component.ScenarioPresenceProbability,
                        0.0,
                        1.0);
                }
            }

            var populatedCultures = unitsByCultureAndCategory
                .Where(entry => entry.Value.Values.Any(units => units.Count != 0))
                .Select(entry => entry.Key)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (populatedCultures.Length == 0)
                return null;

            var scoredPlayerCultures = populatedCultures
                .Where(playerCultures.Contains)
                .ToArray();
            if (scoredPlayerCultures.Length == 0)
                scoredPlayerCultures = populatedCultures;

            // Culture mode (and the all-game-units mode) weights the player scopes equally. In
            // subculture mode a player scope weighs by how many pack-affected units it can field
            // (a unit available to several subcultures counts for each), so subcultures that
            // merely share a culture with the pack, or custom-battle copies of a roster, do not
            // dilute the pack's presence. Nothing resolving falls back to equal weights.
            var playerScopeUnitCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var unitId in packAffectedUnitIds)
            {
                if (!rosterByUnitId.TryGetValue(unitId, out var packUnit))
                    continue;

                foreach (var scopeKey in GetArmyScopeKeys(packUnit, armyScope)
                             .Where(value => !string.IsNullOrWhiteSpace(value))
                             .Select(value => value.Trim().ToLowerInvariant())
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (playerCultures.Contains(scopeKey) &&
                        scoredPlayerCultures.Contains(scopeKey, StringComparer.OrdinalIgnoreCase))
                    {
                        playerScopeUnitCounts[scopeKey] =
                            playerScopeUnitCounts.GetValueOrDefault(scopeKey) + 1;
                    }
                }
            }

            var cultureWeights = armyScope == PackOptimizerArmyScope.Subculture &&
                                 !includeAllGameCultures &&
                                 playerScopeUnitCounts.Count != 0
                ? CreateProportionalPlayerScopeWeights(playerScopeUnitCounts)
                : scoredPlayerCultures.ToDictionary(
                    culture => culture,
                    _ => 1.0 / scoredPlayerCultures.Length,
                    StringComparer.OrdinalIgnoreCase);

            // Opponent cultures are uniformly distributed across the complete resolved game
            // roster. For culture-local assets this only adds the same-culture mirror chance.
            // If the dependency graph proves an asset is shared across cultures, those cultures
            // automatically contribute without needing an explicit culture-pair matrix.
            // In subculture mode the culture still receives the uniform weight and splits it
            // equally across its populated subcultures.
            var parentCultureByScope = BuildParentCultureByScope(
                resolution,
                populatedCultures,
                armyScope);
            var populatedScopesPerCulture = populatedCultures
                .GroupBy(scope => parentCultureByScope[scope], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            var opponentCultureWeights = populatedCultures.ToDictionary(
                scope => scope,
                scope => 1.0 / populatedScopesPerCulture.Count /
                         populatedScopesPerCulture[parentCultureByScope[scope]],
                StringComparer.OrdinalIgnoreCase);

            var occurrenceCache = new Dictionary<string, IReadOnlyDictionary<string, double>>(
                StringComparer.OrdinalIgnoreCase);
            var configurationCache = new Dictionary<string, IReadOnlyList<UnitVisualConfiguration>>(
                StringComparer.OrdinalIgnoreCase);
            var expectedWsModelOccurrencesByUnit =
                new Dictionary<string, ExpectedWsModelOccurrences>(
                    StringComparer.OrdinalIgnoreCase);
            var visualConfigurationsByUnitAndRole = new Dictionary<
                string,
                Dictionary<Wh3UnitVisualRole, IReadOnlyList<UnitVisualConfiguration>>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var (unitId, directVmdsByRole) in directVmdsByUnitAndRole)
            {
                if (directVmdsByRole.Count == 0)
                    continue;

                var accumulatedByWsModel = new Dictionary<
                    string,
                    Dictionary<Wh3UnitVisualRole, double>>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var (role, directVmds) in directVmdsByRole)
                {
                    if (directVmds.Count == 0)
                        continue;

                    var accumulated = new Dictionary<string, double>(
                        StringComparer.OrdinalIgnoreCase);
                    var configurations = new List<UnitVisualConfiguration>();
                    foreach (var vmdPath in directVmds)
                    {
                        var occurrences = GetExpectedWsModelOccurrencesForVmd(
                            state,
                            vmdPath,
                            occurrenceCache,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        foreach (var (wsModelPath, expectedOccurrences) in occurrences)
                        {
                            accumulated[wsModelPath] =
                                accumulated.GetValueOrDefault(wsModelPath) + expectedOccurrences;
                        }

                        configurations.AddRange(GetWsModelConfigurationsForVmd(
                            state,
                            vmdPath,
                            configurationCache,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
                    }

                    if (accumulated.Count == 0)
                        continue;

                    // A unit can have faction-specific direct VMDs for one component. Without
                    // a faction context, treat those visual definitions as equally likely
                    // alternatives, independently for each visual role.
                    var directVariantCount = directVmds.Count;
                    if (configurations.Count != 0)
                    {
                        if (!visualConfigurationsByUnitAndRole.TryGetValue(unitId, out var byRole))
                        {
                            byRole = new Dictionary<Wh3UnitVisualRole, IReadOnlyList<UnitVisualConfiguration>>();
                            visualConfigurationsByUnitAndRole[unitId] = byRole;
                        }

                        byRole[role] = configurations
                            .Select(configuration => configuration with
                            {
                                Probability = configuration.Probability / directVariantCount,
                            })
                            .Where(configuration => configuration.Probability > 0)
                            .ToArray();
                    }
                    foreach (var (wsModelPath, expectedOccurrences) in accumulated)
                    {
                        if (!accumulatedByWsModel.TryGetValue(wsModelPath, out var byRole))
                        {
                            byRole = new Dictionary<Wh3UnitVisualRole, double>();
                            accumulatedByWsModel[wsModelPath] = byRole;
                        }

                        byRole[role] = expectedOccurrences / directVariantCount;
                    }
                }

                if (accumulatedByWsModel.Count != 0)
                {
                    expectedWsModelOccurrencesByUnit[unitId] = new ExpectedWsModelOccurrences(
                        accumulatedByWsModel.ToDictionary(
                            entry => entry.Key,
                            entry => (IReadOnlyDictionary<Wh3UnitVisualRole, double>)entry.Value,
                            StringComparer.OrdinalIgnoreCase));
                }
            }

            foreach (var (unitId, directAssetsByRole) in directAssetsByUnitAndRole)
            {
                if (!expectedWsModelOccurrencesByUnit.TryGetValue(
                        unitId,
                        out var existingOccurrences))
                {
                    existingOccurrences = new ExpectedWsModelOccurrences(
                        new Dictionary<string, IReadOnlyDictionary<Wh3UnitVisualRole, double>>(
                            StringComparer.OrdinalIgnoreCase));
                    expectedWsModelOccurrencesByUnit[unitId] = existingOccurrences;
                }

                var mergedOccurrences = existingOccurrences.ByWsModel.ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value.ToDictionary(
                        roleEntry => roleEntry.Key,
                        roleEntry => roleEntry.Value),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var (role, directAssets) in directAssetsByRole)
                {
                    if (directAssets.Count == 0)
                        continue;

                    // Preserve scenario state/LOD probabilities exactly. Do not
                    // renormalize them: doing so would turn e.g. a 25% destroyed state back
                    // into 100% presence and defeat scenario-driven lifecycle modelling.
                    foreach (var (assetPath, scenarioPresence) in directAssets)
                    {
                        if (scenarioPresence <= 0)
                            continue;

                        if (!mergedOccurrences.TryGetValue(assetPath, out var byRole))
                        {
                            byRole = new Dictionary<Wh3UnitVisualRole, double>();
                            mergedOccurrences[assetPath] = byRole;
                        }

                        byRole[role] = Math.Clamp(
                            byRole.GetValueOrDefault(role) + scenarioPresence,
                            0.0,
                            1.0);
                    }
                }

                expectedWsModelOccurrencesByUnit[unitId] = new ExpectedWsModelOccurrences(
                    mergedOccurrences.ToDictionary(
                        entry => entry.Key,
                        entry => (IReadOnlyDictionary<Wh3UnitVisualRole, double>)entry.Value,
                        StringComparer.OrdinalIgnoreCase));
            }

            // Index the resolved asset closure in the direction used by the planner. The
            // residency and draw-value calculations are called once per proposed merge, so
            // walking every unit in every culture for every proposal is prohibitively costly.
            // Keep both directions: asset -> units lets a proposal identify its consumers,
            // while asset -> cultures lets it skip cultures that cannot reference it at all.
            var unitIdsByAssetPath = new Dictionary<string, HashSet<string>>(
                StringComparer.OrdinalIgnoreCase);

            void AddUnitAssetReference(string assetPathValue, string unitId)
            {
                var assetPath = Normalize(assetPathValue);
                if (assetPath.Length == 0)
                    return;

                if (!unitIdsByAssetPath.TryGetValue(assetPath, out var unitIds))
                {
                    unitIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    unitIdsByAssetPath[assetPath] = unitIds;
                }

                unitIds.Add(unitId);
            }

            foreach (var (assetPath, unitsForVmd) in unitsByVmd)
            {
                foreach (var unitId in unitsForVmd.Values.SelectMany(unitIds => unitIds))
                    AddUnitAssetReference(assetPath, unitId);
            }

            foreach (var (unitId, expectedOccurrences) in expectedWsModelOccurrencesByUnit)
            {
                foreach (var assetPath in expectedOccurrences.ByWsModel.Keys)
                    AddUnitAssetReference(assetPath, unitId);
            }

            var culturesByAssetPath = new Dictionary<
                string,
                IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (assetPath, unitIds) in unitIdsByAssetPath)
            {
                var cultures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var unitId in unitIds)
                {
                    if (culturesByUnit.TryGetValue(unitId, out var unitCultures))
                        cultures.UnionWith(unitCultures);
                }

                if (cultures.Count != 0)
                    culturesByAssetPath[assetPath] = cultures;
            }

            var readOnlyUnitIdsByAssetPath = unitIdsByAssetPath.ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlySet<string>)entry.Value,
                StringComparer.OrdinalIgnoreCase);

            return new ArmyResidencyModel(
                unitsByCategory,
                unitsByCultureAndCategory,
                unitsByVmd,
                culturesByUnit,
                readOnlyUnitIdsByAssetPath,
                culturesByAssetPath,
                cultureWeights,
                opponentCultureWeights,
                entityCountByUnit,
                entityCountByUnitAndRole,
                categoryByUnit,
                directVmdsByUnitAndRole,
                expectedWsModelOccurrencesByUnit,
                visualConfigurationsByUnitAndRole,
                resolution.Scenario,
                includeAllGameCultures)
            {
                Scope = armyScope,
                PlayerScopeUnitCounts = playerScopeUnitCounts,
                ParentCultureByScope = parentCultureByScope,
            };
        }

        private static Dictionary<string, double> CreateProportionalPlayerScopeWeights(
            IReadOnlyDictionary<string, int> unitCountsByScope)
        {
            var total = unitCountsByScope.Values.Sum(count => (double)count);
            return unitCountsByScope.ToDictionary(
                entry => entry.Key,
                entry => entry.Value / total,
                StringComparer.OrdinalIgnoreCase);
        }

        // Maps every populated army scope to its culture: the DB's subculture -> culture table,
        // then the culture the scope's own units agree on, then the scope itself. In culture mode
        // a scope is its own culture.
        private static Dictionary<string, string> BuildParentCultureByScope(
            Wh3UnitCategoryResolution resolution,
            IEnumerable<string> scopes,
            PackOptimizerArmyScope armyScope)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (armyScope == PackOptimizerArmyScope.Culture)
            {
                foreach (var scope in scopes)
                    result[scope] = scope;
                return result;
            }

            var unitCulturesBySubculture = new Dictionary<string, HashSet<string>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var unit in resolution.RosterUnits)
            {
                foreach (var subculture in unit.SubcultureKeys)
                {
                    var key = subculture.Trim().ToLowerInvariant();
                    if (!unitCulturesBySubculture.TryGetValue(key, out var cultures))
                    {
                        cultures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        unitCulturesBySubculture[key] = cultures;
                    }

                    cultures.UnionWith(unit.CultureKeys.Select(value => value.Trim().ToLowerInvariant()));
                }
            }

            foreach (var scope in scopes)
            {
                if (resolution.CultureBySubculture.TryGetValue(scope, out var culture) &&
                    !string.IsNullOrWhiteSpace(culture))
                {
                    result[scope] = culture.Trim().ToLowerInvariant();
                }
                else if (unitCulturesBySubculture.TryGetValue(scope, out var unitCultures) &&
                         unitCultures.Count == 1)
                {
                    result[scope] = unitCultures.First();
                }
                else
                {
                    result[scope] = scope;
                }
            }

            return result;
        }

        private static IReadOnlyDictionary<string, double> GetExpectedWsModelOccurrencesForVmd(
            BatchState state,
            string vmdPathValue,
            Dictionary<string, IReadOnlyDictionary<string, double>> cache,
            HashSet<string> visiting)
        {
            var vmdPath = Normalize(vmdPathValue);
            if (cache.TryGetValue(vmdPath, out var cached))
                return cached;
            if (!visiting.Add(vmdPath))
                return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var container = FindGameplayTraversalContainer(
                    state,
                    state.Source,
                    GetGameplayTraversalContainers(state),
                    vmdPath);
                var file = container?.FindFile(vmdPath);
                if (container == null || file == null)
                {
                    var empty = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    cache[vmdPath] = empty;
                    return empty;
                }

                if (!TryGetVmdForTraversal(
                        state,
                        container,
                        vmdPath,
                        file,
                        out var vmd))
                {
                    var empty = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    cache[vmdPath] = empty;
                    return empty;
                }

                var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                AccumulateExpectedWsModelOccurrences(
                    state,
                    vmd,
                    1.0,
                    result,
                    cache,
                    visiting);
                cache[vmdPath] = result;
                return result;
            }
            finally
            {
                visiting.Remove(vmdPath);
            }
        }

        private const int MaxExactVisualConfigurations = 16384;

        private static IReadOnlyList<UnitVisualConfiguration> GetWsModelConfigurationsForVmd(
            BatchState state,
            string vmdPathValue,
            Dictionary<string, IReadOnlyList<UnitVisualConfiguration>> cache,
            HashSet<string> visiting)
        {
            var vmdPath = Normalize(vmdPathValue);
            if (cache.TryGetValue(vmdPath, out var cached))
                return cached;
            if (!visiting.Add(vmdPath))
                return [];

            try
            {
                var container = FindGameplayTraversalContainer(
                    state,
                    state.Source,
                    GetGameplayTraversalContainers(state),
                    vmdPath);
                var file = container?.FindFile(vmdPath);
                if (container == null ||
                    file == null ||
                    !TryGetVmdForTraversal(state, container, vmdPath, file, out var vmd))
                {
                    cache[vmdPath] = [];
                    return [];
                }

                var configurations = ExpandVisualConfigurations(
                    state,
                    vmd,
                    cache,
                    visiting);
                cache[vmdPath] = configurations;
                return configurations;
            }
            finally
            {
                visiting.Remove(vmdPath);
            }
        }

        private static IReadOnlyList<UnitVisualConfiguration> ExpandVisualConfigurations(
            BatchState state,
            VariantMesh mesh,
            Dictionary<string, IReadOnlyList<UnitVisualConfiguration>> cache,
            HashSet<string> visiting)
        {
            var baseModels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(mesh.ModelReference))
            {
                var modelPath = Normalize(mesh.ModelReference);
                if (Path.GetExtension(modelPath)
                        .Equals(".wsmodel", StringComparison.OrdinalIgnoreCase) &&
                    FindGameplayTraversalContainer(
                        state,
                        state.Source,
                        GetGameplayTraversalContainers(state),
                        modelPath) != null)
                {
                    baseModels[modelPath] = 1;
                }
            }

            var current = new List<UnitVisualConfiguration>
            {
                new(1.0, baseModels),
            };

            foreach (var slot in mesh.ChildSlots ?? [])
            {
                var slotProbability = ParseVmdSlotProbability(slot.Probability);
                var childMeshCount = slot.ChildMeshes?.Count ?? 0;
                var childReferenceCount = slot.ChildReferences?.Count ?? 0;
                var alternativeCount = childMeshCount + childReferenceCount;
                if (alternativeCount == 0 || slotProbability <= 0)
                    continue;

                var alternatives = new List<UnitVisualConfiguration>();
                if (slotProbability < 1.0)
                {
                    alternatives.Add(new UnitVisualConfiguration(
                        1.0 - slotProbability,
                        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)));
                }

                var alternativeWeight = slotProbability / alternativeCount;
                foreach (var child in slot.ChildMeshes ?? [])
                {
                    var childConfigurations = ExpandVisualConfigurations(
                        state,
                        child,
                        cache,
                        visiting);
                    if (childConfigurations.Count == 0)
                        return [];

                    alternatives.AddRange(childConfigurations.Select(configuration => configuration with
                    {
                        Probability = configuration.Probability * alternativeWeight,
                    }));
                }

                foreach (var reference in slot.ChildReferences ?? [])
                {
                    if (string.IsNullOrWhiteSpace(reference.Reference))
                        return [];

                    var childConfigurations = GetWsModelConfigurationsForVmd(
                        state,
                        reference.Reference,
                        cache,
                        visiting);
                    if (childConfigurations.Count == 0)
                        return [];

                    alternatives.AddRange(childConfigurations.Select(configuration => configuration with
                    {
                        Probability = configuration.Probability * alternativeWeight,
                    }));
                }

                if (alternatives.Count == 0)
                    return [];

                var combined = new List<UnitVisualConfiguration>();
                foreach (var left in current)
                {
                    foreach (var right in alternatives)
                    {
                        var probability = left.Probability * right.Probability;
                        if (probability <= 0)
                            continue;

                        var models = left.WsModelOccurrences.ToDictionary(
                            entry => entry.Key,
                            entry => entry.Value,
                            StringComparer.OrdinalIgnoreCase);
                        foreach (var (path, count) in right.WsModelOccurrences)
                            models[path] = models.GetValueOrDefault(path) + count;

                        combined.Add(new UnitVisualConfiguration(probability, models));
                        if (combined.Count > MaxExactVisualConfigurations)
                            return [];
                    }
                }

                current = MergeEquivalentVisualConfigurations(combined);
                if (current.Count > MaxExactVisualConfigurations)
                    return [];
            }

            return current;
        }

        private static List<UnitVisualConfiguration> MergeEquivalentVisualConfigurations(
            IEnumerable<UnitVisualConfiguration> configurations)
        {
            var merged = new Dictionary<string, UnitVisualConfiguration>(StringComparer.Ordinal);
            foreach (var configuration in configurations)
            {
                var key = string.Join(
                    "\u001f",
                    configuration.WsModelOccurrences
                        .Where(entry => entry.Value > 0)
                        .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(entry => $"{entry.Key}\u001e{entry.Value}"));
                if (merged.TryGetValue(key, out var existing))
                {
                    merged[key] = existing with
                    {
                        Probability = existing.Probability + configuration.Probability,
                    };
                }
                else
                {
                    merged[key] = configuration;
                }
            }

            return merged.Values
                .Where(configuration => configuration.Probability > 0)
                .ToList();
        }

        private static void AccumulateExpectedWsModelOccurrences(
            BatchState state,
            VariantMesh mesh,
            double parentProbability,
            Dictionary<string, double> result,
            Dictionary<string, IReadOnlyDictionary<string, double>> cache,
            HashSet<string> visiting)
        {
            if (parentProbability <= 0)
                return;

            if (!string.IsNullOrWhiteSpace(mesh.ModelReference))
            {
                var modelPath = Normalize(mesh.ModelReference);
                if (Path.GetExtension(modelPath)
                        .Equals(".wsmodel", StringComparison.OrdinalIgnoreCase) &&
                    FindGameplayTraversalContainer(
                        state,
                        state.Source,
                        GetGameplayTraversalContainers(state),
                        modelPath) != null)
                {
                    result[modelPath] =
                        result.GetValueOrDefault(modelPath) + parentProbability;
                }
            }

            foreach (var slot in mesh.ChildSlots ?? [])
            {
                var slotProbability = ParseVmdSlotProbability(slot.Probability);
                if (slotProbability <= 0)
                    continue;

                var childMeshCount = slot.ChildMeshes?.Count ?? 0;
                var childReferenceCount = slot.ChildReferences?.Count ?? 0;
                var alternativeCount = childMeshCount + childReferenceCount;
                if (alternativeCount == 0)
                    continue;

                // VMD does not expose a per-child weight. Treat the entries in an active slot as
                // equally likely alternatives, while the slot probability controls whether the
                // slot is populated at all.
                var alternativeProbability =
                    parentProbability * slotProbability / alternativeCount;

                foreach (var child in slot.ChildMeshes ?? [])
                {
                    AccumulateExpectedWsModelOccurrences(
                        state,
                        child,
                        alternativeProbability,
                        result,
                        cache,
                        visiting);
                }

                foreach (var reference in slot.ChildReferences ?? [])
                {
                    if (string.IsNullOrWhiteSpace(reference.Reference))
                        continue;

                    var childOccurrences = GetExpectedWsModelOccurrencesForVmd(
                        state,
                        reference.Reference,
                        cache,
                        visiting);
                    foreach (var (wsModelPath, expectedOccurrences) in childOccurrences)
                    {
                        result[wsModelPath] =
                            result.GetValueOrDefault(wsModelPath) +
                            alternativeProbability * expectedOccurrences;
                    }
                }
            }
        }

        private static double ParseVmdSlotProbability(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 1.0;

            if (!double.TryParse(
                    value.Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var probability))
            {
                return 1.0;
            }

            // Most VMDs use 0..1. Be tolerant of hand-authored percentage-style values.
            if (probability > 1.0 && probability <= 100.0)
                probability /= 100.0;

            return Math.Clamp(probability, 0.0, 1.0);
        }

        private static string GetArmyUnitIdentity(Wh3ResolvedUnitVisual unit)
            => unit.Identity.Trim().ToLowerInvariant();

        private static string GetArmyUnitIdentity(Wh3UnitCategoryUsage usage)
        {
            if (!string.IsNullOrWhiteSpace(usage.MainUnitKey))
                return $"main:{usage.MainUnitKey.Trim().ToLowerInvariant()}";
            if (!string.IsNullOrWhiteSpace(usage.LandUnitKey))
                return $"land:{usage.LandUnitKey.Trim().ToLowerInvariant()}";
            return string.Empty;
        }

        private static string GetArmyUnitIdentity(Wh3UnitDirectAssetUsage usage)
        {
            if (!string.IsNullOrWhiteSpace(usage.MainUnitKey))
                return $"main:{usage.MainUnitKey.Trim().ToLowerInvariant()}";
            if (!string.IsNullOrWhiteSpace(usage.LandUnitKey))
                return $"land:{usage.LandUnitKey.Trim().ToLowerInvariant()}";
            return string.Empty;
        }

        private static HashSet<string> GetBatchRoots(
            IReadOnlyList<AtlasCandidate> candidates,
            IReadOnlyDictionary<MeshKey, HashSet<string>> rootsByMesh)
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                if (rootsByMesh.TryGetValue(candidate.Key, out var candidateRoots))
                    roots.UnionWith(candidateRoots);
                else
                    roots.Add(Normalize(candidate.RootVmdPath));
            }

            return roots;
        }

        private static double GetExpectedArmyResidentPixels(
            BatchState state,
            long atlasPixels,
            IReadOnlyList<AtlasCandidate> candidates,
            IReadOnlyDictionary<MeshKey, HashSet<string>> rootsByMesh)
        {
            if (atlasPixels <= 0)
                return 0;

            var targetWsModels = candidates
                .SelectMany(candidate => candidate.Usages)
                .Select(usage => Normalize(usage.AssetPath))
                .Where(path => path.Length != 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var roots = GetBatchRoots(candidates, rootsByMesh);

            return atlasPixels * GetExpectedArmyResidentProbability(
                state,
                targetWsModels,
                roots);
        }

        private static double GetExpectedArmyResidentProbability(
            BatchState state,
            IReadOnlyCollection<string> targetAssetPaths,
            IEnumerable<string> fallbackRoots)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            var cacheKey = new ArmyResidencyProbabilityCacheKey(
                BuildCanonicalPathSetKey(targetAssetPaths),
                BuildCanonicalPathSetKey(fallbackRoots));
            if (state.AtlasValueGateArmyResidentProbabilityCache.TryGetValue(
                    cacheKey,
                    out var cachedProbability))
            {
                state.AtlasValueGateArmyResidentProbabilityCacheHits++;
                return cachedProbability;
            }

            state.AtlasValueGateArmyResidentProbabilityCacheMisses++;
            var probability = GetExpectedArmyResidentProbability(
                state.ArmyResidencyModel,
                targetAssetPaths,
                fallbackRoots,
                state.CancellationToken);
            state.AtlasValueGateArmyResidentProbabilityCache[cacheKey] = probability;
            return probability;
        }

        private static string BuildCanonicalPathSetKey(IEnumerable<string> paths)
            => string.Join(
                '\u001f',
                paths
                    .Select(Normalize)
                    .Where(path => path.Length != 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));

        private static double GetExpectedArmyResidentProbability(
            ArmyResidencyModel? model,
            IReadOnlyCollection<string> targetAssetPaths,
            IEnumerable<string> fallbackRoots,
            CancellationToken cancellationToken = default)
        {
            if (model == null || model.PlayerCultureWeights.Count == 0)
                return 0;

            var normalizedTargets = targetAssetPaths
                .Select(Normalize)
                .Where(path => path.Length != 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var normalizedFallbackRoots = fallbackRoots
                .Select(Normalize)
                .Where(path => path.Length != 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var relevantUnitIds = GetRelevantArmyUnitIds(
                model,
                normalizedTargets,
                normalizedFallbackRoots);
            cancellationToken.ThrowIfCancellationRequested();
            var relevantCultures = GetRelevantArmyCultures(
                model,
                normalizedTargets,
                normalizedFallbackRoots,
                relevantUnitIds);
            if (relevantUnitIds.Count == 0 || relevantCultures.Count == 0)
                return 0;

            var fallbackCoveredByCategory = model.Scenario.ArmySlotTemplate.Keys.ToDictionary(
                category => category,
                _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            foreach (var root in normalizedFallbackRoots)
            {
                if (!model.UnitsByVmd.TryGetValue(root, out var unitsForVmd))
                    continue;

                foreach (var (category, unitIds) in unitsForVmd)
                {
                    if (fallbackCoveredByCategory.TryGetValue(category, out var covered))
                        covered.UnionWith(unitIds);
                }
            }

            var probabilitiesByCulture = new Dictionary<string, double>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var culture in relevantCultures)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!model.UnitsByCultureAndCategory.TryGetValue(
                        culture,
                        out var unitsByCategory))
                {
                    continue;
                }

                var notResidentProbability = 1.0;
                foreach (var (category, slotCount) in model.Scenario.ArmySlotTemplate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!unitsByCategory.TryGetValue(category, out var cultureUnits))
                        continue;

                    var population = cultureUnits.Count;
                    if (population == 0)
                        continue;

                    double perSlotPresenceProbability = 0;
                    foreach (var unitId in EnumerateRelevantArmyUnitIds(
                                 cultureUnits,
                                 relevantUnitIds))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        double unitCardPresenceProbability = 0;
                        var notPresentAcrossRoles = 1.0;
                        var exactRoles = new HashSet<Wh3UnitVisualRole>();

                        if (model.VisualConfigurationsByUnitAndRole.TryGetValue(
                                unitId,
                                out var configurationsByRole))
                        {
                            foreach (var (role, configurations) in configurationsByRole)
                            {
                                if (configurations.Count == 0)
                                    continue;

                                exactRoles.Add(role);
                                var perEntityPresenceProbability = configurations
                                    .Where(configuration =>
                                        configuration.WsModelOccurrences.Any(entry =>
                                            entry.Value > 0 &&
                                            normalizedTargets.Contains(entry.Key)))
                                    .Sum(configuration => configuration.Probability);
                                perEntityPresenceProbability = Math.Clamp(
                                    perEntityPresenceProbability,
                                    0.0,
                                    1.0);
                                if (perEntityPresenceProbability <= 0)
                                    continue;

                                var entityCount = Math.Max(
                                    1,
                                    model.EntityCountByUnitAndRole
                                        .GetValueOrDefault(unitId)?
                                        .GetValueOrDefault(role, 1) ?? 1);
                                notPresentAcrossRoles *= Math.Pow(
                                    1.0 - perEntityPresenceProbability,
                                    entityCount);
                            }
                        }

                        if (model.ExpectedWsModelOccurrencesByUnit.TryGetValue(
                                unitId,
                                out var expectedOccurrences))
                        {
                            var expectedPresenceByRole =
                                new Dictionary<Wh3UnitVisualRole, double>();
                            foreach (var wsModelPath in normalizedTargets)
                            {
                                if (!expectedOccurrences.ByWsModel.TryGetValue(
                                        wsModelPath,
                                        out var occurrencesByRole))
                                {
                                    continue;
                                }

                                foreach (var (role, expectedOccurrencesPerEntity) in occurrencesByRole)
                                {
                                    if (exactRoles.Contains(role))
                                        continue;

                                    expectedPresenceByRole[role] =
                                        expectedPresenceByRole.GetValueOrDefault(role) +
                                        expectedOccurrencesPerEntity;
                                }
                            }

                            foreach (var (role, expectedOccurrencesPerEntity) in expectedPresenceByRole)
                            {
                                if (expectedOccurrencesPerEntity <= 0)
                                    continue;

                                var entityCount = Math.Max(
                                    1,
                                    model.EntityCountByUnitAndRole
                                        .GetValueOrDefault(unitId)?
                                        .GetValueOrDefault(role, 1) ?? 1);
                                notPresentAcrossRoles *= Math.Pow(
                                    1.0 - Math.Clamp(expectedOccurrencesPerEntity, 0.0, 1.0),
                                    entityCount);
                            }

                            unitCardPresenceProbability = 1.0 - notPresentAcrossRoles;
                        }
                        else if (exactRoles.Count != 0)
                        {
                            unitCardPresenceProbability = 1.0 - notPresentAcrossRoles;
                        }

                        // A fallback VMD root is a category-level consumer. It is a union
                        // with any exact WSModel/direct-asset evidence, not an alternate
                        // branch that is ignored whenever the unit happens to have another
                        // expected visual occurrence.
                        if (fallbackCoveredByCategory[category].Contains(unitId))
                        {
                            unitCardPresenceProbability = Math.Max(
                                unitCardPresenceProbability,
                                1.0);
                        }

                        if (unitCardPresenceProbability <= 0)
                            continue;

                        perSlotPresenceProbability +=
                            unitCardPresenceProbability / population;
                    }

                    perSlotPresenceProbability = Math.Clamp(
                        perSlotPresenceProbability,
                        0.0,
                        1.0);
                    notResidentProbability *= Math.Pow(
                        1.0 - perSlotPresenceProbability,
                        slotCount);
                }

                probabilitiesByCulture[culture] = Math.Clamp(
                    1.0 - notResidentProbability,
                    0.0,
                    1.0);
            }

            return CombineBattleResidentProbabilities(
                probabilitiesByCulture,
                model.PlayerCultureWeights,
                model.OpponentCultureWeights);
        }

        private static double GetWeightedCultureAverage(
            IReadOnlyDictionary<string, double> valuesByCulture,
            IReadOnlyDictionary<string, double> cultureWeights)
        {
            var totalWeight = cultureWeights.Values
                .Where(weight => weight > 0)
                .Sum();
            if (totalWeight <= 0)
                return 0;

            double total = 0;
            foreach (var (culture, weight) in cultureWeights)
            {
                if (weight <= 0)
                    continue;

                total += weight * valuesByCulture.GetValueOrDefault(culture);
            }

            return total / totalWeight;
        }

        private static double CombineBattleResidentProbabilities(
            IReadOnlyDictionary<string, double> probabilitiesByCulture,
            IReadOnlyDictionary<string, double> playerCultureWeights,
            IReadOnlyDictionary<string, double> opponentCultureWeights)
        {
            var playerProbability = Math.Clamp(
                GetWeightedCultureAverage(
                    probabilitiesByCulture,
                    playerCultureWeights),
                0.0,
                1.0);
            var opponentProbability = Math.Clamp(
                GetWeightedCultureAverage(
                    probabilitiesByCulture,
                    opponentCultureWeights),
                0.0,
                1.0);

            // The two armies are independent samples. For an asset used by only one culture,
            // unrelated opponents contribute zero and this reduces to a mirror-battle
            // adjustment. Genuine cross-culture sharing is handled automatically.
            return Math.Clamp(
                1.0 - (1.0 - playerProbability) * (1.0 - opponentProbability),
                0.0,
                1.0);
        }

        private static double CalculateExpectedArmyResidentPixels(
            BatchState state,
            IReadOnlyList<List<AtlasCandidate>> batches)
        {
            if (state.ArmyResidencyModel == null || batches.Count == 0)
                return 0;

            var rootsByMesh = BuildCandidateRootVmdPaths(
                state,
                batches.SelectMany(batch => batch));
            double total = 0;
            foreach (var batch in batches)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                if (!TryGetGeneratedAtlasPixelCost(
                        state,
                        batch,
                        out var pixels,
                        out _))
                {
                    continue;
                }

                total += GetExpectedArmyResidentPixels(
                    state,
                    pixels,
                    batch,
                    rootsByMesh);
            }

            return total;
        }

        private static HashSet<string> GetArmyCulturesForRoots(
            ArmyResidencyModel? model,
            IEnumerable<string> roots)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (model == null)
                return result;

            foreach (var rootValue in roots)
            {
                var root = Normalize(rootValue);
                if (!model.UnitsByVmd.TryGetValue(root, out var byCategory))
                    continue;

                foreach (var unitId in byCategory.Values.SelectMany(unitIds => unitIds))
                {
                    if (model.CulturesByUnit.TryGetValue(unitId, out var cultures))
                        result.UnionWith(cultures);
                }
            }

            return result;
        }

        private static HashSet<string> GetRelevantArmyUnitIds(
            ArmyResidencyModel model,
            IEnumerable<string> assetPaths,
            IEnumerable<string> fallbackRoots)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assetPathValue in assetPaths)
            {
                var assetPath = Normalize(assetPathValue);
                if (assetPath.Length == 0 ||
                    !model.UnitIdsByAssetPath.TryGetValue(assetPath, out var unitIds))
                {
                    continue;
                }

                result.UnionWith(unitIds);
            }

            // VMD roots are a separate residency channel. They intentionally do not
            // depend on UnitIdsByAssetPath: a fallback root is resolved through the
            // scenario's VMD-to-unit mapping, even when the asset index did not retain a
            // direct asset entry for that root.
            foreach (var rootValue in fallbackRoots)
            {
                var root = Normalize(rootValue);
                if (root.Length == 0 ||
                    !model.UnitsByVmd.TryGetValue(root, out var unitsByCategory))
                {
                    continue;
                }

                result.UnionWith(
                    unitsByCategory.Values.SelectMany(unitIds => unitIds));
            }

            return result;
        }

        private static IEnumerable<string> EnumerateRelevantArmyUnitIds(
            IReadOnlySet<string> cultureUnits,
            IReadOnlySet<string> relevantUnitIds)
        {
            // Most mesh groups touch a small subset of a culture's roster. Iterate the
            // smaller side of the intersection so relevance filtering does not merely move
            // the full-roster scan behind a Contains call.
            if (relevantUnitIds.Count < cultureUnits.Count)
            {
                foreach (var unitId in relevantUnitIds)
                {
                    if (cultureUnits.Contains(unitId))
                        yield return unitId;
                }

                yield break;
            }

            foreach (var unitId in cultureUnits)
            {
                if (relevantUnitIds.Contains(unitId))
                    yield return unitId;
            }
        }

        private static HashSet<string> GetRelevantArmyCultures(
            ArmyResidencyModel model,
            IEnumerable<string> assetPaths,
            IEnumerable<string> fallbackRoots,
            IEnumerable<string> relevantUnitIds)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assetPathValue in assetPaths)
            {
                var assetPath = Normalize(assetPathValue);
                if (assetPath.Length == 0 ||
                    !model.CulturesByAssetPath.TryGetValue(assetPath, out var cultures))
                {
                    continue;
                }

                result.UnionWith(cultures);
            }

            foreach (var rootValue in fallbackRoots)
            {
                var root = Normalize(rootValue);
                if (root.Length == 0 ||
                    !model.UnitsByVmd.TryGetValue(root, out var unitsByCategory))
                {
                    continue;
                }

                foreach (var unitId in unitsByCategory.Values.SelectMany(unitIds => unitIds))
                {
                    if (model.CulturesByUnit.TryGetValue(unitId, out var cultures))
                        result.UnionWith(cultures);
                }
            }

            // expectedEntitiesByMesh can provide a conservative unit fallback for an asset
            // whose dependency path was not indexed. Include those units' cultures too.
            foreach (var unitId in relevantUnitIds)
            {
                if (model.CulturesByUnit.TryGetValue(unitId, out var cultures))
                    result.UnionWith(cultures);
            }

            return result;
        }

        private static HashSet<Wh3ArmyUnitCategory> GetArmyCategoriesForRoots(
            ArmyResidencyModel? model,
            IEnumerable<string> roots)
        {
            var result = new HashSet<Wh3ArmyUnitCategory>();
            if (model == null)
                return result;

            foreach (var root in roots)
            {
                if (!model.UnitsByVmd.TryGetValue(root, out var byCategory))
                    continue;

                foreach (var category in byCategory.Keys)
                    result.Add(category);
            }

            return result;
        }

        private static void OptimizeBatchForVmdLocality(
            BatchState state,
            List<AtlasCandidate> batch,
            IReadOnlyDictionary<MeshKey, HashSet<string>> rootsByMesh,
            IReadOnlyList<MergeAffinityGroup> affinityGroups,
            List<List<AtlasCandidate>> output)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            const int maxAcceptedLocalitySplits = 32;
            const int maxGlobalPixelIncreasePercent = 25;
            const int minimumResidentPixelSavingPercent = 10;
            const double comparisonEpsilon = 0.5;

            if (batch.Count < 4 ||
                state.VmdLocalitySplitsAccepted >= maxAcceptedLocalitySplits ||
                !TryGetGeneratedAtlasPixelCost(
                    state,
                    batch,
                    out var baselinePixels,
                    out _))
            {
                output.Add(batch);
                return;
            }

            var baselineRootCount = GetBatchRootCount(batch, rootsByMesh);
            if (baselineRootCount <= 1)
            {
                output.Add(batch);
                return;
            }

            var baselineVmdResidentPixels = GetAtlasResidencyProxy(
                baselinePixels,
                baselineRootCount);
            var baselineArmyResidentPixels = GetExpectedArmyResidentPixels(
                state,
                baselinePixels,
                batch,
                rootsByMesh);
            var useArmyMetric =
                state.ArmyResidencyModel != null &&
                baselineArmyResidentPixels > comparisonEpsilon;
            var baselineAffinity = CalculateMergeAffinityScore(
                [batch],
                affinityGroups);

            AtlasBatchSplitProposal? bestProposal = null;
            var bestVmdResidentPixels = baselineVmdResidentPixels;
            var bestArmyResidentPixels = baselineArmyResidentPixels;
            var bestGlobalPixels = baselinePixels;
            var bestAffinity = baselineAffinity;
            var evaluations = new List<(
                AtlasBatchSplitProposal Proposal,
                ArmyLocalitySplitEvaluationReportEntry Entry)>();

            foreach (var proposal in CreateVmdLocalitySplitProposals(
                         batch,
                         rootsByMesh,
                         state.ArmyResidencyModel,
                         state.CancellationToken))
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                state.VmdLocalitySplitEvaluations++;

                if (!TryGetGeneratedAtlasPixelCost(
                        state,
                        proposal.Left,
                        out var leftPixels,
                        out _) ||
                    !TryGetGeneratedAtlasPixelCost(
                        state,
                        proposal.Right,
                        out var rightPixels,
                        out _))
                {
                    continue;
                }

                var combinedPixels = checked(leftPixels + rightPixels);
                var leftRootCount = GetBatchRootCount(proposal.Left, rootsByMesh);
                var rightRootCount = GetBatchRootCount(proposal.Right, rootsByMesh);
                var combinedVmdResidentPixels = checked(
                    GetAtlasResidencyProxy(leftPixels, leftRootCount) +
                    GetAtlasResidencyProxy(rightPixels, rightRootCount));
                var combinedArmyResidentPixels =
                    GetExpectedArmyResidentPixels(
                        state,
                        leftPixels,
                        proposal.Left,
                        rootsByMesh) +
                    GetExpectedArmyResidentPixels(
                        state,
                        rightPixels,
                        proposal.Right,
                        rootsByMesh);

                string? rejectionReason = null;
                if (combinedPixels * 100 >
                    baselinePixels * (100L + maxGlobalPixelIncreasePercent))
                {
                    rejectionReason = "global-pixel-cap";
                }
                else if (useArmyMetric &&
                         combinedArmyResidentPixels * 100.0 >
                         baselineArmyResidentPixels *
                         (100.0 - minimumResidentPixelSavingPercent))
                {
                    rejectionReason = "insufficient-expected-army-saving";
                }
                else if (useArmyMetric &&
                         combinedVmdResidentPixels > baselineVmdResidentPixels)
                {
                    rejectionReason = "VMD-residency-regression";
                }
                else if (!useArmyMetric &&
                         combinedVmdResidentPixels * 100 >
                         baselineVmdResidentPixels *
                         (100L - minimumResidentPixelSavingPercent))
                {
                    rejectionReason = "insufficient-VMD-residency-saving";
                }

                if (rejectionReason != null)
                {
                    evaluations.Add((
                        proposal,
                        new ArmyLocalitySplitEvaluationReportEntry(
                            false,
                            rejectionReason,
                            baselinePixels,
                            combinedPixels,
                            baselineVmdResidentPixels,
                            combinedVmdResidentPixels,
                            baselineArmyResidentPixels,
                            combinedArmyResidentPixels)));
                    continue;
                }

                var proposedAffinity = CalculateMergeAffinityScore(
                    [proposal.Left, proposal.Right],
                    affinityGroups);
                if (proposedAffinity < baselineAffinity)
                {
                    evaluations.Add((
                        proposal,
                        new ArmyLocalitySplitEvaluationReportEntry(
                            false,
                            "merge-affinity-regression",
                            baselinePixels,
                            combinedPixels,
                            baselineVmdResidentPixels,
                            combinedVmdResidentPixels,
                            baselineArmyResidentPixels,
                            combinedArmyResidentPixels)));
                    continue;
                }

                evaluations.Add((
                    proposal,
                    new ArmyLocalitySplitEvaluationReportEntry(
                        false,
                        string.Empty,
                        baselinePixels,
                        combinedPixels,
                        baselineVmdResidentPixels,
                        combinedVmdResidentPixels,
                        baselineArmyResidentPixels,
                        combinedArmyResidentPixels)));

                var better = useArmyMetric
                    ? combinedArmyResidentPixels < bestArmyResidentPixels - comparisonEpsilon ||
                      (Math.Abs(combinedArmyResidentPixels - bestArmyResidentPixels) <= comparisonEpsilon &&
                       combinedVmdResidentPixels < bestVmdResidentPixels) ||
                      (Math.Abs(combinedArmyResidentPixels - bestArmyResidentPixels) <= comparisonEpsilon &&
                       combinedVmdResidentPixels == bestVmdResidentPixels &&
                       combinedPixels < bestGlobalPixels) ||
                      (Math.Abs(combinedArmyResidentPixels - bestArmyResidentPixels) <= comparisonEpsilon &&
                       combinedVmdResidentPixels == bestVmdResidentPixels &&
                       combinedPixels == bestGlobalPixels &&
                       proposedAffinity > bestAffinity)
                    : combinedVmdResidentPixels < bestVmdResidentPixels ||
                      (combinedVmdResidentPixels == bestVmdResidentPixels &&
                       combinedPixels < bestGlobalPixels) ||
                      (combinedVmdResidentPixels == bestVmdResidentPixels &&
                       combinedPixels == bestGlobalPixels &&
                       proposedAffinity > bestAffinity);

                if (!better)
                    continue;

                bestProposal = proposal;
                bestVmdResidentPixels = combinedVmdResidentPixels;
                bestArmyResidentPixels = combinedArmyResidentPixels;
                bestGlobalPixels = combinedPixels;
                bestAffinity = proposedAffinity;
            }

            foreach (var (proposal, entry) in evaluations)
            {
                var accepted = bestProposal != null && ReferenceEquals(proposal, bestProposal);
                state.ArmyLocalitySplitEvaluations.Add(
                    entry with
                    {
                        Accepted = accepted,
                        Decision = accepted
                            ? "accepted"
                            : string.IsNullOrEmpty(entry.Decision)
                                ? "eligible-not-selected"
                                : entry.Decision,
                    });
            }

            if (bestProposal == null)
            {
                output.Add(batch);
                return;
            }

            state.VmdLocalitySplitsAccepted++;
            state.VmdLocalityResidentPixelsSaved = checked(
                state.VmdLocalityResidentPixelsSaved +
                baselineVmdResidentPixels -
                bestVmdResidentPixels);
            state.ExpectedArmyResidentPixelsSavedByLocality += Math.Max(
                0,
                baselineArmyResidentPixels - bestArmyResidentPixels);
            state.VmdLocalityGlobalPixelsAdded = checked(
                state.VmdLocalityGlobalPixelsAdded +
                Math.Max(0, bestGlobalPixels - baselinePixels));

            OptimizeBatchForVmdLocality(
                state,
                bestProposal.Left,
                rootsByMesh,
                affinityGroups,
                output);
            OptimizeBatchForVmdLocality(
                state,
                bestProposal.Right,
                rootsByMesh,
                affinityGroups,
                output);
        }

        private static IReadOnlyList<AtlasBatchSplitProposal> CreateVmdLocalitySplitProposals(
            IReadOnlyList<AtlasCandidate> batch,
            IReadOnlyDictionary<MeshKey, HashSet<string>> rootsByMesh,
            ArmyResidencyModel? armyModel,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var groups = batch
                .GroupBy(GetAtlasPlanningSourceIdentity)
                .Select(group =>
                {
                    var candidates = group
                        .OrderBy(x => x.RootVmdPath, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.Key.LodIndex)
                        .ThenBy(x => x.Key.PartIndex)
                        .ToList();
                    return new AtlasPlanningCandidateGroup(
                        group.Key,
                        candidates,
                        candidates[0],
                        GetCanonicalCrop(candidates[0]).Crop);
                })
                .ToList();

            if (groups.Count < 2)
                return [];

            var rootsByGroup = groups.ToDictionary(
                group => group.Identity,
                group =>
                {
                    var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var candidate in group.Candidates)
                    {
                        if (rootsByMesh.TryGetValue(candidate.Key, out var candidateRoots))
                            roots.UnionWith(candidateRoots);
                        else
                            roots.Add(Normalize(candidate.RootVmdPath));
                    }

                    return roots;
                });

            var distinctRoots = rootsByGroup.Values
                .SelectMany(roots => roots)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (distinctRoots.Count <= 1)
                return [];

            var proposals = new List<AtlasBatchSplitProposal>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void AddProposal(IEnumerable<AtlasPlanningCandidateGroup> leftGroups)
            {
                var leftGroupList = leftGroups.ToList();
                if (leftGroupList.Count == 0 || leftGroupList.Count == groups.Count)
                    return;

                var leftIdentities = leftGroupList
                    .Select(group => group.Identity)
                    .ToHashSet();
                var rightGroupList = groups
                    .Where(group => !leftIdentities.Contains(group.Identity))
                    .ToList();
                var left = leftGroupList.SelectMany(group => group.Candidates).ToList();
                var right = rightGroupList.SelectMany(group => group.Candidates).ToList();
                if (left.Count < 2 || right.Count < 2)
                    return;

                var signature = string.Join(
                    "\n",
                    left
                        .Select(candidate => candidate.Key.ToString())
                        .OrderBy(value => value, StringComparer.Ordinal));
                if (!seen.Add(signature))
                    return;

                proposals.Add(new AtlasBatchSplitProposal(left, right));
            }

            // Culture is the primary residency boundary. Consider every modeled culture
            // here, not only player-scored cultures, so proven opponent/shared-asset consumers
            // can still keep unrelated placements out of the same atlas.
            if (armyModel != null)
            {
                foreach (var culture in armyModel.UnitsByCultureAndCategory.Keys)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddProposal(groups.Where(group =>
                        GetArmyCulturesForRoots(
                            armyModel,
                            rootsByGroup[group.Identity])
                        .Contains(culture)));
                }

                foreach (var category in armyModel.Scenario.ArmySlotTemplate.Keys)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddProposal(groups.Where(group =>
                        GetArmyCategoriesForRoots(
                            armyModel,
                            rootsByGroup[group.Identity])
                        .Contains(category)));
                }
            }

            // Then try isolating the assets reachable from each root. Shared source/crop
            // identities stay indivisible, so genuinely reused placements remain shared.
            foreach (var root in distinctRoots
                         .OrderByDescending(root =>
                             groups.Count(group => rootsByGroup[group.Identity].Contains(root)))
                         .ThenBy(root => root, StringComparer.OrdinalIgnoreCase)
                         .Take(12))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddProposal(groups.Where(group =>
                    rootsByGroup[group.Identity].Contains(root)));
            }

            // Then cluster equal/similar root signatures. This catches unrelated VMD families
            // that happened to pack efficiently together under the old texture-first ordering.
            string RootSignature(AtlasPlanningCandidateGroup group)
                => string.Join(
                    "\u001f",
                    rootsByGroup[group.Identity]
                        .OrderBy(root => root, StringComparer.OrdinalIgnoreCase));

            var ordered = groups
                .OrderBy(RootSignature, StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => BuildAtlasPlanningOrderKey(group.Representative), StringComparer.Ordinal)
                .ToList();
            var boundaries = new List<int>();
            for (var index = 1; index < ordered.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!RootSignature(ordered[index - 1]).Equals(
                        RootSignature(ordered[index]),
                        StringComparison.OrdinalIgnoreCase))
                {
                    boundaries.Add(index);
                }
            }

            const int maxOrderedLocalitySplitEvaluations = 12;
            IEnumerable<int> selectedBoundaries = boundaries;
            if (boundaries.Count > maxOrderedLocalitySplitEvaluations)
            {
                selectedBoundaries = Enumerable.Range(0, maxOrderedLocalitySplitEvaluations)
                    .Select(index => boundaries[
                        index * (boundaries.Count - 1) /
                        (maxOrderedLocalitySplitEvaluations - 1)])
                    .Distinct();
            }

            foreach (var boundary in selectedBoundaries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddProposal(ordered.Take(boundary));
            }

            const int maxLocalitySplitProposals = 24;
            return proposals.Count <= maxLocalitySplitProposals
                ? proposals
                : proposals.Take(maxLocalitySplitProposals).ToList();
        }

        private static List<List<AtlasCandidate>> OptimizeBatchesForMergeAffinity(
            BatchState state,
            IReadOnlyList<List<AtlasCandidate>> batches)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            var working = batches.Select(batch => batch.ToList()).ToList();
            if (!state.MergeCompatibleMeshesEnabled || working.Count < 2)
                return working;

            var affinityGroups = BuildMergeAffinityGroups(
                state,
                working.SelectMany(batch => batch));
            if (affinityGroups.Count == 0)
                return working;

            var rootsByMesh = BuildCandidateRootVmdPaths(
                state,
                working.SelectMany(batch => batch));
            var expectedArmyEntitiesByMesh = BuildExpectedArmyEntitiesByMesh(
                state.ArmyResidencyModel,
                working.SelectMany(batch => batch),
                rootsByMesh,
                state.CancellationToken);
            var currentExpectedArmyDrawCallsEliminated =
                CalculateExpectedArmyDrawCallsEliminated(
                    state,
                    BuildBatchIndexByMesh(working),
                    affinityGroups,
                    expectedArmyEntitiesByMesh);

            state.MergeAwareAffinityPotentialBefore +=
                CalculateMergeAffinityScore(working, affinityGroups);
            state.ExpectedArmyDrawCallsEliminatedBeforeMergeAware +=
                currentExpectedArmyDrawCallsEliminated;

            const int maxPasses = 2;
            for (var pass = 0; pass < maxPasses; pass++)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                var changed = false;
                var batchByMesh = BuildBatchIndexByMesh(working);
                var candidatePairWeights = new Dictionary<AtlasBatchPair, int>();

                foreach (var group in affinityGroups)
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    var occupiedBatches = group.Meshes
                        .Where(batchByMesh.ContainsKey)
                        .Select(mesh => batchByMesh[mesh])
                        .Distinct()
                        .OrderBy(x => x)
                        .ToArray();

                    for (var left = 0; left < occupiedBatches.Length; left++)
                    {
                        for (var right = left + 1; right < occupiedBatches.Length; right++)
                        {
                            var pair = new AtlasBatchPair(
                                occupiedBatches[left],
                                occupiedBatches[right]);
                            candidatePairWeights[pair] =
                                candidatePairWeights.GetValueOrDefault(pair) + 1;
                        }
                    }
                }

                // Replanning a pair invokes the real atlas packer repeatedly. Bound the search
                // for pathological packs while prioritizing pairs that can reunite the largest
                // number of otherwise-mergeable groups.
                const int maxMergeAwareBatchPairsPerPass = 64;
                var candidatePairs = candidatePairWeights
                    .OrderByDescending(x => x.Value)
                    .ThenBy(x => x.Key.FirstBatchId)
                    .ThenBy(x => x.Key.SecondBatchId)
                    .Take(maxMergeAwareBatchPairsPerPass)
                    .Select(x => x.Key)
                    .ToList();

                state.MergeAwareBatchPairsConsidered += candidatePairs.Count;

                for (var pairIndex = 0; pairIndex < candidatePairs.Count; pairIndex++)
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    ReportProgress(
                        state.Progress,
                        "Optimizing atlas batches",
                        pairIndex + 1,
                        candidatePairs.Count,
                        $"Merge-affinity pass {pass + 1}: pair {pairIndex + 1}");
                    var pair = candidatePairs[pairIndex];
                    var leftIndex = pair.FirstBatchId;
                    var rightIndex = pair.SecondBatchId;
                    var currentLeft = working[leftIndex];
                    var currentRight = working[rightIndex];

                    if (!TryGetGeneratedAtlasPixelCost(
                            state,
                            currentLeft,
                            out var leftBaselinePixels,
                            out _) ||
                        !TryGetGeneratedAtlasPixelCost(
                            state,
                            currentRight,
                            out var rightBaselinePixels,
                            out _))
                    {
                        continue;
                    }

                    var baselinePixels = checked(leftBaselinePixels + rightBaselinePixels);
                    var baselineResidentPixels = checked(
                        GetAtlasResidencyProxy(
                            leftBaselinePixels,
                            GetBatchRootCount(currentLeft, rootsByMesh)) +
                        GetAtlasResidencyProxy(
                            rightBaselinePixels,
                            GetBatchRootCount(currentRight, rootsByMesh)));
                    var baselineArmyResidentPixels =
                        GetExpectedArmyResidentPixels(
                            state,
                            leftBaselinePixels,
                            currentLeft,
                            rootsByMesh) +
                        GetExpectedArmyResidentPixels(
                            state,
                            rightBaselinePixels,
                            currentRight,
                            rootsByMesh);
                    var baselineAffinity = CalculateMergeAffinityScore(working, affinityGroups);
                    var baselineExpectedArmyDrawCallsEliminated =
                        currentExpectedArmyDrawCallsEliminated;
                    AtlasBatchSplitProposal? bestProposal = null;
                    var bestPixels = baselinePixels;
                    var bestResidentPixels = baselineResidentPixels;
                    var bestArmyResidentPixels = baselineArmyResidentPixels;
                    var bestExpectedArmyDrawCallsEliminated =
                        baselineExpectedArmyDrawCallsEliminated;
                    var bestAffinity = baselineAffinity;

                    foreach (var proposal in CreateMergeAwareRepartitionProposals(
                                 currentLeft,
                                 currentRight,
                                 affinityGroups))
                    {
                        state.CancellationToken.ThrowIfCancellationRequested();
                        state.MergeAwareRepartitionEvaluations++;

                        if (!TryGetGeneratedAtlasPixelCost(
                                state,
                                proposal.Left,
                                out var leftPixels,
                                out _) ||
                            !TryGetGeneratedAtlasPixelCost(
                                state,
                                proposal.Right,
                                out var rightPixels,
                                out _))
                        {
                            continue;
                        }

                        var combinedPixels = checked(leftPixels + rightPixels);
                        if (combinedPixels > baselinePixels)
                            continue;

                        var proposedResidentPixels = checked(
                            GetAtlasResidencyProxy(
                                leftPixels,
                                GetBatchRootCount(proposal.Left, rootsByMesh)) +
                            GetAtlasResidencyProxy(
                                rightPixels,
                                GetBatchRootCount(proposal.Right, rootsByMesh)));
                        var proposedArmyResidentPixels =
                            GetExpectedArmyResidentPixels(
                                state,
                                leftPixels,
                                proposal.Left,
                                rootsByMesh) +
                            GetExpectedArmyResidentPixels(
                                state,
                                rightPixels,
                                proposal.Right,
                                rootsByMesh);
                        if ((state.ArmyResidencyModel != null &&
                             proposedArmyResidentPixels > baselineArmyResidentPixels + 0.5) ||
                            proposedResidentPixels > baselineResidentPixels)
                        {
                            state.MergeAwareLocalityRegressionsRejected++;
                            continue;
                        }

                        var proposedAffinity = CalculateMergeAffinityScoreWithReplacement(
                            working,
                            leftIndex,
                            rightIndex,
                            proposal,
                            affinityGroups);

                        // Keep the old unweighted merge count as a hard safety guard. The
                        // army-weighted score decides which non-regressing merge opportunity is
                        // actually more valuable in a representative battle.
                        if (proposedAffinity < baselineAffinity)
                            continue;

                        var proposedBatchByMesh = BuildBatchIndexByMesh(working);
                        foreach (var candidate in proposal.Left)
                            proposedBatchByMesh[candidate.Key] = leftIndex;
                        foreach (var candidate in proposal.Right)
                            proposedBatchByMesh[candidate.Key] = rightIndex;
                        var proposedExpectedArmyDrawCallsEliminated =
                            CalculateExpectedArmyDrawCallsEliminated(
                                state,
                                proposedBatchByMesh,
                                affinityGroups,
                                expectedArmyEntitiesByMesh);

                        const double expectedDrawComparisonEpsilon = 0.000001;
                        const double expectedResidencyComparisonEpsilon = 0.5;
                        var isBetter =
                            proposedArmyResidentPixels <
                                bestArmyResidentPixels - expectedResidencyComparisonEpsilon ||
                            (Math.Abs(
                                 proposedArmyResidentPixels -
                                 bestArmyResidentPixels) <= expectedResidencyComparisonEpsilon &&
                             proposedExpectedArmyDrawCallsEliminated >
                                 bestExpectedArmyDrawCallsEliminated +
                                 expectedDrawComparisonEpsilon) ||
                            (Math.Abs(
                                 proposedArmyResidentPixels -
                                 bestArmyResidentPixels) <= expectedResidencyComparisonEpsilon &&
                             Math.Abs(
                                 proposedExpectedArmyDrawCallsEliminated -
                                 bestExpectedArmyDrawCallsEliminated) <=
                                 expectedDrawComparisonEpsilon &&
                             combinedPixels < bestPixels) ||
                            (Math.Abs(
                                 proposedArmyResidentPixels -
                                 bestArmyResidentPixels) <= expectedResidencyComparisonEpsilon &&
                             Math.Abs(
                                 proposedExpectedArmyDrawCallsEliminated -
                                 bestExpectedArmyDrawCallsEliminated) <=
                                 expectedDrawComparisonEpsilon &&
                             combinedPixels == bestPixels &&
                             proposedResidentPixels < bestResidentPixels) ||
                            (Math.Abs(
                                 proposedArmyResidentPixels -
                                 bestArmyResidentPixels) <= expectedResidencyComparisonEpsilon &&
                             Math.Abs(
                                 proposedExpectedArmyDrawCallsEliminated -
                                 bestExpectedArmyDrawCallsEliminated) <=
                                 expectedDrawComparisonEpsilon &&
                             combinedPixels == bestPixels &&
                             proposedResidentPixels == bestResidentPixels &&
                             proposedAffinity > bestAffinity);

                        if (isBetter)
                        {
                            bestProposal = proposal;
                            bestPixels = combinedPixels;
                            bestResidentPixels = proposedResidentPixels;
                            bestArmyResidentPixels = proposedArmyResidentPixels;
                            bestExpectedArmyDrawCallsEliminated =
                                proposedExpectedArmyDrawCallsEliminated;
                            bestAffinity = proposedAffinity;
                        }
                    }

                    if (bestProposal == null)
                        continue;

                    working[leftIndex] = bestProposal.Left;
                    working[rightIndex] = bestProposal.Right;
                    state.MergeAwareRepartitionsAccepted++;
                    state.MergeAwareRepartitionPixelsSaved = checked(
                        state.MergeAwareRepartitionPixelsSaved +
                        baselinePixels -
                        bestPixels);
                    state.MergeAwareAffinityEliminationsGained +=
                        bestAffinity - baselineAffinity;
                    currentExpectedArmyDrawCallsEliminated =
                        bestExpectedArmyDrawCallsEliminated;
                    state.MergeAwareRepartitionEntries.Add(
                        new MergeAwareRepartitionReportEntry(
                            state.BatchIndex + leftIndex,
                            state.BatchIndex + rightIndex,
                            baselinePixels,
                            bestPixels,
                            baselineAffinity,
                            bestAffinity,
                            baselineExpectedArmyDrawCallsEliminated,
                            bestExpectedArmyDrawCallsEliminated));
                    changed = true;
                }

                if (!changed)
                    break;
            }

            CoalesceMergeAwareBatchesWithoutPixelIncrease(
                state,
                working,
                affinityGroups,
                rootsByMesh,
                expectedArmyEntitiesByMesh,
                ref currentExpectedArmyDrawCallsEliminated);

            state.MergeAwareAffinityPotentialAfter +=
                CalculateMergeAffinityScore(working, affinityGroups);
            return working;
        }

        private static void CoalesceMergeAwareBatchesWithoutPixelIncrease(
            BatchState state,
            List<List<AtlasCandidate>> working,
            IReadOnlyList<MergeAffinityGroup> affinityGroups,
            IReadOnlyDictionary<MeshKey, HashSet<string>> rootsByMesh,
            IReadOnlyDictionary<
                MeshKey,
                Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>> expectedArmyEntitiesByMesh,
            ref double currentExpectedArmyDrawCallsEliminated)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            const int maxCoalesces = 16;
            const int maxPairEvaluationsPerPass = 64;

            for (var pass = 0; pass < maxCoalesces && working.Count >= 2; pass++)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                ReportProgress(
                    state.Progress,
                    "Optimizing atlas batches",
                    pass + 1,
                    maxCoalesces,
                    $"Coalescing merge batches, pass {pass + 1}");
                var batchByMesh = BuildBatchIndexByMesh(working);
                var candidatePairWeights = new Dictionary<AtlasBatchPair, int>();

                foreach (var group in affinityGroups)
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    var occupiedBatches = group.Meshes
                        .Where(batchByMesh.ContainsKey)
                        .Select(mesh => batchByMesh[mesh])
                        .Distinct()
                        .OrderBy(x => x)
                        .ToArray();

                    for (var left = 0; left < occupiedBatches.Length; left++)
                    {
                        for (var right = left + 1; right < occupiedBatches.Length; right++)
                        {
                            var pair = new AtlasBatchPair(
                                occupiedBatches[left],
                                occupiedBatches[right]);
                            candidatePairWeights[pair] =
                                candidatePairWeights.GetValueOrDefault(pair) + 1;
                        }
                    }
                }

                if (candidatePairWeights.Count == 0)
                    break;

                var baselineAffinity = CalculateMergeAffinityScore(working, affinityGroups);
                AtlasBatchPair? bestPair = null;
                List<AtlasCandidate>? bestCombined = null;
                var bestAffinity = baselineAffinity;
                var bestArmyResidentPixelsSaved = double.NegativeInfinity;
                var bestExpectedArmyDrawCallsEliminated =
                    currentExpectedArmyDrawCallsEliminated;
                long bestPixelsSaved = long.MinValue;
                var bestPairWeight = -1;

                var candidatePairs = candidatePairWeights
                    .OrderByDescending(x => x.Value)
                    .ThenBy(x => x.Key.FirstBatchId)
                    .ThenBy(x => x.Key.SecondBatchId)
                    .Take(maxPairEvaluationsPerPass)
                    .ToArray();
                for (var pairIndex = 0; pairIndex < candidatePairs.Length; pairIndex++)
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    ReportProgress(
                        state.Progress,
                        "Optimizing atlas batches",
                        pairIndex + 1,
                        candidatePairs.Length,
                        $"Coalescing pass {pass + 1}: pair {pairIndex + 1}");
                    var entry = candidatePairs[pairIndex];
                    var pair = entry.Key;
                    var leftBatch = working[pair.FirstBatchId];
                    var rightBatch = working[pair.SecondBatchId];

                    if (!TryGetGeneratedAtlasPixelCost(
                            state,
                            leftBatch,
                            out var leftPixels,
                            out _) ||
                        !TryGetGeneratedAtlasPixelCost(
                            state,
                            rightBatch,
                            out var rightPixels,
                            out _))
                    {
                        continue;
                    }

                    var baselinePixels = checked(leftPixels + rightPixels);
                    var baselineResidentPixels = checked(
                        GetAtlasResidencyProxy(
                            leftPixels,
                            GetBatchRootCount(leftBatch, rootsByMesh)) +
                        GetAtlasResidencyProxy(
                            rightPixels,
                            GetBatchRootCount(rightBatch, rootsByMesh)));
                    var baselineArmyResidentPixels =
                        GetExpectedArmyResidentPixels(
                            state,
                            leftPixels,
                            leftBatch,
                            rootsByMesh) +
                        GetExpectedArmyResidentPixels(
                            state,
                            rightPixels,
                            rightBatch,
                            rootsByMesh);
                    var combined = leftBatch.Concat(rightBatch).ToList();
                    state.MergeAwareBatchCoalesceEvaluations++;
                    if (!TryGetGeneratedAtlasPixelCost(
                            state,
                            combined,
                            out var combinedPixels,
                            out _) ||
                        combinedPixels > baselinePixels)
                    {
                        continue;
                    }

                    var combinedResidentPixels = GetAtlasResidencyProxy(
                        combinedPixels,
                        GetBatchRootCount(combined, rootsByMesh));
                    var combinedArmyResidentPixels = GetExpectedArmyResidentPixels(
                        state,
                        combinedPixels,
                        combined,
                        rootsByMesh);
                    if ((state.ArmyResidencyModel != null &&
                         combinedArmyResidentPixels > baselineArmyResidentPixels + 0.5) ||
                        combinedResidentPixels > baselineResidentPixels)
                    {
                        state.MergeAwareLocalityRegressionsRejected++;
                        continue;
                    }

                    var proposedAffinity = CalculateMergeAffinityScoreWithMerge(
                        working,
                        pair.FirstBatchId,
                        pair.SecondBatchId,
                        affinityGroups);
                    if (proposedAffinity <= baselineAffinity)
                        continue;

                    var proposedBatchByMesh = BuildBatchIndexByMesh(working);
                    foreach (var candidate in rightBatch)
                        proposedBatchByMesh[candidate.Key] = pair.FirstBatchId;
                    var proposedExpectedArmyDrawCallsEliminated =
                        CalculateExpectedArmyDrawCallsEliminated(
                            state,
                            proposedBatchByMesh,
                            affinityGroups,
                            expectedArmyEntitiesByMesh);

                    var pixelsSaved = baselinePixels - combinedPixels;
                    var armyResidentPixelsSaved =
                        baselineArmyResidentPixels - combinedArmyResidentPixels;
                    const double expectedDrawComparisonEpsilon = 0.000001;
                    const double expectedResidencyComparisonEpsilon = 0.5;
                    var isBetter =
                        armyResidentPixelsSaved >
                            bestArmyResidentPixelsSaved + expectedResidencyComparisonEpsilon ||
                        (Math.Abs(
                             armyResidentPixelsSaved -
                             bestArmyResidentPixelsSaved) <= expectedResidencyComparisonEpsilon &&
                         proposedExpectedArmyDrawCallsEliminated >
                             bestExpectedArmyDrawCallsEliminated +
                             expectedDrawComparisonEpsilon) ||
                        (Math.Abs(
                             armyResidentPixelsSaved -
                             bestArmyResidentPixelsSaved) <= expectedResidencyComparisonEpsilon &&
                         Math.Abs(
                             proposedExpectedArmyDrawCallsEliminated -
                             bestExpectedArmyDrawCallsEliminated) <=
                             expectedDrawComparisonEpsilon &&
                         pixelsSaved > bestPixelsSaved) ||
                        (Math.Abs(
                             armyResidentPixelsSaved -
                             bestArmyResidentPixelsSaved) <= expectedResidencyComparisonEpsilon &&
                         Math.Abs(
                             proposedExpectedArmyDrawCallsEliminated -
                             bestExpectedArmyDrawCallsEliminated) <=
                             expectedDrawComparisonEpsilon &&
                         pixelsSaved == bestPixelsSaved &&
                         proposedAffinity > bestAffinity) ||
                        (Math.Abs(
                             armyResidentPixelsSaved -
                             bestArmyResidentPixelsSaved) <= expectedResidencyComparisonEpsilon &&
                         Math.Abs(
                             proposedExpectedArmyDrawCallsEliminated -
                             bestExpectedArmyDrawCallsEliminated) <=
                             expectedDrawComparisonEpsilon &&
                         pixelsSaved == bestPixelsSaved &&
                         proposedAffinity == bestAffinity &&
                         entry.Value > bestPairWeight);
                    if (!isBetter)
                        continue;

                    bestPair = pair;
                    bestCombined = combined;
                    bestAffinity = proposedAffinity;
                    bestArmyResidentPixelsSaved = armyResidentPixelsSaved;
                    bestExpectedArmyDrawCallsEliminated =
                        proposedExpectedArmyDrawCallsEliminated;
                    bestPixelsSaved = pixelsSaved;
                    bestPairWeight = entry.Value;
                }

                if (bestPair == null || bestCombined == null)
                    break;

                var selected = bestPair.Value;
                working[selected.FirstBatchId] = bestCombined;
                working.RemoveAt(selected.SecondBatchId);
                state.MergeAwareBatchCoalescesAccepted++;
                state.MergeAwareBatchCoalescePixelsSaved = checked(
                    state.MergeAwareBatchCoalescePixelsSaved +
                    Math.Max(0, bestPixelsSaved));
                state.MergeAwareAffinityEliminationsGained +=
                    bestAffinity - baselineAffinity;
                currentExpectedArmyDrawCallsEliminated =
                    bestExpectedArmyDrawCallsEliminated;
            }
        }

        private static int CalculateMergeAffinityScoreWithMerge(
            IReadOnlyList<List<AtlasCandidate>> batches,
            int leftIndex,
            int rightIndex,
            IReadOnlyList<MergeAffinityGroup> affinityGroups)
        {
            var batchByMesh = BuildBatchIndexByMesh(batches);
            foreach (var candidate in batches[rightIndex])
                batchByMesh[candidate.Key] = leftIndex;

            var score = 0;
            foreach (var group in affinityGroups)
                score += CalculateMergeAffinityContribution(group, batchByMesh);

            return score;
        }

        private static bool CanEarnMergeDrawCredit(string? embeddedRigidPath)
            => string.IsNullOrWhiteSpace(embeddedRigidPath);

        private static bool CanEarnMergeDrawCredit(AtlasCandidate candidate)
            => candidate.Usages.All(usage =>
                CanEarnMergeDrawCredit(usage.EmbeddedRigidPath));

        private static MergeAffinityIdentity BuildMergeAffinityIdentity(
            string geometryPath,
            int lodIndex,
            string rmvIdentity,
            string materialIdentity)
            => new(
                geometryPath,
                lodIndex,
                rmvIdentity,
                materialIdentity);

        private static MergeAffinityIdentity BuildMergeAffinityIdentity(
            BatchState state,
            AtlasCandidate candidate)
            => BuildMergeAffinityIdentity(
                candidate.Key.GeometryPath,
                candidate.Key.LodIndex,
                GetRmvMergeIdentity(candidate.Model),
                BuildMergeAffinityMaterialIdentity(state, candidate));

        private static List<MergeAffinityGroup> BuildMergeAffinityGroups(
            BatchState state,
            IEnumerable<AtlasCandidate> candidates)
            => BuildMergeAffinityGroups(state, candidates, recordDiagnostics: true);

        private static List<MergeAffinityGroup> BuildMergeAffinityGroups(
            BatchState state,
            IEnumerable<AtlasCandidate> candidates,
            bool recordDiagnostics = true)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            var result = new List<MergeAffinityGroup>();

            // Group by the material state that would remain after a safe texture rewrite.
            // This deliberately admits only texture-only counterfactuals; shader and parameter
            // differences remain separate. The value gate still decides whether the resulting
            // atlas residency is worth accepting.
            var prepared = candidates
                .Where(CanEarnMergeDrawCredit)
                .Where(candidate => !HasUnrewritableMaterialConsumerAtlasConflict(state, candidate))
                .Select(candidate => new MergeAffinityCandidateInfo(
                    candidate,
                    GetRmvMergeIdentity(candidate.Model),
                    BuildMergeAffinityMaterialIdentity(state, candidate),
                    GetMaterialMergeDiagnosticSnapshot(
                        state,
                        candidate.MaterialPath,
                        candidate.MaterialDocument)))
                .ToList();

            var buckets = prepared
                .GroupBy(info => new MergeAffinityBucketIdentity(
                    info.Candidate.Key.GeometryPath,
                    info.Candidate.Key.LodIndex,
                    info.RmvIdentity,
                    info.MaterialSnapshot?.Shader ?? string.Empty,
                    info.MaterialSnapshot?.NonTextureIdentity ??
                        $"strict:{info.StrictMaterialIdentity}",
                    info.MaterialSnapshot != null));

            foreach (var bucket in buckets)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                var current = new List<MergeAffinityCandidateInfo>();
                var currentVertexCount = 0;

                foreach (var info in bucket
                             .OrderBy(x => x.Candidate.Key.PartIndex)
                             .ThenBy(x => x.Candidate.RootVmdPath, StringComparer.OrdinalIgnoreCase))
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    var candidate = info.Candidate;
                    var vertexCount = candidate.Model.Mesh.VertexList.Length;
                    if (vertexCount > ushort.MaxValue)
                        continue;

                    var canJoinCurrent = current.Count == 0;
                    var joinsProspectivePair = false;
                    if (!canJoinCurrent)
                    {
                        canJoinCurrent = true;
                        foreach (var existing in current)
                        {
                            state.CancellationToken.ThrowIfCancellationRequested();
                            // StrictMaterialIdentity is the material identity after the
                            // atlas rewrite modeled by BuildMergeAffinityMaterialIdentity.
                            // It must match exactly before a group can be admitted. The old
                            // logic grouped by pre-rewrite non-texture fields and then allowed
                            // a texture-only pair even when constant-vs-resolved channels (or
                            // XML texture-node structure) still produced different generated
                            // materials at execution time.
                            if (!existing.StrictMaterialIdentity.Equals(
                                    info.StrictMaterialIdentity,
                                    StringComparison.Ordinal))
                            {
                                canJoinCurrent = false;
                                break;
                            }

                            if (existing.MaterialSnapshot == null ||
                                info.MaterialSnapshot == null ||
                                existing.MaterialSnapshot.RenderingIdentity.Equals(
                                    info.MaterialSnapshot.RenderingIdentity,
                                    StringComparison.Ordinal))
                            {
                                continue;
                            }

                            if (!AreCounterfactuallyMergeCompatible(
                                    state,
                                    existing,
                                    info))
                            {
                                canJoinCurrent = false;
                                break;
                            }

                            joinsProspectivePair = true;
                        }
                    }

                    if (current.Count != 0 &&
                        (!canJoinCurrent ||
                         currentVertexCount + vertexCount > ushort.MaxValue))
                    {
                        if (current.Count > 1)
                        {
                            var group = CreateMergeAffinityGroup(state, current);
                            result.Add(group);
                            if (recordDiagnostics && group.ProspectiveTextureMergeDraws > 0)
                                state.ProspectiveMergeAffinityGroupsAdded++;
                        }

                        current = [];
                        currentVertexCount = 0;
                    }

                    current.Add(info);
                    currentVertexCount += vertexCount;
                    if (recordDiagnostics && joinsProspectivePair)
                    {
                        state.ProspectiveMergeAffinityPairs++;
                    }
                }

                if (current.Count > 1)
                {
                    var group = CreateMergeAffinityGroup(state, current);
                    result.Add(group);
                    if (recordDiagnostics && group.ProspectiveTextureMergeDraws > 0)
                        state.ProspectiveMergeAffinityGroupsAdded++;
                }
            }

            return result;
        }

        private static MergeAffinityGroup CreateMergeAffinityGroup(
            BatchState state,
            IReadOnlyList<MergeAffinityCandidateInfo> candidates)
        {
            var meshes = candidates
                .Select(info => info.Candidate.Key)
                .ToArray();
            var preAtlasMaterialIdentities = candidates
                .Select(GetPreAtlasMaterialIdentity)
                .ToArray();

            return new MergeAffinityGroup(
                meshes,
                CalculateProspectiveTextureMergeDraws(preAtlasMaterialIdentities),
                GetPreExistingStructuralMergePairs(state, meshes),
                preAtlasMaterialIdentities);
        }

        private static MergeAffinityGroup FilterMergeAffinityGroup(
            MergeAffinityGroup group,
            IReadOnlySet<MeshKey> candidateKeys)
        {
            var selected = group.Meshes
                .Select((mesh, index) => (mesh, index))
                .Where(entry => candidateKeys.Contains(entry.mesh))
                .ToArray();
            var meshes = selected
                .Select(entry => entry.mesh)
                .ToArray();
            var preAtlasMaterialIdentities = selected
                .Select(entry => group.PreAtlasMaterialIdentities[entry.index])
                .ToArray();

            return new MergeAffinityGroup(
                meshes,
                CalculateProspectiveTextureMergeDraws(preAtlasMaterialIdentities),
                FilterPreExistingStructuralMergePairs(group, meshes),
                preAtlasMaterialIdentities);
        }
    }
}
