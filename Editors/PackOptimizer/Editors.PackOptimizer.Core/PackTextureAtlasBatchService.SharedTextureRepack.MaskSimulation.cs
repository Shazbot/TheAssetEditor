using System.Diagnostics;
using System.Text;

namespace Editors.PackOptimizer.Core
{
    public sealed partial class PackTextureAtlasBatchService
    {
        // Shape-aware packing: every page's charts become occupancy masks (their touched cells grown by
        // the gutter, in packing units) and are packed greedily on the page's power-of-two canvas, cut by
        // the planner's own trim rules. The page loop takes the mask layout when it is smaller; this
        // simulation (report only) shows what masks would still gain on the pages that kept their
        // rectangles (pages that took masks are not simulated again).
        // Operations (mask positions and rows looked at) the planner and the simulation may spend per page.
        private const long SharedRepackMaskSimBudget = 6_000_000;

        // RealBytes/SimBytes: the pages' bytes as planned and with the mask packing (never above real);
        // Mismatched: pages whose recomputed real bytes differ from the planned ones; Fallbacks: charts
        // without touched cells (packed as their rectangle).
        private readonly record struct SharedRepackMaskSim(
            long RealBytes,
            long SimBytes,
            int Pages,
            int PagesSmaller,
            int PagesUncut,
            int BudgetHits,
            int Mismatched,
            int Fallbacks,
            long Ticks,
            int MaskPacked = 0,
            long MaskSaved = 0);

        private static SharedRepackMaskSim SimulateSharedRepackMaskPacking(List<SharedRepackPage> pages)
        {
            var clock = Stopwatch.StartNew();
            long real = 0, simulated = 0;
            int smaller = 0, uncut = 0, hits = 0, mismatched = 0, fallbacks = 0, counted = 0, maskPacked = 0;
            long maskSaved = 0;
            foreach (var page in pages)
            {
                if (page.Charts.Count == 0 || page.LayoutWidth <= 0 || page.LayoutHeight <= 0)
                    continue;

                counted++;
                real += page.BytesAfter;
                maskSaved += page.MaskSavedBytes;
                if (page.MaskPacked)
                {
                    // Already packed as masks: nothing left to simulate.
                    maskPacked++;
                    simulated += page.BytesAfter;
                    continue;
                }

                var exact = GetSharedRepackPageBytes(page, page.LayoutWidth, page.LayoutHeight);
                if (exact != page.BytesAfter)
                    mismatched++;
                if (page.SimTrim == null || page.SimUnit <= 0)
                {
                    uncut++;
                    simulated += page.BytesAfter;
                    continue;
                }

                try
                {
                    var masks = new List<SharedRepackMask>(page.Charts.Count);
                    foreach (var chart in page.Charts)
                        masks.Add(BuildSharedRepackChartMask(chart, page.SimUnit, ref fallbacks));

                    var budget = new SharedRepackSimBudget(SharedRepackMaskSimBudget);
                    var found = SharedRepackMaskPacker.TryPackMinimumTrimmedCanvas(
                        masks, page.SimUnit, page.SimCanvasWidth, page.SimCanvasHeight, page.SimTrim,
                        SharedRepackTrimmedPackingWidths, (long)page.LayoutWidth * page.LayoutHeight, budget,
                        out var width, out var height, out _);
                    if (budget.Exceeded)
                        hits++;
                    var bytes = found ? GetSharedRepackPageBytes(page, width, height) : page.BytesAfter;
                    if (bytes < page.BytesAfter)
                        smaller++;
                    simulated += Math.Min(bytes, page.BytesAfter);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    uncut++;
                    simulated += page.BytesAfter;
                }
            }

            return new SharedRepackMaskSim(
                real, simulated, counted, smaller, uncut, hits, mismatched, fallbacks, clock.ElapsedTicks, maskPacked, maskSaved);
        }

        // The bytes of the page's outputs at a layout size (what the planner sums into BytesAfter).
        private static long GetSharedRepackPageBytes(SharedRepackPage page, int width, int height)
        {
            long total = 0;
            foreach (var output in page.Outputs)
            {
                var w = ScaleByExponent(width, output.ExponentX);
                var h = ScaleByExponent(height, output.ExponentY);
                total += EstimateDdsChainBytes(output.Template, w, h, GetFullMipChainLength(w, h));
            }

            return total;
        }

