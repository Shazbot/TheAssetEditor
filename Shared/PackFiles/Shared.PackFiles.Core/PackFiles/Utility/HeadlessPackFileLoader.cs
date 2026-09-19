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
            var cacheFileMaterializeMs = 0.0;
            var cachedContainerSetupMs = 0.0;
            var cachedFileObjectBuildMs = 0.0;
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
                cacheFileMaterializeMs += details.CacheFileMaterializeMs;
                cachedContainerSetupMs += details.CachedContainerSetupMs;
                cachedFileObjectBuildMs += details.CachedFileObjectBuildMs;
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
                "Headless pack cold-start breakdown: totalIncludingCacheFile={TotalIncludingCacheFileMs:F1}ms, cacheFileLoad={CacheFileLoadMs:F1}ms, orderedLoad={OrderedLoadMs:F1}ms, pathSetup={PathSetupMs:F1}ms, cacheLookup={CacheLookupMs:F1}ms (metadataValidation={CacheMetadataValidationMs:F1}ms, fileFilterMaterialize={CacheFileMaterializeMs:F1}ms), cachedContainerSetup={CachedContainerSetupMs:F1}ms, cachedFileObjectBuild={CachedFileObjectBuildMs:F1}ms, diskParse={DiskParseMs:F1}ms, packs={PackCount}, cacheHits={CacheHits}, diskLoads={DiskLoads}, retainedCachedFiles={RetainedCachedFiles}, skippedWemFiles={SkippedWemFiles}",
                totalIncludingCacheFileMs,
                _vanillaCacheFileLoadMs,
                orderedLoadMs,
                pathSetupMs,
                cacheLookupMs,
                cacheMetadataValidationMs,
                cacheFileMaterializeMs,
                cachedContainerSetupMs,
                cachedFileObjectBuildMs,
                diskParseMs,
                packFilePaths.Count,
                cacheHits,
                diskLoads,
                retainedCachedFiles,
                skippedWemFiles);

            Logger.Here().Information(
                "Headless pack slowest loads: {SlowestPacks}",
                string.Join(", ", slowestPacks
                    .OrderByDescending(x => x.TotalMs)
                    .Take(5)
                    .Select(x => $"{Path.GetFileName(x.PackPath)}={x.TotalMs:F1}ms(files={x.RetainedFileCount},wem={x.SkippedWemCount},cache={x.UsedCache})")));

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
            var cachedIndex = _vanillaPackFilesCache?.TryGet(fileInfo);
            cacheLookupStopwatch.Stop();
            if (cachedIndex != null)
            {
                var cachedBuild = CreateContainerFromCachedIndex(fullPath, fileInfo.Length, cachedIndex);
                cachedBuild.Container.IsCaPackFile = isCaPackFile;
                cachedBuild.Container.IsReadOnly = true;
                totalStopwatch.Stop();

                return new PackLoadDetails(
                    fullPath,
                    new HeadlessPackFileLoadResult(cachedBuild.Container, true),
                    UsedCache: true,
                    cachedIndex.PackedFiles.Count,
                    cachedIndex.SkippedWemCount,
                    pathSetupStopwatch.Elapsed.TotalMilliseconds,
                    cacheLookupStopwatch.Elapsed.TotalMilliseconds,
                    cachedIndex.MetadataValidationMs,
                    cachedIndex.FileMaterializeMs,
                    cachedBuild.SetupMs,
                    cachedBuild.FileObjectBuildMs,
                    DiskParseMs: 0,
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
                CacheFileMaterializeMs: 0,
                CachedContainerSetupMs: 0,
                CachedFileObjectBuildMs: 0,
                diskParseStopwatch.Elapsed.TotalMilliseconds,
                totalStopwatch.Elapsed.TotalMilliseconds);
        }

        private static CachedContainerBuildDetails CreateContainerFromCachedIndex(
            string fullPath,
            long fileSize,
            VanillaPackFilesCacheReader.CachedPackIndex cachedIndex)
        {
            var setupStopwatch = Stopwatch.StartNew();
            var header = new PFHeader(
                cachedIndex.Header.Version,
                cachedIndex.Header.ByteMask,
                cachedIndex.Header.ReferenceFileCount)
            {
                Buffer = cachedIndex.Header.Buffer,
                FileCount = (uint)cachedIndex.PackedFiles.Count,
                DataStart = cachedIndex.PackedFiles.Count == 0
                    ? 0
                    : cachedIndex.PackedFiles[0].StartPos
            };
            header.DependantFiles.AddRange(cachedIndex.DependencyPacks);

            var container = PackFileContainer.CreatePackFile(
                Path.GetFileNameWithoutExtension(fullPath),
                fullPath,
                header);
            container.OriginalLoadByteSize = fileSize;
            container.EnsureFileCapacity(cachedIndex.PackedFiles.Count);

            var parent = new PackedFileSourceParent { FilePath = fullPath };
            setupStopwatch.Stop();

            var fileObjectStopwatch = Stopwatch.StartNew();
            foreach (var cachedFile in cachedIndex.PackedFiles)
            {
                var normalizedPath = cachedFile.Name.ToLowerInvariant();
                var source = new PackedFileSource(
                    parent,
                    cachedFile.StartPos,
                    cachedFile.FileSize,
                    header.HasEncryptedData,
                    cachedFile.IsCompressed,
                    CompressionFormat.None,
                    0);
                container.AddOrUpdateFile(
                    normalizedPath,
                    new PackFile(GetFileName(normalizedPath), source));
            }
            fileObjectStopwatch.Stop();

            return new CachedContainerBuildDetails(
                container,
                setupStopwatch.Elapsed.TotalMilliseconds,
                fileObjectStopwatch.Elapsed.TotalMilliseconds);
        }

        private static string GetFileName(string path)
        {
            var separator = path.LastIndexOfAny(['\\', '/']);
            return separator < 0 ? path : path[(separator + 1)..];
        }

        private sealed record CachedContainerBuildDetails(
            PackFileContainer Container,
            double SetupMs,
            double FileObjectBuildMs);

        private sealed record PackLoadDetails(
            string PackPath,
            HeadlessPackFileLoadResult Result,
            bool UsedCache,
            int RetainedFileCount,
            int SkippedWemCount,
            double PathSetupMs,
            double CacheLookupMs,
            double CacheMetadataValidationMs,
            double CacheFileMaterializeMs,
            double CachedContainerSetupMs,
            double CachedFileObjectBuildMs,
            double DiskParseMs,
            double TotalMs);
    }

}
