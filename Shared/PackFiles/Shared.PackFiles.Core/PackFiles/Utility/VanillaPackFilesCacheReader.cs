using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Models.Containers;
using Shared.Core.PackFiles.Models.FileSources;
using Shared.Core.PackFiles.Serialization;
using ZstdSharp;

namespace Shared.Core.PackFiles.Utility;

internal sealed class VanillaPackFilesCacheReader
{
    private const uint CurrentVersion = 4;
    private const byte ExpandedEntryKind = 1;
    private const byte NamesOnlyEntryKind = 2;
    private const int MaxEntryCount = 100_000;
    private const int MaxTotalFileCount = 5_000_000;
    private const int MaxStringBytes = 16 * 1024 * 1024;
    private const double TimestampToleranceMilliseconds = 1.0;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("WVFC");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly ILogger Logger = Logging.Create<VanillaPackFilesCacheReader>();

    private readonly byte[] _payload;
    private readonly Dictionary<string, CacheEntry> _entries;

    public VanillaPackFilesCacheReader(string cachePath)
    {
        var stopwatch = Stopwatch.StartNew();
        (_payload, _entries) = LoadEntries(cachePath);
        stopwatch.Stop();
        LoadElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        var compressedBytes = string.IsNullOrWhiteSpace(cachePath) || File.Exists(cachePath) == false
            ? 0L
            : new FileInfo(cachePath).Length;
        Logger.Here().Information(
            "Vanilla pack files cache loaded in {ElapsedMs:F1}ms with {PackCount} pack entries, compressedBytes={CompressedBytes}, binaryBytes={BinaryBytes}",
            LoadElapsedMilliseconds,
            _entries.Count,
            compressedBytes,
            _payload.LongLength);
    }

    internal double LoadElapsedMilliseconds { get; }

    public CachedContainerBuild? TryBuildContainer(FileInfo packFile)
    {
        var metadataStopwatch = Stopwatch.StartNew();
        if (!_entries.TryGetValue(NormalizePath(packFile.FullName), out var entry)
            || entry.Size != packFile.Length
            || !double.IsFinite(entry.LastChangedLocal)
            || Math.Abs(entry.LastChangedLocal - GetNodeMtimeMilliseconds(packFile)) > TimestampToleranceMilliseconds
            || !IsVanillaHeader(entry.Header)
            || entry.Header.PackFileCount < 0
            || entry.FileCount != entry.Header.PackFileCount
            || entry.NonWemFileCount < 0
            || entry.NonWemFileCount > entry.FileCount
            || entry.FirstStartPos < 0
            || entry.FirstStartPos > packFile.Length)
        {
            return null;
        }
        metadataStopwatch.Stop();

        var setupStopwatch = Stopwatch.StartNew();
        var header = new PFHeader(
            entry.Header.Version,
            entry.Header.ByteMask,
            entry.Header.ReferenceFileCount)
        {
            Buffer = entry.Header.Buffer,
            FileCount = (uint)entry.NonWemFileCount,
            DataStart = 0
        };
        header.DependantFiles.AddRange(entry.DependencyPacks);

        var container = PackFileContainer.CreatePackFile(
            Path.GetFileNameWithoutExtension(packFile.FullName),
            packFile.FullName,
            header);
        container.OriginalLoadByteSize = packFile.Length;
        container.EnsureFileCapacity(entry.NonWemFileCount);

        var parent = new PackedFileSourceParent { FilePath = packFile.FullName };
        setupStopwatch.Stop();

        if (entry.NonWemFileCount == 0)
        {
            return new CachedContainerBuild(
                container,
                RetainedFileCount: 0,
                SkippedWemCount: entry.FileCount,
                metadataStopwatch.Elapsed.TotalMilliseconds,
                DirectFileMaterializeMs: 0,
                setupStopwatch.Elapsed.TotalMilliseconds,
                UsedAllWemFastPath: entry.FileCount > 0);
        }

        var fileMaterializeStopwatch = Stopwatch.StartNew();
        try
        {
            var reader = new CacheBinaryReader(_payload, entry.FilesOffset);
            var skippedWemCount = 0;
            var retainedFileCount = 0;
            var previousName = string.Empty;
            var startPos = entry.FirstStartPos;

            for (var index = 0; index < entry.FileCount; index++)
            {
                var name = reader.ReadFrontCodedString(previousName);
                var fileSize = (long)reader.ReadUInt32();
                var compressed = reader.ReadByte();
                if (compressed > 1)
                    return null;

                if (string.IsNullOrWhiteSpace(name)
                    || startPos < 0
                    || startPos > packFile.Length
                    || fileSize > packFile.Length - startPos)
                {
                    return null;
                }

                if (ShouldIgnoreFile(name))
                {
                    skippedWemCount++;
                }
                else
                {
                    if (retainedFileCount == 0)
                        header.DataStart = startPos;

                    var normalizedPath = name.ToLowerInvariant();
                    var source = new PackedFileSource(
                        parent,
                        startPos,
                        fileSize,
                        header.HasEncryptedData,
                        compressed == 1,
                        CompressionFormat.None,
                        0);
                    container.AddOrUpdateFile(
                        normalizedPath,
                        new PackFile(GetFileName(normalizedPath), source));
                    retainedFileCount++;
                }

                startPos = checked(startPos + fileSize);
                previousName = name;
            }

            if (reader.Position != entry.FilesEndOffset
                || retainedFileCount != entry.NonWemFileCount
                || skippedWemCount != entry.FileCount - entry.NonWemFileCount)
            {
                return null;
            }

            fileMaterializeStopwatch.Stop();
            return new CachedContainerBuild(
                container,
                retainedFileCount,
                skippedWemCount,
                metadataStopwatch.Elapsed.TotalMilliseconds,
                fileMaterializeStopwatch.Elapsed.TotalMilliseconds,
                setupStopwatch.Elapsed.TotalMilliseconds,
                UsedAllWemFastPath: false);
        }
        catch
        {
            return null;
        }
    }

