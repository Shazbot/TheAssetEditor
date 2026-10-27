using Editors.PackOptimizer.Core;

namespace Test.PackOptimizer.Core
{
    public class SharedRepackMaskPackerTests
    {
        private static SharedRepackMask Shape(int width, int height, params (int X, int Y)[] cells)
        {
            var mask = new SharedRepackMask(width, height);
            foreach (var (x, y) in cells)
                mask.Set(x, y);
            return mask;
        }

        private static (int Width, int Height)? Box(int right, int bottom) => (right, bottom);

        [Test]
        public void InterlockingLShapesCutASmallerCanvasThanTheirRectangles()
        {
            const int unit = 8;
            // Two 2x2 rectangles with an L of three cells each: together they tile a 3x2 box.
            var masks = new[]
            {
                Shape(2, 2, (0, 0), (0, 1), (1, 1)),
                Shape(2, 2, (0, 0), (1, 0), (1, 1)),
            };
            var rectangles = new[] { SharedRepackMask.Filled(2, 2), SharedRepackMask.Filled(2, 2) };

            Assert.That(SharedRepackMaskPacker.TryPackMinimumTrimmedCanvas(
                masks, unit, 8 * unit, 8 * unit, Box, 8, long.MaxValue, null,
                out var width, out var height, out var positions), Is.True);
            Assert.That(SharedRepackMaskPacker.TryPackMinimumTrimmedCanvas(
                rectangles, unit, 8 * unit, 8 * unit, Box, 8, long.MaxValue, null,
                out var rectangleWidth, out var rectangleHeight, out _), Is.True);

            Assert.That((long)width * height, Is.EqualTo(3L * 2 * unit * unit));
            Assert.That((long)rectangleWidth * rectangleHeight, Is.EqualTo(4L * 2 * unit * unit));
            Assert.That(positions[0], Is.EqualTo((0, 0)));
            Assert.That(positions[1], Is.EqualTo((unit, 0)));
        }

        [Test]
        public void PositionsStayOnTheUnitGridAndMasksNeverOverlap()
        {
            const int unit = 16;
            var random = new Random(7);
            var masks = new List<SharedRepackMask>();
            for (var index = 0; index < 25; index++)
            {
                var mask = new SharedRepackMask(random.Next(1, 7), random.Next(1, 7));
                for (var y = 0; y < mask.Height; y++)
                {
                    for (var x = 0; x < mask.Width; x++)
                    {
                        if (random.Next(3) != 0)
                            mask.Set(x, y);
                    }
                }

                mask.Set(0, 0);
                masks.Add(mask);
            }

            Assert.That(SharedRepackMaskPacker.TryPackMinimumTrimmedCanvas(
                masks, unit, 80 * unit, 80 * unit, Box, 6, long.MaxValue, null,
                out var width, out var height, out var positions), Is.True);

            var owner = new Dictionary<(int, int), int>();
            for (var index = 0; index < masks.Count; index++)
            {
                Assert.That(positions[index].X % unit, Is.Zero);
                Assert.That(positions[index].Y % unit, Is.Zero);
                for (var y = 0; y < masks[index].Height; y++)
                {
                    for (var x = 0; x < masks[index].Width; x++)
                    {
                        if (!masks[index].Get(x, y))
                            continue;
                        var cell = (positions[index].X / unit + x, positions[index].Y / unit + y);
                        Assert.That(owner.TryAdd(cell, index), Is.True, $"{index} overlaps {owner.GetValueOrDefault(cell)}");
                        Assert.That((cell.Item1 + 1) * unit, Is.LessThanOrEqualTo(width));
                        Assert.That((cell.Item2 + 1) * unit, Is.LessThanOrEqualTo(height));
                    }
                }
            }
        }

        [Test]
        public void ACanvasIsOnlyTakenBelowTheAreaLimit()
        {
            const int unit = 8;
            var masks = new[] { SharedRepackMask.Filled(3, 2) };
            Assert.That(SharedRepackMaskPacker.TryPackMinimumTrimmedCanvas(
                masks, unit, 8 * unit, 8 * unit, Box, 4, 3L * 2 * unit * unit, null, out _, out _, out _), Is.False);
            Assert.That(SharedRepackMaskPacker.TryPackMinimumTrimmedCanvas(
                masks, unit, 8 * unit, 8 * unit, Box, 4, 3L * 2 * unit * unit + 1, null, out _, out _, out _), Is.True);
        }

        private static List<SharedRepackMask> RandomMasks(int seed, int count)
        {
            var random = new Random(seed);
            var masks = new List<SharedRepackMask>();
            for (var index = 0; index < count; index++)
            {
                var mask = new SharedRepackMask(random.Next(1, 9), random.Next(1, 9));
                for (var y = 0; y < mask.Height; y++)
                {
                    for (var x = 0; x < mask.Width; x++)
                    {
                        if (random.Next(3) != 0)
                            mask.Set(x, y);
                    }
                }

                mask.Set(0, 0);
                masks.Add(mask);
            }

            return masks;
        }

