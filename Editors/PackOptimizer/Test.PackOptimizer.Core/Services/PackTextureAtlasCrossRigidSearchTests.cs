using System.Collections;
using System.Reflection;
using System.Xml;

namespace Test.PackOptimizer.Core
{
    public partial class PackTextureAtlasCrossRigidTests
    {
        private sealed record DrawCallSearchCandidate(
            string PayloadId,
            string VmdPath,
            string[] Attachments,
            int GeneratedDraws,
            int DrawsSaved,
            long GeometryBytes = 100_000);

        private const string DrawCallSearchWsPath =
            @"models\partial.wsmodel";
        private const string DrawCallSearchRigidPath =
            @"variantmeshes\draw_call_search.rigid_model_v2";
        private const string DrawCallSearchFallbackVmd =
            @"variantmeshes\variantmeshdefinitions\fallback.variantmeshdefinition";

        private static object CreateDrawCallPortfolioSearchState(
            IReadOnlyList<DrawCallSearchCandidate> candidates,
            int sourceMeshParts,
            params string[] incumbentPayloadIds)
        {
            var model = PackTextureAtlasValueGateTests
                .CreateSyntheticArmyResidencyModelForFallbackTest();
            var expectedByUnit = Get<IDictionary>(
                model,
                "ExpectedWsModelOccurrencesByUnit");
            var expectedOccurrences = expectedByUnit["unit:fallback-test"]!;
            var occurrencesByWsModel = Get<IDictionary>(
                expectedOccurrences,
                "ByWsModel");
            var occurrencesByRole =
                (IDictionary)occurrencesByWsModel[DrawCallSearchWsPath]!;
            foreach (DictionaryEntry entry in occurrencesByRole)
                occurrencesByRole[entry.Key] = 1.0;

            var candidateVmds = candidates
                .Select(candidate => candidate.VmdPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var vmd in candidateVmds)
            {
                if (!vmd.Equals(
                        DrawCallSearchFallbackVmd,
                        StringComparison.OrdinalIgnoreCase))
                {
                    AddSyntheticArmyVmdRoot(model, vmd);
                }
            }

            var index = Activator.CreateInstance(
                GetNestedType("GameplayMeshDependencyIndex"),
                AnyInstance,
                null,
                [true],
                null)!;
            var rootKind = GetNestedType("GameplayMeshDependencyRootKind");
            object Kind(string name) => Enum.Parse(rootKind, name);
            InvokeIndex(
                index,
                "RegisterRoot",
                DrawCallSearchWsPath,
                Kind("ResidencyAsset"));
            InvokeIndex(
                index,
                "AddRootReference",
                DrawCallSearchWsPath,
                DrawCallSearchWsPath);

            foreach (var vmd in candidateVmds)
            {
                InvokeIndex(
                    index,
                    "RegisterRoot",
                    vmd,
                    Kind("SourcePackVmd"));
                InvokeIndex(index, "AddRootReference", DrawCallSearchWsPath, vmd);
                InvokeIndex(index, "AddRootReference", DrawCallSearchRigidPath, vmd);
            }

            var state = CreateBareBatchState();
            SetStateProperty(state, "ArmyResidencyModel", model);
            SetStateProperty(state, "GameplayMeshDependencyIndex", index);
            var occurrenceProbabilities =
                Get<IDictionary>(state, "CrossRigidWsModelOccurrenceProbabilities");
            var addedOccurrenceKeys =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in candidates)
            {
                var instances = candidate.Attachments
                    .Select(attachment => CreateRecord(
                        "CrossRigidVisualInstance",
                        new Dictionary<string, object?>
                        {
                            ["WsModelPath"] = DrawCallSearchWsPath,
                            ["AttachmentIdentity"] = attachment,
                            ["DefiningVmdPath"] = candidate.VmdPath,
                            ["ModelXmlPath"] = $"models\\{attachment}.wsmodel",
                        }))
                    .ToArray();
                var sourceInstances = CreateRecordArray(
                    "CrossRigidVisualInstance",
                    instances);
                var occurrenceSets = candidate.Attachments
                    .Select((attachment, index) => CreateRecord(
                        "CrossRigidRewriteOccurrenceSet",
                        new Dictionary<string, object?>
                        {
                            ["Probability"] = 1.0,
                            ["SourceInstances"] = CreateRecordArray(
                                "CrossRigidVisualInstance",
                                instances[index]),
                        }))
                    .ToArray();
                var sourceGeometry = CreateRecord(
                    "CrossRigidSourceGeometryComponent",
                    new Dictionary<string, object?>
                    {
                        ["WsModelPath"] = DrawCallSearchWsPath,
                        ["RigidPath"] = DrawCallSearchRigidPath,
                        ["VertexCount"] = 1L,
                        ["GeometryBytes"] = 1L,
                        ["MeshPartCounts"] = new[] { sourceMeshParts },
                    });
                var lod = CreateRecord(
                    "CrossRigidLodAnalysis",
                    new Dictionary<string, object?>
                    {
                        ["LodIndex"] = 0,
                        ["ScenarioLodIndex"] = 0,
                        ["IsScenarioRepresentativeState"] = true,
                        ["OutputDrawsAfterCrossRigidMerge"] =
                            candidate.GeneratedDraws,
                        ["DrawsSaved"] = candidate.DrawsSaved,
                        ["LodProbability"] = 1.0,
                    });
                var lods = CreateRecordArray("CrossRigidLodAnalysis", lod);
                var plan = CreateRecord(
                    "CrossRigidMergeAnalysisEntry",
                    new Dictionary<string, object?>
                    {
                        ["RewritePlanId"] = "plan:" + candidate.PayloadId,
                        ["GeneratedPayloadId"] = candidate.PayloadId,
                        ["VmdPath"] = candidate.VmdPath,
                        ["AggregatedConfigurationProbability"] = 1.0,
                        ["ContributingConfigurationCount"] = 1,
                        ["AttachmentIdentity"] =
                            candidate.Attachments.FirstOrDefault() ?? string.Empty,
                        ["SourceInstances"] = sourceInstances,
                        ["RewriteOccurrenceSets"] = CreateRecordArray(
                            "CrossRigidRewriteOccurrenceSet",
                            occurrenceSets),
                        ["SourceGeometry"] = CreateRecordArray(
                            "CrossRigidSourceGeometryComponent",
                            sourceGeometry),
                        ["RigidPaths"] = new[] { DrawCallSearchRigidPath },
                        ["Lods"] = lods,
                        ["ExpectedArmyDrawCallsEliminated"] =
                            candidate.DrawsSaved * candidate.Attachments.Length,
                    });
                AddToList(state, "CrossRigidMergeAnalysisEntries", plan);

                AddToList(
                    state,
                    "CrossRigidGeneratedPayloadAnalysisEntries",
                    CreateRecord(
                        "CrossRigidGeneratedPayloadAnalysisEntry",
                        new Dictionary<string, object?>
                        {
                            ["GeneratedPayloadId"] = candidate.PayloadId,
                            ["RewritePlanCount"] = 1,
                            ["VmdCount"] = 1,
                            ["SourceWsModelPaths"] =
                                new[] { DrawCallSearchWsPath },
                            ["RigidPaths"] = new[] { DrawCallSearchRigidPath },
                            ["Lods"] = lods,
                            ["GeneratedVertexCount"] = 1L,
                            ["GeneratedGeometryBytes"] =
                                candidate.GeometryBytes,
                            ["ExpectedBattleResidentProbability"] = 1.0,
                            ["ExpectedResidentGeneratedGeometryBytes"] =
                                (double)candidate.GeometryBytes,
                        }));
                AddToSet(
                    state,
                    "CrossRigidLegacyEligiblePayloadIds",
                    candidate.PayloadId);

                foreach (var attachment in candidate.Attachments)
                {
                    var occurrenceIdentity =
                        candidate.VmdPath + "\u001f" + attachment;
                    if (!addedOccurrenceKeys.Add(occurrenceIdentity))
                        continue;

                    var key = CreateRecord(
                        "CrossRigidWsModelOccurrenceKey",
                        new Dictionary<string, object?>
                        {
                            ["VmdPath"] = candidate.VmdPath,
                            ["AttachmentIdentity"] = attachment,
                            ["WsModelPath"] = DrawCallSearchWsPath,
                        });
                    occurrenceProbabilities.Add(key, 1.0);
                }
            }

