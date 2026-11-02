using System.Reflection;
using Editors.PackOptimizer.Core;
using Shared.GameFormats.RigidModel.Types;

namespace Test.PackOptimizer.Core
{
    // Phase 0b (report only): far-LOD, reduced-mip atlas cost inside generated payloads.
    public class PackTextureAtlasFarLodAtlasCostTests
    {
        private const string Base = "t_xml_base_colour";
        private const long Mib = 1024 * 1024;
        private static readonly int[] Lods = [0, 1, 2, 3];

        private static PackTextureAtlasBatchService.SharedAtlasCostResult Run(
            int[] sizes,
            int mipShift,
            double?[]? crops = null,
            bool generatedOnly = true,
            Dictionary<string, string>? blocks = null)
        {
            var materials = new Dictionary<string, PackTextureAtlasBatchService.SharedAtlasMaterial>();
            var textures = new Dictionary<string, PackTextureAtlasBatchService.SharedAtlasTexture>();
            var referrers = new Dictionary<string, List<(string Material, string Slot)>>(
                StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < sizes.Length; index++)
            {
                var material = $"m{index}";
                var texture = $"tex\\{material}.dds";
                materials[material] = new PackTextureAtlasBatchService.SharedAtlasMaterial(
                    material,
                    null,
                    [new PackTextureAtlasBatchService.SharedAtlasChannelTexture(
                        Base, TextureType.BaseColour, texture)],
                    []);
                textures[texture] = new PackTextureAtlasBatchService.SharedAtlasTexture(
                    Mib, sizes[index], sizes[index], crops?[index]);
                referrers[texture] = [(material, Base)];
            }

            var group = sizes.Select((_, index) => $"m{index}").ToArray();
            return PackTextureAtlasBatchService.ComputeCrossRigidSharedAtlasCost(
                new PackTextureAtlasBatchService.SharedAtlasCostInput(
                    [new PackTextureAtlasBatchService.SharedAtlasPayload("p", 10, Mib, [group])],
                    material => materials[material],
                    texture => textures.GetValueOrDefault(texture),
                    referrers,
                    texture => blocks?.GetValueOrDefault(texture),
                    _ => [],
                    null,
                    mipShift,
                    generatedOnly));
        }

        private static long MipBytes(int width, int height, int bytesPerBlock)
            => (long)typeof(PackTextureAtlasBatchService)
                .GetMethod("CalculateBcnMipChainBytes", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [width, height, bytesPerBlock])!;

        [Test]
        public void LodSplit_SumsToTheTotalAndSeparatesTheAtlasDependentPart()
        {
            double[] t1 = [10, 8, 6, 4];
            double[] t0 = [3, 3, 2, 4];
            var line = PackTextureAtlasBatchService.BuildCrossRigidLodSplitLine(Lods, t1, t0, 100);

            Assert.That(line, Does.Contain("accepted T1 saving 28"));
            Assert.That(line, Does.Contain("L0 10 / L1 8 / L2 6 / L3 4"));
            // above T0: 7 + 5 + 4 + 0 = 16
            Assert.That(line, Does.Contain("atlas-dependent) 16: L0 7 / L1 5 / L2 4 / L3 0"));
            var sums = PackTextureAtlasBatchService.SumCrossRigidSavingByLod(4, [t1, t1]);
            Assert.That(sums.Sum(), Is.EqualTo(2 * t1.Sum()));
        }

        [Test]
        public void FarSaving_ExcludesNearLodsAndTheT0Part()
        {
            double[] t1 = [10, 8, 6, 4];
            double[] t0 = [3, 3, 2, 4];
            Assert.That(
                PackTextureAtlasBatchService.GetCrossRigidFarAtlasSaving(Lods, t1, t0),
                Is.EqualTo(4).Within(1e-12));
            // Near-only payload has no far saving.
            Assert.That(
                PackTextureAtlasBatchService.GetCrossRigidFarAtlasSaving(
                    Lods, [10, 8, 2, 2], [3, 3, 2, 2]),
                Is.EqualTo(0));
        }

