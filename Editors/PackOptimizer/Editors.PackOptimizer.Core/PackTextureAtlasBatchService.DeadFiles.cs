using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Shared.Core.PackFiles.Models;
using Shared.GameFormats.RigidModel;

namespace Editors.PackOptimizer.Core
{
    public sealed partial class PackTextureAtlasBatchService
    {
        // Opt-in "prune dead files": source files nothing reaches any more. The normal prune only
        // removes what this run made unreachable; this removes what the mod author left behind
        // (authoring files, backup/test meshes, textures nothing names), after every conservative
        // check. A doubt keeps the file. Nothing is deleted before the whole analysis succeeded.
        //
        // A candidate is a source file in the output that no VMD root reaches and that is
        //  * an authoring/junk type in any folder (never loaded by the game), or
        //  * a .dds/.rigid_model_v2/.wsmodel/.xml.material outside the folders whose files are
        //    loaded by name from code or data (ui, script, db, text, audio, animations): those are
        //    only reported.
        // It is kept when it overrides a vanilla file (or vanilla data is not loaded), when
        // another enabled mod uses or overrides it, or (asset types) when any file that stays in
        // the pack mentions it. A mention is the file's stem (the name without any extension)
        // as a case-insensitive plain substring, in UTF-8 or UTF-16LE form: every full path ends
        // in the stem, so the stem needle covers path mentions too, with any slash style. Rigid
        // models count through the textures their materials embed. The set of dead files is a
        // fixpoint: a mention only counts when its file stays, so a dead WSModel that names a
        // dead rigid removes both.
        private static readonly string[] DeadFileAuthoringExtensions =
        [
            ".psd", ".blend", ".blend1", ".fbx", ".max", ".ma", ".mb", ".spp", ".sbs", ".xcf", ".kra", ".bak", ".tmp",
        ];

        private static readonly string[] DeadFileAuthoringNames = ["thumbs.db", "desktop.ini"];

        private static readonly string[] DeadFileAssetSuffixes =
            [".dds", ".rigid_model_v2", ".wsmodel", ".xml.material"];

        private static readonly string[] DeadFileLoadedByNameFolders =
            ["ui\\", "script\\", "db\\", "text\\", "audio\\", "animations\\"];

        // Files the mention scan does not read: images and audio say nothing in text form.
        private static readonly string[] DeadFileUnscannedExtensions =
            [".dds", ".png", ".jpg", ".jpeg", ".tga", ".wem", ".bnk"];

        private const int DeadFileScanChunk = 1 << 20;
        private const int DeadFileReportListLimit = 400;

        private enum DeadFileStatus
        {
            Dead,
            Kept,
        }

        private sealed class DeadFileCandidate
        {
            public string Path { get; init; } = string.Empty;
            public PackFile File { get; init; } = null!;
            // Size in the pack (compressed files count as stored).
            public long Bytes { get; init; }
            // Uncompressed size, read for removed files and DDS files only (0 = not read).
            public long RawBytes { get; set; }
            // Authoring/junk types skip the mention scan: the game cannot load them.
            public bool NeverLoaded { get; init; }
            // Asset type inside a folder that is loaded by name: reported, never removed.
            public bool ReportOnly { get; init; }
            public string Category { get; init; } = string.Empty;
            public string Stem { get; init; } = string.Empty;
            public DeadFileStatus Status { get; set; } = DeadFileStatus.Dead;
            public string KeptReason { get; set; } = string.Empty;
            public string KeptDetail { get; set; } = string.Empty;
        }

        private sealed class DeadFilesResult
        {
            public bool VanillaKnown { get; set; }
            public int Candidates { get; set; }
            public int NeedleStems { get; set; }
            public int Rounds { get; set; }
            public int FilesScanned { get; set; }
            public long BytesScanned { get; set; }
            public int ConsumerFilesScanned { get; set; }
            public int RigidsParsed { get; set; }
            public int RigidsUnparsed { get; set; }
            public TimeSpan Elapsed { get; set; }
            public TimeSpan ScanElapsed { get; set; }
            public string? Failure { get; set; }
            public List<DeadFileCandidate> Removed { get; } = [];
            public List<DeadFileCandidate> Kept { get; } = [];
            public List<DeadFileCandidate> ReportOnly { get; } = [];
            public List<(string Path, string Problem)> DamagedDds { get; } = [];
        }

