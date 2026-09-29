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


    }
}
