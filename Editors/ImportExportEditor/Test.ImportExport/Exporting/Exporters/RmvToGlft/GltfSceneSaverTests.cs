using Editors.ImportExport.Exporting.Exporters.RmvToGltf.Helpers;
using System.IO;
using Moq;
using Shared.Core.Services;
using SharpGLTF.Schema2;

namespace Test.ImportExport.Exporting.Exporters.RmvToGlft;

public class GltfSceneSaverTests
{
    private static byte[] CreateRawKtx2(byte fill) =>
        MeshImportExport.TextureHelper.EncodeBgraToKtx2(
            new MeshImportExport.TextureHelper.DecodedDdsImage(4, 4, Enumerable.Repeat(fill, 64).ToArray()),
            srgb: false).Ktx2Data;

    private static byte[] CreateStub(byte[] ktx2, string sidecarPath) =>
        MeshImportExport.TextureHelper.ToKtx2SidecarStub(
            MeshImportExport.TextureHelper.ToKtx2HeaderOnly(ktx2), sidecarPath);

    [Test]
    public void HeadlessSaverKeepsSameSizedKtx2StubsAsSeparateImages()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"asset-editor-ktx2-stubs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "model.glb");
        var normalPath = Path.Combine(outputDirectory, "body_normal.ktx2");
        var materialPath = Path.Combine(outputDirectory, "body_material_map.ktx2");
        var normal = CreateRawKtx2(10);
        var material = CreateRawKtx2(200);
        File.WriteAllBytes(normalPath, normal);
        File.WriteAllBytes(materialPath, material);

