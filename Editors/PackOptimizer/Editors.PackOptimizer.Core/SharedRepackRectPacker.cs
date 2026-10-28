namespace Editors.PackOptimizer.Core
{
    // Counts the work of the report-only packing simulation so one group cannot run away: every
    // free-rectangle or skyline-segment evaluation costs one operation. Deterministic (no clock).
    internal sealed class SharedRepackSimBudget
    {
        public SharedRepackSimBudget(long maximumOperations) => MaximumOperations = maximumOperations;
        public long MaximumOperations { get; }
        public long Operations { get; set; }
        public bool Exceeded => Operations > MaximumOperations;
    }

    internal enum SharedRepackPackerKind
    {
        // Rows, tallest first (the planner's original packer).
        Shelf,
        // The planner's packer: the shelf result when it fits the canvas (so unchanged layouts stay
        // byte-identical), else the first success of MaxRects (best short side fit, best area fit)
        // and a skyline packer over several orders (height, area, longest side). A canvas is only
        // taken when it is smaller than every canvas the shelf alone would have used.
        Best,
    }

    // Packs rectangle sets onto the smallest power-of-two canvas for one layout group and remembers
    // the answers (sets of the same sizes recur many times in the split search). One budget is shared
    // by everything the solver does; once it is spent only the shelf result is available, and an
    // answer already found stays valid, so the outcome is a pure function of the call sequence.
    internal sealed class SharedRepackLayoutSolver
    {
        private readonly record struct Entry(int Width, int Height, (int X, int Y)[] Positions);

        private readonly Dictionary<string, Entry> found = new(StringComparer.Ordinal);
        private readonly HashSet<string> failed = new(StringComparer.Ordinal);

        // unit: the alignment of the rectangles (0: the largest power of two dividing them all);
        // minimumSide: the smallest canvas side tried.
        public SharedRepackLayoutSolver(SharedRepackPackerKind kind, int unit, int minimumSide, SharedRepackSimBudget budget)
        {
            Kind = kind;
            Unit = unit;
            MinimumSide = minimumSide;
            Budget = budget;
        }

        public SharedRepackPackerKind Kind { get; }
        public int Unit { get; }
        public int MinimumSide { get; }
        public SharedRepackSimBudget Budget { get; }

        // The grid positions are snapped to: the given unit when every size is a multiple of it.
        public int ResolveUnit(IReadOnlyList<(int Width, int Height)> sizes)
            => Unit > 0 && sizes.All(size => size.Width % Unit == 0 && size.Height % Unit == 0)
                ? Unit
                : SharedRepackRectPacker.PowerOfTwoDivisor(sizes);

        public bool TryPack(
            IReadOnlyList<(int Width, int Height)> sizes,
            int maximumWidth,
            int maximumHeight,
            Func<int, int, bool>? canvasAllowed,
            out int canvasWidth,
            out int canvasHeight,
            out (int X, int Y)[] positions)
        {
            canvasWidth = canvasHeight = 0;
            positions = [];
            if (sizes.Count == 0)
                return false;

            var key = new System.Text.StringBuilder(sizes.Count * 8 + 2);
            key.Append(canvasAllowed == null ? 'n' : 'a');
            foreach (var (width, height) in sizes)
                key.Append(width).Append('x').Append(height).Append(';');
            var text = key.ToString();
            if (found.TryGetValue(text, out var entry))
            {
                if (entry.Width > maximumWidth || entry.Height > maximumHeight)
                    return false;
                canvasWidth = entry.Width;
                canvasHeight = entry.Height;
                positions = entry.Positions;
                return true;
            }

            var limits = text + "|" + maximumWidth + "x" + maximumHeight;
            if (failed.Contains(limits))
                return false;

            var unit = ResolveUnit(sizes);
            if (!SharedRepackRectPacker.TryPackCanvases(
                    sizes, unit, maximumWidth, maximumHeight, canvasAllowed, MinimumSide, Kind, Budget,
                    out canvasWidth, out canvasHeight, out positions))
            {
                // A failure while the budget is spent may be an artefact of it; the shelf is
                // still exact then, so the failure is real. Remember it for these limits.
                failed.Add(limits);
                return false;
            }

            found[text] = new Entry(canvasWidth, canvasHeight, positions);
            return true;
        }
    }

    // Axis-aligned rectangle packers (no rotation) for the report-only packing simulation.
    // Sizes and canvases are in pixels and multiples of the given unit; the packers work on the
    // unit grid, so every position is a multiple of the unit as well.
    internal static class SharedRepackRectPacker
    {
        private readonly record struct Free(int X, int Y, int W, int H);

        public static int[] OrderBy(IReadOnlyList<(int Width, int Height)> sizes, int mode)
        {
            var indexes = Enumerable.Range(0, sizes.Count);
            return mode switch
            {
                1 => indexes
                    .OrderByDescending(index => (long)sizes[index].Width * sizes[index].Height)
                    .ThenByDescending(index => sizes[index].Height)
                    .ThenBy(index => index).ToArray(),
                2 => indexes
                    .OrderByDescending(index => Math.Max(sizes[index].Width, sizes[index].Height))
                    .ThenByDescending(index => (long)sizes[index].Width * sizes[index].Height)
                    .ThenBy(index => index).ToArray(),
                _ => indexes
                    .OrderByDescending(index => sizes[index].Height)
                    .ThenByDescending(index => sizes[index].Width)
                    .ThenBy(index => index).ToArray(),
            };
        }

        // Rows, in the given order (the planner's TryShelfPack).
        public static bool TryShelf(
            IReadOnlyList<(int Width, int Height)> sizes,
            int[] order,
            int canvasWidth,
            int canvasHeight,
            out (int X, int Y)[] positions)
        {
            positions = new (int X, int Y)[sizes.Count];
            int x = 0, y = 0, rowHeight = 0;
            foreach (var index in order)
            {
                var (width, height) = sizes[index];
                if (width > canvasWidth || height > canvasHeight)
                    return false;
                if (x + width > canvasWidth)
                {
                    x = 0;
                    y += rowHeight;
                    rowHeight = 0;
                }

                if (y + height > canvasHeight)
                    return false;
                positions[index] = (x, y);
                x += width;
                rowHeight = Math.Max(rowHeight, height);
            }

            return true;
        }

        // MaxRects: free rectangles, split on every placement, contained ones pruned.
        public static bool TryMaxRects(
            IReadOnlyList<(int Width, int Height)> sizes,
            int[] order,
            int canvasWidth,
            int canvasHeight,
            bool areaFit,
            SharedRepackSimBudget? budget,
            out (int X, int Y)[] positions)
        {
            positions = new (int X, int Y)[sizes.Count];
            var state = new MaxRectsState(canvasWidth, canvasHeight, areaFit, budget);
            foreach (var index in order)
            {
                if (!state.TryPlace(sizes[index].Width, sizes[index].Height, out var x, out var y))
                    return false;
                positions[index] = (x, y);
            }

            return true;
        }

        // Skyline, bottom-left: each rectangle goes where its top edge ends lowest.
        public static bool TrySkyline(
            IReadOnlyList<(int Width, int Height)> sizes,
            int[] order,
            int canvasWidth,
            int canvasHeight,
            SharedRepackSimBudget? budget,
            out (int X, int Y)[] positions)
        {
            positions = new (int X, int Y)[sizes.Count];
            var line = new List<(int X, int Y, int W)> { (0, 0, canvasWidth) };
            foreach (var index in order)
            {
                var (width, height) = sizes[index];
                if (width > canvasWidth || height > canvasHeight)
                    return false;

                var bestTop = int.MaxValue;
                var bestX = -1;
                var bestY = 0;
                var bestSegment = -1;
                for (var segment = 0; segment < line.Count; segment++)
                {
                    if (budget != null && ++budget.Operations > budget.MaximumOperations)
                        return false;

                    var x = line[segment].X;
                    if (x + width > canvasWidth)
                        break;

                    var y = 0;
                    var covered = 0;
                    for (var next = segment; covered < width; next++)
                    {
                        y = Math.Max(y, line[next].Y);
                        covered += line[next].W;
                    }

                    if (y + height > canvasHeight)
                        continue;
                    if (y + height < bestTop || (y + height == bestTop && x < bestX))
                    {
                        bestTop = y + height;
                        bestX = x;
                        bestY = y;
                        bestSegment = segment;
                    }
                }

                if (bestSegment < 0)
                    return false;

                positions[index] = (bestX, bestY);
                // Raise the skyline over [bestX, bestX + width).
                var rebuilt = new List<(int X, int Y, int W)>(line.Count + 2);
                foreach (var (sx, sy, sw) in line)
                {
                    var end = sx + sw;
                    if (end <= bestX || sx >= bestX + width)
                    {
                        rebuilt.Add((sx, sy, sw));
                        continue;
                    }

                    if (sx < bestX)
                        rebuilt.Add((sx, sy, bestX - sx));
                    if (end > bestX + width)
                        rebuilt.Add((bestX + width, sy, end - (bestX + width)));
                }

                rebuilt.Add((bestX, bestY + height, width));
                rebuilt.Sort((left, right) => left.X.CompareTo(right.X));
                line.Clear();
                foreach (var segment in rebuilt)
                {
                    if (line.Count != 0 && line[^1].Y == segment.Y)
                        line[^1] = (line[^1].X, line[^1].Y, line[^1].W + segment.W);
                    else
                        line.Add(segment);
                }
            }

            return true;
        }

        // First success of the full packer set on a fixed canvas (unit grid).
        public static bool TryBest(
            IReadOnlyList<(int Width, int Height)> sizes,
            int canvasWidth,
            int canvasHeight,
            SharedRepackSimBudget? budget,
            out (int X, int Y)[] positions,
            int[][]? orderCache = null)
        {
            // The orders depend on the sizes only; a caller trying several canvases passes one cache.
            orderCache ??= new int[3][];
            positions = [];
            if (budget is { Exceeded: true })
                return false;

            long area = 0;
            foreach (var (width, height) in sizes)
            {
                if (width > canvasWidth || height > canvasHeight)
                    return false;
                area += (long)width * height;
            }

            if (area > (long)canvasWidth * canvasHeight)
                return false;

            for (var mode = 0; mode < 3; mode++)
            {
                var order = orderCache[mode] ??= OrderBy(sizes, mode);
                if (TryMaxRects(sizes, order, canvasWidth, canvasHeight, false, budget, out positions) ||
                    TryMaxRects(sizes, order, canvasWidth, canvasHeight, true, budget, out positions) ||
                    TrySkyline(sizes, order, canvasWidth, canvasHeight, budget, out positions))
                {
                    return true;
                }

                if (budget is { Exceeded: true })
                    break;
            }

            positions = [];
            return false;
        }

        // Smallest power-of-two canvas (area, then squarest, then wider) the packer fits all
        // rectangles into; sizes are multiples of the unit. canvasAllowed restricts the canvases
        // (null: any); minimumSide is the smallest side tried.
        public static bool TryPackCanvases(
            IReadOnlyList<(int Width, int Height)> sizes,
            int unit,
            int maximumWidth,
            int maximumHeight,
            Func<int, int, bool>? canvasAllowed,
            int minimumSide,
            SharedRepackPackerKind kind,
            SharedRepackSimBudget? budget,
            out int canvasWidth,
            out int canvasHeight,
            out (int X, int Y)[] positions)
        {
            canvasWidth = canvasHeight = 0;
            positions = [];
            if (sizes.Count == 0)
                return false;

            long area = sizes.Sum(size => (long)size.Width * size.Height);
            var minimumWidth = Math.Max(minimumSide, NextPowerOfTwo(sizes.Max(size => size.Width)));
            var minimumHeight = Math.Max(minimumSide, NextPowerOfTwo(sizes.Max(size => size.Height)));
            var candidates = new List<(int Width, int Height)>();
            for (var width = minimumWidth; width <= maximumWidth; width <<= 1)
            {
                for (var height = minimumHeight; height <= maximumHeight; height <<= 1)
                {
                    if ((long)width * height >= area && width % unit == 0 && height % unit == 0 &&
                        (canvasAllowed == null || canvasAllowed(width, height)))
                    {
                        candidates.Add((width, height));
                    }
                }
            }

            var scaled = sizes.Select(size => (size.Width / unit, size.Height / unit)).ToList();
            var order = OrderBy(scaled, 0);
            var bestOrders = new int[3][];
            bestOrders[0] = order;
            foreach (var candidate in candidates
                         .OrderBy(item => (long)item.Width * item.Height)
                         .ThenBy(item => Math.Abs(Log2(item.Width) - Log2(item.Height)))
                         .ThenByDescending(item => item.Width))
            {
                // The shelf result wins a tie on the canvas; when the budget is spent only it remains.
                var fits = TryShelf(scaled, order, candidate.Width / unit, candidate.Height / unit, out positions);
                if (!fits && kind == SharedRepackPackerKind.Best && budget is not { Exceeded: true })
                    fits = TryBest(scaled, candidate.Width / unit, candidate.Height / unit, budget, out positions, bestOrders);
                if (fits)
                {
                    canvasWidth = candidate.Width;
                    canvasHeight = candidate.Height;
                    for (var index = 0; index < positions.Length; index++)
                        positions[index] = (positions[index].X * unit, positions[index].Y * unit);
                    return true;
                }
            }

            positions = [];
            return false;
        }

        // The layout whose trimmed canvas is smallest: every packer on canvases as tall as the given
        // one and from the widest rectangle up to its width (at most maximumWidths widths, evenly spread).
        // trim maps the used box (right, bottom) to the final canvas, null when it has none. Sizes,
        // canvas and positions are in pixels and multiples of the unit. False when nothing fits.
        public static bool TryPackMinimumTrimmedCanvas(
            IReadOnlyList<(int Width, int Height)> sizes,
            int unit,
            int canvasWidth,
            int canvasHeight,
            Func<int, int, (int Width, int Height)?> trim,
            int maximumWidths,
            SharedRepackSimBudget? budget,
            out int trimmedWidth,
            out int trimmedHeight,
            out (int X, int Y)[] positions)
        {
            trimmedWidth = trimmedHeight = 0;
            positions = [];
            if (sizes.Count == 0 || unit <= 0 || canvasWidth % unit != 0 || canvasHeight % unit != 0 ||
                sizes.Any(size => size.Width % unit != 0 || size.Height % unit != 0))
            {
                return false;
            }

            var scaled = sizes.Select(size => (size.Width / unit, size.Height / unit)).ToList();
            var gridHeight = canvasHeight / unit;
            var widest = scaled.Max(size => size.Item1);
            var gridWidth = canvasWidth / unit;
            if (widest > gridWidth || scaled.Max(size => size.Item2) > gridHeight)
                return false;

            var widths = new SortedSet<int> { gridWidth };
            var span = gridWidth - widest;
            var steps = Math.Max(1, Math.Min(span, maximumWidths - 1));
            for (var step = 0; step <= steps; step++)
                widths.Add(widest + (int)((long)span * step / steps));

            long bestArea = long.MaxValue;
            var orders = Enumerable.Range(0, 3).Select(mode => OrderBy(scaled, mode)).ToArray();
            foreach (var width in widths)
            {
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    if (budget is { Exceeded: true })
                        break;

                    var order = orders[attempt == 0 ? 0 : (attempt - 1) / 3];
                    (int X, int Y)[] trial;
                    var fits = attempt == 0
                        ? TryShelf(scaled, order, width, gridHeight, out trial)
                        : ((attempt - 1) % 3) switch
                        {
                            0 => TryMaxRects(scaled, order, width, gridHeight, false, budget, out trial),
                            1 => TryMaxRects(scaled, order, width, gridHeight, true, budget, out trial),
                            _ => TrySkyline(scaled, order, width, gridHeight, budget, out trial),
                        };
                    if (!fits)
                        continue;

                    int right = 0, bottom = 0;
                    for (var index = 0; index < trial.Length; index++)
                    {
                        right = Math.Max(right, trial[index].X + scaled[index].Item1);
                        bottom = Math.Max(bottom, trial[index].Y + scaled[index].Item2);
                    }

                    if (trim(right * unit, bottom * unit) is not { } canvas)
                        continue;
                    var area = (long)canvas.Width * canvas.Height;
                    if (area >= bestArea)
                        continue;

                    bestArea = area;
                    trimmedWidth = canvas.Width;
                    trimmedHeight = canvas.Height;
                    positions = trial.Select(position => (position.X * unit, position.Y * unit)).ToArray();
                }
            }

            return bestArea != long.MaxValue;
        }

        private static int Log2(int value) => System.Numerics.BitOperations.Log2((uint)value);

        // Largest power of two dividing every size (at least 1).
        public static int PowerOfTwoDivisor(IReadOnlyList<(int Width, int Height)> sizes)
        {
            var bits = 0;
            foreach (var (width, height) in sizes)
                bits |= width | height;
            return bits == 0 ? 1 : (int)(bits & (uint)-bits);
        }

        private static int NextPowerOfTwo(int value)
        {
            var result = 1;
            while (result < value)
                result <<= 1;
            return result;
        }

        // Incremental MaxRects page, cloneable so a whole cluster can be tried and rolled back.
        internal sealed class MaxRectsState
        {
            private List<Free> free;
            private readonly bool areaFit;
            private readonly SharedRepackSimBudget? budget;

            public MaxRectsState(int width, int height, bool areaFit, SharedRepackSimBudget? budget)
            {
                free = [new Free(0, 0, width, height)];
                this.areaFit = areaFit;
                this.budget = budget;
            }

            private MaxRectsState(List<Free> free, bool areaFit, SharedRepackSimBudget? budget)
            {
                this.free = free;
                this.areaFit = areaFit;
                this.budget = budget;
            }

            public MaxRectsState Clone() => new(new List<Free>(free), areaFit, budget);

            public void Restore(MaxRectsState other) => free = new List<Free>(other.free);

            public bool TryPlace(int width, int height, out int x, out int y)
            {
                x = y = 0;
                var found = -1;
                long bestPrimary = long.MaxValue, bestSecondary = long.MaxValue;
                for (var index = 0; index < free.Count; index++)
                {
                    if (budget != null && ++budget.Operations > budget.MaximumOperations)
                        return false;

                    var rect = free[index];
                    if (rect.W < width || rect.H < height)
                        continue;

                    var leftoverW = rect.W - width;
                    var leftoverH = rect.H - height;
                    long primary, secondary;
                    if (areaFit)
                    {
                        primary = (long)rect.W * rect.H - (long)width * height;
                        secondary = Math.Min(leftoverW, leftoverH);
                    }
                    else
                    {
                        primary = Math.Min(leftoverW, leftoverH);
                        secondary = Math.Max(leftoverW, leftoverH);
                    }

                    if (found < 0 || primary < bestPrimary ||
                        (primary == bestPrimary && (secondary < bestSecondary ||
                            (secondary == bestSecondary && (rect.Y < free[found].Y ||
                                (rect.Y == free[found].Y && rect.X < free[found].X))))))
                    {
                        found = index;
                        bestPrimary = primary;
                        bestSecondary = secondary;
                    }
                }

                if (found < 0)
                    return false;

                x = free[found].X;
                y = free[found].Y;
                var added = new List<Free>();
                for (var index = free.Count - 1; index >= 0; index--)
                {
                    var rect = free[index];
                    if (x >= rect.X + rect.W || x + width <= rect.X || y >= rect.Y + rect.H || y + height <= rect.Y)
                        continue;

                    if (x > rect.X)
                        added.Add(new Free(rect.X, rect.Y, x - rect.X, rect.H));
                    if (x + width < rect.X + rect.W)
                        added.Add(new Free(x + width, rect.Y, rect.X + rect.W - (x + width), rect.H));
                    if (y > rect.Y)
                        added.Add(new Free(rect.X, rect.Y, rect.W, y - rect.Y));
                    if (y + height < rect.Y + rect.H)
                        added.Add(new Free(rect.X, y + height, rect.W, rect.Y + rect.H - (y + height)));
                    free.RemoveAt(index);
                }

                foreach (var item in added)
                {
                    if (budget != null)
                        budget.Operations += free.Count;
                    var contained = false;
                    foreach (var other in free)
                    {
                        if (item.X >= other.X && item.Y >= other.Y &&
                            item.X + item.W <= other.X + other.W && item.Y + item.H <= other.Y + other.H)
                        {
                            contained = true;
                            break;
                        }
                    }

                    if (contained)
                        continue;

                    free.RemoveAll(other => other.X >= item.X && other.Y >= item.Y &&
                                            other.X + other.W <= item.X + item.W &&
                                            other.Y + other.H <= item.Y + item.H);
                    free.Add(item);
                }

                return true;
            }
        }
    }
}
