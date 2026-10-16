using System.Drawing;
using System.IO;
using DirectXTexNet;
using Editors.ImportExport.Exporting.Exporters.DdsToMaterialPng;
using Editors.ImportExport;
using MeshImportExport;
using Moq;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Models.FileSources;

namespace Test.ImportExport
{
    public class TextureHelperTests
    {
        // 4x4 texture: the left two columns are `left`, the right two columns are `right` (BGRA).
        private static byte[] BuildDds(DXGI_FORMAT format, byte[] leftBgra, byte[] rightBgra)
        {
            using var image = TexHelper.Instance.Initialize2D(
                DXGI_FORMAT.B8G8R8A8_UNORM, 4, 4, 1, 1, CP_FLAGS.NONE);
            var pixels = new byte[4 * 4 * 4];
            for (var y = 0; y < 4; y++)
            {
                for (var x = 0; x < 4; x++)
                    (x < 2 ? leftBgra : rightBgra).CopyTo(pixels, (y * 4 + x) * 4);
            }

            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, image.GetImage(0).Pixels, pixels.Length);
            ScratchImage? compressed = null;
            try
            {
                var toSave = image;
                if (format != DXGI_FORMAT.B8G8R8A8_UNORM)
                {
                    compressed = image.Compress(format, TEX_COMPRESS_FLAGS.DEFAULT, 0.5f);
                    toSave = compressed;
                }

                using var stream = toSave.SaveToDDSMemory(DDS_FLAGS.NONE);
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                return bytes;
            }
            finally
            {
                compressed?.Dispose();
            }
        }

        private static Bitmap DecodePng(byte[] png) => new(new MemoryStream(png));

        [TestCase(DXGI_FORMAT.B8G8R8A8_UNORM)]
        [TestCase(DXGI_FORMAT.BC3_UNORM)]
        public void ConvertDdsToPng_KeepsRedAsRedAndBlueAsBlue(DXGI_FORMAT format)
        {
            // Pfim's Rgba32 is B,G,R,A in memory and so is GDI+ Format32bppArgb: no swap.
            var dds = BuildDds(format, [0, 0, 255, 128], [255, 0, 0, 128]);

            using var png = DecodePng(TextureHelper.ConvertDdsToPng(dds));

            var red = png.GetPixel(0, 0);
            var blue = png.GetPixel(3, 3);
            Assert.That((red.R, red.G, red.B), Is.EqualTo((255, 0, 0)));
            Assert.That((blue.R, blue.G, blue.B), Is.EqualTo((0, 0, 255)));
            Assert.That(red.A, Is.EqualTo(128).Within(2));
            Assert.That(blue.A, Is.EqualTo(128).Within(2));
        }

        private static byte[] ExportMaterialPng(byte[] dds, bool convertToBlender)
        {
            var packFileService = new Mock<IPackFileService>();
            packFileService.Setup(service => service.FindFile("texture_material_map.dds"))
                .Returns(new PackFile("texture_material_map.dds", new MemorySource(dds)));
            byte[]? saved = null;
            var saveHandler = new Mock<IImageSaveHandler>();
            saveHandler.Setup(handler => handler.Save(It.IsAny<byte[]>(), It.IsAny<string>()))
                .Callback<byte[], string>((png, _) => saved = png);

            new DdsToMaterialPngExporter(packFileService.Object, saveHandler.Object)
                .Export("texture_material_map.dds", Path.Combine(Path.GetTempPath(), "out.png"), convertToBlender);

            return saved!;
        }

        [Test]
        public void DdsToMaterialPng_BlenderConversion_SwapsRedAndBlue()
        {
            // DDS texel R=200, G=100, B=50. Material maps are BC1, so they are opaque.
            var dds = BuildDds(DXGI_FORMAT.B8G8R8A8_UNORM, [50, 100, 200, 255], [50, 100, 200, 255]);

            using var plain = DecodePng(ExportMaterialPng(dds, convertToBlender: false));
            using var blender = DecodePng(ExportMaterialPng(dds, convertToBlender: true));

            var plainPixel = plain.GetPixel(1, 1);
            var blenderPixel = blender.GetPixel(1, 1);
            Assert.That((plainPixel.R, plainPixel.G, plainPixel.B, plainPixel.A), Is.EqualTo((200, 100, 50, 255)));
            // Blender wants the DDS red channel in blue and the DDS blue channel in red; green stays.
            Assert.That((blenderPixel.R, blenderPixel.G, blenderPixel.B, blenderPixel.A), Is.EqualTo((50, 100, 200, 255)));
        }

        [Test]
        public void RawBgraMipChainWriter_HeightNotMultipleOfFour_KeepsTheBottomRows()
        {
            // Large enough for the striped compression path, which only handles whole block rows.
            const int width = 4096;
            const int height = 4098;
            using var writer = Editors.ImportExport.Importing.Importers.PngToDds.PngToDdsImporter
                .CreateRawBgraMipChainWriter(
                    width,
                    height,
                    2,
                    Shared.GameFormats.RigidModel.Types.TextureType.BaseColour,
                    Shared.Core.Settings.GameTypeEnum.Warhammer3);
            Assert.That(writer.UsesLargeBcSplitCompression, Is.False);

            byte[] Solid(int w, int h)
            {
                var pixels = new byte[w * h * 4];
                for (var index = 0; index < pixels.Length; index += 4)
                {
                    pixels[index] = 50;
                    pixels[index + 1] = 100;
                    pixels[index + 2] = 200;
                    pixels[index + 3] = 255;
                }

                return pixels;
            }

            writer.WriteMip(0, Solid(width, height));
            writer.WriteMip(1, Solid(width / 2, height / 2));
            var dds = writer.Complete("tall.dds").DataSource.ReadData();

            using var stream = new MemoryStream(dds);
            using var image = Pfim.Pfimage.FromStream(stream);
            Assert.That(image.Height, Is.EqualTo(height));
            foreach (var row in new[] { height - 3, height - 1 })
            {
                var offset = row * image.Stride + (width / 2) * 4;
                // BGRA, sRGB round trip through BC1: close to (50, 100, 200).
                Assert.That(image.Data[offset], Is.EqualTo(50).Within(6));
                Assert.That(image.Data[offset + 1], Is.EqualTo(100).Within(6));
                Assert.That(image.Data[offset + 2], Is.EqualTo(200).Within(6));
            }
        }
    }
}
