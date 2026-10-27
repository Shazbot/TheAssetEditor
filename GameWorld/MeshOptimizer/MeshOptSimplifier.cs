using System.IO;

namespace GameWorld.MeshOptimizer;

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

/// <summary>
/// Simplification result over plain arrays.
/// <see cref="Remap"/> maps each source vertex to its index in the compacted output, or <see cref="uint.MaxValue"/>
/// when the vertex is no longer used. <see cref="VertexCount"/> is the compact output vertex count.
/// When the reduction was skipped, <see cref="Indices"/> is a copy of the input and <see cref="Remap"/> is the identity.
/// </summary>
public sealed record MeshOptSimplifyResult(ushort[] Indices, uint[] Remap, int VertexCount, MeshOptReducerStats Stats);

public static class MeshOptSimplifier
{
    private const int MinimumTriangleCount = 12;
    private const int AttributeCount = 5;
    private const nuint PositionStride = 3 * sizeof(float);
    private const nuint AttributeStride = AttributeCount * sizeof(float);

    /// <summary>Null when the native meshoptimizer library loads and answers; otherwise why not.</summary>
    public static string? GetNativeLibraryError()
    {
        try
        {
            unsafe
            {
                var positions = stackalloc float[] { 0, 0, 0, 1, 1, 1 };
                if (!float.IsFinite(MeshOptNative.SimplifyScale(positions, 2, PositionStride)))
                    return "meshoptimizer.dll answered with a non-finite scale.";
            }

            return null;
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            return $"The native meshoptimizer library (meshoptimizer.dll) could not be loaded: {exception.Message}";
        }
    }

