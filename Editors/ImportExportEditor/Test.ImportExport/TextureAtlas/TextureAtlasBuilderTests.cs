using System.Drawing;
using System.Drawing.Imaging;
using Editors.ImportExport.Importing.Importers.PngToDds;
using Editors.ImportExport.TextureAtlas;
using Shared.Core.Settings;
using Shared.GameFormats.RigidModel.Types;

namespace Test.ImportExport.TextureAtlas
{
    public class TextureAtlasBuilderTests
    {
        [Test]
        public void CreatePlan_CropsToUsedUvBoundsAndKeepsPositiveAxes()
        {
            var sources = new[]
            {
                new TextureAtlasLayoutSource(0, 1024, 1024, 0.25f, 0.25f, 0.5f, 0.5f),
                new TextureAtlasLayoutSource(1, 1024, 1024, 0.5f, 0.0f, 0.75f, 0.25f)
            };

            var plan = TextureAtlasBuilder.CreatePlan(sources);
            var first = plan.Placements.Single(x => x.Id == 0);

            Assert.That(first.CropWidth, Is.EqualTo(256));
            Assert.That(first.CropHeight, Is.EqualTo(256));

            var min = first.TransformUv(0.25f, 0.25f, plan.Width, plan.Height);
            var maxU = first.TransformUv(0.5f, 0.25f, plan.Width, plan.Height);
            var maxV = first.TransformUv(0.25f, 0.5f, plan.Width, plan.Height);

            Assert.That(maxU.U, Is.GreaterThan(min.U));
            Assert.That(maxU.V, Is.EqualTo(min.V).Within(0.000001f));
            Assert.That(maxV.U, Is.EqualTo(min.U).Within(0.000001f));
            Assert.That(maxV.V, Is.GreaterThan(min.V));
        }

        [Test]
        public void CreatePlan_AddsPaddingAroundEveryCrop()
        {
            var source = new TextureAtlasLayoutSource(7, 512, 512, 0.0f, 0.0f, 0.5f, 0.5f);

            var plan = TextureAtlasBuilder.CreatePlan([source], padding: 8);
            var placement = plan.Placements.Single();

            Assert.That(placement.DestinationX, Is.GreaterThanOrEqualTo(8));
            Assert.That(placement.DestinationY, Is.GreaterThanOrEqualTo(8));
            Assert.That(placement.Padding, Is.EqualTo(8));
        }

        [Test]
        public void BuildPng_CanForceSelectedSourceAlphaOpaque()
        {
            using var bitmap = new Bitmap(4, 4, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.Clear(Color.FromArgb(0, 120, 80, 40));

            using var pngStream = new MemoryStream();
            bitmap.Save(pngStream, ImageFormat.Png);

            var ddsPack = PngToDdsImporter.ImportRaw(
                pngStream.ToArray(),
                TextureType.BaseColour,
                GameTypeEnum.Warhammer3,
                "source.dds");

            var plan = TextureAtlasBuilder.CreatePlan(
                [new TextureAtlasLayoutSource(0, 4, 4, 0, 0, 1, 1)],
                padding: 0);

            var atlasPng = TextureAtlasBuilder.BuildPng(
                plan,
                new Dictionary<int, byte[]> { [0] = ddsPack.DataSource.ReadData() },
                new HashSet<int> { 0 });

            using var atlasStream = new MemoryStream(atlasPng);
            using var atlasBitmap = new Bitmap(atlasStream);

            Assert.That(atlasBitmap.GetPixel(0, 0).A, Is.EqualTo(255));
            Assert.That(atlasBitmap.GetPixel(3, 3).A, Is.EqualTo(255));
        }

        [Test]
        public void CreatePlan_PreservesVirtualCropForWrappedUvs()
        {
            var source = new TextureAtlasLayoutSource(3, 8, 4, -0.25f, -0.5f, 1.25f, 0.5f);

            var plan = TextureAtlasBuilder.CreatePlan([source], padding: 0);
            var placement = plan.Placements.Single();

            Assert.That(placement.CropX, Is.EqualTo(-2));
            Assert.That(placement.CropY, Is.EqualTo(-2));
            Assert.That(placement.CropWidth, Is.EqualTo(12));
            Assert.That(placement.CropHeight, Is.EqualTo(4));

            var min = placement.TransformUv(-0.25f, -0.5f, plan.Width, plan.Height);
            var max = placement.TransformUv(1.25f, 0.5f, plan.Width, plan.Height);

            Assert.That(min.U, Is.EqualTo((float)placement.DestinationX / plan.Width).Within(0.000001f));
            Assert.That(min.V, Is.EqualTo((float)placement.DestinationY / plan.Height).Within(0.000001f));
            Assert.That(max.U, Is.EqualTo((float)(placement.DestinationX + placement.CropWidth) / plan.Width).Within(0.000001f));
            Assert.That(max.V, Is.EqualTo((float)(placement.DestinationY + placement.CropHeight) / plan.Height).Within(0.000001f));
        }

        [Test]
        public void BuildPng_RepeatsSourcePixelsForWrappedUvs()
        {
            // Use two separate 4x4 BC1 blocks so compression preserves a clear difference
            // between the two halves of the source texture.
            using var bitmap = new Bitmap(8, 4, PixelFormat.Format32bppArgb);
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                    bitmap.SetPixel(x, y, x < 4 ? Color.Black : Color.White);
            }

            using var pngStream = new MemoryStream();
            bitmap.Save(pngStream, ImageFormat.Png);

            var ddsPack = PngToDdsImporter.ImportRaw(
                pngStream.ToArray(),
                TextureType.BaseColour,
                GameTypeEnum.Warhammer3,
                "wrapped-source.dds");

            var plan = TextureAtlasBuilder.CreatePlan(
                [new TextureAtlasLayoutSource(0, 8, 4, -0.5f, 0, 1.5f, 1)],
                padding: 0);

            var atlasPng = TextureAtlasBuilder.BuildPng(
                plan,
                new Dictionary<int, byte[]> { [0] = ddsPack.DataSource.ReadData() });

            using var atlasStream = new MemoryStream(atlasPng);
            using var atlasBitmap = new Bitmap(atlasStream);
            var placement = plan.Placements.Single();
            var sampleY = placement.DestinationY;

            Assert.That(atlasBitmap.GetPixel(placement.DestinationX, sampleY), Is.EqualTo(atlasBitmap.GetPixel(placement.DestinationX + 8, sampleY)));
            Assert.That(atlasBitmap.GetPixel(placement.DestinationX + 4, sampleY), Is.EqualTo(atlasBitmap.GetPixel(placement.DestinationX + 12, sampleY)));
            Assert.That(atlasBitmap.GetPixel(placement.DestinationX, sampleY), Is.Not.EqualTo(atlasBitmap.GetPixel(placement.DestinationX + 4, sampleY)));
        }

