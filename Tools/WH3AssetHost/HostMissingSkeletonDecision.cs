using System.Text;
using System.Text.Json;
using Editors.ImportExport.Exporting.Exporters.RmvToGltf;

namespace WH3AssetHost;

/// <summary>
/// Bridges the exporter-core missing-skeleton contract to the manager on the
/// already-connected asset-host pipe. The exporter is synchronous, so the
/// adapter performs a correlated request/response exchange synchronously
/// while the pipe server is dispatching the export request.
/// </summary>
public sealed class HostMissingSkeletonDecision : IMissingSkeletonDecision
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly Stream _stream;
    private readonly CancellationToken _cancellationToken;
    private readonly object _exchangeLock = new();

    public HostMissingSkeletonDecision(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _cancellationToken = cancellationToken;
    }

    public MissingSkeletonAction Decide(MissingSkeletonContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        lock (_exchangeLock)
        {
            var requestId = "decision-" + Guid.NewGuid().ToString("N");
            try
            {
                NamedPipeFrameProtocol.WriteJsonFrameAsync(
                    _stream,
                    new AssetHostMissingSkeletonDecisionRequest(
                        AssetHostProtocol.ProtocolVersion,
                        requestId,
                        "decisionRequest",
                        "missingSkeleton",
                        context.SkeletonName,
                        context.Message),
                    _cancellationToken).GetAwaiter().GetResult();

                while (true)
                {
                    var payload = NamedPipeFrameProtocol.ReadFrameAsync(
                        _stream,
                        _cancellationToken).GetAwaiter().GetResult();
                    if (payload == null)
                        return MissingSkeletonAction.CancelExport;

                    using var document = JsonDocument.Parse(StrictUtf8.GetString(payload));
                    if (!TryReadDecision(document.RootElement, requestId, out var action, out var isCorrelated))
                    {
                        if (isCorrelated)
                            return MissingSkeletonAction.CancelExport;
                        continue;
                    }

                    return action;
                }
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
            {
                return MissingSkeletonAction.CancelExport;
            }
            catch (AssetHostFrameException)
            {
                return MissingSkeletonAction.CancelExport;
            }
            catch (IOException)
            {
                // A disconnected manager cannot answer safely. Abort this
                // export instead of falling back to an implicit approval.
                return MissingSkeletonAction.CancelExport;
            }
            catch (ObjectDisposedException)
            {
                return MissingSkeletonAction.CancelExport;
            }
            catch (JsonException)
            {
                return MissingSkeletonAction.CancelExport;
            }
        }
    }

    public bool ContinueWithoutSkeleton(string skeletonName)
        => Decide(new MissingSkeletonContext(skeletonName))
            == MissingSkeletonAction.ContinueWithoutSkeleton;

    private static bool TryReadDecision(
        JsonElement response,
        string requestId,
        out MissingSkeletonAction action,
        out bool isCorrelated)
    {
        action = MissingSkeletonAction.CancelExport;
        isCorrelated = false;
        if (response.ValueKind != JsonValueKind.Object
            || !TryReadString(response, "requestId", out var responseId)
            || !string.Equals(responseId, requestId, StringComparison.Ordinal))
        {
            return false;
        }

        isCorrelated = true;

        if (TryReadString(response, "command", out var command)
            && !string.Equals(command, "decisionResponse", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(command, "missingSkeletonDecisionResponse", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (response.TryGetProperty("success", out var success)
            && success.ValueKind == JsonValueKind.False)
        {
            action = MissingSkeletonAction.CancelExport;
            return true;
        }

        if (TryReadAction(response, out action))
            return true;

        if (response.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.Object
            && TryReadAction(result, out action))
        {
            return true;
        }

        return false;
    }

    private static bool TryReadAction(JsonElement value, out MissingSkeletonAction action)
    {
        action = MissingSkeletonAction.CancelExport;
        foreach (var propertyName in new[] { "action", "selectedAction", "decision" })
        {
            if (!value.TryGetProperty(propertyName, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.True)
            {
                action = MissingSkeletonAction.ContinueWithoutSkeleton;
                return true;
            }

            if (property.ValueKind == JsonValueKind.False)
            {
                action = MissingSkeletonAction.CancelExport;
                return true;
            }

            if (property.ValueKind != JsonValueKind.String)
                return false;

            var valueText = property.GetString();
            if (string.Equals(valueText, "continueWithoutSkeleton", StringComparison.OrdinalIgnoreCase)
                || string.Equals(valueText, "continue", StringComparison.OrdinalIgnoreCase)
                || string.Equals(valueText, "yes", StringComparison.OrdinalIgnoreCase))
            {
                action = MissingSkeletonAction.ContinueWithoutSkeleton;
                return true;
            }

            if (string.Equals(valueText, "cancelExport", StringComparison.OrdinalIgnoreCase)
                || string.Equals(valueText, "cancel", StringComparison.OrdinalIgnoreCase)
                || string.Equals(valueText, "no", StringComparison.OrdinalIgnoreCase))
            {
                action = MissingSkeletonAction.CancelExport;
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool TryReadString(
        JsonElement value,
        string propertyName,
        out string? text)
    {
        text = null;
        if (!value.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        text = property.GetString();
        return text != null;
    }
}
