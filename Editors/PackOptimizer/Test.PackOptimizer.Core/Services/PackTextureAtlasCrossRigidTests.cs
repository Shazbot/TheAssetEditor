using System.Collections;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Numerics;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.LodHeader;
using Shared.GameFormats.RigidModel.MaterialHeaders;
using Shared.GameFormats.RigidModel.Types;
using Shared.GameFormats.RigidModel.Vertex;

namespace Test.PackOptimizer.Core
{
    public class PackTextureAtlasCrossRigidTests
    {
        private const string ComponentA = "variantmeshes\\a.wsmodel";
        private const string ComponentB = "variantmeshes\\b.wsmodel";

        private static Type GetServiceType()
        {
            var assembly = Assembly.Load("Editors.PackOptimizer.Core");
            return assembly.GetType(
                "Editors.PackOptimizer.Core.PackTextureAtlasBatchService",
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

        private static object CreateBareBatchState()
        {
            var batchStateType = GetNestedType("BatchState");
            var constructor = batchStateType.GetConstructors(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic)
                .Single();
            var packFileService =
                new Moq.Mock<Editors.PackOptimizer.Core.IPackOptimizerPackFiles>();
            var container =
                new Moq.Mock<Shared.Core.PackFiles.Models.IPackFileContainer>();
            return constructor.Invoke(
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
        }

        private static object CreateSelectionState(
            string payloadId,
            double primaryDraws,
            double fallbackDraws)
        {
            var state = CreateBareBatchState();

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

        private const long MiB = 1024L * 1024;

        private static object CreateSelectionEntry(
            string payloadId,
            long geometryBytes,
            double residentBytes,
            double marginalDraws,
            double physicalBytesPerDraw = 0.0)
        {
            return CreateRecord(
                "CrossRigidPayloadSelectionEntry",
                new Dictionary<string, object?>
                {
                    ["GeneratedPayloadId"] = payloadId,
                    ["GeneratedGeometryBytes"] = geometryBytes,
                    ["ExpectedResidentGeneratedGeometryBytes"] = residentBytes,
                    ["MarginalExpectedArmyDrawCallsEliminated"] = marginalDraws,
                    ["PhysicalBytesPerExpectedDraw"] = physicalBytesPerDraw,
                });
        }

        private static string? GetCeilingDecision(
            long geometryBytes,
            double residentBytes,
            double marginalDraws)
        {
            var candidate = CreateSelectionEntry(
                "payload",
                geometryBytes,
                residentBytes,
                marginalDraws);
            return GetStaticMethod("GetCrossRigidPerPayloadCostCeilingDecision")
                .Invoke(null, [candidate, marginalDraws])?
                .ToString();
        }

        // Ceilings are 1x the portfolio budgets: 16 MiB physical and
        // 0.5 MiB resident per marginal expected draw.
        [Test]
        public void PerPayloadCeiling_CheapPayload_IsNotRejected()
        {
            Assert.That(
                GetCeilingDecision(10 * MiB, 0.1 * MiB, 1.0),
                Is.Null);
        }

        [Test]
        public void PerPayloadCeiling_ExactlyAtCeiling_IsNotRejected()
        {
            Assert.That(
                GetCeilingDecision(16 * MiB, 0.5 * MiB, 1.0),
                Is.Null);
        }

        [Test]
        public void PerPayloadCeiling_OverPhysical_IsRejectedAsPhysical()
        {
            Assert.That(
                GetCeilingDecision(70 * MiB, 0.1 * MiB, 1.0),
                Is.EqualTo("PerPayloadPhysicalCostCeilingExceeded"));
            // 0.7 MiB for 0.0049 draws is about 143 MiB per draw.
            Assert.That(
                GetCeilingDecision((long)(0.7 * MiB), 0.1 * MiB, 0.0049),
                Is.EqualTo("PerPayloadPhysicalCostCeilingExceeded"));
        }

        [Test]
        public void PerPayloadCeiling_OverResident_IsRejectedAsResident()
        {
            Assert.That(
                GetCeilingDecision(1 * MiB, 3.0 * MiB, 1.0),
                Is.EqualTo("PerPayloadResidentCostCeilingExceeded"));
        }

        [Test]
        public void PerPayloadCeiling_BothExceeded_ReportsPhysicalFirst()
        {
            Assert.That(
                GetCeilingDecision(70 * MiB, 3.0 * MiB, 1.0),
                Is.EqualTo("PerPayloadPhysicalCostCeilingExceeded"));
        }

        [Test]
        public void PerPayloadCeiling_ZeroMarginal_IsNotCeilingDecision()
        {
            Assert.That(
                GetCeilingDecision(1000 * MiB, 1000.0 * MiB, 0.0),
                Is.Null);
        }

        private static string[] BuildCurve(params object[] entries)
        {
            var list = (IList)Activator.CreateInstance(
                typeof(List<>).MakeGenericType(
                    GetNestedType("CrossRigidPayloadSelectionEntry")))!;
            foreach (var entry in entries)
                list.Add(entry);
            return (string[])GetStaticMethod(
                    "BuildCrossRigidPayloadCostCurveLines")
                .Invoke(null, [list])!;
        }

        [Test]
        public void CostCurve_NoAcceptedPayloads_PrintsNone()
        {
            Assert.That(BuildCurve(), Is.EqualTo(new[] { "  (none)" }));
        }

        // Sorted by physical cost per draw: c (1, draws 5), b (2, draws 3),
        // a (10, draws 2). Total draws 10, so 50% is reached by c alone
        // (5), 75% needs b (8 >= 7.5), and 90%+ needs all three.
        [Test]
        public void CostCurve_AscendingCost_ReportsPayloadCountAtEachShare()
        {
            var lines = BuildCurve(
                CreateSelectionEntry("a", 20 * MiB, 0, 2.0, 10.0 * MiB),
                CreateSelectionEntry("b", 6 * MiB, 0, 3.0, 2.0 * MiB),
                CreateSelectionEntry("c", 5 * MiB, 0, 5.0, 1.0 * MiB));

            Assert.That(lines, Has.Length.EqualTo(6));
            Assert.That(lines[0], Does.Contain(": 1 payload(s), "));
            Assert.That(lines[1], Does.Contain(": 2 payload(s), "));
            Assert.That(lines[2], Does.Contain(": 3 payload(s), "));
            Assert.That(lines[3], Does.Contain(": 3 payload(s), "));
            Assert.That(lines[4], Does.Contain(": 3 payload(s), "));
            Assert.That(lines[5], Does.Contain(": 3 payload(s), "));
            Assert.That(lines[0], Does.Contain("cumulative marginal=5 "));
            Assert.That(lines[1], Does.Contain("cumulative marginal=8 "));
            Assert.That(lines[5], Does.Contain("cumulative marginal=10 "));
        }

        private static object CreatePlanWithLods(
            string planId,
            string payloadId,
            params int[] activeComponentsPerLod)
        {
            var lodType = GetNestedType("CrossRigidLodAnalysis");
            var lods = Array.CreateInstance(
                lodType,
                activeComponentsPerLod.Length);
            for (var index = 0; index < activeComponentsPerLod.Length; index++)
            {
                lods.SetValue(
                    CreateRecord(
                        "CrossRigidLodAnalysis",
                        new Dictionary<string, object?>
                        {
                            ["LodIndex"] = index,
                            ["ActiveComponentCount"] =
                                activeComponentsPerLod[index],
                        }),
                    index);
            }

            return CreateRecord(
                "CrossRigidMergeAnalysisEntry",
                new Dictionary<string, object?>
                {
                    ["RewritePlanId"] = planId,
                    ["GeneratedPayloadId"] = payloadId,
                    ["Lods"] = lods,
                });
        }

        [Test]
        public void DroppedComponentCount_CountsPayloadsOncePerPayload()
        {
            var plans = (IList)Activator.CreateInstance(
                typeof(List<>).MakeGenericType(
                    GetNestedType("CrossRigidMergeAnalysisEntry")))!;
            plans.Add(CreatePlanWithLods("p1", "x", 3, 3, 2));
            plans.Add(CreatePlanWithLods("p2", "x", 3, 2, 2));
            plans.Add(CreatePlanWithLods("p3", "y", 3, 3, 3));
            plans.Add(CreatePlanWithLods("p4", "z", 2, 2));

            Assert.That(
                GetStaticMethod("CountCrossRigidPayloadsWithDroppedComponents")
                    .Invoke(null, [plans]),
                Is.EqualTo(1));
        }

        private static RmvModel CreateWeightedModel(float seed)
        {
            var vertices = Enumerable
                .Range(0, 4)
                .Select(index => new CommonVertex
                {
                    Position = new Vector4(
                        seed + index,
                        seed * 0.5f + index * 0.25f,
                        index * 1.5f,
                        1f),
                    Normal = Vector3.Normalize(new Vector3(0.3f + index, 0.2f, 0.9f)),
                    BiNormal = Vector3.Normalize(new Vector3(0.1f, 0.7f + index, 0.2f)),
                    Tangent = Vector3.Normalize(new Vector3(0.8f, 0.1f, 0.3f + index)),
                    Uv = new Vector2(0.125f * index + seed * 0.01f, 0.5f),
                    Colour = new Vector4(0.2f, 0.4f, 0.6f, 1f),
                    BoneIndex = [(byte)(index % 3), 2],
                    BoneWeight = [0.75f, 0.25f],
                    WeightCount = 2,
                })
                .ToArray();
            var header = RmvCommonHeader.CreateDefault();
            header.ModelTypeFlag = ModelMaterialEnum.weighted;
            return new RmvModel
            {
                CommonHeader = header,
                Material = new WeightedMaterial
                {
                    BinaryVertexFormat = VertexFormat.Weighted,
                    MaterialId = ModelMaterialEnum.weighted,
                    ModelName = "part",
                    TextureDirectory = "tex",
                },
                Mesh = new RmvMesh
                {
                    VertexList = vertices,
                    IndexList = [0, 1, 2, 0, 2, 3],
                },
            };
        }

        private static RmvFile CreateSingleLodRigid(
            RmvVersionEnum version,
            params RmvModel[] models)
        {
            var header = new RmvFileHeader
            {
                _fileType = "RMV2"u8.ToArray(),
                Version = version,
                LodCount = 1,
            };
            header.SkeletonName = "skeleton";
            var rigid = new RmvFile
            {
                Header = header,
                LodHeaders =
                [
                    LodHeaderFactory.Create().CreateEmpty(
                        version,
                        100f,
                        0,
                        0),
                ],
                ModelList = [models],
            };
            rigid.RecalculateOffsets();
            return rigid;
        }

        private static RmvModel MergeOwned(params RmvModel[] models)
        {
            return (RmvModel)GetStaticMethod("MergeRmvModelsOwned")
                .Invoke(null, [models])!;
        }

        private static string DescribeVertex(CommonVertex vertex)
        {
            return string.Join(
                "|",
                vertex.Position,
                vertex.Normal,
                vertex.Uv,
                string.Join(",", vertex.BoneIndex),
                string.Join(",", vertex.BoneWeight));
        }

        [TestCase(1)]
        [TestCase(2)]
        public void MergeOwned_GeneratedModel_SharesNothingMutableWithSources(
            int partCount)
        {
            var sources = Enumerable
                .Range(0, partCount)
                .Select(index => CreateWeightedModel(index + 1f))
                .ToArray();
            var sourceVertexSnapshots = sources
                .Select(source => source.Mesh.VertexList
                    .Select(DescribeVertex)
                    .ToArray())
                .ToArray();
            var sourceIndexSnapshots = sources
                .Select(source => source.Mesh.IndexList.ToArray())
                .ToArray();

            var generated = MergeOwned(sources);

            Assert.That(sources, Has.None.SameAs(generated));
            Assert.That(
                sources.Select(source => source.Mesh),
                Has.None.SameAs(generated.Mesh));
            Assert.That(
                sources.Select(source => source.Material),
                Has.None.SameAs(generated.Material));
            Assert.That(
                sources.Select(source => source.Mesh.VertexList),
                Has.None.SameAs(generated.Mesh.VertexList));
            Assert.That(
                sources.Select(source => source.Mesh.IndexList),
                Has.None.SameAs(generated.Mesh.IndexList));
            var sourceVertices = sources
                .SelectMany(source => source.Mesh.VertexList)
                .ToHashSet();
            Assert.That(
                generated.Mesh.VertexList.Any(sourceVertices.Contains),
                Is.False);

            // Mutate everything the later pipeline may touch in place.
            foreach (var vertex in generated.Mesh.VertexList)
            {
                vertex.Uv = new Vector2(9f, 9f);
                vertex.Normal = Vector3.UnitZ;
                vertex.BoneIndex[0] = 99;
                vertex.BoneWeight[0] = 0.01f;
            }

            generated.Mesh.IndexList[0] = 3;
            generated.Mesh.VertexList = [];
            generated.Mesh.IndexList = [];
            generated.Material.ModelName = "changed";
            generated.UpdateModelTypeFlag(ModelMaterialEnum.default_type);

            for (var index = 0; index < sources.Length; index++)
            {
                Assert.That(
                    sources[index].Mesh.VertexList.Select(DescribeVertex),
                    Is.EqualTo(sourceVertexSnapshots[index]));
                Assert.That(
                    sources[index].Mesh.IndexList,
                    Is.EqualTo(sourceIndexSnapshots[index]));
                Assert.That(sources[index].Material.ModelName, Is.EqualTo("part"));
                Assert.That(
                    sources[index].CommonHeader.ModelTypeFlag,
                    Is.EqualTo(ModelMaterialEnum.weighted));
            }
        }

        // The removed save+reload normalized the generated rigid. Prove on
        // real serialization that, for data that came from a loaded source
        // rigid, serializing the owned model directly yields the same bytes
        // as the old save -> load -> save path, including V7 -> V8.
        [TestCase(RmvVersionEnum.RMV2_V7, 1)]
        [TestCase(RmvVersionEnum.RMV2_V7, 2)]
        [TestCase(RmvVersionEnum.RMV2_V8, 1)]
        [TestCase(RmvVersionEnum.RMV2_V8, 2)]
        public void MergeOwned_SerializedBytes_MatchOldRoundTripPath(
            RmvVersionEnum sourceVersion,
            int partCount)
        {
            var factory = ModelFactory.Create();
            var sourceFile = CreateSingleLodRigid(
                sourceVersion,
                Enumerable
                    .Range(0, partCount)
                    .Select(index => CreateWeightedModel(index + 1.3f))
                    .ToArray());
            var loadedSource = factory.Load(
                factory.Save(sourceFile, false, false));

            var generated = CreateSingleLodRigid(
                RmvVersionEnum.RMV2_V8,
                MergeOwned(loadedSource.ModelList[0]));
            var direct = factory.Save(generated, false, false);

            var reloaded = factory.Load(direct);
            reloaded.RecalculateOffsets();
            var viaRoundTrip = factory.Save(reloaded, false, false);

            Assert.That(direct, Is.EqualTo(viaRoundTrip));
        }

        private const string BaselineVmdPath =
            @"variantmeshes\variantmeshdefinitions\fallback.variantmeshdefinition";

        private static object CreateArmyState()
        {
            var state = CreateBareBatchState();
            var model = typeof(PackTextureAtlasValueGateTests)
                .GetMethod(
                    "CreateSyntheticArmyResidencyModelForFallbackTest",
                    BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, null)!;
            state.GetType()
                .GetProperty(
                    "ArmyResidencyModel",
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic)!
                .SetValue(state, model);
            return state;
        }

        private static object GetScenario(object state)
        {
            var model = state.GetType()
                .GetProperty(
                    "ArmyResidencyModel",
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic)!
                .GetValue(state)!;
            return model.GetType().GetProperty("Scenario")!.GetValue(model)!;
        }

        private static (int Lod, double Probability)[] GetScenarioLods(
            object state)
        {
            return ((IEnumerable<KeyValuePair<int, double>>)GetScenario(state)
                    .GetType()
                    .GetProperty("LodDistribution")!
                    .GetValue(GetScenario(state))!)
                .Where(entry => entry.Value > 0)
                .OrderBy(entry => entry.Key)
                .Select(entry => (entry.Key, entry.Value))
                .ToArray();
        }

        private static double GetSlotCount(object state, string category)
        {
            var scenario = GetScenario(state);
            var template = scenario.GetType()
                .GetProperty("ArmySlotTemplate")!
                .GetValue(scenario)!;
            foreach (var entry in (IEnumerable)template)
            {
                var key = entry.GetType().GetProperty("Key")!.GetValue(entry)!;
                if (key.ToString() == category)
                {
                    return Convert.ToDouble(
                        entry.GetType().GetProperty("Value")!.GetValue(entry));
                }
            }

            throw new InvalidOperationException("Slot category not found.");
        }

        private static double CallWeight(
            object state,
            double probability,
            (double LodProbability, double DrawsPerEntity)[] lods)
        {
            return (double)GetStaticMethod(
                    "CalculateExpectedCrossRigidArmyDrawWeight")
                .Invoke(null, [state, BaselineVmdPath, probability, lods])!;
        }

        // The synthetic army has one InfantryMissile unit (1 entity, 1 role)
        // using the VMD directly, one culture on both sides. The savings
        // must equal sum(lodProbability * slots * probability * saved).
        [Test]
        public void ArmyDrawSavings_AfterWeightingRefactor_MatchesDirectFormula()
        {
            var state = CreateArmyState();
            var scenarioLods = GetScenarioLods(state);
            var slots = GetSlotCount(state, "InfantryMissile");
            const double probability = 0.35;
            var saved = new[] { 3, 2, 0, 1 };

            var lodAnalyses = Array.CreateInstance(
                GetNestedType("CrossRigidLodAnalysis"),
                scenarioLods.Length);
            for (var index = 0; index < scenarioLods.Length; index++)
            {
                lodAnalyses.SetValue(
                    CreateRecord(
                        "CrossRigidLodAnalysis",
                        new Dictionary<string, object?>
                        {
                            ["LodIndex"] = index,
                            ["DrawsSaved"] = saved[index % saved.Length],
                            ["LodProbability"] = scenarioLods[index].Probability,
                        }),
                    index);
            }

            var actual = (double)GetStaticMethod(
                    "CalculateExpectedCrossRigidArmyDrawSavings")
                .Invoke(
                    null,
                    [state, BaselineVmdPath, probability, lodAnalyses])!;

            double expectedPerSide = 0;
            for (var index = 0; index < scenarioLods.Length; index++)
            {
                expectedPerSide +=
                    scenarioLods[index].Probability *
                    slots *
                    (probability * 1.0 * 1 * saved[index % saved.Length]);
            }

            Assert.That(actual, Is.GreaterThan(0));
            // Player and opponent sides each contribute the single culture.
            Assert.That(actual, Is.EqualTo(2 * expectedPerSide).Within(1e-9));
        }

        [Test]
        public void ArmyDrawWeight_BaselineDominatesSavingsAndIgnoresSelection()
        {
            var state = CreateArmyState();
            var scenarioLods = GetScenarioLods(state);
            var savedLods = scenarioLods
                .Select(lod => (lod.Probability, 1.0))
                .ToArray();
            var baselineLods = scenarioLods
                .Select(lod => (lod.Probability, 4.0))
                .ToArray();

            var savings = CallWeight(state, 0.5, savedLods);
            var baseline = CallWeight(state, 1.0, baselineLods);
            ((ISet<string>)state.GetType()
                    .GetProperty(
                        "CrossRigidSelectedPayloadIds",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic)!
                    .GetValue(state)!)
                .Add("payload-a");
            var baselineAfterSelection = CallWeight(state, 1.0, baselineLods);

            Assert.That(savings, Is.GreaterThan(0));
            Assert.That(baseline, Is.GreaterThanOrEqualTo(savings));
            Assert.That(baselineAfterSelection, Is.EqualTo(baseline));
        }

        private static object CreateVisualInstance(string wsModelPath)
        {
            return CreateRecord(
                "CrossRigidVisualInstance",
                new Dictionary<string, object?>
                {
                    ["WsModelPath"] = wsModelPath,
                    ["AttachmentIdentity"] = "<root>",
                    ["DefiningVmdPath"] = BaselineVmdPath,
                });
        }

        private static object CreateVisualConfiguration(
            double probability,
            params string[] wsModelPaths)
        {
            var instanceType = GetNestedType("CrossRigidVisualInstance");
            var instances = Array.CreateInstance(
                instanceType,
                wsModelPaths.Length);
            for (var index = 0; index < wsModelPaths.Length; index++)
                instances.SetValue(CreateVisualInstance(wsModelPaths[index]), index);

            return CreateRecord(
                "CrossRigidVisualConfiguration",
                new Dictionary<string, object?>
                {
                    ["Probability"] = probability,
                    ["AttachmentIdentity"] = "<root>",
                    ["Instances"] = instances,
                    ["OccurrenceSets"] = Array.CreateInstance(
                        GetNestedType("CrossRigidVisualOccurrenceSet"),
                        0),
                });
        }

        private static object CreateExpansion(params object[] configurations)
        {
            var configurationType = GetNestedType("CrossRigidVisualConfiguration");
            var listType = typeof(IReadOnlyList<>).MakeGenericType(configurationType);
            var typed = Array.CreateInstance(
                configurationType,
                configurations.Length);
            for (var index = 0; index < configurations.Length; index++)
                typed.SetValue(configurations[index], index);

            var dictionary = (IDictionary)Activator.CreateInstance(
                typeof(Dictionary<,>).MakeGenericType(typeof(string), listType))!;
            dictionary.Add("<root>", typed);
            return CreateRecord(
                "CrossRigidAttachmentLocalExpansionResult",
                new Dictionary<string, object?>
                {
                    ["ConfigurationsByAttachment"] = dictionary,
                });
        }

        private static Func<string, T?> BuildResolver<T>(
            Dictionary<string, object> componentsByPath)
            where T : class
        {
            return path => componentsByPath.TryGetValue(path, out var value)
                ? (T)value
                : null;
        }

        private static object CreateBaselineComponent(
            string wsModelPath,
            RmvModel[][] lods,
            string[][] assignments)
        {
            var rigid = new RmvFile
            {
                LodHeaders = lods
                    .Select(_ => (RmvLodHeader)new Rmv2LodHeader_V7_V8())
                    .ToArray(),
                ModelList = lods,
            };
            return GetNestedType("CrossRigidAnalysisComponent")
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                .Single()
                .Invoke(
                [
                    BaselineVmdPath,
                    wsModelPath,
                    wsModelPath + ".rigid_model_v2",
                    new XmlDocument(),
                    rigid,
                    assignments,
                    string.Empty,
                    false,
                    false,
                ]);
        }

        // WSModel A: LOD 0 has 3 parts, two sharing one material (merge to
        // one draw) plus one other material = 2 draws; LOD 1 has 1 part.
        // WSModel B: a single LOD with 2 parts on different materials = 2.
        // Configurations: 0.5 -> A+B, 0.5 -> A only (a single-WSModel
        // configuration the merge analysis skips). Scenario LODs beyond a
        // rigid's last LOD use its last LOD.
        [Test]
        public void BaselineExpectedDraws_ProbabilityWeightsConfigurationsAndClampsLods()
        {
            var state = CreateArmyState();
            const string wsA = @"models\a.wsmodel";
            const string wsB = @"models\b.wsmodel";
            var components = new Dictionary<string, object>
            {
                [wsA] = CreateBaselineComponent(
                    wsA,
                    [
                        [CreateWeightedModel(1), CreateWeightedModel(1), CreateWeightedModel(1)],
                        [CreateWeightedModel(1)],
                    ],
                    [
                        [@"materials\one.material", @"materials\one.material", @"materials\two.material"],
                        [@"materials\one.material"],
                    ]),
                [wsB] = CreateBaselineComponent(
                    wsB,
                    [[CreateWeightedModel(1), CreateWeightedModel(1)]],
                    [[@"materials\one.material", @"materials\two.material"]]),
            };
            var expansion = CreateExpansion(
                CreateVisualConfiguration(0.5, wsA, wsB),
                CreateVisualConfiguration(0.5, wsA));
            var resolver = GetType()
                .GetMethod(
                    nameof(BuildResolver),
                    BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(GetNestedType("CrossRigidAnalysisComponent"))
                .Invoke(null, [components])!;

            var baseline = (double)GetStaticMethod(
                    "CalculateCrossRigidBaselineExpectedDraws")
                .Invoke(null, [state, BaselineVmdPath, expansion, resolver])!;

            // LOD 0: 0.5 * (2 + 2) + 0.5 * 2 = 3. LOD >= 1: A uses its
            // LOD 1 (1 draw), B its only LOD (2): 0.5 * 3 + 0.5 * 1 = 2.
            var expectedLods = GetScenarioLods(state)
                .Select(lod => (lod.Probability, lod.Lod == 0 ? 3.0 : 2.0))
                .ToArray();
            Assert.That(baseline, Is.GreaterThan(0));
            Assert.That(
                baseline,
                Is.EqualTo(CallWeight(state, 1.0, expectedLods)).Within(1e-9));
        }

        [Test]
        public void BaselineExpectedDraws_UnitsNotUsingTheVmd_ContributeNothing()
        {
            var state = CreateArmyState();
            var expansion = CreateExpansion(
                CreateVisualConfiguration(1.0, @"models\a.wsmodel"));
            var resolver = GetType()
                .GetMethod(
                    nameof(BuildResolver),
                    BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(GetNestedType("CrossRigidAnalysisComponent"))
                .Invoke(null, [new Dictionary<string, object>()])!;

            var baseline = (double)GetStaticMethod(
                    "CalculateCrossRigidBaselineExpectedDraws")
                .Invoke(
                    null,
                    [state, @"variantmeshes\other.variantmeshdefinition", expansion, resolver])!;

            Assert.That(baseline, Is.EqualTo(0));
        }

        // ---- Ideal cross-rigid merge tiers (report-only diagnostic) ----

        private const string IdealMaterialOne = @"materials\m1.material";
        private const string IdealMaterialTwo = @"materials\m2.material";
        private const string IdealWsOne = @"models\ideal_one.wsmodel";
        private const string IdealWsTwo = @"models\ideal_two.wsmodel";

        private static string BuildMaterialXml(
            string shader,
            string baseColour,
            string legacyDiffuse = "")
        {
            static string Texture(string slot, string source)
                => $"<texture><slot version=\"2\">{slot}</slot>" +
                   $"<source>{source}</source></texture>";

            return "<material><name>ignored</name>" +
                   $"<shader>{shader}</shader><textures>" +
                   Texture("t_xml_base_colour", baseColour) +
                   Texture("t_xml_blood_map", "commontextures/bloodmap.dds") +
                   (legacyDiffuse.Length == 0
                       ? string.Empty
                       : Texture("s_diffuse", legacyDiffuse)) +
                   "</textures><params><param><name>p</name>" +
                   "<type>float</type><value>1</value></param></params>" +
                   "</material>";
        }

        // 8x8 DXT1, one mip: 2x2 blocks * 8 bytes = 32 bytes.
        private static byte[] BuildDxt1Dds()
        {
            var data = new byte[148];
            BitConverter.GetBytes(0x20534444u).CopyTo(data, 0);
            BitConverter.GetBytes(124u).CopyTo(data, 4);
            BitConverter.GetBytes(8).CopyTo(data, 12);
            BitConverter.GetBytes(8).CopyTo(data, 16);
            BitConverter.GetBytes(1).CopyTo(data, 28);
            BitConverter.GetBytes(0x31545844u).CopyTo(data, 84);
            return data;
        }

        private static object CreateIdealState(
            IReadOnlyDictionary<string, byte[]> filesByPath)
        {
            var files = filesByPath.ToDictionary(
                entry => entry.Key.ToLowerInvariant().Replace('/', '\\'),
                entry => new Shared.Core.PackFiles.Models.PackFile(
                    "file",
                    new Shared.Core.PackFiles.Models.FileSources.MemorySource(
                        entry.Value)));
            var container =
                new Moq.Mock<Shared.Core.PackFiles.Models.IPackFileContainer>();
            container
                .Setup(item => item.FindFile(Moq.It.IsAny<string>()))
                .Returns((string path) => files.GetValueOrDefault(path));
            var packFileService =
                new Moq.Mock<Editors.PackOptimizer.Core.IPackOptimizerPackFiles>();
            var state = GetNestedType("BatchState")
                .GetConstructors(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic)
                .Single()
                .Invoke(
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
            var model = typeof(PackTextureAtlasValueGateTests)
                .GetMethod(
                    "CreateSyntheticArmyResidencyModelForFallbackTest",
                    BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, null)!;
            state.GetType()
                .GetProperty(
                    "ArmyResidencyModel",
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic)!
                .SetValue(state, model);
            return state;
        }

        private static object CreateIdealState(
            IReadOnlyDictionary<string, string> materialXmlByPath)
        {
            return CreateIdealState(
                materialXmlByPath.ToDictionary(
                    entry => entry.Key,
                    entry => System.Text.Encoding.UTF8.GetBytes(entry.Value)));
        }

        private static T GetStateProperty<T>(object state, string name)
        {
            return (T)state.GetType()
                .GetProperty(
                    name,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic)!
                .GetValue(state)!;
        }

        private static RmvModel CreateIdealPart(
            int vertexCount = 4,
            bool cinematic = false)
        {
            var model = CreateWeightedModel(1);
            if (vertexCount != 4)
                model.Mesh.VertexList = new CommonVertex[vertexCount];
            if (cinematic)
            {
                ((WeightedMaterial)model.Material).BinaryVertexFormat =
                    VertexFormat.Cinematic;
            }

            return model;
        }

        private static Dictionary<string, object> CreateIdealComponents(
            params (string WsPath, RmvModel Part, string Material)[] items)
        {
            return items.ToDictionary(
                item => item.WsPath,
                item => CreateBaselineComponent(
                    item.WsPath,
                    [[item.Part]],
                    [[item.Material]]));
        }

        private static object CreateIdealResolver(
            Dictionary<string, object> components)
        {
            return typeof(PackTextureAtlasCrossRigidTests)
                .GetMethod(
                    nameof(BuildResolver),
                    BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(GetNestedType("CrossRigidAnalysisComponent"))
                .Invoke(null, [components])!;
        }

        private static double[] RunIdeal(
            object state,
            object expansion,
            Dictionary<string, object> components)
        {
            return (double[])GetStaticMethod(
                    "CalculateCrossRigidIdealMergeExpectedDraws")
                .Invoke(
                    null,
                    [state, BaselineVmdPath, expansion, CreateIdealResolver(components)])!;
        }

        private static double RunBaseline(
            object state,
            object expansion,
            Dictionary<string, object> components)
        {
            return (double)GetStaticMethod(
                    "CalculateCrossRigidBaselineExpectedDraws")
                .Invoke(
                    null,
                    [state, BaselineVmdPath, expansion, CreateIdealResolver(components)])!;
        }

        // Expected value of a root whose entity renders `drawsPerEntity`
        // draws at every scenario LOD (single-LOD rigids clamp).
        private static double ExpectedForConstantDraws(
            object state,
            double drawsPerEntity)
        {
            return CallWeight(
                state,
                1.0,
                GetScenarioLods(state)
                    .Select(lod => (lod.Probability, drawsPerEntity))
                    .ToArray());
        }

        private static void AssertTierDraws(
            object state,
            double[] actual,
            params double[] drawsPerEntityByTier)
        {
            Assert.That(actual, Has.Length.EqualTo(drawsPerEntityByTier.Length));
            for (var tier = 0; tier < actual.Length; tier++)
            {
                Assert.That(
                    actual[tier],
                    Is.EqualTo(
                            ExpectedForConstantDraws(
                                state,
                                drawsPerEntityByTier[tier]))
                        .Within(1e-9),
                    $"tier T{tier}");
            }
        }

        // Two WSModels in one configuration; materials differ only in the
        // base-colour source. Baseline and T0 keep two draws, T1+ merge.
        [Test]
        public void IdealMerge_AtlasableTextureDifference_MergesFromT1()
        {
            var dds = BuildDxt1Dds();
            var state = CreateIdealState(
                new Dictionary<string, byte[]>
                {
                    [IdealMaterialOne] = System.Text.Encoding.UTF8.GetBytes(
                        BuildMaterialXml(
                            "shaders/weighted2_character.xml.shader",
                            "tex/a_base.dds")),
                    [IdealMaterialTwo] = System.Text.Encoding.UTF8.GetBytes(
                        BuildMaterialXml(
                            "shaders/weighted2_character.xml.shader",
                            "Tex/B_base.dds")),
                    [@"tex\a_base.dds"] = dds,
                    [@"tex\b_base.dds"] = dds,
                });
            var components = CreateIdealComponents(
                (IdealWsOne, CreateIdealPart(), IdealMaterialOne),
                (IdealWsTwo, CreateIdealPart(), IdealMaterialTwo));
            var expansion = CreateExpansion(
                CreateVisualConfiguration(1.0, IdealWsOne, IdealWsTwo));

            var baseline = RunBaseline(state, expansion, components);
            var tiers = RunIdeal(state, expansion, components);

            Assert.That(baseline, Is.GreaterThan(0));
            Assert.That(
                baseline,
                Is.EqualTo(ExpectedForConstantDraws(state, 2)).Within(1e-9));
            AssertTierDraws(state, tiers, 2, 1, 1, 1, 1);

            // Texture-cost side: both materials need to share an atlas; the
            // two distinct base-colour textures are sized from their headers.
            GetStaticMethod("FinalizeCrossRigidIdealMergeTextureCost")
                .Invoke(null, [state]);
            Assert.That(
                GetStateProperty<HashSet<string>>(
                    state,
                    "CrossRigidIdealMergeSharedAtlasMaterialPaths"),
                Is.EquivalentTo([IdealMaterialOne, IdealMaterialTwo]));
            Assert.That(
                GetStateProperty<int>(
                    state,
                    "CrossRigidIdealMergeSharedAtlasTextureCount"),
                Is.EqualTo(2));
            Assert.That(
                GetStateProperty<int>(
                    state,
                    "CrossRigidIdealMergeSharedAtlasSizedTextureCount"),
                Is.EqualTo(2));
            Assert.That(
                GetStateProperty<long>(
                    state,
                    "CrossRigidIdealMergeSharedAtlasBcnBytes"),
                Is.EqualTo(64));
        }

        // Identical materials: the strict identity already merges them, so
        // no atlas is needed and nothing is reported as sharing one.
        [Test]
        public void IdealMerge_IdenticalMaterials_MergeAtT0_WithoutSharedAtlas()
        {
            var xml = BuildMaterialXml(
                "shaders/weighted2_character.xml.shader",
                "tex/a_base.dds");
            var state = CreateIdealState(
                new Dictionary<string, string>
                {
                    [IdealMaterialOne] = xml,
                    [IdealMaterialTwo] = xml,
                });
            var components = CreateIdealComponents(
                (IdealWsOne, CreateIdealPart(), IdealMaterialOne),
                (IdealWsTwo, CreateIdealPart(), IdealMaterialTwo));
            var expansion = CreateExpansion(
                CreateVisualConfiguration(1.0, IdealWsOne, IdealWsTwo));

            Assert.That(
                RunBaseline(state, expansion, components),
                Is.EqualTo(ExpectedForConstantDraws(state, 2)).Within(1e-9));
            AssertTierDraws(state, RunIdeal(state, expansion, components), 1, 1, 1, 1, 1);
            Assert.That(
                GetStateProperty<HashSet<string>>(
                    state,
                    "CrossRigidIdealMergeSharedAtlasMaterialPaths"),
                Is.Empty);
        }

        // Materials differing only in a legacy s_diffuse source merge at T2
        // but not at T1.
        [Test]
        public void IdealMerge_LegacySlotDifference_MergesAtT2NotT1()
        {
            var state = CreateIdealState(
                new Dictionary<string, string>
                {
                    [IdealMaterialOne] = BuildMaterialXml(
                        "shaders/weighted2_character.xml.shader",
                        "tex/a_base.dds",
                        "tex/legacy_x.dds"),
                    [IdealMaterialTwo] = BuildMaterialXml(
                        "shaders/weighted2_character.xml.shader",
                        "tex/a_base.dds",
                        "tex/legacy_y.dds"),
                });
            var components = CreateIdealComponents(
                (IdealWsOne, CreateIdealPart(), IdealMaterialOne),
                (IdealWsTwo, CreateIdealPart(), IdealMaterialTwo));
            var expansion = CreateExpansion(
                CreateVisualConfiguration(1.0, IdealWsOne, IdealWsTwo));

            AssertTierDraws(
                state,
                RunIdeal(state, expansion, components),
                2, 2, 1, 1, 1);
        }

        // weighted2 vs weighted4: the material shader path and the RMV
        // vertex format (Weighted vs Cinematic) both differ; only T3 unifies
        // both sides.
        [Test]
        public void IdealMerge_Weighted2VsWeighted4_MergesAtT3NotT2()
        {
            var state = CreateIdealState(
                new Dictionary<string, string>
                {
                    [IdealMaterialOne] = BuildMaterialXml(
                        "shaders/weighted2_character.xml.shader",
                        "tex/a_base.dds"),
                    [IdealMaterialTwo] = BuildMaterialXml(
                        "shaders/weighted4_character.xml.shader",
                        "tex/a_base.dds"),
                });
            var components = CreateIdealComponents(
                (IdealWsOne, CreateIdealPart(), IdealMaterialOne),
                (IdealWsTwo, CreateIdealPart(cinematic: true), IdealMaterialTwo));
            var expansion = CreateExpansion(
                CreateVisualConfiguration(1.0, IdealWsOne, IdealWsTwo));

            AssertTierDraws(
                state,
                RunIdeal(state, expansion, components),
                2, 2, 2, 1, 1);
        }

        [Test]
        public void IdealMerge_RmvIdentity_UnifiesVertexFormatOnlyFromT3()
        {
            var weighted2 = CreateIdealPart();
            var weighted4 = CreateIdealPart(cinematic: true);
            var rmvIdentity = GetStaticMethod("GetRmvMergeIdentity");
            var idealIdentity = GetStaticMethod(
                "GetCrossRigidIdealMergeRmvIdentity");

            Assert.That(
                rmvIdentity.Invoke(null, [weighted2]),
                Is.Not.EqualTo(rmvIdentity.Invoke(null, [weighted4])));
            Assert.That(
                idealIdentity.Invoke(null, [weighted2, false]),
                Is.EqualTo(idealIdentity.Invoke(null, [weighted4, false])));

            // Other RMV state stays strict.
            var otherRenderFlag = CreateIdealPart();
            var header = otherRenderFlag.CommonHeader;
            header.RenderFlag = 5;
            otherRenderFlag.CommonHeader = header;
            Assert.That(
                idealIdentity.Invoke(null, [weighted2, false]),
                Is.Not.EqualTo(idealIdentity.Invoke(null, [otherRenderFlag, false])));
            Assert.That(
                idealIdentity.Invoke(null, [weighted2, true]),
                Is.EqualTo(idealIdentity.Invoke(null, [otherRenderFlag, true])));
        }

        [Test]
        public void IdealMerge_MaterialIdentity_RemovesSlotsAndShaderVariantsPerTier()
        {
            static XmlDocument Load(string xml)
            {
                var document = new XmlDocument();
                document.LoadXml(xml);
                return document;
            }

            string Identity(XmlDocument document, int tier)
                => (string)GetStaticMethod(
                        "BuildCrossRigidIdealMergeMaterialIdentity")
                    .Invoke(null, [document, tier])!;

            var plain = Load(BuildMaterialXml(
                "shaders/weighted2_character.xml.shader",
                "tex/a.dds",
                "tex/x.dds"));
            var otherBase = Load(BuildMaterialXml(
                "Shaders\\Weighted2_Character.xml.shader",
                "tex/b.dds",
                "tex/x.dds"));
            var otherLegacy = Load(BuildMaterialXml(
                "shaders/weighted2_character.xml.shader",
                "tex/a.dds",
                "tex/y.dds"));
            var weighted4 = Load(BuildMaterialXml(
                "shaders/weighted4_character.xml.shader",
                "tex/a.dds",
                "tex/x.dds"));
            var alpha = Load(BuildMaterialXml(
                "shaders/weighted4_character_alpha.xml.shader",
                "tex/a.dds",
                "tex/x.dds"));

            Assert.That(Identity(plain, 0), Is.Not.EqualTo(Identity(otherBase, 0)));
            Assert.That(Identity(plain, 1), Is.EqualTo(Identity(otherBase, 1)));
            Assert.That(Identity(plain, 1), Is.Not.EqualTo(Identity(otherLegacy, 1)));
            Assert.That(Identity(plain, 2), Is.EqualTo(Identity(otherLegacy, 2)));
            Assert.That(Identity(plain, 2), Is.Not.EqualTo(Identity(weighted4, 2)));
            Assert.That(Identity(plain, 3), Is.EqualTo(Identity(weighted4, 3)));
            Assert.That(Identity(plain, 3), Is.Not.EqualTo(Identity(alpha, 3)));
            Assert.That(Identity(plain, 4), Is.EqualTo(Identity(alpha, 4)));

            // Non-atlasable textures remain part of every tier's identity.
            var otherBlood = Load(
                BuildMaterialXml(
                    "shaders/weighted2_character.xml.shader",
                    "tex/a.dds",
                    "tex/x.dds")
                    .Replace("bloodmap.dds", "other_blood.dds"));
            Assert.That(Identity(plain, 4), Is.Not.EqualTo(Identity(otherBlood, 4)));
        }

        // Five single-part WSModels in one configuration, each added
        // distinction removed by one more tier: 5, 4, 3, 2, 1 draws. Also
        // exercises the monotonic self-check on a mixed case.
        [Test]
        public void IdealMerge_MixedConfiguration_TiersAreMonotonic()
        {
            const string alphaShader =
                "shaders/weighted4_character_alpha.xml.shader";
            var materials = new Dictionary<string, string>
            {
                [@"materials\a1.material"] = BuildMaterialXml(
                    "shaders/weighted2_character.xml.shader", "tex/a.dds", "tex/x.dds"),
                [@"materials\a2.material"] = BuildMaterialXml(
                    "shaders/weighted2_character.xml.shader", "tex/b.dds", "tex/x.dds"),
                [@"materials\a3.material"] = BuildMaterialXml(
                    "shaders/weighted2_character.xml.shader", "tex/a.dds", "tex/y.dds"),
                [@"materials\a4.material"] = BuildMaterialXml(
                    "shaders/weighted4_character.xml.shader", "tex/a.dds", "tex/y.dds"),
                [@"materials\a5.material"] = BuildMaterialXml(
                    alphaShader, "tex/a.dds", "tex/y.dds"),
            };
            var state = CreateIdealState(materials);
            var items = materials.Keys
                .Select((material, index) => (
                    WsPath: $@"models\mixed_{index}.wsmodel",
                    Part: CreateIdealPart(cinematic: index >= 3),
                    Material: material))
                .ToArray();
            var components = CreateIdealComponents(items);
            var expansion = CreateExpansion(
                CreateVisualConfiguration(
                    1.0,
                    items.Select(item => item.WsPath).ToArray()));

            var baseline = RunBaseline(state, expansion, components);
            var tiers = RunIdeal(state, expansion, components);

            AssertTierDraws(state, tiers, 5, 4, 3, 2, 1);
            Assert.That(baseline, Is.EqualTo(ExpectedForConstantDraws(state, 5)).Within(1e-9));

            // Runtime self-check: clean for the real results, flagged when a
            // tier exceeds its predecessor.
            GetStateProperty<Dictionary<string, double>>(
                state,
                "CrossRigidBaselineExpectedDrawsByVmd")[BaselineVmdPath] = baseline;
            var byVmd = GetStateProperty<Dictionary<string, double[]>>(
                state,
                "CrossRigidIdealMergeExpectedDrawsByVmd");
            byVmd[BaselineVmdPath] = tiers;
            var violations = GetStaticMethod(
                "CountCrossRigidIdealMergeInvariantViolations");
            Assert.That(violations.Invoke(null, [state]), Is.EqualTo(0));

            byVmd[BaselineVmdPath] = [baseline, baseline, baseline + 1, 0, 0];
            Assert.That(violations.Invoke(null, [state]), Is.EqualTo(1));
            byVmd[BaselineVmdPath] = tiers;

            var lines = (string[])GetStaticMethod(
                    "BuildCrossRigidIdealMergeReportLines")
                .Invoke(null, [state, 0.0])!;
            Assert.That(
                lines[0],
                Does.StartWith("Ideal cross-rigid merge potential"));
            Assert.That(
                lines,
                Has.Some.Contains("Self-check baseline >= T0 >= T1 >= T2 >= T3 >= T4: OK"));
            Assert.That(
                lines,
                Has.Some.Contains("T4 T3 + alpha variant unified (approximation): expected battle draws="));
            Assert.That(lines, Has.Some.Contains("Top 10 VMD roots by T1 saving:"));
            Assert.That(lines, Has.Some.Contains(BaselineVmdPath));
        }

        // Pooling is per configuration: the same two WSModels in separate
        // configurations never pool, and configuration probabilities weight.
        [Test]
        public void IdealMerge_DoesNotPoolAcrossConfigurations()
        {
            var state = CreateIdealState(
                new Dictionary<string, string>
                {
                    [IdealMaterialOne] = BuildMaterialXml(
                        "shaders/weighted2_character.xml.shader", "tex/a.dds"),
                    [IdealMaterialTwo] = BuildMaterialXml(
                        "shaders/weighted2_character.xml.shader", "tex/b.dds"),
                });
            var components = CreateIdealComponents(
                (IdealWsOne, CreateIdealPart(), IdealMaterialOne),
                (IdealWsTwo, CreateIdealPart(), IdealMaterialTwo));
            var expansion = CreateExpansion(
                CreateVisualConfiguration(0.5, IdealWsOne),
                CreateVisualConfiguration(0.5, IdealWsTwo));

            AssertTierDraws(
                state,
                RunIdeal(state, expansion, components),
                1, 1, 1, 1, 1);
        }

        [Test]
        public void IdealMerge_VmdNotUsedByAnyUnit_ContributesNothing()
        {
            var state = CreateIdealState(new Dictionary<string, string>());
            var components = CreateIdealComponents(
                (IdealWsOne, CreateIdealPart(), IdealMaterialOne));
            var expansion = CreateExpansion(
                CreateVisualConfiguration(1.0, IdealWsOne));

            var tiers = (double[])GetStaticMethod(
                    "CalculateCrossRigidIdealMergeExpectedDraws")
                .Invoke(
                    null,
                    [
                        state,
                        @"variantmeshes\other.variantmeshdefinition",
                        expansion,
                        CreateIdealResolver(components),
                    ])!;

            Assert.That(tiers, Is.EqualTo(new double[5]));
        }

        // Unreadable materials fall back to their path, so only parts using
        // the very same path pool.
        [Test]
        public void IdealMerge_UnreadableMaterials_PoolOnlyBySamePath()
        {
            var state = CreateIdealState(new Dictionary<string, string>());
            var components = CreateIdealComponents(
                (IdealWsOne, CreateIdealPart(), IdealMaterialOne),
                (IdealWsTwo, CreateIdealPart(), IdealMaterialTwo));
            var expansion = CreateExpansion(
                CreateVisualConfiguration(1.0, IdealWsOne, IdealWsTwo));

            AssertTierDraws(
                state,
                RunIdeal(state, expansion, components),
                2, 2, 2, 2, 2);
        }

        // Pure pooling function with injected identities and the 16-bit
        // vertex limit.
        [Test]
        public void IdealMerge_Pooling_RespectsVertexLimit()
        {
            int Count(params (int Identity, int Vertices)[] parts)
                => (int)GetStaticMethod("CountCrossRigidIdealMergeDraws")
                    .Invoke(null, [parts])!;

            Assert.That(Count((0, 30000), (0, 30000)), Is.EqualTo(1));
            Assert.That(Count((0, 40000), (0, 40000)), Is.EqualTo(2));
            Assert.That(Count((0, 40000), (0, 20000), (0, 20000)), Is.EqualTo(2));
            Assert.That(Count((0, 70000), (0, 10)), Is.EqualTo(2));
            Assert.That(Count((0, 10), (1, 10), (0, 10), (1, 10)), Is.EqualTo(2));
            Assert.That(Count(), Is.EqualTo(0));
        }

        // End to end: two parts with an identical material would pool at
        // every tier, but 40000 + 40000 vertices exceed the 16-bit limit.
        [Test]
        public void IdealMerge_PartsOverVertexLimit_StayTwoDraws()
        {
            var xml = BuildMaterialXml(
                "shaders/weighted2_character.xml.shader",
                "tex/a.dds");
            var state = CreateIdealState(
                new Dictionary<string, string>
                {
                    [IdealMaterialOne] = xml,
                    [IdealMaterialTwo] = xml,
                });
            var components = CreateIdealComponents(
                (IdealWsOne, CreateIdealPart(40000), IdealMaterialOne),
                (IdealWsTwo, CreateIdealPart(40000), IdealMaterialTwo));
            var expansion = CreateExpansion(
                CreateVisualConfiguration(1.0, IdealWsOne, IdealWsTwo));

            AssertTierDraws(
                state,
                RunIdeal(state, expansion, components),
                2, 2, 2, 2, 2);
        }

        [Test]
        public void IdealMerge_ReportLines_AreSafeWithoutData()
        {
            var state = CreateBareBatchState();

            var lines = (string[])GetStaticMethod(
                    "BuildCrossRigidIdealMergeReportLines")
                .Invoke(null, [state, 3.0])!;

            Assert.That(
                lines,
                Has.Some.Contains("Self-check baseline >= T0 >= T1 >= T2 >= T3 >= T4: OK"));
            Assert.That(lines, Has.None.Contains("NaN"));
            Assert.That(lines, Has.None.Contains("Infinity"));
            Assert.That(
                lines,
                Has.Some.Contains("Shared-atlas (T1) merges would combine 0 distinct material(s) using 0"));
        }

        // ---- T1 payload accounting, state-cap proxy and baked variants ----

        private static object ReadProperty(object target, string name)
        {
            return target.GetType()
                .GetProperty(
                    name,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic)!
                .GetValue(target)!;
        }

        private static object[] GetAccountedPayloads(object state)
        {
            var accounting = ReadProperty(
                state,
                "CrossRigidIdealMergeAccounting");
            return ((IDictionary)ReadProperty(accounting, "Payloads"))
                .Values
                .Cast<object>()
                .Where(payload => (bool)ReadProperty(payload, "IsPayload"))
                .ToArray();
        }

        private static object[] GetAccountedContexts(object state)
        {
            var accounting = ReadProperty(
                state,
                "CrossRigidIdealMergeAccounting");
            return ((IEnumerable)ReadProperty(accounting, "Contexts"))
                .Cast<object>()
                .ToArray();
        }

        private static void RunAccounting(
            object state,
            object expansion,
            Dictionary<string, object> components)
        {
            GetStaticMethod("RecordCrossRigidIdealMergePayloadAccounting")
                .Invoke(
                    null,
                    [state, BaselineVmdPath, expansion, CreateIdealResolver(components)]);
        }

        private static long ExpectedSingleLodGeometryBytes(int partCount)
        {
            var vertexSize = VertexFactory.Create().GetVertexSize(
                VertexFormat.Weighted,
                default(RmvFileHeader).Version);
            // CreateWeightedModel: 4 vertices, 6 indices per part.
            return partCount * (vertexSize * 4L + sizeof(ushort) * 6L);
        }

        // The same WSModel set (listed in different order) is reached by
        // two roots (accounting is run twice for the army's one VMD path):
        // one payload, savings summed, geometry counted once. A
        // single-WSModel configuration that saves nothing is not a payload.
        [Test]
        public void IdealMerge_PayloadAccounting_SharedWsModelSetIsOnePayload()
        {
            var state = CreateIdealState(
                new Dictionary<string, string>
                {
                    [IdealMaterialOne] = BuildMaterialXml(
                        "shaders/weighted2_character.xml.shader", "tex/a.dds"),
                    [IdealMaterialTwo] = BuildMaterialXml(
                        "shaders/weighted2_character.xml.shader", "tex/b.dds"),
                });
            var components = CreateIdealComponents(
                (IdealWsOne, CreateIdealPart(), IdealMaterialOne),
                (IdealWsTwo, CreateIdealPart(), IdealMaterialTwo));

            RunAccounting(
                state,
                CreateExpansion(
                    CreateVisualConfiguration(1.0, IdealWsOne, IdealWsTwo)),
                components);
            RunAccounting(
                state,
                CreateExpansion(
                    CreateVisualConfiguration(0.5, IdealWsTwo, IdealWsOne),
                    CreateVisualConfiguration(0.5, IdealWsOne)),
                components);

            var payloads = GetAccountedPayloads(state);
            Assert.That(payloads, Has.Length.EqualTo(1));
            Assert.That(
                (long)ReadProperty(payloads[0], "GeometryBytes"),
                Is.EqualTo(ExpectedSingleLodGeometryBytes(2)));
            // Two parts per configuration merge into one draw at every LOD:
            // saved draws per entity = 1, occurrence probabilities 1.0 + 0.5.
            Assert.That(
                (double)ReadProperty(payloads[0], "Saving"),
                Is.EqualTo(1.5 * ExpectedForConstantDraws(state, 1)).Within(1e-9));

            var contexts = GetAccountedContexts(state);
            Assert.That(contexts, Has.Length.EqualTo(2));
            Assert.That(
                contexts.Select(context =>
                    (int)ReadProperty(context, "ConfigurationCount")),
                Is.EquivalentTo([1, 2]));
            Assert.That(
                GetStateProperty<object>(
                    state,
                    "CrossRigidIdealMergeAccounting")
                    .GetType()
                    .GetProperty("ProbabilitySumMax")!
                    .GetValue(GetStateProperty<object>(
                        state,
                        "CrossRigidIdealMergeAccounting")),
                Is.EqualTo(1.0).Within(1e-9));
        }

        // Multi-LOD case with two configurations: the payload savings sum
        // equals the exact T1 saving (baseline - T1) because the army
        // weighting is linear in per-LOD draws.
        [Test]
        public void IdealMerge_PayloadAccounting_SumsToExactT1Saving()
        {
            var state = CreateIdealState(
                new Dictionary<string, string>
                {
                    [IdealMaterialOne] = BuildMaterialXml(
                        "shaders/weighted2_character.xml.shader", "tex/a.dds"),
                    [IdealMaterialTwo] = BuildMaterialXml(
                        "shaders/weighted2_character.xml.shader", "tex/b.dds"),
                });
            var components = new Dictionary<string, object>
            {
                [IdealWsOne] = CreateBaselineComponent(
                    IdealWsOne,
                    [
                        [CreateIdealPart(), CreateIdealPart()],
                        [CreateIdealPart()],
                    ],
                    [
                        [IdealMaterialOne, IdealMaterialTwo],
                        [IdealMaterialOne],
                    ]),
                [IdealWsTwo] = CreateBaselineComponent(
                    IdealWsTwo,
                    [[CreateIdealPart()], [CreateIdealPart()]],
                    [[IdealMaterialTwo], [IdealMaterialOne]]),
            };
            var expansion = CreateExpansion(
                CreateVisualConfiguration(0.4, IdealWsOne, IdealWsTwo),
                CreateVisualConfiguration(0.6, IdealWsOne));

            var baseline = RunBaseline(state, expansion, components);
            var tiers = RunIdeal(state, expansion, components);
            RunAccounting(state, expansion, components);

            var payloads = GetAccountedPayloads(state);
            Assert.That(payloads, Has.Length.EqualTo(2));
            Assert.That(baseline - tiers[1], Is.GreaterThan(0));
            Assert.That(
                payloads.Sum(payload => (double)ReadProperty(payload, "Saving")),
                Is.EqualTo(baseline - tiers[1]).Within(1e-9));
            // WS1 has 3 parts over its two LODs, WS2 has 2.
            Assert.That(
                payloads
                    .Select(payload => (long)ReadProperty(payload, "GeometryBytes"))
                    .OrderBy(bytes => bytes),
                Is.EqualTo(
                [
                    ExpectedSingleLodGeometryBytes(3),
                    ExpectedSingleLodGeometryBytes(5),
                ]));

            // The report prints the check line and the greedy/bucket blocks.
            GetStateProperty<Dictionary<string, double>>(
                state,
                "CrossRigidBaselineExpectedDrawsByVmd")[BaselineVmdPath] = baseline;
            GetStateProperty<Dictionary<string, double[]>>(
                state,
                "CrossRigidIdealMergeExpectedDrawsByVmd")[BaselineVmdPath] = tiers;
            var lines = (string[])GetStaticMethod(
                    "BuildCrossRigidIdealMergeReportLines")
                .Invoke(null, [state, 0.0])!;
            Assert.That(
                lines,
                Has.Some.Contains("T1 hypothetical payloads: 2 distinct configuration(s)"));
            Assert.That(lines, Has.Some.Contains("payload savings = "));
            Assert.That(lines, Has.Some.Contains("growth cap 256 MiB (today's): 2 accepted"));
            Assert.That(lines, Has.Some.Contains("growth cap unlimited: 2 accepted"));
            Assert.That(lines, Has.Some.Contains("<=16 configuration(s): 1 context(s) (1 with saving)"));
            Assert.That(lines, Has.Some.Contains("state-cap proxy"));
            Assert.That(lines, Has.Some.Contains("Baked-variant estimate"));
            Assert.That(lines, Has.None.Contains("NaN"));
        }

        [Test]
        public void IdealMerge_GreedySelection_AppliesCeilingCapAndOrdering()
        {
            const long mib = 1024 * 1024;
            const double ceiling = 16.0 * mib;
            var payloads = new (string Key, double Saving, long Bytes)[]
            {
                ("a", 10, 10 * mib),  // 1 MiB per draw
                ("d", 5, 5 * mib),    // 1 MiB per draw, ties with a: key order
                ("b", 4, 8 * mib),    // 2 MiB per draw
                ("c", 1, 20 * mib),   // 20 MiB per draw: over the ceiling
                ("z", 0, 1 * mib),    // no saving
            };

            (int Accepted, double Saving, long Bytes) Select(long? capMiB)
            {
                var result = ((int, double, long))GetStaticMethod(
                        "SelectCrossRigidIdealMergePayloadsGreedy")
                    .Invoke(
                        null,
                        [
                            payloads,
                            ceiling,
                            capMiB.HasValue ? capMiB.Value * mib : (long?)null,
                        ])!;
                return result;
            }

            Assert.That(Select(null), Is.EqualTo((3, 19.0, 23 * mib)));
            // a (10) + d (5) fit in 20 MiB; b would reach 23 and is skipped.
            Assert.That(Select(20), Is.EqualTo((2, 15.0, 15 * mib)));
            // a does not fit in 5 MiB, so it is skipped and d is accepted.
            Assert.That(Select(5), Is.EqualTo((1, 5.0, 5 * mib)));
            Assert.That(Select(0), Is.EqualTo((0, 0.0, 0L)));
        }

        [Test]
        public void IdealMerge_ConfigurationCountBuckets()
        {
            int Bucket(int count)
                => (int)GetStaticMethod(
                        "GetCrossRigidIdealMergeConfigurationBucket")
                    .Invoke(null, [count])!;

            Assert.That(Bucket(1), Is.EqualTo(0));
            Assert.That(Bucket(16), Is.EqualTo(0));
            Assert.That(Bucket(17), Is.EqualTo(1));
            Assert.That(Bucket(64), Is.EqualTo(1));
            Assert.That(Bucket(65), Is.EqualTo(2));
            Assert.That(Bucket(512), Is.EqualTo(2));
            Assert.That(Bucket(513), Is.EqualTo(3));
            Assert.That(Bucket(4096), Is.EqualTo(3));
            Assert.That(Bucket(4097), Is.EqualTo(4));
            Assert.That(Bucket(16384), Is.EqualTo(4));
        }

        [Test]
        public void IdealMerge_ExpansionFailures_AreCountedByReason()
        {
            var state = CreateBareBatchState();
            var record = GetStaticMethod(
                "RecordCrossRigidIdealMergeExpansionFailure");
            record.Invoke(null, [state, "Cross-rigid VMD could not be resolved"]);
            record.Invoke(
                null,
                [
                    state,
                    "Cross-rigid attachment-local configuration limit exceeded " +
                    "(16,384 unique configurations at <root>)",
                ]);

            var accounting = ReadProperty(
                state,
                "CrossRigidIdealMergeAccounting");
            Assert.That(
                ReadProperty(accounting, "ExpansionFailureCount"),
                Is.EqualTo(2));
            Assert.That(
                ReadProperty(accounting, "ExpansionLimitFailureCount"),
                Is.EqualTo(1));
        }

        // ---- Atlas inputs / components of accepted payloads ----

        private static byte[] BuildDxt1Dds(int width, int height)
        {
            var data = BuildDxt1Dds();
            BitConverter.GetBytes(height).CopyTo(data, 12);
            BitConverter.GetBytes(width).CopyTo(data, 16);
            return data;
        }

        [Test]
        public void IdealMerge_AtlasComponents_ChainTransitivelyAndExcludeSingletons()
        {
            var groups = new[]
            {
                new[] { "a", "b" },
                new[] { "b", "c" },
                new[] { "D", "e" },
                new[] { "f" },
                new[] { "x", "X" },
                new[] { "c", "g", "a" },
            };

            var components = (List<string[]>)GetStaticMethod(
                    "BuildCrossRigidIdealMergeAtlasComponents")
                .Invoke(null, [groups])!;

            Assert.That(components, Has.Count.EqualTo(2));
            Assert.That(components[0], Is.EqualTo(new[] { "a", "b", "c", "g" }));
            Assert.That(components[1], Is.EquivalentTo(new[] { "D", "e" }));
            Assert.That(
                components.SelectMany(component => component),
                Has.None.EqualTo("f"));
        }

        [TestCase(0L, 0)]
        [TestCase(4096L * 4096, 0)]
        [TestCase(4096L * 4096 + 1, 1)]
        [TestCase(4L * 4096 * 4096, 1)]
        [TestCase(4L * 4096 * 4096 + 1, 2)]
        public void IdealMerge_PageFitClassification_Boundaries(
            long area,
            int expectedClass)
        {
            Assert.That(
                GetStaticMethod("ClassifyCrossRigidIdealMergeAtlasPageFit")
                    .Invoke(null, [area]),
                Is.EqualTo(expectedClass));
        }

        [Test]
        public void IdealMerge_WorstFitClass_TakesTheWorstComponentOfAPayload()
        {
            var worst = GetStaticMethod(
                "GetCrossRigidIdealMergeWorstFitClass");
            var classes = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["a"] = 0,
                ["b"] = 0,
                ["c"] = 2,
                ["d"] = 1,
            };

            Assert.That(
                worst.Invoke(null, [new[] { new[] { "a", "b" }, new[] { "c", "d" } }, classes]),
                Is.EqualTo(2));
            Assert.That(
                worst.Invoke(null, [new[] { new[] { "a", "b" }, new[] { "d" } }, classes]),
                Is.EqualTo(1));
            Assert.That(
                worst.Invoke(null, [new[] { new[] { "A", "b" } }, classes]),
                Is.EqualTo(0));
            // No qualifying group: the payload needs no shared atlas.
            Assert.That(
                worst.Invoke(null, [Array.Empty<string[]>(), classes]),
                Is.EqualTo(-1));
        }

        // Two accepted payloads ({One, Two} and {Three, Four}) share
        // material m2, so their components merge into one atlas of three
        // materials whose summed base-colour area decides the class.
        [TestCase(8, 8, 0)]
        [TestCase(4096, 4096, 1)]
        [TestCase(8192, 8192, 2)]
        public void IdealMerge_AtlasAnalysis_PayloadsSharingAMaterialShareAnAtlas(
            int width,
            int height,
            int expectedClass)
        {
            const string wsThree = @"models\ideal_three.wsmodel";
            const string wsFour = @"models\ideal_four.wsmodel";
            const string materialThree = @"materials\m3.material";
            var dds = BuildDxt1Dds(width, height);
            var files = new Dictionary<string, byte[]>();
            foreach (var (material, texture) in new[]
                     {
                         (IdealMaterialOne, "a"),
                         (IdealMaterialTwo, "b"),
                         (materialThree, "c"),
                     })
            {
                files[material] = System.Text.Encoding.UTF8.GetBytes(
                    BuildMaterialXml(
                        "shaders/weighted2_character.xml.shader",
                        $"tex/{texture}.dds"));
                files[$@"tex\{texture}.dds"] = dds;
            }

            var state = CreateIdealState(files);
            var components = CreateIdealComponents(
                (IdealWsOne, CreateIdealPart(), IdealMaterialOne),
                (IdealWsTwo, CreateIdealPart(), IdealMaterialTwo),
                (wsThree, CreateIdealPart(), IdealMaterialTwo),
                (wsFour, CreateIdealPart(), materialThree));
            var expansion = CreateExpansion(
                CreateVisualConfiguration(0.5, IdealWsOne, IdealWsTwo),
                CreateVisualConfiguration(0.5, wsThree, wsFour));
            RunAccounting(state, expansion, components);

            var payloads = GetAccountedPayloads(state);
            Assert.That(payloads, Has.Length.EqualTo(2));
            var payloadType = GetNestedType("CrossRigidIdealMergePayload");
            var typed = Array.CreateInstance(payloadType, payloads.Length);
            for (var index = 0; index < payloads.Length; index++)
                typed.SetValue(payloads[index], index);

            var summary = GetStaticMethod("AnalyzeCrossRigidIdealMergeAtlas")
                .Invoke(null, [state, typed])!;
            var atlasComponents = ((IEnumerable)ReadProperty(summary, "Components"))
                .Cast<object>()
                .ToArray();

            Assert.That(atlasComponents, Has.Length.EqualTo(1));
            var component = atlasComponents[0];
            Assert.That(
                ((string[])ReadProperty(component, "Materials")).Length,
                Is.EqualTo(3));
            Assert.That(ReadProperty(summary, "MaterialCount"), Is.EqualTo(3));
            Assert.That(ReadProperty(summary, "TextureCount"), Is.EqualTo(3));
            Assert.That(ReadProperty(summary, "SizedTextureCount"), Is.EqualTo(3));
            Assert.That(ReadProperty(component, "PayloadCount"), Is.EqualTo(2));
            Assert.That(
                ReadProperty(component, "BaseColourPixels"),
                Is.EqualTo(3L * width * height));
            Assert.That(ReadProperty(component, "FitClass"), Is.EqualTo(expectedClass));

            var totalSaving = payloads.Sum(payload =>
                (double)ReadProperty(payload, "Saving"));
            Assert.That(totalSaving, Is.GreaterThan(0));
            var byClass = (double[])ReadProperty(summary, "SavingByFitClass");
            for (var fit = 0; fit < 3; fit++)
            {
                Assert.That(
                    byClass[fit],
                    Is.EqualTo(fit == expectedClass ? totalSaving : 0)
                        .Within(1e-9));
            }

            Assert.That(
                (double)ReadProperty(summary, "SavingWithoutAtlas"),
                Is.EqualTo(0));

            var lines = (string[])GetStaticMethod(
                    "BuildCrossRigidIdealMergeReportLines")
                .Invoke(null, [state, 0.0])!;
            Assert.That(
                lines,
                Has.Some.Contains("atlas inputs: 3 material(s), 3 texture(s)"));
            Assert.That(
                lines,
                Has.Some.Contains("minimum shared atlases): 1, largest 3 material(s)"));
            Assert.That(lines, Has.Some.Contains("5 largest components:"));
            Assert.That(lines, Has.Some.Contains("same accepted set as the previous cap"));
        }

        // ---- engine slot-alternative limit (nesting through group VMDs) ----

        private const int SlotLimit = 5;

        private static string SlotMarker =>
            (string)GetServiceType()
                .GetField(
                    "CrossRigidWriterSlotMarkerAttribute",
                    BindingFlags.NonPublic | BindingFlags.Static)!
                .GetRawConstantValue()!;

        private static string ModelAlternative(int index)
            => $"<VARIANT_MESH model=\"variantmeshes\\m{index}.wsmodel\" />";

        private static XmlElement BuildSlot(
            IEnumerable<string> alternativeXml,
            string slotAttributes = "",
            string meshAttributes = "")
        {
            var document = new XmlDocument();
            document.LoadXml(
                $"<VARIANT_MESH{meshAttributes}><SLOT{slotAttributes}>" +
                string.Concat(alternativeXml) +
                "</SLOT></VARIANT_MESH>");
            return (XmlElement)document.DocumentElement!
                .SelectSingleNode("SLOT")!;
        }

        private static string NormalizedXml(string xml)
        {
            var probe = new XmlDocument();
            probe.LoadXml(xml);
            return probe.DocumentElement!.OuterXml;
        }

        private static string[] ModelAlternatives(int count)
            => Enumerable.Range(0, count).Select(ModelAlternative).ToArray();

        private static string HashPath(XmlDocument document)
            => "variantmeshes\\variantmeshdefinitions\\asset_editor\\cross_rigid\\group_" +
               Convert.ToHexString(
                       System.Security.Cryptography.SHA256.HashData(
                           System.Text.Encoding.UTF8.GetBytes(
                               document.OuterXml)))
                   .ToLowerInvariant() +
               ".variantmeshdefinition";

        // Returns the nesting result record, or null when unchanged.
        private static object? Nest(
            XmlElement slot,
            Dictionary<string, XmlDocument> groups,
            int limit = SlotLimit)
        {
            var register = new Func<XmlDocument, string>(document =>
            {
                var path = HashPath(document);
                groups.TryAdd(path, document);
                return path;
            });
            return GetStaticMethod("NestCrossRigidSlotAlternatives")
                .Invoke(null, [slot, limit, register]);
        }

        private sealed class SlotWalk
        {
            public List<(string Xml, double Probability)> Leaves { get; } = [];
            public List<XmlElement> Slots { get; } = [];
            public int MaxDepth { get; set; }
        }

        private static XmlElement GroupSlot(
            XmlDocument groupDocument)
        {
            Assert.That(groupDocument.DocumentElement!.Name, Is.EqualTo("VARIANT_MESH"));
            Assert.That(
                groupDocument.DocumentElement.HasAttribute("model"),
                Is.False);
            var children = groupDocument.DocumentElement
                .ChildNodes.OfType<XmlElement>().ToArray();
            Assert.That(children, Has.Length.EqualTo(1));
            Assert.That(children[0].Name, Is.EqualTo("SLOT"));
            return children[0];
        }

        // Depth-first walk that follows references into generated groups
        // and applies uniform selection at every slot.
        private static SlotWalk Walk(
            XmlElement slot,
            Dictionary<string, XmlDocument> groups)
        {
            var walk = new SlotWalk();
            WalkSlot(slot, groups, 1.0, 1, walk);
            return walk;
        }

        private static void WalkSlot(
            XmlElement slot,
            Dictionary<string, XmlDocument> groups,
            double probability,
            int depth,
            SlotWalk walk)
        {
            walk.Slots.Add(slot);
            walk.MaxDepth = Math.Max(walk.MaxDepth, depth);
            var alternatives = slot.ChildNodes.OfType<XmlElement>().ToArray();
            foreach (var alternative in alternatives)
            {
                var share = probability / alternatives.Length;
                if (alternative.Name == "VARIANT_MESH_REFERENCE" &&
                    groups.TryGetValue(
                        alternative.GetAttribute("definition"),
                        out var groupDocument))
                {
                    WalkSlot(
                        GroupSlot(groupDocument),
                        groups,
                        share,
                        depth + 1,
                        walk);
                }
                else
                {
                    walk.Leaves.Add((alternative.OuterXml, share));
                }
            }
        }

        private static int CountLeaves(
            XmlElement slot,
            Dictionary<string, XmlDocument> groups)
            => Walk(slot, groups).Leaves.Count;

        // Independent minimum of the number of factors in 2..5.
        private static int MinimalExactDepth(int count)
        {
            if (count == 1)
                return 0;
            var best = int.MaxValue;
            for (var factor = 2; factor <= SlotLimit; factor++)
            {
                if (count % factor != 0)
                    continue;
                var rest = MinimalExactDepth(count / factor);
                if (rest != int.MaxValue)
                    best = Math.Min(best, rest + 1);
            }

            return best;
        }

        private static int MinimalLevels(int count)
        {
            var levels = 1;
            long capacity = SlotLimit;
            while (capacity < count)
            {
                levels++;
                capacity *= SlotLimit;
            }

            return levels;
        }

        [Test]
        public void SlotNesting_AtOrBelowLimit_IsUnchanged(
            [Values(0, 1, 2, 3, 4, 5)] int count)
        {
            var slot = BuildSlot(
                ModelAlternatives(count),
                " name=\"head\" attach_point=\"be_prop_0\" probability=\"0.6\"");
            var before = slot.OwnerDocument.OuterXml;
            var groups = new Dictionary<string, XmlDocument>();

            var result = Nest(slot, groups);

            Assert.That(result, Is.Null);
            Assert.That(slot.OwnerDocument.OuterXml, Is.EqualTo(before));
            Assert.That(groups, Is.Empty);
        }

        [TestCase(6, 2)]
        [TestCase(8, 2)]
        [TestCase(9, 2)]
        [TestCase(10, 2)]
        [TestCase(12, 2)]
        [TestCase(16, 2)]
        [TestCase(25, 2)]
        [TestCase(30, 3)]
        [TestCase(125, 3)]
        [TestCase(4096, 6)]
        public void SlotNesting_ExactCounts_PreserveEveryLeafProbabilityAndOrder(
            int count,
            int expectedDepth)
        {
            var alternatives = ModelAlternatives(count);
            var slot = BuildSlot(alternatives);
            var groups = new Dictionary<string, XmlDocument>();

            var result = Nest(slot, groups)!;

            Assert.That(ReadProperty(result, "Exact"), Is.True);
            Assert.That(
                (int)ReadProperty(result, "OriginalAlternativeCount"),
                Is.EqualTo(count));
            Assert.That(
                (double)ReadProperty(result, "MaxRelativeProbabilityError"),
                Is.EqualTo(0.0));
            Assert.That(MinimalExactDepth(count), Is.EqualTo(expectedDepth));
            Assert.That(
                (int)ReadProperty(result, "Depth"),
                Is.EqualTo(expectedDepth));

            var walk = Walk(slot, groups);
            Assert.That(walk.MaxDepth, Is.EqualTo(expectedDepth));
            foreach (var visited in walk.Slots)
            {
                Assert.That(
                    visited.ChildNodes.OfType<XmlElement>().Count(),
                    Is.LessThanOrEqualTo(SlotLimit));
            }

            // Depth-first order is the original order, nothing lost/added.
            Assert.That(
                walk.Leaves.Select(leaf => leaf.Xml),
                Is.EqualTo(alternatives.Select(NormalizedXml)));
            Assert.That(
                walk.Leaves.Select(leaf => leaf.Probability),
                Has.All.EqualTo(1.0 / count).Within(1e-12));
        }

        [Test]
        public void SlotNesting_DuplicateAlternatives_KeepMultiplicityProbabilities()
        {
            var a = ModelAlternative(1);
            var b = ModelAlternative(2);
            var c = ModelAlternative(3);
            var slot = BuildSlot([a, a, a, b, b, c]);
            var groups = new Dictionary<string, XmlDocument>();

            Nest(slot, groups);

            var probabilityByXml = Walk(slot, groups).Leaves
                .GroupBy(leaf => leaf.Xml)
                .ToDictionary(
                    group => group.Key,
                    group => group.Sum(leaf => leaf.Probability));
            Assert.That(probabilityByXml, Has.Count.EqualTo(3));
            Assert.That(probabilityByXml[a], Is.EqualTo(1.0 / 2).Within(1e-12));
            Assert.That(probabilityByXml[b], Is.EqualTo(1.0 / 3).Within(1e-12));
            Assert.That(probabilityByXml[c], Is.EqualTo(1.0 / 6).Within(1e-12));
        }

        [TestCase(7)]
        [TestCase(11)]
        [TestCase(13)]
        [TestCase(14)]
        [TestCase(26)]
        [TestCase(1009)]
        public void SlotNesting_InexactCounts_AreBalancedAndLoseNothing(
            int count)
        {
            var alternatives = ModelAlternatives(count);
            var slot = BuildSlot(alternatives);
            var groups = new Dictionary<string, XmlDocument>();

            var result = Nest(slot, groups)!;

            Assert.That(ReadProperty(result, "Exact"), Is.False);
            var walk = Walk(slot, groups);
            Assert.That(
                (int)ReadProperty(result, "Depth"),
                Is.EqualTo(MinimalLevels(count)));
            Assert.That(walk.MaxDepth, Is.EqualTo(MinimalLevels(count)));
            Assert.That(walk.Leaves, Has.Count.EqualTo(count));
            Assert.That(
                walk.Leaves.Select(leaf => leaf.Xml),
                Is.EqualTo(alternatives.Select(NormalizedXml)));
            Assert.That(
                walk.Leaves.Sum(leaf => leaf.Probability),
                Is.EqualTo(1.0).Within(1e-12));
            foreach (var visited in walk.Slots)
            {
                var children = visited.ChildNodes.OfType<XmlElement>().ToArray();
                Assert.That(children.Length, Is.LessThanOrEqualTo(SlotLimit));
                var sizes = children
                    .Select(child => child.Name == "VARIANT_MESH_REFERENCE" &&
                                     groups.ContainsKey(
                                         child.GetAttribute("definition"))
                        ? CountLeaves(
                            GroupSlot(groups[child.GetAttribute("definition")]),
                            groups)
                        : 1)
                    .ToArray();
                Assert.That(sizes.Max() - sizes.Min(), Is.LessThanOrEqualTo(1));
            }

            var worstError = walk.Leaves.Max(
                leaf => Math.Abs(count * leaf.Probability - 1.0));
            Assert.That(
                (double)ReadProperty(result, "MaxRelativeProbabilityError"),
                Is.EqualTo(worstError).Within(1e-12));
        }

        [Test]
        public void SlotNesting_SevenAlternatives_ReportsSixthAsMaxRelativeError()
        {
            // 7 -> groups of 4 and 3 (the best of 2..5 groups): leaf
            // probabilities 1/8 and 1/6 against the wanted 1/7. The worst
            // relative error is 7/6 - 1 = 1/6 (7/8 - 1 = -1/8 is smaller).
            var slot = BuildSlot(ModelAlternatives(7));
            var groups = new Dictionary<string, XmlDocument>();

            var result = Nest(slot, groups)!;

            Assert.That(
                (double)ReadProperty(result, "MaxRelativeProbabilityError"),
                Is.EqualTo(1.0 / 6.0).Within(1e-12));
            Assert.That(
                slot.ChildNodes.OfType<XmlElement>().Count(),
                Is.EqualTo(2));
            Assert.That(
                groups.Values.Select(document =>
                    GroupSlot(document).ChildNodes.Count).Order(),
                Is.EqualTo(new[] { 3, 4 }));
        }

        [Test]
        public void SlotNesting_CopiesIdentityAttributes_AndKeepsAttachPointOnOuterSlotOnly()
        {
            var slot = BuildSlot(
                ModelAlternatives(12),
                " name=\"Head\" attach_point=\"be_prop_0\" probability=\"0.6\"" +
                " use_different_attach_point_parts=\"true\"",
                " use_different_attach_point_parts=\"true\"");
            var groups = new Dictionary<string, XmlDocument>();

            Nest(slot, groups);

            Assert.That(slot.GetAttribute("name"), Is.EqualTo("Head"));
            Assert.That(slot.GetAttribute("attach_point"), Is.EqualTo("be_prop_0"));
            Assert.That(slot.GetAttribute("probability"), Is.EqualTo("0.6"));
            Assert.That(
                slot.GetAttribute("use_different_attach_point_parts"),
                Is.EqualTo("true"));
            Assert.That(
                slot.ChildNodes.OfType<XmlElement>().Select(child => child.Name),
                Has.All.EqualTo("VARIANT_MESH_REFERENCE"));
            Assert.That(groups, Is.Not.Empty);
            foreach (var document in groups.Values)
            {
                var inner = GroupSlot(document);
                Assert.That(inner.GetAttribute("name"), Is.EqualTo("Head"));
                Assert.That(inner.HasAttribute("attach_point"), Is.False);
                Assert.That(inner.GetAttribute("probability"), Is.EqualTo("1"));
                Assert.That(
                    inner.GetAttribute("use_different_attach_point_parts"),
                    Is.EqualTo("true"));
                Assert.That(
                    document.DocumentElement!.GetAttribute(
                        "use_different_attach_point_parts"),
                    Is.EqualTo("true"));
            }
        }

        [Test]
        public void SlotNesting_IsDeterministic()
        {
            string Run(out Dictionary<string, XmlDocument> groups)
            {
                var slot = BuildSlot(
                    ModelAlternatives(30),
                    " name=\"x\" probability=\"1\"");
                groups = new Dictionary<string, XmlDocument>();
                Nest(slot, groups);
                return slot.OwnerDocument.OuterXml;
            }

            var first = Run(out var firstGroups);
            var second = Run(out var secondGroups);

            Assert.That(second, Is.EqualTo(first));
            Assert.That(
                secondGroups.ToDictionary(entry => entry.Key, entry => entry.Value.OuterXml),
                Is.EqualTo(firstGroups.ToDictionary(entry => entry.Key, entry => entry.Value.OuterXml)));
        }

        [Test]
        public void SlotNesting_UnexpectedChild_Throws()
        {
            var slot = BuildSlot(
                [.. ModelAlternatives(6), "<META_DATA>x</META_DATA>"]);

            var exception = Assert.Throws<TargetInvocationException>(
                () => Nest(slot, new Dictionary<string, XmlDocument>()));

            Assert.That(exception!.InnerException, Is.InstanceOf<InvalidOperationException>());
        }

        private static XmlDocument BuildMarkedDocument(
            int jointCount,
            int innerCount,
            int authorCount)
        {
            // A writer joint slot whose first three wrappers contain a nested
            // writer slot (as inner joints do), next to an author slot.
            var wrappers = Enumerable.Range(0, jointCount)
                .Select(index =>
                    "<VARIANT_MESH>" +
                    $"<SLOT name=\"s{index}\">" +
                    ModelAlternative(index) +
                    "</SLOT>" +
                    (index < 3
                        ? $"<SLOT {SlotMarker}=\"1\" probability=\"1\">" +
                          string.Concat(ModelAlternatives(innerCount)) +
                          "</SLOT>"
                        : string.Empty) +
                    "</VARIANT_MESH>");
            var document = new XmlDocument();
            document.LoadXml(
                "<VARIANT_MESH>" +
                $"<SLOT {SlotMarker}=\"1\" probability=\"1\">" +
                string.Concat(wrappers) +
                "</SLOT>" +
                "<SLOT name=\"author\">" +
                string.Concat(ModelAlternatives(authorCount)) +
                "</SLOT>" +
                "</VARIANT_MESH>");
            return document;
        }

        [Test]
        public void SlotNesting_Pipeline_NestsWriterSlotsOnly_ShareGroupFiles_AndStripsMarkers()
        {
            var state = CreateBareBatchState();
            var documents = GetStateProperty<Dictionary<string, XmlDocument>>(
                state,
                "ModifiedVmdDocuments");
            const string rootA = "variantmeshes\\variantmeshdefinitions\\a.variantmeshdefinition";
            const string rootB = "variantmeshes\\variantmeshdefinitions\\b.variantmeshdefinition";
            documents[rootA] = BuildMarkedDocument(12, 8, 7);
            documents[rootB] = BuildMarkedDocument(12, 8, 7);

            GetStaticMethod("NestCrossRigidWriterSlots").Invoke(null, [state]);
            GetStaticMethod("DeduplicateCrossRigidGeneratedVmdDocuments")
                .Invoke(null, [state]);

            var groupPaths = GetStateProperty<HashSet<string>>(
                state,
                "CrossRigidGeneratedGroupVmdPaths");
            var generatedPaths = GetStateProperty<HashSet<string>>(
                state,
                "CrossRigidGeneratedVmdPaths");
            // Joint (12) and inner (8) slots, 3 inner slots per root.
            Assert.That(
                GetStateProperty<int>(state, "CrossRigidNestedWriterSlotCount"),
                Is.EqualTo(8));
            Assert.That(
                GetStateProperty<int>(state, "CrossRigidMaxAlternativesBeforeNesting"),
                Is.EqualTo(12));
            Assert.That(
                GetStateProperty<int>(state, "CrossRigidMaxSlotNestingDepth"),
                Is.EqualTo(2));
            Assert.That(
                GetStateProperty<IList>(state, "CrossRigidInexactSlotNestings"),
                Is.Empty);
            Assert.That(groupPaths, Is.Not.Empty);
            Assert.That(groupPaths.IsSubsetOf(generatedPaths), Is.True);
            Assert.That(
                GetStateProperty<int>(state, "CrossRigidGroupVmdFilesRequested"),
                Is.GreaterThan(groupPaths.Count));
            Assert.That(
                documents.Keys.Where(path => path.Contains("group_")).ToHashSet(),
                Is.EquivalentTo(groupPaths));

            // The writer slots respect the limit, the author slot is as written.
            GetStaticMethod("AssertCrossRigidWriterSlotAlternativeLimit")
                .Invoke(null, [documents, SlotLimit]);
            foreach (var root in new[] { rootA, rootB })
            {
                var author = (XmlElement)documents[root].SelectSingleNode(
                    "/VARIANT_MESH/SLOT[@name='author']")!;
                Assert.That(
                    author.ChildNodes.OfType<XmlElement>().Count(),
                    Is.EqualTo(7));
                Assert.That(
                    documents[root].SelectSingleNode(
                        $"/VARIANT_MESH/SLOT[@{SlotMarker}]")!
                        .ChildNodes.OfType<XmlElement>().Count(),
                    Is.LessThanOrEqualTo(SlotLimit));
            }

            // Every reference to a generated group resolves; the identical
            // roots reference the same group files.
            foreach (var document in documents.Values)
            {
                foreach (var reference in document
                             .SelectNodes("//VARIANT_MESH_REFERENCE")!
                             .OfType<XmlElement>())
                {
                    Assert.That(
                        documents.ContainsKey(reference.GetAttribute("definition")),
                        Is.True);
                }
            }

            Assert.That(
                documents[rootA].SelectSingleNode(
                    $"/VARIANT_MESH/SLOT[@{SlotMarker}]")!.OuterXml,
                Is.EqualTo(documents[rootB].SelectSingleNode(
                    $"/VARIANT_MESH/SLOT[@{SlotMarker}]")!.OuterXml));

            GetStaticMethod("StripCrossRigidWriterSlotMarkers")
                .Invoke(null, [documents.Values.ToList()]);
            Assert.That(
                documents.Values.Select(document => document.OuterXml),
                Has.None.Contains(SlotMarker));
        }

        [Test]
        public void SlotLimitGuard_WriterSlotWithSixAlternatives_Throws()
        {
            var document = new XmlDocument();
            document.LoadXml(
                $"<VARIANT_MESH><SLOT {SlotMarker}=\"1\">" +
                string.Concat(ModelAlternatives(6)) +
                "</SLOT></VARIANT_MESH>");
            var documents = new Dictionary<string, XmlDocument>
            {
                ["variantmeshes\\variantmeshdefinitions\\bad.variantmeshdefinition"] = document,
            };

            var exception = Assert.Throws<TargetInvocationException>(
                () => GetStaticMethod("AssertCrossRigidWriterSlotAlternativeLimit")
                    .Invoke(null, [documents, SlotLimit]));

            Assert.That(exception!.InnerException, Is.InstanceOf<InvalidOperationException>());
            Assert.That(
                exception.InnerException!.Message,
                Does.Contain("bad.variantmeshdefinition").And.Contain("6"));
        }

        [Test]
        public void SlotLimitGuard_FiveWriterAlternativesAndOversizedAuthorSlot_DoNotThrow()
        {
            var document = new XmlDocument();
            document.LoadXml(
                $"<VARIANT_MESH><SLOT {SlotMarker}=\"1\">" +
                string.Concat(ModelAlternatives(5)) +
                "</SLOT><SLOT name=\"author\">" +
                string.Concat(ModelAlternatives(9)) +
                "</SLOT></VARIANT_MESH>");
            var documents = new Dictionary<string, XmlDocument> { ["x"] = document };

            Assert.DoesNotThrow(
                () => GetStaticMethod("AssertCrossRigidWriterSlotAlternativeLimit")
                    .Invoke(null, [documents, SlotLimit]));
        }

        // Round trip through the tool's own attachment-local expansion.

        private const string RoundTripBefore =
            "variantmeshes\\variantmeshdefinitions\\before.variantmeshdefinition";
        private const string RoundTripAfter =
            "variantmeshes\\variantmeshdefinitions\\after.variantmeshdefinition";

        private static string WriterJointVmd(int heads, int weapons)
        {
            var wrappers = new List<string>();
            for (var head = 0; head < heads; head++)
            {
                // weapon 0 is the absent state of an optional slot.
                for (var weapon = 0; weapon <= weapons; weapon++)
                {
                    wrappers.Add(
                        "<VARIANT_MESH>" +
                        "<SLOT name=\"head\" attach_point=\"be_head\" probability=\"1\">" +
                        $"<VARIANT_MESH model=\"variantmeshes\\head_{head}.wsmodel\" />" +
                        "</SLOT>" +
                        (weapon == 0
                            ? string.Empty
                            : "<SLOT name=\"weapon\" attach_point=\"be_prop_0\" probability=\"1\">" +
                              $"<VARIANT_MESH model=\"variantmeshes\\weapon_{weapon}.wsmodel\" />" +
                              "</SLOT>") +
                        "</VARIANT_MESH>");
                }
            }

            return "<VARIANT_MESH><SLOT probability=\"1\">" +
                   string.Concat(wrappers) +
                   "</SLOT></VARIANT_MESH>";
        }

        private static object CreateRoundTripState(
            Dictionary<string, string> vmds,
            IEnumerable<string> wsModels)
        {
            var files = new Dictionary<string, Shared.Core.PackFiles.Models.PackFile>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var (path, xml) in vmds)
            {
                files[path] = Shared.Core.PackFiles.Models.PackFile.CreateFromBytes(
                    System.IO.Path.GetFileName(path),
                    System.Text.Encoding.UTF8.GetBytes(xml));
            }

            var known = new HashSet<string>(wsModels, StringComparer.OrdinalIgnoreCase);
            var source = new Moq.Mock<Shared.Core.PackFiles.Models.IPackFileContainer>();
            source.Setup(container => container.ContainsFile(Moq.It.IsAny<string>()))
                .Returns((string path) => files.ContainsKey(path) || known.Contains(path));
            source.Setup(container => container.FindFile(Moq.It.IsAny<string>()))
                .Returns((string path) => files.GetValueOrDefault(path));
            var packFileService = new Moq.Mock<Editors.PackOptimizer.Core.IPackOptimizerPackFiles>();
            packFileService.Setup(service => service.GetAllPackfileContainers())
                .Returns([]);
            var batchStateType = GetNestedType("BatchState");
            var constructor = batchStateType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single();
            return constructor.Invoke(
            [
                source.Object,
                source.Object,
                packFileService.Object,
                string.Empty,
                string.Empty,
                string.Empty,
                false,
                false,
                false,
            ]);
        }

        // attachment -> (rendered model set -> probability)
        private static Dictionary<string, Dictionary<string, double>> ExpandWithTool(
            object state,
            string vmdPath)
        {
            var resultType = GetNestedType("CrossRigidAttachmentLocalExpansionResult");
            var cache = (IDictionary)Activator.CreateInstance(
                typeof(Dictionary<,>).MakeGenericType(typeof(string), resultType))!;
            var result = GetStaticMethod(
                    "GetCrossRigidAttachmentLocalConfigurationsForVmd")
                .Invoke(null, [state, vmdPath, cache, new HashSet<string>()])!;
            Assert.That(ReadProperty(result, "FailureReason") as string, Is.Null);

            var byAttachment = new Dictionary<string, Dictionary<string, double>>();
            foreach (var entry in (IEnumerable)ReadProperty(
                         result,
                         "ConfigurationsByAttachment"))
            {
                var key = (string)ReadProperty(entry, "Key");
                var totals = new Dictionary<string, double>();
                foreach (var configuration in (IEnumerable)ReadProperty(entry, "Value"))
                {
                    var signature = string.Join(
                        "|",
                        ((IEnumerable)ReadProperty(configuration, "Instances"))
                        .Cast<object>()
                        .Select(instance => (string)ReadProperty(instance, "WsModelPath"))
                        .Order());
                    totals[signature] = totals.GetValueOrDefault(signature) +
                                        (double)ReadProperty(configuration, "Probability");
                }

                byAttachment[key] = totals;
            }

            return byAttachment;
        }

        private static string StripNestingSegments(string attachment)
            => attachment.Replace(
                "/attach=<inherit>/mesh_parts=<default>/slot_parts=<default>",
                string.Empty);

        [TestCase(3, 3)]
        [TestCase(5, 5)]
        public void SlotNesting_RoundTripThroughToolExpansion_KeepsConfigurationsAndProbabilities(
            int heads,
            int weapons)
        {
            var beforeXml = WriterJointVmd(heads, weapons);
            var beforeDocument = new XmlDocument();
            beforeDocument.LoadXml(beforeXml);
            var slot = (XmlElement)beforeDocument.DocumentElement!.SelectSingleNode("SLOT")!;
            var groups = new Dictionary<string, XmlDocument>();
            Assert.That(slot.ChildNodes.Count, Is.EqualTo(heads * (weapons + 1)));
            Nest(slot, groups);
            Assert.That(groups, Is.Not.Empty);

            var vmds = new Dictionary<string, string>
            {
                [RoundTripBefore] = beforeXml,
                [RoundTripAfter] = beforeDocument.OuterXml,
            };
            foreach (var (path, document) in groups)
                vmds[path] = document.OuterXml;
            var wsModels = Enumerable.Range(0, heads)
                .Select(index => $"variantmeshes\\head_{index}.wsmodel")
                .Concat(Enumerable.Range(1, weapons)
                    .Select(index => $"variantmeshes\\weapon_{index}.wsmodel"));
            var state = CreateRoundTripState(vmds, wsModels);

            var before = ExpandWithTool(state, RoundTripBefore);
            var after = ExpandWithTool(state, RoundTripAfter);

            // The reference hop adds the child slot's own identity segment
            // to every attachment identity, identically for all groups and
            // nesting levels; disregarding it the expansions are equal.
            Assert.That(
                after.Keys.Except(before.Keys),
                Is.Not.Empty,
                "nested identities are expected to carry the extra slot segment");
            var normalizedBefore = before.ToDictionary(
                entry => StripNestingSegments(entry.Key),
                entry => entry.Value);
            var normalizedAfter = after.ToDictionary(
                entry => StripNestingSegments(entry.Key),
                entry => entry.Value);
            Assert.That(
                normalizedAfter.Keys,
                Is.EquivalentTo(normalizedBefore.Keys));
            Assert.That(normalizedBefore, Has.Count.EqualTo(2));
            foreach (var (attachment, expected) in normalizedBefore)
            {
                var actual = normalizedAfter[attachment];
                Assert.That(actual.Keys, Is.EquivalentTo(expected.Keys));
                Assert.That(expected.Values.Sum(), Is.EqualTo(1.0).Within(1e-12));
                foreach (var (signature, probability) in expected)
                {
                    Assert.That(
                        actual[signature],
                        Is.EqualTo(probability).Within(1e-12),
                        $"{attachment} {signature}");
                }
            }

            // Hand check: the head attachment sees each head with 1/heads.
            var headAttachment = normalizedBefore.Keys.Single(
                key => key.Contains("attach=be_head"));
            Assert.That(
                normalizedAfter[headAttachment]["variantmeshes\\head_0.wsmodel"],
                Is.EqualTo(1.0 / heads).Within(1e-12));
            var weaponAttachment = normalizedBefore.Keys.Single(
                key => key.Contains("attach=be_prop_0"));
            Assert.That(
                normalizedAfter[weaponAttachment][string.Empty],
                Is.EqualTo(1.0 / (weapons + 1)).Within(1e-12));
        }

        // ---- No-atlas run (buildAtlases: false) ---------------------------
        //
        // Process() itself needs a healthy WH3 DB resolution and real pack
        // containers, so these tests drive the stages a no-atlas run executes
        // (structural analysis, MergeCompatibleMeshes, geometry optimization
        // and the geometry selection) on a BatchState that has no atlas data.

        private const string NoAtlasRigidPath =
            "variantmeshes\\test\\pack_a.rigid_model_v2";
        private const string NoAtlasWsModelPath =
            "variantmeshes\\test\\pack_a.wsmodel";
        private const BindingFlags AnyInstance =
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic;

        private static void SetStateProperty(
            object state,
            string name,
            object? value)
        {
            state.GetType()
                .GetProperty(name, AnyInstance)!
                .SetValue(state, value);
        }

        private static object CreateNoAtlasState(
            Func<string, bool>? sourceContains = null,
            string reportPath = "",
            bool buildAtlases = false)
        {
            var constructor = GetNestedType("BatchState")
                .GetConstructors(AnyInstance)
                .Single();
            var source = new Moq.Mock<Shared.Core.PackFiles.Models.IPackFileContainer>();
            source
                .Setup(container => container.ContainsFile(Moq.It.IsAny<string>()))
                .Returns<string>(path => sourceContains?.Invoke(path) ?? true);
            var output = new Moq.Mock<Shared.Core.PackFiles.Models.IPackFileContainer>();
            var packFileService = new Moq.Mock<Editors.PackOptimizer.Core.IPackOptimizerPackFiles>();
            var state = constructor.Invoke(
                [
                    source.Object,
                    output.Object,
                    packFileService.Object,
                    string.Empty,
                    string.Empty,
                    reportPath,
                    true,
                    true,
                    true,
                ]);
            SetStateProperty(state, "StructuralMergeConsumerDiscoveryComplete", true);
            SetStateProperty(state, "BuildAtlases", buildAtlases);
            return state;
        }

        private static object CreateService()
        {
            return Activator.CreateInstance(
                GetServiceType(),
                [
                    new Moq.Mock<Editors.PackOptimizer.Core.IPackOptimizerPackFiles>().Object, null,
                ])!;
        }

        private static void InvokeServiceMethod(
            object service,
            string name,
            params object?[] arguments)
        {
            GetServiceType()
                .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, arguments);
        }

        private static XmlDocument CreateWsModelDocument(
            string rigidPath,
            params string[][] materialsByLod)
        {
            var xml = new System.Text.StringBuilder();
            xml.Append("<model><geometry>").Append(rigidPath).Append("</geometry><materials>");
            for (var lod = 0; lod < materialsByLod.Length; lod++)
            {
                for (var part = 0; part < materialsByLod[lod].Length; part++)
                {
                    xml.Append($"<material lod_index=\"{lod}\" part_index=\"{part}\">")
                        .Append(materialsByLod[lod][part])
                        .Append("</material>");
                }
            }

            xml.Append("</materials></model>");
            var document = new XmlDocument();
            document.LoadXml(xml.ToString());
            return document;
        }

        private static void AddNoAtlasRigid(
            object state,
            string rigidPath,
            string wsModelPath,
            RmvFile rigid,
            params string[][] materialsByLod)
        {
            GetStateProperty<IDictionary>(state, "RigidModels")[rigidPath] = rigid;
            GetStateProperty<IDictionary>(state, "WsDocuments")[wsModelPath] =
                CreateWsModelDocument(rigidPath, materialsByLod);
        }

        private static object CreateMeshKey(string rigidPath, int lod, int part)
        {
            return GetNestedType("MeshKey")
                .GetConstructors(AnyInstance)
                .Single()
                .Invoke([rigidPath, lod, part]);
        }

        // The stages a no-atlas run executes before and during merging.
        private static void RunNoAtlasMergeStages(object state)
        {
            var service = CreateService();
            InvokeServiceMethod(service, "AnalyzeCompatibleMeshMerges", state, CancellationToken.None, null);
            InvokeServiceMethod(service, "MergeCompatibleMeshes", state, CancellationToken.None, null);
        }

        private static string[][] ReadWsAssignments(XmlDocument document, RmvFile rigid)
        {
            var arguments = new object?[] { document, rigid, null, null, false };
            var succeeded = (bool)GetStaticMethod("TryReadWsMaterialAssignments")
                .Invoke(null, arguments)!;
            Assert.That(succeeded, Is.True, (string?)arguments[3]);
            return (string[][])arguments[2]!;
        }

        [Test]
        public void NoAtlas_StructuralPair_IsMergedWithoutAtlasArtifacts()
        {
            var state = CreateNoAtlasState();
            var rigid = CreateSingleLodRigid(
                RmvVersionEnum.RMV2_V8,
                CreateWeightedModel(1f),
                CreateWeightedModel(2f));
            AddNoAtlasRigid(
                state,
                NoAtlasRigidPath,
                NoAtlasWsModelPath,
                rigid,
                ["materials\\shared.material", "materials\\shared.material"]);

            RunNoAtlasMergeStages(state);

            Assert.That(rigid.ModelList[0], Has.Length.EqualTo(1));
            Assert.That(rigid.ModelList[0][0].Mesh.VertexList, Has.Length.EqualTo(8));
            Assert.That(
                rigid.ModelList[0][0].Mesh.IndexList,
                Is.EqualTo(new ushort[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 }));
            Assert.That(GetStateProperty<int>(state, "StructuralOnlyMergeGroupCount"), Is.EqualTo(1));
            Assert.That(GetStateProperty<int>(state, "StructuralOnlyMergePartsRemoved"), Is.EqualTo(1));
            Assert.That(
                GetStateProperty<int>(state, "StructuralAndAtlasAssistedMergeGroupCount"),
                Is.Zero);
            Assert.That(GetStateProperty<int>(state, "AtlasAssistedMergeGroupCount"), Is.Zero);
            Assert.That(GetStateProperty<int>(state, "AtlasAssistedMergePartsRemoved"), Is.Zero);
            Assert.That(GetStateProperty<int>(state, "MeshPartsBeforeMerging"), Is.EqualTo(2));
            Assert.That(GetStateProperty<int>(state, "MeshPartsAfterMerging"), Is.EqualTo(1));

            Assert.That(GetStateProperty<IDictionary>(state, "AtlasBatchByMesh"), Is.Empty);
            Assert.That(GetStateProperty<IEnumerable>(state, "GeneratedTexturePaths"), Is.Empty);
            Assert.That(GetStateProperty<IEnumerable>(state, "GeneratedMaterialPaths"), Is.Empty);
            Assert.That(GetStateProperty<IEnumerable>(state, "ProcessedMeshes"), Is.Empty);
            Assert.That(
                GetStateProperty<HashSet<string>>(state, "ModifiedRigids"),
                Is.EquivalentTo(new[] { NoAtlasRigidPath }));
            Assert.That(
                GetStateProperty<HashSet<string>>(state, "ModifiedWsModels"),
                Is.EquivalentTo(new[] { NoAtlasWsModelPath }));

            // The rewritten WSModel table matches the merged rigid and the rigid
            // survives the serialize/reload round trip the output validation uses.
            var wsDocument = (XmlDocument)GetStateProperty<IDictionary>(state, "WsDocuments")[
                NoAtlasWsModelPath]!;
            Assert.That(
                ReadWsAssignments(wsDocument, rigid)[0],
                Is.EqualTo(new[] { "materials\\shared.material" }));
            rigid.RecalculateOffsets();
            var bytes = ModelFactory.Create().Save(
                rigid,
                validateByReloading: true,
                logProgress: false);
            Assert.That(ModelFactory.Create().Load(bytes).ModelList[0], Has.Length.EqualTo(1));
        }

        // Two parts whose materials differ can only be combined once atlasing
        // has pointed both at one generated material and one atlas batch.
        [Test]
        public void NoAtlas_PartsThatNeedAtlasing_StayUnmerged()
        {
            var state = CreateNoAtlasState();
            var rigid = CreateSingleLodRigid(
                RmvVersionEnum.RMV2_V8,
                CreateWeightedModel(1f),
                CreateWeightedModel(2f));
            AddNoAtlasRigid(
                state,
                NoAtlasRigidPath,
                NoAtlasWsModelPath,
                rigid,
                ["materials\\skin_a.material", "materials\\skin_b.material"]);

            RunNoAtlasMergeStages(state);

            Assert.That(rigid.ModelList[0], Has.Length.EqualTo(2));
            Assert.That(GetStateProperty<int>(state, "StructuralOnlyMergeGroupCount"), Is.Zero);
            Assert.That(GetStateProperty<int>(state, "AtlasAssistedMergeGroupCount"), Is.Zero);
            Assert.That(GetStateProperty<HashSet<string>>(state, "ModifiedRigids"), Is.Empty);
        }

        [Test]
        public void AtlasState_SamePartsThatNeedAtlasing_AreMergedAsAtlasAssisted()
        {
            var state = CreateNoAtlasState(buildAtlases: true);
            var rigid = CreateSingleLodRigid(
                RmvVersionEnum.RMV2_V8,
                CreateWeightedModel(1f),
                CreateWeightedModel(2f));
            AddNoAtlasRigid(
                state,
                NoAtlasRigidPath,
                NoAtlasWsModelPath,
                rigid,
                ["materials\\skin_a.material", "materials\\skin_b.material"]);
            var service = CreateService();
            InvokeServiceMethod(service, "AnalyzeCompatibleMeshMerges", state, CancellationToken.None, null);

            // What atlasing does to this rigid: one atlas batch, one generated material.
            var batches = GetStateProperty<IDictionary>(state, "AtlasBatchByMesh");
            batches[CreateMeshKey(NoAtlasRigidPath, 0, 0)] = 7;
            batches[CreateMeshKey(NoAtlasRigidPath, 0, 1)] = 7;
            var wsDocument = (XmlDocument)GetStateProperty<IDictionary>(state, "WsDocuments")[
                NoAtlasWsModelPath]!;
            foreach (XmlNode node in wsDocument.SelectNodes("/model/materials/material")!)
                node.InnerText = "atlas\\generated.material";
            InvokeServiceMethod(service, "MergeCompatibleMeshes", state, CancellationToken.None, null);

            Assert.That(rigid.ModelList[0], Has.Length.EqualTo(1));
            Assert.That(GetStateProperty<int>(state, "AtlasAssistedMergeGroupCount"), Is.EqualTo(1));
            Assert.That(GetStateProperty<int>(state, "AtlasAssistedMergePartsRemoved"), Is.EqualTo(1));
            Assert.That(GetStateProperty<int>(state, "StructuralOnlyMergeGroupCount"), Is.Zero);
            Assert.That(GetStateProperty<int>(state, "StructuralOnlyMergePartsRemoved"), Is.Zero);
        }

        private static string WriteNoAtlasReport(bool buildAtlases)
        {
            var reportPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"noatlas_report_{Guid.NewGuid():N}.txt");
            try
            {
                var state = CreateNoAtlasState(
                    reportPath: reportPath,
                    buildAtlases: buildAtlases);
                var phases = GetStateProperty<IDictionary>(state, "PhaseDurations");
                phases["Merge compatible meshes"] = TimeSpan.FromSeconds(1);
                GetStaticMethod("WriteReport").Invoke(
                    null,
                    [state, new List<string>(), true, null]);
                return System.IO.File.ReadAllText(reportPath);
            }
            finally
            {
                System.IO.File.Delete(reportPath);
            }
        }

        [Test]
        public void Report_NoAtlasRun_OmitsAtlasSectionsAndKeepsTheRest()
        {
            var report = WriteNoAtlasReport(buildAtlases: false);

            Assert.That(report, Does.Contain("Texture atlasing: NO"));
            Assert.That(
                report,
                Does.Contain("Texture atlasing disabled; atlas sections omitted."));
            Assert.That(report, Does.Not.Contain("Atlas value-gate"));
            Assert.That(report, Does.Not.Contain("Atlas textures generated"));
            Assert.That(report, Does.Not.Contain("Atlas batches generated"));
            Assert.That(report, Does.Not.Contain("LOD-aware atlas"));
            Assert.That(report, Does.Contain("Cross-rigid merges: NO"));
            Assert.That(report, Does.Contain("Modified rigid meshes: 0"));
            Assert.That(report, Does.Contain("Structural-only merge groups applied: 0"));
            Assert.That(report, Does.Contain("Mesh parts removed by structural-only merges: 0"));
            Assert.That(report, Does.Not.Contain("Mesh parts removed by atlas-assisted merges"));
            Assert.That(report, Does.Contain("Phase timings"));
            Assert.That(report, Does.Contain("Merge compatible meshes"));
            Assert.That(report, Does.Contain("Validation"));
        }

        [Test]
        public void Report_AtlasRun_KeepsAtlasSections()
        {
            var report = WriteNoAtlasReport(buildAtlases: true);

            Assert.That(report, Does.Contain("Texture atlasing: YES"));
            Assert.That(report, Does.Not.Contain("atlas sections omitted"));
            Assert.That(report, Does.Contain("Atlas value-gate batches accepted: 0"));
            Assert.That(report, Does.Contain("Atlas textures generated: 0"));
            Assert.That(report, Does.Contain("Mesh parts removed by atlas-assisted merges: 0"));
        }

        [Test]
        public void Report_GeometrySummaryLine_HandlesNoData()
        {
            var reportPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"geometry_report_{Guid.NewGuid():N}.txt");
            try
            {
                var constructor = GetNestedType("BatchState")
                    .GetConstructors(AnyInstance)
                    .Single();
                var container = new Moq.Mock<Shared.Core.PackFiles.Models.IPackFileContainer>().Object;
                var state = constructor.Invoke(
                    [
                        container,
                        container,
                        new Moq.Mock<Editors.PackOptimizer.Core.IPackOptimizerPackFiles>().Object,
                        string.Empty,
                        string.Empty,
                        reportPath,
                        false,
                        false,
                        true,
                    ]);
                GetStaticMethod("WriteReport").Invoke(
                    null,
                    [state, new List<string>(), true, null]);
                var report = System.IO.File.ReadAllText(reportPath);

                // The report uses the current culture for numbers (decimal separator).
                Assert.That(
                    report,
                    Does.Match(
                        @"Geometry optimisation: 0 rigid\(s\), vertices 0 -> 0 \(0[.,]0 %\), " +
                        @"indices 0 -> 0, geometry bytes 0 -> 0"));
                Assert.That(report, Does.Not.Contain("NaN"));
            }
            finally
            {
                System.IO.File.Delete(reportPath);
            }
        }

        private static RmvModel CreateOptimizableModel(
            ModelMaterialEnum modelType = ModelMaterialEnum.weighted)
        {
            // Four unique vertices plus an exact duplicate of vertex 0 and an
            // unreferenced vertex; two triangles reference the duplicate.
            var model = CreateWeightedModel(1f);
            var vertices = model.Mesh.VertexList.ToList();
            vertices.Add(vertices[0]);
            vertices.Add(CreateWeightedModel(9f).Mesh.VertexList[2]);
            model.Mesh.VertexList = vertices.ToArray();
            model.Mesh.IndexList = [0, 1, 2, 4, 2, 3];
            var header = model.CommonHeader;
            header.ModelTypeFlag = modelType;
            model.CommonHeader = header;
            return model;
        }

        private static object CreateDependencyIndex()
        {
            return Activator.CreateInstance(
                GetNestedType("GameplayMeshDependencyIndex"),
                AnyInstance,
                null,
                [false],
                null)!;
        }

        private static void InvokeIndex(object index, string name, params object[] arguments)
        {
            index.GetType().GetMethod(name, AnyInstance)!.Invoke(index, arguments);
        }

        // The geometry-optimization set of a no-atlas run: source-owned rigids
        // reached by a root in scope, with the structural merge's consumer gate.
        [Test]
        public void NoAtlasGeometrySelection_OnlySelectsOwnedInScopeSafeRigids()
        {
            const string scopeRoot = "models\\scope.variantmeshdefinition";
            const string otherRoot = "models\\other.variantmeshdefinition";
            const string vanillaWsModel = "variantmeshes\\vanilla\\ca.wsmodel";
            string Rigid(string name) => $"variantmeshes\\test\\{name}.rigid_model_v2";

            var vanillaOwned = Rigid("vanilla_owned");
            var state = CreateNoAtlasState(
                sourceContains: path =>
                    !path.Equals(vanillaOwned, StringComparison.OrdinalIgnoreCase) &&
                    !path.Equals(vanillaWsModel, StringComparison.OrdinalIgnoreCase));
            var rigids = new Dictionary<string, RmvFile>
            {
                [Rigid("owned_in_scope")] = CreateSingleLodRigid(
                    RmvVersionEnum.RMV2_V8, CreateOptimizableModel()),
                [Rigid("owned_out_of_scope")] = CreateSingleLodRigid(
                    RmvVersionEnum.RMV2_V8, CreateOptimizableModel()),
                [vanillaOwned] = CreateSingleLodRigid(
                    RmvVersionEnum.RMV2_V8, CreateOptimizableModel()),
                [Rigid("immutable_consumer")] = CreateSingleLodRigid(
                    RmvVersionEnum.RMV2_V8, CreateOptimizableModel()),
                [Rigid("cloth")] = CreateSingleLodRigid(
                    RmvVersionEnum.RMV2_V8,
                    CreateOptimizableModel(ModelMaterialEnum.weighted_cloth)),
                [Rigid("bad_indices")] = CreateSingleLodRigid(
                    RmvVersionEnum.RMV2_V8, CreateOptimizableModel()),
                [Rigid("already_merged")] = CreateSingleLodRigid(
                    RmvVersionEnum.RMV2_V8, CreateOptimizableModel()),
            };
            rigids[Rigid("bad_indices")].ModelList[0][0].Mesh.IndexList = [0, 1, 99];
            var rigidModels = GetStateProperty<IDictionary>(state, "RigidModels");
            foreach (var (path, rigid) in rigids)
                rigidModels[path] = rigid;

            var index = CreateDependencyIndex();
            foreach (var path in rigids.Keys.Where(path => path != Rigid("owned_out_of_scope")))
                InvokeIndex(index, "AddRootReference", path, scopeRoot);
            InvokeIndex(index, "AddRootReference", Rigid("owned_out_of_scope"), otherRoot);
            InvokeIndex(index, "AddWsModelConsumer", Rigid("immutable_consumer"), vanillaWsModel);
            SetStateProperty(state, "GameplayMeshDependencyIndex", index);
            GetStateProperty<HashSet<string>>(state, "ModifiedRigids").Add(Rigid("already_merged"));

            var service = CreateService();
            var selected = (HashSet<string>)GetServiceType()
                .GetMethod("SelectNoAtlasGeometryOptimizationRigids", AnyInstance)!
                .Invoke(
                    service,
                    [state, new List<string> { scopeRoot }, CancellationToken.None])!;

            Assert.That(
                selected,
                Is.EquivalentTo(new[]
                {
                    Rigid("owned_in_scope"),
                    Rigid("already_merged"),
                }));
            var skips = GetStateProperty<Dictionary<string, int>>(
                state,
                "GeometrySelectionSkipCounts");
            Assert.That(skips["immutable gameplay WSModel consumer"], Is.EqualTo(1));
            Assert.That(skips["cloth model type"], Is.EqualTo(1));
            Assert.That(skips["invalid index buffer"], Is.EqualTo(1));
            Assert.That(skips, Has.Count.EqualTo(3));
            Assert.That(
                GetStateProperty<int>(state, "GeometryUnmodifiedRigidsSelected"),
                Is.EqualTo(1));

            var vanillaVertices = rigids[vanillaOwned].ModelList[0][0].Mesh.VertexList;
            var vanillaIndices = rigids[vanillaOwned].ModelList[0][0].Mesh.IndexList.ToArray();
            GetServiceType()
                .GetMethod("OptimizeGeometry", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [state, CancellationToken.None, null, selected]);

            // Only the selection is written; nothing else is touched or marked modified.
            Assert.That(
                GetStateProperty<HashSet<string>>(state, "ModifiedRigids"),
                Is.EquivalentTo(selected));
            Assert.That(
                rigids[vanillaOwned].ModelList[0][0].Mesh.VertexList,
                Is.SameAs(vanillaVertices));
            Assert.That(rigids[vanillaOwned].ModelList[0][0].Mesh.IndexList, Is.EqualTo(vanillaIndices));
            Assert.That(
                rigids[Rigid("owned_out_of_scope")].ModelList[0][0].Mesh.VertexList,
                Has.Length.EqualTo(6));
            Assert.That(
                rigids[Rigid("cloth")].ModelList[0][0].Mesh.VertexList,
                Has.Length.EqualTo(6));

            // The selected rigid keeps its part count and triangle count; the
            // duplicate and unreferenced vertices are gone.
            var optimized = rigids[Rigid("owned_in_scope")];
            Assert.That(optimized.ModelList[0], Has.Length.EqualTo(1));
            Assert.That(optimized.ModelList[0][0].Mesh.IndexList, Has.Length.EqualTo(6));
            Assert.That(optimized.ModelList[0][0].Mesh.VertexList, Has.Length.EqualTo(4));
            Assert.That(GetStateProperty<int>(state, "GeometryRigidsOptimized"), Is.EqualTo(2));
            Assert.That(GetStateProperty<long>(state, "GeometryVerticesBefore"), Is.EqualTo(12));
            Assert.That(GetStateProperty<long>(state, "GeometryVerticesAfter"), Is.EqualTo(8));
            Assert.That(GetStateProperty<long>(state, "GeometryIndicesBefore"), Is.EqualTo(12));
            Assert.That(GetStateProperty<long>(state, "GeometryIndicesAfter"), Is.EqualTo(12));
            Assert.That(
                GetStateProperty<long>(state, "GeometryBytesBefore"),
                Is.GreaterThan(GetStateProperty<long>(state, "GeometryBytesAfter")));
        }

        [Test]
        public void NoAtlasGeometrySelection_WithoutDependencyIndex_SelectsNothing()
        {
            var state = CreateNoAtlasState();
            var service = CreateService();

            var selected = (HashSet<string>)GetServiceType()
                .GetMethod("SelectNoAtlasGeometryOptimizationRigids", AnyInstance)!
                .Invoke(
                    service,
                    [state, new List<string> { "models\\scope.variantmeshdefinition" }, CancellationToken.None])!;

            Assert.That(selected, Is.Empty);
        }

        // ---- LOD audit (report only) ---------------------------------------

        private static RmvModel CreateAuditPart(int vertexCount, int triangleCount)
        {
            return new RmvModel
            {
                Mesh = new RmvMesh
                {
                    VertexList = new CommonVertex[vertexCount],
                    IndexList = new ushort[triangleCount * 3],
                },
            };
        }

        // One part per LOD unless a LOD lists several (vertices, triangles) parts.
        private static RmvFile CreateAuditRigid(
            float[] distances,
            byte[]? qualities,
            params (int Vertices, int Triangles)[][] parts)
        {
            return new RmvFile
            {
                LodHeaders = distances
                    .Select((distance, index) =>
                        (RmvLodHeader)new Rmv2LodHeader_V7_V8
                        {
                            _lodCameraDistance = distance,
                            _qualityLvl = qualities?[index] ?? 0,
                        })
                    .ToArray(),
                ModelList = parts
                    .Select(lod => lod
                        .Select(part => CreateAuditPart(part.Vertices, part.Triangles))
                        .ToArray())
                    .ToArray(),
            };
        }

        private static RmvFile CreateTriangleRigid(
            float[] distances,
            params int[] trianglesPerLod)
        {
            return CreateAuditRigid(
                distances,
                null,
                trianglesPerLod.Select(triangles => new[] { (triangles + 2, triangles) }).ToArray());
        }

        private static object BuildAuditRigid(string path, bool sourceOwned, RmvFile rigid)
        {
            return GetStaticMethod("BuildLodAuditRigid").Invoke(null, [path, sourceOwned, rigid])!;
        }

        private static T Get<T>(object target, string name)
        {
            return (T)target.GetType().GetProperty(name, AnyInstance)!.GetValue(target)!;
        }

        [Test]
        public void LodAuditRigid_PerLodStatsComeFromPartsVerticesAndIndices()
        {
            var rigid = CreateAuditRigid(
                [10f, 20f, 30f, 10000f],
                [0, 0, 1, 2],
                [(100, 60), (50, 40)],
                [(80, 50)],
                [(40, 20), (10, 5), (10, 5)],
                [(12, 10)]);

            var audit = BuildAuditRigid("a.rigid_model_v2", true, rigid);
            var lods = Get<IReadOnlyList<object>>(audit, "Lods")
                .Select(lod => (
                    Parts: Get<int>(lod, "PartCount"),
                    Vertices: Get<long>(lod, "Vertices"),
                    Triangles: Get<long>(lod, "Triangles"),
                    Distance: Get<float>(lod, "Distance"),
                    Quality: Get<byte>(lod, "Quality")))
                .ToArray();

            Assert.That(lods, Is.EqualTo(new[]
            {
                (2, 150L, 100L, 10f, (byte)0),
                (1, 80L, 50L, 20f, (byte)0),
                (3, 60L, 30L, 30f, (byte)1),
                (1, 12L, 10L, 10000f, (byte)2),
            }));
            Assert.That(Get<string>(audit, "DistanceProfile"), Is.EqualTo("10/20/30/10000"));
            Assert.That(Get<double?>(audit, "LastToFirstTriangleRatio"), Is.EqualTo(0.1).Within(1e-12));
            Assert.That(Get<bool>(audit, "LastLodIsHeavy"), Is.False);
            Assert.That(Get<bool>(audit, "HasLodNotLighterThanPrevious"), Is.False);
            Assert.That(Get<bool>(audit, "HasFewLods"), Is.False);
        }

        [TestCase(new[] { 1000, 900, 800, 700 }, true, false, false)]
        [TestCase(new[] { 1000, 500, 250, 250 }, false, true, false)]
        [TestCase(new[] { 1000, 500, 250, 249 }, false, false, false)]
        [TestCase(new[] { 1000, 300, 500, 100 }, false, true, false)]
        [TestCase(new[] { 1000, 400, 400, 100 }, false, true, false)]
        [TestCase(new[] { 1000, 400, 100 }, false, false, true)]
        [TestCase(new[] { 1000 }, true, false, true)]
        [TestCase(new[] { 0, 0, 0, 0 }, false, true, false)]
        public void LodAuditRigid_Flags(
            int[] trianglesPerLod,
            bool lastLodHeavy,
            bool notLighter,
            bool fewLods)
        {
            var distances = trianglesPerLod.Select((_, index) => 10f * (index + 1)).ToArray();
            var audit = BuildAuditRigid(
                "a.rigid_model_v2",
                true,
                CreateTriangleRigid(distances, trianglesPerLod));

            Assert.That(Get<bool>(audit, "LastLodIsHeavy"), Is.EqualTo(lastLodHeavy));
            Assert.That(Get<bool>(audit, "HasLodNotLighterThanPrevious"), Is.EqualTo(notLighter));
            Assert.That(Get<bool>(audit, "HasFewLods"), Is.EqualTo(fewLods));
        }

        [Test]
        public void LodAuditRigid_NoTrianglesInLodZero_HasNoRatio()
        {
            var audit = BuildAuditRigid(
                "a.rigid_model_v2",
                true,
                CreateTriangleRigid([10f, 20f], 0, 5));
            var empty = BuildAuditRigid(
                "b.rigid_model_v2",
                true,
                CreateAuditRigid([], null));

            Assert.That(Get<double?>(audit, "LastToFirstTriangleRatio"), Is.Null);
            Assert.That(Get<bool>(audit, "LastLodIsHeavy"), Is.False);
            Assert.That(Get<double?>(empty, "LastToFirstTriangleRatio"), Is.Null);
            Assert.That(Get<bool>(empty, "HasFewLods"), Is.True);
        }

        private static object CreateAuditComponent(
            string wsModelPath,
            bool sourceOwnedRigid,
            RmvFile rigid)
        {
            return GetNestedType("CrossRigidAnalysisComponent")
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                .Single()
                .Invoke(
                [
                    BaselineVmdPath,
                    wsModelPath,
                    wsModelPath + ".rigid_model_v2",
                    new XmlDocument(),
                    rigid,
                    Array.Empty<string[]>(),
                    string.Empty,
                    false,
                    sourceOwnedRigid,
                ]);
        }

        private static object CreateAuditResult()
        {
            return Activator.CreateInstance(GetNestedType("LodAuditResult"), true)!;
        }

        private static object AuditRoot(
            object state,
            object audit,
            object expansion,
            Dictionary<string, object> components,
            string vmdPath = BaselineVmdPath)
        {
            var resolver = typeof(PackTextureAtlasCrossRigidTests)
                .GetMethod(
                    nameof(BuildResolver),
                    BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(GetNestedType("CrossRigidAnalysisComponent"))
                .Invoke(null, [components])!;
            return GetStaticMethod("AuditLodsForRoot")
                .Invoke(null, [state, audit, vmdPath, expansion, resolver])!;
        }

        // WSModel A (two LODs): LOD 0 = 2 parts, 150 vertices, 100 triangles;
        // LOD 1 = 1 part, 60 vertices, 40 triangles. WSModel B (one LOD) = 1 part,
        // 30 vertices, 20 triangles. Configurations: 0.25 -> A+B, 0.75 -> A.
        // Scenario LOD 0: triangles 0.25 * (100 + 20) + 0.75 * 100 = 105,
        // vertices 0.25 * (150 + 30) + 0.75 * 150 = 157.5, parts 0.25 * 3 + 0.75 * 2 = 2.25.
        // Scenario LODs 1-3 use A's last LOD (it has only two) and B's only LOD:
        // triangles 0.25 * (40 + 20) + 0.75 * 40 = 45, vertices 0.25 * 90 + 0.75 * 60 = 67.5,
        // parts 0.25 * 2 + 0.75 * 1 = 1.25.
        private static (object State, object Audit, object Root) AuditTwoConfigurationRoot()
        {
            var state = CreateArmyState();
            const string wsA = @"models\a.wsmodel";
            const string wsB = @"models\b.wsmodel";
            var components = new Dictionary<string, object>
            {
                [wsA] = CreateAuditComponent(
                    wsA,
                    true,
                    CreateAuditRigid(
                        [10f, 20f, 30f, 10000f],
                        null,
                        [(100, 60), (50, 40)],
                        [(60, 40)])),
                [wsB] = CreateAuditComponent(
                    wsB,
                    false,
                    CreateAuditRigid([100f, 200f], null, [(30, 20)])),
            };
            var expansion = CreateExpansion(
                CreateVisualConfiguration(0.25, wsA, wsB),
                CreateVisualConfiguration(0.75, wsA));
            var audit = CreateAuditResult();
            var root = AuditRoot(state, audit, expansion, components);
            return (state, audit, root);
        }

        [Test]
        public void LodAuditRoot_TwoConfigurations_ExpectedPerEntityValuesAreHandWorked()
        {
            var (_, audit, root) = AuditTwoConfigurationRoot();

            Assert.That(Get<IReadOnlyList<int>>(root, "ScenarioLods"), Is.EqualTo(new[] { 0, 1, 2, 3 }));
            Assert.That(
                Get<IReadOnlyList<double>>(root, "TrianglesPerEntity"),
                Is.EqualTo(new[] { 105.0, 45.0, 45.0, 45.0 }).Within(1e-9));
            Assert.That(
                Get<IReadOnlyList<double>>(root, "VerticesPerEntity"),
                Is.EqualTo(new[] { 157.5, 67.5, 67.5, 67.5 }).Within(1e-9));
            Assert.That(
                Get<IReadOnlyList<double>>(root, "PartsPerEntity"),
                Is.EqualTo(new[] { 2.25, 1.25, 1.25, 1.25 }).Within(1e-9));
            Assert.That(Get<bool>(root, "WeightAvailable"), Is.True);

            // Both rigids are counted once for this root; ownership comes from the component.
            var rigids = Get<IDictionary>(audit, "Rigids");
            Assert.That(rigids, Has.Count.EqualTo(2));
            var rootCounts = Get<IDictionary>(audit, "RootCountByRigid");
            Assert.That(rootCounts.Values.Cast<int>(), Is.All.EqualTo(1));
            Assert.That(
                Get<bool>(rigids[@"models\a.wsmodel.rigid_model_v2"]!, "SourceOwned"),
                Is.True);
            Assert.That(
                Get<bool>(rigids[@"models\b.wsmodel.rigid_model_v2"]!, "SourceOwned"),
                Is.False);
            Assert.That(
                Get<IReadOnlyList<string>>(root, "DistanceProfiles"),
                Is.EqualTo(new[] { "10/20/30/10000", "100/200" }));
            Assert.That(
                Get<IReadOnlyList<string>>(root, "RigidPaths"),
                Is.EqualTo(new[]
                {
                    @"models\a.wsmodel.rigid_model_v2",
                    @"models\b.wsmodel.rigid_model_v2",
                }));
        }

        [Test]
        public void LodAuditRoot_BattleValuesAreTheArmyWeightTimesPerLodValues()
        {
            var (state, _, root) = AuditTwoConfigurationRoot();
            var triangles = Get<IReadOnlyList<double>>(root, "TrianglesPerEntity");
            var vertices = Get<IReadOnlyList<double>>(root, "VerticesPerEntity");
            var battleTriangles = Get<IReadOnlyList<double>>(root, "BattleTriangles");
            var battleVertices = Get<IReadOnlyList<double>>(root, "BattleVertices");

            var scenarioLods = GetScenarioLods(state);
            Assert.That(scenarioLods, Has.Length.EqualTo(4));
            for (var index = 0; index < scenarioLods.Length; index++)
            {
                var probability = scenarioLods[index].Probability;
                var unitWeight = CallWeight(state, 1.0, [(probability, 1.0)]);
                Assert.That(unitWeight, Is.GreaterThan(0));
                Assert.That(
                    battleTriangles[index],
                    Is.EqualTo(unitWeight * triangles[index]).Within(1e-9));
                Assert.That(
                    battleTriangles[index],
                    Is.EqualTo(CallWeight(state, 1.0, [(probability, triangles[index])])).Within(1e-9));
                Assert.That(
                    battleVertices[index],
                    Is.EqualTo(unitWeight * vertices[index]).Within(1e-9));
            }

            var units = Get<IReadOnlyList<object>>(root, "Units");
            Assert.That(units.Select(unit => Get<string>(unit, "UnitId")), Is.EqualTo(new[] { "unit:fallback-test" }));
            Assert.That(Get<int>(units[0], "Entities"), Is.GreaterThanOrEqualTo(1));
            Assert.That(Get<int>(root, "UnitCount"), Is.EqualTo(1));
        }

        [Test]
        public void LodAuditRoot_RootNoUnitUses_KeepsPerEntityValuesAndHasNoWeight()
        {
            var state = CreateArmyState();
            const string wsA = @"models\a.wsmodel";
            var components = new Dictionary<string, object>
            {
                [wsA] = CreateAuditComponent(
                    wsA,
                    true,
                    CreateAuditRigid([10f, 20f], null, [(10, 8)], [(6, 4)])),
            };
            var audit = CreateAuditResult();

            var root = AuditRoot(
                state,
                audit,
                CreateExpansion(CreateVisualConfiguration(1.0, wsA)),
                components,
                @"variantmeshes\other.variantmeshdefinition");

            Assert.That(Get<bool>(root, "WeightAvailable"), Is.False);
            Assert.That(
                Get<IReadOnlyList<double>>(root, "TrianglesPerEntity"),
                Is.EqualTo(new[] { 8.0, 4.0, 4.0, 4.0 }));
            Assert.That(Get<IReadOnlyList<double>>(root, "BattleTriangles"), Is.All.EqualTo(0.0));
            Assert.That(Get<IReadOnlyList<object>>(root, "Units"), Is.Empty);
        }

        [Test]
        public void LodAuditRoot_WithoutArmyModel_UsesDefaultScenarioAndNoWeight()
        {
            var state = CreateBareBatchState();
            const string wsA = @"models\a.wsmodel";
            var components = new Dictionary<string, object>
            {
                [wsA] = CreateAuditComponent(
                    wsA,
                    true,
                    CreateAuditRigid([10f], null, [(10, 8)])),
            };
            var audit = CreateAuditResult();

            var root = AuditRoot(
                state,
                audit,
                CreateExpansion(CreateVisualConfiguration(1.0, wsA)),
                components);

            Assert.That(Get<bool>(root, "WeightAvailable"), Is.False);
            Assert.That(
                Get<IReadOnlyList<double>>(root, "TrianglesPerEntity"),
                Is.EqualTo(new[] { 8.0, 8.0, 8.0, 8.0 }));
        }

        [Test]
        public void LodAuditRoot_UnresolvedWsModel_IsSkippedAndCounted()
        {
            var state = CreateArmyState();
            var audit = CreateAuditResult();

            var root = AuditRoot(
                state,
                audit,
                CreateExpansion(CreateVisualConfiguration(1.0, @"models\missing.wsmodel")),
                new Dictionary<string, object>());

            Assert.That(Get<IReadOnlyList<double>>(root, "TrianglesPerEntity"), Is.All.EqualTo(0.0));
            Assert.That(Get<int>(audit, "UnresolvedWsModels"), Is.EqualTo(1));
            Assert.That(Get<IDictionary>(audit, "Rigids"), Is.Empty);
        }

        [Test]
        public void LodAuditRoot_PartsWithOneProfile_AreNotMixed()
        {
            var state = CreateArmyState();
            const string wsA = @"models\a.wsmodel";
            const string wsB = @"models\b.wsmodel";
            var components = new Dictionary<string, object>
            {
                [wsA] = CreateAuditComponent(wsA, true, CreateAuditRigid([10f, 20f], null, [(10, 8)], [(6, 4)])),
                [wsB] = CreateAuditComponent(wsB, true, CreateAuditRigid([10f, 20f], null, [(9, 7)], [(5, 3)])),
            };

            var root = AuditRoot(
                state,
                CreateAuditResult(),
                CreateExpansion(CreateVisualConfiguration(1.0, wsA, wsB)),
                components);

            Assert.That(Get<IReadOnlyList<string>>(root, "DistanceProfiles"), Is.EqualTo(new[] { "10/20" }));
        }

        private static string BuildLodAuditReport(object state)
        {
            var sb = new System.Text.StringBuilder();
            GetStaticMethod("AppendLodAuditSection").Invoke(null, [sb, state]);
            return sb.ToString();
        }

        [Test]
        public void LodAuditReport_RootsAndRigids_ListThePerEntityAndWeightedFigures()
        {
            var (state, audit, _) = AuditTwoConfigurationRoot();
            SetStateProperty(audit, "RootsRequested", 1);
            SetStateProperty(state, "LodAudit", audit);

            var report = BuildLodAuditReport(state);

            Assert.That(report, Does.StartWith("LOD audit" + Environment.NewLine + "---------"));
            Assert.That(report, Does.Contain("Roots audited: 1 / 1 VMD root(s) in scope"));
            Assert.That(report, Does.Contain("Rigids audited: 2 (source-owned 1, vanilla 1)"));
            Assert.That(report, Does.Contain("Heaviest unit looks at distance"));
            Assert.That(report, Does.Contain("triangles per entity: 105 / 45 / 45 / 45"));
            Assert.That(report, Does.Match(@"last/first 42[.,]9\s?%"));
            Assert.That(report, Does.Contain("mesh parts per entity at the last LOD 1"));
            Assert.That(report, Does.Contain("units: unit:fallback-test"));
            Assert.That(report, Does.Contain("10/20/30/10000 | 100/200"));
            Assert.That(report, Does.Contain("Heaviest rigids at the last LOD"));
            Assert.That(report, Does.Contain(@"models\a.wsmodel.rigid_model_v2"));
            Assert.That(report, Does.Contain("roots 1"));
            Assert.That(report, Does.Contain("Roots whose parts mix different LOD-distance profiles: 1"));
            Assert.That(report, Does.Not.Contain("NaN"));
            Assert.That(report, Does.Not.Contain("Infinity"));
        }

        [Test]
        public void LodAuditReport_FlagExamples_AreCappedAtTenWithTheTotalStillCounted()
        {
            var state = CreateBareBatchState();
            var audit = CreateAuditResult();
            var rigids = Get<IDictionary>(audit, "Rigids");
            for (var index = 0; index < 12; index++)
            {
                var path = $"variantmeshes\\heavy_{index:00}.rigid_model_v2";
                rigids[path] = BuildAuditRigid(
                    path,
                    true,
                    CreateTriangleRigid([10f, 20f], 1000, 900));
            }

            rigids["variantmeshes\\vanilla.rigid_model_v2"] = BuildAuditRigid(
                "variantmeshes\\vanilla.rigid_model_v2",
                false,
                CreateTriangleRigid([10f], 1000));
            SetStateProperty(state, "LodAudit", audit);

            var report = BuildLodAuditReport(state);

            // 12 source-owned rigids: all flagged for a heavy last LOD, a LOD that is
            // not lighter is false (900 < 1000), and all have fewer than four LODs.
            Assert.That(report, Does.Contain("Last LOD above 25 % of LOD 0 triangles: 12"));
            Assert.That(report, Does.Contain("same or more triangles than the previous LOD: 0"));
            Assert.That(report, Does.Contain("Fewer than 4 LODs: 12"));
            Assert.That(report, Does.Contain("... and 2 more"));
            Assert.That(
                report.Split(Environment.NewLine).Count(line => line.Contains("e.g. variantmeshes")),
                Is.EqualTo(20));
            Assert.That(report, Does.Not.Contain("vanilla.rigid_model_v2"));
            Assert.That(report, Does.Contain("10/20: 12 rigid(s)"));
            Assert.That(report, Does.Not.Contain("NaN"));
        }

        [Test]
        public void LodAuditReport_LongPaths_AreTruncatedInTheMiddleConsistently()
        {
            var state = CreateBareBatchState();
            var audit = CreateAuditResult();
            var rigids = Get<IDictionary>(audit, "Rigids");
            var path = "variantmeshes\\wh3_main_long_directory_name\\" +
                       new string('x', 90) + ".rigid_model_v2";
            rigids[path] = BuildAuditRigid(path, true, CreateTriangleRigid([10f], 100));
            SetStateProperty(state, "LodAudit", audit);

            var report = BuildLodAuditReport(state);

            Assert.That(report, Does.Not.Contain(path));
            // 30 leading characters, "...", then the last 39 characters.
            Assert.That(
                report,
                Does.Contain(
                    "variantmeshes\\wh3_main_long_di..." +
                    new string('x', 24) + ".rigid_model_v2"));
        }

        [Test]
        public void LodAuditReport_NoData_IsWellFormed()
        {
            var state = CreateBareBatchState();

            var notRun = BuildLodAuditReport(state);
            Assert.That(notRun, Does.Contain("LOD audit did not run."));

            SetStateProperty(state, "LodAudit", CreateAuditResult());
            var empty = BuildLodAuditReport(state);

            Assert.That(empty, Does.Contain("Roots audited: 0 / 0 VMD root(s) in scope"));
            Assert.That(empty, Does.Contain("Rigids audited: 0 (source-owned 0, vanilla 0)"));
            Assert.That(empty, Does.Contain("Expected battle triangles over all roots: 0"));
            Assert.That(empty, Does.Contain("Heaviest unit looks at distance"));
            Assert.That(empty, Does.Contain("Heaviest rigids at the last LOD"));
            Assert.That(empty, Does.Not.Contain("NaN"));
            Assert.That(empty, Does.Not.Contain("Infinity"));
        }

        [Test]
        public void LodAuditReport_RootsWithoutWeight_AreRankedPerEntityAndMarkedNotAvailable()
        {
            var state = CreateBareBatchState();
            const string wsA = @"models\a.wsmodel";
            var components = new Dictionary<string, object>
            {
                [wsA] = CreateAuditComponent(wsA, true, CreateAuditRigid([10f, 20f], null, [(10, 8)], [(6, 4)])),
            };
            var audit = CreateAuditResult();
            AuditRoot(
                state,
                audit,
                CreateExpansion(CreateVisualConfiguration(1.0, wsA)),
                components);
            SetStateProperty(state, "LodAudit", audit);

            var report = BuildLodAuditReport(state);

            Assert.That(report, Does.Contain("no root has an army weight"));
            Assert.That(report, Does.Contain("n/a (no army weight)"));
            Assert.That(report, Does.Contain("triangles per entity: 8 / 4 / 4 / 4"));
            Assert.That(report, Does.Not.Contain("NaN"));
        }

        [Test]
        public void LodAuditSource_ExhaustedTimeBudget_SkipsRootsInsteadOfRunningLong()
        {
            var state = CreateBareBatchState();
            SetStateProperty(
                state,
                "GameplayTraversalContainers",
                new List<Shared.Core.PackFiles.Models.IPackFileContainer>());

            GetStaticMethod("AuditSourceLods").Invoke(
                null,
                [
                    state,
                    new List<string> { "models\\a.variantmeshdefinition", "models\\b.variantmeshdefinition" },
                    CancellationToken.None,
                    null,
                    TimeSpan.Zero,
                ]);
            var audit = GetStateProperty<object>(state, "LodAudit");

            Assert.That(Get<int>(audit, "RootsRequested"), Is.EqualTo(2));
            Assert.That(Get<int>(audit, "RootsSkippedForTime"), Is.EqualTo(2));
            Assert.That(Get<IList>(audit, "Roots"), Is.Empty);
            Assert.That(Get<string?>(audit, "Failure"), Is.Null);
            Assert.That(
                BuildLodAuditReport(state),
                Does.Contain("skipped after the 0 s time budget: 2"));
        }

        [Test]
        public void LodAuditSource_UnresolvableRoot_IsCountedAsAnExpansionFailure()
        {
            var state = CreateBareBatchState();
            SetStateProperty(
                state,
                "GameplayTraversalContainers",
                new List<Shared.Core.PackFiles.Models.IPackFileContainer>());

            GetStaticMethod("AuditSourceLods").Invoke(
                null,
                [
                    state,
                    new List<string> { "models\\a.variantmeshdefinition" },
                    CancellationToken.None,
                    null,
                    null,
                ]);
            var audit = GetStateProperty<object>(state, "LodAudit");

            Assert.That(Get<int>(audit, "ExpansionFailures"), Is.EqualTo(1));
            Assert.That(Get<int>(audit, "RootsSkippedForTime"), Is.Zero);
            Assert.That(Get<string?>(audit, "Failure"), Is.Null);
            Assert.That(Get<IList>(audit, "Roots"), Is.Empty);
            Assert.That(Get<TimeSpan>(audit, "TimeBudget"), Is.EqualTo(TimeSpan.FromSeconds(15)));
        }

        // ---- Asset checks (report only) ------------------------------------

        private static object Call(string name, params object?[] arguments)
        {
            return GetStaticMethod(name).Invoke(null, arguments)!;
        }

        private static object CreateAlphaStatistics(
            long texels,
            int minAlpha,
            long below255,
            long below128)
        {
            return Activator.CreateInstance(
                GetNestedType("AssetCheckAlphaStatistics"),
                texels,
                minAlpha,
                below255,
                below128)!;
        }

        private static string ClassifyAlpha(long texels, int minAlpha, long below128)
        {
            return Call(
                    "ClassifyAssetCheckAlpha",
                    CreateAlphaStatistics(texels, minAlpha, below128, below128))
                .ToString()!;
        }

        [Test]
        public void AssetChecksAlphaStatistics_CountMinimumAndSharesIgnoringRowPadding()
        {
            // 3 x 2 texels, rows 16 bytes apart. Every byte that is not a texel's alpha,
            // padding included, holds 7 and must be ignored.
            var pixels = new byte[28];
            Array.Fill(pixels, (byte)7);
            byte[] alphas = [255, 200, 127, 128, 255, 10];
            for (var texel = 0; texel < alphas.Length; texel++)
                pixels[(texel / 3) * 16 + (texel % 3) * 4 + 3] = alphas[texel];

            var statistics = Call("ComputeAssetCheckAlphaStatistics", pixels, 3, 2, 16);

            Assert.That(Get<long>(statistics, "Texels"), Is.EqualTo(6));
            Assert.That(Get<int>(statistics, "MinAlpha"), Is.EqualTo(10));
            Assert.That(Get<long>(statistics, "Below255"), Is.EqualTo(4));
            Assert.That(Get<long>(statistics, "Below128"), Is.EqualTo(2));
        }

        [Test]
        public void AssetChecksAlphaStatistics_BufferTooSmall_Throws()
        {
            var exception = Assert.Throws<TargetInvocationException>(() =>
                Call("ComputeAssetCheckAlphaStatistics", new byte[15], 2, 2, 8));

            Assert.That(exception!.InnerException, Is.TypeOf<ArgumentException>());
        }

        [TestCase(100L, 255, 0L, "Opaque")]
        [TestCase(100L, 250, 0L, "Opaque")]
        [TestCase(100L, 249, 0L, "MostlyOpaque")]
        [TestCase(1000L, 0, 0L, "MostlyOpaque")]
        [TestCase(1001L, 0, 1L, "MostlyOpaque")]
        [TestCase(1000L, 0, 1L, "UsesAlpha")]
        [TestCase(2000L, 100, 2L, "UsesAlpha")]
        [TestCase(2000L, 100, 1L, "MostlyOpaque")]
        [TestCase(1000000L, 0, 999L, "MostlyOpaque")]
        [TestCase(1000000L, 0, 1000L, "UsesAlpha")]
        [TestCase(0L, 255, 0L, "Unreadable")]
        public void AssetChecksAlphaClassification_Boundaries(
            long texels,
            int minAlpha,
            long below128,
            string expected)
        {
            Assert.That(ClassifyAlpha(texels, minAlpha, below128), Is.EqualTo(expected));
        }

        private static (bool IsAlpha, bool Candidate, string Twin, string Reason) AssessShader(
            string shader,
            params string[] observedShaders)
        {
            var assessment = Call(
                "AssessAssetCheckAlphaShader",
                shader,
                new HashSet<string>(observedShaders, StringComparer.OrdinalIgnoreCase));
            return (
                Get<bool>(assessment, "IsAlphaShader"),
                Get<bool>(assessment, "IsCandidate"),
                Get<string>(assessment, "TwinShader"),
                Get<string>(assessment, "Reason"));
        }

        [TestCase("shaders/weighted4_character_alpha.xml.shader", "shaders\\weighted4_character.xml.shader")]
        [TestCase("Shaders\\Weighted2_Character_Alpha.xml.shader", "shaders\\weighted2_character.xml.shader")]
        [TestCase("shaders/rigid_character_alpha.xml.shader", "shaders\\rigid_character.xml.shader")]
        public void AssetChecksShader_PlainAlphaCharacterVariants_AreCandidatesWithTheirTwin(
            string shader,
            string twin)
        {
            var assessment = AssessShader(shader);

            Assert.That(assessment.IsAlpha, Is.True);
            Assert.That(assessment.Candidate, Is.True);
            Assert.That(assessment.Twin, Is.EqualTo(twin));
        }

        [TestCase("shaders/weighted2_character_alphatint.xml.shader")]
        [TestCase("shaders/weighted4_character_emissive_alpha.xml.shader")]
        [TestCase("shaders/weighted4_random_colours_alpha.xml.shader")]
        [TestCase("shaders/weighted4_faction_colours_skin_alpha.xml.shader")]
        [TestCase("shaders/weighted4_emissive_flow_alpha.xml.shader")]
        [TestCase("shaders/weighted4_default_alpha.xml.shader")]
        public void AssetChecksShader_OtherAlphaVariants_AreNotAssessed(string shader)
        {
            var assessment = AssessShader(shader);

            Assert.That(assessment.IsAlpha, Is.True);
            Assert.That(assessment.Candidate, Is.False);
            Assert.That(assessment.Reason, Is.Not.Empty);
        }

        [Test]
        public void AssetChecksShader_NotAssessedReasonSaysWhetherTheTwinIsInScope()
        {
            const string shader = "shaders/weighted4_character_emissive_alpha.xml.shader";

            Assert.That(
                AssessShader(shader, "shaders\\weighted4_character_emissive.xml.shader").Reason,
                Does.Contain("twin exists"));
            Assert.That(AssessShader(shader).Reason, Does.Contain("no non-alpha twin"));
        }

        [Test]
        public void AssetChecksShader_NonAlphaShader_IsNotAnAlphaShader()
        {
            var assessment = AssessShader("shaders/weighted4_character.xml.shader");

            Assert.That(assessment.IsAlpha, Is.False);
            Assert.That(assessment.Candidate, Is.False);
        }

        // 4x4-texel blocks of BC3 (DXT5). The alpha block stores alpha0, alpha1 and 16
        // three-bit indices; with alpha0 == alpha1 every texel has that alpha, with
        // alpha0 > alpha1 index 1 selects alpha1 and index 0 alpha0. The colour block is zero.
        private static byte[] BuildAlphaTexture(
            int width,
            int height,
            byte alpha,
            bool oneTransparentTexel,
            byte colourSeed = 0)
        {
            var blocks = (width / 4) * (height / 4);
            var data = new byte[blocks * 16];
            for (var block = 0; block < blocks; block++)
            {
                var offset = block * 16;
                data[offset] = alpha;
                data[offset + 1] = alpha;
                data[offset + 8] = colourSeed;
            }

            if (oneTransparentTexel)
            {
                data[1] = 0;
                data[2] = 0x01;
            }

            var dds = new byte[128 + data.Length];
            "DDS "u8.CopyTo(dds);
            BitConverter.GetBytes(124).CopyTo(dds, 4);
            BitConverter.GetBytes(0x1 | 0x2 | 0x4 | 0x1000 | 0x80000).CopyTo(dds, 8);
            BitConverter.GetBytes(height).CopyTo(dds, 12);
            BitConverter.GetBytes(width).CopyTo(dds, 16);
            BitConverter.GetBytes(data.Length).CopyTo(dds, 20);
            BitConverter.GetBytes(1).CopyTo(dds, 28);
            BitConverter.GetBytes(32).CopyTo(dds, 76);
            BitConverter.GetBytes(4).CopyTo(dds, 80);
            "DXT5"u8.CopyTo(dds.AsSpan(84));
            BitConverter.GetBytes(0x1000).CopyTo(dds, 108);
            data.CopyTo(dds, 128);
            return dds;
        }

        private static (string Status, int MinAlpha, long Below255, long Texels) DecodeAlpha(
            byte[] dds)
        {
            var result = Call("DecodeAssetCheckAlpha", dds);
            var statistics = Get<object>(result, "Statistics");
            return (
                Get<object>(result, "Status").ToString()!,
                Get<int>(statistics, "MinAlpha"),
                Get<long>(statistics, "Below255"),
                Get<long>(statistics, "Texels"));
        }

        [Test]
        public void AssetChecksDecode_FullyOpaqueTexture_IsOpaque()
        {
            var result = DecodeAlpha(BuildAlphaTexture(8, 8, 255, false));

            Assert.That(result, Is.EqualTo(("Opaque", 255, 0L, 64L)));
        }

        [Test]
        public void AssetChecksDecode_OneTransparentTexelInASmallTexture_UsesAlpha()
        {
            var result = DecodeAlpha(BuildAlphaTexture(8, 8, 255, true));

            Assert.That(result.Status, Is.EqualTo("UsesAlpha"));
            Assert.That(result.MinAlpha, Is.EqualTo(0));
            Assert.That(result.Below255, Is.EqualTo(1));
        }

        [Test]
        public void AssetChecksDecode_OneTransparentTexelInALargeTexture_IsMostlyOpaqueNotOpaque()
        {
            // 1 of 4096 texels (0.024 %) is below 128: below the 0.1 % limit, but the minimum
            // alpha of 0 rules out OPAQUE. A low mip would average this texel away.
            var result = DecodeAlpha(BuildAlphaTexture(64, 64, 255, true));

            Assert.That(result.Status, Is.EqualTo("MostlyOpaque"));
            Assert.That(result.MinAlpha, Is.EqualTo(0));
            Assert.That(result.Texels, Is.EqualTo(4096));
        }

        [Test]
        public void AssetChecksDecode_TranslucentTexture_UsesAlpha()
        {
            Assert.That(DecodeAlpha(BuildAlphaTexture(8, 8, 100, false)).Status, Is.EqualTo("UsesAlpha"));
        }

        [Test]
        public void AssetChecksDecode_NotADds_IsUnreadableWithAReason()
        {
            var result = Call("DecodeAssetCheckAlpha", new byte[] { 1, 2, 3, 4, 5 });

            Assert.That(Get<object>(result, "Status").ToString(), Is.EqualTo("Unreadable"));
            Assert.That(Get<string>(result, "Reason"), Does.StartWith("decode failed"));
        }

        private static XmlDocument LoadMaterial(string xml)
        {
            var document = new XmlDocument();
            document.LoadXml(xml);
            return document;
        }

        private static string MaterialXml(
            string name,
            string shader,
            string baseColour,
            string normal = "tex/n.dds")
        {
            return "<material><name>" + name + "</name><shader>" + shader + "</shader>" +
                   "<textures><texture><slot version=\"2\">t_xml_base_colour</slot><source>" +
                   baseColour + "</source></texture><texture><slot version=\"2\">t_xml_normal" +
                   "</slot><source>" + normal + "</source></texture></textures>" +
                   "<params><param><name>fuzz_amount</name><type>float</type><value>0</value>" +
                   "</param></params></material>";
        }

        [Test]
        public void AssetChecksMaterialIdentity_SwappingTheShaderMatchesTheNonAlphaSibling()
        {
            var alpha = LoadMaterial(MaterialXml(
                "a_alpha_on",
                "shaders/weighted4_character_alpha.xml.shader",
                "tex/a.dds"));
            var plain = LoadMaterial(MaterialXml(
                "a_alpha_off",
                "shaders/weighted4_character.xml.shader",
                "TEX\\A.dds"));

            var asIsAlpha = Call("BuildAssetCheckMaterialIdentity", alpha, null, null);
            var asIsPlain = Call("BuildAssetCheckMaterialIdentity", plain, null, null);
            var swapped = Call(
                "BuildAssetCheckMaterialIdentity",
                alpha,
                null,
                "shaders\\weighted4_character.xml.shader");

            Assert.That(asIsAlpha, Is.Not.EqualTo(asIsPlain));
            Assert.That(swapped, Is.EqualTo(asIsPlain));
            Assert.That(
                asIsPlain,
                Is.EqualTo("render:" + Call("GetMaterialRenderingIdentity", plain)));
        }

        [Test]
        public void AssetChecksMaterialIdentity_DuplicateTexturesAreIdenticalOnlyAfterCanonicalisation()
        {
            var first = LoadMaterial(MaterialXml("m1", "shaders/weighted4_character.xml.shader", "tex/a.dds"));
            var second = LoadMaterial(MaterialXml("m2", "shaders/weighted4_character.xml.shader", "tex/b.dds"));
            var other = LoadMaterial(MaterialXml("m3", "shaders/weighted4_character.xml.shader", "tex/c.dds"));
            var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [@"tex\a.dds"] = @"tex\a.dds",
                [@"tex\b.dds"] = @"tex\a.dds",
            };

            Assert.That(
                Call("BuildAssetCheckMaterialIdentity", first, null, null),
                Is.Not.EqualTo(Call("BuildAssetCheckMaterialIdentity", second, null, null)));
            Assert.That(
                Call("BuildAssetCheckMaterialIdentity", first, canonical, null),
                Is.EqualTo(Call("BuildAssetCheckMaterialIdentity", second, canonical, null)));
            Assert.That(
                Call("BuildAssetCheckMaterialIdentity", first, canonical, null),
                Is.Not.EqualTo(Call("BuildAssetCheckMaterialIdentity", other, canonical, null)));
        }

        [Test]
        public void AssetChecksDuplicateTextures_SameBytesDifferentPaths_Group_SameLengthDifferentBytes_DoNot()
        {
            var original = BuildAlphaTexture(8, 8, 255, false);
            var copy = (byte[])original.Clone();
            var sameLengthOtherBytes = BuildAlphaTexture(8, 8, 255, false, colourSeed: 9);
            var otherLength = BuildAlphaTexture(16, 8, 255, false);
            Assert.That(sameLengthOtherBytes, Has.Length.EqualTo(original.Length));

            (string, long, string) Entry(string path, byte[] bytes) =>
                (path, (long)bytes.Length, (string)Call("HashAssetCheckContent", bytes));

            var groups = (List<List<string>>)Call(
                "GroupAssetCheckContent",
                new List<(string, long, string)>
                {
                    Entry(@"tex\b_copy.dds", copy),
                    Entry(@"tex\a.dds", original),
                    Entry(@"tex\other_bytes.dds", sameLengthOtherBytes),
                    Entry(@"tex\other_length.dds", otherLength),
                });

            Assert.That(groups, Has.Count.EqualTo(1));
            Assert.That(groups[0], Is.EqualTo(new[] { @"tex\a.dds", @"tex\b_copy.dds" }));
        }

        [Test]
        public void AssetChecksUnlockCounter_RemovableIsTheDrawDifferenceUnderTheVertexLimit()
        {
            // Three parts of 10 vertices: separate = 3 draws, one identity = 1 draw.
            Assert.That(
                Call(
                    "CountAssetCheckRemovableParts",
                    new[] { 10, 10, 10 },
                    new[] { "a", "b", "c" },
                    new[] { "a", "a", "a" }),
                Is.EqualTo(2));
            Assert.That(
                Call(
                    "CountAssetCheckRemovableParts",
                    new[] { 10, 10, 10 },
                    new[] { "a", "b", "b" },
                    new[] { "a", "b", "b" }),
                Is.EqualTo(0));
            // Merged parts still split at the 16-bit vertex limit: 40,000 + 40,000 needs two.
            Assert.That(
                Call(
                    "CountAssetCheckRemovableParts",
                    new[] { 40000, 40000, 100 },
                    new[] { "a", "b", "c" },
                    new[] { "a", "a", "a" }),
                Is.EqualTo(1));
        }

        private static object CreateRmvKeys(string strict, string flagIgnored)
        {
            return Activator.CreateInstance(GetNestedType("AssetCheckRmvKeys"), strict, flagIgnored)!;
        }

        private static object CreatePartMaterial(
            string asIs,
            string check1,
            string check2,
            string combined)
        {
            return Activator.CreateInstance(
                GetNestedType("AssetCheckPartMaterial"),
                asIs,
                check1,
                check2,
                combined)!;
        }

        private static Array ToTypedArray(string nestedTypeName, params object[] items)
        {
            var array = Array.CreateInstance(GetNestedType(nestedTypeName), items.Length);
            for (var index = 0; index < items.Length; index++)
                array.SetValue(items[index], index);
            return array;
        }

        [Test]
        public void AssetChecksUnit_MeasuresCountOnlyTheMaterialChangeUnderEachRmvTreatment()
        {
            // Parts: 0 = alpha material with an opaque texture, 1 = its non-alpha sibling,
            // 2 = a duplicate-texture sibling, 3 = unrelated.
            var parts = ToTypedArray(
                "AssetCheckPartMaterial",
                CreatePartMaterial("alpha", "plain", "alpha", "plain"),
                CreatePartMaterial("plain", "plain", "plain", "plain"),
                CreatePartMaterial("dup", "dup", "plain", "plain"),
                CreatePartMaterial("other", "other", "other", "other"));

            // Same RMV identity everywhere: check 1 merges 0 and 1, check 2 merges 1 and 2,
            // both together merge 0, 1 and 2.
            var same = ToTypedArray(
                "AssetCheckRmvKeys",
                CreateRmvKeys("k", "k"),
                CreateRmvKeys("k", "k"),
                CreateRmvKeys("k", "k"),
                CreateRmvKeys("k", "k"));
            var removable = (int[])Call("EvaluateAssetCheckUnit", new[] { 4, 4, 4, 4 }, same, parts);
            Assert.That(removable, Is.EqualTo(new[] { 1, 1, 1, 2, 2 }));

            // Part 0 differs from part 1 only by the render flag: the strict RMV identity
            // blocks check 1, ignoring the flag (tier T4 treatment) lets it through.
            var flagOnly = ToTypedArray(
                "AssetCheckRmvKeys",
                CreateRmvKeys("k0", "k"),
                CreateRmvKeys("k", "k"),
                CreateRmvKeys("k", "k"),
                CreateRmvKeys("k", "k"));
            removable = (int[])Call("EvaluateAssetCheckUnit", new[] { 4, 4, 4, 4 }, flagOnly, parts);
            Assert.That(removable, Is.EqualTo(new[] { 0, 1, 1, 1, 2 }));

            // A real RMV difference (shader name, vertex format...) blocks every treatment.
            var different = ToTypedArray(
                "AssetCheckRmvKeys",
                CreateRmvKeys("k0", "kk0"),
                CreateRmvKeys("k", "k"),
                CreateRmvKeys("k", "k"),
                CreateRmvKeys("k", "k"));
            removable = (int[])Call("EvaluateAssetCheckUnit", new[] { 4, 4, 4, 4 }, different, parts);
            Assert.That(removable, Is.EqualTo(new[] { 0, 0, 1, 1, 1 }));
        }

        [Test]
        public void AssetChecksUnit_WithNoMaterialChange_NeedsNoRmvComparison()
        {
            var unchanged = ToTypedArray(
                "AssetCheckPartMaterial",
                CreatePartMaterial("a", "a", "a", "a"),
                CreatePartMaterial("b", "b", "b", "b"));
            var changed = ToTypedArray(
                "AssetCheckPartMaterial",
                CreatePartMaterial("a", "b", "a", "b"),
                CreatePartMaterial("b", "b", "b", "b"));

            Assert.That(Call("AssetCheckUnitCanMerge", (object)unchanged), Is.EqualTo(false));
            Assert.That(Call("AssetCheckUnitCanMerge", (object)changed), Is.EqualTo(true));
        }

        [Test]
        public void AssetChecksRmvKeys_RenderFlagOnlyChangesTheStrictKey()
        {
            var first = CreateWeightedModel(1);
            var flagged = CreateWeightedModel(1);
            var header = flagged.CommonHeader;
            header.RenderFlag = 5;
            flagged.CommonHeader = header;
            var otherFormat = CreateWeightedModel(1);
            ((WeightedMaterial)otherFormat.Material).BinaryVertexFormat = VertexFormat.Cinematic;

            var keys = Call("BuildAssetCheckRmvKeys", first);
            var flaggedKeys = Call("BuildAssetCheckRmvKeys", flagged);
            var formatKeys = Call("BuildAssetCheckRmvKeys", otherFormat);

            Assert.That(Get<string>(keys, "Strict"), Is.EqualTo(Call("GetRmvMergeIdentity", first)));
            Assert.That(Get<string>(keys, "Strict"), Is.Not.EqualTo(Get<string>(flaggedKeys, "Strict")));
            Assert.That(Get<string>(keys, "FlagIgnored"), Is.EqualTo(Get<string>(flaggedKeys, "FlagIgnored")));
            Assert.That(Get<string>(keys, "Strict"), Is.Not.EqualTo(Get<string>(formatKeys, "Strict")));
            Assert.That(Get<string>(keys, "FlagIgnored"), Is.Not.EqualTo(Get<string>(formatKeys, "FlagIgnored")));
        }

        // ---- Asset checks: whole pass over synthetic packs -------------------

        private const string AlphaShader = "shaders/weighted4_character_alpha.xml.shader";
        private const string PlainShader = "shaders/weighted4_character.xml.shader";

        private static (object State, object Checks, Dictionary<string, byte[]> Files) RunAssetChecksPass(
            Func<string, string[][][]?>? rmvKeyStrings = null,
            TimeSpan? budget = null,
            Action<Dictionary<string, byte[]>>? changeFiles = null,
            bool markWsModelsAsReached = true)
        {
            var opaque = BuildAlphaTexture(8, 8, 255, false);
            var files = new Dictionary<string, byte[]>
            {
                [@"tex\opaque_a.dds"] = opaque,
                [@"tex\opaque_b.dds"] = (byte[])opaque.Clone(),
                [@"tex\opaque_c.dds"] = BuildAlphaTexture(8, 8, 255, false, colourSeed: 9),
                [@"tex\cutout.dds"] = BuildAlphaTexture(8, 8, 255, true),
                [@"tex\one_hole.dds"] = BuildAlphaTexture(64, 64, 255, true),
                [@"mat\alpha_opaque.xml.material"] = Encode(MaterialXml("alpha_opaque", AlphaShader, "tex/opaque_a.dds")),
                [@"mat\plain_opaque.xml.material"] = Encode(MaterialXml("plain_opaque", PlainShader, "tex/opaque_a.dds")),
                [@"mat\plain_dup.xml.material"] = Encode(MaterialXml("plain_dup", PlainShader, "tex/opaque_b.dds")),
                [@"mat\plain_c.xml.material"] = Encode(MaterialXml("plain_c", PlainShader, "tex/opaque_c.dds")),
                [@"mat\alpha_cutout.xml.material"] = Encode(MaterialXml("alpha_cutout", AlphaShader, "tex/cutout.dds")),
                [@"mat\alpha_hole.xml.material"] = Encode(MaterialXml("alpha_hole", AlphaShader, "tex/one_hole.dds")),
                [@"mat\alpha_missing.xml.material"] = Encode(MaterialXml("alpha_missing", AlphaShader, "tex/missing.dds")),
                [@"mat\emissive.xml.material"] = Encode(MaterialXml(
                    "emissive",
                    "shaders/weighted4_character_emissive_alpha.xml.shader",
                    "tex/opaque_a.dds")),
                [@"mat\alpha_notex.xml.material"] = Encode(
                    "<material><name>n</name><shader>" + AlphaShader + "</shader></material>"),
            };
            changeFiles?.Invoke(files);
            var state = CreateAssetChecksState(files);
            var checks = Activator.CreateInstance(GetNestedType("AssetChecksResult"), true)!;
            SetStateProperty(state, "AssetChecks", checks);

            const string wsA = @"models\a.wsmodel";
            const string wsB = @"models\b.wsmodel";
            string Mat(string name) => @"mat\" + name + ".xml.material";
            var components = new Dictionary<string, object>
            {
                [wsA] = CreateBaselineComponent(
                    wsA,
                    [
                        [CreateWeightedModel(1), CreateWeightedModel(2), CreateWeightedModel(3), CreateWeightedModel(4)],
                        [CreateWeightedModel(5), CreateWeightedModel(6)],
                    ],
                    [
                        [Mat("alpha_opaque"), Mat("plain_opaque"), Mat("alpha_cutout"), Mat("plain_dup")],
                        [Mat("alpha_opaque"), Mat("alpha_hole")],
                    ]),
                [wsB] = CreateBaselineComponent(
                    wsB,
                    [[CreateWeightedModel(1), CreateWeightedModel(2), CreateWeightedModel(3), CreateWeightedModel(4)]],
                    [[Mat("emissive"), Mat("alpha_missing"), Mat("alpha_notex"), Mat("plain_c")]]),
            };
            var audit = CreateAuditResult();
            SetStateProperty(state, "LodAudit", audit);
            AuditRoot(
                state,
                audit,
                CreateExpansion(CreateVisualConfiguration(1.0, wsA, wsB)),
                components);
            if (markWsModelsAsReached)
            {
                foreach (var component in components.Values)
                    Call("ObserveAssetCheckComponent", state, component);
            }

            object? provider = null;
            if (rmvKeyStrings != null)
                provider = CreateRmvKeyProvider(rmvKeyStrings);
            Call("RunAssetChecks", state, CancellationToken.None, null, budget, provider);
            return (state, checks, files);
        }

        private static byte[] Encode(string xml) => System.Text.Encoding.UTF8.GetBytes(xml);

        // [lod][part] -> (strict, flagIgnored) per rigid.
        private static object CreateRmvKeyProvider(Func<string, string[][][]?> keyStrings)
        {
            var keysType = GetNestedType("AssetCheckRmvKeys");
            var jagged = keysType.MakeArrayType().MakeArrayType();
            var method = typeof(PackTextureAtlasCrossRigidTests)
                .GetMethod(nameof(WrapRmvProvider), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(jagged);
            return method.Invoke(null, [(Func<string, object?>)(rigidPath =>
            {
                var perLod = keyStrings(rigidPath);
                if (perLod == null)
                    return null;

                var result = Array.CreateInstance(keysType.MakeArrayType(), perLod.Length);
                for (var lod = 0; lod < perLod.Length; lod++)
                {
                    var lodKeys = Array.CreateInstance(keysType, perLod[lod].Length);
                    for (var part = 0; part < perLod[lod].Length; part++)
                    {
                        lodKeys.SetValue(
                            CreateRmvKeys(perLod[lod][part][0], perLod[lod][part][1]),
                            part);
                    }

                    result.SetValue(lodKeys, lod);
                }

                return result;
            })])!;
        }

        private static Func<string, T?> WrapRmvProvider<T>(Func<string, object?> inner)
            where T : class
        {
            return path => (T?)inner(path);
        }

        private static string[][][] SameKeys(params int[] partsPerLod)
        {
            return partsPerLod
                .Select(count => Enumerable.Range(0, count).Select(_ => new[] { "k", "k" }).ToArray())
                .ToArray();
        }

        private static object CreateAssetChecksState(IReadOnlyDictionary<string, byte[]> sourceFiles)
        {
            var packFiles = sourceFiles.ToDictionary(
                entry => entry.Key.ToLowerInvariant(),
                entry => new Shared.Core.PackFiles.Models.PackFile(
                    "file",
                    new Shared.Core.PackFiles.Models.FileSources.MemorySource(entry.Value)));
            var source = new Moq.Mock<Shared.Core.PackFiles.Models.IPackFileContainer>();
            source
                .Setup(container => container.FindFile(Moq.It.IsAny<string>()))
                .Returns((string path) => packFiles.GetValueOrDefault(path));
            source
                .Setup(container => container.ContainsFile(Moq.It.IsAny<string>()))
                .Returns((string path) => packFiles.ContainsKey(path));
            var packFileService = new Moq.Mock<Editors.PackOptimizer.Core.IPackOptimizerPackFiles>();
            packFileService
                .Setup(service => service.FindFile(
                    Moq.It.IsAny<string>(),
                    Moq.It.IsAny<Shared.Core.PackFiles.Models.IPackFileContainer?>()))
                .Returns((string path, Shared.Core.PackFiles.Models.IPackFileContainer? _) =>
                    packFiles.GetValueOrDefault(path));
            var state = CreateStateFromContainers(source.Object, packFileService.Object);
            SetStateProperty(
                state,
                "GameplayTraversalContainers",
                new List<Shared.Core.PackFiles.Models.IPackFileContainer>());
            return state;
        }

        private static object CreateStateFromContainers(
            Shared.Core.PackFiles.Models.IPackFileContainer container,
            Editors.PackOptimizer.Core.IPackOptimizerPackFiles packFileService)
        {
            var state = GetNestedType("BatchState")
                .GetConstructors(AnyInstance)
                .Single()
                .Invoke(
                [
                    container,
                    container,
                    packFileService,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    false,
                    false,
                    false,
                ]);
            var model = typeof(PackTextureAtlasValueGateTests)
                .GetMethod(
                    "CreateSyntheticArmyResidencyModelForFallbackTest",
                    BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, null)!;
            SetStateProperty(state, "ArmyResidencyModel", model);
            return state;
        }

        private static object GetMaterial(object checks, string path)
        {
            return Get<IDictionary>(checks, "Materials")[path]!;
        }

        private static string StatusOf(object checks, string name)
        {
            return Get<object>(GetMaterial(checks, @"mat\" + name + ".xml.material"), "Status")
                .ToString()!;
        }

        [Test]
        public void AssetChecksPass_ClassifiesAlphaMaterialsFromTheDecodedTexture()
        {
            var (_, checks, _) = RunAssetChecksPass(_ => null);

            Assert.That(Get<string?>(checks, "Failure"), Is.Null);
            Assert.That(StatusOf(checks, "alpha_opaque"), Is.EqualTo("Opaque"));
            Assert.That(StatusOf(checks, "alpha_cutout"), Is.EqualTo("UsesAlpha"));
            Assert.That(StatusOf(checks, "alpha_hole"), Is.EqualTo("MostlyOpaque"));
            Assert.That(StatusOf(checks, "alpha_missing"), Is.EqualTo("Unreadable"));
            Assert.That(StatusOf(checks, "alpha_notex"), Is.EqualTo("NoAlphaTexture"));
            Assert.That(StatusOf(checks, "emissive"), Is.EqualTo("NotAssessed"));
            Assert.That(StatusOf(checks, "plain_opaque"), Is.EqualTo("NotAlphaShader"));
            Assert.That(
                Get<int>(GetMaterial(checks, @"mat\alpha_opaque.xml.material"), "MinAlpha"),
                Is.EqualTo(255));
            Assert.That(
                Get<int>(GetMaterial(checks, @"mat\alpha_hole.xml.material"), "MinAlpha"),
                Is.EqualTo(0));
            // Each candidate texture is decoded once: opaque_a, cutout, one_hole, missing.
            Assert.That(Get<int>(checks, "AlphaTexturesToAssess"), Is.EqualTo(4));
            Assert.That(Get<int>(checks, "AlphaTexturesDecoded"), Is.EqualTo(4));
            Assert.That(Get<int>(checks, "AlphaTexturesNotAssessed"), Is.Zero);
        }

        [Test]
        public void AssetChecksPass_OpaqueAlphaMaterialBecomesMergeableWithItsNonAlphaSibling()
        {
            var (_, checks, _) = RunAssetChecksPass(rigid =>
                rigid.StartsWith("models\\a") ? SameKeys(4, 2) : SameKeys(4));

            // WSModel A, LOD 0: the OPAQUE alpha part merges with plain_opaque (1 part
            // removable); the cut-out alpha part and the unrelated parts do not. LOD 1 has
            // alpha_opaque next to alpha_hole (MOSTLY OPAQUE, keeps its alpha shader).
            Assert.That(Get<long[]>(checks, "PartsRemovable")[0], Is.EqualTo(1));
            Assert.That(Get<long[]>(checks, "PartsRemovable")[1], Is.EqualTo(1));
            Assert.That(Get<int[]>(checks, "RigidsAffected")[0], Is.EqualTo(1));
            Assert.That(Get<int[]>(checks, "UnitsAffected")[0], Is.EqualTo(1));
        }

        [Test]
        public void AssetChecksPass_StrictRmvIdentityBlocksTheMergeButTheRenderFlagTreatmentAllowsIt()
        {
            var (_, checks, _) = RunAssetChecksPass(rigid =>
            {
                var keys = SameKeys(4, 2);
                if (rigid.StartsWith("models\\a"))
                    keys[0][0] = ["strict-differs", "k"];
                return rigid.StartsWith("models\\a") ? keys : SameKeys(4);
            });

            Assert.That(Get<long[]>(checks, "PartsRemovable")[0], Is.Zero);
            Assert.That(Get<long[]>(checks, "PartsRemovable")[1], Is.EqualTo(1));
            Assert.That(Get<int[]>(checks, "RigidsAffected")[0], Is.Zero);
            Assert.That(Get<int[]>(checks, "RigidsAffected")[1], Is.EqualTo(1));
        }

        [Test]
        public void AssetChecksPass_DuplicateTexturesAndMaterialsIdenticalOnlyAfterSharing()
        {
            var (_, checks, _) = RunAssetChecksPass(rigid => SameKeys(4, 2));

            // opaque_a / opaque_b are byte-identical; opaque_c has the same length only.
            Assert.That(Get<int>(checks, "DuplicateGroupCount"), Is.EqualTo(1));
            Assert.That(Get<int>(checks, "DuplicateFileCount"), Is.EqualTo(2));
            Assert.That(Get<int>(checks, "RedundantFileCount"), Is.EqualTo(1));
            Assert.That(Get<long>(checks, "RedundantBytes"), Is.EqualTo(128 + 64));
            Assert.That(Get<long>(checks, "RedundantBcnBytes"), Is.EqualTo(64));
            // The four 192-byte textures share a length, so all four are read.
            Assert.That(Get<int>(checks, "HashedFileCount"), Is.EqualTo(4));
            var canonical = Get<IDictionary>(checks, "CanonicalTextures");
            Assert.That(canonical[@"tex\opaque_b.dds"], Is.EqualTo(@"tex\opaque_a.dds"));
            Assert.That(canonical.Contains(@"tex\opaque_c.dds"), Is.False);

            // As-is nothing is identical; sharing the duplicate texture makes plain_opaque
            // and plain_dup identical.
            Assert.That(Get<int>(Get<object>(checks, "AsIsAll"), "Groups"), Is.Zero);
            var canonicalAll = Get<object>(checks, "CanonicalAll");
            Assert.That(Get<int>(canonicalAll, "Groups"), Is.EqualTo(1));
            Assert.That(Get<int>(canonicalAll, "Files"), Is.EqualTo(2));
            Assert.That(Get<int>(canonicalAll, "Redundant"), Is.EqualTo(1));
            Assert.That(Get<int>(checks, "NewCanonicalGroupCount"), Is.EqualTo(1));
            var newGroup = Get<IList>(checks, "NewCanonicalGroups")[0]!;
            Assert.That(
                Get<IReadOnlyList<string>>(newGroup, "Paths"),
                Is.EqualTo(new[] { @"mat\plain_dup.xml.material", @"mat\plain_opaque.xml.material" }));
            Assert.That(
                Get<IReadOnlyList<string>>(newGroup, "Differences").Single(),
                Is.EqualTo(@"t_xml_base_colour: tex\opaque_b.dds | tex\opaque_a.dds"));
        }

        [Test]
        public void AssetChecksPass_UnlockCountsForEachCheckAndTheirCombination()
        {
            var (_, checks, _) = RunAssetChecksPass(rigid => SameKeys(4, 2));
            var removable = Get<long[]>(checks, "PartsRemovable");

            // WSModel A LOD 0 parts: alpha_opaque, plain_opaque, alpha_cutout, plain_dup.
            // Check 1 merges alpha_opaque with plain_opaque; check 2 merges plain_opaque with
            // plain_dup; together alpha_opaque, plain_opaque and plain_dup become one draw.
            Assert.That(removable[0], Is.EqualTo(1));
            Assert.That(removable[2], Is.EqualTo(1));
            Assert.That(removable[3], Is.EqualTo(2));
            Assert.That(removable[4], Is.EqualTo(2));
            Assert.That(Get<int>(checks, "UnitCount"), Is.EqualTo(3));
            Assert.That(Get<int>(checks, "RigidsLoaded"), Is.EqualTo(1));
        }

        [Test]
        public void AssetChecksPass_ExpectedInstancesUseTheArmyWeightOfTheLodAudit()
        {
            var (state, checks, _) = RunAssetChecksPass(rigid => SameKeys(4, 2));
            var scenario = GetScenarioLods(state);

            // alpha_opaque is part 0 of A at LOD 0 and at LOD 1, one instance per entity.
            var expectedOpaque = CallWeight(
                state,
                1.0,
                scenario.Select(entry => (entry.Probability, 1.0)).ToArray());
            var opaque = ((IDictionary)Get<object>(checks, "Classes"))[ParseStatus("Opaque")]!;
            Assert.That(expectedOpaque, Is.GreaterThan(0));
            Assert.That(Get<double>(opaque, "ExpectedInstances"), Is.EqualTo(expectedOpaque).Within(1e-9));
            Assert.That(Get<int>(opaque, "SourceMaterials"), Is.EqualTo(1));
            Assert.That(Get<int>(opaque, "MeshPartSlots"), Is.EqualTo(2));
            Assert.That(Get<int>(opaque, "WsModels"), Is.EqualTo(1));
            Assert.That(Get<int>(opaque, "Roots"), Is.EqualTo(1));
            var roots = Get<IList>(checks, "Roots");
            Assert.That(roots, Has.Count.EqualTo(1));
            Assert.That(
                Get<IReadOnlyDictionary<string, double>>(roots[0]!, "InstanceWeightByWsModel"),
                Is.EqualTo(new Dictionary<string, double>
                {
                    [@"models\a.wsmodel"] = 1.0,
                    [@"models\b.wsmodel"] = 1.0,
                }));

            // The check-1 unlock (1 draw at source LOD 0 only) is the weight of scenario LOD 0.
            var expectedSaved = CallWeight(state, 1.0, [(scenario[0].Probability, 1.0)]);
            Assert.That(
                Get<double[]>(checks, "ExpectedDrawsSaved")[0],
                Is.EqualTo(expectedSaved).Within(1e-9));

            // All expected part instances: A has 4 parts at LOD 0 and 2 beyond, B 4 parts.
            var totalParts = CallWeight(
                state,
                1.0,
                scenario
                    .Select(entry => (entry.Probability, (entry.Lod == 0 ? 4.0 : 2.0) + 4.0))
                    .ToArray());
            Assert.That(
                Get<double>(checks, "TotalExpectedPartInstances"),
                Is.EqualTo(totalParts).Within(1e-9));

            var top = Get<IList>(checks, "TopOpaqueMaterials");
            Assert.That(top, Has.Count.EqualTo(1));
            Assert.That(Get<string>(top[0]!, "Path"), Is.EqualTo(@"mat\alpha_opaque.xml.material"));
            Assert.That(Get<string>(top[0]!, "TexturePath"), Is.EqualTo(@"tex\opaque_a.dds"));
            Assert.That(Get<int>(top[0]!, "MinAlpha"), Is.EqualTo(255));
            Assert.That(Get<int>(top[0]!, "MeshPartSlots"), Is.EqualTo(2));
        }

        private static object ParseStatus(string name)
        {
            return Enum.Parse(GetNestedType("AssetCheckAlphaStatus"), name);
        }

        [Test]
        public void AssetChecksPass_OnlyMaterialsOfReachedWsModelsAreInScope()
        {
            var (_, checks, _) = RunAssetChecksPass(
                rigid => SameKeys(4, 2),
                markWsModelsAsReached: false);

            Assert.That(Get<IDictionary>(checks, "Materials"), Is.Empty);
            Assert.That(Get<IDictionary>(checks, "Components"), Is.Empty);
            Assert.That(Get<string?>(checks, "Failure"), Is.Null);
        }

        [Test]
        public void AssetChecksPass_ExhaustedBudget_CountsWhatWasNotAssessedInsteadOfRunningLong()
        {
            var (_, checks, _) = RunAssetChecksPass(
                rigid => SameKeys(4, 2),
                budget: TimeSpan.Zero);

            Assert.That(Get<string?>(checks, "Failure"), Is.Null);
            Assert.That(Get<int>(checks, "AlphaTexturesNotAssessed"), Is.EqualTo(4));
            Assert.That(Get<int>(checks, "AlphaTexturesDecoded"), Is.Zero);
            Assert.That(StatusOf(checks, "alpha_opaque"), Is.EqualTo("BudgetExhausted"));
            Assert.That(Get<int>(checks, "HashNotAssessedCount"), Is.EqualTo(4));
            Assert.That(Get<int>(checks, "DuplicateGroupCount"), Is.Zero);
            // Nothing was classified or compared, so no unit is known to need a rigid.
            Assert.That(Get<int>(checks, "UnitsNeedingRmv"), Is.Zero);
            Assert.That(Get<long[]>(checks, "PartsRemovable"), Is.All.Zero);
        }

        [Test]
        public void AssetChecksPass_UnreadableRigid_IsCountedAndDoesNotFailTheRun()
        {
            var (_, checks, _) = RunAssetChecksPass(rigid => null);

            Assert.That(Get<string?>(checks, "Failure"), Is.Null);
            Assert.That(Get<int>(checks, "RigidLoadFailures"), Is.EqualTo(1));
            Assert.That(Get<int>(checks, "UnitsNotEvaluated"), Is.GreaterThan(0));
        }

        [Test]
        public void AssetChecksPass_FailureInsideTheChecks_IsCaughtAndReported()
        {
            var state = CreateAssetChecksState(new Dictionary<string, byte[]>());
            var checks = Activator.CreateInstance(GetNestedType("AssetChecksResult"), true)!;
            SetStateProperty(state, "AssetChecks", checks);
            Call(
                "ObserveAssetCheckComponent",
                state,
                CreateBaselineComponent(
                    @"models\a.wsmodel",
                    [[CreateWeightedModel(1)]],
                    [[@"mat\a.xml.material"]]));
            // A source container whose ownership lookup throws.
            var failing = new Moq.Mock<Shared.Core.PackFiles.Models.IPackFileContainer>();
            failing
                .Setup(container => container.ContainsFile(Moq.It.IsAny<string>()))
                .Throws(new InvalidOperationException("boom"));
            var failingState = CreateStateFromContainers(
                failing.Object,
                new Moq.Mock<Editors.PackOptimizer.Core.IPackOptimizerPackFiles>().Object);
            SetStateProperty(failingState, "AssetChecks", checks);

            Call("RunAssetChecks", failingState, CancellationToken.None, null, null, null);

            Assert.That(Get<string?>(checks, "Failure"), Is.EqualTo("InvalidOperationException: boom"));
            var report = BuildAssetChecksReport(failingState);
            Assert.That(report, Does.Contain("Asset checks stopped early: InvalidOperationException: boom"));
        }

        [Test]
        public void AssetChecksPass_DoesNotTouchSharedCachesOrCounters()
        {
            var (state, _, _) = RunAssetChecksPass(rigid => SameKeys(4, 2));

            Assert.That(GetStateProperty<IDictionary>(state, "MaterialDocuments"), Is.Empty);
            Assert.That(GetStateProperty<IDictionary>(state, "MeshMergeMaterialDiagnostics"), Is.Empty);
            Assert.That(GetStateProperty<IDictionary>(state, "TextureInspections"), Is.Empty);
            Assert.That(GetStateProperty<IDictionary>(state, "CrossRigidMaterialMergeIdentityCache"), Is.Empty);
            Assert.That(GetStateProperty<int>(state, "GameplayTraversalMaterialXmlParseCount"), Is.Zero);
            Assert.That(GetStateProperty<int>(state, "GameplayTraversalContainerResolutionCacheHits"), Is.Zero);
            Assert.That(GetStateProperty<int>(state, "GameplayTraversalContainerResolutionScans"), Is.Zero);
        }

        private static string BuildAssetChecksReport(object state)
        {
            var sb = new System.Text.StringBuilder();
            Call("AppendAssetChecksSection", sb, state);
            return sb.ToString();
        }

        [Test]
        public void AssetChecksReport_WithData_ListsTheFiguresOfBothChecks()
        {
            var (state, _, _) = RunAssetChecksPass(rigid => SameKeys(4, 2));

            var report = BuildAssetChecksReport(state);

            Assert.That(report, Does.StartWith("Asset checks" + Environment.NewLine + "------------"));
            Assert.That(report, Does.Contain("Check 1: alpha materials whose alpha source is opaque"));
            Assert.That(report, Does.Contain("OPAQUE (min alpha >= 250): 1 (1 / 0)"));
            Assert.That(report, Does.Contain("MOSTLY OPAQUE (< 0.1 % of texels below 128): 1 (1 / 0)"));
            Assert.That(report, Does.Contain("USES ALPHA: 1 (1 / 0)"));
            Assert.That(report, Does.Contain("unreadable texture(s): 1 - texture file not found"));
            Assert.That(report, Does.Contain("Not assessed: 1 material(s) - no non-alpha twin shader"));
            Assert.That(report, Does.Contain("1. mat/alpha_opaque.xml.material"));
            Assert.That(report, Does.Contain("min alpha 255"));
            Assert.That(report, Does.Contain("Check 2: duplicate textures and materials"));
            Assert.That(report, Does.Contain("1 group(s), 2 file(s), 1 redundant file(s)"));
            Assert.That(report, Does.Contain("2 files x 192 B"));
            Assert.That(report, Does.Contain("Checks 1 and 2 together"));
            Assert.That(report, Does.Not.Contain("NaN"));
            Assert.That(report, Does.Not.Contain("Infinity"));
        }

        [Test]
        public void AssetChecksReport_NoData_IsWellFormed()
        {
            var state = CreateBareBatchState();
            SetStateProperty(
                state,
                "GameplayTraversalContainers",
                new List<Shared.Core.PackFiles.Models.IPackFileContainer>());

            Assert.That(BuildAssetChecksReport(state), Does.Contain("Asset checks did not run."));

            Call("RunAssetChecks", state, CancellationToken.None, null, null, null);
            var report = BuildAssetChecksReport(state);

            Assert.That(report, Does.Contain("Check 1: alpha materials"));
            Assert.That(report, Does.Contain("Check 2: duplicate textures and materials"));
            Assert.That(report, Does.Contain("Scope: 0 WSModel(s) on 0 rigid(s)"));
            Assert.That(report, Does.Contain("(none)"));
            Assert.That(report, Does.Not.Contain("NaN"));
            Assert.That(report, Does.Not.Contain("Infinity"));
            Assert.That(report, Does.Not.Contain("stopped early"));
        }

        [Test]
        public void AssetChecksLodAudit_SourceTraversalCreatesTheCollectionForTheChecks()
        {
            var state = CreateBareBatchState();
            SetStateProperty(
                state,
                "GameplayTraversalContainers",
                new List<Shared.Core.PackFiles.Models.IPackFileContainer>());

            Call(
                "AuditSourceLods",
                state,
                new List<string> { "models\\a.variantmeshdefinition" },
                CancellationToken.None,
                null,
                null);

            var checks = GetStateProperty<object>(state, "AssetChecks");
            Assert.That(Get<IDictionary>(checks, "Components"), Is.Empty);
            Assert.That(Get<IList>(checks, "Roots"), Is.Empty);
        }

        [Test]
        public void ParamListClone_KeepsValuesAndIsIndependent()
        {
            var source = new ParamList<int>();
            source.Set(WeightedParamterIds.IntParams_Alpha_index, 1);
            source.Set(WeightedParamterIds.IntParams_Decal_index, 2);

            var clone = source.Clone();

            Assert.That(clone.Values, Is.EqualTo(source.Values));
            clone.Set(WeightedParamterIds.IntParams_Alpha_index, 0);
            Assert.That(
                source.Get(WeightedParamterIds.IntParams_Alpha_index),
                Is.EqualTo(1));
        }

        [Test]
        public void WeightedMaterialClone_KeepsParameterLists()
        {
            var model = CreateWeightedModel(1f);
            var material = (WeightedMaterial)model.Material;
            material.IntParams.Set(WeightedParamterIds.IntParams_Alpha_index, 1);
            material.FloatParams.Set(WeightedParamterIds.FloatParams_UvScaleX, 2f);

            var clone = (WeightedMaterial)material.Clone();

            Assert.That(
                clone.IntParams.Get(WeightedParamterIds.IntParams_Alpha_index),
                Is.EqualTo(1));
            Assert.That(
                clone.FloatParams.Get(WeightedParamterIds.FloatParams_UvScaleX),
                Is.EqualTo(2f));
        }

        [Test]
        public void RmvMergeIdentity_DiffersWhenEmbeddedAlphaParamDiffers()
        {
            var opaque = CreateWeightedModel(1f);
            var alpha = CreateWeightedModel(1f);
            ((WeightedMaterial)opaque.Material).IntParams.Set(
                WeightedParamterIds.IntParams_Alpha_index,
                0);
            ((WeightedMaterial)alpha.Material).IntParams.Set(
                WeightedParamterIds.IntParams_Alpha_index,
                1);
            var same = CreateWeightedModel(2f);
            ((WeightedMaterial)same.Material).IntParams.Set(
                WeightedParamterIds.IntParams_Alpha_index,
                0);

            var identity = GetStaticMethod("GetRmvMergeIdentity");
            var opaqueIdentity = (string)identity.Invoke(null, [opaque])!;

            Assert.That(
                (string)identity.Invoke(null, [alpha])!,
                Is.Not.EqualTo(opaqueIdentity));
            Assert.That(
                (string)identity.Invoke(null, [same])!,
                Is.EqualTo(opaqueIdentity));
        }

        [Test]
        public void MergeOwned_KeepsEmbeddedMaterialParameters()
        {
            foreach (var partCount in new[] { 1, 2 })
            {
                var parts = Enumerable
                    .Range(0, partCount)
                    .Select(index =>
                    {
                        var part = CreateWeightedModel(index + 1f);
                        ((WeightedMaterial)part.Material).IntParams.Set(
                            WeightedParamterIds.IntParams_Alpha_index,
                            1);
                        return part;
                    })
                    .ToArray();

                var merged = MergeOwned(parts);

                Assert.That(
                    ((WeightedMaterial)merged.Material).IntParams.Get(
                        WeightedParamterIds.IntParams_Alpha_index),
                    Is.EqualTo(1),
                    $"parts={partCount}");
            }
        }

        // ------------------------------------------------------------------------------
        // Standalone texture downscale.
        // ------------------------------------------------------------------------------

        private static string DownscaleReason(string constantName)
        {
            return (string)GetServiceType()
                .GetField(constantName, BindingFlags.NonPublic | BindingFlags.Static)!
                .GetRawConstantValue()!;
        }

        private static (string Outcome, double Scale, int Width, int Height, int Limiting) Decide(
            double[] densities,
            double reference,
            int width,
            int height)
        {
            var decision = Call("ChooseTextureDownscale", densities, reference, width, height);
            return (
                Get<object>(decision, "Outcome").ToString()!,
                Get<double>(decision, "Scale"),
                Get<int>(decision, "NewWidth"),
                Get<int>(decision, "NewHeight"),
                Get<int>(decision, "LimitingIndex"));
        }

        [TestCase(150.0, 1.0)]
        [TestCase(199.0, 1.0)]
        [TestCase(200.0, 0.5)]
        [TestCase(399.0, 0.5)]
        [TestCase(400.0, 0.25)]
        [TestCase(5000.0, 0.25)]
        public void DownscaleScale_SharesTheAtlasOutlierRuleAndBuckets(
            double density,
            double expected)
        {
            // Reference 100, outlier multiplier 1.5: density 200 is exactly 0.75 requested.
            Assert.That(
                (double)Call("CalculateTexelDensityResolutionScale", density, 100.0, 4096),
                Is.EqualTo(expected));
        }

        [Test]
        public void DownscaleScale_AtlasSizeClampStillAppliesToTheLongestSide()
        {
            Assert.That(
                (double)Call("CalculateTexelDensityResolutionScale", 10000.0, 100.0, 512),
                Is.EqualTo(0.5));
            Assert.That(
                (double)Call("CalculateTexelDensityResolutionScale", 10000.0, 100.0, 256),
                Is.EqualTo(1.0));
        }

        [Test]
        public void DownscaleDecision_BucketsHalfQuarterAndFullResolution()
        {
            Assert.That(Decide([300], 100, 1024, 1024), Is.EqualTo(("Candidate", 0.5, 512, 512, 0)));
            Assert.That(Decide([600], 100, 1024, 1024), Is.EqualTo(("Candidate", 0.25, 256, 256, 0)));
            Assert.That(Decide([190], 100, 1024, 1024).Outcome, Is.EqualTo("NeedsFullResolution"));
            Assert.That(Decide([100], 100, 1024, 1024).Outcome, Is.EqualTo("NeedsFullResolution"));
        }

        [Test]
        public void DownscaleDecision_MinimumDimensionAndNonSquareTextures()
        {
            // 0.25 would give 128 px, so the decision steps back to 0.5.
            Assert.That(Decide([600], 100, 512, 512), Is.EqualTo(("Candidate", 0.5, 256, 256, 0)));
            // Neither 0.25 nor 0.5 keeps 256 px.
            Assert.That(Decide([600], 100, 256, 256).Outcome, Is.EqualTo("BelowMinimumDimension"));
            // 2048x512 at 0.25 would be 512x128; 0.5 gives 1024x256.
            Assert.That(Decide([600], 100, 2048, 512), Is.EqualTo(("Candidate", 0.5, 1024, 256, 0)));
            // 1024x256 at 0.5 would be 512x128: the short side decides.
            Assert.That(Decide([300], 100, 1024, 256).Outcome, Is.EqualTo("BelowMinimumDimension"));
            // 2048x1024 at 0.25 stays above 256 on both sides and stays a power of two.
            Assert.That(Decide([600], 100, 2048, 1024), Is.EqualTo(("Candidate", 0.25, 512, 256, 0)));
        }

        [Test]
        public void DownscaleDecision_TheLargestScaleAnyUserNeedsWins()
        {
            var both = Decide([600, 300], 100, 1024, 1024);
            Assert.That((both.Outcome, both.Scale, both.Limiting), Is.EqualTo(("Candidate", 0.5, 1)));

            var oneNeedsFull = Decide([600, 300, 140], 100, 1024, 1024);
            Assert.That(oneNeedsFull.Outcome, Is.EqualTo("NeedsFullResolution"));
            Assert.That(oneNeedsFull.Limiting, Is.EqualTo(2));
        }

        [Test]
        public void DownscaleDensity_MatchesTheAtlasTexelDensityDefinition()
        {
            // A 3 x 1.5 world quad with a 0.8 x 0.4 UV rectangle on a 1024x512 texture.
            RmvModel model = new()
            {
                Mesh = new RmvMesh
                {
                    VertexList =
                    [
                        new CommonVertex { Position = new Vector4(0, 0, 0, 1), Uv = new Vector2(0, 0) },
                        new CommonVertex { Position = new Vector4(3, 0, 0, 1), Uv = new Vector2(0.8f, 0) },
                        new CommonVertex { Position = new Vector4(3, 1.5f, 0, 1), Uv = new Vector2(0.8f, 0.4f) },
                        new CommonVertex { Position = new Vector4(0, 1.5f, 0, 1), Uv = new Vector2(0, 0.4f) },
                    ],
                    IndexList = [0, 1, 2, 0, 2, 3],
                },
            };
            var arguments = new object?[] { model, 0.0 };
            Assert.That((bool)GetStaticMethod("TryCalculateUvPerWorld").Invoke(null, arguments)!, Is.True);
            var perWorld = (double)arguments[1]!;

            var candidateType = GetNestedType("AtlasCandidate");
            var constructor = candidateType.GetConstructors(AnyInstance)
                .OrderByDescending(item => item.GetParameters().Length)
                .First();
            var parameters = constructor.GetParameters();
            var values = parameters
                .Select(parameter => parameter.ParameterType.IsValueType
                    ? Activator.CreateInstance(parameter.ParameterType)
                    : null)
                .ToArray();
            values[Array.FindIndex(parameters, p => p.Name == "Model")] = model;
            values[Array.FindIndex(parameters, p => p.Name == "Width")] = 1024;
            values[Array.FindIndex(parameters, p => p.Name == "Height")] = 512;
            var candidate = constructor.Invoke(values);
            var atlasArguments = new object?[] { candidate, 0.0 };
            Assert.That(
                (bool)GetStaticMethod("TryCalculateTexelDensity").Invoke(null, atlasArguments)!,
                Is.True);

            Assert.That(
                perWorld * Math.Sqrt(1024.0 * 512.0),
                Is.EqualTo((double)atlasArguments[1]!).Within(1e-9).Percent);
        }

        // --- DDS fixtures -----------------------------------------------------------------

        private static byte[] BuildBcDds(
            int width,
            int height,
            int mipCount,
            string format = "BC1",
            bool dx10 = true,
            bool srgb = true,
            int dxgiFormat = 0)
        {
            var bytesPerBlock = format is "BC1" or "BC4" ? 8 : 16;
            var levels = new List<byte[]>();
            for (var level = 0; level < mipCount; level++)
            {
                var levelWidth = Math.Max(1, width >> level);
                var levelHeight = Math.Max(1, height >> level);
                var bytes = new byte[
                    Math.Max(1, (levelWidth + 3) / 4) *
                    Math.Max(1, (levelHeight + 3) / 4) *
                    bytesPerBlock];
                for (var index = 0; index < bytes.Length; index++)
                    bytes[index] = (byte)((level * 37 + index * 11) % 251);
                levels.Add(bytes);
            }

            var headerSize = dx10 ? 148 : 128;
            var dds = new byte[headerSize + levels.Sum(level => level.Length)];
            "DDS "u8.CopyTo(dds);
            BitConverter.GetBytes(124).CopyTo(dds, 4);
            BitConverter.GetBytes(
                0x1 | 0x2 | 0x4 | 0x1000 | 0x80000 | (mipCount > 1 ? 0x20000 : 0)).CopyTo(dds, 8);
            BitConverter.GetBytes(height).CopyTo(dds, 12);
            BitConverter.GetBytes(width).CopyTo(dds, 16);
            BitConverter.GetBytes(levels[0].Length).CopyTo(dds, 20);
            BitConverter.GetBytes(mipCount).CopyTo(dds, 28);
            BitConverter.GetBytes(32).CopyTo(dds, 76);
            BitConverter.GetBytes(4).CopyTo(dds, 80);
            (dx10 ? "DX10"u8.ToArray() : format == "BC1" ? "DXT1"u8.ToArray() : "DXT5"u8.ToArray())
                .CopyTo(dds, 84);
            BitConverter.GetBytes(0x1000 | (mipCount > 1 ? 0x400008 : 0)).CopyTo(dds, 108);
            if (dx10)
            {
                var dxgi = dxgiFormat != 0
                    ? dxgiFormat
                    : format == "BC1"
                        ? (srgb ? 72 : 71)
                        : format == "BC7"
                            ? (srgb ? 99 : 98)
                            : (srgb ? 78 : 77);
                BitConverter.GetBytes(dxgi).CopyTo(dds, 128);
                BitConverter.GetBytes(3).CopyTo(dds, 132);
                BitConverter.GetBytes(1).CopyTo(dds, 140);
            }

            var offset = headerSize;
            foreach (var level in levels)
            {
                level.CopyTo(dds, offset);
                offset += level.Length;
            }

            return dds;
        }

        private static (int Width, int Height, int Mips, string Format, bool Srgb, bool Dx10, long Bytes)
            ReadDds(byte[] dds)
        {
            var arguments = new object?[] { dds.AsSpan(0, Math.Min(dds.Length, 148)).ToArray(), null };
            Assert.That(
                (bool)GetStaticMethod("TryReadDdsLayout").Invoke(null, arguments)!,
                Is.True,
                "DDS header should be readable");
            var layout = arguments[1]!;
            return (
                Get<int>(layout, "Width"),
                Get<int>(layout, "Height"),
                Get<int>(layout, "MipCount"),
                Get<string>(layout, "Format"),
                Get<bool>(layout, "IsSrgb"),
                Get<bool>(layout, "IsDx10"),
                Get<long>(layout, "ChainBytes"));
        }

        private static bool TryDecodeDds(byte[] dds, out int width, out int height)
        {
            var arguments = new object?[] { dds, null, 0, 0, null };
            var decoded = (bool)GetStaticMethod("TryDecodeDdsTopLevel").Invoke(null, arguments)!;
            width = (int)arguments[2]!;
            height = (int)arguments[3]!;
            return decoded;
        }

        private static object GetLayout(byte[] dds)
        {
            var arguments = new object?[] { dds.AsSpan(0, Math.Min(dds.Length, 148)).ToArray(), null };
            Assert.That((bool)GetStaticMethod("TryReadDdsLayout").Invoke(null, arguments)!, Is.True);
            return arguments[1]!;
        }

        [Test]
        public void MipSlicing_LevelZeroIsByteIdenticalToTheSourcesLevelOne()
        {
            var source = BuildBcDds(512, 512, 10, "BC1", dx10: false, srgb: false);
            var layout = GetLayout(source);
            var arguments = new object?[] { source, layout, 1, null, null };
            Assert.That((bool)GetStaticMethod("TrySliceDdsMips").Invoke(null, arguments)!, Is.True);
            var sliced = (byte[])arguments[3]!;

            var info = ReadDds(sliced);
            Assert.That((info.Width, info.Height, info.Mips), Is.EqualTo((256, 256, 9)));
            Assert.That(info.Format, Is.EqualTo("BC1"));
            Assert.That(info.Dx10, Is.False);

            // Source level 0 is 512x512 (32768 blocks of 8 bytes); level 1 is 256x256.
            const int sourceLevel0 = 128 * 128 * 8;
            const int sourceLevel1 = 64 * 64 * 8;
            Assert.That(sourceLevel0, Is.EqualTo(131072));
            Assert.That(
                sliced.AsSpan(128, sourceLevel1).ToArray(),
                Is.EqualTo(source.AsSpan(128 + sourceLevel0, sourceLevel1).ToArray()));
            Assert.That(sliced.AsSpan(128).ToArray(), Is.EqualTo(source.AsSpan(128 + sourceLevel0).ToArray()));
            Assert.That(sliced.Length, Is.EqualTo(source.Length - sourceLevel0));
            Assert.That(TryDecodeDds(sliced, out var width, out var height), Is.True);
            Assert.That((width, height), Is.EqualTo((256, 256)));
        }

        [Test]
        public void MipSlicing_KeepsTheDx10HeaderAndSrgbFlagAndCanSkipTwoLevels()
        {
            var source = BuildBcDds(1024, 512, 11, "BC3", dx10: true, srgb: true);
            var layout = GetLayout(source);
            var arguments = new object?[] { source, layout, 2, null, null };
            Assert.That((bool)GetStaticMethod("TrySliceDdsMips").Invoke(null, arguments)!, Is.True);
            var sliced = (byte[])arguments[3]!;

            var info = ReadDds(sliced);
            Assert.That((info.Width, info.Height, info.Mips), Is.EqualTo((256, 128, 9)));
            Assert.That((info.Format, info.Srgb, info.Dx10), Is.EqualTo(("BC3", true, true)));
            // The DX10 extension header is carried over unchanged.
            Assert.That(sliced.AsSpan(128, 20).ToArray(), Is.EqualTo(source.AsSpan(128, 20).ToArray()));
            Assert.That(sliced.Length, Is.EqualTo(148 + info.Bytes));
            Assert.That(TryDecodeDds(sliced, out var width, out var height), Is.True);
            Assert.That((width, height), Is.EqualTo((256, 128)));
        }

        [Test]
        public void MipSlicing_RefusesToSliceAwayEveryLevel()
        {
            var source = BuildBcDds(256, 256, 1, "BC1", dx10: false, srgb: false);
            var arguments = new object?[] { source, GetLayout(source), 1, null, null };
            Assert.That((bool)GetStaticMethod("TrySliceDdsMips").Invoke(null, arguments)!, Is.False);
            Assert.That(arguments[4], Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void Reencode_ProducesThePlannedSizeFormatAndACompleteMipChain()
        {
            foreach (var (format, srgb, dx10) in new[]
                     {
                         ("BC1", true, true),
                         ("BC3", false, true),
                         ("BC3", false, false),
                     })
            {
                var source = BuildBcDds(512, 512, 1, format, dx10, srgb);
                var arguments = new object?[] { source, GetLayout(source), 256, 256, srgb, null, null };
                Assert.That(
                    (bool)GetStaticMethod("TryReencodeDds").Invoke(null, arguments)!,
                    Is.True,
                    $"{format} srgb={srgb} dx10={dx10}: {arguments[6]}");
                var result = (byte[])arguments[5]!;

                var info = ReadDds(result);
                Assert.That((info.Width, info.Height, info.Mips), Is.EqualTo((256, 256, 9)));
                Assert.That((info.Format, info.Srgb, info.Dx10), Is.EqualTo((format, srgb, dx10)));
                Assert.That(result.Length, Is.EqualTo((dx10 ? 148 : 128) + info.Bytes));
                Assert.That(TryDecodeDds(result, out var width, out var height), Is.True);
                Assert.That((width, height), Is.EqualTo((256, 256)));
            }
        }

        [Test]
        public void Reencode_RefusesFormatsItDoesNotWrite()
        {
            var source = BuildBcDds(512, 512, 1, "BC3", dx10: true, srgb: false, dxgiFormat: 98);
            var arguments = new object?[] { source, GetLayout(source), 256, 256, false, null, null };
            Assert.That((bool)GetStaticMethod("TryReencodeDds").Invoke(null, arguments)!, Is.False);
        }

        // --- Synthetic pack ---------------------------------------------------------------

        private sealed class DownscaleFixture
        {
            public Dictionary<string, byte[]> Source { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, byte[]> Output { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, byte[]> Vanilla { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, RmvFile> Rigids { get; } = new(StringComparer.OrdinalIgnoreCase);

            public void Add(string path, byte[] data)
            {
                Source[path] = data;
                Output[path] = data;
            }

            public void AddText(string path, string text)
                => Add(path, System.Text.Encoding.UTF8.GetBytes(text));

            public void AddTexture(
                string path,
                int size,
                int mips,
                string format = "BC1",
                bool dx10 = true,
                bool srgb = true,
                int dxgiFormat = 0)
                => Add(path, BuildBcDds(size, size, mips, format, dx10, srgb, dxgiFormat));

            public void AddMaterial(string path, params (string Slot, string Texture)[] textures)
                => AddText(path, DownscaleMaterialXml(textures));

            // One WSModel/rigid pair; every part is a unit quad whose UV scale gives the
            // wanted texel density at the given texture size.
            public void AddModel(
                string rigidPath,
                string wsPath,
                params (string Material, double Density, int TextureSize)[] parts)
            {
                Rigids[rigidPath] = new RmvFile
                {
                    ModelList =
                    [
                        parts.Select(part => CreateDensityPart(part.Density / part.TextureSize)).ToArray(),
                    ],
                };
                AddText(
                    wsPath,
                    CreateWsModelDocument(rigidPath, [parts.Select(part => part.Material).ToArray()])
                        .OuterXml);
            }
        }

        private static string DownscaleMaterialXml(params (string Slot, string Texture)[] textures)
        {
            return "<material><name>x</name><shader>shaders\\weighted2_character.xml.shader</shader><textures>" +
                   string.Concat(textures.Select(texture =>
                       $"<texture><slot version=\"2\">{texture.Slot}</slot>" +
                       $"<source>{texture.Texture}</source></texture>")) +
                   "</textures></material>";
        }

        private static RmvModel CreateDensityPart(double uvPerWorld)
        {
            var scale = (float)uvPerWorld;
            CommonVertex Vertex(float x, float y) => new()
            {
                Position = new Vector4(x, y, 0, 1),
                Uv = new Vector2(x * scale, y * scale),
            };

            return new RmvModel
            {
                Mesh = new RmvMesh
                {
                    VertexList = [Vertex(0, 0), Vertex(1, 0), Vertex(1, 1), Vertex(0, 1)],
                    IndexList = [0, 1, 2, 0, 2, 3],
                },
            };
        }

        private static Moq.Mock<Shared.Core.PackFiles.Models.IPackFileContainer> CreateDownscaleContainer(
            Dictionary<string, byte[]> files,
            bool isCaPackFile = false)
        {
            Shared.Core.PackFiles.Models.PackFile Make(string path)
                => new(
                    System.IO.Path.GetFileName(path),
                    new Shared.Core.PackFiles.Models.FileSources.MemorySource(files[path]));

            var container = new Moq.Mock<Shared.Core.PackFiles.Models.IPackFileContainer>();
            container.Setup(item => item.IsCaPackFile).Returns(isCaPackFile);
            container.Setup(item => item.GetAllFiles())
                .Returns(() => files.ToDictionary(entry => entry.Key, entry => Make(entry.Key)));
            container.Setup(item => item.FindFile(Moq.It.IsAny<string>()))
                .Returns<string>(path => files.ContainsKey(path) ? Make(path) : null);
            container.Setup(item => item.ContainsFile(Moq.It.IsAny<string>()))
                .Returns<string>(path => files.ContainsKey(path));
            return container;
        }

        private static (object Service, object State) CreateDownscaleState(
            DownscaleFixture fixture,
            bool downscale,
            bool buildAtlases = false,
            double? atlasReference = 100.0)
        {
            var source = CreateDownscaleContainer(fixture.Source);
            var output = CreateDownscaleContainer(fixture.Output);
            var vanilla = CreateDownscaleContainer(fixture.Vanilla, isCaPackFile: true);
            var packFileService = new Moq.Mock<Editors.PackOptimizer.Core.IPackOptimizerPackFiles>();
            packFileService
                .Setup(service => service.AddFilesToPack(
                    Moq.It.IsAny<Shared.Core.PackFiles.Models.IPackFileContainer>(),
                    Moq.It.IsAny<List<Shared.Core.PackFiles.Models.NewPackFileEntry>>()))
                .Callback<Shared.Core.PackFiles.Models.IPackFileContainer, List<Shared.Core.PackFiles.Models.NewPackFileEntry>>(
                    (_, entries) =>
                    {
                        foreach (var entry in entries)
                        {
                            var key = (entry.DirectoyPath + "\\" + entry.PackFile.Name).ToLowerInvariant();
                            fixture.Output[key] = entry.PackFile.DataSource.ReadData();
                        }
                    });
            var service = Activator.CreateInstance(
                GetServiceType(),
                [
                    packFileService.Object, null,
                ])!;
            var state = GetNestedType("BatchState")
                .GetConstructors(AnyInstance)
                .Single()
                .Invoke(
                [
                    source.Object,
                    output.Object,
                    packFileService.Object,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    true,
                    true,
                    true,
                ]);
            SetStateProperty(state, "StructuralMergeConsumerDiscoveryComplete", true);
            SetStateProperty(state, "BuildAtlases", buildAtlases);
            SetStateProperty(state, "DownscaleTextures", downscale);
            SetStateProperty(
                state,
                "GameplayTraversalContainers",
                new List<Shared.Core.PackFiles.Models.IPackFileContainer> { vanilla.Object });
            if (atlasReference.HasValue)
                AddToList(state, "TexelDensityGlobalReferences", atlasReference.Value);
            // Healthy gameplay resolution with no lord, hero or unresolved consumer; the
            // character-rule tests replace both.
            InstallDownscaleGameplay(state, CreateDownscaleResolution(true), CreateDownscaleIndex());
            return (service, state);
        }

        private static object RunDownscale(
            object service,
            object state,
            DownscaleFixture fixture)
        {
            Func<string, RmvFile?> loader = path => fixture.Rigids.GetValueOrDefault(path);
            GetServiceType()
                .GetMethod("RunTextureDownscale", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [state, CancellationToken.None, null, loader]);
            return Get<object>(state, "TextureDownscale");
        }

        private static List<object> Candidates(object result)
            => Get<IEnumerable>(Get<object>(result, "Plan"), "Candidates").Cast<object>().ToList();

        private static object Candidate(object result, string path)
            => Candidates(result).Single(candidate => Get<string>(candidate, "Path") == path);

        private static Dictionary<string, int> Reasons(object result)
            => Get<Dictionary<string, int>>(Get<object>(result, "Plan"), "IneligibleReasons");

        private const string DownscaleRigid = "variantmeshes\\wh_main\\r1.rigid_model_v2";
        private const string DownscaleWsModel = "variantmeshes\\wh_main\\r1.wsmodel";

        // Every texture is 1024x1024 with a full mip chain unless said otherwise; the
        // reference density is 100 so a density of 300 asks for 0.5x and 600 for 0.25x.
        private static DownscaleFixture BuildStandardDownscaleFixture()
        {
            var fixture = new DownscaleFixture();
            fixture.AddTexture("textures\\t_half.dds", 1024, 11);
            fixture.AddTexture("textures\\t_agg.dds", 1024, 11);
            fixture.AddTexture("textures\\t_full.dds", 1024, 11);
            fixture.AddTexture("textures\\t_slot.dds", 1024, 11);
            fixture.AddTexture("textures\\t_small.dds", 256, 9);
            fixture.AddTexture("textures\\t_vmd.dds", 1024, 11);
            fixture.AddTexture("textures\\t_bc7.dds", 1024, 1, "BC3", dx10: true, dxgiFormat: 98);
            fixture.AddTexture("textures\\t_override.dds", 1024, 11);
            fixture.Vanilla["textures\\t_override.dds"] = [1];
            fixture.AddTexture("textures\\t_orphan.dds", 1024, 11);
            fixture.AddTexture("textures\\t_unr.dds", 1024, 11);
            fixture.AddTexture("textures\\t_gen_kept.dds", 1024, 11);
            fixture.AddTexture("textures\\t_reenc.dds", 1024, 1, "BC3", dx10: true, srgb: false);
            fixture.AddTexture("textures\\t_unused.dds", 1024, 11);

            fixture.AddMaterial("materials\\m_half.xml.material", ("t_xml_base_colour", "textures\\t_half.dds"));
            fixture.AddMaterial("materials\\m_agg_a.xml.material", ("t_xml_base_colour", "textures\\t_agg.dds"));
            fixture.AddMaterial("materials\\m_agg_b.xml.material", ("t_xml_base_colour", "textures\\t_agg.dds"));
            fixture.AddMaterial("materials\\m_full_a.xml.material", ("t_xml_base_colour", "textures\\t_full.dds"));
            fixture.AddMaterial("materials\\m_full_b.xml.material", ("t_xml_base_colour", "textures\\t_full.dds"));
            fixture.AddMaterial(
                "materials\\m_slot.xml.material",
                ("t_xml_base_colour", "textures\\t_slot.dds"),
                ("t_xml_blood_map", "textures\\t_slot.dds"));
            fixture.AddMaterial("materials\\m_small.xml.material", ("t_xml_base_colour", "textures\\t_small.dds"));
            fixture.AddMaterial("materials\\m_vmd.xml.material", ("t_xml_base_colour", "textures\\t_vmd.dds"));
            fixture.AddMaterial("materials\\m_bc7.xml.material", ("t_xml_base_colour", "textures\\t_bc7.dds"));
            fixture.AddMaterial("materials\\m_override.xml.material", ("t_xml_base_colour", "textures\\t_override.dds"));
            fixture.AddMaterial("materials\\m_orphan.xml.material", ("t_xml_base_colour", "textures\\t_orphan.dds"));
            fixture.AddMaterial("materials\\m_unr.xml.material", ("t_xml_base_colour", "textures\\t_unr.dds"));
            fixture.AddMaterial("materials\\m_vanilla.xml.material", ("t_xml_base_colour", "textures\\vanilla_only.dds"));
            fixture.AddMaterial("materials\\m_reenc.xml.material", ("t_xml_normal", "textures\\t_reenc.dds"));
            fixture.AddText(
                "materials\\broken.xml.material",
                "<material><textures><texture>textures\\t_unr.dds</source>");
            fixture.AddText(
                "variantmeshes\\wh_main\\unit.variantmeshdefinition",
                "<VARIANT_MESH><decal_diffuse>textures\\t_vmd.dds</decal_diffuse></VARIANT_MESH>");

            fixture.AddModel(
                DownscaleRigid,
                DownscaleWsModel,
                ("materials\\m_half.xml.material", 300, 1024),
                ("materials\\m_agg_a.xml.material", 600, 1024),
                ("materials\\m_agg_b.xml.material", 300, 1024),
                ("materials\\m_full_a.xml.material", 600, 1024),
                ("materials\\m_full_b.xml.material", 140, 1024),
                ("materials\\m_slot.xml.material", 600, 1024),
                ("materials\\m_small.xml.material", 600, 256),
                ("materials\\m_vmd.xml.material", 300, 1024),
                ("materials\\m_bc7.xml.material", 300, 1024),
                ("materials\\m_override.xml.material", 300, 1024),
                ("materials\\m_unr.xml.material", 300, 1024),
                ("materials\\m_vanilla.xml.material", 300, 1024),
                ("materials\\m_reenc.xml.material", 300, 1024));

            // An atlas-generated material that kept a source texture, and its atlas texture.
            fixture.AddTexture("textures\\asset_editor\\atlases\\atlas1.dds", 2048, 12);
            fixture.AddMaterial(
                "materials\\asset_editor\\gen1.xml.material",
                ("t_xml_base_colour", "textures\\asset_editor\\atlases\\atlas1.dds"),
                ("t_xml_mask", "textures\\t_gen_kept.dds"));
            return fixture;
        }

        private static (object Service, object State) CreateStandardDownscaleState(
            DownscaleFixture fixture,
            bool downscale,
            double? atlasReference = 100.0)
        {
            var (service, state) = CreateDownscaleState(
                fixture,
                downscale,
                buildAtlases: false,
                atlasReference: atlasReference);
            AddToSet(state, "GeneratedTexturePaths", "textures\\asset_editor\\atlases\\atlas1.dds");
            AddToSet(state, "GeneratedMaterialPaths", "materials\\asset_editor\\gen1.xml.material");
            return (service, state);
        }

        [Test]
        public void Downscale_ReportOnly_ListsCandidatesAndWritesNothing()
        {
            var fixture = BuildStandardDownscaleFixture();
            var before = fixture.Output.ToDictionary(entry => entry.Key, entry => entry.Value);
            var (service, state) = CreateStandardDownscaleState(fixture, downscale: false);

            var result = RunDownscale(service, state, fixture);

            Assert.That(Get<string?>(result, "Failure"), Is.Null);
            Assert.That(Get<bool>(result, "Applied"), Is.False);
            var paths = Candidates(result).Select(candidate => Get<string>(candidate, "Path")).Order().ToArray();
            Assert.That(
                paths,
                Is.EqualTo(new[] { "textures\\t_agg.dds", "textures\\t_half.dds", "textures\\t_reenc.dds" }));
            Assert.That(fixture.Output.Keys.Order(), Is.EqualTo(before.Keys.Order()));
            foreach (var (path, bytes) in before)
                Assert.That(fixture.Output[path], Is.SameAs(bytes), path);
            Assert.That(
                Get<IDictionary>(state, "PhaseDurations").Contains("Texture downscale analysis"),
                Is.True);
        }

        [Test]
        public void Downscale_Candidates_CarryScaleMethodSizesAndTheLimitingUser()
        {
            var fixture = BuildStandardDownscaleFixture();
            var (service, state) = CreateStandardDownscaleState(fixture, downscale: false);
            var result = RunDownscale(service, state, fixture);

            var half = Candidate(result, "textures\\t_half.dds");
            Assert.That(Get<double>(half, "Scale"), Is.EqualTo(0.5));
            Assert.That((Get<int>(half, "NewWidth"), Get<int>(half, "NewHeight")), Is.EqualTo((512, 512)));
            Assert.That(Get<bool>(half, "UseSlicing"), Is.True);
            Assert.That(Get<int>(half, "SkipLevels"), Is.EqualTo(1));
            Assert.That(Get<long>(half, "BytesAfter"), Is.LessThan(Get<long>(half, "BytesBefore")));
            Assert.That(Get<double>(half, "LimitingRelativeDensity"), Is.EqualTo(3.0).Within(0.01));

            // Two parts want 0.25x and 0.5x of one file: the file keeps the larger scale.
            var aggregated = Candidate(result, "textures\\t_agg.dds");
            Assert.That(Get<double>(aggregated, "Scale"), Is.EqualTo(0.5));
            Assert.That(Get<int>(aggregated, "MaterialCount"), Is.EqualTo(2));
            Assert.That(Get<int>(aggregated, "PartCount"), Is.EqualTo(2));
            Assert.That(Get<string>(aggregated, "LimitingUser"), Does.Contain("part 2"));

            var reencoded = Candidate(result, "textures\\t_reenc.dds");
            Assert.That(Get<bool>(reencoded, "UseSlicing"), Is.False);
            Assert.That(Get<long>(reencoded, "BytesAfter"), Is.GreaterThan(0));
        }

        [Test]
        public void Downscale_EachIneligibilityReasonIsCounted()
        {
            var fixture = BuildStandardDownscaleFixture();
            var (service, state) = CreateStandardDownscaleState(fixture, downscale: false);
            var result = RunDownscale(service, state, fixture);
            var plan = Get<object>(result, "Plan");
            var reasons = Reasons(result);
            var examples = Get<Dictionary<string, List<string>>>(plan, "IneligibleExamples");

            void Expect(string constant, string path)
            {
                var reason = DownscaleReason(constant);
                Assert.That(reasons.GetValueOrDefault(reason), Is.EqualTo(1), constant);
                Assert.That(examples[reason].Single(), Does.Contain(path), constant);
            }

            Expect("TextureDownscaleReasonNotOwned", "vanilla_only.dds");
            Expect("TextureDownscaleReasonOverridesVanilla", "t_override.dds");
            Expect("TextureDownscaleReasonSlot", "t_slot.dds");
            Expect("TextureDownscaleReasonNonMaterial", "t_vmd.dds");
            Expect("TextureDownscaleReasonUnreadableReference", "t_unr.dds");
            Expect("TextureDownscaleReasonGeneratedMaterial", "t_gen_kept.dds");
            Expect("TextureDownscaleReasonNoUser", "t_orphan.dds");
            Expect("TextureDownscaleReasonFormat", "t_bc7.dds");
            Expect("TextureDownscaleReasonMinimumDimension", "t_small.dds");

            // A user needing full resolution is not an ineligibility but "not oversized".
            Assert.That(Get<int>(plan, "NeedsFullResolution"), Is.EqualTo(1));
            Assert.That(Get<int>(plan, "GeneratedUntouched"), Is.EqualTo(1));
            Assert.That(Get<int>(plan, "Unreferenced"), Is.EqualTo(1));
            Assert.That(reasons.Values.Sum(), Is.EqualTo(9));
        }

        [Test]
        public void Downscale_ReferencedOnlyThroughAMaterialOutsideTheAssessedScope_IsNotACandidate()
        {
            // A second material uses the half-resolution texture but no WSModel binds it.
            var fixture = BuildStandardDownscaleFixture();
            fixture.AddMaterial(
                "materials\\m_other.xml.material",
                ("t_xml_base_colour", "textures\\t_half.dds"));
            var (service, state) = CreateStandardDownscaleState(fixture, downscale: false);

            var result = RunDownscale(service, state, fixture);

            Assert.That(
                Candidates(result).Select(candidate => Get<string>(candidate, "Path")),
                Does.Not.Contain("textures\\t_half.dds"));
            Assert.That(Reasons(result)[DownscaleReason("TextureDownscaleReasonNoUser")], Is.EqualTo(2));
        }

        [Test]
        public void Downscale_PartWithoutUsableUvs_MakesItsTexturesUnassessable()
        {
            var fixture = BuildStandardDownscaleFixture();
            fixture.Rigids[DownscaleRigid].ModelList[0][0] = new RmvModel
            {
                Mesh = new RmvMesh { VertexList = [], IndexList = [] },
            };
            var (service, state) = CreateStandardDownscaleState(fixture, downscale: false);

            var result = RunDownscale(service, state, fixture);

            Assert.That(
                Candidates(result).Select(candidate => Get<string>(candidate, "Path")),
                Does.Not.Contain("textures\\t_half.dds"));
            Assert.That(
                Reasons(result)[DownscaleReason("TextureDownscaleReasonUnassessableUser")],
                Is.EqualTo(1));
        }

        [Test]
        public void Downscale_ReferenceDensityIsDerivedWhenTheAtlasStageProvidedNone()
        {
            var fixture = new DownscaleFixture();
            fixture.AddTexture("textures\\t_ref.dds", 1024, 11);
            fixture.AddTexture("textures\\t_half.dds", 1024, 11);
            fixture.AddMaterial("materials\\m_ref.xml.material", ("t_xml_base_colour", "textures\\t_ref.dds"));
            fixture.AddMaterial("materials\\m_half.xml.material", ("t_xml_base_colour", "textures\\t_half.dds"));
            var parts = Enumerable
                .Repeat(("materials\\m_ref.xml.material", 100.0, 1024), 8)
                .Append(("materials\\m_half.xml.material", 300.0, 1024))
                .ToArray();
            fixture.AddModel(DownscaleRigid, DownscaleWsModel, parts);
            var (service, state) = CreateDownscaleState(fixture, downscale: false, atlasReference: null);

            var result = RunDownscale(service, state, fixture);
            var plan = Get<object>(result, "Plan");

            Assert.That(Get<double?>(plan, "ReferenceDensity"), Is.EqualTo(100.0).Within(0.01));
            Assert.That(Get<string>(plan, "ReferenceOrigin"), Does.StartWith("derived here"));
            Assert.That(Get<int>(plan, "ReferenceSamples"), Is.EqualTo(9));
            Assert.That(
                Candidates(result).Select(candidate => Get<string>(candidate, "Path")),
                Is.EqualTo(new[] { "textures\\t_half.dds" }));
        }

        [Test]
        public void Downscale_LateRunKeepsTheEarlyReferenceWhenAStageRemovedSampleParts()
        {
            var fixture = new DownscaleFixture();
            fixture.AddTexture("textures\\t_low.dds", 1024, 11);
            fixture.AddTexture("textures\\t_mid.dds", 1024, 11);
            fixture.AddTexture("textures\\t_half.dds", 1024, 11);
            fixture.AddMaterial("materials\\m_low.xml.material", ("t_xml_base_colour", "textures\\t_low.dds"));
            fixture.AddMaterial("materials\\m_mid.xml.material", ("t_xml_base_colour", "textures\\t_mid.dds"));
            fixture.AddMaterial("materials\\m_half.xml.material", ("t_xml_base_colour", "textures\\t_half.dds"));
            var parts = Enumerable.Repeat(("materials\\m_low.xml.material", 100.0, 1024), 4)
                .Concat(Enumerable.Repeat(("materials\\m_mid.xml.material", 200.0, 1024), 4))
                .Append(("materials\\m_half.xml.material", 450.0, 1024))
                .ToArray();
            fixture.AddModel(DownscaleRigid, DownscaleWsModel, parts);
            var (service, state) = CreateDownscaleState(fixture, downscale: false, atlasReference: null);

            var earlyPlan = Get<object>(RunEarlyDownscale(service, state, fixture), "Plan");
            var earlyReference = Get<double?>(earlyPlan, "ReferenceDensity")!.Value;
            // A later stage (atlas or repack) moved the four 200 parts: they no longer sample.
            var processed = Get<object>(state, "ProcessedMeshes");
            foreach (var part in new[] { 4, 5, 6, 7 })
                processed.GetType().GetMethod("Add")!.Invoke(processed, [CreateMeshKey(DownscaleRigid, 0, part)]);

            var plan = Get<object>(RunDownscale(service, state, fixture), "Plan");
            Assert.That(Get<double?>(plan, "ReferenceDensity"), Is.EqualTo(earlyReference));
            Assert.That(Get<int>(plan, "ReferenceSamples"), Is.EqualTo(Get<int>(earlyPlan, "ReferenceSamples")));
            Assert.That(Get<string>(plan, "ReferenceOrigin"), Does.Contain("early analysis on the source state"));

            var disabled = GetServiceType().GetField(
                "TextureDownscaleReferencePinDisabled", BindingFlags.NonPublic | BindingFlags.Static)!;
            var old = (bool)disabled.GetValue(null)!;
            try
            {
                disabled.SetValue(null, true);
                var derived = Get<object>(RunDownscale(service, state, fixture), "Plan");
                Assert.That(Get<string>(derived, "ReferenceOrigin"), Does.StartWith("derived here"));
                Assert.That(Get<double?>(derived, "ReferenceDensity"), Is.Not.EqualTo(earlyReference));
            }
            finally
            {
                disabled.SetValue(null, old);
            }
        }

        [Test]
        public void Downscale_WithoutAReferenceNothingIsACandidate()
        {
            var fixture = new DownscaleFixture();
            fixture.AddTexture("textures\\t_half.dds", 1024, 11);
            fixture.AddMaterial("materials\\m_half.xml.material", ("t_xml_base_colour", "textures\\t_half.dds"));
            fixture.AddModel(DownscaleRigid, DownscaleWsModel, ("materials\\m_half.xml.material", 300, 1024));
            var (service, state) = CreateDownscaleState(fixture, downscale: false, atlasReference: null);

            var result = RunDownscale(service, state, fixture);

            Assert.That(Candidates(result), Is.Empty);
            Assert.That(Reasons(result)[DownscaleReason("TextureDownscaleReasonNoReference")], Is.EqualTo(1));
        }

        [Test]
        public void Downscale_OptionOn_ReplacesEligibleTexturesAtTheSamePathAndLeavesTheRestAlone()
        {
            var fixture = BuildStandardDownscaleFixture();
            var before = fixture.Output.ToDictionary(entry => entry.Key, entry => entry.Value);
            var (service, state) = CreateStandardDownscaleState(fixture, downscale: true);

            var result = RunDownscale(service, state, fixture);

            Assert.That(Get<bool>(result, "Applied"), Is.True);
            Assert.That(Get<List<string>>(result, "RewriteFailures"), Is.Empty);
            Assert.That(Get<int>(result, "Sliced"), Is.EqualTo(2));
            Assert.That(Get<int>(result, "Reencoded"), Is.EqualTo(1));

            var changed = fixture.Output
                .Where(entry => !ReferenceEquals(entry.Value, before[entry.Key]))
                .Select(entry => entry.Key)
                .Order()
                .ToArray();
            Assert.That(
                changed,
                Is.EqualTo(new[] { "textures\\t_agg.dds", "textures\\t_half.dds", "textures\\t_reenc.dds" }));
            Assert.That(fixture.Output.Keys.Order(), Is.EqualTo(before.Keys.Order()));

            var half = ReadDds(fixture.Output["textures\\t_half.dds"]);
            Assert.That((half.Width, half.Height, half.Mips), Is.EqualTo((512, 512, 10)));
            Assert.That((half.Format, half.Srgb, half.Dx10), Is.EqualTo(("BC1", true, true)));
            // The sliced level 0 is the source's level 1, byte for byte.
            const int sourceLevel0 = 256 * 256 * 8;
            const int sourceLevel1 = 128 * 128 * 8;
            Assert.That(
                fixture.Output["textures\\t_half.dds"].AsSpan(148, sourceLevel1).ToArray(),
                Is.EqualTo(before["textures\\t_half.dds"].AsSpan(148 + sourceLevel0, sourceLevel1).ToArray()));

            var reencoded = ReadDds(fixture.Output["textures\\t_reenc.dds"]);
            Assert.That(
                (reencoded.Width, reencoded.Height, reencoded.Mips, reencoded.Format, reencoded.Srgb, reencoded.Dx10),
                Is.EqualTo((512, 512, 10, "BC3", false, true)));
            Assert.That(
                Get<IDictionary>(state, "PhaseDurations").Contains("Texture downscale rewrite"),
                Is.True);

            var errors = new List<string>();
            GetStaticMethod("ValidateDownscaledTextures").Invoke(null, [state, errors, CancellationToken.None]);
            Assert.That(errors, Is.Empty);
            Assert.That(
                Get<IList<string>>(state, "ValidationMessages"),
                Has.Some.Contain("3 downscaled texture(s)"));
        }

        [Test]
        public void Downscale_Validation_ReportsATextureThatNoLongerMatchesItsPlan()
        {
            var fixture = BuildStandardDownscaleFixture();
            var (service, state) = CreateStandardDownscaleState(fixture, downscale: true);
            RunDownscale(service, state, fixture);

            // Wrong size, then a truncated mip chain, then a missing file.
            fixture.Output["textures\\t_half.dds"] = BuildBcDds(256, 256, 9);
            fixture.Output["textures\\t_agg.dds"] = BuildBcDds(512, 512, 5);
            fixture.Output.Remove("textures\\t_reenc.dds");
            var errors = new List<string>();
            GetStaticMethod("ValidateDownscaledTextures").Invoke(null, [state, errors, CancellationToken.None]);

            Assert.That(errors, Has.Count.EqualTo(3));
            Assert.That(errors.Single(error => error.Contains("t_half.dds")), Does.Contain("256x256"));
            Assert.That(errors.Single(error => error.Contains("t_agg.dds")), Does.Contain("mip level"));
            Assert.That(errors.Single(error => error.Contains("t_reenc.dds")), Does.Contain("missing"));
        }

        // Atlas-on state: the parts the atlas replaced no longer use their source materials.
        private const string AtlasRigid = "variantmeshes\\wh_main\\ra.rigid_model_v2";
        private const string AtlasWsModel = "variantmeshes\\wh_main\\ra.wsmodel";
        private const string AtlasTexture = "textures\\asset_editor\\atlases\\atlas1.dds";

        private static (DownscaleFixture Fixture, object Service, object State) BuildAtlasOnState(
            bool downscale)
        {
            var fixture = new DownscaleFixture();
            fixture.AddTexture("textures\\t_moved.dds", 1024, 11);
            fixture.AddTexture("textures\\t_moved2.dds", 1024, 11);
            fixture.AddTexture("textures\\t_reduced.dds", 1024, 11);
            fixture.AddTexture("textures\\t_keep.dds", 1024, 11);
            fixture.AddTexture("textures\\t_gen_kept.dds", 1024, 11);
            fixture.AddMaterial("materials\\m_moved.xml.material", ("t_xml_base_colour", "textures\\t_moved.dds"));
            fixture.AddMaterial("materials\\m_moved2.xml.material", ("t_xml_base_colour", "textures\\t_moved2.dds"));
            fixture.AddMaterial("materials\\m_reduced.xml.material", ("t_xml_base_colour", "textures\\t_reduced.dds"));
            fixture.AddMaterial("materials\\m_keep.xml.material", ("t_xml_base_colour", "textures\\t_keep.dds"));
            // Source WSModel: parts 0, 1 and 3 are the ones the atlas replaced.
            fixture.AddModel(
                AtlasRigid,
                AtlasWsModel,
                ("materials\\m_moved.xml.material", 300, 1024),
                ("materials\\m_reduced.xml.material", 600, 1024),
                ("materials\\m_reduced.xml.material", 300, 1024),
                ("materials\\m_keep.xml.material", 600, 1024),
                ("materials\\m_keep.xml.material", 140, 1024),
                ("materials\\m_moved2.xml.material", 300, 1024));

            // Output after the atlas run.
            fixture.AddTexture(AtlasTexture, 2048, 12);
            fixture.Output[AtlasTexture] = fixture.Source[AtlasTexture];
            fixture.Source.Remove(AtlasTexture);
            const string generated = "materials\\asset_editor\\gen1.xml.material";
            fixture.Output[generated] = System.Text.Encoding.UTF8.GetBytes(
                DownscaleMaterialXml(
                    ("t_xml_base_colour", AtlasTexture),
                    ("t_xml_mask", "textures\\t_gen_kept.dds")));
            fixture.Output["variantmeshes\\wh_main\\ra.wsmodel"] = System.Text.Encoding.UTF8.GetBytes(
                CreateWsModelDocument(
                    AtlasRigid,
                    [[generated, generated, "materials\\m_reduced.xml.material", generated,
                      "materials\\m_keep.xml.material", generated]]).OuterXml);
            // Pruned: the file and its material are gone. Kept: the file stays but its
            // only material was removed with the atlased part.
            fixture.Output.Remove("textures\\t_moved.dds");
            fixture.Output.Remove("materials\\m_moved.xml.material");
            fixture.Output.Remove("materials\\m_moved2.xml.material");

            var (service, state) = CreateDownscaleState(fixture, downscale, buildAtlases: true);
            AddToSet(state, "GeneratedTexturePaths", AtlasTexture);
            AddToSet(state, "GeneratedMaterialPaths", generated);
            var processed = Get<object>(state, "ProcessedMeshes");
            foreach (var part in new[] { 0, 1, 3, 5 })
            {
                processed.GetType().GetMethod("Add")!.Invoke(
                    processed,
                    [CreateMeshKey(AtlasRigid, 0, part)]);
            }

            return (fixture, service, state);
        }

        [Test]
        public void Downscale_AtlasOn_AssessesOnlyTheRemainingUsersAndNeverTouchesAtlasTextures()
        {
            var (fixture, service, state) = BuildAtlasOnState(downscale: true);
            var before = fixture.Output.ToDictionary(entry => entry.Key, entry => entry.Value);

            var result = RunDownscale(service, state, fixture);
            var plan = Get<object>(result, "Plan");

            // Only t_reduced qualifies: its remaining user needs half the resolution.
            Assert.That(
                Candidates(result).Select(candidate => Get<string>(candidate, "Path")),
                Is.EqualTo(new[] { "textures\\t_reduced.dds" }));
            var reduced = Candidate(result, "textures\\t_reduced.dds");
            Assert.That(Get<double>(reduced, "Scale"), Is.EqualTo(0.5));
            Assert.That(Get<int>(reduced, "PartCount"), Is.EqualTo(1));
            Assert.That(Get<string>(reduced, "LimitingUser"), Does.Contain("part 2"));

            // t_keep: the atlased 600 user is gone but the remaining one needs full size.
            Assert.That(Get<int>(plan, "NeedsFullResolution"), Is.EqualTo(1));
            // t_moved was pruned, t_moved2 lost its only reference.
            Assert.That(Get<int>(plan, "AtlasRemovedAllUsers"), Is.EqualTo(2));
            // t_reduced and t_keep lost a user to the atlas but kept others.
            Assert.That(Get<int>(plan, "AssessedWithReducedUserSet"), Is.EqualTo(2));
            Assert.That(Get<int>(plan, "TexturesAssessed"), Is.EqualTo(3));
            Assert.That(Get<int>(plan, "GeneratedUntouched"), Is.EqualTo(1));
            Assert.That(
                Reasons(result)[DownscaleReason("TextureDownscaleReasonGeneratedMaterial")],
                Is.EqualTo(1));
            Assert.That(Get<double?>(plan, "ReferenceDensity"), Is.EqualTo(100.0));
            Assert.That(Get<string>(plan, "ReferenceOrigin"), Does.Contain("atlas stage"));

            Assert.That(Get<bool>(result, "Applied"), Is.True);
            Assert.That(ReadDds(fixture.Output["textures\\t_reduced.dds"]).Width, Is.EqualTo(512));
            foreach (var untouched in new[]
                     {
                         "textures\\t_keep.dds",
                         "textures\\t_gen_kept.dds",
                         "textures\\t_moved2.dds",
                         AtlasTexture,
                     })
            {
                Assert.That(fixture.Output[untouched], Is.SameAs(before[untouched]), untouched);
            }

            var errors = new List<string>();
            GetStaticMethod("ValidateDownscaledTextures").Invoke(null, [state, errors, CancellationToken.None]);
            Assert.That(errors, Is.Empty);
        }

        [Test]
        public void Downscale_AtlasOnWithSeveralAtlasReferences_DerivesItsOwn()
        {
            var (fixture, service, state) = BuildAtlasOnState(downscale: false);
            AddToList(state, "TexelDensityGlobalReferences", 250.0);

            var result = RunDownscale(service, state, fixture);
            var plan = Get<object>(result, "Plan");

            Assert.That(Get<string>(plan, "ReferenceOrigin"), Does.Contain("none was reused"));
        }

        // --- Report -----------------------------------------------------------------------

        private static string BuildDownscaleSection(object state)
        {
            var builder = new System.Text.StringBuilder();
            GetStaticMethod("AppendTextureDownscaleSection").Invoke(null, [builder, state]);
            return builder.ToString();
        }

        [Test]
        public void DownscaleReport_ListedOnly_StatesTheModeReferenceCountsAndTheList()
        {
            var fixture = BuildStandardDownscaleFixture();
            var (service, state) = CreateStandardDownscaleState(fixture, downscale: false);
            RunDownscale(service, state, fixture);

            var report = BuildDownscaleSection(state);

            Assert.That(report, Does.StartWith("Texture downscale candidates"));
            Assert.That(report, Does.Contain("Mode: LISTED ONLY"));
            Assert.That(report, Does.Contain("no texture file was changed"));
            Assert.That(report, Does.Contain("Pack reference density: 100"));
            Assert.That(report, Does.Contain("taken from the atlas stage"));
            Assert.That(report, Does.Match(@"Eligible candidates: 3 \(0[.,]5x=3\)"));
            Assert.That(report, Does.Contain("Not eligible: 8"));
            Assert.That(report, Does.Contain("Method that would be used: mip slicing 2"));
            Assert.That(report, Does.Contain("re-encode 1"));
            Assert.That(report, Does.Contain("textures\\t_half.dds: 1024x1024 -> 512x512, BC1_SRGB"));
            Assert.That(report, Does.Contain("limiting user:"));
            Assert.That(report, Does.Contain("BCn bytes with the whole mip chain"));
            Assert.That(report, Does.Contain("Assessed:"));
            Assert.That(report, Does.Not.Contain("NaN"));
        }

        [Test]
        public void DownscaleReport_Applied_SaysSoAndCountsTheMethods()
        {
            var fixture = BuildStandardDownscaleFixture();
            var (service, state) = CreateStandardDownscaleState(fixture, downscale: true);
            RunDownscale(service, state, fixture);

            var report = BuildDownscaleSection(state);

            Assert.That(report, Does.Contain("Mode: APPLIED. 3 of 3 candidate(s) were rewritten"));
            Assert.That(report, Does.Contain("mip slicing 2, re-encode 1, not rewritten 0"));
            Assert.That(report, Does.Contain("Method used: mip slicing 2"));
        }

        [Test]
        public void DownscaleReport_ListTruncatesAfterSixtyEntries()
        {
            var fixture = new DownscaleFixture();
            var parts = new List<(string, double, int)>();
            for (var index = 0; index < 63; index++)
            {
                var texture = $"textures\\t_{index:D2}.dds";
                var material = $"materials\\m_{index:D2}.xml.material";
                fixture.AddTexture(texture, 1024, 11);
                fixture.AddMaterial(material, ("t_xml_base_colour", texture));
                parts.Add((material, 300, 1024));
            }

            fixture.AddModel(DownscaleRigid, DownscaleWsModel, parts.ToArray());
            var (service, state) = CreateDownscaleState(fixture, downscale: false);
            RunDownscale(service, state, fixture);

            var report = BuildDownscaleSection(state);

            Assert.That(report, Does.Contain("Top 60 by bytes saved:"));
            Assert.That(report, Does.Contain("... and 3 more"));
        }

        [Test]
        public void DownscaleReport_WithoutDataOrWithAFailure_IsWellFormed()
        {
            var noRun = BuildDownscaleSection(CreateNoAtlasState());
            Assert.That(noRun, Does.Contain("Texture downscale analysis did not run."));

            var empty = new DownscaleFixture();
            var (service, state) = CreateDownscaleState(empty, downscale: false, atlasReference: null);
            RunDownscale(service, state, empty);
            var emptyReport = BuildDownscaleSection(state);
            Assert.That(emptyReport, Does.Contain("Eligible candidates: 0"));
            Assert.That(emptyReport, Does.Contain("Pack reference density: none"));
            Assert.That(emptyReport, Does.Not.Contain("NaN"));
            Assert.That(emptyReport, Does.Not.Contain("limiting user"));

            var failed = CreateNoAtlasState();
            var result = Activator.CreateInstance(GetNestedType("TextureDownscaleResult"), nonPublic: true)!;
            result.GetType().GetProperty("Requested", AnyInstance)!.SetValue(result, true);
            result.GetType().GetProperty("Failure", AnyInstance)!.SetValue(result, "InvalidOperationException: boom");
            SetStateProperty(failed, "TextureDownscale", result);
            var failedReport = BuildDownscaleSection(failed);
            Assert.That(failedReport, Does.Contain("analysis failed and was skipped: InvalidOperationException: boom"));
            Assert.That(failedReport, Does.Contain("No texture was changed although downscaling was requested."));
        }

        [Test]
        public void DownscaleReport_ModeLineAndCrossRigidOnlyBehaviour()
        {
            string Write(Action<object> configure)
            {
                var reportPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"downscale_report_{Guid.NewGuid():N}.txt");
                try
                {
                    var state = CreateNoAtlasState(reportPath: reportPath);
                    configure(state);
                    GetStaticMethod("WriteReport").Invoke(
                        null,
                        [state, new List<string>(), true, null]);
                    return System.IO.File.ReadAllText(reportPath);
                }
                finally
                {
                    System.IO.File.Delete(reportPath);
                }
            }

            var off = Write(_ => { });
            Assert.That(off, Does.Contain("Texture downscaling: NO"));
            Assert.That(off, Does.Contain("Texture downscale candidates"));

            var on = Write(state => SetStateProperty(state, "DownscaleTextures", true));
            Assert.That(on, Does.Contain("Texture downscaling: YES"));

            var crossRigidOnly = Write(state => SetStateProperty(state, "CrossRigidOnly", true));
            Assert.That(crossRigidOnly, Does.Contain("Texture downscaling: NO"));
            Assert.That(crossRigidOnly, Does.Not.Contain("Texture downscale candidates"));
        }

        // --- Lord / hero rule -------------------------------------------------------------

        private static Type ServicesType(string name)
            => Assembly.Load("Editors.PackOptimizer.Core")
                .GetType($"Editors.PackOptimizer.Core.{name}", throwOnError: true)!;

        private static object CreateDownscaleUsage(
            string vmd,
            string category,
            int numMen,
            string consumer = "UnitVariant",
            string role = "Men",
            string unit = "unit_a")
        {
            var counts = Activator.CreateInstance(ServicesType("Wh3UnitVisualCounts"), 1, 0, 0, 0)!;
            var usage = Activator.CreateInstance(
                ServicesType("Wh3UnitCategoryUsage"),
                vmd,
                unit,
                "land_" + unit,
                string.Empty,
                string.Empty,
                string.Empty,
                numMen,
                Enum.Parse(ServicesType("Wh3ArmyUnitCategory"), category),
                Enum.Parse(ServicesType("Wh3UnitVisualRole"), role),
                1,
                counts)!;
            usage.GetType().GetProperty("ConsumerType")!
                .SetValue(usage, Enum.Parse(ServicesType("Wh3VmdConsumerType"), consumer));
            return usage;
        }

        private static Array TypedArray(string typeName, params object[] items)
        {
            var array = Array.CreateInstance(ServicesType(typeName), items.Length);
            for (var index = 0; index < items.Length; index++)
                array.SetValue(items[index], index);
            return array;
        }

        private static object CreateUnresolvedConsumer(string category)
            => Activator.CreateInstance(
                ServicesType("Wh3UnresolvedVmdConsumer"),
                "vmd",
                "unit_u",
                "land_unit_u",
                Enum.Parse(ServicesType("Wh3ArmyUnitCategory"), category),
                Enum.Parse(ServicesType("Wh3UnitVisualRole"), "Men"),
                "reason")!;

        private static object CreateDownscaleResolution(
            bool healthy,
            params (string Vmd, object Usage)[] usages)
        {
            var type = ServicesType("Wh3UnitCategoryResolution");
            var scenario = ServicesType("Wh3ArmyVisualScenario")
                .GetProperty("Default", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
                .GetValue(null);
            var constructor = type.GetConstructors(AnyInstance)
                .Single(candidate => candidate.GetParameters().Length > 10);
            var arguments = constructor.GetParameters().Select(parameter =>
            {
                var parameterType = parameter.ParameterType;
                if (parameterType == typeof(bool))
                    return (object?)healthy;
                if (parameterType == typeof(int))
                    return 0;
                if (parameterType == typeof(string))
                    return healthy ? string.Empty : "test: tables missing";
                if (parameterType == ServicesType("Wh3ArmyVisualScenario"))
                    return scenario;
                var definition = parameterType.GetGenericTypeDefinition();
                if (definition == typeof(IReadOnlyDictionary<,>))
                {
                    return Activator.CreateInstance(
                        typeof(Dictionary<,>).MakeGenericType(parameterType.GetGenericArguments()));
                }

                return Array.CreateInstance(parameterType.GetGenericArguments()[0], 0);
            }).ToArray();
            var resolution = constructor.Invoke(arguments);
            var byVmd = (IDictionary)type.GetProperty("UsagesByVmd")!.GetValue(resolution)!;
            foreach (var group in usages.GroupBy(entry => entry.Vmd))
            {
                var list = (IList)Activator.CreateInstance(
                    typeof(List<>).MakeGenericType(ServicesType("Wh3UnitCategoryUsage")))!;
                foreach (var entry in group)
                    list.Add(entry.Usage);
                byVmd[group.Key.ToLowerInvariant()] = list;
            }

            return resolution;
        }

        private static object CreateDownscaleIndex(
            params (string Root, string WsModel)[] links)
        {
            var index = Activator.CreateInstance(
                GetNestedType("GameplayMeshDependencyIndex"),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                [false],
                null)!;
            var kind = Enum.Parse(GetNestedType("GameplayMeshDependencyRootKind"), "SourcePackVmd");
            foreach (var (root, wsModel) in links)
            {
                index.GetType().GetMethod("RegisterRoot")!.Invoke(index, [root, kind]);
                index.GetType().GetMethod("AddRootReference")!.Invoke(index, [wsModel, root]);
            }

            return index;
        }

        private static void InstallDownscaleGameplay(object state, object? resolution, object? index)
        {
            SetStateProperty(state, "UnitCategoryResolution", resolution);
            SetStateProperty(state, "GameplayMeshDependencyIndex", index);
        }

        private const string LordRoot = "variantmeshes\\wh_variantmodels\\hu1\\emp\\lorenzo_lupo\\lupo.variantmeshdefinition";
        private const string TroopRoot = "variantmeshes\\wh_variantmodels\\hu1\\emp\\troop\\troop.variantmeshdefinition";
        private const string LordRigid = "variantmeshes\\wh_main\\lord.rigid_model_v2";
        private const string LordWsModel = "variantmeshes\\wh_main\\lord.wsmodel";

        [TestCase("Lord", 20, "UnitVariant", "Men", true)]
        [TestCase("Hero", 20, "UnitVariant", "Men", true)]
        [TestCase("Lord", 20, "Mount", "Mount", true)]
        [TestCase("InfantryMissile", 40, "BattleAgentArtSet", "Men", true)]
        [TestCase("InfantryMissile", 40, "CustomBattleGeneralUniform", "Men", true)]
        [TestCase("InfantryMissile", 1, "UnitVariant", "Men", true)]
        [TestCase("Unknown", 1, "Mount", "Mount", true)]
        [TestCase("InfantryMissile", 40, "UnitVariant", "Men", false)]
        [TestCase("CavalryChariot", 40, "Mount", "Mount", false)]
        [TestCase("MonsterBeast", 1, "UnitVariant", "Men", false)]
        [TestCase("ArtilleryWarMachine", 1, "Engine", "Engine", false)]
        public void DownscaleCharacter_UsageSignals(
            string category,
            int numMen,
            string consumer,
            string role,
            bool expected)
        {
            var usage = CreateDownscaleUsage("v", category, numMen, consumer, role);
            var arguments = new object?[] { usage, null };

            var result = (bool)GetStaticMethod("IsTextureDownscaleCharacterUsage").Invoke(null, arguments)!;

            Assert.That(result, Is.EqualTo(expected));
            Assert.That((string)arguments[1]!, expected ? Is.Not.Empty : Is.Empty);
        }

        private static (string Kind, string Detail) ClassifyRoot(
            Array? usages = null,
            Array? unresolved = null,
            Array? roster = null,
            Array? direct = null,
            bool incomplete = false)
        {
            var arguments = new object?[]
            {
                usages ?? TypedArray("Wh3UnitCategoryUsage"),
                unresolved ?? TypedArray("Wh3UnresolvedVmdConsumer"),
                roster ?? TypedArray("Wh3ResolvedUnitVisual"),
                direct ?? TypedArray("Wh3UnitDirectAssetUsage"),
                incomplete,
                null,
            };
            var kind = GetStaticMethod("ClassifyTextureDownscaleRoot").Invoke(null, arguments)!;
            return (kind.ToString()!, (string)arguments[5]!);
        }

        [Test]
        public void DownscaleCharacter_RootClassification()
        {
            var troop = CreateDownscaleUsage(TroopRoot, "InfantryMissile", 40);
            var lord = CreateDownscaleUsage(LordRoot, "Lord", 1, "BattleAgentArtSet", unit: "lorenzo_lupo");
            var cavalryMount = CreateDownscaleUsage(TroopRoot, "CavalryChariot", 40, "Mount", "Mount");

            Assert.That(ClassifyRoot().Kind, Is.EqualTo("RankAndFile"), "no consumer, no link");
            Assert.That(
                ClassifyRoot(TypedArray("Wh3UnitCategoryUsage", troop, cavalryMount)).Kind,
                Is.EqualTo("RankAndFile"));

            var character = ClassifyRoot(TypedArray("Wh3UnitCategoryUsage", troop, lord));
            Assert.That(character.Kind, Is.EqualTo("Character"), "any character consumer wins");
            Assert.That(character.Detail, Does.Contain("lorenzo_lupo"));

            Assert.That(
                ClassifyRoot(unresolved: TypedArray("Wh3UnresolvedVmdConsumer", CreateUnresolvedConsumer("InfantryMissile"))).Kind,
                Is.EqualTo("Unclassified"));
            Assert.That(
                ClassifyRoot(unresolved: TypedArray("Wh3UnresolvedVmdConsumer", CreateUnresolvedConsumer("Hero"))).Kind,
                Is.EqualTo("Character"),
                "an unresolved consumer that is known to be a hero still counts as a character");
            Assert.That(ClassifyRoot(incomplete: true).Kind, Is.EqualTo("Unclassified"));
            Assert.That(
                ClassifyRoot(TypedArray("Wh3UnitCategoryUsage", lord), incomplete: true).Kind,
                Is.EqualTo("Character"));
        }

        private static DownscaleFixture BuildCharacterFixture()
        {
            var fixture = new DownscaleFixture();
            foreach (var name in new[] { "lord", "troop", "both" })
            {
                fixture.AddTexture($"textures\\t_{name}.dds", 1024, 11);
                fixture.AddMaterial(
                    $"materials\\m_{name}.xml.material",
                    ("t_xml_base_colour", $"textures\\t_{name}.dds"));
            }

            fixture.AddModel(
                LordRigid,
                LordWsModel,
                ("materials\\m_lord.xml.material", 300, 1024),
                ("materials\\m_both.xml.material", 300, 1024));
            fixture.AddModel(
                DownscaleRigid,
                DownscaleWsModel,
                ("materials\\m_troop.xml.material", 300, 1024),
                ("materials\\m_both.xml.material", 300, 1024));
            return fixture;
        }

        private static (object Service, object State) CreateCharacterState(
            DownscaleFixture fixture,
            object? resolution = null,
            object? index = null)
        {
            var (service, state) = CreateDownscaleState(fixture, downscale: false);
            InstallDownscaleGameplay(
                state,
                resolution ?? CreateDownscaleResolution(
                    true,
                    (LordRoot, CreateDownscaleUsage(LordRoot, "Lord", 1, "BattleAgentArtSet", unit: "lorenzo_lupo")),
                    (TroopRoot, CreateDownscaleUsage(TroopRoot, "InfantryMissile", 40))),
                index ?? CreateDownscaleIndex((LordRoot, LordWsModel), (TroopRoot, DownscaleWsModel)));
            return (service, state);
        }

        [Test]
        public void DownscaleCharacter_TexturesUsedByALordOrHeroAreNeverCandidates()
        {
            var fixture = BuildCharacterFixture();
            var (service, state) = CreateCharacterState(fixture);

            var result = RunDownscale(service, state, fixture);

            Assert.That(
                Candidates(result).Select(candidate => Get<string>(candidate, "Path")),
                Is.EqualTo(new[] { "textures\\t_troop.dds" }),
                "rank-and-file only is kept; lord only and lord plus rank-and-file are excluded");
            var plan = Get<object>(result, "Plan");
            var reason = DownscaleReason("TextureDownscaleReasonCharacter");
            Assert.That(Reasons(result)[reason], Is.EqualTo(2));
            var examples = Get<Dictionary<string, List<string>>>(plan, "IneligibleExamples")[reason];
            Assert.That(examples.Any(example => example.Contains("t_lord.dds")), Is.True);
            Assert.That(examples.All(example => example.Contains("lorenzo_lupo")), Is.True);
            Assert.That(examples.All(example => example.Contains(LordRoot)), Is.True);
            Assert.That(Get<int>(plan, "CharacterWsModels"), Is.EqualTo(1));
        }

        [Test]
        public void DownscaleCharacter_AMountReachedThroughALordRootIsExcludedButACavalryMountIsNot()
        {
            var fixture = BuildCharacterFixture();
            var mountRoot = "variantmeshes\\wh_variantmodels\\hu1\\emp\\horse\\horse.variantmeshdefinition";
            var resolution = CreateDownscaleResolution(
                true,
                (LordRoot, CreateDownscaleUsage(LordRoot, "InfantryMissile", 40)),
                (mountRoot, CreateDownscaleUsage(mountRoot, "CavalryChariot", 40, "Mount", "Mount")),
                (TroopRoot, CreateDownscaleUsage(TroopRoot, "InfantryMissile", 40)));
            // The only root that reaches the "lord" WSModel is the mount VMD.
            var index = CreateDownscaleIndex((mountRoot, LordWsModel), (TroopRoot, DownscaleWsModel));

            var (cavalryService, cavalryState) = CreateCharacterState(fixture, resolution, index);
            var cavalry = RunDownscale(cavalryService, cavalryState, fixture);
            Assert.That(
                Candidates(cavalry).Select(candidate => Get<string>(candidate, "Path")).Order(),
                Is.EqualTo(new[] { "textures\\t_both.dds", "textures\\t_lord.dds", "textures\\t_troop.dds" }));

            var lordMount = CreateDownscaleResolution(
                true,
                (mountRoot, CreateDownscaleUsage(mountRoot, "Lord", 1, "Mount", "Mount")),
                (TroopRoot, CreateDownscaleUsage(TroopRoot, "InfantryMissile", 40)));
            var (service, state) = CreateCharacterState(fixture, lordMount, index);
            var excluded = RunDownscale(service, state, fixture);
            Assert.That(
                Candidates(excluded).Select(candidate => Get<string>(candidate, "Path")),
                Is.EqualTo(new[] { "textures\\t_troop.dds" }));
        }

        [Test]
        public void DownscaleCharacter_AnUnclassifiableRootHoldsItsTexturesBack()
        {
            var fixture = BuildCharacterFixture();
            var resolution = CreateDownscaleResolution(
                true,
                (TroopRoot, CreateDownscaleUsage(TroopRoot, "InfantryMissile", 40)));
            var unresolved = (IDictionary)resolution.GetType()
                .GetProperty("UnresolvedConsumersByVmd")!.GetValue(resolution)!;
            unresolved[LordRoot.ToLowerInvariant()] =
                TypedArray("Wh3UnresolvedVmdConsumer", CreateUnresolvedConsumer("InfantryMissile"));
            var (service, state) = CreateCharacterState(fixture, resolution);

            var result = RunDownscale(service, state, fixture);

            Assert.That(
                Candidates(result).Select(candidate => Get<string>(candidate, "Path")),
                Is.EqualTo(new[] { "textures\\t_troop.dds" }));
            Assert.That(
                Reasons(result)[DownscaleReason("TextureDownscaleReasonCharacterUnclassified")],
                Is.EqualTo(2));
            Assert.That(
                Reasons(result).GetValueOrDefault(DownscaleReason("TextureDownscaleReasonCharacter")),
                Is.EqualTo(0));
        }

        [Test]
        public void DownscaleCharacter_UnavailableResolution_HoldsEveryCandidateBackAndSaysSo()
        {
            foreach (var resolution in new object?[] { null, CreateDownscaleResolution(false) })
            {
                var fixture = BuildCharacterFixture();
                var (service, state) = CreateCharacterState(fixture, resolution ?? CreateDownscaleResolution(true));
                InstallDownscaleGameplay(state, resolution, CreateDownscaleIndex());

                var result = RunDownscale(service, state, fixture);

                Assert.That(Candidates(result), Is.Empty);
                Assert.That(
                    Reasons(result)[DownscaleReason("TextureDownscaleReasonCharacterUnclassified")],
                    Is.EqualTo(3));
                var report = BuildDownscaleSection(state);
                Assert.That(report, Does.Contain("Character rule status: NOT APPLIED PER ROOT because"));
                Assert.That(report, Does.Contain("character rule unavailable"));
            }

            var noIndexFixture = BuildCharacterFixture();
            var (noIndexService, noIndexState) = CreateCharacterState(noIndexFixture);
            SetStateProperty(noIndexState, "GameplayMeshDependencyIndex", null);
            var noIndex = RunDownscale(noIndexService, noIndexState, noIndexFixture);
            Assert.That(Candidates(noIndex), Is.Empty);
            Assert.That(BuildDownscaleSection(noIndexState), Does.Contain("dependency index was not built"));
        }

        [Test]
        public void DownscaleCharacter_ReportStatesTheRuleAndTheStatus()
        {
            var fixture = BuildCharacterFixture();
            var (service, state) = CreateCharacterState(fixture);
            RunDownscale(service, state, fixture);

            var report = BuildDownscaleSection(state);

            Assert.That(report, Does.Contain("Character rule: a texture used by a lord or hero"));
            Assert.That(report, Does.Contain("Character rule status: applied; 1 source WSModel(s)"));
            Assert.That(report, Does.Contain("2 x used by a lord or hero (single-character unit)"));
            Assert.That(report, Does.Contain("e.g. textures\\t_lord.dds"));
        }

        // --- Count lines ------------------------------------------------------------------

        [TestCase("textures\\a.dds", true)]
        [TestCase("a.DDS", true)]
        [TestCase("base_colour_path", false)]
        [TestCase("mask_path", false)]
        [TestCase("textures\\a.png", false)]
        [TestCase("textures\\a", false)]
        [TestCase("   ", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void DownscaleTexturePath_OnlyDdsFilePathsAreTextures(string? value, bool expected)
        {
            Assert.That(
                (bool)GetStaticMethod("IsTextureDownscaleTexturePath").Invoke(null, [value])!,
                Is.EqualTo(expected));
        }

        [Test]
        public void Downscale_PlaceholderAndNonPathValuesAreNotCountedAsTextures()
        {
            var fixture = BuildStandardDownscaleFixture();
            var (service, state) = CreateStandardDownscaleState(fixture, downscale: false);
            var baseline = RunDownscale(service, state, fixture);
            var baselinePlan = Get<object>(baseline, "Plan");

            var withJunk = BuildStandardDownscaleFixture();
            withJunk.AddMaterial(
                "materials\\m_junk.xml.material",
                ("t_xml_base_colour", "base_colour_path"),
                ("t_xml_mask", "mask_path"),
                ("t_xml_normal", "textures\\not_a_dds.png"),
                ("t_xml_gloss", string.Empty));
            var (junkService, junkState) = CreateStandardDownscaleState(withJunk, downscale: false);
            var junk = RunDownscale(junkService, junkState, withJunk);
            var junkPlan = Get<object>(junk, "Plan");

            Assert.That(
                Get<int>(junkPlan, "TexturesConsidered"),
                Is.EqualTo(Get<int>(baselinePlan, "TexturesConsidered")));
            Assert.That(Reasons(junk), Is.EquivalentTo(Reasons(baseline)));
            Assert.That(
                BuildDownscaleSection(junkState),
                Does.Not.Contain("base_colour_path"));
        }

        [Test]
        public void DownscaleReport_CountLinesReconcileToOneTotal()
        {
            var fixture = BuildStandardDownscaleFixture();
            var (service, state) = CreateStandardDownscaleState(fixture, downscale: false);
            var result = RunDownscale(service, state, fixture);
            var plan = Get<object>(result, "Plan");

            var considered = Get<int>(plan, "TexturesConsidered");
            var notOwned = Reasons(result)[DownscaleReason("TextureDownscaleReasonNotOwned")];
            var assessed = Get<int>(plan, "TexturesAssessed");
            var notEligible = Reasons(result).Values.Sum() - notOwned;
            Assert.That(
                considered,
                Is.EqualTo(
                    notOwned +
                    Get<int>(plan, "AtlasRemovedAllUsers") +
                    Get<int>(plan, "Unreferenced") +
                    assessed));
            Assert.That(
                assessed,
                Is.EqualTo(Candidates(result).Count + Get<int>(plan, "NeedsFullResolution") + notEligible));

            var report = BuildDownscaleSection(state);
            Assert.That(report, Does.Contain($"Textures considered: {considered}"));
            Assert.That(report, Does.Contain($"  Assessed: {assessed}"));
            Assert.That(report, Does.Contain($"    Not eligible: {notEligible}"));
            Assert.That(report, Does.Contain("  Not owned by the source pack (vanilla or unresolved): 1"));
            Assert.That(report, Does.Contain("referenced by nothing found"));
            Assert.That(report, Does.Contain("Generated atlas textures (never touched, not counted above): 1"));
        }

        // --- Vanilla texture coverage -----------------------------------------------------

        private const string CoverageRigid = "variantmeshes\\wh_main\\cov1.rigid_model_v2";
        private const string CoverageWsModel = "variantmeshes\\wh_main\\cov1.wsmodel";
        private const string CoverageVanillaRigid = "variantmeshes\\wh_main\\van1.rigid_model_v2";
        private const string CoverageVanillaWsModel = "variantmeshes\\wh_main\\van1.wsmodel";
        private const string CoverageTexture = "textures\\v_tex.dds";
        private const string CoverageMaterial = "materials\\v_mat.xml.material";

        private static float[][] UvQuad(double u0, double v0, double u1, double v1)
            =>
            [
                [(float)u0, (float)v0, (float)u1, (float)v0, (float)u1, (float)v1],
                [(float)u0, (float)v0, (float)u1, (float)v1, (float)u0, (float)v1],
            ];

        // Triangles with explicit UVs (u0, v0, u1, v1, u2, v2 each).
        private static RmvModel CreateUvPart(params float[][] triangles)
        {
            var vertices = new List<CommonVertex>();
            foreach (var triangle in triangles)
            {
                for (var corner = 0; corner < 3; corner++)
                {
                    vertices.Add(new CommonVertex
                    {
                        Position = new Vector4(corner, triangle[corner * 2], 0, 1),
                        Uv = new Vector2(triangle[corner * 2], triangle[corner * 2 + 1]),
                    });
                }
            }

            return new RmvModel
            {
                Mesh = new RmvMesh
                {
                    VertexList = vertices.ToArray(),
                    IndexList = Enumerable.Range(0, vertices.Count).Select(index => (ushort)index).ToArray(),
                },
            };
        }

        // One WSModel/rigid pair whose parts have explicit UVs. A mod-owned rigid exists in
        // the source pack; a vanilla one is only served by the rigid loader.
        private static void AddCoverageModel(
            DownscaleFixture fixture,
            string rigidPath,
            string wsPath,
            bool modOwnedRigid,
            params (string Material, float[][] Triangles)[] parts)
        {
            fixture.Rigids[rigidPath] = new RmvFile
            {
                ModelList = [parts.Select(part => CreateUvPart(part.Triangles)).ToArray()],
            };
            fixture.AddText(
                wsPath,
                CreateWsModelDocument(rigidPath, [parts.Select(part => part.Material).ToArray()])
                    .OuterXml);
            if (modOwnedRigid)
                fixture.Add(rigidPath, [0]);
        }

        private static void AddVanillaCoverageTexture(
            DownscaleFixture fixture,
            string texture,
            string material,
            int size = 1024)
        {
            fixture.Vanilla[texture] = BuildBcDds(size, size, 11);
            fixture.Vanilla[material] = System.Text.Encoding.UTF8.GetBytes(
                DownscaleMaterialXml(("t_xml_base_colour", texture)));
        }

        private static object RunCoverage(
            object service,
            object state,
            DownscaleFixture fixture,
            TimeSpan? budget = null,
            Func<string, RmvFile?>? loader = null)
        {
            loader ??= path => fixture.Rigids.GetValueOrDefault(path);
            GetServiceType()
                .GetMethod("RunVanillaTextureCoverage", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [state, CancellationToken.None, null, loader, budget]);
            return Get<object>(state, "VanillaTextureCoverage");
        }

        private static List<object> CoverageAssessed(object result)
            => Get<IEnumerable>(result, "Assessed").Cast<object>().ToList();

        private static object CoverageTextureOf(object result, string path)
            => CoverageAssessed(result).Single(texture => Get<string>(texture, "Path") == path);

        private static string BuildCoverageSection(object state)
        {
            var builder = new System.Text.StringBuilder();
            GetStaticMethod("AppendVanillaTextureCoverageSection").Invoke(null, [builder, state]);
            return builder.ToString();
        }

        private static ulong[] NewCoverageGrid()
            => (ulong[])GetStaticMethod("CreateVanillaCoverageGrid").Invoke(null, null)!;

        private static double GridCoverage(ulong[] grid)
            => (double)GetStaticMethod("CountVanillaCoverage").Invoke(null, [grid])!;

        private static bool MarkTriangle(
            ulong[] grid,
            double ax, double ay, double bx, double by, double cx, double cy,
            out bool tiled)
        {
            var arguments = new object?[] { grid, ax, ay, bx, by, cx, cy, null };
            var marked = (bool)GetStaticMethod("MarkVanillaCoverageTriangle").Invoke(null, arguments)!;
            tiled = (bool)arguments[7]!;
            return marked;
        }

        private static ulong[] QuadGrid(double u0, double v0, double u1, double v1)
        {
            var grid = NewCoverageGrid();
            MarkTriangle(grid, u0, v0, u1, v0, u1, v1, out _);
            MarkTriangle(grid, u0, v0, u1, v1, u0, v1, out _);
            return grid;
        }

        private static (object Service, object State, DownscaleFixture Fixture) BuildSingleCoverageState(
            params float[][][] partQuads)
        {
            var fixture = new DownscaleFixture();
            AddVanillaCoverageTexture(fixture, CoverageTexture, CoverageMaterial);
            AddCoverageModel(
                fixture,
                CoverageRigid,
                CoverageWsModel,
                modOwnedRigid: true,
                partQuads.Select(quad => (CoverageMaterial, quad)).ToArray());
            var (service, state) = CreateDownscaleState(fixture, downscale: false);
            return (service, state, fixture);
        }

        [Test]
        public void VanillaCoverage_QuadOverAQuarterOfTheTexture_CoversExactlyAQuarter()
        {
            Assert.That(GridCoverage(QuadGrid(0, 0, 0.5, 0.5)), Is.EqualTo(0.25).Within(1e-12));
            Assert.That(GridCoverage(QuadGrid(0, 0, 1, 1)), Is.EqualTo(1.0).Within(1e-12));
            Assert.That(GridCoverage(NewCoverageGrid()), Is.Zero);
        }

        [Test]
        public void VanillaCoverage_TriangleMarksOnlyTheCellsItTouches()
        {
            var grid = NewCoverageGrid();
            Assert.That(MarkTriangle(grid, 0, 0, 0.5, 0, 0, 0.5, out var tiled), Is.True);

            // Half of the quarter square, plus the cells the diagonal cuts: a bit over 12.5 %.
            Assert.That(tiled, Is.False);
            Assert.That(GridCoverage(grid), Is.InRange(0.125, 0.13));
        }

        [Test]
        public void VanillaCoverage_OverlappingPartsAreNotDoubleCounted()
        {
            var (service, state, fixture) = BuildSingleCoverageState(
                UvQuad(0, 0, 0.5, 0.5),
                UvQuad(0, 0, 0.5, 0.5),
                UvQuad(0.25, 0, 0.5, 0.5));

            var result = RunCoverage(service, state, fixture);
            var texture = CoverageTextureOf(result, CoverageTexture);

            Assert.That(Get<double>(texture, "CoverageFraction"), Is.EqualTo(0.25).Within(1e-12));
            Assert.That(Get<IEnumerable>(texture, "ModParts").Cast<object>().Count(), Is.EqualTo(3));
        }

        [Test]
        public void VanillaCoverage_UvsOffsetByWholeTilesLandOnTheSameCells()
        {
            var baseGrid = QuadGrid(0.1, 0.2, 0.4, 0.45);

            Assert.That(QuadGrid(1.1, 2.2, 1.4, 2.45), Is.EqualTo(baseGrid));
            Assert.That(QuadGrid(-0.9, -2.8, -0.6, -2.55), Is.EqualTo(baseGrid));
        }

        [Test]
        public void VanillaCoverage_TriangleCrossingATileBoundaryMarksBothSides()
        {
            var grid = NewCoverageGrid();

            Assert.That(MarkTriangle(grid, 0.75, 0, 1.25, 0, 1.25, 0.25, out var tiled), Is.True);
            MarkTriangle(grid, 0.75, 0, 1.25, 0.25, 0.75, 0.25, out _);

            // Half a tile wide, wrapping from the right edge to the left edge: not tiling.
            Assert.That(tiled, Is.False);
            Assert.That(GridCoverage(grid), Is.EqualTo(0.5 * 0.25).Within(1e-12));
            Assert.That(grid[0] & 1UL, Is.EqualTo(1UL), "cell 0,0 (wrapped side) is set");
            Assert.That(grid[3] >> 63, Is.EqualTo(1UL), "cell 255,0 is set");
        }

        [Test]
        public void VanillaCoverage_DegenerateTrianglesAreSkippedWithoutCrashing()
        {
            var grid = NewCoverageGrid();

            Assert.That(MarkTriangle(grid, 0.1, 0.1, 0.5, 0.5, 0.9, 0.9, out _), Is.False);
            Assert.That(MarkTriangle(grid, 0.2, 0.2, 0.2, 0.2, 0.2, 0.2, out _), Is.False);
            Assert.That(MarkTriangle(grid, double.NaN, 0, 1, 0, 0, 1, out _), Is.False);
            Assert.That(MarkTriangle(grid, 0, 0, 1e12, 0, 0, 1, out _), Is.False);
            Assert.That(GridCoverage(grid), Is.Zero);
        }

        [Test]
        public void VanillaCoverage_PartWithOnlyDegenerateTrianglesIsNotAssessable()
        {
            var (service, state, fixture) = BuildSingleCoverageState(
                [[0.1f, 0.1f, 0.5f, 0.5f, 0.9f, 0.9f]]);

            var result = RunCoverage(service, state, fixture);
            var texture = CoverageTextureOf(result, CoverageTexture);

            Assert.That(Get<int>(result, "PartsUnassessable"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "DegenerateTriangles"), Is.EqualTo(1));
            Assert.That(
                Get<string>(texture, "NotCroppableReason"),
                Does.Contain("cannot be assessed"));
        }

        [Test]
        public void VanillaCoverage_TriangleSpanningMoreThanOnePeriodMarksTheTextureTiled()
        {
            var (service, state, fixture) = BuildSingleCoverageState(
                UvQuad(0, 0, 0.25, 0.25),
                [[0f, 0f, 1.5f, 0f, 0f, 0.25f]]);

            var result = RunCoverage(service, state, fixture);
            var texture = CoverageTextureOf(result, CoverageTexture);

            Assert.That(Get<bool>(texture, "Tiled"), Is.True);
            Assert.That(Get<bool>(texture, "IsCroppable"), Is.False);
            Assert.That(Get<string>(texture, "NotCroppableReason"), Does.Contain("tiles it"));
        }

        [Test]
        public void VanillaCoverage_ExactlyOnePeriodIsNotTiling()
        {
            var (service, state, fixture) = BuildSingleCoverageState(UvQuad(0, 0, 1, 1));

            var texture = CoverageTextureOf(RunCoverage(service, state, fixture), CoverageTexture);

            Assert.That(Get<bool>(texture, "Tiled"), Is.False);
            Assert.That(Get<double>(texture, "CoverageFraction"), Is.EqualTo(1.0).Within(1e-12));
            Assert.That(Get<long>(texture, "BytesSaved"), Is.Zero);
        }

        [Test]
        public void VanillaCoverage_BoundingRectangleFigureCoversTheCropOfEachPart()
        {
            // Two small corner quads: the triangles cover 2 x 1/64, one rectangle per part
            // covers the same here, but a diagonal sliver shows the difference.
            var (service, state, fixture) = BuildSingleCoverageState(
                [[0f, 0f, 0.5f, 0f, 0f, 0.5f]]);

            var texture = CoverageTextureOf(RunCoverage(service, state, fixture), CoverageTexture);

            Assert.That(Get<double>(texture, "BoundingRectangleFraction"), Is.EqualTo(0.25).Within(1e-12));
            Assert.That(
                Get<double>(texture, "CoverageFraction"),
                Is.InRange(0.125, 0.13));
        }

        [Test]
        public void VanillaCoverage_VanillaMaterialBoundByAModWsModelIsFoundAndPackTexturesAreNotListed()
        {
            var fixture = new DownscaleFixture();
            AddVanillaCoverageTexture(fixture, CoverageTexture, CoverageMaterial);
            fixture.AddTexture("textures\\own.dds", 1024, 11);
            fixture.AddMaterial("materials\\own.xml.material", ("t_xml_base_colour", "textures\\own.dds"));
            AddCoverageModel(
                fixture,
                CoverageRigid,
                CoverageWsModel,
                modOwnedRigid: true,
                (CoverageMaterial, UvQuad(0, 0, 0.1f, 0.1f)),
                ("materials\\own.xml.material", UvQuad(0, 0, 0.1f, 0.1f)));
            var (service, state) = CreateDownscaleState(fixture, downscale: false);

            var result = RunCoverage(service, state, fixture);

            Assert.That(
                CoverageAssessed(result).Select(texture => Get<string>(texture, "Path")),
                Is.EqualTo(new[] { CoverageTexture }));
            Assert.That(Get<int>(result, "OwnedBySourcePack"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "MaterialsNotFound"), Is.Zero);
        }

        [Test]
        public void VanillaCoverage_SplitsUsersByWhoOwnsTheRigid()
        {
            var fixture = new DownscaleFixture();
            AddVanillaCoverageTexture(fixture, CoverageTexture, CoverageMaterial);
            AddCoverageModel(
                fixture, CoverageRigid, CoverageWsModel, modOwnedRigid: true,
                (CoverageMaterial, UvQuad(0, 0, 0.1f, 0.1f)));
            AddCoverageModel(
                fixture, CoverageVanillaRigid, CoverageVanillaWsModel, modOwnedRigid: false,
                (CoverageMaterial, UvQuad(0.5f, 0.5f, 0.6f, 0.6f)),
                (CoverageMaterial, UvQuad(0.7f, 0.5f, 0.8f, 0.6f)));
            var (service, state) = CreateDownscaleState(fixture, downscale: false);

            var texture = CoverageTextureOf(RunCoverage(service, state, fixture), CoverageTexture);

            Assert.That(Get<IEnumerable>(texture, "ModParts").Cast<object>().Count(), Is.EqualTo(1));
            Assert.That(Get<IEnumerable>(texture, "VanillaParts").Cast<object>().Count(), Is.EqualTo(2));
            Assert.That(Get<IEnumerable>(texture, "Materials").Cast<object>().Count(), Is.EqualTo(1));
        }

        [Test]
        public void VanillaCoverage_MeasuresThePackAsAuthored_PartsAtlasedInThisRunStillCount()
        {
            var (service, state, fixture) = BuildSingleCoverageState(
                UvQuad(0, 0, 0.5, 0.5),
                UvQuad(0.5, 0.5, 1, 1));
            var processed = Get<object>(state, "ProcessedMeshes");
            processed.GetType().GetMethod("Add")!.Invoke(
                processed,
                [CreateMeshKey(CoverageRigid, 0, 0)]);

            var texture = CoverageTextureOf(RunCoverage(service, state, fixture), CoverageTexture);

            Assert.That(Get<double>(texture, "CoverageFraction"), Is.EqualTo(0.5).Within(1e-12));
        }

        [TestCase(0.0)]
        [TestCase(0.01)]
        [TestCase(0.04)]
        [TestCase(0.25)]
        [TestCase(0.79)]
        [TestCase(0.81)]
        [TestCase(1.0)]
        public void VanillaCoverage_CropEstimateNeverExceedsTheOriginal(double coverage)
        {
            const long original = 1_398_200;
            var arguments = new object?[] { 1024, 1024, 8, original, coverage, null, null, null };

            GetStaticMethod("EstimateVanillaCrop").Invoke(null, arguments);

            var width = (int)arguments[5]!;
            var height = (int)arguments[6]!;
            var bytes = (long)arguments[7]!;
            Assert.That(bytes, Is.LessThanOrEqualTo(original));
            Assert.That(width, Is.LessThanOrEqualTo(1024));
            Assert.That(height, Is.LessThanOrEqualTo(1024));
            Assert.That(width, Is.GreaterThanOrEqualTo(4));
            Assert.That(width, Is.EqualTo(height));
            // The crop holds the used pixels plus packing slack.
            Assert.That((double)width * height, Is.GreaterThanOrEqualTo(Math.Min(1.0, coverage * 1.25) * 1024 * 1024 - 1));
        }

        [Test]
        public void VanillaCoverage_CropEstimateHalvesWhileTheUsedPixelsStillFit()
        {
            var arguments = new object?[] { 1024, 512, 16, 10_000_000L, 0.04, null, null, null };

            GetStaticMethod("EstimateVanillaCrop").Invoke(null, arguments);

            // 4 % x 1.25 = 5 % of 512 Ki pixels: 256x128 (6.25 %) fits, 128x64 does not.
            Assert.That(arguments[5], Is.EqualTo(256));
            Assert.That(arguments[6], Is.EqualTo(128));
        }

        private static (object Service, object State, DownscaleFixture Fixture) BuildMixedCoverageState()
        {
            var fixture = new DownscaleFixture();
            AddVanillaCoverageTexture(fixture, "textures\\v_small.dds", "materials\\v_small.xml.material");
            AddVanillaCoverageTexture(fixture, "textures\\v_tiled.dds", "materials\\v_tiled.xml.material");
            fixture.AddTexture("textures\\own.dds", 1024, 11);
            fixture.AddMaterial("materials\\own.xml.material", ("t_xml_base_colour", "textures\\own.dds"));
            fixture.AddMaterial("materials\\gone.xml.material", ("t_xml_base_colour", "textures\\missing.dds"));
            fixture.Vanilla["textures\\v_bad.dds"] = [1];
            fixture.AddMaterial("materials\\bad.xml.material", ("t_xml_base_colour", "textures\\v_bad.dds"));
            AddCoverageModel(
                fixture, CoverageRigid, CoverageWsModel, modOwnedRigid: true,
                ("materials\\v_small.xml.material", UvQuad(0, 0, 0.25f, 0.25f)),
                ("materials\\v_tiled.xml.material", UvQuad(0, 0, 1.5f, 0.5f)),
                ("materials\\own.xml.material", UvQuad(0, 0, 0.5f, 0.5f)),
                ("materials\\gone.xml.material", UvQuad(0, 0, 0.5f, 0.5f)),
                ("materials\\bad.xml.material", UvQuad(0, 0, 0.5f, 0.5f)));
            var (service, state) = CreateDownscaleState(fixture, downscale: false);
            return (service, state, fixture);
        }

        [Test]
        public void VanillaCoverage_CountsReconcileToTheReferencedTotal_AndNoPackFileChanges()
        {
            var (service, state, fixture) = BuildMixedCoverageState();
            var sourceBefore = fixture.Source.ToDictionary(entry => entry.Key, entry => entry.Value);
            var outputBefore = fixture.Output.ToDictionary(entry => entry.Key, entry => entry.Value);

            var result = RunCoverage(service, state, fixture);

            Assert.That(Get<string?>(result, "Failure"), Is.Null);
            Assert.That(Get<int>(result, "TexturesReferenced"), Is.EqualTo(5));
            Assert.That(Get<int>(result, "OwnedBySourcePack"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "NotFound"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "HeaderUnreadable"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "NotReached"), Is.Zero);
            Assert.That(CoverageAssessed(result), Has.Count.EqualTo(2));
            Assert.That(
                Get<int>(result, "OwnedBySourcePack") + Get<int>(result, "NotFound") +
                Get<int>(result, "HeaderUnreadable") + Get<int>(result, "NotReached") +
                CoverageAssessed(result).Count,
                Is.EqualTo(Get<int>(result, "TexturesReferenced")));
            Assert.That(Get<IEnumerable>(result, "Croppable").Cast<object>().Count(), Is.EqualTo(1));
            Assert.That(fixture.Source, Is.EquivalentTo(sourceBefore));
            Assert.That(fixture.Output, Is.EquivalentTo(outputBefore));
            Assert.That(
                Get<IDictionary>(state, "PhaseDurations").Contains("Vanilla texture coverage"),
                Is.True);
        }

        [Test]
        public void VanillaCoverage_TextureMentionedByANonMaterialFileIsNotCroppable()
        {
            var (service, state, fixture) = BuildSingleCoverageState(UvQuad(0, 0, 0.1f, 0.1f));
            fixture.AddText(
                "variantmeshes\\wh_main\\unit.variantmeshdefinition",
                $"<VARIANT_MESH><decal_diffuse>{CoverageTexture}</decal_diffuse></VARIANT_MESH>");

            var texture = CoverageTextureOf(RunCoverage(service, state, fixture), CoverageTexture);

            Assert.That(Get<bool>(texture, "IsCroppable"), Is.False);
            Assert.That(Get<string>(texture, "NotCroppableReason"), Does.Contain("non-material file"));
        }

        [Test]
        public void VanillaCoverage_TextureInANonAtlasableSlotIsNotCroppable()
        {
            var (service, state, fixture) = BuildSingleCoverageState(UvQuad(0, 0, 0.1f, 0.1f));
            fixture.Vanilla[CoverageMaterial] = System.Text.Encoding.UTF8.GetBytes(
                DownscaleMaterialXml(
                    ("t_xml_base_colour", CoverageTexture),
                    ("t_xml_blood_map", CoverageTexture)));

            var texture = CoverageTextureOf(RunCoverage(service, state, fixture), CoverageTexture);

            Assert.That(Get<string>(texture, "NotCroppableReason"), Does.Contain("not atlasable"));
            Assert.That(Get<double>(texture, "CoverageFraction"), Is.GreaterThan(0));
        }

        [Test]
        public void VanillaCoverage_ExhaustedBudgetCountsTexturesAsNotReached()
        {
            var (service, state, fixture) = BuildMixedCoverageState();

            var result = RunCoverage(service, state, fixture, budget: TimeSpan.Zero);

            Assert.That(Get<bool>(result, "BudgetExhausted"), Is.True);
            Assert.That(Get<int>(result, "RigidsSkippedForTime"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "NotReached"), Is.EqualTo(2));
            Assert.That(CoverageAssessed(result), Is.Empty);
            var report = BuildCoverageSection(state);
            Assert.That(report, Does.Contain("The time budget ran out: 1 rigid(s)"));
            Assert.That(report, Does.Contain("Vanilla, not reached (time budget): 2"));
        }

        [Test]
        public void VanillaCoverage_UnreadableRigidIsIsolatedAndMakesItsTexturesNotCroppable()
        {
            var (service, state, fixture) = BuildSingleCoverageState(UvQuad(0, 0, 0.1f, 0.1f));

            var result = RunCoverage(
                service,
                state,
                fixture,
                loader: _ => throw new InvalidOperationException("boom"));

            Assert.That(Get<string?>(result, "Failure"), Is.Null);
            Assert.That(Get<int>(result, "RigidsUnavailable"), Is.EqualTo(1));
            var texture = CoverageTextureOf(result, CoverageTexture);
            Assert.That(Get<string>(texture, "NotCroppableReason"), Does.Contain("cannot be assessed"));
        }

        [Test]
        public void VanillaCoverage_Report_ListsCroppableTexturesAndSplitsTheCounts()
        {
            var (service, state, fixture) = BuildMixedCoverageState();
            RunCoverage(service, state, fixture);

            var report = BuildCoverageSection(state);

            Assert.That(report, Does.StartWith("Vanilla texture coverage"));
            Assert.That(report, Does.Contain("Mode: REPORT ONLY. No pack file was changed."));
            Assert.That(report, Does.Contain("Textures referenced through the pack's WSModel materials: 5"));
            Assert.That(report, Does.Contain("Owned by the source pack (not assessed here): 1"));
            Assert.That(report, Does.Contain("Not found in any loaded container: 1"));
            Assert.That(report, Does.Contain("Vanilla, header unreadable or layout unsupported: 1"));
            Assert.That(report, Does.Contain("Vanilla, assessed: 2, which split exactly into:"));
            Assert.That(report, Does.Contain("Croppable: 1"));
            Assert.That(report, Does.Contain("Not croppable: 1"));
            Assert.That(report, Does.Contain("1 x a part tiles it"));
            Assert.That(report, Does.Contain("e.g. textures\\v_tiled.dds"));
            Assert.That(report, Does.Contain("below 10 %: 1 texture(s)"));
            Assert.That(report, Does.Contain("All croppable textures by bytes saved:"));
            Assert.That(report, Does.Match(@"textures\\v_small\.dds: 1024x1024 BC1_SRGB"));
            Assert.That(report, Does.Match(@"coverage 6[.,][23]\s?%"));
            Assert.That(report, Does.Contain("crop 512x512"));
            Assert.That(report, Does.Contain("users: 1 material(s), 1 part(s) (1 on mod-owned rigids, 0 on vanilla rigids"));
            Assert.That(report, Does.Not.Contain("NaN"));
        }

        [Test]
        public void VanillaCoverage_Report_StaysLoadedIsNotComputedAndHasNoWeightedTotal()
        {
            var (service, state, fixture) = BuildMixedCoverageState();
            RunCoverage(service, state, fixture);

            var report = BuildCoverageSection(state);

            Assert.That(report, Does.Contain("Stays loaded anyway"));
            Assert.That(report, Does.Contain("not computed (the existing residency model cannot be queried here"));
            Assert.That(report, Does.Contain("No weighted saving is given."));
            Assert.That(report, Does.Contain("This is an upper bound"));
        }

        [Test]
        public void VanillaCoverage_Report_ListTruncatesAfterFortyEntries()
        {
            var fixture = new DownscaleFixture();
            var parts = new List<(string, float[][])>();
            for (var index = 0; index < 43; index++)
            {
                var texture = $"textures\\v_{index:D2}.dds";
                var material = $"materials\\v_{index:D2}.xml.material";
                AddVanillaCoverageTexture(fixture, texture, material);
                parts.Add((material, UvQuad(0, 0, 0.1f, 0.1f)));
            }

            AddCoverageModel(fixture, CoverageRigid, CoverageWsModel, true, parts.ToArray());
            var (service, state) = CreateDownscaleState(fixture, downscale: false);
            RunCoverage(service, state, fixture);

            var report = BuildCoverageSection(state);

            Assert.That(report, Does.Contain("Top 40 croppable textures by bytes saved:"));
            Assert.That(report, Does.Contain("... and 3 more"));
            Assert.That(report, Does.Contain("Croppable: 43"));
        }

        [Test]
        public void VanillaCoverage_Report_StatesFailureAndNotRun()
        {
            var (_, state, _) = BuildSingleCoverageState(UvQuad(0, 0, 0.1f, 0.1f));
            Assert.That(BuildCoverageSection(state), Does.Contain("analysis did not run."));

            var failed = Activator.CreateInstance(GetNestedType("VanillaTextureCoverageResult"), true)!;
            failed.GetType().GetProperty("Failure")!.SetValue(failed, "InvalidOperationException: boom");
            SetStateProperty(state, "VanillaTextureCoverage", failed);

            var report = BuildCoverageSection(state);

            Assert.That(report, Does.Contain("analysis failed and was skipped: InvalidOperationException: boom"));
            Assert.That(report, Does.Contain("No pack file was changed."));
        }

        private static (object Service, object State, DownscaleFixture Fixture) BuildBucketCoverageState()
        {
            var fixture = new DownscaleFixture();
            AddVanillaCoverageTexture(fixture, "textures\\v_a.dds", "materials\\v_a.xml.material");
            AddVanillaCoverageTexture(fixture, "textures\\v_b.dds", "materials\\v_b.xml.material");
            AddVanillaCoverageTexture(fixture, "textures\\v_c.dds", "materials\\v_c.xml.material");
            AddVanillaCoverageTexture(fixture, "textures\\v_d.dds", "materials\\v_d.xml.material");
            // Mod rigid: A, B (below 10 %) and C (16 %) share it, so it must count once.
            AddCoverageModel(
                fixture, CoverageRigid, CoverageWsModel, modOwnedRigid: true,
                ("materials\\v_a.xml.material", UvQuad(0, 0, 0.2, 0.2)),
                ("materials\\v_b.xml.material", UvQuad(0, 0, 0.2, 0.2)),
                ("materials\\v_c.xml.material", UvQuad(0, 0, 0.4, 0.4)),
                ("materials\\v_d.xml.material", UvQuad(0, 0, 0.8, 0.8)));
            AddCoverageModel(
                fixture, CoverageVanillaRigid, CoverageVanillaWsModel, modOwnedRigid: false,
                ("materials\\v_a.xml.material", UvQuad(0.5, 0.5, 0.6, 0.6)),
                ("materials\\v_b.xml.material", UvQuad(0.5, 0.5, 0.6, 0.6)));
            var (service, state) = CreateDownscaleState(fixture, downscale: false);
            return (service, state, fixture);
        }

        private static List<object> CoverageBuckets(object result)
        {
            var type = GetNestedType("VanillaCoverageTexture");
            var croppable = Get<IEnumerable>(result, "Croppable").Cast<object>().ToList();
            var array = Array.CreateInstance(type, croppable.Count);
            for (var index = 0; index < croppable.Count; index++)
                array.SetValue(croppable[index], index);
            return ((IEnumerable)GetStaticMethod("BuildVanillaCoverageBuckets").Invoke(null, [array])!)
                .Cast<object>().ToList();
        }

        private static List<object> BucketTextures(object bucket)
            => Get<IEnumerable>(bucket, "Textures").Cast<object>().ToList();

        [Test]
        public void VanillaCoverage_Buckets_SumToTheCroppableTotals()
        {
            var (service, state, fixture) = BuildBucketCoverageState();
            var result = RunCoverage(service, state, fixture);
            var buckets = CoverageBuckets(result);
            var croppable = Get<IEnumerable>(result, "Croppable").Cast<object>().ToList();

            Assert.That(buckets.Select(bucket => BucketTextures(bucket).Count), Is.EqualTo(new[] { 2, 1, 0, 1 }));
            Assert.That(buckets.Sum(bucket => BucketTextures(bucket).Count), Is.EqualTo(croppable.Count));
            Assert.That(buckets.Sum(bucket => Get<long>(bucket, "OriginalBytes")),
                Is.EqualTo(croppable.Sum(texture => Get<long>(texture, "OriginalBytes"))));
            Assert.That(buckets.Sum(bucket => Get<long>(bucket, "CropBytes")),
                Is.EqualTo(croppable.Sum(texture => Get<long>(texture, "CropBytes"))));
            Assert.That(buckets.Sum(bucket => Get<long>(bucket, "BytesSaved")),
                Is.EqualTo(croppable.Sum(texture => Get<long>(texture, "BytesSaved"))));

            var report = BuildCoverageSection(state);
            Assert.That(report, Does.Contain("The four coverage buckets below sum to these totals."));
            Assert.That(report, Does.Contain("below 10 %: 2 texture(s), vanilla"));
            Assert.That(report, Does.Contain("10 to 25 %: 1 texture(s), vanilla"));
            Assert.That(report, Does.Contain("25 to 50 %: 0 texture(s), vanilla"));
            Assert.That(report, Does.Contain("50 % and above: 1 texture(s), vanilla"));
            Assert.That(report, Does.Contain("replacements added to the pack"));
            Assert.That(
                report,
                Does.Contain(
                    $"Sum of the buckets: {croppable.Count:N0} texture(s), vanilla " +
                    $"{croppable.Sum(texture => Get<long>(texture, "OriginalBytes")):N0} bytes"));
        }

        [Test]
        public void VanillaCoverage_Buckets_BoundaryTexturesLandInTheHigherBucket()
        {
            var type = GetNestedType("VanillaCoverageTexture");
            var values = new[] { 0.0999, 0.10, 0.2499, 0.25, 0.4999, 0.50, 1.0 };
            var array = Array.CreateInstance(type, values.Length);
            for (var index = 0; index < values.Length; index++)
            {
                var texture = Activator.CreateInstance(type, [$"t{index}"])!;
                type.GetProperty("CoverageFraction")!.SetValue(texture, values[index]);
                array.SetValue(texture, index);
            }

            var buckets = ((IEnumerable)GetStaticMethod("BuildVanillaCoverageBuckets").Invoke(null, [array])!)
                .Cast<object>().ToList();

            Assert.That(
                buckets.Select(bucket => BucketTextures(bucket).Select(texture => Get<string>(texture, "Path")).ToArray()),
                Is.EqualTo(new[]
                {
                    new[] { "t0" },
                    new[] { "t1", "t2" },
                    new[] { "t3", "t4" },
                    new[] { "t5", "t6" },
                }));
        }

        [Test]
        public void VanillaCoverage_CumulativeVariants_EqualTheBucketSumsAndCountRigidsOnce()
        {
            var (service, state, fixture) = BuildBucketCoverageState();
            var result = RunCoverage(service, state, fixture);
            var buckets = CoverageBuckets(result);
            var report = BuildCoverageSection(state);

            string Line(int bucketCount, int limit, int modRigids, int vanillaRigids)
            {
                var included = buckets.Take(bucketCount).SelectMany(BucketTextures).ToList();
                return $"Coverage below {limit} %: {included.Count:N0} texture(s), saves " +
                       $"{included.Sum(texture => Get<long>(texture, "BytesSaved")):N0} bytes (upper bound), adds " +
                       $"{included.Sum(texture => Get<long>(texture, "CropBytes")):N0} bytes to the pack, " +
                       $"{modRigids:N0} distinct mod-owned rigid(s) to rewrite, " +
                       $"{vanillaRigids:N0} distinct vanilla rigid(s) needing generated copies";
            }

            // Two textures share both rigids, the third only the mod rigid: one of each.
            Assert.That(report, Does.Contain(Line(1, 10, 1, 1)));
            Assert.That(report, Does.Contain(Line(2, 25, 1, 1)));
        }

        // --- Per-unit texture repack estimate ---------------------------------------------

        private const string RepackRootA = "variantmeshes\\wh_variantmodels\\hu1\\emp\\a\\a.variantmeshdefinition";
        private const string RepackRootB = "variantmeshes\\wh_variantmodels\\hu1\\emp\\b\\b.variantmeshdefinition";
        private const string RepackRootC = "variantmeshes\\wh_variantmodels\\hu1\\emp\\c\\c.variantmeshdefinition";
        private const string RepackShared = "variantmeshes\\wh_main\\shared.rigid_model_v2";
        private const string RepackPrivateA = "variantmeshes\\wh_main\\pa.rigid_model_v2";
        private const string RepackPrivateB = "variantmeshes\\wh_main\\pb.rigid_model_v2";
        private const string RepackIsolated = "variantmeshes\\wh_main\\iso.rigid_model_v2";
        private const string RepackUnrooted = "variantmeshes\\wh_main\\unrooted.rigid_model_v2";
        private const string RepackVanillaTexture = "textures\\rv.dds";
        private const string RepackOwnedTexture = "textures\\ro.dds";
        private const string RepackTiledTexture = "textures\\rn.dds";
        private const long RepackChain = 699_064;

        // Units A and B share one rigid and each has a private one; C is isolated. Texture
        // V is vanilla and O is owned (same material, different slots); N is tiled.
        private static (object Service, object State, DownscaleFixture Fixture) BuildRepackState(
            bool withUnrootedWsModel = false)
        {
            var fixture = new DownscaleFixture();
            fixture.Vanilla[RepackVanillaTexture] = BuildBcDds(1024, 1024, 11);
            fixture.Vanilla[RepackTiledTexture] = BuildBcDds(1024, 1024, 11);
            fixture.AddTexture(RepackOwnedTexture, 1024, 11);
            fixture.AddMaterial(
                "materials\\r_vo.xml.material",
                ("t_xml_base_colour", RepackVanillaTexture),
                ("t_xml_normal", RepackOwnedTexture));
            fixture.AddMaterial("materials\\r_n.xml.material", ("t_xml_base_colour", RepackTiledTexture));
            const string vo = "materials\\r_vo.xml.material";
            const string n = "materials\\r_n.xml.material";
            AddCoverageModel(fixture, RepackShared, "variantmeshes\\wh_main\\ws_shared.wsmodel", true,
                (vo, UvQuad(0, 0, 0.25, 0.25)));
            AddCoverageModel(fixture, RepackPrivateA, "variantmeshes\\wh_main\\ws_pa.wsmodel", true,
                (vo, UvQuad(0.5, 0.5, 0.75, 0.75)),
                (n, UvQuad(0, 0, 1.5, 0.5)));
            AddCoverageModel(fixture, RepackPrivateB, "variantmeshes\\wh_main\\ws_pb.wsmodel", true,
                (vo, UvQuad(0, 0.5, 0.5, 1)));
            AddCoverageModel(fixture, RepackIsolated, "variantmeshes\\wh_main\\ws_iso.wsmodel", true,
                (vo, UvQuad(0, 0, 0.5, 0.5)));
            var links = new List<(string, string)>
            {
                (RepackRootA, "variantmeshes\\wh_main\\ws_shared.wsmodel"),
                (RepackRootB, "variantmeshes\\wh_main\\ws_shared.wsmodel"),
                (RepackRootA, "variantmeshes\\wh_main\\ws_pa.wsmodel"),
                (RepackRootB, "variantmeshes\\wh_main\\ws_pb.wsmodel"),
                (RepackRootC, "variantmeshes\\wh_main\\ws_iso.wsmodel"),
            };
            if (withUnrootedWsModel)
            {
                AddCoverageModel(fixture, RepackUnrooted, "variantmeshes\\wh_main\\ws_unrooted.wsmodel", true,
                    (vo, UvQuad(0, 0, 0.1, 0.1)));
            }

            var (service, state) = CreateDownscaleState(fixture, downscale: false);
            InstallDownscaleGameplay(
                state, CreateDownscaleResolution(true), CreateDownscaleIndex(links.ToArray()));
            return (service, state, fixture);
        }

        private static object RunRepack(
            object service,
            object state,
            DownscaleFixture fixture,
            TimeSpan? budget = null)
        {
            RunCoverage(service, state, fixture);
            GetServiceType()
                .GetMethod("RunUnitTextureRepack", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [state, CancellationToken.None, null, budget]);
            return Get<object>(state, "UnitTextureRepack");
        }

        private static List<object> RepackUnits(object result)
            => Get<IEnumerable>(result, "Units").Cast<object>().ToList();

        private static object RepackUnit(object result, string root)
            => RepackUnits(result).Single(unit => Get<string>(unit, "Root") == root);

        private static long Bcn(int width, int height)
            => (long)GetStaticMethod("CalculateBcnMipChainBytes").Invoke(null, [width, height, 8])!;

        private static string BuildRepackSection(object state)
        {
            var builder = new System.Text.StringBuilder();
            GetStaticMethod("AppendUnitTextureRepackSection").Invoke(null, [builder, state]);
            return builder.ToString();
        }

        [Test]
        public void UnitRepack_OptionA_PerUnitTodayAfterAndRounding()
        {
            var (service, state, fixture) = BuildRepackState();
            var result = RunRepack(service, state, fixture);

            Assert.That(Get<string?>(result, "Failure"), Is.Null);
            Assert.That(Get<string?>(result, "UnavailableReason"), Is.Null);
            var unitA = RepackUnit(result, RepackRootA);
            // Two quarter-of-a-quarter regions (V and O share the parts): 12.5 % -> 512x512.
            Assert.That(Get<long>(unitA, "Today"), Is.EqualTo(3 * RepackChain));
            Assert.That(Get<long>(unitA, "KeptBytes"), Is.EqualTo(RepackChain));
            Assert.That(Get<long>(unitA, "AtlasRaw"), Is.EqualTo(2 * (long)Math.Ceiling(0.125 * RepackChain * 1.25)));
            Assert.That(Get<long>(unitA, "AtlasRounded"), Is.EqualTo(2 * Bcn(512, 512)));
            Assert.That(Get<long>(unitA, "AfterA"), Is.EqualTo(2 * Bcn(512, 512) + RepackChain));
            Assert.That(Get<long>(unitA, "SavingA"), Is.EqualTo(3 * RepackChain - 2 * Bcn(512, 512) - RepackChain));
            // Unit C covers 25 %: 31 % with slack -> a 2^19 pixel area, 1024x512.
            var unitC = RepackUnit(result, RepackRootC);
            Assert.That(Get<long>(unitC, "AtlasRounded"), Is.EqualTo(2 * Bcn(1024, 512)));
            Assert.That(Get<long>(unitC, "KeptBytes"), Is.Zero);
            Assert.That(Get<double>(unitC, "Fill"), Is.EqualTo(0.25).Within(1e-9));

            foreach (var unit in RepackUnits(result))
            {
                Assert.That(Get<long>(unit, "AtlasRounded"), Is.GreaterThanOrEqualTo(Get<long>(unit, "AtlasRaw")));
                Assert.That(Get<long>(unit, "AfterA"), Is.LessThanOrEqualTo(Get<long>(unit, "Today")));
            }
        }

        [Test]
        public void UnitRepack_CountsReconcile_AndUnrootedWsModelsAreExcluded()
        {
            var (service, state, fixture) = BuildRepackState(withUnrootedWsModel: true);
            var result = RunRepack(service, state, fixture);

            Assert.That(Get<int>(result, "UnrootedWsModels"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "UnrootedParts"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "RootsReached"), Is.EqualTo(3));
            Assert.That(
                RepackUnits(result).Count + Get<int>(result, "RootsWithoutAssessableParts") +
                Get<int>(result, "UnitsNotReached"),
                Is.EqualTo(Get<int>(result, "RootsReached")));
            Assert.That(
                Get<int>(result, "TexturesRepackable") + Get<int>(result, "TexturesKept"),
                Is.EqualTo(Get<int>(result, "TexturesUsed")));
            Assert.That(Get<int>(result, "TexturesRepackable"), Is.EqualTo(2));
            Assert.That(Get<int>(result, "TexturesKept"), Is.EqualTo(1));
            var report = BuildRepackSection(state);
            Assert.That(report, Does.Contain("Source WSModels reached by no root (excluded): 1, binding 1 distinct part(s)"));
            Assert.That(report, Does.Contain("They split exactly into: units estimated 3, without an assessed part 0, not reached (time budget) 0"));
        }

        [Test]
        public void UnitRepack_RigidCopiesAndSharingStatistics()
        {
            var (service, state, fixture) = BuildRepackState();
            var result = RunRepack(service, state, fixture);

            var typed = Activator.CreateInstance(
                typeof(List<>).MakeGenericType(GetNestedType("UnitRepackUnit")))!;
            foreach (var unit in RepackUnits(result))
                ((IList)typed).Add(unit);
            var summary = GetStaticMethod("SummarizeUnitRepack").Invoke(null, [result, typed])!;

            Assert.That(Get<int>(summary, "CopiesMod"), Is.EqualTo(1));
            Assert.That(Get<long>(summary, "CopyBytesMod"), Is.EqualTo(1));
            Assert.That(Get<int>(summary, "CopiesVanilla"), Is.Zero);
            var rigids = Get<IDictionary>(result, "Rigids");
            Assert.That(rigids.Count, Is.EqualTo(4));
            Assert.That(Get<IEnumerable>(rigids[RepackShared]!, "Users").Cast<object>().Count(), Is.EqualTo(2));
            Assert.That(Get<IEnumerable>(rigids[RepackPrivateA]!, "Users").Cast<object>().Count(), Is.EqualTo(1));
            Assert.That(Get<int>(RepackUnit(result, RepackRootA), "SharedRigids"), Is.EqualTo(1));
            Assert.That(Get<int>(RepackUnit(result, RepackRootC), "SharedRigids"), Is.Zero);
            Assert.That(
                Get<IEnumerable>(result, "UnitsPerTexture").Cast<int>().OrderBy(count => count),
                Is.EqualTo(new[] { 1, 3, 3 }));
        }

        [Test]
        public void UnitRepack_RemovableOwnedBytesOnlyWhenEveryUserIsRepackable()
        {
            var (service, state, fixture) = BuildRepackState();
            var result = RunRepack(service, state, fixture);
            Assert.That(Get<long>(result, "RemovableOwnedBytes"), Is.EqualTo(RepackChain));
            Assert.That(Get<int>(result, "RemovableOwnedTextures"), Is.EqualTo(1));

            // A WSModel no root reaches still uses the owned texture: not provably removable.
            var (service2, state2, fixture2) = BuildRepackState(withUnrootedWsModel: true);
            Assert.That(Get<long>(RunRepack(service2, state2, fixture2), "RemovableOwnedBytes"), Is.Zero);

            // A non-material mention makes the texture not repackable at all.
            var (service3, state3, fixture3) = BuildRepackState();
            fixture3.AddText(
                "variantmeshes\\wh_main\\x.variantmeshdefinition",
                $"<VARIANT_MESH><decal_diffuse>{RepackOwnedTexture}</decal_diffuse></VARIANT_MESH>");
            var result3 = RunRepack(service3, state3, fixture3);
            Assert.That(Get<long>(result3, "RemovableOwnedBytes"), Is.Zero);
            Assert.That(Get<int>(result3, "TexturesRepackable"), Is.EqualTo(1));
        }

        [Test]
        public void UnitRepack_OptionB_GroupsUnitsThatShareARigid_AndIncludesTheGroupMatesRegions()
        {
            var (service, state, fixture) = BuildRepackState();
            var result = RunRepack(service, state, fixture);
            var groups = Get<IEnumerable>(result, "Groups").Cast<object>().ToList();

            Assert.That(groups, Has.Count.EqualTo(2));
            Assert.That(
                groups.Select(group => Get<IEnumerable>(group, "Units").Cast<object>().Count()).OrderBy(count => count),
                Is.EqualTo(new[] { 1, 2 }));
            var unitA = RepackUnit(result, RepackRootA);
            var unitB = RepackUnit(result, RepackRootB);
            Assert.That(Get<int>(unitA, "Group"), Is.EqualTo(Get<int>(unitB, "Group")));
            Assert.That(Get<int>(unitA, "Group"), Is.Not.EqualTo(Get<int>(RepackUnit(result, RepackRootC), "Group")));

            // Group A+B touches 4096 + 4096 + 16384 cells = 37.5 %: 47 % with slack -> 1024x512,
            // larger than A's own 12.5 % atlas, because B's region is in the group atlas.
            var group = groups.Single(item => Get<IEnumerable>(item, "Units").Cast<object>().Count() == 2);
            Assert.That(Get<long>(group, "AtlasRounded"), Is.EqualTo(2 * Bcn(1024, 512)));
            Assert.That(
                Get<long>(group, "AtlasRounded") + Get<long>(unitA, "KeptBytes"),
                Is.GreaterThan(Get<long>(unitA, "AfterA")));

            var report = BuildRepackSection(state);
            Assert.That(report, Does.Contain("2 group(s); units per group: 1=1, 2-5=1, 6-20=0, 21+=0"));
            Assert.That(report, Does.Contain("largest: 2 unit(s)"));
            Assert.That(report, Does.Contain("rigid copies needed: 0"));
        }

        [Test]
        public void UnitRepack_Pairs_ReportSharedBytesAndTheTwoAtlasAfter()
        {
            var (service, state, fixture) = BuildRepackState();
            var result = RunRepack(service, state, fixture);

            var pair = Get<IEnumerable>(result, "TopPairs").Cast<object>().First();

            // V and O are shared; N is only used by unit A.
            Assert.That(Get<long>(pair, "SharedBytes"), Is.EqualTo(2 * RepackChain));
            Assert.That(Get<long>(pair, "Today"), Is.EqualTo(3 * RepackChain));
            var first = Get<object>(pair, "First");
            var second = Get<object>(pair, "Second");
            Assert.That(
                Get<long>(pair, "After"),
                Is.EqualTo(Get<long>(first, "AtlasRounded") + Get<long>(second, "AtlasRounded") + RepackChain));
        }

        [Test]
        public void UnitRepack_Report_ShowsSummariesCharactersAndTheListing()
        {
            var (service, state, fixture) = BuildRepackState();
            RunRepack(service, state, fixture);

            var report = BuildRepackSection(state);

            Assert.That(report, Does.StartWith("Per-unit texture repack estimate"));
            Assert.That(report, Does.Contain("Mode: REPORT ONLY. No pack file was changed."));
            Assert.That(report, Does.Contain("All units: 3 unit(s)"));
            Assert.That(report, Does.Contain("Rank-and-file units only: 3 unit(s)"));
            Assert.That(report, Does.Contain("Option A (one atlas set per unit"));
            Assert.That(report, Does.Contain("Option B (one atlas set per group"));
            Assert.That(report, Does.Contain("Unit classes: rank-and-file 3, character"));
            Assert.That(report, Does.Contain("Whole pack if every unit were loaded at once (Option A's worst case)"));
            Assert.That(report, Does.Contain("All units by Option A saving:"));
            Assert.That(report, Does.Contain($"{RepackRootA} [rank-and-file]: today {3 * RepackChain:N0}"));
            Assert.That(report, Does.Contain("Most shared rigids (top 10):"));
            Assert.That(report, Does.Not.Contain("NaN"));
            Assert.That(Get<IDictionary>(state, "PhaseDurations").Contains("Per-unit texture repack estimate"), Is.True);
        }

        [Test]
        public void UnitRepack_Report_MissingIndexSaysWhyItCouldNotRun()
        {
            var (service, state, fixture) = BuildRepackState();
            SetStateProperty(state, "GameplayMeshDependencyIndex", null);

            RunRepack(service, state, fixture);
            var report = BuildRepackSection(state);

            Assert.That(report, Does.Contain("could not run: the gameplay mesh dependency index was not built."));
            Assert.That(report, Does.Contain("No pack file was changed."));
        }

        [Test]
        public void UnitRepack_ExhaustedBudgetCountsUnitsNotReached()
        {
            var (service, state, fixture) = BuildRepackState();

            var result = RunRepack(service, state, fixture, TimeSpan.Zero);

            Assert.That(Get<bool>(result, "BudgetExhausted"), Is.True);
            Assert.That(Get<int>(result, "UnitsNotReached"), Is.EqualTo(3));
            Assert.That(RepackUnits(result), Is.Empty);
            Assert.That(BuildRepackSection(state), Does.Contain("The time budget ran out: 3 unit(s) were not reached"));
        }

        // --- Per-unit repack: Options C and C2 ---------------------------------------------

        private const string SigRigidShared = "variantmeshes\\wh_main\\sig_s3.rigid_model_v2";
        private const string SigRigidVanilla = "variantmeshes\\wh_main\\sig_vr.rigid_model_v2";
        private const string SigRigidPrivate = "variantmeshes\\wh_main\\sig_pa.rigid_model_v2";
        private const string SigTextureX = "textures\\sig_x.dds";
        private const string SigTextureY = "textures\\sig_y.dds";
        private const string SigTextureZ = "textures\\sig_z.dds";

        // X is used by units {A,B,C}, Y by {A,B}, Z by {A}. S3 is a mod-owned rigid shared by
        // three units, VR a vanilla rigid shared by two, PA a private one. Unit A also uses
        // a second region of X that B and C do not touch.
        private static (object Service, object State, DownscaleFixture Fixture) BuildSignatureState(
            bool withFullyUsedTexture = false)
        {
            var fixture = new DownscaleFixture();
            foreach (var (texture, material) in new[]
                     {
                         (SigTextureX, "materials\\sig_x.xml.material"),
                         (SigTextureY, "materials\\sig_y.xml.material"),
                         (SigTextureZ, "materials\\sig_z.xml.material"),
                     })
            {
                fixture.Vanilla[texture] = BuildBcDds(1024, 1024, 11);
                fixture.AddMaterial(material, ("t_xml_base_colour", texture));
            }

            AddCoverageModel(fixture, SigRigidShared, "variantmeshes\\wh_main\\ws_sig_s3.wsmodel", true,
                ("materials\\sig_x.xml.material", UvQuad(0, 0, 0.25, 0.25)));
            AddCoverageModel(fixture, SigRigidVanilla, "variantmeshes\\wh_main\\ws_sig_vr.wsmodel", false,
                ("materials\\sig_y.xml.material", UvQuad(0, 0, 0.5, 0.5)));
            AddCoverageModel(fixture, SigRigidPrivate, "variantmeshes\\wh_main\\ws_sig_pa.wsmodel", true,
                ("materials\\sig_z.xml.material", UvQuad(0.5, 0.5, 0.75, 0.75)),
                ("materials\\sig_x.xml.material", UvQuad(0.5, 0, 0.75, 0.25)));
            var links = new List<(string, string)>();
            if (withFullyUsedTexture)
            {
                // Unit C alone uses all of W: its atlas would not be smaller, so it is never built.
                fixture.Vanilla["textures\\sig_w.dds"] = BuildBcDds(1024, 1024, 11);
                fixture.AddMaterial("materials\\sig_w.xml.material", ("t_xml_base_colour", "textures\\sig_w.dds"));
                AddCoverageModel(fixture, "variantmeshes\\wh_main\\sig_full.rigid_model_v2",
                    "variantmeshes\\wh_main\\ws_sig_full.wsmodel", true,
                    ("materials\\sig_w.xml.material", UvQuad(0, 0, 1, 1)));
                links.Add((RepackRootC, "variantmeshes\\wh_main\\ws_sig_full.wsmodel"));
            }

            var (service, state) = CreateDownscaleState(fixture, downscale: false);
            InstallDownscaleGameplay(
                state,
                CreateDownscaleResolution(true),
                CreateDownscaleIndex(
                    links.ToArray().Concat(new[] {
                    (RepackRootA, "variantmeshes\\wh_main\\ws_sig_s3.wsmodel"),
                    (RepackRootB, "variantmeshes\\wh_main\\ws_sig_s3.wsmodel"),
                    (RepackRootC, "variantmeshes\\wh_main\\ws_sig_s3.wsmodel"),
                    (RepackRootA, "variantmeshes\\wh_main\\ws_sig_vr.wsmodel"),
                    (RepackRootB, "variantmeshes\\wh_main\\ws_sig_vr.wsmodel"),
                    (RepackRootA, "variantmeshes\\wh_main\\ws_sig_pa.wsmodel") }).ToArray()));
            return (service, state, fixture);
        }

        private static List<object> SetsOf(object result, string name)
            => Get<IEnumerable>(result, name).Cast<object>().ToList();

        private static int[] SetUnits(object set) => Get<int[]>(set, "Units");

        private static object SetWithUnits(object result, int unitCount)
            => SetsOf(result, "SetsC").Single(set => SetUnits(set).Length == unitCount);

        private static object TypedUnits(object result)
        {
            var typed = Activator.CreateInstance(
                typeof(List<>).MakeGenericType(GetNestedType("UnitRepackUnit")))!;
            foreach (var unit in RepackUnits(result))
                ((IList)typed).Add(unit);
            return typed;
        }

        [Test]
        public void UnitRepack_OptionC_TexturesWithTheSameUnitSetShareAnAtlasAndNothingIsDuplicated()
        {
            var (service, state, fixture) = BuildSignatureState();
            var result = RunRepack(service, state, fixture);

            var sets = SetsOf(result, "SetsC");
            Assert.That(sets.Select(set => SetUnits(set).Length).OrderBy(count => count), Is.EqualTo(new[] { 1, 2, 3 }));
            // Each repackable texture sits in exactly one set, in both variants.
            foreach (var name in new[] { "SetsC", "SetsC2" })
            {
                var entries = SetsOf(result, name)
                    .SelectMany(set => Get<IEnumerable>(set, "Items").Cast<object>()
                        .Select(item => item.GetType().GetField("Item1")!.GetValue(item)!))
                    .ToList();
                Assert.That(entries, Has.Count.EqualTo(Get<int>(result, "TexturesRepackable")));
                Assert.That(entries.Distinct().Count(), Is.EqualTo(entries.Count));
            }

            // Unit B uses X and Y but not Z, so it loads two atlas sets and not the {A} one.
            var unitA = RepackUnit(result, RepackRootA);
            var unitB = RepackUnit(result, RepackRootB);
            var unitC = RepackUnit(result, RepackRootC);
            Assert.That(Get<List<int>[]>(unitB, "SetIndexes")[0], Has.Count.EqualTo(2));
            Assert.That(Get<List<int>[]>(unitA, "SetIndexes")[0], Has.Count.EqualTo(3));
            Assert.That(Get<List<int>[]>(unitC, "SetIndexes")[0], Has.Count.EqualTo(1));
            Assert.That(
                Get<long[]>(unitB, "SetsRounded")[0],
                Is.EqualTo(Get<long>(SetWithUnits(result, 3), "Rounded") + Get<long>(SetWithUnits(result, 2), "Rounded")));
            Assert.That(SetUnits(SetWithUnits(result, 1)), Is.EqualTo(new[] { Get<int>(unitA, "Index") }));
        }

        [Test]
        public void UnitRepack_OptionC_RawFiguresNeverExceedToday()
        {
            var (service, state, fixture) = BuildSignatureState();
            var result = RunRepack(service, state, fixture);

            foreach (var unit in RepackUnits(result))
            {
                Assert.That(
                    Get<long[]>(unit, "SetsRaw")[0] + Get<long>(unit, "KeptBytes"),
                    Is.LessThanOrEqualTo(Get<long>(unit, "Today")));
                Assert.That(
                    Get<long[]>(unit, "SetsRounded")[0],
                    Is.GreaterThanOrEqualTo(Get<long[]>(unit, "SetsRaw")[0]));
            }

            var wholeAfterRaw = SetsOf(result, "SetsC").Sum(set => Get<long>(set, "Raw")) +
                                Get<long>(result, "WholePackKept");
            Assert.That(wholeAfterRaw, Is.LessThanOrEqualTo(Get<long>(result, "WholePackToday")));
            Assert.That(Get<long>(result, "WholePackToday"), Is.EqualTo(3 * RepackChain));
        }

        [Test]
        public void UnitRepack_OptionC_PairAfterCountsASharedAtlasOnce()
        {
            var (service, state, fixture) = BuildSignatureState();
            var result = RunRepack(service, state, fixture);
            var unitA = RepackUnit(result, RepackRootA);
            var unitB = RepackUnit(result, RepackRootB);

            var pair = Get<IEnumerable>(result, "TopPairs").Cast<object>()
                .Single(item => Get<object>(item, "First") == unitA && Get<object>(item, "Second") == unitB);

            // Sets containing A or B: X {A,B,C}, Y {A,B}, Z {A}, each counted once.
            var expected = SetsOf(result, "SetsC").Sum(set => Get<long>(set, "Rounded"));
            Assert.That(Get<long>(pair, "AfterC"), Is.EqualTo(expected));
            Assert.That(Get<long>(pair, "SharedBytes"), Is.EqualTo(2 * RepackChain));
        }

        [Test]
        public void UnitRepack_OptionC_RigidsAreRewrittenOnceOrCopiedOnce()
        {
            var (service, state, fixture) = BuildSignatureState();
            var result = RunRepack(service, state, fixture);

            var summary = GetStaticMethod("SummarizeUnitRepackSets")
                .Invoke(null, [result, TypedUnits(result), Get<object>(result, "SetsC"), 0])!;

            // S3 (three units) and PA are mod-owned: two rewrites in place. VR is vanilla: one copy.
            Assert.That(Get<int>(summary, "RigidsRewritten"), Is.EqualTo(2));
            Assert.That(Get<int>(summary, "VanillaCopies"), Is.EqualTo(1));
            Assert.That(Get<int>(summary, "Sets"), Is.EqualTo(3));
            Assert.That(Get<int>(summary, "Atlases"), Is.EqualTo(3));
        }

        [Test]
        public void UnitRepack_OptionC_ResidualWasteIsWhatTheUnitsOwnPartsDoNotTouch()
        {
            var (service, state, fixture) = BuildSignatureState();
            var result = RunRepack(service, state, fixture);

            // X is used over two regions by A, over one of them by B and C.
            Assert.That(Get<double[]>(RepackUnit(result, RepackRootA), "Waste")[0], Is.EqualTo(0).Within(1e-9));
            Assert.That(Get<double[]>(RepackUnit(result, RepackRootC), "Waste")[0], Is.EqualTo(0.5).Within(1e-9));
            Assert.That(Get<double[]>(RepackUnit(result, RepackRootB), "Waste")[0], Is.EqualTo(1.0 / 6).Within(1e-9));
            var summary = GetStaticMethod("SummarizeUnitRepackSets")
                .Invoke(null, [result, TypedUnits(result), Get<object>(result, "SetsC"), 0])!;
            Assert.That(Get<double>(summary, "WasteMean"), Is.EqualTo((0 + 0.5 + 1.0 / 6) / 3).Within(1e-9));
            Assert.That(Get<double>(summary, "WasteP90"), Is.EqualTo(0.5).Within(1e-9));
        }

        private static List<List<int>> MergeSignatures(int[][] signatures, int unitCount)
        {
            var merged = GetStaticMethod("MergeRepackSignatures").Invoke(null, [signatures, unitCount, null])!;
            return (List<List<int>>)merged.GetType().GetField("Item1")!.GetValue(merged)!;
        }

        [Test]
        public void UnitRepack_OptionC2_MergesAtJaccardFourFifthsButNotAtTwoFifths()
        {
            var groups = MergeSignatures(
                [[0, 1, 2, 3, 4], [0, 1, 2, 3], [0, 1]],
                5);

            Assert.That(groups, Has.Count.EqualTo(2));
            Assert.That(groups[0], Is.EqualTo(new[] { 0, 1 }));
            Assert.That(groups[1], Is.EqualTo(new[] { 2 }));
            Assert.That(groups.SelectMany(group => group).OrderBy(index => index), Is.EqualTo(new[] { 0, 1, 2 }));
        }

        [Test]
        public void UnitRepack_OptionC2_MergingChainsAndIsDeterministic()
        {
            // 9/10 beats 8/10; the merged set then absorbs the third, and the result is stable.
            int[][] signatures =
            [
                [0, 1, 2, 3, 4, 5, 6, 7, 8, 9],
                [0, 1, 2, 3, 4, 5, 6, 7, 8],
                [0, 1, 2, 3, 4, 5, 6, 7],
                [20, 21],
            ];

            var first = MergeSignatures(signatures, 22);
            var second = MergeSignatures(signatures, 22);

            Assert.That(first, Has.Count.EqualTo(2));
            Assert.That(first[0], Is.EqualTo(new[] { 0, 1, 2 }));
            Assert.That(second.Select(group => group.ToArray()), Is.EqualTo(first.Select(group => group.ToArray())));
        }

        [Test]
        public void UnitRepack_OptionC2_TooManySetsAreSkippedWithAReason()
        {
            var signatures = Enumerable.Range(0, 3001).Select(index => new[] { index % 7 }).ToArray();
            var merged = GetStaticMethod("MergeRepackSignatures").Invoke(null, [signatures, 7, null])!;

            Assert.That(merged.GetType().GetField("Item1")!.GetValue(merged), Is.Null);
            Assert.That((string)merged.GetType().GetField("Item2")!.GetValue(merged)!, Does.Contain("cap"));
        }

        [Test]
        public void UnitRepack_OptionC2_EndToEndTotalsReconcile()
        {
            var (service, state, fixture) = BuildSignatureState();
            var result = RunRepack(service, state, fixture);

            // {A,B,C} vs {A,B}: 2/3 < 0.8, so nothing merges in this fixture.
            Assert.That(Get<string?>(result, "C2SkippedReason"), Is.Null);
            Assert.That(SetsOf(result, "SetsC2"), Has.Count.EqualTo(3));
            Assert.That(
                SetsOf(result, "SetsC2").Sum(set => Get<long>(set, "Rounded")),
                Is.EqualTo(SetsOf(result, "SetsC").Sum(set => Get<long>(set, "Rounded"))));
        }

        [Test]
        public void UnitRepack_Report_ShowsOptionCAndC2AndPrintsNegativeSavingsPlainly()
        {
            var (service, state, fixture) = BuildSignatureState();
            RunRepack(service, state, fixture);

            var report = BuildRepackSection(state);

            Assert.That(report, Does.Contain("Option C (one atlas set per signature = the exact set of units that use a texture; each texture placed once):"));
            Assert.That(report, Does.Contain("Option C2 (signature sets merged greedily while Jaccard similarity >= 0.8):"));
            Assert.That(report, Does.Contain("Signature sets: 3, atlases: 3; signature size (units): 1=1, 2-5=2, 6-20=0, 21+=0"));
            Assert.That(report, Does.Contain("rounding overhead"));
            Assert.That(report, Does.Contain("Residual waste"));
            Assert.That(report, Does.Contain("signatures per grid cell instead of per texture would remove it (not implemented)"));
            Assert.That(report, Does.Contain("this is the honest headline"));
            Assert.That(report, Does.Contain("mod-owned rigids rewritten in place 2; vanilla rigids needing one generated copy 1"));
            Assert.That(report, Does.Contain(", C after "));
            Assert.That(report, Does.Not.Contain("saves - "));
            Assert.That(
                GetStaticMethod("FormatSignedBytes").Invoke(null, [-140_344_813_096L]),
                Is.EqualTo("-" + 140_344_813_096L.ToString("N0")));
        }

        private static List<object> CumulativeRows(object result, int[] limits)
        {
            var decisions = GetStaticMethod("BuildUnitRepackSetDecisions").Invoke(null, [result])!;
            return ((IEnumerable)GetStaticMethod("BuildUnitRepackCumulativeRows").Invoke(null, [decisions, limits])!)
                .Cast<object>().ToList();
        }

        [Test]
        public void UnitRepack_Selective_CumulativeRowsAreMonotoneAndAllEqualsThePositiveTotal()
        {
            var (service, state, fixture) = BuildSignatureState(withFullyUsedTexture: true);
            var result = RunRepack(service, state, fixture);
            var decisions = ((IEnumerable)GetStaticMethod("BuildUnitRepackSetDecisions").Invoke(null, [result])!)
                .Cast<object>().ToList();

            var rows = CumulativeRows(result, [1, 2]);

            Assert.That(rows.Select(row => Get<string>(row, "Label")), Is.EqualTo(new[] { "best 1", "best 2", "all" }));
            for (var index = 1; index < rows.Count; index++)
            {
                Assert.That(Get<long>(rows[index], "Saving"), Is.GreaterThanOrEqualTo(Get<long>(rows[index - 1], "Saving")));
                Assert.That(Get<long>(rows[index], "BytesAdded"), Is.GreaterThanOrEqualTo(Get<long>(rows[index - 1], "BytesAdded")));
                Assert.That(Get<int>(rows[index], "Textures"), Is.GreaterThanOrEqualTo(Get<int>(rows[index - 1], "Textures")));
                Assert.That(Get<int>(rows[index], "ModRigids"), Is.GreaterThanOrEqualTo(Get<int>(rows[index - 1], "ModRigids")));
            }

            var positiveTotal = decisions.Where(decision => Get<bool>(decision, "Built")).Sum(decision => Get<long>(decision, "Saving"));
            Assert.That(Get<long>(rows[^1], "Saving"), Is.EqualTo(positiveTotal));
            Assert.That(Get<double>(rows[^1], "Share"), Is.EqualTo(1.0).Within(1e-12));
        }

        [Test]
        public void UnitRepack_Selective_SetWhoseAtlasIsNotSmallerIsNeverBuilt()
        {
            var (service, state, fixture) = BuildSignatureState(withFullyUsedTexture: true);
            var result = RunRepack(service, state, fixture);
            var decisions = ((IEnumerable)GetStaticMethod("BuildUnitRepackSetDecisions").Invoke(null, [result])!)
                .Cast<object>().ToList();

            var never = decisions.Single(decision => Get<string>(decision, "ExampleTexture") == "textures\\sig_w.dds");

            Assert.That(decisions, Has.Count.EqualTo(4));
            Assert.That(Get<long>(never, "Saving"), Is.LessThanOrEqualTo(0));
            Assert.That(Get<bool>(never, "Built"), Is.False);
            // It is excluded from every cumulative row: only the three positive sets count.
            var rows = CumulativeRows(result, [1, 2]);
            Assert.That(Get<int>(rows[^1], "Sets"), Is.EqualTo(3));
            Assert.That(Get<int>(rows[^1], "Textures"), Is.EqualTo(3));
            var report = BuildRepackSection(state);
            Assert.That(report, Does.Contain("positive saving (would be built): 3; zero or negative after rounding (never built): 1"));
        }

        [Test]
        public void UnitRepack_Selective_RigidsAreDistinctAcrossSetsNotSummed()
        {
            var (service, state, fixture) = BuildSignatureState();
            var result = RunRepack(service, state, fixture);
            var decisions = ((IEnumerable)GetStaticMethod("BuildUnitRepackSetDecisions").Invoke(null, [result])!)
                .Cast<object>().ToList();

            var summed = decisions.Sum(decision => Get<HashSet<string>>(decision, "ModRigids").Count);
            var all = CumulativeRows(result, [1])[^1];

            // X touches S3 and PA, Z touches PA: three rigid mentions, two distinct rigids.
            Assert.That(summed, Is.EqualTo(3));
            Assert.That(Get<int>(all, "ModRigids"), Is.EqualTo(2));
            Assert.That(Get<int>(all, "VanillaRigids"), Is.EqualTo(1));
        }

        [Test]
        public void UnitRepack_Selective_ReportListsSetsCumulativeRowsAndEfficiency()
        {
            var (service, state, fixture) = BuildSignatureState();
            RunRepack(service, state, fixture);

            var report = BuildRepackSection(state);

            Assert.That(report, Does.Contain("Option C selective view"));
            Assert.That(report, Does.Contain("All sets by saving:"));
            Assert.That(report, Does.Contain("Build only the best N sets by saving"));
            Assert.That(report, Does.Contain("best 1 (1 set(s), 1 texture(s)): saves"));
            Assert.That(report, Does.Contain("all (3 set(s), 3 texture(s)): saves"));
            Assert.That(report, Does.Contain("Best efficiency (saving per rigid touched), top 10 positive sets:"));
            Assert.That(report, Does.Contain("character: no; e.g. unit"));
        }

        // --- Shared texture repack ---------------------------------------------------------

        private const string SrRigid = "variantmeshes\\wh_main\\sr.rigid_model_v2";
        private const string SrWs = "variantmeshes\\wh_main\\sr.wsmodel";
        private const string SrRoot = "variantmeshes\\wh_variantmodels\\hu1\\emp\\sr\\sr.variantmeshdefinition";
        private const string SrRigid2 = "variantmeshes\\wh_main\\sr2.rigid_model_v2";
        private const string SrWs2 = "variantmeshes\\wh_main\\sr2.wsmodel";
        private const string SrRoot2 = "variantmeshes\\wh_variantmodels\\hu1\\emp\\sr2\\sr2.variantmeshdefinition";
        private const string SrBase = "t_xml_base_colour";
        private const string SrNormal = "t_xml_normal";
        private const string SrMask = "t_xml_mask";
        private const string SrMaterialMap = "t_xml_material_map";

        private static readonly Dictionary<(int, string, bool, int), byte[]> SrTextureCache = [];

        // Colour of one of the 16 x 16 UV blocks of a content texture; every texture of a slot
        // seed shows the same pattern at its own resolution.
        private static (byte R, byte G, byte B) SrColour(int seed, int blockX, int blockY)
            => ((byte)((blockX * 40 + seed * 17 + 20) % 256),
                (byte)((blockY * 40 + seed * 29 + 20) % 256),
                (byte)(((blockX + blockY * 3) * 30 + seed * 11) % 256));

        private static byte[] BuildContentPixels(int size, int seed)
        {
            var bgra = new byte[size * size * 4];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var (r, g, b) = SrColour(seed, x * 16 / size, y * 16 / size);
                    var offset = (y * size + x) * 4;
                    bgra[offset] = b;
                    bgra[offset + 1] = g;
                    bgra[offset + 2] = r;
                    bgra[offset + 3] = 255;
                }
            }
            return bgra;
        }

        private static byte[] BuildContentDds(int size, string format, bool srgb, int seed)
        {
            var key = (size, format, srgb, seed);
            if (SrTextureCache.TryGetValue(key, out var cached))
                return cached;

            var bgra = BuildContentPixels(size, seed);

            var arguments = new object?[] { bgra, size, size, format, srgb, true, null, null };
            Assert.That((bool)GetStaticMethod("TryEncodeSharedRepackDds").Invoke(null, arguments)!, Is.True);
            SrTextureCache[key] = (byte[])arguments[6]!;
            return SrTextureCache[key];
        }

        private static readonly Dictionary<(int, bool, int), byte[]> SrQuickBc7TextureCache = [];

        private static byte[] BuildQuickBc7ContentDds(int size, bool srgb, int seed)
        {
            var key = (size, srgb, seed);
            if (SrQuickBc7TextureCache.TryGetValue(key, out var cached))
                return cached;

            var bgra = BuildContentPixels(size, seed);
            var uncompressed = srgb
                ? Editors.ImportExport.Dds.Native.DXGI_FORMAT.B8G8R8A8_UNORM_SRGB
                : Editors.ImportExport.Dds.Native.DXGI_FORMAT.B8G8R8A8_UNORM;
            var compressed = srgb
                ? Editors.ImportExport.Dds.Native.DXGI_FORMAT.BC7_UNORM_SRGB
                : Editors.ImportExport.Dds.Native.DXGI_FORMAT.BC7_UNORM;
            using var source = Editors.ImportExport.Dds.Native.TexHelper.Instance.Initialize2D(
                uncompressed, size, size, 1, 1, Editors.ImportExport.Dds.Native.CP_FLAGS.NONE);
            var image = source.GetImage(0, 0, 0);
            var pitch = checked((int)image.RowPitch);
            for (var y = 0; y < size; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    bgra, y * size * 4, IntPtr.Add(image.Pixels, y * pitch), size * 4);

            using var chain = source.GenerateMipMaps(
                Editors.ImportExport.Dds.Native.TEX_FILTER_FLAGS.DEFAULT, 0);
            using var packed = chain.Compress(
                compressed, Editors.ImportExport.Dds.Native.TEX_COMPRESS_FLAGS.BC7_QUICK, 0.5f);
            using var stream = packed.SaveToDDSMemory(
                Editors.ImportExport.Dds.Native.DDS_FLAGS.FORCE_DX10_EXT);
            cached = stream.ToArray();
            SrQuickBc7TextureCache[key] = cached;
            return cached;
        }

        private static (byte[] Bgra, int Width, int Height) DecodeDds(byte[] dds)
        {
            var arguments = new object?[] { dds, null, 0, 0, null };
            Assert.That((bool)GetStaticMethod("TryDecodeDdsTopLevel").Invoke(null, arguments)!, Is.True);
            return ((byte[])arguments[1]!, (int)arguments[2]!, (int)arguments[3]!);
        }

        // A quad covering the inside of one UV block (offset by whole tiles when asked).
        private static float[][] SrBlock(int blockX, int blockY, int tileU = 0, int tileV = 0)
            => UvQuad(
                blockX / 16.0 + 0.004 + tileU,
                blockY / 16.0 + 0.004 + tileV,
                (blockX + 1) / 16.0 - 0.004 + tileU,
                (blockY + 1) / 16.0 - 0.004 + tileV);

        private static float[][] SrBlocks(params (int X, int Y)[] blocks)
            => blocks.SelectMany(block => SrBlock(block.X, block.Y)).ToArray();

        private static Func<RmvFile> SrRigidFactory(params (string Material, float[][] Triangles)[][] lods)
            => () => new RmvFile
            {
                ModelList = lods.Select(lod => lod.Select(part => CreateUvPart(part.Triangles)).ToArray()).ToArray(),
            };

        private sealed class SrWorld
        {
            public DownscaleFixture Fixture { get; } = new();
            public Dictionary<string, Func<RmvFile>> Rigids { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<(string Root, string Ws)> Links { get; } = [];
            public List<(string Root, string Rigid)> DirectLinks { get; } = [];
            // Roots whose traversal failed (partial discovery) and the raw text of each.
            public List<(string Root, string Text)> IncompleteRoots { get; } = [];
            public object Service { get; set; } = null!;
            public object State { get; set; } = null!;
            public bool Downscale { get; set; }

            public void AddTexture(string path, int size, string format, bool srgb, int seed, bool owned = false)
            {
                var bytes = format == "BC7"
                    ? BuildQuickBc7ContentDds(size, srgb, seed)
                    : BuildContentDds(size, format, srgb, seed);
                if (owned)
                    Fixture.Add(path, bytes);
                else
                    Fixture.Vanilla[path] = bytes;
            }

            public void AddModel(
                string rigid,
                string ws,
                string root,
                Func<RmvFile> factory,
                string[][] materialsByLod,
                bool ownedRigid = true)
            {
                Rigids[rigid] = factory;
                Fixture.Rigids[rigid] = factory();
                Fixture.AddText(ws, CreateWsModelDocument(rigid, materialsByLod).OuterXml);
                if (ownedRigid)
                    Fixture.Add(rigid, [0]);
                if (!Links.Contains((root, ws)))
                    Links.Add((root, ws));
            }

            public void AddDirectConsumer(string rigid, string root)
            {
                DirectLinks.Add((root, rigid));
                Fixture.AddText(root, $"<VARIANT_MESH><rigid>{rigid}</rigid></VARIANT_MESH>");
            }

            public void AddDirectModel(string rigid, string root, Func<RmvFile> factory)
            {
                Rigids[rigid] = factory;
                Fixture.Rigids[rigid] = factory();
                Fixture.Add(rigid, [0]);
                AddDirectConsumer(rigid, root);
            }

            public object Analyse(bool includeDirectConsumers = true)
            {
                (Service, State) = CreateDownscaleState(Fixture, Downscale);
                var index = CreateDownscaleIndex(Links.ToArray());
                var rootKind = Enum.Parse(GetNestedType("GameplayMeshDependencyRootKind"), "SourcePackVmd");
                foreach (var (root, rigid) in includeDirectConsumers ? DirectLinks : [])
                {
                    index.GetType().GetMethod("RegisterRoot")!.Invoke(index, [root, rootKind]);
                    index.GetType().GetMethod("AddDirectConsumer")!.Invoke(index, [rigid, root]);
                }

                foreach (var (root, text) in IncompleteRoots)
                {
                    Fixture.AddText(root, text);
                    index.GetType().GetMethod("RegisterRoot")!.Invoke(index, [root, rootKind]);
                    index.GetType().GetMethod("RecordFailure")!.Invoke(index,
                    [
                        root, root, "VMD could not be parsed",
                        Enum.Parse(GetNestedType("GameplayMeshDependencyFailureKind"), "ParseFailure"),
                        Enum.Parse(GetNestedType("GameplayMeshDependencyAssetScope"), "SourcePack"),
                    ]);
                }

                InstallDownscaleGameplay(
                    State, CreateDownscaleResolution(true), index);
                if (IncompleteRoots.Count != 0)
                    SetStateProperty(State, "StructuralMergeConsumerDiscoveryComplete", false);
                RunCoverage(Service, State, Fixture);
                GetServiceType()
                    .GetMethod("RunUnitTextureRepack", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(Service, [State, CancellationToken.None, null, null]);
                return State;
            }

            public object Run(
                long minimumSaving = 100_000,
                bool analyse = true,
                long minimumGroupSaving = 10_000,
                int maximumSide = 4096)
            {
                if (analyse)
                    Analyse();
                GetServiceType()
                    .GetMethod("RunSharedTextureRepack", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(
                        Service,
                        [
                            State,
                            CancellationToken.None,
                            null,
                            (Func<string, RmvFile?>)(path => Rigids.TryGetValue(path, out var factory) ? factory() : null),
                            (long?)minimumSaving,
                            (long?)minimumGroupSaving,
                            (int?)maximumSide,
                        ]);
                return Get<object>(State, "SharedTextureRepack");
            }
        }

        private static string SrMaterialPath(string name) => $"materials\\sr_{name}.xml.material";

        private static Func<RmvFile> SrEmbeddedRigidFactory(
            string baseColour,
            string normal,
            string? legacyDiffuse = null,
            ModelMaterialEnum materialType = ModelMaterialEnum.weighted,
            string? mask = null,
            string? materialMap = null,
            string? legacySpecular = null,
            string? legacyGloss = null,
            VertexFormat vertexFormat = VertexFormat.Weighted)
            => () =>
            {
                var model = CreateUvPart(SrBlock(2, 3));
                var material = new WeightedMaterial
                {
                    BinaryVertexFormat = vertexFormat,
                    MaterialId = materialType,
                    ModelName = "embedded",
                };
                material.SetTexture(TextureType.BaseColour, baseColour);
                material.SetTexture(TextureType.Normal, normal);
                if (mask != null)
                    material.SetTexture(TextureType.Mask, mask);
                if (materialMap != null)
                    material.SetTexture(TextureType.MaterialMap, materialMap);
                if (legacyDiffuse != null)
                    material.SetTexture(TextureType.Diffuse, legacyDiffuse);
                if (legacySpecular != null)
                    material.SetTexture(TextureType.Specular, legacySpecular);
                if (legacyGloss != null)
                    material.SetTexture(TextureType.Gloss, legacyGloss);
                model.Material = material;
                return CreateSingleLodRigid(RmvVersionEnum.RMV2_V8, model);
            };

        private static SrWorld BuildDirectEmbeddedSrWorld(
            bool withWsModel,
            ModelMaterialEnum materialType = ModelMaterialEnum.weighted,
            VertexFormat vertexFormat = VertexFormat.Weighted)
        {
            var world = new SrWorld();
            world.AddTexture("textures\\sr_direct_base.dds", 1024, "BC1", true, 11);
            world.AddTexture("textures\\sr_direct_normal.dds", 512, "BC3", false, 12);
            world.AddTexture("textures\\sr_direct_mask.dds", 1024, "BC1", false, 13);
            world.AddTexture("textures\\sr_direct_material_map.dds", 1024, "BC1", false, 14);
            world.AddTexture("textures\\sr_direct_specular.dds", 16, "BC1", false, 15);
            world.AddTexture("textures\\sr_direct_gloss.dds", 16, "BC1", false, 16);
            var materialPath = SrMaterialPath("direct");
            world.Fixture.AddMaterial(
                materialPath,
                (SrBase, "textures\\sr_direct_base.dds"),
                (SrNormal, "textures\\sr_direct_normal.dds"),
                (SrMask, "textures\\sr_direct_mask.dds"),
                (SrMaterialMap, "textures\\sr_direct_material_map.dds"));
            var rigidFactory = SrEmbeddedRigidFactory(
                "textures\\sr_direct_base.dds",
                "textures\\sr_direct_normal.dds",
                legacyDiffuse: "textures\\sr_direct_base.dds",
                mask: "textures\\sr_direct_mask.dds",
                materialMap: "textures\\sr_direct_material_map.dds",
                legacySpecular: "textures\\sr_direct_specular.dds",
                legacyGloss: "textures\\sr_direct_gloss.dds",
                materialType: materialType,
                vertexFormat: vertexFormat);
            if (withWsModel)
            {
                world.AddModel(SrRigid, SrWs, SrRoot, rigidFactory, [[materialPath]]);
                world.AddDirectConsumer(SrRigid, SrRoot);
            }
            else
            {
                world.AddDirectModel(SrRigid, SrRoot, rigidFactory);
            }

            return world;
        }

        // Rigid with two LODs; part 0 uses material m1 (base b1, normal n1), part 1 uses m2.
        // Both LODs show the same regions.
        private static SrWorld BuildStandardSrWorld(
            bool secondLod = true,
            string baseFormat = "BC1",
            bool baseSrgb = true,
            int baseSize = 1024)
        {
            var world = new SrWorld();
            world.AddTexture("textures\\sr_b1.dds", baseSize, baseFormat, baseSrgb, 1);
            world.AddTexture("textures\\sr_n1.dds", baseSize / 2, "BC3", false, 2);
            world.AddTexture("textures\\sr_b2.dds", baseSize, baseFormat, baseSrgb, 3);
            world.AddTexture("textures\\sr_n2.dds", baseSize / 2, "BC3", false, 4);
            world.Fixture.AddMaterial(SrMaterialPath("m1"), (SrBase, "textures\\sr_b1.dds"), (SrNormal, "textures\\sr_n1.dds"));
            world.Fixture.AddMaterial(SrMaterialPath("m2"), (SrBase, "textures\\sr_b2.dds"), (SrNormal, "textures\\sr_n2.dds"));
            var m1 = SrMaterialPath("m1");
            var m2 = SrMaterialPath("m2");
            var lod0 = new[] { (m1, SrBlocks((2, 3), (3, 3))), (m2, SrBlocks((10, 4))) };
            var lod1 = new[] { (m1, SrBlocks((2, 3))), (m2, SrBlocks((10, 4))) };
            world.AddModel(
                SrRigid,
                SrWs,
                SrRoot,
                secondLod ? SrRigidFactory(lod0, lod1) : SrRigidFactory(lod0),
                secondLod ? [[m1, m2], [m1, m2]] : [[m1, m2]]);
            return world;
        }

        private static List<object> SrBuilt(object result)
            => Get<IEnumerable>(result, "BuiltSets").Cast<object>().ToList();

        private static string SrSkips(object result)
            => string.Join(
                "\n",
                Get<IEnumerable>(result, "Skips").Cast<object>().Select(item => item.ToString()));

        private static void AssertFunnelReconciles(object result)
        {
            var considered = Get<int>(result, "SetsConsidered");
            var accounted = Get<int>(result, "BelowThreshold") + Get<int>(result, "MergedIntoOther") +
                            Get<int>(result, "RigidNotEligible") + Get<int>(result, "DroppedByClosure") +
                            Get<int>(result, "NotBuildable") + Get<int>(result, "FailedActualBytes") +
                            Get<int>(result, "FailedPixelCheck") + Get<int>(result, "Built");
            Assert.That(accounted, Is.EqualTo(considered), SrSkips(result));
            Assert.That(Get<IEnumerable>(result, "Skips").Cast<object>().Count(),
                Is.EqualTo(considered - Get<int>(result, "Built")));
        }

        private static (byte[] Bgra, int Width, int Height) SrGeneratedTexture(
            SrWorld world,
            string materialPath,
            string slot)
        {
            var material = new XmlDocument();
            material.LoadXml(System.Text.Encoding.UTF8.GetString(world.Fixture.Output[materialPath]));
            var texture = material.SelectNodes("/material/textures/texture")!.Cast<XmlNode>()
                .Single(node => node.SelectSingleNode("slot")!.InnerText.Trim() == slot)
                .SelectSingleNode("source")!.InnerText;
            return DecodeDds(world.Fixture.Output[texture]);
        }

        private static string SrWsMaterial(object state, string wsPath, int lod, int part)
        {
            var document = (XmlDocument)Get<IDictionary>(state, "WsDocuments")[wsPath]!;
            return document.SelectNodes("/model/materials/material")!.Cast<XmlNode>()
                .Single(node => node.Attributes!["lod_index"]!.Value == lod.ToString() &&
                                node.Attributes["part_index"]!.Value == part.ToString())
                .InnerText;
        }

        // Every triangle of every part must show the same colour at its new UV as the source
        // pattern shows at its old UV, in every slot of its material.
        private static (int Samples, double Max) AssertSrPixels(
            SrWorld world,
            string rigidPath,
            string wsPath,
            Func<RmvFile> original,
            Dictionary<string, int> seedsBySlot,
            Func<int, int, bool>? partFilter = null)
        {
            var before = original();
            var after = (RmvFile)Get<IDictionary>(world.State, "RigidModels")[rigidPath]!;
            var samples = 0;
            double max = 0;
            for (var lod = 0; lod < before.ModelList.Length; lod++)
            {
                for (var part = 0; part < before.ModelList[lod].Length; part++)
                {
                    if (partFilter != null && !partFilter(lod, part))
                        continue;

                    var material = SrWsMaterial(world.State, wsPath, lod, part);
                    var oldMesh = before.ModelList[lod][part].Mesh;
                    var newMesh = after.ModelList[lod][part].Mesh;
                    Assert.That(newMesh.IndexList, Is.EqualTo(oldMesh.IndexList));
                    foreach (var (slot, seed) in seedsBySlot)
                    {
                        var materialXml = new XmlDocument();
                        materialXml.LoadXml(System.Text.Encoding.UTF8.GetString(world.Fixture.Output[material]));
                        var hasSlot = materialXml.SelectNodes("/material/textures/texture")!.Cast<XmlNode>()
                            .Any(node => node.SelectSingleNode("slot")!.InnerText.Trim() == slot);
                        if (!hasSlot)
                            continue;

                        var (bgra, width, height) = SrGeneratedTexture(world, material, slot);
                        for (var index = 0; index < oldMesh.IndexList.Length; index += 3)
                        {
                            double ou = 0, ov = 0, nu = 0, nv = 0;
                            for (var corner = 0; corner < 3; corner++)
                            {
                                var vertex = oldMesh.IndexList[index + corner];
                                ou += oldMesh.VertexList[vertex].Uv.X / 3.0;
                                ov += oldMesh.VertexList[vertex].Uv.Y / 3.0;
                                nu += newMesh.VertexList[vertex].Uv.X / 3.0;
                                nv += newMesh.VertexList[vertex].Uv.Y / 3.0;
                            }

                            ou -= Math.Floor(ou);
                            ov -= Math.Floor(ov);
                            var (r, g, b) = SrColour(seed, (int)(ou * 16), (int)(ov * 16));
                            var x = Math.Clamp((int)(nu * width), 0, width - 1);
                            var y = Math.Clamp((int)(nv * height), 0, height - 1);
                            var pixel = (y * width + x) * 4;
                            var error = new[]
                            {
                                Math.Abs(bgra[pixel] - b), Math.Abs(bgra[pixel + 1] - g), Math.Abs(bgra[pixel + 2] - r),
                            }.Max();
                            max = Math.Max(max, error);
                            samples++;
                            Assert.That(error, Is.LessThanOrEqualTo(12), $"{slot} lod {lod} part {part} tri {index / 3}");
                            Assert.That(nu, Is.InRange(0.0, 1.0));
                            Assert.That(nv, Is.InRange(0.0, 1.0));
                        }
                    }
                }
            }

            return (samples, max);
        }

        private static readonly Dictionary<string, int> SrPart0Seeds = new() { [SrBase] = 1, [SrNormal] = 2 };
        private static readonly Dictionary<string, int> SrPart1Seeds = new() { [SrBase] = 3, [SrNormal] = 4 };

        private static List<string> SrValidate(SrWorld world)
        {
            var errors = new List<string>();
            GetServiceType()
                .GetMethod("ValidateSharedTextureRepack", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(world.Service, [world.State, errors, CancellationToken.None]);
            return errors;
        }

        private static List<string> SrValidateDirectRigidEmbeddedTextures(SrWorld world)
        {
            var errors = new List<string>();
            GetServiceType()
                .GetMethod("ValidateDirectRigidEmbeddedTextureReferences", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(world.Service, [world.State, errors, CancellationToken.None]);
            return errors;
        }

        private static void SrInstallReadableSourceRigid(SrWorld world)
        {
            var rigid = world.Rigids[SrRigid]();
            foreach (var model in rigid.ModelList.SelectMany(lod => lod))
            {
                var isStatic = model.Material?.BinaryVertexFormat == VertexFormat.Static;
                foreach (var vertex in model.Mesh?.VertexList ?? [])
                {
                    vertex.WeightCount = isStatic ? 0 : 2;
                    vertex.BoneIndex = isStatic ? [] : [0, 0];
                    vertex.BoneWeight = isStatic ? [] : [1f, 0f];
                }
            }

            rigid.RecalculateOffsets();
            var bytes = ModelFactory.Create().Save(rigid, validateByReloading: false, logProgress: false);
            world.Fixture.Source[SrRigid] = bytes;
            world.Fixture.Output[SrRigid] = bytes;
        }

        [Test]
        public void SharedRepack_DirectRigid_RewritesEmbeddedSlotsAndKeepsLegacyPathsResolvable()
        {
            var world = BuildDirectEmbeddedSrWorld(withWsModel: true);
            SrInstallReadableSourceRigid(world);
            var original = world.Rigids[SrRigid]();

            var result = world.Run(minimumSaving: 1, minimumGroupSaving: 1);

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            Assert.That(Get<HashSet<string>>(result, "EmbeddedRewrittenRigids"), Is.EquivalentTo([SrRigid]));
            Assert.That(Get<long>(result, "Samples"), Is.GreaterThan(0));
            Assert.That(Get<IEnumerable>(Get<object>(world.State, "UnitTextureRepack"), "SetsC"), Is.Not.Empty);
            var unit = Get<IEnumerable>(Get<object>(world.State, "UnitTextureRepack"), "Units").Cast<object>()
                .Single(item => Get<string>(item, "Root").Equals(SrRoot, StringComparison.OrdinalIgnoreCase));
            Assert.That(Get<HashSet<string>>(unit, "DirectRigids"), Does.Contain(SrRigid));
            Assert.That(Get<IDictionary>(unit, "Textures"), Has.Count.EqualTo(4));

            var rigid = (RmvFile)Get<IDictionary>(world.State, "RigidModels")[SrRigid]!;
            var embedded = rigid.ModelList[0][0].Material!;
            var generatedMaterial = new XmlDocument();
            generatedMaterial.LoadXml(Encoding.UTF8.GetString(world.Fixture.Output[SrWsMaterial(world.State, SrWs, 0, 0)]));
            string Generated(string slot)
                => generatedMaterial.SelectNodes("/material/textures/texture")!.Cast<XmlNode>()
                    .Single(node => node.SelectSingleNode("slot")!.InnerText.Trim() == slot)
                    .SelectSingleNode("source")!.InnerText;

            foreach (var (type, slot) in new[]
                     {
                         (TextureType.BaseColour, SrBase),
                         (TextureType.Normal, SrNormal),
                         (TextureType.Mask, SrMask),
                         (TextureType.MaterialMap, SrMaterialMap),
                     })
            {
                var path = embedded.GetTexture(type)!.Value.Path;
                Assert.That(path, Is.EqualTo(Generated(slot)));
                Assert.That(world.Fixture.Output.ContainsKey(path), Is.True, path);
            }

            Assert.That(embedded.GetTexture(TextureType.Diffuse)!.Value.Path,
                Is.EqualTo(embedded.GetTexture(TextureType.BaseColour)!.Value.Path));
            Assert.That(embedded.GetTexture(TextureType.Specular)!.Value.Path,
                Is.EqualTo("textures\\sr_direct_specular.dds"));
            Assert.That(embedded.GetTexture(TextureType.Gloss)!.Value.Path,
                Is.EqualTo("textures\\sr_direct_gloss.dds"));
            Assert.That(rigid.ModelList[0][0].Mesh!.VertexList.Select(vertex => vertex.Uv),
                Is.Not.EqualTo(original.ModelList[0][0].Mesh!.VertexList.Select(vertex => vertex.Uv)));
            Assert.That(BuildSharedRepackSection(Enabled(world)),
                Does.Contain($"[embedded material, loaded directly by {SrRoot}]").And.Contain(SrRigid));
            Assert.That(SrValidateDirectRigidEmbeddedTextures(world), Is.Empty);
            Assert.That(SrValidate(world), Is.Empty);
            Assert.That(Get<IEnumerable>(world.State, "ValidationMessages").Cast<string>(),
                Has.Some.Contain("PASS:").And.Contain("embedded material texture path"));
        }

        [Test]
        public void SharedRepack_DirectRigid_UniformEmbeddedMaskStaysAsAuthored_WhileOtherSlotsFollowThePages()
        {
            var world = BuildDirectEmbeddedSrWorld(withWsModel: true);
            const string maskPath = "textures\\sr_direct_mask.dds";
            world.Fixture.Vanilla[maskPath] = BuildFlatBlackDds(4);
            SrInstallReadableSourceRigid(world);
            var original = world.Rigids[SrRigid]();

            var result = world.Run(minimumSaving: 1, minimumGroupSaving: 1);

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            Assert.That(Get<HashSet<string>>(result, "EmbeddedRewrittenRigids"), Is.EquivalentTo([SrRigid]));
            var rigid = (RmvFile)Get<IDictionary>(world.State, "RigidModels")[SrRigid]!;
            var embedded = rigid.ModelList[0][0].Material!;
            var generatedMaterial = new XmlDocument();
            generatedMaterial.LoadXml(Encoding.UTF8.GetString(world.Fixture.Output[SrWsMaterial(world.State, SrWs, 0, 0)]));
            string Generated(string slot)
                => generatedMaterial.SelectNodes("/material/textures/texture")!.Cast<XmlNode>()
                    .Single(node => node.SelectSingleNode("slot")!.InnerText.Trim() == slot)
                    .SelectSingleNode("source")!.InnerText;

            Assert.That(embedded.GetTexture(TextureType.Mask)!.Value.Path, Is.EqualTo(maskPath));
            Assert.That(Generated(SrMask), Is.EqualTo(maskPath));
            foreach (var (type, slot) in new[]
                     {
                         (TextureType.BaseColour, SrBase),
                         (TextureType.Normal, SrNormal),
                         (TextureType.MaterialMap, SrMaterialMap),
                     })
            {
                var path = embedded.GetTexture(type)!.Value.Path;
                Assert.That(path, Does.Contain("_repack_"));
                Assert.That(path, Is.EqualTo(Generated(slot)));
                Assert.That(world.Fixture.Output.ContainsKey(path), Is.True, path);
            }

            Assert.That(rigid.ModelList[0][0].Mesh!.VertexList.Select(vertex => vertex.Uv),
                Is.Not.EqualTo(original.ModelList[0][0].Mesh!.VertexList.Select(vertex => vertex.Uv)));
            Assert.That(SrValidateDirectRigidEmbeddedTextures(world), Is.Empty);
            Assert.That(SrValidate(world), Is.Empty);
            Assert.That(Get<IEnumerable>(world.State, "ValidationMessages").Cast<string>(),
                Has.Some.Contain("PASS:").And.Contain("embedded material texture path"));
        }

        [Test]
        public void SharedRepack_DirectRigid_EmbeddedAndWsModelTextureMismatchDropsThePart()
        {
            var world = BuildDirectEmbeddedSrWorld(withWsModel: true);
            world.AddTexture("textures\\sr_direct_ws_base.dds", 1024, "BC1", true, 21);
            world.Fixture.AddMaterial(
                SrMaterialPath("direct"),
                (SrBase, "textures\\sr_direct_ws_base.dds"),
                (SrNormal, "textures\\sr_direct_normal.dds"),
                (SrMask, "textures\\sr_direct_mask.dds"),
                (SrMaterialMap, "textures\\sr_direct_material_map.dds"));

            var result = world.Run(minimumSaving: 1, minimumGroupSaving: 1);

            var drops = Get<Dictionary<string, int>>(result, "PartDropReasons");
            Assert.That(drops.GetValueOrDefault(
                "embedded material textures disagree with the WSModel binding for the same part"), Is.GreaterThan(0));
            Assert.That(Get<int>(result, "Built"), Is.Zero, SrSkips(result));
        }

        [Test]
        public void SharedRepack_DirectRigid_UnusedWsModelDoesNotConstrainEmbeddedMaterial()
        {
            var world = BuildDirectEmbeddedSrWorld(withWsModel: false);
            world.AddTexture("textures\\sr_direct_ws_base.dds", 1024, "BC1", true, 21);
            var materialPath = SrMaterialPath("unused_direct");
            world.Fixture.AddMaterial(
                materialPath,
                (SrBase, "textures\\sr_direct_ws_base.dds"),
                (SrNormal, "textures\\sr_direct_normal.dds"),
                (SrMask, "textures\\sr_direct_mask.dds"),
                (SrMaterialMap, "textures\\sr_direct_material_map.dds"));
            world.Fixture.AddText(
                SrWs,
                CreateWsModelDocument(SrRigid, [[materialPath]]).OuterXml);

            var result = world.Run(minimumSaving: 1, minimumGroupSaving: 1);

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            Assert.That(Get<HashSet<string>>(result, "EmbeddedRewrittenRigids"), Is.EquivalentTo([SrRigid]));
            Assert.That(Get<HashSet<string>>(world.State, "ModifiedWsModels"), Does.Not.Contain(SrWs));
            Assert.That(world.Fixture.Output[SrWs], Is.EqualTo(Encoding.UTF8.GetBytes(
                CreateWsModelDocument(SrRigid, [[materialPath]]).OuterXml)));
            Assert.That(BuildSharedRepackSection(Enabled(world)),
                Does.Contain("[unused WSModel left as authored]").And.Contain(SrWs));
            Assert.That(SrValidate(world), Is.Empty);
        }

        private static SrWorld BuildUnusedWsModelPartialWorld(string incompleteRootText)
        {
            var world = BuildDirectEmbeddedSrWorld(withWsModel: false);
            world.AddTexture("textures\\sr_direct_ws_base.dds", 1024, "BC1", true, 21);
            var materialPath = SrMaterialPath("unused_direct");
            world.Fixture.AddMaterial(
                materialPath,
                (SrBase, "textures\\sr_direct_ws_base.dds"),
                (SrNormal, "textures\\sr_direct_normal.dds"),
                (SrMask, "textures\\sr_direct_mask.dds"),
                (SrMaterialMap, "textures\\sr_direct_material_map.dds"));
            world.Fixture.AddText(SrWs, CreateWsModelDocument(SrRigid, [[materialPath]]).OuterXml);
            world.IncompleteRoots.Add(("variantmeshes\\broken.variantmeshdefinition", incompleteRootText));
            return world;
        }

        [Test]
        public void SharedRepack_DirectRigid_UnusedWsModelUnderPartialDiscoveryWhenNoIncompleteRootMentionsIt()
        {
            var world = BuildUnusedWsModelPartialWorld("<broken>unrelated</broken>");

            var result = world.Run(minimumSaving: 1, minimumGroupSaving: 1);

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            Assert.That(Get<HashSet<string>>(result, "EmbeddedRewrittenRigids"), Is.EquivalentTo([SrRigid]));
            Assert.That(BuildSharedRepackSection(Enabled(world)), Does.Contain("[unused WSModel left as authored]"));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_DirectRigid_WsModelNamedByIncompleteRootStaysBlocked()
        {
            var fileName = SrWs[(SrWs.LastIndexOf('\\') + 1)..].ToUpperInvariant();
            var world = BuildUnusedWsModelPartialWorld($"<broken>{fileName}</broken>");

            var result = world.Run(minimumSaving: 1, minimumGroupSaving: 1);

            Assert.That(Get<int>(result, "Built"), Is.Zero);
            Assert.That(Get<HashSet<string>>(result, "EmbeddedRewrittenRigids"), Is.Empty);
            Assert.That(SrSkips(result) + BuildSharedRepackSection(Enabled(world)),
                Does.Contain("incomplete root variantmeshes\\broken.variantmeshdefinition mentions it"));
        }

        [Test]
        public void SharedRepack_DirectRigid_WsModelNamedBySourceFileStaysBlockedUnderPartialDiscovery()
        {
            var world = BuildUnusedWsModelPartialWorld("<broken>unrelated</broken>");
            world.Fixture.AddText("variantmeshes\\other.variantmeshdefinition", $"<x>{SrWs}</x>");

            var result = world.Run(minimumSaving: 1, minimumGroupSaving: 1);

            Assert.That(Get<int>(result, "Built"), Is.Zero);
            Assert.That(SrSkips(result) + BuildSharedRepackSection(Enabled(world)),
                Does.Contain("source file variantmeshes\\other.variantmeshdefinition mentions it"));
        }

        [Test]
        public void SharedRepack_DirectRigid_WsModelNamedByDbTableStaysBlockedUnderPartialDiscovery()
        {
            var world = BuildUnusedWsModelPartialWorld("<broken>unrelated</broken>");
            var fileName = SrWs[(SrWs.LastIndexOf('\\') + 1)..];
            world.Fixture.AddText("db\\some_tables\\data__", $"\0\0row\0{fileName.ToUpperInvariant()}\0");

            var result = world.Run(minimumSaving: 1, minimumGroupSaving: 1);

            Assert.That(Get<int>(result, "Built"), Is.Zero);
            Assert.That(SrSkips(result) + BuildSharedRepackSection(Enabled(world)),
                Does.Contain("DB table db\\some_tables\\data__ mentions it"));
        }

        [Test]
        public void SharedRepack_DirectOnlyRootDoesNotShiftUnitIndicesOfOtherUnits()
        {
            var plain = BuildStandardSrWorld();
            plain.Analyse();
            var withDirect = BuildStandardSrWorld();
            // Sorts before every other root.
            withDirect.AddDirectModel(
                "variantmeshes\\a_direct.rigid_model_v2",
                "a_first.variantmeshdefinition",
                SrEmbeddedRigidFactory("textures\\a_direct_base.dds", "textures\\a_direct_normal.dds"));
            withDirect.AddTexture("textures\\a_direct_base.dds", 1024, "BC1", true, 31);
            withDirect.AddTexture("textures\\a_direct_normal.dds", 512, "BC3", false, 32);
            withDirect.Analyse();

            string[] Roots(object state) => Get<IEnumerable>(Get<object>(state, "UnitTextureRepack"), "Units")
                .Cast<object>().OrderBy(unit => Get<int>(unit, "Index"))
                .Select(unit => Get<string>(unit, "Root")).ToArray();
            var before = Roots(plain.State);
            var after = Roots(withDirect.State);
            Assert.That(after.Take(before.Length), Is.EqualTo(before));
            Assert.That(after.Length, Is.GreaterThan(before.Length));
        }

        [Test]
        public void SharedRepack_DirectRigidValidationChecksLegacyPathsThatWereNotRewritten()
        {
            var world = BuildDirectEmbeddedSrWorld(withWsModel: false);
            SrInstallReadableSourceRigid(world);
            world.Run(minimumSaving: 1, minimumGroupSaving: 1);
            var rigid = (RmvFile)Get<IDictionary>(world.State, "RigidModels")[SrRigid]!;
            rigid.ModelList[0][0].Material!.SetTexture(TextureType.Gloss, "textures\\missing_legacy.dds");
            var errors = SrValidateDirectRigidEmbeddedTextures(world);

            Assert.That(errors, Has.Some.Contain("Gloss -> textures\\missing_legacy.dds"));
        }

        [Test]
        public void SharedRepack_DirectRigid_DefaultTypeEmbeddedMaterialIsRewritten()
        {
            var world = BuildDirectEmbeddedSrWorld(
                withWsModel: false, ModelMaterialEnum.default_type, VertexFormat.Static);
            SrInstallReadableSourceRigid(world);
            var original = world.Rigids[SrRigid]();

            var result = world.Run(minimumSaving: 1, minimumGroupSaving: 1);

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            Assert.That(Get<HashSet<string>>(result, "EmbeddedRewrittenRigids"), Is.EquivalentTo([SrRigid]));
            var types = Get<IDictionary>(result, "EmbeddedRewrittenPartTypes").Values.Cast<ModelMaterialEnum>();
            Assert.That(types, Is.EqualTo(new[] { ModelMaterialEnum.default_type }));
            var rigid = (RmvFile)Get<IDictionary>(world.State, "RigidModels")[SrRigid]!;
            var embedded = rigid.ModelList[0][0].Material!;
            foreach (var type in new[]
                     {
                         TextureType.BaseColour, TextureType.Normal, TextureType.Mask, TextureType.MaterialMap,
                     })
            {
                var path = embedded.GetTexture(type)!.Value.Path;
                Assert.That(path, Does.Not.StartWith("textures\\sr_direct_"));
                Assert.That(world.Fixture.Output.ContainsKey(path), Is.True, path);
            }

            // Legacy entries follow only the replaced texture they named.
            Assert.That(embedded.GetTexture(TextureType.Diffuse)!.Value.Path,
                Is.EqualTo(embedded.GetTexture(TextureType.BaseColour)!.Value.Path));
            Assert.That(embedded.GetTexture(TextureType.Specular)!.Value.Path,
                Is.EqualTo("textures\\sr_direct_specular.dds"));
            Assert.That(embedded.GetTexture(TextureType.Gloss)!.Value.Path,
                Is.EqualTo("textures\\sr_direct_gloss.dds"));
            Assert.That(rigid.ModelList[0][0].Mesh!.VertexList.Select(vertex => vertex.Uv),
                Is.Not.EqualTo(original.ModelList[0][0].Mesh!.VertexList.Select(vertex => vertex.Uv)));
            Assert.That(BuildSharedRepackSection(Enabled(world)),
                Does.Contain("by embedded material type: default_type 1")
                    .And.Contain("Burn caveat"));
            Assert.That(SrValidateDirectRigidEmbeddedTextures(world), Is.Empty);
            Assert.That(SrValidate(world), Is.Empty);
        }

        [TestCase(ModelMaterialEnum.decal)]
        [TestCase(ModelMaterialEnum.weighted_skin)]
        public void SharedRepack_DirectRigid_UnsupportedEmbeddedMaterialTypeIsRejected(ModelMaterialEnum type)
        {
            var world = BuildDirectEmbeddedSrWorld(withWsModel: false, type);

            var result = world.Run(minimumSaving: 1, minimumGroupSaving: 1);

            var drops = Get<Dictionary<string, int>>(result, "PartDropReasons");
            Assert.That(drops.GetValueOrDefault(
                    $"embedded material type {type} is not verified (only weighted and default_type are)"),
                Is.GreaterThan(0));
            Assert.That(Get<int>(result, "Built"), Is.Zero, SrSkips(result));
        }

        [TestCase(ModelMaterialEnum.weighted, VertexFormat.Position16_bit)]
        [TestCase(ModelMaterialEnum.default_type, VertexFormat.CustomTerrain2)]
        public void SharedRepack_DirectRigid_UnverifiedVertexFormatIsRejected(
            ModelMaterialEnum type,
            VertexFormat format)
        {
            var world = BuildDirectEmbeddedSrWorld(withWsModel: false, type, format);

            var result = world.Run(minimumSaving: 1, minimumGroupSaving: 1);

            var drops = Get<Dictionary<string, int>>(result, "PartDropReasons");
            Assert.That(drops.GetValueOrDefault(
                    $"embedded vertex format {format} is not verified (only Static, Weighted and Cinematic are)"),
                Is.GreaterThan(0));
            Assert.That(Get<int>(result, "Built"), Is.Zero, SrSkips(result));
        }

        [Test]
        public void SharedRepack_DirectRigidKillSwitch_KeepsThePriorOutput()
        {
            var enabledWorld = BuildDirectEmbeddedSrWorld(withWsModel: false);
            enabledWorld.Analyse();
            var enabledCoverage = Get<object>(enabledWorld.State, "VanillaTextureCoverage");
            Assert.That(Get<IDictionary>(enabledCoverage, "EmbeddedMaterialsByPart"), Is.Not.Empty);
            Assert.That(Get<IDictionary>(enabledCoverage, "DirectConsumersByRigid"), Is.Not.Empty);
            var enabledSets = Get<IEnumerable>(Get<object>(enabledWorld.State, "UnitTextureRepack"), "SetsC")
                .Cast<object>().Count();

            var legacyWorld = BuildDirectEmbeddedSrWorld(withWsModel: false);
            legacyWorld.Analyse(includeDirectConsumers: false);
            var legacySets = Get<IEnumerable>(Get<object>(legacyWorld.State, "UnitTextureRepack"), "SetsC")
                .Cast<object>().Count();
            Assert.That(enabledSets, Is.GreaterThan(legacySets),
                "the fixture must prove direct embedded entries change the signature sets");
            legacyWorld.Run(minimumSaving: 1, minimumGroupSaving: 1, analyse: false);
            var expected = SrSnapshot(legacyWorld.Fixture.Output);

            var world = BuildDirectEmbeddedSrWorld(withWsModel: false);
            var disabled = GetServiceType().GetField(
                "SharedRepackDirectRigidsDisabled", BindingFlags.NonPublic | BindingFlags.Static)!;
            var old = (bool)disabled.GetValue(null)!;
            try
            {
                disabled.SetValue(null, true);
                var result = world.Run(minimumSaving: 1, minimumGroupSaving: 1);
                Assert.That(Get<int>(result, "Built"), Is.Zero);
                Assert.That(BuildSharedRepackSection(Enabled(world)), Does.Not.Contain("Direct rigids considered"));
                var coverage = Get<object>(world.State, "VanillaTextureCoverage");
                Assert.That(Get<IDictionary>(coverage, "EmbeddedMaterialsByPart"), Is.Empty);
                Assert.That(Get<IDictionary>(coverage, "DirectConsumersByRigid"), Is.Empty);
                var units = Get<object>(world.State, "UnitTextureRepack");
                Assert.That(Get<IEnumerable>(units, "SetsC").Cast<object>().Count(), Is.EqualTo(legacySets));
                Assert.That(Get<IEnumerable>(units, "Units").Cast<object>()
                    .All(unit => Get<HashSet<string>>(unit, "DirectRigids").Count == 0), Is.True);
                Assert.That(world.Fixture.Output.Keys, Is.EquivalentTo(expected.Keys));
                foreach (var (path, bytes) in expected)
                    Assert.That(world.Fixture.Output[path], Is.EqualTo(bytes), path);
            }
            finally
            {
                disabled.SetValue(null, old);
            }
        }

        private static Dictionary<string, byte[]> SrSnapshot(Dictionary<string, byte[]> files)
            => files.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);

        [Test]
        public void SharedRepack_EndToEnd_EveryTriangleSamplesTheSameColours()
        {
            var world = BuildStandardSrWorld();
            var sourceBefore = SrSnapshot(world.Fixture.Source);
            var vanillaBefore = SrSnapshot(world.Fixture.Vanilla);

            var result = world.Run();

            Assert.That(Get<string>(result, "UnavailableReason"), Is.Null);
            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            AssertFunnelReconciles(result);
            var built = SrBuilt(result).Single();
            Assert.That(Get<string>(built, "Rigid"), Is.EqualTo(SrRigid));
            Assert.That(Get<int>(built, "Tuples"), Is.EqualTo(2));
            Assert.That(Get<int>(built, "Parts"), Is.EqualTo(4));
            var factory = BuildStandardSrWorld().Rigids[SrRigid];

            // Pixels, per part and slot.
            var (samples, max) = AssertSrPixels(world, SrRigid, SrWs, factory, SrPart0Seeds, (lod, part) => part == 0);
            Assert.That(samples, Is.GreaterThan(0));
            AssertSrPixels(world, SrRigid, SrWs, factory, SrPart1Seeds, (lod, part) => part == 1);
            Assert.That(max, Is.LessThanOrEqualTo(12));

            // Output textures are smaller than what they replace.
            Assert.That(Get<long>(built, "After"), Is.LessThan(Get<long>(built, "Before") / 10));
            var outputs = Get<IEnumerable>(built, "Outputs").Cast<object>().ToList();
            Assert.That(outputs, Has.Count.EqualTo(2));
            Assert.That(world.Fixture.Output.Keys.Count(key => key.Contains("_repack_")), Is.EqualTo(4));

            // Rewiring: both WSModel LODs point at generated materials; the originals stay.
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Does.Contain("_repack_"));
            Assert.That(SrWsMaterial(world.State, SrWs, 1, 0), Is.EqualTo(SrWsMaterial(world.State, SrWs, 0, 0)));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Is.Not.EqualTo(SrWsMaterial(world.State, SrWs, 0, 1)));
            Assert.That(
                Get<HashSet<string>>(world.State, "ModifiedWsModels"),
                Is.EquivalentTo(new[] { SrWs }));
            Assert.That(
                Get<HashSet<string>>(world.State, "ModifiedRigids"),
                Is.EquivalentTo(new[] { SrRigid }));
            Assert.That(
                Encoding.UTF8.GetString(world.Fixture.Output[SrMaterialPath("m1")]),
                Does.Contain("sr_b1.dds"));
            var generated = Encoding.UTF8.GetString(world.Fixture.Output[SrWsMaterial(world.State, SrWs, 0, 0)]);
            Assert.That(generated, Does.Not.Contain("sr_b1.dds"));
            Assert.That(generated, Does.Contain("<shader>shaders\\weighted2_character.xml.shader</shader>"));

            // Sources untouched, validation passes.
            foreach (var (path, bytes) in sourceBefore)
                Assert.That(world.Fixture.Source[path], Is.EqualTo(bytes), path);
            foreach (var (path, bytes) in vanillaBefore)
                Assert.That(world.Fixture.Vanilla[path], Is.EqualTo(bytes), path);
            Assert.That(SrValidate(world), Is.Empty);
            Assert.That(Get<IEnumerable>(result, "ValidationErrors").Cast<object>(), Is.Empty);
        }

        // Charts a row packer cannot fit on 512x512 (it needs 1024x512) but MaxRects can: a run of four
        // blocks across, three runs down and a lone block.
        private static SrWorld BuildMaxRectsWinsSrWorld()
        {
            var world = new SrWorld();
            world.AddTexture("textures\\sr_b1.dds", 1024, "BC1", true, 1);
            world.AddTexture("textures\\sr_n1.dds", 512, "BC3", false, 2);
            world.Fixture.AddMaterial(SrMaterialPath("m1"), (SrBase, "textures\\sr_b1.dds"), (SrNormal, "textures\\sr_n1.dds"));
            var m1 = SrMaterialPath("m1");
            var lod0 = new[] { (m1, SrBlocks((9, 6), (10, 6), (11, 6), (12, 6), (14, 0), (5, 1), (5, 2), (5, 3))) };
            world.AddModel(SrRigid, SrWs, SrRoot, SrRigidFactory(lod0), [[m1]]);
            return world;
        }

        [Test]
        public void SharedRepack_EndToEnd_WhereMaxRectsWins_BuildsValidatesAndSamplesTheSameColours()
        {
            var world = BuildMaxRectsWinsSrWorld();

            var result = world.Run(minimumSaving: 1, minimumGroupSaving: 1);

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            Assert.That(Get<int>(result, "FailedPixelCheck"), Is.Zero);
            var built = SrBuilt(result).Single();
            var group = Get<IEnumerable>(built, "Groups").Cast<object>().Single();
            // The shelf packer needs 1024x512 for these charts.
            Assert.That(Get<string>(group, "Sizes"), Is.EqualTo("base_colour 1024x1024 -> 512x512; normal 512x512 -> 256x256"));
            var (samples, max) = AssertSrPixels(
                world, SrRigid, SrWs, BuildMaxRectsWinsSrWorld().Rigids[SrRigid], new Dictionary<string, int> { [SrBase] = 1, [SrNormal] = 2 });
            Assert.That(samples, Is.GreaterThan(0));
            Assert.That(max, Is.LessThanOrEqualTo(12));
            Assert.That(SrValidate(world), Is.Empty);
            Assert.That(Get<IEnumerable>(result, "ValidationErrors").Cast<object>(), Is.Empty);
        }

        [Test]
        public void SharedRepack_EndToEnd_WhereMaxRectsWins_IsDeterministic()
        {
            var first = BuildMaxRectsWinsSrWorld();
            first.Run(minimumSaving: 1, minimumGroupSaving: 1);
            var second = BuildMaxRectsWinsSrWorld();
            second.Run(minimumSaving: 1, minimumGroupSaving: 1);

            Assert.That(second.Fixture.Output.Keys.OrderBy(key => key), Is.EqualTo(first.Fixture.Output.Keys.OrderBy(key => key)));
            foreach (var (path, bytes) in first.Fixture.Output)
                Assert.That(second.Fixture.Output[path], Is.EqualTo(bytes), path);
        }

        [Test]
        public void SharedRepack_SlotListedTwiceWithTheSameSource_BothEntriesAreRewritten()
        {
            var world = BuildStandardSrWorld();
            world.Fixture.AddMaterial(
                SrMaterialPath("m1"),
                (SrBase, "textures\\sr_b1.dds"),
                (SrNormal, "textures\\sr_n1.dds"),
                (SrNormal, "textures\\sr_n1.dds"));

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            var generated = Encoding.UTF8.GetString(world.Fixture.Output[SrWsMaterial(world.State, SrWs, 0, 0)]);
            Assert.That(generated, Does.Not.Contain("sr_n1.dds"));
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(generated);
            var normals = doc.SelectNodes("/material/textures/texture[slot='t_xml_normal']/source")!
                .Cast<System.Xml.XmlNode>().Select(node => node.InnerText).ToList();
            Assert.That(normals, Has.Count.EqualTo(2));
            Assert.That(normals[0], Does.Contain("_repack_"));
            Assert.That(normals[1], Is.EqualTo(normals[0]));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_SlotListedTwiceWithDifferentSources_IsRefused()
        {
            var world = BuildStandardSrWorld();
            world.Fixture.AddMaterial(
                SrMaterialPath("m1"),
                (SrBase, "textures\\sr_b1.dds"),
                (SrNormal, "textures\\sr_n1.dds"),
                (SrNormal, "textures\\sr_n2.dds"));

            // Without copies the dropped part takes the shared n2 (and so the other part) with it.
            object result = null!;
            WithCopyRemovedDisabled(() => result = world.Run());

            Assert.That(Get<int>(result, "Built"), Is.Zero, SrSkips(result));
            Assert.That(SrSkips(result), Does.Contain("no part is left after closing the set"));
            Assert.That(world.Fixture.Output.Keys, Has.None.Contain("_repack_"));
        }

        [Test]
        public void SharedRepack_RegionsSharedByTwoLods_AreStoredOnce()
        {
            var twoLods = BuildStandardSrWorld();
            var oneLod = BuildStandardSrWorld(secondLod: false);

            var resultTwo = twoLods.Run();
            var resultOne = oneLod.Run();

            var builtTwo = SrBuilt(resultTwo).Single();
            var builtOne = SrBuilt(resultOne).Single();
            // LOD 1 only repeats regions of LOD 0: same charts, same layout, same bytes.
            Assert.That(Get<int>(builtTwo, "Charts"), Is.EqualTo(Get<int>(builtOne, "Charts")));
            Assert.That(Get<int>(builtTwo, "Charts"), Is.EqualTo(2));
            Assert.That(Get<long>(builtTwo, "After"), Is.EqualTo(Get<long>(builtOne, "After")));
            Assert.That(Get<int>(builtTwo, "LayoutWidth"), Is.EqualTo(Get<int>(builtOne, "LayoutWidth")));
            Assert.That(Get<int>(builtTwo, "Parts"), Is.EqualTo(4));
            Assert.That(Get<int>(builtOne, "Parts"), Is.EqualTo(2));
        }

        [Test]
        public void SharedRepack_TileOffsetUvs_AreHandledModuloOne()
        {
            var world = new SrWorld();
            world.AddTexture("textures\\sr_b1.dds", 1024, "BC1", true, 1);
            world.AddTexture("textures\\sr_n1.dds", 512, "BC3", false, 2);
            world.Fixture.AddMaterial(SrMaterialPath("m1"), (SrBase, "textures\\sr_b1.dds"), (SrNormal, "textures\\sr_n1.dds"));
            var m1 = SrMaterialPath("m1");
            world.AddModel(
                SrRigid, SrWs, SrRoot,
                SrRigidFactory([(m1, SrBlock(5, 6, 2, -1).Concat(SrBlock(6, 6, 2, -1)).ToArray())]),
                [[m1]]);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            AssertSrPixels(world, SrRigid, SrWs,
                SrRigidFactory([(m1, SrBlock(5, 6, 2, -1).Concat(SrBlock(6, 6, 2, -1)).ToArray())]),
                SrPart0Seeds);
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_TuplesWithDifferentSlotRatios_LandInSeparateLayoutGroups()
        {
            var world = BuildStandardSrWorld(secondLod: false);
            // Tuple 3: a 512 base and a 512 normal (the normal is 1:1 with its base, the other
            // tuples have it at half), and tuple 4: a base only.
            world.AddTexture("textures\\sr_b3.dds", 512, "BC1", true, 5);
            world.AddTexture("textures\\sr_n3.dds", 512, "BC3", false, 6);
            world.AddTexture("textures\\sr_b4.dds", 1024, "BC1", true, 7);
            world.Fixture.AddMaterial(SrMaterialPath("m3"), (SrBase, "textures\\sr_b3.dds"), (SrNormal, "textures\\sr_n3.dds"));
            world.Fixture.AddMaterial(SrMaterialPath("m4"), (SrBase, "textures\\sr_b4.dds"));
            var m1 = SrMaterialPath("m1");
            var m2 = SrMaterialPath("m2");
            var m3 = SrMaterialPath("m3");
            var m4 = SrMaterialPath("m4");
            var factory = SrRigidFactory([
                (m1, SrBlocks((2, 3), (3, 3))), (m2, SrBlocks((10, 4))),
                (m3, SrBlocks((7, 7), (8, 7))), (m4, SrBlocks((12, 12)))]);
            world.AddModel(SrRigid, SrWs, SrRoot, factory, [[m1, m2, m3, m4]]);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            var built = SrBuilt(result).Single();
            AssertSrPixels(world, SrRigid, SrWs, factory, SrPart0Seeds, (_, part) => part == 0);
            AssertSrPixels(world, SrRigid, SrWs, factory, SrPart1Seeds, (_, part) => part == 1);
            AssertSrPixels(world, SrRigid, SrWs, factory,
                new Dictionary<string, int> { [SrBase] = 5, [SrNormal] = 6 }, (_, part) => part == 2);
            AssertSrPixels(world, SrRigid, SrWs, factory,
                new Dictionary<string, int> { [SrBase] = 7 }, (_, part) => part == 3);

            // Three layout groups (ratios 1:0.5, 1:1, base only), each with its own textures,
            // none resampled.
            var groups = Get<IEnumerable>(built, "Groups").Cast<object>().ToList();
            Assert.That(groups, Has.Count.EqualTo(3));
            var outputs = Get<IEnumerable>(built, "Outputs").Cast<object>().ToList();
            Assert.That(outputs, Has.Count.EqualTo(5));
            Assert.That(outputs.Select(output => Get<string>(output, "Path")).Distinct().Count(), Is.EqualTo(5));
            Assert.That(Get<long>(built, "UpsampleOverheadBytes"), Is.Zero);
            var group1Normal = SrGeneratedTexture(world, SrWsMaterial(world.State, SrWs, 0, 0), SrNormal);
            var group2Normal = SrGeneratedTexture(world, SrWsMaterial(world.State, SrWs, 0, 2), SrNormal);
            Assert.That(
                group1Normal.Width,
                Is.EqualTo(SrGeneratedTexture(world, SrWsMaterial(world.State, SrWs, 0, 0), SrBase).Width / 2));
            // A block is 32 normal pixels in both groups: nobody is inflated to the finest ratio.
            Assert.That(RunLengthAround(world, 0, group1Normal.Bgra, group1Normal.Width, group1Normal.Height), Is.EqualTo(32));
            Assert.That(RunLengthAround(world, 2, group2Normal.Bgra, group2Normal.Width, group2Normal.Height), Is.EqualTo(32));

            // Tuple 4 has no normal at all.
            var m4Generated = Encoding.UTF8.GetString(world.Fixture.Output[SrWsMaterial(world.State, SrWs, 0, 3)]);
            Assert.That(m4Generated, Does.Not.Contain(SrNormal));
            Assert.That(SrValidate(world), Is.Empty);
        }

        // --- Duplicate pages of layout groups on the same geometry ---

        // Two parts with the same UV blocks; the second material's base colour is BC3, so its tuple lands in
        // another layout group, and both share one normal texture. With different blocks the charts differ.
        private static SrWorld BuildSharedNormalSrWorld(bool sameGeometry, bool withDissolve = false)
        {
            var world = new SrWorld();
            world.AddTexture("textures\\sr_b1.dds", 1024, "BC1", true, 1);
            world.AddTexture("textures\\sr_n1.dds", 512, "BC3", false, 2);
            world.AddTexture("textures\\sr_b2.dds", 1024, "BC3", true, 3);
            var m1 = SrMaterialPath("m1");
            var m2 = SrMaterialPath("m2");
            world.Fixture.AddMaterial(m1, (SrBase, "textures\\sr_b1.dds"), (SrNormal, "textures\\sr_n1.dds"));
            if (withDissolve)
            {
                // Same textures as m1 plus the shared dissolve map copy: another slot set, so another group.
                world.AddTexture(SrDissolveMapTexture, 512, "BC1", true, 9);
                world.AddTexture(SrDissolveDetailTexture, 1024, "BC1", true, 10);
                world.Fixture.AddText(m2, SrDissolveMaterialXml(SrDissolveShader));
            }
            else
            {
                world.Fixture.AddMaterial(m2, (SrBase, "textures\\sr_b2.dds"), (SrNormal, "textures\\sr_n1.dds"));
            }

            var factory = SrRigidFactory([
                (m1, SrBlocks((2, 3), (3, 3))),
                (m2, sameGeometry ? SrBlocks((2, 3), (3, 3)) : SrBlocks((10, 4)))]);
            world.AddModel(SrRigid, SrWs, SrRoot, factory, [[m1, m2]]);
            return world;
        }

        private static string SrMaterialTexture(SrWorld world, int part, string slot)
            => SrSlotNode(
                    Encoding.UTF8.GetString(world.Fixture.Output[SrWsMaterial(world.State, SrWs, 0, part)]), slot)
                .SelectSingleNode("source")?.InnerText.Trim() ?? string.Empty;

        [Test]
        public void SharedRepack_GroupsOnTheSameGeometry_WriteTheSharedSlotsOnce()
        {
            var world = BuildSharedNormalSrWorld(sameGeometry: true);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            var built = SrBuilt(result).Single();
            Assert.That(Get<IEnumerable>(built, "Groups").Cast<object>().Count(), Is.EqualTo(2));
            // Base colour of each group, and one normal for both.
            var outputs = Get<IEnumerable>(built, "Outputs").Cast<object>().ToList();
            Assert.That(outputs, Has.Count.EqualTo(3));
            var normal = outputs.Single(output => Get<string>(output, "Slot") == SrNormal);
            Assert.That(Get<int>(built, "DeduplicatedOutputs"), Is.EqualTo(1));
            Assert.That(Get<long>(built, "DeduplicatedBytes"), Is.EqualTo(Get<long>(normal, "Bytes")));
            Assert.That(Get<int>(result, "DeduplicatedOutputs"), Is.EqualTo(1));
            Assert.That(world.Fixture.Output.Keys.Count(path => path.Contains("_repack_") && path.EndsWith("_normal.dds")), Is.EqualTo(1));
            Assert.That(Get<List<string>>(result, "GeneratedTextures"), Has.Count.EqualTo(3));

            // Both materials name the one normal; the base colours stay apart.
            Assert.That(SrMaterialTexture(world, 0, SrNormal), Is.EqualTo(Get<string>(normal, "Path")));
            Assert.That(SrMaterialTexture(world, 1, SrNormal), Is.EqualTo(Get<string>(normal, "Path")));
            Assert.That(SrMaterialTexture(world, 0, SrBase), Is.Not.EqualTo(SrMaterialTexture(world, 1, SrBase)));

            // The gate and the set count the shared page once.
            Assert.That(Get<long>(built, "After"), Is.EqualTo(outputs.Sum(output => Get<long>(output, "Bytes"))));
            Assert.That(
                Get<IEnumerable>(built, "Groups").Cast<object>().Sum(group => Get<long>(group, "After")),
                Is.EqualTo(Get<long>(built, "After")));
            Assert.That(BuildSharedRepackSection(Enabled(world)), Does.Contain("deduplicated 1 page(s), " +
                Get<long>(normal, "Bytes").ToString("N0")));

            var factory = SrRigidFactory([
                (SrMaterialPath("m1"), SrBlocks((2, 3), (3, 3))), (SrMaterialPath("m2"), SrBlocks((2, 3), (3, 3)))]);
            AssertSrPixels(world, SrRigid, SrWs, factory, new Dictionary<string, int> { [SrBase] = 1, [SrNormal] = 2 }, (_, part) => part == 0);
            AssertSrPixels(world, SrRigid, SrWs, factory, new Dictionary<string, int> { [SrBase] = 3, [SrNormal] = 2 }, (_, part) => part == 1);
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_TupleWithACopySlot_SharesTheOtherPagesWithThePlainTuple()
        {
            var world = BuildSharedNormalSrWorld(sameGeometry: true, withDissolve: true);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result) + SrPartDropReasons(result));
            var built = SrBuilt(result).Single();
            Assert.That(Get<IEnumerable>(built, "Groups").Cast<object>().Count(), Is.EqualTo(2));
            // base, normal and the dissolve map copy: the plain tuple's base and normal are the same pages.
            var outputs = Get<IEnumerable>(built, "Outputs").Cast<object>().ToList();
            Assert.That(outputs.Select(output => Get<string>(output, "Slot")),
                Is.EquivalentTo(new[] { SrBase, SrNormal, SrDissolveMap }));
            Assert.That(Get<int>(built, "DeduplicatedOutputs"), Is.EqualTo(2));
            Assert.That(SrMaterialTexture(world, 0, SrBase), Is.EqualTo(SrMaterialTexture(world, 1, SrBase)));
            Assert.That(SrMaterialTexture(world, 0, SrNormal), Is.EqualTo(SrMaterialTexture(world, 1, SrNormal)));
            Assert.That(SrMaterialTexture(world, 1, SrDissolveMap), Does.EndWith("_repack_dissolve_map.dds"));
            Assert.That(Get<long>(built, "After"), Is.EqualTo(outputs.Sum(output => Get<long>(output, "Bytes"))));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_GroupsOnDifferentGeometry_AreNotDeduplicated()
        {
            var world = BuildSharedNormalSrWorld(sameGeometry: false);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            var built = SrBuilt(result).Single();
            Assert.That(Get<IEnumerable>(built, "Groups").Cast<object>().Count(), Is.EqualTo(2));
            Assert.That(Get<IEnumerable>(built, "Outputs").Cast<object>().Count(), Is.EqualTo(4));
            Assert.That(Get<int>(built, "DeduplicatedOutputs"), Is.Zero);
            Assert.That(SrMaterialTexture(world, 0, SrNormal), Is.Not.EqualTo(SrMaterialTexture(world, 1, SrNormal)));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_DuplicateThatEncodesDifferently_FailsTheSetAndWritesNothing()
        {
            var world = BuildSharedNormalSrWorld(sameGeometry: true);

            object result = null!;
            WithBeforeCheckHook(
                plan =>
                {
                    var duplicate = HookOutputs(plan).First(output => Get<object?>(output, "DuplicateOf") != null);
                    var data = Get<byte[]>(duplicate, "Data");
                    data[^1] ^= 0x55;
                },
                () => result = world.Run());

            Assert.That(Get<int>(result, "Built"), Is.Zero);
            AssertFunnelReconciles(result);
            Assert.That(SrSkips(result), Does.Contain("encoded differently").And.Contain("signature is wrong"));
            Assert.That(world.Fixture.Output.Keys, Has.None.Contain("_repack_"));
            Assert.That(Get<HashSet<string>>(world.State, "ModifiedRigids"), Is.Empty);
            Assert.That(SrValidate(world), Is.Empty);
        }

        // Pixels between the colour changes along the row through the first triangle's centroid.
        private static int RunLengthAround(SrWorld world, int part, byte[] bgra, int width, int height)
        {
            var rigid = (RmvFile)Get<IDictionary>(world.State, "RigidModels")[SrRigid]!;
            var mesh = rigid.ModelList[0][part].Mesh;
            double u = 0, v = 0;
            for (var corner = 0; corner < 3; corner++)
            {
                u += mesh.VertexList[mesh.IndexList[corner]].Uv.X / 3.0;
                v += mesh.VertexList[mesh.IndexList[corner]].Uv.Y / 3.0;
            }

            var y = (int)(v * height);
            var x = (int)(u * width);
            bool Same(int a, int b)
                => Math.Abs(bgra[(y * width + a) * 4] - bgra[(y * width + b) * 4]) < 24 &&
                   Math.Abs(bgra[(y * width + a) * 4 + 1] - bgra[(y * width + b) * 4 + 1]) < 24;
            var left = x;
            while (left > 0 && Same(left - 1, x))
                left--;
            var right = x;
            while (right < width - 1 && Same(right + 1, x))
                right++;
            return right - left + 1;
        }

        // The standard rigid (m1 + m2) and a second mod-owned rigid of the same unit using both
        // materials on other regions of the same textures.
        private static (SrWorld World, Func<RmvFile> Second) BuildTwoRigidSrWorld()
        {
            var world = BuildStandardSrWorld();
            var m1 = SrMaterialPath("m1");
            var m2 = SrMaterialPath("m2");
            var second = SrRigidFactory([(m1, SrBlocks((9, 9))), (m2, SrBlocks((5, 5)))]);
            world.AddModel(SrRigid2, SrWs2, SrRoot, second, [[m1, m2]]);
            return (world, second);
        }

        private static List<string> SrBuiltRigids(object built)
            => Get<IEnumerable>(built, "Rigids").Cast<object>()
                .Select(item => item.ToString()!)
                .ToList();

        [Test]
        public void SharedRepack_SecondRigidUsingTheTexture_SetIsBuiltAcrossBothRigids()
        {
            var (world, _) = BuildTwoRigidSrWorld();

            var result = world.Run();

            Assert.That(Get<int>(result, "SetsConsidered"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            AssertFunnelReconciles(result);
            var built = SrBuilt(result).Single();
            Assert.That(Get<string>(built, "Rigid"), Is.EqualTo(SrRigid));
            Assert.That(SrBuiltRigids(built), Is.EqualTo(new[] { $"({SrRigid}, 4)", $"({SrRigid2}, 2)" }));
            Assert.That(Get<int>(built, "Parts"), Is.EqualTo(6));
            Assert.That(Get<int>(built, "Tuples"), Is.EqualTo(2));
            Assert.That(
                Get<HashSet<string>>(world.State, "ModifiedRigids"),
                Is.EquivalentTo(new[] { SrRigid, SrRigid2 }));
            Assert.That(
                Get<HashSet<string>>(world.State, "ModifiedWsModels"),
                Is.EquivalentTo(new[] { SrWs, SrWs2 }));
            Assert.That(Get<HashSet<string>>(result, "RepackedRigids"), Is.EquivalentTo(new[] { SrRigid, SrRigid2 }));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_TwoRigidsSharingTextureTuples_UseTheSameGeneratedTextures_AndSampleTheSameColours()
        {
            var (world, second) = BuildTwoRigidSrWorld();
            var first = BuildStandardSrWorld().Rigids[SrRigid];

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            // One generated texture per slot and layout group serves both rigids.
            Assert.That(world.Fixture.Output.Keys.Count(key => key.Contains("_repack_") && key.EndsWith(".dds")), Is.EqualTo(2));
            Assert.That(Get<List<string>>(result, "GeneratedTextures"), Has.Count.EqualTo(2));
            Assert.That(Get<List<string>>(result, "GeneratedMaterials"), Has.Count.EqualTo(2));
            for (var part = 0; part < 2; part++)
            {
                Assert.That(
                    SrWsMaterial(world.State, SrWs2, 0, part),
                    Is.EqualTo(SrWsMaterial(world.State, SrWs, 0, part)));
                Assert.That(SrWsMaterial(world.State, SrWs2, 0, part), Does.Contain("_repack_"));
            }

            AssertSrPixels(world, SrRigid, SrWs, first, SrPart0Seeds, (_, part) => part == 0);
            AssertSrPixels(world, SrRigid, SrWs, first, SrPart1Seeds, (_, part) => part == 1);
            var (samples, max) = AssertSrPixels(world, SrRigid2, SrWs2, second, SrPart0Seeds, (_, part) => part == 0);
            AssertSrPixels(world, SrRigid2, SrWs2, second, SrPart1Seeds, (_, part) => part == 1);
            Assert.That(samples, Is.GreaterThan(0));
            Assert.That(max, Is.LessThanOrEqualTo(12));
            // The second rigid's regions are charts of the same tuples: four charts in all.
            Assert.That(Get<int>(SrBuilt(result).Single(), "Charts"), Is.EqualTo(4));
            Assert.That(SrValidate(world), Is.Empty);
            Assert.That(Get<IEnumerable>(result, "ValidationErrors").Cast<object>(), Is.Empty);
        }

        [Test]
        public void SharedRepack_MaterialUsedByTwoRigids_ProducesExactlyOneGeneratedMaterial()
        {
            var world = BuildStandardSrWorld(secondLod: false);
            var m1 = SrMaterialPath("m1");
            var m2 = SrMaterialPath("m2");
            // The same region as rigid 1's part 0, so both parts sit on one page.
            world.AddModel(SrRigid2, SrWs2, SrRoot, SrRigidFactory([(m1, SrBlocks((2, 3)))]), [[m1]]);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            var materials = Get<List<string>>(result, "GeneratedMaterials");
            Assert.That(materials, Has.Count.EqualTo(2));
            Assert.That(materials.Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(2));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Is.EqualTo(SrWsMaterial(world.State, SrWs2, 0, 0)));
            Assert.That(materials, Does.Contain(SrWsMaterial(world.State, SrWs2, 0, 0)));
            Assert.That(
                world.Fixture.Output.Keys.Count(key => key.Contains("_repack_") && key.EndsWith(".xml.material")),
                Is.EqualTo(2));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [TestCase(true, TestName = "SharedRepack_OneRigidVanillaOwned_ItsTexturesLeaveTheSet_AndTheRestBuilds")]
        [TestCase(false, TestName = "SharedRepack_OneRigidReferencedByAnotherFile_ItsTexturesLeaveTheSet_AndTheRestBuilds")]
        public void SharedRepack_OneIneligibleRigid_ItsTexturesLeaveTheSet_AndTheRestBuilds(bool vanilla)
        {
            var world = BuildStandardSrWorld(secondLod: false);
            var m1 = SrMaterialPath("m1");
            // The second rigid uses only b1 + n1, which part 0 of the first rigid uses too.
            world.AddModel(
                SrRigid2, SrWs2, SrRoot, SrRigidFactory([(m1, SrBlocks((9, 9)))]), [[m1]], ownedRigid: !vanilla);
            if (!vanilla)
                world.Fixture.AddText("variantmeshes\\notes.txt", "uses " + SrRigid2);
            var first = BuildStandardSrWorld(false).Rigids[SrRigid];

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            AssertFunnelReconciles(result);
            var built = SrBuilt(result).Single();
            Assert.That(SrBuiltRigids(built), Is.EqualTo(new[] { $"({SrRigid}, 1)" }));
            var replaced = string.Concat(Get<IEnumerable>(built, "Replaced").Cast<object>());
            Assert.That(replaced, Does.Contain("sr_b2").And.Contain("sr_n2"));
            Assert.That(replaced, Does.Not.Contain("sr_b1").And.Not.Contain("sr_n1"));
            // The texture both rigids use is not repacked: part 0 and the second rigid are untouched.
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Is.EqualTo(m1));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 1), Does.Contain("_repack_"));
            AssertSrPixels(world, SrRigid, SrWs, first, SrPart1Seeds, (_, part) => part == 1);
            var rigid = (RmvFile)Get<IDictionary>(world.State, "RigidModels")[SrRigid]!;
            Assert.That(
                rigid.ModelList[0][0].Mesh.VertexList.Select(vertex => vertex.Uv),
                Is.EqualTo(first().ModelList[0][0].Mesh.VertexList.Select(vertex => vertex.Uv)));
            Assert.That(Get<HashSet<string>>(world.State, "ModifiedRigids"), Is.EquivalentTo(new[] { SrRigid }));
            Assert.That(Get<HashSet<string>>(world.State, "ModifiedWsModels"), Is.EquivalentTo(new[] { SrWs }));
            var report = BuildSharedRepackSection(Enabled(world));
            Assert.That(report, Does.Contain($"rigid {SrRigid2} is not eligible ("));
            Assert.That(report, Does.Contain("the part's rigid is not eligible: "));
            Assert.That(report, Does.Contain(vanilla ? "not owned by the source pack" : "notes.txt"));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_EveryRigidOfTheSetIneligible_IsCountedUnderRigidNotEligible()
        {
            var world = BuildStandardSrWorld();
            var m1 = SrMaterialPath("m1");
            world.Fixture.Vanilla[SrRigid] = [0];
            world.AddModel(SrRigid2, SrWs2, SrRoot, SrRigidFactory([(m1, SrBlocks((9, 9)))]), [[m1]], ownedRigid: false);

            var result = world.Run();

            Assert.That(Get<int>(result, "RigidNotEligible"), Is.EqualTo(1), SrSkips(result));
            Assert.That(Get<int>(result, "Built"), Is.Zero);
            AssertFunnelReconciles(result);
            Assert.That(SrSkips(result), Does.Contain($"{SrRigid}: the rigid overrides a vanilla path"));
            Assert.That(SrSkips(result), Does.Contain($"{SrRigid2}: the rigid is not owned by the source pack"));
        }

        [Test]
        public void SharedRepack_FailureInOneRigid_WritesNothingForEitherRigid()
        {
            var (world, second) = BuildTwoRigidSrWorld();
            var first = BuildStandardSrWorld().Rigids[SrRigid];
            var sourceBefore = SrSnapshot(world.Fixture.Source);

            object result = null!;
            WithBeforeCheckHook(
                plan =>
                {
                    // The chart of the second rigid's region: shift it so its pixels are wrong.
                    var charts = Get<IEnumerable>(Get<IEnumerable>(plan, "Pages").Cast<object>().First(), "Charts")
                        .Cast<object>().ToList();
                    var property = charts[^1].GetType().GetProperty("PackX", AnyInstance)!;
                    property.SetValue(charts[^1], (int)property.GetValue(charts[^1])! + 16);
                },
                () => result = world.Run());

            Assert.That(Get<int>(result, "FailedPixelCheck"), Is.EqualTo(1), SrSkips(result));
            Assert.That(Get<int>(result, "Built"), Is.Zero);
            AssertFunnelReconciles(result);
            Assert.That(world.Fixture.Output.Keys, Has.None.Contain("_repack_"));
            Assert.That(Get<HashSet<string>>(world.State, "ModifiedRigids"), Is.Empty);
            Assert.That(Get<HashSet<string>>(world.State, "ModifiedWsModels"), Is.Empty);
            Assert.That(Get<HashSet<string>>(result, "RepackedRigids"), Is.Empty);
            Assert.That(Get<IDictionary>(world.State, "RigidModels").Contains(SrRigid), Is.False);
            Assert.That(Get<HashSet<string>>(world.State, "SharedRepackTexturePaths"), Is.Empty);
            Assert.That(Get<HashSet<string>>(world.State, "SharedRepackMaterialPaths"), Is.Empty);
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Is.EqualTo(SrMaterialPath("m1")));
            Assert.That(SrWsMaterial(world.State, SrWs2, 0, 1), Is.EqualTo(SrMaterialPath("m2")));
            Assert.That(Get<IEnumerable>(result, "ValidationErrors").Cast<object>(), Is.Empty);
            Assert.That(SrValidate(world), Is.Empty);
            foreach (var (path, bytes) in sourceBefore)
                Assert.That(world.Fixture.Source[path], Is.EqualTo(bytes), path);
            Assert.That(first().ModelList[0].Length, Is.EqualTo(2));
            Assert.That(second().ModelList[0].Length, Is.EqualTo(2));
        }

        [Test]
        public void SharedRepack_ExceptionWhileCheckingASet_WritesNothingAndSkipsTheSet()
        {
            var (world, _) = BuildTwoRigidSrWorld();

            object result = null!;
            WithBeforeCheckHook(_ => throw new InvalidOperationException("boom"), () => result = world.Run());

            Assert.That(Get<int>(result, "NotBuildable"), Is.EqualTo(1), SrSkips(result));
            Assert.That(Get<int>(result, "Built"), Is.Zero);
            AssertFunnelReconciles(result);
            Assert.That(SrSkips(result), Does.Contain("boom"));
            Assert.That(world.Fixture.Output.Keys, Has.None.Contain("_repack_"));
            Assert.That(Get<HashSet<string>>(world.State, "ModifiedRigids"), Is.Empty);
            Assert.That(Get<HashSet<string>>(world.State, "ModifiedWsModels"), Is.Empty);
            Assert.That(SrWsMaterial(world.State, SrWs2, 0, 0), Is.EqualTo(SrMaterialPath("m1")));
        }

        // Rigid 1 uses m1 (b1 + n1) and m2 (b2 + n2). Unit 2 uses m1 in rigid 2, so {b1, n1} is
        // a set of its own spanning two rigids; with unit 3 using m2 in rigid 3 so is {b2, n2}.
        // Without rigid 3, {b2, n2} is a single-rigid set. Either way rigid 1 is rewritten twice.
        [TestCase(true, TestName = "SharedRepack_RigidRewrittenByTwoMultiRigidSets_BuildsBothWithDisjointParts")]
        [TestCase(false, TestName = "SharedRepack_RigidRewrittenByASingleAndAMultiRigidSet_BuildsBothWithDisjointParts")]
        public void SharedRepack_RigidInTwoSets_BuildsBothWithDisjointParts(bool thirdRigid)
        {
            var world = BuildStandardSrWorld();
            var first = BuildStandardSrWorld().Rigids[SrRigid];
            var m1 = SrMaterialPath("m1");
            var m2 = SrMaterialPath("m2");
            var rigid2 = SrRigidFactory([(m1, SrBlocks((9, 9)))]);
            var rigid3 = SrRigidFactory([(m2, SrBlocks((5, 5)))]);
            world.AddModel(SrRigid2, SrWs2, SrRoot2, rigid2, [[m1]]);
            if (thirdRigid)
            {
                world.AddModel(
                    "variantmeshes\\wh_main\\sr3.rigid_model_v2", "variantmeshes\\wh_main\\sr3.wsmodel",
                    "variantmeshes\\wh_variantmodels\\hu1\\emp\\sr3\\sr3.variantmeshdefinition", rigid3, [[m2]]);
            }

            var result = world.Run();

            Assert.That(Get<int>(result, "SetsConsidered"), Is.EqualTo(2), SrSkips(result));
            Assert.That(Get<int>(result, "Built"), Is.EqualTo(2), SrSkips(result));
            AssertFunnelReconciles(result);
            var builtSets = SrBuilt(result);
            Assert.That(
                builtSets.Select(built => SrBuiltRigids(built).Count),
                Is.EquivalentTo(thirdRigid ? new[] { 2, 2 } : new[] { 1, 2 }));
            // The parts are disjoint: part 0 went with {b1, n1}, part 1 with {b2, n2}; the geometry
            // invariants (positions, indices, UV1, unmoved UV0) held for the doubly rewritten rigid.
            Assert.That(Get<IEnumerable>(result, "ValidationErrors").Cast<object>(), Is.Empty);
            Assert.That(
                builtSets.Sum(built => Get<int>(built, "Parts")),
                Is.EqualTo(thirdRigid ? 4 + 1 + 1 : 4 + 1));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Is.Not.EqualTo(SrWsMaterial(world.State, SrWs, 0, 1)));
            AssertSrPixels(world, SrRigid, SrWs, first, SrPart0Seeds, (_, part) => part == 0);
            AssertSrPixels(world, SrRigid, SrWs, first, SrPart1Seeds, (_, part) => part == 1);
            AssertSrPixels(world, SrRigid2, SrWs2, rigid2, SrPart0Seeds);
            if (thirdRigid)
                AssertSrPixels(world, "variantmeshes\\wh_main\\sr3.rigid_model_v2", "variantmeshes\\wh_main\\sr3.wsmodel", rigid3, SrPart1Seeds);
            Assert.That(
                Get<HashSet<string>>(world.State, "ModifiedRigids").Count,
                Is.EqualTo(thirdRigid ? 3 : 2));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_Validation_FailsWhenAGeneratedMaterialIsUsedByAWsModelOfAnUnrewrittenRigid()
        {
            var (world, _) = BuildTwoRigidSrWorld();
            var result = world.Run();
            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            Assert.That(SrValidate(world), Is.Empty);

            Get<HashSet<string>>(result, "RepackedRigids").Remove(SrRigid2);
            var errors = SrValidate(world);

            Assert.That(errors, Has.Some.Contain(SrWs2).And.Contain("was not rewritten"));
        }

        [Test]
        public void SharedRepack_TwoRunsOfASetAcrossTwoRigids_ProduceIdenticalBytes()
        {
            var (first, _) = BuildTwoRigidSrWorld();
            var (second, _) = BuildTwoRigidSrWorld();
            first.Run();
            second.Run();

            var generated = first.Fixture.Output.Keys.Where(key => key.Contains("_repack_")).OrderBy(key => key).ToList();
            Assert.That(generated, Has.Count.EqualTo(4));
            Assert.That(
                second.Fixture.Output.Keys.Where(key => key.Contains("_repack_")).OrderBy(key => key),
                Is.EqualTo(generated));
            foreach (var path in generated)
                Assert.That(second.Fixture.Output[path], Is.EqualTo(first.Fixture.Output[path]), path);
            foreach (var rigidPath in new[] { SrRigid, SrRigid2 })
            {
                var firstRigid = (RmvFile)Get<IDictionary>(first.State, "RigidModels")[rigidPath]!;
                var secondRigid = (RmvFile)Get<IDictionary>(second.State, "RigidModels")[rigidPath]!;
                Assert.That(
                    secondRigid.ModelList.SelectMany(lod => lod).SelectMany(model => model.Mesh.VertexList.Select(vertex => vertex.Uv)),
                    Is.EqualTo(firstRigid.ModelList.SelectMany(lod => lod).SelectMany(model => model.Mesh.VertexList.Select(vertex => vertex.Uv))));
            }
        }

        [Test]
        public void SharedRepack_ReportSection_ListsEveryRigidOfAMultiRigidBuiltSet()
        {
            var (world, _) = BuildTwoRigidSrWorld();
            world.Run();
            SetStateProperty(world.State, "SharedTextureRepackRequested", true);
            SetStateProperty(world.State, "SharedTextureRepackEnabled", true);
            GetStaticMethod("FinalizeSharedTextureRepack").Invoke(null, [world.State]);

            var report = BuildSharedRepackSection(world.State);

            Assert.That(report, Does.Contain("whose users are all mod-owned rigids"));
            Assert.That(report, Does.Not.Contain("Not confined to exactly one rigid"));
            Assert.That(report, Does.Contain("2 rigid(s) rewritten: " + $"{SrRigid} (4 part(s)), {SrRigid2} (2 part(s))"));
            Assert.That(report, Does.Contain("Built: 1"));
            Assert.That(report, Does.Contain("(reconciles)"));
            Assert.That(report, Does.Contain("2 rigid(s) rewritten."));
        }

        [Test]
        public void SharedRepack_VanillaOwnedRigid_IsNotRewritten()
        {
            var world = new SrWorld();
            world.AddTexture("textures\\sr_b1.dds", 1024, "BC1", true, 1);
            world.Fixture.AddMaterial(SrMaterialPath("m1"), (SrBase, "textures\\sr_b1.dds"));
            var m1 = SrMaterialPath("m1");
            world.AddModel(SrRigid, SrWs, SrRoot, SrRigidFactory([(m1, SrBlocks((2, 3)))]), [[m1]], ownedRigid: false);

            var result = world.Run();

            Assert.That(Get<int>(result, "RigidNotEligible"), Is.EqualTo(1), SrSkips(result));
            Assert.That(SrSkips(result), Does.Contain("not owned by the source pack"));
            AssertFunnelReconciles(result);
        }

        [Test]
        public void SharedRepack_RigidOverridingAVanillaPath_IsNotRewritten()
        {
            var world = BuildStandardSrWorld();
            world.Fixture.Vanilla[SrRigid] = [0];

            var result = world.Run();

            Assert.That(Get<int>(result, "RigidNotEligible"), Is.EqualTo(1), SrSkips(result));
            Assert.That(SrSkips(result), Does.Contain("overrides a vanilla path"));
            AssertFunnelReconciles(result);
        }

        [Test]
        public void SharedRepack_WsModelOverridingAVanillaPath_IsNotRewritten()
        {
            var world = BuildStandardSrWorld();
            world.Fixture.Vanilla[SrWs] = [0];

            var result = world.Run();

            Assert.That(Get<int>(result, "RigidNotEligible"), Is.EqualTo(1), SrSkips(result));
            Assert.That(SrSkips(result), Does.Contain($"WSModel {SrWs} overrides a vanilla path"));
            Assert.That(Get<HashSet<string>>(world.State, "ModifiedWsModels"), Is.Empty);
        }

        [Test]
        public void SharedRepack_RigidReferencedByAnotherFile_IsNotRewritten()
        {
            var world = BuildStandardSrWorld();
            world.Fixture.AddText("variantmeshes\\notes.txt", "uses " + SrRigid);

            var result = world.Run();

            Assert.That(Get<int>(result, "RigidNotEligible"), Is.EqualTo(1), SrSkips(result));
            Assert.That(SrSkips(result), Does.Contain("notes.txt"));
        }

        [Test]
        public void SharedRepack_RigidAlreadyModifiedByAnotherStage_IsNotRewritten()
        {
            var world = BuildStandardSrWorld();
            world.Analyse();
            Get<HashSet<string>>(world.State, "ModifiedRigids").Add(SrRigid);

            var result = world.Run(analyse: false);

            Assert.That(Get<int>(result, "RigidNotEligible"), Is.EqualTo(1), SrSkips(result));
            Assert.That(SrSkips(result), Does.Contain("already modified"));
        }

        [Test]
        public void SharedRepack_EstimateBelowTheThreshold_BuildsNothing()
        {
            var world = BuildStandardSrWorld();

            var result = world.Run(minimumSaving: 50_000_000);

            Assert.That(Get<int>(result, "BelowThreshold"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "Built"), Is.Zero);
            AssertFunnelReconciles(result);
            Assert.That(SrSkips(result), Does.Contain("-byte threshold"));
            Assert.That(world.Fixture.Output.Keys, Has.None.Contain("_repack_"));
        }

        [Test]
        public void SharedRepack_DefaultThresholdIsOneMiB()
        {
            Assert.That(
                GetServiceType().GetField("SharedRepackMinimumSavingBytes", BindingFlags.NonPublic | BindingFlags.Static)!
                    .GetRawConstantValue(),
                Is.EqualTo(1L * 1024 * 1024));
        }

        // Part 2 uses base b1 with a normal that cannot be repacked (48 pixels, not a power of
        // two; an unrepackable texture is never merged): b1 leaves the set, which takes part 0
        // (b1 + n1) with it; the second material pair is closed and is still built.
        [Test]
        public void SharedRepack_MixedMaterial_DropsItsTexturesTransitively_AndTheRestStillBuilds()
        {
            var world = BuildStandardSrWorld(secondLod: false);
            world.AddTexture("textures\\sr_n3.dds", 48, "BC3", false, 6);
            world.AddTexture("textures\\sr_b4.dds", 1024, "BC1", true, 7);
            world.Fixture.AddMaterial(SrMaterialPath("m3"), (SrBase, "textures\\sr_b1.dds"), (SrNormal, "textures\\sr_n3.dds"));
            world.Fixture.AddMaterial(SrMaterialPath("m4"), (SrBase, "textures\\sr_b4.dds"), (SrNormal, "textures\\sr_n3.dds"));
            var m1 = SrMaterialPath("m1");
            var m2 = SrMaterialPath("m2");
            var m3 = SrMaterialPath("m3");
            var m4 = SrMaterialPath("m4");
            world.AddModel(
                SrRigid, SrWs, SrRoot,
                SrRigidFactory([(m1, SrBlocks((2, 3))), (m2, SrBlocks((10, 4))), (m3, SrBlocks((3, 3)))]),
                [[m1, m2, m3]]);
            world.AddModel(SrRigid2, SrWs2, SrRoot2, SrRigidFactory([(m4, SrBlocks((1, 1)))]), [[m4]]);

            // The cascade of earlier versions: b1 leaves with part 2 and takes part 0 with it.
            object result = null!;
            WithCopyRemovedDisabled(() => result = world.Run());

            AssertFunnelReconciles(result);
            Assert.That(Get<int>(result, "SetsConsidered"), Is.EqualTo(3), SrSkips(result));
            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            // n3 alone is tiny (below the threshold, never merged: it cannot be repacked) and b4
            // alone is pulled apart by it.
            Assert.That(Get<int>(result, "BelowThreshold"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "DroppedByClosure"), Is.EqualTo(1));
            var reasons = Get<Dictionary<string, int>>(result, "PartDropReasons");
            Assert.That(reasons.Keys, Has.Some.Contain("also uses textures outside the set"));
            var notes = BuildSharedRepackSection(Enabled(world));
            Assert.That(notes, Does.Contain("sr_n3.dds, which is in a different signature set (2 unit(s)"));
            Assert.That(notes, Does.Contain("the texture cannot be repacked, so the set was not merged"));
            var built = SrBuilt(result).Single();
            var replaced = Get<IEnumerable>(built, "Replaced").Cast<object>().Select(item => item.ToString()).ToList();
            Assert.That(replaced, Has.Count.EqualTo(2));
            Assert.That(string.Concat(replaced), Does.Contain("sr_b2").And.Contain("sr_n2"));
            Assert.That(string.Concat(replaced), Does.Not.Contain("sr_b1"));
            // Only part 1 moved; parts 0 and 2 keep their UVs and materials.
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Is.EqualTo(m1));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 2), Is.EqualTo(m3));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 1), Does.Contain("_repack_"));
            var rigid = (RmvFile)Get<IDictionary>(world.State, "RigidModels")[SrRigid]!;
            var untouched = SrRigidFactory([(m1, SrBlocks((2, 3))), (m2, SrBlocks((10, 4))), (m3, SrBlocks((3, 3)))])();
            for (var part = 0; part < 3; part += 2)
            {
                Assert.That(
                    rigid.ModelList[0][part].Mesh.VertexList.Select(vertex => vertex.Uv),
                    Is.EqualTo(untouched.ModelList[0][part].Mesh.VertexList.Select(vertex => vertex.Uv)));
            }

            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_DisagreeingMaterialVariants_OfAnotherSize_MakeThePartIneligible()
        {
            var world = BuildStandardSrWorld(secondLod: false);
            // A second WSModel binds part 0 with a material whose base colour differs in size.
            world.AddTexture("textures\\sr_b1b.dds", 512, "BC1", true, 8);
            world.Fixture.AddMaterial(SrMaterialPath("m1b"), (SrBase, "textures\\sr_b1b.dds"), (SrNormal, "textures\\sr_n1.dds"));
            var m1b = SrMaterialPath("m1b");
            var m2 = SrMaterialPath("m2");
            world.Fixture.AddText(
                "variantmeshes\\wh_main\\sr_variant.wsmodel",
                CreateWsModelDocument(SrRigid, [[m1b, m2]]).OuterXml);
            world.Links.Add((SrRoot, "variantmeshes\\wh_main\\sr_variant.wsmodel"));

            var result = world.Run();

            AssertFunnelReconciles(result);
            var reasons = Get<Dictionary<string, int>>(result, "PartDropReasons");
            Assert.That(reasons.Keys, Has.Some.Contain("differs in size, mip count or format from the layout texture, slot t_xml_base_colour"));
            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            var replaced = string.Concat(Get<IEnumerable>(SrBuilt(result).Single(), "Replaced").Cast<object>());
            Assert.That(replaced, Does.Not.Contain("sr_b1").And.Not.Contain("sr_n1"));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Is.EqualTo(SrMaterialPath("m1")));
        }

        [Test]
        public void SharedRepack_VariantInASlotOnlySomeBindingsHave_IsRefusedWithItsOwnReason()
        {
            var world = BuildStandardSrWorld(secondLod: false);
            // The second WSModel's material for part 0 has no normal at all.
            world.AddTexture("textures\\sr_b1b.dds", 1024, "BC1", true, 8);
            world.Fixture.AddMaterial(SrMaterialPath("m1b"), (SrBase, "textures\\sr_b1b.dds"));
            world.Fixture.AddText(
                "variantmeshes\\wh_main\\sr_variant.wsmodel",
                CreateWsModelDocument(SrRigid, [[SrMaterialPath("m1b"), SrMaterialPath("m2")]]).OuterXml);
            world.Links.Add((SrRoot, "variantmeshes\\wh_main\\sr_variant.wsmodel"));

            var result = world.Run();

            AssertFunnelReconciles(result);
            var reasons = Get<Dictionary<string, int>>(result, "PartDropReasons");
            Assert.That(reasons.Keys, Has.Some.Contain("bound by only some of the part's WSModel bindings"));
            Assert.That(reasons.Keys, Has.None.Contain("differs in size"));
            Assert.That(Get<int>(result, "VariantOutputs"), Is.Zero);
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Is.EqualTo(SrMaterialPath("m1")));
        }

        private const string SrVariantWs = "variantmeshes\\wh_main\\sr_variant.wsmodel";

        private static Func<RmvFile> SrVariantFactory()
            => SrRigidFactory([
                (SrMaterialPath("m1"), SrBlocks((2, 3), (3, 3))), (SrMaterialPath("m2"), SrBlocks((10, 4)))]);

        // The standard rigid, plus a second WSModel that binds part 0 with a material whose base colour is
        // another texture of the same size and format (the normal is shared) and part 1 with the same material.
        private static SrWorld BuildVariantSrWorld()
        {
            var world = BuildStandardSrWorld(secondLod: false);
            world.AddTexture("textures\\sr_b1b.dds", 1024, "BC1", true, 8);
            var m1b = SrMaterialPath("m1b");
            world.Fixture.AddMaterial(m1b, (SrBase, "textures\\sr_b1b.dds"), (SrNormal, "textures\\sr_n1.dds"));
            world.Fixture.AddText(SrVariantWs, CreateWsModelDocument(SrRigid, [[m1b, SrMaterialPath("m2")]]).OuterXml);
            world.Links.Add((SrRoot, SrVariantWs));
            return world;
        }

        private static string SrWsTexture(SrWorld world, string ws, int part, string slot)
            => SrSlotNode(
                    Encoding.UTF8.GetString(world.Fixture.Output[SrWsMaterial(world.State, ws, 0, part)]), slot)
                .SelectSingleNode("source")?.InnerText.Trim() ?? string.Empty;

        [Test]
        public void SharedRepack_TextureVariants_OnePartTwoWsModels_KeepOneUvTransformAndTwoBasePages()
        {
            var world = BuildVariantSrWorld();

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result) + SrPartDropReasons(result));
            AssertFunnelReconciles(result);
            var built = SrBuilt(result).Single();
            // One layout page (base, normal) and one variant page of the base colour.
            var outputs = Get<IEnumerable>(built, "Outputs").Cast<object>().ToList();
            Assert.That(outputs, Has.Count.EqualTo(3));
            Assert.That(Get<int>(built, "VariantOutputs"), Is.EqualTo(1));
            Assert.That(Get<int>(built, "VariantParts"), Is.EqualTo(1));
            Assert.That(Get<long>(built, "VariantSamples"), Is.GreaterThan(0));
            Assert.That(
                outputs.Where(output => Get<string>(output, "Slot") == SrBase).Select(output => Get<string>(output, "Path")).Distinct().Count(),
                Is.EqualTo(2));
            Assert.That(outputs.Count(output => Get<string>(output, "Slot") == SrNormal), Is.EqualTo(1));

            // Each WSModel's material for part 0 names its own base page and the one shared normal page.
            var firstBase = SrWsTexture(world, SrWs, 0, SrBase);
            var secondBase = SrWsTexture(world, SrVariantWs, 0, SrBase);
            Assert.That(firstBase, Is.Not.EqualTo(secondBase));
            Assert.That(SrWsTexture(world, SrWs, 0, SrNormal), Is.EqualTo(SrWsTexture(world, SrVariantWs, 0, SrNormal)));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Is.Not.EqualTo(SrWsMaterial(world.State, SrVariantWs, 0, 0)));
            // Part 1 is bound by the same material in both: one generated material.
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 1), Is.EqualTo(SrWsMaterial(world.State, SrVariantWs, 0, 1)));

            // The mesh has one set of UVs: every triangle shows the right colour through either binding.
            var factory = SrVariantFactory();
            AssertSrPixels(world, SrRigid, SrWs, factory, new Dictionary<string, int> { [SrBase] = 1, [SrNormal] = 2 }, (_, part) => part == 0);
            AssertSrPixels(world, SrRigid, SrVariantWs, factory, new Dictionary<string, int> { [SrBase] = 8, [SrNormal] = 2 }, (_, part) => part == 0);
            AssertSrPixels(world, SrRigid, SrWs, factory, SrPart1Seeds, (_, part) => part == 1);

            // Both base colours are replaced (and the normal); nothing still names them.
            var replaced = string.Concat(Get<IEnumerable>(built, "Replaced").Cast<object>());
            Assert.That(replaced, Does.Contain("sr_b1.dds").And.Contain("sr_b1b.dds").And.Contain("sr_n1.dds"));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_TextureVariants_OtherPartsRegionOfTheVariantPageIsCroppedFromItsLayoutTexture()
        {
            var world = BuildVariantSrWorld();

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result) + SrPartDropReasons(result));
            // Part 1 (no variants) shares the page: in the variant base page its charts still show its own
            // base colour (seed 3), cropped from the layout source.
            var variantPage = SrGeneratedTexture(world, SrWsMaterial(world.State, SrVariantWs, 0, 0), SrBase);
            var after = (RmvFile)Get<IDictionary>(world.State, "RigidModels")[SrRigid]!;
            var mesh = after.ModelList[0][1].Mesh;
            var (expectedR, expectedG, expectedB) = SrColour(3, 10, 4);
            double u = 0, v = 0;
            for (var corner = 0; corner < 3; corner++)
            {
                u += mesh.VertexList[mesh.IndexList[corner]].Uv.X / 3.0;
                v += mesh.VertexList[mesh.IndexList[corner]].Uv.Y / 3.0;
            }

            var x = Math.Clamp((int)(u * variantPage.Width), 0, variantPage.Width - 1);
            var y = Math.Clamp((int)(v * variantPage.Height), 0, variantPage.Height - 1);
            var pixel = (y * variantPage.Width + x) * 4;
            Assert.That(Math.Abs(variantPage.Bgra[pixel] - expectedB), Is.LessThanOrEqualTo(12));
            Assert.That(Math.Abs(variantPage.Bgra[pixel + 1] - expectedG), Is.LessThanOrEqualTo(12));
            Assert.That(Math.Abs(variantPage.Bgra[pixel + 2] - expectedR), Is.LessThanOrEqualTo(12));
        }

        [Test]
        public void SharedRepack_TextureVariants_TwoRuns_ProduceIdenticalBytes()
        {
            var first = BuildVariantSrWorld();
            var second = BuildVariantSrWorld();
            first.Run();
            second.Run();

            Assert.That(first.Fixture.Output.Keys.OrderBy(path => path), Is.EqualTo(second.Fixture.Output.Keys.OrderBy(path => path)));
            foreach (var (path, data) in first.Fixture.Output)
                Assert.That(second.Fixture.Output[path], Is.EqualTo(data), path);
        }

        [Test]
        public void SharedRepack_TextureVariants_TheGateCountsTheVariantPages()
        {
            var first = BuildVariantSrWorld().Run();
            Assert.That(Get<int>(first, "Built"), Is.EqualTo(1), SrSkips(first));
            var built = SrBuilt(first).Single();
            var outputs = Get<IEnumerable>(built, "Outputs").Cast<object>().ToList();
            var variantBytes = Get<long>(built, "VariantBytes");
            Assert.That(variantBytes, Is.GreaterThan(0));
            Assert.That(Get<long>(built, "After"), Is.EqualTo(outputs.Sum(output => Get<long>(output, "Bytes"))));
            Assert.That(
                Get<IEnumerable>(built, "Groups").Cast<object>().Sum(group => Get<long>(group, "After")),
                Is.EqualTo(Get<long>(built, "After")));
            var saving = Get<long>(built, "Before") - Get<long>(built, "After");

            // A threshold one byte above what the pages really save stops the set at the actual-bytes
            // gate (the variant page's bytes are in After); at exactly the saving it is built.
            var exact = BuildVariantSrWorld().Run(minimumSaving: saving);
            Assert.That(Get<int>(exact, "Built"), Is.EqualTo(1), SrSkips(exact));
            var above = BuildVariantSrWorld().Run(minimumSaving: saving + 1);
            Assert.That(Get<int>(above, "Built"), Is.Zero);
            Assert.That(Get<int>(above, "FailedActualBytes"), Is.EqualTo(1), SrSkips(above));
            AssertFunnelReconciles(above);
        }

        [Test]
        public void SharedRepack_MaterialVariantsThatAgreeOnTextures_AreBothRepointed()
        {
            var world = BuildStandardSrWorld(secondLod: false);
            // Same textures, a different shader parameter: its own generated copy.
            world.Fixture.AddText(
                SrMaterialPath("m1c"),
                Encoding.UTF8.GetString(world.Fixture.Source[SrMaterialPath("m1")])
                    .Replace("</textures>", "</textures><params><param><name>zz</name><type>float</type><value>2</value></param></params>"));
            var m1c = SrMaterialPath("m1c");
            var m2 = SrMaterialPath("m2");
            world.Fixture.AddText(
                "variantmeshes\\wh_main\\sr_variant.wsmodel",
                CreateWsModelDocument(SrRigid, [[m1c, m2]]).OuterXml);
            world.Links.Add((SrRoot, "variantmeshes\\wh_main\\sr_variant.wsmodel"));

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            var first = SrWsMaterial(world.State, SrWs, 0, 0);
            var second = SrWsMaterial(world.State, "variantmeshes\\wh_main\\sr_variant.wsmodel", 0, 0);
            Assert.That(first, Is.Not.EqualTo(second));
            Assert.That(Encoding.UTF8.GetString(world.Fixture.Output[second]), Does.Contain("<name>zz</name>"));
            Assert.That(Encoding.UTF8.GetString(world.Fixture.Output[first]), Does.Not.Contain("<name>zz</name>"));
            Assert.That(
                Encoding.UTF8.GetString(world.Fixture.Output[first]).Contains("sr_b1.dds"), Is.False);
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_TriangleCrossingATileBoundary_MakesThePartIneligible()
        {
            var world = BuildStandardSrWorld(secondLod: false);
            var m1 = SrMaterialPath("m1");
            var m2 = SrMaterialPath("m2");
            // u runs from 0.9 to 1.1: inside no single tile.
            float[][] crossing = [[0.9f, 0.5f, 1.1f, 0.5f, 1.0f, 0.6f]];
            world.AddModel(
                SrRigid, SrWs, SrRoot,
                SrRigidFactory([(m1, crossing), (m2, SrBlocks((10, 4)))]),
                [[m1, m2]]);

            var result = world.Run();

            var reasons = Get<Dictionary<string, int>>(result, "PartDropReasons");
            Assert.That(reasons.Keys, Has.Some.Contain("spans more than one UV tile"));
            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Is.EqualTo(m1));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 1), Does.Contain("_repack_"));
        }

        [Test]
        public void SharedRepack_NonAtlasableSlot_MakesThePartIneligible()
        {
            var world = BuildStandardSrWorld(secondLod: false);
            world.AddTexture("textures\\sr_legacy.dds", 256, "BC1", true, 9);
            world.Fixture.AddMaterial(
                SrMaterialPath("m1"),
                (SrBase, "textures\\sr_b1.dds"),
                (SrNormal, "textures\\sr_n1.dds"),
                ("s_legacy", "textures\\sr_legacy.dds"));

            var result = world.Run();

            var reasons = Get<Dictionary<string, int>>(result, "PartDropReasons");
            Assert.That(reasons.Keys, Has.Some.Contain("non-atlasable slot"));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Is.EqualTo(SrMaterialPath("m1")));
            AssertFunnelReconciles(result);
        }

        [Test]
        public void SharedRepack_ActualBytesGate_StopsASetThatDoesNotShrink()
        {
            var world = new SrWorld();
            world.AddTexture("textures\\sr_b1.dds", 1024, "BC1", true, 1);
            world.Fixture.AddMaterial(SrMaterialPath("m1"), (SrBase, "textures\\sr_b1.dds"));
            var m1 = SrMaterialPath("m1");
            // A thin diagonal sliver: a few percent of the grid cells, but its chart is the
            // whole texture, so the estimate promises a lot and the real layout saves nothing.
            float[][] sliver = [[0.01f, 0.01f, 0.99f, 0.98f, 0.99f, 0.99f]];
            world.AddModel(SrRigid, SrWs, SrRoot, SrRigidFactory([(m1, sliver)]), [[m1]]);

            var result = world.Run();

            Assert.That(Get<int>(result, "FailedActualBytes"), Is.EqualTo(1), SrSkips(result));
            Assert.That(Get<int>(result, "Built"), Is.Zero);
            Assert.That(SrSkips(result), Does.Contain("uses nearly the whole texture"));
            AssertFunnelReconciles(result);
            Assert.That(world.Fixture.Output.Keys, Has.None.Contain("_repack_"));
            Assert.That(Get<HashSet<string>>(world.State, "ModifiedRigids"), Is.Empty);
            Assert.That(Get<HashSet<string>>(world.State, "ModifiedWsModels"), Is.Empty);
        }

        [TestCase(true, false, false, false, true)]
        [TestCase(false, false, false, false, false)]
        [TestCase(true, true, false, false, true)]
        [TestCase(false, true, false, false, false)]
        [TestCase(true, true, true, false, true)]
        [TestCase(true, false, true, false, true)]
        [TestCase(false, false, true, false, false)]
        [TestCase(true, false, false, true, false)]
        [TestCase(true, true, true, true, false)]
        public void SharedRepack_OptionResolution_AllowedNextToAtlasingAndCrossRigid_IgnoredInCrossRigidOnly(
            bool requested,
            bool buildAtlases,
            bool crossRigidMerges,
            bool crossRigidOnly,
            bool expected)
        {
            var resolved = (bool)GetStaticMethod("ResolveSharedTextureRepack")
                .Invoke(null, [requested, buildAtlases, crossRigidMerges, crossRigidOnly])!;

            Assert.That(resolved, Is.EqualTo(expected));
        }

        private static void MarkAtlasChanged(object state, string rigid, params (int Lod, int Part)[] parts)
        {
            Get<HashSet<string>>(state, "AtlasChangedRigids").Add(rigid);
            var changedParts = Get<HashSet<(string Rigid, int Lod, int Part)>>(state, "AtlasChangedParts");
            foreach (var (lod, part) in parts)
                changedParts.Add((rigid, lod, part));
        }

        [Test]
        public void SharedRepack_RigidChangedByAtlasing_IsLeftOutWithAClearReason()
        {
            var world = BuildStandardSrWorld();
            world.Analyse();
            MarkAtlasChanged(world.State, SrRigid, (0, 0), (0, 1), (1, 0), (1, 1));

            var result = world.Run(analyse: false);

            Assert.That(Get<int>(result, "RigidNotEligible"), Is.EqualTo(1), SrSkips(result));
            Assert.That(Get<int>(result, "Built"), Is.Zero);
            Assert.That(SrSkips(result), Does.Contain($"{SrRigid}: the rigid was changed by texture atlasing"));
            Assert.That(Get<HashSet<string>>(result, "RigidsChangedByAtlasing"), Is.EquivalentTo(new[] { SrRigid }));
            Assert.That(Get<HashSet<string>>(world.State, "ModifiedWsModels"), Is.Empty);
            AssertFunnelReconciles(result);
        }

        [Test]
        public void SharedRepack_AtlasedPartsOfAnotherRigid_DoNotBlockTheUntouchedRigid()
        {
            var (world, _) = BuildTwoRigidSrWorld();
            world.Analyse();
            // Every part of the second rigid was atlased: it no longer uses the set's textures.
            MarkAtlasChanged(world.State, SrRigid2, (0, 0), (0, 1));

            var result = world.Run(analyse: false);

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            Assert.That(Get<HashSet<string>>(result, "RepackedRigids"), Is.EquivalentTo(new[] { SrRigid }));
            Assert.That(Get<HashSet<string>>(result, "RigidsChangedByAtlasing"), Is.EquivalentTo(new[] { SrRigid2 }));
            Assert.That(Get<int>(result, "AtlasedUsersIgnored"), Is.GreaterThan(0));
            Assert.That(Get<HashSet<string>>(world.State, "ModifiedRigids"), Is.EquivalentTo(new[] { SrRigid }));
            Assert.That(Get<HashSet<string>>(world.State, "ModifiedWsModels"), Is.EquivalentTo(new[] { SrWs }));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_ChangedRigidsUnatlasedPartStillUsesTheTexture_BlocksThatTexture()
        {
            var (world, _) = BuildTwoRigidSrWorld();
            world.Analyse();
            // Only part 0 of the second rigid was atlased; its part 1 still loads the m2 textures.
            MarkAtlasChanged(world.State, SrRigid2, (0, 0));

            var result = world.Run(analyse: false);

            Assert.That(Get<HashSet<string>>(result, "RepackedRigids"), Does.Not.Contain(SrRigid2));
            Assert.That(Get<HashSet<string>>(world.State, "ModifiedWsModels"), Does.Not.Contain(SrWs2));
            Assert.That(
                Get<IDictionary>(result, "PartDropReasons").Keys.Cast<string>(),
                Has.Some.Contain("the rigid was changed by texture atlasing"));
        }

        private const string SrRoot3 = "variantmeshes\\wh_variantmodels\\hu1\\emp\\sr3\\sr3.variantmeshdefinition";
        private const string SrRigid3 = "variantmeshes\\wh_main\\sr3.rigid_model_v2";
        private const string SrWs3 = "variantmeshes\\wh_main\\sr3.wsmodel";
        private const string SrGeneratedRigid = "variantmeshes\\asset_editor\\cross_rigid\\g.rigid_model_v2";
        private const string SrGeneratedWs = "variantmeshes\\asset_editor\\cross_rigid\\g.wsmodel";

        // What the cross-rigid writer leaves behind: a generated rigid in memory (RigidModels) with
        // a generated WSModel binding original materials, both registered as modified and generated.
        private static void AddCrossRigidGeneratedModel(
            SrWorld world,
            Func<RmvFile> factory,
            params string[][] materialsByLod)
        {
            var state = world.State;
            Get<IDictionary>(state, "RigidModels")[SrGeneratedRigid] = factory();
            Get<IDictionary>(state, "WsDocuments")[SrGeneratedWs] =
                CreateWsModelDocument(SrGeneratedRigid, materialsByLod);
            Get<HashSet<string>>(state, "CrossRigidGeneratedRigidPaths").Add(SrGeneratedRigid);
            Get<HashSet<string>>(state, "CrossRigidGeneratedWsModelPaths").Add(SrGeneratedWs);
            Get<HashSet<string>>(state, "ModifiedRigids").Add(SrGeneratedRigid);
            Get<HashSet<string>>(state, "ModifiedWsModels").Add(SrGeneratedWs);
        }

        private static Func<RmvFile> SrStandardFirstLodFactory()
        {
            var m1 = SrMaterialPath("m1");
            var m2 = SrMaterialPath("m2");
            return SrRigidFactory([(m1, SrBlocks((2, 3), (3, 3))), (m2, SrBlocks((10, 4)))]);
        }

        [Test]
        public void SharedRepack_RigidChangedByCrossRigidMerging_IsRewrittenLikeAnyOther()
        {
            var world = BuildStandardSrWorld();
            world.Analyse();
            Get<HashSet<string>>(world.State, "CrossRigidChangedRigids").Add(SrRigid);

            var result = world.Run(analyse: false);

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            Assert.That(Get<int>(result, "RigidNotEligible"), Is.Zero);
            Assert.That(Get<HashSet<string>>(result, "RepackedRigids"), Is.EquivalentTo(new[] { SrRigid }));
            Assert.That(Get<HashSet<string>>(result, "CrossRigidSourceRigidsConsidered"), Is.EquivalentTo(new[] { SrRigid }));
            AssertFunnelReconciles(result);
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_UntouchedRigidIsStillRepacked_NextToCrossRigidMerging()
        {
            var world = BuildStandardSrWorld();
            var m1 = SrMaterialPath("m1");
            // Unit 2 uses m1 in rigid 2, so {b1, n1} is a set of its own; {b2, n2} only rigid 1.
            world.AddModel(SrRigid2, SrWs2, SrRoot2, SrRigidFactory([(m1, SrBlocks((9, 9)))]), [[m1]]);
            world.Analyse();
            Get<HashSet<string>>(world.State, "CrossRigidChangedRigids").Add(SrRigid2);

            var result = world.Run(analyse: false);

            Assert.That(Get<int>(result, "SetsConsidered"), Is.EqualTo(2), SrSkips(result));
            Assert.That(Get<int>(result, "Built"), Is.EqualTo(2), SrSkips(result));
            Assert.That(Get<HashSet<string>>(result, "RepackedRigids"), Is.EquivalentTo(new[] { SrRigid, SrRigid2 }));
            Assert.That(Get<HashSet<string>>(world.State, "ModifiedWsModels"), Is.EquivalentTo(new[] { SrWs, SrWs2 }));
            AssertFunnelReconciles(result);
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_SetUsedByACrossRigidSourceAndAGeneratedRigid_RewritesBothAndLeavesTheCrossRigidOutputAlone()
        {
            var world = BuildStandardSrWorld(secondLod: false);
            var m1 = SrMaterialPath("m1");
            var m2 = SrMaterialPath("m2");
            world.Analyse();
            Get<HashSet<string>>(world.State, "CrossRigidChangedRigids").Add(SrRigid);
            var factory = SrStandardFirstLodFactory();
            AddCrossRigidGeneratedModel(world, factory, [m1, m2]);
            var generatedRigid = (RmvFile)Get<IDictionary>(world.State, "RigidModels")[SrGeneratedRigid]!;
            var indicesBefore = generatedRigid.ModelList[0].Select(model => model.Mesh.IndexList.ToArray()).ToList();
            var generatedRigidsBefore = Get<HashSet<string>>(world.State, "CrossRigidGeneratedRigidPaths").ToList();
            var generatedWsBefore = Get<HashSet<string>>(world.State, "CrossRigidGeneratedWsModelPaths").ToList();

            var result = world.Run(analyse: false);

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            AssertFunnelReconciles(result);
            Assert.That(
                Get<HashSet<string>>(result, "RepackedRigids"),
                Is.EquivalentTo(new[] { SrRigid, SrGeneratedRigid }));
            var built = SrBuilt(result).Single();
            Assert.That(Get<int>(built, "Parts"), Is.EqualTo(4));
            // Both WSModels point to the same new materials, one per original material.
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Does.Contain("_repack_"));
            Assert.That(SrWsMaterial(world.State, SrGeneratedWs, 0, 0), Is.EqualTo(SrWsMaterial(world.State, SrWs, 0, 0)));
            Assert.That(SrWsMaterial(world.State, SrGeneratedWs, 0, 1), Is.EqualTo(SrWsMaterial(world.State, SrWs, 0, 1)));
            // UVs: the same transform, so every triangle samples the same colours in both rigids.
            AssertSrPixels(world, SrRigid, SrWs, factory, SrPart0Seeds, (_, part) => part == 0);
            AssertSrPixels(world, SrRigid, SrWs, factory, SrPart1Seeds, (_, part) => part == 1);
            AssertSrPixels(world, SrGeneratedRigid, SrGeneratedWs, factory, SrPart0Seeds, (_, part) => part == 0);
            AssertSrPixels(world, SrGeneratedRigid, SrGeneratedWs, factory, SrPart1Seeds, (_, part) => part == 1);
            var sourceRigid = (RmvFile)Get<IDictionary>(world.State, "RigidModels")[SrRigid]!;
            for (var part = 0; part < 2; part++)
            {
                Assert.That(
                    generatedRigid.ModelList[0][part].Mesh.VertexList.Select(vertex => vertex.Uv),
                    Is.EqualTo(sourceRigid.ModelList[0][part].Mesh.VertexList.Select(vertex => vertex.Uv)));
            }

            // The cross-rigid output is otherwise unchanged: same generated paths, parts and indices.
            Assert.That(Get<HashSet<string>>(world.State, "CrossRigidGeneratedRigidPaths"), Is.EquivalentTo(generatedRigidsBefore));
            Assert.That(Get<HashSet<string>>(world.State, "CrossRigidGeneratedWsModelPaths"), Is.EquivalentTo(generatedWsBefore));
            Assert.That(generatedRigid.ModelList, Has.Length.EqualTo(1));
            Assert.That(generatedRigid.ModelList[0], Has.Length.EqualTo(2));
            Assert.That(
                generatedRigid.ModelList[0].Select(model => model.Mesh.IndexList.ToArray()),
                Is.EqualTo(indicesBefore));
            Assert.That(Get<HashSet<string>>(world.State, "ModifiedRigids"), Is.EquivalentTo(new[] { SrRigid, SrGeneratedRigid }));
            Assert.That(Get<HashSet<string>>(world.State, "ModifiedWsModels"), Is.EquivalentTo(new[] { SrWs, SrGeneratedWs }));
            Assert.That(
                BuildSharedRepackSection(Enabled(world)),
                Does.Contain("1 generated cross-rigid rigid(s) (2 part(s))"));
            Assert.That(SrValidate(world), Is.Empty);
            Assert.That(Get<IEnumerable>(result, "ValidationErrors").Cast<object>(), Is.Empty);
        }

        [Test]
        public void SharedRepack_GeneratedPartMergingTwoSourceParts_MapsEveryTriangle_AndKeepsBothChartsOnOnePage()
        {
            var world = new SrWorld();
            world.AddTexture("textures\\sr_big.dds", 2048, "BC1", true, 1);
            world.Fixture.AddMaterial(SrMaterialPath("mbig"), (SrBase, "textures\\sr_big.dds"));
            var mbig = SrMaterialPath("mbig");
            var a = UvQuad(0.004, 0.004, 0.2, 0.2);
            var b = UvQuad(0.5, 0.004, 0.7, 0.2);
            var factory = SrRigidFactory([(mbig, a), (mbig, b)]);
            world.AddModel(SrRigid, SrWs, SrRoot, factory, [[mbig, mbig]]);
            world.Analyse();
            Get<HashSet<string>>(world.State, "CrossRigidChangedRigids").Add(SrRigid);
            // The merged model has both quads (two separate charts) in a single part.
            var mergedFactory = SrRigidFactory([(mbig, a.Concat(b).ToArray())]);
            AddCrossRigidGeneratedModel(world, mergedFactory, [mbig]);

            var result = world.Run(analyse: false, minimumSaving: 100_000, minimumGroupSaving: 10_000);

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            var built = SrBuilt(result).Single();
            // The merged part joins both charts into one cluster, so the group cannot be split.
            Assert.That(Get<int>(built, "Pages"), Is.EqualTo(1));
            Assert.That(Get<int>(built, "Charts"), Is.EqualTo(2));
            Assert.That(Get<IEnumerable>(built, "Splits").Cast<object>().Single(), Has.Property("OneCluster").True);
            var material = SrWsMaterial(world.State, SrGeneratedWs, 0, 0);
            Assert.That(material, Does.Contain("_repack_"));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Is.EqualTo(material));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 1), Is.EqualTo(material));
            var (samples, max) = AssertSrPixels(world, SrGeneratedRigid, SrGeneratedWs, mergedFactory, SrBigSeeds);
            Assert.That(samples, Is.EqualTo(4));
            Assert.That(max, Is.LessThanOrEqualTo(12));
            AssertSrPixels(world, SrRigid, SrWs, factory, SrBigSeeds);
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_SetAlsoUsedByAVanillaRigid_IsStillBlocked_EvenWithGeneratedUsers()
        {
            var world = BuildStandardSrWorld(secondLod: false);
            var m1 = SrMaterialPath("m1");
            var m2 = SrMaterialPath("m2");
            // A vanilla rigid keeps b1 + n1 (m1) loaded: it cannot be rewritten.
            world.AddModel(SrRigid2, SrWs2, SrRoot, SrRigidFactory([(m1, SrBlocks((9, 9)))]), [[m1]], ownedRigid: false);
            world.Analyse();
            Get<HashSet<string>>(world.State, "CrossRigidChangedRigids").Add(SrRigid);
            AddCrossRigidGeneratedModel(world, SrStandardFirstLodFactory(), [m1, m2]);

            var result = world.Run(analyse: false);

            AssertFunnelReconciles(result);
            // The m2 textures are used by the source and the generated rigid only: they are repacked.
            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            var replaced = string.Concat(Get<IEnumerable>(SrBuilt(result).Single(), "Replaced").Cast<object>());
            Assert.That(replaced, Does.Contain("sr_b2").And.Not.Contain("sr_b1"));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Is.EqualTo(m1));
            Assert.That(SrWsMaterial(world.State, SrGeneratedWs, 0, 0), Is.EqualTo(m1));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 1), Does.Contain("_repack_"));
            Assert.That(SrWsMaterial(world.State, SrGeneratedWs, 0, 1), Is.EqualTo(SrWsMaterial(world.State, SrWs, 0, 1)));
            Assert.That(
                Get<IDictionary>(result, "PartDropReasons").Keys.Cast<string>(),
                Has.Some.Contain("the part's rigid is not eligible: the rigid is not owned by the source pack"));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_GeneratedPartThatTilesItsTexture_DropsOutWithAReason()
        {
            var world = BuildStandardSrWorld(secondLod: false);
            var m1 = SrMaterialPath("m1");
            var m2 = SrMaterialPath("m2");
            world.Analyse();
            Get<HashSet<string>>(world.State, "CrossRigidChangedRigids").Add(SrRigid);
            // u runs from 0.9 to 1.1 in the generated m1 part: inside no single tile.
            float[][] crossing = [[0.9f, 0.5f, 1.1f, 0.5f, 1.0f, 0.6f]];
            AddCrossRigidGeneratedModel(
                world,
                SrRigidFactory([(m1, crossing), (m2, SrBlocks((10, 4)))]),
                [m1, m2]);

            var result = world.Run(analyse: false);

            var reasons = Get<IDictionary>(result, "PartDropReasons").Keys.Cast<string>().ToList();
            Assert.That(reasons, Has.Some.Contain("spans more than one UV tile"));
            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            // m1's textures left the set: the generated part and the source part keep m1.
            Assert.That(SrWsMaterial(world.State, SrGeneratedWs, 0, 0), Is.EqualTo(m1));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Is.EqualTo(m1));
            Assert.That(SrWsMaterial(world.State, SrGeneratedWs, 0, 1), Does.Contain("_repack_"));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 1), Is.EqualTo(SrWsMaterial(world.State, SrGeneratedWs, 0, 1)));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_AtlasingCrossRigidAndRepackTogether_FunnelReconciles()
        {
            var world = BuildStandardSrWorld();
            var m1 = SrMaterialPath("m1");
            var m2 = SrMaterialPath("m2");
            world.AddModel(SrRigid2, SrWs2, SrRoot2, SrRigidFactory([(m1, SrBlocks((9, 9)))]), [[m1]]);
            world.AddModel(SrRigid3, SrWs3, SrRoot3, SrRigidFactory([(m2, SrBlocks((5, 5)))]), [[m2]]);
            world.Analyse();
            MarkAtlasChanged(world.State, SrRigid2, (0, 0));
            Get<HashSet<string>>(world.State, "CrossRigidChangedRigids").Add(SrRigid3);
            AddCrossRigidGeneratedModel(world, SrRigidFactory([(m2, SrBlocks((5, 5)))]), [m2]);

            var result = world.Run(analyse: false);

            AssertFunnelReconciles(result);
            Assert.That(Get<HashSet<string>>(result, "RigidsChangedByAtlasing"), Does.Contain(SrRigid2));
            Assert.That(Get<HashSet<string>>(result, "CrossRigidSourceRigidsConsidered"), Does.Contain(SrRigid3));
            Assert.That(Get<HashSet<string>>(result, "CrossRigidGeneratedRigidsConsidered"), Does.Contain(SrGeneratedRigid));
            Assert.That(Get<HashSet<string>>(result, "RepackedRigids"), Does.Not.Contain(SrRigid2));
            Assert.That(SrValidate(world), Is.Empty);
        }

        // --- Repack and the same-rigid mesh merge ---------------------------------------------

        // One rigid, one LOD, two parts that share the material m1.
        private static SrWorld BuildSharedMaterialSrWorld()
        {
            var world = new SrWorld();
            world.AddTexture("textures\\sr_b1.dds", 1024, "BC1", true, 1);
            world.AddTexture("textures\\sr_n1.dds", 512, "BC3", false, 2);
            world.Fixture.AddMaterial(SrMaterialPath("m1"), (SrBase, "textures\\sr_b1.dds"), (SrNormal, "textures\\sr_n1.dds"));
            var m1 = SrMaterialPath("m1");
            world.AddModel(
                SrRigid,
                SrWs,
                SrRoot,
                MergeableSrRigid(SrRigidFactory([(m1, SrBlocks((2, 3))), (m1, SrBlocks((10, 4)))])),
                [[m1, m1]]);
            return world;
        }

        // The parts get the header and material the merge compares (CreateUvPart has neither).
        private static Func<RmvFile> MergeableSrRigid(Func<RmvFile> factory)
            => () =>
            {
                var rigid = factory();
                foreach (var part in rigid.ModelList.SelectMany(lod => lod))
                {
                    var header = RmvCommonHeader.CreateDefault();
                    header.ModelTypeFlag = ModelMaterialEnum.weighted;
                    part.CommonHeader = header;
                    part.Material = new WeightedMaterial();
                }

                return rigid;
            };

        private static (int Before, int After, int Entries) RunSrMerge(SrWorld world)
        {
            var rigids = Get<IDictionary>(world.State, "RigidModels");
            if (!rigids.Contains(SrRigid))
                rigids[SrRigid] = world.Rigids[SrRigid]();
            GetServiceType()
                .GetMethod("MergeCompatibleMeshes", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(world.Service, [world.State, CancellationToken.None, null]);
            return (
                Get<int>(world.State, "MeshPartsBeforeMerging"),
                Get<int>(world.State, "MeshPartsAfterMerging"),
                Get<IList>(world.State, "MeshMergeEntries").Count);
        }

        [Test]
        public void SharedRepack_PartsThatSharedAMaterial_StillShareOneGeneratedMaterial_AndStillMerge()
        {
            // Without the repack.
            var plain = BuildSharedMaterialSrWorld();
            plain.Analyse();
            var plainMerge = RunSrMerge(plain);

            var world = BuildSharedMaterialSrWorld();
            var result = world.Run(minimumSaving: 10_000, minimumGroupSaving: 1_000);
            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            var first = SrWsMaterial(world.State, SrWs, 0, 0);
            var second = SrWsMaterial(world.State, SrWs, 0, 1);
            Assert.That(first, Does.Contain("_repack_"));
            Assert.That(second, Is.EqualTo(first));

            var merge = RunSrMerge(world);

            Assert.That(plainMerge.Before - plainMerge.After, Is.EqualTo(1), "the fixture's parts merge without the repack");
            Assert.That(merge.Before - merge.After, Is.EqualTo(plainMerge.Before - plainMerge.After));
            Assert.That(merge.Entries, Is.EqualTo(plainMerge.Entries));
        }

        // --- Report: keep-alpha channels and atlas formats -----------------------------------

        private static string WriteAtlasFormatReport(Action<object> configure)
        {
            var reportPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"atlas_format_report_{Guid.NewGuid():N}.txt");
            try
            {
                var state = CreateNoAtlasState(reportPath: reportPath, buildAtlases: true);
                configure(state);
                GetStaticMethod("WriteReport").Invoke(null, [state, new List<string>(), true, null]);
                return System.IO.File.ReadAllText(reportPath);
            }
            finally
            {
                System.IO.File.Delete(reportPath);
            }
        }

        private static void AddGeneratedAtlas(
            object state,
            string path,
            string slot,
            Editors.ImportExport.Dds.Native.DXGI_FORMAT format,
            string? keepAlphaReason,
            int width = 1024,
            int height = 512)
        {
            Get<HashSet<string>>(state, "GeneratedTexturePaths").Add(path);
            Get<IDictionary>(state, "GeneratedTextureDimensions")[path] = (width, height);
            Get<IDictionary>(state, "GeneratedTextureFormats")[path] =
                Activator.CreateInstance(GetNestedType("GeneratedTextureFormat"), [slot, format, keepAlphaReason])!;
        }

        [Test]
        public void Report_KeepAlphaAtlas_CountsTheBc3ChannelsAndAnnotatesTheList()
        {
            var reason = AlphaSafety("HasAlpha", "weighted4_character_alpha", "t_xml_base_colour")!;
            var report = WriteAtlasFormatReport(state =>
            {
                AddGeneratedAtlas(state, "textures\\asset_editor\\atlases\\a0_base_colour.dds", "t_xml_base_colour",
                    Editors.ImportExport.Dds.Native.DXGI_FORMAT.BC3_UNORM_SRGB, reason);
                AddGeneratedAtlas(state, "textures\\asset_editor\\atlases\\a0_material_map.dds", "t_xml_material_map",
                    Editors.ImportExport.Dds.Native.DXGI_FORMAT.BC1_UNORM_SRGB, null);
                AddGeneratedAtlas(state, "textures\\asset_editor\\atlases\\a0_normal.dds", "t_xml_normal",
                    Editors.ImportExport.Dds.Native.DXGI_FORMAT.BC3_UNORM, null);
                SetStateProperty(state, "KeepAlphaFormatGroupsPlanned", 3);
                SetStateProperty(state, "KeepAlphaFormatGroupsWithAlpha", 1);
            });

            var lines = report.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
            Assert.That(lines, Does.Contain("Atlas textures generated: 3"));
            Assert.That(lines, Does.Contain("Atlas channels written as BC3 to keep alpha: 1 of 2 BC1-target channels (base colour 1, material map 0)"));
            Assert.That(
                lines.Single(line => line.StartsWith("Keep-alpha format groups:")),
                Does.StartWith("Keep-alpha format groups: 1 needing alpha kept of 3 planned"));
            Assert.That(
                lines,
                Does.Contain($"textures\\asset_editor\\atlases\\a0_base_colour.dds (1024x512, BC3_UNORM_SRGB) [BC3: alpha kept: {reason}]"));
            Assert.That(lines, Does.Contain("textures\\asset_editor\\atlases\\a0_material_map.dds (1024x512, BC1_UNORM_SRGB)"));
            Assert.That(lines, Does.Contain("textures\\asset_editor\\atlases\\a0_normal.dds (1024x512, BC3_UNORM)"));
        }

        [Test]
        public void Report_NoKeepAlphaCandidates_PrintsZeroAndBc1Formats()
        {
            var report = WriteAtlasFormatReport(state =>
            {
                AddGeneratedAtlas(state, "textures\\asset_editor\\atlases\\a1_base_colour.dds", "t_xml_base_colour",
                    Editors.ImportExport.Dds.Native.DXGI_FORMAT.BC1_UNORM_SRGB, null);
            });

            var lines = report.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
            Assert.That(lines, Does.Contain("Atlas channels written as BC3 to keep alpha: 0 of 1 BC1-target channels (base colour 0, material map 0)"));
            Assert.That(lines, Does.Contain("Keep-alpha format groups: 0 needing alpha kept of 0 planned (candidates are split by the slots whose alpha must be kept before batching)"));
            Assert.That(lines, Does.Contain("textures\\asset_editor\\atlases\\a1_base_colour.dds (1024x512, BC1_UNORM_SRGB)"));
            Assert.That(report, Does.Not.Contain("[BC3: alpha kept"));
        }

        [Test]
        public void KeepAlphaReason_IsTheFirstMembersReasonForThatSlot()
        {
            var first = CreateKeepAlphaCandidate("t_xml_base_colour");
            Get<IDictionary>(first, "KeepAlphaReasons")["t_xml_base_colour"] = "shader X reads alpha";
            var none = CreateKeepAlphaCandidate();

            var reason = GetStaticMethod("GetKeepAlphaReason").Invoke(null, [ToCandidateList(none, first), "t_xml_base_colour"]);
            var absent = GetStaticMethod("GetKeepAlphaReason").Invoke(null, [ToCandidateList(none, first), "t_xml_material_map"]);

            Assert.That(reason, Is.EqualTo("shader X reads alpha"));
            Assert.That(absent, Is.Null);
        }

        // --- Atlasing runs after cross-rigid merging ---------------------------------------------

        private static object CreateAtlasCandidateFor(string rigidPath, int part)
        {
            var type = GetNestedType("AtlasCandidate");
            var constructor = type.GetConstructors(AnyInstance).OrderByDescending(item => item.GetParameters().Length).First();
            var values = constructor.GetParameters()
                .Select(parameter => parameter.ParameterType.IsValueType
                    ? Activator.CreateInstance(parameter.ParameterType)
                    : null)
                .ToArray();
            var parameters = constructor.GetParameters();
            void Set(string name, object? value)
                => values[Array.FindIndex(parameters, item => item.Name == name)] = value;
            Set("Key", Activator.CreateInstance(GetNestedType("MeshKey"), [rigidPath, 0, part]));
            Set("RootVmdPath", "root.variantmeshdefinition");
            Set("Usages", Activator.CreateInstance(typeof(List<>).MakeGenericType(GetNestedType("WsUsage"))));
            Set("ResolvedChannels", new HashSet<string>());
            Set("ConstantChannels", new Dictionary<string, Editors.ImportExport.TextureAtlas.TextureAtlasConstantColor>());
            Set("MaterialDocument", new XmlDocument());
            return constructor.Invoke(values);
        }

        [Test]
        public void Atlas_RigidsTheCrossRigidWriterBuiltFrom_AreNotCandidates_AndTheSkipSaysWhy()
        {
            var state = CreateBareBatchState();
            Get<HashSet<string>>(state, "CrossRigidChangedRigids").Add("variantmeshes\\wh_main\\a.rigid_model_v2");
            var changed = CreateAtlasCandidateFor("variantmeshes\\wh_main\\a.rigid_model_v2", 0);
            var untouched = CreateAtlasCandidateFor("variantmeshes\\wh_main\\b.rigid_model_v2", 0);

            var kept = (IList)GetStaticMethod("ExcludeCrossRigidChangedAtlasCandidates")
                .Invoke(null, [state, ToCandidateList(changed, untouched)])!;

            Assert.That(kept, Has.Count.EqualTo(1));
            Assert.That(Get<object>(kept[0]!, "Key").ToString(), Does.Contain("b.rigid_model_v2"));
            Assert.That(Get<int>(state, "AtlasCandidatesExcludedByCrossRigid"), Is.EqualTo(1));
            var details = Get<IDictionary>(state, "SkipDetails").Values.Cast<IEnumerable>().SelectMany(list => list.Cast<object>()).ToList();
            Assert.That(details, Has.Count.EqualTo(1));
            Assert.That(Get<string>(details[0], "Reason"), Does.Contain("the rigid was changed by cross-rigid merging"));
        }

        [Test]
        public void Atlas_WithoutCrossRigidChanges_KeepsEveryCandidate()
        {
            var state = CreateBareBatchState();
            var candidates = ToCandidateList(
                CreateAtlasCandidateFor("variantmeshes\\wh_main\\a.rigid_model_v2", 0),
                CreateAtlasCandidateFor("variantmeshes\\wh_main\\b.rigid_model_v2", 0));

            var kept = GetStaticMethod("ExcludeCrossRigidChangedAtlasCandidates").Invoke(null, [state, candidates]);

            Assert.That(kept, Is.SameAs(candidates));
            Assert.That(Get<int>(state, "AtlasCandidatesExcludedByCrossRigid"), Is.Zero);
        }

        [Test]
        public void AtlasGate_TextureStillNamedByAGeneratedCrossRigidWsModel_HasThatConsumer_AndIsNeverRetired()
        {
            var fixture = new DownscaleFixture();
            fixture.AddTexture("textures\\t_kept.dds", 256, 9);
            fixture.AddMaterial("materials\\m_kept.xml.material", ("t_xml_base_colour", "textures\\t_kept.dds"));
            var (_, state) = CreateDownscaleState(fixture, false, buildAtlases: true);
            const string generatedWs = "variantmeshes\\asset_editor\\cross_rigid\\g.wsmodel";
            Get<IDictionary>(state, "WsDocuments")[generatedWs] = CreateWsModelDocument(
                "variantmeshes\\asset_editor\\cross_rigid\\g.rigid_model_v2",
                [["materials\\m_kept.xml.material"]]);
            Get<HashSet<string>>(state, "CrossRigidGeneratedWsModelPaths").Add(generatedWs);

            var index = (IDictionary)GetStaticMethod("BuildAtlasValueGateSourceTextureIndex")
                .Invoke(null, [state, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "textures\\t_kept.dds" }])!;

            var texture = index["textures\\t_kept.dds"]!;
            var references = Get<IEnumerable>(texture, "References").Cast<object>().ToList();
            Assert.That(references, Has.Count.EqualTo(1));
            Assert.That(Get<string>(references[0], "ConsumerAssetPath"), Is.EqualTo(generatedWs));
            // Nothing rewrites that reference, so retiring the texture is never credited, and it
            // counts as a pack consumer like a source WSModel would.
            Assert.That(Get<IEnumerable>(texture, "PackReferences").Cast<object>().Count(), Is.EqualTo(1));
        }

        // --- Shared repack density follows the downscale option ----------------------------

        // As if the early downscale analysis had chosen `scale` (mip slicing) for the texture.
        private static void AddEarlyDownscale(object state, string path, int size, double scale)
        {
            var skip = (int)Math.Round(Math.Log2(1.0 / scale));
            var newSize = size >> skip;
            var candidate = Activator.CreateInstance(
                GetNestedType("TextureDownscaleCandidate"),
                [path, size, size, newSize, newSize, scale, "BC1", "BC1", true, true, true, skip,
                 0L, 0L, 1, 1, "test", 1.0])!;
            Get<IDictionary>(state, "DownscaleEarlyCandidates")[path] = candidate;
            Get<IDictionary>(state, "DownscaleTargetScale")[path] = scale;
        }

        private static long SrGeneratedTextureBytes(SrWorld world)
            => world.Fixture.Output
                .Where(pair => pair.Key.Contains("_repack_") && pair.Key.EndsWith(".dds"))
                .Sum(pair => (long)pair.Value.Length);

        [Test]
        public void SharedRepack_DownscaleOn_BuildsAtTheEarlyScaleAndKeepsThePixelsRight()
        {
            var full = BuildStandardSrWorld();
            var fullResult = full.Run(minimumSaving: 10_000, minimumGroupSaving: 1_000);
            Assert.That(Get<int>(fullResult, "Built"), Is.EqualTo(1), SrSkips(fullResult));

            var world = BuildStandardSrWorld();
            world.Analyse();
            AddEarlyDownscale(world.State, "textures\\sr_b1.dds", 1024, 0.5);
            AddEarlyDownscale(world.State, "textures\\sr_b2.dds", 1024, 0.5);

            var result = world.Run(analyse: false, minimumSaving: 10_000, minimumGroupSaving: 1_000);

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            AssertFunnelReconciles(result);
            Assert.That(
                Get<Dictionary<string, double>>(result, "ScaledSourceTextures"),
                Is.EquivalentTo(new Dictionary<string, double>
                {
                    ["textures\\sr_b1.dds"] = 0.5,
                    ["textures\\sr_b2.dds"] = 0.5,
                }));
            Assert.That(
                Get<HashSet<string>>(world.State, "DownscaleEarlyUsedByRepack"),
                Is.EquivalentTo(new[] { "textures\\sr_b1.dds", "textures\\sr_b2.dds" }));
            // Half the texel density: the generated base colour is far smaller than at full density.
            Assert.That(SrGeneratedTextureBytes(world), Is.LessThan(SrGeneratedTextureBytes(full)));
            // The saving is measured against the scaled sources (two 512 BC1 chains + two 512 BC3
            // normals), not the 1024 originals.
            var built = SrBuilt(result).Single();
            var sourceBytes = Get<long>(built, "Before");
            var fullSourceBytes = Get<long>(SrBuilt(fullResult).Single(), "Before");
            Assert.That(sourceBytes, Is.LessThan(fullSourceBytes));
            Assert.That(Get<long>(built, "After"), Is.LessThan(sourceBytes));
            // The repacked pixels and the block-copied levels still verify.
            var first = BuildStandardSrWorld().Rigids[SrRigid];
            AssertSrPixels(world, SrRigid, SrWs, first, SrPart0Seeds, (_, part) => part == 0);
            AssertSrPixels(world, SrRigid, SrWs, first, SrPart1Seeds, (_, part) => part == 1);
            Assert.That(SrValidate(world), Is.Empty);
            Assert.That(Get<IEnumerable>(result, "ValidationErrors").Cast<object>(), Is.Empty);
            Assert.That(Get<IEnumerable>(result, "PixelFailures").Cast<object>(), Is.Empty);
        }

        [Test]
        public void SharedRepack_DownscaleOff_IsUnchangedByTheDensityCode()
        {
            var a = BuildStandardSrWorld();
            var b = BuildStandardSrWorld();
            b.Analyse();
            // Nothing in the early maps (downscale off): the same bytes as the plain run.
            var resultA = a.Run(minimumSaving: 10_000, minimumGroupSaving: 1_000);
            var resultB = b.Run(analyse: false, minimumSaving: 10_000, minimumGroupSaving: 1_000);

            Assert.That(Get<int>(resultB, "Built"), Is.EqualTo(Get<int>(resultA, "Built")));
            Assert.That(Get<Dictionary<string, double>>(resultB, "ScaledSourceTextures"), Is.Empty);
            Assert.That(
                b.Fixture.Output.Where(pair => pair.Key.Contains("_repack_")).OrderBy(pair => pair.Key).Select(pair => pair.Key),
                Is.EqualTo(a.Fixture.Output.Where(pair => pair.Key.Contains("_repack_")).OrderBy(pair => pair.Key).Select(pair => pair.Key)));
            foreach (var (path, bytes) in a.Fixture.Output.Where(pair => pair.Key.Contains("_repack_")))
                Assert.That(b.Fixture.Output[path], Is.EqualTo(bytes), path);
            Assert.That(BuildSharedRepackSection(a.State), Does.Not.Contain("Density follows the downscale option"));
        }

        // --- Early downscale estimate ----------------------------------------------------

        private static object RunEarlyDownscale(object service, object state, DownscaleFixture fixture)
        {
            Func<string, RmvFile?> loader = path => fixture.Rigids.GetValueOrDefault(path);
            GetServiceType()
                .GetMethod("RunEarlyTextureDownscale", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [state, CancellationToken.None, null, loader]);
            return Get<object>(state, "TextureDownscaleEarly");
        }

        [Test]
        public void Downscale_EarlyEstimate_ProducesTheTargetScalesTheLateRunUsesAndWritesNothing()
        {
            var fixture = BuildStandardDownscaleFixture();
            var (service, state) = CreateStandardDownscaleState(fixture, downscale: true);
            var before = fixture.Output.ToDictionary(entry => entry.Key, entry => entry.Value);

            var early = RunEarlyDownscale(service, state, fixture);

            Assert.That(Get<string?>(early, "Failure"), Is.Null);
            var scales = Get<Dictionary<string, double>>(state, "DownscaleTargetScale");
            Assert.That(scales.Keys.Order(), Is.EqualTo(new[] { "textures\\t_agg.dds", "textures\\t_half.dds", "textures\\t_reenc.dds" }));
            Assert.That(scales["textures\\t_half.dds"], Is.EqualTo(0.5));
            Assert.That(scales.ContainsKey("textures\\t_full.dds"), Is.False);
            Assert.That(Get<bool>(early, "Applied"), Is.False);
            foreach (var (path, bytes) in before)
                Assert.That(fixture.Output[path], Is.SameAs(bytes), path);
            Assert.That(fixture.Output.Count, Is.EqualTo(before.Count));

            // The late run agrees with the early one on the source state.
            var late = RunDownscale(service, state, fixture);
            Assert.That(
                Candidates(late).ToDictionary(item => Get<string>(item, "Path"), item => Get<double>(item, "Scale")),
                Is.EqualTo(scales));
        }

        [Test]
        public void Downscale_EarlyEstimateThatDiffersFromTheLateDecision_IsReported()
        {
            var fixture = BuildStandardDownscaleFixture();
            var (service, state) = CreateStandardDownscaleState(fixture, downscale: true);
            RunEarlyDownscale(service, state, fixture);
            // As if an earlier stage had sized t_half at 0.25 and t_full at 0.5 while the late
            // run (still 0.5 and full size) decides otherwise; t_agg agrees.
            var scales = Get<Dictionary<string, double>>(state, "DownscaleTargetScale");
            scales["textures\\t_half.dds"] = 0.25;
            scales["textures\\t_full.dds"] = 0.5;
            var used = Get<HashSet<string>>(state, "DownscaleEarlyUsedByGate");
            used.UnionWith(["textures\\t_half.dds", "textures\\t_full.dds", "textures\\t_agg.dds"]);

            var late = RunDownscale(service, state, fixture);

            Assert.That(Get<int>(late, "EarlyComparedTextures"), Is.EqualTo(3));
            Assert.That(
                Get<List<string>>(late, "EarlyMismatches"),
                Is.EqualTo(new[]
                {
                    "textures\\t_full.dds: early estimate 0.5x, late decision full size",
                    "textures\\t_half.dds: early estimate 0.25x, late decision 0.5x",
                }));
            var report = new System.Text.StringBuilder();
            GetServiceType().GetMethod("AppendTextureDownscaleSection", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [report, state]);
            Assert.That(report.ToString(), Does.Contain("early/late mismatch: textures\\t_half.dds"));
        }

        // --- Shared texture repack outputs as generated files ------------------------------

        private const string RepackTexture = "textures\\asset_editor\\atlases\\sr_repack_x.dds";
        private const string RepackMaterial = "materials\\sr_m_repack_x.xml.material";

        [Test]
        public void Downscale_RepackMaterialsAndTextures_AreListedWithTheirOwnReasonAndNotRewritten()
        {
            var fixture = BuildStandardDownscaleFixture();
            fixture.AddTexture("textures\\t_rp_kept.dds", 1024, 11);
            fixture.AddTexture(RepackTexture, 1024, 11);
            fixture.AddMaterial(
                RepackMaterial,
                ("t_xml_base_colour", RepackTexture),
                ("t_xml_mask", "textures\\t_rp_kept.dds"));
            var (service, state) = CreateStandardDownscaleState(fixture, downscale: true);
            AddToSet(state, "SharedRepackTexturePaths", RepackTexture);
            AddToSet(state, "SharedRepackMaterialPaths", RepackMaterial);
            var before = fixture.Output.ToDictionary(entry => entry.Key, entry => entry.Value);

            var result = RunDownscale(service, state, fixture);
            var plan = Get<object>(result, "Plan");

            var reason = DownscaleReason("TextureDownscaleReasonRepackMaterial");
            Assert.That(reason, Does.Contain("shared-texture-repack"));
            Assert.That(Reasons(result).GetValueOrDefault(reason), Is.EqualTo(1));
            Assert.That(
                Get<Dictionary<string, List<string>>>(plan, "IneligibleExamples")[reason].Single(),
                Does.Contain("t_rp_kept.dds"));
            // The repack texture is counted on its own line, the atlas figure is unchanged.
            Assert.That(Get<int>(plan, "RepackGeneratedUntouched"), Is.EqualTo(1));
            Assert.That(Get<int>(plan, "GeneratedUntouched"), Is.EqualTo(1));
            Assert.That(Candidates(result).Select(candidate => Get<string>(candidate, "Path")), Does.Not.Contain(RepackTexture));
            Assert.That(Candidates(result).Select(candidate => Get<string>(candidate, "Path")), Does.Not.Contain("textures\\t_rp_kept.dds"));
            foreach (var untouched in new[] { RepackTexture, RepackMaterial, "textures\\t_rp_kept.dds" })
                Assert.That(fixture.Output[untouched], Is.SameAs(before[untouched]), untouched);
        }

        private static List<string> RunGeneratedMaterialValidation(object service, object state)
        {
            var errors = new List<string>();
            GetServiceType()
                .GetMethod("ValidateGeneratedMaterials", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [state, errors, CancellationToken.None, null]);
            return errors;
        }

        [Test]
        public void Validation_CoversRepackMaterialsLikeAtlasMaterials()
        {
            var fixture = new DownscaleFixture();
            fixture.AddTexture("textures\\t_ok.dds", 256, 9);
            fixture.AddTexture(RepackTexture, 256, 9);
            fixture.AddMaterial(RepackMaterial, ("t_xml_base_colour", RepackTexture), ("t_xml_mask", "textures\\t_ok.dds"));
            fixture.AddMaterial("materials\\sr_m_repack_bad.xml.material", ("t_xml_base_colour", "textures\\gone.dds"));
            fixture.AddMaterial("materials\\sr_m_atlas_bad.xml.material", ("t_xml_base_colour", "textures\\gone_too.dds"));
            var (service, state) = CreateDownscaleState(fixture, false, buildAtlases: false);
            AddToSet(state, "SharedRepackMaterialPaths", RepackMaterial);

            Assert.That(RunGeneratedMaterialValidation(service, state), Is.Empty);

            AddToSet(state, "SharedRepackMaterialPaths", "materials\\sr_m_repack_bad.xml.material");
            AddToSet(state, "GeneratedMaterialPaths", "materials\\sr_m_atlas_bad.xml.material");
            // A repack material that vanished is ValidateSharedTextureRepack's finding, not a duplicate here.
            AddToSet(state, "SharedRepackMaterialPaths", "materials\\sr_m_repack_missing.xml.material");
            AddToSet(state, "GeneratedMaterialPaths", "materials\\sr_m_atlas_missing.xml.material");

            var errors = RunGeneratedMaterialValidation(service, state);

            Assert.That(errors, Has.Some.EqualTo(
                "Generated repack material texture no longer resolves: materials\\sr_m_repack_bad.xml.material -> textures\\gone.dds"));
            Assert.That(errors, Has.Some.EqualTo(
                "Generated material texture no longer resolves: materials\\sr_m_atlas_bad.xml.material -> textures\\gone_too.dds"));
            Assert.That(errors, Has.Some.EqualTo(
                "Generated atlas material is missing: materials\\sr_m_atlas_missing.xml.material"));
            Assert.That(errors.Any(error => error.Contains("sr_m_repack_missing")), Is.False);
            Assert.That(errors, Has.Count.EqualTo(3));
        }

        private static (DownscaleFixture Fixture, object Service, object State) BuildRepackPassThroughState(
            bool texturePresentInSource)
        {
            var fixture = new DownscaleFixture();
            fixture.AddTexture(RepackTexture, 256, 9);
            const string passThrough = "textures\\ca_leftover_mask.dds";
            if (texturePresentInSource)
                fixture.AddTexture(passThrough, 256, 9);
            const string source = "materials\\sr_m_source.xml.material";
            fixture.AddMaterial(source, ("t_xml_base_colour", "textures\\sr_orig.dds"), ("t_xml_mask", passThrough));
            fixture.AddMaterial(RepackMaterial, ("t_xml_base_colour", RepackTexture), ("t_xml_mask", passThrough));
            var (service, state) = CreateDownscaleState(fixture, false, buildAtlases: false);
            AddToSet(state, "SharedRepackMaterialPaths", RepackMaterial);
            AddToSet(state, "SharedRepackTexturePaths", RepackTexture);
            Get<IDictionary>(state, "SharedRepackMaterialSources")[RepackMaterial] = source;
            return (fixture, service, state);
        }

        [Test]
        public void Validation_RepackMaterial_PassThroughTextureAlreadyMissingInTheSource_IsNotAnError()
        {
            var (_, service, state) = BuildRepackPassThroughState(texturePresentInSource: false);

            var errors = RunGeneratedMaterialValidation(service, state);

            Assert.That(errors, Is.Empty);
            Assert.That(
                Get<IList>(state, "SharedRepackKeptMissingTextures").Count,
                Is.EqualTo(1));
            SetStateProperty(state, "SharedTextureRepackEnabled", true);
            SetStateProperty(state, "SharedTextureRepack", Activator.CreateInstance(GetNestedType("SharedTextureRepackResult")));
            Assert.That(
                BuildSharedRepackSection(state),
                Does.Contain("already missing in the source material (kept as authored)")
                    .And.Contain("ca_leftover_mask.dds"));
        }

        [Test]
        public void Validation_RepackMaterial_PassThroughTextureThatExistedInTheSourceButIsGone_IsStillAnError()
        {
            var (fixture, service, state) = BuildRepackPassThroughState(texturePresentInSource: true);
            fixture.Output.Remove("textures\\ca_leftover_mask.dds");

            var errors = RunGeneratedMaterialValidation(service, state);

            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0], Does.Contain("Generated repack material texture no longer resolves"));
            Assert.That(Get<IList>(state, "SharedRepackKeptMissingTextures"), Is.Empty);
        }

        [Test]
        public void Validation_RepackMaterial_GeneratedTextureMissing_IsAlwaysAnError()
        {
            var (fixture, service, state) = BuildRepackPassThroughState(texturePresentInSource: false);
            fixture.Output.Remove(RepackTexture);
            fixture.Source.Remove(RepackTexture);

            var errors = RunGeneratedMaterialValidation(service, state);

            Assert.That(errors, Has.Some.Contain($"-> {RepackTexture}"));
        }

        [Test]
        public void SharedRepack_Outputs_AreRegisteredSeparatelyFromTheAtlasOutputs()
        {
            var world = BuildStandardSrWorld();

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            Assert.That(
                Get<HashSet<string>>(world.State, "SharedRepackMaterialPaths"),
                Is.EquivalentTo(Get<List<string>>(result, "GeneratedMaterials")));
            Assert.That(
                Get<HashSet<string>>(world.State, "SharedRepackTexturePaths"),
                Is.EquivalentTo(Get<List<string>>(result, "GeneratedTextures")));
            // The report's atlas counts and lists read these sets: repack outputs stay out.
            Assert.That(Get<HashSet<string>>(world.State, "GeneratedMaterialPaths"), Is.Empty);
            Assert.That(Get<HashSet<string>>(world.State, "GeneratedTexturePaths"), Is.Empty);
        }

        [Test]
        public void SharedRepack_OptionOff_ReportSaysSoAndNothingIsRun()
        {
            var world = BuildStandardSrWorld();
            world.Analyse();
            var sourceBefore = SrSnapshot(world.Fixture.Source);
            var outputBefore = SrSnapshot(world.Fixture.Output);

            var off = BuildSharedRepackSection(world.State);
            SetStateProperty(world.State, "SharedTextureRepackRequested", true);
            var ignored = BuildSharedRepackSection(world.State);

            Assert.That(off, Does.Contain("Shared texture repack: NO"));
            Assert.That(off, Does.Contain("Off (opt-in). No pack file was changed."));
            Assert.That(ignored, Does.Contain("Requested but ignored"));
            Assert.That(Get<object?>(world.State, "SharedTextureRepack"), Is.Null);
            Assert.That(world.Fixture.Output.Count, Is.EqualTo(outputBefore.Count));
            foreach (var (path, bytes) in sourceBefore)
                Assert.That(world.Fixture.Source[path], Is.EqualTo(bytes));
        }

        private static string BuildSharedRepackSection(object state)
        {
            var builder = new StringBuilder();
            GetStaticMethod("AppendSharedTextureRepackSection").Invoke(null, [builder, state]);
            return builder.ToString();
        }

        [Test]
        public void SharedRepack_ComposesWithStructuralMerge_AndKeepsTheRepackedUvs()
        {
            var world = new SrWorld();
            world.AddTexture("textures\\sr_b1.dds", 1024, "BC1", true, 1);
            world.AddTexture("textures\\sr_n1.dds", 512, "BC3", false, 2);
            world.Fixture.AddMaterial(SrMaterialPath("m1"), (SrBase, "textures\\sr_b1.dds"), (SrNormal, "textures\\sr_n1.dds"));
            var m1 = SrMaterialPath("m1");
            RmvFile Build()
            {
                RmvModel Part(float seed, int blockX, int blockY)
                {
                    var model = CreateWeightedModel(seed);
                    var uv = SrBlock(blockX, blockY);
                    var corners = new[]
                    {
                        new Vector2(uv[0][0], uv[0][1]), new Vector2(uv[0][2], uv[0][3]),
                        new Vector2(uv[0][4], uv[0][5]), new Vector2(uv[1][4], uv[1][5]),
                    };
                    for (var vertex = 0; vertex < 4; vertex++)
                        model.Mesh.VertexList[vertex].Uv = corners[vertex];
                    return model;
                }

                return CreateSingleLodRigid(RmvVersionEnum.RMV2_V8, Part(1f, 2, 3), Part(2f, 3, 3));
            }

            world.AddModel(SrRigid, SrWs, SrRoot, Build, [[m1, m1]]);
            var result = world.Run();
            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            var rigid = (RmvFile)Get<IDictionary>(world.State, "RigidModels")[SrRigid]!;
            var repackedUvs = rigid.ModelList[0].SelectMany(model => model.Mesh.VertexList.Select(vertex => vertex.Uv)).ToList();
            var repackedMaterial = SrWsMaterial(world.State, SrWs, 0, 0);
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 1), Is.EqualTo(repackedMaterial));

            InvokeServiceMethod(world.Service, "AnalyzeCompatibleMeshMerges", world.State, CancellationToken.None, null);
            InvokeServiceMethod(world.Service, "MergeCompatibleMeshes", world.State, CancellationToken.None, null);

            // The two parts now share one generated material, so they merge; UVs survive.
            Assert.That(rigid.ModelList[0], Has.Length.EqualTo(1));
            Assert.That(
                rigid.ModelList[0][0].Mesh.VertexList.Select(vertex => vertex.Uv),
                Is.EqualTo(repackedUvs));
            Assert.That(
                ReadWsAssignments((XmlDocument)Get<IDictionary>(world.State, "WsDocuments")[SrWs]!, rigid)[0],
                Is.EqualTo(new[] { repackedMaterial }));
            rigid.RecalculateOffsets();
            var bytes = ModelFactory.Create().Save(rigid, validateByReloading: true, logProgress: false);
            Assert.That(ModelFactory.Create().Load(bytes).ModelList[0], Has.Length.EqualTo(1));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_ComposesWithDownscale_GeneratedTexturesAreNeverDownscaled()
        {
            var world = BuildStandardSrWorld();
            world.Downscale = true;
            world.Run();
            var generated = Get<HashSet<string>>(world.State, "SharedRepackTexturePaths").ToList();
            Assert.That(generated, Has.Count.EqualTo(2));
            var before = generated.ToDictionary(path => path, path => world.Fixture.Output[path].ToArray());

            var downscale = RunDownscale(world.Service, world.State, world.Fixture);

            Assert.That(Get<bool>(downscale, "Applied"), Is.True);
            foreach (var path in generated)
            {
                Assert.That(Candidates(downscale).Select(item => Get<string>(item, "Path")), Has.None.EqualTo(path));
                Assert.That(world.Fixture.Output[path], Is.EqualTo(before[path]), path);
            }

            Assert.That(
                Get<HashSet<string>>(world.State, "SharedRepackMaterialPaths").Count,
                Is.EqualTo(2));
        }

        [Test]
        public void SharedRepack_TwoRuns_ProduceIdenticalBytes()
        {
            var first = BuildStandardSrWorld();
            var second = BuildStandardSrWorld();
            first.Run();
            second.Run();

            var generated = first.Fixture.Output.Keys.Where(key => key.Contains("_repack_")).OrderBy(key => key).ToList();
            Assert.That(generated, Has.Count.EqualTo(4));
            Assert.That(
                second.Fixture.Output.Keys.Where(key => key.Contains("_repack_")).OrderBy(key => key),
                Is.EqualTo(generated));
            foreach (var path in generated)
                Assert.That(second.Fixture.Output[path], Is.EqualTo(first.Fixture.Output[path]), path);
            var firstRigid = (RmvFile)Get<IDictionary>(first.State, "RigidModels")[SrRigid]!;
            var secondRigid = (RmvFile)Get<IDictionary>(second.State, "RigidModels")[SrRigid]!;
            Assert.That(
                secondRigid.ModelList.SelectMany(lod => lod).SelectMany(model => model.Mesh.VertexList.Select(vertex => vertex.Uv)),
                Is.EqualTo(firstRigid.ModelList.SelectMany(lod => lod).SelectMany(model => model.Mesh.VertexList.Select(vertex => vertex.Uv))));
        }

        [Test]
        public void SharedRepack_Validation_FailsOnACorruptedGeneratedTexture()
        {
            var world = BuildStandardSrWorld();
            world.Run();
            var path = Get<HashSet<string>>(world.State, "SharedRepackTexturePaths").First();
            world.Fixture.Output[path] = world.Fixture.Output[path].Take(300).ToArray();

            var errors = SrValidate(world);

            Assert.That(errors, Has.Some.Contain(path));
        }

        [Test]
        public void SharedRepack_Validation_FailsOnAMissingGeneratedMaterial()
        {
            var world = BuildStandardSrWorld();
            world.Run();
            var material = Get<HashSet<string>>(world.State, "SharedRepackMaterialPaths").First();
            world.Fixture.Output.Remove(material);

            var errors = SrValidate(world);

            Assert.That(errors, Has.Some.Contain("material is missing"));
        }

        [Test]
        public void SharedRepack_GeometryInvariants_DetectAChangedPositionOrAnUnmovedUv()
        {
            var factory = SrRigidFactory([("m", SrBlocks((1, 1))), ("m", SrBlocks((2, 2)))]);
            var rigid = factory();
            var snapshot = GetStaticMethod("SnapshotSharedRepackRigid").Invoke(null, [rigid])!;
            var errors = new List<string>();

            void Compare(HashSet<(int, int)> moved, HashSet<(int, int)> directEmbedded)
                => GetStaticMethod("CompareSharedRepackSnapshot")
                    .Invoke(null, ["r", rigid, snapshot, moved, directEmbedded, errors]);

            Compare([(0, 0), (0, 1)], []);
            Assert.That(errors, Is.Empty);

            rigid.ModelList[0][1].Mesh.VertexList[0].Uv = new Vector2(0.5f, 0.5f);
            Compare([(0, 0)], []);
            Assert.That(errors, Has.Some.Contain("UV of an unmoved part changed"));

            errors.Clear();
            rigid.ModelList[0][0].Mesh.VertexList[2].Position = new Vector4(9, 9, 9, 1);
            Compare([(0, 0), (0, 1)], []);
            Assert.That(errors, Has.Some.Contain("position of vertex 2 changed"));

            errors.Clear();
            rigid.ModelList[0][0].Mesh.VertexList[1].Uv = new Vector2(1.5f, 0.2f);
            rigid.ModelList[0][0].Mesh.VertexList[2].Position = factory().ModelList[0][0].Mesh.VertexList[2].Position;
            Compare([(0, 0), (0, 1)], []);
            Assert.That(errors, Has.Some.Contain("outside [0, 1]"));
        }

        [Test]
        public void SharedRepack_SnapshotOnlyAllowsEmbeddedPathChangesForDirectMaterialParts()
        {
            var model = CreateUvPart(SrBlock(0, 0));
            var material = new WeightedMaterial
            {
                BinaryVertexFormat = VertexFormat.Weighted,
                MaterialId = ModelMaterialEnum.weighted,
                ModelName = "snapshot",
            };
            material.SetTexture(TextureType.BaseColour, "textures\\before.dds");
            model.Material = material;
            var rigid = CreateSingleLodRigid(RmvVersionEnum.RMV2_V8, model);
            var snapshot = GetStaticMethod("SnapshotSharedRepackRigid").Invoke(null, [rigid])!;
            material.SetTexture(TextureType.BaseColour, "textures\\after.dds");

            var errors = new List<string>();
            GetStaticMethod("CompareSharedRepackSnapshot")
                .Invoke(null, ["r", rigid, snapshot, new HashSet<(int, int)> { (0, 0) },
                    new HashSet<(int, int)>(), errors]);
            Assert.That(errors, Has.Some.Contain("embedded material texture paths changed unexpectedly"));

            errors.Clear();
            GetStaticMethod("CompareSharedRepackSnapshot")
                .Invoke(null, ["r", rigid, snapshot, new HashSet<(int, int)> { (0, 0) },
                    new HashSet<(int, int)> { (0, 0) }, errors]);
            Assert.That(errors, Is.Empty);
        }

        [Test]
        public void SharedRepack_Layout_PackerIsDeterministicAndOverlapFree()
        {
            (int Width, int Height)[] sizes = [(80, 80), (144, 80), (64, 256), (32, 32), (200, 40)];
            var arguments = new object?[] { sizes.ToList(), 4096, 4096, 0, 0, null };

            var packed = (bool)GetStaticMethod("TryPackSharedRepackLayout").Invoke(null, arguments)!;
            var canvasWidth = (int)arguments[3]!;
            var canvasHeight = (int)arguments[4]!;
            var positions = ((int X, int Y)[])arguments[5]!;
            var again = new object?[] { sizes.ToList(), 4096, 4096, 0, 0, null };
            GetStaticMethod("TryPackSharedRepackLayout").Invoke(null, again);

            Assert.That(packed, Is.True);
            Assert.That(positions, Is.EqualTo(((int X, int Y)[])again[5]!));
            Assert.That(System.Numerics.BitOperations.IsPow2(canvasWidth), Is.True);
            Assert.That(System.Numerics.BitOperations.IsPow2(canvasHeight), Is.True);
            for (var left = 0; left < sizes.Length; left++)
            {
                Assert.That(positions[left].X + sizes[left].Width, Is.LessThanOrEqualTo(canvasWidth));
                Assert.That(positions[left].Y + sizes[left].Height, Is.LessThanOrEqualTo(canvasHeight));
                for (var right = left + 1; right < sizes.Length; right++)
                {
                    var overlaps = positions[left].X < positions[right].X + sizes[right].Width &&
                                   positions[right].X < positions[left].X + sizes[left].Width &&
                                   positions[left].Y < positions[right].Y + sizes[right].Height &&
                                   positions[right].Y < positions[left].Y + sizes[left].Height;
                    Assert.That(overlaps, Is.False, $"{left} overlaps {right}");
                }
            }
        }

        [Test]
        public void SharedRepack_Layout_ReportsFailureWhenNothingFits()
        {
            (int Width, int Height)[] sizes = [(4096, 4096), (4096, 4096)];
            var arguments = new object?[] { sizes.ToList(), 4096, 4096, 0, 0, null };

            Assert.That((bool)GetStaticMethod("TryPackSharedRepackLayout").Invoke(null, arguments)!, Is.False);
        }

        [Test]
        public void SharedRepack_Charts_ComponentsAreEightConnectedAndConservative()
        {
            const int grid = 16;
            var cells = new bool[grid * grid];
            // A triangle in the lower-left and a separate one in the upper-right.
            GetStaticMethod("MarkSharedRepackTriangle")
                .Invoke(null, [cells, grid, grid, 0.0, 0.0, 0.25, 0.0, 0.0, 0.25]);
            GetStaticMethod("MarkSharedRepackTriangle")
                .Invoke(null, [cells, grid, grid, 0.75, 0.75, 1.0, 0.75, 1.0, 1.0]);
            var arguments = new object?[] { cells, grid, grid, null };

            var boxes = ((IEnumerable)GetStaticMethod("FindSharedRepackComponents").Invoke(null, arguments)!)
                .Cast<object>().ToList();

            Assert.That(boxes, Has.Count.EqualTo(2));
            Assert.That(cells[0], Is.True);
            Assert.That(cells[grid * grid - 1], Is.True);
            Assert.That(cells[8 * grid + 8], Is.False);
            var label = (int[])arguments[3]!;
            Assert.That(label[0], Is.EqualTo(0));
            Assert.That(label[grid * grid - 1], Is.EqualTo(1));
        }

        [Test]
        public void SharedRepack_ReportSection_ListsFunnelBuiltSetsAndEverySkip()
        {
            var world = BuildStandardSrWorld();
            world.Run();
            SetStateProperty(world.State, "SharedTextureRepackRequested", true);
            SetStateProperty(world.State, "SharedTextureRepackEnabled", true);
            GetStaticMethod("FinalizeSharedTextureRepack").Invoke(null, [world.State]);

            var report = BuildSharedRepackSection(world.State);

            Assert.That(report, Does.Contain("Shared texture repack: YES"));
            Assert.That(report, Does.Contain("Signature sets considered: 1"));
            Assert.That(report, Does.Contain("Built: 1"));
            Assert.That(report, Does.Contain("(reconciles)"));
            Assert.That(report, Does.Contain("units in signature 1"));
            Assert.That(report, Does.Contain("block-copied"));
            Assert.That(report, Does.Contain("pixel check (before writing):"));
            Assert.That(report, Does.Contain("layout group"));
            Assert.That(report, Does.Contain("Owned source textures replaced: 0"));
            Assert.That(report, Does.Contain("Skipped sets (0)"));
            TestContext.Out.WriteLine(report);
        }

        [Test]
        public void SharedRepack_ReportSection_HasAPackingSimulationThatMatchesThePlannersChartModel()
        {
            var world = BuildStandardSrWorld();
            world.Run();
            SetStateProperty(world.State, "SharedTextureRepackRequested", true);
            SetStateProperty(world.State, "SharedTextureRepackEnabled", true);
            GetStaticMethod("FinalizeSharedTextureRepack").Invoke(null, [world.State]);

            var report = BuildSharedRepackSection(world.State);

            Assert.That(report, Does.Contain("Packing simulation (report only, power-of-two pages):"));
            Assert.That(report, Does.Contain("group failures 0;"));
            Assert.That(report, Does.Contain("baseline-model mismatches 0 chart(s)"));
            Assert.That(report, Does.Not.Contain("same rectangles"));
            Assert.That(report, Does.Contain("MaxRects/skyline, align 16"));
            Assert.That(report, Does.Contain("already packed with the best of shelf, MaxRects and skyline"));
            Assert.That(report, Does.Contain("tight pieces, gutter 4 + align 4"));
        }

        private static readonly (string Slot, string Texture)[] SrPassThrough =
        [
            ("t_xml_blood_map", "commontextures\\bloodmap.dds"),
            ("t_xml_dither", "commontextures\\dither.dds"),
            ("t_xml_distortion_noise", "commontextures\\winds_of_magic_noise.dds"),
            ("t_xml_distortion", "commontextures\\winds_of_magic_specular.dds"),
        ];

        private static string SrMaterialXml(
            string b,
            string n,
            IEnumerable<(string Slot, string Texture)>? extra = null,
            string? blood = null)
        {
            var textures = new List<(string Slot, string Texture)> { (SrBase, b), (SrNormal, n) };
            textures.AddRange(extra ?? []);
            var xml = DownscaleMaterialXml(textures.ToArray());
            if (blood != null)
            {
                xml = xml.Replace(
                    "</material>",
                    $"<params><param><name>blood_uv_scale</name><type>float2</type><value>{blood}</value></param></params></material>");
            }

            return xml;
        }

        private static SrWorld BuildPassThroughWorld(string? blood, IEnumerable<(string Slot, string Texture)>? extra)
        {
            var world = BuildStandardSrWorld(secondLod: false);
            foreach (var (_, texture) in SrPassThrough)
                world.AddTexture(texture, 256, "BC1", true, 9);
            world.Fixture.AddText(
                SrMaterialPath("m1"),
                SrMaterialXml("textures\\sr_b1.dds", "textures\\sr_n1.dds", extra, blood));
            return world;
        }

        private static XmlNode SrSlotNode(string xml, string slot)
        {
            var document = new XmlDocument();
            document.LoadXml(xml);
            return document.SelectNodes("/material/textures/texture")!.Cast<XmlNode>()
                .Single(node => node.SelectSingleNode("slot")!.InnerText.Trim() == slot);
        }

        [Test]
        public void SharedRepack_PassThroughSlots_AreKeptAndBloodScaleIsDividedPerAxis()
        {
            var world = BuildPassThroughWorld("2,3", SrPassThrough);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            var built = SrBuilt(result).Single();
            var generatedPath = SrWsMaterial(world.State, SrWs, 0, 0);
            var generated = Encoding.UTF8.GetString(world.Fixture.Output[generatedPath]);
            var original = Encoding.UTF8.GetString(world.Fixture.Source[SrMaterialPath("m1")]);
            foreach (var (slot, _) in SrPassThrough)
                Assert.That(SrSlotNode(generated, slot).OuterXml, Is.EqualTo(SrSlotNode(original, slot).OuterXml));

            // Reference 1024x1024 over the layout: a non-square layout gives different scales.
            var scaleU = 1024.0 / Get<int>(built, "LayoutWidth");
            var scaleV = 1024.0 / Get<int>(built, "LayoutHeight");
            var expected = string.Create(
                System.Globalization.CultureInfo.InvariantCulture, $"{2 / scaleU:R},{3 / scaleV:R}");
            var document = new XmlDocument();
            document.LoadXml(generated);
            Assert.That(
                document.SelectSingleNode("/material/params/param/value")!.InnerText,
                Is.EqualTo(expected));
            Assert.That(Get<IEnumerable>(built, "BloodScales").Cast<object>().Count(), Is.EqualTo(1));
            Assert.That(SrValidate(world), Is.Empty);
            AssertSrPixels(world, SrRigid, SrWs, BuildStandardSrWorld(false).Rigids[SrRigid], SrPart0Seeds, (_, part) => part == 0);
            var section = BuildSharedRepackSection(Enabled(world));
            Assert.That(section, Does.Contain("blood: ").And.Contain("blood_uv_scale"));
            Assert.That(section, Does.Contain("t_xml_dither").And.Contain("ASSUMPTION"));
        }

        private static object Enabled(SrWorld world)
        {
            SetStateProperty(world.State, "SharedTextureRepackEnabled", true);
            return world.State;
        }

        [Test]
        public void SharedRepack_MaterialWithoutBloodScale_ChangesOnlyTexturePaths()
        {
            var world = BuildPassThroughWorld(null, SrPassThrough);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            var generated = Encoding.UTF8.GetString(
                world.Fixture.Output[SrWsMaterial(world.State, SrWs, 0, 0)]);
            var expected = Encoding.UTF8.GetString(world.Fixture.Source[SrMaterialPath("m1")]);
            foreach (var slot in new[] { SrBase, SrNormal })
            {
                var path = SrSlotNode(generated, slot).SelectSingleNode("source")!.InnerText;
                expected = expected.Replace(
                    SrSlotNode(expected, slot).SelectSingleNode("source")!.InnerText, path);
            }

            Assert.That(generated, Is.EqualTo(expected));
            Assert.That(Get<IEnumerable>(SrBuilt(result).Single(), "BloodScales").Cast<object>(), Is.Empty);
        }

        private const string SrWeighted2Shader = "shaders\\weighted2_character.xml.shader";

        // A world whose material m1 uses the given shader and extra (possibly repeated) slots.
        private static SrWorld BuildShaderSlotWorld(string shader, params (string Slot, string Texture)[] extra)
        {
            var world = BuildStandardSrWorld(secondLod: false);
            foreach (var (_, texture) in SrPassThrough.Concat(extra))
                world.AddTexture(texture, 256, "BC1", true, 9);
            world.Fixture.AddText(
                SrMaterialPath("m1"),
                SrMaterialXml("textures\\sr_b1.dds", "textures\\sr_n1.dds", SrPassThrough.Concat(extra))
                    .Replace(SrWeighted2Shader, shader));
            return world;
        }

        [Test]
        public void SharedRepack_SlotTheShaderNeverBinds_IsIgnored_KeptAsAuthored_AndReported()
        {
            // weighted2_character binds no t_xml_colour_map (compiled-shader table).
            var world = BuildShaderSlotWorld(SrWeighted2Shader, ("t_xml_colour_map", "textures\\sr_cm.dds"));

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result) + SrPartDropReasons(result));
            Assert.That(SrPartDropReasons(result), Does.Not.Contain("non-atlasable slot"));
            var generated = Encoding.UTF8.GetString(world.Fixture.Output[SrWsMaterial(world.State, SrWs, 0, 0)]);
            var original = Encoding.UTF8.GetString(world.Fixture.Source[SrMaterialPath("m1")]);
            Assert.That(SrSlotNode(generated, "t_xml_colour_map").OuterXml,
                Is.EqualTo(SrSlotNode(original, "t_xml_colour_map").OuterXml));
            var unread = Get<SortedDictionary<string, HashSet<string>>>(result, "UnreadSlotMaterials");
            Assert.That(unread.Keys, Is.EqualTo(new[] { "t_xml_colour_map" }));
            Assert.That(unread["t_xml_colour_map"], Has.Count.EqualTo(1));
            var section = BuildSharedRepackSection(Enabled(world));
            Assert.That(section, Does.Contain("Slots ignored because the material's shader never binds them (built sets): t_xml_colour_map in 1 material(s)"));
            Assert.That(section, Does.Contain("ignored as unread by the material's shader: t_xml_colour_map (1 material(s))"));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_SlotTheShaderBinds_StillDropsThePart()
        {
            // rigid_random_colours binds t_xml_colour_map.
            var world = BuildShaderSlotWorld(
                "shaders/rigid_random_colours.xml.shader", ("t_xml_colour_map", "textures\\sr_cm.dds"));

            var result = world.Run();

            // Only the part using m1 is dropped; the other material's part still repacks.
            Assert.That(SrPartDropReasons(result), Does.Contain("non-atlasable slot t_xml_colour_map"));
            Assert.That(Get<SortedDictionary<string, HashSet<string>>>(result, "UnreadSlotMaterials"), Is.Empty);
        }

        [Test]
        public void SharedRepack_ConflictingDuplicatesOfAnUnreadSlot_NoLongerRefuse_ButOfAReadSlotStillDo()
        {
            var duplicates = new[]
            {
                ("t_xml_colour_map", "textures\\sr_cm.dds"),
                ("t_xml_colour_map", "textures\\sr_cm2.dds"),
            };
            var unread = BuildShaderSlotWorld(SrWeighted2Shader, duplicates);
            var unreadResult = unread.Run();
            Assert.That(Get<int>(unreadResult, "Built"), Is.EqualTo(1), SrSkips(unreadResult) + SrPartDropReasons(unreadResult));
            Assert.That(SrPartDropReasons(unreadResult), Does.Not.Contain("more than once"));

            var read = BuildShaderSlotWorld("shaders/rigid_random_colours.xml.shader", duplicates);
            var readResult = read.Run();
            Assert.That(SrPartDropReasons(readResult), Does.Contain("more than once with different sources"));
        }

        [Test]
        public void SharedRepack_UnknownExtraSlot_StillDropsThePart_AndTheReportNamesIt()
        {
            // The material's shader is not in the compiled-shader table, so no slot is ever treated as unread.
            var world = BuildShaderSlotWorld(
                "shaders/not_in_the_table_test.xml.shader", ("t_xml_colour_map", "textures\\sr_cm.dds"));

            var result = world.Run();

            var reasons = Get<Dictionary<string, int>>(result, "PartDropReasons");
            Assert.That(reasons.Keys, Has.Some.Contain("slot t_xml_colour_map"));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Is.EqualTo(SrMaterialPath("m1")));
            Assert.That(BuildSharedRepackSection(Enabled(world)), Does.Contain("1 x a bound material has a texture in the non-atlasable slot t_xml_colour_map"));
            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
        }

        [Test]
        public void SharedRepack_VanillaOwnedMaterials_AreCopiedToAModOwnedFolder()
        {
            var world = BuildStandardSrWorld(secondLod: false);
            foreach (var name in new[] { "m1", "m2" })
            {
                var path = SrMaterialPath(name);
                world.Fixture.Vanilla[path] = world.Fixture.Source[path];
                world.Fixture.Source.Remove(path);
                world.Fixture.Output.Remove(path);
            }

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            foreach (var part in new[] { 0, 1 })
            {
                var generated = SrWsMaterial(world.State, SrWs, 0, part);
                Assert.That(generated, Does.StartWith("variantmeshes\\asset_editor\\repack_materials\\"));
                Assert.That(world.Fixture.Vanilla.ContainsKey(generated), Is.False);
                Assert.That(world.Fixture.Output.ContainsKey(generated), Is.True);
            }

            Assert.That(world.Fixture.Output.ContainsKey(SrMaterialPath("m1")), Is.False);
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_DifferentMaterialsNamingTheSameTextures_AcrossLodsAgree()
        {
            var world = BuildStandardSrWorld();
            // LOD 1 uses a sibling material (another shader) with the same textures.
            world.Fixture.AddText(
                SrMaterialPath("m1_lod2"),
                Encoding.UTF8.GetString(world.Fixture.Source[SrMaterialPath("m1")])
                    .Replace("weighted2_character", "weighted4_character"));
            var m1 = SrMaterialPath("m1");
            var m1b = SrMaterialPath("m1_lod2");
            var m2 = SrMaterialPath("m2");
            world.AddModel(
                SrRigid, SrWs, SrRoot,
                SrRigidFactory(
                    [(m1, SrBlocks((2, 3), (3, 3))), (m2, SrBlocks((10, 4)))],
                    [(m1b, SrBlocks((2, 3))), (m2, SrBlocks((10, 4)))]),
                [[m1, m2], [m1b, m2]]);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            Assert.That(Get<Dictionary<string, int>>(result, "PartDropReasons"), Is.Empty);
            var lod0 = SrWsMaterial(world.State, SrWs, 0, 0);
            var lod1 = SrWsMaterial(world.State, SrWs, 1, 0);
            Assert.That(lod0, Is.Not.EqualTo(lod1));
            Assert.That(Encoding.UTF8.GetString(world.Fixture.Output[lod1]), Does.Contain("weighted4_character"));
            Assert.That(Get<int>(SrBuilt(result).Single(), "Tuples"), Is.EqualTo(2));
            AssertSrPixels(world, SrRigid, SrWs, world.Rigids[SrRigid], SrPart0Seeds, (_, part) => part == 0);
            Assert.That(SrValidate(world), Is.Empty);
        }

        private static byte[] BuildTransparentContentDds(int size, int seed, int transparentBlockX, int transparentBlockY)
        {
            var bgra = new byte[size * size * 4];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var (r, g, b) = SrColour(seed, x * 16 / size, y * 16 / size);
                    var offset = (y * size + x) * 4;
                    bgra[offset] = b;
                    bgra[offset + 1] = g;
                    bgra[offset + 2] = r;
                    bgra[offset + 3] = x * 16 / size == transparentBlockX && y * 16 / size == transparentBlockY
                        ? (byte)0
                        : (byte)255;
                }
            }

            var arguments = new object?[] { bgra, size, size, "BC1", true, true, null, null };
            Assert.That((bool)GetStaticMethod("TryEncodeSharedRepackDds").Invoke(null, arguments)!, Is.True);
            return (byte[])arguments[6]!;
        }

        private static (byte[] Bgra, int Width, int Height) DecodeDdsLevel(byte[] dds, int level)
        {
            if (level == 0)
                return DecodeDds(dds);

            var layout = GetLayout(dds);
            var arguments = new object?[] { dds, layout, level, null, null };
            Assert.That((bool)GetStaticMethod("TrySliceDdsMips").Invoke(null, arguments)!, Is.True, (string?)arguments[4]);
            return DecodeDds((byte[])arguments[3]!);
        }

        private static long DdsBlockOffset(byte[] dds, int level, int blockX, int blockY, int bytesPerBlock)
        {
            var layout = GetLayout(dds);
            var offset = (long)Get<int>(layout, "HeaderSize");
            var width = Get<int>(layout, "Width");
            for (var index = 0; index < level; index++)
                offset += (long)layout.GetType().GetMethod("LevelBytes")!.Invoke(layout, [index])!;
            var blocksWide = (Math.Max(1, width >> level) + 3) / 4;
            return offset + ((long)blockY * blocksWide + blockX) * bytesPerBlock;
        }

        // Point of the first triangle of a part in the old and the rewritten mesh.
        private static ((double U, double V) Old, (double U, double V) New) SrFirstTriangleCentroid(
            SrWorld world, string rigidPath, Func<RmvFile> original, int part)
        {
            var oldMesh = original().ModelList[0][part].Mesh;
            var newMesh = ((RmvFile)Get<IDictionary>(world.State, "RigidModels")[rigidPath]!).ModelList[0][part].Mesh;
            double ou = 0, ov = 0, nu = 0, nv = 0;
            for (var corner = 0; corner < 3; corner++)
            {
                var vertex = oldMesh.IndexList[corner];
                ou += oldMesh.VertexList[vertex].Uv.X / 3.0;
                ov += oldMesh.VertexList[vertex].Uv.Y / 3.0;
                nu += newMesh.VertexList[vertex].Uv.X / 3.0;
                nv += newMesh.VertexList[vertex].Uv.Y / 3.0;
            }

            return ((ou - Math.Floor(ou), ov - Math.Floor(ov)), (nu, nv));
        }

        [Test]
        public void SharedRepack_BlockCopy_GivesByteIdenticalBlocksAndAnExactPixelCheck()
        {
            var world = BuildStandardSrWorld(secondLod: false);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            var built = SrBuilt(result).Single();
            Assert.That(Get<double>(built, "MaxError"), Is.Zero);
            var outputs = Get<IEnumerable>(built, "Outputs").Cast<object>().ToList();
            Assert.That(outputs.Select(output => Get<int>(output, "BlockCopiedLevels")), Has.All.GreaterThanOrEqualTo(1));
            var baseOutput = outputs.Single(output => Get<string>(output, "Slot") == SrBase);
            var levels = Get<int>(baseOutput, "BlockCopiedLevels");
            var factory = BuildStandardSrWorld(false).Rigids[SrRigid];
            var (old, @new) = SrFirstTriangleCentroid(world, SrRigid, factory, 0);
            var source = world.Fixture.Vanilla["textures\\sr_b1.dds"];
            var generated = world.Fixture.Output[Get<string>(baseOutput, "Path")];
            var generatedDecoded = DecodeDds(generated);
            for (var level = 0; level < levels; level++)
            {
                var sourceSize = 1024 >> level;
                var generatedWidth = generatedDecoded.Width >> level;
                var generatedHeight = generatedDecoded.Height >> level;
                var from = DdsBlockOffset(source, level, (int)(old.U * sourceSize) / 4, (int)(old.V * sourceSize) / 4, 8);
                var to = DdsBlockOffset(
                    generated, level, (int)(@new.U * generatedWidth) / 4, (int)(@new.V * generatedHeight) / 4, 8);
                Assert.That(
                    generated.AsSpan((int)to, 8).ToArray(),
                    Is.EqualTo(source.AsSpan((int)from, 8).ToArray()),
                    $"level {level}");
            }

            Assert.That(BuildSharedRepackSection(Enabled(world)), Does.Contain($"{levels} of {Get<int>(baseOutput, "MipLevels")} mip level(s) block-copied"));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void SharedRepack_Bc7Set_PreservesFormatSrgbDx10MipsAndCopiedBlocks(bool srgb)
        {
            var world = BuildStandardSrWorld(
                secondLod: false,
                baseFormat: "BC7",
                baseSrgb: srgb,
                baseSize: 256);

            var result = world.Run(minimumSaving: 1, minimumGroupSaving: 1);

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            Assert.That(Get<int>(result, "FailedPixelCheck"), Is.Zero, SrSkips(result));
            var built = SrBuilt(result).Single();
            Assert.That(Get<double>(built, "MaxError"), Is.LessThanOrEqualTo(12));
            var baseOutputs = Get<IEnumerable>(built, "Outputs").Cast<object>()
                .Where(output => Get<string>(output, "Slot") == SrBase)
                .ToList();
            Assert.That(baseOutputs, Is.Not.Empty);
            Assert.That(baseOutputs.Select(output => Get<string>(output, "Format")), Has.All.EqualTo("BC7"));
            var baseOutput = baseOutputs.First(output =>
                Get<IEnumerable>(output, "Sources").Cast<string>()
                    .Contains("textures\\sr_b1.dds", StringComparer.OrdinalIgnoreCase));
            Assert.That(
                (Get<string>(baseOutput, "Format"), Get<bool>(baseOutput, "Srgb"), Get<bool>(baseOutput, "Dx10")),
                Is.EqualTo(("BC7", srgb, true)));
            Assert.That(Get<int>(baseOutput, "BlockCopiedLevels"), Is.GreaterThan(0));

            var generated = world.Fixture.Output[Get<string>(baseOutput, "Path")];
            var info = ReadDds(generated);
            Assert.That(
                (info.Format, info.Srgb, info.Dx10),
                Is.EqualTo(("BC7", srgb, true)));
            Assert.That(
                info.Mips,
                Is.EqualTo(1 + int.Log2(Math.Max(info.Width, info.Height))),
                "the generated BC7 output has a complete mip chain");

            var source = world.Fixture.Vanilla["textures\\sr_b1.dds"];
            var factory = world.Rigids[SrRigid];
            var (old, @new) = SrFirstTriangleCentroid(world, SrRigid, factory, 0);
            var copiedLevels = Get<int>(baseOutput, "BlockCopiedLevels");
            for (var level = 0; level < copiedLevels; level++)
            {
                var sourceSize = Math.Max(1, 256 >> level);
                var generatedWidth = Math.Max(1, info.Width >> level);
                var generatedHeight = Math.Max(1, info.Height >> level);
                var from = DdsBlockOffset(
                    source, level, (int)(old.U * sourceSize) / 4, (int)(old.V * sourceSize) / 4, 16);
                var to = DdsBlockOffset(
                    generated, level, (int)(@new.U * generatedWidth) / 4, (int)(@new.V * generatedHeight) / 4, 16);
                Assert.That(
                    generated.AsSpan((int)to, 16).ToArray(),
                    Is.EqualTo(source.AsSpan((int)from, 16).ToArray()),
                    $"BC7 {nameof(srgb)}={srgb}, level {level}");
            }

            var (samples, max) = AssertSrPixels(
                world, SrRigid, SrWs, factory, SrPart0Seeds, (_, part) => part == 0);
            Assert.That(samples, Is.GreaterThan(0));
            Assert.That(max, Is.LessThanOrEqualTo(12));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_Bc7NonPowerOfTwoTextureIsStillRefused()
        {
            var layout = GetLayout(BuildBcDds(256, 192, 9, "BC7", dx10: true, srgb: true));
            var reason = (string?)GetStaticMethod("GetSharedRepackBlockedTextureReason").Invoke(null, [layout]);

            Assert.That(reason, Does.Contain("not a power of two of at least 4"));
        }

        [Test]
        public void SharedRepack_MixedBc7AndBc1SourcesStayInSeparateFormatGroups()
        {
            var world = BuildStandardSrWorld(
                secondLod: false,
                baseSize: 256);
            world.Fixture.Vanilla["textures\\sr_b2.dds"] =
                BuildQuickBc7ContentDds(256, srgb: true, seed: 3);

            var result = world.Run(minimumSaving: 1, minimumGroupSaving: 1);

            Assert.That(Get<int>(result, "Built"), Is.GreaterThanOrEqualTo(1), SrSkips(result));
            AssertFunnelReconciles(result);

            string BaseSource(string materialPath)
            {
                var document = new XmlDocument();
                document.LoadXml(Encoding.UTF8.GetString(world.Fixture.Output[materialPath]));
                return document.SelectNodes("/material/textures/texture")!.Cast<XmlNode>()
                    .Single(node => node.SelectSingleNode("slot")!.InnerText.Trim() == SrBase)
                    .SelectSingleNode("source")!.InnerText;
            }

            var firstPath = BaseSource(SrWsMaterial(world.State, SrWs, 0, 0));
            var secondPath = BaseSource(SrWsMaterial(world.State, SrWs, 0, 1));
            Assert.That(firstPath, Is.Not.EqualTo(secondPath));
            Assert.That(ReadDds(world.Fixture.Output[firstPath]).Format, Is.EqualTo("BC1"));
            Assert.That(ReadDds(world.Fixture.Output[secondPath]).Format, Is.EqualTo("BC7"));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_Bc1PunchThroughAlpha_SurvivesAtTheTopLevelAndAtARe_EncodedLowerMip()
        {
            var world = BuildStandardSrWorld(secondLod: false);
            world.Fixture.Vanilla["textures\\sr_b1.dds"] = BuildTransparentContentDds(1024, 1, 2, 3);
            var source = DecodeDds(world.Fixture.Vanilla["textures\\sr_b1.dds"]);
            // Premise: the decoder reports the transparent block as alpha 0.
            Assert.That(source.Bgra[((3 * 64 + 10) * 1024 + 2 * 64 + 10) * 4 + 3], Is.Zero);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            var built = SrBuilt(result).Single();
            var baseOutput = Get<IEnumerable>(built, "Outputs").Cast<object>()
                .Single(output => Get<string>(output, "Slot") == SrBase);
            var generated = world.Fixture.Output[Get<string>(baseOutput, "Path")];
            var factory = BuildStandardSrWorld(false).Rigids[SrRigid];
            var oldMesh = factory().ModelList[0][0].Mesh;
            var newMesh = ((RmvFile)Get<IDictionary>(world.State, "RigidModels")[SrRigid]!).ModelList[0][0].Mesh;
            // First triangle lies in block (2,3): top level, and a level the block copy does not cover.
            var blockLevels = Get<int>(baseOutput, "BlockCopiedLevels");
            foreach (var level in new[] { 0, blockLevels })
            {
                var decoded = DecodeDdsLevel(generated, level);
                double nu = 0, nv = 0;
                for (var corner = 0; corner < 3; corner++)
                {
                    nu += newMesh.VertexList[oldMesh.IndexList[corner]].Uv.X / 3.0;
                    nv += newMesh.VertexList[oldMesh.IndexList[corner]].Uv.Y / 3.0;
                }

                var pixel = (Math.Clamp((int)(nv * decoded.Height), 0, decoded.Height - 1) * decoded.Width +
                             Math.Clamp((int)(nu * decoded.Width), 0, decoded.Width - 1)) * 4;
                Assert.That(decoded.Bgra[pixel + 3], Is.Zero, $"alpha at level {level}");
            }

            Assert.That(blockLevels, Is.LessThan(Get<int>(baseOutput, "MipLevels")));
        }

        private static void WithBeforeCheckHook(Action<object> hook, Action test)
        {
            var field = GetServiceType().GetField("SharedRepackBeforeCheckHook", BindingFlags.NonPublic | BindingFlags.Static)!;
            field.SetValue(null, hook);
            try
            {
                test();
            }
            finally
            {
                field.SetValue(null, null);
            }
        }

        private static List<object> HookOutputs(object plan)
            => Get<IEnumerable>(plan, "Pages").Cast<object>()
                .SelectMany(page => Get<IEnumerable>(page, "Outputs").Cast<object>()).ToList();

        [Test]
        public void SharedRepack_WrongChartOffset_BecomesASkippedSet_AndNothingIsWritten()
        {
            var world = BuildStandardSrWorld();
            var sourceBefore = SrSnapshot(world.Fixture.Source);

            object result = null!;
            WithBeforeCheckHook(
                plan =>
                {
                    var chart = Get<IEnumerable>(Get<IEnumerable>(plan, "Pages").Cast<object>().First(), "Charts")
                        .Cast<object>().First();
                    var property = chart.GetType().GetProperty("PackX", AnyInstance)!;
                    property.SetValue(chart, (int)property.GetValue(chart)! + 16);
                },
                () => result = world.Run());

            Assert.That(Get<int>(result, "FailedPixelCheck"), Is.EqualTo(1), SrSkips(result));
            Assert.That(Get<int>(result, "Built"), Is.Zero);
            AssertFunnelReconciles(result);
            Assert.That(SrSkips(result), Does.Contain("failed the pixel check"));
            Assert.That(world.Fixture.Output.Keys, Has.None.Contain("_repack_"));
            Assert.That(Get<HashSet<string>>(world.State, "ModifiedRigids"), Is.Empty);
            Assert.That(Get<HashSet<string>>(world.State, "ModifiedWsModels"), Is.Empty);
            Assert.That(Get<IEnumerable>(result, "ValidationErrors").Cast<object>(), Is.Empty);
            Assert.That(SrValidate(world), Is.Empty);
            foreach (var (path, bytes) in sourceBefore)
                Assert.That(world.Fixture.Source[path], Is.EqualTo(bytes), path);
            var report = BuildSharedRepackSection(Enabled(world));
            Assert.That(report, Does.Contain("Pixel check failure for"));
            Assert.That(report, Does.Contain("worst:"));
        }

        [TestCase(SrBase, 3, "block-copied lower mip", TestName = "SharedRepack_CorruptedBlockCopiedMip_IsCaught")]
        [TestCase(SrBase, 4, null, TestName = "SharedRepack_CorruptedMipBeyondTheChecked_IsNotJudged")]
        public void SharedRepack_CorruptedLowerMip_IsCaughtByTheMipLevelCheck(string slot, int level, string? expected)
        {
            var world = BuildStandardSrWorld(secondLod: false);

            object result = null!;
            WithBeforeCheckHook(
                plan =>
                {
                    var output = HookOutputs(plan).First(item => Get<string>(item, "Slot") == slot);
                    var data = Get<byte[]>(output, "Data");
                    var start = (int)DdsBlockOffset(data, level, 0, 0, 8);
                    var end = (int)DdsBlockOffset(data, level + 1, 0, 0, 8);
                    for (var index = start; index < end; index++)
                        data[index] = (byte)~data[index];
                },
                () => result = world.Run());

            if (expected == null)
            {
                Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
                return;
            }

            Assert.That(Get<int>(result, "FailedPixelCheck"), Is.EqualTo(1), SrSkips(result));
            Assert.That(SrSkips(result), Does.Contain(expected));
            Assert.That(world.Fixture.Output.Keys, Has.None.Contain("_repack_"));
        }

        // Pretend the first levels are the only block-copied ones, so levels 2 and 3 count as
        // re-encoded (the fixture itself block-copies all of them).
        private static void TreatLevelsAsReencoded(object plan)
        {
            foreach (var output in HookOutputs(plan))
                output.GetType().GetProperty("BlockCopiedLevels", AnyInstance)!.SetValue(output, 1);
        }

        [Test]
        public void SharedRepack_ReencodedMip_ManyBadTexelsFail_ButASingleOutlierDoesNot()
        {
            var many = BuildStandardSrWorld(secondLod: false);
            object manyResult = null!;
            WithBeforeCheckHook(
                plan =>
                {
                    TreatLevelsAsReencoded(plan);
                    var output = HookOutputs(plan).First(item => Get<string>(item, "Slot") == SrBase);
                    var data = Get<byte[]>(output, "Data");
                    for (var index = (int)DdsBlockOffset(data, 3, 0, 0, 8); index < (int)DdsBlockOffset(data, 4, 0, 0, 8); index++)
                        data[index] = (byte)~data[index];
                },
                () => manyResult = many.Run());
            Assert.That(Get<int>(manyResult, "FailedPixelCheck"), Is.EqualTo(1), SrSkips(manyResult));
            Assert.That(SrSkips(manyResult), Does.Contain("of the re-encoded mip 2/3 samples differ"));

            var single = BuildStandardSrWorld(secondLod: false);
            object singleResult = null!;
            WithBeforeCheckHook(
                plan =>
                {
                    TreatLevelsAsReencoded(plan);
                    var output = HookOutputs(plan).First(item => Get<string>(item, "Slot") == SrBase);
                    var data = Get<byte[]>(output, "Data");
                    var texel = (int)DdsBlockOffset(data, 3, 0, 0, 8);
                    // One block of a level the sampler may or may not touch: at most a lone outlier.
                    for (var index = texel; index < texel + 8; index++)
                        data[index] = (byte)~data[index];
                },
                () => singleResult = single.Run());
            Assert.That(Get<int>(singleResult, "Built"), Is.EqualTo(1), SrSkips(singleResult));
        }

        [Test]
        public void SharedRepack_TooSmallTexture_TupleIsLeftUntouched_AndTheRestIsBuilt()
        {
            var world = BuildStandardSrWorld(secondLod: false);
            world.AddTexture("textures\\sr_b5.dds", 4, "BC1", true, 5);
            world.AddTexture("textures\\sr_n5.dds", 4, "BC3", false, 6);
            world.Fixture.AddMaterial(SrMaterialPath("m5"), (SrBase, "textures\\sr_b5.dds"), (SrNormal, "textures\\sr_n5.dds"));
            var m1 = SrMaterialPath("m1");
            var m2 = SrMaterialPath("m2");
            var m5 = SrMaterialPath("m5");
            var factory = SrRigidFactory([(m1, SrBlocks((2, 3), (3, 3))), (m2, SrBlocks((10, 4))), (m5, SrBlocks((1, 1)))]);
            world.AddModel(SrRigid, SrWs, SrRoot, factory, [[m1, m2, m5]]);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 2), Is.EqualTo(m5));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Does.Contain("_repack_"));
            var replaced = string.Concat(Get<IEnumerable>(SrBuilt(result).Single(), "Replaced").Cast<object>());
            Assert.That(replaced, Does.Not.Contain("sr_b5"));
            var rigid = (RmvFile)Get<IDictionary>(world.State, "RigidModels")[SrRigid]!;
            Assert.That(
                rigid.ModelList[0][2].Mesh.VertexList.Select(vertex => vertex.Uv),
                Is.EqualTo(factory().ModelList[0][2].Mesh.VertexList.Select(vertex => vertex.Uv)));
            Assert.That(BuildSharedRepackSection(Enabled(world)), Does.Contain("not a uniform colour"));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_TextureSharedWithAnExcludedTuple_ExcludesBothTuples()
        {
            var world = BuildStandardSrWorld(secondLod: false);
            // Tuple 6 has a tiny base but shares the normal map of tuple 1.
            world.AddTexture("textures\\sr_b5.dds", 4, "BC1", true, 5);
            world.Fixture.AddMaterial(SrMaterialPath("m6"), (SrBase, "textures\\sr_b5.dds"), (SrNormal, "textures\\sr_n1.dds"));
            var m1 = SrMaterialPath("m1");
            var m2 = SrMaterialPath("m2");
            var m6 = SrMaterialPath("m6");
            world.AddModel(
                SrRigid, SrWs, SrRoot,
                SrRigidFactory([(m1, SrBlocks((2, 3))), (m2, SrBlocks((10, 4))), (m6, SrBlocks((1, 1)))]),
                [[m1, m2, m6]]);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Is.EqualTo(m1));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 2), Is.EqualTo(m6));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 1), Does.Contain("_repack_"));
            Assert.That(BuildSharedRepackSection(Enabled(world)), Does.Contain("shares a texture with"));
            var replaced = string.Concat(Get<IEnumerable>(SrBuilt(result).Single(), "Replaced").Cast<object>());
            Assert.That(replaced, Does.Not.Contain("sr_n1").And.Not.Contain("sr_b1"));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_LayoutLargerThanTheLimit_IsSplitIntoPages_KeepingEachPartsChartsTogether()
        {
            var world = BuildStandardSrWorld(secondLod: false);
            var m1 = SrMaterialPath("m1");
            var m2 = SrMaterialPath("m2");
            Func<RmvFile> factory = SrRigidFactory([
                (m1, SrBlocks((2, 3), (3, 3), (2, 4), (3, 4))),
                (m2, SrBlocks((9, 4), (10, 4), (9, 5), (10, 5)))]);
            world.AddModel(SrRigid, SrWs, SrRoot, factory, [[m1, m2]]);

            var result = world.Run(maximumSide: 256);

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            var built = SrBuilt(result).Single();
            Assert.That(Get<int>(built, "Pages"), Is.EqualTo(2));
            Assert.That(Get<IEnumerable>(built, "Outputs").Cast<object>().Count(), Is.EqualTo(4));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Is.Not.EqualTo(SrWsMaterial(world.State, SrWs, 0, 1)));
            AssertSrPixels(world, SrRigid, SrWs, factory, SrPart0Seeds, (_, part) => part == 0);
            AssertSrPixels(world, SrRigid, SrWs, factory, SrPart1Seeds, (_, part) => part == 1);
            foreach (var output in Get<IEnumerable>(built, "Outputs").Cast<object>())
            {
                Assert.That(Get<int>(output, "Width"), Is.LessThanOrEqualTo(256));
                Assert.That(Get<int>(output, "Height"), Is.LessThanOrEqualTo(256));
            }

            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_ChartThatCannotFitTheLimit_ExcludesItsTuple()
        {
            var world = BuildStandardSrWorld(secondLod: false);

            var result = world.Run(maximumSide: 64);

            Assert.That(Get<int>(result, "Built"), Is.Zero);
            Assert.That(Get<int>(result, "NotBuildable"), Is.EqualTo(1), SrSkips(result));
            Assert.That(SrSkips(result), Does.Contain("does not fit"));
            Assert.That(world.Fixture.Output.Keys, Has.None.Contain("_repack_"));
        }

        [Test]
        public void SharedRepack_GroupThreshold_LeavesAGroupThatDoesNotPayUntouched()
        {
            var world = BuildStandardSrWorld(secondLod: false);
            world.AddTexture("textures\\sr_b4.dds", 1024, "BC1", true, 7);
            world.Fixture.AddMaterial(SrMaterialPath("m4"), (SrBase, "textures\\sr_b4.dds"));
            var m1 = SrMaterialPath("m1");
            var m2 = SrMaterialPath("m2");
            var m4 = SrMaterialPath("m4");
            world.AddModel(
                SrRigid, SrWs, SrRoot,
                SrRigidFactory([(m1, SrBlocks((2, 3))), (m2, SrBlocks((10, 4))), (m4, SrBlocks((12, 12)))]),
                [[m1, m2, m4]]);

            // The base-only group saves about 0.68 MB, the first group about 2 MB.
            var result = world.Run(minimumGroupSaving: 800_000);

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 2), Is.EqualTo(m4));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Does.Contain("_repack_"));
            Assert.That(BuildSharedRepackSection(Enabled(world)), Does.Contain("below the 800.000-byte group minimum")
                .Or.Contain("below the 800,000-byte group minimum"));
        }

        [Test]
        public void SharedRepack_SetThreshold_UsesTheActualBytesOfTheBuiltGroups()
        {
            var probe = BuildStandardSrWorld(secondLod: false);
            var probeBuilt = SrBuilt(probe.Run()).Single();
            var actual = Get<long>(probeBuilt, "Before") - Get<long>(probeBuilt, "After");
            var estimated = Get<long>(probeBuilt, "EstimatedSaving");
            Assert.That(estimated, Is.GreaterThan(actual));

            var world = BuildStandardSrWorld(secondLod: false);
            var result = world.Run(minimumSaving: (actual + estimated) / 2);

            Assert.That(Get<int>(result, "FailedActualBytes"), Is.EqualTo(1), SrSkips(result));
            Assert.That(Get<int>(result, "Built"), Is.Zero);
            Assert.That(SrSkips(result), Does.Contain("actual bytes:"));
            Assert.That(world.Fixture.Output.Keys, Has.None.Contain("_repack_"));
        }

        private static byte[] BuildContentDdsSized(int width, int height, string format, bool srgb, int seed)
        {
            var bgra = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var (r, g, b) = SrColour(seed, x * 16 / width, y * 16 / height);
                    var offset = (y * width + x) * 4;
                    bgra[offset] = b;
                    bgra[offset + 1] = g;
                    bgra[offset + 2] = r;
                    bgra[offset + 3] = 255;
                }
            }

            var arguments = new object?[] { bgra, width, height, format, srgb, true, null, null };
            Assert.That((bool)GetStaticMethod("TryEncodeSharedRepackDds").Invoke(null, arguments)!, Is.True);
            return (byte[])arguments[6]!;
        }

        [TestCase(512, 512, 1024, 1024, TestName = "SharedRepack_Sizing_BaseSmallerThanTheOtherSlots")]
        [TestCase(1024, 1024, 256, 256, TestName = "SharedRepack_Sizing_BaseLargerThanTheOtherSlots")]
        [TestCase(1024, 512, 512, 256, TestName = "SharedRepack_Sizing_NonSquareWide")]
        [TestCase(512, 1024, 256, 512, TestName = "SharedRepack_Sizing_NonSquareTall")]
        [TestCase(1024, 1024, 2048, 1024, TestName = "SharedRepack_Sizing_RatioDiffersPerAxis")]
        public void SharedRepack_MixedResolutionTuples_NeverGrowAndKeepTheirPixelsExactly(
            int baseWidth,
            int baseHeight,
            int normalWidth,
            int normalHeight)
        {
            var world = new SrWorld();
            world.Fixture.Vanilla["textures\\sr_b1.dds"] = BuildContentDdsSized(baseWidth, baseHeight, "BC1", true, 1);
            world.Fixture.Vanilla["textures\\sr_n1.dds"] = BuildContentDdsSized(normalWidth, normalHeight, "BC3", false, 2);
            world.Fixture.AddMaterial(SrMaterialPath("m1"), (SrBase, "textures\\sr_b1.dds"), (SrNormal, "textures\\sr_n1.dds"));
            var m1 = SrMaterialPath("m1");
            var factory = SrRigidFactory([(m1, SrBlocks((2, 3), (3, 3), (9, 9)))]);
            world.AddModel(SrRigid, SrWs, SrRoot, factory, [[m1]]);

            var result = world.Run(minimumSaving: 50_000);

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            Assert.That(Get<int>(result, "SizingErrors"), Is.Zero);
            var built = SrBuilt(result).Single();
            Assert.That(Get<double>(built, "MaxError"), Is.Zero);
            var sizes = new Dictionary<string, (int Width, int Height)>
            {
                [SrBase] = (baseWidth, baseHeight),
                [SrNormal] = (normalWidth, normalHeight),
            };
            foreach (var output in Get<IEnumerable>(built, "Outputs").Cast<object>())
            {
                var slot = Get<string>(output, "Slot");
                var width = Get<int>(output, "Width");
                var height = Get<int>(output, "Height");
                Assert.That(width, Is.LessThanOrEqualTo(sizes[slot].Width), slot);
                Assert.That(height, Is.LessThanOrEqualTo(sizes[slot].Height), slot);
                Assert.That((long)width * height, Is.LessThanOrEqualTo((long)sizes[slot].Width * sizes[slot].Height), slot);
                Assert.That(Get<int>(output, "BlockCopiedLevels"), Is.GreaterThanOrEqualTo(1), slot);
            }

            // Same texel density in both slots: a UV block is as many pixels wide as in the source.
            AssertSrPixels(world, SrRigid, SrWs, factory, SrPart0Seeds);
            Assert.That(Get<long>(built, "After"), Is.LessThan(Get<long>(built, "Before")));
            var report = BuildSharedRepackSection(Enabled(world));
            Assert.That(report, Does.Contain($"sizes: base_colour {baseWidth}x{baseHeight} -> "));
            Assert.That(report, Does.Contain($"normal {normalWidth}x{normalHeight} -> "));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_TinyUniformMask_IsKeptAsAuthored_AndDoesNotInflateTheLayout()
        {
            var world = BuildStandardSrWorld(secondLod: false);
            // Vanilla materials carry a 4x4 mask next to 1024 colour maps.
            var bgra = new byte[4 * 4 * 4];
            for (var offset = 0; offset < bgra.Length; offset += 4)
            {
                bgra[offset + 2] = 200;
                bgra[offset + 3] = 255;
            }

            var arguments = new object?[] { bgra, 4, 4, "BC3", false, true, null, null };
            Assert.That((bool)GetStaticMethod("TryEncodeSharedRepackDds").Invoke(null, arguments)!, Is.True);
            world.Fixture.Vanilla["textures\\sr_mask.dds"] = (byte[])arguments[6]!;
            world.Fixture.AddMaterial(
                SrMaterialPath("m1"),
                (SrBase, "textures\\sr_b1.dds"),
                (SrNormal, "textures\\sr_n1.dds"),
                ("t_xml_mask", "textures\\sr_mask.dds"));

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            Assert.That(Get<int>(result, "SizingErrors"), Is.Zero);
            var built = SrBuilt(result).Single();
            var generated = Encoding.UTF8.GetString(world.Fixture.Output[SrWsMaterial(world.State, SrWs, 0, 0)]);
            Assert.That(SrSlotNode(generated, "t_xml_mask").SelectSingleNode("source")!.InnerText, Is.EqualTo("textures\\sr_mask.dds"));
            Assert.That(Get<int>(built, "LayoutWidth"), Is.LessThanOrEqualTo(512));
            var replaced = string.Concat(Get<IEnumerable>(built, "Replaced").Cast<object>());
            Assert.That(replaced, Does.Not.Contain("sr_mask"));
            AssertSrPixels(world, SrRigid, SrWs, BuildStandardSrWorld(false).Rigids[SrRigid], SrPart0Seeds, (_, part) => part == 0);
            Assert.That(BuildSharedRepackSection(Enabled(world)), Does.Contain("kept as authored (uniform): mask"));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_TinyNonUniformTexture_LeavesTheTupleUntouched()
        {
            var world = BuildStandardSrWorld(secondLod: false);
            world.AddTexture("textures\\sr_mask.dds", 4, "BC3", false, 3);
            world.Fixture.AddMaterial(
                SrMaterialPath("m1"),
                (SrBase, "textures\\sr_b1.dds"),
                (SrNormal, "textures\\sr_n1.dds"),
                ("t_xml_mask", "textures\\sr_mask.dds"));

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Is.EqualTo(SrMaterialPath("m1")));
            Assert.That(BuildSharedRepackSection(Enabled(world)), Does.Contain("not a uniform colour"));
        }

        private static byte[] BuildFlatBlackDds(int size)
        {
            var bgra = new byte[size * size * 4];
            for (var offset = 0; offset < bgra.Length; offset += 4)
                bgra[offset + 3] = 255;
            var arguments = new object?[] { bgra, size, size, "BC1", true, true, null, null };
            Assert.That((bool)GetStaticMethod("TryEncodeSharedRepackDds").Invoke(null, arguments)!, Is.True);
            return (byte[])arguments[6]!;
        }

        // Part 0 (m1) also binds a 64 x 64 texture in the given slot that another unit (rigid 2)
        // uses too, so it is outside the set. Returns the world.
        private static SrWorld BuildOutsideTextureWorld(string slot, byte[] texture)
        {
            var world = BuildStandardSrWorld(secondLod: false);
            world.Fixture.Vanilla["textures\\sr_outside.dds"] = texture;
            world.Fixture.AddMaterial(
                SrMaterialPath("m1"),
                (SrBase, "textures\\sr_b1.dds"),
                (SrNormal, "textures\\sr_n1.dds"),
                (slot, "textures\\sr_outside.dds"));
            world.Fixture.AddMaterial(SrMaterialPath("m9"), (slot, "textures\\sr_outside.dds"));
            var m1 = SrMaterialPath("m1");
            var m2 = SrMaterialPath("m2");
            var m9 = SrMaterialPath("m9");
            world.AddModel(
                SrRigid, SrWs, SrRoot,
                SrRigidFactory([(m1, SrBlocks((2, 3), (3, 3))), (m2, SrBlocks((10, 4)))]),
                [[m1, m2]]);
            world.AddModel(SrRigid2, SrWs2, SrRoot2, SrRigidFactory([(m9, SrBlocks((1, 1)))]), [[m9]]);
            return world;
        }

        [Test]
        public void SharedRepack_UniformTextureOutsideTheSet_AnySizeBelowTheLimit_DoesNotDropThePart()
        {
            var world = BuildOutsideTextureWorld("t_xml_mask", BuildFlatBlackDds(64));
            var first = BuildStandardSrWorld(false).Rigids[SrRigid];

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            AssertFunnelReconciles(result);
            var built = SrBuilt(result).Single();
            Assert.That(Get<int>(built, "Parts"), Is.EqualTo(2));
            Assert.That(Get<Dictionary<string, int>>(result, "PartDropReasons"), Is.Empty);
            var generatedPath = SrWsMaterial(world.State, SrWs, 0, 0);
            Assert.That(generatedPath, Does.Contain("_repack_"));
            var generated = Encoding.UTF8.GetString(world.Fixture.Output[generatedPath]);
            Assert.That(
                SrSlotNode(generated, "t_xml_mask").SelectSingleNode("source")!.InnerText,
                Is.EqualTo("textures\\sr_outside.dds"));
            // The flat texture is never repacked and never counted as replaced.
            var replaced = string.Concat(Get<IEnumerable>(built, "Replaced").Cast<object>());
            Assert.That(replaced, Does.Not.Contain("sr_outside"));
            Assert.That(world.Fixture.Output.Keys, Has.None.Contain("_repack_mask"));
            AssertSrPixels(world, SrRigid, SrWs, first, SrPart0Seeds, (_, part) => part == 0);
            Assert.That(BuildSharedRepackSection(Enabled(world)), Does.Contain("kept as authored (uniform): mask"));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_NonUniformTextureOutsideTheSet_StillDropsThePart_AndOnlyItIsNamed()
        {
            // The part also binds a flat texture (material map) that must not be blamed.
            // 48 pixels: not a power of two, so no signature set can take it in.
            var world = BuildOutsideTextureWorld("t_xml_mask", BuildContentDds(48, "BC1", true, 5));
            world.Fixture.Vanilla["textures\\sr_flat.dds"] = BuildFlatBlackDds(256);
            world.Fixture.AddMaterial(
                SrMaterialPath("m1"),
                (SrBase, "textures\\sr_b1.dds"),
                (SrNormal, "textures\\sr_n1.dds"),
                ("t_xml_mask", "textures\\sr_outside.dds"),
                ("t_xml_material_map", "textures\\sr_flat.dds"));

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            AssertFunnelReconciles(result);
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Is.EqualTo(SrMaterialPath("m1")));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 1), Does.Contain("_repack_"));
            Assert.That(
                Get<Dictionary<string, int>>(result, "PartDropReasons").Keys,
                Has.Some.Contain("also uses textures outside the set"));
            var report = BuildSharedRepackSection(Enabled(world));
            Assert.That(report, Does.Contain("sr_outside.dds, which"));
            Assert.That(report, Does.Not.Contain("use textures\\sr_flat.dds"));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_UniformTextureOverTheDecodeLimit_CountsAsNotUniform()
        {
            // 2048 x 2048 flat black: over the 1024 limit it is not decoded, so it is not kept as
            // authored; its signature set is merged in and it is repacked like any texture.
            var world = BuildOutsideTextureWorld("t_xml_mask", BuildFlatBlackDds(2048));

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            Assert.That(Get<int>(result, "MergedIntoOther"), Is.EqualTo(1), SrSkips(result));
            var generated = Encoding.UTF8.GetString(world.Fixture.Output[SrWsMaterial(world.State, SrWs, 0, 0)]);
            Assert.That(
                SrSlotNode(generated, "t_xml_mask").SelectSingleNode("source")!.InnerText,
                Does.Contain("_repack_mask"));
            AssertFunnelReconciles(result);
        }

        // Rigid 1 (unit 1) uses m1 (b1 + n1) and m2 (b2 + n2); rigid 2 (unit 2) uses only the
        // normal map n1. So {b1, b2, n2} is one signature set and {n1} another, and part 0 of
        // rigid 1 has a material whose base colour and normal belong to different sets.
        private static (SrWorld World, Func<RmvFile> First, Func<RmvFile> Second) BuildMergeSrWorld()
        {
            var world = BuildStandardSrWorld(secondLod: false);
            world.Fixture.AddMaterial(SrMaterialPath("mn"), (SrNormal, "textures\\sr_n1.dds"));
            var mn = SrMaterialPath("mn");
            var second = SrRigidFactory([(mn, SrBlocks((9, 9)))]);
            world.AddModel(SrRigid2, SrWs2, SrRoot2, second, [[mn]]);
            return (world, BuildStandardSrWorld(false).Rigids[SrRigid], second);
        }

        private static readonly Dictionary<string, int> SrNormalOnlySeeds = new() { [SrNormal] = 2 };

        [Test]
        public void SharedRepack_SetsConnectedThroughAMaterial_AreMergedAndBuilt_AcrossRigids()
        {
            var (world, first, second) = BuildMergeSrWorld();

            var result = world.Run();

            Assert.That(Get<int>(result, "SetsConsidered"), Is.EqualTo(2));
            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            Assert.That(Get<int>(result, "MergedIntoOther"), Is.EqualTo(1), SrSkips(result));
            Assert.That(Get<int>(result, "MergePlansTried"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "MergePlansBuilt"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "MergePlansFellBack"), Is.Zero);
            Assert.That(Get<int>(result, "LargestMergeSets"), Is.EqualTo(2));
            Assert.That(Get<int>(result, "LargestMergeRigids"), Is.EqualTo(2));
            AssertFunnelReconciles(result);
            var built = SrBuilt(result).Single();
            Assert.That(SrBuiltRigids(built), Is.EqualTo(new[] { $"({SrRigid}, 2)", $"({SrRigid2}, 1)" }));
            Assert.That(Get<IEnumerable>(built, "MergedSets").Cast<object>().Count(), Is.EqualTo(1));
            // Colours: part 0 samples the base from set one's texture and the normal from set two's.
            AssertSrPixels(world, SrRigid, SrWs, first, SrPart0Seeds, (_, part) => part == 0);
            AssertSrPixels(world, SrRigid, SrWs, first, SrPart1Seeds, (_, part) => part == 1);
            var (samples, _) = AssertSrPixels(world, SrRigid2, SrWs2, second, SrNormalOnlySeeds);
            Assert.That(samples, Is.GreaterThan(0));
            Assert.That(
                Get<HashSet<string>>(world.State, "ModifiedRigids"),
                Is.EquivalentTo(new[] { SrRigid, SrRigid2 }));
            Assert.That(SrValidate(world), Is.Empty);
            Assert.That(Get<IEnumerable>(result, "ValidationErrors").Cast<object>(), Is.Empty);
        }

        [Test]
        public void SharedRepack_ReportSection_ListsTheMergedSetsAndThePlanSummary()
        {
            var (world, _, _) = BuildMergeSrWorld();
            world.Run();
            SetStateProperty(world.State, "SharedTextureRepackRequested", true);
            SetStateProperty(world.State, "SharedTextureRepackEnabled", true);
            GetStaticMethod("FinalizeSharedTextureRepack").Invoke(null, [world.State]);

            var report = BuildSharedRepackSection(world.State);

            Assert.That(report, Does.Contain("Merged into another set's plan: 1"));
            Assert.That(report, Does.Contain("1 tried, 1 built, 0 fell back"));
            Assert.That(report, Does.Contain("largest 2 set(s) in 2 rigid(s); 0 plan(s) hit the cap"));
            Assert.That(report, Does.Contain("merged signature sets (1 joined the seed):"));
            Assert.That(report, Does.Contain("sr_n1.dds (1 texture(s), 2 unit(s)): 1 texture(s), 2 unit(s)"));
            Assert.That(report, Does.Contain($"{SrRigid} (2 part(s)), {SrRigid2} (1 part(s))"));
            Assert.That(report, Does.Contain("(reconciles)"));
            Assert.That(report, Does.Contain("merged into another set's plan (textures\\sr_b1.dds"));
        }

        [Test]
        public void SharedRepack_TuplesWithTexturesOfDifferentSignatureSets_AreNeverOnTheSamePage()
        {
            var (world, _, _) = BuildMergeSrWorld();
            var groupKeys = new List<string>();
            var pageKeys = new List<List<string>>();

            WithBeforeCheckHook(
                plan =>
                {
                    groupKeys.AddRange(Get<IEnumerable>(plan, "Tuples").Cast<object>().Select(tuple => Get<string>(tuple, "GroupKey")));
                    foreach (var page in Get<IEnumerable>(plan, "Pages").Cast<object>())
                    {
                        pageKeys.Add(Get<IEnumerable>(page, "Tuples").Cast<object>()
                            .Select(tuple => Get<string>(tuple, "GroupKey")).Distinct().ToList());
                    }
                },
                () => world.Run());

            // (b1, n1) is base: set one, normal: set two; (b2, n2) is set one for both.
            Assert.That(groupKeys.Distinct().Count(), Is.GreaterThanOrEqualTo(3));
            Assert.That(pageKeys, Is.Not.Empty);
            Assert.That(pageKeys.Select(keys => keys.Count), Has.All.EqualTo(1));
        }

        [Test]
        public void SharedRepack_Validation_FailsWhenAGeneratedTextureReplacesTexturesOfTwoSignatureSets()
        {
            var (world, _, _) = BuildMergeSrWorld();
            WithBeforeCheckHook(
                plan =>
                {
                    var output = HookOutputs(plan).First(item => Get<string>(item, "Slot") == SrBase);
                    Get<List<string>>(output, "Sources").Add("textures\\sr_n1.dds");
                },
                () => world.Run());

            var errors = SrValidate(world);

            Assert.That(errors, Has.Some.Contain("signature set(s), not exactly one"));
        }

        [Test]
        public void SharedRepack_MergedPlanFailingThePixelCheck_WritesNothing_SeedRunsAlone_AndTheReleasedSetRunsOnItsOwn()
        {
            var (world, first, _) = BuildMergeSrWorld();
            var calls = 0;
            object result = null!;

            WithBeforeCheckHook(
                plan =>
                {
                    if (calls++ != 0)
                        return;

                    var chart = Get<IEnumerable>(Get<IEnumerable>(plan, "Pages").Cast<object>().First(), "Charts")
                        .Cast<object>().First();
                    var property = chart.GetType().GetProperty("PackX", AnyInstance)!;
                    property.SetValue(chart, (int)property.GetValue(chart)! + 16);
                },
                () => WithReducedPaddingDisabled(() => result = world.Run()));

            // The merged attempt (call 1) failed; the seed alone (call 2) was checked and built.
            // The released set was then planned on its own, not merged: part 0 of rigid 1 uses a
            // texture of the processed seed set and was not rewritten, so its normal map stays.
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            Assert.That(Get<int>(result, "MergedIntoOther"), Is.Zero);
            Assert.That(Get<int>(result, "DroppedByClosure"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "FailedPixelCheck"), Is.Zero);
            Assert.That(Get<int>(result, "MergePlansTried"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "MergePlansBuilt"), Is.Zero);
            Assert.That(Get<int>(result, "MergePlansFellBack"), Is.EqualTo(1));
            AssertFunnelReconciles(result);
            var built = SrBuilt(result).Single();
            Assert.That(Get<string>(built, "FallbackNote"), Does.Contain("merged attempt").And.Contain("pixel check").And.EndWith("built alone"));
            Assert.That(Get<IEnumerable>(built, "MergedSets").Cast<object>(), Is.Empty);
            Assert.That(SrBuiltRigids(built), Is.EqualTo(new[] { $"({SrRigid}, 1)" }));
            // Nothing of the failed merged plan: part 0 keeps its material, only m2' exists.
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Is.EqualTo(SrMaterialPath("m1")));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 1), Does.Contain("_repack_"));
            Assert.That(Get<HashSet<string>>(world.State, "SharedRepackMaterialPaths"), Has.Count.EqualTo(1));
            Assert.That(Get<HashSet<string>>(world.State, "SharedRepackTexturePaths"), Has.Count.EqualTo(2));
            Assert.That(Get<HashSet<string>>(world.State, "ModifiedRigids"), Is.EquivalentTo(new[] { SrRigid }));
            AssertSrPixels(world, SrRigid, SrWs, first, SrPart1Seeds, (_, part) => part == 1);
            Assert.That(Get<IEnumerable>(result, "PixelFailures").Cast<object>().Count(), Is.EqualTo(1));
            // The released set meets b1 again, but its set was processed: outside, dropped.
            var report = BuildSharedRepackSection(Enabled(world));
            Assert.That(report, Does.Contain("in a different signature set (1 unit(s), 3 texture(s)) that was already processed"));
            Assert.That(report, Does.Contain("the merged attempt with 1 other signature set(s) failed"));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_MergedPlans_TwoRunsProduceIdenticalBytes()
        {
            var (first, _, _) = BuildMergeSrWorld();
            var (second, _, _) = BuildMergeSrWorld();
            first.Run();
            second.Run();

            var generated = first.Fixture.Output.Keys.Where(key => key.Contains("_repack_")).OrderBy(key => key).ToList();
            Assert.That(generated, Is.Not.Empty);
            Assert.That(
                second.Fixture.Output.Keys.Where(key => key.Contains("_repack_")).OrderBy(key => key),
                Is.EqualTo(generated));
            foreach (var path in generated)
                Assert.That(second.Fixture.Output[path], Is.EqualTo(first.Fixture.Output[path]), path);
            foreach (var rigidPath in new[] { SrRigid, SrRigid2 })
            {
                var firstRigid = (RmvFile)Get<IDictionary>(first.State, "RigidModels")[rigidPath]!;
                var secondRigid = (RmvFile)Get<IDictionary>(second.State, "RigidModels")[rigidPath]!;
                Assert.That(
                    secondRigid.ModelList.SelectMany(lod => lod).SelectMany(model => model.Mesh.VertexList.Select(vertex => vertex.Uv)),
                    Is.EqualTo(firstRigid.ModelList.SelectMany(lod => lod).SelectMany(model => model.Mesh.VertexList.Select(vertex => vertex.Uv))));
            }
        }

        // Unit k uses rigid k with X_k = (b_k, n_k) and the connector Y_k = (b_k, n_(k+1)), so the
        // signature sets {b_k} and {n_k, n_(k+1) side} form one long chain of connected sets.
        private static SrWorld BuildChainSrWorld(int links)
        {
            var world = new SrWorld();
            for (var k = 0; k < links; k++)
            {
                world.AddTexture($"textures\\sr_cb{k}.dds", 1024, "BC1", true, 1);
                world.AddTexture($"textures\\sr_cn{k}.dds", 512, "BC3", false, 2);
            }

            for (var k = 0; k < links; k++)
            {
                world.Fixture.AddMaterial(
                    SrMaterialPath($"x{k}"), (SrBase, $"textures\\sr_cb{k}.dds"), (SrNormal, $"textures\\sr_cn{k}.dds"));
                if (k + 1 < links)
                {
                    world.Fixture.AddMaterial(
                        SrMaterialPath($"y{k}"), (SrBase, $"textures\\sr_cb{k}.dds"), (SrNormal, $"textures\\sr_cn{k + 1}.dds"));
                }
            }

            for (var k = 0; k < links; k++)
            {
                var x = SrMaterialPath($"x{k}");
                var y = SrMaterialPath($"y{k}");
                var parts = k + 1 < links
                    ? new[] { (x, SrBlocks((2, 3))), (y, SrBlocks((5, 5))) }
                    : new[] { (x, SrBlocks((2, 3))) };
                world.AddModel(
                    $"variantmeshes\\wh_main\\ch{k}.rigid_model_v2",
                    $"variantmeshes\\wh_main\\ch{k}.wsmodel",
                    $"variantmeshes\\wh_variantmodels\\hu1\\emp\\ch{k}\\ch{k}.variantmeshdefinition",
                    SrRigidFactory(parts),
                    [parts.Select(part => part.Item1).ToArray()]);
            }

            return world;
        }

        [Test]
        public void SharedRepack_ChainOfSetsAboveTheCap_StopsMergingAndDropsTheRest()
        {
            var world = BuildChainSrWorld(10);

            var result = world.Run(minimumSaving: 50_000, minimumGroupSaving: 5_000);

            AssertFunnelReconciles(result);
            // The cap stops every plan at 8 sets; the parts that would have pulled in a ninth
            // drop, and the drop takes the rest of the chain with it, as without merging.
            Assert.That(Get<int>(result, "MergeCapHits"), Is.GreaterThanOrEqualTo(1), SrSkips(result));
            Assert.That(Get<int>(result, "LargestMergeSets"), Is.EqualTo(8));
            Assert.That(Get<int>(result, "LargestMergeRigids"), Is.LessThanOrEqualTo(16));
            foreach (var built in SrBuilt(result))
                Assert.That(Get<IEnumerable>(built, "MergedSets").Cast<object>().Count(), Is.LessThanOrEqualTo(7));
            Assert.That(
                BuildSharedRepackSection(Enabled(world)),
                Does.Contain("merge cap reached: at most 8 sets and 16 source rigids and 64 generated rigids per plan"));
            Assert.That(SrValidate(world), Is.Empty);
            Assert.That(Get<IEnumerable>(result, "ValidationErrors").Cast<object>(), Is.Empty);
        }

        private static readonly Dictionary<string, int> SrBigSeeds = new() { [SrBase] = 1 };

        // One 2048 x 2048 base colour used by two parts in separate regions: part 0 shows
        // u 0..aWidth and v 0..aHeight, part 1 shows u 0.5..0.95 and v 0..bHeight. Their charts are
        // far apart, so they are two clusters.
        private static (SrWorld World, Func<RmvFile> Rigid) BuildSplitSrWorld(
            double aWidth, double aHeight, double bHeight, int lods = 1)
        {
            var world = new SrWorld();
            world.AddTexture("textures\\sr_big.dds", 2048, "BC1", true, 1);
            world.Fixture.AddMaterial(SrMaterialPath("mbig"), (SrBase, "textures\\sr_big.dds"));
            var mbig = SrMaterialPath("mbig");
            var a = UvQuad(0.004, 0.004, aWidth, aHeight);
            var b = UvQuad(0.5, 0.004, 0.95, bHeight);
            var parts = new[] { (mbig, a), (mbig, b) };
            var factory = lods == 1 ? SrRigidFactory(parts) : SrRigidFactory(parts, parts);
            world.AddModel(SrRigid, SrWs, SrRoot, factory, lods == 1 ? [[mbig, mbig]] : [[mbig, mbig], [mbig, mbig]]);
            return (world, factory);
        }

        private static List<string> SrPageSizes(object built)
            => Get<IEnumerable>(built, "Outputs").Cast<object>()
                .Select(output => $"{Get<int>(output, "Width")}x{Get<int>(output, "Height")}")
                .OrderBy(size => size, StringComparer.Ordinal)
                .ToList();

        [Test]
        public void SharedRepack_TwoIndependentParts_OnePageCannotShrink_BuildAsTwoSmallerPages()
        {
            // Single page: 2048 x 2048 (the 1184-high chart needs a 2048 side), as big as the
            // source. Split: 1024 x 2048 + 1024 x 1024 = 75 % of the pixels.
            var (world, factory) = BuildSplitSrWorld(0.3, 0.55, 0.45);

            var result = world.Run(minimumGroupSaving: 300_000);

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            AssertFunnelReconciles(result);
            var built = SrBuilt(result).Single();
            Assert.That(Get<int>(built, "Pages"), Is.EqualTo(2));
            Assert.That(SrPageSizes(built), Is.EqualTo(new[] { "1024x1024", "1024x2048" }));
            Assert.That(Get<int>(built, "Parts"), Is.EqualTo(2));
            // Each part reads its own page through its own generated material (one source
            // material, two pages, two generated copies with different names).
            var material0 = SrWsMaterial(world.State, SrWs, 0, 0);
            var material1 = SrWsMaterial(world.State, SrWs, 0, 1);
            Assert.That(material0, Does.Contain("_repack_"));
            Assert.That(material1, Does.Contain("_repack_"));
            Assert.That(material0, Is.Not.EqualTo(material1));
            Assert.That(Get<List<string>>(result, "GeneratedMaterials"), Has.Count.EqualTo(2));
            Assert.That(Get<List<string>>(result, "GeneratedTextures"), Has.Count.EqualTo(2));
            var (samples, max) = AssertSrPixels(world, SrRigid, SrWs, factory, SrBigSeeds);
            Assert.That(samples, Is.GreaterThan(0));
            Assert.That(max, Is.LessThanOrEqualTo(12));
            // The part count is untouched: splitting only changes the textures.
            var rigid = (RmvFile)Get<IDictionary>(world.State, "RigidModels")[SrRigid]!;
            Assert.That(rigid.ModelList[0], Has.Length.EqualTo(2));
            var splits = Get<IEnumerable>(built, "Splits").Cast<object>().ToList();
            Assert.That(splits.Single(), Has.Property("Applied").True);
            Assert.That(Get<long>(splits.Single(), "Saved"), Is.GreaterThan(0));
            var report = BuildSharedRepackSection(Enabled(world));
            Assert.That(report, Does.Contain("split into 2 page(s): 1024x1024 + 1024x2048 (one page would be 2048x2048,"));
            Assert.That(report, Does.Contain("1 group(s) split"));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_SingleClusterGroup_CannotSplit_AndSaysSo()
        {
            // One region seen by a part and by its LOD copy: the same chart, one cluster, one
            // 2048 x 2048 page.
            var world = new SrWorld();
            world.AddTexture("textures\\sr_big.dds", 2048, "BC1", true, 1);
            world.Fixture.AddMaterial(SrMaterialPath("mbig"), (SrBase, "textures\\sr_big.dds"));
            var mbig = SrMaterialPath("mbig");
            // A thin diagonal sliver: little coverage, but one chart as big as the texture.
            float[][] sliver = [[0.004f, 0.004f, 0.95f, 0.95f, 0.95f, 0.90f]];
            world.AddModel(
                SrRigid, SrWs, SrRoot, SrRigidFactory([(mbig, sliver)], [(mbig, sliver)]), [[mbig], [mbig]]);

            var result = world.Run(minimumGroupSaving: 300_000);

            Assert.That(Get<int>(result, "Built"), Is.Zero);
            AssertFunnelReconciles(result);
            Assert.That(SrSkips(result), Does.Contain("one connected cluster: cannot split"));
            Assert.That(world.Fixture.Output.Keys, Has.None.Contain("_repack_"));
        }

        [Test]
        public void SharedRepack_SplitSavingLessThanTheGroupThreshold_IsRejected_AndReportsTheBestSplit()
        {
            var (world, _) = BuildSplitSrWorld(0.3, 0.55, 0.45);

            // The split saves about 700 KB over nothing: under a 1 MB group minimum.
            var result = world.Run(minimumGroupSaving: 1_000_000);

            Assert.That(Get<int>(result, "Built"), Is.Zero, SrSkips(result));
            AssertFunnelReconciles(result);
            Assert.That(SrSkips(result), Does.Contain("2 cluster(s), best split into 2 page(s) "));
            Assert.That(SrSkips(result), Does.Contain("did not pay"));
            Assert.That(world.Fixture.Output.Keys, Has.None.Contain("_repack_"));
        }

        [Test]
        public void SharedRepack_GroupThatAlreadyBuilds_IsSplitWhenStrictlyCheaperByTheThreshold()
        {
            // Single page 2048 x 1024 already saves half; two pages 1024 x 1024 + 1024 x 512 save more.
            var (world, factory) = BuildSplitSrWorld(0.45, 0.45, 0.2);

            var result = world.Run(minimumGroupSaving: 100_000);

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            var built = SrBuilt(result).Single();
            Assert.That(Get<int>(built, "Pages"), Is.EqualTo(2));
            Assert.That(SrPageSizes(built), Is.EqualTo(new[] { "1024x1024", "1024x512" }));
            AssertSrPixels(world, SrRigid, SrWs, factory, SrBigSeeds);
            Assert.That(BuildSharedRepackSection(Enabled(world)), Does.Contain("built, split into 2 page(s): "));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_GroupThatAlreadyBuilds_IsNotSplitWhenTheGainIsUnderTheThreshold()
        {
            var (world, _) = BuildSplitSrWorld(0.45, 0.45, 0.2);

            // The split is about 400 KB cheaper; with a 1 MB group minimum it is not taken.
            var result = world.Run(minimumSaving: 100_000, minimumGroupSaving: 1_000_000);

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            Assert.That(Get<int>(SrBuilt(result).Single(), "Pages"), Is.EqualTo(1));
            Assert.That(SrPageSizes(SrBuilt(result).Single()), Is.EqualTo(new[] { "2048x1024" }));
        }

        [Test]
        public void SharedRepack_SplitPages_TwoRunsProduceIdenticalBytes()
        {
            var (first, _) = BuildSplitSrWorld(0.3, 0.55, 0.45);
            var (second, _) = BuildSplitSrWorld(0.3, 0.55, 0.45);
            first.Run(minimumGroupSaving: 300_000);
            second.Run(minimumGroupSaving: 300_000);

            var generated = first.Fixture.Output.Keys.Where(key => key.Contains("_repack_")).OrderBy(key => key).ToList();
            Assert.That(generated, Has.Count.EqualTo(4));
            Assert.That(
                second.Fixture.Output.Keys.Where(key => key.Contains("_repack_")).OrderBy(key => key),
                Is.EqualTo(generated));
            foreach (var path in generated)
                Assert.That(second.Fixture.Output[path], Is.EqualTo(first.Fixture.Output[path]), path);
        }

        [Test]
        public void SharedRepack_SplitSearch_IsBounded_NeverDuplicatesAChart_AndUsesAtMostFourPages()
        {
            // 100 clusters of one chart each, sizes from 64 to 320.
            var sizes = new List<(int Width, int Height)>();
            for (var index = 0; index < 100; index++)
                sizes.Add((64 + index * 7 % 5 * 64, 64 + index * 5 % 4 * 64));
            var clusters = Enumerable.Range(0, sizes.Count).Select(index => new List<int> { index }).ToList();
            var arguments = new object?[]
            {
                sizes, clusters, 4096, 4096, (Func<int, int, long>)((width, height) => (long)width * height), null, null,
            };

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var pages = (List<List<int>>?)GetStaticMethod("FindSharedRepackSplit").Invoke(null, arguments);
            watch.Stop();

            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(20)));
            Assert.That(pages, Is.Not.Null);
            Assert.That(pages!.Count, Is.InRange(2, 4));
            Assert.That(pages.SelectMany(page => page).OrderBy(index => index), Is.EqualTo(Enumerable.Range(0, sizes.Count)));
        }

        // --- Exact texture compaction -----------------------------------------------------

        private const string CxTexture = "textures\\cx.dds";
        private const string CxMaterial = "materials\\cx.xml.material";

        private static byte[] CxBc1Block(ushort c0, ushort c1, uint indices)
        {
            var block = new byte[8];
            BitConverter.GetBytes(c0).CopyTo(block, 0);
            BitConverter.GetBytes(c1).CopyTo(block, 2);
            BitConverter.GetBytes(indices).CopyTo(block, 4);
            return block;
        }

        private static byte[] CxBc3Block(byte a0, byte a1, ulong alphaIndices, byte[] colour)
        {
            var block = new byte[16];
            block[0] = a0;
            block[1] = a1;
            for (var index = 0; index < 6; index++)
                block[2 + index] = (byte)(alphaIndices >> (8 * index));
            colour.CopyTo(block, 8);
            return block;
        }

        // Independent decoder: texels as (r, g, b, a) packed from the low byte up.
        private static uint[] CxDecode(string format, byte[] block)
        {
            var colour = format == "BC1" ? block : block[8..];
            int c0 = colour[0] | (colour[1] << 8);
            int c1 = colour[2] | (colour[3] << 8);
            (int R, int G, int B) Expand(int c)
            {
                var r = (c >> 11) & 31;
                var g = (c >> 5) & 63;
                var b = c & 31;
                return ((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2));
            }

            var p0 = Expand(c0);
            var p1 = Expand(c1);
            var palette = new (int R, int G, int B, int A)[4];
            palette[0] = (p0.R, p0.G, p0.B, 255);
            palette[1] = (p1.R, p1.G, p1.B, 255);
            if (format != "BC1" || c0 > c1)
            {
                palette[2] = ((2 * p0.R + p1.R) / 3, (2 * p0.G + p1.G) / 3, (2 * p0.B + p1.B) / 3, 255);
                palette[3] = ((p0.R + 2 * p1.R) / 3, (p0.G + 2 * p1.G) / 3, (p0.B + 2 * p1.B) / 3, 255);
            }
            else
            {
                palette[2] = ((p0.R + p1.R) / 2, (p0.G + p1.G) / 2, (p0.B + p1.B) / 2, 255);
                palette[3] = (0, 0, 0, 0);
            }

            var alpha = new int[16];
            Array.Fill(alpha, 255);
            if (format == "BC3")
            {
                int a0 = block[0], a1 = block[1];
                var table = new int[8];
                table[0] = a0;
                table[1] = a1;
                if (a0 > a1)
                {
                    for (var k = 1; k <= 6; k++)
                        table[1 + k] = ((7 - k) * a0 + k * a1) / 7;
                }
                else
                {
                    for (var k = 1; k <= 4; k++)
                        table[1 + k] = ((5 - k) * a0 + k * a1) / 5;
                    table[6] = 0;
                    table[7] = 255;
                }

                ulong bits = 0;
                for (var index = 0; index < 6; index++)
                    bits |= (ulong)block[2 + index] << (8 * index);
                for (var texel = 0; texel < 16; texel++)
                    alpha[texel] = table[(int)((bits >> (3 * texel)) & 7)];
            }
            else if (format == "BC2")
            {
                var bits = BitConverter.ToUInt64(block, 0);
                for (var texel = 0; texel < 16; texel++)
                    alpha[texel] = (int)((bits >> (4 * texel)) & 15) * 17;
            }

            var indices = BitConverter.ToUInt32(colour, 4);
            var texels = new uint[16];
            for (var texel = 0; texel < 16; texel++)
            {
                var entry = palette[(int)((indices >> (2 * texel)) & 3)];
                var a = format == "BC1" ? entry.A : alpha[texel];
                texels[texel] = (uint)(entry.R | (entry.G << 8) | (entry.B << 16) | (a << 24));
            }

            return texels;
        }

        // A DDS whose blocks come from the factory (level, block index).
        private static byte[] CxBuild(
            int width,
            int height,
            int mips,
            string format,
            bool dx10,
            bool srgb,
            Func<int, int, byte[]> blockFactory,
            int dxgiFormat = 0)
        {
            var dds = BuildBcDds(width, height, mips, format, dx10, srgb, dxgiFormat);
            var bytesPerBlock = format == "BC1" ? 8 : 16;
            var offset = dx10 ? 148 : 128;
            for (var level = 0; level < mips; level++)
            {
                var blocks = Math.Max(1, (Math.Max(1, width >> level) + 3) / 4) *
                             Math.Max(1, (Math.Max(1, height >> level) + 3) / 4);
                for (var block = 0; block < blocks; block++)
                {
                    blockFactory(level, block).CopyTo(dds, offset);
                    offset += bytesPerBlock;
                }
            }

            return dds;
        }

        // Every texel of every valid position of every level of a DDS, as decoded texels.
        private static List<uint> CxTexels(byte[] dds)
        {
            var layout = GetLayout(dds);
            var format = Get<string>(layout, "Format");
            var bytesPerBlock = Get<int>(layout, "BytesPerBlock");
            var width = Get<int>(layout, "Width");
            var height = Get<int>(layout, "Height");
            var offset = Get<int>(layout, "HeaderSize");
            var texels = new List<uint>();
            for (var level = 0; level < Get<int>(layout, "MipCount"); level++)
            {
                var w = Math.Max(1, width >> level);
                var h = Math.Max(1, height >> level);
                var blocksWide = (w + 3) / 4;
                var blocksHigh = (h + 3) / 4;
                for (var by = 0; by < blocksHigh; by++)
                {
                    for (var bx = 0; bx < blocksWide; bx++)
                    {
                        var decoded = CxDecode(format, dds[offset..(offset + bytesPerBlock)]);
                        offset += bytesPerBlock;
                        for (var y = 0; y < Math.Min(4, h - by * 4); y++)
                        {
                            for (var x = 0; x < Math.Min(4, w - bx * 4); x++)
                                texels.Add(decoded[y * 4 + x]);
                        }
                    }
                }
            }

            return texels;
        }

        private static DownscaleFixture CxFixture(byte[] dds, string path = CxTexture)
        {
            var fixture = new DownscaleFixture();
            fixture.Add(path, dds);
            fixture.AddMaterial(CxMaterial, ("t_xml_base_colour", path));
            return fixture;
        }

        private static (object Service, object State) CxRun(
            DownscaleFixture fixture,
            bool enabled = true,
            Action<object>? configure = null)
        {
            var (service, state) = CreateDownscaleState(fixture, false);
            SetStateProperty(state, "CompactTextures", enabled);
            configure?.Invoke(state);
            GetServiceType()
                .GetMethod("RunTextureCompaction", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [state, CancellationToken.None, null]);
            return (service, state);
        }

        private static object CxResult(object state) => Get<object>(state, "TextureCompaction");

        private static int CxCompacted(object state)
            => Get<IEnumerable>(CxResult(state), "Compacted").Cast<object>().Count();

        private static Dictionary<string, int> CxSkipped(object state)
            => Get<Dictionary<string, int>>(CxResult(state), "Skipped");

        private static List<string> CxValidate(object state)
        {
            var errors = new List<string>();
            GetStaticMethod("ValidateTextureCompaction").Invoke(null, [state, errors, CancellationToken.None]);
            return errors;
        }

        private static string CxReport(object state)
        {
            var builder = new StringBuilder();
            GetStaticMethod("AppendTextureCompactionSection").Invoke(null, [builder, state]);
            return builder.ToString();
        }

        // c0 = c1 = 0x1234 decodes to rgb (16, 69, 165).
        private const uint CxFlatRgb = 16u | (69u << 8) | (165u << 16);

        private static byte[] CxFlatBlock(string format)
            => format == "BC1"
                ? CxBc1Block(0x1234, 0x1234, 0)
                : CxBc3Block(200, 200, 0, CxBc1Block(0x1234, 0x1234, 0));

        [TestCase("BC3", false, false, TestName = "Compaction_UniformBc3_Legacy_BecomesFourByFour")]
        [TestCase("BC1", false, false, TestName = "Compaction_UniformBc1_Legacy_BecomesFourByFour")]
        [TestCase("BC3", true, true, TestName = "Compaction_UniformBc3_Dx10Srgb_BecomesFourByFour")]
        public void Compaction_UniformTexture_BecomesFourByFour_WithTheSameColour(string format, bool dx10, bool srgb)
        {
            var source = CxBuild(1024, 1024, 11, format, dx10, srgb, (_, _) => CxFlatBlock(format));
            var fixture = CxFixture(source);

            var (_, state) = CxRun(fixture);

            Assert.That(CxCompacted(state), Is.EqualTo(1));
            var output = fixture.Output[CxTexture];
            var (width, height, mips, outFormat, outSrgb, outDx10, chain) = ReadDds(output);
            Assert.That((width, height, mips), Is.EqualTo((4, 4, 3)));
            Assert.That((outFormat, outSrgb, outDx10), Is.EqualTo((format, srgb, dx10)));
            Assert.That(output.Length, Is.EqualTo((dx10 ? 148 : 128) + chain));
            Assert.That(output.Length, Is.LessThan(source.Length / 1000));
            var texels = CxTexels(output);
            Assert.That(texels, Has.Count.EqualTo(16 + 4 + 1));
            var alpha = format == "BC1" ? 255u : 200u;
            Assert.That(texels, Has.All.EqualTo(CxFlatRgb | (alpha << 24)));
            // The same block, verbatim, in every level.
            var blockSize = format == "BC1" ? 8 : 16;
            var headerSize = dx10 ? 148 : 128;
            for (var level = 0; level < 3; level++)
            {
                Assert.That(
                    output[(headerSize + level * blockSize)..(headerSize + (level + 1) * blockSize)],
                    Is.EqualTo(CxFlatBlock(format)));
            }

            Assert.That(CxValidate(state), Is.Empty);
        }

        [Test]
        public void Compaction_UniformAtTheTopButNotAtALowerMip_IsNotShrunk()
        {
            var source = CxBuild(
                1024, 1024, 11, "BC1", true, true,
                (level, block) => level == 3 && block == 5
                    ? CxBc1Block(0x1235, 0x1235, 0)
                    : CxFlatBlock("BC1"));
            var fixture = CxFixture(source);

            var (_, state) = CxRun(fixture);

            Assert.That(CxCompacted(state), Is.Zero);
            Assert.That(fixture.Output[CxTexture], Is.EqualTo(source));
            Assert.That(CxSkipped(state).Keys, Has.Some.Contain("nothing exact to gain"));
        }

        // Blocks of every colour mode: c0 > c1, c0 < c1 and c0 == c1, with scattered indices;
        // alpha 255 through the 8-alpha mode or through index 7 of the 6-alpha mode.
        private static byte[] CxOpaqueBlock(int level, int block, string format)
        {
            var indices = unchecked((uint)((block + 1) * 2654435761u + level * 97));
            var colour = (block % 3) switch
            {
                0 => CxBc1Block(0xF800, 0x001F, indices),
                1 => CxBc1Block(0x001F, 0xF800, indices),
                _ => CxBc1Block(0x07E0, 0x07E0, indices),
            };
            if (format == "BC2")
            {
                var bytes = new byte[16];
                Array.Fill(bytes, (byte)0xFF, 0, 8);
                colour.CopyTo(bytes, 8);
                return bytes;
            }

            return block % 2 == 0
                ? CxBc3Block(255, 255, 0xFFFFFFFFFFFFUL, colour)
                : CxBc3Block(255, 100, 0, colour);
        }

        [TestCase("BC3", false, false, 0, TestName = "Compaction_OpaqueBc3_Legacy_BecomesBc1")]
        [TestCase("BC3", true, true, 0, TestName = "Compaction_OpaqueBc3_Dx10Srgb_BecomesBc1")]
        [TestCase("BC3", true, false, 0, TestName = "Compaction_OpaqueBc3_Dx10Unorm_BecomesBc1")]
        [TestCase("BC2", true, true, 75, TestName = "Compaction_OpaqueBc2_Dx10Srgb_BecomesBc1")]
        public void Compaction_OpaqueBc3_BecomesBc1_AndDecodesIdenticallyAtEveryLevel(
            string format, bool dx10, bool srgb, int dxgi)
        {
            var source = CxBuild(
                64, 64, 7, format, dx10, srgb, (level, block) => CxOpaqueBlock(level, block, format), dxgi);
            var fixture = CxFixture(source);

            var (_, state) = CxRun(fixture);

            Assert.That(CxCompacted(state), Is.EqualTo(1), string.Join(";", CxSkipped(state).Keys));
            var output = fixture.Output[CxTexture];
            var (width, height, mips, outFormat, outSrgb, outDx10, chain) = ReadDds(output);
            Assert.That((width, height, mips), Is.EqualTo((64, 64, 7)));
            Assert.That((outFormat, outSrgb, outDx10), Is.EqualTo(("BC1", srgb, dx10)));
            Assert.That(output.Length, Is.EqualTo((dx10 ? 148 : 128) + chain));
            Assert.That(output.Length, Is.LessThan(source.Length));
            if (dx10)
                Assert.That(BitConverter.ToInt32(output, 128), Is.EqualTo(srgb ? 72 : 71));
            else
                Assert.That(System.Text.Encoding.ASCII.GetString(output, 84, 4), Is.EqualTo("DXT1"));
            Assert.That(BitConverter.ToInt32(output, 20), Is.EqualTo(16 * 16 * 8));
            // Texel for texel, RGBA, every level, including the swapped and equal-endpoint blocks.
            var before = CxTexels(source);
            var after = CxTexels(output);
            Assert.That(before, Has.All.Matches<uint>(texel => texel >> 24 == 255));
            Assert.That(after, Is.EqualTo(before));
            Assert.That(CxValidate(state), Is.Empty);
        }

        [Test]
        public void Compaction_Bc3WithOneTexelOfAlpha254_IsUntouched()
        {
            var source = CxBuild(
                64, 64, 7, "BC3", true, true,
                (level, block) => level == 0 && block == 7
                    ? CxBc3Block(255, 254, 1UL, CxBc1Block(0x07E0, 0x07E0, 0x1B))
                    : CxOpaqueBlock(level, block, "BC3"));
            var fixture = CxFixture(source);

            var (_, state) = CxRun(fixture);

            Assert.That(CxCompacted(state), Is.Zero);
            Assert.That(fixture.Output[CxTexture], Is.EqualTo(source));
            Assert.That(CxSkipped(state).Keys, Has.Some.Contain("nothing exact to gain"));
        }

        [Test]
        public void Compaction_Bc3SixAlphaModeWithIndexSix_IsUntouched_EvenWithBothEndpoints255()
        {
            // a0 = a1 = 255 selects the 6-alpha mode; texel 0 has index 6, alpha 0.
            var source = CxBuild(
                64, 64, 7, "BC3", true, true,
                (level, block) => level == 0 && block == 3
                    ? CxBc3Block(255, 255, 6UL, CxBc1Block(0x07E0, 0x07E0, 0x1B))
                    : CxOpaqueBlock(level, block, "BC3"));
            var fixture = CxFixture(source);

            var (_, state) = CxRun(fixture);

            Assert.That(CxCompacted(state), Is.Zero);
            Assert.That(fixture.Output[CxTexture], Is.EqualTo(source));
        }

        [Test]
        public void Compaction_NormalMapLikeBc3_WhereAlphaCarriesData_IsUntouched()
        {
            var source = CxBuild(
                64, 64, 7, "BC3", true, false,
                (level, block) => CxBc3Block(
                    (byte)(200 - block % 50), (byte)(40 + block % 30), unchecked((ulong)block * 0x9E3779B97F4A7C15UL),
                    CxBc1Block((ushort)(0x8410 + block), (ushort)(0x7BEF - block), (uint)(block * 31))));
            var fixture = CxFixture(source);

            var (_, state) = CxRun(fixture);

            Assert.That(CxCompacted(state), Is.Zero);
            Assert.That(fixture.Output[CxTexture], Is.EqualTo(source));
        }

        [Test]
        public void Compaction_SkipReasons_AreCountedAndTheFilesStayAsTheyWere()
        {
            var flat = CxBuild(256, 256, 9, "BC1", true, true, (_, _) => CxFlatBlock("BC1"));
            var fixture = new DownscaleFixture();
            foreach (var name in new[] { "ok", "ui", "orphan", "generated", "downscaled", "vanilla" })
                fixture.Add($"textures\\{name}.dds", flat);
            fixture.Add("textures\\generated_extra.dds", flat);
            fixture.AddMaterial(
                "materials\\cx.xml.material",
                ("t_xml_base_colour", "textures\\ok.dds"),
                ("t_xml_normal", "textures\\ui.dds"),
                ("t_xml_mask", "textures\\generated.dds"),
                ("t_xml_material_map", "textures\\downscaled.dds"));
            fixture.AddText("ui\\screen.txt", "icon textures\\ui.dds");
            // A texture that exists only in the output (not a source texture).
            fixture.Output["textures\\only_output.dds"] = flat;
            // A BC7 texture is not decoded.
            fixture.AddTexture("textures\\bc7.dds", 256, 1, "BC3", dx10: true, dxgiFormat: 98);
            fixture.AddMaterial("materials\\bc7.xml.material", ("t_xml_base_colour", "textures\\bc7.dds"));

            var (_, state) = CxRun(
                fixture,
                configure: batch =>
                {
                    Get<HashSet<string>>(batch, "GeneratedTexturePaths").Add("textures\\generated.dds");
                    var downscale = Activator.CreateInstance(GetNestedType("TextureDownscaleResult"))!;
                    var candidate = GetNestedType("TextureDownscaleCandidate").GetConstructors().Single().Invoke(
                    [
                        "textures\\downscaled.dds", 256, 256, 128, 128, 0.5, "BC1", "BC1", true, true, true, 1,
                        100L, 50L, 1, 1, "x", 1.0,
                    ]);
                    Get<IList>(downscale, "Rewritten").Add(candidate);
                    SetStateProperty(batch, "TextureDownscale", downscale);
                });

            var result = CxResult(state);
            var skipped = CxSkipped(state);
            // ok and the generated texture (an atlas output is compacted like a source texture).
            Assert.That(CxCompacted(state), Is.EqualTo(2));
            Assert.That(skipped.Single(item => item.Key.Contains("non-material")).Value, Is.EqualTo(1));
            Assert.That(skipped.Single(item => item.Key.Contains("not referenced by any material")).Value, Is.EqualTo(3));
            Assert.That(skipped.Single(item => item.Key.Contains("downscaled")).Value, Is.EqualTo(1));
            Assert.That(skipped.Single(item => item.Key.Contains("not a texture of the source pack")).Value, Is.EqualTo(1));
            Assert.That(skipped.Single(item => item.Key.Contains("BC7")).Value, Is.EqualTo(1));
            foreach (var name in new[] { "ui", "orphan", "downscaled", "generated_extra" })
                Assert.That(fixture.Output[$"textures\\{name}.dds"], Is.EqualTo(flat), name);
            Assert.That(fixture.Output["textures\\ok.dds"].Length, Is.LessThan(flat.Length));
            Assert.That(fixture.Output["textures\\generated.dds"].Length, Is.LessThan(flat.Length));
            // Everything is accounted for.
            Assert.That(Get<int>(result, "Considered"), Is.EqualTo(CxCompacted(state) + skipped.Values.Sum()));
            Assert.That(CxReport(state), Does.Contain("(reconciles)"));
            Assert.That(CxValidate(state), Is.Empty);
        }

        // --- Atlas and repack outputs are compacted too ---------------------------------------

        private static (DownscaleFixture Fixture, object State) CxRunGenerated(
            byte[] texture,
            string collection,
            string shader = "weighted4_character",
            string slot = "t_xml_base_colour",
            bool dropAlpha = false)
        {
            // Only in the output: a generated texture is not a file of the source pack.
            var fixture = new DownscaleFixture();
            fixture.Output[CxTexture] = texture;
            fixture.AddText(CxMaterial, CxMaterialWith(shader, (slot, CxTexture)));
            var (_, state) = CxRun(
                fixture,
                configure: batch =>
                {
                    SetStateProperty(batch, "DropUnreadAlpha", dropAlpha);
                    Get<HashSet<string>>(batch, collection).Add(CxTexture);
                });
            return (fixture, state);
        }

        [TestCase("GeneratedTexturePaths", TestName = "Compaction_OpaqueBc3AtlasPage_BecomesBc1")]
        [TestCase("SharedRepackTexturePaths", TestName = "Compaction_OpaqueBc3RepackTexture_BecomesBc1")]
        public void Compaction_GeneratedOpaqueBc3_BecomesBc1_AndVerifies(string collection)
        {
            var source = CxBuild(64, 64, 7, "BC3", true, true, (level, block) => CxOpaqueBlock(level, block, "BC3"));

            var (fixture, state) = CxRunGenerated(source, collection);

            Assert.That(CxCompacted(state), Is.EqualTo(1), string.Join(";", CxSkipped(state).Keys));
            var output = fixture.Output[CxTexture];
            Assert.That(ReadDds(output).Format, Is.EqualTo("BC1"));
            Assert.That(CxTexels(output), Is.EqualTo(CxTexels(source)));
            Assert.That(Get<bool>(Get<IEnumerable>(CxResult(state), "Compacted").Cast<object>().Single(), "Generated"), Is.True);
            Assert.That(CxValidate(state), Is.Empty);
            Assert.That(CxReport(state), Does.Contain("of which atlas or repack outputs: 1").And.Contain("[atlas or repack output]"));
        }

        [Test]
        public void Compaction_GeneratedBc3ThatKeepsRealAlpha_IsLeftAlone()
        {
            var source = CxAlphaTexture();

            // Rule off: nothing exact to gain.
            var (fixture, state) = CxRunGenerated(source, "GeneratedTexturePaths");
            Assert.That(CxCompacted(state), Is.Zero);
            Assert.That(fixture.Output[CxTexture], Is.EqualTo(source));
            Assert.That(CxSkipped(state).Keys, Has.Some.Contain("nothing exact to gain"));

            // Rule on, a shader that reads the alpha of the slot: refused.
            var (fixtureRead, stateRead) = CxRunGenerated(
                source, "GeneratedTexturePaths", "weighted4_character_alpha", dropAlpha: true);
            Assert.That(CxCompacted(stateRead), Is.Zero);
            Assert.That(fixtureRead.Output[CxTexture], Is.EqualTo(source));
            Assert.That(CxSkipped(stateRead).Keys, Has.Some.Contain("reads alpha of t_xml_base_colour"));
        }

        [Test]
        public void Compaction_GeneratedBc3WhoseMaterialsNeverReadTheAlpha_DropsItWithoutTheVanillaDoubts()
        {
            var source = CxAlphaTexture();

            // No vanilla data is loaded here, which blocks a source texture; a generated one cannot
            // override a vanilla texture, so its generated materials' shaders decide.
            var (fixture, state) = CxRunGenerated(source, "GeneratedTexturePaths", dropAlpha: true);

            Assert.That(CxCompacted(state), Is.EqualTo(1), string.Join(";", CxSkipped(state).Keys));
            AssertCxRgbKept(source, fixture.Output[CxTexture]);
            Assert.That(CxReport(state), Does.Contain("[t_xml_base_colour by weighted4_character]"));
            Assert.That(CxValidate(state), Is.Empty);
        }

        [Test]
        public void Compaction_OfRepackTextures_DoesNotFailTheRepackValidation()
        {
            var world = BuildStandardSrWorld();
            var result = world.Run();
            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            SetStateProperty(world.State, "CompactTextures", true);
            GetServiceType()
                .GetMethod("RunTextureCompaction", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(world.Service, [world.State, CancellationToken.None, null]);

            // The opaque BC3 normal atlas became BC1: no longer the planned format, still valid.
            var compacted = Get<IEnumerable>(CxResult(world.State), "Compacted").Cast<object>().ToList();
            Assert.That(compacted, Is.Not.Empty);
            Assert.That(compacted.All(entry => Get<bool>(entry, "Generated")), Is.True);
            Assert.That(SrValidate(world), Is.Empty);
            Assert.That(CxValidate(world.State), Is.Empty);
        }

        [Test]
        public void Compaction_OptionOff_RunsNothing_AndTheReportSaysSo()
        {
            var source = CxBuild(1024, 1024, 11, "BC3", true, true, (_, _) => CxFlatBlock("BC3"));
            var fixture = CxFixture(source);

            var (_, state) = CxRun(fixture, enabled: false);

            Assert.That(fixture.Output[CxTexture], Is.EqualTo(source));
            Assert.That(Get<bool>(CxResult(state), "Requested"), Is.False);
            Assert.That(CxReport(state), Does.Contain("Exact texture compaction: NO").And.Contain("Off (opt-in)"));
            Assert.That(CxValidate(state), Is.Empty);
        }

        [Test]
        public void Compaction_TwoRuns_ProduceIdenticalBytes()
        {
            byte[] Run(out string report)
            {
                var fixture = new DownscaleFixture();
                fixture.Add("textures\\a.dds", CxBuild(256, 256, 9, "BC3", true, true, (_, _) => CxFlatBlock("BC3")));
                fixture.Add("textures\\b.dds", CxBuild(64, 64, 7, "BC3", false, false, (l, b) => CxOpaqueBlock(l, b, "BC3")));
                fixture.AddMaterial(
                    CxMaterial, ("t_xml_base_colour", "textures\\a.dds"), ("t_xml_normal", "textures\\b.dds"));
                var (_, state) = CxRun(fixture);
                report = CxReport(state);
                return fixture.Output["textures\\a.dds"].Concat(fixture.Output["textures\\b.dds"]).ToArray();
            }

            var first = Run(out var firstReport);
            var second = Run(out var secondReport);

            Assert.That(second, Is.EqualTo(first));
            Assert.That(secondReport.Split('\n').Where(line => !line.StartsWith("Analysis time")), Is.EqualTo(
                firstReport.Split('\n').Where(line => !line.StartsWith("Analysis time"))));
        }

        [Test]
        public void Compaction_Report_ListsOutcomesBytesAndReconciles()
        {
            var fixture = new DownscaleFixture();
            fixture.Add("textures\\flat.dds", CxBuild(512, 512, 10, "BC3", true, true, (_, _) => CxFlatBlock("BC3")));
            fixture.Add("textures\\opaque.dds", CxBuild(64, 64, 7, "BC3", true, true, (l, b) => CxOpaqueBlock(l, b, "BC3")));
            fixture.AddTexture("textures\\busy.dds", 64, 7, "BC3");
            fixture.AddMaterial(
                CxMaterial,
                ("t_xml_base_colour", "textures\\flat.dds"),
                ("t_xml_normal", "textures\\opaque.dds"),
                ("t_xml_mask", "textures\\busy.dds"));

            var (_, state) = CxRun(fixture);

            Assert.That(CxCompacted(state), Is.EqualTo(2));
            var report = CxReport(state);
            Assert.That(report, Does.Contain("Uniform, shrunk to 4x4: 1"));
            Assert.That(report, Does.Contain("BC2/BC3 transcoded to BC1: 1"));
            Assert.That(report, Does.Contain("Skipped: 1"));
            Assert.That(report, Does.Contain("(reconciles)"));
            Assert.That(report, Does.Contain("textures\\flat.dds: uniform, 512x512 BC3_SRGB DX10 -> 4x4 BC3"));
            Assert.That(report, Does.Contain("textures\\opaque.dds: opaque, 64x64 BC3_SRGB DX10 -> 64x64 BC1"));
            Assert.That(report, Does.Contain("total saved"));
            Assert.That(CxValidate(state), Is.Empty);
        }

        [Test]
        public void Compaction_Validation_FailsWhenACompactedTextureIsCorruptedLater()
        {
            var source = CxBuild(64, 64, 7, "BC3", true, true, (l, b) => CxOpaqueBlock(l, b, "BC3"));
            var fixture = CxFixture(source);
            var (_, state) = CxRun(fixture);
            Assert.That(CxValidate(state), Is.Empty);

            fixture.Output[CxTexture][148 + 3] ^= 0xFF;

            Assert.That(CxValidate(state), Has.Some.Contain(CxTexture));
        }

        // A rigid whose embedded material names the given textures (old WH2 slot types).
        private static byte[] CxRigidBytes(params (Shared.GameFormats.RigidModel.Types.TextureType Type, string Path)[] textures)
        {
            var model = CreateWeightedModel(1f);
            foreach (var (type, path) in textures)
                model.Material.SetTexture(type, path);
            var rigid = CreateSingleLodRigid(RmvVersionEnum.RMV2_V8, model);
            rigid.RecalculateOffsets();
            return ModelFactory.Create().Save(rigid, validateByReloading: true, logProgress: false);
        }

        private const string CxRigid = "variantmeshes\\wh_main\\cx.rigid_model_v2";

        [Test]
        public void Compaction_RigidEmbeddedOnlyOpaqueBc3_IsTranscoded_AndMarkedInTheReport()
        {
            var source = CxBuild(64, 64, 7, "BC3", true, true, (l, b) => CxOpaqueBlock(l, b, "BC3"));
            var fixture = new DownscaleFixture();
            fixture.Add("textures\\shield.dds", source);
            fixture.Add(CxRigid, CxRigidBytes((Shared.GameFormats.RigidModel.Types.TextureType.Diffuse, "textures\\shield.dds")));

            var (_, state) = CxRun(fixture);

            Assert.That(CxCompacted(state), Is.EqualTo(1), string.Join(";", CxSkipped(state).Keys));
            var output = fixture.Output["textures\\shield.dds"];
            Assert.That(ReadDds(output).Format, Is.EqualTo("BC1"));
            Assert.That(CxTexels(output), Is.EqualTo(CxTexels(source)));
            var report = CxReport(state);
            Assert.That(report, Does.Contain("[rigid-embedded only]"));
            Assert.That(report, Does.Contain("(reconciles)"));
            Assert.That(CxValidate(state), Is.Empty);
        }

        [Test]
        public void Compaction_RigidEmbeddedOnlyUniformTexture_Shrinks()
        {
            var source = CxBuild(256, 256, 9, "BC1", false, false, (_, _) => CxFlatBlock("BC1"));
            var fixture = new DownscaleFixture();
            fixture.Add("textures\\gloss.dds", source);
            fixture.Add(CxRigid, CxRigidBytes((Shared.GameFormats.RigidModel.Types.TextureType.Gloss, "textures\\gloss.dds")));

            var (_, state) = CxRun(fixture);

            Assert.That(CxCompacted(state), Is.EqualTo(1));
            Assert.That(ReadDds(fixture.Output["textures\\gloss.dds"]).Width, Is.EqualTo(4));
        }

        [Test]
        public void Compaction_TextureNamedByARigidAndATextFile_IsStillSkippedAsNonMaterial()
        {
            var source = CxBuild(256, 256, 9, "BC1", false, false, (_, _) => CxFlatBlock("BC1"));
            var fixture = new DownscaleFixture();
            fixture.Add("textures\\both.dds", source);
            fixture.Add(CxRigid, CxRigidBytes((Shared.GameFormats.RigidModel.Types.TextureType.Diffuse, "textures\\both.dds")));
            fixture.AddText("variantmeshes\\wh_main\\cx.variantmeshdefinition", "<x>textures\\both.dds</x>");

            var (_, state) = CxRun(fixture);

            Assert.That(CxCompacted(state), Is.Zero);
            Assert.That(fixture.Output["textures\\both.dds"], Is.EqualTo(source));
            Assert.That(CxSkipped(state).Keys, Has.Some.Contain("non-material"));
        }

        [Test]
        public void Compaction_ReferenceCountsLine_CountsXmlRigidAndRigidOnlyTextures_AndUnreadableRigids()
        {
            var flat = CxBuild(256, 256, 9, "BC1", false, false, (_, _) => CxFlatBlock("BC1"));
            var fixture = new DownscaleFixture();
            fixture.Add("textures\\xml_only.dds", flat);
            fixture.Add("textures\\both.dds", flat);
            fixture.Add("textures\\rigid_only.dds", flat);
            fixture.Add("textures\\nobody.dds", flat);
            fixture.Add("textures\\from_bad_rigid.dds", flat);
            fixture.AddMaterial(
                CxMaterial, ("t_xml_base_colour", "textures\\xml_only.dds"), ("t_xml_normal", "textures\\both.dds"));
            fixture.Add(
                CxRigid,
                CxRigidBytes((Shared.GameFormats.RigidModel.Types.TextureType.Diffuse, "textures\\both.dds"), (Shared.GameFormats.RigidModel.Types.TextureType.Gloss, "textures\\rigid_only.dds")));
            fixture.Add(
                "variantmeshes\\wh_main\\broken.rigid_model_v2",
                System.Text.Encoding.UTF8.GetBytes("not a rigid textures\\from_bad_rigid.dds"));

            var (_, state) = CxRun(fixture);

            var report = CxReport(state);
            Assert.That(
                report,
                Does.Contain("Referenced by xml material slots: 2; by rigid-embedded materials: 2 (1 only there); rigids read: 1 (unreadable 1)."));
            Assert.That(CxCompacted(state), Is.EqualTo(3));
            Assert.That(CxSkipped(state).Single(item => item.Key.Contains("non-material")).Value, Is.EqualTo(1));
            Assert.That(CxSkipped(state).Single(item => item.Key.Contains("rigid-embedded")).Value, Is.EqualTo(1));
            Assert.That(report, Does.Contain("(reconciles)"));
        }

        // --- Dropping alpha no shader reads ------------------------------------------------

        // Real, varying alpha (8-alpha mode, 200 down to 40) over the colour blocks of the
        // opaque fixture.
        private static byte[] CxAlphaBlock(int level, int block)
            => CxBc3Block(
                200, 40, unchecked((ulong)(block + 1) * 0x9E3779B97F4A7C15UL) & 0xFFFFFFFFFFFFUL,
                CxOpaqueBlock(level, block, "BC3")[8..16]);

        private static byte[] CxAlphaTexture(bool dx10 = true, bool srgb = true)
            => CxBuild(64, 64, 7, "BC3", dx10, srgb, CxAlphaBlock);

        private static string CxMaterialWith(string shader, params (string Slot, string Texture)[] textures)
            => DownscaleMaterialXml(textures).Replace(
                "shaders\\weighted2_character.xml.shader", shader == null ? string.Empty : $"shaders\\{shader}.xml.shader");

        private static (object Service, object State) CxRunAlpha(
            DownscaleFixture fixture,
            bool dropAlpha = true,
            Action<object>? configure = null)
            => CxRun(
                fixture,
                configure: state =>
                {
                    SetStateProperty(state, "DropUnreadAlpha", dropAlpha);
                    configure?.Invoke(state);
                });

        private static DownscaleFixture CxAlphaFixture(string shader, string slot, byte[]? texture = null)
        {
            var fixture = new DownscaleFixture();
            fixture.Add(CxTexture, texture ?? CxAlphaTexture());
            fixture.AddText(CxMaterial, CxMaterialWith(shader, (slot, CxTexture)));
            return fixture;
        }

        private static void AssertCxRgbKept(byte[] source, byte[] output)
        {
            var before = CxTexels(source);
            var after = CxTexels(output);
            Assert.That(after, Has.Count.EqualTo(before.Count));
            for (var index = 0; index < before.Count; index++)
            {
                Assert.That(after[index] & 0x00FFFFFFu, Is.EqualTo(before[index] & 0x00FFFFFFu), $"texel {index}");
                Assert.That(after[index] >> 24, Is.EqualTo(255u));
            }

            Assert.That(before.Any(texel => texel >> 24 != 255), Is.True, "the source really has alpha");
        }

        [TestCase(true, true, TestName = "Compaction_UnreadAlpha_BaseColourOfWeighted2Character_DropsAlpha_Dx10Srgb")]
        [TestCase(false, false, TestName = "Compaction_UnreadAlpha_BaseColourOfWeighted2Character_DropsAlpha_Legacy")]
        public void Compaction_UnreadAlpha_BaseColourReadAsRgb_BecomesBc1_WithTheSameRgb(bool dx10, bool srgb)
        {
            var source = CxAlphaTexture(dx10, srgb);
            var fixture = CxAlphaFixture("weighted2_character", "t_xml_base_colour", source);

            var (_, state) = CxRunAlpha(fixture);

            Assert.That(CxCompacted(state), Is.EqualTo(1), string.Join(";", CxSkipped(state).Keys));
            var output = fixture.Output[CxTexture];
            var (width, height, mips, format, outSrgb, outDx10, chain) = ReadDds(output);
            Assert.That((width, height, mips, format, outSrgb, outDx10), Is.EqualTo((64, 64, 7, "BC1", srgb, dx10)));
            Assert.That(output.Length, Is.EqualTo((dx10 ? 148 : 128) + chain));
            AssertCxRgbKept(source, output);
            var report = CxReport(state);
            Assert.That(report, Does.Contain("Drop unread alpha: YES"));
            Assert.That(report, Does.Contain("alpha not read by any user's shader: 1"));
            Assert.That(report, Does.Contain("alpha unread").And.Contain("[t_xml_base_colour by weighted2_character]"));
            Assert.That(report, Does.Contain("alpha dropped:"));
            Assert.That(report, Does.Contain("(reconciles)"));
            Assert.That(CxValidate(state), Is.Empty);
            Assert.That(
                Get<IEnumerable>(state, "ValidationMessages").Cast<object>().Select(item => item.ToString()),
                Has.Some.Contain("1 alpha-dropped texture(s) match the source RGB"));
        }

        [Test]
        public void Compaction_UnreadAlpha_SamePictureAlsoUsedByAnAlphaShader_IsSkipped()
        {
            var source = CxAlphaTexture();
            var fixture = CxAlphaFixture("weighted2_character", "t_xml_base_colour", source);
            fixture.AddText(
                "materials\\cx_alpha.xml.material",
                CxMaterialWith("weighted2_character_alpha", ("t_xml_base_colour", CxTexture)));

            var (_, state) = CxRunAlpha(fixture);

            Assert.That(CxCompacted(state), Is.Zero);
            Assert.That(fixture.Output[CxTexture], Is.EqualTo(source));
            Assert.That(
                CxSkipped(state).Keys,
                Has.Some.Contain("shader weighted2_character_alpha reads alpha of t_xml_base_colour"));
        }

        [Test]
        public void Compaction_UnreadAlpha_MaterialMapOfWeighted4CharacterAlpha_ReadsOnlyRg_AndDropsAlpha()
        {
            var source = CxAlphaTexture(true, false);
            var fixture = CxAlphaFixture("weighted4_character_alpha", "t_xml_material_map", source);

            var (_, state) = CxRunAlpha(fixture);

            Assert.That(CxCompacted(state), Is.EqualTo(1), string.Join(";", CxSkipped(state).Keys));
            AssertCxRgbKept(source, fixture.Output[CxTexture]);
            Assert.That(CxReport(state), Does.Contain("[t_xml_material_map by weighted4_character_alpha]"));
            Assert.That(CxValidate(state), Is.Empty);
        }

        [Test]
        public void Compaction_UnreadAlpha_NormalMapWithAlpha_IsSkipped_BecauseNormalIsReadAsGa()
        {
            var source = CxAlphaTexture(true, false);
            var fixture = CxAlphaFixture("weighted2_character", "t_xml_normal", source);

            var (_, state) = CxRunAlpha(fixture);

            Assert.That(CxCompacted(state), Is.Zero);
            Assert.That(fixture.Output[CxTexture], Is.EqualTo(source));
            Assert.That(CxSkipped(state).Keys, Has.Some.Contain("reads alpha of t_xml_normal"));
        }

        [Test]
        public void Compaction_UnreadAlpha_UnknownShader_IsSkipped()
        {
            var fixture = CxAlphaFixture("no_such_shader", "t_xml_base_colour");

            var (_, state) = CxRunAlpha(fixture);

            Assert.That(CxCompacted(state), Is.Zero);
            Assert.That(CxSkipped(state).Keys, Has.Some.Contain("shader no_such_shader not in the channel table"));
        }

        [Test]
        public void Compaction_UnreadAlpha_MissingShaderElement_CountsAsUnknown()
        {
            var fixture = CxAlphaFixture("weighted2_character", "t_xml_base_colour");
            fixture.AddText(
                CxMaterial,
                "<material><textures><texture><slot>t_xml_base_colour</slot><source>" + CxTexture +
                "</source></texture></textures></material>");

            var (_, state) = CxRunAlpha(fixture);

            Assert.That(CxCompacted(state), Is.Zero);
            Assert.That(CxSkipped(state).Keys, Has.Some.Contain("shader (none) not in the channel table"));
        }

        [Test]
        public void Compaction_UnreadAlpha_SlotTheShaderDoesNotBind_IsSkipped()
        {
            var fixture = CxAlphaFixture("weighted2_character", "t_xml_not_a_real_slot");

            var (_, state) = CxRunAlpha(fixture);

            Assert.That(CxCompacted(state), Is.Zero);
            Assert.That(
                CxSkipped(state).Keys,
                Has.Some.Contain("slot t_xml_not_a_real_slot is not bound by shader weighted2_character"));
        }

        [Test]
        public void Compaction_UnreadAlpha_AlsoNamedByARigid_IsSkipped()
        {
            var source = CxAlphaTexture();
            var fixture = CxAlphaFixture("weighted2_character", "t_xml_base_colour", source);
            fixture.Add(
                CxRigid,
                CxRigidBytes((Shared.GameFormats.RigidModel.Types.TextureType.Diffuse, CxTexture)));

            var (_, state) = CxRunAlpha(fixture);

            Assert.That(CxCompacted(state), Is.Zero);
            Assert.That(fixture.Output[CxTexture], Is.EqualTo(source));
            Assert.That(CxSkipped(state).Keys, Has.Some.EqualTo(
                    "named by a rigid not drawn only through owned WSModels: the rigid is not used through any WSModel"));
        }

        [Test]
        public void Compaction_UnreadAlpha_PathOverridingAVanillaTexture_IsSkipped()
        {
            var source = CxAlphaTexture();
            var fixture = CxAlphaFixture("weighted2_character", "t_xml_base_colour", source);
            fixture.Vanilla[CxTexture] = [1];

            var (_, state) = CxRunAlpha(fixture);

            Assert.That(CxCompacted(state), Is.Zero);
            Assert.That(CxSkipped(state).Keys, Has.Some.Contain("overrides a vanilla texture"));
        }

        [Test]
        public void Compaction_UnreadAlpha_WithoutVanillaGameData_CannotRuleOutAnOverride_AndIsSkipped()
        {
            var fixture = CxAlphaFixture("weighted2_character", "t_xml_base_colour");

            var (_, state) = CxRunAlpha(
                fixture,
                configure: batch => SetStateProperty(
                    batch, "GameplayTraversalContainers", new List<Shared.Core.PackFiles.Models.IPackFileContainer>()));

            Assert.That(CxCompacted(state), Is.Zero);
            Assert.That(CxSkipped(state).Keys, Has.Some.Contain("vanilla game data is not loaded"));
        }

        [Test]
        public void Compaction_UnreadAlpha_OptionOff_DoesNotTranscode_AndAddsOnlyTheNoLine()
        {
            var source = CxAlphaTexture();
            var fixture = CxAlphaFixture("weighted2_character", "t_xml_base_colour", source);

            var (_, state) = CxRunAlpha(fixture, dropAlpha: false);

            Assert.That(CxCompacted(state), Is.Zero);
            Assert.That(fixture.Output[CxTexture], Is.EqualTo(source));
            Assert.That(CxSkipped(state).Keys, Has.Some.Contain("nothing exact to gain"));
            var report = CxReport(state);
            Assert.That(report, Does.Contain("Drop unread alpha: NO"));
            Assert.That(report, Does.Not.Contain("alpha not read by any user"));
            Assert.That(report, Does.Not.Contain("alpha dropped"));
            Assert.That(report, Does.Not.Contain("alpha unread"));
            Assert.That(report, Does.Not.Contain("channel"));
        }

        [Test]
        public void Compaction_UnreadAlpha_CountsReconcile_WithDroppedSkippedAndOpaqueTextures()
        {
            var fixture = new DownscaleFixture();
            fixture.Add("textures\\drop.dds", CxAlphaTexture());
            fixture.Add("textures\\normal.dds", CxAlphaTexture(true, false));
            fixture.Add("textures\\opaque.dds", CxBuild(64, 64, 7, "BC3", true, true, (l, b) => CxOpaqueBlock(l, b, "BC3")));
            fixture.AddText(
                "materials\\a.xml.material",
                CxMaterialWith(
                    "weighted2_character",
                    ("t_xml_base_colour", "textures\\drop.dds"),
                    ("t_xml_normal", "textures\\normal.dds"),
                    ("t_xml_mask", "textures\\opaque.dds")));

            var (_, state) = CxRunAlpha(fixture);

            Assert.That(CxCompacted(state), Is.EqualTo(2));
            var report = CxReport(state);
            Assert.That(report, Does.Contain("BC2/BC3 transcoded to BC1: 1"));
            Assert.That(report, Does.Contain("alpha not read by any user's shader: 1"));
            Assert.That(report, Does.Contain("Skipped: 1"));
            Assert.That(report, Does.Contain("shader weighted2_character reads alpha of t_xml_normal"));
            Assert.That(report, Does.Contain("(reconciles)"));
            Assert.That(CxValidate(state), Is.Empty);
        }

        [Test]
        public void Compaction_UnreadAlpha_Validation_FailsWhenTheRgbOfADroppedTextureChanges()
        {
            var fixture = CxAlphaFixture("weighted2_character", "t_xml_base_colour");
            var (_, state) = CxRunAlpha(fixture);
            Assert.That(CxValidate(state), Is.Empty);

            fixture.Output[CxTexture][148 + 4] ^= 0xFF;

            Assert.That(CxValidate(state), Has.Some.Contain(CxTexture));
        }

        // A rigid naming CxTexture, bound by an owned WSModel that uses the xml material.
        private static void CxAddRigidThroughWsModel(
            DownscaleFixture fixture,
            string rigid,
            string wsModel)
        {
            fixture.Add(rigid, CxRigidBytes((Shared.GameFormats.RigidModel.Types.TextureType.Diffuse, CxTexture)));
            fixture.AddText(wsModel, CreateWsModelDocument(rigid, [[CxMaterial]]).OuterXml);
        }

        private static void CxAddDirectConsumer(object state, string rigid)
        {
            var index = Get<object>(state, "GameplayMeshDependencyIndex");
            ((IDictionary)Get<object>(index, "DirectConsumersByRigid"))[rigid] =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "variantmeshes\\wh_main\\direct.variantmeshdefinition" };
        }

        [Test]
        public void Compaction_UnreadAlpha_TextureNamedByARigidDrawnOnlyThroughAnOwnedWsModel_IsDropped()
        {
            var source = CxAlphaTexture();
            var fixture = CxAlphaFixture("weighted2_character", "t_xml_base_colour", source);
            CxAddRigidThroughWsModel(fixture, CxRigid, "variantmeshes\\wh_main\\cx.wsmodel");

            var (_, state) = CxRunAlpha(fixture);

            Assert.That(CxCompacted(state), Is.EqualTo(1), string.Join(";", CxSkipped(state).Keys));
            AssertCxRgbKept(source, fixture.Output[CxTexture]);
            Assert.That(
                CxReport(state),
                Does.Contain("[t_xml_base_colour by weighted2_character; also named by 1 rigid(s) drawn only through WSModels]"));
            Assert.That(CxValidate(state), Is.Empty);
        }

        [Test]
        public void Compaction_UnreadAlpha_RigidReferencedDirectlyByAVmd_IsSkipped()
        {
            var source = CxAlphaTexture();
            var fixture = CxAlphaFixture("weighted2_character", "t_xml_base_colour", source);
            CxAddRigidThroughWsModel(fixture, CxRigid, "variantmeshes\\wh_main\\cx.wsmodel");

            var (_, state) = CxRunAlpha(fixture, configure: batch => CxAddDirectConsumer(batch, CxRigid));

            Assert.That(CxCompacted(state), Is.Zero);
            Assert.That(fixture.Output[CxTexture], Is.EqualTo(source));
            Assert.That(CxSkipped(state).Keys, Has.Some.Contain("referenced directly (without a WSModel)"));
        }

        [Test]
        public void Compaction_UnreadAlpha_RigidNothingReferences_IsSkipped()
        {
            var fixture = CxAlphaFixture("weighted2_character", "t_xml_base_colour");
            fixture.Add(CxRigid, CxRigidBytes((Shared.GameFormats.RigidModel.Types.TextureType.Diffuse, CxTexture)));

            var (_, state) = CxRunAlpha(fixture);

            Assert.That(CxCompacted(state), Is.Zero);
            Assert.That(CxSkipped(state).Keys, Has.Some.Contain("the rigid is not used through any WSModel"));
        }

        [Test]
        public void Compaction_UnreadAlpha_RigidOverridingAVanillaPath_IsSkipped()
        {
            var fixture = CxAlphaFixture("weighted2_character", "t_xml_base_colour");
            CxAddRigidThroughWsModel(fixture, CxRigid, "variantmeshes\\wh_main\\cx.wsmodel");
            fixture.Vanilla[CxRigid] = [1];

            var (_, state) = CxRunAlpha(fixture);

            Assert.That(CxCompacted(state), Is.Zero);
            Assert.That(CxSkipped(state).Keys, Has.Some.Contain("the rigid overrides a vanilla path"));
        }

        [Test]
        public void Compaction_UnreadAlpha_RigidReferencedByATextFile_IsSkipped()
        {
            var fixture = CxAlphaFixture("weighted2_character", "t_xml_base_colour");
            CxAddRigidThroughWsModel(fixture, CxRigid, "variantmeshes\\wh_main\\cx.wsmodel");
            fixture.AddText("variantmeshes\\notes.txt", "uses " + CxRigid);

            var (_, state) = CxRunAlpha(fixture);

            Assert.That(CxCompacted(state), Is.Zero);
            Assert.That(CxSkipped(state).Keys, Has.Some.EqualTo(
                    "named by a rigid not drawn only through owned WSModels: another file refers to the rigid other than as a WSModel binding"));
            Assert.That(
                Get<Dictionary<string, List<string>>>(CxResult(state), "SkippedExamples").Values.SelectMany(item => item),
                Has.Some.Contain("notes.txt refers to the rigid"));
        }

        [Test]
        public void Compaction_UnreadAlpha_TwoRigidsNameTheTexture_OneDirect_IsSkipped()
        {
            var fixture = CxAlphaFixture("weighted2_character", "t_xml_base_colour");
            var other = "variantmeshes\\wh_main\\cx_copy.rigid_model_v2";
            CxAddRigidThroughWsModel(fixture, CxRigid, "variantmeshes\\wh_main\\cx.wsmodel");
            CxAddRigidThroughWsModel(fixture, other, "variantmeshes\\wh_main\\cx_copy.wsmodel");

            var (_, state) = CxRunAlpha(fixture, configure: batch => CxAddDirectConsumer(batch, other));

            Assert.That(CxCompacted(state), Is.Zero);
            Assert.That(CxSkipped(state).Keys, Has.Some.Contain("referenced directly (without a WSModel)"));
            Assert.That(CxSkipped(state).Keys, Has.None.Contain(other));
            Assert.That(
                Get<Dictionary<string, List<string>>>(CxResult(state), "SkippedExamples").Values.SelectMany(item => item),
                Has.Some.Contain($"(rigid {other}: the rigid is referenced directly"));
        }

        [Test]
        public void Compaction_UnreadAlpha_RigidWithoutGameplayDiscovery_BlocksInsteadOfAllowing()
        {
            var fixture = CxAlphaFixture("weighted2_character", "t_xml_base_colour");
            CxAddRigidThroughWsModel(fixture, CxRigid, "variantmeshes\\wh_main\\cx.wsmodel");

            var (_, state) = CxRunAlpha(
                fixture,
                configure: batch =>
                {
                    SetStateProperty(batch, "StructuralMergeConsumerDiscoveryComplete", false);
                    SetStateProperty(batch, "GameplayMeshDependencyIndex", null);
                });

            Assert.That(CxCompacted(state), Is.Zero);
            Assert.That(CxSkipped(state).Keys, Has.Some.Contain("gameplay consumer discovery for the rigid is incomplete"));
        }

        [Test]
        public void Compaction_UnreadAlpha_RigidBlocks_ShareOneSkipLinePerCause_WithThePathsInTheExamples()
        {
            var fixture = new DownscaleFixture();
            for (var index = 1; index <= 2; index++)
            {
                var texture = $"textures\\cx{index}.dds";
                fixture.Add(texture, CxAlphaTexture());
                fixture.AddText(
                    $"materials\\cx{index}.xml.material",
                    CxMaterialWith("weighted2_character", ("t_xml_base_colour", texture)));
                fixture.Add(
                    $"variantmeshes\\wh_main\\cx{index}.rigid_model_v2",
                    CxRigidBytes((Shared.GameFormats.RigidModel.Types.TextureType.Diffuse, texture)));
            }

            var (_, state) = CxRunAlpha(fixture);

            const string key =
                "named by a rigid not drawn only through owned WSModels: the rigid is not used through any WSModel";
            Assert.That(CxCompacted(state), Is.Zero);
            Assert.That(CxSkipped(state)[key], Is.EqualTo(2));
            var examples = Get<Dictionary<string, List<string>>>(CxResult(state), "SkippedExamples")[key];
            Assert.That(examples, Has.Count.EqualTo(2));
            Assert.That(examples[0], Does.Contain("textures\\cx1.dds (rigid variantmeshes\\wh_main\\cx1.rigid_model_v2: "));
            Assert.That(examples[1], Does.Contain("cx2.rigid_model_v2"));
            Assert.That(CxReport(state), Does.Contain($"2 x {key}"));
        }

        // --- Bone weight audit -------------------------------------------------------------

        private static string BwClass(byte[] index, byte[] weight)
            => GetStaticMethod("ClassifyBoneWeights").Invoke(null, [index, weight])!.ToString()!;

        [TestCase(new byte[] { 255, 0, 0, 7 }, new byte[] { 1, 2, 3, 4 }, "Exact", TestName = "BoneWeights_255_0_0_IsExact_SingleBone")]
        [TestCase(new byte[] { 128, 127, 0, 7 }, new byte[] { 1, 2, 3, 4 }, "Exact", TestName = "BoneWeights_128_127_0_IsExact_NoImplicitWeight")]
        [TestCase(new byte[] { 100, 0, 0, 7 }, new byte[] { 1, 2, 3, 4 }, "Exact", TestName = "BoneWeights_100_0_0_IsExact_PlusImplicitFourth")]
        [TestCase(new byte[] { 100, 100, 0, 7 }, new byte[] { 1, 2, 3, 4 }, "NeedsFour", TestName = "BoneWeights_100_100_0_NeedsFour_BecauseOfTheImplicitFourth")]
        [TestCase(new byte[] { 100, 100, 54, 7 }, new byte[] { 1, 2, 3, 4 }, "NeedsFour", TestName = "BoneWeights_100_100_54_NeedsFour_SumIs254")]
        [TestCase(new byte[] { 127, 126, 2, 7 }, new byte[] { 1, 2, 3, 4 }, "Near", TestName = "BoneWeights_127_126_2_IsNear")]
        [TestCase(new byte[] { 100, 100, 0, 7 }, new byte[] { 5, 5, 3, 4 }, "Exact", TestName = "BoneWeights_SameBoneInTwoSlots_CountsOnce")]
        [TestCase(new byte[] { 0, 0, 0, 7 }, new byte[] { 1, 2, 3, 4 }, "Exact", TestName = "BoneWeights_AllZero_IsTheImplicitFourthAlone")]
        [TestCase(new byte[] { 100, 100, 0, 255 }, new byte[] { 1, 1, 3, 4 }, "Exact", TestName = "BoneWeights_StoredFourthByteIsIgnored_SameBoneImplicit")]
        public void BoneWeights_ClassifyAVertexFromItsBytes(byte[] weights, byte[] bones, string expected)
            => Assert.That(BwClass(bones, weights), Is.EqualTo(expected));

        private static RmvModel BwModel(VertexFormat format, params (byte[] Bones, byte[] Weights)[] vertices)
        {
            var model = CreateWeightedModel(1f);
            ((WeightedMaterial)model.Material).BinaryVertexFormat = format;
            model.Mesh.VertexList = vertices.Select(vertex => new CommonVertex
            {
                Position = new Vector4(0, 0, 0, 1),
                BoneIndex = vertex.Bones,
                // The weights as the reader makes them: byte / 255.
                BoneWeight = vertex.Weights.Select(weight => weight / 255.0f).ToArray(),
                WeightCount = 4,
            }).ToArray();
            model.Mesh.IndexList = [0, 0, 0];
            return model;
        }

        private static readonly (byte[] Bones, byte[] Weights) BwExact = ([1, 2, 3, 4], [255, 0, 0, 0]);
        private static readonly (byte[] Bones, byte[] Weights) BwNear = ([1, 2, 3, 4], [127, 126, 2, 0]);
        private static readonly (byte[] Bones, byte[] Weights) BwNeedsFour = ([1, 2, 3, 4], [100, 100, 0, 0]);

        private static (object Service, object State) BwState(
            Dictionary<string, RmvModel[][]> rigids,
            Dictionary<string, string> shaderByPart)
        {
            var (service, state) = CreateDownscaleState(new DownscaleFixture(), false);
            var checks = Activator.CreateInstance(GetNestedType("AssetChecksResult"))!;
            SetStateProperty(state, "AssetChecks", checks);
            foreach (var (path, lods) in rigids)
            {
                Get<IDictionary>(checks, "Rigids")[path] = GetNestedType("AssetCheckRigid").GetConstructors().Single().Invoke(
                    [path, true, lods.Select(lod => lod.Select(model => model.Mesh.VertexList.Length).ToArray()).ToArray()]);
                var assignments = lods.Select((lod, lodIndex) => lod
                    .Select((_, part) => $"materials\\{System.IO.Path.GetFileNameWithoutExtension(path)}_{lodIndex}_{part}.xml.material")
                    .ToArray()).ToArray();
                Get<IDictionary>(checks, "Components")[path + ".ws"] = GetNestedType("AssetCheckComponent")
                    .GetConstructors().Single().Invoke([path + ".ws", path, true, true, assignments]);
                foreach (var material in assignments.SelectMany(lod => lod))
                {
                    var entry = GetNestedType("AssetCheckMaterial").GetConstructors().Single().Invoke([material, true]);
                    entry.GetType().GetProperty("Shader")!.SetValue(
                        entry, shaderByPart.GetValueOrDefault(material, "shaders\\weighted4_character.xml.shader"));
                    Get<IDictionary>(checks, "Materials")[material] = entry;
                }
            }

            return (service, state);
        }

        private static object BwRun(
            object state,
            Dictionary<string, RmvModel[][]> rigids,
            TimeSpan? budget = null,
            Func<string, RmvFile?>? loader = null)
        {
            loader ??= path => rigids.TryGetValue(path, out var lods) ? new RmvFile { ModelList = lods } : null;
            GetStaticMethod("RunBoneWeightAudit").Invoke(null, [state, CancellationToken.None, null, loader, budget]);
            return Get<object>(state, "BoneWeightAudit");
        }

        private static List<object> BwMeshes(object result)
            => Get<IEnumerable>(result, "Meshes").Cast<object>().ToList();

        private static string BwReport(object state)
        {
            var builder = new StringBuilder();
            GetStaticMethod("AppendBoneWeightAuditSection").Invoke(null, [builder, state]);
            return builder.ToString();
        }

        [Test]
        public void BoneWeightAudit_MeshWithOneBadVertex_IsNotConvertible_AndNearOnlyIsSeparate()
        {
            var rigids = new Dictionary<string, RmvModel[][]>
            {
                ["models\\a.rigid_model_v2"] = [[
                    BwModel(VertexFormat.Cinematic, BwExact, BwExact),
                    BwModel(VertexFormat.Cinematic, BwExact, BwNeedsFour),
                    BwModel(VertexFormat.Cinematic, BwExact, BwNear),
                ]],
            };
            var (_, state) = BwState(rigids, []);

            var result = BwRun(state, rigids);

            var meshes = BwMeshes(result);
            Assert.That(meshes.Select(mesh => Get<bool>(mesh, "Convertible")), Is.EqualTo(new[] { true, false, false }));
            Assert.That(meshes.Select(mesh => Get<bool>(mesh, "NearOnly")), Is.EqualTo(new[] { false, false, true }));
            Assert.That(Get<int>(meshes[1], "NeedsFour"), Is.EqualTo(1));
            Assert.That(Get<int>(meshes[2], "Near"), Is.EqualTo(1));
        }

        [Test]
        public void BoneWeightAudit_ReportListsTotalsClassesShadersAndReconciles()
        {
            var rigids = new Dictionary<string, RmvModel[][]>
            {
                ["models\\a.rigid_model_v2"] = [
                    [BwModel(VertexFormat.Cinematic, BwExact, BwExact), BwModel(VertexFormat.Cinematic, BwNeedsFour)],
                    [BwModel(VertexFormat.Cinematic, BwExact), BwModel(VertexFormat.Weighted, BwExact)],
                ],
                ["models\\b.rigid_model_v2"] = [[BwModel(VertexFormat.Static)]],
            };
            var (_, state) = BwState(rigids, []);

            var result = BwRun(state, rigids);
            var report = BwReport(state);

            Assert.That(BwMeshes(result), Has.Count.EqualTo(5));
            Assert.That(report, Does.Contain("Cinematic: 3 mesh(es), 4 vertices"));
            Assert.That(report, Does.Contain("Weighted: 1 mesh(es), 1 vertices"));
            Assert.That(report, Does.Contain("Static: 1 mesh(es), 0 vertices"));
            Assert.That(report, Does.Contain("convertible (all vertices EXACT) 2; NEAR-only"));
            Assert.That(report, Does.Contain("needs 4 1; unreadable 0. Check: 3 of 3 (reconciles)"));
            Assert.That(report, Does.Contain("Vertex bytes saved by converting the convertible meshes (4 per vertex): 12"));
            Assert.That(report, Does.Contain("weighted4_character -> weighted2_character: 2 mesh(es), 3 vertices;"));
            Assert.That(report, Does.Contain("slot->channel map"));
            Assert.That(report, Does.Contain("models\\a.rigid_model_v2: 2 mesh(es), 3 vertices, LOD(s) 0,1"));
            Assert.That(report, Does.Contain("not computed (the army residency model is not available)"));
        }

        [Test]
        public void BoneWeightAudit_EmbeddedMaterialRigid_UsesTheMaterialTypeAsShader()
        {
            var rigids = new Dictionary<string, RmvModel[][]>
            {
                ["models\\a.rigid_model_v2"] = [[BwModel(VertexFormat.Cinematic, BwExact)]],
            };
            var (_, state) = BwState(rigids, []);
            Get<IDictionary>(Get<object>(state, "AssetChecks"), "Components").Clear();

            BwRun(state, rigids);

            Assert.That(BwReport(state), Does.Contain("embedded:weighted: 1 mesh(es), 1 vertices; embedded material, no shader to pair"));
        }

        [Test]
        public void BoneWeightAudit_UnreadableAndUnreachedRigids_AreCountedNotGuessed()
        {
            var rigids = new Dictionary<string, RmvModel[][]>
            {
                ["models\\a.rigid_model_v2"] = [[BwModel(VertexFormat.Cinematic, BwExact)]],
                ["models\\b.rigid_model_v2"] = [[BwModel(VertexFormat.Cinematic, BwExact), BwModel(VertexFormat.Cinematic, BwExact)]],
            };
            var (_, state) = BwState(rigids, []);
            var unreadable = BwRun(state, rigids, loader: _ => null);
            Assert.That(Get<int>(unreadable, "RigidsUnreadable"), Is.EqualTo(2));
            Assert.That(Get<int>(unreadable, "MeshesUnreadableRigid"), Is.EqualTo(3));

            var (_, second) = BwState(rigids, []);
            var notReached = BwRun(second, rigids, budget: TimeSpan.Zero);
            Assert.That(Get<int>(notReached, "RigidsNotReached"), Is.EqualTo(2));
            Assert.That(Get<int>(notReached, "MeshesNotReached"), Is.EqualTo(3));
            Assert.That(BwReport(second), Does.Contain("not reached within the 0 s budget 2 (3 mesh(es))"));
        }

        [Test]
        public void BoneWeightAudit_VertexWithoutFourBones_MakesTheMeshUnreadable()
        {
            var rigids = new Dictionary<string, RmvModel[][]>
            {
                ["models\\a.rigid_model_v2"] = [[BwModel(VertexFormat.Cinematic, ([1, 2], [255, 0]))]],
            };
            var (_, state) = BwState(rigids, []);

            var result = BwRun(state, rigids);

            Assert.That(Get<bool>(BwMeshes(result).Single(), "Unreadable"), Is.True);
            Assert.That(BwReport(state), Does.Contain("unreadable 1. Check: 1 of 1 (reconciles)"));
        }

        // --- BC1 atlas alpha safety -------------------------------------------------------

        private static byte[] BuildAlphaDds(string format, byte alpha)
        {
            // One 4x4 block. BC3: alpha0 = alpha1 = alpha with all indices 0, so every texel
            // has exactly this alpha. BC1 is left as the fixture's block bytes.
            var dds = BuildBcDds(4, 4, 1, format, dx10: true, srgb: true);
            if (format != "BC1")
            {
                dds[148] = alpha;
                dds[149] = alpha;
                for (var index = 150; index < 156; index++)
                    dds[index] = 0;
            }

            return dds;
        }

        private static string ClassifyAlpha(byte[] dds)
            => GetStaticMethod("ClassifyTextureAlpha").Invoke(null, [dds])!.ToString()!;

        private static object AlphaState(string name)
            => Enum.Parse(
                GetServiceType().GetNestedType("TextureAlphaState", BindingFlags.NonPublic)!,
                name);

        private static string? AlphaSafety(string alpha, string? shader, string slot)
            => (string?)GetStaticMethod("CheckAtlasAlphaSafety")
                .Invoke(null, [AlphaState(alpha), shader, slot]);

        [Test]
        public void AtlasAlpha_Classify_Bc1AndOpaqueBc3AreNotChanged_Bc3WithAlphaIsFlagged()
        {
            Assert.That(ClassifyAlpha(BuildAlphaDds("BC1", 255)), Is.EqualTo("Opaque"));
            Assert.That(ClassifyAlpha(BuildAlphaDds("BC3", 255)), Is.EqualTo("Opaque"));
            Assert.That(ClassifyAlpha(BuildAlphaDds("BC3", 0)), Is.EqualTo("HasAlpha"));
            Assert.That(ClassifyAlpha(BuildAlphaDds("BC3", 200)), Is.EqualTo("HasAlpha"));
            Assert.That(ClassifyAlpha([1, 2, 3]), Is.EqualTo("Unknown"));
        }

        [Test]
        public void AtlasAlpha_NonOpaqueSourceNoShaderReadsIt_IsAtlasedWithAlphaForcedOpaque()
        {
            // Case b: weighted4_character samples base colour as rgb and the material map as rg.
            Assert.That(
                AlphaSafety("HasAlpha", @"shaders\weighted4_character.xml.shader", "t_xml_base_colour"),
                Is.Null);
            Assert.That(
                AlphaSafety("HasAlpha", "weighted4_character", "t_xml_material_map"),
                Is.Null);

            var state = CreateBareBatchState();
            var sourcePaths = new Dictionary<int, string> { [0] = "a.dds", [1] = "b.dds", [2] = "c.dds" };
            var bytes = new Dictionary<int, byte[]>
            {
                [0] = BuildAlphaDds("BC3", 0),
                [1] = BuildAlphaDds("BC3", 255),
                [2] = BuildAlphaDds("BC1", 255),
            };
            var constants = new Dictionary<int, Editors.ImportExport.TextureAtlas.TextureAtlasConstantColor>
            {
                [3] = new(1, 2, 3, 0),
                [4] = new(1, 2, 3, 255),
            };

            var forced = (HashSet<int>?)GetStaticMethod("GetForceOpaqueAlphaSourceIds").Invoke(
                null,
                [state, Editors.ImportExport.Dds.Native.DXGI_FORMAT.BC1_UNORM_SRGB, sourcePaths, bytes, constants]);

            Assert.That(forced, Is.EquivalentTo(new[] { 0, 3 }));

            // A BC3 batch keeps every source's alpha as is: nothing is forced opaque.
            Assert.That(
                GetStaticMethod("GetForceOpaqueAlphaSourceIds").Invoke(
                    null,
                    [state, Editors.ImportExport.Dds.Native.DXGI_FORMAT.BC3_UNORM_SRGB, sourcePaths, bytes, constants]),
                Is.Null);
        }

        [Test]
        public void AtlasAlpha_OpaqueSources_ForceNothing()
        {
            var state = CreateBareBatchState();
            var forced = GetStaticMethod("GetForceOpaqueAlphaSourceIds").Invoke(
                null,
                [
                    state,
                    Editors.ImportExport.Dds.Native.DXGI_FORMAT.BC1_UNORM_SRGB,
                    new Dictionary<int, string> { [0] = "a.dds", [1] = "b.dds" },
                    new Dictionary<int, byte[]>
                    {
                        [0] = BuildAlphaDds("BC3", 255),
                        [1] = BuildAlphaDds("BC1", 255),
                    },
                    new Dictionary<int, Editors.ImportExport.TextureAtlas.TextureAtlasConstantColor>(),
                ]);

            Assert.That(forced, Is.Null);
        }

        [Test]
        public void AtlasAlpha_AlphaIsDecodedOncePerTexturePath()
        {
            var state = CreateBareBatchState();
            var bytes = new Dictionary<int, byte[]>
            {
                [0] = BuildAlphaDds("BC3", 0),
                [1] = BuildAlphaDds("BC3", 0),
            };
            var paths = new Dictionary<int, string> { [0] = "Shared.dds", [1] = "shared.DDS" };

            GetStaticMethod("GetForceOpaqueAlphaSourceIds").Invoke(
                null,
                [
                    state,
                    Editors.ImportExport.Dds.Native.DXGI_FORMAT.BC1_UNORM_SRGB,
                    paths,
                    bytes,
                    new Dictionary<int, Editors.ImportExport.TextureAtlas.TextureAtlasConstantColor>(),
                ]);

            Assert.That(Get<System.Collections.IDictionary>(state, "TextureAlphaStates").Count, Is.EqualTo(1));
        }

        [TestCase("weighted4_character_alpha", "t_xml_base_colour")]
        [TestCase(@"shaders\weighted4_character_alpha.xml.shader", "t_xml_base_colour")]
        [TestCase("no_such_shader", "t_xml_base_colour")]
        [TestCase(null, "t_xml_base_colour")]
        [TestCase("weighted4_character", "t_xml_not_bound_slot")]
        public void AtlasAlpha_NonOpaqueSourceThatMayBeRead_IsRefusedWithAReason(string? shader, string slot)
        {
            var reason = AlphaSafety("HasAlpha", shader, slot);

            Assert.That(reason, Is.Not.Null.And.Contains(slot));
        }

        [Test]
        public void AtlasAlpha_OpaqueOrUnverifiableSource_ShaderIsIrrelevantOrRefused()
        {
            Assert.That(AlphaSafety("Opaque", "weighted4_character_alpha", "t_xml_base_colour"), Is.Null);
            Assert.That(AlphaSafety("Unknown", "weighted4_character", "t_xml_base_colour"), Is.Not.Null);
        }

        // A 4x4 DDS made by DirectXTex: BGRA texels of the given alpha, optionally BC7 encoded.
        private static byte[] BuildDirectXTexDds(
            Editors.ImportExport.Dds.Native.DXGI_FORMAT format,
            byte alpha,
            bool compressToBc7 = false)
        {
            var texels = new byte[4 * 4 * 4];
            for (var index = 0; index < 16; index++)
            {
                texels[index * 4] = (byte)(index * 9);
                texels[index * 4 + 1] = (byte)(255 - index * 7);
                texels[index * 4 + 2] = 90;
                texels[index * 4 + 3] = alpha;
            }

            using var source = Editors.ImportExport.Dds.Native.TexHelper.Instance.Initialize2D(
                format, 4, 4, 1, 1, Editors.ImportExport.Dds.Native.CP_FLAGS.NONE);
            System.Runtime.InteropServices.Marshal.Copy(texels, 0, source.GetImage(0).Pixels, texels.Length);
            var image = source;
            Editors.ImportExport.Dds.Native.ScratchImage? compressed = null;
            try
            {
                if (compressToBc7)
                {
                    compressed = source.Compress(
                        Editors.ImportExport.Dds.Native.DXGI_FORMAT.BC7_UNORM,
                        Editors.ImportExport.Dds.Native.TEX_COMPRESS_FLAGS.DEFAULT,
                        0.5f);
                    image = compressed;
                }

                using var stream = image.SaveToDDSMemory(Editors.ImportExport.Dds.Native.DDS_FLAGS.NONE);
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                return bytes;
            }
            finally
            {
                compressed?.Dispose();
            }
        }

        [Test]
        public void AtlasAlpha_Classify_UncompressedBgra_ScansTheAlpha()
        {
            Assert.That(
                ClassifyAlpha(BuildDirectXTexDds(Editors.ImportExport.Dds.Native.DXGI_FORMAT.B8G8R8A8_UNORM, 255)),
                Is.EqualTo("Opaque"));
            Assert.That(
                ClassifyAlpha(BuildDirectXTexDds(Editors.ImportExport.Dds.Native.DXGI_FORMAT.B8G8R8A8_UNORM, 10)),
                Is.EqualTo("HasAlpha"));
            Assert.That(
                ClassifyAlpha(BuildDirectXTexDds(Editors.ImportExport.Dds.Native.DXGI_FORMAT.R8G8B8A8_UNORM, 10)),
                Is.EqualTo("HasAlpha"));
        }

        [Test]
        public void AtlasAlpha_Classify_Bc7_ScansTheAlpha()
        {
            Assert.That(
                ClassifyAlpha(BuildDirectXTexDds(Editors.ImportExport.Dds.Native.DXGI_FORMAT.B8G8R8A8_UNORM, 255, compressToBc7: true)),
                Is.EqualTo("Opaque"));
            Assert.That(
                ClassifyAlpha(BuildDirectXTexDds(Editors.ImportExport.Dds.Native.DXGI_FORMAT.B8G8R8A8_UNORM, 10, compressToBc7: true)),
                Is.EqualTo("HasAlpha"));
        }

        [Test]
        public void AtlasAlpha_Classify_FormatsWithoutAlphaAreOpaque()
        {
            // BC5 (dxgi 83) and BC4 (dxgi 80) have no alpha channel.
            Assert.That(
                ClassifyAlpha(BuildBcDds(4, 4, 1, "BC5", dx10: true, srgb: false, dxgiFormat: 83)),
                Is.EqualTo("Opaque"));
            Assert.That(
                ClassifyAlpha(BuildBcDds(4, 4, 1, "BC4", dx10: true, srgb: false, dxgiFormat: 80)),
                Is.EqualTo("Opaque"));
            // B8G8R8X8: the fourth byte is padding, here 0.
            Assert.That(
                ClassifyAlpha(BuildDirectXTexDds(Editors.ImportExport.Dds.Native.DXGI_FORMAT.B8G8R8X8_UNORM, 0)),
                Is.EqualTo("Opaque"));
        }

        private static HashSet<string> DiscoveryKeepAlpha(
            string shader,
            byte[] baseColour,
            byte[]? materialMap = null)
        {
            var files = new Dictionary<string, byte[]> { ["textures\\base.dds"] = baseColour };
            var textures = "<texture><slot>t_xml_base_colour</slot><source>textures\\base.dds</source></texture>";
            var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "t_xml_base_colour" };
            if (materialMap != null)
            {
                files["textures\\map.dds"] = materialMap;
                textures += "<texture><slot>t_xml_material_map</slot><source>textures\\map.dds</source></texture>";
                resolved.Add("t_xml_material_map");
            }

            var document = new XmlDocument();
            document.LoadXml(
                $"<material><shader>shaders\\{shader}.xml.shader</shader><textures>{textures}</textures></material>");
            var state = CreateIdealState(files);
            return (HashSet<string>)GetStaticMethod("GetKeepAlphaChannels").Invoke(
                null,
                [
                    state,
                    document,
                    resolved,
                    new Dictionary<string, Editors.ImportExport.TextureAtlas.TextureAtlasConstantColor>(
                        StringComparer.OrdinalIgnoreCase),
                ]);
        }

        [Test]
        public void AtlasAlpha_Discovery_OnlyMaterialsThatNeedTheirAlphaGetKeepAlpha()
        {
            // Case a: nothing to lose, whatever the shader.
            Assert.That(DiscoveryKeepAlpha("weighted4_character_alpha", BuildAlphaDds("BC3", 255)), Is.Empty);
            Assert.That(DiscoveryKeepAlpha("weighted4_character_alpha", BuildAlphaDds("BC1", 255)), Is.Empty);

            // Case b: alpha present, the shader never reads it, so the BC1 atlas flattens it.
            Assert.That(DiscoveryKeepAlpha("weighted4_character", BuildAlphaDds("BC3", 0)), Is.Empty);

            // Case c: the shader reads alpha, so the slot is kept as BC3 instead of skipping.
            Assert.That(
                DiscoveryKeepAlpha("weighted4_character_alpha", BuildAlphaDds("BC3", 0)),
                Is.EquivalentTo(new[] { "t_xml_base_colour" }));

            // Alpha in a secondary BC1 channel is checked as well, against that slot.
            Assert.That(
                DiscoveryKeepAlpha("weighted4_character", BuildAlphaDds("BC3", 255), BuildAlphaDds("BC3", 0)),
                Is.Empty);
            Assert.That(
                DiscoveryKeepAlpha("weighted4_character_ethereal", BuildAlphaDds("BC3", 255), BuildAlphaDds("BC3", 0)),
                Is.EquivalentTo(new[] { "t_xml_material_map" }));
        }

        private static object CreateKeepAlphaCandidate(params string[] slots)
        {
            var constructor = GetNestedType("AtlasCandidate").GetConstructors(AnyInstance)
                .OrderByDescending(item => item.GetParameters().Length)
                .First();
            var values = constructor.GetParameters()
                .Select(parameter => parameter.ParameterType.IsValueType
                    ? Activator.CreateInstance(parameter.ParameterType)
                    : null)
                .ToArray();
            var candidate = constructor.Invoke(values);
            var keepAlpha = Get<HashSet<string>>(candidate, "KeepAlphaChannels");
            foreach (var slot in slots)
                keepAlpha.Add(slot);
            return candidate;
        }

        private static System.Collections.IList ToCandidateList(params object[] candidates)
        {
            var list = (System.Collections.IList)Activator.CreateInstance(
                typeof(List<>).MakeGenericType(GetNestedType("AtlasCandidate")))!;
            foreach (var candidate in candidates)
                list.Add(candidate);
            return list;
        }

        [Test]
        public void AtlasAlpha_NeedsAlphaAndOpaqueCandidatesAreNeverBatchedTogether()
        {
            // The batch planner closes batches by atlas capacity only and no shader identity is
            // part of its key, so the split by keep-alpha slots has to happen before planning.
            var opaqueA = CreateKeepAlphaCandidate();
            var opaqueB = CreateKeepAlphaCandidate();
            var alphaBase = CreateKeepAlphaCandidate("t_xml_base_colour");
            var alphaBoth = CreateKeepAlphaCandidate("t_xml_base_colour", "t_xml_material_map");

            var groups = ((System.Collections.IEnumerable)GetStaticMethod("SplitCandidatesByKeepAlpha")
                .Invoke(null, [ToCandidateList(alphaBase, opaqueA, alphaBoth, opaqueB)])!)
                .Cast<System.Collections.IList>()
                .ToList();

            Assert.That(groups, Has.Count.EqualTo(3));
            Assert.That(groups.Select(group => group.Count), Is.EquivalentTo(new[] { 2, 1, 1 }));
            foreach (var group in groups)
            {
                var signatures = group.Cast<object>()
                    .Select(candidate => string.Join(",", Get<HashSet<string>>(candidate, "KeepAlphaChannels").Order()))
                    .Distinct();
                Assert.That(signatures.Count(), Is.EqualTo(1));
            }

            // All-opaque input stays a single group, untouched.
            var only = ToCandidateList(opaqueA, opaqueB);
            var single = (System.Collections.IList)GetStaticMethod("SplitCandidatesByKeepAlpha").Invoke(null, [only])!;
            Assert.That(single, Has.Count.EqualTo(1));
        }

        [Test]
        public void AtlasAlpha_AnyKeepAlphaMemberMakesTheChannelBc3_OthersStayBc1()
        {
            object Format(IList list, string slot, Shared.GameFormats.RigidModel.Types.TextureType type)
                => GetStaticMethod("GetAtlasChannelFormat").Invoke(null, [list, slot, type])!;

            var mixed = ToCandidateList(CreateKeepAlphaCandidate(), CreateKeepAlphaCandidate("t_xml_base_colour"));
            var opaque = ToCandidateList(CreateKeepAlphaCandidate(), CreateKeepAlphaCandidate());

            Assert.That(Format(mixed, "t_xml_base_colour", Shared.GameFormats.RigidModel.Types.TextureType.BaseColour),
                Is.EqualTo(Editors.ImportExport.Dds.Native.DXGI_FORMAT.BC3_UNORM_SRGB));
            Assert.That(Format(mixed, "t_xml_material_map", Shared.GameFormats.RigidModel.Types.TextureType.MaterialMap),
                Is.EqualTo(Editors.ImportExport.Dds.Native.DXGI_FORMAT.BC1_UNORM_SRGB));
            Assert.That(Format(opaque, "t_xml_base_colour", Shared.GameFormats.RigidModel.Types.TextureType.BaseColour),
                Is.EqualTo(Editors.ImportExport.Dds.Native.DXGI_FORMAT.BC1_UNORM_SRGB));
            Assert.That(Format(mixed, "t_xml_normal", Shared.GameFormats.RigidModel.Types.TextureType.Normal),
                Is.EqualTo(Editors.ImportExport.Dds.Native.DXGI_FORMAT.BC3_UNORM));
            Assert.That(GetStaticMethod("GetAtlasBytesPerBlock").Invoke(null, [Editors.ImportExport.Dds.Native.DXGI_FORMAT.BC1_UNORM_SRGB]), Is.EqualTo(8));
            Assert.That(GetStaticMethod("GetAtlasBytesPerBlock").Invoke(null, [Editors.ImportExport.Dds.Native.DXGI_FORMAT.BC3_UNORM_SRGB]), Is.EqualTo(16));
        }

        private static void RunGateTotals(
            object state,
            int groupCount,
            params (int OrderedBatches, int AffectedUnits)[] gateResultsPerGroup)
        {
            var totals = Activator.CreateInstance(
                GetNestedType("FormatGroupGateTotals"),
                AnyInstance,
                null,
                [state, groupCount],
                null)!;
            void Set(string name, int value)
                => state.GetType().GetProperty(name, AnyInstance)!.SetValue(state, value);
            foreach (var (orderedBatches, affectedUnits) in gateResultsPerGroup)
            {
                totals.GetType().GetMethod("BeforeGroup")!.Invoke(totals, null);
                // What the value gate writes during the group.
                Set("AtlasValueGateCoverageOrderedBatchCount", orderedBatches);
                Set("AtlasValueGateCandidateAffectedUnitCount", affectedUnits);
                totals.GetType().GetMethod("AfterGroup")!.Invoke(totals, null);
            }
        }

        [Test]
        public void AtlasAlpha_FormatGroups_ValueGateCountersAreTotalledAcrossGroups()
        {
            var state = CreateBareBatchState();

            RunGateTotals(state, 2, (3, 10), (2, 14));

            Assert.That(Get<int>(state, "AtlasValueGateCoverageOrderedBatchCount"), Is.EqualTo(5));
            Assert.That(Get<int>(state, "AtlasValueGateCandidateAffectedUnitCount"), Is.EqualTo(14));
        }

        [Test]
        public void AtlasAlpha_SingleFormatGroup_LeavesValueGateCountersAsTheGateWroteThem()
        {
            var state = CreateBareBatchState();

            RunGateTotals(state, 1, (7, 9));

            Assert.That(Get<int>(state, "AtlasValueGateCoverageOrderedBatchCount"), Is.EqualTo(7));
            Assert.That(Get<int>(state, "AtlasValueGateCandidateAffectedUnitCount"), Is.EqualTo(9));
        }

        // --- Pruning and vanilla overrides ------------------------------------------------

        private static (object State, HashSet<string> ToRemove) CreatePruneState(
            bool vanillaLoaded,
            string[] outputPaths,
            string[] vanillaPaths,
            params string[] toRemove)
        {
            var source = new Moq.Mock<Shared.Core.PackFiles.Models.IPackFileContainer>();
            source.Setup(item => item.IsCaPackFile).Returns(false);
            source.Setup(item => item.ContainsFile(Moq.It.IsAny<string>()))
                .Returns<string>(path => outputPaths.Contains(path, StringComparer.OrdinalIgnoreCase));
            var containers = new List<Shared.Core.PackFiles.Models.IPackFileContainer> { source.Object };
            if (vanillaLoaded)
            {
                var vanilla = new Moq.Mock<Shared.Core.PackFiles.Models.IPackFileContainer>();
                vanilla.Setup(item => item.IsCaPackFile).Returns(true);
                vanilla.Setup(item => item.ContainsFile(Moq.It.IsAny<string>()))
                    .Returns<string>(path => vanillaPaths.Contains(path, StringComparer.OrdinalIgnoreCase));
                containers.Add(vanilla.Object);
            }

            var state = GetNestedType("BatchState")
                .GetConstructors(AnyInstance)
                .Single()
                .Invoke(
                [
                    source.Object,
                    source.Object,
                    new Moq.Mock<Editors.PackOptimizer.Core.IPackOptimizerPackFiles>().Object,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    false,
                    false,
                    false,
                ]);
            state.GetType().GetProperty("GameplayTraversalContainers")!.SetValue(state, containers);
            return (state, new HashSet<string>(toRemove, StringComparer.OrdinalIgnoreCase));
        }

        private static List<(string Path, string Reason)> KeptByPruning(object state)
            => (List<(string Path, string Reason)>)state.GetType().GetProperty("PruneKeptFiles")!.GetValue(state)!;

        [Test]
        public void Prune_UnreachableVanillaOverrideIsKept_ModOnlyPathIsStillPruned()
        {
            var (state, toRemove) = CreatePruneState(
                vanillaLoaded: true,
                outputPaths: ["variantmeshes\\wsmodels\\override.wsmodel", "variantmeshes\\wsmodels\\modonly.wsmodel"],
                vanillaPaths: ["variantmeshes\\wsmodels\\override.wsmodel"],
                "variantmeshes\\wsmodels\\override.wsmodel",
                "variantmeshes\\wsmodels\\modonly.wsmodel");

            GetStaticMethod("KeepVanillaOverrides").Invoke(null, [state, toRemove]);

            Assert.That(toRemove, Is.EquivalentTo(new[] { "variantmeshes\\wsmodels\\modonly.wsmodel" }));
            Assert.That(KeptByPruning(state), Has.Count.EqualTo(1));
            Assert.That(KeptByPruning(state)[0].Path, Is.EqualTo("variantmeshes\\wsmodels\\override.wsmodel"));
            Assert.That(KeptByPruning(state)[0].Reason, Is.EqualTo("overrides a vanilla file"));
        }

        [Test]
        public void Prune_WithoutVanillaData_KeepsEverythingThatCouldBeAnOverride()
        {
            var atlas = @"textures\asset_editor\atlases\old_base_colour.dds";
            var (state, toRemove) = CreatePruneState(
                vanillaLoaded: false,
                outputPaths: ["variantmeshes\\a.wsmodel", "textures\\b.dds", atlas],
                vanillaPaths: [],
                "variantmeshes\\a.wsmodel",
                "textures\\b.dds",
                atlas);

            GetStaticMethod("KeepVanillaOverrides").Invoke(null, [state, toRemove]);

            // Only the tool's own generated atlas can be pruned without knowing the vanilla data.
            Assert.That(toRemove, Is.EquivalentTo(new[] { atlas }));
            Assert.That(KeptByPruning(state).Select(item => item.Path), Is.EquivalentTo(
                new[] { "variantmeshes\\a.wsmodel", "textures\\b.dds" }));
            Assert.That(KeptByPruning(state).All(item => item.Reason.Contains("not loaded")), Is.True);
        }

        [Test]
        public void AtlasAlpha_Bc3AtlasWriterKeepsAlphaWithoutQuantising()
        {
            using var writer = Editors.ImportExport.Importing.Importers.PngToDds.PngToDdsImporter
                .CreateRawBgraMipChainWriter(
                    8,
                    8,
                    1,
                    Shared.GameFormats.RigidModel.Types.TextureType.BaseColour,
                    Shared.Core.Settings.GameTypeEnum.Warhammer3,
                    ddsFormatOverride: Editors.ImportExport.Dds.Native.DXGI_FORMAT.BC3_UNORM_SRGB);
            var pixels = new byte[8 * 8 * 4];
            for (var texel = 0; texel < 64; texel++)
            {
                pixels[texel * 4] = 40;
                pixels[texel * 4 + 1] = 80;
                pixels[texel * 4 + 2] = 120;
                pixels[texel * 4 + 3] = (byte)(texel < 32 ? 100 : 200);
            }

            writer.WriteMip(0, pixels);
            var dds = writer.Complete("atlas.dds").DataSource.ReadData();

            Assert.That(ClassifyAlpha(dds), Is.EqualTo("HasAlpha"));
            var layout = ReadDds(dds);
            Assert.That(layout.Format, Is.EqualTo("BC3"));
            Assert.That(layout.Srgb, Is.True);

            // 100 and 200 are both far from a 0.5 cut: neither collapsed to 0 or 255.
            using var stream = new System.IO.MemoryStream(dds);
            using var image = Pfim.Pfimage.FromStream(stream);
            var alphas = Enumerable.Range(0, 64).Select(texel => image.Data[texel * 4 + 3]).Distinct().ToList();
            Assert.That(alphas, Has.All.InRange((byte)90, (byte)210));
            Assert.That(alphas.Count, Is.GreaterThan(1));
        }

        // --- Other enabled mods (consumers) -----------------------------------------------

        private static Dictionary<string, byte[]> ConsumerFiles(params (string Path, string Text)[] files)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, text) in files)
                result[path] = System.Text.Encoding.UTF8.GetBytes(text);
            return result;
        }

        // Builds the consumer index the way the run does (before DB rows), closes it inside the
        // source and installs it on the state.
        private static object InstallConsumers(
            object state,
            int sourceIndex,
            params (string Name, Dictionary<string, byte[]> Files)[] packs)
        {
            var containers = new List<Shared.Core.PackFiles.Models.IPackFileContainer>();
            foreach (var (name, files) in packs)
            {
                var container = CreateDownscaleContainer(files);
                container.Setup(item => item.Name).Returns(name);
                containers.Add(container.Object);
            }

            var index = GetStaticMethod("BuildConsumerReferenceIndex")
                .Invoke(null, [state, containers, sourceIndex, CancellationToken.None, null])!;
            GetStaticMethod("CloseConsumerUse").Invoke(null, [state, index, CancellationToken.None]);
            SetStateProperty(state, "Consumers", index);
            return index;
        }

        private static bool ConsumerUses(object index, string path)
            => ((IDictionary)Get<object>(index, "Used")).Contains(path);

        private static bool ConsumerFreezes(object index, string path)
            => ((IDictionary)Get<object>(index, "Frozen")).Contains(path);

        private const string ConsumerMaterial = "variantmeshes\\wh_main\\skin.xml.material";
        private const string ConsumerMaterialTexture = "variantmeshes\\wh_main\\tex\\skin_base.dds";
        private const string ConsumerOtherTexture = "variantmeshes\\wh_main\\tex\\other_base.dds";
        private const string ConsumerRigid = "variantmeshes\\wh_main\\shared.rigid_model_v2";

        private static DownscaleFixture BuildConsumerSourceFixture()
        {
            var fixture = new DownscaleFixture();
            fixture.AddTexture(ConsumerMaterialTexture, 64, 7);
            fixture.AddTexture(ConsumerOtherTexture, 64, 7);
            fixture.AddMaterial(ConsumerMaterial, ("t_xml_base_colour", ConsumerMaterialTexture));
            fixture.AddText(ConsumerRigid, "rigid");
            return fixture;
        }

        private static Dictionary<string, byte[]> SubmodFiles()
            => ConsumerFiles(
                ("variantmeshes\\submod\\sub.wsmodel",
                 $"<model><geometry>{ConsumerRigid}</geometry><materials><material lod_index=\"0\" part_index=\"0\">{ConsumerMaterial}</material></materials></model>"));

        [Test]
        public void Consumers_ReferencedFilesAreKeptByPruning_WithPackAndReferrer()
        {
            var fixture = BuildConsumerSourceFixture();
            var (_, state) = CreateStandardDownscaleState(fixture, downscale: false);
            InstallConsumers(state, 1, ("SubMod", SubmodFiles()));
            var toRemove = new HashSet<string>(
                [ConsumerMaterial, ConsumerMaterialTexture, ConsumerOtherTexture, ConsumerRigid],
                StringComparer.OrdinalIgnoreCase);

            GetStaticMethod("KeepVanillaOverrides").Invoke(null, [state, toRemove]);

            // The material, its texture (closure) and the rigid are used by the consumer's WSModel.
            Assert.That(toRemove, Is.EquivalentTo(new[] { ConsumerOtherTexture }));
            var kept = KeptByPruning(state);
            Assert.That(kept.Select(item => item.Path), Is.EquivalentTo(
                new[] { ConsumerMaterial, ConsumerMaterialTexture, ConsumerRigid }));
            Assert.That(kept.All(item => item.Reason.Contains("SubMod") && item.Reason.Contains("sub.wsmodel")), Is.True);
        }

        [Test]
        public void Consumers_OverriddenSourcePathIsFrozenAndKept()
        {
            var fixture = BuildConsumerSourceFixture();
            var (_, state) = CreateStandardDownscaleState(fixture, downscale: false);
            var reskin = ConsumerFiles();
            reskin[ConsumerMaterialTexture] = [1, 2, 3];
            var index = InstallConsumers(state, 0, ("Reskin", reskin));
            var toRemove = new HashSet<string>(
                [ConsumerMaterialTexture, ConsumerOtherTexture],
                StringComparer.OrdinalIgnoreCase);

            Assert.That(ConsumerFreezes(index, ConsumerMaterialTexture), Is.True);
            Assert.That(ConsumerFreezes(index, ConsumerOtherTexture), Is.False);
            Assert.That(
                (bool)GetStaticMethod("OverridesVanillaPath").Invoke(null, [state, ConsumerMaterialTexture, "test"])!,
                Is.True);

            GetStaticMethod("KeepVanillaOverrides").Invoke(null, [state, toRemove]);

            Assert.That(toRemove, Is.EquivalentTo(new[] { ConsumerOtherTexture }));
            Assert.That(KeptByPruning(state).Single().Reason, Does.Contain("overridden by").And.Contain("Reskin"));
            // The consumer loads after the source (source index 0): the report says so.
            Assert.That(KeptByPruning(state).Single().Reason, Does.Contain("loads after the source"));
        }

        [Test]
        public void Consumers_RigidUsedByAConsumerWsModelBlocksLayoutChanges()
        {
            var fixture = BuildConsumerSourceFixture();
            var (_, state) = CreateStandardDownscaleState(fixture, downscale: false);
            var arguments = new object?[] { state, ConsumerRigid, null };

            Assert.That((bool)GetStaticMethod("TryGetStructuralMergeBlockReason").Invoke(null, arguments)!, Is.False);

            InstallConsumers(state, 1, ("SubMod", SubmodFiles()));
            arguments = [state, ConsumerRigid, null];
            Assert.That((bool)GetStaticMethod("TryGetStructuralMergeBlockReason").Invoke(null, arguments)!, Is.True);
            Assert.That((string)arguments[2]!, Does.Contain("[mod]").And.Contain("SubMod"));

            // The shared repack and the compaction's alpha check use the same rigid test.
            var repackReason = (string?)GetStaticMethod("GetRigidNotOnlyThroughOwnedWsModelsReason").Invoke(
                null,
                [
                    state,
                    ConsumerRigid,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase),
                    null,
                ]);
            Assert.That(repackReason, Does.Contain("outside the source pack").And.Contain("SubMod"));

            var blocks = (IEnumerable)Get<object>(Get<object>(state, "Consumers"), "Blocks");
            var stages = blocks.Cast<object>().Select(block => Get<string>(block, "Stage")).Distinct().ToList();
            Assert.That(stages, Does.Contain("same-rigid merge").And.Contain("repack / compaction"));
        }

        [Test]
        public void Consumers_TextureUsedByAConsumerIsNotDownscaled()
        {
            var controlFixture = BuildStandardDownscaleFixture();
            var (controlService, controlState) = CreateStandardDownscaleState(controlFixture, downscale: false);
            var control = RunDownscale(controlService, controlState, controlFixture);
            Assert.That(
                Candidates(control).Select(candidate => Get<string>(candidate, "Path")),
                Does.Contain("textures\\t_half.dds"));

            var fixture = BuildStandardDownscaleFixture();
            var (service, state) = CreateStandardDownscaleState(fixture, downscale: false);
            InstallConsumers(
                state,
                0,
                ("Retexture", ConsumerFiles(
                    ("variantmeshes\\retex\\retex.wsmodel",
                     "<model><geometry>other.rigid_model_v2</geometry><materials><material lod_index=\"0\" part_index=\"0\">materials\\m_half.xml.material</material></materials></model>"))));

            var result = RunDownscale(service, state, fixture);

            Assert.That(
                Candidates(result).Select(candidate => Get<string>(candidate, "Path")),
                Does.Not.Contain("textures\\t_half.dds"));
            Assert.That(Reasons(result).GetValueOrDefault(DownscaleReason("TextureDownscaleReasonOtherMod")), Is.EqualTo(1));
        }

        private static byte[] UniformDds(int size, int mips)
        {
            var dds = BuildBcDds(size, size, mips);
            Array.Clear(dds, 148, dds.Length - 148);
            return dds;
        }

        [Test]
        public void Consumers_TextureUsedByAConsumerIsNotCompacted()
        {
            const string texture = "textures\\uniform.dds";
            const string material = "materials\\uniform.xml.material";
            DownscaleFixture Build()
            {
                var built = new DownscaleFixture();
                built.Add(texture, UniformDds(64, 7));
                built.AddMaterial(material, ("t_xml_base_colour", texture));
                return built;
            }

            int Compacted(object state)
                => ((IEnumerable)Get<object>(Get<object>(state, "TextureCompaction"), "Compacted")).Cast<object>().Count();

            var control = Build();
            var (controlService, controlState) = CreateDownscaleState(control, downscale: false);
            SetStateProperty(controlState, "CompactTextures", true);
            GetServiceType().GetMethod("RunTextureCompaction", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(controlService, [controlState, CancellationToken.None, null]);
            Assert.That(Compacted(controlState), Is.EqualTo(1), "the uniform texture is compacted without consumers");

            var fixture = Build();
            var (service, state) = CreateDownscaleState(fixture, downscale: false);
            SetStateProperty(state, "CompactTextures", true);
            SetStateProperty(state, "DropUnreadAlpha", true);
            InstallConsumers(
                state,
                0,
                ("Mat", ConsumerFiles(("materials\\own.xml.material", $"<material><textures><texture><source>{texture}</source></texture></textures></material>"))));
            GetServiceType().GetMethod("RunTextureCompaction", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [state, CancellationToken.None, null]);

            Assert.That(Compacted(state), Is.EqualTo(0));
            var skipped = Get<Dictionary<string, int>>(Get<object>(state, "TextureCompaction"), "Skipped");
            Assert.That(skipped.GetValueOrDefault(DownscaleReason("TextureCompactionReasonOtherMod")), Is.EqualTo(1));
            Assert.That(fixture.Output[texture], Is.EqualTo(fixture.Source[texture]));
        }

        [Test]
        public void Consumers_DbRowsThatNameASourceVmdMakeItAUsedRoot_AndFallbackIsCounted()
        {
            const string vmd = "variantmeshes\\wh_main\\unit.variantmeshdefinition";
            var fixture = new DownscaleFixture();
            fixture.AddText(vmd, "<VARIANT_MESH><decal_diffuse>textures\\decal.dds</decal_diffuse></VARIANT_MESH>");
            fixture.AddTexture("textures\\decal.dds", 64, 7);
            fixture.AddText("variantmeshes\\wh_main\\other.variantmeshdefinition", "<VARIANT_MESH/>");
            var (_, state) = CreateDownscaleState(fixture, downscale: false);
            var brokenTable = ConsumerFiles(
                ("db\\main_units_tables\\broken", "junk variantmeshes\\wh_main\\other.variantmeshdefinition junk"));
            var index = InstallConsumers(state, 0, ("DbMod", brokenTable));

            var usage = CreateDownscaleUsage(vmd, "InfantryMissile", 40);
            var with = CreateDownscaleResolution(true, (vmd, usage));
            var without = CreateDownscaleResolution(true);
            var failureType = ServicesType("Wh3ConsumerDbFailure");
            var pack = ((IEnumerable)Get<object>(index, "Packs")).Cast<object>().Single();
            var failures = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(failureType))!;
            failures.Add(Activator.CreateInstance(
                failureType, Get<object>(pack, "Container"), "db\\main_units_tables\\broken", "test: no schema")!);
            with.GetType().GetProperty("ConsumerDbFailures")!.SetValue(with, failures);

            GetStaticMethod("FinalizeConsumerReferenceIndex")
                .Invoke(null, [state, index, with, without, CancellationToken.None, null]);

            Assert.That(ConsumerUses(index, vmd), Is.True, "root created by the consumer's rows");
            Assert.That(ConsumerUses(index, "textures\\decal.dds"), Is.True, "and what it names");
            Assert.That(
                Get<IEnumerable<string>>(index, "VmdRootsAddedByDb"),
                Is.EquivalentTo(new[] { vmd }));
            // The undecodable table's text names another VMD: only the fallback added it.
            Assert.That(ConsumerUses(index, "variantmeshes\\wh_main\\other.variantmeshdefinition"), Is.True);
            Assert.That(Get<int>(pack, "DbFallbackAdded"), Is.EqualTo(1));
            Assert.That(Get<IList>(pack, "DbFallbackTables").Count, Is.EqualTo(1));
        }

        [Test]
        public void Consumers_IntegrityCheckFailsWhenAnExternalFileChanges_AndHonoursNamedExceptions()
        {
            var fixture = BuildConsumerSourceFixture();
            var (_, state) = CreateStandardDownscaleState(fixture, downscale: false);
            var index = InstallConsumers(state, 1, ("SubMod", SubmodFiles()));
            var errors = new List<string>();
            GetStaticMethod("ValidateConsumerIntegrity").Invoke(null, [state, errors, CancellationToken.None]);
            Assert.That(errors, Is.Empty);

            fixture.Output[ConsumerMaterialTexture] = [9, 9, 9];
            GetStaticMethod("ValidateConsumerIntegrity").Invoke(null, [state, errors, CancellationToken.None]);
            Assert.That(errors.Single(), Does.Contain(ConsumerMaterialTexture).And.Contain("SubMod"));

            errors.Clear();
            ((HashSet<string>)Get<object>(index, "IntegrityExceptions")).Add(ConsumerMaterialTexture);
            GetStaticMethod("ValidateConsumerIntegrity").Invoke(null, [state, errors, CancellationToken.None]);
            Assert.That(errors, Is.Empty);

            fixture.Output.Remove(ConsumerMaterial);
            GetStaticMethod("ValidateConsumerIntegrity").Invoke(null, [state, errors, CancellationToken.None]);
            Assert.That(errors.Single(), Does.Contain("removed"));
        }

        [Test]
        public void Consumers_NoConsumersLeavesNoTraceInTheReport()
        {
            var fixture = BuildConsumerSourceFixture();
            var (_, state) = CreateStandardDownscaleState(fixture, downscale: false);
            var builder = new StringBuilder();

            GetStaticMethod("AppendConsumerSection").Invoke(null, [builder, state]);
            var errors = new List<string>();
            GetStaticMethod("ValidateConsumerIntegrity").Invoke(null, [state, errors, CancellationToken.None]);

            Assert.That(builder.ToString(), Is.Empty);
            Assert.That(errors, Is.Empty);
            Assert.That(Get<object?>(state, "Consumers"), Is.Null);
            var immutable = (IReadOnlyCollection<string>)GetStaticMethod("GetImmutableGameplayWsModelConsumersForRigid")
                .Invoke(null, [state, ConsumerRigid, "test"])!;
            Assert.That(immutable, Is.Empty);
        }

        [Test]
        public void Consumers_LargeOverlapOfAssetPathsWarnsAboutADerivedPack()
        {
            var fixture = new DownscaleFixture();
            var variant = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < 30; i++)
            {
                var path = $"textures\\t{i}.dds";
                fixture.Add(path, [1]);
                variant[path] = [2];
            }

            var (_, state) = CreateDownscaleState(fixture, downscale: false);
            var index = InstallConsumers(state, 0, ("source_atlas", variant));

            var warnings = Get<List<string>>(index, "Warnings");
            Assert.That(warnings.Single(), Does.Contain("source_atlas").And.Contain("30"));
            Assert.That(((IDictionary)Get<object>(index, "Frozen")).Count, Is.EqualTo(30));
        }

        private static object CreateUsageWithLand(string vmd, string unit, string land)
        {
            var counts = Activator.CreateInstance(ServicesType("Wh3UnitVisualCounts"), 1, 0, 0, 0)!;
            return Activator.CreateInstance(
                ServicesType("Wh3UnitCategoryUsage"),
                vmd,
                unit,
                land,
                string.Empty,
                string.Empty,
                string.Empty,
                40,
                Enum.Parse(ServicesType("Wh3ArmyUnitCategory"), "InfantryMissile"),
                Enum.Parse(ServicesType("Wh3UnitVisualRole"), "Men"),
                1,
                counts)!;
        }

        private static object MergeResolutions(object baseline, object withConsumers)
            => ServicesType("Wh3UnitCategoryResolver")
                .GetMethod("MergeAdditive", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [baseline, withConsumers])!;

        private static List<string> ResolutionUsageKeys(object resolution)
            => ((IDictionary)Get<object>(resolution, "UsagesByVmd")).Keys.Cast<string>().Order().ToList();

        private static int ResolutionUsageCount(object resolution, string vmd)
        {
            var dictionary = (IDictionary)Get<object>(resolution, "UsagesByVmd");
            return dictionary.Contains(vmd) ? ((IList)dictionary[vmd]!).Count : 0;
        }

        [Test]
        public void ConsumerRows_NeverRemoveARootOrChangeAUnit_TheyOnlyAdd()
        {
            const string vmdA = "variantmeshes\\a.variantmeshdefinition";
            const string vmdVanilla = "variantmeshes\\vanilla.variantmeshdefinition";
            const string vmdB = "variantmeshes\\b.variantmeshdefinition";
            const string vmdNew = "variantmeshes\\new.variantmeshdefinition";
            var baseline = CreateDownscaleResolution(
                true,
                (vmdA, CreateUsageWithLand(vmdA, "teb_unit", "land_teb")),
                (vmdB, CreateUsageWithLand(vmdB, "teb_other", "land_other")));
            // The consumer repoints teb_unit at a vanilla land unit (the oldworld case) and
            // adds a unit of its own on a source VMD, plus a second variant for teb_other.
            var withConsumers = CreateDownscaleResolution(
                true,
                (vmdVanilla, CreateUsageWithLand(vmdVanilla, "teb_unit", "land_vanilla")),
                (vmdB, CreateUsageWithLand(vmdB, "teb_other", "land_other")),
                (vmdA, CreateUsageWithLand(vmdA, "cr_new_unit", "land_cr_new")),
                (vmdNew, CreateUsageWithLand(vmdNew, "teb_other", "land_other")));

            var merged = MergeResolutions(baseline, withConsumers);

            // The remapped unit keeps its baseline root; its consumer-made entry is not taken.
            Assert.That(ResolutionUsageCount(merged, vmdA), Is.EqualTo(2), "teb_unit stays, the consumer's new unit is added");
            Assert.That(ResolutionUsageKeys(merged), Does.Not.Contain(vmdVanilla));
            // A new link for an existing unit and its land unit is an addition.
            Assert.That(ResolutionUsageKeys(merged), Does.Contain(vmdNew));
            Assert.That(ResolutionUsageCount(merged, vmdB), Is.EqualTo(1));
        }

        [Test]
        public void ConsumerRows_NothingToAddGivesTheBaseline()
        {
            const string vmdA = "variantmeshes\\a.variantmeshdefinition";
            var baseline = CreateDownscaleResolution(true, (vmdA, CreateUsageWithLand(vmdA, "u", "land_u")));
            var same = CreateDownscaleResolution(true, (vmdA, CreateUsageWithLand(vmdA, "u", "land_u")));

            var merged = MergeResolutions(baseline, same);

            Assert.That(ResolutionUsageKeys(merged), Is.EqualTo(ResolutionUsageKeys(baseline)));
            Assert.That(ResolutionUsageCount(merged, vmdA), Is.EqualTo(1));
        }

        // --- Vanilla files another mod replaces -------------------------------------------------

        private const string VanillaMaterial = "variantmeshes\\vanilla\\m.xml.material";
        private const string VanillaTexture = "textures\\vanilla\\t_replaced.dds";

        private static (object Service, object State, DownscaleFixture Fixture) BuildVanillaReplacementState()
        {
            var fixture = new DownscaleFixture();
            // A vanilla material that names a vanilla texture; the source WSModel binds the material.
            fixture.Vanilla[VanillaMaterial] = System.Text.Encoding.UTF8.GetBytes(
                DownscaleMaterialXml(("t_xml_base_colour", VanillaTexture)));
            fixture.Vanilla[VanillaTexture] = [1];
            fixture.AddText(
                "variantmeshes\\mod\\w.wsmodel",
                $"<model><geometry>{ConsumerRigid}</geometry><materials><material lod_index=\"0\" part_index=\"0\">{VanillaMaterial}</material></materials></model>");
            fixture.AddText(ConsumerRigid, "rigid");
            fixture.AddText("variantmeshes\\mod\\other.wsmodel", "<model><geometry>other.rigid_model_v2</geometry></model>");
            var (service, state) = CreateDownscaleState(fixture, downscale: false);
            return (service, state, fixture);
        }

        private static void ComputeVanillaReplacementFor(object state, object index)
        {
            var vanilla = Get<IEnumerable<Shared.Core.PackFiles.Models.IPackFileContainer>>(state, "GameplayTraversalContainers")
                .Where(container => container.IsCaPackFile).ToList();
            GetStaticMethod("ComputeVanillaReplacement").Invoke(
                null,
                [Get<Shared.Core.PackFiles.Models.IPackFileContainer>(state, "Source"), vanilla, index, CancellationToken.None, null]);
            GetStaticMethod("CloseConsumerUse").Invoke(null, [state, index, CancellationToken.None]);
        }

        [Test]
        public void VanillaReplacement_SourceFilesThatOnlyReferenceAReplacedVanillaFileByPathStayFree()
        {
            var (_, state, _) = BuildVanillaReplacementState();
            var reskin = ConsumerFiles();
            reskin[VanillaTexture] = [9, 9];
            var index = InstallConsumers(state, 0, ("VanillaReskin", reskin));

            ComputeVanillaReplacementFor(state, index);

            Assert.That(((IDictionary)Get<object>(index, "VanillaReplaced")).Contains(VanillaTexture), Is.True);
            Assert.That(((IDictionary)Get<object>(index, "VanillaReachedBy"))[VanillaTexture], Is.EqualTo("variantmeshes\\mod\\w.wsmodel"));
            // The WSModel that binds the vanilla material only names it by path: it and its rigid
            // are not frozen (a generated file referencing the path picks up the other mod's file).
            Assert.That(ConsumerUses(index, "variantmeshes\\mod\\w.wsmodel"), Is.False);
            Assert.That(ConsumerUses(index, ConsumerRigid), Is.False);
            Assert.That(ConsumerUses(index, "variantmeshes\\mod\\other.wsmodel"), Is.False);
            // Pack-level count (what the probe and the result report).
            var pack = ((IEnumerable)Get<object>(index, "Packs")).Cast<object>().Single();
            Assert.That(((IEnumerable)Get<object>(pack, "VanillaReached")).Cast<object>().Count(), Is.EqualTo(1));
            // The rigid can still be merged and rewritten.
            var arguments = new object?[] { state, ConsumerRigid, null };
            Assert.That((bool)GetStaticMethod("TryGetStructuralMergeBlockReason").Invoke(null, arguments)!, Is.False);
        }

        [Test]
        public void VanillaReplacement_ATextureOverrideDoesNotBlockACrossRigidMergeButARigidOrWsModelOverrideDoes()
        {
            const string vanillaRigid = "variantmeshes\\vanilla\\r.rigid_model_v2";
            const string vanillaWsModel = "variantmeshes\\vanilla\\w.wsmodel";
            var (_, state, fixture) = BuildVanillaReplacementState();
            fixture.Vanilla[vanillaRigid] = [1];
            fixture.Vanilla[vanillaWsModel] = System.Text.Encoding.UTF8.GetBytes("<model/>");
            var reskin = ConsumerFiles();
            reskin[VanillaTexture] = [9];
            reskin[VanillaMaterial] = [9];
            reskin[vanillaRigid] = [9];
            reskin[vanillaWsModel] = [9];
            var index = InstallConsumers(state, 0, ("VanillaReskin", reskin));
            ComputeVanillaReplacementFor(state, index);
            var gate = index.GetType().GetMethod("TryBlockCrossRigidCopy")!;

            // The merge names textures and materials by path only: the other mod's file is loaded.
            Assert.That((bool)gate.Invoke(index, [VanillaTexture])!, Is.False);
            Assert.That((bool)gate.Invoke(index, [VanillaMaterial])!, Is.False);
            // It copies rigid geometry and the WSModel's material table into generated files.
            Assert.That((bool)gate.Invoke(index, [vanillaRigid])!, Is.True);
            Assert.That((bool)gate.Invoke(index, [vanillaWsModel])!, Is.True);
            Assert.That((bool)gate.Invoke(index, ["variantmeshes\\vanilla\\unreplaced.rigid_model_v2"])!, Is.False);
            var stages = ((IEnumerable)Get<object>(index, "Blocks")).Cast<object>()
                .Where(block => Get<string>(block, "Kind") == "vanilla-replaced")
                .Select(block => Get<string>(block, "Path") + "@" + Get<string>(block, "Stage"))
                .ToList();
            Assert.That(stages, Is.EquivalentTo(new[] { vanillaRigid + "@cross-rigid merge", vanillaWsModel + "@cross-rigid merge" }));
        }

        [Test]
        public void VanillaReplacement_AReplacedMaterialOnlyMergesByIdentityWithItself()
        {
            var (_, state, _) = BuildVanillaReplacementState();
            var reskin = ConsumerFiles();
            reskin[VanillaMaterial] = [9];
            var index = InstallConsumers(state, 0, ("VanillaReskin", reskin));
            ComputeVanillaReplacementFor(state, index);
            // Whatever the material's content, it no longer merges with a differently named material.
            var identityAfter = (string)GetStaticMethod("GetMeshMergeMaterialIdentity").Invoke(null, [state, VanillaMaterial])!;

            Assert.That(identityAfter, Is.EqualTo("path:" + VanillaMaterial));
        }

        [Test]
        public void VanillaReplacement_AReplacedTextureIsBlockedForTheRepackAndTheAtlas()
        {
            var (_, state, _) = BuildVanillaReplacementState();
            var reskin = ConsumerFiles();
            reskin[VanillaTexture] = [9, 9];
            var index = InstallConsumers(state, 0, ("VanillaReskin", reskin));
            ComputeVanillaReplacementFor(state, index);

            var blocked = (bool)index.GetType().GetMethod("TryBlockVanillaReplaced")!.Invoke(index, [VanillaTexture, "repack"])!;
            var notBlocked = (bool)index.GetType().GetMethod("TryBlockVanillaReplaced")!.Invoke(index, ["textures\\other.dds", "repack"])!;

            Assert.That(blocked, Is.True);
            Assert.That(notBlocked, Is.False);
            var blocks = ((IEnumerable)Get<object>(index, "Blocks")).Cast<object>().ToList();
            Assert.That(blocks.Any(block => Get<string>(block, "Kind") == "vanilla-replaced" && Get<string>(block, "Stage") == "repack"), Is.True);
        }

        [Test]
        public void Probe_ReportsWhichConsumersCanMatter()
        {
            var (_, _, fixture) = BuildVanillaReplacementState();
            fixture.AddText("variantmeshes\\variantmeshdefinitions\\unit_a.variantmeshdefinition", "<VARIANT_MESH/>");
            fixture.AddTexture("textures\\mod_owned.dds", 64, 7);
            var source = CreateDownscaleContainer(fixture.Source);
            var vanilla = CreateDownscaleContainer(fixture.Vanilla, isCaPackFile: true);
            foreach (var container in new[] { source, vanilla })
            {
                container.Setup(item => item.GetDirectoryContent(Moq.It.IsAny<string>()))
                    .Returns(new List<(string Path, Shared.Core.PackFiles.Models.PackFile File)>());
            }

            Shared.Core.PackFiles.Models.IPackFileContainer Pack(string name, Dictionary<string, byte[]> files)
            {
                var container = CreateDownscaleContainer(files);
                container.Setup(item => item.Name).Returns(name);
                container.Setup(item => item.SystemFilePath).Returns("C:\\mods\\" + name + ".pack");
                container.Setup(item => item.GetDirectoryContent(Moq.It.IsAny<string>()))
                    .Returns(new List<(string Path, Shared.Core.PackFiles.Models.PackFile File)>());
                return container.Object;
            }

            var overlapping = ConsumerFiles();
            overlapping["textures\\mod_owned.dds"] = [1, 2];
            var referencing = ConsumerFiles(("variantmeshes\\sub\\sub.wsmodel", $"<model><geometry>{ConsumerRigid}</geometry></model>"));
            var dbNaming = ConsumerFiles(("db\\units_tables\\data", "xx unit_a yy"));
            var vanillaReskin = ConsumerFiles();
            vanillaReskin[VanillaTexture] = [9];
            var unrelated = ConsumerFiles(("ui\\icons\\icon.png", "png"), ("script\\a.lua", "print('hi')"));

            var probed = Editors.PackOptimizer.Core.PackOptimizer.ProbeConsumers(
                source.Object,
                [Pack("overlapping", overlapping), Pack("referencing", referencing), Pack("dbNaming", dbNaming),
                 Pack("vanillaReskin", vanillaReskin), Pack("unrelated", unrelated)],
                null,
                [vanilla.Object]);

            var byName = probed.ToDictionary(pack => pack.Name);
            Assert.That(byName["overlapping"].Relevant && byName["overlapping"].FrozenFiles == 1, Is.True);
            Assert.That(byName["referencing"].Relevant && byName["referencing"].UsedFiles == 1, Is.True);
            Assert.That(byName["dbNaming"].Relevant && byName["dbNaming"].DbMentions == 1, Is.True);
            Assert.That(byName["vanillaReskin"].Relevant && byName["vanillaReskin"].VanillaOverridesReached == 1, Is.True);
            Assert.That(byName["unrelated"].Relevant, Is.False);
        }

        [Test]
        public void ProbeBatch_StaysASupersetOfTheSingleProbe_AndReusesTheScanCache()
        {
            var (_, _, fixture) = BuildVanillaReplacementState();
            fixture.AddText("variantmeshes\\variantmeshdefinitions\\unit_a.variantmeshdefinition", "<VARIANT_MESH/>");
            fixture.AddTexture("textures\\mod_owned.dds", 64, 7);
            var source = CreateDownscaleContainer(fixture.Source);
            var vanilla = CreateDownscaleContainer(fixture.Vanilla, isCaPackFile: true);
            foreach (var container in new[] { source, vanilla })
            {
                container.Setup(item => item.GetDirectoryContent(Moq.It.IsAny<string>()))
                    .Returns(new List<(string Path, Shared.Core.PackFiles.Models.PackFile File)>());
            }

            var directory = Path.Combine(Path.GetTempPath(), "probe-batch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                Shared.Core.PackFiles.Models.IPackFileContainer Pack(string name, Dictionary<string, byte[]> files)
                {
                    var diskPath = Path.Combine(directory, name + ".pack");
                    File.WriteAllText(diskPath, name);
                    var container = CreateDownscaleContainer(files);
                    container.Setup(item => item.Name).Returns(name);
                    container.Setup(item => item.SystemFilePath).Returns(diskPath);
                    container.Setup(item => item.GetDirectoryContent(Moq.It.IsAny<string>()))
                        .Returns(new List<(string Path, Shared.Core.PackFiles.Models.PackFile File)>());
                    return container.Object;
                }

                var overlapping = ConsumerFiles();
                overlapping["textures\\mod_owned.dds"] = [1, 2];
                var referencing = ConsumerFiles(("variantmeshes\\sub\\sub.wsmodel", $"<model><geometry>{ConsumerRigid}</geometry></model>"));
                var dbNaming = ConsumerFiles(("db\\units_tables\\data", "xx unit_a yy"));
                var vanillaReskin = ConsumerFiles();
                vanillaReskin[VanillaTexture] = [9];
                var unrelated = ConsumerFiles(("ui\\icons\\icon.png", "png"), ("script\\a.lua", "print('hi')"));
                var consumers = new[]
                {
                    Pack("overlapping", overlapping), Pack("referencing", referencing), Pack("dbNaming", dbNaming),
                    Pack("vanillaReskin", vanillaReskin), Pack("unrelated", unrelated),
                };
                source.Setup(item => item.Name).Returns("source");
                source.Setup(item => item.SystemFilePath).Returns(Path.Combine(directory, "source.pack"));
                File.WriteAllText(Path.Combine(directory, "source.pack"), "source");

                var single = Editors.PackOptimizer.Core.PackOptimizer.ProbeConsumers(
                    source.Object, consumers, null, [vanilla.Object]);
                var loadOrder = consumers.Prepend(source.Object).ToList();

                var first = Editors.PackOptimizer.Core.PackOptimizer.ProbeConsumersBatch(
                    loadOrder, [0], [vanilla.Object], null, directory, "schema-a");
                var second = Editors.PackOptimizer.Core.PackOptimizer.ProbeConsumersBatch(
                    loadOrder, [0], [vanilla.Object], null, directory, "schema-a");
                var otherSchema = Editors.PackOptimizer.Core.PackOptimizer.ProbeConsumersBatch(
                    loadOrder, [0], [vanilla.Object], null, directory, "schema-b");

                Assert.That(first.PacksScanned, Is.EqualTo(5));
                Assert.That(first.ScanCacheHits, Is.EqualTo(0));
                Assert.That(second.PacksScanned, Is.EqualTo(0));
                Assert.That(second.ScanCacheHits, Is.EqualTo(5));
                Assert.That(otherSchema.PacksScanned, Is.EqualTo(5), "a different DB schema ignores older scans");

                var batch = first.Sources[0].ToDictionary(pack => pack.Name);
                foreach (var pack in single)
                {
                    Assert.That(batch[pack.Name].Relevant || !pack.Relevant, Is.True, pack.Name + " must stay relevant");
                    Assert.That(batch[pack.Name].FrozenFiles, Is.EqualTo(pack.FrozenFiles), pack.Name);
                    Assert.That(batch[pack.Name].UsedFiles, Is.GreaterThanOrEqualTo(pack.UsedFiles), pack.Name);
                    Assert.That(batch[pack.Name].DbMentions, Is.GreaterThanOrEqualTo(pack.DbMentions), pack.Name);
                    Assert.That(batch[pack.Name].VanillaOverridesReached, Is.EqualTo(pack.VanillaOverridesReached), pack.Name);
                }

                Assert.That(batch["unrelated"].Relevant, Is.False);
                Assert.That(second.Sources[0].Select(pack => pack.Relevant), Is.EqualTo(first.Sources[0].Select(pack => pack.Relevant)));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Test]
        public void ScanCachePruning_KeepsOneScanPerPackAndDropsMissingExpiredAndOldFormatOnes()
        {
            var directory = Path.Combine(Path.GetTempPath(), "scan-prune-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                Shared.Core.PackFiles.Models.IPackFileContainer Pack(string name)
                {
                    var diskPath = Path.Combine(directory, name + ".pack");
                    File.WriteAllText(diskPath, name);
                    var container = CreateDownscaleContainer(new Dictionary<string, byte[]>());
                    container.Setup(item => item.SystemFilePath).Returns(diskPath);
                    return container.Object;
                }

                var scanType = GetServiceType().GetNestedType("ConsumerPackScan", BindingFlags.NonPublic)!;
                string Write(Shared.Core.PackFiles.Models.IPackFileContainer pack, string schema, string packPath)
                {
                    var cachePath = (string)GetStaticMethod("GetScanCachePath").Invoke(null, [directory, pack, schema])!;
                    GetStaticMethod("TryWriteScan").Invoke(null, [cachePath, schema, packPath, Activator.CreateInstance(scanType, true)]);
                    return cachePath;
                }

                var kept = Pack("kept");
                var superseded = Write(kept, "schema-a", kept.SystemFilePath!);
                File.SetLastWriteTimeUtc(superseded, DateTime.UtcNow.AddHours(-2));
                var current = Write(kept, "schema-b", kept.SystemFilePath!);

                var gone = Pack("gone");
                var missing = Write(gone, "schema-a", gone.SystemFilePath!);
                File.Delete(gone.SystemFilePath!);

                var stale = Pack("stale");
                var expired = Write(stale, "schema-a", stale.SystemFilePath!);
                File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-31));

                var legacy = Path.Combine(directory, "probe-scan", "legacy.scan");
                File.WriteAllBytes(legacy, [1, 2, 3, 4, 5, 6, 7, 8]);

                var removed = (int)GetStaticMethod("PruneScanCache").Invoke(null, [directory, DateTime.UtcNow])!;

                Assert.That(removed, Is.EqualTo(4));
                Assert.That(File.Exists(current), Is.True, "the newest scan of a pack stays");
                Assert.That(File.Exists(superseded), Is.False, "an older scan of the same pack goes");
                Assert.That(File.Exists(missing), Is.False, "a pack that no longer exists");
                Assert.That(File.Exists(expired), Is.False, "not read for more than 30 days");
                Assert.That(File.Exists(legacy), Is.False, "an unreadable or older format");
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Test]
        public void RigidModelLz4_IsChosenLikeTheModManagersStagingCompressor()
        {
            var compressible = new byte[400 * 1024];
            var random = new Random(7);
            var incompressible = new byte[400 * 1024];
            random.NextBytes(incompressible);
            var small = new byte[100 * 1024];
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["a\\compressible.rigid_model_v2"] = compressible,
                ["a\\random.rigid_model_v2"] = incompressible,
                ["a\\small.rigid_model_v2"] = small,
                ["a\\texture.dds"] = compressible,
            };
            var container = CreateDownscaleContainer(files);

            var selected = (HashSet<string>)GetStaticMethod("SelectRigidModelsForLz4")
                .Invoke(null, [container.Object, CancellationToken.None])!;

            Assert.That(selected, Is.EquivalentTo(new[] { "a\\compressible.rigid_model_v2" }));
        }

        [Test]
        public void RigidModelLz4_ForcedFilesAreWrittenAsLz4OnlyWhileTheOverrideIsSet()
        {
            var game = Shared.Core.Settings.GameInformationDatabase.GetGameById(Shared.Core.Settings.GameTypeEnum.Warhammer3);
            const string rigid = "variantmeshes\\a.rigid_model_v2";
            Assert.That(
                Shared.Core.PackFiles.Utility.FileCompression.GetCompressionFormat(game, rigid),
                Is.EqualTo(Shared.Core.PackFiles.Utility.CompressionFormat.None));
            try
            {
                Shared.Core.PackFiles.Utility.FileCompression.ForcedLz4Files =
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rigid };
                Assert.That(
                    Shared.Core.PackFiles.Utility.FileCompression.GetCompressionFormat(game, rigid),
                    Is.EqualTo(Shared.Core.PackFiles.Utility.CompressionFormat.Lz4));
                Assert.That(
                    Shared.Core.PackFiles.Utility.FileCompression.GetCompressionFormat(game, "variantmeshes\\b.rigid_model_v2"),
                    Is.EqualTo(Shared.Core.PackFiles.Utility.CompressionFormat.None));
            }
            finally
            {
                Shared.Core.PackFiles.Utility.FileCompression.ForcedLz4Files = null;
            }
        }

        // --- Emissive distortion copy ------------------------------------------------------------

        private const string SrEmissiveShader = "shaders/weighted4_character_emissive.xml.shader";
        private const string SrDistortion = "t_xml_emissive_distortion";
        private const string SrGlow = "t_xml_emissive_texture";
        private const string SrNoise = "textures\\commontextures\\sr_noise.dds";
        private const string SrGlowTexture = "textures\\commontextures\\sr_glow.dds";

        private static string SrEmissiveMaterialXml(
            string shader,
            string[]? omitParameters = null,
            bool withDistortion = true,
            bool withGlow = true)
        {
            var textures = new List<(string Slot, string Texture)>
            {
                (SrBase, "textures\\sr_b1.dds"), (SrNormal, "textures\\sr_n1.dds"),
            };
            if (withDistortion)
                textures.Add((SrDistortion, SrNoise));
            if (withGlow)
                textures.Add((SrGlow, SrGlowTexture));
            var xml = DownscaleMaterialXml(textures.ToArray()).Replace("shaders\\weighted2_character.xml.shader", shader);
            (string Name, string Type, string Value)[] parameters =
            [
                ("emissive_tiling", "float2", "2,3"),
                ("emissive_distort_strength", "float", "0.05"),
                ("emissive_direction", "float2", "0.5,-0.25"),
            ];
            return xml.Replace(
                "</material>",
                "<params>" + string.Concat(parameters
                    .Where(item => omitParameters?.Contains(item.Name) != true)
                    .Select(item => $"<param><name>{item.Name}</name><type>{item.Type}</type><value>{item.Value}</value></param>")) +
                "</params></material>");
        }

        private static SrWorld BuildEmissiveSrWorld(
            string shader = SrEmissiveShader,
            string[]? omitParameters = null,
            int noiseSize = 512,
            bool ownedNoise = false,
            bool withDistortion = true)
        {
            var world = new SrWorld();
            world.AddTexture("textures\\sr_b1.dds", 1024, "BC1", true, 1);
            world.AddTexture("textures\\sr_n1.dds", 512, "BC3", false, 2);
            world.AddTexture(SrNoise, noiseSize, "BC1", true, 7, ownedNoise);
            world.AddTexture(SrGlowTexture, 512, "BC1", true, 8);
            var e1 = SrMaterialPath("e1");
            world.Fixture.AddText(e1, SrEmissiveMaterialXml(shader, omitParameters, withDistortion));
            world.AddModel(SrRigid, SrWs, SrRoot, SrRigidFactory([(e1, SrBlocks((2, 3), (3, 3)))]), [[e1]]);
            return world;
        }

        private static readonly Dictionary<string, int> SrEmissiveSeeds =
            new() { [SrBase] = 1, [SrNormal] = 2, [SrDistortion] = 7 };

        private static string SrPartDropReasons(object result)
            => string.Join("\n", Get<IDictionary>(result, "PartDropReasons").Keys.Cast<string>());

        [Test]
        public void SharedRepack_EmissiveDistortion_VerifiedShader_IsCopiedNotReplaced_AndParametersRescaled()
        {
            var world = BuildEmissiveSrWorld();
            var sourceBefore = SrSnapshot(world.Fixture.Source);
            var vanillaBefore = SrSnapshot(world.Fixture.Vanilla);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result) + SrPartDropReasons(result));
            AssertFunnelReconciles(result);
            var built = SrBuilt(result).Single();
            var outputs = Get<IEnumerable>(built, "Outputs").Cast<object>().ToList();
            Assert.That(outputs, Has.Count.EqualTo(3));
            var copy = outputs.Single(output => Get<bool>(output, "IsCopy"));
            Assert.That(Get<string>(copy, "Slot"), Is.EqualTo(SrDistortion));
            Assert.That(Get<string>(copy, "Path"), Does.EndWith("_repack_emissive_distortion.dds"));
            Assert.That(Get<IEnumerable>(copy, "Sources").Cast<string>(), Is.EqualTo(new[] { SrNoise }));

            // Cost counts the copy, but nothing is "replaced" by it.
            var replaced = Get<IEnumerable>(built, "Replaced").Cast<(string Path, bool Owned, long Bytes)>().ToList();
            Assert.That(replaced.Select(item => item.Path), Is.EquivalentTo(new[] { "textures\\sr_b1.dds", "textures\\sr_n1.dds" }));
            Assert.That(Get<long>(built, "Before"), Is.EqualTo(replaced.Sum(item => item.Bytes)));
            Assert.That(Get<long>(built, "After"), Is.EqualTo(outputs.Sum(output => Get<long>(output, "Bytes"))));
            Assert.That(world.Fixture.Vanilla[SrNoise], Is.EqualTo(vanillaBefore[SrNoise]));
            foreach (var (path, bytes) in sourceBefore)
                Assert.That(world.Fixture.Source[path], Is.EqualTo(bytes), path);

            // The crop shows the noise at the new UVs; the glow texture stays as authored.
            var factory = SrRigidFactory([(SrMaterialPath("e1"), SrBlocks((2, 3), (3, 3)))]);
            var (samples, _) = AssertSrPixels(world, SrRigid, SrWs, factory, SrEmissiveSeeds);
            Assert.That(samples, Is.GreaterThan(0));
            var generatedPath = SrWsMaterial(world.State, SrWs, 0, 0);
            var generated = Encoding.UTF8.GetString(world.Fixture.Output[generatedPath]);
            Assert.That(generated, Does.Contain(SrGlowTexture));
            Assert.That(generated, Does.Not.Contain(SrNoise));
            Assert.That(SrSlotNode(generated, SrDistortion).OuterXml, Does.Contain("_repack_emissive_distortion.dds"));

            // tiling / s, strength * s, direction * s with the equal U and V scale s.
            var scaleU = 1024.0 / Get<int>(built, "LayoutWidth");
            var scaleV = 1024.0 / Get<int>(built, "LayoutHeight");
            Assert.That(scaleU, Is.EqualTo(scaleV));
            var document = new XmlDocument();
            document.LoadXml(generated);
            string Value(string name) => document.SelectNodes("/material/params/param")!.Cast<XmlNode>()
                .Single(node => node.SelectSingleNode("name")!.InnerText == name).SelectSingleNode("value")!.InnerText;
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            Assert.That(Value("emissive_tiling"), Is.EqualTo(string.Create(invariant, $"{2 / scaleU:R},{3 / scaleU:R}")));
            Assert.That(Value("emissive_distort_strength"), Is.EqualTo(string.Create(invariant, $"{0.05 * scaleU:R}")));
            Assert.That(Value("emissive_direction"), Is.EqualTo(string.Create(invariant, $"{0.5 * scaleU:R},{-0.25 * scaleU:R}")));
            Assert.That(Get<IEnumerable>(built, "EmissiveScales").Cast<string>().Count(), Is.EqualTo(1));

            // Counters and report.
            Assert.That(Get<int>(result, "EmissiveParts"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "EmissiveCopies"), Is.EqualTo(1));
            Assert.That(Get<long>(result, "EmissiveCopyBytes"), Is.EqualTo(Get<byte[]>(copy, "Data").LongLength));
            Assert.That(Get<int>(result, "EmissiveMaterialsRescaled"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "EmissiveAspectGroups"), Is.EqualTo(1));
            var section = BuildSharedRepackSection(Enabled(world));
            Assert.That(section, Does.Contain("Emissive distortion copy: 1 part(s) moved"));
            Assert.That(section, Does.Contain("1 copy texture(s) written"));
            Assert.That(section, Does.Contain("per-chart phase shift").And.Contain("ASSUMPTION"));
            Assert.That(section, Does.Contain("emissive: ").And.Contain(": UV scale").And.Contain("emissive_tiling 2,3 -> "));
            Assert.That(section, Does.Contain("not a replacement"));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_EmissiveDistortion_OwnedNoise_IsNeverReplacedOrPruned()
        {
            var world = BuildEmissiveSrWorld(ownedNoise: true);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result) + SrPartDropReasons(result));
            SetStateProperty(world.State, "SharedTextureRepackRequested", true);
            SetStateProperty(world.State, "SharedTextureRepackEnabled", true);
            GetStaticMethod("FinalizeSharedTextureRepack").Invoke(null, [world.State]);
            // Only owned textures that were replaced count; the noise was copied, not replaced.
            Assert.That(Get<int>(result, "OwnedReplaced"), Is.EqualTo(0));
            Assert.That(Get<List<string>>(result, "OwnedPruned"), Is.Empty);
            Assert.That(world.Fixture.Output.ContainsKey(SrNoise), Is.True);
            Assert.That(SrValidate(world), Is.Empty);
        }

        [TestCase("shaders/weighted4_phoenix_standard_overlay.xml.shader")]
        [TestCase("")]
        public void SharedRepack_EmissiveDistortion_UnverifiedShader_KeepsThePartBlockedWithTheNewReason(string shader)
        {
            var world = BuildEmissiveSrWorld(shader);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(0));
            var reasons = SrPartDropReasons(result);
            Assert.That(reasons, Does.Contain("the material's shader is not verified for emissive repacking ("));
            Assert.That(reasons, Does.Contain(shader.Length == 0 ? "unknown shader" : shader));
            Assert.That(reasons, Does.Not.Contain("non-atlasable slot"));
            Assert.That(Get<int>(result, "EmissiveCopies"), Is.EqualTo(0));
            Assert.That(world.Fixture.Output.Keys.Count(key => key.Contains("_repack_")), Is.EqualTo(0));
            var section = BuildSharedRepackSection(Enabled(world));
            Assert.That(section, Does.Contain("x the material's shader is not verified for emissive repacking ("));
        }

        [TestCase("emissive_tiling")]
        [TestCase("emissive_distort_strength")]
        [TestCase("emissive_direction")]
        public void SharedRepack_EmissiveDistortion_MissingParameter_KeepsThePartBlockedWithTheNewReason(string missing)
        {
            var world = BuildEmissiveSrWorld(omitParameters: [missing]);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(0));
            Assert.That(SrPartDropReasons(result), Does.Contain("the material lacks emissive parameter " + missing));
        }

        [Test]
        public void SharedRepack_EmissiveTextureWithoutDistortion_KeepsTheOriginalSlotReason()
        {
            var world = BuildEmissiveSrWorld(withDistortion: false);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(0));
            Assert.That(
                SrPartDropReasons(result),
                Does.Contain("a bound material has a texture in the non-atlasable slot t_xml_emissive_texture"));
        }

        [Test]
        public void SharedRepack_EmissiveParameterMath_DividesTilingAndMultipliesStrengthAndDirection()
        {
            var computed = GetStaticMethod("ComputeSharedRepackEmissiveParameters")
                .Invoke(null, [new[] { 2.0, 3.0 }, 0.05, new[] { 0.5, -0.25 }, 0.25])!;
            var (tiling, strength, direction) = ((double[] Tiling, double Strength, double[] Direction))computed;
            Assert.That(tiling, Is.EqualTo(new[] { 8.0, 12.0 }));
            Assert.That(strength, Is.EqualTo(0.0125).Within(1e-15));
            Assert.That(direction, Is.EqualTo(new[] { 0.125, -0.0625 }));

            // The rewrite: invariant "R" text, one description, and a refusal for unequal scales.
            var document = new XmlDocument();
            document.LoadXml(SrEmissiveMaterialXml(SrEmissiveShader));
            var arguments = new object?[] { document, 0.25, 0.25, null };
            Assert.That((bool)GetStaticMethod("TryScaleSharedRepackEmissiveParameters").Invoke(null, arguments)!, Is.True);
            var text = document.OuterXml;
            Assert.That(text, Does.Contain("<value>8,12</value>"));
            Assert.That(text, Does.Contain("<value>0.0125</value>"));
            Assert.That(text, Does.Contain("<value>0.125,-0.0625</value>"));

            var unequal = new XmlDocument();
            unequal.LoadXml(SrEmissiveMaterialXml(SrEmissiveShader));
            var before = unequal.OuterXml;
            arguments = [unequal, 0.25, 0.5, null];
            Assert.That((bool)GetStaticMethod("TryScaleSharedRepackEmissiveParameters").Invoke(null, arguments)!, Is.False);
            Assert.That((string)arguments[3]!, Does.Contain("not equal in U and V"));
            Assert.That(unequal.OuterXml, Is.EqualTo(before));

            var missing = new XmlDocument();
            missing.LoadXml(SrEmissiveMaterialXml(SrEmissiveShader, ["emissive_direction"]));
            arguments = [missing, 0.25, 0.25, null];
            Assert.That((bool)GetStaticMethod("TryScaleSharedRepackEmissiveParameters").Invoke(null, arguments)!, Is.False);
            Assert.That((string)arguments[3]!, Does.Contain("emissive_direction"));
        }

        // ---- dissolve shaders: detail kept + scale divided, map copied ----

        private const string SrDissolveShader = "shaders/weighted4_character_dissolve.xml.shader";
        private const string SrDissolveEmissiveShader = "shaders/weighted4_character_dissolve_emissive.xml.shader";
        private const string SrDissolveMap = "t_xml_dissolve_map";
        private const string SrDissolveDetail = "t_xml_dissolve_detail";
        private const string SrDissolveMapTexture = "textures\\commontextures\\sr_dissolve_map.dds";
        private const string SrDissolveDetailTexture = "textures\\commontextures\\sr_dissolve_detail.dds";

        private static string SrDissolveMaterialXml(
            string shader,
            bool withEmissive = false,
            string? scaleValue = "1,0.5",
            string scaleType = "float2")
        {
            var textures = new List<(string Slot, string Texture)>
            {
                (SrBase, "textures\\sr_b1.dds"), (SrNormal, "textures\\sr_n1.dds"),
                (SrDissolveMap, SrDissolveMapTexture), (SrDissolveDetail, SrDissolveDetailTexture),
            };
            if (withEmissive)
            {
                textures.Add((SrDistortion, SrNoise));
                textures.Add((SrGlow, SrGlowTexture));
            }

            var xml = DownscaleMaterialXml(textures.ToArray()).Replace("shaders\\weighted2_character.xml.shader", shader);
            var parameters = new List<string>();
            if (scaleValue != null)
                parameters.Add($"<param><name>dissolve_uv_scale</name><type>{scaleType}</type><value>{scaleValue}</value></param>");
            if (withEmissive)
            {
                parameters.Add("<param><name>emissive_tiling</name><type>float2</type><value>2,3</value></param>");
                parameters.Add("<param><name>emissive_distort_strength</name><type>float</type><value>0.05</value></param>");
                parameters.Add("<param><name>emissive_direction</name><type>float2</type><value>0.5,-0.25</value></param>");
            }

            return xml.Replace("</material>", "<params>" + string.Concat(parameters) + "</params></material>");
        }

        private static SrWorld BuildDissolveSrWorld(
            string shader = SrDissolveShader,
            bool withEmissive = false,
            string? scaleValue = "1,0.5",
            string scaleType = "float2",
            bool ownedMap = false)
        {
            var world = new SrWorld();
            world.AddTexture("textures\\sr_b1.dds", 1024, "BC1", true, 1);
            world.AddTexture("textures\\sr_n1.dds", 512, "BC3", false, 2);
            world.AddTexture(SrDissolveMapTexture, 512, "BC1", true, 9, ownedMap);
            world.AddTexture(SrDissolveDetailTexture, 1024, "BC1", true, 10);
            if (withEmissive)
            {
                world.AddTexture(SrNoise, 512, "BC1", true, 7);
                world.AddTexture(SrGlowTexture, 512, "BC1", true, 8);
            }

            var d1 = SrMaterialPath("d1");
            world.Fixture.AddText(d1, SrDissolveMaterialXml(shader, withEmissive, scaleValue, scaleType));
            world.AddModel(SrRigid, SrWs, SrRoot, SrRigidFactory([(d1, SrBlocks((2, 3), (3, 3)))]), [[d1]]);
            return world;
        }

        private static readonly Dictionary<string, int> SrDissolveSeeds =
            new() { [SrBase] = 1, [SrNormal] = 2, [SrDissolveMap] = 9 };

        private static string SrParamValue(string materialXml, string name)
        {
            var document = new XmlDocument();
            document.LoadXml(materialXml);
            return document.SelectNodes("/material/params/param")!.Cast<XmlNode>()
                .Single(node => node.SelectSingleNode("name")!.InnerText == name).SelectSingleNode("value")!.InnerText;
        }

        [Test]
        public void SharedRepack_Dissolve_VerifiedShader_CopiesTheMapKeepsTheDetailAndDividesTheScalePerAxis()
        {
            var world = BuildDissolveSrWorld();
            var sourceBefore = SrSnapshot(world.Fixture.Source);
            var vanillaBefore = SrSnapshot(world.Fixture.Vanilla);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result) + SrPartDropReasons(result));
            AssertFunnelReconciles(result);
            var built = SrBuilt(result).Single();
            var outputs = Get<IEnumerable>(built, "Outputs").Cast<object>().ToList();
            Assert.That(outputs, Has.Count.EqualTo(3));
            var copy = outputs.Single(output => Get<bool>(output, "IsCopy"));
            Assert.That(Get<string>(copy, "Slot"), Is.EqualTo(SrDissolveMap));
            Assert.That(Get<string>(copy, "Path"), Does.EndWith("_repack_dissolve_map.dds"));
            Assert.That(Get<IEnumerable>(copy, "Sources").Cast<string>(), Is.EqualTo(new[] { SrDissolveMapTexture }));

            // Neither the map nor the detail is a member, replaced or touched; the copy counts as cost.
            var replaced = Get<IEnumerable>(built, "Replaced").Cast<(string Path, bool Owned, long Bytes)>().ToList();
            Assert.That(replaced.Select(item => item.Path), Is.EquivalentTo(new[] { "textures\\sr_b1.dds", "textures\\sr_n1.dds" }));
            Assert.That(Get<long>(built, "After"), Is.EqualTo(outputs.Sum(output => Get<long>(output, "Bytes"))));
            foreach (var (path, bytes) in vanillaBefore)
                Assert.That(world.Fixture.Vanilla[path], Is.EqualTo(bytes), path);
            foreach (var (path, bytes) in sourceBefore)
                Assert.That(world.Fixture.Source[path], Is.EqualTo(bytes), path);

            // The crop shows the map at the new UVs (pixel-checked); the detail texture stays as authored.
            var factory = SrRigidFactory([(SrMaterialPath("d1"), SrBlocks((2, 3), (3, 3)))]);
            var (samples, _) = AssertSrPixels(world, SrRigid, SrWs, factory, SrDissolveSeeds);
            Assert.That(samples, Is.GreaterThan(0));
            var generated = Encoding.UTF8.GetString(world.Fixture.Output[SrWsMaterial(world.State, SrWs, 0, 0)]);
            Assert.That(SrSlotNode(generated, SrDissolveMap).OuterXml, Does.Contain("_repack_dissolve_map.dds"));
            Assert.That(generated, Does.Not.Contain(SrDissolveMapTexture));
            Assert.That(SrSlotNode(generated, SrDissolveDetail).OuterXml, Does.Contain(SrDissolveDetailTexture));

            // dissolve_uv_scale / scale per axis, with unequal U and V scales (no aspect restriction).
            var scaleU = 1024.0 / Get<int>(built, "LayoutWidth");
            var scaleV = 1024.0 / Get<int>(built, "LayoutHeight");
            Assert.That(scaleU, Is.Not.EqualTo(scaleV));
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            Assert.That(
                SrParamValue(generated, "dissolve_uv_scale"),
                Is.EqualTo(string.Create(invariant, $"{1 / scaleU:R},{0.5 / scaleV:R}")));
            Assert.That(Get<IEnumerable>(built, "DissolveScales").Cast<string>().Single(), Does.Contain("dissolve_uv_scale -> "));

            // Counters, report and no aspect-restricted group.
            Assert.That(Get<int>(result, "DissolveParts"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "DissolveCopies"), Is.EqualTo(1));
            Assert.That(Get<long>(result, "DissolveCopyBytes"), Is.EqualTo(Get<byte[]>(copy, "Data").LongLength));
            Assert.That(Get<int>(result, "DissolveMaterialsRescaled"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "EmissiveParts"), Is.EqualTo(0));
            Assert.That(Get<int>(result, "EmissiveCopies"), Is.EqualTo(0));
            Assert.That(Get<int>(result, "EmissiveAspectGroups"), Is.EqualTo(0));
            var section = BuildSharedRepackSection(Enabled(world));
            Assert.That(section, Does.Contain("Dissolve: 1 part(s) moved with a copied t_xml_dissolve_map"));
            Assert.That(section, Does.Contain("dissolve: ").And.Contain(": UV scale").And.Contain("dissolve_uv_scale -> "));
            Assert.That(section, Does.Contain("per-chart phase shift").And.Contain("ASSUMPTION"));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_Dissolve_OwnedMap_IsNeverReplacedOrPruned()
        {
            var world = BuildDissolveSrWorld(ownedMap: true);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result) + SrPartDropReasons(result));
            SetStateProperty(world.State, "SharedTextureRepackRequested", true);
            SetStateProperty(world.State, "SharedTextureRepackEnabled", true);
            GetStaticMethod("FinalizeSharedTextureRepack").Invoke(null, [world.State]);
            Assert.That(Get<int>(result, "OwnedReplaced"), Is.EqualTo(0));
            Assert.That(Get<List<string>>(result, "OwnedPruned"), Is.Empty);
            Assert.That(world.Fixture.Output.ContainsKey(SrDissolveMapTexture), Is.True);
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_DissolveWithEmissive_AllThreeSpecialSlotsBuildInOnePart()
        {
            var world = BuildDissolveSrWorld(SrDissolveEmissiveShader, withEmissive: true);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result) + SrPartDropReasons(result));
            AssertFunnelReconciles(result);
            var built = SrBuilt(result).Single();
            var outputs = Get<IEnumerable>(built, "Outputs").Cast<object>().ToList();
            Assert.That(
                outputs.Where(output => Get<bool>(output, "IsCopy")).Select(output => Get<string>(output, "Slot")),
                Is.EquivalentTo(new[] { SrDissolveMap, SrDistortion }));
            Assert.That(outputs, Has.Count.EqualTo(4));
            Assert.That(Get<int>(result, "DissolveParts"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "EmissiveParts"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "DissolveCopies"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "EmissiveCopies"), Is.EqualTo(1));
            // The emissive restriction applies, so the scale is equal in U and V, and both rescales ran.
            var generated = Encoding.UTF8.GetString(world.Fixture.Output[SrWsMaterial(world.State, SrWs, 0, 0)]);
            var scale = 1024.0 / Get<int>(built, "LayoutWidth");
            Assert.That(scale, Is.EqualTo(1024.0 / Get<int>(built, "LayoutHeight")));
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            Assert.That(SrParamValue(generated, "dissolve_uv_scale"), Is.EqualTo(string.Create(invariant, $"{1 / scale:R},{0.5 / scale:R}")));
            Assert.That(SrParamValue(generated, "emissive_tiling"), Is.EqualTo(string.Create(invariant, $"{2 / scale:R},{3 / scale:R}")));
            Assert.That(SrSlotNode(generated, SrDissolveMap).OuterXml, Does.Contain("_repack_dissolve_map.dds"));
            Assert.That(SrSlotNode(generated, SrDistortion).OuterXml, Does.Contain("_repack_emissive_distortion.dds"));
            Assert.That(SrSlotNode(generated, SrDissolveDetail).OuterXml, Does.Contain(SrDissolveDetailTexture));
            var seeds = new Dictionary<string, int>(SrDissolveSeeds) { [SrDistortion] = 7 };
            var factory = SrRigidFactory([(SrMaterialPath("d1"), SrBlocks((2, 3), (3, 3)))]);
            var (samples, _) = AssertSrPixels(world, SrRigid, SrWs, factory, seeds);
            Assert.That(samples, Is.GreaterThan(0));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [TestCase("shaders/weighted2_ethereal_dissolve_vfx.xml.shader")]
        [TestCase("shaders/weighted2_saytang_dissolve_alpha.xml.shader")]
        [TestCase("")]
        public void SharedRepack_Dissolve_UnverifiedShader_KeepsThePartBlockedWithTheNewReason(string shader)
        {
            var world = BuildDissolveSrWorld(shader);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(0));
            var reasons = SrPartDropReasons(result);
            Assert.That(reasons, Does.Contain("the material's shader is not verified for dissolve repacking ("));
            Assert.That(reasons, Does.Contain(shader.Length == 0 ? "unknown shader" : shader));
            Assert.That(reasons, Does.Not.Contain("non-atlasable slot"));
            Assert.That(Get<int>(result, "DissolveCopies"), Is.EqualTo(0));
            Assert.That(world.Fixture.Output.Keys.Count(key => key.Contains("_repack_")), Is.EqualTo(0));
            var section = BuildSharedRepackSection(Enabled(world));
            Assert.That(section, Does.Contain("x the material's shader is not verified for dissolve repacking ("));
        }

        [TestCase(null, "float2")]
        [TestCase("0.3", "float2")]
        [TestCase("1,1", "float")]
        [TestCase("1,x", "float2")]
        public void SharedRepack_Dissolve_MissingOrMalformedScale_KeepsThePartBlockedWithTheNewReason(string? value, string type)
        {
            var world = BuildDissolveSrWorld(scaleValue: value, scaleType: type);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(0));
            Assert.That(SrPartDropReasons(result), Does.Contain("the material lacks dissolve parameter dissolve_uv_scale"));
            Assert.That(Get<int>(result, "DissolveCopies"), Is.EqualTo(0));
        }

        [Test]
        public void SharedRepack_Float2ParameterScale_DividesPerAxisInInvariantText()
        {
            var document = new XmlDocument();
            document.LoadXml(SrDissolveMaterialXml(SrDissolveShader, scaleValue: "1,0.5"));
            var arguments = new object?[] { document, "dissolve_uv_scale", 0.5, 0.25, null };
            Assert.That((bool)GetStaticMethod("TryScaleSharedRepackFloat2Parameter").Invoke(null, arguments)!, Is.True);
            Assert.That(SrParamValue(document.OuterXml, "dissolve_uv_scale"), Is.EqualTo("2,2"));
            Assert.That((string)arguments[4]!, Is.EqualTo("2,2 (was 1,0.5)"));

            foreach (var (value, type) in new[] { ("0.3", "float2"), ("1,1", "float") })
            {
                var bad = new XmlDocument();
                bad.LoadXml(SrDissolveMaterialXml(SrDissolveShader, scaleValue: value, scaleType: type));
                var before = bad.OuterXml;
                arguments = [bad, "dissolve_uv_scale", 0.5, 0.5, null];
                Assert.That((bool)GetStaticMethod("TryScaleSharedRepackFloat2Parameter").Invoke(null, arguments)!, Is.False);
                Assert.That(bad.OuterXml, Is.EqualTo(before));
            }
        }

        [Test]
        public void SharedRepack_DissolveMapCopySlot_FollowsTheNoiseRules_WithoutEmissiveCompensation()
        {
            const string baseTexture = "textures\\b.dds";
            const string noise = "textures\\commontextures\\noise_colour.dds";
            var part = SrPart(
                new Dictionary<string, string> { [SrBase] = baseTexture, [SrDissolveMap] = noise }, SrDissolveMap);
            var entries = new Dictionary<string, object>
            {
                [baseTexture] = SrEntry(baseTexture, 2048, 1024),
                [noise] = SrEntry(noise, 256, 256),
            };

            // Never classified (a classifier calling the noise uniform is ignored) and never a set member.
            var tuple = SrBuildTuples([part], entries, texture => texture == noise ? true : null, out var violating).Single();

            Assert.That(violating, Is.Empty);
            Assert.That(Get<string?>(tuple, "Excluded"), Is.Null);
            Assert.That(Get<IDictionary>(tuple, "ConstantSlots").Count, Is.EqualTo(0));
            Assert.That(Get<HashSet<string>>(tuple, "CopySlots"), Is.EquivalentTo(new[] { SrDissolveMap }));
            Assert.That(Get<IEnumerable>(tuple, "MemberTextures").Cast<string>(), Is.EqualTo(new[] { baseTexture }));
            Assert.That((bool)tuple.GetType().GetMethod("IsNoiseCopySlot")!.Invoke(tuple, [SrDissolveMap])!, Is.True);
            Assert.That((bool)tuple.GetType().GetMethod("IsGeneralCopySlot")!.Invoke(tuple, [SrDissolveMap])!, Is.False);
            // No parameter compensation, so no aspect restriction in the group key.
            Assert.That(Get<bool>(tuple, "EmissiveCompensation"), Is.False);
            // Like the emissive noise: the noise decides the cell but not the alignment.
            Assert.That(Get<int>(tuple, "Cell"), Is.EqualTo(32));
            Assert.That(Get<int>(tuple, "Align"), Is.EqualTo(32));
            var key = Get<string>(tuple, "GroupKey");
            Assert.That(key, Does.Contain($"{SrDissolveMap}:BC1:True:-3:-2:copy:{noise}"));
            Assert.That(key, Does.Not.Contain("aspect"));
        }

        // --- Verified weighted flow overlay -------------------------------------------------------

        private const string SrFlowShader = "shaders/weighted2_flow_overlay.xml.shader";
        private const string SrFlowMap = "t_xml_flow_map";
        private const string SrFlowGuid = "t_xml_df7959d8_d174_49d2_88f1_ffc28b954e5f";
        private const string SrFlowNoise = "t_xml_flow_noise";
        private const string SrFlowNormal = "t_xml_flow_normal";
        private const string SrFlowNormal2 = "t_xml_flow_normal_2";
        private const string SrFlowMapTexture = "textures\\sr_flow_map.dds";
        private const string SrFlowCommonMapTexture = "textures\\commontextures\\sr_flow_map.dds";
        private const string SrFlowUniformMapTexture = "textures\\sr_flow_uniform.dds";
        private const string SrFlowGuidTexture = "textures\\commontextures\\sr_flow_guid.dds";
        private const string SrFlowNoiseTexture = "textures\\commontextures\\sr_flow_noise.dds";
        private const string SrFlowNormalTexture = "textures\\commontextures\\sr_flow_normal.dds";
        private const string SrFlowNormal2Texture = "textures\\commontextures\\sr_flow_normal_2.dds";

        private static string SrFlowMaterialXml(
            string shader,
            string flowMap,
            string? omitParameter = null,
            string? overrideParameter = null,
            string? overrideValue = null,
            string overrideType = "float")
        {
            var textures = new List<(string Slot, string Texture)>
            {
                (SrBase, "textures\\sr_b1.dds"),
                (SrNormal, "textures\\sr_n1.dds"),
                (SrFlowMap, flowMap),
                (SrFlowGuid, SrFlowGuidTexture),
                (SrFlowNoise, SrFlowNoiseTexture),
                (SrFlowNormal, SrFlowNormalTexture),
                (SrFlowNormal2, SrFlowNormal2Texture),
            };
            var xml = DownscaleMaterialXml(textures.ToArray())
                .Replace("shaders\\weighted2_character.xml.shader", shader);
            (string Name, string Type, string Value)[] values =
            [
                ("flow_noise_speed", "float", "0.25"),
                ("flow_noise_tiling", "float", "2"),
                ("flow_texture_tiling", "float2", "1.5,0.75"),
                ("flow_distortion_stength", "float", "0.7"),
            ];
            var parameters = string.Concat(values
                .Where(item => !item.Name.Equals(omitParameter, StringComparison.OrdinalIgnoreCase))
                .Select(item =>
                {
                    var changed = item.Name.Equals(overrideParameter, StringComparison.OrdinalIgnoreCase);
                    var value = changed ? overrideValue : item.Value;
                    var type = changed ? overrideType : item.Type;
                    return value == null
                        ? string.Empty
                        : $"<param><name>{item.Name}</name><type>{type}</type><value>{value}</value></param>";
                }));
            return xml.Replace("</material>", $"<params>{parameters}</params></material>");
        }

        private static SrWorld BuildFlowSrWorld(
            string shader = SrFlowShader,
            string flowMap = SrFlowMapTexture,
            string? omitParameter = null,
            string? overrideParameter = null,
            string? overrideValue = null,
            string overrideType = "float")
        {
            var world = new SrWorld();
            world.AddTexture("textures\\sr_b1.dds", 1024, "BC1", true, 1);
            world.AddTexture("textures\\sr_n1.dds", 512, "BC3", false, 2);
            if (flowMap.Equals(SrFlowUniformMapTexture, StringComparison.OrdinalIgnoreCase))
                world.Fixture.Vanilla[flowMap] = UniformDds(4, 3);
            else
                world.AddTexture(flowMap, flowMap.Contains("commontextures", StringComparison.OrdinalIgnoreCase) ? 512 : 1024,
                    "BC1", true, 3);
            world.AddTexture(SrFlowGuidTexture, 256, "BC1", true, 4);
            world.AddTexture(SrFlowNoiseTexture, 256, "BC1", true, 5);
            world.AddTexture(SrFlowNormalTexture, 256, "BC3", false, 6);
            world.AddTexture(SrFlowNormal2Texture, 256, "BC3", false, 7);
            var material = SrMaterialPath("flow1");
            world.Fixture.AddText(material,
                SrFlowMaterialXml(shader, flowMap, omitParameter, overrideParameter, overrideValue, overrideType));
            world.AddModel(SrRigid, SrWs, SrRoot,
                SrRigidFactory([(material, SrBlocks((2, 3), (3, 3)))]), [[material]]);
            return world;
        }

        [Test]
        public void SharedRepack_FlowOverlay_AtlasesModelMap_CopiesGuid_AndCompensatesOnlyVerifiedMaterial()
        {
            var world = BuildFlowSrWorld();
            var sourceBefore = SrSnapshot(world.Fixture.Source);
            var vanillaBefore = SrSnapshot(world.Fixture.Vanilla);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result) + SrPartDropReasons(result));
            AssertFunnelReconciles(result);
            var built = SrBuilt(result).Single();
            var outputs = Get<IEnumerable>(built, "Outputs").Cast<object>().ToList();
            var flowMapAtlas = outputs.Single(output => Get<string>(output, "Slot") == SrFlowMap);
            var guidCopy = outputs.Single(output => Get<string>(output, "Slot") == SrFlowGuid);
            Assert.That(Get<bool>(flowMapAtlas, "IsCopy"), Is.False);
            Assert.That(Get<string>(flowMapAtlas, "Path"), Does.EndWith("_repack_flow_map.dds"));
            Assert.That(Get<bool>(guidCopy, "IsCopy"), Is.True);
            Assert.That(Get<string>(guidCopy, "Path"), Does.EndWith("_repack_flow_guid.dds"));
            Assert.That(Get<IEnumerable>(guidCopy, "Sources").Cast<string>(), Is.EqualTo(new[] { SrFlowGuidTexture }));
            Assert.That(Get<int>(result, "FlowCompensationParts"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "FlowMapAtlases"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "FlowMapCopies"), Is.Zero);
            Assert.That(Get<int>(result, "FlowGuidCopies"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "FlowMaterialsRescaled"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "FlowAspectGroups"), Is.GreaterThan(0));
            Assert.That(Get<int>(built, "FlowMapAtlases"), Is.EqualTo(1));

            var replaced = Get<IEnumerable>(built, "Replaced").Cast<(string Path, bool Owned, long Bytes)>()
                .Select(item => item.Path).ToList();
            Assert.That(replaced, Does.Contain(SrFlowMapTexture));
            Assert.That(replaced, Does.Not.Contain(SrFlowGuidTexture));
            Assert.That(Get<long>(built, "After"), Is.EqualTo(outputs.Sum(output => Get<long>(output, "Bytes"))));
            foreach (var (path, bytes) in sourceBefore)
                Assert.That(world.Fixture.Source[path], Is.EqualTo(bytes), path);
            foreach (var (path, bytes) in vanillaBefore)
                Assert.That(world.Fixture.Vanilla[path], Is.EqualTo(bytes), path);

            var generated = Encoding.UTF8.GetString(world.Fixture.Output[SrWsMaterial(world.State, SrWs, 0, 0)]);
            var original = Encoding.UTF8.GetString(world.Fixture.Source[SrMaterialPath("flow1")]);
            foreach (var slot in new[] { SrFlowNoise, SrFlowNormal, SrFlowNormal2 })
                Assert.That(SrSlotNode(generated, slot).OuterXml, Is.EqualTo(SrSlotNode(original, slot).OuterXml));
            Assert.That(SrSlotNode(generated, SrFlowMap).OuterXml, Does.Contain("_repack_flow_map.dds"));
            Assert.That(SrSlotNode(generated, SrFlowGuid).OuterXml, Does.Contain("_repack_flow_guid.dds"));

            var scaleU = 1024.0 / Get<int>(built, "LayoutWidth");
            var scaleV = 1024.0 / Get<int>(built, "LayoutHeight");
            Assert.That(scaleU, Is.EqualTo(scaleV));
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            Assert.That(SrParamValue(generated, "flow_noise_speed"), Is.EqualTo(string.Create(invariant, $"{0.25 * scaleU:R}")));
            Assert.That(SrParamValue(generated, "flow_noise_tiling"), Is.EqualTo(string.Create(invariant, $"{2 / scaleU:R}")));
            Assert.That(SrParamValue(generated, "flow_texture_tiling"), Is.EqualTo(string.Create(invariant, $"{1.5 / scaleU:R},{0.75 / scaleU:R}")));
            Assert.That(SrParamValue(generated, "flow_distortion_stength"), Is.EqualTo("0.7"));

            var seeds = new Dictionary<string, int>
            {
                [SrBase] = 1, [SrNormal] = 2, [SrFlowMap] = 3, [SrFlowGuid] = 4,
            };
            var factory = SrRigidFactory([(SrMaterialPath("flow1"), SrBlocks((2, 3), (3, 3)))]);
            var (samples, _) = AssertSrPixels(world, SrRigid, SrWs, factory, seeds);
            Assert.That(samples, Is.GreaterThan(0));
            Assert.That(SrValidate(world), Is.Empty);
            var report = BuildSharedRepackSection(Enabled(world));
            Assert.That(report, Does.Contain("Flow overlay: 1 part(s) with verified flow compensation"));
            Assert.That(report, Does.Contain("flow_map joins the local atlas closure"));
            Assert.That(report, Does.Contain("chart phase changes the sampled noise"));
        }

        [Test]
        public void SharedRepack_FlowOverlay_CommonMapIsCopyAndUniformMapRemainsAuthoredWithAspectConstraint()
        {
            var common = BuildFlowSrWorld(flowMap: SrFlowCommonMapTexture);
            var commonResult = common.Run();
            Assert.That(Get<int>(commonResult, "Built"), Is.EqualTo(1), SrSkips(commonResult) + SrPartDropReasons(commonResult));
            var commonBuilt = SrBuilt(commonResult).Single();
            var commonOutputs = Get<IEnumerable>(commonBuilt, "Outputs").Cast<object>().ToList();
            var mapCopy = commonOutputs.Single(output => Get<string>(output, "Slot") == SrFlowMap);
            Assert.That(Get<bool>(mapCopy, "IsCopy"), Is.True);
            Assert.That(Get<string>(mapCopy, "Path"), Does.EndWith("_repack_flow_map.dds"));
            Assert.That(Get<IEnumerable>(mapCopy, "Sources").Cast<string>(), Is.EqualTo(new[] { SrFlowCommonMapTexture }));
            Assert.That(Get<IEnumerable>(commonBuilt, "Replaced").Cast<(string Path, bool Owned, long Bytes)>()
                .Select(item => item.Path), Does.Not.Contain(SrFlowCommonMapTexture));
            Assert.That(common.Fixture.Vanilla[SrFlowCommonMapTexture], Is.Not.Empty);
            var commonFactory = SrRigidFactory([(SrMaterialPath("flow1"), SrBlocks((2, 3), (3, 3)))]);
            AssertSrPixels(common, SrRigid, SrWs, commonFactory,
                new Dictionary<string, int> { [SrBase] = 1, [SrNormal] = 2, [SrFlowMap] = 3, [SrFlowGuid] = 4 });
            Assert.That(SrValidate(common), Is.Empty);

            var uniform = BuildFlowSrWorld(flowMap: SrFlowUniformMapTexture);
            var uniformResult = uniform.Run();
            Assert.That(Get<int>(uniformResult, "Built"), Is.EqualTo(1), SrSkips(uniformResult) + SrPartDropReasons(uniformResult));
            var uniformBuilt = SrBuilt(uniformResult).Single();
            Assert.That(Get<int>(uniformResult, "FlowMapAtlases"), Is.Zero);
            Assert.That(Get<int>(uniformResult, "FlowMapCopies"), Is.Zero);
            Assert.That(Get<int>(uniformResult, "FlowAspectGroups"), Is.GreaterThan(0));
            var uniformMaterial = Encoding.UTF8.GetString(uniform.Fixture.Output[SrWsMaterial(uniform.State, SrWs, 0, 0)]);
            Assert.That(SrSlotNode(uniformMaterial, SrFlowMap).SelectSingleNode("source")!.InnerText,
                Is.EqualTo(SrFlowUniformMapTexture));
            Assert.That(1024.0 / Get<int>(uniformBuilt, "LayoutWidth"),
                Is.EqualTo(1024.0 / Get<int>(uniformBuilt, "LayoutHeight")));
            Assert.That(SrValidate(uniform), Is.Empty);
        }

        [Test]
        public void SharedRepack_FlowMapFromUnlistedShaderCannotEnterClosureWithVerifiedUser()
        {
            var world = BuildFlowSrWorld();
            world.AddTexture("textures\\sr_b2.dds", 1024, "BC1", true, 8);
            world.AddTexture("textures\\sr_n2.dds", 512, "BC3", false, 9);
            var unknownMaterial = SrMaterialPath("flow_unknown");
            var unknownXml = DownscaleMaterialXml(
                    (SrBase, "textures\\sr_b2.dds"),
                    (SrNormal, "textures\\sr_n2.dds"),
                    (SrFlowMap, SrFlowMapTexture))
                .Replace("shaders\\weighted2_character.xml.shader", "shaders/custom_flow_consumer.xml.shader");
            world.Fixture.AddText(unknownMaterial, unknownXml);
            world.AddModel(SrRigid2, SrWs2, SrRoot2,
                SrRigidFactory([(unknownMaterial, SrBlocks((8, 8)))]), [[unknownMaterial]]);

            var result = world.Run();

            Assert.That(SrPartDropReasons(result), Does.Contain(
                "the material's shader is not verified for flow-overlay repacking (shaders/custom_flow_consumer.xml.shader)"));
            var repacked = Get<HashSet<string>>(result, "RepackedRigids");
            Assert.That(repacked, Does.Not.Contain(SrRigid2));
            Assert.That(Get<int>(result, "FlowMapAtlases"), Is.Zero);
            Assert.That(world.Fixture.Output.Keys.Any(path => path.Contains("_repack_flow_map", StringComparison.Ordinal)),
                Is.EqualTo(repacked.Contains(SrRigid)));
            if (repacked.Contains(SrRigid))
            {
                var built = SrBuilt(result).Single();
                var outputs = Get<IEnumerable>(built, "Outputs").Cast<object>().ToList();
                var mapCopy = outputs.Single(output => Get<string>(output, "Slot") == SrFlowMap);
                Assert.That(Get<bool>(mapCopy, "IsCopy"), Is.True);
                Assert.That(Get<IEnumerable>(built, "Replaced").Cast<(string Path, bool Owned, long Bytes)>()
                    .Select(item => item.Path), Does.Not.Contain(SrFlowMapTexture));
                var generated = Encoding.UTF8.GetString(world.Fixture.Output[SrWsMaterial(world.State, SrWs, 0, 0)]);
                Assert.That(SrSlotNode(generated, SrFlowMap).OuterXml, Does.Contain("_repack_flow_map.dds"));
            }
            Assert.That(world.Fixture.Vanilla[SrFlowMapTexture], Is.Not.Empty);
            Assert.That(SrValidate(world), Is.Empty);
        }

        private const string SrFlowMapTexture2 = "textures\\sr_flow_map_2.dds";

        [Test]
        public void SharedRepack_DifferentLocalFlowMapsWithoutSignatureProfilesStayInSeparateGroups()
        {
            var world = BuildFlowSrWorld();
            world.AddTexture(SrFlowMapTexture2, 1024, "BC1", true, 12);
            var material2 = SrMaterialPath("flow2");
            world.Fixture.AddText(material2, SrFlowMaterialXml(SrFlowShader, SrFlowMapTexture2));
            var material1 = SrMaterialPath("flow1");
            world.AddModel(SrRigid, SrWs, SrRoot,
                SrRigidFactory([
                    (material1, SrBlocks((2, 3), (3, 3))),
                    (material2, SrBlocks((10, 4))),
                ]), [[material1, material2]]);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result) + SrPartDropReasons(result));
            var built = SrBuilt(result).Single();
            var mapOutputs = Get<IEnumerable>(built, "Outputs").Cast<object>()
                .Where(output => Get<string>(output, "Slot") == SrFlowMap).ToList();
            Assert.That(mapOutputs, Has.Count.EqualTo(2));
            Assert.That(mapOutputs.All(output => !Get<bool>(output, "IsCopy")), Is.True);
            Assert.That(mapOutputs.SelectMany(output => Get<IEnumerable>(output, "Sources").Cast<string>()),
                Is.EquivalentTo(new[] { SrFlowMapTexture, SrFlowMapTexture2 }));
            Assert.That(mapOutputs.All(output => Get<IEnumerable>(output, "Sources").Cast<string>().Count() == 1), Is.True);
            Assert.That(mapOutputs.Select(output => Get<string>(output, "Path"))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Has.Count.EqualTo(2));
            Assert.That(Get<int>(result, "FlowMapAtlases"), Is.EqualTo(2));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_FlowMapClosureCapRefusesOnlyFlowOwnersAndKeepsRegularPartBuildable()
        {
            var world = BuildFlowSrWorld();
            world.AddTexture("textures\\sr_flow_cap_regular_base.dds", 1024, "BC1", true, 10);
            world.AddTexture("textures\\sr_flow_cap_regular_normal.dds", 512, "BC3", false, 11);
            var regularMaterial = SrMaterialPath("flow_cap_regular");
            world.Fixture.AddMaterial(regularMaterial,
                (SrBase, "textures\\sr_flow_cap_regular_base.dds"),
                (SrNormal, "textures\\sr_flow_cap_regular_normal.dds"));
            var flowMaterial = SrMaterialPath("flow1");
            world.AddModel(SrRigid, SrWs, SrRoot,
                SrRigidFactory([
                    (flowMaterial, SrBlocks((2, 3), (3, 3))),
                    (regularMaterial, SrBlocks((10, 4))),
                ]), [[flowMaterial, regularMaterial]]);

            for (var index = 1; index < 17; index++)
            {
                var baseTexture = $"textures\\sr_flow_cap_{index}_base.dds";
                var normalTexture = $"textures\\sr_flow_cap_{index}_normal.dds";
                var material = SrMaterialPath($"flow_cap_{index}");
                world.AddTexture(baseTexture, 1024, "BC1", true, 20 + index);
                world.AddTexture(normalTexture, 512, "BC3", false, 40 + index);
                var xml = SrFlowMaterialXml(SrFlowShader, SrFlowMapTexture)
                    .Replace("textures\\sr_b1.dds", baseTexture)
                    .Replace("textures\\sr_n1.dds", normalTexture);
                world.Fixture.AddText(material, xml);
                var rigid = $"variantmeshes\\wh_main\\sr_flow_cap_{index}.rigid_model_v2";
                var ws = $"variantmeshes\\wh_main\\sr_flow_cap_{index}.wsmodel";
                var root = $"variantmeshes\\wh_variantmodels\\hu1\\emp\\sr_flow_cap_{index}.variantmeshdefinition";
                world.AddModel(rigid, ws, root,
                    SrRigidFactory([(material, SrBlocks((index % 16, (index * 3) % 16)))]), [[material]]);
            }

            var result = world.Run(minimumSaving: 1, minimumGroupSaving: 1);

            AssertFunnelReconciles(result);
            Assert.That(SrPartDropReasons(result), Does.Contain("the flow-map texture cannot be repacked"));
            Assert.That(SrPartDropReasons(result), Does.Contain("exceed the closure cap"));
            Assert.That(Get<int>(result, "Built"), Is.GreaterThan(0), SrSkips(result));
            Assert.That(Get<HashSet<string>>(result, "RepackedRigids"), Does.Contain(SrRigid));
            Assert.That(Get<int>(result, "FlowMapAtlases"), Is.Zero);
            Assert.That(SrBuilt(result).SelectMany(built => Get<IEnumerable>(built, "Outputs").Cast<object>())
                .Any(output => Get<string>(output, "Slot") == SrFlowMap), Is.False);
            Assert.That(SrBuilt(result).SelectMany(built => Get<IEnumerable>(built, "Replaced").Cast<(string Path, bool Owned, long Bytes)>())
                .Select(item => item.Path), Does.Not.Contain(SrFlowMapTexture));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [TestCase("flow_noise_speed", null, "float")]
        [TestCase("flow_noise_speed", "0.25", "float2")]
        [TestCase("flow_noise_tiling", "NaN", "float")]
        [TestCase("flow_noise_speed", "0.25,", "float")]
        [TestCase("flow_texture_tiling", "1", "float2")]
        [TestCase("flow_texture_tiling", "1,,2", "float2")]
        [TestCase("flow_texture_tiling", "1,Infinity", "float2")]
        public void SharedRepack_FlowOverlay_MissingMalformedNonfiniteOrWrongTypeParameterRefusesPart(
            string name, string? value, string type)
        {
            var world = BuildFlowSrWorld(
                omitParameter: value == null ? name : null,
                overrideParameter: value == null ? null : name,
                overrideValue: value,
                overrideType: type);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.Zero);
            Assert.That(SrPartDropReasons(result), Does.Contain("the material lacks a usable flow parameter " + name));
            Assert.That(Get<int>(result, "FlowGuidCopies"), Is.Zero);
            Assert.That(world.Fixture.Output.Keys.Any(path => path.Contains("_repack_")), Is.False);
        }

        [TestCase("shaders/rigid_flow_overlay.xml.shader", "the material's shader is not verified for flow-overlay repacking")]
        [TestCase("shaders/weighted2_emissive_flow_alpha.xml.shader", "the emissive-flow shader has a fixed flow-noise scroll speed")]
        public void SharedRepack_FlowOverlay_UnverifiedAndEmissiveFlowFamiliesRemainRefused(string shader, string reason)
        {
            var world = BuildFlowSrWorld(shader);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.Zero);
            Assert.That(SrPartDropReasons(result), Does.Contain(reason));
            if (shader.Contains("weighted2_emissive_flow_alpha", StringComparison.Ordinal))
                Assert.That(SrPartDropReasons(result), Does.Contain("flow_texture from UV0 without a compensatable tiling parameter"));
            Assert.That(Get<int>(result, "FlowGuidCopies"), Is.Zero);
            Assert.That(world.Fixture.Output.Keys.Any(path => path.Contains("_repack_")), Is.False);
        }

        [Test]
        public void SharedRepack_FlowParameterScale_IsFiniteTypedEqualAxisAndPreservesUnrelatedParameters()
        {
            var document = new XmlDocument();
            document.LoadXml(SrFlowMaterialXml(SrFlowShader, SrFlowMapTexture));
            var args = new object?[] { document, 0.5, 0.5, null };
            Assert.That((bool)GetStaticMethod("TryScaleSharedRepackFlowParameters").Invoke(null, args)!, Is.True);
            Assert.That(SrParamValue(document.OuterXml, "flow_noise_speed"), Is.EqualTo("0.125"));
            Assert.That(SrParamValue(document.OuterXml, "flow_noise_tiling"), Is.EqualTo("4"));
            Assert.That(SrParamValue(document.OuterXml, "flow_texture_tiling"), Is.EqualTo("3,1.5"));
            Assert.That(SrParamValue(document.OuterXml, "flow_distortion_stength"), Is.EqualTo("0.7"));

            foreach (var (value, type, scaleU, scaleV) in new[]
                     {
                         ("0.25", "float2", 0.5, 0.5),
                         ("bad", "float", 0.5, 0.5),
                         ("0.25,", "float", 0.5, 0.5),
                         ("0.25", "float", 0.5, 0.25),
                     })
            {
                var invalid = new XmlDocument();
                invalid.LoadXml(SrFlowMaterialXml(SrFlowShader, SrFlowMapTexture,
                    overrideParameter: "flow_noise_speed", overrideValue: value, overrideType: type));
                var before = invalid.OuterXml;
                args = [invalid, scaleU, scaleV, null];
                Assert.That((bool)GetStaticMethod("TryScaleSharedRepackFlowParameters").Invoke(null, args)!, Is.False);
                Assert.That(invalid.OuterXml, Is.EqualTo(before));
            }
        }

        [Test]
        public void SharedRepack_FlowCompensation_RestrictsAspectWithoutAnyCopySlot()
        {
            const string baseTexture = "textures\\flow_aspect_base.dds";
            const string flowMap = "textures\\flow_aspect_map.dds";
            var part = SrPart(new Dictionary<string, string> { [SrBase] = baseTexture, [SrFlowMap] = flowMap });
            Get<HashSet<string>>(part, "FlowCompensationMaterials").Add("materials\\flow.xml.material");
            var entries = new Dictionary<string, object>
            {
                [baseTexture] = SrEntry(baseTexture, 2048, 1024),
                [flowMap] = SrEntry(flowMap, 1024, 512),
            };

            var tuple = SrBuildTuples([part], entries, _ => null, out var violating).Single();

            Assert.That(violating, Is.Empty);
            Assert.That(Get<HashSet<string>>(tuple, "CopySlots"), Is.Empty);
            Assert.That(Get<bool>(tuple, "FlowCompensation"), Is.True);
            Assert.That(Get<bool>(tuple, "RequiresEqualAxisScale"), Is.True);
            Assert.That(Get<string>(tuple, "GroupKey"), Does.Contain("aspect:2x1"));
            var pages = SrPackGroup(tuple, 4096, 1, out var split);
            Assert.That(Get<bool>(split, "FlowAspectRestricted"), Is.True);
            Assert.That(Get<bool>(split, "AspectRestricted"), Is.True);
            var page = pages.Single();
            Assert.That((long)Get<int>(page, "LayoutWidth") * 1024,
                Is.EqualTo((long)Get<int>(page, "LayoutHeight") * 2048));
        }

        // ---- tuples and layout groups of a copy slot ----

        private static object SrLayout(int width, int height, string format = "BC1")
        {
            var mips = 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height)));
            var dds = BuildBcDds(width, height, mips, format);
            var arguments = new object?[] { dds, null };
            Assert.That((bool)GetStaticMethod("TryReadDdsLayout").Invoke(null, arguments)!, Is.True);
            return arguments[1]!;
        }

        private static object SrEntry(string path, int width, int height, string format = "BC1")
        {
            var entry = GetNestedType("VanillaCoverageTexture").GetConstructors(AnyInstance).Single().Invoke([path]);
            entry.GetType().GetProperty("Layout")!.SetValue(entry, SrLayout(width, height, format));
            return entry;
        }

        private static object SrPart(Dictionary<string, string> slots, params string[] copySlots)
        {
            var part = Activator.CreateInstance(GetNestedType("SharedRepackPart"))!;
            var type = part.GetType();
            type.GetProperty("Key")!.SetValue(part, CreateMeshKey(SrRigid, 0, 0));
            type.GetProperty("Model")!.SetValue(part, CreateUvPart(SrBlock(2, 3).Concat(SrBlock(3, 3)).ToArray()));
            var slotDictionary = Get<IDictionary>(part, "Slots");
            foreach (var (slot, texture) in slots)
                slotDictionary[slot] = texture;
            var copySet = Get<HashSet<string>>(part, "CopySlots");
            foreach (var slot in copySlots)
                copySet.Add(slot);
            Assert.That(GetStaticMethod("AnalyzeSharedRepackGeometry").Invoke(null, [part]), Is.Null);
            return part;
        }

        private static List<object> SrBuildTuples(
            List<object> parts,
            Dictionary<string, object> entries,
            Func<string, bool?> classify,
            out List<object> violating,
            HashSet<string>? copied = null)
        {
            var partList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(GetNestedType("SharedRepackPart")))!;
            foreach (var part in parts)
                partList.Add(part);
            var entryDictionary = (IDictionary)Activator.CreateInstance(
                typeof(Dictionary<,>).MakeGenericType(typeof(string), GetNestedType("VanillaCoverageTexture")),
                StringComparer.OrdinalIgnoreCase)!;
            foreach (var (path, entry) in entries)
                entryDictionary[path] = entry;
            var arguments = new object?[] { partList, entryDictionary, classify, (Func<string, string>)(texture => "set:" + texture), copied ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase), null };
            var tuples = ((IEnumerable)GetStaticMethod("BuildSharedRepackTuples").Invoke(null, arguments)!).Cast<object>().ToList();
            violating = ((IEnumerable)arguments[5]!).Cast<object>().ToList();
            return tuples;
        }

        [Test]
        public void SharedRepack_CopySlot_DoesNotRaiseTheAlignment_IsNeverUniform_AndKeysTheGroupByNoiseAndAspect()
        {
            const string baseTexture = "textures\\b.dds";
            const string noise = "textures\\commontextures\\noise.dds";
            var part = SrPart(
                new Dictionary<string, string> { [SrBase] = baseTexture, [SrDistortion] = noise }, SrDistortion);
            // 2048x1024 base, 256x256 noise: the noise is 8x / 4x smaller than the reference.
            var entries = new Dictionary<string, object>
            {
                [baseTexture] = SrEntry(baseTexture, 2048, 1024),
                [noise] = SrEntry(noise, 256, 256),
            };

            // A classifier that calls the noise uniform must not matter: a copy slot is never classified.
            var tuples = SrBuildTuples([part], entries, texture => texture == noise ? true : null, out var violating);

            Assert.That(violating, Is.Empty);
            var tuple = tuples.Single();
            Assert.That(Get<string?>(tuple, "Excluded"), Is.Null);
            Assert.That(Get<IDictionary>(tuple, "ConstantSlots").Count, Is.EqualTo(0));
            Assert.That(Get<IDictionary>(tuple, "Slots").Contains(SrDistortion), Is.True);
            Assert.That(Get<HashSet<string>>(tuple, "CopySlots"), Is.EquivalentTo(new[] { SrDistortion }));
            Assert.That(Get<bool>(tuple, "EmissiveCompensation"), Is.True);
            Assert.That(Get<IEnumerable>(tuple, "MemberTextures").Cast<string>(), Is.EqualTo(new[] { baseTexture }));
            // The noise decides the cell (whole noise texels: 4 << 3 = 32) but not the alignment (32 << 0, not 32 << 3).
            Assert.That(Get<int>(tuple, "Cell"), Is.EqualTo(32));
            Assert.That(Get<int>(tuple, "Align"), Is.EqualTo(32));
            var key = Get<string>(tuple, "GroupKey");
            Assert.That(key, Does.Contain($"{SrDistortion}:BC1:True:-3:-2:copy:{noise}"));
            Assert.That(key, Does.Contain("aspect:2x1"));
            Assert.That(key, Does.Contain($"{SrBase}:BC1:True:0:0:set:{baseTexture}"));

            // A tuple whose noise is a different texture, or whose reference has another aspect, is another group.
            const string otherNoise = "textures\\commontextures\\other.dds";
            entries[otherNoise] = SrEntry(otherNoise, 256, 256);
            var other = SrBuildTuples(
                [SrPart(new Dictionary<string, string> { [SrBase] = baseTexture, [SrDistortion] = otherNoise }, SrDistortion)],
                entries, _ => null, out _).Single();
            Assert.That(Get<string>(other, "GroupKey"), Is.Not.EqualTo(key));
            const string squareBase = "textures\\sq.dds";
            entries[squareBase] = SrEntry(squareBase, 1024, 1024);
            var square = SrBuildTuples(
                [SrPart(new Dictionary<string, string> { [SrBase] = squareBase, [SrDistortion] = noise }, SrDistortion)],
                entries, _ => null, out _).Single();
            Assert.That(Get<string>(square, "GroupKey"), Does.Contain("aspect:1x1"));
            Assert.That(Get<string>(square, "GroupKey"), Is.Not.EqualTo(key));

            // Without the copy slot there is no aspect component and the alignment is the same.
            var plain = SrBuildTuples(
                [SrPart(new Dictionary<string, string> { [SrBase] = baseTexture })], entries, _ => null, out _).Single();
            Assert.That(Get<string>(plain, "GroupKey"), Does.Not.Contain("aspect"));
            Assert.That(Get<int>(plain, "Align"), Is.EqualTo(Get<int>(tuple, "Align")));
            Assert.That(Get<int>(plain, "Cell"), Is.EqualTo(16));
        }

        [Test]
        public void SharedRepack_CopySlotOnly_IsNothingToRepack()
        {
            const string noise = "textures\\commontextures\\noise.dds";
            var part = SrPart(new Dictionary<string, string> { [SrDistortion] = noise }, SrDistortion);
            var entries = new Dictionary<string, object> { [noise] = SrEntry(noise, 512, 512) };

            var tuple = SrBuildTuples([part], entries, _ => null, out _).Single();

            Assert.That(Get<string?>(tuple, "Excluded"), Does.Contain("uniform"));
        }

        // A tuple with charts of the given sizes (reference pixels) and no parts.
        private static object SrChartTuple(int referenceWidth, int referenceHeight, bool copy, params (int Width, int Height)[] charts)
        {
            var tuple = Activator.CreateInstance(GetNestedType("SharedRepackTuple"))!;
            var type = tuple.GetType();
            type.GetProperty("Index")!.SetValue(tuple, 0);
            type.GetProperty("ReferenceWidth")!.SetValue(tuple, referenceWidth);
            type.GetProperty("ReferenceHeight")!.SetValue(tuple, referenceHeight);
            type.GetProperty("Cell")!.SetValue(tuple, 16);
            type.GetProperty("Gutter")!.SetValue(tuple, 16);
            type.GetProperty("Align")!.SetValue(tuple, 32);
            type.GetProperty("GroupKey")!.SetValue(tuple, "key");
            var slots = Get<IDictionary>(tuple, "Slots");
            var layouts = Get<IDictionary>(tuple, "Layouts");
            var exponents = Get<IDictionary>(tuple, "SourceExponent");
            slots[SrBase] = "textures\\b.dds";
            layouts[SrBase] = SrLayout(referenceWidth, referenceHeight);
            exponents[SrBase] = (0, 0);
            if (copy)
            {
                slots[SrDistortion] = "textures\\n.dds";
                layouts[SrDistortion] = SrLayout(256, 256);
                exponents[SrDistortion] = (
                    (int)Math.Log2(256) - (int)Math.Log2(referenceWidth), (int)Math.Log2(256) - (int)Math.Log2(referenceHeight));
                Get<HashSet<string>>(tuple, "CopySlots").Add(SrDistortion);
            }

            var chartType = GetNestedType("SharedRepackChart");
            var tupleCharts = Get<IList>(tuple, "Charts");
            foreach (var (width, height) in charts)
            {
                var chart = Activator.CreateInstance(chartType)!;
                chartType.GetProperty("Id")!.SetValue(chart, tupleCharts.Count);
                chartType.GetProperty("Tuple")!.SetValue(chart, tuple);
                chartType.GetProperty("Width")!.SetValue(chart, width);
                chartType.GetProperty("Height")!.SetValue(chart, height);
                tupleCharts.Add(chart);
            }

            return tuple;
        }

        private static List<object> SrPackGroup(object tuple, int maximumSide, long minimumGroupSaving, out object split)
            => SrPackGroup(tuple, maximumSide, minimumGroupSaving, disableReducedPadding: true, out split);

        private static List<object> SrPackGroup(
            object tuple,
            int maximumSide,
            long minimumGroupSaving,
            bool disableReducedPadding,
            out object split)
        {
            var tupleList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(GetNestedType("SharedRepackTuple")))!;
            tupleList.Add(tuple);
            var arguments = new object?[] { "variantmeshes\\a.rigid_model_v2", "id", tupleList, maximumSide, 0, minimumGroupSaving, null, null, null };
            var disabled = GetServiceType().GetField("SharedRepackReducedPaddingDisabled", BindingFlags.NonPublic | BindingFlags.Static)!;
            var previous = (bool)disabled.GetValue(null)!;
            disabled.SetValue(null, disableReducedPadding);
            List<object> pages;
            try
            {
                pages = ((IEnumerable)GetStaticMethod("PackSharedRepackGroup").Invoke(null, arguments)!).Cast<object>().ToList();
            }
            finally
            {
                disabled.SetValue(null, previous);
            }

            Assert.That(arguments[7], Is.Null, arguments[8] as string);
            split = arguments[6]!;
            return pages;
        }

        private static object SrPaddingBoxTuple(
            int referenceWidth,
            int referenceHeight,
            params (int X0, int Y0, int X1, int Y1)[] boxes)
        {
            var tuple = SrChartTuple(referenceWidth, referenceHeight, copy: false);
            Get<int[]>(tuple, "CellChart").AsSpan().Fill(-1);
            tuple.GetType().GetProperty("GridX")!.SetValue(tuple, referenceWidth / 16);
            tuple.GetType().GetProperty("GridY")!.SetValue(tuple, referenceHeight / 16);
            var chartMethod = GetStaticMethod("CreateSharedRepackChart");
            var charts = Get<IList>(tuple, "Charts");
            foreach (var box in boxes)
            {
                var chart = chartMethod.Invoke(null, [tuple, charts.Count, box, false, -1])!;
                charts.Add(chart);
            }

            return tuple;
        }

        private static List<object> SrPaddingCandidates(object tuple, int maximumVariantIndex)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(GetNestedType("SharedRepackTuple")))!;
            list.Add(tuple);
            return ((IEnumerable)GetStaticMethod("GetSharedRepackPaddingCandidates").Invoke(null, [list, maximumVariantIndex])!)
                .Cast<object>().ToList();
        }

        [Test]
        public void SharedRepack_ReducedPadding_ResizesBothLayoutsAndKeepsCellsAndSourcesInside()
        {
            var tuple = SrPaddingBoxTuple(2048, 2048, (20, 20, 28, 28), (50, 50, 58, 58));
            var resize = GetStaticMethod("ResizeSharedRepackCharts");
            var charts = Get<IList>(tuple, "Charts").Cast<object>().ToList();
            var initial = charts.Select(chart => (
                Get<int>(chart, "X0"), Get<int>(chart, "Y0"), Get<int>(chart, "Width"), Get<int>(chart, "Height"))).ToArray();

            resize.Invoke(null, [tuple, 16, 32]);
            Assert.That(
                charts.Select(chart => (Get<int>(chart, "X0"), Get<int>(chart, "Y0"), Get<int>(chart, "Width"), Get<int>(chart, "Height"))),
                Is.EqualTo(initial), "P0 reproduces the original chart rectangles");

            foreach (var (gutter, align) in new[] { (16, 16), (8, 8) })
            {
                resize.Invoke(null, [tuple, gutter, align]);
                foreach (var chart in charts)
                {
                    Assert.That(Get<int>(chart, "LayoutWidth") % align, Is.Zero);
                    Assert.That(Get<int>(chart, "LayoutHeight") % align, Is.Zero);
                    Assert.That(Get<int>(chart, "X0"), Is.LessThanOrEqualTo(Get<int>(chart, "CellX0") * 16));
                    Assert.That(Get<int>(chart, "Y0"), Is.LessThanOrEqualTo(Get<int>(chart, "CellY0") * 16));
                    Assert.That(
                        Get<int>(chart, "X0") + Get<int>(chart, "Width"),
                        Is.GreaterThanOrEqualTo((Get<int>(chart, "CellX1") + 1) * 16));
                    Assert.That(
                        Get<int>(chart, "Y0") + Get<int>(chart, "Height"),
                        Is.GreaterThanOrEqualTo((Get<int>(chart, "CellY1") + 1) * 16));
                    Assert.That(Get<int>(chart, "LayoutWidth"), Is.LessThanOrEqualTo(2048));
                    Assert.That(Get<int>(chart, "LayoutHeight"), Is.LessThanOrEqualTo(2048));
                }
            }

            for (var left = 0; left < charts.Count; left++)
            {
                for (var right = left + 1; right < charts.Count; right++)
                {
                    var overlaps = Get<int>(charts[left], "X0") < Get<int>(charts[right], "X0") + Get<int>(charts[right], "Width") &&
                                   Get<int>(charts[right], "X0") < Get<int>(charts[left], "X0") + Get<int>(charts[left], "Width") &&
                                   Get<int>(charts[left], "Y0") < Get<int>(charts[right], "Y0") + Get<int>(charts[right], "Height") &&
                                   Get<int>(charts[right], "Y0") < Get<int>(charts[left], "Y0") + Get<int>(charts[left], "Height");
                    Assert.That(overlaps, Is.False, $"source charts {left} and {right} overlap");
                }
            }

            var tight = SrTightTuple(SrDiagonalQuads());
            var merged = Get<IList>(tight, "MergedCharts").Cast<object>().ToList();
            var pieces = Get<IList>(tight, "TightCharts").Cast<object>().ToList();
            resize.Invoke(null, [tight, 8, 8]);
            Assert.That(Get<int>(merged[0], "Width"), Is.LessThan(352));
            Assert.That(pieces.All(piece => Get<int>(piece, "Width") < 192), Is.True);
        }

        [Test]
        public void SharedRepack_ReducedPadding_SelectsP2ForASmallerPageAndIsDeterministic()
        {
            object MakeTuple() => SrPaddingBoxTuple(
                1024, 1024,
                (2, 2, 2, 2), (8, 2, 8, 2), (2, 8, 2, 8), (8, 8, 8, 8));

            var pages = SrPackGroup(MakeTuple(), 4096, 1L << 60, disableReducedPadding: false, out var split);
            var page = pages.Single();
            Assert.That(Get<string>(split, "PaddingVariant"), Is.EqualTo("gutter 8 + align 8"));
            Assert.That((Get<int>(page, "LayoutWidth"), Get<int>(page, "LayoutHeight")), Is.EqualTo((64, 64)));
            Assert.That(Get<long>(split, "PaddingSavedBytes"), Is.GreaterThan(0));

            var repeat = SrPackGroup(MakeTuple(), 4096, 1L << 60, disableReducedPadding: false, out var repeatSplit).Single();
            Assert.That(Get<string>(repeatSplit, "PaddingVariant"), Is.EqualTo(Get<string>(split, "PaddingVariant")));
            Assert.That(Get<long>(repeat, "BytesAfter"), Is.EqualTo(Get<long>(page, "BytesAfter")));
            Assert.That(
                Get<IEnumerable>(repeat, "Charts").Cast<object>().Select(chart =>
                    (Get<int>(chart, "PackX"), Get<int>(chart, "PackY"), Get<int>(chart, "LayoutWidth"), Get<int>(chart, "LayoutHeight"))),
                Is.EqualTo(Get<IEnumerable>(page, "Charts").Cast<object>().Select(chart =>
                    (Get<int>(chart, "PackX"), Get<int>(chart, "PackY"), Get<int>(chart, "LayoutWidth"), Get<int>(chart, "LayoutHeight")))));
        }

        [Test]
        public void SharedRepack_ReducedPadding_EvaluatesUniqueVariantsFromP2ToP0AndMapsDuplicatesToSafer()
        {
            var tuple = SrPaddingBoxTuple(1024, 1024, (10, 10, 10, 10));
            Assert.That(SrPaddingCandidates(tuple, 2).Select(variant => Get<string>(variant, "Name")).ToList(),
                Is.EqualTo(new[] { "gutter 8 + align 8", "align 16", "baseline" }));

            tuple.GetType().GetProperty("Align")!.SetValue(tuple, 16);
            var deduplicated = SrPaddingCandidates(tuple, 2);
            Assert.That(deduplicated.Select(variant => Get<string>(variant, "Name")).ToList(),
                Is.EqualTo(new[] { "gutter 8 + align 8", "baseline" }));

            var full = SrPaddingBoxTuple(256, 256, (0, 0, 15, 15));
            Assert.That(SrPaddingCandidates(full, 2).Select(variant => Get<string>(variant, "Name")).ToList(),
                Is.EqualTo(new[] { "gutter 8 + align 8", "align 16", "baseline" }));
            var tied = SrPackGroup(full, 4096, 1L << 60, disableReducedPadding: false, out var split).Single();
            Assert.That(Get<string>(split, "PaddingVariant"), Is.EqualTo("baseline"));
            Assert.That(Get<long>(split, "PaddingSavedBytes"), Is.Zero);
            Assert.That(Get<long>(tied, "BytesAfter"), Is.GreaterThan(0));
        }

        [Test]
        public void SharedRepack_ReducedPadding_NoGainTiesStayAtP0AndKillSwitchRestoresP0()
        {
            var full = SrPaddingBoxTuple(256, 256, (0, 0, 15, 15));
            var baseline = SrPackGroup(full, 4096, 1L << 60, disableReducedPadding: true, out _).Single();
            var tied = SrPackGroup(
                SrPaddingBoxTuple(256, 256, (0, 0, 15, 15)),
                4096, 1L << 60, disableReducedPadding: false, out var tieSplit).Single();
            Assert.That(Get<string>(tieSplit, "PaddingVariant"), Is.EqualTo("baseline"));
            Assert.That(Get<long>(tied, "BytesAfter"), Is.EqualTo(Get<long>(baseline, "BytesAfter")));
            Assert.That(
                Get<IEnumerable>(tied, "Charts").Cast<object>().Select(chart =>
                    (Get<int>(chart, "PackX"), Get<int>(chart, "PackY"), Get<int>(chart, "LayoutWidth"), Get<int>(chart, "LayoutHeight"))),
                Is.EqualTo(Get<IEnumerable>(baseline, "Charts").Cast<object>().Select(chart =>
                    (Get<int>(chart, "PackX"), Get<int>(chart, "PackY"), Get<int>(chart, "LayoutWidth"), Get<int>(chart, "LayoutHeight")))));

            var reduced = SrPackGroup(
                SrPaddingBoxTuple(1024, 1024, (10, 10, 10, 10)),
                4096, 1L << 60, disableReducedPadding: false, out var reducedSplit).Single();
            var disabled = SrPackGroup(
                SrPaddingBoxTuple(1024, 1024, (10, 10, 10, 10)),
                4096, 1L << 60, disableReducedPadding: true, out var disabledSplit).Single();
            Assert.That(Get<string>(disabledSplit, "PaddingVariant"), Is.EqualTo("baseline"));
            Assert.That(Get<string>(reducedSplit, "PaddingVariant"), Is.EqualTo("gutter 8 + align 8"));
            Assert.That(Get<long>(reduced, "BytesAfter"), Is.LessThan(Get<long>(disabled, "BytesAfter")));
        }

        [Test]
        public void SharedRepack_ReducedPadding_UsesThreeThenTwoExactBlockLevels()
        {
            const string texture = "textures\\padding_levels.dds";
            const int size = 512;
            var dds = BuildBcDds(size, size, 10, "BC1");
            var tuple = SrPaddingBoxTuple(size, size, (10, 10, 10, 10));
            Get<IDictionary>(tuple, "Slots")[SrBase] = texture;
            var layout = SrLayout(size, size);
            Get<IDictionary>(tuple, "Layouts")[SrBase] = layout;

            var page = Activator.CreateInstance(GetNestedType("SharedRepackPage"))!;
            Get<IList>(page, "Tuples").Add(tuple);
            var output = Activator.CreateInstance(GetNestedType("SharedRepackSlotOutput"))!;
            output.GetType().GetProperty("Slot")!.SetValue(output, SrBase);
            output.GetType().GetProperty("Format")!.SetValue(output, Get<string>(layout, "Format"));
            output.GetType().GetProperty("Srgb")!.SetValue(output, Get<bool>(layout, "IsSrgb"));
            output.GetType().GetProperty("MipLevels")!.SetValue(output, 10);

            var cacheType = GetNestedType("SharedRepackSourceCache");
            var read = (Func<string, byte[]?>)(_ => dds);
            var cache = cacheType.GetConstructors(AnyInstance).Single().Invoke([read]);
            cacheType.GetMethod("FromBytes", AnyInstance)!.Invoke(cache, [texture, dds]);
            var blockLevels = GetStaticMethod("GetSharedRepackBlockLevels");
            var exactAtLevel = GetStaticMethod("IsSharedRepackBlockCopiedAtLevel");

            Get<IDictionary>(tuple, "SourceExponent")[SrBase] = (0, 0);
            Get<int[]>(tuple, "CellChart").AsSpan().Fill(-1);
            tuple.GetType().GetProperty("Align")!.SetValue(tuple, 16);
            var p1 = (int)blockLevels.Invoke(null, [page, output, cache])!;
            Assert.That(p1, Is.EqualTo(3));
            output.GetType().GetProperty("BlockCopiedLevels")!.SetValue(output, p1);
            Assert.That(Enumerable.Range(0, 5).Select(level => (bool)exactAtLevel.Invoke(null, [output, level])!),
                Is.EqualTo(new[] { true, true, true, false, false }));

            tuple.GetType().GetProperty("Align")!.SetValue(tuple, 8);
            var p2 = (int)blockLevels.Invoke(null, [page, output, cache])!;
            Assert.That(p2, Is.EqualTo(2));
            output.GetType().GetProperty("BlockCopiedLevels")!.SetValue(output, p2);
            Assert.That(Enumerable.Range(0, 5).Select(level => (bool)exactAtLevel.Invoke(null, [output, level])!),
                Is.EqualTo(new[] { true, true, false, false, false }));
        }

        [Test]
        public void SharedRepack_Group_PacksWithMaxRectsWhenTheRowsNeedALargerCanvas()
        {
            // Row packing needs 512x256 for these; MaxRects fits them on 256x256.
            var tuple = SrChartTuple(1024, 1024, copy: false, (64, 160), (224, 96), (32, 192));

            var pages = SrPackGroup(tuple, 4096, 1L << 60, out _);

            var page = pages.Single();
            var width = Get<int>(page, "LayoutWidth");
            var height = Get<int>(page, "LayoutHeight");
            Assert.That((width, height), Is.EqualTo((256, 256)));
            var charts = Get<IEnumerable>(page, "Charts").Cast<object>().ToList();
            Assert.That(charts, Has.Count.EqualTo(3));
            foreach (var chart in charts)
            {
                Assert.That(Get<int>(chart, "PackX") % 32, Is.Zero);
                Assert.That(Get<int>(chart, "PackY") % 32, Is.Zero);
                Assert.That(Get<int>(chart, "PackX") + Get<int>(chart, "LayoutWidth"), Is.LessThanOrEqualTo(width));
                Assert.That(Get<int>(chart, "PackY") + Get<int>(chart, "LayoutHeight"), Is.LessThanOrEqualTo(height));
            }

            for (var left = 0; left < charts.Count; left++)
            {
                for (var right = left + 1; right < charts.Count; right++)
                {
                    int Lx(object c) => Get<int>(c, "PackX");
                    int Ly(object c) => Get<int>(c, "PackY");
                    int Lw(object c) => Get<int>(c, "LayoutWidth");
                    int Lh(object c) => Get<int>(c, "LayoutHeight");
                    var overlaps = Lx(charts[left]) < Lx(charts[right]) + Lw(charts[right]) &&
                                   Lx(charts[right]) < Lx(charts[left]) + Lw(charts[left]) &&
                                   Ly(charts[left]) < Ly(charts[right]) + Lh(charts[right]) &&
                                   Ly(charts[right]) < Ly(charts[left]) + Lh(charts[left]);
                    Assert.That(overlaps, Is.False, $"{left} overlaps {right}");
                }
            }

            // Same input again: the same positions.
            var again = SrPackGroup(SrChartTuple(1024, 1024, copy: false, (64, 160), (224, 96), (32, 192)), 4096, 1L << 60, out _)
                .Single();
            Assert.That(
                Get<IEnumerable>(again, "Charts").Cast<object>().Select(c => (Get<int>(c, "PackX"), Get<int>(c, "PackY"))),
                Is.EqualTo(charts.Select(c => (Get<int>(c, "PackX"), Get<int>(c, "PackY")))));
        }

        [Test]
        public void SharedRepack_CopySlotGroup_RestrictsPageCanvasesToTheReferenceAspect()
        {
            // A 2048x1024 reference (aspect 2:1) with charts that an unrestricted packer fits on a 512x512
            // canvas; the smallest 2:1 canvas that holds them is 1024x512.
            var tuple = SrChartTuple(2048, 1024, copy: true, (512, 128), (256, 256));

            var pages = SrPackGroup(tuple, 4096, 1, out var split);

            Assert.That(Get<bool>(split, "AspectRestricted"), Is.True);
            Assert.That(Get<string>(split, "SingleSize"), Is.EqualTo("1024x512"));
            Assert.That(Get<string>(split, "UnrestrictedSingleSize"), Is.EqualTo("512x512"));
            Assert.That(pages, Is.Not.Empty);
            foreach (var page in pages)
            {
                var width = Get<int>(page, "LayoutWidth");
                var height = Get<int>(page, "LayoutHeight");
                Assert.That((long)width * 1024, Is.EqualTo((long)height * 2048), $"{width}x{height}");
                // scaleU == scaleV for every page, which is what the exact compensation needs.
                Assert.That(2048.0 / width, Is.EqualTo(1024.0 / height));
            }

            // The restriction costs bytes against the unrestricted canvas, and is reported.
            Assert.That(Get<long>(split, "UnrestrictedSingleBytes"), Is.GreaterThan(0));
            Assert.That(Get<long>(split, "SingleBytes"), Is.GreaterThanOrEqualTo(Get<long>(split, "UnrestrictedSingleBytes")));
            var copy = Get<IEnumerable>(pages[0], "Outputs").Cast<object>().Single(output => Get<bool>(output, "IsCopy"));
            Assert.That(Get<string>(copy, "Path"), Does.EndWith("_repack_emissive_distortion.dds"));
            Assert.That(Get<long>(pages[0], "BytesAfter"), Is.EqualTo(
                Get<IEnumerable>(pages[0], "Outputs").Cast<object>().Sum(output => Get<long>(output, "Bytes"))));
        }

        [Test]
        public void SharedRepack_GroupWithoutCopySlot_KeepsTheFreeCanvasChoice()
        {
            var tuple = SrChartTuple(2048, 1024, copy: false, (512, 128), (256, 256));

            var pages = SrPackGroup(tuple, 4096, 1, out var split);

            Assert.That(Get<bool>(split, "AspectRestricted"), Is.False);
            Assert.That(Get<string>(split, "SingleSize"), Is.EqualTo("512x512"), "the free choice is not 2:1");
            Assert.That(Get<long>(split, "UnrestrictedSingleBytes"), Is.EqualTo(0));
            Assert.That(pages, Is.Not.Empty);
        }

        // --- Tight pieces ------------------------------------------------------------------------
        //
        // Two quads on a 2048 texture (cell 16, alignment 32) whose cell boxes are two cells apart, so
        // they are separate 8-connected components whose padded boxes overlap: one merged chart, two
        // tight pieces. DiagonalQuads: both axes (the merged box is mostly empty); SideBySide: one row
        // (the pieces would be larger together than the chart).

        private const string SrTightTexture = "textures\\sr_big.dds";

        private static (double U0, double V0, double U1, double V1)[] SrDiagonalQuads()
            => [(20.2 / 128, 20.2 / 128, 28.8 / 128, 28.8 / 128), (30.2 / 128, 30.2 / 128, 38.8 / 128, 38.8 / 128)];

        private static (double U0, double V0, double U1, double V1)[] SrSideBySideQuads()
            => [(20.2 / 128, 20.2 / 128, 28.8 / 128, 28.8 / 128), (30.2 / 128, 20.2 / 128, 38.8 / 128, 28.8 / 128)];

        // Quads with shared vertices (4 vertices, 6 indices each), as real meshes have.
        private static RmvModel CreateSharedVertexQuads((double U0, double V0, double U1, double V1)[] rects)
        {
            var vertices = new List<CommonVertex>();
            var indices = new List<ushort>();
            foreach (var (u0, v0, u1, v1) in rects)
            {
                var first = (ushort)vertices.Count;
                foreach (var (u, v) in new[] { (u0, v0), (u1, v0), (u1, v1), (u0, v1) })
                    vertices.Add(new CommonVertex { Position = new Vector4((float)u, (float)v, vertices.Count, 1), Uv = new Vector2((float)u, (float)v) });
                indices.AddRange([first, (ushort)(first + 1), (ushort)(first + 2), first, (ushort)(first + 2), (ushort)(first + 3)]);
            }

            return new RmvModel
            {
                Mesh = new RmvMesh { VertexList = vertices.ToArray(), IndexList = indices.ToArray() },
            };
        }

        private static object SrTightPart((double U0, double V0, double U1, double V1)[] rects)
        {
            var part = Activator.CreateInstance(GetNestedType("SharedRepackPart"))!;
            var type = part.GetType();
            type.GetProperty("Key")!.SetValue(part, CreateMeshKey(SrRigid, 0, 0));
            type.GetProperty("Model")!.SetValue(part, CreateSharedVertexQuads(rects));
            Get<IDictionary>(part, "Slots")[SrBase] = SrTightTexture;
            Assert.That(GetStaticMethod("AnalyzeSharedRepackGeometry").Invoke(null, [part]), Is.Null);
            return part;
        }

        private static object SrTightTuple((double U0, double V0, double U1, double V1)[] rects)
        {
            var entries = new Dictionary<string, object> { [SrTightTexture] = SrEntry(SrTightTexture, 2048, 2048) };
            var tuple = SrBuildTuples([SrTightPart(rects)], entries, _ => null, out var violating).Single();
            Assert.That(violating, Is.Empty);
            Assert.That(Get<string?>(tuple, "Excluded"), Is.Null);
            return tuple;
        }

        private static void WithTightPiecesDisabled(Action test)
        {
            var field = GetServiceType().GetField("SharedRepackTightPiecesDisabled", BindingFlags.NonPublic | BindingFlags.Static)!;
            field.SetValue(null, true);
            try
            {
                test();
            }
            finally
            {
                field.SetValue(null, false);
            }
        }

        // The kill switch of the copies of textures that left a plan: the closure cascade of earlier versions.
        private static void WithCopyRemovedDisabled(Action test)
        {
            var field = GetServiceType().GetField("SharedRepackCopyRemovedDisabled", BindingFlags.NonPublic | BindingFlags.Static)!;
            var previous = (bool)field.GetValue(null)!;
            field.SetValue(null, true);
            try
            {
                test();
            }
            finally
            {
                field.SetValue(null, previous);
            }
        }

        private static void WithReducedPaddingDisabled(Action test)
        {
            var field = GetServiceType().GetField("SharedRepackReducedPaddingDisabled", BindingFlags.NonPublic | BindingFlags.Static)!;
            var previous = (bool)field.GetValue(null)!;
            field.SetValue(null, true);
            try
            {
                test();
            }
            finally
            {
                field.SetValue(null, previous);
            }
        }

        private static (SrWorld World, Func<RmvFile> Rigid) BuildTightSrWorld(
            (double U0, double V0, double U1, double V1)[] rects, int lods = 1)
        {
            var world = new SrWorld();
            world.AddTexture(SrTightTexture, 2048, "BC1", true, 1);
            world.Fixture.AddMaterial(SrMaterialPath("mbig"), (SrBase, SrTightTexture));
            var mbig = SrMaterialPath("mbig");
            var triangles = rects.SelectMany(rect => UvQuad(rect.U0, rect.V0, rect.U1, rect.V1)).ToArray();
            var part = new[] { (mbig, triangles) };
            var factory = lods == 1 ? SrRigidFactory(part) : SrRigidFactory(part, part);
            world.AddModel(SrRigid, SrWs, SrRoot, factory, lods == 1 ? [[mbig]] : [[mbig], [mbig]]);
            return (world, factory);
        }

        [Test]
        public void SharedRepack_ReducedPadding_OpensTheGroupGateAndReportsNewlyBuiltGroup()
        {
            var small = new[] { (20.2 / 128, 20.2 / 128, 20.8 / 128, 20.8 / 128) };
            var baselineWorld = BuildTightSrWorld(small).World;
            object baselineResult = null!;
            WithReducedPaddingDisabled(() => baselineResult = baselineWorld.Run(minimumSaving: 1, minimumGroupSaving: 1));
            var baselineBuilt = SrBuilt(baselineResult).Single();
            var baselineSaved = Get<long>(baselineBuilt, "Before") - Get<long>(baselineBuilt, "After");

            var probeWorld = BuildTightSrWorld(small).World;
            var probeResult = probeWorld.Run(minimumSaving: 1, minimumGroupSaving: 1);
            var probeBuilt = SrBuilt(probeResult).Single();
            var reducedSaved = Get<long>(probeBuilt, "Before") - Get<long>(probeBuilt, "After");
            Assert.That(reducedSaved, Is.GreaterThan(baselineSaved));
            Assert.That(BuildSharedRepackSection(Enabled(probeWorld)), Does.Contain("padding: gutter 8 + align 8"));

            var gatedWorld = BuildTightSrWorld(small).World;
            var result = gatedWorld.Run(minimumSaving: 1, minimumGroupSaving: baselineSaved + 1);

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            var report = BuildSharedRepackSection(Enabled(gatedWorld));
            Assert.That(report, Does.Contain("[built only after reduced padding]"));
            Assert.That(report, Does.Contain("newly built group(s)"));
        }

        [Test]
        public void SharedRepack_PixelRetry_RestoresAttemptReportStateAndCountsOnlyTheFinalPlan()
        {
            var small = new[] { (20.2 / 128, 20.2 / 128, 20.8 / 128, 20.8 / 128) };
            var baselineWorld = BuildTightSrWorld(small).World;
            var baseline = baselineWorld.Run(minimumSaving: 1, minimumGroupSaving: 1);

            var retriedWorld = BuildTightSrWorld(small).World;
            var hookCalls = 0;
            object retried = null!;
            WithBeforeCheckHook(
                plan =>
                {
                    if (hookCalls++ != 0)
                        return;

                    var chart = Get<IEnumerable>(Get<IEnumerable>(plan, "Pages").Cast<object>().First(), "Charts")
                        .Cast<object>().First();
                    var packX = chart.GetType().GetProperty("PackX", AnyInstance)!;
                    packX.SetValue(chart, (int)packX.GetValue(chart)! + 16);
                },
                () => retried = retriedWorld.Run(minimumSaving: 1, minimumGroupSaving: 1));

            Assert.That(hookCalls, Is.GreaterThanOrEqualTo(2), "the first pixel check failed and retried");
            Assert.That(Get<int>(retried, "PaddingPixelRetryAttempts"), Is.EqualTo(1));
            Assert.That(Get<int>(retried, "FailedPixelCheck"), Is.Zero, SrSkips(retried));
            Assert.That(Get<int>(retried, "Built"), Is.EqualTo(1), SrSkips(retried));
            Assert.That(Get<IEnumerable>(retried, "PixelFailures").Cast<object>(), Is.Empty);

            foreach (var name in new[] { "AtlasedUsersIgnored", "SizingErrors" })
                Assert.That(Get<int>(retried, name), Is.EqualTo(Get<int>(baseline, name)), name);
            foreach (var name in new[] { "PartDropReasons", "EmissiveAspectLostGroupKeys", "EmissiveAspectLostTupleKeys" })
                Assert.That(Get<IEnumerable>(retried, name).Cast<object>().ToArray(),
                    Is.EqualTo(Get<IEnumerable>(baseline, name).Cast<object>().ToArray()), name);
            foreach (var name in new[] { "ClosureNotes", "SizingLog" })
                Assert.That(Get<IEnumerable>(retried, name).Cast<object>().ToArray(),
                    Is.EqualTo(Get<IEnumerable>(baseline, name).Cast<object>().ToArray()), name);
            var retriedContext = Get<IEnumerable>(retried, "Outcomes").Cast<object>().Single();
            var baselineContext = Get<IEnumerable>(baseline, "Outcomes").Cast<object>().Single();
            Assert.That(Get<IEnumerable>(Get<object>(retriedContext, "Context"), "GroupWaste").Cast<object>().Count(),
                Is.EqualTo(Get<IEnumerable>(Get<object>(baselineContext, "Context"), "GroupWaste").Cast<object>().Count()));
            var report = BuildSharedRepackSection(Enabled(retriedWorld));
            Assert.That(report, Does.Contain("Pixel-check padding retries: 1 retry pass"));
            Assert.That(report, Does.Contain("Groups lowered by pixel check: 1 (1 emitted, 0 gated/untouched)"));
        }

        [Test]
        public void SharedRepack_PixelRetryReportSnapshot_RestoresEveryAttemptMutation()
        {
            var resultType = GetNestedType("SharedTextureRepackResult");
            var result = Activator.CreateInstance(resultType)!;
            resultType.GetProperty("AtlasedUsersIgnored", AnyInstance)!.SetValue(result, 3);
            resultType.GetProperty("SizingErrors", AnyInstance)!.SetValue(result, 2);
            resultType.GetProperty("PackingSimTicks", AnyInstance)!.SetValue(result, 17L);
            Get<IDictionary>(result, "PartDropReasons")["reason"] = 4;
            Get<IList>(result, "ClosureNotes").Add(ValueTuple.Create("label", "note"));
            Get<IList>(result, "SizingLog").Add(ValueTuple.Create("label", "detail"));
            Get<IList>(result, "PixelFailures").Add(ValueTuple.Create("label", "pixel"));
            Get<HashSet<string>>(result, "EmissiveAspectLostGroupKeys").Add("group");
            Get<HashSet<string>>(result, "EmissiveAspectLostTupleKeys").Add("tuple");

            var snapshotType = GetNestedType("SharedRepackAttemptReportSnapshot");
            var snapshot = snapshotType.GetMethod("Capture", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, [result])!;
            resultType.GetProperty("AtlasedUsersIgnored", AnyInstance)!.SetValue(result, 8);
            resultType.GetProperty("SizingErrors", AnyInstance)!.SetValue(result, 6);
            resultType.GetProperty("PackingSimTicks", AnyInstance)!.SetValue(result, 99L);
            Get<IDictionary>(result, "PartDropReasons")["reason"] = 9;
            Get<IList>(result, "ClosureNotes").Add(ValueTuple.Create("again", "note"));
            Get<IList>(result, "SizingLog").Add(ValueTuple.Create("again", "detail"));
            Get<IList>(result, "PixelFailures").Add(ValueTuple.Create("again", "pixel"));
            Get<HashSet<string>>(result, "EmissiveAspectLostGroupKeys").Add("again");
            Get<HashSet<string>>(result, "EmissiveAspectLostTupleKeys").Add("again");

            snapshotType.GetMethod("Restore", AnyInstance)!.Invoke(snapshot, [result]);
            Assert.That(Get<int>(result, "AtlasedUsersIgnored"), Is.EqualTo(3));
            Assert.That(Get<int>(result, "SizingErrors"), Is.EqualTo(2));
            Assert.That(Get<long>(result, "PackingSimTicks"), Is.EqualTo(17));
            Assert.That(Get<IDictionary>(result, "PartDropReasons")["reason"], Is.EqualTo(4));
            Assert.That(Get<IEnumerable>(result, "ClosureNotes").Cast<object>().Count(), Is.EqualTo(1));
            Assert.That(Get<IEnumerable>(result, "SizingLog").Cast<object>().Count(), Is.EqualTo(1));
            Assert.That(Get<IEnumerable>(result, "PixelFailures").Cast<object>().Count(), Is.EqualTo(1));
            Assert.That(Get<HashSet<string>>(result, "EmissiveAspectLostGroupKeys"), Is.EquivalentTo(new[] { "group" }));
            Assert.That(Get<HashSet<string>>(result, "EmissiveAspectLostTupleKeys"), Is.EquivalentTo(new[] { "tuple" }));
        }

        [Test]
        public void SharedRepack_TightPieces_EachComponentBecomesItsOwnPiece_WithTheChartsGutterAndAlignment()
        {
            var tuple = SrTightTuple(SrDiagonalQuads());

            var charts = Get<IList>(tuple, "Charts").Cast<object>().ToList();
            var pieces = Get<IList>(tuple, "TightCharts").Cast<object>().ToList();

            // The merged chart: cells 20..38 plus one cell, snapped to 32 pixels.
            Assert.That(charts, Has.Count.EqualTo(1));
            Assert.That(Get<bool>(charts[0], "Piece"), Is.False);
            Assert.That(
                (Get<int>(charts[0], "X0"), Get<int>(charts[0], "Y0"), Get<int>(charts[0], "Width"), Get<int>(charts[0], "Height")),
                Is.EqualTo((288, 288, 352, 352)));
            // Pieces: cells 20..28 and 30..38, each padded by one cell and snapped to 32 pixels (the
            // rectangles overlap in source space: 448..480).
            Assert.That(pieces, Has.Count.EqualTo(2));
            Assert.That(
                pieces.Select(piece => (Get<int>(piece, "X0"), Get<int>(piece, "Y0"), Get<int>(piece, "Width"), Get<int>(piece, "Height"))),
                Is.EqualTo(new[] { (288, 288, 192, 192), (448, 448, 192, 192) }));
            Assert.That(pieces.Select(piece => Get<bool>(piece, "Piece")), Is.All.True);
            Assert.That(pieces.Select(piece => Get<int>(piece, "MergedId")), Is.All.EqualTo(0));
            Assert.That(pieces.Select(piece => Get<int>(piece, "Id")), Is.EqualTo(new[] { 0, 1 }));
            // The cells: the piece owns exactly its component, the chart owns both.
            var cellPiece = Get<int[]>(tuple, "TightCellChart");
            var cellChart = Get<int[]>(tuple, "MergedCellChart");
            Assert.That(cellPiece.Count(piece => piece >= 0), Is.EqualTo(cellChart.Count(chart => chart >= 0)));
            Assert.That(cellPiece.Count(piece => piece == 0), Is.LessThan(cellChart.Count(chart => chart == 0)));
            Assert.That(cellPiece[25 * 128 + 25], Is.EqualTo(0));
            Assert.That(cellPiece[35 * 128 + 35], Is.EqualTo(1));
            Assert.That(cellPiece[25 * 128 + 35], Is.EqualTo(-1));
            Assert.That(cellChart[25 * 128 + 35], Is.EqualTo(-1));
            Assert.That(Get<string?>(tuple, "TightInconsistent"), Is.Null);
            // The active layout is still the merged one until a group chooses.
            Assert.That(Get<bool>(tuple, "Tight"), Is.False);

            // No merged component, no alternative: one separate quad.
            var plain = SrTightTuple([SrDiagonalQuads()[0]]);
            Assert.That(Get<object?>(plain, "TightCharts"), Is.Null);
        }

        [Test]
        public void SharedRepack_TightPieces_EveryTriangleBelongsToExactlyOnePiece_AndSharedVerticesStayInOne()
        {
            var tuple = SrTightTuple(SrDiagonalQuads());
            var part = Get<IList>(tuple, "Parts").Cast<object>().Single();
            var model = Get<RmvModel>(part, "Model");
            var pieces = Get<IList>(tuple, "TightCharts").Cast<object>().ToList();
            var vertexPiece = Get<int[]>(part, "TightVertexChart");
            var vertexChart = Get<int[]>(part, "MergedVertexChart");
            var indices = model.Mesh.IndexList;
            var trianglesOfPiece = new int[pieces.Count];

            Assert.That(indices.Length / 3, Is.EqualTo(4));
            for (var index = 0; index < indices.Length; index += 3)
            {
                var corners = new[] { indices[index], indices[index + 1], indices[index + 2] };
                var owners = corners.Select(vertex => vertexPiece[vertex]).Distinct().ToList();
                Assert.That(owners, Has.Count.EqualTo(1), $"triangle {index / 3}");
                Assert.That(owners[0], Is.GreaterThanOrEqualTo(0));
                trianglesOfPiece[owners[0]]++;
                // The triangle lies inside its piece's rectangle (reference pixels), whole.
                var piece = pieces[owners[0]];
                foreach (var vertex in corners)
                {
                    var x = model.Mesh.VertexList[vertex].Uv.X * 2048;
                    var y = model.Mesh.VertexList[vertex].Uv.Y * 2048;
                    Assert.That(x, Is.InRange(Get<int>(piece, "X0"), Get<int>(piece, "X0") + Get<int>(piece, "Width")));
                    Assert.That(y, Is.InRange(Get<int>(piece, "Y0"), Get<int>(piece, "Y0") + Get<int>(piece, "Height")));
                }
            }

            Assert.That(trianglesOfPiece, Is.EqualTo(new[] { 2, 2 }));
            // Two triangles share two vertices of a quad; every vertex has one piece.
            Assert.That(vertexPiece, Has.None.EqualTo(-1));
            Assert.That(vertexChart, Is.All.EqualTo(0));
        }

        [Test]
        public void SharedRepack_TightPieces_AnInconsistentAssignment_KeepsTheMergedCharts_AndIsReported()
        {
            var tuple = SrTightTuple(SrDiagonalQuads());
            var grid = Get<int[]>(tuple, "MergedCellChart").Select(chart => chart >= 0).ToArray();
            // A labelling that cuts every quad in two (alternate columns are another component).
            var label = new int[grid.Length];
            for (var cell = 0; cell < grid.Length; cell++)
                label[cell] = grid[cell] ? (cell % 128) % 2 : -1;
            var boxes = (IList)Activator.CreateInstance(
                typeof(List<>).MakeGenericType(typeof((int, int, int, int))))!;
            boxes.Add((20, 20, 38, 38));
            boxes.Add((21, 20, 37, 38));
            tuple.GetType().GetProperty("TightCharts")!.SetValue(tuple, null);
            tuple.GetType().GetProperty("TightInconsistent")!.SetValue(tuple, null);

            GetStaticMethod("BuildSharedRepackTightPieces").Invoke(
                null, [tuple, grid, boxes, label, (Func<int, int>)(_ => 0)]);

            Assert.That(Get<string?>(tuple, "TightInconsistent"), Does.Contain("more than one UV component"));
            Assert.That(Get<object?>(tuple, "TightCharts"), Is.Null);

            // A group with such a tuple keeps the merged layout, whatever the pieces would have saved.
            var page = SrPackGroup(tuple, 4096, 1L << 60, out var split).Single();
            Assert.That(Get<IList>(page, "Charts"), Has.Count.EqualTo(1));
            Assert.That(Get<bool>(split, "TightInconsistent"), Is.True);
            Assert.That(Get<bool>(split, "TightUsed"), Is.False);
        }

        [Test]
        public void SharedRepack_TightPieces_GroupUsesThePiecesOnlyWhenTheirPagesAreSmaller()
        {
            // Diagonal: one 352x352 chart needs 512x512; two 192x192 pieces fit 512x256.
            var diagonal = SrTightTuple(SrDiagonalQuads());
            var pages = SrPackGroup(diagonal, 4096, 1L << 60, out var split);
            var page = pages.Single();
            Assert.That(Get<bool>(split, "TightUsed"), Is.True);
            Assert.That(Get<int>(split, "TightPieces"), Is.EqualTo(2));
            Assert.That(Get<int>(split, "TightMergedCharts"), Is.EqualTo(1));
            Assert.That(Get<long>(split, "TightSavedBytes"), Is.GreaterThan(0));
            Assert.That((Get<int>(page, "LayoutWidth"), Get<int>(page, "LayoutHeight")), Is.EqualTo((512, 256)));
            Assert.That(Get<IEnumerable>(page, "Charts").Cast<object>().Select(chart => Get<bool>(chart, "Piece")), Is.All.True);
            Assert.That(Get<bool>(diagonal, "Tight"), Is.True);
            Assert.That(Get<IList>(diagonal, "Charts"), Has.Count.EqualTo(2));
            // The pieces keep the scale: a piece is as large in the layout as its rectangle.
            foreach (var piece in Get<IEnumerable>(page, "Charts").Cast<object>())
            {
                Assert.That(Get<int>(piece, "LayoutWidth"), Is.EqualTo(Get<int>(piece, "Width")));
                Assert.That(Get<int>(piece, "PackX") % 32, Is.Zero);
                Assert.That(Get<int>(piece, "PackY") % 32, Is.Zero);
            }

            // Side by side: two 192x192 pieces are larger together than the 352x192 chart and need the
            // same 512x256 page; no gain, so the merged chart stays.
            var row = SrTightTuple(SrSideBySideQuads());
            var rowPage = SrPackGroup(row, 4096, 1L << 60, out var rowSplit).Single();
            Assert.That(Get<bool>(rowSplit, "TightTried"), Is.True);
            Assert.That(Get<bool>(rowSplit, "TightUsed"), Is.False);
            Assert.That(Get<bool>(row, "Tight"), Is.False);
            Assert.That(Get<IList>(row, "Charts"), Has.Count.EqualTo(1));
            Assert.That((Get<int>(rowPage, "LayoutWidth"), Get<int>(rowPage, "LayoutHeight")), Is.EqualTo((512, 256)));

            // Disabled, the diagonal group is the merged layout: 512x512, no alternative at all.
            WithTightPiecesDisabled(() =>
            {
                var plain = SrTightTuple(SrDiagonalQuads());
                var plainPage = SrPackGroup(plain, 4096, 1L << 60, out var plainSplit).Single();
                Assert.That(Get<object?>(plain, "TightCharts"), Is.Null);
                Assert.That(Get<bool>(plainSplit, "TightTried"), Is.False);
                Assert.That((Get<int>(plainPage, "LayoutWidth"), Get<int>(plainPage, "LayoutHeight")), Is.EqualTo((512, 512)));
            });
        }

        [Test]
        public void SharedRepack_TightPieces_OverlappingRectangles_DuplicateTheirTexels_AndTheUvRewriteSamplesTheSourceTexels()
        {
            var (world, factory) = BuildTightSrWorld(SrDiagonalQuads());
            object plan = null!;
            (byte[] Bgra, int Width, int Height) page = default;
            var charts = new List<(int X0, int Y0, int Width, int Height, int PackX, int PackY)>();
            object result = null!;

            WithBeforeCheckHook(
                hooked =>
                {
                    plan = hooked;
                    var firstPage = Get<IEnumerable>(hooked, "Pages").Cast<object>().First();
                    foreach (var chart in Get<IEnumerable>(firstPage, "Charts").Cast<object>())
                    {
                        Assert.That(Get<bool>(chart, "Piece"), Is.True);
                        charts.Add((Get<int>(chart, "X0"), Get<int>(chart, "Y0"), Get<int>(chart, "Width"), Get<int>(chart, "Height"),
                            Get<int>(chart, "PackX"), Get<int>(chart, "PackY")));
                    }

                    page = DecodeDds(Get<byte[]>(HookOutputs(hooked).Single(), "Data"));
                },
                () => result = world.Run());

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result));
            Assert.That(charts, Has.Count.EqualTo(2));
            var source = DecodeDds(world.Fixture.Vanilla[SrTightTexture]);
            // The two source rectangles overlap (x and y 448..480); that region is in both pieces.
            var duplicated = 0;
            for (var y = 448; y < 480; y += 3)
            {
                for (var x = 448; x < 480; x += 3)
                {
                    var expected = (y * source.Width + x) * 4;
                    foreach (var chart in charts)
                    {
                        var tx = chart.PackX + (x - chart.X0);
                        var ty = chart.PackY + (y - chart.Y0);
                        var actual = (ty * page.Width + tx) * 4;
                        for (var channel = 0; channel < 4; channel++)
                            Assert.That(page.Bgra[actual + channel], Is.EqualTo(source.Bgra[expected + channel]), $"({x},{y}) in piece at {chart.PackX},{chart.PackY}");
                    }

                    duplicated++;
                }
            }

            Assert.That(duplicated, Is.GreaterThan(50));
            // The rectangles do not overlap in the page.
            Assert.That(
                charts[0].PackX < charts[1].PackX + charts[1].Width && charts[1].PackX < charts[0].PackX + charts[0].Width &&
                charts[0].PackY < charts[1].PackY + charts[1].Height && charts[1].PackY < charts[0].PackY + charts[0].Height,
                Is.False);
            Assert.That((page.Width, page.Height), Is.EqualTo((512, 256)));
            // Every triangle, end to end, reads the texel it read before.
            var (samples, max) = AssertSrPixels(world, SrRigid, SrWs, factory, SrBigSeeds);
            Assert.That(samples, Is.EqualTo(4));
            Assert.That(max, Is.LessThanOrEqualTo(12));
            var built = SrBuilt(result).Single();
            Assert.That(Get<int>(built, "Charts"), Is.EqualTo(2));
            Assert.That(Get<long>(built, "Pieces"), Is.EqualTo(2));
            Assert.That(Get<long>(built, "PiecesSampled"), Is.EqualTo(2));
            var report = BuildSharedRepackSection(Enabled(world));
            Assert.That(report, Does.Contain("[tight pieces: 2 piece(s) instead of 1 chart(s)"));
            Assert.That(report, Does.Contain("tight pieces: 2 of 2 piece(s) sampled"));
            Assert.That(report, Does.Contain("Tight pieces (one rectangle per UV island instead of per merged chart"));
            Assert.That(report, Does.Contain("used by 1 built group(s) (2 piece(s) instead of 1 chart(s)"));
            Assert.That(report, Does.Contain("internal inconsistencies (merged charts kept): 0"));
            Assert.That(report, Does.Contain("baseline-model mismatches 0 chart(s)"));
            Assert.That(report, Does.Contain("group failures 0;"));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_TightPieces_NotSmaller_IsByteIdenticalToTheMergedLayout()
        {
            var (tight, _) = BuildTightSrWorld(SrSideBySideQuads());
            var tightResult = tight.Run();
            var (merged, _) = BuildTightSrWorld(SrSideBySideQuads());
            object mergedResult = null!;
            WithTightPiecesDisabled(() => mergedResult = merged.Run());

            Assert.That(Get<int>(tightResult, "Built"), Is.EqualTo(1), SrSkips(tightResult));
            Assert.That(Get<int>(mergedResult, "Built"), Is.EqualTo(1), SrSkips(mergedResult));
            Assert.That(tight.Fixture.Output.Keys.OrderBy(key => key, StringComparer.Ordinal),
                Is.EqualTo(merged.Fixture.Output.Keys.OrderBy(key => key, StringComparer.Ordinal)));
            foreach (var (path, bytes) in merged.Fixture.Output)
                Assert.That(tight.Fixture.Output[path], Is.EqualTo(bytes), path);
            var report = BuildSharedRepackSection(Enabled(tight));
            Assert.That(report, Does.Contain("1 group(s) had merged charts to split; used by 0 built group(s)"));
            Assert.That(report, Does.Contain("1 group(s) kept the merged charts (pieces not smaller)"));
            Assert.That(report, Does.Not.Contain("[tight pieces:"));
            Assert.That(Get<long>(SrBuilt(tightResult).Single(), "Pieces"), Is.Zero);
        }

        [Test]
        public void SharedRepack_TightPieces_TwoRuns_AreIdentical_AndSmallerThanTheMergedLayout()
        {
            var (first, _) = BuildTightSrWorld(SrDiagonalQuads(), lods: 2);
            var firstResult = first.Run();
            var (second, _) = BuildTightSrWorld(SrDiagonalQuads(), lods: 2);
            second.Run();
            var (merged, _) = BuildTightSrWorld(SrDiagonalQuads(), lods: 2);
            object mergedResult = null!;
            WithTightPiecesDisabled(() => mergedResult = merged.Run());

            Assert.That(Get<int>(firstResult, "Built"), Is.EqualTo(1), SrSkips(firstResult));
            Assert.That(second.Fixture.Output.Keys.OrderBy(key => key, StringComparer.Ordinal),
                Is.EqualTo(first.Fixture.Output.Keys.OrderBy(key => key, StringComparer.Ordinal)));
            foreach (var (path, bytes) in first.Fixture.Output)
                Assert.That(second.Fixture.Output[path], Is.EqualTo(bytes), path);
            Assert.That(
                BuildSharedRepackSection(Enabled(first)),
                Is.EqualTo(BuildSharedRepackSection(Enabled(second))));
            var tightBytes = Get<long>(SrBuilt(firstResult).Single(), "After");
            var mergedBytes = Get<long>(SrBuilt(mergedResult).Single(), "After");
            Assert.That(tightBytes, Is.LessThan(mergedBytes));
            // Both LODs of the part use the same pieces.
            Assert.That(Get<int>(SrBuilt(firstResult).Single(), "Charts"), Is.EqualTo(2));
        }

        // A layout group of two tuples: part 0 (texture A) has merged components, part 1 (texture B) a single
        // quad and so no pieces. The tuple without pieces shares its chart object between the merged and the
        // pieces layout run; when the pieces lose, the merged pages must still hold the merged positions.
        private static (SrWorld World, Func<RmvFile> Rigid) BuildMixedTightSrWorld(
            (double U0, double V0, double U1, double V1)[] rects)
        {
            var world = new SrWorld();
            world.AddTexture(SrTightTexture, 2048, "BC1", true, 1);
            world.AddTexture("textures\\sr_big2.dds", 2048, "BC1", true, 3);
            world.Fixture.AddMaterial(SrMaterialPath("mbig"), (SrBase, SrTightTexture));
            world.Fixture.AddMaterial(SrMaterialPath("mbig2"), (SrBase, "textures\\sr_big2.dds"));
            var a = rects.SelectMany(rect => UvQuad(rect.U0, rect.V0, rect.U1, rect.V1)).ToArray();
            var b = UvQuad(60.2 / 128, 100.2 / 128, 66.8 / 128, 106.8 / 128);
            var factory = SrRigidFactory([(SrMaterialPath("mbig"), a), (SrMaterialPath("mbig2"), b)]);
            world.AddModel(SrRigid, SrWs, SrRoot, factory, [[SrMaterialPath("mbig"), SrMaterialPath("mbig2")]]);
            return (world, factory);
        }

        private static void AssertMixedTightGroup(bool piecesWin)
        {
            var rects = piecesWin ? SrDiagonalQuads() : SrSideBySideQuads();
            var (tight, factory) = BuildMixedTightSrWorld(rects);
            var tightResult = tight.Run();
            var (plain, _) = BuildMixedTightSrWorld(rects);
            object plainResult = null!;
            WithTightPiecesDisabled(() => plainResult = plain.Run());

            Assert.That(Get<int>(tightResult, "Built"), Is.EqualTo(1), SrSkips(tightResult));
            Assert.That(Get<int>(tightResult, "FailedPixelCheck"), Is.Zero, SrSkips(tightResult));
            var built = SrBuilt(tightResult).Single();
            var report = BuildSharedRepackSection(Enabled(tight));
            if (piecesWin)
            {
                Assert.That(report, Does.Contain("[tight pieces:"));
                Assert.That(Get<long>(built, "After"), Is.LessThan(Get<long>(SrBuilt(plainResult).Single(), "After")));
            }
            else
            {
                Assert.That(report, Does.Not.Contain("[tight pieces:"));
                Assert.That(Get<long>(built, "Pieces"), Is.Zero);
                Assert.That(tight.Fixture.Output.Keys.OrderBy(key => key, StringComparer.Ordinal),
                    Is.EqualTo(plain.Fixture.Output.Keys.OrderBy(key => key, StringComparer.Ordinal)));
                foreach (var (path, bytes) in plain.Fixture.Output)
                    Assert.That(tight.Fixture.Output[path], Is.EqualTo(bytes), path);
            }

            // Both parts read the texels they read before (part 0 seed 1, part 1 seed 3).
            AssertSrPixels(tight, SrRigid, SrWs, factory, SrBigSeeds, (lod, part) => part == 0);
            AssertSrPixels(tight, SrRigid, SrWs, factory, new Dictionary<string, int> { [SrBase] = 3 }, (lod, part) => part == 1);
            Assert.That(SrValidate(tight), Is.Empty);
        }

        [Test]
        public void SharedRepack_TightPieces_MixedGroup_PiecesLose_KeepsTheMergedPositionsOfTheTupleWithoutPieces()
            => AssertMixedTightGroup(piecesWin: false);

        [Test]
        public void SharedRepack_TightPieces_MixedGroup_PiecesWin_StillSamplesEveryTexel()
            => AssertMixedTightGroup(piecesWin: true);

        // 2048 base and normal, 512 noise: Align 32, cell 16, reference aspect 1:1. Four small quads on a
        // diagonal merge into one 352x352 chart (a 512x512 page); as pieces they fit a 256x256 page.
        private static SrWorld BuildTightEmissiveSrWorld()
        {
            var world = new SrWorld();
            world.AddTexture("textures\\sr_b1.dds", 2048, "BC1", true, 1);
            world.AddTexture("textures\\sr_n1.dds", 2048, "BC3", false, 2);
            world.AddTexture(SrNoise, 512, "BC1", true, 7);
            world.AddTexture(SrGlowTexture, 512, "BC1", true, 8);
            var e1 = SrMaterialPath("e1");
            world.Fixture.AddText(e1, SrEmissiveMaterialXml(SrEmissiveShader));
            var triangles = new[] { (10, 13), (15, 18), (20, 23), (25, 28) }
                .SelectMany(range => UvQuad((range.Item1 + 0.2) / 128, (range.Item1 + 0.2) / 128, (range.Item2 + 0.8) / 128, (range.Item2 + 0.8) / 128))
                .ToArray();
            world.AddModel(SrRigid, SrWs, SrRoot, SrRigidFactory([(e1, triangles)]), [[e1]]);
            return world;
        }

        [Test]
        public void SharedRepack_TightPieces_EmissiveCompensatedGroup_KeepsTheAspectRestrictionAndTheParameters()
        {
            var world = BuildTightEmissiveSrWorld();
            var triangles = new[] { (10, 13), (15, 18), (20, 23), (25, 28) }
                .SelectMany(range => UvQuad((range.Item1 + 0.2) / 128, (range.Item1 + 0.2) / 128, (range.Item2 + 0.8) / 128, (range.Item2 + 0.8) / 128))
                .ToArray();
            var factory = SrRigidFactory([(SrMaterialPath("e1"), triangles)]);

            var result = world.Run();

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result) + SrPartDropReasons(result));
            var built = SrBuilt(result).Single();
            var splitInfo = Get<IEnumerable>(built, "Splits").Cast<object>().Single();
            Assert.That(Get<bool>(splitInfo, "AspectRestricted"), Is.True);
            Assert.That(Get<int>(built, "Charts"), Is.EqualTo(4));
            Assert.That(Get<int>(built, "LayoutWidth"), Is.EqualTo(Get<int>(built, "LayoutHeight")));
            Assert.That(Get<int>(built, "LayoutWidth"), Is.EqualTo(256));
            // Pieces are only translated: the scale is the page's, equal in U and V, as for any group.
            var scale = 2048.0 / Get<int>(built, "LayoutWidth");
            var generated = Encoding.UTF8.GetString(world.Fixture.Output[SrWsMaterial(world.State, SrWs, 0, 0)]);
            var document = new XmlDocument();
            document.LoadXml(generated);
            string Value(string name) => document.SelectNodes("/material/params/param")!.Cast<XmlNode>()
                .Single(node => node.SelectSingleNode("name")!.InnerText == name).SelectSingleNode("value")!.InnerText;
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            Assert.That(Value("emissive_tiling"), Is.EqualTo(string.Create(invariant, $"{2 / scale:R},{3 / scale:R}")));
            Assert.That(Value("emissive_distort_strength"), Is.EqualTo(string.Create(invariant, $"{0.05 * scale:R}")));
            Assert.That(Value("emissive_direction"), Is.EqualTo(string.Create(invariant, $"{0.5 * scale:R},{-0.25 * scale:R}")));
            Assert.That(Get<int>(result, "EmissiveAspectGroups"), Is.EqualTo(1));
            Assert.That(Get<int>(result, "EmissiveMaterialsRescaled"), Is.EqualTo(1));
            var (samples, max) = AssertSrPixels(world, SrRigid, SrWs, factory, SrEmissiveSeeds);
            Assert.That(samples, Is.GreaterThan(0));
            Assert.That(max, Is.LessThanOrEqualTo(12));
            var section = BuildSharedRepackSection(Enabled(world));
            Assert.That(section, Does.Contain("[tight pieces: 4 piece(s) instead of 1 chart(s)"));
            Assert.That(section, Does.Contain("canvases keep the reference aspect"));
            Assert.That(SrValidate(world), Is.Empty);

            // Without pieces the same group needs the 512x512 page.
            var plain = BuildTightEmissiveSrWorld();
            object plainResult = null!;
            WithTightPiecesDisabled(() => plainResult = plain.Run());
            Assert.That(Get<int>(SrBuilt(plainResult).Single(), "LayoutWidth"), Is.EqualTo(512));
            Assert.That(Get<long>(built, "After"), Is.LessThan(Get<long>(SrBuilt(plainResult).Single(), "After")));
        }

        // --- Copies of textures that left a plan -------------------------------------------------
        //
        // Part 2 (m3 = b1 + n3) is dropped because n3 cannot be repacked; b1 leaves the set with it, which
        // used to take part 0 (m1 = b1 + n1) down too. With copies part 0 stays: b1 is cropped into the pages
        // as a copy for it (the original stays for part 2), n1 is replaced as usual.
        private static SrWorld BuildCopyCascadeSrWorld(bool wide = false)
        {
            var world = new SrWorld();
            world.AddTexture("textures\\sr_b1.dds", 1024, "BC1", true, 1, owned: true);
            world.AddTexture("textures\\sr_n1.dds", 512, "BC3", false, 2, owned: true);
            world.AddTexture("textures\\sr_b2.dds", 1024, "BC1", true, 3, owned: true);
            world.AddTexture("textures\\sr_n2.dds", 512, "BC3", false, 4, owned: true);
            world.AddTexture("textures\\sr_n3.dds", 48, "BC3", false, 6, owned: true);
            world.Fixture.AddMaterial(SrMaterialPath("m1"), (SrBase, "textures\\sr_b1.dds"), (SrNormal, "textures\\sr_n1.dds"));
            world.Fixture.AddMaterial(SrMaterialPath("m2"), (SrBase, "textures\\sr_b2.dds"), (SrNormal, "textures\\sr_n2.dds"));
            world.Fixture.AddMaterial(SrMaterialPath("m3"), (SrBase, "textures\\sr_b1.dds"), (SrNormal, "textures\\sr_n3.dds"));
            var m1 = SrMaterialPath("m1");
            var m2 = SrMaterialPath("m2");
            var m3 = SrMaterialPath("m3");
            // The wide part 0 covers all of b1: its copy costs more than the group saves.
            var part0 = wide ? UvQuad(0.004, 0.004, 0.996, 0.996) : SrBlocks((2, 3), (3, 3));
            world.AddModel(
                SrRigid, SrWs, SrRoot,
                SrRigidFactory([(m1, part0), (m2, SrBlocks((10, 4))), (m3, SrBlocks((3, 3)))]),
                [[m1, m2, m3]]);
            return world;
        }

        private static List<object> SrOutputs(object built)
            => Get<IEnumerable>(built, "Outputs").Cast<object>().ToList();

        private static string SrMaterialSlotSource(SrWorld world, string materialPath, string slot)
        {
            var material = new XmlDocument();
            material.LoadXml(Encoding.UTF8.GetString(world.Fixture.Output[materialPath]));
            return material.SelectNodes("/material/textures/texture")!.Cast<XmlNode>()
                .Single(node => node.SelectSingleNode("slot")!.InnerText.Trim() == slot)
                .SelectSingleNode("source")!.InnerText;
        }

        [Test]
        public void SharedRepack_CopyRemoved_KeepsThePartThatSharedATextureWithADroppedOne()
        {
            var world = BuildCopyCascadeSrWorld();
            var original = world.Fixture.Source["textures\\sr_b1.dds"].ToArray();

            var result = world.Run();

            AssertFunnelReconciles(result);
            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result) + SrPartDropReasons(result));
            var built = SrBuilt(result).Single();
            // b1 is a copy, not a replaced texture; n1, b2 and n2 are replaced.
            var replaced = Get<IEnumerable>(built, "Replaced").Cast<object>().Select(item => item.ToString()).ToList();
            Assert.That(replaced, Has.Count.EqualTo(3));
            Assert.That(string.Concat(replaced), Does.Contain("sr_n1").And.Contain("sr_b2").And.Contain("sr_n2"));
            Assert.That(string.Concat(replaced), Does.Not.Contain("sr_b1"));
            var outputs = SrOutputs(built);
            var copy = outputs.Single(output => Get<bool>(output, "IsCopy"));
            Assert.That(Get<string>(copy, "Slot"), Is.EqualTo(SrBase));
            Assert.That(Get<IEnumerable>(copy, "Sources").Cast<string>(), Is.EqualTo(new[] { "textures\\sr_b1.dds" }));
            // The slot's normal channel name, not the emissive one, and a cropped sheet.
            Assert.That(Get<string>(copy, "Path"), Does.EndWith("_repack_base_colour.dds"));
            Assert.That(Get<int>(copy, "Width"), Is.LessThan(1024));
            Assert.That(Get<bool>(built, "CopyWon"), Is.True);
            Assert.That(Get<IEnumerable>(built, "RemovedCopies"), Is.Not.Empty);
            // The original stays, unchanged, and part 2 keeps its material and UVs.
            Assert.That(world.Fixture.Output["textures\\sr_b1.dds"], Is.EqualTo(original));
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 2), Is.EqualTo(SrMaterialPath("m3")));
            var untouched = SrRigidFactory([(SrMaterialPath("m1"), SrBlocks((2, 3), (3, 3))), (SrMaterialPath("m2"), SrBlocks((10, 4))), (SrMaterialPath("m3"), SrBlocks((3, 3)))])();
            var rigid = (RmvFile)Get<IDictionary>(world.State, "RigidModels")[SrRigid]!;
            Assert.That(
                rigid.ModelList[0][2].Mesh.VertexList.Select(vertex => vertex.Uv),
                Is.EqualTo(untouched.ModelList[0][2].Mesh.VertexList.Select(vertex => vertex.Uv)));
            // Part 0 moved: its material points at the copy for the base colour and a page for the normal.
            Assert.That(
                rigid.ModelList[0][0].Mesh.VertexList.Select(vertex => vertex.Uv),
                Is.Not.EqualTo(untouched.ModelList[0][0].Mesh.VertexList.Select(vertex => vertex.Uv)));
            var generated = SrWsMaterial(world.State, SrWs, 0, 0);
            Assert.That(generated, Does.Contain("_repack_"));
            Assert.That(SrMaterialSlotSource(world, generated, SrBase), Is.EqualTo(Get<string>(copy, "Path")));
            var normalOutput = outputs.Single(output => !Get<bool>(output, "IsCopy") &&
                Get<IEnumerable>(output, "Sources").Cast<string>().Contains("textures\\sr_n1.dds"));
            Assert.That(SrMaterialSlotSource(world, generated, SrNormal), Is.EqualTo(Get<string>(normalOutput, "Path")));
            // Pixel-checked like every output, copy included.
            Assert.That(Get<long>(built, "Samples"), Is.GreaterThan(0));
            Assert.That(Get<double>(built, "MaxError"), Is.Zero);
            Assert.That(Get<IEnumerable>(result, "PixelFailures"), Is.Empty);
            var section = BuildSharedRepackSection(Enabled(world));
            Assert.That(section, Does.Contain("[copies: 1 texture(s)").And.Contain("more than without copies")
                .Or.Contain("built only thanks to the copies"));
            Assert.That(section, Does.Contain("copy of textures\\sr_b1.dds").And.Contain("left the plan when"));
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_CopyRemoved_ValidationFindsAMissingCopySource()
        {
            var world = BuildCopyCascadeSrWorld();
            world.Run();
            Assert.That(SrValidate(world), Is.Empty);

            world.Fixture.Output.Remove("textures\\sr_b1.dds");

            Assert.That(SrValidate(world), Has.Some.Contain("is cropped from textures\\sr_b1.dds"));
        }

        [Test]
        public void SharedRepack_CopyRemoved_KillSwitchRestoresTheCascade()
        {
            var world = BuildCopyCascadeSrWorld();
            object result = null!;
            WithCopyRemovedDisabled(() => result = world.Run());

            var built = SrBuilt(result).Single();
            var replaced = Get<IEnumerable>(built, "Replaced").Cast<object>().Select(item => item.ToString()).ToList();
            Assert.That(replaced, Has.Count.EqualTo(2));
            Assert.That(string.Concat(replaced), Does.Contain("sr_b2").And.Contain("sr_n2"));
            Assert.That(SrOutputs(built), Has.None.Matches<object>(output => Get<bool>(output, "IsCopy")));
            Assert.That(Get<bool>(built, "CopyWon"), Is.False);
            Assert.That(SrWsMaterial(world.State, SrWs, 0, 0), Is.EqualTo(SrMaterialPath("m1")));

            // The generated files are those of a world that never had the cascade.
            var baseline = new SrWorld();
            foreach (var texture in new[] { "b2", "n2" })
                baseline.AddTexture($"textures\\sr_{texture}.dds", texture[0] == 'b' ? 1024 : 512, texture[0] == 'b' ? "BC1" : "BC3", texture[0] == 'b', texture == "b2" ? 3 : 4, owned: true);
            baseline.Fixture.AddMaterial(SrMaterialPath("m2"), (SrBase, "textures\\sr_b2.dds"), (SrNormal, "textures\\sr_n2.dds"));
            baseline.AddModel(
                SrRigid, SrWs, SrRoot,
                SrRigidFactory([(SrMaterialPath("m2"), SrBlocks((10, 4)))]),
                [[SrMaterialPath("m2")]]);
            baseline.Run();
            foreach (var path in baseline.Fixture.Output.Keys.Where(path => path.Contains("_repack_")))
            {
                Assert.That(world.Fixture.Output.ContainsKey(path), Is.True, path);
                Assert.That(world.Fixture.Output[path], Is.EqualTo(baseline.Fixture.Output[path]), path);
            }
        }

        [Test]
        public void SharedRepack_CopyRemoved_ACopyThatDoesNotPay_KeepsTheNormalPlanAndItsOutput()
        {
            var world = BuildCopyCascadeSrWorld(wide: true);
            var result = world.Run(minimumSaving: 1, minimumGroupSaving: 1);

            Assert.That(SrBuilt(result), Has.Count.EqualTo(1), SrSkips(result) + SrPartDropReasons(result));
            var built = SrBuilt(result).Single();
            // Planned both ways, the copy plan saved no more (part 0's group does not pay): the normal plan is built.
            var outcome = Get<IEnumerable>(result, "Outcomes").Cast<object>()
                .Single(item => Get<string>(item, "Bucket") == "built");
            var context = Get<object>(outcome, "Context");
            Assert.That(Get<bool>(context, "PlannedBothWays"), Is.True);
            Assert.That(Get<bool>(context, "CopyWon"), Is.False);
            Assert.That(Get<bool>(built, "CopyWon"), Is.False);
            Assert.That(SrOutputs(built), Has.None.Matches<object>(output => Get<bool>(output, "IsCopy")));
            Assert.That(BuildSharedRepackSection(Enabled(world)), Does.Not.Contain("[copies:"));

            var switched = BuildCopyCascadeSrWorld(wide: true);
            WithCopyRemovedDisabled(() => switched.Run(minimumSaving: 1, minimumGroupSaving: 1));
            Assert.That(world.Fixture.Output.Keys.OrderBy(key => key), Is.EqualTo(switched.Fixture.Output.Keys.OrderBy(key => key)));
            foreach (var (path, bytes) in switched.Fixture.Output)
                Assert.That(world.Fixture.Output[path], Is.EqualTo(bytes), path);
            Assert.That(SrValidate(world), Is.Empty);
        }

        [Test]
        public void SharedRepack_CopyRemoved_KillSwitchChangesTheOutputWhereCopiesWin()
        {
            var world = BuildCopyCascadeSrWorld();
            world.Run();
            var switched = BuildCopyCascadeSrWorld();
            WithCopyRemovedDisabled(() => switched.Run());

            Assert.That(world.Fixture.Output.Keys.OrderBy(key => key), Is.Not.EqualTo(switched.Fixture.Output.Keys.OrderBy(key => key)));
            Assert.That(world.Fixture.Output.Keys.Count(key => key.Contains("_repack_")), Is.GreaterThan(
                switched.Fixture.Output.Keys.Count(key => key.Contains("_repack_"))));
        }

        // A copied base colour is a normal slot for the layout: it can be the reference, raises the chart alignment
        // like any slot and block-copies as many levels as the same slot of a replaced texture. The emissive noise
        // copy keeps its own rules.
        [Test]
        public void SharedRepack_CopyRemoved_CopiedSlotLaysOutLikeANormalSlot()
        {
            const string baseTexture = "textures\\b.dds";
            const string normal = "textures\\n.dds";
            var part = SrPart(new Dictionary<string, string> { [SrBase] = baseTexture, [SrNormal] = normal });
            var entries = new Dictionary<string, object>
            {
                [baseTexture] = SrEntry(baseTexture, 1024, 1024),
                [normal] = SrEntry(normal, 128, 128),
            };

            var plain = SrBuildTuples([part], entries, _ => null, out _).Single();
            var copiedBase = SrBuildTuples(
                [part], entries, _ => null, out _, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { baseTexture }).Single();
            var copiedNormal = SrBuildTuples(
                [part], entries, _ => null, out _, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normal }).Single();

            Assert.That(Get<HashSet<string>>(copiedBase, "CopySlots"), Is.EquivalentTo(new[] { SrBase }));
            Assert.That(Get<bool>(copiedBase, "EmissiveCompensation"), Is.False);
            Assert.That(Get<IEnumerable>(copiedBase, "MemberTextures").Cast<string>(), Is.EqualTo(new[] { normal }));
            foreach (var tuple in new[] { copiedBase, copiedNormal })
            {
                Assert.That(Get<string?>(tuple, "Excluded"), Is.Null);
                Assert.That(Get<int>(tuple, "ReferenceWidth"), Is.EqualTo(Get<int>(plain, "ReferenceWidth")));
                Assert.That(Get<int>(tuple, "Cell"), Is.EqualTo(Get<int>(plain, "Cell")));
                // The normal is 8 times smaller than the reference: the alignment follows it whether or not it is copied.
                Assert.That(Get<int>(tuple, "Align"), Is.EqualTo(Get<int>(plain, "Align")));
            }

            // A group keeps copied and replaced textures apart.
            Assert.That(Get<string>(copiedBase, "GroupKey"), Does.Contain("copy:" + baseTexture));
            Assert.That(Get<string>(copiedBase, "GroupKey"), Is.Not.EqualTo(Get<string>(plain, "GroupKey")));
        }

        [Test]
        public void SharedRepack_CopyRemoved_CopiedBaseColour_BlockCopiesLikeTheOtherSlotsOfItsPage()
        {
            var world = BuildCopyCascadeSrWorld();
            var built = SrBuilt(world.Run()).Single();
            var outputs = SrOutputs(built);
            var copy = outputs.Single(output => Get<bool>(output, "IsCopy"));
            var normal = outputs.Single(output => Get<string>(output, "Slot") == SrNormal &&
                Get<IEnumerable>(output, "Sources").Cast<string>().Contains("textures\\sr_n1.dds"));

            // The page's alignment follows its coarsest slot whether or not the base colour is a copy: the
            // base colour (twice the normal's size) has exactly one more block-aligned level than the normal.
            Assert.That(Get<int>(normal, "BlockCopiedLevels"), Is.GreaterThan(0));
            Assert.That(Get<int>(copy, "BlockCopiedLevels"), Is.EqualTo(Get<int>(normal, "BlockCopiedLevels") + 1));
            Assert.That(Get<int>(copy, "Width"), Is.EqualTo(2 * Get<int>(normal, "Width")));
        }

        // A directly loaded rigid keeps its embedded material in step with the WSModel material: a copied slot
        // points at the copy output, the replaced slots at their pages.
        [Test]
        public void SharedRepack_CopyRemoved_DirectRigidEmbeddedSlotPointsAtTheCopy()
        {
            var world = BuildDirectEmbeddedSrWorld(withWsModel: false);
            SrInstallReadableSourceRigid(world);
            world.AddTexture("textures\\sr_direct_other_base.dds", 1024, "BC1", true, 31);
            world.AddTexture("textures\\sr_cm.dds", 256, "BC1", true, 32);
            // A second rigid shares the direct rigid's normal and has a slot the repack cannot move.
            var other = SrMaterialPath("other");
            world.Fixture.AddText(
                other,
                SrMaterialXml(
                        "textures\\sr_direct_other_base.dds",
                        "textures\\sr_direct_normal.dds",
                        [("t_xml_colour_map", "textures\\sr_cm.dds")])
                    .Replace(SrWeighted2Shader, "shaders/rigid_random_colours.xml.shader"));
            world.AddModel(SrRigid2, SrWs2, SrRoot2, SrRigidFactory([(other, SrBlocks((3, 3)))]), [[other]]);

            var result = world.Run(minimumSaving: 1, minimumGroupSaving: 1);

            Assert.That(Get<int>(result, "Built"), Is.EqualTo(1), SrSkips(result) + SrPartDropReasons(result));
            Assert.That(Get<HashSet<string>>(result, "EmbeddedRewrittenRigids"), Is.EquivalentTo([SrRigid]));
            var built = SrBuilt(result).Single();
            var outputs = SrOutputs(built);
            var copy = outputs.Single(output => Get<bool>(output, "IsCopy"));
            Assert.That(Get<string>(copy, "Slot"), Is.EqualTo(SrNormal));
            Assert.That(Get<IEnumerable>(copy, "Sources").Cast<string>(), Is.EqualTo(new[] { "textures\\sr_direct_normal.dds" }));
            var rigid = (RmvFile)Get<IDictionary>(world.State, "RigidModels")[SrRigid]!;
            var embedded = rigid.ModelList[0][0].Material!;
            Assert.That(embedded.GetTexture(TextureType.Normal)!.Value.Path, Is.EqualTo(Get<string>(copy, "Path")));
            var basePage = outputs.Single(output => !Get<bool>(output, "IsCopy") && Get<string>(output, "Slot") == SrBase);
            Assert.That(embedded.GetTexture(TextureType.BaseColour)!.Value.Path, Is.EqualTo(Get<string>(basePage, "Path")));
            // The legacy diffuse follows the base colour's page.
            Assert.That(embedded.GetTexture(TextureType.Diffuse)!.Value.Path, Is.EqualTo(Get<string>(basePage, "Path")));
            Assert.That(world.Fixture.Output.ContainsKey(Get<string>(copy, "Path")), Is.True);
            Assert.That(SrValidateDirectRigidEmbeddedTextures(world), Is.Empty);
            Assert.That(SrValidate(world), Is.Empty);
        }

        // --- Output version ---------------------------------------------------------------------
        //
        // PackOptimizerVersions.OutputVersion is part of the mod manager's cache key. The golden hash of
        // the optimizer's output stages is pinned here next to it. A change that alters the output for the
        // same input changes the hash; the test then fails until OutputVersion is bumped and the new hash is
        // ADDED under the new version (never edit an existing entry). A change that leaves the output alone
        // (speed, logging, the probe) leaves the hash alone and needs no bump.
        //
        // The stages hashed: texture downscaling and lossless compaction on a fixed set of synthetic
        // textures, geometry optimization and the mesh merge on fixed synthetic meshes, and the
        // battle-presence model's army pools and weights (under the default army scope) on a fixed
        // synthetic roster, since every value-gated stage (cross-rigid merges, atlases) decides on it. Whole-pack output is
        // checked by the fingerprint bench (Tools/PackOptimizerBench), which this test does not replace.
        private static readonly Dictionary<int, string> OutputGoldenHashes = new()
        {
            [1] = "16D0E04C047A3181D23F53880A296765BD51EF01C67FCD1CC7C5D0ECBE7A77F5",
            // 2: army pools per subculture, player scopes weighted by the pack's units.
            [2] = "8B7093023427E55FBF1CD767BA5C4B782EB218E82B78C68131B02BF8A58ECAE6",
            // 3: files the source references are also resolved from its dependency-related packs
            // (dependencyPackPaths); the pinned stages are unchanged without dependencies.
            [3] = "8B7093023427E55FBF1CD767BA5C4B782EB218E82B78C68131B02BF8A58ECAE6",
            // 4: consumer-added units that do not use the source stay out of the army model; the
            // pinned stages have no consumers, so the hash is unchanged.
            [4] = "8B7093023427E55FBF1CD767BA5C4B782EB218E82B78C68131B02BF8A58ECAE6",
            // 5: the shared texture repack builds sets saving at least 1 MiB (was 2 MiB); the
            // repack is not among the pinned stages, so the hash is unchanged.
            [5] = "8B7093023427E55FBF1CD767BA5C4B782EB218E82B78C68131B02BF8A58ECAE6",
            // 6: the shared texture repack also rewrites the rigids cross-rigid merging relied on and the
            // ones it generated; the repack is not among the pinned stages, so the hash is unchanged.
            [6] = "8B7093023427E55FBF1CD767BA5C4B782EB218E82B78C68131B02BF8A58ECAE6",
            // 7: the shared texture repack also repacks parts whose verified emissive shader samples a
            // shared distortion noise (a cropped copy plus rescaled emissive parameters); the repack is
            // not among the pinned stages, so the hash is unchanged.
            [7] = "8B7093023427E55FBF1CD767BA5C4B782EB218E82B78C68131B02BF8A58ECAE6",
            // 8: texture helpers rewrite every entry of a slot listed twice in a material, and materials
            // listing a slot twice with different sources are refused; the pinned stages have no such
            // material, so the hash is unchanged.
            [8] = "8B7093023427E55FBF1CD767BA5C4B782EB218E82B78C68131B02BF8A58ECAE6",
            // 9: the shared texture repack (and the duplicate-slot refusals) ignore a t_xml_ slot the material's
            // shader never binds; the repack is not among the pinned stages, so the hash is unchanged.
            [9] = "8B7093023427E55FBF1CD767BA5C4B782EB218E82B78C68131B02BF8A58ECAE6",
            // 10: the shared texture repack packs its pages with the best of shelf, MaxRects and skyline
            // (and fills split pages on MaxRects too); the repack is not among the pinned stages, so the
            // hash is unchanged.
            [10] = "8B7093023427E55FBF1CD767BA5C4B782EB218E82B78C68131B02BF8A58ECAE6",
            // 11: the shared texture repack lays a group out on tight pieces (one rectangle per UV island)
            // when that makes its pages smaller; the repack is not among the pinned stages, so the hash is
            // unchanged.
            [11] = "8B7093023427E55FBF1CD767BA5C4B782EB218E82B78C68131B02BF8A58ECAE6",
            // 12: the shared texture repack seeds the lower mip levels of its pages from the sources' own decoded
            // mips (gutter included) wherever a rectangle sits on whole texels, instead of box-filtering the page;
            // the repack is not among the pinned stages, so the hash is unchanged.
            [12] = "8B7093023427E55FBF1CD767BA5C4B782EB218E82B78C68131B02BF8A58ECAE6",
            // 13: shared texture repack groups may choose smaller chart padding; the pinned stages do not include it.
            [13] = "8B7093023427E55FBF1CD767BA5C4B782EB218E82B78C68131B02BF8A58ECAE6",
            // 14: direct rigid embedded materials are rewritten by shared repack; the pinned pack has none.
            [14] = "8B7093023427E55FBF1CD767BA5C4B782EB218E82B78C68131B02BF8A58ECAE6",
            // 15: VMDs that only parse leniently no longer make gameplay discovery partial; the pinned pack has none.
            [15] = "8B7093023427E55FBF1CD767BA5C4B782EB218E82B78C68131B02BF8A58ECAE6",
            // 16: the late texture downscale run uses the early run's source-state reference density; the pinned pack has none.
            [16] = "8B7093023427E55FBF1CD767BA5C4B782EB218E82B78C68131B02BF8A58ECAE6",
            // 17: the shared texture repack copies a texture that left a plan into the pages of the parts that stay
            // when that saves more; the repack is not among the pinned stages, so the hash is unchanged.
            [17] = "8B7093023427E55FBF1CD767BA5C4B782EB218E82B78C68131B02BF8A58ECAE6",
            // 18: direct rigid embedded slots that name a uniform texture kept as authored stay unchanged instead of
            // failing the set; the repack is not among the pinned stages, so the hash is unchanged.
            [18] = "8B7093023427E55FBF1CD767BA5C4B782EB218E82B78C68131B02BF8A58ECAE6",
            // 19: the shared texture repack also repacks parts whose verified dissolve shader samples a shared
            // dissolve map (a cropped copy) and a detail texture at UV0 * dissolve_uv_scale (the scale is divided);
            // the repack is not among the pinned stages, so the hash is unchanged.
            [19] = "8B7093023427E55FBF1CD767BA5C4B782EB218E82B78C68131B02BF8A58ECAE6",
            // 20: the shared texture repack writes identical pages of layout groups on the same geometry once;
            // the repack is not among the pinned stages, so the hash is unchanged.
            [20] = "8B7093023427E55FBF1CD767BA5C4B782EB218E82B78C68131B02BF8A58ECAE6",
            // 21: the shared texture repack also repacks a part whose WSModel bindings name different textures
            // of the same size and format (variant pages next to the layout page); the repack is not among the
            // pinned stages, so the hash is unchanged.
            [21] = "8B7093023427E55FBF1CD767BA5C4B782EB218E82B78C68131B02BF8A58ECAE6",
            // 22: the shared texture repack also rewrites directly loaded rigids whose embedded material is
            // default_type (and refuses unverified vertex formats); the repack is not among the pinned
            // stages, so the hash is unchanged.
            [22] = "8B7093023427E55FBF1CD767BA5C4B782EB218E82B78C68131B02BF8A58ECAE6",
            // 23 adds shared flow-overlay rewriting; the output golden fixture has no flow shader material.
            [23] = "8B7093023427E55FBF1CD767BA5C4B782EB218E82B78C68131B02BF8A58ECAE6",
            // 24: the shared texture repack accepts BC7 sets (block-copied where aligned, BC7 encoded elsewhere);
            // the repack is not among the pinned stages, so the hash is unchanged.
            [24] = "8B7093023427E55FBF1CD767BA5C4B782EB218E82B78C68131B02BF8A58ECAE6",
        };

        private static string ComputeOutputGoldenHash()
        {
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);
            void Add(string label, byte[] bytes)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(label + "\n"));
                hash.AppendData(BitConverter.GetBytes(bytes.Length));
                hash.AppendData(bytes);
            }

            // Textures: downscale, then compaction, on the standard synthetic set.
            var fixture = BuildStandardDownscaleFixture();
            var (service, state) = CreateStandardDownscaleState(fixture, downscale: true);
            RunDownscale(service, state, fixture);
            SetStateProperty(state, "CompactTextures", true);
            GetServiceType()
                .GetMethod("RunTextureCompaction", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [state, CancellationToken.None, null]);
            foreach (var (path, bytes) in fixture.Output.OrderBy(item => item.Key, StringComparer.Ordinal))
                Add("file:" + path, bytes);

            // Geometry: a grid with unreferenced and duplicate vertices.
            CommonVertex GridVertex(float x, float y) => new()
            {
                Position = new Vector4(x, y, 0, 1),
                Normal = Vector3.UnitZ,
                BiNormal = Vector3.UnitY,
                Tangent = Vector3.UnitX,
                Uv = new Vector2(x / 8, y / 8),
                Uv1 = Vector2.Zero,
                Colour = Vector4.One,
                BoneIndex = [],
                BoneWeight = [],
                WeightCount = 0,
            };

            RmvModel Grid(float offset)
            {
                var vertices = new List<CommonVertex>();
                var indices = new List<ushort>();
                for (var row = 0; row < 8; row++)
                {
                    for (var column = 0; column < 8; column++)
                        vertices.Add(GridVertex(column + offset, row));
                }

                vertices.Add(GridVertex(100, 100));
                vertices.Add(GridVertex(offset, 0));
                for (var row = 0; row < 7; row++)
                {
                    for (var column = 0; column < 7; column++)
                    {
                        var origin = (ushort)(row * 8 + column);
                        indices.AddRange([origin, (ushort)(origin + 1), (ushort)(origin + 8)]);
                        indices.AddRange([(ushort)(origin + 1), (ushort)(origin + 9), (ushort)(origin + 8)]);
                    }
                }

                return new RmvModel { Mesh = new RmvMesh { VertexList = [.. vertices], IndexList = [.. indices] } };
            }

            void AddModel(string label, RmvModel model)
            {
                using var stream = new MemoryStream();
                using var writer = new BinaryWriter(stream);
                foreach (var vertex in model.Mesh.VertexList)
                {
                    writer.Write(vertex.Position.X);
                    writer.Write(vertex.Position.Y);
                    writer.Write(vertex.Position.Z);
                    writer.Write(vertex.Uv.X);
                    writer.Write(vertex.Uv.Y);
                }

                foreach (var index in model.Mesh.IndexList)
                    writer.Write(index);
                writer.Flush();
                Add(label, stream.ToArray());
            }

            var optimized = Grid(0);
            Editors.PackOptimizer.Core.RigidGeometryOptimizer.Optimize(optimized);
            AddModel("geometry", optimized);

            AddModel("merge", MergeOwned(CreateWeightedModel(1f), CreateWeightedModel(2f)));

            // Battle-presence model: two subcultures of one culture, a second culture, a unit without a
            // subculture; pack-affected and unaffected units. Built with the optimizer's default army scope.
            var defaultArmyScope = new Editors.PackOptimizer.Core.PackOptimizerOptions().ArmyScope;
            var (armyState, armyResolution) = PackTextureAtlasValueGateTests.CreateArmyScopeState(
                PackTextureAtlasValueGateTests.ScopeTestCultures,
                new PackTextureAtlasValueGateTests.ScopedRosterUnit("InfantryMissile", ["sc_a1"], ["culture_a"]),
                new PackTextureAtlasValueGateTests.ScopedRosterUnit("InfantryMissile", ["sc_a1", "sc_b"], ["culture_a", "culture_b"]),
                new PackTextureAtlasValueGateTests.ScopedRosterUnit("InfantryMissile", ["sc_a2"], ["culture_a"], PackAffected: false),
                new PackTextureAtlasValueGateTests.ScopedRosterUnit("InfantryMissile", ["sc_b"], ["culture_b"], PackAffected: false),
                new PackTextureAtlasValueGateTests.ScopedRosterUnit("InfantryMissile", [], ["culture_c"], PackAffected: false));
            var armyModel = PackTextureAtlasValueGateTests.BuildArmyScopeModel(
                armyState,
                armyResolution,
                defaultArmyScope.ToString());
            Add("army", Encoding.UTF8.GetBytes(PackTextureAtlasValueGateTests.DescribeArmyModel(armyModel)));

            return Convert.ToHexString(hash.GetHashAndReset());
        }

        [Test]
        public void OutputVersion_GoldenHashIsPinnedForTheCurrentVersion()
        {
            var actual = ComputeOutputGoldenHash();
            Assert.That(ComputeOutputGoldenHash(), Is.EqualTo(actual), "the golden output must be deterministic");

            var version = Editors.PackOptimizer.Core.PackOptimizerVersions.OutputVersion;
            Assert.That(
                OutputGoldenHashes.Keys.Order(),
                Is.EqualTo(Enumerable.Range(1, OutputGoldenHashes.Count)),
                "every version keeps its pinned hash; add the new one, never edit an old one");
            Assert.That(OutputGoldenHashes.ContainsKey(version), Is.True,
                $"OutputVersion is {version} but no golden hash is pinned for it. Add [{version}] = \"{actual}\".");
            Assert.That(
                actual,
                Is.EqualTo(OutputGoldenHashes[version]),
                "The optimizer's output changed for the same input. Bump PackOptimizerVersions.OutputVersion and " +
                $"pin the new hash under the new version: [{version + 1}] = \"{actual}\".");
        }
    }
}
