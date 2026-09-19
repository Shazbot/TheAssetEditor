using System.Diagnostics;
using System.Linq;
using SharpGLTF.Schema2;

namespace Editors.ImportExport.Exporting.Exporters.RmvToGltf.Helpers;

public interface IGltfSceneSaver
{
    void Save(ModelRoot modelRoot, string fullSystemPath);

    /// <summary>
    /// Saves a scene and, for binary glTF output, removes the exact generated
    /// texture intermediates supplied by the exporter after the save succeeds.
    /// The default implementation preserves compatibility with existing saver
    /// implementations.
    /// </summary>
    void Save(
        ModelRoot modelRoot,
        string fullSystemPath,
        IReadOnlyCollection<string> generatedTexturePaths)
        => Save(modelRoot, fullSystemPath);
}

/// <summary>
/// Scene saver for console/service callers. It lets the caller observe save
/// failures directly.
/// </summary>
public sealed class HeadlessGltfSceneSaver : IGltfSceneSaver
{
    private static readonly ILogger Logger = Logging.Create<HeadlessGltfSceneSaver>();

    public void Save(ModelRoot modelRoot, string fullSystemPath)
        => Save(modelRoot, fullSystemPath, Array.Empty<string>());

    public void Save(
        ModelRoot modelRoot,
        string fullSystemPath,
        IReadOnlyCollection<string> generatedTexturePaths)
    {
        ArgumentNullException.ThrowIfNull(modelRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullSystemPath);
        ArgumentNullException.ThrowIfNull(generatedTexturePaths);

        var totalStopwatch = Stopwatch.StartNew();
        var fullOutputPath = Path.GetFullPath(fullSystemPath);
        var outputDirectory = Path.GetDirectoryName(fullOutputPath)
            ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outputDirectory);

        if (!string.Equals(Path.GetExtension(fullOutputPath), ".glb", StringComparison.OrdinalIgnoreCase))
        {
            var saveStopwatch = Stopwatch.StartNew();
            modelRoot.Save(fullOutputPath);
            saveStopwatch.Stop();
            totalStopwatch.Stop();

            Logger.Here().Debug(
                "GLTF save timing for {OutputName}: total={TotalMs:F1}ms, save={SaveMs:F1}ms, outputBytes={OutputBytes}, logicalImages={LogicalImages}",
                Path.GetFileName(fullOutputPath),
                totalStopwatch.Elapsed.TotalMilliseconds,
                saveStopwatch.Elapsed.TotalMilliseconds,
                GetFileSize(fullOutputPath),
                modelRoot.LogicalImages.Count);
            return;
        }

        var generatedTextures = generatedTexturePaths
            .Where(IsGeneratedTexture)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var generatedTextureSet = generatedTextures.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var generatedTextureBytes = generatedTextures.Sum(GetFileSize);
        var externalizeGeneratedTextures =
            generatedTextures.Length > 0
            && modelRoot.LogicalImages.Count > 0
            && modelRoot.LogicalImages.All(image =>
            {
                if (string.IsNullOrWhiteSpace(image.AlternateWriteFileName))
                    return false;

                var imagePath = Path.GetFullPath(
                    ResolveOutputPath(outputDirectory, image.AlternateWriteFileName));
                return generatedTextureSet.Contains(imagePath) && File.Exists(imagePath);
            });

        TimedFileWriteStream? timedOutputStream = null;
        var fallbackFileWriteMs = 0.0;
        long fallbackBytesWritten = 0;

        var context = WriteContext.Create(
            (assetName, data) =>
            {
                var path = ResolveOutputPath(outputDirectory, assetName);
                var stopwatch = Stopwatch.StartNew();
                using var stream = File.Create(path);
                stream.Write(data.Array!, data.Offset, data.Count);
                stopwatch.Stop();

                fallbackFileWriteMs += stopwatch.Elapsed.TotalMilliseconds;
                fallbackBytesWritten += data.Count;
            },
            assetName =>
            {
                var stream = new TimedFileWriteStream(
                    ResolveOutputPath(outputDirectory, assetName));
                timedOutputStream = stream;
                return stream;
            });

        // The headless preview scene is disposable after this save. Merge its
        // geometry/animation buffers in place so SharpGLTF does not defensively
        // DeepClone() the entire model before producing a GLB.
        //
        // When every logical image maps to a texture the exporter already wrote
        // beside the preview, keep those images external. WHMM serves the whole
        // registered preview directory, so GLTFLoader can fetch the KTX2 sidecars
        // directly and we avoid copying ~tens of MB of texture data into every GLB.
        //
        // If an image cannot be proven to have a generated sidecar, preserve the
        // previous self-contained behavior and embed all images in the GLB.
        var inPlacePrepareStopwatch = Stopwatch.StartNew();
        if (externalizeGeneratedTextures)
        {
            modelRoot.MergeBuffers();
        }
        else
        {
            modelRoot.MergeImages();
            modelRoot.MergeBuffers();
        }
        inPlacePrepareStopwatch.Stop();

