using System.Drawing.Imaging;
using System.IO.Compression;
using System.IO;
using System.Diagnostics;
using Editors.ImportExport.Misc;
using Pfim;
using ZstdSharp;

namespace MeshImportExport
{
    public readonly record struct TexturePngExportResult(string Path, byte[] PngData);
    public readonly record struct TextureZstdProbeResult(
        int RawRgbaBytes,
        int ZstdBytes,
        double RgbaConvertMs,
        double ZstdMs,
        int CompressionLevel);

    public readonly record struct TextureImageExportResult(string Path, byte[] Data);

    public interface ITextureEncodingProbe
    {
        void Probe(
            string texturePath,
            TextureHelper.DecodedDdsImage image,
            bool srgb,
            TextureKtx2EncodeResult losslessKtx2,
            int pngBytes,
            double pngEncodeMs);
    }

    public readonly record struct TextureKtx2EncodeResult(
        byte[] Ktx2Data,
        int RawRgbaBytes,
        int ZstdBytes,
        double RgbaConvertMs,
        double ZstdMs,
        int CompressionLevel,
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

        public static TextureZstdProbeResult ProbeBgraToRgbaZstd(
            DecodedDdsImage image,
            int compressionLevel = 1)
        {
            var payload = CompressBgraToRgbaZstd(image, compressionLevel);
            return new TextureZstdProbeResult(
                payload.RawRgbaBytes,
                payload.CompressedLength,
                payload.RgbaConvertMs,
                payload.ZstdMs,
                compressionLevel);
        }

        /// <summary>
        /// Returns whether the transformed image can be encoded as the
        /// headless preview's single-level raw RGBA + Zstd KTX2 payload.
        /// Raw R8G8B8A8 KTX2 has no block-size restriction.
        /// </summary>
        public static bool CanEncodeKtx2ForSharpGltf(DecodedDdsImage image)
            => image.Width > 0 && image.Height > 0;

        public static TextureKtx2EncodeResult EncodeBgraToKtx2(
            DecodedDdsImage image,
            bool srgb,
            int compressionLevel = 1)
        {
            var payload = CompressBgraToRgbaZstd(image, compressionLevel);
            var dfd = BuildRgba8Dfd(srgb);

            const int identifierLength = 12;
            const int headerLength = 68;
            const int levelIndexLength = 24;
            var dfdOffset = identifierLength + headerLength + levelIndexLength;
            var kvdOffset = checked(dfdOffset + dfd.Length);
            var levelOffset = kvdOffset;

            using var output = new MemoryStream(
                checked(levelOffset + payload.CompressedLength));
            using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);

            writer.Write(new byte[]
            {
                0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32,
                0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A
            });

            writer.Write(srgb ? 43u : 37u); // VK_FORMAT_R8G8B8A8_SRGB / UNORM
            writer.Write(1u); // typeSize
            writer.Write(checked((uint)image.Width));
            writer.Write(checked((uint)image.Height));
            writer.Write(0u); // pixelDepth
            writer.Write(0u); // layerCount
            writer.Write(1u); // faceCount
            writer.Write(1u); // levelCount
            writer.Write(2u); // KHR_SUPERCOMPRESSION_ZSTD
            writer.Write(checked((uint)dfdOffset));
            writer.Write(checked((uint)dfd.Length));
            writer.Write(checked((uint)kvdOffset));
            writer.Write(0u); // kvdByteLength
            writer.Write(0UL); // sgdByteOffset
            writer.Write(0UL); // sgdByteLength

            writer.Write(checked((ulong)levelOffset));
            writer.Write(checked((ulong)payload.CompressedLength));
            writer.Write(checked((ulong)payload.RawRgbaBytes));

            writer.Write(dfd);
            writer.Write(payload.Compressed.AsSpan(0, payload.CompressedLength));
            writer.Flush();

            return new TextureKtx2EncodeResult(
                output.ToArray(),
                payload.RawRgbaBytes,
                payload.CompressedLength,
                payload.RgbaConvertMs,
                payload.ZstdMs,
                compressionLevel,
                srgb);
        }

        private static ZstdRgbaPayload CompressBgraToRgbaZstd(
            DecodedDdsImage image,
            int compressionLevel)
        {
            var expectedLength = checked(image.Width * image.Height * 4);
            if (image.Width <= 0 || image.Height <= 0)
                throw new ArgumentOutOfRangeException(nameof(image), "Texture dimensions must be positive.");
            if (image.BgraPixels == null || image.BgraPixels.Length < expectedLength)
                throw new ArgumentException("The pixel buffer is smaller than the requested image.", nameof(image));

            var convertStopwatch = Stopwatch.StartNew();
            var rgba = new byte[expectedLength];
            for (var index = 0; index < expectedLength; index += 4)
            {
                rgba[index] = image.BgraPixels[index + 2];
                rgba[index + 1] = image.BgraPixels[index + 1];
                rgba[index + 2] = image.BgraPixels[index];
                rgba[index + 3] = image.BgraPixels[index + 3];
            }
            convertStopwatch.Stop();

            var zstdStopwatch = Stopwatch.StartNew();
            var destination = new byte[Compressor.GetCompressBound(rgba.Length)];
            int compressedLength;
            using (var compressor = new Compressor(compressionLevel))
                compressedLength = compressor.Wrap(rgba, destination.AsSpan());
            zstdStopwatch.Stop();

            return new ZstdRgbaPayload(
                destination,
                compressedLength,
                rgba.Length,
                convertStopwatch.Elapsed.TotalMilliseconds,
                zstdStopwatch.Elapsed.TotalMilliseconds);
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

        private readonly record struct ZstdRgbaPayload(
            byte[] Compressed,
            int CompressedLength,
            int RawRgbaBytes,
            double RgbaConvertMs,
            double ZstdMs);

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
