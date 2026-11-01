using System.Globalization;
using System.Text;

namespace Editors.PackOptimizer.Core
{
    public sealed partial class PackTextureAtlasBatchService
    {
        private sealed record CrossRigidExpectedDrawCallTerms(
            IReadOnlyDictionary<string, double> GeneratedExpectedDrawCallsByPayload,
            IReadOnlyList<(string RigidPath, double ExpectedMeshParts)> Sources);

        private static void ApplyCrossRigidExpectedDrawCallIncreaseGate(BatchState state)
        {
            var settings = CrossRigidBudgetSettings.Current;
            settings.Validate();
            var limit = settings.MaxExpectedDrawCallIncrease;
            var selectedPayloadIds = state.CrossRigidSelectedPayloadIds
                .ToHashSet(StringComparer.Ordinal);
            var instanceDrawsSavedByPayload = state.CrossRigidPayloadSelectionEntries
                .Where(entry => entry.Accepted && selectedPayloadIds.Contains(entry.GeneratedPayloadId))
                .GroupBy(entry => entry.GeneratedPayloadId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Last().MarginalExpectedArmyDrawCallsEliminated,
                    StringComparer.Ordinal);
            var context = BuildRetirementContext(state);
            var drawCallTerms = BuildCrossRigidExpectedDrawCallTerms(
                state,
                selectedPayloadIds,
                context);

            var selection = PruneCrossRigidPayloadsToDrawCallIncreaseLimit(
                selectedPayloadIds,
                limit,
                instanceDrawsSavedByPayload,
                selected => EvaluateCrossRigidDrawCallTotals(
                    state,
                    selected,
                    context,
                    drawCallTerms).Delta);

            foreach (var payloadId in selection.DroppedPayloadIds)
                RejectCrossRigidPayloadForDrawCallIncrease(state, payloadId);

            var finalSelectedPayloadIds = state.CrossRigidSelectedPayloadIds
                .ToHashSet(StringComparer.Ordinal);
            var finalEvaluation = EvaluateCrossRigidDrawCallDelta(
                state,
                finalSelectedPayloadIds,
                instanceDrawsSavedByPayload,
                context,
                drawCallTerms);
            var droppedPayloads = selection.DroppedPayloadIds
                .Select(payloadId => new CrossRigidExpectedDrawCallDrop(
                    payloadId,
                    instanceDrawsSavedByPayload.GetValueOrDefault(payloadId)))
                .ToArray();
            state.CrossRigidExpectedDrawCallGateReport =
                new CrossRigidExpectedDrawCallGateReport(
                    finalEvaluation,
                    limit,
                    droppedPayloads,
                    selection.InstanceDrawsGivenUp);
        }

        private static void RejectCrossRigidPayloadForDrawCallIncrease(
            BatchState state,
            string payloadId)
        {
            state.CrossRigidSelectedPayloadIds.Remove(payloadId);
            var droppedPlanIds = state.CrossRigidMergeAnalysisEntries
                .Where(plan => plan.GeneratedPayloadId.Equals(payloadId, StringComparison.Ordinal))
                .Select(plan => plan.RewritePlanId)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var planId in droppedPlanIds)
                state.CrossRigidSharedPayloadFallbackAdmittedPlanIds.Remove(planId);

            var droppedOccurrenceKeys = state.CrossRigidMergeAnalysisEntries
                .Where(plan => plan.GeneratedPayloadId.Equals(payloadId, StringComparison.Ordinal))
                .SelectMany(plan => plan.RewriteOccurrenceSets.Select(set =>
                    BuildCrossRigidSelectedRewriteOccurrenceKey(plan, set)))
                .ToHashSet(StringComparer.Ordinal);
            var stillSelectedOccurrenceKeys = state.CrossRigidMergeAnalysisEntries
                .Where(plan => state.CrossRigidSelectedPayloadIds.Contains(plan.GeneratedPayloadId))
                .SelectMany(plan => plan.RewriteOccurrenceSets.Select(set =>
                    BuildCrossRigidSelectedRewriteOccurrenceKey(plan, set)))
                .Where(state.CrossRigidSelectedRewriteOccurrenceKeys.Contains)
                .ToHashSet(StringComparer.Ordinal);
            droppedOccurrenceKeys.ExceptWith(stillSelectedOccurrenceKeys);
            foreach (var key in droppedOccurrenceKeys)
            {
                state.CrossRigidSelectedRewriteOccurrenceKeys.Remove(key);
                state.CrossRigidSelectedJointCombinationKeysByRewriteOccurrenceKey.Remove(key);
                state.CrossRigidSelectedJointCoverageFractionByRewriteOccurrenceKey.Remove(key);
            }

