using System.Drawing;
using System.Drawing.Imaging;
using DrawingImageFormat = System.Drawing.Imaging.ImageFormat;
using Editors.ImportExport.Importing.Importers.PngToDds;
using Editors.ImportExport.TextureAtlas;
using MeshImportExport;
using Pfim;
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
        public void CreatePlan_UsesSmallestFittingRectangularPowerOfTwoAtlas()
        {
            var plan = TextureAtlasBuilder.CreatePlan(
                [
                    new TextureAtlasLayoutSource(0, 8, 8, 0, 0, 1, 1),
                    new TextureAtlasLayoutSource(1, 8, 8, 0, 0, 1, 1)
                ],
                padding: 0);

            Assert.That((long)plan.Width * plan.Height, Is.EqualTo(128));
            Assert.That(plan.Width, Is.Not.EqualTo(plan.Height));
            Assert.That(plan.Width, Is.AnyOf(8, 16));
            Assert.That(plan.Height, Is.AnyOf(8, 16));
        }

        [Test]
        public void CreatePlan_PrefersRectangularAtlasOverNextSquareSize()
        {
            var plan = TextureAtlasBuilder.CreatePlan(
                [
                    new TextureAtlasLayoutSource(0, 16, 8, 0, 0, 1, 1),
                    new TextureAtlasLayoutSource(1, 16, 8, 0, 0, 1, 1),
                    new TextureAtlasLayoutSource(2, 16, 8, 0, 0, 1, 1)
                ],
                padding: 0);

            Assert.That((long)plan.Width * plan.Height, Is.EqualTo(512));
            Assert.That(plan.Width, Is.Not.EqualTo(plan.Height));
            Assert.That(Math.Max(plan.Width, plan.Height), Is.EqualTo(32));
            Assert.That(Math.Min(plan.Width, plan.Height), Is.EqualTo(16));
        }

        [Test]
        public void BuildPng_CanForceSelectedSourceAlphaOpaque()
        {
            using var bitmap = new Bitmap(4, 4, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.Clear(Color.FromArgb(0, 120, 80, 40));

            using var pngStream = new MemoryStream();
            bitmap.Save(pngStream, DrawingImageFormat.Png);

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
            bitmap.Save(pngStream, DrawingImageFormat.Png);

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
            bitmap.Save(pngStream, DrawingImageFormat.Png);

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
        public void BaseColourAtlasRoundTrip_PreservesDecodedMidtoneBrightness()
        {
            using var bitmap = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.Clear(Color.FromArgb(255, 128, 96, 64));

            using var pngStream = new MemoryStream();
            bitmap.Save(pngStream, DrawingImageFormat.Png);

            var sourceDds = PngToDdsImporter.ImportRaw(
                pngStream.ToArray(),
                TextureType.BaseColour,
                GameTypeEnum.Warhammer3,
                "midtone-source.dds");

            var sourceBytes = sourceDds.DataSource.ReadData();
            var plan = TextureAtlasBuilder.CreatePlan(
                [new TextureAtlasLayoutSource(0, 16, 16, 0, 0, 1, 1)],
                padding: 0);

            var atlasPng = TextureAtlasBuilder.BuildPng(
                plan,
                new Dictionary<int, byte[]> { [0] = sourceBytes });

            var atlasDds = PngToDdsImporter.ImportRaw(
                atlasPng,
                TextureType.BaseColour,
                GameTypeEnum.Warhammer3,
                "midtone-atlas.dds");

            using var sourcePngStream = new MemoryStream(TextureHelper.ConvertDdsToPng(sourceBytes));
            using var sourceBitmap = new Bitmap(sourcePngStream);
            using var atlasPngStream = new MemoryStream(TextureHelper.ConvertDdsToPng(atlasDds.DataSource.ReadData()));
            using var atlasBitmap = new Bitmap(atlasPngStream);

            var sourcePixel = sourceBitmap.GetPixel(8, 8);
            var atlasPixel = atlasBitmap.GetPixel(8, 8);

            Assert.That(atlasPixel.R, Is.EqualTo(sourcePixel.R).Within(8));
            Assert.That(atlasPixel.G, Is.EqualTo(sourcePixel.G).Within(8));
            Assert.That(atlasPixel.B, Is.EqualTo(sourcePixel.B).Within(8));
        }

        [Test]
        public void MaterialMapAtlasRoundTrip_PreservesDecodedChannelValues()
        {
            using var bitmap = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.Clear(Color.FromArgb(255, 64, 192, 32));

            using var pngStream = new MemoryStream();
            bitmap.Save(pngStream, DrawingImageFormat.Png);

            var sourceDds = PngToDdsImporter.ImportRaw(
                pngStream.ToArray(),
                TextureType.MaterialMap,
                GameTypeEnum.Warhammer3,
                "material-roundtrip-source.dds");

            var sourceBytes = sourceDds.DataSource.ReadData();
            var plan = TextureAtlasBuilder.CreatePlan(
                [new TextureAtlasLayoutSource(0, 16, 16, 0, 0, 1, 1)],
                padding: 0);

            var atlasPng = TextureAtlasBuilder.BuildPng(
                plan,
                new Dictionary<int, byte[]> { [0] = sourceBytes });

            var atlasDds = PngToDdsImporter.ImportRaw(
                atlasPng,
                TextureType.MaterialMap,
                GameTypeEnum.Warhammer3,
                "material-roundtrip-atlas.dds");

            using var sourcePngStream = new MemoryStream(TextureHelper.ConvertDdsToPng(sourceBytes));
            using var sourceBitmap = new Bitmap(sourcePngStream);
            using var atlasPngStream = new MemoryStream(TextureHelper.ConvertDdsToPng(atlasDds.DataSource.ReadData()));
            using var atlasBitmap = new Bitmap(atlasPngStream);

            var sourcePixel = sourceBitmap.GetPixel(8, 8);
            var atlasPixel = atlasBitmap.GetPixel(8, 8);

            Assert.That(atlasPixel.R, Is.EqualTo(sourcePixel.R).Within(8));
            Assert.That(atlasPixel.G, Is.EqualTo(sourcePixel.G).Within(8));
            Assert.That(atlasPixel.B, Is.EqualTo(sourcePixel.B).Within(8));
        }

        [Test]
        public void ExplicitAtlasMipChain_PreservesAuthoredSourceMipColours()
        {
            var sourceMipPngs = new[]
            {
                CreateSolidPng(16, Color.White),
                CreateSolidPng(8, Color.Red),
                CreateSolidPng(4, Color.Lime),
                CreateSolidPng(2, Color.Blue),
                CreateSolidPng(1, Color.Gray)
            };

            var sourceDds = PngToDdsImporter.ImportRawMipChain(
                sourceMipPngs,
                TextureType.BaseColour,
                GameTypeEnum.Warhammer3,
                "authored-mips-source.dds");

            var sourceBytes = sourceDds.DataSource.ReadData();
            var plan = TextureAtlasBuilder.CreatePlan(
                [new TextureAtlasLayoutSource(0, 16, 16, 0, 0, 1, 1)],
                padding: 8);

            var atlasMipPixels = TextureAtlasBuilder.BuildMipPixels(
                plan,
                new Dictionary<int, byte[]> { [0] = sourceBytes });
            var atlasDds = PngToDdsImporter.ImportRawBgraMipChain(
                atlasMipPixels,
                plan.Width,
                plan.Height,
                TextureType.BaseColour,
                GameTypeEnum.Warhammer3,
                "authored-mips-atlas.dds");

            using var sourceStream = new MemoryStream(sourceBytes);
            using var sourceImage = Pfimage.FromStream(sourceStream);
            using var atlasStream = new MemoryStream(atlasDds.DataSource.ReadData());
            using var atlasImage = Pfimage.FromStream(atlasStream);

            // Mip level 2 was authored as green. Compare the packed atlas sample to the
            // decoded source mip itself so BC1 quantization is accounted for.
            var sourceMip = sourceImage.MipMaps[1];
            var sourcePixel = ReadPfimPixel(
                sourceImage,
                sourceMip.DataOffset,
                sourceMip.Stride,
                sourceMip.Width / 2,
                sourceMip.Height / 2);

            var atlasMip = atlasImage.MipMaps[1];
            var placement = plan.Placements.Single();
            var atlasX = (int)Math.Floor(
                (placement.DestinationX + placement.CropWidth / 2.0) *
                atlasMip.Width / plan.Width);
            var atlasY = (int)Math.Floor(
                (placement.DestinationY + placement.CropHeight / 2.0) *
                atlasMip.Height / plan.Height);
            var atlasPixel = ReadPfimPixel(
                atlasImage,
                atlasMip.DataOffset,
                atlasMip.Stride,
                atlasX,
                atlasY);

            Assert.That(atlasPixel.R, Is.EqualTo(sourcePixel.R).Within(8));
            Assert.That(atlasPixel.G, Is.EqualTo(sourcePixel.G).Within(8));
            Assert.That(atlasPixel.B, Is.EqualTo(sourcePixel.B).Within(8));
            Assert.That(atlasPixel.G, Is.GreaterThan(atlasPixel.R));
            Assert.That(atlasPixel.G, Is.GreaterThan(atlasPixel.B));
        }

        [Test]
        public void BuildMipPixels_DecodesSharedDdsBytesOnlyOnce()
        {
            using var bitmap = new Bitmap(4, 4, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.Clear(Color.CornflowerBlue);

            using var pngStream = new MemoryStream();
            bitmap.Save(pngStream, DrawingImageFormat.Png);

            var ddsPack = PngToDdsImporter.ImportRaw(
                pngStream.ToArray(),
                TextureType.BaseColour,
                GameTypeEnum.Warhammer3,
                "shared-source.dds");
            var sharedBytes = ddsPack.DataSource.ReadData();

            var plan = TextureAtlasBuilder.CreatePlan(
                [
                    new TextureAtlasLayoutSource(0, 4, 4, 0, 0, 1, 1),
                    new TextureAtlasLayoutSource(1, 4, 4, 0, 0, 1, 1)
                ],
                padding: 0);
            var statistics = new TextureAtlasBuildStatistics();

            _ = TextureAtlasBuilder.BuildMipPixels(
                plan,
                new Dictionary<int, byte[]>
                {
                    [0] = sharedBytes,
                    [1] = sharedBytes
                },
                statistics: statistics);

            Assert.That(statistics.DecodedSourceCount, Is.EqualTo(2));
            Assert.That(statistics.UniqueDecodedDdsCount, Is.EqualTo(1));
        }

        [Test]
        public void BuildPng_CanOmitMissingSecondaryAtlasSources()
        {
            using var bitmap = new Bitmap(4, 4, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.Clear(Color.White);

            using var pngStream = new MemoryStream();
            bitmap.Save(pngStream, DrawingImageFormat.Png);

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

        [Test]
        public void BuildMipPngs_CanPaintConstantSourceAtVirtualDimensions()
        {
            var plan = TextureAtlasBuilder.CreatePlan(
                [new TextureAtlasLayoutSource(7, 32, 16, 0, 0, 1, 1)],
                padding: 2);

            var constant = new TextureAtlasConstantColor(
                B: 30,
                G: 20,
                R: 10,
                A: 255);

            var mipPngs = TextureAtlasBuilder.BuildMipPngs(
                plan,
                new Dictionary<int, byte[]>(),
                constantSources: new Dictionary<int, TextureAtlasConstantColor>
                {
                    [7] = constant
                });

            using var stream = new MemoryStream(mipPngs[0]);
            using var bitmap = new Bitmap(stream);
            var placement = plan.Placements.Single();
            var pixel = bitmap.GetPixel(
                placement.DestinationX + placement.CropWidth / 2,
                placement.DestinationY + placement.CropHeight / 2);

            Assert.That(pixel.R, Is.EqualTo(10));
            Assert.That(pixel.G, Is.EqualTo(20));
            Assert.That(pixel.B, Is.EqualTo(30));
            Assert.That(pixel.A, Is.EqualTo(255));
        }

        [Test]
        public void CalculateOutputDimensions_SizesChannelAgainstPrimaryPlan()
        {
            var plan = TextureAtlasBuilder.CreatePlan(
                [
                    new TextureAtlasLayoutSource(0, 8, 8, 0, 0, 1, 1),
                    new TextureAtlasLayoutSource(1, 8, 8, 0, 0, 1, 1)
                ],
                padding: 0);

            var baseColour = TextureAtlasBuilder.CalculateOutputDimensions(
                plan,
                new Dictionary<int, (int Width, int Height)>
                {
                    [0] = (8, 8),
                    [1] = (8, 8)
                });
            var highResolutionMask = TextureAtlasBuilder.CalculateOutputDimensions(
                plan,
                new Dictionary<int, (int Width, int Height)>
                {
                    [0] = (32, 32),
                    [1] = (8, 8)
                });
            var lowResolutionNormal = TextureAtlasBuilder.CalculateOutputDimensions(
                plan,
                new Dictionary<int, (int Width, int Height)>
                {
                    [0] = (4, 4),
                    [1] = (4, 4)
                });

            Assert.That(baseColour, Is.EqualTo((plan.Width, plan.Height)));
            Assert.That(
                highResolutionMask,
                Is.EqualTo((plan.Width * 4, plan.Height * 4)));
            Assert.That(
                lowResolutionNormal,
                Is.EqualTo((plan.Width / 2, plan.Height / 2)));
        }

        [Test]
        public void BuildMipPngs_CanEmitSmallerPhysicalChannelAtlas()
        {
            var sourceDds = PngToDdsImporter.ImportRaw(
                CreateSolidPng(4, Color.Orange),
                TextureType.Normal,
                GameTypeEnum.Warhammer3,
                "smaller-physical-channel.dds");
            var sourceBytes = sourceDds.DataSource.ReadData();

            // The UV plan is expressed at the 8x8 primary-texture density, while this
            // channel is natively 4x4 and should therefore emit a 4x4 physical atlas.
            var plan = TextureAtlasBuilder.CreatePlan(
                [new TextureAtlasLayoutSource(0, 8, 8, 0, 0, 1, 1)],
                padding: 0);
            var outputDimensions = TextureAtlasBuilder.CalculateOutputDimensions(
                plan,
                new Dictionary<int, (int Width, int Height)> { [0] = (4, 4) });

            var mipPngs = TextureAtlasBuilder.BuildMipPngs(
                plan,
                new Dictionary<int, byte[]> { [0] = sourceBytes },
                outputWidth: outputDimensions.Width,
                outputHeight: outputDimensions.Height);

            using var atlasStream = new MemoryStream(mipPngs[0]);
            using var atlasBitmap = new Bitmap(atlasStream);

            Assert.That(atlasBitmap.Width, Is.EqualTo(4));
            Assert.That(atlasBitmap.Height, Is.EqualTo(4));
        }

        [Test]
        public void CalculateOutputDimensions_RejectsChannelAboveAtlasLimit()
        {
            var plan = TextureAtlasBuilder.CreatePlan(
                [new TextureAtlasLayoutSource(0, 4096, 4096, 0, 0, 1, 1)],
                padding: 0);

            Assert.That(
                () => TextureAtlasBuilder.CalculateOutputDimensions(
                    plan,
                    new Dictionary<int, (int Width, int Height)>
                    {
                        [0] = (16384, 16384)
                    }),
                Throws.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void BuildPng_ResamplesMixedResolutionChannelIntoSharedLayout()
        {
            using var sourceBitmap = new Bitmap(4, 4, PixelFormat.Format32bppArgb);
            for (var y = 0; y < sourceBitmap.Height; y++)
            {
                for (var x = 0; x < sourceBitmap.Width; x++)
                {
                    sourceBitmap.SetPixel(
                        x,
                        y,
                        Color.FromArgb(255, 20 + x * 40, 30 + y * 40, 10 + (x + y) * 20));
                }
            }

            using var pngStream = new MemoryStream();
            sourceBitmap.Save(pngStream, DrawingImageFormat.Png);
            var sourceDds = PngToDdsImporter.ImportRaw(
                pngStream.ToArray(),
                TextureType.MaterialMap,
                GameTypeEnum.Warhammer3,
                "mixed-resolution-source.dds");
            var sourceBytes = sourceDds.DataSource.ReadData();

            var plan = TextureAtlasBuilder.CreatePlan(
                [new TextureAtlasLayoutSource(0, 8, 8, 0, 0, 1, 1)],
                padding: 0);
            var atlasPng = TextureAtlasBuilder.BuildPng(
                plan,
                new Dictionary<int, byte[]> { [0] = sourceBytes });

            using var sourceStream = new MemoryStream(sourceBytes);
            using var sourceImage = Pfimage.FromStream(sourceStream);
            using var atlasStream = new MemoryStream(atlasPng);
            using var atlasBitmap = new Bitmap(atlasStream);
            var placement = plan.Placements.Single();

            for (var y = 0; y < sourceImage.Height; y++)
            {
                for (var x = 0; x < sourceImage.Width; x++)
                {
                    var expected = ReadPfimPixel(sourceImage, 0, sourceImage.Stride, x, y);
                    var actual = atlasBitmap.GetPixel(
                        placement.DestinationX + x * 2,
                        placement.DestinationY + y * 2);
                    Assert.That(actual.ToArgb(), Is.EqualTo(expected.ToArgb()));
                }
            }
        }

        [Test]
        public void BuildMipPngs_DelaysSourceMipsForUpscaledMixedResolutionChannel()
        {
            var sourceDds = PngToDdsImporter.ImportRawMipChain(
                [
                    CreateSolidPng(4, Color.Red),
                    CreateSolidPng(2, Color.Blue),
                    CreateSolidPng(1, Color.Lime)
                ],
                TextureType.MaterialMap,
                GameTypeEnum.Warhammer3,
                "mixed-resolution-mips-source.dds");
            var sourceBytes = sourceDds.DataSource.ReadData();

            var plan = TextureAtlasBuilder.CreatePlan(
                [new TextureAtlasLayoutSource(0, 8, 8, 0, 0, 1, 1)],
                padding: 0);
            var mipPngs = TextureAtlasBuilder.BuildMipPngs(
                plan,
                new Dictionary<int, byte[]> { [0] = sourceBytes });

            using var sourceStream = new MemoryStream(sourceBytes);
            using var sourceImage = Pfimage.FromStream(sourceStream);
            using var atlasMipStream = new MemoryStream(mipPngs[1]);
            using var atlasMip = new Bitmap(atlasMipStream);

            // The atlas rectangle is twice the source resolution. At atlas mip 1 the
            // rectangle has only just reached the source's native 4x4 density, so it
            // must still sample the authored base level (red), not the 2x2 blue mip.
            var expected = ReadPfimPixel(
                sourceImage,
                0,
                sourceImage.Stride,
                sourceImage.Width / 2,
                sourceImage.Height / 2);
            var actual = atlasMip.GetPixel(atlasMip.Width / 2, atlasMip.Height / 2);

            Assert.That(actual.R, Is.EqualTo(expected.R).Within(8));
            Assert.That(actual.G, Is.EqualTo(expected.G).Within(8));
            Assert.That(actual.B, Is.EqualTo(expected.B).Within(8));
            Assert.That(actual.R, Is.GreaterThan(actual.B));
        }

        [Test]
        public void ComputeWrappedRegionContentHash_MatchesRepeatedRegionsAtDifferentOffsets()
        {
            using var bitmap = new Bitmap(8, 4, PixelFormat.Format32bppArgb);
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var localX = x % 4;
                    bitmap.SetPixel(
                        x,
                        y,
                        (localX + y) % 2 == 0 ? Color.Black : Color.White);
                }
            }

            using var pngStream = new MemoryStream();
            bitmap.Save(pngStream, DrawingImageFormat.Png);
            var dds = PngToDdsImporter.ImportRaw(
                pngStream.ToArray(),
                TextureType.BaseColour,
                GameTypeEnum.Warhammer3,
                "repeated-region.dds");
            var bytes = dds.DataSource.ReadData();

            var leftHash = TextureAtlasBuilder.ComputeWrappedRegionContentHash(
                bytes,
                8,
                4,
                0,
                0,
                4,
                4);
            var rightHash = TextureAtlasBuilder.ComputeWrappedRegionContentHash(
                bytes,
                8,
                4,
                4,
                0,
                4,
                4);

            Assert.That(rightHash, Is.EqualTo(leftHash));
        }

        [Test]
        public void ComputeWrappedRegionContentHash_DistinguishesDifferentRegions()
        {
            using var bitmap = new Bitmap(8, 4, PixelFormat.Format32bppArgb);
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                    bitmap.SetPixel(x, y, x < 4 ? Color.Black : Color.White);
            }

            using var pngStream = new MemoryStream();
            bitmap.Save(pngStream, DrawingImageFormat.Png);
            var dds = PngToDdsImporter.ImportRaw(
                pngStream.ToArray(),
                TextureType.BaseColour,
                GameTypeEnum.Warhammer3,
                "different-regions.dds");
            var bytes = dds.DataSource.ReadData();

            var leftHash = TextureAtlasBuilder.ComputeWrappedRegionContentHash(
                bytes,
                8,
                4,
                0,
                0,
                4,
                4);
            var rightHash = TextureAtlasBuilder.ComputeWrappedRegionContentHash(
                bytes,
                8,
                4,
                4,
                0,
                4,
                4);

            Assert.That(rightHash, Is.Not.EqualTo(leftHash));
        }

        [Test]
        public void ComputeWrappedRegionContentHash_IncludesAuthoredMipContent()
        {
            var first = PngToDdsImporter.ImportRawMipChain(
                [
                    CreateSolidPng(8, Color.White),
                    CreateSolidPng(4, Color.Red),
                    CreateSolidPng(2, Color.Gray),
                    CreateSolidPng(1, Color.Black)
                ],
                TextureType.BaseColour,
                GameTypeEnum.Warhammer3,
                "hash-mips-a.dds");
            var second = PngToDdsImporter.ImportRawMipChain(
                [
                    CreateSolidPng(8, Color.White),
                    CreateSolidPng(4, Color.Blue),
                    CreateSolidPng(2, Color.Gray),
                    CreateSolidPng(1, Color.Black)
                ],
                TextureType.BaseColour,
                GameTypeEnum.Warhammer3,
                "hash-mips-b.dds");

            var firstHash = TextureAtlasBuilder.ComputeWrappedRegionContentHash(
                first.DataSource.ReadData(),
                8,
                8,
                0,
                0,
                8,
                8);
            var secondHash = TextureAtlasBuilder.ComputeWrappedRegionContentHash(
                second.DataSource.ReadData(),
                8,
                8,
                0,
                0,
                8,
                8);

            Assert.That(secondHash, Is.Not.EqualTo(firstHash));
        }

        [Test]
        public void TryGetUniformColor_RejectsNonUniformDds()
        {
            using var solid = new Bitmap(8, 8, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(solid))
                graphics.Clear(Color.Black);

            using var solidStream = new MemoryStream();
            solid.Save(solidStream, DrawingImageFormat.Png);
            var solidDds = PngToDdsImporter.ImportRaw(
                solidStream.ToArray(),
                TextureType.Mask,
                GameTypeEnum.Warhammer3,
                "solid.dds");

            Assert.That(
                TextureAtlasBuilder.TryGetUniformColor(
                    solidDds.DataSource.ReadData(),
                    out var constant),
                Is.True);
            Assert.That(constant.R, Is.EqualTo(0));
            Assert.That(constant.G, Is.EqualTo(0));
            Assert.That(constant.B, Is.EqualTo(0));

            using var varied = new Bitmap(8, 8, PixelFormat.Format32bppArgb);
            for (var y = 0; y < varied.Height; y++)
            {
                for (var x = 0; x < varied.Width; x++)
                    varied.SetPixel(x, y, x < 4 ? Color.Black : Color.White);
            }

            using var variedStream = new MemoryStream();
            varied.Save(variedStream, DrawingImageFormat.Png);
            var variedDds = PngToDdsImporter.ImportRaw(
                variedStream.ToArray(),
                TextureType.Mask,
                GameTypeEnum.Warhammer3,
                "varied.dds");

            Assert.That(
                TextureAtlasBuilder.TryGetUniformColor(
                    variedDds.DataSource.ReadData(),
                    out _),
                Is.False);
        }

        private static byte[] CreateSolidPng(int size, Color color)
        {
            using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.Clear(color);

            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }

        private static Color ReadPfimPixel(
            IImage image,
            int dataOffset,
            int stride,
            int x,
            int y)
        {
            var offset = dataOffset + y * stride + x * 4;
            return Color.FromArgb(
                image.Data[offset + 3],
                image.Data[offset + 2],
                image.Data[offset + 1],
                image.Data[offset]);
        }

    }
}
