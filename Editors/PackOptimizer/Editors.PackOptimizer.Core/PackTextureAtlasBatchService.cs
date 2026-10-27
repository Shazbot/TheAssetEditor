using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Editors.ImportExport.Importing.Importers.PngToDds;
using Editors.ImportExport.Importing.Importers.PngToDds.Helpers;
using Editors.ImportExport.TextureAtlas;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Models.Containers;
using Shared.Core.PackFiles.Utility;
using Shared.Core.Settings;
using Shared.GameFormats;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.LodHeader;
using Shared.GameFormats.RigidModel.MaterialHeaders;
using Shared.GameFormats.RigidModel.Types;
using Shared.GameFormats.RigidModel.Vertex;
using Shared.GameFormats.Vmd;
using static Shared.GameFormats.Vmd.VariantMeshDefinition;

namespace Editors.PackOptimizer.Core
{
    public sealed partial class PackTextureAtlasBatchService
    {
        private const string AtlasDirectory = @"textures\asset_editor\atlases";
        private const string AtlasProfilingEnvironmentVariable = "ASSET_EDITOR_ATLAS_PROFILING";
        private const int PackAtlasMaxSize = 4096;
        private const double TexelDensityReferencePercentile = 0.75;
        private const double TexelDensityOutlierMultiplier = 1.5;
        private const double MinAtlasResolutionScale = 0.25;
        private const int MinAtlasResolutionScaleDimension = 256;
        // Calibrated from the !ak_teb3 value curve: preserve almost all expected
        // battle draw savings while rejecting expensive low-value atlas batches.
        private const long MaxNetBcnBytesPerExpectedArmyDraw = 256L * 1024; // 0.25 MiB
        private const long MaxNetBcnBytesPerFallbackDraw = 8L * 1024 * 1024;
        // A texture-only atlas has no geometry-merge draw credit. Permit it only when it
        // retires multiple source textures from the source pack's references and its net
        // payload growth is small per retired texture (or meaningfully displaces modeled
        // battle residency).
        private const long MaxNetBcnBytesPerRetiredSourceTexture = 512L * 1024; // 0.5 MiB
        private const int MinimumRetiredSourceTexturesForTextureOnlyAtlas = 2;
        // A prospective texture merge does eliminate a real geometry draw, even when the
        // original source textures remain reachable through other materials. Give that
        // structural draw a bounded floor plus scenario-proportional credit, while keeping
        // the cumulative pack-growth cap below.
        private const long MaxNetBcnBytesPerTextureOnlyMergeDraw = 4L * 1024 * 1024; // 4 MiB
        private const long MaxNetBcnBytesPerTextureOnlyMergeExpectedArmyDraw =
            768L * 1024; // 0.75 MiB
        // A texture-consolidation atlas keeps geometry and draw credit unchanged. Permit it
        // only when it replaces multiple real texture assignments at a tighter payload cost.
        private const long MaxNetBcnBytesPerConsolidatedTextureAssignment = 256L * 1024; // 0.25 MiB
        private const int MinimumConsolidatedTextureAssignmentsForAtlas = 2;
        // Large planner batches can force a power-of-two atlas allocation for unrelated
        // texture assignments. Start texture-consolidation scoring in small cohorts and
        // recursively split cohorts whose marginal residency is still too expensive.
        private const int TextureConsolidationInitialCohortCandidateLimit = 8;
        // Prospective texture-merge groups can contain unrelated large placements. Search
        // bounded pairs/triples/four-way subsets before giving up on the whole group.
        private const int TextureOnlyMergeSubsetCandidateLimit = 10;
        private const int TextureOnlyMergeSubsetOptionLimit = 128;
        // Reserve part of the subset budget for four-way combinations. Pair/triple options
        // remain the broad first pass; four-way options are most useful when the full group
        // is too expensive but a compact four-mesh subset can still pass the value gate.
        private const int TextureOnlyMergeFourWaySubsetOptionReserve = 32;
        private const int TextureOnlyMergeNearMissLimit = 24;
        private const double AtlasValueGateExpectedDrawEpsilon = 0.000001;
        private const double MaxReachableBcnGrowthRatio = 0.50;
        // Cross-rigid generated assets are selected independently from atlas BCn. Runtime
        // residency and physical pack growth are constrained across the selected portfolio,
        // so cheap payloads can create headroom for individually more expensive payloads
        // without relaxing the final per-benefit limits. A hard cumulative growth cap still
        // bounds absolute pack expansion. Multi-VMD payload reuse is rewarded naturally
        // because all rewrite-plan benefit is aggregated before geometry is charged once.
        private const long MaxCrossRigidExpectedResidentGeometryBytesPerExpectedArmyDraw =
            512L * 1024; // 0.5 MiB
        private const long MaxCrossRigidGeneratedGeometryBytesPerExpectedArmyDraw =
            16L * 1024 * 1024; // 16 MiB
        private const long MaxCrossRigidGeneratedGeometryGrowthBytes =
            256L * 1024 * 1024; // 256 MiB
        private const double MinimumCrossRigidSingleVmdExpectedArmyDraw = 0.02;
        // Per-payload ceiling relative to the portfolio budgets above: a
        // single payload may not cost more than this multiple of the
        // per-draw budget for its own marginal benefit. The portfolio
        // budgets still apply cumulatively on top of this ceiling.
        private const double CrossRigidPerPayloadCostCeilingMultiplier = 1.0;
        // Limit exact generated joint appearance states, not probability precision.
        // Large VMDs need more room than 512 combinations, while the
        // portfolio's geometry and residency budgets still cap output cost.
        // An exact joint writer needs every weighted Cartesian state to
        // preserve the source VMD's selection probabilities. The previous
        // 1,024-state ceiling excluded otherwise rewrite-safe three-plus
        // component groups in high-variation VMDs. Permit a bounded wider
        // search; emitted geometry still passes the full portfolio value
        // and residency gates, and no probabilities are approximated.
        private const int MaxCrossRigidJointStateCombinations = 4096;
        // The game engine shows at most this many alternatives per SLOT
        // (RenderDoc captures); extra alternatives are never selected.
        // More variety has to come from nesting child VMD references.
        private const int CrossRigidEngineSlotAlternativeLimit = 5;
        // Tags every SLOT the cross-rigid writer creates so the limit can
        // be enforced and checked after documents were cloned. Stripped
        // before the VMDs are loaded and saved.
        private const string CrossRigidWriterSlotMarkerAttribute =
            "cross_rigid_writer_slot";
        // When a full co-rendering group is too complex to rewrite jointly,
        // smaller cross-rigid pairs can retain independent visual choices.
        private const int MaxCrossRigidPairFallbacksPerGroup = 24;
        // A bounded three-rigid alternative can recover savings from a
        // full group whose Cartesian appearance space is too large.
        private const int MaxCrossRigidTripleFallbacksPerGroup = 32;
        // Four components can recover merges from otherwise unwritable five+
        // component groups without expanding the global Cartesian writer.
        private const int MaxCrossRigidQuadrupleFallbacksPerGroup = 24;
        // Small five-component subsets can recover four draws when the full
        // appearance group is too large to rewrite as one Cartesian state.
        private const int MaxCrossRigidQuintupleFallbacksPerGroup = 12;
        private const int MaxCrossRigidGeneratedLodCount = 5;
        private const float CrossRigidLodDistanceEpsilon = 0.01f;
        private static readonly bool AtlasProfilingEnabled =
            IsEnabledEnvironmentVariable(AtlasProfilingEnvironmentVariable);

