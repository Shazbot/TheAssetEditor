using Editors.ImportExport.Exporting.Exporters.RmvToGltf;
using WH3AssetHost;

namespace Test.ImportExport.Exporting;

public sealed class AssetHostDispatcherTests
{
    [Test]
    public void Hello_ReturnsCapabilitiesForProtocolV1()
    {
        using var dispatcher = new AssetHostDispatcher(new FakeRuntimeFactory());

        var response = dispatcher.Dispatch(Request("hello", "hello-1"));

        Assert.That(response.Success, Is.True);
        Assert.That(response.Error, Is.Null);
        Assert.That(response.Command, Is.EqualTo("hello"));
        var resultJson = System.Text.Json.JsonSerializer.Serialize(response.Result);
        Assert.That(resultJson, Does.Contain("exportModel"));
        Assert.That(resultJson, Does.Contain("exportPaintedVariant"));
    }

    [Test]
    public void Dispatch_RejectsProtocolMismatchAndUnknownCommand()
    {
        using var dispatcher = new AssetHostDispatcher(new FakeRuntimeFactory());

        var mismatch = dispatcher.Dispatch("{\"protocolVersion\":99,\"requestId\":\"r1\",\"command\":\"hello\"}");
        var unknown = dispatcher.Dispatch(Request("doesNotExist", "r2"));

        Assert.That(mismatch.Error!.Code, Is.EqualTo("ProtocolVersionMismatch"));
        Assert.That(unknown.Error!.Code, Is.EqualTo("UnknownCommand"));
    }

    [Test]
    public void Dispatch_RejectsMalformedJsonAndExportBeforeInitialize()
    {
        using var dispatcher = new AssetHostDispatcher(new FakeRuntimeFactory());

        var malformed = dispatcher.Dispatch("{not-json");
        var beforeInitialize = dispatcher.Dispatch(Request("exportModel", "r1", "assetPath", "model.rigid_model_v2", "outputPath", "model.glb"));

        Assert.That(malformed.Error!.Code, Is.EqualTo("MalformedJson"));
        Assert.That(beforeInitialize.Error!.Code, Is.EqualTo("NotInitialized"));
    }

    [Test]
    public void Export_RejectsAbsoluteAndTraversalOutputPaths()
    {
        var factory = new FakeRuntimeFactory();
        using var dispatcher = new AssetHostDispatcher(factory);
        var root = Path.Combine(Path.GetTempPath(), "asset-host-root", Guid.NewGuid().ToString("N"));
        dispatcher.Dispatch(Initialize(root, "base.pack"));

        var traversal = dispatcher.Dispatch(Request(
            "exportModel",
            "r1",
            "assetPath", "model.rigid_model_v2",
            "outputPath", "..\\outside.glb"));
        var absolute = dispatcher.Dispatch(Request(
            "exportModel",
            "r2",
            "assetPath", "model.rigid_model_v2",
            "outputPath", Path.Combine(root, "absolute.glb")));

        Assert.That(traversal.Error!.Code, Is.EqualTo("InvalidOutputPath"));
        Assert.That(absolute.Error!.Code, Is.EqualTo("InvalidOutputPath"));
        Assert.That(factory.Created[0].Exports, Is.Empty);
    }

    [Test]
    public void Initialize_ReplacesRuntimeOnlyAfterSuccess_AndShutdownDisposesIt()
    {
        var factory = new FakeRuntimeFactory();
        using var dispatcher = new AssetHostDispatcher(factory);
        var root = Path.Combine(Path.GetTempPath(), "asset-host-root", Guid.NewGuid().ToString("N"));

        var first = dispatcher.Dispatch(Initialize(root, "base.pack"));
        var export = dispatcher.Dispatch(Request(
            "exportModel",
            "export-1",
            "assetPath", "model.rigid_model_v2",
            "outputPath", "nested/model.glb",
            "animationPaths", Array.Empty<string>()));
        var second = dispatcher.Dispatch(Initialize(root, "mod.pack"));
        var shutdown = dispatcher.Dispatch(Request("shutdown", "shutdown-1"));

        Assert.That(first.Success, Is.True);
        Assert.That(export.Success, Is.True);
        Assert.That(factory.Created, Has.Count.EqualTo(2));
        Assert.That(factory.Created[0].Disposed, Is.True);
        Assert.That(second.Success, Is.True);
        Assert.That(shutdown.Success, Is.True);
        Assert.That(factory.Created[1].Disposed, Is.True);
        Assert.That(dispatcher.ShutdownRequested, Is.True);
    }

