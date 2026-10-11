using System.Diagnostics;
using System.Globalization;
using System.Text;
using Shared.GameFormats.RigidModel;

namespace Editors.KitbasherEditor.Services
{
    public sealed partial class PackTextureAtlasBatchService
    {
        // Report-only audit of how heavy the gameplay rigids are at distance. It reads
        // rigid geometry and the attachment-local configuration expansion (the same
        // expansion and component resolution the cross-rigid baseline uses) before any
        // stage rewrites a rigid, and never writes to the output pack.
        private const int LodAuditExampleLimit = 10;
        private const int LodAuditRankingLimit = 25;
        private const int LodAuditProfileLimit = 8;
        private const int LodAuditMinimumLodCount = 4;
        private const int LodAuditPathWidth = 72;
        private const int LodAuditRigidCacheLimit = 128;
        private const double LodAuditHeavyLastLodShare = 0.25;
        // The expansion normally takes a fraction of a second per root. If a pack makes
        // it slow, stop starting new roots instead of silently adding minutes to a run.
        private const double LodAuditMinimumBudgetSeconds = 15;
        private const double LodAuditBudgetSecondsPerHundredRoots = 5;

        private sealed record LodAuditLod(
            int PartCount,
            long Vertices,
            long Triangles,
            float Distance,
            byte Quality);

        private sealed record LodAuditRigid(
            string RigidPath,
            bool SourceOwned,
            IReadOnlyList<LodAuditLod> Lods,
            string DistanceProfile)
        {
            public double? LastToFirstTriangleRatio
                => Lods.Count == 0 || Lods[0].Triangles == 0
                    ? null
                    : (double)Lods[^1].Triangles / Lods[0].Triangles;

            public bool LastLodIsHeavy
                => LastToFirstTriangleRatio > LodAuditHeavyLastLodShare;

            public bool HasLodNotLighterThanPrevious
            {
                get
                {
                    for (var index = 1; index < Lods.Count; index++)
                    {
                        if (Lods[index].Triangles >= Lods[index - 1].Triangles)
                            return true;
                    }

                    return false;
                }
            }

            public bool HasFewLods => Lods.Count < LodAuditMinimumLodCount;
        }

        private sealed record LodAuditUnit(string UnitId, int Entities);

        private sealed record LodAuditRoot(
            string VmdPath,
            IReadOnlyList<LodAuditUnit> Units,
            int UnitCount,
            IReadOnlyList<int> ScenarioLods,
            IReadOnlyList<double> TrianglesPerEntity,
            IReadOnlyList<double> VerticesPerEntity,
            IReadOnlyList<double> PartsPerEntity,
            IReadOnlyList<double> BattleTriangles,
            IReadOnlyList<double> BattleVertices,
            bool WeightAvailable,
            IReadOnlyList<string> DistanceProfiles,
            IReadOnlyList<string> RigidPaths);

