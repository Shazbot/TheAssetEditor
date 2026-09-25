using System.Buffers.Binary;
using System.Globalization;
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

    internal sealed record Wh3UnitCategoryUsage(
        string VmdPath,
        string MainUnitKey,
        string LandUnitKey,
        string Caste,
        string LandCategory,
        int NumMen,
        Wh3ArmyUnitCategory Category);

    internal sealed record Wh3UnitCategoryResolution(
        IReadOnlyDictionary<string, IReadOnlyList<Wh3UnitCategoryUsage>> UsagesByVmd,
        IReadOnlyList<string> UnresolvedVmdRoots,
        IReadOnlyDictionary<string, int> ParsedRowsByTable,
        int TableFilesRead,
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

        private static readonly string[] RequiredTables =
        [
            MainUnitsTable,
            LandUnitsTable,
            UnitVariantsTable,
            VariantsTable,
        ];

        private static readonly Lazy<SchemaRoot> Schema = new(LoadSchema);

        public static Wh3UnitCategoryResolution Resolve(
            IPackFileService packFileService,
            IPackFileContainer source,
            IReadOnlyCollection<string> rootVmdPaths,
            CancellationToken cancellationToken)
        {
            var diagnostics = new List<string>();
            var parsedRowsByTable = RequiredTables.ToDictionary(
                table => table,
                _ => 0,
                StringComparer.OrdinalIgnoreCase);
            var effectiveRows = RequiredTables.ToDictionary(
                table => table,
                _ => new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

            var containers = packFileService.GetAllPackfileContainers()
                .Where(container => !IsSameContainer(container, source))
                .ToList();
            containers.Add(source);

            var tableFilesRead = 0;
            foreach (var container in containers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relevantFiles = container.GetAllFiles()
                    .Where(entry => TryGetRequiredTable(entry.Key, out _))
                    .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var (path, file) in relevantFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryGetRequiredTable(path, out var tableName))
                        continue;

                    try
                    {
                        var rows = DecodeTable(
                            tableName,
                            file.DataSource.ReadData(),
                            path,
                            diagnostics);
                        tableFilesRead++;
                        parsedRowsByTable[tableName] += rows.Count;

                        foreach (var row in rows)
                        {
                            var key = BuildEffectiveRowKey(tableName, row);
                            if (string.IsNullOrWhiteSpace(key))
                                continue;

                            effectiveRows[tableName][key] = row;
                        }
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

            var mainRows = effectiveRows[MainUnitsTable].Values.ToList();
            var landRows = effectiveRows[LandUnitsTable];
            var unitVariantRows = effectiveRows[UnitVariantsTable].Values.ToList();
            var variantRows = effectiveRows[VariantsTable];

            var mainByLandUnit = mainRows
                .Where(row => Get(row, "land_unit").Length != 0)
                .GroupBy(row => Get(row, "land_unit"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.ToList(),
                    StringComparer.OrdinalIgnoreCase);

            var usagesByVmd = new Dictionary<string, Dictionary<string, Wh3UnitCategoryUsage>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var unitVariant in unitVariantRows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var landUnitKey = Get(unitVariant, "unit");
                var variantName = Get(unitVariant, "variant");
                if (landUnitKey.Length == 0 || variantName.Length == 0)
                    continue;
                if (!variantRows.TryGetValue(variantName, out var variant))
                    continue;

                var vmdPath = ToVariantMeshDefinitionPath(Get(variant, "variant_filename"));
                if (vmdPath.Length == 0)
                    continue;
                if (!landRows.TryGetValue(landUnitKey, out var land))
                    continue;

                if (!mainByLandUnit.TryGetValue(landUnitKey, out var mains) || mains.Count == 0)
                {
                    AddUsage(
                        usagesByVmd,
                        new Wh3UnitCategoryUsage(
                            vmdPath,
                            string.Empty,
                            landUnitKey,
                            string.Empty,
                            Get(land, "category"),
                            1,
                            Classify(string.Empty, Get(land, "category"))));
                    continue;
                }

                foreach (var main in mains)
                {
                    var mainUnitKey = Get(main, "unit");
                    var caste = Get(main, "caste");
                    var landCategory = Get(land, "category");
                    var numMen = TryParseInt(Get(main, "num_men"), out var parsedNumMen)
                        ? Math.Max(1, parsedNumMen)
                        : 1;

                    AddUsage(
                        usagesByVmd,
                        new Wh3UnitCategoryUsage(
                            vmdPath,
                            mainUnitKey,
                            landUnitKey,
                            caste,
                            landCategory,
                            numMen,
                            Classify(caste, landCategory)));
                }
            }

            var normalizedRoots = rootVmdPaths
                .Select(NormalizePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

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

            return new Wh3UnitCategoryResolution(
                filtered,
                unresolved,
                parsedRowsByTable,
                tableFilesRead,
                diagnostics);
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
            usages[identity] = usage with { VmdPath = vmdPath };
        }

        private static Wh3ArmyUnitCategory Classify(string caste, string landCategory)
        {
            switch (caste.Trim().ToLowerInvariant())
            {
                case "lord":
                    return Wh3ArmyUnitCategory.Lord;
                case "hero":
                    return Wh3ArmyUnitCategory.Hero;
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
                var marker = data.AsSpan(position, 4);
                if (marker.SequenceEqual([0xfd, 0xfe, 0xfc, 0xff]))
                {
                    position += 4;
                    var length = ReadInt16(data, ref position);
                    if (length < 0)
                        throw new InvalidDataException("Negative DB GUID length.");
                    Skip(data, ref position, checked(length * 2));
                    continue;
                }

                if (marker.SequenceEqual([0xfc, 0xfd, 0xfe, 0xff]))
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

        private static string BuildEffectiveRowKey(
            string tableName,
            IReadOnlyDictionary<string, string> row)
        {
            return tableName switch
            {
                MainUnitsTable => Get(row, "unit"),
                LandUnitsTable => Get(row, "key"),
                VariantsTable => Get(row, "variant_name"),
                UnitVariantsTable => $"{Get(row, "faction")}\u001f{Get(row, "unit")}",
                _ => string.Empty,
            };
        }

        private static string Get(
            IReadOnlyDictionary<string, string> row,
            string field)
            => row.TryGetValue(field, out var value) ? value.Trim() : string.Empty;

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
