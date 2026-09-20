using System.Buffers.Binary;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;

namespace WH3AssetHost;

/// <summary>
/// Encodes the unit painter's RGBA8 staging data back to the same DDS storage
/// format used by the source BaseColour texture. The vanilla BaseColour set is
/// overwhelmingly BC1/BC2/BC3/BC7; HDR/float formats are rejected rather than
/// being silently quantized through the painter's 8-bit path.
/// </summary>
internal sealed class BcnDdsEncoder
{
    private const uint DdpfAlphaPixels = 0x00000001;

    public byte[] EncodeRgbaFile(
        string rgbaPath,
        int width,
        int height,
        DdsSourceFormat sourceFormat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rgbaPath);
        ArgumentNullException.ThrowIfNull(sourceFormat);

        var fullPath = Path.GetFullPath(rgbaPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Painted RGBA input was not found.", fullPath);
        if (width != sourceFormat.Width || height != sourceFormat.Height)
        {
            throw new InvalidDataException(
                $"Painted RGBA dimensions {width}x{height} do not match source DDS "
                + $"{sourceFormat.Width}x{sourceFormat.Height}.");
        }

        var expectedBytes = checked(width * height * 4);
        var rgba = File.ReadAllBytes(fullPath);
        if (rgba.Length != expectedBytes)
        {
            throw new InvalidDataException(
                $"Painted RGBA input has {rgba.Length} bytes; expected {expectedBytes} for {width}x{height}.");
        }

        return EncodeRgba(rgba, width, height, sourceFormat);
    }

    internal byte[] EncodeRgba(
        ReadOnlySpan<byte> rgba,
        int width,
        int height,
        DdsSourceFormat sourceFormat)
    {
        ArgumentNullException.ThrowIfNull(sourceFormat);
        if (width != sourceFormat.Width || height != sourceFormat.Height)
        {
            throw new InvalidDataException(
                $"Painted RGBA dimensions {width}x{height} do not match source DDS "
                + $"{sourceFormat.Width}x{sourceFormat.Height}.");
        }

        var expectedBytes = checked(width * height * 4);
        if (rgba.Length != expectedBytes)
        {
            throw new InvalidDataException(
                $"Painted RGBA input has {rgba.Length} bytes; expected {expectedBytes} for {width}x{height}.");
        }

        var baseCompressionFormat = GetSupportedCompressionFormat(sourceFormat);
        var sourceDeclaresOpaqueAlpha = sourceFormat.UsesDx10Header && sourceFormat.Dx10AlphaMode == 3u;
        var useBc1Alpha =
            baseCompressionFormat == CompressionFormat.Bc1
            && HasTransparency(rgba)
            && !sourceDeclaresOpaqueAlpha;
        var compressionFormat = useBc1Alpha
            ? CompressionFormat.Bc1WithAlpha
            : baseCompressionFormat;

        var encoder = new BcEncoder();
        encoder.OutputOptions.GenerateMipMaps = sourceFormat.MipCount > 1;
        encoder.OutputOptions.MaxMipMapLevel = sourceFormat.MipCount;
        encoder.OutputOptions.Format = compressionFormat;
        encoder.OutputOptions.FileFormat = OutputFileFormat.Dds;
        // Export is an offline operation, so prefer the library's balanced
        // compressor over its visibly lower-quality fast BC7 path.
        encoder.OutputOptions.Quality = CompressionQuality.Balanced;
        encoder.OutputOptions.DdsPreferDxt10Header = sourceFormat.UsesDx10Header;
        encoder.OutputOptions.DdsBc1WriteAlphaFlag =
            (sourceFormat.LegacyPixelFormatFlags & DdpfAlphaPixels) != 0;
        encoder.Options.IsParallel = true;

        using var stream = new MemoryStream();
        encoder.EncodeToStream(
            rgba,
            width,
            height,
            BCnEncoder.Encoder.PixelFormat.Rgba32,
            stream);

        var dds = stream.ToArray();
        NormalizeCompressedHeader(dds, sourceFormat);
        PreserveSourceHeaderMetadata(dds, sourceFormat);

        var actual = DdsFormatInspector.Inspect(dds);
        ValidateRoundTrip(sourceFormat, actual);
        return dds;
    }

