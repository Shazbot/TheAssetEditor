using System.IO.Pipes;
using System.Text.Json;
using Editors.ImportExport.Exporting.Exporters.RmvToGltf;
using WH3AssetHost;

namespace Test.ImportExport.Exporting;

public sealed class HostMissingSkeletonDecisionTests
{
    [Test]
    public async Task ServeForwardsMissingSkeletonDecisionAndResumesExport()
    {
        var factory = new InteractiveRuntimeFactory();
        var pipeName = "wh3-asset-host-decision-" + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = new AssetHostPipeServer(factory)
            .RunAsync(pipeName, cancellationToken: cancellation.Token);

        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(cancellation.Token);

            await SendAsync(client, new
            {
                protocolVersion = AssetHostProtocol.ProtocolVersion,
                requestId = "init-decision",
                command = "initialize",
                packPaths = new[] { "base.pack" },
                outputRoot = Path.Combine(Path.GetTempPath(), "asset-host-decision", Guid.NewGuid().ToString("N"))
            });
            var initialize = await ReadJsonAsync(client);
            Assert.That(initialize.RootElement.GetProperty("success").GetBoolean(), Is.True);

            await SendAsync(client, new
            {
                protocolVersion = AssetHostProtocol.ProtocolVersion,
                requestId = "export-decision",
                command = "exportModel",
                assetPath = "model.rigid_model_v2",
                outputPath = "model.glb"
            });

            var decisionRequest = await ReadJsonAsync(client);
            Assert.That(decisionRequest.RootElement.GetProperty("command").GetString(), Is.EqualTo("decisionRequest"));
            Assert.That(decisionRequest.RootElement.GetProperty("decisionType").GetString(), Is.EqualTo("missingSkeleton"));
            Assert.That(decisionRequest.RootElement.GetProperty("skeletonName").GetString(), Is.EqualTo("missing_skeleton"));

            var decisionRequestId = decisionRequest.RootElement.GetProperty("requestId").GetString();
            await SendAsync(client, new
            {
                protocolVersion = AssetHostProtocol.ProtocolVersion,
                requestId = decisionRequestId,
                command = "decisionResponse",
                action = "continueWithoutSkeleton"
            });

            var export = await ReadJsonAsync(client);
            Assert.That(export.RootElement.GetProperty("success").GetBoolean(), Is.True);
            Assert.That(factory.Runtime!.Decision, Is.EqualTo(MissingSkeletonAction.ContinueWithoutSkeleton));

            await SendAsync(client, new
            {
                protocolVersion = AssetHostProtocol.ProtocolVersion,
                requestId = "shutdown-decision",
                command = "shutdown"
            });
            var shutdown = await ReadJsonAsync(client);
            Assert.That(shutdown.RootElement.GetProperty("success").GetBoolean(), Is.True);

            await serverTask;
            Assert.That(factory.Runtime.Disposed, Is.True);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static Task SendAsync(Stream stream, object request)
        => NamedPipeFrameProtocol.WriteJsonFrameAsync(stream, request);

    private static async Task<JsonDocument> ReadJsonAsync(Stream stream)
    {
        var payload = await NamedPipeFrameProtocol.ReadFrameAsync(stream);
        Assert.That(payload, Is.Not.Null);
        return JsonDocument.Parse(payload!);
    }

    private sealed class InteractiveRuntimeFactory : IAssetHostRuntimeFactory, IAssetHostInteractiveRuntimeFactory
    {
        public InteractiveRuntime? Runtime { get; private set; }

        public IAssetHostRuntime Create(
            IReadOnlyList<string> packPaths,
            string outputRoot,
            string? vanillaPackFilesCachePath = null)
        {
            Runtime = new InteractiveRuntime(new HeadlessMissingSkeletonDecision());
            return Runtime;
        }

        public IAssetHostRuntime Create(
            IReadOnlyList<string> packPaths,
            string outputRoot,
            string? vanillaPackFilesCachePath,
            IMissingSkeletonDecision missingSkeletonDecision)
        {
            Runtime = new InteractiveRuntime(missingSkeletonDecision);
            return Runtime;
        }
    }

    private sealed class InteractiveRuntime : IAssetHostRuntime
    {
        private readonly IMissingSkeletonDecision _missingSkeletonDecision;

        public InteractiveRuntime(IMissingSkeletonDecision missingSkeletonDecision)
        {
            _missingSkeletonDecision = missingSkeletonDecision;
        }

        public MissingSkeletonAction? Decision { get; private set; }
        public bool Disposed { get; private set; }

        public ExportResult ExportModel(AssetHostExportRequest request)
        {
            Decision = _missingSkeletonDecision.Decide(
                new MissingSkeletonContext("missing_skeleton", "A skeleton is not present."));
            return new ExportResult(
                Decision == MissingSkeletonAction.ContinueWithoutSkeleton,
                request.OutputPath,
                [],
                [],
                []);
        }

        public AssetHostAnimationCatalog GetAnimationCatalog(string assetPath)
            => new(true, assetPath, null, false, [], []);

        public void Dispose() => Disposed = true;
    }
}
