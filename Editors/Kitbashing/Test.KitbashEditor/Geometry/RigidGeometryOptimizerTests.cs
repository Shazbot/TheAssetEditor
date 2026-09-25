using Editors.KitbasherEditor.Services;
using Microsoft.Xna.Framework;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.Vertex;

namespace Test.KitbashEditor.Geometry
{
    public class RigidGeometryOptimizerTests
    {
        [Test]
        public void Optimize_RemovesUnusedDegenerateAndDuplicateVertices()
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
                Assert.That(statistics.DegenerateTrianglesRemoved, Is.EqualTo(1));
                Assert.That(statistics.IndicesBefore, Is.EqualTo(9));
                Assert.That(statistics.IndicesAfter, Is.EqualTo(6));
                Assert.That(model.Mesh.VertexList, Has.Length.EqualTo(3));
                Assert.That(model.Mesh.IndexList, Has.Length.EqualTo(6));
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
                Assert.That(statistics.DegenerateTrianglesRemoved, Is.Zero);
                Assert.That(model.Mesh.IndexList, Has.Length.EqualTo(6));
                Assert.That(TrianglePositionKeys(model), Is.EquivalentTo(before));
            });
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
