using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
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

    public readonly record struct TextureAtlasConstantColor(
        byte B,
        byte G,
        byte R,
        byte A);

    public sealed class TextureAtlasBuildStatistics
    {
        public int DecodedSourceCount { get; internal set; }
        public int UniqueDecodedDdsCount { get; internal set; }
        public TimeSpan DdsDecodeElapsed { get; internal set; }
        public TimeSpan ComposeElapsed { get; internal set; }
        public int MipLevelsBuilt { get; internal set; }
        public int RowCopyAttempts { get; internal set; }
        public int RowCopyPlacements { get; internal set; }
        public int RowCopyFallbacks { get; internal set; }
        public long RowCopyPixels { get; internal set; }
        public long MappedCorePixels { get; internal set; }
        public long ConstantCorePixels { get; internal set; }
        public long PaddingPixels { get; internal set; }
    }

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

            var largestWidth = pending.Max(x => x.PaddedWidth);
            var largestHeight = pending.Max(x => x.PaddedHeight);
            if (largestWidth > maxAtlasSize || largestHeight > maxAtlasSize)
            {
                throw new InvalidOperationException(
                    $"Selected UV regions do not fit inside a {maxAtlasSize}x{maxAtlasSize} texture atlas.");
            }

            var totalArea = pending.Sum(x => (long)x.PaddedWidth * x.PaddedHeight);
            var minimumWidth = NextPowerOfTwo(Math.Max(1, largestWidth));
            var minimumHeight = NextPowerOfTwo(Math.Max(1, largestHeight));
            var candidateSizes = new List<(int Width, int Height)>();

            for (var width = minimumWidth; width <= maxAtlasSize; width *= 2)
            {
                for (var height = minimumHeight; height <= maxAtlasSize; height *= 2)
                {
                    if ((long)width * height < totalArea)
                        continue;

                    candidateSizes.Add((width, height));
                }
            }

            foreach (var candidate in candidateSizes
                         .OrderBy(x => (long)x.Width * x.Height)
                         .ThenBy(x => Math.Max(x.Width, x.Height))
                         .ThenBy(x => x.Width)
                         .ThenBy(x => x.Height))
            {
                if (TryPack(
                        pending,
                        candidate.Width,
                        candidate.Height,
                        padding,
                        out var placements))
                {
                    return new TextureAtlasPlan(
                        candidate.Width,
                        candidate.Height,
                        placements);
                }
            }

            throw new InvalidOperationException($"Selected UV regions do not fit inside a {maxAtlasSize}x{maxAtlasSize} texture atlas.");
        }

        public static byte[] BuildPng(
            TextureAtlasPlan plan,
            IReadOnlyDictionary<int, byte[]> ddsSources,
            IReadOnlySet<int>? forceOpaqueAlphaSourceIds = null,
            IReadOnlySet<int>? omittedSourceIds = null,
            IReadOnlyDictionary<int, TextureAtlasConstantColor>? constantSources = null)
        {
            var atlasPixels = new byte[checked(plan.Width * plan.Height * 4)];

            foreach (var placement in plan.Placements)
            {
                if (omittedSourceIds?.Contains(placement.Id) == true)
                    continue;

                if (constantSources?.TryGetValue(placement.Id, out var constantColor) == true)
                {
                    CopyConstantRegionAndPadding(
                        atlasPixels,
                        plan.Width,
                        plan.Height,
                        placement,
                        constantColor,
                        forceOpaqueAlphaSourceIds?.Contains(placement.Id) == true);
                    continue;
                }

                if (!ddsSources.TryGetValue(placement.Id, out var ddsBytes))
                    throw new InvalidOperationException($"Missing texture data for atlas source {placement.Id}.");

                using var source = LoadBitmap(ddsBytes);
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
            IReadOnlySet<int>? omittedSourceIds = null,
            CancellationToken cancellationToken = default,
            Action? heartbeat = null,
            IReadOnlyDictionary<int, TextureAtlasConstantColor>? constantSources = null,
            int? outputWidth = null,
            int? outputHeight = null)
        {
            var atlasWidth = outputWidth ?? plan.Width;
            var atlasHeight = outputHeight ?? plan.Height;
            var mipPixels = BuildMipPixels(
                plan,
                ddsSources,
                forceOpaqueAlphaSourceIds,
                omittedSourceIds,
                cancellationToken,
                heartbeat,
                constantSources,
                outputWidth,
                outputHeight);

            var result = new List<byte[]>(mipPixels.Count);
            var mipWidth = atlasWidth;
            var mipHeight = atlasHeight;
            foreach (var pixels in mipPixels)
            {
                using var bitmap = new Bitmap(mipWidth, mipHeight, PixelFormat.Format32bppArgb);
                WritePixels(bitmap, pixels);
                using var pngStream = new MemoryStream();
                bitmap.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
                result.Add(pngStream.ToArray());

                mipWidth = Math.Max(1, mipWidth / 2);
                mipHeight = Math.Max(1, mipHeight / 2);
            }

            return result;
        }

        public static IReadOnlyList<byte[]> BuildMipPixels(
            TextureAtlasPlan plan,
            IReadOnlyDictionary<int, byte[]> ddsSources,
            IReadOnlySet<int>? forceOpaqueAlphaSourceIds = null,
            IReadOnlySet<int>? omittedSourceIds = null,
            CancellationToken cancellationToken = default,
            Action? heartbeat = null,
            IReadOnlyDictionary<int, TextureAtlasConstantColor>? constantSources = null,
            int? outputWidth = null,
            int? outputHeight = null,
            Action<int, int, int, byte[]>? mipConsumer = null,
            bool retainMipPixels = true,
            TextureAtlasBuildStatistics? statistics = null)
        {
            var atlasWidth = outputWidth ?? plan.Width;
            var atlasHeight = outputHeight ?? plan.Height;
            if (atlasWidth <= 0)
                throw new ArgumentOutOfRangeException(nameof(outputWidth));
            if (atlasHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(outputHeight));
            void Pulse()
            {
                cancellationToken.ThrowIfCancellationRequested();
                heartbeat?.Invoke();
            }

            var decodedSources = new Dictionary<int, IImage>();
            var decodedByDdsBytes = new Dictionary<byte[], IImage>(ReferenceEqualityComparer.Instance);
            try
            {
                var decodeStopwatch = Stopwatch.StartNew();
                foreach (var (id, ddsBytes) in ddsSources)
                {
                    Pulse();

                    if (!decodedByDdsBytes.TryGetValue(ddsBytes, out var image))
                    {
                        using var stream = new MemoryStream(ddsBytes);
                        image = Pfimage.FromStream(stream);
                        if (image.Format != PfimImageFormat.Rgba32)
                        {
                            image.Dispose();
                            throw new NotSupportedException(
                                $"Unsupported DDS pixel format for texture atlas generation: {image.Format}. Expected RGBA32.");
                        }

                        decodedByDdsBytes.Add(ddsBytes, image);
                    }

                    decodedSources[id] = image;
                }
                decodeStopwatch.Stop();

                if (statistics != null)
                {
                    statistics.DecodedSourceCount = decodedSources.Count;
                    statistics.UniqueDecodedDdsCount = decodedByDdsBytes.Count;
                    statistics.DdsDecodeElapsed = decodeStopwatch.Elapsed;
                }

                var composeStopwatch = Stopwatch.StartNew();
                var mipPixels = new List<byte[]>();
                var mipWidth = atlasWidth;
                var mipHeight = atlasHeight;
                var mipLevel = 0;

                while (true)
                {
                    Pulse();
                    if (statistics != null)
                        statistics.MipLevelsBuilt++;

                    var atlasPixels = new byte[checked(mipWidth * mipHeight * 4)];
                    var coreOccupancy = new bool[checked(mipWidth * mipHeight)];

                    // Write core regions first so padding can never overwrite another source's
                    // real texels when placements converge at lower mip levels.
                    foreach (var placement in plan.Placements)
                    {
                        Pulse();
                        if (omittedSourceIds?.Contains(placement.Id) == true)
                            continue;
                        var constantColor = default(TextureAtlasConstantColor);
                        var isConstant =
                            constantSources != null &&
                            constantSources.TryGetValue(placement.Id, out constantColor);
                        IImage? source = null;
                        MipLevelInfo? sourceMip = null;
                        if (!isConstant)
                        {
                            if (!decodedSources.TryGetValue(placement.Id, out source))
                                throw new InvalidOperationException($"Missing texture data for atlas source {placement.Id}.");
                            sourceMip = GetMipLevelForLayoutMip(
                                source,
                                placement,
                                plan,
                                atlasWidth,
                                atlasHeight,
                                mipLevel);
                        }

                        var bounds = GetMipPlacementBounds(plan, placement, mipWidth, mipHeight);

                        if (!isConstant)
                        {
                            if (statistics != null)
                                statistics.RowCopyAttempts++;

                            if (TryCopyCorePlacementRows(
                                    atlasPixels,
                                    coreOccupancy,
                                    source!,
                                    sourceMip!,
                                    plan,
                                    placement,
                                    bounds,
                                    mipWidth,
                                    mipHeight,
                                    forceOpaqueAlphaSourceIds?.Contains(placement.Id) == true,
                                    requireUnoccupiedCheck: mipLevel != 0))
                            {
                                if (statistics != null)
                                {
                                    statistics.RowCopyPlacements++;
                                    statistics.RowCopyPixels +=
                                        (long)(bounds.Right - bounds.Left) *
                                        (bounds.Bottom - bounds.Top);
                                }

                                continue;
                            }

                            if (statistics != null)
                                statistics.RowCopyFallbacks++;
                        }

                        var forceOpaqueAlpha =
                            forceOpaqueAlphaSourceIds?.Contains(placement.Id) == true;
                        if (!isConstant)
                        {
                            var mappedPixels = CopyMappedCorePlacement(
                                atlasPixels,
                                coreOccupancy,
                                source!,
                                sourceMip!,
                                plan,
                                placement,
                                bounds,
                                mipWidth,
                                mipHeight,
                                forceOpaqueAlpha);
                            if (statistics != null)
                                statistics.MappedCorePixels += mappedPixels;
                            continue;
                        }

                        long constantCoreSkippedPixels = 0;
                        for (var y = bounds.Top; y < bounds.Bottom; y++)
                        {
                            for (var x = bounds.Left; x < bounds.Right; x++)
                            {
                                var index = y * mipWidth + x;
                                if (coreOccupancy[index])
                                {
                                    constantCoreSkippedPixels++;
                                    continue;
                                }

                                WriteConstantPixel(
                                    atlasPixels,
                                    mipWidth,
                                    x,
                                    y,
                                    constantColor,
                                    forceOpaqueAlpha);
                                coreOccupancy[index] = true;
                            }
                        }

                        if (statistics != null)
                        {
                            statistics.ConstantCorePixels +=
                                (long)(bounds.Right - bounds.Left) *
                                (bounds.Bottom - bounds.Top) -
                                constantCoreSkippedPixels;
                        }
                    }

                    // Rebuild an edge extrusion independently at every mip level. This avoids
                    // the classic atlas problem where a small base-level gutter disappears as
                    // the whole atlas is downsampled and neighboring/transparent regions bleed
                    // into the material.
                    foreach (var placement in plan.Placements)
                    {
                        Pulse();
                        if (omittedSourceIds?.Contains(placement.Id) == true)
                            continue;
                        var constantColor = default(TextureAtlasConstantColor);
                        var isConstant =
                            constantSources != null &&
                            constantSources.TryGetValue(placement.Id, out constantColor);
                        IImage? source = null;
                        MipLevelInfo? sourceMip = null;
                        if (!isConstant)
                        {
                            if (!decodedSources.TryGetValue(placement.Id, out source))
                                throw new InvalidOperationException($"Missing texture data for atlas source {placement.Id}.");
                            sourceMip = GetMipLevelForLayoutMip(
                                source,
                                placement,
                                plan,
                                atlasWidth,
                                atlasHeight,
                                mipLevel);
                        }

                        var bounds = GetMipPlacementBounds(plan, placement, mipWidth, mipHeight);
                        var paddingX = Math.Max(1, (int)Math.Ceiling((double)placement.Padding * mipWidth / plan.Width));
                        var paddingY = Math.Max(1, (int)Math.Ceiling((double)placement.Padding * mipHeight / plan.Height));

                        var left = Math.Max(0, bounds.Left - paddingX);
                        var top = Math.Max(0, bounds.Top - paddingY);
                        var right = Math.Min(mipWidth, bounds.Right + paddingX);
                        var bottom = Math.Min(mipHeight, bounds.Bottom + paddingY);

                        var forceOpaqueAlpha =
                            forceOpaqueAlphaSourceIds?.Contains(placement.Id) == true;

                        // Only visit the actual gutter bands. The old path scanned the full
                        // padded rectangle, including every core texel, merely to discover that
                        // coreOccupancy was already true. On large atlases that effectively
                        // traversed most of the atlas twice.
                        long paddingPixels = 0;
                        paddingPixels += CopyPaddingRectangle(
                            atlasPixels,
                            coreOccupancy,
                            source,
                            sourceMip,
                            plan,
                            placement,
                            constantColor,
                            isConstant,
                            mipWidth,
                            mipHeight,
                            left,
                            top,
                            right,
                            bounds.Top,
                            forceOpaqueAlpha);

                        paddingPixels += CopyPaddingRectangle(
                            atlasPixels,
                            coreOccupancy,
                            source,
                            sourceMip,
                            plan,
                            placement,
                            constantColor,
                            isConstant,
                            mipWidth,
                            mipHeight,
                            left,
                            bounds.Bottom,
                            right,
                            bottom,
                            forceOpaqueAlpha);

                        paddingPixels += CopyPaddingRectangle(
                            atlasPixels,
                            coreOccupancy,
                            source,
                            sourceMip,
                            plan,
                            placement,
                            constantColor,
                            isConstant,
                            mipWidth,
                            mipHeight,
                            left,
                            bounds.Top,
                            bounds.Left,
                            bounds.Bottom,
                            forceOpaqueAlpha);

                        paddingPixels += CopyPaddingRectangle(
                            atlasPixels,
                            coreOccupancy,
                            source,
                            sourceMip,
                            plan,
                            placement,
                            constantColor,
                            isConstant,
                            mipWidth,
                            mipHeight,
                            bounds.Right,
                            bounds.Top,
                            right,
                            bounds.Bottom,
                            forceOpaqueAlpha);
                        if (statistics != null)
                            statistics.PaddingPixels += paddingPixels;
                    }

                    mipConsumer?.Invoke(mipLevel, mipWidth, mipHeight, atlasPixels);
                    if (retainMipPixels)
                        mipPixels.Add(atlasPixels);

                    if (mipWidth == 1 && mipHeight == 1)
                        break;

                    mipWidth = Math.Max(1, mipWidth / 2);
                    mipHeight = Math.Max(1, mipHeight / 2);
                    mipLevel++;
                }

                composeStopwatch.Stop();
                if (statistics != null)
                    statistics.ComposeElapsed = composeStopwatch.Elapsed;

                return mipPixels;
            }
            finally
            {
                foreach (var source in decodedByDdsBytes.Values)
                    source.Dispose();
            }
        }

        public static int CalculateMipLevelCount(int width, int height)
        {
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height));

            var count = 1;
            while (width > 1 || height > 1)
            {
                width = Math.Max(1, width / 2);
                height = Math.Max(1, height / 2);
                count++;
            }

            return count;
        }

        public static (int Width, int Height) CalculateOutputDimensions(
            TextureAtlasPlan plan,
            IReadOnlyDictionary<int, (int Width, int Height)> sourceDimensions,
            int maxAtlasSize = DefaultMaxAtlasSize)
        {
            if (maxAtlasSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxAtlasSize));
            if (sourceDimensions.Count == 0)
                return (plan.Width, plan.Height);

            double requiredScaleX = 0;
            double requiredScaleY = 0;

            foreach (var placement in plan.Placements)
            {
                if (!sourceDimensions.TryGetValue(placement.Id, out var dimensions))
                    continue;
                if (dimensions.Width <= 0 || dimensions.Height <= 0)
                    throw new ArgumentOutOfRangeException(nameof(sourceDimensions));

                requiredScaleX = Math.Max(
                    requiredScaleX,
                    (double)dimensions.Width / placement.SourceWidth);
                requiredScaleY = Math.Max(
                    requiredScaleY,
                    (double)dimensions.Height / placement.SourceHeight);
            }

            if (requiredScaleX <= 0 || requiredScaleY <= 0)
                return (plan.Width, plan.Height);

            var width = NextPowerOfTwo(Math.Max(
                1,
                checked((int)Math.Ceiling(plan.Width * requiredScaleX))));
            var height = NextPowerOfTwo(Math.Max(
                1,
                checked((int)Math.Ceiling(plan.Height * requiredScaleY))));

            if (width > maxAtlasSize || height > maxAtlasSize)
            {
                throw new InvalidOperationException(
                    $"Channel atlas requires {width}x{height}, exceeding the {maxAtlasSize}x{maxAtlasSize} atlas limit.");
            }

            return (width, height);
        }

        private static MipLevelInfo GetMipLevelForLayoutMip(
            IImage source,
            TextureAtlasPlacement placement,
            TextureAtlasPlan plan,
            int atlasWidth,
            int atlasHeight,
            int atlasMipLevel)
        {
            if (atlasMipLevel <= 0)
                return GetMipLevel(source, 0);

            // The shared UV plan is expressed in primary-texture pixels, while each material
            // channel may have a different physical atlas size. Delay authored source mips only
            // when this channel's physical rectangle is larger than the source texture itself.
            var destinationWidth = placement.SourceWidth * (double)atlasWidth / plan.Width;
            var destinationHeight = placement.SourceHeight * (double)atlasHeight / plan.Height;
            var scaleX = destinationWidth / source.Width;
            var scaleY = destinationHeight / source.Height;
            var layoutScale = Math.Max(1.0, Math.Max(scaleX, scaleY));
            var delayedMipLevels = (int)Math.Ceiling(Math.Log2(layoutScale));
            var sourceMipLevel = Math.Max(0, atlasMipLevel - delayedMipLevels);

            return GetMipLevel(source, sourceMipLevel);
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

        private static bool TryCopyCorePlacementRows(
            byte[] atlasPixels,
            bool[] coreOccupancy,
            IImage source,
            MipLevelInfo sourceMip,
            TextureAtlasPlan plan,
            TextureAtlasPlacement placement,
            MipPlacementBounds bounds,
            int mipWidth,
            int mipHeight,
            bool forceOpaqueAlpha,
            bool requireUnoccupiedCheck)
        {
            if (forceOpaqueAlpha ||
                placement.CropX < 0 ||
                placement.CropY < 0 ||
                placement.CropX + placement.CropWidth > placement.SourceWidth ||
                placement.CropY + placement.CropHeight > placement.SourceHeight)
            {
                return false;
            }

            // If source and destination advance at exactly one source texel per atlas texel,
            // any mip can be copied a row at a time instead of doing floating-point UV mapping,
            // floor and modulo operations for every individual pixel. Lower mips first verify
            // that rounding has not caused this core rectangle to overlap an earlier placement.
            if ((long)sourceMip.Width * plan.Width != (long)placement.SourceWidth * mipWidth ||
                (long)sourceMip.Height * plan.Height != (long)placement.SourceHeight * mipHeight)
            {
                return false;
            }

            var copyWidth = bounds.Right - bounds.Left;
            var copyHeight = bounds.Bottom - bounds.Top;
            if (copyWidth <= 0 || copyHeight <= 0)
                return false;

            var atlasBaseX = (bounds.Left + 0.5) * plan.Width / mipWidth;
            var atlasBaseY = (bounds.Top + 0.5) * plan.Height / mipHeight;
            var sourceBaseX = placement.CropX + (atlasBaseX - placement.DestinationX);
            var sourceBaseY = placement.CropY + (atlasBaseY - placement.DestinationY);
            var sourceStartX = (int)Math.Floor(sourceBaseX * sourceMip.Width / placement.SourceWidth);
            var sourceStartY = (int)Math.Floor(sourceBaseY * sourceMip.Height / placement.SourceHeight);

            if (sourceStartX < 0 ||
                sourceStartY < 0 ||
                sourceStartX + copyWidth > sourceMip.Width ||
                sourceStartY + copyHeight > sourceMip.Height)
            {
                return false;
            }

            if (requireUnoccupiedCheck)
            {
                for (var row = 0; row < copyHeight; row++)
                {
                    var destinationPixelOffset =
                        checked((bounds.Top + row) * mipWidth + bounds.Left);
                    if (Array.IndexOf(
                            coreOccupancy,
                            true,
                            destinationPixelOffset,
                            copyWidth) >= 0)
                    {
                        return false;
                    }
                }
            }

            var rowBytes = checked(copyWidth * 4);
            for (var row = 0; row < copyHeight; row++)
            {
                var sourceOffset = checked(
                    sourceMip.DataOffset +
                    (sourceStartY + row) * sourceMip.Stride +
                    sourceStartX * 4);
                var destinationPixelOffset = checked((bounds.Top + row) * mipWidth + bounds.Left);
                var destinationOffset = checked(destinationPixelOffset * 4);

                Buffer.BlockCopy(source.Data, sourceOffset, atlasPixels, destinationOffset, rowBytes);
                Array.Fill(coreOccupancy, true, destinationPixelOffset, copyWidth);
            }

            return true;
        }

        private static long CopyMappedCorePlacement(
            byte[] atlasPixels,
            bool[] coreOccupancy,
            IImage source,
            MipLevelInfo sourceMip,
            TextureAtlasPlan plan,
            TextureAtlasPlacement placement,
            MipPlacementBounds bounds,
            int mipWidth,
            int mipHeight,
            bool forceOpaqueAlpha)
        {
            var width = bounds.Right - bounds.Left;
            if (width <= 0 || bounds.Bottom <= bounds.Top)
                return 0;

            long skippedPixels = 0;

            // X mapping is identical for every destination row. Precompute it once instead of
            // repeating floating-point transform/floor/modulo work for every pixel.
            var sourceXOffsets = new int[width];
            for (var localX = 0; localX < width; localX++)
            {
                var atlasX = bounds.Left + localX;
                var atlasBaseX = (atlasX + 0.5) * plan.Width / mipWidth;
                var sourceBaseX =
                    placement.CropX + (atlasBaseX - placement.DestinationX);
                var sourceX = PositiveModulo(
                    (int)Math.Floor(
                        sourceBaseX * sourceMip.Width / placement.SourceWidth),
                    sourceMip.Width);
                sourceXOffsets[localX] = sourceX * 4;
            }

            for (var y = bounds.Top; y < bounds.Bottom; y++)
            {
                var atlasBaseY = (y + 0.5) * plan.Height / mipHeight;
                var sourceBaseY =
                    placement.CropY + (atlasBaseY - placement.DestinationY);
                var sourceY = PositiveModulo(
                    (int)Math.Floor(
                        sourceBaseY * sourceMip.Height / placement.SourceHeight),
                    sourceMip.Height);
                var sourceRowOffset =
                    sourceMip.DataOffset + sourceY * sourceMip.Stride;
                var destinationPixelOffset = y * mipWidth + bounds.Left;
                var destinationOffset = destinationPixelOffset * 4;

                for (var localX = 0; localX < width; localX++)
                {
                    var occupancyIndex = destinationPixelOffset + localX;
                    if (coreOccupancy[occupancyIndex])
                    {
                        skippedPixels++;
                        continue;
                    }

                    var sourceOffset = sourceRowOffset + sourceXOffsets[localX];
                    var pixelOffset = destinationOffset + localX * 4;
                    atlasPixels[pixelOffset] = source.Data[sourceOffset];
                    atlasPixels[pixelOffset + 1] = source.Data[sourceOffset + 1];
                    atlasPixels[pixelOffset + 2] = source.Data[sourceOffset + 2];
                    atlasPixels[pixelOffset + 3] = forceOpaqueAlpha
                        ? byte.MaxValue
                        : source.Data[sourceOffset + 3];
                    coreOccupancy[occupancyIndex] = true;
                }
            }

            return (long)width * (bounds.Bottom - bounds.Top) - skippedPixels;
        }

        private static long CopyPaddingRectangle(
            byte[] atlasPixels,
            bool[] coreOccupancy,
            IImage? source,
            MipLevelInfo? sourceMip,
            TextureAtlasPlan plan,
            TextureAtlasPlacement placement,
            TextureAtlasConstantColor constantColor,
            bool isConstant,
            int mipWidth,
            int mipHeight,
            int left,
            int top,
            int right,
            int bottom,
            bool forceOpaqueAlpha)
        {
            if (left >= right || top >= bottom)
                return 0;

            long skippedPixels = 0;
            var totalPixels = (long)(right - left) * (bottom - top);

            if (isConstant)
            {
                for (var y = top; y < bottom; y++)
                {
                    var occupancyIndex = y * mipWidth + left;
                    for (var x = left; x < right; x++, occupancyIndex++)
                    {
                        if (coreOccupancy[occupancyIndex])
                        {
                            skippedPixels++;
                            continue;
                        }

                        WriteConstantPixel(
                            atlasPixels,
                            mipWidth,
                            x,
                            y,
                            constantColor,
                            forceOpaqueAlpha);
                    }
                }

                return totalPixels - skippedPixels;
            }

            if (source == null || sourceMip == null)
                throw new InvalidOperationException(
                    $"Missing decoded source for atlas placement {placement.Id}.");

            var width = right - left;
            var sourceXOffsets = new int[width];
            for (var localX = 0; localX < width; localX++)
            {
                var atlasX = left + localX;
                var atlasBaseX = (atlasX + 0.5) * plan.Width / mipWidth;
                atlasBaseX = Math.Clamp(
                    atlasBaseX,
                    placement.DestinationX + 0.5,
                    placement.DestinationX + placement.CropWidth - 0.5);
                var sourceBaseX =
                    placement.CropX + (atlasBaseX - placement.DestinationX);
                var sourceX = PositiveModulo(
                    (int)Math.Floor(
                        sourceBaseX * sourceMip.Width / placement.SourceWidth),
                    sourceMip.Width);
                sourceXOffsets[localX] = sourceX * 4;
            }

            for (var y = top; y < bottom; y++)
            {
                var atlasBaseY = (y + 0.5) * plan.Height / mipHeight;
                atlasBaseY = Math.Clamp(
                    atlasBaseY,
                    placement.DestinationY + 0.5,
                    placement.DestinationY + placement.CropHeight - 0.5);
                var sourceBaseY =
                    placement.CropY + (atlasBaseY - placement.DestinationY);
                var sourceY = PositiveModulo(
                    (int)Math.Floor(
                        sourceBaseY * sourceMip.Height / placement.SourceHeight),
                    sourceMip.Height);
                var sourceRowOffset =
                    sourceMip.DataOffset + sourceY * sourceMip.Stride;
                var destinationPixelOffset = y * mipWidth + left;
                var destinationOffset = destinationPixelOffset * 4;

                for (var localX = 0; localX < width; localX++)
                {
                    var occupancyIndex = destinationPixelOffset + localX;
                    if (coreOccupancy[occupancyIndex])
                    {
                        skippedPixels++;
                        continue;
                    }

                    var sourceOffset = sourceRowOffset + sourceXOffsets[localX];
                    var pixelOffset = destinationOffset + localX * 4;
                    atlasPixels[pixelOffset] = source.Data[sourceOffset];
                    atlasPixels[pixelOffset + 1] = source.Data[sourceOffset + 1];
                    atlasPixels[pixelOffset + 2] = source.Data[sourceOffset + 2];
                    atlasPixels[pixelOffset + 3] = forceOpaqueAlpha
                        ? byte.MaxValue
                        : source.Data[sourceOffset + 3];
                }
            }

            return totalPixels - skippedPixels;
        }

        public static (int Width, int Height) GetDimensions(byte[] ddsBytes)
        {
            // DDS dimensions live in the fixed header. Reading them directly avoids decoding
            // the full BC texture merely to discover width/height during pack planning.
            if (ddsBytes.Length < 20 ||
                BinaryPrimitives.ReadUInt32LittleEndian(ddsBytes.AsSpan(0, 4)) != 0x20534444)
            {
                throw new InvalidDataException("Texture is not a valid DDS file.");
            }

            var height = BinaryPrimitives.ReadInt32LittleEndian(ddsBytes.AsSpan(12, 4));
            var width = BinaryPrimitives.ReadInt32LittleEndian(ddsBytes.AsSpan(16, 4));
            if (width <= 0 || height <= 0)
                throw new InvalidDataException($"DDS has invalid dimensions {width}x{height}.");

            return (width, height);
        }

        public static bool TryGetUniformColor(
            byte[] ddsBytes,
            out TextureAtlasConstantColor color)
        {
            color = default;

            using var stream = new MemoryStream(ddsBytes);
            using var image = Pfimage.FromStream(stream);
            if (image.Format != PfimImageFormat.Rgba32 || image.Width <= 0 || image.Height <= 0)
                return false;

            TextureAtlasConstantColor? expected = null;
            if (!TryValidateUniformLevel(
                    image,
                    image.Width,
                    image.Height,
                    image.Stride,
                    0,
                    ref expected))
                return false;

            foreach (var mip in image.MipMaps)
            {
                if (!TryValidateUniformLevel(
                        image,
                        mip.Width,
                        mip.Height,
                        mip.Stride,
                        mip.DataOffset,
                        ref expected))
                    return false;
            }

            if (!expected.HasValue)
                return false;

            color = expected.Value;
            return true;
        }

        private static bool TryValidateUniformLevel(
            IImage image,
            int width,
            int height,
            int stride,
            int dataOffset,
            ref TextureAtlasConstantColor? expected)
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var offset = dataOffset + y * stride + x * 4;
                    var current = new TextureAtlasConstantColor(
                        image.Data[offset],
                        image.Data[offset + 1],
                        image.Data[offset + 2],
                        image.Data[offset + 3]);

                    if (!expected.HasValue)
                    {
                        expected = current;
                        continue;
                    }

                    if (current != expected.Value)
                        return false;
                }
            }

            return true;
        }

        private static void WriteConstantPixel(
            byte[] atlasPixels,
            int atlasWidth,
            int x,
            int y,
            TextureAtlasConstantColor color,
            bool forceOpaqueAlpha)
        {
            var offset = (y * atlasWidth + x) * 4;
            atlasPixels[offset] = color.B;
            atlasPixels[offset + 1] = color.G;
            atlasPixels[offset + 2] = color.R;
            atlasPixels[offset + 3] = forceOpaqueAlpha ? byte.MaxValue : color.A;
        }

        private static void CopyConstantRegionAndPadding(
            byte[] atlasPixels,
            int atlasWidth,
            int atlasHeight,
            TextureAtlasPlacement placement,
            TextureAtlasConstantColor color,
            bool forceOpaqueAlpha)
        {
            for (var localY = -placement.Padding;
                 localY < placement.CropHeight + placement.Padding;
                 localY++)
            {
                var destinationY = placement.DestinationY + localY;
                if (destinationY < 0 || destinationY >= atlasHeight)
                    throw new InvalidOperationException($"Atlas placement {placement.Id} exceeds atlas bounds.");

                for (var localX = -placement.Padding;
                     localX < placement.CropWidth + placement.Padding;
                     localX++)
                {
                    var destinationX = placement.DestinationX + localX;
                    if (destinationX < 0 || destinationX >= atlasWidth)
                        throw new InvalidOperationException($"Atlas placement {placement.Id} exceeds atlas bounds.");

                    WriteConstantPixel(
                        atlasPixels,
                        atlasWidth,
                        destinationX,
                        destinationY,
                        color,
                        forceOpaqueAlpha);
                }
            }
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
                var sourceY = PositiveModulo(
                    (int)Math.Floor(virtualSourceY * (double)sourceHeight / placement.SourceHeight),
                    sourceHeight);

                for (var localX = -padding; localX < placement.CropWidth + padding; localX++)
                {
                    var destinationX = placement.DestinationX + localX;
                    if (destinationX < 0 || destinationX >= atlasWidth)
                        throw new InvalidOperationException($"Atlas placement {placement.Id} exceeds atlas bounds.");

                    var cropLocalX = Math.Clamp(localX, 0, placement.CropWidth - 1);
                    var virtualSourceX = checked(placement.CropX + cropLocalX);
                    var sourceX = PositiveModulo(
                        (int)Math.Floor(virtualSourceX * (double)sourceWidth / placement.SourceWidth),
                        sourceWidth);

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
            int atlasWidth,
            int atlasHeight,
            int padding,
            out IReadOnlyList<TextureAtlasPlacement> placements)
        {
            var output = new List<TextureAtlasPlacement>(pending.Count);
            var x = 0;
            var y = 0;
            var shelfHeight = 0;

            foreach (var item in pending)
            {
                if (item.PaddedWidth > atlasWidth || item.PaddedHeight > atlasHeight)
                {
                    placements = [];
                    return false;
                }

                if (x + item.PaddedWidth > atlasWidth)
                {
                    y += shelfHeight;
                    x = 0;
                    shelfHeight = 0;
                }

                if (y + item.PaddedHeight > atlasHeight)
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
