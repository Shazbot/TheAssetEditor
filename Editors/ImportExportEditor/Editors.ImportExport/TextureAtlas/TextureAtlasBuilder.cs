using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Pfim;
using PfimImageFormat = Pfim.ImageFormat;

namespace Editors.ImportExport.TextureAtlas
{
    public sealed record TextureAtlasLayoutSource(
        int Id,
        int SourceWidth,
        int SourceHeight,
        float MinU,
        float MinV,
        float MaxU,
        float MaxV);

    public sealed record TextureAtlasSource(
        int Id,
        byte[] DdsBytes,
        float MinU,
        float MinV,
        float MaxU,
        float MaxV);

    public sealed record TextureAtlasPlacement(
        int Id,
        int SourceWidth,
        int SourceHeight,
        int CropX,
        int CropY,
        int CropWidth,
        int CropHeight,
        int DestinationX,
        int DestinationY,
        int Padding)
    {
        public (float U, float V) TransformUv(float u, float v, int atlasWidth, int atlasHeight)
        {
            var sourceX = u * SourceWidth;
            var sourceY = v * SourceHeight;
            var atlasX = DestinationX + sourceX - CropX;
            var atlasY = DestinationY + sourceY - CropY;
            return (atlasX / atlasWidth, atlasY / atlasHeight);
        }
    }

    public sealed record TextureAtlasPlan(
        int Width,
        int Height,
        IReadOnlyList<TextureAtlasPlacement> Placements);

    public static class TextureAtlasBuilder
    {
        public const int DefaultPadding = 8;
        public const int DefaultMaxAtlasSize = 8192;

        public static TextureAtlasPlan CreatePlanFromDds(
            IReadOnlyList<TextureAtlasSource> sources,
            int padding = DefaultPadding,
            int maxAtlasSize = DefaultMaxAtlasSize)
        {
            var layoutSources = new List<TextureAtlasLayoutSource>(sources.Count);
            foreach (var source in sources)
            {
                var (width, height) = GetDimensions(source.DdsBytes);
                layoutSources.Add(new TextureAtlasLayoutSource(
                    source.Id,
                    width,
                    height,
                    source.MinU,
                    source.MinV,
                    source.MaxU,
                    source.MaxV));
            }

            return CreatePlan(layoutSources, padding, maxAtlasSize);
        }

        public static TextureAtlasPlan CreatePlan(
            IReadOnlyList<TextureAtlasLayoutSource> sources,
            int padding = DefaultPadding,
            int maxAtlasSize = DefaultMaxAtlasSize)
        {
            if (sources.Count == 0)
                throw new ArgumentException("At least one atlas source is required.", nameof(sources));
            if (padding < 0)
                throw new ArgumentOutOfRangeException(nameof(padding));
            if (maxAtlasSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxAtlasSize));

            var pending = new List<PendingPlacement>(sources.Count);
            foreach (var source in sources)
            {
                ValidateSource(source);

                // Keep the crop in virtual source-pixel space instead of clamping it to the
                // physical texture. UVs outside 0..1 are valid when the material sampler wraps:
                // the atlas copies the required repeated tiles and then remaps the original UVs.
                var cropX = checked((int)MathF.Floor(source.MinU * source.SourceWidth));
                var cropY = checked((int)MathF.Floor(source.MinV * source.SourceHeight));
                var cropRight = checked((int)MathF.Ceiling(source.MaxU * source.SourceWidth));
                var cropBottom = checked((int)MathF.Ceiling(source.MaxV * source.SourceHeight));
                var cropWidth = checked(cropRight - cropX);
                var cropHeight = checked(cropBottom - cropY);

                if (cropWidth <= 0)
                    cropWidth = 1;
                if (cropHeight <= 0)
                    cropHeight = 1;

                pending.Add(new PendingPlacement(
                    source.Id,
                    source.SourceWidth,
                    source.SourceHeight,
                    cropX,
                    cropY,
                    cropWidth,
                    cropHeight,
                    cropWidth + padding * 2,
                    cropHeight + padding * 2));
            }

            pending.Sort((a, b) =>
            {
                var heightCompare = b.PaddedHeight.CompareTo(a.PaddedHeight);
                return heightCompare != 0 ? heightCompare : b.PaddedWidth.CompareTo(a.PaddedWidth);
            });

            var largestDimension = pending.Max(x => Math.Max(x.PaddedWidth, x.PaddedHeight));
            var totalArea = pending.Sum(x => (long)x.PaddedWidth * x.PaddedHeight);
            var minimumSize = Math.Max(largestDimension, (int)Math.Ceiling(Math.Sqrt(totalArea)));
            var atlasSize = NextPowerOfTwo(Math.Max(1, minimumSize));

