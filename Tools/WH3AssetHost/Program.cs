using System.Diagnostics;
using System.Text.Json;
using Serilog;
using Serilog.Events;
using Editors.ImportExport;
using Editors.ImportExport.Exporting.Exporters.DdsToMaterialPng;
using Editors.ImportExport.Exporting.Exporters.DdsToNormalPng;
using Editors.ImportExport.Exporting.Exporters.RmvToGltf;
using Editors.ImportExport.Exporting.Exporters.RmvToGltf.Helpers;
using GameWorld.Core.Services;
using Shared.Core.Events;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Utility;

namespace WH3AssetHost;

internal static class Program
{
    private static readonly Stopwatch ProcessLifetime = Stopwatch.StartNew();

    public static int Main(string[] args)
    {
        ConfigureFileLogging(args);
        Log.Information(
            "Asset host timing: logging initialized {ElapsedMs}ms after process entry",
            ProcessLifetime.ElapsedMilliseconds);

        try
        {
            if (args.Length > 0 && string.Equals(args[0], "serve", StringComparison.OrdinalIgnoreCase))
                return RunServe(args);

            var request = CliRequest.Parse(args);
            if (request.Error != null)
                return WriteFailure(2, "InvalidArguments", request.Error);

            using var runtime = HeadlessExportRuntime.Create(request.PackPaths);
            var result = runtime.ExportModel(new AssetHostExportRequest(
                request.AssetPath,
                Path.GetFullPath(request.OutputPath),
                request.AnimationPaths,
                request.ExportMaterials,
                request.IncludeSkeleton,
                request.MirrorMesh));
            Console.Out.WriteLine(JsonSerializer.Serialize(result, AssetHostJsonContext.Default.ExportResult));
            if (result.Success)
                return 0;

            WriteDiagnostics(result);
            return 4;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.ToString());
            return WriteFailure(3, "InitializationFailed", exception.Message);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void ConfigureFileLogging(string[] args)
    {
        try
        {
            var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var logRoot = string.IsNullOrWhiteSpace(localApplicationData)
                ? Path.GetTempPath()
                : localApplicationData;
            var logDirectory = Path.Combine(logRoot, "WH3AssetHost", "Logs");
            Directory.CreateDirectory(logDirectory);

            var outputTemplate =
                "[{Timestamp:HH:mm:ss} {Level}] [{ThreadId}] {SourceContext}::{MemberName} : {Message} {Exception}{NewLine}";

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.FromLogContext()
                .Enrich.WithThreadId()
                .WriteTo.File(
                    Path.Combine(logDirectory, "WH3AssetHost-.log"),
                    restrictedToMinimumLevel: LogEventLevel.Information,
                    outputTemplate: outputTemplate,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    shared: true)
                .CreateLogger();

            Log.Information(
                "WH3AssetHost starting in {Mode} mode. Log directory: {LogDirectory}",
                args.Length > 0 ? args[0] : "unknown",
                logDirectory);
        }
        catch
        {
            // Logging is diagnostic-only and must never prevent the host from starting.
        }
    }

    private static int RunServe(string[] args)
    {
        var request = ServeCliRequest.Parse(args);
        if (request.Error != null)
            return WriteFailure(2, "InvalidArguments", request.Error);

        // The serve protocol is carried only by the named pipe. This also
        // prevents a future logger configured by a referenced library from
        // corrupting a caller's stdout capture.
        Console.SetOut(TextWriter.Null);
        try
        {
            var server = new AssetHostPipeServer(new HeadlessExportRuntimeFactory());
            server.RunAsync(request.PipeName, request.ParentProcessId).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.ToString());
            return 3;
        }
    }

    private static int WriteFailure(int exitCode, string code, string message)
    {
        Console.Error.WriteLine($"{code}: {message}");
        var result = new ExportResult(
            false,
            null,
            Array.Empty<string>(),
            Array.Empty<ExportWarning>(),
            [new ExportError(code, message)]);
        Console.Out.WriteLine(JsonSerializer.Serialize(result, AssetHostJsonContext.Default.ExportResult));
        return exitCode;
    }

