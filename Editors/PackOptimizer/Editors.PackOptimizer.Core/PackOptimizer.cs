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
    /// <param name="ModConsumers">Other enabled mods, read-only, in load order: first loads first and loses, last wins. They may reference or override the source's files; the run never breaks them. Null or empty: none.</param>
    /// <param name="SourceLoadOrderIndex">How many of <paramref name="ModConsumers"/> load before the source. Null: all of them (the source wins over every consumer).</param>
    /// <param name="DbSchema">DB table layouts (an RPFM schema) tried before the schema embedded in this library; null uses the embedded one only.</param>
    /// <param name="OutputWritePath">Where the output pack is physically written; null means <paramref name="OutputPath"/>. Lets a caller stage the file and move it into place on success.</param>
    /// <param name="ReportWritePath">Where the report is physically written; null means <paramref name="ReportPath"/>.</param>
    public sealed record PackOptimizerRequest(
        string SourcePackPath,
        IPackFileContainer SourceContainer,
        IReadOnlyList<IPackFileContainer> ConsumerContainers,
        string OutputPath,
        string ReportPath,
        GameTypeEnum Game,
        PackOptimizerOptions Options,
        MissingTexturePolicy MissingTexturePolicy = MissingTexturePolicy.Skip,
        string? CacheDirectory = null,
        string? OutputWritePath = null,
        string? ReportWritePath = null,
        IReadOnlyList<IPackFileContainer>? ModConsumers = null,
        int? SourceLoadOrderIndex = null,
        PackOptimizerDbSchema? DbSchema = null);

    /// <summary>
    /// A consumer pack and why it can matter to the result. A pack with no reason cannot change
    /// what the run keeps, freezes or derives; the narrow cache key may leave it out.
    /// </summary>
    public sealed record PackOptimizerConsumerPack(
        string Path,
        string Name,
        bool Relevant,
        int FrozenFiles,
        int UsedFiles,
        int DbMentions,
        int UndecodableTables,
        int VanillaOverridesReached);

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
        int SkippedMeshCount,
        int ConsumerPackCount = 0,
        int ExternallyUsedFileCount = 0,
        int FrozenFileCount = 0,
        IReadOnlyList<string>? Warnings = null,
        IReadOnlyList<PackOptimizerConsumerPack>? ConsumerPacks = null);

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
                    result.SkippedMeshCount,
                    result.ConsumerPackCount,
                    result.ExternallyUsedFileCount,
                    result.FrozenFileCount,
                    result.Warnings,
                    result.ConsumerPacks);
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

        /// <summary>
        /// Which consumers can matter to a run, without running it: the consumers' overlap with the
        /// source (frozen files), their references to it (used files), DB tables that name its
        /// VMDs, tables that cannot be decoded and vanilla files they replace that the source
        /// reaches. A superset of what the real run finds relevant. Reads index data and the small
        /// text files of each pack only.
        /// </summary>
        public static IReadOnlyList<PackOptimizerConsumerPack> ProbeConsumers(
            IPackFileContainer sourceContainer,
            IReadOnlyList<IPackFileContainer> modConsumers,
            int? sourceLoadOrderIndex,
            IReadOnlyList<IPackFileContainer> vanillaContainers,
            PackOptimizerDbSchema? dbSchema = null,
            CancellationToken cancellationToken = default)
            => PackTextureAtlasBatchService.ProbeConsumers(
                sourceContainer, modConsumers, sourceLoadOrderIndex, vanillaContainers, dbSchema, cancellationToken);

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
