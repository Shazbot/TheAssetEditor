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

namespace Editors.PackOptimizer.Core
{
    public sealed partial class PackTextureAtlasBatchService
    {
        private void MergeDirectRigidModels(
            BatchState state,
            string rigidPath,
            RmvFile rmv,
            CancellationToken cancellationToken)
        {
            var rigidBefore = rmv.ModelList.Sum(x => x.Length);
            var rigidAfter = rigidBefore;
            var rigidChanged = false;

            for (var lodIndex = 0; lodIndex < rmv.ModelList.Length; lodIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var originalModels = rmv.ModelList[lodIndex];
                if (originalModels.Length < 2)
                    continue;

                var groups = BuildDirectMeshMergeGroups(
                    originalModels,
                    lodIndex,
                    rigidPath,
                    state);
                if (groups.All(group => group.PartIndices.Count == 1))
                    continue;

                var lodInvariant = CaptureLodGeometryInvariant(
                    originalModels,
                    rigidPath,
                    lodIndex);
                var mergedModels = new List<RmvModel>(groups.Count);
                for (var newPartIndex = 0; newPartIndex < groups.Count; newPartIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var group = groups[newPartIndex];
                    var models = group.PartIndices
                        .Select(partIndex => originalModels[partIndex])
                        .ToList();
                    MergeGeometryInvariantSnapshot? groupInvariant = null;
                    if (models.Count > 1)
                    {
                        groupInvariant = CaptureMergeGroupInvariant(
                            models,
                            rigidPath,
                            lodIndex,
                            group.PartIndices);
                    }

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
                        var mergeOrigin = GetMeshMergeOrigin(
                            state,
                            rigidPath,
                            lodIndex,
                            group.PartIndices);
                        RecordMeshMergeOrigin(
                            state,
                            mergeOrigin,
                            models.Count - 1);

                        state.MeshMergeEntries.Add(new MeshMergeReportEntry(
                            rigidPath,
                            lodIndex,
                            group.PartIndices.ToArray(),
                            newPartIndex,
                            merged.Mesh.VertexList.Length,
                            BuildEmbeddedMaterialPath(
                                rigidPath,
                                lodIndex,
                                group.PartIndices[0]),
                            mergeOrigin));
                    }
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
            if (rigidChanged)
                state.ModifiedRigids.Add(rigidPath);
        }

        private static List<MeshMergeGroup> BuildDirectMeshMergeGroups(
            IReadOnlyList<RmvModel> models,
            int lodIndex,
            string rigidPath,
            BatchState? state = null)
        {
            var buckets = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (var partIndex = 0; partIndex < models.Count; partIndex++)
            {
                var identity = string.Join(
                    "\u001e",
                    GetAtlasBatchMergeBoundaryIdentity(
                        state,
                        new MeshKey(rigidPath, lodIndex, partIndex)),
                    GetDirectRmvMergeIdentity(models[partIndex]));
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
                            groups.Add(CreateDirectMeshMergeGroup(
                                current,
                                rigidPath,
                                lodIndex));
                            current = [];
                            currentVertexCount = 0;
                        }

                        groups.Add(CreateDirectMeshMergeGroup(
                            [partIndex],
                            rigidPath,
                            lodIndex));
                        continue;
                    }

                    if (current.Count != 0 &&
                        currentVertexCount + vertexCount > ushort.MaxValue)
                    {
                        groups.Add(CreateDirectMeshMergeGroup(
                            current,
                            rigidPath,
                            lodIndex));
                        current = [];
                        currentVertexCount = 0;
                    }

                    current.Add(partIndex);
                    currentVertexCount += vertexCount;
                }

                if (current.Count != 0)
                {
                    groups.Add(CreateDirectMeshMergeGroup(
                        current,
                        rigidPath,
                        lodIndex));
                }
            }

