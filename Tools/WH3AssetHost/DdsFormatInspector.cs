using System.Buffers.Binary;
using System.Text;

namespace WH3AssetHost;

internal sealed record DdsSourceFormat(
    string FormatName,
    int Width,
    int Height,
    int MipCount,
    bool IsSrgb,
    bool UsesDx10Header,
    uint? DxgiFormat,
    uint? Dx10AlphaMode,
    uint? LegacyFourCc,
    uint LegacyPixelFormatFlags);

internal static class DdsFormatInspector
{
    private const uint DdsMagic = 0x20534444;
    private const uint DdpfAlpha = 0x00000002;
    private const uint DdpfFourCc = 0x00000004;
    private const uint DdpfRgb = 0x00000040;
    private const uint DdpfLuminance = 0x00020000;

    private static readonly IReadOnlyDictionary<uint, string> DxgiFormats =
        new Dictionary<uint, string>
        {
            [2] = "R32G32B32A32_FLOAT",
            [10] = "R16G16B16A16_FLOAT",
            [11] = "R16G16B16A16_UNORM",
            [12] = "R16G16B16A16_UINT",
            [13] = "R16G16B16A16_SNORM",
            [14] = "R16G16B16A16_SINT",
            [16] = "R32G32_FLOAT",
            [24] = "R10G10B10A2_UNORM",
            [25] = "R10G10B10A2_UINT",
            [26] = "R11G11B10_FLOAT",
            [28] = "R8G8B8A8_UNORM",
            [29] = "R8G8B8A8_UNORM_SRGB",
            [30] = "R8G8B8A8_UINT",
            [31] = "R8G8B8A8_SNORM",
            [32] = "R8G8B8A8_SINT",
            [34] = "R16G16_FLOAT",
            [35] = "R16G16_UNORM",
            [36] = "R16G16_UINT",
            [37] = "R16G16_SNORM",
            [38] = "R16G16_SINT",
            [41] = "R32_FLOAT",
            [42] = "R32_UINT",
            [43] = "R32_SINT",
            [49] = "R8G8_UNORM",
            [50] = "R8G8_UINT",
            [51] = "R8G8_SNORM",
            [52] = "R8G8_SINT",
            [54] = "R16_FLOAT",
            [56] = "R16_UNORM",
            [57] = "R16_UINT",
            [58] = "R16_SNORM",
            [59] = "R16_SINT",
            [61] = "R8_UNORM",
            [62] = "R8_UINT",
            [63] = "R8_SNORM",
            [64] = "R8_SINT",
            [65] = "A8_UNORM",
            [67] = "R9G9B9E5_SHAREDEXP",
            [71] = "BC1_UNORM",
            [72] = "BC1_UNORM_SRGB",
            [74] = "BC2_UNORM",
            [75] = "BC2_UNORM_SRGB",
            [77] = "BC3_UNORM",
            [78] = "BC3_UNORM_SRGB",
            [80] = "BC4_UNORM",
            [81] = "BC4_SNORM",
            [83] = "BC5_UNORM",
            [84] = "BC5_SNORM",
            [85] = "B5G6R5_UNORM",
            [86] = "B5G5R5A1_UNORM",
            [87] = "B8G8R8A8_UNORM",
            [88] = "B8G8R8X8_UNORM",
            [91] = "B8G8R8A8_UNORM_SRGB",
            [93] = "B8G8R8X8_UNORM_SRGB",
            [95] = "BC6H_UF16",
            [96] = "BC6H_SF16",
            [98] = "BC7_UNORM",
            [99] = "BC7_UNORM_SRGB",
            [115] = "B4G4R4A4_UNORM"
        };

