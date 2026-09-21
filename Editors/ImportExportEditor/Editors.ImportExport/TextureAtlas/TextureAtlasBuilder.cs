using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using MeshImportExport;

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
            atlas.Save(stream, ImageFormat.Png);
            return stream.ToArray();
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
            var pngBytes = TextureHelper.ConvertDdsToPng(ddsBytes);
            using var stream = new MemoryStream(pngBytes);
            using var loaded = new Bitmap(stream);

            var copy = new Bitmap(loaded.Width, loaded.Height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(copy);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(loaded, 0, 0);
            return copy;
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
