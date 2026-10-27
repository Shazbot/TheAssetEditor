using GameWorld.MeshOptimizer;

namespace Test.PackOptimizer.Core
{
    public class MeshOptSimplifierTests
    {
        [TestCase(0.5f)]
        [TestCase(0.25f)]
        public void Simplify_UvSphere_ReducesAndKeepsAValidRemap(float factor)
        {
            var mesh = Sphere(48, 24);

            var result = MeshOptSimplifier.Simplify(mesh.Positions, mesh.Normals, mesh.Uvs, mesh.Indices, factor);

            Assert.That(result.Stats.ReductionSkipped, Is.False, result.Stats.SkipReason);
            AssertValid(mesh, result);
            Assert.That(result.Stats.RequestedTriangleCount, Is.EqualTo((int)Math.Round(mesh.Indices.Length / 3 * (double)factor, MidpointRounding.AwayFromZero)));
            Assert.That(result.Stats.AchievedTriangleCount, Is.LessThan(result.Stats.SourceTriangleCount));
        }

        [Test]
        public void Simplify_SeamedCubeWithSplitVertices_ReducesWithoutChangingTheVertexData()
        {
            // Every face owns its vertices (hard normals and UV seams on every edge).
            var mesh = SeamedCube(10);

            var result = MeshOptSimplifier.Simplify(mesh.Positions, mesh.Normals, mesh.Uvs, mesh.Indices, 0.5f);

            Assert.That(result.Stats.ReductionSkipped, Is.False, result.Stats.SkipReason);
            AssertValid(mesh, result);
            Assert.That(result.Stats.AchievedTriangleCount, Is.LessThan(result.Stats.SourceTriangleCount));
        }

        [Test]
        public void Simplify_RepeatedIndexTriangles_AreDroppedAndTheMeshStillReduces()
        {
            var mesh = Sphere(32, 16);
            ushort[] indices = [.. mesh.Indices, 0, 0, 1, 4, 5, 5, 8, 8, 8];

            var result = MeshOptSimplifier.Simplify(mesh.Positions, mesh.Normals, mesh.Uvs, indices, 0.5f);

            Assert.That(result.Stats.ReductionSkipped, Is.False, result.Stats.SkipReason);
            Assert.That(result.Stats.DroppedDegenerateTriangles, Is.EqualTo(3));
            Assert.That(result.Stats.AchievedTriangleCount, Is.LessThan(result.Stats.SourceTriangleCount));
        }

        [Test]
        public void Simplify_ZeroAreaTriangleWithDistinctIndices_IsNotSkipped()
        {
            var mesh = Sphere(32, 16);
            var extra = (ushort)(mesh.Positions.Length / 3);
            float[] positions = [.. mesh.Positions, 20, 0, 0, 21, 0, 0, 22, 0, 0];
            float[] normals = [.. mesh.Normals, 0, 0, 1, 0, 0, 1, 0, 0, 1];
            float[] uvs = [.. mesh.Uvs, 0, 0, 0.5f, 0, 1, 0];
            ushort[] indices = [.. mesh.Indices, extra, (ushort)(extra + 1), (ushort)(extra + 2)];

            var result = MeshOptSimplifier.Simplify(positions, normals, uvs, indices, 0.5f);

            Assert.That(result.Stats.ReductionSkipped, Is.False, result.Stats.SkipReason);
            Assert.That(result.Stats.DroppedDegenerateTriangles, Is.Zero);
        }

        [Test]
        public void Simplify_OpenGridWithLockedBorder_KeepsEveryBorderVertex()
        {
            const int side = 18;
            var mesh = Grid(side);

            var result = MeshOptSimplifier.Simplify(mesh.Positions, mesh.Normals, mesh.Uvs, mesh.Indices, 0.5f);

            Assert.That(result.Stats.ReductionSkipped, Is.False, result.Stats.SkipReason);
            Assert.That(result.Stats.AchievedTriangleCount, Is.LessThan(result.Stats.SourceTriangleCount));
            for (var y = 0; y < side; y++)
            for (var x = 0; x < side; x++)
            {
                if (x != 0 && y != 0 && x != side - 1 && y != side - 1)
                    continue;

                Assert.That(result.Remap[y * side + x], Is.Not.EqualTo(uint.MaxValue), $"Border vertex ({x},{y}) was removed.");
            }
        }

        [Test]
        public void Simplify_FactorOfOne_ReturnsTheInputUnchanged()
        {
            var mesh = Sphere(16, 8);

            var result = MeshOptSimplifier.Simplify(mesh.Positions, mesh.Normals, mesh.Uvs, mesh.Indices, 1f);

            Assert.That(result.Stats.ReductionSkipped, Is.True);
            Assert.That(result.Indices, Is.EqualTo(mesh.Indices));
            Assert.That(result.VertexCount, Is.EqualTo(mesh.Positions.Length / 3));
            Assert.That(result.Remap, Is.EqualTo(Enumerable.Range(0, result.VertexCount).Select(x => (uint)x)));
        }

        [Test]
        public void Simplify_TooFewTriangles_IsSkippedWithAReason()
        {
            float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];

            var result = MeshOptSimplifier.Simplify(positions, [], [], [0, 1, 2], 0.5f);

            Assert.That(result.Stats.ReductionSkipped, Is.True);
            Assert.That(result.Stats.SkipReason, Is.Not.Null.And.Contains("Fewer than"));
        }

