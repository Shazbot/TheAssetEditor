using Editors.PackOptimizer.Core;

namespace WH3AssetHost.Optimize;

/// <summary>
/// The request file of <c>WH3AssetHost optimize --request &lt;file.json&gt;</c>. Property names
/// are camelCase in the JSON file.
/// </summary>
/// <param name="SchemaVersion">Must be <see cref="OptimizeRequest.CurrentSchemaVersion"/>.</param>
/// <param name="GameDirectory">The game's data folder (the one holding manifest.txt). The game
/// install folder is accepted too when it has a "data" subfolder.</param>
/// <param name="VanillaFileIndexPath">whmm's vanilla pack file index (vanilla-pack-files-cache.bin).</param>
/// <param name="SourcePackPath">The pack to optimize.</param>
/// <param name="OutputPackPath">Final location of the optimized pack; written to a temp file next to it first.</param>
/// <param name="ReportPath">Final location of the report; written to a temp file next to it first.</param>
/// <param name="CacheDirectory">Optional directory for the optimizer's vanilla consumer cache.</param>
/// <param name="Options">What the optimizer does; omitted fields keep the optimizer's defaults.</param>
/// <param name="ConsumerPackPaths">The other enabled mods, in load order: the first entry loads first
/// and loses a conflict, the last entry wins (the order of the mod list, top to bottom). The source
/// pack itself is not listed. They are read-only; the optimizer never changes a file another mod
/// uses or overrides. Omitted or empty means no other mods.</param>
/// <param name="SourceLoadOrderIndex">How many entries of <paramref name="ConsumerPackPaths"/> load
/// before the source pack (0 = the source loads first, count = the source loads last). Required
/// whenever <paramref name="ConsumerPackPaths"/> is not empty; there is no default.</param>
/// <param name="ProbeOnly">Only report which of <paramref name="ConsumerPackPaths"/> can matter to the
/// result (see <see cref="OptimizeProbeLine"/>); nothing is optimized, <c>outputPackPath</c> and
/// <c>reportPath</c> are not needed, and the vanilla file index is used to find vanilla files a mod
/// replaces that the source reaches.</param>
/// <param name="ProbeSources">With <c>probeOnly</c>: probe several sources in one run. Each entry is the
/// path of a pack that also appears in <paramref name="ConsumerPackPaths"/>, which then lists the whole
/// load order (sources included); for a source the consumers are that list without it, and the entries
/// before it load before it. Every pack is scanned once (the scan is kept in
/// <c>cacheDirectory</c>/probe-scan). <c>sourcePackPath</c> is still required: use the first source.</param>
/// <param name="DbSchemaPath">Optional path of an RPFM schema (the mod manager's bundled
/// <c>schema_wh3.json.zst</c>, or plain JSON). The unit category resolver decodes the game's DB
/// tables with its table layouts first and falls back to the layouts embedded in the host, so a game
/// patch that bumps a table version needs a schema update in the manager, not a new host. An
/// unreadable or corrupt file is an invalid request (exit 2). Omitted: the embedded layouts only.</param>
public sealed record OptimizeRequest(
    int SchemaVersion,
    string GameDirectory,
    string VanillaFileIndexPath,
    string SourcePackPath,
    string OutputPackPath,
    string ReportPath,
    string? CacheDirectory,
    OptimizeOptionsRequest? Options,
    IReadOnlyList<string>? ConsumerPackPaths,
    int? SourceLoadOrderIndex = null,
    string? DbSchemaPath = null,
    bool ProbeOnly = false,
    IReadOnlyList<string>? ProbeSources = null)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// The optimizer options as they appear in the request file. Every field is optional; an omitted