        private static readonly (string Slot, TextureType Type, string Suffix)[] AtlasChannels =
        [
            ("t_xml_base_colour", TextureType.BaseColour, "base_colour"),
            ("t_xml_material_map", TextureType.MaterialMap, "material_map"),
            ("t_xml_normal", TextureType.Normal, "normal"),
            ("t_xml_mask", TextureType.Mask, "mask"),
        ];

        private const long MaxAutomaticCommonTextureConstantProbePixels = 4096;
        private const string CanonicalBlackMaskTexturePath =
            @"commontextures\default_black.dds";
        private const string CanonicalFaction3MaskChannelValue = "0,0,1,0";
        private static readonly TextureAtlasConstantColor ZeroMaskColor =
            new(0, 0, 0, 0);

        private static readonly HashSet<string> KnownConstantTexturePaths =
        [
            CanonicalBlackMaskTexturePath,
            @"commontextures\default_white.dds",
            @"commontextures\default_normal.dds",
        ];

        private readonly IPackOptimizerPackFiles _packFileService;
        private readonly string? _cacheDirectory;

        public PackTextureAtlasBatchService(
            IPackOptimizerPackFiles packFileService,
            string? cacheDirectory = null)
        {
            _packFileService = packFileService;
            _cacheDirectory = cacheDirectory;
        }

        public BatchResult Process(
            PackOptimizerRequest request,
            IProgress<TextureAtlasPackProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            // The game packs are queried thousands of times by the stages below: hold their
            // database connections open for the run instead of opening one per lookup.
            var bulkReadScopes = (_packFileService.GetAllPackfileContainers() ?? [])
                .OfType<IBulkReadPackFileContainer>()
                .Select(container => container.BeginBulkRead())
                .ToList();
            try
            {
                return ProcessCore(request, progress, cancellationToken);
            }
            finally
            {
                foreach (var scope in bulkReadScopes)
                    scope.Dispose();
            }
        }

        private BatchResult ProcessCore(
            PackOptimizerRequest request,
            IProgress<TextureAtlasPackProgress>? progress,
            CancellationToken cancellationToken)
        {
            var sourcePath = request.SourcePackPath;
            var outputPath = request.OutputPath;
            var options = request.Options;
            var mergeCompatibleMeshes = options.MergeCompatibleMeshes;
            var shareAtlasesAcrossVmds = options.ShareAtlasesAcrossVmds;
            var optimizeGeometry = options.OptimizeGeometry;
            var atlasAllVmds = options.AtlasAllVmds;
            var scoreAllGameUnits = options.ScoreAllGameUnits;
            var lodAwareAtlasPlanning = options.LodAwareAtlasPlanning;
            var crossRigidOnly = options.CrossRigidOnly;
            var crossRigidMerges = options.CrossRigidMerges;
            var buildAtlases = options.BuildAtlases;
            var downscaleTextures = options.DownscaleTextures;
            var sharedTextureRepack = options.SharedTextureRepack;
            var compactTextures = options.CompactTextures;
            var dropUnreadAlpha = options.DropUnreadAlpha;
            // Meshes with genuinely unresolved secondary textures are no longer atlased
            // because UV0 is shared, so every missing-texture policy skips them.
            _ = request.MissingTexturePolicy;

            var reportPath = request.ReportPath;
            var totalStopwatch = Stopwatch.StartNew();
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, "Loading source pack", item: Path.GetFileName(sourcePath));
            IPackFileContainer? output = null;
            BatchState? state = null;
            List<string> vmdRoots = [];
            List<MalformedVmdEntry> malformedVmdRoots = [];

