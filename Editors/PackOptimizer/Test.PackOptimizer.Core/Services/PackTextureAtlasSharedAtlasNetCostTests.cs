using System.Reflection;
using Editors.PackOptimizer.Core;
using Shared.GameFormats.RigidModel.Types;

namespace Test.PackOptimizer.Core
{
    // Phase 0 (report only) net texture cost model of a cross-rigid shared atlas.
    public class PackTextureAtlasSharedAtlasNetCostTests
    {
        private const string Base = "t_xml_base_colour";
        private const long Mib = 1024 * 1024;

        private sealed class Fixture
        {
            public Dictionary<string, PackTextureAtlasBatchService.SharedAtlasMaterial> Materials { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, PackTextureAtlasBatchService.SharedAtlasTexture> Textures { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, List<(string Material, string Slot)>> Referrers { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Blocks { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public List<PackTextureAtlasBatchService.SharedAtlasPayload> Payloads { get; } = [];
            public List<PackTextureAtlasBatchService.SharedAtlasRepackTexture>? Repack { get; set; }
            public Dictionary<string, string[]> Rigids { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            public void AddMaterial(
                string material,
                int size,
                string? ineligible = null,
                double? crop = null,
                long bytes = Mib,
                string[]? keepAlpha = null,
                string? textureOverride = null)
            {
                var texture = textureOverride ?? $"tex\\{material}.dds";
                Materials[material] = new PackTextureAtlasBatchService.SharedAtlasMaterial(
                    material,
                    ineligible,
                    [new PackTextureAtlasBatchService.SharedAtlasChannelTexture(
                        Base,
                        TextureType.BaseColour,
                        texture)],
                    keepAlpha ?? []);
                Textures[texture] = new PackTextureAtlasBatchService.SharedAtlasTexture(
                    bytes,
                    size,
                    size,
                    crop);
                Reference(texture, material, Base);
                Rigids[material] = [$"rigid\\{material}"];
            }

            public void Reference(string texture, string material, string slot)
            {
                if (!Referrers.TryGetValue(texture, out var list))
                    Referrers[texture] = list = [];
                list.Add((material, slot));
            }

            public void AddPayload(string key, double saving, params string[][] groups)
                => Payloads.Add(new PackTextureAtlasBatchService.SharedAtlasPayload(
                    key,
                    saving,
                    Mib,
                    groups));

            public PackTextureAtlasBatchService.SharedAtlasCostResult Run()
                => PackTextureAtlasBatchService.ComputeCrossRigidSharedAtlasCost(
                    new PackTextureAtlasBatchService.SharedAtlasCostInput(
                        Payloads,
                        material => Materials[material],
                        texture => Textures.GetValueOrDefault(texture),
                        Referrers,
                        texture => Blocks.GetValueOrDefault(texture),
                        material => Rigids.GetValueOrDefault(material) ?? [],
                        Repack));
        }

        private static long MipBytes(int width, int height, int bytesPerBlock)
            => (long)typeof(PackTextureAtlasBatchService)
                .GetMethod(
                    "CalculateBcnMipChainBytes",
                    BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [width, height, bytesPerBlock])!;

        [Test]
        public void Retirement_SharedTextureUsedOutsideTheComponentIsNotRetired()
        {
            var fixture = new Fixture();
            fixture.AddMaterial("m1", 512, bytes: 3 * Mib);
            fixture.AddMaterial("m2", 512, bytes: 5 * Mib);
            // m2's texture is also named by a material that is not atlased.
            fixture.Reference("tex\\m2.dds", "outsider", Base);
            fixture.AddPayload("p", 10, ["m1", "m2"]);

            var result = fixture.Run();

            Assert.That(result.SelectedComponentCount, Is.EqualTo(1));
            Assert.That(result.RetiredTextureCount, Is.EqualTo(1));
            Assert.That(result.RetiredBytes, Is.EqualTo(3 * Mib));
            Assert.That(result.KeptExternalTextureCount, Is.EqualTo(1));
        }

        [Test]
        public void Retirement_TextureNamedInANonAtlasSlotIsNotRetired()
        {
            var fixture = new Fixture();
            fixture.AddMaterial("m1", 512, bytes: 3 * Mib);
            fixture.AddMaterial("m2", 512, bytes: 5 * Mib);
            fixture.Reference("tex\\m1.dds", "m2", "t_xml_emissive");
            fixture.AddPayload("p", 10, ["m1", "m2"]);

            var result = fixture.Run();

            Assert.That(result.RetiredBytes, Is.EqualTo(5 * Mib));
            Assert.That(result.RetiredTextureCount, Is.EqualTo(1));
        }

        [Test]
        public void Retirement_VanillaTextureIsNeverRetiredAndIsPureCost()
        {
            var fixture = new Fixture();
            fixture.AddMaterial("m1", 512, bytes: 3 * Mib);
            fixture.AddMaterial("m2", 512, bytes: 5 * Mib);
            fixture.Blocks["tex\\m2.dds"] = "vanilla";
            fixture.AddPayload("p", 10, ["m1", "m2"]);

            var result = fixture.Run();

            Assert.That(result.RetiredBytes, Is.EqualTo(3 * Mib));
            Assert.That(result.KeptVanillaTextureCount, Is.EqualTo(1));
            Assert.That(result.NetBytes, Is.EqualTo(result.AtlasBytes - 3 * Mib));
        }

        [Test]
        public void Retirement_TextureSharedByTwoComponentsRetiresWhenTheLastIsAdded()
        {
            var fixture = new Fixture();
            fixture.AddMaterial("a1", 256);
            fixture.AddMaterial("a2", 256);
            fixture.AddMaterial("b1", 256);
            fixture.AddMaterial("b2", 256);
            // One texture named by a1 and b1.
            fixture.Reference("tex\\a1.dds", "b1", Base);
            fixture.AddPayload("pa", 10, ["a1", "a2"]);
            fixture.AddPayload("pb", 4, ["b1", "b2"]);

            var shared = fixture.Run();
            // a1 and b1 are in different components, both selected: the
            // shared texture is retired, but only after the second one.
            Assert.That(shared.SelectedComponentCount, Is.EqualTo(2));
            Assert.That(shared.RetiredTextureCount, Is.EqualTo(4));
            Assert.That(shared.Curve[0].MarginalNetBytes,
                Is.GreaterThan(shared.Curve[1].MarginalNetBytes));

            // Drop pb: b1 is no longer atlased, so a1's texture is kept.
            fixture.Payloads.RemoveAt(1);
            var alone = fixture.Run();
            Assert.That(alone.RetiredTextureCount, Is.EqualTo(1));
        }

        [Test]
        public void NetBytes_AreAtlasMinusRetiredAndMatchTheDryRunPage()
        {
            var fixture = new Fixture();
            fixture.AddMaterial("m1", 512, bytes: 2 * Mib);
            fixture.AddMaterial("m2", 512, bytes: 2 * Mib);
            fixture.AddPayload("p", 10, ["m1", "m2"]);

            var result = fixture.Run();

            var page = PackTextureAtlasBatchService.TryPlanCrossRigidSharedAtlasPage(
                [(0, 512, 512, 512, 512), (1, 512, 512, 512, 512)]);
            Assert.That(page.Fits, Is.True);
            var expectedAtlas = MipBytes(page.Width, page.Height, 8);
            Assert.That(result.AtlasBytes, Is.EqualTo(expectedAtlas));
            Assert.That(result.AtlasBytesFullRect, Is.EqualTo(expectedAtlas));
            Assert.That(result.RetiredBytes, Is.EqualTo(4 * Mib));
            Assert.That(result.NetBytes, Is.EqualTo(expectedAtlas - 4 * Mib));
            Assert.That(result.Curve, Has.Count.EqualTo(1));
            Assert.That(result.Curve[0].NetBytes, Is.EqualTo(result.NetBytes));
            Assert.That(result.Curve[0].Draws, Is.EqualTo(10).Within(1e-9));
            Assert.That(result.GeometryBytes, Is.EqualTo(Mib));
        }

        [Test]
        public void NetBytes_KeptAlphaDoublesTheBlockSize()
        {
            var plain = new Fixture();
            plain.AddMaterial("m1", 512);
            plain.AddMaterial("m2", 512);
            plain.AddPayload("p", 1, ["m1", "m2"]);
            var alpha = new Fixture();
            alpha.AddMaterial("m1", 512, keepAlpha: [Base]);
            alpha.AddMaterial("m2", 512);
            alpha.AddPayload("p", 1, ["m1", "m2"]);

            Assert.That(
                alpha.Run().AtlasBytes,
                Is.EqualTo(2 * plain.Run().AtlasBytes));
        }

        [Test]
        public void Fit_TwoFullResolutionPagesDoNotFitButTheirCropsDo()
        {
            var fixture = new Fixture();
            fixture.AddMaterial("m1", 4096, crop: 0.2);
            fixture.AddMaterial("m2", 4096, crop: 0.2);
            fixture.AddPayload("p", 7, ["m1", "m2"]);

            var cropped = fixture.Run();
            Assert.That(cropped.ComponentCount, Is.EqualTo(1));
            Assert.That(cropped.FitComponentCount, Is.EqualTo(1));
            Assert.That(cropped.FitFullRectComponentCount, Is.EqualTo(0));
            Assert.That(cropped.SelectedComponentCount, Is.EqualTo(1));
            Assert.That(cropped.EligibleSaving, Is.EqualTo(7).Within(1e-9));

            var fixtureFull = new Fixture();
            fixtureFull.AddMaterial("m1", 4096);
            fixtureFull.AddMaterial("m2", 4096);
            fixtureFull.AddPayload("p", 7, ["m1", "m2"]);
            var full = fixtureFull.Run();
            Assert.That(full.FitComponentCount, Is.EqualTo(0));
            Assert.That(full.SelectedComponentCount, Is.EqualTo(0));
            Assert.That(full.EligibleSaving, Is.EqualTo(0));
            Assert.That(full.LostFitSaving, Is.EqualTo(7).Within(1e-9));
            Assert.That(full.Curve, Is.Empty);
        }

        [Test]
        public void Ineligible_LostSavingIsAttributedToTheFirstReason()
        {
            var fixture = new Fixture();
            fixture.AddMaterial("a1", 256);
            fixture.AddMaterial("a2", 256);
            fixture.AddMaterial("b1", 256);
            fixture.AddMaterial("b2", 256, ineligible: "emissive");
            fixture.AddMaterial("c1", 256, ineligible: "no base colour");
            fixture.AddMaterial("c2", 256, ineligible: "emissive");
            fixture.AddPayload("pa", 10, ["a1", "a2"]);
            fixture.AddPayload("pb", 4, ["b1", "b2"]);
            fixture.AddPayload("pc", 3, ["c1", "c2"]);

            var result = fixture.Run();

            Assert.That(result.MaterialCount, Is.EqualTo(6));
            Assert.That(result.EligibleMaterialCount, Is.EqualTo(3));
            Assert.That(result.AtlasPayloadSaving, Is.EqualTo(17).Within(1e-9));
            Assert.That(result.LostIneligibleSaving, Is.EqualTo(7).Within(1e-9));
            Assert.That(result.EligibleSaving, Is.EqualTo(10).Within(1e-9));
            Assert.That(result.IneligibleMaterialsByReason["emissive"], Is.EqualTo(2));
            Assert.That(result.LostSavingByReason["emissive"], Is.EqualTo(4).Within(1e-9));
            // "c1" sorts before "c2": the first ineligible member decides.
            Assert.That(result.LostSavingByReason["no base colour"], Is.EqualTo(3).Within(1e-9));
            // b1 is eligible but only needed by the lost payload: no component.
            Assert.That(result.ComponentCount, Is.EqualTo(1));
        }

        [Test]
        public void Curve_PayloadSpanningTwoComponentsCountsAtTheLastOneAdded()
        {
            var fixture = new Fixture();
            foreach (var material in new[] { "a1", "a2", "b1", "b2" })
                fixture.AddMaterial(material, 256);
            // Component A has a cheap payload of its own; the spanning
            // payload needs both components.
            fixture.AddPayload("own", 100, ["a1", "a2"]);
            fixture.AddPayload("span", 50, ["a1", "a2"], ["b1", "b2"]);

            var result = fixture.Run();

            Assert.That(result.SelectedComponentCount, Is.EqualTo(2));
            Assert.That(result.Curve, Has.Count.EqualTo(2));
            Assert.That(result.Curve[0].MarginalDraws, Is.EqualTo(100).Within(1e-9));
            Assert.That(result.Curve[1].MarginalDraws, Is.EqualTo(50).Within(1e-9));
            Assert.That(result.Curve[1].Draws, Is.EqualTo(150).Within(1e-9));
            Assert.That(result.Curve[1].GeometryBytes, Is.EqualTo(2 * Mib));
            Assert.That(result.Curve[1].NetBytes, Is.EqualTo(result.NetBytes));
        }

        [Test]
        public void Reachable_PricePrefixesFollowTheCurve()
        {
            var fixture = new Fixture();
            // Retiring 10 MiB against a ~0.4 MiB atlas: a net gain.
            fixture.AddMaterial("a1", 256, bytes: 10 * Mib);
            fixture.AddMaterial("a2", 256, bytes: 10 * Mib);
            fixture.AddPayload("p", 5, ["a1", "a2"]);

            var result = fixture.Run();

            Assert.That(result.NetBytes, Is.LessThan(0));
            Assert.That(result.Reachable, Has.Count.EqualTo(4));
            Assert.That(result.Reachable.All(item => item.AverageDraws == 5), Is.True);
        }

        [Test]
        public void RepackLoss_CountsUnretiredTexturesOfChangedRigids()
        {
            var fixture = new Fixture();
            fixture.AddMaterial("m1", 256);
            fixture.AddMaterial("m2", 256);
            fixture.AddPayload("p", 5, ["m1", "m2"]);
            fixture.Repack =
            [
                // Retired by the atlas: no loss.
                new("tex\\m1.dds", 100, new[] { "rigid\\m1" }),
                // Not retired (a vanilla-kept extra texture) on a changed rigid.
                new("tex\\other.dds", 40, new[] { "rigid\\m2" }),
                // Untouched rigid.
                new("tex\\far.dds", 7, new[] { "rigid\\far" }),
            ];

            Assert.That(fixture.Run().LostRepackBytes, Is.EqualTo(40));
            fixture.Repack = null;
            Assert.That(fixture.Run().LostRepackBytes, Is.Null);
        }

        [Test]
        public void Lines_AreCompactAndMentionEveryMetric()
        {
            var fixture = new Fixture();
            fixture.AddMaterial("m1", 256);
            fixture.AddMaterial("m2", 256);
            fixture.AddPayload("p", 5, ["m1", "m2"]);
            var lines = PackTextureAtlasBatchService
                .BuildCrossRigidSharedAtlasCostLines(fixture.Run(), 1000);

            Assert.That(lines, Has.Count.EqualTo(5));
            Assert.That(lines[0], Does.Contain("eligibility"));
            Assert.That(lines[1], Does.Contain("fit one page"));
            Assert.That(lines[2], Does.Contain("retired"));
            Assert.That(lines[3], Does.Contain("curve"));
            Assert.That(lines[4], Does.Contain("reachable"));
        }
    }
}
