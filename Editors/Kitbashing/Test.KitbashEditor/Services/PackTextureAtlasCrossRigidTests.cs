using System.Collections;
using System.Reflection;
using System.Xml;
using Microsoft.Xna.Framework;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.LodHeader;
using Shared.GameFormats.RigidModel.MaterialHeaders;
using Shared.GameFormats.RigidModel.Vertex;

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

        private static object CreateBareBatchState()
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
                new Moq.Mock<Shared.Core.PackFiles.IPackFileService>();
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
    }
}
