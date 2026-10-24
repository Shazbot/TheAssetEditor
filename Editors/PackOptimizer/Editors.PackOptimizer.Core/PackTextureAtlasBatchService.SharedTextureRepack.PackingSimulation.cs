using System.Diagnostics;
using System.Text;

namespace Editors.PackOptimizer.Core
{
    public sealed partial class PackTextureAtlasBatchService
    {
        // Report-only simulation of tighter page packing. The planner itself already packs with the
        // best of shelf, MaxRects and skyline on the chart rectangles it sizes, and uses tight pieces
        // (one rectangle per 8-connected component) for a group where they make the pages smaller; the
        // real pages are the baseline here, so a group that already runs on pieces has its pieces as its
        // charts and gains nothing from the tight-piece strategies (no double counting). For every layout group the planner packs, the charts are re-sized and
        // re-packed with other strategies (smaller gutter or alignment, tight pieces) under the SAME constraints (power of
        // two canvases, side limit, aspect restriction, whole clusters per page, up to 4 pages) and
        // the page bytes of the existing page-bytes formula are reported. Nothing here changes a
        // decision or an output byte; any failure is counted, never thrown into the real path.
        //
        // Strategy parameters are in pixels of the COARSEST slot (so they are shifted by the
        // group's downshift into reference pixels): gutter = padding around the touched cell box,
        // align = chart rectangle alignment. Gutter -1 and align 0 mean "as the planner does".
        private sealed record SharedRepackSimStrategy(
            string Name,
            SharedRepackPackerKind Packer,
            int Gutter,
            int Align,
            bool Tight,
            string Safety);

        private static readonly SharedRepackSimStrategy[] SharedRepackSimStrategies =
        [
            new("shelf, align 16 (3 mips block-copied)", SharedRepackPackerKind.Shelf, -1, 16, false,
                "alignment 32 -> 16 px; mip 3 re-encoded instead of block-copied"),
            new("MaxRects/skyline, align 16", SharedRepackPackerKind.Best, -1, 16, false,
                "alignment 32 -> 16 px; mip 3 re-encoded instead of block-copied"),
            new("MaxRects/skyline, align 4 (BC block only)", SharedRepackPackerKind.Best, -1, 4, false,
                "alignment 4 px, gutter unchanged; only mip 0 block-copied"),
            new("MaxRects/skyline, gutter 8 only", SharedRepackPackerKind.Best, 8, 0, false,
                "gutter 16 -> 8 px, alignment unchanged; gutter clean to mip 3"),
            new("MaxRects/skyline, gutter 4 only", SharedRepackPackerKind.Best, 4, 0, false,
                "gutter 16 -> 4 px, alignment unchanged; gutter clean to mip 2"),
            new("MaxRects/skyline, gutter 8 + align 8", SharedRepackPackerKind.Best, 8, 8, false,
                "gutter and alignment 8 px; mips 2+ re-encoded, gutter clean to mip 3"),
            new("shelf, gutter 4 + align 4", SharedRepackPackerKind.Shelf, 4, 4, false,
                "gutter and alignment 4 px (one BC block); only mip 0 block-copied, gutter clean to mip 2"),
            new("MaxRects/skyline, gutter 4 + align 4", SharedRepackPackerKind.Best, 4, 4, false,
                "gutter and alignment 4 px (one BC block); only mip 0 block-copied, gutter clean to mip 2"),
            new("MaxRects/skyline, no gutter + align 4 (bound)", SharedRepackPackerKind.Best, 0, 4, false,
                "NOT SAFE for filtering at any mip; theoretical bound only"),
            new("tight pieces, current padding", SharedRepackPackerKind.Best, -1, 0, true,
                "one rectangle per 8-connected component instead of per merged chart; the planner already uses it where it is smaller, so a group on pieces gains nothing from this one"),
            new("tight pieces, align 16", SharedRepackPackerKind.Best, -1, 16, true,
                "as above with alignment 16 px"),
            new("tight pieces, gutter 4 + align 4", SharedRepackPackerKind.Best, 4, 4, true,
                "as above with 4 px gutter and alignment"),
        ];