            while (atlasSize <= maxAtlasSize)
            {
                if (TryPack(pending, atlasSize, padding, out var placements))
                    return new TextureAtlasPlan(atlasSize, atlasSize, placements);

                if (atlasSize == maxAtlasSize)
                    break;

                atlasSize = Math.Min(atlasSize * 2, maxAtlasSize);
            }

            throw new InvalidOperationException($"Selected UV regions do not fit inside a {maxAtlasSize}x{maxAtlasSize} texture atlas.");
        }

        public static byte[] BuildPng(
            TextureAtlasPlan plan,
            IReadOnlyDictionary<int, byte[]> ddsSources,
            IReadOnlySet<int>? forceOpaqueAlphaSourceIds = null,
            IReadOnlySet<int>? omittedSourceIds = null)
        {
            var atlasPixels = new byte[checked(plan.Width * plan.Height * 4)];

            foreach (var placement in plan.Placements)
            {
                if (omittedSourceIds?.Contains(placement.Id) == true)
                    continue;

                if (!ddsSources.TryGetValue(placement.Id, out var ddsBytes))
                    throw new InvalidOperationException($"Missing texture data for atlas source {placement.Id}.");

                using var source = LoadBitmap(ddsBytes);
                if (source.Width != placement.SourceWidth || source.Height != placement.SourceHeight)
                {
                    throw new InvalidOperationException(
                        $"Texture dimensions for atlas source {placement.Id} are {source.Width}x{source.Height}, " +
                        $"but the shared atlas layout requires {placement.SourceWidth}x{placement.SourceHeight}. " +
                        "The atlas implementation intentionally avoids resampling so tangent-space normal maps remain safe.");
                }

                var sourcePixels = ReadPixels(source);
                CopyWrappedRegionAndPadding(
                    atlasPixels,
                    plan.Width,
                    plan.Height,
                    sourcePixels,
                    source.Width,
                    source.Height,
                    placement,
                    forceOpaqueAlphaSourceIds?.Contains(placement.Id) == true);
            }

            using var atlas = new Bitmap(plan.Width, plan.Height, PixelFormat.Format32bppArgb);
            WritePixels(atlas, atlasPixels);

            using var stream = new MemoryStream();
            atlas.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }

