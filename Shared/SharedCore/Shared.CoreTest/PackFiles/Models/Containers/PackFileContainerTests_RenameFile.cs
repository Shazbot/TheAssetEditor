using Shared.Core.PackFiles.Models.Containers;

namespace Shared.CoreTest.PackFiles.Models.Containers
{
    [TestFixture(typeof(CachedPackFileContainer))]
    [TestFixture(typeof(PackFileContainer))]
    [TestFixture(typeof(SystemFolderContainer))]
    internal class PackFileContainerTests_RenameFile : PackFileContainerTests_TestBase
    {
        public PackFileContainerTests_RenameFile(Type containerType) : base(containerType) { }

        [Test]
        public void RenameFile_RenamesFileOrThrowsOnCached()
        {
            var file = _container.FindFile("folder\\file.txt")!;

            if (IsCachedContainer)
            {
                Assert.Throws<InvalidOperationException>(() => _container.RenameFile(file, "renamed.txt"));
                return;
            }

            _container.RenameFile(file, "renamed.txt");
            Assert.That(_container.ContainsFile("folder\\renamed.txt"), Is.True);
            Assert.That(_container.ContainsFile("folder\\file.txt"), Is.False);
            Assert.That(file.Name, Is.EqualTo("renamed.txt"));
            Assert.That(file.Container, Is.SameAs(_container));
            Assert.That(file.VirtualPath, Is.EqualTo("folder\\renamed.txt"));
        }
    }
}
