using System.Numerics;
using Editors.PackOptimizer.Core;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.LodHeader;
using Shared.GameFormats.RigidModel.Vertex;

namespace Test.PackOptimizer.Core
{
    public class EditorLodDetectorTests
    {
        private const string VanillaDump = @"K:\projects\wh3dump";

        [Test]
        public void Factor_FollowsTheEditorsLerpSchedule()
        {
            Assert.Multiple(() =>
            {
                Assert.That(EditorLodDetector.Factor(1, 4), Is.EqualTo(0.75).Within(1e-9));
                Assert.That(EditorLodDetector.Factor(2, 4), Is.EqualTo(0.5).Within(1e-9));
                Assert.That(EditorLodDetector.Factor(3, 4), Is.EqualTo(0.25).Within(1e-9));
                Assert.That(EditorLodDetector.Factor(1, 5), Is.EqualTo(0.8125).Within(1e-9));
                Assert.That(EditorLodDetector.Factor(1, 2), Is.EqualTo(0.25).Within(1e-9));
            });
        }

        [Test]
        public void Analyze_VertexCountTargetOnEveryLod_IsEditorGenerated()
        {
            // 2000 vertices but 4000 triangles, so a vertex-count target and a triangle-count target are far apart.
            var rigid = Rigid(vertexCount: 2000, triangleCount: 4000, lodTriangles: [Ceil(0.75 * 2000), Ceil(0.5 * 2000), Ceil(0.25 * 2000)]);

            var verdict = EditorLodDetector.Analyze(rigid);

            Assert.Multiple(() =>
            {
                Assert.That(verdict.IsEditorGenerated, Is.True, verdict.Reason);
                Assert.That(verdict.Evidence, Is.EqualTo(EditorLodEvidence.VertexCountTarget));
                Assert.That(verdict.Signals.Levels.Select(x => x.Triangles), Is.EqualTo(new[] { 1500, 1000, 500 }));
            });
        }

        [Test]
        public void Analyze_VertexCountTargetOnTwoMergedParts_UsesTheSumOfTheLod0Parts()
        {
            var rigid = Rigid(vertexCount: 1000, triangleCount: 2000, lodTriangles: [750, 500, 250], parts: 2);

            Assert.That(EditorLodDetector.Analyze(rigid).Evidence, Is.EqualTo(EditorLodEvidence.VertexCountTarget));
        }

        [Test]
        public void Analyze_TriangleScheduleWithoutMarkers_IsReportedButNotAsserted()
        {
            var rigid = Rigid(vertexCount: 2000, triangleCount: 4000, lodTriangles: [3000, 2000, 1000]);

            var verdict = EditorLodDetector.Analyze(rigid);

            Assert.Multiple(() =>
            {
                Assert.That(verdict.IsEditorGenerated, Is.False);
                Assert.That(verdict.Evidence, Is.EqualTo(EditorLodEvidence.ScheduleOnly));
            });
        }

        [Test]
        public void Analyze_TriangleScheduleWithMirroringLostInTheLastLod_IsEditorGenerated()
        {
            var rigid = Rigid(vertexCount: 2000, triangleCount: 4000, lodTriangles: [3000, 2000, 1000]);
            foreach (var vertex in rigid.ModelList[0][0].Mesh.VertexList.Take(200))
                vertex.BiNormal = -vertex.BiNormal;

            var verdict = EditorLodDetector.Analyze(rigid);

            Assert.Multiple(() =>
            {
                Assert.That(verdict.IsEditorGenerated, Is.True);
                Assert.That(verdict.Evidence, Is.EqualTo(EditorLodEvidence.ScheduleWithLostMirroring));
            });

            // Mirroring still present in the last LOD: a CA-style chain, report only.
            foreach (var vertex in rigid.ModelList[3][0].Mesh.VertexList.Take(10))
                vertex.BiNormal = -vertex.BiNormal;
            Assert.That(EditorLodDetector.Analyze(rigid).Evidence, Is.EqualTo(EditorLodEvidence.ScheduleOnly));

            // Too little mirroring in LOD0 (under 2%).
            var few = Rigid(vertexCount: 2000, triangleCount: 4000, lodTriangles: [3000, 2000, 1000]);
            foreach (var vertex in few.ModelList[0][0].Mesh.VertexList.Take(20))
                vertex.BiNormal = -vertex.BiNormal;
            Assert.That(EditorLodDetector.Analyze(few).Evidence, Is.EqualTo(EditorLodEvidence.ScheduleOnly));
        }

