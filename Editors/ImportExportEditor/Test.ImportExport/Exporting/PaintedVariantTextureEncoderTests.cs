using System.Buffers.Binary;
using System.Reflection;
using WH3AssetHost;

namespace Test.ImportExport.Exporting;

public sealed class PaintedVariantTextureEncoderTests
{
    [Test]
    public void DdsInspector_ReadsLegacyDxt5AndMipCount()
    {
        var dds = CreateLegacyFourCcDds("DXT5", width: 1024, height: 512, mipCount: 10);

        var result = Inspect(dds);

        Assert.That(Get<string>(result, "FormatName"), Is.EqualTo("BC3_UNORM"));
        Assert.That(Get<int>(result, "Width"), Is.EqualTo(1024));
        Assert.That(Get<int>(result, "Height"), Is.EqualTo(512));
        Assert.That(Get<int>(result, "MipCount"), Is.EqualTo(10));
        Assert.That(Get<bool>(result, "IsSrgb"), Is.False);
        Assert.That(Get<bool>(result, "UsesDx10Header"), Is.False);
        Assert.That(Get<uint?>(result, "LegacyFourCc"), Is.EqualTo(FourCc("DXT5")));
    }

    [Test]
    public void DdsInspector_ReadsDx10Bc7Srgb()
    {
        var dds = CreateDx10Dds(
            dxgiFormat: 99,
            width: 2048,
            height: 2048,
            mipCount: 12,
            alphaMode: 3);

        var result = Inspect(dds);

        Assert.That(Get<string>(result, "FormatName"), Is.EqualTo("BC7_UNORM_SRGB"));
        Assert.That(Get<int>(result, "MipCount"), Is.EqualTo(12));
        Assert.That(Get<bool>(result, "IsSrgb"), Is.True);
        Assert.That(Get<bool>(result, "UsesDx10Header"), Is.True);
        Assert.That(Get<uint?>(result, "DxgiFormat"), Is.EqualTo(99));
        Assert.That(Get<uint?>(result, "Dx10AlphaMode"), Is.EqualTo(3));
    }

    [TestCase("DXT1", "BC1_UNORM")]
    [TestCase("DXT3", "BC2_UNORM")]
    [TestCase("DXT5", "BC3_UNORM")]
    [TestCase("ATI1", "BC4_UNORM")]
    [TestCase("ATI2", "BC5_UNORM")]
    public void DdsInspector_MapsCommonLegacyBlockFormats(string fourCc, string expectedFormat)
    {
        var result = Inspect(CreateLegacyFourCcDds(fourCc, 256, 256, 1));

        Assert.That(Get<string>(result, "FormatName"), Is.EqualTo(expectedFormat));
        Assert.That(Get<bool>(result, "UsesDx10Header"), Is.False);
    }

    [Test]
    public void BcnEncoder_PreservesLegacyDxt5AndMipCount()
    {
        var source = Inspect(CreateLegacyFourCcDds("DXT5", 4, 4, 3));
        var encoded = Encode(source);

        var result = Inspect(encoded);
        Assert.That(Get<string>(result, "FormatName"), Is.EqualTo("BC3_UNORM"));
        Assert.That(Get<int>(result, "MipCount"), Is.EqualTo(3));
        Assert.That(Get<bool>(result, "UsesDx10Header"), Is.False);
        Assert.That(Get<uint?>(result, "LegacyFourCc"), Is.EqualTo(FourCc("DXT5")));
    }

    [TestCase(71u, "BC1_UNORM")]
    [TestCase(72u, "BC1_UNORM_SRGB")]
    [TestCase(74u, "BC2_UNORM")]
    [TestCase(75u, "BC2_UNORM_SRGB")]
    [TestCase(77u, "BC3_UNORM")]
    [TestCase(78u, "BC3_UNORM_SRGB")]
    [TestCase(98u, "BC7_UNORM")]
    [TestCase(99u, "BC7_UNORM_SRGB")]
    public void BcnEncoder_PreservesSupportedDx10BaseColourFormats(
        uint dxgiFormat,
        string expectedFormat)
    {
        var source = Inspect(CreateDx10Dds(dxgiFormat, 4, 4, 3));
        var encoded = Encode(source);

        var result = Inspect(encoded);
        Assert.That(Get<string>(result, "FormatName"), Is.EqualTo(expectedFormat));
        Assert.That(Get<uint?>(result, "DxgiFormat"), Is.EqualTo(dxgiFormat));
        Assert.That(Get<int>(result, "MipCount"), Is.EqualTo(3));
        Assert.That(Get<bool>(result, "UsesDx10Header"), Is.True);
    }

    [Test]
    public void BcnEncoder_PreservesDx10Bc7SrgbAndAlphaMode()
    {
        var source = Inspect(CreateDx10Dds(99, 4, 4, 3, alphaMode: 3));
        var encoded = Encode(source);

        var result = Inspect(encoded);
        Assert.That(Get<string>(result, "FormatName"), Is.EqualTo("BC7_UNORM_SRGB"));
        Assert.That(Get<int>(result, "MipCount"), Is.EqualTo(3));
        Assert.That(Get<bool>(result, "UsesDx10Header"), Is.True);
        Assert.That(Get<uint?>(result, "DxgiFormat"), Is.EqualTo(99));
        Assert.That(Get<uint?>(result, "Dx10AlphaMode"), Is.EqualTo(3));
    }

