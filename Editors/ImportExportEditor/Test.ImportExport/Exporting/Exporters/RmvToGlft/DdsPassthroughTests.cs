using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using Editors.ImportExport.Exporting.Exporters.DdsToMaterialPng;
using Editors.ImportExport.Exporting.Exporters.DdsToNormalPng;
using Editors.ImportExport.Exporting.Exporters.RmvToGltf;
using Editors.ImportExport.Exporting.Exporters.RmvToGltf.Helpers;
using GameWorld.Core.Services;
using LodHeaderFactory = Shared.GameFormats.RigidModel.LodHeader.LodHeaderFactory;
using MeshImportExport;
using Moq;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.MaterialHeaders;
using Shared.GameFormats.RigidModel.Types;
using Shared.GameFormats.RigidModel.Vertex;
using SharpGLTF.Schema2;

namespace Test.ImportExport.Exporting.Exporters.RmvToGlft;

public class DdsPassthroughTests
{
    private const string BasePath = "textures/body_base_colour.dds";
    private const string NormalPath = "textures/body_normal.dds";
    private const string MaterialPath = "textures/body_material_map.dds";

    [Test]
    public void PassthroughSidecarIsByteIdenticalToTheSourceDds()
    {
        using var scope = new Scope();
        var bc1 = BuildDds(8, 8, 4, dx10Dxgi: 72, blockBytes: 8);
        var dxt5 = BuildDds(8, 8, 4, fourCc: "DXT5", blockBytes: 16);
        scope.Add(BasePath, bc1);
        scope.Add(NormalPath, dxt5);
        scope.Add(MaterialPath, bc1);

        var textures = scope.Handle(ddsTextures: true);

        Assert.That(textures, Has.Count.EqualTo(3));
        Assert.That(textures.All(t => t.SystemFilePath.EndsWith(".dds")), Is.True);
        Assert.That(File.ReadAllBytes(textures.Single(t => t.GltfTextureType == SharpGLTF.Materials.KnownChannel.Normal).SystemFilePath), Is.EqualTo(dxt5));
        Assert.That(File.ReadAllBytes(textures.Single(t => t.GltfTextureType == SharpGLTF.Materials.KnownChannel.BaseColor).SystemFilePath), Is.EqualTo(bc1));
        Assert.That(textures.Select(t => t.Wh3TextureEncoding), Is.EquivalentTo(new[] { "direct", "normal-dxt5nm", "material-map-wh3" }));
        scope.MaterialExporter.Verify(AnyKtx2, Times.Never);
        scope.NormalExporter.Verify(AnyNormalKtx2, Times.Never);
        // The in-memory image is only the DDS header plus a path marker.
        Assert.That(textures.All(t => t.ImageData!.Length < 400 && DdsPassthrough.IsSidecarStub(t.ImageData)), Is.True);
    }

    [Test]
    public void GlbReferencesTheDdsSidecarsAndCarriesEncodingExtras()
    {
        using var scope = new Scope();
        var bc1 = BuildDds(8, 8, 4, dx10Dxgi: 72, blockBytes: 8);
        var dxt5 = BuildDds(12, 8, 4, fourCc: "DXT5", blockBytes: 16);
        scope.Add(BasePath, bc1);
        scope.Add(NormalPath, dxt5);
        scope.Add(MaterialPath, BuildDds(8, 8, 4, dx10Dxgi: 72, blockBytes: 8, seed: 9));

        var textures = scope.Handle(ddsTextures: true);
        var json = scope.SaveGlb(textures);

        TestContext.Out.WriteLine(json.ToJsonString());
        // SharpGLTF adds one shared core-`source` fallback image (the same as it
        // does for KTX2); the three DDS images are the ones with extras.
        var allImages = json["images"]!.AsArray();
        foreach (var file in Directory.GetFiles(scope.Directory))
            TestContext.Out.WriteLine($"{Path.GetFileName(file)} {new FileInfo(file).Length}");
        var images = new JsonArray(allImages.Where(i => i!["extras"] != null).Select(i => (JsonNode)JsonNode.Parse(i!.ToJsonString())!).ToArray());
        Assert.That(allImages, Has.Count.EqualTo(4));
        Assert.That(images, Has.Count.EqualTo(3));
        foreach (var image in images)
            Assert.That(image!["uri"]!.GetValue<string>(), Does.EndWith(".dds"));

        var byEncoding = images.ToDictionary(i => i!["extras"]!["wh3TextureEncoding"]!.GetValue<string>(), i => i!);
        Assert.That(byEncoding.Keys, Is.EquivalentTo(new[] { "direct", "normal-dxt5nm", "material-map-wh3" }));
        Assert.That(byEncoding["normal-dxt5nm"]["extras"]!["wh3Channel"]!.GetValue<string>(), Is.EqualTo("Normal"));
        Assert.That(byEncoding["direct"]["extras"]!["wh3SourceVirtualPath"]!.GetValue<string>(), Is.EqualTo("textures\\body_base_colour.dds"));
        Assert.That(byEncoding["direct"]["extras"]!["wh3GeneratedFileName"]!.GetValue<string>(), Does.EndWith(".dds"));
        Assert.That(json["extensionsUsed"]!.ToJsonString(), Does.Contain("MSFT_texture_dds"));
        Assert.That(json["textures"]!.AsArray().All(t => t!["extensions"]?["MSFT_texture_dds"] != null), Is.True);

        // Sidecars were reused, not rewritten, and equal the source bytes.
        foreach (var texture in textures)
            Assert.That(File.ReadAllBytes(texture.SystemFilePath).Length, Is.GreaterThan(128));
        var normalFile = Path.Combine(scope.Directory, byEncoding["normal-dxt5nm"]["uri"]!.GetValue<string>());
        Assert.That(File.ReadAllBytes(normalFile), Is.EqualTo(dxt5));
    }

