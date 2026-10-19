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
        private static void BuildCrossRigidCanonicalJointContexts(
            BatchState state,
            Dictionary<string, XmlDocument> sourceVmdDocuments)
        {
            state.CrossRigidCanonicalJointDescriptorByParentLocation.Clear();
            state.CrossRigidFactoredJointDescriptorsByParentLocation.Clear();
            state.CrossRigidFactoredJointParentCount = 0;
            state.CrossRigidFactoredJointGroupCount = 0;
            state.CrossRigidFactoredConditionalGroupCount = 0;
            state.CrossRigidFactoredSingleCarrierGroupCount = 0;
            state.CrossRigidFactoredPackingSearchImprovements = 0;
            state.CrossRigidFactoredPairLookaheadSeeds = 0;
            state.CrossRigidPartialCanonicalBeamImprovements = 0;
            state.CrossRigidPartialCanonicalBeamUnionCandidates = 0;
            state.CrossRigidCanonicalJointParentCount = 0;
            state.CrossRigidCanonicalJointContextsFolded = 0;
            state.CrossRigidCanonicalJointParentConflictCount = 0;

            var candidatesByParent =
                new Dictionary<
                    string,
                    List<(
                        string RootVmdPath,
                        CrossRigidJointAlwaysPresentRewriteDescriptor Descriptor,
                        string ContextKey)>>(
                    StringComparer.Ordinal);
            var benefitByContextKey =
                new Dictionary<string, double>(StringComparer.Ordinal);

            foreach (var plan in state.CrossRigidMergeAnalysisEntries)
            {
                // Pair-only fallback plans must not change the canonical
                // Cartesian context used by the old full-group portfolio.
                // They can project into a compatible context at selection
                // time or use an independent parent when safe.
                if (state.CrossRigidPreviouslySuppressedPairFallbackPlanIds
                        .Contains(plan.RewritePlanId))
                    continue;

                foreach (var occurrenceSet in plan.RewriteOccurrenceSets)
                {
                    if (occurrenceSet.SourceInstances
                            .Select(instance =>
                                instance.ActivationSignature)
                            .Distinct(StringComparer.Ordinal)
                            .Count() == 1)
                    {
                        continue;
                    }

                    if (!TryDescribeCrossRigidJointAlwaysPresentRewrite(
                            state,
                            occurrenceSet,
                            sourceVmdDocuments,
                            out var descriptor,
                            out _))
                    {
                        continue;
                    }

                    var parentLocationKey =
                        BuildCrossRigidJointParentLocationKey(
                            plan.VmdPath,
                            descriptor);
                    var contextKey =
                        BuildCrossRigidJointContextKey(
                            plan.VmdPath,
                            descriptor);
                    benefitByContextKey[contextKey] =
                        benefitByContextKey.GetValueOrDefault(contextKey) +
                        plan.ExpectedArmyDrawCallsEliminated *
                        occurrenceSet.Probability /
                        Math.Max(
                            plan.AggregatedConfigurationProbability,
                            AtlasValueGateExpectedDrawEpsilon);
                    if (!candidatesByParent.TryGetValue(
                            parentLocationKey,
                            out var candidates))
                    {
                        candidates = [];
                        candidatesByParent[parentLocationKey] =
                            candidates;
                    }

                    if (!candidates.Any(candidate =>
                            candidate.ContextKey.Equals(
                                contextKey,
                                StringComparison.Ordinal)))
                    {
                        candidates.Add(
                            (
                                plan.VmdPath,
                                descriptor,
                                contextKey));
                    }
                }
            }

            foreach (var (parentLocationKey, candidates) in
                     candidatesByParent)
            {
                CrossRigidJointAlwaysPresentRewriteDescriptor?
                    canonicalDescriptor = null;
                foreach (var candidate in
                         candidates
                             .OrderByDescending(item =>
                                 item.Descriptor.CombinationCount)
                             .ThenByDescending(item =>
                                 item.Descriptor
                                     .CommonAncestorDimensions
                                     .Length)
                             .ThenByDescending(item =>
                                 item.Descriptor.SlotIndices.Length)
                             .ThenBy(
                                 item => item.ContextKey,
                                 StringComparer.Ordinal))
                {
                    if (!candidates.All(other =>
                            IsCrossRigidJointDescriptorStructuralSuperset(
                                candidate.Descriptor,
                                other.Descriptor)))
                    {
                        continue;
                    }

                    canonicalDescriptor =
                        candidate.Descriptor;
                    break;
                }

                // Distinct same-parent slot sets can be independently
                // correct without any existing candidate containing every
                // slot. Synthesize the full sibling-slot product when it
                // remains within the exact-state budget. Nested dimensions
                // are deliberately excluded: their dependencies must not
                // be flattened by a union.
                if (canonicalDescriptor == null &&
                    TryBuildCrossRigidCanonicalSiblingSlotUnion(
                        state,
                        candidates.Select(candidate =>
                            candidate.Descriptor),
                        sourceVmdDocuments,
                        out var siblingUnion))
                {
                    canonicalDescriptor = siblingUnion;
                    RecordCrossRigidAnalysisDiagnostic(
                        state,
                        "Canonical sibling-slot union synthesized",
                        $"{candidates[0].RootVmdPath} / " +
                        $"{siblingUnion.ParentXmlPath}: " +
                        $"slots={string.Join(",", siblingUnion.SlotIndices)}, " +
                        $"weightedStates={siblingUnion.CombinationCount}");
                }

                if (canonicalDescriptor == null &&
                    TryBuildCrossRigidCanonicalConditionalDimensionUnion(
                        state,
                        candidates.Select(candidate =>
                            candidate.Descriptor),
                        sourceVmdDocuments,
                        out var conditionalUnion))
                {
                    canonicalDescriptor = conditionalUnion;
                    RecordCrossRigidAnalysisDiagnostic(
                        state,
                        "Canonical conditional-dimension union synthesized",
                        $"{candidates[0].RootVmdPath} / " +
                        $"{conditionalUnion.ParentXmlPath}: " +
                        $"slots={string.Join(",", conditionalUnion.SlotIndices)}, " +
                        $"dimensions={conditionalUnion.CommonAncestorDimensions.Length}, " +
                        $"weightedStates={conditionalUnion.CombinationCount}");
                }

                if (canonicalDescriptor == null &&
                    TryBuildCrossRigidCanonicalMixedDimensionUnion(
                        state,
                        candidates.Select(candidate => candidate.Descriptor),
                        sourceVmdDocuments,
                        out var mixedUnion))
                {
                    canonicalDescriptor = mixedUnion;
                    RecordCrossRigidAnalysisDiagnostic(
                        state,
                        "Canonical independent sibling/conditional union synthesized",
                        candidates[0].RootVmdPath + " / " +
                        mixedUnion.ParentXmlPath + ": slots=" +
                        string.Join(",", mixedUnion.SlotIndices) +
                        ", dimensions=" +
                        mixedUnion.CommonAncestorDimensions.Length +
                        ", weightedStates=" +
                        mixedUnion.CombinationCount);
                }

                // Disjoint direct root slot sets can remain separate joint
                // slots. Their runtime random choices preserve the original
                // product distribution without expanding the global product.
                if (canonicalDescriptor == null &&
                    TryBuildCrossRigidFactoredRootJointContexts(
                        state,
                        candidates,
                        benefitByContextKey,
                        sourceVmdDocuments,
                        out var factors))
                {
                    state.CrossRigidFactoredJointDescriptorsByParentLocation[
                        parentLocationKey] = factors;
                    state.CrossRigidFactoredJointParentCount++;
                    state.CrossRigidFactoredJointGroupCount += factors.Length;
                    state.CrossRigidFactoredConditionalGroupCount +=
                        factors.Count(factor =>
                            factor.CommonAncestorDimensions.Length != 0);
                    state.CrossRigidFactoredSingleCarrierGroupCount +=
                        factors.Count(factor => factor.SlotIndices.Length == 1);
                    var coveredContexts = candidates.Count(candidate =>
                        factors.Any(factor =>
                            IsCrossRigidJointDescriptorStructuralSuperset(
                                factor, candidate.Descriptor)));
                    if (coveredContexts < candidates.Count)
                        state.CrossRigidCanonicalJointParentConflictCount++;
                    var synthesizedFactors = factors.Count(factor =>
                        !candidates.Any(candidate =>
                            BuildCrossRigidJointContextKey(
                                    candidate.RootVmdPath, factor)
                                .Equals(
                                    candidate.ContextKey,
                                    StringComparison.Ordinal)));
                    RecordCrossRigidAnalysisDiagnostic(
                        state,
                        "Factored root Cartesian contexts synthesized",
                        candidates[0].RootVmdPath +
                        $": covered={coveredContexts}/{candidates.Count}, " +
                        $"synthetic={synthesizedFactors}; " +
                        string.Join(" | ", factors.Select(factor =>
                            $"slots={string.Join(",", factor.SlotIndices)} " +
                            $"weightedStates={factor.CombinationCount}")));
                    continue;
                }

                // A single parent may have several incompatible contexts
                // even though a useful subset can be represented exactly.
                // Do not discard compatible smaller cohorts merely because
                // the union of every observed context exceeds the state cap.
                if (canonicalDescriptor == null &&
                    TryBuildCrossRigidBestPartialCanonicalJointContext(
                        state,
                        candidates.Select(candidate => candidate.Descriptor)
                            .ToArray(),
                        candidates[0].RootVmdPath,
                        benefitByContextKey,
                        sourceVmdDocuments,
                        out var partialDescriptor,
                        out var partialContextCount))
                {
                    canonicalDescriptor = partialDescriptor;
                    var coveredBenefit = candidates
                        .Where(candidate =>
                            IsCrossRigidJointDescriptorStructuralSuperset(
                                partialDescriptor, candidate.Descriptor))
                        .Sum(candidate =>
                            benefitByContextKey.GetValueOrDefault(
                                candidate.ContextKey));
                    RecordCrossRigidAnalysisDiagnostic(
                        state,
                        "Partial canonical joint context synthesized",
                        $"{candidates[0].RootVmdPath} / " +
                        $"{partialDescriptor.ParentXmlPath}: " +
                        $"covered={partialContextCount}/{candidates.Count}, " +
                        $"benefit={coveredBenefit:0.###} expectedDraws, " +
                        $"slots={string.Join(",", partialDescriptor.SlotIndices)}, " +
                        $"dimensions={partialDescriptor.CommonAncestorDimensions.Length}, " +
                        $"weightedStates={partialDescriptor.CombinationCount}");
                }

                if (canonicalDescriptor == null)
                {
                    if (candidates.Count > 1)
                    {
                        state.CrossRigidCanonicalJointParentConflictCount++;
                    }

                    continue;
                }

                var coveredContextCount = candidates.Count(candidate =>
                    IsCrossRigidJointDescriptorStructuralSuperset(
                        canonicalDescriptor,
                        candidate.Descriptor));
                if (coveredContextCount < candidates.Count)
                {
                    // The remaining contexts continue to compete through
                    // their normal parent-location conflict keys.
                    state.CrossRigidCanonicalJointParentConflictCount++;
                }

                state.CrossRigidCanonicalJointDescriptorByParentLocation[
                    parentLocationKey] =
                    canonicalDescriptor;
                if (coveredContextCount > 1)
                {
                    state.CrossRigidCanonicalJointParentCount++;
                    state.CrossRigidCanonicalJointContextsFolded +=
                        coveredContextCount - 1;
                }
            }
        }

        // Root-level carrier slots are independent random choices even when
        // a carrier contains conditional local or child-VMD selections. An
        // incompatible context must not disable factoring unrelated carriers:
        // it can still compete against the factored context at selection time.
        // Never factor overlapping carriers or non-root structural parents.
        private static bool TryBuildCrossRigidFactoredRootJointContexts(
            BatchState state,
            IReadOnlyList<(
                string RootVmdPath,
                CrossRigidJointAlwaysPresentRewriteDescriptor Descriptor,
                string ContextKey)> candidates,
            IReadOnlyDictionary<string, double> benefitByContextKey,
            Dictionary<string, XmlDocument> sourceVmdDocuments,
            out CrossRigidJointAlwaysPresentRewriteDescriptor[] factors)
        {
            factors = [];
            if (candidates.Count < 2)
                return false;

            // A single root carrier can contain several co-rendered
            // descendants with conditional choices. Such a joint context
            // can coexist with disjoint root carriers without multiplying
            // its Cartesian states into the other contexts.
            // Keep every existing anchor, dimension and disjointness check.
            var ranked = candidates
                .Where(item =>
                    item.Descriptor.SlotIndices.Length >= 1 &&
                    item.Descriptor.AnchorReferenceChain.Length == 0 &&
                    item.Descriptor.ParentXmlPath == "/VARIANT_MESH" &&
                    Normalize(item.Descriptor.AnchorVmdPath).Equals(
                        Normalize(item.RootVmdPath),
                        StringComparison.OrdinalIgnoreCase) &&
                    item.Descriptor.CommonAncestorDimensions.All(dimension =>
                        item.Descriptor.SlotIndices.Contains(
                            dimension.CarrierSlotIndex)))
                .OrderByDescending(item =>
                    benefitByContextKey.GetValueOrDefault(item.ContextKey))
                .ThenBy(item => item.Descriptor.CombinationCount)
                .ThenBy(item => item.ContextKey, StringComparer.Ordinal)
                .ToArray();
            if (ranked.Length < 2)
                return false;

            // Overlapping root carriers must form one factor, not two
            // independent SLOTs. An exact bounded union can cover several
            // overlapping candidate contexts, leaving other disjoint carrier
            // groups free to become separate factors. Only combine through
            // the same state/probability/dependency validator used for
            // ordinary canonical joint contexts.
            var possibleFactors = ranked
                .Select(item => item.Descriptor)
                .ToList();
            // Greedy expansion from one candidate can skip a smaller
            // overlapping-carrier union which leaves room for another
            // independent factor. Generate bounded pair seeds through the
            // same exact-state and dependency validators as ordinary unions.
            const int maxPairLookaheadSources = 32;
            const int maxPairLookaheadSeeds = 96;
            var expansionSeeds = ranked.Take(24)
                .Select(item => item.Descriptor)
                .ToList();
            var pairSeedOptions = new Dictionary<
                string,
                (CrossRigidJointAlwaysPresentRewriteDescriptor Descriptor,
                    double Benefit)>(StringComparer.Ordinal);
            var lookaheadCandidates = ranked
                .Take(maxPairLookaheadSources)
                .ToArray();
            for (var leftIndex = 0;
                 leftIndex < lookaheadCandidates.Length;
                 leftIndex++)
            {
                var left = lookaheadCandidates[leftIndex].Descriptor;
                for (var rightIndex = leftIndex + 1;
                     rightIndex < lookaheadCandidates.Length;
                     rightIndex++)
                {
                    var right = lookaheadCandidates[rightIndex].Descriptor;
                    if (!left.SlotIndices.Intersect(
                            right.SlotIndices).Any() ||
                        !TryCombineCrossRigidCanonicalJointContexts(
                            state,
                            left,
                            right,
                            sourceVmdDocuments,
                            out var union) ||
                        union.CommonAncestorDimensions.Any(dimension =>
                            !union.SlotIndices.Contains(
                                dimension.CarrierSlotIndex)))
                    {
                        continue;
                    }

                    var key = BuildCrossRigidJointContextKey(
                        ranked[0].RootVmdPath, union);
                    if (pairSeedOptions.ContainsKey(key))
                        continue;

                    var benefit = ranked
                        .Where(item =>
                            IsCrossRigidJointDescriptorStructuralSuperset(
                                union, item.Descriptor))
                        .Sum(item =>
                            benefitByContextKey.GetValueOrDefault(
                                item.ContextKey));
                    pairSeedOptions.Add(key, (union, benefit));
                }
            }

            var pairSeeds = pairSeedOptions
                .OrderByDescending(item => item.Value.Benefit)
                .ThenBy(item => item.Value.Descriptor.CombinationCount)
                .ThenBy(item => item.Key, StringComparer.Ordinal)
                .Take(maxPairLookaheadSeeds)
                .Select(item => item.Value.Descriptor)
                .ToArray();
            state.CrossRigidFactoredPairLookaheadSeeds += pairSeeds.Length;
            expansionSeeds.AddRange(pairSeeds);

            foreach (var seedDescriptor in expansionSeeds)
            {
                var current = seedDescriptor;
                // Retain the unexpanded pair: its smaller carrier set may
                // fit beside a better disjoint group than its greedy closure.
                possibleFactors.Add(current);
                while (true)
                {
                    CrossRigidJointAlwaysPresentRewriteDescriptor?
                        bestExpansion = null;
                    var currentCoverage = ranked
                        .Where(item =>
                            IsCrossRigidJointDescriptorStructuralSuperset(
                                current, item.Descriptor))
                        .Sum(item =>
                            benefitByContextKey.GetValueOrDefault(
                                item.ContextKey));
                    var bestGain = 0.0;
                    foreach (var candidate in ranked.Take(48))
                    {
                        if (IsCrossRigidJointDescriptorStructuralSuperset(
                                current, candidate.Descriptor) ||
                            !current.SlotIndices.Intersect(
                                candidate.Descriptor.SlotIndices).Any() ||
                            !TryCombineCrossRigidCanonicalJointContexts(
                                state,
                                current,
                                candidate.Descriptor,
                                sourceVmdDocuments,
                                out var expansion) ||
                            expansion.CommonAncestorDimensions.Any(dimension =>
                                !expansion.SlotIndices.Contains(
                                    dimension.CarrierSlotIndex)))
                        {
                            continue;
                        }

                        var expandedCoverage = ranked
                            .Where(item =>
                                IsCrossRigidJointDescriptorStructuralSuperset(
                                    expansion, item.Descriptor))
                            .Sum(item =>
                                benefitByContextKey.GetValueOrDefault(
                                    item.ContextKey));
                        var gain = expandedCoverage - currentCoverage;
                        if (gain <= AtlasValueGateExpectedDrawEpsilon ||
                            gain < bestGain -
                                AtlasValueGateExpectedDrawEpsilon ||
                            (Math.Abs(gain - bestGain) <=
                                 AtlasValueGateExpectedDrawEpsilon &&
                             bestExpansion != null &&
                             expansion.CombinationCount >=
                             bestExpansion.CombinationCount))
                        {
                            continue;
                        }

                        bestExpansion = expansion;
                        bestGain = gain;
                    }

                    if (bestExpansion == null)
                        break;
                    current = bestExpansion;
                }

                possibleFactors.Add(current);
            }

            var options = possibleFactors
                .Select(descriptor => new
                {
                    Descriptor = descriptor,
                    Benefit = ranked
                        .Where(item =>
                            IsCrossRigidJointDescriptorStructuralSuperset(
                                descriptor, item.Descriptor))
                        .Sum(item =>
                            benefitByContextKey.GetValueOrDefault(
                                item.ContextKey)),
                    Key = BuildCrossRigidJointContextKey(
                        ranked[0].RootVmdPath, descriptor),
                })
                .GroupBy(option => option.Key, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderByDescending(option => option.Benefit)
                .ThenBy(option => option.Descriptor.CombinationCount)
                .ThenBy(option => option.Key, StringComparer.Ordinal)
                .Take(96)
                .ToArray();

            // A single greedy largest-benefit factor can block two disjoint
            // smaller groups (AB versus AC+BD). Compare bounded alternatives
            // from each promising seed, including synthesized union factors.
            var bestBenefit = double.NegativeInfinity;
            var bestStateCount = long.MaxValue;
            CrossRigidJointAlwaysPresentRewriteDescriptor[] best = [];
            foreach (var seed in options.Take(64))
            {
                var chosen =
                    new List<CrossRigidJointAlwaysPresentRewriteDescriptor>();
                var usedSlots = new HashSet<int>();
                var benefit = 0.0;
                long stateCount = 0;
                foreach (var option in new[] { seed }
                             .Concat(options.Where(option =>
                                 !option.Key.Equals(
                                     seed.Key, StringComparison.Ordinal))))
                {
                    if (option.Descriptor.SlotIndices.Any(
                            usedSlots.Contains))
                        continue;

                    chosen.Add(option.Descriptor);
                    usedSlots.UnionWith(option.Descriptor.SlotIndices);
                    benefit += option.Benefit;
                    stateCount += option.Descriptor.CombinationCount;
                }

                if (chosen.Count < 2 ||
                    benefit < bestBenefit -
                        AtlasValueGateExpectedDrawEpsilon ||
                    (Math.Abs(benefit - bestBenefit) <=
                         AtlasValueGateExpectedDrawEpsilon &&
                     stateCount >= bestStateCount))
                    continue;

                bestBenefit = benefit;
                bestStateCount = stateCount;
                best = chosen.ToArray();
            }

            // The seeded greedy pass above is a safe baseline, but one
            // high-benefit factor can mask two smaller disjoint factors.
            // Search exact disjoint root-slot sets with bounded states.
            // Only change the factoring if this strictly improves covered
            // candidate benefit (or ties at fewer weighted states).
            const int maxPackingStates = 4096;
            var packingStates = new Dictionary<
                (ulong Slots, int FactorCount),
                (double Benefit, long StateCount, int[] OptionIndices)>
            {
                [(0UL, 0)] = (0, 0, []),
            };
            for (var optionIndex = 0; optionIndex < options.Length;
                 optionIndex++)
            {
                var option = options[optionIndex];
                ulong mask = 0;
                var representable = true;
                foreach (var slot in option.Descriptor.SlotIndices)
                {
                    if (slot < 1 || slot > 63)
                    {
                        representable = false;
                        break;
                    }

                    mask |= 1UL << (slot - 1);
                }

                if (!representable || mask == 0)
                    continue;

                var nextStates = new Dictionary<
                    (ulong Slots, int FactorCount),
                    (double Benefit, long StateCount, int[] OptionIndices)>(
                        packingStates);
                foreach (var (used, selection) in packingStates)
                {
                    if ((used.Slots & mask) != 0)
                        continue;

                    var newKey = (
                        Slots: used.Slots | mask,
                        FactorCount: Math.Min(2, used.FactorCount + 1));
                    var newBenefit = selection.Benefit + option.Benefit;
                    var newStateCount =
                        selection.StateCount +
                        option.Descriptor.CombinationCount;
                    if (nextStates.TryGetValue(newKey, out var previous) &&
                        (previous.Benefit >
                             newBenefit + AtlasValueGateExpectedDrawEpsilon ||
                         (Math.Abs(previous.Benefit - newBenefit) <=
                              AtlasValueGateExpectedDrawEpsilon &&
                          previous.StateCount <= newStateCount)))
                    {
                        continue;
                    }

                    nextStates[newKey] = (
                        newBenefit,
                        newStateCount,
                        selection.OptionIndices.Append(optionIndex).ToArray());
                }

                if (nextStates.Count > maxPackingStates)
                {
                    // Keep promising packings and always retain the empty
                    // starting point for later disjoint alternatives.
                    var reduced = nextStates
                        .OrderByDescending(item => item.Value.Benefit)
                        .ThenBy(item => item.Value.StateCount)
                        .Take(maxPackingStates - 1)
                        .ToDictionary(item => item.Key, item => item.Value);
                    reduced.TryAdd((0UL, 0), (0, 0, []));
                    nextStates = reduced;
                }

                packingStates = nextStates;
            }

            var packingImproved = false;
            foreach (var (key, selection) in packingStates)
            {
                if (key.FactorCount < 2 ||
                    selection.Benefit < bestBenefit -
                        AtlasValueGateExpectedDrawEpsilon ||
                    (Math.Abs(selection.Benefit - bestBenefit) <=
                         AtlasValueGateExpectedDrawEpsilon &&
                     selection.StateCount >= bestStateCount))
                {
                    continue;
                }

                bestBenefit = selection.Benefit;
                bestStateCount = selection.StateCount;
                best = selection.OptionIndices
                    .Select(index => options[index].Descriptor)
                    .ToArray();
                packingImproved = true;
            }

            if (best.Length < 2)
                return false;
            if (packingImproved)
                state.CrossRigidFactoredPackingSearchImprovements++;
            factors = best;
            return true;
        }

        private const string CrossRigidFactoredParentMarker =
            "factored-independent-root-slots";

        private static bool TryGetCrossRigidFactoredDescriptor(
            BatchState state,
            string originalParentKey,
            CrossRigidJointAlwaysPresentRewriteDescriptor descriptor,
            out CrossRigidJointAlwaysPresentRewriteDescriptor factor)
        {
            if (state.CrossRigidFactoredJointDescriptorsByParentLocation
                    .TryGetValue(originalParentKey, out var factors))
            {
                foreach (var candidate in factors)
                {
                    if (IsCrossRigidJointDescriptorStructuralSuperset(
                            candidate, descriptor))
                    {
                        factor = candidate;
                        return true;
                    }
                }
            }
            factor = null!;
            return false;
        }

        private static string GetCrossRigidEffectiveJointParentKey(
            BatchState state,
            string rootVmdPath,
            CrossRigidJointAlwaysPresentRewriteDescriptor descriptor)
        {
            var originalKey = BuildCrossRigidJointParentLocationKey(
                rootVmdPath, descriptor);
            return TryGetCrossRigidFactoredDescriptor(
                    state, originalKey, descriptor, out var factor)
                ? originalKey + "\u001ffactor-slots=" +
                  string.Join(",", factor.SlotIndices)
                : originalKey;
        }

        // The standard writer uses one Cartesian context at a structural
        // parent. When the entire union is too large, find the most
        // valuable compatible subset and leave other contexts competing
        // through their original structural-parent keys. Benefit comes from
        // the same per-context scenario draw estimates used by the
        // generated-payload portfolio; raw context count is only a tie-break.
        // No new states are admitted without the existing exact-probability,
        // dependency and 1,024-state union validators.
        private static bool
            TryBuildCrossRigidBestPartialCanonicalJointContext(
                BatchState state,
                IReadOnlyList<CrossRigidJointAlwaysPresentRewriteDescriptor>
                    candidates,
                string rootVmdPath,
                IReadOnlyDictionary<string, double> benefitByContextKey,
                Dictionary<string, XmlDocument> sourceVmdDocuments,
                out CrossRigidJointAlwaysPresentRewriteDescriptor descriptor,
                out int coveredContextCount)
        {
            descriptor = null!;
            coveredContextCount = 0;
            if (candidates.Count < 3)
                return false;

            var weightedCandidates = candidates
                .Select(candidate => (
                    Descriptor: candidate,
                    Benefit: benefitByContextKey.GetValueOrDefault(
                        BuildCrossRigidJointContextKey(
                            rootVmdPath, candidate))))
                .ToArray();

            (double Benefit, int Count) Score(
                CrossRigidJointAlwaysPresentRewriteDescriptor context)
            {
                var benefit = 0.0;
                var count = 0;
                foreach (var item in weightedCandidates)
                {
                    if (!IsCrossRigidJointDescriptorStructuralSuperset(
                            context, item.Descriptor))
                        continue;

                    benefit += item.Benefit;
                    count++;
                }

                return (benefit, count);
            }

            var bestBenefit = double.NegativeInfinity;
            for (var seedIndex = 0;
                 seedIndex < candidates.Count;
                 seedIndex++)
            {
                var current = candidates[seedIndex];
                var (currentBenefit, currentCoverage) = Score(current);

                // Try all seeds: the highest-count cohort may exclude a
                // more valuable compatible cohort that fits the same cap.
                while (true)
                {
                    CrossRigidJointAlwaysPresentRewriteDescriptor?
                        bestExpansion = null;
                    var bestExpansionBenefit = currentBenefit;
                    var bestExpansionCoverage = currentCoverage;
                    foreach (var candidate in candidates)
                    {
                        if (IsCrossRigidJointDescriptorStructuralSuperset(
                                current, candidate) ||
                            !TryCombineCrossRigidCanonicalJointContexts(
                                state,
                                current,
                                candidate,
                                sourceVmdDocuments,
                                out var expansion))
                        {
                            continue;
                        }

                        var (expansionBenefit, expansionCoverage) =
                            Score(expansion);
                        if (expansionBenefit >
                                bestExpansionBenefit +
                                AtlasValueGateExpectedDrawEpsilon ||
                            (Math.Abs(
                                 expansionBenefit -
                                 bestExpansionBenefit) <=
                             AtlasValueGateExpectedDrawEpsilon &&
                             (expansionCoverage > bestExpansionCoverage ||
                              (expansionCoverage == bestExpansionCoverage &&
                               bestExpansion != null &&
                               expansion.CombinationCount <
                               bestExpansion.CombinationCount))))
                        {
                            bestExpansion = expansion;
                            bestExpansionBenefit = expansionBenefit;
                            bestExpansionCoverage = expansionCoverage;
                        }
                    }

                    if (bestExpansion == null)
                        break;

                    current = bestExpansion;
                    currentBenefit = bestExpansionBenefit;
                    currentCoverage = bestExpansionCoverage;
                }

                if (currentCoverage <= 1 ||
                    currentBenefit <
                        bestBenefit -
                        AtlasValueGateExpectedDrawEpsilon ||
                    (Math.Abs(
                         currentBenefit -
                         bestBenefit) <=
                     AtlasValueGateExpectedDrawEpsilon &&
                     (currentCoverage < coveredContextCount ||
                      (currentCoverage == coveredContextCount &&
                       descriptor != null &&
                       current.CombinationCount >=
                       descriptor.CombinationCount))))
                {
                    continue;
                }

                descriptor = current;
                bestBenefit = currentBenefit;
                coveredContextCount = currentCoverage;
            }

            // A greedy expansion can spend the remaining weighted-state
            // budget on the wrong overlapping context. Keep a bounded
            // frontier of alternative unions instead of committing to only
            // the locally highest-benefit expansion. The existing union
            // validator still enforces dependency structure, exact weights,
            // and the 1,024-state cap for every candidate.
            const int maxPartialCanonicalBeamWidth = 64;
            const int maxPartialCanonicalBeamDepth = 3;
            var greedyDescriptor = descriptor;
            var greedyBenefit = bestBenefit;
            var greedyCoveredCount = coveredContextCount;
            var beam = candidates
                .Select(candidate =>
                {
                    var (benefit, count) = Score(candidate);
                    return (
                        Descriptor: candidate,
                        Benefit: benefit,
                        Count: count);
                })
                .OrderByDescending(item => item.Benefit)
                .ThenByDescending(item => item.Count)
                .ThenBy(item => item.Descriptor.CombinationCount)
                .Take(maxPartialCanonicalBeamWidth)
                .ToArray();
            var visited = new HashSet<string>(
                beam.Select(item => BuildCrossRigidJointContextKey(
                    rootVmdPath, item.Descriptor)),
                StringComparer.Ordinal);
            for (var depth = 0;
                 depth < maxPartialCanonicalBeamDepth && beam.Length != 0;
                 depth++)
            {
                var next = new List<(
                    CrossRigidJointAlwaysPresentRewriteDescriptor Descriptor,
                    double Benefit,
                    int Count)>();
                foreach (var partial in beam)
                {
                    foreach (var candidate in candidates)
                    {
                        if (IsCrossRigidJointDescriptorStructuralSuperset(
                                partial.Descriptor, candidate) ||
                            !TryCombineCrossRigidCanonicalJointContexts(
                                state,
                                partial.Descriptor,
                                candidate,
                                sourceVmdDocuments,
                                out var expansion))
                        {
                            continue;
                        }

                        var (benefit, count) = Score(expansion);
                        if (count <= partial.Count ||
                            !visited.Add(BuildCrossRigidJointContextKey(
                                rootVmdPath, expansion)))
                        {
                            continue;
                        }

                        next.Add((expansion, benefit, count));
                        if (count <= 1 ||
                            benefit < bestBenefit -
                                AtlasValueGateExpectedDrawEpsilon ||
                            (Math.Abs(benefit - bestBenefit) <=
                                 AtlasValueGateExpectedDrawEpsilon &&
                             (count < coveredContextCount ||
                              (count == coveredContextCount &&
                               descriptor != null &&
                               expansion.CombinationCount >=
                                   descriptor.CombinationCount))))
                        {
                            continue;
                        }

                        descriptor = expansion;
                        bestBenefit = benefit;
                        coveredContextCount = count;
                    }
                }

                state.CrossRigidPartialCanonicalBeamUnionCandidates +=
                    next.Count;
                beam = next
                    .OrderByDescending(item => item.Benefit)
                    .ThenByDescending(item => item.Count)
                    .ThenBy(item => item.Descriptor.CombinationCount)
                    .Take(maxPartialCanonicalBeamWidth)
                    .ToArray();
            }

            if (coveredContextCount > 1 &&
                (bestBenefit > greedyBenefit +
                     AtlasValueGateExpectedDrawEpsilon ||
                 (Math.Abs(bestBenefit - greedyBenefit) <=
                      AtlasValueGateExpectedDrawEpsilon &&
                  (coveredContextCount > greedyCoveredCount ||
                   (coveredContextCount == greedyCoveredCount &&
                    greedyDescriptor != null &&
                    descriptor.CombinationCount <
                        greedyDescriptor.CombinationCount)))))
            {
                state.CrossRigidPartialCanonicalBeamImprovements++;
            }

            return coveredContextCount > 1;
        }

        private static bool TryCombineCrossRigidCanonicalJointContexts(
            BatchState state,
            CrossRigidJointAlwaysPresentRewriteDescriptor left,
            CrossRigidJointAlwaysPresentRewriteDescriptor right,
            Dictionary<string, XmlDocument> sourceVmdDocuments,
            out CrossRigidJointAlwaysPresentRewriteDescriptor descriptor)
        {
            if (IsCrossRigidJointDescriptorStructuralSuperset(
                    left, right))
            {
                descriptor = left;
                return true;
            }

            if (IsCrossRigidJointDescriptorStructuralSuperset(
                    right, left))
            {
                descriptor = right;
                return true;
            }

            if (left.CommonAncestorDimensions.Length == 0 &&
                right.CommonAncestorDimensions.Length == 0)
            {
                return TryBuildCrossRigidCanonicalSiblingSlotUnion(
                    state,
                    [left, right],
                    sourceVmdDocuments,
                    out descriptor);
            }

            if (left.CommonAncestorDimensions.Length != 0 &&
                right.CommonAncestorDimensions.Length != 0)
            {
                return TryBuildCrossRigidCanonicalConditionalDimensionUnion(
                    state,
                    [left, right],
                    sourceVmdDocuments,
                    out descriptor);
            }

            // Independent sibling choices can coexist with conditional
            // dimensions only when they do not share a carrier slot.
            return TryBuildCrossRigidCanonicalMixedDimensionUnion(
                state,
                [left, right],
                sourceVmdDocuments,
                out descriptor);
        }

        private static bool TryBuildCrossRigidCanonicalSiblingSlotUnion(
            BatchState state,
            IEnumerable<CrossRigidJointAlwaysPresentRewriteDescriptor>
                candidateDescriptors,
            Dictionary<string, XmlDocument> sourceVmdDocuments,
            out CrossRigidJointAlwaysPresentRewriteDescriptor descriptor)
        {
            descriptor = null!;
            var candidates = candidateDescriptors.ToArray();
            if (candidates.Length < 2 ||
                candidates.Any(candidate =>
                    candidate.CommonAncestorDimensions.Length != 0))
            {
                return false;
            }

            var first = candidates[0];
            if (candidates.Skip(1).Any(candidate =>
                    !Normalize(candidate.AnchorVmdPath).Equals(
                        Normalize(first.AnchorVmdPath),
                        StringComparison.OrdinalIgnoreCase) ||
                    !candidate.ParentXmlPath.Equals(
                        first.ParentXmlPath,
                        StringComparison.Ordinal) ||
                    !BuildCrossRigidReferenceChainSignature(
                        candidate.AnchorReferenceChain).Equals(
                        BuildCrossRigidReferenceChainSignature(
                            first.AnchorReferenceChain),
                        StringComparison.Ordinal)))
            {
                return false;
            }

            var slotIndices = candidates
                .SelectMany(candidate => candidate.SlotIndices)
                .Distinct()
                .OrderBy(index => index)
                .ToArray();

            if (!TryGetCrossRigidSourceVmdDocument(
                    state,
                    first.AnchorVmdPath,
                    sourceVmdDocuments,
                    out var anchorDocument,
                    out _) ||
                anchorDocument.SelectSingleNode(
                    first.ParentXmlPath) is not XmlElement parentElement)
            {
                return false;
            }

            long weightedStates = 1;
            foreach (var slotIndex in slotIndices)
            {
                if (parentElement.SelectSingleNode(
                        $"SLOT[{slotIndex}]") is not XmlElement slotElement)
                {
                    return false;
                }

                var optional = ParseVmdSlotProbability(
                    slotElement.GetAttribute("probability")) <
                    1.0 - AtlasValueGateExpectedDrawEpsilon;
                if (optional &&
                    !IsSupportedCrossRigidOptionalJointSlot(slotElement))
                {
                    return false;
                }

                var alternatives = GetCrossRigidJointAlternatives(
                    slotElement,
                    slotIndex);
                if (alternatives.Length <= 1)
                    return false;

                var factor = alternatives.Sum(alternative =>
                    (long)alternative.Multiplicity);
                if (factor <= 1 ||
                    weightedStates >
                    MaxCrossRigidJointStateCombinations / factor)
                {
                    return false;
                }

                weightedStates *= factor;
            }

            // The canonical descriptor is only the joint context's shape.
            // Each emitted occurrence retains its own source selections
            // and is projected onto every matching union state.
            var canonicalDescriptor = first with
            {
                SlotIndices = slotIndices,
                CombinationCount = (int)weightedStates,
            };
            descriptor = canonicalDescriptor;
            return candidates.All(candidate =>
                IsCrossRigidJointDescriptorStructuralSuperset(
                    canonicalDescriptor,
                    candidate));
        }

        // Lift independent sibling alternatives to root dimensions beside
        // conditional dimensions. A conditional carrier (even when fixed)
        // must remain under its original dependency graph.
        private static bool TryBuildCrossRigidCanonicalMixedDimensionUnion(
            BatchState state,
            IEnumerable<CrossRigidJointAlwaysPresentRewriteDescriptor>
                candidateDescriptors,
            Dictionary<string, XmlDocument> sourceVmdDocuments,
            out CrossRigidJointAlwaysPresentRewriteDescriptor descriptor)
        {
            descriptor = null!;
            var candidates = candidateDescriptors.ToArray();
            var direct = candidates.Where(candidate =>
                candidate.CommonAncestorDimensions.Length == 0).ToArray();
            var conditional = candidates.Where(candidate =>
                candidate.CommonAncestorDimensions.Length != 0).ToArray();
            if (direct.Length == 0 || conditional.Length == 0)
                return false;

            var first = candidates[0];
            if (candidates.Skip(1).Any(candidate =>
                    !Normalize(candidate.AnchorVmdPath).Equals(
                        Normalize(first.AnchorVmdPath),
                        StringComparison.OrdinalIgnoreCase) ||
                    !candidate.ParentXmlPath.Equals(
                        first.ParentXmlPath,
                        StringComparison.Ordinal) ||
                    !BuildCrossRigidReferenceChainSignature(
                        candidate.AnchorReferenceChain).Equals(
                        BuildCrossRigidReferenceChainSignature(
                            first.AnchorReferenceChain),
                        StringComparison.Ordinal)))
            {
                return false;
            }

            if (!TryGetCrossRigidSourceVmdDocument(
                    state,
                    first.AnchorVmdPath,
                    sourceVmdDocuments,
                    out var anchorDocument,
                    out _) ||
                anchorDocument.SelectSingleNode(
                    first.ParentXmlPath) is not XmlElement parentElement)
            {
                return false;
            }

            var directSlots = direct
                .SelectMany(candidate => candidate.SlotIndices)
                .Distinct()
                .OrderBy(index => index)
                .ToArray();
            var conditionalCarrierSlots = conditional
                .SelectMany(candidate => candidate.SlotIndices)
                .ToHashSet();
            if (directSlots.Any(conditionalCarrierSlots.Contains))
                return false;

            var rootDimensions =
                new Dictionary<int, CrossRigidJointSelectionDimension>();
            foreach (var slotIndex in directSlots)
            {
                if (parentElement.SelectSingleNode(
                        "SLOT[" + slotIndex + "]") is not XmlElement slot)
                    return false;
                if (ParseVmdSlotProbability(
                        slot.GetAttribute("probability")) <
                        1.0 - AtlasValueGateExpectedDrawEpsilon &&
                    !IsSupportedCrossRigidOptionalJointSlot(slot))
                {
                    return false;
                }

                var key = BuildCrossRigidJointSelectionDimensionKey(
                    first.AnchorVmdPath,
                    first.ParentXmlPath,
                    slotIndex);
                var alternatives =
                    GetCrossRigidJointAlternatives(slot, slotIndex)
                        .Select(alternative =>
                            new CrossRigidJointDimensionAlternative(
                                key,
                                alternative.IsReference,
                                alternative.AlternativeIndex,
                                alternative.Multiplicity))
                        .ToArray();
                if (alternatives.Length <= 1)
                    return false;

                rootDimensions[slotIndex] =
                    new CrossRigidJointSelectionDimension(
                        key,
                        first.AnchorVmdPath,
                        first.ParentXmlPath,
                        slotIndex,
                        slotIndex,
                        null,
                        Array.Empty<CrossRigidVmdReferenceHop>(),
                        alternatives);
            }

            // The original occurrence descriptors are retained for matching.
            // Only the canonical context shapes are lifted to dimensions.
            var lifted = candidates.Select(candidate =>
                candidate.CommonAncestorDimensions.Length != 0
                    ? candidate
                    : candidate with
                    {
                        CommonAncestorDimensions = candidate.SlotIndices
                            .Select(index => rootDimensions[index])
                            .ToArray(),
                    }).ToArray();
            if (!TryBuildCrossRigidCanonicalConditionalDimensionUnion(
                    state,
                    lifted,
                    sourceVmdDocuments,
                    out var union) ||
                !candidates.All(candidate =>
                    IsCrossRigidJointDescriptorStructuralSuperset(
                        union, candidate)))
            {
                return false;
            }

            descriptor = union;
            return true;
        }

        private static bool IsCrossRigidIndependentRootDimension(
            CrossRigidJointSelectionDimension dimension,
            CrossRigidJointAlwaysPresentRewriteDescriptor descriptor,
            int slotIndex)
            => dimension.SlotIndex == slotIndex &&
               dimension.CarrierSlotIndex == slotIndex &&
               dimension.ParentDimensionKey == null &&
               dimension.ReferenceChainFromAnchor.Length == 0 &&
               string.IsNullOrWhiteSpace(
                   dimension.LocalDimensionSlotRelativeXmlPath) &&
               Normalize(dimension.OwnerVmdPath).Equals(
                   Normalize(descriptor.AnchorVmdPath),
                   StringComparison.OrdinalIgnoreCase) &&
               dimension.ParentXmlPath.Equals(
                   descriptor.ParentXmlPath,
                   StringComparison.Ordinal);

        // Reconcile compatible, overlapping nested choice graphs at the
        // same parent. Every candidate describes a valid subset of the
        // source VMD's structural dependencies; combining their dimensions
        // must preserve the exact parent-child conditions, not flatten them
        // into independent optional appearances.
        private static bool
            TryBuildCrossRigidCanonicalConditionalDimensionUnion(
                BatchState state,
                IEnumerable<CrossRigidJointAlwaysPresentRewriteDescriptor>
                    candidateDescriptors,
                Dictionary<string, XmlDocument> sourceVmdDocuments,
                out CrossRigidJointAlwaysPresentRewriteDescriptor descriptor)
        {
            descriptor = null!;
            var candidates = candidateDescriptors.ToArray();
            if (candidates.Length < 2 ||
                candidates.Any(candidate =>
                    candidate.CommonAncestorDimensions.Length == 0))
            {
                return false;
            }

            var first = candidates[0];
            if (candidates.Skip(1).Any(candidate =>
                    !Normalize(candidate.AnchorVmdPath).Equals(
                        Normalize(first.AnchorVmdPath),
                        StringComparison.OrdinalIgnoreCase) ||
                    !candidate.ParentXmlPath.Equals(
                        first.ParentXmlPath,
                        StringComparison.Ordinal) ||
                    !BuildCrossRigidReferenceChainSignature(
                        candidate.AnchorReferenceChain).Equals(
                        BuildCrossRigidReferenceChainSignature(
                            first.AnchorReferenceChain),
                        StringComparison.Ordinal)))
            {
                return false;
            }

            var byDimensionKey =
                new Dictionary<string, CrossRigidJointSelectionDimension>(
                    StringComparer.Ordinal);
            foreach (var dimension in candidates.SelectMany(candidate =>
                         candidate.CommonAncestorDimensions))
            {
                if (byDimensionKey.TryGetValue(
                        dimension.DimensionKey, out var existing))
                {
                    if (!AreEquivalentCrossRigidJointDimensions(
                            existing, dimension))
                    {
                        return false;
                    }
                }
                else
                {
                    byDimensionKey.Add(dimension.DimensionKey, dimension);
                }
            }

            if (byDimensionKey.Values.Any(dimension =>
                    dimension.ParentDimensionKey != null &&
                    !byDimensionKey.ContainsKey(
                        dimension.ParentDimensionKey)))
            {
                return false;
            }

            var slotIndices = candidates
                .SelectMany(candidate => candidate.SlotIndices)
                .Distinct()
                .OrderBy(index => index)
                .ToArray();
            if (!TryGetCrossRigidSourceVmdDocument(
                    state,
                    first.AnchorVmdPath,
                    sourceVmdDocuments,
                    out var anchorDocument,
                    out _) ||
                anchorDocument.SelectSingleNode(
                    first.ParentXmlPath) is not XmlElement parentElement)
            {
                return false;
            }

            // Match the materializer's carrier-slot restrictions. A
            // probability-less-than-one carrier must be modeled as an
            // explicit direct optional dimension, otherwise cloning it
            // inside a probability-one wrapper would change activation.
            foreach (var slotIndex in slotIndices)
            {
                if (parentElement.SelectSingleNode(
                        $"SLOT[{slotIndex}]") is not XmlElement slot)
                {
                    return false;
                }

                if (ParseVmdSlotProbability(
                        slot.GetAttribute("probability")) >=
                    1.0 - AtlasValueGateExpectedDrawEpsilon)
                {
                    continue;
                }

                if (!IsSupportedCrossRigidOptionalJointSlot(slot) ||
                    !byDimensionKey.Values.Any(dimension =>
                        dimension.CarrierSlotIndex == slotIndex &&
                        dimension.SlotIndex == slotIndex &&
                        Normalize(dimension.OwnerVmdPath).Equals(
                            Normalize(first.AnchorVmdPath),
                            StringComparison.OrdinalIgnoreCase) &&
                        dimension.ParentXmlPath.Equals(
                            first.ParentXmlPath,
                            StringComparison.Ordinal) &&
                        dimension.ReferenceChainFromAnchor.Length == 0 &&
                        string.IsNullOrWhiteSpace(
                            dimension.LocalDimensionSlotRelativeXmlPath) &&
                        dimension.Alternatives.Any(alternative =>
                            alternative.AlternativeIndex == 0)))
                {
                    return false;
                }
            }

            var dimensions = byDimensionKey.Values
                .OrderBy(dimension =>
                    dimension.DimensionKey,
                    StringComparer.Ordinal)
                .ToArray();
            if (!TryEnumerateCrossRigidConditionalJointStates(
                    dimensions,
                    out var combinations,
                    out _))
            {
                return false;
            }

            long weightedStates = 0;
            foreach (var combination in combinations)
            {
                long multiplicity = 1;
                foreach (var alternative in combination)
                {
                    var factor = Math.Max(1, alternative.Multiplicity);
                    if (multiplicity >
                        MaxCrossRigidJointStateCombinations / factor)
                    {
                        return false;
                    }

                    multiplicity *= factor;
                }

                weightedStates += multiplicity;
                if (weightedStates >
                    MaxCrossRigidJointStateCombinations)
                {
                    return false;
                }
            }

            if (weightedStates <= 0)
                return false;

            var union = first with
            {
                SlotIndices = slotIndices,
                CombinationCount = (int)weightedStates,
                CommonAncestorDimensions = dimensions,
            };
            if (!candidates.All(candidate =>
                    IsCrossRigidJointDescriptorStructuralSuperset(
                        union,
                        candidate)))
            {
                return false;
            }

            descriptor = union;
            return true;
        }

        private static bool
            IsCrossRigidJointDescriptorStructuralSuperset(
                CrossRigidJointAlwaysPresentRewriteDescriptor candidate,
                CrossRigidJointAlwaysPresentRewriteDescriptor other)
        {
            if (!Normalize(candidate.AnchorVmdPath).Equals(
                    Normalize(other.AnchorVmdPath),
                    StringComparison.OrdinalIgnoreCase) ||
                !candidate.ParentXmlPath.Equals(
                    other.ParentXmlPath,
                    StringComparison.Ordinal) ||
                !BuildCrossRigidReferenceChainSignature(
                        candidate.AnchorReferenceChain)
                    .Equals(
                        BuildCrossRigidReferenceChainSignature(
                            other.AnchorReferenceChain),
                        StringComparison.Ordinal))
            {
                return false;
            }

            var candidateUsesDimensions =
                candidate.CommonAncestorDimensions.Length != 0;
            var otherUsesDimensions =
                other.CommonAncestorDimensions.Length != 0;
            if (candidateUsesDimensions != otherUsesDimensions)
            {
                // A direct context projects onto conditional states only
                // when every source selection is an independent root choice.
                if (!candidateUsesDimensions ||
                    other.SlotIndices.Any(slotIndex =>
                        !candidate.CommonAncestorDimensions.Any(dimension =>
                            IsCrossRigidIndependentRootDimension(
                                dimension, candidate, slotIndex))) ||
                    other.SourceSelections.Any(selection =>
                        !candidate.CommonAncestorDimensions.Any(dimension =>
                            dimension.DimensionKey.Equals(
                                BuildCrossRigidJointSourceSelectionDimensionKey(
                                    selection),
                                StringComparison.Ordinal) &&
                            IsCrossRigidIndependentRootDimension(
                                dimension, candidate, selection.SlotIndex))))
                {
                    return false;
                }
            }

            var candidateSlots =
                candidate.SlotIndices.ToHashSet();
            if (other.SlotIndices.Any(slotIndex =>
                    !candidateSlots.Contains(slotIndex)))
            {
                return false;
            }

            if (!candidateUsesDimensions)
                return true;

            var candidateDimensions =
                candidate.CommonAncestorDimensions.ToDictionary(
                    dimension => dimension.DimensionKey,
                    StringComparer.Ordinal);
            foreach (var otherDimension in
                     other.CommonAncestorDimensions)
            {
                if (!candidateDimensions.TryGetValue(
                        otherDimension.DimensionKey,
                        out var candidateDimension) ||
                    !AreEquivalentCrossRigidJointDimensions(
                        candidateDimension,
                        otherDimension))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool AreEquivalentCrossRigidJointDimensions(
            CrossRigidJointSelectionDimension left,
            CrossRigidJointSelectionDimension right)
        {
            if (!Normalize(left.OwnerVmdPath).Equals(
                    Normalize(right.OwnerVmdPath),
                    StringComparison.OrdinalIgnoreCase) ||
                !left.ParentXmlPath.Equals(
                    right.ParentXmlPath,
                    StringComparison.Ordinal) ||
                left.SlotIndex != right.SlotIndex ||
                left.CarrierSlotIndex != right.CarrierSlotIndex ||
                !string.Equals(
                    left.ParentDimensionKey,
                    right.ParentDimensionKey,
                    StringComparison.Ordinal) ||
                left.ParentIsReference != right.ParentIsReference ||
                left.ParentAlternativeIndex != right.ParentAlternativeIndex ||
                !string.Equals(
                    left.LocalDimensionSlotRelativeXmlPath,
                    right.LocalDimensionSlotRelativeXmlPath,
                    StringComparison.Ordinal) ||
                !BuildCrossRigidReferenceChainSignature(
                        left.ReferenceChainFromAnchor)
                    .Equals(
                        BuildCrossRigidReferenceChainSignature(
                            right.ReferenceChainFromAnchor),
                        StringComparison.Ordinal))
            {
                return false;
            }

            var leftAlternatives =
                left.Alternatives
                    .OrderBy(alternative =>
                        alternative.IsReference)
                    .ThenBy(alternative =>
                        alternative.AlternativeIndex)
                    .ThenBy(alternative =>
                        alternative.Multiplicity)
                    .Select(alternative =>
                        $"{(alternative.IsReference ? "r" : "m")}:" +
                        $"{alternative.AlternativeIndex}:" +
                        $"{alternative.Multiplicity}")
                    .ToArray();
            var rightAlternatives =
                right.Alternatives
                    .OrderBy(alternative =>
                        alternative.IsReference)
                    .ThenBy(alternative =>
                        alternative.AlternativeIndex)
                    .ThenBy(alternative =>
                        alternative.Multiplicity)
                    .Select(alternative =>
                        $"{(alternative.IsReference ? "r" : "m")}:" +
                        $"{alternative.AlternativeIndex}:" +
                        $"{alternative.Multiplicity}")
                    .ToArray();
            return leftAlternatives.SequenceEqual(
                rightAlternatives,
                StringComparer.Ordinal);
        }

        private static CrossRigidJointAlwaysPresentRewriteDescriptor
            GetCrossRigidEffectiveJointDescriptor(
                BatchState state,
                string rootVmdPath,
                CrossRigidJointAlwaysPresentRewriteDescriptor descriptor)
        {
            var parentLocationKey =
                BuildCrossRigidJointParentLocationKey(
                    rootVmdPath,
                    descriptor);
            if (TryGetCrossRigidFactoredDescriptor(
                    state, parentLocationKey, descriptor,
                    out var factoredDescriptor))
                return factoredDescriptor;

            if (state.CrossRigidCanonicalJointDescriptorByParentLocation
                    .TryGetValue(
                        parentLocationKey,
                        out var canonicalDescriptor) &&
                IsCrossRigidJointDescriptorStructuralSuperset(
                    canonicalDescriptor,
                    descriptor))
            {
                return canonicalDescriptor;
            }

            return descriptor;
        }

        private static bool TryEnumerateCrossRigidConditionalJointStates(
            IReadOnlyList<CrossRigidJointSelectionDimension> dimensions,
            out CrossRigidJointDimensionAlternative[][] states,
            out string reason)
        {
            states = [];
            reason = string.Empty;

            // Resolve dependency parents before children, regardless of
            // chain depth. A missing parent or cycle is rejected rather than
            // ever enumerating a conditional child independently.
            var knownKeys = dimensions
                .Select(dimension => dimension.DimensionKey)
                .ToHashSet(StringComparer.Ordinal);
            var pending = dimensions
                .OrderBy(dimension => dimension.DimensionKey,
                    StringComparer.Ordinal)
                .ToList();
            var orderedDimensions =
                new List<CrossRigidJointSelectionDimension>();
            var resolvedKeys =
                new HashSet<string>(StringComparer.Ordinal);
            while (pending.Count != 0)
            {
                var nextIndex = pending.FindIndex(dimension =>
                    dimension.ParentDimensionKey == null ||
                    resolvedKeys.Contains(dimension.ParentDimensionKey));
                if (nextIndex < 0)
                {
                    reason =
                        "Conditional Cartesian dimension graph has an " +
                        "unresolved or cyclic parent";
                    return false;
                }

                var nextDimension = pending[nextIndex];
                pending.RemoveAt(nextIndex);
                orderedDimensions.Add(nextDimension);
                resolvedKeys.Add(nextDimension.DimensionKey);
            }

            var combinations =
                new List<CrossRigidJointDimensionAlternative[]>
                {
                    Array.Empty<CrossRigidJointDimensionAlternative>(),
                };
            foreach (var dimension in orderedDimensions)
            {
                if (dimension.ParentDimensionKey != null &&
                    !knownKeys.Contains(dimension.ParentDimensionKey))
                {
                    reason = "Conditional Cartesian dimension has no parent";
                    return false;
                }

                var expanded =
                    new List<CrossRigidJointDimensionAlternative[]>();
                foreach (var prefix in combinations)
                {
                    var active =
                        dimension.ParentDimensionKey == null ||
                        prefix.Any(alternative =>
                            alternative.DimensionKey.Equals(
                                dimension.ParentDimensionKey,
                                StringComparison.Ordinal) &&
                            alternative.IsReference ==
                                dimension.ParentIsReference &&
                            alternative.AlternativeIndex ==
                                dimension.ParentAlternativeIndex);
                    if (active)
                    {
                        foreach (var alternative in dimension.Alternatives)
                        {
                            expanded.Add(prefix.Append(alternative).ToArray());
                        }
                    }
                    else
                    {
                        // An inactive child has no appearance selection.
                        // Preserve the exact parent probability by folding
                        // the full child alternative weight into one
                        // synthetic state rather than multiplying variants
                        // that cannot exist under this parent selection.
                        expanded.Add(prefix.Append(
                            new CrossRigidJointDimensionAlternative(
                                dimension.DimensionKey,
                                false,
                                -1,
                                dimension.Alternatives.Sum(alternative =>
                                    alternative.Multiplicity))).ToArray());
                    }

                    if (expanded.Count >
                        MaxCrossRigidJointStateCombinations)
                    {
                        reason =
                            $"Conditional Cartesian state count exceeds " +
                            $"{MaxCrossRigidJointStateCombinations:N0}";
                        return false;
                    }
                }

                combinations = expanded;
            }

            states = combinations.ToArray();
            return true;
        }

        private static bool TryGetCrossRigidMatchingJointCombinations(
            BatchState state,
            CrossRigidJointAlwaysPresentRewriteDescriptor occurrenceDescriptor,
            CrossRigidJointAlwaysPresentRewriteDescriptor contextDescriptor,
            Dictionary<string, XmlDocument> sourceVmdDocuments,
            out CrossRigidJointMatchingCombination[] matchingCombinations,
            out string reason)
        {
            matchingCombinations = [];
            reason = string.Empty;
            if (!IsCrossRigidJointDescriptorStructuralSuperset(
                    contextDescriptor,
                    occurrenceDescriptor))
            {
                reason =
                    "Canonical joint context is not a structural superset of the analyzed occurrence";
                return false;
            }

            if (contextDescriptor.CommonAncestorDimensions.Length == 0)
            {
                if (!TryGetCrossRigidSourceVmdDocument(
                        state,
                        contextDescriptor.AnchorVmdPath,
                        sourceVmdDocuments,
                        out var anchorDocument,
                        out reason) ||
                    anchorDocument.SelectSingleNode(
                        contextDescriptor.ParentXmlPath) is
                    not XmlElement parentElement)
                {
                    if (string.IsNullOrWhiteSpace(reason))
                    {
                        reason =
                            "Canonical joint parent could not be resolved";
                    }

                    return false;
                }

                var constraints =
                    new Dictionary<
                        int,
                        (bool IsReference, int AlternativeIndex)>();
                foreach (var selection in
                         occurrenceDescriptor.SourceSelections)
                {
                    if (!contextDescriptor.SlotIndices.Contains(
                            selection.SlotIndex))
                    {
                        continue;
                    }

                    var value =
                        (
                            selection.IsReference,
                            selection.AlternativeIndex);
                    if (constraints.TryGetValue(
                            selection.SlotIndex,
                            out var existing) &&
                        existing != value)
                    {
                        reason =
                            "Analyzed occurrence requires incompatible alternatives in one canonical joint slot";
                        return false;
                    }

                    constraints[selection.SlotIndex] =
                        value;
                }

                var combinations =
                    new List<CrossRigidJointAlternative[]>
                    {
                        Array.Empty<CrossRigidJointAlternative>(),
                    };
                foreach (var slotIndex in
                         contextDescriptor.SlotIndices)
                {
                    if (parentElement.SelectSingleNode(
                            $"SLOT[{slotIndex}]") is
                        not XmlElement slotElement)
                    {
                        reason =
                            $"Canonical joint source slot {slotIndex} could not be resolved";
                        return false;
                    }

                    var alternatives =
                        GetCrossRigidJointAlternatives(
                            slotElement,
                            slotIndex);
                    if (alternatives.Length <= 1)
                    {
                        reason =
                            $"Canonical joint source slot {slotIndex} no longer has alternatives";
                        return false;
                    }

                    var expanded =
                        new List<CrossRigidJointAlternative[]>();
                    foreach (var prefix in combinations)
                    {
                        foreach (var alternative in alternatives)
                        {
                            expanded.Add(
                                prefix
                                    .Append(alternative)
                                    .ToArray());
                            if (expanded.Count >
                                MaxCrossRigidJointStateCombinations)
                            {
                                reason =
                                    $"Canonical joint Cartesian product exceeds " +
                                    $"{MaxCrossRigidJointStateCombinations:N0} combinations";
                                return false;
                            }
                        }
                    }

                    combinations = expanded;
                }

                var weightedCombinationCount =
                    combinations.Sum(alternatives =>
                        GetCrossRigidJointCombinationMultiplicity(
                            alternatives));
                if (weightedCombinationCount !=
                    contextDescriptor.CombinationCount)
                {
                    reason =
                        "Canonical joint weighted Cartesian product changed since analysis";
                    return false;
                }

                matchingCombinations =
                    combinations
                        .Where(alternatives =>
                            constraints.All(constraint =>
                                alternatives.Any(alternative =>
                                    alternative.SlotIndex ==
                                        constraint.Key &&
                                    alternative.IsReference ==
                                        constraint.Value.IsReference &&
                                    alternative.AlternativeIndex ==
                                        constraint.Value.AlternativeIndex)))
                        .Select(alternatives =>
                            new CrossRigidJointMatchingCombination(
                                BuildCrossRigidJointCombinationKey(
                                    alternatives),
                                GetCrossRigidJointCombinationMultiplicity(
                                    alternatives)))
                        .GroupBy(
                            combination =>
                                combination.CombinationKey,
                            StringComparer.Ordinal)
                        .Select(group =>
                            new CrossRigidJointMatchingCombination(
                                group.Key,
                                group.Sum(item =>
                                    item.Multiplicity)))
                        .OrderBy(
                            combination =>
                                combination.CombinationKey,
                            StringComparer.Ordinal)
                        .ToArray();
            }
            else
            {
                var contextDimensionsByKey =
                    contextDescriptor.CommonAncestorDimensions
                        .ToDictionary(
                            dimension =>
                                dimension.DimensionKey,
                            StringComparer.Ordinal);
                var constraints =
                    new Dictionary<
                        string,
                        (bool IsReference, int AlternativeIndex)>(
                        StringComparer.Ordinal);
                foreach (var selection in
                         occurrenceDescriptor.SourceSelections)
                {
                    var dimensionKey =
                        BuildCrossRigidJointSourceSelectionDimensionKey(
                            selection);
                    if (!contextDimensionsByKey.ContainsKey(
                            dimensionKey))
                    {
                        continue;
                    }

                    var value =
                        (
                            selection.IsReference,
                            selection.AlternativeIndex);
                    if (constraints.TryGetValue(
                            dimensionKey,
                            out var existing) &&
                        existing != value)
                    {
                        reason =
                            "Analyzed occurrence requires incompatible alternatives in one canonical joint dimension";
                        return false;
                    }

                    constraints[dimensionKey] =
                        value;
                }

                if (!TryEnumerateCrossRigidConditionalJointStates(
                        contextDescriptor.CommonAncestorDimensions,
                        out var combinations,
                        out reason))
                {
                    return false;
                }

                var weightedCombinationCount =
                    combinations.Sum(alternatives =>
                        GetCrossRigidJointCombinationMultiplicity(
                            alternatives));
                if (weightedCombinationCount !=
                    contextDescriptor.CombinationCount)
                {
                    reason =
                        "Canonical common-ancestor weighted Cartesian product changed since analysis";
                    return false;
                }

                matchingCombinations =
                    combinations
                        .Where(alternatives =>
                            constraints.All(constraint =>
                                alternatives.Any(alternative =>
                                    alternative.DimensionKey.Equals(
                                        constraint.Key,
                                        StringComparison.Ordinal) &&
                                    alternative.IsReference ==
                                        constraint.Value.IsReference &&
                                    alternative.AlternativeIndex ==
                                        constraint.Value.AlternativeIndex)))
                        .Select(alternatives =>
                            new CrossRigidJointMatchingCombination(
                                BuildCrossRigidCommonAncestorCombinationKey(
                                    alternatives),
                                GetCrossRigidJointCombinationMultiplicity(
                                    alternatives)))
                        .GroupBy(
                            combination =>
                                combination.CombinationKey,
                            StringComparer.Ordinal)
                        .Select(group =>
                            new CrossRigidJointMatchingCombination(
                                group.Key,
                                group.Sum(item =>
                                    item.Multiplicity)))
                        .OrderBy(
                            combination =>
                                combination.CombinationKey,
                            StringComparer.Ordinal)
                        .ToArray();
            }

            if (matchingCombinations.Length == 0)
            {
                reason =
                    "Analyzed occurrence does not match any canonical Cartesian state";
                return false;
            }

            return true;
        }

        private static CrossRigidJointMatchingCombination[]
            FilterCrossRigidSelectedJointCombinations(
                IReadOnlyList<CrossRigidJointMatchingCombination>
                    matchingCombinations,
                IReadOnlySet<string>? selectedCombinationKeys)
        {
            if (selectedCombinationKeys == null)
                return matchingCombinations.ToArray();

            return matchingCombinations
                .Where(combination =>
                    selectedCombinationKeys.Contains(
                        combination.CombinationKey))
                .ToArray();
        }
    }
}
