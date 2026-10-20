using System.IO;
using System.Text.Json;
using ZstdSharp;

namespace Editors.PackOptimizer.Core
{
    /// <summary>
    /// The DB table layouts of an RPFM schema file (as the mod manager bundles it, zstd-compressed
    /// or plain JSON), reduced to the tables the unit category resolver reads. When a run is given
    /// one, the resolver looks a table version up here first and falls back to the schema
    /// embedded in this library.
    /// </summary>
    public sealed class PackOptimizerDbSchema
    {
        internal PackOptimizerDbSchema(string path, Dictionary<string, List<DbSchemaTableVersion>> tables)
        {
            Path = path;
            Tables = tables;
        }

        public string Path { get; }

        internal Dictionary<string, List<DbSchemaTableVersion>> Tables { get; }

        /// <summary>Loads the schema; failures say what is wrong with the file.</summary>
        public static bool TryLoad(string path, out PackOptimizerDbSchema? schema, out string? error)
        {
            schema = null;
            try
            {
                schema = Load(path);
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                           or JsonException or ZstdException or InvalidOperationException)
            {
                error = $"The DB schema '{path}' could not be read: {ex.Message}";
                return false;
            }
        }

        public static PackOptimizerDbSchema Load(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 4 && bytes[0] == 0x28 && bytes[1] == 0xB5 && bytes[2] == 0x2F && bytes[3] == 0xFD)
            {
                using var compressed = new MemoryStream(bytes, writable: false);
                using var decompression = new DecompressionStream(compressed);
                using var plain = new MemoryStream();
                decompression.CopyTo(plain);
                bytes = plain.ToArray();
            }

            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("definitions", out var definitions) ||
                definitions.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("it has no \"definitions\" object (not an RPFM schema).");
            }

            var tables = new Dictionary<string, List<DbSchemaTableVersion>>(StringComparer.OrdinalIgnoreCase);
            foreach (var tableName in Wh3UnitCategoryResolver.RequiredTableNames)
            {
                if (!definitions.TryGetProperty(tableName, out var versions))
                    continue;
                if (versions.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException($"table '{tableName}' has no version list.");

                var list = new List<DbSchemaTableVersion>();
                foreach (var version in versions.EnumerateArray())
                {
                    if (!version.TryGetProperty("version", out var number) || !number.TryGetInt32(out var versionNumber) ||
                        !version.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
                    {
                        throw new InvalidDataException($"a version of table '{tableName}' has no version number or field list.");
                    }

                    var fieldList = new List<DbSchemaField>();
                    foreach (var field in fields.EnumerateArray())
                    {
                        var name = field.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                        var type = field.TryGetProperty("field_type", out var typeElement) ? typeElement.GetString() : null;
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(type))
                            throw new InvalidDataException($"table '{tableName}' version {versionNumber} has a field without a name or type.");
                        fieldList.Add(new DbSchemaField(name, type));
                    }

                    list.Add(new DbSchemaTableVersion(versionNumber, fieldList));
                }

                tables[tableName] = list;
            }

            if (tables.Count == 0)
                throw new InvalidDataException("none of the tables the optimizer reads are in it (not a WH3 RPFM schema).");

            return new PackOptimizerDbSchema(path, tables);
        }
    }

    internal sealed record DbSchemaField(string Name, string FieldType);

    internal sealed record DbSchemaTableVersion(int Version, IReadOnlyList<DbSchemaField> Fields);
}