            try
            {
                var source = request.SourceContainer;
                cancellationToken.ThrowIfCancellationRequested();

                var sourcePaths = source.GetAllFiles().Keys.ToList();

                var outputName = Path.GetFileNameWithoutExtension(outputPath);
                output = _packFileService.CreateOutputContainer(outputName);
                state = new BatchState(
                    source,
                    output,
                    _packFileService,
                    sourcePath,
                    outputPath,
                    reportPath,
                    mergeCompatibleMeshes,
                    shareAtlasesAcrossVmds,
                    optimizeGeometry);
                state.LodAwareAtlasPlanningEnabled = lodAwareAtlasPlanning;
                state.ArmyScope = options.ArmyScope;
                state.SiblingScope = options.SiblingScope;
                state.CrossRigidOnly = crossRigidOnly;
                state.CrossRigidMergesEnabled =
                    crossRigidMerges || crossRigidOnly;
                state.BuildAtlases = buildAtlases;
                // Cross-rigid-only mode never touches textures, whatever the caller asks.
                state.DownscaleTextures = downscaleTextures && !crossRigidOnly;
                state.CompactTextures = compactTextures && !crossRigidOnly;
                state.DropUnreadAlpha = dropUnreadAlpha && state.CompactTextures;
                state.RemakeEditorLods = options.RemakeEditorLods;
                state.EditorLodSettings = options.EditorLodSettings;
                state.EditorLodBoneWeight = options.EditorLodBoneWeight;
                state.EditorLodJointPriority = options.EditorLodJointPriority;
                state.EditorLodReportSkinningError = options.EditorLodReportSkinningError;
                // The repack writes textures, so it is ignored in cross-rigid-only mode (see
                // ResolveSharedTextureRepack); next to atlasing and cross-rigid merges it runs
                // after them and leaves what they changed alone.
                state.SharedTextureRepackRequested = sharedTextureRepack;
                state.SharedTextureRepackEnabled = ResolveSharedTextureRepack(
                    sharedTextureRepack,
                    buildAtlases,
                    crossRigidMerges,
                    crossRigidOnly);
                state.CancellationToken = cancellationToken;
                state.ReportWritePath = request.ReportWritePath;
                state.Progress = progress;
                var gameplayContainerSnapshot =
                    _packFileService.GetAllPackfileContainers().ToList();
                state.GameplayTraversalContainers = gameplayContainerSnapshot;
                var cacheLoadStopwatch = Stopwatch.StartNew();
                state.VanillaAtlasConsumerCache =
                    Wh3VanillaAtlasConsumerCache.Load(
                        _cacheDirectory,
                        request.Game,
                        gameplayContainerSnapshot);
                state.PhaseDurations["Vanilla atlas consumer cache load"] = cacheLoadStopwatch.Elapsed;
                state.ExistingAtlasOutputDetected = HasGeneratedAtlasOutput(source);
                state.DbSchemaPath = request.DbSchema?.Path;

                if (request.ModConsumers is { Count: > 0 } modConsumers)
                {
                    var sourceIndex = request.SourceLoadOrderIndex ?? modConsumers.Count;
                    if (sourceIndex < 0 || sourceIndex > modConsumers.Count)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(request),
                            sourceIndex,
                            $"SourceLoadOrderIndex must be between 0 and {modConsumers.Count}.");
                    }

                    if (request.ResolutionProviders is { Count: > 0 } providerRequest)
                    {
                        foreach (var provider in providerRequest)
                        {
                            if (!modConsumers.Any(consumer => ReferenceEquals(consumer, provider)))
                            {
                                throw new ArgumentException(
                                    "ResolutionProviders must be a subset of ModConsumers.",
                                    nameof(request));
                            }
                        }

                        // Load order of the mod list, not the order the caller listed them in.
                        state.ResolutionProviders = new ResolutionProviderSet(
                            modConsumers
                                .Where(consumer => providerRequest.Any(item => ReferenceEquals(item, consumer)))
                                .ToList());
                    }

                    ReportProgress(progress, "Scanning consumer packs", item: $"{modConsumers.Count} pack(s)");
                    var consumerStopwatch = Stopwatch.StartNew();
                    state.Consumers = BuildConsumerReferenceIndex(
                        state,
                        modConsumers,
                        sourceIndex,
                        cancellationToken,
                        progress);
                    state.PhaseDurations["Scan consumer packs"] = consumerStopwatch.Elapsed;
                }

