using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections;
using Editors.ImportExport.Dds.Native;
using Editors.PackOptimizer.Core;

namespace Test.PackOptimizer.Core
{
    public class SharedRepackSourceLevelTests
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

        private static MethodInfo Service(string name)
            => typeof(PackTextureAtlasBatchService).GetMethod(name, PrivateStatic)!;

        private static object? ReadLevel(object source, int level)
            => SourceType.GetMethod("Level", AnyInstance)!.Invoke(source, [level]);

        private static int[] CachedLevels(object source)
            => ((IDictionary)SourceType.GetField("_levels", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(source)!)
                .Keys.Cast<int>().Order().ToArray();

        private static Type SourceType
            => typeof(PackTextureAtlasBatchService).GetNestedType("SharedRepackSource", BindingFlags.NonPublic)!;

        private static object? CreateSource(
            byte[] dds,
            (byte[] Bgra, int Width, int Height)? seededTopLevel = null)
        {
            var cacheType = typeof(PackTextureAtlasBatchService)
                .GetNestedType("SharedRepackSourceCache", BindingFlags.NonPublic)!;
            var cache = cacheType.GetConstructors(AnyInstance).Single()
                .Invoke([new Func<string, byte[]?>(_ => dds)]);
            return cacheType.GetMethod("FromDecodedBytes", AnyInstance)!
                .Invoke(cache, ["fixture.dds", dds, seededTopLevel]);
        }

        private static object ReadLayout(byte[] dds)
        {
            var arguments = new object?[] { dds.AsSpan(0, Math.Min(dds.Length, 148)).ToArray(), null };
            Assert.That((bool)Service("TryReadDdsLayout").Invoke(null, arguments)!, Is.True);
            return arguments[1]!;
        }

        private static (byte[] Bgra, int Width, int Height)? DecodeLevel(object? source, int level)
        {
            if (source == null)
                return null;
            var decoded = ReadLevel(source, level);
            return decoded == null ? null : ((byte[] Bgra, int Width, int Height))decoded;
        }

        private static (byte[] Bgra, int Width, int Height)? DecodeWithPreviousPath(
            byte[] dds,
            object layout,
            int level)
        {
            var mipDds = dds;
            if (level > 0)
            {
                var sliceArguments = new object?[] { dds, layout, level, null, null };
                if (!(bool)Service("TrySliceDdsMips").Invoke(null, sliceArguments)!)
                    return null;
                mipDds = (byte[])sliceArguments[3]!;
            }

            var decodeArguments = new object?[] { mipDds, null, 0, 0, null };
            if (!(bool)Service("TryDecodeDdsTopLevel").Invoke(null, decodeArguments)!)
                return null;
            return ((byte[] Bgra, int Width, int Height))(
                (byte[])decodeArguments[1]!, (int)decodeArguments[2]!, (int)decodeArguments[3]!);
        }

        private static byte[] BuildMipChain(DXGI_FORMAT compressedFormat)
        {
            const int width = 13;
            const int height = 7;
            using var image = TexHelper.Instance.Initialize2D(
                DXGI_FORMAT.B8G8R8A8_UNORM, width, height, 1, 1, CP_FLAGS.NONE);
            var frame = image.GetImage(0, 0, 0);
            var row = new byte[width * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    row[x * 4] = (byte)(x * 17 + y * 3 + 1);
                    row[x * 4 + 1] = (byte)(y * 19 + x * 7 + 2);
                    row[x * 4 + 2] = (byte)(x * 11 + y * 13 + 3);
                    row[x * 4 + 3] = 255;
                }

                Marshal.Copy(row, 0, IntPtr.Add(frame.Pixels, y * (int)frame.RowPitch), row.Length);
            }

            using var chain = image.GenerateMipMaps(TEX_FILTER_FLAGS.DEFAULT, 0);
            using var compressed = chain.Compress(compressedFormat, TEX_COMPRESS_FLAGS.DEFAULT, 0.5f);
            using var stream = compressed.SaveToDDSMemory(DDS_FLAGS.FORCE_DX10_EXT);
            return stream.ToArray();
        }

