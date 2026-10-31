using System.Numerics;
using GameWorld.MeshOptimizer;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.Types;
using Shared.GameFormats.RigidModel.Vertex;

namespace Editors.PackOptimizer.Core
{
    public sealed record GeometryOptimizationStatistics(
        int VerticesBefore,
        int VerticesAfter,
        int IndicesBefore,
        int IndicesAfter,
        int DuplicateVerticesRemoved,
        int UnreferencedVerticesRemoved);

    public static class RigidGeometryOptimizer
    {
        public static GeometryOptimizationStatistics Optimize(RmvModel model)
        {
            var verticesBefore = model.Mesh.VertexList.Length;
            var indicesBefore = model.Mesh.IndexList.Length;

            if (model.Mesh.IndexList.Length % 3 != 0)
                throw new InvalidOperationException("Mesh index buffer length is not divisible by three.");

            // Preserve every source triangle, including degenerate/zero-area triangles.
            // They are part of the source topology and can be significant to downstream
            // tooling even when they rasterize no pixels. The optimizer may deduplicate
            // identical vertex payloads and reorder triangles for cache locality and
            // overdraw, but it must never change the index/triangle count.
            var sourceIndices = model.Mesh.IndexList;
            var referencedBeforeDedupe = new HashSet<ushort>(sourceIndices);
            var unreferencedVerticesRemoved = verticesBefore - referencedBeforeDedupe.Count;

            var deduplicatedVertices = new List<CommonVertex>(referencedBeforeDedupe.Count);
            var vertexMap = new Dictionary<CommonVertex, ushort>(new CommonVertexComparer());
            var remappedIndices = new ushort[sourceIndices.Length];

            for (var i = 0; i < sourceIndices.Length; i++)
            {
                var oldIndex = sourceIndices[i];
                if (oldIndex >= model.Mesh.VertexList.Length)
                    throw new InvalidOperationException($"Mesh contains invalid vertex index {oldIndex}.");

                var vertex = model.Mesh.VertexList[oldIndex];
                if (!vertexMap.TryGetValue(vertex, out var newIndex))
                {
                    if (deduplicatedVertices.Count > ushort.MaxValue)
                        throw new InvalidOperationException("Optimized mesh exceeds the 16-bit vertex index limit.");

                    newIndex = checked((ushort)deduplicatedVertices.Count);
                    vertexMap.Add(vertex, newIndex);
                    deduplicatedVertices.Add(vertex);
                }

                remappedIndices[i] = newIndex;
            }

            var duplicateVerticesRemoved =
                referencedBeforeDedupe.Count - deduplicatedVertices.Count;

            var vertexPositions = new float[checked(deduplicatedVertices.Count * 3)];
            for (var vertexIndex = 0; vertexIndex < deduplicatedVertices.Count; vertexIndex++)
            {
                var position = deduplicatedVertices[vertexIndex].Position;
                var positionOffset = vertexIndex * 3;
                vertexPositions[positionOffset] = position.X;
                vertexPositions[positionOffset + 1] = position.Y;
                vertexPositions[positionOffset + 2] = position.Z;
            }

            // Overdraw ordering is geometric and can change alpha-blended appearance.
            // This model-level optimizer has no WSModel shader assignment to classify it.
            var cacheAndOverdrawOptimizedIndices =
                MeshOptTriangleOrderOptimizer.OptimizeVertexCacheAndOverdraw(
                    remappedIndices,
                    vertexPositions);

            var (reorderedVertices, reorderedIndices) = ReorderVerticesByFirstUse(
                deduplicatedVertices,
                cacheAndOverdrawOptimizedIndices);

            if (reorderedIndices.Length != indicesBefore)
            {
                throw new InvalidOperationException(
                    $"Geometry optimization changed the index count from {indicesBefore} to {reorderedIndices.Length}.");
            }

            model.Mesh.VertexList = reorderedVertices;
            model.Mesh.IndexList = reorderedIndices;

            return new GeometryOptimizationStatistics(
                verticesBefore,
                reorderedVertices.Length,
                indicesBefore,
                reorderedIndices.Length,
                duplicateVerticesRemoved,
                unreferencedVerticesRemoved);
        }

        private static (CommonVertex[] Vertices, ushort[] Indices) ReorderVerticesByFirstUse(
            IReadOnlyList<CommonVertex> vertices,
            IReadOnlyList<ushort> indices)
        {
            var remap = new Dictionary<ushort, ushort>();
            var outputVertices = new List<CommonVertex>(vertices.Count);
            var outputIndices = new ushort[indices.Count];

            for (var i = 0; i < indices.Count; i++)
            {
                var oldIndex = indices[i];
                if (!remap.TryGetValue(oldIndex, out var newIndex))
                {
                    if (oldIndex >= vertices.Count)
                        throw new InvalidOperationException($"Mesh contains invalid vertex index {oldIndex}.");

                    if (outputVertices.Count > ushort.MaxValue)
                        throw new InvalidOperationException("Optimized mesh exceeds the 16-bit vertex index limit.");

                    newIndex = checked((ushort)outputVertices.Count);
                    remap.Add(oldIndex, newIndex);
                    outputVertices.Add(vertices[oldIndex]);
                }

                outputIndices[i] = newIndex;
            }

            return (outputVertices.ToArray(), outputIndices);
        }

        private sealed class CommonVertexComparer : IEqualityComparer<CommonVertex>
        {
            public bool Equals(CommonVertex? x, CommonVertex? y)
            {
                if (ReferenceEquals(x, y))
                    return true;
                if (x is null || y is null)
                    return false;

                return x.Position.Equals(y.Position) &&
                       x.Normal.Equals(y.Normal) &&
                       x.BiNormal.Equals(y.BiNormal) &&
                       x.Tangent.Equals(y.Tangent) &&
                       x.Uv.Equals(y.Uv) &&
                       x.Uv1.Equals(y.Uv1) &&
                       x.Colour.Equals(y.Colour) &&
                       SequenceEqual(x.BoneIndex, y.BoneIndex) &&
                       SequenceEqual(x.BoneWeight, y.BoneWeight) &&
                       x.WeightCount == y.WeightCount;
            }

            public int GetHashCode(CommonVertex obj)
            {
                var hash = new HashCode();
                hash.Add(obj.Position);
                hash.Add(obj.Normal);
                hash.Add(obj.BiNormal);
                hash.Add(obj.Tangent);
                hash.Add(obj.Uv);
                hash.Add(obj.Uv1);
                hash.Add(obj.Colour);
                AddSequence(ref hash, obj.BoneIndex);
                AddSequence(ref hash, obj.BoneWeight);
                hash.Add(obj.WeightCount);
                return hash.ToHashCode();
            }

            private static bool SequenceEqual<T>(T[]? left, T[]? right)
            {
                if (ReferenceEquals(left, right))
                    return true;
                if (left == null || right == null)
                    return false;
                return left.AsSpan().SequenceEqual(right);
            }

            private static void AddSequence<T>(ref HashCode hash, T[]? values)
            {
                if (values == null)
                {
                    hash.Add(-1);
                    return;
                }

                hash.Add(values.Length);
                foreach (var value in values)
                    hash.Add(value);
            }
        }
    }
}
