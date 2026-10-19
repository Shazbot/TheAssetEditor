using System.Runtime.InteropServices;

namespace Editors.ImportExport.Dds.Native
{
    /// <summary>One image in a <see cref="ScratchImage"/>. The pixel pointer is owned by that image and is valid until it is disposed.</summary>
    public sealed class Image
    {
        public Image(int width, int height, DXGI_FORMAT format, long rowPitch, long slicePitch, IntPtr pixels)
        {
            Width = width;
            Height = height;
            Format = format;
            RowPitch = rowPitch;
            SlicePitch = slicePitch;
            Pixels = pixels;
        }

        public int Width { get; }
        public int Height { get; }
        public DXGI_FORMAT Format { get; }
        public long RowPitch { get; }
        public long SlicePitch { get; }
        public IntPtr Pixels { get; }

        internal DxsImage ToNative() => new()
        {
            Width = (nuint)Width,
            Height = (nuint)Height,
            Format = (int)Format,
            RowPitch = (nuint)RowPitch,
            SlicePitch = (nuint)SlicePitch,
            Pixels = Pixels,
        };
    }

    public sealed class TexMetadata
    {
        internal TexMetadata(in DxsMetadata native)
        {
            Width = checked((int)native.Width);
            Height = checked((int)native.Height);
            Depth = checked((int)native.Depth);
            ArraySize = checked((int)native.ArraySize);
            MipLevels = checked((int)native.MipLevels);
            MiscFlags = native.MiscFlags;
            MiscFlags2 = native.MiscFlags2;
            Format = (DXGI_FORMAT)native.Format;
            Dimension = native.Dimension;
        }

        public int Width { get; }
        public int Height { get; }
        public int Depth { get; }
        public int ArraySize { get; }
        public int MipLevels { get; }
        public uint MiscFlags { get; }
        public uint MiscFlags2 { get; }
        public DXGI_FORMAT Format { get; }
        public int Dimension { get; }
    }

    /// <summary>
    /// A set of images (a mip chain, array or volume) held in native DirectXTex memory. Mirrors the
    /// DirectXTexNet type of the same name for the operations the optimizer uses.
    /// </summary>
    public sealed class ScratchImage : IDisposable
    {
        private IntPtr _handle;

        internal ScratchImage(IntPtr handle)
        {
            _handle = handle;
        }

        ~ScratchImage() => Release();

        internal IntPtr Handle => _handle != IntPtr.Zero
            ? _handle
            : throw new ObjectDisposedException(nameof(ScratchImage));

        public void Dispose()
        {
            Release();
            GC.SuppressFinalize(this);
        }

        private void Release()
        {
            var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
                NativeMethods.dxs_scratch_free(handle);
        }

        public TexMetadata GetMetadata()
        {
            ThrowIfFailed(NativeMethods.dxs_scratch_metadata(Handle, out var native));
            return new TexMetadata(native);
        }

        public int GetImageCount() => checked((int)NativeMethods.dxs_scratch_image_count(Handle));

        /// <summary>The image at a flat index (array item major, mip level within an item), as DirectXTex orders them.</summary>
        public Image GetImage(int index)
        {
            var metadata = GetMetadata();
            return GetImage(index % metadata.MipLevels, index / metadata.MipLevels, 0);
        }

        public Image GetImage(int mip, int item, int slice)
        {
            var hr = NativeMethods.dxs_scratch_get_image(Handle, (nuint)mip, (nuint)item, (nuint)slice, out var native);
            if (hr == unchecked((int)0x80070057))
                throw new IndexOutOfRangeException("The image index is out of range.");
            ThrowIfFailed(hr);
            return new Image(checked((int)native.Width), checked((int)native.Height), (DXGI_FORMAT)native.Format, (long)native.RowPitch, (long)native.SlicePitch, native.Pixels);
        }

        public ScratchImage CreateImageCopy(int imageIndex, bool allow1D, CP_FLAGS flags)
        {
            var hr = NativeMethods.dxs_scratch_create_image_copy(Handle, (nuint)imageIndex, allow1D ? 1 : 0, (uint)flags, out var copy);
            if (hr == unchecked((int)0x80070057))
                throw new IndexOutOfRangeException("The image index is out of range.");
            ThrowIfFailed(hr);
            return new ScratchImage(copy);
        }

