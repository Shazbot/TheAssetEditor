using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MeshImportExport;
using Serilog;

namespace WH3AssetHost;

/// <summary>
/// Temporary headless-only benchmark comparing the known-good Fast PNG
/// preview path with lossless raw-RGBA+Zstd KTX2 and fast UASTC+Zstd KTX2.
///
/// PNG remains the actual exported texture. Transformed pixels are staged as
/// uncompressed DDS files while the model is exported, then BasisU is invoked
/// once per colorspace (at most two processes per model) for all staged
/// textures. This isolates the cost of batched UASTC encoding from per-texture
/// process startup overhead.
/// </summary>
internal sealed class UastcTextureBenchmarkProbe : ITextureEncodingProbe
{
    private const int UastcLevel = 0;
    private const int Ktx2ZstdLevel = 1;
    private static readonly ILogger Logger = Log.ForContext<UastcTextureBenchmarkProbe>();
    private static readonly Lazy<string?> BasisuExecutable = new(FindBasisuExecutable);
    private static int _basisuPathLogged;

    private readonly object _gate = new();
    private readonly List<PendingTexture> _pending = [];
    private string? _root;
    private string? _assetPath;
    private int _nextIndex;

    public void BeginExport(string assetPath)
    {
        string? staleRoot;
        lock (_gate)
        {
            staleRoot = _root;
            _pending.Clear();
            _root = CreateProbeRoot();
            _assetPath = assetPath;
            _nextIndex = 0;
        }

        DeleteDirectoryBestEffort(staleRoot);
    }

