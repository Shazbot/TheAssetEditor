using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Editors.ImportExport.Importing.Importers.PngToDds;
using Editors.ImportExport.TextureAtlas;
using Shared.Core.ErrorHandling;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Utility;
using Shared.Core.Services;
using Shared.Core.Settings;
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

        private static readonly (string Slot, TextureType Type, string Suffix)[] AtlasChannels =
        [
            ("t_xml_base_colour", TextureType.BaseColour, "base_colour"),
            ("t_xml_material_map", TextureType.MaterialMap, "material_map"),
            ("t_xml_normal", TextureType.Normal, "normal"),
            ("t_xml_mask", TextureType.Mask, "mask"),
        ];

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
                    shareAtlasesAcrossVmds: shareAtlasesAcrossVmds);
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
                $"VMD roots: {result.VmdCount}\n" +
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
            bool shareAtlasesAcrossVmds = true)
        {
            var reportPath = BuildReportPath(outputPath);
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, "Loading source pack", item: Path.GetFileName(sourcePath));
            IPackFileContainer? output = null;
            BatchState? state = null;
            List<string> vmdRoots = [];

            try
            {
                var source = _packFileContainerLoader.CreateFromPackFile(
                    PackFileContainerType.Normal,
                    sourcePath,
                    loadAsReadOnly: true);
                cancellationToken.ThrowIfCancellationRequested();

                var sourcePaths = source.GetAllFiles().Keys.ToList();
                vmdRoots = sourcePaths
                    .Where(x => Path.GetExtension(x).Equals(".variantmeshdefinition", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (vmdRoots.Count == 0)
                    throw new InvalidOperationException("The selected pack contains no .variantmeshdefinition files.");

                ReportProgress(progress, "Scanning source dependencies", item: $"{vmdRoots.Count} VMD root(s)");
                var originalReachable = CollectReachableAssetFiles(
                    source,
                    vmdRoots,
                    cancellationToken,
                    progress,
                    "Scanning source dependencies");

                var outputName = Path.GetFileNameWithoutExtension(outputPath);
                output = _packFileService.CreateNewPackFileContainer(
                    outputName,
                    PackFileVersion.PFH5,
                    PackFileCAType.MOD,
                    setEditablePack: false);

                for (var i = 0; i < sourcePaths.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (i == 0 || i == sourcePaths.Count - 1 || i % 25 == 0)
                    {
                        ReportProgress(
                            progress,
                            "Copying source pack",
                            i + 1,
                            sourcePaths.Count,
                            sourcePaths[i]);
                    }

                    _packFileService.CopyFileFromOtherPackFile(source, sourcePaths[i], output);
                }

                state = new BatchState(
                    source,
                    output,
                    sourcePath,
                    outputPath,
                    reportPath,
                    atlasMeshesWithMissingTextures ?? false,
                    mergeCompatibleMeshes,
                    shareAtlasesAcrossVmds);
                BuildWsUsageIndex(state, cancellationToken, progress);

                if (!atlasMeshesWithMissingTextures.HasValue)
                {
                    var missingTextures = FindMissingTextureDependencies(
                        state,
                        vmdRoots,
                        cancellationToken,
                        progress);
                    if (missingTextures.Count != 0)
                    {
                        ReportProgress(progress, "Waiting for missing-texture choice");
                        state.AtlasMeshesWithMissingTextures = _standardDialogs.ShowYesNoBox(
                            BuildMissingTexturePrompt(missingTextures),
                            "Texture Atlas Pack - Missing Textures") == ShowMessageBoxResult.OK;
                    }
                }

                if (shareAtlasesAcrossVmds)
                {
                    ProcessPackWideAtlases(
                        state,
                        vmdRoots,
                        cancellationToken,
                        progress);
                }
                else
                {
                    for (var i = 0; i < vmdRoots.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ProcessVmd(
                            state,
                            vmdRoots[i],
                            i + 1,
                            vmdRoots.Count,
                            cancellationToken,
                            progress);
                    }
                }

                if (mergeCompatibleMeshes)
                    MergeCompatibleMeshes(state, cancellationToken, progress);

                SaveModifiedDocuments(state, cancellationToken, progress);

                ReportProgress(progress, "Scanning rewritten dependencies");
                var currentReachable = CollectReachableAssetFiles(
                    output,
                    vmdRoots,
                    cancellationToken,
                    progress,
                    "Scanning rewritten dependencies");
                PruneUnusedAssetFiles(
                    state,
                    originalReachable,
                    currentReachable,
                    cancellationToken,
                    progress);

                ValidateOutput(state, vmdRoots, cancellationToken, progress);

                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(progress, "Saving output pack", item: Path.GetFileName(outputPath));
                var game = GameInformationDatabase.GetGameById(GameTypeEnum.Warhammer3);
                _packFileService.SavePackContainer(output, outputPath, false, game);

                WriteReport(state, vmdRoots, succeeded: true, failure: null);

                return new BatchResult(
                    vmdRoots.Count,
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
                var doc = GetWsDocument(state, wsPath);
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
                    CollectReachableWsModels(state.Source, vmdPath, cancellationToken));
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
                    materialDoc = LoadXml(materialFile);
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

        private static string BuildMissingTexturePrompt(
            IReadOnlyList<MissingTextureDependency> missingTextures)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Unresolved texture dependencies were found:");
            sb.AppendLine();

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

            sb.AppendLine("Atlas these meshes anyway?");
            sb.AppendLine();
            sb.AppendLine("Yes: atlas them and keep the missing texture paths unchanged.");
            sb.AppendLine("No: skip the affected meshes (recommended).");
            return sb.ToString();
        }

        private void ProcessVmd(
            BatchState state,
            string rootVmdPath,
            int vmdIndex,
            int vmdCount,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var candidates = CollectVmdCandidates(
                state,
                rootVmdPath,
                vmdIndex,
                vmdCount,
                cancellationToken,
                progress);

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

        private void ProcessPackWideAtlases(
            BatchState state,
            IReadOnlyList<string> vmdRoots,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var candidates = new List<AtlasCandidate>();
            var inspectedKeys = new HashSet<MeshKey>();

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
                    inspectedKeys));
            }

            state.PackWideCandidateCount = candidates.Count;

            var batches = CreateBatches(
                state,
                candidates,
                packWide: true,
                cancellationToken: cancellationToken,
                progress: progress);

            var packName = Path.GetFileNameWithoutExtension(state.SourcePath);
            ProcessAtlasBatches(
                state,
                $"pack:{packName}",
                "Pack-wide",
                batches,
                cancellationToken,
                progress);
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
            HashSet<MeshKey>? inspectedKeys = null)
        {
            ReportProgress(progress, "Discovering atlas candidates", vmdIndex, vmdCount, rootVmdPath);
            var wsModels = CollectReachableWsModels(state.Source, rootVmdPath, cancellationToken);
            var candidates = new List<AtlasCandidate>();
            var localInspectedKeys = inspectedKeys ?? new HashSet<MeshKey>();

            foreach (var wsPath in wsModels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var doc = GetWsDocument(state, wsPath);
                if (doc == null)
                    continue;

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

        private AtlasCandidate? TryCreateCandidate(
            BatchState state,
            string rootVmdPath,
            MeshKey key,
            RmvModel model,
            List<WsUsage> usages,
            out string skipReason)
        {
            skipReason = string.Empty;
            var materialPath = usages[0].MaterialPath;
            var materialFile = FindForRead(state, materialPath);
            if (materialFile == null)
            {
                skipReason = $"Material file could not be resolved: {materialPath}";
                return null;
            }

            XmlDocument materialDoc;
            try
            {
                var materialXml = Encoding.UTF8.GetString(materialFile.DataSource.ReadData());
                materialDoc = new XmlDocument();
                materialDoc.LoadXml(materialXml);
            }
            catch (Exception ex)
            {
                skipReason = $"Material XML could not be parsed: {materialPath} ({ex.Message})";
                return null;
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

            var resolvedChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var constantChannels = new Dictionary<string, TextureAtlasConstantColor>(
                StringComparer.OrdinalIgnoreCase);

            int? layoutWidth = null;
            int? layoutHeight = null;
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

                    if (state.AtlasMeshesWithMissingTextures)
                    {
                        state.AllowedMissingTexturePaths.Add(path);
                        continue;
                    }

                    skipReason = $"{channel.Slot} texture could not be resolved: {path}";
                    return null;
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

                    if (!layoutWidth.HasValue)
                    {
                        layoutWidth = inspection.Width;
                        layoutHeight = inspection.Height;
                    }
                    else if (inspection.Width != layoutWidth.Value ||
                             inspection.Height != layoutHeight!.Value)
                    {
                        skipReason =
                            $"{channel.Slot} dimensions {inspection.Width}x{inspection.Height} do not match " +
                            $"atlas layout {layoutWidth.Value}x{layoutHeight.Value}: {path}";
                        return null;
                    }

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

            var width = layoutWidth ?? primaryWidth
                ?? throw new InvalidOperationException("Base-colour dimensions were not resolved.");
            var height = layoutHeight ?? primaryHeight
                ?? throw new InvalidOperationException("Base-colour dimensions were not resolved.");

            UvBounds bounds;
            try
            {
                bounds = GetUvBounds(model);
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
                constantChannels);
        }

        private static TextureInspection GetTextureInspection(
            BatchState state,
            string texturePath,
            PackFile file)
        {
            texturePath = Normalize(texturePath);
            if (state.TextureInspections.TryGetValue(texturePath, out var cached))
                return cached;

            var bytes = file.DataSource.ReadData();
            var dimensions = TextureAtlasBuilder.GetDimensions(bytes);
            var constantColor = default(TextureAtlasConstantColor);
            var isUniformConstant =
                IsKnownConstantTexturePath(texturePath) &&
                TextureAtlasBuilder.TryGetUniformColor(bytes, out constantColor);

            var inspection = new TextureInspection(
                dimensions.Width,
                dimensions.Height,
                isUniformConstant,
                constantColor);
            state.TextureInspections[texturePath] = inspection;
            return inspection;
        }

        private static List<List<AtlasCandidate>> CreateBatches(
            BatchState state,
            List<AtlasCandidate> candidates,
            bool packWide,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var planningCandidates = packWide
                ? candidates
                    .OrderBy(BuildAtlasPlanningOrderKey, StringComparer.Ordinal)
                    .ThenBy(x => x.RootVmdPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Key.LodIndex)
                    .ThenBy(x => x.Key.PartIndex)
                    .ToList()
                : candidates;

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

                if (!CanCreatePlan([candidate], out var singleError))
                {
                    RecordSkip(
                        state,
                        candidate.RootVmdPath,
                        candidate.Key,
                        candidate.Usages.FirstOrDefault()?.WsModelPath ?? string.Empty,
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
                if (CanCreatePlan(trialPlanningRepresentatives, out _))
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
                        orphan.Usages.FirstOrDefault()?.WsModelPath ?? string.Empty,
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
                    orphan.Usages.FirstOrDefault()?.WsModelPath ?? string.Empty,
                    packWide
                        ? "No second compatible mesh was available in the pack-wide atlas candidate set."
                        : "No second compatible mesh was available in this VMD dependency set.");
            }

            return batches;
        }

        private static AtlasPlanningSourceIdentity GetAtlasPlanningSourceIdentity(AtlasCandidate candidate)
            => new(BuildAtlasTextureSetIdentity(candidate), GetEffectiveCrop(candidate));

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
                var crop = GetEffectiveCrop(candidate);
                return string.Join(
                    "\u001f",
                    prefix,
                    crop.X,
                    crop.Y,
                    crop.Width,
                    crop.Height);
            }
            catch (OverflowException)
            {
                // CanCreatePlan records the real per-mesh error. Sorting must never turn a
                // safely-skippable bad candidate into a failure for the whole pack.
                return $"{prefix}\u001finvalid\u001f{candidate.Key}";
            }
        }

        private static bool CanCreatePlan(
            IReadOnlyList<AtlasCandidate> candidates,
            out string error)
        {
            try
            {
                _ = CreateSharedAtlasPlan(candidates);
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
            IReadOnlyList<AtlasCandidate> candidates)
        {
            var mergedBatch = BuildSharedAtlasBatch(candidates, mergeCompatibleCrops: true);
            try
            {
                return (
                    mergedBatch,
                    TextureAtlasBuilder.CreatePlan(ToAtlasLayoutSources(mergedBatch.Sources)));
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or ArgumentException or OverflowException)
            {
                var exactBatch = BuildSharedAtlasBatch(candidates, mergeCompatibleCrops: false);
                return (
                    exactBatch,
                    TextureAtlasBuilder.CreatePlan(ToAtlasLayoutSources(exactBatch.Sources)));
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
            var sharedPlan = CreateSharedAtlasPlan(candidates);
            var sharedBatch = sharedPlan.Batch;
            var plan = sharedPlan.Plan;
            state.AtlasPlacementsGenerated += sharedBatch.Sources.Count;
            state.AtlasPlacementsReused += candidates.Count - sharedBatch.Sources.Count;

            state.AtlasBatchCount++;
            if (state.ShareAtlasesAcrossVmdsEnabled &&
                candidates.Select(x => x.RootVmdPath).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
            {
                state.CrossVmdSharedAtlasBatches++;
            }

            foreach (var sourceGroup in candidates.GroupBy(x => sharedBatch.SourceIdByMesh[x.Key]))
            {
                if (sourceGroup
                    .Select(x => x.RootVmdPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Skip(1)
                    .Any())
                {
                    state.CrossVmdSharedAtlasPlacements++;
                }
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

                if (textureBytes.Count == 0 && constantSources.Count == 0)
                    continue;

                var mipPngs = TextureAtlasBuilder.BuildMipPngs(
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
                    constantSources: constantSources);

                var fileName = $"{atlasStem}_{channel.Suffix}.dds";
                var atlasPackFile = PngToDdsImporter.ImportRawMipChain(
                    mipPngs,
                    channel.Type,
                    GameTypeEnum.Warhammer3,
                    fileName);
                var atlasPath = Normalize($@"{AtlasDirectory}\{fileName}");

                WriteFile(state.Output, atlasPath, atlasPackFile.DataSource.ReadData());
                generatedPaths[channel.Slot] = atlasPath;
                state.GeneratedTexturePaths.Add(atlasPath);
            }

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
                var sourceId = sharedBatch.SourceIdByMesh[candidate.Key];
                var placement = plan.Placements.Single(x => x.Id == sourceId);

                foreach (var vertexIndex in candidate.Model.Mesh.IndexList.Distinct())
                {
                    if (vertexIndex >= candidate.Model.Mesh.VertexList.Length)
                        throw new InvalidOperationException(
                            $"Mesh {candidate.Key} contains invalid vertex index {vertexIndex}.");

                    var uv = candidate.Model.Mesh.VertexList[vertexIndex].Uv;
                    var remapped = placement.TransformUv(uv.X, uv.Y, plan.Width, plan.Height);
                    candidate.Model.Mesh.VertexList[vertexIndex].Uv = new Microsoft.Xna.Framework.Vector2(remapped.U, remapped.V);
                }

                var clonedMaterial = new XmlDocument();
                clonedMaterial.LoadXml(candidate.MaterialDocument.OuterXml);

                foreach (var channel in AtlasChannels)
                {
                    if (!candidate.ResolvedChannels.Contains(channel.Slot) &&
                        !candidate.ConstantChannels.ContainsKey(channel.Slot))
                        continue;
                    if (generatedPaths.TryGetValue(channel.Slot, out var atlasPath))
                        SetTexturePath(clonedMaterial, channel.Slot, atlasPath);
                }

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

                var resolvedMaterialPath = generatedMaterial.Path;

                state.AtlasedMeshes.Add(new AtlasedMeshReportEntry(
                    candidate.RootVmdPath,
                    candidate.Key,
                    candidate.Usages.Select(x => x.WsModelPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    candidate.MaterialPath,
                    resolvedMaterialPath,
                    generatedPaths.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));

                foreach (var usage in candidate.Usages)
                {
                    usage.MaterialNode.InnerText = resolvedMaterialPath;
                    state.ModifiedWsModels.Add(usage.WsModelPath);
                }

                state.ModifiedRigids.Add(candidate.Key.GeometryPath);
                state.ProcessedMeshes.Add(candidate.Key);
            }
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

                    var groups = BuildMeshMergeGroups(
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

        private static List<MeshMergeGroup> BuildMeshMergeGroups(
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

                var identity = string.Join(
                    "\u001e",
                    GetRmvMergeIdentity(models[partIndex]),
                    string.Join("\u001f", materialPaths.Select(Normalize)));

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

            return groups
                .OrderBy(x => x.PartIndices.Min())
                .ToList();
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

            foreach (var rigidPath in state.ModifiedRigids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(progress, "Writing modified assets", ++current, total, rigidPath);
                var rmv = state.RigidModels[rigidPath];
                rmv.RecalculateOffsets();
                WriteFile(state.Output, rigidPath, ModelFactory.Create().Save(rmv));
            }

            foreach (var wsPath in state.ModifiedWsModels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(progress, "Writing modified assets", ++current, total, wsPath);
                var doc = state.WsDocuments[wsPath];
                WriteFile(state.Output, wsPath, Encoding.UTF8.GetBytes(doc.OuterXml));
            }
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

            ProtectStillReferencedAssets(
                state.Output,
                toRemove,
                cancellationToken,
                progress);

            var removePaths = toRemove.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            for (var removeIndex = 0; removeIndex < removePaths.Count; removeIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = removePaths[removeIndex];
                ReportProgress(
                    progress,
                    "Pruning unused assets",
                    removeIndex + 1,
                    removePaths.Count,
                    path);
                var file = state.Output.FindFile(path);
                if (file == null)
                    continue;

                _packFileService.DeleteFile(state.Output, file);
                state.RemovedFiles.Add(path);
            }
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
                    if (extension.Equals(".xml.material", StringComparison.OrdinalIgnoreCase))
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

        private HashSet<string> CollectReachableAssetFiles(
            IPackFileContainer container,
            IReadOnlyList<string> rootVmdPaths,
            CancellationToken cancellationToken = default,
            IProgress<TextureAtlasPackProgress>? progress = null,
            string phase = "Scanning dependencies")
        {
            var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var vmdQueue = new Queue<string>(rootVmdPaths.Select(Normalize));
            var visitedVmds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var processedVmdCount = 0;
            while (vmdQueue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var vmdPath = vmdQueue.Dequeue();
                ReportProgress(progress, phase, ++processedVmdCount, 0, vmdPath);
                if (!visitedVmds.Add(vmdPath))
                    continue;

                var vmdFile = container.FindFile(vmdPath);
                if (vmdFile == null)
                    continue;

                reachable.Add(vmdPath);
                var vmd = VariantMeshDefinitionLoader.Load(vmdFile);
                var modelRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var childVmdRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var directTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectVmdReferences(vmd, modelRefs, childVmdRefs, directTextures);

                foreach (var texture in directTextures)
                {
                    if (container.FindFile(texture) != null)
                        reachable.Add(texture);
                }

                foreach (var child in childVmdRefs)
                {
                    if (container.FindFile(child) != null)
                        vmdQueue.Enqueue(child);
                }

                foreach (var modelPath in modelRefs)
                {
                    var modelFile = container.FindFile(modelPath);
                    if (modelFile == null)
                        continue;

                    reachable.Add(modelPath);
                    if (!Path.GetExtension(modelPath).Equals(".wsmodel", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var wsDoc = LoadXml(modelFile);
                    var geometryPath = Normalize(wsDoc.SelectSingleNode("/model/geometry")?.InnerText);
                    if (!string.IsNullOrWhiteSpace(geometryPath) && container.FindFile(geometryPath) != null)
                        reachable.Add(geometryPath);

                    var materialNodes = wsDoc.SelectNodes("/model/materials/material");
                    if (materialNodes == null)
                        continue;

                    foreach (XmlNode materialNode in materialNodes)
                    {
                        var materialPath = Normalize(materialNode.InnerText);
                        var materialFile = container.FindFile(materialPath);
                        if (materialFile == null)
                            continue;

                        reachable.Add(materialPath);
                        var materialDoc = LoadXml(materialFile);
                        var textureNodes = materialDoc.SelectNodes("/material/textures/texture");
                        if (textureNodes == null)
                            continue;

                        foreach (XmlNode textureNode in textureNodes)
                        {
                            var texturePath = Normalize(
                                textureNode.SelectSingleNode("source")?.InnerText ?? textureNode.InnerText);
                            if (!string.IsNullOrWhiteSpace(texturePath) && container.FindFile(texturePath) != null)
                                reachable.Add(texturePath);
                        }
                    }
                }
            }

            return reachable;
        }

        private static HashSet<string> CollectReachableWsModels(
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

                var vmd = VariantMeshDefinitionLoader.Load(file);
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

        private XmlDocument? GetWsDocument(BatchState state, string wsPath)
        {
            wsPath = Normalize(wsPath);
            if (state.WsDocuments.TryGetValue(wsPath, out var cached))
                return cached;

            var file = state.Source.FindFile(wsPath);
            if (file == null)
                return null;

            var doc = LoadXml(file);
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
            var existing = output.FindFile(fullPath);
            if (existing != null)
                _packFileService.DeleteFile(output, existing);

            var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            var name = Path.GetFileName(fullPath);
            _packFileService.AddFilesToPack(
                output,
                [new NewPackFileEntry(directory, PackFile.CreateFromBytes(name, data))]);
        }

        private static XmlDocument LoadXml(PackFile file)
        {
            var doc = new XmlDocument();
            doc.LoadXml(Encoding.UTF8.GetString(file.DataSource.ReadData()));
            return doc;
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
                    errors.Add($"WSModel geometry no longer resolves: {wsPath} -> {geometryPath}");
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
                        errors.Add($"WSModel material no longer resolves: {wsPath} -> {materialPath}");
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
                                errors.Add(
                                    $"WSModel material table does not match rewritten rigid: " +
                                    $"{wsPath} -> {geometryPath} ({assignmentReason})");
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

                    if (state.AtlasMeshesWithMissingTextures &&
                        state.AllowedMissingTexturePaths.Contains(texturePath))
                    {
                        continue;
                    }

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

        private static void WriteReport(
            BatchState state,
            IReadOnlyList<string> vmdRoots,
            bool succeeded,
            Exception? failure)
        {
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
            sb.AppendLine($"VMD roots: {vmdRoots.Count}");
            sb.AppendLine($"Mesh parts atlased: {state.ProcessedMeshes.Count}");
            sb.AppendLine($"Mesh parts skipped: {GetEffectiveSkippedMeshCount(state)}");
            sb.AppendLine($"Atlas textures generated: {state.GeneratedTexturePaths.Count}");
            sb.AppendLine($"Atlas materials generated: {state.GeneratedMaterialPaths.Count}");
            sb.AppendLine($"Atlas material assignments reused: {state.GeneratedMaterialReuses}");
            sb.AppendLine($"Atlas placements generated: {state.AtlasPlacementsGenerated}");
            sb.AppendLine($"Atlas placements reused: {state.AtlasPlacementsReused}");
            sb.AppendLine($"Pack-wide atlas/material sharing: {(state.ShareAtlasesAcrossVmdsEnabled ? "YES" : "NO")}");
            sb.AppendLine($"Atlas batches generated: {state.AtlasBatchCount}");
            if (state.ShareAtlasesAcrossVmdsEnabled)
            {
                sb.AppendLine($"Pack-wide atlas candidates: {state.PackWideCandidateCount}");
                sb.AppendLine($"Cross-VMD shared atlas batches: {state.CrossVmdSharedAtlasBatches}");
                sb.AppendLine($"Cross-VMD shared placements: {state.CrossVmdSharedAtlasPlacements}");
                sb.AppendLine($"Cross-VMD material reuses: {state.CrossVmdMaterialReuses}");
            }
            sb.AppendLine($"Superseded asset files removed: {state.RemovedFiles.Count}");
            sb.AppendLine($"Atlas meshes with missing textures: {(state.AtlasMeshesWithMissingTextures ? "YES" : "NO")}");
            sb.AppendLine($"Merge compatible mesh parts: {(state.MergeCompatibleMeshesEnabled ? "YES" : "NO")}");
            if (state.MergeCompatibleMeshesEnabled)
            {
                sb.AppendLine($"Mesh parts before merging: {state.MeshPartsBeforeMerging}");
                sb.AppendLine($"Mesh parts after merging: {state.MeshPartsAfterMerging}");
                sb.AppendLine($"Mesh parts eliminated: {state.MeshPartsEliminated}");
            }
            sb.AppendLine();

            sb.AppendLine("VMD roots");
            sb.AppendLine("---------");
            foreach (var vmdPath in vmdRoots.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine(vmdPath);
            if (vmdRoots.Count == 0)
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

            sb.AppendLine("Generated atlas textures");
            sb.AppendLine("------------------------");
            foreach (var path in state.GeneratedTexturePaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine(path);
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

            sb.AppendLine("Allowed unresolved texture paths");
            sb.AppendLine("--------------------------------");
            foreach (var path in state.AllowedMissingTexturePaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine(path);
            if (state.AllowedMissingTexturePaths.Count == 0)
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
            IReadOnlyList<AtlasCandidate> candidates,
            bool mergeCompatibleCrops)
        {
            var sources = new List<SharedAtlasSource>();
            var sourceIdByMesh = new Dictionary<MeshKey, int>();

            foreach (var group in candidates.GroupBy(BuildAtlasTextureSetIdentity))
            {
                var clusters = group
                    .GroupBy(GetEffectiveCrop)
                    .Select(cropGroup => new AtlasCropCluster(
                        cropGroup.Key,
                        cropGroup.ToList()))
                    .ToList();

                // Different LODs commonly use the exact same texture set but touch slightly
                // different UV extents. Requiring an identical crop duplicates most of the
                // texture in the atlas. Merge crops whenever their union costs less atlas area
                // (including padding) than storing them separately.
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
                    var source = new SharedAtlasSource(
                        sources.Count,
                        cluster.Members[0],
                        cluster.Crop);
                    sources.Add(source);

                    foreach (var candidate in cluster.Members)
                        sourceIdByMesh[candidate.Key] = source.Id;
                }
            }

            return new SharedAtlasBatch(sources, sourceIdByMesh);
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
                    var candidateUnion = Union(clusters[i].Crop, clusters[j].Crop);
                    var separateArea =
                        GetPaddedCropArea(clusters[i].Crop) +
                        GetPaddedCropArea(clusters[j].Crop);
                    var mergedArea = GetPaddedCropArea(candidateUnion);
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

        private static long GetPaddedCropArea(AtlasCrop crop)
        {
            var width = checked(crop.Width + TextureAtlasBuilder.DefaultPadding * 2L);
            var height = checked(crop.Height + TextureAtlasBuilder.DefaultPadding * 2L);
            return checked(width * height);
        }

        private static string GetChannelIdentity(AtlasCandidate candidate, string slot)
        {
            var path = GetTexturePath(candidate.MaterialDocument, slot);
            if (string.IsNullOrWhiteSpace(path))
                return "<none>";
            if (IsTexturePlaceholder(path))
                return $"<placeholder>:{path}";
            if (candidate.ConstantChannels.ContainsKey(slot))
                return $"<constant>:{path}";
            if (candidate.ResolvedChannels.Contains(slot))
                return $"<resolved>:{path}";
            return $"<unresolved>:{path}";
        }

        private static AtlasCrop GetEffectiveCrop(AtlasCandidate candidate)
        {
            var cropX = checked((int)MathF.Floor(candidate.Bounds.MinU * candidate.Width));
            var cropY = checked((int)MathF.Floor(candidate.Bounds.MinV * candidate.Height));
            var cropRight = checked((int)MathF.Ceiling(candidate.Bounds.MaxU * candidate.Width));
            var cropBottom = checked((int)MathF.Ceiling(candidate.Bounds.MaxV * candidate.Height));
            var cropWidth = checked(cropRight - cropX);
            var cropHeight = checked(cropBottom - cropY);

            if (cropWidth <= 0)
                cropWidth = 1;
            if (cropHeight <= 0)
                cropHeight = 1;

            return new AtlasCrop(cropX, cropY, cropWidth, cropHeight);
        }

        private static List<TextureAtlasLayoutSource> ToAtlasLayoutSources(
            IReadOnlyList<SharedAtlasSource> sharedSources)
        {
            var sources = new List<TextureAtlasLayoutSource>(sharedSources.Count);
            foreach (var source in sharedSources)
            {
                var candidate = source.Representative;
                sources.Add(new TextureAtlasLayoutSource(
                    source.Id,
                    candidate.Width,
                    candidate.Height,
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

        private static bool IsKnownConstantTexturePath(string? path)
            => KnownConstantTexturePaths.Contains(Normalize(path));

        private static string Normalize(string? path)
            => string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : path.Trim().Replace('/', '\\').Replace("\\\\", "\\").ToLowerInvariant();

        public sealed record TextureAtlasPackProgress(
            string Phase,
            int Current = 0,
            int Total = 0,
            string? Item = null);

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

        private sealed class BatchState
        {
            public IPackFileContainer Source { get; }
            public IPackFileContainer Output { get; }
            public string SourcePath { get; }
            public string OutputPath { get; }
            public string ReportPath { get; }
            public Dictionary<string, XmlDocument> WsDocuments { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, RmvFile> RigidModels { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, TextureInspection> TextureInspections { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<MeshKey, List<WsUsage>> Usages { get; } = [];
            public HashSet<MeshKey> ProcessedMeshes { get; } = [];
            public HashSet<string> ModifiedWsModels { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> ModifiedRigids { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> GeneratedTexturePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> GeneratedMaterialPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, GeneratedMaterialEntry> GeneratedMaterialByContentHash { get; } = new(StringComparer.Ordinal);
            public int GeneratedMaterialReuses { get; set; }
            public HashSet<string> AllowedMissingTexturePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
            public bool AtlasMeshesWithMissingTextures { get; set; }
            public bool MergeCompatibleMeshesEnabled { get; }
            public bool ShareAtlasesAcrossVmdsEnabled { get; }
            public int PackWideCandidateCount { get; set; }
            public int AtlasBatchCount { get; set; }
            public int CrossVmdSharedAtlasBatches { get; set; }
            public int CrossVmdSharedAtlasPlacements { get; set; }
            public int CrossVmdMaterialReuses { get; set; }
            public int AtlasPlacementsGenerated { get; set; }
            public int AtlasPlacementsReused { get; set; }
            public int MeshPartsBeforeMerging { get; set; }
            public int MeshPartsAfterMerging { get; set; }
            public int MeshPartsEliminated => MeshPartsBeforeMerging - MeshPartsAfterMerging;
            public List<MeshMergeReportEntry> MeshMergeEntries { get; } = [];
            public List<string> MeshMergeSkipMessages { get; } = [];
            public int MeshMergeInvariantGroupCount { get; set; }
            public int MeshMergeInvariantLodCount { get; set; }
            public int MeshMergeInvariantWsModelCount { get; set; }
            public List<string> RemovedFiles { get; } = [];
            public List<string> ValidationMessages { get; } = [];
            public List<AtlasedMeshReportEntry> AtlasedMeshes { get; } = [];
            public Dictionary<MeshKey, List<SkipDetail>> SkipDetails { get; } = [];
            public int BatchIndex { get; set; }

            public BatchState(
                IPackFileContainer source,
                IPackFileContainer output,
                string sourcePath,
                string outputPath,
                string reportPath,
                bool atlasMeshesWithMissingTextures,
                bool mergeCompatibleMeshesEnabled,
                bool shareAtlasesAcrossVmdsEnabled)
            {
                Source = source;
                Output = output;
                SourcePath = sourcePath;
                OutputPath = outputPath;
                ReportPath = reportPath;
                AtlasMeshesWithMissingTextures = atlasMeshesWithMissingTextures;
                MergeCompatibleMeshesEnabled = mergeCompatibleMeshesEnabled;
                ShareAtlasesAcrossVmdsEnabled = shareAtlasesAcrossVmdsEnabled;
            }
        }

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

        private sealed record MissingTextureDependency(
            MeshKey Key,
            string Slot,
            string TexturePath);

        private sealed record WsUsage(
            string WsModelPath,
            XmlDocument Document,
            XmlNode MaterialNode,
            string MaterialPath);

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
            Dictionary<string, TextureAtlasConstantColor> ConstantChannels);

        private sealed record SharedAtlasBatch(
            List<SharedAtlasSource> Sources,
            Dictionary<MeshKey, int> SourceIdByMesh);

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
            AtlasCrop Crop);

        private readonly record struct AtlasCrop(
            int X,
            int Y,
            int Width,
            int Height);

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
