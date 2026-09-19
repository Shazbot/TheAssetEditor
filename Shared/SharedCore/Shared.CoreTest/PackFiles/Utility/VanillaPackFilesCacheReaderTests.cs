using System.Text;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Models.FileSources;
using Shared.Core.PackFiles.Utility;
using ZstdSharp;

namespace Shared.CoreTest.PackFiles.Utility;

public sealed class VanillaPackFilesCacheReaderTests
{
    [Test]
    public void InvalidCompressedCache_FallsBackToEmptyCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "VanillaPackFilesCacheReaderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cachePath = Path.Combine(root, "vanilla-pack-files-cache.bin");
        var packPath = Path.Combine(root, "release.pack");

        try
        {
            File.WriteAllBytes(cachePath, [1, 2, 3, 4, 5]);

            var reader = new VanillaPackFilesCacheReader(cachePath);

            Assert.That(reader.TryBuildContainer(new FileInfo(packPath)), Is.Null);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void WrongCacheVersion_FallsBackToEmptyCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "VanillaPackFilesCacheReaderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cachePath = Path.Combine(root, "vanilla-pack-files-cache.bin");
        var packPath = Path.Combine(root, "release.pack");

        try
        {
            using (var payload = new MemoryStream())
            {
                using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(Encoding.ASCII.GetBytes("WVFC"));
                    writer.Write(2u);
                    writer.Write(0u);
                }

                using var cacheStream = File.Create(cachePath);
                using var compressor = new CompressionStream(cacheStream, 1);
                compressor.Write(payload.ToArray());
            }

            var reader = new VanillaPackFilesCacheReader(cachePath);

            Assert.That(reader.TryBuildContainer(new FileInfo(packPath)), Is.Null);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

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
                (Name: "audio\\voice.wem", Data: Encoding.ASCII.GetBytes("unused audio"), IsCompressed: false),
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
            var service = HeadlessPackFileServiceFactory.Create(loaded.Select(x => x.Container));
            var rawFile = service.FindFile("folder\\raw.txt");

            Assert.That(loaded, Has.Count.EqualTo(1));
            Assert.That(loaded[0].IsVanillaPack, Is.True);
            Assert.That(loaded[0].Container.GetFileCount(), Is.EqualTo(2));
            Assert.That(loaded[0].Container.FindFile("audio\\voice.wem"), Is.Null);
            Assert.That(
                loaded[0].Container.FindFile("folder\\raw.txt")!.DataSource.ReadData(),
                Is.EqualTo(rawFiles[0].Data));
            Assert.That(
                loaded[0].Container.FindFile("folder\\compressed.txt")!.DataSource.ReadData(),
                Is.EqualTo(rawFiles[2].Data));
            Assert.That(rawFile, Is.Not.Null);
            Assert.That(rawFile!.Container, Is.SameAs(loaded[0].Container));
            Assert.That(rawFile.VirtualPath, Is.EqualTo("folder\\raw.txt"));
            Assert.That(service.GetFullPath(rawFile), Is.EqualTo("folder\\raw.txt"));
            Assert.That(service.GetPackFileContainer(rawFile), Is.SameAs(loaded[0].Container));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CompactCache_AllWemPack_UsesFastPathWithoutMaterializingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "VanillaPackFilesCacheReaderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var packPath = Path.Combine(root, "audio.pack");
        var cachePath = Path.Combine(root, "vanilla-pack-files-cache.bin");

        try
        {
            var files = new[]
            {
                (Name: "audio\\a.wem", Data: Encoding.ASCII.GetBytes("first"), IsCompressed: false),
                (Name: "audio\\b.wem", Data: Encoding.ASCII.GetBytes("second"), IsCompressed: false)
            };
            var index = BuildPack(packPath, files);
            WriteCache(cachePath, packPath, index);

            var reader = new VanillaPackFilesCacheReader(cachePath);
            var built = reader.TryBuildContainer(new FileInfo(packPath));

            Assert.That(built, Is.Not.Null);
            Assert.That(built!.UsedAllWemFastPath, Is.True);
            Assert.That(built.RetainedFileCount, Is.Zero);
            Assert.That(built.SkippedWemCount, Is.EqualTo(2));
            Assert.That(built.Container.GetFileCount(), Is.Zero);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void HeadlessLoader_WithoutCache_SkipsWemBeforeCreatingPackFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "VanillaPackFilesCacheReaderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var packPath = Path.Combine(root, "release.pack");

        try
        {
            var files = new[]
            {
                (Name: "folder\\keep.txt", Data: Encoding.ASCII.GetBytes("keep"), IsCompressed: false),
                (Name: "audio\\skip.wem", Data: Encoding.ASCII.GetBytes("skip"), IsCompressed: false)
            };
            BuildPack(packPath, files);

            var loaded = new HeadlessPackFileLoader().LoadOrderedWithMetadata([packPath]);

            Assert.That(loaded, Has.Count.EqualTo(1));
            Assert.That(loaded[0].Container.GetFileCount(), Is.EqualTo(1));
            Assert.That(loaded[0].Container.FindFile("folder\\keep.txt"), Is.Not.Null);
            Assert.That(loaded[0].Container.FindFile("audio\\skip.wem"), Is.Null);
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
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("WVFC"));
            writer.Write(4u);
            writer.Write(1u);

            writer.Write((byte)1);
            var recordLengthOffset = payload.Position;
            writer.Write(0u);
            var recordStart = payload.Position;

            WriteString(writer, packPath);
            writer.Write((ulong)packInfo.Length);
            writer.Write((packInfo.LastWriteTimeUtc - DateTime.UnixEpoch).TotalMilliseconds);

            writer.Write(4u);
            writer.Write(Encoding.ASCII.GetBytes("PFH5"));
            writer.Write((int)PackFileCAType.RELEASE);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write((uint)index.Files.Count);
            writer.Write(4u);
            writer.Write(new byte[] { 1, 2, 3, 4 });

            writer.Write(0u);
            writer.Write((uint)index.Files.Count);
            writer.Write((uint)index.Files.Count(file => !file.Name.EndsWith(".wem", StringComparison.OrdinalIgnoreCase)));
            writer.Write((ulong)(index.Files.Count == 0 ? 0 : index.Files[0].StartPos));

            var previousName = string.Empty;
            foreach (var file in index.Files)
            {
                var prefixLength = CommonPrefixLength(previousName, file.Name);
                writer.Write((uint)prefixLength);
                WriteString(writer, file.Name[prefixLength..]);
                writer.Write((uint)file.Size);
                writer.Write((byte)(file.IsCompressed ? 1 : 0));
                previousName = file.Name;
            }

            var recordEnd = payload.Position;
            payload.Position = recordLengthOffset;
            writer.Write((uint)(recordEnd - recordStart));
            payload.Position = recordEnd;
        }

        using var cacheStream = File.Create(cachePath);
        using var compressor = new CompressionStream(cacheStream, 1);
        compressor.Write(payload.ToArray());
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
    }

    private static int CommonPrefixLength(string previous, string current)
    {
        var limit = Math.Min(previous.Length, current.Length);
        var prefix = 0;
        while (prefix < limit && previous[prefix] == current[prefix])
            prefix++;

        if (prefix > 0 && char.IsHighSurrogate(previous[prefix - 1]))
            prefix--;

        return prefix;
    }

    private sealed record PackIndex(IReadOnlyList<CachedFile> Files, int IndexSize);

    private sealed record CachedFile(string Name, long Size, long StartPos, bool IsCompressed);
}
