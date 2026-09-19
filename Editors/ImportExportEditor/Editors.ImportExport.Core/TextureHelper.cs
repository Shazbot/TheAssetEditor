using System.Drawing.Imaging;
using System.IO.Compression;
using System.IO;
using Editors.ImportExport.Misc;
using Pfim;

namespace MeshImportExport
{
    public readonly record struct TexturePngExportResult(string Path, byte[] PngData);

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
