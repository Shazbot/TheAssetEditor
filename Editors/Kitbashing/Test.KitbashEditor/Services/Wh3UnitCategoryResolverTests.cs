using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Moq;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;

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

        private static byte[] BuildPackedTableRows(
            string tableName,
            IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var resolverType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3UnitCategoryResolver",
                throwOnError: true)!;
            var schemaField = resolverType.GetField(
                "Schema",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "Wh3UnitCategoryResolver.Schema was not found.");
            var lazySchema = schemaField.GetValue(null)
                ?? throw new InvalidOperationException("Resolver schema was null.");
            var schemaRoot = lazySchema.GetType().GetProperty("Value")?.GetValue(lazySchema)
                ?? throw new InvalidOperationException("Resolver schema value was null.");
            var definitions = (IDictionary)(schemaRoot.GetType()
                .GetProperty("Definitions")?.GetValue(schemaRoot)
                ?? throw new InvalidOperationException("Resolver schema definitions were null."));
            var versions = (IEnumerable)(definitions[tableName]
                ?? throw new InvalidOperationException($"Schema table '{tableName}' was not found."));
            var version = versions.Cast<object>().First();
            var packedVersion = (int)(version.GetType().GetProperty("Version")?.GetValue(version)
                ?? throw new InvalidOperationException("Schema version was missing."));
            var fields = ((IEnumerable)(version.GetType().GetProperty("Fields")?.GetValue(version)
                ?? throw new InvalidOperationException("Schema fields were missing.")))
                .Cast<object>()
                .ToArray();

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
            writer.Write(new byte[] { 0xfc, 0xfd, 0xfe, 0xff });
            writer.Write(packedVersion);
            writer.Write((byte)0);
            writer.Write(rows.Count);

            foreach (var row in rows)
            {
                foreach (var field in fields)
                {
                    var fieldType = field.GetType().GetProperty("FieldType")?.GetValue(field)?.ToString()
                        ?? throw new InvalidOperationException("Schema field type was missing.");
                    var fieldName = field.GetType().GetProperty("Name")?.GetValue(field)?.ToString()
                        ?? throw new InvalidOperationException("Schema field name was missing.");
                    var value = row.TryGetValue(fieldName, out var supplied)
                        ? supplied
                        : string.Empty;

                    switch (fieldType)
                    {
                        case "Boolean":
                            writer.Write((byte)(value.Length != 0 && value != "0" ? 1 : 0));
                            break;
                        case "ColourRGB":
                        case "I32":
                            writer.Write(value.Length == 0
                                ? 0
                                : int.Parse(value, CultureInfo.InvariantCulture));
                            break;
                        case "I16":
                            writer.Write(value.Length == 0
                                ? (short)0
                                : short.Parse(value, CultureInfo.InvariantCulture));
                            break;
                        case "I64":
                            writer.Write(value.Length == 0
                                ? 0L
                                : long.Parse(value, CultureInfo.InvariantCulture));
                            break;
                        case "F32":
                            writer.Write(value.Length == 0
                                ? 0f
                                : float.Parse(value, CultureInfo.InvariantCulture));
                            break;
                        case "F64":
                            writer.Write(value.Length == 0
                                ? 0d
                                : double.Parse(value, CultureInfo.InvariantCulture));
                            break;
                        case "StringU8":
                            WriteStringU8(writer, value);
                            break;
                        case "StringU16":
                            writer.Write((short)value.Length);
                            writer.Write(Encoding.Unicode.GetBytes(value));
                            break;
                        case "OptionalStringU8":
                            if (value.Length == 0)
                            {
                                writer.Write((byte)0);
                            }
                            else
                            {
                                writer.Write((byte)1);
                                WriteStringU8(writer, value);
                            }
                            break;
                        default:
                            throw new InvalidOperationException(
                                $"Unsupported fixture field type '{fieldType}'.");
                    }
                }
            }

            writer.Flush();
            return stream.ToArray();
        }

        private static Dictionary<string, List<Dictionary<string, string>>>
            CreateMinimalGameplayRows(
                string mainUnitKey = "main_hero",
                string landUnitKey = "land_hero",
                string fallbackVariant = "fallback_variant",
                string fallbackVmd = "fallback")
            => new(StringComparer.OrdinalIgnoreCase)
            {
                ["main_units_tables"] =
                [
                    new()
                    {
                        ["unit"] = mainUnitKey,
                        ["land_unit"] = landUnitKey,
                        ["caste"] = "hero",
                        ["num_men"] = "1",
                    },
                ],
                ["land_units_tables"] =
                [
                    new()
                    {
                        ["key"] = landUnitKey,
                        ["category"] = "inf_melee",
                    },
                ],
                ["unit_variants_tables"] =
                [
                    new()
                    {
                        ["faction"] = "",
                        ["unit"] = landUnitKey,
                        ["name"] = "body",
                        ["variant"] = fallbackVariant,
                    },
                ],
                ["variants_tables"] =
                [
                    new()
                    {
                        ["variant_name"] = fallbackVariant,
                        ["variant_filename"] = fallbackVmd,
                    },
                ],
                ["mounts_tables"] =
                [
                    new() { ["key"] = "unused_mount", ["variant"] = fallbackVariant },
                ],
                ["battlefield_engines_tables"] =
                [
                    new()
                    {
                        ["key"] = "unused_engine",
                        ["engine_type"] = "Generic_No_Crew_Rotate",
                        ["variant"] = fallbackVariant,
                    },
                ],
            };

        private static object ResolveFromDecodedRows(
            Dictionary<string, List<Dictionary<string, string>>> rowsByTable,
            params string[] rootVmdPaths)
        {
            var packedFiles = rowsByTable.ToDictionary(
                entry => entry.Key,
                entry => PackFile.CreateFromBytes(
                    $"{entry.Key}.bin",
                    BuildPackedTableRows(
                        entry.Key,
                        entry.Value.Cast<IReadOnlyDictionary<string, string>>().ToArray())),
                StringComparer.OrdinalIgnoreCase);

            var source = new Mock<IPackFileContainer>();
            source.SetupGet(container => container.Name).Returns("fixture.pack");
            source.SetupGet(container => container.SystemFilePath).Returns("fixture.pack");
            source.SetupGet(container => container.IsCaPackFile).Returns(false);
            source.Setup(container => container.GetDirectoryContent(It.IsAny<string>()))
                .Returns((string directoryPath) =>
                {
                    var normalized = directoryPath.Replace('/', '\\');
                    var tableName = normalized.StartsWith(
                        "db\\",
                        StringComparison.OrdinalIgnoreCase)
                        ? normalized[3..]
                        : normalized;
                    if (!packedFiles.TryGetValue(tableName, out var file))
                        return [];

                    return
                    [
                        ($"{normalized}\\fixture", file),
                    ];
                });
            source.Setup(container => container.ContainsFile(It.IsAny<string>()))
                .Returns(false);

            var packFileService = new Mock<IPackFileService>();
            packFileService.Setup(service => service.GetAllPackfileContainers())
                .Returns([source.Object]);

            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var resolverType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3UnitCategoryResolver",
                throwOnError: true)!;
            var method = resolverType.GetMethod(
                "Resolve",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "Wh3UnitCategoryResolver.Resolve was not found.");

            return method.Invoke(
                null,
                [
                    packFileService.Object,
                    source.Object,
                    rootVmdPaths,
                    new Dictionary<string, IReadOnlyCollection<string>>(
                        StringComparer.OrdinalIgnoreCase),
                    CancellationToken.None,
                    null,
                ])
                ?? throw new InvalidOperationException("Resolve returned null.");
        }

        private static bool ResolutionHasVmd(object resolution, string vmdPath)
        {
            var usages = (IDictionary)(resolution.GetType()
                .GetProperty("UsagesByVmd")?.GetValue(resolution)
                ?? throw new InvalidOperationException("UsagesByVmd was not found."));
            var normalized = vmdPath.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
            return usages.Contains(normalized);
        }

        private static bool ResolutionIsHealthy(object resolution)
            => (bool)(resolution.GetType().GetProperty("IsGameplayResolutionHealthy")
                ?.GetValue(resolution)
                ?? throw new InvalidOperationException(
                    "IsGameplayResolutionHealthy was not found."));

        private static string ResolutionHealthMessage(object resolution)
            => resolution.GetType().GetProperty("GameplayResolutionHealthMessage")
                ?.GetValue(resolution)?.ToString()
                ?? string.Empty;

        private static bool ResolutionHasUnresolvedConsumer(
            object resolution,
            string vmdPath,
            string mainUnitKey)
        {
            var unresolved = (IDictionary)(resolution.GetType()
                .GetProperty("UnresolvedConsumersByVmd")?.GetValue(resolution)
                ?? throw new InvalidOperationException(
                    "UnresolvedConsumersByVmd was not found."));
            var normalized = vmdPath.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
            if (!unresolved.Contains(normalized))
                return false;

            return ((IEnumerable)(unresolved[normalized]
                    ?? throw new InvalidOperationException("Unresolved consumer list was null.")))
                .Cast<object>()
                .Any(consumer =>
                    string.Equals(
                        consumer.GetType().GetProperty("MainUnitKey")?.GetValue(consumer)?.ToString(),
                        mainUnitKey,
                        StringComparison.OrdinalIgnoreCase));
        }

        private static IReadOnlySet<string> ResolutionRosterScopeValues(
            object resolution,
            string mainUnitKey,
            string propertyName)
        {
            var roster = (IEnumerable)(resolution.GetType()
                .GetProperty("RosterUnits")?.GetValue(resolution)
                ?? throw new InvalidOperationException("RosterUnits was not found."));
            var unit = roster.Cast<object>().Single(candidate =>
                string.Equals(
                    candidate.GetType().GetProperty("MainUnitKey")?.GetValue(candidate)?.ToString(),
                    mainUnitKey,
                    StringComparison.OrdinalIgnoreCase));
            var values = (IEnumerable)(unit.GetType().GetProperty(propertyName)?.GetValue(unit)
                ?? throw new InvalidOperationException(
                    $"Roster scope property '{propertyName}' was not found."));
            return values.Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
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

        private static IReadOnlyList<(string Child, string Parent)> GetTransitiveChildVmdParents(
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

            var edges = (IEnumerable)(method.Invoke(
                null,
                [rootVmdPath, childVmdsByVmd, CancellationToken.None])
                ?? throw new InvalidOperationException(
                    "GetTransitiveChildVmdParents returned null."));

            return edges.Cast<object>()
                .Select(edge =>
                {
                    var edgeType = edge.GetType();
                    return (
                        Child: edgeType.GetProperty("Key")?.GetValue(edge)?.ToString()
                            ?? string.Empty,
                        Parent: edgeType.GetProperty("Value")?.GetValue(edge)?.ToString()
                            ?? string.Empty);
                })
                .ToArray();
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
                Assert.That(
                    parents.Single(edge => edge.Child == child).Parent,
                    Is.EqualTo(root));
                Assert.That(
                    parents.Single(edge => edge.Child == grandchild).Parent,
                    Is.EqualTo(child));
            });
        }

        [Test]
        public void ChildVmdPropagation_PreservesBothParentsInDiamondGraph()
        {
            const string root = @"variantmeshes\variantmeshdefinitions\root.variantmeshdefinition";
            const string left = @"variantmeshes\variantmeshdefinitions\left.variantmeshdefinition";
            const string right = @"variantmeshes\variantmeshdefinitions\right.variantmeshdefinition";
            const string child = @"variantmeshes\variantmeshdefinitions\child.variantmeshdefinition";

            var edges = GetTransitiveChildVmdParents(
                root,
                new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [root] = [left, right],
                    [left] = [child],
                    [right] = [child],
                });

            var childParents = edges
                .Where(edge => edge.Child == child)
                .Select(edge => edge.Parent)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.That(childParents, Is.EquivalentTo(new[] { left, right }));
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
        public void GameplayResolutionHealth_AgentCriticalDecodeFailureIsGlobal()
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
                ["Failed to decode db/agent_subtypes_tables/mod_rows from test.pack: bad row"]);

            Assert.That(issue, Does.Contain("agent_subtypes_tables"));
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
        public void GameplayResolutionHealth_AgentSemanticFailureIsConsumerLocal()
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

            Assert.That(issue, Is.Empty);
        }

        [Test]
        public void FullResolver_UnknownFactionKeepsScopedCampaignArtReachable()
        {
            var rows = CreateMinimalGameplayRows();
            rows["variants_tables"].Add(
                new()
                {
                    ["variant_name"] = "scoped_variant",
                    ["variant_filename"] = "scoped_agent",
                });
            rows["agent_subtypes_tables"] =
            [
                new()
                {
                    ["key"] = "hero_subtype",
                    ["associated_unit_override"] = "main_hero",
                },
            ];
            rows["agent_subtype_subculture_overrides_tables"] =
            [
                new()
                {
                    ["subculture"] = "unused_sub",
                    ["subtype"] = "unused_subtype",
                    ["agent"] = "champion",
                    ["associated_unit_override"] = "other_unit",
                },
            ];
            rows["campaign_character_art_sets_tables"] =
            [
                new()
                {
                    ["art_set_id"] = "scoped_art",
                    ["agent_subtype"] = "hero_subtype",
                    ["faction"] = "faction_a",
                },
            ];
            rows["campaign_character_arts_tables"] =
            [
                new()
                {
                    ["id"] = "1",
                    ["art_set_id"] = "scoped_art",
                    ["level"] = "1",
                    ["age"] = "0",
                    ["season"] = "none",
                    ["uniform"] = "scoped_uniform",
                },
            ];
            rows["agent_uniforms_tables"] =
            [
                new()
                {
                    ["uniform_name"] = "scoped_uniform",
                    ["battle_filename"] = "scoped_variant",
                },
            ];
            rows["units_custom_battle_permissions_tables"] =
            [
                new()
                {
                    ["faction"] = "unused_faction",
                    ["unit"] = "other_unit",
                },
            ];
            rows["factions_tables"] =
            [
                new() { ["key"] = "faction_a", ["subculture"] = "sub_a" },
            ];
            rows["cultures_subcultures_tables"] =
            [
                new() { ["subculture"] = "sub_a", ["culture"] = "culture_a" },
            ];

            const string scopedVmd =
                @"variantmeshes\variantmeshdefinitions\scoped_agent.variantmeshdefinition";
            var resolution = ResolveFromDecodedRows(rows, scopedVmd);

            Assert.Multiple(() =>
            {
                Assert.That(ResolutionHasVmd(resolution, scopedVmd), Is.True);
                Assert.That(ResolutionIsHealthy(resolution), Is.True);
                Assert.That(
                    ResolutionRosterScopeValues(resolution, "main_hero", "FactionKeys"),
                    Does.Contain("faction_a"));
                Assert.That(
                    ResolutionRosterScopeValues(resolution, "main_hero", "SubcultureKeys"),
                    Does.Contain("sub_a"));
                Assert.That(
                    ResolutionRosterScopeValues(resolution, "main_hero", "CultureKeys"),
                    Does.Contain("culture_a"));
            });
        }

        [Test]
        public void FullResolver_FallbackOnlyHeroDoesNotRequireOptionalAgentTables()
        {
            var rows = CreateMinimalGameplayRows();
            const string fallbackVmd =
                @"variantmeshes\variantmeshdefinitions\fallback.variantmeshdefinition";

            var resolution = ResolveFromDecodedRows(rows, fallbackVmd);

            Assert.Multiple(() =>
            {
                Assert.That(ResolutionHasVmd(resolution, fallbackVmd), Is.True);
                Assert.That(ResolutionIsHealthy(resolution), Is.True);
                Assert.That(ResolutionHealthMessage(resolution), Is.Empty);
            });
        }

        [Test]
        public void FullResolver_SharedVmdWithUnresolvedHeroIsResolvedButTainted()
        {
            var rows = CreateMinimalGameplayRows();
            rows["main_units_tables"].Add(
                new()
                {
                    ["unit"] = "main_infantry",
                    ["land_unit"] = "land_infantry",
                    ["caste"] = "melee_infantry",
                    ["num_men"] = "100",
                });
            rows["land_units_tables"].Add(
                new()
                {
                    ["key"] = "land_infantry",
                    ["category"] = "inf_melee",
                });
            rows["unit_variants_tables"].Add(
                new()
                {
                    ["faction"] = "",
                    ["unit"] = "land_infantry",
                    ["name"] = "body",
                    ["variant"] = "fallback_variant",
                });

            rows["agent_subtypes_tables"] =
            [
                new()
                {
                    ["key"] = "hero_subtype",
                    ["associated_unit_override"] = "main_hero",
                },
            ];
            rows["agent_subtype_subculture_overrides_tables"] =
            [
                new()
                {
                    ["subculture"] = "unused_sub",
                    ["subtype"] = "unused_subtype",
                    ["agent"] = "champion",
                    ["associated_unit_override"] = "other_unit",
                },
            ];
            rows["campaign_character_art_sets_tables"] =
            [
                new()
                {
                    ["art_set_id"] = "unrelated_art",
                    ["agent_subtype"] = "other_subtype",
                },
            ];
            rows["campaign_character_arts_tables"] =
            [
                new()
                {
                    ["id"] = "2",
                    ["art_set_id"] = "unrelated_art",
                    ["level"] = "1",
                    ["age"] = "0",
                    ["season"] = "none",
                    ["uniform"] = "unused_uniform",
                },
            ];
            rows["agent_uniforms_tables"] =
            [
                new()
                {
                    ["uniform_name"] = "unused_uniform",
                    ["battle_filename"] = "fallback_variant",
                },
            ];
            rows["units_custom_battle_permissions_tables"] =
            [
                new()
                {
                    ["faction"] = "unused_faction",
                    ["unit"] = "other_unit",
                },
            ];
            rows["factions_tables"] =
            [
                new() { ["key"] = "unused_faction", ["subculture"] = "unused_sub" },
            ];
            rows["cultures_subcultures_tables"] =
            [
                new() { ["subculture"] = "unused_sub", ["culture"] = "unused_culture" },
            ];

            const string fallbackVmd =
                @"variantmeshes\variantmeshdefinitions\fallback.variantmeshdefinition";
            var resolution = ResolveFromDecodedRows(rows, fallbackVmd);

            Assert.Multiple(() =>
            {
                // The regular unit keeps the shared VMD in the known usage graph, while the
                // unresolved hero taints it so gameplay atlas selection cannot treat that
                // partial usage set as complete.
                Assert.That(ResolutionHasVmd(resolution, fallbackVmd), Is.True);
                Assert.That(
                    ResolutionHasUnresolvedConsumer(
                        resolution,
                        fallbackVmd,
                        "main_hero"),
                    Is.True);
                Assert.That(ResolutionIsHealthy(resolution), Is.True);
                Assert.That(ResolutionHealthMessage(resolution), Is.Empty);
            });
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
        public void BattleAgentVisual_UnknownFactionUnionsScopedArtSets()
        {
            var resolved = ResolveBattleAgentVisuals(
                "main_lord",
                [],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
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
                    ["faction_a_art"] = new()
                    {
                        ["art_set_id"] = "faction_a_art",
                        ["agent_subtype"] = "lord_subtype",
                        ["faction"] = "faction_a",
                    },
                    ["faction_b_art"] = new()
                    {
                        ["art_set_id"] = "faction_b_art",
                        ["agent_subtype"] = "lord_subtype",
                        ["faction"] = "faction_b",
                    },
                },
                [
                    new()
                    {
                        ["art_set_id"] = "faction_a_art",
                        ["level"] = "1",
                        ["age"] = "0",
                        ["season"] = "none",
                        ["uniform"] = "uniform_a",
                    },
                    new()
                    {
                        ["art_set_id"] = "faction_b_art",
                        ["level"] = "1",
                        ["age"] = "0",
                        ["season"] = "none",
                        ["uniform"] = "uniform_b",
                    },
                ],
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["uniform_a"] = new()
                    {
                        ["uniform_name"] = "uniform_a",
                        ["battle_filename"] = "variant_a",
                    },
                    ["uniform_b"] = new()
                    {
                        ["uniform_name"] = "uniform_b",
                        ["battle_filename"] = "variant_b",
                    },
                },
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["variant_a"] = new() { ["variant_name"] = "variant_a" },
                    ["variant_b"] = new() { ["variant_name"] = "variant_b" },
                },
                out var hasAuthority,
                out var issues);

            Assert.Multiple(() =>
            {
                Assert.That(hasAuthority, Is.False);
                Assert.That(issues, Is.Empty);
                Assert.That(
                    resolved.Select(value => value["variant"]),
                    Is.EquivalentTo(new[] { "variant_a", "variant_b" }));
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
