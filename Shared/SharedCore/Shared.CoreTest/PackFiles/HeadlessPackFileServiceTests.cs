using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Models.Containers;
using Shared.Core.PackFiles.Utility;

namespace Shared.CoreTest.PackFiles;

public sealed class HeadlessPackFileServiceTests
{
    [Test]
    public void Factory_UsesLastExplicitlyAddedContainerAsWinner()
    {
        var service = HeadlessPackFileServiceFactory.Create();
        Assert.That(service, Is.TypeOf<HeadlessPackFileService>());
        var lowPriority = PackFileContainer.CreateReadOnlyPackFile("low-priority");
        lowPriority.AddOrUpdateFile("shared\\file.txt", PackFile.CreateFromBytes("file.txt", [1]));
        var highPriority = PackFileContainer.CreateReadOnlyPackFile("high-priority");
        highPriority.AddOrUpdateFile("shared\\file.txt", PackFile.CreateFromBytes("file.txt", [2]));

        Assert.That(service.AddContainer(lowPriority), Is.Not.Null);
        Assert.That(service.AddContainer(highPriority), Is.Not.Null);

        var winner = service.FindFile("shared/file.txt");

        Assert.That(winner, Is.Not.Null);
        Assert.That(winner!.DataSource.ReadData(), Is.EqualTo([2]));
        Assert.That(service.GetAllPackfileContainers(), Is.EqualTo([lowPriority, highPriority]));
    }

    [Test]
    public void ReverseLookup_UsesOwnerMetadata()
    {
        var container = PackFileContainer.CreateReadOnlyPackFile("test");
        var file = PackFile.CreateFromBytes("foo.txt", [1, 2, 3]);
        container.AddOrUpdateFile(@"folder\foo.txt", file);
        var service = HeadlessPackFileServiceFactory.Create([container]);

        Assert.That(service.GetFullPath(file), Is.EqualTo(@"folder\foo.txt"));
        Assert.That(service.GetFullPath(file, container), Is.EqualTo(@"folder\foo.txt"));
        Assert.That(service.GetPackFileContainer(file), Is.SameAs(container));
    }
}
