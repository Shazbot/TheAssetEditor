using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Models.Containers;
using Shared.Core.Settings;

namespace Editors.PackOptimizer.Core
{
    /// <summary>
    /// What the optimizer does to a pack. Every flag defaults to the value the
    /// batch service has always used when a caller does not say otherwise.
    /// </summary>
    public sealed record PackOptimizerOptions
    {
        public bool MergeCompatibleMeshes { get; init; }
        public bool ShareAtlasesAcrossVmds { get; init; } = true;
        public bool OptimizeGeometry { get; init; }
        public bool AtlasAllVmds { get; init; }
        public bool ScoreAllGameUnits { get; init; }
        public bool LodAwareAtlasPlanning { get; init; } = true;
        public bool CrossRigidOnly { get; init; }
        public bool CrossRigidMerges { get; init; }
        public bool BuildAtlases { get; init; } = true;
        public bool DownscaleTextures { get; init; }
        public bool SharedTextureRepack { get; init; }
        public bool CompactTextures { get; init; }
        public bool DropUnreadAlpha { get; init; }
    }

    /// <summary>
    /// What to do with meshes that reference textures which cannot be resolved.
    /// The optimizer no longer atlases such meshes (UV0 is shared), so both
    /// values currently skip them; the policy is kept so callers state their
    /// intent explicitly and the behavior can be reintroduced.
    /// </summary>
    public enum MissingTexturePolicy
    {
        Skip,
        AtlasWithoutMissingTextures,
    }

    /// <summary>
    /// Everything a run needs, passed explicitly. Nothing is looked up globally.
    /// </summary>
    /// <param name="SourcePackPath">Path of the pack being optimized; used for naming and the report.</param>
    /// <param name="SourceContainer">The already loaded (read-only) source pack.</param>
    /// <param name="ConsumerContainers">Read-only packs the source may reference (vanilla and other loaded packs), in precedence order, later wins.</param>
    /// <param name="OutputPath">Where the optimized pack is saved.</param>
    /// <param name="ReportPath">Where the report is written.</param>
    /// <param name="Game">The game the pack is for.</param>
    /// <param name="CacheDirectory">Directory for the vanilla consumer cache; null disables the cache.</param>
    public sealed record PackOptimizerRequest(
        string SourcePackPath,
        IPackFileContainer SourceContainer,
        IReadOnlyList<IPackFileContainer> ConsumerContainers,
        string OutputPath,
        string ReportPath,
        GameTypeEnum Game,
        PackOptimizerOptions Options,
        MissingTexturePolicy MissingTexturePolicy = MissingTexturePolicy.Skip,
        string? CacheDirectory = null);

    public sealed record PackOptimizerProgress(
        string Stage,
        string? Item,
        int Current,
        int Total);

    public enum PackOptimizerStatus
    {
        Succeeded,
        Cancelled,
    }

    public sealed record PackOptimizerResult(
        PackOptimizerStatus Status,
        string ReportPath,
        string OutputPath,
        int VmdCount,
        int AtlasedMeshCount,
        int GeneratedTextureCount,
        int RemovedFileCount,
        int SkippedMeshCount);

    /// <summary>
    /// Entry point of the library. Failures propagate as exceptions; the output
    /// pack is a standalone container that is saved directly and never
    /// registered with any pack file service.
    /// </summary>
    public static class PackOptimizer
    {
        public static PackOptimizerResult Run(
            PackOptimizerRequest request,
            IProgress<PackOptimizerProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var packFiles = new PackOptimizerPackFiles(request.ConsumerContainers);
            var service = new PackTextureAtlasBatchService(packFiles, request.CacheDirectory);
            // Report synchronously from the worker thread so callers see updates in order;
            // marshalling to a UI thread is the caller's concern.
            IProgress<PackTextureAtlasBatchService.TextureAtlasPackProgress>? inner = progress == null
                ? null
                : new DirectProgress<PackTextureAtlasBatchService.TextureAtlasPackProgress>(
                    value => progress.Report(new PackOptimizerProgress(
                        value.Phase, value.Item, value.Current, value.Total)));

            try
            {
                var result = service.Process(request, inner, cancellationToken);
                return new PackOptimizerResult(
                    PackOptimizerStatus.Succeeded,
                    result.ReportPath,
                    request.OutputPath,
                    result.VmdCount,
                    result.AtlasedMeshCount,
                    result.GeneratedTextureCount,
                    result.RemovedFileCount,
                    result.SkippedMeshCount);
            }
            catch (OperationCanceledException)
            {
                return new PackOptimizerResult(
                    PackOptimizerStatus.Cancelled,
                    request.ReportPath,
                    request.OutputPath,
                    0, 0, 0, 0, 0);
            }
        }

