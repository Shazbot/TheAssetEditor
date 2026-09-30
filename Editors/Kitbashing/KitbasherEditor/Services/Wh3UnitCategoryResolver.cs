using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;

namespace Editors.KitbasherEditor.Services
{
    internal enum Wh3ArmyUnitCategory
    {
        Unknown,
        Lord,
        Hero,
        InfantryMissile,
        CavalryChariot,
        MonsterBeast,
        ArtilleryWarMachine,
    }

    internal enum Wh3UnitVisualRole
    {
        Men,
        Mount,
        Engine,
        Crew,
    }

    internal enum Wh3VmdConsumerType
    {
        UnitVariant,
        CustomBattleGeneralUniform,
        BattleAgentArtSet,
        Mount,
        Engine,
        ExtraEngine,
    }

    internal enum Wh3VisualAssetState
    {
        Live,
        Destroyed,
        Destruct,
    }

    internal enum Wh3EntityRoundingPolicy
    {
        Ceiling,
        Round,
        Floor,
    }

    internal enum Wh3RosterScope
    {
        AllGameUnits,
        SelectedFaction,
        SelectedCulture,
        ModAffectedUnits,
        OptimizedAssetsOnly,
    }

    internal sealed record Wh3ArmyVisualScenario(
        double UnitSizeScale,
        double CrewScale,
        Wh3EntityRoundingPolicy EngineRoundingPolicy,
        IReadOnlyDictionary<int, double> LodDistribution,
        double DestructionProbability,
        double DestructTransitionProbability,
        IReadOnlyDictionary<Wh3ArmyUnitCategory, int> ArmySlotTemplate,
        Wh3RosterScope RosterScope,
        string? RosterScopeKey)
    {
        public static Wh3ArmyVisualScenario Default { get; } = new(
            0.75,
            0.50,
            Wh3EntityRoundingPolicy.Ceiling,
            new Dictionary<int, double> { [0] = 1.0 },
            0.0,
            0.0,
            new Dictionary<Wh3ArmyUnitCategory, int>
            {
                [Wh3ArmyUnitCategory.Lord] = 1,
                [Wh3ArmyUnitCategory.Hero] = 2,
                [Wh3ArmyUnitCategory.InfantryMissile] = 9,
                [Wh3ArmyUnitCategory.CavalryChariot] = 4,
                [Wh3ArmyUnitCategory.MonsterBeast] = 3,
                [Wh3ArmyUnitCategory.ArtilleryWarMachine] = 2,
            },
            Wh3RosterScope.AllGameUnits,
            null);
    }

    internal sealed record Wh3UnitVisualCounts(
        int Riders,
        int Mounts,
        int Engines,
        int Crew)
    {
        public int ForRole(Wh3UnitVisualRole role)
            => role switch
            {
                Wh3UnitVisualRole.Mount => Mounts,
                Wh3UnitVisualRole.Engine => Engines,
                Wh3UnitVisualRole.Crew => Crew,
                _ => Riders,
            };
    }

    internal sealed record Wh3VmdUsageProvenance(
        Wh3VmdConsumerType ConsumerType,
        string RootVmdPath,
        string ParentVmdPath,
        string FactionKey,
        string SubcultureKey,
        string CultureKey,
        string UniformName,
        string ArtSetId)
    {
        public bool IsTransitiveChild => ParentVmdPath.Length != 0;
    }

    internal sealed record Wh3UnitCategoryUsage(
        string VmdPath,
        string MainUnitKey,
        string LandUnitKey,
        string Caste,
        string LandCategory,
        string UiGroupKey,
        int NumMen,
        Wh3ArmyUnitCategory Category,
        Wh3UnitVisualRole VisualRole,
        int EntityCount,
        Wh3UnitVisualCounts VisualCounts)
    {
        public Wh3VmdConsumerType ConsumerType { get; init; } =
            Wh3VmdConsumerType.UnitVariant;
        public string RootVmdPath { get; init; } = string.Empty;
        public string ParentVmdPath { get; init; } = string.Empty;
        public IReadOnlyList<Wh3VmdUsageProvenance> Provenance { get; init; } = [];
        public bool IsTransitiveChild => ParentVmdPath.Length != 0;
    }

    internal sealed record Wh3ResolvedAgentVariant(
        string VariantName,
        Wh3VmdConsumerType ConsumerType,
        string FactionKey,
        string SubcultureKey,
        string CultureKey,
        string UniformName,
        string ArtSetId);

    internal sealed record Wh3AgentVisualResolution(
        IReadOnlyList<Wh3ResolvedAgentVariant> Variants,
        bool HasAuthoritativeVisualPath,
        IReadOnlyList<string> Issues);

    internal sealed record Wh3UnitDirectAssetUsage(
        string AssetPath,
        string MainUnitKey,
        string LandUnitKey,
        Wh3ArmyUnitCategory Category,
        Wh3UnitVisualRole VisualRole,
        int EntityCount,
        Wh3UnitVisualCounts VisualCounts,
        Wh3VisualAssetState State,
        int Lod,
        double ScenarioPresenceProbability)
    {
        public double ExpectedLiveBattlePresence
            => State == Wh3VisualAssetState.Live ? ScenarioPresenceProbability : 0.0;
    }

    internal sealed record Wh3ResolvedUnitComponent(
        Wh3UnitVisualRole Role,
        string AssetPath,
        bool IsVariantMeshDefinition,
        Wh3VisualAssetState State,
        int Lod,
        double ScenarioPresenceProbability);