            InstallDrawCallPortfolioSearchContext(state, candidates);
            SetDrawCallSearchIncumbent(state, incumbentPayloadIds);
            return state;
        }

        private static void InstallDrawCallPortfolioSearchContext(
            object state,
            IReadOnlyList<DrawCallSearchCandidate> candidates)
        {
            var candidateEntryType =
                GetNestedType("CrossRigidPayloadSelectionEntry");
            var decisionType =
                GetNestedType("CrossRigidPayloadSelectionDecision");
            var orderedCandidates =
                Array.CreateInstance(candidateEntryType, candidates.Count);
            var profileType =
                GetNestedType("CrossRigidPayloadRewriteConflictProfile");
            var conflictOccurrenceType =
                GetNestedType("CrossRigidRewriteConflictOccurrence");
            var profileMapType = typeof(Dictionary<,>).MakeGenericType(
                typeof(string),
                profileType);
            var conflictProfiles = (IDictionary)Activator.CreateInstance(
                profileMapType)!;
            var planType = GetNestedType("CrossRigidMergeAnalysisEntry");
            var planArrayType = planType.MakeArrayType();
            var planMapType = typeof(Dictionary<,>).MakeGenericType(
                typeof(string),
                planArrayType);
            var primaryPlansByPayload =
                (IDictionary)Activator.CreateInstance(planMapType)!;
            var plansByPayload =
                (IDictionary)Activator.CreateInstance(planMapType)!;
            var allPlans = Get<IList>(
                    state,
                    "CrossRigidMergeAnalysisEntries")
                .Cast<object>()
                .ToArray();

            for (var candidateIndex = 0;
                 candidateIndex < candidates.Count;
                 candidateIndex++)
            {
                var candidate = candidates[candidateIndex];
                var payloadPlans = allPlans
                    .Where(plan => Get<string>(
                        plan,
                        "GeneratedPayloadId").Equals(
                        candidate.PayloadId,
                        StringComparison.Ordinal))
                    .ToArray();
                var typedPlans = Array.CreateInstance(
                    planType,
                    payloadPlans.Length);
                for (var planIndex = 0;
                     planIndex < payloadPlans.Length;
                     planIndex++)
                {
                    typedPlans.SetValue(payloadPlans[planIndex], planIndex);
                }

                primaryPlansByPayload.Add(candidate.PayloadId, typedPlans);
                plansByPayload.Add(candidate.PayloadId, typedPlans);

                var expectedDraws =
                    candidate.DrawsSaved * candidate.Attachments.Length;
                var entry = CreateRecord(
                    "CrossRigidPayloadSelectionEntry",
                    new Dictionary<string, object?>
                    {
                        ["GeneratedPayloadId"] = candidate.PayloadId,
                        ["Decision"] = Enum.Parse(decisionType, "Accept"),
                        ["ExpectedArmyDrawCallsEliminated"] = expectedDraws,
                        ["MarginalExpectedArmyDrawCallsEliminated"] =
                            expectedDraws,
                        ["MaxRawDrawsSavedPerState"] = candidate.DrawsSaved,
                        ["RewritePlanCount"] = payloadPlans.Length,
                        ["VmdCount"] = payloadPlans
                            .Select(plan => Get<string>(plan, "VmdPath"))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count(),
                        ["GeneratedGeometryBytes"] = candidate.GeometryBytes,
                        ["ExpectedResidentGeneratedGeometryBytes"] =
                            (double)candidate.GeometryBytes,
                        ["PhysicalBytesPerExpectedDraw"] =
                            candidate.GeometryBytes / Math.Max(1.0, expectedDraws),
                        ["ResidentBytesPerExpectedDraw"] =
                            candidate.GeometryBytes / Math.Max(1.0, expectedDraws),
                        ["SelectionRank"] = candidateIndex + 1,
                    });
                AddToList(state, "CrossRigidPayloadSelectionEntries", entry);
                orderedCandidates.SetValue(entry, candidateIndex);

                var occurrences = new List<object>();
                foreach (var plan in payloadPlans)
                {
                    foreach (var occurrenceSet in Get<IList>(
                                 plan,
                                 "RewriteOccurrenceSets")
                             .Cast<object>())
                    {
                        var rewriteOccurrenceKey =
                            (string)GetStaticMethod(
                                    "BuildCrossRigidSelectedRewriteOccurrenceKey")
                                .Invoke(null, [plan, occurrenceSet])!;
                        var conflictKeys =
                            new HashSet<string>(StringComparer.Ordinal);
                        foreach (var instance in Get<IList>(
                                     occurrenceSet,
                                     "SourceInstances")
                                 .Cast<object>())
                        {
                            var consumedOccurrenceKey =
                                (string)GetStaticMethod(
                                        "BuildCrossRigidConsumedOccurrenceKey")
                                    .Invoke(null, [
                                        candidate.VmdPath,
                                        instance,
                                    ])!;
                            conflictKeys.Add(string.Join(
                                "\u001f",
                                "exact",
                                consumedOccurrenceKey));
                        }

                        occurrences.Add(CreateRecord(
                            "CrossRigidRewriteConflictOccurrence",
                            new Dictionary<string, object?>
                            {
                                ["SelectionKey"] = rewriteOccurrenceKey,
                                ["RewriteOccurrenceKey"] =
                                    rewriteOccurrenceKey,
                                ["JointCombinationKey"] = null,
                                ["ExpectedArmyDrawCallsEliminated"] =
                                    candidate.DrawsSaved *
                                    Get<double>(occurrenceSet, "Probability"),
                                ["RewriteConflictKeys"] = conflictKeys,
                                ["JointContextKeyByParentLocation"] =
                                    new Dictionary<string, string>(
                                        StringComparer.Ordinal),
                            }));
                    }
                }

                conflictProfiles.Add(
                    candidate.PayloadId,
                    CreateRecord(
                        "CrossRigidPayloadRewriteConflictProfile",
                        new Dictionary<string, object?>
                        {
                            ["Occurrences"] = CreateRecordArray(
                                "CrossRigidRewriteConflictOccurrence",
                                occurrences.ToArray()),
                        }));
            }

            var searchContext = CreateRecord(
                "CrossRigidDrawCallPortfolioSearchContext",
                new Dictionary<string, object?>
                {
                    ["OrderedCandidates"] = orderedCandidates,
                    ["ConflictProfiles"] = conflictProfiles,
                    ["InvalidProfiles"] =
                        new HashSet<string>(StringComparer.Ordinal),
                    ["SourceVmdDocuments"] =
                        new Dictionary<string, XmlDocument>(
                            StringComparer.OrdinalIgnoreCase),
                    ["PrimaryPlansByPayload"] = primaryPlansByPayload,
                    ["PlansByPayload"] = plansByPayload,
                });
            SetStateProperty(
                state,
                "CrossRigidDrawCallPortfolioSearchContext",
                searchContext);
        }

