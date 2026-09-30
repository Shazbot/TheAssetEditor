using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Editors.ImportExport.Importing.Importers.PngToDds;
using Editors.ImportExport.Importing.Importers.PngToDds.Helpers;
using Editors.ImportExport.TextureAtlas;
using Shared.Core.ErrorHandling;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Utility;
using Shared.Core.Services;
using Shared.Core.Settings;
using Shared.GameFormats;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.MaterialHeaders;
using Shared.GameFormats.RigidModel.Types;
using Shared.GameFormats.RigidModel.Vertex;
using Shared.GameFormats.Vmd;
using static Shared.GameFormats.Vmd.VariantMeshDefinition;

namespace Editors.KitbasherEditor.Services
{
    public sealed class PackTextureAtlasBatchService
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
        private const double AtlasValueGateExpectedDrawEpsilon = 0.000001;
        private const double MaxReachableBcnGrowthRatio = 0.50;
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

        private static readonly HashSet<string> KnownConstantTexturePaths =
        [
            @"commontextures\default_black.dds",
            @"commontextures\default_white.dds",
            @"commontextures\default_normal.dds",
        ];

        private readonly IPackFileService _packFileService;
        private readonly IPackFileContainerLoader _packFileContainerLoader;
        private readonly ApplicationSettingsService _settingsService;
        private readonly IStandardDialogs _standardDialogs;

        public PackTextureAtlasBatchService(
            IPackFileService packFileService,
            IPackFileContainerLoader packFileContainerLoader,
            ApplicationSettingsService settingsService,
            IStandardDialogs standardDialogs)
        {
            _packFileService = packFileService;
            _packFileContainerLoader = packFileContainerLoader;
            _settingsService = settingsService;
            _standardDialogs = standardDialogs;
        }

        public void Run()
        {
            if (_settingsService.CurrentSettings.CurrentGame != GameTypeEnum.Warhammer3)
            {
                _standardDialogs.ShowDialogBox(
                    "Pack texture atlasing currently supports Warhammer 3 packs only.",
                    "Texture Atlas Pack");
                return;
            }

            var browse = _standardDialogs.ShowSystemOpenFileDialog(false, "Pack files|*.pack");
            if (!browse.Result || browse.FilePaths.Count == 0)
                return;

            var sourcePath = browse.FilePaths[0];
            var outputPath = BuildOutputPath(sourcePath);

            BatchResult? result = null;
            var progressWindow = new TextureAtlasProgressWindow((
                mergeCompatibleMeshes,
                shareAtlasesAcrossVmds,
                optimizeGeometry,
                atlasAllVmds,
                cancellationToken,
                progress) =>
            {
                result = Process(
                    sourcePath,
                    outputPath,
                    atlasMeshesWithMissingTextures: null,
                    cancellationToken: cancellationToken,
                    progress: progress,
                    mergeCompatibleMeshes: mergeCompatibleMeshes,
                    shareAtlasesAcrossVmds: shareAtlasesAcrossVmds,
                    optimizeGeometry: optimizeGeometry,
                    atlasAllVmds: atlasAllVmds);
            });

            if (System.Windows.Application.Current?.MainWindow != null)
                progressWindow.Owner = System.Windows.Application.Current.MainWindow;

            progressWindow.ShowDialog();

            if (progressWindow.Failure != null)
            {
                _standardDialogs.ShowExceptionWindow(
                    progressWindow.Failure,
                    "Failed to create texture atlas pack.");
                return;
            }

            if (progressWindow.WasCancelled || result == null)
                return;

            _standardDialogs.ShowDialogBox(
                $"Texture atlas pack created successfully.\n\n" +
                $"Output: {outputPath}\n" +
                $"Atlas VMD roots: {result.VmdCount}\n" +
                $"Mesh parts atlased: {result.AtlasedMeshCount}\n" +
                $"Atlas textures generated: {result.GeneratedTextureCount}\n" +
                $"Unused asset files removed: {result.RemovedFileCount}\n" +
                $"Mesh parts skipped: {result.SkippedMeshCount}\n" +
                $"Report: {result.ReportPath}",
                "Texture Atlas Pack");
        }

        public BatchResult Process(
            string sourcePath,
            string outputPath,
            bool? atlasMeshesWithMissingTextures = false,
            CancellationToken cancellationToken = default,
            IProgress<TextureAtlasPackProgress>? progress = null,
            bool mergeCompatibleMeshes = false,
            bool shareAtlasesAcrossVmds = true,
            bool optimizeGeometry = false,
            bool atlasAllVmds = false)
        {
            // Kept for API compatibility with existing callers. Atlasing meshes with genuine
            // unresolved secondary textures is no longer allowed because UV0 is shared.
            _ = atlasMeshesWithMissingTextures;

            var reportPath = BuildReportPath(outputPath);
            var totalStopwatch = Stopwatch.StartNew();
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, "Loading source pack", item: Path.GetFileName(sourcePath));
            IPackFileContainer? output = null;
            BatchState? state = null;
            List<string> vmdRoots = [];
            List<MalformedVmdEntry> malformedVmdRoots = [];

            try
            {
                var source = _packFileContainerLoader.CreateFromPackFile(
                    PackFileContainerType.Normal,
                    sourcePath,
                    loadAsReadOnly: true);
                cancellationToken.ThrowIfCancellationRequested();

                var sourcePaths = source.GetAllFiles().Keys.ToList();

                var outputName = Path.GetFileNameWithoutExtension(outputPath);
                output = _packFileService.CreateNewPackFileContainer(
                    outputName,
                    PackFileVersion.PFH5,
                    PackFileCAType.MOD,
                    setEditablePack: false);
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
                state.UnitCategoryResolution = Wh3UnitCategoryResolver.Resolve(
                    _packFileService,
                    source,
                    vmdRoots,
                    childVmdsByVmd,
                    cancellationToken);
                if (!atlasAllVmds &&
                    !state.UnitCategoryResolution.IsGameplayResolutionHealthy)
                {
                    throw new InvalidOperationException(
                        "Gameplay-used atlas population could not be resolved safely: " +
                        state.UnitCategoryResolution.GameplayResolutionHealthMessage +
                        " Fix the WH3 DB/schema resolution problem, or explicitly use pack-wide " +
                        "atlas mode if gameplay filtering is not required.");
                }

                state.ArmyResidencyModel = BuildArmyResidencyModel(state, state.UnitCategoryResolution);
                state.PhaseDurations["Resolve unit categories"] = phaseStopwatch.Elapsed;

                var atlasVmdRoots = SelectAtlasVmdRoots(
                    vmdRoots,
                    state.UnitCategoryResolution.UsagesByVmd.Keys,
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
                phaseStopwatch.Restart();
                BuildWsUsageIndex(state, cancellationToken, progress);
                state.PhaseDurations["Index WSModels"] = phaseStopwatch.Elapsed;

                phaseStopwatch.Restart();
                ReportProgress(progress, "Estimating source BCn residency");
                state.SourceBcnResidency = CalculateBcnTextureResidency(
                    state,
                    source,
                    originalReachable,
                    cancellationToken);
                state.PhaseDurations["Estimate source BCn residency"] = phaseStopwatch.Elapsed;

                phaseStopwatch.Restart();
                var candidateDiscovery = DiscoverAtlasCandidates(
                    state,
                    atlasVmdRoots,
                    shareAtlasesAcrossVmds,
                    cancellationToken,
                    progress);
                state.PhaseDurations["Discover atlas candidates"] = phaseStopwatch.Elapsed;

                // UV0 is shared by every material texture channel. If any real
                // secondary texture is unresolved, remapping UV0 while preserving that old
                // texture path would make it sample with atlas UVs and corrupt rendering.
                // Such meshes are therefore always skipped. The only unresolved sentinel
                // normalized to "absent" earlier is t_xml_mask/test_mask.dds.
                var discoveredCandidates = ApplyMissingTextureDecision(
                    state,
                    candidateDiscovery.Candidates);

                phaseStopwatch.Restart();
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
                state.PhaseDurations["Plan and build atlases"] = phaseStopwatch.Elapsed;

                if (mergeCompatibleMeshes)
                {
                    phaseStopwatch.Restart();
                    MergeCompatibleMeshes(state, cancellationToken, progress);
                    state.PhaseDurations["Merge compatible meshes"] = phaseStopwatch.Elapsed;
                }

                if (optimizeGeometry)
                {
                    phaseStopwatch.Restart();
                    OptimizeGeometry(state, cancellationToken, progress);
                    state.PhaseDurations["Optimize geometry"] = phaseStopwatch.Elapsed;
                }

                phaseStopwatch.Restart();
                SaveModifiedDocuments(state, cancellationToken, progress);
                state.PhaseDurations["Write modified assets"] = phaseStopwatch.Elapsed;

                ReportProgress(progress, "Scanning rewritten dependencies");
                phaseStopwatch.Restart();
                var currentReachable = CollectReachableAssetFiles(
                    state,
                    output,
                    assetDependencyRoots,
                    cancellationToken,
                    progress,
                    "Scanning rewritten dependencies");
                state.PhaseDurations["Scan rewritten dependencies"] = phaseStopwatch.Elapsed;

                phaseStopwatch.Restart();
                ReportProgress(progress, "Estimating output BCn residency");
                state.OutputBcnResidency = CalculateBcnTextureResidency(
                    state,
                    output,
                    currentReachable,
                    cancellationToken);
                state.PhaseDurations["Estimate output BCn residency"] = phaseStopwatch.Elapsed;

                phaseStopwatch.Restart();
                PruneUnusedAssetFiles(
                    state,
                    originalReachable,
                    currentReachable,
                    cancellationToken,
                    progress);
                state.PhaseDurations["Prune unused assets"] = phaseStopwatch.Elapsed;

                phaseStopwatch.Restart();
                ValidateOutput(state, vmdRoots, cancellationToken, progress);
                state.PhaseDurations["Validate output"] = phaseStopwatch.Elapsed;

                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(progress, "Saving output pack", item: Path.GetFileName(outputPath));
                phaseStopwatch.Restart();
                var game = GameInformationDatabase.GetGameById(GameTypeEnum.Warhammer3);
                _packFileService.SavePackContainer(output, outputPath, false, game);
                state.PhaseDurations["Save output pack"] = phaseStopwatch.Elapsed;
                state.TotalElapsed = totalStopwatch.Elapsed;

                WriteReport(state, vmdRoots, succeeded: true, failure: null);

                return new BatchResult(
                    atlasVmdRoots.Count,
                    state.ProcessedMeshes.Count,
                    state.GeneratedTexturePaths.Count,
                    state.RemovedFiles.Count,
                    GetEffectiveSkippedMeshCount(state),
                    reportPath);
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
                if (output != null)
                    _packFileService.UnloadPackContainer(output, force: true);
            }
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

        private void BuildWsUsageIndex(
            BatchState state,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var wsPaths = state.Source.GetAllFiles().Keys
                .Where(x => Path.GetExtension(x).Equals(".wsmodel", StringComparison.OrdinalIgnoreCase))
                .ToList();

            for (var wsIndex = 0; wsIndex < wsPaths.Count; wsIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wsPath = wsPaths[wsIndex];
                if (wsIndex == 0 || wsIndex == wsPaths.Count - 1 || wsIndex % 10 == 0)
                {
                    ReportProgress(
                        progress,
                        "Indexing WSModels",
                        wsIndex + 1,
                        wsPaths.Count,
                        wsPath);
                }

                XmlDocument? doc;
                try
                {
                    doc = GetWsDocument(state, wsPath);
                }
                catch (Exception ex) when (
                    ex is InvalidOperationException or
                    XmlException or
                    FormatException or
                    ArgumentException)
                {
                    RecordMalformedWsModel(state, wsPath, ex);
                    continue;
                }

                if (doc == null)
                    continue;

                var geometryPath = Normalize(doc.SelectSingleNode("/model/geometry")?.InnerText);
                if (string.IsNullOrWhiteSpace(geometryPath) || state.Source.FindFile(geometryPath) == null)
                    continue;

                var materialNodes = doc.SelectNodes("/model/materials/material");
                if (materialNodes == null)
                    continue;

                foreach (XmlNode node in materialNodes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryParseIndex(node, "lod_index", out var lodIndex) ||
                        !TryParseIndex(node, "part_index", out var partIndex))
                        continue;

                    var materialPath = Normalize(node.InnerText);
                    var key = new MeshKey(geometryPath, lodIndex, partIndex);
                    if (!state.Usages.TryGetValue(key, out var usages))
                    {
                        usages = [];
                        state.Usages[key] = usages;
                    }

                    usages.Add(new WsUsage(wsPath, doc, node, materialPath));
                }
            }
        }

        private List<MissingTextureDependency> FindMissingTextureDependencies(
            BatchState state,
            IReadOnlyList<string> vmdRoots,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var reachableWsModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var vmdPath in vmdRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                reachableWsModels.UnionWith(
                    GetReachableWsModels(state, vmdPath, cancellationToken));
            }

            var result = new List<MissingTextureDependency>();
            var usagesList = state.Usages.ToList();

            for (var usageIndex = 0; usageIndex < usagesList.Count; usageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (key, usages) = usagesList[usageIndex];
                if (usageIndex == 0 || usageIndex == usagesList.Count - 1 || usageIndex % 10 == 0)
                {
                    ReportProgress(
                        progress,
                        "Checking texture dependencies",
                        usageIndex + 1,
                        usagesList.Count,
                        key.ToString());
                }
                var reachableUsages = usages
                    .Where(x => reachableWsModels.Contains(x.WsModelPath))
                    .ToList();
                if (reachableUsages.Count == 0)
                    continue;

                var materialPaths = usages
                    .Select(x => x.MaterialPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (materialPaths.Count != 1)
                    continue;

                var materialFile = FindForRead(state, materialPaths[0]);
                if (materialFile == null)
                    continue;

                XmlDocument materialDoc;
                try
                {
                    materialDoc = GetMaterialDocument(state, materialPaths[0], materialFile);
                }
                catch
                {
                    continue;
                }

                var shaderPath = materialDoc.SelectSingleNode("/material/shader")?.InnerText ?? string.Empty;
                if (shaderPath.Contains("emissive", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (materialDoc.SelectNodes("/material/textures/texture")?
                        .Cast<XmlNode>()
                        .Any(x => GetTextureSlot(x).Contains("emissive", StringComparison.OrdinalIgnoreCase)) == true)
                {
                    continue;
                }

                var primaryPath = GetTexturePath(materialDoc, "t_xml_base_colour");
                if (string.IsNullOrWhiteSpace(primaryPath) || FindForRead(state, primaryPath) == null)
                    continue;

                foreach (var channel in AtlasChannels.Skip(1))
                {
                    var texturePath = GetTexturePath(materialDoc, channel.Slot);
                    if (string.IsNullOrWhiteSpace(texturePath) || IsTexturePlaceholder(texturePath))
                        continue;

                    if (FindForRead(state, texturePath) == null)
                    {
                        if (IsIgnorableUnresolvedAtlasTexture(channel.Slot, texturePath))
                            continue;

                        result.Add(new MissingTextureDependency(
                            key,
                            channel.Slot,
                            texturePath));
                    }
                }
            }

            return result
                .Distinct()
                .OrderBy(x => x.TexturePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Key.LodIndex)
                .ThenBy(x => x.Key.PartIndex)
                .ThenBy(x => x.Slot, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string BuildMissingTextureDetails(
            IReadOnlyList<MissingTextureDependency> missingTextures)
        {
            var sb = new StringBuilder();

            foreach (var group in missingTextures.GroupBy(
                         x => x.TexturePath,
                         StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(group.Key);
                foreach (var dependency in group)
                {
                    sb.AppendLine(
                        $"  {dependency.Key.GeometryPath} [lod {dependency.Key.LodIndex}, part {dependency.Key.PartIndex}] " +
                        $"({dependency.Slot})");
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        private CandidateDiscoveryResult DiscoverAtlasCandidates(
            BatchState state,
            IReadOnlyList<string> vmdRoots,
            bool packWide,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var candidates = new List<AtlasCandidate>();
            var missingTextures = new List<MissingTextureDependency>();
            var inspectedKeys = packWide ? new HashSet<MeshKey>() : null;

            for (var vmdIndex = 0; vmdIndex < vmdRoots.Count; vmdIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                candidates.AddRange(CollectVmdCandidates(
                    state,
                    vmdRoots[vmdIndex],
                    vmdIndex + 1,
                    vmdRoots.Count,
                    cancellationToken,
                    progress,
                    inspectedKeys,
                    missingTextures));
            }

            var directAssetPaths = state.UnitCategoryResolution?
                .DirectAssetUsagesByPath.Keys
                .Where(path => state.Source.ContainsFile(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? [];
            candidates.AddRange(CollectDirectAssetCandidates(
                state,
                directAssetPaths,
                cancellationToken,
                progress,
                inspectedKeys,
                missingTextures));

            return new CandidateDiscoveryResult(
                candidates,
                missingTextures
                    .Distinct()
                    .OrderBy(x => x.TexturePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Key.LodIndex)
                    .ThenBy(x => x.Key.PartIndex)
                    .ThenBy(x => x.Slot, StringComparer.OrdinalIgnoreCase)
                    .ToList());
        }

        private static List<AtlasCandidate> ApplyMissingTextureDecision(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates)
        {
            var result = new List<AtlasCandidate>(candidates.Count);

            foreach (var candidate in candidates)
            {
                if (candidate.MissingTextures.Count == 0)
                {
                    result.Add(candidate);
                    continue;
                }

                var firstMissing = candidate.MissingTextures[0];
                RecordSkip(
                    state,
                    candidate.RootVmdPath,
                    candidate.Key,
                    candidate.Usages[0].AssetPath,
                    $"{firstMissing.Slot} texture could not be resolved; mesh cannot be safely atlased because UV0 is shared across channels: {firstMissing.TexturePath}");
            }

            return result;
        }

        private void ProcessVmd(
            BatchState state,
            string rootVmdPath,
            List<AtlasCandidate> candidates,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            candidates = candidates
                .Where(x => !state.ProcessedMeshes.Contains(x.Key))
                .ToList();
            candidates = ApplyTexelDensityScaling(state, candidates);
            candidates = AlignSharedUvIslandCuts(state, candidates);

            var batches = CreateBatches(
                state,
                candidates,
                packWide: false,
                cancellationToken: cancellationToken,
                progress: progress);

            ProcessAtlasBatches(
                state,
                rootVmdPath,
                rootVmdPath,
                batches,
                cancellationToken,
                progress);
        }

        private void ProcessDirectAssetAtlases(
            BatchState state,
            List<AtlasCandidate> candidates,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            candidates = candidates
                .Where(candidate => !state.ProcessedMeshes.Contains(candidate.Key))
                .ToList();
            if (candidates.Count == 0)
                return;

            candidates = ApplyTexelDensityScaling(state, candidates);
            candidates = AlignSharedUvIslandCuts(state, candidates);
            var batches = CreateBatches(
                state,
                candidates,
                packWide: false,
                cancellationToken: cancellationToken,
                progress: progress);
            ProcessAtlasBatches(
                state,
                "direct-engine-assets",
                "Direct engine assets",
                batches,
                cancellationToken,
                progress);
        }

        private static bool IsDirectAssetCandidate(AtlasCandidate candidate)
            => candidate.Usages.Any(usage => !string.IsNullOrWhiteSpace(usage.EmbeddedRigidPath)) ||
               candidate.Usages.Any(usage =>
                   !string.IsNullOrWhiteSpace(usage.WsModelPath) &&
                   Path.GetExtension(usage.WsModelPath)
                       .Equals(".wsmodel", StringComparison.OrdinalIgnoreCase) &&
                   usage.WsModelPath.Equals(
                       candidate.RootVmdPath,
                       StringComparison.OrdinalIgnoreCase));

        private void ProcessPackWideAtlases(
            BatchState state,
            List<AtlasCandidate> candidates,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            state.PackWideCandidateCount = candidates.Count;
            candidates = ApplyTexelDensityScaling(state, candidates);
            candidates = AlignSharedUvIslandCuts(state, candidates);

            var stopwatch = Stopwatch.StartNew();
            var batches = CreateBatches(
                state,
                candidates,
                packWide: true,
                cancellationToken: cancellationToken,
                progress: progress);
            AddPhaseDuration(state, "Plan atlas batches", stopwatch.Elapsed);

            stopwatch.Restart();
            var packName = Path.GetFileNameWithoutExtension(state.SourcePath);
            ProcessAtlasBatches(
                state,
                $"pack:{packName}",
                "Pack-wide",
                batches,
                cancellationToken,
                progress);
            AddPhaseDuration(state, "Execute atlas batches", stopwatch.Elapsed);
        }

        private void ProcessAtlasBatches(
            BatchState state,
            string atlasScopeKey,
            string progressScope,
            IReadOnlyList<List<AtlasCandidate>> batches,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(
                    progress,
                    "Building texture atlases",
                    batchIndex + 1,
                    batches.Count,
                    $"{progressScope} — batch {batchIndex + 1}");

                ProcessBatch(
                    state,
                    atlasScopeKey,
                    progressScope,
                    batches[batchIndex],
                    cancellationToken,
                    progress);
            }
        }

        private List<AtlasCandidate> CollectVmdCandidates(
            BatchState state,
            string rootVmdPath,
            int vmdIndex,
            int vmdCount,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress,
            HashSet<MeshKey>? inspectedKeys = null,
            List<MissingTextureDependency>? missingTextureDependencies = null)
        {
            ReportProgress(progress, "Discovering atlas candidates", vmdIndex, vmdCount, rootVmdPath);
            var wsModels = GetReachableWsModels(state, rootVmdPath, cancellationToken);
            var candidates = new List<AtlasCandidate>();
            var localInspectedKeys = inspectedKeys ?? new HashSet<MeshKey>();

            foreach (var wsPath in wsModels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wsFile = state.Source.FindFile(wsPath);
                if (wsFile == null ||
                    !TryGetWsDocumentForTraversal(
                        state,
                        state.Source,
                        wsPath,
                        wsFile,
                        out var doc))
                {
                    continue;
                }

                var geometryPath = Normalize(doc.SelectSingleNode("/model/geometry")?.InnerText);
                if (string.IsNullOrWhiteSpace(geometryPath))
                    continue;

                var rmv = GetRmv(state, geometryPath);
                if (rmv == null)
                    continue;

                var materialNodes = doc.SelectNodes("/model/materials/material");
                if (materialNodes == null)
                    continue;

                foreach (XmlNode node in materialNodes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryParseIndex(node, "lod_index", out var lodIndex) ||
                        !TryParseIndex(node, "part_index", out var partIndex) ||
                        lodIndex < 0 || lodIndex >= rmv.ModelList.Length ||
                        partIndex < 0 || partIndex >= rmv.ModelList[lodIndex].Length)
                    {
                        continue;
                    }

                    var key = new MeshKey(geometryPath, lodIndex, partIndex);
                    ReportProgress(
                        progress,
                        "Discovering atlas candidates",
                        vmdIndex,
                        vmdCount,
                        $"{rootVmdPath} — {key}");

                    if (state.ProcessedMeshes.Contains(key) || !localInspectedKeys.Add(key))
                        continue;

                    if (!state.Usages.TryGetValue(key, out var usages) || usages.Count == 0)
                        continue;

                    var materialPaths = usages
                        .Select(x => x.MaterialPath)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    // The rigid owns UV0, not the WSModel. If the same rigid mesh is used with
                    // different materials, rewriting its UVs would require atlasing every one of
                    // those material variants with the same placement. Keep that uncommon case
                    // untouched rather than silently breaking one of the users.
                    if (materialPaths.Count != 1)
                    {
                        RecordSkip(
                            state,
                            rootVmdPath,
                            key,
                            wsPath,
                            $"Rigid mesh is shared by WSModels using {materialPaths.Count} different materials.");
                        continue;
                    }

                    var candidate = TryCreateCandidate(
                        state,
                        rootVmdPath,
                        key,
                        rmv.ModelList[lodIndex][partIndex],
                        usages,
                        missingTextureDependencies,
                        out var skipReason);
                    if (candidate == null)
                    {
                        RecordSkip(state, rootVmdPath, key, wsPath, skipReason);
                        continue;
                    }

                    candidates.Add(candidate);
                }
            }

            return candidates;
        }

        private List<AtlasCandidate> CollectDirectAssetCandidates(
            BatchState state,
            IReadOnlyList<string> assetPaths,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress,
            HashSet<MeshKey>? inspectedKeys,
            List<MissingTextureDependency>? missingTextureDependencies)
        {
            var candidates = new List<AtlasCandidate>();
            var localInspectedKeys = inspectedKeys ?? new HashSet<MeshKey>();

            for (var assetIndex = 0; assetIndex < assetPaths.Count; assetIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var assetPath = Normalize(assetPaths[assetIndex]);
                ReportProgress(
                    progress,
                    "Discovering direct engine assets",
                    assetIndex + 1,
                    assetPaths.Count,
                    assetPath);

                var extension = Path.GetExtension(assetPath);
                if (extension.Equals(".rigid_model_v2", StringComparison.OrdinalIgnoreCase))
                {
                    var file = state.Source.FindFile(assetPath);
                    if (file == null)
                        continue;

                    RmvFile rmv;
                    try
                    {
                        rmv = GetRmv(state, assetPath) ??
                            throw new InvalidDataException("Rigid model could not be loaded.");
                    }
                    catch (Exception ex)
                    {
                        RecordDirectAssetSkip(
                            state,
                            assetPath,
                            $"Rigid engine asset could not be loaded: {ex.Message}");
                        continue;
                    }

                    for (var lodIndex = 0; lodIndex < rmv.ModelList.Length; lodIndex++)
                    {
                        for (var partIndex = 0;
                             partIndex < rmv.ModelList[lodIndex].Length;
                             partIndex++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var key = new MeshKey(assetPath, lodIndex, partIndex);
                            if (state.ProcessedMeshes.Contains(key) || !localInspectedKeys.Add(key))
                                continue;

                            var model = rmv.ModelList[lodIndex][partIndex];
                            var usage = new WsUsage(
                                string.Empty,
                                null,
                                null,
                                BuildEmbeddedMaterialPath(assetPath, lodIndex, partIndex),
                                assetPath);
                            var candidate = TryCreateCandidate(
                                state,
                                assetPath,
                                key,
                                model,
                                [usage],
                                missingTextureDependencies,
                                out var skipReason,
                                BuildEmbeddedMaterialDocument(model.Material));
                            if (candidate == null)
                            {
                                RecordSkip(state, assetPath, key, string.Empty, skipReason);
                                continue;
                            }

                            candidates.Add(candidate);
                        }
                    }

                    continue;
                }

                if (!extension.Equals(".wsmodel", StringComparison.OrdinalIgnoreCase))
                    continue;

                XmlDocument document;
                try
                {
                    document = GetWsDocument(state, assetPath)
                        ?? throw new InvalidDataException("WSModel could not be loaded.");
                }
                catch (Exception ex)
                {
                    RecordDirectAssetSkip(
                        state,
                        assetPath,
                        $"WSModel engine asset could not be loaded: {ex.Message}");
                    continue;
                }

                var geometryPath = Normalize(
                    document.SelectSingleNode("/model/geometry")?.InnerText);
                if (geometryPath.Length == 0)
                    continue;

                var rmvForWsModel = GetRmv(state, geometryPath);
                var materialNodes = document.SelectNodes("/model/materials/material");
                if (rmvForWsModel == null || materialNodes == null)
                    continue;

                foreach (XmlNode materialNode in materialNodes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryParseIndex(materialNode, "lod_index", out var lodIndex) ||
                        !TryParseIndex(materialNode, "part_index", out var partIndex) ||
                        lodIndex < 0 ||
                        lodIndex >= rmvForWsModel.ModelList.Length ||
                        partIndex < 0 ||
                        partIndex >= rmvForWsModel.ModelList[lodIndex].Length)
                    {
                        continue;
                    }

                    var key = new MeshKey(geometryPath, lodIndex, partIndex);
                    if (state.ProcessedMeshes.Contains(key) || !localInspectedKeys.Add(key))
                        continue;

                    var materialPath = Normalize(materialNode.InnerText);
                    if (materialPath.Length == 0)
                        continue;

                    var usage = new WsUsage(assetPath, document, materialNode, materialPath);
                    var candidate = TryCreateCandidate(
                        state,
                        assetPath,
                        key,
                        rmvForWsModel.ModelList[lodIndex][partIndex],
                        [usage],
                        missingTextureDependencies,
                        out var skipReason);
                    if (candidate == null)
                    {
                        RecordSkip(state, assetPath, key, assetPath, skipReason);
                        continue;
                    }

                    candidates.Add(candidate);
                }
            }

            return candidates;
        }

        private AtlasCandidate? TryCreateCandidate(
            BatchState state,
            string rootVmdPath,
            MeshKey key,
            RmvModel model,
            List<WsUsage> usages,
            List<MissingTextureDependency>? missingTextureDependencies,
            out string skipReason,
            XmlDocument? materialDocumentOverride = null)
        {
            skipReason = string.Empty;
            var materialPath = usages[0].MaterialPath;
            XmlDocument materialDoc;
            if (materialDocumentOverride != null)
            {
                if (TryGetUnsafeEmbeddedUvTexture(model.Material, out var unsafeTexture))
                {
                    skipReason =
                        $"Embedded rigid material contains UV0-dependent texture type " +
                        $"{unsafeTexture.TexureType} that is not rewritten by the atlas path: " +
                        $"{unsafeTexture.Path}";
                    return null;
                }

                materialDoc = materialDocumentOverride;
            }
            else
            {
                var materialFile = FindForRead(state, materialPath);
                if (materialFile == null)
                {
                    skipReason = $"Material file could not be resolved: {materialPath}";
                    return null;
                }

                try
                {
                    materialDoc = GetMaterialDocument(state, materialPath, materialFile);
                }
                catch (Exception ex)
                {
                    skipReason = $"Material XML could not be parsed: {materialPath} ({ex.Message})";
                    return null;
                }
            }

            var shaderPath = materialDoc.SelectSingleNode("/material/shader")?.InnerText ?? string.Empty;
            if (shaderPath.Contains("emissive", StringComparison.OrdinalIgnoreCase))
            {
                skipReason = $"Emissive shader is intentionally not atlased: {shaderPath}";
                return null;
            }

            if (materialDoc.SelectNodes("/material/textures/texture")?
                    .Cast<XmlNode>()
                    .Any(x => GetTextureSlot(x).Contains("emissive", StringComparison.OrdinalIgnoreCase)) == true)
            {
                skipReason = "Material contains an emissive texture slot and must retain its original UV0 mapping.";
                return null;
            }

            var primaryPath = GetTexturePath(materialDoc, "t_xml_base_colour");
            if (string.IsNullOrWhiteSpace(primaryPath))
            {
                skipReason = "Material has no t_xml_base_colour texture.";
                return null;
            }

            var candidateMissingTextures = new List<MissingTextureDependency>();
            var resolvedChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var constantChannels = new Dictionary<string, TextureAtlasConstantColor>(
                StringComparer.OrdinalIgnoreCase);
            var channelDimensions = new Dictionary<string, (int Width, int Height)>(
                StringComparer.OrdinalIgnoreCase);

            int? primaryWidth = null;
            int? primaryHeight = null;

            foreach (var channel in AtlasChannels)
            {
                var path = GetTexturePath(materialDoc, channel.Slot);
                if (string.IsNullOrWhiteSpace(path))
                {
                    if (channel.Slot.Equals("t_xml_base_colour", StringComparison.OrdinalIgnoreCase))
                    {
                        skipReason = "Material has no t_xml_base_colour texture.";
                        return null;
                    }

                    continue;
                }

                if (IsTexturePlaceholder(path))
                    continue;

                var file = FindForRead(state, path);
                if (file == null)
                {
                    if (channel.Slot.Equals("t_xml_base_colour", StringComparison.OrdinalIgnoreCase))
                    {
                        skipReason = $"Base-colour texture could not be resolved: {path}";
                        return null;
                    }

                    if (IsIgnorableUnresolvedAtlasTexture(channel.Slot, path))
                        continue;

                    var missing = new MissingTextureDependency(
                        key,
                        channel.Slot,
                        path);
                    candidateMissingTextures.Add(missing);
                    missingTextureDependencies?.Add(missing);
                    continue;
                }

                try
                {
                    var inspection = GetTextureInspection(state, path, file);

                    if (channel.Slot.Equals("t_xml_base_colour", StringComparison.OrdinalIgnoreCase))
                    {
                        primaryWidth = inspection.Width;
                        primaryHeight = inspection.Height;
                    }

                    if (inspection.IsUniformConstant)
                    {
                        constantChannels[channel.Slot] = inspection.ConstantColor;
                        continue;
                    }

                    // The shared placement is expressed in primary BaseColour pixels. Record
                    // each secondary channel's native resolution separately so its physical
                    // atlas can be sized independently without changing the normalized UVs.
                    channelDimensions[channel.Slot] = (inspection.Width, inspection.Height);
                    resolvedChannels.Add(channel.Slot);
                }
                catch (Exception ex)
                {
                    skipReason = $"{channel.Slot} is not a usable DDS: {path} ({ex.Message})";
                    return null;
                }
            }

            if (!resolvedChannels.Contains("t_xml_base_colour") &&
                !constantChannels.ContainsKey("t_xml_base_colour"))
            {
                skipReason = $"Base-colour texture could not be prepared: {primaryPath}";
                return null;
            }

            var width = primaryWidth
                ?? throw new InvalidOperationException("Base-colour dimensions were not resolved.");
            var height = primaryHeight
                ?? throw new InvalidOperationException("Base-colour dimensions were not resolved.");

            UvBounds bounds;
            TextureAtlasUvIslandNormalization uvIslandAnalysis;
            UvIslandNormalization? uvIslandNormalization;
            try
            {
                var originalBounds = GetUvBounds(model);
                var uvs = model.Mesh.VertexList
                    .Select(vertex => (U: vertex.Uv.X, V: vertex.Uv.Y))
                    .ToArray();
                uvIslandAnalysis = TextureAtlasBuilder.CalculateDisconnectedUvIslandNormalization(
                    uvs,
                    model.Mesh.IndexList);
                uvIslandNormalization = BuildUvIslandNormalization(
                    width,
                    height,
                    originalBounds,
                    uvIslandAnalysis,
                    allowEqualCrop: false);
                bounds = uvIslandNormalization?.NormalizedBounds ?? originalBounds;
            }
            catch (Exception ex)
            {
                skipReason = $"UV0 could not be prepared safely: {ex.Message}";
                return null;
            }

            return new AtlasCandidate(
                rootVmdPath,
                key,
                model,
                usages,
                materialPath,
                materialDoc,
                width,
                height,
                bounds,
                resolvedChannels,
                constantChannels,
                channelDimensions,
                candidateMissingTextures,
                uvIslandAnalysis,
                uvIslandNormalization,
                AtlasResolutionScale: 1.0);
        }

        private static bool IsEmbeddedAtlasTextureTypeSafe(TextureType textureType)
            => textureType is
                TextureType.BaseColour or
                TextureType.Diffuse or
                TextureType.MaterialMap or
                TextureType.Normal or
                TextureType.Mask;

        private static bool TryGetUnsafeEmbeddedUvTexture(
            IRmvMaterial material,
            out RmvTexture unsafeTexture)
        {
            foreach (var texture in material.GetAllTextures())
            {
                if (string.IsNullOrWhiteSpace(texture.Path))
                    continue;
                if (IsEmbeddedAtlasTextureTypeSafe(texture.TexureType))
                    continue;

                unsafeTexture = texture;
                return true;
            }

            unsafeTexture = default;
            return false;
        }

        private static XmlDocument BuildEmbeddedMaterialDocument(IRmvMaterial material)
        {
            var document = new XmlDocument();
            document.LoadXml(
                $"<material><name>embedded_{material.MaterialId}</name>" +
                $"<shader>embedded/{material.MaterialId}</shader><textures /></material>");
            var texturesNode = document.SelectSingleNode("/material/textures")!;
            var writtenSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var texture in material.GetAllTextures())
            {
                if (!TryGetEmbeddedAtlasSlot(texture.TexureType, out var slot) ||
                    string.IsNullOrWhiteSpace(texture.Path) ||
                    !writtenSlots.Add(slot))
                {
                    continue;
                }

                var textureNode = document.CreateElement("texture");
                var slotNode = document.CreateElement("slot");
                slotNode.InnerText = slot;
                textureNode.AppendChild(slotNode);
                var sourceNode = document.CreateElement("source");
                sourceNode.InnerText = texture.Path;
                textureNode.AppendChild(sourceNode);
                texturesNode.AppendChild(textureNode);
            }

            return document;
        }

        private static bool TryGetEmbeddedAtlasSlot(TextureType textureType, out string slot)
        {
            slot = textureType switch
            {
                TextureType.BaseColour or TextureType.Diffuse => "t_xml_base_colour",
                TextureType.MaterialMap => "t_xml_material_map",
                TextureType.Normal => "t_xml_normal",
                TextureType.Mask => "t_xml_mask",
                _ => string.Empty,
            };
            return slot.Length != 0;
        }

        private static void ApplyEmbeddedMaterialTexture(
            IRmvMaterial material,
            string slot,
            string path)
        {
            var textureTypes = slot switch
            {
                "t_xml_base_colour" => new[] { TextureType.BaseColour, TextureType.Diffuse },
                "t_xml_material_map" => new[] { TextureType.MaterialMap },
                "t_xml_normal" => new[] { TextureType.Normal },
                "t_xml_mask" => new[] { TextureType.Mask },
                _ => [],
            };

            foreach (var textureType in textureTypes)
            {
                if (material.GetTexture(textureType).HasValue)
                {
                    material.SetTexture(textureType, path);
                    return;
                }
            }
        }

        private static string BuildEmbeddedMaterialPath(
            string assetPath,
            int lodIndex,
            int partIndex)
            => $"embedded:{assetPath}:lod:{lodIndex}:part:{partIndex}";

        private static void RecordDirectAssetSkip(
            BatchState state,
            string assetPath,
            string reason)
        {
            state.DirectAssetSkipMessages.Add($"{assetPath}: {reason}");
        }

        private static TextureInspection GetTextureInspection(
            BatchState state,
            string texturePath,
            PackFile file)
        {
            texturePath = Normalize(texturePath);
            if (state.TextureInspections.TryGetValue(texturePath, out var cached))
                return cached;

            var dimensions = TextureAtlasBuilder.GetDimensions(file.DataSource.PeekData(20));
            var constantColor = default(TextureAtlasConstantColor);
            var isUniformConstant = false;
            if (ShouldProbeUniformTexture(texturePath, dimensions.Width, dimensions.Height))
            {
                try
                {
                    var bytes = file.DataSource.ReadData();
                    isUniformConstant = TextureAtlasBuilder.TryGetUniformColor(bytes, out constantColor);
                }
                catch
                {
                    // Uniform probing is only an optimization. Unsupported/odd DDS variants
                    // must fall back to the normal atlas path rather than making the mesh ineligible.
                    isUniformConstant = false;
                    constantColor = default;
                }
            }

            if (isUniformConstant)
                state.UniformConstantTexturePaths.Add(texturePath);

            var inspection = new TextureInspection(
                dimensions.Width,
                dimensions.Height,
                isUniformConstant,
                constantColor);
            state.TextureInspections[texturePath] = inspection;
            return inspection;
        }

        private static List<AtlasCandidate> ApplyTexelDensityScaling(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates)
        {
            if (candidates.Count == 0)
                return [];

            var densities = new Dictionary<MeshKey, double>();
            foreach (var candidate in candidates)
            {
                if (TryCalculateTexelDensity(candidate, out var density))
                    densities[candidate.Key] = density;
            }

            if (densities.Count < 2)
                return candidates.ToList();

            var globalReference = Percentile(
                densities.Values.OrderBy(value => value).ToArray(),
                TexelDensityReferencePercentile);
            var densitiesByRoot = candidates
                .Where(candidate => densities.ContainsKey(candidate.Key))
                .GroupBy(candidate => Normalize(candidate.RootVmdPath))
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(candidate => densities[candidate.Key])
                        .OrderBy(value => value)
                        .ToArray(),
                    StringComparer.OrdinalIgnoreCase);

            var result = new List<AtlasCandidate>(candidates.Count);
            foreach (var candidate in candidates)
            {
                if (!densities.TryGetValue(candidate.Key, out var density))
                {
                    result.Add(candidate);
                    continue;
                }

                var rootKey = Normalize(candidate.RootVmdPath);
                var rootValues = densitiesByRoot.GetValueOrDefault(rootKey);
                var reference = rootValues is { Length: >= 4 }
                    ? Percentile(rootValues, TexelDensityReferencePercentile)
                    : globalReference;
                var maximumDensity = Math.Max(
                    double.Epsilon,
                    reference * TexelDensityOutlierMultiplier);
                var requestedScale = Math.Min(1.0, maximumDensity / density);
                var maxDimension = Math.Max(candidate.Width, candidate.Height);
                var minimumScaleForSize = maxDimension <= 0
                    ? 1.0
                    : Math.Min(
                        1.0,
                        MinAtlasResolutionScaleDimension / (double)maxDimension);
                requestedScale = Math.Clamp(
                    requestedScale,
                    Math.Max(MinAtlasResolutionScale, minimumScaleForSize),
                    1.0);

                // Keep physical atlas sizes mip-friendly. A scale bucket also prevents tiny
                // density differences from producing unstable output dimensions across runs.
                var scale = requestedScale <= 0.375
                    ? 0.25
                    : requestedScale <= 0.75
                        ? 0.5
                        : 1.0;

                if (scale >= 1.0)
                {
                    result.Add(candidate);
                    continue;
                }

                state.TexelDensityScaledMeshes++;
                state.TexelDensityOriginalPixelArea = checked(
                    state.TexelDensityOriginalPixelArea +
                    (long)candidate.Width * candidate.Height);
                state.TexelDensityScaledPixelArea = checked(
                    state.TexelDensityScaledPixelArea +
                    (long)Math.Max(1, (int)Math.Ceiling(candidate.Width * scale)) *
                    Math.Max(1, (int)Math.Ceiling(candidate.Height * scale)));
                state.TexelDensityScaleCounts[scale] =
                    state.TexelDensityScaleCounts.GetValueOrDefault(scale) + 1;

                result.Add(candidate with { AtlasResolutionScale = scale });
            }

            return result;
        }

        private static bool TryCalculateTexelDensity(
            AtlasCandidate candidate,
            out double density)
        {
            density = 0;
            var vertices = candidate.Model.Mesh.VertexList;
            var indices = candidate.Model.Mesh.IndexList;
            if (indices.Length < 3 || indices.Length % 3 != 0)
                return false;

            double worldArea = 0;
            double uvPixelArea = 0;
            for (var index = 0; index < indices.Length; index += 3)
            {
                var ia = indices[index];
                var ib = indices[index + 1];
                var ic = indices[index + 2];
                if (ia >= vertices.Length || ib >= vertices.Length || ic >= vertices.Length)
                    return false;

                var a = vertices[ia];
                var b = vertices[ib];
                var c = vertices[ic];
                var ab = b.GetPosistionAsVec3() - a.GetPosistionAsVec3();
                var ac = c.GetPosistionAsVec3() - a.GetPosistionAsVec3();
                var triangleWorldArea =
                    Microsoft.Xna.Framework.Vector3.Cross(ab, ac).Length() * 0.5;
                if (triangleWorldArea <= 0)
                    continue;

                var auv = a.Uv;
                var buv = b.Uv;
                var cuv = c.Uv;
                var triangleUvArea = Math.Abs(
                    (buv.X - auv.X) * (cuv.Y - auv.Y) -
                    (buv.Y - auv.Y) * (cuv.X - auv.X)) * 0.5;
                if (triangleUvArea <= 0)
                    continue;

                worldArea += triangleWorldArea;
                uvPixelArea += triangleUvArea * candidate.Width * (double)candidate.Height;
            }

            if (worldArea <= double.Epsilon || uvPixelArea <= double.Epsilon)
                return false;

            density = Math.Sqrt(uvPixelArea / worldArea);
            return double.IsFinite(density) && density > 0;
        }

        private static double Percentile(
            IReadOnlyList<double> sortedValues,
            double percentile)
        {
            if (sortedValues.Count == 0)
                return 0;
            if (sortedValues.Count == 1)
                return sortedValues[0];

            var position = Math.Clamp(percentile, 0, 1) * (sortedValues.Count - 1);
            var lower = (int)Math.Floor(position);
            var upper = (int)Math.Ceiling(position);
            if (lower == upper)
                return sortedValues[lower];

            var fraction = position - lower;
            return sortedValues[lower] * (1 - fraction) +
                   sortedValues[upper] * fraction;
        }

        private static (int Width, int Height) GetScaledAtlasDimensions(
            AtlasCandidate candidate,
            int width,
            int height)
        {
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height));

            var scale = candidate.AtlasResolutionScale;
            if (!double.IsFinite(scale) || scale <= 0 || scale > 1.0)
            {
                throw new InvalidOperationException(
                    $"Invalid atlas resolution scale {scale} for {candidate.Key}.");
            }

            return (
                Math.Max(1, checked((int)Math.Ceiling(width * scale))),
                Math.Max(1, checked((int)Math.Ceiling(height * scale))));
        }

        private static List<List<AtlasCandidate>> CreateBatches(
            BatchState state,
            List<AtlasCandidate> candidates,
            bool packWide,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            List<AtlasCandidate> planningCandidates;
            if (packWide)
            {
                var rootsByMesh = BuildCandidateRootVmdPaths(state, candidates);
                var localitySignatureBySource = candidates
                    .GroupBy(GetAtlasPlanningSourceIdentity)
                    .ToDictionary(
                        group => group.Key,
                        group =>
                        {
                            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var candidate in group)
                            {
                                if (rootsByMesh.TryGetValue(candidate.Key, out var candidateRoots))
                                    roots.UnionWith(candidateRoots);
                                else
                                    roots.Add(Normalize(candidate.RootVmdPath));
                            }

                            return string.Join(
                                "\u001f",
                                roots.OrderBy(root => root, StringComparer.OrdinalIgnoreCase));
                        });

                planningCandidates = candidates
                    .OrderBy(
                        candidate => localitySignatureBySource[
                            GetAtlasPlanningSourceIdentity(candidate)],
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(BuildAtlasPlanningOrderKey, StringComparer.Ordinal)
                    .ThenBy(x => x.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Key.LodIndex)
                    .ThenBy(x => x.Key.PartIndex)
                    .ToList();
            }
            else
            {
                planningCandidates = candidates;
            }

            var batches = new List<List<AtlasCandidate>>();
            var current = new List<AtlasCandidate>();
            var currentPlanningRepresentatives = new List<AtlasCandidate>();
            var currentPlanningIdentities = new HashSet<AtlasPlanningSourceIdentity>();

            for (var candidateIndex = 0; candidateIndex < planningCandidates.Count; candidateIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = planningCandidates[candidateIndex];
                ReportProgress(
                    progress,
                    "Planning atlas batches",
                    candidateIndex + 1,
                    planningCandidates.Count,
                    candidate.Key.ToString());

                if (!CanCreatePlan(state, [candidate], out var singleError))
                {
                    RecordSkip(
                        state,
                        candidate.RootVmdPath,
                        candidate.Key,
                        candidate.Usages.FirstOrDefault()?.AssetPath ?? string.Empty,
                        $"Atlas planner rejected this mesh: {singleError}");
                    continue;
                }

                var planningIdentity = GetAtlasPlanningSourceIdentity(candidate);

                if (current.Count == 0)
                {
                    current.Add(candidate);
                    currentPlanningRepresentatives.Add(candidate);
                    currentPlanningIdentities.Add(planningIdentity);
                    continue;
                }

                // Exact source/crop duplicates consume no additional atlas space. Keep every
                // candidate in the execution batch, but only one representative in fit checks.
                if (currentPlanningIdentities.Contains(planningIdentity))
                {
                    current.Add(candidate);
                    continue;
                }

                var trialPlanningRepresentatives =
                    currentPlanningRepresentatives.Concat([candidate]).ToList();
                if (CanCreatePlan(state, trialPlanningRepresentatives, out _))
                {
                    current.Add(candidate);
                    currentPlanningRepresentatives.Add(candidate);
                    currentPlanningIdentities.Add(planningIdentity);
                    continue;
                }

                if (current.Count >= 2)
                {
                    batches.Add(current);
                }
                else
                {
                    var orphan = current[0];
                    RecordSkip(
                        state,
                        orphan.RootVmdPath,
                        orphan.Key,
                        orphan.Usages.FirstOrDefault()?.AssetPath ?? string.Empty,
                        "Could not form a compatible multi-mesh atlas before the atlas size/layout limit was reached.");
                }

                current = [candidate];
                currentPlanningRepresentatives = [candidate];
                currentPlanningIdentities = new HashSet<AtlasPlanningSourceIdentity>
                {
                    planningIdentity
                };
            }

            if (current.Count >= 2)
            {
                batches.Add(current);
            }
            else if (current.Count == 1)
            {
                var orphan = current[0];
                RecordSkip(
                    state,
                    orphan.RootVmdPath,
                    orphan.Key,
                    orphan.Usages.FirstOrDefault()?.AssetPath ?? string.Empty,
                    packWide
                        ? "No second compatible mesh was available in the pack-wide atlas candidate set."
                        : "No second compatible mesh was available in this VMD dependency set.");
            }

            var pixelOptimized = OptimizeMaxSizeBatchesForPixelArea(state, batches);
            if (packWide)
            {
                state.ExpectedArmyResidentPixelsBeforeLocality =
                    CalculateExpectedArmyResidentPixels(state, pixelOptimized);
            }

            var localityOptimized = packWide
                ? OptimizeBatchesForVmdLocality(state, pixelOptimized)
                : pixelOptimized;

            if (packWide)
            {
                state.ExpectedArmyResidentPixelsAfterLocality =
                    CalculateExpectedArmyResidentPixels(state, localityOptimized);
            }

            var mergeOptimized = OptimizeBatchesForMergeAffinity(state, localityOptimized);
            var valueOptimized = state.MergeCompatibleMeshesEnabled
                ? FilterBatchesForMergeValue(state, mergeOptimized)
                : mergeOptimized;
            if (packWide)
            {
                state.ExpectedArmyResidentPixelsAfterMergeAware =
                    CalculateExpectedArmyResidentPixels(state, valueOptimized);
            }

            return valueOptimized;
        }

        private static List<List<AtlasCandidate>> OptimizeMaxSizeBatchesForPixelArea(
            BatchState state,
            IReadOnlyList<List<AtlasCandidate>> batches)
        {
            var optimized = new List<List<AtlasCandidate>>();
            foreach (var batch in batches)
                OptimizeMaxSizeBatchForPixelArea(state, batch, optimized);

            return optimized;
        }

        private static void OptimizeMaxSizeBatchForPixelArea(
            BatchState state,
            List<AtlasCandidate> batch,
            List<List<AtlasCandidate>> output)
        {
            if (batch.Count < 4 ||
                !TryGetGeneratedAtlasPixelCost(
                    state,
                    batch,
                    out var parentPixelCost,
                    out var parentTouchesMaxSize) ||
                !parentTouchesMaxSize)
            {
                output.Add(batch);
                return;
            }

            List<AtlasCandidate>? bestLeft = null;
            List<AtlasCandidate>? bestRight = null;
            var bestCombinedPixelCost = parentPixelCost;
            var bestSplitWasNonContiguous = false;
            var validSplitIndices = new List<int>();

            for (var splitIndex = 2; splitIndex <= batch.Count - 2; splitIndex++)
            {
                // Keep identical source/crop identities together. Splitting inside one of these
                // groups only duplicates an atlas placement and cannot improve packing.
                if (GetAtlasPlanningSourceIdentity(batch[splitIndex - 1]) !=
                    GetAtlasPlanningSourceIdentity(batch[splitIndex]))
                {
                    validSplitIndices.Add(splitIndex);
                }
            }

            const int maxOrderedSplitEvaluations = 32;
            IEnumerable<int> splitIndices = validSplitIndices;
            if (validSplitIndices.Count > maxOrderedSplitEvaluations)
            {
                // This is only an optional size optimization. Exhaustively rebuilding two atlas
                // plans for every possible boundary becomes quadratic on very large packs, so
                // sample the ordered boundary set evenly and keep conversion time bounded.
                splitIndices = Enumerable.Range(0, maxOrderedSplitEvaluations)
                    .Select(i => validSplitIndices[
                        i * (validSplitIndices.Count - 1) / (maxOrderedSplitEvaluations - 1)])
                    .Distinct();
            }

            foreach (var splitIndex in splitIndices)
            {
                state.AtlasPixelAreaSplitEvaluations++;

                var left = batch.Take(splitIndex).ToList();
                var right = batch.Skip(splitIndex).ToList();

                if (!TryGetGeneratedAtlasPixelCost(state, left, out var leftCost, out _) ||
                    !TryGetGeneratedAtlasPixelCost(state, right, out var rightCost, out _))
                {
                    continue;
                }

                var combinedCost = checked(leftCost + rightCost);
                if (combinedCost >= bestCombinedPixelCost)
                    continue;

                bestCombinedPixelCost = combinedCost;
                bestLeft = left;
                bestRight = right;
                bestSplitWasNonContiguous = false;
            }

            foreach (var proposal in CreateNonContiguousSplitProposals(batch))
            {
                state.AtlasPixelAreaSplitEvaluations++;
                state.AtlasNonContiguousSplitEvaluations++;

                if (!TryGetGeneratedAtlasPixelCost(state, proposal.Left, out var leftCost, out _) ||
                    !TryGetGeneratedAtlasPixelCost(state, proposal.Right, out var rightCost, out _))
                {
                    continue;
                }

                var combinedCost = checked(leftCost + rightCost);
                if (combinedCost >= bestCombinedPixelCost)
                    continue;

                bestCombinedPixelCost = combinedCost;
                bestLeft = proposal.Left;
                bestRight = proposal.Right;
                bestSplitWasNonContiguous = true;
            }

            if (bestLeft == null || bestRight == null)
            {
                output.Add(batch);
                return;
            }

            state.AtlasPixelAreaOptimizedSplits++;
            var savedPixels = checked(parentPixelCost - bestCombinedPixelCost);
            state.AtlasPixelAreaSavedByOptimizedSplits = checked(
                state.AtlasPixelAreaSavedByOptimizedSplits + savedPixels);
            if (bestSplitWasNonContiguous)
            {
                state.AtlasNonContiguousOptimizedSplits++;
                state.AtlasPixelAreaSavedByNonContiguousSplits = checked(
                    state.AtlasPixelAreaSavedByNonContiguousSplits + savedPixels);
            }

            OptimizeMaxSizeBatchForPixelArea(state, bestLeft, output);
            OptimizeMaxSizeBatchForPixelArea(state, bestRight, output);
        }

        private static List<List<AtlasCandidate>> OptimizeBatchesForVmdLocality(
            BatchState state,
            IReadOnlyList<List<AtlasCandidate>> batches)
        {
            var working = batches.Select(batch => batch.ToList()).ToList();
            if (!state.ShareAtlasesAcrossVmdsEnabled || working.Count == 0)
                return working;

            var allCandidates = working.SelectMany(batch => batch).ToList();
            var rootsByMesh = BuildCandidateRootVmdPaths(state, allCandidates);
            var affinityGroups = BuildMergeAffinityGroups(allCandidates);
            var optimized = new List<List<AtlasCandidate>>();

            foreach (var batch in working)
            {
                OptimizeBatchForVmdLocality(
                    state,
                    batch,
                    rootsByMesh,
                    affinityGroups,
                    optimized);
            }

            return optimized;
        }

        private static Dictionary<MeshKey, HashSet<string>> BuildCandidateRootVmdPaths(
            BatchState state,
            IEnumerable<AtlasCandidate> candidates)
        {
            var rootsByWsModel = new Dictionary<string, HashSet<string>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var (rootVmdPath, reachableWsModels) in state.ReachableWsModelsByRoot)
            {
                var normalizedRoot = Normalize(rootVmdPath);
                foreach (var reachableWsModel in reachableWsModels)
                {
                    var wsModelPath = Normalize(reachableWsModel);
                    if (!rootsByWsModel.TryGetValue(wsModelPath, out var roots))
                    {
                        roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        rootsByWsModel[wsModelPath] = roots;
                    }

                    roots.Add(normalizedRoot);
                }
            }

            var result = new Dictionary<MeshKey, HashSet<string>>();
            foreach (var candidate in candidates)
            {
                var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var usage in candidate.Usages)
                {
                    if (rootsByWsModel.TryGetValue(
                            Normalize(usage.AssetPath),
                            out var usageRoots))
                    {
                        roots.UnionWith(usageRoots);
                    }
                }

                if (roots.Count == 0)
                    roots.Add(Normalize(candidate.RootVmdPath));

                result[candidate.Key] = roots;
            }

            return result;
        }

        private static int GetBatchRootCount(
            IReadOnlyList<AtlasCandidate> candidates,
            IReadOnlyDictionary<MeshKey, HashSet<string>> rootsByMesh)
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                if (rootsByMesh.TryGetValue(candidate.Key, out var candidateRoots))
                    roots.UnionWith(candidateRoots);
                else
                    roots.Add(Normalize(candidate.RootVmdPath));
            }

            return Math.Max(1, roots.Count);
        }

        private static long GetAtlasResidencyProxy(
            long atlasPixels,
            int rootCount)
            => checked(atlasPixels * Math.Max(1, rootCount));

        private static void SetMaximumEntityCount(
            Dictionary<Wh3UnitVisualRole, int> countsByRole,
            Wh3UnitVisualRole role,
            int count)
        {
            if (count <= 0)
                return;

            countsByRole[role] = Math.Max(
                countsByRole.GetValueOrDefault(role),
                count);
        }

        private static ArmyResidencyModel? BuildArmyResidencyModel(
            BatchState state,
            Wh3UnitCategoryResolution? resolution)
        {
            if (resolution == null)
                return null;

            var unitsByCategory = resolution.Scenario.ArmySlotTemplate.Keys.ToDictionary(
                category => category,
                _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            var unitsByVmd = new Dictionary<
                string,
                Dictionary<Wh3ArmyUnitCategory, HashSet<string>>>(
                StringComparer.OrdinalIgnoreCase);
            var entityCountByUnit = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var entityCountByUnitAndRole = new Dictionary<
                string,
                Dictionary<Wh3UnitVisualRole, int>>(StringComparer.OrdinalIgnoreCase);
            var categoryByUnit = new Dictionary<string, Wh3ArmyUnitCategory>(
                StringComparer.OrdinalIgnoreCase);

            // Populate the denominator from the complete DB-derived roster, not from the
            // subset of VMD roots discovered in this pack.
            foreach (var unit in resolution.RosterUnits)
            {
                if (!resolution.Scenario.ArmySlotTemplate.ContainsKey(unit.Category))
                    continue;

                var identity = GetArmyUnitIdentity(unit);
                if (identity.Length == 0)
                    continue;

                unitsByCategory[unit.Category].Add(identity);
                categoryByUnit[identity] = unit.Category;
                var countsByRole = new Dictionary<Wh3UnitVisualRole, int>();
                SetMaximumEntityCount(countsByRole, Wh3UnitVisualRole.Men, unit.VisualCounts.Riders);
                SetMaximumEntityCount(countsByRole, Wh3UnitVisualRole.Mount, unit.VisualCounts.Mounts);
                SetMaximumEntityCount(countsByRole, Wh3UnitVisualRole.Engine, unit.VisualCounts.Engines);
                SetMaximumEntityCount(countsByRole, Wh3UnitVisualRole.Crew, unit.VisualCounts.Crew);
                entityCountByUnitAndRole[identity] = countsByRole;
                entityCountByUnit[identity] = Math.Max(
                    1,
                    countsByRole.Values.DefaultIfEmpty(1).Max());
            }

            foreach (var (vmdPathValue, usages) in resolution.UsagesByVmd)
            {
                var vmdPath = Normalize(vmdPathValue);
                foreach (var usage in usages)
                {
                    if (!resolution.Scenario.ArmySlotTemplate.ContainsKey(usage.Category))
                        continue;

                    var identity = GetArmyUnitIdentity(usage);
                    if (identity.Length == 0)
                        continue;

                    unitsByCategory[usage.Category].Add(identity);
                    var entityCount = Math.Max(1, usage.EntityCount);
                    entityCountByUnit[identity] = Math.Max(
                        entityCountByUnit.GetValueOrDefault(identity, 1),
                        entityCount);

                    if (!entityCountByUnitAndRole.TryGetValue(identity, out var countsByRole))
                    {
                        countsByRole = new Dictionary<Wh3UnitVisualRole, int>();
                        entityCountByUnitAndRole[identity] = countsByRole;
                    }

                    // Every usage carries the full DB-resolved composition.  Record all
                    // components even when a component has no VMD of its own (classic
                    // artillery engines often point directly at a rigid model).
                    SetMaximumEntityCount(
                        countsByRole,
                        Wh3UnitVisualRole.Men,
                        usage.VisualCounts.Riders);
                    SetMaximumEntityCount(
                        countsByRole,
                        Wh3UnitVisualRole.Mount,
                        usage.VisualCounts.Mounts);
                    SetMaximumEntityCount(
                        countsByRole,
                        Wh3UnitVisualRole.Engine,
                        usage.VisualCounts.Engines);
                    SetMaximumEntityCount(
                        countsByRole,
                        Wh3UnitVisualRole.Crew,
                        usage.VisualCounts.Crew);

                    // The usage's role is the component represented by this VMD.  Keep it
                    // even when a synthetic/legacy usage did not carry a complete count set.
                    SetMaximumEntityCount(countsByRole, usage.VisualRole, entityCount);
                    categoryByUnit[identity] = usage.Category;

                    if (!unitsByVmd.TryGetValue(vmdPath, out var byCategory))
                    {
                        byCategory = new Dictionary<Wh3ArmyUnitCategory, HashSet<string>>();
                        unitsByVmd[vmdPath] = byCategory;
                    }

                    if (!byCategory.TryGetValue(usage.Category, out var unitIds))
                    {
                        unitIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        byCategory[usage.Category] = unitIds;
                    }

                    unitIds.Add(identity);
                }
            }

            // Resolve visual probabilities from the DB-referenced VMDs only. Propagated child
            // mappings are useful for atlas reachability but would make a nested VMD look like
            // an independent 100%-probability unit visual.
            var directVmdsByUnitAndRole = new Dictionary<
                string,
                Dictionary<Wh3UnitVisualRole, HashSet<string>>>(
                StringComparer.OrdinalIgnoreCase);
            var directAssetsByUnitAndRole = new Dictionary<
                string,
                Dictionary<
                    Wh3UnitVisualRole,
                    Dictionary<string, double>>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var (vmdPathValue, usages) in resolution.DirectUsagesByVmd)
            {
                var vmdPath = Normalize(vmdPathValue);
                foreach (var usage in usages)
                {
                    if (!resolution.Scenario.ArmySlotTemplate.ContainsKey(usage.Category))
                        continue;

                    var identity = GetArmyUnitIdentity(usage);
                    if (identity.Length == 0)
                        continue;

                    if (!directVmdsByUnitAndRole.TryGetValue(identity, out var directVmdsByRole))
                    {
                        directVmdsByRole = new Dictionary<Wh3UnitVisualRole, HashSet<string>>();
                        directVmdsByUnitAndRole[identity] = directVmdsByRole;
                    }

                    if (!directVmdsByRole.TryGetValue(usage.VisualRole, out var directVmds))
                    {
                        directVmds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        directVmdsByRole[usage.VisualRole] = directVmds;
                    }

                    directVmds.Add(vmdPath);
                }
            }

            foreach (var (assetPathValue, usages) in resolution.DirectAssetUsagesByPath)
            {
                var assetPath = Normalize(assetPathValue);
                foreach (var usage in usages)
                {
                    if (!resolution.Scenario.ArmySlotTemplate.ContainsKey(usage.Category))
                        continue;

                    var identity = GetArmyUnitIdentity(usage);
                    if (identity.Length == 0)
                        continue;

                    unitsByCategory[usage.Category].Add(identity);
                    var entityCount = Math.Max(1, usage.EntityCount);
                    entityCountByUnit[identity] = Math.Max(
                        entityCountByUnit.GetValueOrDefault(identity, 1),
                        entityCount);

                    if (!entityCountByUnitAndRole.TryGetValue(identity, out var countsByRole))
                    {
                        countsByRole = new Dictionary<Wh3UnitVisualRole, int>();
                        entityCountByUnitAndRole[identity] = countsByRole;
                    }

                    SetMaximumEntityCount(countsByRole, Wh3UnitVisualRole.Men, usage.VisualCounts.Riders);
                    SetMaximumEntityCount(countsByRole, Wh3UnitVisualRole.Mount, usage.VisualCounts.Mounts);
                    SetMaximumEntityCount(countsByRole, Wh3UnitVisualRole.Engine, usage.VisualCounts.Engines);
                    SetMaximumEntityCount(countsByRole, Wh3UnitVisualRole.Crew, usage.VisualCounts.Crew);
                    SetMaximumEntityCount(countsByRole, usage.VisualRole, entityCount);
                    categoryByUnit[identity] = usage.Category;

                    if (!unitsByVmd.TryGetValue(assetPath, out var byCategory))
                    {
                        byCategory = new Dictionary<Wh3ArmyUnitCategory, HashSet<string>>();
                        unitsByVmd[assetPath] = byCategory;
                    }

                    if (!byCategory.TryGetValue(usage.Category, out var unitIds))
                    {
                        unitIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        byCategory[usage.Category] = unitIds;
                    }

                    unitIds.Add(identity);

                    if (!directAssetsByUnitAndRole.TryGetValue(identity, out var directAssetsByRole))
                    {
                        directAssetsByRole = new Dictionary<
                            Wh3UnitVisualRole,
                            Dictionary<string, double>>();
                        directAssetsByUnitAndRole[identity] = directAssetsByRole;
                    }

                    if (!directAssetsByRole.TryGetValue(usage.VisualRole, out var directAssets))
                    {
                        directAssets = new Dictionary<string, double>(
                            StringComparer.OrdinalIgnoreCase);
                        directAssetsByRole[usage.VisualRole] = directAssets;
                    }

                    directAssets[assetPath] = Math.Clamp(
                        directAssets.GetValueOrDefault(assetPath) +
                        usage.ScenarioPresenceProbability,
                        0.0,
                        1.0);
                }
            }

            if (!unitsByCategory.Values.Any(units => units.Count != 0))
                return null;

            var occurrenceCache = new Dictionary<string, IReadOnlyDictionary<string, double>>(
                StringComparer.OrdinalIgnoreCase);
            var configurationCache = new Dictionary<string, IReadOnlyList<UnitVisualConfiguration>>(
                StringComparer.OrdinalIgnoreCase);
            var expectedWsModelOccurrencesByUnit =
                new Dictionary<string, ExpectedWsModelOccurrences>(
                    StringComparer.OrdinalIgnoreCase);
            var visualConfigurationsByUnitAndRole = new Dictionary<
                string,
                Dictionary<Wh3UnitVisualRole, IReadOnlyList<UnitVisualConfiguration>>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var (unitId, directVmdsByRole) in directVmdsByUnitAndRole)
            {
                if (directVmdsByRole.Count == 0)
                    continue;

                var accumulatedByWsModel = new Dictionary<
                    string,
                    Dictionary<Wh3UnitVisualRole, double>>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var (role, directVmds) in directVmdsByRole)
                {
                    if (directVmds.Count == 0)
                        continue;

                    var accumulated = new Dictionary<string, double>(
                        StringComparer.OrdinalIgnoreCase);
                    var configurations = new List<UnitVisualConfiguration>();
                    foreach (var vmdPath in directVmds)
                    {
                        var occurrences = GetExpectedWsModelOccurrencesForVmd(
                            state,
                            vmdPath,
                            occurrenceCache,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        foreach (var (wsModelPath, expectedOccurrences) in occurrences)
                        {
                            accumulated[wsModelPath] =
                                accumulated.GetValueOrDefault(wsModelPath) + expectedOccurrences;
                        }

                        configurations.AddRange(GetWsModelConfigurationsForVmd(
                            state,
                            vmdPath,
                            configurationCache,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
                    }

                    if (accumulated.Count == 0)
                        continue;

                    // A unit can have faction-specific direct VMDs for one component. Without
                    // a faction context, treat those visual definitions as equally likely
                    // alternatives, independently for each visual role.
                    var directVariantCount = directVmds.Count;
                    if (configurations.Count != 0)
                    {
                        if (!visualConfigurationsByUnitAndRole.TryGetValue(unitId, out var byRole))
                        {
                            byRole = new Dictionary<Wh3UnitVisualRole, IReadOnlyList<UnitVisualConfiguration>>();
                            visualConfigurationsByUnitAndRole[unitId] = byRole;
                        }

                        byRole[role] = configurations
                            .Select(configuration => configuration with
                            {
                                Probability = configuration.Probability / directVariantCount,
                            })
                            .Where(configuration => configuration.Probability > 0)
                            .ToArray();
                    }
                    foreach (var (wsModelPath, expectedOccurrences) in accumulated)
                    {
                        if (!accumulatedByWsModel.TryGetValue(wsModelPath, out var byRole))
                        {
                            byRole = new Dictionary<Wh3UnitVisualRole, double>();
                            accumulatedByWsModel[wsModelPath] = byRole;
                        }

                        byRole[role] = expectedOccurrences / directVariantCount;
                    }
                }

                if (accumulatedByWsModel.Count != 0)
                {
                    expectedWsModelOccurrencesByUnit[unitId] = new ExpectedWsModelOccurrences(
                        accumulatedByWsModel.ToDictionary(
                            entry => entry.Key,
                            entry => (IReadOnlyDictionary<Wh3UnitVisualRole, double>)entry.Value,
                            StringComparer.OrdinalIgnoreCase));
                }
            }

            foreach (var (unitId, directAssetsByRole) in directAssetsByUnitAndRole)
            {
                if (!expectedWsModelOccurrencesByUnit.TryGetValue(
                        unitId,
                        out var existingOccurrences))
                {
                    existingOccurrences = new ExpectedWsModelOccurrences(
                        new Dictionary<string, IReadOnlyDictionary<Wh3UnitVisualRole, double>>(
                            StringComparer.OrdinalIgnoreCase));
                    expectedWsModelOccurrencesByUnit[unitId] = existingOccurrences;
                }

                var mergedOccurrences = existingOccurrences.ByWsModel.ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value.ToDictionary(
                        roleEntry => roleEntry.Key,
                        roleEntry => roleEntry.Value),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var (role, directAssets) in directAssetsByRole)
                {
                    if (directAssets.Count == 0)
                        continue;

                    // Preserve scenario state/LOD probabilities exactly. Do not
                    // renormalize them: doing so would turn e.g. a 25% destroyed state back
                    // into 100% presence and defeat scenario-driven lifecycle modelling.
                    foreach (var (assetPath, scenarioPresence) in directAssets)
                    {
                        if (scenarioPresence <= 0)
                            continue;

                        if (!mergedOccurrences.TryGetValue(assetPath, out var byRole))
                        {
                            byRole = new Dictionary<Wh3UnitVisualRole, double>();
                            mergedOccurrences[assetPath] = byRole;
                        }

                        byRole[role] = Math.Clamp(
                            byRole.GetValueOrDefault(role) + scenarioPresence,
                            0.0,
                            1.0);
                    }
                }

                expectedWsModelOccurrencesByUnit[unitId] = new ExpectedWsModelOccurrences(
                    mergedOccurrences.ToDictionary(
                        entry => entry.Key,
                        entry => (IReadOnlyDictionary<Wh3UnitVisualRole, double>)entry.Value,
                        StringComparer.OrdinalIgnoreCase));
            }

            return new ArmyResidencyModel(
                unitsByCategory,
                unitsByVmd,
                entityCountByUnit,
                entityCountByUnitAndRole,
                categoryByUnit,
                expectedWsModelOccurrencesByUnit,
                visualConfigurationsByUnitAndRole,
                resolution.Scenario);
        }

        private static IReadOnlyDictionary<string, double> GetExpectedWsModelOccurrencesForVmd(
            BatchState state,
            string vmdPathValue,
            Dictionary<string, IReadOnlyDictionary<string, double>> cache,
            HashSet<string> visiting)
        {
            var vmdPath = Normalize(vmdPathValue);
            if (cache.TryGetValue(vmdPath, out var cached))
                return cached;
            if (!visiting.Add(vmdPath))
                return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var file = state.Source.FindFile(vmdPath);
                if (file == null)
                {
                    var empty = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    cache[vmdPath] = empty;
                    return empty;
                }

                if (!TryGetVmdForTraversal(
                        state,
                        state.Source,
                        vmdPath,
                        file,
                        out var vmd))
                {
                    var empty = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    cache[vmdPath] = empty;
                    return empty;
                }

                var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                AccumulateExpectedWsModelOccurrences(
                    state,
                    vmd,
                    1.0,
                    result,
                    cache,
                    visiting);
                cache[vmdPath] = result;
                return result;
            }
            finally
            {
                visiting.Remove(vmdPath);
            }
        }

        private const int MaxExactVisualConfigurations = 16384;

        private static IReadOnlyList<UnitVisualConfiguration> GetWsModelConfigurationsForVmd(
            BatchState state,
            string vmdPathValue,
            Dictionary<string, IReadOnlyList<UnitVisualConfiguration>> cache,
            HashSet<string> visiting)
        {
            var vmdPath = Normalize(vmdPathValue);
            if (cache.TryGetValue(vmdPath, out var cached))
                return cached;
            if (!visiting.Add(vmdPath))
                return [];

            try
            {
                var file = state.Source.FindFile(vmdPath);
                if (file == null ||
                    !TryGetVmdForTraversal(state, state.Source, vmdPath, file, out var vmd))
                {
                    cache[vmdPath] = [];
                    return [];
                }

                var configurations = ExpandVisualConfigurations(
                    state,
                    vmd,
                    cache,
                    visiting);
                cache[vmdPath] = configurations;
                return configurations;
            }
            finally
            {
                visiting.Remove(vmdPath);
            }
        }

        private static IReadOnlyList<UnitVisualConfiguration> ExpandVisualConfigurations(
            BatchState state,
            VariantMesh mesh,
            Dictionary<string, IReadOnlyList<UnitVisualConfiguration>> cache,
            HashSet<string> visiting)
        {
            var baseModels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(mesh.ModelReference))
            {
                var modelPath = Normalize(mesh.ModelReference);
                if (Path.GetExtension(modelPath)
                        .Equals(".wsmodel", StringComparison.OrdinalIgnoreCase) &&
                    state.Source.ContainsFile(modelPath))
                {
                    baseModels[modelPath] = 1;
                }
            }

            var current = new List<UnitVisualConfiguration>
            {
                new(1.0, baseModels),
            };

            foreach (var slot in mesh.ChildSlots ?? [])
            {
                var slotProbability = ParseVmdSlotProbability(slot.Probability);
                var childMeshCount = slot.ChildMeshes?.Count ?? 0;
                var childReferenceCount = slot.ChildReferences?.Count ?? 0;
                var alternativeCount = childMeshCount + childReferenceCount;
                if (alternativeCount == 0 || slotProbability <= 0)
                    continue;

                var alternatives = new List<UnitVisualConfiguration>();
                if (slotProbability < 1.0)
                {
                    alternatives.Add(new UnitVisualConfiguration(
                        1.0 - slotProbability,
                        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)));
                }

                var alternativeWeight = slotProbability / alternativeCount;
                foreach (var child in slot.ChildMeshes ?? [])
                {
                    var childConfigurations = ExpandVisualConfigurations(
                        state,
                        child,
                        cache,
                        visiting);
                    if (childConfigurations.Count == 0)
                        return [];

                    alternatives.AddRange(childConfigurations.Select(configuration => configuration with
                    {
                        Probability = configuration.Probability * alternativeWeight,
                    }));
                }

                foreach (var reference in slot.ChildReferences ?? [])
                {
                    if (string.IsNullOrWhiteSpace(reference.Reference))
                        return [];

                    var childConfigurations = GetWsModelConfigurationsForVmd(
                        state,
                        reference.Reference,
                        cache,
                        visiting);
                    if (childConfigurations.Count == 0)
                        return [];

                    alternatives.AddRange(childConfigurations.Select(configuration => configuration with
                    {
                        Probability = configuration.Probability * alternativeWeight,
                    }));
                }

                if (alternatives.Count == 0)
                    return [];

                var combined = new List<UnitVisualConfiguration>();
                foreach (var left in current)
                {
                    foreach (var right in alternatives)
                    {
                        var probability = left.Probability * right.Probability;
                        if (probability <= 0)
                            continue;

                        var models = left.WsModelOccurrences.ToDictionary(
                            entry => entry.Key,
                            entry => entry.Value,
                            StringComparer.OrdinalIgnoreCase);
                        foreach (var (path, count) in right.WsModelOccurrences)
                            models[path] = models.GetValueOrDefault(path) + count;

                        combined.Add(new UnitVisualConfiguration(probability, models));
                        if (combined.Count > MaxExactVisualConfigurations)
                            return [];
                    }
                }

                current = MergeEquivalentVisualConfigurations(combined);
                if (current.Count > MaxExactVisualConfigurations)
                    return [];
            }

            return current;
        }

        private static List<UnitVisualConfiguration> MergeEquivalentVisualConfigurations(
            IEnumerable<UnitVisualConfiguration> configurations)
        {
            var merged = new Dictionary<string, UnitVisualConfiguration>(StringComparer.Ordinal);
            foreach (var configuration in configurations)
            {
                var key = string.Join(
                    "\u001f",
                    configuration.WsModelOccurrences
                        .Where(entry => entry.Value > 0)
                        .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(entry => $"{entry.Key}\u001e{entry.Value}"));
                if (merged.TryGetValue(key, out var existing))
                {
                    merged[key] = existing with
                    {
                        Probability = existing.Probability + configuration.Probability,
                    };
                }
                else
                {
                    merged[key] = configuration;
                }
            }

            return merged.Values
                .Where(configuration => configuration.Probability > 0)
                .ToList();
        }

        private static void AccumulateExpectedWsModelOccurrences(
            BatchState state,
            VariantMesh mesh,
            double parentProbability,
            Dictionary<string, double> result,
            Dictionary<string, IReadOnlyDictionary<string, double>> cache,
            HashSet<string> visiting)
        {
            if (parentProbability <= 0)
                return;

            if (!string.IsNullOrWhiteSpace(mesh.ModelReference))
            {
                var modelPath = Normalize(mesh.ModelReference);
                if (Path.GetExtension(modelPath)
                        .Equals(".wsmodel", StringComparison.OrdinalIgnoreCase) &&
                    state.Source.ContainsFile(modelPath))
                {
                    result[modelPath] =
                        result.GetValueOrDefault(modelPath) + parentProbability;
                }
            }

            foreach (var slot in mesh.ChildSlots ?? [])
            {
                var slotProbability = ParseVmdSlotProbability(slot.Probability);
                if (slotProbability <= 0)
                    continue;

                var childMeshCount = slot.ChildMeshes?.Count ?? 0;
                var childReferenceCount = slot.ChildReferences?.Count ?? 0;
                var alternativeCount = childMeshCount + childReferenceCount;
                if (alternativeCount == 0)
                    continue;

                // VMD does not expose a per-child weight. Treat the entries in an active slot as
                // equally likely alternatives, while the slot probability controls whether the
                // slot is populated at all.
                var alternativeProbability =
                    parentProbability * slotProbability / alternativeCount;

                foreach (var child in slot.ChildMeshes ?? [])
                {
                    AccumulateExpectedWsModelOccurrences(
                        state,
                        child,
                        alternativeProbability,
                        result,
                        cache,
                        visiting);
                }

                foreach (var reference in slot.ChildReferences ?? [])
                {
                    if (string.IsNullOrWhiteSpace(reference.Reference))
                        continue;

                    var childOccurrences = GetExpectedWsModelOccurrencesForVmd(
                        state,
                        reference.Reference,
                        cache,
                        visiting);
                    foreach (var (wsModelPath, expectedOccurrences) in childOccurrences)
                    {
                        result[wsModelPath] =
                            result.GetValueOrDefault(wsModelPath) +
                            alternativeProbability * expectedOccurrences;
                    }
                }
            }
        }

        private static double ParseVmdSlotProbability(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 1.0;

            if (!double.TryParse(
                    value.Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var probability))
            {
                return 1.0;
            }

            // Most VMDs use 0..1. Be tolerant of hand-authored percentage-style values.
            if (probability > 1.0 && probability <= 100.0)
                probability /= 100.0;

            return Math.Clamp(probability, 0.0, 1.0);
        }

        private static string GetArmyUnitIdentity(Wh3ResolvedUnitVisual unit)
            => unit.Identity.Trim().ToLowerInvariant();

        private static string GetArmyUnitIdentity(Wh3UnitCategoryUsage usage)
        {
            if (!string.IsNullOrWhiteSpace(usage.MainUnitKey))
                return $"main:{usage.MainUnitKey.Trim().ToLowerInvariant()}";
            if (!string.IsNullOrWhiteSpace(usage.LandUnitKey))
                return $"land:{usage.LandUnitKey.Trim().ToLowerInvariant()}";
            return string.Empty;
        }

        private static string GetArmyUnitIdentity(Wh3UnitDirectAssetUsage usage)
        {
            if (!string.IsNullOrWhiteSpace(usage.MainUnitKey))
                return $"main:{usage.MainUnitKey.Trim().ToLowerInvariant()}";
            if (!string.IsNullOrWhiteSpace(usage.LandUnitKey))
                return $"land:{usage.LandUnitKey.Trim().ToLowerInvariant()}";
            return string.Empty;
        }

        private static HashSet<string> GetBatchRoots(
            IReadOnlyList<AtlasCandidate> candidates,
            IReadOnlyDictionary<MeshKey, HashSet<string>> rootsByMesh)
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                if (rootsByMesh.TryGetValue(candidate.Key, out var candidateRoots))
                    roots.UnionWith(candidateRoots);
                else
                    roots.Add(Normalize(candidate.RootVmdPath));
            }

            return roots;
        }

        private static double GetExpectedArmyResidentPixels(
            BatchState state,
            long atlasPixels,
            IReadOnlyList<AtlasCandidate> candidates,
            IReadOnlyDictionary<MeshKey, HashSet<string>> rootsByMesh)
        {
            if (atlasPixels <= 0)
                return 0;

            var targetWsModels = candidates
                .SelectMany(candidate => candidate.Usages)
                .Select(usage => Normalize(usage.AssetPath))
                .Where(path => path.Length != 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var roots = GetBatchRoots(candidates, rootsByMesh);

            return atlasPixels * GetExpectedArmyResidentProbability(
                state.ArmyResidencyModel,
                targetWsModels,
                roots);
        }

        private static double GetExpectedArmyResidentProbability(
            ArmyResidencyModel? model,
            IReadOnlyCollection<string> targetWsModels,
            IEnumerable<string> fallbackRoots)
        {
            if (model == null)
                return 0;

            var fallbackCoveredByCategory = model.Scenario.ArmySlotTemplate.Keys.ToDictionary(
                category => category,
                _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            foreach (var rootValue in fallbackRoots)
            {
                var root = Normalize(rootValue);
                if (!model.UnitsByVmd.TryGetValue(root, out var unitsForVmd))
                    continue;

                foreach (var (category, unitIds) in unitsForVmd)
                {
                    if (fallbackCoveredByCategory.TryGetValue(category, out var covered))
                        covered.UnionWith(unitIds);
                }
            }

            var notResidentProbability = 1.0;
            foreach (var (category, slotCount) in model.Scenario.ArmySlotTemplate)
            {
                var population = model.UnitsByCategory[category].Count;
                if (population == 0)
                    continue;

                var normalizedTargets = targetWsModels
                    .Select(Normalize)
                    .Where(path => path.Length != 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                double perSlotPresenceProbability = 0;
                foreach (var unitId in model.UnitsByCategory[category])
                {
                    double unitCardPresenceProbability = 0;
                    var notPresentAcrossRoles = 1.0;
                    var exactRoles = new HashSet<Wh3UnitVisualRole>();

                    // Prefer exact VMD configurations for union probability. Summing marginal
                    // probabilities is wrong when target assets can overlap in the same state
                    // (e.g. two independent 50% slots have union 75%, not 100%).
                    if (model.VisualConfigurationsByUnitAndRole.TryGetValue(
                            unitId,
                            out var configurationsByRole))
                    {
                        foreach (var (role, configurations) in configurationsByRole)
                        {
                            if (configurations.Count == 0)
                                continue;

                            exactRoles.Add(role);
                            var perEntityPresenceProbability = configurations
                                .Where(configuration =>
                                    configuration.WsModelOccurrences.Any(entry =>
                                        entry.Value > 0 &&
                                        normalizedTargets.Contains(entry.Key)))
                                .Sum(configuration => configuration.Probability);
                            perEntityPresenceProbability = Math.Clamp(
                                perEntityPresenceProbability,
                                0.0,
                                1.0);
                            if (perEntityPresenceProbability <= 0)
                                continue;

                            var entityCount = Math.Max(
                                1,
                                model.EntityCountByUnitAndRole
                                    .GetValueOrDefault(unitId)?
                                    .GetValueOrDefault(role, 1) ?? 1);
                            notPresentAcrossRoles *= Math.Pow(
                                1.0 - perEntityPresenceProbability,
                                entityCount);
                        }
                    }

                    // Direct assets and any VMD too large for exact enumeration retain the
                    // marginal fallback. Skip roles already evaluated exactly so their target
                    // union is not counted twice.
                    if (model.ExpectedWsModelOccurrencesByUnit.TryGetValue(
                            unitId,
                            out var expectedOccurrences))
                    {
                        var expectedPresenceByRole =
                            new Dictionary<Wh3UnitVisualRole, double>();
                        foreach (var wsModelPath in normalizedTargets)
                        {
                            if (!expectedOccurrences.ByWsModel.TryGetValue(
                                    wsModelPath,
                                    out var occurrencesByRole))
                            {
                                continue;
                            }

                            foreach (var (role, expectedOccurrencesPerEntity) in occurrencesByRole)
                            {
                                if (exactRoles.Contains(role))
                                    continue;

                                expectedPresenceByRole[role] =
                                    expectedPresenceByRole.GetValueOrDefault(role) +
                                    expectedOccurrencesPerEntity;
                            }
                        }

                        foreach (var (role, expectedOccurrencesPerEntity) in expectedPresenceByRole)
                        {
                            if (expectedOccurrencesPerEntity <= 0)
                                continue;

                            var entityCount = Math.Max(
                                1,
                                model.EntityCountByUnitAndRole
                                    .GetValueOrDefault(unitId)?
                                    .GetValueOrDefault(role, 1) ?? 1);
                            notPresentAcrossRoles *= Math.Pow(
                                1.0 - Math.Clamp(expectedOccurrencesPerEntity, 0.0, 1.0),
                                entityCount);
                        }

                        unitCardPresenceProbability = 1.0 - notPresentAcrossRoles;
                    }
                    else if (exactRoles.Count != 0)
                    {
                        unitCardPresenceProbability = 1.0 - notPresentAcrossRoles;
                    }
                    else if (fallbackCoveredByCategory[category].Contains(unitId))
                    {
                        unitCardPresenceProbability = 1.0;
                    }

                    if (unitCardPresenceProbability <= 0)
                        continue;

                    perSlotPresenceProbability +=
                        unitCardPresenceProbability / population;
                }

                perSlotPresenceProbability = Math.Clamp(
                    perSlotPresenceProbability,
                    0.0,
                    1.0);
                notResidentProbability *= Math.Pow(
                    1.0 - perSlotPresenceProbability,
                    slotCount);
            }

            return Math.Clamp(1.0 - notResidentProbability, 0.0, 1.0);
        }

        private static double CalculateExpectedArmyResidentPixels(
            BatchState state,
            IReadOnlyList<List<AtlasCandidate>> batches)
        {
            if (state.ArmyResidencyModel == null || batches.Count == 0)
                return 0;

            var rootsByMesh = BuildCandidateRootVmdPaths(
                state,
                batches.SelectMany(batch => batch));
            double total = 0;
            foreach (var batch in batches)
            {
                if (!TryGetGeneratedAtlasPixelCost(
                        state,
                        batch,
                        out var pixels,
                        out _))
                {
                    continue;
                }

                total += GetExpectedArmyResidentPixels(
                    state,
                    pixels,
                    batch,
                    rootsByMesh);
            }

            return total;
        }

        private static HashSet<Wh3ArmyUnitCategory> GetArmyCategoriesForRoots(
            ArmyResidencyModel? model,
            IEnumerable<string> roots)
        {
            var result = new HashSet<Wh3ArmyUnitCategory>();
            if (model == null)
                return result;

            foreach (var root in roots)
            {
                if (!model.UnitsByVmd.TryGetValue(root, out var byCategory))
                    continue;

                foreach (var category in byCategory.Keys)
                    result.Add(category);
            }

            return result;
        }

        private static void OptimizeBatchForVmdLocality(
            BatchState state,
            List<AtlasCandidate> batch,
            IReadOnlyDictionary<MeshKey, HashSet<string>> rootsByMesh,
            IReadOnlyList<MergeAffinityGroup> affinityGroups,
            List<List<AtlasCandidate>> output)
        {
            const int maxAcceptedLocalitySplits = 32;
            const int maxGlobalPixelIncreasePercent = 25;
            const int minimumResidentPixelSavingPercent = 10;
            const double comparisonEpsilon = 0.5;

            if (batch.Count < 4 ||
                state.VmdLocalitySplitsAccepted >= maxAcceptedLocalitySplits ||
                !TryGetGeneratedAtlasPixelCost(
                    state,
                    batch,
                    out var baselinePixels,
                    out _))
            {
                output.Add(batch);
                return;
            }

            var baselineRootCount = GetBatchRootCount(batch, rootsByMesh);
            if (baselineRootCount <= 1)
            {
                output.Add(batch);
                return;
            }

            var baselineVmdResidentPixels = GetAtlasResidencyProxy(
                baselinePixels,
                baselineRootCount);
            var baselineArmyResidentPixels = GetExpectedArmyResidentPixels(
                state,
                baselinePixels,
                batch,
                rootsByMesh);
            var useArmyMetric =
                state.ArmyResidencyModel != null &&
                baselineArmyResidentPixels > comparisonEpsilon;
            var baselineAffinity = CalculateMergeAffinityScore(
                [batch],
                affinityGroups);

            AtlasBatchSplitProposal? bestProposal = null;
            var bestVmdResidentPixels = baselineVmdResidentPixels;
            var bestArmyResidentPixels = baselineArmyResidentPixels;
            var bestGlobalPixels = baselinePixels;
            var bestAffinity = baselineAffinity;
            var evaluations = new List<(
                AtlasBatchSplitProposal Proposal,
                ArmyLocalitySplitEvaluationReportEntry Entry)>();

            foreach (var proposal in CreateVmdLocalitySplitProposals(
                         batch,
                         rootsByMesh,
                         state.ArmyResidencyModel))
            {
                state.VmdLocalitySplitEvaluations++;

                if (!TryGetGeneratedAtlasPixelCost(
                        state,
                        proposal.Left,
                        out var leftPixels,
                        out _) ||
                    !TryGetGeneratedAtlasPixelCost(
                        state,
                        proposal.Right,
                        out var rightPixels,
                        out _))
                {
                    continue;
                }

                var combinedPixels = checked(leftPixels + rightPixels);
                var leftRootCount = GetBatchRootCount(proposal.Left, rootsByMesh);
                var rightRootCount = GetBatchRootCount(proposal.Right, rootsByMesh);
                var combinedVmdResidentPixels = checked(
                    GetAtlasResidencyProxy(leftPixels, leftRootCount) +
                    GetAtlasResidencyProxy(rightPixels, rightRootCount));
                var combinedArmyResidentPixels =
                    GetExpectedArmyResidentPixels(
                        state,
                        leftPixels,
                        proposal.Left,
                        rootsByMesh) +
                    GetExpectedArmyResidentPixels(
                        state,
                        rightPixels,
                        proposal.Right,
                        rootsByMesh);

                string? rejectionReason = null;
                if (combinedPixels * 100 >
                    baselinePixels * (100L + maxGlobalPixelIncreasePercent))
                {
                    rejectionReason = "global-pixel-cap";
                }
                else if (useArmyMetric &&
                         combinedArmyResidentPixels * 100.0 >
                         baselineArmyResidentPixels *
                         (100.0 - minimumResidentPixelSavingPercent))
                {
                    rejectionReason = "insufficient-expected-army-saving";
                }
                else if (useArmyMetric &&
                         combinedVmdResidentPixels > baselineVmdResidentPixels)
                {
                    rejectionReason = "VMD-residency-regression";
                }
                else if (!useArmyMetric &&
                         combinedVmdResidentPixels * 100 >
                         baselineVmdResidentPixels *
                         (100L - minimumResidentPixelSavingPercent))
                {
                    rejectionReason = "insufficient-VMD-residency-saving";
                }

                if (rejectionReason != null)
                {
                    evaluations.Add((
                        proposal,
                        new ArmyLocalitySplitEvaluationReportEntry(
                            false,
                            rejectionReason,
                            baselinePixels,
                            combinedPixels,
                            baselineVmdResidentPixels,
                            combinedVmdResidentPixels,
                            baselineArmyResidentPixels,
                            combinedArmyResidentPixels)));
                    continue;
                }

                var proposedAffinity = CalculateMergeAffinityScore(
                    [proposal.Left, proposal.Right],
                    affinityGroups);
                if (proposedAffinity < baselineAffinity)
                {
                    evaluations.Add((
                        proposal,
                        new ArmyLocalitySplitEvaluationReportEntry(
                            false,
                            "merge-affinity-regression",
                            baselinePixels,
                            combinedPixels,
                            baselineVmdResidentPixels,
                            combinedVmdResidentPixels,
                            baselineArmyResidentPixels,
                            combinedArmyResidentPixels)));
                    continue;
                }

                evaluations.Add((
                    proposal,
                    new ArmyLocalitySplitEvaluationReportEntry(
                        false,
                        string.Empty,
                        baselinePixels,
                        combinedPixels,
                        baselineVmdResidentPixels,
                        combinedVmdResidentPixels,
                        baselineArmyResidentPixels,
                        combinedArmyResidentPixels)));

                var better = useArmyMetric
                    ? combinedArmyResidentPixels < bestArmyResidentPixels - comparisonEpsilon ||
                      (Math.Abs(combinedArmyResidentPixels - bestArmyResidentPixels) <= comparisonEpsilon &&
                       combinedVmdResidentPixels < bestVmdResidentPixels) ||
                      (Math.Abs(combinedArmyResidentPixels - bestArmyResidentPixels) <= comparisonEpsilon &&
                       combinedVmdResidentPixels == bestVmdResidentPixels &&
                       combinedPixels < bestGlobalPixels) ||
                      (Math.Abs(combinedArmyResidentPixels - bestArmyResidentPixels) <= comparisonEpsilon &&
                       combinedVmdResidentPixels == bestVmdResidentPixels &&
                       combinedPixels == bestGlobalPixels &&
                       proposedAffinity > bestAffinity)
                    : combinedVmdResidentPixels < bestVmdResidentPixels ||
                      (combinedVmdResidentPixels == bestVmdResidentPixels &&
                       combinedPixels < bestGlobalPixels) ||
                      (combinedVmdResidentPixels == bestVmdResidentPixels &&
                       combinedPixels == bestGlobalPixels &&
                       proposedAffinity > bestAffinity);

                if (!better)
                    continue;

                bestProposal = proposal;
                bestVmdResidentPixels = combinedVmdResidentPixels;
                bestArmyResidentPixels = combinedArmyResidentPixels;
                bestGlobalPixels = combinedPixels;
                bestAffinity = proposedAffinity;
            }

            foreach (var (proposal, entry) in evaluations)
            {
                var accepted = bestProposal != null && ReferenceEquals(proposal, bestProposal);
                state.ArmyLocalitySplitEvaluations.Add(
                    entry with
                    {
                        Accepted = accepted,
                        Decision = accepted
                            ? "accepted"
                            : string.IsNullOrEmpty(entry.Decision)
                                ? "eligible-not-selected"
                                : entry.Decision,
                    });
            }

            if (bestProposal == null)
            {
                output.Add(batch);
                return;
            }

            state.VmdLocalitySplitsAccepted++;
            state.VmdLocalityResidentPixelsSaved = checked(
                state.VmdLocalityResidentPixelsSaved +
                baselineVmdResidentPixels -
                bestVmdResidentPixels);
            state.ExpectedArmyResidentPixelsSavedByLocality += Math.Max(
                0,
                baselineArmyResidentPixels - bestArmyResidentPixels);
            state.VmdLocalityGlobalPixelsAdded = checked(
                state.VmdLocalityGlobalPixelsAdded +
                Math.Max(0, bestGlobalPixels - baselinePixels));

            OptimizeBatchForVmdLocality(
                state,
                bestProposal.Left,
                rootsByMesh,
                affinityGroups,
                output);
            OptimizeBatchForVmdLocality(
                state,
                bestProposal.Right,
                rootsByMesh,
                affinityGroups,
                output);
        }

        private static IReadOnlyList<AtlasBatchSplitProposal> CreateVmdLocalitySplitProposals(
            IReadOnlyList<AtlasCandidate> batch,
            IReadOnlyDictionary<MeshKey, HashSet<string>> rootsByMesh,
            ArmyResidencyModel? armyModel)
        {
            var groups = batch
                .GroupBy(GetAtlasPlanningSourceIdentity)
                .Select(group =>
                {
                    var candidates = group
                        .OrderBy(x => x.RootVmdPath, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.Key.LodIndex)
                        .ThenBy(x => x.Key.PartIndex)
                        .ToList();
                    return new AtlasPlanningCandidateGroup(
                        group.Key,
                        candidates,
                        candidates[0],
                        GetCanonicalCrop(candidates[0]).Crop);
                })
                .ToList();

            if (groups.Count < 2)
                return [];

            var rootsByGroup = groups.ToDictionary(
                group => group.Identity,
                group =>
                {
                    var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var candidate in group.Candidates)
                    {
                        if (rootsByMesh.TryGetValue(candidate.Key, out var candidateRoots))
                            roots.UnionWith(candidateRoots);
                        else
                            roots.Add(Normalize(candidate.RootVmdPath));
                    }

                    return roots;
                });

            var distinctRoots = rootsByGroup.Values
                .SelectMany(roots => roots)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (distinctRoots.Count <= 1)
                return [];

            var proposals = new List<AtlasBatchSplitProposal>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void AddProposal(IEnumerable<AtlasPlanningCandidateGroup> leftGroups)
            {
                var leftGroupList = leftGroups.ToList();
                if (leftGroupList.Count == 0 || leftGroupList.Count == groups.Count)
                    return;

                var leftIdentities = leftGroupList
                    .Select(group => group.Identity)
                    .ToHashSet();
                var rightGroupList = groups
                    .Where(group => !leftIdentities.Contains(group.Identity))
                    .ToList();
                var left = leftGroupList.SelectMany(group => group.Candidates).ToList();
                var right = rightGroupList.SelectMany(group => group.Candidates).ToList();
                if (left.Count < 2 || right.Count < 2)
                    return;

                var signature = string.Join(
                    "\n",
                    left
                        .Select(candidate => candidate.Key.ToString())
                        .OrderBy(value => value, StringComparer.Ordinal));
                if (!seen.Add(signature))
                    return;

                proposals.Add(new AtlasBatchSplitProposal(left, right));
            }

            // Army-category boundaries are cheap, high-value proposals for the expected
            // battle-residency objective. Shared source/crop identities remain indivisible.
            if (armyModel != null)
            {
                foreach (var category in armyModel.Scenario.ArmySlotTemplate.Keys)
                {
                    AddProposal(groups.Where(group =>
                        GetArmyCategoriesForRoots(
                            armyModel,
                            rootsByGroup[group.Identity])
                        .Contains(category)));
                }
            }

            // Then try isolating the assets reachable from each root. Shared source/crop
            // identities stay indivisible, so genuinely reused placements remain shared.
            foreach (var root in distinctRoots
                         .OrderByDescending(root =>
                             groups.Count(group => rootsByGroup[group.Identity].Contains(root)))
                         .ThenBy(root => root, StringComparer.OrdinalIgnoreCase)
                         .Take(12))
            {
                AddProposal(groups.Where(group =>
                    rootsByGroup[group.Identity].Contains(root)));
            }

            // Then cluster equal/similar root signatures. This catches unrelated VMD families
            // that happened to pack efficiently together under the old texture-first ordering.
            string RootSignature(AtlasPlanningCandidateGroup group)
                => string.Join(
                    "\u001f",
                    rootsByGroup[group.Identity]
                        .OrderBy(root => root, StringComparer.OrdinalIgnoreCase));

            var ordered = groups
                .OrderBy(RootSignature, StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => BuildAtlasPlanningOrderKey(group.Representative), StringComparer.Ordinal)
                .ToList();
            var boundaries = new List<int>();
            for (var index = 1; index < ordered.Count; index++)
            {
                if (!RootSignature(ordered[index - 1]).Equals(
                        RootSignature(ordered[index]),
                        StringComparison.OrdinalIgnoreCase))
                {
                    boundaries.Add(index);
                }
            }

            const int maxOrderedLocalitySplitEvaluations = 12;
            IEnumerable<int> selectedBoundaries = boundaries;
            if (boundaries.Count > maxOrderedLocalitySplitEvaluations)
            {
                selectedBoundaries = Enumerable.Range(0, maxOrderedLocalitySplitEvaluations)
                    .Select(index => boundaries[
                        index * (boundaries.Count - 1) /
                        (maxOrderedLocalitySplitEvaluations - 1)])
                    .Distinct();
            }

            foreach (var boundary in selectedBoundaries)
                AddProposal(ordered.Take(boundary));

            const int maxLocalitySplitProposals = 24;
            return proposals.Count <= maxLocalitySplitProposals
                ? proposals
                : proposals.Take(maxLocalitySplitProposals).ToList();
        }

        private static List<List<AtlasCandidate>> OptimizeBatchesForMergeAffinity(
            BatchState state,
            IReadOnlyList<List<AtlasCandidate>> batches)
        {
            var working = batches.Select(batch => batch.ToList()).ToList();
            if (!state.MergeCompatibleMeshesEnabled || working.Count < 2)
                return working;

            var affinityGroups = BuildMergeAffinityGroups(
                working.SelectMany(batch => batch));
            if (affinityGroups.Count == 0)
                return working;

            var rootsByMesh = BuildCandidateRootVmdPaths(
                state,
                working.SelectMany(batch => batch));
            var expectedArmyEntitiesByMesh = BuildExpectedArmyEntitiesByMesh(
                state.ArmyResidencyModel,
                working.SelectMany(batch => batch),
                rootsByMesh);
            var currentExpectedArmyDrawCallsEliminated =
                CalculateExpectedArmyDrawCallsEliminated(
                    state,
                    BuildBatchIndexByMesh(working),
                    affinityGroups,
                    expectedArmyEntitiesByMesh);

            state.MergeAwareAffinityPotentialBefore +=
                CalculateMergeAffinityScore(working, affinityGroups);
            state.ExpectedArmyDrawCallsEliminatedBeforeMergeAware +=
                currentExpectedArmyDrawCallsEliminated;

            const int maxPasses = 2;
            for (var pass = 0; pass < maxPasses; pass++)
            {
                var changed = false;
                var batchByMesh = BuildBatchIndexByMesh(working);
                var candidatePairWeights = new Dictionary<AtlasBatchPair, int>();

                foreach (var group in affinityGroups)
                {
                    var occupiedBatches = group.Meshes
                        .Where(batchByMesh.ContainsKey)
                        .Select(mesh => batchByMesh[mesh])
                        .Distinct()
                        .OrderBy(x => x)
                        .ToArray();

                    for (var left = 0; left < occupiedBatches.Length; left++)
                    {
                        for (var right = left + 1; right < occupiedBatches.Length; right++)
                        {
                            var pair = new AtlasBatchPair(
                                occupiedBatches[left],
                                occupiedBatches[right]);
                            candidatePairWeights[pair] =
                                candidatePairWeights.GetValueOrDefault(pair) + 1;
                        }
                    }
                }

                // Replanning a pair invokes the real atlas packer repeatedly. Bound the search
                // for pathological packs while prioritizing pairs that can reunite the largest
                // number of otherwise-mergeable groups.
                const int maxMergeAwareBatchPairsPerPass = 64;
                var candidatePairs = candidatePairWeights
                    .OrderByDescending(x => x.Value)
                    .ThenBy(x => x.Key.FirstBatchId)
                    .ThenBy(x => x.Key.SecondBatchId)
                    .Take(maxMergeAwareBatchPairsPerPass)
                    .Select(x => x.Key)
                    .ToList();

                state.MergeAwareBatchPairsConsidered += candidatePairs.Count;

                foreach (var pair in candidatePairs)
                {
                    var leftIndex = pair.FirstBatchId;
                    var rightIndex = pair.SecondBatchId;
                    var currentLeft = working[leftIndex];
                    var currentRight = working[rightIndex];

                    if (!TryGetGeneratedAtlasPixelCost(
                            state,
                            currentLeft,
                            out var leftBaselinePixels,
                            out _) ||
                        !TryGetGeneratedAtlasPixelCost(
                            state,
                            currentRight,
                            out var rightBaselinePixels,
                            out _))
                    {
                        continue;
                    }

                    var baselinePixels = checked(leftBaselinePixels + rightBaselinePixels);
                    var baselineResidentPixels = checked(
                        GetAtlasResidencyProxy(
                            leftBaselinePixels,
                            GetBatchRootCount(currentLeft, rootsByMesh)) +
                        GetAtlasResidencyProxy(
                            rightBaselinePixels,
                            GetBatchRootCount(currentRight, rootsByMesh)));
                    var baselineArmyResidentPixels =
                        GetExpectedArmyResidentPixels(
                            state,
                            leftBaselinePixels,
                            currentLeft,
                            rootsByMesh) +
                        GetExpectedArmyResidentPixels(
                            state,
                            rightBaselinePixels,
                            currentRight,
                            rootsByMesh);
                    var baselineAffinity = CalculateMergeAffinityScore(working, affinityGroups);
                    var baselineExpectedArmyDrawCallsEliminated =
                        currentExpectedArmyDrawCallsEliminated;
                    AtlasBatchSplitProposal? bestProposal = null;
                    var bestPixels = baselinePixels;
                    var bestResidentPixels = baselineResidentPixels;
                    var bestArmyResidentPixels = baselineArmyResidentPixels;
                    var bestExpectedArmyDrawCallsEliminated =
                        baselineExpectedArmyDrawCallsEliminated;
                    var bestAffinity = baselineAffinity;

                    foreach (var proposal in CreateMergeAwareRepartitionProposals(
                                 currentLeft,
                                 currentRight,
                                 affinityGroups))
                    {
                        state.MergeAwareRepartitionEvaluations++;

                        if (!TryGetGeneratedAtlasPixelCost(
                                state,
                                proposal.Left,
                                out var leftPixels,
                                out _) ||
                            !TryGetGeneratedAtlasPixelCost(
                                state,
                                proposal.Right,
                                out var rightPixels,
                                out _))
                        {
                            continue;
                        }

                        var combinedPixels = checked(leftPixels + rightPixels);
                        if (combinedPixels > baselinePixels)
                            continue;

                        var proposedResidentPixels = checked(
                            GetAtlasResidencyProxy(
                                leftPixels,
                                GetBatchRootCount(proposal.Left, rootsByMesh)) +
                            GetAtlasResidencyProxy(
                                rightPixels,
                                GetBatchRootCount(proposal.Right, rootsByMesh)));
                        var proposedArmyResidentPixels =
                            GetExpectedArmyResidentPixels(
                                state,
                                leftPixels,
                                proposal.Left,
                                rootsByMesh) +
                            GetExpectedArmyResidentPixels(
                                state,
                                rightPixels,
                                proposal.Right,
                                rootsByMesh);
                        if ((state.ArmyResidencyModel != null &&
                             proposedArmyResidentPixels > baselineArmyResidentPixels + 0.5) ||
                            proposedResidentPixels > baselineResidentPixels)
                        {
                            state.MergeAwareLocalityRegressionsRejected++;
                            continue;
                        }

                        var proposedAffinity = CalculateMergeAffinityScoreWithReplacement(
                            working,
                            leftIndex,
                            rightIndex,
                            proposal,
                            affinityGroups);

                        // Keep the old unweighted merge count as a hard safety guard. The
                        // army-weighted score decides which non-regressing merge opportunity is
                        // actually more valuable in a representative battle.
                        if (proposedAffinity < baselineAffinity)
                            continue;

                        var proposedBatchByMesh = BuildBatchIndexByMesh(working);
                        foreach (var candidate in proposal.Left)
                            proposedBatchByMesh[candidate.Key] = leftIndex;
                        foreach (var candidate in proposal.Right)
                            proposedBatchByMesh[candidate.Key] = rightIndex;
                        var proposedExpectedArmyDrawCallsEliminated =
                            CalculateExpectedArmyDrawCallsEliminated(
                                state,
                                proposedBatchByMesh,
                                affinityGroups,
                                expectedArmyEntitiesByMesh);

                        const double expectedDrawComparisonEpsilon = 0.000001;
                        const double expectedResidencyComparisonEpsilon = 0.5;
                        var isBetter =
                            proposedArmyResidentPixels <
                                bestArmyResidentPixels - expectedResidencyComparisonEpsilon ||
                            (Math.Abs(
                                 proposedArmyResidentPixels -
                                 bestArmyResidentPixels) <= expectedResidencyComparisonEpsilon &&
                             proposedExpectedArmyDrawCallsEliminated >
                                 bestExpectedArmyDrawCallsEliminated +
                                 expectedDrawComparisonEpsilon) ||
                            (Math.Abs(
                                 proposedArmyResidentPixels -
                                 bestArmyResidentPixels) <= expectedResidencyComparisonEpsilon &&
                             Math.Abs(
                                 proposedExpectedArmyDrawCallsEliminated -
                                 bestExpectedArmyDrawCallsEliminated) <=
                                 expectedDrawComparisonEpsilon &&
                             combinedPixels < bestPixels) ||
                            (Math.Abs(
                                 proposedArmyResidentPixels -
                                 bestArmyResidentPixels) <= expectedResidencyComparisonEpsilon &&
                             Math.Abs(
                                 proposedExpectedArmyDrawCallsEliminated -
                                 bestExpectedArmyDrawCallsEliminated) <=
                                 expectedDrawComparisonEpsilon &&
                             combinedPixels == bestPixels &&
                             proposedResidentPixels < bestResidentPixels) ||
                            (Math.Abs(
                                 proposedArmyResidentPixels -
                                 bestArmyResidentPixels) <= expectedResidencyComparisonEpsilon &&
                             Math.Abs(
                                 proposedExpectedArmyDrawCallsEliminated -
                                 bestExpectedArmyDrawCallsEliminated) <=
                                 expectedDrawComparisonEpsilon &&
                             combinedPixels == bestPixels &&
                             proposedResidentPixels == bestResidentPixels &&
                             proposedAffinity > bestAffinity);

                        if (isBetter)
                        {
                            bestProposal = proposal;
                            bestPixels = combinedPixels;
                            bestResidentPixels = proposedResidentPixels;
                            bestArmyResidentPixels = proposedArmyResidentPixels;
                            bestExpectedArmyDrawCallsEliminated =
                                proposedExpectedArmyDrawCallsEliminated;
                            bestAffinity = proposedAffinity;
                        }
                    }

                    if (bestProposal == null)
                        continue;

                    working[leftIndex] = bestProposal.Left;
                    working[rightIndex] = bestProposal.Right;
                    state.MergeAwareRepartitionsAccepted++;
                    state.MergeAwareRepartitionPixelsSaved = checked(
                        state.MergeAwareRepartitionPixelsSaved +
                        baselinePixels -
                        bestPixels);
                    state.MergeAwareAffinityEliminationsGained +=
                        bestAffinity - baselineAffinity;
                    currentExpectedArmyDrawCallsEliminated =
                        bestExpectedArmyDrawCallsEliminated;
                    state.MergeAwareRepartitionEntries.Add(
                        new MergeAwareRepartitionReportEntry(
                            state.BatchIndex + leftIndex,
                            state.BatchIndex + rightIndex,
                            baselinePixels,
                            bestPixels,
                            baselineAffinity,
                            bestAffinity,
                            baselineExpectedArmyDrawCallsEliminated,
                            bestExpectedArmyDrawCallsEliminated));
                    changed = true;
                }

                if (!changed)
                    break;
            }

            CoalesceMergeAwareBatchesWithoutPixelIncrease(
                state,
                working,
                affinityGroups,
                rootsByMesh,
                expectedArmyEntitiesByMesh,
                ref currentExpectedArmyDrawCallsEliminated);

            state.MergeAwareAffinityPotentialAfter +=
                CalculateMergeAffinityScore(working, affinityGroups);
            state.ExpectedArmyDrawCallsEliminatedAfterMergeAware +=
                currentExpectedArmyDrawCallsEliminated;
            return working;
        }

        private static void CoalesceMergeAwareBatchesWithoutPixelIncrease(
            BatchState state,
            List<List<AtlasCandidate>> working,
            IReadOnlyList<MergeAffinityGroup> affinityGroups,
            IReadOnlyDictionary<MeshKey, HashSet<string>> rootsByMesh,
            IReadOnlyDictionary<
                MeshKey,
                Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>> expectedArmyEntitiesByMesh,
            ref double currentExpectedArmyDrawCallsEliminated)
        {
            const int maxCoalesces = 16;
            const int maxPairEvaluationsPerPass = 64;

            for (var pass = 0; pass < maxCoalesces && working.Count >= 2; pass++)
            {
                var batchByMesh = BuildBatchIndexByMesh(working);
                var candidatePairWeights = new Dictionary<AtlasBatchPair, int>();

                foreach (var group in affinityGroups)
                {
                    var occupiedBatches = group.Meshes
                        .Where(batchByMesh.ContainsKey)
                        .Select(mesh => batchByMesh[mesh])
                        .Distinct()
                        .OrderBy(x => x)
                        .ToArray();

                    for (var left = 0; left < occupiedBatches.Length; left++)
                    {
                        for (var right = left + 1; right < occupiedBatches.Length; right++)
                        {
                            var pair = new AtlasBatchPair(
                                occupiedBatches[left],
                                occupiedBatches[right]);
                            candidatePairWeights[pair] =
                                candidatePairWeights.GetValueOrDefault(pair) + 1;
                        }
                    }
                }

                if (candidatePairWeights.Count == 0)
                    break;

                var baselineAffinity = CalculateMergeAffinityScore(working, affinityGroups);
                AtlasBatchPair? bestPair = null;
                List<AtlasCandidate>? bestCombined = null;
                var bestAffinity = baselineAffinity;
                var bestArmyResidentPixelsSaved = double.NegativeInfinity;
                var bestExpectedArmyDrawCallsEliminated =
                    currentExpectedArmyDrawCallsEliminated;
                long bestPixelsSaved = long.MinValue;
                var bestPairWeight = -1;

                foreach (var entry in candidatePairWeights
                             .OrderByDescending(x => x.Value)
                             .ThenBy(x => x.Key.FirstBatchId)
                             .ThenBy(x => x.Key.SecondBatchId)
                             .Take(maxPairEvaluationsPerPass))
                {
                    var pair = entry.Key;
                    var leftBatch = working[pair.FirstBatchId];
                    var rightBatch = working[pair.SecondBatchId];

                    if (!TryGetGeneratedAtlasPixelCost(
                            state,
                            leftBatch,
                            out var leftPixels,
                            out _) ||
                        !TryGetGeneratedAtlasPixelCost(
                            state,
                            rightBatch,
                            out var rightPixels,
                            out _))
                    {
                        continue;
                    }

                    var baselinePixels = checked(leftPixels + rightPixels);
                    var baselineResidentPixels = checked(
                        GetAtlasResidencyProxy(
                            leftPixels,
                            GetBatchRootCount(leftBatch, rootsByMesh)) +
                        GetAtlasResidencyProxy(
                            rightPixels,
                            GetBatchRootCount(rightBatch, rootsByMesh)));
                    var baselineArmyResidentPixels =
                        GetExpectedArmyResidentPixels(
                            state,
                            leftPixels,
                            leftBatch,
                            rootsByMesh) +
                        GetExpectedArmyResidentPixels(
                            state,
                            rightPixels,
                            rightBatch,
                            rootsByMesh);
                    var combined = leftBatch.Concat(rightBatch).ToList();
                    state.MergeAwareBatchCoalesceEvaluations++;
                    if (!TryGetGeneratedAtlasPixelCost(
                            state,
                            combined,
                            out var combinedPixels,
                            out _) ||
                        combinedPixels > baselinePixels)
                    {
                        continue;
                    }

                    var combinedResidentPixels = GetAtlasResidencyProxy(
                        combinedPixels,
                        GetBatchRootCount(combined, rootsByMesh));
                    var combinedArmyResidentPixels = GetExpectedArmyResidentPixels(
                        state,
                        combinedPixels,
                        combined,
                        rootsByMesh);
                    if ((state.ArmyResidencyModel != null &&
                         combinedArmyResidentPixels > baselineArmyResidentPixels + 0.5) ||
                        combinedResidentPixels > baselineResidentPixels)
                    {
                        state.MergeAwareLocalityRegressionsRejected++;
                        continue;
                    }

                    var proposedAffinity = CalculateMergeAffinityScoreWithMerge(
                        working,
                        pair.FirstBatchId,
                        pair.SecondBatchId,
                        affinityGroups);
                    if (proposedAffinity <= baselineAffinity)
                        continue;

                    var proposedBatchByMesh = BuildBatchIndexByMesh(working);
                    foreach (var candidate in rightBatch)
                        proposedBatchByMesh[candidate.Key] = pair.FirstBatchId;
                    var proposedExpectedArmyDrawCallsEliminated =
                        CalculateExpectedArmyDrawCallsEliminated(
                            state,
                            proposedBatchByMesh,
                            affinityGroups,
                            expectedArmyEntitiesByMesh);

                    var pixelsSaved = baselinePixels - combinedPixels;
                    var armyResidentPixelsSaved =
                        baselineArmyResidentPixels - combinedArmyResidentPixels;
                    const double expectedDrawComparisonEpsilon = 0.000001;
                    const double expectedResidencyComparisonEpsilon = 0.5;
                    var isBetter =
                        armyResidentPixelsSaved >
                            bestArmyResidentPixelsSaved + expectedResidencyComparisonEpsilon ||
                        (Math.Abs(
                             armyResidentPixelsSaved -
                             bestArmyResidentPixelsSaved) <= expectedResidencyComparisonEpsilon &&
                         proposedExpectedArmyDrawCallsEliminated >
                             bestExpectedArmyDrawCallsEliminated +
                             expectedDrawComparisonEpsilon) ||
                        (Math.Abs(
                             armyResidentPixelsSaved -
                             bestArmyResidentPixelsSaved) <= expectedResidencyComparisonEpsilon &&
                         Math.Abs(
                             proposedExpectedArmyDrawCallsEliminated -
                             bestExpectedArmyDrawCallsEliminated) <=
                             expectedDrawComparisonEpsilon &&
                         pixelsSaved > bestPixelsSaved) ||
                        (Math.Abs(
                             armyResidentPixelsSaved -
                             bestArmyResidentPixelsSaved) <= expectedResidencyComparisonEpsilon &&
                         Math.Abs(
                             proposedExpectedArmyDrawCallsEliminated -
                             bestExpectedArmyDrawCallsEliminated) <=
                             expectedDrawComparisonEpsilon &&
                         pixelsSaved == bestPixelsSaved &&
                         proposedAffinity > bestAffinity) ||
                        (Math.Abs(
                             armyResidentPixelsSaved -
                             bestArmyResidentPixelsSaved) <= expectedResidencyComparisonEpsilon &&
                         Math.Abs(
                             proposedExpectedArmyDrawCallsEliminated -
                             bestExpectedArmyDrawCallsEliminated) <=
                             expectedDrawComparisonEpsilon &&
                         pixelsSaved == bestPixelsSaved &&
                         proposedAffinity == bestAffinity &&
                         entry.Value > bestPairWeight);
                    if (!isBetter)
                        continue;

                    bestPair = pair;
                    bestCombined = combined;
                    bestAffinity = proposedAffinity;
                    bestArmyResidentPixelsSaved = armyResidentPixelsSaved;
                    bestExpectedArmyDrawCallsEliminated =
                        proposedExpectedArmyDrawCallsEliminated;
                    bestPixelsSaved = pixelsSaved;
                    bestPairWeight = entry.Value;
                }

                if (bestPair == null || bestCombined == null)
                    break;

                var selected = bestPair.Value;
                working[selected.FirstBatchId] = bestCombined;
                working.RemoveAt(selected.SecondBatchId);
                state.MergeAwareBatchCoalescesAccepted++;
                state.MergeAwareBatchCoalescePixelsSaved = checked(
                    state.MergeAwareBatchCoalescePixelsSaved +
                    Math.Max(0, bestPixelsSaved));
                state.MergeAwareAffinityEliminationsGained +=
                    bestAffinity - baselineAffinity;
                currentExpectedArmyDrawCallsEliminated =
                    bestExpectedArmyDrawCallsEliminated;
            }
        }

        private static int CalculateMergeAffinityScoreWithMerge(
            IReadOnlyList<List<AtlasCandidate>> batches,
            int leftIndex,
            int rightIndex,
            IReadOnlyList<MergeAffinityGroup> affinityGroups)
        {
            var batchByMesh = BuildBatchIndexByMesh(batches);
            foreach (var candidate in batches[rightIndex])
                batchByMesh[candidate.Key] = leftIndex;

            var score = 0;
            foreach (var group in affinityGroups)
            {
                score += group.Meshes
                    .Where(batchByMesh.ContainsKey)
                    .GroupBy(mesh => batchByMesh[mesh])
                    .Sum(batch => Math.Max(0, batch.Count() - 1));
            }

            return score;
        }

        private static bool CanEarnMergeDrawCredit(string? embeddedRigidPath)
            => string.IsNullOrWhiteSpace(embeddedRigidPath);

        private static bool CanEarnMergeDrawCredit(AtlasCandidate candidate)
            => candidate.Usages.All(usage =>
                CanEarnMergeDrawCredit(usage.EmbeddedRigidPath));

        private static MergeAffinityIdentity BuildMergeAffinityIdentity(
            string geometryPath,
            int lodIndex,
            string rmvIdentity,
            string materialIdentity)
            => new(
                geometryPath,
                lodIndex,
                rmvIdentity,
                materialIdentity);

        private static MergeAffinityIdentity BuildMergeAffinityIdentity(
            AtlasCandidate candidate)
            => BuildMergeAffinityIdentity(
                candidate.Key.GeometryPath,
                candidate.Key.LodIndex,
                GetRmvMergeIdentity(candidate.Model),
                BuildMergeAffinityMaterialIdentity(candidate));

        private static List<MergeAffinityGroup> BuildMergeAffinityGroups(
            IEnumerable<AtlasCandidate> candidates)
        {
            var result = new List<MergeAffinityGroup>();

            var buckets = candidates
                .Where(CanEarnMergeDrawCredit)
                .GroupBy(BuildMergeAffinityIdentity);

            foreach (var bucket in buckets)
            {
                var current = new List<AtlasCandidate>();
                var currentVertexCount = 0;

                foreach (var candidate in bucket
                             .OrderBy(x => x.Key.PartIndex)
                             .ThenBy(x => x.RootVmdPath, StringComparer.OrdinalIgnoreCase))
                {
                    var vertexCount = candidate.Model.Mesh.VertexList.Length;
                    if (vertexCount > ushort.MaxValue)
                        continue;

                    if (current.Count != 0 &&
                        currentVertexCount + vertexCount > ushort.MaxValue)
                    {
                        if (current.Count > 1)
                        {
                            result.Add(new MergeAffinityGroup(
                                current.Select(x => x.Key).ToArray()));
                        }

                        current = [];
                        currentVertexCount = 0;
                    }

                    current.Add(candidate);
                    currentVertexCount += vertexCount;
                }

                if (current.Count > 1)
                {
                    result.Add(new MergeAffinityGroup(
                        current.Select(x => x.Key).ToArray()));
                }
            }

            return result;
        }

        private static string BuildMergeAffinityMaterialIdentity(AtlasCandidate candidate)
        {
            var material = new XmlDocument();
            material.LoadXml(candidate.MaterialDocument.OuterXml);

            foreach (var channel in AtlasChannels)
            {
                if (IsCandidateIgnoredTextureSlot(candidate, channel.Slot))
                {
                    RemoveTextureSlot(material, channel.Slot);
                    continue;
                }

                // Only non-constant resolved channels are guaranteed to be rewritten to the
                // same generated atlas path when two candidates share a batch. Preserved,
                // missing, and constant-only sources remain part of the strict identity.
                if (candidate.ResolvedChannels.Contains(channel.Slot))
                {
                    SetTexturePath(
                        material,
                        channel.Slot,
                        $"__asset_editor_shared_atlas_{channel.Slot}__");
                }
            }

            return GetMaterialRenderingIdentity(material);
        }

        private static Dictionary<MeshKey, int> BuildBatchIndexByMesh(
            IReadOnlyList<List<AtlasCandidate>> batches)
        {
            var result = new Dictionary<MeshKey, int>();
            for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                foreach (var candidate in batches[batchIndex])
                    result[candidate.Key] = batchIndex;
            }
            return result;
        }

        private static int CalculateMergeAffinityScore(
            IReadOnlyList<List<AtlasCandidate>> batches,
            IReadOnlyList<MergeAffinityGroup> affinityGroups)
        {
            var batchByMesh = BuildBatchIndexByMesh(batches);
            var score = 0;

            foreach (var group in affinityGroups)
            {
                score += group.Meshes
                    .Where(batchByMesh.ContainsKey)
                    .GroupBy(mesh => batchByMesh[mesh])
                    .Sum(batch => Math.Max(0, batch.Count() - 1));
            }

            return score;
        }

        private static Dictionary<
            MeshKey,
            Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>>
            BuildExpectedArmyEntitiesByMesh(
                ArmyResidencyModel? model,
                IEnumerable<AtlasCandidate> candidates,
                IReadOnlyDictionary<MeshKey, HashSet<string>> rootsByMesh)
        {
            var result = new Dictionary<
                MeshKey,
                Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>>();
            if (model == null)
                return result;

            foreach (var candidate in candidates
                         .GroupBy(candidate => candidate.Key)
                         .Select(group => group.First()))
            {
                var wsModels = candidate.Usages
                    .Select(usage => Normalize(usage.AssetPath))
                    .Where(path => path.Length != 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (wsModels.Length == 0)
                    continue;

                Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>? byCategory = null;
                foreach (var (category, unitIds) in model.UnitsByCategory)
                {
                    foreach (var unitId in unitIds)
                    {
                        double expectedRenderedEntities = 0;
                        if (model.ExpectedWsModelOccurrencesByUnit.TryGetValue(
                                unitId,
                                out var expectedOccurrences))
                        {
                            foreach (var wsModelPath in wsModels)
                            {
                                if (!expectedOccurrences.ByWsModel.TryGetValue(
                                        wsModelPath,
                                        out var occurrencesByRole))
                                {
                                    continue;
                                }

                                foreach (var (role, expectedOccurrencesPerEntity) in occurrencesByRole)
                                {
                                    var entityCount = Math.Max(
                                        1,
                                        model.EntityCountByUnitAndRole
                                            .GetValueOrDefault(unitId)?
                                            .GetValueOrDefault(role, 1) ?? 1);
                                    expectedRenderedEntities +=
                                        Math.Clamp(expectedOccurrencesPerEntity, 0.0, 1.0) *
                                        entityCount;
                                }
                            }
                        }
                        else if (rootsByMesh.TryGetValue(candidate.Key, out var roots))
                        {
                            // Preserve the old conservative behavior when probability data could
                            // not be built for a resolved unit.
                            foreach (var root in roots)
                            {
                                if (model.UnitsByVmd.TryGetValue(root, out var unitsForVmd) &&
                                    unitsForVmd.TryGetValue(category, out var fallbackUnits) &&
                                    fallbackUnits.Contains(unitId))
                                {
                                    expectedRenderedEntities = Math.Max(
                                        1,
                                        model.EntityCountByUnit.GetValueOrDefault(unitId, 1));
                                    break;
                                }
                            }
                        }

                        if (expectedRenderedEntities <= 0)
                            continue;

                        byCategory ??=
                            new Dictionary<
                                Wh3ArmyUnitCategory,
                                Dictionary<string, double>>();
                        if (!byCategory.TryGetValue(category, out var expectedByUnit))
                        {
                            expectedByUnit =
                                new Dictionary<string, double>(
                                    StringComparer.OrdinalIgnoreCase);
                            byCategory[category] = expectedByUnit;
                        }

                        expectedByUnit[unitId] = expectedRenderedEntities;
                    }
                }

                if (byCategory != null)
                    result[candidate.Key] = byCategory;
            }

            return result;
        }

        private static double CalculateExpectedConfigurationMergeDrawSavings(
            double[] probabilities,
            int[][] coRenderedCountsByConfiguration,
            int entityCount)
        {
            if (entityCount <= 0 ||
                probabilities.Length == 0 ||
                coRenderedCountsByConfiguration.Length == 0)
            {
                return 0;
            }

            var configurationCount = Math.Min(
                probabilities.Length,
                coRenderedCountsByConfiguration.Length);
            double total = 0;
            for (var index = 0; index < configurationCount; index++)
            {
                var probability = probabilities[index];
                if (probability <= 0)
                    continue;

                var positiveCounts = coRenderedCountsByConfiguration[index]
                    .Where(count => count > 0)
                    .ToArray();
                if (positiveCounts.Length < 2)
                    continue;

                total += probability *
                    entityCount *
                    positiveCounts.Min() *
                    (positiveCounts.Length - 1);
            }

            return total;
        }

        private static double CalculateExpectedArmyDrawCallsEliminated(
            BatchState state,
            IReadOnlyDictionary<MeshKey, int> batchByMesh,
            IReadOnlyList<MergeAffinityGroup> affinityGroups,
            IReadOnlyDictionary<
                MeshKey,
                Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>>
                expectedEntitiesByMesh)
        {
            var model = state.ArmyResidencyModel;
            if (model == null)
                return 0;

            double total = 0;
            foreach (var group in affinityGroups)
            {
                foreach (var colocatedMeshes in group.Meshes
                             .Where(batchByMesh.ContainsKey)
                             .GroupBy(mesh => batchByMesh[mesh]))
                {
                    var meshes = colocatedMeshes.ToArray();
                    if (meshes.Length < 2)
                        continue;

                    var lodIndex = meshes[0].LodIndex;
                    var lodProbability = model.Scenario.LodDistribution
                        .GetValueOrDefault(lodIndex);
                    if (lodProbability <= 0)
                        continue;

                    foreach (var (category, slotCount) in model.Scenario.ArmySlotTemplate)
                    {
                        var population = model.UnitsByCategory[category].Count;
                        if (population == 0)
                            continue;

                        double eliminatedDrawsAcrossResolvedUnits = 0;
                        foreach (var unitId in model.UnitsByCategory[category])
                        {
                            var usedExactConfigurations = false;
                            if (model.VisualConfigurationsByUnitAndRole.TryGetValue(
                                    unitId,
                                    out var configurationsByRole) &&
                                configurationsByRole.Count != 0)
                            {
                                usedExactConfigurations = true;
                                foreach (var (role, configurations) in configurationsByRole)
                                {
                                    var entityCount = Math.Max(
                                        1,
                                        model.EntityCountByUnitAndRole
                                            .GetValueOrDefault(unitId)?
                                            .GetValueOrDefault(role, 1) ?? 1);

                                    var configurationProbabilities = new List<double>();
                                    var coRenderedCountsByConfiguration = new List<int[]>();
                                    foreach (var configuration in configurations)
                                    {
                                        if (configuration.Probability <= 0)
                                            continue;

                                        var coRenderedCounts = new int[meshes.Length];
                                        for (var meshIndex = 0; meshIndex < meshes.Length; meshIndex++)
                                        {
                                            var mesh = meshes[meshIndex];
                                            if (!state.Usages.TryGetValue(mesh, out var usages))
                                                continue;

                                            coRenderedCounts[meshIndex] = usages
                                                .Select(usage => Normalize(usage.AssetPath))
                                                .Where(path => path.Length != 0)
                                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                                .Sum(path => configuration.WsModelOccurrences
                                                    .GetValueOrDefault(path));
                                        }

                                        configurationProbabilities.Add(configuration.Probability);
                                        coRenderedCountsByConfiguration.Add(coRenderedCounts);
                                    }

                                    // Evaluate concrete visual states, not marginal averages.
                                    // Mutually-exclusive mesh alternatives can therefore never
                                    // earn draw-call credit together.
                                    eliminatedDrawsAcrossResolvedUnits +=
                                        CalculateExpectedConfigurationMergeDrawSavings(
                                            configurationProbabilities.ToArray(),
                                            coRenderedCountsByConfiguration.ToArray(),
                                            entityCount);
                                }
                            }

                            if (usedExactConfigurations)
                                continue;

                            // Bounded fallback for VMDs whose configuration state space was too
                            // large to enumerate exactly. This retains the previous conservative
                            // marginal estimate instead of making the optimizer fail.
                            var expectedCounts = new List<double>(meshes.Length);
                            foreach (var mesh in meshes)
                            {
                                if (!expectedEntitiesByMesh.TryGetValue(mesh, out var meshUnits) ||
                                    !meshUnits.TryGetValue(category, out var expectedByUnit) ||
                                    !expectedByUnit.TryGetValue(unitId, out var expectedEntities) ||
                                    expectedEntities <= 0)
                                {
                                    continue;
                                }

                                expectedCounts.Add(expectedEntities);
                            }

                            if (expectedCounts.Count >= 2)
                            {
                                eliminatedDrawsAcrossResolvedUnits +=
                                    expectedCounts.Min() * (expectedCounts.Count - 1);
                            }
                        }

                        if (eliminatedDrawsAcrossResolvedUnits <= 0)
                            continue;

                        // Draw calls scale with rendered entity instances. Average the expected
                        // per-unit-card saving across the category, then multiply by the number
                        // of representative army slots in that category.
                        total += lodProbability *
                            slotCount *
                            (eliminatedDrawsAcrossResolvedUnits / population);
                    }
                }
            }

            return total;
        }

        private static List<List<AtlasCandidate>> FilterBatchesForMergeValue(
            BatchState state,
            IReadOnlyList<List<AtlasCandidate>> batches)
        {
            if (batches.Count == 0)
                return [];

            state.AtlasValueGateSourceTextureIndex ??=
                BuildAtlasValueGateSourceTextureIndex(state);

            var allCandidates = batches
                .SelectMany(batch => batch)
                .GroupBy(candidate => candidate.Key)
                .Select(group => group.First())
                .ToList();
            var candidateByKey = allCandidates.ToDictionary(candidate => candidate.Key);
            var affinityGroups = BuildMergeAffinityGroups(allCandidates);
            if (affinityGroups.Count == 0)
            {
                foreach (var candidate in allCandidates)
                {
                    RecordSkip(
                        state,
                        candidate.RootVmdPath,
                        candidate.Key,
                        candidate.Usages.FirstOrDefault()?.AssetPath ?? string.Empty,
                        "Atlas value gate: no compatible mesh merge would be enabled.");
                    state.AtlasValueGateCandidatesRejected++;
                }

                return [];
            }

            var rootsByMesh = BuildCandidateRootVmdPaths(state, allCandidates);
            var expectedEntitiesByMesh = BuildExpectedArmyEntitiesByMesh(
                state.ArmyResidencyModel,
                allCandidates,
                rootsByMesh);
            var result = new List<List<AtlasCandidate>>();

            foreach (var batch in batches)
            {
                var batchKeys = batch.Select(candidate => candidate.Key).ToHashSet();
                var contributingGroups = affinityGroups
                    .Select(group => new MergeAffinityGroup(
                        group.Meshes.Where(batchKeys.Contains).ToArray()))
                    .Where(group => group.Meshes.Length >= 2)
                    .ToList();
                var contributingKeys = contributingGroups
                    .SelectMany(group => group.Meshes)
                    .ToHashSet();

                foreach (var candidate in batch.Where(candidate => !contributingKeys.Contains(candidate.Key)))
                {
                    RecordSkip(
                        state,
                        candidate.RootVmdPath,
                        candidate.Key,
                        candidate.Usages.FirstOrDefault()?.AssetPath ?? string.Empty,
                        "Atlas value gate: this mesh does not contribute to a compatible mesh merge in its planned atlas batch.");
                    state.AtlasValueGateCandidatesRejected++;
                }

                var trimmed = batch
                    .Where(candidate => contributingKeys.Contains(candidate.Key))
                    .ToList();
                if (trimmed.Count < 2)
                    continue;

                if (TryAcceptAtlasValueBatch(
                        state,
                        trimmed,
                        contributingGroups,
                        expectedEntitiesByMesh,
                        out var acceptedResidency,
                        out var acceptedRawDraws,
                        out var acceptedExpectedDraws,
                        out var rejectionReason))
                {
                    result.Add(trimmed);
                    RecordAtlasValueGateAccepted(
                        state,
                        trimmed,
                        acceptedResidency,
                        acceptedRawDraws,
                        acceptedExpectedDraws);
                    continue;
                }

                // A broad shared batch can be too expensive even when individual merge groups
                // are worthwhile. Fall back to one atlas per independent merge-affinity group
                // before rejecting the opportunity completely.
                state.AtlasValueGateBroadBatchesSplit++;
                foreach (var group in contributingGroups)
                {
                    var groupCandidates = group.Meshes
                        .Where(candidateByKey.ContainsKey)
                        .Select(mesh => candidateByKey[mesh])
                        .Where(candidate => batchKeys.Contains(candidate.Key))
                        .DistinctBy(candidate => candidate.Key)
                        .ToList();
                    if (groupCandidates.Count < 2)
                        continue;

                    if (TryAcceptAtlasValueBatch(
                            state,
                            groupCandidates,
                            [group],
                            expectedEntitiesByMesh,
                            out var groupResidency,
                            out var groupRawDraws,
                            out var groupExpectedDraws,
                            out var groupRejectionReason))
                    {
                        result.Add(groupCandidates);
                        RecordAtlasValueGateAccepted(
                            state,
                            groupCandidates,
                            groupResidency,
                            groupRawDraws,
                            groupExpectedDraws);
                    }
                    else
                    {
                        state.AtlasValueGateBatchesRejected++;
                        state.AtlasValueGateCandidatesRejected += groupCandidates.Count;
                        RecordAtlasValueGateRejected(state, groupResidency);

                        foreach (var candidate in groupCandidates)
                        {
                            RecordSkip(
                            state,
                            candidate.RootVmdPath,
                            candidate.Key,
                            candidate.Usages.FirstOrDefault()?.AssetPath ?? string.Empty,
                                $"Atlas value gate: {groupRejectionReason}");
                        }
                    }
                }
            }

            return result;
        }

        private static void RecordAtlasValueGateAccepted(
            BatchState state,
            IReadOnlyList<AtlasCandidate> batch,
            AtlasValueGateResidencyEstimate residency,
            int rawDrawsEliminated,
            double expectedArmyDrawsEliminated)
        {
            state.AtlasValueGateBatchesAccepted++;
            state.AtlasValueGateCandidatesAccepted += batch.Count;
            state.AtlasValueGateGeneratedBcnBytesAccepted = checked(
                state.AtlasValueGateGeneratedBcnBytesAccepted + residency.GeneratedBcnBytes);
            state.AtlasValueGateRetiredBcnBytesAccepted = checked(
                state.AtlasValueGateRetiredBcnBytesAccepted + residency.RetiredSourceBcnBytes);
            state.AtlasValueGateNetBcnBytesAccepted = checked(
                state.AtlasValueGateNetBcnBytesAccepted + residency.NetBcnBytes);
            state.AtlasValueGateExpectedArmyGeneratedBcnBytesAccepted +=
                residency.ExpectedArmyGeneratedBcnBytes;
            state.AtlasValueGateExpectedArmyRetiredBcnBytesAccepted +=
                residency.ExpectedArmyRetiredSourceBcnBytes;
            state.AtlasValueGateExpectedArmyNetBcnBytesAccepted +=
                residency.ExpectedArmyNetBcnBytes;
            state.AtlasValueGateExpectedDrawsAccepted += expectedArmyDrawsEliminated;

            var roots = batch
                .Select(candidate => Normalize(candidate.RootVmdPath))
                .Where(path => path.Length != 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            state.AtlasValueGateAcceptedBatchEconomics.Add(
                new AtlasValueGateBatchEconomics(
                    state.AtlasValueGateBatchesAccepted,
                    batch.Count,
                    roots,
                    residency.GeneratedBcnBytes,
                    residency.RetiredSourceBcnBytes,
                    residency.NetBcnBytes,
                    residency.ExpectedArmyGeneratedBcnBytes,
                    residency.ExpectedArmyRetiredSourceBcnBytes,
                    residency.ExpectedArmyNetBcnBytes,
                    rawDrawsEliminated,
                    expectedArmyDrawsEliminated));

            state.AtlasValueGateRewrittenSourceReferences.UnionWith(
                residency.RewrittenReferences);
        }

        private static void RecordAtlasValueGateRejected(
            BatchState state,
            AtlasValueGateResidencyEstimate residency)
        {
            state.AtlasValueGateGeneratedBcnBytesRejected = checked(
                state.AtlasValueGateGeneratedBcnBytesRejected + residency.GeneratedBcnBytes);
            state.AtlasValueGateRetiredBcnBytesRejected = checked(
                state.AtlasValueGateRetiredBcnBytesRejected + residency.RetiredSourceBcnBytes);
            state.AtlasValueGateNetBcnBytesRejected = checked(
                state.AtlasValueGateNetBcnBytesRejected + residency.NetBcnBytes);
            state.AtlasValueGateExpectedArmyGeneratedBcnBytesRejected +=
                residency.ExpectedArmyGeneratedBcnBytes;
            state.AtlasValueGateExpectedArmyRetiredBcnBytesRejected +=
                residency.ExpectedArmyRetiredSourceBcnBytes;
            state.AtlasValueGateExpectedArmyNetBcnBytesRejected +=
                residency.ExpectedArmyNetBcnBytes;
        }

        private enum AtlasValueGateBudgetDecision
        {
            Accept,
            ScenarioResolvedZeroBenefit,
            FallbackBudgetExceeded,
            ScenarioBudgetExceeded,
            GlobalGrowthCapExceeded,
        }

        private static AtlasValueGateBudgetDecision EvaluateAtlasValueGateBudget(
            bool scenarioResolved,
            int rawDrawsEliminated,
            double expectedArmyDrawsEliminated,
            double globalCostBytes,
            double expectedCostBytes,
            double acceptedNetBcnBytes,
            double proposedNetBcnBytes,
            double sourceBcnBytes)
        {
            if (scenarioResolved)
            {
                if (expectedArmyDrawsEliminated <= AtlasValueGateExpectedDrawEpsilon)
                {
                    if (globalCostBytes > 0)
                        return AtlasValueGateBudgetDecision.ScenarioResolvedZeroBenefit;
                }
                else
                {
                    var expectedBudget =
                        expectedArmyDrawsEliminated * MaxNetBcnBytesPerExpectedArmyDraw;
                    if (expectedCostBytes > expectedBudget)
                        return AtlasValueGateBudgetDecision.ScenarioBudgetExceeded;
                }
            }
            else
            {
                var fallbackBudget =
                    Math.Max(rawDrawsEliminated, 0) *
                    (double)MaxNetBcnBytesPerFallbackDraw;
                if (globalCostBytes > fallbackBudget)
                    return AtlasValueGateBudgetDecision.FallbackBudgetExceeded;
            }

            if (sourceBcnBytes > 0)
            {
                var projectedNetGrowth = acceptedNetBcnBytes + proposedNetBcnBytes;
                var globalGrowthCap = sourceBcnBytes * MaxReachableBcnGrowthRatio;
                if (projectedNetGrowth > globalGrowthCap)
                    return AtlasValueGateBudgetDecision.GlobalGrowthCapExceeded;
            }

            return AtlasValueGateBudgetDecision.Accept;
        }

        private static bool IsAtlasValueBatchScenarioResolved(
            BatchState state,
            IReadOnlyList<AtlasCandidate> batch)
        {
            var resolution = state.UnitCategoryResolution;
            if (state.ArmyResidencyModel == null || resolution == null)
                return false;

            return batch.All(candidate =>
            {
                var root = Normalize(candidate.RootVmdPath);
                if (root.Length != 0 &&
                    resolution.UsagesByVmd.TryGetValue(root, out var usages) &&
                    usages.Count != 0)
                {
                    return true;
                }

                foreach (var usage in candidate.Usages)
                {
                    var assetPath = Normalize(usage.AssetPath);
                    if (assetPath.Length != 0 &&
                        resolution.DirectAssetUsagesByPath.TryGetValue(
                            assetPath,
                            out var directUsages) &&
                        directUsages.Count != 0)
                    {
                        return true;
                    }
                }

                return false;
            });
        }

        private static bool TryAcceptAtlasValueBatch(
            BatchState state,
            IReadOnlyList<AtlasCandidate> batch,
            IReadOnlyList<MergeAffinityGroup> affinityGroups,
            IReadOnlyDictionary<
                MeshKey,
                Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>>
                expectedEntitiesByMesh,
            out AtlasValueGateResidencyEstimate residency,
            out int rawDrawsEliminated,
            out double expectedArmyDrawsEliminated,
            out string rejectionReason)
        {
            residency = AtlasValueGateResidencyEstimate.Empty;
            rawDrawsEliminated = 0;
            expectedArmyDrawsEliminated = 0;
            rejectionReason = string.Empty;

            if (!TryEstimateIncrementalAtlasResidency(
                    state,
                    batch,
                    out residency))
            {
                rejectionReason = "incremental BCn residency could not be estimated safely.";
                return false;
            }

            var singletonBatches = new List<List<AtlasCandidate>>
            {
                batch.ToList()
            };
            var batchByMesh = BuildBatchIndexByMesh(singletonBatches);
            rawDrawsEliminated = CalculateMergeAffinityScore(
                singletonBatches,
                affinityGroups);
            if (rawDrawsEliminated <= 0)
            {
                rejectionReason = "no compatible mesh draw would be eliminated.";
                return false;
            }

            expectedArmyDrawsEliminated = CalculateExpectedArmyDrawCallsEliminated(
                state,
                batchByMesh,
                affinityGroups,
                expectedEntitiesByMesh);

            // Scenario-resolved batches must justify their BCn cost using scenario draw
            // savings. The much looser raw-draw fallback is reserved for assets whose
            // scenario relevance genuinely could not be resolved. A cumulative growth cap
            // prevents either path from quietly bloating the pack's total reachable BCn.
            var scenarioResolved = IsAtlasValueBatchScenarioResolved(state, batch);
            var globalCost = GetChargeableAtlasValueGateBytes(
                residency.GeneratedBcnBytes,
                residency.RetiredSourceBcnBytes);
            var expectedCost = GetChargeableAtlasValueGateBytes(
                residency.ExpectedArmyGeneratedBcnBytes,
                residency.ExpectedArmyRetiredSourceBcnBytes);
            var sourceBcnBytes = state.SourceBcnResidency?.BcnBytes ?? 0;
            var budgetDecision = EvaluateAtlasValueGateBudget(
                scenarioResolved,
                rawDrawsEliminated,
                expectedArmyDrawsEliminated,
                globalCost,
                expectedCost,
                state.AtlasValueGateNetBcnBytesAccepted,
                residency.NetBcnBytes,
                sourceBcnBytes);

            switch (budgetDecision)
            {
                case AtlasValueGateBudgetDecision.ScenarioResolvedZeroBenefit:
                    rejectionReason =
                        $"scenario-resolved batch has no modeled draw benefit but adds " +
                        $"{FormatMiB(globalCost)} chargeable BCn; the raw-draw fallback is " +
                        $"reserved for scenario-unresolved assets.";
                    return false;

                case AtlasValueGateBudgetDecision.FallbackBudgetExceeded:
                    var fallbackBytesPerDraw =
                        globalCost / Math.Max(rawDrawsEliminated, 1);
                    rejectionReason =
                        $"scenario relevance is unresolved; estimated incremental BCn generated " +
                        $"{FormatMiB(residency.GeneratedBcnBytes)}, retires " +
                        $"{FormatMiB(residency.RetiredSourceBcnBytes)}, net " +
                        $"{FormatMiB(residency.NetBcnBytes)}; net cost is " +
                        $"{FormatMiB(fallbackBytesPerDraw)} per raw draw eliminated, above the " +
                        $"{FormatMiB(MaxNetBcnBytesPerFallbackDraw, 2)} fallback budget.";
                    return false;

                case AtlasValueGateBudgetDecision.ScenarioBudgetExceeded:
                    var scenarioBytesPerDraw =
                        expectedCost /
                        Math.Max(
                            expectedArmyDrawsEliminated,
                            AtlasValueGateExpectedDrawEpsilon);
                    rejectionReason =
                        $"scenario-estimated incremental BCn resource payload generated " +
                        $"{FormatMiB(residency.ExpectedArmyGeneratedBcnBytes)}, retires " +
                        $"{FormatMiB(residency.ExpectedArmyRetiredSourceBcnBytes)}, net " +
                        $"{FormatMiB(residency.ExpectedArmyNetBcnBytes)}; net cost is " +
                        $"{FormatMiB(scenarioBytesPerDraw)} per scenario-estimated draw eliminated, above the " +
                        $"{FormatMiB(MaxNetBcnBytesPerExpectedArmyDraw, 2)} budget.";
                    return false;

                case AtlasValueGateBudgetDecision.GlobalGrowthCapExceeded:
                    var projectedNetGrowth =
                        state.AtlasValueGateNetBcnBytesAccepted +
                        (double)residency.NetBcnBytes;
                    var globalGrowthCap =
                        sourceBcnBytes * MaxReachableBcnGrowthRatio;
                    rejectionReason =
                        $"projected accepted net BCn growth {FormatMiB(projectedNetGrowth)} " +
                        $"would exceed the {MaxReachableBcnGrowthRatio:P0} source-payload cap " +
                        $"({FormatMiB(globalGrowthCap)}).";
                    return false;
            }

            return true;
        }

        private static double GetChargeableAtlasValueGateBytes(
            double generatedBytes,
            double retiredSourceBytes)
            => Math.Max(0.0, generatedBytes - retiredSourceBytes);

        private static bool TryEstimateIncrementalAtlasResidency(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates,
            out AtlasValueGateResidencyEstimate estimate)
        {
            estimate = AtlasValueGateResidencyEstimate.Empty;
            if (!TryGetGeneratedAtlasBcnCost(
                    state,
                    candidates,
                    out var generatedBcnBytes,
                    out var generatedBcnBytesBySlot))
            {
                return false;
            }

            var sourceIndex = state.AtlasValueGateSourceTextureIndex ??
                BuildAtlasValueGateSourceTextureIndex(state);
            state.AtlasValueGateSourceTextureIndex = sourceIndex;

            var proposedRewrites = new HashSet<AtlasValueGateSourceReference>();
            foreach (var candidate in candidates)
            {
                foreach (var slot in generatedBcnBytesBySlot.Keys)
                {
                    if (!candidate.ResolvedChannels.Contains(slot) &&
                        !candidate.ConstantChannels.ContainsKey(slot))
                    {
                        continue;
                    }

                    var sourcePath = GetTexturePath(candidate.MaterialDocument, slot);
                    if (string.IsNullOrWhiteSpace(sourcePath))
                        continue;

                    sourcePath = Normalize(sourcePath);
                    if (!sourceIndex.TryGetValue(sourcePath, out var sourceTexture))
                        continue;

                    foreach (var usage in candidate.Usages)
                    {
                        var reference = new AtlasValueGateSourceReference(
                            candidate.Key,
                            Normalize(usage.AssetPath).ToLowerInvariant(),
                            slot.ToLowerInvariant(),
                            0);
                        if (sourceTexture.References.Contains(reference))
                            proposedRewrites.Add(reference);
                    }
                }
            }

            long retiredSourceBcnBytes = 0;
            double expectedArmyRetiredSourceBcnBytes = 0;
            foreach (var sourceTexture in sourceIndex.Values)
            {
                if (!sourceTexture.References.Any(proposedRewrites.Contains))
                    continue;

                var currentlyRewrittenReferenceCount =
                    sourceTexture.References.Count(
                        state.AtlasValueGateRewrittenSourceReferences.Contains);
                var rewrittenReferenceCountAfterProposal =
                    sourceTexture.References.Count(reference =>
                        state.AtlasValueGateRewrittenSourceReferences.Contains(reference) ||
                        proposedRewrites.Contains(reference));
                var currentlyRetired = IsAtlasValueGateSourceTextureRetired(
                    sourceTexture.HasDirectVmdReference,
                    sourceTexture.References.Count,
                    currentlyRewrittenReferenceCount);
                var retiredAfterProposal = IsAtlasValueGateSourceTextureRetired(
                    sourceTexture.HasDirectVmdReference,
                    sourceTexture.References.Count,
                    rewrittenReferenceCountAfterProposal);
                if (!currentlyRetired && retiredAfterProposal)
                {
                    retiredSourceBcnBytes = checked(
                        retiredSourceBcnBytes + sourceTexture.BcnBytes);
                }

                var currentExpectedResidency = GetExpectedArmySourceTextureResidency(
                    state,
                    sourceTexture,
                    state.AtlasValueGateRewrittenSourceReferences,
                    proposedRewrites: null);
                var proposedExpectedResidency = GetExpectedArmySourceTextureResidency(
                    state,
                    sourceTexture,
                    state.AtlasValueGateRewrittenSourceReferences,
                    proposedRewrites);
                expectedArmyRetiredSourceBcnBytes += Math.Max(
                    0.0,
                    currentExpectedResidency - proposedExpectedResidency);
            }

            double expectedArmyGeneratedBcnBytes = 0;
            var rootsByMesh = BuildCandidateRootVmdPaths(state, candidates);
            foreach (var (slot, channelBytes) in generatedBcnBytesBySlot)
            {
                var channelCandidates = candidates
                    .Where(candidate =>
                        candidate.ResolvedChannels.Contains(slot) ||
                        candidate.ConstantChannels.ContainsKey(slot))
                    .ToList();
                if (channelCandidates.Count == 0)
                    continue;

                var targetWsModels = channelCandidates
                    .SelectMany(candidate => candidate.Usages)
                    .Select(usage => Normalize(usage.AssetPath))
                    .Where(path => path.Length != 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var roots = GetBatchRoots(channelCandidates, rootsByMesh);
                var residentProbability = GetExpectedArmyResidentProbability(
                    state.ArmyResidencyModel,
                    targetWsModels,
                    roots);
                expectedArmyGeneratedBcnBytes +=
                    channelBytes * residentProbability;
            }

            estimate = new AtlasValueGateResidencyEstimate(
                generatedBcnBytes,
                retiredSourceBcnBytes,
                checked(generatedBcnBytes - retiredSourceBcnBytes),
                expectedArmyGeneratedBcnBytes,
                expectedArmyRetiredSourceBcnBytes,
                expectedArmyGeneratedBcnBytes - expectedArmyRetiredSourceBcnBytes,
                proposedRewrites);
            return true;
        }

        private static bool IsAtlasValueGateSourceTextureRetired(
            bool hasDirectVmdReference,
            int referenceCount,
            int rewrittenReferenceCount)
            => !hasDirectVmdReference &&
               referenceCount > 0 &&
               rewrittenReferenceCount >= referenceCount;

        private static double GetExpectedArmySourceTextureResidency(
            BatchState state,
            AtlasValueGateSourceTexture sourceTexture,
            IReadOnlySet<AtlasValueGateSourceReference> existingRewrites,
            IReadOnlySet<AtlasValueGateSourceReference>? proposedRewrites)
        {
            if (state.ArmyResidencyModel == null || sourceTexture.BcnBytes <= 0)
                return 0;

            // Direct VMD texture references (for example decal diffuse/normal) are not
            // rewritten by material atlasing. We do not currently model their exact army
            // probability separately, so conservatively keep the full source texture
            // resident in both the current and proposed estimates. This guarantees that
            // such a reference can never create false scenario-estimated retirement credit.
            if (sourceTexture.HasDirectVmdReference)
                return sourceTexture.BcnBytes;

            var remainingWsModels = sourceTexture.References
                .Where(reference =>
                    !existingRewrites.Contains(reference) &&
                    (proposedRewrites == null || !proposedRewrites.Contains(reference)))
                .Select(reference => reference.WsModelPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (remainingWsModels.Length == 0)
                return 0;

            var fallbackRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rootsByWsModel = state.AtlasValueGateRootsByWsModel;
            if (rootsByWsModel != null)
            {
                foreach (var wsModel in remainingWsModels)
                {
                    if (rootsByWsModel.TryGetValue(wsModel, out var roots))
                        fallbackRoots.UnionWith(roots);
                }
            }

            var residentProbability = GetExpectedArmyResidentProbability(
                state.ArmyResidencyModel,
                remainingWsModels,
                fallbackRoots);
            return sourceTexture.BcnBytes * residentProbability;
        }

        private static Dictionary<string, AtlasValueGateSourceTexture>
            BuildAtlasValueGateSourceTextureIndex(BatchState state)
        {
            var reachableWsModels = state.ReachableWsModelsByRoot.Values
                .SelectMany(paths => paths)
                .Select(Normalize)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            reachableWsModels.UnionWith(
                state.UnitCategoryResolution?.DirectAssetUsagesByPath.Keys
                    .Where(path => Path.GetExtension(path).Equals(
                        ".wsmodel",
                        StringComparison.OrdinalIgnoreCase))
                    .Select(Normalize)
                ?? []);
            var referencesByTexture =
                new Dictionary<string, HashSet<AtlasValueGateSourceReference>>(
                    StringComparer.OrdinalIgnoreCase);

            // Walk every reachable WSModel material entry, not just the mesh-usage index.
            // Entries with missing/invalid lod_index or part_index are deliberately retained
            // with Mesh=null so they can block source-texture retirement credit.
            foreach (var (wsModelPathValue, wsDocument) in state.WsDocuments)
            {
                var wsModelPath = Normalize(wsModelPathValue);
                if (!reachableWsModels.Contains(wsModelPath))
                    continue;

                var geometryPath = Normalize(
                    wsDocument.SelectSingleNode("/model/geometry")?.InnerText);
                var materialNodes = wsDocument.SelectNodes("/model/materials/material");
                if (materialNodes == null)
                    continue;

                foreach (XmlNode materialNode in materialNodes)
                {
                    MeshKey? mesh = null;
                    if (!string.IsNullOrWhiteSpace(geometryPath) &&
                        TryParseIndex(materialNode, "lod_index", out var lodIndex) &&
                        TryParseIndex(materialNode, "part_index", out var partIndex))
                    {
                        mesh = new MeshKey(geometryPath, lodIndex, partIndex);
                    }

                    var materialPath = Normalize(materialNode.InnerText);
                    if (string.IsNullOrWhiteSpace(materialPath))
                        continue;

                    XmlDocument material;
                    try
                    {
                        material = GetMaterialDocument(state, materialPath);
                    }
                    catch
                    {
                        continue;
                    }

                    var textureNodes = material.SelectNodes("/material/textures/texture");
                    if (textureNodes == null)
                        continue;

                    var occurrenceBySlot = new Dictionary<string, int>(
                        StringComparer.OrdinalIgnoreCase);
                    foreach (XmlNode textureNode in textureNodes)
                    {
                        var slot = GetTextureSlot(textureNode);
                        var normalizedSlot = string.IsNullOrWhiteSpace(slot)
                            ? "__unslotted__"
                            : slot.ToLowerInvariant();
                        var slotOccurrence = occurrenceBySlot.GetValueOrDefault(normalizedSlot);
                        occurrenceBySlot[normalizedSlot] = slotOccurrence + 1;

                        var texturePath = Normalize(
                            textureNode.SelectSingleNode("source")?.InnerText ??
                            textureNode.InnerText);
                        if (string.IsNullOrWhiteSpace(texturePath) ||
                            IsTexturePlaceholder(texturePath) ||
                            state.Source.FindFile(texturePath) == null)
                        {
                            continue;
                        }

                        if (!referencesByTexture.TryGetValue(
                                texturePath,
                                out var references))
                        {
                            references = [];
                            referencesByTexture[texturePath] = references;
                        }

                        // Index every material texture reference, not only channels we atlas.
                        // A source DDS cannot be credited as retired while an emissive/custom
                        // slot, duplicate slot occurrence, or unindexed material entry still
                        // points at it.
                        references.Add(new AtlasValueGateSourceReference(
                            mesh,
                            wsModelPath.ToLowerInvariant(),
                            normalizedSlot,
                            slotOccurrence));
                    }
                }
            }

            // Engine rows can point directly at a rigid_model_v2 without a WSModel/material
            // layer. Index its embedded material textures with the same identity used by the
            // synthetic direct-asset candidates, so the value gate can credit source textures
            // that become unreachable after the rigid is rewritten.
            var directRigidPaths = state.UnitCategoryResolution?
                .DirectAssetUsagesByPath.Keys
                .Where(path => Path.GetExtension(path).Equals(
                    ".rigid_model_v2",
                    StringComparison.OrdinalIgnoreCase))
                .Select(Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
                ?? [];
            foreach (var rigidPath in directRigidPaths)
            {
                var rigidFile = state.Source.FindFile(rigidPath);
                if (rigidFile == null)
                    continue;

                try
                {
                    var rigid = ModelFactory.Create().Load(rigidFile.DataSource.ReadData());
                    for (var lodIndex = 0; lodIndex < rigid.ModelList.Length; lodIndex++)
                    {
                        for (var partIndex = 0;
                             partIndex < rigid.ModelList[lodIndex].Length;
                             partIndex++)
                        {
                            var mesh = new MeshKey(rigidPath, lodIndex, partIndex);
                            var material = BuildEmbeddedMaterialDocument(
                                rigid.ModelList[lodIndex][partIndex].Material);
                            var textureNodes = material.SelectNodes("/material/textures/texture");
                            if (textureNodes == null)
                                continue;

                            var occurrenceBySlot = new Dictionary<string, int>(
                                StringComparer.OrdinalIgnoreCase);
                            foreach (XmlNode textureNode in textureNodes)
                            {
                                var slot = GetTextureSlot(textureNode);
                                var normalizedSlot = string.IsNullOrWhiteSpace(slot)
                                    ? "__unslotted__"
                                    : slot.ToLowerInvariant();
                                var slotOccurrence = occurrenceBySlot.GetValueOrDefault(normalizedSlot);
                                occurrenceBySlot[normalizedSlot] = slotOccurrence + 1;

                                var texturePath = Normalize(
                                    textureNode.SelectSingleNode("source")?.InnerText ??
                                    textureNode.InnerText);
                                if (string.IsNullOrWhiteSpace(texturePath) ||
                                    IsTexturePlaceholder(texturePath) ||
                                    state.Source.FindFile(texturePath) == null)
                                {
                                    continue;
                                }

                                if (!referencesByTexture.TryGetValue(
                                        texturePath,
                                        out var references))
                                {
                                    references = [];
                                    referencesByTexture[texturePath] = references;
                                }

                                references.Add(new AtlasValueGateSourceReference(
                                    mesh,
                                    rigidPath.ToLowerInvariant(),
                                    normalizedSlot,
                                    slotOccurrence));
                            }
                        }
                    }
                }
                catch
                {
                    // An unparseable direct rigid is reported/skipped during candidate
                    // discovery; it must not make value-gate indexing fail for other assets.
                }
            }

            var directVmdTextures = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var vmd in state.VmdDocuments.Values)
            {
                var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var childVmds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectVmdReferences(vmd, models, childVmds, textures);
                directVmdTextures.UnionWith(textures.Select(Normalize));
            }

            state.AtlasValueGateRootsByWsModel ??=
                BuildArmyRootsByWsModel(state, CancellationToken.None);

            var result = new Dictionary<string, AtlasValueGateSourceTexture>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var (texturePath, references) in referencesByTexture)
            {
                var file = state.Source.FindFile(texturePath);
                if (file == null)
                    continue;

                DdsBcnResidencyEstimate bcn;
                try
                {
                    if (!DdsBcnResidencyEstimator.TryEstimate(
                            file.DataSource.PeekData(148),
                            out bcn))
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                result[texturePath] = new AtlasValueGateSourceTexture(
                    bcn.Bytes,
                    directVmdTextures.Contains(texturePath),
                    references);
            }

            return result;
        }

        private static bool TryGetGeneratedAtlasBcnCost(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates,
            out long bytes)
            => TryGetGeneratedAtlasBcnCost(
                state,
                candidates,
                out bytes,
                out _);

        private static bool TryGetGeneratedAtlasBcnCost(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates,
            out long bytes,
            out Dictionary<string, long> bytesBySlot)
        {
            bytes = 0;
            bytesBySlot = new Dictionary<string, long>(
                StringComparer.OrdinalIgnoreCase);
            try
            {
                // The value gate only needs a conservative residency estimate. Avoid
                // cropped-content hashing here; execution may deduplicate later and therefore
                // use the same or less BCn memory than this estimate.
                var sharedPlan = CreateSharedAtlasPlan(
                    state,
                    candidates,
                    deduplicateByContent: false);
                foreach (var channel in AtlasChannels)
                {
                    var sourceDimensions = new Dictionary<int, (int Width, int Height)>();
                    foreach (var source in sharedPlan.Batch.Sources)
                    {
                        var representative = source.Representative;
                        if (representative.ResolvedChannels.Contains(channel.Slot) &&
                            representative.ChannelDimensions.TryGetValue(
                                channel.Slot,
                                out var dimensions))
                        {
                            sourceDimensions[source.Id] = GetScaledAtlasDimensions(
                                source.Representative,
                                dimensions.Width,
                                dimensions.Height);
                        }
                    }

                    if (sourceDimensions.Count == 0)
                        continue;

                    var outputDimensions = TextureAtlasBuilder.CalculateOutputDimensions(
                        sharedPlan.Plan,
                        sourceDimensions,
                        PackAtlasMaxSize);
                    var bytesPerBlock = channel.Type is TextureType.BaseColour or TextureType.MaterialMap
                        ? 8
                        : 16;
                    var channelBytes = CalculateBcnMipChainBytes(
                        outputDimensions.Width,
                        outputDimensions.Height,
                        bytesPerBlock);
                    bytesBySlot[channel.Slot] = channelBytes;
                    bytes = checked(bytes + channelBytes);
                }

                return true;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or ArgumentException or OverflowException)
            {
                bytes = 0;
                bytesBySlot.Clear();
                return false;
            }
        }

        private static long CalculateBcnMipChainBytes(
            int width,
            int height,
            int bytesPerBlock)
        {
            long total = 0;
            var mipWidth = Math.Max(1, width);
            var mipHeight = Math.Max(1, height);
            while (true)
            {
                var blocksWide = Math.Max(1L, (mipWidth + 3L) / 4L);
                var blocksHigh = Math.Max(1L, (mipHeight + 3L) / 4L);
                total = checked(total + checked(blocksWide * blocksHigh * bytesPerBlock));

                if (mipWidth == 1 && mipHeight == 1)
                    break;
                mipWidth = Math.Max(1, mipWidth / 2);
                mipHeight = Math.Max(1, mipHeight / 2);
            }

            return total;
        }

        private static int CalculateMergeAffinityScoreWithReplacement(
            IReadOnlyList<List<AtlasCandidate>> batches,
            int leftIndex,
            int rightIndex,
            AtlasBatchSplitProposal proposal,
            IReadOnlyList<MergeAffinityGroup> affinityGroups)
        {
            var batchByMesh = BuildBatchIndexByMesh(batches);
            foreach (var candidate in proposal.Left)
                batchByMesh[candidate.Key] = leftIndex;
            foreach (var candidate in proposal.Right)
                batchByMesh[candidate.Key] = rightIndex;

            var score = 0;
            foreach (var group in affinityGroups)
            {
                score += group.Meshes
                    .Where(batchByMesh.ContainsKey)
                    .GroupBy(mesh => batchByMesh[mesh])
                    .Sum(batch => Math.Max(0, batch.Count() - 1));
            }

            return score;
        }

        private static IReadOnlyList<AtlasBatchSplitProposal> CreateMergeAwareRepartitionProposals(
            IReadOnlyList<AtlasCandidate> currentLeft,
            IReadOnlyList<AtlasCandidate> currentRight,
            IReadOnlyList<MergeAffinityGroup> affinityGroups)
        {
            var combined = currentLeft
                .Concat(currentRight)
                .GroupBy(x => x.Key)
                .Select(group => group.First())
                .ToList();

            var planningGroups = combined
                .GroupBy(GetAtlasPlanningSourceIdentity)
                .Select(group =>
                {
                    var candidates = group
                        .OrderBy(x => x.RootVmdPath, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.Key.LodIndex)
                        .ThenBy(x => x.Key.PartIndex)
                        .ToList();
                    return new AtlasPlanningCandidateGroup(
                        group.Key,
                        candidates,
                        candidates[0],
                        GetCanonicalCrop(candidates[0]).Crop);
                })
                .ToList();

            if (planningGroups.Count < 2)
                return [];

            var proposals = new List<AtlasBatchSplitProposal>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void AddProposal(
                IEnumerable<AtlasPlanningCandidateGroup> leftGroups,
                IEnumerable<AtlasPlanningCandidateGroup> rightGroups)
            {
                var leftGroupList = leftGroups.ToList();
                var rightGroupList = rightGroups.ToList();
                var left = leftGroupList.SelectMany(x => x.Candidates).ToList();
                var right = rightGroupList.SelectMany(x => x.Candidates).ToList();

                if (left.Count < 2 || right.Count < 2)
                    return;

                var signature = string.Join(
                    "\n",
                    left.Select(x => x.Key.ToString())
                        .OrderBy(x => x, StringComparer.Ordinal));
                if (!seen.Add(signature))
                    return;

                proposals.Add(new AtlasBatchSplitProposal(left, right));
            }

            var planningGroupByMesh = new Dictionary<MeshKey, int>();
            for (var groupIndex = 0; groupIndex < planningGroups.Count; groupIndex++)
            {
                foreach (var candidate in planningGroups[groupIndex].Candidates)
                    planningGroupByMesh[candidate.Key] = groupIndex;
            }

            var parent = Enumerable.Range(0, planningGroups.Count).ToArray();

            int Find(int index)
            {
                while (parent[index] != index)
                {
                    parent[index] = parent[parent[index]];
                    index = parent[index];
                }
                return index;
            }

            void Union(int left, int right)
            {
                var leftRoot = Find(left);
                var rightRoot = Find(right);
                if (leftRoot != rightRoot)
                    parent[rightRoot] = leftRoot;
            }

            foreach (var affinity in affinityGroups)
            {
                var groupIndices = affinity.Meshes
                    .Where(planningGroupByMesh.ContainsKey)
                    .Select(mesh => planningGroupByMesh[mesh])
                    .Distinct()
                    .ToArray();

                for (var index = 1; index < groupIndices.Length; index++)
                    Union(groupIndices[0], groupIndices[index]);
            }

            var components = Enumerable.Range(0, planningGroups.Count)
                .GroupBy(Find)
                .Select(component => component
                    .Select(index => planningGroups[index])
                    .ToList())
                .ToList();

            long ComponentArea(IReadOnlyList<AtlasPlanningCandidateGroup> component)
                => component.Sum(group => GetPaddedCropArea(group.Crop));

            int ComponentAffinity(IReadOnlyList<AtlasPlanningCandidateGroup> component)
            {
                var meshes = component
                    .SelectMany(group => group.Candidates)
                    .Select(candidate => candidate.Key)
                    .ToHashSet();
                return affinityGroups.Sum(group =>
                {
                    var count = group.Meshes.Count(meshes.Contains);
                    return Math.Max(0, count - 1);
                });
            }

            void AddBalancedComponentProposal(
                IEnumerable<List<AtlasPlanningCandidateGroup>> orderedComponents)
            {
                var leftComponents = new List<List<AtlasPlanningCandidateGroup>>();
                var rightComponents = new List<List<AtlasPlanningCandidateGroup>>();
                long leftArea = 0;
                long rightArea = 0;

                foreach (var component in orderedComponents)
                {
                    var area = ComponentArea(component);
                    if (leftComponents.Count == 0)
                    {
                        leftComponents.Add(component);
                        leftArea += area;
                    }
                    else if (rightComponents.Count == 0)
                    {
                        rightComponents.Add(component);
                        rightArea += area;
                    }
                    else if (leftArea <= rightArea)
                    {
                        leftComponents.Add(component);
                        leftArea += area;
                    }
                    else
                    {
                        rightComponents.Add(component);
                        rightArea += area;
                    }
                }

                AddProposal(
                    leftComponents.SelectMany(x => x),
                    rightComponents.SelectMany(x => x));
            }

            AddBalancedComponentProposal(
                components
                    .OrderByDescending(ComponentAffinity)
                    .ThenByDescending(ComponentArea));
            AddBalancedComponentProposal(
                components
                    .OrderByDescending(ComponentArea)
                    .ThenByDescending(ComponentAffinity));

            // Also try moving each currently split affinity group wholesale to either side.
            // Move whole planning identities, never individual duplicate source/crop users.
            var currentLeftKeys = currentLeft.Select(x => x.Key).ToHashSet();
            var currentRightKeys = currentRight.Select(x => x.Key).ToHashSet();
            var currentLeftGroupIds = planningGroups
                .Select((group, index) => (group, index))
                .Where(x => x.group.Candidates.Any(candidate => currentLeftKeys.Contains(candidate.Key)))
                .Select(x => x.index)
                .ToHashSet();

            foreach (var affinity in affinityGroups)
            {
                var affinityGroupIds = affinity.Meshes
                    .Where(planningGroupByMesh.ContainsKey)
                    .Select(mesh => planningGroupByMesh[mesh])
                    .Distinct()
                    .ToHashSet();

                if (affinityGroupIds.Count < 2 ||
                    !affinity.Meshes.Any(currentLeftKeys.Contains) ||
                    !affinity.Meshes.Any(currentRightKeys.Contains))
                {
                    continue;
                }

                var moveToLeft = currentLeftGroupIds
                    .Union(affinityGroupIds)
                    .ToHashSet();
                AddProposal(
                    planningGroups.Where((_, index) => moveToLeft.Contains(index)),
                    planningGroups.Where((_, index) => !moveToLeft.Contains(index)));

                var moveToRight = currentLeftGroupIds
                    .Except(affinityGroupIds)
                    .ToHashSet();
                AddProposal(
                    planningGroups.Where((_, index) => moveToRight.Contains(index)),
                    planningGroups.Where((_, index) => !moveToRight.Contains(index)));
            }

            // Reuse the existing physical-layout heuristics as additional candidates. The
            // acceptance rule below rejects any proposal that loses merge affinity.
            foreach (var proposal in CreateNonContiguousSplitProposals(combined))
                AddProposal(
                    proposal.Left
                        .GroupBy(GetAtlasPlanningSourceIdentity)
                        .Select(group => planningGroups.First(
                            planning => planning.Identity == group.Key)),
                    proposal.Right
                        .GroupBy(GetAtlasPlanningSourceIdentity)
                        .Select(group => planningGroups.First(
                            planning => planning.Identity == group.Key)));

            const int maxMergeAwareProposals = 48;
            return proposals.Count <= maxMergeAwareProposals
                ? proposals
                : proposals.Take(maxMergeAwareProposals).ToList();
        }

        private static IReadOnlyList<AtlasBatchSplitProposal> CreateNonContiguousSplitProposals(
            IReadOnlyList<AtlasCandidate> batch)
        {
            var groups = batch
                .GroupBy(GetAtlasPlanningSourceIdentity)
                .Select(group =>
                {
                    var candidates = group
                        .OrderBy(x => x.RootVmdPath, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.Key.LodIndex)
                        .ThenBy(x => x.Key.PartIndex)
                        .ToList();
                    return new AtlasPlanningCandidateGroup(
                        group.Key,
                        candidates,
                        candidates[0],
                        GetCanonicalCrop(candidates[0]).Crop);
                })
                .ToList();

            if (groups.Count < 4)
                return [];

            var proposals = new List<AtlasBatchSplitProposal>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void AddProposals(
                IEnumerable<AtlasPlanningCandidateGroup> orderedGroups,
                IReadOnlyList<int> requestedSplitIndices)
            {
                var ordered = orderedGroups.ToList();
                foreach (var requestedIndex in requestedSplitIndices)
                {
                    var splitIndex = Math.Clamp(requestedIndex, 1, ordered.Count - 1);
                    var leftGroups = ordered.Take(splitIndex).ToList();
                    var rightGroups = ordered.Skip(splitIndex).ToList();
                    var left = leftGroups.SelectMany(x => x.Candidates).ToList();
                    var right = rightGroups.SelectMany(x => x.Candidates).ToList();

                    // A one-candidate atlas has no consolidation benefit and the normal planner
                    // deliberately avoids creating it.
                    if (left.Count < 2 || right.Count < 2)
                        continue;

                    var signature = string.Join(
                        "",
                        leftGroups
                            .Select(x => BuildAtlasPlanningOrderKey(x.Representative))
                            .OrderBy(x => x, StringComparer.Ordinal));
                    if (!seen.Add(signature))
                        continue;

                    proposals.Add(new AtlasBatchSplitProposal(left, right));
                }
            }

            var groupCount = groups.Count;
            var broadSplitIndices = new[]
            {
                1,
                2,
                Math.Max(1, groupCount / 3),
                Math.Max(1, groupCount / 2),
                Math.Min(groupCount - 1, (groupCount * 2) / 3),
                Math.Max(1, groupCount - 2)
            };

            // First separate sources with very different per-channel resolution pressure. A
            // small number of 4x masks/normals can otherwise force the physical size of that
            // entire channel up for every placement in the batch.
            foreach (var channel in AtlasChannels)
            {
                var orderedByChannelPressure = groups
                    .OrderByDescending(x => GetChannelScalePressure(x.Representative, channel.Slot))
                    .ThenByDescending(x => (long)x.Crop.Width * x.Crop.Height)
                    .ThenBy(x => BuildAtlasPlanningOrderKey(x.Representative), StringComparer.Ordinal);
                AddProposals(
                    orderedByChannelPressure,
                    [1, 2, Math.Max(1, groupCount / 2), Math.Max(1, groupCount - 2)]);
            }

            // Wide and tall crops frequently combine into a max-size square atlas. Separating them can
            // turn one expensive 4096x4096 batch into two substantially cheaper rectangular atlases.
            AddProposals(
                groups
                    .OrderBy(x => GetCropAspectScore(x.Crop))
                    .ThenByDescending(x => (long)x.Crop.Width * x.Crop.Height)
                    .ThenBy(x => BuildAtlasPlanningOrderKey(x.Representative), StringComparer.Ordinal),
                broadSplitIndices);

            // Also try isolating the largest placements regardless of aspect. This catches mixed
            // size-class batches where a few large sources force a power-of-two jump.
            AddProposals(
                groups
                    .OrderByDescending(x => (long)x.Crop.Width * x.Crop.Height)
                    .ThenByDescending(x => Math.Max(x.Crop.Width, x.Crop.Height))
                    .ThenBy(x => BuildAtlasPlanningOrderKey(x.Representative), StringComparer.Ordinal),
                broadSplitIndices);

            const int maxNonContiguousSplitEvaluations = 32;
            return proposals.Count <= maxNonContiguousSplitEvaluations
                ? proposals
                : proposals.Take(maxNonContiguousSplitEvaluations).ToList();
        }

        private static double GetChannelScalePressure(AtlasCandidate candidate, string slot)
        {
            if (candidate.ConstantChannels.ContainsKey(slot))
                return 0;

            if (!candidate.ResolvedChannels.Contains(slot) ||
                !candidate.ChannelDimensions.TryGetValue(slot, out var dimensions))
            {
                return 0;
            }

            return Math.Max(
                dimensions.Width / (double)Math.Max(1, candidate.Width),
                dimensions.Height / (double)Math.Max(1, candidate.Height));
        }

        private static double GetCropAspectScore(AtlasCrop crop)
            => Math.Log2(
                Math.Max(1, crop.Width) /
                (double)Math.Max(1, crop.Height));

        private static bool TryGetGeneratedAtlasPixelCost(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates,
            out long pixelCost,
            out bool touchesMaxSize,
            bool deduplicateByContent = false)
        {
            pixelCost = 0;
            touchesMaxSize = false;

            try
            {
                var sharedPlan = CreateSharedAtlasPlan(
                    state,
                    candidates,
                    deduplicateByContent);
                foreach (var channel in AtlasChannels)
                {
                    var sourceDimensions = new Dictionary<int, (int Width, int Height)>();

                    foreach (var source in sharedPlan.Batch.Sources)
                    {
                        var representative = source.Representative;
                        if (representative.ResolvedChannels.Contains(channel.Slot) &&
                            representative.ChannelDimensions.TryGetValue(
                                channel.Slot,
                                out var dimensions))
                        {
                            sourceDimensions[source.Id] = GetScaledAtlasDimensions(
                                source.Representative,
                                dimensions.Width,
                                dimensions.Height);
                        }
                    }

                    // ProcessBatch deliberately emits no texture when every present source
                    // for a channel is uniform/constant, so the planning cost must match that
                    // behavior rather than charging a full atlas for a skipped channel.
                    if (sourceDimensions.Count == 0)
                        continue;

                    var outputDimensions = TextureAtlasBuilder.CalculateOutputDimensions(
                        sharedPlan.Plan,
                        sourceDimensions,
                        PackAtlasMaxSize);

                    pixelCost = checked(
                        pixelCost +
                        (long)outputDimensions.Width * outputDimensions.Height);

                    if (outputDimensions.Width == PackAtlasMaxSize ||
                        outputDimensions.Height == PackAtlasMaxSize)
                    {
                        touchesMaxSize = true;
                    }
                }

                return true;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or ArgumentException or OverflowException)
            {
                pixelCost = 0;
                touchesMaxSize = false;
                return false;
            }
        }

        private static AtlasPlanningSourceIdentity GetAtlasPlanningSourceIdentity(AtlasCandidate candidate)
            => new(
                BuildAtlasTextureSetIdentity(candidate),
                GetCanonicalCrop(candidate).Crop,
                candidate.AtlasResolutionScale);

        private static string BuildAtlasPlanningOrderKey(AtlasCandidate candidate)
        {
            var identity = BuildAtlasTextureSetIdentity(candidate);
            var prefix = string.Join(
                "\u001f",
                identity.SourceWidth,
                identity.SourceHeight,
                identity.BaseColour,
                identity.MaterialMap,
                identity.Normal,
                identity.Mask);

            try
            {
                var crop = GetCanonicalCrop(candidate).Crop;
                return string.Join(
                    "\u001f",
                    prefix,
                    crop.X,
                    crop.Y,
                    crop.Width,
                    crop.Height,
                    candidate.AtlasResolutionScale);
            }
            catch (OverflowException)
            {
                // CanCreatePlan records the real per-mesh error. Sorting must never turn a
                // safely-skippable bad candidate into a failure for the whole pack.
                return $"{prefix}\u001finvalid\u001f{candidate.Key}";
            }
        }

        private static bool CanCreatePlan(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates,
            out string error)
        {
            try
            {
                _ = CreateSharedAtlasPlan(state, candidates);
                error = string.Empty;
                return true;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or ArgumentException or OverflowException)
            {
                error = ex.Message;
                return false;
            }
        }

        private static (SharedAtlasBatch Batch, TextureAtlasPlan Plan) CreateSharedAtlasPlan(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates,
            bool deduplicateByContent = false)
        {
            var mergedBatch = BuildSharedAtlasBatch(
                state,
                candidates,
                mergeCompatibleCrops: true,
                deduplicateByContent);
            try
            {
                var mergedPlan = TextureAtlasBuilder.CreatePlan(
                    ToAtlasLayoutSources(mergedBatch.Sources),
                    TextureAtlasBuilder.DefaultPadding,
                    PackAtlasMaxSize);
                ValidateChannelAtlasDimensions(mergedBatch, mergedPlan);
                return (mergedBatch, mergedPlan);
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or ArgumentException or OverflowException)
            {
                var exactBatch = BuildSharedAtlasBatch(
                    state,
                    candidates,
                    mergeCompatibleCrops: false,
                    deduplicateByContent);
                var exactPlan = TextureAtlasBuilder.CreatePlan(
                    ToAtlasLayoutSources(exactBatch.Sources),
                    TextureAtlasBuilder.DefaultPadding,
                    PackAtlasMaxSize);
                ValidateChannelAtlasDimensions(exactBatch, exactPlan);
                return (exactBatch, exactPlan);
            }
        }

        private static void ValidateChannelAtlasDimensions(
            SharedAtlasBatch batch,
            TextureAtlasPlan plan)
        {
            foreach (var channel in AtlasChannels)
            {
                var sourceDimensions = new Dictionary<int, (int Width, int Height)>();
                foreach (var source in batch.Sources)
                {
                    if (source.Representative.ChannelDimensions.TryGetValue(
                            channel.Slot,
                            out var dimensions))
                    {
                        sourceDimensions[source.Id] = GetScaledAtlasDimensions(
                            source.Representative,
                            dimensions.Width,
                            dimensions.Height);
                    }
                }

                if (sourceDimensions.Count > 0)
                    _ = TextureAtlasBuilder.CalculateOutputDimensions(
                        plan,
                        sourceDimensions,
                        PackAtlasMaxSize);
            }
        }

        private void ProcessBatch(
            BatchState state,
            string atlasScopeKey,
            string progressScope,
            List<AtlasCandidate> candidates,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sharedPlan = CreateSharedAtlasPlan(
                state,
                candidates,
                deduplicateByContent: true);
            var sharedBatch = sharedPlan.Batch;
            var plan = sharedPlan.Plan;
            state.AtlasPlacementsGenerated += sharedBatch.Sources.Count;
            state.AtlasPlacementsReused += candidates.Count - sharedBatch.Sources.Count;
            state.WrappedUvPlacementsCanonicalized += sharedBatch.MappingByMesh.Values.Count(
                x => x.WrappedUvCanonicalized);
            state.ContentDeduplicatedAtlasPlacements += sharedBatch.ContentPlacementsReused;
            state.ContentCanonicalizedMeshReferences += sharedBatch.MappingByMesh.Values.Count(
                x => x.ContentCanonicalized);

            foreach (var candidate in candidates)
            {
                if (candidate.UvIslandNormalization == null)
                    continue;

                var normalization = candidate.UvIslandNormalization;
                state.UvIslandNormalizedMeshes++;
                state.UvIslandsShifted += normalization.ShiftedIslandCount;
                state.UvIslandCropPixelsSaved = checked(
                    state.UvIslandCropPixelsSaved +
                    (long)normalization.OriginalCrop.Width * normalization.OriginalCrop.Height -
                    (long)normalization.NormalizedCrop.Width * normalization.NormalizedCrop.Height);
                state.UvIslandNormalizationEntries.Add(new UvIslandNormalizationReportEntry(
                    candidate.Key,
                    normalization.IslandCount,
                    normalization.ShiftedIslandCount,
                    normalization.OriginalCrop,
                    normalization.NormalizedCrop));
            }

            var atlasBatchId = state.BatchIndex;
            if (!TryGetGeneratedAtlasPixelCost(
                    state,
                    candidates,
                    out var atlasBatchPixelCost,
                    out _,
                    deduplicateByContent: true))
                atlasBatchPixelCost = 0;

            state.AtlasBatchDiagnostics[atlasBatchId] = new AtlasBatchDiagnosticSnapshot(
                candidates.ToList(),
                atlasBatchPixelCost);
            foreach (var candidate in candidates)
            {
                state.AtlasBatchByMesh[candidate.Key] = atlasBatchId;
            }

            state.AtlasBatchCount++;
            var rootsByMesh = BuildCandidateRootVmdPaths(state, candidates);
            if (state.ShareAtlasesAcrossVmdsEnabled &&
                GetBatchRootCount(candidates, rootsByMesh) > 1)
            {
                state.CrossVmdSharedAtlasBatches++;
            }

            foreach (var sourceGroup in candidates.GroupBy(x => sharedBatch.MappingByMesh[x.Key].SourceId))
            {
                var sourceRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var candidate in sourceGroup)
                {
                    if (rootsByMesh.TryGetValue(candidate.Key, out var candidateRoots))
                        sourceRoots.UnionWith(candidateRoots);
                    else
                        sourceRoots.Add(Normalize(candidate.RootVmdPath));
                }

                if (sourceRoots.Count > 1)
                    state.CrossVmdSharedAtlasPlacements++;
            }

            var atlasStem = BuildAtlasStem(atlasScopeKey, state.BatchIndex++);
            var generatedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (var channelIndex = 0; channelIndex < AtlasChannels.Length; channelIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var channel = AtlasChannels[channelIndex];
                ReportProgress(
                    progress,
                    "Building texture atlases",
                    channelIndex + 1,
                    AtlasChannels.Length,
                    $"{progressScope} — {channel.Suffix}");
                var textureBytes = new Dictionary<int, byte[]>();
                var textureBytesByPath = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                var constantSources = new Dictionary<int, TextureAtlasConstantColor>();
                var omitted = new HashSet<int>();

                foreach (var source in sharedBatch.Sources)
                {
                    var representative = source.Representative;
                    if (representative.ResolvedChannels.Contains(channel.Slot))
                    {
                        var sourcePath = GetTexturePath(representative.MaterialDocument, channel.Slot);
                        if (string.IsNullOrWhiteSpace(sourcePath))
                        {
                            throw new InvalidOperationException(
                                $"Resolved atlas channel {channel.Slot} has no texture path for {representative.Key}.");
                        }

                        sourcePath = Normalize(sourcePath);
                        if (!textureBytesByPath.TryGetValue(sourcePath, out var bytes))
                        {
                            var file = FindForRead(state, sourcePath)
                                ?? throw new InvalidOperationException(
                                    $"Resolved atlas texture no longer exists: {sourcePath}");
                            bytes = file.DataSource.ReadData();
                            textureBytesByPath[sourcePath] = bytes;
                        }

                        textureBytes[source.Id] = bytes;
                    }
                    else if (representative.ConstantChannels.TryGetValue(
                                 channel.Slot,
                                 out var constantColor))
                    {
                        constantSources[source.Id] = constantColor;
                    }
                    else
                    {
                        omitted.Add(source.Id);
                    }
                }

                if (textureBytes.Count == 0)
                {
                    // UV remapping cannot change a uniform texture. If every present source
                    // for this channel is constant, keep each material's original shared
                    // constant texture path instead of allocating/compressing an atlas.
                    if (constantSources.Count != 0)
                        state.ConstantOnlyAtlasChannelsSkipped++;
                    continue;
                }

                var sourceDimensions = new Dictionary<int, (int Width, int Height)>();
                foreach (var source in sharedBatch.Sources)
                {
                    if (textureBytes.ContainsKey(source.Id) &&
                        source.Representative.ChannelDimensions.TryGetValue(
                            channel.Slot,
                            out var dimensions))
                    {
                        sourceDimensions[source.Id] = GetScaledAtlasDimensions(
                            source.Representative,
                            dimensions.Width,
                            dimensions.Height);
                    }
                }

                var outputDimensions = TextureAtlasBuilder.CalculateOutputDimensions(
                    plan,
                    sourceDimensions,
                    PackAtlasMaxSize);

                var fileName = $"{atlasStem}_{channel.Suffix}.dds";
                using var mipWriter = PngToDdsImporter.CreateRawBgraMipChainWriter(
                    outputDimensions.Width,
                    outputDimensions.Height,
                    TextureAtlasBuilder.CalculateMipLevelCount(
                        outputDimensions.Width,
                        outputDimensions.Height),
                    channel.Type,
                    GameTypeEnum.Warhammer3,
                    collectCompressionStatistics: AtlasProfilingEnabled);

                var rasterStatistics = AtlasProfilingEnabled
                    ? new TextureAtlasBuildStatistics()
                    : null;
                var rasterStopwatch = Stopwatch.StartNew();
                _ = TextureAtlasBuilder.BuildMipPixels(
                    plan,
                    textureBytes,
                    forceOpaqueAlphaSourceIds: null,
                    omittedSourceIds: omitted,
                    cancellationToken: cancellationToken,
                    heartbeat: () =>
                    {
                        ReportProgress(
                            progress,
                            "Building texture atlases",
                            channelIndex + 1,
                            AtlasChannels.Length,
                            $"{progressScope} — {channel.Suffix}");
                        cancellationToken.ThrowIfCancellationRequested();
                    },
                    constantSources: constantSources,
                    outputWidth: outputDimensions.Width,
                    outputHeight: outputDimensions.Height,
                    mipConsumer: (mipLevel, mipWidth, mipHeight, pixels) =>
                        mipWriter.WriteMip(mipLevel, pixels),
                    retainMipPixels: false,
                    statistics: rasterStatistics);
                rasterStopwatch.Stop();
                var rasterElapsed = rasterStopwatch.Elapsed;
                AddPhaseDuration(state, "Rasterize atlas pixels", rasterElapsed);

                var usesLargeBcSplitCompression = mipWriter.UsesLargeBcSplitCompression;
                if (usesLargeBcSplitCompression)
                    state.LargeBcSplitCompressionChannels++;

                var compressionStopwatch = Stopwatch.StartNew();
                var atlasPackFile = mipWriter.Complete(fileName);
                compressionStopwatch.Stop();
                var compressionElapsed = compressionStopwatch.Elapsed;
                var compressionStatistics = mipWriter.LastCompressionStatistics;
                AddPhaseDuration(state, "Compress atlas DDS", compressionElapsed);
                var atlasPath = Normalize($@"{AtlasDirectory}\{fileName}");

                if (AtlasProfilingEnabled && rasterStatistics != null)
                {
                    state.GeneratedTextureTimings.Add(new GeneratedTextureTimingEntry(
                        atlasPath,
                        outputDimensions.Width,
                        outputDimensions.Height,
                        channel.Type,
                        DDSFormatHelper.GetDDSFormat(GameTypeEnum.Warhammer3, channel.Type),
                        rasterElapsed,
                        compressionElapsed,
                        usesLargeBcSplitCompression,
                        rasterStatistics,
                        compressionStatistics));
                }

                WriteFile(state.Output, atlasPath, atlasPackFile.DataSource.ReadData());
                generatedPaths[channel.Slot] = atlasPath;
                state.GeneratedTexturePaths.Add(atlasPath);
                state.GeneratedTextureDimensions[atlasPath] = outputDimensions;
            }

            state.AtlasPlacementDiagnostics.Add(
                BuildAtlasPlacementDiagnosticSnapshot(
                    atlasStem,
                    plan,
                    sharedBatch,
                    candidates,
                    generatedPaths));

            var rewriteStopwatch = Stopwatch.StartNew();
            for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = candidates[candidateIndex];
                ReportProgress(
                    progress,
                    "Rewriting mesh UVs and materials",
                    candidateIndex + 1,
                    candidates.Count,
                    candidate.Key.ToString());
                var mapping = sharedBatch.MappingByMesh[candidate.Key];
                var placement = plan.Placements.Single(x => x.Id == mapping.SourceId);

                foreach (var vertexIndex in candidate.Model.Mesh.IndexList.Distinct())
                {
                    if (vertexIndex >= candidate.Model.Mesh.VertexList.Length)
                        throw new InvalidOperationException(
                            $"Mesh {candidate.Key} contains invalid vertex index {vertexIndex}.");

                    var uv = candidate.Model.Mesh.VertexList[vertexIndex].Uv;
                    var islandOffsetU = candidate.UvIslandNormalization?.TileOffsetUByVertex[vertexIndex] ?? 0;
                    var islandOffsetV = candidate.UvIslandNormalization?.TileOffsetVByVertex[vertexIndex] ?? 0;
                    var remapped = placement.TransformUv(
                        uv.X + islandOffsetU - mapping.UvOffsetU,
                        uv.Y + islandOffsetV - mapping.UvOffsetV,
                        plan.Width,
                        plan.Height);
                    candidate.Model.Mesh.VertexList[vertexIndex].Uv = new Microsoft.Xna.Framework.Vector2(remapped.U, remapped.V);
                }

                var clonedMaterial = new XmlDocument();
                clonedMaterial.LoadXml(candidate.MaterialDocument.OuterXml);

                var unresolvedMaskPath = GetTexturePath(
                    clonedMaterial,
                    "t_xml_mask");
                if (IsIgnorableUnresolvedAtlasTexture(
                        "t_xml_mask",
                        unresolvedMaskPath) &&
                    FindForRead(state, unresolvedMaskPath) == null)
                {
                    RemoveTextureSlot(clonedMaterial, "t_xml_mask");
                }

                foreach (var channel in AtlasChannels)
                {
                    if (!candidate.ResolvedChannels.Contains(channel.Slot) &&
                        !candidate.ConstantChannels.ContainsKey(channel.Slot))
                        continue;
                    if (generatedPaths.TryGetValue(channel.Slot, out var atlasPath))
                        SetTexturePath(clonedMaterial, channel.Slot, atlasPath);
                }

                var isEmbeddedMaterial = candidate.Usages.Any(
                    usage => !string.IsNullOrWhiteSpace(usage.EmbeddedRigidPath));
                string resolvedMaterialPath;
                if (isEmbeddedMaterial)
                {
                    foreach (var channel in AtlasChannels)
                    {
                        if (generatedPaths.TryGetValue(channel.Slot, out var atlasPath))
                        {
                            ApplyEmbeddedMaterialTexture(
                                candidate.Model.Material,
                                channel.Slot,
                                atlasPath);
                        }
                    }

                    resolvedMaterialPath = "<embedded-rigid-material>";
                }
                else
                {
                    var materialXml = clonedMaterial.OuterXml;
                    var renderingIdentity = GetMaterialRenderingIdentity(clonedMaterial);
                    var materialContentHash = ContentHash(renderingIdentity);
                    if (!state.GeneratedMaterialByContentHash.TryGetValue(
                            materialContentHash,
                            out var generatedMaterial))
                    {
                        var newMaterialPath = BuildMaterialPath(candidate.MaterialPath, candidate.Key);
                        WriteFile(state.Output, newMaterialPath, Encoding.UTF8.GetBytes(materialXml));
                        state.GeneratedMaterialPaths.Add(newMaterialPath);
                        generatedMaterial = new GeneratedMaterialEntry(
                            newMaterialPath,
                            renderingIdentity,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            {
                                candidate.RootVmdPath
                            });
                        state.GeneratedMaterialByContentHash.Add(materialContentHash, generatedMaterial);
                    }
                    else if (!generatedMaterial.RenderingIdentity.Equals(renderingIdentity, StringComparison.Ordinal))
                    {
                        // The hash is only an index. Exact rendering-identity equality remains
                        // the final guard so even a theoretical SHA-256 collision cannot share materials.
                        var newMaterialPath = BuildMaterialPath(candidate.MaterialPath, candidate.Key);
                        WriteFile(state.Output, newMaterialPath, Encoding.UTF8.GetBytes(materialXml));
                        state.GeneratedMaterialPaths.Add(newMaterialPath);
                        generatedMaterial = new GeneratedMaterialEntry(
                            newMaterialPath,
                            renderingIdentity,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            {
                                candidate.RootVmdPath
                            });
                    }
                    else
                    {
                        state.GeneratedMaterialReuses++;
                        if (!generatedMaterial.RootVmdPaths.Contains(candidate.RootVmdPath) &&
                            generatedMaterial.RootVmdPaths.Count != 0)
                        {
                            state.CrossVmdMaterialReuses++;
                        }

                        generatedMaterial.RootVmdPaths.Add(candidate.RootVmdPath);
                    }

                    resolvedMaterialPath = generatedMaterial.Path;
                }

                state.AtlasedMeshes.Add(new AtlasedMeshReportEntry(
                    candidate.RootVmdPath,
                    candidate.Key,
                    candidate.Usages.Select(x => x.AssetPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    candidate.MaterialPath,
                    resolvedMaterialPath,
                    generatedPaths.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));

                foreach (var usage in candidate.Usages)
                {
                    if (usage.MaterialNode != null &&
                        !string.IsNullOrWhiteSpace(usage.WsModelPath))
                    {
                        usage.MaterialNode.InnerText = resolvedMaterialPath;
                        state.ModifiedWsModels.Add(usage.WsModelPath);
                    }
                }

                state.ModifiedRigids.Add(candidate.Key.GeometryPath);
                state.ProcessedMeshes.Add(candidate.Key);
            }
            AddPhaseDuration(state, "Rewrite mesh UVs and materials", rewriteStopwatch.Elapsed);
        }

        private void MergeCompatibleMeshes(
            BatchState state,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var rigidPaths = state.ModifiedRigids
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var rigidIndex = 0; rigidIndex < rigidPaths.Count; rigidIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rigidPath = rigidPaths[rigidIndex];
                ReportProgress(
                    progress,
                    "Merging compatible mesh parts",
                    rigidIndex + 1,
                    rigidPaths.Count,
                    rigidPath);

                if (!state.RigidModels.TryGetValue(rigidPath, out var rmv))
                    continue;

                var wsModels = state.WsDocuments
                    .Where(x =>
                        Normalize(x.Value.SelectSingleNode("/model/geometry")?.InnerText)
                            .Equals(rigidPath, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (wsModels.Count == 0)
                {
                    state.MeshMergeSkipMessages.Add(
                        $"{rigidPath}: no in-pack WSModel material table was available.");
                    continue;
                }

                var wsStructureBeforeMerge = wsModels.ToDictionary(
                    x => x.Key,
                    x => GetWsModelNonMaterialIdentity(x.Value),
                    StringComparer.OrdinalIgnoreCase);
                var assignmentsByWsModel = new Dictionary<string, string[][]>(StringComparer.OrdinalIgnoreCase);
                var wsModelIndexByPath = wsModels
                    .Select((entry, index) => (entry.Key, index))
                    .ToDictionary(x => x.Key, x => x.index, StringComparer.OrdinalIgnoreCase);
                var assignmentsValid = true;
                foreach (var (wsPath, wsDocument) in wsModels)
                {
                    if (!TryReadWsMaterialAssignments(wsDocument, rmv, out var assignments, out var reason))
                    {
                        state.MeshMergeSkipMessages.Add($"{rigidPath}: {wsPath}: {reason}");
                        assignmentsValid = false;
                        break;
                    }

                    assignmentsByWsModel[wsPath] = assignments;
                }

                if (!assignmentsValid)
                    continue;

                var rigidBefore = rmv.ModelList.Sum(x => x.Length);
                var rigidAfter = rigidBefore;
                var rigidChanged = false;

                for (var lodIndex = 0; lodIndex < rmv.ModelList.Length; lodIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var originalModels = rmv.ModelList[lodIndex];
                    if (originalModels.Length < 2)
                        continue;

                    var lodInvariant = CaptureLodGeometryInvariant(originalModels, rigidPath, lodIndex);

                    AnalyzeMeshMergeBlockers(
                        state,
                        rigidPath,
                        lodIndex,
                        originalModels,
                        wsModels.Select(x => x.Key).ToList(),
                        assignmentsByWsModel);

                    var groups = BuildMeshMergeGroups(
                        state,
                        originalModels,
                        lodIndex,
                        wsModels.Select(x => x.Key).ToList(),
                        assignmentsByWsModel);

                    if (groups.All(x => x.PartIndices.Count == 1))
                        continue;

                    var mergedModels = new List<RmvModel>(groups.Count);
                    for (var newPartIndex = 0; newPartIndex < groups.Count; newPartIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var group = groups[newPartIndex];
                        var models = group.PartIndices.Select(x => originalModels[x]).ToList();
                        MergeGeometryInvariantSnapshot? groupInvariant = null;
                        if (models.Count > 1)
                            groupInvariant = CaptureMergeGroupInvariant(models, rigidPath, lodIndex, group.PartIndices);

                        var merged = models.Count == 1
                            ? models[0]
                            : MergeRmvModels(models);

                        if (groupInvariant != null)
                        {
                            ValidateMergedGroupGeometry(
                                groupInvariant,
                                merged,
                                rigidPath,
                                lodIndex,
                                group.PartIndices);
                            state.MeshMergeInvariantGroupCount++;
                        }

                        mergedModels.Add(merged);

                        if (models.Count > 1)
                        {
                            state.MeshMergeEntries.Add(new MeshMergeReportEntry(
                                rigidPath,
                                lodIndex,
                                group.PartIndices.ToArray(),
                                newPartIndex,
                                merged.Mesh.VertexList.Length,
                                group.MaterialPathsByWsModel[0]));
                        }
                    }

                    foreach (var (wsPath, _) in wsModels)
                    {
                        var assignments = assignmentsByWsModel[wsPath];
                        var wsModelIndex = wsModelIndexByPath[wsPath];
                        assignments[lodIndex] = groups
                            .Select(group => group.MaterialPathsByWsModel[wsModelIndex])
                            .ToArray();
                    }

                    ValidateLodGeometryInvariant(
                        lodInvariant,
                        mergedModels,
                        rigidPath,
                        lodIndex);
                    state.MeshMergeInvariantLodCount++;

                    rmv.ModelList[lodIndex] = mergedModels.ToArray();
                    rigidAfter -= originalModels.Length - mergedModels.Count;
                    rigidChanged = true;
                }

                state.MeshPartsBeforeMerging += rigidBefore;
                state.MeshPartsAfterMerging += rigidAfter;

                if (!rigidChanged)
                    continue;

                foreach (var (wsPath, wsDocument) in wsModels)
                {
                    RewriteWsMaterialAssignments(
                        wsDocument,
                        assignmentsByWsModel[wsPath]);

                    var nonMaterialIdentity = GetWsModelNonMaterialIdentity(wsDocument);
                    if (!wsStructureBeforeMerge[wsPath].Equals(nonMaterialIdentity, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Mesh merge changed non-material WSModel structure: {wsPath}");
                    }

                    state.MeshMergeInvariantWsModelCount++;
                    state.ModifiedWsModels.Add(wsPath);
                }
            }
        }

        private static void OptimizeGeometry(
            BatchState state,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var rigidPaths = state.ModifiedRigids
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var rigidIndex = 0; rigidIndex < rigidPaths.Count; rigidIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rigidPath = rigidPaths[rigidIndex];
                ReportProgress(
                    progress,
                    "Optimizing geometry",
                    rigidIndex + 1,
                    rigidPaths.Count,
                    rigidPath);

                if (!state.RigidModels.TryGetValue(rigidPath, out var rmv))
                    continue;

                foreach (var lod in rmv.ModelList)
                {
                    foreach (var model in lod)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var statistics = RigidGeometryOptimizer.Optimize(model);
                        state.GeometryMeshesOptimized++;
                        state.GeometryVerticesBefore += statistics.VerticesBefore;
                        state.GeometryVerticesAfter += statistics.VerticesAfter;
                        state.GeometryUnreferencedVerticesRemoved += statistics.UnreferencedVerticesRemoved;
                        state.GeometryDuplicateVerticesRemoved += statistics.DuplicateVerticesRemoved;
                    }
                }
            }
        }

        private static void AnalyzeMeshMergeBlockers(
            BatchState state,
            string rigidPath,
            int lodIndex,
            IReadOnlyList<RmvModel> models,
            IReadOnlyList<string> wsModelPaths,
            IReadOnlyDictionary<string, string[][]> assignmentsByWsModel)
        {
            var rmvComponents = models
                .Select(GetRmvMergeDiagnosticComponents)
                .ToArray();

            var exactBuckets = Enumerable.Range(0, models.Count)
                .GroupBy(partIndex => string.Join(
                    "\u001e",
                    GetRmvMergeIdentity(models[partIndex]),
                    string.Join(
                        "\u001f",
                        wsModelPaths.Select(
                            wsPath => Normalize(assignmentsByWsModel[wsPath][lodIndex][partIndex])))))
                .Where(group => group.Count() > 1);

            foreach (var bucket in exactBuckets)
            {
                var partIndices = bucket.OrderBy(x => x).ToList();
                var totalVertices = partIndices.Sum(x => models[x].Mesh.VertexList.Length);
                if (totalVertices <= ushort.MaxValue)
                    continue;

                var chunkCount = 1;
                var chunkVertices = 0;
                foreach (var partIndex in partIndices)
                {
                    var vertexCount = models[partIndex].Mesh.VertexList.Length;
                    if (chunkVertices != 0 &&
                        chunkVertices + vertexCount > ushort.MaxValue)
                    {
                        chunkCount++;
                        chunkVertices = 0;
                    }

                    chunkVertices += vertexCount;
                }

                RecordMeshMergeBlocker(
                    state,
                    "16-bit vertex limit",
                    $"{rigidPath} [lod {lodIndex}] parts [{string.Join(", ", partIndices)}]: " +
                    $"{totalVertices:N0} vertices require {chunkCount} merge groups.",
                    Math.Max(1, chunkCount - 1));
            }

            for (var leftIndex = 0; leftIndex < models.Count; leftIndex++)
            {
                for (var rightIndex = leftIndex + 1; rightIndex < models.Count; rightIndex++)
                {
                    var materialComparison = CompareWsMaterialAssignmentsForDiagnostics(
                        state,
                        lodIndex,
                        leftIndex,
                        rightIndex,
                        wsModelPaths,
                        assignmentsByWsModel);

                    var rmvDifferences = GetRmvMergeDiagnosticDifferences(
                        rmvComponents[leftIndex],
                        rmvComponents[rightIndex]);

                    if (rmvDifferences.Count == 0)
                    {
                        if (materialComparison.PathsEqual)
                            continue;

                        var reason = materialComparison.SemanticallyEquivalent
                            ? "WSModel material paths differ but rendering identity is identical"
                            : materialComparison.Reason;

                        RecordMaterialMergeBlockerBreakdown(
                            state,
                            materialComparison);

                        RecordMeshMergeBlocker(
                            state,
                            reason,
                            BuildMeshMergeBlockerExample(
                                rigidPath,
                                lodIndex,
                                leftIndex,
                                rightIndex,
                                materialComparison.Detail));

                        if (reason.Equals(
                                "WSModel texture assignments differ",
                                StringComparison.Ordinal))
                        {
                            AnalyzeTextureBlockedMergeOpportunity(
                                state,
                                rigidPath,
                                lodIndex,
                                leftIndex,
                                rightIndex,
                                models,
                                materialComparison);
                        }

                        continue;
                    }

                    // If the WSModel materials are already equivalent, the RMV side is the only
                    // thing preventing a merge. Report one- or two-field RMV near misses and
                    // intentionally ignore pairs that are different in many unrelated ways.
                    if (!materialComparison.SemanticallyEquivalent || rmvDifferences.Count > 2)
                        continue;

                    var rmvReason = rmvDifferences.Count == 1
                        ? rmvDifferences[0]
                        : "Multiple RMV compatibility fields differ";

                    RecordMeshMergeBlocker(
                        state,
                        rmvReason,
                        BuildMeshMergeBlockerExample(
                            rigidPath,
                            lodIndex,
                            leftIndex,
                            rightIndex,
                            string.Join("; ", rmvDifferences)));
                }
            }
        }

        private static void AnalyzeTextureBlockedMergeOpportunity(
            BatchState state,
            string rigidPath,
            int lodIndex,
            int leftPartIndex,
            int rightPartIndex,
            IReadOnlyList<RmvModel> models,
            MaterialMergeDiagnosticComparison materialComparison)
        {
            var leftKey = new MeshKey(rigidPath, lodIndex, leftPartIndex);
            var rightKey = new MeshKey(rigidPath, lodIndex, rightPartIndex);

            if (!state.AtlasBatchByMesh.TryGetValue(leftKey, out var leftBatchId) ||
                !state.AtlasBatchByMesh.TryGetValue(rightKey, out var rightBatchId) ||
                leftBatchId == rightBatchId)
            {
                return;
            }

            if (!TryGetAtlasOnlyTextureDifference(
                    state,
                    materialComparison.LeftMaterialPath,
                    materialComparison.RightMaterialPath,
                    out var textureDifferenceDetail))
            {
                return;
            }

            var leftVertices = models[leftPartIndex].Mesh.VertexList.Length;
            var rightVertices = models[rightPartIndex].Mesh.VertexList.Length;
            if ((long)leftVertices + rightVertices > ushort.MaxValue)
                return;

            var firstBatchId = Math.Min(leftBatchId, rightBatchId);
            var secondBatchId = Math.Max(leftBatchId, rightBatchId);
            var batchPair = new AtlasBatchPair(firstBatchId, secondBatchId);

            if (!state.AtlasBatchCombinationDiagnostics.TryGetValue(batchPair, out var combination))
            {
                combination = BuildAtlasBatchCombinationDiagnostic(state, batchPair);
                state.AtlasBatchCombinationDiagnostics[batchPair] = combination;
            }

            var leftMaterial = GetMaterialMergeDiagnosticSnapshot(
                state,
                materialComparison.LeftMaterialPath);
            if (leftMaterial == null)
                return;

            var opportunityKey = new TextureMergeOpportunityKey(
                rigidPath,
                lodIndex,
                GetRmvMergeIdentity(models[leftPartIndex]),
                ContentHash(leftMaterial.NonTextureIdentity),
                firstBatchId,
                secondBatchId);

            if (!state.TextureMergeOpportunities.TryGetValue(opportunityKey, out var opportunity))
            {
                opportunity = new TextureMergeOpportunityAccumulator(
                    rigidPath,
                    lodIndex,
                    firstBatchId,
                    secondBatchId,
                    combination);
                state.TextureMergeOpportunities[opportunityKey] = opportunity;
            }

            opportunity.PartIndices.Add(leftPartIndex);
            opportunity.PartIndices.Add(rightPartIndex);
            if (opportunity.Examples.Count < 4)
            {
                var example =
                    $"parts {leftPartIndex}/{rightPartIndex}: {textureDifferenceDetail}";
                if (!opportunity.Examples.Contains(example, StringComparer.Ordinal))
                    opportunity.Examples.Add(example);
            }
        }

        private static AtlasBatchCombinationDiagnostic BuildAtlasBatchCombinationDiagnostic(
            BatchState state,
            AtlasBatchPair batchPair)
        {
            if (!state.AtlasBatchDiagnostics.TryGetValue(batchPair.FirstBatchId, out var first) ||
                !state.AtlasBatchDiagnostics.TryGetValue(batchPair.SecondBatchId, out var second))
            {
                return new AtlasBatchCombinationDiagnostic(
                    false,
                    0,
                    0,
                    0,
                    0,
                    "Atlas batch metadata was unavailable.");
            }

            var baselinePixelCost = checked(first.PixelCost + second.PixelCost);
            var combinedCandidates = first.Candidates
                .Concat(second.Candidates)
                .GroupBy(x => x.Key)
                .Select(group => group.First())
                .ToList();

            if (!TryGetGeneratedAtlasPixelCost(
                    state,
                    combinedCandidates,
                    out var combinedPixelCost,
                    out _))
            {
                return new AtlasBatchCombinationDiagnostic(
                    false,
                    baselinePixelCost,
                    0,
                    0,
                    0,
                    "The combined batch exceeds the atlas size/layout limit.");
            }

            var additionalPixels = combinedPixelCost - baselinePixelCost;
            var additionalPercent = baselinePixelCost > 0
                ? additionalPixels * 100.0 / baselinePixelCost
                : 0;

            return new AtlasBatchCombinationDiagnostic(
                true,
                baselinePixelCost,
                combinedPixelCost,
                additionalPixels,
                additionalPercent,
                string.Empty);
        }

        private static bool TryGetAtlasOnlyTextureDifference(
            BatchState state,
            string leftMaterialPath,
            string rightMaterialPath,
            out string detail)
        {
            detail = string.Empty;
            var left = GetMaterialMergeDiagnosticSnapshot(state, leftMaterialPath);
            var right = GetMaterialMergeDiagnosticSnapshot(state, rightMaterialPath);
            if (left == null || right == null)
                return false;

            var differingSlots = left.TextureAssignments.Keys
                .Union(right.TextureAssignments.Keys, StringComparer.OrdinalIgnoreCase)
                .Where(slot =>
                {
                    left.TextureAssignments.TryGetValue(slot, out var leftPath);
                    right.TextureAssignments.TryGetValue(slot, out var rightPath);
                    return !Normalize(leftPath).Equals(
                        Normalize(rightPath),
                        StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            if (differingSlots.Count == 0)
                return false;

            foreach (var slot in differingSlots)
            {
                left.TextureAssignments.TryGetValue(slot, out var leftPath);
                right.TextureAssignments.TryGetValue(slot, out var rightPath);
                leftPath = Normalize(leftPath);
                rightPath = Normalize(rightPath);

                // Co-locating batches can only resolve a difference caused by generated atlas
                // paths. Missing channels, constants, vanilla textures, and other preserved
                // sources are semantic differences and are intentionally not counted here.
                if (string.IsNullOrWhiteSpace(leftPath) ||
                    string.IsNullOrWhiteSpace(rightPath) ||
                    !state.GeneratedTexturePaths.Contains(leftPath) ||
                    !state.GeneratedTexturePaths.Contains(rightPath))
                {
                    return false;
                }
            }

            detail = string.Join(
                ", ",
                differingSlots.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            return true;
        }

        private static MaterialMergeDiagnosticComparison CompareWsMaterialAssignmentsForDiagnostics(
            BatchState state,
            int lodIndex,
            int leftPartIndex,
            int rightPartIndex,
            IReadOnlyList<string> wsModelPaths,
            IReadOnlyDictionary<string, string[][]> assignmentsByWsModel)
        {
            var pathsEqual = true;
            var semanticallyEquivalent = true;
            var reasons = new HashSet<string>(StringComparer.Ordinal);
            string? firstDetail = null;
            string? firstLeftMaterialPath = null;
            string? firstRightMaterialPath = null;

            foreach (var wsPath in wsModelPaths)
            {
                var leftPath = Normalize(assignmentsByWsModel[wsPath][lodIndex][leftPartIndex]);
                var rightPath = Normalize(assignmentsByWsModel[wsPath][lodIndex][rightPartIndex]);
                if (leftPath.Equals(rightPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                pathsEqual = false;
                firstDetail ??= $"{Path.GetFileName(wsPath)}: {leftPath} <> {rightPath}";
                firstLeftMaterialPath ??= leftPath;
                firstRightMaterialPath ??= rightPath;

                var left = GetMaterialMergeDiagnosticSnapshot(state, leftPath);
                var right = GetMaterialMergeDiagnosticSnapshot(state, rightPath);
                if (left == null || right == null)
                {
                    semanticallyEquivalent = false;
                    reasons.Add("WSModel material could not be inspected");
                    continue;
                }

                if (left.RenderingIdentity.Equals(right.RenderingIdentity, StringComparison.Ordinal))
                    continue;

                semanticallyEquivalent = false;
                if (!left.Shader.Equals(right.Shader, StringComparison.OrdinalIgnoreCase))
                {
                    reasons.Add("WSModel material shader differs");
                }
                else if (!left.TextureIdentity.Equals(right.TextureIdentity, StringComparison.Ordinal) &&
                         left.NonTextureIdentity.Equals(right.NonTextureIdentity, StringComparison.Ordinal))
                {
                    reasons.Add("WSModel texture assignments differ");
                }
                else if (!left.NonTextureIdentity.Equals(right.NonTextureIdentity, StringComparison.Ordinal))
                {
                    reasons.Add("WSModel material parameters differ");
                }
                else
                {
                    reasons.Add("WSModel material rendering state differs");
                }
            }

            if (pathsEqual)
            {
                return new MaterialMergeDiagnosticComparison(
                    true,
                    true,
                    string.Empty,
                    "Material assignments are identical.",
                    string.Empty,
                    string.Empty);
            }

            if (semanticallyEquivalent)
            {
                return new MaterialMergeDiagnosticComparison(
                    false,
                    true,
                    "WSModel material paths differ but rendering identity is identical",
                    firstDetail ?? "Material paths differ.",
                    firstLeftMaterialPath ?? string.Empty,
                    firstRightMaterialPath ?? string.Empty);
            }

            var reason = reasons.Count == 1
                ? reasons.Single()
                : "Multiple WSModel material differences";
            return new MaterialMergeDiagnosticComparison(
                false,
                false,
                reason,
                firstDetail ?? reason,
                firstLeftMaterialPath ?? string.Empty,
                firstRightMaterialPath ?? string.Empty);
        }

        private static MaterialMergeDiagnosticSnapshot? GetMaterialMergeDiagnosticSnapshot(
            BatchState state,
            string materialPath)
        {
            materialPath = Normalize(materialPath);
            if (state.MeshMergeMaterialDiagnostics.TryGetValue(materialPath, out var cached))
                return cached;

            try
            {
                var file = FindForReadStatic(state, materialPath);
                if (file == null)
                {
                    state.MeshMergeMaterialDiagnostics[materialPath] = null;
                    return null;
                }

                var material = GetMaterialDocument(state, materialPath, file);
                var shader = Normalize(material.SelectSingleNode("/material/shader")?.InnerText);

                var textureAssignments = material.SelectNodes("/material/textures/texture")?
                    .Cast<XmlNode>()
                    .Select(node => new
                    {
                        Slot = GetTextureSlot(node),
                        Source = Normalize(
                            node.SelectSingleNode("source")?.InnerText ?? node.InnerText)
                    })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Slot))
                    .GroupBy(x => x.Slot, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.First().Source,
                        StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var textureIdentity = string.Join(
                    "\u001f",
                    textureAssignments
                        .OrderBy(x => x.Key, StringComparer.Ordinal)
                        .Select(x => $"{x.Key}={x.Value}"));

                var nonTexture = new XmlDocument();
                nonTexture.LoadXml(material.OuterXml);
                var nameNode = nonTexture.SelectSingleNode("/material/name");
                nameNode?.ParentNode?.RemoveChild(nameNode);
                var texturesNode = nonTexture.SelectSingleNode("/material/textures");
                texturesNode?.ParentNode?.RemoveChild(texturesNode);

                var snapshot = new MaterialMergeDiagnosticSnapshot(
                    GetMaterialRenderingIdentity(material),
                    shader,
                    textureIdentity,
                    nonTexture.OuterXml,
                    textureAssignments,
                    BuildMaterialLeafFieldMap(nonTexture));
                state.MeshMergeMaterialDiagnostics[materialPath] = snapshot;
                return snapshot;
            }
            catch
            {
                state.MeshMergeMaterialDiagnostics[materialPath] = null;
                return null;
            }
        }

        private static void RecordMaterialMergeBlockerBreakdown(
            BatchState state,
            MaterialMergeDiagnosticComparison comparison)
        {
            var left = GetMaterialMergeDiagnosticSnapshot(state, comparison.LeftMaterialPath);
            var right = GetMaterialMergeDiagnosticSnapshot(state, comparison.RightMaterialPath);
            if (left == null || right == null)
                return;

            if (!left.Shader.Equals(right.Shader, StringComparison.OrdinalIgnoreCase))
            {
                var shaders = new[] { left.Shader, right.Shader }
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                IncrementDiagnosticCount(
                    state.MaterialShaderPairBlockerCounts,
                    $"{FormatDiagnosticValue(shaders[0])} <> {FormatDiagnosticValue(shaders[1])}");
                return;
            }

            if (!left.TextureIdentity.Equals(right.TextureIdentity, StringComparison.Ordinal) &&
                left.NonTextureIdentity.Equals(right.NonTextureIdentity, StringComparison.Ordinal))
            {
                var differingSlots = left.TextureAssignments.Keys
                    .Union(right.TextureAssignments.Keys, StringComparer.OrdinalIgnoreCase)
                    .Where(slot =>
                    {
                        left.TextureAssignments.TryGetValue(slot, out var leftPath);
                        right.TextureAssignments.TryGetValue(slot, out var rightPath);
                        return !Normalize(leftPath).Equals(
                            Normalize(rightPath),
                            StringComparison.OrdinalIgnoreCase);
                    });

                foreach (var slot in differingSlots)
                {
                    left.TextureAssignments.TryGetValue(slot, out var leftPath);
                    right.TextureAssignments.TryGetValue(slot, out var rightPath);

                    var texturePair = new[]
                    {
                        (
                            Class: ClassifyMaterialTexturePath(state, leftPath),
                            Path: Normalize(leftPath)),
                        (
                            Class: ClassifyMaterialTexturePath(state, rightPath),
                            Path: Normalize(rightPath))
                    }.OrderBy(
                        x => $"{x.Class}\0{x.Path}",
                        StringComparer.OrdinalIgnoreCase).ToArray();

                    var blockerKey =
                        $"{slot} [{texturePair[0].Class} <> {texturePair[1].Class}]";
                    if (texturePair.Any(x =>
                            x.Class.Equals("preserved", StringComparison.Ordinal)))
                    {
                        blockerKey +=
                            $": {FormatDiagnosticValue(texturePair[0].Path)} <> " +
                            $"{FormatDiagnosticValue(texturePair[1].Path)}";
                    }

                    IncrementDiagnosticCount(
                        state.MaterialTextureSlotBlockerCounts,
                        blockerKey);
                }

                return;
            }

            if (!left.NonTextureIdentity.Equals(right.NonTextureIdentity, StringComparison.Ordinal))
            {
                foreach (var field in left.NonTextureFields.Keys
                             .Union(right.NonTextureFields.Keys, StringComparer.Ordinal)
                             .Where(field =>
                             {
                                 left.NonTextureFields.TryGetValue(field, out var leftValue);
                                 right.NonTextureFields.TryGetValue(field, out var rightValue);
                                 return !string.Equals(leftValue, rightValue, StringComparison.Ordinal);
                             }))
                {
                    var hasLeftValue = left.NonTextureFields.TryGetValue(field, out var leftValue);
                    var hasRightValue = right.NonTextureFields.TryGetValue(field, out var rightValue);
                    var values = new[]
                    {
                        hasLeftValue ? FormatDiagnosticValue(leftValue!) : "<missing>",
                        hasRightValue ? FormatDiagnosticValue(rightValue!) : "<missing>"
                    }.OrderBy(x => x, StringComparer.Ordinal).ToArray();

                    var fieldLabel = field.StartsWith("param:", StringComparison.Ordinal)
                        ? field["param:".Length..]
                        : field.StartsWith("param-type:", StringComparison.Ordinal)
                            ? $"{field["param-type:".Length..]} (type)"
                            : field;

                    IncrementDiagnosticCount(
                        state.MaterialParameterFieldBlockerCounts,
                        $"{fieldLabel}: {values[0]} <> {values[1]}");
                }
            }
        }

        private static string ClassifyMaterialTexturePath(
            BatchState state,
            string? texturePath)
        {
            var normalized = Normalize(texturePath);
            if (string.IsNullOrWhiteSpace(normalized))
                return "missing";
            if (state.GeneratedTexturePaths.Contains(normalized))
                return "generated-atlas";
            if (state.UniformConstantTexturePaths.Contains(normalized) ||
                IsKnownConstantTexturePath(normalized))
            {
                return "uniform-constant";
            }
            if (IsTexturePlaceholder(normalized))
                return "placeholder";
            return "preserved";
        }

        private static string FormatDiagnosticValue(string value)
            => string.IsNullOrWhiteSpace(value) ? "<none>" : value;

        private static void IncrementDiagnosticCount(
            Dictionary<string, int> counts,
            string key)
        {
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        private static IReadOnlyDictionary<string, string> BuildMaterialLeafFieldMap(
            XmlDocument material)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var root = material.DocumentElement;
            if (root == null)
                return result;

            void Visit(XmlNode node, string path)
            {
                if (node.Name.Equals("param", StringComparison.Ordinal) &&
                    node.ParentNode?.Name.Equals("params", StringComparison.Ordinal) == true)
                {
                    var parameterName = node.SelectSingleNode("name")?.InnerText.Trim();
                    var parameterType = node.SelectSingleNode("type")?.InnerText.Trim();
                    var values = node.SelectNodes("value")?.Cast<XmlNode>().ToList() ?? [];

                    if (!string.IsNullOrWhiteSpace(parameterName))
                    {
                        if (!string.IsNullOrWhiteSpace(parameterType))
                            result[$"param-type:{parameterName}"] = parameterType;

                        for (var valueIndex = 0; valueIndex < values.Count; valueIndex++)
                        {
                            var key = values.Count == 1
                                ? $"param:{parameterName}"
                                : $"param:{parameterName}[{valueIndex}]";
                            result[key] = values[valueIndex].InnerText.Trim();
                        }

                        if (values.Count != 0)
                            return;
                    }
                }

                if (node.Attributes != null)
                {
                    foreach (XmlAttribute attribute in node.Attributes
                                 .Cast<XmlAttribute>()
                                 .OrderBy(x => x.Name, StringComparer.Ordinal))
                    {
                        result[$"{path}/@{attribute.Name}"] = attribute.Value;
                    }
                }

                var elementChildren = node.ChildNodes
                    .Cast<XmlNode>()
                    .Where(x => x.NodeType == XmlNodeType.Element)
                    .ToList();
                if (elementChildren.Count == 0)
                {
                    result[path] = node.InnerText.Trim();
                    return;
                }

                var siblingCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var child in elementChildren)
                {
                    var childIndex = siblingCounts.GetValueOrDefault(child.Name);
                    siblingCounts[child.Name] = childIndex + 1;
                    Visit(child, $"{path}/{child.Name}[{childIndex}]");
                }
            }

            Visit(root, $"/{root.Name}");
            return result;
        }

        private static RmvMergeDiagnosticComponents GetRmvMergeDiagnosticComponents(RmvModel model)
        {
            var material = model.Material.Clone();
            if (material is WeightedMaterial weighted)
            {
                weighted.ModelName = string.Empty;
                weighted.TextureDirectory = string.Empty;
                weighted.TexturesParams = [];
            }

            var materialBytes = MaterialFactory.Create().Save(
                model.CommonHeader.ModelTypeFlag,
                material);

            return new RmvMergeDiagnosticComponents(
                model.CommonHeader.ModelTypeFlag.ToString(),
                model.CommonHeader.RenderFlag.ToString(),
                model.Material.BinaryVertexFormat.ToString(),
                model.CommonHeader.ShaderParams.ShaderName ?? string.Empty,
                ContentHash(Convert.ToHexString(materialBytes)));
        }

        private static List<string> GetRmvMergeDiagnosticDifferences(
            RmvMergeDiagnosticComponents left,
            RmvMergeDiagnosticComponents right)
        {
            var differences = new List<string>(5);

            if (!left.ModelTypeFlag.Equals(right.ModelTypeFlag, StringComparison.Ordinal))
                differences.Add("RMV model type differs");
            if (!left.RenderFlag.Equals(right.RenderFlag, StringComparison.Ordinal))
                differences.Add("RMV render flag differs");
            if (!left.VertexFormat.Equals(right.VertexFormat, StringComparison.Ordinal))
                differences.Add("RMV vertex format differs");
            if (!left.ShaderName.Equals(right.ShaderName, StringComparison.Ordinal))
                differences.Add("RMV shader name differs");
            if (!left.MaterialPayloadHash.Equals(right.MaterialPayloadHash, StringComparison.Ordinal))
                differences.Add("RMV embedded material payload differs");

            return differences;
        }

        private static void RecordMeshMergeBlocker(
            BatchState state,
            string reason,
            string example,
            int occurrences = 1)
        {
            if (state.MeshMergeBlockerCounts.TryGetValue(reason, out var existing))
                state.MeshMergeBlockerCounts[reason] = existing + occurrences;
            else
                state.MeshMergeBlockerCounts[reason] = occurrences;

            if (!state.MeshMergeBlockerExamples.TryGetValue(reason, out var examples))
            {
                examples = [];
                state.MeshMergeBlockerExamples[reason] = examples;
            }

            const int maxExamplesPerReason = 8;
            if (examples.Count < maxExamplesPerReason &&
                !examples.Contains(example, StringComparer.Ordinal))
            {
                examples.Add(example);
            }
        }

        private static string BuildMeshMergeBlockerExample(
            string rigidPath,
            int lodIndex,
            int leftPartIndex,
            int rightPartIndex,
            string detail)
            => $"{rigidPath} [lod {lodIndex}, parts {leftPartIndex}/{rightPartIndex}]: {detail}";

        private static List<MeshMergeGroup> BuildMeshMergeGroups(
            BatchState state,
            IReadOnlyList<RmvModel> models,
            int lodIndex,
            IReadOnlyList<string> wsModelPaths,
            IReadOnlyDictionary<string, string[][]> assignmentsByWsModel)
        {
            var buckets = new Dictionary<string, List<int>>(StringComparer.Ordinal);

            for (var partIndex = 0; partIndex < models.Count; partIndex++)
            {
                var materialPaths = wsModelPaths
                    .Select(wsPath => assignmentsByWsModel[wsPath][lodIndex][partIndex])
                    .ToArray();

                var materialIdentities = materialPaths
                    .Select(path => GetMeshMergeMaterialIdentity(state, path))
                    .ToArray();

                var identity = string.Join(
                    "\u001e",
                    GetRmvMergeIdentity(models[partIndex]),
                    string.Join("\u001f", materialIdentities));

                if (!buckets.TryGetValue(identity, out var parts))
                {
                    parts = [];
                    buckets.Add(identity, parts);
                }

                parts.Add(partIndex);
            }

            var groups = new List<MeshMergeGroup>();
            foreach (var parts in buckets.Values)
            {
                var current = new List<int>();
                var currentVertexCount = 0;

                foreach (var partIndex in parts.OrderBy(x => x))
                {
                    var vertexCount = models[partIndex].Mesh.VertexList.Length;
                    if (vertexCount > ushort.MaxValue)
                    {
                        if (current.Count != 0)
                        {
                            groups.Add(CreateMeshMergeGroup(
                                current,
                                lodIndex,
                                wsModelPaths,
                                assignmentsByWsModel));
                            current = [];
                            currentVertexCount = 0;
                        }

                        groups.Add(CreateMeshMergeGroup(
                            [partIndex],
                            lodIndex,
                            wsModelPaths,
                            assignmentsByWsModel));
                        continue;
                    }

                    if (current.Count != 0 &&
                        currentVertexCount + vertexCount > ushort.MaxValue)
                    {
                        groups.Add(CreateMeshMergeGroup(
                            current,
                            lodIndex,
                            wsModelPaths,
                            assignmentsByWsModel));
                        current = [];
                        currentVertexCount = 0;
                    }

                    current.Add(partIndex);
                    currentVertexCount += vertexCount;
                }

                if (current.Count != 0)
                {
                    groups.Add(CreateMeshMergeGroup(
                        current,
                        lodIndex,
                        wsModelPaths,
                        assignmentsByWsModel));
                }
            }

            foreach (var group in groups.Where(x => x.PartIndices.Count > 1))
            {
                var mergedAcrossEquivalentPaths = wsModelPaths.Any(wsPath =>
                    group.PartIndices
                        .Select(partIndex => Normalize(assignmentsByWsModel[wsPath][lodIndex][partIndex]))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Skip(1)
                        .Any());

                if (mergedAcrossEquivalentPaths)
                    state.SemanticMaterialPathMergeParts += group.PartIndices.Count - 1;
            }

            return groups
                .OrderBy(x => x.PartIndices.Min())
                .ToList();
        }

        private static string GetMeshMergeMaterialIdentity(
            BatchState state,
            string materialPath)
        {
            var normalizedPath = Normalize(materialPath);
            var snapshot = GetMaterialMergeDiagnosticSnapshot(state, normalizedPath);
            return snapshot == null
                ? $"path:{normalizedPath}"
                : $"render:{snapshot.RenderingIdentity}";
        }

        private static MeshMergeGroup CreateMeshMergeGroup(
            List<int> partIndices,
            int lodIndex,
            IReadOnlyList<string> wsModelPaths,
            IReadOnlyDictionary<string, string[][]> assignmentsByWsModel)
        {
            var representativePart = partIndices[0];
            var materialPaths = wsModelPaths
                .Select(wsPath => assignmentsByWsModel[wsPath][lodIndex][representativePart])
                .ToArray();

            return new MeshMergeGroup(partIndices.ToList(), materialPaths);
        }

        private static string GetRmvMergeIdentity(RmvModel model)
        {
            var material = model.Material.Clone();
            if (material is WeightedMaterial weighted)
            {
                // WSModel materials own texture/shader selection in this workflow. Keep all
                // transform/bone/parameter state strict, but ignore redundant embedded texture
                // paths and descriptive names that do not affect the merged geometry.
                weighted.ModelName = string.Empty;
                weighted.TextureDirectory = string.Empty;
                weighted.TexturesParams = [];
            }

            var materialBytes = MaterialFactory.Create().Save(
                model.CommonHeader.ModelTypeFlag,
                material);

            var shaderParams = model.CommonHeader.ShaderParams;

            var identity = string.Join(
                "|",
                model.CommonHeader.ModelTypeFlag,
                model.CommonHeader.RenderFlag,
                model.Material.BinaryVertexFormat,
                shaderParams.ShaderName,
                // UnknownValues and AllZeroValues are preserved from the representative RMV
                // header when meshes are merged, but intentionally excluded from compatibility.
                // Asset Editor has no known semantics for either field, and Morrigan contains
                // otherwise-identical render groups fragmented only by non-zero garbage bytes.
                Convert.ToHexString(materialBytes));

            return ContentHash(identity);
        }

        private static MergeGeometryInvariantSnapshot CaptureMergeGroupInvariant(
            IReadOnlyList<RmvModel> models,
            string rigidPath,
            int lodIndex,
            IReadOnlyList<int> partIndices)
        {
            var vertexCount = models.Sum(x => x.Mesh.VertexList.Length);
            var indexCount = models.Sum(x => x.Mesh.IndexList.Length);
            if (indexCount % 3 != 0)
            {
                throw new InvalidOperationException(
                    $"Cannot merge {rigidPath} LOD {lodIndex} parts " +
                    $"[{string.Join(", ", partIndices)}]: index count {indexCount} is not divisible by 3.");
            }

            var expectedIndices = new ushort[indexCount];
            var indexOffset = 0;
            var vertexOffset = 0;
            foreach (var model in models)
            {
                for (var i = 0; i < model.Mesh.IndexList.Length; i++)
                {
                    var sourceIndex = model.Mesh.IndexList[i];
                    if (sourceIndex >= model.Mesh.VertexList.Length)
                    {
                        throw new InvalidOperationException(
                            $"Cannot merge {rigidPath} LOD {lodIndex}: source mesh contains " +
                            $"invalid vertex index {sourceIndex} for {model.Mesh.VertexList.Length} vertices.");
                    }

                    var mergedIndex = checked(sourceIndex + vertexOffset);
                    if (mergedIndex > ushort.MaxValue)
                    {
                        throw new InvalidOperationException(
                            $"Cannot merge {rigidPath} LOD {lodIndex}: merged index exceeds 16-bit range.");
                    }

                    expectedIndices[indexOffset++] = (ushort)mergedIndex;
                }

                vertexOffset += model.Mesh.VertexList.Length;
            }

            return new MergeGeometryInvariantSnapshot(
                vertexCount,
                indexCount,
                indexCount / 3,
                ComputeVertexSequenceHash(models.SelectMany(x => x.Mesh.VertexList)),
                expectedIndices,
                models.Min(x => x.CommonHeader.BoundingBox.MinimumX),
                models.Min(x => x.CommonHeader.BoundingBox.MinimumY),
                models.Min(x => x.CommonHeader.BoundingBox.MinimumZ),
                models.Max(x => x.CommonHeader.BoundingBox.MaximumX),
                models.Max(x => x.CommonHeader.BoundingBox.MaximumY),
                models.Max(x => x.CommonHeader.BoundingBox.MaximumZ));
        }

        private static void ValidateMergedGroupGeometry(
            MergeGeometryInvariantSnapshot expected,
            RmvModel merged,
            string rigidPath,
            int lodIndex,
            IReadOnlyList<int> partIndices)
        {
            var label = $"{rigidPath} LOD {lodIndex} parts [{string.Join(", ", partIndices)}]";

            if (merged.Mesh.VertexList.Length != expected.VertexCount)
            {
                throw new InvalidOperationException(
                    $"Mesh merge geometry invariant failed for {label}: vertex count changed " +
                    $"from {expected.VertexCount} to {merged.Mesh.VertexList.Length}.");
            }

            if (merged.Mesh.IndexList.Length != expected.IndexCount)
            {
                throw new InvalidOperationException(
                    $"Mesh merge geometry invariant failed for {label}: index count changed " +
                    $"from {expected.IndexCount} to {merged.Mesh.IndexList.Length}.");
            }

            if (merged.Mesh.IndexList.Length % 3 != 0 ||
                merged.Mesh.IndexList.Length / 3 != expected.TriangleCount)
            {
                throw new InvalidOperationException(
                    $"Mesh merge geometry invariant failed for {label}: triangle count changed.");
            }

            var mergedVertexHash = ComputeVertexSequenceHash(merged.Mesh.VertexList);
            if (!expected.VertexHash.Equals(mergedVertexHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Mesh merge geometry invariant failed for {label}: vertex data/order changed.");
            }

            if (!merged.Mesh.IndexList.SequenceEqual(expected.ExpectedIndices))
            {
                throw new InvalidOperationException(
                    $"Mesh merge geometry invariant failed for {label}: triangle/index mapping changed.");
            }

            var bounds = merged.CommonHeader.BoundingBox;
            if (!bounds.MinimumX.Equals(expected.MinimumX) ||
                !bounds.MinimumY.Equals(expected.MinimumY) ||
                !bounds.MinimumZ.Equals(expected.MinimumZ) ||
                !bounds.MaximumX.Equals(expected.MaximumX) ||
                !bounds.MaximumY.Equals(expected.MaximumY) ||
                !bounds.MaximumZ.Equals(expected.MaximumZ))
            {
                throw new InvalidOperationException(
                    $"Mesh merge geometry invariant failed for {label}: merged bounds do not equal " +
                    $"the union of source bounds.");
            }
        }

        private static LodGeometryInvariantSnapshot CaptureLodGeometryInvariant(
            IReadOnlyList<RmvModel> models,
            string rigidPath,
            int lodIndex)
        {
            var indexCount = models.Sum(x => x.Mesh.IndexList.Length);
            if (indexCount % 3 != 0)
            {
                throw new InvalidOperationException(
                    $"Cannot merge {rigidPath} LOD {lodIndex}: total index count {indexCount} is not divisible by 3.");
            }

            return new LodGeometryInvariantSnapshot(
                models.Sum(x => x.Mesh.VertexList.Length),
                indexCount,
                indexCount / 3);
        }

        private static void ValidateLodGeometryInvariant(
            LodGeometryInvariantSnapshot expected,
            IReadOnlyList<RmvModel> models,
            string rigidPath,
            int lodIndex)
        {
            var vertexCount = models.Sum(x => x.Mesh.VertexList.Length);
            var indexCount = models.Sum(x => x.Mesh.IndexList.Length);
            if (indexCount % 3 != 0)
            {
                throw new InvalidOperationException(
                    $"Mesh merge geometry invariant failed for {rigidPath} LOD {lodIndex}: " +
                    $"post-merge index count {indexCount} is not divisible by 3.");
            }

            var triangleCount = indexCount / 3;
            if (vertexCount != expected.VertexCount ||
                indexCount != expected.IndexCount ||
                triangleCount != expected.TriangleCount)
            {
                throw new InvalidOperationException(
                    $"Mesh merge geometry invariant failed for {rigidPath} LOD {lodIndex}: " +
                    $"geometry totals changed from {expected.VertexCount} vertices / " +
                    $"{expected.IndexCount} indices / {expected.TriangleCount} triangles to " +
                    $"{vertexCount} / {indexCount} / {triangleCount}.");
            }
        }

        private static string ComputeVertexSequenceHash(IEnumerable<CommonVertex> vertices)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            static void AppendInt32(IncrementalHash hash, int value)
                => hash.AppendData(BitConverter.GetBytes(value));

            static void AppendSingle(IncrementalHash hash, float value)
                => AppendInt32(hash, BitConverter.SingleToInt32Bits(value));

            static void AppendBytes(IncrementalHash hash, byte[]? values)
            {
                if (values == null)
                {
                    AppendInt32(hash, -1);
                    return;
                }

                AppendInt32(hash, values.Length);
                hash.AppendData(values);
            }

            static void AppendSingles(IncrementalHash hash, float[]? values)
            {
                if (values == null)
                {
                    AppendInt32(hash, -1);
                    return;
                }

                AppendInt32(hash, values.Length);
                foreach (var value in values)
                    AppendSingle(hash, value);
            }

            foreach (var vertex in vertices)
            {
                AppendSingle(hash, vertex.Position.X);
                AppendSingle(hash, vertex.Position.Y);
                AppendSingle(hash, vertex.Position.Z);
                AppendSingle(hash, vertex.Position.W);

                AppendSingle(hash, vertex.Normal.X);
                AppendSingle(hash, vertex.Normal.Y);
                AppendSingle(hash, vertex.Normal.Z);
                AppendSingle(hash, vertex.BiNormal.X);
                AppendSingle(hash, vertex.BiNormal.Y);
                AppendSingle(hash, vertex.BiNormal.Z);
                AppendSingle(hash, vertex.Tangent.X);
                AppendSingle(hash, vertex.Tangent.Y);
                AppendSingle(hash, vertex.Tangent.Z);

                AppendSingle(hash, vertex.Uv.X);
                AppendSingle(hash, vertex.Uv.Y);
                AppendSingle(hash, vertex.Uv1.X);
                AppendSingle(hash, vertex.Uv1.Y);

                AppendSingle(hash, vertex.Colour.X);
                AppendSingle(hash, vertex.Colour.Y);
                AppendSingle(hash, vertex.Colour.Z);
                AppendSingle(hash, vertex.Colour.W);

                AppendBytes(hash, vertex.BoneIndex);
                AppendSingles(hash, vertex.BoneWeight);
                AppendInt32(hash, vertex.WeightCount);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }

        private static string GetWsModelNonMaterialIdentity(XmlDocument document)
        {
            var clone = new XmlDocument();
            clone.LoadXml(document.OuterXml);

            var materialNodes = clone.SelectNodes("/model/materials/material");
            if (materialNodes != null)
            {
                foreach (XmlNode materialNode in materialNodes.Cast<XmlNode>().ToList())
                    materialNode.ParentNode?.RemoveChild(materialNode);
            }

            return clone.OuterXml;
        }

        private static RmvModel MergeRmvModels(IReadOnlyList<RmvModel> models)
        {
            if (models.Count == 0)
                throw new ArgumentException("At least one RMV model is required.", nameof(models));
            if (models.Count == 1)
                return models[0];

            var totalVertices = models.Sum(x => x.Mesh.VertexList.Length);
            if (totalVertices > ushort.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Merged RMV mesh would contain {totalVertices} vertices, exceeding the 16-bit index limit.");
            }

            var vertices = new List<CommonVertex>(totalVertices);
            var indices = new List<ushort>(models.Sum(x => x.Mesh.IndexList.Length));
            var vertexOffset = 0;

            foreach (var model in models)
            {
                vertices.AddRange(model.Mesh.VertexList);

                foreach (var index in model.Mesh.IndexList)
                {
                    var mergedIndex = checked(index + vertexOffset);
                    if (mergedIndex > ushort.MaxValue)
                        throw new InvalidOperationException("Merged RMV mesh index exceeds 16-bit range.");
                    indices.Add((ushort)mergedIndex);
                }

                vertexOffset += model.Mesh.VertexList.Length;
            }

            var header = models[0].CommonHeader;
            header.BoundingBox.MinimumX = models.Min(x => x.CommonHeader.BoundingBox.MinimumX);
            header.BoundingBox.MinimumY = models.Min(x => x.CommonHeader.BoundingBox.MinimumY);
            header.BoundingBox.MinimumZ = models.Min(x => x.CommonHeader.BoundingBox.MinimumZ);
            header.BoundingBox.MaximumX = models.Max(x => x.CommonHeader.BoundingBox.MaximumX);
            header.BoundingBox.MaximumY = models.Max(x => x.CommonHeader.BoundingBox.MaximumY);
            header.BoundingBox.MaximumZ = models.Max(x => x.CommonHeader.BoundingBox.MaximumZ);

            return new RmvModel
            {
                CommonHeader = header,
                Material = models[0].Material.Clone(),
                Mesh = new RmvMesh
                {
                    VertexList = vertices.ToArray(),
                    IndexList = indices.ToArray()
                }
            };
        }

        private static bool TryReadWsMaterialAssignments(
            XmlDocument document,
            RmvFile rmv,
            out string[][] assignments,
            out string reason)
        {
            assignments = new string[rmv.ModelList.Length][];
            reason = string.Empty;

            var materialNodes = document.SelectNodes("/model/materials/material");
            if (materialNodes == null)
            {
                reason = "WSModel has no material table.";
                return false;
            }

            var byKey = new Dictionary<(int Lod, int Part), string>();
            foreach (XmlNode node in materialNodes)
            {
                if (!TryParseIndex(node, "lod_index", out var lodIndex) ||
                    !TryParseIndex(node, "part_index", out var partIndex))
                {
                    reason = "WSModel contains a material entry without valid lod_index/part_index.";
                    return false;
                }

                if (!byKey.TryAdd((lodIndex, partIndex), Normalize(node.InnerText)))
                {
                    reason = $"WSModel contains duplicate material entry for LOD {lodIndex}, part {partIndex}.";
                    return false;
                }
            }

            var expectedCount = 0;
            for (var lodIndex = 0; lodIndex < rmv.ModelList.Length; lodIndex++)
            {
                assignments[lodIndex] = new string[rmv.ModelList[lodIndex].Length];
                expectedCount += assignments[lodIndex].Length;

                for (var partIndex = 0; partIndex < assignments[lodIndex].Length; partIndex++)
                {
                    if (!byKey.TryGetValue((lodIndex, partIndex), out var materialPath) ||
                        string.IsNullOrWhiteSpace(materialPath))
                    {
                        reason = $"WSModel is missing material entry for LOD {lodIndex}, part {partIndex}.";
                        return false;
                    }

                    assignments[lodIndex][partIndex] = materialPath;
                }
            }

            if (byKey.Count != expectedCount)
            {
                reason =
                    $"WSModel material table contains {byKey.Count} entries but rigid expects {expectedCount}.";
                return false;
            }

            return true;
        }

        private static void RewriteWsMaterialAssignments(
            XmlDocument document,
            IReadOnlyList<string[]> assignments)
        {
            var materialsNode = document.SelectSingleNode("/model/materials")
                ?? throw new InvalidOperationException("WSModel has no materials node.");

            var existingNodes = materialsNode.SelectNodes("material");
            if (existingNodes != null)
            {
                foreach (XmlNode node in existingNodes.Cast<XmlNode>().ToList())
                    materialsNode.RemoveChild(node);
            }

            for (var lodIndex = 0; lodIndex < assignments.Count; lodIndex++)
            {
                for (var partIndex = 0; partIndex < assignments[lodIndex].Length; partIndex++)
                {
                    var materialNode = document.CreateElement("material");
                    materialNode.SetAttribute("lod_index", lodIndex.ToString());
                    materialNode.SetAttribute("part_index", partIndex.ToString());
                    materialNode.InnerText = assignments[lodIndex][partIndex];
                    materialsNode.AppendChild(materialNode);
                }
            }
        }

        private void SaveModifiedDocuments(
            BatchState state,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var total = state.ModifiedRigids.Count + state.ModifiedWsModels.Count;
            var current = 0;
            var replacements = new List<NewPackFileEntry>(total);

            var serializeStopwatch = Stopwatch.StartNew();
            var rigidPaths = state.ModifiedRigids
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var rigidReplacements = new NewPackFileEntry[rigidPaths.Length];
            var serializedRigidCount = 0;
            var rigidSerializationWorkers = Math.Min(4, Math.Max(1, Environment.ProcessorCount));

            ReportProgress(
                progress,
                "Serializing modified rigids",
                current,
                total,
                $"{rigidPaths.Length:N0} rigid(s) — {rigidSerializationWorkers} worker(s)");

            // Run the bounded parallel work away from the UI thread, but keep progress reporting
            // on the caller thread. TextureAtlasProgressWindow's progress implementation pumps
            // the WPF dispatcher and must never be called from Parallel.For worker threads.
            var rigidSerializationTask = Task.Run(
                () => Parallel.For(
                    0,
                    rigidPaths.Length,
                    new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = rigidSerializationWorkers
                    },
                    rigidIndex =>
                    {
                        var rigidPath = rigidPaths[rigidIndex];
                        var rmv = state.RigidModels[rigidPath];
                        rmv.RecalculateOffsets();

                        // ValidateOutput reloads every rewritten rigid after all replacements are
                        // committed, so doing ModelFactory.Save's immediate round-trip load here would
                        // validate the same bytes twice.
                        var data = ModelFactory.Create().Save(
                            rmv,
                            validateByReloading: false,
                            logProgress: false);
                        rigidReplacements[rigidIndex] = CreateReplacementEntry(rigidPath, data);
                        Interlocked.Increment(ref serializedRigidCount);
                    }),
                CancellationToken.None);

            try
            {
                while (!rigidSerializationTask.IsCompleted)
                {
                    Thread.Sleep(25);
                    ReportProgress(
                        progress,
                        "Serializing modified rigids",
                        current + Volatile.Read(ref serializedRigidCount),
                        total,
                        $"{rigidPaths.Length:N0} rigid(s) — {rigidSerializationWorkers} worker(s)");
                }

                rigidSerializationTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Ensure the serialization workers have observed cancellation before the output
                // pack and its in-memory rigid state are torn down.
                try
                {
                    rigidSerializationTask.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }

                throw;
            }

            ReportProgress(
                progress,
                "Serializing modified rigids",
                current + rigidPaths.Length,
                total,
                $"{rigidPaths.Length:N0} rigid(s) serialized");

            replacements.AddRange(rigidReplacements);
            current += rigidPaths.Length;
            AddPhaseDuration(state, "Serialize modified rigids", serializeStopwatch.Elapsed);

            serializeStopwatch.Restart();
            foreach (var wsPath in state.ModifiedWsModels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(progress, "Serializing modified WSModels", ++current, total, wsPath);

                var doc = state.WsDocuments[wsPath];
                replacements.Add(CreateReplacementEntry(
                    wsPath,
                    Encoding.UTF8.GetBytes(doc.OuterXml)));
            }
            AddPhaseDuration(state, "Serialize modified WSModels", serializeStopwatch.Elapsed);

            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(
                progress,
                "Replacing modified assets",
                replacements.Count,
                replacements.Count,
                $"{replacements.Count:N0} files");

            var replaceStopwatch = Stopwatch.StartNew();
            _packFileService.AddFilesToPack(state.Output, replacements);
            AddPhaseDuration(state, "Replace modified assets in pack", replaceStopwatch.Elapsed);
        }

        private static NewPackFileEntry CreateReplacementEntry(string fullPath, byte[] data)
        {
            fullPath = Normalize(fullPath);
            var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            var name = Path.GetFileName(fullPath);
            return new NewPackFileEntry(
                directory,
                PackFile.CreateFromBytes(name, data));
        }

        private void PruneUnusedAssetFiles(
            BatchState state,
            HashSet<string> originalReachable,
            HashSet<string> currentReachable,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, "Pruning unused assets");
            var toRemove = new HashSet<string>(
                originalReachable.Except(currentReachable, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

            var outputPaths = state.Output.GetAllFiles().Keys.ToList();
            for (var outputIndex = 0; outputIndex < outputPaths.Count; outputIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = outputPaths[outputIndex];
                if (outputIndex == 0 || outputIndex == outputPaths.Count - 1 || outputIndex % 25 == 0)
                {
                    ReportProgress(
                        progress,
                        "Finding unused assets",
                        outputIndex + 1,
                        outputPaths.Count,
                        path);
                }

                var normalized = Normalize(path);
                if (normalized.StartsWith(AtlasDirectory, StringComparison.OrdinalIgnoreCase) &&
                    !currentReachable.Contains(normalized))
                {
                    toRemove.Add(normalized);
                }
            }

            var protectStopwatch = Stopwatch.StartNew();
            ProtectStillReferencedAssets(
                state.Output,
                toRemove,
                cancellationToken,
                progress);
            AddPhaseDuration(state, "Protect referenced assets", protectStopwatch.Elapsed);

            var removePaths = toRemove
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Where(state.Output.ContainsFile)
                .ToList();

            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(
                progress,
                "Pruning unused assets",
                removePaths.Count,
                removePaths.Count,
                $"{removePaths.Count:N0} files");

            var deleteStopwatch = Stopwatch.StartNew();
            _packFileService.DeleteFiles(state.Output, removePaths);
            AddPhaseDuration(state, "Delete pruned assets", deleteStopwatch.Elapsed);
            state.RemovedFiles.AddRange(removePaths);
        }

        private static void ProtectStillReferencedAssets(
            IPackFileContainer output,
            HashSet<string> toRemove,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            if (toRemove.Count == 0)
                return;

            var reverseReferences = BuildReverseAssetReferences(
                output,
                cancellationToken,
                progress);

            // Removing one candidate can make another candidate unsafe to remove. Iterate until
            // every remaining candidate is referenced only by other files that are also being
            // removed, or has no remaining in-pack referrer at all.
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var protectedPaths = toRemove
                    .Where(path =>
                        reverseReferences.TryGetValue(path, out var referrers) &&
                        referrers.Any(referrer => !toRemove.Contains(referrer)))
                    .ToList();

                if (protectedPaths.Count == 0)
                    break;

                foreach (var path in protectedPaths)
                    toRemove.Remove(path);
            }
        }

        private static Dictionary<string, HashSet<string>> BuildReverseAssetReferences(
            IPackFileContainer container,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var reverse = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            void AddReference(string sourcePath, string? targetPath)
            {
                var source = Normalize(sourcePath);
                var target = Normalize(targetPath);
                if (string.IsNullOrWhiteSpace(source) ||
                    string.IsNullOrWhiteSpace(target) ||
                    IsTexturePlaceholder(target))
                {
                    return;
                }

                if (!reverse.TryGetValue(target, out var referrers))
                {
                    referrers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    reverse[target] = referrers;
                }

                referrers.Add(source);
            }

            var allFiles = container.GetAllFiles().ToList();
            for (var fileIndex = 0; fileIndex < allFiles.Count; fileIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (path, file) = allFiles[fileIndex];
                if (fileIndex == 0 || fileIndex == allFiles.Count - 1 || fileIndex % 25 == 0)
                {
                    ReportProgress(
                        progress,
                        "Checking retained references",
                        fileIndex + 1,
                        allFiles.Count,
                        path);
                }

                var extension = Path.GetExtension(path);

                try
                {
                    if (path.EndsWith(".xml.material", StringComparison.OrdinalIgnoreCase))
                    {
                        var materialDoc = LoadXml(file);
                        var textureNodes = materialDoc.SelectNodes("/material/textures/texture");
                        if (textureNodes == null)
                            continue;

                        foreach (XmlNode textureNode in textureNodes)
                        {
                            AddReference(
                                path,
                                textureNode.SelectSingleNode("source")?.InnerText ?? textureNode.InnerText);
                        }

                        continue;
                    }

                    if (extension.Equals(".wsmodel", StringComparison.OrdinalIgnoreCase))
                    {
                        var wsDoc = LoadXml(file);
                        AddReference(path, wsDoc.SelectSingleNode("/model/geometry")?.InnerText);

                        var materialNodes = wsDoc.SelectNodes("/model/materials/material");
                        if (materialNodes == null)
                            continue;

                        foreach (XmlNode materialNode in materialNodes)
                            AddReference(path, materialNode.InnerText);

                        continue;
                    }

                    if (extension.Equals(".rigid_model_v2", StringComparison.OrdinalIgnoreCase))
                    {
                        var rigid = ModelFactory.Create().Load(file.DataSource.ReadData());
                        foreach (var lod in rigid.ModelList)
                        {
                            foreach (var model in lod)
                            {
                                foreach (var texture in model.Material.GetAllTextures())
                                    AddReference(path, texture.Path);
                            }
                        }

                        continue;
                    }

                    if (extension.Equals(".variantmeshdefinition", StringComparison.OrdinalIgnoreCase))
                    {
                        var vmd = VariantMeshDefinitionLoader.Load(file);
                        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var childVmds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        CollectVmdReferences(vmd, models, childVmds, textures);

                        foreach (var target in models)
                            AddReference(path, target);
                        foreach (var target in childVmds)
                            AddReference(path, target);
                        foreach (var target in textures)
                            AddReference(path, target);
                    }
                }
                catch
                {
                    // Cleanup is deliberately conservative, but a malformed unrelated asset
                    // should not make the whole atlas conversion fail. Converted dependencies
                    // are validated separately before the pack is written.
                }
            }

            return reverse;
        }

        private static BcnTextureResidencySummary CalculateBcnTextureResidency(
            BatchState state,
            IPackFileContainer container,
            IReadOnlyCollection<string> reachablePaths,
            CancellationToken cancellationToken)
        {
            var reachable = new HashSet<string>(
                reachablePaths.Select(Normalize),
                StringComparer.OrdinalIgnoreCase);
            var textureWsModels = BuildTextureWsModelMap(
                container,
                reachable,
                cancellationToken);
            var rootsByWsModel = BuildArmyRootsByWsModel(
                state,
                cancellationToken);

            var formatCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var formatBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var reachableDdsCount = 0;
            var bcnTextureCount = 0;
            var unsupportedDdsCount = 0;
            var armyUnmappedBcnTextureCount = 0;
            long bcnBytes = 0;
            double estimatedScenarioResidentBcnBytes = 0;
            long generatedAtlasBcnBytes = 0;
            double estimatedScenarioResidentGeneratedAtlasBcnBytes = 0;

            foreach (var texturePath in reachable
                         .Where(path => Path.GetExtension(path).Equals(
                             ".dds",
                             StringComparison.OrdinalIgnoreCase))
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                reachableDdsCount++;

                var file = container.FindFile(texturePath);
                if (file == null)
                    continue;

                DdsBcnResidencyEstimate estimate;
                try
                {
                    if (!DdsBcnResidencyEstimator.TryEstimate(
                            file.DataSource.PeekData(148),
                            out estimate))
                    {
                        unsupportedDdsCount++;
                        continue;
                    }
                }
                catch
                {
                    unsupportedDdsCount++;
                    continue;
                }

                bcnTextureCount++;
                bcnBytes = checked(bcnBytes + estimate.Bytes);
                formatCounts[estimate.Format] = formatCounts.GetValueOrDefault(estimate.Format) + 1;
                formatBytes[estimate.Format] = checked(
                    formatBytes.GetValueOrDefault(estimate.Format) + estimate.Bytes);

                var targetWsModels = textureWsModels.TryGetValue(texturePath, out var wsModels)
                    ? wsModels
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var fallbackRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var wsModel in targetWsModels)
                {
                    if (rootsByWsModel.TryGetValue(wsModel, out var roots))
                        fallbackRoots.UnionWith(roots);
                }

                if (targetWsModels.Count == 0 && fallbackRoots.Count == 0)
                    armyUnmappedBcnTextureCount++;

                var residentProbability = GetExpectedArmyResidentProbability(
                    state.ArmyResidencyModel,
                    targetWsModels,
                    fallbackRoots);
                var expectedBytes = estimate.Bytes * residentProbability;
                estimatedScenarioResidentBcnBytes += expectedBytes;

                if (state.GeneratedTexturePaths.Contains(texturePath))
                {
                    generatedAtlasBcnBytes = checked(
                        generatedAtlasBcnBytes + estimate.Bytes);
                    estimatedScenarioResidentGeneratedAtlasBcnBytes += expectedBytes;
                }
            }

            return new BcnTextureResidencySummary(
                reachableDdsCount,
                bcnTextureCount,
                unsupportedDdsCount,
                armyUnmappedBcnTextureCount,
                bcnBytes,
                estimatedScenarioResidentBcnBytes,
                generatedAtlasBcnBytes,
                estimatedScenarioResidentGeneratedAtlasBcnBytes,
                formatCounts,
                formatBytes);
        }

        private static Dictionary<string, HashSet<string>> BuildTextureWsModelMap(
            IPackFileContainer container,
            IReadOnlyCollection<string> reachablePaths,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, HashSet<string>>(
                StringComparer.OrdinalIgnoreCase);
            var materialTextures = new Dictionary<string, string[]>(
                StringComparer.OrdinalIgnoreCase);

            void AddTextureReference(string texturePathValue, string assetPathValue)
            {
                var texturePath = Normalize(texturePathValue);
                var assetPath = Normalize(assetPathValue);
                if (texturePath.Length == 0 ||
                    assetPath.Length == 0 ||
                    !Path.GetExtension(texturePath).Equals(
                        ".dds",
                        StringComparison.OrdinalIgnoreCase) ||
                    !reachablePaths.Contains(texturePath, StringComparer.OrdinalIgnoreCase))
                {
                    return;
                }

                if (!result.TryGetValue(texturePath, out var assets))
                {
                    assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    result[texturePath] = assets;
                }

                assets.Add(assetPath);
            }

            foreach (var wsModelPath in reachablePaths
                         .Where(path => Path.GetExtension(path).Equals(
                             ".wsmodel",
                             StringComparison.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wsFile = container.FindFile(wsModelPath);
                if (wsFile == null)
                    continue;

                XmlDocument wsDocument;
                try
                {
                    wsDocument = LoadXml(wsFile);
                }
                catch
                {
                    continue;
                }

                var materialNodes = wsDocument.SelectNodes("/model/materials/material");
                if (materialNodes == null)
                    continue;

                foreach (XmlNode materialNode in materialNodes)
                {
                    var materialPath = Normalize(materialNode.InnerText);
                    if (materialPath.Length == 0)
                        continue;

                    if (!materialTextures.TryGetValue(materialPath, out var texturePaths))
                    {
                        var materialFile = container.FindFile(materialPath);
                        if (materialFile == null)
                        {
                            materialTextures[materialPath] = [];
                            continue;
                        }

                        try
                        {
                            var materialDocument = LoadXml(materialFile);
                            texturePaths = materialDocument
                                .SelectNodes("/material/textures/texture")?
                                .Cast<XmlNode>()
                                .Select(node => Normalize(
                                    node.SelectSingleNode("source")?.InnerText ?? node.InnerText))
                                .Where(path =>
                                    path.Length != 0 &&
                                    Path.GetExtension(path).Equals(
                                        ".dds",
                                        StringComparison.OrdinalIgnoreCase))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToArray()
                                ?? [];
                        }
                        catch
                        {
                            texturePaths = [];
                        }

                        materialTextures[materialPath] = texturePaths;
                    }

                    foreach (var texturePath in texturePaths)
                        AddTextureReference(texturePath, wsModelPath);
                }
            }

            foreach (var rigidPath in reachablePaths
                         .Where(path => Path.GetExtension(path).Equals(
                             ".rigid_model_v2",
                             StringComparison.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rigidFile = container.FindFile(rigidPath);
                if (rigidFile == null)
                    continue;

                try
                {
                    var rigid = ModelFactory.Create().Load(rigidFile.DataSource.ReadData());
                    foreach (var lod in rigid.ModelList)
                    {
                        foreach (var model in lod)
                        {
                            foreach (var texture in model.Material.GetAllTextures())
                                AddTextureReference(texture.Path, rigidPath);
                        }
                    }
                }
                catch
                {
                    // Keep residency estimation conservative for malformed unrelated rigids.
                }
            }

            return result;
        }

        private static Dictionary<string, HashSet<string>> BuildArmyRootsByWsModel(
            BatchState state,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, HashSet<string>>(
                StringComparer.OrdinalIgnoreCase);
            if (state.ArmyResidencyModel == null)
                return result;

            foreach (var root in state.ArmyResidencyModel.UnitsByVmd.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var rootExtension = Path.GetExtension(root);
                if (!rootExtension.Equals(
                        ".variantmeshdefinition",
                        StringComparison.OrdinalIgnoreCase))
                {
                    // Engine assets with no VMD variant (notably battlefield engines whose
                    // variant column is empty) are themselves the residency key. Treating them
                    // as VMDs would yield no reachable WSModel and lose their army probability.
                    if (rootExtension.Equals(".wsmodel", StringComparison.OrdinalIgnoreCase) ||
                        rootExtension.Equals(".rigid_model_v2", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!result.TryGetValue(root, out var directRoots))
                        {
                            directRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            result[root] = directRoots;
                        }

                        directRoots.Add(Normalize(root));
                    }

                    continue;
                }

                foreach (var wsModel in GetReachableWsModels(
                             state,
                             root,
                             cancellationToken))
                {
                    if (!result.TryGetValue(wsModel, out var roots))
                    {
                        roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        result[wsModel] = roots;
                    }

                    roots.Add(Normalize(root));
                }
            }

            return result;
        }

        private static string FormatMiB(double bytes, int decimals = 1)
            => $"{(bytes / (1024.0 * 1024.0)).ToString($"N{decimals}")} MiB";

        private static double GetAtlasValueGateMiBPerDraw(
            double netBytes,
            double drawsEliminated)
        {
            if (drawsEliminated <= 0.000001)
                return 0;

            return Math.Max(0.0, netBytes) /
                   (1024.0 * 1024.0) /
                   drawsEliminated;
        }

        private static double GetAtlasValueGateWorstMiBPerDraw(
            AtlasValueGateBatchEconomics entry)
        {
            var global = GetAtlasValueGateMiBPerDraw(
                entry.NetBcnBytes,
                entry.RawDrawsEliminated);
            var expected = entry.ExpectedArmyDrawsEliminated > 0.000001
                ? GetAtlasValueGateMiBPerDraw(
                    entry.ExpectedArmyNetBcnBytes,
                    entry.ExpectedArmyDrawsEliminated)
                : 0;
            return Math.Max(global, expected);
        }

        private static string FormatAtlasValueGateMiBPerDraw(
            double netBytes,
            double drawsEliminated)
            => drawsEliminated <= 0.000001
                ? "N/A"
                : $"{GetAtlasValueGateMiBPerDraw(netBytes, drawsEliminated):N3} MiB/draw";

        private static string FormatResidencyDelta(double before, double after)
        {
            var deltaBytes = after - before;
            if (before <= 0)
                return $"{(deltaBytes >= 0 ? "+" : string.Empty)}{FormatMiB(deltaBytes)}";

            var deltaPercent = deltaBytes / before * 100.0;
            return $"{(deltaBytes >= 0 ? "+" : string.Empty)}{FormatMiB(deltaBytes)} " +
                   $"({(deltaPercent >= 0 ? "+" : string.Empty)}{deltaPercent:N1}%)";
        }

        private static string FormatBcnBreakdown(BcnTextureResidencySummary summary)
        {
            if (summary.BytesByFormat.Count == 0)
                return "<none>";

            return string.Join(
                ", ",
                summary.BytesByFormat
                    .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(entry =>
                        $"{entry.Key}={FormatMiB(entry.Value)} " +
                        $"({summary.TextureCountByFormat.GetValueOrDefault(entry.Key):N0})"));
        }

        private HashSet<string> CollectReachableAssetFiles(
            BatchState state,
            IPackFileContainer container,
            IReadOnlyList<string> rootAssetPaths,
            CancellationToken cancellationToken = default,
            IProgress<TextureAtlasPackProgress>? progress = null,
            string phase = "Scanning dependencies")
        {
            var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var assetQueue = new Queue<string>(rootAssetPaths.Select(Normalize));
            var visitedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var processedAssetCount = 0;
            void EnqueueExistingAsset(string pathValue)
            {
                var path = Normalize(pathValue);
                if (path.Length == 0 || container.FindFile(path) == null)
                    return;

                reachable.Add(path);
                assetQueue.Enqueue(path);
            }

            void AddMaterialTextures(
                string materialPath,
                PackFile materialFile)
            {
                if (!TryGetMaterialDocumentForTraversal(
                        state,
                        container,
                        materialPath,
                        materialFile,
                        out var materialDocument))
                {
                    return;
                }

                var textureNodes = materialDocument.SelectNodes("/material/textures/texture");
                if (textureNodes == null)
                    return;

                foreach (XmlNode textureNode in textureNodes)
                {
                    EnqueueExistingAsset(
                        textureNode.SelectSingleNode("source")?.InnerText ??
                        textureNode.InnerText);
                }
            }

            void AddRigidTextures(PackFile rigidFile)
            {
                try
                {
                    var rigid = ModelFactory.Create().Load(rigidFile.DataSource.ReadData());
                    foreach (var lod in rigid.ModelList)
                    {
                        foreach (var model in lod)
                        {
                            foreach (var texture in model.Material.GetAllTextures())
                                EnqueueExistingAsset(texture.Path);
                        }
                    }
                }
                catch
                {
                    // A malformed unrelated rigid should not prevent dependency scanning for
                    // the rest of the pack. Rewritten rigids are validated separately.
                }
            }

            while (assetQueue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var assetPath = assetQueue.Dequeue();
                ReportProgress(progress, phase, ++processedAssetCount, 0, assetPath);
                if (!visitedAssets.Add(assetPath))
                    continue;

                var assetFile = container.FindFile(assetPath);
                if (assetFile == null)
                    continue;

                var extension = Path.GetExtension(assetPath);
                if (extension.Equals(".variantmeshdefinition", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryGetVmdForTraversal(
                            state,
                            container,
                            assetPath,
                            assetFile,
                            out var vmd))
                    {
                        continue;
                    }

                    var modelRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var childVmdRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var directTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    CollectVmdReferences(vmd, modelRefs, childVmdRefs, directTextures);

                    foreach (var texture in directTextures)
                        EnqueueExistingAsset(texture);
                    foreach (var child in childVmdRefs)
                        EnqueueExistingAsset(child);
                    foreach (var model in modelRefs)
                        EnqueueExistingAsset(model);

                    continue;
                }

                if (extension.Equals(".rigid_model_v2", StringComparison.OrdinalIgnoreCase))
                {
                    AddRigidTextures(assetFile);
                    continue;
                }

                if (extension.Equals(".xml.material", StringComparison.OrdinalIgnoreCase))
                {
                    AddMaterialTextures(assetPath, assetFile);
                    continue;
                }

                if (!extension.Equals(".wsmodel", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!TryGetWsDocumentForTraversal(
                        state,
                        container,
                        assetPath,
                        assetFile,
                        out var wsDocument))
                {
                    continue;
                }

                EnqueueExistingAsset(wsDocument.SelectSingleNode("/model/geometry")?.InnerText ?? string.Empty);

                var materialNodes = wsDocument.SelectNodes("/model/materials/material");
                if (materialNodes == null)
                    continue;

                foreach (XmlNode materialNode in materialNodes)
                {
                    var materialPath = Normalize(materialNode.InnerText);
                    var materialFile = container.FindFile(materialPath);
                    if (materialFile == null)
                        continue;

                    reachable.Add(materialPath);
                    AddMaterialTextures(materialPath, materialFile);
                }
            }

            return reachable;
        }

        private static IReadOnlyDictionary<string, IReadOnlyCollection<string>> BuildChildVmdDependencyMap(
            BatchState state,
            IPackFileContainer container,
            IReadOnlyCollection<string> vmdPaths,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, IReadOnlyCollection<string>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var pathValue in vmdPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Normalize(pathValue);
                var file = container.FindFile(path);
                if (file == null)
                    continue;

                var vmd = GetVmd(state, container, path, file);
                var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var children = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectVmdReferences(vmd, models, children, textures);

                result[path] = children
                    .Where(container.ContainsFile)
                    .Select(Normalize)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(child => child, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            return result;
        }

        private static bool TryGetVmdForTraversal(
            BatchState state,
            IPackFileContainer container,
            string vmdPathValue,
            PackFile file,
            out VariantMesh vmd)
        {
            var vmdPath = Normalize(vmdPathValue);
            try
            {
                vmd = GetVmd(state, container, vmdPath, file);
                return true;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                XmlException or
                FormatException or
                ArgumentException)
            {
                RecordMalformedReferencedVmd(state, vmdPath, ex);
                vmd = null!;
                return false;
            }
        }

        private static void RecordMalformedReferencedVmd(
            BatchState state,
            string vmdPath,
            Exception exception)
        {
            vmdPath = Normalize(vmdPath);
            if (state.MalformedReferencedVmds.Any(
                    entry => entry.Path.Equals(vmdPath, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var messages = new List<string>();
            for (Exception? current = exception; current != null; current = current.InnerException)
            {
                var message = current.Message.Replace("\r", " ").Replace("\n", " ").Trim();
                if (message.Length != 0 &&
                    !messages.Contains(message, StringComparer.Ordinal))
                {
                    messages.Add(message);
                }
            }

            state.MalformedReferencedVmds.Add(new MalformedVmdEntry(
                vmdPath,
                messages.Count == 0
                    ? exception.GetType().Name
                    : string.Join(" -> ", messages)));
        }

        private static VariantMesh GetVmd(
            BatchState state,
            IPackFileContainer container,
            string vmdPath,
            PackFile? file = null)
        {
            vmdPath = Normalize(vmdPath);
            if (state.VmdDocuments.TryGetValue(vmdPath, out var cached))
                return cached;

            file ??= container.FindFile(vmdPath)
                ?? throw new FileNotFoundException($"VMD file could not be resolved: {vmdPath}");

            var vmd = VariantMeshDefinitionLoader.Load(
                file,
                out var compatibilityRepairs);
            RecordXmlCompatibilityRepairs(
                state,
                vmdPath,
                compatibilityRepairs);
            state.VmdDocuments[vmdPath] = vmd;
            return vmd;
        }

        private static HashSet<string> GetReachableWsModels(
            BatchState state,
            string rootVmdPath,
            CancellationToken cancellationToken = default)
        {
            rootVmdPath = Normalize(rootVmdPath);
            if (state.ReachableWsModelsByRoot.TryGetValue(rootVmdPath, out var cached))
                return cached;

            var reachable = CollectReachableWsModels(
                state,
                state.Source,
                rootVmdPath,
                cancellationToken);
            state.ReachableWsModelsByRoot[rootVmdPath] = reachable;
            return reachable;
        }

        private static HashSet<string> CollectReachableWsModels(
            BatchState state,
            IPackFileContainer container,
            string rootVmdPath,
            CancellationToken cancellationToken = default)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            queue.Enqueue(Normalize(rootVmdPath));

            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var vmdPath = queue.Dequeue();
                if (!visited.Add(vmdPath))
                    continue;

                var file = container.FindFile(vmdPath);
                if (file == null)
                    continue;

                if (!TryGetVmdForTraversal(
                        state,
                        container,
                        vmdPath,
                        file,
                        out var vmd))
                {
                    continue;
                }

                var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var children = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectVmdReferences(vmd, models, children, textures);

                foreach (var model in models)
                {
                    if (Path.GetExtension(model).Equals(".wsmodel", StringComparison.OrdinalIgnoreCase) &&
                        container.FindFile(model) != null)
                    {
                        result.Add(model);
                    }
                }

                foreach (var child in children)
                {
                    if (container.FindFile(child) != null)
                        queue.Enqueue(child);
                }
            }

            return result;
        }

        private static void CollectVmdReferences(
            VariantMesh mesh,
            HashSet<string> modelRefs,
            HashSet<string> childVmdRefs,
            HashSet<string> directTextures)
        {
            if (!string.IsNullOrWhiteSpace(mesh.ModelReference))
                modelRefs.Add(Normalize(mesh.ModelReference));
            if (!string.IsNullOrWhiteSpace(mesh.ImposterModel))
                modelRefs.Add(Normalize(mesh.ImposterModel));
            if (!string.IsNullOrWhiteSpace(mesh.DecalDiffuse))
                directTextures.Add(Normalize(mesh.DecalDiffuse));
            if (!string.IsNullOrWhiteSpace(mesh.DecalNormal))
                directTextures.Add(Normalize(mesh.DecalNormal));

            foreach (var slot in mesh.ChildSlots ?? [])
            {
                foreach (var child in slot.ChildMeshes ?? [])
                    CollectVmdReferences(child, modelRefs, childVmdRefs, directTextures);
                foreach (var reference in slot.ChildReferences ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(reference.Reference))
                        childVmdRefs.Add(Normalize(reference.Reference));
                }
            }
        }

        private static bool TryGetMaterialDocumentForTraversal(
            BatchState state,
            IPackFileContainer container,
            string materialPathValue,
            PackFile file,
            out XmlDocument document)
        {
            var materialPath = Normalize(materialPathValue);
            try
            {
                if (ReferenceEquals(container, state.Source))
                {
                    document = GetMaterialDocument(state, materialPath, file);
                }
                else
                {
                    document = LoadXml(file, out var compatibilityRepairs);
                    RecordXmlCompatibilityRepairs(
                        state,
                        materialPath,
                        compatibilityRepairs);
                }

                return true;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                XmlException or
                FormatException or
                ArgumentException)
            {
                RecordMalformedMaterial(state, materialPath, ex);
                document = null!;
                return false;
            }
        }

        private static void RecordMalformedMaterial(
            BatchState state,
            string materialPath,
            Exception exception)
        {
            materialPath = Normalize(materialPath);
            if (state.MalformedMaterialsIgnored.Any(
                    entry => entry.Path.Equals(materialPath, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var messages = new List<string>();
            for (Exception? current = exception; current != null; current = current.InnerException)
            {
                var message = current.Message.Replace("\r", " ").Replace("\n", " ").Trim();
                if (message.Length != 0 &&
                    !messages.Contains(message, StringComparer.Ordinal))
                {
                    messages.Add(message);
                }
            }

            state.MalformedMaterialsIgnored.Add(new MalformedXmlAssetEntry(
                materialPath,
                messages.Count == 0
                    ? exception.GetType().Name
                    : string.Join(" -> ", messages)));
        }

        private static XmlDocument GetMaterialDocument(
            BatchState state,
            string materialPath,
            PackFile? materialFile = null)
        {
            materialPath = Normalize(materialPath);
            if (state.MaterialDocuments.TryGetValue(materialPath, out var cached))
                return cached;

            materialFile ??= FindForReadStatic(state, materialPath)
                ?? throw new FileNotFoundException($"Material file could not be resolved: {materialPath}");

            var doc = LoadXml(
                materialFile,
                out var compatibilityRepairs);
            RecordXmlCompatibilityRepairs(
                state,
                materialPath,
                compatibilityRepairs);
            state.MaterialDocuments[materialPath] = doc;
            return doc;
        }

        private static PackFile? FindForReadStatic(BatchState state, string path)
        {
            path = Normalize(path);
            return state.Output.FindFile(path)
                   ?? state.Source.FindFile(path)
                   ?? state.PackFileService.FindFile(path);
        }

        private bool TryGetWsDocumentForTraversal(
            BatchState state,
            IPackFileContainer container,
            string wsPathValue,
            PackFile file,
            out XmlDocument document)
        {
            var wsPath = Normalize(wsPathValue);
            try
            {
                if (ReferenceEquals(container, state.Source))
                {
                    document = GetWsDocument(state, wsPath) ?? LoadXml(file);
                }
                else
                {
                    document = LoadXml(file, out var compatibilityRepairs);
                    RecordXmlCompatibilityRepairs(
                        state,
                        wsPath,
                        compatibilityRepairs);
                }

                return true;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                XmlException or
                FormatException or
                ArgumentException)
            {
                RecordMalformedWsModel(state, wsPath, ex);
                document = null!;
                return false;
            }
        }

        private static void RecordMalformedWsModel(
            BatchState state,
            string wsPath,
            Exception exception)
        {
            wsPath = Normalize(wsPath);
            if (state.MalformedWsModelsIgnored.Any(
                    entry => entry.Path.Equals(wsPath, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var messages = new List<string>();
            for (Exception? current = exception; current != null; current = current.InnerException)
            {
                var message = current.Message.Replace("\r", " ").Replace("\n", " ").Trim();
                if (message.Length != 0 &&
                    !messages.Contains(message, StringComparer.Ordinal))
                {
                    messages.Add(message);
                }
            }

            state.MalformedWsModelsIgnored.Add(new MalformedXmlAssetEntry(
                wsPath,
                messages.Count == 0
                    ? exception.GetType().Name
                    : string.Join(" -> ", messages)));
        }

        private XmlDocument? GetWsDocument(BatchState state, string wsPath)
        {
            wsPath = Normalize(wsPath);
            if (state.WsDocuments.TryGetValue(wsPath, out var cached))
                return cached;

            var file = state.Source.FindFile(wsPath);
            if (file == null)
                return null;

            var doc = LoadXml(
                file,
                out var compatibilityRepairs);
            RecordXmlCompatibilityRepairs(
                state,
                wsPath,
                compatibilityRepairs);
            state.WsDocuments[wsPath] = doc;
            return doc;
        }

        private RmvFile? GetRmv(BatchState state, string geometryPath)
        {
            geometryPath = Normalize(geometryPath);
            if (state.RigidModels.TryGetValue(geometryPath, out var cached))
                return cached;

            var file = state.Source.FindFile(geometryPath);
            if (file == null)
                return null;

            var rmv = ModelFactory.Create().Load(file.DataSource.ReadData());
            state.RigidModels[geometryPath] = rmv;
            return rmv;
        }

        private PackFile? FindForRead(BatchState state, string path)
        {
            path = Normalize(path);
            return state.Output.FindFile(path)
                   ?? state.Source.FindFile(path)
                   ?? _packFileService.FindFile(path);
        }

        private void WriteFile(IPackFileContainer output, string fullPath, byte[] data)
        {
            fullPath = Normalize(fullPath);
            var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            var name = Path.GetFileName(fullPath);

            // AddFiles replaces an existing normalized path in-place. Avoid a preceding DeleteFile:
            // deletion performs a linear reverse lookup and emits an extra event/log entry.
            _packFileService.AddFilesToPack(
                output,
                [new NewPackFileEntry(directory, PackFile.CreateFromBytes(name, data))]);
        }

        private static XmlDocument LoadXml(PackFile file)
            => LoadXml(file, out _);

        private static XmlDocument LoadXml(
            PackFile file,
            out IReadOnlyList<string> compatibilityRepairs)
        {
            var content = Encoding.UTF8.GetString(file.DataSource.ReadData());
            return XmlCompatibilityParser.Parse(
                content,
                xml =>
                {
                    var document = new XmlDocument();
                    document.LoadXml(xml);
                    return document;
                },
                out compatibilityRepairs);
        }

        private static void RecordXmlCompatibilityRepairs(
            BatchState state,
            string pathValue,
            IReadOnlyList<string> repairs)
        {
            if (repairs.Count == 0)
                return;

            var path = Normalize(pathValue);
            if (!state.XmlCompatibilityRepairs.TryGetValue(path, out var recorded))
            {
                recorded = new HashSet<string>(StringComparer.Ordinal);
                state.XmlCompatibilityRepairs[path] = recorded;
            }

            foreach (var repair in repairs)
                recorded.Add(repair);
        }

        private static string GetTextureSlot(XmlNode textureNode)
            => textureNode.SelectSingleNode("slot")?.InnerText.Trim() ?? string.Empty;

        private static string GetTexturePath(XmlDocument material, string slot)
        {
            var textureNodes = material.SelectNodes("/material/textures/texture");
            if (textureNodes == null)
                return string.Empty;

            foreach (XmlNode node in textureNodes)
            {
                if (!GetTextureSlot(node).Equals(slot, StringComparison.OrdinalIgnoreCase))
                    continue;

                return Normalize(node.SelectSingleNode("source")?.InnerText ?? node.InnerText);
            }

            return string.Empty;
        }

        private static void SetTexturePath(XmlDocument material, string slot, string path)
        {
            var textureNodes = material.SelectNodes("/material/textures/texture");
            if (textureNodes == null)
                return;

            foreach (XmlNode node in textureNodes)
            {
                if (!GetTextureSlot(node).Equals(slot, StringComparison.OrdinalIgnoreCase))
                    continue;

                var source = node.SelectSingleNode("source");
                if (source != null)
                    source.InnerText = path;
                else
                    node.InnerText = path;
                return;
            }
        }

        private static void RemoveTextureSlot(XmlDocument material, string slot)
        {
            var textureNodes = material.SelectNodes("/material/textures/texture");
            if (textureNodes == null)
                return;

            foreach (var node in textureNodes
                         .Cast<XmlNode>()
                         .Where(node => GetTextureSlot(node).Equals(
                             slot,
                             StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                node.ParentNode?.RemoveChild(node);
            }
        }

        private void ValidateOutput(
            BatchState state,
            IReadOnlyList<string> vmdRoots,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, "Validating output");
            var errors = new List<string>();

            ValidateUnchangedModelPathSet(state, ".variantmeshdefinition", errors);
            ValidateUnchangedModelPathSet(state, ".wsmodel", errors);
            ValidateUnchangedModelPathSet(state, ".rigid_model_v2", errors);

            var validationRigids = state.ModifiedRigids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            for (var rigidIndex = 0; rigidIndex < validationRigids.Count; rigidIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rigidPath = validationRigids[rigidIndex];
                ReportProgress(
                    progress,
                    "Validating modified rigids",
                    rigidIndex + 1,
                    validationRigids.Count,
                    rigidPath);
                var file = state.Output.FindFile(rigidPath);
                if (file == null)
                {
                    errors.Add($"Modified rigid is missing from output: {rigidPath}");
                    continue;
                }

                try
                {
                    _ = ModelFactory.Create().Load(file.DataSource.ReadData());
                }
                catch (Exception ex)
                {
                    errors.Add($"Modified rigid failed to reload: {rigidPath} ({ex.Message})");
                }
            }

            var validationWsModels = state.ModifiedWsModels
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            for (var wsIndex = 0; wsIndex < validationWsModels.Count; wsIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wsPath = validationWsModels[wsIndex];
                ReportProgress(
                    progress,
                    "Validating WSModels",
                    wsIndex + 1,
                    validationWsModels.Count,
                    wsPath);
                var file = state.Output.FindFile(wsPath);
                if (file == null)
                {
                    errors.Add($"Modified WSModel is missing from output: {wsPath}");
                    continue;
                }

                XmlDocument wsDoc;
                try
                {
                    wsDoc = LoadXml(file);
                }
                catch (Exception ex)
                {
                    errors.Add($"Modified WSModel XML is invalid: {wsPath} ({ex.Message})");
                    continue;
                }

                var geometryPath = Normalize(wsDoc.SelectSingleNode("/model/geometry")?.InnerText);
                if (!string.IsNullOrWhiteSpace(geometryPath) &&
                    !CanResolveAfterRewrite(state, geometryPath))
                {
                    if (IsPreservedSourceUnresolvedWsReference(
                            state,
                            wsPath,
                            geometryPath,
                            "/model/geometry"))
                    {
                        AddValidationWarning(
                            state,
                            $"Preserved pre-existing unresolved WSModel geometry: " +
                            $"{wsPath} -> {geometryPath}");
                    }
                    else
                    {
                        errors.Add($"WSModel geometry no longer resolves: {wsPath} -> {geometryPath}");
                    }
                }

                var materialNodes = wsDoc.SelectNodes("/model/materials/material");
                if (materialNodes == null)
                    continue;

                foreach (XmlNode materialNode in materialNodes)
                {
                    var materialPath = Normalize(materialNode.InnerText);
                    if (!string.IsNullOrWhiteSpace(materialPath) &&
                        !CanResolveAfterRewrite(state, materialPath))
                    {
                        if (IsPreservedSourceUnresolvedWsReference(
                                state,
                                wsPath,
                                materialPath,
                                "/model/materials/material"))
                        {
                            AddValidationWarning(
                                state,
                                $"Preserved pre-existing unresolved WSModel material: " +
                                $"{wsPath} -> {materialPath}");
                        }
                        else
                        {
                            errors.Add($"WSModel material no longer resolves: {wsPath} -> {materialPath}");
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(geometryPath))
                {
                    var geometryFile = state.Output.FindFile(geometryPath);
                    if (geometryFile != null)
                    {
                        try
                        {
                            var outputRmv = ModelFactory.Create().Load(geometryFile.DataSource.ReadData());
                            if (!TryReadWsMaterialAssignments(
                                    wsDoc,
                                    outputRmv,
                                    out _,
                                    out var assignmentReason))
                            {
                                if (HasSameSourceWsMaterialMismatch(
                                        state,
                                        wsPath,
                                        geometryPath,
                                        assignmentReason))
                                {
                                    state.ValidationMessages.Add(
                                        $"WARNING: Preserved pre-existing WSModel material-table mismatch: " +
                                        $"{wsPath} -> {geometryPath} ({assignmentReason})");
                                }
                                else
                                {
                                    errors.Add(
                                        $"WSModel material table does not match rewritten rigid: " +
                                        $"{wsPath} -> {geometryPath} ({assignmentReason})");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add(
                                $"Could not validate WSModel material table against rigid: " +
                                $"{wsPath} -> {geometryPath} ({ex.Message})");
                        }
                    }
                }
            }

            var validationMaterials = state.GeneratedMaterialPaths
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            for (var materialIndex = 0; materialIndex < validationMaterials.Count; materialIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var materialPath = validationMaterials[materialIndex];
                ReportProgress(
                    progress,
                    "Validating atlas materials",
                    materialIndex + 1,
                    validationMaterials.Count,
                    materialPath);
                var file = state.Output.FindFile(materialPath);
                if (file == null)
                {
                    errors.Add($"Generated atlas material is missing: {materialPath}");
                    continue;
                }

                XmlDocument materialDoc;
                try
                {
                    materialDoc = LoadXml(file);
                }
                catch (Exception ex)
                {
                    errors.Add($"Generated atlas material XML is invalid: {materialPath} ({ex.Message})");
                    continue;
                }

                var textureNodes = materialDoc.SelectNodes("/material/textures/texture");
                if (textureNodes == null)
                    continue;

                foreach (XmlNode textureNode in textureNodes)
                {
                    var texturePath = Normalize(
                        textureNode.SelectSingleNode("source")?.InnerText ?? textureNode.InnerText);
                    if (string.IsNullOrWhiteSpace(texturePath) || IsTexturePlaceholder(texturePath))
                        continue;

                    if (!CanResolveAfterRewrite(state, texturePath))
                    {
                        errors.Add(
                            $"Generated material texture no longer resolves: {materialPath} -> {texturePath}");
                    }
                }
            }

            foreach (var atlasPath in state.GeneratedTexturePaths)
            {
                if (state.Output.FindFile(atlasPath) == null)
                    errors.Add($"Generated atlas texture is missing from output: {atlasPath}");
            }

            foreach (var vmdPath in vmdRoots)
            {
                if (state.Output.FindFile(vmdPath) == null)
                    errors.Add($"VMD root is missing from output: {vmdPath}");
            }

            if (errors.Count != 0)
            {
                foreach (var error in errors)
                    state.ValidationMessages.Add("ERROR: " + error);

                throw new InvalidOperationException(
                    $"Post-atlas dependency validation failed with {errors.Count} error(s). " +
                    $"See {state.ReportPath} for the full report.\n" +
                    string.Join("\n", errors.Take(10)));
            }

            state.ValidationMessages.Add(
                $"PASS: {state.ModifiedRigids.Count} modified rigid(s), " +
                $"{state.ModifiedWsModels.Count} modified WSModel(s), " +
                $"{state.GeneratedMaterialPaths.Count} generated material(s), and " +
                $"{state.GeneratedTexturePaths.Count} atlas texture(s) validated.");
            state.ValidationMessages.Add(
                "PASS: VMD, WSModel, and rigid_model_v2 path sets are unchanged.");
            if (state.MergeCompatibleMeshesEnabled && state.MeshMergeInvariantGroupCount != 0)
            {
                state.ValidationMessages.Add(
                    $"PASS: merge geometry invariants validated for " +
                    $"{state.MeshMergeInvariantGroupCount} merged group(s) across " +
                    $"{state.MeshMergeInvariantLodCount} LOD(s); " +
                    $"{state.MeshMergeInvariantWsModelCount} WSModel non-material structure check(s) passed.");
            }
        }

        private bool IsPreservedSourceUnresolvedWsReference(
            BatchState state,
            string wsPathValue,
            string referencePathValue,
            string xpath)
        {
            var wsPath = Normalize(wsPathValue);
            var referencePath = Normalize(referencePathValue);
            if (wsPath.Length == 0 || referencePath.Length == 0)
                return false;

            try
            {
                var sourceWsFile = state.Source.FindFile(wsPath);
                if (sourceWsFile == null)
                    return false;

                var sourceWsDocument = LoadXml(sourceWsFile);
                var sourceNodes = sourceWsDocument.SelectNodes(xpath);
                if (sourceNodes == null ||
                    !sourceNodes
                        .Cast<XmlNode>()
                        .Any(node => Normalize(node.InnerText)
                            .Equals(referencePath, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                // The exact same reference was already present in the source WSModel.
                // Only downgrade it to a warning when it was unresolved before rewriting.
                if (state.Source.FindFile(referencePath) != null)
                    return false;

                return _packFileService.FindFile(referencePath) == null;
            }
            catch
            {
                // If the source baseline cannot be established reliably, retain strict
                // validation and keep the rewritten-output failure fatal.
                return false;
            }
        }

        private static void AddValidationWarning(BatchState state, string message)
        {
            var fullMessage = "WARNING: " + message;
            if (!state.ValidationMessages.Contains(fullMessage, StringComparer.Ordinal))
                state.ValidationMessages.Add(fullMessage);
        }

        private static bool HasSameSourceWsMaterialMismatch(
            BatchState state,
            string wsPath,
            string geometryPath,
            string outputReason)
        {
            try
            {
                var sourceWsFile = state.Source.FindFile(wsPath);
                var sourceGeometryFile = state.Source.FindFile(geometryPath);
                if (sourceWsFile == null || sourceGeometryFile == null)
                    return false;

                var sourceWsDocument = LoadXml(sourceWsFile);
                var sourceRmv = ModelFactory.Create().Load(sourceGeometryFile.DataSource.ReadData());
                if (TryReadWsMaterialAssignments(
                        sourceWsDocument,
                        sourceRmv,
                        out _,
                        out var sourceReason))
                {
                    return false;
                }

                return string.Equals(
                    sourceReason,
                    outputReason,
                    StringComparison.Ordinal);
            }
            catch
            {
                // If the source baseline cannot be established reliably, retain strict
                // validation and let the rewritten-output mismatch remain fatal.
                return false;
            }
        }

        private static void ValidateUnchangedModelPathSet(
            BatchState state,
            string extension,
            List<string> errors)
        {
            var sourcePaths = state.Source.GetAllFiles().Keys
                .Where(x => Path.GetExtension(x).Equals(extension, StringComparison.OrdinalIgnoreCase))
                .Select(Normalize)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var outputPaths = state.Output.GetAllFiles().Keys
                .Where(x => Path.GetExtension(x).Equals(extension, StringComparison.OrdinalIgnoreCase))
                .Select(Normalize)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var missing in sourcePaths.Except(outputPaths, StringComparer.OrdinalIgnoreCase))
                errors.Add($"{extension} path was removed instead of rewritten in place: {missing}");
            foreach (var added in outputPaths.Except(sourcePaths, StringComparer.OrdinalIgnoreCase))
                errors.Add($"Unexpected new {extension} path was created: {added}");
        }

        private bool CanResolveAfterRewrite(BatchState state, string path)
        {
            path = Normalize(path);
            if (string.IsNullOrWhiteSpace(path))
                return true;

            if (state.Output.FindFile(path) != null)
                return true;

            // If the selected source pack owned the path, it must still exist in the output.
            // Do not let another loaded pack with the same path mask an accidental deletion.
            if (state.Source.FindFile(path) != null)
                return false;

            return _packFileService.FindFile(path) != null;
        }

        private static void RecordSkip(
            BatchState state,
            string rootVmdPath,
            MeshKey key,
            string wsModelPath,
            string reason)
        {
            if (!state.SkipDetails.TryGetValue(key, out var details))
            {
                details = [];
                state.SkipDetails[key] = details;
            }

            if (!details.Any(x =>
                    x.RootVmdPath.Equals(rootVmdPath, StringComparison.OrdinalIgnoreCase) &&
                    x.WsModelPath.Equals(wsModelPath, StringComparison.OrdinalIgnoreCase) &&
                    x.Reason.Equals(reason, StringComparison.Ordinal)))
            {
                details.Add(new SkipDetail(rootVmdPath, wsModelPath, reason));
            }
        }

        private static int GetEffectiveSkippedMeshCount(BatchState state)
            => state.SkipDetails.Keys.Count(x => !state.ProcessedMeshes.Contains(x));

        private static void AppendDiagnosticCounts(
            StringBuilder sb,
            string title,
            IReadOnlyDictionary<string, int> counts,
            int maxEntries)
        {
            sb.AppendLine($"  {title}:");
            if (counts.Count == 0)
            {
                sb.AppendLine("    (none)");
                return;
            }

            foreach (var entry in counts
                         .OrderByDescending(x => x.Value)
                         .ThenBy(x => x.Key, StringComparer.Ordinal)
                         .Take(maxEntries))
            {
                sb.AppendLine($"    {entry.Key}: {entry.Value}");
            }

            if (counts.Count > maxEntries)
                sb.AppendLine($"    ... {counts.Count - maxEntries} more");
        }

        private static AtlasComponentReuseAnalysis BuildAtlasComponentReuseAnalysis(
            BatchState state)
        {
            var batchesByWsModel = new Dictionary<string, HashSet<int>>(
                StringComparer.OrdinalIgnoreCase);
            var geometryByWsModel = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var (batchId, batch) in state.AtlasBatchDiagnostics)
            {
                foreach (var candidate in batch.Candidates)
                {
                    foreach (var usage in candidate.Usages)
                    {
                        var wsModelPath = Normalize(usage.AssetPath);
                        if (string.IsNullOrWhiteSpace(wsModelPath))
                            continue;

                        if (!batchesByWsModel.TryGetValue(wsModelPath, out var batchIds))
                        {
                            batchIds = [];
                            batchesByWsModel[wsModelPath] = batchIds;
                        }

                        batchIds.Add(batchId);
                        geometryByWsModel.TryAdd(wsModelPath, candidate.Key.GeometryPath);
                    }
                }
            }

            if (batchesByWsModel.Count == 0)
            {
                return new AtlasComponentReuseAnalysis(
                    [],
                    [],
                    0,
                    0);
            }

            var rootsByWsModel = new Dictionary<string, HashSet<string>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var (rootVmdPath, reachableWsModels) in state.ReachableWsModelsByRoot)
            {
                var root = Normalize(rootVmdPath);
                foreach (var reachableWsModel in reachableWsModels)
                {
                    var wsModelPath = Normalize(reachableWsModel);
                    if (!batchesByWsModel.ContainsKey(wsModelPath))
                        continue;

                    if (!rootsByWsModel.TryGetValue(wsModelPath, out var roots))
                    {
                        roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        rootsByWsModel[wsModelPath] = roots;
                    }

                    roots.Add(root);
                }
            }

            var rootsByGeometry = new Dictionary<string, HashSet<string>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var (wsModelPath, roots) in rootsByWsModel)
            {
                if (!geometryByWsModel.TryGetValue(wsModelPath, out var geometryPath))
                    continue;

                if (!rootsByGeometry.TryGetValue(geometryPath, out var geometryRoots))
                {
                    geometryRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    rootsByGeometry[geometryPath] = geometryRoots;
                }

                geometryRoots.UnionWith(roots);
            }

            var components = batchesByWsModel.Keys
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(wsModelPath =>
                {
                    rootsByWsModel.TryGetValue(wsModelPath, out var roots);
                    geometryByWsModel.TryGetValue(wsModelPath, out var geometryPath);
                    return new AtlasComponentReuseEntry(
                        wsModelPath,
                        geometryPath ?? string.Empty,
                        roots?.Count ?? 0,
                        batchesByWsModel[wsModelPath]
                            .OrderBy(x => x)
                            .ToArray());
                })
                .ToList();

            // Count root overlap only for atlased components on different geometry assets and
            // with different atlas-batch membership. This focuses the report on relationships
            // relevant to a future cross-component/pair-specific rebake instead of same-rigid
            // parts that inherently travel together.
            var sharedRootCounts = new Dictionary<(string Left, string Right), int>();
            foreach (var (_, reachableWsModels) in state.ReachableWsModelsByRoot)
            {
                var models = reachableWsModels
                    .Select(Normalize)
                    .Where(batchesByWsModel.ContainsKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var reusedModels = models
                    .Where(model =>
                        rootsByWsModel.TryGetValue(model, out var roots) &&
                        roots.Count > 1)
                    .ToList();
                var seenPairsInRoot = new HashSet<(string Left, string Right)>();

                // One-off <> one-off combinations cannot demonstrate lost reuse, so do not
                // spend quadratic work on them. Pair every reusable component with the other
                // atlased components reachable from the same root.
                foreach (var reusable in reusedModels)
                {
                    if (!geometryByWsModel.TryGetValue(reusable, out var reusableGeometry))
                        continue;

                    foreach (var other in models)
                    {
                        if (reusable.Equals(other, StringComparison.OrdinalIgnoreCase) ||
                            !geometryByWsModel.TryGetValue(other, out var otherGeometry) ||
                            reusableGeometry.Equals(otherGeometry, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (batchesByWsModel[reusable].SetEquals(batchesByWsModel[other]))
                            continue;

                        var key = StringComparer.OrdinalIgnoreCase.Compare(reusable, other) <= 0
                            ? (reusable, other)
                            : (other, reusable);
                        if (!seenPairsInRoot.Add(key))
                            continue;

                        sharedRootCounts[key] = sharedRootCounts.GetValueOrDefault(key) + 1;
                    }
                }
            }

            var pairs = new List<AtlasComponentReusePairEntry>();
            foreach (var (key, sharedRootCount) in sharedRootCounts)
            {
                if (!rootsByWsModel.TryGetValue(key.Left, out var leftRoots) ||
                    !rootsByWsModel.TryGetValue(key.Right, out var rightRoots) ||
                    leftRoots.Count == 0 ||
                    rightRoots.Count == 0)
                {
                    continue;
                }

                var leftCoverage = sharedRootCount / (double)leftRoots.Count;
                var rightCoverage = sharedRootCount / (double)rightRoots.Count;
                var stronglyAsymmetric =
                    (leftCoverage >= 0.8 && rightCoverage <= 0.5) ||
                    (rightCoverage >= 0.8 && leftCoverage <= 0.5);
                var highBidirectionalOverlap =
                    leftCoverage >= 0.8 &&
                    rightCoverage >= 0.8;

                pairs.Add(new AtlasComponentReusePairEntry(
                    key.Left,
                    key.Right,
                    geometryByWsModel[key.Left],
                    geometryByWsModel[key.Right],
                    leftRoots.Count,
                    rightRoots.Count,
                    sharedRootCount,
                    leftCoverage,
                    rightCoverage,
                    batchesByWsModel[key.Left].OrderBy(x => x).ToArray(),
                    batchesByWsModel[key.Right].OrderBy(x => x).ToArray(),
                    stronglyAsymmetric,
                    highBidirectionalOverlap));
            }

            return new AtlasComponentReuseAnalysis(
                components,
                pairs,
                rootsByGeometry.Count,
                rootsByGeometry.Values.Count(roots => roots.Count > 1));
        }

        private static AtlasResidencySummary BuildAtlasResidencySummary(
            BatchState state)
        {
            var allCandidates = state.AtlasBatchDiagnostics.Values
                .SelectMany(batch => batch.Candidates)
                .GroupBy(candidate => candidate.Key)
                .Select(group => group.First())
                .ToList();
            if (allCandidates.Count == 0)
            {
                return new AtlasResidencySummary(
                    0,
                    0,
                    0,
                    0,
                    string.Empty,
                    0);
            }

            var rootsByMesh = BuildCandidateRootVmdPaths(state, allCandidates);
            var pixelsByRoot = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            long globalPixels = 0;
            long aggregateResidentPixels = 0;
            var multiRootBatches = 0;
            var maxRootsPerBatch = 0;

            foreach (var batch in state.AtlasBatchDiagnostics.Values)
            {
                globalPixels = checked(globalPixels + batch.PixelCost);

                var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var candidate in batch.Candidates)
                {
                    if (rootsByMesh.TryGetValue(candidate.Key, out var candidateRoots))
                        roots.UnionWith(candidateRoots);
                    else
                        roots.Add(Normalize(candidate.RootVmdPath));
                }

                var rootCount = Math.Max(1, roots.Count);
                maxRootsPerBatch = Math.Max(maxRootsPerBatch, rootCount);
                if (rootCount > 1)
                    multiRootBatches++;

                aggregateResidentPixels = checked(
                    aggregateResidentPixels +
                    GetAtlasResidencyProxy(batch.PixelCost, rootCount));

                foreach (var root in roots)
                {
                    pixelsByRoot[root] = checked(
                        pixelsByRoot.GetValueOrDefault(root) +
                        batch.PixelCost);
                }
            }

            var worstRoot = pixelsByRoot
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            return new AtlasResidencySummary(
                globalPixels,
                aggregateResidentPixels,
                multiRootBatches,
                maxRootsPerBatch,
                worstRoot.Key ?? string.Empty,
                worstRoot.Value);
        }

        private static void WriteReport(
            BatchState state,
            IReadOnlyList<string> vmdRoots,
            bool succeeded,
            Exception? failure)
        {
            var componentReuse = BuildAtlasComponentReuseAnalysis(state);
            var residency = BuildAtlasResidencySummary(state);
            var sb = new StringBuilder();
            sb.AppendLine("Texture Atlas Pack Report");
            sb.AppendLine("=========================");
            sb.AppendLine($"Status: {(succeeded ? "SUCCESS" : "FAILED")}");
            sb.AppendLine($"Source: {state.SourcePath}");
            sb.AppendLine($"Output: {state.OutputPath}");
            sb.AppendLine($"Report: {state.ReportPath}");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            sb.AppendLine("Summary");
            sb.AppendLine("-------");
            sb.AppendLine($"Validated source VMD roots: {vmdRoots.Count}");
            sb.AppendLine(
                $"Atlas VMD population: {(state.AtlasAllVmdsEnabled ? "PACK-WIDE" : "GAMEPLAY-USED")}");
            sb.AppendLine($"Atlas VMD roots: {state.AtlasVmdRootCount:N0} / {state.SourceVmdRootCount:N0}");
            if (state.UnitCategoryResolution != null)
            {
                sb.AppendLine(
                    $"Gameplay-used VMD roots: " +
                    $"{state.UnitCategoryResolution.UsagesByVmd.Count:N0} / {vmdRoots.Count:N0}");
                sb.AppendLine(
                    $"VMD roots excluded from gameplay-used atlasing: " +
                    $"{Math.Max(0, vmdRoots.Count - state.UnitCategoryResolution.UsagesByVmd.Count):N0}");
                sb.AppendLine(
                    $"Unit-category DB table files read: " +
                    $"{state.UnitCategoryResolution.TableFilesRead}");
                sb.AppendLine(
                    $"Gameplay resolver health: " +
                    $"{(state.UnitCategoryResolution.IsGameplayResolutionHealthy ? "HEALTHY" : "UNHEALTHY")}");
                if (!string.IsNullOrWhiteSpace(
                        state.UnitCategoryResolution.GameplayResolutionHealthMessage))
                {
                    sb.AppendLine(
                        $"Gameplay resolver health detail: " +
                        state.UnitCategoryResolution.GameplayResolutionHealthMessage);
                }

                var directConsumerCounts = state.UnitCategoryResolution.DirectUsagesByVmd.Values
                    .SelectMany(usages => usages)
                    .SelectMany(usage => usage.Provenance)
                    .GroupBy(provenance => provenance.ConsumerType)
                    .OrderBy(group => group.Key)
                    .Select(group => $"{group.Key}={group.Count():N0}")
                    .ToArray();
                sb.AppendLine(
                    $"Direct gameplay VMD consumer links: " +
                    $"{(directConsumerCounts.Length == 0 ? "(none)" : string.Join(", ", directConsumerCounts))}");
                sb.AppendLine(
                    $"Gameplay VMD roots added through child propagation: " +
                    $"{state.UnitCategoryResolution.PropagatedVmdCount:N0}");
            }
            sb.AppendLine($"XML files repaired for compatibility: {state.XmlCompatibilityRepairs.Count}");
            sb.AppendLine($"Malformed VMD root files ignored: {state.MalformedVmdRoots.Count}");
            sb.AppendLine($"Malformed referenced VMD files ignored: {state.MalformedReferencedVmds.Count}");
            sb.AppendLine($"Malformed WSModels ignored: {state.MalformedWsModelsIgnored.Count}");
            sb.AppendLine($"Malformed material XML files ignored: {state.MalformedMaterialsIgnored.Count}");
            sb.AppendLine($"Mesh parts atlased: {state.ProcessedMeshes.Count}");
            sb.AppendLine($"Mesh parts skipped: {GetEffectiveSkippedMeshCount(state)}");
            sb.AppendLine($"Atlas textures generated: {state.GeneratedTexturePaths.Count}");
            sb.AppendLine($"Constant-only atlas channels skipped: {state.ConstantOnlyAtlasChannelsSkipped}");
            sb.AppendLine($"Uniform constant source textures detected: {state.UniformConstantTexturePaths.Count}");
            sb.AppendLine($"Large BC split-compression channels: {state.LargeBcSplitCompressionChannels}");
            sb.AppendLine($"Atlas materials generated: {state.GeneratedMaterialPaths.Count}");
            sb.AppendLine($"Atlas material assignments reused: {state.GeneratedMaterialReuses}");
            sb.AppendLine($"Atlas placements generated: {state.AtlasPlacementsGenerated}");
            sb.AppendLine($"Atlas placements reused: {state.AtlasPlacementsReused}");
            sb.AppendLine($"Wrapped-UV placements canonicalized: {state.WrappedUvPlacementsCanonicalized}");
            sb.AppendLine($"UV-island normalized meshes: {state.UvIslandNormalizedMeshes}");
            sb.AppendLine($"UV islands shifted by integer tiles: {state.UvIslandsShifted}");
            sb.AppendLine($"UV-island source crop pixels saved: {state.UvIslandCropPixelsSaved:N0}");
            sb.AppendLine($"Shared UV-island seam groups aligned: {state.SharedUvIslandCutGroups}");
            sb.AppendLine($"Content-deduplicated atlas placements: {state.ContentDeduplicatedAtlasPlacements}");
            sb.AppendLine($"Mesh references remapped by content dedupe: {state.ContentCanonicalizedMeshReferences}");
            sb.AppendLine($"Cropped-content hashes computed: {state.AtlasRegionContentHashes.Count}");
            sb.AppendLine($"Pack-wide atlas/material sharing: {(state.ShareAtlasesAcrossVmdsEnabled ? "YES" : "NO")}");
            sb.AppendLine($"Atlas batches generated: {state.AtlasBatchCount}");
            sb.AppendLine($"Texel-density downscale candidates: {state.TexelDensityScaledMeshes}");
            if (state.TexelDensityScaledMeshes != 0)
            {
                sb.AppendLine(
                    $"Texel-density scaled source pixel area: " +
                    $"{state.TexelDensityOriginalPixelArea:N0} -> {state.TexelDensityScaledPixelArea:N0}");
                sb.AppendLine(
                    $"Texel-density scale buckets: " +
                    $"{string.Join(", ", state.TexelDensityScaleCounts
                        .OrderBy(entry => entry.Key)
                        .Select(entry => $"{entry.Key:0.##}x={entry.Value:N0}"))}");
            }
            if (state.MergeCompatibleMeshesEnabled)
            {
                sb.AppendLine($"Atlas value-gate batches accepted: {state.AtlasValueGateBatchesAccepted}");
                sb.AppendLine($"Atlas value-gate batches rejected: {state.AtlasValueGateBatchesRejected}");
                sb.AppendLine($"Atlas value-gate broad batches split: {state.AtlasValueGateBroadBatchesSplit}");
                sb.AppendLine(
                    $"Atlas value-gate net BCn budgets: " +
                    $"{FormatMiB(MaxNetBcnBytesPerExpectedArmyDraw, 2)} per scenario-estimated draw; " +
                    $"{FormatMiB(MaxNetBcnBytesPerFallbackDraw, 2)} per raw draw only when scenario relevance is unresolved");
                sb.AppendLine(
                    $"Atlas value-gate cumulative reachable BCn growth cap: " +
                    $"{MaxReachableBcnGrowthRatio:P0} of source reachable BCn payload");
                sb.AppendLine($"Atlas value-gate candidates accepted: {state.AtlasValueGateCandidatesAccepted}");
                sb.AppendLine($"Atlas value-gate candidates rejected: {state.AtlasValueGateCandidatesRejected}");
                sb.AppendLine(
                    $"Atlas value-gate generated BCn accepted: " +
                    $"{FormatMiB(state.AtlasValueGateGeneratedBcnBytesAccepted)}");
                sb.AppendLine(
                    $"Atlas value-gate source BCn retired by accepted batches: " +
                    $"{FormatMiB(state.AtlasValueGateRetiredBcnBytesAccepted)}");
                sb.AppendLine(
                    $"Atlas value-gate net BCn accepted: " +
                    $"{FormatMiB(state.AtlasValueGateNetBcnBytesAccepted)}");
                sb.AppendLine(
                    $"Atlas value-gate scenario-estimated generated BCn accepted: " +
                    $"{FormatMiB(state.AtlasValueGateExpectedArmyGeneratedBcnBytesAccepted)}");
                sb.AppendLine(
                    $"Atlas value-gate scenario-estimated source BCn retired by accepted batches: " +
                    $"{FormatMiB(state.AtlasValueGateExpectedArmyRetiredBcnBytesAccepted)}");
                sb.AppendLine(
                    $"Atlas value-gate scenario-estimated net BCn accepted: " +
                    $"{FormatMiB(state.AtlasValueGateExpectedArmyNetBcnBytesAccepted)}");
                sb.AppendLine(
                    $"Atlas value-gate generated BCn rejected: " +
                    $"{FormatMiB(state.AtlasValueGateGeneratedBcnBytesRejected)}");
                sb.AppendLine(
                    $"Atlas value-gate source BCn retirement rejected: " +
                    $"{FormatMiB(state.AtlasValueGateRetiredBcnBytesRejected)}");
                sb.AppendLine(
                    $"Atlas value-gate net BCn rejected: " +
                    $"{FormatMiB(state.AtlasValueGateNetBcnBytesRejected)}");
                sb.AppendLine(
                    $"Atlas value-gate scenario-estimated net BCn rejected: " +
                    $"{FormatMiB(state.AtlasValueGateExpectedArmyNetBcnBytesRejected)}");
                sb.AppendLine(
                    $"Atlas value-gate scenario-estimated draw eliminations accepted: " +
                    $"{state.AtlasValueGateExpectedDrawsAccepted:N3}");
                sb.AppendLine();
                sb.AppendLine("Accepted atlas value-gate batch economics (worst-to-best)");
                sb.AppendLine("---------------------------------------------------------");
                sb.AppendLine(
                    "Sorted descending by the worse of global net MiB/raw draw and " +
                    "scenario-estimated net MiB/scenario-estimated draw.");
                var valueGateRank = 0;
                foreach (var entry in state.AtlasValueGateAcceptedBatchEconomics
                             .OrderByDescending(GetAtlasValueGateWorstMiBPerDraw)
                             .ThenByDescending(entry => GetAtlasValueGateMiBPerDraw(
                                 entry.NetBcnBytes,
                                 entry.RawDrawsEliminated))
                             .ThenBy(entry => entry.AcceptanceSequence))
                {
                    valueGateRank++;
                    sb.AppendLine(
                        $"  #{valueGateRank}: accepted-seq={entry.AcceptanceSequence}, " +
                        $"candidates={entry.CandidateCount}, roots={entry.RootVmdPaths.Length}");
                    sb.AppendLine(
                        $"    draws: raw={entry.RawDrawsEliminated:N0}, " +
                        $"scenario-estimated={entry.ExpectedArmyDrawsEliminated:N3}");
                    sb.AppendLine(
                        $"    global BCn: generated={FormatMiB(entry.GeneratedBcnBytes)}, " +
                        $"retired={FormatMiB(entry.RetiredSourceBcnBytes)}, " +
                        $"net={FormatMiB(entry.NetBcnBytes)}, " +
                        $"net/draw={FormatAtlasValueGateMiBPerDraw(entry.NetBcnBytes, entry.RawDrawsEliminated)}");
                    sb.AppendLine(
                        $"    scenario-estimated BCn: generated={FormatMiB(entry.ExpectedArmyGeneratedBcnBytes)}, " +
                        $"retired={FormatMiB(entry.ExpectedArmyRetiredSourceBcnBytes)}, " +
                        $"net={FormatMiB(entry.ExpectedArmyNetBcnBytes)}, " +
                        $"net/draw={FormatAtlasValueGateMiBPerDraw(entry.ExpectedArmyNetBcnBytes, entry.ExpectedArmyDrawsEliminated)}");
                    sb.AppendLine(
                        $"    roots: {(entry.RootVmdPaths.Length == 0 ? "<none>" : string.Join(", ", entry.RootVmdPaths))}");
                }
                if (state.AtlasValueGateAcceptedBatchEconomics.Count == 0)
                    sb.AppendLine("  (none)");
            }
            sb.AppendLine($"Atlas pixel-area optimized splits: {state.AtlasPixelAreaOptimizedSplits}");
            sb.AppendLine($"Atlas pixel-area split evaluations: {state.AtlasPixelAreaSplitEvaluations}");
            sb.AppendLine($"Atlas non-contiguous optimized splits: {state.AtlasNonContiguousOptimizedSplits}");
            sb.AppendLine($"Atlas non-contiguous split evaluations: {state.AtlasNonContiguousSplitEvaluations}");
            sb.AppendLine($"Pack atlas maximum dimension: {PackAtlasMaxSize}");
            sb.AppendLine($"Atlas pixels saved by split optimization: {state.AtlasPixelAreaSavedByOptimizedSplits:N0}");
            sb.AppendLine($"Atlas pixels saved by non-contiguous splits: {state.AtlasPixelAreaSavedByNonContiguousSplits:N0}");
            sb.AppendLine($"Army-aware locality split evaluations: {state.VmdLocalitySplitEvaluations}");
            sb.AppendLine($"Army-aware locality splits accepted: {state.VmdLocalitySplitsAccepted}");
            sb.AppendLine($"Estimated VMD-resident atlas pixels saved by locality splits: {state.VmdLocalityResidentPixelsSaved:N0}");
            if (state.ArmyResidencyModel != null)
            {
                sb.AppendLine(
                    $"Scenario army slot model: lord=1, heroes=2, infantry/missile=9, " +
                    $"cavalry/chariots=4, monsters/beasts=3, artillery/war machines=2");
                sb.AppendLine(
                    $"Scenario-estimated resident atlas pixels before locality optimization: " +
                    $"{state.ExpectedArmyResidentPixelsBeforeLocality:N0}");
                sb.AppendLine(
                    $"Scenario-estimated resident atlas pixels after locality optimization: " +
                    $"{state.ExpectedArmyResidentPixelsAfterLocality:N0}");
                sb.AppendLine(
                    $"Scenario-estimated resident atlas pixels saved by locality optimization: " +
                    $"{Math.Max(0, state.ExpectedArmyResidentPixelsBeforeLocality - state.ExpectedArmyResidentPixelsAfterLocality):N0}");
                sb.AppendLine(
                    $"Scenario-estimated resident atlas pixels after merge-aware optimization: " +
                    $"{state.ExpectedArmyResidentPixelsAfterMergeAware:N0}");
            }
            sb.AppendLine($"Global atlas pixels added by locality splits: {state.VmdLocalityGlobalPixelsAdded:N0}");
            sb.AppendLine($"Merge-aware locality regressions rejected: {state.MergeAwareLocalityRegressionsRejected}");
            sb.AppendLine($"Final global atlas pixel cost: {residency.GlobalPixels:N0}");
            sb.AppendLine($"Estimated aggregate VMD-resident atlas pixel cost: {residency.AggregateResidentPixels:N0}");
            sb.AppendLine($"Atlas batches spanning multiple actual VMD roots: {residency.MultiRootBatchCount}");
            sb.AppendLine($"Maximum VMD roots sharing one atlas batch: {residency.MaxRootsPerBatch}");
            if (!string.IsNullOrWhiteSpace(residency.WorstRootVmdPath))
            {
                sb.AppendLine(
                    $"Highest estimated single-VMD atlas pixel residency: " +
                    $"{residency.WorstRootPixels:N0} ({residency.WorstRootVmdPath})");
            }
            sb.AppendLine($"Merge-aware batch pairs considered: {state.MergeAwareBatchPairsConsidered}");
            sb.AppendLine($"Merge-aware repartition evaluations: {state.MergeAwareRepartitionEvaluations}");
            sb.AppendLine($"Merge-aware repartitions accepted: {state.MergeAwareRepartitionsAccepted}");
            sb.AppendLine($"Merge-aware repartition pixels saved: {state.MergeAwareRepartitionPixelsSaved:N0}");
            sb.AppendLine($"Merge-aware no-extra-pixel coalesce evaluations: {state.MergeAwareBatchCoalesceEvaluations}");
            sb.AppendLine($"Merge-aware no-extra-pixel coalesces accepted: {state.MergeAwareBatchCoalescesAccepted}");
            sb.AppendLine($"Merge-aware coalesce pixels saved: {state.MergeAwareBatchCoalescePixelsSaved:N0}");
            sb.AppendLine($"Merge-affinity eliminations before repartition: {state.MergeAwareAffinityPotentialBefore}");
            sb.AppendLine($"Merge-affinity eliminations after repartition: {state.MergeAwareAffinityPotentialAfter}");
            sb.AppendLine($"Merge-affinity eliminations gained: {state.MergeAwareAffinityEliminationsGained}");
            if (state.ArmyResidencyModel != null)
            {
                sb.AppendLine(
                    $"Scenario-estimated draw calls eliminated before merge-aware optimization: " +
                    $"{state.ExpectedArmyDrawCallsEliminatedBeforeMergeAware:N3}");
                sb.AppendLine(
                    $"Scenario-estimated draw calls eliminated after merge-aware optimization: " +
                    $"{state.ExpectedArmyDrawCallsEliminatedAfterMergeAware:N3}");
                sb.AppendLine(
                    $"Scenario-estimated draw-call eliminations gained: " +
                    $"{state.ExpectedArmyDrawCallsEliminatedAfterMergeAware - state.ExpectedArmyDrawCallsEliminatedBeforeMergeAware:+0.000;-0.000;0.000}");
            }
            if (state.ShareAtlasesAcrossVmdsEnabled)
            {
                sb.AppendLine($"Pack-wide atlas candidates: {state.PackWideCandidateCount}");
                sb.AppendLine($"Cross-VMD shared atlas batches: {state.CrossVmdSharedAtlasBatches}");
                sb.AppendLine($"Cross-VMD shared placements: {state.CrossVmdSharedAtlasPlacements}");
                sb.AppendLine($"Cross-VMD material reuses: {state.CrossVmdMaterialReuses}");
            }
            sb.AppendLine(
                $"Atlased WSModels reachable from multiple VMD roots (reachability, not DB asset reuse): " +
                $"{componentReuse.Components.Count(x => x.VmdRootCount > 1)} / {componentReuse.Components.Count}");
            sb.AppendLine(
                $"Atlased geometry paths reused by multiple VMD roots: " +
                $"{componentReuse.ReusedGeometryCount} / {componentReuse.GeometryCount}");
            sb.AppendLine(
                $"Cross-geometry/cross-batch component pairs with asymmetric VMD-root reuse: " +
                $"{componentReuse.Pairs.Count(x => x.StronglyAsymmetric)}");
            sb.AppendLine(
                $"Cross-geometry/cross-batch component pairs with high bidirectional VMD-root overlap: " +
                $"{componentReuse.Pairs.Count(x => x.HighBidirectionalOverlap)}");
            sb.AppendLine($"Superseded asset files removed: {state.RemovedFiles.Count}");
            sb.AppendLine("Meshes with genuine unresolved secondary textures are skipped.");
            sb.AppendLine($"Merge compatible mesh parts: {(state.MergeCompatibleMeshesEnabled ? "YES" : "NO")}");
            if (state.MergeCompatibleMeshesEnabled)
            {
                sb.AppendLine($"Mesh parts before merging: {state.MeshPartsBeforeMerging}");
                sb.AppendLine($"Mesh parts after merging: {state.MeshPartsAfterMerging}");
                sb.AppendLine($"Mesh parts eliminated: {state.MeshPartsEliminated}");
                sb.AppendLine($"Mesh parts merged across semantically identical material paths: {state.SemanticMaterialPathMergeParts}");
                sb.AppendLine($"Mesh merge near-miss blocker occurrences: {state.MeshMergeBlockerCounts.Values.Sum()}");
                var mergeOpportunities = state.TextureMergeOpportunities.Values.ToList();
                sb.AppendLine($"Texture-blocked merge opportunities: {mergeOpportunities.Count}");
                sb.AppendLine($"Texture-blocked merge opportunities whose atlas combination fits: {mergeOpportunities.Count(x => x.Combination.Fits)}");
                sb.AppendLine($"Zero-cost atlas batch combinations: {mergeOpportunities.Count(x => x.Combination.Fits && x.Combination.AdditionalPixels <= 0)}");
                sb.AppendLine($"Atlas combinations <=10% extra pixels: {mergeOpportunities.Count(x => x.Combination.Fits && x.Combination.AdditionalPixels > 0 && x.Combination.AdditionalPercent <= 10)}");
                sb.AppendLine($"Atlas combinations 10-25% extra pixels: {mergeOpportunities.Count(x => x.Combination.Fits && x.Combination.AdditionalPercent > 10 && x.Combination.AdditionalPercent <= 25)}");
                sb.AppendLine($"Atlas combinations >25% extra pixels: {mergeOpportunities.Count(x => x.Combination.Fits && x.Combination.AdditionalPercent > 25)}");
                sb.AppendLine($"Atlas combinations that do not fit: {mergeOpportunities.Count(x => !x.Combination.Fits)}");
            }
            if (state.SourceBcnResidency != null && state.OutputBcnResidency != null)
            {
                var sourceBcn = state.SourceBcnResidency;
                var outputBcn = state.OutputBcnResidency;
                sb.AppendLine();
                sb.AppendLine("BCn texture resource payload and scenario residency estimate");
                sb.AppendLine("-----------------------------------------------------------");
                sb.AppendLine(
                    $"Source reachable DDS textures: {sourceBcn.ReachableDdsCount:N0}; " +
                    $"BCn={sourceBcn.BcnTextureCount:N0}; unsupported/non-BCn={sourceBcn.UnsupportedDdsCount:N0}");
                sb.AppendLine(
                    $"Output reachable DDS textures: {outputBcn.ReachableDdsCount:N0}; " +
                    $"BCn={outputBcn.BcnTextureCount:N0}; unsupported/non-BCn={outputBcn.UnsupportedDdsCount:N0}");
                sb.AppendLine($"Source reachable BCn resource payload: {FormatMiB(sourceBcn.BcnBytes)}");
                sb.AppendLine($"Output reachable BCn resource payload: {FormatMiB(outputBcn.BcnBytes)}");
                sb.AppendLine(
                    $"Reachable BCn resource-payload delta: " +
                    $"{FormatResidencyDelta(sourceBcn.BcnBytes, outputBcn.BcnBytes)}");
                sb.AppendLine(
                    $"Scenario-estimated resident BCn payload: " +
                    $"{FormatMiB(sourceBcn.EstimatedScenarioResidentBcnBytes)} -> " +
                    $"{FormatMiB(outputBcn.EstimatedScenarioResidentBcnBytes)} " +
                    $"({FormatResidencyDelta(sourceBcn.EstimatedScenarioResidentBcnBytes, outputBcn.EstimatedScenarioResidentBcnBytes)})");
                sb.AppendLine(
                    $"Generated atlas BCn resource payload: {FormatMiB(outputBcn.GeneratedAtlasBcnBytes)}");
                sb.AppendLine(
                    $"Scenario-estimated resident generated-atlas BCn payload: " +
                    $"{FormatMiB(outputBcn.EstimatedScenarioResidentGeneratedAtlasBcnBytes)}");
                sb.AppendLine($"Source BCn formats: {FormatBcnBreakdown(sourceBcn)}");
                sb.AppendLine($"Output BCn formats: {FormatBcnBreakdown(outputBcn)}");
                sb.AppendLine(
                    $"BCn textures without an army WSModel mapping: " +
                    $"source={sourceBcn.ArmyUnmappedBcnTextureCount:N0}, " +
                    $"output={outputBcn.ArmyUnmappedBcnTextureCount:N0}");
                sb.AppendLine(
                    "Reachable resource payload is the compressed 4x4 BCn block payload for every declared mip " +
                    "of every reachable BCn DDS; it is not scenario weighted.");
                sb.AppendLine(
                    "Scenario-estimated resident payload weights each texture by the modeled probability that " +
                    "at least one resolved visual in the configured army scenario requires it. This is a " +
                    "model-derived residency proxy, not measured GPU residency and not a visible-texel byte count.");
                sb.AppendLine(
                    "Both figures exclude driver allocation/alignment overhead, non-BCn DDS formats, " +
                    "and textures resolved outside the processed pack.");
                sb.AppendLine();
            }

            sb.AppendLine($"Optimize geometry: {(state.OptimizeGeometryEnabled ? "YES" : "NO")}");
            if (state.OptimizeGeometryEnabled)
            {
                sb.AppendLine($"Geometry meshes optimized: {state.GeometryMeshesOptimized}");
                sb.AppendLine($"Geometry vertices before: {state.GeometryVerticesBefore:N0}");
                sb.AppendLine($"Geometry vertices after: {state.GeometryVerticesAfter:N0}");
                sb.AppendLine($"Geometry vertices removed: {state.GeometryVerticesRemoved:N0}");
                sb.AppendLine($"Unreferenced vertices removed: {state.GeometryUnreferencedVerticesRemoved:N0}");
                sb.AppendLine($"Duplicate vertices removed: {state.GeometryDuplicateVerticesRemoved:N0}");
            }
            sb.AppendLine();

            if (state.UnitCategoryResolution != null)
            {
                var unitResolution = state.UnitCategoryResolution;
                var unitUsages = unitResolution.UsagesByVmd.Values
                    .SelectMany(usages => usages)
                    .ToList();
                var directAssetUsages = unitResolution.DirectAssetUsagesByPath.Values
                    .SelectMany(usages => usages)
                    .ToList();

                sb.AppendLine("Unit category DB resolution");
                sb.AppendLine("---------------------------");
                foreach (var table in unitResolution.ParsedRowsByTable
                             .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"Parsed {table.Key}: {table.Value:N0} row(s)");
                }

                sb.AppendLine($"Relevant DB table files read: {unitResolution.TableFilesRead:N0}");
                sb.AppendLine(
                    $"Gameplay resolver health: " +
                    $"{(unitResolution.IsGameplayResolutionHealthy ? "HEALTHY" : "UNHEALTHY")}");
                if (!string.IsNullOrWhiteSpace(unitResolution.GameplayResolutionHealthMessage))
                {
                    sb.AppendLine(
                        $"Gameplay resolver health detail: " +
                        unitResolution.GameplayResolutionHealthMessage);
                }
                sb.AppendLine($"Directly DB-resolved VMD roots: {unitResolution.DirectlyResolvedVmdCount:N0}");
                sb.AppendLine($"VMD roots resolved through child propagation: {unitResolution.PropagatedVmdCount:N0}");
                sb.AppendLine($"Resolved VMD roots: {unitResolution.UsagesByVmd.Count:N0}");
                sb.AppendLine($"Unresolved VMD roots: {unitResolution.UnresolvedVmdRoots.Count:N0}");
                sb.AppendLine($"Resolved VMD-to-unit links: {unitUsages.Count:N0}");
                sb.AppendLine(
                    $"Resolved direct engine assets: {unitResolution.DirectAssetUsagesByPath.Count:N0} " +
                    $"asset(s), {directAssetUsages.Count:N0} usage(s)");
                sb.AppendLine(
                    $"Complete DB-derived visual roster: {unitResolution.RosterUnits.Count:N0} unit(s); " +
                    $"scope={unitResolution.Scenario.RosterScope}" +
                    (string.IsNullOrWhiteSpace(unitResolution.Scenario.RosterScopeKey)
                        ? string.Empty
                        : $" ({unitResolution.Scenario.RosterScopeKey})"));
                sb.AppendLine(
                    $"Scenario entity scaling: unit={unitResolution.Scenario.UnitSizeScale:0.###}, " +
                    $"crew={unitResolution.Scenario.CrewScale:0.###}, " +
                    $"rounding={unitResolution.Scenario.EngineRoundingPolicy}");
                sb.AppendLine(
                    $"Scenario lifecycle: destroyed={unitResolution.Scenario.DestructionProbability:0.###}, " +
                    $"destruct-transition={unitResolution.Scenario.DestructTransitionProbability:0.###}; " +
                    $"LOD distribution={string.Join(", ", unitResolution.Scenario.LodDistribution.OrderBy(x => x.Key).Select(x => $"lod{x.Key}={x.Value:0.###}"))}");

                var rosterComponents = unitResolution.RosterUnits
                    .SelectMany(unit => unit.Components.Select(component => (Unit: unit, Component: component)))
                    .ToList();
                var trueAssetReuse = rosterComponents
                    .GroupBy(entry => entry.Component.AssetPath, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new
                    {
                        AssetPath = group.Key,
                        UnitCount = group.Select(entry => entry.Unit.Identity)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count(),
                        Roles = group.Select(entry => entry.Component.Role).Distinct().OrderBy(role => role).ToArray(),
                        States = group.Select(entry => entry.Component.State).Distinct().OrderBy(stateValue => stateValue).ToArray(),
                        Lods = group.Select(entry => entry.Component.Lod).Distinct().OrderBy(lod => lod).ToArray(),
                        ExpectedEntityReferences = group.Sum(entry =>
                            entry.Unit.VisualCounts.ForRole(entry.Component.Role) *
                            entry.Component.ScenarioPresenceProbability),
                        FactionCount = group.SelectMany(entry => entry.Unit.FactionKeys)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count(),
                        CultureCount = group.SelectMany(entry => entry.Unit.CultureKeys)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count(),
                        ExampleUnits = group.Select(entry => entry.Unit.MainUnitKey)
                            .Where(key => !string.IsNullOrWhiteSpace(key))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                            .Take(4)
                            .ToArray(),
                    })
                    .Where(entry => entry.UnitCount > 1)
                    .OrderByDescending(entry => entry.UnitCount)
                    .ThenBy(entry => entry.AssetPath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                sb.AppendLine(
                    $"True visual-asset reuse: {trueAssetReuse.Count:N0} asset path(s) referenced by multiple resolved units.");
                sb.AppendLine(
                    "True reuse above is DB unit-to-asset identity reuse; atlas/mesh compatibility is reported separately and does not imply shared source assets.");
                foreach (var reuse in trueAssetReuse.Take(20))
                {
                    sb.AppendLine(
                        $"  reuse: {reuse.AssetPath} | units={reuse.UnitCount:N0} | " +
                        $"roles={string.Join(",", reuse.Roles)} | states={string.Join(",", reuse.States)} | " +
                        $"lods={string.Join(",", reuse.Lods)} | entity-refs={reuse.ExpectedEntityReferences:0.###} | " +
                        $"factions={reuse.FactionCount:N0} | cultures={reuse.CultureCount:N0} | " +
                        $"example-units={string.Join(",", reuse.ExampleUnits)}");
                }
                if (trueAssetReuse.Count > 20)
                    sb.AppendLine($"  ... {trueAssetReuse.Count - 20:N0} more reused asset path(s)");

                foreach (var category in Enum.GetValues<Wh3ArmyUnitCategory>())
                {
                    var categoryUsages = unitUsages
                        .Where(usage => usage.Category == category)
                        .ToList();
                    if (categoryUsages.Count == 0)
                        continue;

                    sb.AppendLine(
                        $"{category}: roots=" +
                        $"{categoryUsages.Select(usage => usage.VmdPath).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0}, " +
                        $"unit-links={categoryUsages.Count:N0}, " +
                        $"median-entities=" +
                        $"{categoryUsages.Select(usage => usage.EntityCount).OrderBy(value => value).ElementAt(categoryUsages.Count / 2):N0}");
                }

                if (unitResolution.UsagesByVmd.Count != 0)
                {
                    sb.AppendLine("Resolved VMD category mappings:");
                    foreach (var (vmdPath, usages) in unitResolution.UsagesByVmd
                                 .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        var categories = usages
                            .Select(usage => usage.Category)
                            .Distinct()
                            .OrderBy(category => category)
                            .ToArray();
                        sb.AppendLine(
                            $"  {vmdPath} | categories={string.Join(",", categories)} | " +
                            $"unit-links={usages.Count}");

                        foreach (var usage in usages)
                        {
                            sb.AppendLine(
                                $"    main={usage.MainUnitKey} | land={usage.LandUnitKey} | " +
                                $"caste={usage.Caste} | land-category={usage.LandCategory} | " +
                                $"ui-group={usage.UiGroupKey} | category={usage.Category} | " +
                                $"role={usage.VisualRole} | provenance-paths={usage.Provenance.Count:N0} | " +
                                $"entities={usage.EntityCount} | num-men={usage.NumMen} | " +
                                $"components=riders:{usage.VisualCounts.Riders}," +
                                $"mounts:{usage.VisualCounts.Mounts}," +
                                $"engines:{usage.VisualCounts.Engines}," +
                                $"crew:{usage.VisualCounts.Crew}");
                            foreach (var provenance in usage.Provenance)
                            {
                                sb.AppendLine(
                                    $"      via={provenance.ConsumerType} | " +
                                    $"kind={(provenance.IsTransitiveChild ? "child" : "direct")} | " +
                                    $"root-vmd={provenance.RootVmdPath} | " +
                                    $"parent-vmd={provenance.ParentVmdPath} | " +
                                    $"faction={provenance.FactionKey} | " +
                                    $"subculture={provenance.SubcultureKey} | " +
                                    $"culture={provenance.CultureKey} | " +
                                    $"uniform={provenance.UniformName} | " +
                                    $"art-set={provenance.ArtSetId}");
                            }
                        }
                    }
                }

                if (unitResolution.UnresolvedVmdRoots.Count != 0)
                {
                    sb.AppendLine("Unresolved VMD roots:");
                    foreach (var root in unitResolution.UnresolvedVmdRoots.Take(50))
                        sb.AppendLine($"  {root}");
                    if (unitResolution.UnresolvedVmdRoots.Count > 50)
                    {
                        sb.AppendLine(
                            $"  ... {unitResolution.UnresolvedVmdRoots.Count - 50:N0} more");
                    }
                }

                if (unitResolution.Diagnostics.Count != 0)
                {
                    sb.AppendLine("Resolver diagnostics:");
                    foreach (var diagnostic in unitResolution.Diagnostics.Take(50))
                        sb.AppendLine($"  {diagnostic}");
                    if (unitResolution.Diagnostics.Count > 50)
                    {
                        sb.AppendLine(
                            $"  ... {unitResolution.Diagnostics.Count - 50:N0} more");
                    }
                }

                sb.AppendLine();
            }

            if (state.ArmyResidencyModel != null)
            {
                sb.AppendLine("Scenario visibility and residency model");
                sb.AppendLine("---------------------------------------");
                foreach (var category in state.ArmyResidencyModel.Scenario.ArmySlotTemplate.Keys)
                {
                    sb.AppendLine(
                        $"{category}: slots={state.ArmyResidencyModel.Scenario.ArmySlotTemplate[category]}, " +
                        $"resolved-units={state.ArmyResidencyModel.UnitsByCategory[category].Count:N0}");
                }

                sb.AppendLine(
                    "Batch residency probability uses 1 - product((1 - coveredUnits/categoryUnits)^categorySlots).");
                sb.AppendLine(
                    "VMD visual model: slot probability controls activation; child meshes/references in an active slot are treated as equal alternatives; missing probability means 1.");
                sb.AppendLine(
                    "Texture residency converts each visual role's probability through its resolved component count before applying category slots.");
                sb.AppendLine(
                    "Expected draw-call eliminations enumerate concrete VMD visual configurations and only credit meshes that co-occur in the same state; oversized state spaces fall back to the conservative marginal model.");
                sb.AppendLine(
                    $"Scenario: unit-scale={state.ArmyResidencyModel.Scenario.UnitSizeScale:0.###}, " +
                    $"crew-scale={state.ArmyResidencyModel.Scenario.CrewScale:0.###}, " +
                    $"engine-rounding={state.ArmyResidencyModel.Scenario.EngineRoundingPolicy}, " +
                    $"destroyed={state.ArmyResidencyModel.Scenario.DestructionProbability:0.###}, " +
                    $"destruct-transition={state.ArmyResidencyModel.Scenario.DestructTransitionProbability:0.###}, " +
                    $"scope={state.ArmyResidencyModel.Scenario.RosterScope}" +
                    (string.IsNullOrWhiteSpace(state.ArmyResidencyModel.Scenario.RosterScopeKey)
                        ? "."
                        : $" ({state.ArmyResidencyModel.Scenario.RosterScopeKey})."));
                sb.AppendLine(
                    "The old aggregate VMD-residency and unweighted merge-affinity metrics remain non-regression guards.");
                sb.AppendLine();

                if (state.ArmyLocalitySplitEvaluations.Count != 0)
                {
                    sb.AppendLine("Army-aware locality split evaluations");
                    foreach (var (entry, index) in state.ArmyLocalitySplitEvaluations
                                 .Select((entry, index) => (entry, index + 1)))
                    {
                        sb.AppendLine(
                            $"  #{index}: {(entry.Accepted ? "ACCEPTED" : "rejected")} " +
                            $"[{entry.Decision}] | scenario-estimated " +
                            $"{entry.BaselineExpectedArmyResidentPixels:N0} -> " +
                            $"{entry.ProposedExpectedArmyResidentPixels:N0} " +
                            $"({entry.ProposedExpectedArmyResidentPixels - entry.BaselineExpectedArmyResidentPixels:+0;-0;0}); " +
                            $"VMD-resident {entry.BaselineVmdResidentPixels:N0} -> " +
                            $"{entry.ProposedVmdResidentPixels:N0} " +
                            $"({entry.ProposedVmdResidentPixels - entry.BaselineVmdResidentPixels:+0;-0;0}); " +
                            $"global {entry.BaselineGlobalPixels:N0} -> " +
                            $"{entry.ProposedGlobalPixels:N0} " +
                            $"({entry.ProposedGlobalPixels - entry.BaselineGlobalPixels:+0;-0;0})");
                    }

                    sb.AppendLine();
                }
            }

            sb.AppendLine("Phase timings");
            sb.AppendLine("-------------");
            foreach (var phase in state.PhaseDurations)
                sb.AppendLine($"{phase.Key}: {phase.Value.TotalMilliseconds:N0} ms");

            var interactiveWait = state.PhaseDurations
                .Where(x => x.Key.StartsWith("Wait for ", StringComparison.Ordinal))
                .Sum(x => x.Value.TotalMilliseconds);
            sb.AppendLine($"Interactive wait time: {interactiveWait:N0} ms");
            sb.AppendLine(
                $"Processing total excluding user waits: " +
                $"{Math.Max(0, state.TotalElapsed.TotalMilliseconds - interactiveWait):N0} ms");
            sb.AppendLine($"Total wall time: {state.TotalElapsed.TotalMilliseconds:N0} ms");
            sb.AppendLine();

            if (AtlasProfilingEnabled)
            {
                sb.AppendLine("Generated atlas texture timings");
                sb.AppendLine("-------------------------------");
                if (state.GeneratedTextureTimings.Count == 0)
                {
                    sb.AppendLine("(none)");
                }
                else
                {
                    foreach (var timing in state.GeneratedTextureTimings
                                 .OrderByDescending(x => x.TotalElapsed)
                                 .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
                    {
                    var rasterMs = (long)Math.Round(timing.RasterElapsed.TotalMilliseconds);
                    var compressionMs = (long)Math.Round(timing.CompressionElapsed.TotalMilliseconds);
                    var totalMs = (long)Math.Round(timing.TotalElapsed.TotalMilliseconds);
                    sb.AppendLine(
                        $"{timing.Path} | {timing.Width}x{timing.Height} | " +
                        $"{timing.TextureType} | {timing.Format} | " +
                        $"raster={rasterMs}ms | compress={compressionMs}ms | total={totalMs}ms | " +
                        $"split={(timing.UsedLargeBcSplitCompression ? "YES" : "NO")}");

                    var raster = timing.RasterStatistics;
                    sb.AppendLine(
                        $"  raster paths: source-ids={raster.DecodedSourceCount} | " +
                        $"unique-dds={raster.UniqueDecodedDdsCount} | " +
                        $"decode={Math.Round(raster.DdsDecodeElapsed.TotalMilliseconds)}ms | " +
                        $"compose={Math.Round(raster.ComposeElapsed.TotalMilliseconds)}ms | " +
                        $"mips={raster.MipLevelsBuilt} | " +
                        $"row-copy-attempts={raster.RowCopyAttempts} | " +
                        $"row-copy-placements={raster.RowCopyPlacements} | " +
                        $"row-copy-fallbacks={raster.RowCopyFallbacks} | " +
                        $"row-copy-pixels={raster.RowCopyPixels} | " +
                        $"mapped-core-pixels={raster.MappedCorePixels} | " +
                        $"constant-core-pixels={raster.ConstantCorePixels} | " +
                        $"padding-pixels={raster.PaddingPixels}");

                    var compression = timing.CompressionStatistics;
                    if (compression != null)
                    {
                        var stripeTimes = compression.Mip0StripeElapsed.Count == 0
                            ? "-"
                            : string.Join(
                                ",",
                                compression.Mip0StripeElapsed.Select(
                                    x => ((long)Math.Round(x.TotalMilliseconds)).ToString()));
                        sb.AppendLine(
                            $"  compression jobs: workers={compression.MaxDegreeOfParallelism} | " +
                            $"parallel-wall={Math.Round(compression.ParallelWallElapsed.TotalMilliseconds)}ms | " +
                            $"mip0-stripes=[{stripeTimes}]ms | " +
                            $"mip-tail={Math.Round(compression.MipTailElapsed.TotalMilliseconds)}ms | " +
                            $"stitch={Math.Round(compression.StitchElapsed.TotalMilliseconds)}ms");
                    }
                }
                sb.AppendLine();
            }
            }
            else
            {
                sb.AppendLine(
                    $"Detailed atlas profiling: disabled " +
                    $"(set {AtlasProfilingEnvironmentVariable}=1 before launch to enable)");
                sb.AppendLine();
            }

            sb.AppendLine("Atlas component reuse topology");
            sb.AppendLine("------------------------------");
            sb.AppendLine(
                "This is conservative VMD-root reachability, not guaranteed simultaneous rendering: " +
                "alternatives in the same VMD/slot can share a root without coexisting in one rendered variant.");
            sb.AppendLine(
                $"Atlased WSModels: {componentReuse.Components.Count}; " +
                $"reused by >1 root: {componentReuse.Components.Count(x => x.VmdRootCount > 1)}; " +
                $"reused by >=5 roots: {componentReuse.Components.Count(x => x.VmdRootCount >= 5)}.");
            sb.AppendLine(
                $"Atlased geometry paths: {componentReuse.GeometryCount}; " +
                $"reused by >1 root: {componentReuse.ReusedGeometryCount}.");
            sb.AppendLine();

            sb.AppendLine("Most reused atlased components");
            sb.AppendLine("  WSModel | VMD roots | atlas batches | geometry");
            foreach (var component in componentReuse.Components
                         .Where(x => x.VmdRootCount > 1)
                         .OrderByDescending(x => x.VmdRootCount)
                         .ThenBy(x => x.WsModelPath, StringComparer.OrdinalIgnoreCase)
                         .Take(24))
            {
                sb.AppendLine(
                    $"  {component.WsModelPath} | roots={component.VmdRootCount} | " +
                    $"batches={string.Join(",", component.AtlasBatchIds)} | " +
                    $"{component.GeometryPath}");
            }
            if (!componentReuse.Components.Any(x => x.VmdRootCount > 1))
                sb.AppendLine("  (none)");
            sb.AppendLine();

            sb.AppendLine("Strongly asymmetric cross-component root overlap");
            sb.AppendLine(
                "  These are the clearest reuse-risk cases for any future pair-specific rebake: " +
                "one component is broadly reused while the other appears in a much narrower subset.");
            var asymmetricPairs = componentReuse.Pairs
                .Where(x => x.StronglyAsymmetric)
                .OrderByDescending(x => Math.Abs(x.LeftCoverage - x.RightCoverage))
                .ThenByDescending(x => Math.Max(x.LeftRootCount, x.RightRootCount))
                .ThenByDescending(x => x.SharedRootCount)
                .Take(24)
                .ToList();
            if (asymmetricPairs.Count == 0)
            {
                sb.AppendLine("  (none)");
            }
            else
            {
                foreach (var pair in asymmetricPairs)
                {
                    sb.AppendLine(
                        $"  {pair.LeftWsModel} <> {pair.RightWsModel}");
                    sb.AppendLine(
                        $"    roots: {pair.LeftRootCount} <> {pair.RightRootCount}; " +
                        $"shared={pair.SharedRootCount}; " +
                        $"coverage={pair.LeftCoverage * 100:F1}% / {pair.RightCoverage * 100:F1}%");
                    sb.AppendLine(
                        $"    geometry: {pair.LeftGeometryPath} <> {pair.RightGeometryPath}");
                    sb.AppendLine(
                        $"    atlas batches: {string.Join(",", pair.LeftAtlasBatchIds)} <> " +
                        $"{string.Join(",", pair.RightAtlasBatchIds)}");
                }
            }
            sb.AppendLine();

            sb.AppendLine("High bidirectional cross-component root overlap");
            sb.AppendLine(
                "  High overlap can identify components that usually travel together, but this is diagnostic only; " +
                "same-root VMD alternatives are not proof that two components render simultaneously.");
            var coupledPairs = componentReuse.Pairs
                .Where(x => x.HighBidirectionalOverlap)
                .OrderByDescending(x => x.SharedRootCount)
                .ThenByDescending(x => Math.Min(x.LeftRootCount, x.RightRootCount))
                .Take(24)
                .ToList();
            if (coupledPairs.Count == 0)
            {
                sb.AppendLine("  (none)");
            }
            else
            {
                foreach (var pair in coupledPairs)
                {
                    sb.AppendLine(
                        $"  {pair.LeftWsModel} <> {pair.RightWsModel}");
                    sb.AppendLine(
                        $"    roots: {pair.LeftRootCount} <> {pair.RightRootCount}; " +
                        $"shared={pair.SharedRootCount}; " +
                        $"coverage={pair.LeftCoverage * 100:F1}% / {pair.RightCoverage * 100:F1}%");
                    sb.AppendLine(
                        $"    geometry: {pair.LeftGeometryPath} <> {pair.RightGeometryPath}");
                    sb.AppendLine(
                        $"    atlas batches: {string.Join(",", pair.LeftAtlasBatchIds)} <> " +
                        $"{string.Join(",", pair.RightAtlasBatchIds)}");
                }
            }
            sb.AppendLine();

            sb.AppendLine("VMD roots");
            sb.AppendLine("---------");
            foreach (var vmdPath in vmdRoots.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine(vmdPath);
            if (vmdRoots.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("XML compatibility repairs");
            sb.AppendLine("-------------------------");
            foreach (var entry in state.XmlCompatibilityRepairs
                         .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(entry.Key);
                foreach (var repair in entry.Value.OrderBy(value => value, StringComparer.Ordinal))
                    sb.AppendLine($"  Repaired: {repair}");
            }
            if (state.XmlCompatibilityRepairs.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("Malformed VMD root files ignored");
            sb.AppendLine("--------------------------------");
            foreach (var entry in state.MalformedVmdRoots.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(entry.Path);
                sb.AppendLine($"  Reason: {entry.Reason}");
            }
            if (state.MalformedVmdRoots.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("Malformed referenced VMD files ignored");
            sb.AppendLine("--------------------------------------");
            foreach (var entry in state.MalformedReferencedVmds
                         .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(entry.Path);
                sb.AppendLine($"  Reason: {entry.Reason}");
            }
            if (state.MalformedReferencedVmds.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("Malformed WSModels ignored");
            sb.AppendLine("--------------------------");
            foreach (var entry in state.MalformedWsModelsIgnored.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(entry.Path);
                sb.AppendLine($"  Reason: {entry.Reason}");
            }
            if (state.MalformedWsModelsIgnored.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("Malformed material XML files ignored");
            sb.AppendLine("------------------------------------");
            foreach (var entry in state.MalformedMaterialsIgnored
                         .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(entry.Path);
                sb.AppendLine($"  Reason: {entry.Reason}");
            }
            if (state.MalformedMaterialsIgnored.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("Direct engine assets skipped");
            sb.AppendLine("----------------------------");
            foreach (var message in state.DirectAssetSkipMessages
                         .OrderBy(message => message, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(message);
            }
            if (state.DirectAssetSkipMessages.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            if (failure != null)
            {
                sb.AppendLine("Failure");
                sb.AppendLine("-------");
                sb.AppendLine(failure.ToString());
                sb.AppendLine();
            }

            sb.AppendLine("Atlased mesh parts");
            sb.AppendLine("-------------------");
            if (state.AtlasedMeshes.Count == 0)
            {
                sb.AppendLine("(none)");
            }
            else
            {
                foreach (var entry in state.AtlasedMeshes
                             .OrderBy(x => x.RootVmdPath, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(x => x.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(x => x.Key.LodIndex)
                             .ThenBy(x => x.Key.PartIndex))
                {
                    sb.AppendLine($"VMD: {entry.RootVmdPath}");
                    sb.AppendLine($"  Mesh: {entry.Key}");
                    sb.AppendLine($"  WSModel(s): {string.Join(", ", entry.WsModelPaths)}");
                    sb.AppendLine($"  Material: {entry.OriginalMaterialPath} -> {entry.NewMaterialPath}");
                    sb.AppendLine($"  Atlas texture(s): {string.Join(", ", entry.AtlasPaths)}");
                }
            }
            sb.AppendLine();

            sb.AppendLine("Skipped mesh parts");
            sb.AppendLine("------------------");
            var effectiveSkips = state.SkipDetails
                .Where(x => !state.ProcessedMeshes.Contains(x.Key))
                .OrderBy(x => x.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Key.LodIndex)
                .ThenBy(x => x.Key.PartIndex)
                .ToList();

            if (effectiveSkips.Count == 0)
            {
                sb.AppendLine("(none)");
            }
            else
            {
                foreach (var (key, details) in effectiveSkips)
                {
                    sb.AppendLine($"Mesh: {key}");
                    foreach (var detail in details)
                    {
                        sb.AppendLine($"  VMD: {detail.RootVmdPath}");
                        if (!string.IsNullOrWhiteSpace(detail.WsModelPath))
                            sb.AppendLine($"  WSModel: {detail.WsModelPath}");
                        sb.AppendLine($"  Reason: {detail.Reason}");
                    }
                }
            }
            sb.AppendLine();

            if (state.MergeCompatibleMeshesEnabled)
            {
                sb.AppendLine("Merged mesh groups");
                sb.AppendLine("------------------");
                if (state.MeshMergeEntries.Count == 0)
                {
                    sb.AppendLine("(none)");
                }
                else
                {
                    foreach (var entry in state.MeshMergeEntries
                                 .OrderBy(x => x.RigidPath, StringComparer.OrdinalIgnoreCase)
                                 .ThenBy(x => x.LodIndex)
                                 .ThenBy(x => x.NewPartIndex))
                    {
                        sb.AppendLine($"Rigid: {entry.RigidPath}");
                        sb.AppendLine($"  LOD: {entry.LodIndex}");
                        sb.AppendLine($"  Old parts: {string.Join(", ", entry.OldPartIndices)}");
                        sb.AppendLine($"  New part: {entry.NewPartIndex}");
                        sb.AppendLine($"  Vertices: {entry.VertexCount}");
                        sb.AppendLine($"  Material: {entry.MaterialPath}");
                    }
                }

                sb.AppendLine();
                sb.AppendLine("Merge-aware atlas repartitions");
                sb.AppendLine("------------------------------");
                if (state.MergeAwareRepartitionEntries.Count == 0)
                {
                    sb.AppendLine("(none)");
                }
                else
                {
                    foreach (var entry in state.MergeAwareRepartitionEntries)
                    {
                        sb.AppendLine(
                            $"Batches {entry.FirstBatchIndex} + {entry.SecondBatchIndex}: " +
                            $"pixels {entry.BaselinePixels:N0} -> {entry.ResultPixels:N0}, " +
                            $"merge affinity {entry.BaselineAffinity} -> {entry.ResultAffinity}, " +
                            $"scenario-estimated draw eliminations " +
                            $"{entry.BaselineExpectedArmyDrawCallsEliminated:N3} -> " +
                            $"{entry.ResultExpectedArmyDrawCallsEliminated:N3}");
                    }
                }
                sb.AppendLine();

                sb.AppendLine("Mesh merge blocker diagnostics");
                sb.AppendLine("------------------------------");
                sb.AppendLine("Counts below are near-miss part pairs/groups; unrelated parts are intentionally omitted.");
                if (state.MeshMergeBlockerCounts.Count == 0)
                {
                    sb.AppendLine("(none)");
                }
                else
                {
                    foreach (var blocker in state.MeshMergeBlockerCounts
                                 .OrderByDescending(x => x.Value)
                                 .ThenBy(x => x.Key, StringComparer.Ordinal))
                    {
                        sb.AppendLine($"{blocker.Key}: {blocker.Value}");
                        if (state.MeshMergeBlockerExamples.TryGetValue(blocker.Key, out var examples))
                        {
                            foreach (var example in examples)
                                sb.AppendLine($"  Example: {example}");
                        }
                    }
                }

                sb.AppendLine();
                sb.AppendLine("Material blocker breakdown");
                sb.AppendLine("--------------------------");
                AppendDiagnosticCounts(
                    sb,
                    "Shader pairs",
                    state.MaterialShaderPairBlockerCounts,
                    maxEntries: 24);
                AppendDiagnosticCounts(
                    sb,
                    "Texture slot/value pairs",
                    state.MaterialTextureSlotBlockerCounts,
                    maxEntries: 32);
                AppendDiagnosticCounts(
                    sb,
                    "Material parameter/value pairs",
                    state.MaterialParameterFieldBlockerCounts,
                    maxEntries: 32);

                sb.AppendLine();
                sb.AppendLine("Texture-blocked merge opportunities");
                sb.AppendLine("-----------------------------------");
                sb.AppendLine("Each entry is one additional merge group potentially removable by combining two existing atlas batches.");
                sb.AppendLine("Pixel cost compares the two current complete batches with one hypothetical combined batch.");
                var opportunities = state.TextureMergeOpportunities.Values
                    .OrderBy(x => x.Combination.Fits ? 0 : 1)
                    .ThenBy(x => x.Combination.AdditionalPercent)
                    .ThenBy(x => x.Combination.AdditionalPixels)
                    .ThenBy(x => x.RigidPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.LodIndex)
                    .ToList();

                if (opportunities.Count == 0)
                {
                    sb.AppendLine("(none)");
                }
                else
                {
                    sb.AppendLine($"Planner-actionable opportunities: {opportunities.Count}");
                    sb.AppendLine($"Opportunities whose atlas combination fits: {opportunities.Count(x => x.Combination.Fits)}");
                    sb.AppendLine($"Unique atlas-batch combinations evaluated: {state.AtlasBatchCombinationDiagnostics.Count}");
                    sb.AppendLine();

                    foreach (var opportunity in opportunities.Take(80))
                    {
                        var combination = opportunity.Combination;
                        sb.AppendLine($"Rigid: {opportunity.RigidPath}");
                        sb.AppendLine($"  LOD: {opportunity.LodIndex}");
                        sb.AppendLine($"  Parts: {string.Join(", ", opportunity.PartIndices.OrderBy(x => x))}");
                        sb.AppendLine($"  Atlas batches: {opportunity.FirstBatchId} + {opportunity.SecondBatchId}");
                        if (combination.Fits)
                        {
                            sb.AppendLine($"  Current atlas pixels: {combination.BaselinePixelCost:N0}");
                            sb.AppendLine($"  Combined atlas pixels: {combination.CombinedPixelCost:N0}");
                            sb.AppendLine(
                                $"  Additional pixels: {combination.AdditionalPixels:N0} " +
                                $"({combination.AdditionalPercent:+0.##;-0.##;0}%)");
                        }
                        else
                        {
                            sb.AppendLine($"  Result: DOES NOT FIT ({combination.FailureReason})");
                        }

                        foreach (var example in opportunity.Examples)
                            sb.AppendLine($"  Texture difference: {example}");
                    }

                    if (opportunities.Count > 80)
                        sb.AppendLine($"... {opportunities.Count - 80} additional opportunities omitted.");
                }

                if (state.MeshMergeSkipMessages.Count != 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Mesh merge skips");
                    sb.AppendLine("----------------");
                    foreach (var message in state.MeshMergeSkipMessages)
                        sb.AppendLine(message);
                }

                sb.AppendLine();
            }

            sb.AppendLine("Uniform constant source textures");
            sb.AppendLine("--------------------------------");
            foreach (var path in state.UniformConstantTexturePaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine(path);
            if (state.UniformConstantTexturePaths.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("UV island normalizations");
            sb.AppendLine("------------------------");
            if (state.UvIslandNormalizationEntries.Count == 0)
            {
                sb.AppendLine("(none)");
            }
            else
            {
                foreach (var entry in state.UvIslandNormalizationEntries
                             .OrderBy(x => x.Mesh.GeometryPath, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(x => x.Mesh.LodIndex)
                             .ThenBy(x => x.Mesh.PartIndex))
                {
                    sb.AppendLine($"Mesh: {entry.Mesh}");
                    sb.AppendLine($"  Islands: {entry.IslandCount}, shifted: {entry.ShiftedIslandCount}");
                    sb.AppendLine(
                        $"  Crop: ({entry.OriginalCrop.X},{entry.OriginalCrop.Y}) " +
                        $"{entry.OriginalCrop.Width}x{entry.OriginalCrop.Height} -> " +
                        $"({entry.NormalizedCrop.X},{entry.NormalizedCrop.Y}) " +
                        $"{entry.NormalizedCrop.Width}x{entry.NormalizedCrop.Height}");
                }
            }
            sb.AppendLine();

            sb.AppendLine("Atlas placement diagnostics");
            sb.AppendLine("---------------------------");
            if (state.AtlasPlacementDiagnostics.Count == 0)
            {
                sb.AppendLine("(none)");
            }
            else
            {
                foreach (var batch in state.AtlasPlacementDiagnostics
                             .OrderBy(x => x.AtlasStem, StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine(
                        $"{batch.AtlasStem}: layout={batch.PlanWidth}x{batch.PlanHeight}, " +
                        $"placements={batch.Placements.Count}");
                    foreach (var path in batch.GeneratedTexturePaths)
                    {
                        if (state.GeneratedTextureDimensions.TryGetValue(path, out var dimensions))
                            sb.AppendLine($"  Generated texture: {path} ({dimensions.Width}x{dimensions.Height})");
                        else
                            sb.AppendLine($"  Generated texture: {path}");
                    }

                    foreach (var placement in batch.Placements.OrderBy(x => x.SourceId))
                    {
                        sb.AppendLine(
                            $"  Placement {placement.SourceId}: " +
                            $"source={placement.SourceWidth}x{placement.SourceHeight}, " +
                            $"crop=({placement.Crop.X},{placement.Crop.Y}) " +
                            $"{placement.Crop.Width}x{placement.Crop.Height}, " +
                            $"destination=({placement.DestinationX},{placement.DestinationY}) " +
                            $"{placement.DestinationWidth}x{placement.DestinationHeight}, " +
                            $"padding={placement.Padding}");
                        sb.AppendLine($"    Representative mesh: {placement.RepresentativeMesh}");
                        sb.AppendLine($"    Material: {placement.MaterialPath}");
                        sb.AppendLine($"    BaseColour: {FormatDiagnosticTexturePath(placement.BaseColourPath)}");
                        sb.AppendLine($"    MaterialMap: {FormatDiagnosticTexturePath(placement.MaterialMapPath)}");
                        sb.AppendLine($"    Normal: {FormatDiagnosticTexturePath(placement.NormalPath)}");
                        sb.AppendLine($"    Mask: {FormatDiagnosticTexturePath(placement.MaskPath)}");

                        AppendPlacementRelation(
                            sb,
                            "Same BaseColour sampled region",
                            placement.SameBaseColourRegionSourceIds);
                        AppendPlacementRelation(
                            sb,
                            "BaseColour crop contains placement(s)",
                            placement.BaseColourContainsSourceIds);
                        AppendPlacementRelation(
                            sb,
                            "BaseColour crop is contained by placement(s)",
                            placement.BaseColourContainedBySourceIds);
                        AppendPlacementRelation(
                            sb,
                            "Full texture-set crop contains placement(s)",
                            placement.FullTextureSetContainsSourceIds);
                        AppendPlacementRelation(
                            sb,
                            "Full texture-set crop is contained by placement(s)",
                            placement.FullTextureSetContainedBySourceIds);

                        sb.AppendLine($"    Mapped meshes: {placement.MeshMappings.Count}");
                        foreach (var mapping in placement.MeshMappings)
                        {
                            var flags = new List<string>();
                            if (mapping.UvIslandCanonicalized)
                                flags.Add("uv-islands");
                            if (mapping.WrappedUvCanonicalized)
                                flags.Add("wrapped-uv");
                            if (mapping.ContentCanonicalized)
                                flags.Add("content-dedupe");

                            sb.AppendLine(
                                $"      {mapping.Mesh} | VMD={mapping.RootVmdPath} | " +
                                $"uv-offset=({mapping.UvOffsetU:0.######},{mapping.UvOffsetV:0.######})" +
                                (flags.Count == 0
                                    ? string.Empty
                                    : $" | {string.Join(",", flags)}"));
                        }
                    }

                    sb.AppendLine();
                }
            }

            sb.AppendLine("Generated atlas textures");
            sb.AppendLine("------------------------");
            foreach (var path in state.GeneratedTexturePaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (state.GeneratedTextureDimensions.TryGetValue(path, out var dimensions))
                    sb.AppendLine($"{path} ({dimensions.Width}x{dimensions.Height})");
                else
                    sb.AppendLine(path);
            }
            if (state.GeneratedTexturePaths.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("Generated atlas materials");
            sb.AppendLine("-------------------------");
            foreach (var path in state.GeneratedMaterialPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine(path);
            if (state.GeneratedMaterialPaths.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("Removed superseded files");
            sb.AppendLine("------------------------");
            foreach (var path in state.RemovedFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine(path);
            if (state.RemovedFiles.Count == 0)
                sb.AppendLine("(none)");
            sb.AppendLine();

            sb.AppendLine("Validation");
            sb.AppendLine("----------");
            foreach (var message in state.ValidationMessages)
                sb.AppendLine(message);
            if (state.ValidationMessages.Count == 0)
                sb.AppendLine("(not completed)");

            File.WriteAllText(state.ReportPath, sb.ToString(), Encoding.UTF8);
        }

        private static List<AtlasCandidate> AlignSharedUvIslandCuts(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates)
        {
            var replacements = new Dictionary<MeshKey, AtlasCandidate>();

            foreach (var group in candidates.GroupBy(BuildAtlasTextureSetIdentity))
            {
                var members = group.ToList();
                if (members.Count < 2)
                    continue;

                var sharedCutU = ChooseSharedUvIslandCut(members, useU: true);
                var sharedCutV = ChooseSharedUvIslandCut(members, useU: false);
                var groupChanged = false;

                foreach (var candidate in members)
                {
                    var proposal = TextureAtlasBuilder.RecalculateDisconnectedUvIslandNormalization(
                        candidate.UvIslandAnalysis,
                        sharedCutU,
                        sharedCutV);
                    var originalBounds = new UvBounds(
                        proposal.OriginalMinU,
                        proposal.OriginalMinV,
                        proposal.OriginalMaxU,
                        proposal.OriginalMaxV);
                    var normalization = BuildUvIslandNormalization(
                        candidate.Width,
                        candidate.Height,
                        originalBounds,
                        proposal,
                        allowEqualCrop: true);

                    if (normalization == null)
                        continue;

                    if (candidate.UvIslandNormalization != null &&
                        normalization.NormalizedCrop == candidate.UvIslandNormalization.NormalizedCrop &&
                        proposal.CutU == candidate.UvIslandAnalysis.CutU &&
                        proposal.CutV == candidate.UvIslandAnalysis.CutV)
                    {
                        continue;
                    }

                    replacements[candidate.Key] = candidate with
                    {
                        Bounds = normalization.NormalizedBounds,
                        UvIslandAnalysis = proposal,
                        UvIslandNormalization = normalization
                    };
                    groupChanged = true;
                }

                if (groupChanged)
                    state.SharedUvIslandCutGroups++;
            }

            return candidates
                .Select(candidate => replacements.TryGetValue(candidate.Key, out var replacement)
                    ? replacement
                    : candidate)
                .ToList();
        }

        private static double ChooseSharedUvIslandCut(
            IReadOnlyList<AtlasCandidate> candidates,
            bool useU)
        {
            var candidateCuts = candidates
                .Select(candidate => useU
                    ? candidate.UvIslandAnalysis.CutU
                    : candidate.UvIslandAnalysis.CutV)
                .Append(0)
                .Distinct()
                .OrderBy(x => x)
                .ToArray();

            var sourceSize = useU ? candidates[0].Width : candidates[0].Height;
            var bestCut = candidateCuts[0];
            long bestUnionPixels = long.MaxValue;
            long bestTotalPixels = long.MaxValue;

            foreach (var cut in candidateCuts)
            {
                var unionMin = double.PositiveInfinity;
                var unionMax = double.NegativeInfinity;
                long totalPixels = 0;

                foreach (var candidate in candidates)
                {
                    var bounds = TextureAtlasBuilder.CalculateDisconnectedUvIslandAxisBoundsForCut(
                        candidate.UvIslandAnalysis,
                        cut,
                        useU);

                    // Express each candidate in the same unwrapped period for this seam.
                    // Whole-mesh integer translations are sampling-equivalent and later become
                    // the existing wrapped-UV mapping offset.
                    var wholeTile = Math.Floor(bounds.Min - cut);
                    var min = bounds.Min - wholeTile;
                    var max = bounds.Max - wholeTile;

                    unionMin = Math.Min(unionMin, min);
                    unionMax = Math.Max(unionMax, max);

                    var pixelMin = checked((int)Math.Floor(min * sourceSize));
                    var pixelMax = checked((int)Math.Ceiling(max * sourceSize));
                    totalPixels = checked(totalPixels + Math.Max(1, pixelMax - pixelMin));
                }

                var unionPixelMin = checked((int)Math.Floor(unionMin * sourceSize));
                var unionPixelMax = checked((int)Math.Ceiling(unionMax * sourceSize));
                var unionPixels = Math.Max(1L, (long)unionPixelMax - unionPixelMin);

                if (unionPixels < bestUnionPixels ||
                    (unionPixels == bestUnionPixels && totalPixels < bestTotalPixels))
                {
                    bestUnionPixels = unionPixels;
                    bestTotalPixels = totalPixels;
                    bestCut = cut;
                }
            }

            return bestCut;
        }

        private static UvIslandNormalization? BuildUvIslandNormalization(
            int sourceWidth,
            int sourceHeight,
            UvBounds originalBounds,
            TextureAtlasUvIslandNormalization proposal,
            bool allowEqualCrop)
        {
            var proposedBounds = new UvBounds(
                proposal.NormalizedMinU,
                proposal.NormalizedMinV,
                proposal.NormalizedMaxU,
                proposal.NormalizedMaxV);
            var originalCrop = GetEffectiveCrop(originalBounds, sourceWidth, sourceHeight);
            var proposedCrop = GetEffectiveCrop(proposedBounds, sourceWidth, sourceHeight);

            var hasUShift = proposal.TileOffsetUByVertex.Any(x => x != 0);
            var hasVShift = proposal.TileOffsetVByVertex.Any(x => x != 0);
            var useU =
                proposedCrop.Width < originalCrop.Width ||
                (allowEqualCrop && hasUShift && proposedCrop.Width == originalCrop.Width);
            var useV =
                proposedCrop.Height < originalCrop.Height ||
                (allowEqualCrop && hasVShift && proposedCrop.Height == originalCrop.Height);
            if (!useU && !useV)
                return null;

            var tileOffsetUByVertex = useU
                ? proposal.TileOffsetUByVertex
                : new int[proposal.TileOffsetUByVertex.Length];
            var tileOffsetVByVertex = useV
                ? proposal.TileOffsetVByVertex
                : new int[proposal.TileOffsetVByVertex.Length];

            var normalizedBounds = new UvBounds(
                useU ? proposedBounds.MinU : originalBounds.MinU,
                useV ? proposedBounds.MinV : originalBounds.MinV,
                useU ? proposedBounds.MaxU : originalBounds.MaxU,
                useV ? proposedBounds.MaxV : originalBounds.MaxV);
            var normalizedCrop = GetEffectiveCrop(normalizedBounds, sourceWidth, sourceHeight);

            var shiftedIslandIds = new HashSet<int>();
            for (var vertexIndex = 0; vertexIndex < proposal.IslandIdByVertex.Length; vertexIndex++)
            {
                if (tileOffsetUByVertex[vertexIndex] == 0 &&
                    tileOffsetVByVertex[vertexIndex] == 0)
                    continue;

                var islandId = proposal.IslandIdByVertex[vertexIndex];
                if (islandId >= 0)
                    shiftedIslandIds.Add(islandId);
            }

            if (shiftedIslandIds.Count == 0)
                return null;

            return new UvIslandNormalization(
                proposal.ComponentCount,
                shiftedIslandIds.Count,
                tileOffsetUByVertex,
                tileOffsetVByVertex,
                originalBounds,
                normalizedBounds,
                originalCrop,
                normalizedCrop);
        }

        private static string FormatDiagnosticTexturePath(string path)
            => string.IsNullOrWhiteSpace(path) ? "<none>" : path;

        private static void AppendPlacementRelation(
            StringBuilder sb,
            string label,
            IReadOnlyList<int> sourceIds)
        {
            if (sourceIds.Count != 0)
                sb.AppendLine($"    {label}: {string.Join(", ", sourceIds)}");
        }

        private static AtlasPlacementBatchDiagnostic BuildAtlasPlacementDiagnosticSnapshot(
            string atlasStem,
            TextureAtlasPlan plan,
            SharedAtlasBatch sharedBatch,
            IReadOnlyList<AtlasCandidate> candidates,
            IReadOnlyDictionary<string, string> generatedPaths)
        {
            var placements = new List<AtlasPlacementDiagnostic>(sharedBatch.Sources.Count);
            var candidatesByKey = candidates.ToDictionary(x => x.Key);

            foreach (var source in sharedBatch.Sources.OrderBy(x => x.Id))
            {
                var representative = source.Representative;
                var placement = plan.Placements.Single(x => x.Id == source.Id);
                var fullTextureSet = BuildAtlasTextureSetIdentity(representative);
                var baseColourPath = Normalize(
                    GetTexturePath(representative.MaterialDocument, "t_xml_base_colour"));
                var materialMapPath = Normalize(
                    GetTexturePath(representative.MaterialDocument, "t_xml_material_map"));
                var normalPath = Normalize(
                    GetTexturePath(representative.MaterialDocument, "t_xml_normal"));
                var maskPath = Normalize(
                    GetTexturePath(representative.MaterialDocument, "t_xml_mask"));

                var sameBaseColourRegion = new List<int>();
                var baseColourContains = new List<int>();
                var baseColourContainedBy = new List<int>();
                var fullSetContains = new List<int>();
                var fullSetContainedBy = new List<int>();

                foreach (var other in sharedBatch.Sources)
                {
                    if (other.Id == source.Id)
                        continue;

                    var sameBaseColour =
                        !string.IsNullOrWhiteSpace(baseColourPath) &&
                        baseColourPath.Equals(
                            Normalize(GetTexturePath(
                                other.Representative.MaterialDocument,
                                "t_xml_base_colour")),
                            StringComparison.OrdinalIgnoreCase) &&
                        representative.Width == other.Representative.Width &&
                        representative.Height == other.Representative.Height;

                    if (sameBaseColour)
                    {
                        if (source.Crop == other.Crop)
                            sameBaseColourRegion.Add(other.Id);
                        else if (ContainsCrop(source.Crop, other.Crop))
                            baseColourContains.Add(other.Id);
                        else if (ContainsCrop(other.Crop, source.Crop))
                            baseColourContainedBy.Add(other.Id);
                    }

                    if (fullTextureSet == BuildAtlasTextureSetIdentity(other.Representative))
                    {
                        if (ContainsCrop(source.Crop, other.Crop) && source.Crop != other.Crop)
                            fullSetContains.Add(other.Id);
                        else if (ContainsCrop(other.Crop, source.Crop) && source.Crop != other.Crop)
                            fullSetContainedBy.Add(other.Id);
                    }
                }

                var meshMappings = sharedBatch.MappingByMesh
                    .Where(x => x.Value.SourceId == source.Id)
                    .OrderBy(x => x.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Key.LodIndex)
                    .ThenBy(x => x.Key.PartIndex)
                    .Select(x =>
                    {
                        var candidate = candidatesByKey[x.Key];
                        return new AtlasPlacementMeshDiagnostic(
                            x.Key,
                            candidate.RootVmdPath,
                            x.Value.UvOffsetU,
                            x.Value.UvOffsetV,
                            candidate.UvIslandNormalization != null,
                            x.Value.WrappedUvCanonicalized,
                            x.Value.ContentCanonicalized);
                    })
                    .ToList();

                placements.Add(new AtlasPlacementDiagnostic(
                    source.Id,
                    representative.Width,
                    representative.Height,
                    source.Crop,
                    placement.DestinationX,
                    placement.DestinationY,
                    placement.CropWidth,
                    placement.CropHeight,
                    placement.Padding,
                    representative.Key,
                    representative.MaterialPath,
                    baseColourPath,
                    materialMapPath,
                    normalPath,
                    maskPath,
                    sameBaseColourRegion,
                    baseColourContains,
                    baseColourContainedBy,
                    fullSetContains,
                    fullSetContainedBy,
                    meshMappings));
            }

            return new AtlasPlacementBatchDiagnostic(
                atlasStem,
                plan.Width,
                plan.Height,
                generatedPaths.Values
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                placements);
        }

        private static bool ContainsCrop(AtlasCrop outer, AtlasCrop inner)
        {
            var outerRight = checked((long)outer.X + outer.Width);
            var outerBottom = checked((long)outer.Y + outer.Height);
            var innerRight = checked((long)inner.X + inner.Width);
            var innerBottom = checked((long)inner.Y + inner.Height);

            return inner.X >= outer.X &&
                   inner.Y >= outer.Y &&
                   innerRight <= outerRight &&
                   innerBottom <= outerBottom;
        }

        private static UvBounds GetUvBounds(RmvModel model)
        {
            if (model.Mesh.IndexList.Length == 0)
                throw new InvalidOperationException("Cannot atlas a mesh with no triangles.");

            var minU = float.MaxValue;
            var minV = float.MaxValue;
            var maxU = float.MinValue;
            var maxV = float.MinValue;

            foreach (var vertexIndex in model.Mesh.IndexList.Distinct())
            {
                if (vertexIndex >= model.Mesh.VertexList.Length)
                    throw new InvalidOperationException("Rigid mesh contains an invalid vertex index.");

                var uv = model.Mesh.VertexList[vertexIndex].Uv;
                minU = Math.Min(minU, uv.X);
                minV = Math.Min(minV, uv.Y);
                maxU = Math.Max(maxU, uv.X);
                maxV = Math.Max(maxV, uv.Y);
            }

            return new UvBounds(minU, minV, maxU, maxV);
        }

        private static SharedAtlasBatch BuildSharedAtlasBatch(
            BatchState state,
            IReadOnlyList<AtlasCandidate> candidates,
            bool mergeCompatibleCrops,
            bool deduplicateByContent)
        {
            var sources = new List<SharedAtlasSource>();
            var mappingByMesh = new Dictionary<MeshKey, SharedAtlasMeshMapping>();

            foreach (var group in candidates.GroupBy(BuildAtlasTextureSetIdentity))
            {
                var canonicalCrops = group.ToDictionary(
                    candidate => candidate.Key,
                    GetCanonicalCrop);
                var clusters = group
                    .GroupBy(candidate => canonicalCrops[candidate.Key].Crop)
                    .Select(cropGroup => new AtlasCropCluster(
                        cropGroup.Key,
                        cropGroup.ToList()))
                    .ToList();

                // Different LODs commonly use the exact same texture set but touch slightly
                // different UV extents. Requiring an identical crop duplicates most of the
                // texture in the atlas. Merge crops whenever their union costs less atlas area
                // (including padding) than storing them separately.
                //
                // Crops that differ only by an integer wrapped-UV tile translation are first
                // canonicalized into the same source-texture period. For example [0..1] and
                // [1..2] share one atlas placement instead of copying the same texels twice.
                while (mergeCompatibleCrops &&
                       TryFindBestCropMerge(clusters, out var leftIndex, out var rightIndex, out var union))
                {
                    var left = clusters[leftIndex];
                    var right = clusters[rightIndex];
                    var members = left.Members.Concat(right.Members).ToList();

                    if (rightIndex > leftIndex)
                    {
                        clusters.RemoveAt(rightIndex);
                        clusters.RemoveAt(leftIndex);
                    }
                    else
                    {
                        clusters.RemoveAt(leftIndex);
                        clusters.RemoveAt(rightIndex);
                    }

                    clusters.Add(new AtlasCropCluster(union, members));
                }

                foreach (var cluster in clusters)
                {
                    var representative = cluster.Members
                        .OrderByDescending(member => member.AtlasResolutionScale)
                        .ThenBy(member => member.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(member => member.Key.LodIndex)
                        .ThenBy(member => member.Key.PartIndex)
                        .First();
                    var source = new SharedAtlasSource(
                        sources.Count,
                        representative,
                        cluster.Crop);
                    sources.Add(source);

                    foreach (var candidate in cluster.Members)
                    {
                        var canonical = canonicalCrops[candidate.Key];
                        var wrapped =
                            canonical.TileOffsetU != 0 ||
                            canonical.TileOffsetV != 0;
                        mappingByMesh[candidate.Key] = new SharedAtlasMeshMapping(
                            source.Id,
                            canonical.TileOffsetU,
                            canonical.TileOffsetV,
                            wrapped,
                            ContentCanonicalized: false);
                    }
                }
            }

            var structuralSourceCount = sources.Count;
            if (deduplicateByContent)
                DeduplicateAtlasSourcesByContent(state, sources, mappingByMesh);

            return new SharedAtlasBatch(
                sources,
                mappingByMesh,
                structuralSourceCount - sources.Count);
        }

        private static void DeduplicateAtlasSourcesByContent(
            BatchState state,
            List<SharedAtlasSource> sources,
            Dictionary<MeshKey, SharedAtlasMeshMapping> mappingByMesh)
        {
            var removedSourceIds = new HashSet<int>();

            foreach (var compatibilityGroup in sources.GroupBy(BuildAtlasContentCompatibilityKey))
            {
                var compatibleSources = compatibilityGroup.ToList();
                if (compatibleSources.Count < 2)
                    continue;

                foreach (var contentGroup in compatibleSources.GroupBy(
                             source => GetAtlasContentIdentity(state, source)))
                {
                    var identicalSources = contentGroup
                        .OrderBy(x => x.Id)
                        .ToList();
                    if (identicalSources.Count < 2)
                        continue;

                    var canonical = identicalSources
                        .OrderByDescending(source => source.Representative.AtlasResolutionScale)
                        .ThenBy(source => source.Id)
                        .First();
                    foreach (var duplicate in identicalSources.Where(source => source.Id != canonical.Id))
                    {
                        removedSourceIds.Add(duplicate.Id);

                        var uvDeltaU =
                            (duplicate.Crop.X - canonical.Crop.X) /
                            (float)duplicate.Representative.Width;
                        var uvDeltaV =
                            (duplicate.Crop.Y - canonical.Crop.Y) /
                            (float)duplicate.Representative.Height;

                        var affectedMeshes = mappingByMesh
                            .Where(x => x.Value.SourceId == duplicate.Id)
                            .Select(x => x.Key)
                            .ToList();
                        foreach (var meshKey in affectedMeshes)
                        {
                            var mapping = mappingByMesh[meshKey];
                            mappingByMesh[meshKey] = mapping with
                            {
                                SourceId = canonical.Id,
                                UvOffsetU = mapping.UvOffsetU + uvDeltaU,
                                UvOffsetV = mapping.UvOffsetV + uvDeltaV,
                                ContentCanonicalized = true
                            };
                        }
                    }
                }
            }

            if (removedSourceIds.Count != 0)
                sources.RemoveAll(x => removedSourceIds.Contains(x.Id));
        }

        private static AtlasContentCompatibilityKey BuildAtlasContentCompatibilityKey(
            SharedAtlasSource source)
        {
            var candidate = source.Representative;
            return new AtlasContentCompatibilityKey(
                candidate.Width,
                candidate.Height,
                source.Crop.Width,
                source.Crop.Height,
                GetChannelContentCompatibility(candidate, "t_xml_base_colour"),
                GetChannelContentCompatibility(candidate, "t_xml_material_map"),
                GetChannelContentCompatibility(candidate, "t_xml_normal"),
                GetChannelContentCompatibility(candidate, "t_xml_mask"));
        }

        private static string GetChannelContentCompatibility(
            AtlasCandidate candidate,
            string slot)
        {
            if (candidate.ConstantChannels.TryGetValue(slot, out var constant))
            {
                return $"constant:{constant.B:X2}{constant.G:X2}{constant.R:X2}{constant.A:X2}";
            }

            if (candidate.ResolvedChannels.Contains(slot) &&
                candidate.ChannelDimensions.TryGetValue(slot, out var dimensions))
            {
                return $"resolved:{dimensions.Width}x{dimensions.Height}";
            }

            return "omitted";
        }

        private static AtlasContentIdentity GetAtlasContentIdentity(
            BatchState state,
            SharedAtlasSource source)
        {
            var candidate = source.Representative;
            return new AtlasContentIdentity(
                GetChannelContentIdentity(state, candidate, source.Crop, "t_xml_base_colour"),
                GetChannelContentIdentity(state, candidate, source.Crop, "t_xml_material_map"),
                GetChannelContentIdentity(state, candidate, source.Crop, "t_xml_normal"),
                GetChannelContentIdentity(state, candidate, source.Crop, "t_xml_mask"));
        }

        private static string GetChannelContentIdentity(
            BatchState state,
            AtlasCandidate candidate,
            AtlasCrop crop,
            string slot)
        {
            if (candidate.ConstantChannels.TryGetValue(slot, out var constant))
            {
                return $"constant:{constant.B:X2}{constant.G:X2}{constant.R:X2}{constant.A:X2}";
            }

            if (!candidate.ResolvedChannels.Contains(slot))
                return "omitted";

            var texturePath = Normalize(GetTexturePath(candidate.MaterialDocument, slot));
            if (string.IsNullOrWhiteSpace(texturePath))
            {
                throw new InvalidOperationException(
                    $"Resolved atlas channel {slot} has no texture path for {candidate.Key}.");
            }

            var cacheKey = new AtlasRegionContentHashKey(
                texturePath,
                candidate.Width,
                candidate.Height,
                crop);
            if (!state.AtlasRegionContentHashes.TryGetValue(cacheKey, out var contentHash))
            {
                var file = FindForReadStatic(state, texturePath)
                    ?? throw new InvalidOperationException(
                        $"Resolved atlas texture no longer exists: {texturePath}");
                var bytes = file.DataSource.ReadData();
                contentHash = TextureAtlasBuilder.ComputeWrappedRegionContentHash(
                    bytes,
                    candidate.Width,
                    candidate.Height,
                    crop.X,
                    crop.Y,
                    crop.Width,
                    crop.Height);
                state.AtlasRegionContentHashes[cacheKey] = contentHash;
            }

            return $"resolved:{contentHash}";
        }

        private static AtlasTextureSetIdentity BuildAtlasTextureSetIdentity(AtlasCandidate candidate)
            => new(
                candidate.Width,
                candidate.Height,
                GetChannelIdentity(candidate, "t_xml_base_colour"),
                GetChannelIdentity(candidate, "t_xml_material_map"),
                GetChannelIdentity(candidate, "t_xml_normal"),
                GetChannelIdentity(candidate, "t_xml_mask"));

        private static bool TryFindBestCropMerge(
            IReadOnlyList<AtlasCropCluster> clusters,
            out int leftIndex,
            out int rightIndex,
            out AtlasCrop union)
        {
            leftIndex = -1;
            rightIndex = -1;
            union = default;
            long bestSavings = 0;

            for (var i = 0; i < clusters.Count; i++)
            {
                for (var j = i + 1; j < clusters.Count; j++)
                {
                    var leftScale = clusters[i].Members.Max(member => member.AtlasResolutionScale);
                    var rightScale = clusters[j].Members.Max(member => member.AtlasResolutionScale);
                    if (Math.Abs(leftScale - rightScale) > 0.000001)
                        continue;

                    var candidateUnion = Union(clusters[i].Crop, clusters[j].Crop);
                    var separateArea =
                        GetPaddedCropArea(clusters[i].Crop, leftScale) +
                        GetPaddedCropArea(clusters[j].Crop, rightScale);
                    var mergedArea = GetPaddedCropArea(candidateUnion, leftScale);
                    var savings = separateArea - mergedArea;

                    if (savings <= bestSavings)
                        continue;

                    bestSavings = savings;
                    leftIndex = i;
                    rightIndex = j;
                    union = candidateUnion;
                }
            }

            return leftIndex >= 0;
        }

        private static AtlasCrop Union(AtlasCrop left, AtlasCrop right)
        {
            var x = Math.Min(left.X, right.X);
            var y = Math.Min(left.Y, right.Y);
            var rightEdge = Math.Max(
                checked(left.X + left.Width),
                checked(right.X + right.Width));
            var bottomEdge = Math.Max(
                checked(left.Y + left.Height),
                checked(right.Y + right.Height));

            return new AtlasCrop(
                x,
                y,
                checked(rightEdge - x),
                checked(bottomEdge - y));
        }

        private static long GetPaddedCropArea(
            AtlasCrop crop,
            double resolutionScale = 1.0)
        {
            if (!double.IsFinite(resolutionScale) ||
                resolutionScale <= 0 ||
                resolutionScale > 1.0)
            {
                throw new ArgumentOutOfRangeException(nameof(resolutionScale));
            }

            var scaledWidth = Math.Max(
                1L,
                checked((long)Math.Ceiling(crop.Width * resolutionScale)));
            var scaledHeight = Math.Max(
                1L,
                checked((long)Math.Ceiling(crop.Height * resolutionScale)));
            var width = checked(scaledWidth + TextureAtlasBuilder.DefaultPadding * 2L);
            var height = checked(scaledHeight + TextureAtlasBuilder.DefaultPadding * 2L);
            return checked(width * height);
        }

        private static string GetChannelIdentity(AtlasCandidate candidate, string slot)
        {
            var path = GetTexturePath(candidate.MaterialDocument, slot);
            if (string.IsNullOrWhiteSpace(path) ||
                IsCandidateIgnoredTextureSlot(candidate, slot))
            {
                return "<none>";
            }
            if (IsTexturePlaceholder(path))
                return $"<placeholder>:{path}";
            if (candidate.ConstantChannels.ContainsKey(slot))
                return $"<constant>:{path}";
            if (candidate.ResolvedChannels.Contains(slot))
                return $"<resolved>:{path}";
            return $"<unresolved>:{path}";
        }

        private static bool IsCandidateIgnoredTextureSlot(
            AtlasCandidate candidate,
            string slot)
        {
            if (candidate.ResolvedChannels.Contains(slot) ||
                candidate.ConstantChannels.ContainsKey(slot) ||
                candidate.MissingTextures.Any(missing =>
                    missing.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return IsIgnorableUnresolvedAtlasTexture(
                slot,
                GetTexturePath(candidate.MaterialDocument, slot));
        }

        private static AtlasCrop GetEffectiveCrop(AtlasCandidate candidate)
            => GetEffectiveCrop(candidate.Bounds, candidate.Width, candidate.Height);

        private static AtlasCrop GetEffectiveCrop(
            UvBounds bounds,
            int sourceWidth,
            int sourceHeight)
        {
            var cropX = checked((int)MathF.Floor(bounds.MinU * sourceWidth));
            var cropY = checked((int)MathF.Floor(bounds.MinV * sourceHeight));
            var cropRight = checked((int)MathF.Ceiling(bounds.MaxU * sourceWidth));
            var cropBottom = checked((int)MathF.Ceiling(bounds.MaxV * sourceHeight));
            var cropWidth = checked(cropRight - cropX);
            var cropHeight = checked(cropBottom - cropY);
            if (cropWidth <= 0) cropWidth = 1;
            if (cropHeight <= 0) cropHeight = 1;
            return new AtlasCrop(cropX, cropY, cropWidth, cropHeight);
        }

        private static CanonicalAtlasCrop GetCanonicalCrop(AtlasCandidate candidate)
        {
            var crop = GetEffectiveCrop(candidate);

            // A crop wider/taller than one source period represents real repeated tiling across
            // a triangle span. Collapsing it would require splitting geometry at wrap seams, so
            // keep the existing virtual crop in that case.
            if (crop.Width > candidate.Width || crop.Height > candidate.Height)
                return new CanonicalAtlasCrop(crop, 0, 0);

            var canonicalX = PositiveModulo(crop.X, candidate.Width);
            var canonicalY = PositiveModulo(crop.Y, candidate.Height);
            var tileOffsetU = checked((crop.X - canonicalX) / candidate.Width);
            var tileOffsetV = checked((crop.Y - canonicalY) / candidate.Height);

            return new CanonicalAtlasCrop(
                new AtlasCrop(canonicalX, canonicalY, crop.Width, crop.Height),
                tileOffsetU,
                tileOffsetV);
        }

        private static int PositiveModulo(int value, int modulus)
        {
            if (modulus <= 0)
                throw new ArgumentOutOfRangeException(nameof(modulus));

            var result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        private static List<TextureAtlasLayoutSource> ToAtlasLayoutSources(
            IReadOnlyList<SharedAtlasSource> sharedSources)
        {
            var sources = new List<TextureAtlasLayoutSource>(sharedSources.Count);
            foreach (var source in sharedSources)
            {
                var candidate = source.Representative;
                var layoutDimensions = GetScaledAtlasDimensions(
                    candidate,
                    candidate.Width,
                    candidate.Height);
                sources.Add(new TextureAtlasLayoutSource(
                    source.Id,
                    layoutDimensions.Width,
                    layoutDimensions.Height,
                    source.Crop.X / (float)candidate.Width,
                    source.Crop.Y / (float)candidate.Height,
                    checked(source.Crop.X + source.Crop.Width) / (float)candidate.Width,
                    checked(source.Crop.Y + source.Crop.Height) / (float)candidate.Height));
            }

            return sources;
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

            return normalized.OuterXml;
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

        private static void ReportProgress(
            IProgress<TextureAtlasPackProgress>? progress,
            string phase,
            int current = 0,
            int total = 0,
            string? item = null)
            => progress?.Report(new TextureAtlasPackProgress(phase, current, total, item));

        public sealed record BatchResult(
            int VmdCount,
            int AtlasedMeshCount,
            int GeneratedTextureCount,
            int RemovedFileCount,
            int SkippedMeshCount,
            string ReportPath);

        private sealed record GeneratedTextureTimingEntry(
            string Path,
            int Width,
            int Height,
            TextureType TextureType,
            DirectXTexNet.DXGI_FORMAT Format,
            TimeSpan RasterElapsed,
            TimeSpan CompressionElapsed,
            bool UsedLargeBcSplitCompression,
            TextureAtlasBuildStatistics RasterStatistics,
            RawBgraMipChainCompressionStatistics? CompressionStatistics)
        {
            public TimeSpan TotalElapsed => RasterElapsed + CompressionElapsed;
        }

        private sealed record AtlasPlacementBatchDiagnostic(
            string AtlasStem,
            int PlanWidth,
            int PlanHeight,
            string[] GeneratedTexturePaths,
            List<AtlasPlacementDiagnostic> Placements);

        private sealed record AtlasPlacementDiagnostic(
            int SourceId,
            int SourceWidth,
            int SourceHeight,
            AtlasCrop Crop,
            int DestinationX,
            int DestinationY,
            int DestinationWidth,
            int DestinationHeight,
            int Padding,
            MeshKey RepresentativeMesh,
            string MaterialPath,
            string BaseColourPath,
            string MaterialMapPath,
            string NormalPath,
            string MaskPath,
            List<int> SameBaseColourRegionSourceIds,
            List<int> BaseColourContainsSourceIds,
            List<int> BaseColourContainedBySourceIds,
            List<int> FullTextureSetContainsSourceIds,
            List<int> FullTextureSetContainedBySourceIds,
            List<AtlasPlacementMeshDiagnostic> MeshMappings);

        private sealed record AtlasPlacementMeshDiagnostic(
            MeshKey Mesh,
            string RootVmdPath,
            float UvOffsetU,
            float UvOffsetV,
            bool UvIslandCanonicalized,
            bool WrappedUvCanonicalized,
            bool ContentCanonicalized);

        private sealed record BcnTextureResidencySummary(
            int ReachableDdsCount,
            int BcnTextureCount,
            int UnsupportedDdsCount,
            int ArmyUnmappedBcnTextureCount,
            long BcnBytes,
            double EstimatedScenarioResidentBcnBytes,
            long GeneratedAtlasBcnBytes,
            double EstimatedScenarioResidentGeneratedAtlasBcnBytes,
            IReadOnlyDictionary<string, int> TextureCountByFormat,
            IReadOnlyDictionary<string, long> BytesByFormat);

        private sealed record ExpectedWsModelOccurrences(
            IReadOnlyDictionary<
                string,
                IReadOnlyDictionary<Wh3UnitVisualRole, double>>
                ByWsModel);

        private sealed record UnitVisualConfiguration(
            double Probability,
            IReadOnlyDictionary<string, int> WsModelOccurrences);

        private sealed record ArmyResidencyModel(
            IReadOnlyDictionary<Wh3ArmyUnitCategory, HashSet<string>> UnitsByCategory,
            IReadOnlyDictionary<
                string,
                Dictionary<Wh3ArmyUnitCategory, HashSet<string>>> UnitsByVmd,
            IReadOnlyDictionary<string, int> EntityCountByUnit,
            IReadOnlyDictionary<
                string,
                Dictionary<Wh3UnitVisualRole, int>> EntityCountByUnitAndRole,
            IReadOnlyDictionary<string, Wh3ArmyUnitCategory> CategoryByUnit,
            IReadOnlyDictionary<string, ExpectedWsModelOccurrences>
                ExpectedWsModelOccurrencesByUnit,
            IReadOnlyDictionary<
                string,
                Dictionary<Wh3UnitVisualRole, IReadOnlyList<UnitVisualConfiguration>>>
                VisualConfigurationsByUnitAndRole,
            Wh3ArmyVisualScenario Scenario);

        private sealed record ArmyLocalitySplitEvaluationReportEntry(
            bool Accepted,
            string Decision,
            long BaselineGlobalPixels,
            long ProposedGlobalPixels,
            long BaselineVmdResidentPixels,
            long ProposedVmdResidentPixels,
            double BaselineExpectedArmyResidentPixels,
            double ProposedExpectedArmyResidentPixels);

        private sealed class BatchState
        {
            public IPackFileContainer Source { get; }
            public IPackFileContainer Output { get; }
            public IPackFileService PackFileService { get; }
            public string SourcePath { get; }
            public string OutputPath { get; }
            public string ReportPath { get; }
            public Dictionary<string, VariantMesh> VmdDocuments { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, XmlDocument> WsDocuments { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, XmlDocument> MaterialDocuments { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, HashSet<string>> ReachableWsModelsByRoot { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Wh3UnitCategoryResolution? UnitCategoryResolution { get; set; }
            public Dictionary<string, HashSet<string>> XmlCompatibilityRepairs { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public List<MalformedVmdEntry> MalformedVmdRoots { get; } = [];
            public List<MalformedVmdEntry> MalformedReferencedVmds { get; } = [];
            public List<MalformedXmlAssetEntry> MalformedWsModelsIgnored { get; } = [];
            public List<MalformedXmlAssetEntry> MalformedMaterialsIgnored { get; } = [];
            public List<string> DirectAssetSkipMessages { get; } = [];
            public Dictionary<string, RmvFile> RigidModels { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, TextureInspection> TextureInspections { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<AtlasRegionContentHashKey, string> AtlasRegionContentHashes { get; } = [];
            public Dictionary<MeshKey, List<WsUsage>> Usages { get; } = [];
            public HashSet<MeshKey> ProcessedMeshes { get; } = [];
            public HashSet<string> ModifiedWsModels { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> ModifiedRigids { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> GeneratedTexturePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, (int Width, int Height)> GeneratedTextureDimensions { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public List<GeneratedTextureTimingEntry> GeneratedTextureTimings { get; } = [];
            public List<AtlasPlacementBatchDiagnostic> AtlasPlacementDiagnostics { get; } = [];
            public HashSet<string> GeneratedMaterialPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, GeneratedMaterialEntry> GeneratedMaterialByContentHash { get; } = new(StringComparer.Ordinal);
            public int GeneratedMaterialReuses { get; set; }
            public bool MergeCompatibleMeshesEnabled { get; }
            public bool ShareAtlasesAcrossVmdsEnabled { get; }
            public bool OptimizeGeometryEnabled { get; }
            public bool AtlasAllVmdsEnabled { get; set; }
            public int SourceVmdRootCount { get; set; }
            public int AtlasVmdRootCount { get; set; }
            public int PackWideCandidateCount { get; set; }
            public int AtlasBatchCount { get; set; }
            public int TexelDensityScaledMeshes { get; set; }
            public long TexelDensityOriginalPixelArea { get; set; }
            public long TexelDensityScaledPixelArea { get; set; }
            public Dictionary<double, int> TexelDensityScaleCounts { get; } = [];
            public int AtlasValueGateBatchesAccepted { get; set; }
            public int AtlasValueGateBatchesRejected { get; set; }
            public int AtlasValueGateBroadBatchesSplit { get; set; }
            public int AtlasValueGateCandidatesAccepted { get; set; }
            public int AtlasValueGateCandidatesRejected { get; set; }
            public long AtlasValueGateGeneratedBcnBytesAccepted { get; set; }
            public long AtlasValueGateGeneratedBcnBytesRejected { get; set; }
            public long AtlasValueGateRetiredBcnBytesAccepted { get; set; }
            public long AtlasValueGateRetiredBcnBytesRejected { get; set; }
            public long AtlasValueGateNetBcnBytesAccepted { get; set; }
            public long AtlasValueGateNetBcnBytesRejected { get; set; }
            public double AtlasValueGateExpectedArmyGeneratedBcnBytesAccepted { get; set; }
            public double AtlasValueGateExpectedArmyGeneratedBcnBytesRejected { get; set; }
            public double AtlasValueGateExpectedArmyRetiredBcnBytesAccepted { get; set; }
            public double AtlasValueGateExpectedArmyRetiredBcnBytesRejected { get; set; }
            public double AtlasValueGateExpectedArmyNetBcnBytesAccepted { get; set; }
            public double AtlasValueGateExpectedArmyNetBcnBytesRejected { get; set; }
            public double AtlasValueGateExpectedDrawsAccepted { get; set; }
            public List<AtlasValueGateBatchEconomics> AtlasValueGateAcceptedBatchEconomics { get; } = [];
            public Dictionary<string, AtlasValueGateSourceTexture>? AtlasValueGateSourceTextureIndex { get; set; }
            public Dictionary<string, HashSet<string>>? AtlasValueGateRootsByWsModel { get; set; }
            public HashSet<AtlasValueGateSourceReference> AtlasValueGateRewrittenSourceReferences { get; } = [];
            public int ConstantOnlyAtlasChannelsSkipped { get; set; }
            public HashSet<string> UniformConstantTexturePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
            public int LargeBcSplitCompressionChannels { get; set; }
            public int AtlasPixelAreaOptimizedSplits { get; set; }
            public int AtlasPixelAreaSplitEvaluations { get; set; }
            public int AtlasNonContiguousOptimizedSplits { get; set; }
            public int AtlasNonContiguousSplitEvaluations { get; set; }
            public long AtlasPixelAreaSavedByOptimizedSplits { get; set; }
            public long AtlasPixelAreaSavedByNonContiguousSplits { get; set; }
            public int VmdLocalitySplitEvaluations { get; set; }
            public int VmdLocalitySplitsAccepted { get; set; }
            public long VmdLocalityResidentPixelsSaved { get; set; }
            public double ExpectedArmyResidentPixelsBeforeLocality { get; set; }
            public double ExpectedArmyResidentPixelsAfterLocality { get; set; }
            public double ExpectedArmyResidentPixelsAfterMergeAware { get; set; }
            public double ExpectedArmyResidentPixelsSavedByLocality { get; set; }
            public ArmyResidencyModel? ArmyResidencyModel { get; set; }
            public BcnTextureResidencySummary? SourceBcnResidency { get; set; }
            public BcnTextureResidencySummary? OutputBcnResidency { get; set; }
            public List<ArmyLocalitySplitEvaluationReportEntry> ArmyLocalitySplitEvaluations { get; } = [];
            public long VmdLocalityGlobalPixelsAdded { get; set; }
            public int MergeAwareLocalityRegressionsRejected { get; set; }
            public int MergeAwareBatchPairsConsidered { get; set; }
            public int MergeAwareRepartitionEvaluations { get; set; }
            public int MergeAwareRepartitionsAccepted { get; set; }
            public long MergeAwareRepartitionPixelsSaved { get; set; }
            public int MergeAwareBatchCoalesceEvaluations { get; set; }
            public int MergeAwareBatchCoalescesAccepted { get; set; }
            public long MergeAwareBatchCoalescePixelsSaved { get; set; }
            public int MergeAwareAffinityPotentialBefore { get; set; }
            public int MergeAwareAffinityPotentialAfter { get; set; }
            public int MergeAwareAffinityEliminationsGained { get; set; }
            public double ExpectedArmyDrawCallsEliminatedBeforeMergeAware { get; set; }
            public double ExpectedArmyDrawCallsEliminatedAfterMergeAware { get; set; }
            public List<MergeAwareRepartitionReportEntry> MergeAwareRepartitionEntries { get; } = [];
            public int CrossVmdSharedAtlasBatches { get; set; }
            public int CrossVmdSharedAtlasPlacements { get; set; }
            public int CrossVmdMaterialReuses { get; set; }
            public int AtlasPlacementsGenerated { get; set; }
            public int AtlasPlacementsReused { get; set; }
            public int WrappedUvPlacementsCanonicalized { get; set; }
            public int UvIslandNormalizedMeshes { get; set; }
            public int UvIslandsShifted { get; set; }
            public long UvIslandCropPixelsSaved { get; set; }
            public int SharedUvIslandCutGroups { get; set; }
            public List<UvIslandNormalizationReportEntry> UvIslandNormalizationEntries { get; } = [];
            public int ContentDeduplicatedAtlasPlacements { get; set; }
            public int ContentCanonicalizedMeshReferences { get; set; }
            public int MeshPartsBeforeMerging { get; set; }
            public int MeshPartsAfterMerging { get; set; }
            public int MeshPartsEliminated => MeshPartsBeforeMerging - MeshPartsAfterMerging;
            public List<MeshMergeReportEntry> MeshMergeEntries { get; } = [];
            public List<string> MeshMergeSkipMessages { get; } = [];
            public Dictionary<string, int> MeshMergeBlockerCounts { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, List<string>> MeshMergeBlockerExamples { get; } = new(StringComparer.Ordinal);
            public int SemanticMaterialPathMergeParts { get; set; }
            public Dictionary<string, int> MaterialShaderPairBlockerCounts { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, int> MaterialTextureSlotBlockerCounts { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, int> MaterialParameterFieldBlockerCounts { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, MaterialMergeDiagnosticSnapshot?> MeshMergeMaterialDiagnostics { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<MeshKey, int> AtlasBatchByMesh { get; } = [];
            public Dictionary<int, AtlasBatchDiagnosticSnapshot> AtlasBatchDiagnostics { get; } = [];
            public Dictionary<AtlasBatchPair, AtlasBatchCombinationDiagnostic> AtlasBatchCombinationDiagnostics { get; } = [];
            public Dictionary<TextureMergeOpportunityKey, TextureMergeOpportunityAccumulator> TextureMergeOpportunities { get; } = [];
            public int MeshMergeInvariantGroupCount { get; set; }
            public int MeshMergeInvariantLodCount { get; set; }
            public int MeshMergeInvariantWsModelCount { get; set; }
            public int GeometryMeshesOptimized { get; set; }
            public long GeometryVerticesBefore { get; set; }
            public long GeometryVerticesAfter { get; set; }
            public long GeometryVerticesRemoved => GeometryVerticesBefore - GeometryVerticesAfter;
            public long GeometryUnreferencedVerticesRemoved { get; set; }
            public long GeometryDuplicateVerticesRemoved { get; set; }
            public List<string> RemovedFiles { get; } = [];
            public List<string> ValidationMessages { get; } = [];
            public List<AtlasedMeshReportEntry> AtlasedMeshes { get; } = [];
            public Dictionary<MeshKey, List<SkipDetail>> SkipDetails { get; } = [];
            public int BatchIndex { get; set; }
            public Dictionary<string, TimeSpan> PhaseDurations { get; } = new(StringComparer.Ordinal);
            public TimeSpan TotalElapsed { get; set; }

            public BatchState(
                IPackFileContainer source,
                IPackFileContainer output,
                IPackFileService packFileService,
                string sourcePath,
                string outputPath,
                string reportPath,
                bool mergeCompatibleMeshesEnabled,
                bool shareAtlasesAcrossVmdsEnabled,
                bool optimizeGeometryEnabled)
            {
                Source = source;
                Output = output;
                PackFileService = packFileService;
                SourcePath = sourcePath;
                OutputPath = outputPath;
                ReportPath = reportPath;
                MergeCompatibleMeshesEnabled = mergeCompatibleMeshesEnabled;
                ShareAtlasesAcrossVmdsEnabled = shareAtlasesAcrossVmdsEnabled;
                OptimizeGeometryEnabled = optimizeGeometryEnabled;
            }
        }

        private sealed record AtlasResidencySummary(
            long GlobalPixels,
            long AggregateResidentPixels,
            int MultiRootBatchCount,
            int MaxRootsPerBatch,
            string WorstRootVmdPath,
            long WorstRootPixels);

        private sealed record AtlasComponentReuseAnalysis(
            List<AtlasComponentReuseEntry> Components,
            List<AtlasComponentReusePairEntry> Pairs,
            int GeometryCount,
            int ReusedGeometryCount);

        private sealed record AtlasComponentReuseEntry(
            string WsModelPath,
            string GeometryPath,
            int VmdRootCount,
            int[] AtlasBatchIds);

        private sealed record AtlasComponentReusePairEntry(
            string LeftWsModel,
            string RightWsModel,
            string LeftGeometryPath,
            string RightGeometryPath,
            int LeftRootCount,
            int RightRootCount,
            int SharedRootCount,
            double LeftCoverage,
            double RightCoverage,
            int[] LeftAtlasBatchIds,
            int[] RightAtlasBatchIds,
            bool StronglyAsymmetric,
            bool HighBidirectionalOverlap);

        private sealed record MaterialMergeDiagnosticSnapshot(
            string RenderingIdentity,
            string Shader,
            string TextureIdentity,
            string NonTextureIdentity,
            IReadOnlyDictionary<string, string> TextureAssignments,
            IReadOnlyDictionary<string, string> NonTextureFields);

        private sealed record MaterialMergeDiagnosticComparison(
            bool PathsEqual,
            bool SemanticallyEquivalent,
            string Reason,
            string Detail,
            string LeftMaterialPath,
            string RightMaterialPath);

        private sealed record AtlasBatchDiagnosticSnapshot(
            List<AtlasCandidate> Candidates,
            long PixelCost);

        private readonly record struct AtlasBatchPair(
            int FirstBatchId,
            int SecondBatchId);

        private sealed record AtlasBatchCombinationDiagnostic(
            bool Fits,
            long BaselinePixelCost,
            long CombinedPixelCost,
            long AdditionalPixels,
            double AdditionalPercent,
            string FailureReason);

        private readonly record struct TextureMergeOpportunityKey(
            string RigidPath,
            int LodIndex,
            string RmvIdentity,
            string NonTextureMaterialIdentity,
            int FirstBatchId,
            int SecondBatchId);

        private sealed class TextureMergeOpportunityAccumulator
        {
            public string RigidPath { get; }
            public int LodIndex { get; }
            public int FirstBatchId { get; }
            public int SecondBatchId { get; }
            public AtlasBatchCombinationDiagnostic Combination { get; }
            public HashSet<int> PartIndices { get; } = [];
            public List<string> Examples { get; } = [];

            public TextureMergeOpportunityAccumulator(
                string rigidPath,
                int lodIndex,
                int firstBatchId,
                int secondBatchId,
                AtlasBatchCombinationDiagnostic combination)
            {
                RigidPath = rigidPath;
                LodIndex = lodIndex;
                FirstBatchId = firstBatchId;
                SecondBatchId = secondBatchId;
                Combination = combination;
            }
        }

        private sealed record RmvMergeDiagnosticComponents(
            string ModelTypeFlag,
            string RenderFlag,
            string VertexFormat,
            string ShaderName,
            string MaterialPayloadHash);

        private sealed record MeshMergeReportEntry(
            string RigidPath,
            int LodIndex,
            int[] OldPartIndices,
            int NewPartIndex,
            int VertexCount,
            string MaterialPath);

        private sealed record MeshMergeGroup(
            List<int> PartIndices,
            string[] MaterialPathsByWsModel);

        private sealed record MergeGeometryInvariantSnapshot(
            int VertexCount,
            int IndexCount,
            int TriangleCount,
            string VertexHash,
            ushort[] ExpectedIndices,
            float MinimumX,
            float MinimumY,
            float MinimumZ,
            float MaximumX,
            float MaximumY,
            float MaximumZ);

        private sealed record LodGeometryInvariantSnapshot(
            int VertexCount,
            int IndexCount,
            int TriangleCount);

        private readonly record struct TextureInspection(
            int Width,
            int Height,
            bool IsUniformConstant,
            TextureAtlasConstantColor ConstantColor);

        private sealed record GeneratedMaterialEntry(
            string Path,
            string RenderingIdentity,
            HashSet<string> RootVmdPaths);

        private sealed record MalformedVmdEntry(
            string Path,
            string Reason);

        private sealed record MalformedXmlAssetEntry(
            string Path,
            string Reason);

        private sealed record MissingTextureDependency(
            MeshKey Key,
            string Slot,
            string TexturePath);

        private sealed record WsUsage(
            string WsModelPath,
            XmlDocument? Document,
            XmlNode? MaterialNode,
            string MaterialPath,
            string? EmbeddedRigidPath = null)
        {
            public string AssetPath => string.IsNullOrWhiteSpace(EmbeddedRigidPath)
                ? WsModelPath
                : EmbeddedRigidPath;
        }

        private readonly record struct AtlasValueGateSourceReference(
            MeshKey? Mesh,
            string WsModelPath,
            string Slot,
            int SlotOccurrence);

        private sealed record AtlasValueGateSourceTexture(
            long BcnBytes,
            bool HasDirectVmdReference,
            HashSet<AtlasValueGateSourceReference> References);

        private sealed record AtlasValueGateBatchEconomics(
            int AcceptanceSequence,
            int CandidateCount,
            string[] RootVmdPaths,
            long GeneratedBcnBytes,
            long RetiredSourceBcnBytes,
            long NetBcnBytes,
            double ExpectedArmyGeneratedBcnBytes,
            double ExpectedArmyRetiredSourceBcnBytes,
            double ExpectedArmyNetBcnBytes,
            int RawDrawsEliminated,
            double ExpectedArmyDrawsEliminated);

        private sealed record AtlasValueGateResidencyEstimate(
            long GeneratedBcnBytes,
            long RetiredSourceBcnBytes,
            long NetBcnBytes,
            double ExpectedArmyGeneratedBcnBytes,
            double ExpectedArmyRetiredSourceBcnBytes,
            double ExpectedArmyNetBcnBytes,
            HashSet<AtlasValueGateSourceReference> RewrittenReferences)
        {
            public static AtlasValueGateResidencyEstimate Empty { get; } = new(
                0,
                0,
                0,
                0,
                0,
                0,
                []);
        }

        private sealed record AtlasCandidate(
            string RootVmdPath,
            MeshKey Key,
            RmvModel Model,
            List<WsUsage> Usages,
            string MaterialPath,
            XmlDocument MaterialDocument,
            int Width,
            int Height,
            UvBounds Bounds,
            HashSet<string> ResolvedChannels,
            Dictionary<string, TextureAtlasConstantColor> ConstantChannels,
            Dictionary<string, (int Width, int Height)> ChannelDimensions,
            List<MissingTextureDependency> MissingTextures,
            TextureAtlasUvIslandNormalization UvIslandAnalysis,
            UvIslandNormalization? UvIslandNormalization,
            double AtlasResolutionScale);

        private sealed record CandidateDiscoveryResult(
            List<AtlasCandidate> Candidates,
            List<MissingTextureDependency> MissingTextures);

        private readonly record struct MergeAffinityIdentity(
            string GeometryPath,
            int LodIndex,
            string RmvIdentity,
            string MaterialIdentity);

        private sealed record MergeAffinityGroup(
            MeshKey[] Meshes);

        private sealed record MergeAwareRepartitionReportEntry(
            int FirstBatchIndex,
            int SecondBatchIndex,
            long BaselinePixels,
            long ResultPixels,
            int BaselineAffinity,
            int ResultAffinity,
            double BaselineExpectedArmyDrawCallsEliminated,
            double ResultExpectedArmyDrawCallsEliminated);

        private sealed record AtlasBatchSplitProposal(
            List<AtlasCandidate> Left,
            List<AtlasCandidate> Right);

        private sealed record AtlasPlanningCandidateGroup(
            AtlasPlanningSourceIdentity Identity,
            List<AtlasCandidate> Candidates,
            AtlasCandidate Representative,
            AtlasCrop Crop);

        private sealed record SharedAtlasBatch(
            List<SharedAtlasSource> Sources,
            Dictionary<MeshKey, SharedAtlasMeshMapping> MappingByMesh,
            int ContentPlacementsReused);

        private readonly record struct SharedAtlasMeshMapping(
            int SourceId,
            float UvOffsetU,
            float UvOffsetV,
            bool WrappedUvCanonicalized,
            bool ContentCanonicalized);

        private sealed record SharedAtlasSource(
            int Id,
            AtlasCandidate Representative,
            AtlasCrop Crop);

        private sealed record AtlasCropCluster(
            AtlasCrop Crop,
            List<AtlasCandidate> Members);

        private readonly record struct AtlasTextureSetIdentity(
            int SourceWidth,
            int SourceHeight,
            string BaseColour,
            string MaterialMap,
            string Normal,
            string Mask);

        private readonly record struct AtlasPlanningSourceIdentity(
            AtlasTextureSetIdentity TextureSet,
            AtlasCrop Crop,
            double ResolutionScale);

        private readonly record struct AtlasContentCompatibilityKey(
            int SourceWidth,
            int SourceHeight,
            int CropWidth,
            int CropHeight,
            string BaseColour,
            string MaterialMap,
            string Normal,
            string Mask);

        private readonly record struct AtlasContentIdentity(
            string BaseColour,
            string MaterialMap,
            string Normal,
            string Mask);

        private readonly record struct AtlasRegionContentHashKey(
            string TexturePath,
            int LayoutSourceWidth,
            int LayoutSourceHeight,
            AtlasCrop Crop);

        private readonly record struct CanonicalAtlasCrop(
            AtlasCrop Crop,
            int TileOffsetU,
            int TileOffsetV);

        private readonly record struct AtlasCrop(
            int X,
            int Y,
            int Width,
            int Height);

        private sealed record UvIslandNormalization(
            int IslandCount,
            int ShiftedIslandCount,
            int[] TileOffsetUByVertex,
            int[] TileOffsetVByVertex,
            UvBounds OriginalBounds,
            UvBounds NormalizedBounds,
            AtlasCrop OriginalCrop,
            AtlasCrop NormalizedCrop);

        private sealed record UvIslandNormalizationReportEntry(
            MeshKey Mesh,
            int IslandCount,
            int ShiftedIslandCount,
            AtlasCrop OriginalCrop,
            AtlasCrop NormalizedCrop);

        private sealed record AtlasedMeshReportEntry(
            string RootVmdPath,
            MeshKey Key,
            string[] WsModelPaths,
            string OriginalMaterialPath,
            string NewMaterialPath,
            string[] AtlasPaths);

        private sealed record SkipDetail(
            string RootVmdPath,
            string WsModelPath,
            string Reason);

        private readonly record struct MeshKey(string GeometryPath, int LodIndex, int PartIndex)
        {
            public override string ToString() => $"{GeometryPath} [lod {LodIndex}, part {PartIndex}]";
        }

        private readonly record struct UvBounds(float MinU, float MinV, float MaxU, float MaxV);
    }
}