    private static void WriteDiagnostics(ExportResult result)
    {
        foreach (var warning in result.Warnings)
            Console.Error.WriteLine($"warning {warning.Code}: {warning.Message}");
        foreach (var error in result.Errors)
            Console.Error.WriteLine($"error {error.Code}: {error.Message}");
    }
}

internal sealed class HeadlessExportRuntime : IAssetHostRuntime
{
    private readonly SkeletonAnimationLookUpHelper _skeletonLookup;
    private readonly IGltfAnimationCatalogResolver _animationCatalogResolver;

    private HeadlessExportRuntime(
        IHeadlessPackFileService packFileService,
        SkeletonAnimationLookUpHelper skeletonLookup,
        HeadlessGltfExportService exportService,
        IGltfAnimationCatalogResolver animationCatalogResolver)
    {
        PackFileService = packFileService;
        _skeletonLookup = skeletonLookup;
        ExportService = exportService;
        _animationCatalogResolver = animationCatalogResolver;
    }

    public IHeadlessPackFileService PackFileService { get; }
    public HeadlessGltfExportService ExportService { get; }

    public static HeadlessExportRuntime Create(
        IReadOnlyList<string> packPaths,
        string? outputRoot = null,
        string? vanillaPackFilesCachePath = null,
        IMissingSkeletonDecision? missingSkeletonDecision = null)
    {
        if (packPaths.Count == 0)
            throw new InvalidOperationException("At least one --pack path is required.");

        var totalStopwatch = Stopwatch.StartNew();
        var phaseStopwatch = Stopwatch.StartNew();

        var eventHub = new NoOpGlobalEventHub();
        var loader = new HeadlessPackFileLoader(vanillaPackFilesCachePath);
        var loadedPacks = loader.LoadOrderedWithMetadata(packPaths);
        phaseStopwatch.Stop();
        var packLoadMs = phaseStopwatch.ElapsedMilliseconds;

        phaseStopwatch.Restart();
        var vanillaPackContainers = loadedPacks
            .Where(x => x.IsVanillaPack)
            .Select(x => x.Container)
            .ToHashSet();
        var packFileService = HeadlessPackFileServiceFactory.Create(
            loadedPacks.Select(x => x.Container));
        phaseStopwatch.Stop();
        var packServiceMs = phaseStopwatch.ElapsedMilliseconds;

        phaseStopwatch.Restart();
        var modelResolver = new ModelAssetResolver(packFileService);
        var compositionResolver = new VariantMeshCompositionResolver(packFileService, modelResolver);
        var skeletonLookup = new SkeletonAnimationLookUpHelper(
            packFileService,
            eventHub,
            new SkeletonAnimationLookupCacheOptions(
                GetAnimationIndexCacheDirectory(),
                vanillaPackContainers));
        var animationCatalogResolver = new GltfAnimationCatalogResolver(
            modelResolver,
            compositionResolver,
            skeletonLookup);
        var imageSaveHandler = new SystemImageSaveHandler();
        var materialExporter = new DdsToMaterialPngExporter(packFileService, imageSaveHandler);
        var normalExporter = new DdsToNormalPngExporter(packFileService, imageSaveHandler);
        var exporter = new RmvToGltfExporter(
            new HeadlessGltfSceneSaver(),
            new GltfMeshBuilder(),
            new GltfTextureHandler(normalExporter, materialExporter, packFileService),
            new GltfSkeletonBuilder(),
            new GltfAnimationBuilder(),
            skeletonLookup,
            modelResolver,
            compositionResolver,
            missingSkeletonDecision ?? new HeadlessMissingSkeletonDecision());
        var runtime = new HeadlessExportRuntime(
            packFileService,
            skeletonLookup,
            new HeadlessGltfExportService(exporter),
            animationCatalogResolver);
        phaseStopwatch.Stop();
        var exportPipelineMs = phaseStopwatch.ElapsedMilliseconds;
        totalStopwatch.Stop();

        Log.ForContext<HeadlessExportRuntime>().Information(
            "Asset host runtime initialized in {TotalMs}ms: {PackCount} packs ({VanillaPackCount} vanilla), packLoad={PackLoadMs}ms, packService={PackServiceMs}ms, exportPipeline={ExportPipelineMs}ms",
            totalStopwatch.ElapsedMilliseconds,
            loadedPacks.Count,
            vanillaPackContainers.Count,
            packLoadMs,
            packServiceMs,
            exportPipelineMs);

        return runtime;
    }