    public void Probe(
        string texturePath,
        TextureHelper.DecodedDdsImage image,
        bool srgb,
        TextureKtx2EncodeResult losslessKtx2,
        int pngBytes,
        double pngEncodeMs)
    {
        string root;
        int index;
        lock (_gate)
        {
            if (_root == null)
            {
                _root = CreateProbeRoot();
                _assetPath ??= "<unscoped>";
            }

            root = _root;
            index = _nextIndex++;
        }

        var groupName = srgb ? "srgb" : "linear";
        var inputDirectory = Path.Combine(root, "input", groupName);
        var outputDirectory = Path.Combine(root, "output", groupName);
        Directory.CreateDirectory(inputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var stem = $"texture_{index:D3}";
        var inputPath = Path.Combine(inputDirectory, stem + ".dds");
        var outputPath = Path.Combine(outputDirectory, stem + ".ktx2");

        var stageStopwatch = Stopwatch.StartNew();
        var stagedBytes = WriteUncompressedBgraDds(inputPath, image);
        stageStopwatch.Stop();

        var pending = new PendingTexture(
            texturePath,
            srgb,
            inputPath,
            outputPath,
            pngBytes,
            pngEncodeMs,
            losslessKtx2.Ktx2Data.Length,
            losslessKtx2.RgbaConvertMs + losslessKtx2.ZstdMs,
            stageStopwatch.Elapsed.TotalMilliseconds,
            stagedBytes);

        lock (_gate)
            _pending.Add(pending);
    }

    public void Flush(string? assetPath = null)
    {
        List<PendingTexture> pending;
        string? root;
        string resolvedAssetPath;

        lock (_gate)
        {
            pending = [.. _pending];
            _pending.Clear();
            root = _root;
            _root = null;
            resolvedAssetPath = assetPath ?? _assetPath ?? "<unknown>";
            _assetPath = null;
            _nextIndex = 0;
        }

        if (pending.Count == 0)
        {
            DeleteDirectoryBestEffort(root);
            return;
        }

        if (root == null)
        {
            Logger.Warning(
                "UASTC batch benchmark skipped for {AssetPath}: staging root was unavailable",
                resolvedAssetPath);
            return;
        }

        try
        {
            var basisuPath = ResolveBasisuExecutable();
            if (basisuPath == null)
            {
                Logger.Warning(
                    "UASTC batch benchmark skipped for {AssetPath}: unable to find basisu.exe under {BaseDirectory}",
                    resolvedAssetPath,
                    AppContext.BaseDirectory);
                return;
            }

            var batchStopwatch = Stopwatch.StartNew();
            var srgbResult = RunBatch(
                basisuPath,
                root,
                pending.Where(x => x.Srgb).ToArray(),
                srgb: true);
            var linearResult = RunBatch(
                basisuPath,
                root,
                pending.Where(x => !x.Srgb).ToArray(),
                srgb: false);
            batchStopwatch.Stop();

            var basisuMs = srgbResult.ElapsedMs + linearResult.ElapsedMs;
            var stageMs = pending.Sum(x => x.StageMs);
            var pngBytes = pending.Sum(x => (long)x.PngBytes);
            var pngEncodeMs = pending.Sum(x => x.PngEncodeMs);
            var losslessBytes = pending.Sum(x => (long)x.LosslessKtx2Bytes);
            var losslessEncodeMs = pending.Sum(x => x.LosslessEncodeMs);
            var stagedDdsBytes = pending.Sum(x => x.StagedDdsBytes);
            var outputCount = 0;
            long uastcBytes = 0;

            foreach (var texture in pending)
            {
                long outputBytes = 0;
                if (File.Exists(texture.OutputPath))
                {
                    outputBytes = new FileInfo(texture.OutputPath).Length;
                    outputCount++;
                    uastcBytes += outputBytes;
                }

                Logger.Information(
                    "Texture codec batch result for {TexturePath}: pngBytes={PngBytes}, pngEncode={PngEncodeMs:F1}ms, losslessKtx2Bytes={LosslessKtx2Bytes}, losslessEncode={LosslessEncodeMs:F1}ms, uastcKtx2Bytes={UastcKtx2Bytes}, uastcVsPng={UastcVsPng:P1}, uastcVsLossless={UastcVsLossless:P1}, srgb={Srgb}, ddsStage={DdsStageMs:F1}ms, stagedDdsBytes={StagedDdsBytes}",
                    texture.TexturePath,
                    texture.PngBytes,
                    texture.PngEncodeMs,
                    texture.LosslessKtx2Bytes,
                    texture.LosslessEncodeMs,
                    outputBytes,
                    texture.PngBytes == 0 ? 0d : (double)outputBytes / texture.PngBytes,
                    texture.LosslessKtx2Bytes == 0
                        ? 0d
                        : (double)outputBytes / texture.LosslessKtx2Bytes,
                    texture.Srgb,
                    texture.StageMs,
                    texture.StagedDdsBytes);
            }

            if (outputCount != pending.Count)
            {
                Logger.Warning(
                    "UASTC batch benchmark produced {OutputCount}/{TextureCount} expected outputs for {AssetPath}",
                    outputCount,
                    pending.Count,
                    resolvedAssetPath);
            }

            Logger.Information(
                "UASTC batch benchmark for {AssetPath}: textures={TextureCount}, outputs={OutputCount}, processes={ProcessCount}, batchWall={BatchWallMs:F1}ms, basisu={BasisuMs:F1}ms (srgb={SrgbBasisuMs:F1}ms, linear={LinearBasisuMs:F1}ms), ddsStage={DdsStageMs:F1}ms, candidateEncode={CandidateEncodeMs:F1}ms, pngBytes={PngBytes}, pngEncode={PngEncodeMs:F1}ms, losslessKtx2Bytes={LosslessKtx2Bytes}, losslessEncode={LosslessEncodeMs:F1}ms, uastcKtx2Bytes={UastcKtx2Bytes}, uastcVsPng={UastcVsPng:P1}, uastcVsLossless={UastcVsLossless:P1}, stagedDdsBytes={StagedDdsBytes}, uastcLevel={UastcLevel}, zstdLevel={Ktx2ZstdLevel}, parallel=true",
                resolvedAssetPath,
                pending.Count,
                outputCount,
                srgbResult.ProcessCount + linearResult.ProcessCount,
                batchStopwatch.Elapsed.TotalMilliseconds,
                basisuMs,
                srgbResult.ElapsedMs,
                linearResult.ElapsedMs,
                stageMs,
                stageMs + basisuMs,
                pngBytes,
                pngEncodeMs,
                losslessBytes,
                losslessEncodeMs,
                uastcBytes,
                pngBytes == 0 ? 0d : (double)uastcBytes / pngBytes,
                losslessBytes == 0 ? 0d : (double)uastcBytes / losslessBytes,
                stagedDdsBytes,
                UastcLevel,
                Ktx2ZstdLevel);
        }
        catch (Exception exception)
        {
            Logger.Warning(
                exception,
                "UASTC batch benchmark failed for {AssetPath}",
                resolvedAssetPath);
        }
        finally
        {
            DeleteDirectoryBestEffort(root);
        }
    }

    private static BatchRunResult RunBatch(
        string basisuPath,
        string root,
        IReadOnlyList<PendingTexture> textures,
        bool srgb)
    {
        if (textures.Count == 0)
            return BatchRunResult.Empty;

        var outputDirectory = Path.Combine(root, "output", srgb ? "srgb" : "linear");
        Directory.CreateDirectory(outputDirectory);

        var args = new StringBuilder();
        args.Append("-uastc ");
        args.Append("-uastc_level ").Append(UastcLevel).Append(' ');
        args.Append("-ktx2 ");
        args.Append("-ktx2_zstandard_level ").Append(Ktx2ZstdLevel).Append(' ');
        args.Append("-individual -parallel ");
        if (!srgb)
            args.Append("-linear ");
        args.Append("-output_path ").Append(Quote(outputDirectory)).Append(' ');

        foreach (var texture in textures)
            args.Append("-file ").Append(Quote(texture.InputPath)).Append(' ');

        var stopwatch = Stopwatch.StartNew();
        var exitCode = RunBasisu(
            basisuPath,
            args.ToString(),
            root,
            out var stdOut,
            out var stdErr);
        stopwatch.Stop();

        if (exitCode != 0)
        {
            Logger.Warning(
                "UASTC batch process failed: colorspace={ColorSpace}, textures={TextureCount}, exitCode={ExitCode}, elapsed={ElapsedMs:F1}ms, stderr={StdErr}, stdout={StdOut}",
                srgb ? "sRGB" : "linear",
                textures.Count,
                exitCode,
                stopwatch.Elapsed.TotalMilliseconds,
                TrimForLog(stdErr),
                TrimForLog(stdOut));
        }
        else
        {
            Logger.Information(
                "UASTC batch process completed: colorspace={ColorSpace}, textures={TextureCount}, elapsed={ElapsedMs:F1}ms",
                srgb ? "sRGB" : "linear",
                textures.Count,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        return new BatchRunResult(1, stopwatch.Elapsed.TotalMilliseconds);
    }

    private static string CreateProbeRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "WH3AssetHost",
            "UastcProbe",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string? ResolveBasisuExecutable()
    {
        var path = BasisuExecutable.Value;
        if (path != null && Interlocked.Exchange(ref _basisuPathLogged, 1) == 0)
        {
            Logger.Information(
                "Using BasisU benchmark executable {BasisuPath}",
                path);
        }

        return path;
    }

    private static string? FindBasisuExecutable()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 or Architecture.Arm => "arm64",
            _ => "x64"
        };

        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "basisu.exe"),
            Path.Combine(baseDirectory, $"windows-{architecture}", "basisu.exe"),
            Path.Combine(baseDirectory, "binaries", $"windows-{architecture}", "basisu.exe"),
            Path.Combine(baseDirectory, "windows", "basisu.exe"),
            Path.Combine(baseDirectory, "binaries", "windows", "basisu.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        try
        {
            return Directory
                .EnumerateFiles(baseDirectory, "basisu.exe", SearchOption.AllDirectories)
                .Where(path => path.Contains("windows", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path.Contains($"windows-{architecture}", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static int RunBasisu(
        string executablePath,
        string arguments,
        string workingDirectory,
        out string stdout,
        out string stderr)
    {
        var processInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = false,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };

        using var process = new Process { StartInfo = processInfo };
        process.Start();
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        stdout = stdoutTask.GetAwaiter().GetResult();
        stderr = stderrTask.GetAwaiter().GetResult();
        return process.ExitCode;
    }

    private static long WriteUncompressedBgraDds(
        string path,
        TextureHelper.DecodedDdsImage image)
    {
        var pixelBytes = checked(image.Width * image.Height * 4);
        if (image.BgraPixels.Length < pixelBytes)
            throw new InvalidDataException("Texture pixel buffer is truncated.");

        using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.SequentialScan);
        using var writer = new BinaryWriter(
            stream,
            Encoding.ASCII,
            leaveOpen: false);

        writer.Write(Encoding.ASCII.GetBytes("DDS "));
        writer.Write(124); // DDS_HEADER.dwSize
        writer.Write(0x0000100f); // CAPS | HEIGHT | WIDTH | PITCH | PIXELFORMAT
        writer.Write(image.Height);
        writer.Write(image.Width);
        writer.Write(checked(image.Width * 4)); // pitch
        writer.Write(0); // depth
        writer.Write(0); // mip count

        for (var index = 0; index < 11; index++)
            writer.Write(0);

        writer.Write(32); // DDS_PIXELFORMAT.dwSize
        writer.Write(0x41); // DDPF_RGB | DDPF_ALPHAPIXELS
        writer.Write(0); // FourCC
        writer.Write(32); // RGB bit count
        writer.Write(0x00ff0000); // R
        writer.Write(0x0000ff00); // G
        writer.Write(0x000000ff); // B
        writer.Write(unchecked((int)0xff000000)); // A

        writer.Write(0x00001000); // DDSCAPS_TEXTURE
        writer.Write(0); // caps2
        writer.Write(0); // caps3
        writer.Write(0); // caps4
        writer.Write(0); // reserved2

        writer.Write(image.BgraPixels, 0, pixelBytes);
        return stream.Position;
    }

    private static string Quote(string path)
        => $"\"{path.Replace("\"", "\\\"")}\"";

    private static string TrimForLog(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var singleLine = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return singleLine.Length <= 500
            ? singleLine
            : singleLine[..500] + "...";
    }

    private static void DeleteDirectoryBestEffort(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception exception)
        {
            Logger.Debug(
                exception,
                "Unable to clean UASTC benchmark directory {ProbeDirectory}",
                path);
        }
    }

    private sealed record PendingTexture(
        string TexturePath,
        bool Srgb,
        string InputPath,
        string OutputPath,
        int PngBytes,
        double PngEncodeMs,
        int LosslessKtx2Bytes,
        double LosslessEncodeMs,
        double StageMs,
        long StagedDdsBytes);

    private readonly record struct BatchRunResult(
        int ProcessCount,
        double ElapsedMs)
    {
        public static BatchRunResult Empty => new(0, 0);
    }
}
