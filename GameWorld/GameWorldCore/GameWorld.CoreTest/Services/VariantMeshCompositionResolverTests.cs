using GameWorld.Core.Services;
using Moq;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.LodHeader;
using Shared.GameFormats.RigidModel.MaterialHeaders;
using Shared.GameFormats.RigidModel.Types;
using Shared.TestUtility;
using Test.TestingUtility.TestUtility;

namespace GameWorld.Core.Test.Services;

public class VariantMeshCompositionResolverTests
{
    private readonly string _romePack = PathHelper.GetDataFolder("Data\\Rome_Man_And_Shield_Pack");

    [Test]
    public void SelectsFirstCandidateThatLoadsSuccessfully()
    {
        var first = PackFile.CreateFromASCII("first.rigid_model_v2", "first");
        var second = PackFile.CreateFromASCII("second.rigid_model_v2", "second");
        var root = PackFile.CreateFromASCII("root.variantmeshdefinition", """
            <VARIANT_MESH>
              <SLOT name="weapon" attach_point="hand">
                <VARIANT_MESH_REFERENCE definition="first.rigid_model_v2" />
                <VARIANT_MESH_REFERENCE definition="second.rigid_model_v2" />
              </SLOT>
            </VARIANT_MESH>
            """);

        var files = new[] { root, first, second };
        var packFileService = CreatePackFileService(files);
        var modelResolver = new Mock<IModelAssetResolver>(MockBehavior.Strict);
        modelResolver
            .Setup(x => x.Resolve(first))
            .Throws(new ModelAssetResolutionException("broken first candidate"));
        modelResolver
            .Setup(x => x.Resolve(second))
            .Returns(CreatePlaceholderAsset(second));

        var result = new VariantMeshCompositionResolver(packFileService.Object, modelResolver.Object).Resolve(root);

        Assert.That(result.HasRenderableContent, Is.True);
        Assert.That(result.Root!.Slots, Has.Count.EqualTo(1));
        Assert.That(result.Root.Slots[0].AttachmentPoint, Is.EqualTo("hand"));
        Assert.That(result.Root.Slots[0].SelectedChild!.ModelAsset!.InputFile, Is.SameAs(second));
        Assert.That(result.Diagnostics.Any(x => x.Contains("broken first candidate", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void SelectsRequestedCandidatesAtEveryNestedSlot()
    {
        var root = PackFile.CreateFromASCII("root.variantmeshdefinition", """
            <VARIANT_MESH>
              <SLOT name="stump_neck">
                <VARIANT_MESH model="missing_stump.rigid_model_v2" />
              </SLOT>
              <SLOT name="head">
                <VARIANT_MESH model="head_0.rigid_model_v2" />
                <VARIANT_MESH model="head_1.rigid_model_v2" />
                <VARIANT_MESH model="head_2.rigid_model_v2" />
                <VARIANT_MESH model="head_3.rigid_model_v2" />
                <VARIANT_MESH model="head_4.rigid_model_v2" />
              </SLOT>
              <SLOT name="body">
                <VARIANT_MESH model="body_0.rigid_model_v2" />
                <VARIANT_MESH model="body_1.rigid_model_v2" />
                <VARIANT_MESH model="body_2.rigid_model_v2" />
              </SLOT>
              <SLOT name="weapon_1">
                <VARIANT_MESH_REFERENCE definition="weapon_1.variantmeshdefinition" />
              </SLOT>
            </VARIANT_MESH>
            """);
        var weaponDefinition = PackFile.CreateFromASCII("weapon_1.variantmeshdefinition", """
            <VARIANT_MESH>
              <SLOT name="weapon">
                <VARIANT_MESH model="weapon_0.rigid_model_v2" />
                <VARIANT_MESH model="weapon_1.rigid_model_v2" />
              </SLOT>
            </VARIANT_MESH>
            """);
        var models = new[]
        {
            PackFile.CreateFromASCII("head_0.rigid_model_v2", "head0"),
            PackFile.CreateFromASCII("head_1.rigid_model_v2", "head1"),
            PackFile.CreateFromASCII("head_2.rigid_model_v2", "head2"),
            PackFile.CreateFromASCII("head_3.rigid_model_v2", "head3"),
            PackFile.CreateFromASCII("head_4.rigid_model_v2", "head4"),
            PackFile.CreateFromASCII("body_0.rigid_model_v2", "body0"),
            PackFile.CreateFromASCII("body_1.rigid_model_v2", "body1"),
            PackFile.CreateFromASCII("body_2.rigid_model_v2", "body2"),
            PackFile.CreateFromASCII("weapon_0.rigid_model_v2", "weapon0"),
            PackFile.CreateFromASCII("weapon_1.rigid_model_v2", "weapon1")
        };
        var packFileService = CreatePackFileService([root, weaponDefinition, .. models]);
        var modelResolver = new Mock<IModelAssetResolver>(MockBehavior.Strict);
        modelResolver
            .Setup(x => x.Resolve(It.IsAny<PackFile>()))
            .Returns((PackFile file) => CreatePlaceholderAsset(file));

        var result = new VariantMeshCompositionResolver(packFileService.Object, modelResolver.Object).Resolve(
            root,
            [
                new VariantMeshSelection("root/slot[0]", 4),
                new VariantMeshSelection("root/slot[1]", 2),
                new VariantMeshSelection("root/slot[2]/choice[0]/slot[0]", 1)
            ]);

        var selectedHead = result.Root!.Slots[0].SelectedChild!.ResolvedModelReference!.ModelAsset!.InputFile;
        var selectedBody = result.Root.Slots[1].SelectedChild!.ResolvedModelReference!.ModelAsset!.InputFile;
        var selectedWeapon = result.Root.Slots[2].SelectedChild!.Slots[0].SelectedChild!
            .ResolvedModelReference!.ModelAsset!.InputFile;

        Assert.That(result.Root.Slots, Has.Count.EqualTo(3));
        Assert.That(selectedHead, Is.SameAs(models[4]));
        Assert.That(selectedBody, Is.SameAs(models[7]));
        Assert.That(selectedWeapon, Is.SameAs(models[9]));
        Assert.That(result.Diagnostics, Is.Empty);
    }

    [Test]
    public void ParsedDefinitionCacheCanBeClearedBetweenAssetSessions()
    {
        var root = PackFile.CreateFromASCII("root.variantmeshdefinition", """
            <VARIANT_MESH>
              <SLOT name="body">
                <VARIANT_MESH model="body.rigid_model_v2" />
              </SLOT>
            </VARIANT_MESH>
            """);
        var body = PackFile.CreateFromASCII("body.rigid_model_v2", "body");
        var packFileService = CreatePackFileService(root, body);
        var modelResolver = new Mock<IModelAssetResolver>(MockBehavior.Strict);
        modelResolver
            .Setup(x => x.Resolve(body))
            .Returns(CreatePlaceholderAsset(body));
        var resolver = new VariantMeshCompositionResolver(
            packFileService.Object,
            modelResolver.Object,
            cacheParsedDefinitions: true);

        var first = resolver.Resolve(root);
        Assert.That(first.HasRenderableContent, Is.True);
        Assert.That(resolver.CachedDefinitionCount, Is.EqualTo(1));

        root.DataSource = PackFile.CreateFromASCII("invalid.variantmeshdefinition", "not xml").DataSource;
        var cached = resolver.Resolve(root);
        Assert.That(cached.HasRenderableContent, Is.True, "The current asset session should reuse the parsed VMD.");

        resolver.ClearParsedDefinitionCache();
        var afterClear = resolver.Resolve(root);
        Assert.That(afterClear.HasRenderableContent, Is.False, "Clearing the session cache must force the VMD to be parsed again.");
    }

    [Test]
    public void DetectsNestedCaseInsensitiveCycleWithDiagnostic()
    {
        var root = PackFile.CreateFromASCII("Models\\Root.variantmeshdefinition", """
            <VARIANT_MESH>
              <SLOT name="child">
                <VARIANT_MESH_REFERENCE definition="models/Child.variantmeshdefinition" />
              </SLOT>
            </VARIANT_MESH>
            """);
        var child = PackFile.CreateFromASCII("Models\\Child.variantmeshdefinition", """
            <VARIANT_MESH>
              <SLOT name="back">
                <VARIANT_MESH_REFERENCE definition="models/root.VARIANTMESHDEFINITION" />
              </SLOT>
            </VARIANT_MESH>
            """);

        var packFileService = CreatePackFileService(root, child);
        var result = new VariantMeshCompositionResolver(packFileService.Object, new Mock<IModelAssetResolver>().Object).Resolve(root);

        Assert.That(result.HasRenderableContent, Is.False);
        Assert.That(result.Diagnostics.Any(x => x.Contains("cycle", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(result.Diagnostics.Any(x => x.Contains("root.variantmeshdefinition", StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public void ResolvesNestedVmdModelReference()
    {
        var root = PackFile.CreateFromASCII("Models\\Root.variantmeshdefinition", """
            <VARIANT_MESH>
              <SLOT name="child" attach_point="hand">
                <VARIANT_MESH_REFERENCE definition="models/Child.variantmeshdefinition" />
              </SLOT>
            </VARIANT_MESH>
            """);
        var child = PackFile.CreateFromASCII("Models\\Child.variantmeshdefinition", """
            <VARIANT_MESH model="models/Body.rigid_model_v2" />
            """);
        var model = PackFile.CreateFromASCII("Models\\Body.rigid_model_v2", "body");
        var packFileService = CreatePackFileService(root, child, model);
        var modelResolver = new Mock<IModelAssetResolver>(MockBehavior.Strict);
        modelResolver
            .Setup(x => x.Resolve(model))
            .Returns(CreatePlaceholderAsset(model));

        var result = new VariantMeshCompositionResolver(packFileService.Object, modelResolver.Object).Resolve(root);

        Assert.That(result.HasRenderableContent, Is.True);
        var selectedChild = result.Root!.Slots.Single().SelectedChild;
        Assert.That(selectedChild, Is.Not.Null);
        Assert.That(selectedChild!.ResolvedModelReference!.ModelAsset!.InputFile, Is.SameAs(model));
        Assert.That(result.Diagnostics, Is.Empty);
    }

    [Test]
    public void DoesNotResolveCandidateRelativeToDefinitionFolder()
    {
        var root = PackFile.CreateFromASCII("Models\\Root.variantmeshdefinition", """
            <VARIANT_MESH>
              <SLOT name="child">
                <VARIANT_MESH_REFERENCE definition="Child.rigid_model_v2" />
              </SLOT>
            </VARIANT_MESH>
            """);
        var nestedCandidate = PackFile.CreateFromASCII("Models\\Child.rigid_model_v2", "child");
        var packFileService = CreatePackFileService(root, nestedCandidate);
        var modelResolver = new Mock<IModelAssetResolver>(MockBehavior.Strict);

        var result = new VariantMeshCompositionResolver(packFileService.Object, modelResolver.Object).Resolve(root);

        Assert.That(result.HasRenderableContent, Is.False);
        Assert.That(modelResolver.Invocations, Is.Empty,
            "Root-relative lookup must not reinterpret a missing candidate as relative to the VMD folder.");
        Assert.That(result.Diagnostics.Any(x => x.Contains("Child.rigid_model_v2", StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public void ResolvesTrackedShieldVariantMeshDefinition()
    {
        var packFileService = PackFileSerivceTestHelper.Create(_romePack);
        var definition = packFileService.FindFile(@"variantmeshes\_variantmodels\man\shield\celtic_oval_patterns.variantmeshdefinition");
        Assert.That(definition, Is.Not.Null);

        var result = new VariantMeshCompositionResolver(packFileService, new ModelAssetResolver(packFileService)).Resolve(definition!);

        Assert.That(result.HasRenderableContent, Is.True);
        Assert.That(result.Root!.Slots, Has.Count.EqualTo(1));
        Assert.That(result.Root.Slots[0].SelectedChild, Is.Not.Null);

        // The tracked slot contains an inline VARIANT_MESH node whose model
        // attribute is represented by ResolvedModelReference.  The exporter
        // intentionally traverses that node before its slot children.
        var selectedChild = result.Root.Slots[0].SelectedChild!;
        Assert.That(selectedChild.ResolvedModelReference, Is.Not.Null);
        Assert.That(selectedChild.ResolvedModelReference!.ModelAsset, Is.Not.Null);
        Assert.That(selectedChild.ResolvedModelReference.ModelAsset!.FirstLod, Is.Not.Empty);
    }

    private static Mock<IPackFileService> CreatePackFileService(params PackFile[] files)
    {
        var byPath = files.ToDictionary(x => Normalize(x.Name), StringComparer.OrdinalIgnoreCase);
        var mock = new Mock<IPackFileService>(MockBehavior.Loose);
        mock.Setup(x => x.FindFile(It.IsAny<string>(), It.IsAny<IPackFileContainer?>()))
            .Returns((string path, IPackFileContainer? _) => byPath.GetValueOrDefault(Normalize(path)));
        mock.Setup(x => x.GetFullPath(It.IsAny<PackFile>(), It.IsAny<IPackFileContainer?>()))
            .Returns((PackFile file, IPackFileContainer? _) => files.First(x => ReferenceEquals(x, file)).Name);
        return mock;
    }

    private static ResolvedModelAsset CreatePlaceholderAsset(PackFile file)
    {
        var model = new RmvModel
        {
            Material = new WeightedMaterial(),
            Mesh = new RmvMesh { VertexList = [], IndexList = [] }
        };
        var header = new RmvFileHeader { Version = RmvVersionEnum.RMV2_V6, LodCount = 1 };
        header.SkeletonName = string.Empty;
        var rmv = new RmvFile
        {
            Header = header,
            ModelList = new[] { new[] { model } },
            LodHeaders = new[] { LodHeaderFactory.Create().CreateEmpty(RmvVersionEnum.RMV2_V6, 0, 0, 0) }
        };
        var part = new ResolvedModelPart(0, 0, model, ResolvedModelMaterial.Create(model.Material));
        return new(file, file, null, null, rmv, new[] { new[] { part } }, []);
    }

    private static string Normalize(string path)
        => path.Replace('/', '\\').Trim().ToLowerInvariant();
}
