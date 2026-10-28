using System.Numerics;
using Shared.GameFormats.RigidModel.Vertex;

namespace Test.PackOptimizer.Core
{
    /// <summary>
    /// Pins the vectorized ConvertertVertexToHalfExtraPrecision to the original brute-force
    /// implementation (kept here as the reference) bit for bit.
    /// </summary>
    public class VertexHalfExtraPrecisionTests
    {
        private record struct Outcome(bool Threw, ushort X, ushort Y, ushort Z, ushort W);

        [Test]
        public void MatchesReferenceOnRandomInputs()
        {
            var random = new Random(12345);
            for (var i = 0; i < 20000; i++)
            {
                var scale = MathF.Pow(10f, random.Next(-8, 6));
                AssertSame(new Vector4(
                    (float)(random.NextDouble() * 2 - 1) * scale,
                    (float)(random.NextDouble() * 2 - 1) * scale,
                    (float)(random.NextDouble() * 2 - 1) * scale,
                    0));
            }
        }

        [Test]
        public void MatchesReferenceOnRandomBitPatterns()
        {
            var random = new Random(999);
            for (var i = 0; i < 20000; i++)
            {
                AssertSame(new Vector4(RandomBits(random), RandomBits(random), RandomBits(random), 0));
            }
        }

        [Test]
        public void MatchesReferenceOnEdgeInputs()
        {
            float[] edges =
            {
                0f, -0f, 1f, -1f, 1e-45f, -1e-45f, 1e-38f, 5.96e-8f, 6.1e-5f, 6.103515625e-05f, 6.1035156e-05f,
                1e-5f, -3e-6f, 0.5f, 1.999f, 2f, 1023.5f, 65504f, 65503.9f, 65519f, 65520f, 65535f, 65536f, 70000f,
                -65504f, -65536f, -1e30f, 1e30f, float.MaxValue, float.MinValue, float.Epsilon,
                float.NaN, float.PositiveInfinity, float.NegativeInfinity, MathF.BitDecrement(65504f), MathF.BitIncrement(65504f)
            };

            foreach (var x in edges)
            foreach (var y in edges)
            foreach (var z in new[] { 0f, 0.001f, -7.5f, 65504f, float.NaN, float.NegativeInfinity, 1e-7f })
                AssertSame(new Vector4(x, y, z, 0));
        }

        [Test]
        public void ScalarFallbackMatchesVectorized()
        {
            var random = new Random(4242);
            for (var i = 0; i < 5000; i++)
            {
                var x = RandomBits(random);
                var y = RandomBits(random);
                var z = (float)(random.NextDouble() * 100 - 50);
                Assert.That(
                    BitConverter.SingleToUInt32Bits(VertexLoadHelper.FindBestW(x, y, z)),
                    Is.EqualTo(BitConverter.SingleToUInt32Bits(VertexLoadHelper.FindBestWScalar(x, y, z))));
            }
        }

        private static float RandomBits(Random random)
        {
            var bits = (uint)random.NextInt64(0, uint.MaxValue + 1L);
            return BitConverter.UInt32BitsToSingle(bits);
        }

        private static void AssertSame(Vector4 input)
        {
            var expected = Reference(input);
            var actual = Actual(input);
            Assert.That(actual, Is.EqualTo(expected), $"input {input.X:R} {input.Y:R} {input.Z:R}");
        }

        private static Outcome Actual(Vector4 input)
        {
            try
            {
                var v = VertexLoadHelper.ConvertertVertexToHalfExtraPrecision(input);
                return new Outcome(false, BitConverter.HalfToUInt16Bits(v.X), BitConverter.HalfToUInt16Bits(v.Y), BitConverter.HalfToUInt16Bits(v.Z), BitConverter.HalfToUInt16Bits(v.W));
            }
            catch (Exception)
            {
                return new Outcome(true, 0, 0, 0, 0);
            }
        }

        private static Outcome Reference(Vector4 input)
        {
            try
            {
                var v = ReferenceEncode(input);
                return new Outcome(false, BitConverter.HalfToUInt16Bits(v.X), BitConverter.HalfToUInt16Bits(v.Y), BitConverter.HalfToUInt16Bits(v.Z), BitConverter.HalfToUInt16Bits(v.W));
            }
            catch (Exception)
            {
                return new Outcome(true, 0, 0, 0, 0);
            }
        }

        // Verbatim copy of the original implementation.
        private static (Half X, Half Y, Half Z, Half W) ReferenceEncode(Vector4 vertexOriginal)
        {
            const uint halfMantissaMax = 1024;
            float bestValueForW = 0;
            float currentSmallestError = float.MaxValue;

            for (ushort iMantissaCounter = 0; iMantissaCounter < halfMantissaMax; iMantissaCounter++)
            {
                if (vertexOriginal.X >= (float)Half.MaxValue || vertexOriginal.Y >= (float)Half.MaxValue || vertexOriginal.Z >= (float)Half.MaxValue)
                    throw new Exception("Input vertex cannot be converted to float16");

                float testW = (float)(1.0 + ((float)iMantissaCounter / 1024.0f));
                var normalizedVertex = new Vector3()
                {
                    X = vertexOriginal.X / testW,
                    Y = vertexOriginal.Y / testW,
                    Z = vertexOriginal.Z / testW
                };

                if (Math.Abs(normalizedVertex.X) == float.PositiveInfinity ||
                    Math.Abs(normalizedVertex.Y) == float.PositiveInfinity ||
                    Math.Abs(normalizedVertex.Z) == float.PositiveInfinity)
                    throw new Exception("Result is out range for conversin to float16");

                var hx = VertexLoadHelper.ConvertFloatToHalf(normalizedVertex.X);
                var hy = VertexLoadHelper.ConvertFloatToHalf(normalizedVertex.Y);
                var hz = VertexLoadHelper.ConvertFloatToHalf(normalizedVertex.Z);
                var hw = VertexLoadHelper.ConvertFloatToHalf(testW);

                var recoveredVertex = new Vector3()
                {
                    X = (float)hx * (float)hw,
                    Y = (float)hy * (float)hw,
                    Z = (float)hz * (float)hw
                };

                float error =
                    Math.Abs(vertexOriginal.X - recoveredVertex.X) +
                    Math.Abs(vertexOriginal.Y - recoveredVertex.Y) +
                    Math.Abs(vertexOriginal.Z - recoveredVertex.Z);

                if (error < currentSmallestError)
                {
                    currentSmallestError = error;
                    bestValueForW = testW;
                }
            }

            var nx = vertexOriginal.X / bestValueForW;
            var ny = vertexOriginal.Y / bestValueForW;
            var nz = vertexOriginal.Z / bestValueForW;
            return (VertexLoadHelper.ConvertFloatToHalf(nx), VertexLoadHelper.ConvertFloatToHalf(ny),
                VertexLoadHelper.ConvertFloatToHalf(nz), VertexLoadHelper.ConvertFloatToHalf(bestValueForW));
        }
    }
}
