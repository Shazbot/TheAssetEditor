using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;
using Shared.Core.PackFiles.Models;
using GameWorld.Core.Serialization;

namespace GameWorld.Core.Services;

/// <summary>
/// Enables the headless host to persist animation-header results for a known
/// set of vanilla pack containers. Mod containers are intentionally supplied
/// by the host only as a lookup set, so they can never be written to this
/// cache accidentally.
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
    private const int CurrentSchemaVersion = 1;

    private readonly ILogger _logger = Logging.Create<SkeletonAnimationLookupCache>();
    private readonly SkeletonAnimationLookupCacheOptions _options;

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
            _logger.Here().Information(
                "Skeleton animation cache SKIP for [{ContainerName}]: container is not cacheable",
                container.Name);
            return null;
        }

        if (!TryGetPackStamp(container, out var packStamp))
        {
            _logger.Here().Information(
                "Skeleton animation cache SKIP for [{ContainerName}]: pack metadata is unavailable",
                container.Name);
            return null;
        }

        var cachePath = GetCacheFilePath(packStamp.PackPath);
        if (!File.Exists(cachePath))
        {
            LogMiss(container, "cache file not found");
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var document = JsonSerializer.Deserialize<CacheDocument>(
                File.ReadAllText(cachePath, Encoding.UTF8),
                SkeletonAnimationLookupCacheJsonContext.Default.CacheDocument);
            if (document == null)
            {
                LogMiss(container, "cache document was empty");
                return null;
            }

            if (document.SchemaVersion != CurrentSchemaVersion)
            {
                LogMiss(
                    container,
                    $"schema version {document.SchemaVersion} does not match {CurrentSchemaVersion}");
                return null;
            }

            if (!string.Equals(document.PackPath, packStamp.PackPath, StringComparison.OrdinalIgnoreCase))
            {
                LogMiss(container, "pack path changed");
                return null;
            }

            if (document.Length != packStamp.Length)
            {
                LogMiss(container, $"pack size changed ({document.Length} -> {packStamp.Length})");
                return null;
            }

            if (document.LastWriteTimeUtcTicks != packStamp.LastWriteTimeUtcTicks)
            {
                LogMiss(container, "pack last-write time changed");
                return null;
            }

            var animationsBySkeletonName = new Dictionary<string, List<AnimationReference>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var animation in document.Animations ?? [])
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

            var skeletonFileNames = (document.SkeletonFileNames ?? [])
                .Where(path => string.IsNullOrWhiteSpace(path) == false)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            stopwatch.Stop();
            _logger.Here().Information(
                "Skeleton animation cache HIT for [{ContainerName}] in {ElapsedMs}ms with {AnimationCount} animation refs and {SkeletonCount} skeleton files",
                container.Name,
                stopwatch.ElapsedMilliseconds,
                animationsBySkeletonName.Values.Sum(x => x.Count),
                skeletonFileNames.Count);

            return (skeletonFileNames, animationsBySkeletonName);
        }
        catch (Exception exception)
        {
            _logger.Here().Warning(
                "Skeleton animation cache MISS for [{ContainerName}] from '{CachePath}': invalid cache: {Message}",
                container.Name,
                cachePath,
                exception.Message);
            return null;
        }
    }

    public void Save(
        IPackFileContainer container,
        (List<string> SkeletonFileNames,
            Dictionary<string, List<AnimationReference>> AnimationsBySkeletonName) discovered)
    {
        if (!_options.CacheableContainers.Contains(container)
            || !TryGetPackStamp(container, out var packStamp))
            return;

        var cachePath = GetCacheFilePath(packStamp.PackPath);
        var document = new CacheDocument
        {
            SchemaVersion = CurrentSchemaVersion,
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

        string? temporaryPath = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(_options.CacheDirectory);
            temporaryPath = string.Concat(cachePath, ".", Guid.NewGuid().ToString("N"), ".tmp");
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(
                    document,
                    SkeletonAnimationLookupCacheJsonContext.Default.CacheDocument),
                Encoding.UTF8);
            File.Move(temporaryPath, cachePath, overwrite: true);
            temporaryPath = null;

            stopwatch.Stop();
            _logger.Here().Information(
                "Skeleton animation cache SAVED for [{ContainerName}] in {ElapsedMs}ms with {AnimationCount} animation refs and {SkeletonCount} skeleton files",
                container.Name,
                stopwatch.ElapsedMilliseconds,
                document.Animations.Count,
                document.SkeletonFileNames.Count);
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

    private void LogMiss(IPackFileContainer container, string reason)
    {
        _logger.Here().Information(
            "Skeleton animation cache MISS for [{ContainerName}]: {Reason}",
            container.Name,
            reason);
    }

    private string GetCacheFilePath(string packPath)
    {
        var pathHash = SHA256.HashData(Encoding.UTF8.GetBytes(packPath.ToUpperInvariant()));
        return Path.Combine(_options.CacheDirectory, $"{Convert.ToHexString(pathHash)}.json");
    }

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