        [TestCase(DXGI_FORMAT.BC1_UNORM)]
        [TestCase(DXGI_FORMAT.BC2_UNORM)]
        [TestCase(DXGI_FORMAT.BC3_UNORM)]
        [TestCase(DXGI_FORMAT.BC7_UNORM)]
        public void EveryCachedMipMatchesThePreviousSliceAndDecodePath(DXGI_FORMAT format)
        {
            var dds = BuildMipChain(format);
            var layout = ReadLayout(dds);
            var mipCount = (int)layout.GetType().GetProperty("MipCount")!.GetValue(layout)!;
            Assert.That(mipCount, Is.GreaterThan(1));

            // Ask for the smallest mip first to ensure the cache does not depend on level zero being read first.
            var source = CreateSource(dds)!;
            var smallest = mipCount - 1;
            var lowerFirst = DecodeLevel(source, smallest);
            Assert.That(lowerFirst, Is.Not.Null);

            var actual = new (byte[] Bgra, int Width, int Height)?[mipCount];
            actual[smallest] = lowerFirst;
            Parallel.For(0, mipCount, level => actual[level] = DecodeLevel(source, level));

            for (var level = 0; level < mipCount; level++)
            {
                var expected = DecodeWithPreviousPath(dds, layout, level);
                Assert.That(expected, Is.Not.Null, $"reference mip {level}");
                Assert.That(actual[level], Is.Not.Null, $"cached mip {level}");
                Assert.That(actual[level]!.Value.Width, Is.EqualTo(expected!.Value.Width), $"mip {level} width");
                Assert.That(actual[level]!.Value.Height, Is.EqualTo(expected.Value.Height), $"mip {level} height");
                Assert.That(actual[level]!.Value.Bgra, Is.EqualTo(expected.Value.Bgra), $"mip {level} pixels");
                Assert.That(DecodeLevel(source, level)!.Value.Bgra, Is.SameAs(actual[level]!.Value.Bgra),
                    $"mip {level} should return its cached pixel array");
            }

            // Cold concurrent requests for the same level share one suffix decode and its cached result.
            var concurrentSource = CreateSource(dds)!;
            var concurrent = new (byte[] Bgra, int Width, int Height)?[16];
            Parallel.For(0, concurrent.Length, index => concurrent[index] = DecodeLevel(concurrentSource, 0));
            foreach (var level in concurrent)
            {
                Assert.That(level, Is.Not.Null);
                Assert.That(level!.Value.Bgra, Is.EqualTo(actual[0]!.Value.Bgra));
            }
        }

        [Test]
        public void SeededTopLevelIsPreservedAndInvalidOrTruncatedSourcesReturnNull()
        {
            var dds = BuildMipChain(DXGI_FORMAT.BC3_UNORM);
            var layout = ReadLayout(dds);
            var mipCount = (int)layout.GetType().GetProperty("MipCount")!.GetValue(layout)!;
            var seeded = new byte[13 * 7 * 4];
            Array.Fill(seeded, (byte)123);
            var source = CreateSource(dds, (seeded, 13, 7))!;

            var top = DecodeLevel(source, 0);
            Assert.That(top, Is.Not.Null);
            Assert.That(top!.Value.Bgra, Is.SameAs(seeded));
            Assert.That(mipCount, Is.GreaterThan(2));
            Assert.That(DecodeLevel(source, 2), Is.Not.Null);
            Assert.That(CachedLevels(source), Is.EqualTo(new[] { 0, 2, 3 }),
                "a preseeded top plus a level 2 request should decode only the level 2 suffix");
            Assert.That(DecodeLevel(source, 0)!.Value.Bgra, Is.SameAs(seeded));
            Assert.That(DecodeLevel(source, -1), Is.Null);
            Assert.That(DecodeLevel(source, mipCount), Is.Null);

            Assert.That(CreateSource(new byte[4]), Is.Null, "invalid DDS header");
            var truncatedSource = CreateSource(dds[..^16]);
            Assert.That(truncatedSource, Is.Not.Null, "the complete header remains readable");
            Assert.That(DecodeLevel(truncatedSource, 0), Is.Null, "a truncated mip chain must fail decoding");
            Assert.That(DecodeLevel(truncatedSource, mipCount - 1), Is.Null, "failed chain decoding is cached");

            var missingCacheType = typeof(PackTextureAtlasBatchService)
                .GetNestedType("SharedRepackSourceCache", BindingFlags.NonPublic)!;
            var missingCache = missingCacheType.GetConstructors(AnyInstance).Single()
                .Invoke([new Func<string, byte[]?>(_ => null)]);
            Assert.That(missingCacheType.GetMethod("Get", AnyInstance)!.Invoke(missingCache, ["missing.dds"]), Is.Null);
        }
    }
}
