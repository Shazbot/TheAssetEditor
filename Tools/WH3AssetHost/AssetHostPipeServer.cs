using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace WH3AssetHost;

/// <summary>
/// Serves one client over one byte-mode named pipe. A single dispatch loop is
/// intentional: pack replacement, export, and shutdown cannot overlap.
/// </summary>
public sealed class AssetHostPipeServer
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly IAssetHostRuntimeFactory _runtimeFactory;

    public AssetHostPipeServer(IAssetHostRuntimeFactory runtimeFactory)
    {
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
    }

    public async Task RunAsync(
        string pipeName,
        int? parentProcessId = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePipeName(pipeName);
        if (parentProcessId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(parentProcessId), "The parent PID must be positive.");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var parentMonitor = parentProcessId.HasValue
            ? MonitorParentAsync(parentProcessId.Value, stop)
            : Task.CompletedTask;

        try
        {
            using var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                transmissionMode: PipeTransmissionMode.Byte,
                options: PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(stop.Token);
                using var dispatcher = new AssetHostDispatcher(
                    _runtimeFactory,
                    new HostMissingSkeletonDecision(pipe, stop.Token));
                await ServeClientAsync(pipe, dispatcher, stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Parent exit or caller cancellation is a normal lifecycle
                // event, not a protocol failure.
            }
        }
        finally
        {
            stop.Cancel();
            try
            {
                await parentMonitor;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task ServeClientAsync(
        NamedPipeServerStream pipe,
        AssetHostDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        while (!dispatcher.ShutdownRequested && !cancellationToken.IsCancellationRequested)
        {
            byte[]? payload;
            try
            {
                payload = await NamedPipeFrameProtocol.ReadFrameAsync(pipe, cancellationToken);
            }
            catch (AssetHostFrameException exception)
            {
                // The header is still trustworthy for zero/oversize frames,
                // so a structured error can be sent before closing. A
                // truncated header/payload cannot safely be framed.
                if (exception.CanRespond)
                {
                    var frameErrorResponse = AssetHostResponse.Fail(
                        string.Empty,
                        null,
                        exception.Code,
                        exception.Message);
                    try
                    {
                        await NamedPipeFrameProtocol.WriteJsonFrameAsync(pipe, frameErrorResponse, cancellationToken);
                    }
                    catch (IOException)
                    {
                    }
                }
                break;
            }
            catch (IOException)
            {
                // Named pipe implementations may report a peer disconnect
                // as an IOException instead of an EOF read.
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (payload == null)
                break;

            AssetHostResponse response;
            try
            {
                var json = StrictUtf8.GetString(payload);
                response = dispatcher.Dispatch(json);
            }
            catch (Exception exception)
            {
                // UTF-8 replacement and JSON errors are request-local. Keep
                // the connection alive unless the framing itself failed.
                response = AssetHostResponse.Fail(
                    string.Empty,
                    null,
                    "MalformedRequest",
                    "The request could not be processed.",
                    exception.Message);
            }

            try
            {
                await NamedPipeFrameProtocol.WriteJsonFrameAsync(pipe, response, cancellationToken);
            }
            catch (IOException)
            {
                // Client disconnected. The using scope disposes the runtime
                // through AssetHostDispatcher and ends this one-client host.
                break;
            }
        }
    }

    private static async Task MonitorParentAsync(int parentProcessId, CancellationTokenSource stop)
    {
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            await parent.WaitForExitAsync(stop.Token);
            if (!stop.IsCancellationRequested)
                stop.Cancel();
        }
        catch (ArgumentException)
        {
            // The parent was already gone before the monitor started.
            if (!stop.IsCancellationRequested)
                stop.Cancel();
        }
        catch (InvalidOperationException)
        {
            if (!stop.IsCancellationRequested)
                stop.Cancel();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    private static void ValidatePipeName(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
            throw new ArgumentException("A pipe name is required.", nameof(pipeName));
        if (pipeName.Contains(Path.DirectorySeparatorChar)
            || pipeName.Contains(Path.AltDirectorySeparatorChar)
            || pipeName.Contains('\0'))
        {
            throw new ArgumentException("The pipe name must be a simple local name.", nameof(pipeName));
        }
    }
}
