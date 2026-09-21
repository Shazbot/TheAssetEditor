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
        public void CreatePlan_RejectsWrappedUvs()
        {
            var source = new TextureAtlasLayoutSource(3, 1024, 1024, -0.1f, 0.0f, 0.5f, 0.5f);

            var exception = Assert.Throws<InvalidOperationException>(() => TextureAtlasBuilder.CreatePlan([source]));

            Assert.That(exception!.Message, Does.Contain("outside 0..1"));
        }
    }
}
