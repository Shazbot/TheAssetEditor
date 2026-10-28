using System.Reflection;
using System.Runtime.InteropServices;

namespace GameWorld.MeshOptimizer;

internal static partial class MeshOptNative
{
    private const string LibraryName = "meshoptimizer";

    static MeshOptNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(MeshOptNative).Assembly, Resolve);
    }

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

    [LibraryImport(LibraryName, EntryPoint = "meshopt_simplifyWithAttributes")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static unsafe partial nuint SimplifyWithAttributes(
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

    // Simplifies in place: indices, vertex positions and attributes are updated (kept vertices move to the
    // collapse result's position and attribute values).
    [LibraryImport(LibraryName, EntryPoint = "meshopt_simplifyWithUpdate")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static unsafe partial nuint SimplifyWithUpdate(
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

    [LibraryImport(LibraryName, EntryPoint = "meshopt_simplifyScale")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static unsafe partial float SimplifyScale(float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride);

    [LibraryImport(LibraryName, EntryPoint = "meshopt_optimizeVertexCache")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static unsafe partial void OptimizeVertexCache(uint* destination, uint* indices, nuint indexCount, nuint vertexCount);

    [LibraryImport(LibraryName, EntryPoint = "meshopt_optimizeVertexFetchRemap")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static unsafe partial nuint OptimizeVertexFetchRemap(uint* destination, uint* indices, nuint indexCount, nuint vertexCount);

    // Looks next to the executable and in runtimes/win-x64/native when the default probing misses the
    // library, so a plain publish (not only a single-file bundle) finds the committed DLL.
    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
            return IntPtr.Zero;

        foreach (var directory in new[]
                 {
                     AppContext.BaseDirectory,
                     Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native"),
                 })
        {
            if (string.IsNullOrEmpty(directory))
                continue;

            var candidate = Path.Combine(directory, LibraryName + ".dll");
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        return IntPtr.Zero;
    }
}
