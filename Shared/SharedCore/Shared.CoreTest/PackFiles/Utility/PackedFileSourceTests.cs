using Shared.Core.PackFiles.Models.FileSources;
using Shared.Core.PackFiles.Utility;

namespace Shared.CoreTest.PackFiles.Utility
{
    internal class PackedFileSourceTests
    {
        private static readonly PackedFileSourceParent TestParent = new() { FilePath = "unused.pack" };

        [TestCase(CompressionFormat.Zstd)]
        [TestCase(CompressionFormat.Lz4)]
        [TestCase(CompressionFormat.Lzma1)]
        public void PeekData_MatchesFullRead_ForCompressedFormats(CompressionFormat compressionFormat)
        {
            var originalData = CreatePayload(16 * 1024);
            var compressedData = FileCompression.Compress(originalData, compressionFormat);
            var source = CreateCompressedSource(compressionFormat, compressedData, originalData.Length);

            using var stream = new MemoryStream(compressedData, writable: false);
            var expected = source.ReadData(stream)[..100];
            var actual = source.PeekData(100, stream);

            Assert.That(actual, Is.EqualTo(expected));
        }

        [TestCase(50)]
        [TestCase(100)]
        [TestCase(150)]
        public void PeekData_ReturnsAvailablePrefix_ForUncompressedEntries(int requestedSize)
        {
            var originalData = CreatePayload(100);
            var source = new PackedFileSource(
                TestParent,
                offset: 0,
                length: originalData.Length,
                isEncrypted: false,
                isCompressed: false,
                compressionFormat: CompressionFormat.None,
                uncompressedSize: 0);

            using var stream = new MemoryStream(originalData, writable: false);
            var actual = source.PeekData(requestedSize, stream);

            Assert.That(actual, Is.EqualTo(originalData[..Math.Min(requestedSize, originalData.Length)]));
        }

        [TestCase(CompressionFormat.Zstd)]
        [TestCase(CompressionFormat.Lz4)]
        [TestCase(CompressionFormat.Lzma1)]
        public void PeekData_ReturnsAvailablePrefix_ForShortCompressedEntries(CompressionFormat compressionFormat)
        {
            var originalData = CreatePayload(50);
            var compressedData = FileCompression.Compress(originalData, compressionFormat);
            var source = CreateCompressedSource(compressionFormat, compressedData, originalData.Length);

            using var stream = new MemoryStream(compressedData, writable: false);
            var actual = source.PeekData(100, stream);

            Assert.That(actual, Is.EqualTo(originalData));
        }

        [Test]
        public void PeekData_ReusesSharedStreamAcrossPackedEntries()
        {
            var entries = new[]
            {
                CreateEntry(CompressionFormat.Zstd, CreatePayload(8 * 1024)),
                CreateEntry(CompressionFormat.Lz4, CreatePayload(8 * 1024, seed: 11)),
                CreateEntry(CompressionFormat.Lzma1, CreatePayload(8 * 1024, seed: 29)),
                CreateEntry(CompressionFormat.None, CreatePayload(8 * 1024, seed: 47))
            };

            using var stream = new MemoryStream();
            var sources = new List<(PackedFileSource Source, byte[] OriginalData)>();
            foreach (var entry in entries)
            {
                var offset = stream.Position;
                stream.Write(entry.StoredData);
                sources.Add((
                    new PackedFileSource(
                        TestParent,
                        offset,
                        entry.StoredData.Length,
                        isEncrypted: false,
                        isCompressed: entry.CompressionFormat != CompressionFormat.None,
                        compressionFormat: entry.CompressionFormat,
                        uncompressedSize: (uint)entry.OriginalData.Length),
                    entry.OriginalData));
            }

            foreach (var (source, originalData) in sources)
                Assert.That(source.PeekData(100, stream), Is.EqualTo(originalData[..100]));
        }