    [Test]
    public void Initialize_FailedReplacementLeavesPreviousRuntimeUsable()
    {
        var factory = new FakeRuntimeFactory();
        using var dispatcher = new AssetHostDispatcher(factory);
        var root = Path.Combine(Path.GetTempPath(), "asset-host-root", Guid.NewGuid().ToString("N"));

        var first = dispatcher.Dispatch(Initialize(root, "base.pack"));
        var firstRuntime = factory.Created.Single();
        factory.FailNextCreate = true;

        var failedReplacement = dispatcher.Dispatch(Initialize(root, "broken.pack"));
        var export = dispatcher.Dispatch(Request(
            "exportModel",
            "export-after-failed-init",
            "assetPath", "model.rigid_model_v2",
            "outputPath", "model.glb"));

        Assert.That(first.Success, Is.True);
        Assert.That(failedReplacement.Error!.Code, Is.EqualTo("InitializationFailed"));
        Assert.That(firstRuntime.Disposed, Is.False);
        Assert.That(export.Success, Is.True);
        Assert.That(firstRuntime.Exports, Has.Count.EqualTo(1));
    }

    [Test]
    public void Export_RejectsNonBooleanOptions()
    {
        var factory = new FakeRuntimeFactory();
        using var dispatcher = new AssetHostDispatcher(factory);
        var root = Path.Combine(Path.GetTempPath(), "asset-host-root", Guid.NewGuid().ToString("N"));
        dispatcher.Dispatch(Initialize(root, "base.pack"));

        var response = dispatcher.Dispatch(Request(
            "exportModel",
            "invalid-options",
            "assetPath", "model.rigid_model_v2",
            "outputPath", "model.glb",
            "includeSkeleton", "yes"));

        Assert.That(response.Error!.Code, Is.EqualTo("InvalidExportOptions"));
        Assert.That(factory.Created[0].Exports, Is.Empty);
    }

    [Test]
    public void Export_PassesVariantMeshSelectionsToRuntime()
    {
        var factory = new FakeRuntimeFactory();
        using var dispatcher = new AssetHostDispatcher(factory);
        var root = Path.Combine(Path.GetTempPath(), "asset-host-root", Guid.NewGuid().ToString("N"));
        dispatcher.Dispatch(Initialize(root, "base.pack"));

        var response = dispatcher.Dispatch(Request(
            "exportModel",
            "variant-selection",
            "assetPath", "unit.variantmeshdefinition",
            "outputPath", "unit.glb",
            "variantSelections", new[]
            {
                new { slotPath = "root/slot[0]", choiceIndex = 4 },
                new { slotPath = "root/slot[2]/choice[0]/slot[0]", choiceIndex = 1 }
            }));

        Assert.That(response.Success, Is.True);
        Assert.That(factory.Created.Single().Exports.Single().VariantSelections, Is.EqualTo(new[]
        {
            new AssetHostVariantMeshSelection("root/slot[0]", 4),
            new AssetHostVariantMeshSelection("root/slot[2]/choice[0]/slot[0]", 1)
        }));
    }