        [Test]
        public void Analyze_TriangleScheduleWithEditorHeaderPadding_IsEditorGenerated()
        {
            var rigid = Rigid(vertexCount: 2000, triangleCount: 4000, lodTriangles: [3000, 2000, 1000]);
            rigid.LodHeaders = Enumerable.Range(0, 4).Select(i => new Rmv2LodHeader_V7_V8_Creator().CreateEmpty(i == 0 ? 20 : 10000, (uint)i, 0)).ToArray();

            var verdict = EditorLodDetector.Analyze(rigid);

            Assert.Multiple(() =>
            {
                Assert.That(verdict.IsEditorGenerated, Is.True);
                Assert.That(verdict.Evidence, Is.EqualTo(EditorLodEvidence.ScheduleWithEditorMarker));
                Assert.That(verdict.Signals.HeaderPaddingMatches, Is.True);
            });
        }

        [Test]
        public void Analyze_CaStyleLodChain_IsNotEditorGenerated()
        {
            var rigid = Rigid(vertexCount: 2000, triangleCount: 4000, lodTriangles: [2300, 1100, 380]);

            var verdict = EditorLodDetector.Analyze(rigid);

            Assert.Multiple(() =>
            {
                Assert.That(verdict.IsEditorGenerated, Is.False);
                Assert.That(verdict.Evidence, Is.EqualTo(EditorLodEvidence.None));
            });
        }

        [Test]
        public void Analyze_ThreeLodsOnTheVertexTarget_IsNotEnoughEvidence()
        {
            // CA ships a few three-LOD rigids that land on the vertex target within 1%.
            var rigid = Rigid(vertexCount: 2000, triangleCount: 4000, lodTriangles: [1250, 500], lodCount: 3);

            Assert.That(EditorLodDetector.Analyze(rigid).IsEditorGenerated, Is.False);
        }

        [Test]
        public void Analyze_VerticesAboutEqualToTriangles_CannotTellTheTargetsApart()
        {
            // 0.75 * vertices == 0.75 * triangles: no LOD separates the editor bug from a sane target.
            var rigid = Rigid(vertexCount: 3000, triangleCount: 3000, lodTriangles: [2250, 1500, 750]);

            Assert.That(EditorLodDetector.Analyze(rigid).Evidence, Is.Not.EqualTo(EditorLodEvidence.VertexCountTarget));
        }

        [Test]
        public void Analyze_SingleLodAndTinyRigids_AreNotJudged()
        {
            Assert.Multiple(() =>
            {
                Assert.That(EditorLodDetector.Analyze(Rigid(2000, 4000, [], lodCount: 1)).IsEditorGenerated, Is.False);
                Assert.That(EditorLodDetector.Analyze(Rigid(20, 40, [30, 20, 10])).IsEditorGenerated, Is.False);
            });
        }

