using System.Diagnostics;
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
        public TexturePngExportResult ExportWithData(string filePath, string outputPath, bool convertToBlueNormalMap);
        public TextureImageExportResult ExportKtx2WithData(string filePath, string outputPath, bool convertToBlueNormalMap);
        public ExportSupportEnum CanExportFile(PackFile file);
    }

    public class DdsToNormalPngExporter : IDdsToNormalPngExporter
    {
        private static readonly ILogger Logger = Logging.Create<DdsToNormalPngExporter>();
        private readonly IPackedFileLookup _packFileLookup;
        private readonly IImageSaveHandler _imageSaveHandler;

        public DdsToNormalPngExporter(
            IPackedFileLookup packFileLookup,
            IImageSaveHandler imageSaveHandler)
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
            => ExportWithData(filePath, outputPath, convertToBlueNormalMap).Path;

        public TexturePngExportResult ExportWithData(string filePath, string outputPath, bool convertToBlueNormalMap)
        {
            var totalStopwatch = Stopwatch.StartNew();
            var phaseStopwatch = Stopwatch.StartNew();

            var packFile = _packFileLookup.FindFile(filePath);
            phaseStopwatch.Stop();
            var lookupMs = phaseStopwatch.Elapsed.TotalMilliseconds;
            if (packFile == null)
            {
                totalStopwatch.Stop();
                Logger.Here().Debug(
                    "DDS normal texture timing for {TexturePath}: status=missing, total={TotalMs:F1}ms, lookup={LookupMs:F1}ms",
                    filePath,
                    totalStopwatch.Elapsed.TotalMilliseconds,
                    lookupMs);
                return new TexturePngExportResult("", Array.Empty<byte>());
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
            var decoded = TextureHelper.DecodeDdsToBgra(bytes);
            phaseStopwatch.Stop();
            var ddsDecodeMs = phaseStopwatch.Elapsed.TotalMilliseconds;

            var normalConvertMs = 0.0;
            if (convertToBlueNormalMap)
            {
                phaseStopwatch.Restart();
                ConvertPackedNormalToStandardInPlace(decoded.BgraPixels);
                phaseStopwatch.Stop();
                normalConvertMs = phaseStopwatch.Elapsed.TotalMilliseconds;
            }

            phaseStopwatch.Restart();
            var imgBytes = TextureHelper.EncodeBgraToPng(decoded);
            phaseStopwatch.Stop();
            var pngEncodeMs = phaseStopwatch.Elapsed.TotalMilliseconds;
            if (imgBytes == null || !imgBytes.Any())
                throw new Exception($"image data invalid/empty. imgBytes.Count = {imgBytes?.Length}");

            phaseStopwatch.Restart();
            _imageSaveHandler.Save(imgBytes, outputFilePath);
            phaseStopwatch.Stop();
            var saveMs = phaseStopwatch.Elapsed.TotalMilliseconds;
            totalStopwatch.Stop();

            Logger.Here().Debug(
                "DDS normal texture timing for {TexturePath}: total={TotalMs:F1}ms, lookup={LookupMs:F1}ms, read={ReadMs:F1}ms, ddsDecode={DdsDecodeMs:F1}ms, normalConvert={NormalConvertMs:F1}ms, pngEncode={PngEncodeMs:F1}ms, save={SaveMs:F1}ms, inputBytes={InputBytes}, outputBytes={OutputBytes}, blueNormal={ConvertToBlueNormalMap}",
                filePath,
                totalStopwatch.Elapsed.TotalMilliseconds,
                lookupMs,
                readMs,
                ddsDecodeMs,
                normalConvertMs,
                pngEncodeMs,
                saveMs,
                bytes.Length,
                imgBytes.Length,
                convertToBlueNormalMap);

            return new TexturePngExportResult(outputFilePath, imgBytes);
        }

        public TextureImageExportResult ExportKtx2WithData(
            string filePath,
            string outputPath,
            bool convertToBlueNormalMap)
        {
            var totalStopwatch = Stopwatch.StartNew();
            var phaseStopwatch = Stopwatch.StartNew();

            var packFile = _packFileLookup.FindFile(filePath);
            phaseStopwatch.Stop();
            var lookupMs = phaseStopwatch.Elapsed.TotalMilliseconds;
            if (packFile == null)
            {
                totalStopwatch.Stop();
                Logger.Here().Debug(
                    "KTX2 normal texture timing for {TexturePath}: status=missing, total={TotalMs:F1}ms, lookup={LookupMs:F1}ms",
                    filePath,
                    totalStopwatch.Elapsed.TotalMilliseconds,
                    lookupMs);
                return new TextureImageExportResult("", Array.Empty<byte>());
            }

            var fileName = Path.GetFileNameWithoutExtension(
                filePath.Replace('\\', Path.DirectorySeparatorChar));
            var outDirectory = Path.GetDirectoryName(outputPath) ?? string.Empty;
            var suffix = convertToBlueNormalMap ? string.Empty : "_raw";
            var outputFilePath = Path.Combine(outDirectory, fileName + suffix + ".ktx2");

            phaseStopwatch.Restart();
            var bytes = packFile.DataSource.ReadData();
            phaseStopwatch.Stop();
            var readMs = phaseStopwatch.Elapsed.TotalMilliseconds;
            if (bytes == null || !bytes.Any())
                throw new Exception($"Could not read file data. bytes.Count = {bytes?.Length}");

            phaseStopwatch.Restart();
            var decoded = TextureHelper.DecodeDdsToBgra(bytes);
            phaseStopwatch.Stop();
            var ddsDecodeMs = phaseStopwatch.Elapsed.TotalMilliseconds;

            var normalConvertMs = 0.0;
            if (convertToBlueNormalMap)
            {
                phaseStopwatch.Restart();
                ConvertPackedNormalToStandardInPlace(decoded.BgraPixels);
                phaseStopwatch.Stop();
                normalConvertMs = phaseStopwatch.Elapsed.TotalMilliseconds;
            }

            var encoded = TextureHelper.EncodeBgraToKtx2(decoded, srgb: false);

            phaseStopwatch.Restart();
            _imageSaveHandler.Save(encoded.Ktx2Data, outputFilePath);
            phaseStopwatch.Stop();
            var saveMs = phaseStopwatch.Elapsed.TotalMilliseconds;
            totalStopwatch.Stop();

            Logger.Here().Debug(
                "KTX2 normal texture timing for {TexturePath}: total={TotalMs:F1}ms, lookup={LookupMs:F1}ms, read={ReadMs:F1}ms, ddsDecode={DdsDecodeMs:F1}ms, normalConvert={NormalConvertMs:F1}ms, rgbaConvert={RgbaConvertMs:F1}ms, zstd={ZstdMs:F1}ms, save={SaveMs:F1}ms, inputBytes={InputBytes}, zstdBytes={ZstdBytes}, outputBytes={OutputBytes}, blueNormal={ConvertToBlueNormalMap}",
                filePath,
                totalStopwatch.Elapsed.TotalMilliseconds,
                lookupMs,
                readMs,
                ddsDecodeMs,
                normalConvertMs,
                encoded.RgbaConvertMs,
                encoded.ZstdMs,
                saveMs,
                bytes.Length,
                encoded.ZstdBytes,
                encoded.Ktx2Data.Length,
                convertToBlueNormalMap);

            return new TextureImageExportResult(outputFilePath, encoded.Ktx2Data);
        }

        private static void ConvertPackedNormalToStandardInPlace(byte[] pixels)
        {
            for (var index = 0; index < pixels.Length; index += 4)
            {
                var packedR = pixels[index + 2];
                var packedG = pixels[index + 1];
                var packedA = pixels[index + 3];

                // WH3 stores tangent-space X as R*A and Y as G. Keep the
                // shader's Y orientation; only reconstruct Z and emit a
                // conventional opaque RGB normal texture.
                var x01 = (packedR / 255d) * (packedA / 255d);
                var y01 = packedG / 255d;
                var normalX = Math.Clamp(2d * x01 - 1d, -1d, 1d);
                var normalY = Math.Clamp(2d * y01 - 1d, -1d, 1d);
                var normalZ = Math.Sqrt(Math.Max(0d, 1d - normalX * normalX - normalY * normalY));

                pixels[index] = EncodeNormalComponent(normalZ);
                pixels[index + 1] = EncodeNormalComponent(normalY);
                pixels[index + 2] = EncodeNormalComponent(normalX);
                pixels[index + 3] = 255;
            }
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
