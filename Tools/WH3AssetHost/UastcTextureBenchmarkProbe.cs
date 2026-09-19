using System.Diagnostics;
using MeshImportExport;
using System.Runtime.InteropServices;
using Serilog;

namespace WH3AssetHost;

/// <summary>
/// Temporary headless-only benchmark comparing the known-good Fast PNG
/// preview path with lossless raw-RGBA+Zstd KTX2 and fast UASTC+Zstd KTX2.
/// PNG remains the actual exported texture; this probe only measures
/// alternatives and never changes the GLB material input.
/// </summary>
internal sealed class UastcTextureBenchmarkProbe : ITextureEncodingProbe
{
    private const int UastcLevel = 0;
    private const int Ktx2ZstdLevel = 1;
    private static readonly ILogger Logger = Log.ForContext<UastcTextureBenchmarkProbe>();

    public void Probe(
        string texturePath,
        TextureHelper.DecodedDdsImage image,
        bool srgb,
        TextureKtx2EncodeResult losslessKtx2)
    {
        var pngStopwatch = Stopwatch.StartNew();
        var pngData = TextureHelper.EncodeBgraToPng(image);
        pngStopwatch.Stop();

        var root = Path.Combine(
            Path.GetTempPath(),
            "WH3AssetHost",
            "UastcProbe",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var inputPath = Path.Combine(root, "input.dds");
        var outputPath = Path.Combine(root, "output.ktx2");

        try
        {
            var stageStopwatch = Stopwatch.StartNew();
            var stagedBytes = WriteUncompressedBgraDds(inputPath, image);
            stageStopwatch.Stop();

            var args =
                $"-file {Quote(inputPath)} -uastc -uastc_level {UastcLevel} " +
                $"-ktx2 -ktx2_zstandard_level {Ktx2ZstdLevel} " +
                $"{(srgb ? string.Empty : "-linear ")}" +
                $"-output_file {Quote(outputPath)}";

            var basisuPath = ResolveBasisuExecutable();
            if (basisuPath == null)
                throw new FileNotFoundException(
                    $"Unable to find basisu.exe under '{AppContext.BaseDirectory}'.");

            var basisuStopwatch = Stopwatch.StartNew();
            var exitCode = RunBasisu(
                basisuPath,
                args,
                root,
                out var stdOut,
                out var stdErr);
            basisuStopwatch.Stop();

            if (exitCode != 0 || !File.Exists(outputPath))
            {
                Logger.Warning(
                    "UASTC KTX2 benchmark failed for {TexturePath}: exitCode={ExitCode}, ddsStage={DdsStageMs:F1}ms, basisu={BasisuMs:F1}ms, stderr={StdErr}, stdout={StdOut}",
                    texturePath,
                    exitCode,
                    stageStopwatch.Elapsed.TotalMilliseconds,
                    basisuStopwatch.Elapsed.TotalMilliseconds,
                    TrimForLog(stdErr),
                    TrimForLog(stdOut));
                return;
            }

            var uastcBytes = new FileInfo(outputPath).Length;
            var losslessEncodeMs = losslessKtx2.RgbaConvertMs + losslessKtx2.ZstdMs;
            var uastcTotalMs =
                stageStopwatch.Elapsed.TotalMilliseconds +
                basisuStopwatch.Elapsed.TotalMilliseconds;

            Logger.Information(
                "Texture codec benchmark for {TexturePath}: pngBytes={PngBytes}, pngEncode={PngEncodeMs:F1}ms, losslessKtx2Bytes={LosslessKtx2Bytes}, losslessEncode={LosslessEncodeMs:F1}ms (rgba={RgbaConvertMs:F1}ms,zstd={RawZstdMs:F1}ms,level={RawZstdLevel}), uastcKtx2Bytes={UastcKtx2Bytes}, uastcTotal={UastcTotalMs:F1}ms (ddsStage={DdsStageMs:F1}ms,basisu={BasisuMs:F1}ms,uastcLevel={UastcLevel},zstdLevel={Ktx2ZstdLevel}), uastcVsPng={UastcVsPng:P1}, uastcVsLossless={UastcVsLossless:P1}, srgb={Srgb}, stagedDdsBytes={StagedDdsBytes}",
                texturePath,
                pngData.Length,
                pngStopwatch.Elapsed.TotalMilliseconds,
                losslessKtx2.Ktx2Data.Length,
                losslessEncodeMs,
                losslessKtx2.RgbaConvertMs,
                losslessKtx2.ZstdMs,
                losslessKtx2.CompressionLevel,
                uastcBytes,
                uastcTotalMs,
                stageStopwatch.Elapsed.TotalMilliseconds,
                basisuStopwatch.Elapsed.TotalMilliseconds,
                UastcLevel,
                Ktx2ZstdLevel,
                pngData.Length == 0 ? 0d : (double)uastcBytes / pngData.Length,
                losslessKtx2.Ktx2Data.Length == 0
                    ? 0d
                    : (double)uastcBytes / losslessKtx2.Ktx2Data.Length,
                srgb,
                stagedBytes);
        }
        catch (Exception exception)
        {
            Logger.Warning(
                exception,
                "UASTC KTX2 benchmark failed for {TexturePath}",
                texturePath);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch (Exception exception)
            {
                Logger.Debug(
                    exception,
                    "Unable to clean UASTC benchmark directory {ProbeDirectory}",
                    root);
            }
        }
    }

    private static readonly Lazy<string?> BasisuExecutable = new(FindBasisuExecutable);
    private static int _basisuPathLogged;

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
            System.Text.Encoding.ASCII,
            leaveOpen: false);

        writer.Write(System.Text.Encoding.ASCII.GetBytes("DDS "));
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
}
