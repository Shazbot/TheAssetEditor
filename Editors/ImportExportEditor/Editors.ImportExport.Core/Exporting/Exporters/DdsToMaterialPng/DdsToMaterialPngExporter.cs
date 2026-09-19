using System.Diagnostics;
using System.IO;
using Editors.ImportExport.Misc;
using MeshImportExport;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;

namespace Editors.ImportExport.Exporting.Exporters.DdsToMaterialPng
{

    public interface IDdsToMaterialPngExporter
    {
        public string Export(string filePath, string outputPath, bool convertToBlenderFormat);
        public TexturePngExportResult ExportWithData(string filePath, string outputPath, bool convertToBlenderFormat);
        public TextureImageExportResult ExportKtx2WithData(string filePath, string outputPath, bool convertToBlenderFormat, bool srgb);
        public ExportSupportEnum CanExportFile(PackFile file);
    }

    public class DdsToMaterialPngExporter : IDdsToMaterialPngExporter
    {
        private static readonly ILogger Logger = Logging.Create<DdsToMaterialPngExporter>();
        private readonly IPackedFileLookup _packFileLookup;
        private readonly IImageSaveHandler _imageSaveHandler;
        private readonly bool _enableKtx2Probe;
        private readonly ITextureEncodingProbe? _textureEncodingProbe;

        public DdsToMaterialPngExporter(
            IPackedFileLookup packFileLookup,
            IImageSaveHandler imageSaveHandler,
            bool enableKtx2Probe = false,
            ITextureEncodingProbe? textureEncodingProbe = null)
        {
            _packFileLookup = packFileLookup;
            _imageSaveHandler = imageSaveHandler;
            _enableKtx2Probe = enableKtx2Probe;
            _textureEncodingProbe = textureEncodingProbe;
        }

        public string Export(string filePath, string outputPath, bool convertToBlenderFormat)
            => ExportWithData(filePath, outputPath, convertToBlenderFormat).Path;

