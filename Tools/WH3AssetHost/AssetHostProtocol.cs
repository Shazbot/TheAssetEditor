using System.Text.Json;
using System.Reflection;
using Editors.ImportExport.Exporting.Exporters.RmvToGltf;

namespace WH3AssetHost;

public sealed record AssetHostExportRequest(
    string AssetPath,
    string OutputPath,
    IReadOnlyList<string> AnimationPaths,
    bool ExportMaterials = true,
    bool IncludeSkeleton = true,
    bool MirrorMesh = true);

public interface IAssetHostRuntime : IDisposable
{
    ExportResult ExportModel(AssetHostExportRequest request);
}

public interface IAssetHostRuntimeFactory
{
    IAssetHostRuntime Create(IReadOnlyList<string> packPaths, string outputRoot);
}

public sealed record AssetHostError(string Code, string Message, string? Details = null);

public sealed record AssetHostResponse(
    int ProtocolVersion,
    string RequestId,
    bool Success,
    string? Command,
    object? Result,
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
        ["hello", "initialize", "exportModel", "shutdown"];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
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
    private IAssetHostRuntime? _runtime;
    private string? _outputRoot;
    private bool _shutdownRequested;

    public AssetHostDispatcher(IAssetHostRuntimeFactory runtimeFactory)
    {
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
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

        try
        {
            return command switch
            {
                "hello" => HandleHello(requestId),
                "initialize" => HandleInitialize(request, requestId),
                "exportModel" => HandleExportModel(request, requestId),
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
                : string.Equals(command, "exportModel", StringComparison.Ordinal)
                    ? "ExportFailed"
                    : "RequestFailed";
            return AssetHostResponse.Fail(requestId, command, code, exception.Message, exception.ToString());
        }
    }

    private static AssetHostResponse HandleHello(string requestId)
        => AssetHostResponse.Ok(requestId, "hello", new
        {
            hostVersion = AssetHostProtocol.HostVersion,
            protocolVersion = AssetHostProtocol.ProtocolVersion,
            capabilities = AssetHostProtocol.Capabilities,
            maxFrameBytes = AssetHostProtocol.MaxFramePayloadBytes
        });

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

        // Build first. If a pack is corrupt, the previous runtime remains
        // usable and is not disposed by a failed replacement.
        var replacement = _runtimeFactory.Create(packPaths, outputRoot);
        var previous = _runtime;
        _runtime = replacement;
        _outputRoot = outputRoot;
        previous?.Dispose();

        return AssetHostResponse.Ok(requestId, "initialize", new { outputRoot, packPaths });
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
            mirrorMesh);
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

    private AssetHostResponse HandleShutdown(string requestId)
    {
        _shutdownRequested = true;
        DisposeRuntime();
        return AssetHostResponse.Ok(requestId, "shutdown", new { shuttingDown = true });
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
