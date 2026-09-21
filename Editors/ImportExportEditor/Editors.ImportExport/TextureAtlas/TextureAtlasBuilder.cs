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

                var cropX = Math.Clamp((int)MathF.Floor(source.MinU * source.SourceWidth), 0, source.SourceWidth - 1);
                var cropY = Math.Clamp((int)MathF.Floor(source.MinV * source.SourceHeight), 0, source.SourceHeight - 1);
                var cropRight = Math.Clamp((int)MathF.Ceiling(source.MaxU * source.SourceWidth), cropX + 1, source.SourceWidth);
                var cropBottom = Math.Clamp((int)MathF.Ceiling(source.MaxV * source.SourceHeight), cropY + 1, source.SourceHeight);
                var cropWidth = cropRight - cropX;
                var cropHeight = cropBottom - cropY;

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
            IReadOnlySet<int>? forceOpaqueAlphaSourceIds = null)
        {
            using var atlas = new Bitmap(plan.Width, plan.Height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(atlas);
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;

            foreach (var placement in plan.Placements)
            {
                if (!ddsSources.TryGetValue(placement.Id, out var ddsBytes))
                    throw new InvalidOperationException($"Missing texture data for atlas source {placement.Id}.");

                using var source = LoadBitmap(ddsBytes);
                if (forceOpaqueAlphaSourceIds?.Contains(placement.Id) == true)
                    ForceOpaqueAlpha(source);

                if (source.Width != placement.SourceWidth || source.Height != placement.SourceHeight)
                {
                    throw new InvalidOperationException(
                        $"Texture dimensions for atlas source {placement.Id} are {source.Width}x{source.Height}, " +
                        $"but the shared atlas layout requires {placement.SourceWidth}x{placement.SourceHeight}. " +
                        "The first atlas implementation intentionally avoids resampling so tangent-space normal maps remain safe.");
                }

                CopyRegionAndPadding(graphics, source, placement);
            }

            using var stream = new MemoryStream();
            atlas.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }

        public static (int Width, int Height) GetDimensions(byte[] ddsBytes)
        {
            using var bitmap = LoadBitmap(ddsBytes);
            return (bitmap.Width, bitmap.Height);
        }

        private static void CopyRegionAndPadding(Graphics graphics, Bitmap source, TextureAtlasPlacement placement)
        {
            var sourceRect = new Rectangle(placement.CropX, placement.CropY, placement.CropWidth, placement.CropHeight);
            var destinationRect = new Rectangle(placement.DestinationX, placement.DestinationY, placement.CropWidth, placement.CropHeight);
            graphics.DrawImage(source, destinationRect, sourceRect, GraphicsUnit.Pixel);

            var padding = placement.Padding;
            if (padding == 0)
                return;

            var left = placement.DestinationX;
            var top = placement.DestinationY;
            var right = left + placement.CropWidth;
            var bottom = top + placement.CropHeight;
            var srcLeft = placement.CropX;
            var srcTop = placement.CropY;
            var srcRight = placement.CropX + placement.CropWidth - 1;
            var srcBottom = placement.CropY + placement.CropHeight - 1;

            graphics.DrawImage(
                source,
                new Rectangle(left, top - padding, placement.CropWidth, padding),
                new Rectangle(srcLeft, srcTop, placement.CropWidth, 1),
                GraphicsUnit.Pixel);
            graphics.DrawImage(
                source,
                new Rectangle(left, bottom, placement.CropWidth, padding),
                new Rectangle(srcLeft, srcBottom, placement.CropWidth, 1),
                GraphicsUnit.Pixel);
            graphics.DrawImage(
                source,
                new Rectangle(left - padding, top, padding, placement.CropHeight),
                new Rectangle(srcLeft, srcTop, 1, placement.CropHeight),
                GraphicsUnit.Pixel);
            graphics.DrawImage(
                source,
                new Rectangle(right, top, padding, placement.CropHeight),
                new Rectangle(srcRight, srcTop, 1, placement.CropHeight),
                GraphicsUnit.Pixel);

            DrawCorner(graphics, source, left - padding, top - padding, padding, srcLeft, srcTop);
            DrawCorner(graphics, source, right, top - padding, padding, srcRight, srcTop);
            DrawCorner(graphics, source, left - padding, bottom, padding, srcLeft, srcBottom);
            DrawCorner(graphics, source, right, bottom, padding, srcRight, srcBottom);
        }

        private static void DrawCorner(Graphics graphics, Bitmap source, int x, int y, int padding, int sourceX, int sourceY)
        {
            graphics.DrawImage(
                source,
                new Rectangle(x, y, padding, padding),
                new Rectangle(sourceX, sourceY, 1, 1),
                GraphicsUnit.Pixel);
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

        private static void ForceOpaqueAlpha(Bitmap bitmap)
        {
            var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var bitmapData = bitmap.LockBits(rectangle, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                var rowSize = Math.Abs(bitmapData.Stride);
                var rowBytes = new byte[rowSize];

                for (var y = 0; y < bitmap.Height; y++)
                {
                    var rowPointer = IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride);
                    Marshal.Copy(rowPointer, rowBytes, 0, rowBytes.Length);

                    for (var x = 0; x < bitmap.Width; x++)
                        rowBytes[x * 4 + 3] = byte.MaxValue;

                    Marshal.Copy(rowBytes, 0, rowPointer, rowBytes.Length);
                }
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }
        }

        private static void ValidateSource(TextureAtlasLayoutSource source)
        {
            if (source.SourceWidth <= 0 || source.SourceHeight <= 0)
                throw new ArgumentException($"Atlas source {source.Id} has invalid texture dimensions.");

            const float epsilon = 0.00001f;
            if (source.MinU < -epsilon || source.MinV < -epsilon || source.MaxU > 1 + epsilon || source.MaxV > 1 + epsilon)
            {
                throw new InvalidOperationException(
                    $"Atlas source {source.Id} uses UV coordinates outside 0..1. " +
                    "Wrapped/tiled UVs are intentionally not supported by the safe atlas path.");
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
