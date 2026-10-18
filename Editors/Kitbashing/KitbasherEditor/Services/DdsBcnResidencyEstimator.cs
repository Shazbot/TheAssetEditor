using System.Buffers.Binary;

namespace Editors.KitbasherEditor.Services
{
    public sealed record DdsBcnResidencyEstimate(
        string Format,
        int Width,
        int Height,
        int MipCount,
        long Bytes);

    public static class DdsBcnResidencyEstimator
    {
        private const uint DdsMagic = 0x20534444; // "DDS "
        private const uint Dx10FourCc = 0x30315844; // "DX10"

        public static bool TryEstimate(ReadOnlySpan<byte> header, out DdsBcnResidencyEstimate estimate)
        {
            estimate = default!;

            if (header.Length < 128 ||
                BinaryPrimitives.ReadUInt32LittleEndian(header[..4]) != DdsMagic ||
                BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4)) != 124)
            {
                return false;
            }

            var height = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12, 4));
            var width = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(16, 4));
            var mipCount = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(28, 4));
            if (width <= 0 || height <= 0)
                return false;

            mipCount = Math.Max(1, mipCount);
            if (mipCount > 32)
                return false;

            var fourCc = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(84, 4));
            if (fourCc != Dx10FourCc)
            {
                var caps2 = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(112, 4));
                const uint legacyCubeMapOrVolumeMask = 0x00000200 | 0x00200000;
                if ((caps2 & legacyCubeMapOrVolumeMask) != 0)
                    return false;
            }

            if (!TryGetBlockFormat(header, fourCc, out var format, out var bytesPerBlock))
                return false;

            long totalBytes = 0;
            var mipWidth = width;
            var mipHeight = height;
            for (var mip = 0; mip < mipCount; mip++)
            {
                var blocksWide = Math.Max(1L, (mipWidth + 3L) / 4L);
                var blocksHigh = Math.Max(1L, (mipHeight + 3L) / 4L);
                totalBytes = checked(totalBytes + checked(blocksWide * blocksHigh * bytesPerBlock));
                mipWidth = Math.Max(1, mipWidth / 2);
                mipHeight = Math.Max(1, mipHeight / 2);
            }

            estimate = new DdsBcnResidencyEstimate(
                format,
                width,
                height,
                mipCount,
                totalBytes);
            return true;
        }

        private static bool TryGetBlockFormat(
            ReadOnlySpan<byte> header,
            uint fourCc,
            out string format,
            out int bytesPerBlock)
        {
            if (fourCc == Dx10FourCc)
            {
                if (header.Length < 148)
                {
                    format = string.Empty;
                    bytesPerBlock = 0;
                    return false;
                }

                // Model material textures are expected to be ordinary 2D textures.
                // Reject arrays/cubemaps instead of understating their residency.
                var resourceDimension = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(132, 4));
                var miscFlag = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(136, 4));
                var arraySize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(140, 4));
                if (resourceDimension != 3 || arraySize != 1 || (miscFlag & 0x4) != 0)
                {
                    format = string.Empty;
                    bytesPerBlock = 0;
                    return false;
                }

                var dxgiFormat = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(128, 4));
                return TryGetDxgiBlockFormat(dxgiFormat, out format, out bytesPerBlock);
            }

            return TryGetLegacyBlockFormat(fourCc, out format, out bytesPerBlock);
        }

        private static bool TryGetLegacyBlockFormat(
            uint fourCc,
            out string format,
            out int bytesPerBlock)
        {
            switch (fourCc)
            {
                case 0x31545844: // DXT1
                    format = "BC1";
                    bytesPerBlock = 8;
                    return true;
                case 0x33545844: // DXT3
                    format = "BC2";
                    bytesPerBlock = 16;
                    return true;
                case 0x35545844: // DXT5
                    format = "BC3";
                    bytesPerBlock = 16;
                    return true;
                case 0x31495441: // ATI1
                case 0x55344342: // BC4U
                case 0x53344342: // BC4S
                    format = "BC4";
                    bytesPerBlock = 8;
                    return true;
                case 0x32495441: // ATI2
                case 0x55354342: // BC5U
                case 0x53354342: // BC5S
                    format = "BC5";
                    bytesPerBlock = 16;
                    return true;
                default:
                    format = string.Empty;
                    bytesPerBlock = 0;
                    return false;
            }
        }

        private static bool TryGetDxgiBlockFormat(
            uint dxgiFormat,
            out string format,
            out int bytesPerBlock)
        {
            if (dxgiFormat is >= 70 and <= 72)
            {
                format = "BC1";
                bytesPerBlock = 8;
                return true;
            }

            if (dxgiFormat is >= 73 and <= 75)
            {
                format = "BC2";
                bytesPerBlock = 16;
                return true;
            }

            if (dxgiFormat is >= 76 and <= 78)
            {
                format = "BC3";
                bytesPerBlock = 16;
                return true;
            }

            if (dxgiFormat is >= 79 and <= 81)
            {
                format = "BC4";
                bytesPerBlock = 8;
                return true;
            }

            if (dxgiFormat is >= 82 and <= 84)
            {
                format = "BC5";
                bytesPerBlock = 16;
                return true;
            }

            if (dxgiFormat is >= 94 and <= 96)
            {
                format = "BC6";
                bytesPerBlock = 16;
                return true;
            }

            if (dxgiFormat is >= 97 and <= 99)
            {
                format = "BC7";
                bytesPerBlock = 16;
                return true;
            }

            format = string.Empty;
            bytesPerBlock = 0;
            return false;
        }
    }
}
