using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Editors.PackOptimizer.Core;
using Serilog;
using Shared.Core.PackFiles.Models;
using Shared.Core.Settings;

namespace WH3AssetHost.Optimize;

/// <summary>
/// <c>WH3AssetHost optimize --request &lt;file.json&gt; [--parent-pid &lt;pid&gt;]</c>: runs the pack
/// optimizer once and exits. It never starts the pipe server. stdout carries only JSON lines
/// (progress, then one result or error); diagnostics go to stderr and the log file.
/// Exit codes: 0 succeeded, 1 failed, 2 invalid request, 3 cancelled.
/// </summary>
internal static class OptimizeMode
{
    private const int ProgressIntervalMs = 100;

    public static int Run(string[] args)
    {
        // Keep the real stdout for the protocol and send anything else that writes to the console
        // (libraries included) to stderr, so stdout can always be parsed line by line.
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        Console.SetOut(Console.Error);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Console.Error.WriteLine($"Unhandled: {e.ExceptionObject}");

        if (args.Length == 2 && string.Equals(args[1], "--check-dds", StringComparison.Ordinal))
            return CheckNativeDds(stdout);

        var (requestPath, parentPid, argumentError) = OptimizeRequestReader.ParseArguments(args);
        if (argumentError != null)
            return Fail(stdout, OptimizeOutcome.InvalidRequest, argumentError, "InvalidArguments");

        string requestJson;
        try
        {
            requestJson = File.ReadAllText(requestPath!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Fail(stdout, OptimizeOutcome.InvalidRequest, $"The request file could not be read: {exception.Message}", exception.GetType().Name);
        }

        var request = OptimizeRequestReader.Parse(requestJson, out var parseError);
        if (parseError != null)
            return Fail(stdout, OptimizeOutcome.InvalidRequest, parseError, "InvalidRequest");

        var validationError = OptimizeRequestValidator.Validate(request);
        if (validationError != null)
            return Fail(stdout, OptimizeOutcome.InvalidRequest, validationError, "InvalidRequest");

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        });
        if (parentPid.HasValue)
            _ = MonitorParentAsync(parentPid.Value, cancellation);

        var nativeError = GetNativeLibraryError();
        if (nativeError != null)
            return Fail(stdout, OptimizeOutcome.Failed, nativeError, "NativeLibraryUnavailable");