        private void PruneDeadFiles(
            BatchState state,
            HashSet<string> currentReachable,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var result = new DeadFilesResult();
            state.DeadFiles = result;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                ReportProgress(progress, "Finding dead files");
                AnalyseDeadFiles(state, result, currentReachable, cancellationToken, progress);
                var paths = result.Removed
                    .Select(candidate => candidate.Path)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Where(state.Output.ContainsFile)
                    .ToList();
                ReportProgress(progress, "Removing dead files", paths.Count, paths.Count, $"{paths.Count:N0} files");
                if (paths.Count != 0)
                {
                    _packFileService.DeleteFiles(state.Output, paths);
                    state.RemovedFiles.AddRange(paths);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The analysis deletes nothing; a failure leaves the output as it was.
                result.Failure = $"{ex.GetType().Name}: {ex.Message}";
                result.Removed.Clear();
            }

            result.Elapsed = stopwatch.Elapsed;
        }

        private static bool IsDeadFileNeverLoaded(string path)
        {
            var name = path[(path.LastIndexOf('\\') + 1)..];
            if (DeadFileAuthoringNames.Contains(name, StringComparer.Ordinal))
                return true;
            return DeadFileAuthoringExtensions.Any(extension => name.EndsWith(extension, StringComparison.Ordinal));
        }

        private static string GetDeadFileStem(string path)
        {
            var name = path[(path.LastIndexOf('\\') + 1)..];
            var dot = name.IndexOf('.');
            return dot > 0 ? name[..dot] : name;
        }

        private static string GetDeadFileCategory(string path)
        {
            foreach (var suffix in DeadFileAssetSuffixes)
            {
                if (path.EndsWith(suffix, StringComparison.Ordinal))
                    return suffix;
            }

            var extension = Path.GetExtension(path);
            return extension.Length == 0 ? "(no extension)" : extension;
        }

        private static bool IsDeadFileUnscanned(string path)
            => DeadFileUnscannedExtensions.Any(extension => path.EndsWith(extension, StringComparison.Ordinal)) ||
               IsDeadFileNeverLoaded(path);

        private void AnalyseDeadFiles(
            BatchState state,
            DeadFilesResult result,
            HashSet<string> currentReachable,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var vanillaKnown = GetGameplayTraversalContainers(state).Any(container =>
                container.IsCaPackFile && !ReferenceEquals(container, state.Source));
            result.VanillaKnown = vanillaKnown;
            var generated = new HashSet<string>(
                state.GeneratedTexturePaths.Concat(state.SharedRepackTexturePaths).Concat(state.GeneratedMaterialPaths)
                    .Select(Normalize),
                StringComparer.OrdinalIgnoreCase);

            var allFiles = new Dictionary<string, PackFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var (rawPath, file) in state.Output.GetAllFiles())
                allFiles[Normalize(rawPath)] = file;

            // Candidates: source files that no root reaches.
            var candidates = new List<DeadFileCandidate>();
            foreach (var (path, file) in allFiles.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (currentReachable.Contains(path) || generated.Contains(path) || !state.Source.ContainsFile(path))
                    continue;

                var neverLoaded = IsDeadFileNeverLoaded(path);
                var isAsset = DeadFileAssetSuffixes.Any(suffix => path.EndsWith(suffix, StringComparison.Ordinal));
                if (!neverLoaded && !isAsset)
                    continue;

                candidates.Add(new DeadFileCandidate
                {
                    Path = path,
                    File = file,
                    Bytes = file.DataSource.Size,
                    NeverLoaded = neverLoaded,
                    ReportOnly = !neverLoaded &&
                                 DeadFileLoadedByNameFolders.Any(folder => path.StartsWith(folder, StringComparison.Ordinal)),
                    Category = GetDeadFileCategory(path),
                    Stem = GetDeadFileStem(path),
                });
            }

            result.Candidates = candidates.Count;
            var byPath = candidates.ToDictionary(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase);

            // Checks that need no file contents.
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = candidate.Path;
                string? reason = null;
                string detail = string.Empty;
                if (state.CrossRigidRetainedSourceAssetPaths.Contains(path))
                {
                    reason = "kept for a cross-rigid merge";
                }
                else if (state.ModifiedWsModels.Contains(path) || state.ModifiedRigids.Contains(path) ||
                         state.EditorLodRemadeRigids.Contains(path))
                {
                    // A stage rewrote it (for instance a WSModel bound to a rewritten rigid that no
                    // root reaches); the output validation expects it to stay.
                    reason = "rewritten by an earlier stage of this run";
                }
                else if (state.Consumers != null && state.Consumers.TryBlock(path, "dead-file"))
                {
                    reason = "another mod uses or overrides it";
                    detail = state.Consumers.Describe(path);
                }
                else if (!vanillaKnown)
                {
                    reason = "vanilla game data is not loaded, so an override of a vanilla file cannot be ruled out";
                }
                else if (OverridesVanillaPath(state, path))
                {
                    reason = "overrides a vanilla file";
                }

