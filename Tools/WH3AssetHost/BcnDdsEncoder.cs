using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;

namespace WH3AssetHost;

/// <summary>
/// Encodes the unit painter's 8-bit RGBA output back to the same DDS storage
/// format used by the source BaseColour texture. The vanilla BaseColour set is
/// overwhelmingly BC1/BC2/BC3/BC7; HDR/float formats are rejected rather than
/// being silently quantized through the painter's 8-bit path.
/// </summary>
internal sealed class BcnDdsEncoder
{
    private const uint DdpfAlphaPixels = 0x00000001;

    public byte[] EncodePngFile(string pngPath, DdsSourceFormat sourceFormat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pngPath);
        ArgumentNullException.ThrowIfNull(sourceFormat);

        var baseCompressionFormat = GetSupportedCompressionFormat(sourceFormat);
        var image = ReadPng(pngPath);
        if (image.Width != sourceFormat.Width || image.Height != sourceFormat.Height)
        {
            throw new InvalidDataException(
                $"Painted PNG dimensions {image.Width}x{image.Height} do not match source DDS "
                + $"{sourceFormat.Width}x{sourceFormat.Height}.");
        }

        var compressionFormat =
            baseCompressionFormat == CompressionFormat.Bc1 && image.HasTransparency
                ? CompressionFormat.Bc1WithAlpha
                : baseCompressionFormat;

        var encoder = new BcEncoder();
        encoder.OutputOptions.GenerateMipMaps = sourceFormat.MipCount > 1;
        encoder.OutputOptions.MaxMipMapLevel = sourceFormat.MipCount;
        encoder.OutputOptions.Format = compressionFormat;
        encoder.OutputOptions.FileFormat = OutputFileFormat.Dds;
        encoder.OutputOptions.Quality =
            compressionFormat is CompressionFormat.Bc7
                ? CompressionQuality.Fast
                : CompressionQuality.Balanced;
        encoder.OutputOptions.DdsPreferDxt10Header = sourceFormat.UsesDx10Header;
        encoder.OutputOptions.DdsBc1WriteAlphaFlag =
            (sourceFormat.LegacyPixelFormatFlags & DdpfAlphaPixels) != 0;
        encoder.Options.IsParallel = true;

        using var stream = new MemoryStream();
        encoder.EncodeToStream(
            image.Data,
            image.Width,
            image.Height,
            BCnEncoder.Encoder.PixelFormat.Rgba32,
            stream);

        var dds = stream.ToArray();
        PreserveSourceHeaderMetadata(dds, sourceFormat);

        var actual = DdsFormatInspector.Inspect(dds);
        ValidateRoundTrip(sourceFormat, actual);
        return dds;
    }

    private static CompressionFormat GetSupportedCompressionFormat(DdsSourceFormat sourceFormat)
    {
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

        return sourceFormat.DxgiFormat switch
        {
            71 or 72 => CompressionFormat.Bc1,
            74 or 75 => CompressionFormat.Bc2,
            77 or 78 => CompressionFormat.Bc3,
            98 or 99 => CompressionFormat.Bc7,
            95 or 96 => throw new NotSupportedException(
                $"BaseColour DDS format '{sourceFormat.FormatName}' contains HDR floating-point data. "
                + "The current unit painter works in 8-bit RGBA, so exporting it would lose HDR values. "
                + "This texture is intentionally left unsupported until the painter has a float/HDR path."),
            _ => throw Unsupported(sourceFormat)
        };
    }

    private static NotSupportedException Unsupported(DdsSourceFormat sourceFormat)
        => new(
            $"BaseColour DDS format '{sourceFormat.FormatName}' cannot be losslessly round-tripped "
            + "through the current 8-bit unit painter. Supported formats are BC1/DXT1, BC2/DXT3, "
            + "BC3/DXT5, and BC7.");

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

    private static RgbaImage ReadPng(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Painted PNG input was not found.", fullPath);

        using var source = new Bitmap(fullPath);
        using var bitmap = new Bitmap(
            source.Width,
            source.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.DrawImageUnscaled(source, 0, 0);
        }

        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bitmapData = bitmap.LockBits(
            rectangle,
            ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = checked(bitmap.Width * 4);
            var bgraRow = new byte[rowBytes];
            var rgba = new byte[checked(rowBytes * bitmap.Height)];
            var hasTransparency = false;

            for (var y = 0; y < bitmap.Height; y++)
            {
                var rowPointer = IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride);
                Marshal.Copy(rowPointer, bgraRow, 0, rowBytes);
                var targetRow = y * rowBytes;
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var sourceIndex = x * 4;
                    var targetIndex = targetRow + sourceIndex;
                    rgba[targetIndex] = bgraRow[sourceIndex + 2];
                    rgba[targetIndex + 1] = bgraRow[sourceIndex + 1];
                    rgba[targetIndex + 2] = bgraRow[sourceIndex];
                    rgba[targetIndex + 3] = bgraRow[sourceIndex + 3];
                    hasTransparency |= bgraRow[sourceIndex + 3] < byte.MaxValue;
                }
            }

            return new RgbaImage(bitmap.Width, bitmap.Height, rgba, hasTransparency);
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, sizeof(uint)));

    private static void WriteUInt32(Span<byte> data, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(offset, sizeof(uint)), value);

    private sealed record RgbaImage(int Width, int Height, byte[] Data, bool HasTransparency);
}
