using System.Collections.Concurrent;
using System.IO;

namespace Editors.ImportExport.Exporting.Exporters.RmvToGltf
{
    public sealed record ExportWarning(string Code, string Message);

    public sealed record ExportError(string Code, string Message, string? Details = null);

    public sealed record ExportResult(
        bool Success,
        string? PrimaryFile,
        IReadOnlyList<string> AuxiliaryFiles,
        IReadOnlyList<ExportWarning> Warnings,
        IReadOnlyList<ExportError> Errors);

    /// <summary>
    /// A result-producing facade over the existing exporter. The exporter
    /// remains responsible for model/material/skeleton/animation conversion;
    /// this class provides the non-UI contract needed by a process or service.
    /// Calls targeting the same output directory are serialized in-process so
    /// the auxiliary-file snapshot cannot interleave with another export in
    /// this host. The underlying texture handler does not yet expose a full
    /// artifact manifest, so callers running multiple host processes must
    /// still dedicate an output directory to each export.
    /// </summary>
    public sealed class HeadlessGltfExportService
    {
        private static readonly ConcurrentDictionary<string, object> OutputDirectoryLocks = new(StringComparer.OrdinalIgnoreCase);
        private readonly RmvToGltfExporter _exporter;

        public HeadlessGltfExportService(RmvToGltfExporter exporter)
        {
            _exporter = exporter;
        }

        public ExportResult Export(RmvToGltfExporterSettings settings)
        {
            string outputPath;
            string? outputDirectory;
            try
            {
                outputPath = Path.GetFullPath(settings.OutputPath);
                outputDirectory = Path.GetDirectoryName(outputPath);
            }
            catch (Exception exception)
            {
                return Failure("InvalidOutputPath", exception.Message);
            }

            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                return Failure("InvalidOutputPath", $"Output path '{settings.OutputPath}' has no parent directory.");
            }

            var directoryLock = OutputDirectoryLocks.GetOrAdd(outputDirectory, static _ => new object());
            lock (directoryLock)
            {
                return ExportSerialized(settings, outputPath, outputDirectory);
            }
        }

        private ExportResult ExportSerialized(
            RmvToGltfExporterSettings settings,
            string outputPath,
            string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);
            var before = CaptureFiles(outputDirectory);

            try
            {
                _exporter.Export(settings with { OutputPath = outputPath });
            }
            catch (Exception exception)
            {
                return new ExportResult(
                    false,
                    null,
                    CaptureAuxiliaryFiles(outputDirectory, before, outputPath),
                    Array.Empty<ExportWarning>(),
                    [new ExportError("ExportFailed", exception.Message, exception.ToString())]);
            }

            if (File.Exists(outputPath) == false)
            {
                return new ExportResult(
                    false,
                    null,
                    CaptureAuxiliaryFiles(outputDirectory, before, outputPath),
                    Array.Empty<ExportWarning>(),
                    [new ExportError(
                        "OutputMissing",
                        $"The exporter completed without creating the requested output '{outputPath}'.")]);
            }

            return new ExportResult(
                true,
                outputPath,
                CaptureAuxiliaryFiles(outputDirectory, before, outputPath),
                Array.Empty<ExportWarning>(),
                Array.Empty<ExportError>());
        }

        private static ExportResult Failure(string code, string message)
            => new(
                false,
                null,
                Array.Empty<string>(),
                Array.Empty<ExportWarning>(),
                [new ExportError(code, message)]);

        private static Dictionary<string, FileStamp> CaptureFiles(string directory)
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .ToDictionary(
                    Path.GetFullPath,
                    path => new FileStamp(new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<string> CaptureAuxiliaryFiles(
            string directory,
            IReadOnlyDictionary<string, FileStamp> before,
            string primaryPath)
        {
            return CaptureFiles(directory)
                .Where(pair => string.Equals(pair.Key, primaryPath, StringComparison.OrdinalIgnoreCase) == false)
                .Where(pair => before.TryGetValue(pair.Key, out var previous) == false || previous != pair.Value)
                .Select(pair => pair.Key)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private readonly record struct FileStamp(long Length, DateTime LastWriteUtc);
    }
}