        public static string BuildOutputPath(string sourcePath)
            => PackTextureAtlasBatchService.BuildOutputPath(sourcePath);

        public static string BuildReportPath(string outputPath)
            => PackTextureAtlasBatchService.BuildReportPath(outputPath);

        private sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
        {
            public void Report(T value) => report(value);
        }
    }

    /// <summary>
    /// The pack operations the optimizer needs: ordered read-only lookups plus
    /// the handful of mutations on its own output container. Implemented by the
    /// library for real runs; tests substitute it.
    /// </summary>
    public interface IPackOptimizerPackFiles : IHeadlessPackFileService
    {
        IPackFileContainer CreateOutputContainer(string name);
        List<PackFile> AddFilesToPack(IPackFileContainer container, List<NewPackFileEntry> newFiles);
        void DeleteFiles(IPackFileContainer container, IReadOnlyCollection<string> paths);
        void SavePackContainer(IPackFileContainer container, string path, GameInformation gameInformation);
        void ReleaseOutputContainer(IPackFileContainer container);
    }

    internal sealed class PackOptimizerPackFiles : IPackOptimizerPackFiles
    {
        private readonly PackFileRepository _lookup;

        public PackOptimizerPackFiles(IEnumerable<IPackFileContainer> consumers)
        {
            _lookup = new PackFileRepository(consumers);
        }

        public IReadOnlyList<IPackFileContainer> Packs => _lookup.Packs;

        public List<IPackFileContainer> GetAllPackfileContainers() => _lookup.Packs.ToList();

        public PackFile? FindFile(string path, IPackFileContainer? container = null)
            => _lookup.FindFile(path, container);

        public string GetFullPath(PackFile file, IPackFileContainer? container = null)
            => _lookup.TryGetFullPath(file, container, out var path)
                ? path!
                : throw new InvalidOperationException($"Unknown path for {file.Name}");

        public IPackFileContainer? GetPackFileContainer(PackFile file)
            => _lookup.GetPackFileContainer(file);

        public List<(string FileName, PackFile Pack)> FindAllWithExtention(
            string extention,
            IPackFileContainer? container = null)
            => _lookup.FindAllWithExtention(extention, container);

        // The output joins the lookup order last, as it did when it was registered with the
        // editor's pack service, so lookups that fall through to it behave the same.
        public IPackFileContainer CreateOutputContainer(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name can not be empty", nameof(name));

            var output = PackFileContainer.CreatePackFile(name, null, PackFileVersion.PFH5);
            _lookup.TryAdd(output);
            return output;
        }

        public List<PackFile> AddFilesToPack(IPackFileContainer container, List<NewPackFileEntry> newFiles)
        {
            var internalContainer = Cast(container);
            if (container.IsReadOnly)
                throw new InvalidOperationException("Can not add files to readonly pack file");
            return internalContainer.AddFiles(newFiles);
        }

        public void DeleteFiles(IPackFileContainer container, IReadOnlyCollection<string> paths)
        {
            var internalContainer = Cast(container);
            if (container.IsReadOnly)
                throw new InvalidOperationException("Can not delete files inside readonly pack file");
            if (paths.Count == 0)
                return;
            internalContainer.DeleteFiles(paths);
        }

        public void SavePackContainer(IPackFileContainer container, string path, GameInformation gameInformation)
        {
            if (container.IsReadOnly)
                throw new InvalidOperationException("Can not save readonly pack file");
            Cast(container).SaveToDisk(path, createBackup: false, gameInformation);
        }

        public void ReleaseOutputContainer(IPackFileContainer container)
        {
            _lookup.Remove(container);
            if (container is IDisposable disposable)
                disposable.Dispose();
        }

        private static IPackFileContainerInternal Cast(IPackFileContainer container)
            => (IPackFileContainerInternal)container;
    }
}
