using System.IO;
using GameWorld.Core.Rendering;
using GameWorld.Core.Rendering.Geometry;

namespace GameWorld.Core.Services.SceneSaving.Lod.MeshOptimizerIntegration;

public sealed record MeshOptReducerOptions
{
    /// <summary>Maximum simplification error relative to the mesh extents.</summary>
    public float TargetError { get; init; } = 0.02f;
    public bool LockBorder { get; init; } = true;
    public bool Prune { get; init; }
    public bool Regularize { get; init; }
    public float NormalWeight { get; init; } = 0.5f;
    public float TextureCoordinateWeight { get; init; } = 1.0f;
}

public sealed record MeshOptReducerStats(
    int SourceTriangleCount,
    int RequestedTriangleCount,
    int AchievedTriangleCount,
    bool TargetReached,
    string LimitReason,
    int DroppedDegenerateTriangles,
    float ResultErrorRelative,
    float ResultErrorAbsolute,
    bool ErrorLimited,
    bool ReductionSkipped,
    string? SkipReason);

public sealed record MeshOptReducerResult(MeshObject Mesh, MeshOptReducerStats Stats);

public static class MeshOptReducer
{
    private const int MinimumTriangleCount = 12;
    private const int AttributeCount = 5;
    private const nuint PositionStride = 3 * sizeof(float);
    private const nuint AttributeStride = AttributeCount * sizeof(float);

    /// <summary>
    /// Reduces a mesh and returns a copy containing only original vertex records and a new index buffer.
    /// Use <see cref="Reduce"/> when the requested/achieved counts and error statistics are needed.
    /// </summary>
    public static MeshObject GetReducedMeshCopy(MeshObject original, float factor, MeshOptReducerOptions? options = null)
        => Reduce(original, factor, options).Mesh;

