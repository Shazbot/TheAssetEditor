using Editors.PackOptimizer.Core;

namespace Test.PackOptimizer.Core
{
    public class SharedRepackPackingSimulationTests
    {
        private static List<(int Width, int Height)> RandomSizes(int seed, int count, int unit, int maximumUnits)
        {
            var random = new Random(seed);
            return Enumerable.Range(0, count)
                .Select(_ => (unit * random.Next(1, maximumUnits + 1), unit * random.Next(1, maximumUnits + 1)))
                .ToList();
        }

        private static void AssertValid(
            IReadOnlyList<(int Width, int Height)> sizes,
            (int X, int Y)[] positions,
            int canvasWidth,
            int canvasHeight,
            int unit)
        {
            Assert.That(positions, Has.Length.EqualTo(sizes.Count));
            for (var left = 0; left < sizes.Count; left++)
            {
                Assert.That(positions[left].X % unit, Is.Zero, $"{left} x is not aligned");
                Assert.That(positions[left].Y % unit, Is.Zero, $"{left} y is not aligned");
                Assert.That(positions[left].X, Is.GreaterThanOrEqualTo(0));
                Assert.That(positions[left].Y, Is.GreaterThanOrEqualTo(0));
                Assert.That(positions[left].X + sizes[left].Width, Is.LessThanOrEqualTo(canvasWidth));
                Assert.That(positions[left].Y + sizes[left].Height, Is.LessThanOrEqualTo(canvasHeight));
                for (var right = left + 1; right < sizes.Count; right++)
                {
                    var overlaps = positions[left].X < positions[right].X + sizes[right].Width &&
                                   positions[right].X < positions[left].X + sizes[left].Width &&
                                   positions[left].Y < positions[right].Y + sizes[right].Height &&
                                   positions[right].Y < positions[left].Y + sizes[left].Height;
                    Assert.That(overlaps, Is.False, $"{left} overlaps {right}");
                }
            }
        }

        [TestCase(1, 8)]
        [TestCase(2, 20)]
        [TestCase(3, 60)]
        public void EveryPackerProducesOverlapFreeAlignedLayouts(int seed, int count)
        {
            const int unit = 8;
            var sizes = RandomSizes(seed, count, unit, 6);
            var area = sizes.Sum(size => (long)size.Width * size.Height);
            var side = 1;
            while ((long)side * side < area * 2)
                side <<= 1;

            foreach (var mode in new[] { 0, 1, 2 })
            {
                var order = SharedRepackRectPacker.OrderBy(sizes, mode);
                Assert.That(order.OrderBy(index => index), Is.EqualTo(Enumerable.Range(0, count)));
                if (SharedRepackRectPacker.TryShelf(sizes, order, side, side, out var shelf))
                    AssertValid(sizes, shelf, side, side, unit);
                foreach (var areaFit in new[] { false, true })
                {
                    Assert.That(
                        SharedRepackRectPacker.TryMaxRects(sizes, order, side, side, areaFit, null, out var maxRects),
                        Is.True, $"maxrects mode {mode} area {areaFit}");
                    AssertValid(sizes, maxRects, side, side, unit);
                }

                Assert.That(SharedRepackRectPacker.TrySkyline(sizes, order, side, side, null, out var skyline), Is.True);
                AssertValid(sizes, skyline, side, side, unit);
            }
        }

        [Test]
        public void PackCanvases_ReturnsPowerOfTwoAlignedCanvasesAndNeverLargerThanTheShelf()
        {
            for (var seed = 10; seed < 30; seed++)
            {
                const int unit = 16;
                var sizes = RandomSizes(seed, 5 + seed % 25, unit, 12);
                var shelf = SharedRepackRectPacker.TryPackCanvases(
                    sizes, unit, 4096, 4096, null, 16, SharedRepackPackerKind.Shelf, null,
                    out var shelfWidth, out var shelfHeight, out var shelfPositions);
                var best = SharedRepackRectPacker.TryPackCanvases(
                    sizes, unit, 4096, 4096, null, 16, SharedRepackPackerKind.Best, null,
                    out var bestWidth, out var bestHeight, out var bestPositions);

                Assert.That(shelf, Is.True);
                Assert.That(best, Is.True);
                Assert.That(System.Numerics.BitOperations.IsPow2(bestWidth), Is.True);
                Assert.That(System.Numerics.BitOperations.IsPow2(bestHeight), Is.True);
                Assert.That((long)bestWidth * bestHeight, Is.LessThanOrEqualTo((long)shelfWidth * shelfHeight), $"seed {seed}");
                AssertValid(sizes, shelfPositions, shelfWidth, shelfHeight, unit);
                AssertValid(sizes, bestPositions, bestWidth, bestHeight, unit);
            }
        }

