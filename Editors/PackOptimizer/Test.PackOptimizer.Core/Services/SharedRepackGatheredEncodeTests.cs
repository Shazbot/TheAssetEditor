using System.Reflection;
using System.Runtime.InteropServices;
using Editors.ImportExport.Dds.Native;
using Editors.PackOptimizer.Core;

namespace Test.PackOptimizer.Core
{
    public class SharedRepackGatheredEncodeTests
    {
        private static MethodInfo Service(string name)
            => typeof(PackTextureAtlasBatchService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

        private static byte[] Canvas(int width, int height)
        {
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var offset = (y * width + x) * 4;
                    pixels[offset] = (byte)(x * 13 + y * 3);
                    pixels[offset + 1] = (byte)(y * 17 + x * 5);
                    pixels[offset + 2] = (byte)(x * 7 + y * 11);
                    pixels[offset + 3] = (byte)(255 - ((x * 9 + y * 5) & 63));
                }
            }

            return pixels;
        }

        private static DXGI_FORMAT CompressedFormat(string format, bool srgb)
            => format switch
            {
                "BC3" => srgb ? DXGI_FORMAT.BC3_UNORM_SRGB : DXGI_FORMAT.BC3_UNORM,
                "BC7" => srgb ? DXGI_FORMAT.BC7_UNORM_SRGB : DXGI_FORMAT.BC7_UNORM,
                _ => throw new ArgumentOutOfRangeException(nameof(format)),
            };

        private static byte[] EncodeWhole(
            byte[] bgra,
            int width,
            int height,
            string format,
            bool srgb,
            bool dx10,
            TEX_COMPRESS_FLAGS compressFlags = TEX_COMPRESS_FLAGS.DEFAULT)
        {
            var uncompressed = srgb ? DXGI_FORMAT.B8G8R8A8_UNORM_SRGB : DXGI_FORMAT.B8G8R8A8_UNORM;
            using var image = TexHelper.Instance.Initialize2D(uncompressed, width, height, 1, 1, CP_FLAGS.NONE);
            var frame = image.GetImage(0, 0, 0);
            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
                Marshal.Copy(bgra, y * rowBytes, IntPtr.Add(frame.Pixels, y * (int)frame.RowPitch), rowBytes);

            using var chain = image.GenerateMipMaps(TEX_FILTER_FLAGS.DEFAULT, 0);
            using var compressed = chain.Compress(CompressedFormat(format, srgb), compressFlags, 0.5f);
            using var stream = compressed.SaveToDDSMemory(dx10 ? DDS_FLAGS.FORCE_DX10_EXT : DDS_FLAGS.NONE);
            return stream.ToArray();
        }

        private static byte[] EncodeGathered(
            byte[] bgra,
            int width,
            int height,
            string format,
            bool srgb,
            bool dx10,
            SharedRepackBlockFate[][] fates)
        {
            var arguments = new object?[]
            {
                bgra, width, height, format, srgb, dx10, 1, fates.Length - 1, null, fates,
                CancellationToken.None, null, null,
            };
            Assert.That(
                (bool)Service("TryEncodeSharedRepackChainGathered").Invoke(null, arguments)!,
                Is.True,
                arguments[12] as string ?? "gathered encode failed");
            return (byte[])arguments[11]!;
        }

        private static SharedRepackBlockFate[][] Fates(int width, int height, bool copyMost = true)
        {
            var mipCount = 1 + (int)Math.Log2(Math.Max(width, height));
            var result = new SharedRepackBlockFate[mipCount][];
            for (var level = 0; level < mipCount; level++)
            {
                var levelWidth = Math.Max(1, width >> level);
                var levelHeight = Math.Max(1, height >> level);
                var blocksWide = SharedRepackBlockCopier.BlocksFor(levelWidth);
                var blocksHigh = SharedRepackBlockCopier.BlocksFor(levelHeight);
                result[level] = new SharedRepackBlockFate[blocksWide * blocksHigh];
                for (var block = 0; block < result[level].Length; block++)
                {
                    var copied = result[level].Length > 1 &&
                                 (copyMost ? (block + level) % 5 != 0 : (block + level) % 5 == 0);
                    result[level][block] = copied
                        ? SharedRepackBlockFate.Copied
                        : (block + level) % 2 == 0
                            ? SharedRepackBlockFate.Gutter
                            : SharedRepackBlockFate.Partial;
                }
            }

            return result;
        }

