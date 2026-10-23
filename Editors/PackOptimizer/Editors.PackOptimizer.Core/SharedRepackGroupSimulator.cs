namespace Editors.PackOptimizer.Core
{
    // One layout group as the packing simulation sees it: chart rectangles (reference pixels,
    // multiples of Unit), the cluster each belongs to (rectangles of one cluster stay on one
    // page), and the constraints of the real planner.
    internal sealed class SharedRepackSimInput
    {
        public IReadOnlyList<(int Width, int Height)> Sizes { get; init; } = [];
        public IReadOnlyList<int> ClusterOf { get; init; } = [];
        public int Unit { get; init; } = 1;
        public int MaximumWidth { get; init; }
        public int MaximumHeight { get; init; }
        public Func<int, int, bool>? CanvasAllowed { get; init; }
        // Bytes of a page with this canvas (all slots of the group).
        public Func<int, int, long> PageBytes { get; init; } = (_, _) => 0;
        // Source bytes the real split decision compares against.
        public long SourceBytes { get; init; }
        public long MinimumSaving { get; init; }
        public int MinimumSide { get; init; } = 16;
        public int MaximumPages { get; init; } = 4;
        public int CandidatesPerLevel { get; init; } = 12;
        public int MaximumAttempts { get; init; } = 400;
    }

    internal sealed record SharedRepackSimPage(
        int Width,
        int Height,
        List<int> Indexes,
        (int X, int Y)[] Positions);

    internal sealed record SharedRepackSimOutcome(
        bool Ok,
        long Bytes,
        List<SharedRepackSimPage> Pages,
        bool SplitApplied,
        long SingleBytes,
        string? Failure)
    {
        public string Sizes => string.Join(" + ", Pages.Select(page => $"{page.Width}x{page.Height}"));
    }

    // Re-packs a group's rectangles under the real constraints (power-of-two canvases, the side
    // limit, the optional canvas predicate, whole clusters per page, up to MaximumPages pages)
    // with a chosen packer. Report only: nothing here touches planner state.
    internal static class SharedRepackGroupSimulator
    {
        public static SharedRepackSimOutcome Simulate(
            SharedRepackSimInput input,
            SharedRepackPackerKind kind,
            SharedRepackSimBudget budget)
        {
            var count = input.Sizes.Count;
            if (count == 0)
                return Failed("no rectangles");
            var solver = new SharedRepackLayoutSolver(kind, input.Unit, 1, budget);

            var clusters = Enumerable.Range(0, count)
                .GroupBy(index => input.ClusterOf[index])
                .Select(group => group.OrderBy(index => index).ToList())
                .OrderByDescending(cluster => cluster.Sum(index => Area(input, index)))
                .ThenBy(cluster => cluster[0])
                .ToList();
            var all = Enumerable.Range(0, count).ToList();
            List<List<int>> assignments;
            var split = false;
            long singleBytes = 0;
            if (TryPage(input, solver, all, input.MaximumWidth, input.MaximumHeight, out var single))
            {
                assignments = [all];
                singleBytes = input.PageBytes(single!.Width, single.Height);
                if (clusters.Count >= 2)
                {
                    var found = FindSplit(
                        input, solver, clusters, single.Width, single.Height, out var bestBytes, out _);
                    if (found != null)
                    {
                        var singleBuilds = input.SourceBytes - singleBytes >= input.MinimumSaving;
                        if (bestBytes < singleBytes &&
                            input.SourceBytes - bestBytes >= input.MinimumSaving &&
                            (!singleBuilds || bestBytes <= singleBytes - input.MinimumSaving))
                        {
                            split = true;
                            assignments = found;
                        }
                    }
                }
            }
            else
            {
                if (budget.Exceeded)
                    return Failed("operation budget exceeded");

                assignments = [];
                foreach (var cluster in clusters)
                {
                    var placed = false;
                    foreach (var page in assignments)
                    {
                        var trial = page.Concat(cluster).ToList();
                        if (trial.Sum(index => Area(input, index)) <= (long)input.MaximumWidth * input.MaximumHeight &&
                            TryPage(input, solver, trial, input.MaximumWidth, input.MaximumHeight, out _))
                        {
                            page.AddRange(cluster);
                            placed = true;
                            break;
                        }
                    }

                    if (placed)
                        continue;
                    if (!TryPage(input, solver, cluster, input.MaximumWidth, input.MaximumHeight, out _))
                        return Failed(budget.Exceeded ? "operation budget exceeded" : "a cluster does not fit");
                    assignments.Add(cluster.ToList());
                }
            }

            var pages = new List<SharedRepackSimPage>();
            long total = 0;
            foreach (var assignment in assignments.Select(page => page.OrderBy(index => index).ToList()))
            {
                if (!TryPage(input, solver, assignment, input.MaximumWidth, input.MaximumHeight, out var page))
                    return Failed(budget.Exceeded ? "operation budget exceeded" : "final page does not fit");
                pages.Add(page!);
                total += input.PageBytes(page!.Width, page.Height);
            }

            return new SharedRepackSimOutcome(true, total, pages, split, singleBytes, null);
        }

        private static SharedRepackSimOutcome Failed(string reason)
            => new(false, 0, [], false, 0, reason);

        private static long Area(SharedRepackSimInput input, int index)
            => (long)input.Sizes[index].Width * input.Sizes[index].Height;

        private static bool TryPage(
            SharedRepackSimInput input,
            SharedRepackLayoutSolver solver,
            List<int> indexes,
            int limitWidth,
            int limitHeight,
            out SharedRepackSimPage? page)
        {
            page = null;
            var ordered = indexes.OrderBy(index => index).ToList();
            if (!solver.TryPack(
                    ordered.Select(index => input.Sizes[index]).ToList(), limitWidth, limitHeight,
                    input.CanvasAllowed, out var width, out var height, out var positions))
            {
                return false;
            }

            page = new SharedRepackSimPage(width, height, ordered, positions);
            return true;
        }

        // The split search of the planner (FindSharedRepackSplitOnCanvases delegates here): the first
        // page's canvas is tried for a bounded set of candidates and filled with whole clusters,
        // largest first; the rest is recursed on. The shelf phase fills the way the planner always
        // did and picks by bytes, then page count, then sizes in text order. With the better packer
        // a second phase fills on MaxRects pages and only replaces the result when strictly smaller,
        // so ties keep the shelf result. Null when no split was found.
        internal static List<List<int>>? FindSplit(
            SharedRepackSimInput input,
            SharedRepackLayoutSolver solver,
            List<List<int>> clusters,
            int limitWidth,
            int limitHeight,
            out long bestBytes,
            out List<(int Width, int Height)> bestSizes)
        {
            List<List<int>>? best = null;
            var bestKey = string.Empty;
            long bestTotal = long.MaxValue;
            var foundSizes = new List<(int Width, int Height)>();
            var budget = solver.Budget;
            var phase = 0;
            var attempts = 0;
            var ordered = clusters
                .Select(cluster => cluster
                    .OrderByDescending(index => input.Sizes[index].Height)
                    .ThenByDescending(index => input.Sizes[index].Width)
                    .ThenBy(index => index)
                    .ToList())
                .OrderByDescending(cluster => cluster.Sum(index => Area(input, index)))
                .ThenBy(cluster => cluster.Min())
                .ToList();
            var gridUnit = solver.ResolveUnit(input.Sizes);

            bool PageOf(IEnumerable<List<int>> clusterSet, out long bytes, out (int Width, int Height) size)
            {
                bytes = 0;
                size = default;
                var indexes = clusterSet.SelectMany(cluster => cluster).OrderBy(index => index).ToList();
                if (!solver.TryPack(
                        indexes.Select(index => input.Sizes[index]).ToList(), limitWidth, limitHeight,
                        input.CanvasAllowed, out var width, out var height, out _))
                {
                    return false;
                }

                size = (width, height);
                bytes = input.PageBytes(width, height);
                return true;
            }

            void Search(
                List<List<int>> remaining,
                List<List<List<int>>> pages,
                List<(int Width, int Height)> pageSizes,
                long accumulated)
            {
                if (phase == 1 && budget.Exceeded)
                    return;
                if (phase == 0 ? accumulated > bestTotal : accumulated >= bestTotal)
                    return;
                if (!PageOf(remaining, out var restBytes, out var restSize))
                    return;

                if (pages.Count > 0)
                {
                    var total = accumulated + restBytes;
                    var allSizes = pageSizes.Append(restSize).ToList();
                    var key = string.Join(",", allSizes.Select(size => $"{size.Width}x{size.Height}"));
                    var count = pages.Count + 1;
                    var better = phase == 0
                        ? total < bestTotal ||
                          (total == bestTotal && (count < best!.Count ||
                              (count == best.Count && string.CompareOrdinal(key, bestKey) < 0)))
                        : total < bestTotal;
                    if (better)
                    {
                        bestTotal = total;
                        bestKey = key;
                        foundSizes = allSizes;
                        best = pages.Select(page => page.SelectMany(cluster => cluster).OrderBy(index => index).ToList())
                            .Append(remaining.SelectMany(cluster => cluster).OrderBy(index => index).ToList())
                            .ToList();
                    }
                }

                if (pages.Count + 2 > input.MaximumPages || remaining.Count < 2)
                    return;

                var restArea = (long)restSize.Width * restSize.Height;
                var candidates = new List<(int Width, int Height)>();
                for (var width = input.MinimumSide; width <= limitWidth; width <<= 1)
                {
                    for (var height = input.MinimumSide; height <= limitHeight; height <<= 1)
                    {
                        if ((long)width * height < restArea &&
                            (input.CanvasAllowed == null || input.CanvasAllowed(width, height)))
                        {
                            candidates.Add((width, height));
                        }
                    }
                }

                foreach (var candidate in candidates
                             .OrderByDescending(item => (long)item.Width * item.Height)
                             .ThenBy(item => Math.Abs(Log2(item.Width) - Log2(item.Height)))
                             .ThenByDescending(item => item.Width)
                             .Take(input.CandidatesPerLevel))
                {
                    if (++attempts > input.MaximumAttempts || (phase == 1 && budget.Exceeded))
                        return;

                    var chosen = new List<List<int>>();
                    var rest = new List<List<int>>();
                    if (phase == 0)
                        FillShelf(input, remaining, candidate, chosen, rest);
                    else
                        FillMaxRects(input, gridUnit, remaining, candidate, budget, chosen, rest);

                    if (chosen.Count == 0 || rest.Count == 0 || !PageOf(chosen, out var chosenBytes, out var chosenSize))
                        continue;

                    Search(
                        rest,
                        pages.Append(chosen).ToList(),
                        pageSizes.Append(chosenSize).ToList(),
                        accumulated + chosenBytes);
                }
            }

            Search(ordered, [], [], 0);
            if (solver.Kind == SharedRepackPackerKind.Best)
            {
                phase = 1;
                attempts = 0;
                Search(ordered, [], [], 0);
            }

            bestBytes = bestTotal == long.MaxValue ? 0 : bestTotal;
            bestSizes = foundSizes;
            return best;
        }

        // The planner's greedy: whole clusters, largest first, on rows of the candidate's size.
        private static void FillShelf(
            SharedRepackSimInput input,
            List<List<int>> remaining,
            (int Width, int Height) candidate,
            List<List<int>> chosen,
            List<List<int>> rest)
        {
            int x = 0, y = 0, rowHeight = 0;
            foreach (var cluster in remaining)
            {
                int saveX = x, saveY = y, saveRow = rowHeight;
                var fits = true;
                foreach (var index in cluster)
                {
                    var (width, height) = input.Sizes[index];
                    if (width > candidate.Width || height > candidate.Height)
                    {
                        fits = false;
                        break;
                    }

                    if (x + width > candidate.Width)
                    {
                        x = 0;
                        y += rowHeight;
                        rowHeight = 0;
                    }

                    if (y + height > candidate.Height)
                    {
                        fits = false;
                        break;
                    }

                    x += width;
                    rowHeight = Math.Max(rowHeight, height);
                }

                if (fits)
                {
                    chosen.Add(cluster);
                }
                else
                {
                    (x, y, rowHeight) = (saveX, saveY, saveRow);
                    rest.Add(cluster);
                }
            }
        }

        // Same, on an incremental MaxRects page: a cluster that does not fit whole is rolled back.
        private static void FillMaxRects(
            SharedRepackSimInput input,
            int unit,
            List<List<int>> remaining,
            (int Width, int Height) candidate,
            SharedRepackSimBudget budget,
            List<List<int>> chosen,
            List<List<int>> rest)
        {
            var state = new SharedRepackRectPacker.MaxRectsState(candidate.Width / unit, candidate.Height / unit, false, budget);
            foreach (var cluster in remaining)
            {
                var snapshot = state.Clone();
                var fits = true;
                foreach (var index in cluster)
                {
                    var (width, height) = input.Sizes[index];
                    if (!state.TryPlace(width / unit, height / unit, out _, out _))
                    {
                        fits = false;
                        break;
                    }
                }

                if (fits)
                {
                    chosen.Add(cluster);
                }
                else
                {
                    state.Restore(snapshot);
                    rest.Add(cluster);
                }
            }
        }

        private static int Log2(int value) => System.Numerics.BitOperations.Log2((uint)value);
    }
}
