using Editors.PackOptimizer.Core;

namespace WH3AssetHost.Optimize;

/// <summary>
/// The request file of <c>WH3AssetHost check --request &lt;file.json&gt;</c>: the visual-asset check of one
/// pack (see <see cref="PackOptimizer.Check"/>). Property names are camelCase in the JSON file. The check
/// reads the pack and the game's packs and writes nothing but the optional report.
/// </summary>
/// <param name="SchemaVersion">Must be <see cref="CheckRequest.CurrentSchemaVersion"/>.</param>
/// <param name="GameDirectory">The game's data folder (the one holding manifest.txt), or the install folder.</param>
/// <param name="VanillaFileIndexPath">whmm's vanilla pack file index (vanilla-pack-files-cache.bin).</param>
/// <param name="SourcePackPath">The pack to check.</param>
/// <param name="ReportPath">Optional. Where the text report is written (to a temp file next to it first).</param>
/// <param name="CacheDirectory">Optional directory for the vanilla consumer cache (shared with the optimizer).</param>
/// <param name="DbSchemaPath">Optional RPFM schema (as for <c>optimize</c>). With it the unit-category resolver finds
/// which variant meshes the game data (the pack's own DB plus the game's) reaches, every finding gets
/// <c>usedByGame</c>, and findings reachable only from unused variant meshes are one level less serious.
/// Without it <c>usedByGame</c> is null.</param>
/// <param name="DependencyPackPaths">Optional other enabled mods, in load order. A reference one of them provides is
/// not reported as missing.</param>
public sealed record CheckRequest(
    int SchemaVersion,
    string GameDirectory,
    string VanillaFileIndexPath,
    string SourcePackPath,
    string? ReportPath = null,
    string? CacheDirectory = null,
    string? DbSchemaPath = null,
    IReadOnlyList<string>? DependencyPackPaths = null)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>One finding on the <c>check</c> result line. <c>severity</c> is "error", "warning" or "info".</summary>
public sealed record CheckFindingLine(
    string Severity,
    string Code,
    string Category,
    string Message,
    string Path,
    string[] RelatedPaths,
    int RelatedCount,
    string? Details,
    bool? UsedByGame = null);

public sealed record CheckSummaryLine(
    int Errors,
    int Warnings,
    int Infos,
    Dictionary<string, int> ByCode,
    Dictionary<string, int> ByCategory,
    int FindingsUsed = 0,
    int FindingsUnused = 0,
    int FindingsUnknown = 0,
    bool UsageKnown = false,
    int VmdsUsed = 0,
    int VmdsUnused = 0);

/// <summary>
/// The final stdout line of <c>check</c> when it finished or was cancelled
/// (<c>{"type":"check","status":"succeeded"|"cancelled",...}</c>). A failed run ends with an error line.
/// </summary>
public sealed record CheckResultLine(
    string Type,
    string Status,
    string HostVersion,
    CheckFindingLine[] Findings,
    CheckSummaryLine Summary,
    string ReportPath,
    double ElapsedSeconds,
    int VmdCount,
    int WsModelCount,
    int RigidCount,
    int MaterialCount)
{
    public static CheckResultLine From(PackOptimizerCheckResult result, string reportPath, double elapsedSeconds)
        => new(
            "check",
            result.Status == PackOptimizerStatus.Cancelled ? "cancelled" : "succeeded",
            AssetHostProtocol.HostVersion,
            result.Findings.Select(finding => new CheckFindingLine(
                PackOptimizerCheck.SeverityName(finding.Severity),
                finding.Code,
                finding.Category,
                finding.Message,
                finding.Path,
                finding.RelatedPaths.ToArray(),
                finding.RelatedCount,
                finding.Details,
                finding.UsedByGame)).ToArray(),
            new CheckSummaryLine(
                result.Summary.Errors,
                result.Summary.Warnings,
                result.Summary.Infos,
                new Dictionary<string, int>(result.Summary.ByCode, StringComparer.Ordinal),
                new Dictionary<string, int>(result.Summary.ByCategory, StringComparer.Ordinal),
                result.Summary.FindingsUsed,
                result.Summary.FindingsUnused,
                result.Summary.FindingsUnknown,
                result.Summary.UsageKnown,
                result.Summary.VmdsUsed,
                result.Summary.VmdsUnused),
            reportPath,
            elapsedSeconds,
            result.VmdCount,
            result.WsModelCount,
            result.RigidCount,
            result.MaterialCount);
}