        [Test]
        public void FarGroups_AreTakenFromLodsTwoAndThreeOnly()
        {
            string[][][] byLod =
            [
                [["a", "b"]],
                [["c", "d"]],
                [["e", "f"]],
                [["e", "f"], ["g", "h"]]
            ];
            var groups = PackTextureAtlasBatchService.GetCrossRigidFarGroups(Lods, byLod);
            Assert.That(groups.Select(group => string.Join("+", group)),
                Is.EqualTo(new[] { "e+f", "g+h" }));
        }

        [Test]
        public void FarRows_AreTheRowsFarScenarioLodsRead()
        {
            Assert.That(PackTextureAtlasBatchService.GetCrossRigidFarLodSourceRows(Lods, 4),
                Is.EqualTo(new[] { 2, 3 }));
            Assert.That(PackTextureAtlasBatchService.GetCrossRigidFarLodSourceRows(Lods, 2),
                Is.EqualTo(new[] { 1 }));
            Assert.That(PackTextureAtlasBatchService.GetCrossRigidFarLodSourceRows(Lods, 0),
                Is.Empty);
        }

        [Test]
        public void MipReduction_DividesSizesAndBytesFollowThePlannedPage()
        {
            var m0 = Run([1024, 1024], 0);
            var m1 = Run([1024, 1024], 1);
            var m2 = Run([1024, 1024], 2);

            foreach (var (result, size) in new[] { (m0, 1024), (m1, 512), (m2, 256) })
            {
                var page = PackTextureAtlasBatchService.TryPlanCrossRigidSharedAtlasPage(
                    [(0, size, size, size, size), (1, size, size, size, size)]);
                Assert.That(page.Fits, Is.True);
                Assert.That(result.LargestPageWidth, Is.EqualTo(page.Width));
                Assert.That(result.AtlasBytes, Is.EqualTo(MipBytes(page.Width, page.Height, 8)));
                Assert.That(result.NetBytes, Is.EqualTo(result.AtlasBytes));
                Assert.That(result.RetiredBytes, Is.EqualTo(0));
            }

            Assert.That(m1.AtlasBytes, Is.LessThan(m0.AtlasBytes / 3));
            Assert.That(m2.AtlasBytes, Is.LessThan(m1.AtlasBytes / 3));
        }

        [Test]
        public void MipReduction_CropsBelowOneBlockAreCountedAndPagesKeepAFloor()
        {
            // 8 px source: m=2 gives a 2x2 crop.
            var result = Run([8, 8], 2);
            Assert.That(result.SubBlockCropCount, Is.EqualTo(2));
            Assert.That(result.AtlasBytes, Is.GreaterThan(0));
            Assert.That(Run([8, 8], 0).SubBlockCropCount, Is.EqualTo(0));
            Assert.That(PackTextureAtlasBatchService.ShiftCrossRigidSharedAtlasSize(1, 2), Is.EqualTo(1));
            Assert.That(PackTextureAtlasBatchService.ShiftCrossRigidSharedAtlasSize(1023, 1), Is.EqualTo(512));
        }

        [Test]
        public void GeneratedOnly_RetiresNothingAndKeepsNoSourceCounts()
        {
            var generated = Run([512, 512], 1);
            var rewriting = Run([512, 512], 1, generatedOnly: false);

            Assert.That(generated.RetiredBytes, Is.EqualTo(0));
            Assert.That(generated.RetiredTextureCount, Is.EqualTo(0));
            Assert.That(generated.KeptVanillaTextureCount, Is.EqualTo(0));
            Assert.That(generated.LostRepackBytes, Is.Null);
            Assert.That(rewriting.RetiredBytes, Is.EqualTo(2 * Mib));
            Assert.That(generated.NetBytes, Is.EqualTo(rewriting.AtlasBytes));
        }

