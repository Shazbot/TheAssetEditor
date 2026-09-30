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

        private static double GetExpectedConfigurationMergeDrawSavings(
            double[] probabilities,
            int[][] coRenderedCountsByConfiguration,
            int entityCount)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "CalculateExpectedConfigurationMergeDrawSavings",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "PackTextureAtlasBatchService.CalculateExpectedConfigurationMergeDrawSavings was not found.");

            return (double)(method.Invoke(
                null,
                [probabilities, coRenderedCountsByConfiguration, entityCount])
                ?? throw new InvalidOperationException(
                    "CalculateExpectedConfigurationMergeDrawSavings returned null."));
        }

        private static object GetMergeAffinityIdentity(
            string geometryPath,
            int lodIndex,
            string rmvIdentity,
            string materialIdentity)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Single(candidate =>
                    candidate.Name == "BuildMergeAffinityIdentity" &&
                    candidate.GetParameters().Length == 4 &&
                    candidate.GetParameters()[0].ParameterType == typeof(string));

            return method.Invoke(
                       null,
                       [geometryPath, lodIndex, rmvIdentity, materialIdentity])
                   ?? throw new InvalidOperationException(
                       "BuildMergeAffinityIdentity returned null.");
        }

        private static string GetValueGateBudgetDecision(
            bool scenarioResolved,
            int rawDrawsEliminated,
            double expectedArmyDrawsEliminated,
            double globalCostBytes,
            double expectedCostBytes,
            double acceptedNetBcnBytes,
            double proposedNetBcnBytes,
            double sourceBcnBytes)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "EvaluateAtlasValueGateBudget",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "PackTextureAtlasBatchService.EvaluateAtlasValueGateBudget was not found.");

            return method.Invoke(
                       null,
                       [
                           scenarioResolved,
                           rawDrawsEliminated,
                           expectedArmyDrawsEliminated,
                           globalCostBytes,
                           expectedCostBytes,
                           acceptedNetBcnBytes,
                           proposedNetBcnBytes,
                           sourceBcnBytes,
                       ])?.ToString()
                   ?? throw new InvalidOperationException(
                       "EvaluateAtlasValueGateBudget returned null.");
        }

        private static bool IsSharedMeshSafeForGameplay(
            bool atlasAllVmdsEnabled,
            IEnumerable<string> affectedWsModels,
            IReadOnlySet<string> taintedGameplayWsModels)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "IsSharedMeshSafeForGameplay",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "PackTextureAtlasBatchService.IsSharedMeshSafeForGameplay was not found.");

            return (bool)(method.Invoke(
                null,
                [atlasAllVmdsEnabled, affectedWsModels, taintedGameplayWsModels])
                ?? throw new InvalidOperationException(
                    "IsSharedMeshSafeForGameplay returned null."));
        }

        private static bool IsCandidateAllowedForMode(
            bool atlasAllVmdsEnabled,
            bool scenarioResolved)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "IsAtlasValueCandidateAllowedForMode",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "PackTextureAtlasBatchService.IsAtlasValueCandidateAllowedForMode was not found.");

            return (bool)(method.Invoke(
                null,
                [atlasAllVmdsEnabled, scenarioResolved])
                ?? throw new InvalidOperationException(
                    "IsAtlasValueCandidateAllowedForMode returned null."));
        }

        private static string GetMarginalValueGateBudgetDecision(
            bool scenarioResolved,
            int baseRawDrawsEliminated,
            int combinedRawDrawsEliminated,
            double baseExpectedArmyDrawsEliminated,
            double combinedExpectedArmyDrawsEliminated,
            double baseGeneratedBcnBytes,
            double baseRetiredSourceBcnBytes,
            double combinedGeneratedBcnBytes,
            double combinedRetiredSourceBcnBytes,
            double baseExpectedGeneratedBcnBytes,
            double baseExpectedRetiredSourceBcnBytes,
            double combinedExpectedGeneratedBcnBytes,
            double combinedExpectedRetiredSourceBcnBytes,
            double acceptedNetBcnBytes,
            double sourceBcnBytes)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "EvaluateMarginalAtlasValueGateBudget",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "PackTextureAtlasBatchService.EvaluateMarginalAtlasValueGateBudget was not found.");

            return method.Invoke(
                       null,
                       [
                           scenarioResolved,
                           baseRawDrawsEliminated,
                           combinedRawDrawsEliminated,
                           baseExpectedArmyDrawsEliminated,
                           combinedExpectedArmyDrawsEliminated,
                           baseGeneratedBcnBytes,
                           baseRetiredSourceBcnBytes,
                           combinedGeneratedBcnBytes,
                           combinedRetiredSourceBcnBytes,
                           baseExpectedGeneratedBcnBytes,
                           baseExpectedRetiredSourceBcnBytes,
                           combinedExpectedGeneratedBcnBytes,
                           combinedExpectedRetiredSourceBcnBytes,
                           acceptedNetBcnBytes,
                           sourceBcnBytes,
                       ])?.ToString()
                   ?? throw new InvalidOperationException(
                       "EvaluateMarginalAtlasValueGateBudget returned null.");
        }

        private static double GetDoubleConstant(string name)
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

            return Convert.ToDouble(
                field.GetRawConstantValue()
                ?? throw new InvalidOperationException($"{name} has no constant value."));
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
        public void CoOccurrence_MutuallyExclusiveAlternativesEarnNoMergeDrawSavings()
        {
            var savings = GetExpectedConfigurationMergeDrawSavings(
                [0.5, 0.5],
                [
                    [1, 0],
                    [0, 1],
                ],
                120);

            Assert.That(savings, Is.EqualTo(0.0));
        }

        [Test]
        public void CoOccurrence_RequiredMeshesScaleSavingsPerEntity()
        {
            var savings = GetExpectedConfigurationMergeDrawSavings(
                [1.0],
                [
                    [1, 1],
                ],
                120);

            Assert.That(savings, Is.EqualTo(120.0));
        }

        [Test]
        public void MergeAffinity_DifferentLodsRemainSeparateDrawScopes()
        {
            var lod0 = GetMergeAffinityIdentity(
                @"variantmeshes\test.rigid_model_v2",
                0,
                "rmv",
                "material");
            var lod0Again = GetMergeAffinityIdentity(
                @"variantmeshes\test.rigid_model_v2",
                0,
                "rmv",
                "material");
            var lod1 = GetMergeAffinityIdentity(
                @"variantmeshes\test.rigid_model_v2",
                1,
                "rmv",
                "material");

            Assert.Multiple(() =>
            {
                Assert.That(lod0, Is.EqualTo(lod0Again));
                Assert.That(lod0, Is.Not.EqualTo(lod1));
            });
        }

        [Test]
        public void GameplaySharedMesh_TaintedConsumerBlocksCandidate()
        {
            Assert.That(
                IsSharedMeshSafeForGameplay(
                    atlasAllVmdsEnabled: false,
                    ["eligible.wsmodel", "shared_tainted.wsmodel"],
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "shared_tainted.wsmodel",
                    }),
                Is.False);
        }

        [Test]
        public void GameplaySharedMesh_PackWideModeAllowsTaintedConsumer()
        {
            Assert.That(
                IsSharedMeshSafeForGameplay(
                    atlasAllVmdsEnabled: true,
                    ["eligible.wsmodel", "shared_tainted.wsmodel"],
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "shared_tainted.wsmodel",
                    }),
                Is.True);
        }

        [Test]
        public void GameplaySharedMesh_UnrelatedTaintDoesNotBlockCandidate()
        {
            Assert.That(
                IsSharedMeshSafeForGameplay(
                    atlasAllVmdsEnabled: false,
                    ["eligible.wsmodel"],
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "other_tainted.wsmodel",
                    }),
                Is.True);
        }

        [Test]
        public void ValueGate_ScenarioResolvedZeroBenefitRejectsPositiveBcnCost()
        {
            var decision = GetValueGateBudgetDecision(
                scenarioResolved: true,
                rawDrawsEliminated: 4,
                expectedArmyDrawsEliminated: 0,
                globalCostBytes: 1 * 1024 * 1024,
                expectedCostBytes: 0,
                acceptedNetBcnBytes: 0,
                proposedNetBcnBytes: 1 * 1024 * 1024,
                sourceBcnBytes: 400 * 1024 * 1024);

            Assert.That(decision, Is.EqualTo("ScenarioResolvedZeroBenefit"));
        }

        [Test]
        public void ValueGate_ScenarioResolvedZeroBenefitAllowsNonPositiveNetCost()
        {
            var decision = GetValueGateBudgetDecision(
                scenarioResolved: true,
                rawDrawsEliminated: 1,
                expectedArmyDrawsEliminated: 0,
                globalCostBytes: 0,
                expectedCostBytes: 0,
                acceptedNetBcnBytes: 0,
                proposedNetBcnBytes: 0,
                sourceBcnBytes: 400 * 1024 * 1024);

            Assert.That(decision, Is.EqualTo("Accept"));
        }

        [TestCase(false, false, false)]
        [TestCase(false, true, true)]
        [TestCase(true, false, true)]
        [TestCase(true, true, true)]
        public void ValueGate_GameplayModeRequiresScenarioResolution(
            bool atlasAllVmdsEnabled,
            bool scenarioResolved,
            bool expected)
        {
            Assert.That(
                IsCandidateAllowedForMode(
                    atlasAllVmdsEnabled,
                    scenarioResolved),
                Is.EqualTo(expected));
        }

        [Test]
        public void ValueGate_PackWideUnresolvedScenarioCanUseRawDrawFallback()
        {
            Assert.That(IsCandidateAllowedForMode(true, false), Is.True);

            var decision = GetValueGateBudgetDecision(
                scenarioResolved: false,
                rawDrawsEliminated: 1,
                expectedArmyDrawsEliminated: 0,
                globalCostBytes: 7 * 1024 * 1024,
                expectedCostBytes: 0,
                acceptedNetBcnBytes: 0,
                proposedNetBcnBytes: 7 * 1024 * 1024,
                sourceBcnBytes: 400 * 1024 * 1024);

            Assert.That(decision, Is.EqualTo("Accept"));
        }

        [Test]
        public void ValueGate_PackWideUnresolvedScenarioStillHonorsFallbackBudget()
        {
            Assert.That(IsCandidateAllowedForMode(true, false), Is.True);

            var decision = GetValueGateBudgetDecision(
                scenarioResolved: false,
                rawDrawsEliminated: 1,
                expectedArmyDrawsEliminated: 0,
                globalCostBytes: 9 * 1024 * 1024,
                expectedCostBytes: 0,
                acceptedNetBcnBytes: 0,
                proposedNetBcnBytes: 9 * 1024 * 1024,
                sourceBcnBytes: 400 * 1024 * 1024);

            Assert.That(decision, Is.EqualTo("FallbackBudgetExceeded"));
        }

        [Test]
        public void ValueGate_ScenarioResolvedBenefitUsesScenarioBudget()
        {
            var decision = GetValueGateBudgetDecision(
                scenarioResolved: true,
                rawDrawsEliminated: 10,
                expectedArmyDrawsEliminated: 1,
                globalCostBytes: 2 * 1024 * 1024,
                expectedCostBytes: 300 * 1024,
                acceptedNetBcnBytes: 0,
                proposedNetBcnBytes: 2 * 1024 * 1024,
                sourceBcnBytes: 400 * 1024 * 1024);

            Assert.That(decision, Is.EqualTo("ScenarioBudgetExceeded"));
        }

        [Test]
        public void ValueGate_MixedBatchCannotHideBadMarginalGroup()
        {
            const double mib = 1024.0 * 1024.0;

            // The broad batch is just under the 0.25 MiB/scenario-draw budget:
            // 1.0 MiB / 4.2 draws ~= 0.238 MiB/draw.
            var broadDecision = GetValueGateBudgetDecision(
                scenarioResolved: true,
                rawDrawsEliminated: 6,
                expectedArmyDrawsEliminated: 4.2,
                globalCostBytes: 1.0 * mib,
                expectedCostBytes: 1.0 * mib,
                acceptedNetBcnBytes: 0,
                proposedNetBcnBytes: 1.0 * mib,
                sourceBcnBytes: 400 * mib);

            // But the second group contributes 0.5 MiB for only 0.2 additional
            // expected draws: 2.5 MiB/draw. It must not be subsidized by the
            // first group's strong economics.
            var marginalDecision = GetMarginalValueGateBudgetDecision(
                scenarioResolved: true,
                baseRawDrawsEliminated: 4,
                combinedRawDrawsEliminated: 6,
                baseExpectedArmyDrawsEliminated: 4.0,
                combinedExpectedArmyDrawsEliminated: 4.2,
                baseGeneratedBcnBytes: 0.5 * mib,
                baseRetiredSourceBcnBytes: 0,
                combinedGeneratedBcnBytes: 1.0 * mib,
                combinedRetiredSourceBcnBytes: 0,
                baseExpectedGeneratedBcnBytes: 0.5 * mib,
                baseExpectedRetiredSourceBcnBytes: 0,
                combinedExpectedGeneratedBcnBytes: 1.0 * mib,
                combinedExpectedRetiredSourceBcnBytes: 0,
                acceptedNetBcnBytes: 0,
                sourceBcnBytes: 400 * mib);

            Assert.Multiple(() =>
            {
                Assert.That(broadDecision, Is.EqualTo("Accept"));
                Assert.That(
                    marginalDecision,
                    Is.EqualTo("ScenarioBudgetExceeded"));
            });
        }

        [Test]
        public void ValueGate_ZeroCostMarginalGroupCanShareAcceptedAtlas()
        {
            const double mib = 1024.0 * 1024.0;

            var decision = GetMarginalValueGateBudgetDecision(
                scenarioResolved: true,
                baseRawDrawsEliminated: 4,
                combinedRawDrawsEliminated: 5,
                baseExpectedArmyDrawsEliminated: 4.0,
                combinedExpectedArmyDrawsEliminated: 4.0,
                baseGeneratedBcnBytes: 0.5 * mib,
                baseRetiredSourceBcnBytes: 0,
                combinedGeneratedBcnBytes: 0.5 * mib,
                combinedRetiredSourceBcnBytes: 0,
                baseExpectedGeneratedBcnBytes: 0.5 * mib,
                baseExpectedRetiredSourceBcnBytes: 0,
                combinedExpectedGeneratedBcnBytes: 0.5 * mib,
                combinedExpectedRetiredSourceBcnBytes: 0,
                acceptedNetBcnBytes: 0,
                sourceBcnBytes: 400 * mib);

            Assert.That(decision, Is.EqualTo("Accept"));
        }

        [Test]
        public void ValueGate_RejectsWhenCumulativeBcnGrowthExceedsGlobalCap()
        {
            var decision = GetValueGateBudgetDecision(
                scenarioResolved: false,
                rawDrawsEliminated: 100,
                expectedArmyDrawsEliminated: 0,
                globalCostBytes: 10 * 1024 * 1024,
                expectedCostBytes: 0,
                acceptedNetBcnBytes: 195 * 1024 * 1024,
                proposedNetBcnBytes: 10 * 1024 * 1024,
                sourceBcnBytes: 400 * 1024 * 1024);

            Assert.That(decision, Is.EqualTo("GlobalGrowthCapExceeded"));
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
                Assert.That(
                    GetDoubleConstant("MaxReachableBcnGrowthRatio"),
                    Is.EqualTo(0.50).Within(0.000001));
            });
        }


        private static IReadOnlyList<string> SelectAtlasVmdRoots(
            string[] validatedRoots,
            string[] gameplayUsedRoots,
            bool atlasAllVmds)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "SelectAtlasVmdRoots",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "PackTextureAtlasBatchService.SelectAtlasVmdRoots was not found.");

            return (IReadOnlyList<string>)(method.Invoke(
                null,
                [validatedRoots, gameplayUsedRoots, atlasAllVmds])
                ?? throw new InvalidOperationException(
                    "SelectAtlasVmdRoots returned null."));
        }

        [Test]
        public void AtlasVmdPopulation_DefaultsToGameplayUsedRoots()
        {
            var selected = SelectAtlasVmdRoots(
                [
                    @"variantmeshes\variantmeshdefinitions\used.variantmeshdefinition",
                    @"variantmeshes\variantmeshdefinitions\unused.variantmeshdefinition",
                    @"variantmeshes\variantmeshdefinitions\child.variantmeshdefinition",
                ],
                [
                    @"VARIANTMESHES/VARIANTMESHDEFINITIONS/USED.VARIANTMESHDEFINITION",
                    @"variantmeshes\variantmeshdefinitions\child.variantmeshdefinition",
                ],
                atlasAllVmds: false);

            Assert.That(
                selected,
                Is.EqualTo(new[]
                {
                    @"variantmeshes\variantmeshdefinitions\used.variantmeshdefinition",
                    @"variantmeshes\variantmeshdefinitions\child.variantmeshdefinition",
                }));
        }

        [Test]
        public void AtlasVmdPopulation_PackWideModeKeepsAllValidatedRoots()
        {
            var roots = new[]
            {
                @"variantmeshes\variantmeshdefinitions\used.variantmeshdefinition",
                @"variantmeshes\variantmeshdefinitions\unused.variantmeshdefinition",
            };

            Assert.That(
                SelectAtlasVmdRoots(roots, Array.Empty<string>(), atlasAllVmds: true),
                Is.EqualTo(roots));
        }


    }
}
