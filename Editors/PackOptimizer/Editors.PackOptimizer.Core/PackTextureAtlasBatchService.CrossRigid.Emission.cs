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
        private static void EmitSelectedCrossRigidMerges(
            BatchState state,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            state.CrossRigidGeneratedPayloadIds.Clear();
            state.CrossRigidGeneratedRigidPaths.Clear();
            state.CrossRigidGeneratedWsModelPaths.Clear();
            state.CrossRigidGeneratedVmdPaths.Clear();
            state.CrossRigidGeneratedVmdFilesDeduplicated = 0;
            state.CrossRigidGeneratedGroupVmdPaths.Clear();
            state.CrossRigidGroupVmdFilesRequested = 0;
            state.CrossRigidNestedWriterSlotCount = 0;
            state.CrossRigidMaxAlternativesBeforeNesting = 0;
            state.CrossRigidMaxSlotNestingDepth = 0;
            state.CrossRigidInexactSlotNestings.Clear();
            state.CrossRigidAuthorSlotsAboveLimit.Clear();
            state.CrossRigidRewrittenRootVmdPaths.Clear();
            state.CrossRigidRetainedSourceAssetPaths.Clear();
            state.CrossRigidChangedRigids.Clear();
            state.CrossRigidEmissionSkipCounts.Clear();
            state.CrossRigidEmissionSkipExamples.Clear();
            state.CrossRigidRewritePlansApplied = 0;
            state.CrossRigidRewriteOccurrenceSetsApplied = 0;
            state.CrossRigidExpectedArmyDrawCallsEmitted = 0;
            state.CrossRigidRawLodDrawsBefore = 0;
            state.CrossRigidRawLodDrawsAfter = 0;
            state.CrossRigidOriginalRootSlotsByVmd.Clear();
            // Counted before emission replaces cached source VMDs.
            CollectCrossRigidAuthorSlotsAboveLimit(state);
            var emissionTimer = Stopwatch.StartNew();
            var validationElapsed = TimeSpan.Zero;
            var geometryBuildElapsed = TimeSpan.Zero;
            var copyOnWriteElapsed = TimeSpan.Zero;

            var selectedPlans = state.CrossRigidMergeAnalysisEntries
                .Where(plan =>
                    plan.RewriteOccurrenceSets.Any(
                        occurrenceSet =>
                            state.CrossRigidSelectedRewriteOccurrenceKeys
                                .Contains(
                                    BuildCrossRigidSelectedRewriteOccurrenceKey(
                                        plan,
                                        occurrenceSet))))
                .OrderBy(plan =>
                    state.CrossRigidSharedPayloadFallbackAdmittedPlanIds
                        .Contains(plan.RewritePlanId))
                .ThenByDescending(plan =>
                    plan.ExpectedArmyDrawCallsEliminated)
                .ThenBy(plan => plan.RewritePlanId, StringComparer.Ordinal)
                .ToArray();
            if (selectedPlans.Length == 0)
                return;

            var traversalContainers = GetGameplayTraversalContainers(state);
            var rigidCache = new Dictionary<string, RmvFile>(
                state.RigidModels,
                StringComparer.OrdinalIgnoreCase);
            var sourceComponentCache =
                new Dictionary<string, CrossRigidAnalysisComponent>(
                    StringComparer.OrdinalIgnoreCase);
            var payloadBuildFailures = new Dictionary<string, string>(
                StringComparer.Ordinal);
            var sourceVmdDocuments = new Dictionary<string, XmlDocument>(
                StringComparer.OrdinalIgnoreCase);
            var generatedClonePathByBranchKey =
                new Dictionary<string, string>(StringComparer.Ordinal);
            var consumedModelOccurrences =
                new HashSet<string>(StringComparer.Ordinal);
            var jointRewriteContexts =
                new Dictionary<string, CrossRigidJointRewriteContext>(
                    StringComparer.Ordinal);
            var jointContextKeyByParentLocation =
                new Dictionary<string, string>(
                    StringComparer.Ordinal);

            var planGroups = selectedPlans
                .GroupBy(
                    plan => Normalize(plan.VmdPath),
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            for (var vmdIndex = 0; vmdIndex < planGroups.Length; vmdIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var vmdPath = planGroups[vmdIndex].Key;
                var plans = planGroups[vmdIndex]
                    .OrderBy(plan =>
                        state.CrossRigidSharedPayloadFallbackAdmittedPlanIds
                            .Contains(plan.RewritePlanId))
                    .ThenByDescending(plan =>
                        plan.ExpectedArmyDrawCallsEliminated)
                    .ThenBy(plan => plan.RewritePlanId, StringComparer.Ordinal)
                    .ToArray();

                ReportProgress(
                    progress,
                    "Emitting cross-rigid merges",
                    vmdIndex + 1,
                    planGroups.Length,
                    vmdPath);

                var vmdFile = state.Source.FindFile(vmdPath);
                if (vmdFile == null)
                {
                    foreach (var plan in plans)
                    {
                        RecordCrossRigidEmissionSkip(
                            state,
                            "VMD is not writable in the source pack",
                            $"{vmdPath}: {plan.RewritePlanId}");
                    }

                    continue;
                }

                XmlDocument rootDocument;
                try
                {
                    rootDocument = LoadXml(vmdFile);
                    sourceVmdDocuments[vmdPath] =
                        CloneXmlDocument(rootDocument);
                    state.CrossRigidOriginalRootSlotsByVmd[vmdPath] =
                        rootDocument.DocumentElement?
                            .SelectNodes("SLOT")
                            ?.OfType<XmlElement>().ToArray() ?? [];
                }
                catch (Exception ex) when (
                    ex is InvalidOperationException or
                    XmlException or
                    FormatException or
                    ArgumentException)
                {
                    foreach (var plan in plans)
                    {
                        RecordCrossRigidEmissionSkip(
                            state,
                            "VMD could not be loaded for rewrite",
                            $"{vmdPath}: {ex.Message}");
                    }

                    continue;
                }

                var rootModified = false;
                foreach (var plan in plans)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var emittedOccurrenceProbability = 0.0;
                    var emittedOccurrenceSetCount = 0;

                    foreach (var occurrenceSet in
                             plan.RewriteOccurrenceSets
                                 .Where(set =>
                                     state.CrossRigidSelectedRewriteOccurrenceKeys
                                         .Contains(
                                             BuildCrossRigidSelectedRewriteOccurrenceKey(
                                                 plan,
                                                 set)))
                                 .OrderByDescending(set =>
                                     set.Probability)
                                 .ThenBy(
                                     set =>
                                         BuildCrossRigidOccurrenceProvenanceSignature(
                                             set.SourceInstances),
                                     StringComparer.Ordinal))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var rewriteOccurrenceKey =
                            BuildCrossRigidSelectedRewriteOccurrenceKey(
                                plan,
                                occurrenceSet);
                        IReadOnlySet<string>? selectedJointCombinationKeys =
                            null;
                        if (state
                                .CrossRigidSelectedJointCombinationKeysByRewriteOccurrenceKey
                                .TryGetValue(
                                    rewriteOccurrenceKey,
                                    out var selectedCombinationKeys))
                        {
                            selectedJointCombinationKeys =
                                selectedCombinationKeys;
                        }

                        var validationStarted = Stopwatch.GetTimestamp();
                        var validationSucceeded =
                            TryValidateCrossRigidRewriteOccurrenceSet(
                                state,
                                vmdPath,
                                plan,
                                occurrenceSet,
                                sourceVmdDocuments,
                                generatedClonePathByBranchKey,
                                consumedModelOccurrences,
                                jointRewriteContexts,
                                jointContextKeyByParentLocation,
                                selectedJointCombinationKeys,
                                out var validationReason);
                        validationElapsed +=
                            Stopwatch.GetElapsedTime(validationStarted);
                        if (!validationSucceeded)
                        {
                            RecordCrossRigidEmissionSkip(
                                state,
                                validationReason,
                                $"{vmdPath} [{plan.AttachmentIdentity}] " +
                                $"payload={plan.GeneratedPayloadId}");
                            continue;
                        }

                        if (payloadBuildFailures.TryGetValue(
                                plan.GeneratedPayloadId,
                                out var priorPayloadFailure))
                        {
                            RecordCrossRigidEmissionSkip(
                                state,
                                "Generated payload could not be built",
                                $"payload {plan.GeneratedPayloadId}: " +
                                priorPayloadFailure);
                            continue;
                        }

                        if (!state.CrossRigidGeneratedPayloadIds.Contains(
                                plan.GeneratedPayloadId))
                        {
                            var geometryBuildStarted = Stopwatch.GetTimestamp();
                            var geometryBuildSucceeded =
                                TryBuildCrossRigidGeneratedPayload(
                                    state,
                                    plan,
                                    traversalContainers,
                                    rigidCache,
                                    sourceComponentCache,
                                    out var payloadFailure);
                            geometryBuildElapsed +=
                                Stopwatch.GetElapsedTime(geometryBuildStarted);
                            if (!geometryBuildSucceeded)
                            {
                                payloadBuildFailures[
                                    plan.GeneratedPayloadId] =
                                    payloadFailure;
                                RecordCrossRigidEmissionSkip(
                                    state,
                                    "Generated payload could not be built",
                                    $"payload {plan.GeneratedPayloadId}: " +
                                    payloadFailure);
                                continue;
                            }
                        }

                        var generatedWsModelPath =
                            BuildCrossRigidGeneratedWsModelPath(
                                plan.GeneratedPayloadId);
                        var copyOnWriteStarted = Stopwatch.GetTimestamp();
                        var copyOnWriteSucceeded =
                            TryApplyCrossRigidRewriteOccurrenceSet(
                                state,
                                vmdPath,
                                rootDocument,
                                occurrenceSet,
                                generatedWsModelPath,
                                sourceVmdDocuments,
                                generatedClonePathByBranchKey,
                                consumedModelOccurrences,
                                jointRewriteContexts,
                                jointContextKeyByParentLocation,
                                selectedJointCombinationKeys,
                                out var appliedProbabilityFraction,
                                out var applyReason);
                        copyOnWriteElapsed +=
                            Stopwatch.GetElapsedTime(copyOnWriteStarted);
                        if (!copyOnWriteSucceeded)
                        {
                            RecordCrossRigidEmissionSkip(
                                state,
                                "Rewrite provenance could not be applied",
                                $"{vmdPath} [{plan.AttachmentIdentity}] " +
                                $"payload={plan.GeneratedPayloadId}: " +
                                applyReason);
                            continue;
                        }

                        emittedOccurrenceProbability +=
                            occurrenceSet.Probability *
                            appliedProbabilityFraction;
                        emittedOccurrenceSetCount++;
                        rootModified = true;
                    }

                    if (emittedOccurrenceSetCount == 0)
                        continue;

                    state.CrossRigidRewritePlansApplied++;
                    state.CrossRigidRewriteOccurrenceSetsApplied +=
                        emittedOccurrenceSetCount;
                    var emittedFraction =
                        plan.AggregatedConfigurationProbability >
                        AtlasValueGateExpectedDrawEpsilon
                            ? Math.Clamp(
                                emittedOccurrenceProbability /
                                plan.AggregatedConfigurationProbability,
                                0.0,
                                1.0)
                            : 0.0;
                    state.CrossRigidExpectedArmyDrawCallsEmitted +=
                        plan.ExpectedArmyDrawCallsEliminated *
                        emittedFraction;
                    state.CrossRigidRawLodDrawsBefore +=
                        plan.Lods
                            .Where(lod =>
                                lod.IsScenarioRepresentativeState)
                            .Sum(lod =>
                                (long)lod.InputDrawsAfterSameRigidMerge);
                    state.CrossRigidRawLodDrawsAfter +=
                        plan.Lods
                            .Where(lod =>
                                lod.IsScenarioRepresentativeState)
                            .Sum(lod =>
                                (long)lod.OutputDrawsAfterCrossRigidMerge);

                    foreach (var instance in plan.SourceInstances)
                    {
                        state.CrossRigidRetainedSourceAssetPaths.Add(
                            Normalize(instance.WsModelPath));
                    }

                    foreach (var source in plan.SourceGeometry)
                    {
                        state.CrossRigidRetainedSourceAssetPaths.Add(
                            Normalize(source.RigidPath));
                    }
                }

                if (rootModified)
                {
                    state.ModifiedVmdDocuments[vmdPath] =
                        rootDocument;
                    state.CrossRigidRewrittenRootVmdPaths.Add(vmdPath);
                    state.CrossRigidRetainedSourceAssetPaths.Add(vmdPath);
                }
            }

            // Emission is the last source of truth for structural savings.
            // A rejected selected occurrence can leave generated RMVs/WSModels
            // in the output without any corresponding draw elimination, and
            // an incomplete joint branch may have partially mutated cloned
            // documents. Do not save such a pack as a successful conversion.
            if (state.CrossRigidEmissionSkipCounts.Count != 0 ||
                state.CrossRigidRewritePlansApplied != selectedPlans.Length ||
                state.CrossRigidRewriteOccurrenceSetsApplied !=
                    state.CrossRigidSelectedRewriteOccurrenceKeys.Count)
            {
                throw new InvalidOperationException(
                    "Cross-rigid emission did not apply every selected " +
                    "copy-on-write rewrite; refusing to save a partial " +
                    $"topology (plans={state.CrossRigidRewritePlansApplied}/" +
                    $"{selectedPlans.Length}, occurrences=" +
                    $"{state.CrossRigidRewriteOccurrenceSetsApplied}/" +
                    $"{state.CrossRigidSelectedRewriteOccurrenceKeys.Count}, " +
                    $"skip reasons={string.Join(", ", state.CrossRigidEmissionSkipCounts.Select(entry => $"{entry.Key}: {entry.Value}"))}). " +
                    "See cross-rigid emission skip diagnostics in the report.");
            }

            // The writer copies the source geometry into generated rigids and keeps the source
            // files; the rigids it relied on are what later stages must not rewrite.
            foreach (var retained in state.CrossRigidRetainedSourceAssetPaths)
            {
                if (retained.EndsWith(".rigid_model_v2", StringComparison.OrdinalIgnoreCase))
                    state.CrossRigidChangedRigids.Add(retained);
            }

            var finalizationTimer = Stopwatch.StartNew();
            FinalizeCrossRigidJointProbabilityMultiplicities(
                jointRewriteContexts.Values);
            // Nest only after multiplicities are final (they duplicate
            // wrappers) and before the dedupe, so equal group documents
            // are recognized and shared like any other generated VMD.
            NestCrossRigidWriterSlots(state);
            DeduplicateCrossRigidGeneratedVmdDocuments(state);
            AssertCrossRigidWriterSlotAlternativeLimit(
                state.ModifiedVmdDocuments,
                CrossRigidEngineSlotAlternativeLimit);
            StripCrossRigidWriterSlotMarkers(
                state.ModifiedVmdDocuments.Values);

            if (state.ModifiedVmdDocuments.Count != 0)
            {
                foreach (var (path, document) in
                         state.ModifiedVmdDocuments)
                {
                    state.VmdDocuments[path] =
                        VariantMeshDefinitionLoader.Load(
                            document.OuterXml);
                }

                state.ReachableWsModelsByRoot.Clear();
                state.ReachableWsModelsByRootVersion++;
            }

            state.CrossRigidTimingDetails["Emit: validate occurrences"] =
                validationElapsed;
            state.CrossRigidTimingDetails["Emit: construct RMV/WSModel payloads"] =
                geometryBuildElapsed;
            state.CrossRigidTimingDetails["Emit: apply copy-on-write rewrites"] =
                copyOnWriteElapsed;
            state.CrossRigidTimingDetails["Emit: finalize and load VMDs"] =
                finalizationTimer.Elapsed;
            state.CrossRigidTimingDetails["Emit: remaining orchestration"] =
                emissionTimer.Elapsed -
                (validationElapsed + geometryBuildElapsed +
                 copyOnWriteElapsed + finalizationTimer.Elapsed);
        }

        // Finalized copies are no longer mutated by the Cartesian writer.
        // Equivalent child VMDs can be shared across otherwise unrelated
        // appearance combinations, provided their referenced child VMDs are
        // canonicalized first. Do not alter source VMD identities.
        private static void DeduplicateCrossRigidGeneratedVmdDocuments(
            BatchState state)
        {
            if (state.CrossRigidGeneratedVmdPaths.Count < 2)
                return;

            var generatedPaths = state.CrossRigidGeneratedVmdPaths
                .Select(Normalize)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var generatedPathSet = generatedPaths.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            var canonicalByPath = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            var canonicalByXml = new Dictionary<string, string>(
                StringComparer.Ordinal);
            var processing = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            string Canonicalize(string generatedPath)
            {
                generatedPath = Normalize(generatedPath);
                if (canonicalByPath.TryGetValue(
                        generatedPath, out var existingPath))
                    return existingPath;

                // A cyclic generated reference cannot be normalized by
                // bottom-up structural sharing. Refuse to change that graph
                // rather than accidentally redirecting its self references.
                if (!processing.Add(generatedPath))
                {
                    throw new InvalidOperationException(
                        "Cross-rigid generated child VMD references contain a cycle: " +
                        generatedPath);
                }

                if (!state.ModifiedVmdDocuments.TryGetValue(
                        generatedPath, out var document))
                {
                    throw new InvalidOperationException(
                        "Cross-rigid generated child VMD document is missing: " +
                        generatedPath);
                }

                var references = document.SelectNodes(
                    "//VARIANT_MESH_REFERENCE");
                if (references != null)
                {
                    foreach (XmlNode node in references)
                    {
                        if (node is not XmlElement reference)
                            continue;

                        var childPath = Normalize(
                            reference.GetAttribute("definition"));
                        if (!generatedPathSet.Contains(childPath))
                            continue;

                        var canonicalChildPath = Canonicalize(childPath);
                        if (!canonicalChildPath.Equals(
                                childPath,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            reference.SetAttribute(
                                "definition", canonicalChildPath);
                        }
                    }
                }

                // OuterXml preserves all slot probabilities, selection
                // order, metadata, model paths, and attachment semantics.
                var xml = document.OuterXml;
                if (!canonicalByXml.TryGetValue(xml, out var canonicalPath))
                {
                    canonicalPath = generatedPath;
                    canonicalByXml.Add(xml, canonicalPath);
                }

                processing.Remove(generatedPath);
                canonicalByPath.Add(generatedPath, canonicalPath);
                return canonicalPath;
            }

            foreach (var generatedPath in generatedPaths)
                Canonicalize(generatedPath);

            // Rewrite both generated and root VMD references only once
            // all content identities are established. Duplicate generated
            // documents are discarded before serialization and validation.
            foreach (var document in state.ModifiedVmdDocuments.Values)
            {
                var references = document.SelectNodes(
                    "//VARIANT_MESH_REFERENCE");
                if (references == null)
                    continue;

                foreach (XmlNode node in references)
                {
                    if (node is not XmlElement reference)
                        continue;

                    var childPath = Normalize(
                        reference.GetAttribute("definition"));
                    if (!canonicalByPath.TryGetValue(
                            childPath, out var canonicalPath) ||
                        childPath.Equals(
                            canonicalPath,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    reference.SetAttribute("definition", canonicalPath);
                }
            }

            foreach (var (generatedPath, canonicalPath) in canonicalByPath)
            {
                if (generatedPath.Equals(
                        canonicalPath,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                state.ModifiedVmdDocuments.Remove(generatedPath);
                state.CrossRigidGeneratedVmdPaths.Remove(generatedPath);
                state.CrossRigidGeneratedGroupVmdPaths.Remove(generatedPath);
                state.CrossRigidGeneratedVmdFilesDeduplicated++;
            }
        }

        private static void
            FinalizeCrossRigidJointProbabilityMultiplicities(
                IEnumerable<CrossRigidJointRewriteContext> contexts)
        {
            // Inner weighted joints must be finalized before outer wrappers
            // are cloned, so every outer clone captures the already-finalized
            // probability distribution of its descendant joints.
            var combinations =
                contexts
                    .SelectMany(context =>
                        context.Combinations.Values)
                    .Distinct()
                    .OrderByDescending(combination =>
                        GetCrossRigidXmlNodeDepth(
                            combination.WrapperElement))
                    .ToArray();

            foreach (var combination in combinations)
            {
                if (combination.ProbabilityMultiplicityFinalized ||
                    combination.ProbabilityMultiplicity <= 1)
                {
                    combination.ProbabilityMultiplicityFinalized = true;
                    continue;
                }

                XmlNode insertionPoint =
                    combination.WrapperElement;
                var parent =
                    combination.WrapperElement.ParentNode;
                if (parent == null)
                    continue;

                for (var copyIndex = 1;
                     copyIndex <
                     combination.ProbabilityMultiplicity;
                     copyIndex++)
                {
                    var clone =
                        combination.WrapperElement.CloneNode(
                            deep: true);
                    parent.InsertAfter(
                        clone,
                        insertionPoint);
                    insertionPoint =
                        clone;
                }

                combination.ProbabilityMultiplicityFinalized = true;
            }
        }

        // The engine selects among at most CrossRigidEngineSlotAlternativeLimit
        // alternatives of a SLOT. A writer joint slot can hold thousands of
        // combination wrappers, so every writer-created slot above the
        // limit is nested through generated group child VMDs:
        //
        //   <SLOT ...outer attributes...>
        //     <VARIANT_MESH_REFERENCE definition="...group..." /> (g <= limit)
        //   </SLOT>
        //
        // and each group VMD is <VARIANT_MESH><SLOT ...>alternatives</SLOT>
        // </VARIANT_MESH>. Every level selects uniformly, so a leaf keeps
        // its probability exactly when all groups of a level have the same
        // size. Group files use the mechanism of the other generated child
        // VMDs: ModifiedVmdDocuments + CrossRigidGeneratedVmdPaths.
        private sealed record CrossRigidSlotSplitPlan(
            int[] GroupSizes,
            CrossRigidSlotSplitPlan?[] ChildPlans,
            int Depth,
            double MinLeafProbability,
            double MaxLeafProbability);

        // Depth counts slot levels, including the outermost slot: six
        // alternatives (3 groups of 2) have depth 2.
        private sealed record CrossRigidSlotNestingResult(
            int OriginalAlternativeCount,
            int Depth,
            bool Exact,
            double MaxRelativeProbabilityError);

        private static bool IsCrossRigidSlotAlternative(
            XmlElement element)
            => element.Name == "VARIANT_MESH" ||
               element.Name == "VARIANT_MESH_REFERENCE";

        private static int CountCrossRigidSlotAlternatives(
            XmlElement slot)
            => slot.ChildNodes
                .OfType<XmlElement>()
                .Count(IsCrossRigidSlotAlternative);

        // Smallest number of factors (each 2..limit, so 4 = 2 * 2 is
        // available) whose product is count; among equally short
        // factorisations the lexicographically largest descending sequence
        // (larger factors nearer the top). Null when count has a prime
        // factor above the limit.
        private static int[]? FindCrossRigidExactSlotFactors(
            int count,
            int maxFactor,
            Dictionary<(int Count, int MaxFactor), int[]?> memo)
        {
            if (count == 1)
                return [];

            if (memo.TryGetValue((count, maxFactor), out var cached))
                return cached;

            int[]? best = null;
            for (var factor = Math.Min(maxFactor, count);
                 factor >= 2;
                 factor--)
            {
                if (count % factor != 0)
                    continue;

                var tail = FindCrossRigidExactSlotFactors(
                    count / factor,
                    factor,
                    memo);
                if (tail == null)
                    continue;

                if (best == null || tail.Length + 1 < best.Length)
                    best = [factor, .. tail];
            }

            memo[(count, maxFactor)] = best;
            return best;
        }

        private static CrossRigidSlotSplitPlan? BuildCrossRigidExactSplitPlan(
            int count,
            int[] factors,
            int index)
        {
            // The last factor is the alternative count of the leaf slot.
            if (index >= factors.Length - 1)
                return null;

            var groupCount = factors[index];
            var groupSize = count / groupCount;
            var child = BuildCrossRigidExactSplitPlan(
                groupSize,
                factors,
                index + 1);
            var leafProbability = 1.0 / count;
            return new CrossRigidSlotSplitPlan(
                Enumerable.Repeat(groupSize, groupCount).ToArray(),
                Enumerable.Repeat(child, groupCount).ToArray(),
                1 + (child?.Depth ?? 1),
                leafProbability,
                leafProbability);
        }

        // Counts with a prime factor above the limit cannot be split into
        // equal groups. Use the minimal number of slot levels, group sizes
        // differing by at most one at every level, and among the allowed
        // group counts the one with the smallest relative probability error.
        private static CrossRigidSlotSplitPlan? BuildCrossRigidBalancedSplitPlan(
            int count,
            int limit,
            Dictionary<int, CrossRigidSlotSplitPlan?> memo)
        {
            if (count <= limit)
                return null;

            if (memo.TryGetValue(count, out var cached))
                return cached;

            var levels = 1;
            long capacity = limit;
            while (capacity < count)
            {
                levels++;
                capacity *= limit;
            }

            var groupCapacity = capacity / limit;
            var minimumGroups = (int)((count + groupCapacity - 1) /
                                      groupCapacity);
            CrossRigidSlotSplitPlan? best = null;
            var bestError = double.MaxValue;
            for (var groupCount = limit;
                 groupCount >= Math.Max(2, minimumGroups);
                 groupCount--)
            {
                var sizes = new int[groupCount];
                var children = new CrossRigidSlotSplitPlan?[groupCount];
                var minLeaf = double.MaxValue;
                var maxLeaf = 0.0;
                var childDepth = 1;
                for (var index = 0; index < groupCount; index++)
                {
                    sizes[index] = count / groupCount +
                                   (index < count % groupCount ? 1 : 0);
                    children[index] = BuildCrossRigidBalancedSplitPlan(
                        sizes[index],
                        limit,
                        memo);
                    childDepth = Math.Max(
                        childDepth,
                        children[index]?.Depth ?? 1);
                    minLeaf = Math.Min(
                        minLeaf,
                        (children[index]?.MinLeafProbability ??
                         1.0 / sizes[index]) / groupCount);
                    maxLeaf = Math.Max(
                        maxLeaf,
                        (children[index]?.MaxLeafProbability ??
                         1.0 / sizes[index]) / groupCount);
                }

                var error = Math.Max(
                    Math.Abs(count * minLeaf - 1.0),
                    Math.Abs(count * maxLeaf - 1.0));
                if (best != null && error >= bestError - 1e-12)
                    continue;

                bestError = error;
                best = new CrossRigidSlotSplitPlan(
                    sizes,
                    children,
                    1 + childDepth,
                    minLeaf,
                    maxLeaf);
            }

            memo[count] = best;
            return best;
        }

        // Pure with respect to the pipeline: nests `slot` in place and hands
        // every finished group document to registerGroupDocument, which
        // returns the path the reference must use. Returns null when the
        // slot already respects the limit (and leaves it untouched).
        private static CrossRigidSlotNestingResult?
            NestCrossRigidSlotAlternatives(
                XmlElement slot,
                int limit,
                Func<XmlDocument, string> registerGroupDocument)
        {
            ArgumentNullException.ThrowIfNull(slot);
            ArgumentNullException.ThrowIfNull(registerGroupDocument);
            if (limit < 2)
                throw new ArgumentOutOfRangeException(nameof(limit));

            var alternatives = new List<XmlElement>();
            foreach (var child in slot.ChildNodes.OfType<XmlElement>())
            {
                if (!IsCrossRigidSlotAlternative(child))
                {
                    throw new InvalidOperationException(
                        $"Cannot nest SLOT with unexpected <{child.Name}> child.");
                }

                alternatives.Add(child);
            }

            var count = alternatives.Count;
            if (count <= limit)
                return null;

            var factors = FindCrossRigidExactSlotFactors(
                count,
                limit,
                new());
            var exact = factors != null;
            var plan = exact
                ? BuildCrossRigidExactSplitPlan(count, factors!, 0)
                : BuildCrossRigidBalancedSplitPlan(count, limit, new());
            ApplyCrossRigidSlotSplitPlan(
                slot,
                alternatives,
                plan!,
                registerGroupDocument);
            return new CrossRigidSlotNestingResult(
                count,
                plan!.Depth,
                exact,
                exact
                    ? 0.0
                    : Math.Max(
                        Math.Abs(count * plan.MinLeafProbability - 1.0),
                        Math.Abs(count * plan.MaxLeafProbability - 1.0)));
        }

        private static void ApplyCrossRigidSlotSplitPlan(
            XmlElement slot,
            IReadOnlyList<XmlElement> alternatives,
            CrossRigidSlotSplitPlan plan,
            Func<XmlDocument, string> registerGroupDocument)
        {
            // The slot's alternatives were direct children of the enclosing
            // VARIANT_MESH; the group root stands in for that mesh.
            var enclosingMesh = slot.ParentNode as XmlElement;
            var ownerDocument = slot.OwnerDocument;
            while (slot.FirstChild != null)
                slot.RemoveChild(slot.FirstChild);

            var offset = 0;
            for (var groupIndex = 0;
                 groupIndex < plan.GroupSizes.Length;
                 groupIndex++)
            {
                var groupDocument = new XmlDocument();
                var groupRoot = groupDocument.CreateElement(
                    "VARIANT_MESH");
                if (enclosingMesh != null &&
                    enclosingMesh.HasAttribute(
                        "use_different_attach_point_parts"))
                {
                    groupRoot.SetAttribute(
                        "use_different_attach_point_parts",
                        enclosingMesh.GetAttribute(
                            "use_different_attach_point_parts"));
                }

                groupDocument.AppendChild(groupRoot);

                // The outer slot alone carries attach_point: slots of a
                // referenced VMD never repeat the attach point of the
                // referencing slot, they inherit it.
                var groupSlot = groupDocument.CreateElement("SLOT");
                foreach (var attribute in
                         slot.Attributes.OfType<XmlAttribute>())
                {
                    if (attribute.Name == "attach_point" ||
                        attribute.Name == "probability")
                    {
                        continue;
                    }

                    groupSlot.SetAttribute(
                        attribute.Name,
                        attribute.Value);
                }

                groupSlot.SetAttribute("probability", "1");
                groupRoot.AppendChild(groupSlot);

                var groupAlternatives = new List<XmlElement>();
                for (var index = 0;
                     index < plan.GroupSizes[groupIndex];
                     index++)
                {
                    var imported = (XmlElement)groupDocument.ImportNode(
                        alternatives[offset + index],
                        deep: true);
                    groupSlot.AppendChild(imported);
                    groupAlternatives.Add(imported);
                }

                offset += plan.GroupSizes[groupIndex];
                if (plan.ChildPlans[groupIndex] is { } childPlan)
                {
                    ApplyCrossRigidSlotSplitPlan(
                        groupSlot,
                        groupAlternatives,
                        childPlan,
                        registerGroupDocument);
                }

                var reference = ownerDocument.CreateElement(
                    "VARIANT_MESH_REFERENCE");
                reference.SetAttribute(
                    "definition",
                    registerGroupDocument(groupDocument));
                slot.AppendChild(reference);
            }
        }

        private static string BuildCrossRigidGeneratedGroupVmdPath(
            string groupXml)
            => Normalize(
                $"variantmeshes\\variantmeshdefinitions\\asset_editor\\cross_rigid\\group_" +
                $"{ContentHash(groupXml).ToLowerInvariant()}.variantmeshdefinition");

        // Runs after multiplicity finalization. Element identity does not
        // survive CloneXmlDocument or wrapper cloning, so writer-created
        // slots carry CrossRigidWriterSlotMarkerAttribute, which is copied
        // by every clone. Innermost slots are nested first: an outer slot
        // copies its alternatives (and the slots inside) into group files.
        private static void NestCrossRigidWriterSlots(
            BatchState state)
        {
            string RegisterGroupDocument(XmlDocument groupDocument)
            {
                state.CrossRigidGroupVmdFilesRequested++;
                var xml = groupDocument.OuterXml;
                var groupPath = BuildCrossRigidGeneratedGroupVmdPath(xml);
                if (state.ModifiedVmdDocuments.TryGetValue(
                        groupPath,
                        out var existing))
                {
                    // The path is derived from the content, so an equal
                    // group simply shares the file.
                    if (!state.CrossRigidGeneratedGroupVmdPaths.Contains(
                            groupPath) ||
                        !existing.OuterXml.Equals(
                            xml,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Cross-rigid generated group VMD path collides with a different document: " +
                            groupPath);
                    }

                    return groupPath;
                }

                if (state.Source.ContainsFile(groupPath) ||
                    state.Output.ContainsFile(groupPath))
                {
                    throw new InvalidOperationException(
                        "Deterministic generated group VMD path already exists: " +
                        groupPath);
                }

                state.ModifiedVmdDocuments[groupPath] = groupDocument;
                state.CrossRigidGeneratedVmdPaths.Add(groupPath);
                state.CrossRigidGeneratedGroupVmdPaths.Add(groupPath);
                return groupPath;
            }

            // Group documents created below are already within the limit.
            var vmdPaths = state.ModifiedVmdDocuments.Keys
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var vmdPath in vmdPaths)
            {
                var writerSlots = state.ModifiedVmdDocuments[vmdPath]
                    .SelectNodes(
                        $"//SLOT[@{CrossRigidWriterSlotMarkerAttribute}]")
                    ?.OfType<XmlElement>()
                    .OrderByDescending(GetCrossRigidXmlNodeDepth)
                    .ToArray() ?? [];
                foreach (var writerSlot in writerSlots)
                {
                    var result = NestCrossRigidSlotAlternatives(
                        writerSlot,
                        CrossRigidEngineSlotAlternativeLimit,
                        RegisterGroupDocument);
                    if (result == null)
                        continue;

                    state.CrossRigidNestedWriterSlotCount++;
                    state.CrossRigidMaxAlternativesBeforeNesting = Math.Max(
                        state.CrossRigidMaxAlternativesBeforeNesting,
                        result.OriginalAlternativeCount);
                    state.CrossRigidMaxSlotNestingDepth = Math.Max(
                        state.CrossRigidMaxSlotNestingDepth,
                        result.Depth);
                    if (!result.Exact)
                    {
                        state.CrossRigidInexactSlotNestings.Add(
                            new CrossRigidInexactSlotNesting(
                                vmdPath,
                                result.OriginalAlternativeCount,
                                result.MaxRelativeProbabilityError));
                    }
                }
            }
        }

        private static void AssertCrossRigidWriterSlotAlternativeLimit(
            IEnumerable<KeyValuePair<string, XmlDocument>> documents,
            int limit)
        {
            foreach (var (vmdPath, document) in documents
                         .OrderBy(
                             entry => entry.Key,
                             StringComparer.OrdinalIgnoreCase))
            {
                var writerSlots = document.SelectNodes(
                                      $"//SLOT[@{CrossRigidWriterSlotMarkerAttribute}]")
                                  ?.OfType<XmlElement>() ?? [];
                foreach (var writerSlot in writerSlots)
                {
                    var count = CountCrossRigidSlotAlternatives(writerSlot);
                    if (count <= limit)
                        continue;

                    throw new InvalidOperationException(
                        "Cross-rigid emission left a writer-created SLOT " +
                        $"with {count} direct alternatives in {vmdPath}; " +
                        $"the engine uses at most {limit}. Refusing to " +
                        "save the pack.");
                }
            }
        }

        private static void StripCrossRigidWriterSlotMarkers(
            IEnumerable<XmlDocument> documents)
        {
            foreach (var document in documents)
            {
                var markedSlots = document.SelectNodes(
                                      $"//SLOT[@{CrossRigidWriterSlotMarkerAttribute}]")
                                  ?.OfType<XmlElement>().ToArray() ?? [];
                foreach (var markedSlot in markedSlots)
                {
                    markedSlot.RemoveAttribute(
                        CrossRigidWriterSlotMarkerAttribute);
                }
            }
        }

        // Source VMDs the tool loaded; author-written slots are never
        // changed, only counted for the report.
        private static void CollectCrossRigidAuthorSlotsAboveLimit(
            BatchState state)
        {
            void Walk(string vmdPath, VariantMesh mesh)
            {
                foreach (var slot in mesh.ChildSlots ?? [])
                {
                    var count = (slot.ChildMeshes?.Count ?? 0) +
                                (slot.ChildReferences?.Count ?? 0);
                    if (count > CrossRigidEngineSlotAlternativeLimit)
                    {
                        state.CrossRigidAuthorSlotsAboveLimit.Add(
                            new CrossRigidOverLimitSlot(
                                vmdPath,
                                slot.Name ?? string.Empty,
                                count));
                    }

                    foreach (var child in slot.ChildMeshes ?? [])
                        Walk(vmdPath, child);
                }
            }

            foreach (var (vmdPath, vmd) in state.VmdDocuments
                         .OrderBy(
                             entry => entry.Key,
                             StringComparer.OrdinalIgnoreCase))
            {
                if (state.Source.ContainsFile(Normalize(vmdPath)))
                    Walk(vmdPath, vmd);
            }
        }

        private static int GetCrossRigidXmlNodeDepth(
            XmlNode node)
        {
            var depth = 0;
            for (var current = node.ParentNode;
                 current != null;
                 current = current.ParentNode)
            {
                depth++;
            }

            return depth;
        }

        private static bool TryValidateCrossRigidRewriteOccurrenceSet(
            BatchState state,
            string rootVmdPath,
            CrossRigidMergeAnalysisEntry plan,
            CrossRigidRewriteOccurrenceSet occurrenceSet,
            Dictionary<string, XmlDocument> sourceVmdDocuments,
            IReadOnlyDictionary<string, string> generatedClonePathByBranchKey,
            IReadOnlySet<string> consumedModelOccurrences,
            IReadOnlyDictionary<string, CrossRigidJointRewriteContext>
                jointRewriteContexts,
            IReadOnlyDictionary<string, string>
                jointContextKeyByParentLocation,
            IReadOnlySet<string>? selectedJointCombinationKeys,
            out string reason)
        {
            reason = string.Empty;
            if (occurrenceSet.SourceInstances.Length < 2)
            {
                reason = "Rewrite occurrence has fewer than two source models";
                return false;
            }

            var activationSignatures = occurrenceSet.SourceInstances
                .Select(instance => instance.ActivationSignature)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var exactActivation = activationSignatures.Length == 1;
            CrossRigidJointAlwaysPresentRewriteDescriptor? jointDescriptor =
                null;
            if (!exactActivation)
            {
                if (!TryDescribeCrossRigidJointAlwaysPresentRewrite(
                        state,
                        occurrenceSet,
                        sourceVmdDocuments,
                        out jointDescriptor,
                        out reason))
                {
                    return false;
                }

                var originalParentKey =
                    BuildCrossRigidJointParentLocationKey(
                        rootVmdPath, jointDescriptor);
                var parentLocationKey =
                    GetCrossRigidEffectiveJointParentKey(
                        state, rootVmdPath, jointDescriptor);
                var contextDescriptor =
                    GetCrossRigidEffectiveJointDescriptor(
                        state,
                        rootVmdPath,
                        jointDescriptor);
                var contextKey =
                    BuildCrossRigidJointContextKey(
                        rootVmdPath,
                        contextDescriptor);
                if (!TryGetCrossRigidMatchingJointCombinations(
                        state,
                        jointDescriptor,
                        contextDescriptor,
                        sourceVmdDocuments,
                        out var matchingCombinations,
                        out reason))
                {
                    return false;
                }

                var selectedCombinations =
                    FilterCrossRigidSelectedJointCombinations(
                        matchingCombinations,
                        selectedJointCombinationKeys);
                if (selectedCombinations.Length == 0)
                {
                    reason =
                        "No selected canonical Cartesian state remains for this rewrite occurrence";
                    return false;
                }

                if (!IsCrossRigidWriterFactorCompatible(
                        jointContextKeyByParentLocation,
                        originalParentKey,
                        parentLocationKey,
                        contextKey))
                {
                    reason =
                        "Joint writer already materialized an incompatible slot factor";
                    return false;
                }

                if (jointRewriteContexts.TryGetValue(
                        contextKey,
                        out var existingContext))
                {
                    foreach (var selectedCombination in
                             selectedCombinations)
                    {
                        if (!existingContext.Combinations.TryGetValue(
                                selectedCombination.CombinationKey,
                                out var combination))
                        {
                            reason =
                                "Joint writer could not locate a matching canonical Cartesian combination";
                            return false;
                        }

                        foreach (var selection in
                                 jointDescriptor.SourceSelections)
                        {
                            var occurrenceKey =
                                BuildCrossRigidOccurrenceProvenanceSignature(
                                    [selection.Instance]);
                            if (combination.ConsumedOccurrenceKeys.Contains(
                                    occurrenceKey))
                            {
                                reason =
                                    "Rewrite occurrence overlaps an already emitted joint-state plan";
                                return false;
                            }
                        }
                    }
                }
            }

            if (occurrenceSet.SourceInstances.Any(instance =>
                    instance.HasRewriteBlockingModifiers))
            {
                reason =
                    "VMD model occurrence has local decal or imposter state";
                return false;
            }

            foreach (var instance in occurrenceSet.SourceInstances)
            {
                if (!instance.AttachmentIdentity.Equals(
                        plan.AttachmentIdentity,
                        StringComparison.Ordinal))
                {
                    reason =
                        "Rewrite occurrence attachment identity no longer matches the analyzed plan";
                    return false;
                }

                if (exactActivation)
                {
                    var occurrenceKey =
                        BuildCrossRigidConsumedOccurrenceKey(
                            rootVmdPath,
                            instance);
                    if (consumedModelOccurrences.Contains(
                            occurrenceKey))
                    {
                        reason =
                            "Rewrite occurrence overlaps an already emitted plan";
                        return false;
                    }
                }

                var currentSourceVmdPath =
                    Normalize(rootVmdPath);
                var prefixHops =
                    new List<CrossRigidVmdReferenceHop>();
                foreach (var hop in instance.ReferenceChain)
                {
                    if (!Normalize(hop.OwnerVmdPath).Equals(
                            currentSourceVmdPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        reason =
                            "Recorded VMD reference provenance is discontinuous";
                        return false;
                    }

                    if (!TryGetCrossRigidSourceVmdDocument(
                            state,
                            currentSourceVmdPath,
                            sourceVmdDocuments,
                            out var ownerDocument,
                            out reason))
                    {
                        return false;
                    }

                    if (ownerDocument.SelectSingleNode(
                            hop.ReferenceXmlPath) is
                            not XmlElement referenceElement ||
                        !Normalize(
                                referenceElement.GetAttribute(
                                    "definition"))
                            .Equals(
                                Normalize(hop.ReferencedVmdPath),
                                StringComparison.OrdinalIgnoreCase))
                    {
                        reason =
                            "Recorded child-VMD reference no longer matches source XML";
                        return false;
                    }

                    prefixHops.Add(hop);
                    var branchKey =
                        BuildCrossRigidGeneratedVmdBranchKey(
                            rootVmdPath,
                            prefixHops);
                    var clonePath =
                        BuildCrossRigidGeneratedVmdPath(
                            branchKey);
                    if (!generatedClonePathByBranchKey.ContainsKey(
                            branchKey) &&
                        (state.Source.ContainsFile(clonePath) ||
                         state.Output.ContainsFile(clonePath)))
                    {
                        reason =
                            "Deterministic generated VMD path already exists";
                        return false;
                    }

                    currentSourceVmdPath =
                        Normalize(hop.ReferencedVmdPath);
                }

                if (!currentSourceVmdPath.Equals(
                        Normalize(instance.DefiningVmdPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    reason =
                        "Recorded model-defining VMD does not match the reference chain";
                    return false;
                }

                if (!TryGetCrossRigidSourceVmdDocument(
                        state,
                        currentSourceVmdPath,
                        sourceVmdDocuments,
                        out var definingDocument,
                        out reason))
                {
                    return false;
                }

                if (definingDocument.SelectSingleNode(
                        instance.ModelXmlPath) is
                        not XmlElement modelElement ||
                    !Normalize(modelElement.GetAttribute("model"))
                        .Equals(
                            Normalize(instance.WsModelPath),
                            StringComparison.OrdinalIgnoreCase))
                {
                    reason =
                        "Recorded model occurrence no longer matches source XML";
                    return false;
                }
            }

            return true;
        }


        private static bool TryApplyCrossRigidRewriteOccurrenceSet(
            BatchState state,
            string rootVmdPath,
            XmlDocument rootDocument,
            CrossRigidRewriteOccurrenceSet occurrenceSet,
            string generatedWsModelPath,
            Dictionary<string, XmlDocument> sourceVmdDocuments,
            Dictionary<string, string> generatedClonePathByBranchKey,
            HashSet<string> consumedModelOccurrences,
            Dictionary<string, CrossRigidJointRewriteContext>
                jointRewriteContexts,
            Dictionary<string, string>
                jointContextKeyByParentLocation,
            IReadOnlySet<string>? selectedJointCombinationKeys,
            out double appliedProbabilityFraction,
            out string reason)
        {
            appliedProbabilityFraction = 0;
            reason = string.Empty;
            var exactActivation =
                occurrenceSet.SourceInstances
                    .Select(instance => instance.ActivationSignature)
                    .Distinct(StringComparer.Ordinal)
                    .Count() == 1;
            if (!exactActivation)
            {
                return TryApplyCrossRigidJointAlwaysPresentRewriteOccurrenceSet(
                    state,
                    rootVmdPath,
                    rootDocument,
                    occurrenceSet,
                    generatedWsModelPath,
                    sourceVmdDocuments,
                    generatedClonePathByBranchKey,
                    jointRewriteContexts,
                    jointContextKeyByParentLocation,
                    selectedJointCombinationKeys,
                    out appliedProbabilityFraction,
                    out reason);
            }

            var orderedInstances = occurrenceSet.SourceInstances
                .OrderBy(
                    instance =>
                        BuildCrossRigidOccurrenceProvenanceSignature(
                            [instance]),
                    StringComparer.Ordinal)
                .ToArray();

            for (var instanceIndex = 0;
                 instanceIndex < orderedInstances.Length;
                 instanceIndex++)
            {
                var instance = orderedInstances[instanceIndex];
                if (!TryResolveCrossRigidMutableDefiningDocument(
                        state,
                        rootVmdPath,
                        rootDocument,
                        instance.ReferenceChain,
                        sourceVmdDocuments,
                        generatedClonePathByBranchKey,
                        out var currentDocument,
                        out reason))
                {
                    return false;
                }

                if (currentDocument.SelectSingleNode(
                        instance.ModelXmlPath) is
                        not XmlElement modelElement)
                {
                    reason =
                        $"Could not locate model occurrence at " +
                        instance.ModelXmlPath;
                    return false;
                }

                if (instanceIndex == 0)
                {
                    modelElement.SetAttribute(
                        "model",
                        generatedWsModelPath);
                }
                else
                {
                    modelElement.RemoveAttribute("model");
                }

                consumedModelOccurrences.Add(
                    BuildCrossRigidConsumedOccurrenceKey(
                        rootVmdPath,
                        instance));
                if (state.Source.ContainsFile(
                        Normalize(instance.DefiningVmdPath)))
                {
                    state.CrossRigidRetainedSourceAssetPaths.Add(
                        Normalize(instance.DefiningVmdPath));
                }
            }

            appliedProbabilityFraction = 1;
            return true;
        }

        private static bool
            TryApplyCrossRigidJointAlwaysPresentRewriteOccurrenceSet(
                BatchState state,
                string rootVmdPath,
                XmlDocument rootDocument,
                CrossRigidRewriteOccurrenceSet occurrenceSet,
                string generatedWsModelPath,
                Dictionary<string, XmlDocument> sourceVmdDocuments,
                Dictionary<string, string> generatedClonePathByBranchKey,
                Dictionary<string, CrossRigidJointRewriteContext>
                    jointRewriteContexts,
                Dictionary<string, string>
                    jointContextKeyByParentLocation,
                IReadOnlySet<string>? selectedJointCombinationKeys,
                out double appliedProbabilityFraction,
                out string reason)
        {
            appliedProbabilityFraction = 0;
            if (!TryDescribeCrossRigidJointAlwaysPresentRewrite(
                    state,
                    occurrenceSet,
                    sourceVmdDocuments,
                    out var descriptor,
                    out reason))
            {
                return false;
            }

            var originalParentKey =
                BuildCrossRigidJointParentLocationKey(
                    rootVmdPath, descriptor);
            var parentLocationKey =
                GetCrossRigidEffectiveJointParentKey(
                    state, rootVmdPath, descriptor);
            var contextDescriptor =
                GetCrossRigidEffectiveJointDescriptor(
                    state,
                    rootVmdPath,
                    descriptor);
            var contextKey =
                BuildCrossRigidJointContextKey(
                    rootVmdPath,
                    contextDescriptor);
            if (!TryGetCrossRigidMatchingJointCombinations(
                    state,
                    descriptor,
                    contextDescriptor,
                    sourceVmdDocuments,
                    out var matchingCombinations,
                    out reason))
            {
                return false;
            }

            var selectedCombinations =
                FilterCrossRigidSelectedJointCombinations(
                    matchingCombinations,
                    selectedJointCombinationKeys);
            if (selectedCombinations.Length == 0)
            {
                reason =
                    "No selected canonical Cartesian state remains for this rewrite occurrence";
                return false;
            }

            var totalMatchingMultiplicity =
                matchingCombinations.Sum(combination =>
                    combination.Multiplicity);
            var selectedMultiplicity =
                selectedCombinations.Sum(combination =>
                    combination.Multiplicity);
            if (totalMatchingMultiplicity <= 0 ||
                selectedMultiplicity <= 0)
            {
                reason =
                    "Selected canonical Cartesian state has invalid multiplicity";
                return false;
            }

            appliedProbabilityFraction =
                (double)selectedMultiplicity /
                totalMatchingMultiplicity;

            if (!IsCrossRigidWriterFactorCompatible(
                    jointContextKeyByParentLocation,
                    originalParentKey,
                    parentLocationKey,
                    contextKey))
            {
                reason =
                    "Joint writer already materialized an incompatible slot factor";
                return false;
            }

            if (!jointRewriteContexts.TryGetValue(
                    contextKey,
                    out var context))
            {
                if (!TryResolveCrossRigidMutableDefiningDocument(
                        state,
                        rootVmdPath,
                        rootDocument,
                        contextDescriptor.AnchorReferenceChain,
                        sourceVmdDocuments,
                        generatedClonePathByBranchKey,
                        out var definingDocument,
                        out reason) ||
                    !TryCreateCrossRigidJointRewriteContext(
                        state,
                        definingDocument,
                        contextKey,
                        parentLocationKey,
                        contextDescriptor,
                        sourceVmdDocuments,
                        out context,
                        out reason))
                {
                    return false;
                }

                jointRewriteContexts[contextKey] = context;
                jointContextKeyByParentLocation[parentLocationKey] =
                    contextKey;
                jointContextKeyByParentLocation[originalParentKey] =
                    parentLocationKey.Equals(
                        originalParentKey, StringComparison.Ordinal)
                        ? contextKey
                        : CrossRigidFactoredParentMarker;
            }

            var orderedSelections = descriptor.SourceSelections
                .OrderBy(
                    selection =>
                        BuildCrossRigidOccurrenceProvenanceSignature(
                            [selection.Instance]),
                    StringComparer.Ordinal)
                .ToArray();
            var resolvedApplications =
                new List<(
                    CrossRigidJointCombinationRewriteState Combination,
                    List<(
                        CrossRigidJointSourceSelection Selection,
                        XmlElement ModelElement)> ModelElements)>();

            foreach (var selectedCombination in
                     selectedCombinations)
            {
                var combinationKey =
                    selectedCombination.CombinationKey;
                if (!context.Combinations.TryGetValue(
                        combinationKey,
                        out var combination))
                {
                    reason =
                        "Matching canonical joint Cartesian combination no longer resolves";
                    return false;
                }

                var resolvedModelElements =
                    new List<(
                        CrossRigidJointSourceSelection Selection,
                        XmlElement ModelElement)>();
                foreach (var selection in orderedSelections)
                {
                    var occurrenceKey =
                        BuildCrossRigidOccurrenceProvenanceSignature(
                            [selection.Instance]);
                    if (combination.ConsumedOccurrenceKeys.Contains(
                            occurrenceKey))
                    {
                        reason =
                            "Joint Cartesian model occurrence was already consumed";
                        return false;
                    }

                    if (!TryResolveCrossRigidJointSelectionModelElement(
                            state,
                            contextKey,
                            combinationKey,
                            combination,
                            selection,
                            sourceVmdDocuments,
                            out var modelElement,
                            out reason))
                    {
                        return false;
                    }

                    if (!Normalize(modelElement.GetAttribute("model"))
                            .Equals(
                                Normalize(selection.Instance.WsModelPath),
                                StringComparison.OrdinalIgnoreCase))
                    {
                        reason =
                            "Joint Cartesian model occurrence no longer matches the analyzed source";
                        return false;
                    }

                    resolvedModelElements.Add(
                        (selection, modelElement));
                }

                resolvedApplications.Add(
                    (
                        combination,
                        resolvedModelElements));
            }

            foreach (var application in
                     resolvedApplications)
            {
                for (var selectionIndex = 0;
                     selectionIndex <
                     application.ModelElements.Count;
                     selectionIndex++)
                {
                    var (selection, modelElement) =
                        application.ModelElements[
                            selectionIndex];
                    if (selectionIndex == 0)
                    {
                        modelElement.SetAttribute(
                            "model",
                            generatedWsModelPath);
                    }
                    else
                    {
                        // Keep META_DATA and all child slots on the selected branch.
                        // Only geometry is displaced by the generated merged WSModel.
                        modelElement.RemoveAttribute("model");
                    }

                    application.Combination.ConsumedOccurrenceKeys.Add(
                        BuildCrossRigidOccurrenceProvenanceSignature(
                            [selection.Instance]));
                    if (state.Source.ContainsFile(
                            Normalize(selection.Instance.DefiningVmdPath)))
                    {
                        state.CrossRigidRetainedSourceAssetPaths.Add(
                            Normalize(selection.Instance.DefiningVmdPath));
                    }
                }
            }

            return true;
        }

        private static bool TryResolveCrossRigidMutableDefiningDocument(
            BatchState state,
            string rootVmdPath,
            XmlDocument rootDocument,
            IReadOnlyList<CrossRigidVmdReferenceHop> referenceChain,
            Dictionary<string, XmlDocument> sourceVmdDocuments,
            Dictionary<string, string> generatedClonePathByBranchKey,
            out XmlDocument currentDocument,
            out string reason)
        {
            reason = string.Empty;
            currentDocument = rootDocument;
            var prefixHops =
                new List<CrossRigidVmdReferenceHop>();

            foreach (var hop in referenceChain)
            {
                prefixHops.Add(hop);
                var branchKey =
                    BuildCrossRigidGeneratedVmdBranchKey(
                        rootVmdPath,
                        prefixHops);
                if (!generatedClonePathByBranchKey.TryGetValue(
                        branchKey,
                        out var generatedVmdPath))
                {
                    generatedVmdPath =
                        BuildCrossRigidGeneratedVmdPath(
                            branchKey);
                    if (state.Source.ContainsFile(generatedVmdPath) ||
                        state.Output.ContainsFile(generatedVmdPath))
                    {
                        reason =
                            "Deterministic generated VMD path already exists";
                        return false;
                    }

                    if (!TryGetCrossRigidSourceVmdDocument(
                            state,
                            hop.ReferencedVmdPath,
                            sourceVmdDocuments,
                            out var sourceChildDocument,
                            out reason))
                    {
                        return false;
                    }

                    var generatedDocument =
                        CloneXmlDocument(
                            sourceChildDocument);
                    state.ModifiedVmdDocuments[
                        generatedVmdPath] =
                        generatedDocument;
                    state.CrossRigidGeneratedVmdPaths.Add(
                        generatedVmdPath);
                    generatedClonePathByBranchKey[
                        branchKey] =
                        generatedVmdPath;
                }

                if (currentDocument.SelectSingleNode(
                        hop.ReferenceXmlPath) is
                    not XmlElement referenceElement)
                {
                    reason =
                        $"Could not locate child VMD reference at " +
                        hop.ReferenceXmlPath;
                    return false;
                }

                referenceElement.SetAttribute(
                    "definition",
                    generatedVmdPath);
                currentDocument =
                    state.ModifiedVmdDocuments[
                        generatedVmdPath];

                if (state.Source.ContainsFile(
                        Normalize(hop.OwnerVmdPath)))
                {
                    state.CrossRigidRetainedSourceAssetPaths.Add(
                        Normalize(hop.OwnerVmdPath));
                }

                if (state.Source.ContainsFile(
                        Normalize(hop.ReferencedVmdPath)))
                {
                    state.CrossRigidRetainedSourceAssetPaths.Add(
                        Normalize(hop.ReferencedVmdPath));
                }
            }

            return true;
        }

        private static bool IsCrossRigidWriterFactorCompatible(
            IReadOnlyDictionary<string, string> occupied,
            string originalParentKey,
            string effectiveParentKey,
            string contextKey)
        {
            var expectedParent = effectiveParentKey.Equals(
                    originalParentKey, StringComparison.Ordinal)
                ? contextKey
                : CrossRigidFactoredParentMarker;
            return (!occupied.TryGetValue(
                        originalParentKey, out var previousParent) ||
                    previousParent.Equals(expectedParent,
                        StringComparison.Ordinal)) &&
                   (!occupied.TryGetValue(
                        effectiveParentKey, out var previousFactor) ||
                    previousFactor.Equals(contextKey,
                        StringComparison.Ordinal));
        }

        private static XmlElement? ResolveCrossRigidFactoredRootSlot(
            BatchState state,
            string parentLocationKey,
            XmlElement parentElement,
            int slotIndex)
        {
            if (!parentLocationKey.Contains(
                    "\u001ffactor-slots=", StringComparison.Ordinal))
                return parentElement.SelectSingleNode(
                    $"SLOT[{slotIndex}]") as XmlElement;

            var rootPath = parentLocationKey.Split('\u001f')[0];
            if (!state.CrossRigidOriginalRootSlotsByVmd.TryGetValue(
                    rootPath, out var originalSlots) ||
                slotIndex < 1 || slotIndex > originalSlots.Length ||
                originalSlots[slotIndex - 1].ParentNode != parentElement)
                return null;

            return originalSlots[slotIndex - 1];
        }

        private static bool TryCreateCrossRigidJointRewriteContext(
            BatchState state,
            XmlDocument definingDocument,
            string contextKey,
            string parentLocationKey,
            CrossRigidJointAlwaysPresentRewriteDescriptor descriptor,
            Dictionary<string, XmlDocument> sourceVmdDocuments,
            out CrossRigidJointRewriteContext context,
            out string reason)
        {
            context = null!;
            reason = string.Empty;
            if (descriptor.CommonAncestorDimensions.Length != 0)
            {
                return TryCreateCrossRigidCommonAncestorJointRewriteContext(
                    state,
                    definingDocument,
                    contextKey,
                    parentLocationKey,
                    descriptor,
                    sourceVmdDocuments,
                    out context,
                    out reason);
            }

            if (definingDocument.SelectSingleNode(
                    descriptor.ParentXmlPath) is
                not XmlElement parentElement)
            {
                reason =
                    "Joint parent VARIANT_MESH could not be resolved in mutable VMD";
                return false;
            }

            var slotDefinitions =
                new List<CrossRigidJointSlotDefinition>();
            foreach (var slotIndex in descriptor.SlotIndices)
            {
                if (ResolveCrossRigidFactoredRootSlot(
                        state, parentLocationKey, parentElement,
                        slotIndex) is not XmlElement slotElement)
                {
                    reason =
                        $"Joint source slot {slotIndex} could not be resolved in mutable VMD";
                    return false;
                }

                var probability =
                    ParseVmdSlotProbability(
                        slotElement.GetAttribute("probability"));
                if (probability <
                        1.0 - AtlasValueGateExpectedDrawEpsilon &&
                    !IsSupportedCrossRigidOptionalJointSlot(
                        slotElement))
                {
                    reason =
                        $"Joint source slot {slotIndex} became an unsupported optional slot";
                    return false;
                }

                var alternatives =
                    GetCrossRigidJointAlternatives(
                        slotElement,
                        slotIndex);
                if (alternatives.Length <= 1)
                {
                    reason =
                        $"Joint source slot {slotIndex} no longer has alternatives";
                    return false;
                }

                slotDefinitions.Add(
                    new CrossRigidJointSlotDefinition(
                        slotIndex,
                        slotElement,
                        alternatives));
            }

            var combinationAlternatives =
                new List<CrossRigidJointAlternative[]>
                {
                    Array.Empty<CrossRigidJointAlternative>(),
                };
            foreach (var slotDefinition in slotDefinitions)
            {
                var expanded =
                    new List<CrossRigidJointAlternative[]>();
                foreach (var prefix in combinationAlternatives)
                {
                    foreach (var alternative in
                             slotDefinition.Alternatives)
                    {
                        expanded.Add(
                            prefix
                                .Append(alternative)
                                .ToArray());
                        if (expanded.Count >
                            MaxCrossRigidJointStateCombinations)
                        {
                            reason =
                                $"Joint Cartesian product exceeds " +
                                $"{MaxCrossRigidJointStateCombinations:N0} combinations";
                            return false;
                        }
                    }
                }

                combinationAlternatives = expanded;
            }

            var expandedCombinationCount =
                combinationAlternatives.Sum(alternatives =>
                    GetCrossRigidJointCombinationMultiplicity(
                        alternatives));
            if (expandedCombinationCount !=
                descriptor.CombinationCount)
            {
                reason =
                    "Joint weighted Cartesian product changed since analysis";
                return false;
            }

            var jointSlot =
                definingDocument.CreateElement("SLOT");
            jointSlot.SetAttribute("probability", "1");
            jointSlot.SetAttribute(
                CrossRigidWriterSlotMarkerAttribute,
                "1");
            var combinations =
                new Dictionary<
                    string,
                    CrossRigidJointCombinationRewriteState>(
                    StringComparer.Ordinal);

            foreach (var alternatives in combinationAlternatives)
            {
                var wrapper =
                    definingDocument.CreateElement(
                        "VARIANT_MESH");
                if (parentElement.HasAttribute(
                        "use_different_attach_point_parts"))
                {
                    wrapper.SetAttribute(
                        "use_different_attach_point_parts",
                        parentElement.GetAttribute(
                            "use_different_attach_point_parts"));
                }

                var selectedElementsBySlotIndex =
                    new Dictionary<int, XmlElement>();
                foreach (var alternative in alternatives)
                {
                    var slotDefinition =
                        slotDefinitions.First(definition =>
                            definition.SlotIndex ==
                            alternative.SlotIndex);
                    if (alternative.AlternativeIndex == 0)
                    {
                        if (!IsSupportedCrossRigidOptionalJointSlot(
                                slotDefinition.SlotElement))
                        {
                            reason =
                                "Synthetic absent joint alternative belongs to a non-supported optional slot";
                            return false;
                        }

                        // Absence is materialized by omitting this slot from
                        // the deterministic wrapper. Probability is preserved
                        // by the wrapper's final integer multiplicity.
                        continue;
                    }

                    var deterministicSlot =
                        (XmlElement)slotDefinition.SlotElement
                            .CloneNode(deep: true);
                    if (ParseVmdSlotProbability(
                            deterministicSlot.GetAttribute(
                                "probability")) <
                        1.0 - AtlasValueGateExpectedDrawEpsilon)
                    {
                        deterministicSlot.SetAttribute(
                            "probability",
                            "1");
                    }

                    if (!TryReduceCrossRigidJointSlotToAlternative(
                            deterministicSlot,
                            alternative,
                            out var selectedElement,
                            out reason))
                    {
                        return false;
                    }

                    wrapper.AppendChild(
                        deterministicSlot);
                    selectedElementsBySlotIndex[
                        alternative.SlotIndex] =
                        selectedElement;
                }

                jointSlot.AppendChild(wrapper);
                var combinationKey =
                    BuildCrossRigidJointCombinationKey(
                        alternatives);
                combinations[combinationKey] =
                    new CrossRigidJointCombinationRewriteState(
                        selectedElementsBySlotIndex,
                        wrapperElement: wrapper,
                        probabilityMultiplicity:
                            GetCrossRigidJointCombinationMultiplicity(
                                alternatives));
            }

            var firstSlot =
                slotDefinitions
                    .OrderBy(definition =>
                        definition.SlotIndex)
                    .First()
                    .SlotElement;
            parentElement.InsertBefore(
                jointSlot,
                firstSlot);
            foreach (var slotDefinition in slotDefinitions)
            {
                parentElement.RemoveChild(
                    slotDefinition.SlotElement);
            }

            context =
                new CrossRigidJointRewriteContext(
                    contextKey,
                    parentLocationKey,
                    combinations);
            return true;
        }

        private static bool
            TryCreateCrossRigidCommonAncestorJointRewriteContext(
                BatchState state,
                XmlDocument definingDocument,
                string contextKey,
                string parentLocationKey,
                CrossRigidJointAlwaysPresentRewriteDescriptor descriptor,
                Dictionary<string, XmlDocument> sourceVmdDocuments,
                out CrossRigidJointRewriteContext context,
                out string reason)
        {
            context = null!;
            reason = string.Empty;
            if (definingDocument.SelectSingleNode(
                    descriptor.ParentXmlPath) is
                not XmlElement parentElement)
            {
                reason =
                    "Common-ancestor joint parent could not be resolved in mutable VMD";
                return false;
            }

            var carrierSlots =
                new Dictionary<int, XmlElement>();
            foreach (var carrierSlotIndex in
                     descriptor.SlotIndices)
            {
                if (ResolveCrossRigidFactoredRootSlot(
                        state, parentLocationKey, parentElement,
                        carrierSlotIndex) is not XmlElement carrierSlotElement)
                {
                    reason =
                        $"Common-ancestor carrier slot {carrierSlotIndex} could not be resolved";
                    return false;
                }

                var probability =
                    ParseVmdSlotProbability(
                        carrierSlotElement.GetAttribute(
                            "probability"));
                if (probability <
                    1.0 - AtlasValueGateExpectedDrawEpsilon)
                {
                    // Only direct optional dimensions may serve as
                    // carrier slots. Every emitted branch explicitly
                    // materializes their absent or probability-1 state.
                    var explicitOptionalDimension =
                        descriptor.CommonAncestorDimensions.Any(dimension =>
                            dimension.CarrierSlotIndex == carrierSlotIndex &&
                            dimension.SlotIndex == carrierSlotIndex &&
                            Normalize(dimension.OwnerVmdPath).Equals(
                                Normalize(descriptor.AnchorVmdPath),
                                StringComparison.OrdinalIgnoreCase) &&
                            dimension.ParentXmlPath.Equals(
                                descriptor.ParentXmlPath,
                                StringComparison.Ordinal) &&
                            dimension.ReferenceChainFromAnchor.Length == 0 &&
                            string.IsNullOrWhiteSpace(
                                dimension.LocalDimensionSlotRelativeXmlPath) &&
                            dimension.Alternatives.Any(alternative =>
                                alternative.AlternativeIndex == 0)) &&
                        IsSupportedCrossRigidOptionalJointSlot(
                            carrierSlotElement);
                    if (!explicitOptionalDimension)
                    {
                        reason =
                            $"Common-ancestor carrier slot {carrierSlotIndex} became an unsupported optional carrier";
                        return false;
                    }
                }

                carrierSlots[carrierSlotIndex] =
                    carrierSlotElement;
            }

            if (!TryEnumerateCrossRigidConditionalJointStates(
                    descriptor.CommonAncestorDimensions,
                    out var combinationAlternatives,
                    out reason))
            {
                return false;
            }

            var expandedCombinationCount =
                combinationAlternatives.Sum(alternatives =>
                    GetCrossRigidJointCombinationMultiplicity(
                        alternatives));
            if (expandedCombinationCount !=
                descriptor.CombinationCount)
            {
                reason =
                    "Common-ancestor weighted Cartesian product changed since analysis";
                return false;
            }

            var generatedVmdPathsBefore =
                state.CrossRigidGeneratedVmdPaths
                    .ToHashSet(
                        StringComparer.OrdinalIgnoreCase);

            var jointSlot =
                definingDocument.CreateElement("SLOT");
            jointSlot.SetAttribute("probability", "1");
            jointSlot.SetAttribute(
                CrossRigidWriterSlotMarkerAttribute,
                "1");
            var combinations =
                new Dictionary<
                    string,
                    CrossRigidJointCombinationRewriteState>(
                    StringComparer.Ordinal);

            foreach (var alternatives in
                     combinationAlternatives)
            {
                var wrapper =
                    definingDocument.CreateElement(
                        "VARIANT_MESH");
                if (parentElement.HasAttribute(
                        "use_different_attach_point_parts"))
                {
                    wrapper.SetAttribute(
                        "use_different_attach_point_parts",
                        parentElement.GetAttribute(
                            "use_different_attach_point_parts"));
                }

                var clonedCarrierSlots =
                    new Dictionary<int, XmlElement>();
                foreach (var carrierSlotIndex in
                         descriptor.SlotIndices)
                {
                    var clonedCarrierSlot =
                        (XmlElement)carrierSlots[
                            carrierSlotIndex]
                            .CloneNode(deep: true);
                    wrapper.AppendChild(
                        clonedCarrierSlot);
                    clonedCarrierSlots[
                        carrierSlotIndex] =
                        clonedCarrierSlot;
                }

                var selectedElementsByDimensionKey =
                    new Dictionary<string, XmlElement>(
                        StringComparer.Ordinal);
                var combination =
                    new CrossRigidJointCombinationRewriteState(
                        clonedCarrierSlots,
                        selectedElementsByDimensionKey,
                        wrapper,
                        GetCrossRigidJointCombinationMultiplicity(
                            alternatives));
                var combinationKey =
                    BuildCrossRigidCommonAncestorCombinationKey(
                        alternatives);

                // The source XML locations are positional XPath indices.
                // Removing an absent optional SLOT[1] shifts a later SLOT[2]
                // to position 1 in that same parent. Materialize deeper
                // dimensions first, then sibling slots in descending index
                // order, while keeping combination identity/probability in
                // its original canonical dimension order.
                var materializationOrder =
                    alternatives
                        .Select(alternative =>
                            (
                                Alternative: alternative,
                                Dimension:
                                    descriptor.CommonAncestorDimensions
                                        .First(item =>
                                            item.DimensionKey.Equals(
                                                alternative.DimensionKey,
                                                StringComparison.Ordinal))
                            ))
                        .OrderByDescending(item =>
                            item.Dimension.ReferenceChainFromAnchor.Length)
                        .ThenByDescending(item =>
                            item.Dimension.ParentXmlPath.Length)
                        .ThenByDescending(item =>
                            item.Dimension.SlotIndex)
                        .ThenBy(item =>
                            item.Dimension.DimensionKey,
                            StringComparer.Ordinal)
                        .ToArray();
                foreach (var entry in materializationOrder)
                {
                    var alternative = entry.Alternative;
                    var dimension = entry.Dimension;
                    if (alternative.AlternativeIndex == -1)
                    {
                        // This child does not exist under the selected
                        // parent branch; no subtree is materialized.
                        continue;
                    }

                    if (!TryMaterializeCrossRigidCommonAncestorDimension(
                            state,
                            contextKey,
                            combinationKey,
                            combination,
                            descriptor,
                            dimension,
                            alternative,
                            sourceVmdDocuments,
                            out var selectedElement,
                            out reason))
                    {
                        foreach (var generatedVmdPath in
                                 state.CrossRigidGeneratedVmdPaths
                                     .Where(path =>
                                         !generatedVmdPathsBefore.Contains(
                                             path))
                                     .ToArray())
                        {
                            state.CrossRigidGeneratedVmdPaths.Remove(
                                generatedVmdPath);
                            state.ModifiedVmdDocuments.Remove(
                                generatedVmdPath);
                        }

                        return false;
                    }

                    selectedElementsByDimensionKey[
                        dimension.DimensionKey] =
                        selectedElement;
                }

                jointSlot.AppendChild(wrapper);
                combinations[combinationKey] =
                    combination;
            }

            var firstCarrierSlot =
                descriptor.SlotIndices
                    .OrderBy(index => index)
                    .Select(index =>
                        carrierSlots[index])
                    .First();
            parentElement.InsertBefore(
                jointSlot,
                firstCarrierSlot);
            foreach (var carrierSlotIndex in
                     descriptor.SlotIndices)
            {
                parentElement.RemoveChild(
                    carrierSlots[
                        carrierSlotIndex]);
            }

            context =
                new CrossRigidJointRewriteContext(
                    contextKey,
                    parentLocationKey,
                    combinations);
            return true;
        }

        private static bool
            TryMaterializeCrossRigidCommonAncestorDimension(
                BatchState state,
                string contextKey,
                string combinationKey,
                CrossRigidJointCombinationRewriteState combination,
                CrossRigidJointAlwaysPresentRewriteDescriptor descriptor,
                CrossRigidJointSelectionDimension dimension,
                CrossRigidJointDimensionAlternative alternative,
                Dictionary<string, XmlDocument> sourceVmdDocuments,
                out XmlElement selectedElement,
                out string reason)
        {
            selectedElement = null!;
            reason = string.Empty;
            if (!combination.SelectedElementsBySlotIndex.TryGetValue(
                    dimension.CarrierSlotIndex,
                    out var carrierSlotElement))
            {
                reason =
                    "Common-ancestor cloned carrier slot could not be located";
                return false;
            }

            if (dimension.ReferenceChainFromAnchor.Length == 0)
            {
                var localDimensionSlotElement =
                    carrierSlotElement;
                if (!string.IsNullOrWhiteSpace(
                        dimension.LocalDimensionSlotRelativeXmlPath))
                {
                    if (carrierSlotElement.SelectSingleNode(
                            dimension.LocalDimensionSlotRelativeXmlPath) is
                        not XmlElement nestedDimensionSlotElement ||
                        !nestedDimensionSlotElement.Name.Equals(
                            "SLOT",
                            StringComparison.Ordinal))
                    {
                        reason =
                            "Common-ancestor nested local dimension slot could not be located";
                        return false;
                    }

                    localDimensionSlotElement =
                        nestedDimensionSlotElement;
                }

                if (alternative.AlternativeIndex == 0)
                {
                    if (!IsSupportedCrossRigidOptionalJointSlot(
                            localDimensionSlotElement))
                    {
                        reason =
                            "Synthetic absent common-ancestor alternative belongs to a non-supported optional slot";
                        return false;
                    }

                    var optionalParent =
                        localDimensionSlotElement.ParentNode;
                    if (optionalParent == null)
                    {
                        reason =
                            "Synthetic absent common-ancestor slot has no parent";
                        return false;
                    }

                    optionalParent.RemoveChild(
                        localDimensionSlotElement);
                    selectedElement =
                        carrierSlotElement;
                    return true;
                }

                if (ParseVmdSlotProbability(
                        localDimensionSlotElement.GetAttribute(
                            "probability")) <
                    1.0 - AtlasValueGateExpectedDrawEpsilon)
                {
                    localDimensionSlotElement.SetAttribute(
                        "probability",
                        "1");
                }

                return TryReduceCrossRigidJointSlotToAlternative(
                    localDimensionSlotElement,
                    new CrossRigidJointAlternative(
                        dimension.SlotIndex,
                        alternative.IsReference,
                        alternative.AlternativeIndex,
                        alternative.Multiplicity),
                    out selectedElement,
                    out reason);
            }

            var carrierSlotXmlPath =
                $"{descriptor.ParentXmlPath}/" +
                $"SLOT[{dimension.CarrierSlotIndex}]";
            var firstHop =
                dimension.ReferenceChainFromAnchor[0];
            if (!TryGetCrossRigidRelativeXmlPath(
                    carrierSlotXmlPath,
                    firstHop.ReferenceXmlPath,
                    out var firstReferenceRelativeXmlPath) ||
                carrierSlotElement.SelectSingleNode(
                    firstReferenceRelativeXmlPath) is
                not XmlElement currentReferenceElement ||
                currentReferenceElement.Name !=
                    "VARIANT_MESH_REFERENCE")
            {
                reason =
                    "Common-ancestor carrier reference could not be located in cloned slot";
                return false;
            }

            XmlDocument? currentDocument = null;
            var localPrefix =
                new List<CrossRigidVmdReferenceHop>();
            foreach (var hop in
                     dimension.ReferenceChainFromAnchor)
            {
                localPrefix.Add(hop);
                var branchKey =
                    string.Join(
                        "\u001f",
                        contextKey,
                        combinationKey,
                        "common-ancestor",
                        dimension.CarrierSlotIndex.ToString(
                            CultureInfo.InvariantCulture),
                        BuildCrossRigidReferenceChainSignature(
                            localPrefix));
                if (!combination.GeneratedVmdPathByBranchKey.TryGetValue(
                        branchKey,
                        out var generatedVmdPath))
                {
                    generatedVmdPath =
                        BuildCrossRigidGeneratedJointVmdPath(
                            branchKey);
                    if (state.Source.ContainsFile(
                            generatedVmdPath) ||
                        state.Output.ContainsFile(
                            generatedVmdPath))
                    {
                        reason =
                            "Deterministic generated common-ancestor VMD path already exists";
                        return false;
                    }

                    if (!TryGetCrossRigidSourceVmdDocument(
                            state,
                            hop.ReferencedVmdPath,
                            sourceVmdDocuments,
                            out var sourceChildDocument,
                            out reason))
                    {
                        return false;
                    }

                    var generatedDocument =
                        CloneXmlDocument(
                            sourceChildDocument);
                    state.ModifiedVmdDocuments[
                        generatedVmdPath] =
                        generatedDocument;
                    state.CrossRigidGeneratedVmdPaths.Add(
                        generatedVmdPath);
                    combination.GeneratedVmdPathByBranchKey[
                        branchKey] =
                        generatedVmdPath;
                }

                currentReferenceElement.SetAttribute(
                    "definition",
                    generatedVmdPath);
                currentDocument =
                    state.ModifiedVmdDocuments[
                        generatedVmdPath];

                if (state.Source.ContainsFile(
                        Normalize(hop.OwnerVmdPath)))
                {
                    state.CrossRigidRetainedSourceAssetPaths.Add(
                        Normalize(hop.OwnerVmdPath));
                }

                if (state.Source.ContainsFile(
                        Normalize(hop.ReferencedVmdPath)))
                {
                    state.CrossRigidRetainedSourceAssetPaths.Add(
                        Normalize(hop.ReferencedVmdPath));
                }

                var hopIndex =
                    localPrefix.Count - 1;
                if (hopIndex + 1 <
                    dimension.ReferenceChainFromAnchor.Length)
                {
                    var nextHop =
                        dimension.ReferenceChainFromAnchor[
                            hopIndex + 1];
                    if (currentDocument.SelectSingleNode(
                            nextHop.ReferenceXmlPath) is
                        not XmlElement nextReferenceElement ||
                        nextReferenceElement.Name !=
                            "VARIANT_MESH_REFERENCE")
                    {
                        reason =
                            "Common-ancestor nested fixed reference could not be located";
                        return false;
                    }

                    currentReferenceElement =
                        nextReferenceElement;
                }
            }

            if (currentDocument == null ||
                currentDocument.SelectSingleNode(
                    $"{dimension.ParentXmlPath}/" +
                    $"SLOT[{dimension.SlotIndex}]") is
                not XmlElement dimensionSlotElement)
            {
                reason =
                    "Common-ancestor terminal selection slot could not be located: " +
                    $"{dimension.OwnerVmdPath} / {dimension.ParentXmlPath}/" +
                    $"SLOT[{dimension.SlotIndex}], " +
                    $"combination={combinationKey}, " +
                    $"referenceHops={dimension.ReferenceChainFromAnchor.Length}";
                return false;
            }

            if (alternative.AlternativeIndex == 0)
            {
                if (!IsSupportedCrossRigidOptionalJointSlot(
                        dimensionSlotElement))
                {
                    reason =
                        "Synthetic absent referenced common-ancestor alternative belongs to a non-supported optional slot";
                    return false;
                }

                var optionalParent =
                    dimensionSlotElement.ParentNode;
                if (optionalParent == null)
                {
                    reason =
                        "Synthetic absent referenced common-ancestor slot has no parent";
                    return false;
                }

                optionalParent.RemoveChild(
                    dimensionSlotElement);
                selectedElement =
                    currentDocument.DocumentElement!;
                return true;
            }

            if (ParseVmdSlotProbability(
                    dimensionSlotElement.GetAttribute(
                        "probability")) <
                1.0 - AtlasValueGateExpectedDrawEpsilon)
            {
                dimensionSlotElement.SetAttribute(
                    "probability",
                    "1");
            }

            return TryReduceCrossRigidJointSlotToAlternative(
                dimensionSlotElement,
                new CrossRigidJointAlternative(
                    dimension.SlotIndex,
                    alternative.IsReference,
                    alternative.AlternativeIndex,
                    alternative.Multiplicity),
                out selectedElement,
                out reason);
        }

        private static CrossRigidJointAlternative[]
            GetCrossRigidJointAlternatives(
                XmlElement slotElement,
                int slotIndex)
        {
            var alternatives =
                new List<CrossRigidJointAlternative>();
            var optional =
                TryGetCrossRigidOptionalJointMultiplicities(
                    slotElement,
                    out var absentMultiplicity,
                    out var presentMultiplicity);
            if (optional)
            {
                // AlternativeIndex 0 is the synthetic "slot absent" state.
                // Integer multiplicities reproduce the authored optional
                // probability exactly once the finished wrappers are cloned.
                alternatives.Add(
                    new CrossRigidJointAlternative(
                        slotIndex,
                        false,
                        0,
                        absentMultiplicity));
            }

            var meshCount =
                slotElement.SelectNodes(
                    "VARIANT_MESH")?.Count ?? 0;
            for (var meshIndex = 1;
                 meshIndex <= meshCount;
                 meshIndex++)
            {
                alternatives.Add(
                    new CrossRigidJointAlternative(
                        slotIndex,
                        false,
                        meshIndex,
                        optional
                            ? presentMultiplicity
                            : 1));
            }

            var referenceCount =
                slotElement.SelectNodes(
                    "VARIANT_MESH_REFERENCE")?.Count ?? 0;
            for (var referenceIndex = 1;
                 referenceIndex <= referenceCount;
                 referenceIndex++)
            {
                alternatives.Add(
                    new CrossRigidJointAlternative(
                        slotIndex,
                        true,
                        referenceIndex,
                        optional
                            ? presentMultiplicity
                            : 1));
            }

            return alternatives.ToArray();
        }

        // Diagnostic-only product calculation, saturating rather than
        // overflowing on unusually large source VMD combinations.
        private static long SaturatingCrossRigidStateProduct(
            long current,
            long factor)
            => current > long.MaxValue / Math.Max(1, factor)
                ? long.MaxValue
                : current * Math.Max(1, factor);

        private static int GetCrossRigidJointCombinationMultiplicity(
            IEnumerable<CrossRigidJointAlternative> alternatives)
            => alternatives.Aggregate(
                1,
                (product, alternative) =>
                    checked(
                        product *
                        Math.Max(
                            1,
                            alternative.Multiplicity)));

        private static int GetCrossRigidJointCombinationMultiplicity(
            IEnumerable<CrossRigidJointDimensionAlternative> alternatives)
            => alternatives.Aggregate(
                1,
                (product, alternative) =>
                    checked(
                        product *
                        Math.Max(
                            1,
                            alternative.Multiplicity)));

        private static bool TryReduceCrossRigidJointSlotToAlternative(
            XmlElement slotElement,
            CrossRigidJointAlternative alternative,
            out XmlElement selectedElement,
            out string reason)
        {
            selectedElement = null!;
            reason = string.Empty;
            var meshElements =
                slotElement.SelectNodes(
                        "VARIANT_MESH")
                    ?.Cast<XmlElement>()
                    .ToArray() ?? [];
            var referenceElements =
                slotElement.SelectNodes(
                        "VARIANT_MESH_REFERENCE")
                    ?.Cast<XmlElement>()
                    .ToArray() ?? [];

            XmlElement? selected;
            if (alternative.IsReference)
            {
                selected =
                    alternative.AlternativeIndex <=
                    referenceElements.Length
                        ? referenceElements[
                            alternative.AlternativeIndex - 1]
                        : null;
            }
            else
            {
                selected =
                    alternative.AlternativeIndex <=
                    meshElements.Length
                        ? meshElements[
                            alternative.AlternativeIndex - 1]
                        : null;
            }

            if (selected == null)
            {
                reason =
                    "Joint selected alternative no longer exists";
                return false;
            }

            foreach (var element in meshElements)
            {
                if (!ReferenceEquals(
                        element,
                        selected))
                {
                    slotElement.RemoveChild(element);
                }
            }

            foreach (var element in referenceElements)
            {
                if (!ReferenceEquals(
                        element,
                        selected))
                {
                    slotElement.RemoveChild(element);
                }
            }

            selectedElement = selected;
            return true;
        }

        private static bool TryResolveCrossRigidJointSelectionModelElement(
            BatchState state,
            string contextKey,
            string combinationKey,
            CrossRigidJointCombinationRewriteState combination,
            CrossRigidJointSourceSelection selection,
            Dictionary<string, XmlDocument> sourceVmdDocuments,
            out XmlElement modelElement,
            out string reason)
        {
            modelElement = null!;
            reason = string.Empty;
            var dimensionKey =
                BuildCrossRigidJointSourceSelectionDimensionKey(
                    selection);
            if (!combination.SelectedElementsByDimensionKey.TryGetValue(
                    dimensionKey,
                    out var selectedElement) &&
                !combination.SelectedElementsBySlotIndex.TryGetValue(
                    selection.SlotIndex,
                    out selectedElement))
            {
                reason =
                    "Joint selected structural alternative could not be located";
                return false;
            }

            if (selectedElement.Name == "SLOT")
            {
                var selectedNodeName =
                    selection.IsReference
                        ? "VARIANT_MESH_REFERENCE"
                        : "VARIANT_MESH";
                if (selectedElement.SelectSingleNode(
                        $"{selectedNodeName}[{selection.AlternativeIndex}]") is
                    not XmlElement fixedSelectedElement)
                {
                    reason =
                        "Joint fixed carrier alternative could not be located";
                    return false;
                }

                selectedElement =
                    fixedSelectedElement;
            }

            var instance = selection.Instance;
            XmlElement currentReferenceElement;
            if (!selection.IsReference)
            {
                if (selectedElement.Name != "VARIANT_MESH")
                {
                    reason =
                        "Joint direct selection is no longer a VARIANT_MESH";
                    return false;
                }

                if (selection.AnchorReferenceHopIndex < 0)
                {
                    if (string.IsNullOrWhiteSpace(
                            selection.DirectModelRelativeXmlPath))
                    {
                        modelElement = selectedElement;
                        return true;
                    }

                    if (selectedElement.SelectSingleNode(
                            selection.DirectModelRelativeXmlPath) is
                        not XmlElement nestedModelElement)
                    {
                        reason =
                            "Joint direct model subtree occurrence could not be located";
                        return false;
                    }

                    modelElement = nestedModelElement;
                    return true;
                }

                if (selection.AnchorReferenceHopIndex >=
                        instance.ReferenceChain.Length ||
                    string.IsNullOrWhiteSpace(
                        selection.DirectFirstReferenceRelativeXmlPath) ||
                    selectedElement.SelectSingleNode(
                        selection.DirectFirstReferenceRelativeXmlPath) is
                    not XmlElement nestedReferenceElement ||
                    nestedReferenceElement.Name !=
                        "VARIANT_MESH_REFERENCE")
                {
                    reason =
                        "Joint direct-selection child-VMD provenance is invalid";
                    return false;
                }

                currentReferenceElement =
                    nestedReferenceElement;
            }
            else
            {
                if (selection.AnchorReferenceHopIndex < 0 ||
                    selection.AnchorReferenceHopIndex >=
                        instance.ReferenceChain.Length ||
                    selectedElement.Name !=
                        "VARIANT_MESH_REFERENCE")
                {
                    reason =
                        "Joint reference selection provenance is invalid";
                    return false;
                }

                currentReferenceElement =
                    selectedElement;
            }

            XmlDocument? currentDocument = null;
            var localPrefix =
                new List<CrossRigidVmdReferenceHop>();
            for (var hopIndex =
                     selection.AnchorReferenceHopIndex;
                 hopIndex < instance.ReferenceChain.Length;
                 hopIndex++)
            {
                var hop = instance.ReferenceChain[hopIndex];
                localPrefix.Add(hop);
                var branchKey = string.Join(
                    "\u001f",
                    contextKey,
                    combinationKey,
                    dimensionKey,
                    BuildCrossRigidReferenceChainSignature(
                        localPrefix));
                if (!combination.GeneratedVmdPathByBranchKey.TryGetValue(
                        branchKey,
                        out var generatedVmdPath))
                {
                    generatedVmdPath =
                        BuildCrossRigidGeneratedJointVmdPath(
                            branchKey);
                    if (state.Source.ContainsFile(generatedVmdPath) ||
                        state.Output.ContainsFile(generatedVmdPath))
                    {
                        reason =
                            "Deterministic generated joint VMD path already exists";
                        return false;
                    }

                    if (!TryGetCrossRigidSourceVmdDocument(
                            state,
                            hop.ReferencedVmdPath,
                            sourceVmdDocuments,
                            out var sourceChildDocument,
                            out reason))
                    {
                        return false;
                    }

                    var generatedDocument =
                        CloneXmlDocument(
                            sourceChildDocument);
                    state.ModifiedVmdDocuments[
                        generatedVmdPath] =
                        generatedDocument;
                    state.CrossRigidGeneratedVmdPaths.Add(
                        generatedVmdPath);
                    combination.GeneratedVmdPathByBranchKey[
                        branchKey] =
                        generatedVmdPath;
                }

                currentReferenceElement.SetAttribute(
                    "definition",
                    generatedVmdPath);
                currentDocument =
                    state.ModifiedVmdDocuments[
                        generatedVmdPath];

                if (state.Source.ContainsFile(
                        Normalize(hop.OwnerVmdPath)))
                {
                    state.CrossRigidRetainedSourceAssetPaths.Add(
                        Normalize(hop.OwnerVmdPath));
                }

                if (state.Source.ContainsFile(
                        Normalize(hop.ReferencedVmdPath)))
                {
                    state.CrossRigidRetainedSourceAssetPaths.Add(
                        Normalize(hop.ReferencedVmdPath));
                }

                if (hopIndex + 1 <
                    instance.ReferenceChain.Length)
                {
                    var nextHop =
                        instance.ReferenceChain[hopIndex + 1];
                    if (currentDocument.SelectSingleNode(
                            nextHop.ReferenceXmlPath) is
                        not XmlElement nextReferenceElement)
                    {
                        reason =
                            $"Could not locate joint child VMD reference at " +
                            nextHop.ReferenceXmlPath;
                        return false;
                    }

                    currentReferenceElement =
                        nextReferenceElement;
                }
            }

            if (currentDocument == null ||
                currentDocument.SelectSingleNode(
                    instance.ModelXmlPath) is
                not XmlElement resolvedModelElement)
            {
                reason =
                    $"Could not locate joint model occurrence at " +
                    instance.ModelXmlPath;
                return false;
            }

            modelElement = resolvedModelElement;
            return true;
        }

        private static string BuildCrossRigidGeneratedJointVmdPath(
            string branchKey)
            => Normalize(
                $"variantmeshes\\variantmeshdefinitions\\asset_editor\\cross_rigid\\joint_" +
                $"{ContentHash(branchKey).ToLowerInvariant()}.variantmeshdefinition");

        private static string BuildCrossRigidJointParentLocationKey(
            string rootVmdPath,
            CrossRigidJointAlwaysPresentRewriteDescriptor descriptor)
            => string.Join(
                "\u001f",
                Normalize(rootVmdPath),
                BuildCrossRigidReferenceChainSignature(
                    descriptor.AnchorReferenceChain),
                descriptor.AnchorVmdPath,
                descriptor.ParentXmlPath);

        private static string BuildCrossRigidJointContextKey(
            string rootVmdPath,
            CrossRigidJointAlwaysPresentRewriteDescriptor descriptor)
            => string.Join(
                "\u001f",
                BuildCrossRigidJointParentLocationKey(
                    rootVmdPath,
                    descriptor),
                descriptor.CommonAncestorDimensions.Length == 0
                    ? string.Join(
                        ",",
                        descriptor.SlotIndices)
                    : string.Join(
                        "\u001e",
                        $"slots={string.Join(",", descriptor.SlotIndices)}",
                        $"dimensions={string.Join(
                            ",",
                            descriptor.CommonAncestorDimensions
                                .Select(dimension =>
                                    dimension.DimensionKey)
                                .OrderBy(
                                    key => key,
                                    StringComparer.Ordinal))}"));

        private static string
            BuildCrossRigidJointSelectedCombinationKey(
                CrossRigidJointAlwaysPresentRewriteDescriptor descriptor)
        {
            if (descriptor.CommonAncestorDimensions.Length == 0)
            {
                return BuildCrossRigidJointCombinationKey(
                    descriptor.SourceSelections
                        .Select(selection =>
                            new CrossRigidJointAlternative(
                                selection.SlotIndex,
                                selection.IsReference,
                                selection.AlternativeIndex))
                        .Distinct());
            }

            var dimensionKeys =
                descriptor.CommonAncestorDimensions
                    .Select(dimension =>
                        dimension.DimensionKey)
                    .ToHashSet(StringComparer.Ordinal);
            return BuildCrossRigidCommonAncestorCombinationKey(
                descriptor.SourceSelections
                    .Where(selection =>
                        dimensionKeys.Contains(
                            BuildCrossRigidJointSourceSelectionDimensionKey(
                                selection)))
                    .Select(selection =>
                        new CrossRigidJointDimensionAlternative(
                            BuildCrossRigidJointSourceSelectionDimensionKey(
                                selection),
                            selection.IsReference,
                            selection.AlternativeIndex))
                    .Distinct());
        }

        private static string BuildCrossRigidJointCombinationKey(
            IEnumerable<CrossRigidJointAlternative> alternatives)
            => string.Join(
                "|",
                alternatives
                    .OrderBy(alternative =>
                        alternative.SlotIndex)
                    .Select(alternative =>
                        $"{alternative.SlotIndex}:" +
                        $"{(alternative.IsReference ? "r" : "m")}" +
                        $"{alternative.AlternativeIndex}"));

        private static string
            BuildCrossRigidCommonAncestorCombinationKey(
                IEnumerable<CrossRigidJointDimensionAlternative>
                    alternatives)
            => string.Join(
                "|",
                alternatives
                    .OrderBy(
                        alternative =>
                            alternative.DimensionKey,
                        StringComparer.Ordinal)
                    .Select(alternative =>
                        $"{alternative.DimensionKey}:" +
                        $"{(alternative.IsReference ? "r" : "m")}" +
                        $"{alternative.AlternativeIndex}"));

        private static bool TryGetCrossRigidSourceVmdDocument(
            BatchState state,
            string vmdPathValue,
            Dictionary<string, XmlDocument> cache,
            out XmlDocument document,
            out string reason)
        {
            var vmdPath = Normalize(vmdPathValue);
            if (cache.TryGetValue(vmdPath, out document!))
            {
                reason = string.Empty;
                return true;
            }

            var container = FindGameplayTraversalContainer(
                state,
                state.Source,
                GetGameplayTraversalContainers(state),
                vmdPath);
            var file = container?.FindFile(vmdPath);
            if (container == null || file == null)
            {
                document = null!;
                reason =
                    $"Referenced VMD could not be resolved: {vmdPath}";
                return false;
            }

            try
            {
                document = LoadXml(file);
                cache[vmdPath] = document;
                reason = string.Empty;
                return true;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                XmlException or
                FormatException or
                ArgumentException)
            {
                document = null!;
                reason =
                    $"Referenced VMD could not be parsed: " +
                    $"{vmdPath} ({ex.Message})";
                return false;
            }
        }

        private static XmlDocument CloneXmlDocument(
            XmlDocument source)
        {
            var clone = new XmlDocument();
            clone.LoadXml(source.OuterXml);
            return clone;
        }

        private static string BuildCrossRigidConsumedOccurrenceKey(
            string rootVmdPath,
            CrossRigidVisualInstance instance)
            => string.Join(
                "\u001f",
                Normalize(rootVmdPath),
                BuildCrossRigidOccurrenceProvenanceSignature(
                    [instance]));

        private static string BuildCrossRigidGeneratedVmdBranchKey(
            string rootVmdPath,
            IReadOnlyList<CrossRigidVmdReferenceHop> prefixHops)
            => string.Join(
                "\u001f",
                Normalize(rootVmdPath),
                string.Join(
                    "\u001e",
                    prefixHops.Select(hop => string.Join(
                        "|",
                        Normalize(hop.OwnerVmdPath),
                        hop.ReferenceXmlPath,
                        Normalize(hop.ReferencedVmdPath)))));

        private static string BuildCrossRigidGeneratedVmdPath(
            string branchKey)
            => Normalize(
                $"variantmeshes\\variantmeshdefinitions\\asset_editor\\cross_rigid\\" +
                $"{ContentHash(branchKey).ToLowerInvariant()}.variantmeshdefinition");

        private static bool TryBuildCrossRigidGeneratedPayload(
            BatchState state,
            CrossRigidMergeAnalysisEntry plan,
            IReadOnlyList<IPackFileContainer> traversalContainers,
            Dictionary<string, RmvFile> rigidCache,
            Dictionary<string, CrossRigidAnalysisComponent>
                sourceComponentCache,
            out string reason)
        {
            reason = string.Empty;
            var buildTimer = Stopwatch.GetTimestamp();
            var generatedRigidPath =
                BuildCrossRigidGeneratedRigidPath(
                    plan.GeneratedPayloadId);
            var generatedWsModelPath =
                BuildCrossRigidGeneratedWsModelPath(
                    plan.GeneratedPayloadId);

            if (state.Source.ContainsFile(generatedRigidPath) ||
                state.Source.ContainsFile(generatedWsModelPath) ||
                state.Output.ContainsFile(generatedRigidPath) ||
                state.Output.ContainsFile(generatedWsModelPath))
            {
                reason =
                    "deterministic generated asset path already exists in the input/output pack";
                return false;
            }

            try
            {
                var componentsByWsModel =
                    new Dictionary<string, CrossRigidAnalysisComponent>(
                        StringComparer.OrdinalIgnoreCase);
                foreach (var wsModelPathValue in plan.SourceInstances
                             .Select(instance => instance.WsModelPath)
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var wsModelPath = Normalize(wsModelPathValue);
                    if (!sourceComponentCache.TryGetValue(
                            wsModelPath,
                            out var component))
                    {
                        if (!TryBuildCrossRigidAnalysisComponent(
                                state,
                                plan.VmdPath,
                                wsModelPath,
                                traversalContainers,
                                rigidCache,
                                out component,
                                out var componentReason))
                        {
                            reason =
                                $"{wsModelPath}: {componentReason}";
                            return false;
                        }

                        sourceComponentCache[wsModelPath] = component;
                    }

                    componentsByWsModel[wsModelPath] =
                        component;
                }

                AddCrossRigidTimingDuration(
                    state,
                    "Emit payload: resolve source components",
                    Stopwatch.GetElapsedTime(buildTimer));
                buildTimer = Stopwatch.GetTimestamp();
                var components = componentsByWsModel.Values
                    .OrderBy(
                        component => component.WsModelPath,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (components.Length < 2)
                {
                    reason =
                        "fewer than two distinct source WSModels remain";
                    return false;
                }

                if (components
                        .Select(component =>
                            component.TopologyIdentity)
                        .Distinct(StringComparer.Ordinal)
                        .Count() != 1)
                {
                    reason =
                        "source WSModel non-material topology is no longer identical";
                    return false;
                }

                if (!AreCrossRigidRigidHeadersCompatible(
                        components,
                        out reason))
                {
                    return false;
                }

                var representative = components[0];
                var generatedLodCount = plan.Lods.Length;
                var generatedModels = new RmvModel[generatedLodCount][];
                var generatedAssignments = new string[generatedLodCount][];

                for (var generatedLodIndex = 0;
                     generatedLodIndex < generatedLodCount;
                     generatedLodIndex++)
                {
                    var lodPlan = plan.Lods[generatedLodIndex];
                    if (lodPlan.LodIndex != generatedLodIndex)
                    {
                        reason =
                            $"generated LOD plan index mismatch: expected {generatedLodIndex}, " +
                            $"found {lodPlan.LodIndex}";
                        return false;
                    }

                    var sourceLodByWsModel =
                        lodPlan.SourceLodSelections.ToDictionary(
                            selection => Normalize(selection.WsModelPath),
                            selection => selection.SourceLodIndex,
                            StringComparer.OrdinalIgnoreCase);
                    var sourceParts =
                        new List<CrossRigidGeneratedSourcePart>();
                    for (var instanceIndex = 0;
                         instanceIndex < plan.SourceInstances.Length;
                         instanceIndex++)
                    {
                        var instance =
                            plan.SourceInstances[instanceIndex];
                        var wsModelPath =
                            Normalize(instance.WsModelPath);
                        if (!sourceLodByWsModel.TryGetValue(
                                wsModelPath,
                                out var sourceLodIndex))
                        {
                            continue;
                        }

                        var component =
                            componentsByWsModel[wsModelPath];
                        if (sourceLodIndex < 0 ||
                            sourceLodIndex >=
                            component.Rigid.ModelList.Length)
                        {
                            reason =
                                $"generated LOD {generatedLodIndex} has invalid source LOD " +
                                $"{sourceLodIndex} for {wsModelPath}";
                            return false;
                        }

                        var models =
                            component.Rigid.ModelList[sourceLodIndex];
                        var assignments =
                            component.MaterialAssignments[sourceLodIndex];

                        for (var partIndex = 0;
                             partIndex < models.Length;
                             partIndex++)
                        {
                            var mergeIdentity =
                                GetCrossRigidSourcePartMergeIdentity(
                                    state,
                                    component.RigidPath,
                                    sourceLodIndex,
                                    partIndex,
                                    models[partIndex],
                                    assignments[partIndex]);

                            sourceParts.Add(
                                new CrossRigidGeneratedSourcePart(
                                    instanceIndex,
                                    component.WsModelPath,
                                    component.RigidPath,
                                    partIndex,
                                    models[partIndex],
                                    assignments[partIndex],
                                    mergeIdentity));
                        }
                    }

                    var outputModels = new List<RmvModel>();
                    var outputMaterials = new List<string>();
                    foreach (var identityGroup in sourceParts
                                 .GroupBy(
                                     part => part.MergeIdentity,
                                     StringComparer.Ordinal)
                                 .OrderBy(
                                     group => group.Key,
                                     StringComparer.Ordinal))
                    {
                        foreach (var bin in
                                 PartitionCrossRigidGeneratedParts(
                                     identityGroup.ToArray()))
                        {
                            var orderedBin = bin
                                .OrderBy(part =>
                                    part.InstanceIndex)
                                .ThenBy(
                                    part => part.WsModelPath,
                                    StringComparer.OrdinalIgnoreCase)
                                .ThenBy(part =>
                                    part.PartIndex)
                                .ToArray();
                            outputModels.Add(
                                MergeRmvModelsOwned(
                                    orderedBin
                                        .Select(part => part.Model)
                                        .ToArray()));
                            outputMaterials.Add(
                                orderedBin
                                    .OrderBy(
                                        part => Normalize(
                                            part.MaterialPath),
                                        StringComparer.OrdinalIgnoreCase)
                                    .ThenBy(part =>
                                        part.InstanceIndex)
                                    .First()
                                    .MaterialPath);
                        }
                    }

                    if (outputModels.Count !=
                        lodPlan.OutputDrawsAfterCrossRigidMerge)
                    {
                        reason =
                            $"LOD {generatedLodIndex} emitted {outputModels.Count} draw(s), " +
                            $"but analysis selected {lodPlan.OutputDrawsAfterCrossRigidMerge}";
                        return false;
                    }

                    generatedModels[generatedLodIndex] =
                        outputModels.ToArray();
                    generatedAssignments[generatedLodIndex] =
                        outputMaterials.ToArray();
                }

                var generatedVersion =
                    components
                        .Select(component =>
                            component.Rigid.Header.Version)
                        .Max();
                var header = representative.Rigid.Header;
                header.Version = generatedVersion;
                header.LodCount = (uint)generatedLodCount;
                var lodHeaderFactory =
                    LodHeaderFactory.Create();
                var generatedRigid = new RmvFile
                {
                    Header = header,
                    LodHeaders = plan.Lods
                        .Select((lod, generatedLodIndex) =>
                        {
                            var representativeSelection =
                                lod.SourceLodSelections.FirstOrDefault(
                                    selection =>
                                        selection.WsModelPath.Equals(
                                            representative.WsModelPath,
                                            StringComparison.OrdinalIgnoreCase));
                            var sourceHeaderIndex =
                                representativeSelection?.SourceLodIndex ??
                                Math.Min(
                                    lod.ScenarioLodIndex,
                                    representative.Rigid.LodHeaders.Length - 1);
                            var generatedLodHeader =
                                lodHeaderFactory.CreateFromBase(
                                    generatedVersion,
                                    representative.Rigid.LodHeaders[
                                        sourceHeaderIndex],
                                    (uint)generatedLodIndex);
                            generatedLodHeader.QualityLvl =
                                lod.QualityLevel;
                            generatedLodHeader.LodCameraDistance =
                                lod.CameraDistance;
                            return generatedLodHeader;
                        })
                        .ToArray(),
                    ModelList = generatedModels,
                };
                generatedRigid.RecalculateOffsets();
                AddCrossRigidTimingDuration(
                    state,
                    "Emit payload: assemble and merge LOD geometry",
                    Stopwatch.GetElapsedTime(buildTimer));
                buildTimer = Stopwatch.GetTimestamp();

                // The generated models were built by MergeRmvModelsOwned, so they
                // already own independent model/mesh/material/vertex objects and
                // later geometry optimization cannot touch the source rigids.
                var generatedWsDocument = new XmlDocument();
                generatedWsDocument.LoadXml(
                    representative.WsDocument.OuterXml);
                var geometryNode =
                    generatedWsDocument.SelectSingleNode(
                        "/model/geometry");
                if (geometryNode == null)
                {
                    reason =
                        "representative WSModel has no geometry node";
                    return false;
                }

                geometryNode.InnerText = generatedRigidPath;
                RewriteWsMaterialAssignments(
                    generatedWsDocument,
                    generatedAssignments);
                if (!TryReadWsMaterialAssignments(
                        generatedWsDocument,
                        generatedRigid,
                        out _,
                        out var assignmentReason))
                {
                    reason =
                        $"generated WSModel material table is invalid ({assignmentReason})";
                    return false;
                }

                state.RigidModels[generatedRigidPath] =
                    generatedRigid;
                state.WsDocuments[generatedWsModelPath] =
                    generatedWsDocument;
                state.ModifiedRigids.Add(
                    generatedRigidPath);
                state.ModifiedWsModels.Add(
                    generatedWsModelPath);
                state.CrossRigidGeneratedPayloadIds.Add(
                    plan.GeneratedPayloadId);
                state.CrossRigidGeneratedRigidPaths.Add(
                    generatedRigidPath);
                state.CrossRigidGeneratedWsModelPaths.Add(
                    generatedWsModelPath);
                AddCrossRigidTimingDuration(
                    state,
                    "Emit payload: generate and validate WSModel",
                    Stopwatch.GetElapsedTime(buildTimer));
                return true;
            }
            catch (Exception ex) when (
                ex is InvalidDataException or
                InvalidOperationException or
                ArgumentException or
                FormatException or
                OverflowException)
            {
                reason = ex.Message;
                return false;
            }
        }

        private static bool AreCrossRigidRigidHeadersCompatible(
            IReadOnlyList<CrossRigidAnalysisComponent> components,
            out string reason)
        {
            reason = string.Empty;
            if (components.Count == 0)
            {
                reason = "no rigid components were supplied";
                return false;
            }

            var baseline = components[0].Rigid;
            var baselineSkeleton =
                baseline.Header.SkeletonName;
            foreach (var component in components)
            {
                var rigid = component.Rigid;
                if (!string.Equals(
                        rigid.Header.SkeletonName,
                        baselineSkeleton,
                        StringComparison.OrdinalIgnoreCase))
                {
                    reason =
                        "source rigid skeletons differ";
                    return false;
                }

                if (rigid.ModelList.Length == 0 ||
                    rigid.LodHeaders.Length == 0 ||
                    rigid.ModelList.Length !=
                        rigid.LodHeaders.Length)
                {
                    reason =
                        "source rigid LOD model/header counts are inconsistent";
                    return false;
                }
            }

            return true;
        }

        private static IReadOnlyList<
                IReadOnlyList<CrossRigidGeneratedSourcePart>>
            PartitionCrossRigidGeneratedParts(
                IReadOnlyList<CrossRigidGeneratedSourcePart> parts)
        {
            var bins =
                new List<List<CrossRigidGeneratedSourcePart>>();
            var vertexCounts = new List<int>();

            foreach (var part in parts
                         .OrderByDescending(item =>
                             item.Model.Mesh.VertexList.Length)
                         .ThenBy(
                             item => item.WsModelPath,
                             StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item =>
                             item.InstanceIndex)
                         .ThenBy(item =>
                             item.PartIndex))
            {
                var vertexCount =
                    part.Model.Mesh.VertexList.Length;
                if (vertexCount > ushort.MaxValue)
                {
                    bins.Add([part]);
                    vertexCounts.Add(vertexCount);
                    continue;
                }

                var placed = false;
                for (var binIndex = 0;
                     binIndex < bins.Count;
                     binIndex++)
                {
                    if (vertexCounts[binIndex] >
                            ushort.MaxValue ||
                        vertexCounts[binIndex] +
                            vertexCount >
                            ushort.MaxValue)
                    {
                        continue;
                    }

                    bins[binIndex].Add(part);
                    vertexCounts[binIndex] +=
                        vertexCount;
                    placed = true;
                    break;
                }

                if (!placed)
                {
                    bins.Add([part]);
                    vertexCounts.Add(vertexCount);
                }
            }

            return bins;
        }


        private static string BuildCrossRigidGeneratedRigidPath(
            string payloadId)
            => Normalize(
                $"variantmeshes\\wh_variantmodels\\asset_editor\\cross_rigid\\{payloadId.ToLowerInvariant()}.rigid_model_v2");

        private static string BuildCrossRigidGeneratedWsModelPath(
            string payloadId)
            => Normalize(
                $"variantmeshes\\wh_variantmodels\\asset_editor\\cross_rigid\\{payloadId.ToLowerInvariant()}.wsmodel");

        private static void RecordCrossRigidEmissionSkip(
            BatchState state,
            string reason,
            string example)
        {
            state.CrossRigidEmissionSkipCounts[reason] =
                state.CrossRigidEmissionSkipCounts
                    .GetValueOrDefault(reason) + 1;
            if (!state.CrossRigidEmissionSkipExamples.TryGetValue(
                    reason,
                    out var examples))
            {
                examples = [];
                state.CrossRigidEmissionSkipExamples[reason] =
                    examples;
            }

            if (examples.Count < 12 &&
                !examples.Contains(
                    example,
                    StringComparer.Ordinal))
            {
                examples.Add(example);
            }
        }

        private static Dictionary<
                CrossRigidWsModelOccurrenceKey,
                double>
            BuildCrossRigidRewriteCoverageByOccurrence(
                BatchState state)
        {
            var coverage = new Dictionary<
                CrossRigidWsModelOccurrenceKey,
                double>();
            foreach (var plan in state.CrossRigidMergeAnalysisEntries)
            {
                foreach (var instance in plan.SourceInstances.Distinct())
                {
                    var key = new CrossRigidWsModelOccurrenceKey(
                        plan.VmdPath,
                        instance.AttachmentIdentity,
                        instance.WsModelPath);
                    coverage[key] = Math.Clamp(
                        coverage.GetValueOrDefault(key) +
                        plan.AggregatedConfigurationProbability,
                        0.0,
                        1.0);
                }
            }

            return coverage;
        }

        private static bool IsCrossRigidSourceRigidFullyCovered(
            BatchState state,
            GameplayMeshDependencyIndex dependencyIndex,
            string rigidPath,
            IReadOnlyDictionary<
                CrossRigidWsModelOccurrenceKey,
                double> rewriteCoverage,
            out string[] wsConsumers)
        {
            wsConsumers = dependencyIndex.WsModelConsumersByRigid
                .GetValueOrDefault(rigidPath)?
                .OrderBy(
                    path => path,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];
            if (wsConsumers.Length == 0 ||
                dependencyIndex.IsAssetAffectedByIncompleteRoot(rigidPath))
            {
                return false;
            }

            if (dependencyIndex.DirectConsumersByRigid.TryGetValue(
                    rigidPath,
                    out var directConsumers) &&
                directConsumers.Count != 0)
            {
                return false;
            }

            return wsConsumers.All(wsModelPath =>
                IsCrossRigidWsModelFullyCovered(
                    state,
                    dependencyIndex,
                    wsModelPath,
                    rewriteCoverage));
        }

        private static bool IsCrossRigidWsModelFullyCovered(
            BatchState state,
            GameplayMeshDependencyIndex dependencyIndex,
            string wsModelPathValue,
            IReadOnlyDictionary<
                CrossRigidWsModelOccurrenceKey,
                double> rewriteCoverage)
        {
            var wsModelPath = Normalize(wsModelPathValue);
            if (wsModelPath.Length == 0 ||
                dependencyIndex.IsAssetAffectedByIncompleteRoot(
                    wsModelPath))
            {
                return false;
            }

            var occurrences =
                state.CrossRigidWsModelOccurrenceProbabilities
                    .Where(entry =>
                        entry.Key.WsModelPath.Equals(
                            wsModelPath,
                            StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            if (occurrences.Length == 0)
                return false;

            var occurrenceRoots = occurrences
                .Select(entry => entry.Key.VmdPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (dependencyIndex.GetRootsForAsset(wsModelPath)
                .Any(root => !occurrenceRoots.Contains(root)))
            {
                return false;
            }

            return occurrences.All(entry =>
                rewriteCoverage.GetValueOrDefault(entry.Key) >=
                entry.Value - AtlasValueGateExpectedDrawEpsilon);
        }

        private static double
            CalculateExpectedCrossRigidPayloadResidentProbability(
                BatchState state,
                IReadOnlyList<CrossRigidMergeAnalysisEntry> rewritePlans)
        {
            var model = state.ArmyResidencyModel;
            if (model == null || rewritePlans.Count == 0)
                return 0;

            var probabilityByVmd = rewritePlans
                .GroupBy(
                    plan => plan.VmdPath,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => Math.Clamp(
                        group.Sum(plan =>
                            plan.AggregatedConfigurationProbability),
                        0.0,
                        1.0),
                    StringComparer.OrdinalIgnoreCase);
            var roots = probabilityByVmd.Keys.ToArray();
            var relevantUnitIds = GetRelevantArmyUnitIds(
                model,
                Array.Empty<string>(),
                roots);
            var relevantCultures = GetRelevantArmyCultures(
                model,
                Array.Empty<string>(),
                roots,
                relevantUnitIds);
            if (relevantUnitIds.Count == 0 ||
                relevantCultures.Count == 0)
            {
                return 0;
            }

            var probabilitiesByCulture =
                new Dictionary<string, double>(
                    StringComparer.OrdinalIgnoreCase);
            foreach (var culture in relevantCultures)
            {
                if (!model.UnitsByCultureAndCategory.TryGetValue(
                        culture,
                        out var unitsByCategory))
                {
                    continue;
                }

                var notResidentProbability = 1.0;
                foreach (var (category, slotCount) in
                         model.Scenario.ArmySlotTemplate)
                {
                    if (!unitsByCategory.TryGetValue(
                            category,
                            out var cultureUnits) ||
                        cultureUnits.Count == 0)
                    {
                        continue;
                    }

                    double perSlotPresenceProbability = 0;
                    foreach (var unitId in
                             EnumerateRelevantArmyUnitIds(
                                 cultureUnits,
                                 relevantUnitIds))
                    {
                        if (!model.DirectVmdsByUnitAndRole.TryGetValue(
                                unitId,
                                out var vmdsByRole))
                        {
                            continue;
                        }

                        var notPresentAcrossRoles = 1.0;
                        foreach (var (role, directVmds) in vmdsByRole)
                        {
                            if (directVmds.Count == 0)
                                continue;

                            double perEntityPresenceProbability = 0;
                            foreach (var vmdPath in directVmds)
                            {
                                if (!probabilityByVmd.TryGetValue(
                                        vmdPath,
                                        out var configurationProbability))
                                {
                                    continue;
                                }

                                perEntityPresenceProbability +=
                                    configurationProbability /
                                    directVmds.Count;
                            }

                            perEntityPresenceProbability = Math.Clamp(
                                perEntityPresenceProbability,
                                0.0,
                                1.0);
                            if (perEntityPresenceProbability <= 0)
                                continue;

                            var entityCount = Math.Max(
                                1,
                                model.EntityCountByUnitAndRole
                                    .GetValueOrDefault(unitId)?
                                    .GetValueOrDefault(role, 1) ?? 1);
                            notPresentAcrossRoles *= Math.Pow(
                                1.0 - perEntityPresenceProbability,
                                entityCount);
                        }

                        var unitCardPresenceProbability =
                            1.0 - notPresentAcrossRoles;
                        if (unitCardPresenceProbability <= 0)
                            continue;

                        perSlotPresenceProbability +=
                            unitCardPresenceProbability /
                            cultureUnits.Count;
                    }

                    perSlotPresenceProbability = Math.Clamp(
                        perSlotPresenceProbability,
                        0.0,
                        1.0);
                    notResidentProbability *= Math.Pow(
                        1.0 - perSlotPresenceProbability,
                        slotCount);
                }

                probabilitiesByCulture[culture] = Math.Clamp(
                    1.0 - notResidentProbability,
                    0.0,
                    1.0);
            }

            return CombineBattleResidentProbabilities(
                probabilitiesByCulture,
                model.PlayerCultureWeights,
                model.OpponentCultureWeights);
        }
    }
}