    [Test]
    public void ExportBatch_PassesAllVariantSelectionsToOneRuntime()
    {
        var factory = new FakeRuntimeFactory();
        using var dispatcher = new AssetHostDispatcher(factory);
        var root = Path.Combine(Path.GetTempPath(), "asset-host-root", Guid.NewGuid().ToString("N"));
        dispatcher.Dispatch(Initialize(root, "base.pack"));

        var response = dispatcher.Dispatch(Request(
            "exportModelBatch",
            "variant-batch",
            "assetPath", "unit.variantmeshdefinition",
            "animationPaths", Array.Empty<string>(),
            "items", new object[]
            {
                new
                {
                    outputPath = "batch/one.glb",
                    variantSelections = new[] { new { slotPath = "root/slot[0]", choiceIndex = 0 } }
                },
                new
                {
                    outputPath = "batch/two.glb",
                    variantSelections = new[] { new { slotPath = "root/slot[0]", choiceIndex = 1 } }
                }
            }));

        Assert.That(response.Success, Is.True);
        Assert.That(response.Command, Is.EqualTo("exportModelBatch"));
        Assert.That(factory.Created.Single().Exports, Has.Count.EqualTo(2));
        Assert.That(factory.Created.Single().Exports[0].VariantSelections, Is.EqualTo(new[]
        {
            new AssetHostVariantMeshSelection("root/slot[0]", 0)
        }));
        Assert.That(factory.Created.Single().Exports[1].VariantSelections, Is.EqualTo(new[]
        {
            new AssetHostVariantMeshSelection("root/slot[0]", 1)
        }));
    }

