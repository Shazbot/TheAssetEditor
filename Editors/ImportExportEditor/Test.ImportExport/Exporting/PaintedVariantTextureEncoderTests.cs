using System.Reflection;
using WH3AssetHost;

namespace Test.ImportExport.Exporting;

public sealed class PaintedVariantTextureEncoderTests
{
    [Test]
    public void Bc7Encoder_WritesSrgbDds()
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "wh3-painted-dds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var pngPath = Path.Combine(tempDirectory, "pixel.png");

        try
        {
            File.WriteAllBytes(
                pngPath,
                Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));

            var encoderType = typeof(AssetHostProtocol).Assembly.GetType(
                "WH3AssetHost.Bc7DdsEncoder",
                throwOnError: true)!;
            var encodeMethod = encoderType.GetMethod(
                "EncodePngFile",
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new AssertionException("Bc7DdsEncoder.EncodePngFile was not found.");

            var dds = (byte[])encodeMethod.Invoke(null, [pngPath])!;

            Assert.That(dds.Length, Is.GreaterThanOrEqualTo(148));
            Assert.That(BitConverter.ToUInt32(dds, 0), Is.EqualTo(0x20534444u), "DDS magic");
            Assert.That(
                System.Text.Encoding.ASCII.GetString(dds, 84, 4),
                Is.EqualTo("DX10"),
                "BC7 requires the DDS DX10 header");
            Assert.That(BitConverter.ToUInt32(dds, 128), Is.EqualTo(99u), "DXGI_FORMAT_BC7_UNORM_SRGB");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }
}