                var allVmdPaths = sourcePaths
                    .Where(x => Path.GetExtension(x).Equals(".variantmeshdefinition", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (allVmdPaths.Count == 0)
                    throw new InvalidOperationException("The selected pack contains no .variantmeshdefinition files.");

                ReportProgress(
                    progress,
                    "Validating VMD roots",
                    item: $"{allVmdPaths.Count} VMD file(s)");
                var phaseStopwatch = Stopwatch.StartNew();
                (vmdRoots, malformedVmdRoots) = ValidateVmdRoots(
                    state,
                    source,
                    allVmdPaths,
                    cancellationToken,
                    progress);
                state.PhaseDurations["Validate VMD roots"] = phaseStopwatch.Elapsed;

                if (vmdRoots.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"The selected pack contains {allVmdPaths.Count} .variantmeshdefinition file(s), " +
                        "but none could be parsed.");
                }

                ReportProgress(
                    progress,
                    "Resolving unit categories",
                    item: $"{vmdRoots.Count} VMD root(s)");
                phaseStopwatch.Restart();
                var childVmdsByVmd = BuildChildVmdDependencyMap(
                    state,
                    source,
                    vmdRoots,
                    cancellationToken);
                // Residency accounting always needs the complete game roster. Mutation is
                // still restricted to the selected pack; culture scoping happens inside the
                // residency model so vanilla units can remain read-only consumers without
                // polluting a single mixed-culture army denominator.
                var modDbContainers = state.Consumers?.Packs
                    .Select(pack => new Wh3ModDbContainer(pack.Container, pack.BelowSource))
                    .ToList();
                state.UnitCategoryResolution = Wh3UnitCategoryResolver.ResolveAgainstContainers(
                    source,
                    vmdRoots,
                    childVmdsByVmd,
                    cancellationToken,
                    Wh3ArmyVisualScenario.Default,
                    gameplayContainerSnapshot,
                    modDbContainers,
                    request.DbSchema);
                if (state.UnitCategoryResolution.IsGameplayResolutionHealthy &&
                    state.Consumers != null)
                {
                    // Consumers may add gameplay users and roots, never remove or change one:
                    // the resolution everything uses is the one without any consumer table plus
                    // what the consumers' rows add. Skipped when no consumer has a table.
                    Wh3UnitCategoryResolution? withoutConsumers = null;
                    if (state.Consumers.Packs.Any(pack => pack.DbTables != 0))
                    {
                        var withoutStopwatch = Stopwatch.StartNew();
                        withoutConsumers = Wh3UnitCategoryResolver.ResolveAgainstContainers(
                            source,
                            vmdRoots,
                            childVmdsByVmd,
                            cancellationToken,
                            Wh3ArmyVisualScenario.Default,
                            gameplayContainerSnapshot,
                            null,
                            request.DbSchema);
                        state.PhaseDurations["Resolve unit categories (without consumers)"] =
                            withoutStopwatch.Elapsed;
                        state.UnitCategoryResolution = Wh3UnitCategoryResolver.MergeAdditive(
                            withoutConsumers,
                            state.UnitCategoryResolution);
                    }

                    FinalizeConsumerReferenceIndex(
                        state,
                        state.Consumers,
                        state.UnitCategoryResolution,
                        withoutConsumers,
                        cancellationToken,
                        progress);
                    state.PhaseDurations["Index consumer references"] = state.Consumers.FinalizeElapsed;
                }

                if (!state.UnitCategoryResolution.IsGameplayResolutionHealthy)
                {
                    throw new InvalidOperationException(
                        "Atlas processing could not be resolved safely because gameplay " +
                        "mesh-consumer discovery requires a healthy WH3 DB/schema resolution: " +
                        state.UnitCategoryResolution.GameplayResolutionHealthMessage +
                        " Fix the WH3 DB/schema resolution problem before running atlas " +
                        "processing; pack-wide mode cannot bypass this safety gate.");
                }

                var resolveElapsed = phaseStopwatch.Elapsed;
                var residencyStopwatch = Stopwatch.StartNew();
                state.ArmyResidencyModel =
                    state.UnitCategoryResolution.IsGameplayResolutionHealthy
                        ? BuildArmyResidencyModel(
                            state,
                            state.UnitCategoryResolution,
                            includeAllGameCultures: scoreAllGameUnits)
                        : null;
                state.PhaseDurations["Resolve unit categories"] = phaseStopwatch.Elapsed;
                // Detail of the phase above (timing lines are stripped from the fingerprint).
                state.PhaseDurations[
                    $"  of which DB tables read ({state.UnitCategoryResolution.TableFilesRead:N0} files, {state.UnitCategoryResolution.DbBytes / (1024 * 1024):N0} MiB)"] =
                    state.UnitCategoryResolution.DbReadElapsed;
                state.PhaseDurations["  of which DB tables decoded"] = state.UnitCategoryResolution.DbDecodeElapsed;
                state.PhaseDurations["  of which resolver, rest"] =
                    resolveElapsed - state.UnitCategoryResolution.DbReadElapsed - state.UnitCategoryResolution.DbDecodeElapsed;
                state.PhaseDurations["  of which army residency model"] = residencyStopwatch.Elapsed;

                var gameplayEligibleVmdRoots = state.UnitCategoryResolution.UsagesByVmd.Keys
                    .Where(state.UnitCategoryResolution.IsVmdUsageComplete);
                var atlasVmdRoots = SelectAtlasVmdRoots(
                    vmdRoots,
                    gameplayEligibleVmdRoots,
                    atlasAllVmds);
                state.AtlasAllVmdsEnabled = atlasAllVmds;
                state.SourceVmdRootCount = vmdRoots.Count;
                state.AtlasVmdRootCount = atlasVmdRoots.Count;
                ReportProgress(
                    progress,
                    "Selecting atlas VMD population",
                    atlasVmdRoots.Count,
                    vmdRoots.Count,
                    atlasAllVmds
                        ? $"Pack-wide: {atlasVmdRoots.Count:N0} / {vmdRoots.Count:N0} VMD root(s)"
                        : $"Gameplay-used: {atlasVmdRoots.Count:N0} / {vmdRoots.Count:N0} VMD root(s)");

                ReportProgress(
                    progress,
                    "Scanning source dependencies",
                    item: malformedVmdRoots.Count == 0
                        ? $"{vmdRoots.Count} VMD root(s)"
                        : $"{vmdRoots.Count} valid VMD root(s), {malformedVmdRoots.Count} malformed file(s) ignored");
                phaseStopwatch.Restart();
                var assetDependencyRoots = vmdRoots
                    .Concat(
                        state.UnitCategoryResolution?.DirectAssetUsagesByPath.Keys ??
                        Array.Empty<string>())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var originalReachable = CollectReachableAssetFiles(
                    state,
                    source,
                    assetDependencyRoots,
                    cancellationToken,
                    progress,
                    "Scanning source dependencies");
                // The value gate must account for every source-root consumer, including
                // roots that are intentionally outside the atlas population. Otherwise a
                // texture shared by an atlased root and a non-atlased root can be credited as
                // retired even though the latter still keeps it reachable in the output.
                state.SourceReachableAssetFiles.UnionWith(originalReachable);
                state.PhaseDurations["Scan source dependencies"] = phaseStopwatch.Elapsed;

                phaseStopwatch.Restart();
                ReportProgress(
                    progress,
                    "Copying source pack",
                    0,
                    sourcePaths.Count,
                    $"{sourcePaths.Count:N0} files");

                var sourceFiles = source.GetAllFiles();
                var bulkCopyEntries = new List<NewPackFileEntry>(sourcePaths.Count);
                foreach (var sourcePathEntry in sourcePaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!sourceFiles.TryGetValue(sourcePathEntry, out var sourceFile))
                        continue;

                    // Keep unchanged files backed by the source IDataSource until the final save.
                    // This avoids eagerly reading/copying every file into a new MemorySource and
                    // also avoids the per-file logging/event overhead of CopyFileFromOtherPackFile.
                    var directory = Path.GetDirectoryName(sourcePathEntry) ?? string.Empty;
                    var fileName = Path.GetFileName(sourcePathEntry);
                    bulkCopyEntries.Add(new NewPackFileEntry(
                        directory,
                        new PackFile(fileName, sourceFile.DataSource)));
                }

                cancellationToken.ThrowIfCancellationRequested();
                _packFileService.AddFilesToPack(output, bulkCopyEntries);
                ReportProgress(
                    progress,
                    "Copying source pack",
                    sourcePaths.Count,
                    sourcePaths.Count,
                    $"{bulkCopyEntries.Count:N0} files copied");
                state.PhaseDurations["Copy source pack"] = phaseStopwatch.Elapsed;

                state.MalformedVmdRoots.AddRange(malformedVmdRoots);
                // Report-only: reads the source geometry before any stage rewrites it.
                phaseStopwatch.Restart();
                AuditSourceLods(state, atlasVmdRoots, cancellationToken, progress);
                state.PhaseDurations["LOD audit"] = phaseStopwatch.Elapsed;
                phaseStopwatch.Restart();
                // Report-only: materials and textures of the rigids the audit reached.
                RunAssetChecks(state, cancellationToken, progress);
                state.PhaseDurations["Asset checks"] = phaseStopwatch.Elapsed;
                // Report-only: needs the rigids and exposure the LOD audit collected.
                if (!crossRigidOnly)
                    RunBoneWeightAudit(state, cancellationToken, progress, null, null);
                // Rebuilds the LODs AssetEditor's old decimator made. Runs here so every later stage
                // (LOD-aware planning, merges, geometry optimisation, repack) sees the new LODs.
                if (state.RemakeEditorLods && !crossRigidOnly)
                {
                    phaseStopwatch.Restart();
                    RemakeEditorLods(state, cancellationToken, progress);
                    state.PhaseDurations["Remake editor LODs"] = phaseStopwatch.Elapsed;
                }