    public static MeshOptReducerResult Reduce(MeshObject original, float factor, MeshOptReducerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (!float.IsFinite(factor))
            throw new ArgumentOutOfRangeException(nameof(factor), "The reduction factor must be finite.");
        if (factor < 0)
            throw new ArgumentOutOfRangeException(nameof(factor), "The reduction factor cannot be negative.");

        options ??= new MeshOptReducerOptions();
        ValidateOptions(options);

        var vertices = original.VertexArray;
        var indices = original.IndexArray;
        var sourceTriangleCount = indices.Length / 3;
        var requestedIndexCount = factor >= 1.0f
            ? indices.Length
            : CalculateTargetIndexCount(indices.Length, factor);
        var requestedTriangleCount = requestedIndexCount / 3;

        if (factor >= 1.0f)
            return ReturnUnchanged(original, sourceTriangleCount, sourceTriangleCount, 0, true, "A reduction factor of 1 or greater leaves the mesh unchanged.");

        if (indices.Length < 3 || indices.Length % 3 != 0)
            return ReturnUnchanged(original, sourceTriangleCount, requestedTriangleCount, 0, true, "The index buffer is empty or is not a sequence of triangles.");

        var inputValidationError = ValidateInput(vertices, indices);
        if (inputValidationError is not null)
            return ReturnUnchanged(original, sourceTriangleCount, requestedTriangleCount, 0, true, inputValidationError);

        if (vertices.Length == 0)
            return ReturnUnchanged(original, sourceTriangleCount, requestedTriangleCount, 0, true, "The source vertex buffer is empty.");

        var nativeIndices = new uint[indices.Length];
        var nativeIndexCount = 0;
        var droppedDegenerateTriangles = 0;
        for (var i = 0; i < indices.Length; i += 3)
        {
            var index0 = indices[i];
            var index1 = indices[i + 1];
            var index2 = indices[i + 2];
            if (index0 == index1 || index1 == index2 || index2 == index0)
            {
                droppedDegenerateTriangles++;
                continue;
            }

            nativeIndices[nativeIndexCount++] = index0;
            nativeIndices[nativeIndexCount++] = index1;
            nativeIndices[nativeIndexCount++] = index2;
        }

        if (nativeIndexCount < 3)
            return ReturnUnchanged(original, sourceTriangleCount, requestedTriangleCount, droppedDegenerateTriangles, true, "No non-repeated-index triangles remain for simplification.");

        if (nativeIndexCount / 3 < MinimumTriangleCount)
            return ReturnUnchanged(original, sourceTriangleCount, requestedTriangleCount, droppedDegenerateTriangles, true, $"Fewer than {MinimumTriangleCount} non-degenerate triangles remain after filtering repeated-index triangles.");

        if (nativeIndexCount != nativeIndices.Length)
            Array.Resize(ref nativeIndices, nativeIndexCount);

        var nativeRequestedIndexCount = Math.Min(requestedIndexCount, nativeIndexCount);
        nativeRequestedIndexCount -= nativeRequestedIndexCount % 3;
        if (nativeRequestedIndexCount < 3)
            return ReturnUnchanged(original, sourceTriangleCount, requestedTriangleCount, droppedDegenerateTriangles, true, "The requested simplification target contains no complete triangle.");

        var positions = new float[checked(vertices.Length * 3)];
        var attributes = new float[checked(vertices.Length * AttributeCount)];
        GetTextureCoordinateBounds(vertices, out var minU, out var maxU, out var minV, out var maxV);

        for (var i = 0; i < vertices.Length; i++)
        {
            var vertex = vertices[i];
            var positionOffset = i * 3;
            positions[positionOffset] = vertex.Position.X;
            positions[positionOffset + 1] = vertex.Position.Y;
            positions[positionOffset + 2] = vertex.Position.Z;

            var attributeOffset = i * AttributeCount;
            attributes[attributeOffset] = vertex.Normal.X;
            attributes[attributeOffset + 1] = vertex.Normal.Y;
            attributes[attributeOffset + 2] = vertex.Normal.Z;
            attributes[attributeOffset + 3] = vertex.TextureCoordinate.X;
            attributes[attributeOffset + 4] = vertex.TextureCoordinate.Y;
        }

        // meshoptimizer.h v1.3, meshopt_simplifyWithAttributes comments (lines 531-537),
        // define weights as each attribute's priority relative to position and cap the
        // attribute count at 32. Normal components span about 2, so 0.5 per component
        // gives them a unit range. Following meshoptimizer's scale guidance, divide the
        // UV base weight by each observed UV-axis range so a full UV span is comparable
        // to the normalized position extent; constant axes get zero weight.
        var attributeWeights = new[]
        {
            options.NormalWeight,
            options.NormalWeight,
            options.NormalWeight,
            ScaleTextureCoordinateWeight(options.TextureCoordinateWeight, (double)maxU - minU),
            ScaleTextureCoordinateWeight(options.TextureCoordinateWeight, (double)maxV - minV)
        };

        var simplifyFlags = 0u;
        if (options.LockBorder)
            simplifyFlags |= MeshOptNative.SimplifyLockBorder;
        if (options.Prune)
            simplifyFlags |= MeshOptNative.SimplifyPrune;
        if (options.Regularize)
            simplifyFlags |= MeshOptNative.SimplifyRegularize;

        var simplifiedIndices = new uint[nativeIndexCount];
        var optimizedIndices = new uint[nativeIndexCount];
        var vertexRemap = new uint[vertices.Length];
        Array.Fill(vertexRemap, uint.MaxValue);
        nuint simplifiedIndexCount;
        float resultErrorRelative;
        float meshScale;

        unsafe
        {
            fixed (uint* indicesPointer = nativeIndices)
            fixed (float* positionsPointer = positions)
            fixed (float* attributesPointer = attributes)
            fixed (float* weightsPointer = attributeWeights)
            fixed (uint* simplifiedIndicesPointer = simplifiedIndices)
            fixed (uint* optimizedIndicesPointer = optimizedIndices)
            fixed (uint* remapPointer = vertexRemap)
            {
                simplifiedIndexCount = MeshOptNative.SimplifyWithAttributes(
                    simplifiedIndicesPointer,
                    indicesPointer,
                    (nuint)nativeIndexCount,
                    positionsPointer,
                    (nuint)vertices.Length,
                    PositionStride,
                    attributesPointer,
                    AttributeStride,
                    weightsPointer,
                    AttributeCount,
                    null,
                    (nuint)nativeRequestedIndexCount,
                    options.TargetError,
                    simplifyFlags,
                    &resultErrorRelative);

                if (simplifiedIndexCount > (nuint)simplifiedIndices.Length)
                    throw new InvalidDataException("meshopt_simplifyWithAttributes returned more indices than its destination buffer can hold.");

                if (simplifiedIndexCount == 0)
                    return ReturnUnchanged(original, sourceTriangleCount, requestedTriangleCount, droppedDegenerateTriangles, true, "meshopt_simplifyWithAttributes returned no triangles.");

                if (simplifiedIndexCount % 3 != 0)
                    throw new InvalidDataException("meshopt_simplifyWithAttributes returned an invalid triangle index count.");

                MeshOptNative.OptimizeVertexCache(
                    optimizedIndicesPointer,
                    simplifiedIndicesPointer,
                    simplifiedIndexCount,
                    (nuint)vertices.Length);

                var compactVertexCount = MeshOptNative.OptimizeVertexFetchRemap(
                    remapPointer,
                    optimizedIndicesPointer,
                    simplifiedIndexCount,
                    (nuint)vertices.Length);

                if (compactVertexCount > (nuint)vertices.Length)
                    throw new InvalidDataException("meshopt_optimizeVertexFetchRemap returned more vertices than the input buffer contains.");

                meshScale = MeshOptNative.SimplifyScale(positionsPointer, (nuint)vertices.Length, PositionStride);

                // Rewrite optimized indices in place through the old-vertex -> fetched-vertex remap.
                for (var i = 0; i < (int)simplifiedIndexCount; i++)
                {
                    var oldIndex = optimizedIndices[i];
                    if (oldIndex >= (uint)vertexRemap.Length)
                        throw new InvalidDataException("The optimized index buffer references a vertex outside the original vertex buffer.");

                    var newIndex = vertexRemap[oldIndex];
                    if (newIndex == uint.MaxValue || newIndex >= compactVertexCount || newIndex > ushort.MaxValue)
                        throw new InvalidDataException("The optimized vertex remap cannot be represented by a ushort index.");

                    optimizedIndices[i] = newIndex;
                }

                if (compactVertexCount > (nuint)(ushort.MaxValue + 1))
                    throw new InvalidDataException("The reduced mesh contains more vertices than a ushort index buffer can address.");

                var outputVertices = new VertexPositionNormalTextureCustom[(int)compactVertexCount];
                for (var oldVertexIndex = 0; oldVertexIndex < vertexRemap.Length; oldVertexIndex++)
                {
                    var newVertexIndex = vertexRemap[oldVertexIndex];
                    if (newVertexIndex == uint.MaxValue)
                        continue;

                    if (newVertexIndex >= compactVertexCount)
                        throw new InvalidDataException("The optimized vertex remap contains an invalid vertex index.");

                    outputVertices[newVertexIndex] = vertices[oldVertexIndex];
                }

                var outputIndices = new ushort[(int)simplifiedIndexCount];
                for (var i = 0; i < outputIndices.Length; i++)
                    outputIndices[i] = checked((ushort)optimizedIndices[i]);

                if (!float.IsFinite(resultErrorRelative) || !float.IsFinite(meshScale) || meshScale < 0)
                    throw new InvalidDataException("meshoptimizer returned a non-finite simplification error or mesh scale.");

                var reducedMesh = CloneWithBuffers(original, outputVertices, outputIndices);
                var achievedTriangleCount = outputIndices.Length / 3;
                var errorLimited = IsErrorLimited(achievedTriangleCount, requestedTriangleCount, resultErrorRelative, options.TargetError);
                var targetReached = achievedTriangleCount <= requestedTriangleCount;
                var stats = new MeshOptReducerStats(
                    sourceTriangleCount,
                    requestedTriangleCount,
                    achievedTriangleCount,
                    targetReached,
                    GetLimitReason(targetReached, errorLimited),
                    droppedDegenerateTriangles,
                    resultErrorRelative,
                    // meshoptimizer.h v1.3, meshopt_simplifyScale comments (lines 610-614):
                    // relative result_error is converted to absolute units by multiplying by this scale.
                    resultErrorRelative * meshScale,
                    errorLimited,
                    false,
                    null);

                return new MeshOptReducerResult(reducedMesh, stats);
            }
        }
    }

