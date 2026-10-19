using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Models.Containers;
using WH3AssetHost.Optimize;

namespace Test.ImportExport.Optimize;

public class OptimizeModeTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "optimize-mode-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private string Touch(string relativePath, string content = "x")
    {
        var path = Path.Combine(_directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private OptimizeRequest ValidRequest(IReadOnlyList<string>? consumers = null)
    {
        Touch("data/manifest.txt", "data.pack\tx\n");
        var json = $$"""
            {
              "schemaVersion": 1,
              "gameDirectory": {{ToJson(Path.Combine(_directory, "data"))}},
              "vanillaFileIndexPath": {{ToJson(Touch("index.bin"))}},
              "sourcePackPath": {{ToJson(Touch("mods/source.pack"))}},
              "outputPackPath": {{ToJson(Path.Combine(_directory, "out", "result.pack"))}},
              "reportPath": {{ToJson(Path.Combine(_directory, "out", "result_report.txt"))}},
              "options": { "optimizeGeometry": true, "buildAtlases": false }
            }
            """;
        var request = OptimizeRequestReader.Parse(json, out var error);
        Assert.That(error, Is.Null);
        return request! with { ConsumerPackPaths = consumers };
    }

    private static string ToJson(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    [Test]
    public void Parse_ReadsCamelCaseFieldsAndOptions()
    {
        var request = ValidRequest();

        Assert.That(request.SchemaVersion, Is.EqualTo(1));
        var options = request.Options!.ToOptimizerOptions();
        Assert.That(options.OptimizeGeometry, Is.True);
        Assert.That(options.BuildAtlases, Is.False);
        // Omitted option fields keep the optimizer's defaults.
        Assert.That(options.ShareAtlasesAcrossVmds, Is.True);
        Assert.That(options.LodAwareAtlasPlanning, Is.True);
        Assert.That(options.MergeCompatibleMeshes, Is.False);
        Assert.That(request.CacheDirectory, Is.Null);
    }

    [Test]
    public void Parse_ToleratesCommentsAndTrailingCommas()
    {
        var request = OptimizeRequestReader.Parse(
            "{ \"schemaVersion\": 1, // note\n \"options\": {},\n }",
            out var error);

        Assert.That(error, Is.Null);
        Assert.That(request!.SchemaVersion, Is.EqualTo(1));
    }

    [Test]
    public void Parse_ReportsMalformedJson()
    {
        var request = OptimizeRequestReader.Parse("{ not json", out var error);

        Assert.That(request, Is.Null);
        Assert.That(error, Does.Contain("not valid"));
    }

    [Test]
    public void Validate_AcceptsACompleteRequest()
        => Assert.That(OptimizeRequestValidator.Validate(ValidRequest()), Is.Null);

    [Test]
    public void Validate_AcceptsTheGameInstallFolderAsWellAsTheDataFolder()
    {
        var request = ValidRequest() with { GameDirectory = _directory };

        Assert.That(OptimizeRequestValidator.Validate(request), Is.Null);
    }

    [Test]
    public void Validate_RejectsNullAndWrongSchemaVersion()
    {
        Assert.That(OptimizeRequestValidator.Validate(null), Is.Not.Null);
        Assert.That(OptimizeRequestValidator.Validate(ValidRequest() with { SchemaVersion = 2 }), Does.Contain("schemaVersion"));
    }

    [TestCase("gameDirectory")]
    [TestCase("vanillaFileIndexPath")]
    [TestCase("sourcePackPath")]
    [TestCase("outputPackPath")]
    [TestCase("reportPath")]
    public void Validate_RequiresEveryPath(string field)
    {
        var request = ValidRequest();
        request = field switch
        {
            "gameDirectory" => request with { GameDirectory = " " },
            "vanillaFileIndexPath" => request with { VanillaFileIndexPath = "" },
            "sourcePackPath" => request with { SourcePackPath = "" },
            "outputPackPath" => request with { OutputPackPath = "" },
            _ => request with { ReportPath = "" },
        };

        Assert.That(OptimizeRequestValidator.Validate(request), Does.Contain(field));
    }

    [Test]
    public void Validate_RequiresOptions()
        => Assert.That(OptimizeRequestValidator.Validate(ValidRequest() with { Options = null }), Does.Contain("options"));

    [Test]
    public void Validate_RejectsMissingFilesAndAGameFolderWithoutManifest()
    {
        Assert.That(OptimizeRequestValidator.Validate(ValidRequest() with { VanillaFileIndexPath = Path.Combine(_directory, "nope.bin") }),
            Does.Contain("vanillaFileIndexPath"));
        Assert.That(OptimizeRequestValidator.Validate(ValidRequest() with { SourcePackPath = Path.Combine(_directory, "nope.pack") }),
            Does.Contain("sourcePackPath"));
        Assert.That(OptimizeRequestValidator.Validate(ValidRequest() with { GameDirectory = Path.Combine(_directory, "mods") }),
            Does.Contain("manifest.txt"));
    }

    [Test]
    public void Validate_RejectsOutputThatWouldOverwriteTheSourceOrBeTheReport()
    {
        var request = ValidRequest();

        Assert.That(OptimizeRequestValidator.Validate(request with { OutputPackPath = request.SourcePackPath }), Does.Contain("differ"));
        Assert.That(OptimizeRequestValidator.Validate(request with { ReportPath = request.OutputPackPath }), Does.Contain("differ"));
        Assert.That(OptimizeRequestValidator.Validate(request with { OutputPackPath = Path.Combine(_directory, "out", "result.bin") }), Does.Contain(".pack"));
    }

    [Test]
    public void Validate_ChecksConsumerPacksExist()
    {
        var existing = Touch("mods/consumer.pack");

        Assert.That(OptimizeRequestValidator.Validate(ValidRequest([existing])), Is.Null);
        Assert.That(OptimizeRequestValidator.Validate(ValidRequest([existing, Path.Combine(_directory, "gone.pack")])), Does.Contain("consumerPackPaths"));
    }

    [Test]
    public void ParseArguments_AcceptsRequestAndParentPid()
    {
        var (path, pid, error) = OptimizeRequestReader.ParseArguments(["optimize", "--request", "r.json", "--parent-pid", "42"]);

        Assert.That(error, Is.Null);
        Assert.That(path, Is.EqualTo("r.json"));
        Assert.That(pid, Is.EqualTo(42));
    }

    [TestCase("optimize")]
    [TestCase("optimize --request")]
    [TestCase("optimize --request r.json --parent-pid 0")]
    [TestCase("optimize --request r.json --bogus")]
    [TestCase("serve --request r.json")]
    public void ParseArguments_RejectsBadArguments(string commandLine)
        => Assert.That(OptimizeRequestReader.ParseArguments(commandLine.Split(' ')).Error, Is.Not.Null);

    [TestCase(OptimizeOutcome.Succeeded, 0)]
    [TestCase(OptimizeOutcome.Failed, 1)]
    [TestCase(OptimizeOutcome.InvalidRequest, 2)]
    [TestCase(OptimizeOutcome.Cancelled, 3)]
    public void ExitCodes_MapOutcomes(OptimizeOutcome outcome, int expected)
        => Assert.That(OptimizeExitCodes.For(outcome), Is.EqualTo(expected));

    [Test]
    public void ReadManifestPackPaths_KeepsManifestOrderAndSkipsMissingAndDuplicatePacks()
    {
        Touch("data/a.pack");
        Touch("data/b.pack");
        Touch("data/manifest.txt", "b.pack\t1\na.pack\t2\nmissing.pack\t3\nB.PACK\t4\nnot a pack\n");
        var warnings = new List<string>();

        var paths = HostVanillaContainers.ReadManifestPackPaths(Path.Combine(_directory, "data"), warnings.Add);

        Assert.That(paths.Select(Path.GetFileName), Is.EqualTo(new[] { "b.pack", "a.pack" }));
        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.That(warnings[0], Does.Contain("missing.pack"));
    }

    [Test]
    public void OrderLikeTheEditor_SortsByLoadOrderThenName()
    {
        IPackFileContainer Make(string name) => NamedContainer(name);
        var late = Make("zeta");
        var earlyB = Make("beta");
        var earlyA = Make("alpha");

        var ordered = HostVanillaContainers.OrderLikeTheEditor(
            [(late, 1), (earlyB, 0), (earlyA, 0)]);

        Assert.That(ordered.Select(container => container.Name), Is.EqualTo(new[] { "alpha", "beta", "zeta" }));
    }

    private static IPackFileContainer NamedContainer(string name)
    {
        var container = PackFileContainer.CreatePackFile(name);
        container.Name = name;
        return container;
    }
}