    private static string GetAnimationIndexCacheDirectory()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var cacheRoot = string.IsNullOrWhiteSpace(localApplicationData)
            ? Path.GetTempPath()
            : localApplicationData;
        return Path.Combine(cacheRoot, "WH3AssetHost", "AnimationIndex");
    }

    public AssetHostAnimationCatalog GetAnimationCatalog(string assetPath)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var phaseStopwatch = Stopwatch.StartNew();

        var inputModel = PackFileService.FindFile(assetPath);
        phaseStopwatch.Stop();
        var assetLookupMs = phaseStopwatch.ElapsedMilliseconds;
        if (inputModel == null)
        {
            totalStopwatch.Stop();
            Log.ForContext<HeadlessExportRuntime>().Information(
                "Asset host animation catalog completed in {TotalMs}ms for {AssetPath}: success=false, assetLookup={AssetLookupMs}ms",
                totalStopwatch.ElapsedMilliseconds,
                assetPath,
                assetLookupMs);

            return new AssetHostAnimationCatalog(
                false,
                assetPath,
                null,
                false,
                [],
                [$"Asset '{assetPath}' was not found in the supplied packs."]);
        }

        phaseStopwatch.Restart();
        var catalog = _animationCatalogResolver.Resolve(inputModel);
        phaseStopwatch.Stop();
        var resolveMs = phaseStopwatch.ElapsedMilliseconds;

        phaseStopwatch.Restart();
        var animations = catalog.Animations
            .Select(animation => animation.AnimationFile)
            .Where(path => string.IsNullOrWhiteSpace(path) == false)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new AssetHostAnimationReference(path))
            .ToList();
        phaseStopwatch.Stop();
        var materializeMs = phaseStopwatch.ElapsedMilliseconds;
        totalStopwatch.Stop();

        Log.ForContext<HeadlessExportRuntime>().Information(
            "Asset host animation catalog completed in {TotalMs}ms for {AssetPath}: success=true, assetLookup={AssetLookupMs}ms, resolve={ResolveMs}ms, materialize={MaterializeMs}ms, animations={AnimationCount}, diagnostics={DiagnosticCount}",
            totalStopwatch.ElapsedMilliseconds,
            assetPath,
            assetLookupMs,
            resolveMs,
            materializeMs,
            animations.Count,
            catalog.Diagnostics.Count);

        return new AssetHostAnimationCatalog(
            true,
            assetPath,
            catalog.SkeletonName,
            catalog.HasSkeletonFile,
            animations,
            catalog.Diagnostics);
    }

    public ExportResult ExportModel(AssetHostExportRequest request)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var phaseStopwatch = Stopwatch.StartNew();

        var inputModel = PackFileService.FindFile(request.AssetPath);
        phaseStopwatch.Stop();
        var assetLookupMs = phaseStopwatch.ElapsedMilliseconds;
        if (inputModel == null)
        {
            totalStopwatch.Stop();
            Log.ForContext<HeadlessExportRuntime>().Information(
                "Asset host export completed in {TotalMs}ms for {AssetPath}: success=false, assetLookup={AssetLookupMs}ms, reason=AssetNotFound",
                totalStopwatch.ElapsedMilliseconds,
                request.AssetPath,
                assetLookupMs);
            return Failure(
                "AssetNotFound",
                $"Asset '{request.AssetPath}' was not found in the supplied packs.");
        }

        phaseStopwatch.Restart();
        var animationFiles = new List<PackFile>();
        foreach (var animationPath in request.AnimationPaths)
        {
            var animation = PackFileService.FindFile(animationPath);
            if (animation == null)
            {
                phaseStopwatch.Stop();
                totalStopwatch.Stop();
                Log.ForContext<HeadlessExportRuntime>().Information(
                    "Asset host export completed in {TotalMs}ms for {AssetPath}: success=false, assetLookup={AssetLookupMs}ms, animationLookup={AnimationLookupMs}ms, reason=AnimationNotFound",
                    totalStopwatch.ElapsedMilliseconds,
                    request.AssetPath,
                    assetLookupMs,
                    phaseStopwatch.ElapsedMilliseconds);
                return Failure(
                    "AnimationNotFound",
                    $"Animation '{animationPath}' was not found in the supplied packs.");
            }
            animationFiles.Add(animation);
        }
        phaseStopwatch.Stop();
        var animationLookupMs = phaseStopwatch.ElapsedMilliseconds;

        var settings = new RmvToGltfExporterSettings(
            inputModel,
            animationFiles,
            Path.GetFullPath(request.OutputPath),
            request.ExportMaterials,
            ConvertMaterialTextureToBlender: true,
            ConvertNormalTextureToBlue: true,
            ExportAnimations: animationFiles.Count > 0,
            MirrorMesh: request.MirrorMesh)
        {
            IncludeSkeleton = request.IncludeSkeleton,
            VariantMeshSelections = request.VariantSelections?
                .Select(selection => new VariantMeshSelection(selection.SlotPath, selection.ChoiceIndex))
                .ToList() ?? [],
            // The masks are auxiliary files and are not referenced by the
            // glTF scene. The mod-manager render only needs embedded material
            // channels, so avoid converting and inverting them.
            ExportAuxiliaryMasks = false
        };

        phaseStopwatch.Restart();
        var result = ExportService.Export(settings);
        phaseStopwatch.Stop();
        var exportMs = phaseStopwatch.ElapsedMilliseconds;
        totalStopwatch.Stop();

        Log.ForContext<HeadlessExportRuntime>().Information(
            "Asset host export completed in {TotalMs}ms for {AssetPath}: success={Success}, assetLookup={AssetLookupMs}ms, animationLookup={AnimationLookupMs}ms, gltfExport={ExportMs}ms, animations={AnimationCount}, variantSelections={VariantSelectionCount}, materials={ExportMaterials}, skeleton={IncludeSkeleton}",
            totalStopwatch.ElapsedMilliseconds,
            request.AssetPath,
            result.Success,
            assetLookupMs,
            animationLookupMs,
            exportMs,
            animationFiles.Count,
            request.VariantSelections?.Count ?? 0,
            request.ExportMaterials,
            request.IncludeSkeleton);

        return result;
    }

    private static ExportResult Failure(string code, string message)
        => new(
            false,
            null,
            Array.Empty<string>(),
            Array.Empty<ExportWarning>(),
            [new ExportError(code, message)]);

    public void Dispose() => _skeletonLookup.Dispose();
}

