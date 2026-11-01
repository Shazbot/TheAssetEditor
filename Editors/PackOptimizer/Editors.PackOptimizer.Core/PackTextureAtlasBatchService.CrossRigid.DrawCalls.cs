using System.Globalization;
using System.Text;

namespace Editors.PackOptimizer.Core
{
    public sealed partial class PackTextureAtlasBatchService
    {
        private sealed record CrossRigidExpectedDrawCallTerms(
            IReadOnlyDictionary<string, double> GeneratedExpectedDrawCallsByPayload,
            IReadOnlyList<(string RigidPath, double ExpectedMeshParts)> Sources);

        private sealed record CrossRigidDrawCallPortfolioStateSnapshot(
            CrossRigidPayloadSelectionEntry[] PayloadSelectionEntries,
            HashSet<string> SelectedPayloadIds,
            HashSet<string> SelectedRewriteOccurrenceKeys,
            Dictionary<string, HashSet<string>> SelectedJointCombinationKeys,
            Dictionary<string, double> SelectedJointCoverageFractions,
            int SharedFallbackCandidatePlans,
            int SharedFallbackCompatibleStates,
            int SharedFallbackAcceptedStates,
            double SharedFallbackExpectedDraws,
            double SharedFallbackResidentBytes,
            int SharedFallbackBudgetRejections,
            HashSet<string> SharedFallbackAdmittedPlanIds);

        private sealed record CrossRigidDrawCallPortfolioTrial(
            double Objective,
            long GeometryBytes,
            double ResidentBytes,
            double Delta,
            CrossRigidExpectedDrawCallEvaluation DrawCallEvaluation,
            CrossRigidDrawCallPortfolioStateSnapshot State);

        private sealed record CrossRigidPortfolioReplayDetails(
            HashSet<string> SelectedRewriteConflictKeys,
            Dictionary<string, string> SelectedJointContextKeyByParentLocation,
            Dictionary<string, List<CrossRigidRewriteConflictOccurrence>>
                SelectedOccurrencesByPayload,
            Dictionary<string, HashSet<string>>
                SelectedJointCombinationKeysByRewriteOccurrenceKey,
            CrossRigidPayloadSelectionEntry[] AcceptedEntries);

        private sealed record CrossRigidPortfolioSearchProposal(
            CrossRigidPayloadSelectionEntry[] OrderedCandidates,
            bool IsCohort,
            bool IsAlternativeCohort);

        private sealed record CrossRigidSourceRigidCompletionCohort(
            string RigidPath,
            CrossRigidPayloadSelectionEntry[] Candidates,
            bool IsAlternative);

