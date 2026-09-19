using System.IO;
using System.Buffers.Binary;
using System.Text;
using MeshImportExport;
using ZstdSharp;

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
    public void ZstdTextureProbeReportsCompressedTransformedPayload()
    {
        var image = new TextureHelper.DecodedDdsImage(
            4,
            4,
            Enumerable.Repeat(new byte[] { 30, 20, 10, 255 }, 16)
                .SelectMany(x => x)
                .ToArray());

        var result = TextureHelper.ProbeBgraToRgbaZstd(image);

        Assert.That(result.RawRgbaBytes, Is.EqualTo(4 * 4 * 4));
        Assert.That(result.ZstdBytes, Is.GreaterThan(0));
        Assert.That(result.ZstdBytes, Is.LessThan(result.RawRgbaBytes));
        Assert.That(result.CompressionLevel, Is.EqualTo(1));
        Assert.That(result.RgbaConvertMs, Is.GreaterThanOrEqualTo(0));
        Assert.That(result.ZstdMs, Is.GreaterThanOrEqualTo(0));
    }

    [TestCase(4, 4, true)]
    [TestCase(512, 512, true)]
    [TestCase(1024, 1024, false)]
    [TestCase(2048, 2048, false)]
    [TestCase(510, 512, false)]
    public void RawKtx2CompatibilityGuardUsesOnlyProvenPreviewSizes(
        int width,
        int height,
        bool expected)
    {
        var image = new TextureHelper.DecodedDdsImage(width, height, Array.Empty<byte>());

        Assert.That(TextureHelper.CanEncodeKtx2ForSharpGltf(image), Is.EqualTo(expected));
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

}