        [TestCase(false, CompressionFormat.None)]
        [TestCase(true, CompressionFormat.Zstd)]
        public void PeekData_PreservesEncryptedSources(bool isCompressed, CompressionFormat compressionFormat)
        {
            var originalData = CreatePayload(16 * 1024);
            var unencryptedData = isCompressed
                ? FileCompression.Compress(originalData, compressionFormat)
                : originalData;
            var encryptedData = FileEncryption.Encrypt(unencryptedData);
            var source = new PackedFileSource(
                TestParent,
                offset: 0,
                length: encryptedData.Length,
                isEncrypted: true,
                isCompressed: isCompressed,
                compressionFormat: compressionFormat,
                uncompressedSize: (uint)originalData.Length);

            using var stream = new MemoryStream(encryptedData, writable: false);
            var expected = source.ReadData(stream)[..100];
            var actual = source.PeekData(100, stream);

            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void PeekData_ZstdDoesNotReadTheEntireCompressedEntry()
        {
            var originalData = CreatePseudoRandomPayload(4 * 1024 * 1024);
            var compressedData = FileCompression.Compress(originalData, CompressionFormat.Zstd);
            var source = CreateCompressedSource(CompressionFormat.Zstd, compressedData, originalData.Length);
            using var countingStream = new CountingStream(new MemoryStream(compressedData, writable: false));

            var actual = source.PeekData(100, countingStream);

            Assert.That(actual, Is.EqualTo(originalData[..100]));
            Assert.That(
                countingStream.BytesRead,
                Is.LessThan(compressedData.Length / 4),
                $"Expected a small prefix read, but consumed {countingStream.BytesRead:N0} of {compressedData.Length:N0} compressed bytes.");
        }

        private static PackedFileSource CreateCompressedSource(
            CompressionFormat compressionFormat,
            byte[] compressedData,
            int uncompressedSize)
        {
            return new PackedFileSource(
                TestParent,
                offset: 0,
                length: compressedData.Length,
                isEncrypted: false,
                isCompressed: true,
                compressionFormat: compressionFormat,
                uncompressedSize: (uint)uncompressedSize);
        }

        private static (CompressionFormat CompressionFormat, byte[] StoredData, byte[] OriginalData) CreateEntry(
            CompressionFormat compressionFormat,
            byte[] originalData)
        {
            var storedData = compressionFormat == CompressionFormat.None
                ? originalData
                : FileCompression.Compress(originalData, compressionFormat);
            return (compressionFormat, storedData, originalData);
        }

        private static byte[] CreatePayload(int length, int seed = 0)
        {
            var payload = new byte[length];
            for (var i = 0; i < payload.Length; i++)
                payload[i] = (byte)((i * 37 + seed * 13) % 251);
            return payload;
        }

        private static byte[] CreatePseudoRandomPayload(int length)
        {
            var payload = new byte[length];
            uint state = 0x1234_5678;
            for (var i = 0; i < payload.Length; i++)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                payload[i] = (byte)state;
            }

            return payload;
        }

        private sealed class CountingStream(Stream innerStream) : Stream
        {
            public long BytesRead { get; private set; }

            public override bool CanRead => innerStream.CanRead;
            public override bool CanSeek => innerStream.CanSeek;
            public override bool CanWrite => innerStream.CanWrite;
            public override long Length => innerStream.Length;
            public override long Position
            {
                get => innerStream.Position;
                set => innerStream.Position = value;
            }

            public override void Flush() => innerStream.Flush();

            public override int Read(byte[] buffer, int offset, int count)
            {
                var bytesRead = innerStream.Read(buffer, offset, count);
                BytesRead += bytesRead;
                return bytesRead;
            }

            public override int Read(Span<byte> buffer)
            {
                var bytesRead = innerStream.Read(buffer);
                BytesRead += bytesRead;
                return bytesRead;
            }

            public override long Seek(long offset, SeekOrigin origin) => innerStream.Seek(offset, origin);

            public override void SetLength(long value) => innerStream.SetLength(value);

            public override void Write(byte[] buffer, int offset, int count) => innerStream.Write(buffer, offset, count);

            public override void Write(ReadOnlySpan<byte> buffer) => innerStream.Write(buffer);

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    innerStream.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
