using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Test.KitbashEditor.Services
{
    public class Wh3ArmyVisualContractTests
    {
        private static Assembly AssetEditorAssembly => Assembly.Load("Editors.KitbasherEditor");

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
    }
}
