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
        private void BuildWsUsageIndex(
            BatchState state,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var wsPaths = state.Source.GetAllFiles().Keys
                .Where(x => Path.GetExtension(x).Equals(".wsmodel", StringComparison.OrdinalIgnoreCase))
                .ToList();

            for (var wsIndex = 0; wsIndex < wsPaths.Count; wsIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wsPath = wsPaths[wsIndex];
                if (wsIndex == 0 || wsIndex == wsPaths.Count - 1 || wsIndex % 10 == 0)
                {
                    ReportProgress(
                        progress,
                        "Indexing WSModels",
                        wsIndex + 1,
                        wsPaths.Count,
                        wsPath);
                }

                XmlDocument? doc;
                try
                {
                    doc = GetWsDocument(state, wsPath);
                }
                catch (Exception ex) when (
                    ex is InvalidOperationException or
                    XmlException or
                    FormatException or
                    ArgumentException)
                {
                    RecordMalformedWsModel(state, wsPath, ex);
                    continue;
                }

                if (doc == null)
                    continue;

                var geometryPath = Normalize(doc.SelectSingleNode("/model/geometry")?.InnerText);
                if (string.IsNullOrWhiteSpace(geometryPath) || state.Source.FindFile(geometryPath) == null)
                    continue;

                var materialNodes = doc.SelectNodes("/model/materials/material");
                if (materialNodes == null)
                    continue;

                foreach (XmlNode node in materialNodes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryParseIndex(node, "lod_index", out var lodIndex) ||
                        !TryParseIndex(node, "part_index", out var partIndex))
                        continue;

                    var materialPath = Normalize(node.InnerText);
                    var key = new MeshKey(geometryPath, lodIndex, partIndex);
                    if (!state.Usages.TryGetValue(key, out var usages))
                    {
                        usages = [];
                        state.Usages[key] = usages;
                    }

                    usages.Add(new WsUsage(wsPath, doc, node, materialPath));
                }
            }
        }

        private static void IndexImmutableMeshMergeConsumers(
            BatchState state,
            IReadOnlyList<string> vmdRoots,
            CancellationToken cancellationToken)
        {
            var resolution = state.UnitCategoryResolution;
            var dependencyStopwatch = Stopwatch.StartNew();
            var dependencyIndex = BuildGameplayMeshDependencyIndex(
                state,
                vmdRoots,
                cancellationToken);
            state.PhaseDurations["  Gameplay mesh dependency index (inside the phase that calls it)"] =
                dependencyStopwatch.Elapsed;
            state.GameplayMeshDependencyIndex = dependencyIndex;

            state.ImmutableMeshMergeConsumersByRigid.Clear();
            state.DirectRigidMeshMergeConsumersByRigid.Clear();
            state.StructuralMergeConsumerDiscoveryFailures.Clear();
            state.ConsumerDiscoveryBlocks.Clear();
            foreach (var failure in dependencyIndex.BlockingFailureDetails)
            {
                state.StructuralMergeConsumerDiscoveryFailures.Add(
                    FormatGameplayMeshDependencyFailure(failure));
            }

            state.StructuralMergeConsumerDiscoveryComplete =
                resolution != null &&
                resolution.IsGameplayResolutionHealthy &&
                dependencyIndex.IsComplete;

            // These two dictionaries are retained as compact reporting/test views. The
            // dependency index is the authoritative topology and safety source.
            foreach (var (rigidPath, wsModelPaths) in dependencyIndex.WsModelConsumersByRigid)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!state.Source.ContainsFile(rigidPath))
                    continue;

                var immutableConsumers = wsModelPaths
                    .Where(path => !state.Source.ContainsFile(path))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (immutableConsumers.Count != 0)
                {
                    state.ImmutableMeshMergeConsumersByRigid[rigidPath] =
                        immutableConsumers;
                }
            }

            foreach (var (rigidPath, rootConsumers) in dependencyIndex.DirectConsumersByRigid)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!state.Source.ContainsFile(rigidPath) || rootConsumers.Count == 0)
                    continue;

                state.DirectRigidMeshMergeConsumersByRigid[rigidPath] =
                    new HashSet<string>(
                        rootConsumers,
                        StringComparer.OrdinalIgnoreCase);
            }
        }

        private static GameplayMeshDependencyIndex BuildGameplayMeshDependencyIndex(
            BatchState state,
            IReadOnlyList<string> sourceVmdRoots,
            CancellationToken cancellationToken)
        {
            var resolution = state.UnitCategoryResolution;

            // bc8b75c9's UnitIdsByAssetPath is intentionally an army-planning index: units
            // outside the slot template or modeled cultures may be absent. Reuse that fast
            // population, but never treat it as the complete mutation-safety authority.
            var indexedGameplayAssets = state.CheckMode
                ? Array.Empty<string>()
                : state.ArmyResidencyModel?.UnitIdsByAssetPath.Keys
                    .Where(IsMeshConsumerAssetPath)
                    ?? Array.Empty<string>();

            // The resolver roster is the complete DB-derived visual population before the
            // residency model applies army/category/culture filters. Every resolved VMD
            // component remains a safety root because an excluded unit can still reference
            // an immutable CA WSModel sharing a source-owned rigid.
            var safetyRosterVmdRoots = state.CheckMode
                ? Array.Empty<string>()
                : resolution?.RosterUnits
                .SelectMany(unit => unit.Components)
                .Where(component => component.IsVariantMeshDefinition)
                .Select(component => component.AssetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                ?? Array.Empty<string>();

            var rootKindsByPath =
                new Dictionary<string, HashSet<GameplayMeshDependencyRootKind>>(
                    StringComparer.OrdinalIgnoreCase);

            void AddRoot(
                string pathValue,
                GameplayMeshDependencyRootKind rootKind)
            {
                var path = Normalize(pathValue);
                if (path.Length == 0)
                    return;

                if (!rootKindsByPath.TryGetValue(path, out var kinds))
                {
                    kinds = [];
                    rootKindsByPath[path] = kinds;
                }

                kinds.Add(rootKind);
            }

            foreach (var path in sourceVmdRoots)
                AddRoot(path, GameplayMeshDependencyRootKind.SourcePackVmd);
            foreach (var path in indexedGameplayAssets)
                AddRoot(path, GameplayMeshDependencyRootKind.ResidencyAsset);
            foreach (var path in safetyRosterVmdRoots)
                AddRoot(path, GameplayMeshDependencyRootKind.ResolvedRosterVmd);
            foreach (var path in resolution?.UnresolvedConsumersByVmd.Keys ??
                         Array.Empty<string>())
            {
                AddRoot(path, GameplayMeshDependencyRootKind.UnresolvedResolverVmd);
            }

            foreach (var path in resolution?.DirectAssetUsagesByPath.Keys
                         .Where(IsMeshConsumerAssetPath) ?? Array.Empty<string>())
            {
                AddRoot(path, GameplayMeshDependencyRootKind.DirectGameplayAsset);
            }

            foreach (var path in state.Consumers?.RootFiles.Keys ?? (IEnumerable<string>)Array.Empty<string>())
                AddRoot(path, GameplayMeshDependencyRootKind.ConsumerPackAsset);

            var roots = rootKindsByPath.Keys
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var index = new GameplayMeshDependencyIndex(
                CanClassifyVerifiedMissingDependencies(state));
            foreach (var (rootPath, rootKinds) in rootKindsByPath)
            {
                foreach (var rootKind in rootKinds)
                    index.RegisterRoot(rootPath, rootKind);
            }

            var wsModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rootAssetPath in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var traversal = CollectGameplayMeshConsumers(
                    state,
                    rootAssetPath,
                    cancellationToken,
                    index);
                if (!traversal.IsComplete)
                {
                    foreach (var failure in traversal.Failures)
                    {
                        index.RecordFailure(
                            rootAssetPath,
                            failure.AssetPath,
                            failure.Reason,
                            failure.FailureKind,
                            failure.AssetScope);
                    }
                }

                foreach (var wsModelPath in traversal.WsModelPaths)
                    wsModels.Add(Normalize(wsModelPath));

                foreach (var rigidPath in traversal.DirectRigidPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    index.AddDirectConsumer(rigidPath, rootAssetPath);
                }
            }

            // Parse each WSModel once, after every gameplay root has been traversed. This
            // ensures a shared malformed/missing WSModel is attributed to every root that
            // references it, rather than only to the first root encountered.
            foreach (var wsModelPath in wsModels.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var referencingRoots = index.GetRootsForAsset(wsModelPath).ToArray();
                var container = FindDependencyContainer(state, wsModelPath);
                var file = container?.FindFile(wsModelPath);
                if (container == null || file == null)
                {
                    RecordFailureForRoots(
                        index,
                        referencingRoots,
                        wsModelPath,
                        "WSModel could not be resolved",
                        GameplayMeshDependencyFailureKind.MissingReference,
                        GetGameplayMeshDependencyAssetScope(
                            state,
                            wsModelPath,
                            container));
                    continue;
                }

                // BuildWsUsageIndex deliberately excludes malformed source WSModels. They
                // still consume the rigid topology, so consumer safety must fail closed.
                if (state.Source.ContainsFile(wsModelPath) &&
                    state.MalformedWsModelsIgnored.Any(entry =>
                        entry.Path.Equals(
                            wsModelPath,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    RecordFailureForRoots(
                        index,
                        referencingRoots,
                        wsModelPath,
                        "source WSModel is malformed and its geometry consumer cannot be established",
                        GameplayMeshDependencyFailureKind.ParseFailure,
                        GameplayMeshDependencyAssetScope.SourcePack);
                    continue;
                }

                // Reuse the compact effective WSModel entry used by atlas value
                // accounting. Source XML is already memoized in BatchState; for a
                // vanilla CA container this also populates the persistent consumer
                // cache, so the later value-gate pass does not parse the same WSModel
                // again.
                if (!TryGetDependencyWsModelEntry(
                        state,
                        container,
                        wsModelPath,
                        file,
                        out var wsModelEntry))
                {
                    RecordFailureForRoots(
                        index,
                        referencingRoots,
                        wsModelPath,
                        "WSModel could not be parsed",
                        GameplayMeshDependencyFailureKind.ParseFailure,
                        GetGameplayMeshDependencyAssetScope(
                            state,
                            wsModelPath,
                            container));
                    continue;
                }

                var geometryPath = wsModelEntry.GeometryPath;
                if (geometryPath.Length == 0)
                {
                    RecordFailureForRoots(
                        index,
                        referencingRoots,
                        wsModelPath,
                        "WSModel has no geometry path",
                        GameplayMeshDependencyFailureKind.InvalidReference,
                        GetGameplayMeshDependencyAssetScope(
                            state,
                            wsModelPath,
                            container));
                    continue;
                }

                if (!Path.GetExtension(geometryPath).Equals(
                        ".rigid_model_v2",
                        StringComparison.OrdinalIgnoreCase))
                {
                    RecordFailureForRoots(
                        index,
                        referencingRoots,
                        geometryPath,
                        "WSModel geometry reference has an unknown asset type",
                        GameplayMeshDependencyFailureKind.UnknownType,
                        GetGameplayMeshDependencyAssetScope(
                            state,
                            geometryPath));
                    continue;
                }

                index.AddDependency(wsModelPath, geometryPath);
                foreach (var root in referencingRoots)
                    index.AddRootReference(geometryPath, root);

                var geometryContainer = FindDependencyContainer(state, geometryPath);
                if (geometryContainer == null || geometryContainer.FindFile(geometryPath) == null)
                {
                    RecordFailureForRoots(
                        index,
                        referencingRoots,
                        geometryPath,
                        "WSModel geometry dependency could not be resolved",
                        GameplayMeshDependencyFailureKind.MissingReference,
                        GetGameplayMeshDependencyAssetScope(
                            state,
                            geometryPath,
                            geometryContainer));
                    continue;
                }

                index.AddWsModelConsumer(geometryPath, wsModelPath);
            }

            // A WSModel can be reached from several gameplay roots. Some roots are
            // processed before the WSModel is parsed and some after it, so the one-time
            // consumer indexing above cannot reliably propagate every root to the
            // geometry. Finish that propagation once all root traversals are known; this
            // lets the safety gate reject only rigids in an incomplete dependency graph
            // instead of poisoning unrelated atlas candidates in the pack.
            index.PropagateRootReferences();

            return index;
        }

        private static IReadOnlyCollection<string> GetImmutableGameplayWsModelConsumersForRigid(
            BatchState state,
            string rigidPath,
            [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        {
            rigidPath = Normalize(rigidPath);
            IReadOnlyCollection<string> immutable;
            if (state.GameplayMeshDependencyIndex != null &&
                state.GameplayMeshDependencyIndex.WsModelConsumersByRigid.TryGetValue(
                    rigidPath,
                    out var indexedConsumers))
            {
                immutable = indexedConsumers
                    .Where(path => !state.Source.ContainsFile(path) ||
                                   state.Consumers?.IsModContainer(
                                       FindDependencyContainer(state, path)) == true)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            else
            {
                immutable = state.ImmutableMeshMergeConsumersByRigid.TryGetValue(
                        rigidPath,
                        out var consumers)
                    ? consumers
                    : Array.Empty<string>();
            }

            // Other mods that use or override the rigid (or a WSModel bound to it) count like
            // vanilla WSModels: any entry blocks every layout change.
            var modBlockers = GetConsumerRigidBlockers(state, rigidPath, caller);
            return modBlockers.Count == 0
                ? immutable
                : immutable.Concat(modBlockers).ToArray();
        }

        private static bool HasDirectGameplayRigidConsumer(
            BatchState state,
            string rigidPath)
        {
            rigidPath = Normalize(rigidPath);
            if (state.GameplayMeshDependencyIndex != null)
            {
                return state.GameplayMeshDependencyIndex.DirectConsumersByRigid
                    .TryGetValue(rigidPath, out var indexedConsumers) &&
                    indexedConsumers.Count != 0;
            }

            return state.DirectRigidMeshMergeConsumersByRigid.TryGetValue(
                       rigidPath,
                       out var consumers) &&
                   consumers.Count != 0;
        }

        private static bool IsGameplayConsumerDiscoverySafeForAsset(
            BatchState state,
            string assetPath)
        {
            if (state.StructuralMergeConsumerDiscoveryComplete)
                return true;

            // A resolver/schema failure is a pack-wide safety failure. When the resolver
            // is healthy, however, an incomplete traversal only invalidates the assets
            // that are actually reachable from one of the failed roots.
            if (state.UnitCategoryResolution == null ||
                !state.UnitCategoryResolution.IsGameplayResolutionHealthy ||
                state.GameplayMeshDependencyIndex == null)
            {
                return false;
            }

            return !state.GameplayMeshDependencyIndex.IsAssetAffectedByIncompleteRoot(
                assetPath);
        }

        private static bool IsAtlasCandidateGameplayDiscoverySafe(
            BatchState state,
            AtlasCandidate candidate)
        {
            if (state.StructuralMergeConsumerDiscoveryComplete)
                return true;

            if (state.UnitCategoryResolution == null ||
                !state.UnitCategoryResolution.IsGameplayResolutionHealthy ||
                state.GameplayMeshDependencyIndex == null)
            {
                return false;
            }

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                candidate.RootVmdPath,
                candidate.Key.GeometryPath,
            };
            foreach (var usage in candidate.Usages)
            {
                if (!string.IsNullOrWhiteSpace(usage.WsModelPath))
                    paths.Add(usage.WsModelPath);
            }

            return paths.All(
                path => !state.GameplayMeshDependencyIndex.IsAssetAffectedByIncompleteRoot(
                    path));
        }

        private static void RecordStructuralMergeConsumerDiscoveryFailure(
            BatchState state,
            string path,
            string reason)
        {
            state.StructuralMergeConsumerDiscoveryComplete = false;
            state.StructuralMergeConsumerDiscoveryFailures.Add(
                $"{Normalize(path)}: {reason}");
        }

        private static bool IsMeshConsumerAssetPath(string path)
        {
            var extension = Path.GetExtension(Normalize(path));
            return extension.Equals(".wsmodel", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".rigid_model_v2", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(
                       ".variantmeshdefinition",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static GameplayMeshDependencyAssetScope
            GetGameplayMeshDependencyAssetScope(
                BatchState state,
                string assetPathValue,
                IPackFileContainer? resolvedContainer = null)
        {
            var assetPath = Normalize(assetPathValue);
            if (resolvedContainer != null)
            {
                return ReferenceEquals(resolvedContainer, state.Source)
                    ? GameplayMeshDependencyAssetScope.SourcePack
                    : GameplayMeshDependencyAssetScope.CaPack;
            }

            if (assetPath.Length != 0 && state.Source.ContainsFile(assetPath))
                return GameplayMeshDependencyAssetScope.SourcePack;

            return assetPath.Length != 0 &&
                   FindGameplayTraversalContainer(
                       state,
                       state.Source,
                       GetGameplayTraversalContainers(state),
                       assetPath) != null
                ? GameplayMeshDependencyAssetScope.CaPack
                : GameplayMeshDependencyAssetScope.Missing;
        }

        private static bool CanClassifyVerifiedMissingDependencies(BatchState state)
        {
            var containers = state.GameplayTraversalContainers;
            // The check has no unit resolution: the game packs alone make "missing everywhere" verifiable.
            return (state.CheckMode || state.UnitCategoryResolution?.IsGameplayResolutionHealthy == true) &&
                   containers != null &&
                   containers.Any(container => container.IsCaPackFile);
        }

        private static void RecordFailureForRoots(
            GameplayMeshDependencyIndex index,
            IEnumerable<string> rootPaths,
            string assetPath,
            string reason,
            GameplayMeshDependencyFailureKind failureKind,
            GameplayMeshDependencyAssetScope assetScope)
        {
            var roots = rootPaths
                .Select(Normalize)
                .Where(path => path.Length != 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (roots.Length == 0)
            {
                index.RecordFailure(
                    string.Empty,
                    assetPath,
                    reason,
                    failureKind,
                    assetScope);
                return;
            }

            foreach (var rootPath in roots)
            {
                index.RecordFailure(
                    rootPath,
                    assetPath,
                    reason,
                    failureKind,
                    assetScope);
            }
        }

        private static GameplayMeshConsumerTraversal CollectGameplayMeshConsumers(
            BatchState state,
            string rootAssetPath,
            CancellationToken cancellationToken,
            GameplayMeshDependencyIndex? dependencyIndex = null)
        {
            var result = new GameplayMeshConsumerTraversal();
            var queue = new Queue<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rootPath = Normalize(rootAssetPath);
            if (rootPath.Length == 0)
            {
                result.IsComplete = false;
                result.Failures.Add(
                    new GameplayMeshTraversalFailure(
                        "<empty root>",
                        "empty root",
                        GameplayMeshDependencyFailureKind.InvalidReference,
                        GameplayMeshDependencyAssetScope.Missing));
                return result;
            }

            dependencyIndex?.GameplayRoots.Add(rootPath);
            queue.Enqueue(rootPath);
            while (queue.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var assetPath = queue.Dequeue();
                if (!visited.Add(assetPath))
                    continue;

                dependencyIndex?.AddRootReference(assetPath, rootPath);

                var extension = Path.GetExtension(assetPath);
                if (extension.Equals(".wsmodel", StringComparison.OrdinalIgnoreCase))
                {
                    result.WsModelPaths.Add(assetPath);
                    continue;
                }

                if (extension.Equals(".rigid_model_v2", StringComparison.OrdinalIgnoreCase))
                {
                    var rigidContainer = FindDependencyContainer(state, assetPath);
                    if (rigidContainer == null || rigidContainer.FindFile(assetPath) == null)
                    {
                        result.IsComplete = false;
                        result.Failures.Add(
                            new GameplayMeshTraversalFailure(
                                assetPath,
                                "rigid model could not be resolved",
                                GameplayMeshDependencyFailureKind.MissingReference,
                                GetGameplayMeshDependencyAssetScope(
                                    state,
                                    assetPath,
                                    rigidContainer)));
                    }
                    else
                    {
                        result.DirectRigidPaths.Add(assetPath);
                    }
                    continue;
                }

                if (!extension.Equals(
                        ".variantmeshdefinition",
                        StringComparison.OrdinalIgnoreCase))
                {
                    // A gameplay root/reference without a recognized extension cannot be
                    // classified safely, regardless of whether a file happens to exist at
                    // that path. Validate the type before treating absence as verified.
                    result.IsComplete = false;
                    result.Failures.Add(
                        new GameplayMeshTraversalFailure(
                            assetPath,
                            "unrecognized asset type",
                            GameplayMeshDependencyFailureKind.UnknownType,
                            GetGameplayMeshDependencyAssetScope(
                                state,
                                assetPath)));

                    continue;
                }

                var container = FindDependencyContainer(state, assetPath);
                var file = container?.FindFile(assetPath);
                if (container == null ||
                    file == null ||
                    !TryGetVmdForTraversal(
                        state,
                        container,
                        assetPath,
                        file,
                        out var vmd))
                {
                    result.IsComplete = false;
                    result.Failures.Add(
                        new GameplayMeshTraversalFailure(
                            assetPath,
                            container == null || file == null
                                ? "VMD could not be resolved"
                                : "VMD could not be parsed",
                            container == null || file == null
                                ? GameplayMeshDependencyFailureKind.MissingReference
                                : GameplayMeshDependencyFailureKind.ParseFailure,
                            GetGameplayMeshDependencyAssetScope(
                                state,
                                assetPath,
                                container)));
                    continue;
                }

                var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var children = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectVmdReferences(vmd, models, children, textures);

                foreach (var modelValue in models)
                {
                    var modelPath = Normalize(modelValue);
                    if (modelPath.Length == 0)
                        continue;

                    var modelExtension = Path.GetExtension(modelPath);
                    var isWsModel = modelExtension.Equals(
                        ".wsmodel",
                        StringComparison.OrdinalIgnoreCase);
                    var isRigidModel = modelExtension.Equals(
                        ".rigid_model_v2",
                        StringComparison.OrdinalIgnoreCase);
                    var isVariantMeshDefinition = modelExtension.Equals(
                        ".variantmeshdefinition",
                        StringComparison.OrdinalIgnoreCase);
                    if (!isWsModel && !isRigidModel && !isVariantMeshDefinition)
                    {
                        result.IsComplete = false;
                        result.Failures.Add(
                            new GameplayMeshTraversalFailure(
                                modelPath,
                                "unrecognized model reference",
                                GameplayMeshDependencyFailureKind.UnknownType,
                                GetGameplayMeshDependencyAssetScope(
                                    state,
                                    modelPath)));
                        continue;
                    }

                    dependencyIndex?.AddDependency(assetPath, modelPath);
                    dependencyIndex?.AddRootReference(modelPath, rootPath);

                    var modelContainer = FindDependencyContainer(state, modelPath);
                    if (modelContainer == null)
                    {
                        result.IsComplete = false;
                        result.Failures.Add(
                            new GameplayMeshTraversalFailure(
                                modelPath,
                                "model dependency could not be resolved",
                                GameplayMeshDependencyFailureKind.MissingReference,
                                GameplayMeshDependencyAssetScope.Missing));
                        continue;
                    }

                    if (isWsModel)
                    {
                        result.WsModelPaths.Add(modelPath);
                    }
                    else if (isRigidModel)
                    {
                        result.DirectRigidPaths.Add(modelPath);
                    }
                    else if (isVariantMeshDefinition)
                    {
                        queue.Enqueue(modelPath);
                    }
                }

                foreach (var childValue in children)
                {
                    var childPath = Normalize(childValue);
                    if (childPath.Length == 0)
                        continue;

                    if (!Path.GetExtension(childPath).Equals(
                            ".variantmeshdefinition",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        result.IsComplete = false;
                        result.Failures.Add(
                            new GameplayMeshTraversalFailure(
                                childPath,
                                "unrecognized child VMD reference",
                                GameplayMeshDependencyFailureKind.UnknownType,
                                GetGameplayMeshDependencyAssetScope(
                                    state,
                                    childPath)));
                        continue;
                    }

                    dependencyIndex?.AddDependency(assetPath, childPath);
                    dependencyIndex?.AddRootReference(childPath, rootPath);

                    if (FindDependencyContainer(state, childPath) == null)
                    {
                        result.IsComplete = false;
                        result.Failures.Add(
                            new GameplayMeshTraversalFailure(
                                childPath,
                                "child VMD dependency could not be resolved",
                                GameplayMeshDependencyFailureKind.MissingReference,
                                GetGameplayMeshDependencyAssetScope(
                                    state,
                                    childPath)));
                        continue;
                    }

                    queue.Enqueue(childPath);
                }
            }

            return result;
        }

        private HashSet<string> LoadStructuralMergeRigidModels(
            BatchState state,
            CancellationToken cancellationToken)
        {
            var rigidPaths = new HashSet<string>(
                state.RigidModels.Keys.Select(Normalize),
                StringComparer.OrdinalIgnoreCase);

            foreach (var document in state.WsDocuments.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var geometryPath = Normalize(
                    document.SelectSingleNode("/model/geometry")?.InnerText);
                if (geometryPath.Length != 0 && state.Source.ContainsFile(geometryPath))
                    rigidPaths.Add(geometryPath);
            }

            foreach (var assetPath in state.SourceReachableAssetFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Path.GetExtension(assetPath).Equals(
                        ".rigid_model_v2",
                        StringComparison.OrdinalIgnoreCase) &&
                    state.Source.ContainsFile(assetPath))
                {
                    rigidPaths.Add(Normalize(assetPath));
                }
            }

            foreach (var rigidPath in rigidPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!state.Source.ContainsFile(rigidPath) ||
                    state.RigidModels.ContainsKey(rigidPath))
                {
                    continue;
                }

                try
                {
                    _ = GetRmv(state, rigidPath);
                }
                catch (Exception ex) when (
                    ex is InvalidDataException or
                    InvalidOperationException or
                    ArgumentException or
                    FormatException)
                {
                    RecordStructuralMergeSkip(
                        state,
                        rigidPath,
                        $"rigid model could not be loaded for structural merge analysis: {ex.Message}");
                }
            }

            return state.RigidModels.Keys
                .Where(path => state.Source.ContainsFile(path))
                .Select(Normalize)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private void AnalyzeCompatibleMeshMerges(
            BatchState state,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var rigidPaths = LoadStructuralMergeRigidModels(state, cancellationToken)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var rigidIndex = 0; rigidIndex < rigidPaths.Count; rigidIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rigidPath = rigidPaths[rigidIndex];
                ReportProgress(
                    progress,
                    "Analyzing structural mesh merges",
                    rigidIndex + 1,
                    rigidPaths.Count,
                    rigidPath);

                if (!TryGetStructuralMergeBlockReason(state, rigidPath, out _))
                    AnalyzeCompatibleMeshMergesForRigid(state, rigidPath, cancellationToken);
            }
        }

        private HashSet<string> CollectReachableAssetFiles(
            BatchState state,
            IPackFileContainer container,
            IReadOnlyList<string> rootAssetPaths,
            CancellationToken cancellationToken = default,
            IProgress<TextureAtlasPackProgress>? progress = null,
            string phase = "Scanning dependencies")
        {
            var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var assetQueue = new Queue<string>(rootAssetPaths.Select(Normalize));
            var visitedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var processedAssetCount = 0;
            void EnqueueExistingAsset(string pathValue)
            {
                var path = Normalize(pathValue);
                if (path.Length == 0 || container.FindFile(path) == null)
                    return;

                reachable.Add(path);
                assetQueue.Enqueue(path);
            }

            void AddMaterialTextures(
                string materialPath,
                PackFile materialFile)
            {
                if (!TryGetMaterialDocumentForTraversal(
                        state,
                        container,
                        materialPath,
                        materialFile,
                        out var materialDocument))
                {
                    return;
                }

                var textureNodes = materialDocument.SelectNodes("/material/textures/texture");
                if (textureNodes == null)
                    return;

                foreach (XmlNode textureNode in textureNodes)
                {
                    EnqueueExistingAsset(
                        textureNode.SelectSingleNode("source")?.InnerText ??
                        textureNode.InnerText);
                }
            }

            void AddRigidTextures(PackFile rigidFile)
            {
                try
                {
                    var rigid = ModelFactory.Create().Load(rigidFile.DataSource.ReadData());
                    foreach (var lod in rigid.ModelList)
                    {
                        foreach (var model in lod)
                        {
                            foreach (var texture in model.Material.GetAllTextures())
                                EnqueueExistingAsset(texture.Path);
                        }
                    }
                }
                catch
                {
                    // A malformed unrelated rigid should not prevent dependency scanning for
                    // the rest of the pack. Rewritten rigids are validated separately.
                }
            }

            while (assetQueue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var assetPath = assetQueue.Dequeue();
                ReportProgress(progress, phase, ++processedAssetCount, 0, assetPath);
                if (!visitedAssets.Add(assetPath))
                    continue;

                var assetFile = container.FindFile(assetPath);
                if (assetFile == null)
                    continue;

                var extension = Path.GetExtension(assetPath);
                if (extension.Equals(".variantmeshdefinition", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryGetVmdForTraversal(
                            state,
                            container,
                            assetPath,
                            assetFile,
                            out var vmd))
                    {
                        continue;
                    }

                    var modelRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var childVmdRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var directTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    CollectVmdReferences(vmd, modelRefs, childVmdRefs, directTextures);

                    foreach (var texture in directTextures)
                        EnqueueExistingAsset(texture);
                    foreach (var child in childVmdRefs)
                        EnqueueExistingAsset(child);
                    foreach (var model in modelRefs)
                        EnqueueExistingAsset(model);

                    continue;
                }

                if (extension.Equals(".rigid_model_v2", StringComparison.OrdinalIgnoreCase))
                {
                    AddRigidTextures(assetFile);
                    continue;
                }

                if (extension.Equals(".xml.material", StringComparison.OrdinalIgnoreCase))
                {
                    AddMaterialTextures(assetPath, assetFile);
                    continue;
                }

                if (!extension.Equals(".wsmodel", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!TryGetWsDocumentForTraversal(
                        state,
                        container,
                        assetPath,
                        assetFile,
                        out var wsDocument))
                {
                    continue;
                }

                EnqueueExistingAsset(wsDocument.SelectSingleNode("/model/geometry")?.InnerText ?? string.Empty);

                var materialNodes = wsDocument.SelectNodes("/model/materials/material");
                if (materialNodes == null)
                    continue;

                foreach (XmlNode materialNode in materialNodes)
                {
                    var materialPath = Normalize(materialNode.InnerText);
                    var materialFile = container.FindFile(materialPath);
                    if (materialFile == null)
                        continue;

                    reachable.Add(materialPath);
                    AddMaterialTextures(materialPath, materialFile);
                }
            }

            return reachable;
        }

        private static IReadOnlyDictionary<string, IReadOnlyCollection<string>> BuildChildVmdDependencyMap(
            BatchState state,
            IPackFileContainer container,
            IReadOnlyCollection<string> vmdPaths,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, IReadOnlyCollection<string>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var pathValue in vmdPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Normalize(pathValue);
                var file = container.FindFile(path);
                if (file == null)
                    continue;

                var vmd = GetVmd(state, container, path, file);
                var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var children = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectVmdReferences(vmd, models, children, textures);

                result[path] = children
                    .Where(container.ContainsFile)
                    .Select(Normalize)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(child => child, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            return result;
        }

        private static bool TryGetVmdForTraversal(
            BatchState state,
            IPackFileContainer container,
            string vmdPathValue,
            PackFile file,
            out VariantMesh vmd)
        {
            var vmdPath = Normalize(vmdPathValue);
            try
            {
                vmd = GetVmd(state, container, vmdPath, file);
                return true;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                XmlException or
                FormatException or
                ArgumentException)
            {
                RecordMalformedReferencedVmd(state, vmdPath, ex);
                vmd = null!;
                return false;
            }
        }

        private static void RecordMalformedReferencedVmd(
            BatchState state,
            string vmdPath,
            Exception exception)
        {
            vmdPath = Normalize(vmdPath);
            if (state.MalformedReferencedVmds.Any(
                    entry => entry.Path.Equals(vmdPath, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var messages = new List<string>();
            for (Exception? current = exception; current != null; current = current.InnerException)
            {
                var message = current.Message.Replace("\r", " ").Replace("\n", " ").Trim();
                if (message.Length != 0 &&
                    !messages.Contains(message, StringComparer.Ordinal))
                {
                    messages.Add(message);
                }
            }

            state.MalformedReferencedVmds.Add(new MalformedVmdEntry(
                vmdPath,
                messages.Count == 0
                    ? exception.GetType().Name
                    : string.Join(" -> ", messages)));
        }

        private static VariantMesh GetVmd(
            BatchState state,
            IPackFileContainer container,
            string vmdPath,
            PackFile? file = null)
        {
            vmdPath = Normalize(vmdPath);
            // A consumer's VMD may sit on a path the source also has: it is parsed for the
            // dependency index only and never shares the source's cached document.
            if (state.Consumers?.IsModContainer(container) == true)
            {
                var consumerFile = file ?? container.FindFile(vmdPath)
                    ?? throw new FileNotFoundException($"VMD file could not be resolved: {vmdPath}");
                return VariantMeshDefinitionLoader.Load(consumerFile, out _);
            }

            if (state.VmdDocuments.TryGetValue(vmdPath, out var cached))
                return cached;

            file ??= container.FindFile(vmdPath)
                ?? throw new FileNotFoundException($"VMD file could not be resolved: {vmdPath}");

            var vmd = VariantMeshDefinitionLoader.Load(
                file,
                out var compatibilityRepairs);
            RecordXmlCompatibilityRepairs(
                state,
                vmdPath,
                compatibilityRepairs);
            state.VmdDocuments[vmdPath] = vmd;
            return vmd;
        }

        private static HashSet<string> GetReachableWsModels(
            BatchState state,
            string rootVmdPath,
            CancellationToken cancellationToken = default)
        {
            rootVmdPath = Normalize(rootVmdPath);
            if (state.ReachableWsModelsByRoot.TryGetValue(rootVmdPath, out var cached))
                return cached;

            var reachable = CollectReachableWsModels(
                state,
                state.Source,
                rootVmdPath,
                cancellationToken);
            state.ReachableWsModelsByRoot[rootVmdPath] = reachable;
            state.ReachableWsModelsByRootVersion++;
            return reachable;
        }

        private static HashSet<string> CollectReachableWsModels(
            BatchState state,
            IPackFileContainer container,
            string rootVmdPath,
            CancellationToken cancellationToken = default)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            queue.Enqueue(Normalize(rootVmdPath));

            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var vmdPath = queue.Dequeue();
                if (!visited.Add(vmdPath))
                    continue;

                var file = container.FindFile(vmdPath);
                if (file == null)
                    continue;

                if (!TryGetVmdForTraversal(
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

                foreach (var model in models)
                {
                    if (Path.GetExtension(model).Equals(".wsmodel", StringComparison.OrdinalIgnoreCase) &&
                        container.FindFile(model) != null)
                    {
                        result.Add(model);
                    }
                }

                foreach (var child in children)
                {
                    if (container.FindFile(child) != null)
                        queue.Enqueue(child);
                }
            }

            return result;
        }

        private static void CollectVmdReferences(
            VariantMesh mesh,
            HashSet<string> modelRefs,
            HashSet<string> childVmdRefs,
            HashSet<string> directTextures)
        {
            if (!string.IsNullOrWhiteSpace(mesh.ModelReference))
                modelRefs.Add(Normalize(mesh.ModelReference));
            if (!string.IsNullOrWhiteSpace(mesh.ImposterModel))
                modelRefs.Add(Normalize(mesh.ImposterModel));
            if (!string.IsNullOrWhiteSpace(mesh.DecalDiffuse))
                directTextures.Add(Normalize(mesh.DecalDiffuse));
            if (!string.IsNullOrWhiteSpace(mesh.DecalNormal))
                directTextures.Add(Normalize(mesh.DecalNormal));

            foreach (var slot in mesh.ChildSlots ?? [])
            {
                foreach (var child in slot.ChildMeshes ?? [])
                    CollectVmdReferences(child, modelRefs, childVmdRefs, directTextures);
                foreach (var reference in slot.ChildReferences ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(reference.Reference))
                        childVmdRefs.Add(Normalize(reference.Reference));
                }
            }
        }

        private static bool TryGetMaterialDocumentForTraversal(
            BatchState state,
            IPackFileContainer container,
            string materialPathValue,
            PackFile file,
            out XmlDocument document)
        {
            var materialPath = Normalize(materialPathValue);
            try
            {
                if (ReferenceEquals(container, state.Source))
                {
                    document = GetMaterialDocument(state, materialPath, file);
                }
                else
                {
                    state.GameplayTraversalMaterialXmlParseCount++;
                    document = LoadXml(file, out var compatibilityRepairs);
                    RecordXmlCompatibilityRepairs(
                        state,
                        materialPath,
                        compatibilityRepairs);
                }

                return true;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                XmlException or
                FormatException or
                ArgumentException)
            {
                RecordMalformedMaterial(state, materialPath, ex);
                document = null!;
                return false;
            }
        }

        private static void RecordMalformedMaterial(
            BatchState state,
            string materialPath,
            Exception exception)
        {
            materialPath = Normalize(materialPath);
            if (state.MalformedMaterialsIgnored.Any(
                    entry => entry.Path.Equals(materialPath, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var messages = new List<string>();
            for (Exception? current = exception; current != null; current = current.InnerException)
            {
                var message = current.Message.Replace("\r", " ").Replace("\n", " ").Trim();
                if (message.Length != 0 &&
                    !messages.Contains(message, StringComparer.Ordinal))
                {
                    messages.Add(message);
                }
            }

            state.MalformedMaterialsIgnored.Add(new MalformedXmlAssetEntry(
                materialPath,
                messages.Count == 0
                    ? exception.GetType().Name
                    : string.Join(" -> ", messages)));
        }

        private static XmlDocument GetMaterialDocument(
            BatchState state,
            string materialPath,
            PackFile? materialFile = null)
        {
            materialPath = Normalize(materialPath);
            if (state.MaterialDocuments.TryGetValue(materialPath, out var cached))
                return cached;

            materialFile ??= FindForReadStatic(state, materialPath)
                ?? throw new FileNotFoundException($"Material file could not be resolved: {materialPath}");

            state.GameplayTraversalMaterialXmlParseCount++;
            var doc = LoadXml(
                materialFile,
                out var compatibilityRepairs);
            RecordXmlCompatibilityRepairs(
                state,
                materialPath,
                compatibilityRepairs);
            state.MaterialDocuments[materialPath] = doc;
            return doc;
        }

        private static PackFile? FindForReadStatic(BatchState state, string path)
        {
            path = Normalize(path);
            return state.Output.FindFile(path)
                   ?? state.Source.FindFile(path)
                   ?? state.PackFileService.FindFile(path);
        }

        private static bool TryGetWsDocumentForTraversal(
            BatchState state,
            IPackFileContainer container,
            string wsPathValue,
            PackFile file,
            out XmlDocument document)
        {
            var wsPath = Normalize(wsPathValue);
            try
            {
                if (ReferenceEquals(container, state.Source))
                {
                    var cachedDocument = GetWsDocument(state, wsPath);
                    if (cachedDocument != null)
                    {
                        document = cachedDocument;
                    }
                    else
                    {
                        state.GameplayTraversalWsModelXmlParseCount++;
                        document = LoadXml(file);
                    }
                }
                else
                {
                    state.GameplayTraversalWsModelXmlParseCount++;
                    document = LoadXml(file, out var compatibilityRepairs);
                    RecordXmlCompatibilityRepairs(
                        state,
                        wsPath,
                        compatibilityRepairs);
                }

                return true;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                XmlException or
                FormatException or
                ArgumentException)
            {
                RecordMalformedWsModel(state, wsPath, ex);
                document = null!;
                return false;
            }
        }

        private static void RecordMalformedWsModel(
            BatchState state,
            string wsPath,
            Exception exception)
        {
            wsPath = Normalize(wsPath);
            if (state.MalformedWsModelsIgnored.Any(
                    entry => entry.Path.Equals(wsPath, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var messages = new List<string>();
            for (Exception? current = exception; current != null; current = current.InnerException)
            {
                var message = current.Message.Replace("\r", " ").Replace("\n", " ").Trim();
                if (message.Length != 0 &&
                    !messages.Contains(message, StringComparer.Ordinal))
                {
                    messages.Add(message);
                }
            }

            state.MalformedWsModelsIgnored.Add(new MalformedXmlAssetEntry(
                wsPath,
                messages.Count == 0
                    ? exception.GetType().Name
                    : string.Join(" -> ", messages)));
        }

        private static XmlDocument? GetWsDocument(BatchState state, string wsPath)
        {
            wsPath = Normalize(wsPath);
            if (state.WsDocuments.TryGetValue(wsPath, out var cached))
                return cached;

            var file = state.Source.FindFile(wsPath);
            if (file == null)
                return null;

            state.GameplayTraversalWsModelXmlParseCount++;
            var doc = LoadXml(
                file,
                out var compatibilityRepairs);
            RecordXmlCompatibilityRepairs(
                state,
                wsPath,
                compatibilityRepairs);
            state.WsDocuments[wsPath] = doc;
            return doc;
        }

        private RmvFile? GetRmv(BatchState state, string geometryPath)
        {
            geometryPath = Normalize(geometryPath);
            if (state.RigidModels.TryGetValue(geometryPath, out var cached))
                return cached;

            var file = state.Source.FindFile(geometryPath);
            if (file == null)
                return null;

            var rmv = ModelFactory.Create().Load(file.DataSource.ReadData());
            state.RigidModels[geometryPath] = rmv;
            return rmv;
        }

        private PackFile? FindForRead(BatchState state, string path)
        {
            path = Normalize(path);
            return state.Output.FindFile(path)
                   ?? state.Source.FindFile(path)
                   ?? _packFileService.FindFile(path);
        }

        private void WriteFile(IPackFileContainer output, string fullPath, byte[] data)
        {
            fullPath = Normalize(fullPath);
            var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            var name = Path.GetFileName(fullPath);

            // AddFiles replaces an existing normalized path in-place. Avoid a preceding DeleteFile:
            // deletion performs a linear reverse lookup and emits an extra event/log entry.
            _packFileService.AddFilesToPack(
                output,
                [new NewPackFileEntry(directory, PackFile.CreateFromBytes(name, data))]);
        }

        private static XmlDocument LoadXml(PackFile file)
            => LoadXml(file, out _);

        private static XmlDocument LoadXml(
            PackFile file,
            out IReadOnlyList<string> compatibilityRepairs)
        {
            var content = Encoding.UTF8.GetString(file.DataSource.ReadData());
            return XmlCompatibilityParser.Parse(
                content,
                xml =>
                {
                    var document = new XmlDocument();
                    document.LoadXml(xml);
                    return document;
                },
                out compatibilityRepairs);
        }

        private static void RecordXmlCompatibilityRepairs(
            BatchState state,
            string pathValue,
            IReadOnlyList<string> repairs)
        {
            if (repairs.Count == 0)
                return;

            var path = Normalize(pathValue);
            if (!state.XmlCompatibilityRepairs.TryGetValue(path, out var recorded))
            {
                recorded = new HashSet<string>(StringComparer.Ordinal);
                state.XmlCompatibilityRepairs[path] = recorded;
            }

            foreach (var repair in repairs)
                recorded.Add(repair);
        }

        private static string GetTextureSlot(XmlNode textureNode)
            => textureNode.SelectSingleNode("slot")?.InnerText.Trim() ?? string.Empty;

        private static string GetTexturePath(XmlDocument material, string slot)
        {
            var textureNodes = material.SelectNodes("/material/textures/texture");
            if (textureNodes == null)
                return string.Empty;

            foreach (XmlNode node in textureNodes)
            {
                if (!GetTextureSlot(node).Equals(slot, StringComparison.OrdinalIgnoreCase))
                    continue;

                return Normalize(node.SelectSingleNode("source")?.InnerText ?? node.InnerText);
            }

            return string.Empty;
        }

        private static Faction3MaskCanonicalization CanonicalizeFaction3MaskParameter(
            XmlDocument material)
        {
            var parameter = material.SelectNodes("/material/params/param")?
                .Cast<XmlNode>()
                .FirstOrDefault(node =>
                    node.SelectSingleNode("name")?.InnerText.Trim().Equals(
                        "faction3_mask_channel",
                        StringComparison.OrdinalIgnoreCase) == true);
            if (parameter == null ||
                !string.Equals(
                    parameter.SelectSingleNode("type")?.InnerText.Trim(),
                    "float4",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Faction3MaskCanonicalization.None;
            }

            var valueNode = parameter.SelectSingleNode("value");
            if (valueNode == null)
                return Faction3MaskCanonicalization.None;

            var values = valueNode.InnerText
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(value => double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsed)
                        ? (double?)parsed
                        : null)
                .ToArray();
            if (values.Length != 4 || values.Any(value => !value.HasValue))
                return Faction3MaskCanonicalization.None;

            var components = values.Select(value => value!.Value).ToArray();
            const double epsilon = 0.000001;
            var isZero = components.All(value => Math.Abs(value) <= epsilon);
            var isBlue = Math.Abs(components[0]) <= epsilon &&
                         Math.Abs(components[1]) <= epsilon &&
                         Math.Abs(components[2] - 1.0) <= epsilon &&
                         Math.Abs(components[3]) <= epsilon;
            if (isZero)
            {
                valueNode.InnerText = CanonicalFaction3MaskChannelValue;
                return Faction3MaskCanonicalization.ZeroSelector;
            }

            return isBlue
                ? Faction3MaskCanonicalization.BlueSelector
                : Faction3MaskCanonicalization.None;
        }

        private static void EnsureTexturePath(XmlDocument material, string slot, string path)
        {
            if (GetTexturePath(material, slot).Length != 0)
            {
                SetTexturePath(material, slot, path);
                return;
            }

            var textures = material.SelectSingleNode("/material/textures");
            if (textures == null)
            {
                textures = material.CreateElement("textures");
                material.DocumentElement?.AppendChild(textures);
            }

            if (textures == null)
                return;

            var texture = material.CreateElement("texture");
            var slotNode = material.CreateElement("slot");
            slotNode.SetAttribute("version", "2");
            slotNode.InnerText = slot;
            texture.AppendChild(slotNode);

            var sourceNode = material.CreateElement("source");
            sourceNode.InnerText = path;
            texture.AppendChild(sourceNode);

            // Most WH3 material XMLs place mask immediately after base colour and before
            // material-map/normal. Use that stable position for a synthesized mask so the
            // generated material can share identity with an otherwise identical masked one.
            var requestedOrder = GetCanonicalAtlasTextureOrder(slot);
            var insertBefore = textures.ChildNodes
                .Cast<XmlNode>()
                .FirstOrDefault(node =>
                {
                    if (!node.Name.Equals("texture", StringComparison.OrdinalIgnoreCase))
                        return false;

                    var existingOrder = GetCanonicalAtlasTextureOrder(GetTextureSlot(node));
                    return requestedOrder >= 0 &&
                           existingOrder > requestedOrder;
                });
            if (insertBefore != null)
                textures.InsertBefore(texture, insertBefore);
            else
                textures.AppendChild(texture);
        }

        private static int GetCanonicalAtlasTextureOrder(string slot)
        {
            if (slot.Equals("t_xml_base_colour", StringComparison.OrdinalIgnoreCase))
                return 0;
            if (slot.Equals("t_xml_mask", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (slot.Equals("t_xml_material_map", StringComparison.OrdinalIgnoreCase))
                return 2;
            if (slot.Equals("t_xml_normal", StringComparison.OrdinalIgnoreCase))
                return 3;

            return -1;
        }

        private static void SetTexturePath(XmlDocument material, string slot, string path)
        {
            var textureNodes = material.SelectNodes("/material/textures/texture");
            if (textureNodes == null)
                return;

            foreach (XmlNode node in textureNodes)
            {
                if (!GetTextureSlot(node).Equals(slot, StringComparison.OrdinalIgnoreCase))
                    continue;

                var source = node.SelectSingleNode("source");
                if (source != null)
                    source.InnerText = path;
                else
                    node.InnerText = path;
                return;
            }
        }

        private static void RemoveTextureSlot(XmlDocument material, string slot)
        {
            var textureNodes = material.SelectNodes("/material/textures/texture");
            if (textureNodes == null)
                return;

            foreach (var node in textureNodes
                         .Cast<XmlNode>()
                         .Where(node => GetTextureSlot(node).Equals(
                             slot,
                             StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                node.ParentNode?.RemoveChild(node);
            }
        }
    }
}
