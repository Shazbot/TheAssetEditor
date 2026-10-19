using System.Reflection;
using System.Runtime.InteropServices;
using DirectXTexNet;
using Editors.PackOptimizer.Core;

namespace Test.PackOptimizer.Core
{
    public class SharedRepackBlockCopierTests
    {
        private static readonly SharedRepackBlockFate Copied = SharedRepackBlockFate.Copied;

        // A level of blocks whose every byte is distinguishable: block (bx, by) is filled with a value derived from its position.
        private static byte[] MakeLevel(int blocksWide, int blocksHigh, int bytesPerBlock, int seed)
        {
            var data = new byte[blocksWide * blocksHigh * bytesPerBlock];
            for (var by = 0; by < blocksHigh; by++)
            {
                for (var bx = 0; bx < blocksWide; bx++)
                {
                    for (var i = 0; i < bytesPerBlock; i++)
                        data[(by * blocksWide + bx) * bytesPerBlock + i] = (byte)(seed + bx * 7 + by * 31 + i);
                }
            }

            return data;
        }

        [Test]
        public void AlignedCopy_IsBitExactAndLeavesTheRestAlone()
        {
            var source = MakeLevel(8, 8, 8, 1);
            var sheet = MakeLevel(16, 16, 8, 100);
            var before = (byte[])sheet.Clone();
            var fates = new SharedRepackBlockFate[16 * 16];
            var failures = 0;

            // A 16x12 texel crop (4x3 blocks) from source block (2,1) onto sheet block (5,6).
            var copied = SharedRepackBlockCopier.CopyRectangle(
                source, 8, 8, sheet, 16, 16, 8, 8, 4, 20, 24, 16, 12, fates, ref failures);

            Assert.That(copied, Is.EqualTo(12));
            Assert.That(failures, Is.EqualTo(0));
            for (var by = 0; by < 16; by++)
            {
                for (var bx = 0; bx < 16; bx++)
                {
                    var inside = bx is >= 5 and < 9 && by is >= 6 and < 9;
                    var sheetBlock = sheet.AsSpan((by * 16 + bx) * 8, 8);
                    if (inside)
                    {
                        var sourceBlock = source.AsSpan(((by - 6 + 1) * 8 + (bx - 5 + 2)) * 8, 8);
                        Assert.That(sheetBlock.SequenceEqual(sourceBlock), Is.True, $"block {bx},{by}");
                        Assert.That(fates[by * 16 + bx], Is.EqualTo(Copied));
                    }
                    else
                    {
                        Assert.That(sheetBlock.SequenceEqual(before.AsSpan((by * 16 + bx) * 8, 8)), Is.True, $"block {bx},{by} must be untouched");
                        Assert.That(fates[by * 16 + bx], Is.EqualTo(SharedRepackBlockFate.Gutter));
                    }
                }
            }
        }

        [TestCase(2, 0, 0, 0, TestName = "source x off the grid")]
        [TestCase(0, 3, 0, 0, TestName = "source y off the grid")]
        [TestCase(0, 0, 1, 0, TestName = "sheet x off the grid")]
        [TestCase(0, 0, 0, 6, TestName = "sheet y off the grid")]
        public void MisalignedCrop_FallsBackAndIsReported(int sourceX, int sourceY, int destinationX, int destinationY)
        {
            var source = MakeLevel(8, 8, 8, 1);
            var sheet = MakeLevel(8, 8, 8, 100);
            var before = (byte[])sheet.Clone();
            var fates = new SharedRepackBlockFate[64];
            var failures = 0;

            var copied = SharedRepackBlockCopier.CopyRectangle(
                source, 8, 8, sheet, 8, 8, 8, sourceX, sourceY, destinationX, destinationY, 8, 8, fates, ref failures);

            Assert.That(copied, Is.EqualTo(0));
            Assert.That(sheet, Is.EqualTo(before));
            Assert.That(fates.Any(fate => fate == SharedRepackBlockFate.Misaligned), Is.True);
            Assert.That(fates.Any(fate => fate == Copied), Is.False);
        }

