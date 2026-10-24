using System.Runtime.InteropServices;
using GameWorld.Core.Rendering;
using GameWorld.Core.Rendering.Geometry;
using GameWorld.Core.Services;
using GameWorld.Core.Services.SceneSaving.Lod.MeshOptimizerIntegration;
using GameWorld.Core.Test.TestUtility;
using Microsoft.Xna.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Shared.GameFormats.RigidModel;
using Shared.Ui.Editors.BoneMapping;

namespace GameWorld.Core.Test.Services.SceneSaving.Lod.MeshOptimizerIntegration;

internal sealed class MeshOptReducerTests
{
    private static readonly float[] ReductionFactors = [0.5f, 0.25f];

    [Test]
    public void Reduce_UvSphere_PreservesAuthoredVerticesAndProducesValidTriangles()
    {
        var original = CreateUvSphere(48, 24);
        Assert.That(GetHandednessSigns(original.VertexArray), Is.SupersetOf(new[] { -1, 1 }));

        foreach (var factor in ReductionFactors)
        {
            var result = MeshOptReducer.Reduce(original, factor);
            AssertSaneReduction(original, result, factor);
        }
    }

    [Test]
    public void Reduce_UvSeamedCube_PreservesAuthoredVerticesAndProducesValidTriangles()
    {
        var original = CreateUvSeamedCube(12);
        Assert.That(GetHandednessSigns(original.VertexArray), Is.SupersetOf(new[] { -1, 1 }));

        foreach (var factor in ReductionFactors)
        {
            var result = MeshOptReducer.Reduce(original, factor);
            AssertSaneReduction(original, result, factor);
        }
    }

    [Test]
    public void Reduce_RepeatedIndexTriangles_FiltersThemAndStillReduces()
    {
        var original = CreateUvSphere(32, 16);
        original.IndexArray =
        [
            .. original.IndexArray,
            0, 0, 1,
            4, 5, 5,
            8, 8, 8
        ];

        var result = MeshOptReducer.Reduce(original, 0.5f);

        Assert.That(result.Stats.ReductionSkipped, Is.False, result.Stats.SkipReason);
        Assert.That(result.Stats.DroppedDegenerateTriangles, Is.EqualTo(3));
        Assert.That(result.Stats.AchievedTriangleCount, Is.LessThan(result.Stats.SourceTriangleCount));
        AssertValidIndicesAndTriangles(result.Mesh);
        AssertOutputVerticesAreBitIdenticalToInput(original.VertexArray, result.Mesh.VertexArray);
    }

    [Test]
    public void Reduce_ZeroAreaTriangleWithDistinctIndices_DoesNotSkipMesh()
    {
        var original = CreateUvSphere(32, 16);
        var firstExtraVertex = original.VertexArray.Length;
        original.VertexArray =
        [
            .. original.VertexArray,
            CreateVertex(new Vector3(20, 0, 0), Vector3.UnitZ, Vector3.UnitX, new Vector2(0, 0), 1),
            CreateVertex(new Vector3(21, 0, 0), Vector3.UnitZ, Vector3.UnitX, new Vector2(0.5f, 0), 1),
            CreateVertex(new Vector3(22, 0, 0), Vector3.UnitZ, Vector3.UnitX, new Vector2(1, 0), 1)
        ];
        original.IndexArray = [.. original.IndexArray, (ushort)firstExtraVertex, (ushort)(firstExtraVertex + 1), (ushort)(firstExtraVertex + 2)];

        var result = MeshOptReducer.Reduce(original, 0.5f);

        Assert.That(result.Stats.ReductionSkipped, Is.False, result.Stats.SkipReason);
        Assert.That(result.Stats.DroppedDegenerateTriangles, Is.Zero);
        Assert.That(result.Stats.AchievedTriangleCount, Is.GreaterThan(0));
        AssertValidIndicesAndTriangles(result.Mesh);
        AssertOutputVerticesAreBitIdenticalToInput(original.VertexArray, result.Mesh.VertexArray);
    }