        private sealed class LodAuditResult
        {
            public Dictionary<string, LodAuditRigid> Rigids { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, LodAuditRigid?> RigidByWsModel { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int> RootCountByRigid { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public List<LodAuditRoot> Roots { get; } = [];
            public IReadOnlyList<int> ScenarioLods { get; set; } = [];
            public IReadOnlyList<double> ScenarioLodProbabilities { get; set; } = [];
            public int RootsRequested { get; set; }
            public int RootsSkippedForTime { get; set; }
            public int ExpansionFailures { get; set; }
            public int UnresolvedWsModels { get; set; }
            public TimeSpan TimeBudget { get; set; }
            public TimeSpan Elapsed { get; set; }
            public string? Failure { get; set; }
        }

        private static void AuditSourceLods(
            BatchState state,
            IReadOnlyCollection<string> scopeVmdRoots,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress,
            TimeSpan? timeBudget = null)
        {
            var audit = new LodAuditResult();
            state.LodAudit = audit;
            state.AssetChecks = new AssetChecksResult();
            var timer = Stopwatch.StartNew();
            try
            {
                var roots = scopeVmdRoots
                    .Select(Normalize)
                    .Where(path => path.Length != 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                audit.RootsRequested = roots.Count;
                audit.TimeBudget = timeBudget ?? TimeSpan.FromSeconds(Math.Max(
                    LodAuditMinimumBudgetSeconds,
                    roots.Count / 100.0 * LodAuditBudgetSecondsPerHundredRoots));

                var traversalContainers = GetGameplayTraversalContainers(state);
                var rigidCache = new Dictionary<string, RmvFile>(
                    StringComparer.OrdinalIgnoreCase);
                var expansionCache =
                    new Dictionary<string, CrossRigidAttachmentLocalExpansionResult>(
                        StringComparer.OrdinalIgnoreCase);

                for (var rootIndex = 0; rootIndex < roots.Count; rootIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (timer.Elapsed > audit.TimeBudget)
                    {
                        audit.RootsSkippedForTime = roots.Count - rootIndex;
                        break;
                    }

                    var vmdPath = roots[rootIndex];
                    ReportProgress(
                        progress,
                        "Auditing LODs",
                        rootIndex + 1,
                        roots.Count,
                        vmdPath);
                    var expansion = GetCrossRigidAttachmentLocalConfigurationsForVmd(
                        state,
                        vmdPath,
                        expansionCache,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    if (expansion.FailureReason != null)
                    {
                        audit.ExpansionFailures++;
                        continue;
                    }

                    AuditLodsForRoot(
                        state,
                        audit,
                        vmdPath,
                        expansion,
                        wsModelPath =>
                        {
                            if (!TryBuildCrossRigidAnalysisComponent(
                                    state,
                                    vmdPath,
                                    wsModelPath,
                                    traversalContainers,
                                    rigidCache,
                                    out var component,
                                    out _))
                            {
                                return null;
                            }

                            ObserveAssetCheckComponent(state, component);
                            return component;
                        });
                    // Rigid geometry is large; the audit keeps only per-LOD totals.
                    if (rigidCache.Count > LodAuditRigidCacheLimit)
                        rigidCache.Clear();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The audit is diagnostic: it must never fail a run or the output pack.
                audit.Failure = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                // The component resolution above records cross-rigid diagnostics that
                // belong to the cross-rigid analysis; it clears them itself when it runs.
                state.CrossRigidAnalysisDiagnosticCounts.Clear();
                state.CrossRigidAnalysisDiagnosticExamples.Clear();
                audit.Elapsed = timer.Elapsed;
            }
        }

        private static LodAuditRigid BuildLodAuditRigid(
            string rigidPath,
            bool sourceOwned,
            RmvFile rigid)
        {
            var headers = rigid.LodHeaders ?? [];
            var lods = new List<LodAuditLod>(rigid.ModelList.Length);
            for (var lodIndex = 0; lodIndex < rigid.ModelList.Length; lodIndex++)
            {
                var models = rigid.ModelList[lodIndex];
                var header = lodIndex < headers.Length ? headers[lodIndex] : null;
                lods.Add(new LodAuditLod(
                    models.Length,
                    models.Sum(model => (long)model.Mesh.VertexList.Length),
                    models.Sum(model => (long)model.Mesh.IndexList.Length) / 3,
                    header?.LodCameraDistance ?? 0f,
                    header?.QualityLvl ?? (byte)0));
            }

            return new LodAuditRigid(
                rigidPath,
                sourceOwned,
                lods,
                string.Join(
                    "/",
                    headers.Select(header => header.LodCameraDistance.ToString(
                        "0.###",
                        CultureInfo.InvariantCulture))));
        }

        // Per-entity values follow CalculateCrossRigidBaselineExpectedDraws exactly:
        // for each scenario LOD k, the probability-weighted sum over every attachment's
        // configurations of each instance's value at source LOD min(k, lodCount - 1).
        // The army weighting then reuses CalculateExpectedCrossRigidArmyDrawWeight with
        // the per-LOD value in place of draws per entity.
        private static LodAuditRoot AuditLodsForRoot(
            BatchState state,
            LodAuditResult audit,
            string vmdPathValue,
            CrossRigidAttachmentLocalExpansionResult expansion,
            Func<string, CrossRigidAnalysisComponent?> resolveComponent)
        {
            var vmdPath = Normalize(vmdPathValue);
            var model = state.ArmyResidencyModel;
            var scenarioLods = (model?.Scenario ?? Wh3ArmyVisualScenario.Default)
                .LodDistribution
                .Where(entry => entry.Value > 0)
                .OrderBy(entry => entry.Key)
                .ToArray();
            audit.ScenarioLods = scenarioLods.Select(entry => entry.Key).ToArray();
            audit.ScenarioLodProbabilities =
                scenarioLods.Select(entry => entry.Value).ToArray();

            LodAuditRigid? ResolveRigid(string wsModelPath)
            {
                if (audit.RigidByWsModel.TryGetValue(wsModelPath, out var cached))
                    return cached;

                LodAuditRigid? rigid = null;
                var component = resolveComponent(wsModelPath);
                if (component == null)
                {
                    audit.UnresolvedWsModels++;
                }
                else if (!audit.Rigids.TryGetValue(component.RigidPath, out rigid))
                {
                    rigid = BuildLodAuditRigid(
                        component.RigidPath,
                        component.SourceOwnedRigid,
                        component.Rigid);
                    audit.Rigids[component.RigidPath] = rigid;
                }

                audit.RigidByWsModel[wsModelPath] = rigid;
                return rigid;
            }

            var triangles = new double[scenarioLods.Length];
            var vertices = new double[scenarioLods.Length];
            var parts = new double[scenarioLods.Length];
            var reachedRigids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var instanceWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var configurations in expansion.ConfigurationsByAttachment.Values)
            {
                foreach (var configuration in configurations)
                {
                    if (configuration.Probability <= 0)
                        continue;

                    foreach (var instance in configuration.Instances)
                    {
                        var rigid = ResolveRigid(instance.WsModelPath);
                        if (rigid == null || rigid.Lods.Count == 0)
                            continue;

                        reachedRigids.Add(rigid.RigidPath);
                        var wsModelKey = Normalize(instance.WsModelPath);
                        instanceWeights[wsModelKey] =
                            instanceWeights.GetValueOrDefault(wsModelKey) +
                            configuration.Probability;
                        for (var index = 0; index < scenarioLods.Length; index++)
                        {
                            var lod = rigid.Lods[Math.Min(
                                scenarioLods[index].Key,
                                rigid.Lods.Count - 1)];
                            triangles[index] += configuration.Probability * lod.Triangles;
                            vertices[index] += configuration.Probability * lod.Vertices;
                            parts[index] += configuration.Probability * lod.PartCount;
                        }
                    }
                }
            }

            ObserveAssetCheckRoot(state, vmdPath, instanceWeights);
            foreach (var rigidPath in reachedRigids)
            {
                audit.RootCountByRigid[rigidPath] =
                    audit.RootCountByRigid.GetValueOrDefault(rigidPath) + 1;
            }

            var weightAvailable =
                model != null &&
                GetRelevantArmyUnitIds(model, Array.Empty<string>(), [vmdPath]).Count != 0;
            var battleTriangles = new double[scenarioLods.Length];
            var battleVertices = new double[scenarioLods.Length];
            if (weightAvailable)
            {
                for (var index = 0; index < scenarioLods.Length; index++)
                {
                    battleTriangles[index] = CalculateExpectedCrossRigidArmyDrawWeight(
                        state,
                        vmdPath,
                        1.0,
                        [(scenarioLods[index].Value, triangles[index])]);
                    battleVertices[index] = CalculateExpectedCrossRigidArmyDrawWeight(
                        state,
                        vmdPath,
                        1.0,
                        [(scenarioLods[index].Value, vertices[index])]);
                }
            }

            var (units, unitCount) = GetLodAuditRootUnits(model, vmdPath);
            var root = new LodAuditRoot(
                vmdPath,
                units,
                unitCount,
                scenarioLods.Select(entry => entry.Key).ToArray(),
                triangles,
                vertices,
                parts,
                battleTriangles,
                battleVertices,
                weightAvailable,
                reachedRigids
                    .Select(path => audit.Rigids[path].DistanceProfile)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(profile => profile, StringComparer.Ordinal)
                    .ToArray(),
                reachedRigids.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray());
            audit.Roots.Add(root);
            return root;
        }

        private static (List<LodAuditUnit> Units, int UnitCount) GetLodAuditRootUnits(
            ArmyResidencyModel? model,
            string vmdPath)
        {
            var units = new List<LodAuditUnit>();
            if (model == null)
                return (units, 0);

            var unitCount = 0;
            foreach (var (unitId, vmdsByRole) in model.DirectVmdsByUnitAndRole
                         .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                var entities = 0;
                var usesRoot = false;
                foreach (var (role, directVmds) in vmdsByRole)
                {
                    if (!directVmds.Contains(vmdPath))
                        continue;

                    usesRoot = true;
                    entities += Math.Max(
                        1,
                        model.EntityCountByUnitAndRole
                            .GetValueOrDefault(unitId)?
                            .GetValueOrDefault(role, 1) ?? 1);
                }

                if (!usesRoot)
                    continue;

                unitCount++;
                if (units.Count < 3)
                    units.Add(new LodAuditUnit(unitId, entities));
            }

            return (units, unitCount);
        }

        private static string FormatLodAuditPath(string path)
        {
            if (path.Length <= LodAuditPathWidth)
                return path;

            const int head = 30;
            var tail = LodAuditPathWidth - head - 3;
            return path[..head] + "..." + path[^tail..];
        }

        private static string FormatLodAuditSeries(IEnumerable<double> values)
            => string.Join(" / ", values.Select(value => value.ToString("N0")));

        private static string FormatLodAuditSeries(IEnumerable<long> values)
            => string.Join(" / ", values.Select(value => value.ToString("N0")));

        private static string FormatLodAuditShare(double part, double whole)
            => whole > 0 && double.IsFinite(part / whole)
                ? (part / whole).ToString("P1")
                : "n/a";

        private static string FormatLodAuditRigidExample(LodAuditRigid rigid)
            => $"{FormatLodAuditPath(rigid.RigidPath)} " +
               $"(tris {FormatLodAuditSeries(rigid.Lods.Select(lod => lod.Triangles))}; " +
               $"dist {(rigid.DistanceProfile.Length == 0 ? "n/a" : rigid.DistanceProfile)})";

        private static void AppendLodAuditExamples(
            StringBuilder sb,
            IReadOnlyCollection<string> examples,
            int total)
        {
            foreach (var example in examples.Take(LodAuditExampleLimit))
                sb.AppendLine($"    e.g. {example}");
            if (total > LodAuditExampleLimit)
                sb.AppendLine($"    ... and {total - LodAuditExampleLimit:N0} more");
        }

        private static void AppendLodAuditSection(StringBuilder sb, BatchState state)
        {
            sb.AppendLine("LOD audit");
            sb.AppendLine("---------");
            var audit = state.LodAudit;
            if (audit == null)
            {
                sb.AppendLine("LOD audit did not run.");
                sb.AppendLine();
                return;
            }

            sb.AppendLine(
                "Report only: source-state rigid geometry, read before merging and " +
                "optimization. The output pack is not changed.");
            sb.AppendLine(
                $"Roots audited: {audit.Roots.Count:N0} / {audit.RootsRequested:N0} VMD root(s) in " +
                $"scope; expansion failed: {audit.ExpansionFailures:N0}; skipped after the " +
                $"{audit.TimeBudget.TotalSeconds:N0} s time budget: {audit.RootsSkippedForTime:N0}");
            sb.AppendLine($"LOD audit time: {audit.Elapsed.TotalMilliseconds:N0} ms");
            if (audit.Failure != null)
                sb.AppendLine($"LOD audit stopped early: {audit.Failure}");

            var sourceRigids = audit.Rigids.Values.Where(rigid => rigid.SourceOwned).ToList();
            sb.AppendLine(
                $"Rigids audited: {audit.Rigids.Count:N0} (source-owned {sourceRigids.Count:N0}, " +
                $"vanilla {audit.Rigids.Count - sourceRigids.Count:N0}); " +
                $"WSModels without resolvable geometry: {audit.UnresolvedWsModels:N0}");
            var lodCount = audit.ScenarioLods.Count;
            sb.AppendLine(
                "Scenario LODs: " +
                (lodCount == 0
                    ? "(none)"
                    : string.Join(
                        ", ",
                        audit.ScenarioLods.Select((lod, index) =>
                            $"{lod} ({audit.ScenarioLodProbabilities[index]:P0})"))));

            var weightedRoots = audit.Roots.Where(root => root.WeightAvailable).ToList();
            sb.AppendLine(
                $"Roots with an army weight: {weightedRoots.Count:N0} / {audit.Roots.Count:N0} " +
                "(others have no unit using them directly; shown per entity, weight n/a)");
            var battleTriangles = new double[lodCount];
            var battleVertices = new double[lodCount];
            foreach (var root in weightedRoots)
            {
                for (var index = 0; index < lodCount; index++)
                {
                    battleTriangles[index] += root.BattleTriangles[index];
                    battleVertices[index] += root.BattleVertices[index];
                }
            }

            var triangleTotal = battleTriangles.Sum();
            var vertexTotal = battleVertices.Sum();
            sb.AppendLine(
                $"Expected battle triangles over all roots: {triangleTotal:N0}" +
                (lodCount == 0
                    ? string.Empty
                    : " = " + string.Join(
                        " + ",
                        battleTriangles.Select((value, index) =>
                            $"LOD {audit.ScenarioLods[index]} {value:N0} " +
                            $"({FormatLodAuditShare(value, triangleTotal)})"))));
            sb.AppendLine(
                $"Expected battle vertices over all roots: {vertexTotal:N0}" +
                (lodCount == 0
                    ? string.Empty
                    : " = " + string.Join(
                        " + ",
                        battleVertices.Select((value, index) =>
                            $"LOD {audit.ScenarioLods[index]} {value:N0} " +
                            $"({FormatLodAuditShare(value, vertexTotal)})"))));
            sb.AppendLine();

            sb.AppendLine("LOD-distance profiles of source-owned rigids");
            var profiles = sourceRigids
                .GroupBy(rigid => rigid.DistanceProfile.Length == 0 ? "n/a" : rigid.DistanceProfile)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .ToList();
            if (profiles.Count == 0)
                sb.AppendLine("  (none)");
            foreach (var group in profiles.Take(LodAuditProfileLimit))
                sb.AppendLine($"  {group.Key}: {group.Count():N0} rigid(s)");
            if (profiles.Count > LodAuditProfileLimit)
            {
                sb.AppendLine(
                    $"  ... and {profiles.Count - LodAuditProfileLimit:N0} more profile(s)");
            }

            sb.AppendLine();
            sb.AppendLine("Flags (rigid flags count source-owned rigids; the root flag counts audited roots)");
            var heavyLast = sourceRigids
                .Where(rigid => rigid.LastLodIsHeavy)
                .OrderBy(rigid => rigid.RigidPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            sb.AppendLine(
                $"  Last LOD above {LodAuditHeavyLastLodShare:P0} of LOD 0 triangles: " +
                $"{heavyLast.Count:N0}");
            AppendLodAuditExamples(
                sb,
                heavyLast.Select(FormatLodAuditRigidExample).ToList(),
                heavyLast.Count);
            var notLighter = sourceRigids
                .Where(rigid => rigid.HasLodNotLighterThanPrevious)
                .OrderBy(rigid => rigid.RigidPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            sb.AppendLine(
                $"  A LOD with the same or more triangles than the previous LOD: " +
                $"{notLighter.Count:N0}");
            AppendLodAuditExamples(
                sb,
                notLighter.Select(FormatLodAuditRigidExample).ToList(),
                notLighter.Count);
            var fewLods = sourceRigids
                .Where(rigid => rigid.HasFewLods)
                .OrderBy(rigid => rigid.RigidPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            sb.AppendLine($"  Fewer than {LodAuditMinimumLodCount} LODs: {fewLods.Count:N0}");
            AppendLodAuditExamples(
                sb,
                fewLods.Select(FormatLodAuditRigidExample).ToList(),
                fewLods.Count);
            var mixedRoots = audit.Roots
                .Where(root => root.DistanceProfiles.Count > 1)
                .OrderBy(root => root.VmdPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            sb.AppendLine(
                $"  Roots whose parts mix different LOD-distance profiles: {mixedRoots.Count:N0}");
            AppendLodAuditExamples(
                sb,
                mixedRoots
                    .Select(root =>
                        $"{FormatLodAuditPath(root.VmdPath)} ({string.Join(" | ", root.DistanceProfiles)})")
                    .ToList(),
                mixedRoots.Count);
            sb.AppendLine();

            AppendLodAuditHeaviestRoots(sb, audit, triangleTotal, battleTriangles);
            AppendLodAuditHeaviestRigids(sb, audit, sourceRigids);
        }

        private static void AppendLodAuditHeaviestRoots(
            StringBuilder sb,
            LodAuditResult audit,
            double triangleTotal,
            IReadOnlyList<double> battleTrianglesByLod)
        {
            sb.AppendLine("Heaviest unit looks at distance");
            var lodCount = audit.ScenarioLods.Count;
            if (lodCount == 0 || audit.Roots.Count == 0)
            {
                sb.AppendLine("  (none)");
                sb.AppendLine();
                return;
            }

            var last = lodCount - 1;
            var lastLodTotal = battleTrianglesByLod[last];
            sb.AppendLine(
                $"Top {LodAuditRankingLimit} roots by expected battle triangles at scenario LOD " +
                $"{audit.ScenarioLods[last]} (the last). Per-entity values are triangles at " +
                $"scenario LODs {string.Join(" / ", audit.ScenarioLods)}.");
            var weighted = audit.Roots
                .Where(root => root.WeightAvailable)
                .OrderByDescending(root => root.BattleTriangles[last])
                .ThenBy(root => root.VmdPath, StringComparer.OrdinalIgnoreCase)
                .Take(LodAuditRankingLimit)
                .ToList();
            var rank = 0;
            foreach (var root in weighted)
            {
                rank++;
                AppendLodAuditRoot(sb, rank, root, last, triangleTotal, lastLodTotal);
            }

            var unweighted = audit.Roots
                .Where(root => !root.WeightAvailable)
                .OrderByDescending(root => root.TrianglesPerEntity[last])
                .ThenBy(root => root.VmdPath, StringComparer.OrdinalIgnoreCase)
                .Take(weighted.Count == 0 ? LodAuditRankingLimit : LodAuditExampleLimit)
                .ToList();
            if (weighted.Count == 0)
                sb.AppendLine("  (no root has an army weight; ranked by triangles per entity)");
            else if (unweighted.Count != 0)
                sb.AppendLine("  Roots without an army weight, by triangles per entity at the last LOD:");
            rank = 0;
            foreach (var root in unweighted)
            {
                rank++;
                AppendLodAuditRoot(sb, rank, root, last, triangleTotal, lastLodTotal);
            }

            sb.AppendLine();
        }

        private static void AppendLodAuditRoot(
            StringBuilder sb,
            int rank,
            LodAuditRoot root,
            int last,
            double triangleTotal,
            double lastLodTotal)
        {
            sb.AppendLine($"  {rank,2}. {FormatLodAuditPath(root.VmdPath)}");
            sb.AppendLine(
                "      units: " +
                (root.Units.Count == 0
                    ? "(none)"
                    : string.Join(
                        ", ",
                        root.Units.Select(unit => $"{unit.UnitId} ({unit.Entities:N0} ent.)")) +
                      (root.UnitCount > root.Units.Count
                          ? $" (+{root.UnitCount - root.Units.Count:N0} more)"
                          : string.Empty)));
            sb.AppendLine(
                $"      triangles per entity: {FormatLodAuditSeries(root.TrianglesPerEntity)}; " +
                $"last/first {FormatLodAuditShare(root.TrianglesPerEntity[last], root.TrianglesPerEntity[0])}; " +
                $"mesh parts per entity at the last LOD {root.PartsPerEntity[last]:N1}");
            sb.AppendLine(
                root.WeightAvailable
                    ? $"      expected battle triangles at the last LOD: {root.BattleTriangles[last]:N0} " +
                      $"({FormatLodAuditShare(root.BattleTriangles[last], lastLodTotal)} of the last-LOD " +
                      $"total, {FormatLodAuditShare(root.BattleTriangles[last], triangleTotal)} of the pack total)"
                    : "      expected battle triangles at the last LOD: n/a (no army weight)");
            sb.AppendLine(
                "      LOD-distance profiles: " +
                (root.DistanceProfiles.Count == 0
                    ? "n/a"
                    : string.Join(" | ", root.DistanceProfiles)));
        }

        private static void AppendLodAuditHeaviestRigids(
            StringBuilder sb,
            LodAuditResult audit,
            IReadOnlyList<LodAuditRigid> sourceRigids)
        {
            sb.AppendLine("Heaviest rigids at the last LOD");
            var ranked = sourceRigids
                .Where(rigid => rigid.Lods.Count != 0)
                .Select(rigid => (
                    Rigid: rigid,
                    Roots: audit.RootCountByRigid.GetValueOrDefault(rigid.RigidPath)))
                .OrderByDescending(entry => entry.Rigid.Lods[^1].Triangles * (double)entry.Roots)
                .ThenBy(entry => entry.Rigid.RigidPath, StringComparer.OrdinalIgnoreCase)
                .Take(LodAuditRankingLimit)
                .ToList();
            if (ranked.Count == 0)
            {
                sb.AppendLine("  (none)");
                sb.AppendLine();
                return;
            }

            sb.AppendLine(
                $"Top {LodAuditRankingLimit} source-owned rigids by last-LOD triangles x " +
                "gameplay-used roots that reach them.");
            var rank = 0;
            foreach (var (rigid, roots) in ranked)
            {
                rank++;
                sb.AppendLine($"  {rank,2}. {FormatLodAuditPath(rigid.RigidPath)}");
                sb.AppendLine(
                    $"      triangles per LOD: {FormatLodAuditSeries(rigid.Lods.Select(lod => lod.Triangles))}; " +
                    $"distances {(rigid.DistanceProfile.Length == 0 ? "n/a" : rigid.DistanceProfile)}; " +
                    $"last/first {(rigid.LastToFirstTriangleRatio is { } ratio ? ratio.ToString("P1") : "n/a")}; " +
                    $"roots {roots:N0}");
            }

            sb.AppendLine();
        }
    }
}
