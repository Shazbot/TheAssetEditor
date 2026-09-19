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

        public HeadlessPackFileLoader(string? vanillaPackFilesCachePath = null)
        {
            if (string.IsNullOrWhiteSpace(vanillaPackFilesCachePath) == false)
                _vanillaPackFilesCache = new VanillaPackFilesCacheReader(vanillaPackFilesCachePath);
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
            var cacheLookupMs = 0.0;
            var cachedContainerBuildMs = 0.0;
            var diskParseMs = 0.0;

            for (var index = 0; index < packFilePaths.Count; index++)
            {
                var fullPath = Path.GetFullPath(packFilePaths[index]);
                if (normalizedPaths.Add(fullPath) == false)
                    throw new InvalidOperationException($"Pack file '{fullPath}' was supplied more than once.");

                var details = LoadPackWithDetails(fullPath, firstPackIsCaPack && index == 0);
                containers.Add(details.Result);
                cacheLookupMs += details.CacheLookupMs;
                cachedContainerBuildMs += details.CachedContainerBuildMs;
                diskParseMs += details.DiskParseMs;
                skippedWemFiles += details.SkippedWemCount;

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
            Logger.Here().Information(
                "Headless pack load completed in {TotalMs:F1}ms for {PackCount} packs: cacheHits={CacheHits}, diskLoads={DiskLoads}, cacheLookup={CacheLookupMs:F1}ms, cachedContainerBuild={CachedContainerBuildMs:F1}ms, diskParse={DiskParseMs:F1}ms, retainedCachedFiles={RetainedCachedFiles}, skippedWemFiles={SkippedWemFiles}",
                totalStopwatch.Elapsed.TotalMilliseconds,
                packFilePaths.Count,
                cacheHits,
                diskLoads,
                cacheLookupMs,
                cachedContainerBuildMs,
                diskParseMs,
                retainedCachedFiles,
                skippedWemFiles);

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
            if (string.IsNullOrWhiteSpace(packFilePath))
                throw new ArgumentException("A pack file path is required.", nameof(packFilePath));
            if (File.Exists(packFilePath) == false)
                throw new FileNotFoundException($"Pack file '{packFilePath}' was not found.", packFilePath);

            var fullPath = Path.GetFullPath(packFilePath);
            var fileInfo = new FileInfo(fullPath);

            var cacheLookupStopwatch = Stopwatch.StartNew();
            var cachedIndex = _vanillaPackFilesCache?.TryGet(fileInfo);
            cacheLookupStopwatch.Stop();
            if (cachedIndex != null)
            {
                var containerBuildStopwatch = Stopwatch.StartNew();
                var cachedContainer = CreateContainerFromCachedIndex(fullPath, fileInfo.Length, cachedIndex);
                containerBuildStopwatch.Stop();

                cachedContainer.IsCaPackFile = isCaPackFile;
                cachedContainer.IsReadOnly = true;
                return new PackLoadDetails(
                    new HeadlessPackFileLoadResult(cachedContainer, true),
                    UsedCache: true,
                    cachedIndex.PackedFiles.Count,
                    cachedIndex.SkippedWemCount,
                    cacheLookupStopwatch.Elapsed.TotalMilliseconds,
                    containerBuildStopwatch.Elapsed.TotalMilliseconds,
                    DiskParseMs: 0);
            }

            var diskParseStopwatch = Stopwatch.StartNew();
            using var fileStream = File.OpenRead(fullPath);
            using var reader = new BinaryReader(fileStream, Encoding.ASCII);
            var container = PackFileSerializerLoader.Load(
                fullPath,
                fileStream.Length,
                reader,
                new CaPackDuplicateFileResolver(),
                static path => !VanillaPackFilesCacheReader.ShouldIgnoreFile(path));
            diskParseStopwatch.Stop();

            var skippedWemCount = checked((int)container.Header.FileCount - container.GetFileCount());
            container.Header.FileCount = (uint)container.GetFileCount();

            container.IsCaPackFile = isCaPackFile;
            container.IsReadOnly = true;

            var isVanillaPack = container.Header.PackFileType is
                PackFileCAType.BOOT or
                PackFileCAType.RELEASE or
                PackFileCAType.PATCH or
                PackFileCAType.MOVIE;
            return new PackLoadDetails(
                new HeadlessPackFileLoadResult(container, isVanillaPack),
                UsedCache: false,
                container.GetFileCount(),
                skippedWemCount,
                cacheLookupStopwatch.Elapsed.TotalMilliseconds,
                CachedContainerBuildMs: 0,
                diskParseStopwatch.Elapsed.TotalMilliseconds);
        }

        private static PackFileContainer CreateContainerFromCachedIndex(
            string fullPath,
            long fileSize,
            VanillaPackFilesCacheReader.CachedPackIndex cachedIndex)
        {
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

            return container;
        }

        private static string GetFileName(string path)
        {
            var separator = path.LastIndexOfAny(['\\', '/']);
            return separator < 0 ? path : path[(separator + 1)..];
        }

        private sealed record PackLoadDetails(
            HeadlessPackFileLoadResult Result,
            bool UsedCache,
            int RetainedFileCount,
            int SkippedWemCount,
            double CacheLookupMs,
            double CachedContainerBuildMs,
            double DiskParseMs);
    }

}