        var outcome = Execute(request!, stdout, cancellation.Token);
        return OptimizeExitCodes.For(outcome);
    }

    /// <summary>
    /// Loads the native DirectXTex shim and compresses a tiny texture through it. Proves the library is found
    /// and works in this build (single-file, trimmed or not).
    /// </summary>
    private static int CheckNativeDds(TextWriter stdout)
    {
        try
        {
            using var image = Editors.ImportExport.Dds.Native.TexHelper.Instance.Initialize2D(
                Editors.ImportExport.Dds.Native.DXGI_FORMAT.B8G8R8A8_UNORM, 8, 8, 1, 1, Editors.ImportExport.Dds.Native.CP_FLAGS.NONE);
            using var compressed = image.Compress(
                Editors.ImportExport.Dds.Native.DXGI_FORMAT.BC1_UNORM, Editors.ImportExport.Dds.Native.TEX_COMPRESS_FLAGS.DEFAULT, 0.5f);
            var native = Editors.ImportExport.Dds.Native.TexHelper.NativeVersion;
            Console.Error.WriteLine($"DirectXTex shim: version {native.Version}, {native.Tag} ({native.Commit})");
            WriteLine(stdout, new OptimizeResultLine("result", "dds-ok", string.Empty, string.Empty, 0, 0, 0, 0, 0, 0), OptimizeJsonContext.Default.OptimizeResultLine);
            return OptimizeExitCodes.Succeeded;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.ToString());
            return Fail(stdout, OptimizeOutcome.Failed, exception.Message, exception.GetType().Name);
        }
    }

    /// <summary>Null when the native DirectXTex shim loads; otherwise why not.</summary>
    internal static string? GetNativeLibraryError()
    {
        try
        {
            _ = Editors.ImportExport.Dds.Native.TexHelper.NativeVersion;
            return null;
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            return $"The native DirectXTex library (DirectXTexShim.dll) could not be loaded: {exception.Message}";
        }
    }

    private static OptimizeOutcome Execute(OptimizeRequest request, TextWriter stdout, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var finalOutput = Path.GetFullPath(request.OutputPackPath);
        var finalReport = Path.GetFullPath(request.ReportPath);
        var tempOutput = finalOutput + ".optimizing";
        var tempReport = finalReport + ".optimizing";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(finalOutput)!);
            Directory.CreateDirectory(Path.GetDirectoryName(finalReport)!);
            DeleteQuietly(tempOutput);
            DeleteQuietly(tempReport);

            var dataFolder = OptimizeRequestValidator.ResolveDataFolder(request.GameDirectory)!;
            var vanilla = HostVanillaContainers.LoadVanilla(dataFolder, request.VanillaFileIndexPath, Console.Error.WriteLine);
            cancellationToken.ThrowIfCancellationRequested();
            var source = HostVanillaContainers.LoadSource(request.SourcePackPath);
            cancellationToken.ThrowIfCancellationRequested();

            var consumers = new List<IPackFileContainer>();
            if (request.ConsumerPackPaths is { Count: > 0 })
            {
                var loadWatch = Stopwatch.StartNew();
                foreach (var consumerPath in request.ConsumerPackPaths)
                {
                    consumers.Add(HostVanillaContainers.LoadConsumer(consumerPath));
                    cancellationToken.ThrowIfCancellationRequested();
                }

                Console.Error.WriteLine(
                    $"Loaded {consumers.Count} consumer pack index(es) in {loadWatch.Elapsed.TotalSeconds:0.0} s.");
                Log.Information("Loaded {Count} consumer pack(s)", consumers.Count);
            }

            var progress = new ThrottledProgress(stdout);
            var result = PackOptimizer.Run(
                new PackOptimizerRequest(
                    Path.GetFullPath(request.SourcePackPath),
                    source,
                    vanilla,
                    finalOutput,
                    finalReport,
                    GameTypeEnum.Warhammer3,
                    request.Options!.ToOptimizerOptions(),
                    MissingTexturePolicy.Skip,
                    request.CacheDirectory,
                    OutputWritePath: tempOutput,
                    ReportWritePath: tempReport,
                    ModConsumers: consumers,
                    SourceLoadOrderIndex: consumers.Count == 0 ? null : request.SourceLoadOrderIndex),
                progress,
                cancellationToken);

            if (result.Status == PackOptimizerStatus.Cancelled)
            {
                DeleteTemps(tempOutput, tempReport);
                WriteLine(stdout, new OptimizeResultLine("result", "cancelled", finalOutput, finalReport, 0, 0, 0, 0, 0, elapsed.Elapsed.TotalSeconds), OptimizeJsonContext.Default.OptimizeResultLine);
                return OptimizeOutcome.Cancelled;
            }

            foreach (var warning in result.Warnings ?? [])
                Console.Error.WriteLine($"WARNING: {warning}");

            File.Move(tempOutput, finalOutput, overwrite: true);
            if (File.Exists(tempReport))
                File.Move(tempReport, finalReport, overwrite: true);

            WriteLine(stdout, new OptimizeResultLine(
                "result",
                "succeeded",
                finalOutput,
                finalReport,
                result.VmdCount,
                result.AtlasedMeshCount,
                result.GeneratedTextureCount,
                result.RemovedFileCount,
                result.SkippedMeshCount,
                elapsed.Elapsed.TotalSeconds,
                result.Warnings?.ToArray() ?? [],
                result.ConsumerPackCount,
                result.ExternallyUsedFileCount,
                result.FrozenFileCount), OptimizeJsonContext.Default.OptimizeResultLine);
            return OptimizeOutcome.Succeeded;
        }
        catch (OperationCanceledException)
        {
            DeleteTemps(tempOutput, tempReport);
            WriteLine(stdout, new OptimizeResultLine("result", "cancelled", finalOutput, finalReport, 0, 0, 0, 0, 0, elapsed.Elapsed.TotalSeconds), OptimizeJsonContext.Default.OptimizeResultLine);
            return OptimizeOutcome.Cancelled;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.ToString());
            Log.Error(exception, "Optimize failed");
            // Keep a report written for the failed run next to the target so the cause can be read.
            DeleteTemps(tempOutput, null);
            if (File.Exists(tempReport))
            {
                try
                {
                    File.Move(tempReport, finalReport, overwrite: true);
                }
                catch
                {
                    DeleteQuietly(tempReport);
                }
            }

            WriteLine(stdout, new OptimizeErrorLine("error", exception.Message, exception.GetType().Name), OptimizeJsonContext.Default.OptimizeErrorLine);
            return OptimizeOutcome.Failed;
        }
    }

    private static int Fail(TextWriter stdout, OptimizeOutcome outcome, string message, string exceptionType)
    {
        Console.Error.WriteLine($"{exceptionType}: {message}");
        WriteLine(stdout, new OptimizeErrorLine("error", message, exceptionType), OptimizeJsonContext.Default.OptimizeErrorLine);
        return OptimizeExitCodes.For(outcome);
    }

    private static void DeleteTemps(string output, string? report)
    {
        DeleteQuietly(output);
        DeleteQuietly(output + "_temp");
        if (report != null)
            DeleteQuietly(report);
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not delete '{path}': {exception.Message}");
        }
    }

    private static readonly object StdoutLock = new();

    private static void WriteLine<T>(TextWriter stdout, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var line = JsonSerializer.Serialize(value, typeInfo);
        lock (StdoutLock)
            stdout.WriteLine(line);
    }

    private static async Task MonitorParentAsync(int parentProcessId, CancellationTokenSource cancellation)
    {
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            await parent.WaitForExitAsync(cancellation.Token);
            cancellation.Cancel();
        }
        catch (ArgumentException)
        {
            cancellation.Cancel();
        }
        catch (InvalidOperationException)
        {
            cancellation.Cancel();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class ThrottledProgress(TextWriter stdout) : IProgress<PackOptimizerProgress>
    {
        private readonly Stopwatch _sinceLast = Stopwatch.StartNew();
        private string? _lastStage;

        public void Report(PackOptimizerProgress value)
        {
            var stageChanged = !string.Equals(_lastStage, value.Stage, StringComparison.Ordinal);
            var completed = value.Total > 0 && value.Current >= value.Total;
            if (!stageChanged && !completed && _sinceLast.ElapsedMilliseconds < ProgressIntervalMs)
                return;

            _lastStage = value.Stage;
            _sinceLast.Restart();
            WriteLine(stdout, OptimizeProgressLine.From(value), OptimizeJsonContext.Default.OptimizeProgressLine);
        }
    }
}
