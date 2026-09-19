using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Serilog;
using Shared.Core.PackFiles.Models;
using GameWorld.Core.Serialization;

namespace GameWorld.Core.Services;

/// <summary>
/// Enables the headless host to persist animation-header results for a known
/// set of vanilla pack containers. The on-disk cache is one Brotli-compressed
/// document, while validation and invalidation remain per pack.
/// </summary>
public sealed class SkeletonAnimationLookupCacheOptions
{
    public SkeletonAnimationLookupCacheOptions(
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

internal sealed class SkeletonAnimationLookupCache
{
    private const int CurrentSchemaVersion = 2;
    private const string CacheFileName = "animation-index.br";

    private readonly ILogger _logger = Logging.Create<SkeletonAnimationLookupCache>();
    private readonly SkeletonAnimationLookupCacheOptions _options;
    private readonly object _sync = new();
    private readonly Dictionary<string, CachedPack> _packsByPath = new(StringComparer.OrdinalIgnoreCase);

    private bool _isLoaded;
    private bool _isDirty;

    public SkeletonAnimationLookupCache(SkeletonAnimationLookupCacheOptions options)
    {
        _options = options;
    }

    public (
        List<string> SkeletonFileNames,
        Dictionary<string, List<AnimationReference>> AnimationsBySkeletonName)? TryLoad(
        IPackFileContainer container)
    {
        if (!_options.CacheableContainers.Contains(container))
        {
            _logger.Here().Debug(
                "Skeleton animation cache SKIP for [{ContainerName}]: container is not cacheable",
                container.Name);
            return null;
        }

        if (!TryGetPackStamp(container, out var packStamp))
        {
            _logger.Here().Debug(
                "Skeleton animation cache SKIP for [{ContainerName}]: pack metadata is unavailable",
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

        var animationsBySkeletonName = new Dictionary<string, List<AnimationReference>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var animation in cachedPack.Animations ?? [])
        {
            if (string.IsNullOrWhiteSpace(animation.SkeletonName)
                || string.IsNullOrWhiteSpace(animation.AnimationPath))
                continue;

            if (!animationsBySkeletonName.TryGetValue(animation.SkeletonName, out var animations))
            {
                animations = [];
                animationsBySkeletonName[animation.SkeletonName] = animations;
            }

            animations.Add(new AnimationReference(animation.AnimationPath, container));
        }

        var skeletonFileNames = (cachedPack.SkeletonFileNames ?? [])
            .Where(path => string.IsNullOrWhiteSpace(path) == false)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        stopwatch.Stop();
        _logger.Here().Debug(
            "Skeleton animation cache HIT for [{ContainerName}] in {ElapsedMs}ms with {AnimationCount} animation refs and {SkeletonCount} skeleton files",
            container.Name,
            stopwatch.ElapsedMilliseconds,
            animationsBySkeletonName.Values.Sum(x => x.Count),
            skeletonFileNames.Count);

        return (skeletonFileNames, animationsBySkeletonName);
    }

    public void Save(
        IPackFileContainer container,
        (List<string> SkeletonFileNames,
            Dictionary<string, List<AnimationReference>> AnimationsBySkeletonName) discovered)
    {
        if (!_options.CacheableContainers.Contains(container)
            || !TryGetPackStamp(container, out var packStamp))
            return;

        EnsureLoaded();

        var cachedPack = new CachedPack
        {
            PackPath = packStamp.PackPath,
            Length = packStamp.Length,
            LastWriteTimeUtcTicks = packStamp.LastWriteTimeUtcTicks,
            SkeletonFileNames = discovered.SkeletonFileNames
                .Where(path => string.IsNullOrWhiteSpace(path) == false)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Animations = discovered.AnimationsBySkeletonName
                .SelectMany(pair => pair.Value.Select(animation => new CachedAnimation
                {
                    SkeletonName = pair.Key,
                    AnimationPath = animation.AnimationFile
                }))
                .Where(animation => string.IsNullOrWhiteSpace(animation.SkeletonName) == false
                    && string.IsNullOrWhiteSpace(animation.AnimationPath) == false)
                .OrderBy(animation => animation.SkeletonName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(animation => animation.AnimationPath, StringComparer.OrdinalIgnoreCase)
                .ToList()
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
                        SkeletonAnimationLookupCacheJsonContext.Default.CacheDocument);
                }

                File.Move(temporaryPath, cachePath, overwrite: true);
                temporaryPath = null;
                _isDirty = false;

                stopwatch.Stop();
                var compressedBytes = new FileInfo(cachePath).Length;
                _logger.Here().Debug(
                    "Skeleton animation cache SAVED combined index in {ElapsedMs}ms: {PackCount} packs, {AnimationCount} animation refs, {SkeletonCount} skeleton files, {CompressedBytes} bytes compressed",
                    stopwatch.ElapsedMilliseconds,
                    document.Packs.Count,
                    document.Packs.Sum(pack => pack.Animations?.Count ?? 0),
                    document.Packs.Sum(pack => pack.SkeletonFileNames?.Count ?? 0),
                    compressedBytes);
            }
            catch (Exception exception)
            {
                _logger.Here().Warning(
                    "Unable to save skeleton animation lookup cache '{CachePath}': {Message}",
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
                        // Cache writes are best effort; leave the lookup usable.
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
                    "Skeleton animation combined cache not found at '{CachePath}'",
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
                        SkeletonAnimationLookupCacheJsonContext.Default.CacheDocument);
                }

                if (document == null)
                    throw new InvalidDataException("Cache document was empty.");
                if (document.SchemaVersion != CurrentSchemaVersion)
                    throw new InvalidDataException(
                        $"Schema version {document.SchemaVersion} does not match {CurrentSchemaVersion}.");

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
                _logger.Here().Debug(
                    "Skeleton animation combined cache LOADED in {ElapsedMs}ms with {PackCount} pack entries ({RemovedPackCount} stale entries removed)",
                    stopwatch.ElapsedMilliseconds,
                    _packsByPath.Count,
                    stalePaths.Count);
            }
            catch (Exception exception)
            {
                _packsByPath.Clear();
                _logger.Here().Warning(
                    "Ignoring invalid skeleton animation combined cache '{CachePath}': {Message}",
                    cachePath,
                    exception.Message);
            }
        }
    }

    private void LogMiss(IPackFileContainer container, string reason)
    {
        _logger.Here().Debug(
            "Skeleton animation cache MISS for [{ContainerName}]: {Reason}",
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
        public List<string> SkeletonFileNames { get; set; } = [];
        public List<CachedAnimation> Animations { get; set; } = [];
    }

    internal sealed class CachedAnimation
    {
        public string SkeletonName { get; set; } = string.Empty;
        public string AnimationPath { get; set; } = string.Empty;
    }
}
