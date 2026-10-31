using System.IO;

namespace GameWorld.MeshOptimizer;

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
}
