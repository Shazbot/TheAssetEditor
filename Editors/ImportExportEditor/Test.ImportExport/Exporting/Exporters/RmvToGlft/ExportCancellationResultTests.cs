using Editors.ImportExport.Exporting.Exporters;
using Editors.ImportExport.Exporting.Exporters.DdsToMaterialPng;
using Editors.ImportExport.Exporting.Exporters.DdsToNormalPng;
using Editors.ImportExport.Exporting.Exporters.RmvToGltf;
using Editors.ImportExport.Exporting.Exporters.RmvToGltf.Helpers;
using Editors.ImportExport.Misc;
using GameWorld.Core.Services;
using Moq;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.MaterialHeaders;
using SharpGLTF.Schema2;

namespace Test.ImportExport.Exporting.Exporters.RmvToGlft;

public sealed class ExportCancellationResultTests
{
    [Test]
    public void RmvExporter_ReturnsCancelledAndDoesNotSave_WhenMissingSkeletonDecisionCancels()
    {
        var input = PackFile.CreateFromASCII("model.rigid_model_v2", "model");
        var resolvedAsset = CreateAsset(input, "missing_skeleton");
        var modelResolver = new Mock<IModelAssetResolver>();
        modelResolver.Setup(x => x.Resolve(input)).Returns(resolvedAsset);
        var packFileService = new Mock<IPackFileService>();
        var sceneSaver = new RecordingSceneSaver();
        var decision = new CancellingMissingSkeletonDecision();
        var exporter = CreateExporter(
            input,
            modelResolver.Object,
            packFileService.Object,
            sceneSaver,
            decision);

        var result = exporter.Export(CreateSettings(
            input,
            Path.Combine(Path.GetTempPath(), $"cancelled-export-{Guid.NewGuid():N}.glb"),
            exportAnimations: true,
            includeSkeleton: true));

        Assert.That(result.Status, Is.EqualTo(ExportExecutionStatus.Cancelled));
        Assert.That(sceneSaver.SaveCount, Is.EqualTo(0));
        Assert.That(decision.Context?.SkeletonName, Is.EqualTo("missing_skeleton"));
    }

