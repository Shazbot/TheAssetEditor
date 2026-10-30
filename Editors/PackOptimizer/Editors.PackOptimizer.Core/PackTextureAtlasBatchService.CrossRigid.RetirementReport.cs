using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Editors.PackOptimizer.Core
{
    /// <summary>
    /// Pure coverage math of the cross-rigid retirement report (report-only).
    /// </summary>
    internal static class CrossRigidRetirementMath
    {
        /// <summary>
        /// Covered share of a set of occurrences. Each occurrence has a probability and the probability that
        /// a rewrite covers it. The share is the probability-weighted covered fraction; the set is fully
        /// covered when every occurrence is covered up to its own probability (within epsilon).
        /// </summary>
        public static (double Share, bool FullyCovered, int FullyCoveredCount) Evaluate(
            IEnumerable<(double Probability, double Coverage)> occurrences,
            double epsilon)
        {
            double total = 0;
            double covered = 0;
            var fully = 0;
            var count = 0;
            var all = true;
            foreach (var (probability, coverage) in occurrences)
            {
                count++;
                total += probability;
                covered += Math.Min(probability, Math.Max(0, coverage));
                if (coverage >= probability - epsilon)
                    fully++;
                else
                    all = false;
            }

            if (count == 0)
                return (0, false, 0);

            return (total <= 0 ? 0 : covered / total, all, fully);
        }

        /// <summary>
        /// A unit stops loading a rigid when it reaches the rigid through at least one root VMD and every
        /// root VMD it reaches the rigid through is retired for it.
        /// </summary>
        public static bool IsUnitRetired(
            IEnumerable<string> unitRoots,
            IReadOnlySet<string> rootsReachingRigid,
            Func<string, bool> isRetiredInRoot)
        {
            var any = false;
            foreach (var root in unitRoots)
            {
                if (!rootsReachingRigid.Contains(root))
                    continue;

                any = true;
                if (!isRetiredInRoot(root))
                    return false;
            }

            return any;
        }
    }

    public sealed partial class PackTextureAtlasBatchService
    {
        private const int RetirementReportTopN = 20;

        private sealed class RetirementRvState
        {
            public int Occurrences;
            public int Uncovered;
            public double Total;
            public double Covered;
        }

        private sealed class RetirementCoverage
        {
            public required string Name { get; init; }
            public required Dictionary<CrossRigidWsModelOccurrenceKey, double> Coverage { get; init; }
            public required HashSet<string> VmdsWithPlans { get; init; }
        }

        private sealed class RetirementContext
        {
            public required BatchState State { get; init; }
            public required GameplayMeshDependencyIndex? Index { get; init; }
            public required Dictionary<string, List<(CrossRigidWsModelOccurrenceKey Key, double P)>> OccByVmd { get; init; }
            public required Dictionary<string, HashSet<string>> RigidsOfWs { get; init; }
            public required Dictionary<string, HashSet<string>> WsOfRigid { get; init; }
            public required Dictionary<string, long> RigidBytes { get; init; }
            public required Dictionary<string, HashSet<string>> VmdsOfRigid { get; init; }
            public required Dictionary<string, List<(CrossRigidWsModelOccurrenceKey Key, double P)>> OccByRigidVmd { get; init; }
            public required Dictionary<string, List<(CrossRigidWsModelOccurrenceKey Key, double P)>> OccByWs { get; init; }
            public required Dictionary<string, List<string>> VmdsByUnit { get; init; }
            public required Dictionary<CrossRigidWsModelOccurrenceKey, List<(CrossRigidMergeAnalysisEntry Plan, CrossRigidRewriteOccurrenceSet Set, bool Selected)>> Coverers { get; init; }
            public required Dictionary<string, CrossRigidPayloadSelectionEntry> DecisionByPayload { get; init; }
            public required Dictionary<string, List<CrossRigidMergeAnalysisEntry>> PlansByPayload { get; init; }
            public Dictionary<string, double> ProbabilityBefore { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, double> ProbabilityAfterCache { get; } = new(StringComparer.Ordinal);
            public int ModelCalls;
        }

        private static string ShortVmdName(string path)
        {
            var slash = path.LastIndexOfAny(['/', '\\']);
            var name = slash >= 0 ? path[(slash + 1)..] : path;
            const string suffix = ".variantmeshdefinition";
            return name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? name[..^suffix.Length]
                : name;
        }

        private static string ShortAssetName(string path)
        {
            var slash = path.LastIndexOfAny(['/', '\\']);
            return slash >= 0 ? path[(slash + 1)..] : path;
        }

        private static string FormatMib(double bytes)
            => (bytes / (1024.0 * 1024.0)).ToString("0.00", CultureInfo.InvariantCulture) + " MiB";

        private static string Pct(double value)
            => (value * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";

        private static void AppendCrossRigidRetirementReport(StringBuilder sb, BatchState state)
        {
            sb.AppendLine();
            sb.AppendLine("Cross-rigid retirement potential (report-only)");
            sb.AppendLine("----------------------------------------------");
            if (!state.ReportDiagnostics)
            {
                sb.AppendLine("Skipped: report diagnostics are off.");
                return;
            }

            if (state.CrossRigidMergeAnalysisEntries.Count == 0 ||
                state.CrossRigidWsModelOccurrenceProbabilities.Count == 0)
            {
                sb.AppendLine("No cross-rigid candidate plans.");
                return;
            }

            var timer = Stopwatch.StartNew();
            var previousCulture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            try
            {
                var context = BuildRetirementContext(state);
                var all = BuildRetirementCoverage(state, "ALL candidate plans", selectedOnly: false);
                var selected = BuildRetirementCoverage(state, "SELECTED (emitted)", selectedOnly: true);
                var allResult = EvaluateRetirementScope(context, all);
                var selResult = EvaluateRetirementScope(context, selected);

                sb.AppendLine(
                    "Coverage per source occurrence (VMD, attachment, WSModel) = sum of rewrite occurrence-set " +
                    "probabilities covering it, clamped to 1. In the ALL scope overlapping candidate plans add up, " +
                    "so ALL is an upper bound. A rigid is unit-retired for a VMD when every occurrence of a WSModel " +
                    "using it in that VMD is covered up to its own probability.");
                AppendRetirementScope(sb, context, all, allResult);
                AppendRetirementScope(sb, context, selected, selResult);
                AppendRetirementWhatIf(sb, context, selected, selResult, allResult);
                sb.AppendLine(
                    $"Retirement section time: {timer.Elapsed.TotalSeconds:0.00} s " +
                    $"({context.ModelCalls:N0} residency-model evaluations)");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                sb.AppendLine($"Retirement report failed: {exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
            }
        }

        private static RetirementContext BuildRetirementContext(BatchState state)
        {
            var rigidsOfWs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var rigidBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var plan in state.CrossRigidMergeAnalysisEntries)
            {
                foreach (var source in plan.SourceGeometry)
                {
                    var ws = Normalize(source.WsModelPath);
                    if (!rigidsOfWs.TryGetValue(ws, out var rigids))
                    {
                        rigids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        rigidsOfWs[ws] = rigids;
                    }

                    rigids.Add(source.RigidPath);
                    rigidBytes.TryAdd(source.RigidPath, source.GeometryBytes);
                }
            }

            var index = state.GameplayMeshDependencyIndex;
            var wsOfRigid = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (ws, rigids) in rigidsOfWs)
            {
                foreach (var rigid in rigids)
                {
                    if (!wsOfRigid.TryGetValue(rigid, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        wsOfRigid[rigid] = set;
                    }

                    set.Add(ws);
                }
            }

            if (index != null)
            {
                foreach (var rigid in rigidBytes.Keys)
                {
                    if (index.WsModelConsumersByRigid.TryGetValue(rigid, out var consumers))
                        wsOfRigid[rigid].UnionWith(consumers);
                }
            }

            var occByVmd = new Dictionary<string, List<(CrossRigidWsModelOccurrenceKey, double)>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var (key, probability) in state.CrossRigidWsModelOccurrenceProbabilities)
            {
                if (!occByVmd.TryGetValue(key.VmdPath, out var list))
                {
                    list = [];
                    occByVmd[key.VmdPath] = list;
                }

                list.Add((key, probability));
            }

            var vmdsOfRigid = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var occByRigidVmd = new Dictionary<string, List<(CrossRigidWsModelOccurrenceKey, double)>>(StringComparer.OrdinalIgnoreCase);
            var occByWs = new Dictionary<string, List<(CrossRigidWsModelOccurrenceKey, double)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (vmd, occurrences) in occByVmd)
            {
                foreach (var (key, probability) in occurrences)
                {
                    if (!occByWs.TryGetValue(key.WsModelPath, out var wsList))
                    {
                        wsList = [];
                        occByWs[key.WsModelPath] = wsList;
                    }

                    wsList.Add((key, probability));
                    if (!rigidsOfWs.TryGetValue(key.WsModelPath, out var rigids))
                        continue;

                    foreach (var rigid in rigids)
                    {
                        if (!vmdsOfRigid.TryGetValue(rigid, out var vmds))
                        {
                            vmds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            vmdsOfRigid[rigid] = vmds;
                        }

                        vmds.Add(vmd);
                        var rv = rigid + "\u001f" + vmd;
                        if (!occByRigidVmd.TryGetValue(rv, out var rvList))
                        {
                            rvList = [];
                            occByRigidVmd[rv] = rvList;
                        }

                        rvList.Add((key, probability));
                    }
                }
            }

            var vmdsByUnit = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (state.ArmyResidencyModel != null)
            {
                foreach (var (vmd, byCategory) in state.ArmyResidencyModel.UnitsByVmd)
                {
                    foreach (var unit in byCategory.Values.SelectMany(units => units))
                    {
                        if (!vmdsByUnit.TryGetValue(unit, out var list))
                        {
                            list = [];
                            vmdsByUnit[unit] = list;
                        }

                        list.Add(vmd);
                    }
                }
            }

            var coverers = new Dictionary<
                CrossRigidWsModelOccurrenceKey,
                List<(CrossRigidMergeAnalysisEntry, CrossRigidRewriteOccurrenceSet, bool)>>();
            foreach (var plan in state.CrossRigidMergeAnalysisEntries)
            {
                foreach (var set in plan.RewriteOccurrenceSets)
                {
                    var isSelected = state.CrossRigidSelectedRewriteOccurrenceKeys.Contains(
                        BuildCrossRigidSelectedRewriteOccurrenceKey(plan, set));
                    foreach (var instance in set.SourceInstances.Distinct())
                    {
                        var key = new CrossRigidWsModelOccurrenceKey(
                            plan.VmdPath,
                            instance.AttachmentIdentity,
                            instance.WsModelPath);
                        if (!coverers.TryGetValue(key, out var list))
                        {
                            list = [];
                            coverers[key] = list;
                        }

                        list.Add((plan, set, isSelected));
                    }
                }
            }

            var decisions = new Dictionary<string, CrossRigidPayloadSelectionEntry>(StringComparer.Ordinal);
            foreach (var entry in state.CrossRigidPayloadSelectionEntries)
            {
                if (!decisions.TryGetValue(entry.GeneratedPayloadId, out var current) ||
                    (!current.Accepted && entry.Accepted))
                {
                    decisions[entry.GeneratedPayloadId] = entry;
                }
            }

            var plansByPayload = state.CrossRigidMergeAnalysisEntries
                .GroupBy(plan => plan.GeneratedPayloadId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

            return new RetirementContext
            {
                State = state,
                Index = index,
                OccByVmd = occByVmd,
                RigidsOfWs = rigidsOfWs,
                WsOfRigid = wsOfRigid,
                RigidBytes = rigidBytes,
                VmdsOfRigid = vmdsOfRigid,
                OccByRigidVmd = occByRigidVmd,
                OccByWs = occByWs,
                VmdsByUnit = vmdsByUnit,
                Coverers = coverers,
                DecisionByPayload = decisions,
                PlansByPayload = plansByPayload,
            };
        }

        private static RetirementCoverage BuildRetirementCoverage(
            BatchState state,
            string name,
            bool selectedOnly)
        {
            var coverage = new Dictionary<CrossRigidWsModelOccurrenceKey, double>();
            var vmdsWithPlans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var plan in state.CrossRigidMergeAnalysisEntries)
            {
                foreach (var set in plan.RewriteOccurrenceSets)
                {
                    if (selectedOnly &&
                        !state.CrossRigidSelectedRewriteOccurrenceKeys.Contains(
                            BuildCrossRigidSelectedRewriteOccurrenceKey(plan, set)))
                    {
                        continue;
                    }

                    vmdsWithPlans.Add(plan.VmdPath);
                    foreach (var instance in set.SourceInstances.Distinct())
                    {
                        var key = new CrossRigidWsModelOccurrenceKey(
                            plan.VmdPath,
                            instance.AttachmentIdentity,
                            instance.WsModelPath);
                        coverage[key] = Math.Min(1.0, coverage.GetValueOrDefault(key) + set.Probability);
                    }
                }
            }

            return new RetirementCoverage { Name = name, Coverage = coverage, VmdsWithPlans = vmdsWithPlans };
        }

        // ---------------------------------------------------------------- scope evaluation

        private sealed class RetirementVmdStats
        {
            public required string Vmd { get; init; }
            // Occurrences of WSModels that appear in at least one candidate plan ("mergeable").
            public double RelaxedShare;
            public bool RelaxedFull;
            public int RelaxedOccurrences;
            public int RelaxedFullyCovered;
            // Every occurrence of the VMD.
            public double StrictShare;
            public bool StrictFull;
            public int StrictOccurrences;
            public long SourceBytes;
            public long RetiredBytes;
            public int RigidCount;
            public int RetiredRigidCount;
        }

        private sealed class RetirementScopeResult
        {
            public Dictionary<string, RetirementRvState> Rv { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, RetirementVmdStats> Vmds { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, HashSet<string>> RetiredUnitsByRigid { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, double> CreditByRigid { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, double> CeilingByRigid { get; } = new(StringComparer.OrdinalIgnoreCase);
            public double CreditTotal;
            public double CeilingTotal;
            // Rigids that would be pack-removable if the root-without-occurrence-data blocker were waived.
            public HashSet<string> WaivedEligibleRigids { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private static RetirementRvState GetRv(
            RetirementContext context,
            RetirementCoverage scope,
            RetirementScopeResult result,
            string rigid,
            string vmd)
        {
            var key = rigid + "\u001f" + vmd;
            if (result.Rv.TryGetValue(key, out var cached))
                return cached;

            var state = new RetirementRvState();
            if (context.OccByRigidVmd.TryGetValue(rigid + "\u001f" + vmd, out var occurrences))
                FillRv(state, occurrences, scope.Coverage);

            result.Rv[key] = state;
            return state;
        }

        private static void FillRv(
            RetirementRvState state,
            List<(CrossRigidWsModelOccurrenceKey Key, double P)> occurrences,
            IReadOnlyDictionary<CrossRigidWsModelOccurrenceKey, double> coverage)
        {
            foreach (var (key, probability) in occurrences)
            {
                var covered = coverage.GetValueOrDefault(key);
                state.Occurrences++;
                state.Total += probability;
                state.Covered += Math.Min(probability, covered);
                if (covered < probability - AtlasValueGateExpectedDrawEpsilon)
                    state.Uncovered++;
            }
        }

        private static RetirementScopeResult EvaluateRetirementScope(
            RetirementContext context,
            RetirementCoverage scope)
        {
            var result = new RetirementScopeResult();
            foreach (var (vmd, occurrences) in context.OccByVmd)
            {
                var relaxed = occurrences
                    .Where(entry => context.RigidsOfWs.ContainsKey(entry.Key.WsModelPath))
                    .ToList();
                if (relaxed.Count == 0)
                    continue;

                var (relaxedShare, relaxedFull, relaxedFully) = CrossRigidRetirementMath.Evaluate(
                    relaxed.Select(entry => (entry.P, scope.Coverage.GetValueOrDefault(entry.Key))),
                    AtlasValueGateExpectedDrawEpsilon);
                var (strictShare, strictFull, _) = CrossRigidRetirementMath.Evaluate(
                    occurrences.Select(entry => (entry.P, scope.Coverage.GetValueOrDefault(entry.Key))),
                    AtlasValueGateExpectedDrawEpsilon);
                var stats = new RetirementVmdStats
                {
                    Vmd = vmd,
                    RelaxedShare = relaxedShare,
                    RelaxedFull = relaxedFull,
                    RelaxedOccurrences = relaxed.Count,
                    RelaxedFullyCovered = relaxedFully,
                    StrictShare = strictShare,
                    StrictFull = strictFull,
                    StrictOccurrences = occurrences.Count,
                };
                var rigids = relaxed
                    .SelectMany(entry => context.RigidsOfWs[entry.Key.WsModelPath])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                foreach (var rigid in rigids)
                {
                    var bytes = context.RigidBytes.GetValueOrDefault(rigid);
                    stats.SourceBytes += bytes;
                    stats.RigidCount++;
                    if (GetRv(context, scope, result, rigid, vmd).Uncovered == 0)
                    {
                        stats.RetiredBytes += bytes;
                        stats.RetiredRigidCount++;
                    }
                }

                result.Vmds[vmd] = stats;
            }

            // Residency credit for rigids that are unit-retired in at least one VMD.
            var model = context.State.ArmyResidencyModel;
            if (model != null)
            {
                foreach (var rigid in context.RigidBytes.Keys)
                {
                    var retiredUnits = ComputeRetiredUnits(context, scope, result, rigid);
                    if (retiredUnits.Count == 0)
                        continue;

                    result.RetiredUnitsByRigid[rigid] = retiredUnits;
                    var bytes = context.RigidBytes[rigid];
                    var before = GetRigidProbabilityBefore(context, rigid);
                    var after = GetRigidProbabilityAfter(context, rigid, retiredUnits);
                    var credit = bytes * Math.Max(0, before - after);
                    result.CreditByRigid[rigid] = credit;
                    result.CeilingByRigid[rigid] = bytes * before;
                    result.CreditTotal += credit;
                    result.CeilingTotal += bytes * before;
                }
            }

            return result;
        }

        private static HashSet<string> ComputeRetiredUnits(
            RetirementContext context,
            RetirementCoverage scope,
            RetirementScopeResult result,
            string rigid)
        {
            var retired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var model = context.State.ArmyResidencyModel;
            if (model == null ||
                !context.VmdsOfRigid.TryGetValue(rigid, out var analyzedVmds) ||
                !context.WsOfRigid.TryGetValue(rigid, out var wsModels))
            {
                return retired;
            }

            // Cheap exit: no VMD retires this rigid.
            if (!analyzedVmds.Any(vmd => GetRv(context, scope, result, rigid, vmd).Uncovered == 0))
                return retired;

            var roots = new HashSet<string>(analyzedVmds, StringComparer.OrdinalIgnoreCase);
            if (context.Index != null)
            {
                foreach (var ws in wsModels)
                    roots.UnionWith(context.Index.GetRootsForAsset(ws));
            }

            var relevantUnits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ws in wsModels)
            {
                if (model.UnitIdsByAssetPath.TryGetValue(ws, out var units))
                    relevantUnits.UnionWith(units);
            }

            foreach (var unit in relevantUnits)
            {
                if (!context.VmdsByUnit.TryGetValue(unit, out var unitRoots))
                    continue;

                if (CrossRigidRetirementMath.IsUnitRetired(
                        unitRoots,
                        roots,
                        root => analyzedVmds.Contains(root) &&
                                GetRv(context, scope, result, rigid, root).Uncovered == 0))
                {
                    retired.Add(unit);
                }
            }

            return retired;
        }

        private static double GetRigidProbabilityBefore(RetirementContext context, string rigid)
        {
            if (context.ProbabilityBefore.TryGetValue(rigid, out var cached))
                return cached;

            context.ModelCalls++;
            var value = GetExpectedArmyResidentProbability(
                context.State.ArmyResidencyModel,
                context.WsOfRigid[rigid].ToArray(),
                Array.Empty<string>());
            context.ProbabilityBefore[rigid] = value;
            return value;
        }

        private static double GetRigidProbabilityAfter(
            RetirementContext context,
            string rigid,
            HashSet<string> retiredUnits)
        {
            var key = rigid + "|" + string.Join(",", retiredUnits.OrderBy(unit => unit, StringComparer.Ordinal));
            if (context.ProbabilityAfterCache.TryGetValue(key, out var cached))
                return cached;

            context.ModelCalls++;
            var value = GetExpectedArmyResidentProbability(
                context.State.ArmyResidencyModel,
                context.WsOfRigid[rigid].ToArray(),
                Array.Empty<string>(),
                default,
                retiredUnits);
            context.ProbabilityAfterCache[key] = value;
            return value;
        }

        // ---------------------------------------------------------------- output

        private static string DescribeVmdUnits(RetirementContext context, string vmd)
        {
            var model = context.State.ArmyResidencyModel;
            if (model == null || !model.UnitsByVmd.TryGetValue(vmd, out var byCategory))
                return "units=?";

            var units = byCategory.Values.SelectMany(set => set).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(unit => unit, StringComparer.OrdinalIgnoreCase).ToArray();
            var shown = string.Join("/", units.Take(2));
            return $"units={units.Length}" + (shown.Length == 0 ? "" : $" ({shown}{(units.Length > 2 ? ",.." : "")})");
        }

        private static void AppendRetirementScope(
            StringBuilder sb,
            RetirementContext context,
            RetirementCoverage scope,
            RetirementScopeResult result)
        {
            sb.AppendLine();
            sb.AppendLine($"== Scope: {scope.Name} ==");
            var vmds = result.Vmds.Values.ToArray();
            var touched = vmds.Length;
            var withAnyCoverage = vmds.Count(v => v.RelaxedShare > 0);
            sb.AppendLine(
                $"VMDs with mergeable (candidate-WSModel) occurrences: {touched:N0}; with any coverage: {withAnyCoverage:N0}");
            sb.AppendLine(
                $"VMD coverage of mergeable occurrences (probability-weighted): 100%: {vmds.Count(v => v.RelaxedFull):N0}, " +
                $">=90%: {vmds.Count(v => v.RelaxedShare >= 0.9):N0}, >=80%: {vmds.Count(v => v.RelaxedShare >= 0.8):N0}, " +
                $">=50%: {vmds.Count(v => v.RelaxedShare >= 0.5):N0}");
            sb.AppendLine(
                $"VMD coverage of ALL occurrences (incl. WSModels no plan can touch): 100%: {vmds.Count(v => v.StrictFull):N0}, " +
                $">=90%: {vmds.Count(v => v.StrictShare >= 0.9):N0}, >=50%: {vmds.Count(v => v.StrictShare >= 0.5):N0}");
            var totalOcc = vmds.Sum(v => v.RelaxedOccurrences);
            var fullyOcc = vmds.Sum(v => v.RelaxedFullyCovered);
            sb.AppendLine(
                $"Mergeable occurrences fully covered: {fullyOcc:N0} / {totalOcc:N0} ({Pct(totalOcc == 0 ? 0 : (double)fullyOcc / totalOcc)})");
            sb.AppendLine(
                $"Per-VMD source geometry: {FormatMib(vmds.Sum(v => v.SourceBytes))} summed over VMDs " +
                $"(a rigid used by several VMDs counts once per VMD); unit-retired in the VMD: {FormatMib(vmds.Sum(v => v.RetiredBytes))}");
            var rvRetired = result.Rv.Count(pair => pair.Value.Occurrences > 0 && pair.Value.Uncovered == 0);
            var rvTotal = result.Rv.Count(pair => pair.Value.Occurrences > 0);
            sb.AppendLine($"Source rigid x VMD pairs unit-retired: {rvRetired:N0} / {rvTotal:N0}");

            // Residency credit.
            var retiredRigids = result.RetiredUnitsByRigid.Count;
            var model = context.State.ArmyResidencyModel;
            if (model == null)
            {
                sb.AppendLine("Residency credit: no army residency model.");
            }
            else
            {
                var generatedResidentAll = context.DecisionByPayload.Values.Sum(e => e.ExpectedResidentGeneratedGeometryBytes);
                var generatedResidentSelected = context.DecisionByPayload.Values
                    .Where(e => e.Accepted)
                    .Sum(e => e.ExpectedResidentGeneratedGeometryBytes);
                var generated = scope.Name.StartsWith("ALL", StringComparison.Ordinal)
                    ? generatedResidentAll
                    : generatedResidentSelected;
                sb.AppendLine(
                    $"Residency credit (exact union under the scenario model: a rigid stays resident while any non-retired unit loads it): " +
                    $"{retiredRigids:N0} rigids with >=1 retired unit; credit {FormatMib(result.CreditTotal)}; " +
                    $"ceiling if every unit retired {FormatMib(result.CeilingTotal)}; " +
                    $"generated resident bytes of the payloads in scope {FormatMib(generated)} " +
                    $"(credit/generated {Pct(generated <= 0 ? 0 : result.CreditTotal / generated)})");
                var unitsRetired = result.RetiredUnitsByRigid.Values.Sum(set => set.Count);
                sb.AppendLine($"Retired (unit, rigid) pairs: {unitsRetired:N0}");
            }

            // Top VMDs.
            sb.AppendLine($"Top {RetirementReportTopN} VMDs by unit-retired source bytes (share = mergeable-occurrence coverage):");
            foreach (var v in vmds
                         .OrderByDescending(v => v.RetiredBytes)
                         .ThenByDescending(v => v.RelaxedShare)
                         .Take(RetirementReportTopN)
                         .Where(v => v.RetiredBytes > 0))
            {
                sb.AppendLine(
                    $"  {ShortVmdName(v.Vmd)}: share {Pct(v.RelaxedShare)} ({v.RelaxedFullyCovered}/{v.RelaxedOccurrences} occ), " +
                    $"all-occ {Pct(v.StrictShare)}, retired {FormatMib(v.RetiredBytes)} of {FormatMib(v.SourceBytes)} " +
                    $"({v.RetiredRigidCount}/{v.RigidCount} rigids), {DescribeVmdUnits(context, v.Vmd)}");
            }

            sb.AppendLine($"Top {RetirementReportTopN} rigids by residency credit:");
            foreach (var (rigid, credit) in result.CreditByRigid
                         .OrderByDescending(pair => pair.Value)
                         .Take(RetirementReportTopN)
                         .Where(pair => pair.Value > 0))
            {
                sb.AppendLine(
                    $"  {ShortAssetName(rigid)}: credit {FormatMib(credit)} of {FormatMib(context.RigidBytes[rigid])} " +
                    $"(ceiling {FormatMib(result.CeilingByRigid[rigid])}), retired units {result.RetiredUnitsByRigid[rigid].Count}");
            }

            AppendRetirementPackPotential(sb, context, scope, result);
            AppendRetirementNearMisses(sb, context, scope, result);
        }

        private static string? ClassifyRetirementOwnership(BatchState state, string path)
        {
            var normalized = Normalize(path);
            if (!state.Source.ContainsFile(normalized))
                return "not in source pack (vanilla or dependency pack)";

            foreach (var container in GetGameplayTraversalContainers(state))
            {
                if (container.IsCaPackFile &&
                    !ReferenceEquals(container, state.Source) &&
                    container.ContainsFile(normalized))
                {
                    return "vanilla-owned (source pack overrides a CA file)";
                }
            }

            if (state.Consumers?.IsExternal(normalized) == true)
                return "other mod uses or overrides it";

            return null;
        }

        private const string RootsWithoutDataLabel = "reached by a root with no occurrence data: ";

        private sealed class RetirementBlocker
        {
            public int Primary;
            public long PrimaryBytes;
            public int Any;
            public readonly List<string> Examples = [];
        }

        private static void AppendRetirementPackPotential(
            StringBuilder sb,
            RetirementContext context,
            RetirementCoverage scope,
            RetirementScopeResult result)
        {
            var state = context.State;
            var index = context.Index;
            var blockers = new SortedDictionary<string, RetirementBlocker>(StringComparer.Ordinal);
            var eligible = 0;
            long eligibleBytes = 0;
            long waivedEligibleBytes = 0;
            var waivedBlockers = new SortedDictionary<string, RetirementBlocker>(StringComparer.Ordinal);
            var eligibleExamples = new List<string>();

            void Record(string label, bool primary, string rigid, string example)
            {
                if (!blockers.TryGetValue(label, out var blocker))
                {
                    blocker = new RetirementBlocker();
                    blockers[label] = blocker;
                }

                blocker.Any++;
                if (primary)
                {
                    blocker.Primary++;
                    blocker.PrimaryBytes += context.RigidBytes[rigid];
                    if (blocker.Examples.Count < 3)
                        blocker.Examples.Add(example);
                }
            }

            foreach (var (rigid, bytes) in context.RigidBytes)
            {
                var labels = new List<(string Label, string Example)>();
                var vmds = context.VmdsOfRigid.GetValueOrDefault(rigid) ?? [];
                var firstVmd = vmds.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? "?";
                var ownership = ClassifyRetirementOwnership(state, rigid);
                if (ownership != null)
                    labels.Add(("owner: " + ownership, $"{ShortAssetName(rigid)} ({ShortVmdName(firstVmd)})"));

                var wsModels = context.WsOfRigid[rigid];
                if (index == null ||
                    index.IsAssetAffectedByIncompleteRoot(rigid) ||
                    wsModels.Any(ws => index.IsAssetAffectedByIncompleteRoot(ws)))
                {
                    labels.Add(("incomplete gameplay root", $"{ShortAssetName(rigid)} ({ShortVmdName(firstVmd)})"));
                }

                if (index != null &&
                    index.DirectConsumersByRigid.TryGetValue(rigid, out var direct) &&
                    direct.Count != 0)
                {
                    labels.Add(("direct (non-WSModel) consumer", $"{ShortAssetName(rigid)} <- {ShortAssetName(direct.First())}"));
                }

                if (index != null)
                {
                    foreach (var ws in wsModels)
                    {
                        var occurrenceVmds = (context.OccByWs.GetValueOrDefault(ws) ?? [])
                            .Select(entry => entry.Key.VmdPath)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var unanalyzed = index.GetRootsForAsset(ws).FirstOrDefault(root => !occurrenceVmds.Contains(root));
                        if (unanalyzed != null)
                        {
                            string kind;
                            if (!unanalyzed.EndsWith(".variantmeshdefinition", StringComparison.OrdinalIgnoreCase))
                            {
                                kind = "root is not a VMD (" + Path.GetExtension(unanalyzed) + ")";
                            }
                            else
                            {
                                var rootBlockers = state.CrossRigidAnalysisBlockerCountsByVmd.GetValueOrDefault(unanalyzed);
                                kind = rootBlockers == null || rootBlockers.Count == 0
                                    ? "VMD root without occurrence data, no analysis blocker recorded"
                                    : "VMD root without occurrence data, blocker: " +
                                      rootBlockers.OrderByDescending(pair => pair.Value).First().Key;
                            }

                            labels.Add((
                                RootsWithoutDataLabel + kind,
                                $"{ShortAssetName(rigid)} via {ShortAssetName(ws)} in {ShortVmdName(unanalyzed)}"));
                            break;
                        }
                    }
                }

                var noPlanVmd = vmds.FirstOrDefault(v => !scope.VmdsWithPlans.Contains(v));
                if (noPlanVmd != null)
                {
                    labels.Add((
                        "used by a VMD with no merge plan in scope",
                        $"{ShortAssetName(rigid)} in {ShortVmdName(noPlanVmd)} ({DescribeVmdUnits(context, noPlanVmd)})"));
                }

                var uncoveredVmd = vmds.FirstOrDefault(v =>
                    scope.VmdsWithPlans.Contains(v) && GetRv(context, scope, result, rigid, v).Uncovered != 0);
                if (uncoveredVmd != null)
                {
                    var rv = GetRv(context, scope, result, rigid, uncoveredVmd);
                    labels.Add((
                        "uncovered occurrence in a VMD that has merges",
                        $"{ShortAssetName(rigid)} in {ShortVmdName(uncoveredVmd)} ({rv.Uncovered}/{rv.Occurrences} occ uncovered, {DescribeVmdUnits(context, uncoveredVmd)})"));
                }

                var waived = labels.Where(l => !l.Label.StartsWith(RootsWithoutDataLabel, StringComparison.Ordinal)).ToList();
                if (waived.Count == 0)
                {
                    result.WaivedEligibleRigids.Add(rigid);
                    waivedEligibleBytes += bytes;
                }
                else
                {
                    if (!waivedBlockers.TryGetValue(waived[0].Label, out var wb))
                    {
                        wb = new RetirementBlocker();
                        waivedBlockers[waived[0].Label] = wb;
                    }

                    wb.Primary++;
                    wb.PrimaryBytes += bytes;
                    if (wb.Examples.Count < 3)
                        wb.Examples.Add(waived[0].Example);
                }

                if (labels.Count == 0)
                {
                    eligible++;
                    eligibleBytes += bytes;
                    if (eligibleExamples.Count < 5)
                        eligibleExamples.Add(ShortAssetName(rigid));
                    continue;
                }

                foreach (var (label, example) in labels)
                    Record(label, label == labels[0].Label, rigid, example);
            }

            // WSModel-level count: owned, no external use, complete, every occurrence covered.
            var eligibleWs = 0;
            foreach (var ws in context.RigidsOfWs.Keys)
            {
                if (ClassifyRetirementOwnership(state, ws) != null ||
                    index == null ||
                    index.IsAssetAffectedByIncompleteRoot(ws))
                {
                    continue;
                }

                var occurrences = (context.OccByWs.GetValueOrDefault(ws) ?? []).ToArray();
                if (occurrences.Length == 0)
                    continue;

                var roots = occurrences.Select(entry => entry.Key.VmdPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (index.GetRootsForAsset(ws).Any(root => !roots.Contains(root)))
                    continue;

                if (occurrences.All(entry =>
                        scope.Coverage.GetValueOrDefault(entry.Key) >= entry.P - AtlasValueGateExpectedDrawEpsilon))
                {
                    eligibleWs++;
                }
            }

            sb.AppendLine(
                $"Pack-retirement potential: rigids removable from the pack {eligible:N0} ({FormatMib(eligibleBytes)}) of " +
                $"{context.RigidBytes.Count:N0} candidate source rigids ({FormatMib(context.RigidBytes.Values.Sum())}); " +
                $"WSModels removable {eligibleWs:N0} of {context.RigidsOfWs.Count:N0}");
            if (eligibleExamples.Count != 0)
                sb.AppendLine($"  e.g. {string.Join(", ", eligibleExamples)}");

            sb.AppendLine("  Blockers (primary = first failing check in the order listed; any = rigid has the blocker):");
            foreach (var (label, blocker) in blockers
                         .OrderByDescending(pair => pair.Value.PrimaryBytes)
                         .ThenByDescending(pair => pair.Value.Any))
            {
                sb.AppendLine(
                    $"    {label}: primary {blocker.Primary:N0} ({FormatMib(blocker.PrimaryBytes)}), any {blocker.Any:N0}");
                foreach (var example in blocker.Examples)
                    sb.AppendLine($"      - {example}");
            }

            sb.AppendLine(
                $"  If the 'root with no occurrence data' blocker were waived: removable {result.WaivedEligibleRigids.Count:N0} rigids " +
                $"({FormatMib(waivedEligibleBytes)}); remaining primary blockers:");
            foreach (var (label, blocker) in waivedBlockers.OrderByDescending(pair => pair.Value.PrimaryBytes))
            {
                sb.AppendLine($"    {label}: {blocker.Primary:N0} ({FormatMib(blocker.PrimaryBytes)})");
                foreach (var example in blocker.Examples)
                    sb.AppendLine($"      - {example}");
            }
        }

        private static string DescribeRetirementDecision(
            RetirementContext context,
            RetirementCoverage scope,
            CrossRigidWsModelOccurrenceKey key,
            double probability,
            double coverage)
        {
            context.Coverers.TryGetValue(key, out var coverers);
            coverers ??= [];
            var isAll = scope.Name.StartsWith("ALL", StringComparison.Ordinal);
            if (coverers.Count == 0)
            {
                var blockers = context.State.CrossRigidAnalysisBlockerCountsByVmd.GetValueOrDefault(key.VmdPath);
                var top = blockers?.OrderByDescending(pair => pair.Value).FirstOrDefault();
                return top == null || top.Value.Key == null
                    ? "no plan at this attachment; no analysis blocker recorded for the VMD"
                    : $"no plan at this attachment; VMD analysis blocker: {top.Value.Key}";
            }

            var allCoverage = coverers.Sum(c => c.Set.Probability);
            if (isAll)
                return "plans cover only part of the configuration probability";

            if (allCoverage < probability - AtlasValueGateExpectedDrawEpsilon && coverage <= 0)
                return "plans cover only part of the configuration probability (and none selected)";

            if (coverers.Any(c => c.Selected))
                return "partially selected: remaining occurrence sets conflicted or were not selected";

            var best = coverers
                .Select(c => context.DecisionByPayload.GetValueOrDefault(c.Plan.GeneratedPayloadId))
                .Where(e => e != null)
                .OrderBy(e => e!.SelectionRank)
                .FirstOrDefault();
            if (best == null)
                return "payload not evaluated by the value gate";

            return best.Accepted
                ? "payload accepted but this occurrence set not selected (conflict / joint state)"
                : "payload rejected: " + best.Decision;
        }

        private static void AppendRetirementNearMisses(
            StringBuilder sb,
            RetirementContext context,
            RetirementCoverage scope,
            RetirementScopeResult result)
        {
            var near = result.Vmds.Values
                .Where(v => v.RelaxedShare >= 0.8 && !v.RelaxedFull)
                .OrderByDescending(v => v.SourceBytes)
                .ToArray();
            var reasons = new Dictionary<string, (int Occurrences, double Mass, string Example)>(StringComparer.Ordinal);
            foreach (var v in near)
            {
                foreach (var (key, probability) in context.OccByVmd[v.Vmd])
                {
                    if (!context.RigidsOfWs.ContainsKey(key.WsModelPath))
                        continue;

                    var coverage = scope.Coverage.GetValueOrDefault(key);
                    if (coverage >= probability - AtlasValueGateExpectedDrawEpsilon)
                        continue;

                    var reason = DescribeRetirementDecision(context, scope, key, probability, coverage);
                    var current = reasons.GetValueOrDefault(reason);
                    reasons[reason] = (
                        current.Occurrences + 1,
                        current.Mass + (probability - coverage),
                        current.Example ?? $"{ShortVmdName(v.Vmd)}: {ShortAssetName(key.WsModelPath)}");
                }
            }

            sb.AppendLine(
                $"Near-miss VMDs (>=80% and <100% of mergeable occurrences covered): {near.Length:N0}; " +
                $"residual uncovered occurrences by reason:");
            foreach (var (reason, data) in reasons.OrderByDescending(pair => pair.Value.Mass))
            {
                sb.AppendLine(
                    $"    {reason}: {data.Occurrences:N0} occ, uncovered mass {data.Mass:0.##} (e.g. {data.Example})");
            }

            foreach (var v in near.Take(RetirementReportTopN))
            {
                var blockers = context.State.CrossRigidAnalysisBlockerCountsByVmd.GetValueOrDefault(v.Vmd);
                var blockerText = blockers == null
                    ? ""
                    : "; blockers: " + string.Join(
                        " | ",
                        blockers.OrderByDescending(pair => pair.Value).Take(2).Select(pair => $"{pair.Key} x{pair.Value}"));
                sb.AppendLine(
                    $"    {ShortVmdName(v.Vmd)}: share {Pct(v.RelaxedShare)} ({v.RelaxedFullyCovered}/{v.RelaxedOccurrences} occ), " +
                    $"source {FormatMib(v.SourceBytes)}, {DescribeVmdUnits(context, v.Vmd)}{blockerText}");
            }
        }

        // ---------------------------------------------------------------- what-if

        private static void AppendRetirementWhatIf(
            StringBuilder sb,
            RetirementContext context,
            RetirementCoverage selected,
            RetirementScopeResult selResult,
            RetirementScopeResult allResult)
        {
            var state = context.State;
            var model = state.ArmyResidencyModel;
            sb.AppendLine();
            sb.AppendLine("== What-if: resident-cost gate credited with unit-retired source bytes (estimate only) ==");
            if (model == null)
            {
                sb.AppendLine("No army residency model.");
                return;
            }

            var accepted = context.DecisionByPayload.Values.Where(e => e.Accepted).ToArray();
            var rejected = context.DecisionByPayload.Values.Where(e => !e.Accepted).ToArray();
            sb.AppendLine(
                "Rejected payloads by decision: " + string.Join(
                    ", ",
                    rejected.GroupBy(e => e.Decision)
                        .OrderByDescending(g => g.Count())
                        .Select(g => $"{g.Key}={g.Count():N0}")));

            var acceptedDraws = accepted.Sum(e => e.MarginalExpectedArmyDrawCallsEliminated);
            var acceptedGenerated = (double)accepted.Sum(e => e.GeneratedGeometryBytes);
            var acceptedResident = accepted.Sum(e => e.ExpectedResidentGeneratedGeometryBytes);
            var residentBudget = (double)MaxCrossRigidExpectedResidentGeometryBytesPerExpectedArmyDraw;
            var physicalBudget = (double)MaxCrossRigidGeneratedGeometryBytesPerExpectedArmyDraw;
            var growthCap = (double)MaxCrossRigidGeneratedGeometryGrowthBytes;
            var ceilingMultiplier = CrossRigidPerPayloadCostCeilingMultiplier;
            sb.AppendLine(
                $"Selected: draws {acceptedDraws:0.##}, generated {FormatMib(acceptedGenerated)}, resident {FormatMib(acceptedResident)} " +
                $"(budget {FormatMib(acceptedDraws * residentBudget)}), selected retirement credit {FormatMib(selResult.CreditTotal)}");

            var candidates = rejected
                .Where(e => e.Decision is CrossRigidPayloadSelectionDecision.ScenarioResidencyBudgetExceeded
                    or CrossRigidPayloadSelectionDecision.PerPayloadResidentCostCeilingExceeded)
                .ToArray();
            var rows = new List<(CrossRigidPayloadSelectionEntry Entry, double Draws, double Credit, double Net, int Retired)>();
            foreach (var entry in candidates)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                var draws = entry.MarginalExpectedArmyDrawCallsEliminated > AtlasValueGateExpectedDrawEpsilon
                    ? entry.MarginalExpectedArmyDrawCallsEliminated
                    : entry.ExpectedArmyDrawCallsEliminated;
                var credit = EstimatePayloadMarginalCredit(context, selected, selResult, entry.GeneratedPayloadId, out var retired);
                var net = Math.Max(0, entry.ExpectedResidentGeneratedGeometryBytes - credit);
                rows.Add((entry, draws, credit, net, retired));
            }

            // Per-payload ceiling and greedy portfolio admission over the final selected state.
            double Greedy(bool withCredit, out int count, out double draws, out double generated)
            {
                var d = acceptedDraws;
                var g = acceptedGenerated;
                var r = acceptedResident - (withCredit ? selResult.CreditTotal : 0);
                count = 0;
                draws = 0;
                generated = 0;
                var added = 0.0;
                foreach (var row in rows
                             .OrderBy(row => (withCredit ? row.Net : row.Entry.ExpectedResidentGeneratedGeometryBytes) / Math.Max(row.Draws, 1e-9)))
                {
                    var res = withCredit ? row.Net : row.Entry.ExpectedResidentGeneratedGeometryBytes;
                    var gen = (double)row.Entry.GeneratedGeometryBytes;
                    if (row.Draws <= AtlasValueGateExpectedDrawEpsilon ||
                        res / row.Draws > ceilingMultiplier * residentBudget ||
                        gen / row.Draws > ceilingMultiplier * physicalBudget ||
                        r + res > (d + row.Draws) * residentBudget ||
                        g + gen > (d + row.Draws) * physicalBudget ||
                        g + gen > growthCap)
                    {
                        continue;
                    }

                    r += res;
                    g += gen;
                    d += row.Draws;
                    count++;
                    draws += row.Draws;
                    generated += gen;
                    added += res;
                }

                return added;
            }

            Greedy(false, out var controlCount, out var controlDraws, out var controlGenerated);
            Greedy(true, out var whatIfCount, out var whatIfDraws, out var whatIfGenerated);
            sb.AppendLine(
                $"Resident-gated rejected payloads (portfolio residency budget or per-payload resident ceiling): {candidates.Length:N0}; " +
                $"payloads with a positive marginal credit: {rows.Count(row => row.Credit > 0):N0}; " +
                $"total marginal credit {FormatMib(rows.Sum(row => row.Credit))} (not additive across payloads)");
            sb.AppendLine(
                $"Control (no credit, same greedy over the final state; should be ~0 if the estimate is faithful): " +
                $"{controlCount:N0} payloads, {controlDraws:0.##} draws, {FormatMib(controlGenerated)}");
            sb.AppendLine(
                $"With credit: {whatIfCount:N0} payloads would newly pass the existing budgets, +{whatIfDraws:0.##} expected draws " +
                $"(selected now {acceptedDraws:0.##}), +{FormatMib(whatIfGenerated)} generated geometry");
            var stillPhysical = rejected.Count(e => e.Decision is CrossRigidPayloadSelectionDecision.PhysicalPerDrawBudgetExceeded
                or CrossRigidPayloadSelectionDecision.PerPayloadPhysicalCostCeilingExceeded
                or CrossRigidPayloadSelectionDecision.GlobalGrowthCapExceeded);
            sb.AppendLine(
                $"Unaffected by a resident credit: physical/growth rejections {stillPhysical:N0}, conflicts " +
                $"{rejected.Count(e => e.Decision == CrossRigidPayloadSelectionDecision.RewriteOccurrenceConflict):N0}, " +
                $"zero/small benefit {rejected.Count(e => e.Decision is CrossRigidPayloadSelectionDecision.ScenarioResolvedZeroBenefit or CrossRigidPayloadSelectionDecision.SingleVmdBenefitTooSmall):N0}");

            var physicalRejected = rejected
                .Where(e => e.Decision is CrossRigidPayloadSelectionDecision.PerPayloadPhysicalCostCeilingExceeded
                    or CrossRigidPayloadSelectionDecision.PhysicalPerDrawBudgetExceeded)
                .ToArray();
            var physicalPass = 0;
            double physicalPassDraws = 0;
            double physicalPassBytes = 0;
            var physicalWithCredit = 0;
            foreach (var entry in physicalRejected)
            {
                var plans = context.PlansByPayload.GetValueOrDefault(entry.GeneratedPayloadId);
                if (plans == null || plans.Count == 0)
                    continue;

                var removable = plans[0].SourceGeometry
                    .Select(source => source.RigidPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(allResult.WaivedEligibleRigids.Contains)
                    .Sum(rigid => context.RigidBytes.GetValueOrDefault(rigid));
                if (removable > 0)
                    physicalWithCredit++;

                var draws = entry.MarginalExpectedArmyDrawCallsEliminated > AtlasValueGateExpectedDrawEpsilon
                    ? entry.MarginalExpectedArmyDrawCallsEliminated
                    : entry.ExpectedArmyDrawCallsEliminated;
                var net = Math.Max(0, entry.GeneratedGeometryBytes - removable);
                if (draws > AtlasValueGateExpectedDrawEpsilon &&
                    net / draws <= ceilingMultiplier * physicalBudget &&
                    entry.ExpectedResidentGeneratedGeometryBytes / draws <= ceilingMultiplier * residentBudget)
                {
                    physicalPass++;
                    physicalPassDraws += draws;
                    physicalPassBytes += entry.GeneratedGeometryBytes;
                }
            }

            sb.AppendLine(
                $"Physical-gate rejections {physicalRejected.Length:N0}: {physicalWithCredit:N0} have a source rigid that is pack-removable " +
                $"when the root-data blocker is waived (optimistic bound, needs a retirement writer); " +
                $"{physicalPass:N0} would then pass the per-payload ceilings ({physicalPassDraws:0.##} draws, {FormatMib(physicalPassBytes)} generated)");
            sb.AppendLine($"Top {RetirementReportTopN} resident-gated payloads by credit (gross resident -> net, marginal draws):");
            foreach (var row in rows
                         .OrderByDescending(row => row.Credit)
                         .Take(RetirementReportTopN)
                         .Where(row => row.Credit > 0))
            {
                var rigids = context.PlansByPayload.GetValueOrDefault(row.Entry.GeneratedPayloadId)?
                    .FirstOrDefault()?.RigidPaths.Take(2).Select(ShortAssetName) ?? [];
                sb.AppendLine(
                    $"  {row.Entry.GeneratedPayloadId} [{row.Entry.Decision}]: draws {row.Draws:0.##}, " +
                    $"resident {FormatMib(row.Entry.ExpectedResidentGeneratedGeometryBytes)} -> {FormatMib(row.Net)} " +
                    $"(credit {FormatMib(row.Credit)}, {row.Retired} rigids newly retired), " +
                    $"generated {FormatMib(row.Entry.GeneratedGeometryBytes)}, rigids {string.Join("+", rigids)}");
            }
        }

        private static double EstimatePayloadMarginalCredit(
            RetirementContext context,
            RetirementCoverage selected,
            RetirementScopeResult selResult,
            string payloadId,
            out int newlyRetiredRigids)
        {
            newlyRetiredRigids = 0;
            if (!context.PlansByPayload.TryGetValue(payloadId, out var plans) || plans.Count == 0)
                return 0;

            // Coverage if the payload were added: only occurrences the selected set leaves open.
            var extra = new Dictionary<CrossRigidWsModelOccurrenceKey, double>(selected.Coverage);
            foreach (var plan in plans)
            {
                foreach (var set in plan.RewriteOccurrenceSets)
                {
                    foreach (var instance in set.SourceInstances.Distinct())
                    {
                        var key = new CrossRigidWsModelOccurrenceKey(plan.VmdPath, instance.AttachmentIdentity, instance.WsModelPath);
                        var probability = context.State.CrossRigidWsModelOccurrenceProbabilities.GetValueOrDefault(key);
                        var current = extra.GetValueOrDefault(key);
                        if (current >= probability - AtlasValueGateExpectedDrawEpsilon)
                            continue;

                        extra[key] = Math.Min(1.0, current + set.Probability);
                    }
                }
            }

            var scope = new RetirementCoverage
            {
                Name = "what-if",
                Coverage = extra,
                VmdsWithPlans = selected.VmdsWithPlans,
            };
            var temp = new RetirementScopeResult();
            double credit = 0;
            foreach (var rigid in plans[0].SourceGeometry
                         .Select(source => source.RigidPath)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!context.RigidBytes.ContainsKey(rigid))
                    continue;

                var units = ComputeRetiredUnits(context, scope, temp, rigid);
                var currentUnits = selResult.RetiredUnitsByRigid.GetValueOrDefault(rigid);
                if (units.Count == 0 || (currentUnits != null && units.SetEquals(currentUnits)))
                    continue;

                var after = GetRigidProbabilityAfter(context, rigid, units);
                var afterSelected = currentUnits == null
                    ? GetRigidProbabilityBefore(context, rigid)
                    : GetRigidProbabilityAfter(context, rigid, currentUnits);
                var marginal = context.RigidBytes[rigid] * Math.Max(0, afterSelected - after);
                if (marginal > 0)
                    newlyRetiredRigids++;

                credit += marginal;
            }

            return credit;
        }
    }
}
