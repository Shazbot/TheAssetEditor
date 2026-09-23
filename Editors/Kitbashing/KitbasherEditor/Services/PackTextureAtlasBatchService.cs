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
using Shared.GameFormats.RigidModel.Types;
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

            using (_standardDialogs.ShowWaitCursor())
            {
                try
                {
                    var result = Process(sourcePath, outputPath);
                    _standardDialogs.ShowDialogBox(
                        $"Texture atlas pack created successfully.\n\n" +
                        $"Output: {outputPath}\n" +
                        $"VMD roots: {result.VmdCount}\n" +
                        $"Mesh parts atlased: {result.AtlasedMeshCount}\n" +
                        $"Atlas textures generated: {result.GeneratedTextureCount}\n" +
                        $"Unused asset files removed: {result.RemovedFileCount}\n" +
                        $"Mesh parts skipped: {result.SkippedMeshCount}",
                        "Texture Atlas Pack");
                }
                catch (Exception ex)
                {
                    _standardDialogs.ShowExceptionWindow(ex, "Failed to create texture atlas pack.");
                }
            }
        }

        public BatchResult Process(string sourcePath, string outputPath)
        {
            var source = _packFileContainerLoader.CreateFromPackFile(
                PackFileContainerType.Normal,
                sourcePath,
                loadAsReadOnly: true);

            var vmdRoots = source.GetAllFiles().Keys
                .Where(x => Path.GetExtension(x).Equals(".variantmeshdefinition", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (vmdRoots.Count == 0)
                throw new InvalidOperationException("The selected pack contains no .variantmeshdefinition files.");

            var originalReachable = CollectReachableAssetFiles(source, vmdRoots);

            var outputName = Path.GetFileNameWithoutExtension(outputPath);
            var output = _packFileService.CreateNewPackFileContainer(
                outputName,
                PackFileVersion.PFH5,
                PackFileCAType.MOD,
                setEditablePack: false);

            foreach (var path in source.GetAllFiles().Keys)
                _packFileService.CopyFileFromOtherPackFile(source, path, output);

            var state = new BatchState(source, output);
            BuildWsUsageIndex(state);

            foreach (var vmdPath in vmdRoots)
                ProcessVmd(state, vmdPath);

            SaveModifiedDocuments(state);

            var currentReachable = CollectReachableAssetFiles(output, vmdRoots);
            var removed = PruneUnusedAssetFiles(output, originalReachable, currentReachable);

            var game = GameInformationDatabase.GetGameById(GameTypeEnum.Warhammer3);
            _packFileService.SavePackContainer(output, outputPath, false, game);

            return new BatchResult(
                vmdRoots.Count,
                state.ProcessedMeshes.Count,
                state.GeneratedTextureCount,
                removed,
                state.SkippedMeshCount);
        }

        public static string BuildOutputPath(string sourcePath)
        {
            var directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
            var stem = Path.GetFileNameWithoutExtension(sourcePath);
            return Path.Combine(directory, stem + "_atlas.pack");
        }

        private void BuildWsUsageIndex(BatchState state)
        {
            foreach (var wsPath in state.Source.GetAllFiles().Keys
                         .Where(x => Path.GetExtension(x).Equals(".wsmodel", StringComparison.OrdinalIgnoreCase)))
            {
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

        private void ProcessVmd(BatchState state, string rootVmdPath)
        {
            var wsModels = CollectReachableWsModels(state.Source, rootVmdPath);
            var candidates = new List<AtlasCandidate>();

            foreach (var wsPath in wsModels)
            {
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
                    if (!TryParseIndex(node, "lod_index", out var lodIndex) ||
                        !TryParseIndex(node, "part_index", out var partIndex) ||
                        lodIndex < 0 || lodIndex >= rmv.ModelList.Length ||
                        partIndex < 0 || partIndex >= rmv.ModelList[lodIndex].Length)
                    {
                        continue;
                    }

                    var key = new MeshKey(geometryPath, lodIndex, partIndex);
                    if (state.ProcessedMeshes.Contains(key) ||
                        candidates.Any(x => x.Key == key))
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
                        state.SkippedMeshCount++;
                        continue;
                    }

                    var candidate = TryCreateCandidate(state, key, rmv.ModelList[lodIndex][partIndex], usages);
                    if (candidate == null)
                    {
                        state.SkippedMeshCount++;
                        continue;
                    }

                    candidates.Add(candidate);
                }
            }

            foreach (var batch in CreateBatches(candidates))
                ProcessBatch(state, rootVmdPath, batch);
        }

        private AtlasCandidate? TryCreateCandidate(
            BatchState state,
            MeshKey key,
            RmvModel model,
            List<WsUsage> usages)
        {
            var materialPath = usages[0].MaterialPath;
            var materialFile = FindForRead(state, materialPath);
            if (materialFile == null)
                return null;

            var materialXml = Encoding.UTF8.GetString(materialFile.DataSource.ReadData());
            var materialDoc = new XmlDocument();
            materialDoc.LoadXml(materialXml);

            var shaderPath = materialDoc.SelectSingleNode("/material/shader")?.InnerText ?? string.Empty;
            if (shaderPath.Contains("emissive", StringComparison.OrdinalIgnoreCase))
                return null;

            if (materialDoc.SelectNodes("/material/textures/texture")?
                    .Cast<XmlNode>()
                    .Any(x => GetTextureSlot(x).Contains("emissive", StringComparison.OrdinalIgnoreCase)) == true)
            {
                return null;
            }

            var primaryPath = GetTexturePath(materialDoc, "t_xml_base_colour");
            if (string.IsNullOrWhiteSpace(primaryPath))
                return null;

            var primaryFile = FindForRead(state, primaryPath);
            if (primaryFile == null)
                return null;

            var primaryBytes = primaryFile.DataSource.ReadData();
            var (width, height) = TextureAtlasBuilder.GetDimensions(primaryBytes);

            var channelBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["t_xml_base_colour"] = primaryBytes
            };

            foreach (var channel in AtlasChannels.Skip(1))
            {
                var path = GetTexturePath(materialDoc, channel.Slot);
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                var file = FindForRead(state, path);
                if (file == null)
                    continue;

                var bytes = file.DataSource.ReadData();
                var dimensions = TextureAtlasBuilder.GetDimensions(bytes);
                if (dimensions.Width != width || dimensions.Height != height)
                    return null;

                channelBytes[channel.Slot] = bytes;
            }

            var bounds = GetUvBounds(model);
            return new AtlasCandidate(
                key,
                model,
                usages,
                materialPath,
                materialDoc,
                primaryBytes,
                width,
                height,
                bounds,
                channelBytes);
        }

        private static List<List<AtlasCandidate>> CreateBatches(List<AtlasCandidate> candidates)
        {
            var batches = new List<List<AtlasCandidate>>();
            var current = new List<AtlasCandidate>();

            foreach (var candidate in candidates)
            {
                if (!CanCreatePlan([candidate]))
                    continue;

                if (current.Count == 0)
                {
                    current.Add(candidate);
                    continue;
                }

                var trial = current.Concat([candidate]).ToList();
                if (CanCreatePlan(trial))
                {
                    current.Add(candidate);
                    continue;
                }

                if (current.Count >= 2)
                    batches.Add(current);

                current = [candidate];
            }

            if (current.Count >= 2)
                batches.Add(current);

            return batches;
        }

        private static bool CanCreatePlan(IReadOnlyList<AtlasCandidate> candidates)
        {
            try
            {
                TextureAtlasBuilder.CreatePlanFromDds(ToAtlasSources(candidates));
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private void ProcessBatch(BatchState state, string rootVmdPath, List<AtlasCandidate> candidates)
        {
            var plan = TextureAtlasBuilder.CreatePlanFromDds(ToAtlasSources(candidates));
            var atlasStem = BuildAtlasStem(rootVmdPath, state.BatchIndex++);
            var generatedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var channel in AtlasChannels)
            {
                var textureBytes = new Dictionary<int, byte[]>();
                var omitted = new HashSet<int>();

                for (var i = 0; i < candidates.Count; i++)
                {
                    if (candidates[i].ChannelBytes.TryGetValue(channel.Slot, out var bytes))
                        textureBytes[i] = bytes;
                    else
                        omitted.Add(i);
                }

                if (textureBytes.Count == 0)
                    continue;

                var mipPngs = TextureAtlasBuilder.BuildMipPngs(
                    plan,
                    textureBytes,
                    forceOpaqueAlphaSourceIds: null,
                    omitted);

                var fileName = $"{atlasStem}_{channel.Suffix}.dds";
                var atlasPackFile = PngToDdsImporter.ImportRawMipChain(
                    mipPngs,
                    channel.Type,
                    GameTypeEnum.Warhammer3,
                    fileName);
                var atlasPath = Normalize($@"{AtlasDirectory}\{fileName}");

                WriteFile(state.Output, atlasPath, atlasPackFile.DataSource.ReadData());
                generatedPaths[channel.Slot] = atlasPath;
                state.GeneratedTextureCount++;
            }

            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                var placement = plan.Placements.Single(x => x.Id == i);

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
                    if (!candidate.ChannelBytes.ContainsKey(channel.Slot))
                        continue;
                    if (generatedPaths.TryGetValue(channel.Slot, out var atlasPath))
                        SetTexturePath(clonedMaterial, channel.Slot, atlasPath);
                }

                var newMaterialPath = BuildMaterialPath(candidate.MaterialPath, candidate.Key);
                WriteFile(state.Output, newMaterialPath, Encoding.UTF8.GetBytes(clonedMaterial.OuterXml));

                foreach (var usage in candidate.Usages)
                {
                    usage.MaterialNode.InnerText = newMaterialPath;
                    state.ModifiedWsModels.Add(usage.WsModelPath);
                }

                state.ModifiedRigids.Add(candidate.Key.GeometryPath);
                state.ProcessedMeshes.Add(candidate.Key);
            }
        }

        private void SaveModifiedDocuments(BatchState state)
        {
            foreach (var rigidPath in state.ModifiedRigids)
            {
                var rmv = state.RigidModels[rigidPath];
                rmv.RecalculateOffsets();
                WriteFile(state.Output, rigidPath, ModelFactory.Create().Save(rmv));
            }

            foreach (var wsPath in state.ModifiedWsModels)
            {
                var doc = state.WsDocuments[wsPath];
                WriteFile(state.Output, wsPath, Encoding.UTF8.GetBytes(doc.OuterXml));
            }
        }

        private int PruneUnusedAssetFiles(
            IPackFileContainer output,
            HashSet<string> originalReachable,
            HashSet<string> currentReachable)
        {
            var toRemove = new HashSet<string>(
                originalReachable.Except(currentReachable, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

            // Also remove orphaned model assets from the selected pack. Keep unrelated pack
            // content (DB, scripts, localisation, UI textures, etc.) untouched.
            foreach (var path in output.GetAllFiles().Keys)
            {
                var extension = Path.GetExtension(path);
                if (extension.Equals(".xml.material", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".wsmodel", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".rigid_model_v2", StringComparison.OrdinalIgnoreCase) ||
                    (extension.Equals(".dds", StringComparison.OrdinalIgnoreCase) &&
                     Normalize(path).StartsWith(@"variantmeshes\", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!currentReachable.Contains(Normalize(path)))
                        toRemove.Add(Normalize(path));
                }
            }

            var removed = 0;
            foreach (var path in toRemove)
            {
                var file = output.FindFile(path);
                if (file == null)
                    continue;

                _packFileService.DeleteFile(output, file);
                removed++;
            }

            return removed;
        }

        private HashSet<string> CollectReachableAssetFiles(
            IPackFileContainer container,
            IReadOnlyList<string> rootVmdPaths)
        {
            var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var vmdQueue = new Queue<string>(rootVmdPaths.Select(Normalize));
            var visitedVmds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (vmdQueue.Count > 0)
            {
                var vmdPath = vmdQueue.Dequeue();
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

        private static HashSet<string> CollectReachableWsModels(IPackFileContainer container, string rootVmdPath)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            queue.Enqueue(Normalize(rootVmdPath));

            while (queue.Count > 0)
            {
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
            {
                _packFileService.SaveFile(existing, data);
                return;
            }

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

        private static List<TextureAtlasSource> ToAtlasSources(IReadOnlyList<AtlasCandidate> candidates)
        {
            var sources = new List<TextureAtlasSource>(candidates.Count);
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                sources.Add(new TextureAtlasSource(
                    i,
                    candidate.PrimaryBytes,
                    candidate.Bounds.MinU,
                    candidate.Bounds.MinV,
                    candidate.Bounds.MaxU,
                    candidate.Bounds.MaxV));
            }

            return sources;
        }

        private static bool TryParseIndex(XmlNode node, string attributeName, out int value)
        {
            value = -1;
            var attribute = node.Attributes?[attributeName];
            return attribute != null && int.TryParse(attribute.Value, out value);
        }

        private static string BuildAtlasStem(string rootVmdPath, int batchIndex)
        {
            var stem = SafeName(Path.GetFileNameWithoutExtension(rootVmdPath));
            var hash = StableHash(Normalize(rootVmdPath));
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

        private static string Normalize(string? path)
            => string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : path.Trim().Replace('/', '\\').Replace("\\\\", "\\").ToLowerInvariant();

        public sealed record BatchResult(
            int VmdCount,
            int AtlasedMeshCount,
            int GeneratedTextureCount,
            int RemovedFileCount,
            int SkippedMeshCount);

        private sealed class BatchState
        {
            public IPackFileContainer Source { get; }
            public IPackFileContainer Output { get; }
            public Dictionary<string, XmlDocument> WsDocuments { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, RmvFile> RigidModels { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<MeshKey, List<WsUsage>> Usages { get; } = [];
            public HashSet<MeshKey> ProcessedMeshes { get; } = [];
            public HashSet<string> ModifiedWsModels { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> ModifiedRigids { get; } = new(StringComparer.OrdinalIgnoreCase);
            public int GeneratedTextureCount { get; set; }
            public int SkippedMeshCount { get; set; }
            public int BatchIndex { get; set; }

            public BatchState(IPackFileContainer source, IPackFileContainer output)
            {
                Source = source;
                Output = output;
            }
        }

        private sealed record WsUsage(
            string WsModelPath,
            XmlDocument Document,
            XmlNode MaterialNode,
            string MaterialPath);

        private sealed record AtlasCandidate(
            MeshKey Key,
            RmvModel Model,
            List<WsUsage> Usages,
            string MaterialPath,
            XmlDocument MaterialDocument,
            byte[] PrimaryBytes,
            int Width,
            int Height,
            UvBounds Bounds,
            Dictionary<string, byte[]> ChannelBytes);

        private readonly record struct MeshKey(string GeometryPath, int LodIndex, int PartIndex)
        {
            public override string ToString() => $"{GeometryPath} [lod {LodIndex}, part {PartIndex}]";
        }

        private readonly record struct UvBounds(float MinU, float MinV, float MaxU, float MaxV);
    }
}
