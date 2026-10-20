using System.IO.Compression;
using System.Reflection;
using System.Text;
using Editors.PackOptimizer.Core;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Serialization;
using Shared.Core.PackFiles.Utility;

namespace Test.PackOptimizer.Core.Services
{
    [TestFixture]
    public class DbSchemaTests
    {
        private static readonly Type ResolverType = Assembly.Load("Editors.PackOptimizer.Core")
            .GetType("Editors.PackOptimizer.Core.Wh3UnitCategoryResolver", throwOnError: true)!;

        private static List<Dictionary<string, string>> Decode(
            string table,
            byte[] data,
            List<string> diagnostics,
            PackOptimizerDbSchema? supplied,
            Dictionary<string, int>? sources = null)
            => (List<Dictionary<string, string>>)Invoke(
                ResolverType.GetMethod("DecodeTableWithSchemas", BindingFlags.NonPublic | BindingFlags.Static)!,
                table, data, $"db/{table}/test", diagnostics, supplied, sources);

        private static object Invoke(MethodInfo method, params object?[] arguments)
        {
            try
            {
                return method.Invoke(null, arguments)!;
            }
            catch (TargetInvocationException ex)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
                throw;
            }
        }

        private string _directory = null!;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "db-schema-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown() => Directory.Delete(_directory, recursive: true);

        // A table of the layout (key StringU8, order I32) at the given version, rows included.
        private static byte[] Table(int version, int rows, bool extraByte = false)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
            writer.Write(new byte[] { 0xfc, 0xfd, 0xfe, 0xff });
            writer.Write(version);
            writer.Write((byte)0);
            writer.Write(rows);
            for (var i = 0; i < rows; i++)
            {
                var key = Encoding.ASCII.GetBytes("key" + i);
                writer.Write((ushort)key.Length);
                writer.Write(key);
                writer.Write(i);
            }

