using System.Reflection;
using System.Xml;

namespace Test.KitbashEditor.Services
{
    public class PackTextureAtlasValueGateTests
    {
        private static double GetChargeableBytes(double generatedBytes, double retiredSourceBytes)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "GetChargeableAtlasValueGateBytes",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "PackTextureAtlasBatchService.GetChargeableAtlasValueGateBytes was not found.");

            return (double)(method.Invoke(null, [generatedBytes, retiredSourceBytes])
                ?? throw new InvalidOperationException(
                    "GetChargeableAtlasValueGateBytes returned null."));
        }


        private static bool IsSourceTextureRetired(
            bool hasDirectVmdReference,
            int referenceCount,
            int rewrittenReferenceCount)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "IsAtlasValueGateSourceTextureRetired",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "PackTextureAtlasBatchService.IsAtlasValueGateSourceTextureRetired was not found.");

            return (bool)(method.Invoke(
                null,
                [hasDirectVmdReference, referenceCount, rewrittenReferenceCount])
                ?? throw new InvalidOperationException(
                    "IsAtlasValueGateSourceTextureRetired returned null."));
        }


        private static bool IsIgnorableUnresolvedTexture(string slot, string path)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "IsIgnorableUnresolvedAtlasTexture",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "PackTextureAtlasBatchService.IsIgnorableUnresolvedAtlasTexture was not found.");

            return (bool)(method.Invoke(null, [slot, path])
                ?? throw new InvalidOperationException(
                    "IsIgnorableUnresolvedAtlasTexture returned null."));
        }


        private static void RemoveTextureSlot(XmlDocument material, string slot)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "RemoveTextureSlot",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "PackTextureAtlasBatchService.RemoveTextureSlot was not found.");

            method.Invoke(null, [material, slot]);
        }



        private static long GetConstant(string name)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var field = serviceType.GetField(
                name,
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    $"PackTextureAtlasBatchService.{name} was not found.");

            return (long)(field.GetRawConstantValue()
                ?? throw new InvalidOperationException($"{name} has no constant value."));
        }

        private static double GetMiBPerDraw(double netBytes, double drawsEliminated)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "GetAtlasValueGateMiBPerDraw",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "PackTextureAtlasBatchService.GetAtlasValueGateMiBPerDraw was not found.");

            return (double)(method.Invoke(null, [netBytes, drawsEliminated])
                ?? throw new InvalidOperationException(
                    "GetAtlasValueGateMiBPerDraw returned null."));
        }

        private static bool IsEmbeddedTextureTypeSafe(string textureTypeName)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "IsEmbeddedAtlasTextureTypeSafe",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "PackTextureAtlasBatchService.IsEmbeddedAtlasTextureTypeSafe was not found.");

            var parameterType = method.GetParameters().Single().ParameterType;
            var textureType = Enum.Parse(parameterType, textureTypeName);
            return (bool)(method.Invoke(null, [textureType])
                ?? throw new InvalidOperationException(
                    "IsEmbeddedAtlasTextureTypeSafe returned null."));
        }

        private static bool CanEarnMergeDrawCredit(string? embeddedRigidPath)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Single(candidate =>
                    candidate.Name == "CanEarnMergeDrawCredit" &&
                    candidate.GetParameters().Length == 1 &&
                    candidate.GetParameters()[0].ParameterType == typeof(string));

            return (bool)(method.Invoke(null, [embeddedRigidPath])
                ?? throw new InvalidOperationException(
                    "CanEarnMergeDrawCredit returned null."));
        }

        [TestCase(16.0, 0.0, 16.0)]
        [TestCase(16.0, 12.0, 4.0)]
        [TestCase(16.0, 16.0, 0.0)]
        [TestCase(16.0, 20.0, 0.0)]
        public void ChargeableBytes_UsesOnlyPositiveIncrementalResidency(
            double generatedBytes,
            double retiredSourceBytes,
            double expected)
        {
            Assert.That(
                GetChargeableBytes(generatedBytes, retiredSourceBytes),
                Is.EqualTo(expected));
        }
        [TestCase(false, 2, 2, true)]
        [TestCase(false, 2, 1, false)]
        [TestCase(false, 0, 0, false)]
        [TestCase(true, 2, 2, false)]
        public void SourceTextureRetirement_RequiresCompleteRewriteCoverageAndNoDirectVmdReference(
            bool hasDirectVmdReference,
            int referenceCount,
            int rewrittenReferenceCount,
            bool expected)
        {
            Assert.That(
                IsSourceTextureRetired(
                    hasDirectVmdReference,
                    referenceCount,
                    rewrittenReferenceCount),
                Is.EqualTo(expected));
        }

        [TestCase("t_xml_mask", "test_mask.dds", true)]
        [TestCase("t_xml_mask", @"variantmeshes\foo\tex\test_mask.dds", true)]
        [TestCase("T_XML_MASK", @"variantmeshes/foo/tex/TEST_MASK.DDS", true)]
        [TestCase("t_xml_mask", @"variantmeshes\foo\tex\real_mask.dds", false)]
        [TestCase("t_xml_material_map", "test_mask.dds", false)]
        [TestCase("t_xml_base_colour", "test_mask.dds", false)]
        public void UnresolvedTestMask_IsOnlyIgnoredForMaskSlot(
            string slot,
            string path,
            bool expected)
        {
            Assert.That(IsIgnorableUnresolvedTexture(slot, path), Is.EqualTo(expected));
        }


        [Test]
        public void RemoveTextureSlot_RemovesMaskWithoutTouchingOtherTextures()
        {
            var material = new XmlDocument();
            material.LoadXml(
                "<material><textures>" +
                "<texture><slot>t_xml_base_colour</slot><source>base.dds</source></texture>" +
                "<texture><slot>t_xml_mask</slot><source>test_mask.dds</source></texture>" +
                "</textures></material>");

            RemoveTextureSlot(material, "t_xml_mask");

            Assert.Multiple(() =>
            {
                Assert.That(
                    material.SelectSingleNode(
                        "/material/textures/texture[slot='t_xml_mask']"),
                    Is.Null);
                Assert.That(
                    material.SelectSingleNode(
                        "/material/textures/texture[slot='t_xml_base_colour']/source")?.InnerText,
                    Is.EqualTo("base.dds"));
            });
        }

        [TestCase("BaseColour", true)]
        [TestCase("Diffuse", true)]
        [TestCase("MaterialMap", true)]
        [TestCase("Normal", true)]
        [TestCase("Mask", true)]
        [TestCase("Emissive", false)]
        [TestCase("EmissiveDistortion", false)]
        [TestCase("Ambient_occlusion", false)]
        [TestCase("Specular", false)]
        [TestCase("Gloss", false)]
        public void EmbeddedRigidAtlasing_OnlyAcceptsTextureTypesItRewrites(
            string textureType,
            bool expected)
        {
            Assert.That(IsEmbeddedTextureTypeSafe(textureType), Is.EqualTo(expected));
        }

        [TestCase(null, true)]
        [TestCase("", true)]
        [TestCase(@"warmachines\engine.rigid_model_v2", false)]
        public void DirectEmbeddedRigid_CannotEarnWsModelMergeDrawCredit(
            string? embeddedRigidPath,
            bool expected)
        {
            Assert.That(CanEarnMergeDrawCredit(embeddedRigidPath), Is.EqualTo(expected));
        }

        [TestCase(8.0 * 1024 * 1024, 4.0, 2.0)]
        [TestCase(512.0 * 1024, 2.0, 0.25)]
        [TestCase(-8.0 * 1024 * 1024, 4.0, 0.0)]
        [TestCase(8.0 * 1024 * 1024, 0.0, 0.0)]
        public void MiBPerDraw_UsesPositiveNetResidencyPerEliminatedDraw(
            double netBytes,
            double drawsEliminated,
            double expected)
        {
            Assert.That(
                GetMiBPerDraw(netBytes, drawsEliminated),
                Is.EqualTo(expected).Within(0.000001));
        }


        [Test]
        public void ValueGateBudgets_MatchCalibratedResidencyCurve()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    GetConstant("MaxNetBcnBytesPerExpectedArmyDraw"),
                    Is.EqualTo(256L * 1024));
                Assert.That(
                    GetConstant("MaxNetBcnBytesPerFallbackDraw"),
                    Is.EqualTo(8L * 1024 * 1024));
            });
        }



    }
}
