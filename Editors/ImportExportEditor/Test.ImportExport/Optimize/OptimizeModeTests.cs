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

    private OptimizeRequest ValidRequest(IReadOnlyList<string>? consumers = null, int? sourceLoadOrderIndex = null)
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
        return request! with { ConsumerPackPaths = consumers, SourceLoadOrderIndex = sourceLoadOrderIndex };
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
        Assert.That(options.ArmyScope, Is.EqualTo(Editors.PackOptimizer.Core.PackOptimizerArmyScope.Subculture));
        Assert.That(options.MergeCompatibleMeshes, Is.False);
        Assert.That(request.CacheDirectory, Is.Null);
    }

    [Test]
    public void ArmyScope_IsReadFromTheOptionsAndRejectsUnknownValues()
    {
        Assert.That(
            new OptimizeOptionsRequest(ArmyScope: "Culture").ToOptimizerOptions().ArmyScope,
            Is.EqualTo(Editors.PackOptimizer.Core.PackOptimizerArmyScope.Culture));
        Assert.That(
            new OptimizeOptionsRequest(ArmyScope: " subculture ").ToOptimizerOptions().ArmyScope,
            Is.EqualTo(Editors.PackOptimizer.Core.PackOptimizerArmyScope.Subculture));
        Assert.Throws<ArgumentException>(() => new OptimizeOptionsRequest(ArmyScope: "faction").ToOptimizerOptions());
        Assert.That(new OptimizeOptionsRequest().ToOptimizerOptions().RemakeEditorLods, Is.False);
        Assert.That(new OptimizeOptionsRequest(RemakeEditorLods: true).ToOptimizerOptions().RemakeEditorLods, Is.True);
        Assert.That(new OptimizeOptionsRequest().ToOptimizerOptions().ReportDiagnostics, Is.True);
        Assert.That(new OptimizeOptionsRequest(ReportDiagnostics: false).ToOptimizerOptions().ReportDiagnostics, Is.False);
        Assert.That(GameWorld.MeshOptimizer.MeshOptSimplifier.GetNativeLibraryError(), Is.Null);
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

        Assert.That(OptimizeRequestValidator.Validate(ValidRequest([existing], 0)), Is.Null);
        Assert.That(OptimizeRequestValidator.Validate(ValidRequest([existing, Path.Combine(_directory, "gone.pack")], 0)), Does.Contain("consumerPackPaths"));
        Assert.That(OptimizeRequestValidator.Validate(ValidRequest()), Is.Null, "no consumers needs no index");
    }

    [Test]
    public void Validate_ChecksDependencyPacks()
    {
        var dependency = Touch("mods/dependency.pack");
        var other = Touch("mods/other.pack");
        var request = ValidRequest([other, dependency], 1);

        Assert.That(OptimizeRequestValidator.Validate(request with { DependencyPackPaths = [dependency] }), Is.Null);
        Assert.That(OptimizeRequestValidator.Validate(request with { DependencyPackPaths = [] }), Is.Null);
        Assert.That(OptimizeRequestValidator.Validate(request with { DependencyPackPaths = [Path.Combine(_directory, "gone.pack")] }),
            Does.Contain("dependencyPackPaths"));
        Assert.That(OptimizeRequestValidator.Validate(request with { DependencyPackPaths = [request.SourcePackPath] }),
            Does.Contain("lists the source pack"));
        Assert.That(OptimizeRequestValidator.Validate(request with { DependencyPackPaths = [Touch("mods/unlisted.pack")] }),
            Does.Contain("is not in consumerPackPaths"));
    }

    [Test]
    public void Validate_RequiresTheSourcePositionWhenThereAreConsumers()
    {
        var first = Touch("mods/first.pack");
        var second = Touch("mods/second.pack");

        Assert.That(OptimizeRequestValidator.Validate(ValidRequest([first, second])), Does.Contain("sourceLoadOrderIndex is required"));
        Assert.That(OptimizeRequestValidator.Validate(ValidRequest([first, second], -1)), Does.Contain("outside 0..2"));
        Assert.That(OptimizeRequestValidator.Validate(ValidRequest([first, second], 3)), Does.Contain("outside 0..2"));
        Assert.That(OptimizeRequestValidator.Validate(ValidRequest([first, second], 2)), Is.Null);
    }

    [Test]
    public void Validate_RejectsTheSourceTheOutputAndDerivedPacksAsConsumers()
    {
        var other = Touch("mods/other.pack");
        var source = Touch("mods/source.pack");
        var derived = Touch("mods/source_atlas - Copy.pack");
        var outputNamed = Touch("elsewhere/result.pack");

        Assert.That(OptimizeRequestValidator.Validate(ValidRequest([other, source], 0)), Does.Contain("source pack"));
        Assert.That(OptimizeRequestValidator.Validate(ValidRequest([other, derived], 0)), Does.Contain("derived"));
        Assert.That(OptimizeRequestValidator.Validate(ValidRequest([other, outputNamed], 0)), Does.Contain("name of the output"));
        Assert.That(OptimizeRequestValidator.Validate(ValidRequest([other, other], 0)), Does.Contain("more than once"));
    }

    // A minimal PFH5 pack: the given (path, content) entries in order, duplicates included.
    private string WriteRawPack(string relativePath, params (string Path, string Content)[] entries)
    {
        using var index = new MemoryStream();
        using var data = new MemoryStream();
        using (var indexWriter = new BinaryWriter(index, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var bytes = System.Text.Encoding.ASCII.GetBytes(content);
                indexWriter.Write((uint)bytes.Length);
                indexWriter.Write((byte)0);
                indexWriter.Write(System.Text.Encoding.ASCII.GetBytes(path));
                indexWriter.Write((byte)0);
                data.Write(bytes);
            }
        }

        var full = Path.Combine(_directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var file = File.Create(full);
        using var writer = new BinaryWriter(file, System.Text.Encoding.ASCII);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("PFH5"));
        writer.Write(3); // mod pack, no flags
        writer.Write(0u);
        writer.Write(0u);
        writer.Write((uint)entries.Length);
        writer.Write((uint)index.Length);
        writer.Write(0u); // timestamp
        writer.Write(index.ToArray());
        writer.Write(data.ToArray());
        return full;
    }

    [Test]
    public void LoadConsumer_SkipsDuplicateFilesWithoutAskingAnyone()
    {
        var pack = WriteRawPack(
            "mods/dupes.pack",
            ("readme.md", "one"),
            ("variantmeshes/notes.txt", "a"),
            ("variantmeshes/notes.txt", "b"),
            ("readme.md", "two"));

        var filtered = HostVanillaContainers.LoadConsumer(pack);
        var all = HostVanillaContainers.LoadConsumer(pack, allFiles: true);

        Assert.That(filtered.GetFileCount(), Is.EqualTo(1), "only the listed types, the duplicate skipped");
        Assert.That(all.GetFileCount(), Is.EqualTo(2));
        Assert.That(System.Text.Encoding.ASCII.GetString(all.FindFile("readme.md")!.DataSource.ReadData()), Is.EqualTo("one"));
    }

    [Test]
    public void Validate_ChecksTheDbSchemaFile()
    {
        var request = ValidRequest();
        var good = Touch("schema/good.json", "{\"definitions\":{\"main_units_tables\":[{\"version\":7,\"fields\":[{\"name\":\"unit\",\"field_type\":\"StringU8\"}]}]}}");
        var corrupt = Touch("schema/corrupt.json", "not a schema");
        var wrongCompressed = Path.Combine(_directory, "schema", "bad.json.zst");
        File.WriteAllBytes(wrongCompressed, [0x28, 0xB5, 0x2F, 0xFD, 1, 2, 3]);

        Assert.That(OptimizeRequestValidator.Validate(request with { DbSchemaPath = good }), Is.Null);
        Assert.That(OptimizeRequestValidator.Validate(request with { DbSchemaPath = null }), Is.Null);
        Assert.That(OptimizeRequestValidator.Validate(request with { DbSchemaPath = corrupt }), Does.Contain("could not be read").And.Contain("corrupt.json"));
        Assert.That(OptimizeRequestValidator.Validate(request with { DbSchemaPath = wrongCompressed }), Does.Contain("could not be read"));
        Assert.That(OptimizeRequestValidator.Validate(request with { DbSchemaPath = Path.Combine(_directory, "nope.json") }), Does.Contain("does not exist"));
    }

    [Test]
    public void Parse_ReadsTheDbSchemaPath()
    {
        var request = OptimizeRequestReader.Parse(
            "{ \"schemaVersion\": 1, \"gameDirectory\": \"g\", \"vanillaFileIndexPath\": \"v\", \"sourcePackPath\": \"s\", \"outputPackPath\": \"o.pack\", \"reportPath\": \"r\", \"options\": {}, \"dbSchemaPath\": \"C:/x/schema_wh3.json.zst\" }",
            out var error);

        Assert.That(error, Is.Null);
        Assert.That(request!.DbSchemaPath, Is.EqualTo("C:/x/schema_wh3.json.zst"));
    }

    [Test]
    public void Parse_ReadsTheSourcePosition()
    {
        var json = """
            { "schemaVersion": 1, "gameDirectory": "g", "vanillaFileIndexPath": "v", "sourcePackPath": "s",
              "outputPackPath": "o.pack", "reportPath": "r", "options": {},
              "consumerPackPaths": ["a.pack", "b.pack"], "sourceLoadOrderIndex": 1 }
            """;
        var request = OptimizeRequestReader.Parse(json, out var error);

        Assert.That(error, Is.Null);
        Assert.That(request!.ConsumerPackPaths, Is.EqualTo(new[] { "a.pack", "b.pack" }));
        Assert.That(request.SourceLoadOrderIndex, Is.EqualTo(1));
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
