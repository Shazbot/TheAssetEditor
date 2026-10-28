using System.Numerics;
using System.Runtime.InteropServices;
using Shared.ByteParsing;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.Vertex;
using Shared.GameFormats.RigidModel.Vertex.Formats;

#pragma warning disable CS0649
#pragma warning disable CS0649
namespace Test.PackOptimizer.Core
{
    /// <summary>
    /// Pins the span based vertex readers to the original per-vertex marshalling readers
    /// (kept here as the reference) bit for bit.
    /// </summary>
    public class VertexReaderEquivalenceTests
    {
        private const int VertexCount = 4000;
        private static readonly ushort[] SpecialHalves = [0x0000, 0x8000, 0x7C00, 0xFC00, 0x7E00, 0xFE00, 0x7C01, 0xFFFF, 0x0001, 0x3C00, 0x7BFF];
        private static readonly uint[] SpecialFloats = [0u, 0x80000000u, 0x7F800000u, 0xFF800000u, 0x7FC00000u, 0xFFC00001u, 0x7F800001u, 0xFFFFFFFFu, 0x00000001u, 0x3F800000u];

        // Private copy of the old StaticVertex marshalling layout.
        private struct StaticData
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] position;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public byte[] uv;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public byte[] uvExtra;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public byte[] normal;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public byte[] tangent;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public byte[] biNormal;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public byte[] RGBA;
        }

        private static Vector3 SwapXZ(Vector3 v) => new(v.Z, v.Y, v.X);

        private static CommonVertex OldStatic(byte[] buffer, int offset)
        {
            var d = ByteHelper.ByteArrayToStructure<StaticData>(buffer, offset);
            return new CommonVertex()
            {
                Position = VertexLoadHelper.CreatVector4HalfFloat(d.position).ToVector4(1),
                Normal = SwapXZ(VertexLoadHelper.CreatVector4Byte(d.normal).ToVector3()),
                Tangent = SwapXZ(VertexLoadHelper.CreatVector4Byte(d.tangent).ToVector3()),
                BiNormal = SwapXZ(VertexLoadHelper.CreatVector4Byte(d.biNormal).ToVector3()),
                Uv = VertexLoadHelper.CreatVector2HalfFloat(d.uv).ToVector2(),
                Uv1 = VertexLoadHelper.CreatVector2HalfFloat(d.uvExtra).ToVector2(),
                Colour = VertexLoadHelper.CreatVector4Byte(d.RGBA).ToVector4(),
                BoneIndex = new byte[0],
                BoneWeight = new float[0]
            };
        }

        private static CommonVertex OldFloatFormat(Vector4 position, Vector3 normal) => new()
        {
            Position = position,
            Normal = normal,
            BiNormal = Vector3.UnitY,
            Tangent = Vector3.UnitY,
            Uv = Vector2.Zero,
            BoneIndex = new byte[] { },
            BoneWeight = new float[] { },
            WeightCount = 0
        };

        private static byte[] RandomBuffer(int vertexSize, int seed)
        {
            var random = new Random(seed);
            var buffer = new byte[vertexSize * VertexCount + 5];
            random.NextBytes(buffer);
            for (var v = 0; v < VertexCount; v++)
            {
                var start = v * vertexSize;
                for (var k = 0; k < 3; k++)
                {
                    if (random.Next(2) == 0)
                    {
                        var slot = start + 2 * random.Next(vertexSize / 2);
                        BitConverter.TryWriteBytes(buffer.AsSpan(slot, 2), SpecialHalves[random.Next(SpecialHalves.Length)]);
                    }
                    if (random.Next(2) == 0 && vertexSize >= 4)
                    {
                        var slot = start + 4 * random.Next(vertexSize / 4);
                        BitConverter.TryWriteBytes(buffer.AsSpan(slot, 4), SpecialFloats[random.Next(SpecialFloats.Length)]);
                    }
                }
            }
            return buffer;
        }

        private static int Bits(float f) => BitConverter.SingleToInt32Bits(f);

        private static void AssertSame(CommonVertex expected, CommonVertex actual, string context)
        {
            void V4(Vector4 e, Vector4 a, string n) => Assert.That((Bits(a.X), Bits(a.Y), Bits(a.Z), Bits(a.W)), Is.EqualTo((Bits(e.X), Bits(e.Y), Bits(e.Z), Bits(e.W))), $"{context} {n}");
            void V3(Vector3 e, Vector3 a, string n) => Assert.That((Bits(a.X), Bits(a.Y), Bits(a.Z)), Is.EqualTo((Bits(e.X), Bits(e.Y), Bits(e.Z))), $"{context} {n}");
            void V2(Vector2 e, Vector2 a, string n) => Assert.That((Bits(a.X), Bits(a.Y)), Is.EqualTo((Bits(e.X), Bits(e.Y))), $"{context} {n}");

            V4(expected.Position, actual.Position, "Position");
            V3(expected.Normal, actual.Normal, "Normal");
            V3(expected.Tangent, actual.Tangent, "Tangent");
            V3(expected.BiNormal, actual.BiNormal, "BiNormal");
            V2(expected.Uv, actual.Uv, "Uv");
            V2(expected.Uv1, actual.Uv1, "Uv1");
            V4(expected.Colour, actual.Colour, "Colour");
            Assert.That(actual.BoneIndex, Is.EqualTo(expected.BoneIndex), context);
            Assert.That(actual.BoneWeight, Is.EqualTo(expected.BoneWeight), context);
            Assert.That(actual.WeightCount, Is.EqualTo(expected.WeightCount), context);
        }

        [Test]
        public void StaticVertexMatchesMarshalledReader()
        {
            var creator = new StaticVertexCreator();
            var size = (int)creator.GetVertexSize(RmvVersionEnum.RMV2_V8);
            Assert.That(size, Is.EqualTo(32));
            var buffer = RandomBuffer(size, 1);
            var actual = creator.ReadArray(RmvVersionEnum.RMV2_V8, buffer, 3, size, VertexCount);
            for (var i = 0; i < VertexCount; i++)
                AssertSame(OldStatic(buffer, 3 + i * size), actual[i], $"static #{i}");
        }

        private static CommonVertex OldPositionNormal<TData>(byte[] buffer, int offset, Func<TData, byte[]> position, Func<TData, byte[]>? normal) where TData : struct
        {
            var d = ByteHelper.ByteArrayToStructure<TData>(buffer, offset);
            return OldFloatFormat(
                VertexLoadHelper.CreatVector4Float(position(d)).ToVector4(1),
                normal == null ? Vector3.UnitY : VertexLoadHelper.CreatVector4Float(normal(d)).ToVector3());
        }

        [Test]
        public void TerrainAndCollisionVerticesMatchMarshalledReader()
        {
            var cases = new (IVertexCreator Creator, Func<byte[], int, CommonVertex> Old)[]
            {
                (new Position16_bitVertexCreator(), (b, o) => OldPositionNormal<Position16_bitVertexCreator.Data>(b, o, d => d.position, null)),
                (new CustomTerrainVertexCreator(), (b, o) => OldPositionNormal<CustomTerrainVertexCreator.Data>(b, o, d => d.position, d => d.normal)),
                (new CustomTerrain2VertexCreator(), (b, o) => OldPositionNormal<CustomTerrain2VertexCreator.Data>(b, o, d => d.position, d => d.normal)),
                (new CollisionVertexCreator(), (b, o) => OldPositionNormal<CollisionVertexCreator.Data>(b, o, d => d.position, null)),
            };

            foreach (var (creator, old) in cases)
            {
                var size = (int)creator.GetVertexSize(RmvVersionEnum.RMV2_V8);
                var buffer = RandomBuffer(size, 2);
                var actual = creator.ReadArray(RmvVersionEnum.RMV2_V8, buffer, 3, size, VertexCount);
                for (var i = 0; i < VertexCount; i++)
                    AssertSame(old(buffer, 3 + i * size), actual[i], $"{creator.GetType().Name} #{i}");
            }
        }

        [Test]
        public void ShortBufferStillThrows()
        {
            IVertexCreator[] creators = [new StaticVertexCreator(), new Position16_bitVertexCreator(), new CustomTerrainVertexCreator(), new CustomTerrain2VertexCreator(), new CollisionVertexCreator()];
            foreach (var creator in creators)
            {
                var size = (int)creator.GetVertexSize(RmvVersionEnum.RMV2_V8);
                var buffer = new byte[size - 1];
                Assert.Throws<Exception>(() => creator.ReadArray(RmvVersionEnum.RMV2_V8, buffer, 0, size, 1), creator.GetType().Name);
            }
        }
    }
}
