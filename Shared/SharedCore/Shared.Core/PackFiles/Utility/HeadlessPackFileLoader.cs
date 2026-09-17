using System.Text;
using Shared.Core.Events;
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
        private readonly VanillaPackFilesCacheReader? _vanillaPackFilesCache;

        public HeadlessPackFileLoader(string? vanillaPackFilesCachePath = null)
        {
            if (string.IsNullOrWhiteSpace(vanillaPackFilesCachePath) == false)
                _vanillaPackFilesCache = new VanillaPackFilesCacheReader(vanillaPackFilesCachePath);
        }

        public IPackFileContainer LoadPack(string packFilePath, bool isCaPackFile = false)
            => LoadPackWithMetadata(packFilePath, isCaPackFile).Container;

        public IReadOnlyList<HeadlessPackFileLoadResult> LoadOrderedWithMetadata(
            IReadOnlyList<string> packFilePaths,
            bool firstPackIsCaPack = true)
        {
            ArgumentNullException.ThrowIfNull(packFilePaths);

            var normalizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var containers = new List<HeadlessPackFileLoadResult>(packFilePaths.Count);
            for (var index = 0; index < packFilePaths.Count; index++)
            {
                var fullPath = Path.GetFullPath(packFilePaths[index]);
                if (normalizedPaths.Add(fullPath) == false)
                    throw new InvalidOperationException($"Pack file '{fullPath}' was supplied more than once.");

                containers.Add(LoadPackWithMetadata(fullPath, firstPackIsCaPack && index == 0));
            }

            return containers;
        }

        public IReadOnlyList<IPackFileContainer> LoadOrdered(
            IReadOnlyList<string> packFilePaths,
            bool firstPackIsCaPack = true)
            => LoadOrderedWithMetadata(packFilePaths, firstPackIsCaPack)
                .Select(x => x.Container)
                .ToList();

        private HeadlessPackFileLoadResult LoadPackWithMetadata(string packFilePath, bool isCaPackFile)
        {
            if (string.IsNullOrWhiteSpace(packFilePath))
                throw new ArgumentException("A pack file path is required.", nameof(packFilePath));
            if (File.Exists(packFilePath) == false)
                throw new FileNotFoundException($"Pack file '{packFilePath}' was not found.", packFilePath);

            var fullPath = Path.GetFullPath(packFilePath);
            var fileInfo = new FileInfo(fullPath);
            var cachedIndex = _vanillaPackFilesCache?.TryGet(fileInfo);
            if (cachedIndex != null)
            {
                var cachedContainer = CreateContainerFromCachedIndex(fullPath, fileInfo.Length, cachedIndex);
                cachedContainer.IsCaPackFile = isCaPackFile;
                cachedContainer.IsReadOnly = true;
                return new HeadlessPackFileLoadResult(cachedContainer, true);
            }

            using var fileStream = File.OpenRead(fullPath);
            using var reader = new BinaryReader(fileStream, Encoding.ASCII);
            var container = PackFileSerializerLoader.Load(
                fullPath,
                fileStream.Length,
                reader,
                new CaPackDuplicateFileResolver());

            // Set these flags before returning. Do not call SaveSettings: a
            // read-only host load must not write editor metadata beside packs.
            container.IsCaPackFile = isCaPackFile;
            container.IsReadOnly = true;

            // CA's pack header identifies user-created packs as MOD. The
            // remaining known CA header types are the game's own packs. Keep
            // this classification here, next to header parsing, so callers do
            // not have to guess from paths or filenames.
            var isVanillaPack = container.Header.PackFileType is
                PackFileCAType.BOOT or
                PackFileCAType.RELEASE or
                PackFileCAType.PATCH or
                PackFileCAType.MOVIE;
            return new HeadlessPackFileLoadResult(container, isVanillaPack);
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
            container.FileList = new Dictionary<string, PackFile>(cachedIndex.PackedFiles.Count);

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
    }

    /// <summary>
    /// Creates the internal PackFileService with all interactive safeguards
    /// disabled. The service still preserves its normal lookup rule: the last
    /// added container wins.
    /// </summary>
    public static class HeadlessPackFileServiceFactory
    {
        public static IPackFileService Create(IGlobalEventHub? globalEventHub = null)
        {
            var service = new PackFileService(globalEventHub)
            {
                EnforceGameFilesMustBeLoaded = false,
                MessageBoxProvider = new NoOpSimpleMessageBox()
            };
            return service;
        }

        private sealed class NoOpSimpleMessageBox : ISimpleMessageBox
        {
            public void ShowDialogBox(string message, string title)
            {
                // Headless callers receive load failures through exceptions;
                // this guard prevents an unexpected UI if a future service
                // path attempts to display a duplicate/rejection message.
            }
        }
    }
}
