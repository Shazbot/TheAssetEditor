using System.Numerics;
using Editors.ImportExport.Exporting.Exporters.RmvToGltf;
using Editors.ImportExport.Exporting.Exporters.RmvToGltf.Helpers;
using GameWorld.Core.Services;
using Moq;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.GameFormats.Animation;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.LodHeader;
using Shared.GameFormats.RigidModel.MaterialHeaders;
using Shared.GameFormats.RigidModel.Transforms;
using Shared.GameFormats.RigidModel.Types;
using Shared.GameFormats.RigidModel.Vertex;

namespace Test.ImportExport.Exporting.Exporters.RmvToGlft;

public class AnimationsOnlyExportTests
{
    [Test]
    public void AnimationsOnlyExportKeepsSkeletonNodesAndSkipsMeshesAndTextures()
    {
        var full = Export(animationsOnly: false);
        var animationsOnly = Export(animationsOnly: true);

        Assert.That(animationsOnly.Saver.ModelRoot, Is.Not.Null);
        var root = animationsOnly.Saver.ModelRoot!;
        Assert.That(root.LogicalMeshes, Has.Count.EqualTo(0));
        Assert.That(root.LogicalSkins, Has.Count.EqualTo(0));
        Assert.That(root.LogicalImages, Has.Count.EqualTo(0));
        Assert.That(root.LogicalMaterials, Has.Count.EqualTo(0));
        Assert.That(animationsOnly.TextureHandler.CallCount, Is.EqualTo(0), "Textures must not be touched.");

        var fullNames = full.Saver.ModelRoot!.LogicalNodes.Select(x => x.Name).ToHashSet();
        var names = root.LogicalNodes.Select(x => x.Name).ToList();
        Assert.That(names, Does.Contain("root"));
        Assert.That(names, Does.Contain("//skeleton//test_skeleton"));
        Assert.That(names.Where(name => !string.IsNullOrEmpty(name)), Is.All.Matches<string>(fullNames.Contains),
            "Skeleton node names must match the full export so clips bind to the loaded scene.");
    }

    [Test]
    public void AnimationsOnlyExportReportsFailedClipsWithoutFailingTheExport()
    {
        var sink = new List<string>();
        var result = Export(animationsOnly: true, animations: [PackFile.CreateFromASCII("broken.anim", "not an animation")], sink: sink);

        Assert.That(result.Saver.IsSaveCalled, Is.True);
        Assert.That(sink, Has.Count.EqualTo(1));
        Assert.That(sink[0], Does.StartWith("clip0|broken.anim|"));
        Assert.That(result.Saver.ModelRoot!.LogicalAnimations, Has.Count.EqualTo(0));
    }

    private static (TestGltfSceneSaver Saver, CountingTextureHandler TextureHandler) Export(
        bool animationsOnly,
        List<PackFile>? animations = null,
        List<string>? sink = null)
    {
        var input = PackFile.CreateFromASCII("root.variantmeshdefinition", "<VARIANT_MESH />");
        var node = new ResolvedVariantMeshNode("root", input, null) { ModelAsset = CreateAsset(input, weighted: true) };
        var composition = new ResolvedVariantMeshComposition(input, node, []);
        var compositionResolver = new Mock<IVariantMeshCompositionResolver>();
        compositionResolver.Setup(x => x.Resolve(input)).Returns(composition);

        var packFileService = new Mock<IPackFileService>().Object;
        var skeletonLookup = new Mock<ISkeletonAnimationLookUpHelper>();
        skeletonLookup.Setup(x => x.GetSkeletonFileFromName("test_skeleton")).Returns(CreateSkeletonFile());
        var saver = new TestGltfSceneSaver();
        var textureHandler = new CountingTextureHandler();
        var exporter = new RmvToGltfExporter(
            saver,
            new GltfMeshBuilder(),
            textureHandler,
            new GltfSkeletonBuilder(packFileService),
            new GltfAnimationBuilder(packFileService),
            skeletonLookup.Object,
            new Mock<IModelAssetResolver>().Object,
            compositionResolver.Object);

        animations ??= [];
        exporter.Export(new RmvToGltfExporterSettings(
            input,
            animations,
            Path.Combine(Path.GetTempPath(), "animations-only.glb"),
            false,
            false,
            false,
            animations.Count > 0,
            false)
        {
            AnimationsOnly = animationsOnly,
            AnimationNames = animations.Select((_, index) => $"clip{index}").ToList(),
            AnimationWarningSink = sink,
        });
        return (saver, textureHandler);
    }

