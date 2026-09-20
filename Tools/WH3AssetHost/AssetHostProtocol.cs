using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using Editors.ImportExport.Exporting.Exporters.RmvToGltf;
using Serilog;

namespace WH3AssetHost;

public sealed record AssetHostExportRequest(
    string AssetPath,
    string OutputPath,
    IReadOnlyList<string> AnimationPaths,
    bool ExportMaterials = true,
    bool IncludeSkeleton = true,
    bool MirrorMesh = true,
    IReadOnlyList<AssetHostVariantMeshSelection>? VariantSelections = null);

public sealed record AssetHostVariantMeshSelection(string SlotPath, int ChoiceIndex);

public sealed record AssetHostBatchExportItem(
    string OutputPath,
    IReadOnlyList<AssetHostVariantMeshSelection> VariantSelections);

public sealed record AssetHostBatchExportRequest(
    string AssetPath,
    IReadOnlyList<AssetHostBatchExportItem> Items,
    IReadOnlyList<string> AnimationPaths,
    bool ExportMaterials = true,
    bool IncludeSkeleton = true,
    bool MirrorMesh = true);

public sealed record AssetHostBatchExportResult(IReadOnlyList<ExportResult> Exports);

public sealed record AssetHostPaintedTextureInput(
    string SourceVirtualPath,
    string RgbaPath,
    int Width,
    int Height);

public sealed record AssetHostPaintedVariantRequest(
    string AssetPath,
    string OutputDirectory,
    string VariantName,
    IReadOnlyList<AssetHostPaintedTextureInput> Textures,
    IReadOnlyList<AssetHostVariantMeshSelection>? VariantSelections = null);

public sealed record AssetHostPaintedVariantResult(
    bool Success,
    string? VariantMeshVirtualPath,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<AssetHostError> Errors);

public sealed record AssetHostAnimationReference(string Path);

public sealed record AssetHostAnimationCatalog(
    bool Success,
    string AssetPath,
    string? SkeletonName,
    bool HasSkeletonFile,
    IReadOnlyList<AssetHostAnimationReference> Animations,
    IReadOnlyList<string> Diagnostics);

public sealed record AssetHostHelloResult(
    string HostVersion,
    int ProtocolVersion,
    IReadOnlyList<string> Capabilities,
    int MaxFrameBytes);

public sealed record AssetHostInitializationResult(
    string OutputRoot,
    IReadOnlyList<string> PackPaths);

public sealed record AssetHostShutdownResult(bool ShuttingDown);

public sealed record AssetHostMissingSkeletonDecisionRequest(
    int ProtocolVersion,
    string RequestId,
    string Command,
    string DecisionType,
    string SkeletonName,
    string? Message);

public interface IAssetHostRuntime : IDisposable
{
    ExportResult ExportModel(AssetHostExportRequest request);

    AssetHostBatchExportResult ExportModels(AssetHostBatchExportRequest request)
        => new(
            request.Items
                .Select(item => ExportModel(new AssetHostExportRequest(
                    request.AssetPath,
                    item.OutputPath,
                    request.AnimationPaths,
                    request.ExportMaterials,
                    request.IncludeSkeleton,
                    request.MirrorMesh,
                    item.VariantSelections)))
                .ToList());

    AssetHostAnimationCatalog GetAnimationCatalog(string assetPath);

    AssetHostPaintedVariantResult ExportPaintedVariant(AssetHostPaintedVariantRequest request)
        => new(
            false,
            null,
            Array.Empty<string>(),
            Array.Empty<string>(),
            [new AssetHostError("UnsupportedOperation", "This runtime does not support painted variant export.")]);
}

public interface IAssetHostRuntimeFactory
{
    IAssetHostRuntime Create(
        IReadOnlyList<string> packPaths,
        string outputRoot,
        string? vanillaPackFilesCachePath = null);
}

/// <summary>
/// Optional factory extension used by the named-pipe server. Keeping the
/// extension separate preserves the deterministic factory contract used by
/// CLI callers and existing protocol tests.
/// </summary>
public interface IAssetHostInteractiveRuntimeFactory
{
    IAssetHostRuntime Create(
        IReadOnlyList<string> packPaths,
        string outputRoot,
        string? vanillaPackFilesCachePath,
        IMissingSkeletonDecision missingSkeletonDecision);
}

public sealed record AssetHostError(string Code, string Message, string? Details = null);