        context.ImageWriting = ResourceWriteMode.SatelliteFile;
        if (externalizeGeneratedTextures)
        {
            context.ImageWriteCallback = (_, assetName, _) =>
            {
                var imagePath = Path.GetFullPath(
                    ResolveOutputPath(outputDirectory, assetName));
                if (!generatedTextureSet.Contains(imagePath) || !File.Exists(imagePath))
                    throw new InvalidOperationException(
                        $"Expected generated preview texture sidecar '{assetName}' was not available.");

                // The texture exporter already wrote the exact KTX2/PNG bytes.
                // Returning the URI without writing avoids duplicating that I/O.
                return assetName;
            };
        }

        var sharpGltfStopwatch = Stopwatch.StartNew();
        modelRoot.SaveGLB(Path.GetFileName(fullOutputPath), context);
        sharpGltfStopwatch.Stop();

        var fileWriteMs = (timedOutputStream?.IoElapsedMilliseconds ?? 0)
            + fallbackFileWriteMs;
        var bytesWritten = (timedOutputStream?.BytesWritten ?? 0)
            + fallbackBytesWritten;
        var preprocessEmbedSerializeMs = Math.Max(
            0,
            sharpGltfStopwatch.Elapsed.TotalMilliseconds - fileWriteMs);

        var cleanupStopwatch = Stopwatch.StartNew();
        if (!externalizeGeneratedTextures)
        {
            foreach (var texturePath in generatedTextures)
            {
                // Embedded GLBs no longer need the exact generated texture
                // intermediates. External-texture previews retain them until
                // WHMM releases and removes the entire preview directory.
                if (File.Exists(texturePath))
                    File.Delete(texturePath);
            }
        }
        cleanupStopwatch.Stop();
        totalStopwatch.Stop();

        Logger.Here().Debug(
            "GLB save timing for {OutputName}: total={TotalMs:F1}ms, externalTextures={ExternalTextures}, inPlacePrepare={InPlacePrepareMs:F1}ms, sharpGltf={SharpGltfMs:F1}ms, preprocessSerialize={PreprocessSerializeMs:F1}ms, fileWrite={FileWriteMs:F1}ms, cleanup={CleanupMs:F1}ms, outputBytes={OutputBytes}, streamedBytes={StreamedBytes}, logicalImages={LogicalImages}, generatedTextureBytes={GeneratedTextureBytes}, generatedTextureCount={GeneratedTextureCount}",
            Path.GetFileName(fullOutputPath),
            totalStopwatch.Elapsed.TotalMilliseconds,
            externalizeGeneratedTextures,
            inPlacePrepareStopwatch.Elapsed.TotalMilliseconds,
            sharpGltfStopwatch.Elapsed.TotalMilliseconds,
            preprocessEmbedSerializeMs,
            fileWriteMs,
            cleanupStopwatch.Elapsed.TotalMilliseconds,
            GetFileSize(fullOutputPath),
            bytesWritten,
            modelRoot.LogicalImages.Count,
            generatedTextureBytes,
            generatedTextures.Length);
    }

    private static bool IsGeneratedTexture(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".ktx2", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveOutputPath(string outputDirectory, string rawUri)
        => Path.Combine(outputDirectory, Uri.UnescapeDataString(rawUri));

    private static long GetFileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private sealed class TimedFileWriteStream : Stream
    {
        private readonly FileStream _stream;
        private long _ioTicks;
        private long _bytesWritten;
        private bool _disposed;

        public TimedFileWriteStream(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var start = Stopwatch.GetTimestamp();
            _stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);
            _ioTicks += Stopwatch.GetTimestamp() - start;
        }

        public double IoElapsedMilliseconds
            => _ioTicks * 1000.0 / Stopwatch.Frequency;

        public long BytesWritten => _bytesWritten;

        public override bool CanRead => false;
        public override bool CanSeek => _stream.CanSeek;
        public override bool CanWrite => true;
        public override long Length => _stream.Length;

        public override long Position
        {
            get => _stream.Position;
            set => _stream.Position = value;
        }

        public override void Flush()
            => Measure(_stream.Flush);

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => _stream.Seek(offset, origin);

        public override void SetLength(long value)
            => _stream.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            Measure(() => _stream.Write(buffer, offset, count));
            _bytesWritten += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                _stream.Write(buffer);
            }
            finally
            {
                _ioTicks += Stopwatch.GetTimestamp() - start;
            }

            _bytesWritten += buffer.Length;
        }

        public override void WriteByte(byte value)
        {
            Measure(() => _stream.WriteByte(value));
            _bytesWritten++;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                Measure(_stream.Dispose);
            }

            base.Dispose(disposing);
        }

        private void Measure(Action action)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                action();
            }
            finally
            {
                _ioTicks += Stopwatch.GetTimestamp() - start;
            }
        }
    }
}
