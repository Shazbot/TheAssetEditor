using DirectXTexNet;
using Editors.ImportExport.Common.Interfaces;
using Editors.ImportExport.Importing.Importers.PngToDds.Helpers;
using System.IO;
using System.Runtime.InteropServices;
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

        public static PackFile ImportRawMipChain(
            IReadOnlyList<byte[]> pngMipLevels,
            TextureType textureType,
            GameTypeEnum gameType,
            string outFileName)
        {
            if (pngMipLevels.Count == 0)
                throw new ArgumentException("At least one mip level is required.", nameof(pngMipLevels));

            var wicFlags = IsLinearTexture(textureType)
                ? WIC_FLAGS.IGNORE_SRGB
                : WIC_FLAGS.DEFAULT_SRGB;

            using var baseImage = LoadPngFromMemory(pngMipLevels[0], wicFlags);
            using var imageWithMips = baseImage.CreateCopyWithEmptyMipMaps(
                pngMipLevels.Count,
                baseImage.GetMetadata().Format,
                CP_FLAGS.NONE,
                zeroOutMipMaps: true);

            for (var mip = 1; mip < pngMipLevels.Count; mip++)
            {
                using var mipImage = LoadPngFromMemory(pngMipLevels[mip], wicFlags);
                var source = mipImage.GetImage(0, 0, 0);
                var destination = imageWithMips.GetImage(mip, 0, 0);

                if (source.Width != destination.Width || source.Height != destination.Height)
                {
                    throw new InvalidOperationException(
                        $"Atlas mip {mip} is {source.Width}x{source.Height}, expected " +
                        $"{destination.Width}x{destination.Height}.");
                }

                TexHelper.Instance.CopyRectangle(
                    source,
                    0,
                    0,
                    source.Width,
                    source.Height,
                    destination,
                    TEX_FILTER_FLAGS.DEFAULT,
                    0,
                    0);
            }

            return CompressAndSave(imageWithMips, textureType, gameType, outFileName);
        }

        private static ScratchImage LoadPngFromMemory(byte[] pngBytes, WIC_FLAGS flags)
        {
            var handle = GCHandle.Alloc(pngBytes, GCHandleType.Pinned);
            try
            {
                return TexHelper.Instance.LoadFromWICMemory(
                    handle.AddrOfPinnedObject(),
                    pngBytes.LongLength,
                    flags);
            }
            finally
            {
                handle.Free();
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

            using var imageWithMips = processedImage.GenerateMipMaps(TEX_FILTER_FLAGS.DEFAULT, 0);
            return CompressAndSave(imageWithMips, textureType, gameType, outFileName);
        }

        private static PackFile CompressAndSave(
            ScratchImage imageWithMips,
            TextureType textureType,
            GameTypeEnum gameType,
            string outFileName)
        {
            var ddsFormat = DDSFormatHelper.GetDDSFormat(gameType, textureType);
            using var ddsImage = imageWithMips.Compress(ddsFormat, TEX_COMPRESS_FLAGS.DEFAULT, 0.5f);
            using var ddsMemStream = ddsImage.SaveToDDSMemory(DDS_FLAGS.NONE);

            var ddsBytes = new byte[ddsMemStream.Length];
            ddsMemStream.Read(ddsBytes, 0, ddsBytes.Length);
            return new PackFile(outFileName, new MemorySource(ddsBytes));
        }

        private static bool IsLinearTexture(TextureType textureType)
            => textureType is TextureType.Normal or TextureType.Mask or TextureType.Gloss;
    }
}
