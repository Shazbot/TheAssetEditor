using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Test.KitbashEditor.Services
{
    public class Wh3ArmyVisualContractTests
    {
        private static Assembly AssetEditorAssembly => Assembly.Load("Editors.KitbasherEditor");

        private static object GetDefaultScenario()
            => AssetEditorAssembly
                .GetType(
                    "Editors.KitbasherEditor.Services.Wh3ArmyVisualScenario",
                    throwOnError: true)!
                .GetProperty(
                    "Default",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetValue(null)
                ?? throw new InvalidOperationException(
                    "Wh3ArmyVisualScenario.Default was not found.");

        private static object CreateScenarioWithLodDistribution(
            object defaultScenario,
            IReadOnlyDictionary<int, double> lodDistribution)
        {
            var scenarioType = defaultScenario.GetType();
            var constructor = scenarioType.GetConstructors(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Single(candidate => candidate.GetParameters().Length == 9);

            object Value(string propertyName)
                => scenarioType.GetProperty(propertyName)?.GetValue(defaultScenario)
                   ?? throw new InvalidOperationException(
                       $"Scenario property '{propertyName}' was not found.");

            return constructor.Invoke(
            [
                Value("UnitSizeScale"),
                Value("CrewScale"),
                Value("EngineRoundingPolicy"),
                lodDistribution,
                Value("DestructionProbability"),
                Value("DestructTransitionProbability"),
                Value("ArmySlotTemplate"),
                Value("RosterScope"),
                scenarioType.GetProperty("RosterScopeKey")?.GetValue(defaultScenario),
            ]);
        }

        private static object NormalizeScenario(object scenario)
            => scenario.GetType()
                .GetMethod(
                    "NormalizeForUse",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(scenario, null)
                ?? throw new InvalidOperationException(
                    "Wh3ArmyVisualScenario.NormalizeForUse was not found.");

        private static IReadOnlyDictionary<int, double> GetLodDistribution(object scenario)
        {
            return (IReadOnlyDictionary<int, double>)(scenario.GetType()
                .GetProperty("LodDistribution")
                ?.GetValue(scenario)
                ?? throw new InvalidOperationException(
                    "Scenario LOD distribution was not found."));
        }

        private static double GetLodWeightedDrawSavings(
            object scenario,
            int lod,
            double unweightedDrawSavings)
        {
            return (double)(scenario.GetType()
                .GetMethod(
                    "GetLodWeightedDrawSavings",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(scenario, [lod, unweightedDrawSavings])
                ?? throw new InvalidOperationException(
                    "Wh3ArmyVisualScenario.GetLodWeightedDrawSavings was not found."));
        }

        private static Exception GetInvocationException(Action action)
        {
            try
            {
                action();
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                return exception.InnerException;
            }

            throw new AssertionException("Expected the reflected call to throw.");
        }

        private static string ReadEmbeddedJson(string fileName)
        {
            var assembly = AssetEditorAssembly;
            var resourceName = assembly.GetManifestResourceNames()
                .Single(name => name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded resource {fileName} was not found.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private static object DeserializeContract(string json)
        {
            var type = AssetEditorAssembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3ArmyVisualContract",
                throwOnError: true)!;
            var method = type.GetMethod(
                "Deserialize",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("Wh3ArmyVisualContract.Deserialize was not found.");
            return method.Invoke(null, [json])
                ?? throw new InvalidOperationException("Wh3ArmyVisualContract.Deserialize returned null.");
        }

        private static string SerializeContract(object document)
        {
            var type = AssetEditorAssembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3ArmyVisualContract",
                throwOnError: true)!;
            var method = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Single(candidate =>
                    candidate.Name == "Serialize" &&
                    candidate.GetParameters().Length == 1 &&
                    candidate.GetParameters()[0].ParameterType.Name == "Wh3ArmyVisualContractDocument");
            return (string)(method.Invoke(null, [document])
                ?? throw new InvalidOperationException("Wh3ArmyVisualContract.Serialize returned null."));
        }

        [Test]
        public void SharedFixture_ParsesAndRoundTripsCanonicalWireShape()
        {
            var fixture = ReadEmbeddedJson("army-visual-contract-v1.json");
            var document = DeserializeContract(fixture);
            var roundTrip = SerializeContract(document);

            using var parsed = JsonDocument.Parse(roundTrip);
            var root = parsed.RootElement;
            var scenario = root.GetProperty("scenario");
            var unit = root.GetProperty("units")[0];
            var components = unit.GetProperty("components");

            Assert.Multiple(() =>
            {
                Assert.That(root.GetProperty("kind").GetString(), Is.EqualTo("wh3-army-visual-contract"));
                Assert.That(root.GetProperty("contractVersion").GetInt32(), Is.EqualTo(1));
                Assert.That(scenario.GetProperty("engineRoundingPolicy").GetString(), Is.EqualTo("ceil"));
                Assert.That(scenario.GetProperty("rosterScope").GetProperty("kind").GetString(), Is.EqualTo("faction"));
                Assert.That(unit.GetProperty("counts").GetProperty("crew").GetInt32(), Is.EqualTo(22));
                Assert.That(components[0].GetProperty("role").GetString(), Is.EqualTo("crew"));
                Assert.That(components[1].GetProperty("assetType").GetString(), Is.EqualTo("wsmodel"));
                Assert.That(components[2].GetProperty("state").GetString(), Is.EqualTo("destroyed"));
            });
        }

        [Test]
        public void SharedSchema_PinsVersionRolesStatesAndScenarioFields()
        {
            using var schema = JsonDocument.Parse(ReadEmbeddedJson("ArmyVisualContract.schema.json"));
            var root = schema.RootElement;
            var definitions = root.GetProperty("$defs");
            var scenarioProperties = definitions
                .GetProperty("scenario")
                .GetProperty("properties");

            Assert.Multiple(() =>
            {
                Assert.That(
                    root.GetProperty("properties").GetProperty("contractVersion").GetProperty("const").GetInt32(),
                    Is.EqualTo(1));
                Assert.That(
                    definitions.GetProperty("role").GetProperty("enum")
                        .EnumerateArray().Select(value => value.GetString()).ToArray(),
                    Is.EqualTo(new[] { "men", "mounts", "engines", "crew", "asset" }));
                Assert.That(
                    definitions.GetProperty("state").GetProperty("enum")
                        .EnumerateArray().Select(value => value.GetString()).ToArray(),
                    Is.EqualTo(new[] { "live", "destroyed", "destruct" }));
                Assert.That(scenarioProperties.TryGetProperty("lodDistribution", out _), Is.True);
                Assert.That(scenarioProperties.TryGetProperty("destructionProbability", out _), Is.True);
                Assert.That(scenarioProperties.TryGetProperty("destructTransitionProbability", out _), Is.True);
                Assert.That(scenarioProperties.TryGetProperty("armySlotTemplate", out _), Is.True);
                Assert.That(scenarioProperties.TryGetProperty("rosterScope", out _), Is.True);
            });
        }

        [Test]
        public void AtlasScenario_DefaultsToPackAffectedRosterScope()
        {
            var scenarioType = GetDefaultScenario().GetType();
            var scenario = scenarioType.GetProperty(
                    "PackAffected",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetValue(null)
                ?? throw new InvalidOperationException("Wh3ArmyVisualScenario.PackAffected was not found.");

            Assert.That(
                scenarioType.GetProperty("RosterScope")?.GetValue(scenario)?.ToString(),
                Is.EqualTo("ModAffectedUnits"));
        }

        [Test]
        public void AtlasScenario_DefaultUsesNormalizedFourLodProfile()
        {
            var distribution = GetLodDistribution(GetDefaultScenario());

            Assert.Multiple(() =>
            {
                Assert.That(distribution.Keys, Is.EquivalentTo(new[] { 0, 1, 2, 3 }));
                Assert.That(distribution[0], Is.EqualTo(0.25).Within(0.000000001));
                Assert.That(distribution[1], Is.EqualTo(0.25).Within(0.000000001));
                Assert.That(distribution[2], Is.EqualTo(0.25).Within(0.000000001));
                Assert.That(distribution[3], Is.EqualTo(0.25).Within(0.000000001));
                Assert.That(distribution.Values.Sum(), Is.EqualTo(1.0).Within(0.000000001));
            });
        }

        [Test]
        public void AtlasScenario_NormalizesCustomWeightsAndDropsZeroEntries()
        {
            var scenario = CreateScenarioWithLodDistribution(
                GetDefaultScenario(),
                new Dictionary<int, double>
                {
                    [0] = 2.0,
                    [1] = 0.0,
                    [3] = 1.0,
                });
            var normalized = GetLodDistribution(NormalizeScenario(scenario));

            Assert.Multiple(() =>
            {
                Assert.That(normalized.Keys, Is.EquivalentTo(new[] { 0, 3 }));
                Assert.That(normalized[0], Is.EqualTo(2.0 / 3.0).Within(0.000000001));
                Assert.That(normalized[3], Is.EqualTo(1.0 / 3.0).Within(0.000000001));
                Assert.That(normalized.Values.Sum(), Is.EqualTo(1.0).Within(0.000000001));
            });
        }

        [Test]
        public void AtlasScenario_MissingLodGetsNoCreditAndDrawSavingsAreWeighted()
        {
            var scenario = GetDefaultScenario();
            var weightedLodSavings = Enumerable.Range(0, 4)
                .Sum(lod => GetLodWeightedDrawSavings(scenario, lod, 4.0));

            Assert.Multiple(() =>
            {
                Assert.That(GetLodWeightedDrawSavings(scenario, 2, 4.0), Is.EqualTo(1.0).Within(0.000000001));
                Assert.That(GetLodWeightedDrawSavings(scenario, 4, 4.0), Is.EqualTo(0.0));
                Assert.That(weightedLodSavings, Is.EqualTo(4.0).Within(0.000000001));
            });
        }

        [Test]
        public void AtlasScenario_RejectsInvalidLodWeights()
        {
            var scenario = GetDefaultScenario();
            var invalidNegative = CreateScenarioWithLodDistribution(
                scenario,
                new Dictionary<int, double> { [0] = -1.0 });
            var invalidTotal = CreateScenarioWithLodDistribution(
                scenario,
                new Dictionary<int, double> { [0] = 0.0, [1] = 0.0 });

            var negativeException = GetInvocationException(() => NormalizeScenario(invalidNegative));
            var totalException = GetInvocationException(() => NormalizeScenario(invalidTotal));

            Assert.Multiple(() =>
            {
                Assert.That(negativeException, Is.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(totalException, Is.TypeOf<ArgumentException>());
            });
        }

        [Test]
        public void SharedContract_RejectsUnnormalizedLodDistribution()
        {
            var fixture = ReadEmbeddedJson("army-visual-contract-v1.json")
                .Replace("\"0\": 0.75", "\"0\": 0.80", StringComparison.Ordinal);

            var exception = GetInvocationException(() => DeserializeContract(fixture));

            Assert.That(exception, Is.TypeOf<InvalidDataException>());
        }
    }
}
