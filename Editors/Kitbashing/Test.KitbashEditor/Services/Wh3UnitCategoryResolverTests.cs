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


    }
}