    private static (byte[] Payload, Dictionary<string, CacheEntry> Entries) LoadEntries(string cachePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cachePath) || File.Exists(cachePath) == false)
                return EmptyCache();

            byte[] payload;
            using (var compressedStream = File.OpenRead(cachePath))
            using (var decompressionStream = new DecompressionStream(compressedStream))
            using (var memory = new MemoryStream())
            {
                decompressionStream.CopyTo(memory);
                payload = memory.ToArray();
            }

            var reader = new CacheBinaryReader(payload);
            if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
                return EmptyCache();
            if (reader.ReadUInt32() != CurrentVersion)
                return EmptyCache();

            var entryCount = CheckedCount(reader.ReadUInt32(), MaxEntryCount);
            var entries = new Dictionary<string, CacheEntry>(entryCount, StringComparer.OrdinalIgnoreCase);

            for (var entryIndex = 0; entryIndex < entryCount; entryIndex++)
            {
                var kind = reader.ReadByte();
                if (kind is not (ExpandedEntryKind or NamesOnlyEntryKind))
                    throw new InvalidDataException("Unknown vanilla cache entry kind.");

                var recordLength = CheckedCount(reader.ReadUInt32(), reader.Remaining);
                var recordEnd = checked(reader.Position + recordLength);

                var packPath = reader.ReadString();
                var size = CheckedInt64(reader.ReadUInt64());
                var lastChangedLocal = reader.ReadDouble();

                if (kind == ExpandedEntryKind)
                {
                    var header = ReadHeader(reader);

                    var dependencyCount = CheckedCount(reader.ReadUInt32(), 100_000);
                    var dependencyPacks = new string[dependencyCount];
                    for (var dependencyIndex = 0; dependencyIndex < dependencyCount; dependencyIndex++)
                    {
                        dependencyPacks[dependencyIndex] = reader.ReadString();
                        if (string.IsNullOrWhiteSpace(dependencyPacks[dependencyIndex]))
                            throw new InvalidDataException("Empty dependency path in vanilla cache.");
                    }

                    var fileCount = CheckedCount(reader.ReadUInt32(), MaxTotalFileCount);
                    var nonWemFileCount = CheckedCount(reader.ReadUInt32(), fileCount);
                    var firstStartPos = CheckedInt64(reader.ReadUInt64());
                    var filesOffset = reader.Position;

                    if (header.PackFileCount != fileCount)
                        throw new InvalidDataException("Pack file count does not match compact cache record.");

                    entries[NormalizePath(packPath)] = new CacheEntry(
                        size,
                        lastChangedLocal,
                        header,
                        dependencyPacks,
                        fileCount,
                        nonWemFileCount,
                        firstStartPos,
                        filesOffset,
                        recordEnd);
                }

                reader.SkipTo(recordEnd);
            }

            if (reader.Remaining != 0)
                throw new InvalidDataException("Trailing data in vanilla cache.");

            return (payload, entries);
        }
        catch
        {
            // The cache is disposable. Corrupt, incomplete, or older cache data
            // must never prevent the host from parsing packs normally.
            return EmptyCache();
        }
    }

    private static CachedPackFileHeader ReadHeader(CacheBinaryReader reader)
    {
        var headerLength = CheckedCount(reader.ReadUInt32(), 1024);
        var headerBytes = reader.ReadBytes(headerLength).ToArray();
        var byteMask = reader.ReadInt32();
        var referenceFileCount = reader.ReadUInt32();
        var packFileIndexSize = reader.ReadUInt32();
        var packFileCount = CheckedCount(reader.ReadUInt32(), int.MaxValue);
        var headerBufferLength = CheckedCount(reader.ReadUInt32(), 1024 * 1024);
        var headerBuffer = reader.ReadBytes(headerBufferLength).ToArray();

        var version = Encoding.ASCII.GetString(headerBytes);
        if (headerBytes.Length != 4
            || headerBuffer.Length == 0
            || version is not ("PFH0" or "PFH2" or "PFH3" or "PFH4" or "PFH5" or "PFH6"))
        {
            throw new InvalidDataException("Invalid pack header in vanilla cache.");
        }

        return new CachedPackFileHeader(
            version,
            byteMask,
            referenceFileCount,
            packFileIndexSize,
            packFileCount,
            headerBuffer);
    }

    private static int CheckedCount(uint value, int maximum)
    {
        if (value > (uint)maximum)
            throw new InvalidDataException("Count exceeds compact cache limit.");
        return (int)value;
    }

    private static long CheckedInt64(ulong value)
    {
        if (value > long.MaxValue)
            throw new InvalidDataException("Unsigned cache value exceeds Int64.");
        return (long)value;
    }

    private static bool IsVanillaHeader(CachedPackFileHeader header)
        => (PackFileCAType)(header.ByteMask & 15) is
            PackFileCAType.BOOT or
            PackFileCAType.RELEASE or
            PackFileCAType.PATCH or
            PackFileCAType.MOVIE;

    internal static bool ShouldIgnoreFile(string path)
        => path.EndsWith(".wem", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .Replace('\\', '/')
                .TrimEnd('/')
                .ToUpperInvariant();
        }
        catch
        {
            return path.Replace('\\', '/').TrimEnd('/').ToUpperInvariant();
        }
    }

    private static double GetNodeMtimeMilliseconds(FileInfo packFile)
        => (packFile.LastWriteTimeUtc - DateTime.UnixEpoch).TotalMilliseconds;

    private static string GetFileName(string path)
    {
        var separator = path.LastIndexOfAny(['\\', '/']);
        return separator < 0 ? path : path[(separator + 1)..];
    }

    private static (byte[] Payload, Dictionary<string, CacheEntry> Entries) EmptyCache()
        => (Array.Empty<byte>(), new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase));

    internal sealed record CachedContainerBuild(
        PackFileContainer Container,
        int RetainedFileCount,
        int SkippedWemCount,
        double MetadataValidationMs,
        double DirectFileMaterializeMs,
        double ContainerSetupMs,
        bool UsedAllWemFastPath);

    internal sealed record CachedPackFileHeader(
        string Version,
        int ByteMask,
        uint ReferenceFileCount,
        uint PackFileIndexSize,
        int PackFileCount,
        byte[] Buffer);

    private sealed record CacheEntry(
        long Size,
        double LastChangedLocal,
        CachedPackFileHeader Header,
        IReadOnlyList<string> DependencyPacks,
        int FileCount,
        int NonWemFileCount,
        long FirstStartPos,
        int FilesOffset,
        int FilesEndOffset);

    private sealed class CacheBinaryReader
    {
        private readonly byte[] _bytes;
        private int _offset;

        public CacheBinaryReader(byte[] bytes, int offset = 0)
        {
            _bytes = bytes;
            if (offset < 0 || offset > bytes.Length)
                throw new InvalidDataException("Invalid compact cache offset.");
            _offset = offset;
        }

        public int Position => _offset;
        public int Remaining => _bytes.Length - _offset;

        public byte ReadByte()
        {
            Require(1);
            return _bytes[_offset++];
        }

        public uint ReadUInt32()
        {
            Require(4);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(_offset, 4));
            _offset += 4;
            return value;
        }

        public int ReadInt32()
        {
            Require(4);
            var value = BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(_offset, 4));
            _offset += 4;
            return value;
        }

        public ulong ReadUInt64()
        {
            Require(8);
            var value = BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan(_offset, 8));
            _offset += 8;
            return value;
        }

        public double ReadDouble()
        {
            Require(8);
            var bits = BinaryPrimitives.ReadInt64LittleEndian(_bytes.AsSpan(_offset, 8));
            _offset += 8;
            var value = BitConverter.Int64BitsToDouble(bits);
            if (!double.IsFinite(value))
                throw new InvalidDataException("Non-finite double in vanilla cache.");
            return value;
        }

        public ReadOnlySpan<byte> ReadBytes(int length)
        {
            Require(length);
            var value = _bytes.AsSpan(_offset, length);
            _offset += length;
            return value;
        }

        public string ReadString()
        {
            var length = CheckedCount(ReadUInt32(), MaxStringBytes);
            return StrictUtf8.GetString(ReadBytes(length));
        }

        public void SkipString()
        {
            var length = CheckedCount(ReadUInt32(), MaxStringBytes);
            Require(length);
            _offset += length;
        }

        public string ReadFrontCodedString(string previous)
        {
            var prefixLength = CheckedCount(ReadUInt32(), previous.Length);
            var suffix = ReadString();
            return string.Concat(previous.AsSpan(0, prefixLength), suffix.AsSpan());
        }

        public void SkipTo(int position)
        {
            if (position < _offset || position > _bytes.Length)
                throw new InvalidDataException("Invalid compact cache record boundary.");
            _offset = position;
        }

        private void Require(int length)
        {
            if (length < 0 || length > Remaining)
                throw new InvalidDataException("Truncated compact vanilla cache.");
        }
    }
}
