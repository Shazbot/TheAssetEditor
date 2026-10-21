using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Pfim;

namespace MeshImportExport
{
    public class TextureHelper
    {
        public static byte[] ConvertDdsToPng(byte[] ddsbyteSteam)
        {
            using var stream = new MemoryStream(ddsbyteSteam);
            using var image = Pfimage.FromStream(stream);

            var pixelFormat = image.Format switch
            {
                Pfim.ImageFormat.Rgba32 => PixelFormat.Format32bppArgb,
                Pfim.ImageFormat.Rgb24 => PixelFormat.Format24bppRgb,
                _ => throw new NotSupportedException($"Unsupported DDS format: {image.Format}")
            };

            // Pfim's decoded byte layout is already compatible with the matching
            // GDI+ pixel format. Do not swap red/blue here: doing so corrupts the
            // atlas BaseColour before it is encoded back to DDS.
            var handle = GCHandle.Alloc(image.Data, GCHandleType.Pinned);
            try
            {
                var data = Marshal.UnsafeAddrOfPinnedArrayElement(image.Data, 0);
                using var bitmap = new Bitmap(image.Width, image.Height, image.Stride, pixelFormat, data);
                using var output = new MemoryStream();
                bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
                return output.ToArray();
            }
            finally
            {
                handle.Free();
            }
        }

        public static byte[] ConvertPngToDds(byte[] png)
        {
            using var m = new MemoryStream();
            using var w = new BinaryWriter(m);
            w.Write(png);
            m.Seek(0, SeekOrigin.Begin);
            using var bitmap = new Bitmap(m);

            PixelFormat pixelFormat = PixelFormat.Format32bppArgb;
            Pfim.ImageFormat imageFormat = Pfim.ImageFormat.Rgba32;
            if (bitmap.PixelFormat == PixelFormat.Format32bppArgb)
            {
                pixelFormat = PixelFormat.Format32bppArgb;
                imageFormat = Pfim.ImageFormat.Rgba32;
            }
            else if (bitmap.PixelFormat == PixelFormat.Format24bppRgb)
            {
                pixelFormat = PixelFormat.Format24bppRgb;
                imageFormat = Pfim.ImageFormat.Rgb24;
            }
            else
            {
                throw new NotSupportedException($"Unsupported PNG format: {bitmap.PixelFormat}");
            }

            BitmapData bitmapData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, pixelFormat);
            byte[] imageData = new byte[bitmapData.Stride * bitmapData.Height];
            System.Runtime.InteropServices.Marshal.Copy(bitmapData.Scan0, imageData, 0, imageData.Length);
            bitmap.UnlockBits(bitmapData);

            //var image = Pfim.Pfim.FromStream(//Create(imageData, bitmap.Width, bitmap.Height, imageFormat);

            using var b = new MemoryStream();
            var imageNew = Pfimage.FromStream(b);
            using var writer = new BinaryWriter(b);
            writer.Write(imageNew.Data);
            return b.ToArray();
        }
    }
}