        // Strategies the summary ranks groups by (everything except the unsafe bound).
        private static bool IsSharedRepackSimRanked(SharedRepackSimStrategy strategy) => strategy.Gutter != 0;

        private const long SharedRepackSimOperationBudget = 40_000_000;

        private sealed record SharedRepackSimItem(string Strategy, bool Ok, long Bytes, int Pages, string Sizes, string? Failure);

        private sealed class SharedRepackGroupSim
        {
            public string Label { get; init; } = string.Empty;
            public bool Gated { get; init; }
            public long SourceBytes { get; init; }
            public long AfterBytes { get; init; }
            public string RealSizes { get; init; } = string.Empty;
            public int Charts { get; init; }
            public int Pieces { get; set; }
            // Charts whose recomputed baseline rectangle differs from the planner's (should be 0).
            public int FormulaMismatches { get; set; }
            public string? Failure { get; set; }
            public List<SharedRepackSimItem> Items { get; } = [];
        }

        private static (int Downshift, int CellDownshift) GetSharedRepackDownshifts(SharedRepackTuple tuple)
        {
            int downshift = 0, cellDownshift = 0;
            foreach (var (slot, exponent) in tuple.SourceExponent)
            {
                var shift = Math.Max(-exponent.X, -exponent.Y);
                cellDownshift = Math.Max(cellDownshift, shift);
                if (!tuple.CopySlots.Contains(slot))
                    downshift = Math.Max(downshift, shift);
            }

            return (downshift, cellDownshift);
        }

        // A chart box (cells, inclusive) as a padded reference-pixel rectangle.
        private static (int X0, int Y0, int Width, int Height) GetSharedRepackRectangle(
            SharedRepackTuple tuple,
            (int X0, int Y0, int X1, int Y1) box,
            int gutter,
            int align)
        {
            var cell = tuple.Cell;
            var left = Math.Max(0, box.X0 * cell - gutter);
            var top = Math.Max(0, box.Y0 * cell - gutter);
            var right = Math.Min(tuple.ReferenceWidth, (box.X1 + 1) * cell + gutter);
            var bottom = Math.Min(tuple.ReferenceHeight, (box.Y1 + 1) * cell + gutter);
            if (tuple.ReferenceWidth % align == 0 && tuple.ReferenceHeight % align == 0)
            {
                left = left / align * align;
                top = top / align * align;
                right = Math.Min(tuple.ReferenceWidth, RoundUpTo(right, align));
                bottom = Math.Min(tuple.ReferenceHeight, RoundUpTo(bottom, align));
            }

            return (left, top, right - left, bottom - top);
        }

        private static SharedRepackGroupSim? SimulateSharedRepackGroupPacking(
            SharedTextureRepackResult result,
            List<SharedRepackTuple> groupTuples,
            List<SharedRepackPage> pages,
            int maximumSide,
            long before,
            long after,
            bool gated,
            string label)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                var charts = groupTuples.SelectMany(tuple => tuple.Charts).ToList();
                var sim = new SharedRepackGroupSim
                {
                    Label = label,
                    Gated = gated,
                    SourceBytes = before,
                    AfterBytes = after,
                    RealSizes = string.Join(" + ", pages.Select(page => $"{page.LayoutWidth}x{page.LayoutHeight}")),
                    Charts = charts.Count,
                };
                try
                {
                    RunSharedRepackSimStrategies(sim, groupTuples, charts, maximumSide, result.MinimumGroupSavingBytes);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    sim.Failure = $"{ex.GetType().Name}: {ex.Message}";
                }

