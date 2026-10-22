using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Editors.PackOptimizer.Core;
using Serilog;
using Shared.Core.PackFiles.Models;
using Shared.Core.Settings;

namespace WH3AssetHost.Optimize;

/// <summary>
/// <c>WH3AssetHost check --request &lt;file.json&gt; [--parent-pid &lt;pid&gt;]</c>: runs the visual-asset check
/// of one pack and exits; nothing is optimized and no pack is written. Same line protocol as
/// <c>optimize</c>: stdout carries only JSON lines (<c>progress</c>, then one <c>check</c> or <c>error</c>
/// line); diagnostics go to stderr and the log file. Exit codes: 0 succeeded, 1 failed, 2 invalid request,
/// 3 cancelled.
/// </summary>
internal static class CheckMode
{
    public static int Run(string[] args)
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        Console.SetOut(Console.Error);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Console.Error.WriteLine($"Unhandled: {e.ExceptionObject}");

        var (requestPath, parentPid, argumentError) = OptimizeRequestReader.ParseArguments(args, "check");
        if (argumentError != null)
            return OptimizeMode.Fail(stdout, OptimizeOutcome.InvalidRequest, argumentError, "InvalidArguments");

        string requestJson;
        try
        {
            requestJson = File.ReadAllText(requestPath!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return OptimizeMode.Fail(stdout, OptimizeOutcome.InvalidRequest, $"The request file could not be read: {exception.Message}", exception.GetType().Name);
        }

        var request = CheckRequestReader.Parse(requestJson, out var parseError);
        if (parseError != null)
            return OptimizeMode.Fail(stdout, OptimizeOutcome.InvalidRequest, parseError, "InvalidRequest");

        var validationError = CheckRequestValidator.Validate(request);
        if (validationError != null)
            return OptimizeMode.Fail(stdout, OptimizeOutcome.InvalidRequest, validationError, "InvalidRequest");

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
            _ = OptimizeMode.MonitorParentAsync(parentPid.Value, cancellation);

        var nativeError = OptimizeMode.GetNativeLibraryError();
        if (nativeError != null)
            return OptimizeMode.Fail(stdout, OptimizeOutcome.Failed, nativeError, "NativeLibraryUnavailable");

        return OptimizeExitCodes.For(Execute(request!, stdout, cancellation.Token));
    }

    private static OptimizeOutcome Execute(CheckRequest request, TextWriter stdout, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var reportPath = string.IsNullOrWhiteSpace(request.ReportPath) ? string.Empty : Path.GetFullPath(request.ReportPath);
        var tempReport = reportPath.Length == 0 ? string.Empty : reportPath + ".checking";
        try
        {
            if (reportPath.Length != 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                OptimizeMode.DeleteQuietly(tempReport);
            }

            var dataFolder = OptimizeRequestValidator.ResolveDataFolder(request.GameDirectory)!;
            var vanilla = HostVanillaContainers.LoadVanilla(dataFolder, request.VanillaFileIndexPath, Console.Error.WriteLine);
            cancellationToken.ThrowIfCancellationRequested();
            var source = HostVanillaContainers.LoadSource(request.SourcePackPath);
            cancellationToken.ThrowIfCancellationRequested();

            var dependencies = new List<IPackFileContainer>();
            foreach (var dependencyPath in request.DependencyPackPaths ?? [])
            {
                dependencies.Add(HostVanillaContainers.LoadConsumer(dependencyPath));
                cancellationToken.ThrowIfCancellationRequested();
            }

            var result = PackOptimizer.Check(
                new PackOptimizerCheckRequest(
                    Path.GetFullPath(request.SourcePackPath),
                    source,
                    vanilla,
                    GameTypeEnum.Warhammer3,
                    request.CacheDirectory,
                    string.IsNullOrWhiteSpace(request.DbSchemaPath) ? null : PackOptimizerDbSchema.Load(request.DbSchemaPath),
                    dependencies),
                new OptimizeMode.ThrottledProgress(stdout),
                cancellationToken);

            if (result.Status == PackOptimizerStatus.Cancelled)
            {
                OptimizeMode.WriteLine(
                    stdout, CheckResultLine.From(result, string.Empty, elapsed.Elapsed.TotalSeconds), OptimizeJsonContext.Default.CheckResultLine);
                return OptimizeOutcome.Cancelled;
            }

            if (reportPath.Length != 0)
            {
                File.WriteAllText(tempReport, result.ReportText, Encoding.UTF8);
                File.Move(tempReport, reportPath, overwrite: true);
            }

            OptimizeMode.WriteLine(
                stdout, CheckResultLine.From(result, reportPath, elapsed.Elapsed.TotalSeconds), OptimizeJsonContext.Default.CheckResultLine);
            return OptimizeOutcome.Succeeded;
        }
        catch (OperationCanceledException)
        {
            if (tempReport.Length != 0)
                OptimizeMode.DeleteQuietly(tempReport);
            OptimizeMode.WriteLine(
                stdout,
                new CheckResultLine(
                    "check", "cancelled", AssetHostProtocol.HostVersion, [], new CheckSummaryLine(0, 0, 0, [], []),
                    string.Empty, elapsed.Elapsed.TotalSeconds, 0, 0, 0, 0),
                OptimizeJsonContext.Default.CheckResultLine);
            return OptimizeOutcome.Cancelled;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.ToString());
            Log.Error(exception, "Check failed");
            if (tempReport.Length != 0)
                OptimizeMode.DeleteQuietly(tempReport);
            OptimizeMode.WriteLine(
                stdout, new OptimizeErrorLine("error", exception.Message, exception.GetType().Name), OptimizeJsonContext.Default.OptimizeErrorLine);
            return OptimizeOutcome.Failed;
        }
    }
}