    public static DdsSourceFormat Inspect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Inspect(data.AsSpan());
    }

    public static DdsSourceFormat Inspect(ReadOnlySpan<byte> data)
    {
        if (data.Length < 128)
            throw new InvalidDataException("DDS data is shorter than the standard 128-byte header.");
        if (ReadUInt32(data, 0) != DdsMagic)
            throw new InvalidDataException("Texture does not start with the DDS magic.");
        if (ReadUInt32(data, 4) != 124)
            throw new InvalidDataException("DDS header size is not 124 bytes.");
        if (ReadUInt32(data, 76) != 32)
            throw new InvalidDataException("DDS pixel-format header size is not 32 bytes.");

        var headerFlags = ReadUInt32(data, 8);
        var height = checked((int)ReadUInt32(data, 12));
        var width = checked((int)ReadUInt32(data, 16));
        var mipCount = (headerFlags & 0x00020000) != 0
            ? checked((int)ReadUInt32(data, 28))
            : 1;
        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"DDS has invalid dimensions {width}x{height}.");
        if (mipCount <= 0)
            mipCount = 1;

        var pixelFormatFlags = ReadUInt32(data, 80);
        var fourCc = ReadUInt32(data, 84);

        if ((pixelFormatFlags & DdpfFourCc) != 0)
        {
            if (fourCc == FourCc("DX10"))
            {
                if (data.Length < 148)
                    throw new InvalidDataException("DDS declares a DX10 header but is shorter than 148 bytes.");

                var dxgi = ReadUInt32(data, 128);
                if (!DxgiFormats.TryGetValue(dxgi, out var format))
                {
                    throw new InvalidDataException(
                        $"DDS uses DXGI format {dxgi}, which is not yet mapped for painted export.");
                }

                var resourceDimension = ReadUInt32(data, 132);
                var miscFlag = ReadUInt32(data, 136);
                var arraySize = ReadUInt32(data, 140);
                if (resourceDimension != 3 || arraySize != 1 || (miscFlag & 0x4) != 0)
                {
                    throw new InvalidDataException(
                        "Painted export currently supports only non-array 2D DDS textures.");
                }

                return new DdsSourceFormat(
                    format,
                    width,
                    height,
                    mipCount,
                    format.EndsWith("_SRGB", StringComparison.Ordinal),
                    UsesDx10Header: true,
                    DxgiFormat: dxgi,
                    Dx10AlphaMode: ReadUInt32(data, 144) & 0x7,
                    LegacyFourCc: null,
                    LegacyPixelFormatFlags: 0);
            }

            ValidateLegacyTextureShape(data);

            var legacyFormat = fourCc switch
            {
                var value when value == FourCc("DXT1") => "BC1_UNORM",
                var value when value is var _ && value == FourCc("DXT2") => "BC2_UNORM",
                var value when value == FourCc("DXT3") => "BC2_UNORM",
                var value when value is var _ && value == FourCc("DXT4") => "BC3_UNORM",
                var value when value == FourCc("DXT5") => "BC3_UNORM",
                var value when value == FourCc("ATI1") || value == FourCc("BC4U") => "BC4_UNORM",
                var value when value == FourCc("BC4S") => "BC4_SNORM",
                var value when value == FourCc("ATI2") || value == FourCc("BC5U") => "BC5_UNORM",
                var value when value == FourCc("BC5S") => "BC5_SNORM",
                // A few legacy DDS writers store D3DFORMAT numeric constants in dwFourCC.
                36u => "R16G16B16A16_UNORM",
                110u => "R16G16B16A16_SNORM",
                111u => "R16_FLOAT",
                112u => "R16G16_FLOAT",
                113u => "R16G16B16A16_FLOAT",
                114u => "R32_FLOAT",
                115u => "R32G32_FLOAT",
                116u => "R32G32B32A32_FLOAT",
                _ => null
            };

            if (legacyFormat == null)
            {
                var text = FourCcText(fourCc);
                throw new InvalidDataException(
                    $"DDS uses unsupported legacy FourCC 0x{fourCc:X8}{(text == null ? string.Empty : $" ('{text}')")}.");
            }

            return new DdsSourceFormat(
                legacyFormat,
                width,
                height,
                mipCount,
                IsSrgb: false,
                UsesDx10Header: false,
                DxgiFormat: null,
                Dx10AlphaMode: null,
                LegacyFourCc: fourCc,
                LegacyPixelFormatFlags: pixelFormatFlags);
        }

        var rgbBitCount = ReadUInt32(data, 88);
        var rMask = ReadUInt32(data, 92);
        var gMask = ReadUInt32(data, 96);
        var bMask = ReadUInt32(data, 100);
        var aMask = ReadUInt32(data, 104);

        string? uncompressed = null;
        if ((pixelFormatFlags & DdpfRgb) != 0)
        {
            uncompressed = (rgbBitCount, rMask, gMask, bMask, aMask) switch
            {
                (32, 0x000000ff, 0x0000ff00, 0x00ff0000, 0xff000000) => "R8G8B8A8_UNORM",
                (32, 0x00ff0000, 0x0000ff00, 0x000000ff, 0xff000000) => "B8G8R8A8_UNORM",
                (32, 0x00ff0000, 0x0000ff00, 0x000000ff, 0x00000000) => "B8G8R8X8_UNORM",
                (16, 0x0000f800, 0x000007e0, 0x0000001f, 0x00000000) => "B5G6R5_UNORM",
                (16, 0x00007c00, 0x000003e0, 0x0000001f, 0x00008000) => "B5G5R5A1_UNORM",
                (16, 0x00000f00, 0x000000f0, 0x0000000f, 0x0000f000) => "B4G4R4A4_UNORM",
                _ => null
            };
        }
        else if ((pixelFormatFlags & DdpfLuminance) != 0)
        {
            uncompressed = (rgbBitCount, rMask, gMask, aMask) switch
            {
                (8, 0x000000ff, 0x00000000, 0x00000000) => "R8_UNORM",
                (16, 0x0000ffff, 0x00000000, 0x00000000) => "R16_UNORM",
                (16, 0x000000ff, 0x00000000, 0x0000ff00) => "R8G8_UNORM",
                _ => null
            };
        }
        else if ((pixelFormatFlags & DdpfAlpha) != 0 && rgbBitCount == 8 && aMask == 0x000000ff)
        {
            uncompressed = "A8_UNORM";
        }

        if (uncompressed == null)
        {
            throw new InvalidDataException(
                $"DDS uses an unsupported legacy uncompressed layout: flags=0x{pixelFormatFlags:X8}, "
                + $"bits={rgbBitCount}, masks=0x{rMask:X8}/0x{gMask:X8}/0x{bMask:X8}/0x{aMask:X8}.");
        }

        ValidateLegacyTextureShape(data);

        return new DdsSourceFormat(
            uncompressed,
            width,
            height,
            mipCount,
            IsSrgb: false,
            UsesDx10Header: false,
            DxgiFormat: null,
            Dx10AlphaMode: null,
            LegacyFourCc: null,
            LegacyPixelFormatFlags: pixelFormatFlags);
    }

    private static void ValidateLegacyTextureShape(ReadOnlySpan<byte> data)
    {
        var caps2 = ReadUInt32(data, 112);
        if ((caps2 & (0x00000200u | 0x00200000u)) != 0 || ReadUInt32(data, 24) > 1)
        {
            throw new InvalidDataException(
                "Painted export currently supports only non-volume, non-cubemap DDS textures.");
        }
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, sizeof(uint)));

    internal static uint FourCc(string value)
    {
        if (value.Length != 4)
            throw new ArgumentException("FourCC values must contain exactly four characters.", nameof(value));
        return (uint)value[0]
            | ((uint)value[1] << 8)
            | ((uint)value[2] << 16)
            | ((uint)value[3] << 24);
    }

    private static string? FourCcText(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        foreach (var character in bytes)
        {
            if (character is < 0x20 or > 0x7e)
                return null;
        }
        return Encoding.ASCII.GetString(bytes);
    }
}