        public static IReadOnlyList<byte[]> BuildMipPngs(
            TextureAtlasPlan plan,
            IReadOnlyDictionary<int, byte[]> ddsSources,
            IReadOnlySet<int>? forceOpaqueAlphaSourceIds = null,
            IReadOnlySet<int>? omittedSourceIds = null)
        {
            var decodedSources = new Dictionary<int, IImage>();
            try
            {
                foreach (var (id, ddsBytes) in ddsSources)
                {
                    using var stream = new MemoryStream(ddsBytes);
                    var image = Pfimage.FromStream(stream);
                    if (image.Format != PfimImageFormat.Rgba32)
                    {
                        image.Dispose();
                        throw new NotSupportedException(
                            $"Unsupported DDS pixel format for texture atlas generation: {image.Format}. Expected RGBA32.");
                    }

                    decodedSources[id] = image;
                }

                var mipPngs = new List<byte[]>();
                var mipWidth = plan.Width;
                var mipHeight = plan.Height;
                var mipLevel = 0;

                while (true)
                {
                    var atlasPixels = new byte[checked(mipWidth * mipHeight * 4)];
                    var coreOccupancy = new bool[checked(mipWidth * mipHeight)];

                    // Write core regions first so padding can never overwrite another source's
                    // real texels when placements converge at lower mip levels.
                    foreach (var placement in plan.Placements)
                    {
                        if (omittedSourceIds?.Contains(placement.Id) == true)
                            continue;
                        if (!decodedSources.TryGetValue(placement.Id, out var source))
                            throw new InvalidOperationException($"Missing texture data for atlas source {placement.Id}.");

                        var sourceMip = GetMipLevel(source, mipLevel);
                        var bounds = GetMipPlacementBounds(plan, placement, mipWidth, mipHeight);

                        for (var y = bounds.Top; y < bounds.Bottom; y++)
                        {
                            for (var x = bounds.Left; x < bounds.Right; x++)
                            {
                                var index = y * mipWidth + x;
                                if (coreOccupancy[index])
                                    continue;

                                CopySourceMipPixel(
                                    atlasPixels,
                                    mipWidth,
                                    x,
                                    y,
                                    source,
                                    sourceMip,
                                    plan,
                                    placement,
                                    mipWidth,
                                    mipHeight,
                                    clampToCrop: false,
                                    forceOpaqueAlphaSourceIds?.Contains(placement.Id) == true);
                                coreOccupancy[index] = true;
                            }
                        }
                    }

                    // Rebuild an edge extrusion independently at every mip level. This avoids
                    // the classic atlas problem where a small base-level gutter disappears as
                    // the whole atlas is downsampled and neighboring/transparent regions bleed
                    // into the material.
                    foreach (var placement in plan.Placements)
                    {
                        if (omittedSourceIds?.Contains(placement.Id) == true)
                            continue;
                        if (!decodedSources.TryGetValue(placement.Id, out var source))
                            throw new InvalidOperationException($"Missing texture data for atlas source {placement.Id}.");

                        var sourceMip = GetMipLevel(source, mipLevel);
                        var bounds = GetMipPlacementBounds(plan, placement, mipWidth, mipHeight);
                        var paddingX = Math.Max(1, (int)Math.Ceiling((double)placement.Padding * mipWidth / plan.Width));
                        var paddingY = Math.Max(1, (int)Math.Ceiling((double)placement.Padding * mipHeight / plan.Height));

                        var left = Math.Max(0, bounds.Left - paddingX);
                        var top = Math.Max(0, bounds.Top - paddingY);
                        var right = Math.Min(mipWidth, bounds.Right + paddingX);
                        var bottom = Math.Min(mipHeight, bounds.Bottom + paddingY);

                        for (var y = top; y < bottom; y++)
                        {
                            for (var x = left; x < right; x++)
                            {
                                var index = y * mipWidth + x;
                                if (coreOccupancy[index])
                                    continue;

                                CopySourceMipPixel(
                                    atlasPixels,
                                    mipWidth,
                                    x,
                                    y,
                                    source,
                                    sourceMip,
                                    plan,
                                    placement,
                                    mipWidth,
                                    mipHeight,
                                    clampToCrop: true,
                                    forceOpaqueAlphaSourceIds?.Contains(placement.Id) == true);
                            }
                        }
                    }

                    using var bitmap = new Bitmap(mipWidth, mipHeight, PixelFormat.Format32bppArgb);
                    WritePixels(bitmap, atlasPixels);
                    using var pngStream = new MemoryStream();
                    bitmap.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
                    mipPngs.Add(pngStream.ToArray());

                    if (mipWidth == 1 && mipHeight == 1)
                        break;

                    mipWidth = Math.Max(1, mipWidth / 2);
                    mipHeight = Math.Max(1, mipHeight / 2);
                    mipLevel++;
                }

                return mipPngs;
            }
            finally
            {
                foreach (var source in decodedSources.Values)
                    source.Dispose();
            }
        }

        private static MipLevelInfo GetMipLevel(IImage source, int requestedLevel)
        {
            if (requestedLevel <= 0 || source.MipMaps.Length == 0)
                return new MipLevelInfo(source.Width, source.Height, source.Stride, 0);

            var mipIndex = Math.Min(requestedLevel - 1, source.MipMaps.Length - 1);
            var mip = source.MipMaps[mipIndex];
            return new MipLevelInfo(mip.Width, mip.Height, mip.Stride, mip.DataOffset);
        }

        private static MipPlacementBounds GetMipPlacementBounds(
            TextureAtlasPlan plan,
            TextureAtlasPlacement placement,
            int mipWidth,
            int mipHeight)
        {
            var left = Math.Clamp(
                (int)Math.Floor((double)placement.DestinationX * mipWidth / plan.Width),
                0,
                mipWidth - 1);
            var top = Math.Clamp(
                (int)Math.Floor((double)placement.DestinationY * mipHeight / plan.Height),
                0,
                mipHeight - 1);
            var right = Math.Clamp(
                (int)Math.Ceiling((double)(placement.DestinationX + placement.CropWidth) * mipWidth / plan.Width),
                left + 1,
                mipWidth);
            var bottom = Math.Clamp(
                (int)Math.Ceiling((double)(placement.DestinationY + placement.CropHeight) * mipHeight / plan.Height),
                top + 1,
                mipHeight);

            return new MipPlacementBounds(left, top, right, bottom);
        }