    [Test]
    public void Reduce_OpenGridWithLockBorder_PreservesTheBorderLoop()
    {
        const int side = 18;
        var original = CreateGrid(side, 0, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, 1);

        var result = MeshOptReducer.Reduce(original, 0.5f);

        Assert.That(result.Stats.ReductionSkipped, Is.False, result.Stats.SkipReason);
        Assert.That(result.Stats.AchievedTriangleCount, Is.LessThan(result.Stats.SourceTriangleCount));
        var outputPositions = result.Mesh.VertexArray.Select(GetGridPoint).ToHashSet();
        for (var coordinate = 0; coordinate < side; coordinate++)
        {
            Assert.That(outputPositions, Does.Contain(new GridPoint(coordinate, 0)));
            Assert.That(outputPositions, Does.Contain(new GridPoint(coordinate, side - 1)));
            Assert.That(outputPositions, Does.Contain(new GridPoint(0, coordinate)));
            Assert.That(outputPositions, Does.Contain(new GridPoint(side - 1, coordinate)));
        }

        var sourceBorderEdges = GetGridBorderEdges(original, side);
        var outputBorderEdges = GetGridBorderEdges(result.Mesh, side);
        Assert.That(outputBorderEdges, Is.SupersetOf(sourceBorderEdges));
    }

    [Test]
    public void MeshReduction_WhenMeshoptimizerThrows_UsesManagedFallbackAndPreservesMirroredFrames()
    {
        var original = CreateMirroredGridPair(10);
        var sink = new CollectingLogSink();
        using var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        var result = MeshReduction.Reduce(
            original,
            0.5f,
            "mirrored-fallback-test",
            logger,
            (_, _) => throw new EntryPointNotFoundException("synthetic test failure"));

        Assert.That(result.IndexArray.Length / 3, Is.LessThan(original.IndexArray.Length / 3));
        AssertValidIndicesAndTriangles(result);
        Assert.That(sink.Events.Select(x => x.RenderMessage()), Has.Some.Contains("name='mirrored-fallback-test'").And.Contains("path=fallback"));

        var outputSigns = new HashSet<int>();
        foreach (var vertex in result.VertexArray)
        {
            Assert.That(vertex.Normal.Length(), Is.EqualTo(1f).Within(0.0001f));
            var sign = GetHandedness(vertex);
            outputSigns.Add(sign);
            var expectedSign = vertex.Position.X < 2 ? 1 : -1;
            Assert.That(sign, Is.EqualTo(expectedSign));
        }

        Assert.That(outputSigns, Is.SupersetOf(new[] { -1, 1 }));
    }