        private static void ApplyCrossRigidExpectedDrawCallIncreaseGate(BatchState state)
        {
            var settings = CrossRigidBudgetSettings.Current;
            settings.Validate();
            var limit = settings.MaxExpectedDrawCallIncrease;
            var selectedPayloadIds = state.CrossRigidSelectedPayloadIds
                .ToHashSet(StringComparer.Ordinal);
            var originallySelectedPayloadIds =
                selectedPayloadIds.ToHashSet(StringComparer.Ordinal);
            var instanceDrawsSavedByPayload = state.CrossRigidPayloadSelectionEntries
                .Where(entry => entry.Accepted && selectedPayloadIds.Contains(entry.GeneratedPayloadId))
                .GroupBy(entry => entry.GeneratedPayloadId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Last().MarginalExpectedArmyDrawCallsEliminated,
                    StringComparer.Ordinal);
            var originalInstanceDrawsSavedByPayload =
                new Dictionary<string, double>(
                    instanceDrawsSavedByPayload,
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

            var postPruneSelectedPayloadIds = state.CrossRigidSelectedPayloadIds
                .ToHashSet(StringComparer.Ordinal);
            var postPruneEvaluation = EvaluateCrossRigidDrawCallDelta(
                state,
                postPruneSelectedPayloadIds,
                instanceDrawsSavedByPayload,
                context,
                drawCallTerms);
            var postPruneState =
                CaptureCrossRigidDrawCallPortfolioState(state);
            var portfolioSearch = OptimizeCrossRigidPortfolioAfterDrawCallGate(
                state,
                limit,
                postPruneEvaluation.Delta,
                state.CrossRigidDrawCallPortfolioSearchContext);

            var finalSelectedPayloadIds = state.CrossRigidSelectedPayloadIds
                .ToHashSet(StringComparer.Ordinal);
            instanceDrawsSavedByPayload = state.CrossRigidPayloadSelectionEntries
                .Where(entry => entry.Accepted && finalSelectedPayloadIds.Contains(entry.GeneratedPayloadId))
                .GroupBy(entry => entry.GeneratedPayloadId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Last().MarginalExpectedArmyDrawCallsEliminated,
                    StringComparer.Ordinal);
            context = BuildRetirementContext(state);
            drawCallTerms = BuildCrossRigidExpectedDrawCallTerms(
                state,
                finalSelectedPayloadIds,
                context);
            var finalEvaluation = EvaluateCrossRigidDrawCallDelta(
                state,
                finalSelectedPayloadIds,
                instanceDrawsSavedByPayload,
                context,
                drawCallTerms);
            if (double.IsNaN(finalEvaluation.Delta) ||
                finalEvaluation.Delta >
                limit + AtlasValueGateExpectedDrawEpsilon)
            {
                RestoreCrossRigidDrawCallPortfolioState(state, postPruneState);
                finalSelectedPayloadIds = state.CrossRigidSelectedPayloadIds
                    .ToHashSet(StringComparer.Ordinal);
                context = BuildRetirementContext(state);
                drawCallTerms = BuildCrossRigidExpectedDrawCallTerms(
                    state,
                    finalSelectedPayloadIds,
                    context);
                instanceDrawsSavedByPayload = state.CrossRigidPayloadSelectionEntries
                    .Where(entry => entry.Accepted &&
                                    finalSelectedPayloadIds.Contains(entry.GeneratedPayloadId))
                    .GroupBy(entry => entry.GeneratedPayloadId, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Last().MarginalExpectedArmyDrawCallsEliminated,
                        StringComparer.Ordinal);
                finalEvaluation = EvaluateCrossRigidDrawCallDelta(
                    state,
                    finalSelectedPayloadIds,
                    instanceDrawsSavedByPayload,
                    context,
                    drawCallTerms);
                portfolioSearch = portfolioSearch with
                {
                    BestObjective = portfolioSearch.IncumbentObjective,
                    BestDelta = portfolioSearch.IncumbentDelta,
                    AddedPayloadIds = Array.Empty<string>(),
                    RemovedPayloadIds = Array.Empty<string>(),
                };
            }

            if (double.IsNaN(finalEvaluation.Delta) ||
                finalEvaluation.Delta >
                limit + AtlasValueGateExpectedDrawEpsilon)
            {
                throw new InvalidOperationException(
                    "The cross-rigid draw-call gate could not restore a portfolio within its configured limit.");
            }

            var droppedPayloads = originallySelectedPayloadIds
                .Except(finalSelectedPayloadIds, StringComparer.Ordinal)
                .OrderBy(payloadId => payloadId, StringComparer.Ordinal)
                .Select(payloadId => new CrossRigidExpectedDrawCallDrop(
                    payloadId,
                    instanceDrawsSavedByPayload
                        .GetValueOrDefault(payloadId,
                            originalInstanceDrawsSavedByPayload
                                .GetValueOrDefault(payloadId))))
                .ToArray();
            state.CrossRigidExpectedDrawCallGateReport =
                new CrossRigidExpectedDrawCallGateReport(
                    finalEvaluation,
                    limit,
                    droppedPayloads,
                    droppedPayloads.Sum(drop => drop.InstanceDrawsGivenUp),
                    portfolioSearch);
            state.CrossRigidDrawCallPortfolioSearchContext = null;
        }

        private static CrossRigidExpectedDrawCallPortfolioSearchReport
            OptimizeCrossRigidPortfolioAfterDrawCallGate(
                BatchState state,
                double limit,
                double incumbentDelta,
                CrossRigidDrawCallPortfolioSearchContext? searchContext)
        {
            const int trialsPerRound = 64;
            const int maxRounds = 3;
            const int trialBudget = trialsPerRound * maxRounds;
            const int maxReservedSourceRigidCohortTrials = 16;
            const int maxReservedAlternativeCohortTrials = 8;
            var initialIncumbentIds = state.CrossRigidSelectedPayloadIds
                .ToHashSet(StringComparer.Ordinal);
            var initialIncumbentEntries = state.CrossRigidPayloadSelectionEntries
                .Where(entry => entry.Accepted &&
                                initialIncumbentIds.Contains(entry.GeneratedPayloadId))
                .GroupBy(entry => entry.GeneratedPayloadId, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToArray();
            var incumbentObjective = initialIncumbentEntries.Sum(entry =>
                entry.MarginalExpectedArmyDrawCallsEliminated);
            var incumbentGeometryBytes = initialIncumbentEntries.Sum(entry =>
                entry.GeneratedGeometryBytes);
            var incumbentResidentBytes = initialIncumbentEntries.Sum(entry =>
                entry.ExpectedResidentGeneratedGeometryBytes);
            var candidateCount = searchContext?.OrderedCandidates
                .Select(entry => entry.GeneratedPayloadId)
                .Distinct(StringComparer.Ordinal)
                .Count(id => !initialIncumbentIds.Contains(id)) ?? 0;

            if (searchContext == null || double.IsPositiveInfinity(limit))
            {
                return new CrossRigidExpectedDrawCallPortfolioSearchReport(
                    incumbentObjective,
                    incumbentObjective,
                    incumbentDelta,
                    incumbentDelta,
                    0,
                    trialBudget,
                    trialsPerRound,
                    0,
                    0,
                    Array.Empty<int>(),
                    false,
                    candidateCount,
                    0,
                    0,
                    Array.Empty<string>(),
                    Array.Empty<string>());
            }

            var candidateById = searchContext.OrderedCandidates
                .GroupBy(entry => entry.GeneratedPayloadId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    StringComparer.Ordinal);
            var initialIncumbentOrder = GetCrossRigidPortfolioIncumbentOrder(
                state,
                initialIncumbentIds,
                candidateById);
            var initialUnselectedCandidates = GetCrossRigidPortfolioUnselectedCandidates(
                candidateById,
                initialIncumbentIds,
                searchContext.InvalidProfiles);
            var stableRetirementContext = BuildRetirementContext(state);
            var stableSources = BuildCrossRigidExpectedDrawCallTerms(
                state,
                new HashSet<string>(StringComparer.Ordinal),
                stableRetirementContext).Sources;
            var globalSeenPortfolioKeys = new HashSet<string>(StringComparer.Ordinal);
            var trialCountsByRound = new List<int>();
            var currentObjective = incumbentObjective;
            var currentGeometryBytes = incumbentGeometryBytes;
            var currentResidentBytes = incumbentResidentBytes;
            var currentDelta = incumbentDelta;
            var totalTrials = 0;
            var improvements = 0;
            var cohortTrials = 0;
            var alternativeCohortTrials = 0;
            var budgetExhausted = false;

            CrossRigidPortfolioSearchProposal CreateProposal(
                IReadOnlyList<CrossRigidPayloadSelectionEntry> front,
                IReadOnlyList<CrossRigidPayloadSelectionEntry> incumbentOrder,
                bool includeIncumbent,
                bool isCohort,
                bool isAlternativeCohort)
            {
                var promotedIds = front
                    .Select(entry => entry.GeneratedPayloadId)
                    .ToHashSet(StringComparer.Ordinal);
                var order = includeIncumbent
                    ? front.Concat(incumbentOrder.Where(entry =>
                            !promotedIds.Contains(entry.GeneratedPayloadId)))
                        .ToArray()
                    : front.ToArray();
                return new CrossRigidPortfolioSearchProposal(
                    order,
                    isCohort,
                    isAlternativeCohort);
            }

            var firstPassCohorts = BuildCrossRigidSourceRigidCompletionCohorts(
                state,
                initialUnselectedCandidates,
                initialIncumbentIds,
                stableRetirementContext,
                includeAlternatives: false,
                alternativeLimit: 0);
            var cohortProposalCount = firstPassCohorts.Length * 2;
            var cohortReservation = Math.Min(
                maxReservedSourceRigidCohortTrials,
                cohortProposalCount);
            var firstPassSingleCount = Math.Min(
                initialUnselectedCandidates.Length,
                trialsPerRound - cohortReservation);
            var firstPassProposals = new List<CrossRigidPortfolioSearchProposal>();
            foreach (var candidate in initialUnselectedCandidates.Take(firstPassSingleCount))
            {
                firstPassProposals.Add(CreateProposal(
                    [candidate],
                    initialIncumbentOrder,
                    includeIncumbent: true,
                    isCohort: false,
                    isAlternativeCohort: false));
            }

            foreach (var cohort in firstPassCohorts.Take(
                         (trialsPerRound - firstPassProposals.Count) / 2))
            {
                firstPassProposals.Add(CreateProposal(
                    cohort.Candidates,
                    initialIncumbentOrder,
                    includeIncumbent: false,
                    isCohort: true,
                    cohort.IsAlternative));
                firstPassProposals.Add(CreateProposal(
                    cohort.Candidates,
                    initialIncumbentOrder,
                    includeIncumbent: true,
                    isCohort: true,
                    cohort.IsAlternative));
            }

            var firstPassFillerProposals = new List<CrossRigidPortfolioSearchProposal>();
            foreach (var candidate in initialUnselectedCandidates.Skip(firstPassSingleCount))
            {
                firstPassFillerProposals.Add(CreateProposal(
                    [candidate],
                    initialIncumbentOrder,
                    includeIncumbent: true,
                    isCohort: false,
                    isAlternativeCohort: false));
            }

            var firstPassExpandedCohorts = BuildCrossRigidSourceRigidCompletionCohorts(
                state,
                initialUnselectedCandidates,
                initialIncumbentIds,
                stableRetirementContext,
                includeAlternatives: true,
                alternativeLimit: 2);
            foreach (var cohort in firstPassExpandedCohorts)
            {
                firstPassFillerProposals.Add(CreateProposal(
                    cohort.Candidates,
                    initialIncumbentOrder,
                    includeIncumbent: false,
                    isCohort: true,
                    cohort.IsAlternative));
                firstPassFillerProposals.Add(CreateProposal(
                    cohort.Candidates,
                    initialIncumbentOrder,
                    includeIncumbent: true,
                    isCohort: true,
                    cohort.IsAlternative));
            }

            for (var roundIndex = 0; roundIndex < maxRounds; roundIndex++)
            {
                var currentIds = state.CrossRigidSelectedPayloadIds
                    .ToHashSet(StringComparer.Ordinal);
                var retirementContext = BuildRetirementContext(state);
                var incumbentOrder = roundIndex == 0
                    ? initialIncumbentOrder
                    : GetCrossRigidPortfolioIncumbentOrder(
                        state,
                        currentIds,
                        candidateById);
                var proposals = new List<CrossRigidPortfolioSearchProposal>();
                if (roundIndex == 0)
                {
                    // Keep the complete v45 proposal prefix intact. Fill any slots lost
                    // to duplicate portfolios only after that prefix has been queued.
                    proposals.AddRange(firstPassProposals);
                    proposals.AddRange(firstPassFillerProposals);
                }
                else
                {
                    var unselectedCandidates = GetCrossRigidPortfolioUnselectedCandidates(
                        candidateById,
                        currentIds,
                        searchContext.InvalidProfiles);
                    foreach (var candidate in unselectedCandidates)
                    {
                        proposals.Add(CreateProposal(
                            [candidate],
                            incumbentOrder,
                            includeIncumbent: true,
                            isCohort: false,
                            isAlternativeCohort: false));
                        // A candidate alone can replace an incumbent that consumes
                        // geometry budget even when no occurrence key conflicts.
                        proposals.Add(CreateProposal(
                            [candidate],
                            incumbentOrder,
                            includeIncumbent: false,
                            isCohort: false,
                            isAlternativeCohort: false));
                    }

                    var expandedCohorts = BuildCrossRigidSourceRigidCompletionCohorts(
                        state,
                        unselectedCandidates,
                        currentIds,
                        retirementContext,
                        includeAlternatives: true,
                        alternativeLimit: roundIndex == 1 ? 4 : 8);
                    foreach (var cohort in expandedCohorts)
                    {
                        proposals.Add(CreateProposal(
                            cohort.Candidates,
                            incumbentOrder,
                            includeIncumbent: false,
                            isCohort: true,
                            cohort.IsAlternative));
                        proposals.Add(CreateProposal(
                            cohort.Candidates,
                            incumbentOrder,
                            includeIncumbent: true,
                            isCohort: true,
                            cohort.IsAlternative));
                    }
                }

                var evaluatedThisRound = 0;
                var seenInRound = new HashSet<string>(StringComparer.Ordinal);
                CrossRigidDrawCallPortfolioTrial? bestTrial = null;
                var bestObjective = currentObjective;
                var bestGeometryBytes = currentGeometryBytes;
                var bestResidentBytes = currentResidentBytes;
                var bestDelta = currentDelta;
                bool TryEvaluateProposal(CrossRigidPortfolioSearchProposal proposal)
                {
                    var portfolioKey = BuildCrossRigidPortfolioSearchKey(
                        proposal.OrderedCandidates);
                    if (globalSeenPortfolioKeys.Contains(portfolioKey) ||
                        !seenInRound.Add(portfolioKey))
                    {
                        return false;
                    }

                    if (totalTrials >= trialBudget)
                    {
                        budgetExhausted = true;
                        return false;
                    }

                    if (evaluatedThisRound >= trialsPerRound)
                        return false;

                    state.CancellationToken.ThrowIfCancellationRequested();
                    globalSeenPortfolioKeys.Add(portfolioKey);
                    totalTrials++;
                    evaluatedThisRound++;
                    if (proposal.IsCohort)
                    {
                        cohortTrials++;
                        if (proposal.IsAlternativeCohort)
                            alternativeCohortTrials++;
                    }

                    var trial = EvaluateCrossRigidPortfolioWithSharedFallbacks(
                        state,
                        proposal.OrderedCandidates,
                        searchContext,
                        retirementContext,
                        stableSources);
                    if (double.IsNaN(trial.Delta) ||
                        trial.Delta >
                        limit + AtlasValueGateExpectedDrawEpsilon)
                    {
                        return true;
                    }

                    if (!IsBetterCrossRigidDrawCallPortfolioTrial(
                            trial,
                            bestObjective,
                            bestGeometryBytes,
                            bestResidentBytes))
                    {
                        return true;
                    }

                    bestObjective = trial.Objective;
                    bestGeometryBytes = trial.GeometryBytes;
                    bestResidentBytes = trial.ResidentBytes;
                    bestDelta = trial.Delta;
                    bestTrial = trial;
                    return true;
                }

                var cohortProposals = proposals
                    .Where(proposal => proposal.IsCohort)
                    .ToArray();
                var singleProposals = proposals
                    .Where(proposal => !proposal.IsCohort)
                    .ToArray();
                if (roundIndex == 0)
                {
                    // The first-pass order is the committed v45 sequence, followed by
                    // refill proposals. Keep that ordering intact for non-regression.
                    foreach (var proposal in proposals)
                    {
                        if (evaluatedThisRound >= trialsPerRound || budgetExhausted)
                            break;
                        TryEvaluateProposal(proposal);
                    }
                }
                else
                {
                    var reservedCohortTrials = 0;
                    var reservedAlternativeTrials = 0;
                    foreach (var proposal in cohortProposals.Where(
                                 candidate => candidate.IsAlternativeCohort))
                    {
                        if (reservedAlternativeTrials >= maxReservedAlternativeCohortTrials ||
                            evaluatedThisRound >= trialsPerRound ||
                            budgetExhausted)
                        {
                            break;
                        }

                        if (TryEvaluateProposal(proposal))
                        {
                            reservedAlternativeTrials++;
                            reservedCohortTrials++;
                        }
                    }

                    foreach (var proposal in cohortProposals.Where(
                                 candidate => !candidate.IsAlternativeCohort))
                    {
                        if (reservedCohortTrials >= maxReservedSourceRigidCohortTrials ||
                            evaluatedThisRound >= trialsPerRound ||
                            budgetExhausted)
                        {
                            break;
                        }

                        if (TryEvaluateProposal(proposal))
                            reservedCohortTrials++;
                    }

                    foreach (var proposal in singleProposals)
                    {
                        if (evaluatedThisRound >= trialsPerRound || budgetExhausted)
                            break;
                        TryEvaluateProposal(proposal);
                    }

                    foreach (var proposal in cohortProposals)
                    {
                        if (evaluatedThisRound >= trialsPerRound || budgetExhausted)
                            break;
                        TryEvaluateProposal(proposal);
                    }
                }

                if (totalTrials >= trialBudget && !budgetExhausted)
                {
                    budgetExhausted = proposals.Any(proposal =>
                        !globalSeenPortfolioKeys.Contains(
                            BuildCrossRigidPortfolioSearchKey(
                                proposal.OrderedCandidates)));
                }

                if (evaluatedThisRound == 0)
                    break;

                trialCountsByRound.Add(evaluatedThisRound);
                if (bestTrial != null)
                {
                    ApplyCrossRigidPortfolioTrial(state, bestTrial, currentIds);
                    currentObjective = bestObjective;
                    currentGeometryBytes = bestGeometryBytes;
                    currentResidentBytes = bestResidentBytes;
                    currentDelta = bestDelta;
                    improvements++;
                }

                if (budgetExhausted)
                    break;
            }

            return new CrossRigidExpectedDrawCallPortfolioSearchReport(
                incumbentObjective,
                currentObjective,
                incumbentDelta,
                currentDelta,
                totalTrials,
                trialBudget,
                trialsPerRound,
                trialCountsByRound.Count,
                improvements,
                trialCountsByRound.ToArray(),
                budgetExhausted,
                candidateCount,
                cohortTrials,
                alternativeCohortTrials,
                state.CrossRigidSelectedPayloadIds
                    .Except(initialIncumbentIds, StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray(),
                initialIncumbentIds
                    .Except(state.CrossRigidSelectedPayloadIds, StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray());
        }

        private static string BuildCrossRigidPortfolioSearchKey(
            IReadOnlyList<CrossRigidPayloadSelectionEntry> orderedCandidates)
            => string.Concat(orderedCandidates.Select(entry =>
                entry.GeneratedPayloadId.Length.ToString(CultureInfo.InvariantCulture) + ":" +
                entry.GeneratedPayloadId));

        private static CrossRigidPayloadSelectionEntry[] GetCrossRigidPortfolioIncumbentOrder(
            BatchState state,
            IReadOnlySet<string> incumbentIds,
            IReadOnlyDictionary<string, CrossRigidPayloadSelectionEntry> candidateById)
            => state.CrossRigidPayloadSelectionEntries
                .Where(entry => entry.Accepted &&
                                incumbentIds.Contains(entry.GeneratedPayloadId))
                .Select(entry => candidateById.GetValueOrDefault(entry.GeneratedPayloadId))
                .Where(entry => entry != null)
                .Cast<CrossRigidPayloadSelectionEntry>()
                .GroupBy(entry => entry.GeneratedPayloadId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();

        private static CrossRigidPayloadSelectionEntry[] GetCrossRigidPortfolioUnselectedCandidates(
            IReadOnlyDictionary<string, CrossRigidPayloadSelectionEntry> candidateById,
            IReadOnlySet<string> selectedIds,
            IReadOnlySet<string> invalidProfiles)
            => candidateById.Values
                .Where(entry => !selectedIds.Contains(entry.GeneratedPayloadId) &&
                                !invalidProfiles.Contains(entry.GeneratedPayloadId))
                .OrderByDescending(entry => entry.ExpectedArmyDrawCallsEliminated)
                .ThenBy(entry => entry.GeneratedPayloadId, StringComparer.Ordinal)
                .ToArray();

        private static CrossRigidSourceRigidCompletionCohort[]
            BuildCrossRigidSourceRigidCompletionCohorts(
                BatchState state,
                IReadOnlyList<CrossRigidPayloadSelectionEntry> candidates,
                IReadOnlySet<string> incumbentPayloadIds,
                RetirementContext retirementContext,
                bool includeAlternatives,
                int alternativeLimit)
        {
            var candidateById = candidates
                .GroupBy(entry => entry.GeneratedPayloadId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var baselineCoverage = BuildCrossRigidDrawCallCoverage(
                retirementContext,
                incumbentPayloadIds).Coverage;
            var candidateCoverageByRigid = new Dictionary<
                string,
                Dictionary<string, Dictionary<CrossRigidWsModelOccurrenceKey, double>>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var plan in state.CrossRigidMergeAnalysisEntries)
            {
                if (!candidateById.ContainsKey(plan.GeneratedPayloadId))
                    continue;

                foreach (var rigid in plan.SourceGeometry
                             .Select(source => Normalize(source.RigidPath))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!candidateCoverageByRigid.TryGetValue(rigid, out var byPayload))
                    {
                        byPayload = new Dictionary<
                            string,
                            Dictionary<CrossRigidWsModelOccurrenceKey, double>>(
                            StringComparer.Ordinal);
                        candidateCoverageByRigid[rigid] = byPayload;
                    }

                    if (!byPayload.TryGetValue(plan.GeneratedPayloadId, out var coverageByOccurrence))
                    {
                        coverageByOccurrence = new Dictionary<
                            CrossRigidWsModelOccurrenceKey,
                            double>();
                        byPayload[plan.GeneratedPayloadId] = coverageByOccurrence;
                    }

                    foreach (var set in plan.RewriteOccurrenceSets)
                    {
                        foreach (var instance in set.SourceInstances.Distinct())
                        {
                            if (!retirementContext.RigidsOfWs.TryGetValue(
                                    Normalize(instance.WsModelPath),
                                    out var sourceRigids) ||
                                !sourceRigids.Contains(rigid))
                            {
                                continue;
                            }

                            var occurrenceKey = new CrossRigidWsModelOccurrenceKey(
                                Normalize(plan.VmdPath),
                                instance.AttachmentIdentity,
                                Normalize(instance.WsModelPath));
                            if (!state.CrossRigidWsModelOccurrenceProbabilities.ContainsKey(
                                    occurrenceKey))
                            {
                                continue;
                            }

                            coverageByOccurrence[occurrenceKey] = Math.Min(
                                1.0,
                                coverageByOccurrence.GetValueOrDefault(occurrenceKey) +
                                set.Probability);
                        }
                    }
                }
            }

            var originalCohorts = new List<CrossRigidSourceRigidCompletionCohort>();
            var alternativeCohorts = new List<CrossRigidSourceRigidCompletionCohort>();
            foreach (var (rigid, candidateCoverage) in candidateCoverageByRigid
                         .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                var targetCoverage = state.CrossRigidWsModelOccurrenceProbabilities
                    .Where(pair => retirementContext.RigidsOfWs.TryGetValue(
                                       Normalize(pair.Key.WsModelPath),
                                       out var sourceRigids) &&
                                   sourceRigids.Contains(rigid))
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                if (targetCoverage.Count == 0)
                    continue;

                var remaining = new Dictionary<CrossRigidWsModelOccurrenceKey, double>();
                foreach (var (occurrenceKey, targetProbability) in targetCoverage)
                {
                    var alreadyCovered = Math.Min(
                        targetProbability,
                        baselineCoverage.GetValueOrDefault(occurrenceKey));
                    var uncovered = targetProbability - alreadyCovered;
                    if (uncovered > AtlasValueGateExpectedDrawEpsilon)
                        remaining[occurrenceKey] = uncovered;
                }

                if (remaining.Count == 0)
                    continue;

                var originalCover = BuildCrossRigidSourceRigidCompletionCover(
                    candidateCoverage,
                    candidateById,
                    remaining,
                    incumbentPayloadIds,
                    null);
                if (originalCover != null)
                {
                    originalCohorts.Add(new CrossRigidSourceRigidCompletionCohort(
                        rigid,
                        originalCover,
                        IsAlternative: false));
                }
                if (!includeAlternatives || alternativeLimit <= 0)
                    continue;

                var seenAlternativeCovers = new HashSet<string>(StringComparer.Ordinal);
                if (originalCover != null)
                    seenAlternativeCovers.Add(
                        BuildCrossRigidPortfolioSearchKey(originalCover));
                var alternativeCount = 0;
                foreach (var winner in originalCover ?? Array.Empty<CrossRigidPayloadSelectionEntry>())
                {
                    if (alternativeCount >= alternativeLimit)
                        break;
                    var excluded = incumbentPayloadIds.ToHashSet(StringComparer.Ordinal);
                    excluded.Add(winner.GeneratedPayloadId);
                    var alternativeCover = BuildCrossRigidSourceRigidCompletionCover(
                        candidateCoverage,
                        candidateById,
                        remaining,
                        excluded,
                        null);
                    if (!AddAlternativeCover(alternativeCover))
                        continue;
                }

                var seedCandidates = candidateCoverage
                    .Select(pair => (
                        PayloadId: pair.Key,
                        Gain: remaining.Sum(item => Math.Min(
                            item.Value,
                            pair.Value.GetValueOrDefault(item.Key)))))
                    .Where(candidate =>
                        candidate.Gain > AtlasValueGateExpectedDrawEpsilon &&
                        !incumbentPayloadIds.Contains(candidate.PayloadId))
                    .OrderByDescending(candidate => candidate.Gain)
                    .ThenByDescending(candidate => candidateById[candidate.PayloadId]
                        .ExpectedArmyDrawCallsEliminated)
                    .ThenBy(candidate => candidate.PayloadId, StringComparer.Ordinal)
                    .Take(Math.Max(4, alternativeLimit * 2));
                foreach (var seed in seedCandidates)
                {
                    if (alternativeCount >= alternativeLimit)
                        break;
                    var alternativeCover = BuildCrossRigidSourceRigidCompletionCover(
                        candidateCoverage,
                        candidateById,
                        remaining,
                        incumbentPayloadIds,
                        seed.PayloadId);
                    AddAlternativeCover(alternativeCover);
                }

                bool AddAlternativeCover(CrossRigidPayloadSelectionEntry[]? cover)
                {
                    if (cover == null || cover.Length < 2)
                        return false;
                    var key = BuildCrossRigidPortfolioSearchKey(cover);
                    if (!seenAlternativeCovers.Add(key))
                        return false;
                    alternativeCohorts.Add(new CrossRigidSourceRigidCompletionCohort(
                        rigid,
                        cover,
                        IsAlternative: true));
                    alternativeCount++;
                    return true;
                }
            }

            return originalCohorts
                .OrderByDescending(cohort => cohort.Candidates.Sum(candidate =>
                    candidate.ExpectedArmyDrawCallsEliminated))
                .ThenBy(cohort => cohort.RigidPath, StringComparer.OrdinalIgnoreCase)
                .Concat(alternativeCohorts
                    .OrderByDescending(cohort => cohort.Candidates.Sum(candidate =>
                        candidate.ExpectedArmyDrawCallsEliminated))
                    .ThenBy(cohort => cohort.RigidPath, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }

        private static CrossRigidPayloadSelectionEntry[]?
            BuildCrossRigidSourceRigidCompletionCover(
                IReadOnlyDictionary<
                    string,
                    Dictionary<CrossRigidWsModelOccurrenceKey, double>> candidateCoverage,
                IReadOnlyDictionary<string, CrossRigidPayloadSelectionEntry> candidateById,
                IReadOnlyDictionary<CrossRigidWsModelOccurrenceKey, double> targetCoverage,
                IReadOnlySet<string> excludedPayloadIds,
                string? forcedFirstPayloadId)
        {
            var remaining = new Dictionary<CrossRigidWsModelOccurrenceKey, double>(
                targetCoverage);
            var chosen = new List<CrossRigidPayloadSelectionEntry>();
            var chosenIds = new HashSet<string>(StringComparer.Ordinal);

            void Select(string payloadId)
            {
                var candidateCoverageByOccurrence = candidateCoverage[payloadId];
                chosen.Add(candidateById[payloadId]);
                chosenIds.Add(payloadId);
                foreach (var occurrenceKey in remaining.Keys.ToArray())
                {
                    var uncovered = remaining[occurrenceKey] -
                                    candidateCoverageByOccurrence.GetValueOrDefault(
                                        occurrenceKey);
                    if (uncovered <= AtlasValueGateExpectedDrawEpsilon)
                        remaining.Remove(occurrenceKey);
                    else
                        remaining[occurrenceKey] = uncovered;
                }
            }

            if (forcedFirstPayloadId != null)
            {
                if (excludedPayloadIds.Contains(forcedFirstPayloadId) ||
                    !candidateCoverage.ContainsKey(forcedFirstPayloadId))
                {
                    return null;
                }

                var forcedGain = remaining.Sum(item => Math.Min(
                    item.Value,
                    candidateCoverage[forcedFirstPayloadId].GetValueOrDefault(item.Key)));
                if (forcedGain <= AtlasValueGateExpectedDrawEpsilon)
                    return null;
                Select(forcedFirstPayloadId);
            }

            while (remaining.Count != 0 && chosen.Count < 8)
            {
                CrossRigidPayloadSelectionEntry? bestCandidate = null;
                double bestGain = 0;
                foreach (var (payloadId, coverageByOccurrence) in candidateCoverage)
                {
                    if (excludedPayloadIds.Contains(payloadId) ||
                        chosenIds.Contains(payloadId) ||
                        !candidateById.TryGetValue(payloadId, out var candidate))
                    {
                        continue;
                    }

                    var gain = remaining.Sum(item => Math.Min(
                        item.Value,
                        coverageByOccurrence.GetValueOrDefault(item.Key)));
                    if (gain > bestGain + AtlasValueGateExpectedDrawEpsilon ||
                        (Math.Abs(gain - bestGain) <= AtlasValueGateExpectedDrawEpsilon &&
                         gain > AtlasValueGateExpectedDrawEpsilon &&
                         (bestCandidate == null ||
                          candidate.ExpectedArmyDrawCallsEliminated >
                              bestCandidate.ExpectedArmyDrawCallsEliminated +
                              AtlasValueGateExpectedDrawEpsilon ||
                          (Math.Abs(candidate.ExpectedArmyDrawCallsEliminated -
                                    bestCandidate.ExpectedArmyDrawCallsEliminated) <=
                           AtlasValueGateExpectedDrawEpsilon &&
                           string.CompareOrdinal(
                               candidate.GeneratedPayloadId,
                               bestCandidate.GeneratedPayloadId) < 0))))
                    {
                        bestGain = gain;
                        bestCandidate = candidate;
                    }
                }

                if (bestCandidate == null ||
                    bestGain <= AtlasValueGateExpectedDrawEpsilon)
                {
                    break;
                }

                Select(bestCandidate.GeneratedPayloadId);
            }

            return remaining.Count == 0 && chosen.Count >= 2
                ? chosen.ToArray()
                : null;
        }

        private static bool IsBetterCrossRigidDrawCallPortfolioTrial(
            CrossRigidDrawCallPortfolioTrial candidate,
            double currentObjective,
            long currentGeometryBytes,
            double currentResidentBytes)
        {
            if (candidate.Objective >
                currentObjective + AtlasValueGateExpectedDrawEpsilon)
            {
                return true;
            }

            if (Math.Abs(candidate.Objective - currentObjective) >
                AtlasValueGateExpectedDrawEpsilon)
            {
                return false;
            }

            return candidate.ResidentBytes <
                       currentResidentBytes - AtlasValueGateExpectedDrawEpsilon ||
                   (Math.Abs(candidate.ResidentBytes - currentResidentBytes) <=
                    AtlasValueGateExpectedDrawEpsilon &&
                    candidate.GeometryBytes < currentGeometryBytes);
        }

        private static CrossRigidDrawCallPortfolioTrial
            EvaluateCrossRigidPortfolioWithSharedFallbacks(
                BatchState state,
                IReadOnlyList<CrossRigidPayloadSelectionEntry> orderedCandidates,
                CrossRigidDrawCallPortfolioSearchContext searchContext,
                RetirementContext retirementContext,
                IReadOnlyList<(string RigidPath, double ExpectedMeshParts)> stableSources)
        {
            var originalState = CaptureCrossRigidDrawCallPortfolioState(state);
            try
            {
                var portfolio = EvaluateCrossRigidPayloadPortfolio(
                    state,
                    orderedCandidates,
                    searchContext.ConflictProfiles,
                    searchContext.InvalidProfiles,
                    searchContext.SourceVmdDocuments);
                var details = BuildCrossRigidPortfolioReplayDetails(
                    state,
                    orderedCandidates,
                    portfolio.SelectedPayloadIds,
                    searchContext);
                ApplyCrossRigidPortfolioReplayDetailsToState(
                    state,
                    portfolio.SelectedPayloadIds,
                    details,
                    searchContext.ConflictProfiles);

                var selectedRewriteConflictOwnerByKey =
                    new Dictionary<string, CrossRigidSelectedRewriteConflictOwner>(
                        StringComparer.Ordinal);
                var selectedJointContextOwnerByParentLocation =
                    new Dictionary<string, CrossRigidSelectedRewriteConflictOwner>(
                        StringComparer.Ordinal);
                foreach (var (payloadId, occurrences) in details.SelectedOccurrencesByPayload)
                {
                    var candidate = details.AcceptedEntries.First(entry =>
                        entry.GeneratedPayloadId.Equals(payloadId, StringComparison.Ordinal));
                    foreach (var occurrence in occurrences)
                    {
                        var owner = new CrossRigidSelectedRewriteConflictOwner(
                            payloadId,
                            occurrence.SelectionKey,
                            occurrence.ExpectedArmyDrawCallsEliminated,
                            candidate.MaxRawDrawsSavedPerState,
                            candidate.GeneratedGeometryBytes,
                            candidate.SelectionRank);
                        foreach (var key in occurrence.RewriteConflictKeys)
                            selectedRewriteConflictOwnerByKey.TryAdd(key, owner);
                        foreach (var pair in occurrence.JointContextKeyByParentLocation)
                            selectedJointContextOwnerByParentLocation[pair.Key] = owner;
                    }
                }

                var acceptedExpectedDraws = portfolio.ExpectedDraws;
                var acceptedExpectedResidentGeometryBytes = portfolio.ResidentBytes;
                ResetCrossRigidSharedPayloadFallbackState(state);
                AdmitCrossRigidSharedPayloadFallbacks(
                    state,
                    searchContext.PrimaryPlansByPayload,
                    searchContext.PlansByPayload,
                    details.SelectedRewriteConflictKeys,
                    selectedRewriteConflictOwnerByKey,
                    details.SelectedJointContextKeyByParentLocation,
                    selectedJointContextOwnerByParentLocation,
                    searchContext.SourceVmdDocuments,
                    ref acceptedExpectedDraws,
                    ref acceptedExpectedResidentGeometryBytes,
                    reportDiagnostics: false);

                var selectedPayloadIds = state.CrossRigidSelectedPayloadIds
                    .ToHashSet(StringComparer.Ordinal);
                var drawCallTerms = BuildCrossRigidExpectedDrawCallTerms(
                    state,
                    selectedPayloadIds,
                    retirementContext,
                    stableSources);
                var exactCoverage = BuildCrossRigidDrawCallCoverageFromSelectedOccurrences(
                    retirementContext,
                    selectedPayloadIds);
                var drawCallEvaluation = EvaluateCrossRigidDrawCallTotals(
                    state,
                    selectedPayloadIds,
                    retirementContext,
                    drawCallTerms,
                    exactCoverage);
                var acceptedEntries = state.CrossRigidPayloadSelectionEntries
                    .Where(entry => entry.Accepted &&
                                    selectedPayloadIds.Contains(entry.GeneratedPayloadId))
                    .GroupBy(entry => entry.GeneratedPayloadId, StringComparer.Ordinal)
                    .Select(group => group.Last())
                    .ToArray();
                var trialState = CaptureCrossRigidDrawCallPortfolioState(state);
                return new CrossRigidDrawCallPortfolioTrial(
                    acceptedEntries.Sum(entry =>
                        entry.MarginalExpectedArmyDrawCallsEliminated),
                    acceptedEntries.Sum(entry => entry.GeneratedGeometryBytes),
                    acceptedEntries.Sum(entry =>
                        entry.ExpectedResidentGeneratedGeometryBytes),
                    drawCallEvaluation.Delta,
                    drawCallEvaluation,
                    trialState);
            }
            finally
            {
                RestoreCrossRigidDrawCallPortfolioState(state, originalState);
            }
        }

        private static CrossRigidPortfolioReplayDetails
            BuildCrossRigidPortfolioReplayDetails(
                BatchState state,
                IReadOnlyList<CrossRigidPayloadSelectionEntry> orderedCandidates,
                IReadOnlySet<string> selectedPayloadIds,
                CrossRigidDrawCallPortfolioSearchContext searchContext)
        {
            var selectedRewriteConflictKeys = new HashSet<string>(StringComparer.Ordinal);
            var selectedJointContexts = new Dictionary<string, string>(StringComparer.Ordinal);
            var selectedOccurrencesByPayload = new Dictionary<
                string,
                List<CrossRigidRewriteConflictOccurrence>>(StringComparer.Ordinal);
            var jointCombinationKeysByOccurrence = new Dictionary<
                string,
                HashSet<string>>(StringComparer.Ordinal);
            var acceptedEntries = new List<CrossRigidPayloadSelectionEntry>();
            var seenPayloads = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in orderedCandidates)
            {
                var payloadId = candidate.GeneratedPayloadId;
                if (!selectedPayloadIds.Contains(payloadId) ||
                    !seenPayloads.Add(payloadId) ||
                    !searchContext.ConflictProfiles.TryGetValue(payloadId, out var profile))
                {
                    continue;
                }

                var occurrences = SelectCrossRigidCompatibleRewriteOccurrences(
                    profile,
                    selectedRewriteConflictKeys,
                    selectedJointContexts);
                var marginalExpectedDraws = occurrences.Sum(occurrence =>
                    occurrence.ExpectedArmyDrawCallsEliminated);
                var effectiveCandidate = candidate with
                {
                    Decision = CrossRigidPayloadSelectionDecision.Accept,
                    MarginalExpectedArmyDrawCallsEliminated = marginalExpectedDraws,
                    PhysicalBytesPerExpectedDraw = marginalExpectedDraws >
                            AtlasValueGateExpectedDrawEpsilon
                        ? candidate.GeneratedGeometryBytes / marginalExpectedDraws
                        : double.PositiveInfinity,
                    ResidentBytesPerExpectedDraw = marginalExpectedDraws >
                            AtlasValueGateExpectedDrawEpsilon
                        ? candidate.ExpectedResidentGeneratedGeometryBytes / marginalExpectedDraws
                        : double.PositiveInfinity,
                };
                acceptedEntries.Add(effectiveCandidate);
                selectedOccurrencesByPayload[payloadId] = occurrences.ToList();
                foreach (var occurrence in occurrences)
                {
                    selectedRewriteConflictKeys.UnionWith(occurrence.RewriteConflictKeys);
                    foreach (var context in occurrence.JointContextKeyByParentLocation)
                        selectedJointContexts[context.Key] = context.Value;
                    if (string.IsNullOrWhiteSpace(occurrence.JointCombinationKey))
                        continue;
                    if (!jointCombinationKeysByOccurrence.TryGetValue(
                            occurrence.RewriteOccurrenceKey,
                            out var combinationKeys))
                    {
                        combinationKeys = new HashSet<string>(StringComparer.Ordinal);
                        jointCombinationKeysByOccurrence[occurrence.RewriteOccurrenceKey] =
                            combinationKeys;
                    }

                    combinationKeys.Add(occurrence.JointCombinationKey);
                }
            }

            return new CrossRigidPortfolioReplayDetails(
                selectedRewriteConflictKeys,
                selectedJointContexts,
                selectedOccurrencesByPayload,
                jointCombinationKeysByOccurrence,
                acceptedEntries.ToArray());
        }

        private static void ApplyCrossRigidPortfolioReplayDetailsToState(
            BatchState state,
            IReadOnlySet<string> selectedPayloadIds,
            CrossRigidPortfolioReplayDetails details,
            IReadOnlyDictionary<string, CrossRigidPayloadRewriteConflictProfile> conflictProfiles)
        {
            state.CrossRigidPayloadSelectionEntries.Clear();
            state.CrossRigidPayloadSelectionEntries.AddRange(details.AcceptedEntries);
            state.CrossRigidSelectedPayloadIds.Clear();
            state.CrossRigidSelectedPayloadIds.UnionWith(selectedPayloadIds);
            state.CrossRigidSelectedRewriteOccurrenceKeys.Clear();
            state.CrossRigidSelectedJointCombinationKeysByRewriteOccurrenceKey.Clear();
            state.CrossRigidSelectedJointCoverageFractionByRewriteOccurrenceKey.Clear();
            foreach (var occurrences in details.SelectedOccurrencesByPayload.Values)
            {
                foreach (var occurrence in occurrences)
                    state.CrossRigidSelectedRewriteOccurrenceKeys.Add(
                        occurrence.RewriteOccurrenceKey);
            }

            foreach (var (rewriteOccurrenceKey, combinationKeys) in
                     details.SelectedJointCombinationKeysByRewriteOccurrenceKey)
            {
                state.CrossRigidSelectedJointCombinationKeysByRewriteOccurrenceKey[
                    rewriteOccurrenceKey] = new HashSet<string>(
                        combinationKeys,
                        StringComparer.Ordinal);
            }

            foreach (var payloadId in selectedPayloadIds)
            {
                if (conflictProfiles.TryGetValue(payloadId, out var profile))
                    UpdateCrossRigidSelectedJointCombinationCoverage(state, profile);
            }
        }

        private static void ResetCrossRigidSharedPayloadFallbackState(BatchState state)
        {
            state.CrossRigidSharedPayloadFallbackCandidatePlans = 0;
            state.CrossRigidSharedPayloadFallbackCompatibleStates = 0;
            state.CrossRigidSharedPayloadFallbackAcceptedStates = 0;
            state.CrossRigidSharedPayloadFallbackExpectedDraws = 0;
            state.CrossRigidSharedPayloadFallbackResidentBytes = 0;
            state.CrossRigidSharedPayloadFallbackBudgetRejections = 0;
            state.CrossRigidSharedPayloadFallbackAdmittedPlanIds.Clear();
        }

        private static CrossRigidDrawCallPortfolioStateSnapshot
            CaptureCrossRigidDrawCallPortfolioState(BatchState state)
        {
            var jointCombinationKeys = new Dictionary<string, HashSet<string>>(
                StringComparer.Ordinal);
            foreach (var (rewriteOccurrenceKey, combinationKeys) in
                     state.CrossRigidSelectedJointCombinationKeysByRewriteOccurrenceKey)
            {
                jointCombinationKeys[rewriteOccurrenceKey] = new HashSet<string>(
                    combinationKeys,
                    StringComparer.Ordinal);
            }

            return new CrossRigidDrawCallPortfolioStateSnapshot(
                state.CrossRigidPayloadSelectionEntries.ToArray(),
                state.CrossRigidSelectedPayloadIds.ToHashSet(StringComparer.Ordinal),
                state.CrossRigidSelectedRewriteOccurrenceKeys.ToHashSet(StringComparer.Ordinal),
                jointCombinationKeys,
                new Dictionary<string, double>(
                    state.CrossRigidSelectedJointCoverageFractionByRewriteOccurrenceKey,
                    StringComparer.Ordinal),
                state.CrossRigidSharedPayloadFallbackCandidatePlans,
                state.CrossRigidSharedPayloadFallbackCompatibleStates,
                state.CrossRigidSharedPayloadFallbackAcceptedStates,
                state.CrossRigidSharedPayloadFallbackExpectedDraws,
                state.CrossRigidSharedPayloadFallbackResidentBytes,
                state.CrossRigidSharedPayloadFallbackBudgetRejections,
                state.CrossRigidSharedPayloadFallbackAdmittedPlanIds
                    .ToHashSet(StringComparer.Ordinal));
        }

        private static void RestoreCrossRigidDrawCallPortfolioState(
            BatchState state,
            CrossRigidDrawCallPortfolioStateSnapshot snapshot)
        {
            state.CrossRigidPayloadSelectionEntries.Clear();
            state.CrossRigidPayloadSelectionEntries.AddRange(snapshot.PayloadSelectionEntries);
            state.CrossRigidSelectedPayloadIds.Clear();
            state.CrossRigidSelectedPayloadIds.UnionWith(snapshot.SelectedPayloadIds);
            state.CrossRigidSelectedRewriteOccurrenceKeys.Clear();
            state.CrossRigidSelectedRewriteOccurrenceKeys.UnionWith(
                snapshot.SelectedRewriteOccurrenceKeys);
            state.CrossRigidSelectedJointCombinationKeysByRewriteOccurrenceKey.Clear();
            foreach (var (rewriteOccurrenceKey, combinationKeys) in
                     snapshot.SelectedJointCombinationKeys)
            {
                state.CrossRigidSelectedJointCombinationKeysByRewriteOccurrenceKey[
                    rewriteOccurrenceKey] = new HashSet<string>(
                        combinationKeys,
                        StringComparer.Ordinal);
            }

            state.CrossRigidSelectedJointCoverageFractionByRewriteOccurrenceKey.Clear();
            foreach (var (rewriteOccurrenceKey, coverageFraction) in
                     snapshot.SelectedJointCoverageFractions)
            {
                state.CrossRigidSelectedJointCoverageFractionByRewriteOccurrenceKey[
                    rewriteOccurrenceKey] = coverageFraction;
            }

            state.CrossRigidSharedPayloadFallbackCandidatePlans =
                snapshot.SharedFallbackCandidatePlans;
            state.CrossRigidSharedPayloadFallbackCompatibleStates =
                snapshot.SharedFallbackCompatibleStates;
            state.CrossRigidSharedPayloadFallbackAcceptedStates =
                snapshot.SharedFallbackAcceptedStates;
            state.CrossRigidSharedPayloadFallbackExpectedDraws =
                snapshot.SharedFallbackExpectedDraws;
            state.CrossRigidSharedPayloadFallbackResidentBytes =
                snapshot.SharedFallbackResidentBytes;
            state.CrossRigidSharedPayloadFallbackBudgetRejections =
                snapshot.SharedFallbackBudgetRejections;
            state.CrossRigidSharedPayloadFallbackAdmittedPlanIds.Clear();
            state.CrossRigidSharedPayloadFallbackAdmittedPlanIds.UnionWith(
                snapshot.SharedFallbackAdmittedPlanIds);
        }

        private static void ApplyCrossRigidPortfolioTrial(
            BatchState state,
            CrossRigidDrawCallPortfolioTrial trial,
            IReadOnlySet<string> incumbentPayloadIds)
        {
            var previousEntries = state.CrossRigidPayloadSelectionEntries.ToArray();
            var acceptedById = trial.State.PayloadSelectionEntries
                .Where(entry => entry.Accepted)
                .GroupBy(entry => entry.GeneratedPayloadId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
            RestoreCrossRigidDrawCallPortfolioState(state, trial.State);

            var updatedEntries = new List<CrossRigidPayloadSelectionEntry>();
            var writtenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in previousEntries)
            {
                if (acceptedById.TryGetValue(entry.GeneratedPayloadId, out var accepted))
                {
                    if (writtenIds.Add(entry.GeneratedPayloadId))
                        updatedEntries.Add(accepted);
                    continue;
                }

                if (entry.Accepted && incumbentPayloadIds.Contains(entry.GeneratedPayloadId))
                {
                    updatedEntries.Add(entry with
                    {
                        Decision = CrossRigidPayloadSelectionDecision.RejectedPortfolioSearch,
                        MarginalExpectedArmyDrawCallsEliminated = 0,
                        PhysicalBytesPerExpectedDraw = double.PositiveInfinity,
                        ResidentBytesPerExpectedDraw = double.PositiveInfinity,
                    });
                }
                else
                {
                    updatedEntries.Add(entry);
                }
            }

            foreach (var accepted in acceptedById.Values
                         .OrderBy(entry => entry.SelectionRank)
                         .ThenBy(entry => entry.GeneratedPayloadId, StringComparer.Ordinal))
            {
                if (writtenIds.Add(accepted.GeneratedPayloadId))
                    updatedEntries.Add(accepted);
            }

            state.CrossRigidPayloadSelectionEntries.Clear();
            state.CrossRigidPayloadSelectionEntries.AddRange(updatedEntries);
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
            CrossRigidExpectedDrawCallTerms drawCallTerms,
            RetirementCoverage? selectedCoverage = null)
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

            var coverage = selectedCoverage ??
                BuildCrossRigidDrawCallCoverage(context, selectedPayloadIds);
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
            RetirementContext context,
            IReadOnlyList<(string RigidPath, double ExpectedMeshParts)>? sourcesOverride = null)
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

            var sources = sourcesOverride ?? context.PlansByPayload.Values
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

        private static RetirementCoverage
            BuildCrossRigidDrawCallCoverageFromSelectedOccurrences(
                RetirementContext context,
                IReadOnlySet<string> selectedPayloadIds)
        {
            var state = context.State;
            var coverage = new Dictionary<CrossRigidWsModelOccurrenceKey, double>();
            var vmdsWithPlans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var payloadId in selectedPayloadIds)
            {
                if (!context.PlansByPayload.TryGetValue(payloadId, out var plans))
                    continue;

                foreach (var plan in plans)
                {
                    foreach (var set in plan.RewriteOccurrenceSets)
                    {
                        var occurrenceKey = BuildCrossRigidSelectedRewriteOccurrenceKey(plan, set);
                        if (!state.CrossRigidSelectedRewriteOccurrenceKeys.Contains(occurrenceKey))
                            continue;

                        var jointCoverageFraction = state
                            .CrossRigidSelectedJointCoverageFractionByRewriteOccurrenceKey
                            .GetValueOrDefault(
                                occurrenceKey,
                                state.CrossRigidSelectedJointCombinationKeysByRewriteOccurrenceKey
                                    .ContainsKey(occurrenceKey)
                                    ? 0
                                    : 1);
                        if (jointCoverageFraction <= 0)
                            continue;

                        var contribution = set.Probability * jointCoverageFraction;
                        foreach (var instance in set.SourceInstances.Distinct())
                        {
                            var key = new CrossRigidWsModelOccurrenceKey(
                                plan.VmdPath,
                                instance.AttachmentIdentity,
                                instance.WsModelPath);
                            coverage[key] = Math.Min(
                                1.0,
                                coverage.GetValueOrDefault(key) + contribution);
                            vmdsWithPlans.Add(plan.VmdPath);
                        }
                    }
                }
            }

            return new RetirementCoverage
            {
                Name = "DRAW CALL GATE PORTFOLIO TRIAL",
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
            var portfolioSearch = report.PortfolioSearch;
            var trialsByRound = portfolioSearch.TrialsByRound.Length == 0
                ? "(none)"
                : string.Join(", ", portfolioSearch.TrialsByRound.Select(trials =>
                    trials.ToString("N0", CultureInfo.InvariantCulture)));
            sb.AppendLine(
                $"Post-gate portfolio search: incumbent objective={FormatExpectedDrawCallNumber(portfolioSearch.IncumbentObjective)}, " +
                $"best objective={FormatExpectedDrawCallNumber(portfolioSearch.BestObjective)}, " +
                $"modeled Δ={FormatExpectedDrawCallNumber(portfolioSearch.IncumbentDelta)} -> " +
                $"{FormatExpectedDrawCallNumber(portfolioSearch.BestDelta)}; " +
                $"trials={portfolioSearch.Trials:N0}/{portfolioSearch.TrialBudget:N0} (global), " +
                $"per-round budget={portfolioSearch.PerRoundTrialBudget:N0}, " +
                $"rounds={portfolioSearch.Rounds:N0}, " +
                $"improvements={portfolioSearch.Improvements:N0}, " +
                $"trials by round=[{trialsByRound}], " +
                $"budget exhausted={(portfolioSearch.BudgetExhausted ? "yes" : "no")}, " +
                $"unselected candidates={portfolioSearch.CandidateCount:N0}, " +
                $"source-rigid cohorts tried={portfolioSearch.CohortTrials:N0}, " +
                $"alternative cohort trials={portfolioSearch.AlternativeCohortTrials:N0}");
            if (portfolioSearch.AddedPayloadIds.Length != 0 ||
                portfolioSearch.RemovedPayloadIds.Length != 0)
            {
                sb.AppendLine(
                    $"Portfolio search changes: added={portfolioSearch.AddedPayloadIds.Length:N0}, " +
                    $"removed={portfolioSearch.RemovedPayloadIds.Length:N0}");
            }
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