        private static void CopySourceMipPixel(
            byte[] atlasPixels,
            int atlasRowWidth,
            int atlasX,
            int atlasY,
            IImage source,
            MipLevelInfo sourceMip,
            TextureAtlasPlan plan,
            TextureAtlasPlacement placement,
            int mipWidth,
            int mipHeight,
            bool clampToCrop,
            bool forceOpaqueAlpha)
        {
            // Map the destination mip pixel center back through the exact base-level atlas UV
            // transform. The source mip level then supplies the authored texel for the same LOD.
            var atlasBaseX = (atlasX + 0.5) * plan.Width / mipWidth;
            var atlasBaseY = (atlasY + 0.5) * plan.Height / mipHeight;

            if (clampToCrop)
            {
                atlasBaseX = Math.Clamp(
                    atlasBaseX,
                    placement.DestinationX + 0.5,
                    placement.DestinationX + placement.CropWidth - 0.5);
                atlasBaseY = Math.Clamp(
                    atlasBaseY,
                    placement.DestinationY + 0.5,
                    placement.DestinationY + placement.CropHeight - 0.5);
            }

            var sourceBaseX = placement.CropX + (atlasBaseX - placement.DestinationX);
            var sourceBaseY = placement.CropY + (atlasBaseY - placement.DestinationY);

            var sourceX = PositiveModulo(
                (int)Math.Floor(sourceBaseX * sourceMip.Width / placement.SourceWidth),
                sourceMip.Width);
            var sourceY = PositiveModulo(
                (int)Math.Floor(sourceBaseY * sourceMip.Height / placement.SourceHeight),
                sourceMip.Height);

            var sourceOffset = sourceMip.DataOffset + sourceY * sourceMip.Stride + sourceX * 4;
            var destinationOffset = (atlasY * atlasRowWidth + atlasX) * 4;

            atlasPixels[destinationOffset] = source.Data[sourceOffset];
            atlasPixels[destinationOffset + 1] = source.Data[sourceOffset + 1];
            atlasPixels[destinationOffset + 2] = source.Data[sourceOffset + 2];
            atlasPixels[destinationOffset + 3] = forceOpaqueAlpha
                ? byte.MaxValue
                : source.Data[sourceOffset + 3];
        }

        public static (int Width, int Height) GetDimensions(byte[] ddsBytes)
        {
            using var bitmap = LoadBitmap(ddsBytes);
            return (bitmap.Width, bitmap.Height);
        }

        private static void CopyWrappedRegionAndPadding(
            byte[] atlasPixels,
            int atlasWidth,
            int atlasHeight,
            byte[] sourcePixels,
            int sourceWidth,
            int sourceHeight,
            TextureAtlasPlacement placement,
            bool forceOpaqueAlpha)
        {
            var padding = placement.Padding;

            for (var localY = -padding; localY < placement.CropHeight + padding; localY++)
            {
                var destinationY = placement.DestinationY + localY;
                if (destinationY < 0 || destinationY >= atlasHeight)
                    throw new InvalidOperationException($"Atlas placement {placement.Id} exceeds atlas bounds.");

                // Padding is an edge extrusion, not another wrapped tile. Clamp to the virtual
                // crop edge first, then wrap that virtual source coordinate into the DDS.
                var cropLocalY = Math.Clamp(localY, 0, placement.CropHeight - 1);
                var virtualSourceY = checked(placement.CropY + cropLocalY);
                var sourceY = PositiveModulo(virtualSourceY, sourceHeight);

                for (var localX = -padding; localX < placement.CropWidth + padding; localX++)
                {
                    var destinationX = placement.DestinationX + localX;
                    if (destinationX < 0 || destinationX >= atlasWidth)
                        throw new InvalidOperationException($"Atlas placement {placement.Id} exceeds atlas bounds.");

                    var cropLocalX = Math.Clamp(localX, 0, placement.CropWidth - 1);
                    var virtualSourceX = checked(placement.CropX + cropLocalX);
                    var sourceX = PositiveModulo(virtualSourceX, sourceWidth);

                    var sourceOffset = (sourceY * sourceWidth + sourceX) * 4;
                    var destinationOffset = (destinationY * atlasWidth + destinationX) * 4;

                    atlasPixels[destinationOffset] = sourcePixels[sourceOffset];
                    atlasPixels[destinationOffset + 1] = sourcePixels[sourceOffset + 1];
                    atlasPixels[destinationOffset + 2] = sourcePixels[sourceOffset + 2];
                    atlasPixels[destinationOffset + 3] = forceOpaqueAlpha
                        ? byte.MaxValue
                        : sourcePixels[sourceOffset + 3];
                }
            }
        }