    [Test]
    public void HeadlessService_ReturnsExportCancelled_WhenExporterCancelsAndOutputAlreadyExists()
    {
        var outputDirectory = CreateOutputDirectory();
        try
        {
            var outputPath = Path.Combine(outputDirectory, "model.glb");
            File.WriteAllText(outputPath, "old output");
            var exporter = new StubExporter(ExportExecutionResult.Cancelled());
            var result = new HeadlessGltfExportService(exporter).Export(CreateSettings(
                PackFile.CreateFromASCII("model.rigid_model_v2", "model"),
                outputPath,
                exportAnimations: false,
                includeSkeleton: false));

            Assert.That(result.Success, Is.False);
            Assert.That(result.PrimaryFile, Is.Null);
            Assert.That(result.Errors, Has.Count.EqualTo(1));
            Assert.That(result.Errors[0].Code, Is.EqualTo("ExportCancelled"));
            Assert.That(result.Errors[0].Message, Is.EqualTo("The export was cancelled."));
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Test]
    public void HeadlessService_ReturnsOutputNotUpdated_WhenCompletedExporterLeavesExistingOutputUnchanged()
    {
        var outputDirectory = CreateOutputDirectory();
        try
        {
            var outputPath = Path.Combine(outputDirectory, "model.glb");
            File.WriteAllText(outputPath, "old output");
            var exporter = new StubExporter(ExportExecutionResult.Completed());
            var result = new HeadlessGltfExportService(exporter).Export(CreateSettings(
                PackFile.CreateFromASCII("model.rigid_model_v2", "model"),
                outputPath,
                exportAnimations: false,
                includeSkeleton: false));

            Assert.That(result.Success, Is.False);
            Assert.That(result.PrimaryFile, Is.Null);
            Assert.That(result.Errors, Has.Count.EqualTo(1));
            Assert.That(result.Errors[0].Code, Is.EqualTo("OutputNotUpdated"));
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Test]
    public void HeadlessService_ReturnsSuccess_WhenCompletedExporterCreatesOutput()
    {
        var outputDirectory = CreateOutputDirectory();
        try
        {
            var outputPath = Path.Combine(outputDirectory, "model.glb");
            var exporter = new StubExporter(
                ExportExecutionResult.Completed(),
                settings => File.WriteAllText(settings.OutputPath, "new output"));
            var result = new HeadlessGltfExportService(exporter).Export(CreateSettings(
                PackFile.CreateFromASCII("model.rigid_model_v2", "model"),
                outputPath,
                exportAnimations: false,
                includeSkeleton: false));

            Assert.That(result.Success, Is.True);
            Assert.That(result.PrimaryFile, Is.EqualTo(Path.GetFullPath(outputPath)));
            Assert.That(result.Errors, Is.Empty);
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    private static RmvToGltfExporter CreateExporter(
        PackFile input,
        IModelAssetResolver modelResolver,
        IPackFileService packFileService,
        RecordingSceneSaver sceneSaver,
        IMissingSkeletonDecision missingSkeletonDecision)
    {
        return new RmvToGltfExporter(
            sceneSaver,
            new GltfMeshBuilder(),
            new GltfTextureHandler(
                new Mock<IDdsToNormalPngExporter>().Object,
                new Mock<IDdsToMaterialPngExporter>().Object,
                packFileService),
            new GltfSkeletonBuilder(packFileService),
            new GltfAnimationBuilder(packFileService),
            new Mock<ISkeletonAnimationLookUpHelper>().Object,
            modelResolver,
            variantMeshResolver: null,
            missingSkeletonDecision);
    }

    private static RmvToGltfExporterSettings CreateSettings(
        PackFile input,
        string outputPath,
        bool exportAnimations,
        bool includeSkeleton)
        => new(
            input,
            [],
            outputPath,
            ExportMaterials: false,
            ConvertMaterialTextureToBlender: false,
            ConvertNormalTextureToBlue: false,
            ExportAnimations: exportAnimations,
            MirrorMesh: false)
        {
            IncludeSkeleton = includeSkeleton
        };

    private static ResolvedModelAsset CreateAsset(PackFile input, string skeletonName)
    {
        var material = new WeightedMaterial { ModelName = Path.GetFileNameWithoutExtension(input.Name) };
        var model = new RmvModel { Material = material };
        var rmv = new RmvFile
        {
            Header = new RmvFileHeader { SkeletonName = skeletonName, LodCount = 1 },
            ModelList = [new[] { model }],
            LodHeaders = []
        };
        var part = new ResolvedModelPart(0, 0, model, ResolvedModelMaterial.Create(material));
        return new ResolvedModelAsset(input, input, null, null, rmv, [new[] { part }], []);
    }

    private static string CreateOutputDirectory()
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "export-cancellation-result-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);
        return outputDirectory;
    }

    private static void DeleteOutputDirectory(string outputDirectory)
    {
        if (Directory.Exists(outputDirectory))
            Directory.Delete(outputDirectory, recursive: true);
    }

    private sealed class CancellingMissingSkeletonDecision : IMissingSkeletonDecision
    {
        public MissingSkeletonContext? Context { get; private set; }

        public MissingSkeletonAction Decide(MissingSkeletonContext context)
        {
            Context = context;
            return MissingSkeletonAction.CancelExport;
        }

        public bool ContinueWithoutSkeleton(string skeletonName) => false;
    }

    private sealed class RecordingSceneSaver : IGltfSceneSaver
    {
        public int SaveCount { get; private set; }

        public void Save(ModelRoot modelRoot, string fullSystemPath)
            => SaveCount++;
    }

    private sealed class StubExporter : IRmvToGltfExporter
    {
        private readonly ExportExecutionResult _result;
        private readonly Action<RmvToGltfExporterSettings>? _onExport;

        public StubExporter(
            ExportExecutionResult result,
            Action<RmvToGltfExporterSettings>? onExport = null)
        {
            _result = result;
            _onExport = onExport;
        }

        public ExportSupportEnum CanExportFile(PackFile file)
            => ExportSupportEnum.HighPriority;

        public ExportExecutionResult Export(RmvToGltfExporterSettings settings)
        {
            _onExport?.Invoke(settings);
            return _result;
        }
    }
}