                return sim;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new SharedRepackGroupSim { Label = label, Gated = gated, SourceBytes = before, AfterBytes = after, Failure = ex.GetType().Name };
            }
            finally
            {
                result.PackingSimTicks += clock.ElapsedTicks;
            }
        }

        private static void RunSharedRepackSimStrategies(
            SharedRepackGroupSim sim,
            List<SharedRepackTuple> groupTuples,
            List<SharedRepackChart> charts,
            int maximumSide,
            long minimumGroupSaving)
        {
            var first = groupTuples[0];
            var slots = first.Slots.Keys.ToList();
            var exponents = slots.ToDictionary(slot => slot, slot => first.SourceExponent[slot]);
            var maximumWidth = maximumSide >> Math.Max(0, exponents.Values.Max(exponent => exponent.X));
            var maximumHeight = maximumSide >> Math.Max(0, exponents.Values.Max(exponent => exponent.Y));
            Func<int, int, bool>? canvasAllowed = first.EmissiveCompensation
                ? (width, height) => (long)width * first.ReferenceHeight == (long)height * first.ReferenceWidth
                : null;

            long PageBytes(int width, int height)
            {
                long total = 0;
                foreach (var slot in slots)
                {
                    var w = ScaleByExponent(width, exponents[slot].X);
                    var h = ScaleByExponent(height, exponents[slot].Y);
                    total += EstimateDdsChainBytes(first.Layouts[slot], w, h, GetFullMipChainLength(w, h));
                }

                return total;
            }

            var sources = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var tuple in groupTuples)
            {
                foreach (var slot in slots)
                    sources[tuple.Slots[slot]] = tuple.Layouts[slot].ChainBytes;
            }

            // Clusters of charts, as PackSharedRepackGroup builds them.
            var chartIndex = new Dictionary<SharedRepackChart, int>();
            for (var index = 0; index < charts.Count; index++)
                chartIndex[charts[index]] = index;
            var parent = Enumerable.Range(0, charts.Count).ToArray();
            int Find(int node)
            {
                while (parent[node] != node)
                {
                    parent[node] = parent[parent[node]];
                    node = parent[node];
                }

                return node;
            }

            foreach (var tuple in groupTuples)
            {
                foreach (var part in tuple.Parts)
                {
                    int? head = null;
                    for (var vertex = 0; vertex < part.VertexChart.Length; vertex++)
                    {
                        if (!part.Used[vertex])
                            continue;

                        var id = chartIndex[tuple.Charts[part.VertexChart[vertex]]];
                        if (head == null)
                            head = id;
                        else
                            parent[Find(id)] = Find(head.Value);
                    }
                }
            }

            // The pieces of one merged chart share a cluster, as in PackSharedRepackGroupLayout.
            foreach (var tuple in groupTuples)
            {
                var head = new Dictionary<int, int>();
                foreach (var chart in tuple.Charts.Where(chart => chart.MergedId >= 0))
                {
                    var id = chartIndex[chart];
                    if (head.TryGetValue(chart.MergedId, out var sibling))
                        parent[Find(id)] = Find(sibling);
                    else
                        head[chart.MergedId] = id;
                }
            }

            var chartCluster = Enumerable.Range(0, charts.Count).Select(Find).ToArray();

            // 8-connected components of every tuple (cells are labelled by their chart already).
            Dictionary<SharedRepackTuple, List<((int X0, int Y0, int X1, int Y1) Box, int Chart)>>? components = null;
            Dictionary<SharedRepackTuple, List<((int X0, int Y0, int X1, int Y1) Box, int Chart)>> GetComponents()
            {
                if (components != null)
                    return components;

                components = [];
                foreach (var tuple in groupTuples)
                {
                    var grid = tuple.CellChart.Select(chart => chart >= 0).ToArray();
                    var boxes = FindSharedRepackComponents(grid, tuple.GridX, tuple.GridY, out var label);
                    var owner = new int[boxes.Count];
                    for (var cell = 0; cell < label.Length; cell++)
                    {
                        if (label[cell] >= 0)
                            owner[label[cell]] = tuple.CellChart[cell];
                    }

                    components[tuple] = boxes.Select((box, index) => (box, owner[index])).ToList();
                }

                return components;
            }

            // Does the recomputed baseline rectangle match the planner's? (A self-check of the model.)
            foreach (var tuple in groupTuples)
            {
                foreach (var chart in tuple.Charts)
                {
                    var rectangle = GetSharedRepackRectangle(
                        tuple, (chart.CellX0, chart.CellY0, chart.CellX1, chart.CellY1), tuple.Gutter, tuple.Align);
                    var groupAlign = first.Align;
                    var width = RoundUpTo(rectangle.Width, groupAlign);
                    var height = RoundUpTo(rectangle.Height, groupAlign);
                    if (width != chart.LayoutWidth || height != chart.LayoutHeight)
                        sim.FormulaMismatches++;
                }
            }

            foreach (var strategy in SharedRepackSimStrategies)
            {
                try
                {
                    var budget = new SharedRepackSimBudget(SharedRepackSimOperationBudget);
                    var sizes = new List<(int Width, int Height)>();
                    var clusterOf = new List<int>();
                    var unit = int.MaxValue;
                    string? invalid = null;
                    var baseline = strategy.Gutter < 0 && strategy.Align == 0 && !strategy.Tight;

                    void Add(SharedRepackTuple tuple, int width, int height, int cluster)
                    {
                        foreach (var slot in slots)
                        {
                            var layout = tuple.Layouts[slot];
                            var exponent = tuple.SourceExponent[slot];
                            if (ScaleByExponent(width, exponent.X) > layout.Width ||
                                ScaleByExponent(height, exponent.Y) > layout.Height)
                            {
                                invalid ??= $"a {width}x{height} rectangle exceeds the {slot} texture";
                            }
                        }

                        if (width > maximumWidth || height > maximumHeight)
                            invalid ??= $"a {width}x{height} rectangle exceeds the side limit";
                        sizes.Add((width, height));
                        clusterOf.Add(cluster);
                    }

                    foreach (var tuple in groupTuples)
                    {
                        var (downshift, cellDownshift) = GetSharedRepackDownshifts(tuple);
                        var gutter = strategy.Gutter < 0 ? tuple.Gutter : Math.Min(tuple.Cell, strategy.Gutter << downshift);
                        var align = strategy.Align == 0
                            ? tuple.Align
                            : Math.Min(tuple.Align, Math.Max(4 << cellDownshift, strategy.Align << downshift));
                        unit = Math.Min(unit, align);
                        if (baseline)
                        {
                            foreach (var chart in tuple.Charts)
                                Add(tuple, chart.LayoutWidth, chart.LayoutHeight, chartCluster[chartIndex[chart]]);
                        }
                        else if (!strategy.Tight)
                        {
                            foreach (var chart in tuple.Charts)
                            {
                                var rectangle = GetSharedRepackRectangle(
                                    tuple, (chart.CellX0, chart.CellY0, chart.CellX1, chart.CellY1), gutter, align);
                                Add(
                                    tuple,
                                    RoundUpTo(rectangle.Width, align),
                                    RoundUpTo(rectangle.Height, align),
                                    chartCluster[chartIndex[chart]]);
                            }
                        }
                        else
                        {
                            foreach (var (box, chartId) in GetComponents()[tuple])
                            {
                                var rectangle = GetSharedRepackRectangle(tuple, box, gutter, align);
                                Add(
                                    tuple,
                                    RoundUpTo(rectangle.Width, align),
                                    RoundUpTo(rectangle.Height, align),
                                    chartCluster[chartIndex[tuple.Charts[chartId]]]);
                            }
                        }
                    }

                    if (strategy.Tight)
                        sim.Pieces = sizes.Count;
                    if (invalid != null)
                    {
                        sim.Items.Add(new(strategy.Name, false, 0, 0, string.Empty, invalid));
                        continue;
                    }

                    // The planner's own unit: every rectangle is a multiple of the alignment.
                    if (sizes.Any(size => size.Width % unit != 0 || size.Height % unit != 0))
                    {
                        sim.Items.Add(new(strategy.Name, false, 0, 0, string.Empty, "rectangles are not a multiple of the alignment"));
                        continue;
                    }

                    var input = new SharedRepackSimInput
                    {
                        Sizes = sizes,
                        ClusterOf = clusterOf,
                        Unit = unit,
                        MaximumWidth = maximumWidth,
                        MaximumHeight = maximumHeight,
                        CanvasAllowed = canvasAllowed,
                        PageBytes = PageBytes,
                        SourceBytes = sources.Values.Sum(),
                        MinimumSaving = minimumGroupSaving,
                        MaximumPages = SharedRepackMaximumSplitPages,
                        CandidatesPerLevel = SharedRepackSplitCandidatesPerLevel,
                        MaximumAttempts = SharedRepackMaximumSplitAttempts,
                    };
                    var outcome = SharedRepackGroupSimulator.Simulate(input, strategy.Packer, budget);
                    sim.Items.Add(new(
                        strategy.Name, outcome.Ok, outcome.Bytes, outcome.Pages.Count, outcome.Sizes, outcome.Failure));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    sim.Items.Add(new(strategy.Name, false, 0, 0, string.Empty, $"{ex.GetType().Name}: {ex.Message}"));
                }
            }
        }

        // The "Packing simulation" report section.
        private static void AppendSharedRepackPackingSimulation(StringBuilder sb, SharedTextureRepackResult result)
        {
            var built = result.Outcomes
                .Where(outcome => outcome.Bucket == SharedRepackBucketBuilt && outcome.Context != null)
                .SelectMany(outcome => outcome.Context!.GroupWaste.Where(waste => !waste.Gated && waste.Sim != null))
                .Select(waste => waste.Sim!)
                .ToList();
            var gated = result.Outcomes
                .Where(outcome => outcome.Context != null)
                .SelectMany(outcome => outcome.Context!.GroupWaste.Where(waste => waste.Gated && waste.Sim != null))
                .Select(waste => waste.Sim!)
                .ToList();
            if (built.Count == 0 && gated.Count == 0)
                return;

            var minimum = result.MinimumGroupSavingBytes;
            var all = built.Concat(gated).ToList();
            sb.AppendLine();
            sb.AppendLine("Packing simulation (report only, power-of-two pages):");
            sb.AppendLine(
                "  The same charts re-packed under the same constraints (power-of-two canvases, side limit, aspect " +
                "restriction, whole clusters per page, up to 4 pages); bytes use the planner's page-bytes formula. " +
                "The baseline is the real pages, which are already packed with the best of shelf, MaxRects and skyline " +
                "and, in a group where one rectangle per UV island made the pages smaller, already on tight pieces; " +
                "the strategies change the gutter, the alignment or the rectangles themselves. " +
                "A group keeps its real pages when a strategy does not make it smaller. Nothing here changes the output.");
            sb.AppendLine(
                $"  Simulated groups: {built.Count:N0} built-set groups, {gated.Count:N0} left-untouched groups; " +
                $"simulation time {TimeSpan.FromTicks((long)(result.PackingSimTicks * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency)).TotalSeconds:0.0} s; " +
                $"group failures {all.Count(item => item.Failure != null):N0}; " +
                $"baseline-model mismatches {all.Sum(item => item.FormulaMismatches):N0} chart(s) of {all.Sum(item => item.Charts):N0}.");

            long Real(SharedRepackGroupSim sim) => sim.AfterBytes;
            long Best(SharedRepackGroupSim sim, string strategy)
            {
                var item = sim.Items.FirstOrDefault(entry => entry.Strategy == strategy);
                return item is { Ok: true } ? Math.Min(item.Bytes, Real(sim)) : Real(sim);
            }

            // Set threshold projection per outcome: the saving the set has now (its actual bytes when
            // built, else what its built groups save) plus what the strategy adds. Rough: closure of
            // tuples sharing a texture and the actual-bytes re-check are not redone.
            var setOutcomes = result.Outcomes
                .Where(outcome => outcome.Context != null &&
                                  (outcome.Bucket == SharedRepackBucketBuilt || outcome.Bucket == SharedRepackBucketBytes) &&
                                  outcome.Context.GroupWaste.Any(waste => waste.Sim != null))
                .ToList();
            var threshold = result.MinimumSavingBytes;

            sb.AppendLine(
                $"  Per strategy (built = groups of built sets; untouched = groups below the {minimum:N0}-byte group minimum):");
            foreach (var strategy in SharedRepackSimStrategies)
            {
                var builtAfter = built.Sum(Real);
                var builtSim = built.Sum(sim => Best(sim, strategy.Name));
                var smaller = built.Count(sim => Best(sim, strategy.Name) < Real(sim));
                var halved = built.Count(sim => Best(sim, strategy.Name) * 2 <= Real(sim) + 64);
                var failed = all.Count(sim => sim.Items.Any(item => item.Strategy == strategy.Name && !item.Ok));
                var passing = gated
                    .Where(sim => sim.SourceBytes - Best(sim, strategy.Name) >= minimum && sim.SourceBytes - Best(sim, strategy.Name) > 0)
                    .ToList();
                var gatedSaved = passing.Sum(sim => sim.SourceBytes - Best(sim, strategy.Name));
                var gatedSmaller = gated.Count(sim => Best(sim, strategy.Name) < Real(sim));
                var nowPassing = 0;
                var newlyPassing = 0;
                foreach (var outcome in setOutcomes)
                {
                    var groups = outcome.Context!.GroupWaste.Where(waste => waste.Sim != null).Select(waste => waste.Sim!).ToList();
                    var current = outcome.Actual ?? groups.Where(sim => !sim.Gated).Sum(sim => sim.SourceBytes - sim.AfterBytes);
                    var projected = current
                                    + groups.Where(sim => !sim.Gated).Sum(sim => Real(sim) - Best(sim, strategy.Name))
                                    + groups.Where(sim => sim.Gated && sim.SourceBytes - Best(sim, strategy.Name) >= minimum &&
                                                          sim.SourceBytes - Best(sim, strategy.Name) > 0)
                                        .Sum(sim => sim.SourceBytes - Best(sim, strategy.Name));
                    if (projected >= threshold)
                    {
                        nowPassing++;
                        if (current < threshold)
                            newlyPassing++;
                    }
                }

                sb.AppendLine($"  * {strategy.Name} [{strategy.Safety}]");
                sb.AppendLine(
                    $"      built: pages {builtAfter:N0} -> {builtSim:N0} bytes ({FormatSignedBytes(builtSim - builtAfter)}), " +
                    $"{smaller:N0} of {built.Count:N0} groups smaller ({halved:N0} drop at least one power-of-two step)");
                sb.AppendLine(
                    $"      untouched: {passing.Count:N0} of {gated.Count:N0} groups would pass the group minimum, saving " +
                    $"{gatedSaved:N0} bytes ({gatedSmaller:N0} get smaller pages); sets reaching the {threshold:N0}-byte set " +
                    $"threshold: {nowPassing:N0} of {setOutcomes.Count:N0} considered ({newlyPassing:N0} newly, rough){(failed != 0 ? $"; {failed:N0} simulation failure(s)" : string.Empty)}");
            }

            var ranked = SharedRepackSimStrategies.Where(IsSharedRepackSimRanked).Select(strategy => strategy.Name).ToList();
            var top = all
                .Select(sim =>
                {
                    var bestName = ranked.OrderBy(name => Best(sim, name)).ThenBy(name => name, StringComparer.Ordinal).First();
                    return (Sim: sim, Name: bestName, Bytes: Best(sim, bestName));
                })
                .Where(item => item.Bytes < Real(item.Sim))
                .OrderByDescending(item => Real(item.Sim) - item.Bytes)
                .ThenBy(item => item.Sim.Label, StringComparer.Ordinal)
                .Take(10)
                .ToList();
            sb.AppendLine("  Top groups by gain (best ranked strategy, which excludes the unsafe bound):");
            if (top.Count == 0)
                sb.AppendLine("    none");
            foreach (var (sim, name, bytes) in top)
            {
                var item = sim.Items.First(entry => entry.Strategy == name);
                sb.AppendLine(
                    $"    {(sim.Gated ? "untouched" : "built")}: {Shorten(sim.Label, 80)}: sources {sim.SourceBytes:N0}, pages " +
                    $"{sim.AfterBytes:N0} ({sim.RealSizes}) -> {bytes:N0} ({item.Sizes}) with {name}" +
                    (sim.Gated && sim.SourceBytes - bytes >= minimum ? "; would pass the group minimum" : string.Empty));
            }
        }

        private static string Shorten(string text, int length)
            => text.Length <= length ? text : text[..(length - 3)] + "...";
    }
}
