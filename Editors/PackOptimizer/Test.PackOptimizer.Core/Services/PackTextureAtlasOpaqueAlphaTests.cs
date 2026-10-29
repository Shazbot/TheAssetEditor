using System.Collections;
using System.Reflection;
using System.Text;

namespace Test.PackOptimizer.Core
{
    // Opt-in switch of opaque alpha-tested character materials to the non-alpha twin shader
    // (PackTextureAtlasBatchService.OpaqueAlpha.cs).
    public partial class PackTextureAtlasCrossRigidTests
    {
        private const string OaAlphaShader = "weighted4_character_alpha";

        private static byte[] OaTexture(Func<int, int, byte[]> blocks)
            => CxBuild(64, 64, 7, "BC3", true, true, blocks);

        private static byte[] OaOpaque()
            => OaTexture((level, block) => CxOpaqueBlock(level, block, "BC3"));

        // Alpha 252 everywhere: opaque for the switch (>= 250) but real alpha for compaction.
        private static byte[] OaAlmostOpaque()
            => OaTexture((level, block) => CxBc3Block(252, 252, 0, CxOpaqueBlock(level, block, "BC3")[8..16]));

        // One block of every level has alpha 200.
        private static byte[] OaOneLowTexel()
            => OaTexture((level, block) => block == 0
                ? CxBc3Block(200, 200, 0, CxOpaqueBlock(level, block, "BC3")[8..16])
                : CxOpaqueBlock(level, block, "BC3"));

        private static (object Service, object State) OaRun(DownscaleFixture fixture, Action<object>? configure = null)
        {
            var (service, state) = CreateDownscaleState(fixture, false);
            configure?.Invoke(state);
            GetServiceType()
                .GetMethod("SwitchOpaqueAlphaShaders", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [state, CancellationToken.None, null]);
            return (service, state);
        }

        private static object OaResult(object state) => Get<object>(state, "OpaqueAlpha");

        private static List<string> OaSwitched(object state)
            => Get<IEnumerable>(OaResult(state), "Switched").Cast<object>().Select(item => Get<string>(item, "Path")).ToList();

        private static Dictionary<string, int> OaSkipped(object state)
            => Get<Dictionary<string, int>>(OaResult(state), "Skipped");

        private static string OaMaterialText(DownscaleFixture fixture)
            => Encoding.UTF8.GetString(fixture.Output[CxMaterial]);

        private static List<string> OaValidate(object state)
        {
            var errors = new List<string>();
            GetStaticMethod("ValidateOpaqueAlpha").Invoke(null, [state, errors, CancellationToken.None]);
            return errors;
        }

        [Test]
        public void OpaqueAlpha_OpaqueWeighted4CharacterAlphaMaterial_IsSwitchedToTheTwinShader()
        {
            var fixture = CxAlphaFixture(OaAlphaShader, "t_xml_base_colour", OaOpaque());
            var before = Encoding.UTF8.GetString(fixture.Output[CxMaterial]);

            var (_, state) = OaRun(fixture);

            Assert.That(OaSwitched(state), Is.EqualTo(new[] { CxMaterial }));
            var after = OaMaterialText(fixture);
            Assert.That(after, Does.Contain("<shader>shaders\\weighted4_character.xml.shader</shader>"));
            Assert.That(after, Is.EqualTo(before.Replace("weighted4_character_alpha.xml.shader", "weighted4_character.xml.shader")));
            Assert.That(OaValidate(state), Is.Empty);
            var report = new StringBuilder();
            GetStaticMethod("AppendOpaqueAlphaSection").Invoke(null, [report, state]);
            Assert.That(report.ToString(), Does.Contain("Opaque alpha shaders").And.Contain("switched: 1"));
        }

        [TestCase("rigid_character_alpha")]
        [TestCase("weighted2_character_alpha")]
        public void OpaqueAlpha_OtherPlainCharacterFamilies_AreSwitchedToo(string shader)
        {
            var fixture = CxAlphaFixture(shader, "t_xml_base_colour", OaOpaque());

            var (_, state) = OaRun(fixture);

            Assert.That(OaSwitched(state), Has.Count.EqualTo(1));
            Assert.That(OaMaterialText(fixture), Does.Contain(shader.Replace("_alpha", string.Empty) + ".xml.shader"));
        }

        [Test]
        public void OpaqueAlpha_OneTexelBelowTheLimit_IsNotSwitched()
        {
            var fixture = CxAlphaFixture(OaAlphaShader, "t_xml_base_colour", OaOneLowTexel());
            var before = fixture.Output[CxMaterial];

            var (_, state) = OaRun(fixture);

            Assert.That(OaSwitched(state), Is.Empty);
            Assert.That(fixture.Output[CxMaterial], Is.EqualTo(before));
            Assert.That(OaSkipped(state).Keys, Has.Some.Contain("not opaque"));
        }

        [Test]
        public void OpaqueAlpha_NonPlainAlphaFamily_IsNotSwitched()
        {
            var fixture = CxAlphaFixture("weighted4_emissive_alpha", "t_xml_base_colour", OaOpaque());
            var before = fixture.Output[CxMaterial];

            var (_, state) = OaRun(fixture);

            Assert.That(OaSwitched(state), Is.Empty);
            Assert.That(fixture.Output[CxMaterial], Is.EqualTo(before));
            Assert.That(OaSkipped(state).Keys, Has.Some.Contain("not a plain character alpha shader"));
        }