        [Test]
        public void PackCanvases_RespectsTheCanvasPredicateAndTheSideLimit()
        {
            (int Width, int Height)[] sizes = [(64, 64), (128, 64), (64, 128), (256, 64)];
            Assert.That(
                SharedRepackRectPacker.TryPackCanvases(
                    sizes, 64, 4096, 4096, (w, h) => w == h, 16, SharedRepackPackerKind.Best, null,
                    out var width, out var height, out var positions),
                Is.True);
            Assert.That(width, Is.EqualTo(height));
            AssertValid(sizes, positions, width, height, 64);

            Assert.That(
                SharedRepackRectPacker.TryPackCanvases(
                    sizes, 64, 128, 128, null, 16, SharedRepackPackerKind.Best, null,
                    out _, out _, out _),
                Is.False);
        }

        [Test]
        public void MaxRectsBeatsTheShelfWhereRowsWasteSpace()
        {
            // Tall pillars with flat bars between them: rows are bad at this, free rectangles are not.
            var sizes = new List<(int Width, int Height)>();
            for (var index = 0; index < 4; index++)
                sizes.Add((32, 256));
            for (var index = 0; index < 8; index++)
                sizes.Add((96, 32));
            SharedRepackRectPacker.TryPackCanvases(
                sizes, 32, 4096, 4096, null, 16, SharedRepackPackerKind.Shelf, null, out var shelfWidth, out var shelfHeight, out _);
            SharedRepackRectPacker.TryPackCanvases(
                sizes, 32, 4096, 4096, null, 16, SharedRepackPackerKind.Best, null, out var bestWidth, out var bestHeight, out _);

            Assert.That((long)bestWidth * bestHeight, Is.LessThanOrEqualTo((long)shelfWidth * shelfHeight));
        }

        [Test]
        public void Budget_StopsARunawayPackingWithoutThrowing()
        {
            var sizes = new List<(int Width, int Height)> { (64, 160), (224, 96), (32, 192) };
            var budget = new SharedRepackSimBudget(3);

            var packed = SharedRepackRectPacker.TryPackCanvases(
                sizes, 32, 4096, 4096, null, 16, SharedRepackPackerKind.Best, budget,
                out var width, out var height, out var positions);
            SharedRepackRectPacker.TryPackCanvases(
                sizes, 32, 4096, 4096, null, 16, SharedRepackPackerKind.Shelf, null,
                out var shelfWidth, out var shelfHeight, out var shelfPositions);

            // The spent budget leaves the shelf result, never a failure.
            Assert.That(budget.Exceeded, Is.True);
            Assert.That(packed, Is.True);
            Assert.That((width, height), Is.EqualTo((shelfWidth, shelfHeight)));
            Assert.That(positions, Is.EqualTo(shelfPositions));
        }

        private static SharedRepackSimInput GroupInput(
            IReadOnlyList<(int Width, int Height)> sizes,
            IReadOnlyList<int> clusters,
            long sourceBytes,
            long minimumSaving)
            => new()
            {
                Sizes = sizes,
                ClusterOf = clusters,
                Unit = 32,
                MaximumWidth = 4096,
                MaximumHeight = 4096,
                PageBytes = (width, height) => (long)width * height,
                SourceBytes = sourceBytes,
                MinimumSaving = minimumSaving,
            };