            if (extraByte)
                writer.Write((byte)9);
            writer.Flush();
            return stream.ToArray();
        }

        private string WriteSchema(string fileName, string json, bool zstd = false)
        {
            var path = Path.Combine(_directory, fileName);
            if (!zstd)
            {
                File.WriteAllText(path, json);
                return path;
            }

            using var compressor = new ZstdSharp.Compressor();
            File.WriteAllBytes(path, compressor.Wrap(Encoding.UTF8.GetBytes(json)).ToArray());
            return path;
        }

        private static string SchemaJson(string table, int version, string type = "I32", string secondField = "order")
            => "{\"version\":5,\"definitions\":{\"" + table + "\":[{\"version\":" + version + ",\"fields\":[" +
               "{\"name\":\"key\",\"field_type\":\"StringU8\"},{\"name\":\"" + secondField + "\",\"field_type\":\"" + type + "\"}]}]}}";

        [Test]
        public void SuppliedSchema_DecodesAVersionTheEmbeddedSchemaDoesNotKnow()
        {
            // ui_unit_groupings_tables is known to the embedded schema up to version 1 only.
            const string table = "ui_unit_group_parents_tables";
            var supplied = PackOptimizerDbSchema.Load(WriteSchema("future.json.zst", SchemaJson(table, 2), zstd: true));
            var sources = new Dictionary<string, int>();

            var withoutSupplied = new List<string>();
            var skipped = Decode(table, Table(2, 3), withoutSupplied, null);
            var rows = Decode(table, Table(2, 3), [], supplied, sources);

            Assert.That(skipped, Is.Empty);
            Assert.That(withoutSupplied.Single(), Does.Contain("no schema"));
            Assert.That(rows.Select(row => row["key"]), Is.EqualTo(new[] { "key0", "key1", "key2" }));
            Assert.That(rows[2]["order"], Is.EqualTo("2"));
            Assert.That(sources.Keys.Single(), Is.EqualTo($"{table} v2: supplied schema"));
        }

        [Test]
        public void SuppliedSchema_IsTriedFirst_AndTheEmbeddedOneFillsTheGaps()
        {
            const string table = "ui_unit_group_parents_tables";
            // Embedded version 1: icon, key, order, mp_cap. The supplied layout of the same version
            // renames "order": which name the row has says which schema decoded it.
            var bytes = Version1Table();
            var supplied = PackOptimizerDbSchema.Load(WriteSchema(
                "renamed.json",
                "{\"definitions\":{\"" + table + "\":[{\"version\":1,\"fields\":[" +
                "{\"name\":\"icon\",\"field_type\":\"StringU8\"},{\"name\":\"key\",\"field_type\":\"StringU8\"}," +
                "{\"name\":\"renamed\",\"field_type\":\"I32\"},{\"name\":\"mp_cap\",\"field_type\":\"I32\"}]}]}}"));
            var sources = new Dictionary<string, int>();

            var fromSupplied = Decode(table, bytes, [], supplied, sources);
            Assert.That(fromSupplied[0].ContainsKey("renamed"), Is.True);
            Assert.That(sources.Keys.Single(), Does.EndWith("supplied schema"));

            Assert.That(Decode(table, bytes, [], null)[0].ContainsKey("order"), Is.True);

            // A supplied schema without the table: the embedded layout decodes it.
            var other = PackOptimizerDbSchema.Load(WriteSchema("other.json", SchemaJson("main_units_tables", 7)));
            var fallbackSources = new Dictionary<string, int>();
            var rows = Decode(table, bytes, [], other, fallbackSources);
            Assert.That(rows[0].ContainsKey("order"), Is.True);
            Assert.That(fallbackSources.Keys.Single(), Does.EndWith("embedded schema"));
        }

        private static byte[] Version1Table()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
            writer.Write(new byte[] { 0xfc, 0xfd, 0xfe, 0xff });
            writer.Write(1);
            writer.Write((byte)0);
            writer.Write(1);
            foreach (var text in new[] { "icon", "key" })
            {
                writer.Write((ushort)text.Length);
                writer.Write(Encoding.ASCII.GetBytes(text));
            }

            writer.Write(5);
            writer.Write(6);
            writer.Flush();
            return stream.ToArray();
        }

        [Test]
        public void LeftoverBytesAfterTheLastRow_FailTheDecode()
        {
            const string table = "ui_unit_group_parents_tables";
            var supplied = PackOptimizerDbSchema.Load(WriteSchema("ok.json", SchemaJson(table, 2)));

            var ex = Assert.Throws<InvalidDataException>(() => Decode(table, Table(2, 2, extraByte: true), [], supplied))!;

            Assert.That(ex.Message, Does.Contain(table).And.Contain("version 2").And.Contain("1 byte(s)"));
            Assert.DoesNotThrow(() => Decode(table, Table(2, 2), [], supplied));
        }

        [Test]
        public void AnUnsupportedFieldTypeInANeededTable_NamesTheTableVersionAndField()
        {
            const string table = "ui_unit_group_parents_tables";
            var supplied = PackOptimizerDbSchema.Load(WriteSchema("odd.json", SchemaJson(table, 2, type: "SequenceU32")));

            var ex = Assert.Throws<InvalidDataException>(() => Decode(table, Table(2, 1), [], supplied))!;

            Assert.That(ex.Message, Does.Contain(table).And.Contain("version 2").And.Contain("'order'").And.Contain("SequenceU32"));
        }

        [Test]
        public void ACorruptOrForeignSchemaFile_IsRejectedWithAMessage()
        {
            var garbage = Path.Combine(_directory, "garbage.zst");
            File.WriteAllBytes(garbage, [0x28, 0xB5, 0x2F, 0xFD, 1, 2, 3, 4, 5]);
            var notJson = WriteSchema("text.json", "this is not json");
            var wrongShape = WriteSchema("shape.json", "{\"hello\":1}");
            var noTables = WriteSchema("empty.json", "{\"definitions\":{\"other_tables\":[]}}");

            foreach (var path in new[] { garbage, notJson, wrongShape, noTables, Path.Combine(_directory, "missing.json") })
            {
                Assert.That(PackOptimizerDbSchema.TryLoad(path, out var schema, out var error), Is.False, path);
                Assert.That(schema, Is.Null);
                Assert.That(error, Does.Contain(path));
            }
        }

        [Test]
        public void TheSuppliedSchemaIsReadFromZstdAndPlainJsonAlike()
        {
            const string table = "ui_unit_group_parents_tables";
            var json = SchemaJson(table, 2);
            foreach (var path in new[] { WriteSchema("a.json", json), WriteSchema("b.json.zst", json, zstd: true) })
            {
                var schema = PackOptimizerDbSchema.Load(path);
                Assert.That(Decode(table, Table(2, 1), [], schema), Has.Count.EqualTo(1), path);
            }
        }

        // --- Opt-in: the installed game, the source mod and the workshop consumers --------------

        private const string GameDb = @"K:\SteamLibrary\steamapps\common\Total War WARHAMMER III\data\db.pack";
        private const string WhmmSchema = @"K:\projects\whmm\schema\schema_wh3.json.zst";

        private static IEnumerable<string> RealPacks()
        {
            yield return GameDb;
            yield return @"K:\wh3mods\albion\!ak_teb3.pack";
            var workshop = @"K:\SteamLibrary\steamapps\workshop\content\1142710\";
            foreach (var relative in new[]
                     {
                         @"3022054734\!!!!!!Champions_of_undeath_merged_fun_tyme.pack",
                         @"2860304449\!!!!_ovn_halflings.pack",
                         @"3566493524\!!!!calm_kislev_erengrad.pack",
                         @"3081800026\!cr_oldworld_campaign.pack",
                         @"3030996759\!scm_marienburg.pack",
                         @"3280233154\gra_cecil_gastonne.pack",
                         @"2931087074\inq_lol_hero.pack",
                         @"3147025997\Professional_Empire_state_troops_reskin.pack",
                     })
            {
                yield return workshop + relative;
            }
        }

        [Test, Explicit("Reads the installed game, the source mod and 8 workshop packs")]
        public void Parity_EveryTableThatBothSchemasKnowDecodesIdentically_AndNothingHasLeftoverBytes()
        {
            var supplied = PackOptimizerDbSchema.Load(WhmmSchema);
            var tables = (IReadOnlyList<string>)ResolverType
                .GetProperty("RequiredTableNames", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var compared = 0;
            var differences = new List<string>();
            var failures = new List<string>();
            var suppliedOnly = 0;
            var embeddedOnly = 0;
            var vanillaTables = new HashSet<string>();

            foreach (var pack in RealPacks())
            {
                using var stream = File.OpenRead(pack);
                using var reader = new BinaryReader(stream, Encoding.ASCII);
                var container = PackFileSerializerLoader.Load(
                    pack, stream.Length, reader, new CaPackDuplicateFileResolver(), path => path.StartsWith("db\\", StringComparison.Ordinal));
                foreach (var table in tables)
                {
                    foreach (var (path, file) in container.GetDirectoryContent($"db\\{table}"))
                    {
                        var data = file.DataSource.ReadData();
                        List<Dictionary<string, string>>? fromSupplied = null;
                        List<Dictionary<string, string>>? fromEmbedded = null;
                        var sources = new Dictionary<string, int>();
                        try { fromSupplied = Decode(table, data, [], supplied, sources); }
                        catch (Exception ex) { failures.Add($"{Path.GetFileName(pack)} {path} (supplied): {ex.Message}"); }
                        try { fromEmbedded = Decode(table, data, [], null); }
                        catch (Exception ex) { failures.Add($"{Path.GetFileName(pack)} {path} (embedded): {ex.Message}"); }
                        if (pack == GameDb && fromSupplied != null)
                            vanillaTables.Add(table);

                        if (fromSupplied == null || fromEmbedded == null)
                            continue;
                        if (fromSupplied.Count == 0 && fromEmbedded.Count != 0) embeddedOnly++;
                        else if (fromEmbedded.Count == 0 && fromSupplied.Count != 0) suppliedOnly++;
                        else if (fromSupplied.Count != 0 || fromEmbedded.Count != 0)
                        {
                            compared++;
                            if (fromSupplied.Count != fromEmbedded.Count)
                            {
                                differences.Add($"{Path.GetFileName(pack)} {path}: {fromSupplied.Count} vs {fromEmbedded.Count} rows");
                                continue;
                            }

                            for (var row = 0; row < fromSupplied.Count; row++)
                            {
                                if (!fromSupplied[row].OrderBy(p => p.Key).SequenceEqual(fromEmbedded[row].OrderBy(p => p.Key)))
                                {
                                    differences.Add($"{Path.GetFileName(pack)} {path}: row {row} differs");
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            TestContext.Out.WriteLine(
                $"vanilla tables decoded: {vanillaTables.Count}; table files compared: {compared}; only the supplied schema decoded: {suppliedOnly}; " +
                $"only the embedded: {embeddedOnly}; differences: {differences.Count}; failures: {failures.Count}");
            foreach (var line in differences.Concat(failures).Take(30))
                TestContext.Out.WriteLine("  " + line);
            Assert.That(vanillaTables.Count, Is.EqualTo(tables.Count));
            Assert.That(differences, Is.Empty);
            Assert.That(failures, Is.Empty);
        }
    }
}