    [Test]
    public void ExportPaintedVariant_PassesValidatedPathsAndSelectionsToRuntime()
    {
        var factory = new FakeRuntimeFactory();
        using var dispatcher = new AssetHostDispatcher(factory);
        var root = Path.Combine(Path.GetTempPath(), "asset-host-root", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "painted", "input"));
        File.WriteAllBytes(
            Path.Combine(root, "painted", "input", "body.png"),
            [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        dispatcher.Dispatch(Initialize(root, "base.pack"));

        var response = dispatcher.Dispatch(Request(
            "exportPaintedVariant",
            "painted-1",
            "assetPath", "unit.variantmeshdefinition",
            "outputDirectory", "painted/generated",
            "variantName", "unit_painted",
            "textures", new[]
            {
                new
                {
                    sourceVirtualPath = "variantmeshes\\unit\\body_base_colour.dds",
                    pngPath = "painted/input/body.png"
                }
            },
            "variantSelections", new[]
            {
                new { slotPath = "root/slot[0]", choiceIndex = 3 }
            }));

        Assert.That(response.Success, Is.True);
        var request = factory.Created.Single().PaintedExports.Single();
        Assert.That(request.AssetPath, Is.EqualTo("unit.variantmeshdefinition"));
        Assert.That(request.VariantName, Is.EqualTo("unit_painted"));
        Assert.That(request.OutputDirectory, Is.EqualTo(Path.Combine(root, "painted", "generated")));
        Assert.That(request.Textures.Single().SourceVirtualPath, Is.EqualTo("variantmeshes\\unit\\body_base_colour.dds"));
        Assert.That(request.Textures.Single().PngPath, Is.EqualTo(Path.Combine(root, "painted", "input", "body.png")));
        Assert.That(request.VariantSelections, Is.EqualTo(new[]
        {
            new AssetHostVariantMeshSelection("root/slot[0]", 3)
        }));
    }

    [Test]
    public void ExportPaintedVariant_RejectsInputsOutsideOutputRoot()
    {
        var factory = new FakeRuntimeFactory();
        using var dispatcher = new AssetHostDispatcher(factory);
        var root = Path.Combine(Path.GetTempPath(), "asset-host-root", Guid.NewGuid().ToString("N"));
        dispatcher.Dispatch(Initialize(root, "base.pack"));

        var response = dispatcher.Dispatch(Request(
            "exportPaintedVariant",
            "painted-invalid",
            "assetPath", "unit.variantmeshdefinition",
            "outputDirectory", "../outside",
            "variantName", "unit_painted",
            "textures", Array.Empty<object>()));

        Assert.That(response.Error!.Code, Is.EqualTo("InvalidOutputDirectory"));
        Assert.That(factory.Created.Single().PaintedExports, Is.Empty);
    }

    [Test]
    public void Export_PropagatesStableMissingAssetAndAnimationCodes()
    {
        var factory = new FakeRuntimeFactory
        {
            NextResult = Failure("AssetNotFound", "asset missing")
        };
        using var dispatcher = new AssetHostDispatcher(factory);
        var root = Path.Combine(Path.GetTempPath(), "asset-host-root", Guid.NewGuid().ToString("N"));
        dispatcher.Dispatch(Initialize(root, "base.pack"));

        var response = dispatcher.Dispatch(Request(
            "exportModel",
            "r1",
            "assetPath", "missing.rigid_model_v2",
            "outputPath", "missing.glb",
            "animationPaths", new[] { "missing.anim" }));

        Assert.That(response.Error!.Code, Is.EqualTo("AssetNotFound"));
        Assert.That(response.Result, Is.Not.Null);

        factory.NextResult = Failure("AnimationNotFound", "animation missing");
        var animationResponse = dispatcher.Dispatch(Request(
            "exportModel",
            "r2",
            "assetPath", "model.rigid_model_v2",
            "outputPath", "missing-animation.glb",
            "animationPaths", new[] { "missing.anim" }));
        Assert.That(animationResponse.Error!.Code, Is.EqualTo("AnimationNotFound"));
    }

    private static string Initialize(string root, params string[] packs)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            protocolVersion = AssetHostProtocol.ProtocolVersion,
            requestId = "init-" + Guid.NewGuid().ToString("N"),
            command = "initialize",
            packPaths = packs,
            outputRoot = root
        });

    private static string Request(string command, string requestId, params object[] properties)
    {
        var body = new Dictionary<string, object?>
        {
            ["protocolVersion"] = AssetHostProtocol.ProtocolVersion,
            ["requestId"] = requestId,
            ["command"] = command
        };
        for (var index = 0; index + 1 < properties.Length; index += 2)
            body[(string)properties[index]] = properties[index + 1];
        return System.Text.Json.JsonSerializer.Serialize(body);
    }

    private static ExportResult Failure(string code, string message)
        => new(false, null, [], [], [new ExportError(code, message)]);

    private sealed class FakeRuntimeFactory : IAssetHostRuntimeFactory
    {
        public List<FakeRuntime> Created { get; } = [];
        public ExportResult? NextResult { get; set; }
        public bool FailNextCreate { get; set; }

        public IAssetHostRuntime Create(
            IReadOnlyList<string> packPaths,
            string outputRoot,
            string? vanillaPackFilesCachePath = null)
        {
            if (FailNextCreate)
            {
                FailNextCreate = false;
                throw new InvalidOperationException("synthetic initialization failure");
            }

            var runtime = new FakeRuntime(this);
            Created.Add(runtime);
            return runtime;
        }
    }

    private sealed class FakeRuntime(FakeRuntimeFactory factory) : IAssetHostRuntime
    {
        public bool Disposed { get; private set; }
        public List<AssetHostExportRequest> Exports { get; } = [];
        public List<AssetHostPaintedVariantRequest> PaintedExports { get; } = [];

        public ExportResult ExportModel(AssetHostExportRequest request)
        {
            Exports.Add(request);
            return factory.NextResult ?? new ExportResult(true, request.OutputPath, [], [], []);
        }

        public AssetHostAnimationCatalog GetAnimationCatalog(string assetPath)
            => new(true, assetPath, null, false, [], []);

        public AssetHostPaintedVariantResult ExportPaintedVariant(AssetHostPaintedVariantRequest request)
        {
            PaintedExports.Add(request);
            return new(
                true,
                $"variantmeshes\\variantmeshdefinitions\\whmm_unit_painter\\{request.VariantName}.variantmeshdefinition",
                [],
                [],
                []);
        }

        public void Dispose() => Disposed = true;
    }
}
