using System.Buffers.Binary;

namespace MeshImportExport
{
    /// <summary>Result of <see cref="DdsPassthrough.TryInspect"/>.</summary>
    public readonly record struct DdsPassthroughInfo(
        string FormatName,
        int Width,
        int Height,
        int MipCount,
        int HeaderLength);

    /// <summary>
    /// Minimal DDS header reader used to decide whether a pack texture can be
    /// written beside a preview unchanged (block-compressed 2D texture) instead
    /// of being decoded to RGBA. Also builds the SharpGLTF sidecar stub for such
    /// files: the DDS header plus a marker and the sidecar path.
    /// </summary>
    public static class DdsPassthrough
    {
        private const uint DdsMagic = 0x20534444;
        private const uint DdpfFourCc = 0x4;
        private const uint Caps2Cubemap = 0x200;
        private const uint Caps2Volume = 0x200000;

        private static uint FourCc(string s) => (uint)(s[0] | s[1] << 8 | s[2] << 16 | s[3] << 24);

        private static string? LegacyName(uint fourCc)
        {
            if (fourCc == FourCc("DXT1")) return "DXT1";
            if (fourCc == FourCc("DXT3")) return "DXT3";
            if (fourCc == FourCc("DXT5")) return "DXT5";
            if (fourCc == FourCc("ATI1") || fourCc == FourCc("BC4U")) return "BC4";
            if (fourCc == FourCc("ATI2") || fourCc == FourCc("BC5U")) return "BC5";
            return null;
        }

        private static string? DxgiName(uint dxgi) => dxgi switch
        {
            70 or 71 => "BC1_UNORM",
            72 => "BC1_UNORM_SRGB",
            73 or 74 => "BC2_UNORM",
            75 => "BC2_UNORM_SRGB",
            76 or 77 => "BC3_UNORM",
            78 => "BC3_UNORM_SRGB",
            79 or 80 => "BC4_UNORM",
            81 => "BC4_SNORM",
            82 or 83 => "BC5_UNORM",
            84 => "BC5_SNORM",
            97 or 98 => "BC7_UNORM",
            99 => "BC7_UNORM_SRGB",
            _ => null
        };

        /// <summary>
        /// True when the data is a 2D, non-array, non-cube BC1/2/3/4/5/7 DDS whose
        /// width and height are multiples of four.
        /// </summary>
        public static bool TryInspect(ReadOnlySpan<byte> data, out DdsPassthroughInfo info)
        {
            info = default;
            if (data.Length < 128
                || BinaryPrimitives.ReadUInt32LittleEndian(data) != DdsMagic
                || BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 124
                || BinaryPrimitives.ReadUInt32LittleEndian(data[76..]) != 32)
                return false;

            var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
            var height = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
            var width = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
            var mips = (flags & 0x20000) != 0 ? BinaryPrimitives.ReadUInt32LittleEndian(data[28..]) : 1;
            if (width == 0 || height == 0 || width > 16384 || height > 16384 || width % 4 != 0 || height % 4 != 0)
                return false;
            if (mips == 0) mips = 1;

            var caps2 = BinaryPrimitives.ReadUInt32LittleEndian(data[112..]);
            if ((caps2 & (Caps2Cubemap | Caps2Volume)) != 0)
                return false;

            var pfFlags = BinaryPrimitives.ReadUInt32LittleEndian(data[80..]);
            if ((pfFlags & DdpfFourCc) == 0)
                return false;
            var fourCc = BinaryPrimitives.ReadUInt32LittleEndian(data[84..]);

            string? name;
            var headerLength = 128;
            if (fourCc == FourCc("DX10"))
            {
                if (data.Length < 148)
                    return false;
                var dxgi = BinaryPrimitives.ReadUInt32LittleEndian(data[128..]);
                var dimension = BinaryPrimitives.ReadUInt32LittleEndian(data[132..]);
                var misc = BinaryPrimitives.ReadUInt32LittleEndian(data[136..]);
                var arraySize = BinaryPrimitives.ReadUInt32LittleEndian(data[140..]);
                if (dimension != 3 || arraySize != 1 || (misc & 0x4) != 0)
                    return false;
                name = DxgiName(dxgi);
                headerLength = 148;
            }
            else
            {
                name = LegacyName(fourCc);
            }

            if (name == null)
                return false;
            info = new DdsPassthroughInfo(name, (int)width, (int)height, (int)mips, headerLength);
            return true;
        }

        private static readonly byte[] StubMarker = "WH3SIDECAR:"u8.ToArray();

        /// <summary>
        /// Header-only DDS followed by the marker and sidecar path. SharpGLTF
        /// recognizes the DDS magic and hashes only a few hundred bytes; the path
        /// keeps same-sized textures distinct and lets the GLB writer find the file.
        /// </summary>
        public static byte[] ToSidecarStub(ReadOnlySpan<byte> ddsBytes, int headerLength, string sidecarPath)
        {
            var pathBytes = System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(sidecarPath));
            var stub = new byte[headerLength + StubMarker.Length + pathBytes.Length];
            ddsBytes[..headerLength].CopyTo(stub);
            StubMarker.CopyTo(stub.AsSpan(headerLength));
            pathBytes.CopyTo(stub.AsSpan(headerLength + StubMarker.Length));
            return stub;
        }

        /// <summary>True for a <see cref="ToSidecarStub"/> result.</summary>
        public static bool IsSidecarStub(ReadOnlySpan<byte> data)
            => TryGetStubSidecarPath(data, out _);

        public static bool TryGetStubSidecarPath(ReadOnlySpan<byte> data, out string sidecarPath)
        {
            sidecarPath = string.Empty;
            if (data.Length < 128 + StubMarker.Length + 1
                || BinaryPrimitives.ReadUInt32LittleEndian(data) != DdsMagic)
                return false;
            foreach (var headerLength in new[] { 128, 148 })
            {
                if (data.Length > headerLength + StubMarker.Length
                    && data[headerLength..].StartsWith(StubMarker))
                {
                    sidecarPath = System.Text.Encoding.UTF8.GetString(data[(headerLength + StubMarker.Length)..]);
                    return true;
                }
            }
            return false;
        }
    }
}
