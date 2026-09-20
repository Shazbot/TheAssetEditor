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

        Assert.That(Get<string>(result, "TexconvFormat"), Is.EqualTo("BC3_UNORM"));
        Assert.That(Get<int>(result, "Width"), Is.EqualTo(1024));
        Assert.That(Get<int>(result, "Height"), Is.EqualTo(512));
        Assert.That(Get<int>(result, "MipCount"), Is.EqualTo(10));
        Assert.That(Get<bool>(result, "IsSrgb"), Is.False);
        Assert.That(Get<bool>(result, "PreferLegacyHeader"), Is.True);
    }

    [Test]
    public void DdsInspector_ReadsDx10Bc7Srgb()
    {
        var dds = CreateDx10Dds(dxgiFormat: 99, width: 2048, height: 2048, mipCount: 12);

        var result = Inspect(dds);

        Assert.That(Get<string>(result, "TexconvFormat"), Is.EqualTo("BC7_UNORM_SRGB"));
        Assert.That(Get<int>(result, "MipCount"), Is.EqualTo(12));
        Assert.That(Get<bool>(result, "IsSrgb"), Is.True);
        Assert.That(Get<bool>(result, "PreferLegacyHeader"), Is.False);
    }

    [TestCase("DXT1", "BC1_UNORM")]
    [TestCase("DXT3", "BC2_UNORM")]
    [TestCase("ATI1", "BC4_UNORM")]
    [TestCase("ATI2", "BC5_UNORM")]
    public void DdsInspector_MapsCommonLegacyBlockFormats(string fourCc, string expectedFormat)
    {
        var result = Inspect(CreateLegacyFourCcDds(fourCc, 256, 256, 1));

        Assert.That(Get<string>(result, "TexconvFormat"), Is.EqualTo(expectedFormat));
        Assert.That(Get<bool>(result, "PreferLegacyHeader"), Is.True);
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

    private static T Get<T>(object instance, string propertyName)
        => (T)(instance.GetType().GetProperty(propertyName)?.GetValue(instance)
            ?? throw new AssertionException($"Property '{propertyName}' was not found."));

    private static byte[] CreateLegacyFourCcDds(
        string fourCc,
        int width,
        int height,
        int mipCount)
    {
        var dds = CreateHeader(width, height, mipCount, 128);
        WriteUInt32(dds, 80, 0x00000004);
        WriteUInt32(dds, 84, FourCc(fourCc));
        return dds;
    }

    private static byte[] CreateDx10Dds(
        uint dxgiFormat,
        int width,
        int height,
        int mipCount)
    {
        var dds = CreateHeader(width, height, mipCount, 148);
        WriteUInt32(dds, 80, 0x00000004);
        WriteUInt32(dds, 84, FourCc("DX10"));
        WriteUInt32(dds, 128, dxgiFormat);
        WriteUInt32(dds, 132, 3); // D3D10_RESOURCE_DIMENSION_TEXTURE2D
        WriteUInt32(dds, 140, 1); // array size
        return dds;
    }

    private static byte[] CreateHeader(int width, int height, int mipCount, int length)
    {
        var dds = new byte[length];
        WriteUInt32(dds, 0, 0x20534444);
        WriteUInt32(dds, 4, 124);
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