    [Test]
    public void UncompressedAndNonMultipleOfFourDdsFallBackToKtx2()
    {
        using var scope = new Scope();
        // Uncompressed 32-bit BGRA DDS.
        scope.Add(BasePath, BuildUncompressedDds(8, 8));
        // BC1 with a 6x6 size (not a multiple of four).
        scope.Add(MaterialPath, BuildDds(6, 6, 1, dx10Dxgi: 72, blockBytes: 8));
        // BC3 passthrough candidate.
        scope.Add(NormalPath, BuildDds(8, 8, 1, fourCc: "DXT5", blockBytes: 16));

        var textures = scope.Handle(ddsTextures: true);

        Assert.That(textures, Has.Count.EqualTo(3));
        var normal = textures.Single(t => t.GltfTextureType == SharpGLTF.Materials.KnownChannel.Normal);
        Assert.That(normal.SystemFilePath, Does.EndWith(".dds"));
        var fallbacks = textures.Where(t => t != normal).ToList();
        Assert.That(fallbacks.All(t => t.SystemFilePath.EndsWith(".ktx2")), Is.True);
        Assert.That(fallbacks.All(t => t.Wh3TextureEncoding == null), Is.True);
        scope.MaterialExporter.Verify(AnyKtx2, Times.Exactly(2));
    }

    [Test]
    public void FlagOffNeverTouchesTheDdsPath()
    {
        using var scope = new Scope();
        scope.Add(BasePath, BuildDds(8, 8, 4, dx10Dxgi: 72, blockBytes: 8));
        scope.Add(NormalPath, BuildDds(8, 8, 4, fourCc: "DXT5", blockBytes: 16));
        scope.Add(MaterialPath, BuildDds(8, 8, 4, dx10Dxgi: 72, blockBytes: 8));

        var textures = scope.Handle(ddsTextures: false);
        var json = scope.SaveGlb(textures);

        Assert.That(textures.All(t => t.SystemFilePath.EndsWith(".ktx2")), Is.True);
        Assert.That(textures.All(t => t.Wh3TextureEncoding == null), Is.True);
        scope.Lookup.Verify(l => l.FindFile(It.IsAny<string>(), It.IsAny<IPackFileContainer?>()), Times.Never);
        Assert.That(Directory.GetFiles(scope.Directory, "*.dds"), Is.Empty);
        Assert.That(json.ToJsonString(), Does.Not.Contain("wh3TextureEncoding"));
        Assert.That(json.ToJsonString(), Does.Not.Contain("MSFT_texture_dds"));
    }