                // Discovered before the cross-rigid stage (on the source state), processed after it.
                List<AtlasCandidate> discoveredCandidates = [];
                phaseStopwatch.Restart();
                if (crossRigidOnly)
                {
                    ReportProgress(
                        progress,
                        "Indexing gameplay mesh consumers",
                        item: "Cross-rigid-only mode");
                    IndexImmutableMeshMergeConsumers(
                        state,
                        vmdRoots,
                        cancellationToken);
                    state.PhaseDurations["Index gameplay mesh consumers"] =
                        phaseStopwatch.Elapsed;
                }
                else
                {
                    BuildWsUsageIndex(state, cancellationToken, progress);
                    if (buildAtlases)
                        IndexTaintedGameplayWsModels(state, cancellationToken);
                    // Direct rigid consumers constrain atlas rewrites as well as structural
                    // mesh merging. Discover them before atlas candidates even when the
                    // optional structural merge pass is disabled.
                    IndexImmutableMeshMergeConsumers(
                        state,
                        vmdRoots,
                        cancellationToken);
                    state.PhaseDurations["Index WSModels"] =
                        phaseStopwatch.Elapsed;

                    if (buildAtlases)
                    {
                        phaseStopwatch.Restart();
                        ReportProgress(progress, "Estimating source BCn residency");
                        state.SourceBcnResidency = CalculateBcnTextureResidency(
                            state,
                            source,
                            originalReachable,
                            cancellationToken);
                        state.PhaseDurations["Estimate source BCn residency"] =
                            phaseStopwatch.Elapsed;

                        phaseStopwatch.Restart();
                        var candidateDiscovery = DiscoverAtlasCandidates(
                            state,
                            atlasVmdRoots,
                            shareAtlasesAcrossVmds,
                            cancellationToken,
                            progress);

                        // UV0 is shared by every material texture channel. If any real
                        // secondary texture is unresolved, remapping UV0 while preserving that old
                        // texture path would make it sample with atlas UVs and corrupt rendering.
                        // Such meshes are therefore always skipped. The only unresolved sentinel
                        // normalized to "absent" earlier is t_xml_mask/test_mask.dds.
                        discoveredCandidates = ApplyMissingTextureDecision(
                            state,
                            candidateDiscovery.Candidates);
                        state.AtlasValueGateCandidateTexturePaths.Clear();
                        state.AtlasValueGateCandidateTexturePaths.UnionWith(
                            GetAtlasValueGateCandidateTexturePaths(discoveredCandidates));
                        state.AtlasValueGateCandidateTexturePathsInitialized = true;
                        state.PhaseDurations["Discover atlas candidates"] =
                            phaseStopwatch.Elapsed;
                    }

                    if (mergeCompatibleMeshes)
                    {
                        phaseStopwatch.Restart();
                        AnalyzeCompatibleMeshMerges(
                            state,
                            cancellationToken,
                            progress);
                        state.PhaseDurations["Analyze structural mesh merges"] =
                            phaseStopwatch.Elapsed;
                    }

                    // The downscale analysis on the source state, for the stages that size
                    // textures before the late (authoritative) downscale run on the output.
                    if (state.DownscaleTextures &&
                        (buildAtlases || state.SharedTextureRepackEnabled))
                    {
                        phaseStopwatch.Restart();
                        RunEarlyTextureDownscale(state, cancellationToken, progress);
                    }

                    if (state.SharedTextureRepackEnabled)
                    {
                        // The two analyses the repack needs run here, before atlasing: they
                        // measure the source pack as authored (fresh reads), so running them
                        // early changes no figure; they get a generous budget because an
                        // incomplete analysis must not be acted on. The repack itself runs
                        // after the atlas and cross-rigid stages (below).
                        RunVanillaTextureCoverage(
                            state,
                            cancellationToken,
                            progress,
                            budget: SharedRepackAnalysisBudget);
                        RunUnitTextureRepack(
                            state,
                            cancellationToken,
                            progress,
                            SharedRepackAnalysisBudget);
                    }
                }

                // Cross-rigid merging is opt-in: it rewrites VMDs and adds
                // generated geometry for a small measured benefit.
                if (crossRigidMerges || crossRigidOnly)
                {
                    phaseStopwatch.Restart();
                    AnalyzeCrossRigidMergeOpportunities(
                        state,
                        atlasVmdRoots,
                        cancellationToken,
                        progress);
                    state.PhaseDurations["Analyze cross-rigid merges"] =
                        phaseStopwatch.Elapsed;

                    phaseStopwatch.Restart();
                    EmitSelectedCrossRigidMerges(
                        state,
                        cancellationToken,
                        progress);
                    state.PhaseDurations["Emit cross-rigid merges"] =
                        phaseStopwatch.Elapsed;
                }

                // Atlasing runs after cross-rigid merging, which keeps first pick of the meshes:
                // the rigids the cross-rigid writer built from are not atlas candidates, and the
                // generated cross-rigid WSModels count as users of the textures they still name.
                if (buildAtlases && !crossRigidOnly)
                {
                    phaseStopwatch.Restart();
                    discoveredCandidates = ExcludeCrossRigidChangedAtlasCandidates(
                        state,
                        discoveredCandidates);
                    if (shareAtlasesAcrossVmds)
                    {
                        ProcessPackWideAtlases(
                            state,
                            discoveredCandidates,
                            cancellationToken,
                            progress);
                    }
                    else
                    {
                        for (var i = 0; i < atlasVmdRoots.Count; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var rootCandidates = discoveredCandidates
                                .Where(x =>
                                    x.RootVmdPath.Equals(
                                        atlasVmdRoots[i],
                                        StringComparison.OrdinalIgnoreCase))
                                .ToList();
                            ProcessVmd(
                                state,
                                atlasVmdRoots[i],
                                rootCandidates,
                                cancellationToken,
                                progress);
                        }

                        ProcessDirectAssetAtlases(
                            state,
                            discoveredCandidates
                                .Where(IsDirectAssetCandidate)
                                .ToList(),
                            cancellationToken,
                            progress);
                    }

                    state.PhaseDurations["Plan and build atlases"] =
                        phaseStopwatch.Elapsed;
                }