                if (reason != null)
                    MarkDeadFileKept(candidate, reason, detail);
            }

            // Mention scan for the asset types that are still dead.
            var scanStopwatch = Stopwatch.StartNew();
            var testable = candidates.Where(candidate => !candidate.NeverLoaded && candidate.Status == DeadFileStatus.Dead).ToList();
            if (testable.Count != 0)
                RunDeadFileMentionScan(state, result, allFiles, byPath, testable, cancellationToken, progress);
            result.ScanElapsed = scanStopwatch.Elapsed;

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.Status == DeadFileStatus.Kept)
                    result.Kept.Add(candidate);
                else if (candidate.ReportOnly)
                    result.ReportOnly.Add(candidate);
                else
                    result.Removed.Add(candidate);

                var isDds = candidate.Path.EndsWith(".dds", StringComparison.Ordinal);
                if (isDds || (candidate.Status == DeadFileStatus.Dead && !candidate.ReportOnly))
                {
                    try
                    {
                        candidate.RawBytes = candidate.File.DataSource.ReadData().LongLength;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        candidate.RawBytes = candidate.Bytes;
                    }
                }

                if (isDds)
                {
                    var problem = GetDeadDdsProblem(candidate);
                    if (problem != null)
                        result.DamagedDds.Add((candidate.Path, problem));
                }
            }
        }

        private static void MarkDeadFileKept(DeadFileCandidate candidate, string reason, string detail)
        {
            candidate.Status = DeadFileStatus.Kept;
            candidate.KeptReason = reason;
            candidate.KeptDetail = detail;
        }

        private static string? GetDeadDdsProblem(DeadFileCandidate candidate)
        {
            try
            {
                var header = candidate.File.DataSource.PeekData(TextureDownscaleHeaderPeekBytes);
                if (!TryReadDdsLayout(header, out var layout))
                {
                    // A valid header of a format the layout reader does not know (uncompressed, ...) is not damage.
                    var validMagic = header.Length >= 128 && header[0] == 'D' && header[1] == 'D' &&
                                     header[2] == 'S' && header[3] == ' ' && BitConverter.ToInt32(header, 4) == 124;
                    return validMagic ? null : "unreadable header";
                }
                var needed = layout.HeaderSize + layout.ChainBytes;
                return candidate.RawBytes < needed
                    ? $"truncated ({candidate.RawBytes:N0} of {needed:N0} bytes)"
                    : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return $"unreadable ({ex.GetType().Name})";
            }
        }

        // ---- Mention scan -----------------------------------------------------------------

        private sealed class DeadFileNeedles
        {
            public SearchValues<string> Values { get; init; } = null!;
            // Needle text (Latin-1 view of the UTF-8 or UTF-16LE bytes) -> stem id.
            public Dictionary<string, int> Ids { get; init; } = null!;
            public int[] Lengths { get; init; } = [];
            public int MaxLength { get; init; }
        }

        private static DeadFileNeedles BuildDeadFileNeedles(IReadOnlyList<(int Id, string Stem)> stems)
        {
            var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, stem) in stems)
            {
                ids.TryAdd(Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(stem)), id);
                ids.TryAdd(Encoding.Latin1.GetString(Encoding.Unicode.GetBytes(stem)), id);
            }

            var lengths = ids.Keys.Select(key => key.Length).Distinct().Order().ToArray();
            return new DeadFileNeedles
            {
                Values = SearchValues.Create(ids.Keys.ToArray(), StringComparison.OrdinalIgnoreCase),
                Ids = ids,
                Lengths = lengths,
                MaxLength = lengths.Length == 0 ? 1 : lengths[^1],
            };
        }

        // Calls `found` with the id of every needle in the bytes (a needle may be reported more than once).
        private static void ScanDeadFileBytes(
            ReadOnlySpan<byte> data,
            DeadFileNeedles needles,
            char[] buffer,
            Action<int> found)
        {
            var lookup = needles.Ids.GetAlternateLookup<ReadOnlySpan<char>>();
            var overlap = needles.MaxLength - 1;
            for (var offset = 0; offset < data.Length; offset += DeadFileScanChunk)
            {
                var end = Math.Min(data.Length, offset + DeadFileScanChunk + overlap);
                var length = Encoding.Latin1.GetChars(data[offset..end], buffer);
                var text = buffer.AsSpan(0, length);
                var position = 0;
                while (position < text.Length)
                {
                    var relative = text[position..].IndexOfAny(needles.Values);
                    if (relative < 0)
                        break;

                    var index = position + relative;
                    foreach (var needleLength in needles.Lengths)
                    {
                        if (index + needleLength > text.Length)
                            break;
                        if (lookup.TryGetValue(text.Slice(index, needleLength), out var id))
                            found(id);
                    }

                    position = index + 1;
                }

                if (end == data.Length)
                    break;
            }
        }

        // The texture paths a rigid's materials embed, as text. null when it does not parse.
        private static byte[]? ReadDeadFileRigidTextureText(PackFile file)
        {
            try
            {
                var rigid = ModelFactory.Create().LoadMaterialsOnly(file.DataSource.ReadData());
                var sb = new StringBuilder();
                foreach (var lod in rigid.ModelList ?? [])
                {
                    foreach (var model in lod)
                    {
                        foreach (var texture in model.Material?.GetAllTextures() ?? [])
                            sb.Append(texture.Path).Append('\n');
                    }
                }

                return Encoding.UTF8.GetBytes(sb.ToString());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }

        private void RunDeadFileMentionScan(
            BatchState state,
            DeadFilesResult result,
            Dictionary<string, PackFile> allFiles,
            Dictionary<string, DeadFileCandidate> byPath,
            List<DeadFileCandidate> testable,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            // Stem id -> the candidates carrying that stem.
            var owners = new List<List<DeadFileCandidate>>();
            var stemIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in testable)
            {
                if (!stemIds.TryGetValue(candidate.Stem, out var id))
                {
                    id = owners.Count;
                    stemIds[candidate.Stem] = id;
                    owners.Add([]);
                }

                owners[id].Add(candidate);
            }

            var stems = stemIds.Select(pair => (Id: pair.Value, Stem: pair.Key)).ToList();
            result.NeedleStems = stems.Count;
            var alive = new HashSet<int>(stems.Select(item => item.Id));

            // Files that stay in the pack and can mention something. Rigids are read through their
            // embedded textures. Candidates that are still dead are not part of it.
            var pending = new List<(string Path, PackFile File)>();
            foreach (var (path, file) in allFiles.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (byPath.TryGetValue(path, out var candidate) &&
                    candidate.Status == DeadFileStatus.Dead && !candidate.ReportOnly)
                {
                    continue;
                }

                if (IsDeadFileUnscanned(path))
                    continue;
                pending.Add((path, file));
            }

            var parallel = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8),
            };
            var referrers = new ConcurrentDictionary<int, string>();
            var mergeLock = new object();
            var rigidsParsed = 0;
            var rigidsUnparsed = 0;

            // Another enabled mod's files, once: they all stay. (Images, audio and rigids are not read.)
            var consumerRefs = new Dictionary<int, string>();
            if (state.Consumers != null)
            {
                var needles = BuildDeadFileNeedles(stems);
                var consumerFiles = new List<(string Pack, string Path, PackFile File)>();
                foreach (var pack in state.Consumers.Packs)
                {
                    foreach (var (rawPath, file) in pack.Files)
                    {
                        var path = Normalize(rawPath);
                        if (IsDeadFileUnscanned(path) || path.EndsWith(".rigid_model_v2", StringComparison.Ordinal))
                            continue;
                        consumerFiles.Add((pack.Name, path, file));
                    }
                }

                consumerFiles.Sort((a, b) => string.CompareOrdinal(a.Pack + "|" + a.Path, b.Pack + "|" + b.Path));
                var scanned = 0;
                Parallel.ForEach(
                    consumerFiles,
                    parallel,
                    () => (Buffer: new char[DeadFileScanChunk + needles.MaxLength], Hits: new HashSet<int>()),
                    (item, _, local) =>
                    {
                        local.Hits.Clear();
                        byte[]? data = null;
                        if (item.File.DataSource.Size <= ConsumerScanFileLimit)
                            data = TryReadConsumerFile(item.File, ConsumerScanFileLimit);
                        if (data != null)
                        {
                            ScanDeadFileBytes(data, needles, local.Buffer, id => local.Hits.Add(id));
                            Interlocked.Increment(ref scanned);
                        }

                        if (local.Hits.Count != 0)
                        {
                            var label = item.Pack + ": " + item.Path;
                            lock (mergeLock)
                            {
                                foreach (var id in local.Hits)
                                {
                                    if (!consumerRefs.TryGetValue(id, out var existing) ||
                                        string.CompareOrdinal(label, existing) < 0)
                                    {
                                        consumerRefs[id] = label;
                                    }
                                }
                            }
                        }

                        return local;
                    },
                    _ => { });
                result.ConsumerFilesScanned = scanned;
                foreach (var (id, label) in consumerRefs)
                {
                    foreach (var candidate in owners[id])
                    {
                        if (candidate.Status == DeadFileStatus.Dead)
                            MarkDeadFileKept(candidate, "mentioned by another mod's file", label);
                    }

                    alive.Remove(id);
                }
            }

            while (pending.Count != 0 && alive.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Rounds++;
                var needles = BuildDeadFileNeedles(stems.Where(item => alive.Contains(item.Id)).ToList());
                var round = pending;
                var done = 0;
                long bytes = 0;
                referrers.Clear();
                Parallel.ForEach(
                    round,
                    parallel,
                    () => (Buffer: new char[DeadFileScanChunk + needles.MaxLength], Hits: new HashSet<int>()),
                    (item, _, local) =>
                    {
                        var current = Interlocked.Increment(ref done);
                        if (current % 200 == 0)
                            ReportProgress(progress, "Scanning for mentions of dead files", current, round.Count, item.Path);
                        local.Hits.Clear();
                        try
                        {
                            byte[]? data;
                            if (item.Path.EndsWith(".rigid_model_v2", StringComparison.Ordinal))
                            {
                                data = ReadDeadFileRigidTextureText(item.File);
                                if (data == null)
                                {
                                    // Does not parse: whatever its bytes contain counts.
                                    Interlocked.Increment(ref rigidsUnparsed);
                                    data = item.File.DataSource.ReadData();
                                }
                                else
                                {
                                    Interlocked.Increment(ref rigidsParsed);
                                }
                            }
                            else
                            {
                                data = item.File.DataSource.ReadData();
                            }

                            Interlocked.Add(ref bytes, data.LongLength);
                            ScanDeadFileBytes(data, needles, local.Buffer, id => local.Hits.Add(id));
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // Unreadable: cannot say what it names, so it keeps every candidate.
                            foreach (var id in alive)
                                local.Hits.Add(id);
                        }

                        if (local.Hits.Count != 0)
                        {
                            foreach (var id in local.Hits)
                            {
                                // A candidate that mentions its own stem (an excluded-folder file) does not count.
                                if (owners[id].Count == 1 &&
                                    owners[id][0].Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                referrers.AddOrUpdate(
                                    id,
                                    item.Path,
                                    (_, existing) => string.CompareOrdinal(item.Path, existing) < 0 ? item.Path : existing);
                            }
                        }

                        return local;
                    },
                    _ => { });
                result.FilesScanned += round.Count;
                result.BytesScanned += bytes;

                var next = new List<(string Path, PackFile File)>();
                foreach (var (id, referrer) in referrers.OrderBy(pair => pair.Key))
                {
                    foreach (var candidate in owners[id])
                    {
                        if (candidate.Status != DeadFileStatus.Dead ||
                            candidate.Path.Equals(referrer, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        MarkDeadFileKept(candidate, "mentioned by a file that stays", referrer);
                        if (!candidate.ReportOnly && !IsDeadFileUnscanned(candidate.Path))
                            next.Add((candidate.Path, candidate.File));
                    }

                    if (owners[id].All(candidate => candidate.Status == DeadFileStatus.Kept))
                        alive.Remove(id);
                }

                // A kept candidate now mentions things too.
                pending = next;
            }

            result.RigidsParsed = rigidsParsed;
            result.RigidsUnparsed = rigidsUnparsed;
        }

        // ---- Report -----------------------------------------------------------------------

        private static void AppendDeadFilesSection(StringBuilder sb, BatchState state)
        {
            var result = state.DeadFiles;
            if (result == null)
                return;

            sb.AppendLine("Dead files");
            sb.AppendLine("----------");
            if (result.Failure != null)
            {
                sb.AppendLine($"Analysis failed, nothing was removed: {result.Failure}");
                sb.AppendLine();
                return;
            }

            var removedBytes = result.Removed.Sum(candidate => candidate.Bytes);
            var removedRaw = result.Removed.Sum(candidate => candidate.RawBytes);
            sb.AppendLine("Mode: REMOVE (unreachable source files that nothing mentions and no other mod uses)");
            sb.AppendLine(
                $"Vanilla game data: {(result.VanillaKnown ? "loaded" : "not loaded (every asset candidate is kept)")}");
            sb.AppendLine($"Unreachable source files examined: {result.Candidates:N0}");
            sb.AppendLine(
                $"Mention scan: {result.NeedleStems:N0} stem(s), {result.Rounds:N0} round(s), " +
                $"{result.FilesScanned:N0} file(s) / {FormatMiB(result.BytesScanned)} of this pack" +
                (result.ConsumerFilesScanned == 0 ? string.Empty : $" plus {result.ConsumerFilesScanned:N0} file(s) of other mods") +
                $", rigid models read through their materials {result.RigidsParsed:N0} (unparsable, read raw: {result.RigidsUnparsed:N0})");
            sb.AppendLine(
                $"Removed: {result.Removed.Count:N0} file(s), {FormatMiB(removedBytes)} in the pack " +
                $"({FormatMiB(removedRaw)} uncompressed)");
            foreach (var group in result.Removed
                         .GroupBy(candidate => candidate.Category, StringComparer.Ordinal)
                         .OrderByDescending(group => group.Sum(candidate => candidate.Bytes))
                         .ThenBy(group => group.Key, StringComparer.Ordinal))
            {
                sb.AppendLine(
                    $"  by type {group.Key}: {group.Count():N0} file(s), {FormatMiB(group.Sum(candidate => candidate.Bytes))} " +
                    $"({FormatMiB(group.Sum(candidate => candidate.RawBytes))} uncompressed)");
            }

            foreach (var group in result.Removed
                         .GroupBy(candidate => candidate.Path.Split('\\')[0], StringComparer.Ordinal)
                         .OrderByDescending(group => group.Sum(candidate => candidate.Bytes))
                         .ThenBy(group => group.Key, StringComparer.Ordinal))
            {
                sb.AppendLine(
                    $"  by folder {group.Key}\\: {group.Count():N0} file(s), {FormatMiB(group.Sum(candidate => candidate.Bytes))} " +
                    $"({FormatMiB(group.Sum(candidate => candidate.RawBytes))} uncompressed)");
            }

            sb.AppendLine("Removed files (path, bytes in the pack, bytes uncompressed, type):");
            foreach (var candidate in result.Removed.OrderBy(candidate => candidate.Path, StringComparer.Ordinal))
                sb.AppendLine($"  {candidate.Path}, {candidate.Bytes:N0}, {candidate.RawBytes:N0}, {candidate.Category}");
            if (result.Removed.Count == 0)
                sb.AppendLine("  (none)");

            sb.AppendLine($"Kept candidates: {result.Kept.Count:N0}");
            foreach (var group in result.Kept
                         .GroupBy(candidate => candidate.KeptReason, StringComparer.Ordinal)
                         .OrderByDescending(group => group.Count())
                         .ThenBy(group => group.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"  {group.Key}: {group.Count():N0} file(s), {FormatMiB(group.Sum(candidate => candidate.Bytes))}");
                foreach (var candidate in group.OrderBy(candidate => candidate.Path, StringComparer.Ordinal).Take(DeadFileReportListLimit))
                {
                    sb.AppendLine(
                        $"    {candidate.Path}, {candidate.Bytes:N0}" +
                        (candidate.KeptDetail.Length == 0 ? string.Empty : $" <- {candidate.KeptDetail}"));
                }

                if (group.Count() > DeadFileReportListLimit)
                    sb.AppendLine($"    ... and {group.Count() - DeadFileReportListLimit:N0} more");
            }

            sb.AppendLine(
                $"Unreferenced files in folders that code or data load by name (report only, kept): " +
                $"{result.ReportOnly.Count:N0} file(s), {FormatMiB(result.ReportOnly.Sum(candidate => candidate.Bytes))}");
            foreach (var candidate in result.ReportOnly.OrderByDescending(candidate => candidate.Bytes)
                         .ThenBy(candidate => candidate.Path, StringComparer.Ordinal).Take(DeadFileReportListLimit))
            {
                sb.AppendLine($"  {candidate.Path}, {candidate.Bytes:N0}");
            }

            sb.AppendLine($"Damaged DDS among the candidates: {result.DamagedDds.Count:N0}");
            foreach (var (path, problem) in result.DamagedDds.OrderBy(item => item.Path, StringComparer.Ordinal))
                sb.AppendLine($"  {path}: {problem}");
            sb.AppendLine();
        }
    }
}
