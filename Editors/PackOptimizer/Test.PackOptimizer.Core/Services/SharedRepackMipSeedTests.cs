using System.Reflection;
using Editors.PackOptimizer.Core;

namespace Test.PackOptimizer.Core
{
    public class SharedRepackMipSeedTests
    {
        private static byte[] Solid(int width, int height, byte b, byte g, byte r)
        {
            var data = new byte[width * height * 4];
            for (var i = 0; i < width * height; i++)
            {
                data[i * 4] = b;
                data[i * 4 + 1] = g;
                data[i * 4 + 2] = r;
                data[i * 4 + 3] = 255;
            }

            return data;
        }

        // Every texel distinguishable.
        private static byte[] Pattern(int width, int height, int seed)
        {
            var data = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var o = (y * width + x) * 4;
                    data[o] = (byte)(x * 7 + seed);
                    data[o + 1] = (byte)(y * 11 + seed);
                    data[o + 2] = (byte)(x * 3 + y * 5 + seed);
                    data[o + 3] = 255;
                }
            }

            return data;
        }

        private static SharedRepackMipSeed Seed(
            int level, int sx, int sy, int dx, int dy, int w, int h,
            (byte[] Bgra, int Width, int Height)? source, byte[] pixels, int levelWidth, int levelHeight,
            int shiftX = 0, int shiftY = 0)
            => SharedRepackMipSeeder.Seed(level, shiftX, shiftY, sx, sy, dx, dy, w, h, source, pixels, levelWidth, levelHeight, out _);

        [Test]
        public void ARectangleGetsExactlyTheSourcesMipTexels_GutterIncluded()
        {
            // Sheet level 2 is 32x32 (128x128 sheet). The rectangle is 64x48 level-0 texels at sheet (32,64),
            // reading the source from (16,8); the source's level 2 is 16x16 with its own, unrelated content.
            var sourceLevel = Pattern(16, 16, 40);
            var pixels = Solid(32, 32, 9, 9, 9);
            var seed = Seed(2, 16, 8, 32, 64, 64, 48, (sourceLevel, 16, 16), pixels, 32, 32);

            Assert.That(seed, Is.EqualTo(SharedRepackMipSeed.SourceMip));
            for (var y = 0; y < 32; y++)
            {
                for (var x = 0; x < 32; x++)
                {
                    var inside = x is >= 8 and < 24 && y is >= 16 and < 28;
                    var o = (y * 32 + x) * 4;
                    if (inside)
                    {
                        var s = (Math.Min(2 + (y - 16), 15) * 16 + Math.Min(4 + (x - 8), 15)) * 4;
                        Assert.That(pixels.AsSpan(o, 4).SequenceEqual(sourceLevel.AsSpan(s, 4)), Is.True, $"texel {x},{y}");
                    }
                    else
                    {
                        Assert.That(pixels[o], Is.EqualTo(9), $"texel {x},{y} outside the rectangle must stay");
                    }
                }
            }
        }

        [Test]
        public void GutterTexelsComeFromTheSourceNeighbourhoodNotTheNeighbouringPiece()
        {
            // Two pieces side by side on the sheet; their sources differ strongly (red / blue). The
            // box-filtered canvas (stand-in: a gradient) mixes them across the seam; the seeded level must not.
            var red = Solid(16, 16, 0, 0, 255);
            var blue = Solid(16, 16, 255, 0, 0);
            var pixels = new byte[32 * 32 * 4];
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = (byte)(i % 251);

            // Level 2: piece A at sheet x 0..63 (level-0), piece B at 64..127, both 128 high.
            Assert.That(Seed(2, 0, 0, 0, 0, 64, 128, (red, 16, 16), pixels, 32, 32), Is.EqualTo(SharedRepackMipSeed.SourceMip));
            Assert.That(Seed(2, 0, 0, 64, 0, 64, 128, (blue, 16, 16), pixels, 32, 32), Is.EqualTo(SharedRepackMipSeed.SourceMip));
            for (var y = 0; y < 32; y++)
            {
                for (var x = 0; x < 32; x++)
                {
                    var o = (y * 32 + x) * 4;
                    var expected = x < 16 ? new byte[] { 0, 0, 255, 255 } : new byte[] { 255, 0, 0, 255 };
                    Assert.That(pixels.AsSpan(o, 4).SequenceEqual(expected), Is.True, $"texel {x},{y}");
                }
            }
        }

        [Test]
        public void ReadingPastTheSourceLevelRepeatsItsEdgeTexels()
        {
            var sourceLevel = Pattern(4, 4, 3);
            var pixels = new byte[8 * 8 * 4];
            Assert.That(Seed(1, 0, 0, 0, 0, 16, 16, (sourceLevel, 4, 4), pixels, 8, 8), Is.EqualTo(SharedRepackMipSeed.SourceMip));
            for (var y = 0; y < 8; y++)
            {
                for (var x = 0; x < 8; x++)
                {
                    var s = (Math.Min(y, 3) * 4 + Math.Min(x, 3)) * 4;
                    Assert.That(pixels.AsSpan((y * 8 + x) * 4, 4).SequenceEqual(sourceLevel.AsSpan(s, 4)), Is.True, $"texel {x},{y}");
                }
            }
        }

        [TestCase(2, 0, 0, 32, 32, TestName = "source x off the level's texel grid")]
        [TestCase(0, 0, 0, 34, 32, TestName = "sheet x off the level's texel grid")]
        [TestCase(0, 6, 0, 32, 32, TestName = "source y off the level's texel grid")]
        public void NonIntegerPositionsFallBackAndTouchNothing(int sx, int sy, int dx, int dy, int unused)
        {
            _ = unused;
            var pixels = Solid(32, 32, 7, 7, 7);
            var before = (byte[])pixels.Clone();
            var seed = Seed(2, sx, sy, dx, dy, 64, 64, (Pattern(16, 16, 0), 16, 16), pixels, 32, 32);

            Assert.That(seed, Is.EqualTo(SharedRepackMipSeed.NonIntegerPosition));
            Assert.That(pixels, Is.EqualTo(before));
        }

        [Test]
        public void ShortChainSmallRectangleAndExponentMismatchFallBack()
        {
            var pixels = Solid(32, 32, 7, 7, 7);
            var before = (byte[])pixels.Clone();

            Assert.That(Seed(2, 0, 0, 0, 0, 64, 64, null, pixels, 32, 32), Is.EqualTo(SharedRepackMipSeed.BeyondSourceMips));
            Assert.That(Seed(5, 0, 0, 0, 0, 16, 16, (Pattern(4, 4, 0), 4, 4), pixels, 32, 32), Is.EqualTo(SharedRepackMipSeed.NoWholeTexel));
            Assert.That(Seed(2, 0, 0, 0, 0, 64, 64, (Pattern(16, 16, 0), 16, 16), pixels, 32, 32, shiftX: 1), Is.EqualTo(SharedRepackMipSeed.ExponentDiffers));
            Assert.That(pixels, Is.EqualTo(before));
        }

        [Test]
        public void ARectangleThatIsNotAWholeNumberOfTexelsSeedsTheWholeOnesAndReportsTheEdge()
        {
            var pixels = Solid(8, 8, 7, 7, 7);
            var seed = SharedRepackMipSeeder.Seed(2, 0, 0, 0, 0, 0, 0, 10, 8, (Pattern(4, 4, 3), 4, 4), pixels, 8, 8, out var partial);
            Assert.That(seed, Is.EqualTo(SharedRepackMipSeed.SourceMip));
            Assert.That(partial, Is.True);
            Assert.That(pixels[(0 * 8 + 1) * 4], Is.Not.EqualTo(7)); // x 0..1 seeded (10 >> 2 = 2)
            Assert.That(pixels[(0 * 8 + 2) * 4], Is.EqualTo(7));     // the partial third texel is left to the box filter
        }

        [Test]
        public void TheDifferenceHistogramCountsWhatTheBoxFilterGotWrong()
        {
            var source = Solid(2, 2, 100, 100, 100);
            var pixels = Solid(2, 2, 100, 100, 100);
            pixels[0] = 90; // one texel 10 off, one 40 off
            pixels[4 + 1] = 140;
            var histogram = new long[256];
            SharedRepackMipSeeder.Seed(1, 0, 0, 0, 0, 0, 0, 4, 4, (source, 2, 2), pixels, 2, 2, out _, histogram);

            Assert.That(histogram[0], Is.EqualTo(2));
            Assert.That(histogram[10], Is.EqualTo(1));
            Assert.That(histogram[40], Is.EqualTo(1));
            var stats = new SharedRepackMipSeedStats();
            stats.Differences[1] = histogram;
            var (texels, mean, _, max, over) = stats.Summarize(1, 3, 16);
            Assert.That((texels, max), Is.EqualTo((4L, 40)));
            Assert.That(mean, Is.EqualTo(12.5).Within(1e-9));
            Assert.That(over, Is.EqualTo(0.25).Within(1e-9));
        }

        private static MethodInfo Service(string name)
            => typeof(PackTextureAtlasBatchService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

        private static byte[] EncodeChain(byte[] canvas, int size, int firstLevel, int lastLevel, Action<int, byte[], int, int>? overlay)
        {
            var arguments = new object?[] { canvas, size, size, "BC1", false, true, firstLevel, lastLevel, overlay, null, null };
            Assert.That((bool)Service("TryEncodeSharedRepackChain").Invoke(null, arguments)!, Is.True, arguments[10] as string);
            return (byte[])arguments[9]!;
        }

        private static byte[] EncodeSingle(byte[] bgra, int size)
        {
            var arguments = new object?[] { bgra, size, size, "BC1", false, true, null, null };
            Assert.That((bool)Service("TryEncodeSharedRepackDds").Invoke(null, arguments)!, Is.True);
            return (byte[])arguments[6]!;
        }

        private static (int Offset, int BytesPerBlock) LevelOffset(byte[] dds, int level)
        {
            var read = Service("TryReadDdsLayout");
            var arguments = new object?[] { dds.AsSpan(0, Math.Min(dds.Length, 256)).ToArray(), null };
            Assert.That((bool)read.Invoke(null, arguments)!, Is.True);
            var layout = arguments[1]!;
            var offset = (long)Service("GetSharedRepackLevelOffset").Invoke(null, [layout, level])!;
            return ((int)offset, (int)layout.GetType().GetProperty("BytesPerBlock")!.GetValue(layout)!);
        }

        [Test]
        public void SeededBlocksEncodeLikeTheSourceCropAndTheChainIsDeterministic()
        {
            // 128x128 sheet, a gradient canvas. At level 2 (32x32) the rectangle's 16x16 texel crop of the
            // source's level 2 is placed at texel (8,8); its blocks must equal the encoding of that crop alone.
            var canvas = new byte[128 * 128 * 4];
            for (var i = 0; i < canvas.Length; i++)
                canvas[i] = (byte)(i * 13 % 256);

            var sourceLevel = Pattern(32, 32, 77);
            var crop = new byte[16 * 16 * 4];
            for (var y = 0; y < 16; y++)
                Buffer.BlockCopy(sourceLevel, ((4 + y) * 32 + 4) * 4, crop, y * 16 * 4, 16 * 4);

            Action<int, byte[], int, int> overlay = (level, pixels, width, height) =>
            {
                if (level == 2)
                {
                    // sheet (32,32) level-0, source (16,16), 64x64 level-0 texels
                    var seed = Seed(2, 16, 16, 32, 32, 64, 64, (sourceLevel, 32, 32), pixels, width, height);
                    Assert.That(seed, Is.EqualTo(SharedRepackMipSeed.SourceMip));
                }
            };

            var first = EncodeChain(canvas, 128, 1, 6, overlay);
            var second = EncodeChain(canvas, 128, 1, 6, overlay);
            Assert.That(second, Is.EqualTo(first), "the chain must be deterministic");

            var (sheetOffset, bytesPerBlock) = LevelOffset(first, 2);
            var single = EncodeSingle(crop, 16);
            var (cropOffset, _) = LevelOffset(single, 0);
            for (var by = 0; by < 4; by++)
            {
                for (var bx = 0; bx < 4; bx++)
                {
                    var sheetBlock = first.AsSpan(sheetOffset + ((2 + by) * 8 + 2 + bx) * bytesPerBlock, bytesPerBlock);
                    var cropBlock = single.AsSpan(cropOffset + (by * 4 + bx) * bytesPerBlock, bytesPerBlock);
                    Assert.That(sheetBlock.SequenceEqual(cropBlock), Is.True, $"block {bx},{by}");
                }
            }

            // Levels the overlay leaves alone are the plain chain's.
            var plain = EncodeChain(canvas, 128, 0, 0, null);
            var (plain3, _) = LevelOffset(plain, 3);
            var (seeded3, _) = LevelOffset(first, 3);
            Assert.That(first.AsSpan(seeded3, 16 * 16 / 2).SequenceEqual(plain.AsSpan(plain3, 16 * 16 / 2)), Is.True);
        }
    }
}