            return groups
                .OrderBy(group => group.PartIndices.Min())
                .ToList();
        }

        private static MeshMergeGroup CreateDirectMeshMergeGroup(
            IReadOnlyList<int> partIndices,
            string rigidPath,
            int lodIndex)
            => new(
                partIndices.ToList(),
                [BuildEmbeddedMaterialPath(rigidPath, lodIndex, partIndices[0])]);

        private static string GetDirectRmvMergeIdentity(RmvModel model)
        {
            var materialBytes = MaterialFactory.Create().Save(
                model.CommonHeader.ModelTypeFlag,
                model.Material);
            var identity = string.Join(
                "|",
                model.CommonHeader.ModelTypeFlag,
                model.CommonHeader.RenderFlag,
                model.Material.BinaryVertexFormat,
                model.CommonHeader.ShaderParams.ShaderName,
                Convert.ToHexString(materialBytes));
            return ContentHash(identity);
        }

        private void MergeCompatibleMeshes(
            BatchState state,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var rigidPaths = LoadStructuralMergeRigidModels(state, cancellationToken)
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

                if (TryGetStructuralMergeBlockReason(state, rigidPath, out _))
                    continue;

                if (!state.RigidModels.TryGetValue(rigidPath, out var rmv))
                    continue;

                var wsModels = GetWritableWsModelsForRigid(state, rigidPath);

                if (wsModels.Count == 0)
                {
                    MergeDirectRigidModels(
                        state,
                        rigidPath,
                        rmv,
                        cancellationToken);
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
                        assignmentsByWsModel,
                        includeEmbeddedMaterialIdentity:
                            HasDirectGameplayRigidConsumer(state, rigidPath));

                    var groups = BuildMeshMergeGroups(
                        state,
                        originalModels,
                        lodIndex,
                        rigidPath,
                        wsModels.Select(x => x.Key).ToList(),
                        assignmentsByWsModel,
                        includeEmbeddedMaterialIdentity:
                            HasDirectGameplayRigidConsumer(state, rigidPath));

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
                            var mergeOrigin = GetMeshMergeOrigin(
                                state,
                                rigidPath,
                                lodIndex,
                                group.PartIndices);
                            RecordMeshMergeOrigin(
                                state,
                                mergeOrigin,
                                models.Count - 1);

                            state.MeshMergeEntries.Add(new MeshMergeReportEntry(
                                rigidPath,
                                lodIndex,
                                group.PartIndices.ToArray(),
                                newPartIndex,
                                merged.Mesh.VertexList.Length,
                                group.MaterialPathsByWsModel[0],
                                mergeOrigin));
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

                state.ModifiedRigids.Add(rigidPath);
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
            IProgress<TextureAtlasPackProgress>? progress,
            IEnumerable<string>? additionalRigidPaths = null)
        {
            // Rigids rewritten by atlasing or merging are always optimized. The
            // no-atlas run also passes its own selection of otherwise unmodified
            // rigids; those only become written files once optimized.
            var rigidPaths = state.ModifiedRigids
                .Concat(additionalRigidPaths ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var vertexFactory = VertexFactory.Create();

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
                        var vertexSize = (long)vertexFactory.GetVertexSize(
                            model.Material.BinaryVertexFormat,
                            rmv.Header.Version);
                        var statistics = RigidGeometryOptimizer.Optimize(model);
                        state.GeometryMeshesOptimized++;
                        state.GeometryVerticesBefore += statistics.VerticesBefore;
                        state.GeometryVerticesAfter += statistics.VerticesAfter;
                        state.GeometryUnreferencedVerticesRemoved += statistics.UnreferencedVerticesRemoved;
                        state.GeometryDuplicateVerticesRemoved += statistics.DuplicateVerticesRemoved;
                        state.GeometryIndicesBefore += statistics.IndicesBefore;
                        state.GeometryIndicesAfter += statistics.IndicesAfter;
                        state.GeometryBytesBefore +=
                            vertexSize * statistics.VerticesBefore +
                            sizeof(ushort) * (long)statistics.IndicesBefore;
                        state.GeometryBytesAfter +=
                            vertexSize * statistics.VerticesAfter +
                            sizeof(ushort) * (long)statistics.IndicesAfter;
                    }
                }

                state.GeometryRigidsOptimized++;
                state.ModifiedRigids.Add(rigidPath);
            }
        }

        // The no-atlas run optimizes more than the rigids a merge rewrote. A rigid
        // qualifies when the source pack owns it (vanilla/CA rigids are never
        // written), a root in scope reaches it, and the same consumer-safety gate
        // the structural merge uses passes: complete consumer discovery and no
        // immutable (vanilla) WSModel consumer. The optimizer keeps the part count,
        // part order and triangle count of every model, so WSModel material
        // indices and direct VMD references stay valid; it only reorders and
        // deduplicates vertices inside a part. Cloth model types are left alone
        // because nothing here shows that their vertex order is free to change,
        // and a rigid whose index buffers the optimizer would reject is skipped
        // instead of failing the run.
        private HashSet<string> SelectNoAtlasGeometryOptimizationRigids(
            BatchState state,
            IReadOnlyCollection<string> scopeVmdRoots,
            CancellationToken cancellationToken)
        {
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var index = state.GameplayMeshDependencyIndex;
            if (index == null)
                return selected;

            var scope = scopeVmdRoots
                .Select(Normalize)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            void Skip(string reason)
            {
                state.GeometrySelectionSkipCounts[reason] =
                    state.GeometrySelectionSkipCounts.GetValueOrDefault(reason) + 1;
            }

            foreach (var (assetPath, roots) in index.RootsByAssetPath
                         .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rigidPath = Normalize(assetPath);
                if (!Path.GetExtension(rigidPath).Equals(
                        ".rigid_model_v2",
                        StringComparison.OrdinalIgnoreCase) ||
                    !state.Source.ContainsFile(rigidPath) ||
                    !roots.Any(scope.Contains))
                {
                    continue;
                }

                if (!IsGameplayConsumerDiscoverySafeForAsset(state, rigidPath))
                {
                    Skip("consumer discovery incomplete");
                    continue;
                }

                if (GetImmutableGameplayWsModelConsumersForRigid(state, rigidPath).Count != 0)
                {
                    Skip("immutable gameplay WSModel consumer");
                    continue;
                }

                RmvFile? rmv;
                try
                {
                    rmv = GetRmv(state, rigidPath);
                }
                catch (Exception ex) when (
                    ex is InvalidDataException or
                    InvalidOperationException or
                    ArgumentException or
                    FormatException)
                {
                    Skip("rigid could not be loaded");
                    continue;
                }

                if (rmv == null)
                {
                    Skip("rigid could not be loaded");
                    continue;
                }

                var models = rmv.ModelList.SelectMany(lod => lod).ToList();
                if (models.Any(model =>
                        model.CommonHeader.ModelTypeFlag is
                            ModelMaterialEnum.cloth or
                            ModelMaterialEnum.weighted_cloth))
                {
                    Skip("cloth model type");
                    continue;
                }

                if (models.Any(model =>
                        model.Mesh.IndexList.Length % 3 != 0 ||
                        model.Mesh.IndexList.Any(
                            vertexIndex => vertexIndex >= model.Mesh.VertexList.Length)))
                {
                    Skip("invalid index buffer");
                    continue;
                }

                if (!state.ModifiedRigids.Contains(rigidPath))
                    state.GeometryUnmodifiedRigidsSelected++;
                selected.Add(rigidPath);
            }

            return selected;
        }

        private static void AnalyzeMeshMergeBlockers(
            BatchState state,
            string rigidPath,
            int lodIndex,
            IReadOnlyList<RmvModel> models,
            IReadOnlyList<string> wsModelPaths,
            IReadOnlyDictionary<string, string[][]> assignmentsByWsModel,
            bool includeEmbeddedMaterialIdentity)
        {
            var rmvComponents = models
                .Select(model => GetRmvMergeDiagnosticComponents(
                    model,
                    includeEmbeddedMaterialIdentity))
                .ToArray();

            var exactBuckets = Enumerable.Range(0, models.Count)
                .GroupBy(partIndex => string.Join(
                    "\u001e",
                    GetRmvMergeIdentityForMerge(
                        models[partIndex],
                        includeEmbeddedMaterialIdentity),
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

                    if (rmvDifferences.Count == 0 &&
                        materialComparison.SemanticallyEquivalent &&
                        TryGetLogicalAtlasBatchBoundary(
                            state,
                            rigidPath,
                            lodIndex,
                            leftIndex,
                            rightIndex,
                            out var atlasBatchBoundaryDetail))
                    {
                        RecordMeshMergeBlocker(
                            state,
                            "Logical atlas batch boundary",
                            BuildMeshMergeBlockerExample(
                                rigidPath,
                                lodIndex,
                                leftIndex,
                                rightIndex,
                                atlasBatchBoundaryDetail));
                        continue;
                    }

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
                            AnalyzeCounterfactualTexturePair(
                                state,
                                rigidPath,
                                lodIndex,
                                leftIndex,
                                rightIndex,
                                materialComparison);
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

        private static void AnalyzeCounterfactualTexturePair(
            BatchState state,
            string rigidPath,
            int lodIndex,
            int leftPartIndex,
            int rightPartIndex,
            MaterialMergeDiagnosticComparison materialComparison)
        {
            var left = GetMaterialMergeDiagnosticSnapshot(
                state,
                materialComparison.LeftMaterialPath);
            var right = GetMaterialMergeDiagnosticSnapshot(
                state,
                materialComparison.RightMaterialPath);
            if (left == null || right == null)
                return;

            var differingSlots = GetDifferingMaterialTextureSlots(
                    state,
                    left,
                    right)
                .ToList();
            if (differingSlots.Count == 0)
                return;

            state.CounterfactualTextureOnlyPairsEvaluated++;
            var classification = ClassifyCounterfactualTexturePair(
                state,
                left,
                right,
                differingSlots,
                out var eligibilityDetail);

            if (classification.Equals(
                    "Atlasable differing texture channels",
                    StringComparison.Ordinal))
            {
                state.CounterfactualTextureOnlyPairsAtlasable++;
            }
            else if (classification.Equals(
                         "Uniform-constant equivalence",
                         StringComparison.Ordinal))
            {
                state.CounterfactualTextureOnlyPairsCanonicalizable++;
            }

            IncrementDiagnosticCount(
                state.CounterfactualTexturePairCounts,
                classification);

            var example = BuildMeshMergeBlockerExample(
                rigidPath,
                lodIndex,
                leftPartIndex,
                rightPartIndex,
                $"slots [{string.Join(", ", differingSlots.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}]; " +
                $"materials {materialComparison.LeftMaterialPath} <> " +
                $"{materialComparison.RightMaterialPath}; {eligibilityDetail}");
            AddDiagnosticExample(
                state.CounterfactualTexturePairExamples,
                classification,
                example);
        }

        private static string ClassifyCounterfactualTexturePair(
            BatchState state,
            MaterialMergeDiagnosticSnapshot left,
            MaterialMergeDiagnosticSnapshot right,
            IReadOnlyList<string> differingSlots,
            out string detail,
            bool allowDifferentUniformMaskConstants = false)
        {
            detail = string.Empty;

            if (left.Shader.Contains("emissive", StringComparison.OrdinalIgnoreCase) ||
                right.Shader.Contains("emissive", StringComparison.OrdinalIgnoreCase) ||
                left.TextureAssignments.Keys.Any(slot =>
                    slot.Contains("emissive", StringComparison.OrdinalIgnoreCase)) ||
                right.TextureAssignments.Keys.Any(slot =>
                    slot.Contains("emissive", StringComparison.OrdinalIgnoreCase)))
            {
                detail = "emissive or UV0-dependent material content";
                return "Material is not safely atlasable";
            }

            if (differingSlots.Any(slot => !IsAtlasChannelSlot(slot)))
            {
                detail = "at least one differing texture slot is outside the atlas channel set";
                return "Non-atlas texture slot";
            }

            if (!TryValidateCounterfactualMaterialTextureSet(
                    state,
                    left,
                    out var materialLeftFailure))
            {
                detail = materialLeftFailure ??
                    "one material has an unusable texture source";
                return "Material has an unresolved or unusable texture source";
            }

            if (!TryValidateCounterfactualMaterialTextureSet(
                    state,
                    right,
                    out var materialRightFailure))
            {
                detail = materialRightFailure ??
                    "one material has an unusable texture source";
                return "Material has an unresolved or unusable texture source";
            }

            var onlyUniformConstants = true;
            foreach (var slot in differingSlots)
            {
                left.TextureAssignments.TryGetValue(slot, out var leftPathValue);
                right.TextureAssignments.TryGetValue(slot, out var rightPathValue);
                var leftPath = NormalizeCounterfactualTextureAssignment(
                    state,
                    slot,
                    leftPathValue);
                var rightPath = NormalizeCounterfactualTextureAssignment(
                    state,
                    slot,
                    rightPathValue);

                // An unresolved test mask is removed by the real atlas rewrite. If one
                // side has that ignored mask and the other side has a real source, the
                // materials still have different effective channel sets and cannot be
                // treated as a texture-only merge.
                if (leftPath.Length == 0 || rightPath.Length == 0)
                {
                    detail =
                        $"{slot} has an ignored unresolved source on one material and a real source on the other";
                    return "Differing texture source is not atlasable";
                }

                if (!TryGetCounterfactualTextureSource(
                        state,
                        leftPath,
                        out var leftSource,
                        out var textureLeftFailure))
                {
                    detail = textureLeftFailure ??
                        $"{slot} has an unusable texture source";
                    return "Differing texture source is not atlasable";
                }

                if (!TryGetCounterfactualTextureSource(
                        state,
                        rightPath,
                        out var rightSource,
                        out var textureRightFailure))
                {
                    detail = textureRightFailure ??
                        $"{slot} has an unusable texture source";
                    return "Differing texture source is not atlasable";
                }

                var leftIsUniform = leftSource.IsUniformConstant;
                var rightIsUniform = rightSource.IsUniformConstant;
                if (leftIsUniform && rightIsUniform)
                {
                    if (!leftSource.ConstantColor.Equals(rightSource.ConstantColor))
                    {
                        if (allowDifferentUniformMaskConstants &&
                            slot.Equals("t_xml_mask", StringComparison.OrdinalIgnoreCase))
                        {
                            // Canonical faction-3 masks are deliberately packed as regions,
                            // even when both source regions happen to be uniform. A black
                            // synthesized region and a non-black uniform region therefore
                            // remain mergeable without forcing either source path to change.
                            onlyUniformConstants = false;
                            continue;
                        }

                        detail =
                            $"{slot} contains different uniform colors and cannot share one source path";
                        return "Different uniform constants";
                    }

                    continue;
                }

                onlyUniformConstants = false;
            }

            if (onlyUniformConstants)
            {
                detail = "all differing channels have the same uniform color; path canonicalization is sufficient";
                return "Uniform-constant equivalence";
            }

            detail = "all differing slots are atlas channels with readable source textures";
            return "Atlasable differing texture channels";
        }

        private static bool TryValidateCounterfactualMaterialTextureSet(
            BatchState state,
            MaterialMergeDiagnosticSnapshot material,
            out string? failure)
        {
            failure = null;
            foreach (var channel in AtlasChannels)
            {
                var hasPath = material.TextureAssignments.TryGetValue(
                    channel.Slot,
                    out var path);
                if (!hasPath || string.IsNullOrWhiteSpace(path))
                {
                    if (channel.Slot.Equals(
                            "t_xml_base_colour",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        failure = "base-colour texture assignment is missing";
                        return false;
                    }

                    continue;
                }

                if (IsTexturePlaceholder(path))
                {
                    if (channel.Slot.Equals(
                            "t_xml_base_colour",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        failure = "base-colour texture assignment is a placeholder";
                        return false;
                    }

                    continue;
                }

                if (!TryGetCounterfactualTextureSource(
                        state,
                        path,
                        out _,
                        out var sourceFailure))
                {
                    if (channel.Slot.Equals(
                            "t_xml_mask",
                            StringComparison.OrdinalIgnoreCase) &&
                        IsIgnorableUnresolvedAtlasTexture(channel.Slot, path))
                    {
                        continue;
                    }

                    failure = $"{channel.Slot}: {sourceFailure}";
                    return false;
                }
            }

            return true;
        }

        private static bool TryGetCounterfactualTextureSource(
            BatchState state,
            string? texturePath,
            out TextureInspection source,
            out string failure)
        {
            source = default;
            failure = string.Empty;
            var normalizedPath = Normalize(texturePath);
            if (normalizedPath.Length == 0)
            {
                failure = "texture assignment is missing";
                return false;
            }

            if (IsTexturePlaceholder(normalizedPath))
            {
                failure = "texture assignment is a placeholder";
                return false;
            }

            // A synthesized mask slot intentionally uses the game-independent black
            // semantic. It remains a valid uniform source even when the common texture is
            // not present in the pack containers currently loaded by the planner.
            if (normalizedPath.Equals(
                    CanonicalBlackMaskTexturePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                source = new TextureInspection(1, 1, true, ZeroMaskColor);
                return true;
            }

            if (state.GeneratedTexturePaths.Contains(normalizedPath))
            {
                // Generated atlases have already passed the atlas writer and can be treated as
                // non-uniform sources for this diagnostic. Their precise source composition is
                // irrelevant to the counterfactual classification.
                source = new TextureInspection(0, 0, false, default);
                return true;
            }

            var file = FindForReadStatic(state, normalizedPath);
            if (file == null)
            {
                failure = $"source texture could not be resolved: {normalizedPath}";
                return false;
            }

            try
            {
                source = GetTextureInspection(state, normalizedPath, file);
                return true;
            }
            catch (Exception ex)
            {
                failure = $"source texture could not be inspected: {normalizedPath} ({ex.Message})";
                return false;
            }
        }

        private static bool IsAtlasChannelSlot(string slot)
            => AtlasChannels.Any(channel =>
                channel.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase));

        private static IEnumerable<string> GetDifferingMaterialTextureSlots(
            BatchState state,
            MaterialMergeDiagnosticSnapshot left,
            MaterialMergeDiagnosticSnapshot right)
            => left.TextureAssignments.Keys
                .Union(right.TextureAssignments.Keys, StringComparer.OrdinalIgnoreCase)
                .Where(slot =>
                {
                    left.TextureAssignments.TryGetValue(slot, out var leftPath);
                    right.TextureAssignments.TryGetValue(slot, out var rightPath);
                    return !NormalizeCounterfactualTextureAssignment(
                            state,
                            slot,
                            leftPath)
                        .Equals(
                        NormalizeCounterfactualTextureAssignment(
                            state,
                            slot,
                            rightPath),
                        StringComparison.OrdinalIgnoreCase);
                });

        private static string NormalizeCounterfactualTextureAssignment(
            BatchState state,
            string slot,
            string? path)
        {
            var normalized = Normalize(path);
            if (normalized.Length != 0 &&
                IsIgnorableUnresolvedAtlasTexture(slot, normalized) &&
                FindForReadStatic(state, normalized) == null)
            {
                // This matches candidate discovery and ProcessBatch: unresolved test_mask
                // is an absent channel, not a texture source that needs to be atlas-packed.
                return string.Empty;
            }

            return normalized;
        }

        private static void AddDiagnosticExample(
            Dictionary<string, List<string>> examplesByCategory,
            string category,
            string example)
        {
            if (!examplesByCategory.TryGetValue(category, out var examples))
            {
                examples = [];
                examplesByCategory[category] = examples;
            }

            const int maxExamplesPerCategory = 8;
            if (examples.Count < maxExamplesPerCategory &&
                !examples.Contains(example, StringComparer.Ordinal))
            {
                examples.Add(example);
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
            string materialPath,
            XmlDocument? materialOverride = null)
        {
            materialPath = Normalize(materialPath);
            if (materialOverride == null &&
                state.MeshMergeMaterialDiagnostics.TryGetValue(materialPath, out var cached))
                return cached;

            try
            {
                XmlDocument material;
                if (materialOverride != null)
                {
                    material = materialOverride;
                }
                else
                {
                    var file = FindForReadStatic(state, materialPath);
                    if (file == null)
                    {
                        state.MeshMergeMaterialDiagnostics[materialPath] = null;
                        return null;
                    }

                    material = GetMaterialDocument(state, materialPath, file);
                }

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
                NormalizeMaterialResourcePaths(nonTexture);

                var snapshot = new MaterialMergeDiagnosticSnapshot(
                    GetMaterialRenderingIdentity(material),
                    shader,
                    textureIdentity,
                    nonTexture.OuterXml,
                    textureAssignments,
                    BuildMaterialLeafFieldMap(nonTexture));
                if (materialOverride == null)
                    state.MeshMergeMaterialDiagnostics[materialPath] = snapshot;
                return snapshot;
            }
            catch
            {
                if (materialOverride == null)
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

        private static RmvMergeDiagnosticComponents GetRmvMergeDiagnosticComponents(
            RmvModel model,
            bool includeEmbeddedMaterialIdentity = false)
        {
            var material = model.Material.Clone();
            if (!includeEmbeddedMaterialIdentity && material is WeightedMaterial weighted)
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

        private static string GetCrossRigidSourcePartMergeIdentity(
            BatchState state,
            string rigidPath,
            int lodIndex,
            int partIndex,
            RmvModel model,
            string materialPath)
            => GetCrossRigidSourcePartIdentity(
                state,
                rigidPath,
                lodIndex,
                partIndex,
                model,
                materialPath).Identity;

        private static CrossRigidPartIdentity GetCrossRigidSourcePartIdentity(
            BatchState state,
            string rigidPath,
            int lodIndex,
            int partIndex,
            RmvModel model,
            string materialPath)
        {
            // The same source part is inspected repeatedly across attachment
            // alternatives, LOD states and VMD roots. RMV identity serializes
            // embedded material metadata; material identity may parse XML.
            // Both depend only on the source part / material during the
            // cross-rigid analysis and emission stages.
            var meshKey = new MeshKey(
                Normalize(rigidPath),
                lodIndex,
                partIndex);
            if (!state.CrossRigidSourceMeshMergeIdentityCache.TryGetValue(
                    meshKey,
                    out var modelIdentity))
            {
                modelIdentity = GetRmvMergeIdentityForMerge(
                    model,
                    includeEmbeddedMaterialIdentity: false);
                state.CrossRigidSourceMeshMergeIdentityCache[meshKey] =
                    modelIdentity;
            }

            var normalizedMaterialPath = Normalize(materialPath);
            if (!state.CrossRigidMaterialMergeIdentityCache.TryGetValue(
                    normalizedMaterialPath,
                    out var materialIdentity))
            {
                materialIdentity = GetMeshMergeMaterialIdentity(
                    state,
                    normalizedMaterialPath);
                state.CrossRigidMaterialMergeIdentityCache[
                    normalizedMaterialPath] = materialIdentity;
            }

            // Evaluate the logical atlas boundary on each use: it may vary
            // between physical mesh states and is not covered by either cache.
            var boundaryIdentity = GetAtlasBatchMergeBoundaryIdentity(
                state,
                meshKey);
            var identityKey = (meshKey, normalizedMaterialPath, boundaryIdentity);
            if (!state.CrossRigidSourcePartIdentityCache.TryGetValue(
                    identityKey,
                    out var partIdentity))
            {
                partIdentity = new CrossRigidPartIdentity(
                    string.Join(
                        "\u001e",
                        boundaryIdentity,
                        modelIdentity,
                        materialIdentity));
                state.CrossRigidSourcePartIdentityCache[identityKey] =
                    partIdentity;
            }

            return partIdentity;
        }

        private static string GetRmvMergeIdentityForMerge(
            RmvModel model,
            bool includeEmbeddedMaterialIdentity)
            => includeEmbeddedMaterialIdentity
                ? GetDirectRmvMergeIdentity(model)
                : GetRmvMergeIdentity(model);

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

        private static string BuildStructuralMergePairKey(
            string rigidPath,
            int lodIndex,
            int leftPartIndex,
            int rightPartIndex)
        {
            var first = Math.Min(leftPartIndex, rightPartIndex);
            var second = Math.Max(leftPartIndex, rightPartIndex);
            return $"{Normalize(rigidPath)}|{lodIndex}|{first}|{second}";
        }

        private static bool IsPreAtlasStructuralMergeGroup(
            BatchState state,
            string rigidPath,
            int lodIndex,
            IReadOnlyList<int> partIndices)
        {
            if (partIndices.Count < 2)
                return false;

            for (var left = 0; left < partIndices.Count; left++)
            {
                for (var right = left + 1;
                     right < partIndices.Count;
                     right++)
                {
                    if (!state.PreAtlasStructuralMergePairs.Contains(
                            BuildStructuralMergePairKey(
                                rigidPath,
                                lodIndex,
                                partIndices[left],
                                partIndices[right])))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static string GetMeshMergeOrigin(
            BatchState state,
            string rigidPath,
            int lodIndex,
            IReadOnlyList<int> partIndices)
        {
            if (IsPreAtlasStructuralMergeGroup(
                    state,
                    rigidPath,
                    lodIndex,
                    partIndices))
            {
                return "structural-only";
            }

            var hasPreExistingPair = false;
            for (var left = 0; left < partIndices.Count && !hasPreExistingPair; left++)
            {
                for (var right = left + 1;
                     right < partIndices.Count;
                     right++)
                {
                    if (state.PreAtlasStructuralMergePairs.Contains(
                            BuildStructuralMergePairKey(
                                rigidPath,
                                lodIndex,
                                partIndices[left],
                                partIndices[right])))
                    {
                        hasPreExistingPair = true;
                        break;
                    }
                }
            }

            return hasPreExistingPair
                ? "structural+atlas-assisted"
                : "atlas-assisted";
        }

        private static void RecordMeshMergeOrigin(
            BatchState state,
            string mergeOrigin,
            int partsRemoved)
        {
            if (mergeOrigin.Equals("structural-only", StringComparison.Ordinal))
            {
                state.StructuralOnlyMergeGroupCount++;
                state.StructuralOnlyMergePartsRemoved += partsRemoved;
            }
            else if (mergeOrigin.Equals(
                         "structural+atlas-assisted",
                         StringComparison.Ordinal))
            {
                state.StructuralAndAtlasAssistedMergeGroupCount++;
                state.StructuralAndAtlasAssistedMergePartsRemoved += partsRemoved;
            }
            else
            {
                state.AtlasAssistedMergeGroupCount++;
                state.AtlasAssistedMergePartsRemoved += partsRemoved;
            }
        }

        private static string[] GetPreExistingStructuralMergePairs(
            BatchState state,
            IReadOnlyList<MeshKey> meshes)
        {
            if (meshes.Count < 2)
                return [];

            var pairs = new List<string>();
            for (var left = 0; left < meshes.Count; left++)
            {
                for (var right = left + 1; right < meshes.Count; right++)
                {
                    var pair = BuildStructuralMergePairKey(
                        meshes[left].GeometryPath,
                        meshes[left].LodIndex,
                        meshes[left].PartIndex,
                        meshes[right].PartIndex);
                    if (state.PreAtlasStructuralMergePairs.Contains(pair))
                        pairs.Add(pair);
                }
            }

            return pairs.ToArray();
        }

        private static string[] FilterPreExistingStructuralMergePairs(
            MergeAffinityGroup group,
            IReadOnlyList<MeshKey> meshes)
        {
            if (meshes.Count < 2 || group.PreExistingStructuralMergePairs.Length == 0)
                return [];

            var existingPairs = group.PreExistingStructuralMergePairs
                .ToHashSet(StringComparer.Ordinal);
            var pairs = new List<string>();
            for (var left = 0; left < meshes.Count; left++)
            {
                for (var right = left + 1; right < meshes.Count; right++)
                {
                    var pair = BuildStructuralMergePairKey(
                        meshes[left].GeometryPath,
                        meshes[left].LodIndex,
                        meshes[left].PartIndex,
                        meshes[right].PartIndex);
                    if (existingPairs.Contains(pair))
                        pairs.Add(pair);
                }
            }

            return pairs.ToArray();
        }

        private static List<MeshMergeGroup> BuildMeshMergeGroups(
            BatchState state,
            IReadOnlyList<RmvModel> models,
            int lodIndex,
            string rigidPath,
            IReadOnlyList<string> wsModelPaths,
            IReadOnlyDictionary<string, string[][]> assignmentsByWsModel,
            bool recordDiagnostics = true,
            bool includeEmbeddedMaterialIdentity = false)
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
                    GetAtlasBatchMergeBoundaryIdentity(
                        state,
                        new MeshKey(rigidPath, lodIndex, partIndex)),
                    GetRmvMergeIdentityForMerge(
                        models[partIndex],
                        includeEmbeddedMaterialIdentity),
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
                if (recordDiagnostics)
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
            // Parts merge when their materials render identically, and the merged part keeps one
            // of the material paths. A material that is a vanilla file another mod replaces is not
            // what the game will render, so it only merges with parts using the very same path.
            if (state.Consumers?.TryBlockVanillaReplaced(normalizedPath, "mesh merge by material content") == true)
                return $"path:{normalizedPath}";

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

        private static string GetAtlasBatchMergeBoundaryIdentity(
            BatchState? state,
            MeshKey meshKey)
        {
            if (state != null &&
                state.AtlasBatchByMesh.TryGetValue(meshKey, out var atlasBatchId))
            {
                return $"atlas-batch:{atlasBatchId}";
            }

            return "atlas-batch:none";
        }

        private static bool TryGetLogicalAtlasBatchBoundary(
            BatchState state,
            string rigidPath,
            int lodIndex,
            int leftPartIndex,
            int rightPartIndex,
            out string detail)
        {
            detail = string.Empty;
            if (!state.AtlasBatchByMesh.TryGetValue(
                    new MeshKey(rigidPath, lodIndex, leftPartIndex),
                    out var leftBatchId) ||
                !state.AtlasBatchByMesh.TryGetValue(
                    new MeshKey(rigidPath, lodIndex, rightPartIndex),
                    out var rightBatchId) ||
                leftBatchId == rightBatchId)
            {
                return false;
            }

            detail =
                $"logical atlas batches {leftBatchId} and {rightBatchId} remain separate";
            return true;
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

        // MergeRmvModels returns the source model itself for a single part and
        // shares CommonVertex instances (a mutable class) for merged parts.
        // Generated cross-rigid rigids are later modified in place (UV
        // rewrites, geometry optimization), so they must own their data.
        private static RmvModel MergeRmvModelsOwned(
            IReadOnlyList<RmvModel> models)
        {
            var merged = MergeRmvModels(models);
            var aliasesSource = ReferenceEquals(merged, models[0]);
            return new RmvModel
            {
                CommonHeader = merged.CommonHeader,
                Material = aliasesSource
                    ? merged.Material.Clone()
                    : merged.Material,
                Mesh = new RmvMesh
                {
                    VertexList = merged.Mesh.VertexList
                        .Select(CloneCommonVertex)
                        .ToArray(),
                    IndexList = aliasesSource
                        ? merged.Mesh.IndexList.ToArray()
                        : merged.Mesh.IndexList,
                },
            };
        }

        private static CommonVertex CloneCommonVertex(CommonVertex vertex)
        {
            return new CommonVertex
            {
                Position = vertex.Position,
                Normal = vertex.Normal,
                BiNormal = vertex.BiNormal,
                Tangent = vertex.Tangent,
                Uv = vertex.Uv,
                Uv1 = vertex.Uv1,
                Colour = vertex.Colour,
                BoneIndex = vertex.BoneIndex?.ToArray()!,
                BoneWeight = vertex.BoneWeight?.ToArray()!,
                WeightCount = vertex.WeightCount,
            };
        }

        private static bool TryReadWsMaterialAssignments(
            XmlDocument document,
            RmvFile rmv,
            out string[][] assignments,
            out string reason,
            bool allowUnreferencedMaterialEntries = false)
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

            // In the cross-rigid copy-on-write path alone, allow obsolete
            // material assignments for LOD/part indices absent from the
            // current rigid. Required assignments were all verified above.
            // The generated WSModel is emitted with an exact replacement
            // table, so no unused source rows are carried forward.
            if (!allowUnreferencedMaterialEntries &&
                byKey.Count != expectedCount)
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
    }
}
