using System.Globalization;
using System.Numerics;
using System.Text;
using GameWorld.MeshOptimizer;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.Types;
using Shared.GameFormats.RigidModel.Vertex;

namespace Editors.PackOptimizer.Core
{
    public sealed partial class PackTextureAtlasBatchService
    {
        // Rebuilds LOD 1+ of rigids that AssetEditor's old decimator generated (EditorLodDetector).
        // LOD0, the LOD count, the LOD headers and every LOD's part list and materials stay as they are;
        // only the meshes of LOD 1+ are replaced, so WSModel material tables remain valid.
        private const int EditorLodMaxLevels = 16;
        private const float EditorLodErrorLimitedShare = 0.8f;

        private sealed class ShapeSamples
        {
            public List<float> ForwardAbs { get; } = [];
            public List<float> ForwardRel { get; } = [];
            public List<float> ReverseAbs { get; } = [];
            public List<float> ReverseRel { get; } = [];
        }

        private sealed record VanishedComponent(string Rigid, int Lod, int Part, int Triangles, Vector3 Size);

        private sealed class EditorLodRemakeResult
        {
            public int Candidates { get; set; }
            public Dictionary<EditorLodEvidence, int> Detected { get; } = [];
            public Dictionary<EditorLodEvidence, int> Remade { get; } = [];
            public Dictionary<string, int> Skipped { get; } = new(StringComparer.Ordinal);
            public int RigidsRemade { get; set; }
            public int LodsReplaced { get; set; }
            public int LodsKeptNotLighter { get; set; }
            public int PartsRebuilt { get; set; }
            public int PartsKeptTooLarge { get; set; }
            public int PartsKeptNotReduced { get; set; }
            public int PartsMissedTargetByError { get; set; }
            public int PartsMissedTargetByTopology { get; set; }
            public IReadOnlyList<EditorLodLevelSettings> Settings { get; set; } = EditorLodLevelSettings.Default;
            public float BoneWeight { get; set; }
            public bool JointPriority { get; set; }
            public int SkinnedPartsRebuilt { get; set; }
            // Report-only: LOD0 components (welded by position) that have no triangle left in the rebuilt part.
            public int[] VanishedComponents { get; } = new int[EditorLodMaxLevels];
            public long[] VanishedComponentTriangles { get; } = new long[EditorLodMaxLevels];
            public int[] SourceComponents { get; } = new int[EditorLodMaxLevels];
            public List<VanishedComponent> Vanished { get; } = [];
            public int SeamGroupsSnapped { get; set; }
            public int UpdatedParts { get; set; }
            // Report-only shape error per LOD level (old LOD and rebuilt LOD, same parts): LOD0 samples to the LOD surface
            // (forward) and LOD samples to the LOD0 surface (reverse); absolute and relative to the part's LOD0 bounding diagonal.
            public ShapeSamples[] ShapeOld { get; } = Enumerable.Range(0, EditorLodMaxLevels).Select(_ => new ShapeSamples()).ToArray();
            public ShapeSamples[] ShapeNew { get; } = Enumerable.Range(0, EditorLodMaxLevels).Select(_ => new ShapeSamples()).ToArray();
            // Skinning-error samples per LOD level (old LODs and the rebuilt ones, same parts and vertices).
            public List<float>[] SkinningOld { get; } = Enumerable.Range(0, EditorLodMaxLevels).Select(_ => new List<float>()).ToArray();
            public List<float>[] SkinningNew { get; } = Enumerable.Range(0, EditorLodMaxLevels).Select(_ => new List<float>()).ToArray();
            public long[] TrianglesBefore { get; } = new long[EditorLodMaxLevels];
            public long[] TrianglesAfter { get; } = new long[EditorLodMaxLevels];
            public long[] BytesBefore { get; } = new long[EditorLodMaxLevels];
            public long[] BytesAfter { get; } = new long[EditorLodMaxLevels];
            public int[] LodsReplacedByLevel { get; } = new int[EditorLodMaxLevels];
                    public string? NativeError { get; set; }

            public void Skip(string reason) => Skipped[reason] = Skipped.GetValueOrDefault(reason) + 1;
        }