    internal sealed record Wh3ResolvedUnitVisual(
        string Identity,
        string MainUnitKey,
        string LandUnitKey,
        Wh3ArmyUnitCategory Category,
        Wh3UnitVisualCounts VisualCounts,
        IReadOnlyList<Wh3ResolvedUnitComponent> Components)
    {
        public IReadOnlySet<string> FactionKeys { get; init; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlySet<string> SubcultureKeys { get; init; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlySet<string> CultureKeys { get; init; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed record Wh3UnitCategoryResolution(
        IReadOnlyDictionary<string, IReadOnlyList<Wh3UnitCategoryUsage>> UsagesByVmd,
        IReadOnlyDictionary<string, IReadOnlyList<Wh3UnitCategoryUsage>> DirectUsagesByVmd,
        IReadOnlyDictionary<string, IReadOnlyList<Wh3UnitDirectAssetUsage>> DirectAssetUsagesByPath,
        IReadOnlyList<Wh3ResolvedUnitVisual> RosterUnits,
        Wh3ArmyVisualScenario Scenario,
        IReadOnlyList<string> UnresolvedVmdRoots,
        IReadOnlyDictionary<string, int> ParsedRowsByTable,
        int TableFilesRead,
        int DirectlyResolvedVmdCount,
        int PropagatedVmdCount,
        bool IsGameplayResolutionHealthy,
        string GameplayResolutionHealthMessage,
        IReadOnlyList<string> Diagnostics)
    {
        public IReadOnlyList<Wh3UnitCategoryUsage> GetUsages(string vmdPath)
            => UsagesByVmd.TryGetValue(NormalizePath(vmdPath), out var usages)
                ? usages
                : [];

        private static string NormalizePath(string value)
            => value.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
    }

    /// <summary>
    /// Resolves a variantmeshdefinition back to the gameplay units that can use it:
    /// unit_variants.unit -> land_units.key, unit_variants.variant -> variants.variant_name,
    /// variants.variant_filename -> VMD, and main_units.land_unit -> land_units.key.
    ///
    /// The packed DB rows are decoded by table version and schema field name.  The schema subset is
    /// generated from WHMM's schema_wh3.json rather than relying on hard-coded column indices.
    /// </summary>
    internal static class Wh3UnitCategoryResolver
    {
        private const string MainUnitsTable = "main_units_tables";
        private const string LandUnitsTable = "land_units_tables";
        private const string UnitVariantsTable = "unit_variants_tables";
        private const string VariantsTable = "variants_tables";
        private const string UiUnitGroupingsTable = "ui_unit_groupings_tables";
        private const string UiUnitGroupParentsTable = "ui_unit_group_parents_tables";
        private const string MountsTable = "mounts_tables";
        private const string BattlefieldEnginesTable = "battlefield_engines_tables";
        private const string ExtraEnginesTable = "land_units_to_extra_engines_tables";
        private const string WarscapeAnimatedLodTable = "warscape_animated_lod_tables";
        private const string FactionsTable = "factions_tables";
        private const string CulturesSubculturesTable = "cultures_subcultures_tables";
        private const string CustomBattlePermissionsTable = "units_custom_battle_permissions_tables";
        private const string AgentSubtypesTable = "agent_subtypes_tables";
        private const string AgentSubtypeSubcultureOverridesTable = "agent_subtype_subculture_overrides_tables";
        private const string CampaignCharacterArtSetsTable = "campaign_character_art_sets_tables";
        private const string CampaignCharacterArtsTable = "campaign_character_arts_tables";
        private const string AgentUniformsTable = "agent_uniforms_tables";

        // The optimizer models a large-size battle.  WHMM uses the same scalar for the
        // entity count shown by its unit viewer.  Crew is a separate visual population for
        // crewed engines; it is intentionally derived from num_men, never from ammunition.

        private static readonly string[] RequiredTables =
        [
            MainUnitsTable,
            LandUnitsTable,
            UnitVariantsTable,
            VariantsTable,
            UiUnitGroupingsTable,
            UiUnitGroupParentsTable,
            MountsTable,
            BattlefieldEnginesTable,
            ExtraEnginesTable,
            WarscapeAnimatedLodTable,
            FactionsTable,
            CulturesSubculturesTable,
            CustomBattlePermissionsTable,
            AgentSubtypesTable,
            AgentSubtypeSubcultureOverridesTable,
            CampaignCharacterArtSetsTable,
            CampaignCharacterArtsTable,
            AgentUniformsTable,
        ];

        private static readonly string[] GameplayCriticalTables =
        [
            MainUnitsTable,
            LandUnitsTable,
            UnitVariantsTable,
            VariantsTable,
            MountsTable,
            BattlefieldEnginesTable,
        ];

        private static readonly string[] AgentGameplayCriticalTables =
        [
            AgentSubtypesTable,
            AgentSubtypeSubcultureOverridesTable,
            CampaignCharacterArtSetsTable,
            CampaignCharacterArtsTable,
            AgentUniformsTable,
            CustomBattlePermissionsTable,
            FactionsTable,
            CulturesSubculturesTable,
        ];

        private static readonly Lazy<SchemaRoot> Schema = new(LoadSchema);

        public static Wh3UnitCategoryResolution Resolve(
            IPackFileService packFileService,
            IPackFileContainer source,
            IReadOnlyCollection<string> rootVmdPaths,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> childVmdsByVmd,
            CancellationToken cancellationToken,
            Wh3ArmyVisualScenario? scenario = null)
        {
            var activeScenario = scenario ?? Wh3ArmyVisualScenario.Default;
            var diagnostics = new List<string>();
            var parsedRowsByTable = RequiredTables.ToDictionary(
                table => table,
                _ => 0,
                StringComparer.OrdinalIgnoreCase);
            var effectiveRows = RequiredTables.ToDictionary(
                table => table,
                _ => new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

            // Resolve against the game's CA database plus the selected source pack only.
            // Other editable/mod packs that happen to be open in Asset Editor must not change
            // classification of the pack being processed.
            var containers = packFileService.GetAllPackfileContainers()
                .Where(container =>
                    container.IsCaPackFile &&
                    !IsSameContainer(container, source))
                .ToList();
            containers.Add(source);

            var tableFilesRead = 0;
            foreach (var container in containers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var tableName in RequiredTables)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var folder = $"db\\{tableName}";
                    var tableFiles = container.GetDirectoryContent(folder);
                    foreach (var (path, file) in tableFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            var rows = DecodeTable(
                                tableName,
                                file.DataSource.ReadData(),
                                path,
                                diagnostics);
                            tableFilesRead++;
                            parsedRowsByTable[tableName] += rows.Count;

                            ApplyEffectiveRows(
                                tableName,
                                effectiveRows[tableName],
                                rows);
                        }
                        catch (Exception ex) when (
                            ex is InvalidDataException or
                            EndOfStreamException or
                            ArgumentException or
                            OverflowException)
                        {
                            diagnostics.Add(
                                $"Failed to decode {path} from {DescribeContainer(container)}: " +
                                ex.Message.Replace("\r", " ").Replace("\n", " "));
                        }
                    }
                }
            }

            var mainRows = effectiveRows[MainUnitsTable].Values.ToList();
            var landRows = effectiveRows[LandUnitsTable];
            var unitVariantRows = effectiveRows[UnitVariantsTable].Values.ToList();
            var variantRows = effectiveRows[VariantsTable];
            var uiUnitGroupings = effectiveRows[UiUnitGroupingsTable];
            var uiUnitGroupParents = effectiveRows[UiUnitGroupParentsTable];
            var mountRows = effectiveRows[MountsTable];
            var engineRows = effectiveRows[BattlefieldEnginesTable];
            var factionRows = effectiveRows[FactionsTable];
            var cultureRows = effectiveRows[CulturesSubculturesTable];
            var customBattlePermissionRows = effectiveRows[CustomBattlePermissionsTable].Values.ToList();
            var agentSubtypeRows = effectiveRows[AgentSubtypesTable];
            var agentSubtypeOverrideRows = effectiveRows[AgentSubtypeSubcultureOverridesTable];
            var campaignCharacterArtSetRows = effectiveRows[CampaignCharacterArtSetsTable];
            var campaignCharacterArtRows = effectiveRows[CampaignCharacterArtsTable].Values.ToList();
            var agentUniformRows = effectiveRows[AgentUniformsTable];
            var animatedLodRowsByKey = effectiveRows[WarscapeAnimatedLodTable].Values
                .Where(row => Get(row, "animated").Length != 0)
                .GroupBy(row => Get(row, "animated"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(row => Get(row, "filename"))
                        .Where(path => path.Length != 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);
            var extraEngineRowsByLandUnit = effectiveRows[ExtraEnginesTable].Values
                .Where(row => Get(row, "land_unit").Length != 0)
                .GroupBy(row => Get(row, "land_unit"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.ToList(),
                    StringComparer.OrdinalIgnoreCase);

            var mainByLandUnit = mainRows
                .Where(row => Get(row, "land_unit").Length != 0)
                .GroupBy(row => Get(row, "land_unit"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.ToList(),
                    StringComparer.OrdinalIgnoreCase);

            var factionsByMainUnit = new Dictionary<string, HashSet<string>>(
                StringComparer.OrdinalIgnoreCase);
            var factionsByLandUnit = new Dictionary<string, HashSet<string>>(
                StringComparer.OrdinalIgnoreCase);

            static void AddScopedFaction(
                Dictionary<string, HashSet<string>> target,
                string unitKey,
                string factionKey)
            {
                if (string.IsNullOrWhiteSpace(unitKey) || string.IsNullOrWhiteSpace(factionKey))
                    return;
                if (!target.TryGetValue(unitKey, out var factions))
                {
                    factions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    target[unitKey] = factions;
                }
                factions.Add(factionKey);
            }

            foreach (var permission in customBattlePermissionRows)
                AddScopedFaction(factionsByMainUnit, Get(permission, "unit"), Get(permission, "faction"));

            // unit_variants.faction is visual-specific rather than a complete recruitment
            // permission, but it is a useful fallback for mod units that omit custom-battle
            // permission rows.
            foreach (var unitVariant in unitVariantRows)
            {
                var scopedLandUnitKey = Get(unitVariant, "unit");
                var factionKey = Get(unitVariant, "faction");
                AddScopedFaction(factionsByLandUnit, scopedLandUnitKey, factionKey);
                if (mainByLandUnit.TryGetValue(scopedLandUnitKey, out var scopedMains))
                {
                    foreach (var scopedMain in scopedMains)
                        AddScopedFaction(factionsByMainUnit, Get(scopedMain, "unit"), factionKey);
                }
            }

            var subcultureByFaction = factionRows.Values
                .Where(row => Get(row, "key").Length != 0)
                .ToDictionary(
                    row => Get(row, "key"),
                    row => Get(row, "subculture"),
                    StringComparer.OrdinalIgnoreCase);
            var cultureBySubculture = cultureRows.Values
                .Where(row => Get(row, "subculture").Length != 0)
                .ToDictionary(
                    row => Get(row, "subculture"),
                    row => Get(row, "culture"),
                    StringComparer.OrdinalIgnoreCase);

            var usagesByVmd = new Dictionary<string, Dictionary<string, Wh3UnitCategoryUsage>>(
                StringComparer.OrdinalIgnoreCase);
            var directAssetUsagesByPath = new Dictionary<
                string,
                Dictionary<string, Wh3UnitDirectAssetUsage>>(
                StringComparer.OrdinalIgnoreCase);

            void AddVariantUsage(
                string variantName,
                string mainUnitKey,
                string landUnitKey,
                string caste,
                string landCategory,
                string uiGroupKey,
                Wh3ArmyUnitCategory category,
                Wh3UnitVisualRole visualRole,
                Wh3VmdConsumerType consumerType,
                int numMen,
                Wh3UnitVisualCounts visualCounts,
                string factionKey = "",
                string subcultureKey = "",
                string cultureKey = "",
                string uniformName = "",
                string artSetId = "")
            {
                if (variantName.Length == 0 ||
                    !variantRows.TryGetValue(variantName, out var variant))
                {
                    return;
                }

                var componentVmdPath = ToVariantMeshDefinitionPath(
                    Get(variant, "variant_filename"));
                if (componentVmdPath.Length == 0)
                    return;

                var entityCount = visualCounts.ForRole(visualRole);
                if (entityCount <= 0)
                    return;

                AddUsage(
                    usagesByVmd,
                    new Wh3UnitCategoryUsage(
                        componentVmdPath,
                        mainUnitKey,
                        landUnitKey,
                        caste,
                        landCategory,
                        uiGroupKey,
                        numMen,
                        category,
                        visualRole,
                        entityCount,
                        visualCounts)
                    {
                        ConsumerType = consumerType,
                        RootVmdPath = componentVmdPath,
                        Provenance =
                        [
                            new Wh3VmdUsageProvenance(
                                consumerType,
                                componentVmdPath,
                                string.Empty,
                                factionKey,
                                subcultureKey,
                                cultureKey,
                                uniformName,
                                artSetId),
                        ],
                    });
            }

            void AddDirectAssetUsage(
                string assetPath,
                string mainUnitKey,
                string landUnitKey,
                Wh3ArmyUnitCategory category,
                Wh3UnitVisualRole visualRole,
                Wh3UnitVisualCounts visualCounts,
                Wh3VisualAssetState stateValue,
                int lod,
                double scenarioPresenceProbability)
            {
                assetPath = NormalizePath(assetPath);
                if (assetPath.Length == 0 || !source.ContainsFile(assetPath))
                    return;

                var entityCount = visualCounts.ForRole(visualRole);
                if (entityCount <= 0)
                    return;

                if (!directAssetUsagesByPath.TryGetValue(assetPath, out var usages))
                {
                    usages = new Dictionary<string, Wh3UnitDirectAssetUsage>(
                        StringComparer.OrdinalIgnoreCase);
                    directAssetUsagesByPath[assetPath] = usages;
                }

                var identity = string.IsNullOrWhiteSpace(mainUnitKey)
                    ? $"land:{landUnitKey}"
                    : $"main:{mainUnitKey}";
                identity += $"|role:{visualRole}|state:{stateValue}|lod:{Math.Max(0, lod)}";
                if (usages.TryGetValue(identity, out var existing))
                {
                    scenarioPresenceProbability = Math.Max(
                        existing.ScenarioPresenceProbability,
                        scenarioPresenceProbability);
                }

                usages[identity] = new Wh3UnitDirectAssetUsage(
                    assetPath,
                    mainUnitKey,
                    landUnitKey,
                    category,
                    visualRole,
                    entityCount,
                    visualCounts,
                    stateValue,
                    Math.Max(0, lod),
                    Math.Clamp(scenarioPresenceProbability, 0.0, 1.0));
            }

            void AddEngineAssetUsages(
                IReadOnlyDictionary<string, string> engine,
                string mainUnitKey,
                string landUnitKey,
                Wh3ArmyUnitCategory category,
                Wh3UnitVisualCounts visualCounts)
            {
                foreach (var field in new[]
                         {
                             "model",
                             "destroyed_model",
                             "destruct_model",
                             "destruction_animation",
                         })
                {
                    var stateValue = GetDirectEngineAssetState(field);
                    foreach (var assetPath in ResolveEngineAssetPaths(
                                 Get(engine, field),
                                 animatedLodRowsByKey))
                    {
                        var lod = ResolveAssetLod(assetPath);
                        AddDirectAssetUsage(
                            assetPath,
                            mainUnitKey,
                            landUnitKey,
                            category,
                            Wh3UnitVisualRole.Engine,
                            visualCounts,
                            stateValue,
                            lod,
                            GetScenarioPresenceProbability(activeScenario, stateValue, lod));
                    }
                }
            }

            var unitVariantsByLandUnit = unitVariantRows
                .Where(row => Get(row, "unit").Length != 0)
                .GroupBy(row => Get(row, "unit"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(row => Get(row, "variant"))
                        .Where(variant => variant.Length != 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            var agentPrimaryMainUnits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var agentFallbackMainUnits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var agentResolutionIssues = new List<string>();
            var agentResolutionAttempted = false;

            void AddMainUnitVisualUsages(
                IReadOnlyDictionary<string, string> main,
                string landUnitKey,
                IReadOnlyDictionary<string, string> land,
                IReadOnlyList<string> fallbackVariantNames)
            {
                var mainUnitKey = Get(main, "unit");
                var caste = Get(main, "caste");
                var landCategory = Get(land, "category");
                var uiGroupKey = ResolveUiGroupKey(
                    main,
                    caste,
                    uiUnitGroupings,
                    uiUnitGroupParents);
                var category = Classify(caste, landCategory, uiGroupKey);
                var numMen = TryParseInt(Get(main, "num_men"), out var parsedNumMen)
                    ? Math.Max(1, parsedNumMen)
                    : 1;
                var engineKey = Get(land, "engine");
                engineRows.TryGetValue(engineKey, out var engine);
                var visualCounts = ResolveVisualCountsForScenario(main, land, engine, activeScenario);
                var mainVisualRole = visualCounts.Crew > 0
                    ? Wh3UnitVisualRole.Crew
                    : Wh3UnitVisualRole.Men;

                var handledPrimaryVisuals = false;
                if (category is Wh3ArmyUnitCategory.Lord or Wh3ArmyUnitCategory.Hero)
                {
                    agentResolutionAttempted = true;

                    var factions = factionsByMainUnit.TryGetValue(mainUnitKey, out var mainFactions)
                        ? (IReadOnlyCollection<string>)mainFactions
                        : Array.Empty<string>();
                    var agentResolution = ResolveBattleAgentVisuals(
                        mainUnitKey,
                        factions,
                        subcultureByFaction,
                        cultureBySubculture,
                        customBattlePermissionRows,
                        agentSubtypeRows,
                        agentSubtypeOverrideRows,
                        campaignCharacterArtSetRows,
                        campaignCharacterArtRows,
                        agentUniformRows,
                        variantRows);
                    agentResolutionIssues.AddRange(agentResolution.Issues);

                    foreach (var agentVariant in agentResolution.Variants)
                    {
                        AddVariantUsage(
                            agentVariant.VariantName,
                            mainUnitKey,
                            landUnitKey,
                            caste,
                            landCategory,
                            uiGroupKey,
                            category,
                            mainVisualRole,
                            agentVariant.ConsumerType,
                            numMen,
                            visualCounts,
                            agentVariant.FactionKey,
                            agentVariant.SubcultureKey,
                            agentVariant.CultureKey,
                            agentVariant.UniformName,
                            agentVariant.ArtSetId);
                    }

                    if (agentResolution.Variants.Count != 0)
                    {
                        handledPrimaryVisuals = true;
                        if (mainUnitKey.Length != 0)
                            agentPrimaryMainUnits.Add(mainUnitKey);
                    }
                    else if (agentResolution.HasAuthoritativeVisualPath)
                    {
                        // Do not hide a broken authoritative agent path behind unit_variants.
                        handledPrimaryVisuals = true;
                    }
                    else if (fallbackVariantNames.Count != 0 && mainUnitKey.Length != 0)
                    {
                        agentFallbackMainUnits.Add(mainUnitKey);
                    }
                }

                if (!handledPrimaryVisuals)
                {
                    foreach (var primaryVariantName in fallbackVariantNames)
                    {
                        AddVariantUsage(
                            primaryVariantName,
                            mainUnitKey,
                            landUnitKey,
                            caste,
                            landCategory,
                            uiGroupKey,
                            category,
                            mainVisualRole,
                            Wh3VmdConsumerType.UnitVariant,
                            numMen,
                            visualCounts);
                    }
                }

                var mountKey = Get(land, "mount");
                if (mountKey.Length != 0 &&
                    mountRows.TryGetValue(mountKey, out var mount))
                {
                    AddVariantUsage(
                        Get(mount, "variant"),
                        mainUnitKey,
                        landUnitKey,
                        caste,
                        landCategory,
                        uiGroupKey,
                        category,
                        Wh3UnitVisualRole.Mount,
                        Wh3VmdConsumerType.Mount,
                        numMen,
                        visualCounts);
                }

                if (engineKey.Length != 0 && engine != null)
                {
                    AddVariantUsage(
                        Get(engine, "variant"),
                        mainUnitKey,
                        landUnitKey,
                        caste,
                        landCategory,
                        uiGroupKey,
                        category,
                        Wh3UnitVisualRole.Engine,
                        Wh3VmdConsumerType.Engine,
                        numMen,
                        visualCounts);

                    AddEngineAssetUsages(
                        engine,
                        mainUnitKey,
                        landUnitKey,
                        category,
                        visualCounts);
                }

                if (!extraEngineRowsByLandUnit.TryGetValue(landUnitKey, out var extraEngines))
                    return;

                foreach (var extraEngineRow in extraEngines)
                {
                    var extraEngineKey = Get(extraEngineRow, "battle_engine");
                    if (extraEngineKey.Length == 0 ||
                        !engineRows.TryGetValue(extraEngineKey, out var extraEngine))
                    {
                        continue;
                    }

                    var extraEngineVisualCounts =
                        ResolveExtraEngineVisualCounts(visualCounts);
                    AddVariantUsage(
                        Get(extraEngine, "variant"),
                        mainUnitKey,
                        landUnitKey,
                        caste,
                        landCategory,
                        uiGroupKey,
                        category,
                        Wh3UnitVisualRole.Engine,
                        Wh3VmdConsumerType.ExtraEngine,
                        numMen,
                        extraEngineVisualCounts);

                    AddEngineAssetUsages(
                        extraEngine,
                        mainUnitKey,
                        landUnitKey,
                        category,
                        extraEngineVisualCounts);
                }
            }

            // Unit-first resolution is authoritative for gameplay usage. Lords/heroes use
            // their battle agent uniform chain first; unit_variants remains their fallback.
            foreach (var main in mainRows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var landUnitKey = Get(main, "land_unit");
                if (landUnitKey.Length == 0 ||
                    !landRows.TryGetValue(landUnitKey, out var land))
                {
                    continue;
                }

                var fallbackVariantNames = unitVariantsByLandUnit.TryGetValue(
                    landUnitKey,
                    out var variantsForLandUnit)
                    ? variantsForLandUnit
                    : Array.Empty<string>();
                AddMainUnitVisualUsages(
                    main,
                    landUnitKey,
                    land,
                    fallbackVariantNames);
            }

            // Orphan unit_variants rows are deliberately not gameplay authority. Keep them
            // visible for diagnostics, but only pack-wide atlas mode may process their VMDs.
            var orphanUnitVariantRows = unitVariantRows
                .Where(row =>
                {
                    var landUnitKey = Get(row, "unit");
                    return landUnitKey.Length != 0 && !mainByLandUnit.ContainsKey(landUnitKey);
                })
                .ToList();
            if (orphanUnitVariantRows.Count != 0)
            {
                var orphanVmdCount = orphanUnitVariantRows
                    .Select(row => Get(row, "variant"))
                    .Where(variantName => variantName.Length != 0)
                    .Select(variantName =>
                        variantRows.TryGetValue(variantName, out var variant)
                            ? ToVariantMeshDefinitionPath(Get(variant, "variant_filename"))
                            : string.Empty)
                    .Where(path => path.Length != 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                diagnostics.Add(
                    $"Ignored {orphanUnitVariantRows.Count:N0} orphan unit_variants row(s) " +
                    $"with no main_units gameplay consumer; {orphanVmdCount:N0} VMD(s) remain " +
                    $"eligible only in pack-wide atlas mode.");
            }

            diagnostics.Add(
                $"Battle agent visual resolution: {agentPrimaryMainUnits.Count:N0} main unit(s) " +
                $"resolved through explicit/scoped agent visuals; " +
                $"{agentFallbackMainUnits.Count:N0} used unit_variants fallback; " +
                $"{agentResolutionIssues.Count:N0} semantic issue(s).");
            foreach (var issue in agentResolutionIssues)
                diagnostics.Add($"Agent visual resolution issue: {issue}");

            // Seed the roster directly from effective main_units + land_units so the
            // denominator does not depend on a unit having a VMD/asset that happens to be
            // discoverable in the selected pack. Visual usages are attached afterwards.
            var rosterSeeds = new List<Wh3ResolvedUnitVisual>();
            foreach (var main in mainRows)
            {
                var landUnitKey = Get(main, "land_unit");
                if (landUnitKey.Length == 0 ||
                    !landRows.TryGetValue(landUnitKey, out var land))
                {
                    continue;
                }

                var mainUnitKey = Get(main, "unit");
                if (mainUnitKey.Length == 0)
                    mainUnitKey = landUnitKey;

                var caste = Get(main, "caste");
                var landCategory = Get(land, "category");
                var uiGroupKey = ResolveUiGroupKey(
                    main,
                    caste,
                    uiUnitGroupings,
                    uiUnitGroupParents);
                var engineKey = Get(land, "engine");
                engineRows.TryGetValue(engineKey, out var engine);
                var visualCounts = ResolveVisualCountsForScenario(
                    main,
                    land,
                    engine,
                    activeScenario);
                var identity = $"main:{mainUnitKey.Trim().ToLowerInvariant()}";

                var factions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (factionsByMainUnit.TryGetValue(mainUnitKey, out var mainFactions))
                    factions.UnionWith(mainFactions);
                if (factionsByLandUnit.TryGetValue(landUnitKey, out var landFactions))
                    factions.UnionWith(landFactions);

                var subcultures = factions
                    .Select(faction => subcultureByFaction.GetValueOrDefault(faction))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var cultures = subcultures
                    .Select(subculture => cultureBySubculture.GetValueOrDefault(subculture))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                rosterSeeds.Add(new Wh3ResolvedUnitVisual(
                    identity,
                    mainUnitKey,
                    landUnitKey,
                    Classify(caste, landCategory, uiGroupKey),
                    visualCounts,
                    [])
                {
                    FactionKeys = factions,
                    SubcultureKeys = subcultures,
                    CultureKeys = cultures,
                });
            }

            // Build the complete DB-derived unit roster before filtering to VMDs discovered in
            // the selected pack. This keeps the army denominator stable when a pack happens to
            // contain only a subset of the game's visual definitions.
            var rosterUnits = BuildResolvedRoster(
                usagesByVmd,
                directAssetUsagesByPath,
                rosterSeeds);

            var normalizedRoots = rootVmdPaths
                .Select(NormalizePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            rosterUnits = FilterRosterForScenario(
                rosterUnits,
                activeScenario,
                source,
                normalizedRoots,
                diagnostics);

            var directSourceVmds = normalizedRoots
                .Where(root => usagesByVmd.ContainsKey(root))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var directUsagesByVmd = directSourceVmds
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    path => path,
                    path => (IReadOnlyList<Wh3UnitCategoryUsage>)usagesByVmd[path].Values
                        .OrderBy(usage => usage.Category)
                        .ThenBy(usage => usage.MainUnitKey, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(usage => usage.LandUnitKey, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);

            PropagateUsagesToChildVmds(
                usagesByVmd,
                directSourceVmds,
                childVmdsByVmd,
                cancellationToken);

            var filtered = new Dictionary<string, IReadOnlyList<Wh3UnitCategoryUsage>>(
                StringComparer.OrdinalIgnoreCase);
            var unresolved = new List<string>();
            foreach (var root in normalizedRoots)
            {
                if (!usagesByVmd.TryGetValue(root, out var usages) || usages.Count == 0)
                {
                    unresolved.Add(root);
                    continue;
                }

                filtered[root] = usages.Values
                    .OrderBy(usage => usage.Category)
                    .ThenBy(usage => usage.MainUnitKey, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(usage => usage.LandUnitKey, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var gameplayResolutionHealthMessage =
                GetGameplayResolutionHealthIssue(
                    parsedRowsByTable,
                    diagnostics,
                    agentResolutionAttempted,
                    agentResolutionIssues);

            return new Wh3UnitCategoryResolution(
                filtered,
                directUsagesByVmd,
                directAssetUsagesByPath.ToDictionary(
                    entry => entry.Key,
                    entry => (IReadOnlyList<Wh3UnitDirectAssetUsage>)entry.Value.Values
                        .OrderBy(usage => usage.MainUnitKey, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(usage => usage.LandUnitKey, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(usage => usage.VisualRole)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase),
                rosterUnits,
                activeScenario,
                unresolved,
                parsedRowsByTable,
                tableFilesRead,
                directSourceVmds.Count,
                Math.Max(0, filtered.Count - directSourceVmds.Count),
                gameplayResolutionHealthMessage.Length == 0,
                gameplayResolutionHealthMessage,
                diagnostics);
        }

        private static IReadOnlyList<Wh3ResolvedUnitVisual> BuildResolvedRoster(
            IReadOnlyDictionary<string, Dictionary<string, Wh3UnitCategoryUsage>> usagesByVmd,
            IReadOnlyDictionary<string, Dictionary<string, Wh3UnitDirectAssetUsage>> directAssetsByPath,
            IEnumerable<Wh3ResolvedUnitVisual> seeds)
        {
            var builders = new Dictionary<
                string,
                (string MainUnitKey, string LandUnitKey, Wh3ArmyUnitCategory Category,
                    Wh3UnitVisualCounts Counts, List<Wh3ResolvedUnitComponent> Components)>(
                StringComparer.OrdinalIgnoreCase);

            static string Identity(string mainUnitKey, string landUnitKey)
                => string.IsNullOrWhiteSpace(mainUnitKey)
                    ? $"land:{landUnitKey}".ToLowerInvariant()
                    : $"main:{mainUnitKey}".ToLowerInvariant();

            foreach (var seed in seeds)
            {
                builders[seed.Identity] = (
                    seed.MainUnitKey,
                    seed.LandUnitKey,
                    seed.Category,
                    seed.VisualCounts,
                    seed.Components.ToList());
            }

            foreach (var (vmdPathValue, usages) in usagesByVmd)
            {
                var vmdPath = NormalizePath(vmdPathValue);
                foreach (var usage in usages.Values)
                {
                    var identity = Identity(usage.MainUnitKey, usage.LandUnitKey);
                    if (!builders.TryGetValue(identity, out var builder))
                    {
                        builder = (
                            usage.MainUnitKey,
                            usage.LandUnitKey,
                            usage.Category,
                            usage.VisualCounts,
                            []);
                    }

                    if (!builder.Components.Any(component =>
                            component.IsVariantMeshDefinition &&
                            component.Role == usage.VisualRole &&
                            component.AssetPath.Equals(vmdPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        builder.Components.Add(new Wh3ResolvedUnitComponent(
                            usage.VisualRole,
                            vmdPath,
                            true,
                            Wh3VisualAssetState.Live,
                            0,
                            1.0));
                    }

                    builders[identity] = builder;
                }
            }

            foreach (var (assetPathValue, usages) in directAssetsByPath)
            {
                var assetPath = NormalizePath(assetPathValue);
                foreach (var usage in usages.Values)
                {
                    var identity = Identity(usage.MainUnitKey, usage.LandUnitKey);
                    if (!builders.TryGetValue(identity, out var builder))
                    {
                        builder = (
                            usage.MainUnitKey,
                            usage.LandUnitKey,
                            usage.Category,
                            usage.VisualCounts,
                            []);
                    }

                    if (!builder.Components.Any(component =>
                            !component.IsVariantMeshDefinition &&
                            component.Role == usage.VisualRole &&
                            component.State == usage.State &&
                            component.Lod == usage.Lod &&
                            component.AssetPath.Equals(assetPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        builder.Components.Add(new Wh3ResolvedUnitComponent(
                            usage.VisualRole,
                            assetPath,
                            false,
                            usage.State,
                            usage.Lod,
                            usage.ScenarioPresenceProbability));
                    }

                    builders[identity] = builder;
                }
            }

            return builders
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new Wh3ResolvedUnitVisual(
                    entry.Key,
                    entry.Value.MainUnitKey,
                    entry.Value.LandUnitKey,
                    entry.Value.Category,
                    entry.Value.Counts,
                    entry.Value.Components
                        .OrderBy(component => component.Role)
                        .ThenBy(component => component.State)
                        .ThenBy(component => component.Lod)
                        .ThenBy(component => component.AssetPath, StringComparer.OrdinalIgnoreCase)
                        .ToArray()))
                .ToArray();
        }

        private static IReadOnlyList<Wh3ResolvedUnitVisual> FilterRosterForScenario(
            IReadOnlyList<Wh3ResolvedUnitVisual> roster,
            Wh3ArmyVisualScenario scenario,
            IPackFileContainer source,
            IReadOnlyCollection<string> optimizedRoots,
            List<string> diagnostics)
        {
            var scopeKey = scenario.RosterScopeKey?.Trim() ?? string.Empty;
            if ((scenario.RosterScope is Wh3RosterScope.SelectedFaction or Wh3RosterScope.SelectedCulture) &&
                scopeKey.Length == 0)
            {
                diagnostics.Add(
                    $"Roster scope {scenario.RosterScope} requires RosterScopeKey; using all resolved units.");
                return roster;
            }

            var optimized = optimizedRoots
                .Select(NormalizePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            bool Matches(Wh3ResolvedUnitVisual unit)
                => scenario.RosterScope switch
                {
                    Wh3RosterScope.SelectedFaction =>
                        unit.FactionKeys.Contains(scopeKey),
                    Wh3RosterScope.SelectedCulture =>
                        unit.CultureKeys.Contains(scopeKey) ||
                        unit.SubcultureKeys.Contains(scopeKey),
                    Wh3RosterScope.ModAffectedUnits =>
                        unit.Components.Any(component => source.ContainsFile(component.AssetPath)),
                    Wh3RosterScope.OptimizedAssetsOnly =>
                        unit.Components.Any(component =>
                            optimized.Contains(NormalizePath(component.AssetPath))),
                    _ => true,
                };

            var filtered = roster.Where(Matches).ToArray();
            diagnostics.Add(
                $"Roster scope {scenario.RosterScope}" +
                (scopeKey.Length == 0 ? string.Empty : $" ({scopeKey})") +
                $": {filtered.Length:N0} / {roster.Count:N0} unit(s).");
            return filtered;
        }

        private static void AddUsage(
            Dictionary<string, Dictionary<string, Wh3UnitCategoryUsage>> usagesByVmd,
            Wh3UnitCategoryUsage usage)
        {
            var vmdPath = NormalizePath(usage.VmdPath);
            if (!usagesByVmd.TryGetValue(vmdPath, out var usages))
            {
                usages = new Dictionary<string, Wh3UnitCategoryUsage>(StringComparer.OrdinalIgnoreCase);
                usagesByVmd[vmdPath] = usages;
            }

            // Faction-specific unit_variants rows can point the same gameplay unit at the same VMD.
            // They are alternative selectors, not additional battlefield units, so collapse them.
            var identity = string.IsNullOrWhiteSpace(usage.MainUnitKey)
                ? $"land:{usage.LandUnitKey}"
                : $"main:{usage.MainUnitKey}";
            identity += $"|role:{usage.VisualRole}";
            var normalizedUsage = usage with { VmdPath = vmdPath };
            if (!usages.TryGetValue(identity, out var existing))
            {
                usages[identity] = normalizedUsage;
                return;
            }

            var mergedProvenance = existing.Provenance
                .Concat(normalizedUsage.Provenance)
                .GroupBy(
                    provenance =>
                        $"{provenance.ConsumerType}\u001f{provenance.RootVmdPath}\u001f" +
                        $"{provenance.ParentVmdPath}\u001f{provenance.FactionKey}\u001f" +
                        $"{provenance.SubcultureKey}\u001f{provenance.CultureKey}\u001f" +
                        $"{provenance.UniformName}\u001f{provenance.ArtSetId}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            usages[identity] = existing with { Provenance = mergedProvenance };
        }

        private static void PropagateUsagesToChildVmds(
            Dictionary<string, Dictionary<string, Wh3UnitCategoryUsage>> usagesByVmd,
            IReadOnlyCollection<string> directSourceVmds,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> childVmdsByVmd,
            CancellationToken cancellationToken)
        {
            foreach (var directVmdValue in directSourceVmds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directVmd = NormalizePath(directVmdValue);
                if (!usagesByVmd.TryGetValue(directVmd, out var sourceUsages) ||
                    sourceUsages.Count == 0)
                {
                    continue;
                }

                foreach (var (child, parent) in GetTransitiveChildVmdParents(
                             directVmd,
                             childVmdsByVmd,
                             cancellationToken))
                {
                    foreach (var usage in sourceUsages.Values)
                    {
                        var childProvenance = usage.Provenance
                            .Select(provenance => provenance with
                            {
                                RootVmdPath = directVmd,
                                ParentVmdPath = parent,
                            })
                            .ToArray();
                        AddUsage(
                            usagesByVmd,
                            usage with
                            {
                                VmdPath = child,
                                RootVmdPath = directVmd,
                                ParentVmdPath = parent,
                                Provenance = childProvenance,
                            });
                    }
                }
            }
        }

        private static IReadOnlyDictionary<string, string> GetTransitiveChildVmdParents(
            string rootVmdPath,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> childVmdsByVmd,
            CancellationToken cancellationToken)
        {
            var root = NormalizePath(rootVmdPath);
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.Length == 0)
                return result;

            var queue = new Queue<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
            queue.Enqueue(root);

            while (queue.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parent = queue.Dequeue();
                if (!childVmdsByVmd.TryGetValue(parent, out var children))
                    continue;

                foreach (var childValue in children)
                {
                    var child = NormalizePath(childValue);
                    if (child.Length == 0 || !visited.Add(child))
                        continue;

                    result[child] = parent;
                    queue.Enqueue(child);
                }
            }

            return result;
        }

        private static IReadOnlyList<string> ResolveBattleAgentVariantNames(
            string mainUnitKey,
            IReadOnlyDictionary<string, Dictionary<string, string>> agentSubtypeRows,
            IReadOnlyDictionary<string, Dictionary<string, string>> agentSubtypeOverrideRows,
            IReadOnlyDictionary<string, Dictionary<string, string>> campaignCharacterArtSetRows,
            IEnumerable<Dictionary<string, string>> campaignCharacterArtRows,
            IReadOnlyDictionary<string, Dictionary<string, string>> agentUniformRows)
        {
            // Compatibility helper for focused tests and callers without faction context.
            var variants = new Dictionary<string, Dictionary<string, string>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var uniform in agentUniformRows.Values)
            {
                foreach (var field in new[] { "battle_filename", "filename" })
                {
                    var variant = Get(uniform, field);
                    if (variant.Length != 0 && variant != "." && !variants.ContainsKey(variant))
                    {
                        variants[variant] = new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["variant_name"] = variant,
                            ["variant_filename"] = variant,
                        };
                    }
                }
            }

            return ResolveBattleAgentVisuals(
                    mainUnitKey,
                    Array.Empty<string>(),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    Array.Empty<Dictionary<string, string>>(),
                    agentSubtypeRows,
                    agentSubtypeOverrideRows,
                    campaignCharacterArtSetRows,
                    campaignCharacterArtRows,
                    agentUniformRows,
                    variants)
                .Variants
                .Select(variant => variant.VariantName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static Wh3AgentVisualResolution ResolveBattleAgentVisuals(
            string mainUnitKey,
            IReadOnlyCollection<string> factionKeys,
            IReadOnlyDictionary<string, string> subcultureByFaction,
            IReadOnlyDictionary<string, string> cultureBySubculture,
            IEnumerable<Dictionary<string, string>> customBattlePermissionRows,
            IReadOnlyDictionary<string, Dictionary<string, string>> agentSubtypeRows,
            IReadOnlyDictionary<string, Dictionary<string, string>> agentSubtypeOverrideRows,
            IReadOnlyDictionary<string, Dictionary<string, string>> campaignCharacterArtSetRows,
            IEnumerable<Dictionary<string, string>> campaignCharacterArtRows,
            IReadOnlyDictionary<string, Dictionary<string, string>> agentUniformRows,
            IReadOnlyDictionary<string, Dictionary<string, string>> variantRows)
        {
            if (string.IsNullOrWhiteSpace(mainUnitKey))
                return new Wh3AgentVisualResolution([], false, []);

            var issues = new List<string>();
            var results = new List<Wh3ResolvedAgentVariant>();
            var permissions = customBattlePermissionRows
                .Where(row => Get(row, "unit")
                    .Equals(mainUnitKey, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var contexts = factionKeys
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(faction =>
                {
                    var subculture = subcultureByFaction.GetValueOrDefault(faction) ?? string.Empty;
                    var culture = subculture.Length == 0
                        ? string.Empty
                        : cultureBySubculture.GetValueOrDefault(subculture) ?? string.Empty;
                    return (Faction: faction, Subculture: subculture, Culture: culture);
                })
                .ToList();

            if (contexts.Count == 0)
                contexts.Add((string.Empty, string.Empty, string.Empty));

            bool TryResolveUniformVariant(
                string uniformName,
                Wh3VmdConsumerType consumerType,
                string faction,
                string subculture,
                string culture,
                string artSetId)
            {
                if (uniformName.Length == 0)
                    return false;
                if (!agentUniformRows.TryGetValue(uniformName, out var uniform))
                {
                    issues.Add(
                        $"{mainUnitKey}: uniform '{uniformName}' referenced by {consumerType} " +
                        "was not found in agent_uniforms_tables.");
                    return false;
                }

                var variantName = Get(uniform, "battle_filename");
                if (variantName.Length == 0 || variantName == ".")
                    variantName = Get(uniform, "filename");
                if (variantName.Length == 0 || variantName == ".")
                {
                    issues.Add(
                        $"{mainUnitKey}: uniform '{uniformName}' has neither battle_filename " +
                        "nor filename.");
                    return false;
                }

                if (!variantRows.ContainsKey(variantName))
                {
                    issues.Add(
                        $"{mainUnitKey}: uniform '{uniformName}' resolves to variant " +
                        $"'{variantName}', which was not found in variants_tables.");
                    return false;
                }

                results.Add(new Wh3ResolvedAgentVariant(
                    variantName,
                    consumerType,
                    faction,
                    subculture,
                    culture,
                    uniformName,
                    artSetId));
                return true;
            }

            var hasAuthority = false;
            var artRows = campaignCharacterArtRows.ToList();

            foreach (var context in contexts)
            {
                var contextPermissions = permissions
                    .Where(row =>
                    {
                        var faction = Get(row, "faction");
                        return context.Faction.Length == 0 ||
                               faction.Length == 0 ||
                               faction.Equals(context.Faction, StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();
                var explicitUniforms = contextPermissions
                    .Select(row => Get(row, "general_uniform"))
                    .Where(uniform => uniform.Length != 0 && uniform != ".")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (explicitUniforms.Length != 0)
                {
                    hasAuthority = true;
                    foreach (var uniformName in explicitUniforms)
                    {
                        TryResolveUniformVariant(
                            uniformName,
                            Wh3VmdConsumerType.CustomBattleGeneralUniform,
                            context.Faction,
                            context.Subculture,
                            context.Culture,
                            string.Empty);
                    }

                    // general_uniform is explicit battle authority for this faction context.
                    continue;
                }

                var subtypeContexts = agentSubtypeRows.Values
                    .Where(row => Get(row, "associated_unit_override")
                        .Equals(mainUnitKey, StringComparison.OrdinalIgnoreCase))
                    .Select(row => (Subtype: Get(row, "key"), Agent: string.Empty))
                    .Concat(
                        agentSubtypeOverrideRows.Values
                            .Where(row =>
                            {
                                if (!Get(row, "associated_unit_override")
                                        .Equals(mainUnitKey, StringComparison.OrdinalIgnoreCase))
                                {
                                    return false;
                                }

                                var subculture = Get(row, "subculture");
                                return subculture.Length == 0 ||
                                       subculture.Equals(
                                           context.Subculture,
                                           StringComparison.OrdinalIgnoreCase);
                            })
                            .Select(row => (
                                Subtype: Get(row, "subtype"),
                                Agent: Get(row, "agent"))))
                    .Where(candidate => candidate.Subtype.Length != 0)
                    .Distinct()
                    .ToArray();
                if (subtypeContexts.Length == 0)
                    continue;

                static bool ScopeMatches(string required, string actual)
                    => required.Length == 0 ||
                       required.Equals(actual, StringComparison.OrdinalIgnoreCase);

                var candidateArtSets = campaignCharacterArtSetRows.Values
                    .Where(row =>
                    {
                        var subtype = Get(row, "agent_subtype");
                        var agentType = Get(row, "agent_type");
                        return subtypeContexts.Any(candidate =>
                                   candidate.Subtype.Equals(
                                       subtype,
                                       StringComparison.OrdinalIgnoreCase) &&
                                   (candidate.Agent.Length == 0 ||
                                    candidate.Agent.Equals(
                                        agentType,
                                        StringComparison.OrdinalIgnoreCase))) &&
                               ScopeMatches(Get(row, "faction"), context.Faction) &&
                               ScopeMatches(Get(row, "subculture"), context.Subculture) &&
                               ScopeMatches(Get(row, "culture"), context.Culture);
                    })
                    .Select(row => new
                    {
                        Row = row,
                        Score =
                            (Get(row, "faction").Length == 0 ? 0 : 4) +
                            (Get(row, "subculture").Length == 0 ? 0 : 2) +
                            (Get(row, "culture").Length == 0 ? 0 : 1),
                    })
                    .ToList();
                if (candidateArtSets.Count == 0)
                    continue;

                hasAuthority = true;
                var maxSpecificity = candidateArtSets.Max(candidate => candidate.Score);
                foreach (var artSet in candidateArtSets
                             .Where(candidate => candidate.Score == maxSpecificity)
                             .Select(candidate => candidate.Row))
                {
                    var artSetId = Get(artSet, "art_set_id");
                    var rowsForSet = artRows
                        .Where(row => Get(row, "art_set_id")
                            .Equals(artSetId, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (rowsForSet.Count == 0)
                    {
                        issues.Add(
                            $"{mainUnitKey}: applicable art set '{artSetId}' has no " +
                            "campaign_character_arts row.");
                        continue;
                    }

                    static int NumericOrder(
                        IReadOnlyDictionary<string, string> row,
                        string field)
                        => TryParseInt(Get(row, field), out var value)
                            ? value
                            : int.MaxValue;

                    var minimumLevel = rowsForSet.Min(row => NumericOrder(row, "level"));
                    rowsForSet = rowsForSet
                        .Where(row => NumericOrder(row, "level") == minimumLevel)
                        .ToList();

                    var minimumAge = rowsForSet.Min(row => NumericOrder(row, "age"));
                    rowsForSet = rowsForSet
                        .Where(row => NumericOrder(row, "age") == minimumAge)
                        .ToList();

                    var noSeasonRows = rowsForSet
                        .Where(row =>
                        {
                            var season = Get(row, "season");
                            return season.Length == 0 ||
                                   season.Equals("none", StringComparison.OrdinalIgnoreCase);
                        })
                        .ToList();
                    if (noSeasonRows.Count != 0)
                        rowsForSet = noSeasonRows;

                    foreach (var artRow in rowsForSet)
                    {
                        var uniformName = Get(artRow, "uniform");
                        if (uniformName.Length == 0)
                        {
                            issues.Add(
                                $"{mainUnitKey}: art set '{artSetId}' selected an art row " +
                                "without a uniform.");
                            continue;
                        }

                        TryResolveUniformVariant(
                            uniformName,
                            Wh3VmdConsumerType.BattleAgentArtSet,
                            context.Faction,
                            context.Subculture,
                            context.Culture,
                            artSetId);
                    }
                }
            }

            var deduplicated = results
                .GroupBy(
                    result =>
                        $"{result.VariantName}\u001f{result.ConsumerType}\u001f" +
                        $"{result.FactionKey}\u001f{result.SubcultureKey}\u001f" +
                        $"{result.CultureKey}\u001f{result.UniformName}\u001f{result.ArtSetId}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(result => result.VariantName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(result => result.FactionKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new Wh3AgentVisualResolution(deduplicated, hasAuthority, issues);
        }

        private static string ResolveUiGroupKey(
            IReadOnlyDictionary<string, string> main,
            string caste,
            IReadOnlyDictionary<string, Dictionary<string, string>> uiUnitGroupings,
            IReadOnlyDictionary<string, Dictionary<string, string>> uiUnitGroupParents)
        {
            var groupingKey = Get(main, "ui_unit_group_land");
            if (groupingKey.Length == 0 ||
                !uiUnitGroupings.TryGetValue(groupingKey, out var grouping))
            {
                return string.Empty;
            }

            var parentGroupKey = Get(grouping, "parent_group");
            if (parentGroupKey.Length == 0)
                return string.Empty;

            // Match WHMM Unit Viewer's getUiGroupKey: a handful of vanilla lords point at
            // heroes_agents, but the viewer keeps them in Commander when that parent exists.
            if (caste.Trim().Equals("lord", StringComparison.OrdinalIgnoreCase) &&
                parentGroupKey.Equals("heroes_agents", StringComparison.OrdinalIgnoreCase) &&
                uiUnitGroupParents.ContainsKey("commander"))
            {
                return "commander";
            }

            return uiUnitGroupParents.ContainsKey(parentGroupKey)
                ? parentGroupKey
                : string.Empty;
        }

        private static Wh3ArmyUnitCategory Classify(
            string caste,
            string landCategory,
            string uiGroupKey)
        {
            // Use the same roster grouping source as WHMM's Unit Viewer:
            // main_units.ui_unit_group_land -> ui_unit_groupings.parent_group.
            // Caste / land category are only fallbacks for units without a usable UI group.
            switch (uiGroupKey.Trim().ToLowerInvariant())
            {
                case "commander":
                    return Wh3ArmyUnitCategory.Lord;
                case "heroes_agents":
                    return Wh3ArmyUnitCategory.Hero;

                case "infantry":
                case "missile_infantry":
                    return Wh3ArmyUnitCategory.InfantryMissile;

                case "cavalry_chariots":
                case "missile_cavalry_chariots":
                    return Wh3ArmyUnitCategory.CavalryChariot;

                case "monster_beasts":
                case "missile_monster_beasts":
                case "constructs":
                    return Wh3ArmyUnitCategory.MonsterBeast;

                case "flying_war_machine":
                case "artillery_war_machines":
                    return Wh3ArmyUnitCategory.ArtilleryWarMachine;
            }

            switch (caste.Trim().ToLowerInvariant())
            {
                case "lord":
                    return Wh3ArmyUnitCategory.Lord;
                case "hero":
                    return Wh3ArmyUnitCategory.Hero;
                case "melee_infantry":
                case "missile_infantry":
                case "infantry":
                    return Wh3ArmyUnitCategory.InfantryMissile;
                case "melee_cavalry":
                case "missile_cavalry":
                case "cavalry":
                case "chariot":
                    return Wh3ArmyUnitCategory.CavalryChariot;
                case "monster":
                case "monstrous_infantry":
                case "war_beast":
                    return Wh3ArmyUnitCategory.MonsterBeast;
                case "warmachine":
                case "war_machine":
                case "artillery":
                    return Wh3ArmyUnitCategory.ArtilleryWarMachine;
            }

            return landCategory.Trim().ToLowerInvariant() switch
            {
                "inf_melee" or "inf_ranged" => Wh3ArmyUnitCategory.InfantryMissile,
                "cavalry" => Wh3ArmyUnitCategory.CavalryChariot,
                "war_beast" => Wh3ArmyUnitCategory.MonsterBeast,
                "artillery" or "war_machine" => Wh3ArmyUnitCategory.ArtilleryWarMachine,
                _ => Wh3ArmyUnitCategory.Unknown,
            };
        }

        private static Wh3UnitVisualCounts ResolveVisualCounts(
            IReadOnlyDictionary<string, string> main,
            IReadOnlyDictionary<string, string> land,
            IReadOnlyDictionary<string, string>? engine)
            => ResolveVisualCountsForScenario(main, land, engine, Wh3ArmyVisualScenario.Default);

        private static Wh3UnitVisualCounts ResolveVisualCountsForScenario(
            IReadOnlyDictionary<string, string> main,
            IReadOnlyDictionary<string, string> land,
            IReadOnlyDictionary<string, string>? engine,
            Wh3ArmyVisualScenario scenario)
        {
            var rawMen = TryParseInt(Get(main, "num_men"), out var parsedMen)
                ? Math.Max(1, parsedMen)
                : 1;

            var mountKey = Get(land, "mount");
            var hasMount = mountKey.Length != 0;
            var mountsPerCarrier = TryParseInt(Get(land, "num_mounts"), out var parsedMounts)
                ? Math.Max(0, parsedMounts)
                : 0;
            if (hasMount && mountsPerCarrier == 0)
                mountsPerCarrier = 1;

            var engineKey = Get(land, "engine");
            var hasEngine = engineKey.Length != 0;
            var rawEngines = TryParseInt(Get(land, "num_engines"), out var parsedEngines)
                ? Math.Max(0, parsedEngines)
                : 0;
            if (hasEngine && rawEngines == 0)
                rawEngines = 1;

            var engines = hasEngine
                ? ScaleEntityCount(rawEngines, scenario.UnitSizeScale, scenario.EngineRoundingPolicy)
                : 0;
            var crewedEngine = hasEngine && IsCrewedEngine(Get(engine, "engine_type"));
            var riders = crewedEngine
                ? 0
                : ScaleEntityCount(rawMen, scenario.UnitSizeScale, scenario.EngineRoundingPolicy);
            var crew = crewedEngine
                ? ScaleEntityCount(rawMen, scenario.CrewScale, scenario.EngineRoundingPolicy)
                : 0;

            // For a mounted unit, num_mounts is the number of mounts attached to one
            // battlefield carrier.  A chariot uses num_engines as its carrier count, so
            // Skeleton Chariots resolve to ceil(12 * .75) = 9 carriers and 9 * 2 = 18
            // mounts.  Their rider VMD is still driven by the large-size num_men count:
            // ceil(24 * .75) = 18 riders.
            var carrierCount = engines > 0 ? engines : Math.Max(1, riders);
            var mounts = hasMount
                ? checked(carrierCount * Math.Max(1, mountsPerCarrier))
                : 0;

            // A Generic_3_Crew engine keeps its crew in the unit's main VMD.  The dump's
            // Screaming Skull Catapult row has num_men=44, so this intentionally resolves
            // to ceil(44 * .5) = 22 crew.  primary_ammo is not consulted.
            return new Wh3UnitVisualCounts(riders, mounts, engines, crew);
        }

        private static Wh3UnitVisualCounts ResolveExtraEngineVisualCounts(
            Wh3UnitVisualCounts visualCounts)
            => visualCounts with
            {
                // An extra-engine mapping represents an engine component even when
                // land_units.engine is empty. Reuse the primary carrier count when present;
                // otherwise keep one attached engine instead of dropping the component.
                Engines = Math.Max(1, visualCounts.Engines),
            };

        private static Wh3VisualAssetState GetDirectEngineAssetState(string field)
            => field.Equals("model", StringComparison.OrdinalIgnoreCase)
                ? Wh3VisualAssetState.Live
                : field.Equals("destroyed_model", StringComparison.OrdinalIgnoreCase)
                    ? Wh3VisualAssetState.Destroyed
                    : Wh3VisualAssetState.Destruct;

        private static double GetDirectEngineAssetExpectedLiveBattlePresence(string field)
        {
            var stateValue = GetDirectEngineAssetState(field);
            return GetScenarioPresenceProbability(
                Wh3ArmyVisualScenario.Default,
                stateValue,
                0);
        }

        private static double GetScenarioPresenceProbability(
            Wh3ArmyVisualScenario scenario,
            Wh3VisualAssetState stateValue,
            int lod)
        {
            var destroyedProbability = Math.Clamp(
                scenario.DestructionProbability,
                0.0,
                1.0);
            var destructProbability = Math.Clamp(
                scenario.DestructTransitionProbability,
                0.0,
                1.0 - destroyedProbability);
            var stateProbability = stateValue switch
            {
                Wh3VisualAssetState.Live =>
                    1.0 - destroyedProbability - destructProbability,
                Wh3VisualAssetState.Destroyed => destroyedProbability,
                Wh3VisualAssetState.Destruct => destructProbability,
                _ => 0.0,
            };
            if (stateProbability <= 0)
                return 0;

            if (!scenario.LodDistribution.TryGetValue(Math.Max(0, lod), out var lodProbability))
                return 0;

            return Math.Clamp(stateProbability * lodProbability, 0.0, 1.0);
        }

        private static int ResolveAssetLod(string assetPath)
        {
            var name = Path.GetFileNameWithoutExtension(assetPath);
            var marker = name.LastIndexOf("_lod", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                return 0;

            var digits = new string(name
                .Skip(marker + 4)
                .TakeWhile(char.IsDigit)
                .ToArray());
            return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lod)
                ? Math.Max(0, lod)
                : 0;
        }

        private static bool IsCrewedEngine(string engineType)
            => engineType.Contains("crew", StringComparison.OrdinalIgnoreCase) &&
               !engineType.Contains("no_crew", StringComparison.OrdinalIgnoreCase);

        private static int ScaleEntityCount(
            int rawCount,
            double scale,
            Wh3EntityRoundingPolicy roundingPolicy)
        {
            if (rawCount <= 0)
                return 0;

            var scaled = rawCount * scale;
            var rounded = roundingPolicy switch
            {
                Wh3EntityRoundingPolicy.Floor => (int)Math.Floor(scaled),
                Wh3EntityRoundingPolicy.Round => (int)Math.Round(scaled),
                _ => (int)Math.Ceiling(scaled),
            };
            return Math.Max(1, rounded);
        }

        private static List<Dictionary<string, string>> DecodeTable(
            string tableName,
            byte[] data,
            string packedPath,
            List<string> diagnostics)
        {
            if (!Schema.Value.Definitions.TryGetValue(tableName, out var versions))
                return [];

            var position = 0;
            int? packedVersion = null;
            while (position + 4 <= data.Length)
            {
                if (HasMarker(data, position, 0xfd, 0xfe, 0xfc, 0xff))
                {
                    position += 4;
                    var length = ReadInt16(data, ref position);
                    if (length < 0)
                        throw new InvalidDataException("Negative DB GUID length.");
                    Skip(data, ref position, checked(length * 2));
                    continue;
                }

                if (HasMarker(data, position, 0xfc, 0xfd, 0xfe, 0xff))
                {
                    position += 4;
                    packedVersion = ReadInt32(data, ref position);
                    continue;
                }

                // Matches WHMM's DB parser: one format byte follows the optional GUID/version
                // markers before the entry count.
                position++;
                break;
            }

            var schemaVersion = versions.FirstOrDefault(version => version.Version == packedVersion)
                ?? versions.FirstOrDefault(version => version.Version == 0);
            if (schemaVersion == null ||
                (packedVersion.HasValue && schemaVersion.Version < packedVersion.Value))
            {
                diagnostics.Add(
                    $"Skipped {packedPath}: no schema for {tableName} version " +
                    $"{(packedVersion.HasValue ? packedVersion.Value.ToString(CultureInfo.InvariantCulture) : "<none>")}.");
                return [];
            }

            var entryCount = ReadInt32(data, ref position);
            if (entryCount < 0)
                throw new InvalidDataException($"Negative row count {entryCount}.");

            var rows = new List<Dictionary<string, string>>(entryCount);
            for (var rowIndex = 0; rowIndex < entryCount; rowIndex++)
            {
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var field in schemaVersion.Fields)
                    row[field.Name] = ReadField(data, ref position, field.FieldType);
                rows.Add(row);
            }

            return rows;
        }

        private static bool HasMarker(
            byte[] data,
            int position,
            byte first,
            byte second,
            byte third,
            byte fourth)
            => position >= 0 &&
               position <= data.Length - 4 &&
               data[position] == first &&
               data[position + 1] == second &&
               data[position + 2] == third &&
               data[position + 3] == fourth;

        private static string ReadField(byte[] data, ref int position, string type)
        {
            return type switch
            {
                "Boolean" => ReadByte(data, ref position) == 0 ? "0" : "1",
                "ColourRGB" => ReadInt32(data, ref position).ToString(CultureInfo.InvariantCulture),
                "StringU16" => ReadStringU16(data, ref position),
                "StringU8" => ReadStringU8(data, ref position),
                "OptionalStringU8" => ReadOptionalStringU8(data, ref position),
                "F32" => ReadSingle(data, ref position).ToString("R", CultureInfo.InvariantCulture),
                "I32" => ReadInt32(data, ref position).ToString(CultureInfo.InvariantCulture),
                "I16" => ReadInt16(data, ref position).ToString(CultureInfo.InvariantCulture),
                "F64" => ReadDouble(data, ref position).ToString("R", CultureInfo.InvariantCulture),
                "I64" => ReadInt64(data, ref position).ToString(CultureInfo.InvariantCulture),
                _ => throw new InvalidDataException($"Unsupported WH3 DB field type '{type}'."),
            };
        }

        private static string ReadStringU8(byte[] data, ref int position)
        {
            var length = ReadUInt16(data, ref position);
            EnsureAvailable(data, position, length);
            var value = Encoding.ASCII.GetString(data, position, length);
            position += length;
            return value;
        }

        private static string ReadStringU16(byte[] data, ref int position)
        {
            var length = ReadInt16(data, ref position);
            if (length < 0)
                throw new InvalidDataException("Negative UTF-16 string length.");

            var byteLength = checked(length * 2);
            EnsureAvailable(data, position, byteLength);
            var value = Encoding.Unicode.GetString(data, position, byteLength);
            position += byteLength;
            return value;
        }

        private static string ReadOptionalStringU8(byte[] data, ref int position)
        {
            var present = ReadByte(data, ref position);
            return present == 1 ? ReadStringU8(data, ref position) : string.Empty;
        }

        private static byte ReadByte(byte[] data, ref int position)
        {
            EnsureAvailable(data, position, 1);
            return data[position++];
        }

        private static short ReadInt16(byte[] data, ref int position)
        {
            EnsureAvailable(data, position, 2);
            var value = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(position, 2));
            position += 2;
            return value;
        }

        private static ushort ReadUInt16(byte[] data, ref int position)
        {
            EnsureAvailable(data, position, 2);
            var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position, 2));
            position += 2;
            return value;
        }

        private static int ReadInt32(byte[] data, ref int position)
        {
            EnsureAvailable(data, position, 4);
            var value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position, 4));
            position += 4;
            return value;
        }

        private static long ReadInt64(byte[] data, ref int position)
        {
            EnsureAvailable(data, position, 8);
            var value = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(position, 8));
            position += 8;
            return value;
        }

        private static float ReadSingle(byte[] data, ref int position)
        {
            var bits = ReadInt32(data, ref position);
            return BitConverter.Int32BitsToSingle(bits);
        }

        private static double ReadDouble(byte[] data, ref int position)
        {
            var bits = ReadInt64(data, ref position);
            return BitConverter.Int64BitsToDouble(bits);
        }

        private static void Skip(byte[] data, ref int position, int count)
        {
            EnsureAvailable(data, position, count);
            position += count;
        }

        private static void EnsureAvailable(byte[] data, int position, int count)
        {
            if (position < 0 || count < 0 || position > data.Length - count)
                throw new EndOfStreamException(
                    $"DB row exceeds packed file length at offset {position:N0}.");
        }

        private static bool TryGetRequiredTable(string path, out string tableName)
        {
            var normalized = NormalizePath(path);
            foreach (var candidate in RequiredTables)
            {
                if (normalized.StartsWith(
                        $"db\\{candidate}\\",
                        StringComparison.OrdinalIgnoreCase))
                {
                    tableName = candidate;
                    return true;
                }
            }

            tableName = string.Empty;
            return false;
        }

        private static void ApplyEffectiveRows(
            string tableName,
            Dictionary<string, Dictionary<string, string>> effectiveRows,
            IEnumerable<Dictionary<string, string>> rows)
        {
            // Containers are processed CA-first and source-pack-last. Assignment by key is
            // intentional: later rows override earlier rows exactly like the game's/mod's
            // effective DB view.
            foreach (var row in rows)
            {
                var key = BuildEffectiveRowKey(tableName, row);
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                effectiveRows[key] = row;
            }
        }

        private static string BuildEffectiveRowKey(
            string tableName,
            IReadOnlyDictionary<string, string> row)
        {
            return tableName switch
            {
                MainUnitsTable => Get(row, "unit"),
                LandUnitsTable => Get(row, "key"),
                VariantsTable => Get(row, "variant_name"),
                // name identifies the visual slot.  Collapsing only by faction+unit loses
                // separate rider/crew/mount selectors when a unit has more than one row.
                UnitVariantsTable =>
                    $"{Get(row, "faction")}\u001f{Get(row, "unit")}\u001f{Get(row, "name")}",
                UiUnitGroupingsTable => Get(row, "key"),
                UiUnitGroupParentsTable => Get(row, "key"),
                MountsTable => Get(row, "key"),
                BattlefieldEnginesTable => Get(row, "key"),
                WarscapeAnimatedLodTable =>
                    $"{Get(row, "key")}\u001f{Get(row, "animated")}\u001f{Get(row, "filename")}",
                FactionsTable => Get(row, "key"),
                CulturesSubculturesTable => Get(row, "subculture"),
                CustomBattlePermissionsTable =>
                    $"{Get(row, "faction")}\u001f{Get(row, "general_unit")}\u001f{Get(row, "unit")}",
                ExtraEnginesTable =>
                    $"{Get(row, "land_unit")}\u001f{Get(row, "attach_articulation")}\u001f" +
                    Get(row, "battle_engine"),
                AgentSubtypesTable => Get(row, "key"),
                AgentSubtypeSubcultureOverridesTable =>
                    $"{Get(row, "subculture")}\u001f{Get(row, "subtype")}\u001f{Get(row, "agent")}",
                CampaignCharacterArtSetsTable => Get(row, "art_set_id"),
                CampaignCharacterArtsTable => Get(row, "id"),
                AgentUniformsTable => Get(row, "uniform_name"),
                _ => string.Empty,
            };
        }

        private static string Get(
            IReadOnlyDictionary<string, string>? row,
            string field)
            => row != null && row.TryGetValue(field, out var value)
                ? value.Trim()
                : string.Empty;

        private static string ToVariantMeshDefinitionPath(string value)
        {
            var path = NormalizePath(value);
            if (path.Length == 0)
                return string.Empty;

            if (!path.EndsWith(".variantmeshdefinition", StringComparison.OrdinalIgnoreCase))
                path += ".variantmeshdefinition";

            if (!path.StartsWith("variantmeshes\\", StringComparison.OrdinalIgnoreCase))
            {
                path = $"variantmeshes\\variantmeshdefinitions\\{path}";
            }
            else if (!path.StartsWith(
                         "variantmeshes\\variantmeshdefinitions\\",
                         StringComparison.OrdinalIgnoreCase))
            {
                path = $"variantmeshes\\variantmeshdefinitions\\{Path.GetFileName(path)}";
            }

            return NormalizePath(path);
        }

        private static string NormalizePath(string value)
            => value.Replace('/', '\\').TrimStart('\\').Trim().ToLowerInvariant();

        private static IReadOnlyList<string> ResolveEngineAssetPaths(
            string reference,
            IReadOnlyDictionary<string, List<string>> animatedLodRowsByKey)
        {
            reference = NormalizePath(reference);
            if (reference.Length == 0)
                return [];

            if (Path.GetExtension(reference).Length != 0)
                return [reference];

            return animatedLodRowsByKey.TryGetValue(reference, out var paths)
                ? paths.Select(NormalizePath).Where(path => path.Length != 0).ToArray()
                : [];
        }

        private static string GetGameplayResolutionHealthIssue(
            IReadOnlyDictionary<string, int> parsedRowsByTable,
            IReadOnlyList<string> diagnostics,
            bool agentResolutionAttempted = false,
            IReadOnlyList<string>? semanticIssues = null)
        {
            var requiredTables = agentResolutionAttempted
                ? GameplayCriticalTables.Concat(AgentGameplayCriticalTables)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : GameplayCriticalTables;

            var missingRows = requiredTables
                .Where(table =>
                    !parsedRowsByTable.TryGetValue(table, out var count) ||
                    count <= 0)
                .ToArray();

            var failedCriticalTables = requiredTables
                .Where(table => diagnostics.Any(diagnostic =>
                {
                    var normalized = NormalizePath(diagnostic);
                    return normalized.Contains(
                               $"db\\{table}\\",
                               StringComparison.OrdinalIgnoreCase) &&
                           (normalized.Contains(
                                "failed to decode",
                                StringComparison.OrdinalIgnoreCase) ||
                            normalized.StartsWith(
                                "skipped ",
                                StringComparison.OrdinalIgnoreCase));
                }))
                .ToArray();

            var semantic = semanticIssues?
                .Where(issue => !string.IsNullOrWhiteSpace(issue))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];

            if (missingRows.Length == 0 &&
                failedCriticalTables.Length == 0 &&
                semantic.Length == 0)
            {
                return string.Empty;
            }

            var problems = new List<string>();
            if (missingRows.Length != 0)
            {
                problems.Add(
                    $"no decoded rows for critical table(s): {string.Join(", ", missingRows)}");
            }

            if (failedCriticalTables.Length != 0)
            {
                problems.Add(
                    $"decode/schema failures in critical table(s): " +
                    $"{string.Join(", ", failedCriticalTables)}");
            }

            if (semantic.Length != 0)
            {
                problems.Add(
                    $"agent visual reference coverage failed: " +
                    $"{string.Join(" | ", semantic.Take(8))}" +
                    (semantic.Length > 8
                        ? $" | ... {semantic.Length - 8:N0} more"
                        : string.Empty));
            }

            return string.Join("; ", problems) + ".";
        }

        private static bool TryParseInt(string value, out int result)
            => int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out result);

        private static bool IsSameContainer(
            IPackFileContainer left,
            IPackFileContainer right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (string.IsNullOrWhiteSpace(left.SystemFilePath) ||
                string.IsNullOrWhiteSpace(right.SystemFilePath))
            {
                return false;
            }

            try
            {
                return Path.GetFullPath(left.SystemFilePath)
                    .Equals(
                        Path.GetFullPath(right.SystemFilePath),
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return left.SystemFilePath.Equals(
                    right.SystemFilePath,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string DescribeContainer(IPackFileContainer container)
            => container.SystemFilePath ?? container.Name;

        private static SchemaRoot LoadSchema()
        {
            var assembly = typeof(Wh3UnitCategoryResolver).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name =>
                    name.EndsWith(
                        "Wh3UnitCategorySchema.json",
                        StringComparison.OrdinalIgnoreCase));
            if (resourceName == null)
                throw new InvalidOperationException(
                    "Embedded WH3 unit-category DB schema was not found.");

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    "Embedded WH3 unit-category DB schema could not be opened.");
            return JsonSerializer.Deserialize<SchemaRoot>(
                       stream,
                       new JsonSerializerOptions
                       {
                           PropertyNameCaseInsensitive = true,
                       })
                   ?? throw new InvalidOperationException(
                       "Embedded WH3 unit-category DB schema is invalid.");
        }

        private sealed class SchemaRoot
        {
            [JsonPropertyName("definitions")]
            public Dictionary<string, List<TableVersionSchema>> Definitions { get; set; } =
                new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class TableVersionSchema
        {
            [JsonPropertyName("version")]
            public int Version { get; set; }

            [JsonPropertyName("fields")]
            public List<SchemaField> Fields { get; set; } = [];
        }

        private sealed class SchemaField
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("field_type")]
            public string FieldType { get; set; } = string.Empty;
        }
    }
}
