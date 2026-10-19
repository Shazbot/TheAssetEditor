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
}