                if (state.SharedTextureRepackEnabled)
                {
                    // After atlasing and cross-rigid merging, before the merge and geometry
                    // optimization, which then see the repacked rigids and materials. It only
                    // acts on what the earlier stages did not change: atlas-changed and
                    // cross-rigid rigids are skipped, and the rigids the cross-rigid writer
                    // generated block the sets whose textures they still use.
                    RunSharedTextureRepack(state, cancellationToken, progress);
                }

                if (mergeCompatibleMeshes && !crossRigidOnly)
                {
                    phaseStopwatch.Restart();
                    MergeCompatibleMeshes(
                        state,
                        cancellationToken,
                        progress);
                    state.PhaseDurations["Merge compatible meshes"] =
                        phaseStopwatch.Elapsed;
                }

                if (optimizeGeometry && !crossRigidOnly)
                {
                    phaseStopwatch.Restart();
                    // Only rigids already rewritten by a merge, an atlas or a
                    // cross-rigid payload are optimized. Extending this to every
                    // source-owned gameplay rigid (SelectNoAtlasGeometryOptimizationRigids)
                    // re-saved 353 extra rigids on the reference pack for 0.5 % fewer
                    // vertices, so it is not used.
                    OptimizeGeometry(
                        state,
                        cancellationToken,
                        progress);
                    state.PhaseDurations["Optimize geometry"] = phaseStopwatch.Elapsed;
                }

                CommitEditorLodRemadeRigids(state);
                phaseStopwatch.Restart();
                SaveModifiedDocuments(state, cancellationToken, progress);
                state.PhaseDurations["Write modified assets"] = phaseStopwatch.Elapsed;

                if (!crossRigidOnly)
                {
                    ReportProgress(progress, "Scanning rewritten dependencies");
                    phaseStopwatch.Restart();
                    var currentReachable = CollectReachableAssetFiles(
                        state,
                        output,
                        assetDependencyRoots,
                        cancellationToken,
                        progress,
                        "Scanning rewritten dependencies");
                    state.PhaseDurations["Scan rewritten dependencies"] =
                        phaseStopwatch.Elapsed;

                    phaseStopwatch.Restart();
                    PruneUnusedAssetFiles(
                        state,
                        originalReachable,
                        currentReachable,
                        cancellationToken,
                        progress);
                    state.PhaseDurations["Prune unused assets"] =
                        phaseStopwatch.Elapsed;
                    FinalizeSharedTextureRepack(state);

                    // Output BCn residency only serves the atlas value accounting, so a run
                    // without atlases skips the final scan as well.
                    if (buildAtlases)
                    {
                        // The pre-prune dependency scan is needed to decide what to remove, but it
                        // still contains superseded source textures. Re-scan after pruning so the
                        // reported output residency and source-retirement accounting describe the
                        // pack that will actually be saved.
                        ReportProgress(progress, "Scanning final dependencies");
                        phaseStopwatch.Restart();
                        var finalReachable = CollectReachableAssetFiles(
                            state,
                            output,
                            assetDependencyRoots,
                            cancellationToken,
                            progress,
                            "Scanning final dependencies");
                        state.PhaseDurations["Scan final dependencies"] =
                            phaseStopwatch.Elapsed;

                        phaseStopwatch.Restart();
                        ReportProgress(progress, "Estimating output BCn residency");
                        state.OutputBcnResidency = CalculateBcnTextureResidency(
                            state,
                            output,
                            finalReachable,
                            cancellationToken);
                        state.PhaseDurations["Estimate output BCn residency"] =
                            phaseStopwatch.Elapsed;
                    }
                }

                if (!crossRigidOnly)
                {
                    // Reads the post-atlas, post-merge, post-prune output. Report-only
                    // unless the downscale option is on.
                    RunTextureDownscale(state, cancellationToken, progress);

                    // Last writer: lossless compaction of what is in the output now. Does
                    // nothing unless its option is on.
                    RunTextureCompaction(state, cancellationToken, progress);

                    // Report-only and independent of the atlas/downscale options: measures
                    // the source pack as authored, so it runs after them without seeing
                    // their changes.
                    // (Already done before the shared texture repack when that is on.)
                    if (state.VanillaTextureCoverage == null)
                        RunVanillaTextureCoverage(state, cancellationToken, progress);

                    // Report-only; combines the coverage collection per gameplay root.
                    if (state.UnitTextureRepack == null)
                        RunUnitTextureRepack(state, cancellationToken, progress);
                }

                phaseStopwatch.Restart();
                ValidateOutput(state, vmdRoots, cancellationToken, progress);
                state.PhaseDurations["Validate output"] = phaseStopwatch.Elapsed;

                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(progress, "Saving output pack", item: Path.GetFileName(outputPath));
                phaseStopwatch.Restart();
                var game = GameInformationDatabase.GetGameById(request.Game);
                var summary = BuildSummary(state);
                var rigidsToCompress = options.CompressRigidModels
                    ? SelectRigidModelsForLz4(output, cancellationToken)
                    : null;
                FileCompression.ForcedLz4Files = rigidsToCompress;
                FileCompression.ForcedLz4Level = RigidLz4Level;
                try
                {
                    _packFileService.SavePackContainer(output, request.OutputWritePath ?? outputPath, game);
                }
                finally
                {
                    FileCompression.ForcedLz4Files = null;
                    FileCompression.ForcedLz4Level = K4os.Compression.LZ4.LZ4Level.L00_FAST;
                }

                state.PhaseDurations["Save output pack"] = phaseStopwatch.Elapsed;
                state.TotalElapsed = totalStopwatch.Elapsed;

                WriteReport(state, vmdRoots, succeeded: true, failure: null);