    [Test]
    public void Reduce_TuskRigidModel_ReducesEveryMeshAtRequestedFactorsAndPrintsStats()
    {
        var assetPath = FindAssetPath(
            "Rome_Man_And_Shield_Pack",
            "variantmeshes",
            "variantmeshes",
            "_variantmodels",
            "elephant",
            "bodyparts",
            "tusks_a_lod1.rigid_model_v2");
        var rmvFile = ModelFactory.Create().Load(File.ReadAllBytes(assetPath));
        var meshBuilder = new MeshBuilderService(new TestGeometryGraphicsContextFactory());
        var sink = new CollectingLogSink();
        using var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        var table = new List<string>
        {
            "| Mesh | Factor | Source tris | Requested tris | Achieved tris | Relative error | ErrorLimited | Limit reason |",
            "|---|---:|---:|---:|---:|---:|:---:|---|"
        };
        var meshCount = 0;

        for (var lodIndex = 0; lodIndex < rmvFile.ModelList.Length; lodIndex++)
        {
            var modelList = rmvFile.ModelList[lodIndex];
            for (var meshIndex = 0; meshIndex < modelList.Length; meshIndex++)
            {
                var model = modelList[meshIndex];
                var meshName = $"LOD{lodIndex}.Mesh{meshIndex}";
                var original = meshBuilder.BuildMeshFromRmvModel(model, rmvFile.Header.SkeletonName);
                Assert.That(original.IndexArray.Length % 3, Is.Zero, meshName);
                Assert.That(original.IndexArray.Length / 3, Is.GreaterThan(0), meshName);
                meshCount++;

                foreach (var factor in ReductionFactors)
                {
                    var result = MeshOptReducer.Reduce(original, factor);
                    var stats = result.Stats;
                    var loggedResult = MeshReduction.Reduce(original, factor, meshName, logger, (_, _) => result);
                    Assert.That(stats.AchievedTriangleCount, Is.GreaterThan(0), meshName);
                    Assert.That(stats.AchievedTriangleCount, Is.LessThanOrEqualTo(stats.SourceTriangleCount), meshName);
                    Assert.That(float.IsFinite(stats.ResultErrorRelative), Is.True, meshName);
                    Assert.That(stats.TargetReached, Is.EqualTo(stats.AchievedTriangleCount <= stats.RequestedTriangleCount), meshName);
                    var expectedLimitReason = stats.TargetReached ? "none" : stats.ErrorLimited ? "error" : "topology";
                    Assert.That(stats.LimitReason, Is.EqualTo(expectedLimitReason), meshName);
                    var expectedLoggedLimitReason = stats.LimitReason == "topology" ? "topology/locked-border" : stats.LimitReason;
                    var reductionLog = sink.Events.FirstOrDefault(log =>
                    {
                        var message = log.RenderMessage();
                        return message.Contains($"name='{meshName}'") &&
                            message.Contains($"requested={stats.RequestedTriangleCount} tris") &&
                            message.Contains($"achieved={stats.AchievedTriangleCount} tris");
                    });
                    Assert.That(reductionLog, Is.Not.Null, meshName);
                    Assert.That(reductionLog!.RenderMessage(), Does.Contain($"LimitReason={expectedLoggedLimitReason}"), meshName);
                    Assert.That(loggedResult.IndexArray.Length / 3, Is.EqualTo(stats.AchievedTriangleCount), meshName);
                    if (stats.SourceTriangleCount >= 12)
                        Assert.That(stats.ReductionSkipped, Is.False, stats.SkipReason);
                    AssertValidIndicesAndTriangles(result.Mesh);
                    var tableLimitReason = stats.LimitReason == "topology" ? "topology/locked-border" : stats.LimitReason;
                    table.Add($"| {meshName} | {factor:0.##} | {stats.SourceTriangleCount} | {stats.RequestedTriangleCount} | {stats.AchievedTriangleCount} | {stats.ResultErrorRelative:G6} | {stats.ErrorLimited} | {tableLimitReason} |");
                }
            }
        }

        Assert.That(meshCount, Is.GreaterThan(0), "The tusk RMV2 asset should contain at least one mesh.");
        Assert.That(sink.Events.Any(log => log.Level == LogEventLevel.Warning), Is.True,
            "Mesh reductions missing their targets by more than 25% should log a warning.");
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, table));
    }

    private static void AssertSaneReduction(MeshObject original, MeshOptReducerResult result, float factor)
    {
        Assert.That(result.Stats.ReductionSkipped, Is.False, result.Stats.SkipReason);
        AssertReductionCount(result.Stats, factor);
        AssertValidIndicesAndTriangles(result.Mesh);
        AssertOutputVerticesAreBitIdenticalToInput(original.VertexArray, result.Mesh.VertexArray);
    }

    private static void AssertReductionCount(MeshOptReducerStats stats, float factor)
    {
        var requested = Math.Max(1, (int)Math.Round(stats.SourceTriangleCount * (double)factor, MidpointRounding.AwayFromZero));
        var allowedDifference = requested * 0.10;
        Assert.That(stats.RequestedTriangleCount, Is.EqualTo(requested));
        Assert.That(stats.TargetReached, Is.EqualTo(stats.AchievedTriangleCount <= stats.RequestedTriangleCount));
        var expectedLimitReason = stats.TargetReached ? "none" : stats.ErrorLimited ? "error" : "topology";
        Assert.That(stats.LimitReason, Is.EqualTo(expectedLimitReason));
        Assert.That(
            Math.Abs(stats.AchievedTriangleCount - requested) <= allowedDifference || stats.ErrorLimited,
            Is.True,
            $"Requested {requested}, achieved {stats.AchievedTriangleCount}, result error {stats.ResultErrorRelative:G6}, ErrorLimited={stats.ErrorLimited}.");
    }

    private static void AssertValidIndicesAndTriangles(MeshObject mesh)
    {
        Assert.That(mesh.IndexArray.Length % 3, Is.Zero);
        Assert.That(mesh.IndexArray.All(x => x < mesh.VertexArray.Length), Is.True);
        for (var i = 0; i < mesh.IndexArray.Length; i += 3)
        {
            Assert.That(mesh.IndexArray[i], Is.Not.EqualTo(mesh.IndexArray[i + 1]), $"Triangle {i / 3} repeats an index.");
            Assert.That(mesh.IndexArray[i + 1], Is.Not.EqualTo(mesh.IndexArray[i + 2]), $"Triangle {i / 3} repeats an index.");
            Assert.That(mesh.IndexArray[i + 2], Is.Not.EqualTo(mesh.IndexArray[i]), $"Triangle {i / 3} repeats an index.");
        }
    }

    private static void AssertOutputVerticesAreBitIdenticalToInput(
        VertexPositionNormalTextureCustom[] input,
        VertexPositionNormalTextureCustom[] output)
    {
        var inputVertexBytes = new HashSet<string>();
        for (var i = 0; i < input.Length; i++)
            inputVertexBytes.Add(Convert.ToHexString(MemoryMarshal.AsBytes(input.AsSpan(i, 1))));

        for (var i = 0; i < output.Length; i++)
        {
            var outputVertexBytes = Convert.ToHexString(MemoryMarshal.AsBytes(output.AsSpan(i, 1)));
            Assert.That(inputVertexBytes.Contains(outputVertexBytes), Is.True, $"Output vertex {i} is not bit-identical to any source vertex.");
        }
    }

    private static HashSet<int> GetHandednessSigns(IEnumerable<VertexPositionNormalTextureCustom> vertices)
        => vertices.Select(GetHandedness).ToHashSet();

    private static int GetHandedness(VertexPositionNormalTextureCustom vertex)
    {
        var orientation = Vector3.Dot(Vector3.Cross(vertex.Normal, vertex.Tangent), vertex.BiNormal);
        return orientation < 0 ? -1 : 1;
    }

    private static MeshObject CreateUvSphere(int longitudeSegments, int latitudeSegments)
    {
        var vertices = new List<VertexPositionNormalTextureCustom>();
        var indices = new List<ushort>();
        var topIndex = AddSphereVertex(new Vector3(0, 1, 0), 0.5f, 0, 1, Vector3.UnitX);
        var ringStarts = new int[latitudeSegments - 1];

        for (var latitude = 1; latitude < latitudeSegments; latitude++)
        {
            ringStarts[latitude - 1] = vertices.Count;
            var theta = Math.PI / 2 - Math.PI * latitude / latitudeSegments;
            for (var longitude = 0; longitude <= longitudeSegments; longitude++)
            {
                var phi = 2 * Math.PI * longitude / longitudeSegments;
                var cosTheta = (float)Math.Cos(theta);
                var position = new Vector3(cosTheta * (float)Math.Cos(phi), (float)Math.Sin(theta), cosTheta * (float)Math.Sin(phi));
                var tangent = new Vector3(-(float)Math.Sin(phi), 0, (float)Math.Cos(phi));
                var handedness = longitude >= longitudeSegments / 3 && longitude < 2 * longitudeSegments / 3 ? -1 : 1;
                AddSphereVertex(position, longitude / (float)longitudeSegments, latitude / (float)latitudeSegments, handedness, tangent);
            }
        }

        var bottomIndex = AddSphereVertex(new Vector3(0, -1, 0), 0.5f, 1, 1, Vector3.UnitX);

        for (var longitude = 0; longitude < longitudeSegments; longitude++)
        {
            indices.Add((ushort)topIndex);
            indices.Add((ushort)(ringStarts[0] + longitude + 1));
            indices.Add((ushort)(ringStarts[0] + longitude));
        }

        for (var latitude = 0; latitude < ringStarts.Length - 1; latitude++)
        {
            for (var longitude = 0; longitude < longitudeSegments; longitude++)
            {
                var a = ringStarts[latitude] + longitude;
                var b = a + 1;
                var c = ringStarts[latitude + 1] + longitude;
                var d = c + 1;
                indices.Add((ushort)a);
                indices.Add((ushort)b);
                indices.Add((ushort)d);
                indices.Add((ushort)a);
                indices.Add((ushort)d);
                indices.Add((ushort)c);
            }
        }

        var lastRing = ringStarts[^1];
        for (var longitude = 0; longitude < longitudeSegments; longitude++)
        {
            indices.Add((ushort)(lastRing + longitude));
            indices.Add((ushort)bottomIndex);
            indices.Add((ushort)(lastRing + longitude + 1));
        }

        return CreateMesh(vertices, indices);

        int AddSphereVertex(Vector3 position, float u, float v, int handedness, Vector3? tangentOverride = null)
        {
            var normal = Vector3.Normalize(position);
            var tangent = tangentOverride is Vector3 overrideTangent
                ? Vector3.Normalize(overrideTangent)
                : Vector3.UnitX;
            vertices.Add(CreateVertex(position, normal, tangent, new Vector2(u, v), handedness));
            return vertices.Count - 1;
        }
    }

    private static MeshObject CreateUvSeamedCube(int subdivisions)
    {
        var vertices = new List<VertexPositionNormalTextureCustom>();
        var indices = new List<ushort>();
        var faces = new[]
        {
            (Origin: new Vector3(-1, -1, 1), U: new Vector3(2, 0, 0), V: new Vector3(0, 2, 0)),
            (Origin: new Vector3(1, -1, -1), U: new Vector3(-2, 0, 0), V: new Vector3(0, 2, 0)),
            (Origin: new Vector3(1, -1, 1), U: new Vector3(0, 0, -2), V: new Vector3(0, 2, 0)),
            (Origin: new Vector3(-1, -1, -1), U: new Vector3(0, 0, 2), V: new Vector3(0, 2, 0)),
            (Origin: new Vector3(-1, 1, 1), U: new Vector3(2, 0, 0), V: new Vector3(0, 0, -2)),
            (Origin: new Vector3(-1, -1, -1), U: new Vector3(2, 0, 0), V: new Vector3(0, 0, 2))
        };

        for (var faceIndex = 0; faceIndex < faces.Length; faceIndex++)
        {
            var face = faces[faceIndex];
            var normal = Vector3.Normalize(Vector3.Cross(face.U, face.V));
            var handedness = faceIndex % 2 == 0 ? 1 : -1;
            var tangent = Vector3.Normalize(face.U) * handedness;
            var start = vertices.Count;

            for (var y = 0; y <= subdivisions; y++)
            {
                var v = y / (float)subdivisions;
                for (var x = 0; x <= subdivisions; x++)
                {
                    var u = x / (float)subdivisions;
                    var position = face.Origin + face.U * u + face.V * v;
                    var uvU = handedness < 0 ? 1 - u : u;
                    vertices.Add(CreateVertex(position, normal, tangent, new Vector2(uvU, v), handedness));
                }
            }

            for (var y = 0; y < subdivisions; y++)
            {
                for (var x = 0; x < subdivisions; x++)
                {
                    var a = start + y * (subdivisions + 1) + x;
                    var b = a + 1;
                    var d = a + subdivisions + 1;
                    var c = d + 1;
                    indices.Add((ushort)a);
                    indices.Add((ushort)b);
                    indices.Add((ushort)c);
                    indices.Add((ushort)a);
                    indices.Add((ushort)c);
                    indices.Add((ushort)d);
                }
            }
        }

        return CreateMesh(vertices, indices);
    }

    private static MeshObject CreateGrid(int side, float xOffset, Vector3 normal, Vector3 tangent, Vector3 binormal, int handedness)
    {
        var vertices = new List<VertexPositionNormalTextureCustom>(side * side);
        var indices = new List<ushort>((side - 1) * (side - 1) * 6);
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var position = new Vector3(x + xOffset, y, 0);
                vertices.Add(CreateVertex(position, normal, tangent, new Vector2(x / (float)(side - 1), y / (float)(side - 1)), handedness, binormal));
            }
        }

        AddGridIndices(side, indices);
        return CreateMesh(vertices, indices);
    }

    private static MeshObject CreateMirroredGridPair(int side)
    {
        var vertices = new List<VertexPositionNormalTextureCustom>(side * side * 2);
        var indices = new List<ushort>((side - 1) * (side - 1) * 12);
        for (var component = 0; component < 2; component++)
        {
            var handedness = component == 0 ? 1 : -1;
            var tangent = Vector3.UnitX * handedness;
            var binormal = Vector3.Cross(Vector3.UnitZ, tangent) * handedness;
            var start = vertices.Count;
            var xOffset = component == 0 ? 0 : 3;

            for (var y = 0; y < side; y++)
            {
                for (var x = 0; x < side; x++)
                {
                    var position = new Vector3(xOffset + x / (float)(side - 1), y / (float)(side - 1), 0);
                    vertices.Add(CreateVertex(position, Vector3.UnitZ, tangent, new Vector2(x / (float)(side - 1), y / (float)(side - 1)), handedness, binormal));
                }
            }

            AddGridIndices(side, indices, start);
        }

        return CreateMesh(vertices, indices);
    }

    private static void AddGridIndices(int side, List<ushort> indices, int startIndex = 0)
    {
        for (var y = 0; y < side - 1; y++)
        {
            for (var x = 0; x < side - 1; x++)
            {
                var a = startIndex + y * side + x;
                var b = a + 1;
                var d = a + side;
                var c = d + 1;
                indices.Add((ushort)a);
                indices.Add((ushort)b);
                indices.Add((ushort)c);
                indices.Add((ushort)a);
                indices.Add((ushort)c);
                indices.Add((ushort)d);
            }
        }
    }

    private static VertexPositionNormalTextureCustom CreateVertex(Vector3 position, Vector3 normal, Vector3 tangent, Vector2 uv, int handedness, Vector3? binormalOverride = null)
    {
        normal = Vector3.Normalize(normal);
        tangent = Vector3.Normalize(tangent - normal * Vector3.Dot(normal, tangent));
        var binormal = binormalOverride ?? Vector3.Cross(normal, tangent) * handedness;
        return new VertexPositionNormalTextureCustom
        {
            Position = new Vector4(position, 1),
            Normal = normal,
            Tangent = tangent,
            BiNormal = Vector3.Normalize(binormal),
            TextureCoordinate = uv,
            TextureCoordinate1 = uv,
            BlendIndices = Vector4.Zero,
            BlendWeights = Vector4.Zero
        };
    }

    private static MeshObject CreateMesh(IEnumerable<VertexPositionNormalTextureCustom> vertices, IEnumerable<ushort> indices)
    {
        var mesh = new MeshObject(new TestGeometryGraphicsContextFactory().Create(), "test-skeleton");
        mesh.ChangeVertexType(UiVertexFormat.Static, updateMesh: false);
        mesh.VertexArray = vertices.ToArray();
        mesh.IndexArray = indices.ToArray();
        return mesh;
    }

    private static HashSet<GridEdge> GetGridBorderEdges(MeshObject mesh, int side)
    {
        var edges = new HashSet<GridEdge>();
        for (var i = 0; i < mesh.IndexArray.Length; i += 3)
        {
            AddEdge(mesh.IndexArray[i], mesh.IndexArray[i + 1]);
            AddEdge(mesh.IndexArray[i + 1], mesh.IndexArray[i + 2]);
            AddEdge(mesh.IndexArray[i + 2], mesh.IndexArray[i]);
        }
        return edges;

        void AddEdge(ushort firstIndex, ushort secondIndex)
        {
            var first = GetGridPoint(mesh.VertexArray[firstIndex]);
            var second = GetGridPoint(mesh.VertexArray[secondIndex]);
            if (!IsBorderPoint(first, side) || !IsBorderPoint(second, side))
                return;

            if (Math.Abs(first.X - second.X) + Math.Abs(first.Y - second.Y) != 1)
                return;

            edges.Add(GridEdge.Create(first, second));
        }
    }

    private static GridPoint GetGridPoint(VertexPositionNormalTextureCustom vertex)
        => new((int)vertex.Position.X, (int)vertex.Position.Y);

    private static bool IsBorderPoint(GridPoint point, int side)
        => point.X == 0 || point.Y == 0 || point.X == side - 1 || point.Y == side - 1;

    private static string FindAssetPath(params string[] relativePath)
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, "Data", .. relativePath]);
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        Assert.Fail($"Could not find test asset Data/{Path.Combine(relativePath)} starting from {TestContext.CurrentContext.TestDirectory}.");
        return string.Empty;
    }

    private sealed class CollectingLogSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private readonly record struct GridPoint(int X, int Y);
    private readonly record struct GridEdge(GridPoint First, GridPoint Second)
    {
        public static GridEdge Create(GridPoint first, GridPoint second)
            => first.X < second.X || first.X == second.X && first.Y <= second.Y
                ? new GridEdge(first, second)
                : new GridEdge(second, first);
    }
}