    /// <param name="positions">xyz per vertex.</param>
    /// <param name="normals">xyz per vertex, or empty to ignore normals.</param>
    /// <param name="uvs">uv per vertex, or empty to ignore texture coordinates.</param>
    /// <param name="indices">Triangle list.</param>
    /// <param name="factor">Triangle count factor; 1 or more leaves the mesh unchanged.</param>
    public static MeshOptSimplifyResult Simplify(
        ReadOnlySpan<float> positions,
        ReadOnlySpan<float> normals,
        ReadOnlySpan<float> uvs,
        ReadOnlySpan<ushort> indices,
        float factor,
        MeshOptReducerOptions? options = null)
    {
        if (!float.IsFinite(factor))
            throw new ArgumentOutOfRangeException(nameof(factor), "The reduction factor must be finite.");
        if (factor < 0)
            throw new ArgumentOutOfRangeException(nameof(factor), "The reduction factor cannot be negative.");
        if (positions.Length % 3 != 0)
            throw new ArgumentException("Positions must hold three floats per vertex.", nameof(positions));

        options ??= new MeshOptReducerOptions();
        ValidateOptions(options);

        var vertexCount = positions.Length / 3;
        if (normals.Length != 0 && normals.Length != vertexCount * 3)
            throw new ArgumentException("Normals must be empty or hold three floats per vertex.", nameof(normals));
        if (uvs.Length != 0 && uvs.Length != vertexCount * 2)
            throw new ArgumentException("UVs must be empty or hold two floats per vertex.", nameof(uvs));

        var sourceTriangleCount = indices.Length / 3;
        var requestedIndexCount = factor >= 1.0f
            ? indices.Length
            : CalculateTargetIndexCount(indices.Length, factor);
        var requestedTriangleCount = requestedIndexCount / 3;

        if (factor >= 1.0f)
            return Unchanged(indices, vertexCount, sourceTriangleCount, sourceTriangleCount, 0, "A reduction factor of 1 or greater leaves the mesh unchanged.");

        if (indices.Length < 3 || indices.Length % 3 != 0)
            return Unchanged(indices, vertexCount, sourceTriangleCount, requestedTriangleCount, 0, "The index buffer is empty or is not a sequence of triangles.");

        var inputValidationError = ValidateInput(positions, normals, uvs, indices, vertexCount);
        if (inputValidationError is not null)
            return Unchanged(indices, vertexCount, sourceTriangleCount, requestedTriangleCount, 0, inputValidationError);

        if (vertexCount == 0)
            return Unchanged(indices, vertexCount, sourceTriangleCount, requestedTriangleCount, 0, "The source vertex buffer is empty.");

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
            return Unchanged(indices, vertexCount, sourceTriangleCount, requestedTriangleCount, droppedDegenerateTriangles, "No non-repeated-index triangles remain for simplification.");

        if (nativeIndexCount / 3 < MinimumTriangleCount)
            return Unchanged(indices, vertexCount, sourceTriangleCount, requestedTriangleCount, droppedDegenerateTriangles, $"Fewer than {MinimumTriangleCount} non-degenerate triangles remain after filtering repeated-index triangles.");

        if (nativeIndexCount != nativeIndices.Length)
            Array.Resize(ref nativeIndices, nativeIndexCount);

        var nativeRequestedIndexCount = Math.Min(requestedIndexCount, nativeIndexCount);
        nativeRequestedIndexCount -= nativeRequestedIndexCount % 3;
        if (nativeRequestedIndexCount < 3)
            return Unchanged(indices, vertexCount, sourceTriangleCount, requestedTriangleCount, droppedDegenerateTriangles, "The requested simplification target contains no complete triangle.");

        var positionArray = positions.ToArray();
        var attributes = new float[checked(vertexCount * AttributeCount)];
        var minU = float.PositiveInfinity;
        var maxU = float.NegativeInfinity;
        var minV = float.PositiveInfinity;
        var maxV = float.NegativeInfinity;
        for (var i = 0; i < vertexCount; i++)
        {
            var attributeOffset = i * AttributeCount;
            if (normals.Length != 0)
            {
                attributes[attributeOffset] = normals[i * 3];
                attributes[attributeOffset + 1] = normals[i * 3 + 1];
                attributes[attributeOffset + 2] = normals[i * 3 + 2];
            }

            if (uvs.Length != 0)
            {
                var u = uvs[i * 2];
                var v = uvs[i * 2 + 1];
                attributes[attributeOffset + 3] = u;
                attributes[attributeOffset + 4] = v;
                minU = MathF.Min(minU, u);
                maxU = MathF.Max(maxU, u);
                minV = MathF.Min(minV, v);
                maxV = MathF.Max(maxV, v);
            }
        }

        // meshoptimizer.h v1.3, meshopt_simplifyWithAttributes comments (lines 531-537),
        // define weights as each attribute's priority relative to position and cap the
        // attribute count at 32. Normal components span about 2, so 0.5 per component
        // gives them a unit range. Following meshoptimizer's scale guidance, divide the
        // UV base weight by each observed UV-axis range so a full UV span is comparable
        // to the normalized position extent; constant axes get zero weight.
        var normalWeight = normals.Length != 0 ? options.NormalWeight : 0f;
        var attributeWeights = new[]
        {
            normalWeight,
            normalWeight,
            normalWeight,
            uvs.Length != 0 ? ScaleTextureCoordinateWeight(options.TextureCoordinateWeight, (double)maxU - minU) : 0f,
            uvs.Length != 0 ? ScaleTextureCoordinateWeight(options.TextureCoordinateWeight, (double)maxV - minV) : 0f
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
        var vertexRemap = new uint[vertexCount];
        Array.Fill(vertexRemap, uint.MaxValue);
        nuint simplifiedIndexCount;
        float resultErrorRelative;
        float meshScale;
        nuint compactVertexCount;

        unsafe
        {
            fixed (uint* indicesPointer = nativeIndices)
            fixed (float* positionsPointer = positionArray)
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
                    (nuint)vertexCount,
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
                    return Unchanged(indices, vertexCount, sourceTriangleCount, requestedTriangleCount, droppedDegenerateTriangles, "meshopt_simplifyWithAttributes returned no triangles.");

                if (simplifiedIndexCount % 3 != 0)
                    throw new InvalidDataException("meshopt_simplifyWithAttributes returned an invalid triangle index count.");

                MeshOptNative.OptimizeVertexCache(
                    optimizedIndicesPointer,
                    simplifiedIndicesPointer,
                    simplifiedIndexCount,
                    (nuint)vertexCount);

                compactVertexCount = MeshOptNative.OptimizeVertexFetchRemap(
                    remapPointer,
                    optimizedIndicesPointer,
                    simplifiedIndexCount,
                    (nuint)vertexCount);

                if (compactVertexCount > (nuint)vertexCount)
                    throw new InvalidDataException("meshopt_optimizeVertexFetchRemap returned more vertices than the input buffer contains.");

                meshScale = MeshOptNative.SimplifyScale(positionsPointer, (nuint)vertexCount, PositionStride);
            }
        }

        if (compactVertexCount > (nuint)(ushort.MaxValue + 1))
            throw new InvalidDataException("The reduced mesh contains more vertices than a ushort index buffer can address.");

        // Rewrite optimized indices through the old-vertex -> fetched-vertex remap.
        var outputIndices = new ushort[(int)simplifiedIndexCount];
        for (var i = 0; i < outputIndices.Length; i++)
        {
            var oldIndex = optimizedIndices[i];
            if (oldIndex >= (uint)vertexRemap.Length)
                throw new InvalidDataException("The optimized index buffer references a vertex outside the original vertex buffer.");

            var newIndex = vertexRemap[oldIndex];
            if (newIndex == uint.MaxValue || newIndex >= compactVertexCount || newIndex > ushort.MaxValue)
                throw new InvalidDataException("The optimized vertex remap cannot be represented by a ushort index.");

            outputIndices[i] = (ushort)newIndex;
        }

        if (!float.IsFinite(resultErrorRelative) || !float.IsFinite(meshScale) || meshScale < 0)
            throw new InvalidDataException("meshoptimizer returned a non-finite simplification error or mesh scale.");

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

        return new MeshOptSimplifyResult(outputIndices, vertexRemap, (int)compactVertexCount, stats);
    }

