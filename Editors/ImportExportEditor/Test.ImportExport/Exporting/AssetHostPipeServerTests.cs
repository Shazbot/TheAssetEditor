using System.IO.Pipes;
using System.Text.Json;
using Editors.ImportExport.Exporting.Exporters.RmvToGltf;
using WH3AssetHost;

namespace Test.ImportExport.Exporting;

public sealed class AssetHostPipeServerTests
{
    [Test]
    public async Task PipeServer_ProcessesSequentialRequestsAndDisposesOnShutdown()
    {
        var factory = new FakeRuntimeFactory();
        var pipeName = "wh3-asset-host-test-" + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = new AssetHostPipeServer(factory);
        var serverTask = server.RunAsync(pipeName, cancellationToken: cancellation.Token);

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
                requestId = "hello-1",
                command = "hello"
            });
            var hello = await ReadJsonAsync(client);
            Assert.That(hello.RootElement.GetProperty("success").GetBoolean(), Is.True);

            var outputRoot = Path.Combine(Path.GetTempPath(), "asset-host-pipe", Guid.NewGuid().ToString("N"));
            await SendAsync(client, new
            {
                protocolVersion = AssetHostProtocol.ProtocolVersion,
                requestId = "init-1",
                command = "initialize",
                packPaths = new[] { "base.pack", "mod.pack" },
                outputRoot
            });
            var initialize = await ReadJsonAsync(client);
            Assert.That(initialize.RootElement.GetProperty("success").GetBoolean(), Is.True);

            await SendAsync(client, new
            {
                protocolVersion = AssetHostProtocol.ProtocolVersion,
                requestId = "export-1",
                command = "exportModel",
                assetPath = "model.rigid_model_v2",
                outputPath = "nested/model.glb"
            });
            var export = await ReadJsonAsync(client);
            Assert.That(export.RootElement.GetProperty("success").GetBoolean(), Is.True);

            await SendAsync(client, new
            {
                protocolVersion = AssetHostProtocol.ProtocolVersion,
                requestId = "shutdown-1",
                command = "shutdown"
            });
            var shutdown = await ReadJsonAsync(client);
            Assert.That(shutdown.RootElement.GetProperty("success").GetBoolean(), Is.True);

            await serverTask;
            Assert.That(factory.Created, Has.Count.EqualTo(1));
            Assert.That(factory.Created[0].Disposed, Is.True);
            Assert.That(factory.Created[0].Exports, Has.Count.EqualTo(1));
            Assert.That(factory.Created[0].Exports[0].OutputPath, Does.EndWith(Path.Combine("nested", "model.glb")));
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

    [Test]
    public async Task PipeServer_DisposesRuntimeWhenClientDisconnects()
    {
        var factory = new FakeRuntimeFactory();
        var pipeName = "wh3-asset-host-disconnect-" + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = new AssetHostPipeServer(factory);
        var serverTask = server.RunAsync(pipeName, cancellationToken: cancellation.Token);

        try
        {
            using (var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous))
            {
                await client.ConnectAsync(cancellation.Token);
                await SendAsync(client, new
                {
                    protocolVersion = AssetHostProtocol.ProtocolVersion,
                    requestId = "init-disconnect",
                    command = "initialize",
                    packPaths = new[] { "base.pack" },
                    outputRoot = Path.Combine(Path.GetTempPath(), "asset-host-pipe", Guid.NewGuid().ToString("N"))
                });
                var initialize = await ReadJsonAsync(client);
                Assert.That(initialize.RootElement.GetProperty("success").GetBoolean(), Is.True);
            }

            await serverTask;
            Assert.That(factory.Created, Has.Count.EqualTo(1));
            Assert.That(factory.Created[0].Disposed, Is.True);
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

    private sealed class FakeRuntimeFactory : IAssetHostRuntimeFactory
    {
        public List<FakeRuntime> Created { get; } = [];

        public IAssetHostRuntime Create(IReadOnlyList<string> packPaths, string outputRoot)
        {
            var runtime = new FakeRuntime();
            Created.Add(runtime);
            return runtime;
        }
    }

    private sealed class FakeRuntime : IAssetHostRuntime
    {
        public bool Disposed { get; private set; }
        public List<AssetHostExportRequest> Exports { get; } = [];

        public ExportResult ExportModel(AssetHostExportRequest request)
        {
            Exports.Add(request);
            return new ExportResult(true, request.OutputPath, [], [], []);
        }

        public AssetHostAnimationCatalog GetAnimationCatalog(string assetPath)
            => new(true, assetPath, null, false, [], []);

        public void Dispose() => Disposed = true;
    }
}
