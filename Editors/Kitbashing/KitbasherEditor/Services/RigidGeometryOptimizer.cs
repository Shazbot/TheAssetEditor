using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.Types;
using Shared.GameFormats.RigidModel.Vertex;

namespace Editors.KitbasherEditor.Services
{
    public sealed record GeometryOptimizationStatistics(
        int VerticesBefore,
        int VerticesAfter,
        int IndicesBefore,
        int IndicesAfter,
        int DegenerateTrianglesRemoved,
        int DuplicateVerticesRemoved,
        int UnreferencedVerticesRemoved);

    public static class RigidGeometryOptimizer
    {
        private const int VertexCacheSize = 32;

        public static GeometryOptimizationStatistics Optimize(RmvModel model)
        {
            var verticesBefore = model.Mesh.VertexList.Length;
            var indicesBefore = model.Mesh.IndexList.Length;

            var filteredIndices = RemoveDegenerateTriangles(
                model.Mesh.VertexList,
                model.Mesh.IndexList,
                out var degenerateTrianglesRemoved);

            var referencedBeforeDedupe = new HashSet<ushort>(filteredIndices);
            var unreferencedVerticesRemoved = verticesBefore - referencedBeforeDedupe.Count;

            var deduplicatedVertices = new List<CommonVertex>(referencedBeforeDedupe.Count);
            var vertexMap = new Dictionary<CommonVertex, ushort>(new CommonVertexComparer());
            var remappedIndices = new ushort[filteredIndices.Length];

            for (var i = 0; i < filteredIndices.Length; i++)
            {
                var oldIndex = filteredIndices[i];
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

            var cacheOptimizedIndices = OptimizeTriangleOrder(
                remappedIndices,
                deduplicatedVertices.Count);

            var (reorderedVertices, reorderedIndices) = ReorderVerticesByFirstUse(
                deduplicatedVertices,
                cacheOptimizedIndices);

            model.Mesh.VertexList = reorderedVertices;
            model.Mesh.IndexList = reorderedIndices;

            return new GeometryOptimizationStatistics(
                verticesBefore,
                reorderedVertices.Length,
                indicesBefore,
                reorderedIndices.Length,
                degenerateTrianglesRemoved,
                duplicateVerticesRemoved,
                unreferencedVerticesRemoved);
        }

        private static ushort[] RemoveDegenerateTriangles(
            IReadOnlyList<CommonVertex> vertices,
            IReadOnlyList<ushort> indices,
            out int removedTriangleCount)
        {
            if (indices.Count % 3 != 0)
                throw new InvalidOperationException("Mesh index buffer length is not divisible by three.");

            var output = new List<ushort>(indices.Count);
            removedTriangleCount = 0;

            for (var i = 0; i < indices.Count; i += 3)
            {
                var a = indices[i];
                var b = indices[i + 1];
                var c = indices[i + 2];

                if (a >= vertices.Count || b >= vertices.Count || c >= vertices.Count)
                    throw new InvalidOperationException("Mesh contains an invalid vertex index.");

                if (a == b || b == c || a == c ||
                    SamePosition(vertices[a], vertices[b]) ||
                    SamePosition(vertices[b], vertices[c]) ||
                    SamePosition(vertices[a], vertices[c]))
                {
                    removedTriangleCount++;
                    continue;
                }

                output.Add(a);
                output.Add(b);
                output.Add(c);
            }

            return output.ToArray();
        }

        private static bool SamePosition(CommonVertex left, CommonVertex right)
            => left.Position.X == right.Position.X &&
               left.Position.Y == right.Position.Y &&
               left.Position.Z == right.Position.Z &&
               left.Position.W == right.Position.W;

        private static ushort[] OptimizeTriangleOrder(
            IReadOnlyList<ushort> indices,
            int vertexCount)
        {
            var triangleCount = indices.Count / 3;
            if (triangleCount <= 1)
                return indices.ToArray();

            var adjacency = new List<int>[vertexCount];
            for (var i = 0; i < adjacency.Length; i++)
                adjacency[i] = [];

            for (var triangle = 0; triangle < triangleCount; triangle++)
            {
                var baseIndex = triangle * 3;
                adjacency[indices[baseIndex]].Add(triangle);
                adjacency[indices[baseIndex + 1]].Add(triangle);
                adjacency[indices[baseIndex + 2]].Add(triangle);
            }

            var emitted = new bool[triangleCount];
            var remainingValence = adjacency.Select(x => x.Count).ToArray();
            var cache = new List<ushort>(VertexCacheSize);
            var output = new ushort[indices.Count];
            var outputIndex = 0;
            var nextLinearTriangle = 0;
            var candidates = new HashSet<int>();

            for (var emittedCount = 0; emittedCount < triangleCount; emittedCount++)
            {
                var bestTriangle = -1;
                var bestScore = int.MinValue;

                foreach (var candidate in candidates)
                {
                    if (emitted[candidate])
                        continue;

                    var baseIndex = candidate * 3;
                    var a = indices[baseIndex];
                    var b = indices[baseIndex + 1];
                    var c = indices[baseIndex + 2];

                    var cacheHits =
                        (cache.Contains(a) ? 1 : 0) +
                        (cache.Contains(b) ? 1 : 0) +
                        (cache.Contains(c) ? 1 : 0);

                    var valence =
                        remainingValence[a] +
                        remainingValence[b] +
                        remainingValence[c];

                    var score = cacheHits * 100000 - valence;
                    if (score > bestScore ||
                        (score == bestScore && candidate < bestTriangle))
                    {
                        bestTriangle = candidate;
                        bestScore = score;
                    }
                }

                if (bestTriangle < 0)
                {
                    while (nextLinearTriangle < triangleCount &&
                           emitted[nextLinearTriangle])
                    {
                        nextLinearTriangle++;
                    }

                    bestTriangle = nextLinearTriangle;
                }

                var triangleBase = bestTriangle * 3;
                var triangleVertices = new[]
                {
                    indices[triangleBase],
                    indices[triangleBase + 1],
                    indices[triangleBase + 2]
                };

                output[outputIndex++] = triangleVertices[0];
                output[outputIndex++] = triangleVertices[1];
                output[outputIndex++] = triangleVertices[2];
                emitted[bestTriangle] = true;
                candidates.Remove(bestTriangle);

                foreach (var vertex in triangleVertices)
                    remainingValence[vertex]--;

                TouchCache(cache, triangleVertices);

                candidates.Clear();
                foreach (var vertex in cache)
                {
                    foreach (var triangle in adjacency[vertex])
                    {
                        if (!emitted[triangle])
                            candidates.Add(triangle);
                    }
                }
            }

            return output;
        }

        private static void TouchCache(List<ushort> cache, IReadOnlyList<ushort> vertices)
        {
            for (var i = vertices.Count - 1; i >= 0; i--)
            {
                var vertex = vertices[i];
                cache.Remove(vertex);
                cache.Insert(0, vertex);
            }

            if (cache.Count > VertexCacheSize)
                cache.RemoveRange(VertexCacheSize, cache.Count - VertexCacheSize);
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
                AddSequence(hash, obj.BoneIndex);
                AddSequence(hash, obj.BoneWeight);
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

            private static void AddSequence<T>(HashCode hash, T[]? values)
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
