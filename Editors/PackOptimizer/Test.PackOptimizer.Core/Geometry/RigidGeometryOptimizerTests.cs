using Editors.PackOptimizer.Core;
using GameWorld.MeshOptimizer;
using System.Numerics;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.Vertex;

namespace Test.PackOptimizer.Core
{
    public class RigidGeometryOptimizerTests
    {
        [Test]
        public void Optimize_PreservesDegenerateTrianglesWhileRemovingUnusedAndDuplicateVertices()
        {
            var model = CreateModel(
                [
                    Vertex(0, 0, 0),
                    Vertex(1, 0, 0),
                    Vertex(0, 1, 0),
                    Vertex(0, 1, 0),
                    Vertex(5, 5, 5),
                ],
                [
                    0, 1, 2,
                    0, 1, 3,
                    0, 0, 1,
                ]);

            var statistics = RigidGeometryOptimizer.Optimize(model);

            Assert.Multiple(() =>
            {
                Assert.That(statistics.VerticesBefore, Is.EqualTo(5));
                Assert.That(statistics.VerticesAfter, Is.EqualTo(3));
                Assert.That(statistics.UnreferencedVerticesRemoved, Is.EqualTo(1));
                Assert.That(statistics.DuplicateVerticesRemoved, Is.EqualTo(1));
                Assert.That(statistics.IndicesBefore, Is.EqualTo(9));
                Assert.That(statistics.IndicesAfter, Is.EqualTo(9));
                Assert.That(model.Mesh.VertexList, Has.Length.EqualTo(3));
                Assert.That(model.Mesh.IndexList, Has.Length.EqualTo(9));
            });
        }

        [Test]
        public void Optimize_PreservesTriangleCountWhenExactVertexDeduplicationMakesTriangleIndexDegenerate()
        {
            var model = CreateModel(
                [
                    Vertex(0, 0, 0),
                    Vertex(1, 0, 0),
                    Vertex(0, 1, 0),
                    Vertex(0, 1, 0),
                ],
                [
                    0, 1, 2,
                    0, 2, 3,
                ]);

            var triangleCountBefore = model.Mesh.IndexList.Length / 3;

            var statistics = RigidGeometryOptimizer.Optimize(model);

            Assert.Multiple(() =>
            {
                Assert.That(statistics.DuplicateVerticesRemoved, Is.EqualTo(1));
                Assert.That(model.Mesh.IndexList.Length / 3, Is.EqualTo(triangleCountBefore));
                Assert.That(statistics.IndicesAfter, Is.EqualTo(statistics.IndicesBefore));
            });
        }

        [Test]
        public void Optimize_PreservesNonDegenerateTriangleGeometry()
        {
            var model = CreateModel(
                [
                    Vertex(0, 0, 0),
                    Vertex(1, 0, 0),
                    Vertex(0, 1, 0),
                    Vertex(1, 1, 0),
                ],
                [
                    0, 1, 2,
                    2, 1, 3,
                ]);

            var before = TrianglePositionKeys(model);

            var statistics = RigidGeometryOptimizer.Optimize(model);

            Assert.Multiple(() =>
            {
                Assert.That(model.Mesh.IndexList, Has.Length.EqualTo(6));
                Assert.That(TrianglePositionKeys(model), Is.EquivalentTo(before));
            });
        }

        [Test]
        public void Optimize_PreservesOrientedTrianglesAndFullVertexPayloadsWithNativeOrdering()
        {
            var vertices = new[]
            {
                Vertex(0, 0, 8, 1),
                Vertex(1, 0, 8, 2),
                Vertex(0, 1, 8, 3),
                Vertex(0, 0, 0, 4),
                Vertex(1, 0, 0, 5),
                Vertex(0, 1, 0, 6),
                Vertex(0, 0, 4, 7),
                Vertex(1, 0, 4, 8),
                Vertex(0, 1, 4, 9),
                Vertex(0, 0, 12, 10),
            };
            ushort[] indices =
            [
                6, 7, 8,
                0, 1, 2,
                3, 4, 5,
                9, 9, 0,
            ];
            var model = CreateModel(vertices, (ushort[])indices.Clone());
            var before = TrianglePayloadKeys(vertices, indices);

            var statistics = RigidGeometryOptimizer.Optimize(model);

            Assert.Multiple(() =>
            {
                Assert.That(statistics.IndicesAfter, Is.EqualTo(indices.Length));
                Assert.That(TrianglePayloadKeys(model), Is.EquivalentTo(before));
            });
        }

