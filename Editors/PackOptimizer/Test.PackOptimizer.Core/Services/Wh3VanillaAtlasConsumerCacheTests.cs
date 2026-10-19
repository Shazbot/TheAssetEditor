using System.IO.Compression;
using System.Text;
using Editors.PackOptimizer.Core;
using Moq;
using Shared.Core.PackFiles.Models;
using Shared.Core.Settings;

namespace Test.PackOptimizer.Core.Services
{
    [TestFixture]
    public class Wh3VanillaAtlasConsumerCacheTests
    {
        private const string CacheFileName = "wh3-atlas-vanilla-consumers.json.gz";

        // The asset host's vanilla containers have no persistent identity (ContainerType.Normal).
        // A run made only of them must leave a cache file written by the editor untouched.
        [Test]
        public void VolatileOnlyRun_DoesNotPruneOrRewritePersistentEntries()
        {
            var directory = Path.Combine(Path.GetTempPath(), "cache-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var cachePath = Path.Combine(directory, CacheFileName);
                var json =
                    "{\"Version\":3,\"ParserVersion\":\"wsmodel-texture-consumers-v3\",\"Game\":\"" +
                    GameTypeEnum.Warhammer3 + "\",\"Containers\":{\"EDITOR-SCOPE\":{\"WsModels\":{},\"Materials\":{},\"Rigids\":{}}}}";
                using (var file = File.Create(cachePath))
                using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                    gzip.Write(Encoding.UTF8.GetBytes(json));
                var before = File.ReadAllBytes(cachePath);

                var container = new Mock<IPackFileContainer>();
                container.SetupGet(value => value.IsCaPackFile).Returns(true);
                container.SetupGet(value => value.ContainerType).Returns(PackFileContainerType.Normal);
                container.SetupGet(value => value.PackFileSettings).Returns(new PackFileSettings());

                var cache = Wh3VanillaAtlasConsumerCache.Load(directory, GameTypeEnum.Warhammer3, [container.Object]);
                Assert.That(cache, Is.Not.Null);
                cache!.Save();

                Assert.That(File.ReadAllBytes(cachePath), Is.EqualTo(before));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
