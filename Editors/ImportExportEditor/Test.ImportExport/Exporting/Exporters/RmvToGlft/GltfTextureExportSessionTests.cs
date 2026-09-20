using System.IO;
using System.Numerics;
using Editors.ImportExport.Exporting.Exporters.DdsToMaterialPng;
using Editors.ImportExport.Exporting.Exporters.DdsToNormalPng;
using Editors.ImportExport.Exporting.Exporters.RmvToGltf;
using Editors.ImportExport.Exporting.Exporters.RmvToGltf.Helpers;
using GameWorld.Core.Services;
using MeshImportExport;
using Moq;
using Shared.Core.PackFiles.Models;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.LodHeader;
using Shared.GameFormats.RigidModel.MaterialHeaders;
using Shared.GameFormats.RigidModel.Types;
using Shared.GameFormats.RigidModel.Vertex;
using SharpGLTF.Materials;
using SharpGLTF.Schema2;

namespace Test.ImportExport.Exporting.Exporters.RmvToGlft;

public class GltfTextureExportSessionTests
{
    [Test]
    public void ComposedSessionReusesAnIdenticalSourcePath()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"asset-editor-textures-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        try
        {
            var materialExporter = new Mock<IDdsToMaterialPngExporter>();
            materialExporter
                .Setup(x => x.ExportWithData(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
                .Returns((string source, string output, bool _) =>
                {
                    var path = Path.Combine(Path.GetDirectoryName(output)!, "shared.png");
                    var pngData = System.Text.Encoding.UTF8.GetBytes(source);
                    File.WriteAllBytes(path, pngData);
                    return new TexturePngExportResult(path, pngData);
                });

            var handler = new GltfTextureHandler(new Mock<IDdsToNormalPngExporter>().Object, materialExporter.Object);
            var asset = CreateAsset("textures\\same\\shared.dds", "textures/same/shared.dds");
            var settings = new RmvToGltfExporterSettings(
                asset.InputFile,
                [],
                Path.Combine(outputDirectory, "model.glb"),
                true,
                false,
                false,
                false,
                false);

            var textures = handler.HandleTextures(asset, settings, new GltfTextureExportSession(collisionSafe: true));

            Assert.That(textures, Has.Count.EqualTo(2));
            Assert.That(textures.Select(x => x.SystemFilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(1));
            Assert.That(textures.Select(x => x.SourceVirtualPath).Distinct(StringComparer.OrdinalIgnoreCase), Is.EqualTo(new[]
            {
                "textures/same/shared.dds"
            }));
            materialExporter.Verify(x => x.ExportWithData(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Once);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Test]
    public void ComposedSessionDisambiguatesDifferentSourcesWithTheSameBasename()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"asset-editor-textures-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        try
        {
            var materialExporter = new Mock<IDdsToMaterialPngExporter>();
            materialExporter
                .Setup(x => x.ExportWithData(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
                .Returns((string source, string output, bool _) =>
                {
                    var path = Path.Combine(Path.GetDirectoryName(output)!, "shared.png");
                    var pngData = System.Text.Encoding.UTF8.GetBytes(source);
                    File.WriteAllBytes(path, pngData);
                    return new TexturePngExportResult(path, pngData);
                });

            var normalExporter = new Mock<IDdsToNormalPngExporter>();
            var handler = new GltfTextureHandler(normalExporter.Object, materialExporter.Object);
            var asset = CreateAsset(
                "textures\\first\\shared.dds",
                "textures\\second\\shared.dds");
            var settings = new RmvToGltfExporterSettings(
                asset.InputFile,
                [],
                Path.Combine(outputDirectory, "model.glb"),
                true,
                false,
                false,
                false,
                false)
            {
                MaxTextureParallelism = 2
            };

            var textures = handler.HandleTextures(asset, settings, new GltfTextureExportSession(collisionSafe: true));

            Assert.That(textures, Has.Count.EqualTo(2));
            Assert.That(textures.Select(x => x.SystemFilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(2));
            Assert.That(textures.Select(x => x.SourceVirtualPath), Is.EquivalentTo(new[]
            {
                "textures/first/shared.dds",
                "textures/second/shared.dds"
            }));
            Assert.That(textures.All(x => File.Exists(x.SystemFilePath)), Is.True);
            materialExporter.Verify(x => x.ExportWithData(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Exactly(2));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Test]
    public async Task ParallelTextureExportRunsIndependentBasenamesConcurrentlyAndPreservesOrder()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"asset-editor-textures-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        using var entered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim(false);

        try
        {
            var materialExporter = new Mock<IDdsToMaterialPngExporter>();
            materialExporter
                .Setup(x => x.ExportKtx2WithData(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<bool>(),
                    It.IsAny<bool>()))
                .Returns((string source, string output, bool _, bool _) =>
                {
                    entered.Signal();
                    if (!release.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Parallel texture conversion did not release in time.");

                    var stem = Path.GetFileNameWithoutExtension(source);
                    var path = Path.Combine(Path.GetDirectoryName(output)!, stem + ".ktx2");
                    return new TextureImageExportResult(path, [1, 2, 3, 4]);
                });

            var handler = new GltfTextureHandler(
                new Mock<IDdsToNormalPngExporter>().Object,
                materialExporter.Object);
            var asset = CreateAsset("textures/first.dds", "textures/second.dds");
            var settings = new RmvToGltfExporterSettings(
                asset.InputFile,
                [],
                Path.Combine(outputDirectory, "model.glb"),
                true,
                false,
                false,
                false,
                false)
            {
                UseKtx2Textures = true,
                ExportAuxiliaryMasks = false,
                MaxTextureParallelism = 2
            };

            var exportTask = Task.Run(() =>
                handler.HandleTextures(
                    asset,
                    settings,
                    new GltfTextureExportSession(collisionSafe: true)));

            var bothEntered = entered.Wait(TimeSpan.FromSeconds(5));
            release.Set();
            var textures = await exportTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(bothEntered, Is.True, "Both distinct texture conversions should enter before either is released.");
            Assert.That(textures, Has.Count.EqualTo(2));
            Assert.That(Path.GetFileName(textures[0].SystemFilePath), Does.StartWith("first_"));
            Assert.That(Path.GetFileName(textures[1].SystemFilePath), Does.StartWith("second_"));
            Assert.That(textures.All(x => x.ImageData != null && x.ImageData.SequenceEqual(new byte[] { 1, 2, 3, 4 })), Is.True);
        }
        finally
        {
            release.Set();
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Test]
    public void MeshBuilderUsesInMemoryTextureDataWhenGeneratedFileIsMissing()
    {
        var asset = CreateTexturedAsset(
            "textures/body_base_colour.dds",
            "textures/body_mask.dds");
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            $"missing-texture-{Guid.NewGuid():N}.png");
        var settings = new RmvToGltfExporterSettings(
            asset.InputFile,
            [],
            Path.Combine(Path.GetTempPath(), $"model-{Guid.NewGuid():N}.glb"),
            true,
            false,
            false,
            false,
            false);
        var textures = new List<TextureResult>
        {
            new(0, missingPath, KnownChannel.BaseColor)
            {
                ImageData = OnePixelPng
            }
        };

        var meshBuilder = new GltfMeshBuilder()
            .Build(asset, textures, settings, willHaveSkeleton: false)
            .Single();
        var model = ModelRoot.CreateModel();

        Assert.That(File.Exists(missingPath), Is.False);
        model.CreateMesh(meshBuilder);

        var image = model.LogicalImages.Single();
        Assert.That(image.Content.SourcePath, Is.Null);
        Assert.That(image.Content.Content.ToArray(), Is.EqualTo(OnePixelPng));
    }

    [Test]
    public void HandlerKeepsCachedKtx2BytesAcrossExportSessions()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), $"asset-editor-textures-{Guid.NewGuid():N}");
        var firstDirectory = Path.Combine(rootDirectory, "first");
        var secondDirectory = Path.Combine(rootDirectory, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var encoded = new byte[] { 10, 20, 30, 40 };

        try
        {
            var materialExporter = new Mock<IDdsToMaterialPngExporter>();
            materialExporter
                .Setup(x => x.ExportKtx2WithData(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<bool>(),
                    It.IsAny<bool>()))
                .Returns((string source, string output, bool convertToBlender, bool srgb) =>
                {
                    var path = Path.Combine(Path.GetDirectoryName(output)!, "cache.ktx2");
                    return new TextureImageExportResult(path, encoded);
                });

            var handler = new GltfTextureHandler(
                new Mock<IDdsToNormalPngExporter>().Object,
                materialExporter.Object);
            var asset = CreateAsset("textures/cache.dds", "textures/cache.dds");
            var firstSettings = new RmvToGltfExporterSettings(
                asset.InputFile,
                [],
                Path.Combine(firstDirectory, "model.glb"),
                true,
                false,
                false,
                false,
                false)
            {
                UseKtx2Textures = true,
                ExportAuxiliaryMasks = false
            };
            var secondSettings = firstSettings with
            {
                OutputPath = Path.Combine(secondDirectory, "model.glb")
            };

            var firstTextures = handler.HandleTextures(
                asset,
                firstSettings,
                new GltfTextureExportSession(collisionSafe: false));
            var secondTextures = handler.HandleTextures(
                asset,
                secondSettings,
                new GltfTextureExportSession(collisionSafe: false));

            materialExporter.Verify(
                x => x.ExportKtx2WithData(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<bool>(),
                    It.IsAny<bool>()),
                Times.Once);
            Assert.That(firstTextures.All(x => x.ImageData != null && x.ImageData.SequenceEqual(encoded)), Is.True);
            Assert.That(secondTextures.All(x => x.ImageData != null && x.ImageData.SequenceEqual(encoded)), Is.True);
            Assert.That(File.Exists(Path.Combine(secondDirectory, "cache.ktx2")), Is.True);
        }
        finally
        {
            if (Directory.Exists(rootDirectory))
                Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Test]
    public void HandlerCachesConvertedPngAcrossExportSessions()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), $"asset-editor-textures-{Guid.NewGuid():N}");
        var firstDirectory = Path.Combine(rootDirectory, "first");
        var secondDirectory = Path.Combine(rootDirectory, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);

        try
        {
            var materialExporter = new Mock<IDdsToMaterialPngExporter>();
            materialExporter
                .Setup(x => x.ExportWithData(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
                .Returns((string source, string output, bool _) =>
                {
                    var path = Path.Combine(Path.GetDirectoryName(output)!, "cache.png");
                    // Deliberately do not write the first export to disk. The
                    // conversion cache must be populated from returned bytes.
                    return new TexturePngExportResult(path, OnePixelPng);
                });

            var handler = new GltfTextureHandler(new Mock<IDdsToNormalPngExporter>().Object, materialExporter.Object);
            var asset = CreateAsset("textures/cache.dds", "textures/cache.dds");
            var firstSettings = new RmvToGltfExporterSettings(
                asset.InputFile,
                [],
                Path.Combine(firstDirectory, "model.glb"),
                true,
                false,
                false,
                false,
                false);
            var secondSettings = firstSettings with
            {
                OutputPath = Path.Combine(secondDirectory, "model.glb")
            };

            handler.HandleTextures(asset, firstSettings, new GltfTextureExportSession(collisionSafe: false));
            var secondTextures = handler.HandleTextures(
                asset,
                secondSettings,
                new GltfTextureExportSession(collisionSafe: false));

            materialExporter.Verify(
                x => x.ExportWithData(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()),
                Times.Once);
            Assert.That(secondTextures, Has.Count.EqualTo(2));
            Assert.That(secondTextures.Select(x => x.SystemFilePath).Distinct().Single(),
                Is.EqualTo(Path.Combine(secondDirectory, "cache.png")));
            Assert.That(File.Exists(Path.Combine(secondDirectory, "cache.png")), Is.True);
        }
        finally
        {
            if (Directory.Exists(rootDirectory))
                Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Test]
    public void AuxiliaryMaskExportCanBeDisabled()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"asset-editor-textures-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        try
        {
            var baseColourPath = Path.Combine(outputDirectory, "body_base_colour.png");
            File.WriteAllBytes(baseColourPath, OnePixelPng);
            var materialExporter = new Mock<IDdsToMaterialPngExporter>();
            materialExporter
                .Setup(x => x.ExportWithData(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
                .Returns((string source, string output, bool convertToBlender) =>
                    source.EndsWith("base_colour.dds", StringComparison.OrdinalIgnoreCase)
                        ? new TexturePngExportResult(baseColourPath, OnePixelPng)
                        : throw new InvalidOperationException("The auxiliary mask should not be exported."));

            var asset = CreateTexturedAsset(
                "textures/body_base_colour.dds",
                "textures/body_mask.dds");
            var settings = new RmvToGltfExporterSettings(
                asset.InputFile,
                [],
                Path.Combine(outputDirectory, "model.glb"),
                true,
                false,
                false,
                false,
                false)
            {
                ExportAuxiliaryMasks = false
            };
            var handler = new GltfTextureHandler(
                new Mock<IDdsToNormalPngExporter>().Object,
                materialExporter.Object);

            var textures = handler.HandleTextures(
                asset,
                settings,
                new GltfTextureExportSession(collisionSafe: false));

            Assert.That(textures, Has.Count.EqualTo(1));
            Assert.That(textures[0].GltfTextureType, Is.EqualTo(KnownChannel.BaseColor));
            materialExporter.Verify(
                x => x.ExportWithData(
                    It.Is<string>(path => path.EndsWith("body_mask.dds", StringComparison.OrdinalIgnoreCase)),
                    It.IsAny<string>(),
                    It.IsAny<bool>()),
                Times.Never);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Test]
    public void MaskExportDoesNotReplaceBaseColourInFinalGlbMaterial()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"asset-editor-textures-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        var outputPath = Path.Combine(outputDirectory, "model.glb");
        var baseColourPath = Path.Combine(outputDirectory, "body_base_colour.png");
        var maskPath = Path.Combine(outputDirectory, "body_mask.png");
        File.WriteAllBytes(baseColourPath, OnePixelPng);

        try
        {
            var materialExporter = new Mock<IDdsToMaterialPngExporter>();
            materialExporter
                .Setup(x => x.ExportWithData(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
                .Returns((string source, string output, bool convertToBlender) =>
                    source.EndsWith("base_colour.dds", StringComparison.OrdinalIgnoreCase)
                        ? new TexturePngExportResult(baseColourPath, OnePixelPng)
                        // Keep the auxiliary path absent and return no image bytes,
                        // so this channel-binding test does not invoke GDI+.
                        : new TexturePngExportResult(maskPath, Array.Empty<byte>()));

            var normalExporter = new Mock<IDdsToNormalPngExporter>();
            var handler = new GltfTextureHandler(normalExporter.Object, materialExporter.Object);
            var asset = CreateTexturedAsset(
                "textures/body_base_colour.dds",
                "textures/body_mask.dds");
            var settings = new RmvToGltfExporterSettings(
                asset.InputFile,
                [],
                outputPath,
                true,
                false,
                false,
                false,
                false);

            var textures = handler.HandleTextures(
                asset,
                settings,
                new GltfTextureExportSession(collisionSafe: false));

            materialExporter.Verify(
                x => x.ExportWithData(
                    It.Is<string>(path => path.EndsWith("base_colour.dds", StringComparison.OrdinalIgnoreCase)),
                    It.IsAny<string>(),
                    It.IsAny<bool>()),
                Times.Once);
            materialExporter.Verify(
                x => x.ExportWithData(
                    It.Is<string>(path => path.EndsWith("body_mask.dds", StringComparison.OrdinalIgnoreCase)),
                    It.IsAny<string>(),
                    It.IsAny<bool>()),
                Times.Once);

            Assert.That(textures, Has.Count.EqualTo(1));
            Assert.That(textures[0].GltfTextureType, Is.EqualTo(KnownChannel.BaseColor));
            Assert.That(textures[0].SystemFilePath, Is.EqualTo(baseColourPath));

            var meshBuilder = new GltfMeshBuilder()
                .Build(asset, textures, settings, willHaveSkeleton: false)
                .Single();
            var model = ModelRoot.CreateModel();
            var scene = model.UseScene("default");
            var mesh = model.CreateMesh(meshBuilder);
            scene.CreateNode("body").WithMesh(mesh);

            var baseColourChannel = model.LogicalMaterials.Single().FindChannel("BaseColor");
            Assert.That(baseColourChannel, Is.Not.Null);
            var baseColourImage = baseColourChannel!.Value.Texture!.PrimaryImage!;
            Assert.That(baseColourImage.Content.SourcePath, Does.EndWith("body_base_colour.png"));
            Assert.That(baseColourImage.Content.SourcePath, Does.Not.EndWith("body_mask.png"));

            var staticModel = ModelRoot.CreateModel();
            staticModel.CreateMesh(new GltfStaticMeshBuilder().Build(asset, textures, settings).Single());
            Assert.That(staticModel.LogicalMaterials.Single().Alpha, Is.EqualTo(SharpGLTF.Schema2.AlphaMode.MASK));

            model.Save(outputPath);
            ModelRoot.Validate(outputPath);
            var reloaded = ModelRoot.Load(outputPath);
            var reloadedBaseColourChannel = reloaded.LogicalMaterials.Single().FindChannel("BaseColor");
            Assert.That(reloadedBaseColourChannel, Is.Not.Null);
            Assert.That(reloadedBaseColourChannel!.Value.Texture, Is.Not.Null);
            Assert.That(reloaded.LogicalImages, Has.Count.EqualTo(1));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static ResolvedModelAsset CreateAsset(string firstTexture, string secondTexture)
    {
        var firstModel = CreateModel("first", firstTexture);
        var secondModel = CreateModel("second", secondTexture);
        var models = new[] { firstModel, secondModel };
        var header = new RmvFileHeader { Version = RmvVersionEnum.RMV2_V6, LodCount = 1 };
        header.SkeletonName = string.Empty;
        var rmv = new RmvFile
        {
            Header = header,
            ModelList = new[] { models },
            LodHeaders = new[] { LodHeaderFactory.Create().CreateEmpty(RmvVersionEnum.RMV2_V6, 0, 0, 0) }
        };

        var parts = new[]
        {
            new ResolvedModelPart(0, 0, firstModel, ResolvedModelMaterial.Create(firstModel.Material)),
            new ResolvedModelPart(0, 1, secondModel, ResolvedModelMaterial.Create(secondModel.Material))
        };
        var input = PackFile.CreateFromASCII("model.rigid_model_v2", "model");
        return new ResolvedModelAsset(input, input, null, null, rmv, new[] { parts }, []);
    }

    private static RmvModel CreateModel(string name, string texture)
    {
        var material = new WeightedMaterial { ModelName = name };
        material.SetTexture(TextureType.Diffuse, texture);
        return new RmvModel
        {
            Material = material,
            Mesh = new RmvMesh { VertexList = [], IndexList = [] }
        };
    }

    private static ResolvedModelAsset CreateTexturedAsset(string baseColourPath, string maskPath)
    {
        var material = new WeightedMaterial { ModelName = "textured" };
        material.SetTexture(TextureType.BaseColour, baseColourPath);
        material.SetTexture(TextureType.Mask, maskPath);

        var model = new RmvModel
        {
            Material = material,
            Mesh = new RmvMesh
            {
                VertexList =
                [
                    CreateVertex(0, 0, 0),
                    CreateVertex(1, 0, 0),
                    CreateVertex(0, 1, 0)
                ],
                IndexList = [0, 1, 2]
            }
        };
        var header = new RmvFileHeader
        {
            Version = RmvVersionEnum.RMV2_V6,
            LodCount = 1,
            SkeletonName = string.Empty
        };
        var rmv = new RmvFile
        {
            Header = header,
            ModelList = [new[] { model }],
            LodHeaders = [LodHeaderFactory.Create().CreateEmpty(RmvVersionEnum.RMV2_V6, 0, 0, 0)]
        };
        var input = PackFile.CreateFromASCII("textured.rigid_model_v2", "textured");
        var part = new ResolvedModelPart(0, 0, model, ResolvedModelMaterial.Create(material));
        return new ResolvedModelAsset(input, input, null, null, rmv, [new[] { part }], []);
    }

    private static CommonVertex CreateVertex(float x, float y, float z)
        => new()
        {
            Position = new Vector4(x, y, z, 1),
            Normal = Vector3.UnitZ,
            Tangent = Vector3.UnitX,
            Uv = Vector2.Zero,
            BoneIndex = new byte[4],
            BoneWeight = new float[4]
        };

    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
}
