using System.Globalization;
using System.Text;

namespace Editors.PackOptimizer.Core
{
    /// <summary>
    /// What happened to one 4x4 block of a generated repack sheet at one mip level. A block is
    /// either a verbatim copy of a source block or whatever DirectXTex encoded for it.
    /// </summary>
    internal enum SharedRepackBlockFate : byte
    {
        /// <summary>No source texel in the block at this level: padding between charts.</summary>
        Gutter = 0,

        /// <summary>No chart has a whole texel left at this level: the level is box-filtered, never overlaid.</summary>
        Generated = 1,

        /// <summary>The block is touched by a chart but is not wholly inside it (chart edge, or shared with a neighbour).</summary>
        Partial = 2,

        /// <summary>The chart's crop is not on the 4x4 grid in the source or the sheet at this level.</summary>
        Misaligned = 3,

        /// <summary>The source has no such mip level (or is too short to hold it).</summary>
        BeyondSourceMips = 4,

        /// <summary>The source's block format differs from the sheet's.</summary>
        FormatMismatch = 5,

        /// <summary>The source could not be read.</summary>
        SourceUnavailable = 6,

        /// <summary>The block is the source's own block, copied bit for bit.</summary>
        Copied = 7,
    }

    /// <summary>Per-level, per-cause block counts of one generated sheet.</summary>
    internal sealed class SharedRepackBlockStats
    {
        private static readonly int FateCount = Enum.GetValues<SharedRepackBlockFate>().Length;

        public SharedRepackBlockStats(int levels)
        {
            LevelWidths = new int[levels];
            LevelHeights = new int[levels];
            Counts = new long[levels][];
            for (var level = 0; level < levels; level++)
                Counts[level] = new long[FateCount];
        }

        public int[] LevelWidths { get; }
        public int[] LevelHeights { get; }
        public long[][] Counts { get; }
        public int Levels => Counts.Length;

        public long Total => Counts.Sum(level => level.Sum());
        public long Copied => Counts.Sum(level => level[(int)SharedRepackBlockFate.Copied]);
        public long Encoded => Total - Copied;

        public long Count(SharedRepackBlockFate fate) => Counts.Sum(level => level[(int)fate]);
        public long Count(int level, SharedRepackBlockFate fate) => Counts[level][(int)fate];
        public long LevelTotal(int level) => Counts[level].Sum();

        public void Add(SharedRepackBlockStats other)
        {
            for (var level = 0; level < Math.Min(Levels, other.Levels); level++)
            {
                for (var fate = 0; fate < FateCount; fate++)
                    Counts[level][fate] += other.Counts[level][fate];
            }
        }

        public string DescribeCauses()
        {
            var parts = new List<string>();
            foreach (var fate in Enum.GetValues<SharedRepackBlockFate>())
            {
                if (fate == SharedRepackBlockFate.Copied)
                    continue;

                var count = Count(fate);
                if (count != 0)
                    parts.Add($"{count.ToString("N0", CultureInfo.CurrentCulture)} {Describe(fate)}");
            }

            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }

        public static string Describe(SharedRepackBlockFate fate) => fate switch
        {
            SharedRepackBlockFate.Gutter => "gutter/padding",
            SharedRepackBlockFate.Generated => "generated level",
            SharedRepackBlockFate.Partial => "partly covered",
            SharedRepackBlockFate.Misaligned => "misaligned crop",
            SharedRepackBlockFate.BeyondSourceMips => "beyond the source's mips",
            SharedRepackBlockFate.FormatMismatch => "format mismatch",
            SharedRepackBlockFate.SourceUnavailable => "source unreadable",
            _ => fate.ToString(),
        };