        private void RemakeEditorLods(
            BatchState state,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var result = state.EditorLods;
            result.Settings = state.EditorLodSettings;
            result.BoneWeight = state.EditorLodBoneWeight;
            result.JointPriority = state.EditorLodJointPriority;
            var rigidPaths = state.SourceReachableAssetFiles
                .Where(path => path.EndsWith(".rigid_model_v2", StringComparison.OrdinalIgnoreCase) &&
                               state.Source.ContainsFile(path))
                .Select(Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var vertexFactory = VertexFactory.Create();

            for (var index = 0; index < rigidPaths.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rigidPath = rigidPaths[index];
                ReportPeriodicProgress(progress, "Remaking editor LODs", index + 1, rigidPaths.Count, rigidPath);
                result.Candidates++;

                RmvFile? rmv;
                try
                {
                    rmv = GetRmv(state, rigidPath);
                }
                catch (Exception ex) when (
                    ex is InvalidDataException or InvalidOperationException or ArgumentException or FormatException)
                {
                    continue;
                }

                if (rmv == null)
                    continue;

                var verdict = EditorLodDetector.Analyze(rmv);
                if (!verdict.IsEditorGenerated)
                    continue;

                result.Detected[verdict.Evidence] = result.Detected.GetValueOrDefault(verdict.Evidence) + 1;

                if (state.Consumers?.TryBlock(rigidPath, "editor LOD remake") == true)
                {
                    result.Skip("consumer used or overridden");
                    continue;
                }

                var models = rmv.ModelList.SelectMany(lod => lod).ToList();
                if (models.Any(model => model.CommonHeader.ModelTypeFlag is
                        ModelMaterialEnum.cloth or ModelMaterialEnum.weighted_cloth))
                {
                    result.Skip("cloth model type");
                    continue;
                }

                if (models.Any(model => model.Mesh.IndexList.Length % 3 != 0 ||
                                        model.Mesh.IndexList.Any(i => i >= model.Mesh.VertexList.Length)))
                {
                    result.Skip("invalid index buffer");
                    continue;
                }

                var mappingFailure = TryMapEditorLodParts(rmv, out var maps);
                if (mappingFailure != null)
                {
                    result.Skip(mappingFailure);
                    continue;
                }

                try
                {
                    if (RebuildEditorLods(state, rigidPath, rmv, maps!, vertexFactory, cancellationToken))
                    {
                        state.EditorLodRemadeRigids.Add(rigidPath);
                        result.RigidsRemade++;
                        result.Remade[verdict.Evidence] = result.Remade.GetValueOrDefault(verdict.Evidence) + 1;
                    }
                    else
                    {
                        result.Skip("no LOD became lighter than the one above");
                    }
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                {
                    // Without the native library nothing can be remade; say so once and stop.
                    result.NativeError = ex.Message;
                    result.Skip("meshoptimizer could not be loaded");
                    return;
                }
            }
        }

        // Called once, right before the modified assets are saved: from here the remade rigids are
        // ordinary modified rigids.
        private static void CommitEditorLodRemadeRigids(BatchState state)
            => state.ModifiedRigids.UnionWith(state.EditorLodRemadeRigids);

        private static string EditorLodMaterialKey(RmvModel model)
            => model.Material == null
                ? string.Empty
                : model.Material.MaterialId + "|" + string.Join(
                    ";",
                    model.Material.GetAllTextures()
                        .Select(texture => (int)texture.TexureType + "=" + Normalize(texture.Path))
                        .OrderBy(x => x, StringComparer.Ordinal));

        private static int EditorLodWeightCount(RmvModel model)
            => model.Material?.BinaryVertexFormat switch
            {
                VertexFormat.Static => 0,
                VertexFormat.Weighted => 2,
                VertexFormat.Cinematic => 4,
                _ => -1,
            };

        // For every LOD i >= 1, the LOD0 part indices each of its parts was built from: one to one when
        // the part lists match, else the editor's own grouping (same material, same static/weighted class,
        // first-fit in LOD0 order). Null result = mapped; otherwise the reason it could not be.
        private static string? TryMapEditorLodParts(RmvFile rmv, out int[][][]? maps)
        {
            maps = null;
            var lod0 = rmv.ModelList[0];
            var all = rmv.ModelList.SelectMany(x => x);
            if (all.Any(model => EditorLodWeightCount(model) < 0))
                return "unsupported vertex format";

            var result = new int[rmv.ModelList.Length - 1][][];
            var lod0Keys = lod0.Select(EditorLodMaterialKey).ToArray();
            for (var lod = 1; lod < rmv.ModelList.Length; lod++)
            {
                var parts = rmv.ModelList[lod];
                int[][] groups;
                if (parts.Length == lod0.Length &&
                    Enumerable.Range(0, parts.Length).All(i => EditorLodMaterialKey(parts[i]) == lod0Keys[i]))
                {
                    groups = Enumerable.Range(0, parts.Length).Select(i => new[] { i }).ToArray();
                }
                else
                {
                    var grouped = new List<(string Key, List<int> Members)>();
                    for (var i = 0; i < lod0.Length; i++)
                    {
                        var key = lod0Keys[i] + "|" + (EditorLodWeightCount(lod0[i]) == 0 ? "S" : "W");
                        var existing = grouped.FindIndex(g => g.Key == key);
                        if (existing < 0)
                            grouped.Add((key, [i]));
                        else
                            grouped[existing].Members.Add(i);
                    }

                    if (grouped.Count != parts.Length ||
                        Enumerable.Range(0, parts.Length).Any(g => EditorLodMaterialKey(parts[g]) != lod0Keys[grouped[g].Members[0]]))
                    {
                        return "part mapping ambiguous";
                    }

                    groups = grouped.Select(g => g.Members.ToArray()).ToArray();
                }

                for (var g = 0; g < groups.Length; g++)
                {
                    var target = EditorLodWeightCount(parts[g]);
                    if (groups[g].Any(member => EditorLodWeightCount(lod0[member]) < target))
                        return "LOD bone format richer than LOD0";
                    if (target == 0 && groups[g].Any(member => EditorLodWeightCount(lod0[member]) != 0))
                        return "LOD bone format richer than LOD0";
                }

                result[lod - 1] = groups;
            }

            maps = result;
            return null;
        }

        private static bool RebuildEditorLods(
            BatchState state,
            string rigidPath,
            RmvFile rmv,
            int[][][] maps,
            VertexFactory vertexFactory,
            CancellationToken cancellationToken)
        {
            var result = state.EditorLods;
            var lod0 = rmv.ModelList[0];
            var lodCount = rmv.ModelList.Length;
            var previousTriangles = lod0.Sum(model => (long)model.Mesh.IndexList.Length / 3);
            var anyReplaced = false;

            for (var lod = 1; lod < lodCount; lod++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parts = rmv.ModelList[lod];
                var groups = maps[lod - 1];
                var factor = (float)EditorLodDetector.Factor(lod, lodCount);
                var levelSettings = EditorLodLevelSettings.For(state.EditorLodSettings, lod);
                var defaultOptions = new MeshOptReducerOptions();
                var options = new MeshOptReducerOptions
                {
                    TargetError = levelSettings.TargetError,
                    LockBorder = levelSettings.LockBorder,
                    PreserveFolds = levelSettings.Flags.HasFlag(EditorLodSimplifyFlags.PreserveFolds),
                    RegularizeLight = levelSettings.Flags.HasFlag(EditorLodSimplifyFlags.RegularizeLight),
                    ErrorClamped = levelSettings.Flags.HasFlag(EditorLodSimplifyFlags.ErrorClamped),
                    Prune = levelSettings.Flags.HasFlag(EditorLodSimplifyFlags.Prune),
                    UpdateVertices = levelSettings.Method == EditorLodSimplifyMethod.Update,
                    NormalWeight = defaultOptions.NormalWeight * levelSettings.AttributeScale,
                    TextureCoordinateWeight = defaultOptions.TextureCoordinateWeight * levelSettings.AttributeScale,
                };
                var pendingVanished = new List<VanishedComponent>();
                var pendingSourceComponents = 0;
                var rebuilt = new RmvMesh?[parts.Length];
                var pendingRebuilt = 0;
                var pendingTooLarge = 0;
                var pendingNotReduced = 0;
                var pendingError = 0;
                var pendingTopology = 0;
                var pendingSkinned = 0;
                var pendingSnapped = 0;
                var pendingUpdated = 0;
                var surfaces = new (Vector3[] Positions, ushort[] Indices)?[parts.Length];

                for (var g = 0; g < parts.Length; g++)
                {
                    var members = groups[g];
                    var vertexCount = members.Sum(m => lod0[m].Mesh.VertexList.Length);
                    if (vertexCount > ushort.MaxValue)
                    {
                        pendingTooLarge++;
                        continue;
                    }

                    var vertices = new CommonVertex[vertexCount];
                    var positions = new float[vertexCount * 3];
                    var normals = new float[vertexCount * 3];
                    var uvs = new float[vertexCount * 2];
                    var indices = new List<ushort>();
                    var offset = 0;
                    foreach (var member in members)
                    {
                        var source = lod0[member].Mesh;
                        foreach (var vertex in source.VertexList)
                        {
                            vertices[offset] = vertex;
                            positions[offset * 3] = vertex.Position.X;
                            positions[offset * 3 + 1] = vertex.Position.Y;
                            positions[offset * 3 + 2] = vertex.Position.Z;
                            normals[offset * 3] = vertex.Normal.X;
                            normals[offset * 3 + 1] = vertex.Normal.Y;
                            normals[offset * 3 + 2] = vertex.Normal.Z;
                            uvs[offset * 2] = vertex.Uv.X;
                            uvs[offset * 2 + 1] = vertex.Uv.Y;
                            offset++;
                        }

                        var vertexOffset = offset - source.VertexList.Length;
                        indices.AddRange(source.IndexList.Select(i => (ushort)(i + vertexOffset)));
                    }

                    var indexArray = indices.ToArray();
                    if (state.EditorLodReportSkinningError)
                    {
                        surfaces[g] = (Enumerable.Range(0, vertexCount).Select(i => new Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2])).ToArray(), indexArray);
                    }
                    var skinned = EditorLodWeightCount(parts[g]) > 0;
                    float[] boneEmbedding = [];
                    byte[] jointFlags = [];
                    if (skinned && state.EditorLodBoneWeight > 0)
                        boneEmbedding = BuildEditorLodBoneEmbedding(vertices);
                    if (skinned && state.EditorLodJointPriority)
                        jointFlags = BuildEditorLodJointFlags(vertices, indexArray);
                    if (boneEmbedding.Length != 0 || jointFlags.Length != 0)
                        pendingSkinned++;

                    var simplified = MeshOptSimplifier.Simplify(
                        positions, normals, uvs, indexArray, factor, options,
                        boneEmbedding, boneEmbedding.Length == 0 ? 0 : EditorLodEmbeddingSize, state.EditorLodBoneWeight, jointFlags);
                    if (simplified.Stats.ReductionSkipped)
                    {
                        pendingNotReduced++;
                        continue;
                    }

                    if (state.EditorLodReportSkinningError)
                    {
                        var vanished = FindVanishedComponents(vertices, indexArray, simplified, rigidPath, lod, g, out var sourceComponents);
                        pendingVanished.AddRange(vanished);
                        pendingSourceComponents += sourceComponents;
                    }

                    var targetWeights = EditorLodWeightCount(parts[g]);
                    var output = new CommonVertex[simplified.VertexCount];
                    NearestVertexGrid? nearestGrid = null;
                    if (simplified.HasUpdate && state.EditorLodUpdateNearestWeights && targetWeights > 0)
                        nearestGrid = new NearestVertexGrid(vertices);
                    for (var old = 0; old < simplified.Remap.Length; old++)
                    {
                        var mapped = simplified.Remap[old];
                        if (mapped == uint.MaxValue)
                            continue;

                        output[mapped] = simplified.HasUpdate
                            ? BuildUpdatedEditorLodVertex(vertices[old], simplified, old, targetWeights, nearestGrid)
                            : ConvertEditorLodVertex(vertices[old], targetWeights);
                    }

                    if (simplified.HasUpdate)
                    {
                        pendingUpdated++;
                        pendingSnapped += simplified.SeamViolations;
                    }

                    rebuilt[g] = new RmvMesh { VertexList = output, IndexList = simplified.Indices };
                    pendingRebuilt++;
                    if (!simplified.Stats.TargetReached)
                    {
                        // meshoptimizer stops at the last collapse under the cap, so a result error close to
                        // the cap (not only at it) means the cap, not the topology, ended the reduction.
                        if (simplified.Stats.ErrorLimited ||
                            simplified.Stats.ResultErrorRelative >= EditorLodErrorLimitedShare * levelSettings.TargetError)
                            pendingError++;
                        else
                            pendingTopology++;
                    }
                }

                var newTriangles = 0L;
                for (var g = 0; g < parts.Length; g++)
                    newTriangles += (rebuilt[g] ?? parts[g].Mesh).IndexList.Length / 3;

                if (pendingRebuilt == 0 || newTriangles >= previousTriangles)
                {
                    result.LodsKeptNotLighter++;
                    previousTriangles = parts.Sum(model => (long)model.Mesh.IndexList.Length / 3);
                    continue;
                }

                var version = rmv.Header.Version;
                for (var g = 0; g < parts.Length; g++)
                {
                    if (rebuilt[g] == null)
                        continue;

                    var model = parts[g];
                    if (state.EditorLodReportSkinningError && EditorLodWeightCount(model) > 0)
                    {
                        var lod0Vertices = groups[g].SelectMany(m => lod0[m].Mesh.VertexList).ToArray();
                        result.SkinningOld[Math.Min(lod, EditorLodMaxLevels - 1)].AddRange(SampleEditorLodSkinningError(lod0Vertices, model.Mesh));
                        result.SkinningNew[Math.Min(lod, EditorLodMaxLevels - 1)].AddRange(SampleEditorLodSkinningError(lod0Vertices, rebuilt[g]!));
                    }

                    if (state.EditorLodReportSkinningError && surfaces[g] is { } surface)
                    {
                        var lod0Vertices = groups[g].SelectMany(m => lod0[m].Mesh.VertexList).ToArray();
                        var level = Math.Min(lod, EditorLodMaxLevels - 1);
                        AddShapeSamples(result.ShapeOld[level], lod0Vertices, surface, model.Mesh);
                        AddShapeSamples(result.ShapeNew[level], lod0Vertices, surface, rebuilt[g]!);
                    }

                    var vertexSize = (long)vertexFactory.GetVertexSize(model.Material.BinaryVertexFormat, version);
                    result.TrianglesBefore[Math.Min(lod, EditorLodMaxLevels - 1)] += model.Mesh.IndexList.Length / 3;
                    result.BytesBefore[Math.Min(lod, EditorLodMaxLevels - 1)] +=
                        vertexSize * model.Mesh.VertexList.Length + sizeof(ushort) * (long)model.Mesh.IndexList.Length;
                    model.Mesh = rebuilt[g]!;
                    result.TrianglesAfter[Math.Min(lod, EditorLodMaxLevels - 1)] += model.Mesh.IndexList.Length / 3;
                    result.BytesAfter[Math.Min(lod, EditorLodMaxLevels - 1)] +=
                        vertexSize * model.Mesh.VertexList.Length + sizeof(ushort) * (long)model.Mesh.IndexList.Length;
                    UpdateEditorLodBoundingBox(model);
                }

                result.LodsReplaced++;
                result.LodsReplacedByLevel[Math.Min(lod, EditorLodMaxLevels - 1)]++;
                result.PartsRebuilt += pendingRebuilt;
                result.UpdatedParts += pendingUpdated;
                result.SeamGroupsSnapped += pendingSnapped;
                result.SourceComponents[Math.Min(lod, EditorLodMaxLevels - 1)] += pendingSourceComponents;
                result.VanishedComponents[Math.Min(lod, EditorLodMaxLevels - 1)] += pendingVanished.Count;
                result.VanishedComponentTriangles[Math.Min(lod, EditorLodMaxLevels - 1)] += pendingVanished.Sum(x => (long)x.Triangles);
                result.Vanished.AddRange(pendingVanished);
                result.SkinnedPartsRebuilt += pendingSkinned;
                result.PartsKeptTooLarge += pendingTooLarge;
                result.PartsKeptNotReduced += pendingNotReduced;
                result.PartsMissedTargetByError += pendingError;
                result.PartsMissedTargetByTopology += pendingTopology;
                previousTriangles = newTriangles;
                anyReplaced = true;
            }

            return anyReplaced;
        }

