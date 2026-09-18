using Shared.Core.PackFiles.Models.Containers;

namespace Shared.CoreTest.PackFiles.Models.Containers
{
    [TestFixture(typeof(CachedPackFileContainer))]
    [TestFixture(typeof(PackFileContainer))]
    [TestFixture(typeof(SystemFolderContainer))]
    internal class PackFileContainerTests_DeleteFolder : PackFileContainerTests_TestBase
    {
        public PackFileContainerTests_DeleteFolder(Type containerType) : base(containerType) { }

        [Test]
        public void DeleteFolder_RemovesFolderTreeOrThrowsOnCached()
        {
            if (IsCachedContainer)
            {
                Assert.Throws<InvalidOperationException>(() => _container.DeleteFolder("models"));
                return;
            }

            var removedFile = _container.FindFile("models\\unit.model")!;
            var unaffectedFile = _container.FindFile("folder\\file.txt")!;

            _container.DeleteFolder("models");
            Assert.That(_container.ContainsFile("models\\unit.model"), Is.False);
            Assert.That(_container.ContainsFile("models\\textures\\diffuse.dds"), Is.False);
            Assert.That(removedFile.Container, Is.Null);
            Assert.That(removedFile.VirtualPath, Is.Null);
            Assert.That(unaffectedFile.Container, Is.SameAs(_container));
        }
    }
}