    private static int CalculateTargetIndexCount(int indexCount, float factor)
    {
        var targetTriangleCount = (int)Math.Round(indexCount * (double)factor / 3.0, MidpointRounding.AwayFromZero);
        return Math.Max(3, checked(targetTriangleCount * 3));
    }

    private static string? ValidateInput(VertexPositionNormalTextureCustom[] vertices, ushort[] indices)
    {
        for (var i = 0; i < vertices.Length; i++)
        {
            var vertex = vertices[i];
            if (!float.IsFinite(vertex.Position.X) || !float.IsFinite(vertex.Position.Y) || !float.IsFinite(vertex.Position.Z) ||
                !float.IsFinite(vertex.Normal.X) || !float.IsFinite(vertex.Normal.Y) || !float.IsFinite(vertex.Normal.Z) ||
                !float.IsFinite(vertex.TextureCoordinate.X) || !float.IsFinite(vertex.TextureCoordinate.Y))
            {
                return $"Vertex {i} contains a non-finite position, normal, or UV value.";
            }
        }

        for (var i = 0; i < indices.Length; i += 3)
        {
            var index0 = indices[i];
            var index1 = indices[i + 1];
            var index2 = indices[i + 2];
            if (index0 >= vertices.Length || index1 >= vertices.Length || index2 >= vertices.Length)
                return $"Triangle {i / 3} references a vertex outside the source vertex buffer.";

        }

        return null;
    }