        private static void SetDrawCallSearchIncumbent(
            object state,
            IReadOnlyCollection<string> selectedPayloadIds)
        {
            var selectedIds =
                Get<HashSet<string>>(state, "CrossRigidSelectedPayloadIds");
            selectedIds.Clear();
            selectedIds.UnionWith(selectedPayloadIds);

            var selectedOccurrenceKeys =
                Get<HashSet<string>>(
                    state,
                    "CrossRigidSelectedRewriteOccurrenceKeys");
            selectedOccurrenceKeys.Clear();
            Get<Dictionary<string, HashSet<string>>>(
                state,
                "CrossRigidSelectedJointCombinationKeysByRewriteOccurrenceKey")
                .Clear();
            Get<Dictionary<string, double>>(
                state,
                "CrossRigidSelectedJointCoverageFractionByRewriteOccurrenceKey")
                .Clear();
            Get<HashSet<string>>(
                state,
                "CrossRigidSharedPayloadFallbackAdmittedPlanIds")
                .Clear();

            var selected = selectedPayloadIds.ToHashSet(StringComparer.Ordinal);
            var entries = Get<IList>(
                state,
                "CrossRigidPayloadSelectionEntries");
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index]!;
                var payloadId = Get<string>(entry, "GeneratedPayloadId");
                entries[index] = RecreateSelectionEntry(
                    entry,
                    selected.Contains(payloadId)
                        ? "Accept"
                        : "RewriteOccurrenceConflict",
                    selected.Contains(payloadId)
                        ? Get<double>(
                            entry,
                            "ExpectedArmyDrawCallsEliminated")
                        : Get<double>(
                            entry,
                            "MarginalExpectedArmyDrawCallsEliminated"));
            }

            foreach (var plan in Get<IList>(
                         state,
                         "CrossRigidMergeAnalysisEntries")
                     .Cast<object>())
            {
                if (!selected.Contains(
                        Get<string>(plan, "GeneratedPayloadId")))
                {
                    continue;
                }

                foreach (var occurrenceSet in Get<IList>(
                             plan,
                             "RewriteOccurrenceSets")
                         .Cast<object>())
                {
                    selectedOccurrenceKeys.Add(
                        (string)GetStaticMethod(
                                "BuildCrossRigidSelectedRewriteOccurrenceKey")
                            .Invoke(null, [plan, occurrenceSet])!);
                }
            }
        }

        private static object RecreateSelectionEntry(
            object entry,
            string decision,
            double marginalDraws)
        {
            var entryType = GetNestedType("CrossRigidPayloadSelectionEntry");
            var constructor = entryType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public)
                .Single();
            var decisionType =
                GetNestedType("CrossRigidPayloadSelectionDecision");
            var values = new Dictionary<string, object?>();
            foreach (var parameter in constructor.GetParameters())
            {
                if (parameter.Name == "Decision")
                {
                    values[parameter.Name!] = Enum.Parse(decisionType, decision);
                }
                else if (parameter.Name == "MarginalExpectedArmyDrawCallsEliminated")
                {
                    values[parameter.Name!] = marginalDraws;
                }
                else
                {
                    values[parameter.Name!] = entryType
                        .GetProperty(parameter.Name!)!
                        .GetValue(entry);
                }
            }

            return constructor.Invoke(constructor.GetParameters()
                .Select(parameter => values[parameter.Name!])
                .ToArray());
        }

        private static double EvaluateDrawCallDeltaForSelection(
            object state,
            params string[] selectedPayloadIds)
            => Get<double>(
                EvaluateDrawCallForSelection(state, selectedPayloadIds),
                "Delta");

        private static object EvaluateDrawCallForSelection(
            object state,
            params string[] selectedPayloadIds)
        {
            SetDrawCallSearchIncumbent(state, selectedPayloadIds);
            var context = GetStaticMethod("BuildRetirementContext")
                .Invoke(null, [state])!;
            var selected = selectedPayloadIds.ToHashSet(StringComparer.Ordinal);
            var terms = GetStaticMethod(
                    "BuildCrossRigidExpectedDrawCallTerms")
                .Invoke(null, [state, selected, context, null])!;
            var totals = GetStaticMethod(
                    "EvaluateCrossRigidDrawCallTotals")
                .Invoke(null, [state, selected, context, terms, null])!;
            return totals;
        }

        private static object RunDrawCallPortfolioSearch(
            object state,
            double limit)
            => RunExpectedDrawCallGate(state, limit);

        private static object GetDrawCallSearchReport(object gateReport)
        {
            var property = gateReport.GetType().GetProperty(
                "PortfolioSearch",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property?.GetValue(gateReport)
                ?? throw new InvalidOperationException(
                    "The gate report did not expose its portfolio-search report.");
        }

        [Test]
        public void DrawCallPortfolioSearch_AddsUnselectedPayloadsAsSourceRetirementCohort()
        {
            const string first = "payload:cohort-a";
            const string second = "payload:cohort-b";
            var state = CreateDrawCallPortfolioSearchState(
                [
                    new DrawCallSearchCandidate(
                        first,
                        DrawCallSearchFallbackVmd,
                        ["part-1", "part-2"],
                        GeneratedDraws: 1,
                        DrawsSaved: 1),
                    new DrawCallSearchCandidate(
                        second,
                        DrawCallSearchFallbackVmd,
                        ["part-3", "part-4"],
                        GeneratedDraws: 1,
                        DrawsSaved: 1),
                ],
                2,
                first);

            var firstOnly = EvaluateDrawCallForSelection(state, first);
            Assert.That(
                Get<double>(firstOnly, "Delta"),
                Is.GreaterThan(0),
                $"before={Get<double>(firstOnly, "Before")}, after={Get<double>(firstOnly, "After")}; " +
                "the first payload cannot retire the rigid while the second source occurrence group is uncovered.");
            Assert.That(
                EvaluateDrawCallDeltaForSelection(state, second),
                Is.GreaterThan(0),
                "The second payload cannot retire the rigid while the first VMD is uncovered.");
            Assert.That(
                EvaluateDrawCallDeltaForSelection(state, first, second),
                Is.LessThanOrEqualTo(0),
                "The two disjoint payloads together retire the source rigid.");
            SetDrawCallSearchIncumbent(state, new[] { first });

            var report = RunDrawCallPortfolioSearch(state, limit: 0);
            Assert.That(
                Get<HashSet<string>>(state, "CrossRigidSelectedPayloadIds"),
                Is.EquivalentTo(new[] { first, second }),
                $"trials={Get<int>(GetDrawCallSearchReport(report), "Trials")}, " +
                $"cohorts={Get<int>(GetDrawCallSearchReport(report), "CohortTrials")}, " +
                $"candidates={Get<int>(GetDrawCallSearchReport(report), "CandidateCount")}, " +
                $"incumbent={Get<double>(GetDrawCallSearchReport(report), "IncumbentObjective")}, " +
                $"best={Get<double>(GetDrawCallSearchReport(report), "BestObjective")}, " +
                $"bestDelta={Get<double>(GetDrawCallSearchReport(report), "BestDelta")}");
            Assert.That(
                Get<double>(Get<object>(report, "Evaluation"), "Delta"),
                Is.EqualTo(0).Within(1e-9));
            Assert.That(
                Get<double>(
                    GetDrawCallSearchReport(report),
                    "BestObjective"),
                Is.GreaterThan(
                    Get<double>(
                        GetDrawCallSearchReport(report),
                        "IncumbentObjective")));
        }

        [Test]
        public void DrawCallPortfolioSearch_ReplacesConflictingPayloadWithFeasibleAlternative()
        {
            const string incumbent = "payload:replacement-incumbent";
            const string replacement = "payload:replacement-alternative";
            var state = CreateDrawCallPortfolioSearchState(
                [
                    new DrawCallSearchCandidate(
                        incumbent,
                        DrawCallSearchFallbackVmd,
                        ["shared"],
                        GeneratedDraws: 2,
                        DrawsSaved: 1),
                    new DrawCallSearchCandidate(
                        replacement,
                        DrawCallSearchFallbackVmd,
                        ["shared", "extra-1", "extra-2"],
                        GeneratedDraws: 1,
                        DrawsSaved: 1),
                ],
                1,
                incumbent);

            Assert.That(
                EvaluateDrawCallDeltaForSelection(state, incumbent),
                Is.GreaterThan(0),
                "The incumbent covers only one of the source occurrences.");
            Assert.That(
                EvaluateDrawCallDeltaForSelection(state, replacement),
                Is.EqualTo(0).Within(1e-9),
                "The alternative covers all source occurrences and retires the rigid.");
            SetDrawCallSearchIncumbent(state, new[] { incumbent });

            var report = RunDrawCallPortfolioSearch(state, limit: 0);
            Assert.That(
                Get<HashSet<string>>(state, "CrossRigidSelectedPayloadIds"),
                Is.EquivalentTo(new[] { replacement }));
            Assert.That(
                Get<double>(Get<object>(report, "Evaluation"), "Delta"),
                Is.EqualTo(0).Within(1e-9));
        }

        [Test]
        public void DrawCallPortfolioSearch_NeverRegressesFeasibleIncumbentAndAlwaysEnforcesModelLimit()
        {
            const string incumbent = "payload:limit-incumbent";
            const string infeasible = "payload:limit-infeasible";
            var state = CreateDrawCallPortfolioSearchState(
                [
                    new DrawCallSearchCandidate(
                        incumbent,
                        DrawCallSearchFallbackVmd,
                        ["part-1", "part-2"],
                        GeneratedDraws: 2,
                        DrawsSaved: 1),
                    new DrawCallSearchCandidate(
                        infeasible,
                        DrawCallSearchFallbackVmd,
                        ["part-1", "part-2"],
                        GeneratedDraws: 3,
                        DrawsSaved: 10),
                ],
                2,
                incumbent);

            Assert.That(
                EvaluateDrawCallDeltaForSelection(state, incumbent),
                Is.EqualTo(0).Within(1e-9));
            Assert.That(
                EvaluateDrawCallDeltaForSelection(state, infeasible),
                Is.GreaterThan(0),
                "The larger modeled instance benefit must not override the draw-call limit.");
            SetDrawCallSearchIncumbent(state, new[] { incumbent });

            var report = RunDrawCallPortfolioSearch(state, limit: 0);
            Assert.That(
                Get<HashSet<string>>(state, "CrossRigidSelectedPayloadIds"),
                Is.EquivalentTo(new[] { incumbent }));
            Assert.That(
                Get<double>(Get<object>(report, "Evaluation"), "Delta"),
                Is.LessThanOrEqualTo(1e-9));
            Assert.That(
                Get<double>(
                    GetDrawCallSearchReport(report),
                    "BestObjective"),
                Is.GreaterThanOrEqualTo(
                    Get<double>(
                        GetDrawCallSearchReport(report),
                        "IncumbentObjective")));
        }

        [Test]
        public void DrawCallPortfolioSearch_InfinitySkipsSearchAndPreservesSelection()
        {
            const string incumbent = "payload:infinite-incumbent";
            const string candidate = "payload:infinite-candidate";
            var state = CreateDrawCallPortfolioSearchState(
                [
                    new DrawCallSearchCandidate(
                        incumbent,
                        DrawCallSearchFallbackVmd,
                        ["one", "two"],
                        GeneratedDraws: 1,
                        DrawsSaved: 1),
                    new DrawCallSearchCandidate(
                        candidate,
                        DrawCallSearchFallbackVmd,
                        ["one", "two", "three"],
                        GeneratedDraws: 1,
                        DrawsSaved: 1),
                ],
                1,
                incumbent);
            var payloadsBefore = Get<HashSet<string>>(
                    state,
                    "CrossRigidSelectedPayloadIds")
                .ToHashSet(StringComparer.Ordinal);
            var occurrencesBefore = Get<HashSet<string>>(
                    state,
                    "CrossRigidSelectedRewriteOccurrenceKeys")
                .ToHashSet(StringComparer.Ordinal);

            var report = RunDrawCallPortfolioSearch(
                state,
                double.PositiveInfinity);
            Assert.That(
                Get<HashSet<string>>(state, "CrossRigidSelectedPayloadIds"),
                Is.EquivalentTo(payloadsBefore));
            Assert.That(
                Get<HashSet<string>>(
                    state,
                    "CrossRigidSelectedRewriteOccurrenceKeys"),
                Is.EquivalentTo(occurrencesBefore));
            Assert.That(
                Get<int>(GetDrawCallSearchReport(report), "Trials"),
                Is.EqualTo(0));
        }

        [Test]
        public void DrawCallPortfolioSearch_IsDeterministicAndRespectsTrialBudget()
        {
            var candidates = Enumerable.Range(0, 70)
                .Select(index => new DrawCallSearchCandidate(
                    $"payload:bounded-{index:D2}",
                    DrawCallSearchFallbackVmd,
                    ["same-occurrence"],
                    GeneratedDraws: 1,
                    DrawsSaved: 1))
                .ToArray();
            var state = CreateDrawCallPortfolioSearchState(
                candidates,
                1,
                "payload:bounded-00");

            var report = RunDrawCallPortfolioSearch(state, limit: 0);
            var search = GetDrawCallSearchReport(report);
            var trials = Get<int>(search, "Trials");
            var budget = Get<int>(search, "TrialBudget");
            Assert.That(trials, Is.LessThanOrEqualTo(budget));
            Assert.That(Get<bool>(search, "BudgetExhausted"), Is.True);
            Assert.That(Get<int>(search, "CandidateCount"), Is.GreaterThan(trials));
            Assert.That(
                Get<HashSet<string>>(state, "CrossRigidSelectedPayloadIds"),
                Has.Count.EqualTo(1),
                "A feasible portfolio remains selected after the bounded search.");

            var repeatedState = CreateDrawCallPortfolioSearchState(
                candidates,
                1,
                "payload:bounded-00");
            var repeatedReport = RunDrawCallPortfolioSearch(
                repeatedState,
                limit: 0);
            var repeatedSearch = GetDrawCallSearchReport(repeatedReport);
            Assert.That(
                Get<int>(repeatedSearch, "Trials"),
                Is.EqualTo(trials));
            Assert.That(
                Get<double>(repeatedSearch, "BestObjective"),
                Is.EqualTo(Get<double>(search, "BestObjective")));
            Assert.That(
                Get<HashSet<string>>(
                    repeatedState,
                    "CrossRigidSelectedPayloadIds"),
                Is.EquivalentTo(
                    Get<HashSet<string>>(
                        state,
                        "CrossRigidSelectedPayloadIds")));
        }
    }
}
