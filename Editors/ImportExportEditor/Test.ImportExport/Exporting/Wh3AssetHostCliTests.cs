using WH3AssetHost;

namespace Test.ImportExport.Exporting;

public sealed class Wh3AssetHostCliTests
{
    [TestCase("--pack")]
    [TestCase("--asset")]
    [TestCase("--output")]
    [TestCase("--animation")]
    public void Parse_RejectsFlagAsMissingValue(string option)
    {
        var request = CliRequest.Parse(["export", option, "--asset", "model.rigid_model_v2", "--output", "model.glb", "--pack", "game.pack"]);

        Assert.That(request.Error, Does.Contain($"{option} requires"));
    }

    [Test]
    public void Parse_PreservesRepeatedPacksAndAnimations()
    {
        var request = CliRequest.Parse([
            "export",
            "--pack", "base.pack",
            "--pack", "mod.pack",
            "--asset", "variantmeshes\\unit.rigid_model_v2",
            "--output", "unit.glb",
            "--animation", "animations\\idle.anim",
            "--animation", "animations\\walk.anim",
            "--no-materials",
            "--no-skeleton",
            "--no-mirror"]);

        Assert.That(request.Error, Is.Null);
        Assert.That(request.PackPaths, Is.EqualTo(["base.pack", "mod.pack"]));
        Assert.That(request.AnimationPaths, Is.EqualTo(["animations\\idle.anim", "animations\\walk.anim"]));
        Assert.That(request.ExportMaterials, Is.False);
        Assert.That(request.IncludeSkeleton, Is.False);
        Assert.That(request.MirrorMesh, Is.False);
    }

    [Test]
    public void ServeParse_RequiresPipeAndParentPid()
    {
        var request = ServeCliRequest.Parse(["serve", "--pipe", "wh3-host", "--parent-pid", "1234"]);
        var missingPipe = ServeCliRequest.Parse(["serve", "--parent-pid", "1234"]);
        var missingParent = ServeCliRequest.Parse(["serve", "--pipe", "wh3-host"]);
        var missingPid = ServeCliRequest.Parse(["serve", "--pipe", "wh3-host", "--parent-pid", "--pipe"]);

        Assert.That(request.Error, Is.Null);
        Assert.That(request.PipeName, Is.EqualTo("wh3-host"));
        Assert.That(request.ParentProcessId, Is.EqualTo(1234));
        Assert.That(missingPipe.Error, Does.Contain("--pipe is required"));
        Assert.That(missingParent.Error, Does.Contain("--parent-pid is required"));
        Assert.That(missingPid.Error, Does.Contain("--parent-pid requires"));
    }
}