        private static bool[][] CopiedMask(SharedRepackBlockFate[][] fates)
            => fates.Select(level => level.Select(fate => fate == SharedRepackBlockFate.Copied).ToArray()).ToArray();

        private static int HeaderSize(bool dx10) => dx10 ? 148 : 128;

        private static int LevelOffset(int width, int height, int bytesPerBlock, int level, bool dx10)
        {
            var offset = HeaderSize(dx10);
            for (var index = 0; index < level; index++)
            {
                var levelWidth = Math.Max(1, width >> index);
                var levelHeight = Math.Max(1, height >> index);
                offset += SharedRepackBlockCopier.BlocksFor(levelWidth) *
                          SharedRepackBlockCopier.BlocksFor(levelHeight) * bytesPerBlock;
            }

            return offset;
        }

        private static byte[] DecodeWithOptimizer(byte[] dds)
        {
            var arguments = new object?[] { dds, null, 0, 0, null };
            Assert.That((bool)Service("TryDecodeDdsTopLevel").Invoke(null, arguments)!, Is.True, arguments[4] as string);
            return (byte[])arguments[1]!;
        }

        [TestCase("BC3", false)]
        [TestCase("BC7", false)]
        [TestCase("BC7", true)]
        public void GatheredBlocksMatchWholePageEncodeAndRoundTrip(string format, bool srgb)
        {
            const int width = 32;
            const int height = 32;
            const bool dx10 = true;
            var canvas = Canvas(width, height);
            var fates = Fates(width, height);
            var whole = EncodeWhole(canvas, width, height, format, srgb, dx10);
            var wholeQuick = format == "BC7"
                ? EncodeWhole(canvas, width, height, format, srgb, dx10, TEX_COMPRESS_FLAGS.BC7_QUICK)
                : whole;
            var gathered = EncodeGathered(canvas, width, height, format, srgb, dx10, fates);
            const int bytesPerBlock = 16;

            Assert.That(gathered.AsSpan(0, HeaderSize(dx10)).SequenceEqual(whole.AsSpan(0, HeaderSize(dx10))), Is.True);
            Assert.That(fates.SelectMany(level => level).Any(fate => fate == SharedRepackBlockFate.Gutter), Is.True);
            Assert.That(fates.SelectMany(level => level).Any(fate => fate == SharedRepackBlockFate.Partial), Is.True);
            for (var level = 0; level < fates.Length; level++)
            {
                var offset = LevelOffset(width, height, bytesPerBlock, level, dx10);
                for (var block = 0; block < fates[level].Length; block++)
                {
                    var actual = gathered.AsSpan(offset + block * bytesPerBlock, bytesPerBlock);
                    var expected = (fates[level][block] == SharedRepackBlockFate.Gutter ? wholeQuick : whole)
                        .AsSpan(offset + block * bytesPerBlock, bytesPerBlock);
                    if (fates[level][block] != SharedRepackBlockFate.Copied)
                        Assert.That(actual.SequenceEqual(expected), Is.True, $"{format} level {level} block {block}");
                    else if (format == "BC3")
                        expected.CopyTo(gathered.AsSpan(offset + block * bytesPerBlock, bytesPerBlock));
                }
            }

            if (format == "BC3")
                Assert.That(gathered, Is.EqualTo(whole), "filling the copied blocks must reconstruct the whole-page bytes");
            Assert.That(DecodeWithOptimizer(gathered), Has.Length.EqualTo(width * height * 4));
        }

