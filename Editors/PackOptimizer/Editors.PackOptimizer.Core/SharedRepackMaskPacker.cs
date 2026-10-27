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
        // Placement rules. FirstFit: the first legal place by row, then column. Contact: among the legal
        // places that add the least height (rows past the used bottom are never better), the one that
        // touches the most placed cells and canvas edges (the left and top walls count), so holes fill first.
        public const int FirstFit = 0;
        public const int Contact = 1;

        // Orders 0-5: mask area, height, longer side, bounding-box hole area, width, perimeter (each
        // largest first, ties by area, then index). Orders 6+ are seeded shuffles of order mode % 6.
        public const int OrderCount = 6;
        public const int ShuffleCount = 12;

        // Variants tried in this order: (rule, order). Cheap first fits first, so a spent budget loses the dearest.
        private static readonly (int Rule, int Order)[] Variants = BuildVariants();

        private static (int Rule, int Order)[] BuildVariants()
        {
            var variants = new List<(int, int)>();
            for (var order = 0; order < OrderCount + ShuffleCount; order++)
                variants.Add((FirstFit, order));
            variants.Add((Contact, 0));
            variants.Add((Contact, 1));
            return variants.ToArray();
        }

        internal static int VariantCount => Variants.Length;

        // Test hook: how many leading variants TryPackMinimumTrimmedCanvas tries (all by default).
        internal static int VariantLimit = int.MaxValue;

        private static int[] OrderOf(IReadOnlyList<SharedRepackMask> masks, int[] areas, int mode)
            => Enumerable.Range(0, masks.Count)
                .OrderByDescending(index => OrderKey(masks[index], areas[index], index, mode))
                .ThenByDescending(index => areas[index])
                .ThenBy(index => index)
                .ToArray();

        // Modes 0-5 are plain keys; a higher mode takes the plain key of mode % 6 scaled by a fixed
        // pseudo-random factor in [0.7, 1.3] drawn from (mode, index), so the order is a seeded shuffle of it.
        private static double OrderKey(SharedRepackMask mask, int area, int index, int mode)
        {
            double key = (mode % OrderCount) switch
            {
                1 => mask.Height,
                2 => Math.Max(mask.Width, mask.Height),
                3 => mask.Width * mask.Height - area,
                4 => mask.Width,
                5 => mask.Width + mask.Height,
                _ => area,
            };
            if (mode < OrderCount)
                return key;
            var hash = ((ulong)mode << 32 | (uint)index) * 0x9E3779B97F4A7C15UL;
            hash = (hash ^ (hash >> 30)) * 0xBF58476D1CE4E5B9UL;
            hash = (hash ^ (hash >> 27)) * 0x94D049BB133111EBUL;
            hash ^= hash >> 31;
            return key * (0.7 + 0.6 * ((hash >> 11) / (double)(1UL << 53)));
        }

        // Masks in one of the orders, each at the place its rule picks. Canvas and mask sizes are in
        // units. areaLimit (cells, 0 = none): gives up once the used box cannot get below it (the box is
        // at least as wide as the widest mask). False when a mask does not fit, the limit is hit or the budget ran out.
        public static bool TryPack(
            IReadOnlyList<SharedRepackMask> masks,
            int[] areas,
            int mode,
            int width,
            int height,
            SharedRepackSimBudget? budget,
            out (int X, int Y)[] positions,
            out int right,
            out int bottom,
            int rule = FirstFit,
            long areaLimit = 0)
        {
            positions = new (int X, int Y)[masks.Count];
            right = bottom = 0;
            var words = (width + 63) >> 6;
            var stride = words + 1;
            var canvas = new ulong[(height + 2) * stride];
            var widest = masks.Count == 0 ? 0 : masks.Max(mask => mask.Width);
            var shifted = new ulong[stride + 1];
            foreach (var index in OrderOf(masks, areas, mode))
            {
                var mask = masks[index];
                if (mask.Width > width || mask.Height > height)
                    return false;

                if (!FindPosition(canvas, stride, mask, width, height, bottom, rule, budget, shifted, out var x, out var y))
                    return false;

                Place(canvas, stride, mask, x, y);
                positions[index] = (x, y);
                right = Math.Max(right, x + mask.Width);
                bottom = Math.Max(bottom, y + mask.Height);
                if (areaLimit > 0 && (long)Math.Max(right, widest) * bottom >= areaLimit)
                    return false;
            }

            return true;
        }

        private static bool FindPosition(
            ulong[] canvas, int stride, SharedRepackMask mask, int width, int height, int bottom,
            int rule, SharedRepackSimBudget? budget, ulong[] shifted, out int bestX, out int bestY)
        {
            bestX = bestY = 0;
            var found = false;
            int bestBottom = 0, bestContact = -1;
            // A row at or below the used bottom is empty, so nothing past it is better than it.
            var lastRow = rule == FirstFit ? height - mask.Height : Math.Min(height - mask.Height, bottom);
            for (var y = 0; y <= lastRow; y++)
            {
                var newBottom = Math.Max(bottom, y + mask.Height);
                if (found && newBottom > bestBottom)
                    break;
                for (var x = 0; x + mask.Width <= width; x++)
                {
                    if (budget != null)
                    {
                        budget.Operations++;
                        if (budget.Exceeded)
                            return false;
                    }

                    if (Overlaps(canvas, stride, mask, x, y, budget))
                        continue;

                    if (rule == FirstFit)
                    {
                        bestX = x;
                        bestY = y;
                        return true;
                    }

                    var contact = ContactOf(canvas, stride, mask, x, y, shifted);
                    if (budget != null)
                        budget.Operations += mask.Height;
                    if (found && contact <= bestContact)
                        continue;
                    found = true;
                    bestBottom = newBottom;
                    bestContact = contact;
                    bestX = x;
                    bestY = y;
                }
            }

            return found;
        }

        // Edges the mask would share with placed cells; the left wall and the top wall count as placed.
        private static int ContactOf(ulong[] canvas, int stride, SharedRepackMask mask, int x, int y, ulong[] shifted)
        {
            var wordShift = x >> 6;
            var bitShift = x & 63;
            var contact = 0;
            for (var row = 0; row < mask.Height; row++)
            {
                var source = mask.Row(row);
                Array.Clear(shifted);
                for (var word = 0; word < source.Length; word++)
                {
                    var value = source[word];
                    shifted[wordShift + word] |= value << bitShift;
                    if (bitShift != 0)
                        shifted[wordShift + word + 1] |= value >> (64 - bitShift);
                }

                var offset = (y + row) * stride;
                var above = y + row - 1;
                for (var word = wordShift; word <= wordShift + source.Length && word < stride; word++)
                {
                    var cells = shifted[word];
                    if (cells == 0)
                        continue;
                    var here = canvas[offset + word];
                    var before = word == 0 ? 1UL : canvas[offset + word - 1] >> 63;
                    var after = word + 1 < stride ? canvas[offset + word + 1] << 63 : 0UL;
                    contact += System.Numerics.BitOperations.PopCount(cells & ((here << 1) | before));
                    contact += System.Numerics.BitOperations.PopCount(cells & ((here >> 1) | after));
                    contact += System.Numerics.BitOperations.PopCount(cells & (above < 0 ? ulong.MaxValue : canvas[above * stride + word]));
                    contact += System.Numerics.BitOperations.PopCount(cells & canvas[(y + row + 1) * stride + word]);
                }
            }

            return contact;
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
            var cellArea = (long)unit * unit;
            for (var variant = 0; variant < Math.Min(Variants.Length, VariantLimit); variant++)
            {
                var (rule, mode) = Variants[variant];
                foreach (var width in widths)
                {
                    if (budget is { Exceeded: true })
                        return found;

                    // A cut canvas is at least widest x used height, so it only gains below this height.
                    var cap = (int)Math.Min(gridHeight, (best - 1) / ((long)widest * cellArea));
                    if (cap <= 0 || total > (long)width * cap)
                        continue;
                    var limit = (best - 1) / cellArea + 1;
                    if (!TryPack(masks, areas, mode, width, cap, budget, out var trial, out var right, out var bottom, rule, limit))
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
