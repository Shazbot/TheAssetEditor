using System.Diagnostics;
using System.Text;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Models.Containers;
using Shared.Core.PackFiles.Models.FileSources;
using Shared.Core.PackFiles.Serialization;

namespace Shared.Core.PackFiles.Utility
{
    /// <summary>
    /// Loads read-only pack containers for service/CLI callers. Unlike
    /// PackFileContainerLoader this class does not consult editor settings,
    /// caches, wait cursors, or dialog services.
    /// </summary>
    public interface IHeadlessPackFileLoader
    {
        IPackFileContainer LoadPack(string packFilePath, bool isCaPackFile = false);

        IReadOnlyList<IPackFileContainer> LoadOrdered(
            IReadOnlyList<string> packFilePaths,
            bool firstPackIsCaPack = true);

        IReadOnlyList<HeadlessPackFileLoadResult> LoadOrderedWithMetadata(
            IReadOnlyList<string> packFilePaths,
            bool firstPackIsCaPack = true);
    }

    public sealed record HeadlessPackFileLoadResult(
        IPackFileContainer Container,
        bool IsVanillaPack);

    public sealed class HeadlessPackFileLoader : IHeadlessPackFileLoader
    {
        private static readonly ILogger Logger = Logging.Create<HeadlessPackFileLoader>();
        private readonly VanillaPackFilesCacheReader? _vanillaPackFilesCache;
        private readonly double _vanillaCacheFileLoadMs;

        public HeadlessPackFileLoader(string? vanillaPackFilesCachePath = null)
        {
            if (string.IsNullOrWhiteSpace(vanillaPackFilesCachePath) == false)
            {
                _vanillaPackFilesCache = new VanillaPackFilesCacheReader(vanillaPackFilesCachePath);
                _vanillaCacheFileLoadMs = _vanillaPackFilesCache.LoadElapsedMilliseconds;
            }
        }

        public IPackFileContainer LoadPack(string packFilePath, bool isCaPackFile = false)
            => LoadPackWithDetails(packFilePath, isCaPackFile).Result.Container;

        public IReadOnlyList<HeadlessPackFileLoadResult> LoadOrderedWithMetadata(
            IReadOnlyList<string> packFilePaths,
            bool firstPackIsCaPack = true)
        {
            ArgumentNullException.ThrowIfNull(packFilePaths);

            var totalStopwatch = Stopwatch.StartNew();
            var normalizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var containers = new List<HeadlessPackFileLoadResult>(packFilePaths.Count);
            var cacheHits = 0;
            var diskLoads = 0;
            var retainedCachedFiles = 0;
            var skippedWemFiles = 0;
            var pathSetupMs = 0.0;
            var cacheLookupMs = 0.0;
            var cacheMetadataValidationMs = 0.0;
            var cacheFileIndexBuildMs = 0.0;
            var cachedContainerSetupMs = 0.0;
            var allWemFastPathPacks = 0;
            var diskParseMs = 0.0;
            var slowestPacks = new List<PackLoadDetails>();

            for (var index = 0; index < packFilePaths.Count; index++)
            {
                var fullPath = Path.GetFullPath(packFilePaths[index]);
                if (normalizedPaths.Add(fullPath) == false)
                    throw new InvalidOperationException($"Pack file '{fullPath}' was supplied more than once.");

                var details = LoadPackWithDetails(fullPath, firstPackIsCaPack && index == 0);
                containers.Add(details.Result);
                pathSetupMs += details.PathSetupMs;
                cacheLookupMs += details.CacheLookupMs;
                cacheMetadataValidationMs += details.CacheMetadataValidationMs;
                cacheFileIndexBuildMs += details.CacheFileIndexBuildMs;
                cachedContainerSetupMs += details.CachedContainerSetupMs;
                if (details.UsedAllWemFastPath)
                    allWemFastPathPacks++;
                diskParseMs += details.DiskParseMs;
                skippedWemFiles += details.SkippedWemCount;
                slowestPacks.Add(details);

                if (details.UsedCache)
                {
                    cacheHits++;
                    retainedCachedFiles += details.RetainedFileCount;
                }
                else
                {
                    diskLoads++;
                }
            }

            totalStopwatch.Stop();
            var orderedLoadMs = totalStopwatch.Elapsed.TotalMilliseconds;
            var totalIncludingCacheFileMs = orderedLoadMs + _vanillaCacheFileLoadMs;
            Logger.Here().Information(
                "Headless pack cold-start breakdown: totalIncludingCacheFile={TotalIncludingCacheFileMs:F1}ms, cacheFileLoad={CacheFileLoadMs:F1}ms, orderedLoad={OrderedLoadMs:F1}ms, pathSetup={PathSetupMs:F1}ms, cacheReadBuild={CacheLookupMs:F1}ms (metadataValidation={CacheMetadataValidationMs:F1}ms, containerSetup={CachedContainerSetupMs:F1}ms, lazyFileIndexBuild={CacheFileIndexBuildMs:F1}ms), diskParse={DiskParseMs:F1}ms, packs={PackCount}, cacheHits={CacheHits}, diskLoads={DiskLoads}, retainedCachedFiles={RetainedCachedFiles}, materializedCachedFilesAtStartup=0, skippedWemFiles={SkippedWemFiles}, allWemFastPathPacks={AllWemFastPathPacks}",
                totalIncludingCacheFileMs,
                _vanillaCacheFileLoadMs,
                orderedLoadMs,
                pathSetupMs,
                cacheLookupMs,
                cacheMetadataValidationMs,
                cachedContainerSetupMs,
                cacheFileIndexBuildMs,
                diskParseMs,
                packFilePaths.Count,
                cacheHits,
                diskLoads,
                retainedCachedFiles,
                skippedWemFiles,
                allWemFastPathPacks);

            Logger.Here().Debug(
                "Headless pack slowest loads: {SlowestPacks}",
                string.Join(", ", slowestPacks
                    .OrderByDescending(x => x.TotalMs)
                    .Take(5)
                    .Select(x => $"{Path.GetFileName(x.PackPath)}={x.TotalMs:F1}ms(files={x.RetainedFileCount},wem={x.SkippedWemCount},cache={x.UsedCache},wemFast={x.UsedAllWemFastPath})")));

            return containers;
        }

