using System.Text.Json;
using Editors.PackOptimizer.Core;
using WH3AssetHost.Optimize;

namespace Test.ImportExport.Optimize;

public class CheckModeTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "check-mode-tests-" + Guid.NewGuid().ToString("N"));
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

    private static string ToJson(string value) => JsonSerializer.Serialize(value);

    private CheckRequest ValidRequest(string? reportPath = null)
    {
        Touch("data/manifest.txt", "data.pack\tx\n");
        var json = $$"""
            {
              "schemaVersion": 1,
              "gameDirectory": {{ToJson(Path.Combine(_directory, "data"))}},
              "vanillaFileIndexPath": {{ToJson(Touch("index.bin"))}},
              "sourcePackPath": {{ToJson(Touch("mods/source.pack"))}}
              {{(reportPath == null ? string.Empty : $", \"reportPath\": {ToJson(reportPath)}")}}
            }
            """;
        var request = CheckRequestReader.Parse(json, out var error);
        Assert.That(error, Is.Null);
        return request!;
    }

    [Test]
    public void Parse_ReadsCamelCaseFields_AndOptionalOnesDefaultToNull()
    {
        var request = ValidRequest();

        Assert.That(request.SchemaVersion, Is.EqualTo(CheckRequest.CurrentSchemaVersion));
        Assert.That(request.SourcePackPath, Does.EndWith("source.pack"));
        Assert.That(request.ReportPath, Is.Null);
        Assert.That(request.CacheDirectory, Is.Null);
        Assert.That(CheckRequestValidator.Validate(request), Is.Null);
    }

    [Test]
    public void Parse_ToleratesCommentsTrailingCommasAndReportsBadJson()
    {
        var ok = CheckRequestReader.Parse(
            "{ /* c */ \"schemaVersion\": 1, \"gameDirectory\": \"g\", \"vanillaFileIndexPath\": \"v\", \"sourcePackPath\": \"s\", }",
            out var okError);
        var bad = CheckRequestReader.Parse("{ not json", out var badError);

        Assert.That(okError, Is.Null);
        Assert.That(ok!.GameDirectory, Is.EqualTo("g"));
        Assert.That(bad, Is.Null);
        Assert.That(badError, Does.StartWith("The request file is not valid"));
    }

    [Test]
    public void ParseArguments_AcceptsCheckAndRejectsOtherCommands()
    {
        var (path, pid, error) = OptimizeRequestReader.ParseArguments(["check", "--request", "r.json", "--parent-pid", "42"], "check");
        var wrong = OptimizeRequestReader.ParseArguments(["optimize", "--request", "r.json"], "check");
        var missing = OptimizeRequestReader.ParseArguments(["check"], "check");

        Assert.That(error, Is.Null);
        Assert.That(path, Is.EqualTo("r.json"));
        Assert.That(pid, Is.EqualTo(42));
        Assert.That(wrong.Error, Does.StartWith("Usage: WH3AssetHost check"));
        Assert.That(missing.Error, Does.Contain("--request is required"));
    }

    [Test]
    public void Validate_RejectsUnusableRequests()
    {
        var request = ValidRequest();

        Assert.That(CheckRequestValidator.Validate(null), Does.Contain("empty"));
        Assert.That(CheckRequestValidator.Validate(request with { SchemaVersion = 9 }), Does.Contain("Unsupported schemaVersion 9"));
        Assert.That(CheckRequestValidator.Validate(request with { SourcePackPath = " " }), Does.Contain("sourcePackPath is required"));
        Assert.That(CheckRequestValidator.Validate(request with { SourcePackPath = Path.Combine(_directory, "nope.pack") }), Does.Contain("does not exist"));
        Assert.That(CheckRequestValidator.Validate(request with { VanillaFileIndexPath = Path.Combine(_directory, "nope.bin") }), Does.Contain("vanillaFileIndexPath"));
        Assert.That(CheckRequestValidator.Validate(request with { GameDirectory = Path.Combine(_directory, "mods") }), Does.Contain("manifest.txt"));
        Assert.That(CheckRequestValidator.Validate(request with { ReportPath = request.SourcePackPath }), Does.Contain("must differ"));
        Assert.That(CheckRequestValidator.Validate(request with { ReportPath = Path.Combine(_directory, "report.pack") }), Does.Contain("must not be a .pack"));
        Assert.That(CheckRequestValidator.Validate(request with { ReportPath = Path.Combine(_directory, "out", "report.txt") }), Is.Null);
        Assert.That(CheckRequestValidator.Validate(request with { DbSchemaPath = Path.Combine(_directory, "no.zst") }), Does.Contain("dbSchemaPath"));
        Assert.That(CheckRequestValidator.Validate(request with { DependencyPackPaths = [Path.Combine(_directory, "no.pack")] }), Does.Contain("dependencyPackPaths"));
        Assert.That(CheckRequestValidator.Validate(request with { DependencyPackPaths = [request.SourcePackPath] }), Does.Contain("lists the source pack"));
        Assert.That(CheckRequestValidator.Validate(request with { DependencyPackPaths = [Touch("mods/dep.pack")] }), Is.Null);
    }

    [Test]
    public void ResultLine_SerializesTheDocumentedShape()
    {
        var findings = new[]
        {
            new PackCheckFinding(
                PackCheckSeverity.Error, PackCheckCodes.MissingTexture, PackCheckCategories.Textures,
                "msg", "materials\\a.xml.material", ["tex\\a.dds"], 3, "slot -> tex\\a.dds", true),
            new PackCheckFinding(
                PackCheckSeverity.Info, PackCheckCodes.DuplicateTexture, PackCheckCategories.Textures,
                "dup", "tex\\b.dds", [], 0),
        };
        var summary = PackOptimizerCheck.Summarize(findings, true, 4, 1);
        var result = new PackOptimizerCheckResult(
            PackOptimizerStatus.Succeeded, findings, summary, "text", 7, 6, 5, 4, TimeSpan.FromSeconds(2));

        var json = CheckRequestReader.ToJson(CheckResultLine.From(result, "C:\\reports\\r.txt", 2.5));

        Assert.That(json, Does.Not.Contain("\n"), "one JSON object per stdout line");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.That(root.GetProperty("type").GetString(), Is.EqualTo("check"));
        Assert.That(root.GetProperty("status").GetString(), Is.EqualTo("succeeded"));
        Assert.That(root.GetProperty("reportPath").GetString(), Is.EqualTo("C:\\reports\\r.txt"));
        Assert.That(root.GetProperty("elapsedSeconds").GetDouble(), Is.EqualTo(2.5));
        Assert.That(root.GetProperty("vmdCount").GetInt32(), Is.EqualTo(7));
        Assert.That(root.GetProperty("hostVersion").GetString(), Is.Not.Empty);
        var summaryJson = root.GetProperty("summary");
        Assert.That(summaryJson.GetProperty("errors").GetInt32(), Is.EqualTo(1));
        Assert.That(summaryJson.GetProperty("infos").GetInt32(), Is.EqualTo(1));
        Assert.That(summaryJson.GetProperty("byCode").GetProperty("MissingTexture").GetInt32(), Is.EqualTo(1));
        Assert.That(summaryJson.GetProperty("byCategory").GetProperty("Textures").GetInt32(), Is.EqualTo(2));
        var first = root.GetProperty("findings")[0];
        Assert.That(first.GetProperty("severity").GetString(), Is.EqualTo("error"));
        Assert.That(first.GetProperty("code").GetString(), Is.EqualTo("MissingTexture"));
        Assert.That(first.GetProperty("category").GetString(), Is.EqualTo("Textures"));
        Assert.That(first.GetProperty("path").GetString(), Is.EqualTo("materials\\a.xml.material"));
        Assert.That(first.GetProperty("relatedPaths")[0].GetString(), Is.EqualTo("tex\\a.dds"));
        Assert.That(first.GetProperty("relatedCount").GetInt32(), Is.EqualTo(3));
        Assert.That(first.GetProperty("details").GetString(), Is.EqualTo("slot -> tex\\a.dds"));
        Assert.That(first.GetProperty("usedByGame").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("findings")[1].GetProperty("usedByGame").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(summaryJson.GetProperty("usageKnown").GetBoolean(), Is.True);
        Assert.That(summaryJson.GetProperty("findingsUsed").GetInt32(), Is.EqualTo(1));
        Assert.That(summaryJson.GetProperty("findingsUnknown").GetInt32(), Is.EqualTo(1));
        Assert.That(summaryJson.GetProperty("vmdsUnused").GetInt32(), Is.EqualTo(1));
        Assert.That(root.GetProperty("findings")[1].GetProperty("severity").GetString(), Is.EqualTo("info"));
    }

    [Test]
    public void ResultLine_ReportsCancellation()
    {
        var result = new PackOptimizerCheckResult(
            PackOptimizerStatus.Cancelled, [], PackOptimizerCheck.Summarize([]), string.Empty, 0, 0, 0, 0, TimeSpan.Zero);

        using var document = JsonDocument.Parse(CheckRequestReader.ToJson(CheckResultLine.From(result, string.Empty, 0.1)));

        Assert.That(document.RootElement.GetProperty("status").GetString(), Is.EqualTo("cancelled"));
        Assert.That(document.RootElement.GetProperty("findings").GetArrayLength(), Is.Zero);
    }
}
