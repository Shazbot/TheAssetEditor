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
        private static void AnalyzeCrossRigidMergeOpportunities(
            BatchState state,
            IReadOnlyList<string> vmdRoots,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            state.CrossRigidMergeAnalysisEntries.Clear();
            state.CrossRigidGeneratedPayloadAnalysisEntries.Clear();
            state.CrossRigidAnalysisBlockerCounts.Clear();
            state.CrossRigidAnalysisBlockerExamples.Clear();
            state.CrossRigidRigidHeaderBlockerCounts.Clear();
            state.CrossRigidRigidHeaderBlockerRigidCombinations.Clear();
            state.CrossRigidRigidHeaderBlockerVmdRoots.Clear();
            state.CrossRigidRigidHeaderQualityProfiles.Clear();
            state.CrossRigidRigidHeaderFullLodProfiles.Clear();
            state.CrossRigidAnalysisDiagnosticCounts.Clear();
            state.CrossRigidAnalysisDiagnosticExamples.Clear();
            state.CrossRigidJointStateCapExclusionsByVmd.Clear();
            state.CrossRigidActivationRewriteOccurrenceSetCounts.Clear();
            state.CrossRigidActivationRewritePlanCounts.Clear();
            state.CrossRigidActivationRewriteExpectedDrawCalls.Clear();
            state.CrossRigidModifierProfileOccurrenceSetCounts.Clear();
            state.CrossRigidModifierProfilePlanCounts.Clear();
            state.CrossRigidModifierProfileExpectedDrawCalls.Clear();
            state.CrossRigidWsModelOccurrenceProbabilities.Clear();
            state.CrossRigidDisplacedSourceGeometryEntries.Clear();
            state.CrossRigidAnalysisVmdCount = 0;
            state.CrossRigidAnalysisDirectWsModelCount = 0;
            state.CrossRigidAnalysisConfigurationCount = 0;
            state.CrossRigidAnalysisOpportunityObservationCount = 0;
            state.CrossRigidPairFallbackSubgroupObservations = 0;
            state.CrossRigidTripleFallbackSubgroupObservations = 0;
            state.CrossRigidQuadrupleFallbackSubgroupObservations = 0;
            state.CrossRigidQuintupleFallbackSubgroupObservations = 0;
            state.CrossRigidMiddleTripleFallbackSubgroupObservations = 0;
            state.CrossRigidMiddleQuadrupleFallbackSubgroupObservations = 0;
            state.CrossRigidPairFallbackPlansCompetingWithSafeFullGroups = 0;
            state.CrossRigidPreviouslySuppressedPairFallbackPlanIds.Clear();
            state.CrossRigidLegacyEligiblePayloadIds.Clear();
            state.CrossRigidPairFallbackPlansEvaluated = 0;
            state.CrossRigidBaselineExpectedDrawsByVmd.Clear();
            state.CrossRigidBaselineUnresolvedWsModelCount = 0;
            state.CrossRigidBaselineRigidsLoaded = 0;
            state.CrossRigidIdealMergeExpectedDrawsByVmd.Clear();
            state.CrossRigidIdealMergeIdentityIds.Clear();
            state.CrossRigidIdealMergePartIdentityCache.Clear();
            state.CrossRigidIdealMergeMaterialIdentityCache.Clear();
            state.CrossRigidIdealMergeConfigurationDrawCache.Clear();
            state.CrossRigidIdealMergeSharedAtlasMaterialPaths.Clear();
            state.CrossRigidIdealMergeSharedAtlasTextureCount = 0;
            state.CrossRigidIdealMergeSharedAtlasSizedTextureCount = 0;
            state.CrossRigidIdealMergeSharedAtlasBcnBytes = 0;
            state.CrossRigidIdealMergeAccounting.Clear();
            state.CrossRigidTimingDetails.Clear();
            state.CrossRigidSourceMeshMergeIdentityCache.Clear();
            state.CrossRigidMaterialMergeIdentityCache.Clear();
            state.CrossRigidSourcePartIdentityCache.Clear();
            state.CrossRigidLodAnalysisCache.Clear();
            var crossRigidAnalysisStage = Stopwatch.StartNew();

            var rigidCache = new Dictionary<string, RmvFile>(
                state.RigidModels,
                StringComparer.OrdinalIgnoreCase);
            var traversalContainers = GetGameplayTraversalContainers(state);
            var visualConfigurationCache =
                new Dictionary<string, CrossRigidAttachmentLocalExpansionResult>(
                    StringComparer.OrdinalIgnoreCase);
            var rewritePlanAccumulators =
                new Dictionary<string, CrossRigidMergePlanAccumulator>(
                    StringComparer.Ordinal);
            var rejectedPlanKeys = new HashSet<string>(StringComparer.Ordinal);
            var fullGroupPlanKeys = new HashSet<string>(StringComparer.Ordinal);
            var fallbackParentPlanKeys =
                new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var activationClassificationVmdDocuments =
                new Dictionary<string, XmlDocument>(
                    StringComparer.OrdinalIgnoreCase);
            var baselineComponents =
                new Dictionary<string, CrossRigidAnalysisComponent?>(
                    StringComparer.OrdinalIgnoreCase);

            for (var vmdIndex = 0; vmdIndex < vmdRoots.Count; vmdIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var vmdPath = Normalize(vmdRoots[vmdIndex]);
                ReportProgress(
                    progress,
                    "Analyzing cross-rigid merges",
                    vmdIndex + 1,
                    vmdRoots.Count,
                    vmdPath);

                // A merge rewrites the root VMD in place: not one another mod uses or overrides.
                if (state.Consumers?.TryBlock(vmdPath, "cross-rigid merge") == true)
                {
                    RecordCrossRigidAnalysisBlocker(
                        state,
                        "The VMD is used or overridden by another enabled mod",
                        vmdPath);
                    continue;
                }

                var expansion = GetCrossRigidAttachmentLocalConfigurationsForVmd(
                    state,
                    vmdPath,
                    visualConfigurationCache,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                if (expansion.FailureReason != null)
                {
                    RecordCrossRigidIdealMergeExpansionFailure(
                        state,
                        expansion.FailureReason);
                    RecordCrossRigidAnalysisBlocker(
                        state,
                        expansion.FailureReason,
                        vmdPath);
                    continue;
                }

                RecordCrossRigidWsModelOccurrenceProbabilities(
                    state,
                    vmdPath,
                    expansion);
                var configurations = expansion.ConfigurationsByAttachment
                    .Values
                    .SelectMany(items => items)
                    .ToArray();
                var allWsModels = configurations
                    .SelectMany(configuration => configuration.Instances)
                    .Select(instance => instance.WsModelPath)
                    .Where(path => path.Length != 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (allWsModels.Length < 2)
                {
                    // Nothing to merge, but the root still counts in the
                    // expected-draw baseline.
                    var baselineTimer = Stopwatch.GetTimestamp();
                    var rigidsBefore = rigidCache.Count;
                    RecordCrossRigidBaselineExpectedDraws(
                        state,
                        vmdPath,
                        expansion,
                        wsModelPath =>
                            ResolveCrossRigidBaselineComponent(
                                state,
                                vmdPath,
                                wsModelPath,
                                traversalContainers,
                                rigidCache,
                                baselineComponents));
                    state.CrossRigidBaselineRigidsLoaded +=
                        rigidCache.Count - rigidsBefore;
                    AddCrossRigidTimingDuration(
                        state,
                        "Analyze: baseline expected draws",
                        Stopwatch.GetElapsedTime(baselineTimer));
                    RecordCrossRigidIdealMergeExpectedDraws(
                        state,
                        vmdPath,
                        expansion,
                        wsModelPath =>
                            ResolveCrossRigidBaselineComponent(
                                state,
                                vmdPath,
                                wsModelPath,
                                traversalContainers,
                                rigidCache,
                                baselineComponents));
                    continue;
                }

                state.CrossRigidAnalysisVmdCount++;
                state.CrossRigidAnalysisDirectWsModelCount += allWsModels.Length;
                state.CrossRigidAnalysisConfigurationCount += configurations.Length;

                var componentsByWsModel =
                    new Dictionary<string, CrossRigidAnalysisComponent>(
                        StringComparer.OrdinalIgnoreCase);
                foreach (var wsModelPath in allWsModels)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryBuildCrossRigidAnalysisComponent(
                            state,
                            vmdPath,
                            wsModelPath,
                            traversalContainers,
                            rigidCache,
                            out var component,
                            out var reason))
                    {
                        RecordCrossRigidAnalysisBlocker(
                            state,
                            reason,
                            $"{vmdPath}: {wsModelPath}");
                        continue;
                    }

                    componentsByWsModel[wsModelPath] = component;
                }

                var baselineAnalysisTimer = Stopwatch.GetTimestamp();
                foreach (var wsModelPath in allWsModels)
                {
                    // Reuse the components built above; a failed build is
                    // already a recorded blocker and is not retried.
                    if (baselineComponents.TryAdd(
                            wsModelPath,
                            componentsByWsModel.GetValueOrDefault(
                                wsModelPath)) &&
                        !componentsByWsModel.ContainsKey(wsModelPath))
                    {
                        state.CrossRigidBaselineUnresolvedWsModelCount++;
                    }
                }

                RecordCrossRigidBaselineExpectedDraws(
                    state,
                    vmdPath,
                    expansion,
                    wsModelPath =>
                        ResolveCrossRigidBaselineComponent(
                            state,
                            vmdPath,
                            wsModelPath,
                            traversalContainers,
                            rigidCache,
                            baselineComponents));
                AddCrossRigidTimingDuration(
                    state,
                    "Analyze: baseline expected draws",
                    Stopwatch.GetElapsedTime(baselineAnalysisTimer));
                RecordCrossRigidIdealMergeExpectedDraws(
                    state,
                    vmdPath,
                    expansion,
                    wsModelPath =>
                        ResolveCrossRigidBaselineComponent(
                            state,
                            vmdPath,
                            wsModelPath,
                            traversalContainers,
                            rigidCache,
                            baselineComponents));

                foreach (var configuration in configurations
                             .Where(configuration =>
                                 configuration.Probability > 0 &&
                                 configuration.Instances.Count >= 2))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attachmentComponents =
                        new List<CrossRigidAnalysisInstanceComponent>();
                    foreach (var instance in configuration.Instances)
                    {
                        if (!componentsByWsModel.TryGetValue(
                                instance.WsModelPath,
                                out var component))
                        {
                            continue;
                        }

                        attachmentComponents.Add(
                            new CrossRigidAnalysisInstanceComponent(
                                instance,
                                component));
                    }

                    if (attachmentComponents
                            .Select(item => item.Component.RigidPath)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count() < 2)
                    {
                        continue;
                    }

                    // A generated WSModel can be built explicitly, but keep non-material
                    // WSModel state strict in the analysis. This prevents the report from
                    // claiming opportunities that would require unrelated WSModel runtime
                    // fields to be reconciled by the writer.
                    var topologyGroups = attachmentComponents
                        .GroupBy(
                            item => item.Component.TopologyIdentity,
                            StringComparer.Ordinal)
                        .Select(group => group
                            .OrderBy(
                                item => item.Component.WsModelPath,
                                StringComparer.OrdinalIgnoreCase)
                            .ToArray())
                        .Where(group => group
                            .Select(item => item.Component.RigidPath)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count() >= 2)
                        .ToArray();

                    if (topologyGroups.Length == 0)
                    {
                        RecordCrossRigidAnalysisBlocker(
                            state,
                            "WSModel non-material topology differs",
                            $"{vmdPath} [{configuration.AttachmentIdentity}]: " +
                            $"{attachmentComponents.Count} co-rendered component instance(s)");
                        continue;
                    }

                    foreach (var (group, isSubgroupFallback, fallbackSize, fullGroup) in
                             EnumerateCrossRigidCandidateGroups(
                                 topologyGroups, state))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (isSubgroupFallback)
                            state.CrossRigidPairFallbackSubgroupObservations++;
                        if (fallbackSize == 3)
                            state.CrossRigidTripleFallbackSubgroupObservations++;
                        if (fallbackSize == 4)
                            state.CrossRigidQuadrupleFallbackSubgroupObservations++;
                        if (fallbackSize == 5)
                            state.CrossRigidQuintupleFallbackSubgroupObservations++;
                        var rewritePlanKey = BuildCrossRigidRewritePlanKey(
                            vmdPath,
                            configuration.AttachmentIdentity,
                            group);
                        if (!isSubgroupFallback)
                        {
                            fullGroupPlanKeys.Add(rewritePlanKey);
                        }
                        else
                        {
                            if (!fallbackParentPlanKeys.TryGetValue(
                                    rewritePlanKey, out var parentKeys))
                            {
                                parentKeys = new HashSet<string>(
                                    StringComparer.Ordinal);
                                fallbackParentPlanKeys.Add(
                                    rewritePlanKey, parentKeys);
                            }

                            parentKeys.Add(BuildCrossRigidRewritePlanKey(
                                vmdPath,
                                configuration.AttachmentIdentity,
                                fullGroup));
                        }

                        if (rewritePlanAccumulators.TryGetValue(
                                rewritePlanKey,
                                out var existingPlan))
                        {
                            existingPlan.AggregatedConfigurationProbability +=
                                configuration.Probability;
                            existingPlan.ContributingConfigurationCount++;
                            existingPlan.RewriteOccurrenceSets.AddRange(
                                BuildCrossRigidRewriteOccurrenceSets(
                                    configuration,
                                    group));
                            state.CrossRigidAnalysisOpportunityObservationCount++;
                            continue;
                        }

                        if (rejectedPlanKeys.Contains(rewritePlanKey))
                            continue;

                        var distinctComponents = group
                            .Select(item => item.Component)
                            .GroupBy(
                                component => component.WsModelPath,
                                StringComparer.OrdinalIgnoreCase)
                            .Select(items => items.First())
                            .ToArray();
                        if (!AreCrossRigidRigidHeadersCompatible(
                                distinctComponents,
                                out var rigidHeaderReason))
                        {
                            rejectedPlanKeys.Add(rewritePlanKey);
                            RecordCrossRigidRigidHeaderBlocker(
                                state,
                                vmdPath,
                                configuration.AttachmentIdentity,
                                distinctComponents,
                                rigidHeaderReason);
                            continue;
                        }

                        var sourceLodCounts =
                            distinctComponents
                                .Select(component =>
                                    component.Rigid.ModelList.Length)
                                .Distinct()
                                .OrderBy(count => count)
                                .ToArray();
                        var sourceVersions =
                            distinctComponents
                                .Select(component =>
                                    component.Rigid.Header.Version)
                                .Distinct()
                                .ToArray();
                        if (sourceVersions.Length > 1)
                        {
                            RecordCrossRigidAnalysisDiagnostic(
                                state,
                                "Cross-rigid source RMV versions normalized",
                                $"{vmdPath} [{configuration.AttachmentIdentity}]: " +
                                $"{string.Join(", ", sourceVersions.OrderBy(version => version))} -> " +
                                $"{sourceVersions.Max()}");
                        }

                        var lodAnalyses =
                            BuildCrossRigidLodAnalyses(
                                state,
                                group,
                                cancellationToken);
                        if (sourceLodCounts.Length > 1)
                        {
                            RecordCrossRigidAnalysisDiagnostic(
                                state,
                                "Cross-rigid source LOD counts reconciled",
                                $"{vmdPath} [{configuration.AttachmentIdentity}]: " +
                                $"sourceCounts=[{string.Join(",", sourceLodCounts)}] -> " +
                                $"generated={lodAnalyses.Count}");
                        }

                        var qualityProfiles = distinctComponents
                            .Select(component =>
                                BuildCrossRigidQualityProfile(component.Rigid))
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(profile => profile, StringComparer.Ordinal)
                            .ToArray();
                        var distanceProfiles = distinctComponents
                            .Select(component =>
                                BuildCrossRigidDistanceProfile(component.Rigid))
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(profile => profile, StringComparer.Ordinal)
                            .ToArray();
                        if (qualityProfiles.Length > 1 ||
                            distanceProfiles.Length > 1)
                        {
                            RecordCrossRigidAnalysisDiagnostic(
                                state,
                                "Cross-rigid source LOD headers reconciled",
                                $"{vmdPath} [{configuration.AttachmentIdentity}]: " +
                                $"sourceQ={string.Join(" <> ", qualityProfiles)}, " +
                                $"sourceD={string.Join(" <> ", distanceProfiles)} -> " +
                                $"Q=[{string.Join(",", lodAnalyses.Select(lod => lod.QualityLevel))}], " +
                                $"D=[{string.Join(",", lodAnalyses.Select(lod => lod.CameraDistance.ToString("0.###", CultureInfo.InvariantCulture)))}]");
                        }

                        if (lodAnalyses.Count == 0 ||
                            lodAnalyses.All(lod => lod.DrawsSaved <= 0))
                        {
                            rejectedPlanKeys.Add(rewritePlanKey);
                            RecordCrossRigidAnalysisBlocker(
                                state,
                                "No actual cross-rigid draw saving",
                                $"{vmdPath} [{configuration.AttachmentIdentity}]: " +
                                $"{string.Join(", ", group.Select(item => item.Component.RigidPath).Distinct(StringComparer.OrdinalIgnoreCase))}");
                            continue;
                        }

                        var sourceGeometry = group
                            .Select(item =>
                                BuildCrossRigidSourceGeometryComponent(item))
                            .ToArray();
                        var sourceInstances = group
                            .Select(item => item.Instance)
                            .OrderBy(
                                instance => instance.WsModelPath,
                                StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        var rewriteOccurrenceSets =
                            BuildCrossRigidRewriteOccurrenceSets(
                                configuration,
                                group);
                        if (rewriteOccurrenceSets.Count == 0)
                        {
                            rejectedPlanKeys.Add(rewritePlanKey);
                            RecordCrossRigidAnalysisBlocker(
                                state,
                                "Cross-rigid rewrite provenance could not be retained",
                                $"{vmdPath} [{configuration.AttachmentIdentity}]");
                            continue;
                        }

                        var generatedPayloadKey =
                            BuildCrossRigidGeneratedPayloadKey(
                                sourceInstances,
                                lodAnalyses);
                        var rewritePlanId = ContentHash(string.Join(
                            "\u001f",
                            rewritePlanKey,
                            generatedPayloadKey));
                        var generatedPayloadId =
                            ContentHash(generatedPayloadKey);

                        rewritePlanAccumulators[rewritePlanKey] =
                            new CrossRigidMergePlanAccumulator(
                                rewritePlanId,
                                generatedPayloadId,
                                generatedPayloadKey,
                                vmdPath,
                                configuration.AttachmentIdentity,
                                sourceInstances,
                                sourceGeometry,
                                group.Select(item => item.Component.RigidPath)
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .OrderBy(
                                        path => path,
                                        StringComparer.OrdinalIgnoreCase)
                                    .ToArray(),
                                lodAnalyses.ToArray(),
                                rewriteOccurrenceSets.ToList(),
                                configuration.Probability,
                                1);
                        state.CrossRigidAnalysisOpportunityObservationCount++;
                    }
                }
            }

            FinalizeCrossRigidIdealMergeTextureCost(state);
            state.CrossRigidTimingDetails["Analyze: enumerate candidates"] =
                crossRigidAnalysisStage.Elapsed;
            crossRigidAnalysisStage.Restart();
            ExpandCrossRigidRewriteCoverageAcrossConfigurations(
                state,
                rewritePlanAccumulators.Values,
                visualConfigurationCache);
            state.CrossRigidTimingDetails["Analyze: expand occurrence coverage"] =
                crossRigidAnalysisStage.Elapsed;
            crossRigidAnalysisStage.Restart();

            // Analyze full groups first so we can identify the fallback
            // alternatives which *would previously* have been suppressed.
            // A writable full-group plan is not necessarily selected by
            // the portfolio or compatible with every other winning rewrite.
            // Keep its pairs as genuine alternatives: conflict-state checks
            // and the portfolio budgets will decide which can be emitted.
            var safeFullGroupPlanKeys = new HashSet<string>(
                StringComparer.Ordinal);
            foreach (var (planKey, plan) in rewritePlanAccumulators
                         .OrderBy(entry =>
                             fullGroupPlanKeys.Contains(entry.Key) ? 0 : 1)
                         .ThenBy(entry => entry.Key, StringComparer.Ordinal))
            {
                if (!fullGroupPlanKeys.Contains(planKey) &&
                    fallbackParentPlanKeys.TryGetValue(
                        planKey, out var fallbackParents))
                {
                    if (fallbackParents.Any(
                            safeFullGroupPlanKeys.Contains))
                    {
                        state.CrossRigidPairFallbackPlansCompetingWithSafeFullGroups++;
                    }

                    state.CrossRigidPairFallbackPlansEvaluated++;
                }

                var allRewriteOccurrenceSets =
                    MergeCrossRigidRewriteOccurrenceSets(
                        plan.RewriteOccurrenceSets);
                var classifiedOccurrenceSets =
                    new List<(
                        CrossRigidRewriteOccurrenceSet OccurrenceSet,
                        CrossRigidActivationRewriteClass RewriteClass,
                        string Detail)>();
                foreach (var occurrenceSet in allRewriteOccurrenceSets)
                {
                    var rewriteClass =
                        ClassifyCrossRigidRewriteOccurrenceSet(
                            state,
                            occurrenceSet,
                            activationClassificationVmdDocuments,
                            out var classificationDetail);
                    classifiedOccurrenceSets.Add(
                        (occurrenceSet, rewriteClass, classificationDetail));
                }

                var modifierProfiledOccurrenceSets =
                    classifiedOccurrenceSets
                        .Select(item =>
                        {
                            var topologyClass =
                                ClassifyCrossRigidRewriteOccurrenceSetTopology(
                                    state,
                                    item.OccurrenceSet,
                                    activationClassificationVmdDocuments,
                                    out _);
                            var modifierProfile =
                                BuildCrossRigidModifierProfile(
                                    item.OccurrenceSet,
                                    plan.SourceGeometry);
                            return (
                                item.OccurrenceSet,
                                TopologyClass: topologyClass,
                                ModifierProfile: modifierProfile);
                        })
                        .ToArray();

                foreach (var modifierGroup in
                         modifierProfiledOccurrenceSets
                             .Where(item =>
                                 !item.ModifierProfile.Equals(
                                     "none",
                                     StringComparison.Ordinal))
                             .GroupBy(item => string.Join(
                                 " | ",
                                 GetCrossRigidActivationRewriteClassLabel(
                                     item.TopologyClass),
                                 item.ModifierProfile),
                                 StringComparer.Ordinal))
                {
                    var profileItems = modifierGroup.ToArray();
                    var profileProbability = Math.Clamp(
                        profileItems.Sum(item =>
                            item.OccurrenceSet.Probability),
                        0.0,
                        1.0);
                    state.CrossRigidModifierProfileOccurrenceSetCounts[
                        modifierGroup.Key] =
                        state.CrossRigidModifierProfileOccurrenceSetCounts
                            .GetValueOrDefault(modifierGroup.Key) +
                        profileItems.Length;
                    state.CrossRigidModifierProfilePlanCounts[
                        modifierGroup.Key] =
                        state.CrossRigidModifierProfilePlanCounts
                            .GetValueOrDefault(modifierGroup.Key) +
                        1;
                    state.CrossRigidModifierProfileExpectedDrawCalls[
                        modifierGroup.Key] =
                        state.CrossRigidModifierProfileExpectedDrawCalls
                            .GetValueOrDefault(modifierGroup.Key) +
                        CalculateExpectedCrossRigidArmyDrawSavings(
                            state,
                            plan.VmdPath,
                            profileProbability,
                            plan.Lods);
                }

                var rewriteableOccurrenceSets =
                    new List<CrossRigidRewriteOccurrenceSet>();
                foreach (var classified in classifiedOccurrenceSets)
                {
                    if (classified.RewriteClass ==
                        CrossRigidActivationRewriteClass.ExactActivationSafe)
                    {
                        rewriteableOccurrenceSets.Add(
                            classified.OccurrenceSet);
                        continue;
                    }

                    if (classified.RewriteClass !=
                            CrossRigidActivationRewriteClass.JointAlwaysPresentSlots &&
                        classified.RewriteClass !=
                            CrossRigidActivationRewriteClass.JointOptionalSlots)
                    {
                        continue;
                    }

                    if (TryDescribeCrossRigidJointAlwaysPresentRewrite(
                            state,
                            classified.OccurrenceSet,
                            activationClassificationVmdDocuments,
                            out _,
                            out var jointReason))
                    {
                        rewriteableOccurrenceSets.Add(
                            classified.OccurrenceSet);
                    }
                    else
                    {
                        RecordCrossRigidAnalysisDiagnostic(
                            state,
                            "Joint activation rewrite state is outside conservative writer",
                            $"{plan.VmdPath} [{plan.AttachmentIdentity}]: " +
                            $"{jointReason}; sources=[" +
                            $"{string.Join(", ", classified.OccurrenceSet.SourceInstances.Select(instance => instance.WsModelPath))}]");
                        const string requiredStatesMarker =
                            "; requiredWeightedStates=";
                        var requiredStatesAt =
                            jointReason.IndexOf(requiredStatesMarker,
                                StringComparison.Ordinal);
                        var categorizedJointReason =
                            requiredStatesAt >= 0
                                ? jointReason[..requiredStatesAt]
                                : jointReason;
                        // Keep per-root, per-plan accounting of the
                        // state-cap failures. Observation counts alone
                        // exaggerate opportunities repeated across visual
                        // alternatives and competing subgroup plans.
                        if (jointReason.Contains(
                                "Cartesian product exceeds",
                                StringComparison.Ordinal))
                        {
                            var rootPath = Normalize(plan.VmdPath);
                            if (!state.CrossRigidJointStateCapExclusionsByVmd
                                    .TryGetValue(rootPath, out var excluded))
                            {
                                excluded =
                                    new CrossRigidJointStateCapExclusionAggregate();
                                state.CrossRigidJointStateCapExclusionsByVmd.Add(
                                    rootPath, excluded);
                            }

                            excluded.OccurrenceSetCount++;
                            excluded.RewritePlanIds.Add(plan.RewritePlanId);
                            if (requiredStatesAt >= 0 &&
                                long.TryParse(
                                    jointReason[(requiredStatesAt +
                                        requiredStatesMarker.Length)..],
                                    NumberStyles.Integer,
                                    CultureInfo.InvariantCulture,
                                    out var requiredWeightedStates))
                            {
                                excluded.MaxRequiredWeightedStates =
                                    Math.Max(
                                        excluded.MaxRequiredWeightedStates,
                                        requiredWeightedStates);
                                if (requiredWeightedStates <= 8192)
                                    excluded.RequiredUpTo8192++;
                                else if (requiredWeightedStates <= 16384)
                                    excluded.RequiredUpTo16384++;
                                else if (requiredWeightedStates <= 65536)
                                    excluded.RequiredUpTo65536++;
                                else
                                    excluded.RequiredAbove65536++;
                            }
                            else
                            {
                                excluded.UnknownRequiredStates++;
                            }
                            if (classified.OccurrenceSet.SourceInstances.Length == 2)
                                excluded.TwoSourceOccurrenceSetCount++;
                            else
                                excluded.OtherSourceOccurrenceSetCount++;
                            if (jointReason.StartsWith(
                                    "common-ancestor Cartesian product",
                                    StringComparison.Ordinal))
                            {
                                excluded.CommonAncestorOccurrenceSetCount++;
                            }
                            else if (jointReason.StartsWith(
                                         "joint Cartesian product",
                                         StringComparison.Ordinal))
                            {
                                excluded.DirectJointOccurrenceSetCount++;
                            }
                            else
                            {
                                excluded.OtherCapOccurrenceSetCount++;
                            }

                            // Deliberately overlap-inclusive: different
                            // candidate merges can compete for the same
                            // occurrence and must not be summed as
                            // independently realizable savings.
                            excluded.OverlapInclusiveExpectedDraws +=
                                CalculateExpectedCrossRigidArmyDrawSavings(
                                    state,
                                    plan.VmdPath,
                                    classified.OccurrenceSet.Probability,
                                    plan.Lods);
                        }

                        // Aggregate the actual reason separately rather
                        // than showing only the first few examples from a
                        // large, undifferentiated rejection category.
                        RecordCrossRigidAnalysisDiagnostic(
                            state,
                            $"Joint writer exclusion: {categorizedJointReason}",
                            $"{plan.VmdPath} [{plan.AttachmentIdentity}]" +
                            (requiredStatesAt >= 0
                                ? jointReason[requiredStatesAt..]
                                : string.Empty));
                    }
                }

                var rewriteableOccurrenceSetArray =
                    rewriteableOccurrenceSets.ToArray();
                var aggregatedProbability = Math.Clamp(
                    rewriteableOccurrenceSetArray.Sum(set =>
                        set.Probability),
                    0.0,
                    1.0);

                foreach (var classificationGroup in
                         classifiedOccurrenceSets.GroupBy(item =>
                             item.RewriteClass))
                {
                    var rewriteClass = classificationGroup.Key;
                    var classItems = classificationGroup.ToArray();
                    var classProbability = Math.Clamp(
                        classItems.Sum(item =>
                            item.OccurrenceSet.Probability),
                        0.0,
                        1.0);
                    state.CrossRigidActivationRewriteOccurrenceSetCounts[
                        rewriteClass] =
                        state.CrossRigidActivationRewriteOccurrenceSetCounts
                            .GetValueOrDefault(rewriteClass) +
                        classItems.Length;
                    state.CrossRigidActivationRewritePlanCounts[
                        rewriteClass] =
                        state.CrossRigidActivationRewritePlanCounts
                            .GetValueOrDefault(rewriteClass) +
                        1;
                    state.CrossRigidActivationRewriteExpectedDrawCalls[
                        rewriteClass] =
                        state.CrossRigidActivationRewriteExpectedDrawCalls
                            .GetValueOrDefault(rewriteClass) +
                        CalculateExpectedCrossRigidArmyDrawSavings(
                            state,
                            plan.VmdPath,
                            classProbability,
                            plan.Lods);

                    if (rewriteClass !=
                        CrossRigidActivationRewriteClass.ExactActivationSafe)
                    {
                        var detail = classItems
                            .Select(item => item.Detail)
                            .FirstOrDefault(value =>
                                !string.IsNullOrWhiteSpace(value));
                        RecordCrossRigidAnalysisDiagnostic(
                            state,
                            $"Activation rewrite class: " +
                            $"{GetCrossRigidActivationRewriteClassLabel(rewriteClass)}",
                            $"{plan.VmdPath} [{plan.AttachmentIdentity}]: " +
                            $"{classItems.Length}/{allRewriteOccurrenceSets.Length} state(s), " +
                            $"probability={classProbability:0.######}" +
                            (string.IsNullOrWhiteSpace(detail)
                                ? string.Empty
                                : $"; {detail}"));
                    }
                }

                var excludedOccurrenceSetCount =
                    allRewriteOccurrenceSets.Length -
                    rewriteableOccurrenceSetArray.Length;
                if (excludedOccurrenceSetCount != 0)
                {
                    RecordCrossRigidAnalysisDiagnostic(
                        state,
                        "Activation-unsafe rewrite states excluded before value gate",
                        $"{plan.VmdPath} [{plan.AttachmentIdentity}]: " +
                        $"{excludedOccurrenceSetCount}/{allRewriteOccurrenceSets.Length} state(s)");
                }

                if (aggregatedProbability <=
                    AtlasValueGateExpectedDrawEpsilon)
                {
                    RecordCrossRigidAnalysisBlocker(
                        state,
                        "No copy-on-write-safe activation state",
                        $"{plan.VmdPath} [{plan.AttachmentIdentity}]");
                    continue;
                }

                if (fullGroupPlanKeys.Contains(planKey))
                {
                    safeFullGroupPlanKeys.Add(planKey);
                }

                // Track provenance at plan granularity: generated geometry
                // can be shared by both existing full-group plans and newly
                // admitted fallback pairs across different VMDs.
                var newlyAdmittedPairFallback =
                    !fullGroupPlanKeys.Contains(planKey) &&
                    fallbackParentPlanKeys.TryGetValue(
                        planKey, out var parentPlanKeys) &&
                    parentPlanKeys.Any(safeFullGroupPlanKeys.Contains);
                if (newlyAdmittedPairFallback)
                {
                    state.CrossRigidPreviouslySuppressedPairFallbackPlanIds.Add(
                        plan.RewritePlanId);
                }
                else
                {
                    state.CrossRigidLegacyEligiblePayloadIds.Add(
                        plan.GeneratedPayloadId);
                }

                // The merge copies the geometry of every rigid and the material table of every WSModel
                // it combines into the generated files. A rigid or WSModel that is a vanilla file
                // another mod replaces would be baked in as its vanilla version, so such a plan is
                // refused. Materials, textures and the other files it only names by path are not
                // copied (material parts that merge by content are handled in the merge identity).
                if (state.Consumers != null)
                {
                    var bakedReplaced = false;
                    foreach (var path in plan.SourceInstances.Select(instance => Normalize(instance.WsModelPath))
                                 .Concat(plan.SourceGeometry.Select(source => Normalize(source.RigidPath)))
                                 .Append(Normalize(plan.VmdPath))
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (state.Consumers.TryBlockCrossRigidCopy(path))
                            bakedReplaced = true;
                    }

                    if (bakedReplaced)
                    {
                        RecordCrossRigidAnalysisBlocker(
                            state,
                            "The plan would copy a rigid or WSModel that is a vanilla file another enabled mod replaces",
                            $"{plan.VmdPath} [{plan.AttachmentIdentity}]");
                        continue;
                    }
                }

                state.CrossRigidMergeAnalysisEntries.Add(
                    new CrossRigidMergeAnalysisEntry(
                        plan.RewritePlanId,
                        plan.GeneratedPayloadId,
                        plan.VmdPath,
                        aggregatedProbability,
                        rewriteableOccurrenceSetArray.Length,
                        plan.AttachmentIdentity,
                        plan.SourceInstances,
                        rewriteableOccurrenceSetArray,
                        plan.SourceGeometry,
                        plan.RigidPaths,
                        plan.Lods,
                        CalculateExpectedCrossRigidArmyDrawSavings(
                            state,
                            plan.VmdPath,
                            aggregatedProbability,
                            plan.Lods)));
            }

            state.CrossRigidTimingDetails["Analyze: classify rewrite states"] =
                crossRigidAnalysisStage.Elapsed;
            crossRigidAnalysisStage.Restart();
            BuildCrossRigidGeneratedPayloadAnalysis(state);
            state.CrossRigidTimingDetails["Analyze: build payload estimates"] =
                crossRigidAnalysisStage.Elapsed;
            crossRigidAnalysisStage.Restart();
            SelectCrossRigidGeneratedPayloads(state);
            state.CrossRigidTimingDetails["Analyze: select payload portfolio"] =
                crossRigidAnalysisStage.Elapsed;
        }

        private static void
            ExpandCrossRigidRewriteCoverageAcrossConfigurations(
                BatchState state,
                IEnumerable<CrossRigidMergePlanAccumulator> plans,
                IReadOnlyDictionary<
                    string,
                    CrossRigidAttachmentLocalExpansionResult>
                    visualConfigurationCache)
        {
            foreach (var plan in plans)
            {
                if (!visualConfigurationCache.TryGetValue(
                        Normalize(plan.VmdPath),
                        out var expansion) ||
                    expansion.FailureReason != null ||
                    !expansion.ConfigurationsByAttachment.TryGetValue(
                        plan.AttachmentIdentity,
                        out var configurations))
                {
                    continue;
                }

                var beforeProbability =
                    Math.Clamp(
                        MergeCrossRigidRewriteOccurrenceSets(
                                plan.RewriteOccurrenceSets)
                            .Sum(set => set.Probability),
                        0.0,
                        1.0);
                var expectedCounts =
                    BuildCrossRigidWsModelMultiplicity(
                        plan.SourceInstances);
                var projectedSets =
                    configurations
                        .Where(configuration =>
                            configuration.Probability > 0)
                        .SelectMany(configuration =>
                            BuildCrossRigidRewriteOccurrenceSets(
                                configuration,
                                expectedCounts))
                        .ToArray();
                if (projectedSets.Length == 0)
                    continue;

                var merged =
                    MergeCrossRigidRewriteOccurrenceSets(
                        projectedSets);
                plan.RewriteOccurrenceSets.Clear();
                plan.RewriteOccurrenceSets.AddRange(merged);

                var afterProbability =
                    Math.Clamp(
                        merged.Sum(set => set.Probability),
                        0.0,
                        1.0);
                if (afterProbability >
                    beforeProbability +
                    AtlasValueGateExpectedDrawEpsilon)
                {
                    RecordCrossRigidAnalysisDiagnostic(
                        state,
                        "Cross-rigid merge coverage expanded across co-render states",
                        $"{plan.VmdPath} " +
                        $"[{plan.AttachmentIdentity}]: " +
                        $"{plan.SourceInstances.Length} source(s), " +
                        $"probability {beforeProbability:0.######} -> " +
                        $"{afterProbability:0.######}");
                }
            }
        }

        private static Dictionary<string, int>
            BuildCrossRigidWsModelMultiplicity(
                IEnumerable<CrossRigidVisualInstance> instances)
            => instances
                .Select(instance =>
                    Normalize(instance.WsModelPath))
                .GroupBy(
                    path => path,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Count(),
                    StringComparer.OrdinalIgnoreCase);

        private static IEnumerable<(
                CrossRigidAnalysisInstanceComponent[] Group,
                bool IsSubgroupFallback,
                int FallbackSize,
                CrossRigidAnalysisInstanceComponent[] FullGroup)>
            EnumerateCrossRigidCandidateGroups(
                CrossRigidAnalysisInstanceComponent[][] topologyGroups,
                BatchState state)
        {
            foreach (var group in topologyGroups)
            {
                // Keep the original complete merge candidate. Pairwise
                // alternatives are strictly additional choices for the
                // existing provenance validator and value/conflict gates.
                yield return (group, false, 0, group);
                if (group.Length < 3)
                    continue;

                var uniquePairs = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                var emitted = 0;
                for (var firstIndex = 0;
                     firstIndex < group.Length &&
                     emitted < MaxCrossRigidPairFallbacksPerGroup;
                     firstIndex++)
                {
                    for (var secondIndex = firstIndex + 1;
                         secondIndex < group.Length &&
                         emitted < MaxCrossRigidPairFallbacksPerGroup;
                         secondIndex++)
                    {
                        var first = group[firstIndex];
                        var second = group[secondIndex];
                        if (first.Component.RigidPath.Equals(
                                second.Component.RigidPath,
                                StringComparison.OrdinalIgnoreCase) ||
                            first.Instance.WsModelPath.Equals(
                                second.Instance.WsModelPath,
                                StringComparison.OrdinalIgnoreCase))
                            continue;

                        var paths = new[]
                        {
                            Normalize(first.Instance.WsModelPath),
                            Normalize(second.Instance.WsModelPath)
                        };
                        Array.Sort(
                            paths,
                            StringComparer.OrdinalIgnoreCase);
                        var pairKey = string.Join("\u001f", paths);
                        if (!uniquePairs.Add(pairKey))
                            continue;

                        emitted++;
                        yield return (
                            new[] { first, second },
                            true,
                            2,
                            group);
                    }
                }

                // The complete three-component case has already been
                // yielded above. For larger groups, fall back to a bounded
                // three-component merge, including combinations of different
                // WSModels which share a rigid path. Two distinct rigids are
                // still required to provide an actual cross-rigid merge.
                if (group.Length >= 4)
                {
                    var uniqueTriples = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
                    var emittedTriples = 0;
                    // Preserve the previously considered sixteen distinct-
                    // rigid triples exactly. Then examine eight additional
                    // combinations from the opposite end, including separate
                    // WSModels that reuse one of the selected rigid paths.
                    foreach (var scanForward in new[] { true, false })
                    {
                        var candidates = scanForward
                            ? group
                            : group.Reverse().ToArray();
                        // Preserve the original 16 forward and 8 reverse
                        // candidates. The expanded total reserves eight
                        // genuinely new choices for middle-out sampling.
                        var stageLimit = scanForward
                            ? 16
                            : MaxCrossRigidTripleFallbacksPerGroup - 8;
                        for (var firstIndex = 0;
                             firstIndex < candidates.Length &&
                             emittedTriples < stageLimit;
                             firstIndex++)
                        {
                            for (var secondIndex = firstIndex + 1;
                                 secondIndex < candidates.Length &&
                                 emittedTriples < stageLimit;
                                 secondIndex++)
                            {
                                for (var thirdIndex = secondIndex + 1;
                                     thirdIndex < candidates.Length &&
                                     emittedTriples < stageLimit;
                                     thirdIndex++)
                                {
                                    var items = new[]
                                    {
                                        candidates[firstIndex],
                                        candidates[secondIndex],
                                        candidates[thirdIndex],
                                    };
                                    if (items.Select(item =>
                                            item.Component.RigidPath)
                                        .Distinct(
                                            StringComparer.OrdinalIgnoreCase)
                                        .Count() < (scanForward ? 3 : 2))
                                        continue;

                                    var paths = items.Select(item =>
                                            Normalize(item.Instance.WsModelPath))
                                        .ToArray();
                                    if (paths.Distinct(
                                            StringComparer.OrdinalIgnoreCase)
                                        .Count() != 3)
                                        continue;

                                    Array.Sort(
                                        paths,
                                        StringComparer.OrdinalIgnoreCase);
                                    if (!uniqueTriples.Add(
                                            string.Join("\u001f", paths)))
                                        continue;

                                    emittedTriples++;
                                    yield return (
                                        items,
                                        true,
                                        3,
                                        group);
                                }
                            }
                        }
                    }
                    // The original first sixteen forward and eight reverse
                    // candidates remain unchanged. Examine a small third
                    // cohort around the middle, where large appearance groups
                    // otherwise never reach the fallback enumerator.
                    foreach (var items in
                             EnumerateCrossRigidDistinctFallbackSubsets(
                                     BuildCrossRigidMiddleOutFallbackOrder(group),
                                     3,
                                     uniqueTriples)
                                 .Take(MaxCrossRigidTripleFallbacksPerGroup -
                                       emittedTriples))
                    {
                        emittedTriples++;
                        state.CrossRigidMiddleTripleFallbackSubgroupObservations++;
                        yield return (items, true, 3, group);
                    }
                }

                // A full group with exactly four components is already
                // represented by its original candidate. Only groups with
                // at least five members can have a true four-way fallback.
                // All WSModel paths must be different, but source rigid paths
                // may repeat because distinct WSModels are distinct render
                // occurrences. Require two different rigids and pass every
                // candidate through the existing provenance/value gates.
                if (group.Length >= 5 &&
                    group.Select(item => item.Component.RigidPath)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count() >= 2)
                {
                    var uniqueQuadruples = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
                    var emittedQuadruples = 0;
                    // Preserve the original eight distinct-rigid candidates
                    // before adding up to eight more from the opposite end,
                    // including WSModels backed by repeated rigid sources.
                    foreach (var scanForward in new[] { true, false })
                    {
                        var candidates = scanForward
                            ? group
                            : group.Reverse().ToArray();
                        // Preserve the original 8 forward and 8 reverse
                        // candidates; reserve the last 8 for middle-out
                        // groups rather than letting reverse exhaust them.
                        var stageLimit = scanForward
                            ? 8
                            : MaxCrossRigidQuadrupleFallbacksPerGroup - 8;
                        for (var firstIndex = 0;
                             firstIndex < candidates.Length &&
                             emittedQuadruples < stageLimit;
                             firstIndex++)
                        {
                            for (var secondIndex = firstIndex + 1;
                                 secondIndex < candidates.Length &&
                                 emittedQuadruples < stageLimit;
                                 secondIndex++)
                            {
                                for (var thirdIndex = secondIndex + 1;
                                     thirdIndex < candidates.Length &&
                                     emittedQuadruples < stageLimit;
                                     thirdIndex++)
                                {
                                    for (var fourthIndex = thirdIndex + 1;
                                         fourthIndex < candidates.Length &&
                                         emittedQuadruples < stageLimit;
                                         fourthIndex++)
                                    {
                                        var items = new[]
                                        {
                                            candidates[firstIndex],
                                            candidates[secondIndex],
                                            candidates[thirdIndex],
                                            candidates[fourthIndex],
                                        };
                                        if (items.Select(item =>
                                                item.Component.RigidPath)
                                            .Distinct(
                                                StringComparer.OrdinalIgnoreCase)
                                            .Count() < (scanForward ? 4 : 2))
                                            continue;

                                        var paths = items.Select(item =>
                                                Normalize(item.Instance.WsModelPath))
                                            .ToArray();
                                        if (paths.Distinct(
                                                StringComparer.OrdinalIgnoreCase)
                                            .Count() != 4)
                                            continue;

                                        Array.Sort(
                                            paths,
                                            StringComparer.OrdinalIgnoreCase);
                                        if (!uniqueQuadruples.Add(
                                                string.Join("\u001f", paths)))
                                            continue;

                                        emittedQuadruples++;
                                        yield return (
                                            items,
                                            true,
                                            4,
                                            group);
                                    }
                                }
                            }
                        }
                    }
                    // Preserve the original eight forward and eight reverse
                    // quadruples, then look at the middle of long groups.
                    foreach (var items in
                             EnumerateCrossRigidDistinctFallbackSubsets(
                                     BuildCrossRigidMiddleOutFallbackOrder(group),
                                     4,
                                     uniqueQuadruples)
                                 .Take(MaxCrossRigidQuadrupleFallbacksPerGroup -
                                       emittedQuadruples))
                    {
                        emittedQuadruples++;
                        state.CrossRigidMiddleQuadrupleFallbackSubgroupObservations++;
                        yield return (items, true, 4, group);
                    }
                }

                // When a six-or-larger full group exceeds the rewrite state
                // budget, a bounded five-way subset may recover four draws.
                // Only genuinely distinct WSModel occurrences are combined;
                // repeated rigid paths are fine if at least two remain.
                if (group.Length >= 6 &&
                    group.Select(item => item.Component.RigidPath)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2 &&
                    group.Select(item => Normalize(item.Instance.WsModelPath))
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 5)
                {
                    var seen = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
                    var emittedQuintuples = 0;
                    var orders = new[]
                    {
                        group,
                        group.Reverse().ToArray(),
                        BuildCrossRigidMiddleOutFallbackOrder(group)
                    };
                    for (var pass = 0; pass < orders.Length; pass++)
                    {
                        var stageLimit =
                            (pass + 1) * MaxCrossRigidQuintupleFallbacksPerGroup /
                            orders.Length;
                        foreach (var items in
                                 EnumerateCrossRigidDistinctFallbackSubsets(
                                         orders[pass], 5, seen)
                                     .Take(stageLimit - emittedQuintuples))
                        {
                            emittedQuintuples++;
                            yield return (items, true, 5, group);
                        }
                    }
                }
            }
        }

        // A third sampling order prevents lexicographic prefix/suffix caps
        // from excluding all components near the middle of long groups.
        private static CrossRigidAnalysisInstanceComponent[]
            BuildCrossRigidMiddleOutFallbackOrder(
                CrossRigidAnalysisInstanceComponent[] group)
            => group.Select((item, index) => (Item: item, Index: index))
                .OrderBy(item => Math.Abs(
                    2 * item.Index - (group.Length - 1)))
                .ThenBy(item => item.Index)
                .Select(item => item.Item)
                .ToArray();

        // All new subsets remain subject to the same topology, provenance,
        // state-budget, generated-geometry and value gates as full groups.
        // Limit the search effort as well as the number of emitted subsets.
        private static IEnumerable<CrossRigidAnalysisInstanceComponent[]>
            EnumerateCrossRigidDistinctFallbackSubsets(
                CrossRigidAnalysisInstanceComponent[] candidates,
                int size,
                HashSet<string> seen)
        {
            const int maxInspectedCombinations = 512;
            var selected = new CrossRigidAnalysisInstanceComponent[size];
            var inspected = 0;
            return Enumerate(0, 0);

            IEnumerable<CrossRigidAnalysisInstanceComponent[]> Enumerate(
                int nextIndex, int depth)
            {
                if (inspected >= maxInspectedCombinations)
                    yield break;
                if (depth == size)
                {
                    inspected++;
                    if (selected.Select(item => item.Component.RigidPath)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count() < 2)
                        yield break;

                    var paths = selected.Select(item =>
                            Normalize(item.Instance.WsModelPath))
                        .ToArray();
                    if (paths.Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count() != size)
                        yield break;

                    Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
                    if (seen.Add(string.Join("\u001f", paths)))
                        yield return selected.ToArray();
                    yield break;
                }

                for (var index = nextIndex;
                     index <= candidates.Length - (size - depth) &&
                     inspected < maxInspectedCombinations;
                     index++)
                {
                    selected[depth] = candidates[index];
                    foreach (var subset in Enumerate(index + 1, depth + 1))
                        yield return subset;
                }
            }
        }

        private static string BuildCrossRigidRewritePlanKey(
            string vmdPath,
            string attachmentIdentity,
            IReadOnlyList<CrossRigidAnalysisInstanceComponent> group)
        {
            // Same content as Join(US, Normalize(vmd), identity,
            // Join(RS, ws paths stably ordered OrdinalIgnoreCase)).
            var paths = new string[group.Count];
            for (var i = 0; i < paths.Length; i++)
            {
                var path = group[i].Instance.WsModelPath;
                var j = i;
                // Stable insertion sort.
                while (j > 0 &&
                       StringComparer.OrdinalIgnoreCase.Compare(
                           paths[j - 1], path) > 0)
                {
                    paths[j] = paths[j - 1];
                    j--;
                }

                paths[j] = path;
            }

            return string.Concat(
                Normalize(vmdPath),
                "\u001f",
                attachmentIdentity,
                "\u001f",
                string.Join("\u001e", paths));
        }

        private enum CrossRigidActivationRewriteClass
        {
            ExactActivationSafe,
            JointAlwaysPresentSlots,
            JointOptionalSlots,
            LocalModelModifiers,
            Unclassifiable,
        }

        private static bool
            IsCrossRigidRewriteOccurrenceSetActivationSafe(
                CrossRigidRewriteOccurrenceSet occurrenceSet)
            => occurrenceSet.SourceInstances.Length >= 2 &&
               occurrenceSet.SourceInstances
                   .Select(instance => instance.ActivationSignature)
                   .Distinct(StringComparer.Ordinal)
                   .Count() == 1 &&
               occurrenceSet.SourceInstances.All(instance =>
                   !instance.HasRewriteBlockingModifiers);

        private static CrossRigidActivationRewriteClass
            ClassifyCrossRigidRewriteOccurrenceSet(
                BatchState state,
                CrossRigidRewriteOccurrenceSet occurrenceSet,
                Dictionary<string, XmlDocument> sourceVmdDocuments,
                out string detail)
        {
            if (occurrenceSet.SourceInstances.Any(instance =>
                    instance.HasRewriteBlockingModifiers))
            {
                detail =
                    BuildCrossRigidModifierProfile(
                        occurrenceSet,
                        []);
                return CrossRigidActivationRewriteClass.LocalModelModifiers;
            }

            return ClassifyCrossRigidRewriteOccurrenceSetTopology(
                state,
                occurrenceSet,
                sourceVmdDocuments,
                out detail);
        }

        private static CrossRigidActivationRewriteClass
            ClassifyCrossRigidRewriteOccurrenceSetTopology(
                BatchState state,
                CrossRigidRewriteOccurrenceSet occurrenceSet,
                Dictionary<string, XmlDocument> sourceVmdDocuments,
                out string detail)
        {
            detail = string.Empty;
            if (occurrenceSet.SourceInstances.Length < 2)
            {
                detail = "fewer than two source model occurrences";
                return CrossRigidActivationRewriteClass.Unclassifiable;
            }

            var activationSignatures =
                occurrenceSet.SourceInstances
                    .Select(instance => instance.ActivationSignature)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            if (activationSignatures.Length == 1)
            {
                return CrossRigidActivationRewriteClass.ExactActivationSafe;
            }

            var tokenSets = occurrenceSet.SourceInstances
                .Select(instance =>
                    instance.ActivationSignature.Equals(
                        "<always>",
                        StringComparison.Ordinal)
                        ? new HashSet<string>(StringComparer.Ordinal)
                        : instance.ActivationSignature
                            .Split(
                                '\u001f',
                                StringSplitOptions.RemoveEmptyEntries)
                            .ToHashSet(StringComparer.Ordinal))
                .ToArray();
            if (tokenSets.Length == 0)
            {
                detail = "activation signature set is empty";
                return CrossRigidActivationRewriteClass.Unclassifiable;
            }

            var commonTokens = new HashSet<string>(
                tokenSets[0],
                StringComparer.Ordinal);
            foreach (var tokenSet in tokenSets.Skip(1))
                commonTokens.IntersectWith(tokenSet);

            var differingTokens = tokenSets
                .SelectMany(tokens => tokens)
                .Where(token => !commonTokens.Contains(token))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (differingTokens.Length == 0)
            {
                detail =
                    "activation signatures differ only by token ordering";
                return CrossRigidActivationRewriteClass.Unclassifiable;
            }

            var hasOptionalSlot = false;
            foreach (var token in differingTokens)
            {
                if (!TryClassifyCrossRigidActivationToken(
                        state,
                        token,
                        sourceVmdDocuments,
                        out var optionalSlot,
                        out var tokenReason))
                {
                    detail = tokenReason;
                    return CrossRigidActivationRewriteClass.Unclassifiable;
                }

                hasOptionalSlot |= optionalSlot;
            }

            detail =
                $"{differingTokens.Length} differing activation token(s)";
            return hasOptionalSlot
                ? CrossRigidActivationRewriteClass.JointOptionalSlots
                : CrossRigidActivationRewriteClass.JointAlwaysPresentSlots;
        }

        private static string BuildCrossRigidModifierProfile(
            CrossRigidRewriteOccurrenceSet occurrenceSet,
            IReadOnlyList<CrossRigidSourceGeometryComponent>
                sourceGeometry)
        {
            var rigidByWsModel = sourceGeometry
                .GroupBy(
                    source => source.WsModelPath,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().RigidPath,
                    StringComparer.OrdinalIgnoreCase);

            var imposterInstances = occurrenceSet.SourceInstances
                .Where(instance =>
                    !string.IsNullOrWhiteSpace(
                        instance.ImposterModelPath))
                .ToArray();
            var hasMirroredImposter = false;
            var hasIndependentImposter = false;
            foreach (var instance in imposterInstances)
            {
                if (rigidByWsModel.TryGetValue(
                        instance.WsModelPath,
                        out var rigidPath) &&
                    Normalize(instance.ImposterModelPath).Equals(
                        Normalize(rigidPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    hasMirroredImposter = true;
                }
                else
                {
                    hasIndependentImposter = true;
                }
            }

            var parts = new List<string>();
            if (hasMirroredImposter)
                parts.Add("imposter mirrors source rigid");
            if (hasIndependentImposter)
                parts.Add("independent imposter geometry");
            if (occurrenceSet.SourceInstances.Any(instance =>
                    instance.HasDecalDiffuse ||
                    instance.HasDecalNormal))
            {
                var hasDiffuse = occurrenceSet.SourceInstances.Any(
                    instance => instance.HasDecalDiffuse);
                var hasNormal = occurrenceSet.SourceInstances.Any(
                    instance => instance.HasDecalNormal);
                parts.Add(
                    hasDiffuse && hasNormal
                        ? "diffuse+normal decal"
                        : hasDiffuse
                            ? "diffuse decal"
                            : "normal decal");
            }

            if (occurrenceSet.SourceInstances.Any(instance =>
                    instance.HasMetadata))
            {
                parts.Add("metadata");
            }

            // When source geometry is unavailable (for the broad diagnostic
            // emitted before a plan is materialized), retain imposter presence
            // instead of incorrectly classifying it as independent geometry.
            if (imposterInstances.Length != 0 &&
                sourceGeometry.Count == 0)
            {
                parts.Remove("independent imposter geometry");
                parts.Remove("imposter mirrors source rigid");
                parts.Insert(0, "imposter_model");
            }

            return parts.Count == 0
                ? "none"
                : string.Join(" + ", parts);
        }

        private static bool TryDescribeCrossRigidJointAlwaysPresentRewrite(
            BatchState state,
            CrossRigidRewriteOccurrenceSet occurrenceSet,
            Dictionary<string, XmlDocument> sourceVmdDocuments,
            out CrossRigidJointAlwaysPresentRewriteDescriptor descriptor,
            out string reason)
        {
            descriptor = null!;
            reason = string.Empty;
            if (occurrenceSet.SourceInstances.Length < 2)
            {
                reason = "fewer than two source model occurrences";
                return false;
            }

            if (occurrenceSet.SourceInstances.Any(instance =>
                    instance.HasRewriteBlockingModifiers))
            {
                reason =
                    "source occurrence has imposter or decal state";
                return false;
            }

            var tokenSets = occurrenceSet.SourceInstances
                .Select(instance =>
                    instance.ActivationSignature.Equals(
                        "<always>",
                        StringComparison.Ordinal)
                        ? new HashSet<string>(StringComparer.Ordinal)
                        : instance.ActivationSignature
                            .Split(
                                '\u001f',
                                StringSplitOptions.RemoveEmptyEntries)
                            .ToHashSet(StringComparer.Ordinal))
                .ToArray();
            if (tokenSets.Length == 0)
            {
                reason = "activation signature set is empty";
                return false;
            }

            var commonTokens = new HashSet<string>(
                tokenSets[0],
                StringComparer.Ordinal);
            foreach (var tokenSet in tokenSets.Skip(1))
                commonTokens.IntersectWith(tokenSet);

            var candidates =
                new List<CrossRigidJointSourceSelectionCandidate>();
            var candidateFailureReasons =
                new List<string>();
            var requiredOptionalDimensionKeys =
                new HashSet<string>(
                    StringComparer.Ordinal);
            foreach (var differingToken in
                     tokenSets
                         .SelectMany(tokens => tokens)
                         .Where(token =>
                             !commonTokens.Contains(token))
                         .Distinct(StringComparer.Ordinal))
            {
                if (!TryParseCrossRigidActivationToken(
                        state,
                        differingToken,
                        sourceVmdDocuments,
                        out var differingSelection,
                        out var differingReason))
                {
                    candidateFailureReasons.Add(
                        differingReason);
                    continue;
                }

                if (!differingSelection.OptionalSlot)
                    continue;

                // Every differing optional slot must remain explicit even
                // when its probability/topology is not yet supported. That
                // prevents a deeper terminal selection from bypassing an
                // unsupported optional ancestor.
                requiredOptionalDimensionKeys.Add(
                    BuildCrossRigidJointSelectionDimensionKey(
                        differingSelection.OwnerVmdPath,
                        differingSelection.ParentXmlPath,
                        differingSelection.SlotIndex));

                if (!TryValidateCrossRigidOptionalJointSelection(
                        state,
                        differingSelection,
                        sourceVmdDocuments,
                        out var optionalReason))
                {
                    candidateFailureReasons.Add(
                        optionalReason);
                    continue;
                }
            }

            for (var instanceIndex = 0;
                 instanceIndex < occurrenceSet.SourceInstances.Length;
                 instanceIndex++)
            {
                var instance =
                    occurrenceSet.SourceInstances[instanceIndex];
                var orderedDifferingTokens =
                    instance.ActivationSignature.Equals(
                        "<always>",
                        StringComparison.Ordinal)
                        ? Array.Empty<string>()
                        : instance.ActivationSignature
                            .Split(
                                '\u001f',
                                StringSplitOptions.RemoveEmptyEntries)
                            .Where(token =>
                                !commonTokens.Contains(token))
                            .ToArray();
                if (orderedDifferingTokens.Length == 0)
                    continue;

                // Only the terminal varying selection fully determines this
                // source occurrence. Anchoring at an outer token while a
                // deeper alternative still varies would remove geometry from
                // sibling descendant states that are not part of this merge.
                var token =
                    orderedDifferingTokens[^1];
                if (!TryParseCrossRigidActivationToken(
                        state,
                        token,
                        sourceVmdDocuments,
                        out var tokenSelection,
                        out var tokenReason))
                {
                    candidateFailureReasons.Add(tokenReason);
                    continue;
                }

                if (tokenSelection.OptionalSlot &&
                    !TryValidateCrossRigidOptionalJointSelection(
                        state,
                        tokenSelection,
                        sourceVmdDocuments,
                        out var optionalSelectionReason))
                {
                    candidateFailureReasons.Add(
                        optionalSelectionReason);
                    continue;
                }

                if (!TryGetCrossRigidReferenceChainPrefixToVmd(
                        instance,
                        tokenSelection.OwnerVmdPath,
                        out var anchorReferenceChain))
                {
                    candidateFailureReasons.Add(
                        "selection owner is not on the source model VMD provenance chain");
                    continue;
                }

                if (!TryBuildCrossRigidJointSourceSelection(
                        instance,
                        tokenSelection,
                        anchorReferenceChain,
                        out var sourceSelection,
                        out var selectionReason))
                {
                    candidateFailureReasons.Add(selectionReason);
                    continue;
                }

                candidates.Add(
                    new CrossRigidJointSourceSelectionCandidate(
                        instanceIndex,
                        tokenSelection.OwnerVmdPath,
                        tokenSelection.ParentXmlPath,
                        anchorReferenceChain,
                        sourceSelection));
            }

            var candidateGroups = candidates
                .GroupBy(candidate => string.Join(
                    "\u001f",
                    BuildCrossRigidReferenceChainSignature(
                        candidate.AnchorReferenceChain),
                    candidate.AnchorVmdPath,
                    candidate.ParentXmlPath),
                    StringComparer.Ordinal)
                .Where(group =>
                    group
                        .Select(candidate =>
                            candidate.InstanceIndex)
                        .Distinct()
                        .Count() ==
                    occurrenceSet.SourceInstances.Length)
                .Select(group =>
                {
                    var perInstance = group
                        .GroupBy(candidate =>
                            candidate.InstanceIndex)
                        .OrderBy(instanceGroup =>
                            instanceGroup.Key)
                        .Select(instanceGroup =>
                            instanceGroup
                                .OrderBy(candidate =>
                                    candidate.SourceSelection.SlotIndex)
                                .ThenBy(candidate =>
                                    candidate.SourceSelection.AlternativeIndex)
                                .First())
                        .ToArray();
                    return new
                    {
                        Candidates = perInstance,
                        AnchorDepth =
                            perInstance[0].AnchorReferenceChain.Length,
                        ParentDepth =
                            perInstance[0].ParentXmlPath.Count(
                                character => character == '/'),
                    };
                })
                .OrderBy(group => group.AnchorDepth)
                .ThenBy(group => group.ParentDepth)
                .ThenBy(group =>
                    group.Candidates[0].AnchorVmdPath,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(group =>
                    group.Candidates[0].ParentXmlPath,
                    StringComparer.Ordinal)
                .ToArray();

            foreach (var candidateGroup in candidateGroups)
            {
                var selectedCandidates =
                    candidateGroup.Candidates;
                var sourceSelections =
                    selectedCandidates
                        .Select(candidate =>
                            candidate.SourceSelection)
                        .ToArray();

                var incompatibleSharedSlot =
                    sourceSelections
                        .GroupBy(selection =>
                            selection.SlotIndex)
                        .Any(slotGroup =>
                            slotGroup
                                .Select(selection =>
                                    string.Join(
                                        ":",
                                        selection.IsReference
                                            ? "r"
                                            : "m",
                                        selection.AlternativeIndex))
                                .Distinct(StringComparer.Ordinal)
                                .Count() != 1);
                if (incompatibleSharedSlot)
                    continue;

                var selectedDimensionKeys =
                    sourceSelections
                        .Select(
                            BuildCrossRigidJointSourceSelectionDimensionKey)
                        .ToHashSet(
                            StringComparer.Ordinal);
                if (!requiredOptionalDimensionKeys.IsSubsetOf(
                        selectedDimensionKeys))
                {
                    candidateFailureReasons.Add(
                        "same-parent optional writer requires every differing optional slot to be an explicit Cartesian dimension");
                    continue;
                }

                var anchorVmdPath =
                    selectedCandidates[0].AnchorVmdPath;
                var parentXmlPath =
                    selectedCandidates[0].ParentXmlPath;
                var anchorReferenceChain =
                    selectedCandidates[0].AnchorReferenceChain;

                if (!TryGetCrossRigidSourceVmdDocument(
                        state,
                        anchorVmdPath,
                        sourceVmdDocuments,
                        out var anchorDocument,
                        out var anchorReason) ||
                    anchorDocument.SelectSingleNode(
                        parentXmlPath) is not XmlElement parentElement)
                {
                    candidateFailureReasons.Add(
                        string.IsNullOrWhiteSpace(anchorReason)
                            ? "joint parent VARIANT_MESH could not be resolved"
                            : anchorReason);
                    continue;
                }

                var slotIndices =
                    sourceSelections
                        .Select(selection =>
                            selection.SlotIndex)
                        .Distinct()
                        .OrderBy(index => index)
                        .ToArray();
                long combinationCount = 1;
                var valid = true;
                foreach (var slotIndex in slotIndices)
                {
                    if (parentElement.SelectSingleNode(
                            $"SLOT[{slotIndex}]") is
                        not XmlElement slotElement)
                    {
                        valid = false;
                        candidateFailureReasons.Add(
                            $"joint source slot {slotIndex} no longer resolves");
                        break;
                    }

                    var probability =
                        ParseVmdSlotProbability(
                            slotElement.GetAttribute("probability"));
                    if (probability <
                            1.0 - AtlasValueGateExpectedDrawEpsilon &&
                        !IsSupportedCrossRigidOptionalJointSlot(
                            slotElement))
                    {
                        valid = false;
                        candidateFailureReasons.Add(
                            $"joint source slot {slotIndex} has an unsupported optional probability/topology");
                        break;
                    }

                    var alternatives =
                        GetCrossRigidJointAlternatives(
                            slotElement,
                            slotIndex);
                    if (alternatives.Length <= 1)
                    {
                        valid = false;
                        candidateFailureReasons.Add(
                            $"joint source slot {slotIndex} is not an alternative slot");
                        break;
                    }

                    combinationCount *=
                        alternatives.Sum(alternative =>
                            alternative.Multiplicity);
                    if (combinationCount >
                        MaxCrossRigidJointStateCombinations)
                    {
                        // Keep rejecting at the same limit, but measure
                        // the complete weighted product for the report.
                        // This is a count only: no extra VMD states are
                        // generated and no eligibility check is relaxed.
                        foreach (var remainingSlotIndex in
                                 slotIndices.Where(index => index > slotIndex))
                        {
                            if (parentElement.SelectSingleNode(
                                    $"SLOT[{remainingSlotIndex}]") is not
                                XmlElement remainingSlot)
                                break;

                            var factor = GetCrossRigidJointAlternatives(
                                    remainingSlot, remainingSlotIndex)
                                .Sum(alternative => alternative.Multiplicity);
                            if (factor < 1)
                                break;
                            combinationCount =
                                SaturatingCrossRigidStateProduct(
                                    combinationCount, factor);
                        }

                        valid = false;
                        candidateFailureReasons.Add(
                            $"joint Cartesian product exceeds " +
                            $"{MaxCrossRigidJointStateCombinations:N0} combinations" +
                            $"; requiredWeightedStates={combinationCount}");
                        break;
                    }
                }

                if (!valid)
                    continue;

                descriptor =
                    new CrossRigidJointAlwaysPresentRewriteDescriptor(
                        anchorVmdPath,
                        parentXmlPath,
                        anchorReferenceChain,
                        sourceSelections
                            .OrderBy(selection =>
                                selection.SlotIndex)
                            .ThenBy(selection =>
                                BuildCrossRigidOccurrenceProvenanceSignature(
                                    [selection.Instance]),
                                StringComparer.Ordinal)
                            .ToArray(),
                        slotIndices,
                        (int)combinationCount,
                        []);
                return true;
            }

            if (TryDescribeCrossRigidFixedReferenceCommonAncestorRewrite(
                    state,
                    occurrenceSet,
                    sourceVmdDocuments,
                    out descriptor,
                    out var commonAncestorReason))
            {
                return true;
            }

            if (TryDescribeCrossRigidFixedLocalCommonAncestorRewrite(
                    state,
                    occurrenceSet,
                    sourceVmdDocuments,
                    out descriptor,
                    out var localCommonAncestorReason))
            {
                return true;
            }

            // Prefer the general cross-VMD common-ancestor explanation.
            // The fixed-local fallback reports a generic "different
            // defining VMDs" for any mixed-source case, masking the
            // specific reference-carrier or optional-topology constraint.
            reason =
                candidateGroups.Length == 0
                    ? new[]
                        {
                            commonAncestorReason,
                            localCommonAncestorReason,
                        }
                        .FirstOrDefault(value =>
                            !string.IsNullOrWhiteSpace(value)) ??
                      "shared structural parent could not be materialized safely"
                    : candidateFailureReasons
                        .Append(commonAncestorReason)
                        .Append(localCommonAncestorReason)
                        .FirstOrDefault(value =>
                            !string.IsNullOrWhiteSpace(value)) ??
                      "shared structural parent could not be materialized safely";
            return false;
        }

        private static bool
            TryDescribeCrossRigidFixedReferenceCommonAncestorRewrite(
                BatchState state,
                CrossRigidRewriteOccurrenceSet occurrenceSet,
                Dictionary<string, XmlDocument> sourceVmdDocuments,
                out CrossRigidJointAlwaysPresentRewriteDescriptor descriptor,
                out string reason)
        {
            descriptor = null!;
            reason = string.Empty;

            var instances = occurrenceSet.SourceInstances;
            if (instances.Length < 2)
            {
                reason =
                    "common-ancestor joint has fewer than two source occurrences";
                return false;
            }

            var tokenSets = instances
                .Select(instance =>
                    instance.ActivationSignature.Equals(
                        "<always>",
                        StringComparison.Ordinal)
                        ? new HashSet<string>(StringComparer.Ordinal)
                        : instance.ActivationSignature
                            .Split(
                                '\u001f',
                                StringSplitOptions.RemoveEmptyEntries)
                            .ToHashSet(StringComparer.Ordinal))
                .ToArray();
            var commonTokens = new HashSet<string>(
                tokenSets[0],
                StringComparer.Ordinal);
            foreach (var tokenSet in tokenSets.Skip(1))
                commonTokens.IntersectWith(tokenSet);

            var commonReferenceChain =
                GetCrossRigidCommonReferenceChainPrefix(instances);
            string anchorVmdPath;
            if (commonReferenceChain.Length != 0)
            {
                anchorVmdPath =
                    Normalize(
                        commonReferenceChain[^1].ReferencedVmdPath);
            }
            else
            {
                var rootVmdPaths = instances
                    .Select(instance =>
                        instance.ReferenceChain.Length == 0
                            ? Normalize(instance.DefiningVmdPath)
                            : Normalize(
                                instance.ReferenceChain[0].OwnerVmdPath))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (rootVmdPaths.Length != 1)
                {
                    reason =
                        "source selections do not share one structural root VMD";
                    return false;
                }

                anchorVmdPath = rootVmdPaths[0];
            }

            if (!TryGetCrossRigidSourceVmdDocument(
                    state,
                    anchorVmdPath,
                    sourceVmdDocuments,
                    out var anchorDocument,
                    out reason))
            {
                return false;
            }

            string? anchorParentXmlPath = null;
            var sourceSelections =
                new List<CrossRigidJointSourceSelection>();
            var fixedSourceInstances =
                new List<CrossRigidVisualInstance>();
            var fixedCarrierSlotIndices =
                new HashSet<int>();
            var dimensionsByKey =
                new Dictionary<
                    string,
                    CrossRigidJointSelectionDimension>(
                    StringComparer.Ordinal);

            for (var instanceIndex = 0;
                 instanceIndex < instances.Length;
                 instanceIndex++)
            {
                var instance = instances[instanceIndex];
                var orderedDifferingTokens =
                    instance.ActivationSignature.Equals(
                        "<always>",
                        StringComparison.Ordinal)
                        ? Array.Empty<string>()
                        : instance.ActivationSignature
                            .Split(
                                '\u001f',
                                StringSplitOptions.RemoveEmptyEntries)
                            .Where(token =>
                                !commonTokens.Contains(token))
                            .ToArray();
                if (orderedDifferingTokens.Length == 0)
                {
                    if (!TryValidateCrossRigidFixedCommonAncestorSourceInstance(
                            state,
                            instance,
                            sourceVmdDocuments,
                            out reason))
                    {
                        return false;
                    }

                    fixedSourceInstances.Add(instance);
                    continue;
                }

                var dependencySelections =
                    new List<CrossRigidActivationTokenSelection>();
                var conditionalChildSelections =
                    new List<CrossRigidActivationTokenSelection>();
                foreach (var differingToken in
                         orderedDifferingTokens)
                {
                    if (!TryParseCrossRigidActivationToken(
                            state,
                            differingToken,
                            sourceVmdDocuments,
                            out var dependencySelection,
                            out reason))
                    {
                        return false;
                    }

                    // Optional dimensions are admitted only when their
                    // probability has an exact bounded representation.
                    // Parent/child dependence is resolved after collecting
                    // the entire ordered activation chain, rather than
                    // assuming the optional token must be its root.
                    if (dependencySelection.OptionalSlot &&
                        !TryValidateCrossRigidOptionalJointSelection(
                            state,
                            dependencySelection,
                            sourceVmdDocuments,
                            out reason))
                    {
                        return false;
                    }

                    if (!TryGetCrossRigidReferenceChainPrefixToVmd(
                            instance,
                            dependencySelection.OwnerVmdPath,
                            out var dependencyReferenceChain) ||
                        !TryBuildCrossRigidJointSourceSelection(
                            instance,
                            dependencySelection,
                            dependencyReferenceChain,
                            out _,
                            out reason))
                    {
                        if (string.IsNullOrWhiteSpace(reason))
                        {
                            reason =
                                "dependency selection is not on the source provenance chain";
                        }

                        return false;
                    }

                    dependencySelections.Add(
                        dependencySelection);
                }

                // Fixed single-reference prefixes are provenance, not
                // independent appearance choices. Keeping them as the
                // dependency root incorrectly excludes independent child-VMD
                // slots such as teb_billmen -> teb_militia_base -> torso.
                // Preserve the reference hop in the instance's chain so the
                // generated VMD is still cloned copy-on-write.
                while (dependencySelections.Count > 1 &&
                       dependencySelections[0].IsReference)
                {
                    var fixedPrefix = dependencySelections[0];
                    if (!TryGetCrossRigidSourceVmdDocument(
                            state,
                            fixedPrefix.OwnerVmdPath,
                            sourceVmdDocuments,
                            out var prefixDocument,
                            out reason) ||
                        prefixDocument.SelectSingleNode(
                            $"{fixedPrefix.ParentXmlPath}/SLOT[{fixedPrefix.SlotIndex}]") is
                            not XmlElement prefixSlot)
                    {
                        if (string.IsNullOrWhiteSpace(reason))
                            reason = "Fixed reference prefix slot is missing";
                        return false;
                    }

                    if (ParseVmdSlotProbability(
                            prefixSlot.GetAttribute("probability")) <
                            1.0 - AtlasValueGateExpectedDrawEpsilon ||
                        (prefixSlot.SelectNodes("VARIANT_MESH")?.Count ?? 0) != 0 ||
                        (prefixSlot.SelectNodes("VARIANT_MESH_REFERENCE")?.Count ?? 0) != 1 ||
                        fixedPrefix.AlternativeIndex != 1)
                    {
                        break;
                    }

                    dependencySelections.RemoveAt(0);
                    orderedDifferingTokens =
                        orderedDifferingTokens.Skip(1).ToArray();
                }

                // Each varying child must exist only under its exact parent
                // alternative. Preserve the full conditional chain rather
                // than treating a grandchild as an independent Cartesian
                // dimension. Deterministic intermediate selections are
                // included to preserve the parent/child provenance, but a
                // deterministic terminal suffix needs no extra dimension.
                var tokenSelection = dependencySelections[0];
                var lastVaryingSelectionIndex = 0;
                for (var selectionIndex = 1;
                     selectionIndex < dependencySelections.Count;
                     selectionIndex++)
                {
                    var descendant = dependencySelections[selectionIndex];
                    if (!TryGetCrossRigidSourceVmdDocument(
                            state,
                            descendant.OwnerVmdPath,
                            sourceVmdDocuments,
                            out var descendantDocument,
                            out reason) ||
                        descendantDocument.SelectSingleNode(
                            $"{descendant.ParentXmlPath}/SLOT[{descendant.SlotIndex}]") is
                            not XmlElement descendantSlot)
                    {
                        if (string.IsNullOrWhiteSpace(reason))
                            reason = "Nested conditional selection slot is missing";
                        return false;
                    }

                    if (GetCrossRigidJointAlternatives(
                            descendantSlot,
                            descendant.SlotIndex).Length > 1)
                    {
                        lastVaryingSelectionIndex = selectionIndex;
                    }
                }

                if (lastVaryingSelectionIndex != 0)
                {
                    for (var selectionIndex = 1;
                         selectionIndex <= lastVaryingSelectionIndex;
                         selectionIndex++)
                    {
                        var parentSelection =
                            dependencySelections[selectionIndex - 1];
                        var childSelection =
                            dependencySelections[selectionIndex];
                        var expectedChildParent =
                            $"{parentSelection.ParentXmlPath}/" +
                            $"SLOT[{parentSelection.SlotIndex}]/" +
                            $"VARIANT_MESH[{parentSelection.AlternativeIndex}]";
                        var isDirectReferencedChild = false;
                        if (parentSelection.IsReference &&
                            Normalize(parentSelection.OwnerVmdPath).Equals(
                                anchorVmdPath,
                                StringComparison.OrdinalIgnoreCase) &&
                            TryGetCrossRigidReferenceChainPrefixToVmd(
                                instance,
                                childSelection.OwnerVmdPath,
                                out var childReferenceChain) &&
                            childReferenceChain.Length ==
                                commonReferenceChain.Length + 1)
                        {
                            var referenceHop = childReferenceChain[^1];
                            var expectedReferencePath =
                                $"{parentSelection.ParentXmlPath}/" +
                                $"SLOT[{parentSelection.SlotIndex}]/" +
                                $"VARIANT_MESH_REFERENCE[{parentSelection.AlternativeIndex}]";
                            isDirectReferencedChild =
                                Normalize(referenceHop.OwnerVmdPath).Equals(
                                    Normalize(parentSelection.OwnerVmdPath),
                                    StringComparison.OrdinalIgnoreCase) &&
                                Normalize(referenceHop.ReferencedVmdPath).Equals(
                                    Normalize(childSelection.OwnerVmdPath),
                                    StringComparison.OrdinalIgnoreCase) &&
                                referenceHop.ReferenceXmlPath.Equals(
                                    expectedReferencePath,
                                    StringComparison.Ordinal) &&
                                childSelection.ParentXmlPath.StartsWith(
                                    "/VARIANT_MESH",
                                    StringComparison.Ordinal);
                        }

                        var isLocalChild =
                            !parentSelection.IsReference &&
                            Normalize(childSelection.OwnerVmdPath).Equals(
                                Normalize(parentSelection.OwnerVmdPath),
                                StringComparison.OrdinalIgnoreCase) &&
                            childSelection.ParentXmlPath.Equals(
                                expectedChildParent,
                                StringComparison.Ordinal);
                        if (!isLocalChild &&
                            !isDirectReferencedChild)
                        {
                            reason =
                                "common-ancestor optional dependency has nested varying selections";
                            return false;
                        }

                        conditionalChildSelections.Add(childSelection);
                    }
                }

                if (!TryValidateCrossRigidDeterministicJointSuffix(
                        state,
                        orderedDifferingTokens.Skip(
                            lastVaryingSelectionIndex + 1),
                        sourceVmdDocuments,
                        out reason))
                {
                    return false;
                }

                var sourceTokenSelection =
                    conditionalChildSelections.Count == 0
                        ? tokenSelection
                        : conditionalChildSelections[^1];

                if (!TryGetCrossRigidReferenceChainPrefixToVmd(
                        instance,
                        tokenSelection.OwnerVmdPath,
                        out var ownerReferenceChain))
                {
                    reason =
                        "dependency-root selection owner is not on the source provenance chain";
                    return false;
                }

                if (ownerReferenceChain.Length <
                    commonReferenceChain.Length)
                {
                    reason =
                        "terminal selection precedes the common structural anchor";
                    return false;
                }

                for (var hopIndex = 0;
                     hopIndex < commonReferenceChain.Length;
                     hopIndex++)
                {
                    if (!AreCrossRigidReferenceHopsEqual(
                            ownerReferenceChain[hopIndex],
                            commonReferenceChain[hopIndex]))
                    {
                        reason =
                            "terminal selection provenance diverges before the common structural anchor";
                        return false;
                    }
                }

                if (!TryGetCrossRigidReferenceChainPrefixToVmd(
                        instance,
                        sourceTokenSelection.OwnerVmdPath,
                        out var sourceReferenceChain) ||
                    !TryBuildCrossRigidJointSourceSelection(
                        instance,
                        sourceTokenSelection,
                        sourceReferenceChain,
                        out var sourceSelection,
                        out reason))
                {
                    return false;
                }

                int carrierSlotIndex;
                string currentAnchorParentXmlPath;
                var relativeReferenceChain =
                    ownerReferenceChain
                        .Skip(commonReferenceChain.Length)
                        .ToArray();

                if (Normalize(tokenSelection.OwnerVmdPath).Equals(
                        anchorVmdPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    currentAnchorParentXmlPath =
                        tokenSelection.ParentXmlPath;
                    carrierSlotIndex =
                        tokenSelection.SlotIndex;
                    if (relativeReferenceChain.Length != 0)
                    {
                        reason =
                            "direct anchor selection unexpectedly retains descendant reference hops";
                        return false;
                    }
                }
                else
                {
                    if (relativeReferenceChain.Length == 0 ||
                        !Normalize(
                                relativeReferenceChain[0].OwnerVmdPath)
                            .Equals(
                                anchorVmdPath,
                                StringComparison.OrdinalIgnoreCase) ||
                        !TryParseCrossRigidReferenceSlotLocation(
                            relativeReferenceChain[0].ReferenceXmlPath,
                            out currentAnchorParentXmlPath,
                            out carrierSlotIndex,
                            out _))
                    {
                        reason =
                            "common-ancestor carrier slot could not be identified";
                        return false;
                    }

                    foreach (var hop in relativeReferenceChain)
                    {
                        if (!TryParseCrossRigidReferenceSlotLocation(
                                hop.ReferenceXmlPath,
                                out var hopParentXmlPath,
                                out var hopSlotIndex,
                                out var hopReferenceIndex) ||
                            !TryGetCrossRigidSourceVmdDocument(
                                state,
                                hop.OwnerVmdPath,
                                sourceVmdDocuments,
                                out var hopOwnerDocument,
                                out reason) ||
                            hopOwnerDocument.SelectSingleNode(
                                $"{hopParentXmlPath}/SLOT[{hopSlotIndex}]") is
                                not XmlElement hopSlotElement)
                        {
                            if (string.IsNullOrWhiteSpace(reason))
                            {
                                reason =
                                    "fixed reference carrier could not be resolved";
                            }

                            return false;
                        }

                        var hopProbability =
                            ParseVmdSlotProbability(
                                hopSlotElement.GetAttribute(
                                    "probability"));
                        var hopMeshCount =
                            hopSlotElement.SelectNodes(
                                "VARIANT_MESH")?.Count ?? 0;
                        var hopReferenceCount =
                            hopSlotElement.SelectNodes(
                                "VARIANT_MESH_REFERENCE")?.Count ?? 0;
                        if (hopProbability <
                                1.0 -
                                AtlasValueGateExpectedDrawEpsilon ||
                            hopMeshCount != 0 ||
                            hopReferenceCount != 1 ||
                            hopReferenceIndex != 1)
                        {
                            reason =
                                "common-ancestor descendant selection crosses a non-fixed reference carrier";
                            return false;
                        }
                    }
                }

                anchorParentXmlPath ??=
                    currentAnchorParentXmlPath;
                if (!currentAnchorParentXmlPath.Equals(
                        anchorParentXmlPath,
                        StringComparison.Ordinal))
                {
                    reason =
                        "source selections do not share one common-ancestor VARIANT_MESH parent";
                    return false;
                }

                if (anchorDocument.SelectSingleNode(
                        $"{anchorParentXmlPath}/SLOT[{carrierSlotIndex}]") is
                    not XmlElement carrierSlotElement)
                {
                    reason =
                        "common-ancestor carrier slot is missing";
                    return false;
                }

                // The direct optional slot is itself the Cartesian
                // dimension: its absent/present alternatives already have
                // exact integer weights. Optional ancestors of deeper
                // dimensions still require conditional enumeration.
                var directOptionalCarrier =
                    tokenSelection.OptionalSlot &&
                    tokenSelection.SlotIndex == carrierSlotIndex &&
                    Normalize(tokenSelection.OwnerVmdPath).Equals(
                        anchorVmdPath,
                        StringComparison.OrdinalIgnoreCase) &&
                    tokenSelection.ParentXmlPath.Equals(
                        anchorParentXmlPath,
                        StringComparison.Ordinal) &&
                    IsSupportedCrossRigidOptionalJointSlot(
                        carrierSlotElement);
                if (ParseVmdSlotProbability(
                        carrierSlotElement.GetAttribute(
                            "probability")) <
                        1.0 -
                        AtlasValueGateExpectedDrawEpsilon &&
                    !directOptionalCarrier)
                {
                    reason =
                        "common-ancestor optional carrier requires a direct Cartesian dimension";
                    return false;
                }

                if (!TryGetCrossRigidSourceVmdDocument(
                        state,
                        tokenSelection.OwnerVmdPath,
                        sourceVmdDocuments,
                        out var dimensionOwnerDocument,
                        out reason) ||
                    dimensionOwnerDocument.SelectSingleNode(
                        $"{tokenSelection.ParentXmlPath}/SLOT[{tokenSelection.SlotIndex}]") is
                    not XmlElement dimensionSlotElement)
                {
                    if (string.IsNullOrWhiteSpace(reason))
                    {
                        reason =
                            "terminal selection slot could not be resolved";
                    }

                    return false;
                }

                var dimensionAlternatives =
                    GetCrossRigidJointAlternatives(
                            dimensionSlotElement,
                            tokenSelection.SlotIndex)
                        .Select(alternative =>
                            new CrossRigidJointDimensionAlternative(
                                BuildCrossRigidJointSelectionDimensionKey(
                                    tokenSelection.OwnerVmdPath,
                                    tokenSelection.ParentXmlPath,
                                    tokenSelection.SlotIndex),
                                alternative.IsReference,
                                alternative.AlternativeIndex,
                                alternative.Multiplicity))
                        .ToArray();
                if (dimensionAlternatives.Length <= 1)
                {
                    reason =
                        "terminal common-ancestor selection is not an alternative dimension";
                    return false;
                }

                var dimensionKey =
                    BuildCrossRigidJointSelectionDimensionKey(
                        tokenSelection.OwnerVmdPath,
                        tokenSelection.ParentXmlPath,
                        tokenSelection.SlotIndex);
                var dimension =
                    new CrossRigidJointSelectionDimension(
                        dimensionKey,
                        tokenSelection.OwnerVmdPath,
                        tokenSelection.ParentXmlPath,
                        tokenSelection.SlotIndex,
                        carrierSlotIndex,
                        null,
                        relativeReferenceChain,
                        dimensionAlternatives);

                if (dimensionsByKey.TryGetValue(
                        dimensionKey,
                        out var existingDimension))
                {
                    if (existingDimension.CarrierSlotIndex !=
                            carrierSlotIndex ||
                        !BuildCrossRigidReferenceChainSignature(
                                existingDimension
                                    .ReferenceChainFromAnchor)
                            .Equals(
                                BuildCrossRigidReferenceChainSignature(
                                    relativeReferenceChain),
                                StringComparison.Ordinal))
                    {
                        reason =
                            "one selection dimension resolves through incompatible common-ancestor carriers";
                        return false;
                    }
                }
                else
                {
                    dimensionsByKey[dimensionKey] =
                        dimension;
                }

                var conditionalParentSelection = tokenSelection;
                foreach (var childSelection in conditionalChildSelections)
                {
                    var childKey =
                        BuildCrossRigidJointSelectionDimensionKey(
                            childSelection.OwnerVmdPath,
                            childSelection.ParentXmlPath,
                            childSelection.SlotIndex);
                    if (!TryGetCrossRigidSourceVmdDocument(
                            state,
                            childSelection.OwnerVmdPath,
                            sourceVmdDocuments,
                            out var childDocument,
                            out reason) ||
                        childDocument.SelectSingleNode(
                            $"{childSelection.ParentXmlPath}/SLOT[{childSelection.SlotIndex}]") is
                            not XmlElement childSlot)
                    {
                        if (string.IsNullOrWhiteSpace(reason))
                            reason = "Conditional nested child slot is missing";
                        return false;
                    }

                    if (!TryGetCrossRigidReferenceChainPrefixToVmd(
                            instance,
                            childSelection.OwnerVmdPath,
                            out var childOwnerReferenceChain) ||
                        childOwnerReferenceChain.Length <
                            commonReferenceChain.Length)
                    {
                        reason = "Conditional child reference provenance is missing";
                        return false;
                    }

                    var childRelativeReferenceChain =
                        childOwnerReferenceChain
                            .Skip(commonReferenceChain.Length)
                            .ToArray();
                    string? childRelativeSlotPath = null;
                    if (childRelativeReferenceChain.Length == 0)
                    {
                        if (!TryGetCrossRigidRelativeXmlPath(
                                $"{anchorParentXmlPath}/SLOT[{carrierSlotIndex}]",
                                $"{childSelection.ParentXmlPath}/SLOT[{childSelection.SlotIndex}]",
                                out var relativeSlotPath))
                        {
                            reason = "Conditional nested child is outside carrier";
                            return false;
                        }

                        childRelativeSlotPath = relativeSlotPath;
                    }

                    var childAlternatives =
                        GetCrossRigidJointAlternatives(
                                childSlot,
                                childSelection.SlotIndex)
                            .Select(alternative =>
                                new CrossRigidJointDimensionAlternative(
                                    childKey,
                                    alternative.IsReference,
                                    alternative.AlternativeIndex,
                                    alternative.Multiplicity))
                            .ToArray();
                    // A deterministic intermediate ancestor has exactly one
                    // alternative. It still needs a conditional dimension so
                    // that deeper children do not escape their selected branch.
                    if (childAlternatives.Length == 0)
                    {
                        reason = "Conditional nested child has no alternatives";
                        return false;
                    }

                    var parentKey =
                        BuildCrossRigidJointSelectionDimensionKey(
                            conditionalParentSelection.OwnerVmdPath,
                            conditionalParentSelection.ParentXmlPath,
                            conditionalParentSelection.SlotIndex);
                    var childDimension =
                        new CrossRigidJointSelectionDimension(
                            childKey,
                            childSelection.OwnerVmdPath,
                            childSelection.ParentXmlPath,
                            childSelection.SlotIndex,
                            carrierSlotIndex,
                            childRelativeSlotPath,
                            childRelativeReferenceChain,
                            childAlternatives,
                            parentKey,
                            conditionalParentSelection.IsReference,
                            conditionalParentSelection.AlternativeIndex);
                    if (dimensionsByKey.TryGetValue(
                            childKey,
                            out var existingChild))
                    {
                        if (!AreEquivalentCrossRigidJointDimensions(
                                existingChild,
                                childDimension))
                        {
                            reason = "Conditional nested child has incompatible provenance";
                            return false;
                        }
                    }
                    else
                    {
                        dimensionsByKey[childKey] = childDimension;
                    }

                    conditionalParentSelection = childSelection;
                }

                sourceSelections.Add(sourceSelection);
            }

            if (string.IsNullOrWhiteSpace(
                    anchorParentXmlPath) ||
                dimensionsByKey.Count == 0)
            {
                reason =
                    "common-ancestor writer could not identify a probability-1 dependency-root dimension";
                return false;
            }

            foreach (var fixedSourceInstance in
                     fixedSourceInstances)
            {
                if (!TryBuildCrossRigidFixedCommonAncestorSourceSelection(
                        fixedSourceInstance,
                        anchorVmdPath,
                        anchorParentXmlPath,
                        commonReferenceChain,
                        anchorDocument,
                        out var fixedSourceSelection,
                        out var fixedCarrierSlotIndex,
                        out reason))
                {
                    return false;
                }

                sourceSelections.Add(fixedSourceSelection);
                fixedCarrierSlotIndices.Add(
                    fixedCarrierSlotIndex);
            }

            foreach (var dimensionGroup in sourceSelections
                         .GroupBy(
                             BuildCrossRigidJointSourceSelectionDimensionKey,
                             StringComparer.Ordinal))
            {
                if (dimensionGroup
                        .Select(selection =>
                            $"{(selection.IsReference ? "r" : "m")}:" +
                            selection.AlternativeIndex)
                        .Distinct(StringComparer.Ordinal)
                        .Count() != 1)
                {
                    reason =
                        "co-render state selects incompatible alternatives from one common-ancestor dimension";
                    return false;
                }
            }

            // Optional dimensions may only be materialized independently
            // when their absent branch cannot remove another Cartesian
            // dimension's containing subtree. The source-level
            // differing-token check does not detect dependence between
            // dimensions contributed by *different* source instances.
            // Such nested optional domains require conditional rather
            // than independent Cartesian enumeration.
            foreach (var optionalDimension in
                     dimensionsByKey.Values.Where(dimension =>
                         dimension.Alternatives.Any(alternative =>
                             alternative.AlternativeIndex == 0)))
            {
                var optionalSlotXmlPath =
                    $"{optionalDimension.ParentXmlPath}/" +
                    $"SLOT[{optionalDimension.SlotIndex}]";
                foreach (var otherDimension in
                         dimensionsByKey.Values.Where(dimension =>
                             !dimension.DimensionKey.Equals(
                                 optionalDimension.DimensionKey,
                                 StringComparison.Ordinal)))
                {
                    var sameDocumentDescendant =
                        Normalize(otherDimension.OwnerVmdPath)
                            .Equals(
                                Normalize(optionalDimension.OwnerVmdPath),
                                StringComparison.OrdinalIgnoreCase) &&
                        otherDimension.ParentXmlPath.StartsWith(
                            optionalSlotXmlPath + "/",
                            StringComparison.Ordinal);
                    var descendantReference =
                        otherDimension.ReferenceChainFromAnchor.Any(hop =>
                            Normalize(hop.OwnerVmdPath).Equals(
                                Normalize(optionalDimension.OwnerVmdPath),
                                StringComparison.OrdinalIgnoreCase) &&
                            hop.ReferenceXmlPath.StartsWith(
                                optionalSlotXmlPath + "/",
                                StringComparison.Ordinal));
                    if (!sameDocumentDescendant &&
                        !descendantReference)
                    {
                        continue;
                    }

                    // Nested conditional descendants remain safe even when
                    // the optional ancestor is more than one level above the
                    // dependent dimension.
                    var ancestorKey = otherDimension.ParentDimensionKey;
                    var visitedAncestorKeys =
                        new HashSet<string>(StringComparer.Ordinal);
                    var coveredByConditionalParent = false;
                    while (ancestorKey != null &&
                           visitedAncestorKeys.Add(ancestorKey))
                    {
                        if (ancestorKey.Equals(
                                optionalDimension.DimensionKey,
                                StringComparison.Ordinal))
                        {
                            coveredByConditionalParent = true;
                            break;
                        }

                        ancestorKey =
                            dimensionsByKey.TryGetValue(
                                ancestorKey,
                                out var ancestorDimension)
                                ? ancestorDimension.ParentDimensionKey
                                : null;
                    }

                    if (coveredByConditionalParent)
                        continue;

                    reason =
                        "common-ancestor optional slot contains another " +
                        "Cartesian dependency dimension; conditional " +
                        $"rewriting is required ({optionalDimension.DimensionKey} " +
                        $"> {otherDimension.DimensionKey})";
                    return false;
                }
            }

            long combinationCount = 1;
            foreach (var dimension in dimensionsByKey.Values)
            {
                combinationCount = SaturatingCrossRigidStateProduct(
                    combinationCount,
                    dimension.Alternatives.Sum(alternative =>
                        alternative.Multiplicity));
            }

            if (combinationCount > MaxCrossRigidJointStateCombinations)
            {
                reason =
                    $"common-ancestor Cartesian product exceeds " +
                    $"{MaxCrossRigidJointStateCombinations:N0} combinations" +
                    $"; requiredWeightedStates={combinationCount}";
                return false;
            }

            descriptor =
                new CrossRigidJointAlwaysPresentRewriteDescriptor(
                    anchorVmdPath,
                    anchorParentXmlPath,
                    commonReferenceChain,
                    sourceSelections
                        .OrderBy(
                            BuildCrossRigidJointSourceSelectionDimensionKey,
                            StringComparer.Ordinal)
                        .ThenBy(selection =>
                            BuildCrossRigidOccurrenceProvenanceSignature(
                                [selection.Instance]),
                            StringComparer.Ordinal)
                        .ToArray(),
                    dimensionsByKey.Values
                        .Select(dimension =>
                            dimension.CarrierSlotIndex)
                        .Concat(fixedCarrierSlotIndices)
                        .Distinct()
                        .OrderBy(index => index)
                        .ToArray(),
                    (int)combinationCount,
                    dimensionsByKey.Values
                        .OrderBy(
                            dimension =>
                                dimension.DimensionKey,
                            StringComparer.Ordinal)
                        .ToArray());
            return true;
        }

        private static bool
            TryDescribeCrossRigidFixedLocalCommonAncestorRewrite(
                BatchState state,
                CrossRigidRewriteOccurrenceSet occurrenceSet,
                Dictionary<string, XmlDocument> sourceVmdDocuments,
                out CrossRigidJointAlwaysPresentRewriteDescriptor descriptor,
                out string reason)
        {
            descriptor = null!;
            reason = string.Empty;
            var instances =
                occurrenceSet.SourceInstances;
            if (instances.Length < 2)
            {
                reason =
                    "fixed-local common-ancestor joint has fewer than two source occurrences";
                return false;
            }

            var anchorVmdPaths =
                instances
                    .Select(instance =>
                        Normalize(instance.DefiningVmdPath))
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            if (anchorVmdPaths.Length != 1)
            {
                reason =
                    "fixed-local common-ancestor sources belong to different defining VMDs";
                return false;
            }

            var anchorVmdPath =
                anchorVmdPaths[0];
            // All source models must reach the same defining VMD via the
            // same *complete* reference chain. Merely sharing a prefix
            // would allow a rewrite to leak into distinct child branches.
            // The joint writer already clones this anchor chain copy-on-write.
            var commonReferenceChain =
                GetCrossRigidCommonReferenceChainPrefix(instances);
            if (instances.Any(instance =>
                    instance.ReferenceChain.Length !=
                    commonReferenceChain.Length) ||
                (commonReferenceChain.Length != 0 &&
                 !Normalize(
                         commonReferenceChain[^1].ReferencedVmdPath)
                     .Equals(
                         anchorVmdPath,
                         StringComparison.OrdinalIgnoreCase)))
            {
                reason =
                    "fixed-local common-ancestor sources do not share a complete child-VMD reference chain";
                return false;
            }
            if (!TryGetCrossRigidSourceVmdDocument(
                    state,
                    anchorVmdPath,
                    sourceVmdDocuments,
                    out var anchorDocument,
                    out reason))
            {
                return false;
            }

            var tokenSets =
                instances
                    .Select(instance =>
                        instance.ActivationSignature.Equals(
                            "<always>",
                            StringComparison.Ordinal)
                            ? new HashSet<string>(
                                StringComparer.Ordinal)
                            : instance.ActivationSignature
                                .Split(
                                    '\u001f',
                                    StringSplitOptions.RemoveEmptyEntries)
                                .ToHashSet(
                                    StringComparer.Ordinal))
                    .ToArray();
            var commonTokens =
                new HashSet<string>(
                    tokenSets[0],
                    StringComparer.Ordinal);
            foreach (var tokenSet in
                     tokenSets.Skip(1))
            {
                commonTokens.IntersectWith(
                    tokenSet);
            }

            var fixedSourceInstances =
                new List<CrossRigidVisualInstance>();
            var pendingDimensions =
                new List<(
                    CrossRigidActivationTokenSelection TokenSelection,
                    CrossRigidJointSourceSelection SourceSelection)>();
            var anchorParentCandidates =
                new List<string>();

            foreach (var instance in instances)
            {
                var orderedDifferingTokens =
                    instance.ActivationSignature.Equals(
                        "<always>",
                        StringComparison.Ordinal)
                        ? Array.Empty<string>()
                        : instance.ActivationSignature
                            .Split(
                                '\u001f',
                                StringSplitOptions.RemoveEmptyEntries)
                            .Where(token =>
                                !commonTokens.Contains(token))
                            .ToArray();

                if (orderedDifferingTokens.Length == 0)
                {
                    if (!TryValidateCrossRigidFixedCommonAncestorSourceInstance(
                            state,
                            instance,
                            sourceVmdDocuments,
                            out reason) ||
                        !TryGetCrossRigidSelectionParentXmlPath(
                            instance.ModelXmlPath,
                            out var fixedParentXmlPath))
                    {
                        if (string.IsNullOrWhiteSpace(reason))
                        {
                            reason =
                                "fixed-local source parent could not be identified";
                        }

                        return false;
                    }

                    fixedSourceInstances.Add(
                        instance);
                    anchorParentCandidates.Add(
                        fixedParentXmlPath);
                    continue;
                }

                var dependencySelections =
                    new List<CrossRigidActivationTokenSelection>();
                foreach (var differingToken in
                         orderedDifferingTokens)
                {
                    if (!TryParseCrossRigidActivationToken(
                            state,
                            differingToken,
                            sourceVmdDocuments,
                            out var dependencySelection,
                            out reason))
                    {
                        return false;
                    }

                    if (dependencySelection.OptionalSlot)
                    {
                        if (orderedDifferingTokens.Length != 1)
                        {
                            reason =
                                "fixed-local optional dependency has nested varying selections";
                            return false;
                        }

                        if (!TryValidateCrossRigidOptionalJointSelection(
                                state,
                                dependencySelection,
                                sourceVmdDocuments,
                                out reason))
                        {
                            return false;
                        }
                    }

                    if (!Normalize(
                            dependencySelection.OwnerVmdPath)
                        .Equals(
                            anchorVmdPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        reason =
                            "fixed-local dependency selection belongs to another VMD";
                        return false;
                    }

                    dependencySelections.Add(
                        dependencySelection);
                }

                var tokenSelection =
                    dependencySelections[0];
                if (!TryBuildCrossRigidJointSourceSelection(
                        instance,
                        tokenSelection,
                        commonReferenceChain,
                        out var sourceSelection,
                        out reason))
                {
                    return false;
                }

                anchorParentCandidates.Add(
                    tokenSelection.ParentXmlPath);
                pendingDimensions.Add(
                    (
                        tokenSelection,
                        sourceSelection));
            }

            if (pendingDimensions.Count == 0 ||
                !TryGetDeepestCommonCrossRigidVariantMeshParentPath(
                    anchorParentCandidates,
                    out var anchorParentXmlPath) ||
                anchorDocument.SelectSingleNode(
                    anchorParentXmlPath) is
                not XmlElement)
            {
                reason =
                    "fixed-local common structural parent could not be identified";
                return false;
            }

            var sourceSelections =
                new List<CrossRigidJointSourceSelection>();
            var fixedCarrierSlotIndices =
                new HashSet<int>();
            var dimensionsByKey =
                new Dictionary<
                    string,
                    CrossRigidJointSelectionDimension>(
                    StringComparer.Ordinal);

            foreach (var pending in
                     pendingDimensions)
            {
                var tokenSelection =
                    pending.TokenSelection;
                int carrierSlotIndex;
                string? localDimensionSlotRelativeXmlPath =
                    null;

                if (tokenSelection.ParentXmlPath.Equals(
                        anchorParentXmlPath,
                        StringComparison.Ordinal))
                {
                    carrierSlotIndex =
                        tokenSelection.SlotIndex;
                }
                else
                {
                    if (!TryParseCrossRigidImmediateSelectionLocation(
                            anchorParentXmlPath,
                            tokenSelection.ParentXmlPath,
                            out carrierSlotIndex,
                            out _,
                            out _) ||
                        !TryValidateCrossRigidFixedLocalCarrierPath(
                            anchorDocument,
                            anchorParentXmlPath,
                            tokenSelection.ParentXmlPath,
                            out reason))
                    {
                        if (string.IsNullOrWhiteSpace(reason))
                        {
                            reason =
                                "fixed local wrapper could not be lifted to the common parent";
                        }

                        return false;
                    }

                    var carrierSlotXmlPath =
                        $"{anchorParentXmlPath}/" +
                        $"SLOT[{carrierSlotIndex}]";
                    var dimensionSlotXmlPath =
                        $"{tokenSelection.ParentXmlPath}/" +
                        $"SLOT[{tokenSelection.SlotIndex}]";
                    if (!TryGetCrossRigidRelativeXmlPath(
                            carrierSlotXmlPath,
                            dimensionSlotXmlPath,
                            out localDimensionSlotRelativeXmlPath))
                    {
                        reason =
                            "nested local dimension is outside its fixed carrier slot";
                        return false;
                    }
                }

                if (anchorDocument.SelectSingleNode(
                        $"{anchorParentXmlPath}/SLOT[{carrierSlotIndex}]") is
                    not XmlElement carrierSlotElement)
                {
                    reason =
                        "fixed-local common-ancestor carrier slot is missing";
                    return false;
                }

                // Never lift through an optional wrapper unless that
                // wrapper is exactly the enumerated absent/present slot.
                var directOptionalCarrier =
                    tokenSelection.OptionalSlot &&
                    tokenSelection.SlotIndex == carrierSlotIndex &&
                    tokenSelection.ParentXmlPath.Equals(
                        anchorParentXmlPath,
                        StringComparison.Ordinal) &&
                    IsSupportedCrossRigidOptionalJointSlot(
                        carrierSlotElement);
                if (ParseVmdSlotProbability(
                        carrierSlotElement.GetAttribute(
                            "probability")) <
                        1.0 -
                        AtlasValueGateExpectedDrawEpsilon &&
                    !directOptionalCarrier)
                {
                    reason =
                        "fixed-local optional carrier requires a direct Cartesian dimension";
                    return false;
                }

                if (anchorDocument.SelectSingleNode(
                        $"{tokenSelection.ParentXmlPath}/SLOT[{tokenSelection.SlotIndex}]") is
                    not XmlElement dimensionSlotElement)
                {
                    reason =
                        "fixed-local terminal dimension slot could not be resolved";
                    return false;
                }

                var dimensionKey =
                    BuildCrossRigidJointSelectionDimensionKey(
                        tokenSelection.OwnerVmdPath,
                        tokenSelection.ParentXmlPath,
                        tokenSelection.SlotIndex);
                var alternatives =
                    GetCrossRigidJointAlternatives(
                            dimensionSlotElement,
                            tokenSelection.SlotIndex)
                        .Select(alternative =>
                            new CrossRigidJointDimensionAlternative(
                                dimensionKey,
                                alternative.IsReference,
                                alternative.AlternativeIndex,
                                alternative.Multiplicity))
                        .ToArray();
                if (alternatives.Length <= 1)
                {
                    reason =
                        "fixed-local terminal selection is not an alternative dimension";
                    return false;
                }

                var dimension =
                    new CrossRigidJointSelectionDimension(
                        dimensionKey,
                        tokenSelection.OwnerVmdPath,
                        tokenSelection.ParentXmlPath,
                        tokenSelection.SlotIndex,
                        carrierSlotIndex,
                        localDimensionSlotRelativeXmlPath,
                        Array.Empty<
                            CrossRigidVmdReferenceHop>(),
                        alternatives);

                if (dimensionsByKey.TryGetValue(
                        dimensionKey,
                        out var existingDimension))
                {
                    if (existingDimension.CarrierSlotIndex !=
                            carrierSlotIndex ||
                        !string.Equals(
                            existingDimension
                                .LocalDimensionSlotRelativeXmlPath,
                            localDimensionSlotRelativeXmlPath,
                            StringComparison.Ordinal))
                    {
                        reason =
                            "one fixed-local dimension resolves through incompatible carriers";
                        return false;
                    }
                }
                else
                {
                    dimensionsByKey[
                        dimensionKey] =
                        dimension;
                }

                sourceSelections.Add(
                    pending.SourceSelection);
            }

            foreach (var fixedSourceInstance in
                     fixedSourceInstances)
            {
                if (!TryBuildCrossRigidFixedCommonAncestorSourceSelection(
                        fixedSourceInstance,
                        anchorVmdPath,
                        anchorParentXmlPath,
                        commonReferenceChain,
                        anchorDocument,
                        out var fixedSourceSelection,
                        out var fixedCarrierSlotIndex,
                        out reason))
                {
                    return false;
                }

                sourceSelections.Add(
                    fixedSourceSelection);
                fixedCarrierSlotIndices.Add(
                    fixedCarrierSlotIndex);
            }

            foreach (var dimensionGroup in
                     sourceSelections.GroupBy(
                         BuildCrossRigidJointSourceSelectionDimensionKey,
                         StringComparer.Ordinal))
            {
                if (dimensionGroup
                        .Select(selection =>
                            $"{(selection.IsReference ? "r" : "m")}:" +
                            selection.AlternativeIndex)
                        .Distinct(
                            StringComparer.Ordinal)
                        .Count() != 1)
                {
                    reason =
                        "co-render state selects incompatible alternatives from one fixed-local dimension";
                    return false;
                }
            }

            // Optional direct dimensions are independent only when no
            // other dimension lives inside their selected subtree.
            // Otherwise absent/present states must be enumerated
            // conditionally rather than as a plain Cartesian product.
            foreach (var optionalDimension in dimensionsByKey.Values
                         .Where(dimension => dimension.Alternatives.Any(
                             alternative => alternative.AlternativeIndex == 0)))
            {
                var optionalSlotPath =
                    $"{optionalDimension.ParentXmlPath}/SLOT[{optionalDimension.SlotIndex}]";
                foreach (var otherDimension in dimensionsByKey.Values)
                {
                    if (otherDimension.DimensionKey.Equals(
                            optionalDimension.DimensionKey,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (Normalize(otherDimension.OwnerVmdPath).Equals(
                            Normalize(optionalDimension.OwnerVmdPath),
                            StringComparison.OrdinalIgnoreCase) &&
                        otherDimension.ParentXmlPath.StartsWith(
                            optionalSlotPath + "/",
                            StringComparison.Ordinal))
                    {
                        reason =
                            "fixed-local optional Cartesian dimension contains a nested varying dimension";
                        return false;
                    }
                }
            }

            long combinationCount = 1;
            foreach (var dimension in
                     dimensionsByKey.Values)
            {
                combinationCount *=
                    dimension.Alternatives.Sum(alternative =>
                        alternative.Multiplicity);
                if (combinationCount >
                    MaxCrossRigidJointStateCombinations)
                {
                    reason =
                        $"fixed-local Cartesian product exceeds " +
                        $"{MaxCrossRigidJointStateCombinations:N0} combinations";
                    return false;
                }
            }

            descriptor =
                new CrossRigidJointAlwaysPresentRewriteDescriptor(
                    anchorVmdPath,
                    anchorParentXmlPath,
                    commonReferenceChain,
                    sourceSelections
                        .OrderBy(
                            BuildCrossRigidJointSourceSelectionDimensionKey,
                            StringComparer.Ordinal)
                        .ThenBy(selection =>
                            BuildCrossRigidOccurrenceProvenanceSignature(
                                [selection.Instance]),
                            StringComparer.Ordinal)
                        .ToArray(),
                    dimensionsByKey.Values
                        .Select(dimension =>
                            dimension.CarrierSlotIndex)
                        .Concat(
                            fixedCarrierSlotIndices)
                        .Distinct()
                        .OrderBy(index => index)
                        .ToArray(),
                    (int)combinationCount,
                    dimensionsByKey.Values
                        .OrderBy(
                            dimension =>
                                dimension.DimensionKey,
                            StringComparer.Ordinal)
                        .ToArray());
            return true;
        }

        private static bool
            TryGetCrossRigidSelectionParentXmlPath(
                string selectionXmlPath,
                out string parentXmlPath)
        {
            parentXmlPath =
                string.Empty;
            const string slotMarker =
                "/SLOT[";
            var slotMarkerIndex =
                selectionXmlPath.LastIndexOf(
                    slotMarker,
                    StringComparison.Ordinal);
            if (slotMarkerIndex <= 0)
                return false;

            parentXmlPath =
                selectionXmlPath[
                    ..slotMarkerIndex];
            return parentXmlPath.Length != 0;
        }

        private static bool
            TryGetDeepestCommonCrossRigidVariantMeshParentPath(
                IReadOnlyList<string> parentXmlPaths,
                out string commonParentXmlPath)
        {
            commonParentXmlPath =
                string.Empty;
            if (parentXmlPaths.Count == 0)
                return false;

            var splitPaths =
                parentXmlPaths
                    .Select(path =>
                        path.Split(
                            '/',
                            StringSplitOptions
                                .RemoveEmptyEntries))
                    .ToArray();
            var commonLength =
                splitPaths.Min(parts =>
                    parts.Length);

            for (var partIndex = 0;
                 partIndex < commonLength;
                 partIndex++)
            {
                var candidate =
                    splitPaths[0][partIndex];
                if (splitPaths
                    .Skip(1)
                    .Any(parts =>
                        !parts[partIndex].Equals(
                            candidate,
                            StringComparison.Ordinal)))
                {
                    commonLength =
                        partIndex;
                    break;
                }
            }

            while (commonLength > 0 &&
                   splitPaths[0][commonLength - 1]
                       .StartsWith(
                           "SLOT[",
                           StringComparison.Ordinal))
            {
                commonLength--;
            }

            if (commonLength == 0)
                return false;

            var terminalPart =
                splitPaths[0][commonLength - 1];
            // Traversal records the document root as /VARIANT_MESH,
            // while nested model alternatives use VARIANT_MESH[n].
            // Both forms are valid structural parents for a joint that
            // spans sibling top-level slots.
            if (!terminalPart.Equals(
                    "<root>",
                    StringComparison.Ordinal) &&
                !terminalPart.Equals(
                    "VARIANT_MESH",
                    StringComparison.Ordinal) &&
                !terminalPart.StartsWith(
                    "VARIANT_MESH[",
                    StringComparison.Ordinal))
            {
                return false;
            }

            commonParentXmlPath =
                (parentXmlPaths[0].StartsWith(
                    "/",
                    StringComparison.Ordinal)
                    ? "/"
                    : string.Empty) +
                string.Join(
                    "/",
                    splitPaths[0]
                        .Take(commonLength));
            return commonParentXmlPath.Length !=
                   0;
        }

        private static bool
            TryValidateCrossRigidFixedLocalCarrierPath(
                XmlDocument document,
                string ancestorParentXmlPath,
                string descendantXmlPath,
                out string reason)
        {
            reason =
                string.Empty;
            if (document.SelectSingleNode(
                    ancestorParentXmlPath) is
                not XmlElement ancestorElement ||
                document.SelectSingleNode(
                    descendantXmlPath) is
                not XmlElement descendantElement)
            {
                reason =
                    "fixed local carrier path could not be resolved";
                return false;
            }

            XmlElement? current =
                descendantElement;
            while (current != null &&
                   !ReferenceEquals(
                       current,
                       ancestorElement))
            {
                if (current.ParentNode is
                    XmlElement slotElement &&
                    slotElement.Name.Equals(
                        "SLOT",
                        StringComparison.Ordinal))
                {
                    var probability =
                        ParseVmdSlotProbability(
                            slotElement.GetAttribute(
                                "probability"));
                    if (probability <
                        1.0 -
                        AtlasValueGateExpectedDrawEpsilon)
                    {
                        reason =
                            "common-ancestor descendant selection crosses an optional local carrier";
                        return false;
                    }

                    // The local carrier itself does not have to be a
                    // single-alternative slot. It is cloned intact into
                    // every outer Cartesian wrapper, so its own
                    // probability-1 alternative distribution is preserved.
                    // Only the nested dependency dimension is reduced in
                    // the branch where it exists. Sibling carrier
                    // alternatives therefore retain exactly their original
                    // aggregate probability across the wrappers.

                    current =
                        slotElement.ParentNode as
                            XmlElement;
                    continue;
                }

                current =
                    current.ParentNode as
                        XmlElement;
            }

            if (!ReferenceEquals(
                    current,
                    ancestorElement))
            {
                reason =
                    "fixed local carrier path does not descend from the common structural parent";
                return false;
            }

            return true;
        }

        private static bool
            TryValidateCrossRigidFixedCommonAncestorSourceInstance(
                BatchState state,
                CrossRigidVisualInstance instance,
                Dictionary<string, XmlDocument> sourceVmdDocuments,
                out string reason)
        {
            reason = string.Empty;
            if (instance.ActivationSignature.Equals(
                    "<always>",
                    StringComparison.Ordinal))
            {
                return true;
            }

            foreach (var token in
                     instance.ActivationSignature.Split(
                         '\u001f',
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TryParseCrossRigidActivationToken(
                        state,
                        token,
                        sourceVmdDocuments,
                        out var selection,
                        out reason))
                {
                    return false;
                }

                if (selection.OptionalSlot)
                {
                    reason =
                        "common-ancestor fixed source crosses an optional slot";
                    return false;
                }
            }

            return true;
        }

        private static bool
            TryBuildCrossRigidFixedCommonAncestorSourceSelection(
                CrossRigidVisualInstance instance,
                string anchorVmdPath,
                string anchorParentXmlPath,
                IReadOnlyList<CrossRigidVmdReferenceHop>
                    commonReferenceChain,
                XmlDocument anchorDocument,
                out CrossRigidJointSourceSelection sourceSelection,
                out int carrierSlotIndex,
                out string reason)
        {
            sourceSelection = null!;
            carrierSlotIndex = 0;
            reason = string.Empty;

            string provenanceXmlPath;
            if (instance.ReferenceChain.Length >
                commonReferenceChain.Count)
            {
                var firstDescendantHop =
                    instance.ReferenceChain[
                        commonReferenceChain.Count];
                if (!Normalize(
                        firstDescendantHop.OwnerVmdPath)
                    .Equals(
                        Normalize(anchorVmdPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    reason =
                        "fixed source provenance does not descend from the common structural anchor";
                    return false;
                }

                provenanceXmlPath =
                    firstDescendantHop.ReferenceXmlPath;
            }
            else
            {
                if (!Normalize(instance.DefiningVmdPath)
                    .Equals(
                        Normalize(anchorVmdPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    reason =
                        "fixed source defining VMD does not match the common structural anchor";
                    return false;
                }

                provenanceXmlPath =
                    instance.ModelXmlPath;
            }

            if (!TryParseCrossRigidImmediateSelectionLocation(
                    anchorParentXmlPath,
                    provenanceXmlPath,
                    out carrierSlotIndex,
                    out var isReference,
                    out var alternativeIndex))
            {
                reason =
                    "fixed source carrier selection could not be identified under the common structural parent";
                return false;
            }

            if (anchorDocument.SelectSingleNode(
                    $"{anchorParentXmlPath}/SLOT[{carrierSlotIndex}]") is
                not XmlElement carrierSlotElement)
            {
                reason =
                    $"fixed source carrier slot {carrierSlotIndex} could not be resolved";
                return false;
            }

            var carrierProbability =
                ParseVmdSlotProbability(
                    carrierSlotElement.GetAttribute(
                        "probability"));
            if (carrierProbability <
                1.0 - AtlasValueGateExpectedDrawEpsilon)
            {
                reason =
                    $"fixed source carrier slot {carrierSlotIndex} is optional";
                return false;
            }

            var tokenSelection =
                new CrossRigidActivationTokenSelection(
                    Normalize(anchorVmdPath),
                    anchorParentXmlPath,
                    carrierSlotIndex,
                    isReference,
                    alternativeIndex,
                    OptionalSlot: false);
            return TryBuildCrossRigidJointSourceSelection(
                instance,
                tokenSelection,
                commonReferenceChain,
                out sourceSelection,
                out reason);
        }

        private static bool
            TryParseCrossRigidImmediateSelectionLocation(
                string parentXmlPath,
                string provenanceXmlPath,
                out int slotIndex,
                out bool isReference,
                out int alternativeIndex)
        {
            slotIndex = 0;
            isReference = false;
            alternativeIndex = 0;

            var prefix =
                $"{parentXmlPath}/SLOT[";
            if (!provenanceXmlPath.StartsWith(
                    prefix,
                    StringComparison.Ordinal))
            {
                return false;
            }

            var slotIndexStart =
                prefix.Length;
            var slotIndexEnd =
                provenanceXmlPath.IndexOf(
                    ']',
                    slotIndexStart);
            if (slotIndexEnd <= slotIndexStart ||
                !int.TryParse(
                    provenanceXmlPath[
                        slotIndexStart..
                        slotIndexEnd],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out slotIndex) ||
                slotIndex <= 0 ||
                slotIndexEnd + 2 >
                    provenanceXmlPath.Length ||
                provenanceXmlPath[
                    slotIndexEnd + 1] != '/')
            {
                slotIndex = 0;
                return false;
            }

            var selectionStart =
                slotIndexEnd + 2;
            const string referencePrefix =
                "VARIANT_MESH_REFERENCE[";
            const string meshPrefix =
                "VARIANT_MESH[";
            string selectionPrefix;
            if (provenanceXmlPath.AsSpan(
                    selectionStart)
                .StartsWith(
                    referencePrefix.AsSpan(),
                    StringComparison.Ordinal))
            {
                isReference = true;
                selectionPrefix =
                    referencePrefix;
            }
            else if (provenanceXmlPath.AsSpan(
                         selectionStart)
                     .StartsWith(
                         meshPrefix.AsSpan(),
                         StringComparison.Ordinal))
            {
                selectionPrefix =
                    meshPrefix;
            }
            else
            {
                slotIndex = 0;
                return false;
            }

            var alternativeIndexStart =
                selectionStart +
                selectionPrefix.Length;
            var alternativeIndexEnd =
                provenanceXmlPath.IndexOf(
                    ']',
                    alternativeIndexStart);
            if (alternativeIndexEnd <=
                    alternativeIndexStart ||
                !int.TryParse(
                    provenanceXmlPath[
                        alternativeIndexStart..
                        alternativeIndexEnd],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out alternativeIndex) ||
                alternativeIndex <= 0)
            {
                slotIndex = 0;
                alternativeIndex = 0;
                return false;
            }

            return true;
        }

        private static CrossRigidVmdReferenceHop[]
            GetCrossRigidCommonReferenceChainPrefix(
                IReadOnlyList<CrossRigidVisualInstance> instances)
        {
            if (instances.Count == 0)
                return [];

            var minimumLength =
                instances.Min(instance =>
                    instance.ReferenceChain.Length);
            var commonLength = 0;
            while (commonLength < minimumLength)
            {
                var candidate =
                    instances[0].ReferenceChain[commonLength];
                if (instances.Skip(1).Any(instance =>
                        !AreCrossRigidReferenceHopsEqual(
                            candidate,
                            instance.ReferenceChain[commonLength])))
                {
                    break;
                }

                commonLength++;
            }

            return instances[0]
                .ReferenceChain
                .Take(commonLength)
                .ToArray();
        }

        private static bool
            TryParseCrossRigidReferenceSlotLocation(
                string referenceXmlPath,
                out string parentXmlPath,
                out int slotIndex,
                out int referenceIndex)
        {
            parentXmlPath = string.Empty;
            slotIndex = 0;
            referenceIndex = 0;

            const string slotMarker = "/SLOT[";
            const string referenceMarker =
                "]/VARIANT_MESH_REFERENCE[";
            var slotMarkerIndex =
                referenceXmlPath.LastIndexOf(
                    slotMarker,
                    StringComparison.Ordinal);
            var referenceMarkerIndex =
                referenceXmlPath.LastIndexOf(
                    referenceMarker,
                    StringComparison.Ordinal);
            if (slotMarkerIndex <= 0 ||
                referenceMarkerIndex <=
                    slotMarkerIndex + slotMarker.Length)
            {
                return false;
            }

            var slotText = referenceXmlPath[
                (slotMarkerIndex + slotMarker.Length)..
                referenceMarkerIndex];
            var referenceIndexStart =
                referenceMarkerIndex +
                referenceMarker.Length;
            var referenceIndexEnd =
                referenceXmlPath.IndexOf(
                    ']',
                    referenceIndexStart);
            if (referenceIndexEnd <= referenceIndexStart ||
                !int.TryParse(
                    slotText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out slotIndex) ||
                !int.TryParse(
                    referenceXmlPath[
                        referenceIndexStart..
                        referenceIndexEnd],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out referenceIndex) ||
                slotIndex <= 0 ||
                referenceIndex <= 0)
            {
                slotIndex = 0;
                referenceIndex = 0;
                return false;
            }

            parentXmlPath =
                referenceXmlPath[..slotMarkerIndex];
            return !string.IsNullOrWhiteSpace(
                parentXmlPath);
        }

        private static string
            BuildCrossRigidJointSelectionDimensionKey(
                string ownerVmdPath,
                string parentXmlPath,
                int slotIndex)
            => string.Join(
                "\u001f",
                Normalize(ownerVmdPath),
                parentXmlPath,
                slotIndex.ToString(
                    CultureInfo.InvariantCulture));

        private static string
            BuildCrossRigidJointSourceSelectionDimensionKey(
                CrossRigidJointSourceSelection selection)
            => BuildCrossRigidJointSelectionDimensionKey(
                selection.SelectionOwnerVmdPath,
                selection.SelectionParentXmlPath,
                selection.SlotIndex);

        private static bool TryGetCrossRigidReferenceChainPrefixToVmd(
            CrossRigidVisualInstance instance,
            string targetVmdPathValue,
            out CrossRigidVmdReferenceHop[] prefix)
        {
            var targetVmdPath =
                Normalize(targetVmdPathValue);
            var referenceChain =
                instance.ReferenceChain;
            if (referenceChain.Length == 0)
            {
                prefix = [];
                return Normalize(instance.DefiningVmdPath).Equals(
                    targetVmdPath,
                    StringComparison.OrdinalIgnoreCase);
            }

            if (Normalize(referenceChain[0].OwnerVmdPath).Equals(
                    targetVmdPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                prefix = [];
                return true;
            }

            for (var hopIndex = 0;
                 hopIndex < referenceChain.Length;
                 hopIndex++)
            {
                if (!Normalize(
                        referenceChain[hopIndex].ReferencedVmdPath)
                    .Equals(
                        targetVmdPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                prefix =
                    referenceChain
                        .Take(hopIndex + 1)
                        .ToArray();
                return true;
            }

            prefix = [];
            return false;
        }

        private static bool TryBuildCrossRigidJointSourceSelection(
            CrossRigidVisualInstance instance,
            CrossRigidActivationTokenSelection tokenSelection,
            IReadOnlyList<CrossRigidVmdReferenceHop>
                anchorReferenceChain,
            out CrossRigidJointSourceSelection sourceSelection,
            out string reason)
        {
            sourceSelection = null!;
            reason = string.Empty;
            var anchorReferenceHopIndex = -1;
            string? directModelRelativeXmlPath = null;
            string? directFirstReferenceRelativeXmlPath = null;
            var parentXmlPath =
                tokenSelection.ParentXmlPath;

            if (instance.ReferenceChain.Length <
                anchorReferenceChain.Count)
            {
                reason =
                    "source reference chain is shorter than the structural anchor chain";
                return false;
            }

            for (var hopIndex = 0;
                 hopIndex < anchorReferenceChain.Count;
                 hopIndex++)
            {
                if (!AreCrossRigidReferenceHopsEqual(
                        instance.ReferenceChain[hopIndex],
                        anchorReferenceChain[hopIndex]))
                {
                    reason =
                        "source reference chain no longer matches the structural anchor";
                    return false;
                }
            }

            if (tokenSelection.IsReference)
            {
                if (instance.ReferenceChain.Length <=
                    anchorReferenceChain.Count)
                {
                    reason =
                        "selected reference branch has no recorded child-VMD hop";
                    return false;
                }

                var expectedReferenceXmlPath =
                    $"{parentXmlPath}/SLOT[{tokenSelection.SlotIndex}]/" +
                    $"VARIANT_MESH_REFERENCE[{tokenSelection.AlternativeIndex}]";
                var anchorHop =
                    instance.ReferenceChain[
                        anchorReferenceChain.Count];
                if (!Normalize(anchorHop.OwnerVmdPath).Equals(
                        tokenSelection.OwnerVmdPath,
                        StringComparison.OrdinalIgnoreCase) ||
                    !anchorHop.ReferenceXmlPath.Equals(
                        expectedReferenceXmlPath,
                        StringComparison.Ordinal))
                {
                    reason =
                        "selected reference alternative does not contain the source provenance";
                    return false;
                }

                anchorReferenceHopIndex =
                    anchorReferenceChain.Count;
            }
            else
            {
                var expectedModelXmlPath =
                    $"{parentXmlPath}/SLOT[{tokenSelection.SlotIndex}]/" +
                    $"VARIANT_MESH[{tokenSelection.AlternativeIndex}]";

                if (instance.ReferenceChain.Length ==
                    anchorReferenceChain.Count)
                {
                    if (!Normalize(instance.DefiningVmdPath).Equals(
                            tokenSelection.OwnerVmdPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        reason =
                            "direct structural selection does not own the source model";
                        return false;
                    }

                    if (!instance.ModelXmlPath.Equals(
                            expectedModelXmlPath,
                            StringComparison.Ordinal) &&
                        !TryGetCrossRigidRelativeXmlPath(
                            expectedModelXmlPath,
                            instance.ModelXmlPath,
                            out directModelRelativeXmlPath))
                    {
                        reason =
                            "source model is outside the selected structural subtree";
                        return false;
                    }
                }
                else
                {
                    anchorReferenceHopIndex =
                        anchorReferenceChain.Count;
                    var firstNestedHop =
                        instance.ReferenceChain[
                            anchorReferenceHopIndex];
                    if (!Normalize(firstNestedHop.OwnerVmdPath).Equals(
                            tokenSelection.OwnerVmdPath,
                            StringComparison.OrdinalIgnoreCase) ||
                        !TryGetCrossRigidRelativeXmlPath(
                            expectedModelXmlPath,
                            firstNestedHop.ReferenceXmlPath,
                            out directFirstReferenceRelativeXmlPath))
                    {
                        reason =
                            "source child-VMD provenance is outside the selected structural subtree";
                        return false;
                    }
                }
            }

            sourceSelection =
                new CrossRigidJointSourceSelection(
                    instance,
                    tokenSelection.OwnerVmdPath,
                    tokenSelection.ParentXmlPath,
                    tokenSelection.SlotIndex,
                    tokenSelection.IsReference,
                    tokenSelection.AlternativeIndex,
                    anchorReferenceHopIndex,
                    directModelRelativeXmlPath,
                    directFirstReferenceRelativeXmlPath);
            return true;
        }

        private static bool TryGetCrossRigidRelativeXmlPath(
            string ancestorXmlPath,
            string descendantXmlPath,
            out string relativeXmlPath)
        {
            relativeXmlPath = string.Empty;
            if (string.IsNullOrWhiteSpace(ancestorXmlPath) ||
                string.IsNullOrWhiteSpace(descendantXmlPath) ||
                descendantXmlPath.Length <= ancestorXmlPath.Length ||
                !descendantXmlPath.StartsWith(
                    ancestorXmlPath,
                    StringComparison.Ordinal) ||
                descendantXmlPath[ancestorXmlPath.Length] != '/')
            {
                return false;
            }

            relativeXmlPath =
                descendantXmlPath[(ancestorXmlPath.Length + 1)..];
            return relativeXmlPath.Length != 0;
        }

        private static bool AreCrossRigidReferenceHopsEqual(
            CrossRigidVmdReferenceHop left,
            CrossRigidVmdReferenceHop right)
            => Normalize(left.OwnerVmdPath).Equals(
                   Normalize(right.OwnerVmdPath),
                   StringComparison.OrdinalIgnoreCase) &&
               left.ReferenceXmlPath.Equals(
                   right.ReferenceXmlPath,
                   StringComparison.Ordinal) &&
               Normalize(left.ReferencedVmdPath).Equals(
                   Normalize(right.ReferencedVmdPath),
                   StringComparison.OrdinalIgnoreCase);

        private static string BuildCrossRigidReferenceChainSignature(
            IEnumerable<CrossRigidVmdReferenceHop> referenceChain)
            => string.Join(
                "\u001e",
                referenceChain.Select(hop => string.Join(
                    "|",
                    Normalize(hop.OwnerVmdPath),
                    hop.ReferenceXmlPath,
                    Normalize(hop.ReferencedVmdPath))));

        private static bool TryParseCrossRigidActivationToken(
            BatchState state,
            string token,
            Dictionary<string, XmlDocument> sourceVmdDocuments,
            out CrossRigidActivationTokenSelection selection,
            out string reason)
        {
            selection = null!;
            reason = string.Empty;

            var ownerSeparator = token.IndexOf(
                ":",
                StringComparison.Ordinal);
            var slotMarker = token.LastIndexOf(
                "/slot=",
                StringComparison.Ordinal);
            var meshMarker = token.LastIndexOf(
                "/mesh=",
                StringComparison.Ordinal);
            var referenceMarker = token.LastIndexOf(
                "/ref=",
                StringComparison.Ordinal);
            var branchMarker = Math.Max(
                meshMarker,
                referenceMarker);
            if (ownerSeparator <= 0 ||
                slotMarker <= ownerSeparator ||
                branchMarker <= slotMarker)
            {
                reason =
                    $"activation token could not be parsed: {token}";
                return false;
            }

            var slotIndexStart =
                slotMarker + "/slot=".Length;
            var slotIndexText = token[
                slotIndexStart..branchMarker];
            if (!int.TryParse(
                    slotIndexText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var slotIndex) ||
                slotIndex <= 0)
            {
                reason =
                    $"activation token has invalid slot index: {token}";
                return false;
            }

            var isReference = referenceMarker == branchMarker;
            var alternativeMarker =
                isReference ? "/ref=" : "/mesh=";
            var alternativeIndexText = token[
                (branchMarker + alternativeMarker.Length)..];
            if (!int.TryParse(
                    alternativeIndexText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var alternativeIndex) ||
                alternativeIndex <= 0)
            {
                reason =
                    $"activation token has invalid alternative index: {token}";
                return false;
            }

            var ownerVmdPath = Normalize(
                token[..ownerSeparator]);
            var parentXmlPath = token[
                (ownerSeparator + 1)..slotMarker];
            if (!TryGetCrossRigidSourceVmdDocument(
                    state,
                    ownerVmdPath,
                    sourceVmdDocuments,
                    out var ownerDocument,
                    out reason))
            {
                return false;
            }

            if (ownerDocument.SelectSingleNode(
                    $"{parentXmlPath}/SLOT[{slotIndex}]") is
                not XmlElement slotElement)
            {
                reason =
                    $"activation token slot no longer resolves in source VMD: {token}";
                return false;
            }

            var slotProbability =
                ParseVmdSlotProbability(
                    slotElement.GetAttribute("probability"));
            selection =
                new CrossRigidActivationTokenSelection(
                    ownerVmdPath,
                    parentXmlPath,
                    slotIndex,
                    isReference,
                    alternativeIndex,
                    slotProbability <
                        1.0 - AtlasValueGateExpectedDrawEpsilon);
            return true;
        }

        private static bool
            TryValidateCrossRigidDeterministicJointSuffix(
                BatchState state,
                IEnumerable<string> descendantTokens,
                Dictionary<string, XmlDocument> sourceVmdDocuments,
                out string reason)
        {
            reason = string.Empty;
            foreach (var descendantToken in descendantTokens)
            {
                if (!TryParseCrossRigidActivationToken(
                        state,
                        descendantToken,
                        sourceVmdDocuments,
                        out var selection,
                        out reason) ||
                    !TryGetCrossRigidSourceVmdDocument(
                        state,
                        selection.OwnerVmdPath,
                        sourceVmdDocuments,
                        out var ownerDocument,
                        out reason))
                {
                    return false;
                }

                if (ownerDocument.SelectSingleNode(
                        $"{selection.ParentXmlPath}/SLOT[{selection.SlotIndex}]") is
                    not XmlElement slotElement)
                {
                    reason =
                        "nested optional dependency's descendant slot is missing";
                    return false;
                }

                var alternativeCount =
                    (slotElement.SelectNodes("VARIANT_MESH")?.Count ?? 0) +
                    (slotElement.SelectNodes("VARIANT_MESH_REFERENCE")?.Count ?? 0);
                if (selection.OptionalSlot || alternativeCount != 1)
                {
                    reason =
                        "common-ancestor optional dependency has nested varying selections";
                    return false;
                }
            }

            return true;
        }

        private static bool
            TryValidateCrossRigidOptionalJointSelection(
                BatchState state,
                CrossRigidActivationTokenSelection selection,
                Dictionary<string, XmlDocument> sourceVmdDocuments,
                out string reason)
        {
            reason = string.Empty;
            if (!selection.OptionalSlot)
                return true;

            if (!TryGetCrossRigidSourceVmdDocument(
                    state,
                    selection.OwnerVmdPath,
                    sourceVmdDocuments,
                    out var ownerDocument,
                    out reason))
            {
                return false;
            }

            if (ownerDocument.SelectSingleNode(
                    $"{selection.ParentXmlPath}/SLOT[{selection.SlotIndex}]") is
                not XmlElement slotElement)
            {
                reason =
                    "optional joint slot no longer resolves in source VMD";
                return false;
            }

            if (!IsSupportedCrossRigidOptionalJointSlot(
                    slotElement))
            {
                reason =
                    "optional joint writer requires a nonempty slot, a bounded exact rational probability, and a bounded weighted alternative expansion";
                return false;
            }

            return true;
        }

        private static bool
            IsSupportedCrossRigidOptionalJointSlot(
                XmlElement slotElement)
            => TryGetCrossRigidOptionalJointMultiplicities(
                slotElement,
                out _,
                out _);

        private static bool
            TryGetCrossRigidOptionalJointMultiplicities(
                XmlElement slotElement,
                out int absentMultiplicity,
                out int presentMultiplicity)
        {
            absentMultiplicity = 0;
            presentMultiplicity = 0;
            var alternativeCount =
                (slotElement.SelectNodes(
                    "VARIANT_MESH")?.Count ?? 0) +
                (slotElement.SelectNodes(
                    "VARIANT_MESH_REFERENCE")?.Count ?? 0);
            if (alternativeCount == 0)
                return false;

            var probabilityText =
                slotElement.GetAttribute("probability");
            if (!decimal.TryParse(
                    probabilityText,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var probability) ||
                probability <= 0 ||
                probability >= 1)
            {
                return false;
            }

            const int maxExactDenominator = 32;
            for (var denominator = 2;
                 denominator <= maxExactDenominator;
                 denominator++)
            {
                var scaled =
                    probability * denominator;
                if (scaled != decimal.Truncate(scaled))
                    continue;

                var numerator =
                    decimal.ToInt32(scaled);
                if (numerator <= 0 ||
                    numerator >= denominator)
                {
                    continue;
                }

                // With k equally likely present alternatives and
                // probability n/d, the absent weight is (d - n) * k
                // and each present alternative has weight n. Divide
                // every weight by their common divisor before counting
                // emitted wrappers. The distribution remains *exact*:
                // P(absent) = (d - n) / d and
                // P(each present alternative) = n / (d * k).
                //
                // This matters when k and n share a factor: an optional
                // 90% slot with three alternatives needs only 10
                // equally likely wrappers (1 absent, 3 per present
                // alternative), not the unreduced 30.
                var absentWeight =
                    (long)(denominator - numerator) * alternativeCount;
                long divisor = numerator;
                var remainder = absentWeight;
                while (remainder != 0)
                {
                    var next = divisor % remainder;
                    divisor = remainder;
                    remainder = next;
                }

                var reducedAbsent = absentWeight / divisor;
                var reducedPresent = numerator / divisor;
                var weightedCount =
                    reducedAbsent + reducedPresent * alternativeCount;
                if (weightedCount > MaxCrossRigidJointStateCombinations)
                    return false;

                absentMultiplicity = (int)reducedAbsent;
                presentMultiplicity = (int)reducedPresent;
                return true;
            }

            return false;
        }

        private static bool TryClassifyCrossRigidActivationToken(
            BatchState state,
            string token,
            Dictionary<string, XmlDocument> sourceVmdDocuments,
            out bool optionalSlot,
            out string reason)
        {
            if (!TryParseCrossRigidActivationToken(
                    state,
                    token,
                    sourceVmdDocuments,
                    out var selection,
                    out reason))
            {
                optionalSlot = false;
                return false;
            }

            optionalSlot = selection.OptionalSlot;
            return true;
        }

        private static string GetCrossRigidActivationRewriteClassLabel(
            CrossRigidActivationRewriteClass rewriteClass)
            => rewriteClass switch
            {
                CrossRigidActivationRewriteClass.ExactActivationSafe =>
                    "exact activation (current writer)",
                CrossRigidActivationRewriteClass.JointAlwaysPresentSlots =>
                    "joint state across probability-1 slots/alternatives",
                CrossRigidActivationRewriteClass.JointOptionalSlots =>
                    "joint state involving an optional slot",
                CrossRigidActivationRewriteClass.LocalModelModifiers =>
                    "local model modifiers require preservation",
                _ => "unclassifiable activation topology",
            };

        private static IReadOnlyList<CrossRigidRewriteOccurrenceSet>
            BuildCrossRigidRewriteOccurrenceSets(
                CrossRigidVisualConfiguration configuration,
                IReadOnlyList<CrossRigidAnalysisInstanceComponent> group)
            => BuildCrossRigidRewriteOccurrenceSets(
                configuration,
                group
                    .Select(item => item.Instance)
                    .ToArray());

        private static IReadOnlyList<CrossRigidRewriteOccurrenceSet>
            BuildCrossRigidRewriteOccurrenceSets(
                CrossRigidVisualConfiguration configuration,
                IReadOnlyList<CrossRigidVisualInstance> sourceInstances)
            => BuildCrossRigidRewriteOccurrenceSets(
                configuration,
                BuildCrossRigidWsModelMultiplicity(
                    sourceInstances));

        private static IReadOnlyList<CrossRigidRewriteOccurrenceSet>
            BuildCrossRigidRewriteOccurrenceSets(
                CrossRigidVisualConfiguration configuration,
                IReadOnlyDictionary<string, int> expectedCounts)
        {
            // Merged on the fly, with the key that MergeCrossRigidRewriteOccurrenceSets would derive: the
            // instances are already ordered by their provenance signature, so the key is those signatures
            // joined (no second sort and no second signature pass per state).
            var merged = new Dictionary<string, CrossRigidRewriteOccurrenceSet>(StringComparer.Ordinal);
            var expectedTotal = 0;
            foreach (var count in expectedCounts.Values)
                expectedTotal += count;

            // A configuration can only yield a matching state when every
            // expected WSModel occurs in at least one of its states.
            var configurationPaths =
                GetCrossRigidConfigurationWsModelPaths(configuration);
            foreach (var expectedPath in expectedCounts.Keys)
            {
                if (!configurationPaths.Contains(expectedPath))
                    return Array.Empty<CrossRigidRewriteOccurrenceSet>();
            }

            foreach (var occurrenceSet in configuration.OccurrenceSets)
            {
                // Cheap pre-filter: the selected instances can only match the
                // expected multiplicities if their total matches. This avoids
                // allocating for the (vast majority of) non-matching states.
                var matching = 0;
                foreach (var instance in occurrenceSet.Instances)
                {
                    if (expectedCounts.ContainsKey(
                            GetCrossRigidInstanceNormalizedWsModelPath(
                                instance)))
                    {
                        matching++;
                    }
                }

                if (matching != expectedTotal)
                    continue;

                var selected = occurrenceSet.Instances
                    .Where(instance =>
                        expectedCounts.ContainsKey(
                            GetCrossRigidInstanceNormalizedWsModelPath(
                                instance)))
                    .ToArray();
                var actualCounts =
                    BuildCrossRigidWsModelMultiplicity(
                        selected);
                if (actualCounts.Count != expectedCounts.Count ||
                    expectedCounts.Any(entry =>
                        !actualCounts.TryGetValue(
                            entry.Key,
                            out var actualCount) ||
                        actualCount != entry.Value))
                {
                    continue;
                }

                if (occurrenceSet.Probability <= 0)
                    continue;

                var ordered = selected
                    .Select(instance => (
                        Signature: BuildCrossRigidInstanceProvenanceSignature(instance),
                        Instance: instance))
                    .OrderBy(item => item.Signature, StringComparer.Ordinal)
                    .ToArray();
                var key = string.Join("\u001e", ordered.Select(item => item.Signature));
                if (merged.TryGetValue(key, out var existing))
                {
                    merged[key] = existing with
                    {
                        Probability = existing.Probability + occurrenceSet.Probability,
                    };
                }
                else
                {
                    merged[key] = new CrossRigidRewriteOccurrenceSet(
                        occurrenceSet.Probability,
                        ordered.Select(item => item.Instance).ToArray());
                }
            }

            return merged
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => entry.Value)
                .ToArray();
        }

        private static CrossRigidRewriteOccurrenceSet[]
            MergeCrossRigidRewriteOccurrenceSets(
                IEnumerable<CrossRigidRewriteOccurrenceSet> occurrenceSets)
        {
            var merged = new Dictionary<
                string,
                CrossRigidRewriteOccurrenceSet>(
                StringComparer.Ordinal);
            foreach (var occurrenceSet in occurrenceSets)
            {
                if (occurrenceSet.Probability <= 0)
                    continue;

                var key =
                    BuildCrossRigidOccurrenceProvenanceSignature(
                        occurrenceSet.SourceInstances);
                if (merged.TryGetValue(key, out var existing))
                {
                    merged[key] = existing with
                    {
                        Probability =
                            existing.Probability +
                            occurrenceSet.Probability,
                    };
                }
                else
                {
                    merged[key] = occurrenceSet;
                }
            }

            return merged
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => entry.Value)
                .ToArray();
        }

        private static string BuildCrossRigidGeneratedPayloadKey(
            IReadOnlyList<CrossRigidVisualInstance> sourceInstances,
            IReadOnlyList<CrossRigidLodAnalysis> lodAnalyses)
            => string.Join(
                "\u001f",
                string.Join(
                    "\u001e",
                    sourceInstances
                        .Select(instance => instance.WsModelPath)
                        .OrderBy(
                            path => path,
                            StringComparer.OrdinalIgnoreCase)),
                BuildCrossRigidLodLayoutSignature(lodAnalyses));

        private static string BuildCrossRigidLodLayoutSignature(
            IReadOnlyList<CrossRigidLodAnalysis> lodAnalyses)
            => string.Join(
                "\u001e",
                lodAnalyses
                    .OrderBy(lod => lod.LodIndex)
                    .Select(lod =>
                        string.Join(
                            ":",
                            lod.LodIndex,
                            lod.ScenarioLodIndex,
                            lod.QualityLevel,
                            lod.CameraDistance.ToString(
                                "R",
                                CultureInfo.InvariantCulture),
                            lod.IsScenarioRepresentativeState ? 1 : 0,
                            string.Join(
                                ",",
                                lod.SourceLodSelections
                                    .OrderBy(
                                        selection => selection.WsModelPath,
                                        StringComparer.OrdinalIgnoreCase)
                                    .Select(selection =>
                                        $"{Normalize(selection.WsModelPath)}={selection.SourceLodIndex}")),
                            lod.OutputLayoutSignature)));

        private static CrossRigidSourceGeometryComponent
            BuildCrossRigidSourceGeometryComponent(
                CrossRigidAnalysisInstanceComponent item)
        {
            long vertices = 0;
            long geometryBytes = 0;
            var vertexFactory = VertexFactory.Create();
            foreach (var model in item.Component.Rigid.ModelList.SelectMany(
                         lod => lod))
            {
                vertices += model.Mesh.VertexList.Length;
                var vertexSize = vertexFactory.GetVertexSize(
                    model.Material.BinaryVertexFormat,
                    item.Component.Rigid.Header.Version);
                geometryBytes +=
                    checked((long)vertexSize *
                            model.Mesh.VertexList.Length);
                geometryBytes +=
                    checked((long)sizeof(ushort) *
                            model.Mesh.IndexList.Length);
            }

            return new CrossRigidSourceGeometryComponent(
                item.Component.WsModelPath,
                item.Component.RigidPath,
                vertices,
                geometryBytes);
        }

        private static void RecordCrossRigidWsModelOccurrenceProbabilities(
            BatchState state,
            string vmdPath,
            CrossRigidAttachmentLocalExpansionResult expansion)
        {
            foreach (var (attachmentIdentity, configurations) in
                     expansion.ConfigurationsByAttachment)
            {
                var wsModels = configurations
                    .SelectMany(configuration => configuration.Instances)
                    .Select(instance => instance.WsModelPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                foreach (var wsModelPath in wsModels)
                {
                    var occurrenceProbability = Math.Clamp(
                        configurations
                            .Where(configuration =>
                                configuration.Instances.Any(instance =>
                                    instance.WsModelPath.Equals(
                                        wsModelPath,
                                        StringComparison.OrdinalIgnoreCase)))
                            .Sum(configuration => configuration.Probability),
                        0.0,
                        1.0);
                    if (occurrenceProbability <= 0)
                        continue;

                    state.CrossRigidWsModelOccurrenceProbabilities[
                        new CrossRigidWsModelOccurrenceKey(
                            Normalize(vmdPath),
                            attachmentIdentity,
                            Normalize(wsModelPath))] =
                        occurrenceProbability;
                }
            }
        }

        private static void BuildCrossRigidGeneratedPayloadAnalysis(
            BatchState state)
        {
            var dependencyIndex = state.GameplayMeshDependencyIndex;
            var rewriteCoverage =
                BuildCrossRigidRewriteCoverageByOccurrence(state);
            var sourceGeometryByRigid =
                state.CrossRigidMergeAnalysisEntries
                    .SelectMany(entry => entry.SourceGeometry)
                    .GroupBy(
                        source => source.RigidPath,
                        StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.First(),
                        StringComparer.OrdinalIgnoreCase);

            var fullyDisplacedRigids =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (dependencyIndex != null)
            {
                foreach (var (rigidPath, sourceGeometry) in
                         sourceGeometryByRigid)
                {
                    if (!IsCrossRigidSourceRigidFullyCovered(
                            state,
                            dependencyIndex,
                            rigidPath,
                            rewriteCoverage,
                            out var wsConsumers))
                    {
                        continue;
                    }

                    var residentProbability =
                        GetExpectedArmyResidentProbability(
                            state,
                            wsConsumers,
                            Array.Empty<string>());
                    var expectedResidentBytes =
                        sourceGeometry.GeometryBytes *
                        residentProbability;
                    fullyDisplacedRigids.Add(rigidPath);
                    state.CrossRigidDisplacedSourceGeometryEntries.Add(
                        new CrossRigidDisplacedSourceGeometryEntry(
                            rigidPath,
                            sourceGeometry.GeometryBytes,
                            residentProbability,
                            expectedResidentBytes));
                }
            }

            var payloadGroups = state.CrossRigidMergeAnalysisEntries
                .GroupBy(
                    entry => entry.GeneratedPayloadId,
                    StringComparer.Ordinal)
                .Select(group => group.ToArray())
                .ToArray();
            var canonicalPayloadByDisplacedRigid =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
            foreach (var rigidPath in fullyDisplacedRigids)
            {
                var canonicalPayloadId = payloadGroups
                    .Where(plans =>
                        plans[0].SourceGeometry.Any(source =>
                            source.RigidPath.Equals(
                                rigidPath,
                                StringComparison.OrdinalIgnoreCase)))
                    .Select(plans => plans[0].GeneratedPayloadId)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (canonicalPayloadId != null)
                {
                    canonicalPayloadByDisplacedRigid[rigidPath] =
                        canonicalPayloadId;
                }
            }

            foreach (var plans in payloadGroups)
            {
                var representative = plans[0];
                var consumerDiscoveryComplete =
                    dependencyIndex != null &&
                    representative.SourceGeometry.All(source =>
                        !dependencyIndex.IsAssetAffectedByIncompleteRoot(
                            source.WsModelPath) &&
                        !dependencyIndex.IsAssetAffectedByIncompleteRoot(
                            source.RigidPath));
                var externalConsumers = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                long potentiallyDuplicatedGeometryBytes = 0;

                foreach (var source in representative.SourceGeometry)
                {
                    if (!fullyDisplacedRigids.Contains(source.RigidPath))
                    {
                        potentiallyDuplicatedGeometryBytes +=
                            source.GeometryBytes;
                    }

                    if (dependencyIndex == null)
                        continue;

                    if (dependencyIndex.WsModelConsumersByRigid.TryGetValue(
                            source.RigidPath,
                            out var wsConsumers))
                    {
                        foreach (var consumer in wsConsumers)
                        {
                            if (!IsCrossRigidWsModelFullyCovered(
                                    state,
                                    dependencyIndex,
                                    consumer,
                                    rewriteCoverage))
                            {
                                externalConsumers.Add(consumer);
                            }
                        }
                    }

                    if (dependencyIndex.DirectConsumersByRigid.TryGetValue(
                            source.RigidPath,
                            out var directConsumers))
                    {
                        externalConsumers.UnionWith(directConsumers);
                    }

                    if (dependencyIndex.IsAssetAffectedByIncompleteRoot(
                            source.RigidPath))
                    {
                        externalConsumers.UnionWith(
                            dependencyIndex.GetIncompleteRootsForAsset(
                                source.RigidPath));
                    }
                }

                var generatedVertices =
                    representative.Lods.Sum(
                        lod => lod.GeneratedVertexCount);
                var generatedGeometryBytes =
                    representative.Lods.Sum(
                        lod => lod.GeneratedGeometryBytes);
                var residentProbability =
                    CalculateExpectedCrossRigidPayloadResidentProbability(
                        state,
                        plans);
                var expectedResidentGeneratedBytes =
                    generatedGeometryBytes * residentProbability;
                var expectedResidentDisplacedBytes =
                    representative.SourceGeometry
                        .Select(source => source.RigidPath)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(rigidPath =>
                            canonicalPayloadByDisplacedRigid
                                .TryGetValue(
                                    rigidPath,
                                    out var payloadId) &&
                            payloadId.Equals(
                                representative.GeneratedPayloadId,
                                StringComparison.Ordinal))
                        .Sum(rigidPath =>
                            state.CrossRigidDisplacedSourceGeometryEntries
                                .First(entry =>
                                    entry.RigidPath.Equals(
                                        rigidPath,
                                        StringComparison.OrdinalIgnoreCase))
                                .ExpectedResidentGeometryBytes);

                if (!consumerDiscoveryComplete)
                {
                    RecordCrossRigidAnalysisDiagnostic(
                        state,
                        "Incomplete gameplay consumer discovery (non-blocking)",
                        $"payload {representative.GeneratedPayloadId}: " +
                        $"{string.Join(", ", representative.RigidPaths)}");
                }

                state.CrossRigidGeneratedPayloadAnalysisEntries.Add(
                    new CrossRigidGeneratedPayloadAnalysisEntry(
                        representative.GeneratedPayloadId,
                        plans.Length,
                        plans.Select(plan => plan.VmdPath)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count(),
                        representative.SourceInstances
                            .Select(instance => instance.WsModelPath)
                            .OrderBy(
                                path => path,
                                StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        representative.RigidPaths,
                        representative.Lods,
                        generatedVertices,
                        generatedGeometryBytes,
                        potentiallyDuplicatedGeometryBytes,
                        residentProbability,
                        expectedResidentGeneratedBytes,
                        expectedResidentDisplacedBytes,
                        externalConsumers
                            .OrderBy(
                                path => path,
                                StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        consumerDiscoveryComplete));
            }
        }
    }
}
