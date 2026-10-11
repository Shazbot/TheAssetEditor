using System.Collections;
using System.Reflection;
using System.Xml;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.LodHeader;

namespace Test.KitbashEditor.Services
{
    public class PackTextureAtlasCrossRigidTests
    {
        private const string ComponentA = "variantmeshes\\a.wsmodel";
        private const string ComponentB = "variantmeshes\\b.wsmodel";

        private static Type GetServiceType()
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            return assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
        }

        private static Type GetNestedType(string name)
        {
            return GetServiceType().GetNestedType(
                       name,
                       BindingFlags.NonPublic)
                   ?? throw new InvalidOperationException(
                       $"{name} was not found.");
        }

        private static MethodInfo GetStaticMethod(string name)
        {
            return GetServiceType().GetMethod(
                       name,
                       BindingFlags.NonPublic | BindingFlags.Static)
                   ?? throw new InvalidOperationException(
                       $"PackTextureAtlasBatchService.{name} was not found.");
        }

        private static RmvFile CreateRigid(
            int modelListLength,
            params float[] lodDistances)
        {
            var headers = lodDistances
                .Select(distance =>
                    (RmvLodHeader)new Rmv2LodHeader_V7_V8
                    {
                        _lodCameraDistance = distance,
                    })
                .ToArray();
            return new RmvFile
            {
                LodHeaders = headers,
                ModelList = Enumerable
                    .Range(0, modelListLength)
                    .Select(_ => Array.Empty<RmvModel>())
                    .ToArray(),
            };
        }

        private static RmvFile CreateRigid(params float[] lodDistances)
        {
            return CreateRigid(lodDistances.Length, lodDistances);
        }

        private static object CreateComponent(
            string wsModelPath,
            RmvFile rigid)
        {
            var constructor = GetNestedType("CrossRigidAnalysisComponent")
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                .Single();
            return constructor.Invoke(
                [
                    "models\\test.variantmeshdefinition",
                    wsModelPath,
                    wsModelPath + ".rigid_model_v2",
                    new XmlDocument(),
                    rigid,
                    Array.Empty<string[]>(),
                    string.Empty,
                    false,
                    false,
                ]);
        }

        private static Array CreateComponents(params object[] components)
        {
            var array = Array.CreateInstance(
                GetNestedType("CrossRigidAnalysisComponent"),
                components.Length);
            for (var index = 0; index < components.Length; index++)
                array.SetValue(components[index], index);
            return array;
        }

        private sealed record ScheduleEntry(
            float CameraDistance,
            Dictionary<string, int> SourceLodByComponent);

        private static List<ScheduleEntry> BuildSchedule(
            params (string Path, float[] Distances)[] sources)
        {
            var components = CreateComponents(
                sources
                    .Select(source =>
                        CreateComponent(
                            source.Path,
                            CreateRigid(source.Distances)))
                    .ToArray());
            var schedule = (IEnumerable)(GetStaticMethod(
                        "BuildBoundedCrossRigidUnionLodSchedule")
                    .Invoke(null, [components])
                ?? throw new InvalidOperationException(
                    "BuildBoundedCrossRigidUnionLodSchedule returned null."));

            var result = new List<ScheduleEntry>();
            foreach (var entry in schedule)
            {
                var entryType = entry.GetType();
                var cameraDistance = (float)entryType
                    .GetProperty("CameraDistance")!
                    .GetValue(entry)!;
                var selections = new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var selection in (IEnumerable)entryType
                             .GetProperty("SourceLodSelections")!
                             .GetValue(entry)!)
                {
                    var selectionType = selection.GetType();
                    selections.Add(
                        (string)selectionType
                            .GetProperty("WsModelPath")!
                            .GetValue(selection)!,
                        (int)selectionType
                            .GetProperty("SourceLodIndex")!
                            .GetValue(selection)!);
                }

                result.Add(new ScheduleEntry(cameraDistance, selections));
            }

