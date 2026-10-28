using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.IO;
using System.Diagnostics;
using Editors.ImportExport.Misc;
using Pfim;

namespace MeshImportExport
{
    public readonly record struct TexturePngExportResult(string Path, byte[] PngData);
    public readonly record struct TextureImageExportResult(string Path, byte[] Data);

    public readonly record struct TextureKtx2EncodeResult(
        byte[] Ktx2Data,
        int RawRgbaBytes,
        double RgbaConvertMs,
        bool Srgb);

    public class TextureHelper
    {
        public readonly record struct DecodedDdsImage(int Width, int Height, byte[] BgraPixels);

        public static DecodedDdsImage DecodeDdsToBgra(byte[] ddsBytes)
        {
            ArgumentNullException.ThrowIfNull(ddsBytes);

            using var stream = new MemoryStream(ddsBytes, writable: false);
            using var image = Pfimage.FromStream(stream);

            var width = image.Width;
            var height = image.Height;
            var destinationStride = checked(width * 4);
            var pixels = new byte[checked(destinationStride * height)];

            switch (image.Format)
            {
                case Pfim.ImageFormat.Rgba32:
                    CopyRgba32ToBgra(image, pixels, destinationStride);
                    break;

                case Pfim.ImageFormat.Rgb24:
                    CopyRgb24ToBgra(image, pixels, destinationStride);
                    break;

                default:
                    throw new NotSupportedException($"Unsupported DDS format: {image.Format}");
            }

            return new DecodedDdsImage(width, height, pixels);
        }

        public static byte[] EncodeBgraToPng(DecodedDdsImage image)
            => EncodeBgraToPng(image.Width, image.Height, image.BgraPixels);

        /// <summary>
        /// SharpGLTF 1.0.6 rejects raw RGBA KTX2 images whose dimensions are
        /// not multiples of four, even though the uncompressed KTX2 payload
        /// itself can represent those dimensions. Use PNG for that subset so
        /// the generated image remains lossless and can still be embedded in
        /// the glTF material.
        /// </summary>
        public static bool CanEncodeKtx2ForSharpGltf(DecodedDdsImage image)
            => image.Width > 0
                && image.Height > 0
                && image.Width % 4 == 0
                && image.Height % 4 == 0;