        [Test]
        public void ContactPlacementFillsANotchWhereFirstFitExtendsTheBox()
        {
            // A 3 x 3 block with a notch at (2, 1), then a single cell: the first fit puts the cell right of
            // the block at (3, 0) (box 4 x 3), contact puts it into the notch (three neighbours, box 3 x 3).
            var cells = new List<(int X, int Y)>();
            for (var y = 0; y < 3; y++)
            {
                for (var x = 0; x < 3; x++)
                {
                    if (x != 2 || y != 1)
                        cells.Add((x, y));
                }
            }

            var masks = new[] { Shape(3, 3, cells.ToArray()), SharedRepackMask.Filled(1, 1) };
            var areas = masks.Select(mask => mask.Area()).ToArray();

            Assert.That(SharedRepackMaskPacker.TryPack(
                masks, areas, 0, 5, 5, null, out var firstFit, out var firstRight, out var firstBottom), Is.True);
            Assert.That(SharedRepackMaskPacker.TryPack(
                masks, areas, 0, 5, 5, null, out var contact, out var right, out var bottom, SharedRepackMaskPacker.Contact), Is.True);

            Assert.That(firstFit[1], Is.EqualTo((3, 0)));
            Assert.That(firstRight * firstBottom, Is.EqualTo(12));
            Assert.That(contact[1], Is.EqualTo((2, 1)));
            Assert.That(right * bottom, Is.EqualTo(9));
        }

        [Test]
        public void EveryRuleAndOrderIsDeterministicOnTheGridWithoutOverlaps()
        {
            var masks = RandomMasks(11, 30);
            var areas = masks.Select(mask => mask.Area()).ToArray();
            foreach (var rule in new[] { SharedRepackMaskPacker.FirstFit, SharedRepackMaskPacker.Contact })
            {
                for (var mode = 0; mode < SharedRepackMaskPacker.OrderCount + SharedRepackMaskPacker.ShuffleCount; mode++)
                {
                    Assert.That(SharedRepackMaskPacker.TryPack(
                        masks, areas, mode, 40, 200, null, out var first, out var firstRight, out var firstBottom, rule), Is.True);
                    Assert.That(SharedRepackMaskPacker.TryPack(
                        masks, areas, mode, 40, 200, null, out var second, out var secondRight, out var secondBottom, rule), Is.True);
                    Assert.That(second, Is.EqualTo(first));
                    Assert.That((secondRight, secondBottom), Is.EqualTo((firstRight, firstBottom)));

                    var owner = new HashSet<(int, int)>();
                    for (var index = 0; index < masks.Count; index++)
                    {
                        for (var y = 0; y < masks[index].Height; y++)
                        {
                            for (var x = 0; x < masks[index].Width; x++)
                            {
                                if (!masks[index].Get(x, y))
                                    continue;
                                var cell = (first[index].X + x, first[index].Y + y);
                                Assert.That(owner.Add(cell), Is.True, $"rule {rule} order {mode}: mask {index} overlaps");
                                Assert.That(cell.Item1, Is.LessThan(firstRight));
                                Assert.That(cell.Item2, Is.LessThan(firstBottom));
                            }
                        }
                    }
                }
            }
        }

        [Test]
        public void TheMinimumCanvasSearchIsDeterministicAndNeverWorseThanFirstFit()
        {
            const int unit = 8;
            var masks = RandomMasks(5, 40);
            Assert.That(SharedRepackMaskPacker.TryPackMinimumTrimmedCanvas(
                masks, unit, 64 * unit, 64 * unit, Box, 8, long.MaxValue, null,
                out var width, out var height, out var positions), Is.True);
            Assert.That(SharedRepackMaskPacker.TryPackMinimumTrimmedCanvas(
                masks, unit, 64 * unit, 64 * unit, Box, 8, long.MaxValue, null,
                out var againWidth, out var againHeight, out var againPositions), Is.True);
            Assert.That((againWidth, againHeight), Is.EqualTo((width, height)));
            Assert.That(againPositions, Is.EqualTo(positions));

            var saved = SharedRepackMaskPacker.VariantLimit;
            try
            {
                SharedRepackMaskPacker.VariantLimit = SharedRepackMaskPacker.OrderCount;
                Assert.That(SharedRepackMaskPacker.TryPackMinimumTrimmedCanvas(
                    masks, unit, 64 * unit, 64 * unit, Box, 8, long.MaxValue, null,
                    out var firstFitWidth, out var firstFitHeight, out _), Is.True);
                Assert.That((long)width * height, Is.LessThanOrEqualTo((long)firstFitWidth * firstFitHeight));
            }
            finally
            {
                SharedRepackMaskPacker.VariantLimit = saved;
            }
        }
    }
}
