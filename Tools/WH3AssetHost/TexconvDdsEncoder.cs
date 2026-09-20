using System.Diagnostics;

namespace WH3AssetHost;

internal sealed class TexconvDdsEncoder
{
    private const int TexconvTimeoutMilliseconds = 120_000;
    private readonly string _texconvPath;

    public TexconvDdsEncoder(string? texconvPath = null)
    {
        _texconvPath = Path.GetFullPath(
            texconvPath ?? Path.Combine(AppContext.BaseDirectory, "texconv.exe"));
        if (!File.Exists(_texconvPath))
        {
            throw new FileNotFoundException(
                $"Bundled DirectXTex texconv.exe was not found at '{_texconvPath}'.",
                _texconvPath);
        }
    }

    public byte[] EncodePngFile(string pngPath, DdsSourceFormat sourceFormat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pngPath);
        ArgumentNullException.ThrowIfNull(sourceFormat);

        var fullPngPath = Path.GetFullPath(pngPath);
        if (!File.Exists(fullPngPath))
            throw new FileNotFoundException("Painted PNG input was not found.", fullPngPath);

        var outputDirectory = Path.Combine(
            Path.GetDirectoryName(fullPngPath) ?? Path.GetTempPath(),
            ".texconv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _texconvPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = outputDirectory
            };
            startInfo.ArgumentList.Add("-nologo");
            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add("-ft");
            startInfo.ArgumentList.Add("DDS");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(sourceFormat.TexconvFormat);
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add(sourceFormat.MipCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (sourceFormat.IsSrgb)
                startInfo.ArgumentList.Add("-srgb");
            if (sourceFormat.PreferLegacyHeader)
                startInfo.ArgumentList.Add("-dx9");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add(outputDirectory);
            startInfo.ArgumentList.Add(fullPngPath);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                throw new InvalidOperationException("Failed to start bundled texconv.exe.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TexconvTimeoutMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Preserve the timeout error below even if the process already exited.
                }
                throw new TimeoutException(
                    $"texconv did not finish within {TexconvTimeoutMilliseconds / 1000} seconds.");
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"texconv failed with exit code {process.ExitCode}: "
                    + $"{(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim()}");
            }

            var outputPath = Path.Combine(
                outputDirectory,
                Path.GetFileNameWithoutExtension(fullPngPath) + ".DDS");
            if (!File.Exists(outputPath))
            {
                outputPath = Directory.EnumerateFiles(outputDirectory, "*.dds", SearchOption.TopDirectoryOnly)
                    .SingleOrDefault()
                    ?? throw new InvalidOperationException(
                        $"texconv completed successfully but did not create a DDS file. Output: {stdout.Trim()}");
            }

            var dds = File.ReadAllBytes(outputPath);
            var actual = DdsFormatInspector.Inspect(dds);
            if (!string.Equals(actual.TexconvFormat, sourceFormat.TexconvFormat, StringComparison.OrdinalIgnoreCase)
                || actual.Width != sourceFormat.Width
                || actual.Height != sourceFormat.Height
                || actual.MipCount != sourceFormat.MipCount)
            {
                throw new InvalidDataException(
                    "texconv output did not preserve the requested DDS layout: "
                    + $"expected {sourceFormat.TexconvFormat} {sourceFormat.Width}x{sourceFormat.Height} "
                    + $"{sourceFormat.MipCount} mips, got {actual.TexconvFormat} "
                    + $"{actual.Width}x{actual.Height} {actual.MipCount} mips.");
            }

            return dds;
        }
        finally
        {
            try
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
            catch
            {
                // Staging cleanup is best-effort.
            }
        }
    }
}