        /// <summary>
        /// A new image of the same size with <paramref name="levels"/> mip levels whose level 0 is a copy of
        /// this image's level 0 (per array item); the other levels are zeroed when asked. Matches DirectXTexNet,
        /// except that cube maps are not supported (the optimizer has none).
        /// </summary>
        public ScratchImage CreateCopyWithEmptyMipMaps(int levels, DXGI_FORMAT format, CP_FLAGS flags, bool zeroOutMipMaps)
        {
            var metadata = GetMetadata();
            if ((metadata.MiscFlags & 0x4) != 0)
                throw new NotSupportedException("Cube maps are not supported.");

            var result = TexHelper.Instance.Initialize2D(format, metadata.Width, metadata.Height, metadata.ArraySize, levels, flags);
            try
            {
                for (var item = 0; item < metadata.ArraySize; item++)
                {
                    TexHelper.Instance.CopyRectangle(GetImage(0, item, 0), 0, 0, metadata.Width, metadata.Height, result.GetImage(0, item, 0), TEX_FILTER_FLAGS.DEFAULT, 0, 0);
                    if (!zeroOutMipMaps)
                        continue;

                    for (var mip = 1; mip < levels; mip++)
                    {
                        var image = result.GetImage(mip, item, 0);
                        unsafe
                        {
                            new Span<byte>((void*)image.Pixels, checked((int)(image.RowPitch * image.Height))).Clear();
                        }
                    }
                }

                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        public ScratchImage GenerateMipMaps(TEX_FILTER_FLAGS filter, int levels)
        {
            ThrowIfFailed(NativeMethods.dxs_generate_mipmaps(Handle, (uint)filter, (nuint)levels, out var result));
            return new ScratchImage(result);
        }

        public ScratchImage Resize(int width, int height, TEX_FILTER_FLAGS filter)
        {
            ThrowIfFailed(NativeMethods.dxs_resize(Handle, (nuint)width, (nuint)height, (uint)filter, out var result));
            return new ScratchImage(result);
        }

        public ScratchImage Compress(DXGI_FORMAT format, TEX_COMPRESS_FLAGS flags, float threshold)
        {
            ThrowIfFailed(NativeMethods.dxs_compress(Handle, (int)format, (uint)flags, threshold, out var result));
            return new ScratchImage(result);
        }

        public ScratchImage Decompress(DXGI_FORMAT format)
        {
            ThrowIfFailed(NativeMethods.dxs_decompress(Handle, (int)format, out var result));
            return new ScratchImage(result);
        }

        /// <summary>The DDS file bytes. The stream owns a managed copy; the native buffer is released before this returns.</summary>
        public MemoryStream SaveToDDSMemory(DDS_FLAGS flags)
        {
            ThrowIfFailed(NativeMethods.dxs_save_dds_memory(Handle, (uint)flags, out var data, out var size, out var blob));
            try
            {
                var bytes = new byte[checked((int)size)];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                return new MemoryStream(bytes, writable: false);
            }
            finally
            {
                NativeMethods.dxs_blob_free(blob);
            }
        }

        internal static void ThrowIfFailed(int hr)
        {
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);
        }
    }

    /// <summary>Entry point mirroring DirectXTexNet's <c>TexHelper.Instance</c>.</summary>
    public sealed class TexHelper
    {
        public static TexHelper Instance { get; } = new();

        private TexHelper()
        {
        }

        /// <summary>The DirectXTex release the native shim was built from, e.g. "mar2024 (5cfd711...)" and its DIRECTX_TEX_VERSION.</summary>
        public static (int Version, string Tag, string Commit) NativeVersion => (
            NativeMethods.dxs_version(),
            Marshal.PtrToStringUTF8(NativeMethods.dxs_source_tag()) ?? string.Empty,
            Marshal.PtrToStringUTF8(NativeMethods.dxs_source_commit()) ?? string.Empty);

        public ScratchImage Initialize2D(DXGI_FORMAT format, int width, int height, int arraySize, int mipLevels, CP_FLAGS flags)
        {
            ScratchImage.ThrowIfFailed(NativeMethods.dxs_scratch_initialize2d((int)format, (nuint)width, (nuint)height, (nuint)arraySize, (nuint)mipLevels, (uint)flags, out var handle));
            return new ScratchImage(handle);
        }

        public ScratchImage LoadFromDDSMemory(IntPtr source, long size, DDS_FLAGS flags)
        {
            ScratchImage.ThrowIfFailed(NativeMethods.dxs_load_dds_memory(source, (nuint)size, (uint)flags, out var handle));
            return new ScratchImage(handle);
        }

        public ScratchImage LoadFromWICMemory(IntPtr source, long size, WIC_FLAGS flags)
        {
            ScratchImage.ThrowIfFailed(NativeMethods.dxs_load_wic_memory(source, (nuint)size, (uint)flags, out var handle));
            return new ScratchImage(handle);
        }

        public ScratchImage LoadFromWICFile(string path, WIC_FLAGS flags)
        {
            ArgumentNullException.ThrowIfNull(path);
            ScratchImage.ThrowIfFailed(NativeMethods.dxs_load_wic_file(path, (uint)flags, out var handle));
            return new ScratchImage(handle);
        }

        public void CopyRectangle(Image source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, Image destination, TEX_FILTER_FLAGS filter, int xOffset, int yOffset)
        {
            var native = source.ToNative();
            var target = destination.ToNative();
            ScratchImage.ThrowIfFailed(NativeMethods.dxs_copy_rectangle(in native, (nuint)sourceX, (nuint)sourceY, (nuint)sourceWidth, (nuint)sourceHeight, in target, (uint)filter, (nuint)xOffset, (nuint)yOffset));
        }
    }
}
