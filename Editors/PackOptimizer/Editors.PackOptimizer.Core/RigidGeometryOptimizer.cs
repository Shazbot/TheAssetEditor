using System.Numerics;
using GameWorld.MeshOptimizer;
using Shared.GameFormats.RigidModel;
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
            ArgumentNullException.ThrowIfNull(model);

            var sourceVertices = model.Mesh.VertexList;
            var sourceIndices = model.Mesh.IndexList;
            var verticesBefore = sourceVertices.Length;
            var indicesBefore = sourceIndices.Length;
            if (indicesBefore % 3 != 0)
                throw new InvalidOperationException("Mesh index buffer length is not divisible by three.");

            var referencedBeforeDedupe = new HashSet<ushort>();
            for (var i = 0; i < sourceIndices.Length; i++)
            {
                var index = sourceIndices[i];
                if (index >= verticesBefore)
                    throw new InvalidOperationException($"Mesh contains invalid vertex index {index}.");
                referencedBeforeDedupe.Add(index);
            }

            var vertexKeys = CommonVertexNativeKeyEncoder.Encode(sourceVertices);
            var vertexRemap = MeshOptTriangleOrderOptimizer.GenerateVertexRemap(
                sourceIndices,
                vertexKeys.Bytes,
                verticesBefore,
                vertexKeys.Stride);
            var duplicateVerticesRemoved = referencedBeforeDedupe.Count - vertexRemap.UniqueVertexCount;
            if (duplicateVerticesRemoved < 0)
                throw new InvalidOperationException("Native vertex remap returned more unique vertices than were referenced.");

            var remappedVertices = new CommonVertex[vertexRemap.UniqueVertexCount];
            var hasRepresentative = new bool[vertexRemap.UniqueVertexCount];
            foreach (var sourceIndex in sourceIndices)
            {
                var remappedIndex = vertexRemap.OldToNew[sourceIndex];
                if (remappedIndex == uint.MaxValue || remappedIndex >= (uint)remappedVertices.Length || remappedIndex > ushort.MaxValue)
                    throw new InvalidOperationException("Native vertex remap returned an invalid vertex for a referenced index.");

                var compactedIndex = (int)remappedIndex;
                if (!hasRepresentative[compactedIndex])
                {
                    remappedVertices[compactedIndex] = sourceVertices[sourceIndex];
                    hasRepresentative[compactedIndex] = true;
                }
            }

            if (hasRepresentative.Any(found => !found))
                throw new InvalidOperationException("Native vertex remap returned a unique vertex with no referenced representative.");

            // Filtering runs on the original full-payload keys so redundant faces are
            // identified before the vertex remap is applied to their surviving indices.
            // Removing repeated faces can change opacity accumulation for blended
            // materials; this model-level pass has no WSModel material classification.
            var filteredSourceIndices = MeshOptTriangleOrderOptimizer.FilterIndexBuffer(
                sourceIndices,
                vertexKeys.Bytes,
                verticesBefore,
                vertexKeys.Stride);
            if (filteredSourceIndices.Length > indicesBefore || filteredSourceIndices.Length % 3 != 0)
                throw new InvalidOperationException("Native triangle filtering returned an invalid index count.");

            var remappedFilteredIndices = new ushort[filteredSourceIndices.Length];
            for (var i = 0; i < filteredSourceIndices.Length; i++)
            {
                var remappedIndex = vertexRemap.OldToNew[filteredSourceIndices[i]];
                if (remappedIndex == uint.MaxValue || remappedIndex >= (uint)remappedVertices.Length || remappedIndex > ushort.MaxValue)
                    throw new InvalidOperationException("Native vertex remap returned an invalid vertex for a surviving triangle.");
                remappedFilteredIndices[i] = (ushort)remappedIndex;
            }

            var vertexPositions = new float[checked(remappedVertices.Length * 3)];
            for (var vertexIndex = 0; vertexIndex < remappedVertices.Length; vertexIndex++)
            {
                var position = remappedVertices[vertexIndex].Position;
                var positionOffset = vertexIndex * 3;
                vertexPositions[positionOffset] = position.X;
                vertexPositions[positionOffset + 1] = position.Y;
                vertexPositions[positionOffset + 2] = position.Z;
            }

            // This model-level optimizer has no WSModel shader assignment to classify
            // alpha blending, so the existing geometric overdraw-ordering risk remains.
            var cacheAndOverdrawOptimizedIndices =
                MeshOptTriangleOrderOptimizer.OptimizeVertexCacheAndOverdraw(
                    remappedFilteredIndices,
                    vertexPositions);

            var vertexFetchResult = MeshOptTriangleOrderOptimizer.OptimizeVertexFetch(
                cacheAndOverdrawOptimizedIndices,
                remappedVertices.Length);
            var reorderedVertices = new CommonVertex[vertexFetchResult.SourceVertexIndices.Length];
            for (var vertexIndex = 0; vertexIndex < reorderedVertices.Length; vertexIndex++)
            {
                var sourceVertexIndex = vertexFetchResult.SourceVertexIndices[vertexIndex];
                if ((uint)sourceVertexIndex >= (uint)remappedVertices.Length)
                    throw new InvalidOperationException("Native vertex fetch returned an invalid source vertex.");
                reorderedVertices[vertexIndex] = remappedVertices[sourceVertexIndex];
            }

            var reorderedIndices = vertexFetchResult.Indices;
            if (reorderedIndices.Length > indicesBefore || reorderedIndices.Length % 3 != 0)
                throw new InvalidOperationException("Geometry optimization returned an invalid index count.");
            foreach (var index in reorderedIndices)
            {
                if (index >= reorderedVertices.Length)
                    throw new InvalidOperationException("Geometry optimization returned an index outside the compacted vertex array.");
            }

            var unreferencedVerticesRemoved = verticesBefore - reorderedVertices.Length - duplicateVerticesRemoved;
            if (unreferencedVerticesRemoved < 0)
                throw new InvalidOperationException("Geometry optimization reported more vertices removed than existed in the input.");

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
    }
}
