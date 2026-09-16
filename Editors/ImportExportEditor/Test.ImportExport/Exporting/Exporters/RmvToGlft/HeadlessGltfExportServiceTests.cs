using Editors.ImportExport.Exporting.Exporters.DdsToMaterialPng;
using Editors.ImportExport.Exporting.Exporters.DdsToNormalPng;
using Editors.ImportExport.Exporting.Exporters.RmvToGltf;
using Editors.ImportExport.Exporting.Exporters.RmvToGltf.Helpers;
using GameWorld.Core.Services;
using Moq;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Models.FileSources;

namespace Test.ImportExport.Exporting.Exporters.RmvToGlft;

public sealed class HeadlessGltfExportServiceTests
{
    [Test]
    public void Export_ReturnsStructuredFailure_WhenExporterThrows()
    {
        var modelResolver = new Mock<IModelAssetResolver>();
        modelResolver
            .Setup(x => x.Resolve(It.IsAny<PackFile>()))
            .Throws(new InvalidOperationException("synthetic model failure"));

        var packFileService = new Mock<IPackFileService>().Object;
        var exporter = new RmvToGltfExporter(
            new HeadlessGltfSceneSaver(),
            new GltfMeshBuilder(),
            new GltfTextureHandler(
                new Mock<IDdsToNormalPngExporter>().Object,
                new Mock<IDdsToMaterialPngExporter>().Object,
                packFileService),
            new GltfSkeletonBuilder(packFileService),
            new GltfAnimationBuilder(packFileService),
            new Mock<ISkeletonAnimationLookUpHelper>().Object,
            modelResolver.Object,
            variantMeshResolver: null,
            missingSkeletonDecision: new HeadlessMissingSkeletonDecision());
        var service = new HeadlessGltfExportService(exporter);
        var outputPath = Path.Combine(Path.GetTempPath(), $"headless-export-failure-{Guid.NewGuid():N}.glb");

        var result = service.Export(new RmvToGltfExporterSettings(
            PackFile.CreateFromBytes("synthetic.rigid_model_v2", []),
            [],
            outputPath,
            ExportMaterials: false,
            ConvertMaterialTextureToBlender: false,
            ConvertNormalTextureToBlue: false,
            ExportAnimations: false,
            MirrorMesh: false));

        Assert.That(result.Success, Is.False);
        Assert.That(result.PrimaryFile, Is.Null);
        Assert.That(result.Errors, Has.Count.EqualTo(1));
        Assert.That(result.Errors[0].Code, Is.EqualTo("ExportFailed"));
        Assert.That(result.Errors[0].Message, Does.Contain("synthetic model failure"));
        Assert.That(File.Exists(outputPath), Is.False);
    }
}