public sealed record AssetHostResponse(
    int ProtocolVersion,
    string RequestId,
    bool Success,
    string? Command,
    [property: JsonConverter(typeof(AssetHostResponseResultJsonConverter))] object? Result,
    AssetHostError? Error)
{
    public static AssetHostResponse Ok(string requestId, string command, object? result = null)
        => new(AssetHostProtocol.ProtocolVersion, requestId, true, command, result, null);

    public static AssetHostResponse Fail(
        string requestId,
        string? command,
        string code,
        string message,
        string? details = null,
        object? result = null)
        => new(
            AssetHostProtocol.ProtocolVersion,
            requestId,
            false,
            command,
            result,
            new AssetHostError(code, message, details));
}

public static class AssetHostProtocol
{
    public const int ProtocolVersion = 1;
    public const int MaxFramePayloadBytes = 1024 * 1024;
    public static string HostVersion { get; } =
        typeof(AssetHostProtocol).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AssetHostProtocol).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public static readonly IReadOnlyList<string> Capabilities =
        ["hello", "initialize", "getAnimationCatalog", "exportModel", "exportModelBatch", "exportPaintedVariant", "paintedVariantRgba", "variantMeshSelections", "missingSkeletonDecision", "shutdown"];
}

/// <summary>
/// Dispatches one protocol request. It owns the initialized runtime and
/// therefore also defines replacement/disposal semantics for initialize.
/// Dispatch is synchronous by design; the pipe server has one serialized
/// read/dispatch/write loop.
/// </summary>
public sealed class AssetHostDispatcher : IDisposable
{
    private readonly IAssetHostRuntimeFactory _runtimeFactory;
    private readonly ILogger _logger;
    private IAssetHostRuntime? _runtime;
    private string? _outputRoot;
    private readonly IMissingSkeletonDecision? _missingSkeletonDecision;
    private bool _shutdownRequested;

    public AssetHostDispatcher(
        IAssetHostRuntimeFactory runtimeFactory,
        IMissingSkeletonDecision? missingSkeletonDecision = null)
    {
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        _missingSkeletonDecision = missingSkeletonDecision;
        _logger = Log.ForContext<AssetHostDispatcher>();
    }

    public bool ShutdownRequested => _shutdownRequested;

