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

        private sealed record ProjectileDisplayRow(
            string Key,
            string DisplayModel,
            string Impact,
            int TipOffsetBits,
            byte TrailSpin,
            int ScaleBits);

        private static readonly (string Name, string Type)[] ProjectileDisplayFields =
        [
            ("display_model", "OptionalStringU8"),
            ("impact", "OptionalStringU8"),
            ("key", "StringU8"),
            ("launch_fx", "OptionalStringU8"),
            ("trail_fx", "OptionalStringU8"),
            ("stationary_fx", "OptionalStringU8"),
            ("airborne_anim", "OptionalStringU8"),
            ("landing_anim", "OptionalStringU8"),
            ("tip_offset", "F32"),
            ("trail_spin", "Boolean"),
            ("launch_camera_shake", "OptionalStringU8"),
            ("impact_bounce", "OptionalStringU8"),
            ("impact_penetrate", "OptionalStringU8"),
            ("impact_blood", "OptionalStringU8"),
            ("impact_bounce_blood", "OptionalStringU8"),
            ("impact_penetrate_blood", "OptionalStringU8"),
            ("mirroring_start", "OptionalStringU8"),
            ("mirroring_trail", "OptionalStringU8"),
            ("scale", "F32"),
        ];

        private static string ProjectileDisplaySchemaJson()
            => "{\"definitions\":{\"projectile_displays_tables\":[{\"version\":12,\"fields\":[" +
               string.Join(",", ProjectileDisplayFields.Select(field =>
                   "{\"name\":\"" + field.Name + "\",\"field_type\":\"" + field.Type + "\"}")) +
               "]}]}}";

        private static void WriteStringU8(BinaryWriter writer, string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        private static void WriteOptionalStringU8(BinaryWriter writer, string? value)
        {
            if (value == null)
            {
                writer.Write((byte)0);
                return;
            }

            writer.Write((byte)1);
            WriteStringU8(writer, value);
        }

        private static byte[] ProjectileDisplayRowBytes(ProjectileDisplayRow row)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
            WriteOptionalStringU8(writer, row.DisplayModel);
            WriteOptionalStringU8(writer, row.Impact);
            WriteStringU8(writer, row.Key);
            WriteOptionalStringU8(writer, "launch_" + row.Key);
            WriteOptionalStringU8(writer, null);
            WriteOptionalStringU8(writer, "stationary_" + row.Key);
            WriteOptionalStringU8(writer, null);
            WriteOptionalStringU8(writer, "landing_" + row.Key);
            writer.Write(row.TipOffsetBits);
            writer.Write(row.TrailSpin);
            WriteOptionalStringU8(writer, "camera_" + row.Key);
            WriteOptionalStringU8(writer, null);
            WriteOptionalStringU8(writer, "penetrate_" + row.Key);
            WriteOptionalStringU8(writer, null);
            WriteOptionalStringU8(writer, "bounce_blood_" + row.Key);
            WriteOptionalStringU8(writer, null);
            WriteOptionalStringU8(writer, "mirror_start_" + row.Key);
            WriteOptionalStringU8(writer, null);
            writer.Write(row.ScaleBits);
            writer.Flush();
            return stream.ToArray();
        }

        private static byte[] ProjectileDisplayTable(params ProjectileDisplayRow[] rows)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
            writer.Write(new byte[] { 0xfd, 0xfe, 0xfc, 0xff });
            writer.Write((short)2);
            writer.Write(Encoding.Unicode.GetBytes("AB"));
            writer.Write(new byte[] { 0xfc, 0xfd, 0xfe, 0xff });
            writer.Write(12);
            writer.Write((byte)0);
            writer.Write(rows.Length);
            foreach (var row in rows)
                writer.Write(ProjectileDisplayRowBytes(row));
            writer.Flush();
            return stream.ToArray();
        }

        private static (bool Success, byte[]? Data, HashSet<string> Keys, string? Failure)
            RewriteProjectileDisplayRigidRows(
                byte[] data,
                string expectedPath,
                string replacementPath,
                IReadOnlySet<string> rowKeys,
                PackOptimizerDbSchema? supplied = null)
        {
            var method = ResolverType.GetMethod(
                "TryRewriteSourceRigidReferenceRows",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            object?[] arguments =
            [
                "projectile_displays_tables",
                data,
                "db/projectile_displays_tables/ak_teb",
                supplied,
                rowKeys,
                expectedPath,
                replacementPath,
                null,
                null,
                null,
            ];
            var success = (bool)method.Invoke(null, arguments)!;
            return (
                success,
                (byte[]?)arguments[7],
                (HashSet<string>)arguments[8]!,
                (string?)arguments[9]);
        }

        [Test]
        public void ProjectileDisplayRigidReference_RewriteChangesOnlyTheSelectedTypedCell()
        {
            const string table = "projectile_displays_tables";
            const string key = "teb_irrana";
            const string originalPath = "VariantMeshes/wh_variantmodels/hu1/brt/brt_props/brt_men_at_arms_spear_1h_01.rigid_model_v2";
            const string replacementPath = "VariantMeshes\\shared_texture_repack\\spear_clone.rigid_model_v2";
            var supplied = PackOptimizerDbSchema.Load(WriteSchema("projectile.json", ProjectileDisplaySchemaJson()));
            var rows = new[]
            {
                new ProjectileDisplayRow("teb_roc_organ", "VariantMeshes/other_1.rigid_model_v2", "impact_a", unchecked((int)0x80000000), 1, 0x7fc01234),
                new ProjectileDisplayRow(key, originalPath, "impact_b", 0x3f123456, 0, unchecked((int)0x80000000)),
                new ProjectileDisplayRow("teb_galloper_gun", "VariantMeshes/other_2.rigid_model_v2", "impact_c", 0x7fc05678, 1, 0x3f000000),
            };
            var original = ProjectileDisplayTable(rows);
            var originalSnapshot = original.ToArray();

            // The normal optimizer request has no supplied schema; its embedded table layout must
            // still decode version 12. The supplied-schema route must also retain the extra layout.
            Assert.That(Decode(table, original, [], null), Has.Count.EqualTo(3));
            Assert.That(Decode(table, original, [], supplied), Has.Count.EqualTo(3));
            var rewritten = RewriteProjectileDisplayRigidRows(
                original,
                originalPath.Replace('/', '\\').ToLowerInvariant(),
                replacementPath,
                new HashSet<string>([key], StringComparer.OrdinalIgnoreCase));

            Assert.That(rewritten.Success, Is.True, rewritten.Failure);
            Assert.That(rewritten.Data, Is.Not.Null);
            Assert.That(rewritten.Keys, Is.EquivalentTo(new[] { key }));
            var headerLength = 4 + 2 + 4 + 4 + 4 + 1 + 4;
            var expected = original[..headerLength]
                .Concat(ProjectileDisplayRowBytes(rows[0]))
                .Concat(ProjectileDisplayRowBytes(rows[1] with { DisplayModel = replacementPath }))
                .Concat(ProjectileDisplayRowBytes(rows[2]))
                .ToArray();
            Assert.That(rewritten.Data, Is.EqualTo(expected), "only the schema-selected row cell and its length may change");
            Assert.That(original, Is.EqualTo(originalSnapshot), "the source table buffer remains unchanged");

            var afterRows = Decode(table, rewritten.Data!, [], null);
            Assert.That(afterRows.Select(row => row["key"]), Is.EqualTo(rows.Select(row => row.Key)));
            for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
            {
                foreach (var field in ProjectileDisplayFields)
                {
                    var expectedValue = rowIndex == 1 && field.Name == "display_model"
                        ? replacementPath
                        : Decode(table, original, [], null)[rowIndex][field.Name];
                    Assert.That(afterRows[rowIndex][field.Name], Is.EqualTo(expectedValue), $"row {rowIndex}, field {field.Name}");
                }
            }

            var duplicate = ProjectileDisplayTable(rows[1], rows[1]);
            var rejected = RewriteProjectileDisplayRigidRows(
                duplicate,
                originalPath,
                replacementPath,
                new HashSet<string>([key], StringComparer.OrdinalIgnoreCase));
            Assert.That(rejected.Success, Is.False);
            Assert.That(rejected.Data, Is.Null);
            Assert.That(rejected.Failure, Does.Contain("duplicated"));
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