        /// <summary>
        /// Writes a single-level RGBA8 KTX2 with no supercompression. The
        /// preview files stay on the local machine (OS page cache), so the
        /// larger raw payload costs less than a zstd encode here plus the
        /// decode in the viewer.
        /// </summary>
        public static TextureKtx2EncodeResult EncodeBgraToKtx2(
            DecodedDdsImage image,
            bool srgb)
        {
            ArgumentNullException.ThrowIfNull(image.BgraPixels);
            if (image.Width <= 0 || image.Height <= 0)
                throw new ArgumentOutOfRangeException(nameof(image), "Texture dimensions must be positive.");
            var rawLength = checked(image.Width * image.Height * 4);
            if (image.BgraPixels.Length < rawLength)
                throw new ArgumentException("The pixel buffer is smaller than the requested image.", nameof(image));

            var dfd = BuildRgba8Dfd(srgb);

            const int identifierLength = 12;
            const int headerLength = 68;
            const int levelIndexLength = 24;
            var dfdOffset = identifierLength + headerLength + levelIndexLength;
            var kvdOffset = checked(dfdOffset + dfd.Length);
            // Unsupercompressed levels must be aligned to lcm(texel size, 4).
            var levelOffset = (kvdOffset + 3) & ~3;

            var output = new byte[checked(levelOffset + rawLength)];
            var span = output.AsSpan();

            new byte[]
            {
                0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32,
                0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A
            }.CopyTo(span);

            var cursor = identifierLength;
            void WriteU32(uint value)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(cursor, 4), value);
                cursor += 4;
            }
            void WriteU64(ulong value)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(cursor, 8), value);
                cursor += 8;
            }

            WriteU32(srgb ? 43u : 37u); // VK_FORMAT_R8G8B8A8_SRGB / UNORM
            WriteU32(1u); // typeSize
            WriteU32(checked((uint)image.Width));
            WriteU32(checked((uint)image.Height));
            WriteU32(0u); // pixelDepth
            WriteU32(0u); // layerCount
            WriteU32(1u); // faceCount
            WriteU32(1u); // levelCount
            WriteU32(0u); // KHR_SUPERCOMPRESSION_NONE
            WriteU32(checked((uint)dfdOffset));
            WriteU32(checked((uint)dfd.Length));
            WriteU32(checked((uint)kvdOffset));
            WriteU32(0u); // kvdByteLength
            WriteU64(0UL); // sgdByteOffset
            WriteU64(0UL); // sgdByteLength

            WriteU64(checked((ulong)levelOffset));
            WriteU64(checked((ulong)rawLength)); // byteLength
            WriteU64(checked((ulong)rawLength)); // uncompressedByteLength

            dfd.CopyTo(span.Slice(dfdOffset));

            // BGRA -> RGBA straight into the output buffer.
            var convertStopwatch = Stopwatch.StartNew();
            var src = image.BgraPixels;
            for (var index = 0; index < rawLength; index += 4)
            {
                var dst = levelOffset + index;
                output[dst] = src[index + 2];
                output[dst + 1] = src[index + 1];
                output[dst + 2] = src[index];
                output[dst + 3] = src[index + 3];
            }
            convertStopwatch.Stop();

            return new TextureKtx2EncodeResult(
                output,
                rawLength,
                convertStopwatch.Elapsed.TotalMilliseconds,
                srgb);
        }

        /// <summary>
        /// Returns only the KTX2 header, level index and DFD of a single-level
        /// raw KTX2. SharpGLTF hashes and validates image bytes while building and
        /// saving the scene (roughly 1 s for ~150 MB of raw RGBA), but the preview
        /// GLB only references the sidecar file, so the header is enough for it.
        /// Anything that is not a KTX2 is returned unchanged.
        /// </summary>
        public static byte[] ToKtx2HeaderOnly(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (data.Length < 104
                || data[0] != 0xAB || data[1] != 0x4B || data[2] != 0x54 || data[3] != 0x58
                || data[4] != 0x20 || data[5] != 0x32 || data[6] != 0x30 || data[7] != 0xBB)
                return data;

            var levelOffset = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(80, 8));
            return levelOffset == 0 || levelOffset >= (ulong)data.Length
                ? data
                : data.AsSpan(0, (int)levelOffset).ToArray();
        }

        private static readonly byte[] Ktx2StubMarker = "WH3SIDECAR:"u8.ToArray();

        /// <summary>
        /// Appends the sidecar path to a header-only KTX2. SharpGLTF merges images
        /// with equal bytes, and headers of same-sized textures are identical, so
        /// without this two different textures would share one image. The path
        /// also lets the GLB writer copy the real file if it has to write it.
        /// </summary>
        public static byte[] ToKtx2SidecarStub(byte[] headerOnly, string sidecarPath)
        {
            ArgumentNullException.ThrowIfNull(headerOnly);
            ArgumentException.ThrowIfNullOrWhiteSpace(sidecarPath);
            if (!IsKtx2HeaderOnly(headerOnly))
                return headerOnly;

            var levelOffset = (int)BinaryPrimitives.ReadUInt64LittleEndian(headerOnly.AsSpan(80, 8));
            var pathBytes = System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(sidecarPath));
            var stub = new byte[levelOffset + Ktx2StubMarker.Length + pathBytes.Length];
            headerOnly.AsSpan(0, levelOffset).CopyTo(stub);
            Ktx2StubMarker.CopyTo(stub.AsSpan(levelOffset));
            pathBytes.CopyTo(stub.AsSpan(levelOffset + Ktx2StubMarker.Length));
            return stub;
        }

        /// <summary>
        /// True for a KTX2 without its pixel data: the bare header from
        /// <see cref="ToKtx2HeaderOnly"/> or a <see cref="ToKtx2SidecarStub"/>.
        /// </summary>
        public static bool IsKtx2HeaderOnly(ReadOnlySpan<byte> data)
        {
            if (data.Length < 104
                || data[0] != 0xAB || data[1] != 0x4B || data[2] != 0x54 || data[3] != 0x58
                || data[4] != 0x20 || data[5] != 0x32 || data[6] != 0x30 || data[7] != 0xBB)
                return false;

            var levelOffset = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(80, 8));
            if (levelOffset == 0 || levelOffset > (ulong)data.Length)
                return false;
            return (ulong)data.Length == levelOffset || data[(int)levelOffset..].StartsWith(Ktx2StubMarker);
        }

        /// <summary>Reads the sidecar path from <see cref="ToKtx2SidecarStub"/> output.</summary>
        public static bool TryGetKtx2StubSidecarPath(ReadOnlySpan<byte> data, out string sidecarPath)
        {
            sidecarPath = string.Empty;
            if (!IsKtx2HeaderOnly(data))
                return false;

            var levelOffset = (int)BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(80, 8));
            var tail = data[levelOffset..];
            if (!tail.StartsWith(Ktx2StubMarker) || tail.Length == Ktx2StubMarker.Length)
                return false;

            sidecarPath = System.Text.Encoding.UTF8.GetString(tail[Ktx2StubMarker.Length..]);
            return true;
        }

        private static byte[] BuildRgba8Dfd(bool srgb)
        {
            // Matches Khronos KTX-Software createDFDUnpacked(0, 4, 1, 0,
            // s_SRGB/s_UNORM) for VK_FORMAT_R8G8B8A8_*.
            const int sampleCount = 4;
            const int descriptorBlockSize = 24 + sampleCount * 16;
            var dfd = new byte[4 + descriptorBlockSize];

            using var stream = new MemoryStream(dfd, writable: true);
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write(checked((uint)dfd.Length));
            writer.Write((ushort)0); // KHR_DF_VENDORID_KHRONOS
            writer.Write((ushort)0); // KHR_DF_KHR_DESCRIPTORTYPE_BASICFORMAT
            writer.Write((ushort)2); // KHR_DF_VERSIONNUMBER_LATEST
            writer.Write((ushort)descriptorBlockSize);
            writer.Write((byte)1); // KHR_DF_MODEL_RGBSDA
            writer.Write((byte)1); // KHR_DF_PRIMARIES_BT709
            writer.Write((byte)(srgb ? 2 : 1)); // KHR_DF_TRANSFER_SRGB / LINEAR
            writer.Write((byte)0); // KHR_DF_FLAG_ALPHA_STRAIGHT
            writer.Write(new byte[] { 0, 0, 0, 0 }); // 1x1x1x1 texel block
            writer.Write(new byte[] { 4, 0, 0, 0, 0, 0, 0, 0 }); // bytesPlane

            for (var channel = 0; channel < sampleCount; channel++)
            {
                writer.Write(checked((ushort)(channel * 8))); // bitOffset
                writer.Write((byte)7); // bitLength stores bits - 1
                var channelType = channel == 3 ? 15 : channel;
                if (srgb && channel == 3)
                    channelType |= 0x10; // alpha is linear in an sRGB texture
                writer.Write(checked((byte)channelType));
                writer.Write(new byte[] { 0, 0, 0, 0 }); // samplePosition
                writer.Write(0u);
                writer.Write(255u);
            }

            return dfd;
        }

        public static byte[] EncodeBgraToPng(int width, int height, byte[] bgraPixels)
        {
            ArgumentNullException.ThrowIfNull(bgraPixels);

            var rowBytes = checked(width * 4);
            var expectedLength = checked(rowBytes * height);
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "PNG dimensions must be positive.");
            if (bgraPixels.Length < expectedLength)
                throw new ArgumentException("The pixel buffer is smaller than the requested image.", nameof(bgraPixels));

            // GDI+ can premultiply and then unpremultiply semi-transparent
            // pixels while saving PNGs.  That changes channel values by one or
            // more (for example, 128 can become 127), which is not acceptable
            // for packed game textures. Write an RGBA PNG directly so the
            // decoded image contains exactly the source channel bytes.
            var scanlines = new byte[checked((rowBytes + 1) * height)];
            for (var row = 0; row < height; row++)
            {
                var sourceRow = checked(row * rowBytes);
                var destinationRow = checked(row * (rowBytes + 1));
                // Filter type 0 (None).
                scanlines[destinationRow] = 0;

                for (var column = 0; column < width; column++)
                {
                    var sourceIndex = checked(sourceRow + column * 4);
                    var destinationIndex = checked(destinationRow + 1 + column * 4);

                    // PNG color type 6 is RGBA; the source buffer is BGRA.
                    scanlines[destinationIndex] = bgraPixels[sourceIndex + 2];
                    scanlines[destinationIndex + 1] = bgraPixels[sourceIndex + 1];
                    scanlines[destinationIndex + 2] = bgraPixels[sourceIndex];
                    scanlines[destinationIndex + 3] = bgraPixels[sourceIndex + 3];
                }
            }

            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                zlib.Write(scanlines, 0, scanlines.Length);

            using var output = new MemoryStream();
            output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

            Span<byte> header = stackalloc byte[13];
            WriteBigEndian(header[0..4], (uint)width);
            WriteBigEndian(header[4..8], (uint)height);
            header[8] = 8; // bit depth
            header[9] = 6; // RGBA
            header[10] = 0; // compression method
            header[11] = 0; // filter method
            header[12] = 0; // no interlace
            WritePngChunk(output, "IHDR", header);
            WritePngChunk(
                output,
                "IDAT",
                compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length)));
            WritePngChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
            return output.ToArray();
        }

        private static void WritePngChunk(Stream output, string type, ReadOnlySpan<byte> data)
        {
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            Span<byte> length = stackalloc byte[4];
            WriteBigEndian(length, checked((uint)data.Length));
            output.Write(length);
            output.Write(typeBytes);
            output.Write(data);

            var crcValue = UpdateCrc32(0xffffffffu, typeBytes);
            crcValue = UpdateCrc32(crcValue, data);
            Span<byte> crc = stackalloc byte[4];
            WriteBigEndian(crc, ~crcValue);
            output.Write(crc);
        }

        private static void WriteBigEndian(Span<byte> destination, uint value)
        {
            destination[0] = (byte)(value >> 24);
            destination[1] = (byte)(value >> 16);
            destination[2] = (byte)(value >> 8);
            destination[3] = (byte)value;
        }

        private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var value in data)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ (0xedb88320u & unchecked((uint)-(int)(crc & 1)));
            }

            return crc;
        }

        public static byte[] ConvertDdsToPng(byte[] ddsbyteSteam)
            => EncodeBgraToPng(DecodeDdsToBgra(ddsbyteSteam));

        private static void CopyRgba32ToBgra(IImage image, byte[] destination, int destinationStride)
        {
            var sourceStride = image.Stride;
            var rowBytes = checked(image.Width * 4);
            if (sourceStride < rowBytes)
                throw new InvalidDataException($"DDS stride {sourceStride} is smaller than the expected row size {rowBytes}.");

            for (var row = 0; row < image.Height; row++)
            {
                Buffer.BlockCopy(
                    image.Data,
                    checked(row * sourceStride),
                    destination,
                    checked(row * destinationStride),
                    rowBytes);
            }
        }

        private static void CopyRgb24ToBgra(IImage image, byte[] destination, int destinationStride)
        {
            var sourceStride = image.Stride;
            var sourceRowBytes = checked(image.Width * 3);
            if (sourceStride < sourceRowBytes)
                throw new InvalidDataException($"DDS stride {sourceStride} is smaller than the expected row size {sourceRowBytes}.");

            for (var row = 0; row < image.Height; row++)
            {
                var sourceRow = checked(row * sourceStride);
                var destinationRow = checked(row * destinationStride);
                for (var x = 0; x < image.Width; x++)
                {
                    var sourceIndex = checked(sourceRow + x * 3);
                    var destinationIndex = checked(destinationRow + x * 4);

                    // Pfim's decoded RGB24 bytes are BGR, matching GDI+'s in-memory ordering.
                    destination[destinationIndex] = image.Data[sourceIndex];
                    destination[destinationIndex + 1] = image.Data[sourceIndex + 1];
                    destination[destinationIndex + 2] = image.Data[sourceIndex + 2];
                    destination[destinationIndex + 3] = 255;
                }
            }
        }

        public static byte[] ConvertPngToDds(byte[] png)
        {
            using var m = new MemoryStream();
            using var w = new BinaryWriter(m);
            w.Write(png);
            m.Seek(0, SeekOrigin.Begin);
            using var bitmap = new System.Drawing.Bitmap(m);

            PixelFormat pixelFormat = PixelFormat.Format32bppArgb;
            if (bitmap.PixelFormat == PixelFormat.Format32bppArgb)
            {
                pixelFormat = PixelFormat.Format32bppArgb;
            }
            else if (bitmap.PixelFormat == PixelFormat.Format24bppRgb)
            {
                pixelFormat = PixelFormat.Format24bppRgb;
            }
            else
            {
                throw new NotSupportedException($"Unsupported PNG format: {bitmap.PixelFormat}");
            }

            BitmapData bitmapData = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, pixelFormat);
            byte[] imageData = new byte[bitmapData.Stride * bitmapData.Height];
            System.Runtime.InteropServices.Marshal.Copy(bitmapData.Scan0, imageData, 0, imageData.Length);
            bitmap.UnlockBits(bitmapData);

            using var b = new MemoryStream();
            var imageNew = Pfimage.FromStream(b);
            using var writer = new BinaryWriter(b);
            writer.Write(imageNew.Data);
            return b.ToArray();
        }
    }
}