        [Test]
        public void PartlyCoveredBlocksAreLeftToTheEncoder()
        {
            var source = MakeLevel(4, 4, 16, 1);
            var sheet = MakeLevel(4, 4, 16, 100);
            var fates = new SharedRepackBlockFate[16];
            var failures = 0;

            // 6x6 texels: only the first block is wholly inside.
            var copied = SharedRepackBlockCopier.CopyRectangle(source, 4, 4, sheet, 4, 4, 16, 0, 0, 0, 0, 6, 6, fates, ref failures);

            Assert.That(copied, Is.EqualTo(1));
            Assert.That(fates[0], Is.EqualTo(Copied));
            Assert.That(fates[1], Is.EqualTo(SharedRepackBlockFate.Partial));
            Assert.That(fates[4], Is.EqualTo(SharedRepackBlockFate.Partial));
            Assert.That(fates[5], Is.EqualTo(SharedRepackBlockFate.Partial));
            Assert.That(fates[2], Is.EqualTo(SharedRepackBlockFate.Gutter));
        }

        [Test]
        public void FormatMismatchFallsBack()
        {
            Assert.That(SharedRepackBlockCopier.AreBlocksCompatible("BC1", "BC1"), Is.True);
            Assert.That(SharedRepackBlockCopier.AreBlocksCompatible("bc3", "BC3"), Is.True);
            Assert.That(SharedRepackBlockCopier.AreBlocksCompatible("BC3", "BC1"), Is.False, "BC3 into a BC1 sheet");
            Assert.That(SharedRepackBlockCopier.AreBlocksCompatible("BC1", "BC3"), Is.False, "BC1 into a BC3 sheet");
            Assert.That(SharedRepackBlockCopier.AreBlocksCompatible("BC7", "BC1"), Is.False, "BC7 source");
            Assert.That(SharedRepackBlockCopier.GetFallbackReason(true, "BC3", 16, "BC1", 8, 12, 1 << 20, 1 << 10, 0, 0, 0, 0, 0),
                Is.EqualTo(SharedRepackBlockFate.FormatMismatch));
        }

        [Test]
        public void StoredValueRule_SrgbAndUnormOfTheSameFamilyAreInterchangeable()
        {
            // The repack decodes with Pfim and re-encodes the stored values with no colour-space conversion,
            // so only the block family matters; the sRGB flag is not a parameter of the decision.
            Assert.That(SharedRepackBlockCopier.GetFallbackReason(true, "BC1", 8, "BC1", 8, 12, 1 << 20, 1 << 10, 2, 64, 64, 128, 0),
                Is.Null);
        }

        [Test]
        public void LevelBeyondTheSourcesMipsFallsBack()
        {
            Assert.That(SharedRepackBlockCopier.GetFallbackReason(true, "BC1", 8, "BC1", 8, 3, 1 << 20, 1 << 10, 3, 0, 0, 0, 0),
                Is.EqualTo(SharedRepackBlockFate.BeyondSourceMips));
            Assert.That(SharedRepackBlockCopier.GetFallbackReason(true, "BC1", 8, "BC1", 8, 4, 1 << 20, 1 << 10, 3, 0, 0, 0, 0),
                Is.Null);
            Assert.That(SharedRepackBlockCopier.GetFallbackReason(true, "BC1", 8, "BC1", 8, 4, 100, 1 << 10, 0, 0, 0, 0, 0),
                Is.EqualTo(SharedRepackBlockFate.BeyondSourceMips), "a truncated source");
        }

        [Test]
        public void UnreadableSourceAndOriginsThatAreNotWholeTexelsAtTheLevelFallBack()
        {
            Assert.That(SharedRepackBlockCopier.GetFallbackReason(false, "", 0, "BC1", 8, 0, 0, 0, 0, 0, 0, 0, 0),
                Is.EqualTo(SharedRepackBlockFate.SourceUnavailable));
            // Level 2: origins must be multiples of 4 texels at level 0.
            Assert.That(SharedRepackBlockCopier.GetFallbackReason(true, "BC1", 8, "BC1", 8, 12, 1 << 20, 1 << 10, 2, 6, 0, 0, 0),
                Is.EqualTo(SharedRepackBlockFate.Misaligned));
        }

