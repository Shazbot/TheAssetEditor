using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Shared.Core.PackFiles.Models;
using ZstdSharp;

namespace Editors.ImportExport.Exporting.Exporters.RmvToGltf;

public sealed class GltfAnimationMetadataLookupCacheOptions
{
    public GltfAnimationMetadataLookupCacheOptions(
        string cacheDirectory,
        IReadOnlySet<IPackFileContainer> cacheableContainers)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectory))
            throw new ArgumentException("A cache directory is required.", nameof(cacheDirectory));

        CacheDirectory = Path.GetFullPath(cacheDirectory);
        CacheableContainers = cacheableContainers ?? throw new ArgumentNullException(nameof(cacheableContainers));
    }

    public string CacheDirectory { get; }
    public IReadOnlySet<IPackFileContainer> CacheableContainers { get; }
}

internal sealed record GltfAnimationMetadataCachedContext(
    string FragmentPath,
    string SkeletonName,
    string? MetaPath,
    string? PersistentMetaPath,
    IReadOnlyDictionary<string, string> SlotAnimations);

internal sealed class GltfAnimationMetadataLookupCache
{
    private const uint CacheMagic = 0x434D4147; // GAMC
    private const uint PayloadMagic = 0x58444D47; // GMDX
    private const int CurrentSchemaVersion = 2;
    private const int CurrentPayloadVersion = 1;
    private const string CacheFileName = "animation-metadata-index.bin";
    private const string LegacyCacheFileName = "animation-metadata-index.br";

    private const int MaxPackCount = 10_000;
    private const int MaxFragmentCount = 1_000_000;
    private const int MaxAnimationCount = 1_000_000;
    private const int MaxMetadataPathCount = 2_000_000;
    private const int MaxContextCount = 10_000_000;
    private const int MaxCompressedPackBytes = 512 * 1024 * 1024;
    private const int MaxUncompressedPackBytes = 1024 * 1024 * 1024;

    private readonly ILogger _logger = Logging.Create<GltfAnimationMetadataLookupCache>();
    private readonly GltfAnimationMetadataLookupCacheOptions _options;
    private readonly object _sync = new();
    private readonly Dictionary<string, CachedPackEnvelope> _packsByPath = new(StringComparer.OrdinalIgnoreCase);

    private bool _isLoaded;
    private bool _isDirty;

    public GltfAnimationMetadataLookupCache(GltfAnimationMetadataLookupCacheOptions options)
    {
        _options = options;
    }

    public bool IsCacheable(IPackFileContainer container)
        => _options.CacheableContainers.Contains(container);

