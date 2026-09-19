using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Test.ImportExport;

internal readonly record struct ExactPngPixel(byte R, byte G, byte B, byte A);

internal static class PngTestHelper
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static ExactPngPixel ReadFirstPixelRgba(byte[] pngData)
    {
        ArgumentNullException.ThrowIfNull(pngData);
        if (pngData.Length < PngSignature.Length ||
            !pngData.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
            throw new InvalidDataException("Invalid PNG signature.");

        var offset = PngSignature.Length;
        var width = 0;
        var height = 0;
        using var compressed = new MemoryStream();

        while (offset + 12 <= pngData.Length)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(pngData.AsSpan(offset, 4)));
            offset += 4;

            var type = Encoding.ASCII.GetString(pngData, offset, 4);
            offset += 4;
            if (length < 0 || offset + length + 4 > pngData.Length)
                throw new InvalidDataException("Invalid PNG chunk length.");

            var data = pngData.AsSpan(offset, length);
            offset += length;
            offset += 4; // CRC

            if (type == "IHDR")
            {
                if (data.Length != 13)
                    throw new InvalidDataException("Invalid PNG IHDR.");
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[..4]));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)));
                if (data[8] != 8 || data[9] != 6 || data[12] != 0)
                    throw new NotSupportedException("Test helper expects non-interlaced 8-bit RGBA PNGs.");
            }
            else if (type == "IDAT")
            {
                compressed.Write(data);
            }
            else if (type == "IEND")
            {
                break;
            }
        }

        if (width <= 0 || height <= 0)
            throw new InvalidDataException("PNG is missing a valid IHDR.");

        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var scanlines = raw.ToArray();

        var rowBytes = checked(width * 4);
        var expectedLength = checked((rowBytes + 1) * height);
        if (scanlines.Length != expectedLength)
            throw new InvalidDataException("Unexpected PNG scanline length.");
        if (scanlines[0] != 0)
            throw new NotSupportedException("Test helper expects PNG filter type 0.");

        return new ExactPngPixel(
            scanlines[1],
            scanlines[2],
            scanlines[3],
            scanlines[4]);
    }
}
