using System.Runtime.InteropServices;
using System.Reflection;
using Editors.ImportExport.Dds.Native;

namespace Test.PackOptimizer.Core
{
    /// <summary>The native DirectXTex shim: version pin and the behaviors the optimizer relies on.</summary>
    public class DirectXTexShimTests
    {
        private static Dictionary<string, string> ReadPin()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Native", "DirectXTexShim", "directxtex.pin")))
                directory = directory.Parent;
            Assert.That(directory, Is.Not.Null, "Native/DirectXTexShim/directxtex.pin was not found above the test output");

            return File.ReadAllLines(Path.Combine(directory!.FullName, "Native", "DirectXTexShim", "directxtex.pin"))
                .Where(line => line.Length != 0 && !line.StartsWith('#'))
                .Select(line => line.Split('=', 2))
                .ToDictionary(parts => parts[0], parts => parts[1]);
        }

        [Test]
        public void TheCommittedDllIsBuiltFromThePinnedDirectXTexSource()
        {
            var pin = ReadPin();
            var native = TexHelper.NativeVersion;

            Assert.That(native.Commit, Is.EqualTo(pin["commit"]));
            Assert.That(native.Tag, Is.EqualTo(pin["tag"]));
            Assert.That(native.Version.ToString(), Is.EqualTo(pin["version"]));
        }

        private static ScratchImage Gradient(DXGI_FORMAT format, int width, int height)
        {
            var image = TexHelper.Instance.Initialize2D(format, width, height, 1, 1, CP_FLAGS.NONE);
            var frame = image.GetImage(0, 0, 0);
            var row = new byte[width * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    row[x * 4] = (byte)x;
                    row[x * 4 + 1] = (byte)y;
                    row[x * 4 + 2] = (byte)(x + y);
                    row[x * 4 + 3] = 255;
                }

                Marshal.Copy(row, 0, IntPtr.Add(frame.Pixels, y * (int)frame.RowPitch), row.Length);
            }