        public string DescribeLevels()
        {
            var builder = new StringBuilder();
            for (var level = 0; level < Levels; level++)
            {
                if (level != 0)
                    builder.Append("; ");
                var total = LevelTotal(level);
                builder.Append(CultureInfo.CurrentCulture, $"L{level} {Count(level, SharedRepackBlockFate.Copied):N0}/{total:N0}");
                var left = Enum.GetValues<SharedRepackBlockFate>()
                    .Where(fate => fate != SharedRepackBlockFate.Copied && Count(level, fate) != 0)
                    .Select(fate => $"{Count(level, fate):N0} {Describe(fate)}")
                    .ToList();
                if (left.Count != 0)
                    builder.Append(" (").Append(string.Join(", ", left)).Append(')');
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// Copies a source texture's compressed 4x4 blocks into a generated sheet wherever a chart's crop
    /// lies on the block grid. Works on primitives (level byte spans, block grids) so the rules can be
    /// tested without a pack or a DDS parser; nothing here decodes or re-encodes.
    /// </summary>
    internal static class SharedRepackBlockCopier
    {
        public const int BlockEdge = 4;

        /// <summary>
        /// A source block can stand in for a sheet block only if the block format is the same.
        /// The sRGB flag is metadata: the current repack decodes and re-encodes stored values without any
        /// colour-space conversion, so a UNORM block placed on an sRGB sheet (or the reverse) has the same
        /// stored values as the current path would produce, and is allowed.
        /// </summary>
        public static bool AreBlocksCompatible(string sourceFormat, string targetFormat)
            => string.Equals(sourceFormat, targetFormat, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Why a chart's crop cannot be block-copied at a level, or null when it can. Coordinates are the
        /// crop's level-0 texel origins in the source and on the sheet.
        /// </summary>
        public static SharedRepackBlockFate? GetFallbackReason(
            bool sourceReadable,
            string sourceFormat,
            int sourceBytesPerBlock,
            string targetFormat,
            int targetBytesPerBlock,
            int sourceMipCount,
            long sourceLength,
            long sourceRequiredLength,
            int level,
            int sourceX,
            int sourceY,
            int destinationX,
            int destinationY)
        {
            if (!sourceReadable)
                return SharedRepackBlockFate.SourceUnavailable;
            if (!AreBlocksCompatible(sourceFormat, targetFormat) || sourceBytesPerBlock != targetBytesPerBlock)
                return SharedRepackBlockFate.FormatMismatch;
            if (sourceMipCount <= level || sourceLength < sourceRequiredLength)
                return SharedRepackBlockFate.BeyondSourceMips;

            // The sheet's level-n crop is the source's own level-n crop only if the origins are whole
            // texels at that level.
            if (((sourceX | sourceY | destinationX | destinationY) & ((1 << level) - 1)) != 0)
                return SharedRepackBlockFate.Misaligned;
            return null;
        }

        public static int BlocksFor(int texels) => (Math.Max(1, texels) + BlockEdge - 1) / BlockEdge;

        /// <summary>
        /// Marks every sheet block the destination rectangle touches with <paramref name="fate"/> unless a
        /// block already has a stronger outcome (higher enum value wins; Copied is the strongest).
        /// </summary>
        public static void Mark(
            Span<SharedRepackBlockFate> fates,
            int blocksWide,
            int blocksHigh,
            int destinationX,
            int destinationY,
            int width,
            int height,
            SharedRepackBlockFate fate)
        {
            if (width <= 0 || height <= 0)
                return;

            var firstX = Math.Max(0, destinationX / BlockEdge);
            var firstY = Math.Max(0, destinationY / BlockEdge);
            var lastX = Math.Min(blocksWide - 1, (destinationX + width - 1) / BlockEdge);
            var lastY = Math.Min(blocksHigh - 1, (destinationY + height - 1) / BlockEdge);
            for (var by = firstY; by <= lastY; by++)
            {
                for (var bx = firstX; bx <= lastX; bx++)
                {
                    ref var slot = ref fates[by * blocksWide + bx];
                    if (fate > slot)
                        slot = fate;
                }
            }
        }

        /// <summary>
        /// Copies the blocks wholly inside the crop. Texel coordinates are at the level being copied.
        /// The crop must start on the 4x4 grid in both the source and the destination; blocks only
        /// partly inside the crop are left alone. Returns the number of blocks copied and records the
        /// outcome of every touched destination block in <paramref name="fates"/>; each copied row is
        /// compared with its source afterwards and a difference is counted in <paramref name="verifyFailures"/>.
        /// </summary>
        public static int CopyRectangle(
            ReadOnlySpan<byte> sourceLevel,
            int sourceBlocksWide,
            int sourceBlocksHigh,
            Span<byte> destinationLevel,
            int destinationBlocksWide,
            int destinationBlocksHigh,
            int bytesPerBlock,
            int sourceX,
            int sourceY,
            int destinationX,
            int destinationY,
            int width,
            int height,
            Span<SharedRepackBlockFate> fates,
            ref int verifyFailures)
        {
            if (width <= 0 || height <= 0)
                return 0;

            var aligned = sourceX % BlockEdge == 0 && sourceY % BlockEdge == 0 &&
                          destinationX % BlockEdge == 0 && destinationY % BlockEdge == 0 &&
                          sourceX >= 0 && sourceY >= 0 && destinationX >= 0 && destinationY >= 0;
            var untouchedFate = aligned ? SharedRepackBlockFate.Partial : SharedRepackBlockFate.Misaligned;
            Mark(fates, destinationBlocksWide, destinationBlocksHigh, destinationX, destinationY, width, height, untouchedFate);
            if (!aligned)
                return 0;

            var blocksWide = Math.Min(width / BlockEdge, Math.Min(sourceBlocksWide - sourceX / BlockEdge, destinationBlocksWide - destinationX / BlockEdge));
            var blocksHigh = Math.Min(height / BlockEdge, Math.Min(sourceBlocksHigh - sourceY / BlockEdge, destinationBlocksHigh - destinationY / BlockEdge));
            var copied = 0;
            for (var by = 0; by < blocksHigh; by++)
            {
                var from = ((long)(sourceY / BlockEdge + by) * sourceBlocksWide + sourceX / BlockEdge) * bytesPerBlock;
                var to = ((long)(destinationY / BlockEdge + by) * destinationBlocksWide + destinationX / BlockEdge) * bytesPerBlock;
                var length = blocksWide * bytesPerBlock;
                if (blocksWide <= 0 || from + length > sourceLevel.Length || to + length > destinationLevel.Length)
                    continue;

                sourceLevel.Slice((int)from, length).CopyTo(destinationLevel.Slice((int)to, length));
                // A copied block decodes exactly like its source block because it is the same bytes.
                if (!sourceLevel.Slice((int)from, length).SequenceEqual(destinationLevel.Slice((int)to, length)))
                    verifyFailures++;
                for (var bx = 0; bx < blocksWide; bx++)
                    fates[(destinationY / BlockEdge + by) * destinationBlocksWide + destinationX / BlockEdge + bx] = SharedRepackBlockFate.Copied;
                copied += blocksWide;
            }

            return copied;
        }

        /// <summary>Counts the fates of one level into the statistics. Untouched blocks are gutter or, when no chart reaches this level, generated.</summary>
        public static void Tally(SharedRepackBlockStats stats, int level, ReadOnlySpan<SharedRepackBlockFate> fates, bool levelHasCharts)
        {
            foreach (var fate in fates)
            {
                var effective = fate == SharedRepackBlockFate.Gutter && !levelHasCharts ? SharedRepackBlockFate.Generated : fate;
                stats.Counts[level][(int)effective]++;
            }
        }
    }

    /// <summary>How the encoder input of one chart rectangle at one mip level of a sheet was produced.</summary>
    internal enum SharedRepackMipSeed : byte
    {
        /// <summary>The source's own mip level, decoded and placed (gutter included).</summary>
        SourceMip = 0,

        /// <summary>The sheet's slot is scaled differently from the source's: no one-to-one level mapping.</summary>
        ExponentDiffers = 1,

        /// <summary>The rectangle does not start on a whole texel of this level, in the source or on the sheet.</summary>
        NonIntegerPosition = 2,

        /// <summary>The source has no such mip level.</summary>
        BeyondSourceMips = 3,

        /// <summary>The rectangle is smaller than one texel of this level.</summary>
        NoWholeTexel = 4,
    }

    /// <summary>Per-cause counts, in (chart rectangle, mip level) pairs, of how a sheet's lower levels were seeded.</summary>
    internal sealed class SharedRepackMipSeedStats
    {
        private static readonly int SeedCount = Enum.GetValues<SharedRepackMipSeed>().Length;

        public long[] Counts { get; } = new long[SeedCount];

        /// <summary>
        /// Per level: how far (largest channel difference) the texels that were seeded from a source mip
        /// were from what the box filter of the sheet's top level had put there (index = difference).
        /// That is the error the previous encoder input had against the source's own level.
        /// </summary>
        public long[][] Differences { get; } = Enumerable.Range(0, 16).Select(_ => new long[256]).ToArray();

        /// <summary>Pairs seeded from the source's mip whose last texel row or column was only partly inside the rectangle (that edge stayed box-filtered).</summary>
        public long PartialEdge { get; set; }

        public long Seeded => Counts[(int)SharedRepackMipSeed.SourceMip];
        public long Total => Counts.Sum();
        public long Count(SharedRepackMipSeed seed) => Counts[(int)seed];

        public void Add(SharedRepackMipSeedStats other)
        {
            for (var index = 0; index < Counts.Length; index++)
                Counts[index] += other.Counts[index];
            PartialEdge += other.PartialEdge;
            for (var level = 0; level < Differences.Length; level++)
            {
                for (var value = 0; value < 256; value++)
                    Differences[level][value] += other.Differences[level][value];
            }
        }

        /// <summary>Texel count, mean, 99th percentile, maximum and share above <paramref name="threshold"/> of the box-filter difference over levels first..last.</summary>
        public (long Texels, double Mean, int P99, int Max, double OverShare) Summarize(int first, int last, int threshold)
        {
            var histogram = new long[256];
            for (var level = Math.Max(0, first); level <= Math.Min(Differences.Length - 1, last); level++)
            {
                for (var value = 0; value < 256; value++)
                    histogram[value] += Differences[level][value];
            }

            var total = histogram.Sum();
            if (total == 0)
                return (0, 0, 0, 0, 0);
            double sum = 0;
            long running = 0;
            var p99 = 255;
            var max = 0;
            long over = 0;
            for (var value = 0; value < 256; value++)
            {
                sum += (double)value * histogram[value];
                if (histogram[value] != 0)
                    max = value;
                if (value > threshold)
                    over += histogram[value];
            }

            for (var value = 0; value < 256; value++)
            {
                running += histogram[value];
                if (running >= total * 0.99)
                {
                    p99 = value;
                    break;
                }
            }

            return (total, sum / total, p99, max, (double)over / total);
        }

        public string Describe()
        {
            var fallbacks = Enum.GetValues<SharedRepackMipSeed>()
                .Where(seed => seed != SharedRepackMipSeed.SourceMip && Count(seed) != 0)
                .Select(seed => $"{Count(seed).ToString("N0", CultureInfo.CurrentCulture)} {DescribeSeed(seed)}")
                .ToList();
            return $"seeded from the source's mips {Seeded.ToString("N0", CultureInfo.CurrentCulture)} of {Total.ToString("N0", CultureInfo.CurrentCulture)} " +
                   $"(chart, level) pairs; box-filtered fallback: {(fallbacks.Count == 0 ? "none" : string.Join(", ", fallbacks))}" +
                   (PartialEdge == 0 ? string.Empty : $"; {PartialEdge.ToString("N0", CultureInfo.CurrentCulture)} seeded pair(s) kept a box-filtered partial edge texel") +
                   DescribeDifferences();
        }

        /// <summary>What the seeded texels differed by from the plain box filter of the top level (the error that input had against the source's own level).</summary>
        public string DescribeDifferences()
        {
            var parts = new List<string>();
            foreach (var (name, first, last) in new[] { ("mips 1-3", 1, 3), ("mips 4+", 4, 15) })
            {
                var (texels, mean, p99, max, over) = Summarize(first, last, 16);
                if (texels != 0)
                {
                    parts.Add(string.Format(
                        CultureInfo.CurrentCulture,
                        "{0}: {1:N0} texel(s), box filter vs source mip max {2}, 99th percentile {3}, mean {4:0.00}, {5:P2} over 16",
                        name, texels, max, p99, mean, over));
                }
            }

            return parts.Count == 0 ? string.Empty : "; " + string.Join("; ", parts);
        }

        public static string DescribeSeed(SharedRepackMipSeed seed) => seed switch
        {
            SharedRepackMipSeed.SourceMip => "from the source's mip",
            SharedRepackMipSeed.ExponentDiffers => "slot scale differs from the source",
            SharedRepackMipSeed.NonIntegerPosition => "non-integer texel position",
            SharedRepackMipSeed.BeyondSourceMips => "beyond the source's mips",
            SharedRepackMipSeed.NoWholeTexel => "smaller than a texel",
            _ => seed.ToString(),
        };
    }

    /// <summary>
    /// Seeds one chart rectangle of a sheet's mip level with the source's own, decoded mip level (instead
    /// of leaving the box filter of the sheet's top level there). Primitives only, so it can be tested
    /// without a pack.
    /// </summary>
    internal static class SharedRepackMipSeeder
    {
        /// <summary>
        /// Copies the rectangle (level-0 texel units: source origin, sheet origin and size, the whole
        /// reserved rectangle with its gutter) at <paramref name="level"/> from the source's level
        /// <paramref name="level"/> into <paramref name="pixels"/> (the sheet's BGRA level), reading
        /// outside the source level like the top level does (edge texels repeat). The optional histogram
        /// counts, per seeded texel, the largest channel difference to what was there. Returns why it did
        /// not, leaving <paramref name="pixels"/> untouched, or SourceMip.
        /// </summary>
        public static SharedRepackMipSeed Seed(
            int level,
            int shiftX,
            int shiftY,
            int sourceX,
            int sourceY,
            int destinationX,
            int destinationY,
            int width,
            int height,
            (byte[] Bgra, int Width, int Height)? sourceLevel,
            byte[] pixels,
            int levelWidth,
            int levelHeight,
            out bool partialEdge,
            long[]? differenceHistogram = null)
        {
            partialEdge = false;
            if (shiftX != 0 || shiftY != 0)
                return SharedRepackMipSeed.ExponentDiffers;

            var mask = (1 << level) - 1;
            if (((sourceX | sourceY | destinationX | destinationY) & mask) != 0 || destinationX < 0 || destinationY < 0)
                return SharedRepackMipSeed.NonIntegerPosition;

            var wide = width >> level;
            var high = height >> level;
            if (wide <= 0 || high <= 0)
                return SharedRepackMipSeed.NoWholeTexel;
            if (sourceLevel == null)
                return SharedRepackMipSeed.BeyondSourceMips;

            var (bgra, sourceWidth, sourceHeight) = sourceLevel.Value;
            var sx = sourceX >> level;
            var sy = sourceY >> level;
            var dx = destinationX >> level;
            var dy = destinationY >> level;
            wide = Math.Min(wide, levelWidth - dx);
            high = Math.Min(high, levelHeight - dy);
            partialEdge = ((width | height) & mask) != 0;
            for (var y = 0; y < high; y++)
            {
                var row = Math.Clamp(sy + y, 0, sourceHeight - 1);
                for (var x = 0; x < wide; x++)
                {
                    var column = Math.Clamp(sx + x, 0, sourceWidth - 1);
                    var from = (row * sourceWidth + column) * 4;
                    var to = ((dy + y) * levelWidth + dx + x) * 4;
                    if (differenceHistogram != null)
                    {
                        var difference = 0;
                        for (var channel = 0; channel < 4; channel++)
                            difference = Math.Max(difference, Math.Abs(bgra[from + channel] - pixels[to + channel]));
                        differenceHistogram[difference]++;
                    }

                    Buffer.BlockCopy(bgra, from, pixels, to, 4);
                }
            }

            return SharedRepackMipSeed.SourceMip;
        }
    }
}
