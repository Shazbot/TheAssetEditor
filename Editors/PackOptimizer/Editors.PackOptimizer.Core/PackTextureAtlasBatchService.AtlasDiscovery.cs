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
        private void AnalyzeCompatibleMeshMergesForRigid(
            BatchState state,
            string rigidPath,
            CancellationToken cancellationToken)
        {
            if (!state.RigidModels.TryGetValue(rigidPath, out var rmv))
                return;

            var wsModels = GetWritableWsModelsForRigid(state, rigidPath);
            if (wsModels.Count == 0)
            {
                for (var lodIndex = 0; lodIndex < rmv.ModelList.Length; lodIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var groups = BuildDirectMeshMergeGroups(
                        rmv.ModelList[lodIndex],
                        lodIndex,
                        rigidPath);
                    RecordPreAtlasStructuralMergeGroups(
                        state,
                        rigidPath,
                        lodIndex,
                        groups);
                }

                return;
            }

            var assignmentsByWsModel = new Dictionary<string, string[][]>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var (wsPath, wsDocument) in wsModels)
            {
                if (!TryReadWsMaterialAssignments(
                        wsDocument,
                        rmv,
                        out var assignments,
                        out var reason))
                {
                    RecordStructuralMergeSkip(state, rigidPath, $"{wsPath}: {reason}");
                    return;
                }

                assignmentsByWsModel[wsPath] = assignments;
            }

            var wsModelPaths = wsModels
                .Select(x => x.Key)
                .ToList();
            for (var lodIndex = 0; lodIndex < rmv.ModelList.Length; lodIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var models = rmv.ModelList[lodIndex];
                if (models.Length < 2)
                    continue;

                var groups = BuildMeshMergeGroups(
                    state,
                    models,
                    lodIndex,
                    rigidPath,
                    wsModelPaths,
                    assignmentsByWsModel,
                    recordDiagnostics: false,
                    includeEmbeddedMaterialIdentity:
                        HasDirectGameplayRigidConsumer(state, rigidPath));

                RecordPreAtlasStructuralMergeGroups(
                    state,
                    rigidPath,
                    lodIndex,
                    groups);
            }
        }

        private static void RecordPreAtlasStructuralMergeGroups(
            BatchState state,
            string rigidPath,
            int lodIndex,
            IReadOnlyList<MeshMergeGroup> groups)
        {
            foreach (var group in groups.Where(x => x.PartIndices.Count > 1))
            {
                state.PreAtlasStructuralMergeGroupCount++;
                var componentMembers = group.PartIndices
                    .Select(partIndex => new MeshKey(rigidPath, lodIndex, partIndex))
                    .ToHashSet();
                var componentSignature = string.Join(
                    "\u001f",
                    componentMembers
                        .OrderBy(member => member.PartIndex)
                        .Select(member =>
                            BuildStructuralMergePairKey(
                                member.GeometryPath,
                                member.LodIndex,
                                member.PartIndex,
                                member.PartIndex)));
                if (state.PreAtlasStructuralMergeComponentSignatures.Add(componentSignature))
                    state.PreAtlasStructuralMergeComponents.Add(componentMembers);

                foreach (var partIndex in group.PartIndices)
                {
                    state.PreAtlasStructuralMergeMeshes.Add(
                        new MeshKey(rigidPath, lodIndex, partIndex));
                }

                for (var left = 0; left < group.PartIndices.Count; left++)
                {
                    for (var right = left + 1;
                         right < group.PartIndices.Count;
                         right++)
                    {
                        state.PreAtlasStructuralMergePairs.Add(
                            BuildStructuralMergePairKey(
                                rigidPath,
                                lodIndex,
                                group.PartIndices[left],
                                group.PartIndices[right]));
                    }
                }
            }
        }

        private static List<KeyValuePair<string, XmlDocument>> GetWritableWsModelsForRigid(
            BatchState state,
            string rigidPath)
            => state.WsDocuments
                .Where(x =>
                    Normalize(x.Value.SelectSingleNode("/model/geometry")?.InnerText)
                        .Equals(rigidPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

        private static bool TryGetStructuralMergeBlockReason(
            BatchState state,
            string rigidPath,
            out string reason)
        {
            if (!IsGameplayConsumerDiscoverySafeForAsset(state, rigidPath))
            {
                reason = "complete gameplay consumer discovery was not available; structural merge is blocked conservatively.";
                RecordGameplayMeshDiscoveryBlock(
                    state,
                    "StructuralMerge",
                    [rigidPath]);
                RecordStructuralMergeSkip(state, rigidPath, reason);
                return true;
            }

            var immutableConsumers =
                GetImmutableGameplayWsModelConsumersForRigid(state, rigidPath);
            if (immutableConsumers.Count != 0)
            {
                reason =
                    $"immutable gameplay WSModel consumer(s) would require part remapping: " +
                    $"{string.Join(", ", immutableConsumers.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}";
                RecordStructuralMergeSkip(state, rigidPath, reason);
                return true;
            }

            reason = string.Empty;
            return false;
        }

        private static void RecordStructuralMergeSkip(
            BatchState state,
            string rigidPath,
            string reason)
        {
            var message = $"{rigidPath}: {reason}";
            if (state.MeshMergeSkipMessages.Contains(message, StringComparer.Ordinal))
                return;

            state.MeshMergeSkipMessages.Add(message);
        }

        private const string AtlasSkipReasonCrossRigidChangedRigid =
            "the rigid was changed by cross-rigid merging, which has first pick of its meshes";

        // Atlasing runs after the cross-rigid stage and leaves its work alone: the source rigids
        // the writer built generated models from are not atlas candidates (their parts keep the
        // materials the generated WSModels name too).
        private static List<AtlasCandidate> ExcludeCrossRigidChangedAtlasCandidates(
            BatchState state,
            List<AtlasCandidate> candidates)
        {
            if (state.CrossRigidChangedRigids.Count == 0)
                return candidates;

            var kept = new List<AtlasCandidate>(candidates.Count);
            foreach (var candidate in candidates)
            {
                if (!state.CrossRigidChangedRigids.Contains(Normalize(candidate.Key.GeometryPath)))
                {
                    kept.Add(candidate);
                    continue;
                }

                RecordSkip(
                    state,
                    candidate.RootVmdPath,
                    candidate.Key,
                    candidate.Usages.FirstOrDefault()?.AssetPath ?? string.Empty,
                    AtlasSkipReasonCrossRigidChangedRigid);
                state.AtlasCandidatesExcludedByCrossRigid++;
            }

            state.AtlasValueGateCandidateTexturePaths.Clear();
            state.AtlasValueGateCandidateTexturePaths.UnionWith(
                GetAtlasValueGateCandidateTexturePaths(kept));
            return kept;
        }

        private static void IndexTaintedGameplayWsModels(
            BatchState state,
            CancellationToken cancellationToken)
        {
            state.TaintedGameplayWsModels.Clear();
            var resolution = state.UnitCategoryResolution;
            if (state.AtlasAllVmdsEnabled ||
                resolution == null ||
                resolution.UnresolvedConsumersByVmd.Count == 0)
            {
                return;
            }

            foreach (var rootValue in resolution.UnresolvedConsumersByVmd.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var wsModel in CollectReachableGameplayWsModels(
                             state,
                             rootValue,
                             cancellationToken))
                {
                    state.TaintedGameplayWsModels.Add(Normalize(wsModel));
                }
            }
        }

        // Kept as a small compatibility entry point for existing reflection-based tests and
        // callers. Production traversal uses the BatchState overload below so repeated path
        // resolution is memoized for the duration of a batch.
        private static IPackFileContainer? FindGameplayTraversalContainer(
            IPackFileContainer source,
            IReadOnlyList<IPackFileContainer> loadedContainers,
            string pathValue)
            => ResolveGameplayTraversalContainer(
                source,
                loadedContainers,
                Normalize(pathValue));

        private static IPackFileContainer? FindGameplayTraversalContainer(
            BatchState state,
            IPackFileContainer source,
            IReadOnlyList<IPackFileContainer> loadedContainers,
            string pathValue)
        {
            var path = Normalize(pathValue);
            if (path.Length == 0)
                return null;

            if (state.GameplayTraversalContainerResolutions.TryGetValue(
                    path,
                    out var cachedResolution))
            {
                state.GameplayTraversalContainerResolutionCacheHits++;
                return cachedResolution.Container;
            }

            state.GameplayTraversalContainerResolutionScans++;

            var resolvedContainer = ResolveGameplayTraversalContainer(
                source,
                loadedContainers,
                path,
                state.ResolutionProviders);
            if (resolvedContainer != null)
            {
                state.GameplayTraversalContainerResolutions[path] =
                    new GameplayTraversalContainerResolution(resolvedContainer);
                return resolvedContainer;
            }

            // Cache negative results too. The source and loaded CA containers are stable for
            // the duration of a batch, so an unresolved path cannot become resolvable later.
            state.GameplayTraversalContainerResolutions[path] =
                GameplayTraversalContainerResolution.Missing;
            return null;
        }

        private static IPackFileContainer? ResolveGameplayTraversalContainer(
            IPackFileContainer source,
            IReadOnlyList<IPackFileContainer> loadedContainers,
            string path)
            => ResolveGameplayTraversalContainer(source, loadedContainers, path, null);

        private static IPackFileContainer? ResolveGameplayTraversalContainer(
            IPackFileContainer source,
            IReadOnlyList<IPackFileContainer> loadedContainers,
            string path,
            ResolutionProviderSet? resolutionProviders)
        {
            if (path.Length == 0)
                return null;

            // The selected source pack is the effective override for this batch.
            if (source.ContainsFile(path))
                return source;

            // The source owns the files being optimized. For paths absent from it, search the
            // dependency-related packs in reverse load order (last wins), then the CA game containers.
            var providerHit = resolutionProviders?.Find(source, path);
            if (providerHit != null)
                return providerHit;

            // Search CA containers in reverse load order to preserve normal pack-file precedence.
            for (var index = loadedContainers.Count - 1; index >= 0; index--)
            {
                var container = loadedContainers[index];
                if (!container.IsCaPackFile || ReferenceEquals(container, source))
                    continue;
                if (container.ContainsFile(path))
                    return container;
            }

            return null;
        }

        private static IReadOnlyList<IPackFileContainer> GetGameplayTraversalContainers(
            BatchState state)
        {
            state.GameplayTraversalContainers ??=
                state.PackFileService.GetAllPackfileContainers().ToList();
            return state.GameplayTraversalContainers;
        }

        private static HashSet<string> CollectReachableGameplayWsModels(
            BatchState state,
            string rootVmdPath,
            CancellationToken cancellationToken)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var loadedContainers = GetGameplayTraversalContainers(state);
            queue.Enqueue(Normalize(rootVmdPath));

            while (queue.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var vmdPath = queue.Dequeue();
                if (vmdPath.Length == 0 || !visited.Add(vmdPath))
                    continue;

                var container = FindGameplayTraversalContainer(
                    state,
                    state.Source,
                    loadedContainers,
                    vmdPath);
                var file = container?.FindFile(vmdPath);
                if (container == null ||
                    file == null ||
                    !TryGetVmdForTraversal(
                        state,
                        container,
                        vmdPath,
                        file,
                        out var vmd))
                {
                    continue;
                }

                var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var children = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectVmdReferences(vmd, models, children, textures);

                foreach (var modelValue in models)
                {
                    var model = Normalize(modelValue);
                    if (!Path.GetExtension(model).Equals(
                            ".wsmodel",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (FindGameplayTraversalContainer(
                            state,
                            state.Source,
                            loadedContainers,
                            model) != null)
                    {
                        // Keep the logical path. A source-pack override of this path is the
                        // exact asset that the atlas rewrite would modify.
                        result.Add(model);
                    }
                }

                foreach (var childValue in children)
                {
                    var child = Normalize(childValue);
                    if (FindGameplayTraversalContainer(
                            state,
                            state.Source,
                            loadedContainers,
                            child) != null)
                    {
                        queue.Enqueue(child);
                    }
                }
            }

            return result;
        }

        private static bool IsSharedMeshSafeForGameplay(
            bool atlasAllVmdsEnabled,
            IEnumerable<string> affectedWsModels,
            IReadOnlySet<string> taintedGameplayWsModels)
            => atlasAllVmdsEnabled ||
               !affectedWsModels
                   .Select(Normalize)
                   .Any(taintedGameplayWsModels.Contains);

        private static bool IsCandidateSharedMeshSafeForGameplay(
            BatchState state,
            MeshKey key,
            IReadOnlyCollection<WsUsage> candidateUsages)
        {
            if (state.AtlasAllVmdsEnabled ||
                state.TaintedGameplayWsModels.Count == 0)
            {
                return true;
            }

            var affectedUsages = state.Usages.TryGetValue(key, out var allUsages)
                ? allUsages
                : candidateUsages;
            return IsSharedMeshSafeForGameplay(
                state.AtlasAllVmdsEnabled,
                affectedUsages
                    .Select(usage => usage.WsModelPath)
                    .Where(path => !string.IsNullOrWhiteSpace(path)),
                state.TaintedGameplayWsModels);
        }

        private List<MissingTextureDependency> FindMissingTextureDependencies(
            BatchState state,
            IReadOnlyList<string> vmdRoots,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var reachableWsModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var vmdPath in vmdRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                reachableWsModels.UnionWith(
                    GetReachableWsModels(state, vmdPath, cancellationToken));
            }

            var result = new List<MissingTextureDependency>();
            var usagesList = state.Usages.ToList();

            for (var usageIndex = 0; usageIndex < usagesList.Count; usageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (key, usages) = usagesList[usageIndex];
                if (usageIndex == 0 || usageIndex == usagesList.Count - 1 || usageIndex % 10 == 0)
                {
                    ReportProgress(
                        progress,
                        "Checking texture dependencies",
                        usageIndex + 1,
                        usagesList.Count,
                        key.ToString());
                }
                var reachableUsages = usages
                    .Where(x => reachableWsModels.Contains(x.WsModelPath))
                    .ToList();
                if (reachableUsages.Count == 0)
                    continue;

                var materialPaths = usages
                    .Select(x => x.MaterialPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (materialPaths.Count != 1)
                    continue;

                var materialFile = FindForRead(state, materialPaths[0]);
                if (materialFile == null)
                    continue;

                XmlDocument materialDoc;
                try
                {
                    materialDoc = GetMaterialDocument(state, materialPaths[0], materialFile);
                }
                catch
                {
                    continue;
                }

                var shaderPath = materialDoc.SelectSingleNode("/material/shader")?.InnerText ?? string.Empty;
                if (shaderPath.Contains("emissive", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (materialDoc.SelectNodes("/material/textures/texture")?
                        .Cast<XmlNode>()
                        .Any(x => GetTextureSlot(x).Contains("emissive", StringComparison.OrdinalIgnoreCase)) == true)
                {
                    continue;
                }

                if (FindConflictingDuplicateTextureSlot(materialDoc) != null)
                    continue;

                var primaryPath = GetTexturePath(materialDoc, "t_xml_base_colour");
                if (string.IsNullOrWhiteSpace(primaryPath) || FindForRead(state, primaryPath) == null)
                    continue;

                foreach (var channel in AtlasChannels.Skip(1))
                {
                    var texturePath = GetTexturePath(materialDoc, channel.Slot);
                    if (string.IsNullOrWhiteSpace(texturePath) || IsTexturePlaceholder(texturePath))
                        continue;

                    if (FindForRead(state, texturePath) == null)
                    {
                        if (IsIgnorableUnresolvedAtlasTexture(channel.Slot, texturePath))
                            continue;

                        result.Add(new MissingTextureDependency(
                            key,
                            channel.Slot,
                            texturePath));
                    }
                }
            }

            return result
                .Distinct()
                .OrderBy(x => x.TexturePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Key.LodIndex)
                .ThenBy(x => x.Key.PartIndex)
                .ThenBy(x => x.Slot, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string BuildMissingTextureDetails(
            IReadOnlyList<MissingTextureDependency> missingTextures)
        {
            var sb = new StringBuilder();

            foreach (var group in missingTextures.GroupBy(
                         x => x.TexturePath,
                         StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(group.Key);
                foreach (var dependency in group)
                {
                    sb.AppendLine(
                        $"  {dependency.Key.GeometryPath} [lod {dependency.Key.LodIndex}, part {dependency.Key.PartIndex}] " +
                        $"({dependency.Slot})");
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        private CandidateDiscoveryResult DiscoverAtlasCandidates(
            BatchState state,
            IReadOnlyList<string> vmdRoots,
            bool packWide,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var candidates = new List<AtlasCandidate>();
            var missingTextures = new List<MissingTextureDependency>();
            var inspectedKeys = packWide ? new HashSet<MeshKey>() : null;

            for (var vmdIndex = 0; vmdIndex < vmdRoots.Count; vmdIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                candidates.AddRange(CollectVmdCandidates(
                    state,
                    vmdRoots[vmdIndex],
                    vmdIndex + 1,
                    vmdRoots.Count,
                    cancellationToken,
                    progress,
                    inspectedKeys,
                    missingTextures));
            }

            var directAssetPaths = state.UnitCategoryResolution?
                .DirectAssetUsagesByPath.Keys
                .Where(path => state.Source.ContainsFile(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? [];
            candidates.AddRange(CollectDirectAssetCandidates(
                state,
                directAssetPaths,
                cancellationToken,
                progress,
                inspectedKeys,
                missingTextures));

            candidates = FilterAtlasCandidatesWithUnrewritableMaterialConsumers(
                state,
                candidates);

            if (state.ExistingAtlasOutputDetected)
            {
                var existingAtlasCandidates = candidates
                    .Where(candidate => IsAlreadyGeneratedAtlasCandidate(candidate))
                    .ToList();
                foreach (var candidate in existingAtlasCandidates)
                {
                    RecordSkip(
                        state,
                        candidate.RootVmdPath,
                        candidate.Key,
                        candidate.Usages.FirstOrDefault()?.AssetPath ?? string.Empty,
                        "Existing generated atlas material/texture was preserved; only newly un-atlased candidates remain eligible.");
                }

                state.ExistingGeneratedAtlasCandidateCount =
                    existingAtlasCandidates
                        .Select(candidate => candidate.Key)
                        .Distinct()
                        .Count();
                candidates = candidates
                    .Where(candidate => !IsAlreadyGeneratedAtlasCandidate(candidate))
                    .ToList();
            }

            return new CandidateDiscoveryResult(
                candidates,
                missingTextures
                    .Distinct()
                    .OrderBy(x => x.TexturePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Key.LodIndex)
                    .ThenBy(x => x.Key.PartIndex)
                    .ThenBy(x => x.Slot, StringComparer.OrdinalIgnoreCase)
                    .ToList());
        }

        private static List<AtlasCandidate> FilterAtlasCandidatesWithUnrewritableMaterialConsumers(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates)
        {
            var discoverySafeCandidates = new List<AtlasCandidate>(candidates.Count);
            foreach (var candidate in candidates)
            {
                if (IsAtlasCandidateGameplayDiscoverySafe(state, candidate))
                {
                    discoverySafeCandidates.Add(candidate);
                    continue;
                }

                var discoveryPaths = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    candidate.RootVmdPath,
                    candidate.Key.GeometryPath,
                };
                foreach (var usage in candidate.Usages)
                {
                    if (!string.IsNullOrWhiteSpace(usage.WsModelPath))
                        discoveryPaths.Add(usage.WsModelPath);
                }

                RecordGameplayMeshDiscoveryBlock(
                    state,
                    "AtlasRewrite",
                    discoveryPaths);

                RecordSkip(
                    state,
                    candidate.RootVmdPath,
                    candidate.Key,
                    candidate.Usages.FirstOrDefault()?.AssetPath ?? string.Empty,
                    "Gameplay mesh-consumer discovery was incomplete; atlas rewriting was skipped because every rigid material consumer could not be proven safe.");
            }
            candidates = discoverySafeCandidates;

            // A rigid owns UV0, so every material consumer must be rewritten together with
            // it. Direct embedded materials are safe only when there is no WSModel material
            // consumer for the same key; immutable CA WSModels are never safe because their
            // material nodes cannot be rewritten at all. Reject the whole key so discovery
            // ordering cannot let another candidate slip through after the first one.
            var blockedKeys = candidates
                .Where(candidate => HasUnrewritableMaterialConsumerAtlasConflict(state, candidate))
                .Select(candidate => candidate.Key)
                .ToHashSet();
            if (blockedKeys.Count == 0)
                return candidates.ToList();

            foreach (var candidate in candidates.Where(candidate => blockedKeys.Contains(candidate.Key)))
            {
                RecordSkip(
                    state,
                    candidate.RootVmdPath,
                    candidate.Key,
                    candidate.Usages.FirstOrDefault()?.AssetPath ?? string.Empty,
                    "Rigid mesh has a non-rewritable material consumer (direct/embedded or immutable CA WSModel); atlas rewriting would change shared UV0 without rewriting every consumer.");
            }

            return candidates
                .Where(candidate => !blockedKeys.Contains(candidate.Key))
                .ToList();
        }

        private static void RecordGameplayMeshDiscoveryBlock(
            BatchState state,
            string blockKind,
            IEnumerable<string> assetPaths)
        {
            var index = state.GameplayMeshDependencyIndex;
            var globalDiscoveryFailure = index == null ||
                                         state.UnitCategoryResolution == null ||
                                         !state.UnitCategoryResolution.IsGameplayResolutionHealthy;
            foreach (var assetPathValue in assetPaths)
            {
                var assetPath = Normalize(assetPathValue);
                if (assetPath.Length == 0)
                    continue;

                var incompleteRoots = index?.GetIncompleteRootsForAsset(assetPath)
                    ?? Array.Empty<string>();
                if (incompleteRoots.Count == 0)
                {
                    if (globalDiscoveryFailure ||
                        index?.IsAssetAffectedByIncompleteRoot(assetPath) == true)
                    {
                        state.ConsumerDiscoveryBlocks.Add(
                            new GameplayMeshDiscoveryBlock(
                                assetPath,
                                blockKind,
                                globalDiscoveryFailure
                                    ? "<global>"
                                    : "<unattributed>"));
                    }

                    continue;
                }

                foreach (var incompleteRoot in incompleteRoots)
                {
                    state.ConsumerDiscoveryBlocks.Add(
                        new GameplayMeshDiscoveryBlock(
                            assetPath,
                            blockKind,
                            incompleteRoot));
                }
            }
        }

        private static bool HasUnrewritableMaterialConsumerAtlasConflict(
            BatchState state,
            AtlasCandidate candidate)
        {
            var geometryPath = Normalize(candidate.Key.GeometryPath);
            if (GetImmutableGameplayWsModelConsumersForRigid(
                    state,
                    geometryPath).Count != 0)
            {
                return true;
            }

            var hasWsModelMaterialConsumer = candidate.Usages.Any(
                usage => string.IsNullOrWhiteSpace(usage.EmbeddedRigidPath));
            if (!hasWsModelMaterialConsumer &&
                state.Usages.TryGetValue(candidate.Key, out var usages))
            {
                hasWsModelMaterialConsumer = usages.Any(
                    usage => string.IsNullOrWhiteSpace(usage.EmbeddedRigidPath));
            }

            return HasDirectRigidConsumerAtlasConflict(
                state,
                geometryPath,
                hasWsModelMaterialConsumer);
        }

        private static bool HasDirectRigidConsumerAtlasConflict(
            BatchState state,
            string geometryPath,
            bool hasWsModelMaterialConsumer)
            => hasWsModelMaterialConsumer &&
               HasDirectGameplayRigidConsumer(state, geometryPath);

        private static List<AtlasCandidate> ApplyMissingTextureDecision(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates)
        {
            var result = new List<AtlasCandidate>(candidates.Count);

            foreach (var candidate in candidates)
            {
                var consumerBlock = GetConsumerAtlasBlock(state, candidate);
                if (consumerBlock != null)
                {
                    RecordSkip(
                        state,
                        candidate.RootVmdPath,
                        candidate.Key,
                        candidate.Usages[0].AssetPath,
                        $"another mod uses or overrides what this mesh would rewrite: {consumerBlock}");
                    continue;
                }

                if (candidate.MissingTextures.Count == 0)
                {
                    result.Add(candidate);
                    continue;
                }

                var firstMissing = candidate.MissingTextures[0];
                RecordSkip(
                    state,
                    candidate.RootVmdPath,
                    candidate.Key,
                    candidate.Usages[0].AssetPath,
                    $"{firstMissing.Slot} texture could not be resolved; mesh cannot be safely atlased because UV0 is shared across channels: {firstMissing.TexturePath}");
            }

            return result;
        }

        private void ProcessVmd(
            BatchState state,
            string rootVmdPath,
            List<AtlasCandidate> candidates,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            candidates = candidates
                .Where(x => !state.ProcessedMeshes.Contains(x.Key))
                .ToList();
            candidates = ApplyTexelDensityScaling(state, candidates);
            candidates = AlignSharedUvIslandCuts(state, candidates);

            var formatGroups = SplitCandidatesByKeepAlpha(candidates);
            CountKeepAlphaFormatGroups(state, formatGroups);
            var gateTotals = new FormatGroupGateTotals(state, formatGroups.Count);
            foreach (var formatGroup in formatGroups)
            {
                gateTotals.BeforeGroup();
                var batches = CreateBatches(
                    state,
                    formatGroup,
                    packWide: false,
                    cancellationToken: cancellationToken,
                    progress: progress);

                ProcessAtlasBatches(
                    state,
                    rootVmdPath,
                    rootVmdPath,
                    batches,
                    cancellationToken,
                    progress);
                gateTotals.AfterGroup();
            }
        }

        private void ProcessDirectAssetAtlases(
            BatchState state,
            List<AtlasCandidate> candidates,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            candidates = candidates
                .Where(candidate => !state.ProcessedMeshes.Contains(candidate.Key))
                .ToList();
            if (candidates.Count == 0)
                return;

            candidates = ApplyTexelDensityScaling(state, candidates);
            candidates = AlignSharedUvIslandCuts(state, candidates);
            var formatGroups = SplitCandidatesByKeepAlpha(candidates);
            CountKeepAlphaFormatGroups(state, formatGroups);
            var gateTotals = new FormatGroupGateTotals(state, formatGroups.Count);
            foreach (var formatGroup in formatGroups)
            {
                gateTotals.BeforeGroup();
                var batches = CreateBatches(
                    state,
                    formatGroup,
                    packWide: false,
                    cancellationToken: cancellationToken,
                    progress: progress);
                ProcessAtlasBatches(
                    state,
                    "direct-engine-assets",
                    "Direct engine assets",
                    batches,
                    cancellationToken,
                    progress);
                gateTotals.AfterGroup();
            }
        }

        private static bool IsDirectAssetCandidate(AtlasCandidate candidate)
            => candidate.Usages.Any(usage => !string.IsNullOrWhiteSpace(usage.EmbeddedRigidPath)) ||
               candidate.Usages.Any(usage =>
                   !string.IsNullOrWhiteSpace(usage.WsModelPath) &&
                   Path.GetExtension(usage.WsModelPath)
                       .Equals(".wsmodel", StringComparison.OrdinalIgnoreCase) &&
                   usage.WsModelPath.Equals(
                       candidate.RootVmdPath,
                       StringComparison.OrdinalIgnoreCase));

        private void ProcessPackWideAtlases(
            BatchState state,
            List<AtlasCandidate> candidates,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            state.PackWideCandidateCount = candidates.Count;
            candidates = ApplyTexelDensityScaling(state, candidates);
            candidates = AlignSharedUvIslandCuts(state, candidates);

            var packName = Path.GetFileNameWithoutExtension(state.SourcePath);
            var formatGroups = SplitCandidatesByKeepAlpha(candidates);
            CountKeepAlphaFormatGroups(state, formatGroups);
            var gateTotals = new FormatGroupGateTotals(state, formatGroups.Count);
            foreach (var formatGroup in formatGroups)
            {
                gateTotals.BeforeGroup();
                var stopwatch = Stopwatch.StartNew();
                var batches = CreateBatches(
                    state,
                    formatGroup,
                    packWide: true,
                    cancellationToken: cancellationToken,
                    progress: progress);
                AddPhaseDuration(state, "Plan atlas batches", stopwatch.Elapsed);

                stopwatch.Restart();
                ProcessAtlasBatches(
                    state,
                    $"pack:{packName}",
                    "Pack-wide",
                    batches,
                    cancellationToken,
                    progress);
                AddPhaseDuration(state, "Execute atlas batches", stopwatch.Elapsed);
                gateTotals.AfterGroup();
            }
        }

        private void ProcessAtlasBatches(
            BatchState state,
            string atlasScopeKey,
            string progressScope,
            IReadOnlyList<List<AtlasCandidate>> batches,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var pageGroups = BuildSharedAtlasPageGroups(
                state,
                batches,
                cancellationToken,
                progress);
            var pageGroupByBatchIndex = new SharedAtlasPageGroup[batches.Count];
            foreach (var pageGroup in pageGroups)
            {
                foreach (var batchIndex in pageGroup.BatchIndices)
                    pageGroupByBatchIndex[batchIndex] = pageGroup;
            }

            var generatedPages = new Dictionary<SharedAtlasPageGroup, SharedAtlasPage>();
            for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pageGroup = pageGroupByBatchIndex[batchIndex]
                    ?? throw new InvalidOperationException(
                        $"Atlas batch {batchIndex} was not assigned to a shared atlas page group.");
                if (!generatedPages.TryGetValue(pageGroup, out var sharedPage))
                {
                    sharedPage = GenerateSharedAtlasPage(
                        state,
                        atlasScopeKey,
                        progressScope,
                        pageGroup,
                        cancellationToken,
                        progress);
                    generatedPages.Add(pageGroup, sharedPage);
                }

                ReportProgress(
                    progress,
                    "Building texture atlases",
                    batchIndex + 1,
                    batches.Count,
                    $"{progressScope} — batch {batchIndex + 1} ({sharedPage.AtlasStem})");

                ProcessBatch(
                    state,
                    atlasScopeKey,
                    progressScope,
                    batches[batchIndex],
                    cancellationToken,
                    progress,
                    sharedPage);
            }
        }

        private static List<SharedAtlasPageGroup> BuildSharedAtlasPageGroups(
            BatchState state,
            IReadOnlyList<List<AtlasCandidate>> batches,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var pageGroups = new List<SharedAtlasPageGroup>();
            for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = batches[batchIndex];
                ReportProgress(
                    progress,
                    "Planning shared atlas pages",
                    batchIndex + 1,
                    batches.Count,
                    $"Checking batch {batchIndex + 1}");

                var canShare = TryGetSharedAtlasPageCompatibilityKey(
                    batch,
                    out var compatibilityKey);
                var hasSinglePagePlan = TryCreateSharedAtlasPagePlan(
                    state,
                    batch,
                    out var singlePagePlan);
                SharedAtlasPageGroup? selectedGroup = null;

                if (canShare && hasSinglePagePlan)
                {
                    foreach (var pageGroup in pageGroups)
                    {
                        if (!pageGroup.CompatibilityKey.HasValue ||
                            pageGroup.CompatibilityKey.Value != compatibilityKey ||
                            pageGroup.PagePlan == null)
                        {
                            continue;
                        }

                        state.SharedAtlasPageSharingEvaluations++;
                        var combinedCandidates = pageGroup.Candidates
                            .Concat(batch)
                            .ToList();
                        if (!TryCreateSharedAtlasPagePlan(
                                state,
                                combinedCandidates,
                                out var combinedPagePlan) ||
                            !FitsWithinReplacedPageBudget(
                                pageGroup.OriginalPageFootprints
                                    .Append(singlePagePlan.Footprint)
                                    .ToArray(),
                                combinedPagePlan.Footprint))
                        {
                            continue;
                        }

                        selectedGroup = pageGroup;
                        pageGroup.BatchIndices.Add(batchIndex);
                        pageGroup.Batches.Add(batch);
                        pageGroup.Candidates.AddRange(batch);
                        pageGroup.OriginalPageFootprints.Add(singlePagePlan.Footprint);
                        pageGroup.PagePlan = combinedPagePlan;
                        break;
                    }
                }

                if (selectedGroup != null)
                    continue;

                pageGroups.Add(
                    new SharedAtlasPageGroup(
                        canShare ? compatibilityKey : null,
                        new List<int> { batchIndex },
                        new List<List<AtlasCandidate>> { batch },
                        batch.ToList(),
                        hasSinglePagePlan ? singlePagePlan : null,
                        hasSinglePagePlan
                            ? new List<AtlasPageFootprint> { singlePagePlan.Footprint }
                            : []));
            }

            foreach (var pageGroup in pageGroups.Where(x => x.BatchIndices.Count > 1))
            {
                state.SharedAtlasPageCoalescesAccepted++;
                state.SharedAtlasPageParticipatingBatches += pageGroup.BatchIndices.Count;
                var originalPixels = pageGroup.OriginalPageFootprints.Sum(x => x.PixelCost);
                var sharedPixels = pageGroup.PagePlan?.Footprint.PixelCost ?? originalPixels;
                state.SharedAtlasPagePixelsSaved = checked(
                    state.SharedAtlasPagePixelsSaved +
                    Math.Max(0, originalPixels - sharedPixels));
            }

            return pageGroups;
        }

        private static bool TryGetSharedAtlasPageCompatibilityKey(
            IReadOnlyList<AtlasCandidate> batch,
            out SharedAtlasPageCompatibilityKey compatibilityKey)
        {
            compatibilityKey = default;
            if (batch.Count == 0)
                return false;

            var roots = batch
                .Select(candidate => Normalize(candidate.RootVmdPath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (roots.Length != 1 || string.IsNullOrWhiteSpace(roots[0]))
                return false;

            // Source identities, channel modes, resolution scales, and material shaders do
            // not need to match: each material is rewritten independently against the shared
            // placement. The combined plan and page budget are the physical safety checks.
            compatibilityKey = new SharedAtlasPageCompatibilityKey(
                roots[0],
                BuildSharedAtlasPageOutputPolicySignature());
            return true;
        }

        private static string BuildSharedAtlasPageOutputPolicySignature()
            => string.Join(
                "\u001f",
                "raw-bgra-mip-chain-v1",
                PackAtlasMaxSize,
                TextureAtlasBuilder.DefaultPadding,
                string.Join(
                    "\u001d",
                    AtlasChannels.Select(channel =>
                        $"{channel.Slot}:{channel.Type}:{channel.Suffix}")));

        private static bool TryCreateSharedAtlasPagePlan(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates,
            out SharedAtlasPagePlan pagePlan)
        {
            try
            {
                var sharedPlan = CreateSharedAtlasPlan(
                    state,
                    candidates,
                    deduplicateByContent: true);
                var footprint = CalculateAtlasPageFootprint(sharedPlan);
                pagePlan = new SharedAtlasPagePlan(
                    sharedPlan.Batch,
                    sharedPlan.Plan,
                    footprint);
                return true;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or ArgumentException or OverflowException)
            {
                pagePlan = null!;
                return false;
            }
        }

        private static AtlasPageFootprint CalculateAtlasPageFootprint(
            (SharedAtlasBatch Batch, TextureAtlasPlan Plan) sharedPlan)
        {
            var outputDimensionsByChannel =
                new Dictionary<string, (int Width, int Height)>(StringComparer.OrdinalIgnoreCase);
            long pixelCost = 0;

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
                            representative,
                            dimensions.Width,
                            dimensions.Height);
                    }
                }

                var requiresConstantOnlyAtlas = RequiresConstantOnlyMaskAtlas(
                    channel.Slot,
                    sharedPlan.Batch.Sources);
                if (sourceDimensions.Count == 0 && !requiresConstantOnlyAtlas)
                    continue;

                var outputDimensions = TextureAtlasBuilder.CalculateOutputDimensions(
                    sharedPlan.Plan,
                    sourceDimensions,
                    PackAtlasMaxSize);
                outputDimensionsByChannel[channel.Slot] = outputDimensions;
                pixelCost = checked(
                    pixelCost +
                    (long)outputDimensions.Width * outputDimensions.Height);
            }

            return new AtlasPageFootprint(outputDimensionsByChannel, pixelCost);
        }

        private SharedAtlasPage GenerateSharedAtlasPage(
            BatchState state,
            string atlasScopeKey,
            string progressScope,
            SharedAtlasPageGroup pageGroup,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pagePlan = pageGroup.PagePlan;
            if (pagePlan == null)
            {
                if (!TryCreateSharedAtlasPagePlan(
                        state,
                        pageGroup.Candidates,
                        out pagePlan))
                {
                    throw new InvalidOperationException(
                        "Could not create the shared atlas page plan for an accepted atlas batch.");
                }

                pageGroup.PagePlan = pagePlan;
            }

            var sharedBatch = pagePlan.Batch;
            var plan = pagePlan.Plan;
            var candidates = pageGroup.Candidates;
            state.AtlasPlacementsGenerated += sharedBatch.Sources.Count;
            state.AtlasPlacementsReused += candidates.Count - sharedBatch.Sources.Count;
            state.WrappedUvPlacementsCanonicalized += sharedBatch.MappingByMesh.Values.Count(
                x => x.WrappedUvCanonicalized);
            state.ContentDeduplicatedAtlasPlacements += sharedBatch.ContentPlacementsReused;
            state.ContentCanonicalizedMeshReferences += sharedBatch.MappingByMesh.Values.Count(
                x => x.ContentCanonicalized);

            var atlasStem = BuildAtlasStem(atlasScopeKey, state.AtlasPageIndex++);
            var generatedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

                var requiresConstantOnlyAtlas = RequiresConstantOnlyMaskAtlas(
                    channel.Slot,
                    sharedBatch.Sources);
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
                    atlasFormat == Editors.ImportExport.Dds.Native.DXGI_FORMAT.BC3_UNORM_SRGB &&
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

            return new SharedAtlasPage(
                sharedBatch,
                plan,
                atlasStem,
                generatedPaths);
        }

        private static void RecordAtlasPhysicalPageResidency(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates,
            IReadOnlyDictionary<string, string> generatedPaths)
        {
            long pixelCost = 0;
            foreach (var atlasPath in generatedPaths.Values.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!state.GeneratedTextureDimensions.TryGetValue(atlasPath, out var dimensions))
                    continue;

                pixelCost = checked(
                    pixelCost +
                    (long)dimensions.Width * dimensions.Height);
            }

            state.AtlasPhysicalPageDiagnostics.Add(
                new AtlasPhysicalPageDiagnostic(candidates.ToList(), pixelCost));
        }

        // A shared page may trade rectangular shape for area: two 1024x2048 pages can
        // legitimately become one 2048x2048 page. Keep both the raw-pixel guard and a
        // per-channel BCn mip-chain budget, but do not require the result to fit inside
        // one of the replaced rectangles.
        private static bool FitsWithinReplacedPageBudget(
            IReadOnlyList<AtlasPageFootprint> originalPages,
            AtlasPageFootprint combinedPage)
        {
            if (originalPages.Count == 0)
                return false;

            var originalPixelCost = originalPages.Sum(page => page.PixelCost);
            if (combinedPage.PixelCost > originalPixelCost)
                return false;

            foreach (var (channel, combinedDimensions) in combinedPage.OutputDimensionsByChannel)
            {
                var combinedBcnBytes = CalculateAtlasChannelBcnMipChainBytes(
                    channel,
                    combinedDimensions);
                var originalBcnBytes = originalPages.Sum(originalPage =>
                    originalPage.OutputDimensionsByChannel.TryGetValue(
                        channel,
                        out var originalDimensions)
                        ? CalculateAtlasChannelBcnMipChainBytes(channel, originalDimensions)
                        : 0L);

                if (combinedBcnBytes > originalBcnBytes)
                {
                    return false;
                }
            }

            return true;
        }

        private static long CalculateAtlasChannelBcnMipChainBytes(
            string channelSlot,
            (int Width, int Height) dimensions)
        {
            var channel = AtlasChannels.Single(candidate =>
                candidate.Slot.Equals(channelSlot, StringComparison.OrdinalIgnoreCase));
            // Every page compared here comes from one format group, so BC1 and BC3 never mix and
            // the ratio between a combined page and the pages it replaces does not depend on it.
            var bytesPerBlock = channel.Type is TextureType.BaseColour or TextureType.MaterialMap
                ? 8
                : 16;
            return CalculateBcnMipChainBytes(
                dimensions.Width,
                dimensions.Height,
                bytesPerBlock);
        }

        private List<AtlasCandidate> CollectVmdCandidates(
            BatchState state,
            string rootVmdPath,
            int vmdIndex,
            int vmdCount,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress,
            HashSet<MeshKey>? inspectedKeys = null,
            List<MissingTextureDependency>? missingTextureDependencies = null)
        {
            ReportProgress(progress, "Discovering atlas candidates", vmdIndex, vmdCount, rootVmdPath);
            var wsModels = GetReachableWsModels(state, rootVmdPath, cancellationToken);
            var candidates = new List<AtlasCandidate>();
            var localInspectedKeys = inspectedKeys ?? new HashSet<MeshKey>();

            foreach (var wsPath in wsModels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wsFile = state.Source.FindFile(wsPath);
                if (wsFile == null ||
                    !TryGetWsDocumentForTraversal(
                        state,
                        state.Source,
                        wsPath,
                        wsFile,
                        out var doc))
                {
                    continue;
                }

                var geometryPath = Normalize(doc.SelectSingleNode("/model/geometry")?.InnerText);
                if (string.IsNullOrWhiteSpace(geometryPath))
                    continue;

                var rmv = GetRmv(state, geometryPath);
                if (rmv == null)
                    continue;

                var materialNodes = doc.SelectNodes("/model/materials/material");
                if (materialNodes == null)
                    continue;

                foreach (XmlNode node in materialNodes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryParseIndex(node, "lod_index", out var lodIndex) ||
                        !TryParseIndex(node, "part_index", out var partIndex) ||
                        lodIndex < 0 || lodIndex >= rmv.ModelList.Length ||
                        partIndex < 0 || partIndex >= rmv.ModelList[lodIndex].Length)
                    {
                        continue;
                    }

                    var key = new MeshKey(geometryPath, lodIndex, partIndex);
                    ReportProgress(
                        progress,
                        "Discovering atlas candidates",
                        vmdIndex,
                        vmdCount,
                        $"{rootVmdPath} — {key}");

                    if (state.ProcessedMeshes.Contains(key) || !localInspectedKeys.Add(key))
                        continue;

                    if (!state.Usages.TryGetValue(key, out var usages) || usages.Count == 0)
                        continue;

                    var materialPaths = usages
                        .Select(x => x.MaterialPath)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    // The rigid owns UV0, not the WSModel. If the same rigid mesh is used with
                    // different materials, rewriting its UVs would require atlasing every one of
                    // those material variants with the same placement. Keep that uncommon case
                    // untouched rather than silently breaking one of the users.
                    if (materialPaths.Count != 1)
                    {
                        RecordSkip(
                            state,
                            rootVmdPath,
                            key,
                            wsPath,
                            $"Rigid mesh is shared by WSModels using {materialPaths.Count} different materials.");
                        continue;
                    }

                    var candidate = TryCreateCandidate(
                        state,
                        rootVmdPath,
                        key,
                        rmv.ModelList[lodIndex][partIndex],
                        usages,
                        missingTextureDependencies,
                        out var skipReason);
                    if (candidate == null)
                    {
                        RecordSkip(state, rootVmdPath, key, wsPath, skipReason);
                        continue;
                    }

                    candidates.Add(candidate);
                }
            }

            return candidates;
        }

        private List<AtlasCandidate> CollectDirectAssetCandidates(
            BatchState state,
            IReadOnlyList<string> assetPaths,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress,
            HashSet<MeshKey>? inspectedKeys,
            List<MissingTextureDependency>? missingTextureDependencies)
        {
            var candidates = new List<AtlasCandidate>();
            var localInspectedKeys = inspectedKeys ?? new HashSet<MeshKey>();

            for (var assetIndex = 0; assetIndex < assetPaths.Count; assetIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var assetPath = Normalize(assetPaths[assetIndex]);
                ReportProgress(
                    progress,
                    "Discovering direct engine assets",
                    assetIndex + 1,
                    assetPaths.Count,
                    assetPath);

                var extension = Path.GetExtension(assetPath);
                if (extension.Equals(".rigid_model_v2", StringComparison.OrdinalIgnoreCase))
                {
                    var file = state.Source.FindFile(assetPath);
                    if (file == null)
                        continue;

                    RmvFile rmv;
                    try
                    {
                        rmv = GetRmv(state, assetPath) ??
                            throw new InvalidDataException("Rigid model could not be loaded.");
                    }
                    catch (Exception ex)
                    {
                        RecordDirectAssetSkip(
                            state,
                            assetPath,
                            $"Rigid engine asset could not be loaded: {ex.Message}");
                        continue;
                    }

                    for (var lodIndex = 0; lodIndex < rmv.ModelList.Length; lodIndex++)
                    {
                        for (var partIndex = 0;
                             partIndex < rmv.ModelList[lodIndex].Length;
                             partIndex++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var key = new MeshKey(assetPath, lodIndex, partIndex);
                            if (state.ProcessedMeshes.Contains(key) || !localInspectedKeys.Add(key))
                                continue;

                            var model = rmv.ModelList[lodIndex][partIndex];
                            var usage = new WsUsage(
                                string.Empty,
                                null,
                                null,
                                BuildEmbeddedMaterialPath(assetPath, lodIndex, partIndex),
                                assetPath);
                            var candidate = TryCreateCandidate(
                                state,
                                assetPath,
                                key,
                                model,
                                [usage],
                                missingTextureDependencies,
                                out var skipReason,
                                BuildEmbeddedMaterialDocument(model.Material));
                            if (candidate == null)
                            {
                                RecordSkip(state, assetPath, key, string.Empty, skipReason);
                                continue;
                            }

                            candidates.Add(candidate);
                        }
                    }

                    continue;
                }

                if (!extension.Equals(".wsmodel", StringComparison.OrdinalIgnoreCase))
                    continue;

                XmlDocument document;
                try
                {
                    document = GetWsDocument(state, assetPath)
                        ?? throw new InvalidDataException("WSModel could not be loaded.");
                }
                catch (Exception ex)
                {
                    RecordDirectAssetSkip(
                        state,
                        assetPath,
                        $"WSModel engine asset could not be loaded: {ex.Message}");
                    continue;
                }

                var geometryPath = Normalize(
                    document.SelectSingleNode("/model/geometry")?.InnerText);
                if (geometryPath.Length == 0)
                    continue;

                var rmvForWsModel = GetRmv(state, geometryPath);
                var materialNodes = document.SelectNodes("/model/materials/material");
                if (rmvForWsModel == null || materialNodes == null)
                    continue;

                foreach (XmlNode materialNode in materialNodes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryParseIndex(materialNode, "lod_index", out var lodIndex) ||
                        !TryParseIndex(materialNode, "part_index", out var partIndex) ||
                        lodIndex < 0 ||
                        lodIndex >= rmvForWsModel.ModelList.Length ||
                        partIndex < 0 ||
                        partIndex >= rmvForWsModel.ModelList[lodIndex].Length)
                    {
                        continue;
                    }

                    var key = new MeshKey(geometryPath, lodIndex, partIndex);
                    if (state.ProcessedMeshes.Contains(key) || !localInspectedKeys.Add(key))
                        continue;

                    var materialPath = Normalize(materialNode.InnerText);
                    if (materialPath.Length == 0)
                        continue;

                    var usage = new WsUsage(assetPath, document, materialNode, materialPath);
                    var candidate = TryCreateCandidate(
                        state,
                        assetPath,
                        key,
                        rmvForWsModel.ModelList[lodIndex][partIndex],
                        [usage],
                        missingTextureDependencies,
                        out var skipReason);
                    if (candidate == null)
                    {
                        RecordSkip(state, assetPath, key, assetPath, skipReason);
                        continue;
                    }

                    candidates.Add(candidate);
                }
            }

            return candidates;
        }

        private AtlasCandidate? TryCreateCandidate(
            BatchState state,
            string rootVmdPath,
            MeshKey key,
            RmvModel model,
            List<WsUsage> usages,
            List<MissingTextureDependency>? missingTextureDependencies,
            out string skipReason,
            XmlDocument? materialDocumentOverride = null)
        {
            skipReason = string.Empty;
            if (!IsCandidateSharedMeshSafeForGameplay(state, key, usages))
            {
                skipReason =
                    "Rigid mesh is shared with a WSModel reachable from an unresolved " +
                    "gameplay consumer; gameplay-used mode leaves the shared asset unchanged.";
                return null;
            }

            var materialPath = usages[0].MaterialPath;
            XmlDocument materialDoc;
            if (materialDocumentOverride != null)
            {
                if (TryGetUnsafeEmbeddedUvTexture(model.Material, out var unsafeTexture))
                {
                    skipReason =
                        $"Embedded rigid material contains UV0-dependent texture type " +
                        $"{unsafeTexture.TexureType} that is not rewritten by the atlas path: " +
                        $"{unsafeTexture.Path}";
                    return null;
                }

                materialDoc = materialDocumentOverride;
            }
            else
            {
                var materialFile = FindForRead(state, materialPath);
                if (materialFile == null)
                {
                    skipReason = $"Material file could not be resolved: {materialPath}";
                    return null;
                }

                try
                {
                    materialDoc = GetMaterialDocument(state, materialPath, materialFile);
                }
                catch (Exception ex)
                {
                    skipReason = $"Material XML could not be parsed: {materialPath} ({ex.Message})";
                    return null;
                }
            }

            // GetMaterialDocument returns the cached source XML. Canonical mask rewriting is
            // candidate-local, so never mutate that shared document while preparing a batch.
            if (materialDocumentOverride == null)
            {
                var sourceMaterial = materialDoc;
                materialDoc = new XmlDocument();
                materialDoc.LoadXml(sourceMaterial.OuterXml);
            }

            var shaderPath = materialDoc.SelectSingleNode("/material/shader")?.InnerText ?? string.Empty;
            if (shaderPath.Contains("emissive", StringComparison.OrdinalIgnoreCase))
            {
                skipReason = $"Emissive shader is intentionally not atlased: {shaderPath}";
                return null;
            }

            if (materialDoc.SelectNodes("/material/textures/texture")?
                    .Cast<XmlNode>()
                    .Any(x => GetTextureSlot(x).Contains("emissive", StringComparison.OrdinalIgnoreCase)) == true)
            {
                skipReason = "Material contains an emissive texture slot and must retain its original UV0 mapping.";
                return null;
            }

            if (FindConflictingDuplicateTextureSlot(materialDoc) is { } conflictingSlot)
            {
                skipReason = $"Material lists texture slot {conflictingSlot} more than once with different " +
                    "sources; which one the engine samples is unknown.";
                return null;
            }

            var primaryPath = GetTexturePath(materialDoc, "t_xml_base_colour");
            if (string.IsNullOrWhiteSpace(primaryPath))
            {
                skipReason = "Material has no t_xml_base_colour texture.";
                return null;
            }

            var faction3MaskCanonicalization = materialDocumentOverride == null
                ? CanonicalizeFaction3MaskParameter(materialDoc)
                : Faction3MaskCanonicalization.None;
            var canonicalMaskWasAdded = false;
            var clearMaskBlueChannel = false;
            var requiresMaskAtlas = false;
            if (faction3MaskCanonicalization != Faction3MaskCanonicalization.None)
            {
                var maskPath = GetTexturePath(materialDoc, "t_xml_mask");
                var maskIsAbsent = string.IsNullOrWhiteSpace(maskPath) ||
                    IsTexturePlaceholder(maskPath) ||
                    (IsIgnorableUnresolvedAtlasTexture("t_xml_mask", maskPath) &&
                     FindForRead(state, maskPath) == null);
                if (maskIsAbsent)
                {
                    EnsureTexturePath(
                        materialDoc,
                        "t_xml_mask",
                        CanonicalBlackMaskTexturePath);
                    canonicalMaskWasAdded = true;
                    requiresMaskAtlas = true;
                }
                else if (faction3MaskCanonicalization == Faction3MaskCanonicalization.ZeroSelector)
                {
                    // The old selector ignored this texture completely. Preserve that exact
                    // result after switching to the common blue-channel selector by clearing
                    // only blue in the source region; green, red and alpha remain untouched
                    // for the other mask consumers.
                    clearMaskBlueChannel = true;
                    requiresMaskAtlas = true;
                }
            }

            var candidateMissingTextures = new List<MissingTextureDependency>();
            var resolvedChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var constantChannels = new Dictionary<string, TextureAtlasConstantColor>(
                StringComparer.OrdinalIgnoreCase);
            var channelDimensions = new Dictionary<string, (int Width, int Height)>(
                StringComparer.OrdinalIgnoreCase);

            int? primaryWidth = null;
            int? primaryHeight = null;

            foreach (var channel in AtlasChannels)
            {
                var path = GetTexturePath(materialDoc, channel.Slot);
                if (string.IsNullOrWhiteSpace(path))
                {
                    if (channel.Slot.Equals("t_xml_base_colour", StringComparison.OrdinalIgnoreCase))
                    {
                        skipReason = "Material has no t_xml_base_colour texture.";
                        return null;
                    }

                    continue;
                }

                if (IsTexturePlaceholder(path))
                    continue;

                var file = FindForRead(state, path);
                if (canonicalMaskWasAdded &&
                    channel.Slot.Equals("t_xml_mask", StringComparison.OrdinalIgnoreCase) &&
                    path.Equals(CanonicalBlackMaskTexturePath, StringComparison.OrdinalIgnoreCase))
                {
                    constantChannels[channel.Slot] = ZeroMaskColor;
                    RegisterUniformConstantTextureCanonicalPath(
                        state,
                        channel.Slot,
                        ZeroMaskColor,
                        CanonicalBlackMaskTexturePath);
                    continue;
                }

                if (file == null)
                {
                    if (channel.Slot.Equals("t_xml_base_colour", StringComparison.OrdinalIgnoreCase))
                    {
                        skipReason = $"Base-colour texture could not be resolved: {path}";
                        return null;
                    }

                    if (IsIgnorableUnresolvedAtlasTexture(channel.Slot, path))
                        continue;

                    var missing = new MissingTextureDependency(
                        key,
                        channel.Slot,
                        path);
                    candidateMissingTextures.Add(missing);
                    missingTextureDependencies?.Add(missing);
                    continue;
                }

                try
                {
                    var inspection = GetTextureInspection(state, path, file);

                    if (channel.Slot.Equals("t_xml_base_colour", StringComparison.OrdinalIgnoreCase))
                    {
                        primaryWidth = inspection.Width;
                        primaryHeight = inspection.Height;
                    }

                    if (inspection.IsUniformConstant)
                    {
                        var sourceConstantColor = inspection.ConstantColor;
                        var constantColor = sourceConstantColor;
                        if (channel.Slot.Equals(
                                "t_xml_mask",
                                StringComparison.OrdinalIgnoreCase) &&
                            faction3MaskCanonicalization == Faction3MaskCanonicalization.ZeroSelector)
                        {
                            // constantChannels describes the future atlas bytes. The
                            // canonical source-path map must still use the bytes in the
                            // original texture, otherwise a B=255 source can be advertised
                            // globally as a B=0 source.
                            constantColor = constantColor with { B = 0 };
                        }

                        constantChannels[channel.Slot] = constantColor;
                        RegisterUniformConstantTextureCanonicalPath(
                            state,
                            channel.Slot,
                            sourceConstantColor,
                            path);
                        continue;
                    }

                    // The shared placement is expressed in primary BaseColour pixels. Record
                    // each secondary channel's native resolution separately so its physical
                    // atlas can be sized independently without changing the normalized UVs.
                    channelDimensions[channel.Slot] = (inspection.Width, inspection.Height);
                    resolvedChannels.Add(channel.Slot);
                }
                catch (Exception ex)
                {
                    skipReason = $"{channel.Slot} is not a usable DDS: {path} ({ex.Message})";
                    return null;
                }
            }

            if (!resolvedChannels.Contains("t_xml_base_colour") &&
                !constantChannels.ContainsKey("t_xml_base_colour"))
            {
                skipReason = $"Base-colour texture could not be prepared: {primaryPath}";
                return null;
            }

            var keepAlphaReasons = GetKeepAlphaReasons(
                state,
                materialDoc,
                resolvedChannels,
                constantChannels);

            var width = primaryWidth
                ?? throw new InvalidOperationException("Base-colour dimensions were not resolved.");
            var height = primaryHeight
                ?? throw new InvalidOperationException("Base-colour dimensions were not resolved.");

            UvBounds bounds;
            TextureAtlasUvIslandNormalization uvIslandAnalysis;
            UvIslandNormalization? uvIslandNormalization;
            try
            {
                var originalBounds = GetUvBounds(model);
                var uvs = model.Mesh.VertexList
                    .Select(vertex => (U: vertex.Uv.X, V: vertex.Uv.Y))
                    .ToArray();
                uvIslandAnalysis = TextureAtlasBuilder.CalculateDisconnectedUvIslandNormalization(
                    uvs,
                    model.Mesh.IndexList);
                uvIslandNormalization = BuildUvIslandNormalization(
                    width,
                    height,
                    originalBounds,
                    uvIslandAnalysis,
                    allowEqualCrop: false);
                bounds = uvIslandNormalization?.NormalizedBounds ?? originalBounds;
            }
            catch (Exception ex)
            {
                skipReason = $"UV0 could not be prepared safely: {ex.Message}";
                return null;
            }

            return new AtlasCandidate(
                rootVmdPath,
                key,
                model,
                usages,
                materialPath,
                materialDoc,
                width,
                height,
                bounds,
                resolvedChannels,
                constantChannels,
                channelDimensions,
                candidateMissingTextures,
                uvIslandAnalysis,
                uvIslandNormalization,
                AtlasResolutionScale: 1.0,
                RequiresMaskAtlas: requiresMaskAtlas,
                ClearMaskBlueChannel: clearMaskBlueChannel)
            {
                KeepAlphaChannels = keepAlphaReasons.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
                KeepAlphaReasons = keepAlphaReasons,
            };
        }

        private static bool IsEmbeddedAtlasTextureTypeSafe(TextureType textureType)
            => textureType is
                TextureType.BaseColour or
                TextureType.Diffuse or
                TextureType.MaterialMap or
                TextureType.Normal or
                TextureType.Mask;

        private static bool TryGetUnsafeEmbeddedUvTexture(
            IRmvMaterial material,
            out RmvTexture unsafeTexture)
        {
            foreach (var texture in material.GetAllTextures())
            {
                if (string.IsNullOrWhiteSpace(texture.Path))
                    continue;
                if (IsEmbeddedAtlasTextureTypeSafe(texture.TexureType))
                    continue;

                unsafeTexture = texture;
                return true;
            }

            unsafeTexture = default;
            return false;
        }

        private static XmlDocument BuildEmbeddedMaterialDocument(IRmvMaterial material)
        {
            var document = new XmlDocument();
            document.LoadXml(
                $"<material><name>embedded_{material.MaterialId}</name>" +
                $"<shader>embedded/{material.MaterialId}</shader><textures /></material>");
            var texturesNode = document.SelectSingleNode("/material/textures")!;
            var writtenSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var texture in material.GetAllTextures())
            {
                if (!TryGetEmbeddedAtlasSlot(texture.TexureType, out var slot) ||
                    string.IsNullOrWhiteSpace(texture.Path) ||
                    !writtenSlots.Add(slot))
                {
                    continue;
                }

                var textureNode = document.CreateElement("texture");
                var slotNode = document.CreateElement("slot");
                slotNode.InnerText = slot;
                textureNode.AppendChild(slotNode);
                var sourceNode = document.CreateElement("source");
                sourceNode.InnerText = texture.Path;
                textureNode.AppendChild(sourceNode);
                texturesNode.AppendChild(textureNode);
            }

            return document;
        }

        private static bool TryGetEmbeddedAtlasSlot(TextureType textureType, out string slot)
        {
            slot = textureType switch
            {
                TextureType.BaseColour or TextureType.Diffuse => "t_xml_base_colour",
                TextureType.MaterialMap => "t_xml_material_map",
                TextureType.Normal => "t_xml_normal",
                TextureType.Mask => "t_xml_mask",
                _ => string.Empty,
            };
            return slot.Length != 0;
        }

        private static void ApplyEmbeddedMaterialTexture(
            IRmvMaterial material,
            string slot,
            string path)
        {
            var textureTypes = slot switch
            {
                "t_xml_base_colour" => new[] { TextureType.BaseColour, TextureType.Diffuse },
                "t_xml_material_map" => new[] { TextureType.MaterialMap },
                "t_xml_normal" => new[] { TextureType.Normal },
                "t_xml_mask" => new[] { TextureType.Mask },
                _ => [],
            };

            foreach (var textureType in textureTypes)
            {
                if (material.GetTexture(textureType).HasValue)
                {
                    material.SetTexture(textureType, path);
                    return;
                }
            }
        }

        private static string BuildEmbeddedMaterialPath(
            string assetPath,
            int lodIndex,
            int partIndex)
            => $"embedded:{assetPath}:lod:{lodIndex}:part:{partIndex}";

        private static void RecordDirectAssetSkip(
            BatchState state,
            string assetPath,
            string reason)
        {
            state.DirectAssetSkipMessages.Add($"{assetPath}: {reason}");
        }

        private static TextureInspection GetTextureInspection(
            BatchState state,
            string texturePath,
            PackFile file)
        {
            texturePath = Normalize(texturePath);
            if (state.TextureInspections.TryGetValue(texturePath, out var cached))
                return cached;

            var dimensions = TextureAtlasBuilder.GetDimensions(file.DataSource.PeekData(20));
            var constantColor = default(TextureAtlasConstantColor);
            var isUniformConstant = false;
            if (ShouldProbeUniformTexture(texturePath, dimensions.Width, dimensions.Height))
            {
                try
                {
                    var bytes = file.DataSource.ReadData();
                    isUniformConstant = TextureAtlasBuilder.TryGetUniformColor(bytes, out constantColor);
                }
                catch
                {
                    // Uniform probing is only an optimization. Unsupported/odd DDS variants
                    // must fall back to the normal atlas path rather than making the mesh ineligible.
                    isUniformConstant = false;
                    constantColor = default;
                }
            }

            if (isUniformConstant)
                state.UniformConstantTexturePaths.Add(texturePath);

            var inspection = new TextureInspection(
                dimensions.Width,
                dimensions.Height,
                isUniformConstant,
                constantColor);
            state.TextureInspections[texturePath] = inspection;
            return inspection;
        }

        private static void RegisterUniformConstantTextureCanonicalPath(
            BatchState state,
            string slot,
            TextureAtlasConstantColor sourceColor,
            string texturePath)
        {
            var key = BuildUniformConstantTextureKey(slot, sourceColor);
            var normalizedPath = Normalize(texturePath);
            if (normalizedPath.Length == 0)
                return;

            if (!state.UniformConstantTextureCanonicalPaths.TryGetValue(key, out var existing) ||
                string.Compare(normalizedPath, existing, StringComparison.OrdinalIgnoreCase) < 0)
            {
                // The key is content-derived, so replacing the path with the stable
                // lexicographically-first source is safe. It lets generated materials with
                // equivalent uniform channels share an identity without treating arbitrary
                // non-uniform textures as interchangeable.
                state.UniformConstantTextureCanonicalPaths[key] = normalizedPath;
            }
        }

        private static void CanonicalizeUniformConstantTexturePaths(
            BatchState state,
            XmlDocument material,
            IReadOnlyDictionary<string, TextureAtlasConstantColor> constantChannels,
            bool recordAssignment = true)
        {
            foreach (var (slot, color) in constantChannels)
            {
                var currentPath = GetTexturePath(material, slot);
                if (currentPath.Length == 0 ||
                    state.GeneratedTexturePaths.Contains(currentPath))
                    continue;

                var key = BuildUniformConstantTextureKey(slot, color);
                if (!state.UniformConstantTextureCanonicalPaths.TryGetValue(
                        key,
                        out var canonicalPath) ||
                    currentPath.Equals(canonicalPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                SetTexturePath(material, slot, canonicalPath);
                if (recordAssignment)
                    state.UniformConstantTextureAssignmentsCanonicalized++;
            }
        }

        private static string BuildUniformConstantTextureKey(
            string slot,
            TextureAtlasConstantColor color)
            => string.Join(
                ":",
                Normalize(slot),
                color.B,
                color.G,
                color.R,
                color.A);

        private static List<AtlasCandidate> ApplyTexelDensityScaling(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates)
        {
            if (candidates.Count == 0)
                return [];

            var densities = new Dictionary<MeshKey, double>();
            foreach (var candidate in candidates)
            {
                if (TryCalculateTexelDensity(candidate, out var density))
                    densities[candidate.Key] = density;
            }

            if (densities.Count < 2)
                return candidates.ToList();

            var globalReference = Percentile(
                densities.Values.OrderBy(value => value).ToArray(),
                TexelDensityReferencePercentile);
            state.TexelDensityGlobalReferences.Add(globalReference);
            var densitiesByRoot = candidates
                .Where(candidate => densities.ContainsKey(candidate.Key))
                .GroupBy(candidate => Normalize(candidate.RootVmdPath))
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(candidate => densities[candidate.Key])
                        .OrderBy(value => value)
                        .ToArray(),
                    StringComparer.OrdinalIgnoreCase);

            var result = new List<AtlasCandidate>(candidates.Count);
            foreach (var candidate in candidates)
            {
                if (!densities.TryGetValue(candidate.Key, out var density))
                {
                    result.Add(candidate);
                    continue;
                }

                var rootKey = Normalize(candidate.RootVmdPath);
                var rootValues = densitiesByRoot.GetValueOrDefault(rootKey);
                var reference = rootValues is { Length: >= 4 }
                    ? Percentile(rootValues, TexelDensityReferencePercentile)
                    : globalReference;
                var scale = CalculateTexelDensityResolutionScale(
                    density,
                    reference,
                    Math.Max(candidate.Width, candidate.Height));

                if (scale >= 1.0)
                {
                    result.Add(candidate);
                    continue;
                }

                state.TexelDensityScaledMeshes++;
                state.TexelDensityOriginalPixelArea = checked(
                    state.TexelDensityOriginalPixelArea +
                    (long)candidate.Width * candidate.Height);
                state.TexelDensityScaledPixelArea = checked(
                    state.TexelDensityScaledPixelArea +
                    (long)Math.Max(1, (int)Math.Ceiling(candidate.Width * scale)) *
                    Math.Max(1, (int)Math.Ceiling(candidate.Height * scale)));
                state.TexelDensityScaleCounts[scale] =
                    state.TexelDensityScaleCounts.GetValueOrDefault(scale) + 1;

                result.Add(candidate with { AtlasResolutionScale = scale });
            }

            return result;
        }

        // Shared with the standalone texture downscale: the same outlier multiplier,
        // clamps and buckets decide how far a texture may shrink for a given density.
        private static double CalculateTexelDensityResolutionScale(
            double density,
            double reference,
            int maxDimension)
        {
            var maximumDensity = Math.Max(
                double.Epsilon,
                reference * TexelDensityOutlierMultiplier);
            var requestedScale = Math.Min(1.0, maximumDensity / density);
            var minimumScaleForSize = maxDimension <= 0
                ? 1.0
                : Math.Min(
                    1.0,
                    MinAtlasResolutionScaleDimension / (double)maxDimension);
            requestedScale = Math.Clamp(
                requestedScale,
                Math.Max(MinAtlasResolutionScale, minimumScaleForSize),
                1.0);

            // Keep physical atlas sizes mip-friendly. A scale bucket also prevents tiny
            // density differences from producing unstable output dimensions across runs.
            return requestedScale <= 0.375
                ? 0.25
                : requestedScale <= 0.75
                    ? 0.5
                    : 1.0;
        }

        private static bool TryCalculateTexelDensity(
            AtlasCandidate candidate,
            out double density)
        {
            density = 0;
            var vertices = candidate.Model.Mesh.VertexList;
            var indices = candidate.Model.Mesh.IndexList;
            if (indices.Length < 3 || indices.Length % 3 != 0)
                return false;

            double worldArea = 0;
            double uvPixelArea = 0;
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

                var auv = a.Uv;
                var buv = b.Uv;
                var cuv = c.Uv;
                var triangleUvArea = Math.Abs(
                    (buv.X - auv.X) * (cuv.Y - auv.Y) -
                    (buv.Y - auv.Y) * (cuv.X - auv.X)) * 0.5;
                if (triangleUvArea <= 0)
                    continue;

                worldArea += triangleWorldArea;
                uvPixelArea += triangleUvArea * candidate.Width * (double)candidate.Height;
            }

            if (worldArea <= double.Epsilon || uvPixelArea <= double.Epsilon)
                return false;

            density = Math.Sqrt(uvPixelArea / worldArea);
            return double.IsFinite(density) && density > 0;
        }

        private static double Percentile(
            IReadOnlyList<double> sortedValues,
            double percentile)
        {
            if (sortedValues.Count == 0)
                return 0;
            if (sortedValues.Count == 1)
                return sortedValues[0];

            var position = Math.Clamp(percentile, 0, 1) * (sortedValues.Count - 1);
            var lower = (int)Math.Floor(position);
            var upper = (int)Math.Ceiling(position);
            if (lower == upper)
                return sortedValues[lower];

            var fraction = position - lower;
            return sortedValues[lower] * (1 - fraction) +
                   sortedValues[upper] * fraction;
        }

        private static (int Width, int Height) GetScaledAtlasDimensions(
            AtlasCandidate candidate,
            int width,
            int height)
        {
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height));

            var scale = candidate.AtlasResolutionScale;
            if (!double.IsFinite(scale) || scale <= 0 || scale > 1.0)
            {
                throw new InvalidOperationException(
                    $"Invalid atlas resolution scale {scale} for {candidate.Key}.");
            }

            return (
                Math.Max(1, checked((int)Math.Ceiling(width * scale))),
                Math.Max(1, checked((int)Math.Ceiling(height * scale))));
        }
    }
}
