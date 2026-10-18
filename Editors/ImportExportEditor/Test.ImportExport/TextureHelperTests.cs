using System.IO;
using System.Buffers.Binary;
using System.Text;
using MeshImportExport;
using ZstdSharp;
using System.Drawing;
using DirectXTexNet;
using Editors.ImportExport.Exporting.Exporters.DdsToMaterialPng;
using Editors.ImportExport;
using Moq;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Models.FileSources;

namespace Test.ImportExport;

public class TextureHelperTests
{
    [Test]
    public void ConvertDdsToPngPreservesRedForSolidDxt1Texture()
    {
        var png = TextureHelper.ConvertDdsToPng(CreateSolidRedDxt1Dds());

        var pixel = PngTestHelper.ReadFirstPixelRgba(png);

        Assert.That(pixel.R, Is.GreaterThan(200));
        Assert.That(pixel.G, Is.LessThan(20));
        Assert.That(pixel.B, Is.LessThan(20));
    }

    [TestCase(4, 4, true)]
    [TestCase(1024, 512, true)]
    [TestCase(1024, 513, false)]
    [TestCase(1, 1, false)]
    public void RawKtx2CompatibilityGuardMatchesSharpGltfDimensionSupport(
        int width,
        int height,
        bool expected)
    {
        var image = new TextureHelper.DecodedDdsImage(width, height, Array.Empty<byte>());

        Assert.That(TextureHelper.CanEncodeKtx2ForSharpGltf(image), Is.EqualTo(expected));
    }

    [Test]
    public void DecodeDxt5SupportsNonMultipleOfFourDimensions()
    {
        var decoded = TextureHelper.DecodeDdsToBgra(CreateSolidDxt5Dds(1024, 513, 11));

        Assert.That(decoded.Width, Is.EqualTo(1024));
        Assert.That(decoded.Height, Is.EqualTo(513));
        Assert.That(decoded.BgraPixels.Length, Is.EqualTo(1024 * 513 * 4));

        var pixel = PngTestHelper.ReadFirstPixelRgba(TextureHelper.EncodeBgraToPng(decoded));
        Assert.That(pixel.R, Is.GreaterThan(200));
        Assert.That(pixel.G, Is.LessThan(20));
        Assert.That(pixel.B, Is.LessThan(20));
    }

    [Test]
    public void DecodeAndEncodeDdsPreservesRgbaChannelsWithoutIntermediatePng()
    {
        var dds = CreateA8R8G8B8Dds(17, 34, 201, 77);

        var decoded = TextureHelper.DecodeDdsToBgra(dds);

        Assert.That(decoded.Width, Is.EqualTo(1));
        Assert.That(decoded.Height, Is.EqualTo(1));
        Assert.That(decoded.BgraPixels, Is.EqualTo(new byte[] { 201, 34, 17, 77 }));

        var png = TextureHelper.EncodeBgraToPng(decoded);
        var pixel = PngTestHelper.ReadFirstPixelRgba(png);

        Assert.That(pixel.R, Is.EqualTo(17));
        Assert.That(pixel.G, Is.EqualTo(34));
        Assert.That(pixel.B, Is.EqualTo(201));
        Assert.That(pixel.A, Is.EqualTo(77));
    }

