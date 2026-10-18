using Shared.Core.PackFiles.Models.Containers;
using Shared.Core.Settings;

namespace Shared.CoreTest.PackFiles.Models.Containers
{
    internal class CachedPackFileContainerTests_BulkRead
    {
        [Test]
        public void BulkRead_LookupsGiveTheSameAnswers_AndTheFileCanBeDeletedAfterTheScope()
        {
            var dbFilePath = Path.Combine(Path.GetTempPath(), "CachedPackFileContainerBulk_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var existing = PackFileContainerTests_TestBase.TestFiles[0].RelativePath;
                var container = CachedPackFileContainer.CreateFromFileList(
                    "TestCache",
                    PackFileContainerTests_TestBase.TestFiles,
                    useInMemoryDb: false,
                    dbFilePath: dbFilePath,
                    systemFilePath: @"c:\game\data",
                    sourcePackFilePath: @"c:\game\data\pack1.pack",
                    gameVersion: GameTypeEnum.Warhammer3);
                var before = (container.ContainsFile(existing), container.FindFile(existing)?.Name, container.ContainsFile("nothing\\here.dds"));

                using (var scope = container.BeginBulkRead())
                using (container.BeginBulkRead())
                {
                    var inside = (container.ContainsFile(existing), container.FindFile(existing)?.Name, container.ContainsFile("nothing\\here.dds"));
                    Assert.That(inside, Is.EqualTo(before));
                    Assert.That(before.Item1, Is.True);
                    Assert.That(before.Item3, Is.False);
                }

                container.Dispose();
                Assert.DoesNotThrow(() => File.Delete(dbFilePath));
                Assert.That(File.Exists(dbFilePath), Is.False);
            }
            finally
            {
                if (File.Exists(dbFilePath))
                    File.Delete(dbFilePath);
            }
        }

        [Test]
        public void BulkRead_DisposingTheScopeTwice_Throws_Nothing()
        {
            var dbFilePath = Path.Combine(Path.GetTempPath(), "CachedPackFileContainerBulk_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using var container = CachedPackFileContainer.CreateFromFileList(
                    "TestCache",
                    PackFileContainerTests_TestBase.TestFiles,
                    useInMemoryDb: false,
                    dbFilePath: dbFilePath);
                var scope = container.BeginBulkRead();
                scope.Dispose();

                Assert.DoesNotThrow(scope.Dispose);
            }
            finally
            {
                if (File.Exists(dbFilePath))
                    File.Delete(dbFilePath);
            }
        }
    }
}
