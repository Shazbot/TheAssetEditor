using System.Text;
using Moq;
using Shared.Core.Events;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Utility;
using Shared.Core.Settings;

namespace Shared.CoreTest.PackFiles.Utility
{
    internal class FileCompressionTests
    {
        private IPackFileService _packFileService;
        private IPackFileContainer _container;

        [SetUp]
        public void Setup()
        {
            var eventHub = new Mock<IGlobalEventHub>();
            _packFileService = new PackFileService(eventHub.Object);
            _container = _packFileService.CreateNewPackFileContainer("EncryptedOutput", PackFileVersion.PFH5, PackFileCAType.MOD, true);

            // Use files that are large enough for compression to be effective as files that are too small may actually increase in size when compressed
            List<NewPackFileEntry> files = [
                new("Directory_0", PackFile.CreateFromASCII("file0.txt", new string('A', 1_024))),
                new("Directory_0", PackFile.CreateFromASCII("file1.txt", new string('B', 2_048))),
                new("Directory_0\\subfolder", PackFile.CreateFromASCII("subfile0.txt", new string('C', 4_096))),
                new("Directory_0\\subfolder", PackFile.CreateFromASCII("subfile1.txt", new string('D', 8_192)))
            ];

            _packFileService.AddFilesToPack(_container, files);
        }

        [Test]
        public void TestCompressAndDecompressPackFile()
        {
            var compressionFormats = Enum.GetValues<CompressionFormat>().Where(compressionFormat => compressionFormat != CompressionFormat.None);
            var originals = _container.GetAllFiles().ToDictionary(file => file.Value.Name, file => file.Value.DataSource.ReadData());

            foreach (var fileName in originals.Keys)
            {
                var data = originals[fileName];

                foreach (var compressionFormat in compressionFormats)
                {
                    var compressedData = FileCompression.Compress(data, compressionFormat);
                    var decompressedData = FileCompression.Decompress(compressedData, data.Length, compressionFormat);
                    Assert.That(decompressedData, Has.Length.EqualTo(data.Length), $"[{compressionFormat}] {fileName} length mismatch");

                    var expectedValue = Encoding.UTF8.GetString(originals[fileName]);
                    var actualValue = Encoding.UTF8.GetString(decompressedData);
                    Assert.That(actualValue, Is.EqualTo(expectedValue), $"[{compressionFormat}] {fileName} content mismatch after round-trip");

                    // Feed back in for the next iteration
                    data = decompressedData;
                }
            }
        }

        [Test]
        public void ForcedLz4Precompressed_IsUsedOnlyForMatchingDataAndDropped()
        {
            var data = Encoding.ASCII.GetBytes(new string('Z', 5_000));
            var other = Encoding.ASCII.GetBytes(new string('Y', 5_000));
            const string path = "a\\b.rigid_model_v2";
            var marker = new byte[] { 1, 2, 3 };
            var entry = new FileCompression.PrecompressedLz4(data.Length, System.Security.Cryptography.SHA256.HashData(data), marker);

            FileCompression.ForcedLz4Files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };
            try
            {
                FileCompression.ForcedLz4Precompressed = new() { [path] = entry };
                Assert.That(FileCompression.Compress(data, CompressionFormat.Lz4, path), Is.SameAs(marker));
                Assert.That(FileCompression.Compress(data, CompressionFormat.Lz4, path), Is.Not.SameAs(marker));

                FileCompression.ForcedLz4Precompressed = new() { [path] = entry };
                var recompressed = FileCompression.Compress(other, CompressionFormat.Lz4, path);
                Assert.That(FileCompression.Decompress(recompressed, other.Length, CompressionFormat.Lz4), Is.EqualTo(other));
            }
            finally
            {
                FileCompression.ForcedLz4Files = null;
                FileCompression.ForcedLz4Precompressed = null;
            }
        }
    }
}