        [Test]
        public void MeshLevelBlockers_DoNotApplyInTheFarModel()
        {
            Assert.That(PackTextureAtlasBatchService.ResolveCrossRigidSharedAtlasMeshLevelReason(
                false, false, "mesh part shared by several materials"), Is.Null);
            Assert.That(PackTextureAtlasBatchService.ResolveCrossRigidSharedAtlasMeshLevelReason(
                false, true, "rigid used outside the source pack"), Is.Null);
            Assert.That(PackTextureAtlasBatchService.ResolveCrossRigidSharedAtlasMeshLevelReason(
                true, false, null), Is.EqualTo("no source WSModel user"));
            Assert.That(PackTextureAtlasBatchService.ResolveCrossRigidSharedAtlasMeshLevelReason(
                true, true, "mesh part shared by several materials"),
                Is.EqualTo("mesh part shared by several materials"));
            Assert.That(PackTextureAtlasBatchService.ResolveCrossRigidSharedAtlasMeshLevelReason(
                true, true, null), Is.Null);
        }

        [Test]
        public void Greedy_UsesTheFarBytesNotTheFullRigidBytes()
        {
            var greedy = typeof(PackTextureAtlasBatchService).GetMethod(
                "SelectCrossRigidIdealMergePayloadKeysGreedy",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            // Full rigids would not fit a 2 MiB cap, the far rows do.
            (string, double, long)[] far = [("a", 10, Mib), ("b", 10, Mib)];
            (string, double, long)[] full = [("a", 10, 40 * Mib), ("b", 10, 40 * Mib)];
            var withFar = (List<(string Key, double Saving, long Bytes)>)greedy.Invoke(
                null, [far, 1e12, (long?)2 * Mib])!;
            var withFull = (List<(string Key, double Saving, long Bytes)>)greedy.Invoke(
                null, [full, 1e12, (long?)2 * Mib])!;

            Assert.That(withFar, Has.Count.EqualTo(2));
            Assert.That(withFar.Sum(item => item.Bytes), Is.EqualTo(2 * Mib));
            Assert.That(withFull, Is.Empty);
        }

        [Test]
        public void ChannelPage_IsClampedToTheMaximumTextureSizeAtTheChosenMip()
        {
            PackTextureAtlasBatchService.SharedAtlasChannelTexture Tex(
                string slot, TextureType type, string path) => new(slot, type, path);
            var materials = new Dictionary<string, PackTextureAtlasBatchService.SharedAtlasMaterial>();
            var textures = new Dictionary<string, PackTextureAtlasBatchService.SharedAtlasTexture>();
            foreach (var name in new[] { "m0", "m1" })
            {
                materials[name] = new PackTextureAtlasBatchService.SharedAtlasMaterial(
                    name, null,
                    [Tex(Base, TextureType.BaseColour, $"b{name}"),
                     Tex("t_xml_normal", TextureType.Normal, $"n{name}")],
                    []);
                // A 16 px base colour next to a 4096 px normal map: ratio 256.
                textures[$"b{name}"] = new(1024, 16, 16, null);
                textures[$"n{name}"] = new(Mib, 4096, 4096, null);
            }

            foreach (var mip in new[] { 0, 2 })
            {
                var result = PackTextureAtlasBatchService.ComputeCrossRigidSharedAtlasCost(
                    new PackTextureAtlasBatchService.SharedAtlasCostInput(
                        [new PackTextureAtlasBatchService.SharedAtlasPayload("p", 1, Mib, [["m0", "m1"]])],
                        material => materials[material],
                        texture => textures.GetValueOrDefault(texture),
                        new Dictionary<string, List<(string Material, string Slot)>>(),
                        _ => null,
                        _ => [],
                        null,
                        mip,
                        true));
                var limit = 4096 >> mip;
                // Two channels, each at most limit x limit BC1/BC5 pages.
                var ceiling = MipBytes(limit, limit, 8) + MipBytes(limit, limit, 16);
                Assert.That(result.AtlasBytes, Is.GreaterThan(0).And.LessThanOrEqualTo(ceiling));
            }
        }
    }
}