        [Test]
        public void NativeOrdering_ReturnsAnEmptyIndexBufferWithoutVertices()
        {
            var result = MeshOptTriangleOrderOptimizer.OptimizeVertexCacheAndOverdraw([], []);

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void NativeOrdering_PreservesSingleTriangleOrientation()
        {
            ushort[] indices = [2, 0, 1];
            float[] positions =
            [
                0, 0, 0,
                1, 0, 0,
                0, 1, 0,
            ];

            var result = MeshOptTriangleOrderOptimizer.OptimizeVertexCacheAndOverdraw(indices, positions);
            var signedArea = TriangleSignedArea(result, positions);

            Assert.Multiple(() =>
            {
                Assert.That(result, Has.Length.EqualTo(3));
                Assert.That(result, Is.EquivalentTo(indices));
                Assert.That(signedArea, Is.GreaterThan(0));
            });
        }

        [Test]
        public void NativeOrdering_RejectsMalformedAndOutOfRangeIndices()
        {
            float[] positions =
            [
                0, 0, 0,
                1, 0, 0,
                0, 1, 0,
            ];

            Assert.Multiple(() =>
            {
                Assert.Throws<ArgumentException>(() =>
                    MeshOptTriangleOrderOptimizer.OptimizeVertexCacheAndOverdraw([0, 1], positions));
                Assert.Throws<ArgumentException>(() =>
                    MeshOptTriangleOrderOptimizer.OptimizeVertexCacheAndOverdraw([0, 1, 3], positions));
            });
        }

        [Test]
        public void NativeOrdering_RejectsNonFinitePositions()
        {
            ushort[] indices = [0, 1, 2];
            float[] positions =
            [
                0, 0, 0,
                1, 0, 0,
                0, float.NaN, 0,
            ];

            Assert.Throws<ArgumentException>(() =>
                MeshOptTriangleOrderOptimizer.OptimizeVertexCacheAndOverdraw(indices, positions));
        }

        private static RmvModel CreateModel(CommonVertex[] vertices, ushort[] indices)
            => new()
            {
                Mesh = new RmvMesh
                {
                    VertexList = vertices,
                    IndexList = indices,
                },
            };

        private static CommonVertex Vertex(float x, float y, float z)
            => new()
            {
                Position = new Vector4(x, y, z, 1),
                Normal = Vector3.UnitZ,
                BiNormal = Vector3.UnitY,
                Tangent = Vector3.UnitX,
                Uv = Vector2.Zero,
                Uv1 = Vector2.Zero,
                Colour = Vector4.One,
                BoneIndex = [],
                BoneWeight = [],
                WeightCount = 0,
            };

        private static CommonVertex Vertex(float x, float y, float z, byte payloadId)
        {
            var payload = payloadId;
            return new CommonVertex
            {
                Position = new Vector4(x, y, z, 1 + payload),
                Normal = new Vector3(payload, payload + 1, payload + 2),
                BiNormal = new Vector3(payload + 3, payload + 4, payload + 5),
                Tangent = new Vector3(payload + 6, payload + 7, payload + 8),
                Uv = new Vector2(payload, payload + 0.25f),
                Uv1 = new Vector2(payload + 0.5f, payload + 0.75f),
                Colour = new Vector4(payload, payload + 1, payload + 2, payload + 3),
                BoneIndex = [payloadId],
                BoneWeight = [payload + 0.125f],
                WeightCount = 1,
            };
        }

        private static string[] TrianglePayloadKeys(RmvModel model)
            => TrianglePayloadKeys(model.Mesh.VertexList, model.Mesh.IndexList);

        private static string[] TrianglePayloadKeys(
            IReadOnlyList<CommonVertex> vertices,
            IReadOnlyList<ushort> indices)
        {
            var result = new string[indices.Count / 3];
            for (var i = 0; i < indices.Count; i += 3)
            {
                result[i / 3] = string.Join("|",
                    VertexPayloadKey(vertices[indices[i]]),
                    VertexPayloadKey(vertices[indices[i + 1]]),
                    VertexPayloadKey(vertices[indices[i + 2]]));
            }

            return result;
        }

        private static string VertexPayloadKey(CommonVertex vertex)
        {
            var boneIndices = vertex.BoneIndex == null
                ? "<null>"
                : Convert.ToHexString(vertex.BoneIndex);
            var boneWeights = vertex.BoneWeight == null
                ? "<null>"
                : string.Join(",", vertex.BoneWeight.Select(FloatBits));

            return string.Join("/",
                VectorKey(vertex.Position),
                VectorKey(vertex.Normal),
                VectorKey(vertex.BiNormal),
                VectorKey(vertex.Tangent),
                VectorKey(vertex.Uv),
                VectorKey(vertex.Uv1),
                VectorKey(vertex.Colour),
                boneIndices,
                boneWeights,
                vertex.WeightCount.ToString());
        }

        private static string VectorKey(Vector2 value)
            => $"{FloatBits(value.X)},{FloatBits(value.Y)}";

        private static string VectorKey(Vector3 value)
            => $"{FloatBits(value.X)},{FloatBits(value.Y)},{FloatBits(value.Z)}";

        private static string VectorKey(Vector4 value)
            => $"{FloatBits(value.X)},{FloatBits(value.Y)},{FloatBits(value.Z)},{FloatBits(value.W)}";

        private static int FloatBits(float value)
            => BitConverter.SingleToInt32Bits(value);

        private static float TriangleSignedArea(IReadOnlyList<ushort> indices, IReadOnlyList<float> positions)
        {
            var first = indices[0] * 3;
            var second = indices[1] * 3;
            var third = indices[2] * 3;
            return (positions[second] - positions[first]) * (positions[third + 1] - positions[first + 1]) -
                   (positions[second + 1] - positions[first + 1]) * (positions[third] - positions[first]);
        }

        private static string[] TrianglePositionKeys(RmvModel model)
        {
            var result = new List<string>();
            for (var i = 0; i < model.Mesh.IndexList.Length; i += 3)
            {
                var triangle = new[]
                {
                    PositionKey(model.Mesh.VertexList[model.Mesh.IndexList[i]]),
                    PositionKey(model.Mesh.VertexList[model.Mesh.IndexList[i + 1]]),
                    PositionKey(model.Mesh.VertexList[model.Mesh.IndexList[i + 2]]),
                };
                result.Add(string.Join("|", triangle));
            }

            return result.ToArray();
        }

        private static string PositionKey(CommonVertex vertex)
            => $"{vertex.Position.X},{vertex.Position.Y},{vertex.Position.Z}";
    }
}
