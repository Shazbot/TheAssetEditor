using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Models.Containers;
using Shared.Core.Settings;

namespace Editors.PackOptimizer.Core
{
    /// <summary>The unit grouping of the battle-presence model's random armies.</summary>
    public enum PackOptimizerArmyScope
    {
        Subculture = 0,
        Culture = 1,
    }

    /// <summary>How far the atlas source-texture sibling rule links parts that share a source texture.</summary>
    public enum PackOptimizerSiblingScope
    {
        /// <summary>Only parts the same consumer asset (one WSModel or rigid) references the texture from (default).</summary>
        Model = 0,
        /// <summary>Parts of every consumer asset the source pack owns: removes textures another model keeps
        /// referenced, at the cost of far fewer atlased parts.</summary>
        Pack = 1,
    }

    /// <summary>Optional meshoptimizer simplification flags of one editor-LOD level.</summary>
    [Flags]
    public enum EditorLodSimplifyFlags
    {
        None = 0,
        PreserveFolds = 1,
        RegularizeLight = 2,
        ErrorClamped = 4,
        /// <summary>Drops small disconnected components; only allowed from LOD3 on.</summary>
        Prune = 8,
    }

    /// <summary>Simplifier settings for one LOD level of the editor LOD rebuild.</summary>
    public sealed record EditorLodLevelSettings(
        float TargetError,
        bool LockBorder,
        EditorLodSimplifyFlags Flags = EditorLodSimplifyFlags.None,
        float AttributeScale = 1f)
    {
        /// <summary>The first LOD level (1-based) on which <see cref="EditorLodSimplifyFlags.Prune"/> may be used.</summary>
        public const int FirstPruneLevel = 3;

        public static readonly IReadOnlyList<EditorLodLevelSettings> Default =
        [
            new(0.02f, true),
            new(0.04f, true),
            new(0.08f, false),
        ];

        /// <summary>The settings of LOD <paramref name="lodIndex"/> (1-based); levels past the table use its last entry.</summary>
        public static EditorLodLevelSettings For(IReadOnlyList<EditorLodLevelSettings> table, int lodIndex)
            => table[Math.Min(Math.Max(lodIndex, 1) - 1, table.Count - 1)];

        /// <summary>Throws when the table is empty, has a bad value, or prunes before <see cref="FirstPruneLevel"/>.</summary>
        public static IReadOnlyList<EditorLodLevelSettings> Validate(IReadOnlyList<EditorLodLevelSettings> table)
        {
            if (table.Count == 0)
                throw new ArgumentException("The editor LOD settings need at least one level.");
            for (var i = 0; i < table.Count; i++)
            {
                var level = i + 1;
                var entry = table[i];
                if (!float.IsFinite(entry.TargetError) || entry.TargetError < 0)
                    throw new ArgumentException($"Editor LOD level {level}: the error cap must be finite and not negative.");
                if (!float.IsFinite(entry.AttributeScale) || entry.AttributeScale < 0)
                    throw new ArgumentException($"Editor LOD level {level}: the attribute scale must be finite and not negative.");
                if (entry.Flags.HasFlag(EditorLodSimplifyFlags.Prune) && level < FirstPruneLevel)
                    throw new ArgumentException($"Editor LOD level {level}: prune is only allowed from LOD{FirstPruneLevel} on.");
            }

            return table;
        }

        /// <summary>
        /// Parses "error:locked|free[:flags][:attrScale]" entries separated by commas, for example
        /// "0.02:locked:folds+reglight,0.04:locked:folds,0.08:free:prune+clamped:attr0.5". Flags are joined with '+'
        /// (folds, reglight, clamped, prune). The flags and the attribute scale are optional and may come in either order.
        /// </summary>
        public static IReadOnlyList<EditorLodLevelSettings> Parse(string text)
        {
            var table = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part =>
                {
                    var pieces = part.Split(':', StringSplitOptions.TrimEntries);
                    var flags = EditorLodSimplifyFlags.None;
                    var scale = 1f;
                    foreach (var extra in pieces.Skip(2))
                    {
                        if (extra.StartsWith("attr", StringComparison.OrdinalIgnoreCase))
                        {
                            scale = float.Parse(extra[4..], System.Globalization.CultureInfo.InvariantCulture);
                            continue;
                        }

                        foreach (var name in extra.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            flags |= name.ToLowerInvariant() switch
                            {
                                "folds" or "preservefolds" => EditorLodSimplifyFlags.PreserveFolds,
                                "reglight" or "regularizelight" => EditorLodSimplifyFlags.RegularizeLight,
                                "clamped" or "errorclamped" => EditorLodSimplifyFlags.ErrorClamped,
                                "prune" => EditorLodSimplifyFlags.Prune,
                                _ => throw new ArgumentException($"Unknown editor LOD flag '{name}' in '{part}'."),
                            };
                        }
                    }

                    return new EditorLodLevelSettings(
                        float.Parse(pieces[0], System.Globalization.CultureInfo.InvariantCulture),
                        !(pieces.Length > 1 && pieces[1].Equals("free", StringComparison.OrdinalIgnoreCase)),
                        flags,
                        scale);
                })
                .ToArray();
            return Validate(table);
        }
    }

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
        /// <summary>
        /// Which unit groups form separate random-army pools in the battle-presence model.
        /// <see cref="PackOptimizerArmyScope.Subculture"/> (default) keeps e.g. the Tilean and Estalian
        /// mercenary lists apart from the rest of their culture; <see cref="PackOptimizerArmyScope.Culture"/>
        /// pools every subculture of a culture.
        /// </summary>
        public PackOptimizerArmyScope ArmyScope { get; init; } = PackOptimizerArmyScope.Subculture;
        /// <summary>
        /// Whether atlas candidates that share a source texture are grouped per consumer asset
        /// (<see cref="PackOptimizerSiblingScope.Model"/>, default) or across every source-pack consumer
        /// of the texture (<see cref="PackOptimizerSiblingScope.Pack"/>).
        /// </summary>
        public PackOptimizerSiblingScope SiblingScope { get; init; } = PackOptimizerSiblingScope.Model;
        public bool CrossRigidOnly { get; init; }
        public bool CrossRigidMerges { get; init; }
        public bool BuildAtlases { get; init; } = true;
        public bool DownscaleTextures { get; init; }
        public bool SharedTextureRepack { get; init; }
        public bool CompactTextures { get; init; }
        public bool DropUnreadAlpha { get; init; }
        /// <summary>
        /// Writes the output's .rigid_model_v2 files LZ4-compressed the way the mod manager's staging
        /// compressor does (an LZ4 frame behind the 4-byte size, only for files of at least
        /// 256 KiB that shrink to 75 % or less). Off by default: it changes the saved bytes, not the
        /// content.
        /// </summary>
        public bool CompressRigidModels { get; init; }
        /// <summary>
        /// Rebuilds LOD 1+ of rigids that AssetEditor's old decimator generated (see EditorLodDetector) from
        /// LOD0 with meshoptimizer. LOD0, the LOD count, headers and part lists stay as they are.
        /// </summary>
        public bool RemakeEditorLods { get; init; }
        /// <summary>
        /// Simplifier settings of the editor LOD rebuild per LOD level: entry 0 is LOD1, entry 1 is LOD2 and the
        /// last entry applies to every deeper LOD. Default: 2 % error with the border locked at LOD1, 4 % locked at
        /// LOD2, 8 % with the border free from LOD3.
        /// </summary>
        public IReadOnlyList<EditorLodLevelSettings> EditorLodSettings { get; init; } = EditorLodLevelSettings.Default;
        /// <summary>
        /// Weight of the bone-embedding attributes of the editor LOD rebuild for skinned parts: each bone gets a fixed
        /// pseudo-random 4D unit vector and each vertex carries the weight-blend of its bones' vectors, so collapses
        /// across joint boundaries cost more. 0 (default) leaves the rebuild as it was.
        /// </summary>
        public float EditorLodBoneWeight { get; init; }
        /// <summary>Marks vertices whose dominant bone differs from a neighbour's as priority vertices (off by default).</summary>
        public bool EditorLodJointPriority { get; init; }
        /// <summary>Samples the skinning error of the rebuilt LODs for the report (report-only, costs time; off by default).</summary>
        public bool EditorLodReportSkinningError { get; init; }
    }

    /// <summary>
    /// The versions that decide whether a stored result may be reused. Callers key their caches on them.
    /// </summary>
    public static class PackOptimizerVersions
    {
        /// <summary>
        /// Version of the optimizer's OUTPUT. Bump it, and only then, when the optimizer produces different
        /// output for the same inputs and options (a stage, a threshold or a codec changed). A change that
        /// leaves the output as it was (speed, logging, the probe) must not bump it, because the mod
        /// manager reuses cached results for the same version. Enforced by the golden-output test
        /// (OutputVersionGoldenTests): it pins a hash of the output of the optimizer's deterministic output
        /// stages next to this number, so changing the output without bumping the version fails the test.
        /// </summary>
        public const int OutputVersion = 32;

        /// <summary>
        /// Version of the consumer scan and probe results (what makes another mod relevant) and of the format
        /// of the scan cache files. Bump it when a pack's scan data, the relevance rules or the file format
        /// change; cached scans of older versions are ignored and pruned.
        /// </summary>
        public const int ScanVersion = 2;

        /// <summary>
        /// Version of the visual-asset check (the host's <c>check</c> action). A host that prints
        /// <c>check=N</c> in its version line supports it; callers may use that to detect an older host.
        /// Not part of any cache key.
        /// </summary>
        public const int CheckVersion = 2;

        public static string Describe() => $"output={OutputVersion} scan={ScanVersion} check={CheckVersion}";
    }

    /// <summary>What an optimization saved, for display. All sizes are bytes.</summary>
    /// <param name="TextureBytesSaved">DDS bytes in the source minus DDS bytes in the output.</param>
    /// <param name="DrawCallsEliminated">Scenario-expected battle draw calls the selected merges remove.</param>
    /// <param name="MergedModelsGenerated">Rigid models in the output that the source did not have.</param>
    public sealed record PackOptimizerSummary(
        long TextureBytesSaved,
        double DrawCallsEliminated,
        int MergedModelsGenerated);

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
    /// <param name="ResolutionProviders">Read-only packs, each an instance from <paramref name="ModConsumers"/>, that have a declared dependency relationship with the source (either direction). A file the source references but does not contain is looked up in them (last in load order wins) before the game packs, as if the source and they were one pack. Only the files they supply are read; they are never changed or counted as game data. Null or empty: none.</param>
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
        PackOptimizerDbSchema? DbSchema = null,
        IReadOnlyList<IPackFileContainer>? ResolutionProviders = null);

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
        int VanillaOverridesReached,
        bool ResolutionProvider = false);

    /// <param name="Sources">Per requested source, in request order, its consumers (the load order without it).</param>
    public sealed record PackOptimizerBatchProbe(
        IReadOnlyList<IReadOnlyList<PackOptimizerConsumerPack>> Sources,
        int PacksScanned,
        int ScanCacheHits,
        TimeSpan ScanElapsed);

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
        IReadOnlyList<PackOptimizerConsumerPack>? ConsumerPacks = null,
        PackOptimizerSummary? Summary = null);

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
                    result.ConsumerPacks,
                    result.Summary);
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
        /// The visual-asset check: runs only the detection stages of the optimizer (broken or invalid
        /// references, malformed or repaired XML, unreadable models and textures, alpha-tested materials with
        /// opaque alpha, duplicate textures, LOD and bone weight problems) and returns the findings. It
        /// optimizes nothing and writes no pack. Cancellation returns <see cref="PackOptimizerStatus.Cancelled"/>
        /// with no findings.
        /// </summary>
        public static PackOptimizerCheckResult Check(
            PackOptimizerCheckRequest request,
            IProgress<PackOptimizerProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var packFiles = new PackOptimizerPackFiles(request.ConsumerContainers);
            var service = new PackTextureAtlasBatchService(packFiles, request.CacheDirectory);
            IProgress<PackTextureAtlasBatchService.TextureAtlasPackProgress>? inner = progress == null
                ? null
                : new DirectProgress<PackTextureAtlasBatchService.TextureAtlasPackProgress>(
                    value => progress.Report(new PackOptimizerProgress(
                        value.Phase, value.Item, value.Current, value.Total)));

            try
            {
                return service.Check(request, inner, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                var summary = PackOptimizerCheck.Summarize([]);
                return new PackOptimizerCheckResult(
                    PackOptimizerStatus.Cancelled, [], summary, string.Empty, 0, 0, 0, 0, TimeSpan.Zero);
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

        /// <summary>
        /// The probe for several sources against one load order (every enabled mod, sources included).
        /// Each pack is scanned once and the scan is kept in <paramref name="cacheDirectory"/> (null: not
        /// kept); <paramref name="schemaId"/> identifies the DB schema in use so a changed schema ignores
        /// older scans. For source <c>i</c> the consumers are the load order without it.
        /// </summary>
        public static PackOptimizerBatchProbe ProbeConsumersBatch(
            IReadOnlyList<IPackFileContainer> loadOrder,
            IReadOnlyList<int> sourceIndexes,
            IReadOnlyList<IPackFileContainer> vanillaContainers,
            PackOptimizerDbSchema? dbSchema = null,
            string? cacheDirectory = null,
            string schemaId = "",
            CancellationToken cancellationToken = default)
        {
            var result = PackTextureAtlasBatchService.ProbeBatch(
                loadOrder, sourceIndexes, vanillaContainers, dbSchema, cacheDirectory, schemaId, cancellationToken);
            return new PackOptimizerBatchProbe(result.Sources, result.PacksScanned, result.ScanCacheHits, result.ScanElapsed);
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
