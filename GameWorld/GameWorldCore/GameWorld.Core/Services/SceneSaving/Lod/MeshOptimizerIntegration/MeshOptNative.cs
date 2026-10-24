using System.Runtime.InteropServices;

namespace GameWorld.Core.Services.SceneSaving.Lod.MeshOptimizerIntegration;

internal static class MeshOptNative
{
    private const string LibraryName = "meshoptimizer.dll";

    // Values from meshoptimizer.h v1.3, Simplification options enum.
    internal const uint SimplifyLockBorder = 1u << 0;
    internal const uint SimplifySparse = 1u << 1;
    internal const uint SimplifyErrorAbsolute = 1u << 2;
    internal const uint SimplifyPrune = 1u << 3;
    internal const uint SimplifyRegularize = 1u << 4;
    internal const uint SimplifyPermissive = 1u << 5;
    internal const uint SimplifyRegularizeLight = 1u << 6;
    internal const uint SimplifyPreserveFolds = 1u << 7;
    internal const uint SimplifyErrorClamped = 1u << 8;

    // Values from meshoptimizer.h v1.3, simplification vertex flags enum.
    internal const byte SimplifyVertexLock = 1 << 0;
    internal const byte SimplifyVertexProtect = 1 << 1;
    internal const byte SimplifyVertexPriority = 1 << 2;

    [DllImport(LibraryName, EntryPoint = "meshopt_simplifyWithAttributes", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern unsafe nuint SimplifyWithAttributes(
        uint* destination,
        uint* indices,
        nuint indexCount,
        float* vertexPositions,
        nuint vertexCount,
        nuint vertexPositionsStride,
        float* vertexAttributes,
        nuint vertexAttributesStride,
        float* attributeWeights,
        nuint attributeCount,
        byte* vertexLock,
        nuint targetIndexCount,
        float targetError,
        uint options,
        float* resultError);

    [DllImport(LibraryName, EntryPoint = "meshopt_simplifyScale", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern unsafe float SimplifyScale(float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride);

    [DllImport(LibraryName, EntryPoint = "meshopt_optimizeVertexCache", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern unsafe void OptimizeVertexCache(uint* destination, uint* indices, nuint indexCount, nuint vertexCount);

    [DllImport(LibraryName, EntryPoint = "meshopt_optimizeVertexFetchRemap", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern unsafe nuint OptimizeVertexFetchRemap(uint* destination, uint* indices, nuint indexCount, nuint vertexCount);
}