        // The chart's cells in packing units: every unit cell its rectangle shares with a touched cell
        // grown by the gutter (the band the rectangle path keeps, clamped to the rectangle). The band
        // outside the texture stays in (the rectangle path fills it with the edge texels, which a clamped
        // sampler reads next to the border) unless the test hook clamps to the texture.
        private static SharedRepackMask BuildSharedRepackChartMask(SharedRepackChart chart, int unit, ref int fallbacks)
        {
            var tuple = chart.Tuple;
            var width = chart.LayoutWidth / unit;
            var height = chart.LayoutHeight / unit;
            var mask = new SharedRepackMask(width, height);
            // A tuple without its cell grid (hand-built) keeps the rectangle.
            if (tuple.CellChart.Length != tuple.GridX * tuple.GridY || tuple.CellChart.Length == 0)
            {
                fallbacks++;
                return SharedRepackMask.Filled(width, height);
            }

            var cell = tuple.Cell;
            var gutter = tuple.Gutter;
            for (var y = chart.CellY0; y <= chart.CellY1; y++)
            {
                for (var x = chart.CellX0; x <= chart.CellX1; x++)
                {
                    if (tuple.CellChart[y * tuple.GridX + x] != chart.Id)
                        continue;

                    var floorX = SharedRepackMaskClampsToTexture ? 0 : int.MinValue;
                    var ceilX = SharedRepackMaskClampsToTexture ? tuple.ReferenceWidth : int.MaxValue;
                    var ceilY = SharedRepackMaskClampsToTexture ? tuple.ReferenceHeight : int.MaxValue;
                    var left = Math.Max(Math.Max(floorX, x * cell - gutter), chart.X0);
                    var top = Math.Max(Math.Max(floorX, y * cell - gutter), chart.Y0);
                    var right = Math.Min(Math.Min(ceilX, (x + 1) * cell + gutter), chart.X0 + chart.Width);
                    var bottom = Math.Min(Math.Min(ceilY, (y + 1) * cell + gutter), chart.Y0 + chart.Height);
                    if (right <= left || bottom <= top)
                        continue;

                    var first = (left - chart.X0) / unit;
                    var last = (right - 1 - chart.X0) / unit;
                    for (var row = (top - chart.Y0) / unit; row <= (bottom - 1 - chart.Y0) / unit; row++)
                        mask.SetRange(row, first, last + 1);
                }
            }

            if (mask.Area() == 0)
            {
                fallbacks++;
                return SharedRepackMask.Filled(width, height);
            }

            return mask;
        }

        // Report lines for the mask packing of built or untouched groups, next to the padding split.
        private static void AppendSharedRepackMaskSim(
            StringBuilder sb,
            string indent,
            List<SharedRepackGroupWaste> groups,
            long minimumGroupSaving,
            bool gated,
            bool topGroups)
        {
            var sims = groups.Where(waste => waste.MaskSim.Pages != 0).ToList();
            if (sims.Count == 0)
                return;

            var real = sims.Sum(waste => waste.MaskSim.RealBytes);
            var simulated = sims.Sum(waste => waste.MaskSim.SimBytes);
            var pages = sims.Sum(waste => waste.MaskSim.Pages);
            var smallerGroups = sims.Count(waste => waste.MaskSim.SimBytes < waste.MaskSim.RealBytes);
            var saving = real - simulated;
            sb.AppendLine(
                $"{indent}cell-mask packing simulated (greedy masks on the same canvases and trim rules, never above the real page): " +
                $"pages {real:N0} -> {simulated:N0} bytes, saves {saving:N0} ({(real == 0 ? 0 : 100.0 * saving / real):0.00}% of page bytes); " +
                $"{smallerGroups:N0} of {sims.Count:N0} group(s) smaller, {sims.Count - smallerGroups:N0} not (equal or worse); " +
                $"{sims.Sum(waste => waste.MaskSim.PagesSmaller):N0} of {pages:N0} page(s) smaller " +
                $"({sims.Sum(waste => waste.MaskSim.PagesUncut):N0} not cut, kept)");
            var maskPages = sims.Sum(waste => waste.MaskSim.MaskPacked);
            if (maskPages != 0)
            {
                sb.AppendLine(
                    $"{indent}  {maskPages:N0} of {pages:N0} page(s) are packed as masks (saving {sims.Sum(waste => waste.MaskSim.MaskSaved):N0} " +
                    "bytes against their rectangle layout); the figures above are what masks could still gain on the other pages");
            }

            if (gated)
            {
                var passing = sims
                    .Where(waste => waste.SourceBytes - waste.MaskSim.SimBytes >= minimumGroupSaving)
                    .ToList();
                sb.AppendLine(
                    $"{indent}  {passing.Count:N0} would pass the {minimumGroupSaving:N0}-byte group minimum, adding " +
                    $"{passing.Sum(waste => waste.SourceBytes - waste.MaskSim.SimBytes):N0} bytes saved (before the set threshold " +
                    "and the closure of their sets)");
            }

            sb.AppendLine(
                $"{indent}  simulation {sims.Sum(waste => waste.MaskSim.Ticks) * 1000.0 / Stopwatch.Frequency / 1000.0:0.0} s, " +
                $"{sims.Sum(waste => waste.MaskSim.BudgetHits):N0} page(s) hit the budget; check: " +
                $"{sims.Sum(waste => waste.MaskSim.Mismatched):N0} page(s) with recomputed bytes != planned, " +
                $"{sims.Sum(waste => waste.MaskSim.Fallbacks):N0} chart(s) without touched cells; the true saving also depends on " +
                "the set-level gates, and groups that start to pass would grow it (not modelled)");
            if (!topGroups)
                return;

            // The groups with the biggest gain: what the mask-packed pages saved against their rectangles,
            // plus what the simulation still finds on the others.
            foreach (var waste in sims
                         .Where(waste => waste.MaskSim.MaskSaved != 0 || waste.MaskSim.RealBytes > waste.MaskSim.SimBytes)
                         .OrderByDescending(waste => waste.MaskSim.MaskSaved + waste.MaskSim.RealBytes - waste.MaskSim.SimBytes)
                         .ThenBy(waste => waste.Label, StringComparer.Ordinal)
                         .Take(10))
            {
                sb.AppendLine(
                    $"{indent}  mask packing saves {waste.MaskSim.MaskSaved:N0} against rectangles (pages now {waste.MaskSim.RealBytes:N0}), " +
                    $"{waste.MaskSim.RealBytes - waste.MaskSim.SimBytes:N0} more simulated: {waste.Label}");
            }
        }
    }
}