/// field keeps the optimizer's own default. (Declared separately from <see cref="PackOptimizerOptions"/>
/// because the source-generated JSON reader does not run that record's property initializers.)
/// </summary>
public sealed record OptimizeOptionsRequest(
    bool? MergeCompatibleMeshes = null,
    bool? ShareAtlasesAcrossVmds = null,
    bool? OptimizeGeometry = null,
    bool? AtlasAllVmds = null,
    bool? ScoreAllGameUnits = null,
    bool? LodAwareAtlasPlanning = null,
    bool? CrossRigidOnly = null,
    bool? CrossRigidMerges = null,
    bool? BuildAtlases = null,
    bool? DownscaleTextures = null,
    bool? SharedTextureRepack = null,
    bool? CompactTextures = null,
    bool? DropUnreadAlpha = null,
    bool? CompressRigidModels = null,
    string? ArmyScope = null,
    string? SiblingScope = null)
{
    public PackOptimizerOptions ToOptimizerOptions()
    {
        var defaults = new PackOptimizerOptions();
        return new PackOptimizerOptions
        {
            MergeCompatibleMeshes = MergeCompatibleMeshes ?? defaults.MergeCompatibleMeshes,
            ShareAtlasesAcrossVmds = ShareAtlasesAcrossVmds ?? defaults.ShareAtlasesAcrossVmds,
            OptimizeGeometry = OptimizeGeometry ?? defaults.OptimizeGeometry,
            AtlasAllVmds = AtlasAllVmds ?? defaults.AtlasAllVmds,
            ScoreAllGameUnits = ScoreAllGameUnits ?? defaults.ScoreAllGameUnits,
            LodAwareAtlasPlanning = LodAwareAtlasPlanning ?? defaults.LodAwareAtlasPlanning,
            CrossRigidOnly = CrossRigidOnly ?? defaults.CrossRigidOnly,
            CrossRigidMerges = CrossRigidMerges ?? defaults.CrossRigidMerges,
            BuildAtlases = BuildAtlases ?? defaults.BuildAtlases,
            DownscaleTextures = DownscaleTextures ?? defaults.DownscaleTextures,
            SharedTextureRepack = SharedTextureRepack ?? defaults.SharedTextureRepack,
            CompactTextures = CompactTextures ?? defaults.CompactTextures,
            DropUnreadAlpha = DropUnreadAlpha ?? defaults.DropUnreadAlpha,
            CompressRigidModels = CompressRigidModels ?? defaults.CompressRigidModels,
            ArmyScope = ParseArmyScope(ArmyScope, defaults.ArmyScope),
            SiblingScope = ParseSiblingScope(SiblingScope, defaults.SiblingScope),
        };
    }

    private static PackOptimizerSiblingScope ParseSiblingScope(string? value, PackOptimizerSiblingScope fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (Enum.TryParse<PackOptimizerSiblingScope>(value.Trim(), ignoreCase: true, out var parsed) &&
            Enum.IsDefined(parsed))
            return parsed;
        throw new ArgumentException($"Unknown siblingScope '{value}'; expected 'model' or 'pack'.");
    }

    private static PackOptimizerArmyScope ParseArmyScope(string? value, PackOptimizerArmyScope fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (Enum.TryParse<PackOptimizerArmyScope>(value.Trim(), ignoreCase: true, out var parsed) &&
            Enum.IsDefined(parsed))
            return parsed;
        throw new ArgumentException($"Unknown armyScope '{value}'; expected 'subculture' or 'culture'.");
    }
}

/// <summary>Command line and request-file parsing for <c>optimize</c>.</summary>
public static class OptimizeRequestReader
{
    /// <returns>The request file path and optional parent pid, or an error message.</returns>
    public static (string? RequestPath, int? ParentPid, string? Error) ParseArguments(string[] args)
        => ParseArguments(args, "optimize");

