using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Serialization;
using ZstdSharp;

namespace Shared.Core.PackFiles.Utility;

internal sealed class VanillaPackFilesCacheReader
{
    private const int CurrentVersion = 2;
    private const double TimestampToleranceMilliseconds = 1.0;
    private static readonly ILogger Logger = Logging.Create<VanillaPackFilesCacheReader>();

    private readonly Dictionary<string, CacheEntry> _entries;

    public VanillaPackFilesCacheReader(string cachePath)
    {
        var stopwatch = Stopwatch.StartNew();
        _entries = LoadEntries(cachePath);
        stopwatch.Stop();
        LoadElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        Logger.Here().Information(
            "Vanilla pack files cache loaded in {ElapsedMs:F1}ms with {PackCount} pack entries",
            LoadElapsedMilliseconds,
            _entries.Count);
    }

    internal double LoadElapsedMilliseconds { get; }

    public CachedPackIndex? TryGet(FileInfo packFile)
    {
        var metadataStopwatch = Stopwatch.StartNew();
        if (!_entries.TryGetValue(NormalizePath(packFile.FullName), out var entry)
            || entry.PackedFiles == null
            || entry.PackHeader == null
            || entry.DependencyPacks == null
            || entry.Size != packFile.Length
            || !double.IsFinite(entry.LastChangedLocal)
            || Math.Abs(entry.LastChangedLocal - GetNodeMtimeMilliseconds(packFile)) > TimestampToleranceMilliseconds)
        {
            return null;
        }

        if (!TryReadHeader(entry.PackHeader, out var header)
            || !IsVanillaHeader(header)
            || header.PackFileCount < 0
            || entry.PackedFiles.Count != header.PackFileCount)
        {
            return null;
        }
        metadataStopwatch.Stop();

        var fileMaterializeStopwatch = Stopwatch.StartNew();
        var packedFiles = new List<CachedPackedFile>(entry.PackedFiles.Count);
        var skippedWemCount = 0;
        var previousEnd = -1L;
        foreach (var packedFile in entry.PackedFiles)
        {
            if (string.IsNullOrWhiteSpace(packedFile.Name)
                || packedFile.FileSize < 0
                || packedFile.FileSize > int.MaxValue
                || packedFile.StartPos < 0
                || packedFile.StartPos > packFile.Length
                || packedFile.FileSize > packFile.Length - packedFile.StartPos
                || packedFile.StartPos < previousEnd)
            {
                return null;
            }

            previousEnd = packedFile.StartPos + packedFile.FileSize;
            if (ShouldIgnoreFile(packedFile.Name))
            {
                skippedWemCount++;
                continue;
            }

            packedFiles.Add(new CachedPackedFile(
                packedFile.Name,
                packedFile.FileSize,
                packedFile.StartPos,
                packedFile.IsCompressed));
        }

        if (entry.DependencyPacks.Any(string.IsNullOrWhiteSpace))
            return null;

        fileMaterializeStopwatch.Stop();
        return new CachedPackIndex(
            header,
            packedFiles,
            entry.DependencyPacks,
            skippedWemCount,
            metadataStopwatch.Elapsed.TotalMilliseconds,
            fileMaterializeStopwatch.Elapsed.TotalMilliseconds);
    }

    private static Dictionary<string, CacheEntry> LoadEntries(string cachePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cachePath) || File.Exists(cachePath) == false)
                return new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

            using var compressedStream = File.OpenRead(cachePath);
            using var decompressionStream = new DecompressionStream(compressedStream);
            var document = JsonSerializer.Deserialize(
                decompressionStream,
                VanillaPackFilesCacheJsonContext.Default.CacheDocument);
            if (document?.Version != CurrentVersion || document.Entries == null)
                return new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

            return document.Entries
                .Where(pair => string.IsNullOrWhiteSpace(pair.Key) == false && pair.Value != null)
                .ToDictionary(pair => NormalizePath(pair.Key), pair => pair.Value!, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // This cache is disposable. A corrupt, incomplete, or older cache
            // must never prevent the host from loading packs normally.
            return new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool TryReadHeader(CachePackHeader source, out CachedPackFileHeader header)
    {
        header = null!;
        if (source.ByteMask < int.MinValue
            || source.ByteMask > int.MaxValue
            || source.RefFileCount < 0
            || source.RefFileCount > uint.MaxValue
            || source.PackFileIndexSize < 0
            || source.PackFileIndexSize > uint.MaxValue
            || source.PackFileCount < 0
            || source.PackFileCount > int.MaxValue
            || string.IsNullOrWhiteSpace(source.Header)
            || string.IsNullOrWhiteSpace(source.HeaderBuffer))
        {
            return false;
        }

        try
        {
            var headerBytes = Convert.FromBase64String(source.Header);
            var headerBuffer = Convert.FromBase64String(source.HeaderBuffer);
            var version = Encoding.ASCII.GetString(headerBytes);
            if (headerBytes.Length != 4
                || headerBuffer.Length == 0
                || version is not ("PFH0" or "PFH2" or "PFH3" or "PFH4" or "PFH5" or "PFH6"))
            {
                return false;
            }

            header = new CachedPackFileHeader(
                version,
                (int)source.ByteMask,
                (uint)source.RefFileCount,
                (uint)source.PackFileIndexSize,
                (int)source.PackFileCount,
                headerBuffer);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
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

    internal sealed record CachedPackIndex(
        CachedPackFileHeader Header,
        IReadOnlyList<CachedPackedFile> PackedFiles,
        IReadOnlyList<string> DependencyPacks,
        int SkippedWemCount,
        double MetadataValidationMs,
        double FileMaterializeMs);

    internal sealed record CachedPackFileHeader(
        string Version,
        int ByteMask,
        uint ReferenceFileCount,
        uint PackFileIndexSize,
        int PackFileCount,
        byte[] Buffer);

    internal sealed record CachedPackedFile(
        string Name,
        long FileSize,
        long StartPos,
        bool IsCompressed);

    internal sealed class CacheDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("entries")]
        public Dictionary<string, CacheEntry>? Entries { get; set; }
    }

    internal sealed class CacheEntry
    {
        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("lastChangedLocal")]
        public double LastChangedLocal { get; set; }

        [JsonPropertyName("packedFiles")]
        public List<CachedPackedFileDto>? PackedFiles { get; set; }

        [JsonPropertyName("packHeader")]
        public CachePackHeader? PackHeader { get; set; }

        [JsonPropertyName("dependencyPacks")]
        public List<string>? DependencyPacks { get; set; }
    }

    internal sealed class CachedPackedFileDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("file_size")]
        public long FileSize { get; set; }

        [JsonPropertyName("start_pos")]
        public long StartPos { get; set; }

        [JsonPropertyName("is_compressed")]
        public bool IsCompressed { get; set; }
    }

    internal sealed class CachePackHeader
    {
        [JsonPropertyName("header")]
        public string? Header { get; set; }

        [JsonPropertyName("byteMask")]
        public long ByteMask { get; set; }

        [JsonPropertyName("refFileCount")]
        public long RefFileCount { get; set; }

        [JsonPropertyName("pack_file_index_size")]
        public long PackFileIndexSize { get; set; }

        [JsonPropertyName("pack_file_count")]
        public long PackFileCount { get; set; }

        [JsonPropertyName("header_buffer")]
        public string? HeaderBuffer { get; set; }
    }
}