internal sealed class HeadlessExportRuntimeFactory : IAssetHostRuntimeFactory, IAssetHostInteractiveRuntimeFactory
{
    public IAssetHostRuntime Create(
        IReadOnlyList<string> packPaths,
        string outputRoot,
        string? vanillaPackFilesCachePath = null)
    {
        Directory.CreateDirectory(outputRoot);
        return HeadlessExportRuntime.Create(packPaths, outputRoot, vanillaPackFilesCachePath);
    }

    public IAssetHostRuntime Create(
        IReadOnlyList<string> packPaths,
        string outputRoot,
        string? vanillaPackFilesCachePath,
        IMissingSkeletonDecision missingSkeletonDecision)
    {
        Directory.CreateDirectory(outputRoot);
        return HeadlessExportRuntime.Create(
            packPaths,
            outputRoot,
            vanillaPackFilesCachePath,
            missingSkeletonDecision);
    }
}

internal sealed class NoOpGlobalEventHub : IGlobalEventHub
{
    public void PublishGlobalEvent<T>(T e) { }
    public void Register<T>(object owner, Action<T> action) { }
    public void UnRegister(object owner) { }
}

public sealed record CliRequest(
    IReadOnlyList<string> PackPaths,
    string AssetPath,
    string OutputPath,
    IReadOnlyList<string> AnimationPaths,
    bool ExportMaterials,
    bool IncludeSkeleton,
    bool MirrorMesh,
    string? Error)
{
    public static CliRequest Parse(string[] args)
    {
        var packs = new List<string>();
        var animations = new List<string>();
        string? asset = null;
        string? output = null;
        var exportMaterials = true;
        var includeSkeleton = true;
        var mirrorMesh = true;

        if (args.Length == 0 || !string.Equals(args[0], "export", StringComparison.OrdinalIgnoreCase))
            return Invalid("Usage: WH3AssetHost export --pack <path> --asset <virtual-path> --output <path> [--animation <virtual-path>] [--no-materials] [--no-skeleton] [--no-mirror]");

        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--pack":
                    if (!TryReadValue(args, ref index, out var pack)) return Invalid("--pack requires a path.");
                    packs.Add(pack);
                    break;
                case "--asset":
                    if (!TryReadValue(args, ref index, out asset)) return Invalid("--asset requires a virtual path.");
                    break;
                case "--output":
                    if (!TryReadValue(args, ref index, out output)) return Invalid("--output requires a path.");
                    break;
                case "--animation":
                    if (!TryReadValue(args, ref index, out var animation)) return Invalid("--animation requires a virtual path.");
                    animations.Add(animation);
                    break;
                case "--no-materials":
                    exportMaterials = false;
                    break;
                case "--no-skeleton":
                    includeSkeleton = false;
                    break;
                case "--no-mirror":
                    mirrorMesh = false;
                    break;
                default:
                    return Invalid($"Unknown argument '{argument}'.");
            }
        }

        if (packs.Count == 0) return Invalid("At least one --pack is required.");
        if (string.IsNullOrWhiteSpace(asset)) return Invalid("--asset is required.");
        if (string.IsNullOrWhiteSpace(output)) return Invalid("--output is required.");
        var extension = Path.GetExtension(output);
        if (!extension.Equals(".glb", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase))
            return Invalid("--output must end in .glb or .gltf.");

        return new CliRequest(packs, asset, output, animations, exportMaterials, includeSkeleton, mirrorMesh, null);
    }

    private static CliRequest Invalid(string error)
        => new([], string.Empty, string.Empty, [], true, true, true, error);

    private static bool TryReadValue(string[] args, ref int index, out string value)
    {
        if (index + 1 < args.Length
            && string.IsNullOrWhiteSpace(args[index + 1]) == false
            && args[index + 1].StartsWith("--", StringComparison.Ordinal) == false)
        {
            value = args[++index];
            return true;
        }

        value = string.Empty;
        return false;
    }
}

