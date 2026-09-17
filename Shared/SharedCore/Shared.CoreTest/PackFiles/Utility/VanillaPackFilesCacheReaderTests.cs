using System.Text;
using System.Text.Json;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Models.FileSources;
using Shared.Core.PackFiles.Utility;
using ZstdSharp;

namespace Shared.CoreTest.PackFiles.Utility;

public sealed class VanillaPackFilesCacheReaderTests
{
    [Test]
    public void HeadlessLoader_UsesExpandedCacheAndReadsCompressedPayloads()
    {
        var root = Path.Combine(Path.GetTempPath(), "VanillaPackFilesCacheReaderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var packPath = Path.Combine(root, "release.pack");
        var cachePath = Path.Combine(root, "vanilla-pack-files-cache.bin");

        try
        {
            var rawFiles = new[]
            {
                (Name: "folder\\raw.txt", Data: Encoding.ASCII.GetBytes("raw payload"), IsCompressed: false),
                (Name: "folder\\compressed.txt", Data: Encoding.ASCII.GetBytes(new string('C', 256)), IsCompressed: true)
            };
            var storedFiles = rawFiles
                .Select(file => file.IsCompressed
                    ? (file.Name, Data: FileCompression.Compress(file.Data, CompressionFormat.Zstd), file.IsCompressed)
                    : file)
                .ToArray();
            var index = BuildPack(packPath, storedFiles);
            WriteCache(cachePath, packPath, index);

            var loaded = new HeadlessPackFileLoader(cachePath).LoadOrderedWithMetadata([packPath]);

            Assert.That(loaded, Has.Count.EqualTo(1));
            Assert.That(loaded[0].IsVanillaPack, Is.True);
            Assert.That(loaded[0].Container.GetFileCount(), Is.EqualTo(2));
            Assert.That(
                loaded[0].Container.FindFile("folder\\raw.txt")!.DataSource.ReadData(),
                Is.EqualTo(rawFiles[0].Data));
            Assert.That(
                loaded[0].Container.FindFile("folder\\compressed.txt")!.DataSource.ReadData(),
                Is.EqualTo(rawFiles[1].Data));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static PackIndex BuildPack(
        string packPath,
        IReadOnlyList<(string Name, byte[] Data, bool IsCompressed)> files)
    {
        using var indexStream = new MemoryStream();
        using (var indexWriter = new BinaryWriter(indexStream, Encoding.ASCII, leaveOpen: true))
        {
            foreach (var file in files)
            {
                indexWriter.Write((uint)file.Data.Length);
                indexWriter.Write(file.IsCompressed);
                indexWriter.Write(Encoding.ASCII.GetBytes(file.Name));
                indexWriter.Write((byte)0);
            }
        }

        var indexBytes = indexStream.ToArray();
        var dataStart = 24L + 4 + indexBytes.Length;
        var packedFiles = new List<CachedFile>(files.Count);
        using (var packStream = File.Create(packPath))
        using (var writer = new BinaryWriter(packStream, Encoding.ASCII, leaveOpen: false))
        {
            writer.Write(Encoding.ASCII.GetBytes("PFH5"));
            writer.Write((int)PackFileCAType.RELEASE);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write((uint)files.Count);
            writer.Write((uint)indexBytes.Length);
            writer.Write(new byte[] { 1, 2, 3, 4 });
            writer.Write(indexBytes);

            var fileOffset = dataStart;
            foreach (var file in files)
            {
                writer.Write(file.Data);
                packedFiles.Add(new CachedFile(file.Name, file.Data.Length, fileOffset, file.IsCompressed));
                fileOffset += file.Data.Length;
            }
        }

        return new PackIndex(packedFiles, indexBytes.Length);
    }

    private static void WriteCache(string cachePath, string packPath, PackIndex index)
    {
        var packInfo = new FileInfo(packPath);
        var document = new
        {
            version = 2,
            entries = new Dictionary<string, object>
            {
                [packPath] = new
                {
                    size = packInfo.Length,
                    lastChangedLocal = (packInfo.LastWriteTimeUtc - DateTime.UnixEpoch).TotalMilliseconds,
                    packedFiles = index.Files.Select(file => new
                    {
                        name = file.Name,
                        file_size = file.Size,
                        start_pos = file.StartPos,
                        is_compressed = file.IsCompressed
                    }),
                    packHeader = new
                    {
                        header = Convert.ToBase64String(Encoding.ASCII.GetBytes("PFH5")),
                        byteMask = (int)PackFileCAType.RELEASE,
                        refFileCount = 0,
                        pack_file_index_size = 0,
                        pack_file_count = index.Files.Count,
                        header_buffer = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 })
                    },
                    dependencyPacks = Array.Empty<string>()
                }
            }
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(document);
        using var cacheStream = File.Create(cachePath);
        using var compressor = new CompressionStream(cacheStream, 1);
        compressor.Write(json);
    }

    private sealed record PackIndex(IReadOnlyList<CachedFile> Files, int IndexSize);

    private sealed record CachedFile(string Name, long Size, long StartPos, bool IsCompressed);
}
