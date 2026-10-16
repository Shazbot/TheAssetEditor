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
using Shared.GameFormats.RigidModel.LodHeader;
using Shared.GameFormats.RigidModel.MaterialHeaders;
using Shared.GameFormats.RigidModel.Types;
using Shared.GameFormats.RigidModel.Vertex;
using Shared.GameFormats.Vmd;
using static Shared.GameFormats.Vmd.VariantMeshDefinition;

namespace Editors.KitbasherEditor.Services
{
    public sealed partial class PackTextureAtlasBatchService
    {
        private void SaveModifiedDocuments(
            BatchState state,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var total =
                state.ModifiedRigids.Count +
                state.ModifiedWsModels.Count +
                state.ModifiedVmdDocuments.Count;
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

            serializeStopwatch.Restart();
            foreach (var (vmdPath, document) in
                     state.ModifiedVmdDocuments
                         .OrderBy(
                             entry => entry.Key,
                             StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(
                    progress,
                    "Serializing modified VMDs",
                    ++current,
                    total,
                    vmdPath);
                replacements.Add(
                    CreateReplacementEntry(
                        vmdPath,
                        Encoding.UTF8.GetBytes(
                            document.OuterXml)));
            }
            AddPhaseDuration(
                state,
                "Serialize modified VMDs",
                serializeStopwatch.Elapsed);

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

            // The first cross-rigid writer deliberately duplicates source geometry instead
            // of claiming source retirement. Keep every source WSModel/RMV participating in
            // an emitted plan even when the rewritten VMD no longer reaches it.
            toRemove.ExceptWith(
                state.CrossRigidRetainedSourceAssetPaths);

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

            // Done before the reference protection: a kept override stays in the pack and its
            // own references must then keep their targets alive.
            KeepVanillaOverrides(state, toRemove);

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

        // Vanilla wsmodels, VMDs and materials can still name a path the mod overrides, so an
        // override that stopped being reachable from the mod's own roots must stay. Every doubt
        // blocks, as in texture compaction: without the vanilla data loaded nothing that could
        // be an override is pruned. Generated atlas files are the tool's own and cannot be one.
        private static void KeepVanillaOverrides(BatchState state, HashSet<string> toRemove)
        {
            var vanillaKnown = GetGameplayTraversalContainers(state).Any(container =>
                container.IsCaPackFile && !ReferenceEquals(container, state.Source));
            foreach (var path in toRemove.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList())
            {
                if (path.StartsWith(AtlasDirectory, StringComparison.OrdinalIgnoreCase) ||
                    !state.Output.ContainsFile(path))
                {
                    continue;
                }

                var reason = !vanillaKnown
                    ? "vanilla game data is not loaded, so an override of a vanilla file cannot be ruled out"
                    : OverridesVanillaPath(state, path)
                        ? "overrides a vanilla file"
                        : null;
                if (reason == null)
                    continue;

                toRemove.Remove(path);
                state.PruneKeptFiles.Add((path, reason));
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
                    state,
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
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress = null)
        {
            var result = new Dictionary<string, HashSet<string>>(
                StringComparer.OrdinalIgnoreCase);
            if (state.ArmyResidencyModel == null)
                return result;

            var rootPaths = state.ArmyResidencyModel.UnitsByVmd.Keys.ToArray();
            ReportProgress(
                progress,
                "Evaluating atlas value",
                0,
                rootPaths.Length,
                "Indexing army asset roots");
            for (var rootIndex = 0; rootIndex < rootPaths.Length; rootIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportPeriodicProgress(
                    progress,
                    "Evaluating atlas value",
                    rootIndex + 1,
                    rootPaths.Length,
                    "Indexing army asset roots");
                var root = rootPaths[rootIndex];

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

        private void ValidateOutput(
            BatchState state,
            IReadOnlyList<string> vmdRoots,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, "Validating output");
            var errors = new List<string>();

            ValidateUnchangedModelPathSet(
                state,
                ".variantmeshdefinition",
                errors,
                state.CrossRigidGeneratedVmdPaths);
            ValidateUnchangedModelPathSet(
                state,
                ".wsmodel",
                errors,
                state.CrossRigidGeneratedWsModelPaths);
            ValidateUnchangedModelPathSet(
                state,
                ".rigid_model_v2",
                errors,
                state.CrossRigidGeneratedRigidPaths);

            var validationVmds = state.ModifiedVmdDocuments.Keys
                .OrderBy(
                    path => path,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
            for (var vmdIndex = 0;
                 vmdIndex < validationVmds.Count;
                 vmdIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var vmdPath = validationVmds[vmdIndex];
                ReportProgress(
                    progress,
                    "Validating modified VMDs",
                    vmdIndex + 1,
                    validationVmds.Count,
                    vmdPath);
                var file = state.Output.FindFile(vmdPath);
                if (file == null)
                {
                    errors.Add(
                        $"Modified VMD is missing from output: {vmdPath}");
                    continue;
                }

                try
                {
                    var vmd = VariantMeshDefinitionLoader.Load(file);
                    var modelRefs = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
                    var childVmdRefs = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
                    var directTextures = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
                    CollectVmdReferences(
                        vmd,
                        modelRefs,
                        childVmdRefs,
                        directTextures);

                    foreach (var generatedReference in modelRefs
                                 .Select(Normalize)
                                 .Where(path =>
                                     state.CrossRigidGeneratedWsModelPaths
                                         .Contains(path))
                                 .Distinct(
                                     StringComparer.OrdinalIgnoreCase))
                    {
                        if (!CanResolveAfterRewrite(
                                state,
                                generatedReference))
                        {
                            errors.Add(
                                $"Cross-rigid generated WSModel no longer resolves: " +
                                $"{vmdPath} -> {generatedReference}");
                        }
                    }

                    foreach (var generatedChildVmd in childVmdRefs
                                 .Select(Normalize)
                                 .Where(path =>
                                     state.CrossRigidGeneratedVmdPaths
                                         .Contains(path))
                                 .Distinct(
                                     StringComparer.OrdinalIgnoreCase))
                    {
                        if (!CanResolveAfterRewrite(
                                state,
                                generatedChildVmd))
                        {
                            errors.Add(
                                $"Cross-rigid generated child VMD no longer resolves: " +
                                $"{vmdPath} -> {generatedChildVmd}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(
                        $"Modified VMD failed to reload: {vmdPath} ({ex.Message})");
                }
            }

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

            ValidateGeneratedMaterials(state, errors, cancellationToken, progress);

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

            ValidateDownscaledTextures(state, errors, cancellationToken);
            ValidateSharedTextureRepack(state, errors, cancellationToken);
            ValidateTextureCompaction(state, errors, cancellationToken);

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
                $"PASS: {state.CrossRigidRewrittenRootVmdPaths.Count} rewritten source VMD(s), " +
                $"{state.CrossRigidGeneratedVmdPaths.Count} generated VMD clone(s), " +
                $"{state.ModifiedRigids.Count} modified/generated rigid(s), " +
                $"{state.ModifiedWsModels.Count} modified/generated WSModel(s), " +
                $"{state.GeneratedMaterialPaths.Count} generated material(s), and " +
                $"{state.GeneratedTexturePaths.Count} atlas texture(s) validated.");
            state.ValidationMessages.Add(
                $"PASS: source VMD/WSModel/RMV2 paths were preserved; " +
                $"{state.CrossRigidGeneratedVmdPaths.Count} generated cross-rigid VMD clone(s), " +
                $"{state.CrossRigidGeneratedWsModelPaths.Count} generated cross-rigid WSModel(s), and " +
                $"{state.CrossRigidGeneratedRigidPaths.Count} generated cross-rigid RMV2 file(s) were validated.");
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

        // The atlas stage's materials and the shared texture repack's: each must exist, parse and
        // only name textures that still resolve. A repack material that is missing or invalid is
        // already reported by ValidateSharedTextureRepack, so only its textures are checked here.
        private void ValidateGeneratedMaterials(
            BatchState state,
            List<string> errors,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var validationMaterials = state.GeneratedMaterialPaths
                .Select(path => (Path: path, Repack: false))
                .Concat(state.SharedRepackMaterialPaths.Select(path => (Path: path, Repack: true)))
                .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            for (var materialIndex = 0; materialIndex < validationMaterials.Count; materialIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (materialPath, repack) = validationMaterials[materialIndex];
                var label = repack ? "repack" : "atlas";
                ReportProgress(
                    progress,
                    "Validating generated materials",
                    materialIndex + 1,
                    validationMaterials.Count,
                    materialPath);
                var file = state.Output.FindFile(materialPath);
                if (file == null)
                {
                    if (!repack)
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
                    if (!repack)
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
                            repack
                                ? $"Generated repack material texture no longer resolves: {materialPath} -> {texturePath}"
                                : $"Generated material texture no longer resolves: {materialPath} -> {texturePath}");
                    }
                }
            }
        }

        private static void ValidateUnchangedModelPathSet(
            BatchState state,
            string extension,
            List<string> errors,
            IReadOnlySet<string>? expectedAddedPaths = null)
        {
            var sourcePaths = state.Source.GetAllFiles().Keys
                .Where(x => Path.GetExtension(x).Equals(extension, StringComparison.OrdinalIgnoreCase))
                .Select(Normalize)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var outputPaths = state.Output.GetAllFiles().Keys
                .Where(x => Path.GetExtension(x).Equals(extension, StringComparison.OrdinalIgnoreCase))
                .Select(Normalize)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var expectedAdditions = expectedAddedPaths == null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : expectedAddedPaths
                    .Select(Normalize)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var missing in sourcePaths.Except(outputPaths, StringComparer.OrdinalIgnoreCase))
                errors.Add($"{extension} path was removed instead of rewritten in place: {missing}");

            var actualAdditions = outputPaths
                .Except(sourcePaths, StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var added in actualAdditions
                         .Except(expectedAdditions, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"Unexpected new {extension} path was created: {added}");
            }

            foreach (var expected in expectedAdditions
                         .Except(actualAdditions, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"Expected generated {extension} path is missing: {expected}");
            }
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
    }
}