        public TexturePngExportResult ExportWithData(string filePath, string outputPath, bool convertToBlenderFormat)
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
                    "DDS material texture timing for {TexturePath}: status=missing, total={TotalMs:F1}ms, lookup={LookupMs:F1}ms",
                    filePath,
                    totalStopwatch.Elapsed.TotalMilliseconds,
                    lookupMs);
                return new TexturePngExportResult("", Array.Empty<byte>());
            }

            var fileName = Path.GetFileNameWithoutExtension(
                filePath.Replace('\\', Path.DirectorySeparatorChar));
            var outDirectory = Path.GetDirectoryName(outputPath) ?? string.Empty;
            var outFilePath = Path.Combine(outDirectory, fileName + ".png");

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

            var channelConvertMs = 0.0;
            if (convertToBlenderFormat)
            {
                phaseStopwatch.Restart();
                ConvertToBlenderFormatInPlace(decoded.BgraPixels);
                phaseStopwatch.Stop();
                channelConvertMs = phaseStopwatch.Elapsed.TotalMilliseconds;
            }

            phaseStopwatch.Restart();
            var imgBytes = TextureHelper.EncodeBgraToPng(decoded);
            phaseStopwatch.Stop();
            var pngEncodeMs = phaseStopwatch.Elapsed.TotalMilliseconds;
            if (imgBytes == null || !imgBytes.Any())
                throw new Exception($"image data invalid/empty. imgBytes.Count = {imgBytes?.Length}");

            phaseStopwatch.Restart();
            _imageSaveHandler.Save(imgBytes, outFilePath);
            phaseStopwatch.Stop();
            var saveMs = phaseStopwatch.Elapsed.TotalMilliseconds;
            totalStopwatch.Stop();

            if (_textureEncodingProbe != null)
            {
                var srgb = IsLikelySrgbTexture(filePath, convertToBlenderFormat);
                var losslessKtx2 = TextureHelper.EncodeBgraToKtx2(decoded, srgb);
                _textureEncodingProbe.Probe(filePath, decoded, srgb, losslessKtx2);
            }

            if (_enableKtx2Probe)
            {
                var probe = TextureHelper.ProbeBgraToRgbaZstd(decoded);
                Logger.Here().Information(
                    "KTX2 Zstd feasibility for {TexturePath}: rawRgbaBytes={RawRgbaBytes}, zstdBytes={ZstdBytes}, rgbaConvert={RgbaConvertMs:F1}ms, zstd={ZstdMs:F1}ms, level={ZstdLevel}, pngBytes={PngBytes}, pngEncode={PngEncodeMs:F1}ms, zstdVsPng={ZstdVsPng:P1}",
                    filePath,
                    probe.RawRgbaBytes,
                    probe.ZstdBytes,
                    probe.RgbaConvertMs,
                    probe.ZstdMs,
                    probe.CompressionLevel,
                    imgBytes.Length,
                    pngEncodeMs,
                    imgBytes.Length == 0 ? 0d : (double)probe.ZstdBytes / imgBytes.Length);
            }

            Logger.Here().Information(
                "DDS material texture timing for {TexturePath}: total={TotalMs:F1}ms, lookup={LookupMs:F1}ms, read={ReadMs:F1}ms, ddsDecode={DdsDecodeMs:F1}ms, channelConvert={ChannelConvertMs:F1}ms, pngEncode={PngEncodeMs:F1}ms, save={SaveMs:F1}ms, inputBytes={InputBytes}, outputBytes={OutputBytes}, blender={ConvertToBlender}",
                filePath,
                totalStopwatch.Elapsed.TotalMilliseconds,
                lookupMs,
                readMs,
                ddsDecodeMs,
                channelConvertMs,
                pngEncodeMs,
                saveMs,
                bytes.Length,
                imgBytes.Length,
                convertToBlenderFormat);

            return new TexturePngExportResult(outFilePath, imgBytes);
        }

        public TextureImageExportResult ExportKtx2WithData(
            string filePath,
            string outputPath,
            bool convertToBlenderFormat,
            bool srgb)
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
                    "KTX2 material texture timing for {TexturePath}: status=missing, total={TotalMs:F1}ms, lookup={LookupMs:F1}ms",
                    filePath,
                    totalStopwatch.Elapsed.TotalMilliseconds,
                    lookupMs);
                return new TextureImageExportResult("", Array.Empty<byte>());
            }

            var fileName = Path.GetFileNameWithoutExtension(
                filePath.Replace('\\', Path.DirectorySeparatorChar));
            var outDirectory = Path.GetDirectoryName(outputPath) ?? string.Empty;
            var outFilePath = Path.Combine(outDirectory, fileName + ".ktx2");

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

            var channelConvertMs = 0.0;
            if (convertToBlenderFormat)
            {
                phaseStopwatch.Restart();
                ConvertToBlenderFormatInPlace(decoded.BgraPixels);
                phaseStopwatch.Stop();
                channelConvertMs = phaseStopwatch.Elapsed.TotalMilliseconds;
            }

            if (!TextureHelper.CanEncodeKtx2ForSharpGltf(decoded))
            {
                phaseStopwatch.Restart();
                var pngData = TextureHelper.EncodeBgraToPng(decoded);
                phaseStopwatch.Stop();
                var pngEncodeMs = phaseStopwatch.Elapsed.TotalMilliseconds;
                var pngPath = Path.Combine(outDirectory, fileName + ".png");

                phaseStopwatch.Restart();
                _imageSaveHandler.Save(pngData, pngPath);
                phaseStopwatch.Stop();
                var pngSaveMs = phaseStopwatch.Elapsed.TotalMilliseconds;
                totalStopwatch.Stop();

                Logger.Here().Information(
                    "KTX2 material texture fallback for {TexturePath}: reason=rawKtx2Compatibility, width={Width}, height={Height}, pngEncode={PngEncodeMs:F1}ms, save={SaveMs:F1}ms, outputBytes={OutputBytes}, srgb={Srgb}, blender={ConvertToBlender}",
                    filePath,
                    decoded.Width,
                    decoded.Height,
                    pngEncodeMs,
                    pngSaveMs,
                    pngData.Length,
                    srgb,
                    convertToBlenderFormat);

                return new TextureImageExportResult(pngPath, pngData);
            }

            var encoded = TextureHelper.EncodeBgraToKtx2(decoded, srgb);

            phaseStopwatch.Restart();
            _imageSaveHandler.Save(encoded.Ktx2Data, outFilePath);
            phaseStopwatch.Stop();
            var saveMs = phaseStopwatch.Elapsed.TotalMilliseconds;
            totalStopwatch.Stop();

            _textureEncodingProbe?.Probe(filePath, decoded, srgb, encoded);

            Logger.Here().Information(
                "KTX2 material texture timing for {TexturePath}: total={TotalMs:F1}ms, lookup={LookupMs:F1}ms, read={ReadMs:F1}ms, ddsDecode={DdsDecodeMs:F1}ms, channelConvert={ChannelConvertMs:F1}ms, rgbaConvert={RgbaConvertMs:F1}ms, zstd={ZstdMs:F1}ms, save={SaveMs:F1}ms, inputBytes={InputBytes}, zstdBytes={ZstdBytes}, outputBytes={OutputBytes}, srgb={Srgb}, blender={ConvertToBlender}",
                filePath,
                totalStopwatch.Elapsed.TotalMilliseconds,
                lookupMs,
                readMs,
                ddsDecodeMs,
                channelConvertMs,
                encoded.RgbaConvertMs,
                encoded.ZstdMs,
                saveMs,
                bytes.Length,
                encoded.ZstdBytes,
                encoded.Ktx2Data.Length,
                srgb,
                convertToBlenderFormat);

            return new TextureImageExportResult(outFilePath, encoded.Ktx2Data);
        }

        public ExportSupportEnum CanExportFile(PackFile file)
        {
            if (FileExtensionHelper.IsDdsMaterialFile(file.Name))
                return ExportSupportEnum.HighPriority;
            else if (FileExtensionHelper.IsDdsFile(file.Name))
                return ExportSupportEnum.Supported;
            return ExportSupportEnum.NotSupported;
        }

        private static bool IsLikelySrgbTexture(
            string filePath,
            bool convertToBlenderFormat)
        {
            if (convertToBlenderFormat)
                return false;

            var normalized = filePath.Replace('\\', '/').ToLowerInvariant();
            return !normalized.Contains("normal")
                && !normalized.Contains("material")
                && !normalized.Contains("gloss")
                && !normalized.Contains("roughness")
                && !normalized.Contains("metallic")
                && !normalized.Contains("occlusion")
                && !normalized.Contains("_ao")
                && !normalized.Contains("mask");
        }

        private static void ConvertToBlenderFormatInPlace(byte[] pixels)
        {
            for (var index = 0; index < pixels.Length; index += 4)
            {
                (pixels[index], pixels[index + 2]) = (pixels[index + 2], pixels[index]);
                pixels[index + 3] = 255;
            }
        }
    }
}
