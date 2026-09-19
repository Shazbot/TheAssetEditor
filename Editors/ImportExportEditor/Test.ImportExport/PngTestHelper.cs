using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Test.ImportExport;

internal readonly record struct ExactPngPixel(byte R, byte G, byte B, byte A);

internal static class PngTestHelper
{
    private const int BytesPerPixel = 4;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static ExactPngPixel ReadFirstPixelRgba(byte[] pngData)
    {
        var decoded = DecodeRgba(pngData);
        return new ExactPngPixel(
            decoded.Pixels[0],
            decoded.Pixels[1],
            decoded.Pixels[2],
            decoded.Pixels[3]);
    }

    public static byte ReadFirstFilterType(byte[] pngData)
        => DecodeRgba(pngData).FilterTypes[0];

    public static byte[] ReadAllPixelsRgba(byte[] pngData)
        => DecodeRgba(pngData).Pixels;

    public static byte[] ReadFilterTypes(byte[] pngData)
        => DecodeRgba(pngData).FilterTypes;

    private static DecodedPng DecodeRgba(byte[] pngData)
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

        var rowBytes = checked(width * BytesPerPixel);
        var expectedLength = checked((rowBytes + 1) * height);
        if (scanlines.Length != expectedLength)
            throw new InvalidDataException("Unexpected PNG scanline length.");

        var pixels = new byte[checked(rowBytes * height)];
        var filters = new byte[height];

        for (var row = 0; row < height; row++)
        {
            var scanlineOffset = checked(row * (rowBytes + 1));
            var pixelOffset = checked(row * rowBytes);
            var previousPixelOffset = checked((row - 1) * rowBytes);
            var filter = scanlines[scanlineOffset];
            filters[row] = filter;

            for (var index = 0; index < rowBytes; index++)
            {
                var filtered = scanlines[scanlineOffset + 1 + index];
                var left = index >= BytesPerPixel ? pixels[pixelOffset + index - BytesPerPixel] : 0;
                var above = row > 0 ? pixels[previousPixelOffset + index] : 0;
                var upperLeft = row > 0 && index >= BytesPerPixel
                    ? pixels[previousPixelOffset + index - BytesPerPixel]
                    : 0;

                var predictor = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => above,
                    3 => (left + above) >> 1,
                    4 => PaethPredictor(left, above, upperLeft),
                    _ => throw new NotSupportedException($"Unsupported PNG filter type {filter}.")
                };

                pixels[pixelOffset + index] = unchecked((byte)(filtered + predictor));
            }
        }

        return new DecodedPng(pixels, filters);
    }

    private static int PaethPredictor(int left, int above, int upperLeft)
    {
        var prediction = left + above - upperLeft;
        var distanceLeft = Math.Abs(prediction - left);
        var distanceAbove = Math.Abs(prediction - above);
        var distanceUpperLeft = Math.Abs(prediction - upperLeft);

        if (distanceLeft <= distanceAbove && distanceLeft <= distanceUpperLeft)
            return left;
        if (distanceAbove <= distanceUpperLeft)
            return above;
        return upperLeft;
    }

    private readonly record struct DecodedPng(byte[] Pixels, byte[] FilterTypes);
}
