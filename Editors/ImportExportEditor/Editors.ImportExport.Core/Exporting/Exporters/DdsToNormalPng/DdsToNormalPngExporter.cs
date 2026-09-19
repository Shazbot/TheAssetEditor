using System.Diagnostics;
using System.Drawing;
using System.IO;
using Editors.ImportExport.Misc;
using MeshImportExport;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;

namespace Editors.ImportExport.Exporting.Exporters.DdsToNormalPng
{

    public interface IDdsToNormalPngExporter
    {
        public string Export(string filePath, string outputPath, bool convertToBlueNormalMap);
        public ExportSupportEnum CanExportFile(PackFile file);
    }

    public class DdsToNormalPngExporter : IDdsToNormalPngExporter
    {
        private static readonly ILogger Logger = Logging.Create<DdsToNormalPngExporter>();
        private readonly IPackedFileLookup _packFileLookup;
        private readonly IImageSaveHandler _imageSaveHandler;

        public DdsToNormalPngExporter(IPackedFileLookup packFileLookup, IImageSaveHandler imageSaveHandler)
        {
            _packFileLookup = packFileLookup;
            _imageSaveHandler = imageSaveHandler;
        }

        public ExportSupportEnum CanExportFile(PackFile file)
        {
            if (FileExtensionHelper.IsDdsMaterialFile(file.Name))
                return ExportSupportEnum.HighPriority;
            else if (FileExtensionHelper.IsDdsFile(file.Name))
                return ExportSupportEnum.Supported;
            return ExportSupportEnum.NotSupported;
        }

        public string Export(string filePath, string outputPath, bool convertToBlueNormalMap)
        {
            var totalStopwatch = Stopwatch.StartNew();
            var phaseStopwatch = Stopwatch.StartNew();

            var packFile = _packFileLookup.FindFile(filePath);
            phaseStopwatch.Stop();
            var lookupMs = phaseStopwatch.Elapsed.TotalMilliseconds;
            if (packFile == null)
            {
                totalStopwatch.Stop();
                Logger.Here().Information(
                    "DDS normal texture timing for {TexturePath}: status=missing, total={TotalMs:F1}ms, lookup={LookupMs:F1}ms",
                    filePath,
                    totalStopwatch.Elapsed.TotalMilliseconds,
                    lookupMs);
                return "";
            }

            var fileName = Path.GetFileNameWithoutExtension(
                filePath.Replace('\\', Path.DirectorySeparatorChar));
            var outDirectory = Path.GetDirectoryName(outputPath) ?? string.Empty;
            var outputFilePath = Path.Combine(
                outDirectory,
                convertToBlueNormalMap ? fileName + ".png" : fileName + "_raw.png");

            phaseStopwatch.Restart();
            var bytes = packFile.DataSource.ReadData();
            phaseStopwatch.Stop();
            var readMs = phaseStopwatch.Elapsed.TotalMilliseconds;
            if (bytes == null || !bytes.Any())
                throw new Exception($"Could not read file data. bytes.Count = {bytes?.Length}");

            phaseStopwatch.Restart();
            var imgBytes = TextureHelper.ConvertDdsToPng(bytes);
            phaseStopwatch.Stop();
            var ddsToPngMs = phaseStopwatch.Elapsed.TotalMilliseconds;
            if (imgBytes == null || !imgBytes.Any())
                throw new Exception($"image data invalid/empty. imgBytes.Count = {imgBytes?.Length}");

            var normalConvertMs = 0.0;
            if (convertToBlueNormalMap)
            {
                phaseStopwatch.Restart();
                imgBytes = ConvertPackedNormalToStandard(imgBytes);
                phaseStopwatch.Stop();
                normalConvertMs = phaseStopwatch.Elapsed.TotalMilliseconds;
            }

            phaseStopwatch.Restart();
            _imageSaveHandler.Save(imgBytes, outputFilePath);
            phaseStopwatch.Stop();
            var saveMs = phaseStopwatch.Elapsed.TotalMilliseconds;
            totalStopwatch.Stop();

            Logger.Here().Information(
                "DDS normal texture timing for {TexturePath}: total={TotalMs:F1}ms, lookup={LookupMs:F1}ms, read={ReadMs:F1}ms, ddsToPng={DdsToPngMs:F1}ms, normalConvert={NormalConvertMs:F1}ms, save={SaveMs:F1}ms, inputBytes={InputBytes}, outputBytes={OutputBytes}, blueNormal={ConvertToBlueNormalMap}",
                filePath,
                totalStopwatch.Elapsed.TotalMilliseconds,
                lookupMs,
                readMs,
                ddsToPngMs,
                normalConvertMs,
                saveMs,
                bytes.Length,
                imgBytes.Length,
                convertToBlueNormalMap);

            return outputFilePath;
        }

        private static byte[] ConvertPackedNormalToStandard(byte[] pngBytes)
        {
            using var inputStream = new MemoryStream(pngBytes);
            using var image = Image.FromStream(inputStream);
            using var source = new Bitmap(image);
            var sourcePixels = BitmapPixelBuffer.ReadBgra(source);
            var outputPixels = new byte[sourcePixels.Length];

            for (var index = 0; index < sourcePixels.Length; index += 4)
            {
                var packedR = sourcePixels[index + 2];
                var packedG = sourcePixels[index + 1];
                var packedA = sourcePixels[index + 3];

                // WH3 stores tangent-space X as R*A and Y as G. Keep the
                // shader's Y orientation; only reconstruct Z and emit a
                // conventional opaque RGB normal texture.
                var x01 = (packedR / 255d) * (packedA / 255d);
                var y01 = packedG / 255d;
                var normalX = Math.Clamp(2d * x01 - 1d, -1d, 1d);
                var normalY = Math.Clamp(2d * y01 - 1d, -1d, 1d);
                var normalZ = Math.Sqrt(Math.Max(0d, 1d - normalX * normalX - normalY * normalY));

                outputPixels[index] = EncodeNormalComponent(normalZ);
                outputPixels[index + 1] = EncodeNormalComponent(normalY);
                outputPixels[index + 2] = EncodeNormalComponent(normalX);
                outputPixels[index + 3] = 255;
            }

            using var output = BitmapPixelBuffer.CreateBitmap(source.Width, source.Height, outputPixels);
            using var outputStream = new MemoryStream();
            output.Save(outputStream, System.Drawing.Imaging.ImageFormat.Png);
            return outputStream.ToArray();
        }

        private static byte EncodeNormalComponent(double component)
        {
            if (!double.IsFinite(component))
                return 0;

            var encoded = ((Math.Clamp(component, -1d, 1d) + 1d) * 0.5d) * 255d;
            return (byte)Math.Clamp((int)Math.Round(encoded, MidpointRounding.AwayFromZero), 0, 255);
        }
    }
}