        // ---- decoded equality, with real encodes ---------------------------------------------------

        private static byte[] EncodeDds(int width, int height, DXGI_FORMAT target, Func<int, int, (byte B, byte G, byte R, byte A)> pixel, bool dx10)
        {
            using var image = TexHelper.Instance.Initialize2D(DXGI_FORMAT.B8G8R8A8_UNORM, width, height, 1, 1, CP_FLAGS.NONE);
            var frame = image.GetImage(0, 0, 0);
            var pitch = (int)frame.RowPitch;
            var row = new byte[width * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var (b, g, r, a) = pixel(x, y);
                    row[x * 4] = b;
                    row[x * 4 + 1] = g;
                    row[x * 4 + 2] = r;
                    row[x * 4 + 3] = a;
                }

                Marshal.Copy(row, 0, IntPtr.Add(frame.Pixels, y * pitch), row.Length);
            }

            using var compressed = image.Compress(target, TEX_COMPRESS_FLAGS.DEFAULT, 0.5f);
            using var stream = compressed.SaveToDDSMemory(dx10 ? DDS_FLAGS.FORCE_DX10_EXT : DDS_FLAGS.NONE);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes, 0, bytes.Length);
            return bytes;
        }

        private static byte[] Decode(byte[] dds, DXGI_FORMAT decoded)
        {
            var handle = GCHandle.Alloc(dds, GCHandleType.Pinned);
            try
            {
                using var image = TexHelper.Instance.LoadFromDDSMemory(handle.AddrOfPinnedObject(), dds.Length, DDS_FLAGS.NONE);
                using var uncompressed = image.Decompress(decoded);
                var frame = uncompressed.GetImage(0, 0, 0);
                var result = new byte[frame.Width * frame.Height * 4];
                for (var y = 0; y < frame.Height; y++)
                    Marshal.Copy(IntPtr.Add(frame.Pixels, y * (int)frame.RowPitch), result, y * frame.Width * 4, frame.Width * 4);
                return result;
            }
            finally
            {
                handle.Free();
            }
        }

        // Reads the level geometry the way the optimizer does: through its own layout parser and offset helper.
        private static (int HeaderSize, int BytesPerBlock) ReadLayout(byte[] dds)
        {
            var service = typeof(PackTextureAtlasBatchService);
            var read = service.GetMethod("TryReadDdsLayout", BindingFlags.NonPublic | BindingFlags.Static)!;
            var arguments = new object?[] { dds.AsSpan(0, Math.Min(dds.Length, 256)).ToArray(), null };
            Assert.That((bool)read.Invoke(null, arguments)!, Is.True);
            var layout = arguments[1]!;
            var offset = (long)service.GetMethod("GetSharedRepackLevelOffset", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [layout, 0])!;
            var bytesPerBlock = (int)layout.GetType().GetProperty("BytesPerBlock")!.GetValue(layout)!;
            return ((int)offset, bytesPerBlock);
        }

        [TestCase(false, false, TestName = "legacy source into legacy sheet")]
        [TestCase(true, false, TestName = "DX10 source into legacy sheet")]
        [TestCase(false, true, TestName = "legacy source into DX10 sheet")]
        [TestCase(true, true, TestName = "DX10 source into DX10 sheet")]
        public void Bc1PunchThroughBlocksSurviveAndHeaderKindsOnlyMoveTheOffsets(bool sourceDx10, bool sheetDx10)
        {
            // Source: 32x32, left half opaque colour gradient, right half fully transparent (punch-through).
            var source = EncodeDds(32, 32, DXGI_FORMAT.BC1_UNORM, (x, y) => x < 16 ? ((byte)(x * 8), (byte)(y * 8), (byte)(255 - x * 4), (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0), sourceDx10);
            var sheet = EncodeDds(64, 64, DXGI_FORMAT.BC1_UNORM, (x, y) => ((byte)128, (byte)128, (byte)128, (byte)255), sheetDx10);
            var (sourceHeader, bytesPerBlock) = ReadLayout(source);
            var (sheetHeader, _) = ReadLayout(sheet);
            Assert.That(sourceHeader, Is.EqualTo(sourceDx10 ? 148 : 128));
            Assert.That(sheetHeader, Is.EqualTo(sheetDx10 ? 148 : 128));

            var failures = 0;
            var fates = new SharedRepackBlockFate[16 * 16];
            var copied = SharedRepackBlockCopier.CopyRectangle(
                source.AsSpan(sourceHeader, 8 * 8 * bytesPerBlock), 8, 8,
                sheet.AsSpan(sheetHeader, 16 * 16 * bytesPerBlock), 16, 16, bytesPerBlock,
                0, 0, 16, 32, 32, 32, fates, ref failures);
            Assert.That(copied, Is.EqualTo(64));

            var decodedSource = Decode(source, DXGI_FORMAT.B8G8R8A8_UNORM);
            var decodedSheet = Decode(sheet, DXGI_FORMAT.B8G8R8A8_UNORM);
            var transparentSeen = 0;
            for (var y = 0; y < 32; y++)
            {
                for (var x = 0; x < 32; x++)
                {
                    var from = (y * 32 + x) * 4;
                    var to = ((y + 32) * 64 + x + 16) * 4;
                    for (var c = 0; c < 4; c++)
                        Assert.That(decodedSheet[to + c], Is.EqualTo(decodedSource[from + c]), $"texel {x},{y} channel {c}");
                    if (decodedSource[from + 3] == 0)
                        transparentSeen++;
                }
            }

            Assert.That(transparentSeen, Is.EqualTo(32 * 16), "the punch-through texels must stay transparent");
            // Outside the crop the sheet is untouched.
            Assert.That(decodedSheet[0], Is.EqualTo(128).Within(8));
        }

        [Test]
        public void Bc3AlphaIsCopiedWithTheColourBlock()
        {
            var source = EncodeDds(16, 16, DXGI_FORMAT.BC3_UNORM, (x, y) => ((byte)(x * 16), (byte)(y * 16), (byte)40, (byte)(x * 16 + y)), false);
            var sheet = EncodeDds(32, 16, DXGI_FORMAT.BC3_UNORM, (x, y) => ((byte)10, (byte)10, (byte)10, (byte)255), true);
            var (sourceHeader, bytesPerBlock) = ReadLayout(source);
            var (sheetHeader, _) = ReadLayout(sheet);
            Assert.That(bytesPerBlock, Is.EqualTo(16));

            var failures = 0;
            var fates = new SharedRepackBlockFate[8 * 4];
            var copied = SharedRepackBlockCopier.CopyRectangle(
                source.AsSpan(sourceHeader, 4 * 4 * bytesPerBlock), 4, 4,
                sheet.AsSpan(sheetHeader, 8 * 4 * bytesPerBlock), 8, 4, bytesPerBlock,
                0, 0, 16, 0, 16, 16, fates, ref failures);
            Assert.That(copied, Is.EqualTo(16));

            var decodedSource = Decode(source, DXGI_FORMAT.B8G8R8A8_UNORM);
            var decodedSheet = Decode(sheet, DXGI_FORMAT.B8G8R8A8_UNORM);
            for (var y = 0; y < 16; y++)
            {
                for (var x = 0; x < 16; x++)
                {
                    for (var c = 0; c < 4; c++)
                        Assert.That(decodedSheet[(y * 32 + x + 16) * 4 + c], Is.EqualTo(decodedSource[(y * 16 + x) * 4 + c]), $"texel {x},{y} channel {c}");
                }
            }
        }

        [Test]
        public void CopyIsDeterministic()
        {
            byte[] Run()
            {
                var source = MakeLevel(8, 8, 16, 3);
                var sheet = MakeLevel(16, 16, 16, 9);
                var fates = new SharedRepackBlockFate[256];
                var failures = 0;
                SharedRepackBlockCopier.CopyRectangle(source, 8, 8, sheet, 16, 16, 16, 4, 8, 12, 16, 20, 16, fates, ref failures);
                SharedRepackBlockCopier.CopyRectangle(source, 8, 8, sheet, 16, 16, 16, 0, 0, 0, 0, 32, 32, fates, ref failures);
                return sheet;
            }

            Assert.That(Run(), Is.EqualTo(Run()));
        }
    }
}
