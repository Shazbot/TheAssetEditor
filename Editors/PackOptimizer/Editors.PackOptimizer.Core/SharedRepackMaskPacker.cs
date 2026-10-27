namespace Editors.PackOptimizer.Core
{
    // A chart's occupancy in packing units (row-major bits): the cells it needs, not its rectangle.
    internal sealed class SharedRepackMask
    {
        private readonly ulong[] bits;

        public SharedRepackMask(int width, int height)
        {
            Width = width;
            Height = height;
            Words = (width + 63) >> 6;
            bits = new ulong[Words * height];
        }

        public int Width { get; }
        public int Height { get; }
        public int Words { get; }

        public bool Get(int x, int y) => (bits[y * Words + (x >> 6)] >> (x & 63) & 1) != 0;

        public void Set(int x, int y) => bits[y * Words + (x >> 6)] |= 1UL << (x & 63);

        // Sets x in [left, right) of one row (clamped to the mask).
        public void SetRange(int y, int left, int right)
        {
            left = Math.Max(0, left);
            right = Math.Min(Width, right);
            for (var x = left; x < right;)
            {
                var word = x >> 6;
                var end = Math.Min(right, (word + 1) << 6);
                var count = end - x;
                var run = count == 64 ? ulong.MaxValue : ((1UL << count) - 1) << (x & 63);
                bits[y * Words + word] |= run;
                x = end;
            }
        }

        public int Area()
        {
            var total = 0;
            foreach (var word in bits)
                total += System.Numerics.BitOperations.PopCount(word);
            return total;
        }

        public ReadOnlySpan<ulong> Row(int y) => new(bits, y * Words, Words);

        // A stable digest of the shape (size and cells), for output signatures.
        public ulong Hash()
        {
            var hash = 14695981039346656037UL;
            hash = (hash ^ (ulong)Width) * 1099511628211UL;
            hash = (hash ^ (ulong)Height) * 1099511628211UL;
            foreach (var word in bits)
                hash = (hash ^ word) * 1099511628211UL;
            return hash;
        }

        // The cells as disjoint rectangles (x, y, width, height in cells): each row's runs, a run that
        // repeats the previous row's run extends it. Rows then columns, so the order is deterministic.
        public List<(int X, int Y, int Width, int Height)> ToRectangles()
        {
            var result = new List<(int X, int Y, int Width, int Height)>();
            var open = new Dictionary<(int X, int Width), int>();
            for (var y = 0; y < Height; y++)
            {
                var next = new Dictionary<(int X, int Width), int>();
                for (var x = 0; x < Width;)
                {
                    if (!Get(x, y))
                    {
                        x++;
                        continue;
                    }

                    var start = x;
                    while (x < Width && Get(x, y))
                        x++;
                    var key = (start, x - start);
                    if (open.TryGetValue(key, out var index))
                    {
                        var rectangle = result[index];
                        result[index] = (rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height + 1);
                        next[key] = index;
                    }
                    else
                    {
                        result.Add((start, y, x - start, 1));
                        next[key] = result.Count - 1;
                    }
                }

                open = next;
            }

            return result;
        }

        public static SharedRepackMask Filled(int width, int height)
        {
            var mask = new SharedRepackMask(width, height);
            for (var y = 0; y < height; y++)
                mask.SetRange(y, 0, width);
            return mask;
        }
    }

    // Greedy packer for chart masks (report-only simulation of shape-aware packing). Pure: positions
    // are multiples of the unit, the masks never overlap, and the work is bounded by a budget.
    internal static class SharedRepackMaskPacker
    {
        // Masks in one of three orders (largest first, ties by index), each at the first legal place by row, then column.
        // Canvas and mask sizes are in units. False when a mask does not fit or the budget ran out.
        public static bool TryPack(
            IReadOnlyList<SharedRepackMask> masks,
            int[] areas,
            int mode,
            int width,
            int height,
            SharedRepackSimBudget? budget,
            out (int X, int Y)[] positions,
            out int right,
            out int bottom)
        {
            positions = new (int X, int Y)[masks.Count];
            right = bottom = 0;
            var words = (width + 63) >> 6;
            var canvas = new ulong[(height + 1) * (words + 1)];
            var stride = words + 1;
            // Mode 0: by mask area; 1: by height; 2: by the longer side (ties by area, then index).
            var order = Enumerable.Range(0, masks.Count)
                .OrderByDescending(index => mode switch
                {
                    1 => masks[index].Height,
                    2 => Math.Max(masks[index].Width, masks[index].Height),
                    _ => areas[index],
                })
                .ThenByDescending(index => areas[index])
                .ThenBy(index => index)
                .ToArray();
            foreach (var index in order)
            {
                var mask = masks[index];
                if (mask.Width > width || mask.Height > height)
                    return false;

                var placed = false;
                for (var y = 0; y + mask.Height <= height && !placed; y++)
                {
                    for (var x = 0; x + mask.Width <= width; x++)
                    {
                        if (budget != null)
                        {
                            budget.Operations++;
                            if (budget.Exceeded)
                                return false;
                        }

                        if (!Overlaps(canvas, stride, mask, x, y, budget))
                        {
                            Place(canvas, stride, mask, x, y);
                            positions[index] = (x, y);
                            right = Math.Max(right, x + mask.Width);
                            bottom = Math.Max(bottom, y + mask.Height);
                            placed = true;
                            break;
                        }
                    }
                }

                if (!placed)
                    return false;
            }

            return true;
        }

        private static bool Overlaps(ulong[] canvas, int stride, SharedRepackMask mask, int x, int y, SharedRepackSimBudget? budget)
        {
            var wordShift = x >> 6;
            var bitShift = x & 63;
            for (var row = 0; row < mask.Height; row++)
            {
                var source = mask.Row(row);
                var offset = (y + row) * stride + wordShift;
                for (var word = 0; word < source.Length; word++)
                {
                    var value = source[word];
                    if (value == 0)
                        continue;
                    if ((canvas[offset + word] & (value << bitShift)) != 0)
                        return true;
                    if (bitShift != 0 && (canvas[offset + word + 1] & (value >> (64 - bitShift))) != 0)
                        return true;
                }

                if (budget != null)
                    budget.Operations++;
            }

            return false;
        }

        private static void Place(ulong[] canvas, int stride, SharedRepackMask mask, int x, int y)
        {
            var wordShift = x >> 6;
            var bitShift = x & 63;
            for (var row = 0; row < mask.Height; row++)
            {
                var source = mask.Row(row);
                var offset = (y + row) * stride + wordShift;
                for (var word = 0; word < source.Length; word++)
                {
                    var value = source[word];
                    canvas[offset + word] |= value << bitShift;
                    if (bitShift != 0)
                        canvas[offset + word + 1] |= value >> (64 - bitShift);
                }
            }
        }

        // The smallest cut canvas over the same canvas widths TryPackMinimumTrimmedCanvas tries (the
        // given canvas and up to maximumWidths evenly spread from the widest mask), through the same
        // trim rules. Only canvases with an area below areaLimit count (pass the page's current area
        // to look for a gain). Masks, canvas and positions: the canvas and the positions in pixels
        // (multiples of the unit), the masks in units.
        public static bool TryPackMinimumTrimmedCanvas(
            IReadOnlyList<SharedRepackMask> masks,
            int unit,
            int canvasWidth,
            int canvasHeight,
            Func<int, int, (int Width, int Height)?> trim,
            int maximumWidths,
            long areaLimit,
            SharedRepackSimBudget? budget,
            out int trimmedWidth,
            out int trimmedHeight,
            out (int X, int Y)[] positions)
        {
            trimmedWidth = trimmedHeight = 0;
            positions = [];
            if (masks.Count == 0 || unit <= 0 || canvasWidth % unit != 0 || canvasHeight % unit != 0)
                return false;

            var gridHeight = canvasHeight / unit;
            var gridWidth = canvasWidth / unit;
            var widest = masks.Max(mask => mask.Width);
            if (widest > gridWidth || masks.Max(mask => mask.Height) > gridHeight)
                return false;

            var areas = masks.Select(mask => mask.Area()).ToArray();
            var widths = new SortedSet<int> { gridWidth };
            var span = gridWidth - widest;
            var steps = Math.Max(1, Math.Min(span, maximumWidths - 1));
            for (var step = 0; step <= steps; step++)
                widths.Add(widest + (int)((long)span * step / steps));

            var best = areaLimit;
            var found = false;
            long total = areas.Sum(area => (long)area);
            foreach (var width in widths)
            {
                if (budget is { Exceeded: true })
                    break;

                // A cut canvas is at least widest x used height, so it only gains below this height.
                var cap = (int)Math.Min(gridHeight, (best - 1) / ((long)widest * unit * unit));
                if (cap <= 0 || total > (long)width * cap)
                    continue;
                for (var mode = 0; mode < 3; mode++)
                {
                    cap = (int)Math.Min(gridHeight, (best - 1) / ((long)widest * unit * unit));
                    if (cap <= 0 || total > (long)width * cap || budget is { Exceeded: true })
                        break;
                    if (!TryPack(masks, areas, mode, width, cap, budget, out var trial, out var right, out var bottom))
                        continue;
                    if (trim(right * unit, bottom * unit) is not { } canvas)
                        continue;
                    var area = (long)canvas.Width * canvas.Height;
                    if (area >= best)
                        continue;

                    best = area;
                    found = true;
                    trimmedWidth = canvas.Width;
                    trimmedHeight = canvas.Height;
                    positions = trial.Select(position => (position.X * unit, position.Y * unit)).ToArray();
                }
            }

            return found;
        }
    }
}
