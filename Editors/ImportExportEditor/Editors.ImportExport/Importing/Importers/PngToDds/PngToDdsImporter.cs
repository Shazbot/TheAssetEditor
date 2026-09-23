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

        public static PackFile ImportRawBgraMipChain(
            IReadOnlyList<byte[]> bgraMipLevels,
            int width,
            int height,
            TextureType textureType,
            GameTypeEnum gameType,
            string outFileName)
        {
            if (bgraMipLevels.Count == 0)
                throw new ArgumentException("At least one mip level is required.", nameof(bgraMipLevels));

            using var writer = CreateRawBgraMipChainWriter(
                width,
                height,
                bgraMipLevels.Count,
                textureType,
                gameType);
            for (var mip = 0; mip < bgraMipLevels.Count; mip++)
                writer.WriteMip(mip, bgraMipLevels[mip]);

            return writer.Complete(outFileName);
        }

        public static RawBgraMipChainWriter CreateRawBgraMipChainWriter(
            int width,
            int height,
            int mipLevelCount,
            TextureType textureType,
            GameTypeEnum gameType)
            => new(width, height, mipLevelCount, textureType, gameType);

        public sealed class RawBgraMipChainWriter : IDisposable
        {
            private readonly ScratchImage _imageWithMips;
            private readonly TextureType _textureType;
            private readonly GameTypeEnum _gameType;
            private bool _disposed;

            internal RawBgraMipChainWriter(
                int width,
                int height,
                int mipLevelCount,
                TextureType textureType,
                GameTypeEnum gameType)
            {
                if (width <= 0)
                    throw new ArgumentOutOfRangeException(nameof(width));
                if (height <= 0)
                    throw new ArgumentOutOfRangeException(nameof(height));
                if (mipLevelCount <= 0)
                    throw new ArgumentOutOfRangeException(nameof(mipLevelCount));

                var sourceFormat = IsLinearTexture(textureType)
                    ? DXGI_FORMAT.B8G8R8A8_UNORM
                    : DXGI_FORMAT.B8G8R8A8_UNORM_SRGB;

                _imageWithMips = TexHelper.Instance.Initialize2D(
                    sourceFormat,
                    width,
                    height,
                    1,
                    mipLevelCount,
                    CP_FLAGS.NONE);
                _textureType = textureType;
                _gameType = gameType;
            }

            public void WriteMip(int mipLevel, byte[] bgraPixels)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                var destination = _imageWithMips.GetImage(mipLevel, 0, 0);
                var rowBytes = checked(destination.Width * 4);
                var expectedLength = checked(rowBytes * destination.Height);
                if (bgraPixels.Length != expectedLength)
                {
                    throw new InvalidOperationException(
                        $"Atlas mip {mipLevel} has {bgraPixels.Length} BGRA bytes, expected {expectedLength} " +
                        $"for {destination.Width}x{destination.Height}.");
                }

                var destinationStride = checked((int)destination.RowPitch);
                for (var y = 0; y < destination.Height; y++)
                {
                    Marshal.Copy(
                        bgraPixels,
                        checked(y * rowBytes),
                        IntPtr.Add(destination.Pixels, checked(y * destinationStride)),
                        rowBytes);
                }
            }

            public PackFile Complete(string outFileName)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return CompressAndSave(
                    _imageWithMips,
                    _textureType,
                    _gameType,
                    outFileName);
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed = true;
                _imageWithMips.Dispose();
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