    private static void GetTextureCoordinateBounds(VertexPositionNormalTextureCustom[] vertices, out float minU, out float maxU, out float minV, out float maxV)
    {
        minU = float.PositiveInfinity;
        maxU = float.NegativeInfinity;
        minV = float.PositiveInfinity;
        maxV = float.NegativeInfinity;

        foreach (var vertex in vertices)
        {
            minU = MathF.Min(minU, vertex.TextureCoordinate.X);
            maxU = MathF.Max(maxU, vertex.TextureCoordinate.X);
            minV = MathF.Min(minV, vertex.TextureCoordinate.Y);
            maxV = MathF.Max(maxV, vertex.TextureCoordinate.Y);
        }
    }

    private static float ScaleTextureCoordinateWeight(float baseWeight, double range)
    {
        if (baseWeight == 0 || range == 0)
            return 0;

        var scaledWeight = baseWeight / range;
        if (!double.IsFinite(scaledWeight) || scaledWeight > float.MaxValue)
            return 0;

        return (float)scaledWeight;
    }

    private static bool IsErrorLimited(int achievedTriangleCount, int requestedTriangleCount, float resultError, float targetError)
    {
        if (achievedTriangleCount <= requestedTriangleCount)
            return false;

        var tolerance = MathF.Max(1.0e-6f, targetError * 1.0e-5f);
        return targetError == 0 ? resultError > 0 : resultError + tolerance >= targetError;
    }

    private static string GetLimitReason(bool targetReached, bool errorLimited)
        => targetReached ? "none" : errorLimited ? "error" : "topology";

    private static void ValidateOptions(MeshOptReducerOptions options)
    {
        if (!float.IsFinite(options.TargetError) || options.TargetError < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "TargetError must be a finite, non-negative relative error.");
        if (!float.IsFinite(options.NormalWeight) || options.NormalWeight < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "NormalWeight must be finite and non-negative.");
        if (!float.IsFinite(options.TextureCoordinateWeight) || options.TextureCoordinateWeight < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "TextureCoordinateWeight must be finite and non-negative.");
    }

    private static MeshOptReducerResult ReturnUnchanged(MeshObject original, int sourceTriangleCount, int requestedTriangleCount, int droppedDegenerateTriangles, bool skipped, string? reason)
    {
        var unchanged = CloneWithBuffers(
            original,
            (VertexPositionNormalTextureCustom[])original.VertexArray.Clone(),
            (ushort[])original.IndexArray.Clone());
        var targetReached = sourceTriangleCount <= requestedTriangleCount;
        return new MeshOptReducerResult(
            unchanged,
            new MeshOptReducerStats(sourceTriangleCount, requestedTriangleCount, sourceTriangleCount, targetReached,
                GetLimitReason(targetReached, false), droppedDegenerateTriangles, 0, 0, false, skipped, reason));
    }

    private static MeshObject CloneWithBuffers(MeshObject original, VertexPositionNormalTextureCustom[] vertices, ushort[] indices)
    {
        var clone = original.Clone(false);
        clone.IndexArray = indices;
        clone.VertexArray = vertices;
        clone.RebuildIndexBuffer();
        clone.RebuildVertexBuffer();
        return clone;
    }
}