    public AssetHostResponse Dispatch(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return Dispatch(document.RootElement);
        }
        catch (JsonException exception)
        {
            return AssetHostResponse.Fail(
                string.Empty,
                null,
                "MalformedJson",
                "The request was not valid JSON.",
                exception.Message);
        }
    }

    public AssetHostResponse Dispatch(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object)
            return AssetHostResponse.Fail(string.Empty, null, "MalformedRequest", "The request must be a JSON object.");

        var requestId = ReadString(request, "requestId") ?? string.Empty;
        var command = ReadString(request, "command");
        if (!TryReadInt32(request, "protocolVersion", out var protocolVersion)
            || protocolVersion != AssetHostProtocol.ProtocolVersion)
        {
            return AssetHostResponse.Fail(
                requestId,
                command,
                "ProtocolVersionMismatch",
                $"Only protocol version {AssetHostProtocol.ProtocolVersion} is supported.");
        }

        if (string.IsNullOrWhiteSpace(requestId))
            return AssetHostResponse.Fail(string.Empty, command, "MissingRequestId", "The requestId field is required.");
        if (string.IsNullOrWhiteSpace(command))
            return AssetHostResponse.Fail(requestId, null, "MissingCommand", "The command field is required.");

        var stopwatch = Stopwatch.StartNew();
        AssetHostResponse response;
        try
        {
            response = command switch
            {
                "hello" => HandleHello(requestId),
                "initialize" => HandleInitialize(request, requestId),
                "getAnimationCatalog" => HandleGetAnimationCatalog(request, requestId),
                "exportModel" => HandleExportModel(request, requestId),
                "exportModelBatch" => HandleExportModelBatch(request, requestId),
                "exportPaintedVariant" => HandleExportPaintedVariant(request, requestId),
                "shutdown" => HandleShutdown(requestId),
                _ => AssetHostResponse.Fail(requestId, command, "UnknownCommand", $"Unknown command '{command}'.")
            };
        }
        catch (Exception exception)
        {
            // A request must never take down the dispatcher. Initialization
            // and export failures are kept distinct for callers that need to
            // decide whether to retry or repair their pack set.
            var code = string.Equals(command, "initialize", StringComparison.Ordinal)
                ? "InitializationFailed"
                : string.Equals(command, "getAnimationCatalog", StringComparison.Ordinal)
                    ? "AnimationCatalogFailed"
                    : string.Equals(command, "exportModel", StringComparison.Ordinal)
                        || string.Equals(command, "exportModelBatch", StringComparison.Ordinal)
                        || string.Equals(command, "exportPaintedVariant", StringComparison.Ordinal)
                        ? "ExportFailed"
                        : "RequestFailed";
            _logger.Error(
                exception,
                "Asset host request failed: requestId={RequestId}, command={Command}, code={Code}",
                requestId,
                command,
                code);
            response = AssetHostResponse.Fail(requestId, command, code, exception.Message);
        }

        stopwatch.Stop();
        _logger.Information(
            "Asset host request completed: requestId={RequestId}, command={Command}, success={Success}, elapsed={ElapsedMs}ms",
            requestId,
            command,
            response.Success,
            stopwatch.ElapsedMilliseconds);
        return response;
    }

    private static AssetHostResponse HandleHello(string requestId)
        => AssetHostResponse.Ok(
            requestId,
            "hello",
            new AssetHostHelloResult(
                AssetHostProtocol.HostVersion,
                AssetHostProtocol.ProtocolVersion,
                AssetHostProtocol.Capabilities,
                AssetHostProtocol.MaxFramePayloadBytes));

    private AssetHostResponse HandleInitialize(JsonElement request, string requestId)
    {
        if (!TryReadStringArray(request, "packPaths", out var packPaths)
            || packPaths.Count == 0)
        {
            return AssetHostResponse.Fail(
                requestId,
                "initialize",
                "InvalidInitialization",
                "initialize requires a non-empty packPaths array.");
        }

        var rawOutputRoot = ReadString(request, "outputRoot");
        if (!TryNormalizeOutputRoot(rawOutputRoot, out var outputRoot, out var outputError))
        {
            return AssetHostResponse.Fail(
                requestId,
                "initialize",
                "InvalidOutputRoot",
                outputError!);
        }

        var vanillaPackFilesCachePath = ReadString(request, "vanillaPackFilesCachePath");

        // Build first. If a pack is corrupt, the previous runtime remains
        // usable and is not disposed by a failed replacement.
        var runtimeCreateStopwatch = Stopwatch.StartNew();
        var replacement = CreateRuntime(packPaths, outputRoot, vanillaPackFilesCachePath);
        runtimeCreateStopwatch.Stop();

        var runtimeReplaceStopwatch = Stopwatch.StartNew();
        var previous = _runtime;
        _runtime = replacement;
        _outputRoot = outputRoot;
        previous?.Dispose();
        runtimeReplaceStopwatch.Stop();

        _logger.Debug(
            "Asset host initialize phases: requestId={RequestId}, packs={PackCount}, runtimeCreate={RuntimeCreateMs}ms, runtimeReplace={RuntimeReplaceMs}ms, vanillaPackCache={HasVanillaPackCache}",
            requestId,
            packPaths.Count,
            runtimeCreateStopwatch.ElapsedMilliseconds,
            runtimeReplaceStopwatch.ElapsedMilliseconds,
            string.IsNullOrWhiteSpace(vanillaPackFilesCachePath) == false);

        return AssetHostResponse.Ok(
            requestId,
            "initialize",
            new AssetHostInitializationResult(outputRoot, packPaths));
    }

    private IAssetHostRuntime CreateRuntime(
        IReadOnlyList<string> packPaths,
        string outputRoot,
        string? vanillaPackFilesCachePath)
    {
        if (_missingSkeletonDecision != null
            && _runtimeFactory is IAssetHostInteractiveRuntimeFactory interactiveFactory)
        {
            return interactiveFactory.Create(
                packPaths,
                outputRoot,
                vanillaPackFilesCachePath,
                _missingSkeletonDecision);
        }

        return _runtimeFactory.Create(packPaths, outputRoot, vanillaPackFilesCachePath);
    }

    private AssetHostResponse HandleGetAnimationCatalog(JsonElement request, string requestId)
    {
        if (_runtime == null || _outputRoot == null)
        {
            return AssetHostResponse.Fail(
                requestId,
                "getAnimationCatalog",
                "NotInitialized",
                "initialize must succeed before getAnimationCatalog.");
        }

        var assetPath = ReadString(request, "assetPath");
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            return AssetHostResponse.Fail(
                requestId,
                "getAnimationCatalog",
                "MissingAssetPath",
                "getAnimationCatalog requires assetPath.");
        }

        return AssetHostResponse.Ok(requestId, "getAnimationCatalog", _runtime.GetAnimationCatalog(assetPath));
    }

    private AssetHostResponse HandleExportModel(JsonElement request, string requestId)
    {
        if (_runtime == null || _outputRoot == null)
        {
            return AssetHostResponse.Fail(
                requestId,
                "exportModel",
                "NotInitialized",
                "initialize must succeed before exportModel.");
        }

        var assetPath = ReadString(request, "assetPath");
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            return AssetHostResponse.Fail(
                requestId,
                "exportModel",
                "MissingAssetPath",
                "exportModel requires assetPath.");
        }

        var relativeOutputPath = ReadString(request, "outputPath");
        if (!TryResolveOutputPath(_outputRoot, relativeOutputPath, out var outputPath, out var outputError))
        {
            return AssetHostResponse.Fail(
                requestId,
                "exportModel",
                "InvalidOutputPath",
                outputError!);
        }

        if (!TryReadStringArray(request, "animationPaths", out var animationPaths, allowMissing: true))
        {
            return AssetHostResponse.Fail(
                requestId,
                "exportModel",
                "InvalidAnimationPaths",
                "animationPaths must be an array of virtual paths.");
        }

        if (!TryReadVariantMeshSelections(request, out var variantSelections, allowMissing: true))
        {
            return AssetHostResponse.Fail(
                requestId,
                "exportModel",
                "InvalidVariantMeshSelections",
                "variantSelections must be an array of slotPath/choiceIndex objects.");
        }

        if (!TryReadBoolean(request, true, out var exportMaterials, "exportMaterials", "materials")
            || !TryReadBoolean(request, true, out var includeSkeleton, "includeSkeleton", "skeleton")
            || !TryReadBoolean(request, true, out var mirrorMesh, "mirrorMesh", "mirror"))
        {
            return AssetHostResponse.Fail(
                requestId,
                "exportModel",
                "InvalidExportOptions",
                "exportMaterials, includeSkeleton, and mirrorMesh must be boolean values.");
        }

        var exportRequest = new AssetHostExportRequest(
            assetPath,
            outputPath,
            animationPaths,
            exportMaterials,
            includeSkeleton,
            mirrorMesh,
            variantSelections);
        var result = _runtime.ExportModel(exportRequest);
        if (result.Success)
            return AssetHostResponse.Ok(requestId, "exportModel", result);

        var firstError = result.Errors.FirstOrDefault();
        return AssetHostResponse.Fail(
            requestId,
            "exportModel",
            firstError?.Code ?? "ExportFailed",
            firstError?.Message ?? "The export failed.",
            firstError?.Details,
            result);
    }

    private AssetHostResponse HandleExportModelBatch(JsonElement request, string requestId)
    {
        if (_runtime == null || _outputRoot == null)
        {
            return AssetHostResponse.Fail(
                requestId,
                "exportModelBatch",
                "NotInitialized",
                "initialize must succeed before exportModelBatch.");
        }

        var assetPath = ReadString(request, "assetPath");
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            return AssetHostResponse.Fail(
                requestId,
                "exportModelBatch",
                "MissingAssetPath",
                "exportModelBatch requires assetPath.");
        }

        if (!request.TryGetProperty("items", out var itemsProperty)
            || itemsProperty.ValueKind != JsonValueKind.Array)
        {
            return AssetHostResponse.Fail(
                requestId,
                "exportModelBatch",
                "InvalidBatchItems",
                "items must be a non-empty array of outputPath/variantSelections objects.");
        }

        var items = new List<AssetHostBatchExportItem>();
        foreach (var item in itemsProperty.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return AssetHostResponse.Fail(
                    requestId,
                    "exportModelBatch",
                    "InvalidBatchItems",
                    "Each batch item must be an object.");
            }

            var relativeOutputPath = ReadString(item, "outputPath");
            if (!TryResolveOutputPath(_outputRoot, relativeOutputPath, out var outputPath, out var outputError))
            {
                return AssetHostResponse.Fail(
                    requestId,
                    "exportModelBatch",
                    "InvalidOutputPath",
                    outputError!);
            }

            if (!TryReadVariantMeshSelections(item, out var variantSelections, allowMissing: true))
            {
                return AssetHostResponse.Fail(
                    requestId,
                    "exportModelBatch",
                    "InvalidVariantMeshSelections",
                    "Each item's variantSelections must be an array of slotPath/choiceIndex objects.");
            }

            items.Add(new AssetHostBatchExportItem(outputPath, variantSelections));
            if (items.Count > 100)
            {
                return AssetHostResponse.Fail(
                    requestId,
                    "exportModelBatch",
                    "BatchTooLarge",
                    "exportModelBatch supports at most 100 items.");
            }
        }

        if (items.Count == 0)
        {
            return AssetHostResponse.Fail(
                requestId,
                "exportModelBatch",
                "InvalidBatchItems",
                "exportModelBatch requires at least one item.");
        }

        if (!TryReadStringArray(request, "animationPaths", out var animationPaths, allowMissing: true))
        {
            return AssetHostResponse.Fail(
                requestId,
                "exportModelBatch",
                "InvalidAnimationPaths",
                "animationPaths must be an array of virtual paths.");
        }

        if (!TryReadBoolean(request, true, out var exportMaterials, "exportMaterials", "materials")
            || !TryReadBoolean(request, true, out var includeSkeleton, "includeSkeleton", "skeleton")
            || !TryReadBoolean(request, true, out var mirrorMesh, "mirrorMesh", "mirror"))
        {
            return AssetHostResponse.Fail(
                requestId,
                "exportModelBatch",
                "InvalidExportOptions",
                "exportMaterials, includeSkeleton, and mirrorMesh must be boolean values.");
        }

        var result = _runtime.ExportModels(new AssetHostBatchExportRequest(
            assetPath,
            items,
            animationPaths,
            exportMaterials,
            includeSkeleton,
            mirrorMesh));
        return AssetHostResponse.Ok(requestId, "exportModelBatch", result);
    }


    private AssetHostResponse HandleExportPaintedVariant(JsonElement request, string requestId)
    {
        if (_runtime == null || _outputRoot == null)
        {
            return AssetHostResponse.Fail(
                requestId,
                "exportPaintedVariant",
                "NotInitialized",
                "initialize must succeed before exportPaintedVariant.");
        }

        var assetPath = ReadString(request, "assetPath")?.Trim();
        var variantName = ReadString(request, "variantName")?.Trim();
        var relativeOutputDirectory = ReadString(request, "outputDirectory")?.Trim();
        if (string.IsNullOrWhiteSpace(assetPath)
            || string.IsNullOrWhiteSpace(variantName)
            || string.IsNullOrWhiteSpace(relativeOutputDirectory))
        {
            return AssetHostResponse.Fail(
                requestId,
                "exportPaintedVariant",
                "InvalidPaintedVariantRequest",
                "assetPath, variantName, and outputDirectory are required.");
        }

        if (!TryResolveDirectoryPath(_outputRoot, relativeOutputDirectory, out var outputDirectory, out var outputError))
        {
            return AssetHostResponse.Fail(
                requestId,
                "exportPaintedVariant",
                "InvalidOutputDirectory",
                outputError!);
        }

        if (!TryReadVariantMeshSelections(request, out var variantSelections, allowMissing: true))
        {
            return AssetHostResponse.Fail(
                requestId,
                "exportPaintedVariant",
                "InvalidVariantMeshSelections",
                "variantSelections must be an array of slotPath/choiceIndex objects.");
        }

        if (!request.TryGetProperty("textures", out var texturesProperty)
            || texturesProperty.ValueKind != JsonValueKind.Array)
        {
            return AssetHostResponse.Fail(
                requestId,
                "exportPaintedVariant",
                "InvalidPaintedTextures",
                "textures must be a non-empty array.");
        }

        var textures = new List<AssetHostPaintedTextureInput>();
        foreach (var texture in texturesProperty.EnumerateArray())
        {
            if (texture.ValueKind != JsonValueKind.Object)
            {
                return AssetHostResponse.Fail(
                    requestId,
                    "exportPaintedVariant",
                    "InvalidPaintedTextures",
                    "Each painted texture must be an object.");
            }

            var sourceVirtualPath = ReadString(texture, "sourceVirtualPath")?.Trim();
            var relativeRgbaPath = ReadString(texture, "rgbaPath")?.Trim();
            if (string.IsNullOrWhiteSpace(sourceVirtualPath)
                || string.IsNullOrWhiteSpace(relativeRgbaPath)
                || !TryReadInt32(texture, "width", out var width)
                || !TryReadInt32(texture, "height", out var height)
                || width is <= 0 or > 16384
                || height is <= 0 or > 16384)
            {
                return AssetHostResponse.Fail(
                    requestId,
                    "exportPaintedVariant",
                    "InvalidPaintedTexture",
                    "Each painted texture requires sourceVirtualPath, rgbaPath, and dimensions from 1 to 16384.");
            }

            if (!TryResolveInputRgbaPath(_outputRoot, relativeRgbaPath, width, height, out var rgbaPath, out var rgbaError))
            {
                return AssetHostResponse.Fail(
                    requestId,
                    "exportPaintedVariant",
                    "InvalidPaintedTexture",
                    rgbaError ?? "The painted texture RGBA path is invalid.");
            }

            textures.Add(new AssetHostPaintedTextureInput(sourceVirtualPath, rgbaPath, width, height));
            if (textures.Count > 64)
            {
                return AssetHostResponse.Fail(
                    requestId,
                    "exportPaintedVariant",
                    "TooManyPaintedTextures",
                    "A painted variant may contain at most 64 modified textures.");
            }
        }

        if (textures.Count == 0)
        {
            return AssetHostResponse.Fail(
                requestId,
                "exportPaintedVariant",
                "InvalidPaintedTextures",
                "At least one painted texture is required.");
        }

        var result = _runtime.ExportPaintedVariant(new AssetHostPaintedVariantRequest(
            assetPath,
            outputDirectory,
            variantName,
            textures,
            variantSelections));
        if (result.Success)
            return AssetHostResponse.Ok(requestId, "exportPaintedVariant", result);

        var firstError = result.Errors.FirstOrDefault();
        return AssetHostResponse.Fail(
            requestId,
            "exportPaintedVariant",
            firstError?.Code ?? "PaintedVariantExportFailed",
            firstError?.Message ?? "The painted variant export failed.",
            firstError?.Details,
            result);
    }

    private AssetHostResponse HandleShutdown(string requestId)
    {
        _shutdownRequested = true;
        DisposeRuntime();
        return AssetHostResponse.Ok(
            requestId,
            "shutdown",
            new AssetHostShutdownResult(ShuttingDown: true));
    }

    private static bool TryNormalizeOutputRoot(string? rawRoot, out string outputRoot, out string? error)
    {
        outputRoot = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(rawRoot))
        {
            error = "outputRoot is required.";
            return false;
        }

        try
        {
            outputRoot = Path.GetFullPath(rawRoot);
            return true;
        }
        catch (Exception exception)
        {
            error = $"outputRoot is invalid: {exception.Message}";
            return false;
        }
    }

    private static bool TryResolveOutputPath(
        string outputRoot,
        string? relativeOutputPath,
        out string outputPath,
        out string? error)
    {
        outputPath = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(relativeOutputPath))
        {
            error = "outputPath is required.";
            return false;
        }

        try
        {
            if (Path.IsPathRooted(relativeOutputPath) || relativeOutputPath.IndexOf('\0') >= 0)
            {
                error = "outputPath must be relative to outputRoot.";
                return false;
            }

            // Normalize both separators so a request produced on another
            // platform cannot bypass the root check on Windows (or tests on
            // Unix).
            var normalizedRelativePath = relativeOutputPath
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            outputPath = Path.GetFullPath(Path.Combine(outputRoot, normalizedRelativePath));
            var relativeToRoot = Path.GetRelativePath(outputRoot, outputPath);
            if (relativeToRoot == ".."
                || relativeToRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.IsPathRooted(relativeToRoot))
            {
                outputPath = string.Empty;
                error = "outputPath must remain under outputRoot.";
                return false;
            }

            var extension = Path.GetExtension(outputPath);
            if (!extension.Equals(".glb", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase))
            {
                outputPath = string.Empty;
                error = "outputPath must end in .glb or .gltf.";
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            outputPath = string.Empty;
            error = $"outputPath is invalid: {exception.Message}";
            return false;
        }
    }

    private static bool TryResolveDirectoryPath(
        string outputRoot,
        string relativePath,
        out string outputPath,
        out string? error)
    {
        outputPath = string.Empty;
        error = null;
        try
        {
            if (Path.IsPathRooted(relativePath) || relativePath.IndexOf('\0') >= 0)
            {
                error = "outputDirectory must be relative to outputRoot.";
                return false;
            }

            var normalized = relativePath
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            outputPath = Path.GetFullPath(Path.Combine(outputRoot, normalized));
            var relativeToRoot = Path.GetRelativePath(outputRoot, outputPath);
            if (relativeToRoot == ".."
                || relativeToRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.IsPathRooted(relativeToRoot))
            {
                outputPath = string.Empty;
                error = "outputDirectory must remain under outputRoot.";
                return false;
            }
            return true;
        }
        catch (Exception exception)
        {
            outputPath = string.Empty;
            error = $"outputDirectory is invalid: {exception.Message}";
            return false;
        }
    }

    private static bool TryResolveInputRgbaPath(
        string outputRoot,
        string relativePath,
        int width,
        int height,
        out string rgbaPath,
        out string? error)
    {
        if (!TryResolveDirectoryPath(outputRoot, relativePath, out rgbaPath, out error))
            return false;

        if (!Path.GetExtension(rgbaPath).Equals(".rgba", StringComparison.OrdinalIgnoreCase))
        {
            rgbaPath = string.Empty;
            error = "Painted texture input must be a .rgba file.";
            return false;
        }
        if (!File.Exists(rgbaPath))
        {
            rgbaPath = string.Empty;
            error = "Painted texture input does not exist.";
            return false;
        }

        try
        {
            var expectedBytes = checked((long)width * height * 4);
            if (expectedBytes > 64L * 1024 * 1024)
            {
                rgbaPath = string.Empty;
                error = "Painted texture RGBA data may not exceed 64 MiB.";
                return false;
            }

            var actualBytes = new FileInfo(rgbaPath).Length;
            if (actualBytes != expectedBytes)
            {
                rgbaPath = string.Empty;
                error = $"Painted texture RGBA data has {actualBytes} bytes; expected {expectedBytes} for {width}x{height}.";
                return false;
            }
        }
        catch (OverflowException)
        {
            rgbaPath = string.Empty;
            error = "Painted texture dimensions are too large.";
            return false;
        }

        return true;
    }

    private static string? ReadString(JsonElement request, string propertyName)
        => request.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryReadInt32(JsonElement request, string propertyName, out int value)
    {
        value = default;
        return request.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }

    private static bool TryReadBoolean(
        JsonElement request,
        bool defaultValue,
        out bool value,
        params string[] propertyNames)
    {
        value = defaultValue;
        foreach (var propertyName in propertyNames)
        {
            if (!request.TryGetProperty(propertyName, out var propertyValue))
                continue;
            if (propertyValue.ValueKind != JsonValueKind.False && propertyValue.ValueKind != JsonValueKind.True)
                return false;
            value = propertyValue.GetBoolean();
            return true;
        }
        return true;
    }

    private static bool TryReadStringArray(
        JsonElement request,
        string propertyName,
        out List<string> values,
        bool allowMissing = false)
    {
        values = [];
        if (!request.TryGetProperty(propertyName, out var property))
            return allowMissing;
        if (property.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                return false;
            values.Add(item.GetString()!);
        }
        return true;
    }

    private static bool TryReadVariantMeshSelections(
        JsonElement request,
        out List<AssetHostVariantMeshSelection> values,
        bool allowMissing = false)
    {
        values = [];
        if (!request.TryGetProperty("variantSelections", out var property))
            return allowMissing;
        if (property.ValueKind != JsonValueKind.Array)
            return false;

        var seenSlotPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                return false;

            var slotPath = ReadString(item, "slotPath")?.Trim();
            if (string.IsNullOrWhiteSpace(slotPath)
                || !TryReadInt32(item, "choiceIndex", out var choiceIndex)
                || choiceIndex < 0
                || !seenSlotPaths.Add(slotPath))
                return false;

            values.Add(new AssetHostVariantMeshSelection(slotPath, choiceIndex));
        }

        return true;
    }

    public void Dispose()
    {
        DisposeRuntime();
        GC.SuppressFinalize(this);
    }

    private void DisposeRuntime()
    {
        var runtime = _runtime;
        _runtime = null;
        _outputRoot = null;
        runtime?.Dispose();
    }
}
