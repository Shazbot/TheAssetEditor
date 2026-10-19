using System.Reflection;
using System.Runtime.InteropServices;

namespace Editors.ImportExport.Dds.Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct DxsImage
    {
        public nuint Width;
        public nuint Height;
        public int Format;
        public nuint RowPitch;
        public nuint SlicePitch;
        public IntPtr Pixels;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DxsMetadata
    {
        public nuint Width;
        public nuint Height;
        public nuint Depth;
        public nuint ArraySize;
        public nuint MipLevels;
        public uint MiscFlags;
        public uint MiscFlags2;
        public int Format;
        public int Dimension;
    }

    /// <summary>
    /// P/Invoke surface of DirectXTexShim.dll (Native/DirectXTexShim). Source-generated, so it is safe under
    /// trimming and in a single-file bundle. Every function returns an HRESULT.
    /// </summary>
    internal static partial class NativeMethods
    {
        public const string Library = "DirectXTexShim";

        static NativeMethods()
        {
            NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);
        }

        [LibraryImport(Library)] public static partial int dxs_version();
        [LibraryImport(Library)] public static partial IntPtr dxs_source_tag();
        [LibraryImport(Library)] public static partial IntPtr dxs_source_commit();

        [LibraryImport(Library)]
        public static partial int dxs_scratch_initialize2d(int format, nuint width, nuint height, nuint arraySize, nuint mipLevels, uint cpFlags, out IntPtr image);

        [LibraryImport(Library)] public static partial void dxs_scratch_free(IntPtr image);
        [LibraryImport(Library)] public static partial int dxs_scratch_metadata(IntPtr image, out DxsMetadata metadata);
        [LibraryImport(Library)] public static partial nuint dxs_scratch_image_count(IntPtr image);
        [LibraryImport(Library)] public static partial int dxs_scratch_get_image(IntPtr image, nuint mip, nuint item, nuint slice, out DxsImage result);

        [LibraryImport(Library)]
        public static partial int dxs_scratch_create_image_copy(IntPtr image, nuint imageIndex, int allow1D, uint cpFlags, out IntPtr copy);

        [LibraryImport(Library)] public static partial int dxs_generate_mipmaps(IntPtr image, uint filter, nuint levels, out IntPtr result);
        [LibraryImport(Library)] public static partial int dxs_resize(IntPtr image, nuint width, nuint height, uint filter, out IntPtr result);
        [LibraryImport(Library)] public static partial int dxs_compress(IntPtr image, int format, uint flags, float threshold, out IntPtr result);
        [LibraryImport(Library)] public static partial int dxs_decompress(IntPtr image, int format, out IntPtr result);

        [LibraryImport(Library)]
        public static partial int dxs_save_dds_memory(IntPtr image, uint flags, out IntPtr data, out nuint size, out IntPtr blob);

        [LibraryImport(Library)] public static partial void dxs_blob_free(IntPtr blob);
        [LibraryImport(Library)] public static partial int dxs_load_dds_memory(IntPtr data, nuint size, uint flags, out IntPtr image);
        [LibraryImport(Library)] public static partial int dxs_load_wic_memory(IntPtr data, nuint size, uint flags, out IntPtr image);

        [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
        public static partial int dxs_load_wic_file(string path, uint flags, out IntPtr image);

        [LibraryImport(Library)]
        public static partial int dxs_copy_rectangle(in DxsImage source, nuint x, nuint y, nuint width, nuint height, in DxsImage destination, uint filter, nuint xOffset, nuint yOffset);

        // Looks next to the executable and in runtimes/win-x64/native when the default probing misses the
        // library, so a plain publish (not only a single-file bundle) finds the committed DLL.
        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!string.Equals(libraryName, Library, StringComparison.Ordinal))
                return IntPtr.Zero;

            foreach (var directory in new[]
                     {
                         AppContext.BaseDirectory,
                         Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native"),
                     })
            {
                if (string.IsNullOrEmpty(directory))
                    continue;

                var candidate = Path.Combine(directory, Library + ".dll");
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                    return handle;
            }

            return IntPtr.Zero;
        }
    }
}