        [Test]
        public void Simulate_KeepsClustersOnOnePageAndEveryPageValid()
        {
            var sizes = new List<(int Width, int Height)>
            {
                (512, 512), (256, 64), (64, 256), (128, 128),
                (512, 256), (96, 96),
            };
            int[] clusters = [0, 0, 1, 1, 2, 2];
            foreach (var kind in new[] { SharedRepackPackerKind.Shelf, SharedRepackPackerKind.Best })
            {
                var outcome = SharedRepackGroupSimulator.Simulate(
                    GroupInput(sizes, clusters, 4096L * 4096, 1), kind, new SharedRepackSimBudget(10_000_000));

                Assert.That(outcome.Ok, Is.True, outcome.Failure);
                var seen = outcome.Pages.SelectMany(page => page.Indexes).OrderBy(index => index).ToList();
                Assert.That(seen, Is.EqualTo(Enumerable.Range(0, sizes.Count)));
                foreach (var cluster in clusters.Distinct())
                {
                    var members = Enumerable.Range(0, sizes.Count).Where(index => clusters[index] == cluster);
                    Assert.That(
                        members.Select(index => outcome.Pages.FindIndex(page => page.Indexes.Contains(index))).Distinct().Count(),
                        Is.EqualTo(1));
                }

                foreach (var page in outcome.Pages)
                {
                    AssertValid(page.Indexes.Select(index => sizes[index]).ToList(), page.Positions, page.Width, page.Height, 32);
                    Assert.That(System.Numerics.BitOperations.IsPow2(page.Width), Is.True);
                    Assert.That(System.Numerics.BitOperations.IsPow2(page.Height), Is.True);
                }
            }
        }

        [Test]
        public void Simulate_BestPackerIsNeverWorseThanTheShelfOnTheSameRectangles()
        {
            for (var seed = 40; seed < 60; seed++)
            {
                var sizes = RandomSizes(seed, 12 + seed % 10, 32, 8);
                var clusters = Enumerable.Range(0, sizes.Count).Select(index => index / 3).ToList();
                var input = GroupInput(sizes, clusters, 4096L * 4096 * 2, 1);

                var shelf = SharedRepackGroupSimulator.Simulate(input, SharedRepackPackerKind.Shelf, new SharedRepackSimBudget(50_000_000));
                var best = SharedRepackGroupSimulator.Simulate(input, SharedRepackPackerKind.Best, new SharedRepackSimBudget(50_000_000));

                Assert.That(shelf.Ok && best.Ok, Is.True, $"{shelf.Failure} {best.Failure}");
                Assert.That(best.SingleBytes, Is.LessThanOrEqualTo(shelf.SingleBytes), $"seed {seed}");
            }
        }

        [Test]
        public void Simulate_ReportsAFailureInsteadOfThrowingWhenNothingFits()
        {
            var outcome = SharedRepackGroupSimulator.Simulate(
                GroupInput([(4096, 4096), (4096, 4096)], [0, 1], 1, 1),
                SharedRepackPackerKind.Best,
                new SharedRepackSimBudget(1_000_000));

            Assert.That(outcome.Ok, Is.True, "two clusters on two pages is a legal split of the side-limited case");
            Assert.That(outcome.Pages, Has.Count.EqualTo(2));

            var impossible = SharedRepackGroupSimulator.Simulate(
                GroupInput([(8192, 64)], [0], 1, 1), SharedRepackPackerKind.Best, new SharedRepackSimBudget(1_000_000));
            Assert.That(impossible.Ok, Is.False);
            Assert.That(impossible.Failure, Is.Not.Null);
        }

        private static SharedRepackLayoutSolver Solver(SharedRepackPackerKind kind, int unit, long budget = 40_000_000)
            => new(kind, unit, 1, new SharedRepackSimBudget(budget));

        [TestCase(1, 5)]
        [TestCase(2, 12)]
        [TestCase(3, 30)]
        [TestCase(4, 80)]
        public void Solver_NeverUsesAMoreExpensiveCanvasThanTheShelf_AndKeepsShelfLayoutsOtherwise(int seed, int count)
        {
            for (var round = 0; round < 25; round++)
            {
                var sizes = RandomSizes(seed * 100 + round, count, 32, 6);
                Assert.That(Solver(SharedRepackPackerKind.Shelf, 32).TryPack(sizes, 4096, 4096, null, out var shelfWidth, out var shelfHeight, out var shelf), Is.True);
                Assert.That(Solver(SharedRepackPackerKind.Best, 32).TryPack(sizes, 4096, 4096, null, out var width, out var height, out var positions), Is.True);

                Assert.That((long)width * height, Is.LessThanOrEqualTo((long)shelfWidth * shelfHeight));
                AssertValid(sizes, positions, width, height, 32);
                Assert.That(System.Numerics.BitOperations.IsPow2(width) && System.Numerics.BitOperations.IsPow2(height), Is.True);
                if ((width, height) == (shelfWidth, shelfHeight))
                    Assert.That(positions, Is.EqualTo(shelf), "same canvas: the shelf positions stay");
            }
        }

