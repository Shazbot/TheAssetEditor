using DirectXTexNet;
using Editors.ImportExport.Common.Interfaces;
using Editors.ImportExport.Importing.Importers.PngToDds.Helpers;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Models.FileSources;
using Shared.Core.Settings;
using Shared.GameFormats.RigidModel.Types;

namespace Editors.ImportExport.Importing.Importers.PngToDds
{
    public class PngToDdsImporter
    {
        public static PackFile Import(string inputPath, TextureType textureType, GameTypeEnum gameType, string outFileName)
            => ImportInternal(inputPath, textureType, gameType, outFileName, processForGame: true);

        public static PackFile ImportRaw(byte[] pngBytes, TextureType textureType, GameTypeEnum gameType, string outFileName)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"asset_editor_atlas_{Guid.NewGuid():N}.png");
            try
            {
                File.WriteAllBytes(tempPath, pngBytes);
                return ImportInternal(tempPath, textureType, gameType, outFileName, processForGame: false);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        private static PackFile ImportInternal(
            string inputPath,
            TextureType textureType,
            GameTypeEnum gameType,
            string outFileName,
            bool processForGame)
        {
            var wicFlags = processForGame
                ? WIC_FLAGS.DEFAULT_SRGB
                : IsLinearTexture(textureType) ? WIC_FLAGS.IGNORE_SRGB : WIC_FLAGS.DEFAULT_SRGB;
            var scratchImagePng = TexHelper.Instance.LoadFromWICFile(inputPath, wicFlags);

            var processedImage = processForGame
                ? ImageProcessorFactory.CreateImageProcessor(textureType).Transform(scratchImagePng)
                : scratchImagePng;

            var imageWithMips = processedImage.GenerateMipMaps(TEX_FILTER_FLAGS.DEFAULT, 0);
            var ddsFormat = DDSFormatHelper.GetDDSFormat(gameType, textureType);
            var ddsImage = imageWithMips.Compress(ddsFormat, TEX_COMPRESS_FLAGS.DEFAULT, 0.5f);

            var ddsMemStream = ddsImage.SaveToDDSMemory(DDS_FLAGS.NONE);
            var ddsBytes = new byte[ddsMemStream.Length];
            ddsMemStream.Read(ddsBytes, 0, ddsBytes.Length);

            return new PackFile(outFileName, new MemorySource(ddsBytes));
        }

        private static bool IsLinearTexture(TextureType textureType)
            => textureType is TextureType.Normal or TextureType.Mask or TextureType.Gloss;
    }
}
