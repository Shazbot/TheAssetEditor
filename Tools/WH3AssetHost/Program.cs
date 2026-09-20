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
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.WsModel;

namespace WH3AssetHost;

internal static class Program
{
    private static readonly Stopwatch ProcessLifetime = Stopwatch.StartNew();

    public static int Main(string[] args)
    {
        ConfigureFileLogging(args);
        Log.Debug(
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

            var logLevel = string.Equals(
                Environment.GetEnvironmentVariable("WH3ASSETHOST_LOG_LEVEL"),
                "Debug",
                StringComparison.OrdinalIgnoreCase)
                ? LogEventLevel.Debug
                : LogEventLevel.Information;

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Is(logLevel)
                .Enrich.FromLogContext()
                .Enrich.WithThreadId()
                .WriteTo.File(
                    Path.Combine(logDirectory, "WH3AssetHost-.log"),
                    restrictedToMinimumLevel: logLevel,
                    outputTemplate: outputTemplate,
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: 10 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
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

internal sealed class CurrentAssetModelResolverCache : IModelAssetResolver
{
    private readonly IModelAssetResolver _inner;
    private readonly Dictionary<string, ResolvedModelAsset> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public CurrentAssetModelResolverCache(IModelAssetResolver inner)
    {
        _inner = inner;
    }

    public int CachedAssetCount => _cache.Count;
    public long CacheHits { get; private set; }
    public long CacheMisses { get; private set; }

    public ResolvedModelAsset Resolve(PackFile inputFile)
    {
        ArgumentNullException.ThrowIfNull(inputFile);

        var cacheKey = NormalizeCacheKey(inputFile);
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            CacheHits++;
            return cached;
        }

        var resolved = _inner.Resolve(inputFile);
        _cache[cacheKey] = resolved;
        CacheMisses++;
        return resolved;
    }

    public ResolvedModelMaterials ResolveMaterials(
        RmvFile model,
        WsModelFile? wsModel = null,
        string? modelPath = null)
        => _inner.ResolveMaterials(model, wsModel, modelPath);

    public void Clear()
    {
        _cache.Clear();
        CacheHits = 0;
        CacheMisses = 0;
    }

    private static string NormalizeCacheKey(PackFile file)
        => (file.VirtualPath ?? file.Name).Replace('/', '\\').Trim().TrimStart('\\');
}

internal sealed class HeadlessExportRuntime : IAssetHostRuntime
{
    private readonly SkeletonAnimationLookUpHelper _skeletonLookup;
    private readonly IGltfAnimationCatalogResolver _animationCatalogResolver;
    private readonly CurrentAssetModelResolverCache _modelResolverCache;
    private readonly VariantMeshCompositionResolver _compositionResolver;
    private readonly GltfTextureHandler _textureHandler;
    private readonly RmvToGltfExporter _exporter;
    private string? _currentAssetSessionKey;

    private HeadlessExportRuntime(
        IHeadlessPackFileService packFileService,
        SkeletonAnimationLookUpHelper skeletonLookup,
        HeadlessGltfExportService exportService,
        IGltfAnimationCatalogResolver animationCatalogResolver,
        CurrentAssetModelResolverCache modelResolverCache,
        VariantMeshCompositionResolver compositionResolver,
        GltfTextureHandler textureHandler,
        RmvToGltfExporter exporter)
    {
        PackFileService = packFileService;
        _skeletonLookup = skeletonLookup;
        ExportService = exportService;
        _animationCatalogResolver = animationCatalogResolver;
        _modelResolverCache = modelResolverCache;
        _compositionResolver = compositionResolver;
        _textureHandler = textureHandler;
        _exporter = exporter;
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
        var metadataCacheableVanillaContainers = loadedPacks
            .Where(x => x.UsedVanillaFilesCache)
            .Select(x => x.Container)
            .ToHashSet();
        var packFileService = HeadlessPackFileServiceFactory.Create(
            loadedPacks.Select(x => x.Container));
        phaseStopwatch.Stop();
        var packServiceMs = phaseStopwatch.ElapsedMilliseconds;

        phaseStopwatch.Restart();
        var modelResolver = new CurrentAssetModelResolverCache(new ModelAssetResolver(packFileService));
        var compositionResolver = new VariantMeshCompositionResolver(
            packFileService,
            modelResolver,
            cacheParsedDefinitions: true);
        var skeletonLookup = new SkeletonAnimationLookUpHelper(
            packFileService,
            eventHub,
            new SkeletonAnimationLookupCacheOptions(
                GetAnimationIndexCacheDirectory(),
                vanillaPackContainers));
        var animationMetadataCacheOptions = new GltfAnimationMetadataLookupCacheOptions(
            GetAnimationMetadataCacheDirectory(),
            metadataCacheableVanillaContainers);
        var animationMetadataResolver = new GltfAnimationMetadataContextResolver(
            packFileService,
            animationMetadataCacheOptions);
        var animationCatalogResolver = new GltfAnimationCatalogResolver(
            modelResolver,
            compositionResolver,
            skeletonLookup,
            animationMetadataResolver);
        var imageSaveHandler = new SystemImageSaveHandler();
        var materialExporter = new DdsToMaterialPngExporter(packFileService, imageSaveHandler);
        var normalExporter = new DdsToNormalPngExporter(packFileService, imageSaveHandler);
        var textureHandler = new GltfTextureHandler(normalExporter, materialExporter, packFileService);
        var exporter = new RmvToGltfExporter(
            new HeadlessGltfSceneSaver(),
            new GltfMeshBuilder(),
            textureHandler,
            new GltfSkeletonBuilder(),
            new GltfAnimationBuilder(
                packFileService,
                animationMetadataCacheOptions,
                animationMetadataResolver),
            skeletonLookup,
            modelResolver,
            compositionResolver,
            missingSkeletonDecision ?? new HeadlessMissingSkeletonDecision(),
            cacheBuiltMeshes: true);
        var runtime = new HeadlessExportRuntime(
            packFileService,
            skeletonLookup,
            new HeadlessGltfExportService(exporter),
            animationCatalogResolver,
            modelResolver,
            compositionResolver,
            textureHandler,
            exporter);
        phaseStopwatch.Stop();
        var exportPipelineMs = phaseStopwatch.ElapsedMilliseconds;
        totalStopwatch.Stop();

        Log.ForContext<HeadlessExportRuntime>().Information(
            "Asset host runtime initialized in {TotalMs}ms: {PackCount} packs ({VanillaPackCount} vanilla, {MetadataCacheableVanillaPackCount} metadata-cacheable), packLoad={PackLoadMs}ms, packService={PackServiceMs}ms, exportPipeline={ExportPipelineMs}ms",
            totalStopwatch.ElapsedMilliseconds,
            loadedPacks.Count,
            vanillaPackContainers.Count,
            metadataCacheableVanillaContainers.Count,
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

    private static string GetAnimationMetadataCacheDirectory()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var cacheRoot = string.IsNullOrWhiteSpace(localApplicationData)
            ? Path.GetTempPath()
            : localApplicationData;
        return Path.Combine(cacheRoot, "WH3AssetHost", "AnimationMetadata");
    }

    private void EnsureAssetSession(string assetPath)
    {
        var sessionKey = assetPath.Replace('/', '\\').Trim().TrimStart('\\').ToLowerInvariant();
        if (string.Equals(_currentAssetSessionKey, sessionKey, StringComparison.Ordinal))
            return;

        if (_currentAssetSessionKey != null)
        {
            Log.ForContext<HeadlessExportRuntime>().Debug(
                "Clearing current-asset caches while switching from {PreviousAsset} to {AssetPath}: parsedModels={ParsedModelCount}, parsedVmdDefinitions={ParsedVmdDefinitionCount}, cachedBuiltMeshes={CachedBuiltMeshCount}, modelCacheHits={ModelCacheHits}, modelCacheMisses={ModelCacheMisses}, builtMeshCacheHits={BuiltMeshCacheHits}, builtMeshCacheMisses={BuiltMeshCacheMisses}",
                _currentAssetSessionKey,
                sessionKey,
                _modelResolverCache.CachedAssetCount,
                _compositionResolver.CachedDefinitionCount,
                _exporter.CachedBuiltMeshCount,
                _modelResolverCache.CacheHits,
                _modelResolverCache.CacheMisses,
                _exporter.BuiltMeshCacheHits,
                _exporter.BuiltMeshCacheMisses);
        }

        _modelResolverCache.Clear();
        _compositionResolver.ClearParsedDefinitionCache();
        _textureHandler.ClearConvertedTextureCache();
        _exporter.ClearBuiltMeshCache();
        _currentAssetSessionKey = sessionKey;
    }

    public AssetHostAnimationCatalog GetAnimationCatalog(string assetPath)
    {
        EnsureAssetSession(assetPath);
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
        var loadedContainers = PackFileService.GetAllPackfileContainers();
        var animations = catalog.Selections
            .Where(selection => string.IsNullOrWhiteSpace(selection.AnimationFile) == false)
            .Select(selection => new AssetHostAnimationReference(
                selection.AnimationFile,
                GetPackIndex(loadedContainers, selection.Container),
                selection.FragmentPath,
                selection.MetadataPath))
            .OrderBy(animation => animation.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(animation => animation.PackIndex)
            .ThenBy(animation => animation.FragmentPath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(animation => animation.MetadataPath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
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
        EnsureAssetSession(request.AssetPath);
        var modelCacheHitsBefore = _modelResolverCache.CacheHits;
        var modelCacheMissesBefore = _modelResolverCache.CacheMisses;
        var builtMeshCacheHitsBefore = _exporter.BuiltMeshCacheHits;
        var builtMeshCacheMissesBefore = _exporter.BuiltMeshCacheMisses;
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
        var metadataSelections = new List<GltfAnimationMetadataSelection?>();
        for (var animationIndex = 0; animationIndex < request.AnimationPaths.Count; animationIndex++)
        {
            var animationPath = request.AnimationPaths[animationIndex];
            var requestedSelection = request.AnimationSelections != null
                && request.AnimationSelections.Count > animationIndex
                ? request.AnimationSelections[animationIndex]
                : null;
            IPackFileContainer? selectedContainer = null;
            if (requestedSelection?.PackIndex is int packIndex)
            {
                var containers = PackFileService.GetAllPackfileContainers();
                if ((uint)packIndex >= (uint)containers.Count)
                    return Failure(
                        "AnimationPackNotFound",
                        $"Animation pack index {packIndex} for '{animationPath}' is no longer available.");
                selectedContainer = containers[packIndex];
            }

            var animation = PackFileService.FindFile(animationPath, selectedContainer);
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
            metadataSelections.Add(requestedSelection?.FragmentPath == null
                ? null
                : new GltfAnimationMetadataSelection(
                    requestedSelection.FragmentPath,
                    requestedSelection.MetadataPath));
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
            AnimationMetadataSelections = metadataSelections,
            VariantMeshSelections = request.VariantSelections?
                .Select(selection => new VariantMeshSelection(selection.SlotPath, selection.ChoiceIndex))
                .ToList() ?? [],
            // The masks are auxiliary files and are not referenced by the
            // glTF scene. The mod-manager render only needs embedded material
            // channels, so avoid converting and inverting them.
            ExportAuxiliaryMasks = false,
            // WHMM has a dedicated raw RGBA + Zstd KTX2 compatibility loader
            // for this low-latency headless preview format.
            UseKtx2Textures = true,
            // DDS decode, channel conversion and Zstd are independent per
            // texture. Bound parallelism so previews use available CPU cores
            // without letting a texture-heavy model monopolize the machine.
            MaxTextureParallelism = Math.Min(4, Math.Max(1, Environment.ProcessorCount))
        };

        phaseStopwatch.Restart();
        var result = ExportService.Export(settings);
        phaseStopwatch.Stop();
        var exportMs = phaseStopwatch.ElapsedMilliseconds;
        totalStopwatch.Stop();

        Log.ForContext<HeadlessExportRuntime>().Information(
            "Asset host export completed in {TotalMs}ms for {AssetPath}: success={Success}, assetLookup={AssetLookupMs}ms, animationLookup={AnimationLookupMs}ms, gltfExport={ExportMs}ms, animations={AnimationCount}, variantSelections={VariantSelectionCount}, materials={ExportMaterials}, skeleton={IncludeSkeleton}, modelCacheHits={ModelCacheHits}, modelCacheMisses={ModelCacheMisses}, builtMeshCacheHits={BuiltMeshCacheHits}, builtMeshCacheMisses={BuiltMeshCacheMisses}, cachedModels={CachedModelCount}, cachedVmdDefinitions={CachedVmdDefinitionCount}, cachedBuiltMeshes={CachedBuiltMeshCount}",
            totalStopwatch.ElapsedMilliseconds,
            request.AssetPath,
            result.Success,
            assetLookupMs,
            animationLookupMs,
            exportMs,
            animationFiles.Count,
            request.VariantSelections?.Count ?? 0,
            request.ExportMaterials,
            request.IncludeSkeleton,
            _modelResolverCache.CacheHits - modelCacheHitsBefore,
            _modelResolverCache.CacheMisses - modelCacheMissesBefore,
            _exporter.BuiltMeshCacheHits - builtMeshCacheHitsBefore,
            _exporter.BuiltMeshCacheMisses - builtMeshCacheMissesBefore,
            _modelResolverCache.CachedAssetCount,
            _compositionResolver.CachedDefinitionCount,
            _exporter.CachedBuiltMeshCount);

        return result;
    }

    public AssetHostPaintedVariantResult ExportPaintedVariant(AssetHostPaintedVariantRequest request)
    {
        EnsureAssetSession(request.AssetPath);
        var stopwatch = Stopwatch.StartNew();
        var result = new PaintedVariantExporter(PackFileService, _compositionResolver).Export(request);
        stopwatch.Stop();

        Log.ForContext<HeadlessExportRuntime>().Information(
            "Painted variant export completed in {ElapsedMs}ms for {AssetPath}: success={Success}, textures={TextureCount}, files={FileCount}",
            stopwatch.ElapsedMilliseconds,
            request.AssetPath,
            result.Success,
            request.Textures.Count,
            result.Files.Count);

        return result;
    }

    private static ExportResult Failure(string code, string message)
        => new(
            false,
            null,
            Array.Empty<string>(),
            Array.Empty<ExportWarning>(),
            [new ExportError(code, message)]);

    private static int? GetPackIndex(
        IReadOnlyList<IPackFileContainer> containers,
        IPackFileContainer selected)
    {
        for (var index = 0; index < containers.Count; index++)
        {
            if (ReferenceEquals(containers[index], selected))
                return index;
        }

        return null;
    }

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
