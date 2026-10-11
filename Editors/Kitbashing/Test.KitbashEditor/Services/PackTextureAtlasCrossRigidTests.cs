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
    }
}
