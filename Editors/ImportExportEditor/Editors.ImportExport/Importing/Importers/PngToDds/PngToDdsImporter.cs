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
            private readonly int _width;
            private readonly int _height;
            private readonly int _mipLevelCount;
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
                _width = width;
                _height = height;
                _mipLevelCount = mipLevelCount;
            }

            public bool UsesLargeBcSplitCompression
            {
                get
                {
                    var ddsFormat = DDSFormatHelper.GetDDSFormat(_gameType, _textureType);
                    return ShouldUseLargeBcSplitCompression(
                        ddsFormat,
                        _width,
                        _height,
                        _mipLevelCount);
                }
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
                if (destinationStride == rowBytes)
                {
                    Marshal.Copy(bgraPixels, 0, destination.Pixels, bgraPixels.Length);
                    return;
                }

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
                    outFileName,
                    allowLargeBcSplitCompression: true,
                    width: _width,
                    height: _height,
                    mipLevelCount: _mipLevelCount);
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
            string outFileName,
            bool allowLargeBcSplitCompression = false,
            int width = 0,
            int height = 0,
            int mipLevelCount = 0)
        {
            var ddsFormat = DDSFormatHelper.GetDDSFormat(gameType, textureType);
            using var ddsImage =
                allowLargeBcSplitCompression &&
                ShouldUseLargeBcSplitCompression(ddsFormat, width, height, mipLevelCount)
                    ? CompressLargeBcMipChain(
                        imageWithMips,
                        ddsFormat,
                        width,
                        height,
                        mipLevelCount)
                    : imageWithMips.Compress(
                        ddsFormat,
                        TEX_COMPRESS_FLAGS.DEFAULT,
                        0.5f);
            using var ddsMemStream = ddsImage.SaveToDDSMemory(DDS_FLAGS.NONE);

            var ddsBytes = new byte[ddsMemStream.Length];
            ddsMemStream.Read(ddsBytes, 0, ddsBytes.Length);
            return new PackFile(outFileName, new MemorySource(ddsBytes));
        }

        private static bool ShouldUseLargeBcSplitCompression(
            DXGI_FORMAT format,
            int width,
            int height,
            int mipLevelCount)
        {
            if (Environment.ProcessorCount < 2 || mipLevelCount < 2)
                return false;

            var isBc1OrBc3 = format is
                DXGI_FORMAT.BC1_UNORM or
                DXGI_FORMAT.BC1_UNORM_SRGB or
                DXGI_FORMAT.BC3_UNORM or
                DXGI_FORMAT.BC3_UNORM_SRGB;
            if (!isBc1OrBc3)
                return false;

            const long minimumPixels = 4096L * 4096;
            return width >= 4 &&
                   height >= 12 &&
                   (long)width * height >= minimumPixels;
        }

        private static ScratchImage CompressLargeBcMipChain(
            ScratchImage sourceMipChain,
            DXGI_FORMAT ddsFormat,
            int width,
            int height,
            int mipLevelCount)
        {
            var sourceBase = sourceMipChain.GetImage(0, 0, 0);
            var sourceFormat = sourceBase.Format;
            var stripeBlockRows = SplitBlockRows(height / 4, 3);
            var stripes = new CompressedBcStripe?[stripeBlockRows.Length];
            CompressedBcMipTail? tail = null;

            var actions = new List<Action>(stripeBlockRows.Length + 1);
            var startBlockRow = 0;
            for (var stripeIndex = 0; stripeIndex < stripeBlockRows.Length; stripeIndex++)
            {
                var capturedIndex = stripeIndex;
                var capturedStartBlockRow = startBlockRow;
                var capturedBlockRows = stripeBlockRows[stripeIndex];
                actions.Add(() =>
                {
                    stripes[capturedIndex] = CompressBcStripe(
                        sourceBase,
                        sourceFormat,
                        ddsFormat,
                        width,
                        capturedStartBlockRow,
                        capturedBlockRows);
                });
                startBlockRow += capturedBlockRows;
            }

            actions.Add(() =>
            {
                tail = CompressBcMipTail(
                    sourceMipChain,
                    sourceFormat,
                    ddsFormat,
                    width,
                    height,
                    mipLevelCount);
            });

            Parallel.Invoke(
                new ParallelOptions { MaxDegreeOfParallelism = 2 },
                [.. actions]);

            var result = TexHelper.Instance.Initialize2D(
                ddsFormat,
                width,
                height,
                1,
                mipLevelCount,
                CP_FLAGS.NONE);

            try
            {
                var destinationBase = result.GetImage(0, 0, 0);
                var destinationStride = checked((int)destinationBase.RowPitch);

                foreach (var stripe in stripes)
                {
                    if (stripe == null)
                        throw new InvalidOperationException("BC atlas stripe compression did not complete.");
                    if (stripe.RowPitch != destinationStride)
                    {
                        throw new InvalidOperationException(
                            $"Compressed atlas stripe row pitch {stripe.RowPitch} does not match " +
                            $"destination row pitch {destinationStride}.");
                    }

                    var destinationOffset = checked(
                        stripe.StartBlockRow * destinationStride);
                    Marshal.Copy(
                        stripe.Bytes,
                        0,
                        IntPtr.Add(destinationBase.Pixels, destinationOffset),
                        stripe.Bytes.Length);
                }

                if (tail == null)
                    throw new InvalidOperationException("BC atlas mip-tail compression did not complete.");

                for (var tailMip = 0; tailMip < tail.Mips.Count; tailMip++)
                {
                    var destination = result.GetImage(tailMip + 1, 0, 0);
                    var compressedMip = tail.Mips[tailMip];
                    if (checked((int)destination.RowPitch) != compressedMip.RowPitch ||
                        checked((int)destination.SlicePitch) != compressedMip.Bytes.Length)
                    {
                        throw new InvalidOperationException(
                            $"Compressed atlas mip {tailMip + 1} layout does not match destination.");
                    }

                    Marshal.Copy(
                        compressedMip.Bytes,
                        0,
                        destination.Pixels,
                        compressedMip.Bytes.Length);
                }

                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private static CompressedBcStripe CompressBcStripe(
            DirectXTexNet.Image sourceBase,
            DXGI_FORMAT sourceFormat,
            DXGI_FORMAT ddsFormat,
            int width,
            int startBlockRow,
            int blockRows)
        {
            var stripeHeight = checked(blockRows * 4);
            using var stripe = TexHelper.Instance.Initialize2D(
                sourceFormat,
                width,
                stripeHeight,
                1,
                1,
                CP_FLAGS.NONE);
            var stripeImage = stripe.GetImage(0, 0, 0);

            TexHelper.Instance.CopyRectangle(
                sourceBase,
                0,
                checked(startBlockRow * 4),
                width,
                stripeHeight,
                stripeImage,
                TEX_FILTER_FLAGS.DEFAULT,
                0,
                0);

            using var compressed = stripe.Compress(
                ddsFormat,
                TEX_COMPRESS_FLAGS.DEFAULT,
                0.5f);
            var compressedImage = compressed.GetImage(0, 0, 0);
            var bytes = new byte[checked((int)compressedImage.SlicePitch)];
            Marshal.Copy(compressedImage.Pixels, bytes, 0, bytes.Length);

            return new CompressedBcStripe(
                startBlockRow,
                checked((int)compressedImage.RowPitch),
                bytes);
        }

        private static CompressedBcMipTail CompressBcMipTail(
            ScratchImage sourceMipChain,
            DXGI_FORMAT sourceFormat,
            DXGI_FORMAT ddsFormat,
            int width,
            int height,
            int mipLevelCount)
        {
            var tailMipCount = mipLevelCount - 1;
            using var tail = TexHelper.Instance.Initialize2D(
                sourceFormat,
                Math.Max(1, width / 2),
                Math.Max(1, height / 2),
                1,
                tailMipCount,
                CP_FLAGS.NONE);

            for (var tailMip = 0; tailMip < tailMipCount; tailMip++)
            {
                var source = sourceMipChain.GetImage(tailMip + 1, 0, 0);
                var destination = tail.GetImage(tailMip, 0, 0);
                if (source.Width != destination.Width || source.Height != destination.Height)
                {
                    throw new InvalidOperationException(
                        $"Atlas mip-tail source {tailMip + 1} is {source.Width}x{source.Height}, " +
                        $"expected {destination.Width}x{destination.Height}.");
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

            using var compressed = tail.Compress(
                ddsFormat,
                TEX_COMPRESS_FLAGS.DEFAULT,
                0.5f);
            var mips = new List<CompressedBcMip>(tailMipCount);
            for (var tailMip = 0; tailMip < tailMipCount; tailMip++)
            {
                var image = compressed.GetImage(tailMip, 0, 0);
                var bytes = new byte[checked((int)image.SlicePitch)];
                Marshal.Copy(image.Pixels, bytes, 0, bytes.Length);
                mips.Add(new CompressedBcMip(
                    checked((int)image.RowPitch),
                    bytes));
            }

            return new CompressedBcMipTail(mips);
        }

        private static int[] SplitBlockRows(int totalBlockRows, int parts)
        {
            if (totalBlockRows < parts)
                throw new ArgumentOutOfRangeException(nameof(totalBlockRows));

            var rows = new int[parts];
            var baseRows = totalBlockRows / parts;
            var remainder = totalBlockRows % parts;
            for (var i = 0; i < parts; i++)
                rows[i] = baseRows + (i < remainder ? 1 : 0);
            return rows;
        }

        private sealed record CompressedBcStripe(
            int StartBlockRow,
            int RowPitch,
            byte[] Bytes);

        private sealed record CompressedBcMip(
            int RowPitch,
            byte[] Bytes);

        private sealed record CompressedBcMipTail(
            List<CompressedBcMip> Mips);

        private static bool IsLinearTexture(TextureType textureType)
            => textureType is TextureType.Normal or TextureType.Mask or TextureType.Gloss;
    }
}