        private static byte[] ReadPixels(Bitmap bitmap)
        {
            var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var bitmapData = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var pixels = new byte[checked(bitmap.Width * bitmap.Height * 4)];
                var rowBytes = bitmap.Width * 4;

                for (var y = 0; y < bitmap.Height; y++)
                {
                    var rowPointer = IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride);
                    Marshal.Copy(rowPointer, pixels, y * rowBytes, rowBytes);
                }

                return pixels;
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }
        }

        private static void WritePixels(Bitmap bitmap, byte[] pixels)
        {
            var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var bitmapData = bitmap.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var expectedLength = checked(bitmap.Width * bitmap.Height * 4);
                if (pixels.Length != expectedLength)
                    throw new ArgumentException($"Expected {expectedLength} pixel bytes, got {pixels.Length}.", nameof(pixels));

                var rowBytes = bitmap.Width * 4;
                for (var y = 0; y < bitmap.Height; y++)
                {
                    var rowPointer = IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride);
                    Marshal.Copy(pixels, y * rowBytes, rowPointer, rowBytes);
                }
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }
        }

        private static int PositiveModulo(int value, int modulus)
        {
            var result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        private static Bitmap LoadBitmap(byte[] ddsBytes)
        {
            using var stream = new MemoryStream(ddsBytes);
            using var image = Pfimage.FromStream(stream);

            if (image.Format != PfimImageFormat.Rgba32)
            {
                throw new NotSupportedException(
                    $"Unsupported DDS pixel format for texture atlas generation: {image.Format}. Expected RGBA32.");
            }

            // Pfim's Rgba32 buffer is already laid out exactly as GDI+'s
            // Format32bppArgb expects in memory. Do not swap red/blue here.
            var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
            var rectangle = new Rectangle(0, 0, image.Width, image.Height);
            var bitmapData = bitmap.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            try
            {
                var rowBytes = checked(image.Width * 4);
                for (var y = 0; y < image.Height; y++)
                {
                    var rowPointer = IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride);
                    Marshal.Copy(image.Data, y * image.Stride, rowPointer, rowBytes);
                }
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }

            return bitmap;
        }

        private static void ValidateSource(TextureAtlasLayoutSource source)
        {
            if (source.SourceWidth <= 0 || source.SourceHeight <= 0)
                throw new ArgumentException($"Atlas source {source.Id} has invalid texture dimensions.");

            if (!float.IsFinite(source.MinU) ||
                !float.IsFinite(source.MinV) ||
                !float.IsFinite(source.MaxU) ||
                !float.IsFinite(source.MaxV))
            {
                throw new ArgumentException($"Atlas source {source.Id} has non-finite UV coordinates.");
            }

            if (source.MaxU < source.MinU || source.MaxV < source.MinV)
                throw new ArgumentException($"Atlas source {source.Id} has invalid UV bounds.");
        }

        private static bool TryPack(
            IReadOnlyList<PendingPlacement> pending,
            int atlasSize,
            int padding,
            out IReadOnlyList<TextureAtlasPlacement> placements)
        {
            var output = new List<TextureAtlasPlacement>(pending.Count);
            var x = 0;
            var y = 0;
            var shelfHeight = 0;

            foreach (var item in pending)
            {
                if (item.PaddedWidth > atlasSize || item.PaddedHeight > atlasSize)
                {
                    placements = [];
                    return false;
                }

                if (x + item.PaddedWidth > atlasSize)
                {
                    y += shelfHeight;
                    x = 0;
                    shelfHeight = 0;
                }

                if (y + item.PaddedHeight > atlasSize)
                {
                    placements = [];
                    return false;
                }

                output.Add(new TextureAtlasPlacement(
                    item.Id,
                    item.SourceWidth,
                    item.SourceHeight,
                    item.CropX,
                    item.CropY,
                    item.CropWidth,
                    item.CropHeight,
                    x + padding,
                    y + padding,
                    padding));

                x += item.PaddedWidth;
                shelfHeight = Math.Max(shelfHeight, item.PaddedHeight);
            }

            placements = output;
            return true;
        }

        private static int NextPowerOfTwo(int value)
        {
            var result = 1;
            while (result < value)
                result <<= 1;
            return result;
        }

        private sealed record MipLevelInfo(int Width, int Height, int Stride, int DataOffset);

        private sealed record MipPlacementBounds(int Left, int Top, int Right, int Bottom);

        private sealed record PendingPlacement(
            int Id,
            int SourceWidth,
            int SourceHeight,
            int CropX,
            int CropY,
            int CropWidth,
            int CropHeight,
            int PaddedWidth,
            int PaddedHeight);
    }
}
