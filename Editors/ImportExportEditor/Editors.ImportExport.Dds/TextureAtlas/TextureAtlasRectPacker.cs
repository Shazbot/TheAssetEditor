namespace Editors.ImportExport.TextureAtlas
{
    /// <summary>
    /// Deterministic, rotation-free rectangle packer used by <see cref="TextureAtlasBuilder"/>.
    /// Tries the legacy shelf packer first (so results are never worse than before), then MaxRects
    /// with several placement heuristics and input orderings. Positions are returned per input index.
    /// </summary>
    internal static class TextureAtlasRectPacker
    {
        // MaxRects is O(n * free-list^2); beyond this only the shelf packer is attempted.
        private const int MaxRectsItemLimit = 2000;

        // ContactPoint scores every candidate against all placed rects, so it is limited further.
        private const int ContactPointItemLimit = 300;

        private enum Heuristic
        {
            BestShortSideFit,
            BestAreaFit,
            BottomLeft,
            BestLongSideFit,
            ContactPoint,
        }

        private enum Ordering
        {
            MaxSideDescending,
            AreaDescending,
            HeightDescending,
            WidthDescending,
            PerimeterDescending,
        }

        public static bool TryPack(
            IReadOnlyList<(int Width, int Height)> sizes,
            int atlasWidth,
            int atlasHeight,
            out (int X, int Y)[] positions)
        {
            if (TryShelfPack(sizes, atlasWidth, atlasHeight, out positions))
                return true;

            if (sizes.Count > MaxRectsItemLimit)
                return false;

            foreach (var ordering in Enum.GetValues<Ordering>())
            {
                var order = CreateOrder(sizes, ordering);
                foreach (var heuristic in Enum.GetValues<Heuristic>())
                {
                    if (heuristic == Heuristic.ContactPoint && sizes.Count > ContactPointItemLimit)
                        continue;
                    if (TryMaxRects(sizes, order, atlasWidth, atlasHeight, heuristic, out positions))
                        return true;
                }
            }

            positions = [];
            return false;
        }

        /// <summary>Single-pass shelf packer in the given input order (legacy behaviour).</summary>
        public static bool TryShelfPack(
            IReadOnlyList<(int Width, int Height)> sizes,
            int atlasWidth,
            int atlasHeight,
            out (int X, int Y)[] positions)
        {
            positions = new (int X, int Y)[sizes.Count];
            var x = 0;
            var y = 0;
            var shelfHeight = 0;

            for (var i = 0; i < sizes.Count; i++)
            {
                var (width, height) = sizes[i];
                if (width > atlasWidth || height > atlasHeight)
                    return Fail(out positions);

                if (x + width > atlasWidth)
                {
                    y += shelfHeight;
                    x = 0;
                    shelfHeight = 0;
                }

                if (y + height > atlasHeight)
                    return Fail(out positions);

                positions[i] = (x, y);
                x += width;
                shelfHeight = Math.Max(shelfHeight, height);
            }

            return true;
        }

        private static bool Fail(out (int X, int Y)[] positions)
        {
            positions = [];
            return false;
        }

        private static int[] CreateOrder(IReadOnlyList<(int Width, int Height)> sizes, Ordering ordering)
        {
            var order = new int[sizes.Count];
            for (var i = 0; i < order.Length; i++)
                order[i] = i;

            long Key(int index)
            {
                var (w, h) = sizes[index];
                return ordering switch
                {
                    Ordering.MaxSideDescending => Math.Max(w, h),
                    Ordering.AreaDescending => (long)w * h,
                    Ordering.HeightDescending => h,
                    Ordering.WidthDescending => w,
                    _ => (long)w + h,
                };
            }

            // Secondary key is the long side then the input index, so ties are fully deterministic.
            Array.Sort(order, (a, b) =>
            {
                var c = Key(b).CompareTo(Key(a));
                if (c != 0)
                    return c;
                c = Math.Max(sizes[b].Width, sizes[b].Height).CompareTo(Math.Max(sizes[a].Width, sizes[a].Height));
                return c != 0 ? c : a.CompareTo(b);
            });
            return order;
        }

        private readonly record struct FreeRect(int X, int Y, int Width, int Height);

        private static bool TryMaxRects(
            IReadOnlyList<(int Width, int Height)> sizes,
            int[] order,
            int atlasWidth,
            int atlasHeight,
            Heuristic heuristic,
            out (int X, int Y)[] positions)
        {
            positions = new (int X, int Y)[sizes.Count];
            var free = new List<FreeRect> { new(0, 0, atlasWidth, atlasHeight) };
            var scratch = new List<FreeRect>();
            var placedRects = new List<FreeRect>();

            foreach (var index in order)
            {
                var (width, height) = sizes[index];
                var best = -1;
                long bestPrimary = long.MaxValue;
                long bestSecondary = long.MaxValue;

                for (var f = 0; f < free.Count; f++)
                {
                    var rect = free[f];
                    if (width > rect.Width || height > rect.Height)
                        continue;

                    var leftoverW = rect.Width - width;
                    var leftoverH = rect.Height - height;
                    long primary;
                    long secondary;
                    switch (heuristic)
                    {
                        case Heuristic.BestShortSideFit:
                            primary = Math.Min(leftoverW, leftoverH);
                            secondary = Math.Max(leftoverW, leftoverH);
                            break;
                        case Heuristic.BestAreaFit:
                            primary = (long)rect.Width * rect.Height - (long)width * height;
                            secondary = Math.Min(leftoverW, leftoverH);
                            break;
                        case Heuristic.BestLongSideFit:
                            primary = Math.Max(leftoverW, leftoverH);
                            secondary = Math.Min(leftoverW, leftoverH);
                            break;
                        case Heuristic.ContactPoint:
                            // Maximise touching perimeter (walls and placed rects) at the free
                            // rect's top-left corner; negate so lower is better.
                            primary = -ContactScore(placedRects, rect.X, rect.Y, width, height, atlasWidth, atlasHeight);
                            secondary = (long)rect.Y + height;
                            break;
                        default:
                            primary = (long)rect.Y + height;
                            secondary = rect.X;
                            break;
                    }

                    // Strict less-than keeps the first free rect in list order on ties; the list
                    // order itself is deterministic.
                    if (primary < bestPrimary || (primary == bestPrimary && secondary < bestSecondary))
                    {
                        best = f;
                        bestPrimary = primary;
                        bestSecondary = secondary;
                    }
                }

                if (best < 0)
                {
                    positions = [];
                    return false;
                }

                var placed = new FreeRect(free[best].X, free[best].Y, width, height);
                positions[index] = (placed.X, placed.Y);
                if (heuristic == Heuristic.ContactPoint)
                    placedRects.Add(placed);
                SplitAndPrune(free, scratch, placed);
            }

            return true;
        }

        private static long ContactScore(
            List<FreeRect> placed,
            int x,
            int y,
            int width,
            int height,
            int atlasWidth,
            int atlasHeight)
        {
            long score = 0;
            if (x == 0 || x + width == atlasWidth)
                score += height;
            if (y == 0 || y + height == atlasHeight)
                score += width;

            for (var i = 0; i < placed.Count; i++)
            {
                var other = placed[i];
                if (other.X == x + width || other.X + other.Width == x)
                    score += Overlap(y, y + height, other.Y, other.Y + other.Height);
                if (other.Y == y + height || other.Y + other.Height == y)
                    score += Overlap(x, x + width, other.X, other.X + other.Width);
            }

            return score;
        }

        private static int Overlap(int aStart, int aEnd, int bStart, int bEnd)
            => Math.Max(0, Math.Min(aEnd, bEnd) - Math.Max(aStart, bStart));

        private static void SplitAndPrune(List<FreeRect> free, List<FreeRect> scratch, FreeRect placed)
        {
            scratch.Clear();
            var placedRight = placed.X + placed.Width;
            var placedBottom = placed.Y + placed.Height;

            for (var i = 0; i < free.Count; i++)
            {
                var rect = free[i];
                var right = rect.X + rect.Width;
                var bottom = rect.Y + rect.Height;
                if (placed.X >= right || placedRight <= rect.X ||
                    placed.Y >= bottom || placedBottom <= rect.Y)
                {
                    scratch.Add(rect);
                    continue;
                }

                if (placed.X > rect.X)
                    scratch.Add(new FreeRect(rect.X, rect.Y, placed.X - rect.X, rect.Height));
                if (placedRight < right)
                    scratch.Add(new FreeRect(placedRight, rect.Y, right - placedRight, rect.Height));
                if (placed.Y > rect.Y)
                    scratch.Add(new FreeRect(rect.X, rect.Y, rect.Width, placed.Y - rect.Y));
                if (placedBottom < bottom)
                    scratch.Add(new FreeRect(rect.X, placedBottom, rect.Width, bottom - placedBottom));
            }

            // Remove free rects fully contained in another (identical rects: keep the first).
            free.Clear();
            for (var i = 0; i < scratch.Count; i++)
            {
                var a = scratch[i];
                var contained = false;
                for (var j = 0; j < scratch.Count && !contained; j++)
                {
                    if (i == j)
                        continue;
                    var b = scratch[j];
                    if (a.X >= b.X && a.Y >= b.Y &&
                        a.X + a.Width <= b.X + b.Width && a.Y + a.Height <= b.Y + b.Height)
                    {
                        contained = a != b || j < i;
                    }
                }

                if (!contained)
                    free.Add(a);
            }
        }
    }
}
