using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Models.Containers;

namespace Shared.CoreTest.PackFiles;

public sealed class PackFileRepositoryTests
{
    [Test]
    public void LookupUsesLastLoadedPackAndPreservesPackOrder()
    {
        var lowPriority = PackFileContainer.CreateReadOnlyPackFile("low-priority");
        lowPriority.AddOrUpdateFile("shared\\file.txt", PackFile.CreateFromBytes("file.txt", [1]));
        var highPriority = PackFileContainer.CreateReadOnlyPackFile("high-priority");
        highPriority.AddOrUpdateFile("shared\\file.txt", PackFile.CreateFromBytes("file.txt", [2]));

        var repository = new PackFileRepository([lowPriority, highPriority]);

        var winner = repository.FindFile("shared/file.txt");

        Assert.That(winner, Is.Not.Null);
        Assert.That(winner!.DataSource.ReadData(), Is.EqualTo([2]));
        Assert.That(repository.Packs, Is.EqualTo([lowPriority, highPriority]));
    }

    [Test]
    public void HeadlessServiceExposesLocationAndExtensionCapabilities()
    {
        var container = PackFileContainer.CreateReadOnlyPackFile("animations");
        container.AddOrUpdateFile("animations\\unit.anim", PackFile.CreateFromBytes("unit.anim", [1]));
        container.AddOrUpdateFile("animations\\unit.txt", PackFile.CreateFromBytes("unit.txt", [2]));

        var service = new HeadlessPackFileService([container]);
        var animation = service.FindFile("ANIMATIONS/UNIT.ANIM");

        Assert.That(animation, Is.Not.Null);
        Assert.That(service.GetFullPath(animation!), Is.EqualTo("animations\\unit.anim"));
        Assert.That(service.GetPackFileContainer(animation), Is.SameAs(container));
        Assert.That(service.FindAllWithExtention(".anim"), Has.Count.EqualTo(1));
    }
}
