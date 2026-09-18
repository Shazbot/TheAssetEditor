using System.Buffers.Binary;
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
                var compressionInfo = ResolveCompressionInfo(data);
                data = FileCompression.Decompress(data, compressionInfo.UncompressedSize, compressionInfo.Format);
                if (data.Length != compressionInfo.UncompressedSize)
                    throw new InvalidDataException($"Decompressed bytes {data.Length:N0} does not match the expected uncompressed bytes {compressionInfo.UncompressedSize:N0}.");
            }

            return data;
        }

        public byte[] PeekData(int size)
        {
            byte[] data;

            using (var stream = File.Open(Parent.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Seek(Offset, SeekOrigin.Begin);

                if (!IsEncrypted && !IsCompressed)
                {
                    data = new byte[size];
                    stream.ReadExactly(data);
                }
                else
                {
                    data = new byte[Size];
                    stream.ReadExactly(data);

                    if (IsEncrypted)
                        data = FileEncryption.Decrypt(data);

                    if (IsCompressed)
                    {
                        var compressionInfo = ResolveCompressionInfo(data);
                        data = FileCompression.Decompress(data, size, compressionInfo.Format);
                        if (data.Length != size)
                            throw new InvalidDataException($"Decompressed bytes {data.Length:N0} does not match the expected uncompressed bytes {size:N0}.");
                    }
                }
            }           

            return data;
        }

        private (CompressionFormat Format, int UncompressedSize) ResolveCompressionInfo(byte[] data)
        {
            if (CompressionFormat != CompressionFormat.None && UncompressedSize > 0)
            {
                if (UncompressedSize > int.MaxValue)
                    throw new InvalidDataException($"Uncompressed bytes {UncompressedSize:N0} exceed the supported size.");

                return (CompressionFormat, (int)UncompressedSize);
            }

            if (data.Length < 8)
                throw new InvalidDataException("Compressed data does not contain a compression header.");

            var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4));
            if (uncompressedSize > int.MaxValue)
                throw new InvalidDataException($"Uncompressed bytes {uncompressedSize:N0} exceed the supported size.");

            var compressionFormat = FileCompression.GetCompressionFormat(data.AsSpan(4, 4).ToArray());
            if (compressionFormat == CompressionFormat.None)
                throw new InvalidDataException("Compressed data has an unknown compression format.");

            return (compressionFormat, (int)uncompressedSize);
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