    private static ResolvedModelAsset CreateAsset(
        PackFile input,
        string modelName = "synthetic",
        string skeletonName = "test_skeleton",
        bool weighted = false,
        int matrixIndex = -1)
    {
        var material = new WeightedMaterial { ModelName = modelName, MatrixIndex = matrixIndex };
        var model = new RmvModel
        {
            Material = material,
            Mesh = new RmvMesh
            {
                VertexList =
                [
                    CreateVertex(new Vector3(0, 0, 0), weighted),
                    CreateVertex(new Vector3(1, 0, 0), weighted),
                    CreateVertex(new Vector3(0, 1, 0), weighted)
                ],
                IndexList = [0, 1, 2]
            }
        };
        var header = new RmvFileHeader
        {
            Version = RmvVersionEnum.RMV2_V6,
            LodCount = 1,
            SkeletonName = skeletonName
        };
        var rmv = new RmvFile
        {
            Header = header,
            ModelList = [new[] { model }],
            LodHeaders = [LodHeaderFactory.Create().CreateEmpty(RmvVersionEnum.RMV2_V6, 0, 0, 0)]
        };
        var part = new ResolvedModelPart(0, 0, model, ResolvedModelMaterial.Create(material));
        return new ResolvedModelAsset(input, input, null, null, rmv, [new[] { part }], []);
    }

    private static CommonVertex CreateVertex(Vector3 position, bool weighted = false)
    {
        var vertex = new CommonVertex
        {
            Position = new Vector4(position.X, position.Y, position.Z, 1),
            Normal = Vector3.UnitZ,
            Tangent = Vector3.UnitX,
            Uv = Vector2.Zero,
            BoneIndex = new byte[4],
            BoneWeight = new float[4]
        };

        if (weighted)
        {
            vertex.WeightCount = 1;
            vertex.BoneIndex[0] = 0;
            vertex.BoneWeight[0] = 1;
        }

        return vertex;
    }

    private static AnimationFile CreateSkeletonFile()
    {
        var file = new AnimationFile();
        file.Header.SkeletonName = "test_skeleton";
        file.Bones = [new AnimationFile.BoneInfo { Id = 0, Name = "root", ParentId = AnimationFile.BoneIndexNoParent }];
        var frame = new AnimationFile.Frame();
        frame.Transforms.Add(new RmvVector3(Vector3.Zero));
        frame.Quaternion.Add(new RmvVector4(0, 0, 0, 1));
        var part = new AnimationFile.AnimationPart();
        part.DynamicFrames.Add(frame);
        file.AnimationParts.Add(part);
        return file;
    }

    private sealed class CountingTextureHandler : IGltfTextureHandler
    {
        public int CallCount { get; private set; }

        public List<TextureResult> HandleTextures(RmvFile rmvFile, RmvToGltfExporterSettings settings)
        {
            CallCount++;
            return [];
        }

        public List<TextureResult> HandleTextures(ResolvedModelAsset asset, RmvToGltfExporterSettings settings)
        {
            CallCount++;
            return [];
        }

        public List<TextureResult> HandleTextures(
            ResolvedModelAsset asset,
            RmvToGltfExporterSettings settings,
            GltfTextureExportSession session)
        {
            CallCount++;
            return [];
        }
    }
}
