using Shared.Core.PackFiles.Models.Containers;

namespace Shared.CoreTest.PackFiles.Models.Containers
{
    [TestFixture(typeof(CachedPackFileContainer))]
    [TestFixture(typeof(PackFileContainer))]
    [TestFixture(typeof(SystemFolderContainer))]
    internal class PackFileContainerTests_RenameDirectory : PackFileContainerTests_TestBase
    {
        public PackFileContainerTests_RenameDirectory(Type containerType) : base(containerType) { }

        [Test]
        public void RenameDirectory_RenamesNodeOrThrowsOnCached()
        {
            if (IsCachedContainer)
            {
                Assert.Throws<InvalidOperationException>(() => _container.RenameDirectory("models", "newmodels"));
                return;
            }

            var file = _container.FindFile("models\\unit.model")!;
            var newPath = _container.RenameDirectory("models", "newmodels");
            Assert.That(newPath, Is.EqualTo("newmodels"));
            Assert.That(_container.ContainsFile("newmodels\\unit.model"), Is.True);
            Assert.That(file.Container, Is.SameAs(_container));
            Assert.That(file.VirtualPath, Is.EqualTo("newmodels\\unit.model"));
            Assert.That(_container.GetFullPath(file), Is.EqualTo("newmodels\\unit.model"));
        }
    }
}
