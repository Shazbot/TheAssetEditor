using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Shared.Core.PackFiles.Models;

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

internal sealed class GltfAnimationMetadataLookupCache
{
    private const int CurrentSchemaVersion = 1;
    private const string CacheFileName = "animation-metadata-index.br";

    private readonly ILogger _logger = Logging.Create<GltfAnimationMetadataLookupCache>();
    private readonly GltfAnimationMetadataLookupCacheOptions _options;
    private readonly object _sync = new();
    private readonly Dictionary<string, CachedPack> _packsByPath = new(StringComparer.OrdinalIgnoreCase);

    private bool _isLoaded;
    private bool _isDirty;

    public GltfAnimationMetadataLookupCache(GltfAnimationMetadataLookupCacheOptions options)
    {
        _options = options;
    }

    public bool IsCacheable(IPackFileContainer container)
        => _options.CacheableContainers.Contains(container);

    public IReadOnlyList<CachedFragment>? TryLoad(IPackFileContainer container)
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

        var stopwatch = Stopwatch.StartNew();
        CachedPack cachedPack;
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
        }

        stopwatch.Stop();
        _logger.Here().Debug(
            "GLTF animation metadata cache HIT for [{ContainerName}] in {ElapsedMs}ms: fragments={FragmentCount}, entries={EntryCount}",
            container.Name,
            stopwatch.ElapsedMilliseconds,
            cachedPack.Fragments?.Count ?? 0,
            cachedPack.Fragments?.Sum(fragment => fragment.Entries?.Count ?? 0) ?? 0);

        return cachedPack.Fragments ?? [];
    }

    public void Save(IPackFileContainer container, IReadOnlyList<CachedFragment> fragments)
    {
        if (!IsCacheable(container)
            || !TryGetPackStamp(container, out var packStamp))
            return;

        EnsureLoaded();

        var cachedPack = new CachedPack
        {
            PackPath = packStamp.PackPath,
            Length = packStamp.Length,
            LastWriteTimeUtcTicks = packStamp.LastWriteTimeUtcTicks,
            Fragments = fragments.ToList()
        };

        lock (_sync)
        {
            _packsByPath[packStamp.PackPath] = cachedPack;
            _isDirty = true;
        }
    }

    public void Flush()
    {
        EnsureLoaded();

        lock (_sync)
        {
            if (!_isDirty)
                return;

            var document = new CacheDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                Packs = _packsByPath.Values
                    .OrderBy(pack => pack.PackPath, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };

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
                using (var brotliStream = new BrotliStream(
                    fileStream,
                    CompressionLevel.Optimal,
                    leaveOpen: false))
                {
                    JsonSerializer.Serialize(
                        brotliStream,
                        document,
                        GltfAnimationMetadataLookupCacheJsonContext.Default.CacheDocument);
                }

                File.Move(temporaryPath, cachePath, overwrite: true);
                temporaryPath = null;
                _isDirty = false;

                stopwatch.Stop();
                var compressedBytes = new FileInfo(cachePath).Length;
                _logger.Here().Information(
                    "GLTF animation metadata cache SAVED in {ElapsedMs}ms: packs={PackCount}, fragments={FragmentCount}, entries={EntryCount}, compressedBytes={CompressedBytes}",
                    stopwatch.ElapsedMilliseconds,
                    document.Packs.Count,
                    document.Packs.Sum(pack => pack.Fragments?.Count ?? 0),
                    document.Packs.Sum(pack => pack.Fragments?.Sum(fragment => fragment.Entries?.Count ?? 0) ?? 0),
                    compressedBytes);
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
                    "GLTF animation metadata cache not found at '{CachePath}'",
                    cachePath);
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                CacheDocument? document;
                using (var fileStream = new FileStream(
                    cachePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                using (var brotliStream = new BrotliStream(
                    fileStream,
                    CompressionMode.Decompress,
                    leaveOpen: false))
                {
                    document = JsonSerializer.Deserialize(
                        brotliStream,
                        GltfAnimationMetadataLookupCacheJsonContext.Default.CacheDocument);
                }

                if (document == null)
                    throw new InvalidDataException("Cache document was empty.");
                if (document.SchemaVersion != CurrentSchemaVersion)
                {
                    throw new InvalidDataException(
                        $"Schema version {document.SchemaVersion} does not match {CurrentSchemaVersion}.");
                }

                foreach (var pack in document.Packs ?? [])
                {
                    if (string.IsNullOrWhiteSpace(pack.PackPath))
                        continue;
                    _packsByPath[Path.GetFullPath(pack.PackPath)] = pack;
                }

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
                    "GLTF animation metadata cache LOADED in {ElapsedMs}ms with {PackCount} pack entries ({RemovedPackCount} stale entries removed)",
                    stopwatch.ElapsedMilliseconds,
                    _packsByPath.Count,
                    stalePaths.Count);
            }
            catch (Exception exception)
            {
                _packsByPath.Clear();
                _logger.Here().Warning(
                    "Ignoring invalid GLTF animation metadata cache '{CachePath}': {Message}",
                    cachePath,
                    exception.Message);
            }
        }
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

    internal sealed class CacheDocument
    {
        public int SchemaVersion { get; set; }
        public List<CachedPack> Packs { get; set; } = [];
    }

    internal sealed class CachedPack
    {
        public string PackPath { get; set; } = string.Empty;
        public long Length { get; set; }
        public long LastWriteTimeUtcTicks { get; set; }
        public List<CachedFragment> Fragments { get; set; } = [];
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
}