public sealed record ServeCliRequest(string PipeName, int? ParentProcessId, string? Error)
{
    public static ServeCliRequest Parse(string[] args)
    {
        string? pipeName = null;
        int? parentProcessId = null;
        if (args.Length == 0 || !string.Equals(args[0], "serve", StringComparison.OrdinalIgnoreCase))
            return Invalid("Usage: WH3AssetHost serve --pipe <name> [--parent-pid <pid>]");

        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--pipe":
                    if (!TryReadValue(args, ref index, out var value))
                        return Invalid("--pipe requires a name.");
                    pipeName = value;
                    break;
                case "--parent-pid":
                    if (!TryReadValue(args, ref index, out var pidValue)
                        || !int.TryParse(pidValue, out var parsedPid)
                        || parsedPid <= 0)
                    {
                        return Invalid("--parent-pid requires a positive integer.");
                    }
                    parentProcessId = parsedPid;
                    break;
                default:
                    return Invalid($"Unknown argument '{args[index]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(pipeName))
            return Invalid("--pipe is required.");
        if (!parentProcessId.HasValue)
            return Invalid("--parent-pid is required for production serve mode.");
        return new ServeCliRequest(pipeName, parentProcessId, null);
    }

    private static ServeCliRequest Invalid(string error) => new(string.Empty, null, error);

    private static bool TryReadValue(string[] args, ref int index, out string value)
    {
        if (index + 1 < args.Length
            && string.IsNullOrWhiteSpace(args[index + 1]) == false
            && args[index + 1].StartsWith("--", StringComparison.Ordinal) == false)
        {
            value = args[++index];
            return true;
        }

        value = string.Empty;
        return false;
    }
}
