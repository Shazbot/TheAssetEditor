using System.IO;
using System.Reflection;
using System.Text;

namespace Test.KitbashEditor.Services
{
    public class Wh3UnitCategoryResolverTests
    {
        private static string Classify(string caste, string landCategory, string uiGroupKey = "")
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var resolverType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3UnitCategoryResolver",
                throwOnError: true)!;
            var method = resolverType.GetMethod(
                "Classify",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("Wh3UnitCategoryResolver.Classify was not found.");

            return method.Invoke(null, [caste, landCategory, uiGroupKey])?.ToString()
                ?? throw new InvalidOperationException("Wh3UnitCategoryResolver.Classify returned null.");
        }


        private static List<Dictionary<string, string>> DecodeTable(
            string tableName,
            byte[] data,
            List<string>? diagnostics = null)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var resolverType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3UnitCategoryResolver",
                throwOnError: true)!;
            var method = resolverType.GetMethod(
                "DecodeTable",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "Wh3UnitCategoryResolver.DecodeTable was not found.");

            diagnostics ??= [];
            return (List<Dictionary<string, string>>)(method.Invoke(
                null,
                [tableName, data, $"db/{tableName}/test", diagnostics])
                ?? throw new InvalidOperationException("DecodeTable returned null."));
        }

        private static void ApplyEffectiveRows(
            string tableName,
            Dictionary<string, Dictionary<string, string>> target,
            IEnumerable<Dictionary<string, string>> rows)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var resolverType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3UnitCategoryResolver",
                throwOnError: true)!;
            var method = resolverType.GetMethod(
                "ApplyEffectiveRows",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "Wh3UnitCategoryResolver.ApplyEffectiveRows was not found.");

            method.Invoke(null, [tableName, target, rows]);
        }

        private static object ResolveVisualCounts(
            IReadOnlyDictionary<string, string> main,
            IReadOnlyDictionary<string, string> land,
            IReadOnlyDictionary<string, string>? engine)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var resolverType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3UnitCategoryResolver",
                throwOnError: true)!;
            var method = resolverType.GetMethod(
                "ResolveVisualCounts",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "Wh3UnitCategoryResolver.ResolveVisualCounts was not found.");

            return method.Invoke(null, [main, land, engine])
                ?? throw new InvalidOperationException(
                    "Wh3UnitCategoryResolver.ResolveVisualCounts returned null.");
        }

        private static int GetVisualCount(object counts, string propertyName)
            => (int)(counts.GetType().GetProperty(propertyName)?.GetValue(counts)
                ?? throw new InvalidOperationException(
                    $"Visual count property '{propertyName}' was not found."));

        private static object ResolveExtraEngineVisualCounts(object counts)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var resolverType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3UnitCategoryResolver",
                throwOnError: true)!;
            var method = resolverType.GetMethod(
                "ResolveExtraEngineVisualCounts",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "Wh3UnitCategoryResolver.ResolveExtraEngineVisualCounts was not found.");

            return method.Invoke(null, [counts])
                ?? throw new InvalidOperationException(
                    "ResolveExtraEngineVisualCounts returned null.");
        }

        private static double GetDirectEngineAssetExpectedLiveBattlePresence(string field)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var resolverType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3UnitCategoryResolver",
                throwOnError: true)!;
            var method = resolverType.GetMethod(
                "GetDirectEngineAssetExpectedLiveBattlePresence",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "Wh3UnitCategoryResolver.GetDirectEngineAssetExpectedLiveBattlePresence was not found.");

            return (double)(method.Invoke(null, [field])
                ?? throw new InvalidOperationException(
                    "GetDirectEngineAssetExpectedLiveBattlePresence returned null."));
        }

        private static IReadOnlyList<string> ResolveEngineAssetPaths(
            string reference,
            IReadOnlyDictionary<string, List<string>> animatedLodRowsByKey)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var resolverType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3UnitCategoryResolver",
                throwOnError: true)!;
            var method = resolverType.GetMethod(
                "ResolveEngineAssetPaths",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "Wh3UnitCategoryResolver.ResolveEngineAssetPaths was not found.");

            return (IReadOnlyList<string>)(method.Invoke(
                null,
                [reference, animatedLodRowsByKey])
                ?? throw new InvalidOperationException(
                    "Wh3UnitCategoryResolver.ResolveEngineAssetPaths returned null."));
        }

        private static byte[] BuildUiUnitGroupParentsRow(
            string icon,
            string key,
            int order,
            int mpCap)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

            // WH3 DB header: version marker, version 1, format byte, row count.
            writer.Write(new byte[] { 0xfc, 0xfd, 0xfe, 0xff });
            writer.Write(1);
            writer.Write((byte)0);
            writer.Write(1);

            WriteStringU8(writer, icon);
            WriteStringU8(writer, key);
            writer.Write(order);
            writer.Write(mpCap);
            writer.Flush();
            return stream.ToArray();
        }

        private static void WriteStringU8(BinaryWriter writer, string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        [TestCase("melee_cavalry", "war_beast", "CavalryChariot")]
        [TestCase("missile_cavalry", "inf_ranged", "CavalryChariot")]
        [TestCase("chariot", "war_machine", "CavalryChariot")]
        [TestCase("monster", "inf_melee", "MonsterBeast")]
        [TestCase("monstrous_infantry", "inf_melee", "MonsterBeast")]
        [TestCase("war_beast", "inf_melee", "MonsterBeast")]
        [TestCase("warmachine", "artillery", "ArtilleryWarMachine")]
        [TestCase("melee_infantry", "war_beast", "InfantryMissile")]
        public void CasteTakesPrecedenceOverLandCategory(
            string caste,
            string landCategory,
            string expected)
        {
            Assert.That(Classify(caste, landCategory), Is.EqualTo(expected));
        }

        [TestCase("missile_infantry", "inf_ranged", "monster_beasts", "MonsterBeast")]
        [TestCase("melee_infantry", "inf_melee", "cavalry_chariots", "CavalryChariot")]
        [TestCase("monster", "inf_melee", "artillery_war_machines", "ArtilleryWarMachine")]
        public void UnitViewerUiGroupTakesPrecedence(
            string caste,
            string landCategory,
            string uiGroupKey,
            string expected)
        {
            Assert.That(Classify(caste, landCategory, uiGroupKey), Is.EqualTo(expected));
        }

        [TestCase("lord", "war_machine", "Lord")]
        [TestCase("hero", "cavalry", "Hero")]
        public void CharacterCasteAlwaysWins(
            string caste,
            string landCategory,
            string expected)
        {
            Assert.That(Classify(caste, landCategory), Is.EqualTo(expected));
        }
        [Test]
        public void DecodeTable_DecodesPackedVersionedRowsBySchema()
        {
            var diagnostics = new List<string>();
            var rows = DecodeTable(
                "ui_unit_group_parents_tables",
                BuildUiUnitGroupParentsRow(
                    "ui/units/commander.png",
                    "commander",
                    17,
                    4),
                diagnostics);

            Assert.Multiple(() =>
            {
                Assert.That(diagnostics, Is.Empty);
                Assert.That(rows, Has.Count.EqualTo(1));
                Assert.That(rows[0]["icon"], Is.EqualTo("ui/units/commander.png"));
                Assert.That(rows[0]["key"], Is.EqualTo("commander"));
                Assert.That(rows[0]["order"], Is.EqualTo("17"));
                Assert.That(rows[0]["mp_cap"], Is.EqualTo("4"));
            });
        }

        [Test]
        public void EffectiveRows_LaterSourceRowOverridesEarlierCaRow()
        {
            var effective = new Dictionary<string, Dictionary<string, string>>(
                StringComparer.OrdinalIgnoreCase);
            var caRow = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["key"] = "commander",
                ["icon"] = "ca_icon"
            };
            var sourceRow = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["key"] = "commander",
                ["icon"] = "mod_icon"
            };

            ApplyEffectiveRows("ui_unit_group_parents_tables", effective, [caRow]);
            ApplyEffectiveRows("ui_unit_group_parents_tables", effective, [sourceRow]);

            Assert.Multiple(() =>
            {
                Assert.That(effective, Has.Count.EqualTo(1));
                Assert.That(effective["commander"]["icon"], Is.EqualTo("mod_icon"));
            });
        }

        [Test]
        public void EffectiveRows_UnitVariantsKeepsDistinctVisualSlots()
        {
            var effective = new Dictionary<string, Dictionary<string, string>>(
                StringComparer.OrdinalIgnoreCase);

            ApplyEffectiveRows(
                "unit_variants_tables",
                effective,
                [
                    new Dictionary<string, string>
                    {
                        ["faction"] = "",
                        ["unit"] = "unit",
                        ["name"] = "crew",
                        ["variant"] = "crew_variant",
                    },
                    new Dictionary<string, string>
                    {
                        ["faction"] = "",
                        ["unit"] = "unit",
                        ["name"] = "mount",
                        ["variant"] = "mount_variant",
                    },
                ]);
            ApplyEffectiveRows(
                "unit_variants_tables",
                effective,
                [
                    new Dictionary<string, string>
                    {
                        ["faction"] = "",
                        ["unit"] = "unit",
                        ["name"] = "crew",
                        ["variant"] = "modded_crew_variant",
                    },
                ]);

            Assert.Multiple(() =>
            {
                Assert.That(effective, Has.Count.EqualTo(2));
                Assert.That(
                    effective.Values.Single(row => row["name"] == "crew")["variant"],
                    Is.EqualTo("modded_crew_variant"));
                Assert.That(
                    effective.Values.Single(row => row["name"] == "mount")["variant"],
                    Is.EqualTo("mount_variant"));
            });
        }

        [Test]
        public void EffectiveRows_UnitVariantsKeepFactionSpecificVisualsSeparate()
        {
            var effective = new Dictionary<string, Dictionary<string, string>>(
                StringComparer.OrdinalIgnoreCase);

            ApplyEffectiveRows(
                "unit_variants_tables",
                effective,
                [
                    new Dictionary<string, string>
                    {
                        ["faction"] = "faction_a",
                        ["unit"] = "shared_unit",
                        ["name"] = "body",
                        ["variant"] = "variant_a",
                    },
                    new Dictionary<string, string>
                    {
                        ["faction"] = "faction_b",
                        ["unit"] = "shared_unit",
                        ["name"] = "body",
                        ["variant"] = "variant_b",
                    },
                ]);

            Assert.Multiple(() =>
            {
                Assert.That(effective, Has.Count.EqualTo(2));
                Assert.That(
                    effective.Values.Single(row => row["faction"] == "faction_a")["variant"],
                    Is.EqualTo("variant_a"));
                Assert.That(
                    effective.Values.Single(row => row["faction"] == "faction_b")["variant"],
                    Is.EqualTo("variant_b"));
            });
        }

        [Test]
        public void EffectiveRows_AgentSubtypeOverridesKeepCompositeKeyRowsSeparate()
        {
            var effective = new Dictionary<string, Dictionary<string, string>>(
                StringComparer.OrdinalIgnoreCase);

            ApplyEffectiveRows(
                "agent_subtype_subculture_overrides_tables",
                effective,
                [
                    new Dictionary<string, string>
                    {
                        ["subculture"] = "sub_a",
                        ["subtype"] = "shared_subtype",
                        ["agent"] = "general",
                        ["associated_unit_override"] = "unit_a",
                    },
                    new Dictionary<string, string>
                    {
                        ["subculture"] = "sub_b",
                        ["subtype"] = "shared_subtype",
                        ["agent"] = "general",
                        ["associated_unit_override"] = "unit_b",
                    },
                    new Dictionary<string, string>
                    {
                        ["subculture"] = "sub_a",
                        ["subtype"] = "shared_subtype",
                        ["agent"] = "champion",
                        ["associated_unit_override"] = "unit_c",
                    },
                ]);

            Assert.That(effective, Has.Count.EqualTo(3));
        }

        [Test]
        public void EffectiveRows_AnimatedLodKeepsAllFilesForAnAnimatedKey()
        {
            var effective = new Dictionary<string, Dictionary<string, string>>(
                StringComparer.OrdinalIgnoreCase);

            ApplyEffectiveRows(
                "warscape_animated_lod_tables",
                effective,
                [
                    new Dictionary<string, string>
                    {
                        ["key"] = "tmb_skull_catapult",
                        ["filename"] = @"warmachines\engines\tmb_skull_catapult\catapult_01.rigid_model_v2",
                        ["animated"] = "tmb_skull_catapult",
                    },
                    new Dictionary<string, string>
                    {
                        ["key"] = "tmb_skull_catapult",
                        ["filename"] = @"warmachines\engines\tmb_skull_catapult\catapult_01_lod2.rigid_model_v2",
                        ["animated"] = "tmb_skull_catapult",
                    },
                ]);

            Assert.That(effective, Has.Count.EqualTo(2));
        }

        [Test]
        public void VisualCounts_SkeletonChariotUsesCarrierMountAndRiderRoles()
        {
            var counts = ResolveVisualCounts(
                new Dictionary<string, string>
                {
                    ["num_men"] = "24",
                },
                new Dictionary<string, string>
                {
                    ["mount"] = "wh2_dlc09_tmb_mnt_tomb_steed_chariot",
                    ["num_mounts"] = "2",
                    ["engine"] = "wh2_dlc09_tmb_chariot",
                    ["num_engines"] = "12",
                },
                new Dictionary<string, string>
                {
                    ["engine_type"] = "Generic_No_Crew_Rotate",
                });

            Assert.Multiple(() =>
            {
                Assert.That(GetVisualCount(counts, "Riders"), Is.EqualTo(18));
                Assert.That(GetVisualCount(counts, "Mounts"), Is.EqualTo(18));
                Assert.That(GetVisualCount(counts, "Engines"), Is.EqualTo(9));
                Assert.That(GetVisualCount(counts, "Crew"), Is.EqualTo(0));
            });
        }

        [Test]
        public void VisualCounts_ScreamingSkullUsesCrewCountAndIgnoresAmmo()
        {
            var counts = ResolveVisualCounts(
                new Dictionary<string, string>
                {
                    ["num_men"] = "44",
                    ["primary_ammo"] = "22",
                },
                new Dictionary<string, string>
                {
                    ["engine"] = "wh2_dlc09_tmb_art_screaming_skull_catapult",
                    ["num_engines"] = "4",
                },
                new Dictionary<string, string>
                {
                    ["engine_type"] = "Generic_3_Crew",
                });

            Assert.Multiple(() =>
            {
                Assert.That(GetVisualCount(counts, "Riders"), Is.EqualTo(0));
                Assert.That(GetVisualCount(counts, "Mounts"), Is.EqualTo(0));
                Assert.That(GetVisualCount(counts, "Engines"), Is.EqualTo(3));
                Assert.That(GetVisualCount(counts, "Crew"), Is.EqualTo(22));
            });
        }

        [Test]
        public void ExtraEngineVisualCounts_KeepAnEngineWhenPrimaryEngineIsAbsent()
        {
            var baseCounts = ResolveVisualCounts(
                new Dictionary<string, string>
                {
                    ["num_men"] = "1",
                },
                new Dictionary<string, string>(),
                null);

            var extraCounts = ResolveExtraEngineVisualCounts(baseCounts);

            Assert.Multiple(() =>
            {
                Assert.That(GetVisualCount(baseCounts, "Engines"), Is.EqualTo(0));
                Assert.That(GetVisualCount(extraCounts, "Engines"), Is.EqualTo(1));
            });
        }

        [TestCase("model", 1.0)]
        [TestCase("destroyed_model", 0.0)]
        [TestCase("destruct_model", 0.0)]
        [TestCase("destruction_animation", 0.0)]
        public void DirectEngineAssetPresence_OnlyLiveModelCountsAsNormalBattleVisual(
            string field,
            double expected)
        {
            Assert.That(
                GetDirectEngineAssetExpectedLiveBattlePresence(field),
                Is.EqualTo(expected));
        }

        [Test]
        public void EngineAssetReference_ResolvesAnimatedLodAndExplicitDestroyedModel()
        {
            var animatedLodRows = new Dictionary<string, List<string>>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["tmb_skull_catapult"] =
                [
                    @"warmachines\engines\tmb_skull_catapult\tmb_screaming_skull_catapult_01.rigid_model_v2",
                ],
                ["tmb_skull_catapult_destruct"] =
                [
                    @"warmachines\engines\tmb_skull_catapult\tmb_screaming_skull_catapult_01_destruct.wsmodel",
                ],
            };

            var modelPaths = ResolveEngineAssetPaths(
                "tmb_skull_catapult",
                animatedLodRows);
            var destructionAnimationPaths = ResolveEngineAssetPaths(
                "tmb_skull_catapult_destruct",
                animatedLodRows);
            var destroyedPaths = ResolveEngineAssetPaths(
                @"warmachines\engines\tmb_skull_catapult\tmb_screaming_skull_catapult_01_destroyed.wsmodel",
                animatedLodRows);

            Assert.Multiple(() =>
            {
                Assert.That(
                    modelPaths,
                    Is.EqualTo(
                    [
                        @"warmachines\engines\tmb_skull_catapult\tmb_screaming_skull_catapult_01.rigid_model_v2",
                    ]));
                Assert.That(
                    destructionAnimationPaths,
                    Is.EqualTo(
                    [
                        @"warmachines\engines\tmb_skull_catapult\tmb_screaming_skull_catapult_01_destruct.wsmodel",
                    ]));
                Assert.That(
                    destroyedPaths,
                    Is.EqualTo(
                    [
                        @"warmachines\engines\tmb_skull_catapult\tmb_screaming_skull_catapult_01_destroyed.wsmodel",
                    ]));
            });
        }


        private static IReadOnlyList<string> ResolveBattleAgentVariantNames(
            string mainUnitKey,
            Dictionary<string, Dictionary<string, string>> agentSubtypes,
            Dictionary<string, Dictionary<string, string>> subtypeOverrides,
            Dictionary<string, Dictionary<string, string>> artSets,
            List<Dictionary<string, string>> arts,
            Dictionary<string, Dictionary<string, string>> uniforms)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var resolverType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3UnitCategoryResolver",
                throwOnError: true)!;
            var method = resolverType.GetMethod(
                "ResolveBattleAgentVariantNames",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "Wh3UnitCategoryResolver.ResolveBattleAgentVariantNames was not found.");

            return (IReadOnlyList<string>)(method.Invoke(
                null,
                [mainUnitKey, agentSubtypes, subtypeOverrides, artSets, arts, uniforms])
                ?? throw new InvalidOperationException(
                    "ResolveBattleAgentVariantNames returned null."));
        }

        private static IReadOnlyList<Dictionary<string, string>> ResolveBattleAgentVisuals(
            string mainUnitKey,
            IReadOnlyCollection<string> factions,
            IReadOnlyDictionary<string, string> subcultureByFaction,
            IReadOnlyDictionary<string, string> cultureBySubculture,
            List<Dictionary<string, string>> permissions,
            Dictionary<string, Dictionary<string, string>> agentSubtypes,
            Dictionary<string, Dictionary<string, string>> subtypeOverrides,
            Dictionary<string, Dictionary<string, string>> artSets,
            List<Dictionary<string, string>> arts,
            Dictionary<string, Dictionary<string, string>> uniforms,
            Dictionary<string, Dictionary<string, string>> variants,
            out bool hasAuthority,
            out IReadOnlyList<string> issues)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var resolverType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3UnitCategoryResolver",
                throwOnError: true)!;
            var method = resolverType.GetMethod(
                "ResolveBattleAgentVisuals",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "Wh3UnitCategoryResolver.ResolveBattleAgentVisuals was not found.");

            var resolution = method.Invoke(
                null,
                [
                    mainUnitKey,
                    factions,
                    subcultureByFaction,
                    cultureBySubculture,
                    permissions,
                    agentSubtypes,
                    subtypeOverrides,
                    artSets,
                    arts,
                    uniforms,
                    variants,
                ])
                ?? throw new InvalidOperationException(
                    "ResolveBattleAgentVisuals returned null.");

            hasAuthority = (bool)(resolution.GetType()
                .GetProperty("HasAuthoritativeVisualPath")?.GetValue(resolution)
                ?? false);
            issues = ((System.Collections.IEnumerable)(resolution.GetType()
                    .GetProperty("Issues")?.GetValue(resolution)
                    ?? Array.Empty<string>()))
                .Cast<object>()
                .Select(value => value.ToString() ?? string.Empty)
                .ToArray();

            var values = new List<Dictionary<string, string>>();
            var resolvedVariants = (System.Collections.IEnumerable)(resolution.GetType()
                .GetProperty("Variants")?.GetValue(resolution)
                ?? Array.Empty<object>());
            foreach (var value in resolvedVariants)
            {
                var type = value!.GetType();
                values.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["variant"] = type.GetProperty("VariantName")?.GetValue(value)?.ToString() ?? string.Empty,
                    ["consumer"] = type.GetProperty("ConsumerType")?.GetValue(value)?.ToString() ?? string.Empty,
                    ["faction"] = type.GetProperty("FactionKey")?.GetValue(value)?.ToString() ?? string.Empty,
                    ["uniform"] = type.GetProperty("UniformName")?.GetValue(value)?.ToString() ?? string.Empty,
                    ["art_set"] = type.GetProperty("ArtSetId")?.GetValue(value)?.ToString() ?? string.Empty,
                });
            }

            return values;
        }

        private static IReadOnlyDictionary<string, string> GetTransitiveChildVmdParents(
            string rootVmdPath,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> childVmdsByVmd)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var resolverType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3UnitCategoryResolver",
                throwOnError: true)!;
            var method = resolverType.GetMethod(
                "GetTransitiveChildVmdParents",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "Wh3UnitCategoryResolver.GetTransitiveChildVmdParents was not found.");

            return (IReadOnlyDictionary<string, string>)(method.Invoke(
                null,
                [rootVmdPath, childVmdsByVmd, CancellationToken.None])
                ?? throw new InvalidOperationException(
                    "GetTransitiveChildVmdParents returned null."));
        }

        private static string GetGameplayResolutionHealthIssue(
            IReadOnlyDictionary<string, int> parsedRowsByTable,
            IReadOnlyList<string>? diagnostics = null)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var resolverType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3UnitCategoryResolver",
                throwOnError: true)!;
            var method = resolverType.GetMethod(
                "GetGameplayResolutionHealthIssue",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "Wh3UnitCategoryResolver.GetGameplayResolutionHealthIssue was not found.");

            return (string)(method.Invoke(
                null,
                [
                    parsedRowsByTable,
                    diagnostics ?? Array.Empty<string>(),
                    false,
                    Array.Empty<string>(),
                ])
                ?? throw new InvalidOperationException(
                    "GetGameplayResolutionHealthIssue returned null."));
        }

        private static string GetGameplayResolutionHealthIssueForAgents(
            IReadOnlyDictionary<string, int> parsedRowsByTable,
            IReadOnlyList<string>? diagnostics = null,
            IReadOnlyList<string>? semanticIssues = null)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var resolverType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3UnitCategoryResolver",
                throwOnError: true)!;
            var method = resolverType.GetMethod(
                "GetGameplayResolutionHealthIssue",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "Wh3UnitCategoryResolver.GetGameplayResolutionHealthIssue was not found.");

            return (string)(method.Invoke(
                null,
                [
                    parsedRowsByTable,
                    diagnostics ?? Array.Empty<string>(),
                    true,
                    semanticIssues ?? Array.Empty<string>(),
                ])
                ?? throw new InvalidOperationException(
                    "GetGameplayResolutionHealthIssue returned null."));
        }

        [Test]
        public void ChildVmdPropagation_TracksParentAcrossMultipleLevels()
        {
            const string root = @"variantmeshes\variantmeshdefinitions\root.variantmeshdefinition";
            const string child = @"variantmeshes\variantmeshdefinitions\child.variantmeshdefinition";
            const string grandchild = @"variantmeshes\variantmeshdefinitions\grandchild.variantmeshdefinition";

            var parents = GetTransitiveChildVmdParents(
                root,
                new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [root] = [child],
                    [child] = [grandchild],
                    [grandchild] = [root],
                });

            Assert.Multiple(() =>
            {
                Assert.That(parents, Has.Count.EqualTo(2));
                Assert.That(parents[child], Is.EqualTo(root));
                Assert.That(parents[grandchild], Is.EqualTo(child));
            });
        }

        [Test]
        public void GameplayResolutionHealth_RequiresAllCriticalGameplayTables()
        {
            var rows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["main_units_tables"] = 100,
                ["land_units_tables"] = 100,
                ["unit_variants_tables"] = 100,
                ["variants_tables"] = 0,
                ["mounts_tables"] = 10,
                ["battlefield_engines_tables"] = 10,
            };

            var issue = GetGameplayResolutionHealthIssue(rows);

            Assert.That(issue, Does.Contain("variants_tables"));
        }

        [Test]
        public void GameplayResolutionHealth_FailsOnPartialCriticalDecodeFailure()
        {
            var rows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["main_units_tables"] = 100,
                ["land_units_tables"] = 100,
                ["unit_variants_tables"] = 100,
                ["variants_tables"] = 100,
                ["mounts_tables"] = 10,
                ["battlefield_engines_tables"] = 10,
            };

            var issue = GetGameplayResolutionHealthIssue(
                rows,
                ["Failed to decode db/unit_variants_tables/mod_rows from test.pack: bad row"]);

            Assert.That(issue, Does.Contain("unit_variants_tables"));
        }

        [Test]
        public void GameplayResolutionHealth_IsHealthyWhenCriticalTablesDecode()
        {
            var rows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["main_units_tables"] = 100,
                ["land_units_tables"] = 100,
                ["unit_variants_tables"] = 100,
                ["variants_tables"] = 100,
                ["mounts_tables"] = 10,
                ["battlefield_engines_tables"] = 10,
            };

            Assert.That(GetGameplayResolutionHealthIssue(rows), Is.Empty);
        }

        [Test]
        public void GameplayResolutionHealth_AgentSemanticFailureIsFatal()
        {
            var rows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["main_units_tables"] = 100,
                ["land_units_tables"] = 100,
                ["unit_variants_tables"] = 100,
                ["variants_tables"] = 100,
                ["mounts_tables"] = 10,
                ["battlefield_engines_tables"] = 10,
                ["agent_subtypes_tables"] = 10,
                ["agent_subtype_subculture_overrides_tables"] = 10,
                ["campaign_character_art_sets_tables"] = 10,
                ["campaign_character_arts_tables"] = 10,
                ["agent_uniforms_tables"] = 10,
                ["units_custom_battle_permissions_tables"] = 10,
                ["factions_tables"] = 10,
                ["cultures_subcultures_tables"] = 10,
            };

            var issue = GetGameplayResolutionHealthIssueForAgents(
                rows,
                semanticIssues: ["main_hero: missing explicit uniform"]);

            Assert.That(issue, Does.Contain("agent visual reference coverage failed"));
        }

        [Test]
        public void BattleAgentVisual_GeneralUniformOverridesArtSetForFaction()
        {
            var resolved = ResolveBattleAgentVisuals(
                "main_hero",
                ["faction_a"],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["faction_a"] = "sub_a",
                },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sub_a"] = "culture_a",
                },
                [
                    new Dictionary<string, string>
                    {
                        ["unit"] = "main_hero",
                        ["faction"] = "faction_a",
                        ["general_uniform"] = "explicit_uniform",
                    },
                ],
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["hero_subtype"] = new()
                    {
                        ["key"] = "hero_subtype",
                        ["associated_unit_override"] = "main_hero",
                    },
                },
                new(StringComparer.OrdinalIgnoreCase),
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["generic_art"] = new()
                    {
                        ["art_set_id"] = "generic_art",
                        ["agent_subtype"] = "hero_subtype",
                    },
                },
                [
                    new Dictionary<string, string>
                    {
                        ["art_set_id"] = "generic_art",
                        ["level"] = "1",
                        ["age"] = "0",
                        ["season"] = "none",
                        ["uniform"] = "art_uniform",
                    },
                ],
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["explicit_uniform"] = new()
                    {
                        ["uniform_name"] = "explicit_uniform",
                        ["battle_filename"] = "explicit_variant",
                    },
                    ["art_uniform"] = new()
                    {
                        ["uniform_name"] = "art_uniform",
                        ["battle_filename"] = "art_variant",
                    },
                },
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["explicit_variant"] = new() { ["variant_name"] = "explicit_variant" },
                    ["art_variant"] = new() { ["variant_name"] = "art_variant" },
                },
                out var hasAuthority,
                out var issues);

            Assert.Multiple(() =>
            {
                Assert.That(hasAuthority, Is.True);
                Assert.That(issues, Is.Empty);
                Assert.That(resolved, Has.Count.EqualTo(1));
                Assert.That(resolved[0]["variant"], Is.EqualTo("explicit_variant"));
                Assert.That(resolved[0]["consumer"], Is.EqualTo("CustomBattleGeneralUniform"));
                Assert.That(resolved[0]["uniform"], Is.EqualTo("explicit_uniform"));
            });
        }

        [Test]
        public void BattleAgentVisual_UsesMostSpecificApplicableArtSet()
        {
            var resolved = ResolveBattleAgentVisuals(
                "main_lord",
                ["faction_a"],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["faction_a"] = "sub_a",
                },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sub_a"] = "culture_a",
                },
                [],
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["lord_subtype"] = new()
                    {
                        ["key"] = "lord_subtype",
                        ["associated_unit_override"] = "main_lord",
                    },
                },
                new(StringComparer.OrdinalIgnoreCase),
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["generic"] = new()
                    {
                        ["art_set_id"] = "generic",
                        ["agent_subtype"] = "lord_subtype",
                    },
                    ["wrong_faction"] = new()
                    {
                        ["art_set_id"] = "wrong_faction",
                        ["agent_subtype"] = "lord_subtype",
                        ["faction"] = "faction_b",
                    },
                    ["specific"] = new()
                    {
                        ["art_set_id"] = "specific",
                        ["agent_subtype"] = "lord_subtype",
                        ["faction"] = "faction_a",
                    },
                },
                [
                    new Dictionary<string, string>
                    {
                        ["art_set_id"] = "generic",
                        ["level"] = "1",
                        ["age"] = "0",
                        ["season"] = "none",
                        ["uniform"] = "generic_uniform",
                    },
                    new Dictionary<string, string>
                    {
                        ["art_set_id"] = "specific",
                        ["level"] = "1",
                        ["age"] = "0",
                        ["season"] = "none",
                        ["uniform"] = "specific_uniform",
                    },
                ],
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["generic_uniform"] = new()
                    {
                        ["uniform_name"] = "generic_uniform",
                        ["battle_filename"] = "generic_variant",
                    },
                    ["specific_uniform"] = new()
                    {
                        ["uniform_name"] = "specific_uniform",
                        ["battle_filename"] = "specific_variant",
                    },
                },
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["generic_variant"] = new() { ["variant_name"] = "generic_variant" },
                    ["specific_variant"] = new() { ["variant_name"] = "specific_variant" },
                },
                out var hasAuthority,
                out var issues);

            Assert.Multiple(() =>
            {
                Assert.That(hasAuthority, Is.True);
                Assert.That(issues, Is.Empty);
                Assert.That(resolved.Select(value => value["variant"]),
                    Is.EqualTo(new[] { "specific_variant" }));
                Assert.That(resolved[0]["art_set"], Is.EqualTo("specific"));
                Assert.That(resolved[0]["consumer"], Is.EqualTo("BattleAgentArtSet"));
            });
        }

        [Test]
        public void BattleAgentVisual_SubtypeOverrideRespectsAgentType()
        {
            var resolved = ResolveBattleAgentVisuals(
                "main_hero",
                ["faction_a"],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["faction_a"] = "sub_a",
                },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                [],
                new(StringComparer.OrdinalIgnoreCase),
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["sub_a|shared|champion"] = new()
                    {
                        ["subculture"] = "sub_a",
                        ["subtype"] = "shared_subtype",
                        ["agent"] = "champion",
                        ["associated_unit_override"] = "main_hero",
                    },
                    ["sub_a|shared|general"] = new()
                    {
                        ["subculture"] = "sub_a",
                        ["subtype"] = "shared_subtype",
                        ["agent"] = "general",
                        ["associated_unit_override"] = "other_unit",
                    },
                },
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["champion_art"] = new()
                    {
                        ["art_set_id"] = "champion_art",
                        ["agent_subtype"] = "shared_subtype",
                        ["agent_type"] = "champion",
                    },
                    ["general_art"] = new()
                    {
                        ["art_set_id"] = "general_art",
                        ["agent_subtype"] = "shared_subtype",
                        ["agent_type"] = "general",
                    },
                },
                [
                    new Dictionary<string, string>
                    {
                        ["art_set_id"] = "champion_art",
                        ["level"] = "1",
                        ["age"] = "0",
                        ["season"] = "none",
                        ["uniform"] = "champion_uniform",
                    },
                    new Dictionary<string, string>
                    {
                        ["art_set_id"] = "general_art",
                        ["level"] = "1",
                        ["age"] = "0",
                        ["season"] = "none",
                        ["uniform"] = "general_uniform",
                    },
                ],
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["champion_uniform"] = new()
                    {
                        ["uniform_name"] = "champion_uniform",
                        ["battle_filename"] = "champion_variant",
                    },
                    ["general_uniform"] = new()
                    {
                        ["uniform_name"] = "general_uniform",
                        ["battle_filename"] = "general_variant",
                    },
                },
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["champion_variant"] = new() { ["variant_name"] = "champion_variant" },
                    ["general_variant"] = new() { ["variant_name"] = "general_variant" },
                },
                out var hasAuthority,
                out var issues);

            Assert.Multiple(() =>
            {
                Assert.That(hasAuthority, Is.True);
                Assert.That(issues, Is.Empty);
                Assert.That(resolved.Select(value => value["variant"]),
                    Is.EqualTo(new[] { "champion_variant" }));
            });
        }

        [Test]
        public void BattleAgentVisual_BrokenGeneralUniformIsAuthoritativeAndUnhealthy()
        {
            var resolved = ResolveBattleAgentVisuals(
                "main_hero",
                ["faction_a"],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                [
                    new Dictionary<string, string>
                    {
                        ["unit"] = "main_hero",
                        ["faction"] = "faction_a",
                        ["general_uniform"] = "missing_uniform",
                    },
                ],
                new(StringComparer.OrdinalIgnoreCase),
                new(StringComparer.OrdinalIgnoreCase),
                new(StringComparer.OrdinalIgnoreCase),
                [],
                new(StringComparer.OrdinalIgnoreCase),
                new(StringComparer.OrdinalIgnoreCase),
                out var hasAuthority,
                out var issues);

            Assert.Multiple(() =>
            {
                Assert.That(hasAuthority, Is.True);
                Assert.That(resolved, Is.Empty);
                Assert.That(issues, Has.Count.EqualTo(1));
                Assert.That(issues[0], Does.Contain("missing_uniform"));
            });
        }

        [Test]
        public void BattleAgentVisual_PrefersBattleUniformVariant()
        {
            var variants = ResolveBattleAgentVariantNames(
                "main_lord",
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["lord_subtype"] = new()
                    {
                        ["key"] = "lord_subtype",
                        ["associated_unit_override"] = "main_lord",
                    },
                },
                new(StringComparer.OrdinalIgnoreCase),
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["lord_art"] = new()
                    {
                        ["art_set_id"] = "lord_art",
                        ["agent_subtype"] = "lord_subtype",
                    },
                },
                [
                    new()
                    {
                        ["art_set_id"] = "lord_art",
                        ["level"] = "1",
                        ["age"] = "0",
                        ["season"] = "none",
                        ["uniform"] = "lord_uniform",
                    },
                ],
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["lord_uniform"] = new()
                    {
                        ["uniform_name"] = "lord_uniform",
                        ["battle_filename"] = "lord_battle_variant",
                        ["filename"] = "lord_campaign_variant",
                    },
                });

            Assert.That(variants, Is.EqualTo(new[] { "lord_battle_variant" }));
        }

        [Test]
        public void BattleAgentVisual_FallsBackToUniformFilenameWhenBattleFilenameIsMissing()
        {
            var variants = ResolveBattleAgentVariantNames(
                "main_hero",
                new(StringComparer.OrdinalIgnoreCase),
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["hero_subtype"] = new()
                    {
                        ["subtype"] = "hero_subtype",
                        ["associated_unit_override"] = "main_hero",
                    },
                },
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["hero_art"] = new()
                    {
                        ["art_set_id"] = "hero_art",
                        ["agent_subtype"] = "hero_subtype",
                    },
                },
                [
                    new()
                    {
                        ["art_set_id"] = "hero_art",
                        ["level"] = "1",
                        ["age"] = "0",
                        ["season"] = "none",
                        ["uniform"] = "hero_uniform",
                    },
                ],
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["hero_uniform"] = new()
                    {
                        ["uniform_name"] = "hero_uniform",
                        ["battle_filename"] = ".",
                        ["filename"] = "hero_fallback_variant",
                    },
                });

            Assert.That(variants, Is.EqualTo(new[] { "hero_fallback_variant" }));
        }

        [Test]
        public void BattleAgentVisual_UsesLowestLevelAgeAndPrefersNoSeason()
        {
            var variants = ResolveBattleAgentVariantNames(
                "main_lord",
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["lord_subtype"] = new()
                    {
                        ["key"] = "lord_subtype",
                        ["associated_unit_override"] = "main_lord",
                    },
                },
                new(StringComparer.OrdinalIgnoreCase),
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["lord_art"] = new()
                    {
                        ["art_set_id"] = "lord_art",
                        ["agent_subtype"] = "lord_subtype",
                    },
                },
                [
                    new()
                    {
                        ["art_set_id"] = "lord_art",
                        ["level"] = "2",
                        ["age"] = "0",
                        ["season"] = "none",
                        ["uniform"] = "later",
                    },
                    new()
                    {
                        ["art_set_id"] = "lord_art",
                        ["level"] = "1",
                        ["age"] = "1",
                        ["season"] = "none",
                        ["uniform"] = "older",
                    },
                    new()
                    {
                        ["art_set_id"] = "lord_art",
                        ["level"] = "1",
                        ["age"] = "0",
                        ["season"] = "winter",
                        ["uniform"] = "winter",
                    },
                    new()
                    {
                        ["art_set_id"] = "lord_art",
                        ["level"] = "1",
                        ["age"] = "0",
                        ["season"] = "none",
                        ["uniform"] = "baseline",
                    },
                ],
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["later"] = new() { ["uniform_name"] = "later", ["battle_filename"] = "later_variant" },
                    ["older"] = new() { ["uniform_name"] = "older", ["battle_filename"] = "older_variant" },
                    ["winter"] = new() { ["uniform_name"] = "winter", ["battle_filename"] = "winter_variant" },
                    ["baseline"] = new() { ["uniform_name"] = "baseline", ["battle_filename"] = "baseline_variant" },
                });

            Assert.That(variants, Is.EqualTo(new[] { "baseline_variant" }));
        }

    }
}
