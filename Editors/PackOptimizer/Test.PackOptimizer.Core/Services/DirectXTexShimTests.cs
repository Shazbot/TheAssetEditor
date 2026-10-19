using System.Runtime.InteropServices;
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

        private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
        {
            public bool Equals(byte[]? x, byte[]? y) => x != null && y != null && x.AsSpan().SequenceEqual(y);
            public int GetHashCode(byte[] value) => value.Length;
        }
    }
}