        [Test]
        public void BuildPng_PreservesBaseColourChannelOrder()
        {
            using var bitmap = new Bitmap(4, 4, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.Clear(Color.FromArgb(255, 240, 40, 10));

            using var pngStream = new MemoryStream();
            bitmap.Save(pngStream, ImageFormat.Png);

            var ddsPack = PngToDdsImporter.ImportRaw(
                pngStream.ToArray(),
                TextureType.BaseColour,
                GameTypeEnum.Warhammer3,
                "channel-order-source.dds");

            var plan = TextureAtlasBuilder.CreatePlan(
                [new TextureAtlasLayoutSource(0, 4, 4, 0, 0, 1, 1)],
                padding: 0);

            var atlasPng = TextureAtlasBuilder.BuildPng(
                plan,
                new Dictionary<int, byte[]> { [0] = ddsPack.DataSource.ReadData() });

            using var atlasStream = new MemoryStream(atlasPng);
            using var atlasBitmap = new Bitmap(atlasStream);
            var placement = plan.Placements.Single();
            var pixel = atlasBitmap.GetPixel(placement.DestinationX, placement.DestinationY);

            Assert.That(pixel.R, Is.GreaterThan(pixel.B));
            Assert.That(pixel.R, Is.GreaterThan(pixel.G));
        }

        [Test]
        public void BuildPng_CanOmitMissingSecondaryAtlasSources()
        {
            using var bitmap = new Bitmap(4, 4, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.Clear(Color.White);

            using var pngStream = new MemoryStream();
            bitmap.Save(pngStream, ImageFormat.Png);

            var ddsPack = PngToDdsImporter.ImportRaw(
                pngStream.ToArray(),
                TextureType.MaterialMap,
                GameTypeEnum.Warhammer3,
                "material-source.dds");

            var plan = TextureAtlasBuilder.CreatePlan(
                [
                    new TextureAtlasLayoutSource(0, 4, 4, 0, 0, 1, 1),
                    new TextureAtlasLayoutSource(1, 4, 4, 0, 0, 1, 1)
                ],
                padding: 0);

            var atlasPng = TextureAtlasBuilder.BuildPng(
                plan,
                new Dictionary<int, byte[]> { [0] = ddsPack.DataSource.ReadData() },
                forceOpaqueAlphaSourceIds: null,
                omittedSourceIds: new HashSet<int> { 1 });

            using var atlasStream = new MemoryStream(atlasPng);
            using var atlasBitmap = new Bitmap(atlasStream);
            var present = plan.Placements.Single(x => x.Id == 0);
            var omitted = plan.Placements.Single(x => x.Id == 1);

            Assert.That(atlasBitmap.GetPixel(present.DestinationX, present.DestinationY).A, Is.GreaterThan(0));
            Assert.That(atlasBitmap.GetPixel(omitted.DestinationX, omitted.DestinationY).A, Is.EqualTo(0));
        }

    }
}
