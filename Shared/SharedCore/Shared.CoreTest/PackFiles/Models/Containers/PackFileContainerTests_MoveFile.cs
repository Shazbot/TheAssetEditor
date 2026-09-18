using Shared.Core.PackFiles.Models.Containers;

namespace Shared.CoreTest.PackFiles.Models.Containers
{
    [TestFixture(typeof(CachedPackFileContainer))]
    [TestFixture(typeof(PackFileContainer))]
    [TestFixture(typeof(SystemFolderContainer))]
    internal class PackFileContainerTests_MoveFile : PackFileContainerTests_TestBase
    {
        public PackFileContainerTests_MoveFile(Type containerType) : base(containerType) { }

        [Test]
        public void MoveFile_MovesFileOrThrowsOnCached()
        {
            var file = _container.FindFile("folder\\file.txt")!;

            if (IsCachedContainer)
            {
                Assert.Throws<InvalidOperationException>(() => _container.MoveFile(file, "other"));
                return;
            }

            _container.MoveFile(file, "other");
            Assert.That(_container.ContainsFile("other\\file.txt"), Is.True);
            Assert.That(_container.ContainsFile("folder\\file.txt"), Is.False);
            Assert.That(file.Container, Is.SameAs(_container));
            Assert.That(file.VirtualPath, Is.EqualTo("other\\file.txt"));
            Assert.That(_container.GetFullPath(file), Is.EqualTo("other\\file.txt"));
        }

        [Test]
        public void MoveFile_ToRoot_UsesRootRelativePathOrThrowsOnCached()
        {
            var file = _container.FindFile("folder\\file.txt")!;

            if (IsCachedContainer)
            {
                Assert.Throws<InvalidOperationException>(() => _container.MoveFile(file, string.Empty));
                return;
            }

            _container.MoveFile(file, string.Empty);

            Assert.That(_container.ContainsFile("file.txt"), Is.True);
            Assert.That(_container.ContainsFile("\\file.txt"), Is.False);
            Assert.That(_container.ContainsFile("folder\\file.txt"), Is.False);
        }
    }
}
