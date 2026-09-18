using Shared.ByteParsing;
using Shared.Core.PackFiles.Utility;

namespace Shared.Core.PackFiles.Models.FileSources
{
    public class PackedFileSourceParent
    {
        public required string FilePath { get; set; }
    }

    public record PackedFileSource : IDataSource
    {
        public long Offset { get; private set; }
        public long Size { get; private set; }
        public bool IsEncrypted { get; private set; }
        public bool IsCompressed { get; set; }
        public CompressionFormat CompressionFormat { get; set; }
        public uint UncompressedSize { get; set; }
        public PackedFileSourceParent Parent { get; set; }

        public PackedFileSource(
            PackedFileSourceParent parent,
            long offset,
            long length,
            bool isEncrypted,
            bool isCompressed,
            CompressionFormat compressionFormat,
            uint uncompressedSize)
        {
            Offset = offset;
            Parent = parent;
            Size = length;
            IsEncrypted = isEncrypted;
            IsCompressed = isCompressed;
            CompressionFormat = compressionFormat;
            UncompressedSize = uncompressedSize;
        }

        public byte[] ReadData()
        {
            using var stream = File.Open(Parent.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return ReadData(stream);
        }

        public byte[] ReadData(Stream knownStream)
        {
            var data = new byte[Size];
            knownStream.Seek(Offset, SeekOrigin.Begin);
            knownStream.ReadExactly(data, 0, (int)Size);

            if (IsEncrypted)
                data = FileEncryption.Decrypt(data);

            if (IsCompressed)
            {
                data = FileCompression.Decompress(data, (int)UncompressedSize, CompressionFormat);
                if (data.Length != UncompressedSize)
                    throw new InvalidDataException($"Decompressed bytes {data.Length:N0} does not match the expected uncompressed bytes {UncompressedSize:N0}.");
            }

            return data;
        }

        public byte[] PeekData(int size)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(size);

            using var stream = File.Open(Parent.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return PeekData(size, stream);
        }

        public byte[] PeekData(int size, Stream knownStream)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(size);
            ArgumentNullException.ThrowIfNull(knownStream);

            // The current encryption API only supports whole-entry decryption. Keep this
            // fallback correct while allowing the common unencrypted path to stay streamed.
            if (IsEncrypted)
            {
                var data = ReadData(knownStream);
                return data.AsSpan(0, Math.Min(size, data.Length)).ToArray();
            }

            if (!IsCompressed)
            {
                var prefixSize = checked((int)Math.Min((long)size, Size));
                var data = new byte[prefixSize];

                knownStream.Seek(Offset, SeekOrigin.Begin);
                knownStream.ReadExactly(data);
                return data;
            }

            var decompressedPrefixSize = checked((int)Math.Min((long)size, UncompressedSize));
            using var boundedStream = new BoundedReadStream(knownStream, Offset, Size);
            var decompressedData = FileCompression.Decompress(boundedStream, decompressedPrefixSize, CompressionFormat);
            if (decompressedData.Length != decompressedPrefixSize)
                throw new InvalidDataException($"Decompressed bytes {decompressedData.Length:N0} does not match the expected prefix bytes {decompressedPrefixSize:N0}.");

            return decompressedData;
        }

        public byte[] ReadDataWithoutDecompressing()
        {
            var data = new byte[Size];

            using (var stream = File.Open(Parent.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Seek(Offset, SeekOrigin.Begin);
                stream.ReadExactly(data);
            }

            if (IsEncrypted)
                data = FileEncryption.Decrypt(data);

            return data;
        }

        public ByteChunk ReadDataAsChunk() => new ByteChunk(ReadData());
    }
}
