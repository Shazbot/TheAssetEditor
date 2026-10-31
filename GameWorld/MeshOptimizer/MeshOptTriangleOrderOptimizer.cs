using System.IO;

namespace GameWorld.MeshOptimizer;

/// <summary>
/// The native vertex-fetch result. SourceVertexIndices maps each new vertex index to its
/// index in the input vertex array.
/// </summary>
public sealed record MeshOptVertexFetchResult(ushort[] Indices, int[] SourceVertexIndices);

/// <summary>
/// Reorders triangle indices for vertex-cache locality and then overdraw.
/// </summary>
public static class MeshOptTriangleOrderOptimizer
{
    private const nuint VertexPositionStride = 3 * sizeof(float);
    private const float OverdrawThreshold = 1.05f;

    /// <summary>
    /// Runs meshoptimizer's vertex-cache optimizer followed by its overdraw optimizer.
    /// Positions are packed as three floats (x, y, z) per vertex.
    /// </summary>
    public static ushort[] OptimizeVertexCacheAndOverdraw(ushort[] indices, float[] vertexPositions)
    {
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(vertexPositions);

        if (indices.Length % 3 != 0)
            throw new ArgumentException("Index buffer length must be divisible by three.", nameof(indices));

        if (vertexPositions.Length % 3 != 0)
            throw new ArgumentException("Vertex positions must contain packed float3 values.", nameof(vertexPositions));

        if (indices.Length == 0)
            return [];

        for (var i = 0; i < vertexPositions.Length; i++)
        {
            if (!float.IsFinite(vertexPositions[i]))
            {
                throw new ArgumentException(
                    $"Vertex position component at offset {i} is not finite.",
                    nameof(vertexPositions));
            }
        }

        var vertexCount = vertexPositions.Length / 3;
        var nativeIndices = new uint[indices.Length];
        for (var i = 0; i < indices.Length; i++)
        {
            var index = indices[i];
            if (index >= vertexCount)
            {
                throw new ArgumentException(
                    $"Index {index} at offset {i} is outside the {vertexCount}-vertex position buffer.",
                    nameof(indices));
            }

            nativeIndices[i] = index;
        }

        var cacheOptimizedIndices = new uint[indices.Length];
        var overdrawOptimizedIndices = new uint[indices.Length];

        unsafe
        {
            fixed (uint* nativeIndicesPointer = nativeIndices)
            fixed (uint* cacheOptimizedIndicesPointer = cacheOptimizedIndices)
            fixed (uint* overdrawOptimizedIndicesPointer = overdrawOptimizedIndices)
            fixed (float* vertexPositionsPointer = vertexPositions)
            {
                MeshOptNative.OptimizeVertexCache(
                    cacheOptimizedIndicesPointer,
                    nativeIndicesPointer,
                    (nuint)nativeIndices.Length,
                    (nuint)vertexCount);

                MeshOptNative.OptimizeOverdraw(
                    overdrawOptimizedIndicesPointer,
                    cacheOptimizedIndicesPointer,
                    (nuint)cacheOptimizedIndices.Length,
                    vertexPositionsPointer,
                    (nuint)vertexCount,
                    VertexPositionStride,
                    OverdrawThreshold);
            }
        }

        var result = new ushort[indices.Length];
        for (var i = 0; i < result.Length; i++)
        {
            var index = overdrawOptimizedIndices[i];
            if (index >= (uint)vertexCount || index > ushort.MaxValue)
            {
                throw new InvalidDataException(
                    "meshoptimizer returned an index outside the source vertex buffer.");
            }

            result[i] = (ushort)index;
        }

        return result;
    }

    /// <summary>
    /// Applies meshoptimizer's vertex-fetch order to a triangle index buffer and returns
    /// the new-to-source vertex mapping. Vertex data itself must be reordered by the caller.
    /// </summary>
    public static MeshOptVertexFetchResult OptimizeVertexFetch(ushort[] indices, int vertexCount)
    {
        ArgumentNullException.ThrowIfNull(indices);

        if (vertexCount < 0 || vertexCount > ushort.MaxValue + 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(vertexCount),
                $"Vertex count must be between zero and {ushort.MaxValue + 1}.");
        }

        if (indices.Length % 3 != 0)
            throw new ArgumentException("Index buffer length must be divisible by three.", nameof(indices));

        if (indices.Length == 0)
            return new MeshOptVertexFetchResult([], []);

        var nativeIndices = new uint[indices.Length];
        for (var i = 0; i < indices.Length; i++)
        {
            var index = indices[i];
            if (index >= vertexCount)
            {
                throw new ArgumentException(
                    $"Index {index} at offset {i} is outside the {vertexCount}-vertex input buffer.",
                    nameof(indices));
            }

            nativeIndices[i] = index;
        }

        var sourceVertexIds = new uint[vertexCount];
        for (var i = 0; i < sourceVertexIds.Length; i++)
            sourceVertexIds[i] = (uint)i;

        var fetchedSourceVertexIds = new uint[vertexCount];
        nuint fetchedVertexCount;

        unsafe
        {
            fixed (uint* nativeIndicesPointer = nativeIndices)
            fixed (uint* sourceVertexIdsPointer = sourceVertexIds)
            fixed (uint* fetchedSourceVertexIdsPointer = fetchedSourceVertexIds)
            {
                fetchedVertexCount = MeshOptNative.OptimizeVertexFetch(
                    fetchedSourceVertexIdsPointer,
                    nativeIndicesPointer,
                    (nuint)nativeIndices.Length,
                    sourceVertexIdsPointer,
                    (nuint)sourceVertexIds.Length,
                    (nuint)sizeof(uint));
            }
        }

        if (fetchedVertexCount > (nuint)vertexCount)
            throw new InvalidDataException("meshoptimizer returned more vertices than the input buffer contains.");

        var sourceVertexIndices = new int[(int)fetchedVertexCount];
        var seenSourceVertices = new bool[vertexCount];
        for (var i = 0; i < sourceVertexIndices.Length; i++)
        {
            var sourceVertex = fetchedSourceVertexIds[i];
            if (sourceVertex >= (uint)vertexCount || seenSourceVertices[(int)sourceVertex])
            {
                throw new InvalidDataException(
                    "meshoptimizer returned an invalid or duplicate source vertex in its fetch order.");
            }

            seenSourceVertices[(int)sourceVertex] = true;
            sourceVertexIndices[i] = (int)sourceVertex;
        }

        var remappedIndices = new ushort[indices.Length];
        for (var i = 0; i < remappedIndices.Length; i++)
        {
            var remappedIndex = nativeIndices[i];
            if ((nuint)remappedIndex >= fetchedVertexCount || remappedIndex > ushort.MaxValue)
            {
                throw new InvalidDataException(
                    "meshoptimizer returned an index outside the fetched vertex buffer.");
            }

            remappedIndices[i] = (ushort)remappedIndex;
        }

        return new MeshOptVertexFetchResult(remappedIndices, sourceVertexIndices);
    }
}