        [Test]
        public void Solver_FitsWhereTheShelfNeedsALargerCanvas()
        {
            (int Width, int Height)[] sizes = [(64, 160), (224, 96), (32, 192)];

            Solver(SharedRepackPackerKind.Shelf, 32).TryPack(sizes, 4096, 4096, null, out var shelfWidth, out var shelfHeight, out _);
            Assert.That(Solver(SharedRepackPackerKind.Best, 32).TryPack(sizes, 4096, 4096, null, out var width, out var height, out var positions), Is.True);

            Assert.That((shelfWidth, shelfHeight), Is.EqualTo((512, 256)));
            Assert.That((width, height), Is.EqualTo((256, 256)));
            AssertValid(sizes, positions, width, height, 32);
        }

        [Test]
        public void Solver_IsDeterministicAndRespectsTheCanvasPredicate()
        {
            var sizes = RandomSizes(77, 40, 32, 6);
            Func<int, int, bool> square = (width, height) => width == height;

            Solver(SharedRepackPackerKind.Best, 32).TryPack(sizes, 4096, 4096, square, out var width1, out var height1, out var first);
            Solver(SharedRepackPackerKind.Best, 32).TryPack(sizes, 4096, 4096, square, out var width2, out var height2, out var second);

            Assert.That(width1, Is.EqualTo(height1));
            Assert.That((width2, height2, second), Is.EqualTo((width1, height1, first)));
            AssertValid(sizes, first, width1, height1, 32);
        }

        [Test]
        public void Solver_SpentBudgetFallsBackToTheShelfResult()
        {
            (int Width, int Height)[] sizes = [(64, 160), (224, 96), (32, 192)];
            Solver(SharedRepackPackerKind.Shelf, 32).TryPack(sizes, 4096, 4096, null, out var shelfWidth, out var shelfHeight, out var shelf);

            var spent = Solver(SharedRepackPackerKind.Best, 32, budget: 1);
            Assert.That(spent.TryPack(sizes, 4096, 4096, null, out var width, out var height, out var positions), Is.True);

            Assert.That(spent.Budget.Exceeded, Is.True);
            Assert.That((width, height), Is.EqualTo((shelfWidth, shelfHeight)));
            Assert.That(positions, Is.EqualTo(shelf));
        }

        [Test]
        public void Solver_DerivesTheGridWhenTheUnitDoesNotDivideTheSizes()
        {
            (int Width, int Height)[] sizes = [(96, 64), (48, 48), (16, 80)];

            Assert.That(Solver(SharedRepackPackerKind.Best, 32).TryPack(sizes, 4096, 4096, null, out var width, out var height, out var positions), Is.True);

            AssertValid(sizes, positions, width, height, 16);
        }

        [Test]
        public void FindSplit_NeverCostsMoreThanTheShelfSearch_AndKeepsClustersWhole()
        {
            for (var seed = 90; seed < 105; seed++)
            {
                var sizes = RandomSizes(seed, 14 + seed % 6, 32, 8);
                var clusters = Enumerable.Range(0, sizes.Count).GroupBy(index => index / 3).Select(group => group.ToList()).ToList();
                var input = new SharedRepackSimInput
                {
                    Sizes = sizes,
                    ClusterOf = Enumerable.Range(0, sizes.Count).Select(index => index / 3).ToList(),
                    Unit = 32,
                    PageBytes = (width, height) => (long)width * height,
                };

                var shelfSolver = Solver(SharedRepackPackerKind.Shelf, 32);
                var bestSolver = Solver(SharedRepackPackerKind.Best, 32);
                shelfSolver.TryPack(sizes, 4096, 4096, null, out var limitWidth, out var limitHeight, out _);
                var shelf = SharedRepackGroupSimulator.FindSplit(input, shelfSolver, clusters, limitWidth, limitHeight, out var shelfBytes, out _);
                var best = SharedRepackGroupSimulator.FindSplit(input, bestSolver, clusters, limitWidth, limitHeight, out var bestBytes, out _);

                if (shelf == null)
                    continue;
                Assert.That(best, Is.Not.Null);
                Assert.That(bestBytes, Is.LessThanOrEqualTo(shelfBytes), $"seed {seed}");
                foreach (var cluster in clusters)
                    Assert.That(best!.Count(page => page.Contains(cluster[0])), Is.EqualTo(1));
                foreach (var cluster in clusters)
                    Assert.That(best!.Single(page => page.Contains(cluster[0])), Is.SupersetOf(cluster));
            }
        }
    }
}