        [Test]
        public void OpaqueAlpha_MaterialUsedByAnotherMod_IsNotSwitched()
        {
            var fixture = CxAlphaFixture(OaAlphaShader, "t_xml_base_colour", OaOpaque());
            var before = fixture.Output[CxMaterial];

            var (_, state) = OaRun(
                fixture,
                configure: built => InstallConsumers(
                    built,
                    0,
                    ("OtherMod", ConsumerFiles((
                        "variantmeshes\\mod\\other.wsmodel",
                        $"<model><geometry>variantmeshes\\mod\\other.rigid_model_v2</geometry><materials><material lod_index=\"0\" part_index=\"0\">{CxMaterial}</material></materials></model>")))));

            Assert.That(OaSwitched(state), Is.Empty);
            Assert.That(fixture.Output[CxMaterial], Is.EqualTo(before));
            Assert.That(OaSkipped(state).Keys, Has.Some.Contain("another mod"));
        }

        [Test]
        public void OpaqueAlpha_VanillaMaterialOverride_AlphaTintAndAmbiguousSources_AreNotSwitched()
        {
            var overriding = CxAlphaFixture(OaAlphaShader, "t_xml_base_colour", OaOpaque());
            overriding.Vanilla[CxMaterial] = [1];
            var (_, overrideState) = OaRun(overriding);
            Assert.That(OaSwitched(overrideState), Is.Empty);
            Assert.That(OaSkipped(overrideState).Keys, Has.Some.Contain("overrides a vanilla material"));

            var tint = CxAlphaFixture(OaAlphaShader, "t_xml_base_colour", OaOpaque());
            tint.AddText(
                CxMaterial,
                Encoding.UTF8.GetString(tint.Output[CxMaterial]).Replace(
                    "</material>",
                    "<params><param><name>alpha_pixels_colour</name><value>1,1,1,1</value></param></params></material>"));
            var (_, tintState) = OaRun(tint);
            Assert.That(OaSwitched(tintState), Is.Empty);
            Assert.That(OaSkipped(tintState).Keys, Has.Some.Contain("alpha_pixels_colour"));

            var ambiguous = new DownscaleFixture();
            ambiguous.Add(CxTexture, OaOpaque());
            ambiguous.Add("textures\\cx_two.dds", OaOpaque());
            ambiguous.AddText(
                CxMaterial,
                CxMaterialWith(OaAlphaShader, ("t_xml_base_colour", CxTexture), ("s_diffuse", "textures\\cx_two.dds")));
            var (_, ambiguousState) = OaRun(ambiguous);
            Assert.That(OaSwitched(ambiguousState), Is.Empty);
            Assert.That(OaSkipped(ambiguousState).Keys, Has.Some.Contain("several different alpha source textures"));
        }

        [Test]
        public void OpaqueAlpha_UnsupportedAlphaTextureFormat_IsNotSwitched()
        {
            var fixture = new DownscaleFixture();
            fixture.Add(CxTexture, CxBuild(64, 64, 1, "BC3", true, false, (_, _) => CxOpaqueBlock(0, 0, "BC3"), dxgiFormat: 98));
            fixture.AddText(CxMaterial, CxMaterialWith(OaAlphaShader, ("t_xml_base_colour", CxTexture)));

            var (_, state) = OaRun(fixture);

            Assert.That(OaSwitched(state), Is.Empty);
            Assert.That(OaSkipped(state).Keys, Has.Some.Contain("not decoded exactly"));
        }

        [Test]
        public void OpaqueAlpha_SwitchLetsCompactionDropTheUnreadAlpha_ControlKeepsIt()
        {
            void Compact(object service, object state)
            {
                SetStateProperty(state, "CompactTextures", true);
                SetStateProperty(state, "DropUnreadAlpha", true);
                GetServiceType()
                    .GetMethod("RunTextureCompaction", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(service, [state, CancellationToken.None, null]);
            }

            var control = CxAlphaFixture(OaAlphaShader, "t_xml_base_colour", OaAlmostOpaque());
            var (controlService, controlState) = CreateDownscaleState(control, false);
            Compact(controlService, controlState);
            Assert.That(CxCompacted(controlState), Is.Zero, "the alpha shader reads the alpha, so the texture stays BC3");

            var fixture = CxAlphaFixture(OaAlphaShader, "t_xml_base_colour", OaAlmostOpaque());
            var (service, state) = CreateDownscaleState(fixture, false);
            GetServiceType()
                .GetMethod("SwitchOpaqueAlphaShaders", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [state, CancellationToken.None, null]);
            Compact(service, state);

            Assert.That(OaSwitched(state), Has.Count.EqualTo(1));
            Assert.That(CxCompacted(state), Is.EqualTo(1));
            Assert.That(ReadDds(fixture.Output[CxTexture]).Format, Is.EqualTo("BC1"));
            Assert.That(OaValidate(state), Is.Empty, "a BC1 texture without punch-through is still opaque");
            var report = new StringBuilder();
            GetStaticMethod("AppendOpaqueAlphaSection").Invoke(null, [report, state]);
            Assert.That(report.ToString(), Does.Contain("dropped to BC1: 1"));
        }

        [Test]
        public void OpaqueAlpha_Validation_FailsWhenTheAlphaTextureTurnsTransparentLater()
        {
            var fixture = CxAlphaFixture(OaAlphaShader, "t_xml_base_colour", OaOpaque());
            var (_, state) = OaRun(fixture);
            Assert.That(OaValidate(state), Is.Empty);

            fixture.Output[CxTexture] = OaOneLowTexel();

            Assert.That(OaValidate(state), Has.Some.Contain("no longer opaque"));
        }
    }
}