        [Test]
        public void Analyze_DroppedTangentMirroringAndBlackColour_AreReportedAsSignals()
        {
            var rigid = Rigid(vertexCount: 2000, triangleCount: 4000, lodTriangles: [1500, 1000, 500]);
            foreach (var vertex in rigid.ModelList.SelectMany(x => x).SelectMany(x => x.Mesh.VertexList))
                vertex.Colour = new Vector4(0, 0, 0, 1);
            foreach (var model in rigid.ModelList[0])
            {
                foreach (var vertex in model.Mesh.VertexList.Take(1000))
                    vertex.BiNormal = -vertex.BiNormal;
            }

            var signals = EditorLodDetector.Analyze(rigid).Signals;

            Assert.Multiple(() =>
            {
                Assert.That(signals.Lod0MirroredShare, Is.EqualTo(0.5).Within(0.001));
                Assert.That(signals.LastLodMirroredShare, Is.Zero);
                Assert.That(signals.Lod0BlackColourShare, Is.EqualTo(1).Within(1e-9));
                Assert.That(signals.LodsBlackColourShare, Is.EqualTo(1).Within(1e-9));
            });
        }

        [Test]
        public void Analyze_VanillaRigids_AreNotEditorGenerated()
        {
            if (!Directory.Exists(VanillaDump))
                Assert.Ignore("The extracted vanilla files are not available.");

            // Stratified sample: units, props/weapons and buildings, spread over the whole dump.
            var files = Directory.EnumerateFiles(Path.Combine(VanillaDump, "variantmeshes"), "*.rigid_model_v2", SearchOption.AllDirectories)
                .OrderBy(x => x, StringComparer.Ordinal)
                .Where((_, index) => index % 12 == 0)
                .ToList();
            Assert.That(files, Is.Not.Empty);

            var positives = new List<string>();
            var judged = 0;
            foreach (var file in files)
            {
                RmvFile rigid;
                try
                {
                    rigid = ModelFactory.Create().Load(File.ReadAllBytes(file));
                }
                catch (Exception)
                {
                    continue;
                }

                judged++;
                if (EditorLodDetector.Analyze(rigid).IsEditorGenerated)
                    positives.Add(file);
            }

            Assert.That(judged, Is.GreaterThan(100));
            Assert.That(positives, Is.Empty, string.Join(Environment.NewLine, positives));
        }

        private static int Ceil(double value) => (int)Math.Ceiling(value);

        /// <summary>A rigid whose LOD0 has the given totals (split over <paramref name="parts"/>) and whose LOD i has <c>lodTriangles[i-1]</c> triangles.</summary>
        private static RmvFile Rigid(int vertexCount, int triangleCount, int[] lodTriangles, int parts = 1, int lodCount = 4)
        {
            var lods = new List<RmvModel[]> { Enumerable.Range(0, parts).Select(_ => Model(vertexCount / parts, triangleCount / parts)).ToArray() };
            for (var i = 0; i < lodTriangles.Length; i++)
                lods.Add(Enumerable.Range(0, parts).Select(_ => Model(vertexCount / parts / 2, lodTriangles[i] / parts)).ToArray());

            return new RmvFile { ModelList = lods.Take(lodCount).ToArray() };
        }

        private static RmvModel Model(int vertexCount, int triangleCount, Vector4? colourOverride = null)
        {
            var colour = colourOverride ?? Vector4.One;
            var vertices = Enumerable.Range(0, vertexCount).Select(i => new CommonVertex
            {
                Position = new Vector4(i, i * 0.5f, 0, 1),
                Normal = Vector3.UnitZ,
                Tangent = Vector3.UnitX,
                BiNormal = Vector3.UnitY,
                Uv = new Vector2(i * 0.001f, 0),
                Colour = colour,
                BoneIndex = [],
                BoneWeight = [],
            }).ToArray();

            var indices = new ushort[triangleCount * 3];
            for (var i = 0; i < triangleCount; i++)
            {
                indices[i * 3] = (ushort)(i % vertexCount);
                indices[i * 3 + 1] = (ushort)((i + 1) % vertexCount);
                indices[i * 3 + 2] = (ushort)((i + 2) % vertexCount);
            }

            return new RmvModel { Mesh = new RmvMesh { VertexList = vertices, IndexList = indices } };
        }
    }
}