            for (var index = 0; index < state.CrossRigidPayloadSelectionEntries.Count; index++)
            {
                var entry = state.CrossRigidPayloadSelectionEntries[index];
                if (!entry.Accepted || !entry.GeneratedPayloadId.Equals(payloadId, StringComparison.Ordinal))
                    continue;

                state.CrossRigidPayloadSelectionEntries[index] = entry with
                {
                    Decision = CrossRigidPayloadSelectionDecision.RejectedDrawCallIncrease,
                };
            }
        }

        private static CrossRigidExpectedDrawCallEvaluation EvaluateCrossRigidDrawCallDelta(
            BatchState state,
            IReadOnlySet<string> selectedPayloadIds,
            IReadOnlyDictionary<string, double> instanceDrawsSavedByPayload,
            RetirementContext context,
            CrossRigidExpectedDrawCallTerms drawCallTerms)
        {
            var totals = EvaluateCrossRigidDrawCallTotals(
                state,
                selectedPayloadIds,
                context,
                drawCallTerms);
            var perPayload = new List<CrossRigidExpectedDrawCallPayloadDelta>();
            foreach (var payloadId in selectedPayloadIds.OrderBy(id => id, StringComparer.Ordinal))
            {
                var withoutPayload = selectedPayloadIds
                    .Where(id => !id.Equals(payloadId, StringComparison.Ordinal))
                    .ToHashSet(StringComparer.Ordinal);
                var withoutDelta = EvaluateCrossRigidDrawCallTotals(
                    state,
                    withoutPayload,
                    context,
                    drawCallTerms).Delta;
                var marginalDelta = totals.Delta - withoutDelta;
                var drawsSaved = instanceDrawsSavedByPayload.GetValueOrDefault(payloadId);
                var score = drawsSaved <= AtlasValueGateExpectedDrawEpsilon
                    ? double.PositiveInfinity
                    : marginalDelta / drawsSaved;
                if (double.IsNaN(score))
                    score = double.PositiveInfinity;
                perPayload.Add(new CrossRigidExpectedDrawCallPayloadDelta(
                    payloadId,
                    marginalDelta,
                    drawsSaved,
                    score));
            }

            return totals with { PerPayload = perPayload.ToArray() };
        }

        private static CrossRigidExpectedDrawCallEvaluation EvaluateCrossRigidDrawCallTotals(
            BatchState state,
            IReadOnlySet<string> selectedPayloadIds,
            RetirementContext context,
            CrossRigidExpectedDrawCallTerms drawCallTerms)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            if (state.ArmyResidencyModel == null)
            {
                return new CrossRigidExpectedDrawCallEvaluation(
                    0,
                    0,
                    0,
                    Array.Empty<CrossRigidExpectedDrawCallPayloadDelta>(),
                    Array.Empty<CrossRigidExpectedDrawCallSourceDelta>());
            }

            var coverage = BuildCrossRigidDrawCallCoverage(context, selectedPayloadIds);
            var retirement = EvaluateCrossRigidDrawCallRetirement(context, coverage);
            var sources = new List<CrossRigidExpectedDrawCallSourceDelta>();
            double before = 0;
            double after = 0;
            foreach (var (rigid, expectedMeshParts) in drawCallTerms.Sources)
            {
                var probabilityBefore = GetRigidProbabilityBefore(context, rigid);
                var retiredUnits = retirement.RetiredUnitsByRigid.GetValueOrDefault(rigid);
                var probabilityAfter = retiredUnits is { Count: > 0 }
                    ? GetRigidProbabilityAfter(context, rigid, retiredUnits)
                    : probabilityBefore;
                before += probabilityBefore * expectedMeshParts;
                after += probabilityAfter * expectedMeshParts;
                sources.Add(new CrossRigidExpectedDrawCallSourceDelta(
                    rigid,
                    probabilityBefore,
                    probabilityAfter,
                    expectedMeshParts));
            }

            foreach (var payloadId in selectedPayloadIds.OrderBy(
                         id => id,
                         StringComparer.Ordinal))
            {
                after += drawCallTerms.GeneratedExpectedDrawCallsByPayload
                    .GetValueOrDefault(payloadId);
            }