        // LOD0 components (vertices welded by position, then joined by triangles) that have no triangle in the
        // simplified result: what the simplifier (or prune) made disappear.
        private static List<VanishedComponent> FindVanishedComponents(
            CommonVertex[] vertices, ushort[] indices, MeshOptSimplifyResult simplified, string rigidPath, int lod, int part, out int sourceComponents)
        {
            var parent = Enumerable.Range(0, vertices.Length).ToArray();
            int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]];
                    x = parent[x];
                }

                return x;
            }

            void Union(int a, int b)
            {
                a = Find(a);
                b = Find(b);
                if (a != b)
                    parent[a] = b;
            }

            var firstAtPosition = new Dictionary<(float, float, float), int>();
            for (var i = 0; i < vertices.Length; i++)
            {
                var key = (vertices[i].Position.X, vertices[i].Position.Y, vertices[i].Position.Z);
                if (firstAtPosition.TryGetValue(key, out var first))
                    Union(i, first);
                else
                    firstAtPosition[key] = i;
            }

            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                Union(indices[i], indices[i + 1]);
                Union(indices[i], indices[i + 2]);
            }

            var triangles = new Dictionary<int, int>();
            var min = new Dictionary<int, Vector3>();
            var max = new Dictionary<int, Vector3>();
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                var root = Find(indices[i]);
                triangles[root] = triangles.GetValueOrDefault(root) + 1;
                for (var k = 0; k < 3; k++)
                {
                    var v = vertices[indices[i + k]].Position;
                    var p = new Vector3(v.X, v.Y, v.Z);
                    min[root] = min.TryGetValue(root, out var lo) ? Vector3.Min(lo, p) : p;
                    max[root] = max.TryGetValue(root, out var hi) ? Vector3.Max(hi, p) : p;
                }
            }

            sourceComponents = triangles.Count;
            var newToOld = new int[simplified.VertexCount];
            for (var old = 0; old < simplified.Remap.Length; old++)
            {
                if (simplified.Remap[old] != uint.MaxValue)
                    newToOld[simplified.Remap[old]] = old;
            }

            var alive = new HashSet<int>();
            foreach (var index in simplified.Indices)
                alive.Add(Find(newToOld[index]));

            return triangles
                .Where(entry => !alive.Contains(entry.Key))
                .Select(entry => new VanishedComponent(rigidPath, lod, part, entry.Value, max[entry.Key] - min[entry.Key]))
                .ToList();
        }

        // Rebuilds a kept vertex after meshopt_simplifyWithUpdate: position, UV0 and normal come from the updated
        // values; the tangent frame is rebuilt from the original tangent and handedness; bones, UV1, colour and the
        // rest come from the original record (or, optionally, bones from the nearest LOD0 vertex).
        internal static CommonVertex BuildUpdatedEditorLodVertex(
            CommonVertex source, MeshOptSimplifyResult result, int old, int targetWeights, NearestVertexGrid? nearest)
        {
            var position = new Vector4(
                result.UpdatedPositions![old * 3], result.UpdatedPositions[old * 3 + 1], result.UpdatedPositions[old * 3 + 2], source.Position.W);
            var a = result.AttributeCount;
            var attributes = result.UpdatedAttributes!;
            var normal = new Vector3(attributes[old * a], attributes[old * a + 1], attributes[old * a + 2]);
            normal = normal.LengthSquared() < 1e-12f ? source.Normal : Vector3.Normalize(normal);
            var uv = new Vector2(attributes[old * a + 3], attributes[old * a + 4]);

            var handedness = Vector3.Dot(Vector3.Cross(source.Normal, source.Tangent), source.BiNormal) < 0 ? -1f : 1f;
            var tangent = source.Tangent - normal * Vector3.Dot(normal, source.Tangent);
            if (tangent.LengthSquared() < 1e-12f)
                tangent = Vector3.Cross(normal, MathF.Abs(normal.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY);
            tangent = Vector3.Normalize(tangent);
            var binormal = Vector3.Normalize(Vector3.Cross(normal, tangent) * handedness);

            var boneSource = nearest != null ? nearest.Nearest(new Vector3(position.X, position.Y, position.Z)) : source;
            var converted = ConvertEditorLodVertex(source, targetWeights);
            var bones = ConvertEditorLodVertex(boneSource, targetWeights);
            converted.Position = position;
            converted.Normal = normal;
            converted.Tangent = tangent;
            converted.BiNormal = binormal;
            converted.Uv = uv;
            converted.BoneIndex = bones.BoneIndex;
            converted.BoneWeight = bones.BoneWeight;
            return converted;
        }

        // Uniform grid over the LOD0 vertices of one part, for nearest-vertex lookups.
        internal sealed class NearestVertexGrid
        {
            private readonly CommonVertex[] _vertices;
            private readonly Dictionary<(int, int, int), List<int>> _cells = [];
            private readonly float _cell;

            public NearestVertexGrid(CommonVertex[] vertices)
            {
                _vertices = vertices;
                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                foreach (var v in vertices)
                {
                    var p = new Vector3(v.Position.X, v.Position.Y, v.Position.Z);
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }

                var extent = Math.Max((max - min).Length(), 1e-6f);
                _cell = extent / Math.Max(4f, MathF.Cbrt(vertices.Length));
                for (var i = 0; i < vertices.Length; i++)
                {
                    var key = Key(new Vector3(vertices[i].Position.X, vertices[i].Position.Y, vertices[i].Position.Z));
                    if (!_cells.TryGetValue(key, out var list))
                        _cells[key] = list = [];
                    list.Add(i);
                }
            }

            private (int, int, int) Key(Vector3 p) => ((int)MathF.Floor(p.X / _cell), (int)MathF.Floor(p.Y / _cell), (int)MathF.Floor(p.Z / _cell));

            public CommonVertex Nearest(Vector3 p)
            {
                var center = Key(p);
                var best = -1;
                var bestDistance = float.MaxValue;
                for (var radius = 0; radius < 64; radius++)
                {
                    for (var x = -radius; x <= radius; x++)
                    for (var y = -radius; y <= radius; y++)
                    for (var z = -radius; z <= radius; z++)
                    {
                        if (Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z))) != radius)
                            continue;
                        if (!_cells.TryGetValue((center.Item1 + x, center.Item2 + y, center.Item3 + z), out var list))
                            continue;
                        foreach (var i in list)
                        {
                            var d = Vector3.DistanceSquared(p, new Vector3(_vertices[i].Position.X, _vertices[i].Position.Y, _vertices[i].Position.Z));
                            if (d < bestDistance)
                            {
                                bestDistance = d;
                                best = i;
                            }
                        }
                    }

                    // Everything within the found distance has been seen once the ring radius passes it.
                    if (best >= 0 && MathF.Sqrt(bestDistance) <= radius * _cell)
                        break;
                }

                return _vertices[best >= 0 ? best : 0];
            }
        }

        private static void AddShapeSamples(ShapeSamples target, CommonVertex[] lod0Vertices, (Vector3[] Positions, ushort[] Indices) lod0Surface, RmvMesh lod)
        {
            if (lod0Vertices.Length == 0 || lod.VertexList.Length == 0 || lod.IndexList.Length < 3)
                return;

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in lod0Surface.Positions)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            var size = Math.Max((max - min).Length(), 1e-9f);
            var lodPositions = lod.VertexList.Select(v => new Vector3(v.Position.X, v.Position.Y, v.Position.Z)).ToArray();
            var lod0Points = lod0Vertices.Select(v => new Vector3(v.Position.X, v.Position.Y, v.Position.Z)).ToArray();
            var forward = SurfaceDistances(Sample(lod0Points), lodPositions, lod.IndexList);
            var reverse = SurfaceDistances(Sample(lodPositions), lod0Surface.Positions, lod0Surface.Indices);
            target.ForwardAbs.AddRange(forward);
            target.ForwardRel.AddRange(forward.Select(x => x / size));
            target.ReverseAbs.AddRange(reverse);
            target.ReverseRel.AddRange(reverse.Select(x => x / size));
        }

        private static Vector3[] Sample(Vector3[] points)
        {
            var step = Math.Max(1, points.Length / EditorLodSkinningSamplesPerPart);
            return points.Where((_, i) => i % step == 0).ToArray();
        }

        internal static float[] SurfaceDistances(Vector3[] points, Vector3[] surface, ushort[] indices)
        {
            var result = new float[points.Length];
            Parallel.For(0, points.Length, k =>
            {
                var best = float.MaxValue;
                for (var t = 0; t + 2 < indices.Length; t += 3)
                {
                    var closest = ClosestPointOnTriangle(points[k], surface[indices[t]], surface[indices[t + 1]], surface[indices[t + 2]], out _);
                    var distance = Vector3.DistanceSquared(points[k], closest);
                    // Degenerate (zero-area) triangles give NaN; they have no surface to measure against.
                    if (float.IsFinite(distance))
                        best = Math.Min(best, distance);
                }

                result[k] = best == float.MaxValue ? float.NaN : MathF.Sqrt(best);
            });
            return result.Where(float.IsFinite).ToArray();
        }

        private const int EditorLodEmbeddingSize = 4;
        private const int EditorLodSkinningSamplesPerPart = 128;

        /// <summary>A fixed unit vector in 4D for a bone index (splitmix64 of the index), so the output is reproducible.</summary>
        internal static Vector4 EditorLodBoneVector(byte bone)
        {
            static ulong Mix(ref ulong state)
            {
                state += 0x9E3779B97F4A7C15UL;
                var z = state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }

            var seed = 0x42A1E5UL * 1000003UL + bone;
            var vector = new Vector4(
                (float)(Mix(ref seed) >> 40) / (1 << 23) - 1f,
                (float)(Mix(ref seed) >> 40) / (1 << 23) - 1f,
                (float)(Mix(ref seed) >> 40) / (1 << 23) - 1f,
                (float)(Mix(ref seed) >> 40) / (1 << 23) - 1f);
            return vector.LengthSquared() < 1e-6f ? Vector4.UnitX : Vector4.Normalize(vector);
        }

        // 4 attributes per vertex: sum of weight * bone vector.
        internal static float[] BuildEditorLodBoneEmbedding(CommonVertex[] vertices)
        {
            var cache = new Vector4[256];
            var known = new bool[256];
            var result = new float[vertices.Length * EditorLodEmbeddingSize];
            for (var i = 0; i < vertices.Length; i++)
            {
                var sum = Vector4.Zero;
                var indices = vertices[i].BoneIndex ?? [];
                var weights = vertices[i].BoneWeight ?? [];
                for (var b = 0; b < Math.Min(indices.Length, weights.Length); b++)
                {
                    var bone = indices[b];
                    if (!known[bone])
                    {
                        cache[bone] = EditorLodBoneVector(bone);
                        known[bone] = true;
                    }

                    sum += weights[b] * cache[bone];
                }

                result[i * 4] = sum.X;
                result[i * 4 + 1] = sum.Y;
                result[i * 4 + 2] = sum.Z;
                result[i * 4 + 3] = sum.W;
            }

            return result;
        }

        private static int EditorLodDominantBone(CommonVertex vertex)
        {
            var indices = vertex.BoneIndex ?? [];
            var weights = vertex.BoneWeight ?? [];
            var best = -1;
            var bestWeight = 0f;
            for (var b = 0; b < Math.Min(indices.Length, weights.Length); b++)
            {
                if (weights[b] > bestWeight)
                {
                    bestWeight = weights[b];
                    best = indices[b];
                }
            }

            return best;
        }

        // Vertices on an edge whose ends have different dominant bones are joint vertices: priority.
        internal static byte[] BuildEditorLodJointFlags(CommonVertex[] vertices, ushort[] indices)
        {
            var dominant = vertices.Select(EditorLodDominantBone).ToArray();
            var flags = new byte[vertices.Length];
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                for (var e = 0; e < 3; e++)
                {
                    var a = indices[i + e];
                    var b = indices[i + (e + 1) % 3];
                    if (dominant[a] != dominant[b])
                    {
                        flags[a] = MeshOptSimplifier.VertexPriority;
                        flags[b] = MeshOptSimplifier.VertexPriority;
                    }
                }
            }

            return flags;
        }

        // L1 distance between a LOD0 vertex's bone weights and the weights interpolated at the closest point of the
        // LOD surface, over a deterministic sample of the LOD0 vertices.
        internal static List<float> SampleEditorLodSkinningError(CommonVertex[] lod0Vertices, RmvMesh surface)
        {
            var result = new List<float>();
            if (lod0Vertices.Length == 0 || surface.IndexList.Length < 3)
                return result;

            var step = Math.Max(1, lod0Vertices.Length / EditorLodSkinningSamplesPerPart);
            var samples = Enumerable.Range(0, lod0Vertices.Length).Where(i => i % step == 0).ToArray();
            var positions = surface.VertexList.Select(v => new Vector3(v.Position.X, v.Position.Y, v.Position.Z)).ToArray();
            var errors = new float[samples.Length];
            Parallel.For(0, samples.Length, k =>
            {
                var vertex = lod0Vertices[samples[k]];
                var p = new Vector3(vertex.Position.X, vertex.Position.Y, vertex.Position.Z);
                var bestDistance = float.MaxValue;
                var bestTriangle = 0;
                var bestWeights = new Vector3(1, 0, 0);
                for (var t = 0; t + 2 < surface.IndexList.Length; t += 3)
                {
                    var closest = ClosestPointOnTriangle(
                        p, positions[surface.IndexList[t]], positions[surface.IndexList[t + 1]], positions[surface.IndexList[t + 2]], out var barycentric);
                    var distance = Vector3.DistanceSquared(p, closest);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestTriangle = t;
                        bestWeights = barycentric;
                    }
                }

                var blended = new Dictionary<byte, float>();
                void Add(CommonVertex source, float factor)
                {
                    var indices = source.BoneIndex ?? [];
                    var weights = source.BoneWeight ?? [];
                    for (var b = 0; b < Math.Min(indices.Length, weights.Length); b++)
                        blended[indices[b]] = blended.GetValueOrDefault(indices[b]) + factor * weights[b];
                }

                Add(surface.VertexList[surface.IndexList[bestTriangle]], bestWeights.X);
                Add(surface.VertexList[surface.IndexList[bestTriangle + 1]], bestWeights.Y);
                Add(surface.VertexList[surface.IndexList[bestTriangle + 2]], bestWeights.Z);

                var original = new Dictionary<byte, float>();
                var originalIndices = vertex.BoneIndex ?? [];
                var originalWeights = vertex.BoneWeight ?? [];
                for (var b = 0; b < Math.Min(originalIndices.Length, originalWeights.Length); b++)
                    original[originalIndices[b]] = original.GetValueOrDefault(originalIndices[b]) + originalWeights[b];

                var error = 0f;
                foreach (var bone in original.Keys.Union(blended.Keys))
                    error += Math.Abs(original.GetValueOrDefault(bone) - blended.GetValueOrDefault(bone));
                errors[k] = error;
            });

            result.AddRange(errors);
            return result;
        }

        // Ericson, Real-Time Collision Detection 5.1.5, with barycentric coordinates of the result.
        private static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out Vector3 barycentric)
        {
            var ab = b - a;
            var ac = c - a;
            var ap = p - a;
            var d1 = Vector3.Dot(ab, ap);
            var d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) { barycentric = new Vector3(1, 0, 0); return a; }
            var bp = p - b;
            var d3 = Vector3.Dot(ab, bp);
            var d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) { barycentric = new Vector3(0, 1, 0); return b; }
            var vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0)
            {
                var v = d1 / (d1 - d3);
                barycentric = new Vector3(1 - v, v, 0);
                return a + v * ab;
            }

            var cp = p - c;
            var d5 = Vector3.Dot(ab, cp);
            var d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) { barycentric = new Vector3(0, 0, 1); return c; }
            var vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0)
            {
                var w = d2 / (d2 - d6);
                barycentric = new Vector3(1 - w, 0, w);
                return a + w * ac;
            }

            var va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            {
                var w = (d4 - d3) / (d4 - d3 + (d5 - d6));
                barycentric = new Vector3(0, 1 - w, w);
                return b + w * (c - b);
            }

            var denominator = 1f / (va + vb + vc);
            var vv = vb * denominator;
            var ww = vc * denominator;
            barycentric = new Vector3(1 - vv - ww, vv, ww);
            return a + ab * vv + ac * ww;
        }

        private static CommonVertex ConvertEditorLodVertex(CommonVertex source, int targetWeights)
        {
            var converted = new CommonVertex
            {
                Position = source.Position,
                Normal = source.Normal,
                BiNormal = source.BiNormal,
                Tangent = source.Tangent,
                Uv = source.Uv,
                Uv1 = source.Uv1,
                Colour = source.Colour,
                WeightCount = targetWeights,
                BoneIndex = [],
                BoneWeight = [],
            };

            if (targetWeights == 0)
                return converted;

            var indices = source.BoneIndex ?? [];
            var weights = source.BoneWeight ?? [];
            if (indices.Length == targetWeights && weights.Length == targetWeights)
            {
                converted.BoneIndex = (byte[])indices.Clone();
                converted.BoneWeight = (float[])weights.Clone();
                return converted;
            }

            // Fewer bones than LOD0: keep the strongest influences and renormalize.
            var order = Enumerable.Range(0, Math.Min(indices.Length, weights.Length))
                .OrderByDescending(i => weights[i])
                .Take(targetWeights)
                .ToArray();
            var newIndices = new byte[targetWeights];
            var newWeights = new float[targetWeights];
            for (var i = 0; i < order.Length; i++)
            {
                newIndices[i] = indices[order[i]];
                newWeights[i] = weights[order[i]];
            }

            var total = newWeights.Sum();
            if (total > 0)
            {
                for (var i = 0; i < newWeights.Length; i++)
                    newWeights[i] /= total;
            }
            else
            {
                newWeights[0] = 1;
            }

            converted.BoneIndex = newIndices;
            converted.BoneWeight = newWeights;
            return converted;
        }

        private static void UpdateEditorLodBoundingBox(RmvModel model)
        {
            if (model.Mesh.VertexList.Length == 0)
                return;

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var vertex in model.Mesh.VertexList)
            {
                var position = new Vector3(vertex.Position.X, vertex.Position.Y, vertex.Position.Z);
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
            }

            model.UpdateBoundingBox(min, max);
        }

        private static void AppendEditorLodsSection(StringBuilder sb, BatchState state)
        {
            if (!state.RemakeEditorLods)
                return;

            var r = state.EditorLods;
            var inv = CultureInfo.InvariantCulture;
            sb.AppendLine("Editor LOD remake");
            sb.AppendLine("-----------------");
            sb.AppendLine(
                $"Rigids examined (source-owned, reachable from a root): {r.Candidates:N0}; " +
                "settings per LOD: " + string.Join(
                    ", ",
                    Enumerable.Range(1, r.Settings.Count).Select(level =>
                        $"LOD{level}{(level == r.Settings.Count ? "+" : string.Empty)} {r.Settings[level - 1].TargetError.ToString("0.###", inv)} " +
                        $"{(r.Settings[level - 1].LockBorder ? "locked" : "free")}" +
                        (r.Settings[level - 1].Flags == EditorLodSimplifyFlags.None ? string.Empty : $" [{r.Settings[level - 1].Flags}]") +
                        (r.Settings[level - 1].AttributeScale == 1f ? string.Empty : $" attr x{r.Settings[level - 1].AttributeScale.ToString("0.###", inv)}") +
                        (r.Settings[level - 1].Method == EditorLodSimplifyMethod.Update ? " update" : string.Empty))));
            string Tiers(Dictionary<EditorLodEvidence, int> counts)
                => counts.Count == 0
                    ? "(none)"
                    : string.Join(", ", counts.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value:N0}"));
            sb.AppendLine($"Detected: {r.Detected.Values.Sum():N0} ({Tiers(r.Detected)})");
            sb.AppendLine($"Remade: {r.RigidsRemade:N0} ({Tiers(r.Remade)})");
            sb.AppendLine(
                "Skipped: " + (r.Skipped.Count == 0
                    ? "(none)"
                    : string.Join(", ", r.Skipped.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}={x.Value:N0}"))));
            sb.AppendLine(
                $"LODs replaced: {r.LodsReplaced:N0}; LODs kept because the rebuild was not lighter than the LOD above: {r.LodsKeptNotLighter:N0}");
            sb.AppendLine(
                $"Parts rebuilt: {r.PartsRebuilt:N0}; parts kept (merged part over 65535 vertices): {r.PartsKeptTooLarge:N0}; " +
                $"parts kept (too small to reduce): {r.PartsKeptNotReduced:N0}");
            sb.AppendLine(
                $"Rebuilt parts that missed the triangle target: error cap {r.PartsMissedTargetByError:N0}, topology/locked border {r.PartsMissedTargetByTopology:N0}");
            for (var level = 1; level < EditorLodMaxLevels; level++)
            {
                if (r.LodsReplacedByLevel[level] == 0)
                    continue;

                double Change(long before, long after) => before == 0 ? 0 : 100.0 * (after - before) / before;
                sb.AppendLine(
                    $"  LOD {level}: {r.LodsReplacedByLevel[level]:N0} replaced, " +
                    $"triangles {r.TrianglesBefore[level]:N0} -> {r.TrianglesAfter[level]:N0} " +
                    $"({Change(r.TrianglesBefore[level], r.TrianglesAfter[level]).ToString("+0.0;-0.0;0.0", inv)} %), " +
                    $"bytes {r.BytesBefore[level]:N0} -> {r.BytesAfter[level]:N0} " +
                    $"({Change(r.BytesBefore[level], r.BytesAfter[level]).ToString("+0.0;-0.0;0.0", inv)} %)");
            }

            if (r.BoneWeight > 0 || r.JointPriority)
            {
                sb.AppendLine(
                    $"Bone-aware rebuild: embedding weight {r.BoneWeight.ToString("0.###", inv)}, joint priority {(r.JointPriority ? "on" : "off")}; " +
                    $"{r.SkinnedPartsRebuilt:N0} skinned part(s) rebuilt with bone attributes");
            }

            if (state.EditorLodReportSkinningError)
            {
                sb.AppendLine("Skinning error (report only): L1 distance between a LOD0 vertex's bone weights and the weights interpolated at the closest point of the LOD surface; up to 128 sampled LOD0 vertices per skinned part");
                string Stat(List<float> values)
                {
                    if (values.Count == 0)
                        return "n/a";
                    var sorted = values.Order().ToArray();
                    return $"mean {sorted.Average().ToString("0.0000", inv)}, p95 {sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(sorted.Length * 0.95))].ToString("0.0000", inv)}, max {sorted[^1].ToString("0.0000", inv)} ({sorted.Length:N0} samples)";
                }

                for (var level = 1; level < EditorLodMaxLevels; level++)
                {
                    if (r.SourceComponents[level] != 0)
                    {
                        sb.AppendLine(
                            $"  LOD {level}: LOD0 components with no triangle left: {r.VanishedComponents[level]:N0} of {r.SourceComponents[level]:N0} " +
                            $"({r.VanishedComponentTriangles[level]:N0} LOD0 triangles)");
                    }

                    if (r.SkinningOld[level].Count == 0)
                        continue;

                    sb.AppendLine($"  LOD {level}: old {Stat(r.SkinningOld[level])}");
                    sb.AppendLine($"  LOD {level}: new {Stat(r.SkinningNew[level])}");
                }
            }

            var largest = r.Vanished.Where(x => x.Lod >= 3).OrderByDescending(x => x.Triangles).ThenBy(x => x.Rigid, StringComparer.Ordinal).Take(10).ToList();
            if (state.EditorLodReportSkinningError && largest.Count != 0)
            {
                sb.AppendLine("Largest LOD0 components with no triangle left at LOD3+ (rigid, LOD, part, LOD0 triangles, bounding size x/y/z):");
                foreach (var entry in largest)
                {
                    sb.AppendLine(
                        $"  {entry.Rigid} LOD {entry.Lod} part {entry.Part}: {entry.Triangles:N0} tris, " +
                        $"{entry.Size.X.ToString("0.###", inv)} x {entry.Size.Y.ToString("0.###", inv)} x {entry.Size.Z.ToString("0.###", inv)}");
                }
            }

            if (state.EditorLodReportSkinningError)
            {
                if (r.UpdatedParts != 0)
                {
                    sb.AppendLine(
                        $"Update method: {r.UpdatedParts:N0} part(s) rebuilt with meshopt_simplifyWithUpdate; seam groups snapped to one position " +
                        $"(meshoptimizer moved their members apart): {r.SeamGroupsSnapped:N0}" +
                        (state.EditorLodUpdateNearestWeights ? "; bone weights taken from the nearest LOD0 vertex" : string.Empty));
                }

                sb.AppendLine("Shape error (report only): distance between the LOD and the LOD0 surface; forward = LOD0 sample vertices to the LOD surface, reverse = LOD vertices to the LOD0 surface; relative = fraction of the part's LOD0 bounding diagonal; up to 128 samples per part");
                static string ShapeStat(List<float> values, string format)
                {
                    if (values.Count == 0)
                        return "n/a";
                    var sorted = values.Order().ToArray();
                    return $"mean {sorted.Average().ToString(format, CultureInfo.InvariantCulture)}, p95 {sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(sorted.Length * 0.95))].ToString(format, CultureInfo.InvariantCulture)}";
                }

                for (var level = 1; level < EditorLodMaxLevels; level++)
                {
                    if (r.ShapeOld[level].ForwardAbs.Count == 0)
                        continue;

                    foreach (var (label, samples) in new[] { ("old", r.ShapeOld[level]), ("new", r.ShapeNew[level]) })
                    {
                        sb.AppendLine(
                            $"  LOD {level} {label}: forward rel {ShapeStat(samples.ForwardRel, "0.00000")}, abs {ShapeStat(samples.ForwardAbs, "0.00000")}; " +
                            $"reverse rel {ShapeStat(samples.ReverseRel, "0.00000")}, abs {ShapeStat(samples.ReverseAbs, "0.00000")} ({samples.ForwardAbs.Count:N0} + {samples.ReverseAbs.Count:N0} samples)");
                    }
                }
            }

            if (r.NativeError != null)
                sb.AppendLine($"meshoptimizer could not be loaded: {r.NativeError}");
            sb.AppendLine();
        }
    }
}
