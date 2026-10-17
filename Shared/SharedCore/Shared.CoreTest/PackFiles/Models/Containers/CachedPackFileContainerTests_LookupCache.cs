using Shared.Core.PackFiles.Models.Containers;

namespace Shared.CoreTest.PackFiles.Models.Containers
{
    internal class CachedPackFileContainerTests_LookupCache
    {
        [Test]
        public void RepeatedLookups_HitTheCache_AndEveryFindFileReturnsItsOwnPackFile()
        {
            var dbFilePath = Path.Combine(Path.GetTempPath(), "CachedPackFileContainerLookup_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using var container = CachedPackFileContainer.CreateFromFileList(
                    "TestCache",
                    PackFileContainerTests_TestBase.TestFiles,
                    useInMemoryDb: false,
                    dbFilePath: dbFilePath);
                var existing = PackFileContainerTests_TestBase.TestFiles[0].RelativePath;
                var hitsBefore = PackFileLookupDiagnostics.Hits;

                var first = container.FindFile(existing);
                var second = container.FindFile(existing.ToUpperInvariant());
                var missing1 = container.ContainsFile("nothing\\here.dds");
                var missing2 = container.FindFile("nothing\\here.dds");

                Assert.That(first, Is.Not.Null);
                Assert.That(second, Is.Not.Null.And.Not.SameAs(first));
                Assert.That(second!.Name, Is.EqualTo(first!.Name));
                Assert.That(missing1, Is.False);
                Assert.That(missing2, Is.Null);
                // The second lookup of each path was answered from the cache (also the negative one).
                Assert.That(PackFileLookupDiagnostics.Hits - hitsBefore, Is.EqualTo(2));
            }
            finally
            {
                if (File.Exists(dbFilePath))
                    File.Delete(dbFilePath);
            }
        }
    }
}
