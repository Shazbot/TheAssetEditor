using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Shared.Core.PackFiles.Models;

namespace Editors.PackOptimizer.Core
{
    public sealed partial class PackTextureAtlasBatchService
    {
        /// <summary>
        /// What a pack says, independent of any source: the asset paths its text files and the rigids its
        /// VMDs draw name (hashed), the names its DB tables mention (hashed) and how many of its tables
        /// cannot be decoded. Hashes can only collide into extra matches, so a probe from them stays a
        /// superset of the exact scan. Cached on disk per pack (path, size, modification time), the DB
        /// schema in use and <see cref="PackOptimizerVersions.ScanVersion"/>.
        /// </summary>
        internal sealed class ConsumerPackScan
        {
            public HashSet<ulong> TextTokens { get; } = [];
            public HashSet<ulong> DbKeys { get; } = [];
            public int UndecodableTables { get; set; }
        }

        private const int ConsumerScanFileMagic = 0x314E4353; // "SCN1"
        private static readonly Regex ConsumerDbTokenPattern = new(@"[a-z0-9_\\/\.\-]{4,260}", RegexOptions.Compiled);

        internal static ulong HashScanToken(string token)
        {
            // FNV-1a over the UTF-16 code units: stable across runs and machines.
            var hash = 14695981039346656037UL;
            foreach (var character in token)
            {
                hash = (hash ^ character) * 1099511628211UL;
            }

            return hash;
        }

        private static string DbNameOf(string token)
        {
            var name = token[(token.LastIndexOf('\\') + 1)..];
            if (name.EndsWith(".variantmeshdefinition", StringComparison.Ordinal))
                name = name[..^".variantmeshdefinition".Length];
            return name;
        }

