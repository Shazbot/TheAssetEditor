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
        private static int CalculateMergeAffinityScoreWithReplacement(
            IReadOnlyList<List<AtlasCandidate>> batches,
            int leftIndex,
            int rightIndex,
            AtlasBatchSplitProposal proposal,
            IReadOnlyList<MergeAffinityGroup> affinityGroups)
        {
            var batchByMesh = BuildBatchIndexByMesh(batches);
            foreach (var candidate in proposal.Left)
                batchByMesh[candidate.Key] = leftIndex;
            foreach (var candidate in proposal.Right)
                batchByMesh[candidate.Key] = rightIndex;

            var score = 0;
            foreach (var group in affinityGroups)
                score += CalculateMergeAffinityContribution(group, batchByMesh);

            return score;
        }

        private static IReadOnlyList<AtlasBatchSplitProposal> CreateMergeAwareRepartitionProposals(
            IReadOnlyList<AtlasCandidate> currentLeft,
            IReadOnlyList<AtlasCandidate> currentRight,
            IReadOnlyList<MergeAffinityGroup> affinityGroups)
        {
            var combined = currentLeft
                .Concat(currentRight)
                .GroupBy(x => x.Key)
                .Select(group => group.First())
                .ToList();

            var planningGroups = combined
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

            if (planningGroups.Count < 2)
                return [];

            var proposals = new List<AtlasBatchSplitProposal>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void AddProposal(
                IEnumerable<AtlasPlanningCandidateGroup> leftGroups,
                IEnumerable<AtlasPlanningCandidateGroup> rightGroups)
            {
                var leftGroupList = leftGroups.ToList();
                var rightGroupList = rightGroups.ToList();
                var left = leftGroupList.SelectMany(x => x.Candidates).ToList();
                var right = rightGroupList.SelectMany(x => x.Candidates).ToList();

                if (left.Count < 2 || right.Count < 2)
                    return;

                var signature = string.Join(
                    "\n",
                    left.Select(x => x.Key.ToString())
                        .OrderBy(x => x, StringComparer.Ordinal));
                if (!seen.Add(signature))
                    return;

                proposals.Add(new AtlasBatchSplitProposal(left, right));
            }

            var planningGroupByMesh = new Dictionary<MeshKey, int>();
            for (var groupIndex = 0; groupIndex < planningGroups.Count; groupIndex++)
            {
                foreach (var candidate in planningGroups[groupIndex].Candidates)
                    planningGroupByMesh[candidate.Key] = groupIndex;
            }

            var parent = Enumerable.Range(0, planningGroups.Count).ToArray();

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

            foreach (var affinity in affinityGroups)
            {
                var groupIndices = affinity.Meshes
                    .Where(planningGroupByMesh.ContainsKey)
                    .Select(mesh => planningGroupByMesh[mesh])
                    .Distinct()
                    .ToArray();

                for (var index = 1; index < groupIndices.Length; index++)
                    Union(groupIndices[0], groupIndices[index]);
            }

            var components = Enumerable.Range(0, planningGroups.Count)
                .GroupBy(Find)
                .Select(component => component
                    .Select(index => planningGroups[index])
                    .ToList())
                .ToList();

            long ComponentArea(IReadOnlyList<AtlasPlanningCandidateGroup> component)
                => component.Sum(group => GetPaddedCropArea(group.Crop));

            int ComponentAffinity(IReadOnlyList<AtlasPlanningCandidateGroup> component)
            {
                var meshes = component
                    .SelectMany(group => group.Candidates)
                    .Select(candidate => candidate.Key)
                    .ToHashSet();
                var batchByMesh = meshes.ToDictionary(mesh => mesh, _ => 0);
                return affinityGroups.Sum(group =>
                    CalculateMergeAffinityContribution(group, batchByMesh));
            }

            void AddBalancedComponentProposal(
                IEnumerable<List<AtlasPlanningCandidateGroup>> orderedComponents)
            {
                var leftComponents = new List<List<AtlasPlanningCandidateGroup>>();
                var rightComponents = new List<List<AtlasPlanningCandidateGroup>>();
                long leftArea = 0;
                long rightArea = 0;

                foreach (var component in orderedComponents)
                {
                    var area = ComponentArea(component);
                    if (leftComponents.Count == 0)
                    {
                        leftComponents.Add(component);
                        leftArea += area;
                    }
                    else if (rightComponents.Count == 0)
                    {
                        rightComponents.Add(component);
                        rightArea += area;
                    }
                    else if (leftArea <= rightArea)
                    {
                        leftComponents.Add(component);
                        leftArea += area;
                    }
                    else
                    {
                        rightComponents.Add(component);
                        rightArea += area;
                    }
                }

                AddProposal(
                    leftComponents.SelectMany(x => x),
                    rightComponents.SelectMany(x => x));
            }

            AddBalancedComponentProposal(
                components
                    .OrderByDescending(ComponentAffinity)
                    .ThenByDescending(ComponentArea));
            AddBalancedComponentProposal(
                components
                    .OrderByDescending(ComponentArea)
                    .ThenByDescending(ComponentAffinity));

            // Also try moving each currently split affinity group wholesale to either side.
            // Move whole planning identities, never individual duplicate source/crop users.
            var currentLeftKeys = currentLeft.Select(x => x.Key).ToHashSet();
            var currentRightKeys = currentRight.Select(x => x.Key).ToHashSet();
            var currentLeftGroupIds = planningGroups
                .Select((group, index) => (group, index))
                .Where(x => x.group.Candidates.Any(candidate => currentLeftKeys.Contains(candidate.Key)))
                .Select(x => x.index)
                .ToHashSet();

            foreach (var affinity in affinityGroups)
            {
                var affinityGroupIds = affinity.Meshes
                    .Where(planningGroupByMesh.ContainsKey)
                    .Select(mesh => planningGroupByMesh[mesh])
                    .Distinct()
                    .ToHashSet();

                if (affinityGroupIds.Count < 2 ||
                    !affinity.Meshes.Any(currentLeftKeys.Contains) ||
                    !affinity.Meshes.Any(currentRightKeys.Contains))
                {
                    continue;
                }

                var moveToLeft = currentLeftGroupIds
                    .Union(affinityGroupIds)
                    .ToHashSet();
                AddProposal(
                    planningGroups.Where((_, index) => moveToLeft.Contains(index)),
                    planningGroups.Where((_, index) => !moveToLeft.Contains(index)));

                var moveToRight = currentLeftGroupIds
                    .Except(affinityGroupIds)
                    .ToHashSet();
                AddProposal(
                    planningGroups.Where((_, index) => moveToRight.Contains(index)),
                    planningGroups.Where((_, index) => !moveToRight.Contains(index)));
            }

            // Reuse the existing physical-layout heuristics as additional candidates. The
            // acceptance rule below rejects any proposal that loses merge affinity.
            foreach (var proposal in CreateNonContiguousSplitProposals(combined))
                AddProposal(
                    proposal.Left
                        .GroupBy(GetAtlasPlanningSourceIdentity)
                        .Select(group => planningGroups.First(
                            planning => planning.Identity == group.Key)),
                    proposal.Right
                        .GroupBy(GetAtlasPlanningSourceIdentity)
                        .Select(group => planningGroups.First(
                            planning => planning.Identity == group.Key)));

            const int maxMergeAwareProposals = 48;
            return proposals.Count <= maxMergeAwareProposals
                ? proposals
                : proposals.Take(maxMergeAwareProposals).ToList();
        }

        private static IReadOnlyList<AtlasBatchSplitProposal> CreateNonContiguousSplitProposals(
            IReadOnlyList<AtlasCandidate> batch)
        {
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

            if (groups.Count < 4)
                return [];

            var proposals = new List<AtlasBatchSplitProposal>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void AddProposals(
                IEnumerable<AtlasPlanningCandidateGroup> orderedGroups,
                IReadOnlyList<int> requestedSplitIndices)
            {
                var ordered = orderedGroups.ToList();
                foreach (var requestedIndex in requestedSplitIndices)
                {
                    var splitIndex = Math.Clamp(requestedIndex, 1, ordered.Count - 1);
                    var leftGroups = ordered.Take(splitIndex).ToList();
                    var rightGroups = ordered.Skip(splitIndex).ToList();
                    var left = leftGroups.SelectMany(x => x.Candidates).ToList();
                    var right = rightGroups.SelectMany(x => x.Candidates).ToList();

                    // A one-candidate atlas has no consolidation benefit and the normal planner
                    // deliberately avoids creating it.
                    if (left.Count < 2 || right.Count < 2)
                        continue;

                    var signature = string.Join(
                        "",
                        leftGroups
                            .Select(x => BuildAtlasPlanningOrderKey(x.Representative))
                            .OrderBy(x => x, StringComparer.Ordinal));
                    if (!seen.Add(signature))
                        continue;

                    proposals.Add(new AtlasBatchSplitProposal(left, right));
                }
            }

            var groupCount = groups.Count;
            var broadSplitIndices = new[]
            {
                1,
                2,
                Math.Max(1, groupCount / 3),
                Math.Max(1, groupCount / 2),
                Math.Min(groupCount - 1, (groupCount * 2) / 3),
                Math.Max(1, groupCount - 2)
            };

            // First separate sources with very different per-channel resolution pressure. A
            // small number of 4x masks/normals can otherwise force the physical size of that
            // entire channel up for every placement in the batch.
            foreach (var channel in AtlasChannels)
            {
                var orderedByChannelPressure = groups
                    .OrderByDescending(x => GetChannelScalePressure(x.Representative, channel.Slot))
                    .ThenByDescending(x => (long)x.Crop.Width * x.Crop.Height)
                    .ThenBy(x => BuildAtlasPlanningOrderKey(x.Representative), StringComparer.Ordinal);
                AddProposals(
                    orderedByChannelPressure,
                    [1, 2, Math.Max(1, groupCount / 2), Math.Max(1, groupCount - 2)]);
            }

            // Wide and tall crops frequently combine into a max-size square atlas. Separating them can
            // turn one expensive 4096x4096 batch into two substantially cheaper rectangular atlases.
            AddProposals(
                groups
                    .OrderBy(x => GetCropAspectScore(x.Crop))
                    .ThenByDescending(x => (long)x.Crop.Width * x.Crop.Height)
                    .ThenBy(x => BuildAtlasPlanningOrderKey(x.Representative), StringComparer.Ordinal),
                broadSplitIndices);

            // Also try isolating the largest placements regardless of aspect. This catches mixed
            // size-class batches where a few large sources force a power-of-two jump.
            AddProposals(
                groups
                    .OrderByDescending(x => (long)x.Crop.Width * x.Crop.Height)
                    .ThenByDescending(x => Math.Max(x.Crop.Width, x.Crop.Height))
                    .ThenBy(x => BuildAtlasPlanningOrderKey(x.Representative), StringComparer.Ordinal),
                broadSplitIndices);

            const int maxNonContiguousSplitEvaluations = 32;
            return proposals.Count <= maxNonContiguousSplitEvaluations
                ? proposals
                : proposals.Take(maxNonContiguousSplitEvaluations).ToList();
        }

        private static double GetChannelScalePressure(AtlasCandidate candidate, string slot)
        {
            if (candidate.ConstantChannels.ContainsKey(slot))
                return 0;

            if (!candidate.ResolvedChannels.Contains(slot) ||
                !candidate.ChannelDimensions.TryGetValue(slot, out var dimensions))
            {
                return 0;
            }

            return Math.Max(
                dimensions.Width / (double)Math.Max(1, candidate.Width),
                dimensions.Height / (double)Math.Max(1, candidate.Height));
        }

        private static double GetCropAspectScore(AtlasCrop crop)
            => Math.Log2(
                Math.Max(1, crop.Width) /
                (double)Math.Max(1, crop.Height));

        private static bool TryGetGeneratedAtlasPixelCost(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates,
            out long pixelCost,
            out bool touchesMaxSize,
            bool deduplicateByContent = false)
        {
            pixelCost = 0;
            touchesMaxSize = false;

            try
            {
                var sharedPlan = CreateSharedAtlasPlan(
                    state,
                    candidates,
                    deduplicateByContent);
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
                    // ProcessBatch deliberately emits no texture when every present source
                    // for a channel is uniform/constant, except for a mask channel forced
                    // into an atlas to preserve canonicalized blue-mask semantics.
                    if (sourceDimensions.Count == 0 && !requiresConstantOnlyAtlas)
                        continue;

                    var outputDimensions = TextureAtlasBuilder.CalculateOutputDimensions(
                        sharedPlan.Plan,
                        sourceDimensions,
                        PackAtlasMaxSize);

                    pixelCost = checked(
                        pixelCost +
                        (long)outputDimensions.Width * outputDimensions.Height);

                    if (outputDimensions.Width == PackAtlasMaxSize ||
                        outputDimensions.Height == PackAtlasMaxSize)
                    {
                        touchesMaxSize = true;
                    }
                }

                return true;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or ArgumentException or OverflowException)
            {
                pixelCost = 0;
                touchesMaxSize = false;
                return false;
            }
        }

        private static AtlasPlanningSourceIdentity GetAtlasPlanningSourceIdentity(AtlasCandidate candidate)
            => new(
                BuildAtlasTextureSetIdentity(candidate),
                GetCanonicalCrop(candidate).Crop,
                candidate.AtlasResolutionScale);

        private static string BuildAtlasPlanningOrderKey(AtlasCandidate candidate)
        {
            var identity = BuildAtlasTextureSetIdentity(candidate);
            var prefix = string.Join(
                "\u001f",
                identity.SourceWidth,
                identity.SourceHeight,
                identity.BaseColour,
                identity.MaterialMap,
                identity.Normal,
                identity.Mask);

            try
            {
                var crop = GetCanonicalCrop(candidate).Crop;
                return string.Join(
                    "\u001f",
                    prefix,
                    crop.X,
                    crop.Y,
                    crop.Width,
                    crop.Height,
                    candidate.AtlasResolutionScale);
            }
            catch (OverflowException)
            {
                // CanCreatePlan records the real per-mesh error. Sorting must never turn a
                // safely-skippable bad candidate into a failure for the whole pack.
                return $"{prefix}\u001finvalid\u001f{candidate.Key}";
            }
        }

        private static bool CanCreatePlan(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates,
            out string error)
        {
            try
            {
                _ = CreateSharedAtlasPlan(state, candidates);
                error = string.Empty;
                return true;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or ArgumentException or OverflowException)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool RequiresConstantOnlyMaskAtlas(
            string slot,
            IReadOnlyList<SharedAtlasSource> sources)
            => slot.Equals("t_xml_mask", StringComparison.OrdinalIgnoreCase) &&
                sources.Any(source => source.Representative.RequiresMaskAtlas);

        private static (SharedAtlasBatch Batch, TextureAtlasPlan Plan) CreateSharedAtlasPlan(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates,
            bool deduplicateByContent = false)
        {
            var mergedBatch = BuildSharedAtlasBatch(
                state,
                candidates,
                mergeCompatibleCrops: true,
                deduplicateByContent);
            try
            {
                var mergedPlan = TextureAtlasBuilder.CreatePlan(
                    ToAtlasLayoutSources(mergedBatch.Sources),
                    TextureAtlasBuilder.DefaultPadding,
                    PackAtlasMaxSize);
                ValidateChannelAtlasDimensions(mergedBatch, mergedPlan);
                return (mergedBatch, mergedPlan);
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or ArgumentException or OverflowException)
            {
                var exactBatch = BuildSharedAtlasBatch(
                    state,
                    candidates,
                    mergeCompatibleCrops: false,
                    deduplicateByContent);
                var exactPlan = TextureAtlasBuilder.CreatePlan(
                    ToAtlasLayoutSources(exactBatch.Sources),
                    TextureAtlasBuilder.DefaultPadding,
                    PackAtlasMaxSize);
                ValidateChannelAtlasDimensions(exactBatch, exactPlan);
                return (exactBatch, exactPlan);
            }
        }

        private static void ValidateChannelAtlasDimensions(
            SharedAtlasBatch batch,
            TextureAtlasPlan plan)
        {
            foreach (var channel in AtlasChannels)
            {
                var sourceDimensions = new Dictionary<int, (int Width, int Height)>();
                foreach (var source in batch.Sources)
                {
                    if (source.Representative.ChannelDimensions.TryGetValue(
                            channel.Slot,
                            out var dimensions))
                    {
                        sourceDimensions[source.Id] = GetScaledAtlasDimensions(
                            source.Representative,
                            dimensions.Width,
                            dimensions.Height);
                    }
                }

                if (sourceDimensions.Count > 0)
                    _ = TextureAtlasBuilder.CalculateOutputDimensions(
                        plan,
                        sourceDimensions,
                        PackAtlasMaxSize);
            }
        }

        private void ProcessBatch(
            BatchState state,
            string atlasScopeKey,
            string progressScope,
            List<AtlasCandidate> candidates,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress,
            SharedAtlasPage? sharedPage)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SharedAtlasBatch sharedBatch;
            TextureAtlasPlan plan;
            Dictionary<string, string> generatedPaths;
            if (sharedPage != null)
            {
                sharedBatch = sharedPage.Batch;
                plan = sharedPage.Plan;
                generatedPaths = sharedPage.GeneratedPaths;
            }
            else
            {
                var sharedPlan = CreateSharedAtlasPlan(
                    state,
                    candidates,
                    deduplicateByContent: true);
                sharedBatch = sharedPlan.Batch;
                plan = sharedPlan.Plan;
                state.AtlasPlacementsGenerated += sharedBatch.Sources.Count;
                state.AtlasPlacementsReused += candidates.Count - sharedBatch.Sources.Count;
                state.WrappedUvPlacementsCanonicalized += sharedBatch.MappingByMesh.Values.Count(
                    x => x.WrappedUvCanonicalized);
                state.ContentDeduplicatedAtlasPlacements += sharedBatch.ContentPlacementsReused;
                state.ContentCanonicalizedMeshReferences += sharedBatch.MappingByMesh.Values.Count(
                    x => x.ContentCanonicalized);
                generatedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            foreach (var candidate in candidates)
            {
                if (candidate.UvIslandNormalization == null)
                    continue;

                var normalization = candidate.UvIslandNormalization;
                state.UvIslandNormalizedMeshes++;
                state.UvIslandsShifted += normalization.ShiftedIslandCount;
                state.UvIslandCropPixelsSaved = checked(
                    state.UvIslandCropPixelsSaved +
                    (long)normalization.OriginalCrop.Width * normalization.OriginalCrop.Height -
                    (long)normalization.NormalizedCrop.Width * normalization.NormalizedCrop.Height);
                state.UvIslandNormalizationEntries.Add(new UvIslandNormalizationReportEntry(
                    candidate.Key,
                    normalization.IslandCount,
                    normalization.ShiftedIslandCount,
                    normalization.OriginalCrop,
                    normalization.NormalizedCrop));
            }

            var atlasBatchId = state.BatchIndex++;
            if (!TryGetGeneratedAtlasPixelCost(
                    state,
                    candidates,
                    out var atlasBatchPixelCost,
                    out _,
                    deduplicateByContent: true))
                atlasBatchPixelCost = 0;

            state.AtlasBatchDiagnostics[atlasBatchId] = new AtlasBatchDiagnosticSnapshot(
                candidates.ToList(),
                atlasBatchPixelCost);
            foreach (var candidate in candidates)
            {
                state.AtlasBatchByMesh[candidate.Key] = atlasBatchId;
            }

            state.AtlasBatchCount++;
            var rootsByMesh = BuildCandidateRootVmdPaths(state, candidates);
            if (state.ShareAtlasesAcrossVmdsEnabled &&
                GetBatchRootCount(candidates, rootsByMesh) > 1)
            {
                state.CrossVmdSharedAtlasBatches++;
            }

            foreach (var sourceGroup in candidates.GroupBy(x => sharedBatch.MappingByMesh[x.Key].SourceId))
            {
                var sourceRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var candidate in sourceGroup)
                {
                    if (rootsByMesh.TryGetValue(candidate.Key, out var candidateRoots))
                        sourceRoots.UnionWith(candidateRoots);
                    else
                        sourceRoots.Add(Normalize(candidate.RootVmdPath));
                }

                if (sourceRoots.Count > 1)
                    state.CrossVmdSharedAtlasPlacements++;
            }

            if (sharedPage == null)
            {
                var atlasStem = BuildAtlasStem(atlasScopeKey, state.AtlasPageIndex++);

                for (var channelIndex = 0; channelIndex < AtlasChannels.Length; channelIndex++)
                {
                cancellationToken.ThrowIfCancellationRequested();
                var channel = AtlasChannels[channelIndex];
                ReportProgress(
                    progress,
                    "Building texture atlases",
                    channelIndex + 1,
                    AtlasChannels.Length,
                    $"{progressScope} — {channel.Suffix}");
                var textureBytes = new Dictionary<int, byte[]>();
                var textureBytesByPath = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                var constantSources = new Dictionary<int, TextureAtlasConstantColor>();
                var sourcePaths = new Dictionary<int, string>();
                var omitted = new HashSet<int>();

                foreach (var source in sharedBatch.Sources)
                {
                    var representative = source.Representative;
                    if (representative.ResolvedChannels.Contains(channel.Slot))
                    {
                        var sourcePath = GetTexturePath(representative.MaterialDocument, channel.Slot);
                        if (string.IsNullOrWhiteSpace(sourcePath))
                        {
                            throw new InvalidOperationException(
                                $"Resolved atlas channel {channel.Slot} has no texture path for {representative.Key}.");
                        }

                        sourcePath = Normalize(sourcePath);
                        if (!textureBytesByPath.TryGetValue(sourcePath, out var bytes))
                        {
                            var file = FindForRead(state, sourcePath)
                                ?? throw new InvalidOperationException(
                                    $"Resolved atlas texture no longer exists: {sourcePath}");
                            bytes = file.DataSource.ReadData();
                            textureBytesByPath[sourcePath] = bytes;
                        }

                        textureBytes[source.Id] = bytes;
                        sourcePaths[source.Id] = sourcePath;
                    }
                    else if (representative.ConstantChannels.TryGetValue(
                                 channel.Slot,
                                 out var constantColor))
                    {
                        constantSources[source.Id] = constantColor;
                    }
                    else
                    {
                        omitted.Add(source.Id);
                    }
                }

                var requiresConstantOnlyAtlas =
                    RequiresConstantOnlyMaskAtlas(channel.Slot, sharedBatch.Sources);
                if (textureBytes.Count == 0 && !requiresConstantOnlyAtlas)
                {
                    // UV remapping cannot change a uniform texture. If every present source
                    // for this channel is constant, keep each material's original shared
                    // constant texture path instead of allocating/compressing an atlas.
                    if (constantSources.Count != 0)
                        state.ConstantOnlyAtlasChannelsSkipped++;
                    continue;
                }

                var sourceDimensions = new Dictionary<int, (int Width, int Height)>();
                foreach (var source in sharedBatch.Sources)
                {
                    if (textureBytes.ContainsKey(source.Id) &&
                        source.Representative.ChannelDimensions.TryGetValue(
                            channel.Slot,
                            out var dimensions))
                    {
                        sourceDimensions[source.Id] = GetScaledAtlasDimensions(
                            source.Representative,
                            dimensions.Width,
                            dimensions.Height);
                    }
                }

                var outputDimensions = TextureAtlasBuilder.CalculateOutputDimensions(
                    plan,
                    sourceDimensions,
                    PackAtlasMaxSize);

                var atlasFormat = GetAtlasChannelFormat(candidates, channel.Slot, channel.Type);
                var fileName = $"{atlasStem}_{channel.Suffix}.dds";
                using var mipWriter = PngToDdsImporter.CreateRawBgraMipChainWriter(
                    outputDimensions.Width,
                    outputDimensions.Height,
                    TextureAtlasBuilder.CalculateMipLevelCount(
                        outputDimensions.Width,
                        outputDimensions.Height),
                    channel.Type,
                    GameTypeEnum.Warhammer3,
                    collectCompressionStatistics: AtlasProfilingEnabled,
                    ddsFormatOverride: atlasFormat);

                var rasterStatistics = AtlasProfilingEnabled
                    ? new TextureAtlasBuildStatistics()
                    : null;
                var rasterStopwatch = Stopwatch.StartNew();
                var clearBlueSourceIds = channel.Slot.Equals(
                        "t_xml_mask",
                        StringComparison.OrdinalIgnoreCase)
                    ? sharedBatch.Sources
                        .Where(source => source.Representative.ClearMaskBlueChannel)
                        .Select(source => source.Id)
                        .ToHashSet()
                    : null;
                _ = TextureAtlasBuilder.BuildMipPixels(
                    plan,
                    textureBytes,
                    forceOpaqueAlphaSourceIds: GetForceOpaqueAlphaSourceIds(
                        state,
                        atlasFormat,
                        sourcePaths,
                        textureBytes,
                        constantSources),
                    omittedSourceIds: omitted,
                    cancellationToken: cancellationToken,
                    heartbeat: () =>
                    {
                        ReportProgress(
                            progress,
                            "Building texture atlases",
                            channelIndex + 1,
                            AtlasChannels.Length,
                            $"{progressScope} — {channel.Suffix}");
                        cancellationToken.ThrowIfCancellationRequested();
                    },
                    constantSources: constantSources,
                    outputWidth: outputDimensions.Width,
                    outputHeight: outputDimensions.Height,
                    mipConsumer: (mipLevel, mipWidth, mipHeight, pixels) =>
                        mipWriter.WriteMip(mipLevel, pixels),
                    retainMipPixels: false,
                    statistics: rasterStatistics,
                    clearBlueSourceIds: clearBlueSourceIds);
                rasterStopwatch.Stop();
                var rasterElapsed = rasterStopwatch.Elapsed;
                AddPhaseDuration(state, "Rasterize atlas pixels", rasterElapsed);

                var usesLargeBcSplitCompression = mipWriter.UsesLargeBcSplitCompression;
                if (usesLargeBcSplitCompression)
                    state.LargeBcSplitCompressionChannels++;

                var compressionStopwatch = Stopwatch.StartNew();
                var atlasPackFile = mipWriter.Complete(fileName);
                compressionStopwatch.Stop();
                var compressionElapsed = compressionStopwatch.Elapsed;
                var compressionStatistics = mipWriter.LastCompressionStatistics;
                AddPhaseDuration(state, "Compress atlas DDS", compressionElapsed);
                var atlasPath = Normalize($@"{AtlasDirectory}\{fileName}");

                if (AtlasProfilingEnabled && rasterStatistics != null)
                {
                    state.GeneratedTextureTimings.Add(new GeneratedTextureTimingEntry(
                        atlasPath,
                        outputDimensions.Width,
                        outputDimensions.Height,
                        channel.Type,
                        atlasFormat,
                        rasterElapsed,
                        compressionElapsed,
                        usesLargeBcSplitCompression,
                        rasterStatistics,
                        compressionStatistics));
                }

                WriteFile(state.Output, atlasPath, atlasPackFile.DataSource.ReadData());
                generatedPaths[channel.Slot] = atlasPath;
                state.GeneratedTexturePaths.Add(atlasPath);
                state.GeneratedTextureDimensions[atlasPath] = outputDimensions;
                state.GeneratedTextureFormats[atlasPath] = new GeneratedTextureFormat(
                    channel.Slot,
                    atlasFormat,
                    atlasFormat == DirectXTexNet.DXGI_FORMAT.BC3_UNORM_SRGB &&
                    IsBc1AtlasTarget(channel.Type)
                        ? GetKeepAlphaReason(candidates, channel.Slot)
                        : null);
            }

            RecordAtlasPhysicalPageResidency(state, candidates, generatedPaths);
            state.AtlasPlacementDiagnostics.Add(
                BuildAtlasPlacementDiagnosticSnapshot(
                    atlasStem,
                    plan,
                    sharedBatch,
                    candidates,
                    generatedPaths));
            }

            var rewriteStopwatch = Stopwatch.StartNew();
            for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = candidates[candidateIndex];
                ReportProgress(
                    progress,
                    "Rewriting mesh UVs and materials",
                    candidateIndex + 1,
                    candidates.Count,
                    candidate.Key.ToString());
                var mapping = sharedBatch.MappingByMesh[candidate.Key];
                var placement = plan.Placements.Single(x => x.Id == mapping.SourceId);

                foreach (var vertexIndex in candidate.Model.Mesh.IndexList.Distinct())
                {
                    if (vertexIndex >= candidate.Model.Mesh.VertexList.Length)
                        throw new InvalidOperationException(
                            $"Mesh {candidate.Key} contains invalid vertex index {vertexIndex}.");

                    var uv = candidate.Model.Mesh.VertexList[vertexIndex].Uv;
                    var islandOffsetU = candidate.UvIslandNormalization?.TileOffsetUByVertex[vertexIndex] ?? 0;
                    var islandOffsetV = candidate.UvIslandNormalization?.TileOffsetVByVertex[vertexIndex] ?? 0;
                    var remapped = placement.TransformUv(
                        uv.X + islandOffsetU - mapping.UvOffsetU,
                        uv.Y + islandOffsetV - mapping.UvOffsetV,
                        plan.Width,
                        plan.Height);
                    candidate.Model.Mesh.VertexList[vertexIndex].Uv = new Microsoft.Xna.Framework.Vector2(remapped.U, remapped.V);
                }

                var clonedMaterial = new XmlDocument();
                clonedMaterial.LoadXml(candidate.MaterialDocument.OuterXml);

                var unresolvedMaskPath = GetTexturePath(
                    clonedMaterial,
                    "t_xml_mask");
                if (IsIgnorableUnresolvedAtlasTexture(
                        "t_xml_mask",
                        unresolvedMaskPath) &&
                    FindForRead(state, unresolvedMaskPath) == null)
                {
                    RemoveTextureSlot(clonedMaterial, "t_xml_mask");
                }

                foreach (var channel in AtlasChannels)
                {
                    if (!candidate.ResolvedChannels.Contains(channel.Slot) &&
                        !candidate.ConstantChannels.ContainsKey(channel.Slot))
                        continue;
                    if (generatedPaths.TryGetValue(channel.Slot, out var atlasPath))
                        SetTexturePath(clonedMaterial, channel.Slot, atlasPath);
                }

                // Only canonicalize constant channels that remain source-backed. If another
                // candidate caused this channel to receive an atlas, the generated atlas path
                // is already the stronger sharing identity.
                CanonicalizeUniformConstantTexturePaths(
                    state,
                    clonedMaterial,
                    candidate.ConstantChannels);

                var isEmbeddedMaterial = candidate.Usages.Any(
                    usage => !string.IsNullOrWhiteSpace(usage.EmbeddedRigidPath));
                string resolvedMaterialPath;
                if (isEmbeddedMaterial)
                {
                    foreach (var channel in AtlasChannels)
                    {
                        if (generatedPaths.TryGetValue(channel.Slot, out var atlasPath))
                        {
                            ApplyEmbeddedMaterialTexture(
                                candidate.Model.Material,
                                channel.Slot,
                                atlasPath);
                        }
                    }

                    resolvedMaterialPath = "<embedded-rigid-material>";
                }
                else
                {
                    var materialXml = clonedMaterial.OuterXml;
                    var renderingIdentity = GetMaterialRenderingIdentity(clonedMaterial);
                    var materialContentHash = ContentHash(renderingIdentity);
                    if (!state.GeneratedMaterialByContentHash.TryGetValue(
                            materialContentHash,
                            out var generatedMaterial))
                    {
                        var newMaterialPath = BuildMaterialPath(candidate.MaterialPath, candidate.Key);
                        WriteFile(state.Output, newMaterialPath, Encoding.UTF8.GetBytes(materialXml));
                        state.GeneratedMaterialPaths.Add(newMaterialPath);
                        generatedMaterial = new GeneratedMaterialEntry(
                            newMaterialPath,
                            renderingIdentity,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            {
                                candidate.RootVmdPath
                            });
                        state.GeneratedMaterialByContentHash.Add(materialContentHash, generatedMaterial);
                    }
                    else if (!generatedMaterial.RenderingIdentity.Equals(renderingIdentity, StringComparison.Ordinal))
                    {
                        // The hash is only an index. Exact rendering-identity equality remains
                        // the final guard so even a theoretical SHA-256 collision cannot share materials.
                        var newMaterialPath = BuildMaterialPath(candidate.MaterialPath, candidate.Key);
                        WriteFile(state.Output, newMaterialPath, Encoding.UTF8.GetBytes(materialXml));
                        state.GeneratedMaterialPaths.Add(newMaterialPath);
                        generatedMaterial = new GeneratedMaterialEntry(
                            newMaterialPath,
                            renderingIdentity,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            {
                                candidate.RootVmdPath
                            });
                    }
                    else
                    {
                        state.GeneratedMaterialReuses++;
                        if (!generatedMaterial.RootVmdPaths.Contains(candidate.RootVmdPath) &&
                            generatedMaterial.RootVmdPaths.Count != 0)
                        {
                            state.CrossVmdMaterialReuses++;
                        }

                        generatedMaterial.RootVmdPaths.Add(candidate.RootVmdPath);
                    }

                    resolvedMaterialPath = generatedMaterial.Path;
                }

                state.AtlasedMeshes.Add(new AtlasedMeshReportEntry(
                    candidate.RootVmdPath,
                    candidate.Key,
                    candidate.Usages.Select(x => x.AssetPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    candidate.MaterialPath,
                    resolvedMaterialPath,
                    generatedPaths.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));

                foreach (var usage in candidate.Usages)
                {
                    if (usage.MaterialNode != null &&
                        !string.IsNullOrWhiteSpace(usage.WsModelPath))
                    {
                        usage.MaterialNode.InnerText = resolvedMaterialPath;
                        state.ModifiedWsModels.Add(usage.WsModelPath);
                    }
                }

                state.ModifiedRigids.Add(candidate.Key.GeometryPath);
                state.AtlasChangedRigids.Add(Normalize(candidate.Key.GeometryPath));
                state.AtlasChangedParts.Add((
                    Normalize(candidate.Key.GeometryPath),
                    candidate.Key.LodIndex,
                    candidate.Key.PartIndex));
                state.ProcessedMeshes.Add(candidate.Key);
            }
            AddPhaseDuration(state, "Rewrite mesh UVs and materials", rewriteStopwatch.Elapsed);
        }

        private static List<AtlasCandidate> AlignSharedUvIslandCuts(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates)
        {
            var replacements = new Dictionary<MeshKey, AtlasCandidate>();

            foreach (var group in candidates.GroupBy(BuildAtlasTextureSetIdentity))
            {
                var members = group.ToList();
                if (members.Count < 2)
                    continue;

                var sharedCutU = ChooseSharedUvIslandCut(members, useU: true);
                var sharedCutV = ChooseSharedUvIslandCut(members, useU: false);
                var groupChanged = false;

                foreach (var candidate in members)
                {
                    var proposal = TextureAtlasBuilder.RecalculateDisconnectedUvIslandNormalization(
                        candidate.UvIslandAnalysis,
                        sharedCutU,
                        sharedCutV);
                    var originalBounds = new UvBounds(
                        proposal.OriginalMinU,
                        proposal.OriginalMinV,
                        proposal.OriginalMaxU,
                        proposal.OriginalMaxV);
                    var normalization = BuildUvIslandNormalization(
                        candidate.Width,
                        candidate.Height,
                        originalBounds,
                        proposal,
                        allowEqualCrop: true);

                    if (normalization == null)
                        continue;

                    if (candidate.UvIslandNormalization != null &&
                        normalization.NormalizedCrop == candidate.UvIslandNormalization.NormalizedCrop &&
                        proposal.CutU == candidate.UvIslandAnalysis.CutU &&
                        proposal.CutV == candidate.UvIslandAnalysis.CutV)
                    {
                        continue;
                    }

                    replacements[candidate.Key] = candidate with
                    {
                        Bounds = normalization.NormalizedBounds,
                        UvIslandAnalysis = proposal,
                        UvIslandNormalization = normalization
                    };
                    groupChanged = true;
                }

                if (groupChanged)
                    state.SharedUvIslandCutGroups++;
            }

            return candidates
                .Select(candidate => replacements.TryGetValue(candidate.Key, out var replacement)
                    ? replacement
                    : candidate)
                .ToList();
        }

        private static double ChooseSharedUvIslandCut(
            IReadOnlyList<AtlasCandidate> candidates,
            bool useU)
        {
            var candidateCuts = candidates
                .Select(candidate => useU
                    ? candidate.UvIslandAnalysis.CutU
                    : candidate.UvIslandAnalysis.CutV)
                .Append(0)
                .Distinct()
                .OrderBy(x => x)
                .ToArray();

            var sourceSize = useU ? candidates[0].Width : candidates[0].Height;
            var bestCut = candidateCuts[0];
            long bestUnionPixels = long.MaxValue;
            long bestTotalPixels = long.MaxValue;

            foreach (var cut in candidateCuts)
            {
                var unionMin = double.PositiveInfinity;
                var unionMax = double.NegativeInfinity;
                long totalPixels = 0;

                foreach (var candidate in candidates)
                {
                    var bounds = TextureAtlasBuilder.CalculateDisconnectedUvIslandAxisBoundsForCut(
                        candidate.UvIslandAnalysis,
                        cut,
                        useU);

                    // Express each candidate in the same unwrapped period for this seam.
                    // Whole-mesh integer translations are sampling-equivalent and later become
                    // the existing wrapped-UV mapping offset.
                    var wholeTile = Math.Floor(bounds.Min - cut);
                    var min = bounds.Min - wholeTile;
                    var max = bounds.Max - wholeTile;

                    unionMin = Math.Min(unionMin, min);
                    unionMax = Math.Max(unionMax, max);

                    var pixelMin = checked((int)Math.Floor(min * sourceSize));
                    var pixelMax = checked((int)Math.Ceiling(max * sourceSize));
                    totalPixels = checked(totalPixels + Math.Max(1, pixelMax - pixelMin));
                }

                var unionPixelMin = checked((int)Math.Floor(unionMin * sourceSize));
                var unionPixelMax = checked((int)Math.Ceiling(unionMax * sourceSize));
                var unionPixels = Math.Max(1L, (long)unionPixelMax - unionPixelMin);

                if (unionPixels < bestUnionPixels ||
                    (unionPixels == bestUnionPixels && totalPixels < bestTotalPixels))
                {
                    bestUnionPixels = unionPixels;
                    bestTotalPixels = totalPixels;
                    bestCut = cut;
                }
            }

            return bestCut;
        }

        private static UvIslandNormalization? BuildUvIslandNormalization(
            int sourceWidth,
            int sourceHeight,
            UvBounds originalBounds,
            TextureAtlasUvIslandNormalization proposal,
            bool allowEqualCrop)
        {
            var proposedBounds = new UvBounds(
                proposal.NormalizedMinU,
                proposal.NormalizedMinV,
                proposal.NormalizedMaxU,
                proposal.NormalizedMaxV);
            var originalCrop = GetEffectiveCrop(originalBounds, sourceWidth, sourceHeight);
            var proposedCrop = GetEffectiveCrop(proposedBounds, sourceWidth, sourceHeight);

            var hasUShift = proposal.TileOffsetUByVertex.Any(x => x != 0);
            var hasVShift = proposal.TileOffsetVByVertex.Any(x => x != 0);
            var useU =
                proposedCrop.Width < originalCrop.Width ||
                (allowEqualCrop && hasUShift && proposedCrop.Width == originalCrop.Width);
            var useV =
                proposedCrop.Height < originalCrop.Height ||
                (allowEqualCrop && hasVShift && proposedCrop.Height == originalCrop.Height);
            if (!useU && !useV)
                return null;

            var tileOffsetUByVertex = useU
                ? proposal.TileOffsetUByVertex
                : new int[proposal.TileOffsetUByVertex.Length];
            var tileOffsetVByVertex = useV
                ? proposal.TileOffsetVByVertex
                : new int[proposal.TileOffsetVByVertex.Length];

            var normalizedBounds = new UvBounds(
                useU ? proposedBounds.MinU : originalBounds.MinU,
                useV ? proposedBounds.MinV : originalBounds.MinV,
                useU ? proposedBounds.MaxU : originalBounds.MaxU,
                useV ? proposedBounds.MaxV : originalBounds.MaxV);
            var normalizedCrop = GetEffectiveCrop(normalizedBounds, sourceWidth, sourceHeight);

            var shiftedIslandIds = new HashSet<int>();
            for (var vertexIndex = 0; vertexIndex < proposal.IslandIdByVertex.Length; vertexIndex++)
            {
                if (tileOffsetUByVertex[vertexIndex] == 0 &&
                    tileOffsetVByVertex[vertexIndex] == 0)
                    continue;

                var islandId = proposal.IslandIdByVertex[vertexIndex];
                if (islandId >= 0)
                    shiftedIslandIds.Add(islandId);
            }

            if (shiftedIslandIds.Count == 0)
                return null;

            return new UvIslandNormalization(
                proposal.ComponentCount,
                shiftedIslandIds.Count,
                tileOffsetUByVertex,
                tileOffsetVByVertex,
                originalBounds,
                normalizedBounds,
                originalCrop,
                normalizedCrop);
        }

        private static string FormatDiagnosticTexturePath(string path)
            => string.IsNullOrWhiteSpace(path) ? "<none>" : path;

        private static void AppendPlacementRelation(
            StringBuilder sb,
            string label,
            IReadOnlyList<int> sourceIds)
        {
            if (sourceIds.Count != 0)
                sb.AppendLine($"    {label}: {string.Join(", ", sourceIds)}");
        }

        private static AtlasPlacementBatchDiagnostic BuildAtlasPlacementDiagnosticSnapshot(
            string atlasStem,
            TextureAtlasPlan plan,
            SharedAtlasBatch sharedBatch,
            IReadOnlyList<AtlasCandidate> candidates,
            IReadOnlyDictionary<string, string> generatedPaths)
        {
            var placements = new List<AtlasPlacementDiagnostic>(sharedBatch.Sources.Count);
            var candidatesByKey = candidates.ToDictionary(x => x.Key);

            foreach (var source in sharedBatch.Sources.OrderBy(x => x.Id))
            {
                var representative = source.Representative;
                var placement = plan.Placements.Single(x => x.Id == source.Id);
                var fullTextureSet = BuildAtlasTextureSetIdentity(representative);
                var baseColourPath = Normalize(
                    GetTexturePath(representative.MaterialDocument, "t_xml_base_colour"));
                var materialMapPath = Normalize(
                    GetTexturePath(representative.MaterialDocument, "t_xml_material_map"));
                var normalPath = Normalize(
                    GetTexturePath(representative.MaterialDocument, "t_xml_normal"));
                var maskPath = Normalize(
                    GetTexturePath(representative.MaterialDocument, "t_xml_mask"));

                var sameBaseColourRegion = new List<int>();
                var baseColourContains = new List<int>();
                var baseColourContainedBy = new List<int>();
                var fullSetContains = new List<int>();
                var fullSetContainedBy = new List<int>();

                foreach (var other in sharedBatch.Sources)
                {
                    if (other.Id == source.Id)
                        continue;

                    var sameBaseColour =
                        !string.IsNullOrWhiteSpace(baseColourPath) &&
                        baseColourPath.Equals(
                            Normalize(GetTexturePath(
                                other.Representative.MaterialDocument,
                                "t_xml_base_colour")),
                            StringComparison.OrdinalIgnoreCase) &&
                        representative.Width == other.Representative.Width &&
                        representative.Height == other.Representative.Height;

                    if (sameBaseColour)
                    {
                        if (source.Crop == other.Crop)
                            sameBaseColourRegion.Add(other.Id);
                        else if (ContainsCrop(source.Crop, other.Crop))
                            baseColourContains.Add(other.Id);
                        else if (ContainsCrop(other.Crop, source.Crop))
                            baseColourContainedBy.Add(other.Id);
                    }

                    if (fullTextureSet == BuildAtlasTextureSetIdentity(other.Representative))
                    {
                        if (ContainsCrop(source.Crop, other.Crop) && source.Crop != other.Crop)
                            fullSetContains.Add(other.Id);
                        else if (ContainsCrop(other.Crop, source.Crop) && source.Crop != other.Crop)
                            fullSetContainedBy.Add(other.Id);
                    }
                }

                var meshMappings = sharedBatch.MappingByMesh
                    .Where(x => x.Value.SourceId == source.Id)
                    .OrderBy(x => x.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Key.LodIndex)
                    .ThenBy(x => x.Key.PartIndex)
                    .Select(x =>
                    {
                        var candidate = candidatesByKey[x.Key];
                        return new AtlasPlacementMeshDiagnostic(
                            x.Key,
                            candidate.RootVmdPath,
                            x.Value.UvOffsetU,
                            x.Value.UvOffsetV,
                            candidate.UvIslandNormalization != null,
                            x.Value.WrappedUvCanonicalized,
                            x.Value.ContentCanonicalized);
                    })
                    .ToList();

                placements.Add(new AtlasPlacementDiagnostic(
                    source.Id,
                    representative.Width,
                    representative.Height,
                    source.Crop,
                    placement.DestinationX,
                    placement.DestinationY,
                    placement.CropWidth,
                    placement.CropHeight,
                    placement.Padding,
                    representative.Key,
                    representative.MaterialPath,
                    baseColourPath,
                    materialMapPath,
                    normalPath,
                    maskPath,
                    sameBaseColourRegion,
                    baseColourContains,
                    baseColourContainedBy,
                    fullSetContains,
                    fullSetContainedBy,
                    meshMappings));
            }

            return new AtlasPlacementBatchDiagnostic(
                atlasStem,
                plan.Width,
                plan.Height,
                generatedPaths.Values
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                placements);
        }

        private static bool ContainsCrop(AtlasCrop outer, AtlasCrop inner)
        {
            var outerRight = checked((long)outer.X + outer.Width);
            var outerBottom = checked((long)outer.Y + outer.Height);
            var innerRight = checked((long)inner.X + inner.Width);
            var innerBottom = checked((long)inner.Y + inner.Height);

            return inner.X >= outer.X &&
                   inner.Y >= outer.Y &&
                   innerRight <= outerRight &&
                   innerBottom <= outerBottom;
        }

        private static UvBounds GetUvBounds(RmvModel model)
        {
            if (model.Mesh.IndexList.Length == 0)
                throw new InvalidOperationException("Cannot atlas a mesh with no triangles.");

            var minU = float.MaxValue;
            var minV = float.MaxValue;
            var maxU = float.MinValue;
            var maxV = float.MinValue;

            foreach (var vertexIndex in model.Mesh.IndexList.Distinct())
            {
                if (vertexIndex >= model.Mesh.VertexList.Length)
                    throw new InvalidOperationException("Rigid mesh contains an invalid vertex index.");

                var uv = model.Mesh.VertexList[vertexIndex].Uv;
                minU = Math.Min(minU, uv.X);
                minV = Math.Min(minV, uv.Y);
                maxU = Math.Max(maxU, uv.X);
                maxV = Math.Max(maxV, uv.Y);
            }

            return new UvBounds(minU, minV, maxU, maxV);
        }

        private static SharedAtlasBatch BuildSharedAtlasBatch(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates,
            bool mergeCompatibleCrops,
            bool deduplicateByContent)
        {
            var sources = new List<SharedAtlasSource>();
            var mappingByMesh = new Dictionary<MeshKey, SharedAtlasMeshMapping>();

            foreach (var group in candidates.GroupBy(BuildAtlasTextureSetIdentity))
            {
                var canonicalCrops = group.ToDictionary(
                    candidate => candidate.Key,
                    GetCanonicalCrop);
                var clusters = group
                    .GroupBy(candidate => canonicalCrops[candidate.Key].Crop)
                    .Select(cropGroup => new AtlasCropCluster(
                        cropGroup.Key,
                        cropGroup.ToList()))
                    .ToList();

                // Different LODs commonly use the exact same texture set but touch slightly
                // different UV extents. Requiring an identical crop duplicates most of the
                // texture in the atlas. Merge crops whenever their union costs less atlas area
                // (including padding) than storing them separately.
                //
                // Crops that differ only by an integer wrapped-UV tile translation are first
                // canonicalized into the same source-texture period. For example [0..1] and
                // [1..2] share one atlas placement instead of copying the same texels twice.
                while (mergeCompatibleCrops &&
                       TryFindBestCropMerge(clusters, out var leftIndex, out var rightIndex, out var union))
                {
                    var left = clusters[leftIndex];
                    var right = clusters[rightIndex];
                    var members = left.Members.Concat(right.Members).ToList();

                    if (rightIndex > leftIndex)
                    {
                        clusters.RemoveAt(rightIndex);
                        clusters.RemoveAt(leftIndex);
                    }
                    else
                    {
                        clusters.RemoveAt(leftIndex);
                        clusters.RemoveAt(rightIndex);
                    }

                    clusters.Add(new AtlasCropCluster(union, members));
                }

                foreach (var cluster in clusters)
                {
                    var representative = cluster.Members
                        .OrderByDescending(member => member.AtlasResolutionScale)
                        .ThenBy(member => member.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(member => member.Key.LodIndex)
                        .ThenBy(member => member.Key.PartIndex)
                        .First();
                    var source = new SharedAtlasSource(
                        sources.Count,
                        representative,
                        cluster.Crop);
                    sources.Add(source);

                    foreach (var candidate in cluster.Members)
                    {
                        var canonical = canonicalCrops[candidate.Key];
                        var wrapped =
                            canonical.TileOffsetU != 0 ||
                            canonical.TileOffsetV != 0;
                        mappingByMesh[candidate.Key] = new SharedAtlasMeshMapping(
                            source.Id,
                            canonical.TileOffsetU,
                            canonical.TileOffsetV,
                            wrapped,
                            ContentCanonicalized: false);
                    }
                }
            }

            var structuralSourceCount = sources.Count;
            if (deduplicateByContent)
                DeduplicateAtlasSourcesByContent(state, sources, mappingByMesh);

            return new SharedAtlasBatch(
                sources,
                mappingByMesh,
                structuralSourceCount - sources.Count);
        }

        private static void DeduplicateAtlasSourcesByContent(
            BatchState state,
            List<SharedAtlasSource> sources,
            Dictionary<MeshKey, SharedAtlasMeshMapping> mappingByMesh)
        {
            var removedSourceIds = new HashSet<int>();

            foreach (var compatibilityGroup in sources.GroupBy(BuildAtlasContentCompatibilityKey))
            {
                var compatibleSources = compatibilityGroup.ToList();
                if (compatibleSources.Count < 2)
                    continue;

                foreach (var contentGroup in compatibleSources.GroupBy(
                             source => GetAtlasContentIdentity(state, source)))
                {
                    var identicalSources = contentGroup
                        .OrderBy(x => x.Id)
                        .ToList();
                    if (identicalSources.Count < 2)
                        continue;

                    var canonical = identicalSources
                        .OrderByDescending(source => source.Representative.AtlasResolutionScale)
                        .ThenBy(source => source.Id)
                        .First();
                    foreach (var duplicate in identicalSources.Where(source => source.Id != canonical.Id))
                    {
                        removedSourceIds.Add(duplicate.Id);

                        var uvDeltaU =
                            (duplicate.Crop.X - canonical.Crop.X) /
                            (float)duplicate.Representative.Width;
                        var uvDeltaV =
                            (duplicate.Crop.Y - canonical.Crop.Y) /
                            (float)duplicate.Representative.Height;

                        var affectedMeshes = mappingByMesh
                            .Where(x => x.Value.SourceId == duplicate.Id)
                            .Select(x => x.Key)
                            .ToList();
                        foreach (var meshKey in affectedMeshes)
                        {
                            var mapping = mappingByMesh[meshKey];
                            mappingByMesh[meshKey] = mapping with
                            {
                                SourceId = canonical.Id,
                                UvOffsetU = mapping.UvOffsetU + uvDeltaU,
                                UvOffsetV = mapping.UvOffsetV + uvDeltaV,
                                ContentCanonicalized = true
                            };
                        }
                    }
                }
            }

            if (removedSourceIds.Count != 0)
                sources.RemoveAll(x => removedSourceIds.Contains(x.Id));
        }

        private static AtlasContentCompatibilityKey BuildAtlasContentCompatibilityKey(
            SharedAtlasSource source)
        {
            var candidate = source.Representative;
            return new AtlasContentCompatibilityKey(
                candidate.Width,
                candidate.Height,
                source.Crop.Width,
                source.Crop.Height,
                GetChannelContentCompatibility(candidate, "t_xml_base_colour"),
                GetChannelContentCompatibility(candidate, "t_xml_material_map"),
                GetChannelContentCompatibility(candidate, "t_xml_normal"),
                GetChannelContentCompatibility(candidate, "t_xml_mask"));
        }

        private static string GetChannelContentCompatibility(
            AtlasCandidate candidate,
            string slot)
        {
            if (candidate.ConstantChannels.TryGetValue(slot, out var constant))
            {
                return $"constant:{constant.B:X2}{constant.G:X2}{constant.R:X2}{constant.A:X2}";
            }

            if (candidate.ResolvedChannels.Contains(slot) &&
                candidate.ChannelDimensions.TryGetValue(slot, out var dimensions))
            {
                var clearBlue = slot.Equals("t_xml_mask", StringComparison.OrdinalIgnoreCase) &&
                                candidate.ClearMaskBlueChannel;
                return $"resolved{(clearBlue ? "-clear-blue" : string.Empty)}:" +
                       $"{dimensions.Width}x{dimensions.Height}";
            }

            return "omitted";
        }

        private static AtlasContentIdentity GetAtlasContentIdentity(
            BatchState state,
            SharedAtlasSource source)
        {
            var candidate = source.Representative;
            return new AtlasContentIdentity(
                GetChannelContentIdentity(state, candidate, source.Crop, "t_xml_base_colour"),
                GetChannelContentIdentity(state, candidate, source.Crop, "t_xml_material_map"),
                GetChannelContentIdentity(state, candidate, source.Crop, "t_xml_normal"),
                GetChannelContentIdentity(state, candidate, source.Crop, "t_xml_mask"));
        }

        private static string GetChannelContentIdentity(
            BatchState state,
            AtlasCandidate candidate,
            AtlasCrop crop,
            string slot)
        {
            if (candidate.ConstantChannels.TryGetValue(slot, out var constant))
            {
                return $"constant:{constant.B:X2}{constant.G:X2}{constant.R:X2}{constant.A:X2}";
            }

            if (!candidate.ResolvedChannels.Contains(slot))
                return "omitted";

            var texturePath = Normalize(GetTexturePath(candidate.MaterialDocument, slot));
            if (string.IsNullOrWhiteSpace(texturePath))
            {
                throw new InvalidOperationException(
                    $"Resolved atlas channel {slot} has no texture path for {candidate.Key}.");
            }

            var cacheKey = new AtlasRegionContentHashKey(
                texturePath,
                candidate.Width,
                candidate.Height,
                crop);
            if (!state.AtlasRegionContentHashes.TryGetValue(cacheKey, out var contentHash))
            {
                var file = FindForReadStatic(state, texturePath)
                    ?? throw new InvalidOperationException(
                        $"Resolved atlas texture no longer exists: {texturePath}");
                var bytes = file.DataSource.ReadData();
                contentHash = TextureAtlasBuilder.ComputeWrappedRegionContentHash(
                    bytes,
                    candidate.Width,
                    candidate.Height,
                    crop.X,
                    crop.Y,
                    crop.Width,
                    crop.Height);
                state.AtlasRegionContentHashes[cacheKey] = contentHash;
            }

            var clearBlue = slot.Equals("t_xml_mask", StringComparison.OrdinalIgnoreCase) &&
                            candidate.ClearMaskBlueChannel;
            return $"resolved{(clearBlue ? "-clear-blue" : string.Empty)}:{contentHash}";
        }

        private static AtlasTextureSetIdentity BuildAtlasTextureSetIdentity(AtlasCandidate candidate)
            => new(
                candidate.Width,
                candidate.Height,
                GetChannelIdentity(candidate, "t_xml_base_colour"),
                GetChannelIdentity(candidate, "t_xml_material_map"),
                GetChannelIdentity(candidate, "t_xml_normal"),
                GetChannelIdentity(candidate, "t_xml_mask"));

        private static bool TryFindBestCropMerge(
            IReadOnlyList<AtlasCropCluster> clusters,
            out int leftIndex,
            out int rightIndex,
            out AtlasCrop union)
        {
            leftIndex = -1;
            rightIndex = -1;
            union = default;
            long bestSavings = 0;

            for (var i = 0; i < clusters.Count; i++)
            {
                for (var j = i + 1; j < clusters.Count; j++)
                {
                    var leftScale = clusters[i].Members.Max(member => member.AtlasResolutionScale);
                    var rightScale = clusters[j].Members.Max(member => member.AtlasResolutionScale);
                    if (Math.Abs(leftScale - rightScale) > 0.000001)
                        continue;

                    var candidateUnion = Union(clusters[i].Crop, clusters[j].Crop);
                    var separateArea =
                        GetPaddedCropArea(clusters[i].Crop, leftScale) +
                        GetPaddedCropArea(clusters[j].Crop, rightScale);
                    var mergedArea = GetPaddedCropArea(candidateUnion, leftScale);
                    var savings = separateArea - mergedArea;

                    if (savings <= bestSavings)
                        continue;

                    bestSavings = savings;
                    leftIndex = i;
                    rightIndex = j;
                    union = candidateUnion;
                }
            }

            return leftIndex >= 0;
        }

        private static AtlasCrop Union(AtlasCrop left, AtlasCrop right)
        {
            var x = Math.Min(left.X, right.X);
            var y = Math.Min(left.Y, right.Y);
            var rightEdge = Math.Max(
                checked(left.X + left.Width),
                checked(right.X + right.Width));
            var bottomEdge = Math.Max(
                checked(left.Y + left.Height),
                checked(right.Y + right.Height));

            return new AtlasCrop(
                x,
                y,
                checked(rightEdge - x),
                checked(bottomEdge - y));
        }

        private static long GetPaddedCropArea(
            AtlasCrop crop,
            double resolutionScale = 1.0)
        {
            if (!double.IsFinite(resolutionScale) ||
                resolutionScale <= 0 ||
                resolutionScale > 1.0)
            {
                throw new ArgumentOutOfRangeException(nameof(resolutionScale));
            }

            var scaledWidth = Math.Max(
                1L,
                checked((long)Math.Ceiling(crop.Width * resolutionScale)));
            var scaledHeight = Math.Max(
                1L,
                checked((long)Math.Ceiling(crop.Height * resolutionScale)));
            var width = checked(scaledWidth + TextureAtlasBuilder.DefaultPadding * 2L);
            var height = checked(scaledHeight + TextureAtlasBuilder.DefaultPadding * 2L);
            return checked(width * height);
        }

        private static string GetChannelIdentity(AtlasCandidate candidate, string slot)
        {
            var path = GetTexturePath(candidate.MaterialDocument, slot);
            if (string.IsNullOrWhiteSpace(path) ||
                IsCandidateIgnoredTextureSlot(candidate, slot))
            {
                return "<none>";
            }
            if (IsTexturePlaceholder(path))
                return $"<placeholder>:{path}";
            if (candidate.ConstantChannels.ContainsKey(slot))
            {
                var constant = candidate.ConstantChannels[slot];
                return $"<constant>:{path}:{constant.B:X2}{constant.G:X2}{constant.R:X2}{constant.A:X2}";
            }
            if (candidate.ResolvedChannels.Contains(slot))
            {
                if (slot.Equals("t_xml_mask", StringComparison.OrdinalIgnoreCase) &&
                    candidate.ClearMaskBlueChannel)
                {
                    return $"<resolved-clear-blue>:{path}";
                }

                return $"<resolved>:{path}";
            }
            return $"<unresolved>:{path}";
        }

        private static bool IsCandidateIgnoredTextureSlot(
            AtlasCandidate candidate,
            string slot)
        {
            if (candidate.ResolvedChannels.Contains(slot) ||
                candidate.ConstantChannels.ContainsKey(slot) ||
                candidate.MissingTextures.Any(missing =>
                    missing.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return IsIgnorableUnresolvedAtlasTexture(
                slot,
                GetTexturePath(candidate.MaterialDocument, slot));
        }

        private static AtlasCrop GetEffectiveCrop(AtlasCandidate candidate)
            => GetEffectiveCrop(candidate.Bounds, candidate.Width, candidate.Height);

        private static AtlasCrop GetEffectiveCrop(
            UvBounds bounds,
            int sourceWidth,
            int sourceHeight)
        {
            var cropX = checked((int)MathF.Floor(bounds.MinU * sourceWidth));
            var cropY = checked((int)MathF.Floor(bounds.MinV * sourceHeight));
            var cropRight = checked((int)MathF.Ceiling(bounds.MaxU * sourceWidth));
            var cropBottom = checked((int)MathF.Ceiling(bounds.MaxV * sourceHeight));
            var cropWidth = checked(cropRight - cropX);
            var cropHeight = checked(cropBottom - cropY);
            if (cropWidth <= 0) cropWidth = 1;
            if (cropHeight <= 0) cropHeight = 1;
            return new AtlasCrop(cropX, cropY, cropWidth, cropHeight);
        }

        private static CanonicalAtlasCrop GetCanonicalCrop(AtlasCandidate candidate)
        {
            var crop = GetEffectiveCrop(candidate);

            // A crop wider/taller than one source period represents real repeated tiling across
            // a triangle span. Collapsing it would require splitting geometry at wrap seams, so
            // keep the existing virtual crop in that case.
            if (crop.Width > candidate.Width || crop.Height > candidate.Height)
                return new CanonicalAtlasCrop(crop, 0, 0);

            var canonicalX = PositiveModulo(crop.X, candidate.Width);
            var canonicalY = PositiveModulo(crop.Y, candidate.Height);
            var tileOffsetU = checked((crop.X - canonicalX) / candidate.Width);
            var tileOffsetV = checked((crop.Y - canonicalY) / candidate.Height);

            return new CanonicalAtlasCrop(
                new AtlasCrop(canonicalX, canonicalY, crop.Width, crop.Height),
                tileOffsetU,
                tileOffsetV);
        }

        private static int PositiveModulo(int value, int modulus)
        {
            if (modulus <= 0)
                throw new ArgumentOutOfRangeException(nameof(modulus));

            var result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        private static List<TextureAtlasLayoutSource> ToAtlasLayoutSources(
            IReadOnlyList<SharedAtlasSource> sharedSources)
        {
            var sources = new List<TextureAtlasLayoutSource>(sharedSources.Count);
            foreach (var source in sharedSources)
            {
                var candidate = source.Representative;
                var layoutDimensions = GetScaledAtlasDimensions(
                    candidate,
                    candidate.Width,
                    candidate.Height);
                sources.Add(new TextureAtlasLayoutSource(
                    source.Id,
                    layoutDimensions.Width,
                    layoutDimensions.Height,
                    source.Crop.X / (float)candidate.Width,
                    source.Crop.Y / (float)candidate.Height,
                    checked(source.Crop.X + source.Crop.Width) / (float)candidate.Width,
                    checked(source.Crop.Y + source.Crop.Height) / (float)candidate.Height));
            }

            return sources;
        }
    }
}