        try
        {
            var materialBuilder = new SharpGLTF.Materials.MaterialBuilder("body").WithMetallicRoughness();
            materialBuilder.WithChannelImage(SharpGLTF.Materials.KnownChannel.Normal, CreateStub(normal, normalPath));
            materialBuilder.WithChannelImage(SharpGLTF.Materials.KnownChannel.MetallicRoughness, CreateStub(material, materialPath));
            materialBuilder.UseChannel(SharpGLTF.Materials.KnownChannel.Normal).Texture.PrimaryImage.AlternateWriteFileName = "body_normal.ktx2";
            materialBuilder.UseChannel(SharpGLTF.Materials.KnownChannel.MetallicRoughness).Texture.PrimaryImage.AlternateWriteFileName = "body_material_map.ktx2";

            var mesh = new SharpGLTF.Geometry.MeshBuilder<SharpGLTF.Geometry.VertexTypes.VertexPosition>("mesh");
            mesh.UsePrimitive(materialBuilder).AddTriangle(
                new SharpGLTF.Geometry.VertexTypes.VertexPosition(0, 0, 0),
                new SharpGLTF.Geometry.VertexTypes.VertexPosition(1, 0, 0),
                new SharpGLTF.Geometry.VertexTypes.VertexPosition(0, 1, 0));
            var scene = new SharpGLTF.Scenes.SceneBuilder();
            scene.AddRigidMesh(mesh, System.Numerics.Matrix4x4.Identity);
            var model = scene.ToGltf2();

            // Two KTX2 images (plus SharpGLTF's PNG fallback), not one merged image.
            Assert.That(model.LogicalImages.Count(image => image.Content.IsKtx2), Is.EqualTo(2));

            new HeadlessGltfSceneSaver().Save(model, outputPath, [normalPath, materialPath]);

            Assert.That(File.ReadAllBytes(normalPath), Is.EqualTo(normal));
            Assert.That(File.ReadAllBytes(materialPath), Is.EqualTo(material));
            var glbText = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(outputPath));
            Assert.That(glbText, Does.Contain("body_normal.ktx2"));
            Assert.That(glbText, Does.Contain("body_material_map.ktx2"));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Test]
    public void HeadlessSaverCopiesSidecarInsteadOfWritingKtx2Stub()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"asset-editor-ktx2-stub-copy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "model.glb");
        var sidecarPath = Path.Combine(outputDirectory, "body.ktx2");
        var ktx2 = CreateRawKtx2(42);
        File.WriteAllBytes(sidecarPath, ktx2);

        try
        {
            var model = ModelRoot.CreateModel();
            model.UseScene("default");
            var image = model.CreateImage("renamed");
            image.Content = new SharpGLTF.Memory.MemoryImage(CreateStub(ktx2, sidecarPath));
            image.AlternateWriteFileName = "renamed.ktx2";

            new HeadlessGltfSceneSaver().Save(model, outputPath, [sidecarPath]);

            Assert.That(File.ReadAllBytes(Path.Combine(outputDirectory, "renamed.ktx2")), Is.EqualTo(ktx2));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Test]
    public void HeadlessSaverRejectsKtx2StubWithoutSidecar()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"asset-editor-ktx2-stub-missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "model.glb");
        var otherGeneratedPath = Path.Combine(outputDirectory, "other.ktx2");
        File.WriteAllBytes(otherGeneratedPath, CreateRawKtx2(1));

        try
        {
            var model = ModelRoot.CreateModel();
            model.UseScene("default");
            var image = model.CreateImage("missing");
            image.Content = new SharpGLTF.Memory.MemoryImage(
                CreateStub(CreateRawKtx2(7), Path.Combine(outputDirectory, "missing.ktx2")));
            image.AlternateWriteFileName = "missing.ktx2";

            Assert.Throws<InvalidOperationException>(
                () => new HeadlessGltfSceneSaver().Save(model, outputPath, [otherGeneratedPath]));
            Assert.That(File.Exists(Path.Combine(outputDirectory, "missing.ktx2")), Is.False);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }
    [Test]
    public void SavesReloadableGlbWhenOutputUsesGlbExtension()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"asset-editor-{Guid.NewGuid():N}.glb");
        var dialogs = new Mock<IStandardDialogs>();

        try
        {
            var model = ModelRoot.CreateModel();
            model.UseScene("default");
            var saver = new GltfSceneSaver(dialogs.Object);

            saver.Save(model, outputPath);

            Assert.That(File.Exists(outputPath), Is.True);
            ModelRoot.Validate(outputPath);
            var reloaded = ModelRoot.Load(outputPath);
            Assert.That(reloaded, Is.Not.Null);
            dialogs.Verify(x => x.ShowExceptionWindow(It.IsAny<Exception>()), Times.Never);
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [Test]
    public void HeadlessSaverWritesReloadableGlbAndRemovesGeneratedTexture()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"asset-editor-headless-glb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        var outputPath = Path.Combine(outputDirectory, "model.glb");
        var texturePath = Path.Combine(outputDirectory, "body.png");
        File.WriteAllBytes(texturePath, [1, 2, 3]);

        try
        {
            var model = ModelRoot.CreateModel();
            model.UseScene("default");
            var saver = new HeadlessGltfSceneSaver();

            saver.Save(model, outputPath, [texturePath]);

            Assert.That(File.Exists(outputPath), Is.True);
            ModelRoot.Validate(outputPath);
            Assert.That(ModelRoot.Load(outputPath), Is.Not.Null);
            Assert.That(File.Exists(texturePath), Is.False);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Test]
    public void HeadlessSaverKeepsEmbeddedImagesInsideGlbWithoutSidecars()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"asset-editor-headless-embedded-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        var outputPath = Path.Combine(outputDirectory, "model.glb");
        var pngBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

        try
        {
            var model = ModelRoot.CreateModel();
            model.UseScene("default");
            model.CreateImage("body").Content = new SharpGLTF.Memory.MemoryImage(pngBytes);
            var saver = new HeadlessGltfSceneSaver();

            saver.Save(model, outputPath);

            Assert.That(File.Exists(outputPath), Is.True);
            ModelRoot.Validate(outputPath);

            var reloaded = ModelRoot.Load(outputPath);
            Assert.That(reloaded.LogicalImages, Has.Count.EqualTo(1));
            Assert.That(reloaded.LogicalImages[0].Content.IsValid, Is.True);
            Assert.That(
                Directory.GetFiles(outputDirectory).Select(Path.GetFileName),
                Is.EquivalentTo(new[] { "model.glb" }));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Test]
    public void HeadlessSaverKeepsReferencedGeneratedTextureAsExternalSidecar()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"asset-editor-headless-sidecar-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        var outputPath = Path.Combine(outputDirectory, "model.glb");
        var texturePath = Path.Combine(outputDirectory, "body.png");
        var pngBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        File.WriteAllBytes(texturePath, pngBytes);

        try
        {
            var model = ModelRoot.CreateModel();
            model.UseScene("default");
            var image = model.CreateImage("body");
            image.Content = new SharpGLTF.Memory.MemoryImage(pngBytes);
            image.AlternateWriteFileName = "body.png";
            var saver = new HeadlessGltfSceneSaver();

            saver.Save(model, outputPath, [texturePath]);

            Assert.That(File.Exists(outputPath), Is.True);
            Assert.That(File.Exists(texturePath), Is.True);
            Assert.That(File.ReadAllBytes(texturePath), Is.EqualTo(pngBytes));

            var glbText = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(outputPath));
            Assert.That(glbText, Does.Contain("body.png"));

            ModelRoot.Validate(outputPath);
            var reloaded = ModelRoot.Load(outputPath);
            Assert.That(reloaded.LogicalImages, Has.Count.EqualTo(1));
            Assert.That(reloaded.LogicalImages[0].Content.IsValid, Is.True);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Test]
    public void HeadlessSaverExternalizesUnmatchedLogicalImages()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"asset-editor-headless-extra-sidecar-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        var outputPath = Path.Combine(outputDirectory, "model.glb");
        var generatedTexturePath = Path.Combine(outputDirectory, "body.png");
        var extraTexturePath = Path.Combine(outputDirectory, "extra.png");
        var pngBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        File.WriteAllBytes(generatedTexturePath, pngBytes);

        try
        {
            var model = ModelRoot.CreateModel();
            model.UseScene("default");

            var generatedImage = model.CreateImage("body");
            generatedImage.Content = new SharpGLTF.Memory.MemoryImage(pngBytes);
            generatedImage.AlternateWriteFileName = "body.png";

            var unmatchedImage = model.CreateImage("extra");
            unmatchedImage.Content = new SharpGLTF.Memory.MemoryImage(pngBytes);
            unmatchedImage.AlternateWriteFileName = "extra.png";

            var saver = new HeadlessGltfSceneSaver();
            saver.Save(model, outputPath, [generatedTexturePath]);

            Assert.That(File.Exists(outputPath), Is.True);
            Assert.That(File.ReadAllBytes(generatedTexturePath), Is.EqualTo(pngBytes));
            Assert.That(File.Exists(extraTexturePath), Is.True);
            Assert.That(File.ReadAllBytes(extraTexturePath), Is.EqualTo(pngBytes));

            var glbText = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(outputPath));
            Assert.That(glbText, Does.Contain("body.png"));
            Assert.That(glbText, Does.Contain("extra.png"));

            ModelRoot.Validate(outputPath);
            var reloaded = ModelRoot.Load(outputPath);
            Assert.That(reloaded.LogicalImages, Has.Count.EqualTo(2));
            Assert.That(reloaded.LogicalImages.All(image => image.Content.IsValid), Is.True);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Test]
    public void HeadlessSaverRemovesGeneratedKtx2Texture()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"asset-editor-headless-ktx2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        var outputPath = Path.Combine(outputDirectory, "model.glb");
        var texturePath = Path.Combine(outputDirectory, "body.ktx2");
        File.WriteAllBytes(texturePath, [1, 2, 3]);

        try
        {
            var model = ModelRoot.CreateModel();
            model.UseScene("default");
            var saver = new HeadlessGltfSceneSaver();

            saver.Save(model, outputPath, [texturePath]);

            Assert.That(File.Exists(outputPath), Is.True);
            ModelRoot.Validate(outputPath);
            Assert.That(File.Exists(texturePath), Is.False);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Test]
    public void RetainsGeneratedTexturePngForGltfOutput()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"asset-editor-gltf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        var outputPath = Path.Combine(outputDirectory, "model.gltf");
        var texturePath = Path.Combine(outputDirectory, "body.png");
        File.WriteAllBytes(texturePath, [1, 2, 3]);

        try
        {
            var saver = new GltfSceneSaver(new Mock<IStandardDialogs>().Object);
            var model = ModelRoot.CreateModel();
            model.UseScene("default");
            saver.Save(model, outputPath, [texturePath]);

            Assert.That(File.Exists(outputPath), Is.True);
            Assert.That(File.Exists(texturePath), Is.True);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Test]
    public void RemovesOnlyReferencedTexturePngsAfterSuccessfulGlbSave()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"asset-editor-glb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        var outputPath = Path.Combine(outputDirectory, "model.glb");
        var texturePath = Path.Combine(outputDirectory, "body.png");
        var maskPath = Path.Combine(outputDirectory, "body_mask.png");
        var auxiliaryPath = Path.Combine(outputDirectory, "body_displacement.png");
        File.WriteAllBytes(texturePath, [1, 2, 3]);
        File.WriteAllBytes(maskPath, [4, 5, 6]);
        File.WriteAllBytes(auxiliaryPath, [7, 8, 9]);

        try
        {
            var saver = new GltfSceneSaver(new Mock<IStandardDialogs>().Object);
            var model = ModelRoot.CreateModel();
            model.UseScene("default");
            saver.Save(model, outputPath, [texturePath, texturePath]);

            Assert.That(File.Exists(outputPath), Is.True);
            ModelRoot.Validate(outputPath);
            Assert.That(File.Exists(texturePath), Is.False);
            Assert.That(File.Exists(maskPath), Is.True);
            Assert.That(File.Exists(auxiliaryPath), Is.True);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Test]
    public void RetainsGeneratedTexturePngWhenGlbSaveFails()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"asset-editor-glb-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        var outputPath = Path.Combine(outputDirectory, "missing", "model.glb");
        var texturePath = Path.Combine(outputDirectory, "body.png");
        File.WriteAllBytes(texturePath, [1, 2, 3]);
        var dialogs = new Mock<IStandardDialogs>();

        try
        {
            var saver = new GltfSceneSaver(dialogs.Object);
            var model = ModelRoot.CreateModel();
            model.UseScene("default");
            saver.Save(model, outputPath, [texturePath]);

            Assert.That(File.Exists(texturePath), Is.True);
            dialogs.Verify(x => x.ShowExceptionWindow(It.IsAny<Exception>()), Times.Once);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }
}