    private static bool HasTransparency(ReadOnlySpan<byte> rgba)
    {
        for (var index = 3; index < rgba.Length; index += 4)
        {
            if (rgba[index] < byte.MaxValue)
                return true;
        }
        return false;
    }

    private static CompressionFormat GetSupportedCompressionFormat(DdsSourceFormat sourceFormat)
    {
        if (sourceFormat.FormatName.StartsWith("BC6H", StringComparison.Ordinal)
            || sourceFormat.FormatName.Contains("FLOAT", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"BaseColour DDS format '{sourceFormat.FormatName}' contains HDR/floating-point data. "
                + "The current unit painter works in 8-bit RGBA, so exporting it would lose source values. "
                + "This texture is intentionally left unsupported until the painter has a float/HDR path.");
        }

        if (!sourceFormat.UsesDx10Header)
        {
            var fourCc = sourceFormat.LegacyFourCc;
            if (fourCc == DdsFormatInspector.FourCc("DXT1"))
                return CompressionFormat.Bc1;
            if (fourCc == DdsFormatInspector.FourCc("DXT3"))
                return CompressionFormat.Bc2;
            if (fourCc == DdsFormatInspector.FourCc("DXT5"))
                return CompressionFormat.Bc3;

            throw Unsupported(sourceFormat);
        }

        return sourceFormat.DxgiFormat.GetValueOrDefault() switch
        {
            71u or 72u => CompressionFormat.Bc1,
            74u or 75u => CompressionFormat.Bc2,
            77u or 78u => CompressionFormat.Bc3,
            98u or 99u => CompressionFormat.Bc7,
            _ => throw Unsupported(sourceFormat)
        };
    }

    private static NotSupportedException Unsupported(DdsSourceFormat sourceFormat)
        => new(
            $"BaseColour DDS format '{sourceFormat.FormatName}' cannot be losslessly round-tripped "
            + "through the current 8-bit unit painter. Supported formats are BC1/DXT1, BC2/DXT3, "
            + "BC3/DXT5, and BC7.");

    private static void NormalizeCompressedHeader(byte[] dds, DdsSourceFormat sourceFormat)
    {
        // BCnEncoder 2.3 writes the mip count and mip caps, but does not set
        // DDSD_MIPMAPCOUNT or DDSD_LINEARSIZE. Some DDS readers tolerate that;
        // WH3 output should be a standards-complete DDS instead of relying on it.
        const uint ddsdMipmapCount = 0x00020000;
        const uint ddsdLinearSize = 0x00080000;
        const uint ddsCapsComplex = 0x00000008;
        const uint ddsCapsTexture = 0x00001000;
        const uint ddsCapsMipmap = 0x00400000;

        var flags = ReadUInt32(dds, 8) | ddsdLinearSize;
        if (sourceFormat.MipCount > 1)
            flags |= ddsdMipmapCount;
        else
            flags &= ~ddsdMipmapCount;
        WriteUInt32(dds, 8, flags);

        var bytesPerBlock = sourceFormat.FormatName.StartsWith("BC1_", StringComparison.Ordinal)
            ? 8u
            : 16u;
        var blockWidth = Math.Max(1u, ((uint)sourceFormat.Width + 3u) / 4u);
        var blockHeight = Math.Max(1u, ((uint)sourceFormat.Height + 3u) / 4u);
        WriteUInt32(dds, 20, checked(blockWidth * blockHeight * bytesPerBlock));

        var caps = ReadUInt32(dds, 108) | ddsCapsTexture;
        if (sourceFormat.MipCount > 1)
            caps |= ddsCapsComplex | ddsCapsMipmap;
        else
            caps &= ~(ddsCapsComplex | ddsCapsMipmap);
        WriteUInt32(dds, 108, caps);
    }