        public IReadOnlyList<IPackFileContainer> LoadOrdered(
            IReadOnlyList<string> packFilePaths,
            bool firstPackIsCaPack = true)
            => LoadOrderedWithMetadata(packFilePaths, firstPackIsCaPack)
                .Select(x => x.Container)
                .ToList();

        private PackLoadDetails LoadPackWithDetails(string packFilePath, bool isCaPackFile)
        {
            var totalStopwatch = Stopwatch.StartNew();
            var pathSetupStopwatch = Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(packFilePath))
                throw new ArgumentException("A pack file path is required.", nameof(packFilePath));
            if (File.Exists(packFilePath) == false)
                throw new FileNotFoundException($"Pack file '{packFilePath}' was not found.", packFilePath);

            var fullPath = Path.GetFullPath(packFilePath);
            var fileInfo = new FileInfo(fullPath);
            pathSetupStopwatch.Stop();

            var cacheLookupStopwatch = Stopwatch.StartNew();
            var cachedBuild = _vanillaPackFilesCache?.TryBuildContainer(fileInfo);
            cacheLookupStopwatch.Stop();
            if (cachedBuild != null)
            {
                cachedBuild.Container.IsCaPackFile = isCaPackFile;
                cachedBuild.Container.IsReadOnly = true;
                totalStopwatch.Stop();

                return new PackLoadDetails(
                    fullPath,
                    new HeadlessPackFileLoadResult(cachedBuild.Container, true),
                    UsedCache: true,
                    cachedBuild.RetainedFileCount,
                    cachedBuild.SkippedWemCount,
                    pathSetupStopwatch.Elapsed.TotalMilliseconds,
                    cacheLookupStopwatch.Elapsed.TotalMilliseconds,
                    cachedBuild.MetadataValidationMs,
                    cachedBuild.FileIndexBuildMs,
                    cachedBuild.ContainerSetupMs,
                    DiskParseMs: 0,
                    cachedBuild.UsedAllWemFastPath,
                    totalStopwatch.Elapsed.TotalMilliseconds);
            }

            var skippedWemCount = 0;
            var diskParseStopwatch = Stopwatch.StartNew();
            using var fileStream = File.OpenRead(fullPath);
            using var reader = new BinaryReader(fileStream, Encoding.ASCII);
            var container = PackFileSerializerLoader.Load(
                fullPath,
                fileStream.Length,
                reader,
                new CaPackDuplicateFileResolver(),
                path =>
                {
                    if (!VanillaPackFilesCacheReader.ShouldIgnoreFile(path))
                        return true;

                    skippedWemCount++;
                    return false;
                });
            diskParseStopwatch.Stop();

            container.Header.FileCount = (uint)container.GetFileCount();

            container.IsCaPackFile = isCaPackFile;
            container.IsReadOnly = true;

            var isVanillaPack = container.Header.PackFileType is
                PackFileCAType.BOOT or
                PackFileCAType.RELEASE or
                PackFileCAType.PATCH or
                PackFileCAType.MOVIE;
            totalStopwatch.Stop();
            return new PackLoadDetails(
                fullPath,
                new HeadlessPackFileLoadResult(container, isVanillaPack),
                UsedCache: false,
                container.GetFileCount(),
                skippedWemCount,
                pathSetupStopwatch.Elapsed.TotalMilliseconds,
                cacheLookupStopwatch.Elapsed.TotalMilliseconds,
                CacheMetadataValidationMs: 0,
                CacheFileIndexBuildMs: 0,
                CachedContainerSetupMs: 0,
                diskParseStopwatch.Elapsed.TotalMilliseconds,
                UsedAllWemFastPath: false,
                totalStopwatch.Elapsed.TotalMilliseconds);
        }

        private sealed record PackLoadDetails(
            string PackPath,
            HeadlessPackFileLoadResult Result,
            bool UsedCache,
            int RetainedFileCount,
            int SkippedWemCount,
            double PathSetupMs,
            double CacheLookupMs,
            double CacheMetadataValidationMs,
            double CacheFileIndexBuildMs,
            double CachedContainerSetupMs,
            double DiskParseMs,
            bool UsedAllWemFastPath,
            double TotalMs);
    }

}