    public CachedPackIndex? TryLoad(IPackFileContainer container)
    {
        if (!IsCacheable(container))
            return null;

        if (!TryGetPackStamp(container, out var packStamp))
        {
            _logger.Here().Debug(
                "GLTF animation metadata cache SKIP for [{ContainerName}]: pack metadata is unavailable",
                container.Name);
            return null;
        }

        EnsureLoaded();

        CachedPackEnvelope cachedPack;
        lock (_sync)
        {
            if (!_packsByPath.TryGetValue(packStamp.PackPath, out var foundPack))
            {
                LogMiss(container, "pack entry not found");
                return null;
            }

            cachedPack = foundPack;

            if (cachedPack.Length != packStamp.Length)
            {
                _packsByPath.Remove(packStamp.PackPath);
                _isDirty = true;
                LogMiss(container, $"pack size changed ({cachedPack.Length} -> {packStamp.Length})");
                return null;
            }

            if (cachedPack.LastWriteTimeUtcTicks != packStamp.LastWriteTimeUtcTicks)
            {
                _packsByPath.Remove(packStamp.PackPath);
                _isDirty = true;
                LogMiss(container, "pack last-write time changed");
                return null;
            }

            if (cachedPack.ParsedIndex != null)
                return cachedPack.ParsedIndex;
        }

        var totalStopwatch = Stopwatch.StartNew();
        var phaseStopwatch = Stopwatch.StartNew();

        try
        {
            using var decompressor = new Decompressor();
            var payload = decompressor.Unwrap(
                cachedPack.CompressedPayload,
                cachedPack.UncompressedLength).ToArray();
            phaseStopwatch.Stop();
            var decompressMs = phaseStopwatch.Elapsed.TotalMilliseconds;

            if (payload.Length != cachedPack.UncompressedLength)
            {
                throw new InvalidDataException(
                    $"Decompressed payload length {payload.Length} does not match expected {cachedPack.UncompressedLength}.");
            }

            phaseStopwatch.Restart();
            var parsedIndex = CachedPackIndex.Create(payload);
            phaseStopwatch.Stop();
            totalStopwatch.Stop();

            lock (_sync)
            {
                if (_packsByPath.TryGetValue(packStamp.PackPath, out var current)
                    && ReferenceEquals(current, cachedPack))
                {
                    current.ParsedIndex = parsedIndex;
                }
            }

            _logger.Here().Information(
                "GLTF animation metadata cache HIT for [{ContainerName}] in {ElapsedMs:F1}ms: decompress={DecompressMs:F1}ms, binaryIndex={BinaryIndexMs:F1}ms, fragments={FragmentCount}, entries={EntryCount}, animations={AnimationCount}, compressedBytes={CompressedBytes}, binaryBytes={BinaryBytes}",
                container.Name,
                totalStopwatch.Elapsed.TotalMilliseconds,
                decompressMs,
                phaseStopwatch.Elapsed.TotalMilliseconds,
                parsedIndex.FragmentCount,
                parsedIndex.EntryCount,
                parsedIndex.AnimationCount,
                cachedPack.CompressedPayload.Length,
                payload.Length);

            return parsedIndex;
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                _packsByPath.Remove(packStamp.PackPath);
                _isDirty = true;
            }

            _logger.Here().Warning(
                "Ignoring invalid GLTF animation metadata cache entry for [{ContainerName}]: {Message}",
                container.Name,
                exception.Message);
            return null;
        }
    }

    public void Save(IPackFileContainer container, IReadOnlyList<CachedFragment> fragments)
    {
        if (!IsCacheable(container)
            || !TryGetPackStamp(container, out var packStamp))
            return;

        EnsureLoaded();

        var stopwatch = Stopwatch.StartNew();
        var payload = BuildPayload(fragments);
        var binaryBytes = payload.Length;

        using var compressor = new Compressor(1);
        var compressedPayload = compressor.Wrap(payload).ToArray();
        stopwatch.Stop();

        var cachedPack = new CachedPackEnvelope
        {
            PackPath = packStamp.PackPath,
            Length = packStamp.Length,
            LastWriteTimeUtcTicks = packStamp.LastWriteTimeUtcTicks,
            UncompressedLength = binaryBytes,
            CompressedPayload = compressedPayload
        };

        lock (_sync)
        {
            _packsByPath[packStamp.PackPath] = cachedPack;
            _isDirty = true;
        }

        _logger.Here().Information(
            "GLTF animation metadata cache encoded [{ContainerName}] in {ElapsedMs:F1}ms: fragments={FragmentCount}, entries={EntryCount}, compressedBytes={CompressedBytes}, binaryBytes={BinaryBytes}",
            container.Name,
            stopwatch.Elapsed.TotalMilliseconds,
            fragments.Count,
            fragments.Sum(fragment => fragment.Entries.Count),
            compressedPayload.Length,
            binaryBytes);
    }

    public void Flush()
    {
        EnsureLoaded();

        lock (_sync)
        {
            if (!_isDirty)
                return;

            var cachePath = GetCacheFilePath();
            string? temporaryPath = null;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                Directory.CreateDirectory(_options.CacheDirectory);
                temporaryPath = string.Concat(cachePath, ".", Guid.NewGuid().ToString("N"), ".tmp");

                using (var fileStream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                using (var writer = new BinaryWriter(fileStream, Encoding.UTF8, leaveOpen: false))
                {
                    writer.Write(CacheMagic);
                    writer.Write(CurrentSchemaVersion);

                    var packs = _packsByPath.Values
                        .OrderBy(pack => pack.PackPath, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    writer.Write(packs.Count);

                    foreach (var pack in packs)
                    {
                        writer.Write(pack.PackPath);
                        writer.Write(pack.Length);
                        writer.Write(pack.LastWriteTimeUtcTicks);
                        writer.Write(pack.UncompressedLength);
                        writer.Write(pack.CompressedPayload.Length);
                        writer.Write(pack.CompressedPayload);
                    }
                }

                File.Move(temporaryPath, cachePath, overwrite: true);
                temporaryPath = null;
                _isDirty = false;

                TryDeleteLegacyCache();

                stopwatch.Stop();
                var fileBytes = new FileInfo(cachePath).Length;
                _logger.Here().Information(
                    "GLTF animation metadata cache SAVED in {ElapsedMs:F1}ms: packs={PackCount}, fileBytes={FileBytes}",
                    stopwatch.Elapsed.TotalMilliseconds,
                    _packsByPath.Count,
                    fileBytes);
            }
            catch (Exception exception)
            {
                _logger.Here().Warning(
                    "Unable to save GLTF animation metadata cache '{CachePath}': {Message}",
                    cachePath,
                    exception.Message);
            }
            finally
            {
                if (temporaryPath != null)
                {
                    try
                    {
                        File.Delete(temporaryPath);
                    }
                    catch
                    {
                        // Cache writes are best effort.
                    }
                }
            }
        }
    }

    private void EnsureLoaded()
    {
        lock (_sync)
        {
            if (_isLoaded)
                return;

            _isLoaded = true;
            var cachePath = GetCacheFilePath();
            if (!File.Exists(cachePath))
            {
                _logger.Here().Debug(
                    "GLTF animation metadata binary cache not found at '{CachePath}'",
                    cachePath);
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var fileStream = new FileStream(
                    cachePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                using var reader = new BinaryReader(fileStream, Encoding.UTF8, leaveOpen: false);

                if (reader.ReadUInt32() != CacheMagic)
                    throw new InvalidDataException("Cache magic did not match.");

                var schemaVersion = reader.ReadInt32();
                if (schemaVersion != CurrentSchemaVersion)
                {
                    throw new InvalidDataException(
                        $"Schema version {schemaVersion} does not match {CurrentSchemaVersion}.");
                }

                var packCount = ReadCount(reader, MaxPackCount, "pack");
                for (var index = 0; index < packCount; index++)
                {
                    var packPath = Path.GetFullPath(reader.ReadString());
                    var length = reader.ReadInt64();
                    var lastWriteTicks = reader.ReadInt64();
                    var uncompressedLength = reader.ReadInt32();
                    var compressedLength = reader.ReadInt32();

                    if (uncompressedLength < 0 || uncompressedLength > MaxUncompressedPackBytes)
                        throw new InvalidDataException($"Invalid uncompressed payload length {uncompressedLength}.");
                    if (compressedLength < 0 || compressedLength > MaxCompressedPackBytes)
                        throw new InvalidDataException($"Invalid compressed payload length {compressedLength}.");

                    var compressedPayload = reader.ReadBytes(compressedLength);
                    if (compressedPayload.Length != compressedLength)
                        throw new EndOfStreamException("Animation metadata cache ended inside a compressed payload.");

                    _packsByPath[packPath] = new CachedPackEnvelope
                    {
                        PackPath = packPath,
                        Length = length,
                        LastWriteTimeUtcTicks = lastWriteTicks,
                        UncompressedLength = uncompressedLength,
                        CompressedPayload = compressedPayload
                    };
                }

                if (fileStream.Position != fileStream.Length)
                    throw new InvalidDataException("Animation metadata cache had trailing bytes.");

                var currentPackPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var container in _options.CacheableContainers)
                {
                    if (TryGetPackStamp(container, out var currentStamp))
                        currentPackPaths.Add(currentStamp.PackPath);
                }

                var stalePaths = _packsByPath.Keys
                    .Where(path => !currentPackPaths.Contains(path))
                    .ToList();
                foreach (var stalePath in stalePaths)
                    _packsByPath.Remove(stalePath);

                if (stalePaths.Count > 0)
                    _isDirty = true;

                stopwatch.Stop();
                _logger.Here().Information(
                    "GLTF animation metadata binary cache LOADED in {ElapsedMs:F1}ms with {PackCount} pack entries ({RemovedPackCount} stale entries removed), fileBytes={FileBytes}",
                    stopwatch.Elapsed.TotalMilliseconds,
                    _packsByPath.Count,
                    stalePaths.Count,
                    fileStream.Length);
            }
            catch (Exception exception)
            {
                _packsByPath.Clear();
                _logger.Here().Warning(
                    "Ignoring invalid GLTF animation metadata binary cache '{CachePath}': {Message}",
                    cachePath,
                    exception.Message);
            }
        }
    }

    private static byte[] BuildPayload(IReadOnlyList<CachedFragment> fragments)
    {
        var animationIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var animationPaths = new List<string>();
        var metadataIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var metadataPaths = new List<string>();
        var contexts = new List<ContextBuildRecord>();

        for (var fragmentIndex = 0; fragmentIndex < fragments.Count; fragmentIndex++)
        {
            foreach (var entry in fragments[fragmentIndex].Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.AnimationPath))
                    continue;

                if (!animationIds.TryGetValue(entry.AnimationPath, out var animationId))
                {
                    animationId = animationPaths.Count;
                    animationIds.Add(entry.AnimationPath, animationId);
                    animationPaths.Add(entry.AnimationPath);
                }

                var metadataId = -1;
                if (!string.IsNullOrWhiteSpace(entry.MetaPath))
                {
                    if (!metadataIds.TryGetValue(entry.MetaPath, out metadataId))
                    {
                        metadataId = metadataPaths.Count;
                        metadataIds.Add(entry.MetaPath, metadataId);
                        metadataPaths.Add(entry.MetaPath);
                    }
                }

                contexts.Add(new ContextBuildRecord(animationId, fragmentIndex, metadataId));
            }
        }

        var counts = new int[animationPaths.Count];
        foreach (var context in contexts)
            counts[context.AnimationId]++;

        var starts = new int[counts.Length];
        var running = 0;
        for (var animationId = 0; animationId < counts.Length; animationId++)
        {
            starts[animationId] = running;
            running = checked(running + counts[animationId]);
        }

        var positions = (int[])starts.Clone();
        var groupedContexts = new GroupedContextRecord[contexts.Count];
        foreach (var context in contexts)
        {
            var destination = positions[context.AnimationId]++;
            groupedContexts[destination] = new GroupedContextRecord(
                context.FragmentId,
                context.MetadataId);
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(PayloadMagic);
        writer.Write(CurrentPayloadVersion);

        writer.Write(fragments.Count);
        foreach (var fragment in fragments)
        {
            writer.Write(fragment.FragmentPath ?? string.Empty);
            writer.Write(fragment.SkeletonName ?? string.Empty);
            WriteNullableString(writer, fragment.PersistentMetaPath);

            writer.Write(fragment.SlotAnimations.Count);
            foreach (var slot in fragment.SlotAnimations)
            {
                writer.Write(slot.SlotName ?? string.Empty);
                writer.Write(slot.AnimationPath ?? string.Empty);
            }
        }

        writer.Write(animationPaths.Count);
        var groupedCursor = 0;
        for (var animationId = 0; animationId < animationPaths.Count; animationId++)
        {
            writer.Write(animationPaths[animationId]);
            var count = counts[animationId];
            writer.Write(count);

            for (var contextIndex = 0; contextIndex < count; contextIndex++)
            {
                var context = groupedContexts[groupedCursor++];
                writer.Write(context.FragmentId);
                writer.Write(context.MetadataId);
            }
        }

        writer.Write(metadataPaths.Count);
        foreach (var metadataPath in metadataPaths)
            writer.Write(metadataPath);

        writer.Flush();
        return stream.ToArray();
    }

    private void LogMiss(IPackFileContainer container, string reason)
    {
        _logger.Here().Debug(
            "GLTF animation metadata cache MISS for [{ContainerName}]: {Reason}",
            container.Name,
            reason);
    }

    private string GetCacheFilePath()
        => Path.Combine(_options.CacheDirectory, CacheFileName);

    private void TryDeleteLegacyCache()
    {
        var legacyPath = Path.Combine(_options.CacheDirectory, LegacyCacheFileName);
        try
        {
            if (File.Exists(legacyPath))
                File.Delete(legacyPath);
        }
        catch
        {
            // Legacy cleanup is optional.
        }
    }

    private static int ReadCount(BinaryReader reader, int maximum, string description)
    {
        var value = reader.ReadInt32();
        if (value < 0 || value > maximum)
        {
            throw new InvalidDataException(
                $"Invalid {description} count {value}; expected 0..{maximum}.");
        }

        return value;
    }

    private static void WriteNullableString(BinaryWriter writer, string? value)
    {
        writer.Write(value != null);
        if (value != null)
            writer.Write(value);
    }

    private static string? ReadNullableString(BinaryReader reader)
        => reader.ReadBoolean() ? reader.ReadString() : null;

    private static bool TryGetPackStamp(IPackFileContainer container, out PackStamp stamp)
    {
        stamp = default;
        if (string.IsNullOrWhiteSpace(container.SystemFilePath))
            return false;

        try
        {
            var fileInfo = new FileInfo(container.SystemFilePath);
            if (!fileInfo.Exists)
                return false;

            stamp = new PackStamp(
                Path.GetFullPath(fileInfo.FullName),
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc.Ticks);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private readonly record struct PackStamp(
        string PackPath,
        long Length,
        long LastWriteTimeUtcTicks);

    private readonly record struct ContextBuildRecord(
        int AnimationId,
        int FragmentId,
        int MetadataId);

    private readonly record struct GroupedContextRecord(
        int FragmentId,
        int MetadataId);

    private sealed class CachedPackEnvelope
    {
        public string PackPath { get; set; } = string.Empty;
        public long Length { get; set; }
        public long LastWriteTimeUtcTicks { get; set; }
        public int UncompressedLength { get; set; }
        public byte[] CompressedPayload { get; set; } = [];
        public CachedPackIndex? ParsedIndex { get; set; }
    }

    internal sealed class CachedFragment
    {
        public string FragmentPath { get; set; } = string.Empty;
        public string SkeletonName { get; set; } = string.Empty;
        public string? PersistentMetaPath { get; set; }
        public List<CachedSlotAnimation> SlotAnimations { get; set; } = [];
        public List<CachedEntry> Entries { get; set; } = [];
    }

    internal sealed class CachedSlotAnimation
    {
        public string SlotName { get; set; } = string.Empty;
        public string AnimationPath { get; set; } = string.Empty;
    }

    internal sealed class CachedEntry
    {
        public string AnimationPath { get; set; } = string.Empty;
        public string? MetaPath { get; set; }
    }

    internal sealed class CachedPackIndex
    {
        private readonly byte[] _payload;
        private readonly CachedFragmentRuntime[] _fragments;
        private readonly string[] _metadataPaths;
        private readonly Dictionary<string, AnimationRange> _ranges;

        private CachedPackIndex(
            byte[] payload,
            CachedFragmentRuntime[] fragments,
            string[] metadataPaths,
            Dictionary<string, AnimationRange> ranges,
            int entryCount)
        {
            _payload = payload;
            _fragments = fragments;
            _metadataPaths = metadataPaths;
            _ranges = ranges;
            EntryCount = entryCount;
        }

        public int FragmentCount => _fragments.Length;
        public int EntryCount { get; }
        public int AnimationCount => _ranges.Count;
        public IEnumerable<string> AnimationPaths => _ranges.Keys;

        public IReadOnlyList<GltfAnimationMetadataCachedContext> GetContexts(string animationPath)
        {
            if (!_ranges.TryGetValue(animationPath, out var range))
                return [];

            var output = new List<GltfAnimationMetadataCachedContext>(range.Count);
            var offset = range.Offset;

            for (var index = 0; index < range.Count; index++)
            {
                EnsurePayloadBytes(_payload, offset, sizeof(int) * 2);

                var fragmentId = BinaryPrimitives.ReadInt32LittleEndian(
                    _payload.AsSpan(offset, sizeof(int)));
                var metadataId = BinaryPrimitives.ReadInt32LittleEndian(
                    _payload.AsSpan(offset + sizeof(int), sizeof(int)));
                offset += sizeof(int) * 2;

                if ((uint)fragmentId >= (uint)_fragments.Length)
                    throw new InvalidDataException($"Cached fragment id {fragmentId} is out of range.");

                var fragment = _fragments[fragmentId];

                string? metadataPath = null;
                if (metadataId >= 0)
                {
                    if ((uint)metadataId >= (uint)_metadataPaths.Length)
                        throw new InvalidDataException($"Cached metadata id {metadataId} is out of range.");
                    metadataPath = _metadataPaths[metadataId];
                }

                output.Add(new GltfAnimationMetadataCachedContext(
                    fragment.FragmentPath,
                    fragment.SkeletonName,
                    metadataPath,
                    fragment.PersistentMetaPath,
                    fragment.SlotAnimations));
            }

            return output;
        }

        public static CachedPackIndex Create(byte[] payload)
        {
            using var stream = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            if (reader.ReadUInt32() != PayloadMagic)
                throw new InvalidDataException("Animation metadata payload magic did not match.");

            var payloadVersion = reader.ReadInt32();
            if (payloadVersion != CurrentPayloadVersion)
            {
                throw new InvalidDataException(
                    $"Payload version {payloadVersion} does not match {CurrentPayloadVersion}.");
            }

            var fragmentCount = ReadCount(reader, MaxFragmentCount, "fragment");
            var fragments = new CachedFragmentRuntime[fragmentCount];

            for (var fragmentIndex = 0; fragmentIndex < fragmentCount; fragmentIndex++)
            {
                var fragmentPath = reader.ReadString();
                var skeletonName = reader.ReadString();
                var persistentMetaPath = ReadNullableString(reader);

                var slotCount = ReadCount(reader, 256, "docking slot");
                var slotAnimations = new Dictionary<string, string>(
                    slotCount,
                    StringComparer.Ordinal);
                for (var slotIndex = 0; slotIndex < slotCount; slotIndex++)
                {
                    var slotName = reader.ReadString();
                    var animationPath = reader.ReadString();
                    if (!slotAnimations.ContainsKey(slotName))
                        slotAnimations.Add(slotName, animationPath);
                }

                fragments[fragmentIndex] = new CachedFragmentRuntime(
                    fragmentPath,
                    skeletonName,
                    persistentMetaPath,
                    slotAnimations);
            }

            var animationCount = ReadCount(reader, MaxAnimationCount, "animation");
            var ranges = new Dictionary<string, AnimationRange>(
                animationCount,
                StringComparer.OrdinalIgnoreCase);
            var entryCount = 0;

            for (var animationIndex = 0; animationIndex < animationCount; animationIndex++)
            {
                var animationPath = reader.ReadString();
                var contextCount = ReadCount(reader, MaxContextCount, "animation context");

                var contextBytes = checked(contextCount * sizeof(int) * 2);
                if (stream.Position > payload.Length - contextBytes)
                    throw new EndOfStreamException("Animation metadata payload ended inside context records.");

                var offset = checked((int)stream.Position);
                ranges[animationPath] = new AnimationRange(offset, contextCount);
                stream.Position += contextBytes;
                entryCount = checked(entryCount + contextCount);
            }

            if (entryCount > MaxContextCount)
                throw new InvalidDataException($"Context count {entryCount} exceeds {MaxContextCount}.");

            var metadataPathCount = ReadCount(reader, MaxMetadataPathCount, "metadata path");
            var metadataPaths = new string[metadataPathCount];
            for (var metadataIndex = 0; metadataIndex < metadataPathCount; metadataIndex++)
                metadataPaths[metadataIndex] = reader.ReadString();

            if (stream.Position != payload.Length)
                throw new InvalidDataException("Animation metadata payload had trailing bytes.");

            return new CachedPackIndex(
                payload,
                fragments,
                metadataPaths,
                ranges,
                entryCount);
        }

        private static void EnsurePayloadBytes(byte[] payload, int offset, int count)
        {
            if (offset < 0 || count < 0 || offset > payload.Length - count)
                throw new EndOfStreamException("Animation metadata context record was truncated.");
        }

        private sealed record CachedFragmentRuntime(
            string FragmentPath,
            string SkeletonName,
            string? PersistentMetaPath,
            IReadOnlyDictionary<string, string> SlotAnimations);

        private readonly record struct AnimationRange(int Offset, int Count);
    }
}