    [Test]
    public void HeaderInspectionCoversTheSupportedFormats()
    {
        foreach (var dxgi in new uint[] { 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81, 82, 83, 84, 97, 98, 99 })
            Assert.That(DdsPassthrough.TryInspect(BuildDds(8, 8, 1, dx10Dxgi: dxgi, blockBytes: 16), out _), Is.True, $"dxgi {dxgi}");
        foreach (var fourCc in new[] { "DXT1", "DXT3", "DXT5", "ATI1", "ATI2", "BC4U", "BC5U" })
            Assert.That(DdsPassthrough.TryInspect(BuildDds(8, 8, 1, fourCc: fourCc, blockBytes: 16), out _), Is.True, fourCc);
        Assert.That(DdsPassthrough.TryInspect(BuildDds(8, 8, 1, dx10Dxgi: 28, blockBytes: 16), out _), Is.False);
        Assert.That(DdsPassthrough.TryInspect(BuildDds(8, 8, 1, dx10Dxgi: 95, blockBytes: 16), out _), Is.False); // BC6H
        Assert.That(DdsPassthrough.TryInspect(BuildDds(8, 8, 1, dx10Dxgi: 72, blockBytes: 8, arraySize: 2), out _), Is.False);
        Assert.That(DdsPassthrough.TryInspect(BuildDds(8, 8, 1, fourCc: "DXT1", blockBytes: 8, cubemap: true), out _), Is.False);
        Assert.That(DdsPassthrough.TryInspect(BuildUncompressedDds(8, 8), out _), Is.False);
    }

    private static System.Linq.Expressions.Expression<Action<IDdsToMaterialPngExporter>> AnyKtx2
        => x => x.ExportKtx2WithData(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>());

    private static System.Linq.Expressions.Expression<Action<IDdsToNormalPngExporter>> AnyNormalKtx2
        => x => x.ExportKtx2WithData(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>());