    [Test]
    public void EncodeBgraToKtx2WritesLosslessLinearRgbaPayload()
    {
        var pixels = Enumerable.Range(0, 16)
            .SelectMany(index => index % 2 == 0
                ? new byte[] { 201, 34, 17, 77 }
                : new byte[] { 30, 20, 10, 40 })
            .ToArray();
        var image = new TextureHelper.DecodedDdsImage(4, 4, pixels);

        var result = TextureHelper.EncodeBgraToKtx2(image, srgb: false);
        var data = result.Ktx2Data;

        Assert.That(data.AsSpan(0, 12).ToArray(), Is.EqualTo(new byte[]
        {
            0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32,
            0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A
        }));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12, 4)), Is.EqualTo(37u));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(44, 4)), Is.EqualTo(2u));

        var dfdOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(48, 4)));
        Assert.That(data[dfdOffset + 14], Is.EqualTo(1)); // linear transfer

        var levelOffset = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(80, 8)));
        var levelLength = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(88, 8)));
        var rawLength = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(96, 8)));
        Assert.That(rawLength, Is.EqualTo(4 * 4 * 4));

        using var decompressor = new Decompressor();
        var rgba = decompressor.Unwrap(data.AsSpan(levelOffset, levelLength)).ToArray();
        Assert.That(rgba.Length, Is.EqualTo(4 * 4 * 4));
        Assert.That(rgba.AsSpan(0, 8).ToArray(), Is.EqualTo(new byte[] { 17, 34, 201, 77, 10, 20, 30, 40 }));

        var sharpGltfImage = new SharpGLTF.Memory.MemoryImage(data);
        Assert.That(sharpGltfImage.IsKtx2, Is.True);
        Assert.That(sharpGltfImage.IsValid, Is.True);
    }

    [Test]
    public void EncodeBgraToKtx2WritesSrgbDescriptorWithLinearAlpha()
    {
        var image = new TextureHelper.DecodedDdsImage(1, 1, new byte[] { 3, 2, 1, 4 });

        var data = TextureHelper.EncodeBgraToKtx2(image, srgb: true).Ktx2Data;
        var dfdOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(48, 4)));

        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12, 4)), Is.EqualTo(43u));
        Assert.That(data[dfdOffset + 14], Is.EqualTo(2)); // sRGB transfer
        Assert.That(data[dfdOffset + 79], Is.EqualTo(0x1f)); // alpha | LINEAR
    }

    private static byte[] CreateSolidRedDxt1Dds()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("DDS "));
        writer.Write(124); // DDS_HEADER.dwSize
        writer.Write(0x00081007); // CAPS | HEIGHT | WIDTH | PIXELFORMAT | LINEARSIZE
        writer.Write(4); // dwHeight
        writer.Write(4); // dwWidth
        writer.Write(8); // dwPitchOrLinearSize: one DXT1 block
        writer.Write(0); // dwDepth
        writer.Write(0); // dwMipMapCount

        for (var i = 0; i < 11; i++)
            writer.Write(0); // dwReserved1

        writer.Write(32); // DDS_PIXELFORMAT.dwSize
        writer.Write(0x00000004); // DDPF_FOURCC
        writer.Write(Encoding.ASCII.GetBytes("DXT1"));
        writer.Write(0); // dwRGBBitCount
        writer.Write(0); // dwRBitMask
        writer.Write(0); // dwGBitMask
        writer.Write(0); // dwBBitMask
        writer.Write(0); // dwABitMask

        writer.Write(0x00001000); // DDSCAPS_TEXTURE
        writer.Write(0); // dwCaps2
        writer.Write(0); // dwCaps3
        writer.Write(0); // dwCaps4
        writer.Write(0); // dwReserved2

        // DXT1 endpoint 0 is solid red, endpoint 1 is black, and all pixels
        // select endpoint 0.
        writer.Write((ushort)0xF800); // RGB565 red
        writer.Write((ushort)0x0000); // RGB565 black
        writer.Write(0u); // four 2-bit selectors, all zero

        return stream.ToArray();
    }

    private static byte[] CreateSolidDxt5Dds(int width, int height, int mipCount)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("DDS "));
        writer.Write(124); // DDS_HEADER.dwSize
        writer.Write(0x000A1007); // CAPS | HEIGHT | WIDTH | PIXELFORMAT | MIPMAPCOUNT | LINEARSIZE
        writer.Write(height);
        writer.Write(width);
        writer.Write(Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16);
        writer.Write(0);
        writer.Write(mipCount);

        for (var i = 0; i < 11; i++)
            writer.Write(0);

        writer.Write(32); // DDS_PIXELFORMAT.dwSize
        writer.Write(0x00000004); // DDPF_FOURCC
        writer.Write(Encoding.ASCII.GetBytes("DXT5"));
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        writer.Write(0x00401008); // DDSCAPS_TEXTURE | COMPLEX | MIPMAP
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        for (var mip = 0; mip < mipCount; mip++)
        {
            var mipWidth = Math.Max(1, width >> mip);
            var mipHeight = Math.Max(1, height >> mip);
            var blocksWide = Math.Max(1, (mipWidth + 3) / 4);
            var blocksHigh = Math.Max(1, (mipHeight + 3) / 4);

            for (var block = 0; block < blocksWide * blocksHigh; block++)
            {
                writer.Write((byte)255); // alpha endpoint 0
                writer.Write((byte)255); // alpha endpoint 1
                writer.Write(new byte[6]); // alpha selectors, all endpoint 0
                writer.Write((ushort)0xF800); // RGB565 red
                writer.Write((ushort)0x0000); // RGB565 black
                writer.Write(0u); // color selectors, all endpoint 0
            }
        }

        return stream.ToArray();
    }

    private static byte[] CreateA8R8G8B8Dds(byte red, byte green, byte blue, byte alpha)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("DDS "));
        writer.Write(124);
        writer.Write(0x0000100f);
        writer.Write(1);
        writer.Write(1);
        writer.Write(4);
        writer.Write(0);
        writer.Write(0);

        for (var i = 0; i < 11; i++)
            writer.Write(0);

        writer.Write(32);
        writer.Write(0x41);
        writer.Write(0);
        writer.Write(32);
        writer.Write(0x00ff0000);
        writer.Write(0x0000ff00);
        writer.Write(0x000000ff);
        writer.Write(unchecked((int)0xff000000));

        writer.Write(0x00001000);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        writer.Write(blue);
        writer.Write(green);
        writer.Write(red);
        writer.Write(alpha);
        return stream.ToArray();
    }

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