            return result;
        }

        private static int FindSourceLodAtDistance(
            RmvFile rigid,
            float distance)
        {
            return (int)(GetStaticMethod("FindCrossRigidSourceLodAtDistance")
                .Invoke(null, [rigid, distance])
                ?? throw new InvalidOperationException(
                    "FindCrossRigidSourceLodAtDistance returned null."));
        }

        // Hand-worked: all rigids have empty LODs, so every boundary weighs
        // 1. Union boundaries are 10,20,30,100,200,300,400,10000 and the
        // generated LOD count is capped at max(4, 4) = 4. The final boundary
        // is kept; the remaining 3 slots are picked at weighted centers
        // 7 * (1/6, 3/6, 5/6) = 1.17, 3.5, 5.83 against boundary centers
        // 0.5, 1.5, ..., 6.5, which selects 20, 100 and 300.
        // Samples are just past the near edge of each interval.
        [Test]
        public void UnionSchedule_DifferentProfiles_KeepsEveryComponentInEveryLod()
        {
            var schedule = BuildSchedule(
                (ComponentA, [100f, 200f, 300f, 400f]),
                (ComponentB, [10f, 20f, 30f, 10000f]));

            Assert.That(
                schedule.Select(entry => entry.CameraDistance),
                Is.EqualTo(new[] { 20f, 100f, 300f, 10000f }));
            Assert.That(
                schedule.Select(entry => entry.SourceLodByComponent.Count),
                Is.All.EqualTo(2));
            Assert.That(
                schedule.Select(entry =>
                    entry.SourceLodByComponent[ComponentA]),
                Is.EqualTo(new[] { 0, 0, 1, 3 }));
            Assert.That(
                schedule.Select(entry =>
                    entry.SourceLodByComponent[ComponentB]),
                Is.EqualTo(new[] { 0, 2, 3, 3 }));
        }

        // Hand-worked: A=[10,20] and B=[100,200,300,400]. Union boundaries
        // 10,20,100,200,300,400 are bounded to 4. The pool without the final
        // boundary has 5 entries with centers 0.5..4.5; targets
        // 5 * (1/6, 3/6, 5/6) = 0.83, 2.5, 4.17 select 10, 100 and 300, so
        // the schedule is 10,100,300,400. A's last LOD distance (20) is
        // below the samples of the last two intervals, so A has to stay at
        // its last LOD instead of being dropped.
        [Test]
        public void UnionSchedule_SourceEndsBeforeSample_UsesLastLodInsteadOfDropping()
        {
            var schedule = BuildSchedule(
                (ComponentA, [10f, 20f]),
                (ComponentB, [100f, 200f, 300f, 400f]));

            Assert.That(
                schedule.Select(entry => entry.CameraDistance),
                Is.EqualTo(new[] { 10f, 100f, 300f, 400f }));
            Assert.That(
                schedule.Select(entry => entry.SourceLodByComponent.Count),
                Is.All.EqualTo(2));
            Assert.That(
                schedule.Select(entry =>
                    entry.SourceLodByComponent[ComponentA]),
                Is.EqualTo(new[] { 0, 1, 1, 1 }));
            Assert.That(
                schedule.Select(entry =>
                    entry.SourceLodByComponent[ComponentB]),
                Is.EqualTo(new[] { 0, 0, 1, 3 }));
        }

        // A=[100,200], B=[50,100,200]. All 3 union boundaries fit, so the
        // schedule is 50,100,200. Interval 50..100 starts exactly where B
        // leaves LOD 0 and A is still in LOD 0; interval 100..200 starts
        // exactly where A and B both leave their current LOD.
        [Test]
        public void UnionSchedule_SourceSwitchesExactlyAtGeneratedBoundary_SelectsLodStartingAtEdge()
        {
            var schedule = BuildSchedule(
                (ComponentA, [100f, 200f]),
                (ComponentB, [50f, 100f, 200f]));

            Assert.That(
                schedule.Select(entry => entry.CameraDistance),
                Is.EqualTo(new[] { 50f, 100f, 200f }));
            Assert.That(
                schedule.Select(entry =>
                    entry.SourceLodByComponent[ComponentA]),
                Is.EqualTo(new[] { 0, 0, 1 }));
            Assert.That(
                schedule.Select(entry =>
                    entry.SourceLodByComponent[ComponentB]),
                Is.EqualTo(new[] { 0, 1, 2 }));
        }

        [Test]
        public void UnionSchedule_IdenticalBoundaries_SwitchesBothSourcesAtTheBoundary()
        {
            var schedule = BuildSchedule(
                (ComponentA, [100f, 200f]),
                (ComponentB, [100f, 200f]));

            Assert.That(
                schedule.Select(entry => entry.CameraDistance),
                Is.EqualTo(new[] { 100f, 200f }));
            Assert.That(
                schedule.Select(entry =>
                    entry.SourceLodByComponent[ComponentA]),
                Is.EqualTo(new[] { 0, 1 }));
            Assert.That(
                schedule.Select(entry =>
                    entry.SourceLodByComponent[ComponentB]),
                Is.EqualTo(new[] { 0, 1 }));
        }

        [Test]
        public void UnionSchedule_FirstInterval_UsesMostDetailedSourceLod()
        {
            var schedule = BuildSchedule(
                (ComponentA, [5f, 50f]),
                (ComponentB, [1f, 2f, 3f]));

            Assert.That(
                schedule[0].SourceLodByComponent[ComponentA],
                Is.EqualTo(0));
            Assert.That(
                schedule[0].SourceLodByComponent[ComponentB],
                Is.EqualTo(0));
        }

        [Test]
        public void FindSourceLodAtDistance_BeyondLastLod_ReturnsLastLod()
        {
            var rigid = CreateRigid(100f, 200f, 300f, 400f);

            Assert.That(FindSourceLodAtDistance(rigid, 0f), Is.EqualTo(0));
            Assert.That(FindSourceLodAtDistance(rigid, 100f), Is.EqualTo(0));
            Assert.That(
                FindSourceLodAtDistance(rigid, 100.02f),
                Is.EqualTo(1));
            Assert.That(FindSourceLodAtDistance(rigid, 400f), Is.EqualTo(3));
            Assert.That(
                FindSourceLodAtDistance(rigid, 10000f),
                Is.EqualTo(3));
        }

        [Test]
        public void FindSourceLodAtDistance_BeyondLastLod_IsBoundedByModelList()
        {
            var rigid = CreateRigid(2, 100f, 200f, 300f);

            Assert.That(
                FindSourceLodAtDistance(rigid, 10000f),
                Is.EqualTo(1));
        }

        private static object CreateRecord(
            string typeName,
            IReadOnlyDictionary<string, object?> values)
        {
            var constructor = GetNestedType(typeName)
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                .Single();
            var arguments = constructor.GetParameters()
                .Select(parameter =>
                {
                    if (values.TryGetValue(parameter.Name!, out var value))
                        return value;
                    if (parameter.ParameterType.IsArray)
                    {
                        return Array.CreateInstance(
                            parameter.ParameterType.GetElementType()!,
                            0);
                    }

                    return parameter.ParameterType == typeof(string)
                        ? string.Empty
                        : Activator.CreateInstance(parameter.ParameterType);
                })
                .ToArray();
            return constructor.Invoke(arguments);
        }

        private static object CreatePlan(
            string planId,
            string payloadId,
            double expectedDraws)
        {
            return CreateRecord(
                "CrossRigidMergeAnalysisEntry",
                new Dictionary<string, object?>
                {
                    ["RewritePlanId"] = planId,
                    ["GeneratedPayloadId"] = payloadId,
                    ["VmdPath"] = "models\\" + planId + ".variantmeshdefinition",
                    ["ExpectedArmyDrawCallsEliminated"] = expectedDraws,
                });
        }

        private static object CreatePayload(string payloadId)
        {
            return CreateRecord(
                "CrossRigidGeneratedPayloadAnalysisEntry",
                new Dictionary<string, object?>
                {
                    ["GeneratedPayloadId"] = payloadId,
                    ["RewritePlanCount"] = 2,
                    ["VmdCount"] = 2,
                    ["GeneratedGeometryBytes"] = 100_000L,
                    ["ExpectedResidentGeneratedGeometryBytes"] = 50_000.0,
                });
        }

        private static object CreateSelectionState(
            string payloadId,
            double primaryDraws,
            double fallbackDraws)
        {
            var batchStateType = GetNestedType("BatchState");
            var constructor = batchStateType.GetConstructors(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic)
                .Single();
            var packFileService =
                new Moq.Mock<Shared.Core.PackFiles.IPackFileService>();
            var container =
                new Moq.Mock<Shared.Core.PackFiles.Models.IPackFileContainer>();
            var state = constructor.Invoke(
                [
                    container.Object,
                    container.Object,
                    packFileService.Object,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    false,
                    false,
                    false,
                ]);

            var primaryPlan = CreatePlan("primary", payloadId, primaryDraws);
            var fallbackPlan = CreatePlan("fallback", payloadId, fallbackDraws);
            AddToList(state, "CrossRigidMergeAnalysisEntries", primaryPlan);
            AddToList(state, "CrossRigidMergeAnalysisEntries", fallbackPlan);
            AddToList(
                state,
                "CrossRigidGeneratedPayloadAnalysisEntries",
                CreatePayload(payloadId));
            AddToSet(state, "CrossRigidLegacyEligiblePayloadIds", payloadId);
            AddToSet(
                state,
                "CrossRigidPreviouslySuppressedPairFallbackPlanIds",
                "fallback");

            return state;
        }

        private static void AddToList(
            object state,
            string propertyName,
            object item)
        {
            var list = state.GetType()
                .GetProperty(
                    propertyName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic)!
                .GetValue(state)!;
            list.GetType().GetMethod("Add")!.Invoke(list, [item]);
        }

        private static void AddToSet(
            object state,
            string propertyName,
            string item)
        {
            var set = (HashSet<string>)state.GetType()
                .GetProperty(
                    propertyName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic)!
                .GetValue(state)!;
            set.Add(item);
        }

        private static bool LegacyEligibleContains(
            object state,
            string payloadId)
        {
            return ((HashSet<string>)state.GetType()
                .GetProperty(
                    "CrossRigidLegacyEligiblePayloadIds",
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic)!
                .GetValue(state)!).Contains(payloadId);
        }

        private static object GetSelectionEntry(
            object state,
            string payloadId)
        {
            var entries = (IEnumerable)state.GetType()
                .GetProperty(
                    "CrossRigidPayloadSelectionEntries",
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic)!
                .GetValue(state)!;
            return entries.Cast<object>().Single(entry =>
                (string)entry.GetType()
                    .GetProperty("GeneratedPayloadId")!
                    .GetValue(entry)! == payloadId);
        }

        private static T GetEntryValue<T>(object entry, string propertyName)
        {
            return (T)entry.GetType()
                .GetProperty(propertyName)!
                .GetValue(entry)!;
        }

        [Test]
        public void SelectPayloads_ZeroBenefitPrimaryWithValuableFallback_IsNotZeroBenefitAndBecomesFallbackOnly()
        {
            var state = CreateSelectionState(
                "payload-a",
                primaryDraws: 0.0,
                fallbackDraws: 2.2);

            GetStaticMethod("SelectCrossRigidGeneratedPayloads")
                .Invoke(null, [state]);

            var entry = GetSelectionEntry(state, "payload-a");
            Assert.That(
                GetEntryValue<object>(entry, "Decision").ToString(),
                Is.Not.EqualTo("ScenarioResolvedZeroBenefit"));
            Assert.That(
                GetEntryValue<double>(entry, "ExpectedArmyDrawCallsEliminated"),
                Is.EqualTo(2.2).Within(1e-9));
            Assert.That(GetEntryValue<int>(entry, "RewritePlanCount"), Is.EqualTo(2));
            Assert.That(
                GetEntryValue<double>(
                    entry,
                    "ExpectedResidentGeneratedGeometryBytes"),
                Is.EqualTo(50_000.0));
            Assert.That(LegacyEligibleContains(state, "payload-a"), Is.False);
        }

        [Test]
        public void SelectPayloads_ZeroBenefitPrimaryAndZeroBenefitFallback_StaysZeroBenefitAndPrimary()
        {
            var state = CreateSelectionState(
                "payload-a",
                primaryDraws: 0.0,
                fallbackDraws: 0.0);

            GetStaticMethod("SelectCrossRigidGeneratedPayloads")
                .Invoke(null, [state]);

            var entry = GetSelectionEntry(state, "payload-a");
            Assert.That(
                GetEntryValue<object>(entry, "Decision").ToString(),
                Is.EqualTo("ScenarioResolvedZeroBenefit"));
            Assert.That(LegacyEligibleContains(state, "payload-a"), Is.True);
        }

        [Test]
        public void SelectPayloads_ValuablePrimary_StaysPrimary()
        {
            var state = CreateSelectionState(
                "payload-a",
                primaryDraws: 3.0,
                fallbackDraws: 2.2);

            GetStaticMethod("SelectCrossRigidGeneratedPayloads")
                .Invoke(null, [state]);

            var entry = GetSelectionEntry(state, "payload-a");
            Assert.That(
                GetEntryValue<double>(entry, "ExpectedArmyDrawCallsEliminated"),
                Is.EqualTo(3.0).Within(1e-9));
            Assert.That(GetEntryValue<int>(entry, "RewritePlanCount"), Is.EqualTo(1));
            Assert.That(LegacyEligibleContains(state, "payload-a"), Is.True);
        }
    }
}
