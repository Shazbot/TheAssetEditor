using System.Text;
using Shared.Core.Events;
using Shared.Core.PackFiles.Models;
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
    }

    public sealed class HeadlessPackFileLoader : IHeadlessPackFileLoader
    {
        public IPackFileContainer LoadPack(string packFilePath, bool isCaPackFile = false)
        {
            if (string.IsNullOrWhiteSpace(packFilePath))
                throw new ArgumentException("A pack file path is required.", nameof(packFilePath));
            if (File.Exists(packFilePath) == false)
                throw new FileNotFoundException($"Pack file '{packFilePath}' was not found.", packFilePath);

            var fullPath = Path.GetFullPath(packFilePath);
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
            return container;
        }

        public IReadOnlyList<IPackFileContainer> LoadOrdered(
            IReadOnlyList<string> packFilePaths,
            bool firstPackIsCaPack = true)
        {
            ArgumentNullException.ThrowIfNull(packFilePaths);

            var normalizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var containers = new List<IPackFileContainer>(packFilePaths.Count);
            for (var index = 0; index < packFilePaths.Count; index++)
            {
                var fullPath = Path.GetFullPath(packFilePaths[index]);
                if (normalizedPaths.Add(fullPath) == false)
                    throw new InvalidOperationException($"Pack file '{fullPath}' was supplied more than once.");

                containers.Add(LoadPack(fullPath, firstPackIsCaPack && index == 0));
            }

            return containers;
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