    /// <summary>The same command line for another subcommand (<c>check</c>).</summary>
    public static (string? RequestPath, int? ParentPid, string? Error) ParseArguments(string[] args, string command)
    {
        string? requestPath = null;
        int? parentPid = null;
        if (args.Length == 0 || !string.Equals(args[0], command, StringComparison.OrdinalIgnoreCase))
            return (null, null, $"Usage: WH3AssetHost {command} --request <file.json> [--parent-pid <pid>]");

        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--request" when index + 1 < args.Length:
                    requestPath = args[++index];
                    break;
                case "--parent-pid" when index + 1 < args.Length:
                    if (!int.TryParse(args[++index], out var pid) || pid <= 0)
                        return (null, null, "--parent-pid requires a positive integer.");
                    parentPid = pid;
                    break;
                default:
                    return (null, null, $"Unknown or incomplete argument '{args[index]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(requestPath))
            return (null, null, "--request is required.");
        return (requestPath, parentPid, null);
    }

    /// <summary>Parses the request JSON; comments and trailing commas are tolerated.</summary>
    public static OptimizeRequest? Parse(string json, out string? error)
    {
        try
        {
            var request = System.Text.Json.JsonSerializer.Deserialize(json, OptimizeJsonContext.Default.OptimizeRequest);
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

/// <summary>One line on stdout while the optimizer runs.</summary>
public sealed record OptimizeProgressLine(string Type, string Stage, string? Item, int Current, int Total)
{
    public static OptimizeProgressLine From(PackOptimizerProgress progress)
        => new("progress", progress.Stage, progress.Item, progress.Current, progress.Total);
}

/// <summary>The final stdout line of a run that finished or was cancelled.</summary>
public sealed record OptimizeResultLine(
    string Type,
    string Status,
    string OutputPackPath,
    string ReportPath,
    int VmdCount,
    int AtlasedMeshCount,
    int GeneratedTextureCount,
    int RemovedFileCount,
    int SkippedMeshCount,
    double ElapsedSeconds,
    string[]? Warnings = null,
    int ConsumerPackCount = 0,
    int ExternallyUsedFileCount = 0,
    int FrozenFileCount = 0,
    string? HostVersion = null,
    OptimizeConsumerPackLine[]? ConsumerPacks = null,
    int OutputVersion = 0,
    OptimizeSummaryLine? Summary = null);

/// <summary>What a run saved (see <see cref="PackOptimizerSummary"/>); sizes are bytes.</summary>
public sealed record OptimizeSummaryLine(
    long SourceBytes,
    long OutputBytes,
    long TextureBytesSaved,
    double DrawCallsEliminated,
    int MergedModelsGenerated);

/// <summary>A consumer that can matter to a result and why; any non-zero reason makes it relevant.</summary>
public sealed record OptimizeConsumerPackLine(
    string Path,
    bool Relevant,
    OptimizeConsumerReasons Reasons);

public sealed record OptimizeConsumerReasons(
    int FrozenFiles,
    int UsedFiles,
    int DbMentions,
    int UndecodableTables,
    int VanillaOverridesReached);

/// <summary>The only stdout line of <c>optimize</c> with <c>probeOnly</c>.</summary>
public sealed record OptimizeProbeLine(
    string Type,
    string HostVersion,
    OptimizeConsumerPackLine[] Consumers,
    double ElapsedSeconds,
    int OutputVersion = 0,
    int ScanVersion = 0,
    OptimizeSourceProbeLine[]? Sources = null,
    int PacksScanned = 0,
    int ScanCacheHits = 0,
    double ScanSeconds = 0);

/// <summary>One source of a batch probe and its consumers.</summary>
public sealed record OptimizeSourceProbeLine(string SourcePackPath, OptimizeConsumerPackLine[] Consumers);

/// <summary>The final stdout line of a run that failed.</summary>
public sealed record OptimizeErrorLine(string Type, string Message, string ExceptionType);

public enum OptimizeOutcome
{
    Succeeded,
    Failed,
    InvalidRequest,
    Cancelled,
}

public static class OptimizeExitCodes
{
    public const int Succeeded = 0;
    public const int Failed = 1;
    public const int InvalidRequest = 2;
    public const int Cancelled = 3;

    public static int For(OptimizeOutcome outcome) => outcome switch
    {
        OptimizeOutcome.Succeeded => Succeeded,
        OptimizeOutcome.InvalidRequest => InvalidRequest,
        OptimizeOutcome.Cancelled => Cancelled,
        _ => Failed,
    };
}

/// <summary>Request validation: everything that can be checked without running the optimizer.</summary>
public static class OptimizeRequestValidator
{
    /// <returns>An error message, or null when the request is usable.</returns>
    public static string? Validate(OptimizeRequest? request)
    {
        if (request == null)
            return "The request file is empty.";
        if (request.SchemaVersion != OptimizeRequest.CurrentSchemaVersion)
        {
            return $"Unsupported schemaVersion {request.SchemaVersion}; this host reads version " +
                   $"{OptimizeRequest.CurrentSchemaVersion}.";
        }

        foreach (var (name, value) in new[]
                 {
                     ("gameDirectory", request.GameDirectory),
                     ("vanillaFileIndexPath", request.VanillaFileIndexPath),
                     ("sourcePackPath", request.SourcePackPath),
                     ("outputPackPath", request.ProbeOnly ? "-" : request.OutputPackPath),
                     ("reportPath", request.ProbeOnly ? "-" : request.ReportPath),
                 })
        {
            if (string.IsNullOrWhiteSpace(value))
                return $"{name} is required.";
        }

        if (request.Options == null && !request.ProbeOnly)
            return "options is required.";

        if (ResolveDataFolder(request.GameDirectory) == null)
            return $"gameDirectory '{request.GameDirectory}' does not contain a data folder with manifest.txt.";
        if (!File.Exists(request.VanillaFileIndexPath))
            return $"vanillaFileIndexPath '{request.VanillaFileIndexPath}' does not exist.";
        if (!File.Exists(request.SourcePackPath))
            return $"sourcePackPath '{request.SourcePackPath}' does not exist.";

        var source = Path.GetFullPath(request.SourcePackPath);
        var output = request.ProbeOnly ? string.Empty : Path.GetFullPath(request.OutputPackPath);
        var report = request.ProbeOnly ? string.Empty : Path.GetFullPath(request.ReportPath);
        if (!request.ProbeOnly)
        {
            if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase))
                return "outputPackPath must differ from sourcePackPath.";
            if (string.Equals(output, report, StringComparison.OrdinalIgnoreCase))
                return "reportPath must differ from outputPackPath.";
            if (!output.EndsWith(".pack", StringComparison.OrdinalIgnoreCase))
                return "outputPackPath must end with .pack.";
        }

        if (!string.IsNullOrWhiteSpace(request.DbSchemaPath))
        {
            if (!File.Exists(request.DbSchemaPath))
                return $"dbSchemaPath '{request.DbSchemaPath}' does not exist.";
            if (!PackOptimizerDbSchema.TryLoad(request.DbSchemaPath, out _, out var schemaError))
                return schemaError;
        }

        var consumers = request.ConsumerPackPaths ?? [];
        foreach (var consumer in consumers)
        {
            if (string.IsNullOrWhiteSpace(consumer) || !File.Exists(consumer))
                return $"consumerPackPaths entry '{consumer}' does not exist.";
        }

        if (request.ProbeSources is { Count: > 0 })
        {
            if (!request.ProbeOnly)
                return "probeSources requires probeOnly.";
            var loadOrder = consumers.Select(Path.GetFullPath).ToList();
            if (loadOrder.Distinct(StringComparer.OrdinalIgnoreCase).Count() != loadOrder.Count)
                return "consumerPackPaths lists a pack more than once.";
            foreach (var probeSource in request.ProbeSources)
            {
                if (!loadOrder.Contains(Path.GetFullPath(probeSource), StringComparer.OrdinalIgnoreCase))
                    return $"probeSources entry '{probeSource}' is not in consumerPackPaths (the whole load order).";
            }

            return null;
        }

        if (consumers.Count == 0)
            return null;

        if (!request.SourceLoadOrderIndex.HasValue)
            return "sourceLoadOrderIndex is required when consumerPackPaths is not empty.";
        if (request.SourceLoadOrderIndex.Value < 0 || request.SourceLoadOrderIndex.Value > consumers.Count)
        {
            return $"sourceLoadOrderIndex {request.SourceLoadOrderIndex.Value} is outside 0..{consumers.Count} " +
                   "(the number of consumerPackPaths entries that load before the source).";
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var consumer in consumers)
        {
            var full = Path.GetFullPath(consumer);
            if (string.Equals(full, source, StringComparison.OrdinalIgnoreCase))
                return $"consumerPackPaths lists the source pack '{consumer}'; the source is not one of the other mods.";
            if (string.Equals(full, output, StringComparison.OrdinalIgnoreCase))
                return $"consumerPackPaths lists the output pack '{consumer}'.";
            var derived = GetDerivedPackReason(full, source, output);
            if (derived != null)
                return $"consumerPackPaths entry '{consumer}' {derived}; leave derived packs of the source out of the other mods.";
            if (!seen.Add(full))
                return $"consumerPackPaths lists '{consumer}' more than once.";
        }

        return null;
    }

    /// <summary>
    /// Why a pack looks like an output of this source (the output pack itself, or named like
    /// the optimizer names its outputs), else null. Such a pack overrides nearly everything the
    /// source has and would freeze the whole run.
    /// </summary>
    public static string? GetDerivedPackReason(string consumerFullPath, string sourceFullPath, string outputFullPath)
    {
        var name = Path.GetFileNameWithoutExtension(consumerFullPath);
        var sourceStem = Path.GetFileNameWithoutExtension(sourceFullPath);
        if (string.Equals(name, Path.GetFileNameWithoutExtension(outputFullPath), StringComparison.OrdinalIgnoreCase))
            return "has the name of the output pack";
        var optimizerOutput = Path.GetFileNameWithoutExtension(PackOptimizer.BuildOutputPath(sourceFullPath));
        if (name.StartsWith(optimizerOutput, StringComparison.OrdinalIgnoreCase))
            return $"looks like a derived output of the source ('{optimizerOutput}...')";
        return null;
    }

    /// <summary>
    /// The folder holding manifest.txt: the given folder, or its "data" subfolder when the
    /// game install folder was given.
    /// </summary>
    public static string? ResolveDataFolder(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
            return null;

        var full = Path.GetFullPath(gameDirectory);
        if (File.Exists(Path.Combine(full, "manifest.txt")))
            return full;

        var data = Path.Combine(full, "data");
        return File.Exists(Path.Combine(data, "manifest.txt")) ? data : null;
    }
}