    [Test]
    public void BcnEncoder_PreservesLegacyDxt1AlphaFlag()
    {
        var source = Inspect(
            CreateLegacyFourCcDds(
                "DXT1",
                4,
                4,
                3,
                pixelFormatFlags: 0x00000004 | 0x00000001));
        var encoded = Encode(source);

        var result = Inspect(encoded);
        Assert.That(Get<uint>(result, "LegacyPixelFormatFlags") & 0x1u, Is.EqualTo(0x1u));
        Assert.That(Get<uint?>(result, "LegacyFourCc"), Is.EqualTo(FourCc("DXT1")));
    }

    [Test]
    public void BcnEncoder_RejectsLegacyFloatBaseColourInsteadOfQuantizing()
    {
        var source = Inspect(CreateLegacyNumericFourCcDds(116, 4, 4, 1));

        var exception = Assert.Throws<TargetInvocationException>(() => Encode(source));
        Assert.That(exception!.InnerException, Is.TypeOf<NotSupportedException>());
        Assert.That(exception.InnerException!.Message, Does.Contain("floating-point"));
        Assert.That(exception.InnerException.Message, Does.Contain("8-bit RGBA"));
    }

    [Test]
    public void BcnEncoder_RejectsBc6hInsteadOfQuantizingHdr()
    {
        var source = Inspect(CreateDx10Dds(95, 4, 4, 3));

        var exception = Assert.Throws<TargetInvocationException>(() => Encode(source));
        Assert.That(exception!.InnerException, Is.TypeOf<NotSupportedException>());
        Assert.That(exception.InnerException!.Message, Does.Contain("HDR"));
        Assert.That(exception.InnerException.Message, Does.Contain("8-bit RGBA"));
    }

    private static object Inspect(byte[] dds)
    {
        var inspectorType = typeof(AssetHostProtocol).Assembly.GetType(
            "WH3AssetHost.DdsFormatInspector",
            throwOnError: true)!;
        var method = inspectorType.GetMethod(
            "Inspect",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(byte[])],
            modifiers: null)
            ?? throw new AssertionException("DdsFormatInspector.Inspect(byte[]) was not found.");
        return method.Invoke(null, [dds])
            ?? throw new AssertionException("DdsFormatInspector returned null.");
    }

    private static byte[] Encode(object sourceFormat)
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "wh3-painted-dds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var rgbaPath = Path.Combine(tempDirectory, "painted.rgba");

        try
        {
            var rgba = new byte[4 * 4 * 4];
            for (var index = 0; index < rgba.Length; index += 4)
            {
                rgba[index] = 200;
                rgba[index + 1] = 100;
                rgba[index + 2] = 50;
                rgba[index + 3] = 255;
            }
            File.WriteAllBytes(rgbaPath, rgba);

            var encoderType = typeof(AssetHostProtocol).Assembly.GetType(
                "WH3AssetHost.BcnDdsEncoder",
                throwOnError: true)!;
            var encoder = Activator.CreateInstance(encoderType, nonPublic: true)
                ?? throw new AssertionException("BcnDdsEncoder could not be created.");
            var method = encoderType.GetMethod(
                "EncodeRgbaFile",
                BindingFlags.Public | BindingFlags.Instance)
                ?? throw new AssertionException("BcnDdsEncoder.EncodeRgbaFile was not found.");

            return (byte[])(method.Invoke(encoder, [rgbaPath, 4, 4, sourceFormat])
                ?? throw new AssertionException("BcnDdsEncoder returned null."));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static T Get<T>(object instance, string propertyName)
        => (T)(instance.GetType().GetProperty(propertyName)?.GetValue(instance)
            ?? throw new AssertionException($"Property '{propertyName}' was not found."));

    private static byte[] CreateLegacyFourCcDds(
        string fourCc,
        int width,
        int height,
        int mipCount,
        uint pixelFormatFlags = 0x00000004)
    {
        var dds = CreateHeader(width, height, mipCount, 128);
        WriteUInt32(dds, 80, pixelFormatFlags);
        WriteUInt32(dds, 84, FourCc(fourCc));
        return dds;
    }

    private static byte[] CreateLegacyNumericFourCcDds(
        uint fourCc,
        int width,
        int height,
        int mipCount)
    {
        var dds = CreateHeader(width, height, mipCount, 128);
        WriteUInt32(dds, 80, 0x00000004);
        WriteUInt32(dds, 84, fourCc);
        return dds;
    }

    private static byte[] CreateDx10Dds(
        uint dxgiFormat,
        int width,
        int height,
        int mipCount,
        uint alphaMode = 0)
    {
        var dds = CreateHeader(width, height, mipCount, 148);
        WriteUInt32(dds, 80, 0x00000004);
        WriteUInt32(dds, 84, FourCc("DX10"));
        WriteUInt32(dds, 128, dxgiFormat);
        WriteUInt32(dds, 132, 3); // D3D10_RESOURCE_DIMENSION_TEXTURE2D
        WriteUInt32(dds, 140, 1); // array size
        WriteUInt32(dds, 144, alphaMode);
        return dds;
    }

    private static byte[] CreateHeader(int width, int height, int mipCount, int length)
    {
        var dds = new byte[length];
        WriteUInt32(dds, 0, 0x20534444);
        WriteUInt32(dds, 4, 124);
        WriteUInt32(dds, 8, mipCount > 1 ? 0x00020000u : 0u);
        WriteUInt32(dds, 12, checked((uint)height));
        WriteUInt32(dds, 16, checked((uint)width));
        WriteUInt32(dds, 28, checked((uint)mipCount));
        WriteUInt32(dds, 76, 32);
        return dds;
    }

    private static uint FourCc(string value)
        => (uint)value[0]
            | ((uint)value[1] << 8)
            | ((uint)value[2] << 16)
            | ((uint)value[3] << 24);

    private static void WriteUInt32(byte[] target, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset, sizeof(uint)), value);
}
