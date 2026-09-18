using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Models.Containers;
using Shared.Core.PackFiles.Models.FileSources;

namespace Shared.CoreTest.PackFiles.Models.Containers
{
    internal class PackFileContainerTests_Ownership
    {
        [Test]
        public void AddOrUpdateFile_RejectsAFileOwnedByAnotherContainer()
        {
            var first = PackFileContainer.CreatePackFile("first");
            var second = PackFileContainer.CreatePackFile("second");
            var file = new PackFile("file.txt", new MemorySource([1]));
            var existingSecondFile = new PackFile("file.txt", new MemorySource([2]));

            first.AddOrUpdateFile("file.txt", file);
            second.AddOrUpdateFile("file.txt", existingSecondFile);

            Assert.Throws<InvalidOperationException>(() => second.AddOrUpdateFile("file.txt", file));
            Assert.That(file.Container, Is.SameAs(first));
            Assert.That(file.VirtualPath, Is.EqualTo("file.txt"));
            Assert.That(second.FindFile("file.txt"), Is.SameAs(existingSecondFile));
            Assert.That(existingSecondFile.Container, Is.SameAs(second));
        }

        [Test]
        public void MergePackFileContainer_ClonesFilesAndPreservesSourceOwnership()
        {
            var source = PackFileContainer.CreatePackFile("source");
            var target = PackFileContainer.CreatePackFile("target");
            var sourceFile = new PackFile("file.txt", new MemorySource([1, 2, 3]));
            source.AddOrUpdateFile("folder\\file.txt", sourceFile);

            target.MergePackFileContainer(source);

            var targetFile = target.FindFile("folder\\file.txt")!;
            Assert.That(targetFile, Is.Not.SameAs(sourceFile));
            Assert.That(sourceFile.Container, Is.SameAs(source));
            Assert.That(sourceFile.VirtualPath, Is.EqualTo("folder\\file.txt"));
            Assert.That(targetFile.Container, Is.SameAs(target));
            Assert.That(targetFile.VirtualPath, Is.EqualTo("folder\\file.txt"));
        }
    }
}
