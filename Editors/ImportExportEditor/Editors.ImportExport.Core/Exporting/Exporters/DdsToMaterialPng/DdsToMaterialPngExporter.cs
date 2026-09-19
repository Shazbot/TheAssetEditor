using System.Diagnostics;
using System.Drawing;
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
        public ExportSupportEnum CanExportFile(PackFile file);
    }

    public class DdsToMaterialPngExporter : IDdsToMaterialPngExporter
    {
        private static readonly ILogger Logger = Logging.Create<DdsToMaterialPngExporter>();
        private readonly IPackedFileLookup _packFileLookup;
        private readonly IImageSaveHandler _imageSaveHandler;
        public DdsToMaterialPngExporter(IPackedFileLookup packFileLookup, IImageSaveHandler imageSaveHandler)
        {
            _packFileLookup = packFileLookup;
            _imageSaveHandler = imageSaveHandler;
        }

        public string Export(string filePath, string outputPath, bool convertToBlenderFormat)
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
                return "";
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
            var imgBytes = TextureHelper.ConvertDdsToPng(bytes);
            phaseStopwatch.Stop();
            var ddsToPngMs = phaseStopwatch.Elapsed.TotalMilliseconds;
            if (imgBytes == null || !imgBytes.Any())
                throw new Exception($"image data invalid/empty. imgBytes.Count = {imgBytes?.Length}");

            var channelConvertMs = 0.0;
            if (convertToBlenderFormat)
            {
                phaseStopwatch.Restart();
                imgBytes = ConvertToBlenderFormat(imgBytes);
                phaseStopwatch.Stop();
                channelConvertMs = phaseStopwatch.Elapsed.TotalMilliseconds;
            }

            phaseStopwatch.Restart();
            _imageSaveHandler.Save(imgBytes, outFilePath);
            phaseStopwatch.Stop();
            var saveMs = phaseStopwatch.Elapsed.TotalMilliseconds;
            totalStopwatch.Stop();

            Logger.Here().Information(
                "DDS material texture timing for {TexturePath}: total={TotalMs:F1}ms, lookup={LookupMs:F1}ms, read={ReadMs:F1}ms, ddsToPng={DdsToPngMs:F1}ms, channelConvert={ChannelConvertMs:F1}ms, save={SaveMs:F1}ms, inputBytes={InputBytes}, outputBytes={OutputBytes}, blender={ConvertToBlender}",
                filePath,
                totalStopwatch.Elapsed.TotalMilliseconds,
                lookupMs,
                readMs,
                ddsToPngMs,
                channelConvertMs,
                saveMs,
                bytes.Length,
                imgBytes.Length,
                convertToBlenderFormat);

            return outFilePath;
        }

        public ExportSupportEnum CanExportFile(PackFile file)
        {
            if (FileExtensionHelper.IsDdsMaterialFile(file.Name))
                return ExportSupportEnum.HighPriority;
            else if (FileExtensionHelper.IsDdsFile(file.Name))
                return ExportSupportEnum.Supported;
            return ExportSupportEnum.NotSupported;
        }

        private static byte[] ConvertToBlenderFormat(byte[] imgBytes)
        {
            using var ms = new MemoryStream(imgBytes);

            using var image = Image.FromStream(ms);
            using var bitmap = new Bitmap(image);
            var pixels = BitmapPixelBuffer.ReadBgra(bitmap);
            for (var index = 0; index < pixels.Length; index += 4)
            {
                (pixels[index], pixels[index + 2]) = (pixels[index + 2], pixels[index]);
                pixels[index + 3] = 255;
            }

            using var converted = BitmapPixelBuffer.CreateBitmap(bitmap.Width, bitmap.Height, pixels);
            using var output = new MemoryStream();
            converted.Save(output, System.Drawing.Imaging.ImageFormat.Png);
            return output.ToArray();
        }
    }
}