        private static ConsumerPackScan ScanConsumerPack(
            IPackFileContainer pack,
            PackOptimizerDbSchema? dbSchema,
            CancellationToken cancellationToken)
        {
            var scan = new ConsumerPackScan();
            var files = pack.GetAllFiles();
            var rigidRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in files.Keys.Select(Normalize).Where(IsConsumerScannableText).OrderBy(item => item, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = pack.FindFile(path);
                var data = file == null ? null : TryReadConsumerFile(file, ConsumerScanFileLimit);
                if (data == null)
                    continue;

                var isVmd = path.EndsWith(".variantmeshdefinition", StringComparison.OrdinalIgnoreCase);
                foreach (var token in ExtractAssetTokens(data, LooksWide(data)))
                {
                    scan.TextTokens.Add(HashScanToken(token));
                    if (isVmd && token.EndsWith(".rigid_model_v2", StringComparison.OrdinalIgnoreCase))
                        rigidRefs.Add(token);
                }
            }

            // A consumer VMD that draws a rigid of its own uses that rigid's embedded materials, whose
            // texture paths may be source textures.
            foreach (var rigid in rigidRefs.OrderBy(item => item, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = pack.FindFile(rigid);
                var data = file == null ? null : TryReadConsumerFile(file, 256L * 1024 * 1024);
                if (data == null)
                    continue;

                foreach (var token in ExtractAssetTokens(data, includeWideView: false))
                    scan.TextTokens.Add(HashScanToken(token));
            }

            foreach (var path in files.Keys.Select(Normalize)
                         .Where(item => item.StartsWith("db\\", StringComparison.Ordinal))
                         .OrderBy(item => item, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = pack.FindFile(path);
                var data = file == null ? null : TryReadConsumerFile(file, ConsumerScanFileLimit);
                if (data == null)
                    continue;

                foreach (var wide in new[] { false, true })
                {
                    var bytes = wide ? data.Where(value => value != 0).ToArray() : data;
                    var text = Encoding.Latin1.GetString(bytes).ToLowerInvariant();
                    foreach (Match match in ConsumerDbTokenPattern.Matches(text))
                    {
                        var token = match.Value.Replace('/', '\\');
                        scan.DbKeys.Add(HashScanToken(token));
                        scan.DbKeys.Add(HashScanToken(DbNameOf(token)));
                    }
                }
            }

            scan.UndecodableTables = Wh3UnitCategoryResolver.CountUndecodableTables(pack, dbSchema);
            return scan;
        }

        private static string? GetScanCachePath(string? directory, IPackFileContainer pack, string schemaId)
        {
            var path = pack.SystemFilePath;
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            var info = new FileInfo(path);
            var key = $"{Path.GetFullPath(path).ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{schemaId}|{PackOptimizerVersions.ScanVersion}";
            var name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
            return Path.Combine(directory, "probe-scan", name + ".scan");
        }

        private static ConsumerPackScan? TryReadScan(string cachePath, string schemaId)
        {
            try
            {
                if (!File.Exists(cachePath))
                    return null;

                using var reader = new BinaryReader(File.OpenRead(cachePath));
                if (reader.ReadInt32() != ConsumerScanFileMagic ||
                    reader.ReadInt32() != PackOptimizerVersions.ScanVersion ||
                    reader.ReadString() != schemaId)
                {
                    return null;
                }

                var scan = new ConsumerPackScan { UndecodableTables = reader.ReadInt32() };
                var textCount = reader.ReadInt32();
                for (var index = 0; index < textCount; index++)
                    scan.TextTokens.Add(reader.ReadUInt64());
                var dbCount = reader.ReadInt32();
                for (var index = 0; index < dbCount; index++)
                    scan.DbKeys.Add(reader.ReadUInt64());
                return scan;
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void TryWriteScan(string cachePath, string schemaId, ConsumerPackScan scan)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                var temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var writer = new BinaryWriter(File.Create(temporary)))
                {
                    writer.Write(ConsumerScanFileMagic);
                    writer.Write(PackOptimizerVersions.ScanVersion);
                    writer.Write(schemaId);
                    writer.Write(scan.UndecodableTables);
                    writer.Write(scan.TextTokens.Count);
                    foreach (var value in scan.TextTokens)
                        writer.Write(value);
                    writer.Write(scan.DbKeys.Count);
                    foreach (var value in scan.DbKeys)
                        writer.Write(value);
                }

                File.Move(temporary, cachePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The scan cache only saves time.
            }
        }

        internal sealed record BatchProbeResult(
            IReadOnlyList<IReadOnlyList<PackOptimizerConsumerPack>> Sources,
            int PacksScanned,
            int ScanCacheHits,
            TimeSpan ScanElapsed);

        /// <summary>
        /// The probe for several sources against one load order: every pack is loaded and scanned once (or
        /// read from the scan cache), then each source is matched against the stored scans. For source
        /// <c>i</c> the consumers are the load order without it, and the packs before it load before it.
        /// </summary>
        internal static BatchProbeResult ProbeBatch(
            IReadOnlyList<IPackFileContainer> loadOrder,
            IReadOnlyList<int> sourceIndexes,
            IReadOnlyList<IPackFileContainer> vanilla,
            PackOptimizerDbSchema? dbSchema,
            string? cacheDirectory,
            string schemaId,
            CancellationToken cancellationToken)
        {
            var scanWatch = Stopwatch.StartNew();
            var scans = new ConsumerPackScan?[loadOrder.Count];
            var scanned = 0;
            var hits = 0;
            ConsumerPackScan GetScan(int index)
            {
                if (scans[index] != null)
                    return scans[index]!;

                var cachePath = GetScanCachePath(cacheDirectory, loadOrder[index], schemaId);
                var scan = cachePath == null ? null : TryReadScan(cachePath, schemaId);
                if (scan != null)
                {
                    hits++;
                }
                else
                {
                    scan = ScanConsumerPack(loadOrder[index], dbSchema, cancellationToken);
                    scanned++;
                    if (cachePath != null)
                        TryWriteScan(cachePath, schemaId, scan);
                }

                scans[index] = scan;
                return scan;
            }

            var vanillaCa = vanilla.Where(container => container.IsCaPackFile).ToList();
            var vanillaTokens = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            var results = new List<IReadOnlyList<PackOptimizerConsumerPack>>();
            foreach (var sourceIndex in sourceIndexes)
            {
                var source = loadOrder[sourceIndex];
                var consumerIndexes = Enumerable.Range(0, loadOrder.Count).Where(index => index != sourceIndex).ToList();
                var packs = consumerIndexes
                    .Select((loadIndex, position) => new ConsumerPackEntry(position, loadOrder[loadIndex], loadIndex < sourceIndex))
                    .ToList();
                var index_ = new ConsumerReferenceIndex(packs);

                var sourcePaths = source.GetAllFiles().Keys.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var sourceHashes = sourcePaths.Select(HashScanToken).ToHashSet();
                var dbNames = new HashSet<ulong>();
                foreach (var path in sourcePaths)
                {
                    if (path.EndsWith(".variantmeshdefinition", StringComparison.Ordinal))
                        dbNames.Add(HashScanToken(Path.GetFileNameWithoutExtension(path)));
                    else if (path.EndsWith(".wsmodel", StringComparison.Ordinal) || path.EndsWith(".rigid_model_v2", StringComparison.Ordinal))
                        dbNames.Add(HashScanToken(path));
                }

                foreach (var pack in packs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var scan = GetScan(consumerIndexes[pack.Index]);
                    foreach (var path in sourcePaths)
                    {
                        if (IsConsumerAssetPath(path) && pack.Container.ContainsFile(path))
                            pack.OverlapAssets++;
                    }

                    var used = 0;
                    foreach (var hash in sourceHashes)
                    {
                        if (scan.TextTokens.Contains(hash))
                            used++;
                    }

                    pack.BatchSeedHits = used;
                    pack.DbMentions = dbNames.Count(hash => scan.DbKeys.Contains(hash));
                    pack.UndecodableTables = scan.UndecodableTables;
                }

                ComputeVanillaReplacement(source, vanillaCa, index_, cancellationToken, vanillaTokens);
                results.Add(DescribeConsumerPacks(index_));
            }

            return new BatchProbeResult(results, scanned, hits, scanWatch.Elapsed);
        }
    }
}
