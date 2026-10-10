using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Editors.KitbasherEditor.Services
{
    internal sealed record Wh3ArmyVisualContractRosterScope(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("key")] string? Key);

    internal sealed record Wh3ArmyVisualContractScenario(
        [property: JsonPropertyName("unitSizeScale")] double UnitSizeScale,
        [property: JsonPropertyName("crewScale")] double CrewScale,
        [property: JsonPropertyName("engineRoundingPolicy")] string EngineRoundingPolicy,
        [property: JsonPropertyName("lodDistribution")] Dictionary<string, double> LodDistribution,
        [property: JsonPropertyName("destructionProbability")] double DestructionProbability,
        [property: JsonPropertyName("destructTransitionProbability")] double DestructTransitionProbability,
        [property: JsonPropertyName("armySlotTemplate")] Dictionary<string, int> ArmySlotTemplate,
        [property: JsonPropertyName("rosterScope")] Wh3ArmyVisualContractRosterScope RosterScope);

    internal sealed record Wh3ArmyVisualContractCounts(
        [property: JsonPropertyName("men")] int Men,
        [property: JsonPropertyName("mounts")] int Mounts,
        [property: JsonPropertyName("engines")] int Engines,
        [property: JsonPropertyName("crew")] int Crew);

    internal sealed record Wh3ArmyVisualContractComponent(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("assetPath")] string AssetPath,
        [property: JsonPropertyName("assetType")] string AssetType,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("lod")] int Lod,
        [property: JsonPropertyName("probability")] double Probability,
        [property: JsonPropertyName("entities")] int Entities);

    internal sealed record Wh3ArmyVisualContractUnit(
        [property: JsonPropertyName("identity")] string Identity,
        [property: JsonPropertyName("mainUnitKey")] string MainUnitKey,
        [property: JsonPropertyName("landUnitKey")] string LandUnitKey,
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("factionKeys")] string[] FactionKeys,
        [property: JsonPropertyName("subcultureKeys")] string[] SubcultureKeys,
        [property: JsonPropertyName("cultureKeys")] string[] CultureKeys,
        [property: JsonPropertyName("counts")] Wh3ArmyVisualContractCounts Counts,
        [property: JsonPropertyName("components")] Wh3ArmyVisualContractComponent[] Components);

    internal sealed record Wh3ArmyVisualContractDocument(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("contractVersion")] int ContractVersion,
        [property: JsonPropertyName("scenario")] Wh3ArmyVisualContractScenario Scenario,
        [property: JsonPropertyName("units")] Wh3ArmyVisualContractUnit[] Units);

    internal static class Wh3ArmyVisualContract
    {
        public const string Kind = "wh3-army-visual-contract";
        public const int ContractVersion = 1;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
        };

        private static readonly HashSet<string> Categories =
        [
            "Unknown",
            "Lord",
            "Hero",
            "InfantryMissile",
            "CavalryChariot",
            "MonsterBeast",
            "ArtilleryWarMachine",
        ];

        private static readonly HashSet<string> Roles =
        [
            "men",
            "mounts",
            "engines",
            "crew",
            "asset",
        ];

        private static readonly HashSet<string> States =
        [
            "live",
            "destroyed",
            "destruct",
        ];

        private static readonly HashSet<string> AssetTypes =
        [
            "variantmeshdefinition",
            "wsmodel",
            "rigid_model_v2",
            "other",
        ];

        private static readonly HashSet<string> RosterScopes =
        [
            "all",
            "culture",
            "faction",
            "mod",
            "optimized-assets",
        ];

        public static Wh3ArmyVisualContractDocument Create(
            Wh3UnitCategoryResolution resolution)
        {
            var scenario = resolution.Scenario;
            return new Wh3ArmyVisualContractDocument(
                Kind,
                ContractVersion,
                new Wh3ArmyVisualContractScenario(
                    scenario.UnitSizeScale,
                    scenario.CrewScale,
                    ToContractRoundingPolicy(scenario.EngineRoundingPolicy),
                    scenario.LodDistribution.ToDictionary(
                        entry => entry.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        entry => entry.Value),
                    scenario.DestructionProbability,
                    scenario.DestructTransitionProbability,
                    scenario.ArmySlotTemplate.ToDictionary(
                        entry => entry.Key.ToString(),
                        entry => entry.Value),
                    new Wh3ArmyVisualContractRosterScope(
                        ToContractRosterScope(scenario.RosterScope),
                        string.IsNullOrWhiteSpace(scenario.RosterScopeKey)
                            ? null
                            : scenario.RosterScopeKey)),
                resolution.RosterUnits.Select(ToContractUnit).ToArray());
        }

        public static string Serialize(Wh3UnitCategoryResolution resolution)
            => Serialize(Create(resolution));

        public static string Serialize(Wh3ArmyVisualContractDocument document)
        {
            Validate(document);
            return JsonSerializer.Serialize(document, JsonOptions);
        }

        public static Wh3ArmyVisualContractDocument Deserialize(string json)
        {
            var document = JsonSerializer.Deserialize<Wh3ArmyVisualContractDocument>(
                json,
                JsonOptions)
                ?? throw new InvalidDataException("Army visual contract JSON is empty.");
            Validate(document);
            return document;
        }

        private static Wh3ArmyVisualContractUnit ToContractUnit(
            Wh3ResolvedUnitVisual unit)
            => new(
                unit.Identity,
                unit.MainUnitKey,
                unit.LandUnitKey,
                unit.Category.ToString(),
                unit.FactionKeys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                unit.SubcultureKeys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                unit.CultureKeys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                new Wh3ArmyVisualContractCounts(
                    unit.VisualCounts.Riders,
                    unit.VisualCounts.Mounts,
                    unit.VisualCounts.Engines,
                    unit.VisualCounts.Crew),
                unit.Components.Select(component =>
                    new Wh3ArmyVisualContractComponent(
                        ToContractRole(component.Role),
                        component.AssetPath,
                        ToContractAssetType(component),
                        ToContractState(component.State),
                        component.Lod,
                        component.ScenarioPresenceProbability,
                        Math.Max(1, unit.VisualCounts.ForRole(component.Role))))
                    .ToArray());

        private static string ToContractRole(Wh3UnitVisualRole role)
            => role switch
            {
                Wh3UnitVisualRole.Mount => "mounts",
                Wh3UnitVisualRole.Engine => "engines",
                Wh3UnitVisualRole.Crew => "crew",
                _ => "men",
            };

        private static string ToContractState(Wh3VisualAssetState state)
            => state switch
            {
                Wh3VisualAssetState.Destroyed => "destroyed",
                Wh3VisualAssetState.Destruct => "destruct",
                _ => "live",
            };

        private static string ToContractAssetType(Wh3ResolvedUnitComponent component)
        {
            if (component.IsVariantMeshDefinition)
                return "variantmeshdefinition";

            return Path.GetExtension(component.AssetPath).ToLowerInvariant() switch
            {
                ".wsmodel" => "wsmodel",
                ".rigid_model_v2" => "rigid_model_v2",
                _ => "other",
            };
        }

        private static string ToContractRoundingPolicy(Wh3EntityRoundingPolicy rounding)
            => rounding switch
            {
                Wh3EntityRoundingPolicy.Round => "round",
                Wh3EntityRoundingPolicy.Floor => "floor",
                _ => "ceil",
            };

        private static string ToContractRosterScope(Wh3RosterScope scope)
            => scope switch
            {
                Wh3RosterScope.SelectedCulture => "culture",
                Wh3RosterScope.SelectedFaction => "faction",
                Wh3RosterScope.ModAffectedUnits => "mod",
                Wh3RosterScope.OptimizedAssetsOnly => "optimized-assets",
                _ => "all",
            };

        private static void Validate(Wh3ArmyVisualContractDocument document)
        {
            if (!document.Kind.Equals(Kind, StringComparison.Ordinal))
                throw new InvalidDataException($"Unexpected army visual contract kind: {document.Kind}.");
            if (document.ContractVersion != ContractVersion)
                throw new InvalidDataException(
                    $"Unsupported army visual contract version: {document.ContractVersion}.");
            if (document.Scenario == null)
                throw new InvalidDataException("Army visual contract scenario is missing.");
            if (document.Units == null)
                throw new InvalidDataException("Army visual contract units are missing.");

            var scenario = document.Scenario;
            if (scenario.UnitSizeScale <= 0 || scenario.CrewScale <= 0)
                throw new InvalidDataException("Army visual scenario scales must be positive.");
            if (scenario.EngineRoundingPolicy is not ("ceil" or "round" or "floor"))
                throw new InvalidDataException("Army visual scenario rounding policy is invalid.");
            if (scenario.LodDistribution == null ||
                scenario.LodDistribution.Count == 0 ||
                scenario.LodDistribution.Any(entry =>
                    !int.TryParse(entry.Key, out var lod) ||
                    lod < 0 ||
                    !double.IsFinite(entry.Value) ||
                    entry.Value < 0 ||
                    entry.Value > 1))
            {
                throw new InvalidDataException("Army visual scenario LOD distribution is invalid.");
            }

            var lodProbabilityTotal = scenario.LodDistribution.Values.Sum();
            if (!double.IsFinite(lodProbabilityTotal) ||
                lodProbabilityTotal <= 0 ||
                Math.Abs(lodProbabilityTotal - 1.0) > 0.000001)
            {
                throw new InvalidDataException(
                    "Army visual scenario LOD distribution must be normalized to a positive total of 1.");
            }

            if (scenario.DestructionProbability is < 0 or > 1 ||
                scenario.DestructTransitionProbability is < 0 or > 1 ||
                scenario.DestructionProbability + scenario.DestructTransitionProbability > 1.000001)
            {
                throw new InvalidDataException("Army visual scenario lifecycle probabilities are invalid.");
            }

            if (scenario.ArmySlotTemplate == null ||
                scenario.ArmySlotTemplate.Any(entry =>
                    !Categories.Contains(entry.Key) ||
                    entry.Key == "Unknown" ||
                    entry.Value < 0))
            {
                throw new InvalidDataException("Army visual scenario slot template is invalid.");
            }

            if (scenario.RosterScope == null ||
                !RosterScopes.Contains(scenario.RosterScope.Kind) ||
                (scenario.RosterScope.Kind is "culture" or "faction" &&
                 string.IsNullOrWhiteSpace(scenario.RosterScope.Key)))
            {
                throw new InvalidDataException("Army visual scenario roster scope is invalid.");
            }

            foreach (var unit in document.Units)
            {
                if (string.IsNullOrWhiteSpace(unit.Identity) ||
                    string.IsNullOrWhiteSpace(unit.LandUnitKey) ||
                    !Categories.Contains(unit.Category) ||
                    unit.Counts == null ||
                    unit.Components == null)
                {
                    throw new InvalidDataException("Army visual contract contains an invalid unit.");
                }

                foreach (var component in unit.Components)
                {
                    if (!Roles.Contains(component.Role) ||
                        !States.Contains(component.State) ||
                        !AssetTypes.Contains(component.AssetType) ||
                        string.IsNullOrWhiteSpace(component.AssetPath) ||
                        component.Lod < 0 ||
                        component.Probability is < 0 or > 1 ||
                        component.Entities < 1)
                    {
                        throw new InvalidDataException(
                            $"Army visual contract contains an invalid component for {unit.Identity}.");
                    }
                }
            }
        }
    }
}
