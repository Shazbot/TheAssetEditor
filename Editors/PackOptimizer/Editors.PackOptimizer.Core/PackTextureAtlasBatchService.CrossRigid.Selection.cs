using System.Runtime.CompilerServices;
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
        private static void SelectCrossRigidGeneratedPayloads(
            BatchState state)
        {
            state.CrossRigidPayloadSelectionEntries.Clear();
            state.CrossRigidSelectedPayloadIds.Clear();
            state.CrossRigidSelectedRewriteOccurrenceKeys.Clear();
            state.CrossRigidSelectedJointCombinationKeysByRewriteOccurrenceKey.Clear();
            state.CrossRigidRewriteConflictReasonCounts.Clear();
            state.CrossRigidRewriteConflictExpectedDraws.Clear();
            state.CrossRigidRewriteConflictExamples.Clear();
            state.CrossRigidCanonicalJointDescriptorByParentLocation.Clear();
            state.CrossRigidCanonicalJointParentCount = 0;
            state.CrossRigidCanonicalJointContextsFolded = 0;
            state.CrossRigidCanonicalJointParentConflictCount = 0;
            state.CrossRigidPortfolioOrderingsEvaluated = 0;
            state.CrossRigidRawGreedyExpectedDraws = 0;
            state.CrossRigidBestPortfolioExpectedDraws = 0;
            state.CrossRigidPortfolioPairTrials = 0;
            state.CrossRigidPortfolioContextCohortTrials = 0;
            state.CrossRigidPortfolioLocalInsertionTrials = 0;
            state.CrossRigidPortfolioLocalInsertionImprovements = 0;
            state.CrossRigidPortfolioLocalPairInsertionTrials = 0;
            state.CrossRigidPortfolioLocalPairInsertionImprovements = 0;
            state.CrossRigidSharedPayloadFallbackCandidatePlans = 0;
            state.CrossRigidSharedPayloadFallbackCompatibleStates = 0;
            state.CrossRigidSharedPayloadFallbackAcceptedStates = 0;
            state.CrossRigidSharedPayloadFallbackExpectedDraws = 0;
            state.CrossRigidSharedPayloadFallbackResidentBytes = 0;
            state.CrossRigidSharedPayloadFallbackBudgetRejections = 0;
            state.CrossRigidSharedPayloadFallbackAdmittedPlanIds.Clear();

            var plansByPayload =
                state.CrossRigidMergeAnalysisEntries
                    .GroupBy(
                        entry => entry.GeneratedPayloadId,
                        StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.ToArray(),
                        StringComparer.Ordinal);

            // Supplemental pairwise plans may reuse geometry from an older
            // full-group payload. Scoring by the entire shared payload would
            // quietly introduce those pairs into the protected baseline.
            // Use only originally eligible *rewrite plans* for primary
            // benefit, VMD coverage and residency; full-payload analysis
            // remains available for diagnostics and fallback-only payloads.
            var primaryPlansByPayload = plansByPayload
                .ToDictionary(
                    group => group.Key,
                    group => group.Value
                        .Where(plan =>
                            !state.CrossRigidPreviouslySuppressedPairFallbackPlanIds
                                .Contains(plan.RewritePlanId))
                        .ToArray(),
                    StringComparer.Ordinal);

            var selectionCandidates =
                new List<CrossRigidPayloadSelectionEntry>();

            foreach (var payload in
                     state.CrossRigidGeneratedPayloadAnalysisEntries)
            {
                var primaryPlans =
                    primaryPlansByPayload[payload.GeneratedPayloadId];
                var allPlans =
                    plansByPayload[payload.GeneratedPayloadId];
                var isPrimary = primaryPlans.Length != 0;
                if (isPrimary &&
                    primaryPlans.Sum(plan =>
                        plan.ExpectedArmyDrawCallsEliminated) <=
                    AtlasValueGateExpectedDrawEpsilon &&
                    allPlans.Sum(plan =>
                        plan.ExpectedArmyDrawCallsEliminated) >
                    AtlasValueGateExpectedDrawEpsilon)
                {
                    // The primary plans carry no benefit; only suppressed-pair
                    // fallback plans do. Treat the payload as fallback-only
                    // everywhere: downstream stages pick the primary or
                    // supplementary path from the legacy-eligible set.
                    isPrimary = false;
                    state.CrossRigidLegacyEligiblePayloadIds.Remove(
                        payload.GeneratedPayloadId);
                }

                var relevantPlans = isPrimary
                    ? primaryPlans
                    : allPlans;
                var expectedDraws = relevantPlans.Sum(plan =>
                    plan.ExpectedArmyDrawCallsEliminated);
                var generatedGeometryBytes =
                    (double)payload.GeneratedGeometryBytes;
                var expectedResidentBytes = isPrimary
                    ? generatedGeometryBytes *
                      CalculateExpectedCrossRigidPayloadResidentProbability(
                          state,
                          primaryPlans)
                    : payload.ExpectedResidentGeneratedGeometryBytes;
                var relevantVmdCount = isPrimary
                    ? primaryPlans.Select(plan => plan.VmdPath)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count()
                    : payload.VmdCount;
                var physicalBytesPerExpectedDraw =
                    expectedDraws > AtlasValueGateExpectedDrawEpsilon
                        ? generatedGeometryBytes / expectedDraws
                        : double.PositiveInfinity;
                var residentBytesPerExpectedDraw =
                    expectedDraws > AtlasValueGateExpectedDrawEpsilon
                        ? expectedResidentBytes / expectedDraws
                        : double.PositiveInfinity;

                var decision =
                    CrossRigidPayloadSelectionDecision.Accept;
                if (expectedDraws <= AtlasValueGateExpectedDrawEpsilon)
                {
                    decision =
                        CrossRigidPayloadSelectionDecision
                            .ScenarioResolvedZeroBenefit;
                }
                else if (relevantVmdCount == 1 &&
                         expectedDraws <
                         MinimumCrossRigidSingleVmdExpectedArmyDraw)
                {
                    decision =
                        CrossRigidPayloadSelectionDecision
                            .SingleVmdBenefitTooSmall;
                }

                var maxRawDrawsSavedPerState =
                    payload.Lods
                        .Where(lod =>
                            lod.IsScenarioRepresentativeState)
                        .Select(lod =>
                            lod.DrawsSaved)
                        .DefaultIfEmpty(0)
                        .Max();
                var entry = new CrossRigidPayloadSelectionEntry(
                    payload.GeneratedPayloadId,
                    decision,
                    expectedDraws,
                    expectedDraws,
                    maxRawDrawsSavedPerState,
                    relevantPlans.Length,
                    relevantVmdCount,
                    payload.GeneratedGeometryBytes,
                    expectedResidentBytes,
                    physicalBytesPerExpectedDraw,
                    residentBytesPerExpectedDraw,
                    0);

                if (decision ==
                    CrossRigidPayloadSelectionDecision.Accept)
                {
                    selectionCandidates.Add(entry);
                }
                else
                {
                    state.CrossRigidPayloadSelectionEntries.Add(entry);
                }
            }

            var orderedCandidates =
                selectionCandidates
                    // Optimize the expected benefit of the competing rewrite
                    // portfolio, not the largest single rendered-state reduction.
                    // A large raw-draw merge may occupy the same Cartesian states
                    // as several cheaper, more valuable merges. Normalize physical
                    // and resident payload costs by their respective budgets before
                    // comparing the benefit of mutually competing payloads.
                    .OrderByDescending(entry =>
                        entry.ExpectedArmyDrawCallsEliminated /
                        Math.Max(
                            1.0,
                            Math.Max(
                                (double)entry.GeneratedGeometryBytes /
                                MaxCrossRigidGeneratedGeometryBytesPerExpectedArmyDraw,
                                entry.ExpectedResidentGeneratedGeometryBytes /
                                MaxCrossRigidExpectedResidentGeometryBytesPerExpectedArmyDraw)))
                    .ThenByDescending(entry =>
                        entry.ExpectedArmyDrawCallsEliminated)
                    .ThenBy(entry =>
                        Math.Max(
                            entry.ResidentBytesPerExpectedDraw /
                            MaxCrossRigidExpectedResidentGeometryBytesPerExpectedArmyDraw,
                            entry.PhysicalBytesPerExpectedDraw /
                            MaxCrossRigidGeneratedGeometryBytesPerExpectedArmyDraw))
                    .ThenByDescending(entry =>
                        entry.MaxRawDrawsSavedPerState)
                    .ThenByDescending(entry =>
                        entry.RewritePlanCount)
                    .ThenBy(
                        entry => entry.GeneratedPayloadId,
                        StringComparer.Ordinal)
                    .Select((entry, index) =>
                        entry with
                        {
                            SelectionRank = index + 1,
                        })
                    .ToList();

            var conflictProfileVmdDocuments =
                new Dictionary<string, XmlDocument>(
                    StringComparer.OrdinalIgnoreCase);
            BuildCrossRigidCanonicalJointContexts(
                state,
                conflictProfileVmdDocuments);
            var conflictProfiles =
                new Dictionary<
                    string,
                    CrossRigidPayloadRewriteConflictProfile>(
                    StringComparer.Ordinal);
            var selectedRewriteConflictKeys =
                new HashSet<string>(StringComparer.Ordinal);
            var selectedRewriteConflictOwnerByKey =
                new Dictionary<
                    string,
                    CrossRigidSelectedRewriteConflictOwner>(
                    StringComparer.Ordinal);
            var selectedJointContextKeyByParentLocation =
                new Dictionary<string, string>(
                    StringComparer.Ordinal);
            var selectedJointContextOwnerByParentLocation =
                new Dictionary<
                    string,
                    CrossRigidSelectedRewriteConflictOwner>(
                    StringComparer.Ordinal);

            // Compare whole portfolios before committing any rewrites. Each
            // candidate is replayed through the same exact-occurrence and
            // nested-context conflict rules used by the final writer.
            var portfolioSearchTimer = Stopwatch.StartNew();
            orderedCandidates =
                OptimizeCrossRigidPayloadCandidateOrdering(
                    state,
                    orderedCandidates,
                    conflictProfiles,
                    conflictProfileVmdDocuments);
            state.CrossRigidTimingDetails["Analyze: portfolio ordering search"] =
                portfolioSearchTimer.Elapsed;

            var acceptedGeneratedGeometryBytes = 0L;
            var acceptedExpectedResidentGeometryBytes = 0.0;
            var acceptedExpectedDraws = 0.0;
            var pendingCandidates =
                new List<CrossRigidPayloadSelectionEntry>(
                    orderedCandidates);
            var deferredBudgetDecisionByPayload =
                new Dictionary<
                    string,
                    CrossRigidPayloadSelectionDecision>(
                    StringComparer.Ordinal);
            var admittingSupplementaryPairs = false;

            while (pendingCandidates.Count != 0)
            {
                var acceptedThisPass = false;

                for (var candidateIndex = 0;
                     candidateIndex < pendingCandidates.Count;)
                {
                    var candidate =
                        pendingCandidates[candidateIndex];

                    // Retry all budget-deferred primary candidates before
                    // any supplementary pair can occupy their structural
                    // states. The fallback stage begins only after the
                    // protected primary portfolio stops making progress.
                    if (!admittingSupplementaryPairs &&
                        !state.CrossRigidLegacyEligiblePayloadIds.Contains(
                            candidate.GeneratedPayloadId))
                    {
                        candidateIndex++;
                        continue;
                    }

                    if (!conflictProfiles.TryGetValue(
                            candidate.GeneratedPayloadId,
                            out var conflictProfile))
                    {
                        if (!TryBuildCrossRigidPayloadRewriteConflictProfile(
                                state,
                                candidate.GeneratedPayloadId,
                                conflictProfileVmdDocuments,
                                !state.CrossRigidLegacyEligiblePayloadIds.Contains(
                                    candidate.GeneratedPayloadId),
                                out conflictProfile,
                                out var conflictBuildReason))
                        {
                            RecordCrossRigidRewriteConflict(
                                state,
                                "Conflict profile could not be built",
                                candidate,
                                string.IsNullOrWhiteSpace(
                                    conflictBuildReason)
                                    ? $"payload={candidate.GeneratedPayloadId}"
                                    : $"payload={candidate.GeneratedPayloadId}: " +
                                      conflictBuildReason);
                            state.CrossRigidPayloadSelectionEntries.Add(
                                candidate with
                                {
                                    Decision =
                                        CrossRigidPayloadSelectionDecision
                                            .RewriteOccurrenceConflict,
                                    MarginalExpectedArmyDrawCallsEliminated =
                                        0,
                                    PhysicalBytesPerExpectedDraw =
                                        double.PositiveInfinity,
                                    ResidentBytesPerExpectedDraw =
                                        double.PositiveInfinity,
                                });
                            pendingCandidates.RemoveAt(
                                candidateIndex);
                            deferredBudgetDecisionByPayload.Remove(
                                candidate.GeneratedPayloadId);
                            continue;
                        }

                        conflictProfiles[
                            candidate.GeneratedPayloadId] =
                            conflictProfile;
                    }

                    var compatibleOccurrences =
                        SelectCrossRigidCompatibleRewriteOccurrences(
                            conflictProfile,
                            selectedRewriteConflictKeys,
                            selectedJointContextKeyByParentLocation);
                    var marginalExpectedDraws =
                        compatibleOccurrences.Sum(occurrence =>
                            occurrence.ExpectedArmyDrawCallsEliminated);
                    var effectiveCandidate =
                        candidate with
                        {
                            MarginalExpectedArmyDrawCallsEliminated =
                                marginalExpectedDraws,
                            PhysicalBytesPerExpectedDraw =
                                marginalExpectedDraws >
                                AtlasValueGateExpectedDrawEpsilon
                                    ? candidate.GeneratedGeometryBytes /
                                      marginalExpectedDraws
                                    : double.PositiveInfinity,
                            ResidentBytesPerExpectedDraw =
                                marginalExpectedDraws >
                                AtlasValueGateExpectedDrawEpsilon
                                    ? candidate
                                          .ExpectedResidentGeneratedGeometryBytes /
                                      marginalExpectedDraws
                                    : double.PositiveInfinity,
                        };

                    if (marginalExpectedDraws <=
                        AtlasValueGateExpectedDrawEpsilon)
                    {
                        var (conflictReason, conflictDetail) =
                            ClassifyCrossRigidRewriteOccurrenceConflict(
                                conflictProfile,
                                selectedRewriteConflictKeys,
                                selectedRewriteConflictOwnerByKey,
                                selectedJointContextKeyByParentLocation,
                                selectedJointContextOwnerByParentLocation);
                        RecordCrossRigidRewriteConflict(
                            state,
                            conflictReason,
                            effectiveCandidate,
                            $"payload={candidate.GeneratedPayloadId}: " +
                            conflictDetail);
                        state.CrossRigidPayloadSelectionEntries.Add(
                            effectiveCandidate with
                            {
                                Decision =
                                    CrossRigidPayloadSelectionDecision
                                        .RewriteOccurrenceConflict,
                            });
                        pendingCandidates.RemoveAt(
                            candidateIndex);
                        deferredBudgetDecisionByPayload.Remove(
                            candidate.GeneratedPayloadId);
                        continue;
                    }

                    if (candidate.VmdCount == 1 &&
                        marginalExpectedDraws <
                        MinimumCrossRigidSingleVmdExpectedArmyDraw)
                    {
                        state.CrossRigidPayloadSelectionEntries.Add(
                            effectiveCandidate with
                            {
                                Decision =
                                    CrossRigidPayloadSelectionDecision
                                        .SingleVmdBenefitTooSmall,
                            });
                        pendingCandidates.RemoveAt(
                            candidateIndex);
                        deferredBudgetDecisionByPayload.Remove(
                            candidate.GeneratedPayloadId);
                        continue;
                    }

                    // Marginal benefit only shrinks as more payloads are
                    // selected, so this per-payload rejection is final.
                    var ceilingDecision =
                        GetCrossRigidPerPayloadCostCeilingDecision(
                            candidate,
                            marginalExpectedDraws);
                    if (ceilingDecision != null)
                    {
                        state.CrossRigidPayloadSelectionEntries.Add(
                            effectiveCandidate with
                            {
                                Decision = ceilingDecision.Value,
                            });
                        pendingCandidates.RemoveAt(
                            candidateIndex);
                        deferredBudgetDecisionByPayload.Remove(
                            candidate.GeneratedPayloadId);
                        continue;
                    }

                    var projectedExpectedDraws =
                        acceptedExpectedDraws +
                        marginalExpectedDraws;
                    var projectedExpectedResidentGeometryBytes =
                        acceptedExpectedResidentGeometryBytes +
                        candidate.ExpectedResidentGeneratedGeometryBytes;
                    if (projectedExpectedResidentGeometryBytes >
                        projectedExpectedDraws *
                        MaxCrossRigidExpectedResidentGeometryBytesPerExpectedArmyDraw)
                    {
                        deferredBudgetDecisionByPayload[
                            candidate.GeneratedPayloadId] =
                            CrossRigidPayloadSelectionDecision
                                .ScenarioResidencyBudgetExceeded;
                        candidateIndex++;
                        continue;
                    }

                    var projectedGeneratedGeometryBytes =
                        acceptedGeneratedGeometryBytes +
                        candidate.GeneratedGeometryBytes;
                    if (projectedGeneratedGeometryBytes >
                        projectedExpectedDraws *
                        MaxCrossRigidGeneratedGeometryBytesPerExpectedArmyDraw)
                    {
                        deferredBudgetDecisionByPayload[
                            candidate.GeneratedPayloadId] =
                            CrossRigidPayloadSelectionDecision
                                .PhysicalPerDrawBudgetExceeded;
                        candidateIndex++;
                        continue;
                    }

                    if (projectedGeneratedGeometryBytes >
                        MaxCrossRigidGeneratedGeometryGrowthBytes)
                    {
                        state.CrossRigidPayloadSelectionEntries.Add(
                            effectiveCandidate with
                            {
                                Decision =
                                    CrossRigidPayloadSelectionDecision
                                        .GlobalGrowthCapExceeded,
                            });
                        pendingCandidates.RemoveAt(
                            candidateIndex);
                        deferredBudgetDecisionByPayload.Remove(
                            candidate.GeneratedPayloadId);
                        continue;
                    }

                    acceptedGeneratedGeometryBytes =
                        projectedGeneratedGeometryBytes;
                    acceptedExpectedResidentGeometryBytes =
                        projectedExpectedResidentGeometryBytes;
                    acceptedExpectedDraws =
                        projectedExpectedDraws;

                    foreach (var occurrence in
                             compatibleOccurrences)
                    {
                        var conflictOwner =
                            new CrossRigidSelectedRewriteConflictOwner(
                                candidate.GeneratedPayloadId,
                                occurrence.SelectionKey,
                                occurrence.ExpectedArmyDrawCallsEliminated,
                                candidate.MaxRawDrawsSavedPerState,
                                candidate.GeneratedGeometryBytes,
                                candidate.SelectionRank);
                        foreach (var rewriteConflictKey in
                                 occurrence.RewriteConflictKeys)
                        {
                            selectedRewriteConflictKeys.Add(
                                rewriteConflictKey);
                            selectedRewriteConflictOwnerByKey.TryAdd(
                                rewriteConflictKey,
                                conflictOwner);
                        }

                        foreach (var pair in
                                 occurrence
                                     .JointContextKeyByParentLocation)
                        {
                            selectedJointContextKeyByParentLocation[
                                pair.Key] =
                                pair.Value;
                            selectedJointContextOwnerByParentLocation[
                                pair.Key] =
                                conflictOwner;
                        }

                        state.CrossRigidSelectedRewriteOccurrenceKeys.Add(
                            occurrence.RewriteOccurrenceKey);
                        if (!string.IsNullOrWhiteSpace(
                                occurrence.JointCombinationKey))
                        {
                            if (!state
                                    .CrossRigidSelectedJointCombinationKeysByRewriteOccurrenceKey
                                    .TryGetValue(
                                        occurrence.RewriteOccurrenceKey,
                                        out var selectedCombinationKeys))
                            {
                                selectedCombinationKeys =
                                    new HashSet<string>(
                                        StringComparer.Ordinal);
                                state
                                    .CrossRigidSelectedJointCombinationKeysByRewriteOccurrenceKey[
                                        occurrence.RewriteOccurrenceKey] =
                                    selectedCombinationKeys;
                            }

                            selectedCombinationKeys.Add(
                                occurrence.JointCombinationKey);
                        }
                    }

                    state.CrossRigidSelectedPayloadIds.Add(
                        candidate.GeneratedPayloadId);
                    state.CrossRigidPayloadSelectionEntries.Add(
                        effectiveCandidate with
                        {
                            Decision =
                                CrossRigidPayloadSelectionDecision.Accept,
                        });
                    pendingCandidates.RemoveAt(
                        candidateIndex);
                    deferredBudgetDecisionByPayload.Remove(
                        candidate.GeneratedPayloadId);
                    acceptedThisPass = true;
                }

                if (!acceptedThisPass)
                {
                    if (!admittingSupplementaryPairs)
                    {
                        admittingSupplementaryPairs = true;
                        continue;
                    }

                    break;
                }
            }

            foreach (var candidate in pendingCandidates)
            {
                var marginalExpectedDraws =
                    conflictProfiles.TryGetValue(
                        candidate.GeneratedPayloadId,
                        out var conflictProfile)
                        ? SelectCrossRigidCompatibleRewriteOccurrences(
                                conflictProfile,
                                selectedRewriteConflictKeys,
                                selectedJointContextKeyByParentLocation)
                            .Sum(occurrence =>
                                occurrence.ExpectedArmyDrawCallsEliminated)
                        : candidate
                            .MarginalExpectedArmyDrawCallsEliminated;
                state.CrossRigidPayloadSelectionEntries.Add(
                    candidate with
                    {
                        Decision =
                            deferredBudgetDecisionByPayload.GetValueOrDefault(
                                candidate.GeneratedPayloadId,
                                CrossRigidPayloadSelectionDecision
                                    .ScenarioResidencyBudgetExceeded),
                        MarginalExpectedArmyDrawCallsEliminated =
                            marginalExpectedDraws,
                        PhysicalBytesPerExpectedDraw =
                            marginalExpectedDraws >
                            AtlasValueGateExpectedDrawEpsilon
                                ? candidate.GeneratedGeometryBytes /
                                  marginalExpectedDraws
                                : double.PositiveInfinity,
                        ResidentBytesPerExpectedDraw =
                            marginalExpectedDraws >
                            AtlasValueGateExpectedDrawEpsilon
                                ? candidate
                                      .ExpectedResidentGeneratedGeometryBytes /
                                  marginalExpectedDraws
                                : double.PositiveInfinity,
                    });
            }

            // Reuse selected primary geometry only after both primary and
            // normal fallback-only payload portfolios have been finalized.
            AdmitCrossRigidSharedPayloadFallbacks(
                state,
                primaryPlansByPayload,
                plansByPayload,
                selectedRewriteConflictKeys,
                selectedRewriteConflictOwnerByKey,
                selectedJointContextKeyByParentLocation,
                selectedJointContextOwnerByParentLocation,
                conflictProfileVmdDocuments,
                ref acceptedExpectedDraws,
                ref acceptedExpectedResidentGeometryBytes);
        }

        private static void AdmitCrossRigidSharedPayloadFallbacks(
            BatchState state,
            IReadOnlyDictionary<string, CrossRigidMergeAnalysisEntry[]>
                primaryPlansByPayload,
            IReadOnlyDictionary<string, CrossRigidMergeAnalysisEntry[]>
                plansByPayload,
            HashSet<string> selectedRewriteConflictKeys,
            Dictionary<string, CrossRigidSelectedRewriteConflictOwner>
                selectedRewriteConflictOwnerByKey,
            Dictionary<string, string>
                selectedJointContextKeyByParentLocation,
            Dictionary<string, CrossRigidSelectedRewriteConflictOwner>
                selectedJointContextOwnerByParentLocation,
            Dictionary<string, XmlDocument> sourceVmdDocuments,
            ref double acceptedExpectedDraws,
            ref double acceptedExpectedResidentGeometryBytes)
        {
            // A payload with no accepted primary still has to pay for its
            // geometry through the existing fallback-only selection stage.
            // This pass never adds a new payload or evicts a selected rewrite.
            var selectedPrimaryPayloads =
                state.CrossRigidPayloadSelectionEntries
                    .Where(entry =>
                        entry.Accepted &&
                        state.CrossRigidLegacyEligiblePayloadIds.Contains(
                            entry.GeneratedPayloadId))
                    .OrderBy(entry => entry.SelectionRank)
                    .ToArray();

            foreach (var primary in selectedPrimaryPayloads)
            {
                var payloadId = primary.GeneratedPayloadId;
                var fallbackPlans = plansByPayload[payloadId]
                    .Where(plan =>
                        state.CrossRigidPreviouslySuppressedPairFallbackPlanIds
                            .Contains(plan.RewritePlanId))
                    .ToArray();
                if (fallbackPlans.Length == 0)
                    continue;

                state.CrossRigidSharedPayloadFallbackCandidatePlans +=
                    fallbackPlans.Length;
                if (!TryBuildCrossRigidPayloadRewriteConflictProfile(
                        state,
                        payloadId,
                        sourceVmdDocuments,
                        true,
                        out var fallbackProfile,
                        out var profileReason,
                        onlySupplementaryPairFallbackPlans: true))
                {
                    RecordCrossRigidAnalysisDiagnostic(
                        state,
                        "Shared-payload fallback profile unavailable",
                        $"payload={payloadId}: {profileReason}");
                    continue;
                }

                // Exact occurrences, structural slots, and canonical joint
                // contexts must all remain compatible with existing winners.
                var compatible = SelectCrossRigidCompatibleRewriteOccurrences(
                    fallbackProfile,
                    selectedRewriteConflictKeys,
                    selectedJointContextKeyByParentLocation);
                state.CrossRigidSharedPayloadFallbackCompatibleStates +=
                    compatible.Length;
                var marginalDraws = compatible.Sum(occurrence =>
                    occurrence.ExpectedArmyDrawCallsEliminated);
                if (marginalDraws <= AtlasValueGateExpectedDrawEpsilon)
                    continue;

                var selectedOccurrenceKeys = compatible
                    .Select(occurrence => occurrence.RewriteOccurrenceKey)
                    .ToHashSet(StringComparer.Ordinal);
                var admittedPlans = fallbackPlans
                    .Where(plan =>
                        plan.RewriteOccurrenceSets.Any(set =>
                            selectedOccurrenceKeys.Contains(
                                BuildCrossRigidSelectedRewriteOccurrenceKey(
                                    plan, set))))
                    .ToArray();
                if (admittedPlans.Length == 0)
                    continue;

                // Generated physical bytes were paid by the primary. Reaching
                // more units can still make that geometry resident more often;
                // charge the conservative upper-bound residency increment.
                var primaryPlans = primaryPlansByPayload[payloadId];
                var primaryResidency =
                    CalculateExpectedCrossRigidPayloadResidentProbability(
                        state, primaryPlans);
                var expandedResidency =
                    CalculateExpectedCrossRigidPayloadResidentProbability(
                        state,
                        primaryPlans.Concat(admittedPlans).ToArray());
                var incrementalResidentBytes =
                    primary.GeneratedGeometryBytes *
                    Math.Max(0.0, expandedResidency - primaryResidency);
                var projectedDraws = acceptedExpectedDraws + marginalDraws;
                if (acceptedExpectedResidentGeometryBytes +
                        incrementalResidentBytes >
                    projectedDraws *
                    MaxCrossRigidExpectedResidentGeometryBytesPerExpectedArmyDraw)
                {
                    state.CrossRigidSharedPayloadFallbackBudgetRejections++;
                    continue;
                }

                // Commit the same selection keys the writer and original
                // portfolio use. A joint rewrite may accept only a subset of
                // its Cartesian states, so retain those combination keys.
                foreach (var occurrence in compatible)
                {
                    var owner = new CrossRigidSelectedRewriteConflictOwner(
                        payloadId,
                        occurrence.SelectionKey,
                        occurrence.ExpectedArmyDrawCallsEliminated,
                        primary.MaxRawDrawsSavedPerState,
                        primary.GeneratedGeometryBytes,
                        primary.SelectionRank);
                    foreach (var key in occurrence.RewriteConflictKeys)
                    {
                        selectedRewriteConflictKeys.Add(key);
                        selectedRewriteConflictOwnerByKey.TryAdd(key, owner);
                    }

                    foreach (var context in
                             occurrence.JointContextKeyByParentLocation)
                    {
                        selectedJointContextKeyByParentLocation[context.Key] =
                            context.Value;
                        selectedJointContextOwnerByParentLocation[context.Key] =
                            owner;
                    }

                    state.CrossRigidSelectedRewriteOccurrenceKeys.Add(
                        occurrence.RewriteOccurrenceKey);
                    if (string.IsNullOrWhiteSpace(
                            occurrence.JointCombinationKey))
                    {
                        continue;
                    }

                    if (!state
                            .CrossRigidSelectedJointCombinationKeysByRewriteOccurrenceKey
                            .TryGetValue(
                                occurrence.RewriteOccurrenceKey,
                                out var combinationKeys))
                    {
                        combinationKeys =
                            new HashSet<string>(StringComparer.Ordinal);
                        state
                            .CrossRigidSelectedJointCombinationKeysByRewriteOccurrenceKey[
                                occurrence.RewriteOccurrenceKey] =
                            combinationKeys;
                    }

                    combinationKeys.Add(occurrence.JointCombinationKey);
                }

                acceptedExpectedDraws = projectedDraws;
                acceptedExpectedResidentGeometryBytes +=
                    incrementalResidentBytes;
                state.CrossRigidSharedPayloadFallbackAcceptedStates +=
                    compatible.Length;
                state.CrossRigidSharedPayloadFallbackExpectedDraws +=
                    marginalDraws;
                state.CrossRigidSharedPayloadFallbackResidentBytes +=
                    incrementalResidentBytes;
                foreach (var plan in admittedPlans)
                {
                    state.CrossRigidSharedPayloadFallbackAdmittedPlanIds.Add(
                        plan.RewritePlanId);
                }

                // Update the accepted payload's accounting, not the physical
                // payload count. Small one-VMD additions do not need to meet
                // the standalone minimum when geometry is already selected.
                var updatedMarginal =
                    primary.MarginalExpectedArmyDrawCallsEliminated +
                    marginalDraws;
                var updatedResident =
                    primary.ExpectedResidentGeneratedGeometryBytes +
                    incrementalResidentBytes;
                var acceptedIndex =
                    state.CrossRigidPayloadSelectionEntries.FindIndex(
                        entry =>
                            entry.Accepted &&
                            entry.GeneratedPayloadId.Equals(
                                payloadId, StringComparison.Ordinal));
                state.CrossRigidPayloadSelectionEntries[acceptedIndex] =
                    primary with
                    {
                        ExpectedArmyDrawCallsEliminated =
                            primary.ExpectedArmyDrawCallsEliminated +
                            marginalDraws,
                        MarginalExpectedArmyDrawCallsEliminated =
                            updatedMarginal,
                        RewritePlanCount =
                            primary.RewritePlanCount + admittedPlans.Length,
                        VmdCount = primaryPlans.Concat(admittedPlans)
                            .Select(plan => plan.VmdPath)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count(),
                        ExpectedResidentGeneratedGeometryBytes =
                            updatedResident,
                        PhysicalBytesPerExpectedDraw =
                            primary.GeneratedGeometryBytes / updatedMarginal,
                        ResidentBytesPerExpectedDraw =
                            updatedResident / updatedMarginal,
                    };
            }
        }

        private static List<CrossRigidPayloadSelectionEntry>
            OptimizeCrossRigidPayloadCandidateOrdering(
                BatchState state,
                IReadOnlyList<CrossRigidPayloadSelectionEntry> candidates,
                Dictionary<string, CrossRigidPayloadRewriteConflictProfile>
                    conflictProfiles,
                Dictionary<string, XmlDocument> sourceVmdDocuments)
        {
            // Keep the original full-group portfolio intact before
            // evaluating newly admitted pairs as supplementary rewrites.
            // A small pair's high scenario benefit must not displace many
            // otherwise writable full-group states just by changing order.
            var primaryCandidates = candidates
                .Where(entry =>
                    state.CrossRigidLegacyEligiblePayloadIds.Contains(
                        entry.GeneratedPayloadId))
                .ToArray();
            var fallbackOnlyCandidates = candidates
                .Where(entry =>
                    !state.CrossRigidLegacyEligiblePayloadIds.Contains(
                        entry.GeneratedPayloadId))
                .OrderByDescending(entry =>
                    entry.ExpectedArmyDrawCallsEliminated)
                .ThenBy(entry =>
                    entry.GeneratedPayloadId,
                    StringComparer.Ordinal)
                .ToArray();

            // A generated payload shared with any legacy rewrite remains
            // in the primary portfolio, so its geometry is charged once.
            var rawDrawOrder =
                primaryCandidates
                    .OrderByDescending(entry =>
                        entry.MaxRawDrawsSavedPerState)
                    .ThenBy(entry =>
                        Math.Max(
                            entry.ResidentBytesPerExpectedDraw /
                            MaxCrossRigidExpectedResidentGeometryBytesPerExpectedArmyDraw,
                            entry.PhysicalBytesPerExpectedDraw /
                            MaxCrossRigidGeneratedGeometryBytesPerExpectedArmyDraw))
                    .ThenBy(entry =>
                        entry.ResidentBytesPerExpectedDraw /
                        MaxCrossRigidExpectedResidentGeometryBytesPerExpectedArmyDraw +
                        entry.PhysicalBytesPerExpectedDraw /
                        MaxCrossRigidGeneratedGeometryBytesPerExpectedArmyDraw)
                    .ThenByDescending(entry =>
                        entry.RewritePlanCount)
                    .ThenByDescending(entry =>
                        entry.ExpectedArmyDrawCallsEliminated)
                    .ThenBy(entry =>
                        entry.GeneratedPayloadId,
                        StringComparer.Ordinal)
                    .ToArray();
            var scenarioBenefitOrder =
                primaryCandidates
                    .OrderByDescending(entry =>
                        entry.ExpectedArmyDrawCallsEliminated)
                    .ThenBy(entry =>
                        Math.Max(
                            entry.ResidentBytesPerExpectedDraw /
                            MaxCrossRigidExpectedResidentGeometryBytesPerExpectedArmyDraw,
                            entry.PhysicalBytesPerExpectedDraw /
                            MaxCrossRigidGeneratedGeometryBytesPerExpectedArmyDraw))
                    .ThenBy(entry =>
                        entry.GeneratedPayloadId,
                        StringComparer.Ordinal)
                    .ToArray();

            var invalidProfiles =
                new HashSet<string>(StringComparer.Ordinal);
            var bestOrder = rawDrawOrder;
            var best =
                EvaluateCrossRigidPayloadPortfolio(
                    state,
                    bestOrder,
                    conflictProfiles,
                    invalidProfiles,
                    sourceVmdDocuments);
            var evaluated = 1;
            state.CrossRigidRawGreedyExpectedDraws =
                best.ExpectedDraws;

            foreach (var alternative in
                     new IReadOnlyList<CrossRigidPayloadSelectionEntry>[]
                     {
                         primaryCandidates,
                         scenarioBenefitOrder,
                     })
            {
                var trial =
                    EvaluateCrossRigidPayloadPortfolio(
                        state,
                        alternative,
                        conflictProfiles,
                        invalidProfiles,
                        sourceVmdDocuments);
                evaluated++;
                if (IsBetterCrossRigidPayloadPortfolio(
                        trial,
                        best))
                {
                    best = trial;
                    bestOrder = alternative.ToArray();
                }
            }

            // A rejected payload may replace several overlapping winners.
            // Preserve the best global contenders but include representative
            // alternatives from other VMD roots. Otherwise the seed budget
            // tends to be monopolized by one large Cartesian family.
            const int maxGlobalSeeds = 16;
            const int maxDiverseSeeds = 8;
            var primaryVmdByPayload =
                state.CrossRigidMergeAnalysisEntries
                    .GroupBy(
                        entry => entry.GeneratedPayloadId,
                        StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.First().VmdPath,
                        StringComparer.Ordinal);
            var rejectedCandidates =
                primaryCandidates
                    .Where(entry =>
                        !best.SelectedPayloadIds.Contains(
                            entry.GeneratedPayloadId))
                    .OrderByDescending(entry =>
                        entry.ExpectedArmyDrawCallsEliminated)
                    .ThenBy(entry =>
                        entry.PhysicalBytesPerExpectedDraw)
                    .ThenBy(entry =>
                        entry.GeneratedPayloadId,
                        StringComparer.Ordinal)
                    .ToArray();
            var globalSeeds =
                rejectedCandidates
                    .Take(maxGlobalSeeds)
                    .ToArray();
            var globalSeedIds =
                globalSeeds
                    .Select(entry =>
                        entry.GeneratedPayloadId)
                    .ToHashSet(StringComparer.Ordinal);
            var diverseSeeds =
                rejectedCandidates
                    .GroupBy(
                        entry =>
                            primaryVmdByPayload.TryGetValue(
                                entry.GeneratedPayloadId,
                                out var vmdPath)
                                ? vmdPath
                                : entry.GeneratedPayloadId,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .Where(entry =>
                        !globalSeedIds.Contains(
                            entry.GeneratedPayloadId))
                    .Take(maxDiverseSeeds)
                    .ToArray();
            // A VMD can contain several mutually incompatible joint
            // Cartesian contexts. Sampling only globally expensive payloads
            // and one rejected payload per VMD misses alternatives from a
            // *different structural parent or slot set* in the same VMD.
            // Sample those contexts independently, but always evaluate each
            // ordering against the exact same occurrence conflicts and
            // selected-portfolio geometry/residency budgets.
            var contextSeedObservations =
                new List<(
                    string ParentLocation,
                    string ContextKey,
                    CrossRigidPayloadSelectionEntry Candidate)>();
            foreach (var candidate in rejectedCandidates)
            {
                if (!conflictProfiles.TryGetValue(
                        candidate.GeneratedPayloadId,
                        out var profile))
                {
                    if (!TryBuildCrossRigidPayloadRewriteConflictProfile(
                            state,
                            candidate.GeneratedPayloadId,
                            sourceVmdDocuments,
                            !state.CrossRigidLegacyEligiblePayloadIds.Contains(
                                candidate.GeneratedPayloadId),
                            out profile,
                            out _))
                    {
                        invalidProfiles.Add(candidate.GeneratedPayloadId);
                        continue;
                    }

                    conflictProfiles[candidate.GeneratedPayloadId] =
                        profile;
                }

                foreach (var context in
                         profile.Occurrences
                             .SelectMany(occurrence =>
                                 occurrence.JointContextKeyByParentLocation)
                             .GroupBy(pair =>
                                 pair.Key + "\u001f" + pair.Value,
                                 StringComparer.Ordinal)
                             .Select(group => group.First()))
                {
                    contextSeedObservations.Add(
                        (context.Key, context.Value, candidate));
                }
            }

            // Prefer breadth across distinct parents, then a second
            // competing context under each parent. This specifically
            // exposes slot-set choices such as the independent optional
            // alternatives in teb_roc_estragon that global benefit
            // ranking alone may never try.
            var contextOptionsByParent =
                contextSeedObservations
                    .GroupBy(
                        observation => observation.ParentLocation,
                        StringComparer.Ordinal)
                    .Select(parentGroup =>
                        parentGroup
                            .GroupBy(
                                observation => observation.ContextKey,
                                StringComparer.Ordinal)
                            .Select(contextGroup =>
                                contextGroup
                                    .OrderByDescending(observation =>
                                        observation.Candidate
                                            .ExpectedArmyDrawCallsEliminated)
                                    .ThenBy(observation =>
                                        observation.Candidate.GeneratedPayloadId,
                                        StringComparer.Ordinal)
                                    .First()
                                    .Candidate)
                            .OrderByDescending(candidate =>
                                candidate.ExpectedArmyDrawCallsEliminated)
                            .ThenBy(candidate =>
                                candidate.GeneratedPayloadId,
                                StringComparer.Ordinal)
                            .ToArray())
                    .OrderByDescending(options =>
                        options[0].ExpectedArmyDrawCallsEliminated)
                    .ThenBy(options =>
                        options[0].GeneratedPayloadId,
                        StringComparer.Ordinal)
                    .ToArray();
            const int maxContextSeeds = 24;
            var contextSeedIds =
                globalSeeds.Concat(diverseSeeds)
                    .Select(candidate => candidate.GeneratedPayloadId)
                    .ToHashSet(StringComparer.Ordinal);
            var contextSeeds =
                contextOptionsByParent
                    .Select(options => options[0])
                    .Concat(contextOptionsByParent
                        .Where(options => options.Length > 1)
                        .Select(options => options[1]))
                    .Where(candidate =>
                        contextSeedIds.Add(candidate.GeneratedPayloadId))
                    .Take(maxContextSeeds)
                    .ToArray();
            var alternativeSeeds =
                globalSeeds.Concat(diverseSeeds).Concat(contextSeeds)
                    .ToArray();

            foreach (var seed in alternativeSeeds)
            {
                var trialOrder =
                    new[] { seed }
                        .Concat(bestOrder.Where(entry =>
                            !entry.GeneratedPayloadId.Equals(
                                seed.GeneratedPayloadId,
                                StringComparison.Ordinal)))
                        .ToArray();
                var trial =
                    EvaluateCrossRigidPayloadPortfolio(
                        state,
                        trialOrder,
                        conflictProfiles,
                        invalidProfiles,
                        sourceVmdDocuments);
                evaluated++;
                if (IsBetterCrossRigidPayloadPortfolio(
                        trial,
                        best))
                {
                    best = trial;
                    bestOrder = trialOrder;
                }
            }

            // A competing Cartesian context can require several payloads
            // to replace existing joint-state owners. Promoting one or two
            // candidates cannot expose their combined benefit. Evaluate
            // bounded cohorts sharing an exact parent/context; the existing
            // occurrence conflicts and portfolio budgets still gate each
            // candidate in the ordinary replay.
            var contextCohorts =
                contextSeedObservations
                    .GroupBy(
                        item => item.ParentLocation + "\u001e" +
                                item.ContextKey,
                        StringComparer.Ordinal)
                    .Select(group => new
                    {
                        group.First().ParentLocation,
                        group.First().ContextKey,
                        Candidates = group
                            .Select(item => item.Candidate)
                            .GroupBy(item => item.GeneratedPayloadId,
                                StringComparer.Ordinal)
                            .Select(items => items.First())
                            .OrderByDescending(item =>
                                item.ExpectedArmyDrawCallsEliminated)
                            .ThenBy(item => item.GeneratedPayloadId,
                                StringComparer.Ordinal)
                            .Take(16)
                            .ToArray(),
                    })
                    .Where(cohort => cohort.Candidates.Length >= 2)
                    .OrderByDescending(cohort =>
                        cohort.Candidates.Sum(item =>
                            item.ExpectedArmyDrawCallsEliminated))
                    .ThenBy(cohort => cohort.ParentLocation,
                        StringComparer.Ordinal)
                    .ThenBy(cohort => cohort.ContextKey,
                        StringComparer.Ordinal)
                    .ToArray();
            // Reserve trials for distinct structural parents as well as
            // globally strong cohorts, rather than letting one VMD family
            // consume the entire bounded search budget.
            var cohortsToEvaluate =
                contextCohorts.Take(8)
                    .Concat(contextCohorts
                        .GroupBy(cohort => cohort.ParentLocation,
                            StringComparer.Ordinal)
                        .Select(group => group.First()))
                    .GroupBy(
                        cohort => cohort.ParentLocation + "\u001e" +
                                  cohort.ContextKey,
                        StringComparer.Ordinal)
                    .Select(group => group.First())
                    .Take(16)
                    .ToArray();
            foreach (var cohort in cohortsToEvaluate)
            {
                var promotedIds =
                    cohort.Candidates
                        .Select(entry => entry.GeneratedPayloadId)
                        .ToHashSet(StringComparer.Ordinal);
                var trialOrder =
                    cohort.Candidates
                        .Concat(bestOrder.Where(entry =>
                            !promotedIds.Contains(
                                entry.GeneratedPayloadId)))
                        .ToArray();
                var trial =
                    EvaluateCrossRigidPayloadPortfolio(
                        state,
                        trialOrder,
                        conflictProfiles,
                        invalidProfiles,
                        sourceVmdDocuments);
                evaluated++;
                if (IsBetterCrossRigidPayloadPortfolio(trial, best))
                {
                    best = trial;
                    bestOrder = trialOrder;
                }
            }

            state.CrossRigidPortfolioContextCohortTrials =
                cohortsToEvaluate.Length;

            // One-for-many substitutions are not sufficient when two cheaper
            // payloads jointly replace a large merge. Search a bounded number
            // of pairs within the same visual root and across roots. Each
            // ordering is replayed from scratch with exact Cartesian-state
            // occurrence conflicts, nested-path compatibility, and both
            // selected-portfolio budgets. Never bypass the writer's safety
            // rules or accept an ordering worse than the raw-draw baseline.
            var pairSeeds =
                alternativeSeeds
                    .Where(entry =>
                        !best.SelectedPayloadIds.Contains(
                            entry.GeneratedPayloadId))
                    .ToArray();
            var pairOptions =
                pairSeeds
                    .SelectMany((first, index) =>
                        pairSeeds.Skip(index + 1).Select(second =>
                            new
                            {
                                First = first,
                                Second = second,
                                SameRoot =
                                    primaryVmdByPayload.TryGetValue(
                                        first.GeneratedPayloadId,
                                        out var firstVmd) &&
                                    primaryVmdByPayload.TryGetValue(
                                        second.GeneratedPayloadId,
                                        out var secondVmd) &&
                                    firstVmd.Equals(
                                        secondVmd,
                                        StringComparison.OrdinalIgnoreCase),
                                CombinedBenefit =
                                    first.ExpectedArmyDrawCallsEliminated +
                                    second.ExpectedArmyDrawCallsEliminated,
                            }))
                    .ToArray();
            const int maxSameRootPairs = 16;
            const int maxCrossRootPairs = 8;
            var pairsToEvaluate =
                pairOptions
                    .Where(pair => pair.SameRoot)
                    .OrderByDescending(pair =>
                        pair.CombinedBenefit)
                    .ThenBy(pair =>
                        pair.First.GeneratedPayloadId,
                        StringComparer.Ordinal)
                    .ThenBy(pair =>
                        pair.Second.GeneratedPayloadId,
                        StringComparer.Ordinal)
                    .Take(maxSameRootPairs)
                    .Concat(
                        pairOptions
                            .Where(pair => !pair.SameRoot)
                            .OrderByDescending(pair =>
                                pair.CombinedBenefit)
                            .ThenBy(pair =>
                                pair.First.GeneratedPayloadId,
                                StringComparer.Ordinal)
                            .ThenBy(pair =>
                                pair.Second.GeneratedPayloadId,
                                StringComparer.Ordinal)
                            .Take(maxCrossRootPairs))
                    .ToArray();
            foreach (var pair in pairsToEvaluate)
            {
                // The first candidate may block some of the second's
                // concrete Cartesian states, so try both precedences.
                foreach (var front in
                         new[]
                         {
                             new[] { pair.First, pair.Second },
                             new[] { pair.Second, pair.First },
                         })
                {
                    var frontIds =
                        front
                            .Select(entry => entry.GeneratedPayloadId)
                            .ToHashSet(StringComparer.Ordinal);
                    var trialOrder =
                        front
                            .Concat(bestOrder.Where(entry =>
                                !frontIds.Contains(
                                    entry.GeneratedPayloadId)))
                            .ToArray();
                    var trial =
                        EvaluateCrossRigidPayloadPortfolio(
                            state,
                            trialOrder,
                            conflictProfiles,
                            invalidProfiles,
                            sourceVmdDocuments);
                    evaluated++;
                    if (IsBetterCrossRigidPayloadPortfolio(
                            trial,
                            best))
                    {
                        best = trial;
                        bestOrder = trialOrder;
                    }
                }
            }

            // Promoting a candidate to the front can displace unrelated
            // winners before reaching its actual conflicting occurrence.
            // Try bounded insertions before the first selected conflicting
            // owners. The complete portfolio replay remains authoritative.
            // Each category gets its own allowance. Appending contextSeeds
            // after rejectedCandidates and then taking a global prefix
            // starves them completely whenever there are 24+ rejects.
            // Prefer structural context alternatives, then supplement with
            // the strongest global and different-VMD rejected candidates.
            const int maxLocalInsertionContextSeeds = 16;
            const int maxLocalInsertionGlobalSeeds = 8;
            const int maxLocalInsertionDiverseSeeds = 8;
            const int maxLocalInsertionOwnersPerSeed = 2;
            var localInsertionTrials = 0;
            var localInsertionImprovements = 0;
            var localSeeds = contextSeeds
                .Take(maxLocalInsertionContextSeeds)
                .Concat(globalSeeds.Take(maxLocalInsertionGlobalSeeds))
                .Concat(diverseSeeds.Take(maxLocalInsertionDiverseSeeds))
                .GroupBy(entry => entry.GeneratedPayloadId,
                    StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            foreach (var contender in localSeeds)
            {
                if (best.SelectedPayloadIds.Contains(
                        contender.GeneratedPayloadId) ||
                    !conflictProfiles.TryGetValue(
                        contender.GeneratedPayloadId,
                        out var contenderProfile))
                {
                    continue;
                }

                var conflictingOwners = bestOrder
                    .Where(entry =>
                        best.SelectedPayloadIds.Contains(
                            entry.GeneratedPayloadId) &&
                        conflictProfiles.TryGetValue(
                            entry.GeneratedPayloadId, out var winnerProfile) &&
                        MayCrossRigidPayloadProfilesCompete(
                            contenderProfile, winnerProfile))
                    .Take(maxLocalInsertionOwnersPerSeed)
                    .ToArray();
                foreach (var owner in conflictingOwners)
                {
                    var trialOrder = bestOrder
                        .Where(entry => !entry.GeneratedPayloadId.Equals(
                            contender.GeneratedPayloadId,
                            StringComparison.Ordinal))
                        .ToList();
                    var ownerIndex = trialOrder.FindIndex(entry =>
                        entry.GeneratedPayloadId.Equals(
                            owner.GeneratedPayloadId,
                            StringComparison.Ordinal));
                    if (ownerIndex < 0)
                        continue;

                    trialOrder.Insert(ownerIndex, contender);
                    var trial = EvaluateCrossRigidPayloadPortfolio(
                        state,
                        trialOrder,
                        conflictProfiles,
                        invalidProfiles,
                        sourceVmdDocuments);
                    localInsertionTrials++;
                    evaluated++;
                    if (IsBetterCrossRigidPayloadPortfolio(trial, best))
                    {
                        best = trial;
                        bestOrder = trialOrder.ToArray();
                        localInsertionImprovements++;
                    }
                }
            }

            // Two compatible challengers can jointly displace an
            // established owner even when neither wins alone. Earlier pair
            // trials promoted candidates globally, potentially displacing
            // unrelated winners before the contested parent was reached.
            // Insert bounded same-context pairs immediately before a
            // conflicting selected owner and replay the whole portfolio.
            const int maxLocalPairOptions = 24;
            const int maxLocalPairOwners = 2;
            var localPairTrials = 0;
            var localPairImprovements = 0;
            var localPairSeeds = localSeeds
                .Where(entry =>
                    !best.SelectedPayloadIds.Contains(
                        entry.GeneratedPayloadId) &&
                    conflictProfiles.ContainsKey(entry.GeneratedPayloadId))
                .ToArray();
            var localPairContexts = localPairSeeds
                .ToDictionary(
                    entry => entry.GeneratedPayloadId,
                    entry => conflictProfiles[entry.GeneratedPayloadId]
                        .Occurrences
                        .SelectMany(occurrence =>
                            occurrence.JointContextKeyByParentLocation)
                        .Select(context =>
                            context.Key + "\u001e" + context.Value)
                        .ToHashSet(StringComparer.Ordinal),
                    StringComparer.Ordinal);
            var localPairOptions = localPairSeeds
                .SelectMany((first, index) =>
                    localPairSeeds.Skip(index + 1)
                        .Where(second =>
                            localPairContexts[first.GeneratedPayloadId]
                                .Overlaps(localPairContexts[
                                    second.GeneratedPayloadId]))
                        .Select(second => (
                            First: first,
                            Second: second,
                            Benefit: first.ExpectedArmyDrawCallsEliminated +
                                     second.ExpectedArmyDrawCallsEliminated)))
                .OrderByDescending(pair => pair.Benefit)
                .ThenBy(pair => pair.First.GeneratedPayloadId,
                    StringComparer.Ordinal)
                .ThenBy(pair => pair.Second.GeneratedPayloadId,
                    StringComparer.Ordinal)
                .Take(maxLocalPairOptions)
                .ToArray();
            foreach (var pair in localPairOptions)
            {
                if (best.SelectedPayloadIds.Contains(
                        pair.First.GeneratedPayloadId) ||
                    best.SelectedPayloadIds.Contains(
                        pair.Second.GeneratedPayloadId))
                {
                    continue;
                }

                var firstProfile =
                    conflictProfiles[pair.First.GeneratedPayloadId];
                var secondProfile =
                    conflictProfiles[pair.Second.GeneratedPayloadId];
                var owners = bestOrder
                    .Where(entry =>
                        best.SelectedPayloadIds.Contains(
                            entry.GeneratedPayloadId) &&
                        conflictProfiles.TryGetValue(
                            entry.GeneratedPayloadId,
                            out var ownerProfile) &&
                        (MayCrossRigidPayloadProfilesCompete(
                             firstProfile, ownerProfile) ||
                         MayCrossRigidPayloadProfilesCompete(
                             secondProfile, ownerProfile)))
                    .Take(maxLocalPairOwners)
                    .ToArray();
                foreach (var owner in owners)
                {
                    foreach (var promoted in new[]
                             {
                                 new[] { pair.First, pair.Second },
                                 new[] { pair.Second, pair.First },
                             })
                    {
                        var promotedIds = promoted
                            .Select(entry => entry.GeneratedPayloadId)
                            .ToHashSet(StringComparer.Ordinal);
                        var trialOrder = bestOrder
                            .Where(entry =>
                                !promotedIds.Contains(
                                    entry.GeneratedPayloadId))
                            .ToList();
                        var ownerIndex = trialOrder.FindIndex(entry =>
                            entry.GeneratedPayloadId.Equals(
                                owner.GeneratedPayloadId,
                                StringComparison.Ordinal));
                        if (ownerIndex < 0)
                            continue;

                        trialOrder.InsertRange(ownerIndex, promoted);
                        var trial = EvaluateCrossRigidPayloadPortfolio(
                            state,
                            trialOrder,
                            conflictProfiles,
                            invalidProfiles,
                            sourceVmdDocuments);
                        localPairTrials++;
                        evaluated++;
                        if (IsBetterCrossRigidPayloadPortfolio(
                                trial, best))
                        {
                            best = trial;
                            bestOrder = trialOrder.ToArray();
                            localPairImprovements++;
                        }
                    }
                }
            }

            state.CrossRigidPortfolioLocalPairInsertionTrials =
                localPairTrials;
            state.CrossRigidPortfolioLocalPairInsertionImprovements =
                localPairImprovements;
            state.CrossRigidPortfolioLocalInsertionTrials =
                localInsertionTrials;
            state.CrossRigidPortfolioLocalInsertionImprovements =
                localInsertionImprovements;
            state.CrossRigidPortfolioOrderingsEvaluated = evaluated;
            state.CrossRigidPortfolioPairTrials = pairsToEvaluate.Length * 2;
            state.CrossRigidBestPortfolioExpectedDraws =
                best.ExpectedDraws;
            // The final greedy replay applies the same conflict keys,
            // exact Cartesian matching, per-payload benefit floor, geometry
            // and residency budgets to pair-only additions, after the best
            // original portfolio has occupied its writable states.
            return bestOrder
                .Concat(fallbackOnlyCandidates)
                .Select((entry, index) =>
                    entry with { SelectionRank = index + 1 })
                .ToList();
        }

        // Conservative trial prefilter: even an overlap involving an
        // unselected state only schedules another ordering; it never grants
        // rewrite compatibility or bypasses the exact conflict evaluator.
        private static bool MayCrossRigidPayloadProfilesCompete(
            CrossRigidPayloadRewriteConflictProfile contender,
            CrossRigidPayloadRewriteConflictProfile selected)
        {
            var selectedKeys = selected.Occurrences
                .SelectMany(occurrence => occurrence.RewriteConflictKeys)
                .ToHashSet(StringComparer.Ordinal);
            var selectedContexts = selected.Occurrences
                .SelectMany(occurrence =>
                    occurrence.JointContextKeyByParentLocation)
                .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(pair => pair.Value)
                        .ToHashSet(StringComparer.Ordinal),
                    StringComparer.Ordinal);
            return contender.Occurrences.Any(occurrence =>
                HasCrossRigidRewriteConflict(
                    occurrence.RewriteConflictKeys, selectedKeys) ||
                occurrence.JointContextKeyByParentLocation.Any(pair =>
                    selectedContexts.TryGetValue(
                        pair.Key, out var contexts) &&
                    contexts.Any(context =>
                        !context.Equals(pair.Value,
                            StringComparison.Ordinal))));
        }

        private static bool IsBetterCrossRigidPayloadPortfolio(
            (double ExpectedDraws, long GeometryBytes,
                double ResidentBytes, HashSet<string> SelectedPayloadIds)
                candidate,
            (double ExpectedDraws, long GeometryBytes,
                double ResidentBytes, HashSet<string> SelectedPayloadIds)
                current)
        {
            if (candidate.ExpectedDraws >
                current.ExpectedDraws +
                AtlasValueGateExpectedDrawEpsilon)
            {
                return true;
            }

            if (Math.Abs(
                    candidate.ExpectedDraws -
                    current.ExpectedDraws) >
                AtlasValueGateExpectedDrawEpsilon)
            {
                return false;
            }

            return candidate.ResidentBytes <
                       current.ResidentBytes -
                       AtlasValueGateExpectedDrawEpsilon ||
                   (Math.Abs(
                        candidate.ResidentBytes -
                        current.ResidentBytes) <=
                    AtlasValueGateExpectedDrawEpsilon &&
                    candidate.GeometryBytes <
                    current.GeometryBytes);
        }

        // Shared by the final writer and the portfolio-ordering replay so
        // both apply the same per-payload ceiling. Returns null when the
        // payload's own cost per marginal draw is within the ceiling.
        private static CrossRigidPayloadSelectionDecision?
            GetCrossRigidPerPayloadCostCeilingDecision(
                CrossRigidPayloadSelectionEntry candidate,
                double marginalExpectedDraws)
        {
            if (marginalExpectedDraws <=
                AtlasValueGateExpectedDrawEpsilon)
            {
                return null;
            }

            if (candidate.GeneratedGeometryBytes /
                marginalExpectedDraws >
                CrossRigidPerPayloadCostCeilingMultiplier *
                MaxCrossRigidGeneratedGeometryBytesPerExpectedArmyDraw)
            {
                return CrossRigidPayloadSelectionDecision
                    .PerPayloadPhysicalCostCeilingExceeded;
            }

            if (candidate.ExpectedResidentGeneratedGeometryBytes /
                marginalExpectedDraws >
                CrossRigidPerPayloadCostCeilingMultiplier *
                MaxCrossRigidExpectedResidentGeometryBytesPerExpectedArmyDraw)
            {
                return CrossRigidPayloadSelectionDecision
                    .PerPayloadResidentCostCeilingExceeded;
            }

            return null;
        }

        // One ordering of the candidates, compared by the entries' identity. The ordering search
        // regenerates orderings it already evaluated (a seed that is already first, repeated
        // pair orders), and an ordering's result depends on nothing but the entries' order and the
        // read-only profiles, so the first result is reused.
        private sealed class CrossRigidPortfolioOrderKey : IEquatable<CrossRigidPortfolioOrderKey>
        {
            private readonly CrossRigidPayloadSelectionEntry[] _entries;
            private readonly int _hash;

            public CrossRigidPortfolioOrderKey(IReadOnlyList<CrossRigidPayloadSelectionEntry> entries)
            {
                _entries = new CrossRigidPayloadSelectionEntry[entries.Count];
                var hash = new HashCode();
                for (var index = 0; index < _entries.Length; index++)
                {
                    _entries[index] = entries[index];
                    hash.Add(RuntimeHelpers.GetHashCode(entries[index]));
                }

                _hash = hash.ToHashCode();
            }

            public bool Equals(CrossRigidPortfolioOrderKey? other)
            {
                if (other == null || other._hash != _hash || other._entries.Length != _entries.Length)
                    return false;

                for (var index = 0; index < _entries.Length; index++)
                {
                    if (!ReferenceEquals(_entries[index], other._entries[index]))
                        return false;
                }

                return true;
            }

            public override bool Equals(object? obj) => Equals(obj as CrossRigidPortfolioOrderKey);

            public override int GetHashCode() => _hash;
        }

        private static readonly ConditionalWeakTable<
            Dictionary<string, CrossRigidPayloadRewriteConflictProfile>,
            Dictionary<CrossRigidPortfolioOrderKey, (
                double ExpectedDraws,
                long GeometryBytes,
                double ResidentBytes,
                HashSet<string> SelectedPayloadIds)>> CrossRigidPortfolioMemo = new();

        private static (
            double ExpectedDraws,
            long GeometryBytes,
            double ResidentBytes,
            HashSet<string> SelectedPayloadIds)
            EvaluateCrossRigidPayloadPortfolio(
                BatchState state,
                IReadOnlyList<CrossRigidPayloadSelectionEntry>
                    orderedCandidates,
                Dictionary<string, CrossRigidPayloadRewriteConflictProfile>
                    conflictProfiles,
                HashSet<string> invalidProfiles,
                Dictionary<string, XmlDocument> sourceVmdDocuments)
        {
            var memo = CrossRigidPortfolioMemo.GetValue(conflictProfiles, _ => new());
            var key = new CrossRigidPortfolioOrderKey(orderedCandidates);
            if (memo.TryGetValue(key, out var cached))
                return cached;

            var evaluated = EvaluateCrossRigidPayloadPortfolioUncached(
                state,
                orderedCandidates,
                conflictProfiles,
                invalidProfiles,
                sourceVmdDocuments);
            memo[key] = evaluated;
            return evaluated;
        }

        private static (
            double ExpectedDraws,
            long GeometryBytes,
            double ResidentBytes,
            HashSet<string> SelectedPayloadIds)
            EvaluateCrossRigidPayloadPortfolioUncached(
                BatchState state,
                IReadOnlyList<CrossRigidPayloadSelectionEntry>
                    orderedCandidates,
                Dictionary<string, CrossRigidPayloadRewriteConflictProfile>
                    conflictProfiles,
                HashSet<string> invalidProfiles,
                Dictionary<string, XmlDocument> sourceVmdDocuments)
        {
            var selectedKeys =
                new HashSet<string>(StringComparer.Ordinal);
            var selectedContexts =
                new Dictionary<string, string>(
                    StringComparer.Ordinal);
            var selectedIds =
                new HashSet<string>(StringComparer.Ordinal);
            var pending = orderedCandidates;
            var alive = new bool[pending.Count];
            Array.Fill(alive, true);
            var aliveCount = pending.Count;
            var physicalBytes = 0L;
            var residentBytes = 0.0;
            var expectedDraws = 0.0;

            // Match the final greedy writer, including budget deferrals.
            // The evaluator mutates only local selection sets and caches
            // read-only conflict profiles; it never marks rewrites for output.
            while (aliveCount != 0)
            {
                var acceptedThisPass = false;
                for (var index = 0; index < pending.Count; index++)
                {
                    if (!alive[index])
                        continue;

                    var candidate = pending[index];
                    if (invalidProfiles.Contains(
                            candidate.GeneratedPayloadId))
                    {
                        alive[index] = false;
                        aliveCount--;
                        continue;
                    }

                    if (!conflictProfiles.TryGetValue(
                            candidate.GeneratedPayloadId,
                            out var profile))
                    {
                        if (!TryBuildCrossRigidPayloadRewriteConflictProfile(
                                state,
                                candidate.GeneratedPayloadId,
                                sourceVmdDocuments,
                                !state.CrossRigidLegacyEligiblePayloadIds.Contains(
                                    candidate.GeneratedPayloadId),
                                out profile,
                                out _))
                        {
                            invalidProfiles.Add(
                                candidate.GeneratedPayloadId);
                            alive[index] = false;
                            aliveCount--;
                            continue;
                        }

                        conflictProfiles[
                            candidate.GeneratedPayloadId] = profile;
                    }

                    var occurrences =
                        SelectCrossRigidCompatibleRewriteOccurrences(
                            profile,
                            selectedKeys,
                            selectedContexts);
                    var marginalDraws =
                        occurrences.Sum(occurrence =>
                            occurrence.ExpectedArmyDrawCallsEliminated);
                    if (marginalDraws <=
                            AtlasValueGateExpectedDrawEpsilon ||
                        (candidate.VmdCount == 1 &&
                         marginalDraws <
                         MinimumCrossRigidSingleVmdExpectedArmyDraw))
                    {
                        alive[index] = false;
                        aliveCount--;
                        continue;
                    }

                    if (GetCrossRigidPerPayloadCostCeilingDecision(
                            candidate,
                            marginalDraws) != null)
                    {
                        alive[index] = false;
                        aliveCount--;
                        continue;
                    }

                    var projectedDraws =
                        expectedDraws + marginalDraws;
                    var projectedResident =
                        residentBytes +
                        candidate.ExpectedResidentGeneratedGeometryBytes;
                    if (projectedResident >
                        projectedDraws *
                        MaxCrossRigidExpectedResidentGeometryBytesPerExpectedArmyDraw)
                    {
                        continue;
                    }

                    var projectedPhysical =
                        physicalBytes +
                        candidate.GeneratedGeometryBytes;
                    if (projectedPhysical >
                        projectedDraws *
                        MaxCrossRigidGeneratedGeometryBytesPerExpectedArmyDraw)
                    {
                        continue;
                    }

                    if (projectedPhysical >
                        MaxCrossRigidGeneratedGeometryGrowthBytes)
                    {
                        alive[index] = false;
                        aliveCount--;
                        continue;
                    }

                    physicalBytes = projectedPhysical;
                    residentBytes = projectedResident;
                    expectedDraws = projectedDraws;
                    selectedIds.Add(
                        candidate.GeneratedPayloadId);
                    foreach (var occurrence in occurrences)
                    {
                        selectedKeys.UnionWith(
                            occurrence.RewriteConflictKeys);
                        foreach (var context in
                                 occurrence.JointContextKeyByParentLocation)
                        {
                            selectedContexts[context.Key] =
                                context.Value;
                        }
                    }

                    alive[index] = false;
                    aliveCount--;
                    acceptedThisPass = true;
                }

                if (!acceptedThisPass)
                    break;
            }

            return (
                expectedDraws,
                physicalBytes,
                residentBytes,
                selectedIds);
        }

        private static CrossRigidRewriteConflictOccurrence[]
            SelectCrossRigidCompatibleRewriteOccurrences(
                CrossRigidPayloadRewriteConflictProfile profile,
                IReadOnlySet<string> selectedRewriteConflictKeys,
                IReadOnlyDictionary<string, string>
                    selectedJointContextKeyByParentLocation)
        {
            // The caller's selected keys/contexts are immutable for this
            // evaluation. Only track additions made by *this* payload.
            // Copying the whole selected portfolio here was quadratic across
            // candidates, multiplied again by each portfolio-ordering trial.
            var localRewriteConflictKeys =
                new HashSet<string>(StringComparer.Ordinal);
            var localJointContextKeyByParentLocation =
                new Dictionary<string, string>(StringComparer.Ordinal);
            var selected =
                new List<CrossRigidRewriteConflictOccurrence>();

            // Profile occurrences are already sorted by expected draw benefit,
            // then by selection key, when the conflict profile is constructed.
            foreach (var occurrence in profile.Occurrences)
            {
                if (HasCrossRigidRewriteConflict(
                        occurrence.RewriteConflictKeys,
                        selectedRewriteConflictKeys) ||
                    HasCrossRigidRewriteConflict(
                        occurrence.RewriteConflictKeys,
                        localRewriteConflictKeys) ||
                    occurrence.JointContextKeyByParentLocation.Any(
                        pair =>
                            (selectedJointContextKeyByParentLocation
                                 .TryGetValue(
                                     pair.Key,
                                     out var existingContextKey) &&
                             !existingContextKey.Equals(
                                 pair.Value,
                                 StringComparison.Ordinal)) ||
                            (localJointContextKeyByParentLocation
                                 .TryGetValue(
                                     pair.Key,
                                     out var localContextKey) &&
                             !localContextKey.Equals(
                                 pair.Value,
                                 StringComparison.Ordinal))))
                {
                    continue;
                }

                selected.Add(occurrence);
                localRewriteConflictKeys.UnionWith(
                    occurrence.RewriteConflictKeys);
                foreach (var pair in
                         occurrence.JointContextKeyByParentLocation)
                {
                    localJointContextKeyByParentLocation[
                        pair.Key] =
                        pair.Value;
                }
            }

            return selected.ToArray();
        }

        private static bool HasCrossRigidRewriteConflict(
            IReadOnlySet<string> candidateKeys,
            IReadOnlySet<string> selectedKeys)
        {
            const string exactStructuralPrefix =
                "exact-structural-slot\u001f";
            const string jointStructuralPrefix =
                "joint-structural-slot\u001f";
            const string jointTraversalPrefix =
                "joint-traversal-slot\u001f";

            // Allocation-free probing: counterpart keys are built in a
            // stack buffer and looked up by span. Falls back to strings for
            // sets that do not support span lookups.
            HashSet<string>.AlternateLookup<ReadOnlySpan<char>> lookup = default;
            var hasLookup = selectedKeys is HashSet<string> hashSet &&
                            hashSet.TryGetAlternateLookup(out lookup);

            foreach (var key in candidateKeys)
            {
                if (key.StartsWith(
                        exactStructuralPrefix,
                        StringComparison.Ordinal))
                {
                    if (ContainsWithPrefix(
                            selectedKeys, hasLookup, lookup,
                            jointStructuralPrefix,
                            key.AsSpan(exactStructuralPrefix.Length)))
                    {
                        return true;
                    }

                    continue;
                }

                if (key.StartsWith(
                        jointStructuralPrefix,
                        StringComparison.Ordinal))
                {
                    var suffix =
                        key.AsSpan(jointStructuralPrefix.Length);
                    if (ContainsWithPrefix(
                            selectedKeys, hasLookup, lookup,
                            exactStructuralPrefix, suffix) ||
                        ContainsWithPrefix(
                            selectedKeys, hasLookup, lookup,
                            jointTraversalPrefix, suffix))
                    {
                        return true;
                    }

                    continue;
                }

                if (key.StartsWith(
                        jointTraversalPrefix,
                        StringComparison.Ordinal))
                {
                    if (ContainsWithPrefix(
                            selectedKeys, hasLookup, lookup,
                            jointStructuralPrefix,
                            key.AsSpan(jointTraversalPrefix.Length)))
                    {
                        return true;
                    }

                    continue;
                }

                if (selectedKeys.Contains(key))
                    return true;
            }

            return false;
        }

        private static bool ContainsWithPrefix(
            IReadOnlySet<string> selectedKeys,
            bool hasLookup,
            HashSet<string>.AlternateLookup<ReadOnlySpan<char>> lookup,
            string prefix,
            ReadOnlySpan<char> suffix)
        {
            var length = prefix.Length + suffix.Length;
            char[]? rented = null;
            Span<char> buffer = length <= 512
                ? stackalloc char[512]
                : (rented = System.Buffers.ArrayPool<char>.Shared.Rent(length));
            try
            {
                prefix.AsSpan().CopyTo(buffer);
                suffix.CopyTo(buffer[prefix.Length..]);
                var probe = buffer[..length];
                return hasLookup
                    ? lookup.Contains(probe)
                    : selectedKeys.Contains(probe.ToString());
            }
            finally
            {
                if (rented != null)
                    System.Buffers.ArrayPool<char>.Shared.Return(rented);
            }
        }

        private static (string Reason, string Detail)
            ClassifyCrossRigidRewriteOccurrenceConflict(
                CrossRigidPayloadRewriteConflictProfile profile,
                IReadOnlySet<string> selectedRewriteConflictKeys,
                IReadOnlyDictionary<
                    string,
                    CrossRigidSelectedRewriteConflictOwner>
                    selectedRewriteConflictOwnerByKey,
                IReadOnlyDictionary<string, string>
                    selectedJointContextKeyByParentLocation,
                IReadOnlyDictionary<
                    string,
                    CrossRigidSelectedRewriteConflictOwner>
                    selectedJointContextOwnerByParentLocation)
        {
            const string exactStructuralPrefix =
                "exact-structural-slot\u001f";
            const string jointStructuralPrefix =
                "joint-structural-slot\u001f";
            const string jointTraversalPrefix =
                "joint-traversal-slot\u001f";

            foreach (var occurrence in profile.Occurrences)
            {
                foreach (var pair in
                         occurrence.JointContextKeyByParentLocation)
                {
                    if (!selectedJointContextKeyByParentLocation.TryGetValue(
                            pair.Key,
                            out var selectedContextKey) ||
                        selectedContextKey.Equals(
                            pair.Value,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var selectedSuffix =
                        selectedContextKey.StartsWith(
                            pair.Key + "\u001f",
                            StringComparison.Ordinal)
                            ? selectedContextKey[
                                (pair.Key.Length + 1)..]
                            : selectedContextKey;
                    var candidateSuffix =
                        pair.Value.StartsWith(
                            pair.Key + "\u001f",
                            StringComparison.Ordinal)
                            ? pair.Value[
                                (pair.Key.Length + 1)..]
                            : pair.Value;
                    var ownerDetail =
                        selectedJointContextOwnerByParentLocation
                            .TryGetValue(
                                pair.Key,
                                out var contextOwner)
                            ? DescribeCrossRigidSelectedConflictOwner(
                                contextOwner)
                            : string.Empty;
                    return (
                        "Different joint slot/dimension set under one structural parent",
                        $"parent={pair.Key}; selected={selectedSuffix}; " +
                        $"candidate={candidateSuffix}{ownerDetail}");
                }

                foreach (var key in occurrence.RewriteConflictKeys)
                {
                    if (key.StartsWith(
                            exactStructuralPrefix,
                            StringComparison.Ordinal))
                    {
                        var counterpart =
                            jointStructuralPrefix +
                            key[exactStructuralPrefix.Length..];
                        if (selectedRewriteConflictKeys.Contains(
                                counterpart))
                        {
                            return (
                                "Exact rewrite overlaps a selected joint structural slot",
                                key +
                                DescribeCrossRigidSelectedConflictOwner(
                                    selectedRewriteConflictOwnerByKey,
                                    counterpart));
                        }

                        continue;
                    }

                    if (key.StartsWith(
                            jointStructuralPrefix,
                            StringComparison.Ordinal))
                    {
                        var suffix =
                            key[jointStructuralPrefix.Length..];
                        if (selectedRewriteConflictKeys.Contains(
                                exactStructuralPrefix + suffix))
                        {
                            return (
                                "Joint rewrite overlaps a selected exact structural slot",
                                key +
                                DescribeCrossRigidSelectedConflictOwner(
                                    selectedRewriteConflictOwnerByKey,
                                    exactStructuralPrefix + suffix));
                        }

                        if (selectedRewriteConflictKeys.Contains(
                                jointTraversalPrefix + suffix))
                        {
                            return (
                                "Joint rewrite would invalidate a selected nested joint path",
                                key +
                                DescribeCrossRigidSelectedConflictOwner(
                                    selectedRewriteConflictOwnerByKey,
                                    jointTraversalPrefix + suffix));
                        }

                        continue;
                    }

                    if (key.StartsWith(
                            jointTraversalPrefix,
                            StringComparison.Ordinal))
                    {
                        var counterpart =
                            jointStructuralPrefix +
                            key[jointTraversalPrefix.Length..];
                        if (selectedRewriteConflictKeys.Contains(
                                counterpart))
                        {
                            return (
                                "Joint traversal passes through a selected rewritten slot",
                                key +
                                DescribeCrossRigidSelectedConflictOwner(
                                    selectedRewriteConflictOwnerByKey,
                                    counterpart));
                        }

                        continue;
                    }

                    if (!selectedRewriteConflictKeys.Contains(key))
                        continue;

                    if (key.StartsWith(
                            "exact\u001f",
                            StringComparison.Ordinal))
                    {
                        return (
                            "Exact source model occurrence already selected",
                            key +
                            DescribeCrossRigidSelectedConflictOwner(
                                selectedRewriteConflictOwnerByKey,
                                key));
                    }

                    if (key.StartsWith(
                            "joint\u001f",
                            StringComparison.Ordinal))
                    {
                        return (
                            "Joint Cartesian source occurrence already selected",
                            key +
                            DescribeCrossRigidSelectedConflictOwner(
                                selectedRewriteConflictOwnerByKey,
                                key));
                    }

                    return (
                        "Rewrite conflict key already selected",
                        key +
                        DescribeCrossRigidSelectedConflictOwner(
                            selectedRewriteConflictOwnerByKey,
                            key));
                }
            }

            return (
                "Rewrite conflict could not be classified",
                "all occurrence states were incompatible but no specific key was identified");
        }

        private static string DescribeCrossRigidSelectedConflictOwner(
            IReadOnlyDictionary<
                string,
                CrossRigidSelectedRewriteConflictOwner>
                ownerByConflictKey,
            string conflictKey)
            => ownerByConflictKey.TryGetValue(
                    conflictKey,
                    out var owner)
                ? DescribeCrossRigidSelectedConflictOwner(
                    owner)
                : string.Empty;

        private static string DescribeCrossRigidSelectedConflictOwner(
            CrossRigidSelectedRewriteConflictOwner owner)
            => $"; ownerPayload={owner.GeneratedPayloadId}, " +
               $"ownerStateExpectedDraws=" +
               $"{owner.OccurrenceExpectedArmyDrawCallsEliminated:0.######}, " +
               $"ownerMaxRawDraws={owner.MaxRawDrawsSavedPerState}, " +
               $"ownerGeometry={FormatMiB(owner.GeneratedGeometryBytes, 2)}, " +
               $"ownerRank={owner.SelectionRank}";

        private static void RecordCrossRigidRewriteConflict(
            BatchState state,
            string reason,
            CrossRigidPayloadSelectionEntry candidate,
            string example)
        {
            state.CrossRigidRewriteConflictReasonCounts[reason] =
                state.CrossRigidRewriteConflictReasonCounts
                    .GetValueOrDefault(reason) + 1;
            state.CrossRigidRewriteConflictExpectedDraws[reason] =
                state.CrossRigidRewriteConflictExpectedDraws
                    .GetValueOrDefault(reason) +
                candidate.ExpectedArmyDrawCallsEliminated;

            if (!state.CrossRigidRewriteConflictExamples.TryGetValue(
                    reason,
                    out var examples))
            {
                examples = [];
                state.CrossRigidRewriteConflictExamples[reason] =
                    examples;
            }

            const int maxExamplesPerReason = 8;
            if (examples.Count < maxExamplesPerReason &&
                !examples.Contains(
                    example,
                    StringComparer.Ordinal))
            {
                examples.Add(example);
            }
        }

        private static void AddCrossRigidStructuralSlotConflictKeys(
            HashSet<string> conflictKeys,
            string rootVmdPath,
            CrossRigidVisualInstance instance)
        {
            for (var hopIndex = 0;
                 hopIndex < instance.ReferenceChain.Length;
                 hopIndex++)
            {
                var hop =
                    instance.ReferenceChain[hopIndex];
                AddCrossRigidStructuralSlotConflictKeysForXmlPath(
                    conflictKeys,
                    "exact-structural-slot",
                    rootVmdPath,
                    instance.ReferenceChain.Take(
                        hopIndex),
                    hop.OwnerVmdPath,
                    hop.ReferenceXmlPath);
            }

            AddCrossRigidStructuralSlotConflictKeysForXmlPath(
                conflictKeys,
                "exact-structural-slot",
                rootVmdPath,
                instance.ReferenceChain,
                instance.DefiningVmdPath,
                instance.ModelXmlPath);
        }

        private static void
            AddCrossRigidJointTraversalStructuralSlotConflictKeys(
                HashSet<string> conflictKeys,
                string rootVmdPath,
                CrossRigidJointAlwaysPresentRewriteDescriptor descriptor)
        {
            for (var hopIndex = 0;
                 hopIndex < descriptor.AnchorReferenceChain.Length;
                 hopIndex++)
            {
                var hop =
                    descriptor.AnchorReferenceChain[hopIndex];
                AddCrossRigidStructuralSlotConflictKeysForXmlPath(
                    conflictKeys,
                    "joint-traversal-slot",
                    rootVmdPath,
                    descriptor.AnchorReferenceChain.Take(
                        hopIndex),
                    hop.OwnerVmdPath,
                    hop.ReferenceXmlPath);
            }

            AddCrossRigidStructuralSlotConflictKeysForXmlPath(
                conflictKeys,
                "joint-traversal-slot",
                rootVmdPath,
                descriptor.AnchorReferenceChain,
                descriptor.AnchorVmdPath,
                descriptor.ParentXmlPath);
        }

        private static void
            AddCrossRigidStructuralSlotConflictKeysForXmlPath(
                HashSet<string> conflictKeys,
                string conflictKind,
                string rootVmdPath,
                IEnumerable<CrossRigidVmdReferenceHop>
                    anchorReferenceChain,
                string ownerVmdPath,
                string xmlPath)
        {
            const string slotMarker = "/SLOT[";
            var searchIndex = 0;
            while (searchIndex < xmlPath.Length)
            {
                var slotMarkerIndex =
                    xmlPath.IndexOf(
                        slotMarker,
                        searchIndex,
                        StringComparison.Ordinal);
                if (slotMarkerIndex < 0)
                    break;

                var slotIndexStart =
                    slotMarkerIndex +
                    slotMarker.Length;
                var slotIndexEnd =
                    xmlPath.IndexOf(
                        ']',
                        slotIndexStart);
                if (slotIndexEnd <= slotIndexStart ||
                    !int.TryParse(
                        xmlPath[
                            slotIndexStart..
                            slotIndexEnd],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var slotIndex) ||
                    slotIndex <= 0)
                {
                    searchIndex =
                        slotIndexStart;
                    continue;
                }

                conflictKeys.Add(
                    BuildCrossRigidStructuralSlotConflictKey(
                        conflictKind,
                        rootVmdPath,
                        anchorReferenceChain,
                        ownerVmdPath,
                        xmlPath[..slotMarkerIndex],
                        slotIndex));
                searchIndex =
                    slotIndexEnd + 1;
            }
        }

        private static string
            BuildCrossRigidStructuralSlotConflictKey(
                string conflictKind,
                string rootVmdPath,
                IEnumerable<CrossRigidVmdReferenceHop>
                    anchorReferenceChain,
                string ownerVmdPath,
                string parentXmlPath,
                int slotIndex)
            => string.Join(
                "\u001f",
                conflictKind,
                Normalize(rootVmdPath),
                BuildCrossRigidReferenceChainSignature(
                    anchorReferenceChain),
                Normalize(ownerVmdPath),
                parentXmlPath,
                slotIndex.ToString(
                    CultureInfo.InvariantCulture));

        private static string BuildCrossRigidSelectedRewriteOccurrenceKey(
            CrossRigidMergeAnalysisEntry plan,
            CrossRigidRewriteOccurrenceSet occurrenceSet)
            => string.Join(
                "\u001f",
                plan.RewritePlanId,
                BuildCrossRigidOccurrenceProvenanceSignature(
                    occurrenceSet.SourceInstances));

        private static string
            BuildCrossRigidSelectedRewriteCombinationKey(
                string rewriteOccurrenceKey,
                string combinationKey)
            => string.Join(
                "\u001f",
                rewriteOccurrenceKey,
                "canonical-joint-state",
                combinationKey);

        private static bool
            TryBuildCrossRigidPayloadRewriteConflictProfile(
                BatchState state,
                string generatedPayloadId,
                Dictionary<string, XmlDocument> sourceVmdDocuments,
                bool includeSupplementaryPairFallbackPlans,
                out CrossRigidPayloadRewriteConflictProfile profile,
                out string reason,
                bool onlySupplementaryPairFallbackPlans = false)
        {
            reason = string.Empty;
            var occurrences =
                new List<CrossRigidRewriteConflictOccurrence>();

            foreach (var plan in
                     state.CrossRigidMergeAnalysisEntries.Where(
                         entry =>
                             entry.GeneratedPayloadId.Equals(
                                 generatedPayloadId,
                                 StringComparison.Ordinal) &&
                             (onlySupplementaryPairFallbackPlans
                                  ? state.CrossRigidPreviouslySuppressedPairFallbackPlanIds
                                      .Contains(entry.RewritePlanId)
                                  : includeSupplementaryPairFallbackPlans ||
                                    !state.CrossRigidPreviouslySuppressedPairFallbackPlanIds
                                        .Contains(entry.RewritePlanId))))
            {
                foreach (var occurrenceSet in
                         plan.RewriteOccurrenceSets)
                {
                    var rewriteOccurrenceKey =
                        BuildCrossRigidSelectedRewriteOccurrenceKey(
                            plan,
                            occurrenceSet);
                    var expectedDraws =
                        CalculateExpectedCrossRigidArmyDrawSavings(
                            state,
                            plan.VmdPath,
                            occurrenceSet.Probability,
                            plan.Lods);
                    var rewriteConflictKeys =
                        new HashSet<string>(
                            StringComparer.Ordinal);
                    var jointContextKeyByParentLocation =
                        new Dictionary<string, string>(
                            StringComparer.Ordinal);
                    var activationSignatureCount =
                        occurrenceSet.SourceInstances
                            .Select(instance =>
                                instance.ActivationSignature)
                            .Distinct(StringComparer.Ordinal)
                            .Count();
                    if (activationSignatureCount == 1)
                    {
                        foreach (var instance in
                                 occurrenceSet.SourceInstances)
                        {
                            rewriteConflictKeys.Add(
                                string.Join(
                                    "\u001f",
                                    "exact",
                                    BuildCrossRigidConsumedOccurrenceKey(
                                        plan.VmdPath,
                                        instance)));
                            AddCrossRigidStructuralSlotConflictKeys(
                                rewriteConflictKeys,
                                plan.VmdPath,
                                instance);
                        }

                        occurrences.Add(
                            new CrossRigidRewriteConflictOccurrence(
                                rewriteOccurrenceKey,
                                rewriteOccurrenceKey,
                                null,
                                expectedDraws,
                                rewriteConflictKeys,
                                jointContextKeyByParentLocation));
                        continue;
                    }

                    if (!TryDescribeCrossRigidJointAlwaysPresentRewrite(
                            state,
                            occurrenceSet,
                            sourceVmdDocuments,
                            out var descriptor,
                            out reason))
                    {
                        profile = null!;
                        return false;
                    }

                    var originalParentKey =
                        BuildCrossRigidJointParentLocationKey(
                            plan.VmdPath, descriptor);
                    var parentLocationKey =
                        GetCrossRigidEffectiveJointParentKey(
                            state, plan.VmdPath, descriptor);
                    var contextDescriptor =
                        GetCrossRigidEffectiveJointDescriptor(
                            state,
                            plan.VmdPath,
                            descriptor);
                    var contextKey =
                        BuildCrossRigidJointContextKey(
                            plan.VmdPath,
                            contextDescriptor);
                    jointContextKeyByParentLocation[
                        parentLocationKey] = contextKey;
                    if (!parentLocationKey.Equals(
                            originalParentKey, StringComparison.Ordinal))
                    {
                        // Ordinary contexts conflict with this marker;
                        // disjoint factors all share the same marker.
                        jointContextKeyByParentLocation[
                            originalParentKey] =
                            CrossRigidFactoredParentMarker;
                    }
                    AddCrossRigidJointTraversalStructuralSlotConflictKeys(
                        rewriteConflictKeys,
                        plan.VmdPath,
                        contextDescriptor);
                    foreach (var slotIndex in
                             contextDescriptor.SlotIndices)
                    {
                        rewriteConflictKeys.Add(
                            BuildCrossRigidStructuralSlotConflictKey(
                                "joint-structural-slot",
                                plan.VmdPath,
                                contextDescriptor.AnchorReferenceChain,
                                contextDescriptor.AnchorVmdPath,
                                contextDescriptor.ParentXmlPath,
                                slotIndex));
                    }

                    if (!TryGetCrossRigidMatchingJointCombinations(
                            state,
                            descriptor,
                            contextDescriptor,
                            sourceVmdDocuments,
                            out var matchingCombinations,
                            out reason))
                    {
                        profile = null!;
                        return false;
                    }

                    var totalMatchingMultiplicity =
                        matchingCombinations.Sum(combination =>
                            combination.Multiplicity);
                    if (totalMatchingMultiplicity <= 0)
                    {
                        reason =
                            "Canonical joint state projection has zero total multiplicity";
                        profile = null!;
                        return false;
                    }

                    foreach (var matchingCombination in
                             matchingCombinations)
                    {
                        var combinationConflictKeys =
                            new HashSet<string>(
                                rewriteConflictKeys,
                                StringComparer.Ordinal);
                        foreach (var selection in
                                 descriptor.SourceSelections)
                        {
                            combinationConflictKeys.Add(
                                string.Join(
                                    "\u001f",
                                    "joint",
                                    contextKey,
                                    matchingCombination.CombinationKey,
                                    BuildCrossRigidOccurrenceProvenanceSignature(
                                        [selection.Instance])));
                        }

                        occurrences.Add(
                            new CrossRigidRewriteConflictOccurrence(
                                BuildCrossRigidSelectedRewriteCombinationKey(
                                    rewriteOccurrenceKey,
                                    matchingCombination.CombinationKey),
                                rewriteOccurrenceKey,
                                matchingCombination.CombinationKey,
                                expectedDraws *
                                matchingCombination.Multiplicity /
                                totalMatchingMultiplicity,
                                combinationConflictKeys,
                                new Dictionary<string, string>(
                                    jointContextKeyByParentLocation,
                                    StringComparer.Ordinal)));
                    }
                }
            }

            profile =
                new CrossRigidPayloadRewriteConflictProfile(
                    occurrences
                        .OrderByDescending(occurrence =>
                            occurrence.ExpectedArmyDrawCallsEliminated)
                        .ThenBy(
                            occurrence =>
                                occurrence.SelectionKey,
                            StringComparer.Ordinal)
                        .ToArray());
            return true;
        }
    }
}