            return new CrossRigidExpectedDrawCallEvaluation(
                before,
                after,
                after - before,
                Array.Empty<CrossRigidExpectedDrawCallPayloadDelta>(),
                sources.ToArray());
        }

        private static CrossRigidExpectedDrawCallTerms BuildCrossRigidExpectedDrawCallTerms(
            BatchState state,
            IReadOnlySet<string> initiallySelectedPayloadIds,
            RetirementContext context)
        {
            if (state.ArmyResidencyModel == null)
            {
                return new CrossRigidExpectedDrawCallTerms(
                    new Dictionary<string, double>(StringComparer.Ordinal),
                    Array.Empty<(string RigidPath, double ExpectedMeshParts)>());
            }

            var lodDistribution = state.ArmyResidencyModel.Scenario.LodDistribution;
            var generatedExpectedDrawCallsByPayload =
                new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var payloadId in initiallySelectedPayloadIds.OrderBy(
                         id => id,
                         StringComparer.Ordinal))
            {
                if (!context.PlansByPayload.TryGetValue(payloadId, out var allPayloadPlans))
                    continue;

                var plans = allPayloadPlans
                    .Where(plan => plan.RewriteOccurrenceSets.Any(set =>
                        state.CrossRigidSelectedRewriteOccurrenceKeys.Contains(
                            BuildCrossRigidSelectedRewriteOccurrenceKey(plan, set))))
                    .ToArray();
                if (plans.Length == 0)
                    continue;

                var generatedPayload = state.CrossRigidGeneratedPayloadAnalysisEntries
                    .FirstOrDefault(entry => entry.GeneratedPayloadId.Equals(
                        payloadId,
                        StringComparison.Ordinal));
                var generatedLods = generatedPayload?.Lods ?? plans[0].Lods;
                var expectedMeshParts = GetExpectedCrossRigidGeneratedMeshParts(
                    generatedLods,
                    lodDistribution);
                var probability = CalculateExpectedCrossRigidPayloadResidentProbability(
                    state,
                    plans);
                generatedExpectedDrawCallsByPayload[payloadId] =
                    probability * expectedMeshParts;
            }

            var sources = context.PlansByPayload.Values
                .SelectMany(payloadPlans => payloadPlans)
                .SelectMany(plan => plan.SourceGeometry)
                .GroupBy(
                    source => Normalize(source.RigidPath),
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var rigid = group.Key;
                    var expectedMeshParts = GetExpectedCrossRigidMeshParts(
                        group.First().MeshPartCounts,
                        lodDistribution);
                    return (RigidPath: rigid, ExpectedMeshParts: expectedMeshParts);
                })
                .Where(source =>
                    source.ExpectedMeshParts > 0 &&
                    context.WsOfRigid.ContainsKey(source.RigidPath))
                .ToArray();

            return new CrossRigidExpectedDrawCallTerms(
                generatedExpectedDrawCallsByPayload,
                sources);
        }

        private static RetirementCoverage BuildCrossRigidDrawCallCoverage(
            RetirementContext context,
            IReadOnlySet<string> selectedPayloadIds)
        {
            var coverage = new Dictionary<CrossRigidWsModelOccurrenceKey, double>();
            var vmdsWithPlans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var payloadId in selectedPayloadIds)
            {
                if (!context.DrawCallCoverageByPayload.TryGetValue(payloadId, out var contributions))
                    continue;

                foreach (var (key, probability) in contributions)
                {
                    vmdsWithPlans.Add(key.VmdPath);
                    coverage[key] = Math.Min(1.0, coverage.GetValueOrDefault(key) + probability);
                }
            }

            return new RetirementCoverage
            {
                Name = "DRAW CALL GATE",
                Coverage = coverage,
                VmdsWithPlans = vmdsWithPlans,
            };
        }

        private static RetirementScopeResult EvaluateCrossRigidDrawCallRetirement(
            RetirementContext context,
            RetirementCoverage coverage)
        {
            var result = new RetirementScopeResult();
            foreach (var rigid in context.RigidBytes.Keys)
            {
                context.State.CancellationToken.ThrowIfCancellationRequested();
                var retiredUnits = ComputeRetiredUnits(
                    context,
                    coverage,
                    result,
                    rigid,
                    ignoreBookkeepingWsSelfRoots: true);
                if (HasCrossRigidDrawCallRetirementBlocker(context, rigid))
                    retiredUnits.Clear();
                if (retiredUnits.Count > 0)
                    result.RetiredUnitsByRigid[rigid] = retiredUnits;
            }

            return result;
        }

        private static double GetExpectedCrossRigidMeshParts(
            IReadOnlyList<int> meshPartCounts,
            IReadOnlyDictionary<int, double> lodDistribution)
        {
            if (meshPartCounts.Count == 0)
                return 0;

            double expected = 0;
            foreach (var (scenarioLodIndex, probability) in lodDistribution)
            {
                if (probability <= 0)
                    continue;
                var sourceLodIndex = Math.Clamp(scenarioLodIndex, 0, meshPartCounts.Count - 1);
                expected += probability * meshPartCounts[sourceLodIndex];
            }

            return expected;
        }

        private static double GetExpectedCrossRigidGeneratedMeshParts(
            IReadOnlyList<CrossRigidLodAnalysis> lods,
            IReadOnlyDictionary<int, double> lodDistribution)
        {
            if (lods.Count == 0)
                return 0;

            var drawCountByLod = lods
                .GroupBy(lod => lod.LodIndex)
                .ToDictionary(
                    group => group.Key,
                    group => group.FirstOrDefault(lod => lod.IsScenarioRepresentativeState)?.OutputDrawsAfterCrossRigidMerge
                        ?? group.First().OutputDrawsAfterCrossRigidMerge);
            var lodCount = drawCountByLod.Keys.Max() + 1;
            double expected = 0;
            foreach (var (scenarioLodIndex, probability) in lodDistribution)
            {
                if (probability <= 0)
                    continue;
                var generatedLodIndex = Math.Clamp(scenarioLodIndex, 0, lodCount - 1);
                if (!drawCountByLod.TryGetValue(generatedLodIndex, out var meshParts))
                {
                    meshParts = drawCountByLod
                        .Where(entry => entry.Key <= generatedLodIndex)
                        .OrderByDescending(entry => entry.Key)
                        .Select(entry => entry.Value)
                        .FirstOrDefault();
                }
                expected += probability * meshParts;
            }

            return expected;
        }

        private static bool HasCrossRigidDrawCallRetirementBlocker(
            RetirementContext context,
            string rigid)
        {
            var index = context.Index;
            if (index == null ||
                index.IsAssetAffectedByIncompleteRoot(rigid) ||
                !context.WsOfRigid.TryGetValue(rigid, out var wsModels) ||
                wsModels.Any(index.IsAssetAffectedByIncompleteRoot))
            {
                return true;
            }

            return index.DirectConsumersByRigid.TryGetValue(rigid, out var directConsumers) &&
                   directConsumers.Count != 0;
        }

        private static (HashSet<string> SelectedPayloadIds, string[] DroppedPayloadIds, double InstanceDrawsGivenUp, double Delta)
            PruneCrossRigidPayloadsToDrawCallIncreaseLimit(
                IEnumerable<string> selectedPayloadIds,
                double limit,
                IReadOnlyDictionary<string, double> instanceDrawsSavedByPayload,
                Func<IReadOnlySet<string>, double> evaluateDelta)
        {
            if (double.IsNaN(limit) || double.IsNegativeInfinity(limit) || limit < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(limit),
                    limit,
                    "The cross-rigid expected draw-call increase limit must be nonnegative, finite, or positive infinity.");
            }

            var selected = selectedPayloadIds.ToHashSet(StringComparer.Ordinal);
            var dropped = new List<string>();
            double drawsGivenUp = 0;
            var delta = evaluateDelta(selected);
            if (double.IsNaN(delta))
                throw new InvalidOperationException("Cross-rigid expected draw-call evaluation returned NaN.");

            while (delta > limit + AtlasValueGateExpectedDrawEpsilon && selected.Count != 0)
            {
                var candidates = selected.Select(payloadId =>
                {
                    var withoutPayload = selected
                        .Where(id => !id.Equals(payloadId, StringComparison.Ordinal))
                        .ToHashSet(StringComparer.Ordinal);
                    var deltaWithoutPayload = evaluateDelta(withoutPayload);
                    var marginalDelta = delta - deltaWithoutPayload;
                    var instanceDrawsSaved = instanceDrawsSavedByPayload.GetValueOrDefault(payloadId);
                    var score = instanceDrawsSaved <= AtlasValueGateExpectedDrawEpsilon
                        ? double.PositiveInfinity
                        : marginalDelta / instanceDrawsSaved;
                    if (double.IsNaN(score))
                        score = double.PositiveInfinity;
                    return (PayloadId: payloadId, DeltaWithout: deltaWithoutPayload, DrawsSaved: instanceDrawsSaved, Score: score);
                })
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.PayloadId, StringComparer.Ordinal)
                .First();

                selected.Remove(candidates.PayloadId);
                dropped.Add(candidates.PayloadId);
                drawsGivenUp += candidates.DrawsSaved;
                delta = candidates.DeltaWithout;
            }

            return (selected, dropped.ToArray(), drawsGivenUp, delta);
        }

        private static void AppendCrossRigidExpectedBattleDrawCallReport(
            StringBuilder sb,
            BatchState state)
        {
            sb.AppendLine();
            sb.AppendLine("Cross-rigid expected battle draw calls (distinct meshes)");
            sb.AppendLine("-------------------------------------------------------");
            var report = state.CrossRigidExpectedDrawCallGateReport;
            if (report == null)
            {
                sb.AppendLine("Not evaluated (cross-rigid merge analysis did not run).");
                return;
            }

            var evaluation = report.Evaluation;
            sb.AppendLine(
                $"Expected draws before={FormatExpectedDrawCallNumber(evaluation.Before)}, " +
                $"after={FormatExpectedDrawCallNumber(evaluation.After)}, " +
                $"Δ={FormatExpectedDrawCallNumber(evaluation.Delta)}; " +
                $"maximum increase={FormatExpectedDrawCallNumber(report.Limit)}.");
            sb.AppendLine(
                $"Payloads dropped by the gate: {report.DroppedPayloads.Length:N0}; " +
                $"instance draws given up: {FormatExpectedDrawCallNumber(report.InstanceDrawsGivenUp)}");
            foreach (var dropped in report.DroppedPayloads)
            {
                sb.AppendLine(
                    $"  dropped={dropped.GeneratedPayloadId}, " +
                    $"instanceDrawsGivenUp={FormatExpectedDrawCallNumber(dropped.InstanceDrawsGivenUp)}");
            }

            if (state.ReportDiagnostics)
            {
                sb.AppendLine("Top 10 selected payloads by marginal ΔD per instance draw:");
                var topPayloads = evaluation.PerPayload
                    .OrderByDescending(entry => entry.DeltaPerInstanceDraw)
                    .ThenBy(entry => entry.GeneratedPayloadId, StringComparer.Ordinal)
                    .Take(10)
                    .ToArray();
                if (topPayloads.Length == 0)
                {
                    sb.AppendLine("  (none)");
                }
                else
                {
                    foreach (var payload in topPayloads)
                    {
                        sb.AppendLine(
                            $"  {payload.GeneratedPayloadId}: marginalΔD={FormatExpectedDrawCallNumber(payload.DeltaDrawCalls)}, " +
                            $"instanceDrawsSaved={FormatExpectedDrawCallNumber(payload.InstanceDrawsSaved)}, " +
                            $"ΔDPerInstanceDraw={FormatExpectedDrawCallNumber(payload.DeltaPerInstanceDraw)}");
                    }
                }
            }

            sb.AppendLine("Source rigids with lower residency after selected rewrites:");
            var lowerResidency = evaluation.SourceRigids
                .Where(source => source.ProbabilityAfter < source.ProbabilityBefore)
                .OrderByDescending(source => source.ProbabilityBefore - source.ProbabilityAfter)
                .ThenBy(source => source.RigidPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (lowerResidency.Length == 0)
            {
                sb.AppendLine("  (none)");
            }
            else
            {
                foreach (var source in lowerResidency)
                {
                    sb.AppendLine(
                        $"  {source.RigidPath}: P={FormatExpectedDrawCallNumber(source.ProbabilityBefore)} -> " +
                        $"{FormatExpectedDrawCallNumber(source.ProbabilityAfter)}, " +
                        $"expectedMeshParts={FormatExpectedDrawCallNumber(source.ExpectedMeshParts)}, " +
                        $"drawsReduced={FormatExpectedDrawCallNumber((source.ProbabilityBefore - source.ProbabilityAfter) * source.ExpectedMeshParts)}");
                }
            }

        }

        private static string FormatExpectedDrawCallNumber(double value)
            => double.IsPositiveInfinity(value)
                ? "inf"
                : value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
