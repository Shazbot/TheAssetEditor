using System.Drawing.Imaging;
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

            using var bitmap = BitmapPixelBuffer.CreateBitmap(width, height, bgraPixels);
            using var output = new MemoryStream();
            bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
            return output.ToArray();
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
