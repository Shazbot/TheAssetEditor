using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Models.FileSources;
using Shared.Core.PackFiles.Serialization;
using Shared.Core.Settings;
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
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

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
        setupStopwatch.Stop();

        if (entry.NonWemFileCount == 0)
        {
            var containerSetupStopwatch = Stopwatch.StartNew();
            var emptyContainer = new LazyVanillaPackFileContainer(
                Path.GetFileNameWithoutExtension(packFile.FullName),
                packFile.FullName,
                packFile.Length,
                header,
                _payload,
                entry,
                new Dictionary<ulong, LazyFileRecord>(),
                null,
                uniqueFileCount: 0);
            containerSetupStopwatch.Stop();

            return new CachedContainerBuild(
                emptyContainer,
                RetainedFileCount: 0,
                SkippedWemCount: entry.FileCount,
                metadataStopwatch.Elapsed.TotalMilliseconds,
                FileIndexBuildMs: 0,
                setupStopwatch.Elapsed.TotalMilliseconds + containerSetupStopwatch.Elapsed.TotalMilliseconds,
                UsedAllWemFastPath: entry.FileCount > 0);
        }

        var fileIndexStopwatch = Stopwatch.StartNew();
        try
        {
            var reader = new CacheBinaryReader(_payload, entry.FilesOffset);
            var fileIndex = new Dictionary<ulong, LazyFileRecord>(entry.NonWemFileCount);
            Dictionary<ulong, List<CollisionEntry>>? hashCollisions = null;
            var skippedWemCount = 0;
            var retainedFileCount = 0;
            var uniqueFileCount = 0;
            var previousName = string.Empty;
            var startPos = entry.FirstStartPos;
            long firstRetainedStartPos = -1;

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
                    if (firstRetainedStartPos < 0)
                        firstRetainedStartPos = startPos;

                    var normalizedPath = PathNormalization.NormalizeFileName(name);
                    var record = new LazyFileRecord(
                        index,
                        startPos,
                        fileSize,
                        compressed == 1);

                    if (AddIndexEntry(
                        _payload,
                        entry,
                        normalizedPath,
                        record,
                        fileIndex,
                        ref hashCollisions))
                    {
                        uniqueFileCount++;
                    }

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

            header.DataStart = firstRetainedStartPos < 0 ? 0 : firstRetainedStartPos;
            fileIndexStopwatch.Stop();

            var containerSetupStopwatch = Stopwatch.StartNew();
            var container = new LazyVanillaPackFileContainer(
                Path.GetFileNameWithoutExtension(packFile.FullName),
                packFile.FullName,
                packFile.Length,
                header,
                _payload,
                entry,
                fileIndex,
                hashCollisions,
                uniqueFileCount);
            containerSetupStopwatch.Stop();
            setupStopwatch.Stop();

            if (hashCollisions is { Count: > 0 })
            {
                Logger.Here().Warning(
                    "Vanilla lazy file index for {PackName} encountered {CollisionGroupCount} hash collision group(s); exact-path fallback is active",
                    packFile.Name,
                    hashCollisions.Count);
            }

            return new CachedContainerBuild(
                container,
                retainedFileCount,
                skippedWemCount,
                metadataStopwatch.Elapsed.TotalMilliseconds,
                fileIndexStopwatch.Elapsed.TotalMilliseconds,
                setupStopwatch.Elapsed.TotalMilliseconds + containerSetupStopwatch.Elapsed.TotalMilliseconds,
                UsedAllWemFastPath: false);
        }
        catch
        {
            return null;
        }
    }

    private static bool AddIndexEntry(
        byte[] payload,
        CacheEntry entry,
        string normalizedPath,
        LazyFileRecord record,
        Dictionary<ulong, LazyFileRecord> fileIndex,
        ref Dictionary<ulong, List<CollisionEntry>>? hashCollisions)
    {
        var hash = HashNormalizedPath(normalizedPath);

        if (hashCollisions != null
            && hashCollisions.TryGetValue(hash, out var collisionEntries))
        {
            for (var index = 0; index < collisionEntries.Count; index++)
            {
                if (!string.Equals(collisionEntries[index].Path, normalizedPath, StringComparison.Ordinal))
                    continue;

                collisionEntries[index] = new CollisionEntry(normalizedPath, record);
                return false;
            }

            collisionEntries.Add(new CollisionEntry(normalizedPath, record));
            return true;
        }

        if (!fileIndex.TryGetValue(hash, out var existing))
        {
            fileIndex.Add(hash, record);
            return true;
        }

        var existingPath = ReadNormalizedPathAtIndex(payload, entry, existing.FileIndex);
        if (string.Equals(existingPath, normalizedPath, StringComparison.Ordinal))
        {
            fileIndex[hash] = record;
            return false;
        }

        hashCollisions ??= new Dictionary<ulong, List<CollisionEntry>>();
        hashCollisions[hash] =
        [
            new CollisionEntry(existingPath, existing),
            new CollisionEntry(normalizedPath, record)
        ];
        return true;
    }

    private static string ReadNormalizedPathAtIndex(byte[] payload, CacheEntry entry, int fileIndex)
    {
        if (fileIndex < 0 || fileIndex >= entry.FileCount)
            throw new InvalidDataException("Invalid cached file index.");

        var reader = new CacheBinaryReader(payload, entry.FilesOffset);
        var previousName = string.Empty;

        for (var index = 0; index <= fileIndex; index++)
        {
            var name = reader.ReadFrontCodedString(previousName);
            _ = reader.ReadUInt32();
            _ = reader.ReadByte();
            if (index == fileIndex)
                return PathNormalization.NormalizeFileName(name);
            previousName = name;
        }

        throw new InvalidDataException("Unable to resolve cached file path.");
    }

    private static ulong HashNormalizedPath(string normalizedPath)
    {
        var hash = FnvOffsetBasis;
        foreach (var value in normalizedPath)
        {
            hash ^= value;
            hash = unchecked(hash * FnvPrime);
        }

        return hash;
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
        IPackFileContainer Container,
        int RetainedFileCount,
        int SkippedWemCount,
        double MetadataValidationMs,
        double FileIndexBuildMs,
        double ContainerSetupMs,
        bool UsedAllWemFastPath);

    internal sealed record CachedPackFileHeader(
        string Version,
        int ByteMask,
        uint ReferenceFileCount,
        uint PackFileIndexSize,
        int PackFileCount,
        byte[] Buffer);

    internal sealed record CacheEntry(
        long Size,
        double LastChangedLocal,
        CachedPackFileHeader Header,
        IReadOnlyList<string> DependencyPacks,
        int FileCount,
        int NonWemFileCount,
        long FirstStartPos,
        int FilesOffset,
        int FilesEndOffset);

    internal readonly record struct LazyFileRecord(
        int FileIndex,
        long Offset,
        long Size,
        bool IsCompressed);

    internal sealed record CollisionEntry(
        string Path,
        LazyFileRecord Record);

    internal sealed class LazyVanillaPackFileContainer : IPackFileContainerInternal
    {
        private readonly byte[] _payload;
        private readonly CacheEntry _entry;
        private readonly Dictionary<ulong, LazyFileRecord> _fileIndex;
        private readonly Dictionary<ulong, List<CollisionEntry>>? _hashCollisions;
        private readonly int _uniqueFileCount;
        private readonly PackedFileSourceParent _sourceParent;
        private readonly Dictionary<string, PackFile> _materializedFiles = new(StringComparer.Ordinal);
        private readonly object _materializedLock = new();

        internal LazyVanillaPackFileContainer(
            string name,
            string systemFilePath,
            long originalLoadByteSize,
            PFHeader header,
            byte[] payload,
            CacheEntry entry,
            Dictionary<ulong, LazyFileRecord> fileIndex,
            Dictionary<ulong, List<CollisionEntry>>? hashCollisions,
            int uniqueFileCount)
        {
            Name = name;
            SystemFilePath = systemFilePath;
            OriginalLoadByteSize = originalLoadByteSize;
            Header = header;
            _payload = payload;
            _entry = entry;
            _fileIndex = fileIndex;
            _hashCollisions = hashCollisions;
            _uniqueFileCount = uniqueFileCount;
            _sourceParent = new PackedFileSourceParent { FilePath = systemFilePath };
            PackFileSettings.SaveLocationPath = systemFilePath;
        }

        public string Name { get; }
        public PFHeader Header { get; }
        public long OriginalLoadByteSize { get; }
        public bool IsReadOnly
        {
            get => true;
            set { }
        }

        public bool IsCaPackFile { get; set; }
        public string? SystemFilePath { get; }
        public PackFileSettings PackFileSettings { get; } = new();
        public PackFileContainerType ContainerType => PackFileContainerType.Normal;

        internal int MaterializedFileCount
        {
            get
            {
                lock (_materializedLock)
                    return _materializedFiles.Count;
            }
        }

        internal int IndexedFileCount => _uniqueFileCount;

        public void SaveSettings() => PackFileSettings.Save();

        public int GetFileCount() => _uniqueFileCount;

        public PackFile? FindFile(string path)
        {
            var normalizedPath = PathNormalization.NormalizeFileName(path);
            return TryGetRecord(normalizedPath, out var record)
                ? Materialize(normalizedPath, record)
                : null;
        }

        public bool ContainsFile(string path)
        {
            var normalizedPath = PathNormalization.NormalizeFileName(path);
            return TryGetRecord(normalizedPath, out _);
        }

        public string? GetFullPath(PackFile file)
            => ReferenceEquals(file.Container, this) ? file.VirtualPath : null;

        public IReadOnlyDictionary<string, PackFile> GetAllFiles()
        {
            foreach (var (path, record) in EnumerateFiles())
                _ = Materialize(path, record);

            lock (_materializedLock)
                return _materializedFiles;
        }

        public SortedDictionary<string, List<string>> GetAllFilesByFolder()
            => PackFileSortHelper.BuildSortedFilesByFolder(EnumerateFiles().Select(x => x.Path));

        public List<(string Path, PackFile File)> SearchFiles(
            string? textFilter,
            IReadOnlyList<string>? extensions)
        {
            var results = new List<(string Path, PackFile File)>();

            foreach (var (path, record) in EnumerateFiles())
            {
                var fileName = GetFileName(path);

                if (extensions != null && extensions.Count > 0)
                {
                    var matchesExtension = false;
                    foreach (var ext in extensions)
                    {
                        if (fileName.Contains(ext, StringComparison.OrdinalIgnoreCase))
                        {
                            matchesExtension = true;
                            break;
                        }
                    }

                    if (!matchesExtension)
                        continue;
                }

                if (!string.IsNullOrWhiteSpace(textFilter)
                    && !fileName.Contains(textFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add((path, Materialize(path, record)));
            }

            results.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
            return results;
        }

        public List<(string FileName, PackFile Pack)> FindAllWithExtention(string extention)
        {
            var normalizedExtension = extention.ToLower();
            var results = new List<(string FileName, PackFile Pack)>();

            foreach (var (path, record) in EnumerateFiles())
            {
                if (Path.GetExtension(path) == normalizedExtension)
                    results.Add((path, Materialize(path, record)));
            }

            return results;
        }

        public List<(string Path, PackFile File)> GetDirectoryContent(string directoryPath)
        {
            var normalizedDirectory = PathNormalization.NormalizeDirectoryPath(directoryPath);
            var prefix = string.IsNullOrEmpty(normalizedDirectory) ? "" : normalizedDirectory + "\\";
            var directFileSlashCount = string.IsNullOrEmpty(normalizedDirectory)
                ? 0
                : normalizedDirectory.Count(c => c == '\\') + 1;
            var results = new List<(string Path, PackFile File)>();

            foreach (var (path, record) in EnumerateFiles())
            {
                if ((string.IsNullOrEmpty(prefix)
                        || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    && path.Count(c => c == '\\') == directFileSlashCount)
                {
                    results.Add((path, Materialize(path, record)));
                }
            }

            results.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Path, b.Path));
            return results;
        }

        public void AddOrUpdateFile(string path, PackFile file)
            => ThrowReadOnly();

        public List<PackFile> AddFiles(List<NewPackFileEntry> newFiles)
            => throw CreateReadOnlyException();

        public PackFile? DeleteFile(PackFile file)
            => throw CreateReadOnlyException();

        public void DeleteFolder(string folder)
            => ThrowReadOnly();

        public void MoveFile(PackFile file, string newFolderPath)
            => ThrowReadOnly();

        public string RenameDirectory(string currentNodeName, string newName)
            => throw CreateReadOnlyException();

        public void RenameFile(PackFile file, string newName)
            => ThrowReadOnly();

        public void SaveFileData(PackFile file, byte[] data)
            => ThrowReadOnly();

        public void SaveToDisk(
            string path,
            bool createBackup,
            GameInformation gameInformation)
            => ThrowReadOnly();

        private bool TryGetRecord(string normalizedPath, out LazyFileRecord record)
        {
            var hash = HashNormalizedPath(normalizedPath);
            if (_hashCollisions != null
                && _hashCollisions.TryGetValue(hash, out var collisionEntries))
            {
                foreach (var collisionEntry in collisionEntries)
                {
                    if (string.Equals(collisionEntry.Path, normalizedPath, StringComparison.Ordinal))
                    {
                        record = collisionEntry.Record;
                        return true;
                    }
                }

                record = default;
                return false;
            }

            return _fileIndex.TryGetValue(hash, out record);
        }

        private PackFile Materialize(string normalizedPath, LazyFileRecord record)
        {
            lock (_materializedLock)
            {
                if (_materializedFiles.TryGetValue(normalizedPath, out var existing))
                    return existing;

                var source = new PackedFileSource(
                    _sourceParent,
                    record.Offset,
                    record.Size,
                    Header.HasEncryptedData,
                    record.IsCompressed,
                    CompressionFormat.None,
                    0);
                var file = new PackFile(GetFileName(normalizedPath), source);
                file.AttachToContainer(this, normalizedPath);
                _materializedFiles.Add(normalizedPath, file);
                return file;
            }
        }

        private IEnumerable<(string Path, LazyFileRecord Record)> EnumerateFiles()
        {
            var reader = new CacheBinaryReader(_payload, _entry.FilesOffset);
            var previousName = string.Empty;
            var startPos = _entry.FirstStartPos;

            for (var index = 0; index < _entry.FileCount; index++)
            {
                var name = reader.ReadFrontCodedString(previousName);
                var fileSize = (long)reader.ReadUInt32();
                var compressed = reader.ReadByte();

                if (!ShouldIgnoreFile(name))
                {
                    var normalizedPath = PathNormalization.NormalizeFileName(name);
                    var record = new LazyFileRecord(index, startPos, fileSize, compressed == 1);
                    if (TryGetRecord(normalizedPath, out var indexedRecord)
                        && indexedRecord.FileIndex == record.FileIndex)
                    {
                        yield return (normalizedPath, record);
                    }
                }

                startPos = checked(startPos + fileSize);
                previousName = name;
            }
        }

        private static InvalidOperationException CreateReadOnlyException()
            => new("Vanilla cached pack containers are read-only.");

        private static void ThrowReadOnly()
            => throw CreateReadOnlyException();
    }

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