            return image;
        }

        [Test]
        public void MipChainCompressAndSaveProduceAValidDds()
        {
            using var image = Gradient(DXGI_FORMAT.B8G8R8A8_UNORM, 64, 32);
            using var chain = image.GenerateMipMaps(TEX_FILTER_FLAGS.DEFAULT, 0);
            Assert.That(chain.GetMetadata().MipLevels, Is.EqualTo(7));

            using var compressed = chain.Compress(DXGI_FORMAT.BC1_UNORM, TEX_COMPRESS_FLAGS.DEFAULT, 0.5f);
            Assert.That(compressed.GetImage(0, 0, 0).SlicePitch, Is.EqualTo(16 * 8 * 8));

            using var legacy = compressed.SaveToDDSMemory(DDS_FLAGS.NONE);
            using var dx10 = compressed.SaveToDDSMemory(DDS_FLAGS.FORCE_DX10_EXT);
            Assert.That(System.Text.Encoding.ASCII.GetString(legacy.ToArray(), 0, 4), Is.EqualTo("DDS "));
            Assert.That(dx10.Length - legacy.Length, Is.EqualTo(20), "a DX10 header adds 20 bytes");
        }

        [Test]
        public void Failures_RaiseTheSameExceptionsAsDirectXTexNet()
        {
            using var srgb = Gradient(DXGI_FORMAT.B8G8R8A8_UNORM_SRGB, 64, 64);

            // DirectXTex rejects a BOX resize of an sRGB image to a quarter or three quarters of its size (E_FAIL); a half is fine.
            Assert.That(() => srgb.Resize(16, 16, TEX_FILTER_FLAGS.BOX), Throws.TypeOf<COMException>().With.Property("HResult").EqualTo(unchecked((int)0x80004005)));
            Assert.That(() => srgb.Resize(48, 48, TEX_FILTER_FLAGS.BOX), Throws.TypeOf<COMException>());
            using var half = srgb.Resize(32, 32, TEX_FILTER_FLAGS.BOX);
            Assert.That(half.GetImage(0, 0, 0).Width, Is.EqualTo(32));

            Assert.That(() => srgb.GetImage(3, 0, 0), Throws.TypeOf<IndexOutOfRangeException>());
        }

        [Test]
        public void ParallelCompressFlagIsAcceptedAndGivesTheSameBytesAsTheDefault()
        {
            using var image = Gradient(DXGI_FORMAT.B8G8R8A8_UNORM, 128, 128);
            using var serial = image.Compress(DXGI_FORMAT.BC3_UNORM, TEX_COMPRESS_FLAGS.DEFAULT, 0.5f);
            using var parallel = image.Compress(DXGI_FORMAT.BC3_UNORM, TEX_COMPRESS_FLAGS.PARALLEL, 0.5f);

            Assert.That(parallel.SaveToDDSMemory(DDS_FLAGS.NONE).ToArray(), Is.EqualTo(serial.SaveToDDSMemory(DDS_FLAGS.NONE).ToArray()));
        }

        [Test]
        public void ImagesCanBeUsedFromSeveralThreadsAtOnce()
        {
            var results = new byte[8][];
            Parallel.For(0, results.Length, index =>
            {
                using var image = Gradient(DXGI_FORMAT.B8G8R8A8_UNORM, 64, 64);
                using var compressed = image.Compress(DXGI_FORMAT.BC1_UNORM, TEX_COMPRESS_FLAGS.DEFAULT, 0.5f);
                results[index] = compressed.SaveToDDSMemory(DDS_FLAGS.NONE).ToArray();
            });

            Assert.That(results.Distinct(new ByteArrayComparer()).Count(), Is.EqualTo(1));
        }

        [Test]
        public void Bc7Decode_MatchesDirectXTexIncludingSlicedMips()
        {
            foreach (var (sourceFormat, compressedFormat, decodedFormat) in new[]
                     {
                         (DXGI_FORMAT.B8G8R8A8_UNORM, DXGI_FORMAT.BC7_UNORM, DXGI_FORMAT.B8G8R8A8_UNORM),
                         (DXGI_FORMAT.B8G8R8A8_UNORM_SRGB, DXGI_FORMAT.BC7_UNORM_SRGB, DXGI_FORMAT.B8G8R8A8_UNORM_SRGB),
                     })
            {
                using var source = Noisy(sourceFormat, 32, 16);
                using var chain = source.GenerateMipMaps(TEX_FILTER_FLAGS.DEFAULT, 0);
                using var compressed = chain.Compress(compressedFormat, TEX_COMPRESS_FLAGS.DEFAULT, 0.5f);
                using var stream = compressed.SaveToDDSMemory(DDS_FLAGS.FORCE_DX10_EXT);
                var dds = stream.ToArray();

                Assert.That(BitConverter.ToUInt32(dds, 84), Is.EqualTo(0x30315844u));
                Assert.That(BitConverter.ToInt32(dds, 128), Is.EqualTo((int)compressedFormat));

                using var expectedImage = compressed.Decompress(decodedFormat);
                var expectedTop = ReadBgra(expectedImage.GetImage(0, 0, 0));
                using var pfimStream = new MemoryStream(dds);
                using var pfim = Pfim.Pfimage.FromStream(pfimStream);
                Assert.That(pfim.Format, Is.EqualTo(Pfim.ImageFormat.Rgba32));
                Assert.That(ReadPfim(pfim), Is.EqualTo(expectedTop));
                var decoded = InvokeDecode(dds, out var width, out var height);
                Assert.That((width, height), Is.EqualTo((32, 16)));
                Assert.That(decoded, Is.EqualTo(expectedTop));

                var layoutArguments = new object?[] { dds.AsSpan(0, 148).ToArray(), null };
                Assert.That(OptimizerMethod("TryReadDdsLayout").Invoke(null, layoutArguments), Is.EqualTo(true));
                var sliceArguments = new object?[] { dds, layoutArguments[1], 1, null, null };
                Assert.That(OptimizerMethod("TrySliceDdsMips").Invoke(null, sliceArguments), Is.EqualTo(true));
                var sliced = (byte[])sliceArguments[3]!;
                var expectedLevelOne = ReadBgra(expectedImage.GetImage(1, 0, 0));
                var slicedDecoded = InvokeDecode(sliced, out width, out height);
                Assert.That((width, height), Is.EqualTo((16, 8)));
                Assert.That(slicedDecoded, Is.EqualTo(expectedLevelOne));
            }
        }

        private static ScratchImage Noisy(DXGI_FORMAT format, int width, int height)
        {
            var random = new Random(0xBC7);
            var image = TexHelper.Instance.Initialize2D(
                format, width, height, 1, 1, CP_FLAGS.NONE);
            var frame = image.GetImage(0, 0, 0);
            var row = new byte[width * 4];
            for (var y = 0; y < height; y++)
            {
                random.NextBytes(row);
                Marshal.Copy(row, 0, IntPtr.Add(frame.Pixels, y * (int)frame.RowPitch), row.Length);
            }

            return image;
        }

        private static MethodInfo OptimizerMethod(string name)
        {
            var type = Assembly.Load("Editors.PackOptimizer.Core")
                .GetType("Editors.PackOptimizer.Core.PackTextureAtlasBatchService", throwOnError: true)!;
            return type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(name);
        }

        private static byte[] InvokeDecode(byte[] dds, out int width, out int height)
        {
            var arguments = new object?[] { dds, null, 0, 0, null };
            var decoded = (bool)OptimizerMethod("TryDecodeDdsTopLevel").Invoke(null, arguments)!;
            Assert.That(decoded, Is.True, arguments[4] as string ?? "BC7 decode failed");
            width = (int)arguments[2]!;
            height = (int)arguments[3]!;
            return (byte[])arguments[1]!;
        }

        private static byte[] ReadBgra(Image image)
        {
            var bytes = new byte[checked(image.Width * image.Height * 4)];
            var rowBytes = checked(image.Width * 4);
            var pitch = checked((int)image.RowPitch);
            for (var y = 0; y < image.Height; y++)
            {
                Marshal.Copy(
                    IntPtr.Add(image.Pixels, checked(y * pitch)),
                    bytes,
                    y * rowBytes,
                    rowBytes);
            }

            return bytes;
        }

        private static byte[] ReadPfim(Pfim.IImage image)
        {
            var bytes = new byte[checked(image.Width * image.Height * 4)];
            var rowBytes = checked(image.Width * 4);
            for (var y = 0; y < image.Height; y++)
                image.Data.AsSpan(y * image.Stride, rowBytes).CopyTo(bytes.AsSpan(y * rowBytes, rowBytes));
            return bytes;
        }

        private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
        {
            public bool Equals(byte[]? x, byte[]? y) => x != null && y != null && x.AsSpan().SequenceEqual(y);
            public int GetHashCode(byte[] value) => value.Length;
        }
    }
}
