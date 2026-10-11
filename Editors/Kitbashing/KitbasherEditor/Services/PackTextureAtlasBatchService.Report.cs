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
        private static string FormatMiB(double bytes, int decimals = 1)
            => $"{(bytes / (1024.0 * 1024.0)).ToString($"N{decimals}")} MiB";

        private static string[] BuildCrossRigidPayloadCostCurveLines(
            IEnumerable<CrossRigidPayloadSelectionEntry> acceptedPayloads)
        {
            var ordered = acceptedPayloads
                .OrderBy(entry => entry.PhysicalBytesPerExpectedDraw)
                .ThenBy(
                    entry => entry.GeneratedPayloadId,
                    StringComparer.Ordinal)
                .ToArray();
            if (ordered.Length == 0)
                return ["  (none)"];

            var cumulativeDraws = new double[ordered.Length];
            var cumulativeBytes = new long[ordered.Length];
            double runningDraws = 0;
            long runningBytes = 0;
            for (var index = 0; index < ordered.Length; index++)
            {
                runningDraws +=
                    ordered[index].MarginalExpectedArmyDrawCallsEliminated;
                runningBytes += ordered[index].GeneratedGeometryBytes;
                cumulativeDraws[index] = runningDraws;
                cumulativeBytes[index] = runningBytes;
            }

            var lines = new List<string>();
            foreach (var share in new[] { 0.50, 0.75, 0.90, 0.95, 0.99, 1.0 })
            {
                var target = share * runningDraws;
                var pointIndex = Array.FindIndex(
                    cumulativeDraws,
                    draws => draws >= target - (runningDraws * 1e-9));
                if (pointIndex < 0)
                    pointIndex = ordered.Length - 1;

                lines.Add(
                    $"  {share:P0} of marginal draws: " +
                    $"{pointIndex + 1:N0} payload(s), " +
                    $"cumulative marginal={cumulativeDraws[pointIndex]:0.###} expected draw(s), " +
                    $"cumulative physical={FormatMiB(cumulativeBytes[pointIndex])}, " +
                    $"last payload physical per expected draw=" +
                    FormatMiB(
                        ordered[pointIndex].PhysicalBytesPerExpectedDraw,
                        2));
            }

            return lines.ToArray();
        }

        private static int CountCrossRigidPayloadsWithDroppedComponents(
            IEnumerable<CrossRigidMergeAnalysisEntry> plans)
        {
            // A generated LOD with fewer active components than LOD 0 means
            // a source was dropped at range instead of using its last LOD.
            return plans
                .GroupBy(
                    plan => plan.GeneratedPayloadId,
                    StringComparer.Ordinal)
                .Count(group => group.Any(plan =>
                {
                    var lod0 = plan.Lods.FirstOrDefault(lod =>
                        lod.LodIndex == 0);
                    return lod0 != null &&
                           plan.Lods.Any(lod =>
                               lod.LodIndex > 0 &&
                               lod.ActiveComponentCount <
                               lod0.ActiveComponentCount);
                }));
        }

        private static void AppendAtlasValueGateSourceTextureResidencyDiagnostics(
            StringBuilder sb,
            BatchState state)
        {
            var caOwnedDiagnostics = state
                .AtlasValueGateSourceTextureResidencyDiagnostics
                .Values
                .Where(diagnostic => !diagnostic.IsOwnedBySourcePack)
                .OrderBy(diagnostic => diagnostic.TexturePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            sb.AppendLine();
            sb.AppendLine(
                "Atlas value-gate accepted CA-owned source-texture residency diagnostics");
            sb.AppendLine(
                "--------------------------------------------------------------------------");
            if (caOwnedDiagnostics.Length == 0)
            {
                sb.AppendLine("  (none)");
                return;
            }

            sb.AppendLine(
                $"  textures={caOwnedDiagnostics.Length:N0}, " +
                $"current={FormatMiB(caOwnedDiagnostics.Sum(
                    diagnostic => diagnostic.CurrentExpectedResidentBcnBytes), 3)}, " +
                $"after={FormatMiB(caOwnedDiagnostics.Sum(
                    diagnostic => diagnostic.ProposedExpectedResidentBcnBytes), 3)}, " +
                $"scenario-retired={FormatMiB(caOwnedDiagnostics.Sum(
                    diagnostic => diagnostic.ExpectedRetiredBcnBytes), 3)}");
            foreach (var diagnostic in caOwnedDiagnostics)
            {
                sb.AppendLine(
                    $"  {diagnostic.TexturePath}: " +
                    $"BCn={FormatMiB(diagnostic.BcnBytes, 3)}, " +
                    $"refs={diagnostic.ReferenceCount:N0}, " +
                    $"rewritten={diagnostic.RewrittenReferenceCountBefore:N0}->" +
                    $"{diagnostic.RewrittenReferenceCountAfter:N0}, " +
                    $"direct-vmd={(diagnostic.HasDirectVmdReference ? "YES" : "NO")}, " +
                    $"direct-source-vmd={(diagnostic.HasDirectSourcePackVmdReference ? "YES" : "NO")}, " +
                    $"mapping={(diagnostic.ConsumerMappingResolved ? "resolved" : "conservative")}");
                sb.AppendLine(
                    $"    residency: P={diagnostic.CurrentResidentProbability:0.000000}->" +
                    $"{diagnostic.ProposedResidentProbability:0.000000}, " +
                    $"expected={FormatMiB(diagnostic.CurrentExpectedResidentBcnBytes, 3)}->" +
                    $"{FormatMiB(diagnostic.ProposedExpectedResidentBcnBytes, 3)}, " +
                    $"retired={FormatMiB(diagnostic.ExpectedRetiredBcnBytes, 3)}");
                sb.AppendLine(
                    $"    consumers before: " +
                    FormatAtlasValueGateConsumerPaths(diagnostic.CurrentConsumerWsModels));
                sb.AppendLine(
                    $"    consumers after: " +
                    FormatAtlasValueGateConsumerPaths(diagnostic.ProposedConsumerWsModels));
                sb.AppendLine(
                    $"    scenario before: {diagnostic.CurrentScenarioProjection}");
                sb.AppendLine(
                    $"    scenario after: {diagnostic.ProposedScenarioProjection}");
            }
        }

        private static string FormatAtlasValueGateConsumerPaths(
            IReadOnlyList<string> paths)
        {
            if (paths.Count == 0)
                return "<none>";

            const int maxDisplayedPaths = 4;
            var displayed = paths
                .Take(maxDisplayedPaths)
                .ToArray();
            var suffix = paths.Count > maxDisplayedPaths
                ? $", ... (+{paths.Count - maxDisplayedPaths:N0})"
                : string.Empty;
            return string.Join(", ", displayed) + suffix;
        }

        private static double GetAtlasValueGateMiBPerDraw(
            double netBytes,
            double drawsEliminated)
        {
            if (drawsEliminated <= 0.000001)
                return 0;

            return Math.Max(0.0, netBytes) /
                   (1024.0 * 1024.0) /
                   drawsEliminated;
        }

        private static double GetAtlasValueGateWorstMiBPerDraw(
            AtlasValueGateBatchEconomics entry)
        {
            var global = GetAtlasValueGateMiBPerDraw(
                entry.NetBcnBytes,
                entry.RawDrawsEliminated);
            var expected = entry.ExpectedArmyDrawsEliminated > 0.000001
                ? GetAtlasValueGateMiBPerDraw(
                    entry.ExpectedArmyNetBcnBytes,
                    entry.ExpectedArmyDrawsEliminated)
                : 0;
            return Math.Max(global, expected);
        }

        private static string FormatAtlasValueGateMiBPerDraw(
            double netBytes,
            double drawsEliminated)
            => drawsEliminated <= 0.000001
                ? "N/A"
                : $"{GetAtlasValueGateMiBPerDraw(netBytes, drawsEliminated):N3} MiB/draw";

        private static string FormatResidencyDelta(double before, double after)
        {
            var deltaBytes = after - before;
            if (before <= 0)
                return $"{(deltaBytes >= 0 ? "+" : string.Empty)}{FormatMiB(deltaBytes)}";

            var deltaPercent = deltaBytes / before * 100.0;
            return $"{(deltaBytes >= 0 ? "+" : string.Empty)}{FormatMiB(deltaBytes)} " +
                   $"({(deltaPercent >= 0 ? "+" : string.Empty)}{deltaPercent:N1}%)";
        }

        private static string FormatBcnBreakdown(BcnTextureResidencySummary summary)
        {
            if (summary.BytesByFormat.Count == 0)
                return "<none>";

            return string.Join(
                ", ",
                summary.BytesByFormat
                    .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(entry =>
                        $"{entry.Key}={FormatMiB(entry.Value)} " +
                        $"({summary.TextureCountByFormat.GetValueOrDefault(entry.Key):N0})"));
        }

        private static void AppendDiagnosticCounts(
            StringBuilder sb,
            string title,
            IReadOnlyDictionary<string, int> counts,
            int maxEntries)
        {
            sb.AppendLine($"  {title}:");
            if (counts.Count == 0)
            {
                sb.AppendLine("    (none)");
                return;
            }

            foreach (var entry in counts
                         .OrderByDescending(x => x.Value)
                         .ThenBy(x => x.Key, StringComparer.Ordinal)
                         .Take(maxEntries))
            {
                sb.AppendLine($"    {entry.Key}: {entry.Value}");
            }

            if (counts.Count > maxEntries)
                sb.AppendLine($"    ... {counts.Count - maxEntries} more");
        }

        private static void AppendGameplayMeshDependencyDiagnostics(
            StringBuilder sb,
            BatchState state)
        {
            sb.AppendLine("Gameplay mesh-consumer discovery diagnostics");
            sb.AppendLine("--------------------------------------------");

            var index = state.GameplayMeshDependencyIndex;
            if (index == null)
            {
                sb.AppendLine("(not available)");
                sb.AppendLine();
                return;
            }

            var containerSnapshot = state.GameplayTraversalContainers;
            var blockingFailures = index.BlockingFailureDetails.ToArray();
            var verifiedMissingFailures = index.VerifiedMissingFailureDetails.ToArray();
            var caContainerCount = containerSnapshot?.Count(container => container.IsCaPackFile) ?? 0;
            sb.AppendLine(
                $"Gameplay container snapshot: {(containerSnapshot == null ? "UNAVAILABLE" : "CAPTURED")} " +
                $"({containerSnapshot?.Count ?? 0:N0} container(s), {caContainerCount:N0} CA container(s))");
            sb.AppendLine(
                $"Verified-missing classification: {(index.VerifiedMissingClassificationEnabled ? "ENABLED" : "DISABLED")}");
            sb.AppendLine($"Discovery roots: {index.GameplayRoots.Count}");
            sb.AppendLine($"Incomplete roots: {index.IncompleteRoots.Count}");
            sb.AppendLine($"Failure details: {index.FailureDetails.Count}");
            sb.AppendLine($"Blocking failure details: {blockingFailures.Length}");
            sb.AppendLine($"Verified-missing dependency details: {verifiedMissingFailures.Length}");
            sb.AppendLine(
                $"Verified-missing dependency paths: " +
                $"{verifiedMissingFailures
                    .Select(failure => failure.AssetPath)
                    .Where(path => path.Length != 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count()}");
            sb.AppendLine(
                $"Discovery-blocked assets: " +
                $"{state.ConsumerDiscoveryBlocks.Select(x => x.AssetPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count()}");
            sb.AppendLine(
                $"Discovery-blocked structural assets: " +
                $"{state.ConsumerDiscoveryBlocks
                    .Where(x => x.BlockKind.Equals(
                        "StructuralMerge",
                        StringComparison.Ordinal))
                    .Select(x => x.AssetPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count()}");
            sb.AppendLine(
                $"Discovery-blocked atlas candidates: " +
                $"{state.ConsumerDiscoveryBlocks
                    .Where(x => x.BlockKind.Equals(
                        "AtlasRewrite",
                        StringComparison.Ordinal))
                    .Select(x => x.AssetPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count()}");

            var rootOriginCounts = index.RootKindsByPath
                .SelectMany(entry => entry.Value.Select(kind =>
                    FormatGameplayMeshDependencyRootKind(kind)))
                .GroupBy(value => value, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            AppendDiagnosticCounts(
                sb,
                "Root origins",
                rootOriginCounts,
                maxEntries: 16);

            var incompleteRootOriginCounts = index.IncompleteRoots
                .SelectMany(root => index.GetRootKinds(root)
                    .Select(kind => FormatGameplayMeshDependencyRootKind(kind)))
                .GroupBy(value => value, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            AppendDiagnosticCounts(
                sb,
                "Incomplete root origins",
                incompleteRootOriginCounts,
                maxEntries: 16);

            var failureScopeCounts = index.FailureDetails
                .GroupBy(
                    failure => FormatGameplayMeshDependencyAssetScope(failure.AssetScope),
                    StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            AppendDiagnosticCounts(
                sb,
                "Failure asset ownership",
                failureScopeCounts,
                maxEntries: 8);

            var failureReasonCounts = index.FailureDetails
                .GroupBy(failure => failure.Reason, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            AppendDiagnosticCounts(
                sb,
                "Failure reasons",
                failureReasonCounts,
                maxEntries: 32);

            AppendGameplayMeshDependencyFailureDetails(
                sb,
                "Blocking failure details",
                index,
                blockingFailures);
            AppendGameplayMeshDependencyFailureDetails(
                sb,
                "Verified-missing dependency details",
                index,
                verifiedMissingFailures);

            sb.AppendLine();
            sb.AppendLine("Discovery-blocked assets");
            sb.AppendLine("------------------------");
            if (state.ConsumerDiscoveryBlocks.Count == 0)
            {
                sb.AppendLine("(none)");
            }
            else
            {
                foreach (var block in state.ConsumerDiscoveryBlocks
                             .OrderBy(entry => entry.BlockKind, StringComparer.Ordinal)
                             .ThenBy(entry => entry.AssetPath, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(entry => entry.IncompleteRootPath, StringComparer.OrdinalIgnoreCase))
                {
                    var rootOrigins = index.GetRootKinds(block.IncompleteRootPath)
                        .Select(FormatGameplayMeshDependencyRootKind)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray();
                    sb.AppendLine(
                        $"  kind={block.BlockKind} | " +
                        $"asset={FormatDiagnosticPath(block.AssetPath)} | " +
                        $"incomplete-root={FormatDiagnosticPath(block.IncompleteRootPath)} | " +
                        $"origins={(rootOrigins.Length == 0 ? "Unknown" : string.Join(",", rootOrigins))}");
                }
            }

            sb.AppendLine();
        }

        private static void AppendGameplayMeshDependencyFailureDetails(
            StringBuilder sb,
            string title,
            GameplayMeshDependencyIndex index,
            IEnumerable<GameplayMeshDependencyFailure> failures)
        {
            sb.AppendLine(title);
            sb.AppendLine(new string('-', title.Length));
            var orderedFailures = failures
                .OrderBy(entry => entry.RootPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.AssetPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Reason, StringComparer.Ordinal)
                .ThenBy(entry => entry.AssetScope)
                .ToArray();
            if (orderedFailures.Length == 0)
            {
                sb.AppendLine("(none)");
                return;
            }

            foreach (var failure in orderedFailures)
            {
                var rootOrigins = index.GetRootKinds(failure.RootPath)
                    .Select(FormatGameplayMeshDependencyRootKind)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                sb.AppendLine(
                    $"  root={FormatDiagnosticPath(failure.RootPath)} | " +
                    $"origins={(rootOrigins.Length == 0 ? "Unknown" : string.Join(",", rootOrigins))} | " +
                    $"asset={FormatDiagnosticPath(failure.AssetPath)} | " +
                    $"kind={failure.FailureKind} | " +
                    $"disposition={failure.Disposition} | " +
                    $"owner={FormatGameplayMeshDependencyAssetScope(failure.AssetScope)} | " +
                    $"reason={failure.Reason}");
            }
        }

        private static string FormatGameplayMeshDependencyRootKind(
            GameplayMeshDependencyRootKind rootKind)
            => rootKind switch
            {
                GameplayMeshDependencyRootKind.SourcePackVmd => "SourcePackVmd",
                GameplayMeshDependencyRootKind.ResolvedRosterVmd => "ResolvedRosterVmd",
                GameplayMeshDependencyRootKind.ResidencyAsset => "ResidencyAsset",
                GameplayMeshDependencyRootKind.UnresolvedResolverVmd => "UnresolvedResolverVmd",
                GameplayMeshDependencyRootKind.DirectGameplayAsset => "DirectGameplayAsset",
                _ => rootKind.ToString(),
            };

        private static string FormatGameplayMeshDependencyAssetScope(
            GameplayMeshDependencyAssetScope assetScope)
            => assetScope switch
            {
                GameplayMeshDependencyAssetScope.SourcePack => "SourcePack",
                GameplayMeshDependencyAssetScope.CaPack => "CaPack",
                GameplayMeshDependencyAssetScope.Missing => "Missing",
                _ => assetScope.ToString(),
            };

        private static string FormatGameplayMeshDependencyFailure(
            GameplayMeshDependencyFailure failure)
            => $"{(failure.RootPath.Length == 0 ? "<unknown-root>" : failure.RootPath)} -> " +
               $"{(failure.AssetPath.Length == 0 ? "<unknown-asset>" : failure.AssetPath)}: " +
               $"{failure.Reason} (kind={failure.FailureKind}, " +
               $"disposition={failure.Disposition}, scope={failure.AssetScope})";

        private static string FormatDiagnosticPath(string path)
            => string.IsNullOrWhiteSpace(path) ? "<unknown>" : path;

        private static AtlasComponentReuseAnalysis BuildAtlasComponentReuseAnalysis(
            BatchState state)
        {
            var batchesByWsModel = new Dictionary<string, HashSet<int>>(
                StringComparer.OrdinalIgnoreCase);
            var geometryByWsModel = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var (batchId, batch) in state.AtlasBatchDiagnostics)
            {
                foreach (var candidate in batch.Candidates)
                {
                    foreach (var usage in candidate.Usages)
                    {
                        var wsModelPath = Normalize(usage.AssetPath);
                        if (string.IsNullOrWhiteSpace(wsModelPath))
                            continue;

                        if (!batchesByWsModel.TryGetValue(wsModelPath, out var batchIds))
                        {
                            batchIds = [];
                            batchesByWsModel[wsModelPath] = batchIds;
                        }

                        batchIds.Add(batchId);
                        geometryByWsModel.TryAdd(wsModelPath, candidate.Key.GeometryPath);
                    }
                }
            }

            if (batchesByWsModel.Count == 0)
            {
                return new AtlasComponentReuseAnalysis(
                    [],
                    [],
                    0,
                    0);
            }

            var rootsByWsModel = new Dictionary<string, HashSet<string>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var (rootVmdPath, reachableWsModels) in state.ReachableWsModelsByRoot)
            {
                var root = Normalize(rootVmdPath);
                foreach (var reachableWsModel in reachableWsModels)
                {
                    var wsModelPath = Normalize(reachableWsModel);
                    if (!batchesByWsModel.ContainsKey(wsModelPath))
                        continue;

                    if (!rootsByWsModel.TryGetValue(wsModelPath, out var roots))
                    {
                        roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        rootsByWsModel[wsModelPath] = roots;
                    }

                    roots.Add(root);
                }
            }

            var rootsByGeometry = new Dictionary<string, HashSet<string>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var (wsModelPath, roots) in rootsByWsModel)
            {
                if (!geometryByWsModel.TryGetValue(wsModelPath, out var geometryPath))
                    continue;

                if (!rootsByGeometry.TryGetValue(geometryPath, out var geometryRoots))
                {
                    geometryRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    rootsByGeometry[geometryPath] = geometryRoots;
                }

                geometryRoots.UnionWith(roots);
            }

            var components = batchesByWsModel.Keys
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(wsModelPath =>
                {
                    rootsByWsModel.TryGetValue(wsModelPath, out var roots);
                    geometryByWsModel.TryGetValue(wsModelPath, out var geometryPath);
                    return new AtlasComponentReuseEntry(
                        wsModelPath,
                        geometryPath ?? string.Empty,
                        roots?.Count ?? 0,
                        batchesByWsModel[wsModelPath]
                            .OrderBy(x => x)
                            .ToArray());
                })
                .ToList();

            // Count root overlap only for atlased components on different geometry assets and
            // with different atlas-batch membership. This focuses the report on relationships
            // relevant to a future cross-component/pair-specific rebake instead of same-rigid
            // parts that inherently travel together.
            var sharedRootCounts = new Dictionary<(string Left, string Right), int>();
            foreach (var (_, reachableWsModels) in state.ReachableWsModelsByRoot)
            {
                var models = reachableWsModels
                    .Select(Normalize)
                    .Where(batchesByWsModel.ContainsKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var reusedModels = models
                    .Where(model =>
                        rootsByWsModel.TryGetValue(model, out var roots) &&
                        roots.Count > 1)
                    .ToList();
                var seenPairsInRoot = new HashSet<(string Left, string Right)>();

                // One-off <> one-off combinations cannot demonstrate lost reuse, so do not
                // spend quadratic work on them. Pair every reusable component with the other
                // atlased components reachable from the same root.
                foreach (var reusable in reusedModels)
                {
                    if (!geometryByWsModel.TryGetValue(reusable, out var reusableGeometry))
                        continue;

                    foreach (var other in models)
                    {
                        if (reusable.Equals(other, StringComparison.OrdinalIgnoreCase) ||
                            !geometryByWsModel.TryGetValue(other, out var otherGeometry) ||
                            reusableGeometry.Equals(otherGeometry, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (batchesByWsModel[reusable].SetEquals(batchesByWsModel[other]))
                            continue;

                        var key = StringComparer.OrdinalIgnoreCase.Compare(reusable, other) <= 0
                            ? (reusable, other)
                            : (other, reusable);
                        if (!seenPairsInRoot.Add(key))
                            continue;

                        sharedRootCounts[key] = sharedRootCounts.GetValueOrDefault(key) + 1;
                    }
                }
            }

            var pairs = new List<AtlasComponentReusePairEntry>();
            foreach (var (key, sharedRootCount) in sharedRootCounts)
            {
                if (!rootsByWsModel.TryGetValue(key.Left, out var leftRoots) ||
                    !rootsByWsModel.TryGetValue(key.Right, out var rightRoots) ||
                    leftRoots.Count == 0 ||
                    rightRoots.Count == 0)
                {
                    continue;
                }

                var leftCoverage = sharedRootCount / (double)leftRoots.Count;
                var rightCoverage = sharedRootCount / (double)rightRoots.Count;
                var stronglyAsymmetric =
                    (leftCoverage >= 0.8 && rightCoverage <= 0.5) ||
                    (rightCoverage >= 0.8 && leftCoverage <= 0.5);
                var highBidirectionalOverlap =
                    leftCoverage >= 0.8 &&
                    rightCoverage >= 0.8;

                pairs.Add(new AtlasComponentReusePairEntry(
                    key.Left,
                    key.Right,
                    geometryByWsModel[key.Left],
                    geometryByWsModel[key.Right],
                    leftRoots.Count,
                    rightRoots.Count,
                    sharedRootCount,
                    leftCoverage,
                    rightCoverage,
                    batchesByWsModel[key.Left].OrderBy(x => x).ToArray(),
                    batchesByWsModel[key.Right].OrderBy(x => x).ToArray(),
                    stronglyAsymmetric,
                    highBidirectionalOverlap));
            }

            return new AtlasComponentReuseAnalysis(
                components,
                pairs,
                rootsByGeometry.Count,
                rootsByGeometry.Values.Count(roots => roots.Count > 1));
        }

        private static AtlasResidencySummary BuildAtlasResidencySummary(
            BatchState state)
        {
            var physicalPages = state.AtlasPhysicalPageDiagnostics;
            var allCandidates = physicalPages
                .SelectMany(page => page.Candidates)
                .GroupBy(candidate => candidate.Key)
                .Select(group => group.First())
                .ToList();
            if (allCandidates.Count == 0)
            {
                return new AtlasResidencySummary(
                    0,
                    0,
                    0,
                    0,
                    string.Empty,
                    0);
            }

            var rootsByMesh = BuildCandidateRootVmdPaths(state, allCandidates);
            var pixelsByRoot = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            long globalPixels = 0;
            long aggregateResidentPixels = 0;
            var multiRootPages = 0;
            var maxRootsPerPage = 0;

            foreach (var page in physicalPages)
            {
                globalPixels = checked(globalPixels + page.PixelCost);

                var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var candidate in page.Candidates)
                {
                    if (rootsByMesh.TryGetValue(candidate.Key, out var candidateRoots))
                        roots.UnionWith(candidateRoots);
                    else
                        roots.Add(Normalize(candidate.RootVmdPath));
                }

                var rootCount = Math.Max(1, roots.Count);
                maxRootsPerPage = Math.Max(maxRootsPerPage, rootCount);
                if (rootCount > 1)
                    multiRootPages++;

                aggregateResidentPixels = checked(
                    aggregateResidentPixels +
                    GetAtlasResidencyProxy(page.PixelCost, rootCount));

                foreach (var root in roots)
                {
                    pixelsByRoot[root] = checked(
                        pixelsByRoot.GetValueOrDefault(root) +
                        page.PixelCost);
                }
            }

            var worstRoot = pixelsByRoot
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            return new AtlasResidencySummary(
                globalPixels,
                aggregateResidentPixels,
                multiRootPages,
                maxRootsPerPage,
                worstRoot.Key ?? string.Empty,
                worstRoot.Value);
        }

        private static void WriteReport(
            BatchState state,
            IReadOnlyList<string> vmdRoots,
            bool succeeded,
            Exception? failure)
        {
            var componentReuse = BuildAtlasComponentReuseAnalysis(state);
            var residency = BuildAtlasResidencySummary(state);
            var sb = new StringBuilder();
            sb.AppendLine("Texture Atlas Pack Report");
            sb.AppendLine("=========================");
            sb.AppendLine($"Status: {(succeeded ? "SUCCESS" : "FAILED")}");
            sb.AppendLine($"Source: {state.SourcePath}");
            sb.AppendLine($"Output: {state.OutputPath}");
            sb.AppendLine($"Report: {state.ReportPath}");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            sb.AppendLine("Summary");
            sb.AppendLine("-------");
            sb.AppendLine($"Validated source VMD roots: {vmdRoots.Count}");
            sb.AppendLine(
                $"Atlas VMD population: {(state.AtlasAllVmdsEnabled ? "PACK-WIDE" : "GAMEPLAY-USED")}");
            sb.AppendLine($"Atlas VMD roots: {state.AtlasVmdRootCount:N0} / {state.SourceVmdRootCount:N0}");
            if (state.UnitCategoryResolution != null)
            {
                if (state.ArmyResidencyModel != null)
                {
                    sb.AppendLine(
                        $"Atlas value-model roster scope: culture-local " +
                        $"({(state.ArmyResidencyModel.IncludesAllGameCultures ? "all game cultures" : "pack-affected cultures")})");
                    sb.AppendLine(
                        $"Atlas value-model player cultures: {state.ArmyResidencyModel.PlayerCultureWeights.Count:N0} " +
                        $"[{string.Join(", ", state.ArmyResidencyModel.PlayerCultureWeights.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))}]");
                    sb.AppendLine(
                        $"Atlas value-model opponent cultures: {state.ArmyResidencyModel.OpponentCultureWeights.Count:N0} " +
                        "(uniform across the resolved game roster; unrelated cultures contribute zero for culture-local assets).");
                    sb.AppendLine(
                        "Vanilla-owned visual assets: read-only residency consumers (never rewritten; never physically retired).");
                    sb.AppendLine(
                        "Atlas value-gate pack-local retirement scope: source-pack WSModel/rigid " +
                        "rewrites; source-pack direct VMD references still block retirement, while " +
                        "vanilla-only consumers remain residency-only.");
                }
                var gameplayEligibleVmdCount = state.UnitCategoryResolution.UsagesByVmd.Keys
                    .Count(state.UnitCategoryResolution.IsVmdUsageComplete);
                sb.AppendLine(
                    $"Gameplay-used VMD roots: " +
                    $"{gameplayEligibleVmdCount:N0} / {vmdRoots.Count:N0}");
                sb.AppendLine(
                    $"VMD roots tainted by unresolved gameplay consumers: " +
                    $"{state.UnitCategoryResolution.UnresolvedConsumersByVmd.Count:N0}");
                sb.AppendLine(
                    $"VMD roots excluded from gameplay-used atlasing: " +
                    $"{Math.Max(0, vmdRoots.Count - gameplayEligibleVmdCount):N0}");
                sb.AppendLine(
                    $"Unit-category DB table files read: " +
                    $"{state.UnitCategoryResolution.TableFilesRead}");
                sb.AppendLine(
                    $"Gameplay resolver global health: " +
                    $"{(state.UnitCategoryResolution.IsGameplayResolutionHealthy ? "HEALTHY" : "UNHEALTHY")}");
                if (!string.IsNullOrWhiteSpace(
                        state.UnitCategoryResolution.GameplayResolutionHealthMessage))
                {
                    sb.AppendLine(
                        $"Gameplay resolver health detail: " +
                        state.UnitCategoryResolution.GameplayResolutionHealthMessage);
                }

                var directConsumerCounts = state.UnitCategoryResolution.DirectUsagesByVmd.Values
                    .SelectMany(usages => usages)
                    .SelectMany(usage => usage.Provenance)
                    .GroupBy(provenance => provenance.ConsumerType)
                    .OrderBy(group => group.Key)
                    .Select(group => $"{group.Key}={group.Count():N0}")
                    .ToArray();
                sb.AppendLine(
                    $"Direct gameplay VMD consumer links: " +
                    $"{(directConsumerCounts.Length == 0 ? "(none)" : string.Join(", ", directConsumerCounts))}");
                sb.AppendLine(
                    $"Gameplay VMD roots added through child propagation: " +
                    $"{state.UnitCategoryResolution.PropagatedVmdCount:N0}");
            }
            sb.AppendLine($"XML files repaired for compatibility: {state.XmlCompatibilityRepairs.Count}");
            sb.AppendLine($"Malformed VMD root files ignored: {state.MalformedVmdRoots.Count}");
            sb.AppendLine($"Malformed referenced VMD files ignored: {state.MalformedReferencedVmds.Count}");
            sb.AppendLine($"Malformed WSModels ignored: {state.MalformedWsModelsIgnored.Count}");
            sb.AppendLine($"Malformed material XML files ignored: {state.MalformedMaterialsIgnored.Count}");
            sb.AppendLine(
                $"Gameplay traversal container resolutions: " +
                $"{state.GameplayTraversalContainerResolutions.Count:N0} unique path(s); " +
                $"{state.GameplayTraversalContainerResolutionScans:N0} scan(s), " +
                $"{state.GameplayTraversalContainerResolutionCacheHits:N0} cache hit(s)");
            sb.AppendLine(
                $"Atlas value-gate reachable WSModels: " +
                $"{state.AtlasValueGateReachableWsModelCount:N0} total " +
                $"(source={state.AtlasValueGateSourceWsModelCount:N0}, " +
                $"CA={state.AtlasValueGateCaWsModelCount:N0}, " +
                $"unresolved={state.AtlasValueGateUnresolvedWsModelCount:N0})");
            sb.AppendLine(
                $"Atlas value-gate direct consumer paths: " +
                $"rigid={state.AtlasValueGateDirectRigidPathCount:N0}, " +
                $"VMD={state.AtlasValueGateDirectVmdPathCount:N0}");
            sb.AppendLine(
                $"Atlas value-gate WSModel consumer cache: " +
                $"{state.AtlasValueGateWsModelConsumerCacheHits:N0} hit(s) " +
                $"(memory={state.AtlasValueGateWsModelConsumerMemoryCacheHits:N0}, " +
                $"vanilla={state.AtlasValueGateWsModelConsumerVanillaCacheHits:N0}), " +
                $"{state.AtlasValueGateWsModelConsumerCacheMisses:N0} miss(es), " +
                $"{state.AtlasValueGateWsModelConsumerEntryBuilds:N0} entry build(s)");
            sb.AppendLine(
                $"Atlas value-gate material consumer cache: " +
                $"{state.AtlasValueGateMaterialConsumerCacheHits:N0} hit(s) " +
                $"(memory={state.AtlasValueGateMaterialConsumerMemoryCacheHits:N0}, " +
                $"vanilla={state.AtlasValueGateMaterialConsumerVanillaCacheHits:N0}), " +
                $"{state.AtlasValueGateMaterialConsumerCacheMisses:N0} miss(es), " +
                $"{state.AtlasValueGateMaterialConsumerEntryBuilds:N0} entry build(s)");
            sb.AppendLine(
                $"Atlas value-gate traversal XML parses: " +
                $"WSModel={state.AtlasValueGateGameplayTraversalWsModelXmlParseCount:N0}, " +
                $"material={state.AtlasValueGateGameplayTraversalMaterialXmlParseCount:N0}");
            sb.AppendLine(
                $"Atlas value-gate container resolution activity: " +
                $"{state.AtlasValueGateContainerResolutionScans:N0} scan(s), " +
                $"{state.AtlasValueGateContainerResolutionCacheHits:N0} cache hit(s)");
            var transformedAtlasRoots = state.AtlasedMeshes
                .Select(entry => entry.RootVmdPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            sb.AppendLine($"Mesh parts atlased/remapped: {state.ProcessedMeshes.Count}");
            sb.AppendLine(
                $"Atlas VMD roots with atlased mesh parts: {transformedAtlasRoots:N0} / " +
                $"{state.AtlasVmdRootCount:N0}");
            sb.AppendLine($"Modified rigid meshes: {state.ModifiedRigids.Count:N0}");
            sb.AppendLine($"Modified WSModels: {state.ModifiedWsModels.Count:N0}");
            sb.AppendLine($"Mesh parts skipped: {GetEffectiveSkippedMeshCount(state)}");
            sb.AppendLine(
                $"Existing Asset Editor atlas output detected: " +
                $"{(state.ExistingAtlasOutputDetected
                    ? $"YES ({state.ExistingGeneratedAtlasCandidateCount:N0} existing candidate(s) preserved; new candidates remained eligible)"
                    : "NO")}");
            sb.AppendLine($"Atlas textures generated: {state.GeneratedTexturePaths.Count}");
            sb.AppendLine(
                $"Shared physical atlas-page evaluations: {state.SharedAtlasPageSharingEvaluations}");
            sb.AppendLine(
                $"Shared physical atlas pages accepted: {state.SharedAtlasPageCoalescesAccepted}");
            sb.AppendLine(
                $"Batches participating in shared physical pages: " +
                $"{state.SharedAtlasPageParticipatingBatches}");
            sb.AppendLine(
                $"Pixels saved by shared physical atlas pages: " +
                $"{state.SharedAtlasPagePixelsSaved:N0}");
            sb.AppendLine($"Constant-only atlas channels skipped: {state.ConstantOnlyAtlasChannelsSkipped}");
            sb.AppendLine($"Uniform constant source textures detected: {state.UniformConstantTexturePaths.Count}");
            sb.AppendLine(
                $"Uniform constant material assignments canonicalized: " +
                $"{state.UniformConstantTextureAssignmentsCanonicalized}");
            sb.AppendLine($"Large BC split-compression channels: {state.LargeBcSplitCompressionChannels}");
            sb.AppendLine($"Atlas materials generated: {state.GeneratedMaterialPaths.Count}");
            sb.AppendLine($"Atlas material assignments reused: {state.GeneratedMaterialReuses}");
            sb.AppendLine($"Atlas placements generated: {state.AtlasPlacementsGenerated}");
            sb.AppendLine($"Atlas placements reused: {state.AtlasPlacementsReused}");
            sb.AppendLine($"Wrapped-UV placements canonicalized: {state.WrappedUvPlacementsCanonicalized}");
            sb.AppendLine($"UV-island normalized meshes: {state.UvIslandNormalizedMeshes}");
            sb.AppendLine($"UV islands shifted by integer tiles: {state.UvIslandsShifted}");
            sb.AppendLine($"UV-island source crop pixels saved: {state.UvIslandCropPixelsSaved:N0}");
            sb.AppendLine($"Shared UV-island seam groups aligned: {state.SharedUvIslandCutGroups}");
            sb.AppendLine($"Content-deduplicated atlas placements: {state.ContentDeduplicatedAtlasPlacements}");
            sb.AppendLine($"Mesh references remapped by content dedupe: {state.ContentCanonicalizedMeshReferences}");
            sb.AppendLine($"Cropped-content hashes computed: {state.AtlasRegionContentHashes.Count}");
            sb.AppendLine($"Pack-wide atlas/material sharing: {(state.ShareAtlasesAcrossVmdsEnabled ? "YES" : "NO")}");
            sb.AppendLine($"Cross-rigid-only mode: {(state.CrossRigidOnly ? "YES" : "NO")}");
            sb.AppendLine(
                $"LOD-aware atlas family planning: " +
                $"{(state.LodAwareAtlasPlanningEnabled ? "YES" : "NO")}");
            if (state.LodAwareAtlasPlanningEnabled)
            {
                sb.AppendLine(
                    $"LOD-aware atlas family closures: {state.LodAwareAtlasFamilyClosures:N0}");
                sb.AppendLine(
                    $"LOD-aware atlas families spanning multiple LODs: " +
                    $"{state.LodAwareAtlasFamiliesSpanningMultipleLods:N0}");
                sb.AppendLine(
                    $"LOD-aware family candidates closed over: " +
                    $"{state.LodAwareAtlasFamilyClosureCandidates:N0}");
                sb.AppendLine(
                    $"LOD-aware structural components replanned atomically: " +
                    $"{state.LodAwareAtlasStructuralComponentsReplanned:N0}");
                sb.AppendLine(
                    $"LOD-aware structural components rejected: " +
                    $"{state.LodAwareAtlasStructuralComponentsRejected:N0}");
            }
            sb.AppendLine($"Atlas batches generated: {state.AtlasBatchCount}");
            sb.AppendLine($"Texel-density downscale candidates: {state.TexelDensityScaledMeshes}");
            if (state.TexelDensityScaledMeshes != 0)
            {
                sb.AppendLine(
                    $"Texel-density scaled source pixel area: " +
                    $"{state.TexelDensityOriginalPixelArea:N0} -> {state.TexelDensityScaledPixelArea:N0}");
                sb.AppendLine(
                    $"Texel-density scale buckets: " +
                    $"{string.Join(", ", state.TexelDensityScaleCounts
                        .OrderBy(entry => entry.Key)
                        .Select(entry => $"{entry.Key:0.##}x={entry.Value:N0}"))}");
            }
            if (state.MergeCompatibleMeshesEnabled)
            {
                sb.AppendLine($"Atlas value-gate batches accepted: {state.AtlasValueGateBatchesAccepted}");
                sb.AppendLine($"Atlas value-gate batches rejected: {state.AtlasValueGateBatchesRejected}");
                sb.AppendLine(
                    $"Atlas value-gate texture-only merge batches accepted: " +
                    $"{state.AtlasValueGateTextureOnlyMergeBatchesAccepted}");
                sb.AppendLine(
                    $"Atlas value-gate texture-only merge candidates accepted: " +
                    $"{state.AtlasValueGateTextureOnlyMergeCandidatesAccepted}");
                sb.AppendLine(
                    $"Atlas value-gate texture-only merge marginal attachments accepted: " +
                    $"{state.AtlasValueGateTextureOnlyMergeMarginalGroupsAccepted}");
                sb.AppendLine(
                    $"Atlas value-gate texture-only merge cross-batch attachment groups evaluated: " +
                    $"{state.AtlasValueGateTextureOnlyMergeCrossBatchGroupsEvaluated}");
                sb.AppendLine(
                    $"Atlas value-gate texture-only merge cross-batch attachment groups accepted: " +
                    $"{state.AtlasValueGateTextureOnlyMergeCrossBatchGroupsAccepted}");
                sb.AppendLine(
                    $"Atlas value-gate texture-only merge subset groups considered: " +
                    $"{state.AtlasValueGateTextureOnlyMergeSubsetGroupsConsidered}");
                sb.AppendLine(
                    $"Atlas value-gate texture-only merge subset options evaluated: " +
                    $"{state.AtlasValueGateTextureOnlyMergeSubsetOptionsEvaluated}");
                sb.AppendLine(
                    $"Atlas value-gate texture-only merge subset option groups truncated: " +
                    $"{state.AtlasValueGateTextureOnlyMergeSubsetOptionGroupsTruncated}");
                sb.AppendLine(
                    $"Atlas value-gate texture-only merge maximum subset group candidates: " +
                    $"{state.AtlasValueGateTextureOnlyMergeMaximumSubsetGroupCandidateCount}");
                sb.AppendLine(
                    $"Atlas value-gate texture-only merge four-way options generated: " +
                    $"{state.AtlasValueGateTextureOnlyMergeFourWayOptionsGenerated}");
                sb.AppendLine(
                    $"Atlas value-gate texture-only merge four-way options evaluated: " +
                    $"{state.AtlasValueGateTextureOnlyMergeFourWayOptionsEvaluated}");
                sb.AppendLine(
                    $"Atlas value-gate texture-only merge four-way groups accepted: " +
                    $"{state.AtlasValueGateTextureOnlyMergeFourWayGroupsAccepted}");
                sb.AppendLine(
                    $"Atlas value-gate texture-only batches accepted: " +
                    $"{state.AtlasValueGateTextureOnlyBatchesAccepted}");
                sb.AppendLine(
                    $"Atlas value-gate texture-only candidates accepted: " +
                    $"{state.AtlasValueGateTextureOnlyCandidatesAccepted}");
                sb.AppendLine(
                    $"Atlas value-gate texture-only physically retired source textures: " +
                    $"{state.AtlasValueGateTextureOnlyRetiredSourceTextureCount:N0}");
                sb.AppendLine(
                    $"Atlas value-gate texture-only pack-local source textures no longer referenced: " +
                    $"{state.AtlasValueGateTextureOnlyPackReferenceRetiredSourceTextureCount:N0}");
                sb.AppendLine(
                    $"Atlas value-gate texture-only scenario-displaced source textures: " +
                    $"{state.AtlasValueGateTextureOnlyScenarioDisplacedSourceTextureCount:N0} " +
                    $"({state.AtlasValueGateTextureOnlyExpectedRetiredSourceTextureCount:N3} texture-equivalents)");
                sb.AppendLine($"Atlas value-gate broad batches split: {state.AtlasValueGateBroadBatchesSplit}");
                sb.AppendLine(
                    $"Atlas value-gate unresolved candidates skipped: " +
                    $"{state.AtlasValueGateUnresolvedCandidatesSkipped}");
                sb.AppendLine(
                    $"Atlas value-gate marginal groups accepted: " +
                    $"{state.AtlasValueGateMarginalGroupsAccepted}");
                sb.AppendLine(
                    $"Atlas value-gate mixed groups rejected: " +
                    $"{state.AtlasValueGateMixedGroupsRejected}");
                sb.AppendLine(
                    $"Atlas value-gate unresolved policy: " +
                    $"{(state.AtlasAllVmdsEnabled ? "PACK-WIDE raw-draw fallback allowed" : "GAMEPLAY-USED unresolved assets are left unchanged")}");
                sb.AppendLine(
                    $"Atlas value-gate batch ordering: greedy affected-unit coverage per " +
                    $"scenario-estimated net BCn MiB " +
                    $"({state.AtlasValueGateCoverageOrderedBatchCount:N0} batch(es))");
                sb.AppendLine(
                    $"Atlas value-gate candidate affected-unit coverage: " +
                    $"{state.AtlasValueGateCandidateAffectedUnitCount:N0}");
                sb.AppendLine(
                    $"Atlas value-gate accepted affected-unit coverage: " +
                    $"{state.AtlasValueGateAcceptedAffectedUnitCount:N0}");
                sb.AppendLine(
                    $"Atlas value-gate net BCn budgets: " +
                    $"{FormatMiB(MaxNetBcnBytesPerExpectedArmyDraw, 2)} per scenario-estimated draw; " +
                    $"{FormatMiB(MaxNetBcnBytesPerFallbackDraw, 2)} per raw draw only in pack-wide mode when scenario relevance is unresolved");
                sb.AppendLine(
                    $"Atlas value-gate texture-only budget: " +
                    $"{FormatMiB(MaxNetBcnBytesPerRetiredSourceTexture, 2)} net per " +
                    $"pack-local source texture removed from references or scenario-displaced " +
                    $"source texture; minimum " +
                    $"{MinimumRetiredSourceTexturesForTextureOnlyAtlas:N0} pack-local source " +
                    "texture retirements, with at least 1.0 scenario-displaced " +
                    "texture-equivalent when pack-local retirement alone does not qualify");
                sb.AppendLine(
                    $"Atlas value-gate texture-only merge credit: " +
                    $"{FormatMiB(MaxNetBcnBytesPerTextureOnlyMergeDraw, 2)} per structural " +
                    $"draw eliminated, or " +
                    $"{FormatMiB(MaxNetBcnBytesPerTextureOnlyMergeExpectedArmyDraw, 2)} " +
                    "per scenario-estimated draw when larger; the scenario budget also gets " +
                    $"{FormatMiB(MaxNetBcnBytesPerRetiredSourceTexture, 2)} per pack-local-only " +
                    "source texture whose references are fully retired; physical and runtime " +
                    "retirement remain accounted separately; scenario-resolved benefit required");
                sb.AppendLine(
                    $"Atlas value-gate texture-consolidation batches accepted: " +
                    $"{state.AtlasValueGateTextureConsolidationBatchesAccepted}");
                sb.AppendLine(
                    $"Atlas value-gate texture-consolidation candidates accepted: " +
                    $"{state.AtlasValueGateTextureConsolidationCandidatesAccepted}");
                sb.AppendLine(
                    $"Atlas value-gate texture assignments consolidated: " +
                    $"{state.AtlasValueGateTextureConsolidatedAssignments:N0}");
                sb.AppendLine(
                    $"Atlas value-gate texture-consolidation cohorts evaluated: " +
                    $"{state.AtlasValueGateTextureConsolidationCohortsEvaluated:N0}");
                sb.AppendLine(
                    $"Atlas value-gate texture-consolidation cohorts split: " +
                    $"{state.AtlasValueGateTextureConsolidationCohortsSplit:N0}");
                sb.AppendLine(
                    $"Atlas value-gate texture-consolidation budget: " +
                    $"{FormatMiB(MaxNetBcnBytesPerConsolidatedTextureAssignment, 2)} net per " +
                    $"consolidated texture assignment; minimum " +
                    $"{MinimumConsolidatedTextureAssignmentsForAtlas:N0} assignments; " +
                    $"initial cohort limit {TextureConsolidationInitialCohortCandidateLimit:N0}");
                sb.AppendLine(
                    $"Atlas value-gate cumulative reachable BCn growth cap: " +
                    $"{MaxReachableBcnGrowthRatio:P0} of source reachable BCn payload");
                sb.AppendLine($"Atlas value-gate candidates accepted: {state.AtlasValueGateCandidatesAccepted}");
                sb.AppendLine($"Atlas value-gate candidates rejected: {state.AtlasValueGateCandidatesRejected}");
                sb.AppendLine(
                    $"Atlas value-gate generated BCn accepted: " +
                    $"{FormatMiB(state.AtlasValueGateGeneratedBcnBytesAccepted)}");
                sb.AppendLine(
                    $"Atlas value-gate source BCn retired by accepted batches: " +
                    $"{FormatMiB(state.AtlasValueGateRetiredBcnBytesAccepted)}");
                sb.AppendLine(
                    $"Atlas value-gate texture-only source textures retired: " +
                    $"{state.AtlasValueGateTextureOnlyRetiredSourceTextureCount:N0} " +
                    $"(scenario-estimated {state.AtlasValueGateTextureOnlyExpectedRetiredSourceTextureCount:N3} texture-equivalents)");
                sb.AppendLine(
                    $"Atlas value-gate texture-only pack-local source textures no longer referenced: " +
                    $"{state.AtlasValueGateTextureOnlyPackReferenceRetiredSourceTextureCount:N0}");
                sb.AppendLine(
                    $"Atlas value-gate texture-only source BCn retired: " +
                    $"{FormatMiB(state.AtlasValueGateTextureOnlyRetiredBcnBytesAccepted)} " +
                    $"(scenario-estimated {FormatMiB(state.AtlasValueGateTextureOnlyExpectedRetiredBcnBytesAccepted)})");
                sb.AppendLine(
                    $"Atlas value-gate net BCn accepted: " +
                    $"{FormatMiB(state.AtlasValueGateNetBcnBytesAccepted)}");
                sb.AppendLine(
                    $"Atlas value-gate scenario-estimated generated BCn accepted: " +
                    $"{FormatMiB(state.AtlasValueGateExpectedArmyGeneratedBcnBytesAccepted)}");
                sb.AppendLine(
                    $"Atlas value-gate scenario-estimated source BCn retired by accepted batches: " +
                    $"{FormatMiB(state.AtlasValueGateExpectedArmyRetiredBcnBytesAccepted)}");
                sb.AppendLine(
                    $"Atlas value-gate scenario-estimated net BCn accepted: " +
                    $"{FormatMiB(state.AtlasValueGateExpectedArmyNetBcnBytesAccepted)}");
                AppendAtlasValueGateSourceTextureResidencyDiagnostics(sb, state);
                sb.AppendLine(
                    $"Atlas value-gate generated BCn rejected: " +
                    $"{FormatMiB(state.AtlasValueGateGeneratedBcnBytesRejected)}");
                sb.AppendLine(
                    $"Atlas value-gate source BCn retirement rejected: " +
                    $"{FormatMiB(state.AtlasValueGateRetiredBcnBytesRejected)}");
                sb.AppendLine(
                    $"Atlas value-gate net BCn rejected: " +
                    $"{FormatMiB(state.AtlasValueGateNetBcnBytesRejected)}");
                sb.AppendLine(
                    $"Atlas value-gate scenario-estimated net BCn rejected: " +
                    $"{FormatMiB(state.AtlasValueGateExpectedArmyNetBcnBytesRejected)}");
                sb.AppendLine(
                    $"Atlas value-gate scenario-estimated draw eliminations accepted: " +
                    $"{state.AtlasValueGateExpectedDrawsAccepted:N3}");
                sb.AppendLine();
                sb.AppendLine("Accepted atlas value-gate batch economics (worst-to-best)");
                sb.AppendLine("---------------------------------------------------------");
                sb.AppendLine(
                    "Sorted descending by the worse of global net MiB/raw draw and " +
                    "scenario-estimated net MiB/scenario-estimated draw.");
                var valueGateRank = 0;
                foreach (var entry in state.AtlasValueGateAcceptedBatchEconomics
                             .OrderByDescending(GetAtlasValueGateWorstMiBPerDraw)
                             .ThenByDescending(entry => GetAtlasValueGateMiBPerDraw(
                                 entry.NetBcnBytes,
                                 entry.RawDrawsEliminated))
                             .ThenBy(entry => entry.AcceptanceSequence))
                {
                    valueGateRank++;
                    sb.AppendLine(
                        $"  #{valueGateRank}: accepted-seq={entry.AcceptanceSequence}, " +
                        $"kind={entry.AcceptanceKind}, candidates={entry.CandidateCount}, " +
                        $"roots={entry.RootVmdPaths.Length}");
                    sb.AppendLine(
                        $"    draws: raw={entry.RawDrawsEliminated:N0}, " +
                        $"scenario-estimated={entry.ExpectedArmyDrawsEliminated:N3}");
                    sb.AppendLine(
                        $"    affected units={entry.AffectedUnitCount:N0}, " +
                        $"expected rendered entities={entry.ExpectedRenderedEntities:N3}");
                    sb.AppendLine(
                        $"    coverage efficiency=" +
                        $"{FormatAffectedUnitsPerScenarioMiB(entry.AffectedUnitCount, entry.ExpectedArmyNetBcnBytes)} " +
                        $"affected units / scenario MiB");
                    sb.AppendLine(
                        $"    global BCn: generated={FormatMiB(entry.GeneratedBcnBytes)}, " +
                        $"retired={FormatMiB(entry.RetiredSourceBcnBytes)}, " +
                        $"net={FormatMiB(entry.NetBcnBytes)}, " +
                        $"net/draw={FormatAtlasValueGateMiBPerDraw(entry.NetBcnBytes, entry.RawDrawsEliminated)}");
                    sb.AppendLine(
                        $"    scenario-estimated BCn: generated={FormatMiB(entry.ExpectedArmyGeneratedBcnBytes)}, " +
                        $"retired={FormatMiB(entry.ExpectedArmyRetiredSourceBcnBytes)}, " +
                        $"net={FormatMiB(entry.ExpectedArmyNetBcnBytes)}, " +
                        $"net/draw={FormatAtlasValueGateMiBPerDraw(entry.ExpectedArmyNetBcnBytes, entry.ExpectedArmyDrawsEliminated)}");
                    sb.AppendLine(
                        $"    roots: {(entry.RootVmdPaths.Length == 0 ? "<none>" : string.Join(", ", entry.RootVmdPaths))}");
                }
                if (state.AtlasValueGateAcceptedBatchEconomics.Count == 0)
                    sb.AppendLine("  (none)");

                sb.AppendLine();
                sb.AppendLine(
                    $"Texture-only merge near misses (best {TextureOnlyMergeNearMissLimit:N0})");
                sb.AppendLine(
                    "------------------------------------------------------------");
                if (state.AtlasValueGateTextureOnlyMergeNearMisses.Count == 0)
                {
                    sb.AppendLine("  (none)");
                }
                else
                {
                    var nearMissRank = 0;
                    foreach (var nearMiss in state.AtlasValueGateTextureOnlyMergeNearMisses
                                 .OrderBy(entry => entry.BudgetPressure)
                                 .ThenByDescending(entry => entry.ExpectedArmyDrawsEliminated)
                                 .Take(TextureOnlyMergeNearMissLimit))
                    {
                        nearMissRank++;
                        sb.AppendLine(
                            $"  #{nearMissRank}: candidates={nearMiss.CandidateCount:N0}, " +
                            $"pressure={nearMiss.BudgetPressure:N3}x, " +
                            $"global={FormatMiB(nearMiss.IncrementalGlobalCostBytes)}, " +
                            $"scenario={FormatMiB(nearMiss.IncrementalExpectedCostBytes)}, " +
                            $"draws raw={nearMiss.RawDrawsEliminated:N0}, " +
                            $"scenario-estimated={nearMiss.ExpectedArmyDrawsEliminated:N3}");
                        sb.AppendLine(
                            $"    candidate group: {nearMiss.CandidateGroup}");
                        sb.AppendLine(
                            $"    host batch: {nearMiss.HostBatch}");
                        sb.AppendLine(
                            $"    reason: {nearMiss.RejectionReason}");
                    }
                }
            }
            sb.AppendLine($"Atlas pixel-area optimized splits: {state.AtlasPixelAreaOptimizedSplits}");
            sb.AppendLine($"Atlas pixel-area split evaluations: {state.AtlasPixelAreaSplitEvaluations}");
            sb.AppendLine($"Atlas non-contiguous optimized splits: {state.AtlasNonContiguousOptimizedSplits}");
            sb.AppendLine($"Atlas non-contiguous split evaluations: {state.AtlasNonContiguousSplitEvaluations}");
            sb.AppendLine($"Pack atlas maximum dimension: {PackAtlasMaxSize}");
            sb.AppendLine($"Atlas pixels saved by split optimization: {state.AtlasPixelAreaSavedByOptimizedSplits:N0}");
            sb.AppendLine($"Atlas pixels saved by non-contiguous splits: {state.AtlasPixelAreaSavedByNonContiguousSplits:N0}");
            sb.AppendLine($"Army-aware locality split evaluations: {state.VmdLocalitySplitEvaluations}");
            sb.AppendLine($"Army-aware locality splits accepted: {state.VmdLocalitySplitsAccepted}");
            sb.AppendLine($"Estimated VMD-resident atlas pixels saved by locality splits: {state.VmdLocalityResidentPixelsSaved:N0}");
            if (state.ArmyResidencyModel != null)
            {
                sb.AppendLine(
                    $"Scenario army slot model: lord=1, heroes=2, infantry/missile=9, " +
                    $"cavalry/chariots=4, monsters/beasts=3, artillery/war machines=2");
                sb.AppendLine(
                    $"Scenario-estimated resident atlas pixels before locality optimization: " +
                    $"{state.ExpectedArmyResidentPixelsBeforeLocality:N0}");
                sb.AppendLine(
                    $"Scenario-estimated resident atlas pixels after locality optimization: " +
                    $"{state.ExpectedArmyResidentPixelsAfterLocality:N0}");
                sb.AppendLine(
                    $"Scenario-estimated resident atlas pixels saved by locality optimization: " +
                    $"{Math.Max(0, state.ExpectedArmyResidentPixelsBeforeLocality - state.ExpectedArmyResidentPixelsAfterLocality):N0}");
                sb.AppendLine(
                    $"Scenario-estimated resident atlas pixels after merge-aware optimization: " +
                    $"{state.ExpectedArmyResidentPixelsAfterMergeAware:N0}");
            }
            sb.AppendLine($"Global atlas pixels added by locality splits: {state.VmdLocalityGlobalPixelsAdded:N0}");
            sb.AppendLine($"Merge-aware locality regressions rejected: {state.MergeAwareLocalityRegressionsRejected}");
            sb.AppendLine($"Final global atlas pixel cost: {residency.GlobalPixels:N0}");
            sb.AppendLine($"Estimated aggregate VMD-resident atlas pixel cost: {residency.AggregateResidentPixels:N0}");
            sb.AppendLine(
                $"Physical atlas pages spanning multiple actual VMD roots: " +
                $"{residency.MultiRootPageCount}");
            sb.AppendLine(
                $"Maximum VMD roots sharing one physical atlas page: " +
                $"{residency.MaxRootsPerPage}");
            if (!string.IsNullOrWhiteSpace(residency.WorstRootVmdPath))
            {
                sb.AppendLine(
                    $"Highest estimated single-VMD atlas pixel residency: " +
                    $"{residency.WorstRootPixels:N0} ({residency.WorstRootVmdPath})");
            }
            sb.AppendLine($"Merge-aware batch pairs considered: {state.MergeAwareBatchPairsConsidered}");
            sb.AppendLine($"Merge-aware repartition evaluations: {state.MergeAwareRepartitionEvaluations}");
            sb.AppendLine($"Merge-aware repartitions accepted: {state.MergeAwareRepartitionsAccepted}");
            sb.AppendLine($"Merge-aware repartition pixels saved: {state.MergeAwareRepartitionPixelsSaved:N0}");
            sb.AppendLine($"Merge-aware no-extra-pixel coalesce evaluations: {state.MergeAwareBatchCoalesceEvaluations}");
            sb.AppendLine($"Merge-aware no-extra-pixel coalesces accepted: {state.MergeAwareBatchCoalescesAccepted}");
            sb.AppendLine($"Merge-aware coalesce pixels saved: {state.MergeAwareBatchCoalescePixelsSaved:N0}");
            sb.AppendLine($"Merge-affinity eliminations before repartition: {state.MergeAwareAffinityPotentialBefore}");
            sb.AppendLine($"Merge-affinity eliminations after repartition: {state.MergeAwareAffinityPotentialAfter}");
            sb.AppendLine($"Merge-affinity eliminations gained: {state.MergeAwareAffinityEliminationsGained}");
            if (state.ArmyResidencyModel != null)
            {
                sb.AppendLine(
                    $"Scenario-estimated draw calls eliminated before merge-aware optimization: " +
                    $"{state.ExpectedArmyDrawCallsEliminatedBeforeMergeAware:N3}");
                sb.AppendLine(
                    $"Scenario-estimated draw calls eliminated after merge-aware/value-gate optimization: " +
                    $"{state.ExpectedArmyDrawCallsEliminatedAfterValueGate:N3}");
                sb.AppendLine(
                    $"Scenario-estimated draw-call eliminations gained: " +
                    $"{state.ExpectedArmyDrawCallsEliminatedAfterValueGate - state.ExpectedArmyDrawCallsEliminatedBeforeMergeAware:+0.000;-0.000;0.000}");
            }
            if (state.ShareAtlasesAcrossVmdsEnabled)
            {
                sb.AppendLine($"Pack-wide atlas candidates: {state.PackWideCandidateCount}");
                sb.AppendLine($"Cross-VMD shared atlas batches: {state.CrossVmdSharedAtlasBatches}");
                sb.AppendLine($"Cross-VMD shared placements: {state.CrossVmdSharedAtlasPlacements}");
                sb.AppendLine($"Cross-VMD material reuses: {state.CrossVmdMaterialReuses}");
            }
            sb.AppendLine(
                $"Atlased WSModels reachable from multiple VMD roots (reachability, not DB asset reuse): " +
                $"{componentReuse.Components.Count(x => x.VmdRootCount > 1)} / {componentReuse.Components.Count}");
            sb.AppendLine(
                $"Atlased geometry paths reused by multiple VMD roots: " +
                $"{componentReuse.ReusedGeometryCount} / {componentReuse.GeometryCount}");
            sb.AppendLine(
                $"Cross-geometry/cross-batch component pairs with asymmetric VMD-root reuse: " +
                $"{componentReuse.Pairs.Count(x => x.StronglyAsymmetric)}");
            sb.AppendLine(
                $"Cross-geometry/cross-batch component pairs with high bidirectional VMD-root overlap: " +
                $"{componentReuse.Pairs.Count(x => x.HighBidirectionalOverlap)}");
            sb.AppendLine($"Superseded asset files removed: {state.RemovedFiles.Count}");
            sb.AppendLine("Meshes with genuine unresolved secondary textures are skipped.");
            sb.AppendLine($"Merge compatible mesh parts: {(state.MergeCompatibleMeshesEnabled ? "YES" : "NO")}");
            if (state.MergeCompatibleMeshesEnabled)
            {
                sb.AppendLine($"Mesh parts before merging: {state.MeshPartsBeforeMerging}");
                sb.AppendLine($"Mesh parts after merging: {state.MeshPartsAfterMerging}");
                sb.AppendLine($"Mesh parts eliminated: {state.MeshPartsEliminated}");
                sb.AppendLine(
                    $"Pre-atlas structural merge groups identified: " +
                    $"{state.PreAtlasStructuralMergeGroupCount}");
                sb.AppendLine(
                    $"Structural-only merge groups applied: " +
                    $"{state.StructuralOnlyMergeGroupCount}");
                sb.AppendLine(
                    $"Structural+atlas-assisted merge groups applied: " +
                    $"{state.StructuralAndAtlasAssistedMergeGroupCount}");
                sb.AppendLine(
                    $"Atlas-assisted merge groups applied: " +
                    $"{state.AtlasAssistedMergeGroupCount}");
                sb.AppendLine(
                    $"Pre-atlas structural meshes protected from atlas rewriting: " +
                    $"{state.AtlasProtectedStructuralMeshCount}");
                var structuralDiscoveryStatus = state.StructuralMergeConsumerDiscoveryComplete
                    ? "COMPLETE"
                    : state.UnitCategoryResolution?.IsGameplayResolutionHealthy == true &&
                      state.GameplayMeshDependencyIndex != null
                        ? "PARTIAL (assets reachable from incomplete roots blocked)"
                        : "INCOMPLETE/BLOCKED";
                sb.AppendLine(
                    $"Structural merge consumer discovery: " +
                    structuralDiscoveryStatus);
                sb.AppendLine(
                    $"Writable rigids with immutable gameplay consumers: " +
                    $"{state.ImmutableMeshMergeConsumersByRigid.Count}");
                sb.AppendLine(
                    $"Writable rigids with direct gameplay rigid consumers: " +
                    $"{state.DirectRigidMeshMergeConsumersByRigid.Count}");
                sb.AppendLine(
                    $"Structural consumer discovery failures: " +
                    $"{state.StructuralMergeConsumerDiscoveryFailures.Count}");
                sb.AppendLine(
                    $"Gameplay roots with incomplete consumer discovery: " +
                    $"{state.GameplayMeshDependencyIndex?.IncompleteRoots.Count ?? 0}");
                sb.AppendLine($"Mesh parts merged across semantically identical material paths: {state.SemanticMaterialPathMergeParts}");
                sb.AppendLine($"Mesh merge near-miss blocker occurrences: {state.MeshMergeBlockerCounts.Values.Sum()}");
                var mergeOpportunities = state.TextureMergeOpportunities.Values.ToList();
                sb.AppendLine($"Texture-blocked merge opportunities: {mergeOpportunities.Count}");
                sb.AppendLine($"Texture-blocked merge opportunities whose atlas combination fits: {mergeOpportunities.Count(x => x.Combination.Fits)}");
                sb.AppendLine(
                    $"Counterfactual texture-only pairs evaluated: " +
                    $"{state.CounterfactualTextureOnlyPairsEvaluated}");
                sb.AppendLine(
                    $"Counterfactual texture-only pairs atlasable: " +
                    $"{state.CounterfactualTextureOnlyPairsAtlasable}");
                sb.AppendLine(
                    $"Counterfactual texture-only pairs constant-equivalent: " +
                    $"{state.CounterfactualTextureOnlyPairsCanonicalizable}");
                sb.AppendLine(
                    $"Prospective merge-affinity groups added: " +
                    $"{state.ProspectiveMergeAffinityGroupsAdded}");
                sb.AppendLine(
                    $"Prospective merge-affinity pairs: " +
                    $"{state.ProspectiveMergeAffinityPairs}");
                sb.AppendLine($"Zero-cost atlas batch combinations: {mergeOpportunities.Count(x => x.Combination.Fits && x.Combination.AdditionalPixels <= 0)}");
                sb.AppendLine($"Atlas combinations <=10% extra pixels: {mergeOpportunities.Count(x => x.Combination.Fits && x.Combination.AdditionalPixels > 0 && x.Combination.AdditionalPercent <= 10)}");
                sb.AppendLine($"Atlas combinations 10-25% extra pixels: {mergeOpportunities.Count(x => x.Combination.Fits && x.Combination.AdditionalPercent > 10 && x.Combination.AdditionalPercent <= 25)}");
                sb.AppendLine($"Atlas combinations >25% extra pixels: {mergeOpportunities.Count(x => x.Combination.Fits && x.Combination.AdditionalPercent > 25)}");
                sb.AppendLine($"Atlas combinations that do not fit: {mergeOpportunities.Count(x => !x.Combination.Fits)}");
            }

            if (state.MergeCompatibleMeshesEnabled)
            {
                sb.AppendLine();
                sb.AppendLine("Cross-rigid merge analysis and emission");
                sb.AppendLine("---------------------------------------");
                sb.AppendLine(
                    "Analysis records exact model/reference provenance. Metadata stays on its original VARIANT_MESH branch and does not block geometry rewrites; imposter/decal state remains rewrite-blocking. The emission pass writes value-gate-selected exact-activation states plus conservative structural joint states; selected branches may contain nested models or cross child VMD references, which are cloned per Cartesian combination instead of modified globally.");
                sb.AppendLine(
                    "VMD rewrite plans are separated from VMD-independent generated RMV/WSModel payloads; payload geometry is charged once across all rewrite plans that can reuse it. Probability-1 selections that share a structural VMD parent are materialized as a bounded Cartesian slot with the same uniform product distribution. For a source with nested probability-1 activation choices, only its highest differing selection becomes a Cartesian dependency-root dimension; deeper selections stay conditional inside that selected subtree and are resolved copy-on-write at their exact model occurrence. Always-present probability-1 source slots may be carried through every Cartesian branch so they can merge safely with varying siblings. Dependency roots may be lifted through probability-1 local wrappers while preserving the wrapper's own alternatives, and through fixed child-VMD references to their common structural parent. Fixed-local common ancestors under the same complete child-VMD reference chain can also be materialized with copy-on-write branch clones. Compatible narrower joint descriptions at one structural parent are folded into an already-analyzed bounded superset Cartesian context; narrower rewrites are projected across every matching superset state. At one shared structural parent, optional slots with a bounded exact rational probability and bounded alternative count are represented as weighted absent/present-alternative Cartesian states; each present alternative receives its conditional uniform share, and finished wrappers are duplicated by integer multiplicity to preserve the original slot probability exactly. Exactly representable, bounded optional dependency dimensions are admitted across fixed child-VMD references, including deterministic descendants inside an optional selected subtree; bounded local nested varying-selection chains and directly selected child-VMD reference alternatives are enumerated conditionally with exact inactive-child weights; fixed single-reference prefixes are kept as copy-on-write provenance, not independent Cartesian dimensions; deeper variable-reference chains and unsupported optional-slot topologies remain excluded.");
                sb.AppendLine(
                    "Visual probability is projected per attachment context, avoiding the full-VMD Cartesian product of unrelated appearance slots.");
                sb.AppendLine(
                    "Shared or incompletely discovered structural consumers are duplication-cost diagnostics, not eligibility failures.");
                sb.AppendLine(
                    "Generated-geometry residency uses a conservative upper-bound union of attachment-local rewrite probabilities; displaced-source credit is a strict lower bound.");
                sb.AppendLine(
                    "Cross-rigid LOD reconciliation preserves identical authored distance schedules. When source camera-distance schedules differ, their transition distances are unioned, weighted by affected geometry, and reduced to at most the largest source LOD count with a hard cap of 5; each retained interval samples the source LOD each component would render there. Mixed quality profiles use quality level 2 on generated LOD0 and quality level 0 afterward.");
                sb.AppendLine(
                    "Geometry payload bytes are serialized vertex plus 16-bit index payload only; RMV headers and material metadata are excluded.");
                sb.AppendLine(
                    $"VMD roots with multiple reachable WSModel paths analyzed: " +
                    $"{state.CrossRigidAnalysisVmdCount:N0}");
                sb.AppendLine(
                    $"Distinct WSModel paths inspected: " +
                    $"{state.CrossRigidAnalysisDirectWsModelCount:N0}");
                sb.AppendLine(
                    $"Attachment-local co-render configurations inspected: " +
                    $"{state.CrossRigidAnalysisConfigurationCount:N0}");
                sb.AppendLine(
                    $"Eligible configuration-group observations before rewrite-plan dedupe: " +
                    $"{state.CrossRigidAnalysisOpportunityObservationCount:N0}");
                sb.AppendLine(
                    $"Cross-rigid fallback subgroups considered: " +
                    $"{state.CrossRigidPairFallbackSubgroupObservations:N0} " +
                    $"(triples={state.CrossRigidTripleFallbackSubgroupObservations:N0}, " +
                    $"quadruples={state.CrossRigidQuadrupleFallbackSubgroupObservations:N0}, " +
                    $"quintuples={state.CrossRigidQuintupleFallbackSubgroupObservations:N0}); " +
                    $"middle-out triples={state.CrossRigidMiddleTripleFallbackSubgroupObservations:N0}, " +
                    $"middle-out quadruples={state.CrossRigidMiddleQuadrupleFallbackSubgroupObservations:N0}");
                sb.AppendLine(
                    $"Subgroup fallback plans competing with writable full groups: " +
                    $"{state.CrossRigidPairFallbackPlansCompetingWithSafeFullGroups:N0}");
                sb.AppendLine(
                    $"Subgroup fallback plans evaluated (including competing subsets): " +
                    $"{state.CrossRigidPairFallbackPlansEvaluated:N0}");
                sb.AppendLine(
                    "Activation rewrite-state classification " +
                    "(plans may appear in more than one class):");
                foreach (var rewriteClass in
                         Enum.GetValues<CrossRigidActivationRewriteClass>())
                {
                    var occurrenceSetCount =
                        state.CrossRigidActivationRewriteOccurrenceSetCounts
                            .GetValueOrDefault(rewriteClass);
                    var planCount =
                        state.CrossRigidActivationRewritePlanCounts
                            .GetValueOrDefault(rewriteClass);
                    var expectedDraws =
                        state.CrossRigidActivationRewriteExpectedDrawCalls
                            .GetValueOrDefault(rewriteClass);
                    sb.AppendLine(
                        $"  {GetCrossRigidActivationRewriteClassLabel(rewriteClass)}: " +
                        $"states={occurrenceSetCount:N0}, plans={planCount:N0}, " +
                        $"scenario-expected draws={expectedDraws:0.###}");
                }
                sb.AppendLine(
                    "Local-modifier profile by activation topology " +
                    "(plans may appear in more than one row):");
                if (state.CrossRigidModifierProfileOccurrenceSetCounts.Count == 0)
                {
                    sb.AppendLine("  none");
                }
                else
                {
                    foreach (var profile in
                             state.CrossRigidModifierProfileOccurrenceSetCounts
                                 .Keys
                                 .OrderByDescending(key =>
                                     state.CrossRigidModifierProfileExpectedDrawCalls
                                         .GetValueOrDefault(key))
                                 .ThenBy(
                                     key => key,
                                     StringComparer.Ordinal))
                    {
                        sb.AppendLine(
                            $"  {profile}: " +
                            $"states={state.CrossRigidModifierProfileOccurrenceSetCounts.GetValueOrDefault(profile):N0}, " +
                            $"plans={state.CrossRigidModifierProfilePlanCounts.GetValueOrDefault(profile):N0}, " +
                            $"scenario-expected draws={state.CrossRigidModifierProfileExpectedDrawCalls.GetValueOrDefault(profile):0.###}");
                    }
                }
                sb.AppendLine(
                    $"Unique copy-on-write-safe VMD rewrite plans with real draw savings: " +
                    $"{state.CrossRigidMergeAnalysisEntries.Count:N0}");
                sb.AppendLine(
                    $"Unique VMD-independent generated payloads: " +
                    $"{state.CrossRigidGeneratedPayloadAnalysisEntries.Count:N0}");
                sb.AppendLine(
                    $"Rewrite plans sharing a generated payload: " +
                    $"{state.CrossRigidGeneratedPayloadAnalysisEntries.Sum(payload => Math.Max(0, payload.RewritePlanCount - 1)):N0}");
                sb.AppendLine(
                    $"Scenario-expected battle draw calls eliminated: " +
                    $"{state.CrossRigidMergeAnalysisEntries.Sum(entry => entry.ExpectedArmyDrawCallsEliminated):0.###}");
                var generatedVerticesBeforePayloadReuse =
                    state.CrossRigidMergeAnalysisEntries.Sum(entry =>
                        entry.Lods.Sum(lod =>
                            lod.GeneratedVertexCount));
                var generatedGeometryBytesBeforePayloadReuse =
                    state.CrossRigidMergeAnalysisEntries.Sum(entry =>
                        entry.Lods.Sum(lod =>
                            lod.GeneratedGeometryBytes));
                var generatedVerticesAfterPayloadReuse =
                    state.CrossRigidGeneratedPayloadAnalysisEntries.Sum(
                        payload => payload.GeneratedVertexCount);
                var generatedGeometryBytesAfterPayloadReuse =
                    state.CrossRigidGeneratedPayloadAnalysisEntries.Sum(
                        payload => payload.GeneratedGeometryBytes);
                sb.AppendLine(
                    $"Generated structural vertices before cross-VMD payload reuse: " +
                    $"{generatedVerticesBeforePayloadReuse:N0}");
                sb.AppendLine(
                    $"Generated structural vertices after cross-VMD payload reuse: " +
                    $"{generatedVerticesAfterPayloadReuse:N0}");
                sb.AppendLine(
                    $"Generated geometry payload before cross-VMD reuse: " +
                    $"{FormatMiB(generatedGeometryBytesBeforePayloadReuse)}");
                sb.AppendLine(
                    $"Generated geometry payload after cross-VMD reuse: " +
                    $"{FormatMiB(generatedGeometryBytesAfterPayloadReuse)}");
                sb.AppendLine(
                    $"Generated geometry payload eliminated by cross-VMD reuse: " +
                    $"{FormatMiB(Math.Max(0, generatedGeometryBytesBeforePayloadReuse - generatedGeometryBytesAfterPayloadReuse))}");
                sb.AppendLine(
                    $"Potentially duplicated geometry payload due to retained/unknown source consumers: " +
                    $"{FormatMiB(state.CrossRigidGeneratedPayloadAnalysisEntries.Sum(payload => payload.PotentiallyDuplicatedGeometryBytes))}");
                sb.AppendLine(
                    $"Scenario-estimated resident generated geometry payload (conservative upper bound): " +
                    $"{FormatMiB(state.CrossRigidGeneratedPayloadAnalysisEntries.Sum(payload => payload.ExpectedResidentGeneratedGeometryBytes))}");
                sb.AppendLine(
                    $"Source rigids with complete per-occurrence rewrite coverage: " +
                    $"{state.CrossRigidDisplacedSourceGeometryEntries.Count:N0}");
                sb.AppendLine(
                    $"Source geometry eligible for displacement: " +
                    $"{FormatMiB(state.CrossRigidDisplacedSourceGeometryEntries.Sum(entry => entry.GeometryBytes))}");
                sb.AppendLine(
                    $"Scenario-estimated displaced source geometry residency credit (strict lower bound): " +
                    $"{FormatMiB(state.CrossRigidDisplacedSourceGeometryEntries.Sum(entry => entry.ExpectedResidentGeometryBytes))}");
                sb.AppendLine(
                    $"Scenario-estimated net generated geometry residency: " +
                    $"{FormatMiB(state.CrossRigidGeneratedPayloadAnalysisEntries.Sum(payload => payload.ExpectedResidentGeneratedGeometryBytes - payload.ExpectedResidentDisplacedSourceGeometryBytes))}");
                sb.AppendLine(
                    $"Generated payloads with known external structural consumers: " +
                    $"{state.CrossRigidGeneratedPayloadAnalysisEntries.Count(payload => payload.ExternalStructuralConsumers.Length != 0):N0}");
                sb.AppendLine(
                    $"Generated payloads with incomplete consumer discovery: " +
                    $"{state.CrossRigidGeneratedPayloadAnalysisEntries.Count(payload => !payload.ConsumerDiscoveryComplete):N0}");

                var selectedPayloads =
                    state.CrossRigidPayloadSelectionEntries
                        .Where(entry => entry.Accepted)
                        .ToArray();
                var rejectedPayloads =
                    state.CrossRigidPayloadSelectionEntries
                        .Where(entry => !entry.Accepted)
                        .ToArray();
                var selectedExpectedDraws =
                    selectedPayloads.Sum(entry =>
                        entry.MarginalExpectedArmyDrawCallsEliminated);
                var totalExpectedDraws =
                    state.CrossRigidPayloadSelectionEntries.Sum(entry =>
                        entry.ExpectedArmyDrawCallsEliminated);
                var selectedRewritePlans =
                    state.CrossRigidMergeAnalysisEntries
                        .Where(plan =>
                            plan.RewriteOccurrenceSets.Any(
                                occurrenceSet =>
                                    state.CrossRigidSelectedRewriteOccurrenceKeys
                                        .Contains(
                                            BuildCrossRigidSelectedRewriteOccurrenceKey(
                                                plan,
                                                occurrenceSet))))
                        .ToArray();
                var selectedGeneratedGeometryBytes =
                    selectedPayloads.Sum(entry =>
                        entry.GeneratedGeometryBytes);
                var selectedExpectedResidentGeometryBytes =
                    selectedPayloads.Sum(entry =>
                        entry.ExpectedResidentGeneratedGeometryBytes);

                sb.AppendLine();
                sb.AppendLine("Cross-rigid generated-payload value gate");
                sb.AppendLine("----------------------------------------");
                sb.AppendLine(
                    "The value gate defines the emission candidate set; topology-unsafe or non-inline rewrite plans remain unchanged and are reported separately.");
                sb.AppendLine(
                    "The runtime gate charges gross expected generated-geometry residency; displaced-source credit is not spent by the gate.");
                sb.AppendLine(
                    "Rewrite conflicts are resolved at exact occurrence granularity and, for canonical joint supersets, per concrete Cartesian state. Narrower rewrites can therefore remain selected in non-conflicting superset states. A bounded portfolio search compares raw-draw, cost-density, scenario-benefit, individual competing-payload, and bounded pairwise competing-payload orderings using conflict-deduplicated marginal draw savings; the best valid portfolio is replayed for emission. Partially selected payloads conservatively pay their full generated geometry and full estimated residency. Conflict diagnostics identify the already-selected payload that owns the blocking occurrence/state.");
                sb.AppendLine(
                    "Scenario-resident and physical-per-draw limits are selected-portfolio budgets rather than per-payload hard gates; budget-deferred candidates are retried after cheaper accepted payloads create headroom.");
                sb.AppendLine(
                    $"Scenario-resident geometry budget: " +
                    $"{FormatMiB(MaxCrossRigidExpectedResidentGeometryBytesPerExpectedArmyDraw, 2)} per expected draw");
                sb.AppendLine(
                    $"Physical generated-geometry budget: " +
                    $"{FormatMiB(MaxCrossRigidGeneratedGeometryBytesPerExpectedArmyDraw, 2)} per expected draw");
                sb.AppendLine(
                    $"Cumulative generated-geometry cap: " +
                    $"{FormatMiB(MaxCrossRigidGeneratedGeometryGrowthBytes, 0)}");
                sb.AppendLine(
                    $"Minimum one-VMD payload benefit: " +
                    $"{MinimumCrossRigidSingleVmdExpectedArmyDraw:0.###} expected draws");
                sb.AppendLine(
                    $"Cross-rigid exact weighted Cartesian state cap: " +
                    $"{MaxCrossRigidJointStateCombinations:N0} combinations");
                sb.AppendLine(
                    $"Canonical joint-parent supersets: " +
                    $"{state.CrossRigidCanonicalJointParentCount:N0} parent(s), " +
                    $"{state.CrossRigidCanonicalJointContextsFolded:N0} narrower context(s) folded, " +
                    $"{state.CrossRigidCanonicalJointParentConflictCount:N0} multi-context parent(s) left separate");
                sb.AppendLine(
                    $"Partial canonical joint beam search: " +
                    $"{state.CrossRigidPartialCanonicalBeamUnionCandidates:N0} union candidate(s), " +
                    $"{state.CrossRigidPartialCanonicalBeamImprovements:N0} improvement(s)");
                sb.AppendLine(
                    $"Factored independent-root Cartesian contexts: " +
                    $"{state.CrossRigidFactoredJointParentCount:N0} parent(s), " +
                    $"{state.CrossRigidFactoredJointGroupCount:N0} separate context group(s), " +
                    $"{state.CrossRigidFactoredConditionalGroupCount:N0} with conditional dimensions, " +
                    $"{state.CrossRigidFactoredSingleCarrierGroupCount:N0} single-carrier factor(s), " +
                    $"{state.CrossRigidFactoredPackingSearchImprovements:N0} packing improvement(s), " +
                    $"{state.CrossRigidFactoredPairLookaheadSeeds:N0} pair lookahead seed(s)");
                sb.AppendLine(
                    $"Cross-rigid portfolio search: " +
                    $"{state.CrossRigidPortfolioOrderingsEvaluated:N0} ordering(s) evaluated; " +
                    $"raw-draw baseline={state.CrossRigidRawGreedyExpectedDraws:0.###}, " +
                    $"best={state.CrossRigidBestPortfolioExpectedDraws:0.###} expected draws; " +
                    $"pair ordering trials={state.CrossRigidPortfolioPairTrials:N0}, " +
                    $"context cohort trials={state.CrossRigidPortfolioContextCohortTrials:N0}, " +
                    $"local insertion trials={state.CrossRigidPortfolioLocalInsertionTrials:N0}, " +
                    $"local improvements={state.CrossRigidPortfolioLocalInsertionImprovements:N0}, " +
                    $"local pair insertions={state.CrossRigidPortfolioLocalPairInsertionTrials:N0}, " +
                    $"local pair improvements={state.CrossRigidPortfolioLocalPairInsertionImprovements:N0}");
                sb.AppendLine(
                    $"Payload candidates: " +
                    $"{state.CrossRigidPayloadSelectionEntries.Count:N0}");
                sb.AppendLine(
                    $"Payloads accepted: {selectedPayloads.Length:N0}");
                sb.AppendLine(
                    $"Payloads rejected: {rejectedPayloads.Length:N0}");
                sb.AppendLine(
                    $"Cross-rigid protected primary portfolio: " +
                    $"{state.CrossRigidPayloadSelectionEntries.Count(entry => state.CrossRigidLegacyEligiblePayloadIds.Contains(entry.GeneratedPayloadId)):N0} eligible payload(s), " +
                    $"{selectedPayloads.Count(entry => state.CrossRigidLegacyEligiblePayloadIds.Contains(entry.GeneratedPayloadId)):N0} accepted");
                sb.AppendLine(
                    $"Supplementary subgroup-only fallback payloads: " +
                    $"{state.CrossRigidPayloadSelectionEntries.Count(entry => !state.CrossRigidLegacyEligiblePayloadIds.Contains(entry.GeneratedPayloadId)):N0} candidate(s), " +
                    $"{selectedPayloads.Count(entry => !state.CrossRigidLegacyEligiblePayloadIds.Contains(entry.GeneratedPayloadId)):N0} accepted");
                sb.AppendLine(
                    $"Selected-primary geometry reused by supplementary subgroups: " +
                    $"{state.CrossRigidSharedPayloadFallbackCandidatePlans:N0} candidate plan(s), " +
                    $"{state.CrossRigidSharedPayloadFallbackAdmittedPlanIds.Count:N0} admitted plan(s), " +
                    $"{state.CrossRigidSharedPayloadFallbackAcceptedStates:N0}/" +
                    $"{state.CrossRigidSharedPayloadFallbackCompatibleStates:N0} compatible state(s), " +
                    $"{state.CrossRigidSharedPayloadFallbackExpectedDraws:0.###} extra expected draw(s), " +
                    $"0 additional generated geometry bytes, " +
                    $"{FormatMiB(state.CrossRigidSharedPayloadFallbackResidentBytes)} additional expected residency, " +
                    $"{state.CrossRigidSharedPayloadFallbackBudgetRejections:N0} blocked payload(s) by residency budget");
                sb.AppendLine(
                    $"Accepted payloads reused by multiple selected rewrite plans: " +
                    $"{selectedRewritePlans
                        .GroupBy(
                            plan => plan.GeneratedPayloadId,
                            StringComparer.Ordinal)
                        .Count(group => group.Count() > 1):N0}");
                sb.AppendLine(
                    $"Accepted one-VMD payloads: " +
                    $"{selectedPayloads.Count(entry => entry.VmdCount == 1):N0}");
                sb.AppendLine(
                    $"Selected VMD rewrite-plan references: " +
                    $"{selectedRewritePlans.Length:N0}");
                sb.AppendLine(
                    $"Standalone candidate expected draws (overlap-inclusive): " +
                    $"{totalExpectedDraws:0.###}");
                sb.AppendLine(
                    $"Conflict-deduplicated expected draws selected: " +
                    $"{selectedExpectedDraws:0.###} " +
                    $"({(totalExpectedDraws > AtlasValueGateExpectedDrawEpsilon ? selectedExpectedDraws / totalExpectedDraws : 0):P1} of overlap-inclusive candidate total)");
                var baselineAllRoots =
                    state.CrossRigidBaselineExpectedDrawsByVmd.Values.Sum();
                var baselineRewrittenRoots = selectedRewritePlans
                    .Select(plan => plan.VmdPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Sum(vmdPath =>
                        state.CrossRigidBaselineExpectedDrawsByVmd
                            .GetValueOrDefault(vmdPath));
                sb.AppendLine(
                    $"Baseline expected battle draws (all analyzed VMD roots): " +
                    $"{baselineAllRoots:0.###}");
                sb.AppendLine(
                    $"Baseline expected battle draws (VMD roots with selected rewrites): " +
                    $"{baselineRewrittenRoots:0.###}");
                sb.AppendLine(
                    $"Selected expected draws as share of baseline: " +
                    $"{(baselineAllRoots > AtlasValueGateExpectedDrawEpsilon ? selectedExpectedDraws / baselineAllRoots : 0):P1} of all analyzed, " +
                    $"{(baselineRewrittenRoots > AtlasValueGateExpectedDrawEpsilon ? selectedExpectedDraws / baselineRewrittenRoots : 0):P1} of rewritten roots");
                sb.AppendLine(
                    $"Baseline WSModels without resolvable geometry (draws omitted): " +
                    $"{state.CrossRigidBaselineUnresolvedWsModelCount:N0}; " +
                    $"rigids loaded only for the baseline pass (upper bound): " +
                    $"{state.CrossRigidBaselineRigidsLoaded:N0}");
                sb.AppendLine(
                    $"Selected generated geometry payload: " +
                    $"{FormatMiB(selectedGeneratedGeometryBytes)}");
                sb.AppendLine(
                    $"Selected expected resident generated geometry: " +
                    $"{FormatMiB(selectedExpectedResidentGeometryBytes)}");
                sb.AppendLine(
                    $"Selected physical geometry per expected draw: " +
                    $"{FormatMiB(selectedExpectedDraws > AtlasValueGateExpectedDrawEpsilon ? selectedGeneratedGeometryBytes / selectedExpectedDraws : 0, 2)}");
                sb.AppendLine(
                    $"Selected resident geometry per expected draw: " +
                    $"{FormatMiB(selectedExpectedDraws > AtlasValueGateExpectedDrawEpsilon ? selectedExpectedResidentGeometryBytes / selectedExpectedDraws : 0, 2)}");

                // Report marginal, conflict-deduplicated benefit rather than
                // the overlap-inclusive standalone benefit when explaining
                // how much geometry the selected portfolio actually costs.
                // These are diagnostic cohorts; the selected portfolio and
                // its existing budgets remain unchanged.
                sb.AppendLine(
                    "Accepted cross-rigid geometry cost by payload reuse:");
                foreach (var cohort in selectedPayloads
                             .GroupBy(entry =>
                                 entry.VmdCount == 1
                                     ? "one-VMD payloads"
                                     : "multi-VMD payloads"))
                {
                    var cohortDraws = cohort.Sum(entry =>
                        entry.MarginalExpectedArmyDrawCallsEliminated);
                    var cohortGeometry = cohort.Sum(entry =>
                        entry.GeneratedGeometryBytes);
                    sb.AppendLine(
                        $"  {cohort.Key}: {cohort.Count():N0} payload(s), " +
                        $"marginal={cohortDraws:0.###} expected draw(s), " +
                        $"physical={FormatMiB(cohortGeometry)}, " +
                        $"physical per expected draw=" +
                        FormatMiB(
                            cohortDraws > AtlasValueGateExpectedDrawEpsilon
                                ? cohortGeometry / cohortDraws
                                : 0,
                            2));
                }

                sb.AppendLine(
                    "Accepted cross-rigid payload cost curve " +
                    "(ascending physical cost per marginal expected draw):");
                foreach (var line in
                         BuildCrossRigidPayloadCostCurveLines(
                             selectedPayloads))
                {
                    sb.AppendLine(line);
                }

                sb.AppendLine(
                    "Highest physical cost per marginal expected draw " +
                    "among selected payloads (top 12):");
                foreach (var entry in selectedPayloads
                             .Where(entry =>
                                 entry.MarginalExpectedArmyDrawCallsEliminated >
                                 AtlasValueGateExpectedDrawEpsilon)
                             .OrderByDescending(entry =>
                                 entry.GeneratedGeometryBytes /
                                 entry.MarginalExpectedArmyDrawCallsEliminated)
                             .ThenBy(entry =>
                                 entry.GeneratedPayloadId,
                                 StringComparer.Ordinal)
                             .Take(12))
                {
                    sb.AppendLine(
                        $"  payload={entry.GeneratedPayloadId}, " +
                        $"VMDs={entry.VmdCount:N0}, " +
                        $"marginal={entry.MarginalExpectedArmyDrawCallsEliminated:0.######} draws, " +
                        $"physical={FormatMiB(entry.GeneratedGeometryBytes)}, " +
                        $"physical per expected draw=" +
                        FormatMiB(
                            entry.GeneratedGeometryBytes /
                            entry.MarginalExpectedArmyDrawCallsEliminated,
                            2));
                }


                if (rejectedPayloads.Length != 0)
                {
                    sb.AppendLine("Cross-rigid payload value-gate rejection breakdown:");
                    foreach (var group in rejectedPayloads
                                 .GroupBy(entry => entry.Decision)
                                 .OrderByDescending(group => group.Count())
                                 .ThenBy(group => group.Key))
                    {
                        sb.AppendLine(
                            $"  {group.Key}: {group.Count():N0} payload(s), " +
                            $"standalone={group.Sum(entry => entry.ExpectedArmyDrawCallsEliminated):0.###} expected draw(s), " +
                            $"marginal={group.Sum(entry => entry.MarginalExpectedArmyDrawCallsEliminated):0.###}, " +
                            $"{FormatMiB(group.Sum(entry => entry.GeneratedGeometryBytes))} geometry");
                    }
                }

                if (state.CrossRigidRewriteConflictReasonCounts.Count != 0)
                {
                    sb.AppendLine(
                        "Cross-rigid rewrite-conflict diagnostics " +
                        "(final zero-marginal payloads):");
                    foreach (var (reason, count) in
                             state.CrossRigidRewriteConflictReasonCounts
                                 .OrderByDescending(entry => entry.Value)
                                 .ThenBy(
                                     entry => entry.Key,
                                     StringComparer.Ordinal))
                    {
                        sb.AppendLine(
                            $"  {reason}: {count:N0} payload(s), " +
                            $"{state.CrossRigidRewriteConflictExpectedDraws.GetValueOrDefault(reason):0.###} standalone expected draw(s)");
                        if (!state.CrossRigidRewriteConflictExamples.TryGetValue(
                                reason,
                                out var examples))
                        {
                            continue;
                        }

                        foreach (var example in examples)
                            sb.AppendLine($"    - {example}");
                    }
                }

                sb.AppendLine();
                sb.AppendLine("Cross-rigid merge emission");
                sb.AppendLine("--------------------------");
                var selectedRewritePlanCount =
                    selectedRewritePlans.Length;
                sb.AppendLine(
                    $"Selected rewrite-plan references: " +
                    $"{selectedRewritePlanCount:N0}");
                sb.AppendLine(
                    $"Rewrite plans emitted: " +
                    $"{state.CrossRigidRewritePlansApplied:N0}");
                sb.AppendLine(
                    $"Selected rewrite plans left unchanged: " +
                    $"{Math.Max(0, selectedRewritePlanCount - state.CrossRigidRewritePlansApplied):N0}");
                sb.AppendLine(
                    $"Generated payloads written: " +
                    $"{state.CrossRigidGeneratedPayloadIds.Count:N0}");
                sb.AppendLine(
                    $"Generated RMV2 files: " +
                    $"{state.CrossRigidGeneratedRigidPaths.Count:N0}");
                sb.AppendLine(
                    $"Generated WSModel files: " +
                    $"{state.CrossRigidGeneratedWsModelPaths.Count:N0}");
                sb.AppendLine(
                    $"Generated copy-on-write VMD files: " +
                    $"{state.CrossRigidGeneratedVmdPaths.Count:N0}");
                sb.AppendLine(
                    $"Identical generated child VMD copies deduplicated: " +
                    $"{state.CrossRigidGeneratedVmdFilesDeduplicated:N0}");
                sb.AppendLine(
                    $"Source-pack VMD files rewritten: " +
                    $"{state.CrossRigidRewrittenRootVmdPaths.Count:N0}");
                var selectedRewriteOccurrenceSetCount =
                    state.CrossRigidSelectedRewriteOccurrenceKeys.Count;
                sb.AppendLine(
                    $"Rewrite occurrence sets selected: " +
                    $"{selectedRewriteOccurrenceSetCount:N0}");
                sb.AppendLine(
                    $"Canonical joint Cartesian states selected: " +
                    $"{state.CrossRigidSelectedJointCombinationKeysByRewriteOccurrenceKey.Values.Sum(keys => keys.Count):N0}");
                sb.AppendLine(
                    $"Rewrite occurrence sets emitted: " +
                    $"{state.CrossRigidRewriteOccurrenceSetsApplied:N0}");
                sb.AppendLine(
                    $"Generated payloads with fewer active components than LOD 0 in a later LOD: " +
                    $"{CountCrossRigidPayloadsWithDroppedComponents(selectedRewritePlans):N0}");
                sb.AppendLine(
                    $"Scenario-estimated battle draws emitted: " +
                    $"{state.CrossRigidExpectedArmyDrawCallsEmitted:0.###}");
                sb.AppendLine(
                    $"Emitted rewrite-plan LOD draws: " +
                    $"{state.CrossRigidRawLodDrawsBefore:N0} -> " +
                    $"{state.CrossRigidRawLodDrawsAfter:N0} " +
                    $"(-{Math.Max(0, state.CrossRigidRawLodDrawsBefore - state.CrossRigidRawLodDrawsAfter):N0})");
                sb.AppendLine(
                    $"Retained original VMD/WSModel/RMV2 paths protected from pruning: " +
                    $"{state.CrossRigidRetainedSourceAssetPaths.Count:N0}");
                sb.AppendLine(
                    "Cross-rigid source-asset retirement: DISABLED; originals are retained in this first writer.");
                if (state.CrossRigidEmissionSkipCounts.Count != 0)
                {
                    sb.AppendLine("Cross-rigid emission skip breakdown:");
                    foreach (var (reason, count) in
                             state.CrossRigidEmissionSkipCounts
                                 .OrderByDescending(entry => entry.Value)
                                 .ThenBy(
                                     entry => entry.Key,
                                     StringComparer.Ordinal))
                    {
                        sb.AppendLine($"  {reason}: {count:N0}");
                        if (!state.CrossRigidEmissionSkipExamples.TryGetValue(
                                reason,
                                out var examples))
                        {
                            continue;
                        }

                        foreach (var example in examples)
                            sb.AppendLine($"    - {example}");
                    }
                }

                sb.AppendLine(
                    $"Cross-rigid analysis blockers: " +
                    $"{state.CrossRigidAnalysisBlockerCounts.Values.Sum():N0}");

                if (state.CrossRigidAnalysisBlockerCounts.Count != 0)
                {
                    sb.AppendLine("Cross-rigid blocker breakdown:");
                    foreach (var (reason, count) in
                             state.CrossRigidAnalysisBlockerCounts
                                 .OrderByDescending(entry => entry.Value)
                                 .ThenBy(
                                     entry => entry.Key,
                                     StringComparer.Ordinal))
                    {
                        sb.AppendLine($"  {reason}: {count:N0}");
                        if (reason.Equals(
                                "Cross-rigid source rigid headers are not merge-compatible",
                                StringComparison.Ordinal) &&
                            state.CrossRigidRigidHeaderBlockerCounts.Count != 0)
                        {
                            var uniqueRigidCombinations =
                                state.CrossRigidRigidHeaderBlockerRigidCombinations
                                    .Values
                                    .SelectMany(items => items)
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .Count();
                            var affectedVmdRoots =
                                state.CrossRigidRigidHeaderBlockerVmdRoots
                                    .Values
                                    .SelectMany(items => items)
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .Count();
                            sb.AppendLine(
                                $"    Deduplicated: {uniqueRigidCombinations:N0} unique rigid combination(s), " +
                                $"{affectedVmdRoots:N0} affected VMD root(s)");
                            sb.AppendLine(
                                "    Header incompatibility detail " +
                                "(observations / unique rigid combinations / VMD roots):");
                            foreach (var (headerReason, observationCount) in
                                     state.CrossRigidRigidHeaderBlockerCounts
                                         .OrderByDescending(entry => entry.Value)
                                         .ThenBy(
                                             entry => entry.Key,
                                             StringComparer.Ordinal))
                            {
                                var combinationCount =
                                    state.CrossRigidRigidHeaderBlockerRigidCombinations
                                        .GetValueOrDefault(headerReason)?
                                        .Count ?? 0;
                                var vmdRootCount =
                                    state.CrossRigidRigidHeaderBlockerVmdRoots
                                        .GetValueOrDefault(headerReason)?
                                        .Count ?? 0;
                                sb.AppendLine(
                                    $"      {headerReason}: " +
                                    $"{observationCount:N0} / " +
                                    $"{combinationCount:N0} / " +
                                    $"{vmdRootCount:N0}");
                            }

                            const int maxCrossRigidHeaderProfilePatterns = 16;
                            if (state.CrossRigidRigidHeaderQualityProfiles.Count != 0)
                            {
                                sb.AppendLine(
                                    "    Top quality-level profile mismatch patterns " +
                                    "(observations / unique rigid combinations / VMD roots):");
                                foreach (var (profile, aggregate) in
                                         state.CrossRigidRigidHeaderQualityProfiles
                                             .OrderByDescending(entry =>
                                                 entry.Value.RigidCombinations.Count)
                                             .ThenByDescending(entry =>
                                                 entry.Value.ObservationCount)
                                             .ThenBy(
                                                 entry => entry.Key,
                                                 StringComparer.Ordinal)
                                             .Take(maxCrossRigidHeaderProfilePatterns))
                                {
                                    sb.AppendLine(
                                        $"      {profile}: " +
                                        $"{aggregate.ObservationCount:N0} / " +
                                        $"{aggregate.RigidCombinations.Count:N0} / " +
                                        $"{aggregate.VmdRoots.Count:N0}");
                                }
                            }

                            if (state.CrossRigidRigidHeaderFullLodProfiles.Count != 0)
                            {
                                sb.AppendLine(
                                    "    Top full LOD profile mismatch patterns " +
                                    "(observations / unique rigid combinations / VMD roots):");
                                foreach (var (profile, aggregate) in
                                         state.CrossRigidRigidHeaderFullLodProfiles
                                             .OrderByDescending(entry =>
                                                 entry.Value.RigidCombinations.Count)
                                             .ThenByDescending(entry =>
                                                 entry.Value.ObservationCount)
                                             .ThenBy(
                                                 entry => entry.Key,
                                                 StringComparer.Ordinal)
                                             .Take(maxCrossRigidHeaderProfilePatterns))
                                {
                                    sb.AppendLine(
                                        $"      {profile}: " +
                                        $"{aggregate.ObservationCount:N0} / " +
                                        $"{aggregate.RigidCombinations.Count:N0} / " +
                                        $"{aggregate.VmdRoots.Count:N0}");
                                }
                            }
                        }

                        if (!state.CrossRigidAnalysisBlockerExamples.TryGetValue(
                                reason,
                                out var examples))
                        {
                            continue;
                        }

                        foreach (var example in examples)
                            sb.AppendLine($"    - {example}");
                    }
                }

                if (state.CrossRigidAnalysisDiagnosticCounts.Count != 0)
                {
                    sb.AppendLine("Cross-rigid non-blocking diagnostics:");
                    foreach (var (reason, count) in
                             state.CrossRigidAnalysisDiagnosticCounts
                                 .OrderByDescending(entry => entry.Value)
                                 .ThenBy(
                                     entry => entry.Key,
                                     StringComparer.Ordinal))
                    {
                        sb.AppendLine($"  {reason}: {count:N0}");
                        if (!state.CrossRigidAnalysisDiagnosticExamples.TryGetValue(
                                reason,
                                out var examples))
                        {
                            continue;
                        }

                        foreach (var example in examples)
                            sb.AppendLine($"    - {example}");
                    }
                }

                if (state.CrossRigidJointStateCapExclusionsByVmd.Count != 0)
                {
                    var excludedByVmd =
                        state.CrossRigidJointStateCapExclusionsByVmd;
                    sb.AppendLine(
                        "Cross-rigid joint state-cap exclusions by VMD " +
                        "(overlap-inclusive potential, not attainable savings):");
                    sb.AppendLine(
                        $"  totals: roots={excludedByVmd.Count:N0}, " +
                        $"plans={excludedByVmd.Sum(item => item.Value.RewritePlanIds.Count):N0}, " +
                        $"occurrenceSets={excludedByVmd.Sum(item => item.Value.OccurrenceSetCount):N0}, " +
                        $"two-source={excludedByVmd.Sum(item => item.Value.TwoSourceOccurrenceSetCount):N0}, " +
                        $"three-plus/other={excludedByVmd.Sum(item => item.Value.OtherSourceOccurrenceSetCount):N0}, " +
                        $"grossExpectedDraws={excludedByVmd.Sum(item => item.Value.OverlapInclusiveExpectedDraws):0.###}");
                    foreach (var (root, excluded) in excludedByVmd
                                 .OrderByDescending(item =>
                                     item.Value.OverlapInclusiveExpectedDraws)
                                 .ThenByDescending(item =>
                                     item.Value.RewritePlanIds.Count)
                                 .ThenBy(item => item.Key, StringComparer.Ordinal)
                                 .Take(20))
                    {
                        sb.AppendLine(
                            $"  {root}: plans={excluded.RewritePlanIds.Count:N0}, " +
                            $"occurrences={excluded.OccurrenceSetCount:N0}, " +
                            $"two-source={excluded.TwoSourceOccurrenceSetCount:N0}, " +
                            $"three-plus/other={excluded.OtherSourceOccurrenceSetCount:N0}, " +
                            $"commonAncestor={excluded.CommonAncestorOccurrenceSetCount:N0}, " +
                            $"direct={excluded.DirectJointOccurrenceSetCount:N0}, " +
                            $"other={excluded.OtherCapOccurrenceSetCount:N0}, " +
                            $"requiredStates=(<=8192:{excluded.RequiredUpTo8192:N0}, " +
                            $"<=16384:{excluded.RequiredUpTo16384:N0}, " +
                            $"<=65536:{excluded.RequiredUpTo65536:N0}, " +
                            $">65536:{excluded.RequiredAbove65536:N0}, " +
                            $"unknown:{excluded.UnknownRequiredStates:N0}, " +
                            $"max:{excluded.MaxRequiredWeightedStates:N0}), " +
                            $"grossExpectedDraws={excluded.OverlapInclusiveExpectedDraws:0.###}");
                    }
                }

                const int maxCrossRigidReportEntries = 40;
                if (state.CrossRigidGeneratedPayloadAnalysisEntries.Count != 0)
                {
                    sb.AppendLine("Top cross-rigid generated payloads:");
                    foreach (var payload in
                             state.CrossRigidGeneratedPayloadAnalysisEntries
                                 .OrderByDescending(item =>
                                     state.CrossRigidMergeAnalysisEntries
                                         .Where(plan =>
                                             plan.GeneratedPayloadId.Equals(
                                                 item.GeneratedPayloadId,
                                                 StringComparison.Ordinal))
                                         .Sum(plan =>
                                             plan.ExpectedArmyDrawCallsEliminated))
                                 .ThenByDescending(item =>
                                     item.GeneratedGeometryBytes)
                                 .Take(maxCrossRigidReportEntries))
                    {
                        var payloadPlans =
                            state.CrossRigidMergeAnalysisEntries
                                .Where(plan =>
                                    plan.GeneratedPayloadId.Equals(
                                        payload.GeneratedPayloadId,
                                        StringComparison.Ordinal))
                                .ToArray();
                        var expectedDrawSavings = payloadPlans.Sum(
                            plan =>
                                plan.ExpectedArmyDrawCallsEliminated);
                        var selection =
                            state.CrossRigidPayloadSelectionEntries
                                .FirstOrDefault(entry =>
                                    entry.GeneratedPayloadId.Equals(
                                        payload.GeneratedPayloadId,
                                        StringComparison.Ordinal));
                        var gateStatus = selection == null
                            ? "NOT_EVALUATED"
                            : selection.Accepted
                                ? "ACCEPT"
                                : selection.Decision.ToString();
                        var marginalExpectedDrawSavings =
                            selection?.MarginalExpectedArmyDrawCallsEliminated ??
                            expectedDrawSavings;
                        sb.AppendLine(
                            $"  payload={payload.GeneratedPayloadId}: gate={gateStatus}, rewritePlans=" +
                            $"{payload.RewritePlanCount:N0}, VMDs={payload.VmdCount:N0}, " +
                            $"expectedBattleDrawsSaved={expectedDrawSavings:0.###}, " +
                            $"marginalAtGate={marginalExpectedDrawSavings:0.###}, " +
                            $"generatedGeometry={FormatMiB(payload.GeneratedGeometryBytes)}, " +
                            $"residentProbability={payload.ExpectedBattleResidentProbability:0.######}, " +
                            $"expectedResidentGenerated={FormatMiB(payload.ExpectedResidentGeneratedGeometryBytes)}, " +
                            $"expectedResidentDisplaced={FormatMiB(payload.ExpectedResidentDisplacedSourceGeometryBytes)}, " +
                            $"externalConsumers={payload.ExternalStructuralConsumers.Length}, " +
                            $"consumerDiscovery=" +
                            $"{(payload.ConsumerDiscoveryComplete ? "COMPLETE" : "PARTIAL")}");
                        sb.AppendLine(
                            $"    WSModels: {string.Join(", ", payload.SourceWsModelPaths)}");
                        sb.AppendLine(
                            $"    Rigids: {string.Join(", ", payload.RigidPaths)}");
                        foreach (var lod in payload.Lods)
                        {
                            sb.AppendLine(
                                $"    LOD {lod.LodIndex}: " +
                                $"scenarioLOD={lod.ScenarioLodIndex}, " +
                                $"quality={lod.QualityLevel}, " +
                                $"distance={lod.CameraDistance:0.###}, " +
                                $"sourceLODs=[" +
                                $"{string.Join(", ", lod.SourceLodSelections.OrderBy(selection => selection.WsModelPath, StringComparer.OrdinalIgnoreCase).Select(selection => $"{Path.GetFileNameWithoutExtension(selection.WsModelPath)}={selection.SourceLodIndex}"))}], " +
                                $"activeComponents={lod.ActiveComponentCount}, " +
                                $"activeRigids={lod.ActiveRigidCount}, draws=" +
                                $"{lod.InputDrawsAfterSameRigidMerge}->" +
                                $"{lod.OutputDrawsAfterCrossRigidMerge}, " +
                                $"saved={lod.DrawsSaved}, " +
                                $"scenarioProbability={lod.LodProbability:0.###}");

                        }

                        foreach (var plan in payloadPlans
                                     .OrderByDescending(item =>
                                         item.ExpectedArmyDrawCallsEliminated)
                                     .Take(8))
                        {
                            sb.AppendLine(
                                $"    rewrite: {plan.VmdPath} @ " +
                                $"{plan.AttachmentIdentity}, probability=" +
                                $"{plan.AggregatedConfigurationProbability:0.######}, " +
                                $"expectedDrawsSaved=" +
                                $"{plan.ExpectedArmyDrawCallsEliminated:0.###}");
                        }

                        if (payload.ExternalStructuralConsumers.Length != 0)
                        {
                            sb.AppendLine(
                                $"    External consumers: " +
                                $"{string.Join(", ", payload.ExternalStructuralConsumers.Take(12))}" +
                                $"{(payload.ExternalStructuralConsumers.Length > 12 ? ", ..." : string.Empty)}");
                        }
                    }

                    if (state.CrossRigidGeneratedPayloadAnalysisEntries.Count >
                        maxCrossRigidReportEntries)
                    {
                        sb.AppendLine(
                            $"  ... " +
                            $"{state.CrossRigidGeneratedPayloadAnalysisEntries.Count - maxCrossRigidReportEntries:N0} more generated payload(s)");
                    }
                }
            }

            if (state.SourceBcnResidency != null && state.OutputBcnResidency != null)
            {
                var sourceBcn = state.SourceBcnResidency;
                var outputBcn = state.OutputBcnResidency;
                sb.AppendLine();
                sb.AppendLine("BCn texture resource payload and scenario residency estimate");
                sb.AppendLine("-----------------------------------------------------------");
                sb.AppendLine(
                    "Output reachability is measured after superseded assets are pruned.");
                sb.AppendLine(
                    $"Source reachable DDS textures: {sourceBcn.ReachableDdsCount:N0}; " +
                    $"BCn={sourceBcn.BcnTextureCount:N0}; unsupported/non-BCn={sourceBcn.UnsupportedDdsCount:N0}");
                sb.AppendLine(
                    $"Output reachable DDS textures: {outputBcn.ReachableDdsCount:N0}; " +
                    $"BCn={outputBcn.BcnTextureCount:N0}; unsupported/non-BCn={outputBcn.UnsupportedDdsCount:N0}");
                sb.AppendLine($"Source reachable BCn resource payload: {FormatMiB(sourceBcn.BcnBytes)}");
                sb.AppendLine($"Output reachable BCn resource payload: {FormatMiB(outputBcn.BcnBytes)}");
                sb.AppendLine(
                    $"Reachable BCn resource-payload delta: " +
                    $"{FormatResidencyDelta(sourceBcn.BcnBytes, outputBcn.BcnBytes)}");
                sb.AppendLine(
                    $"Scenario-estimated resident BCn payload: " +
                    $"{FormatMiB(sourceBcn.EstimatedScenarioResidentBcnBytes)} -> " +
                    $"{FormatMiB(outputBcn.EstimatedScenarioResidentBcnBytes)} " +
                    $"({FormatResidencyDelta(sourceBcn.EstimatedScenarioResidentBcnBytes, outputBcn.EstimatedScenarioResidentBcnBytes)})");
                sb.AppendLine(
                    $"Generated atlas BCn resource payload: {FormatMiB(outputBcn.GeneratedAtlasBcnBytes)}");
                sb.AppendLine(
                    $"Scenario-estimated resident generated-atlas BCn payload: " +
                    $"{FormatMiB(outputBcn.EstimatedScenarioResidentGeneratedAtlasBcnBytes)}");
                sb.AppendLine($"Source BCn formats: {FormatBcnBreakdown(sourceBcn)}");
                sb.AppendLine($"Output BCn formats: {FormatBcnBreakdown(outputBcn)}");
                sb.AppendLine(
                    $"BCn textures without an army WSModel mapping: " +
                    $"source={sourceBcn.ArmyUnmappedBcnTextureCount:N0}, " +
                    $"output={outputBcn.ArmyUnmappedBcnTextureCount:N0}");
                sb.AppendLine(
                    "Reachable resource payload is the compressed 4x4 BCn block payload for every declared mip " +
                    "of every reachable BCn DDS; it is not scenario weighted.");
                sb.AppendLine(
                    "Scenario-estimated resident payload weights each texture by the modeled probability that " +
                    "at least one resolved visual in the configured army scenario requires it. This is a " +
                    "model-derived residency proxy, not measured GPU residency and not a visible-texel byte count.");
                sb.AppendLine(
                    "Both figures exclude driver allocation/alignment overhead, non-BCn DDS formats, " +
                    "and textures resolved outside the processed pack.");
                sb.AppendLine();
            }

            sb.AppendLine($"Optimize geometry: {(state.OptimizeGeometryEnabled ? "YES" : "NO")}");
            if (state.OptimizeGeometryEnabled)
            {
                sb.AppendLine($"Geometry meshes optimized: {state.GeometryMeshesOptimized}");
                sb.AppendLine($"Geometry vertices before: {state.GeometryVerticesBefore:N0}");
                sb.AppendLine($"Geometry vertices after: {state.GeometryVerticesAfter:N0}");
                sb.AppendLine($"Geometry vertices removed: {state.GeometryVerticesRemoved:N0}");
                sb.AppendLine($"Unreferenced vertices removed: {state.GeometryUnreferencedVerticesRemoved:N0}");
                sb.AppendLine($"Duplicate vertices removed: {state.GeometryDuplicateVerticesRemoved:N0}");
            }
            sb.AppendLine();

            if (state.UnitCategoryResolution != null)
            {
                var unitResolution = state.UnitCategoryResolution;
                var unitUsages = unitResolution.UsagesByVmd.Values
                    .SelectMany(usages => usages)
                    .ToList();
                var directAssetUsages = unitResolution.DirectAssetUsagesByPath.Values
                    .SelectMany(usages => usages)
                    .ToList();

                sb.AppendLine("Unit category DB resolution");
                sb.AppendLine("---------------------------");
                foreach (var table in unitResolution.ParsedRowsByTable
                             .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"Parsed {table.Key}: {table.Value:N0} row(s)");
                }

                sb.AppendLine($"Relevant DB table files read: {unitResolution.TableFilesRead:N0}");
                sb.AppendLine(
                    $"Gameplay resolver global health: " +
                    $"{(unitResolution.IsGameplayResolutionHealthy ? "HEALTHY" : "UNHEALTHY")}");
                if (!string.IsNullOrWhiteSpace(unitResolution.GameplayResolutionHealthMessage))
                {
                    sb.AppendLine(
                        $"Gameplay resolver health detail: " +
                        unitResolution.GameplayResolutionHealthMessage);
                }
                sb.AppendLine($"Directly DB-resolved VMD roots: {unitResolution.DirectlyResolvedVmdCount:N0}");
                sb.AppendLine($"VMD roots resolved through child propagation: {unitResolution.PropagatedVmdCount:N0}");
                sb.AppendLine($"Resolved VMD roots: {unitResolution.UsagesByVmd.Count:N0}");
                sb.AppendLine($"Unresolved VMD roots: {unitResolution.UnresolvedVmdRoots.Count:N0}");
                sb.AppendLine(
                    $"VMD roots tainted by unresolved gameplay consumers: " +
                    $"{unitResolution.UnresolvedConsumersByVmd.Count:N0}");
                sb.AppendLine($"Resolved VMD-to-unit links: {unitUsages.Count:N0}");
                sb.AppendLine(
                    $"Resolved direct engine assets: {unitResolution.DirectAssetUsagesByPath.Count:N0} " +
                    $"asset(s), {directAssetUsages.Count:N0} usage(s)");
                sb.AppendLine(
                    $"Scenario roster after scope: {unitResolution.RosterUnits.Count:N0} unit(s); " +
                    $"scope={unitResolution.Scenario.RosterScope}" +
                    (string.IsNullOrWhiteSpace(unitResolution.Scenario.RosterScopeKey)
                        ? string.Empty
                        : $" ({unitResolution.Scenario.RosterScopeKey})"));
                sb.AppendLine(
                    $"Scenario entity scaling: unit={unitResolution.Scenario.UnitSizeScale:0.###}, " +
                    $"crew={unitResolution.Scenario.CrewScale:0.###}, " +
                    $"rounding={unitResolution.Scenario.EngineRoundingPolicy}");
                sb.AppendLine(
                    $"Scenario lifecycle: destroyed={unitResolution.Scenario.DestructionProbability:0.###}, " +
                    $"destruct-transition={unitResolution.Scenario.DestructTransitionProbability:0.###}; " +
                    $"LOD distribution={string.Join(", ", unitResolution.Scenario.LodDistribution.OrderBy(x => x.Key).Select(x => $"lod{x.Key}={x.Value:0.###}"))}");

                var rosterComponents = unitResolution.RosterUnits
                    .SelectMany(unit => unit.Components.Select(component => (Unit: unit, Component: component)))
                    .ToList();
                var trueAssetReuse = rosterComponents
                    .GroupBy(entry => entry.Component.AssetPath, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new
                    {
                        AssetPath = group.Key,
                        UnitCount = group.Select(entry => entry.Unit.Identity)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count(),
                        Roles = group.Select(entry => entry.Component.Role).Distinct().OrderBy(role => role).ToArray(),
                        States = group.Select(entry => entry.Component.State).Distinct().OrderBy(stateValue => stateValue).ToArray(),
                        Lods = group.Select(entry => entry.Component.Lod).Distinct().OrderBy(lod => lod).ToArray(),
                        ExpectedEntityReferences = group.Sum(entry =>
                            entry.Unit.VisualCounts.ForRole(entry.Component.Role) *
                            entry.Component.ScenarioPresenceProbability),
                        FactionCount = group.SelectMany(entry => entry.Unit.FactionKeys)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count(),
                        CultureCount = group.SelectMany(entry => entry.Unit.CultureKeys)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count(),
                        ExampleUnits = group.Select(entry => entry.Unit.MainUnitKey)
                            .Where(key => !string.IsNullOrWhiteSpace(key))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                            .Take(4)
                            .ToArray(),
                    })
                    .Where(entry => entry.UnitCount > 1)
                    .OrderByDescending(entry => entry.UnitCount)
                    .ThenBy(entry => entry.AssetPath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                sb.AppendLine(
                    $"True visual-asset reuse: {trueAssetReuse.Count:N0} asset path(s) referenced by multiple resolved units.");
                sb.AppendLine(
                    "True reuse above is DB unit-to-asset identity reuse; atlas/mesh compatibility is reported separately and does not imply shared source assets.");
                foreach (var reuse in trueAssetReuse.Take(20))
                {
                    sb.AppendLine(
                        $"  reuse: {reuse.AssetPath} | units={reuse.UnitCount:N0} | " +
                        $"roles={string.Join(",", reuse.Roles)} | states={string.Join(",", reuse.States)} | " +
                        $"lods={string.Join(",", reuse.Lods)} | entity-refs={reuse.ExpectedEntityReferences:0.###} | " +
                        $"factions={reuse.FactionCount:N0} | cultures={reuse.CultureCount:N0} | " +
                        $"example-units={string.Join(",", reuse.ExampleUnits)}");
                }
                if (trueAssetReuse.Count > 20)
                    sb.AppendLine($"  ... {trueAssetReuse.Count - 20:N0} more reused asset path(s)");

                foreach (var category in Enum.GetValues<Wh3ArmyUnitCategory>())
                {
                    var categoryUsages = unitUsages
                        .Where(usage => usage.Category == category)
                        .ToList();
                    if (categoryUsages.Count == 0)
                        continue;

                    sb.AppendLine(
                        $"{category}: roots=" +
                        $"{categoryUsages.Select(usage => usage.VmdPath).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0}, " +
                        $"unit-links={categoryUsages.Count:N0}, " +
                        $"median-entities=" +
                        $"{categoryUsages.Select(usage => usage.EntityCount).OrderBy(value => value).ElementAt(categoryUsages.Count / 2):N0}");
                }

                if (unitResolution.UsagesByVmd.Count != 0)
                {
                    sb.AppendLine("Resolved VMD category mappings:");
                    foreach (var (vmdPath, usages) in unitResolution.UsagesByVmd
                                 .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        var categories = usages
                            .Select(usage => usage.Category)
                            .Distinct()
                            .OrderBy(category => category)
                            .ToArray();
                        sb.AppendLine(
                            $"  {vmdPath} | categories={string.Join(",", categories)} | " +
                            $"unit-links={usages.Count}");

                        foreach (var usage in usages)
                        {
                            sb.AppendLine(
                                $"    main={usage.MainUnitKey} | land={usage.LandUnitKey} | " +
                                $"caste={usage.Caste} | land-category={usage.LandCategory} | " +
                                $"ui-group={usage.UiGroupKey} | category={usage.Category} | " +
                                $"role={usage.VisualRole} | provenance-paths={usage.Provenance.Count:N0} | " +
                                $"entities={usage.EntityCount} | num-men={usage.NumMen} | " +
                                $"components=riders:{usage.VisualCounts.Riders}," +
                                $"mounts:{usage.VisualCounts.Mounts}," +
                                $"engines:{usage.VisualCounts.Engines}," +
                                $"crew:{usage.VisualCounts.Crew}");
                            foreach (var provenance in usage.Provenance)
                            {
                                sb.AppendLine(
                                    $"      via={provenance.ConsumerType} | " +
                                    $"kind={(provenance.IsTransitiveChild ? "child" : "direct")} | " +
                                    $"root-vmd={provenance.RootVmdPath} | " +
                                    $"parent-vmd={provenance.ParentVmdPath} | " +
                                    $"faction={provenance.FactionKey} | " +
                                    $"subculture={provenance.SubcultureKey} | " +
                                    $"culture={provenance.CultureKey} | " +
                                    $"uniform={provenance.UniformName} | " +
                                    $"art-set={provenance.ArtSetId}");
                            }
                        }
                    }
                }

                if (unitResolution.UnresolvedConsumersByVmd.Count != 0)
                {
                    sb.AppendLine("Tainted VMD gameplay consumers:");
                    foreach (var (vmdPath, consumers) in unitResolution.UnresolvedConsumersByVmd
                                 .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                                 .Take(50))
                    {
                        sb.AppendLine($"  {vmdPath} | unresolved-consumers={consumers.Count:N0}");
                        foreach (var consumer in consumers.Take(8))
                        {
                            sb.AppendLine(
                                $"    main={consumer.MainUnitKey} | land={consumer.LandUnitKey} | " +
                                $"category={consumer.Category} | role={consumer.VisualRole} | " +
                                $"reason={consumer.Reason}");
                        }
                    }
                    if (unitResolution.UnresolvedConsumersByVmd.Count > 50)
                    {
                        sb.AppendLine(
                            $"  ... {unitResolution.UnresolvedConsumersByVmd.Count - 50:N0} more VMD path(s)");
                    }
                }

                if (unitResolution.UnresolvedVmdRoots.Count != 0)
                {
                    sb.AppendLine("Unresolved VMD roots:");
                    foreach (var root in unitResolution.UnresolvedVmdRoots.Take(50))
                        sb.AppendLine($"  {root}");
                    if (unitResolution.UnresolvedVmdRoots.Count > 50)
                    {
                        sb.AppendLine(
                            $"  ... {unitResolution.UnresolvedVmdRoots.Count - 50:N0} more");
                    }
                }

                if (unitResolution.Diagnostics.Count != 0)
                {
                    sb.AppendLine("Resolver diagnostics:");
                    foreach (var diagnostic in unitResolution.Diagnostics.Take(50))
                        sb.AppendLine($"  {diagnostic}");
                    if (unitResolution.Diagnostics.Count > 50)
                    {
                        sb.AppendLine(
                            $"  ... {unitResolution.Diagnostics.Count - 50:N0} more");
                    }
                }

                sb.AppendLine();
            }

            if (state.ArmyResidencyModel != null)
            {
                sb.AppendLine("Scenario visibility and residency model");
                sb.AppendLine("---------------------------------------");
                foreach (var category in state.ArmyResidencyModel.Scenario.ArmySlotTemplate.Keys)
                {
                    var culturePopulations = state.ArmyResidencyModel.UnitsByCultureAndCategory.Keys
                        .Select(culture =>
                            state.ArmyResidencyModel.UnitsByCultureAndCategory[culture][category].Count)
                        .Where(count => count > 0)
                        .ToArray();
                    sb.AppendLine(
                        $"{category}: slots={state.ArmyResidencyModel.Scenario.ArmySlotTemplate[category]}, " +
                        $"resolved-units={state.ArmyResidencyModel.UnitsByCategory[category].Count:N0}, " +
                        $"culture-population-range=" +
                        $"{(culturePopulations.Length == 0 ? "0" : $"{culturePopulations.Min():N0}-{culturePopulations.Max():N0}")}");
                }

                sb.AppendLine(
                    "Batch residency is evaluated per culture, then combined from player and opponent one-army marginals. Unrelated cultures contribute zero for culture-local assets; same-culture mirrors and proven cross-culture sharing contribute automatically without a culture-pair matrix.");
                sb.AppendLine(
                    "VMD visual model: slot probability controls activation; child meshes/references in an active slot are treated as equal alternatives; missing probability means 1.");
                sb.AppendLine(
                    "Texture residency converts each visual role's probability through its resolved component count before applying category slots.");
                sb.AppendLine(
                    "Expected draw-call eliminations enumerate concrete VMD visual configurations and only credit meshes that co-occur in the same state; oversized state spaces fall back to the conservative marginal model.");
                sb.AppendLine(
                    $"Scenario: unit-scale={state.ArmyResidencyModel.Scenario.UnitSizeScale:0.###}, " +
                    $"crew-scale={state.ArmyResidencyModel.Scenario.CrewScale:0.###}, " +
                    $"engine-rounding={state.ArmyResidencyModel.Scenario.EngineRoundingPolicy}, " +
                    $"destroyed={state.ArmyResidencyModel.Scenario.DestructionProbability:0.###}, " +
                    $"destruct-transition={state.ArmyResidencyModel.Scenario.DestructTransitionProbability:0.###}, " +
                    $"scope={state.ArmyResidencyModel.Scenario.RosterScope}" +
                    (string.IsNullOrWhiteSpace(state.ArmyResidencyModel.Scenario.RosterScopeKey)
                        ? "."
                        : $" ({state.ArmyResidencyModel.Scenario.RosterScopeKey})."));
                sb.AppendLine(
                    "The old aggregate VMD-residency and unweighted merge-affinity metrics remain non-regression guards.");
                sb.AppendLine();

                if (state.ArmyLocalitySplitEvaluations.Count != 0)
                {
                    sb.AppendLine("Army-aware locality split evaluations");
                    foreach (var (entry, index) in state.ArmyLocalitySplitEvaluations
                                 .Select((entry, index) => (entry, index + 1)))
                    {
                        sb.AppendLine(
                            $"  #{index}: {(entry.Accepted ? "ACCEPTED" : "rejected")} " +
                            $"[{entry.Decision}] | scenario-estimated " +
                            $"{entry.BaselineExpectedArmyResidentPixels:N0} -> " +
                            $"{entry.ProposedExpectedArmyResidentPixels:N0} " +
                            $"({entry.ProposedExpectedArmyResidentPixels - entry.BaselineExpectedArmyResidentPixels:+0;-0;0}); " +
                            $"VMD-resident {entry.BaselineVmdResidentPixels:N0} -> " +
                            $"{entry.ProposedVmdResidentPixels:N0} " +
                            $"({entry.ProposedVmdResidentPixels - entry.BaselineVmdResidentPixels:+0;-0;0}); " +
                            $"global {entry.BaselineGlobalPixels:N0} -> " +
                            $"{entry.ProposedGlobalPixels:N0} " +
                            $"({entry.ProposedGlobalPixels - entry.BaselineGlobalPixels:+0;-0;0})");
                    }

                    sb.AppendLine();
                }
            }

            sb.AppendLine("Phase timings");
            sb.AppendLine("-------------");
            foreach (var phase in state.PhaseDurations)
                sb.AppendLine($"{phase.Key}: {phase.Value.TotalMilliseconds:N0} ms");
            if (state.CrossRigidTimingDetails.Count != 0)
            {
                sb.AppendLine();
                sb.AppendLine("Cross-rigid timing details (included in parent phases)");
                sb.AppendLine("------------------------------------------------------");
                foreach (var detail in state.CrossRigidTimingDetails)
                {
                    sb.AppendLine(
                        $"{detail.Key}: {detail.Value.TotalMilliseconds:N0} ms");
                }
            }

            var interactiveWait = state.PhaseDurations
                .Where(x => x.Key.StartsWith("Wait for ", StringComparison.Ordinal))
                .Sum(x => x.Value.TotalMilliseconds);
            sb.AppendLine($"Interactive wait time: {interactiveWait:N0} ms");
            sb.AppendLine(
                $"Processing total excluding user waits: " +
                $"{Math.Max(0, state.TotalElapsed.TotalMilliseconds - interactiveWait):N0} ms");
            sb.AppendLine($"Total wall time: {state.TotalElapsed.TotalMilliseconds:N0} ms");
            sb.AppendLine();

            if (AtlasProfilingEnabled)
            {
                sb.AppendLine("Generated atlas texture timings");
                sb.AppendLine("-------------------------------");
                if (state.GeneratedTextureTimings.Count == 0)
                {
                    sb.AppendLine("(none)");
                }
                else
                {
                    foreach (var timing in state.GeneratedTextureTimings
                                 .OrderByDescending(x => x.TotalElapsed)
                                 .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
                    {
                    var rasterMs = (long)Math.Round(timing.RasterElapsed.TotalMilliseconds);
                    var compressionMs = (long)Math.Round(timing.CompressionElapsed.TotalMilliseconds);
                    var totalMs = (long)Math.Round(timing.TotalElapsed.TotalMilliseconds);
                    sb.AppendLine(
                        $"{timing.Path} | {timing.Width}x{timing.Height} | " +
                        $"{timing.TextureType} | {timing.Format} | " +
                        $"raster={rasterMs}ms | compress={compressionMs}ms | total={totalMs}ms | " +
                        $"split={(timing.UsedLargeBcSplitCompression ? "YES" : "NO")}");

                    var raster = timing.RasterStatistics;
                    sb.AppendLine(
                        $"  raster paths: source-ids={raster.DecodedSourceCount} | " +
                        $"unique-dds={raster.UniqueDecodedDdsCount} | " +
                        $"decode={Math.Round(raster.DdsDecodeElapsed.TotalMilliseconds)}ms | " +
                        $"compose={Math.Round(raster.ComposeElapsed.TotalMilliseconds)}ms | " +
                        $"mips={raster.MipLevelsBuilt} | " +
                        $"row-copy-attempts={raster.RowCopyAttempts} | " +
                        $"row-copy-placements={raster.RowCopyPlacements} | " +
                        $"row-copy-fallbacks={raster.RowCopyFallbacks} | " +
                        $"row-copy-pixels={raster.RowCopyPixels} | " +
                        $"mapped-core-pixels={raster.MappedCorePixels} | " +
                        $"constant-core-pixels={raster.ConstantCorePixels} | " +
                        $"padding-pixels={raster.PaddingPixels}");

                    var compression = timing.CompressionStatistics;
                    if (compression != null)
                    {
                        var stripeTimes = compression.Mip0StripeElapsed.Count == 0
                            ? "-"
                            : string.Join(
                                ",",
                                compression.Mip0StripeElapsed.Select(
                                    x => ((long)Math.Round(x.TotalMilliseconds)).ToString()));
                        sb.AppendLine(
                            $"  compression jobs: workers={compression.MaxDegreeOfParallelism} | " +
                            $"parallel-wall={Math.Round(compression.ParallelWallElapsed.TotalMilliseconds)}ms | " +
                            $"mip0-stripes=[{stripeTimes}]ms | " +
                            $"mip-tail={Math.Round(compression.MipTailElapsed.TotalMilliseconds)}ms | " +
                            $"stitch={Math.Round(compression.StitchElapsed.TotalMilliseconds)}ms");
                    }
                }
                sb.AppendLine();
            }
            }
            else
            {
                sb.AppendLine(
                    $"Detailed atlas profiling: disabled " +
                    $"(set {AtlasProfilingEnvironmentVariable}=1 before launch to enable)");
                sb.AppendLine();
            }

            sb.AppendLine("Atlas component reuse topology");
            sb.AppendLine("------------------------------");
            sb.AppendLine(
                "This is conservative VMD-root reachability, not guaranteed simultaneous rendering: " +
                "alternatives in the same VMD/slot can share a root without coexisting in one rendered variant.");
            sb.AppendLine(
                $"Atlased WSModels: {componentReuse.Components.Count}; " +
                $"reused by >1 root: {componentReuse.Components.Count(x => x.VmdRootCount > 1)}; " +
                $"reused by >=5 roots: {componentReuse.Components.Count(x => x.VmdRootCount >= 5)}.");
            sb.AppendLine(
                $"Atlased geometry paths: {componentReuse.GeometryCount}; " +
                $"reused by >1 root: {componentReuse.ReusedGeometryCount}.");
            sb.AppendLine();

            sb.AppendLine("Most reused atlased components");
            sb.AppendLine("  WSModel | VMD roots | atlas batches | geometry");
            foreach (var component in componentReuse.Components
                         .Where(x => x.VmdRootCount > 1)
                         .OrderByDescending(x => x.VmdRootCount)
                         .ThenBy(x => x.WsModelPath, StringComparer.OrdinalIgnoreCase)
                         .Take(24))
            {
                sb.AppendLine(
                    $"  {component.WsModelPath} | roots={component.VmdRootCount} | " +
                    $"batches={string.Join(",", component.AtlasBatchIds)} | " +
                    $"{component.GeometryPath}");
            }
            if (!componentReuse.Components.Any(x => x.VmdRootCount > 1))
                sb.AppendLine("  (none)");
            sb.AppendLine();

            sb.AppendLine("Strongly asymmetric cross-component root overlap");
            sb.AppendLine(
                "  These are the clearest reuse-risk cases for any future pair-specific rebake: " +
                "one component is broadly reused while the other appears in a much narrower subset.");
            var asymmetricPairs = componentReuse.Pairs
                .Where(x => x.StronglyAsymmetric)
                .OrderByDescending(x => Math.Abs(x.LeftCoverage - x.RightCoverage))
                .ThenByDescending(x => Math.Max(x.LeftRootCount, x.RightRootCount))
                .ThenByDescending(x => x.SharedRootCount)
                .Take(24)
                .ToList();
            if (asymmetricPairs.Count == 0)
            {
                sb.AppendLine("  (none)");
            }
            else
            {
                foreach (var pair in asymmetricPairs)
                {
                    sb.AppendLine(
                        $"  {pair.LeftWsModel} <> {pair.RightWsModel}");
                    sb.AppendLine(
                        $"    roots: {pair.LeftRootCount} <> {pair.RightRootCount}; " +
                        $"shared={pair.SharedRootCount}; " +
                        $"coverage={pair.LeftCoverage * 100:F1}% / {pair.RightCoverage * 100:F1}%");
                    sb.AppendLine(
                        $"    geometry: {pair.LeftGeometryPath} <> {pair.RightGeometryPath}");
                    sb.AppendLine(
                        $"    atlas batches: {string.Join(",", pair.LeftAtlasBatchIds)} <> " +
                        $"{string.Join(",", pair.RightAtlasBatchIds)}");
                }
            }
            sb.AppendLine();

            sb.AppendLine("High bidirectional cross-component root overlap");
            sb.AppendLine(
                "  High overlap can identify components that usually travel together, but this is diagnostic only; " +
                "same-root VMD alternatives are not proof that two components render simultaneously.");
            var coupledPairs = componentReuse.Pairs
                .Where(x => x.HighBidirectionalOverlap)
                .OrderByDescending(x => x.SharedRootCount)
                .ThenByDescending(x => Math.Min(x.LeftRootCount, x.RightRootCount))
                .Take(24)
                .ToList();
            if (coupledPairs.Count == 0)
            {
                sb.AppendLine("  (none)");
            }
            else
            {
                foreach (var pair in coupledPairs)
                {
                    sb.AppendLine(
                        $"  {pair.LeftWsModel} <> {pair.RightWsModel}");
                    sb.AppendLine(
                        $"    roots: {pair.LeftRootCount} <> {pair.RightRootCount}; " +
                        $"shared={pair.SharedRootCount}; " +
                        $"coverage={pair.LeftCoverage * 100:F1}% / {pair.RightCoverage * 100:F1}%");
                    sb.AppendLine(
                        $"    geometry: {pair.LeftGeometryPath} <> {pair.RightGeometryPath}");
                    sb.AppendLine(
                        $"    atlas batches: {string.Join(",", pair.LeftAtlasBatchIds)} <> " +
                        $"{string.Join(",", pair.RightAtlasBatchIds)}");
                }
            }
            sb.AppendLine();

            sb.AppendLine("VMD roots");
            sb.AppendLine("---------");
            foreach (var vmdPath in vmdRoots.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine(vmdPath);
            if (vmdRoots.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("XML compatibility repairs");
            sb.AppendLine("-------------------------");
            foreach (var entry in state.XmlCompatibilityRepairs
                         .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(entry.Key);
                foreach (var repair in entry.Value.OrderBy(value => value, StringComparer.Ordinal))
                    sb.AppendLine($"  Repaired: {repair}");
            }
            if (state.XmlCompatibilityRepairs.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("Malformed VMD root files ignored");
            sb.AppendLine("--------------------------------");
            foreach (var entry in state.MalformedVmdRoots.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(entry.Path);
                sb.AppendLine($"  Reason: {entry.Reason}");
            }
            if (state.MalformedVmdRoots.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("Malformed referenced VMD files ignored");
            sb.AppendLine("--------------------------------------");
            foreach (var entry in state.MalformedReferencedVmds
                         .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(entry.Path);
                sb.AppendLine($"  Reason: {entry.Reason}");
            }
            if (state.MalformedReferencedVmds.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("Malformed WSModels ignored");
            sb.AppendLine("--------------------------");
            foreach (var entry in state.MalformedWsModelsIgnored.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(entry.Path);
                sb.AppendLine($"  Reason: {entry.Reason}");
            }
            if (state.MalformedWsModelsIgnored.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("Malformed material XML files ignored");
            sb.AppendLine("------------------------------------");
            foreach (var entry in state.MalformedMaterialsIgnored
                         .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(entry.Path);
                sb.AppendLine($"  Reason: {entry.Reason}");
            }
            if (state.MalformedMaterialsIgnored.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("Direct engine assets skipped");
            sb.AppendLine("----------------------------");
            foreach (var message in state.DirectAssetSkipMessages
                         .OrderBy(message => message, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(message);
            }
            if (state.DirectAssetSkipMessages.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            if (failure != null)
            {
                sb.AppendLine("Failure");
                sb.AppendLine("-------");
                sb.AppendLine(failure.ToString());
                sb.AppendLine();
            }

            sb.AppendLine("Atlased mesh parts");
            sb.AppendLine("-------------------");
            if (state.AtlasedMeshes.Count == 0)
            {
                sb.AppendLine("(none)");
            }
            else
            {
                foreach (var entry in state.AtlasedMeshes
                             .OrderBy(x => x.RootVmdPath, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(x => x.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(x => x.Key.LodIndex)
                             .ThenBy(x => x.Key.PartIndex))
                {
                    sb.AppendLine($"VMD: {entry.RootVmdPath}");
                    sb.AppendLine($"  Mesh: {entry.Key}");
                    sb.AppendLine($"  WSModel(s): {string.Join(", ", entry.WsModelPaths)}");
                    sb.AppendLine($"  Material: {entry.OriginalMaterialPath} -> {entry.NewMaterialPath}");
                    sb.AppendLine($"  Atlas texture(s): {string.Join(", ", entry.AtlasPaths)}");
                }
            }
            sb.AppendLine();

            sb.AppendLine("Skipped mesh parts");
            sb.AppendLine("------------------");
            var effectiveSkips = state.SkipDetails
                .Where(x => !state.ProcessedMeshes.Contains(x.Key))
                .OrderBy(x => x.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Key.LodIndex)
                .ThenBy(x => x.Key.PartIndex)
                .ToList();

            if (effectiveSkips.Count == 0)
            {
                sb.AppendLine("(none)");
            }
            else
            {
                foreach (var (key, details) in effectiveSkips)
                {
                    sb.AppendLine($"Mesh: {key}");
                    foreach (var detail in details)
                    {
                        sb.AppendLine($"  VMD: {detail.RootVmdPath}");
                        if (!string.IsNullOrWhiteSpace(detail.WsModelPath))
                            sb.AppendLine($"  WSModel: {detail.WsModelPath}");
                        sb.AppendLine($"  Reason: {detail.Reason}");
                    }
                }
            }
            sb.AppendLine();

            if (state.MergeCompatibleMeshesEnabled)
            {
                sb.AppendLine("Merged mesh groups");
                sb.AppendLine("------------------");
                if (state.MeshMergeEntries.Count == 0)
                {
                    sb.AppendLine("(none)");
                }
                else
                {
                    foreach (var entry in state.MeshMergeEntries
                                 .OrderBy(x => x.RigidPath, StringComparer.OrdinalIgnoreCase)
                                 .ThenBy(x => x.LodIndex)
                                 .ThenBy(x => x.NewPartIndex))
                    {
                        sb.AppendLine($"Rigid: {entry.RigidPath}");
                        sb.AppendLine($"  LOD: {entry.LodIndex}");
                        sb.AppendLine($"  Old parts: {string.Join(", ", entry.OldPartIndices)}");
                        sb.AppendLine($"  New part: {entry.NewPartIndex}");
                        sb.AppendLine($"  Vertices: {entry.VertexCount}");
                        sb.AppendLine($"  Material: {entry.MaterialPath}");
                        sb.AppendLine($"  Origin: {entry.MergeOrigin}");
                    }
                }

                sb.AppendLine();
                sb.AppendLine("Merge-aware atlas repartitions");
                sb.AppendLine("------------------------------");
                if (state.MergeAwareRepartitionEntries.Count == 0)
                {
                    sb.AppendLine("(none)");
                }
                else
                {
                    foreach (var entry in state.MergeAwareRepartitionEntries)
                    {
                        sb.AppendLine(
                            $"Batches {entry.FirstBatchIndex} + {entry.SecondBatchIndex}: " +
                            $"pixels {entry.BaselinePixels:N0} -> {entry.ResultPixels:N0}, " +
                            $"merge affinity {entry.BaselineAffinity} -> {entry.ResultAffinity}, " +
                            $"scenario-estimated draw eliminations " +
                            $"{entry.BaselineExpectedArmyDrawCallsEliminated:N3} -> " +
                            $"{entry.ResultExpectedArmyDrawCallsEliminated:N3}");
                    }
                }
                sb.AppendLine();

                sb.AppendLine("Mesh merge blocker diagnostics");
                sb.AppendLine("------------------------------");
                sb.AppendLine("Counts below are near-miss part pairs/groups; unrelated parts are intentionally omitted.");
                if (state.MeshMergeBlockerCounts.Count == 0)
                {
                    sb.AppendLine("(none)");
                }
                else
                {
                    foreach (var blocker in state.MeshMergeBlockerCounts
                                 .OrderByDescending(x => x.Value)
                                 .ThenBy(x => x.Key, StringComparer.Ordinal))
                    {
                        sb.AppendLine($"{blocker.Key}: {blocker.Value}");
                        if (state.MeshMergeBlockerExamples.TryGetValue(blocker.Key, out var examples))
                        {
                            foreach (var example in examples)
                                sb.AppendLine($"  Example: {example}");
                        }
                    }
                }

                sb.AppendLine();
                sb.AppendLine("Material blocker breakdown");
                sb.AppendLine("--------------------------");
                AppendDiagnosticCounts(
                    sb,
                    "Shader pairs",
                    state.MaterialShaderPairBlockerCounts,
                    maxEntries: 24);
                AppendDiagnosticCounts(
                    sb,
                    "Texture slot/value pairs",
                    state.MaterialTextureSlotBlockerCounts,
                    maxEntries: 32);
                AppendDiagnosticCounts(
                    sb,
                    "Material parameter/value pairs",
                    state.MaterialParameterFieldBlockerCounts,
                    maxEntries: 32);

                sb.AppendLine();
                sb.AppendLine("Counterfactual texture compatibility");
                sb.AppendLine("------------------------------------");
                sb.AppendLine(
                    "Diagnostic only: these pairs retain their current shader and material " +
                    "parameters and ask whether their differing texture sources could be " +
                    "rewritten into common atlas channels.");
                sb.AppendLine(
                    $"Texture-only material pairs evaluated: " +
                    $"{state.CounterfactualTextureOnlyPairsEvaluated}");
                sb.AppendLine(
                    $"Pairs with atlasable differing texture channels: " +
                    $"{state.CounterfactualTextureOnlyPairsAtlasable}");
                sb.AppendLine(
                    $"Pairs reducible by uniform-constant path canonicalization: " +
                    $"{state.CounterfactualTextureOnlyPairsCanonicalizable}");
                sb.AppendLine(
                    $"Prospective merge-affinity groups added: " +
                    $"{state.ProspectiveMergeAffinityGroupsAdded}");
                sb.AppendLine(
                    $"Prospective merge-affinity pairs: " +
                    $"{state.ProspectiveMergeAffinityPairs}");
                AppendDiagnosticCounts(
                    sb,
                    "Classifications",
                    state.CounterfactualTexturePairCounts,
                    maxEntries: 16);
                foreach (var category in state.CounterfactualTexturePairCounts
                             .OrderByDescending(x => x.Value)
                             .ThenBy(x => x.Key, StringComparer.Ordinal)
                             .Where(x => state.CounterfactualTexturePairExamples.ContainsKey(x.Key)))
                {
                    sb.AppendLine($"  Examples — {category.Key}:");
                    foreach (var example in state.CounterfactualTexturePairExamples[category.Key])
                        sb.AppendLine($"    {example}");
                }

                sb.AppendLine();
                sb.AppendLine("Texture-blocked merge opportunities");
                sb.AppendLine("-----------------------------------");
                sb.AppendLine("Each entry is one additional merge group potentially removable by combining two existing atlas batches.");
                sb.AppendLine("Pixel cost compares the two current complete batches with one hypothetical combined batch.");
                var opportunities = state.TextureMergeOpportunities.Values
                    .OrderBy(x => x.Combination.Fits ? 0 : 1)
                    .ThenBy(x => x.Combination.AdditionalPercent)
                    .ThenBy(x => x.Combination.AdditionalPixels)
                    .ThenBy(x => x.RigidPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.LodIndex)
                    .ToList();

                if (opportunities.Count == 0)
                {
                    sb.AppendLine("(none)");
                }
                else
                {
                    sb.AppendLine($"Planner-actionable opportunities: {opportunities.Count}");
                    sb.AppendLine($"Opportunities whose atlas combination fits: {opportunities.Count(x => x.Combination.Fits)}");
                    sb.AppendLine($"Unique atlas-batch combinations evaluated: {state.AtlasBatchCombinationDiagnostics.Count}");
                    sb.AppendLine();

                    foreach (var opportunity in opportunities.Take(80))
                    {
                        var combination = opportunity.Combination;
                        sb.AppendLine($"Rigid: {opportunity.RigidPath}");
                        sb.AppendLine($"  LOD: {opportunity.LodIndex}");
                        sb.AppendLine($"  Parts: {string.Join(", ", opportunity.PartIndices.OrderBy(x => x))}");
                        sb.AppendLine($"  Atlas batches: {opportunity.FirstBatchId} + {opportunity.SecondBatchId}");
                        if (combination.Fits)
                        {
                            sb.AppendLine($"  Current atlas pixels: {combination.BaselinePixelCost:N0}");
                            sb.AppendLine($"  Combined atlas pixels: {combination.CombinedPixelCost:N0}");
                            sb.AppendLine(
                                $"  Additional pixels: {combination.AdditionalPixels:N0} " +
                                $"({combination.AdditionalPercent:+0.##;-0.##;0}%)");
                        }
                        else
                        {
                            sb.AppendLine($"  Result: DOES NOT FIT ({combination.FailureReason})");
                        }

                        foreach (var example in opportunity.Examples)
                            sb.AppendLine($"  Texture difference: {example}");
                    }

                    if (opportunities.Count > 80)
                        sb.AppendLine($"... {opportunities.Count - 80} additional opportunities omitted.");
                }

                if (state.MeshMergeSkipMessages.Count != 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Mesh merge skips");
                    sb.AppendLine("----------------");
                    foreach (var message in state.MeshMergeSkipMessages)
                        sb.AppendLine(message);
                }

                sb.AppendLine();
            }

            AppendGameplayMeshDependencyDiagnostics(sb, state);

            sb.AppendLine("Uniform constant source textures");
            sb.AppendLine("--------------------------------");
            foreach (var path in state.UniformConstantTexturePaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine(path);
            if (state.UniformConstantTexturePaths.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("UV island normalizations");
            sb.AppendLine("------------------------");
            if (state.UvIslandNormalizationEntries.Count == 0)
            {
                sb.AppendLine("(none)");
            }
            else
            {
                foreach (var entry in state.UvIslandNormalizationEntries
                             .OrderBy(x => x.Mesh.GeometryPath, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(x => x.Mesh.LodIndex)
                             .ThenBy(x => x.Mesh.PartIndex))
                {
                    sb.AppendLine($"Mesh: {entry.Mesh}");
                    sb.AppendLine($"  Islands: {entry.IslandCount}, shifted: {entry.ShiftedIslandCount}");
                    sb.AppendLine(
                        $"  Crop: ({entry.OriginalCrop.X},{entry.OriginalCrop.Y}) " +
                        $"{entry.OriginalCrop.Width}x{entry.OriginalCrop.Height} -> " +
                        $"({entry.NormalizedCrop.X},{entry.NormalizedCrop.Y}) " +
                        $"{entry.NormalizedCrop.Width}x{entry.NormalizedCrop.Height}");
                }
            }
            sb.AppendLine();

            sb.AppendLine("Atlas placement diagnostics");
            sb.AppendLine("---------------------------");
            if (state.AtlasPlacementDiagnostics.Count == 0)
            {
                sb.AppendLine("(none)");
            }
            else
            {
                foreach (var batch in state.AtlasPlacementDiagnostics
                             .OrderBy(x => x.AtlasStem, StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine(
                        $"{batch.AtlasStem}: layout={batch.PlanWidth}x{batch.PlanHeight}, " +
                        $"placements={batch.Placements.Count}");
                    foreach (var path in batch.GeneratedTexturePaths)
                    {
                        if (state.GeneratedTextureDimensions.TryGetValue(path, out var dimensions))
                            sb.AppendLine($"  Generated texture: {path} ({dimensions.Width}x{dimensions.Height})");
                        else
                            sb.AppendLine($"  Generated texture: {path}");
                    }

                    foreach (var placement in batch.Placements.OrderBy(x => x.SourceId))
                    {
                        sb.AppendLine(
                            $"  Placement {placement.SourceId}: " +
                            $"source={placement.SourceWidth}x{placement.SourceHeight}, " +
                            $"crop=({placement.Crop.X},{placement.Crop.Y}) " +
                            $"{placement.Crop.Width}x{placement.Crop.Height}, " +
                            $"destination=({placement.DestinationX},{placement.DestinationY}) " +
                            $"{placement.DestinationWidth}x{placement.DestinationHeight}, " +
                            $"padding={placement.Padding}");
                        sb.AppendLine($"    Representative mesh: {placement.RepresentativeMesh}");
                        sb.AppendLine($"    Material: {placement.MaterialPath}");
                        sb.AppendLine($"    BaseColour: {FormatDiagnosticTexturePath(placement.BaseColourPath)}");
                        sb.AppendLine($"    MaterialMap: {FormatDiagnosticTexturePath(placement.MaterialMapPath)}");
                        sb.AppendLine($"    Normal: {FormatDiagnosticTexturePath(placement.NormalPath)}");
                        sb.AppendLine($"    Mask: {FormatDiagnosticTexturePath(placement.MaskPath)}");

                        AppendPlacementRelation(
                            sb,
                            "Same BaseColour sampled region",
                            placement.SameBaseColourRegionSourceIds);
                        AppendPlacementRelation(
                            sb,
                            "BaseColour crop contains placement(s)",
                            placement.BaseColourContainsSourceIds);
                        AppendPlacementRelation(
                            sb,
                            "BaseColour crop is contained by placement(s)",
                            placement.BaseColourContainedBySourceIds);
                        AppendPlacementRelation(
                            sb,
                            "Full texture-set crop contains placement(s)",
                            placement.FullTextureSetContainsSourceIds);
                        AppendPlacementRelation(
                            sb,
                            "Full texture-set crop is contained by placement(s)",
                            placement.FullTextureSetContainedBySourceIds);

                        sb.AppendLine($"    Mapped meshes: {placement.MeshMappings.Count}");
                        foreach (var mapping in placement.MeshMappings)
                        {
                            var flags = new List<string>();
                            if (mapping.UvIslandCanonicalized)
                                flags.Add("uv-islands");
                            if (mapping.WrappedUvCanonicalized)
                                flags.Add("wrapped-uv");
                            if (mapping.ContentCanonicalized)
                                flags.Add("content-dedupe");

                            sb.AppendLine(
                                $"      {mapping.Mesh} | VMD={mapping.RootVmdPath} | " +
                                $"uv-offset=({mapping.UvOffsetU:0.######},{mapping.UvOffsetV:0.######})" +
                                (flags.Count == 0
                                    ? string.Empty
                                    : $" | {string.Join(",", flags)}"));
                        }
                    }

                    sb.AppendLine();
                }
            }

            sb.AppendLine("Generated atlas textures");
            sb.AppendLine("------------------------");
            foreach (var path in state.GeneratedTexturePaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (state.GeneratedTextureDimensions.TryGetValue(path, out var dimensions))
                    sb.AppendLine($"{path} ({dimensions.Width}x{dimensions.Height})");
                else
                    sb.AppendLine(path);
            }
            if (state.GeneratedTexturePaths.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("Generated atlas materials");
            sb.AppendLine("-------------------------");
            foreach (var path in state.GeneratedMaterialPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine(path);
            if (state.GeneratedMaterialPaths.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("Removed superseded files");
            sb.AppendLine("------------------------");
            foreach (var path in state.RemovedFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine(path);
            if (state.RemovedFiles.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("Validation");
            sb.AppendLine("----------");
            foreach (var message in state.ValidationMessages)
                sb.AppendLine(message);
            if (state.ValidationMessages.Count == 0)
                sb.AppendLine("(not completed)");

            File.WriteAllText(state.ReportPath, sb.ToString(), Encoding.UTF8);
        }
    }
}
