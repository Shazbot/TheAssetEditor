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
            var packFileService = new Moq.Mock<Shared.Core.PackFiles.IPackFileService>();
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
            var packFileService = new Moq.Mock<Shared.Core.PackFiles.IPackFileService>();
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
                    new Moq.Mock<Shared.Core.PackFiles.IPackFileService>().Object,
                    new Moq.Mock<Shared.Core.PackFiles.Utility.IPackFileContainerLoader>().Object,
                    null!,
                    new Moq.Mock<Shared.Core.Services.IStandardDialogs>().Object,
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
                        new Moq.Mock<Shared.Core.PackFiles.IPackFileService>().Object,
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
    }
}