    private static void PreserveSourceHeaderMetadata(byte[] dds, DdsSourceFormat sourceFormat)
    {
        if (dds.Length < 128)
            throw new InvalidDataException("BCnEncoder produced a DDS shorter than the standard header.");

        if (sourceFormat.UsesDx10Header)
        {
            if (dds.Length < 148 || ReadUInt32(dds, 84) != DdsFormatInspector.FourCc("DX10"))
            {
                throw new InvalidDataException(
                    "BCnEncoder was asked to preserve a DX10 DDS header but did not emit one.");
            }

            if (!sourceFormat.DxgiFormat.HasValue)
                throw new InvalidDataException("Source DX10 DDS did not retain its DXGI format.");

            // BCnEncoder emits the UNORM member of each BC family. The compressed
            // payload is identical for the corresponding sRGB variant, so restore
            // the exact source DXGI enum after compression.
            WriteUInt32(dds, 128, sourceFormat.DxgiFormat.Value);

            if (sourceFormat.Dx10AlphaMode.HasValue)
            {
                var miscFlags2 = ReadUInt32(dds, 144);
                miscFlags2 = (miscFlags2 & ~0x7u) | (sourceFormat.Dx10AlphaMode.Value & 0x7u);
                WriteUInt32(dds, 144, miscFlags2);
            }
            return;
        }

        if (!sourceFormat.LegacyFourCc.HasValue)
            throw Unsupported(sourceFormat);
        if (ReadUInt32(dds, 84) == DdsFormatInspector.FourCc("DX10"))
        {
            throw new InvalidDataException(
                "BCnEncoder was asked to preserve a legacy DDS header but emitted a DX10 header.");
        }

        WriteUInt32(dds, 84, sourceFormat.LegacyFourCc.Value);

        // DXT1's optional alpha-pixel flag is not consistently used by DDS tools.
        // Preserve the source bit exactly rather than imposing BCnEncoder's default.
        var pixelFormatFlags = ReadUInt32(dds, 80);
        pixelFormatFlags =
            (pixelFormatFlags & ~DdpfAlphaPixels)
            | (sourceFormat.LegacyPixelFormatFlags & DdpfAlphaPixels);
        WriteUInt32(dds, 80, pixelFormatFlags);
    }

    private static void ValidateRoundTrip(DdsSourceFormat expected, DdsSourceFormat actual)
    {
        if (!string.Equals(actual.FormatName, expected.FormatName, StringComparison.OrdinalIgnoreCase)
            || actual.Width != expected.Width
            || actual.Height != expected.Height
            || actual.MipCount != expected.MipCount
            || actual.UsesDx10Header != expected.UsesDx10Header
            || actual.DxgiFormat != expected.DxgiFormat
            || actual.Dx10AlphaMode != expected.Dx10AlphaMode
            || actual.LegacyFourCc != expected.LegacyFourCc
            || (actual.LegacyPixelFormatFlags & DdpfAlphaPixels)
                != (expected.LegacyPixelFormatFlags & DdpfAlphaPixels))
        {
            throw new InvalidDataException(
                "BCnEncoder output did not preserve the source DDS layout: "
                + $"expected {Describe(expected)}, got {Describe(actual)}.");
        }
    }

    private static string Describe(DdsSourceFormat format)
        => $"{format.FormatName} {format.Width}x{format.Height} {format.MipCount} mips "
            + (format.UsesDx10Header
                ? $"DX10/{format.DxgiFormat}"
                : $"legacy/0x{format.LegacyFourCc ?? 0:X8}");

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, sizeof(uint)));

    private static void WriteUInt32(Span<byte> data, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(offset, sizeof(uint)), value);
}
