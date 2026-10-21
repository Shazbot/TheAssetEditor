using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Shared.Core.PackFiles.Models;
using Shared.GameFormats.RigidModel;

namespace Editors.PackOptimizer.Core
{
    // Other enabled mods ("consumers"): packs that load alongside the source pack and may
    // reference or override its files. They are read-only and are never part of the pack file
    // service, so no generic read can fall through to them.
    //
    //  * E ("externally used"): source files reachable from a consumer file, transitively inside
    //    the source. Never pruned, never modified in place by any stage.
    //  * F ("frozen"): source paths that a consumer also has. Never modified, moved, retired or
    //    pruned, and nothing is redirected away from them, whatever the load order.
    //
    // Both are over-approximations on purpose: a doubt keeps the file as it is.
    public sealed partial class PackTextureAtlasBatchService
    {
        private const long ConsumerScanFileLimit = 16L * 1024 * 1024;
        private const double ConsumerDerivedOverlapShare = 0.5;
        private const int ConsumerDerivedMinimumOverlap = 25;

        // Paths named inside a consumer file. The class allows spaces (some folders have them);
        // every suffix after a space is tried as well, see ExtractAssetTokens.
        private static readonly Regex ConsumerAssetTokenPattern = new(
            @"[A-Za-z0-9_\\/\.\- ]{1,260}\.(?:variantmeshdefinition|wsmodel|rigid_model_v2|dds|xml\.material|material)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private sealed class ConsumerPackEntry
        {
            public ConsumerPackEntry(int index, IPackFileContainer container, bool belowSource)
            {
                Index = index;
                Container = container;
                BelowSource = belowSource;
                Name = !string.IsNullOrWhiteSpace(container.Name)
                    ? container.Name
                    : Path.GetFileNameWithoutExtension(container.SystemFilePath ?? string.Empty);
                Files = container.GetAllFiles();
            }

            public int Index { get; }
            public IPackFileContainer Container { get; }
            public bool BelowSource { get; }
            public string Name { get; }
            public IReadOnlyDictionary<string, PackFile> Files { get; }
            public int AssetFiles { get; set; }
            public int OverlapAssets { get; set; }
            public int OverlapOther { get; set; }
            public int FilesScanned { get; set; }
            public int FilesUnreadable { get; set; }
            public TimeSpan ScanElapsed { get; set; }
            public int DbTables { get; set; }
            public int DbRootsAdded { get; set; }
            public List<(string Path, string Reason)> DbFallbackTables { get; } = [];
            public int DbFallbackAdded { get; set; }
            // Distinct source files this pack's files name, even when another pack named them first.
            public HashSet<string> SeedHits { get; } = new(StringComparer.OrdinalIgnoreCase);
            public int DbMentions { get; set; }
            public int UndecodableTables { get; set; }
            public HashSet<string> VanillaReached { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed record ConsumerUse(
            ConsumerPackEntry Pack,
            string Referrer,
            string Parent,
            bool Fallback,
            // The file reaches a vanilla path another mod replaces (not a reference from a mod).
            bool VanillaPath = false);

        private sealed record ConsumerBlock(
            string Path,
            string Stage,
            string Kind,
            string Pack,
            string Detail);

        private sealed class ConsumerReferenceIndex
        {
            private readonly HashSet<string> _blockKeys = new(StringComparer.OrdinalIgnoreCase);

            public ConsumerReferenceIndex(IReadOnlyList<ConsumerPackEntry> packs)
            {
                Packs = packs;
                foreach (var pack in packs)
                    ModContainers.Add(pack.Container);
            }

            public IReadOnlyList<ConsumerPackEntry> Packs { get; }
            public HashSet<IPackFileContainer> ModContainers { get; } =
                new(ReferenceEqualityComparer.Instance);
            public Dictionary<string, ConsumerUse> Used { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, List<ConsumerPackEntry>> Frozen { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            // Consumer VMDs and WSModels that name a source VMD, WSModel or rigid: roots of the
            // gameplay mesh dependency index.
            public Dictionary<string, ConsumerPackEntry> RootFiles { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Closed { get; } = new(StringComparer.OrdinalIgnoreCase);
            // Vanilla paths another enabled mod also has (and the source does not): what the source
            // reaches through them is not what the game will load, so nothing is derived from them.
            public Dictionary<string, List<ConsumerPackEntry>> VanillaReplaced { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            // Replaced vanilla path -> the first source file that reaches it.
            public Dictionary<string, string> VanillaReachedBy { get; } = new(StringComparer.OrdinalIgnoreCase);
            public bool IsVanillaReplaced(string path) => VanillaReplaced.ContainsKey(path);

            public bool IsVanillaTouched(string path) => Used.TryGetValue(path, out var use) && use.VanillaPath;

            public bool TryBlockVanillaReplaced(string path, string stage)
            {
                if (!VanillaReplaced.TryGetValue(path, out var packs))
                    return false;

                RecordBlock(
                    path, stage, "vanilla-replaced", string.Join(", ", packs.Select(pack => pack.Name)),
                    $"vanilla path replaced by {string.Join(", ", packs.Select(pack => pack.Name))}");
                return true;
            }
            public List<ConsumerBlock> Blocks { get; } = [];
            public List<string> Warnings { get; } = [];
            public List<string> VmdRootsAddedByDb { get; } = [];
            public TimeSpan ScanElapsed { get; set; }
            public TimeSpan FinalizeElapsed { get; set; }
            public int FilesScanned { get; set; }
            public bool Finalized { get; set; }
            // Source files a stage may legitimately change although they are in E or F. Empty
            // today: every stage that rewrites a file checks the sets first.
            public HashSet<string> IntegrityExceptions { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<(string Path, string Reason)> IntegrityViolations { get; } = [];
            public int IntegrityChecked { get; set; }

            // True when only the frozen set holds the path.
            public bool TryBlockFrozen(string path, string stage)
            {
                if (!Frozen.TryGetValue(path, out var freezers))
                    return false;

                RecordBlock(
                    path, stage, "overridden", string.Join(", ", freezers.Select(pack => pack.Name)),
                    DescribeFreeze(freezers));
                return true;
            }

            public void RecordBlock(string path, string stage, string kind, string pack, string detail)
            {
                if (_blockKeys.Add(path + "|" + stage + "|" + kind))
                    Blocks.Add(new ConsumerBlock(path, stage, kind, pack, detail));
            }

            public bool IsModContainer(IPackFileContainer? container)
                => container != null && ModContainers.Contains(container);

            public bool IsUsed(string path) => Used.ContainsKey(path);
            public bool IsFrozen(string path) => Frozen.ContainsKey(path);
            public bool IsExternal(string path) => IsUsed(path) || IsFrozen(path);

            // True when the path is in E or F; records the block once per (path, stage).
            public bool TryBlock(string path, string stage)
            {
                var hasUse = Used.TryGetValue(path, out var use);
                var hasFreeze = Frozen.TryGetValue(path, out var freezers);
                if (!hasUse && !hasFreeze)
                    return false;

                if (hasUse)
                    RecordBlock(path, stage, "used", use!.Pack.Name, DescribeUse(use));
                if (hasFreeze)
                {
                    RecordBlock(
                        path, stage, "overridden", string.Join(", ", freezers!.Select(pack => pack.Name)),
                        DescribeFreeze(freezers!));
                }

                return true;
            }

            public string Describe(string path)
            {
                var parts = new List<string>();
                if (Used.TryGetValue(path, out var use))
                    parts.Add($"used by {DescribeUse(use)}");
                if (Frozen.TryGetValue(path, out var freezers))
                    parts.Add($"overridden by {DescribeFreeze(freezers)}");
                return string.Join("; ", parts);
            }

            public static string DescribeUse(ConsumerUse use)
                => use.VanillaPath
                    ? $"{use.Pack.Name} ({use.Referrer}{(use.Parent.Length == 0 ? string.Empty : " via " + use.Parent)})"
                    : use.Parent.Length == 0
                    ? $"{use.Pack.Name} ({use.Referrer}{(use.Fallback ? ", DB text fallback" : string.Empty)})"
                    : $"{use.Pack.Name} ({use.Referrer} via {use.Parent}{(use.Fallback ? ", DB text fallback" : string.Empty)})";

            public static string DescribeFreeze(IEnumerable<ConsumerPackEntry> packs)
                => string.Join(", ", packs.Select(pack =>
                    $"{pack.Name} ({(pack.BelowSource ? "loads before the source" : "loads after the source")})"));
        }

        private static bool IsConsumerAssetPath(string path)
            => path.EndsWith(".variantmeshdefinition", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".wsmodel", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".rigid_model_v2", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".xml.material", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);

        private static string GetConsumerAssetKind(string path)
        {
            if (path.EndsWith(".variantmeshdefinition", StringComparison.OrdinalIgnoreCase))
                return "VMD";
            if (path.EndsWith(".wsmodel", StringComparison.OrdinalIgnoreCase))
                return "WSModel";
            if (path.EndsWith(".rigid_model_v2", StringComparison.OrdinalIgnoreCase))
                return "rigid";
            if (path.EndsWith(".xml.material", StringComparison.OrdinalIgnoreCase))
                return "material";
            if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                return "texture";
            return "other";
        }

        private static bool IsConsumerScannableText(string path)
            => IsTextureDownscaleTextFile(path) ||
               path.EndsWith(".xml.material", StringComparison.OrdinalIgnoreCase);

        // Asset paths named in raw file bytes: a Latin-1 view (UTF-8 and ASCII paths) and, when
        // the bytes look like UTF-16 or are a DB table, the same view with the zero bytes removed.
        private static IEnumerable<string> ExtractAssetTokens(byte[] data, bool includeWideView)
        {
            foreach (var token in ExtractAssetTokens(Encoding.Latin1.GetString(data)))
                yield return token;

            if (!includeWideView)
                yield break;

            var stripped = new byte[data.Length];
            var length = 0;
            foreach (var value in data)
            {
                if (value != 0)
                    stripped[length++] = value;
            }

            if (length == data.Length)
                yield break;

            foreach (var token in ExtractAssetTokens(Encoding.Latin1.GetString(stripped, 0, length)))
                yield return token;
        }

        private static IEnumerable<string> ExtractAssetTokens(string text)
        {
            foreach (Match match in ConsumerAssetTokenPattern.Matches(text))
            {
                var token = match.Value;
                while (true)
                {
                    var normalized = Normalize(token).TrimStart('\\');
                    if (normalized.Length != 0)
                        yield return normalized;

                    var space = token.IndexOf(' ');
                    if (space < 0)
                        break;

                    token = token[(space + 1)..];
                }
            }
        }

        private static bool LooksWide(byte[] data)
            => data.Length > 2 &&
               ((data[0] == 0xFF && data[1] == 0xFE) || (data[0] == 0xFE && data[1] == 0xFF) ||
                (data.Length > 8 && data[1] == 0 && data[3] == 0));

        private static byte[]? TryReadConsumerFile(PackFile file, long limit)
        {
            try
            {
                var data = file.DataSource.ReadData();
                return data.LongLength > limit ? null : data;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }

        // The packs, the frozen set and the seeds found in consumer files (everything except DB
        // rows, which need the unit category resolution). Cheap: the pack index plus the small
        // text files of each consumer.
        private static ConsumerReferenceIndex BuildConsumerReferenceIndex(
            BatchState state,
            IReadOnlyList<IPackFileContainer> modConsumers,
            int sourceLoadOrderIndex,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
            => BuildConsumerReferenceIndexFor(state.Source, modConsumers, sourceLoadOrderIndex, cancellationToken, progress);

        private static ConsumerReferenceIndex BuildConsumerReferenceIndexFor(
            IPackFileContainer source,
            IReadOnlyList<IPackFileContainer> modConsumers,
            int sourceLoadOrderIndex,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var stopwatch = Stopwatch.StartNew();
            var packs = new List<ConsumerPackEntry>(modConsumers.Count);
            for (var index = 0; index < modConsumers.Count; index++)
                packs.Add(new ConsumerPackEntry(index, modConsumers[index], index < sourceLoadOrderIndex));

            var index_ = new ConsumerReferenceIndex(packs);
            var sourceFiles = source.GetAllFiles();

            // F: source paths a consumer also has. Only asset types the optimizer can touch are
            // frozen; the rest (UI, scripts, tool leftovers) is counted and ignored.
            var sourcePaths = sourceFiles.Keys.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var sourceAssetCount = 0;
            foreach (var path in sourcePaths)
            {
                if (IsConsumerAssetPath(path))
                    sourceAssetCount++;
            }

            foreach (var pack in packs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pack.AssetFiles = pack.Files.Keys.Count(path => IsConsumerAssetPath(path));
                pack.DbTables = pack.Files.Keys.Count(path => Normalize(path).StartsWith("db\\", StringComparison.Ordinal));
                foreach (var path in sourcePaths)
                {
                    if (!pack.Container.ContainsFile(path))
                        continue;

                    if (!IsConsumerAssetPath(path))
                    {
                        pack.OverlapOther++;
                        continue;
                    }

                    pack.OverlapAssets++;
                    if (!index_.Frozen.TryGetValue(path, out var list))
                    {
                        list = [];
                        index_.Frozen[path] = list;
                    }

                    list.Add(pack);
                }

                if (pack.AssetFiles > 0 &&
                    pack.OverlapAssets >= ConsumerDerivedMinimumOverlap &&
                    (double)pack.OverlapAssets / pack.AssetFiles > ConsumerDerivedOverlapShare)
                {
                    index_.Warnings.Add(
                        $"Consumer pack '{pack.Name}' overrides {pack.OverlapAssets:N0} of its {pack.AssetFiles:N0} " +
                        $"asset paths ({(double)pack.OverlapAssets / pack.AssetFiles:P0}) of the source pack. It may be " +
                        "a variant or derived output of the source; every one of those paths is frozen.");
                }
            }

            // Seeds: source paths named by consumer files.
            var directRigidRefs = new List<(ConsumerPackEntry Pack, string Vmd, string Rigid)>();
            for (var packIndex = 0; packIndex < packs.Count; packIndex++)
            {
                var pack = packs[packIndex];
                var packStopwatch = Stopwatch.StartNew();
                var texts = pack.Files.Keys
                    .Select(Normalize)
                    .Where(IsConsumerScannableText)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToList();
                for (var fileIndex = 0; fileIndex < texts.Count; fileIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = texts[fileIndex];
                    if (fileIndex % 200 == 0)
                    {
                        ReportProgress(
                            progress,
                            "Scanning consumer packs",
                            packIndex * 1000 + Math.Min(999, fileIndex * 1000 / Math.Max(1, texts.Count)),
                            packs.Count * 1000,
                            $"{pack.Name}: {path}");
                    }

                    var file = pack.Container.FindFile(path);
                    var data = file == null ? null : TryReadConsumerFile(file, ConsumerScanFileLimit);
                    if (data == null)
                    {
                        pack.FilesUnreadable++;
                        continue;
                    }

                    pack.FilesScanned++;
                    var isVmd = path.EndsWith(".variantmeshdefinition", StringComparison.OrdinalIgnoreCase);
                    var isWsModel = path.EndsWith(".wsmodel", StringComparison.OrdinalIgnoreCase);
                    foreach (var token in ExtractAssetTokens(data, LooksWide(data)))
                    {
                        if (token.Equals(path, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (source.ContainsFile(token))
                        {
                            index_.Used.TryAdd(token, new ConsumerUse(pack, path, string.Empty, false));
                            pack.SeedHits.Add(token);
                            var kind = GetConsumerAssetKind(token);
                            if ((isVmd || isWsModel) && kind is "VMD" or "WSModel" or "rigid")
                                index_.RootFiles.TryAdd(path, pack);
                        }
                        else if (isVmd && token.EndsWith(".rigid_model_v2", StringComparison.OrdinalIgnoreCase))
                        {
                            directRigidRefs.Add((pack, path, token));
                        }
                    }
                }

                pack.ScanElapsed = packStopwatch.Elapsed;
                index_.FilesScanned += pack.FilesScanned;
            }

            // A consumer VMD that draws a consumer rigid directly uses that rigid's embedded
            // materials; the textures they name may be source textures. (A rigid drawn through a
            // WSModel uses the WSModel's materials instead, so those are not read.)
            foreach (var group in directRigidRefs
                         .OrderBy(item => item.Rigid, StringComparer.Ordinal)
                         .ThenBy(item => item.Pack.Index))
            {
                cancellationToken.ThrowIfCancellationRequested();
                PackFile? rigidFile = null;
                foreach (var pack in packs.OrderByDescending(item => item.Index))
                {
                    rigidFile = pack.Container.FindFile(group.Rigid);
                    if (rigidFile != null)
                        break;
                }

                var data = rigidFile == null ? null : TryReadConsumerFile(rigidFile, 256L * 1024 * 1024);
                if (data == null)
                    continue;

                foreach (var token in ExtractAssetTokens(data, includeWideView: false))
                {
                    if (source.ContainsFile(token))
                    {
                        index_.Used.TryAdd(
                            token, new ConsumerUse(group.Pack, group.Rigid, group.Vmd, false));
                    }
                }
            }

            index_.ScanElapsed = stopwatch.Elapsed;
            return index_;
        }

        // Vanilla paths a consumer replaces that the source reaches (directly or through vanilla
        // VMDs, WSModels and materials). The source files that reach one are marked like files a
        // mod uses: nothing is derived from, merged with or rebound through them. Reaching is a
        // token scan, so it over-approximates; a source rigid's own embedded texture paths are
        // not followed.
        private static void ComputeVanillaReplacement(
            IPackFileContainer source,
            IReadOnlyList<IPackFileContainer> vanilla,
            ConsumerReferenceIndex index,
            CancellationToken cancellationToken)
        {
            bool VanillaHas(string path)
            {
                for (var i = vanilla.Count - 1; i >= 0; i--)
                {
                    if (vanilla[i].ContainsFile(path))
                        return true;
                }

                return false;
            }

            PackFile? FindVanilla(string path)
            {
                for (var i = vanilla.Count - 1; i >= 0; i--)
                {
                    if (vanilla[i].ContainsFile(path))
                        return vanilla[i].FindFile(path);
                }

                return null;
            }

            foreach (var pack in index.Packs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var key in pack.Files.Keys)
                {
                    var path = Normalize(key);
                    if (!IsConsumerAssetPath(path) || source.ContainsFile(path) || !VanillaHas(path))
                        continue;

                    if (!index.VanillaReplaced.TryGetValue(path, out var packs))
                    {
                        packs = [];
                        index.VanillaReplaced[path] = packs;
                    }

                    packs.Add(pack);
                }
            }

            if (index.VanillaReplaced.Count == 0)
                return;

            var memo = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> Reach(string vanillaPath)
            {
                if (memo.TryGetValue(vanillaPath, out var cached))
                    return cached;

                var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!visiting.Add(vanillaPath))
                    return result;

                if (index.VanillaReplaced.ContainsKey(vanillaPath))
                {
                    result.Add(vanillaPath);
                }
                else if (vanillaPath.EndsWith(".variantmeshdefinition", StringComparison.OrdinalIgnoreCase) ||
                         vanillaPath.EndsWith(".wsmodel", StringComparison.OrdinalIgnoreCase) ||
                         vanillaPath.EndsWith(".xml.material", StringComparison.OrdinalIgnoreCase))
                {
                    var file = FindVanilla(vanillaPath);
                    var data = file == null ? null : TryReadConsumerFile(file, ConsumerScanFileLimit);
                    if (data != null)
                    {
                        foreach (var token in ExtractAssetTokens(data, LooksWide(data)).Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            if (token.Equals(vanillaPath, StringComparison.OrdinalIgnoreCase) || source.ContainsFile(token))
                                continue;

                            result.UnionWith(Reach(token));
                        }
                    }
                }

                visiting.Remove(vanillaPath);
                memo[vanillaPath] = result;
                return result;
            }

            foreach (var key in source.GetAllFiles().Keys.OrderBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Normalize(key);
                if (!(path.EndsWith(".variantmeshdefinition", StringComparison.OrdinalIgnoreCase) ||
                      path.EndsWith(".wsmodel", StringComparison.OrdinalIgnoreCase) ||
                      path.EndsWith(".xml.material", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var file = source.FindFile(path);
                var data = file == null ? null : TryReadConsumerFile(file, ConsumerScanFileLimit);
                if (data == null)
                    continue;

                var reached = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var token in ExtractAssetTokens(data, LooksWide(data)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (token.Equals(path, StringComparison.OrdinalIgnoreCase) || source.ContainsFile(token))
                        continue;

                    reached.UnionWith(Reach(token));
                }

                if (reached.Count == 0)
                    continue;

                var first = reached.First();
                var firstPack = index.VanillaReplaced[first][0];
                index.Used.TryAdd(
                    path,
                    new ConsumerUse(
                        firstPack,
                        $"reaches the vanilla file {first}, which {string.Join(", ", index.VanillaReplaced[first].Select(item => item.Name))} replaces",
                        string.Empty,
                        false,
                        VanillaPath: true));
                foreach (var replaced in reached)
                {
                    index.VanillaReachedBy.TryAdd(replaced, path);
                    foreach (var replacer in index.VanillaReplaced[replaced])
                        replacer.VanillaReached.Add(replaced);
                }
            }
        }

        private static IReadOnlyList<PackOptimizerConsumerPack> DescribeConsumerPacks(ConsumerReferenceIndex index)
            => index.Packs.Select(pack =>
            {
                var frozen = pack.OverlapAssets;
                var used = pack.SeedHits.Count;
                var dbMentions = Math.Max(pack.DbMentions, pack.DbRootsAdded + pack.DbFallbackAdded);
                var undecodable = Math.Max(pack.UndecodableTables, pack.DbFallbackTables.Count);
                var vanilla = pack.VanillaReached.Count;
                return new PackOptimizerConsumerPack(
                    pack.Container.SystemFilePath ?? string.Empty,
                    pack.Name,
                    frozen > 0 || used > 0 || dbMentions > 0 || undecodable > 0 || vanilla > 0,
                    frozen,
                    used,
                    dbMentions,
                    undecodable,
                    vanilla);
            }).ToList();

        public static IReadOnlyList<PackOptimizerConsumerPack> ProbeConsumers(
            IPackFileContainer source,
            IReadOnlyList<IPackFileContainer> modConsumers,
            int? sourceLoadOrderIndex,
            IReadOnlyList<IPackFileContainer> vanilla,
            PackOptimizerDbSchema? dbSchema,
            CancellationToken cancellationToken)
        {
            var index = BuildConsumerReferenceIndexFor(
                source, modConsumers, sourceLoadOrderIndex ?? modConsumers.Count, cancellationToken, null);

            // DB tables that name a VMD, WSModel or rigid of the source (either text encoding):
            // a superset of the tables that give a source file a new gameplay root.
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in source.GetAllFiles().Keys)
            {
                var path = Normalize(key);
                if (path.EndsWith(".variantmeshdefinition", StringComparison.Ordinal))
                    names.Add(Path.GetFileNameWithoutExtension(path));
                else if (path.EndsWith(".wsmodel", StringComparison.Ordinal) || path.EndsWith(".rigid_model_v2", StringComparison.Ordinal))
                    names.Add(path);
            }

            var tokenPattern = new Regex(@"[a-z0-9_\\/\.\-]{4,260}", RegexOptions.Compiled);
            foreach (var pack in index.Packs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mentioned = new HashSet<string>(StringComparer.Ordinal);
                foreach (var key in pack.Files.Keys.Select(Normalize)
                             .Where(item => item.StartsWith("db\\", StringComparison.Ordinal))
                             .OrderBy(item => item, StringComparer.Ordinal))
                {
                    var file = pack.Container.FindFile(key);
                    var data = file == null ? null : TryReadConsumerFile(file, ConsumerScanFileLimit);
                    if (data == null)
                        continue;

                    foreach (var wide in new[] { false, true })
                    {
                        var bytes = wide ? data.Where(value => value != 0).ToArray() : data;
                        var text = Encoding.Latin1.GetString(bytes).ToLowerInvariant();
                        foreach (Match match in tokenPattern.Matches(text))
                        {
                            var token = match.Value.Replace('/', '\\');
                            if (names.Contains(token))
                            {
                                mentioned.Add(token);
                                continue;
                            }

                            var name = token[(token.LastIndexOf('\\') + 1)..];
                            if (name.EndsWith(".variantmeshdefinition", StringComparison.Ordinal))
                                name = name[..^".variantmeshdefinition".Length];
                            if (names.Contains(name))
                                mentioned.Add(name);
                        }
                    }
                }

                pack.DbMentions = mentioned.Count;
                pack.UndecodableTables = Wh3UnitCategoryResolver.CountUndecodableTables(pack.Container, dbSchema);
            }

            ComputeVanillaReplacement(
                source, vanilla.Where(container => container.IsCaPackFile).ToList(), index, cancellationToken);
            return DescribeConsumerPacks(index);
        }

        private static HashSet<string> GetResolutionRootPaths(Wh3UnitCategoryResolution resolution)
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in resolution.UsagesByVmd.Keys)
                roots.Add(Normalize(path));
            foreach (var path in resolution.DirectAssetUsagesByPath.Keys)
                roots.Add(Normalize(path));
            foreach (var path in resolution.UnresolvedConsumersByVmd.Keys)
                roots.Add(Normalize(path));
            foreach (var unit in resolution.RosterUnits)
            {
                foreach (var component in unit.Components)
                {
                    if (!string.IsNullOrWhiteSpace(component.AssetPath))
                        roots.Add(Normalize(component.AssetPath));
                }
            }

            return roots;
        }

        // Adds what the DB rows of the consumers contribute, closes E inside the source and
        // records the warnings. `withoutConsumers` is the resolution of the same run without any
        // consumer table; the roots only the consumers' rows create are seeds.
        private static void FinalizeConsumerReferenceIndex(
            BatchState state,
            ConsumerReferenceIndex index,
            Wh3UnitCategoryResolution withConsumers,
            Wh3UnitCategoryResolution? withoutConsumers,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var stopwatch = Stopwatch.StartNew();
            ReportProgress(progress, "Indexing consumer references");

            // 1. Source files that become gameplay roots only through a consumer's DB rows.
            if (withoutConsumers != null)
            {
                var before = GetResolutionRootPaths(withoutConsumers);
                var newRoots = GetResolutionRootPaths(withConsumers)
                    .Where(path => !before.Contains(path) && state.Source.ContainsFile(path))
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToList();
                if (newRoots.Count != 0)
                {
                    var attribution = AttributeDbRoots(index, newRoots, cancellationToken);
                    foreach (var root in newRoots)
                    {
                        var pack = attribution.GetValueOrDefault(root) ??
                                   index.Packs.OrderByDescending(item => item.Index)
                                       .FirstOrDefault(item => item.DbTables != 0) ??
                                   index.Packs[^1];
                        if (index.Used.TryAdd(root, new ConsumerUse(pack, "(DB rows)", string.Empty, false)))
                            pack.DbRootsAdded++;
                        index.VmdRootsAddedByDb.Add(root);
                    }
                }
            }

            // 1b. Vanilla paths other mods replace that the source reaches.
            ComputeVanillaReplacement(
                state.Source,
                GetGameplayTraversalContainers(state).Where(container => container.IsCaPackFile).ToList(),
                index,
                cancellationToken);

            // 2. Close E inside the source, then the DB text fallback per pack (so its own
            //    contribution can be counted).
            CloseConsumerUse(state, index, cancellationToken);
            foreach (var failure in withConsumers.ConsumerDbFailures
                         .OrderBy(item => item.Container.Name, StringComparer.Ordinal)
                         .ThenBy(item => item.TablePath, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pack = index.Packs.FirstOrDefault(item => ReferenceEquals(item.Container, failure.Container));
                if (pack == null)
                    continue;

                pack.DbFallbackTables.Add((failure.TablePath, failure.Reason));
                var file = pack.Container.FindFile(failure.TablePath);
                var data = file == null ? null : TryReadConsumerFile(file, ConsumerScanFileLimit);
                if (data == null)
                    continue;

                var added = 0;
                foreach (var token in ExtractAssetTokens(data, includeWideView: true))
                {
                    if (state.Source.ContainsFile(token) &&
                        index.Used.TryAdd(token, new ConsumerUse(pack, failure.TablePath, string.Empty, true)))
                    {
                        added++;
                    }
                }

                var closed = CloseConsumerUse(state, index, cancellationToken);
                pack.DbFallbackAdded += added + closed;
            }

            index.Finalized = true;
            index.FinalizeElapsed = stopwatch.Elapsed;
        }

        // Which pack's DB tables name each root (by file name without extension, in either
        // text encoding). Falls back to the caller's default when none does.
        private static Dictionary<string, ConsumerPackEntry> AttributeDbRoots(
            ConsumerReferenceIndex index,
            IReadOnlyList<string> roots,
            CancellationToken cancellationToken)
        {
            var names = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var root in roots)
            {
                var name = Path.GetFileNameWithoutExtension(root);
                if (!names.TryGetValue(name, out var list))
                {
                    list = [];
                    names[name] = list;
                }

                list.Add(root);
            }

            var result = new Dictionary<string, ConsumerPackEntry>(StringComparer.OrdinalIgnoreCase);
            var nameRegex = new Regex(@"[a-z0-9_\\/\.\-]{4,260}", RegexOptions.Compiled);
            foreach (var pack in index.Packs.OrderByDescending(item => item.Index))
            {
                foreach (var path in pack.Files.Keys.Select(Normalize)
                             .Where(item => item.StartsWith("db\\", StringComparison.Ordinal))
                             .OrderBy(item => item, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var file = pack.Container.FindFile(path);
                    var data = file == null ? null : TryReadConsumerFile(file, ConsumerScanFileLimit);
                    if (data == null)
                        continue;

                    foreach (var view in new[] { false, true })
                    {
                        var bytes = data;
                        if (view)
                            bytes = data.Where(value => value != 0).ToArray();
                        var text = Encoding.Latin1.GetString(bytes).ToLowerInvariant();
                        foreach (Match match in nameRegex.Matches(text))
                        {
                            var token = match.Value.Replace('/', '\\');
                            token = token[(token.LastIndexOf('\\') + 1)..];
                            if (token.EndsWith(".variantmeshdefinition", StringComparison.Ordinal))
                                token = token[..^".variantmeshdefinition".Length];
                            if (names.TryGetValue(token, out var matching))
                            {
                                foreach (var root in matching)
                                    result.TryAdd(root, pack);
                            }
                        }
                    }

                    if (result.Count == roots.Count)
                        return result;
                }
            }

            return result;
        }

        // Adds everything a source file in E names (inside the source) until nothing is new.
        // Returns how many files were added.
        private static int CloseConsumerUse(
            BatchState state,
            ConsumerReferenceIndex index,
            CancellationToken cancellationToken)
            => CloseConsumerUseFor(state.Source, index, cancellationToken);

        private static int CloseConsumerUseFor(
            IPackFileContainer source,
            ConsumerReferenceIndex index,
            CancellationToken cancellationToken)
        {
            var queue = new Queue<string>(index.Used.Keys.OrderBy(path => path, StringComparer.Ordinal));
            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var added = 0;
            // Everything already closed in an earlier call is skipped through `Closed`.
            var closed = index.Closed;
            while (queue.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = queue.Dequeue();
                if (!processed.Add(path) || closed.Contains(path))
                    continue;

                closed.Add(path);
                var use = index.Used[path];
                IEnumerable<string> tokens;
                if (path.EndsWith(".variantmeshdefinition", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".wsmodel", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".xml.material", StringComparison.OrdinalIgnoreCase))
                {
                    var file = source.FindFile(path);
                    var data = file == null ? null : TryReadConsumerFile(file, ConsumerScanFileLimit);
                    tokens = data == null ? [] : ExtractAssetTokens(data, LooksWide(data));
                }
                else if (path.EndsWith(".rigid_model_v2", StringComparison.OrdinalIgnoreCase))
                {
                    tokens = ReadRigidTextureTokens(source.FindFile(path));
                }
                else
                {
                    continue;
                }

                foreach (var token in tokens.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (token.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                        !source.ContainsFile(token))
                    {
                        continue;
                    }

                    if (index.Used.TryAdd(token, new ConsumerUse(use.Pack, use.Referrer, path, use.Fallback, use.VanillaPath)))
                    {
                        added++;
                        queue.Enqueue(token);
                    }
                }
            }

            return added;
        }

        private static IEnumerable<string> ReadRigidTextureTokens(PackFile? file)
        {
            if (file == null)
                return [];

            try
            {
                var data = file.DataSource.ReadData();
                var rigid = ModelFactory.Create().Load(data);
                var tokens = new List<string>();
                foreach (var lod in rigid.ModelList ?? [])
                {
                    foreach (var model in lod)
                    {
                        foreach (var texture in model.Material?.GetAllTextures() ?? [])
                        {
                            var normalized = Normalize(texture.Path).TrimStart('\\');
                            if (normalized.Length != 0)
                                tokens.Add(normalized);
                        }
                    }
                }

                return tokens;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Unparseable: whatever paths its bytes contain count.
                try
                {
                    return ExtractAssetTokens(file.DataSource.ReadData(), includeWideView: false).ToList();
                }
                catch (Exception inner) when (inner is not OperationCanceledException)
                {
                    return [];
                }
            }
        }

        // Why a rigid may not change, as the names that stand in for "immutable WSModel
        // consumers" in the stages' existing checks (any entry blocks). Empty without consumers.
        // Why an atlas candidate may not be rewritten because of other mods, else null: its
        // geometry is handled by the rigid check, this covers the WSModels, the material and the
        // textures it names.
        private static string? GetConsumerAtlasBlock(BatchState state, AtlasCandidate candidate)
        {
            var consumers = state.Consumers;
            if (consumers == null)
                return null;

            var paths = new List<string> { Normalize(candidate.MaterialPath) };
            paths.AddRange(candidate.Usages.Select(usage => Normalize(usage.WsModelPath)));
            paths.AddRange(candidate.Usages
                .Where(usage => !string.IsNullOrWhiteSpace(usage.EmbeddedRigidPath))
                .Select(usage => Normalize(usage.EmbeddedRigidPath)));
            var textureNodes = candidate.MaterialDocument.SelectNodes("/material/textures/texture");
            if (textureNodes != null)
            {
                foreach (System.Xml.XmlNode node in textureNodes)
                    paths.Add(Normalize(node.SelectSingleNode("source")?.InnerText ?? node.InnerText));
            }

            foreach (var path in paths.Where(path => path.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (consumers.TryBlock(path, "atlas"))
                    return $"{path} is {consumers.Describe(path)}";
                if (consumers.TryBlockVanillaReplaced(path, "atlas"))
                    return $"{path} is a vanilla file another mod replaces";
            }

            return null;
        }

        private static IReadOnlyList<string> GetConsumerRigidBlockers(
            BatchState state,
            string rigidPath,
            string caller)
        {
            var consumers = state.Consumers;
            if (consumers == null)
                return [];

            var blockers = new List<string>();
            var found = new List<(string Kind, string Pack, string Detail)>();
            if (consumers.Used.TryGetValue(rigidPath, out var use))
            {
                blockers.Add($"[mod] {ConsumerReferenceIndex.DescribeUse(use)}");
                found.Add(("used", use.Pack.Name, ConsumerReferenceIndex.DescribeUse(use)));
            }

            if (consumers.Frozen.TryGetValue(rigidPath, out var freezers))
            {
                blockers.Add($"[mod] overridden by {ConsumerReferenceIndex.DescribeFreeze(freezers)}");
                found.Add(("overridden", string.Join(", ", freezers.Select(pack => pack.Name)),
                    ConsumerReferenceIndex.DescribeFreeze(freezers)));
            }

            // A WSModel that names this rigid and is itself used or overridden by a consumer
            // would have its part indices or bindings changed by a layout change.
            if (state.GameplayMeshDependencyIndex != null &&
                state.GameplayMeshDependencyIndex.WsModelConsumersByRigid.TryGetValue(rigidPath, out var wsModels))
            {
                foreach (var wsModel in wsModels.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    if (consumers.Used.TryGetValue(wsModel, out var wsUse))
                    {
                        blockers.Add($"[mod] WSModel {wsModel}: {ConsumerReferenceIndex.DescribeUse(wsUse)}");
                        found.Add(("used", wsUse.Pack.Name, $"WSModel {wsModel}: {ConsumerReferenceIndex.DescribeUse(wsUse)}"));
                    }
                    else if (consumers.Frozen.TryGetValue(wsModel, out var wsFreezers))
                    {
                        blockers.Add($"[mod] WSModel {wsModel} overridden by {ConsumerReferenceIndex.DescribeFreeze(wsFreezers)}");
                        found.Add(("overridden", string.Join(", ", wsFreezers.Select(pack => pack.Name)),
                            $"WSModel {wsModel}: {ConsumerReferenceIndex.DescribeFreeze(wsFreezers)}"));
                    }
                }
            }

            // Source WSModels bound to the rigid that no gameplay root reaches are not in the
            // dependency index but are rewritten together with it.
            foreach (var (wsPath, document) in state.WsDocuments)
            {
                var wsNormalized = Normalize(wsPath);
                if ((!consumers.Used.ContainsKey(wsNormalized) && !consumers.Frozen.ContainsKey(wsNormalized)) ||
                    !Normalize(document.SelectSingleNode("/model/geometry")?.InnerText)
                        .Equals(rigidPath, StringComparison.OrdinalIgnoreCase) ||
                    blockers.Any(text => text.Contains($"WSModel {wsNormalized}", StringComparison.Ordinal)))
                {
                    continue;
                }

                blockers.Add($"[mod] WSModel {wsNormalized}: {consumers.Describe(wsNormalized)}");
                found.Add((consumers.Used.ContainsKey(wsNormalized) ? "used" : "overridden",
                    consumers.Used.TryGetValue(wsNormalized, out var wsUse2)
                        ? wsUse2.Pack.Name
                        : string.Join(", ", consumers.Frozen[wsNormalized].Select(pack => pack.Name)),
                    $"WSModel {wsNormalized}: {consumers.Describe(wsNormalized)}"));
            }

            foreach (var (kind, pack, detail) in found)
                consumers.RecordBlock(rigidPath, StageForCaller(caller), kind, pack, detail);

            return blockers;
        }

        // The stage a check that asks "may this rigid change?" belongs to, for the report.
        private static string StageForCaller(string caller)
            => caller switch
            {
                "TryGetStructuralMergeBlockReason" => "same-rigid merge",
                "GetRigidNotOnlyThroughOwnedWsModelsReason" => "repack / compaction",
                "SelectNoAtlasGeometryOptimizationRigids" => "geometry optimisation",
                "HasUnrewritableMaterialConsumerAtlasConflict" or "IsAtlasCandidateGameplayDiscoverySafe" => "atlas",
                "CheckUnreadAlpha" or "CompactTextures" or "CompactTexture" => "compaction",
                "KeepVanillaOverrides" => "prune",
                _ when caller.Contains("Repack", StringComparison.Ordinal) => "repack",
                _ => caller,
            };

        private static bool TryGetDependencyWsModelEntry(
            BatchState state,
            IPackFileContainer container,
            string wsModelPath,
            PackFile file,
            out VanillaWsModelConsumerEntry entry)
        {
            if (state.Consumers?.IsModContainer(container) != true)
                return TryGetWsModelConsumerEntry(state, container, wsModelPath, file, out entry);

            // A consumer's WSModel may sit on a path the source or the game also has, so it never
            // shares the path-keyed entry caches.
            try
            {
                entry = BuildWsModelConsumerEntry(LoadXml(file, out _));
                return true;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                System.Xml.XmlException or
                FormatException or
                ArgumentException)
            {
                entry = null!;
                return false;
            }
        }

        // The mesh dependency index resolves through the consumers too (they are roots of it),
        // highest priority first: mods that load after the source, the source, mods that load
        // before it, then the game packs. Without consumers this is the usual resolution.
        private static IPackFileContainer? FindDependencyContainer(BatchState state, string pathValue)
        {
            var consumers = state.Consumers;
            if (consumers == null)
            {
                return FindGameplayTraversalContainer(
                    state, state.Source, GetGameplayTraversalContainers(state), pathValue);
            }

            var path = Normalize(pathValue);
            if (path.Length == 0)
                return null;

            if (state.DependencyContainerResolutions.TryGetValue(path, out var cached))
                return cached;

            IPackFileContainer? resolved = null;
            foreach (var pack in consumers.Packs.Where(item => !item.BelowSource).OrderByDescending(item => item.Index))
            {
                if (pack.Container.ContainsFile(path))
                {
                    resolved = pack.Container;
                    break;
                }
            }

            if (resolved == null && state.Source.ContainsFile(path))
                resolved = state.Source;
            if (resolved == null)
            {
                foreach (var pack in consumers.Packs.Where(item => item.BelowSource).OrderByDescending(item => item.Index))
                {
                    if (pack.Container.ContainsFile(path))
                    {
                        resolved = pack.Container;
                        break;
                    }
                }
            }

            resolved ??= ResolveGameplayTraversalContainer(
                state.Source, GetGameplayTraversalContainers(state), path);
            state.DependencyContainerResolutions[path] = resolved;
            return resolved;
        }

        // Every path in E or F must come out of the run byte for byte as it went in. The only
        // exceptions are the named ones in ConsumerReferenceIndex.IntegrityExceptions.
        private static void ValidateConsumerIntegrity(
            BatchState state,
            List<string> errors,
            CancellationToken cancellationToken)
        {
            var consumers = state.Consumers;
            if (consumers == null)
                return;

            var paths = consumers.Used.Keys.Concat(consumers.Frozen.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (consumers.IntegrityExceptions.Contains(path))
                    continue;

                consumers.IntegrityChecked++;
                var before = state.Source.FindFile(path);
                var after = state.Output.FindFile(path);
                string? problem = null;
                if (before == null)
                {
                    continue;
                }

                if (after == null)
                {
                    problem = "was removed from the output";
                }
                else if (!ReferenceEquals(before.DataSource, after.DataSource))
                {
                    var left = before.DataSource.ReadData();
                    var right = after.DataSource.ReadData();
                    if (!left.AsSpan().SequenceEqual(right))
                        problem = "differs from the source in the output";
                }

                if (problem != null)
                {
                    consumers.IntegrityViolations.Add((path, problem));
                    errors.Add(
                        $"Another mod relies on {path} ({consumers.Describe(path)}), but it {problem}.");
                }
            }

            if (consumers.IntegrityViolations.Count == 0)
            {
                state.ValidationMessages.Add(
                    $"PASS: {consumers.IntegrityChecked:N0} file(s) that other mods use or override are " +
                    "byte-identical to the source in the output.");
            }
        }

        // Which schema decoded each table; only when a schema was supplied. Every line starts with
        // "DB schema source:" so the bench fingerprint can leave them out when comparing a run that
        // used the supplied schema with one that did not.
        private static void AppendDbSchemaSources(StringBuilder sb, BatchState state)
        {
            if (state.DbSchemaPath == null || state.UnitCategoryResolution == null)
                return;

            sb.AppendLine($"DB schema source: supplied schema file {state.DbSchemaPath}");
            foreach (var line in state.UnitCategoryResolution.DbSchemaSources)
                sb.AppendLine($"DB schema source: {line}");
        }

        private static void AppendConsumerSection(StringBuilder sb, BatchState state)
        {
            var consumers = state.Consumers;
            if (consumers == null)
                return;

            sb.AppendLine("Other mods (consumers)");
            sb.AppendLine("----------------------");
            sb.AppendLine(
                $"Consumer packs: {consumers.Packs.Count:N0} (load order, first loads first and loses, last wins; " +
                $"{consumers.Packs.Count(pack => pack.BelowSource):N0} load before the source, " +
                $"{consumers.Packs.Count(pack => !pack.BelowSource):N0} after it)");
            sb.AppendLine(
                $"Files scanned: {consumers.FilesScanned:N0}; scan {consumers.ScanElapsed.TotalSeconds:0.0} s, " +
                $"reference index {consumers.FinalizeElapsed.TotalSeconds:0.0} s");
            foreach (var warning in consumers.Warnings)
                sb.AppendLine($"WARNING: {warning}");

            sb.AppendLine();
            sb.AppendLine("Pack | position | asset files | overrides (assets / other) | files scanned | DB tables | DB tables read as text | source paths added by that fallback | roots added by DB rows");
            foreach (var pack in consumers.Packs)
            {
                sb.AppendLine(
                    $"{pack.Name} | {pack.Index} ({(pack.BelowSource ? "before" : "after")} source) | {pack.AssetFiles:N0} | " +
                    $"{pack.OverlapAssets:N0} / {pack.OverlapOther:N0} | {pack.FilesScanned:N0} | {pack.DbTables:N0} | " +
                    $"{pack.DbFallbackTables.Count:N0} | {pack.DbFallbackAdded:N0} | {pack.DbRootsAdded:N0}");
                foreach (var (path, reason) in pack.DbFallbackTables)
                    sb.AppendLine($"    DB table read as text because it could not be decoded: {path}: {reason}");
            }

            AppendConsumerDbSection(sb, state, consumers);
            sb.AppendLine();
            sb.AppendLine("Kept because another mod uses it (not pruned, not changed in place)");
            sb.AppendLine("--------------------------------------------------------------------");
            AppendConsumerCountTable(sb, consumers.Used.Keys, "files used by other mods");
            foreach (var (path, use) in consumers.Used.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                var stages = consumers.Blocks
                    .Where(block => block.Kind == "used" && block.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                    .Select(block => block.Stage)
                    .Distinct()
                    .OrderBy(stage => stage, StringComparer.Ordinal)
                    .ToList();
                sb.AppendLine(
                    $"{path} | {ConsumerReferenceIndex.DescribeUse(use)} | " +
                    (stages.Count == 0 ? "not touched by any stage" : "blocked: " + string.Join(", ", stages)));
            }

            if (consumers.Used.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("Frozen because another mod overrides it");
            sb.AppendLine("----------------------------------------");
            AppendConsumerCountTable(sb, consumers.Frozen.Keys, "source paths that other mods also have");
            foreach (var (path, packs) in consumers.Frozen.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                var stages = consumers.Blocks
                    .Where(block => block.Kind == "overridden" && block.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                    .Select(block => block.Stage)
                    .Distinct()
                    .OrderBy(stage => stage, StringComparer.Ordinal)
                    .ToList();
                sb.AppendLine(
                    $"{path} | {ConsumerReferenceIndex.DescribeFreeze(packs)} | " +
                    (stages.Count == 0 ? "not touched by any stage" : "blocked: " + string.Join(", ", stages)));
            }

            if (consumers.Frozen.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("Vanilla paths another mod replaces");
            sb.AppendLine("----------------------------------");
            sb.AppendLine(
                $"Vanilla asset paths other mods also have (the source does not): {consumers.VanillaReplaced.Count:N0}; " +
                $"reached by the source: {consumers.VanillaReachedBy.Count:N0}; " +
                $"source files frozen because they reach one: {consumers.Used.Values.Count(use => use.VanillaPath):N0}");
            foreach (var pack in consumers.Packs.Where(pack => consumers.VanillaReplaced.Values.Any(list => list.Contains(pack))))
            {
                sb.AppendLine(
                    $"  {pack.Name}: replaces {consumers.VanillaReplaced.Values.Count(list => list.Contains(pack)):N0} vanilla asset path(s), " +
                    $"{pack.VanillaReached.Count:N0} reached by the source");
            }

            foreach (var (replaced, reachedBy) in consumers.VanillaReachedBy.OrderBy(item => item.Key, StringComparer.Ordinal).Take(200))
            {
                sb.AppendLine(
                    $"{replaced} | replaced by {string.Join(", ", consumers.VanillaReplaced[replaced].Select(pack => pack.Name))} | reached by {reachedBy}");
            }

            if (consumers.VanillaReachedBy.Count == 0)
                sb.AppendLine("(none reached)");
            sb.AppendLine();

            sb.AppendLine("Blocked operations");
            sb.AppendLine("------------------");
            foreach (var group in consumers.Blocks
                         .GroupBy(block => block.Stage)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                sb.AppendLine(
                    $"{group.Key}: {group.Select(block => block.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0} file(s) " +
                    $"(used {group.Count(block => block.Kind == "used"):N0}, overridden {group.Count(block => block.Kind == "overridden"):N0})");
            }

            if (consumers.Blocks.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("Source VMDs and assets that become gameplay roots only through consumer DB rows");
            sb.AppendLine("---------------------------------------------------------------------------------");
            foreach (var root in consumers.VmdRootsAddedByDb)
                sb.AppendLine(root);
            if (consumers.VmdRootsAddedByDb.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("Integrity");
            sb.AppendLine("---------");
            sb.AppendLine(
                $"Checked {consumers.IntegrityChecked:N0} file(s) in E and F against the source; " +
                $"{consumers.IntegrityViolations.Count:N0} violation(s); " +
                $"{consumers.IntegrityExceptions.Count:N0} named exception(s).");
            foreach (var (path, reason) in consumers.IntegrityViolations)
                sb.AppendLine($"VIOLATION {path}: {reason}");
            foreach (var path in consumers.IntegrityExceptions.OrderBy(item => item, StringComparer.Ordinal))
                sb.AppendLine($"EXCEPTION {path}");
            sb.AppendLine();
        }

        private static void AppendConsumerDbSection(StringBuilder sb, BatchState state, ConsumerReferenceIndex consumers)
        {
            var resolution = state.UnitCategoryResolution;
            if (resolution == null)
                return;

            sb.AppendLine();
            sb.AppendLine("Consumer DB rows");
            sb.AppendLine("----------------");
            foreach (var pack in consumers.Packs)
            {
                var overrides = resolution.ConsumerRowOverrides
                    .Where(item => ReferenceEquals(item.Container, pack.Container))
                    .ToList();
                var ignored = overrides.Count(item => item.ReplacedGameOrSourceRow);
                var byTable = string.Join(", ", overrides
                    .Where(item => item.ReplacedGameOrSourceRow)
                    .GroupBy(item => item.Table)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => $"{group.Key} {group.Count():N0}"));
                sb.AppendLine(
                    $"{pack.Name}: rows added {resolution.ConsumerRowsAdded.GetValueOrDefault(pack.Container):N0}; " +
                    $"existing game/source rows overridden (ignored for analysis) {ignored:N0}" +
                    (byTable.Length == 0 ? string.Empty : $" [{byTable}]"));
            }

            var misread = resolution.ConsumerTableInfos
                .Where(info => !info.DecodeInfo.EndsWith("bytesLeft=0", StringComparison.Ordinal))
                .ToList();
            sb.AppendLine($"Consumer tables decoded with bytes left over after the last row: {misread.Count:N0}");
            foreach (var info in misread.Take(50))
                sb.AppendLine($"    {info.Container.Name}: {info.TablePath}: {info.DecodeInfo}");

            foreach (var group in resolution.ConsumerRowOverrides
                         .Where(item => item.ReplacedGameOrSourceRow)
                         .GroupBy(item => (item.Container, item.FilePath))
                         .OrderBy(group => group.Key.Container.Name, StringComparer.Ordinal)
                         .ThenBy(group => group.Key.FilePath, StringComparer.Ordinal))
            {
                var info = resolution.ConsumerTableInfos.FirstOrDefault(item =>
                    ReferenceEquals(item.Container, group.Key.Container) && item.TablePath == group.Key.FilePath);
                sb.AppendLine($"    table {group.Key.FilePath}: {group.Count():N0} overridden row(s); decode {info?.DecodeInfo}");
            }

            sb.AppendLine("Overridden game/source rows (consumer table file | key | changed columns before -> after), first 400:");
            foreach (var item in resolution.ConsumerRowOverrides
                         .Where(item => item.ReplacedGameOrSourceRow)
                         .OrderBy(item => item.Container.Name, StringComparer.Ordinal)
                         .ThenBy(item => item.Table, StringComparer.Ordinal)
                         .ThenBy(item => item.Key, StringComparer.Ordinal)
                         .Take(400))
            {
                var changes = item.Changes.Length > 300 ? item.Changes[..300] + "..." : item.Changes;
                sb.AppendLine($"    {item.Container.Name}: {item.FilePath} | {item.Key.Replace('\u001f', '/')} | {changes}");
            }
        }

        private static void AppendConsumerCountTable(StringBuilder sb, IEnumerable<string> paths, string label)
        {
            var counts = paths
                .GroupBy(GetConsumerAssetKind)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Key} {group.Count():N0}")
                .ToList();
            sb.AppendLine($"Total {label}: {(counts.Count == 0 ? "0" : string.Join(", ", counts))}");
        }
    }
}