        [Test]
        public void Simplify_NonFiniteInput_IsSkippedAndNegativeFactorThrows()
        {
            var mesh = Sphere(16, 8);
            mesh.Positions[4] = float.NaN;

            var result = MeshOptSimplifier.Simplify(mesh.Positions, mesh.Normals, mesh.Uvs, mesh.Indices, 0.5f);

            Assert.That(result.Stats.ReductionSkipped, Is.True);
            Assert.That(result.Stats.SkipReason, Does.Contain("non-finite"));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                MeshOptSimplifier.Simplify(mesh.Positions, mesh.Normals, mesh.Uvs, mesh.Indices, -1f));
        }

        [Test]
        public void Simplify_WithoutNormalsAndUvs_StillReduces()
        {
            var mesh = Sphere(32, 16);

            var result = MeshOptSimplifier.Simplify(mesh.Positions, [], [], mesh.Indices, 0.5f);

            Assert.That(result.Stats.ReductionSkipped, Is.False, result.Stats.SkipReason);
            Assert.That(result.Stats.AchievedTriangleCount, Is.LessThan(result.Stats.SourceTriangleCount));
        }

        private static void AssertValid(Mesh source, MeshOptSimplifyResult result)
        {
            Assert.That(result.Indices.Length % 3, Is.Zero);
            Assert.That(result.Indices.All(x => x < result.VertexCount), Is.True);
            for (var i = 0; i < result.Indices.Length; i += 3)
            {
                Assert.That(result.Indices[i], Is.Not.EqualTo(result.Indices[i + 1]));
                Assert.That(result.Indices[i + 1], Is.Not.EqualTo(result.Indices[i + 2]));
                Assert.That(result.Indices[i + 2], Is.Not.EqualTo(result.Indices[i]));
            }

            var used = result.Remap.Count(x => x != uint.MaxValue);
            Assert.That(used, Is.EqualTo(result.VertexCount));
            Assert.That(result.Remap.Where(x => x != uint.MaxValue).Distinct().Count(), Is.EqualTo(used));
            Assert.That(result.Remap, Has.Length.EqualTo(source.Positions.Length / 3));
        }

        private sealed record Mesh(float[] Positions, float[] Normals, float[] Uvs, ushort[] Indices);

        private static Mesh Sphere(int longitudes, int latitudes)
        {
            var positions = new List<float>();
            var normals = new List<float>();
            var uvs = new List<float>();
            for (var lat = 0; lat <= latitudes; lat++)
            for (var lon = 0; lon <= longitudes; lon++)
            {
                var theta = Math.PI * lat / latitudes;
                var phi = 2 * Math.PI * lon / longitudes;
                var x = (float)(Math.Sin(theta) * Math.Cos(phi));
                var y = (float)Math.Cos(theta);
                var z = (float)(Math.Sin(theta) * Math.Sin(phi));
                positions.AddRange([x, y, z]);
                normals.AddRange([x, y, z]);
                uvs.AddRange([(float)lon / longitudes, (float)lat / latitudes]);
            }

            var indices = new List<ushort>();
            var stride = longitudes + 1;
            for (var lat = 0; lat < latitudes; lat++)
            for (var lon = 0; lon < longitudes; lon++)
            {
                var a = (ushort)(lat * stride + lon);
                var b = (ushort)(a + 1);
                var c = (ushort)(a + stride);
                var d = (ushort)(c + 1);
                indices.AddRange([a, c, b, b, c, d]);
            }

            return new Mesh([.. positions], [.. normals], [.. uvs], [.. indices]);
        }

        private static Mesh Grid(int side, float z = 0, float[]? normal = null, int uvScale = 1)
        {
            normal ??= [0, 0, 1];
            var positions = new List<float>();
            var normals = new List<float>();
            var uvs = new List<float>();
            for (var y = 0; y < side; y++)
            for (var x = 0; x < side; x++)
            {
                positions.AddRange([x, y, z]);
                normals.AddRange(normal);
                uvs.AddRange([(float)x / (side - 1) * uvScale, (float)y / (side - 1) * uvScale]);
            }

            var indices = new List<ushort>();
            for (var y = 0; y < side - 1; y++)
            for (var x = 0; x < side - 1; x++)
            {
                var a = (ushort)(y * side + x);
                indices.AddRange([a, (ushort)(a + 1), (ushort)(a + side), (ushort)(a + 1), (ushort)(a + side + 1), (ushort)(a + side)]);
            }

            return new Mesh([.. positions], [.. normals], [.. uvs], [.. indices]);
        }

        private static Mesh SeamedCube(int subdivisions)
        {
            var positions = new List<float>();
            var normals = new List<float>();
            var uvs = new List<float>();
            var indices = new List<ushort>();
            float[][] faceNormals = [[0, 0, 1], [0, 0, -1], [1, 0, 0], [-1, 0, 0], [0, 1, 0], [0, -1, 0]];
            foreach (var normal in faceNormals)
            {
                var offset = (ushort)(positions.Count / 3);
                var grid = Grid(subdivisions + 1);
                for (var i = 0; i < grid.Positions.Length / 3; i++)
                {
                    var u = grid.Positions[i * 3] / subdivisions - 0.5f;
                    var v = grid.Positions[i * 3 + 1] / subdivisions - 0.5f;
                    // Place the face grid on the cube side the normal points to.
                    float[] p = normal[2] != 0 ? [u, v, normal[2] * 0.5f]
                        : normal[0] != 0 ? [normal[0] * 0.5f, u, v]
                        : [u, normal[1] * 0.5f, v];
                    positions.AddRange(p);
                    normals.AddRange(normal);
                    uvs.AddRange([grid.Uvs[i * 2], grid.Uvs[i * 2 + 1]]);
                }

                indices.AddRange(grid.Indices.Select(x => (ushort)(x + offset)));
            }

            return new Mesh([.. positions], [.. normals], [.. uvs], [.. indices]);
        }
    }
}