    private static int CalculateTargetIndexCount(int indexCount, float factor)
    {
        var targetTriangleCount = (int)Math.Round(indexCount * (double)factor / 3.0, MidpointRounding.AwayFromZero);
        return Math.Max(3, checked(targetTriangleCount * 3));
    }

    private static string? ValidateInput(
        ReadOnlySpan<float> positions, ReadOnlySpan<float> normals, ReadOnlySpan<float> uvs, ReadOnlySpan<ushort> indices, int vertexCount)
    {
        for (var i = 0; i < vertexCount; i++)
        {
            if (!float.IsFinite(positions[i * 3]) || !float.IsFinite(positions[i * 3 + 1]) || !float.IsFinite(positions[i * 3 + 2]) ||
                (normals.Length != 0 && (!float.IsFinite(normals[i * 3]) || !float.IsFinite(normals[i * 3 + 1]) || !float.IsFinite(normals[i * 3 + 2]))) ||
                (uvs.Length != 0 && (!float.IsFinite(uvs[i * 2]) || !float.IsFinite(uvs[i * 2 + 1]))))
            {
                return $"Vertex {i} contains a non-finite position, normal, or UV value.";
            }
        }

        for (var i = 0; i < indices.Length; i += 3)
        {
            if (indices[i] >= vertexCount || indices[i + 1] >= vertexCount || indices[i + 2] >= vertexCount)
                return $"Triangle {i / 3} references a vertex outside the source vertex buffer.";
        }

        return null;
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

    private static MeshOptSimplifyResult Unchanged(
        ReadOnlySpan<ushort> indices, int vertexCount, int sourceTriangleCount, int requestedTriangleCount, int droppedDegenerateTriangles, string reason)
    {
        var remap = new uint[vertexCount];
        for (var i = 0; i < remap.Length; i++)
            remap[i] = (uint)i;

        var targetReached = sourceTriangleCount <= requestedTriangleCount;
        var stats = new MeshOptReducerStats(sourceTriangleCount, requestedTriangleCount, sourceTriangleCount, targetReached,
            GetLimitReason(targetReached, false), droppedDegenerateTriangles, 0, 0, false, true, reason);
        return new MeshOptSimplifyResult(indices.ToArray(), remap, vertexCount, stats);
    }
}