                return new BatchResult(
                    atlasVmdRoots.Count,
                    state.ProcessedMeshes.Count,
                    state.GeneratedTexturePaths.Count,
                    state.RemovedFiles.Count,
                    GetEffectiveSkippedMeshCount(state),
                    reportPath)
                {
                    ConsumerPackCount = state.Consumers?.Packs.Count ?? 0,
                    ExternallyUsedFileCount = state.Consumers?.Used.Count ?? 0,
                    FrozenFileCount = state.Consumers?.Frozen.Count ?? 0,
                    Warnings = state.Consumers?.Warnings.ToList() ?? [],
                    ConsumerPacks = state.Consumers == null ? [] : DescribeConsumerPacks(state.Consumers, state.ResolutionProviders),
                    Summary = summary,
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (state != null)
                {
                    state.TotalElapsed = totalStopwatch.Elapsed;
                    try
                    {
                        WriteReport(state, vmdRoots, succeeded: false, failure: ex);
                    }
                    catch
                    {
                        // Keep the original conversion/validation exception.
                    }
                }

                throw;
            }
            finally
            {
                state?.VanillaAtlasConsumerCache?.Save();
                if (output != null)
                    _packFileService.ReleaseOutputContainer(output);
            }
        }

        // What the run saved, from the finished output container.
        private static PackOptimizerSummary BuildSummary(BatchState state)
        {
            static long TextureBytes(IPackFileContainer container)
                => container.GetAllFiles()
                    .Where(item => item.Key.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                    .Sum(item => item.Value.DataSource.Size);

            var generatedRigids = state.Output.GetAllFiles().Keys.Count(path =>
                path.EndsWith(".rigid_model_v2", StringComparison.OrdinalIgnoreCase) && !state.Source.ContainsFile(path));
            var drawCalls = state.CrossRigidPayloadSelectionEntries
                .Where(entry => entry.Accepted)
                .Sum(entry => entry.MarginalExpectedArmyDrawCallsEliminated);
            return new PackOptimizerSummary(
                TextureBytes(state.Source) - TextureBytes(state.Output),
                Math.Round(drawCalls, 1),
                generatedRigids);
        }

        // The mod manager's staging compressor LZ4-compresses a rigid_model_v2 of at least 256 KiB
        // when the frame, with the 4-byte size in front, is at most 75 % of the file. The same rule
        // and the same payload layout decide here, with the pack writer's own LZ4 encoder.
        private const int RigidLz4MinimumBytes = 256 * 1024;
        private const double RigidLz4MaximumRatioPercent = 75;
        // Rigid models are compressed with a higher LZ4 level than the pack writer's fast default: the
        // staging compressor's encoder (lz4_flex) and K4os differ by a few percent per file, and the
        // higher level keeps every file that the staging rule would accept.
        private const K4os.Compression.LZ4.LZ4Level RigidLz4Level = K4os.Compression.LZ4.LZ4Level.L03_HC;

        private static HashSet<string> SelectRigidModelsForLz4(IPackFileContainer output, CancellationToken cancellationToken)
        {
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, file) in output.GetAllFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!path.EndsWith(".rigid_model_v2", StringComparison.OrdinalIgnoreCase) ||
                    file.DataSource.Size < RigidLz4MinimumBytes)
                {
                    continue;
                }

                var data = file.DataSource.ReadData();
                if (data.Length < RigidLz4MinimumBytes)
                    continue;

                var compressed = FileCompression.CompressLz4(data, RigidLz4Level);

                if (compressed.Length * 100.0 / data.Length <= RigidLz4MaximumRatioPercent)
                    selected.Add(path);
            }

            return selected;
        }

        public static string BuildOutputPath(string sourcePath)
        {
            var directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
            var stem = Path.GetFileNameWithoutExtension(sourcePath);
            return Path.Combine(directory, stem + "_atlas.pack");
        }

        public static string BuildReportPath(string outputPath)
        {
            var directory = Path.GetDirectoryName(outputPath) ?? string.Empty;
            var stem = Path.GetFileNameWithoutExtension(outputPath);
            return Path.Combine(directory, stem + "_report.txt");
        }

        private static List<string> SelectAtlasVmdRoots(
            IReadOnlyList<string> validatedVmdRoots,
            IEnumerable<string> gameplayUsedVmds,
            bool atlasAllVmds)
        {
            if (atlasAllVmds)
                return validatedVmdRoots.ToList();

            var used = gameplayUsedVmds
                .Select(Normalize)
                .Where(path => path.Length != 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return validatedVmdRoots
                .Where(path => used.Contains(Normalize(path)))
                .ToList();
        }

        private static (List<string> ValidRoots, List<MalformedVmdEntry> MalformedRoots) ValidateVmdRoots(
            BatchState state,
            IPackFileContainer source,
            IReadOnlyList<string> vmdPaths,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var validRoots = new List<string>(vmdPaths.Count);
            var malformedRoots = new List<MalformedVmdEntry>();

            for (var i = 0; i < vmdPaths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Normalize(vmdPaths[i]);
                if (i == 0 || i == vmdPaths.Count - 1 || i % 25 == 0)
                {
                    ReportProgress(
                        progress,
                        "Validating VMD roots",
                        i + 1,
                        vmdPaths.Count,
                        path);
                }

                var file = source.FindFile(path);
                if (file == null)
                    continue;

                try
                {
                    _ = GetVmd(state, source, path, file);
                    validRoots.Add(path);
                }
                catch (Exception ex) when (
                    ex is InvalidOperationException or
                    XmlException or
                    FormatException or
                    ArgumentException)
                {
                    malformedRoots.Add(new MalformedVmdEntry(
                        path,
                        ex.Message.Replace("\r", " ").Replace("\n", " ")));
                }
            }

            return (validRoots, malformedRoots);
        }

        private static bool TryParseIndex(XmlNode node, string attributeName, out int value)
        {
            value = -1;
            var attribute = node.Attributes?[attributeName];
            return attribute != null && int.TryParse(attribute.Value, out value);
        }

        private static string BuildAtlasStem(string atlasScopeKey, int batchIndex)
        {
            const string packPrefix = "pack:";
            var isPackWide = atlasScopeKey.StartsWith(packPrefix, StringComparison.OrdinalIgnoreCase);
            var stemSource = isPackWide
                ? atlasScopeKey[packPrefix.Length..]
                : Path.GetFileNameWithoutExtension(atlasScopeKey);
            var stem = SafeName(stemSource);
            var hash = StableHash(Normalize(atlasScopeKey));
            return $"{stem}_{hash}_atlas_{batchIndex:D3}";
        }

        private static bool HasGeneratedAtlasOutput(IPackFileContainer source)
            => source.GetAllFiles().Keys.Any(path =>
            {
                var normalized = Normalize(path);
                return normalized.StartsWith(
                           AtlasDirectory + "\\",
                           StringComparison.OrdinalIgnoreCase) &&
                       Path.GetFileName(normalized).Contains(
                           "_atlas_",
                           StringComparison.OrdinalIgnoreCase);
            });

        private static bool IsAlreadyGeneratedAtlasCandidate(AtlasCandidate candidate)
            => AtlasChannels.Any(channel =>
                Normalize(GetTexturePath(candidate.MaterialDocument, channel.Slot))
                    .StartsWith(
                        AtlasDirectory + "\\",
                        StringComparison.OrdinalIgnoreCase));

        private static string BuildMaterialPath(string originalPath, MeshKey key)
        {
            var normalized = Normalize(originalPath);
            var directory = Path.GetDirectoryName(normalized) ?? string.Empty;
            var stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(normalized));
            var hash = StableHash($"{key.GeometryPath}|{key.LodIndex}|{key.PartIndex}");
            var fileName = $"{SafeName(stem)}_atlas_{hash}.xml.material";
            return Normalize(Path.Combine(directory, fileName));
        }

        private static string SafeName(string value)
        {
            var safe = new string(value
                .ToLowerInvariant()
                .Select(x => char.IsLetterOrDigit(x) ? x : '_')
                .ToArray())
                .Trim('_');
            return string.IsNullOrWhiteSpace(safe) ? "asset" : safe;
        }

        private static string StableHash(string value)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
        }

        private static string ContentHash(string value)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(bytes);
        }

        private static string GetMaterialRenderingIdentity(XmlDocument material)
        {
            var normalized = new XmlDocument();
            normalized.LoadXml(material.OuterXml);

            // The material name is descriptive identity, not render state. Keeping it in the
            // dedupe signature prevents otherwise-identical atlas materials from being shared.
            var nameNode = normalized.SelectSingleNode("/material/name");
            nameNode?.ParentNode?.RemoveChild(nameNode);

            // Pack resource paths are case-insensitive and may be authored with either slash
            // convention. Normalize only those path-bearing fields; shader variants and all
            // other material parameters remain part of the strict rendering identity.
            NormalizeMaterialResourcePaths(normalized);

            return normalized.OuterXml;
        }

        private static void NormalizeMaterialResourcePaths(XmlDocument material)
        {
            var shaderNode = material.SelectSingleNode("/material/shader");
            if (shaderNode != null)
                shaderNode.InnerText = Normalize(shaderNode.InnerText);

            var textureNodes = material.SelectNodes("/material/textures/texture");
            if (textureNodes == null)
                return;

            foreach (XmlNode textureNode in textureNodes)
            {
                var sourceNode = textureNode.SelectSingleNode("source");
                if (sourceNode != null)
                {
                    sourceNode.InnerText = Normalize(sourceNode.InnerText);
                    continue;
                }

                if (textureNode.ChildNodes.Count == 1 &&
                    textureNode.FirstChild?.NodeType == XmlNodeType.Text)
                {
                    textureNode.InnerText = Normalize(textureNode.InnerText);
                }
            }
        }

        private static bool IsTexturePlaceholder(string? path)
            => Normalize(path).Equals("mask_path", StringComparison.OrdinalIgnoreCase);

        private static bool IsIgnorableUnresolvedAtlasTexture(
            string slot,
            string? path)
        {
            if (!slot.Equals("t_xml_mask", StringComparison.OrdinalIgnoreCase))
                return false;

            var normalized = Normalize(path);
            return normalized.Equals("test_mask.dds", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(@"\test_mask.dds", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsKnownConstantTexturePath(string? path)
            => KnownConstantTexturePaths.Contains(Normalize(path));

        private static bool ShouldProbeUniformTexture(
            string? path,
            int width,
            int height)
        {
            var normalized = Normalize(path);
            if (IsKnownConstantTexturePath(normalized))
                return true;

            // WH3 ships many tiny shared defaults in commontextures (default_base_colour,
            // default_diffuse, default_material_map, default colours, flat normals, etc.).
            // Probe small common textures generically so new/default aliases do not require
            // a brittle hard-coded filename list. TryGetUniformColor remains the final guard.
            return normalized.StartsWith(@"commontextures\", StringComparison.OrdinalIgnoreCase) &&
                   width > 0 &&
                   height > 0 &&
                   (long)width * height <= MaxAutomaticCommonTextureConstantProbePixels;
        }

        private static string Normalize(string? path)
            => string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : path.Trim().Replace('/', '\\').Replace("\\\\", "\\").ToLowerInvariant();

        public sealed record TextureAtlasPackProgress(
            string Phase,
            int Current = 0,
            int Total = 0,
            string? Item = null);

        private static bool IsEnabledEnvironmentVariable(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return value != null &&
                   (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("on", StringComparison.OrdinalIgnoreCase));
        }

        private static void AddPhaseDuration(
            BatchState state,
            string phase,
            TimeSpan elapsed)
        {
            if (state.PhaseDurations.TryGetValue(phase, out var existing))
                state.PhaseDurations[phase] = existing + elapsed;
            else
                state.PhaseDurations[phase] = elapsed;
        }

        private static void AddCrossRigidTimingDuration(
            BatchState state,
            string phase,
            TimeSpan elapsed)
        {
            if (state.CrossRigidTimingDetails.TryGetValue(
                    phase,
                    out var existing))
            {
                state.CrossRigidTimingDetails[phase] = existing + elapsed;
            }
            else
            {
                state.CrossRigidTimingDetails[phase] = elapsed;
            }
        }

        private static void ReportProgress(
            IProgress<TextureAtlasPackProgress>? progress,
            string phase,
            int current = 0,
            int total = 0,
            string? item = null)
            => progress?.Report(new TextureAtlasPackProgress(phase, current, total, item));

        private static void ReportPeriodicProgress(
            IProgress<TextureAtlasPackProgress>? progress,
            string phase,
            int current,
            int total,
            string item)
        {
            if (current == 1 || current == total || current % 16 == 0)
            {
                ReportProgress(progress, phase, current, total, item);
            }
        }
    }
}