public static class CheckRequestReader
{
    /// <summary>The result line exactly as <c>check</c> writes it to stdout (source-generated, trim safe).</summary>
    public static string ToJson(CheckResultLine line)
        => System.Text.Json.JsonSerializer.Serialize(line, OptimizeJsonContext.Default.CheckResultLine);

    /// <summary>Parses the request JSON; comments and trailing commas are tolerated.</summary>
    public static CheckRequest? Parse(string json, out string? error)
    {
        try
        {
            var request = System.Text.Json.JsonSerializer.Deserialize(json, OptimizeJsonContext.Default.CheckRequest);
            error = request == null ? "The request file is empty." : null;
            return request;
        }
        catch (System.Text.Json.JsonException exception)
        {
            error = $"The request file is not valid: {exception.Message}";
            return null;
        }
    }
}

/// <summary>Request validation for <c>check</c>: everything that can be checked without running it.</summary>
public static class CheckRequestValidator
{
    /// <returns>An error message, or null when the request is usable.</returns>
    public static string? Validate(CheckRequest? request)
    {
        if (request == null)
            return "The request file is empty.";
        if (request.SchemaVersion != CheckRequest.CurrentSchemaVersion)
        {
            return $"Unsupported schemaVersion {request.SchemaVersion}; this host reads version " +
                   $"{CheckRequest.CurrentSchemaVersion}.";
        }

        foreach (var (name, value) in new[]
                 {
                     ("gameDirectory", request.GameDirectory),
                     ("vanillaFileIndexPath", request.VanillaFileIndexPath),
                     ("sourcePackPath", request.SourcePackPath),
                 })
        {
            if (string.IsNullOrWhiteSpace(value))
                return $"{name} is required.";
        }

        if (OptimizeRequestValidator.ResolveDataFolder(request.GameDirectory) == null)
            return $"gameDirectory '{request.GameDirectory}' does not contain a data folder with manifest.txt.";
        if (!File.Exists(request.VanillaFileIndexPath))
            return $"vanillaFileIndexPath '{request.VanillaFileIndexPath}' does not exist.";
        if (!File.Exists(request.SourcePackPath))
            return $"sourcePackPath '{request.SourcePackPath}' does not exist.";

        if (!string.IsNullOrWhiteSpace(request.ReportPath))
        {
            var report = Path.GetFullPath(request.ReportPath);
            if (string.Equals(report, Path.GetFullPath(request.SourcePackPath), StringComparison.OrdinalIgnoreCase))
                return "reportPath must differ from sourcePackPath.";
            if (report.EndsWith(".pack", StringComparison.OrdinalIgnoreCase))
                return "reportPath must not be a .pack file.";
        }

        if (!string.IsNullOrWhiteSpace(request.DbSchemaPath))
        {
            if (!File.Exists(request.DbSchemaPath))
                return $"dbSchemaPath '{request.DbSchemaPath}' does not exist.";
            if (!PackOptimizerDbSchema.TryLoad(request.DbSchemaPath, out _, out var schemaError))
                return schemaError;
        }

        var source = Path.GetFullPath(request.SourcePackPath);
        foreach (var dependency in request.DependencyPackPaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(dependency) || !File.Exists(dependency))
                return $"dependencyPackPaths entry '{dependency}' does not exist.";
            if (string.Equals(Path.GetFullPath(dependency), source, StringComparison.OrdinalIgnoreCase))
                return $"dependencyPackPaths lists the source pack '{dependency}'.";
        }

        return null;
    }
}