    private sealed class Scope : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"asset-editor-dds-{Guid.NewGuid():N}");
        public Mock<IPackedFileLookup> Lookup { get; } = new();
        public Mock<IDdsToMaterialPngExporter> MaterialExporter { get; } = new();
        public Mock<IDdsToNormalPngExporter> NormalExporter { get; } = new();
        private readonly Dictionary<string, PackFile> _files = new(StringComparer.OrdinalIgnoreCase);

        public Scope()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Lookup.Setup(l => l.FindFile(It.IsAny<string>(), It.IsAny<IPackFileContainer?>()))
                .Returns((string path, IPackFileContainer? _) => _files.GetValueOrDefault(path.Replace('\\', '/')));

            // Stand-ins for the real KTX2 exporters: write a small valid RGBA KTX2.
            MaterialExporter.Setup(AnyKtx2Setup)
                .Returns((string source, string output, bool _, bool _) => Ktx2(source, output, ""));
            NormalExporter.Setup(x => x.ExportKtx2WithData(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
                .Returns((string source, string output, bool _) => Ktx2(source, output, ""));
        }

        private static System.Linq.Expressions.Expression<Func<IDdsToMaterialPngExporter, TextureImageExportResult>> AnyKtx2Setup
            => x => x.ExportKtx2WithData(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>());

        private static TextureImageExportResult Ktx2(string source, string output, string suffix)
        {
            var path = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(source) + suffix + ".ktx2");
            var data = TextureHelper.EncodeBgraToKtx2(
                new TextureHelper.DecodedDdsImage(4, 4, Enumerable.Repeat((byte)source.Length, 64).ToArray()),
                srgb: false).Ktx2Data;
            File.WriteAllBytes(path, data);
            return new TextureImageExportResult(path, data);
        }

        public void Add(string path, byte[] bytes)
            => _files[path] = PackFile.CreateFromBytes(Path.GetFileName(path), bytes);

        public List<TextureResult> Handle(bool ddsTextures)
        {
            var handler = new GltfTextureHandler(NormalExporter.Object, MaterialExporter.Object, Lookup.Object);
            Asset = CreateAsset();
            Settings = new RmvToGltfExporterSettings(
                Asset.InputFile, [], Path.Combine(Directory, "model.glb"), true, true, true, false, false)
            {
                UseKtx2Textures = true,
                UseDdsTextures = ddsTextures,
                ExportAuxiliaryMasks = false,
                MaxTextureParallelism = 2
            };
            return handler.HandleTextures(Asset, Settings, new GltfTextureExportSession(collisionSafe: true));
        }

        public ResolvedModelAsset Asset { get; private set; } = null!;
        public RmvToGltfExporterSettings Settings { get; private set; } = null!;

        public JsonObject SaveGlb(List<TextureResult> textures)
        {
            var meshBuilder = new GltfMeshBuilder().Build(Asset, textures, Settings, willHaveSkeleton: false).Single();
            var model = ModelRoot.CreateModel();
            var scene = model.UseScene("default");
            scene.CreateNode("n").WithMesh(model.CreateMesh(meshBuilder));
            var glb = Path.Combine(Directory, "model.glb");
            new HeadlessGltfSceneSaver().Save(model, glb, textures.Select(t => t.SystemFilePath).ToList());

            var bytes = File.ReadAllBytes(glb);
            var jsonLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12, 4));
            return JsonNode.Parse(bytes.AsSpan(20, jsonLength))!.AsObject();
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private static ResolvedModelAsset CreateAsset()
    {
        var material = new WeightedMaterial { ModelName = "body" };
        material.SetTexture(TextureType.BaseColour, BasePath);
        material.SetTexture(TextureType.Normal, NormalPath);
        material.SetTexture(TextureType.MaterialMap, MaterialPath);
        var model = new RmvModel
        {
            Material = material,
            Mesh = new RmvMesh
            {
                VertexList = [Vertex(0, 0, 0), Vertex(1, 0, 0), Vertex(0, 1, 0)],
                IndexList = [0, 1, 2]
            }
        };
        var header = new RmvFileHeader { Version = RmvVersionEnum.RMV2_V6, LodCount = 1, SkeletonName = string.Empty };
        var rmv = new RmvFile
        {
            Header = header,
            ModelList = [new[] { model }],
            LodHeaders = [LodHeaderFactory.Create().CreateEmpty(RmvVersionEnum.RMV2_V6, 0, 0, 0)]
        };
        var input = PackFile.CreateFromASCII("body.rigid_model_v2", "body");
        var part = new ResolvedModelPart(0, 0, model, ResolvedModelMaterial.Create(material));
        return new ResolvedModelAsset(input, input, null, null, rmv, [new[] { part }], []);
    }

    private static CommonVertex Vertex(float x, float y, float z) => new()
    {
        Position = new Vector4(x, y, z, 1),
        Normal = Vector3.UnitZ,
        Tangent = Vector3.UnitX,
        Uv = Vector2.Zero,
        BoneIndex = new byte[4],
        BoneWeight = new float[4]
    };

    /// <summary>Builds a DDS with a legacy (FourCC) or DX10 header and pseudo-random block data.</summary>
    private static byte[] BuildDds(
        int width, int height, int mips, string? fourCc = null, uint dx10Dxgi = 0,
        int blockBytes = 8, int seed = 1, uint arraySize = 1, bool cubemap = false)
    {
        var headerLength = dx10Dxgi != 0 ? 148 : 128;
        long payload = 0;
        for (var level = 0; level < mips; level++)
            payload += (long)Math.Max(1, (width >> level) / 4) * Math.Max(1, (height >> level) / 4) * blockBytes;
        var data = new byte[headerLength + payload];
        new Random(seed).NextBytes(data.AsSpan(headerLength));
        var span = data.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, 0x20534444);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 124);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)mips);
        BinaryPrimitives.WriteUInt32LittleEndian(span[76..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(span[80..], 4);
        var code = dx10Dxgi != 0 ? "DX10" : fourCc!;
        for (var i = 0; i < 4; i++)
            data[84 + i] = (byte)code[i];
        BinaryPrimitives.WriteUInt32LittleEndian(span[108..], 0x1000 | 0x8 | 0x400000);
        if (cubemap)
            BinaryPrimitives.WriteUInt32LittleEndian(span[112..], 0x200);
        if (dx10Dxgi != 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[128..], dx10Dxgi);
            BinaryPrimitives.WriteUInt32LittleEndian(span[132..], 3);
            BinaryPrimitives.WriteUInt32LittleEndian(span[140..], arraySize);
        }
        return data;
    }

    private static byte[] BuildUncompressedDds(int width, int height)
    {
        var data = new byte[128 + width * height * 4];
        var span = data.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, 0x20534444);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 124);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 0x1 | 0x2 | 0x4 | 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(span[76..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(span[80..], 0x41); // RGB | ALPHAPIXELS
        BinaryPrimitives.WriteUInt32LittleEndian(span[88..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(span[92..], 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(span[96..], 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(span[100..], 0x000000FF);
        BinaryPrimitives.WriteUInt32LittleEndian(span[104..], 0xFF000000);
        BinaryPrimitives.WriteUInt32LittleEndian(span[108..], 0x1000);
        return data;
    }
}