        [Test]
        public void GeneratedBlocksUseTheVisibleQualityNotTheGutterOne()
        {
            const int width = 32;
            const int height = 32;
            var canvas = Canvas(width, height);
            var fates = Fates(width, height);
            for (var level = 0; level < fates.Length; level++)
            {
                for (var block = 0; block < fates[level].Length; block++)
                {
                    if (fates[level][block] == SharedRepackBlockFate.Gutter)
                        fates[level][block] = SharedRepackBlockFate.Generated;
                }
            }

            var whole = EncodeWhole(canvas, width, height, "BC7", false, true);
            var quick = EncodeWhole(canvas, width, height, "BC7", false, true, TEX_COMPRESS_FLAGS.BC7_QUICK);
            var gathered = EncodeGathered(canvas, width, height, "BC7", false, true, fates);
            var differsFromQuick = false;
            for (var level = 0; level < fates.Length; level++)
            {
                var offset = LevelOffset(width, height, 16, level, true);
                for (var block = 0; block < fates[level].Length; block++)
                {
                    if (fates[level][block] != SharedRepackBlockFate.Generated)
                        continue;
                    var start = offset + block * 16;
                    Assert.That(
                        gathered.AsSpan(start, 16).SequenceEqual(whole.AsSpan(start, 16)),
                        Is.True,
                        $"level {level} block {block}");
                    differsFromQuick |= !whole.AsSpan(start, 16).SequenceEqual(quick.AsSpan(start, 16));
                }
            }

            Assert.That(differsFromQuick, Is.True, "default and quick must differ for at least one block");
        }

        [Test]
        public void ChartBelowOneTexelMarksItsBlocksGeneratedNotGutter()
        {
            // Mirrors the plan: a chart that vanished at this level is visible content for the box filter.
            var fates = new SharedRepackBlockFate[4 * 4];
            SharedRepackBlockCopier.Mark(fates, 4, 4, 4, 8, 1, 1, SharedRepackBlockFate.Generated);
            Assert.That(fates[2 * 4 + 1], Is.EqualTo(SharedRepackBlockFate.Generated));
            Assert.That(fates.Count(fate => fate == SharedRepackBlockFate.Gutter), Is.EqualTo(15));
            // A stronger claim on the block outranks it, so Generated never hides real data.
            SharedRepackBlockCopier.Mark(fates, 4, 4, 4, 8, 4, 4, SharedRepackBlockFate.Partial);
            Assert.That(fates[2 * 4 + 1], Is.EqualTo(SharedRepackBlockFate.Partial));
        }

        [Test]
        public void GatheredChunksSpanMultipleMips()
        {
            const int width = 64;
            const int height = 128;
            var fates = Fates(width, height, copyMost: false);
            var copied = CopiedMask(fates);
            Assert.That(fates[0].Count(fate => fate != SharedRepackBlockFate.Copied), Is.GreaterThan(256));
            Assert.That(fates[1].Count(fate => fate != SharedRepackBlockFate.Copied), Is.GreaterThan(0));

            var canvas = Canvas(width, height);
            var whole = EncodeWhole(canvas, width, height, "BC3", false, true);
            var gathered = EncodeGathered(canvas, width, height, "BC3", false, true, fates);
            const int bytesPerBlock = 16;
            for (var level = 0; level < fates.Length; level++)
            {
                var offset = LevelOffset(width, height, bytesPerBlock, level, true);
                for (var block = 0; block < fates[level].Length; block++)
                {
                    var actual = gathered.AsSpan(offset + block * bytesPerBlock, bytesPerBlock);
                    var expected = whole.AsSpan(offset + block * bytesPerBlock, bytesPerBlock);
                    if (!copied[level][block])
                        Assert.That(actual.SequenceEqual(expected), Is.True, $"level {level} block {block}");
                    else
                        expected.CopyTo(gathered.AsSpan(offset + block * bytesPerBlock, bytesPerBlock));
                }
            }

            Assert.That(gathered, Is.EqualTo(whole));
            Assert.That(DecodeWithOptimizer(gathered), Has.Length.EqualTo(width * height * 4));
        }

    }
}
