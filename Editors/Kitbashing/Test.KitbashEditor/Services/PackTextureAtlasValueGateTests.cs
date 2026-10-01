using System.Reflection;
using System.Xml;
using Moq;
using Shared.Core.PackFiles.Models;

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
            bool isOwnedBySourcePack,
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
                [isOwnedBySourcePack, hasDirectVmdReference, referenceCount, rewrittenReferenceCount])
                ?? throw new InvalidOperationException(
                    "IsAtlasValueGateSourceTextureRetired returned null."));
        }


        private static double CombineBattleResidentProbabilities(
            IReadOnlyDictionary<string, double> probabilitiesByCulture,
            IReadOnlyDictionary<string, double> playerCultureWeights,
            IReadOnlyDictionary<string, double> opponentCultureWeights)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "CombineBattleResidentProbabilities",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "PackTextureAtlasBatchService.CombineBattleResidentProbabilities was not found.");

            return (double)(method.Invoke(
                null,
                [probabilitiesByCulture, playerCultureWeights, opponentCultureWeights])
                ?? throw new InvalidOperationException(
                    "CombineBattleResidentProbabilities returned null."));
        }

        private static bool HasSufficientTextureOnlyRetirement(
            int physicallyRetiredTextureCount,
            long physicallyRetiredBcnBytes,
            int scenarioDisplacedTextureCount,
            double expectedRetiredTextureEquivalents,
            double expectedRetiredBcnBytes)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "HasSufficientTextureOnlyRetirement",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "PackTextureAtlasBatchService.HasSufficientTextureOnlyRetirement was not found.");

            return (bool)(method.Invoke(
                null,
                [
                    physicallyRetiredTextureCount,
                    physicallyRetiredBcnBytes,
                    scenarioDisplacedTextureCount,
                    expectedRetiredTextureEquivalents,
                    expectedRetiredBcnBytes,
                ])
                ?? throw new InvalidOperationException(
                    "HasSufficientTextureOnlyRetirement returned null."));
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

            return Convert.ToInt64(field.GetRawConstantValue()
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

        private static double GetAffectedUnitsPerScenarioMiB(
            int affectedUnitCount,
            double expectedNetBcnBytes)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "GetAffectedUnitsPerScenarioMiB",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "PackTextureAtlasBatchService.GetAffectedUnitsPerScenarioMiB was not found.");

            return (double)(method.Invoke(null, [affectedUnitCount, expectedNetBcnBytes])
                ?? throw new InvalidOperationException(
                    "GetAffectedUnitsPerScenarioMiB returned null."));
        }

        private static string FormatAffectedUnitsPerScenarioMiB(
            int affectedUnitCount,
            double expectedNetBcnBytes)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "FormatAffectedUnitsPerScenarioMiB",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "PackTextureAtlasBatchService.FormatAffectedUnitsPerScenarioMiB was not found.");

            return (string)(method.Invoke(null, [affectedUnitCount, expectedNetBcnBytes])
                ?? throw new InvalidOperationException(
                    "FormatAffectedUnitsPerScenarioMiB returned null."));
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

        private static string GetMaterialRenderingIdentity(string materialXml)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "GetMaterialRenderingIdentity",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "PackTextureAtlasBatchService.GetMaterialRenderingIdentity was not found.");

            var material = new XmlDocument();
            material.LoadXml(materialXml);
            return (string)(method.Invoke(null, [material])
                ?? throw new InvalidOperationException(
                    "GetMaterialRenderingIdentity returned null."));
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

        private static string GetTextureOnlyMergeValueGateBudgetDecision(
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
                "EvaluateTextureOnlyMergeAtlasValueGateBudget",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "PackTextureAtlasBatchService.EvaluateTextureOnlyMergeAtlasValueGateBudget was not found.");

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
                       "EvaluateTextureOnlyMergeAtlasValueGateBudget returned null.");
        }

        private static IPackFileContainer? FindGameplayTraversalContainer(
            IPackFileContainer source,
            IReadOnlyList<IPackFileContainer> loadedContainers,
            string path)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "FindGameplayTraversalContainer",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                [typeof(IPackFileContainer), typeof(IReadOnlyList<IPackFileContainer>), typeof(string)],
                null)
                ?? throw new InvalidOperationException(
                    "PackTextureAtlasBatchService.FindGameplayTraversalContainer was not found.");

            return (IPackFileContainer?)method.Invoke(
                null,
                [source, loadedContainers, path]);
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
        [TestCase(true, false, 2, 2, true)]
        [TestCase(true, false, 2, 1, false)]
        [TestCase(true, false, 0, 0, false)]
        [TestCase(true, true, 2, 2, false)]
        [TestCase(false, false, 2, 2, false)]
        public void SourceTextureRetirement_RequiresPackOwnershipCompleteRewriteCoverageAndNoDirectVmdReference(
            bool isOwnedBySourcePack,
            bool hasDirectVmdReference,
            int referenceCount,
            int rewrittenReferenceCount,
            bool expected)
        {
            Assert.That(
                IsSourceTextureRetired(
                    isOwnedBySourcePack,
                    hasDirectVmdReference,
                    referenceCount,
                    rewrittenReferenceCount),
                Is.EqualTo(expected));
        }

        [Test]
        public void CultureResidency_UnrelatedOpponentOnlyAddsMirrorChance()
        {
            var probabilities = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["empire"] = 0.5,
                ["dwarfs"] = 0.0,
                ["cathay"] = 0.0,
            };
            var playerWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["empire"] = 1.0,
            };
            var opponentWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["empire"] = 1.0 / 3.0,
                ["dwarfs"] = 1.0 / 3.0,
                ["cathay"] = 1.0 / 3.0,
            };

            Assert.That(
                CombineBattleResidentProbabilities(
                    probabilities,
                    playerWeights,
                    opponentWeights),
                Is.EqualTo(7.0 / 12.0).Within(0.000001));
        }

        [Test]
        public void CultureResidency_GenuineCrossCultureSharingContributesWithoutPairMatrix()
        {
            var probabilities = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["empire"] = 0.5,
                ["dwarfs"] = 0.25,
                ["cathay"] = 0.0,
            };
            var playerWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["empire"] = 1.0,
            };
            var opponentWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["empire"] = 1.0 / 3.0,
                ["dwarfs"] = 1.0 / 3.0,
                ["cathay"] = 1.0 / 3.0,
            };

            Assert.That(
                CombineBattleResidentProbabilities(
                    probabilities,
                    playerWeights,
                    opponentWeights),
                Is.EqualTo(0.625).Within(0.000001));
        }

        [Test]
        public void CultureResidency_SameCultureOpponentUsesUnionNotDoubleCounting()
        {
            var probabilities = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["empire"] = 0.5,
            };
            var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["empire"] = 1.0,
            };

            Assert.That(
                CombineBattleResidentProbabilities(probabilities, weights, weights),
                Is.EqualTo(0.75).Within(0.000001));
        }

        [TestCase(2, 1024L, 0, 0.0, 0.0, true)]
        [TestCase(0, 0L, 2, 1.0, 1024.0, true)]
        [TestCase(0, 0L, 2, 0.99, 1024.0, false)]
        [TestCase(0, 0L, 1, 1.0, 1024.0, false)]
        public void TextureOnlyFallback_AllowsScenarioDisplacementWithoutPhysicalRetirement(
            int physicallyRetiredTextureCount,
            long physicallyRetiredBcnBytes,
            int scenarioDisplacedTextureCount,
            double expectedRetiredTextureEquivalents,
            double expectedRetiredBcnBytes,
            bool expected)
        {
            Assert.That(
                HasSufficientTextureOnlyRetirement(
                    physicallyRetiredTextureCount,
                    physicallyRetiredBcnBytes,
                    scenarioDisplacedTextureCount,
                    expectedRetiredTextureEquivalents,
                    expectedRetiredBcnBytes),
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
        public void GameplayTraversal_UsesCaVmdWhenRootIsNotInSourcePack()
        {
            const string path =
                @"variantmeshes\variantmeshdefinitions\vanilla.variantmeshdefinition";
            var source = new Mock<IPackFileContainer>();
            var ca = new Mock<IPackFileContainer>();
            var unrelatedMod = new Mock<IPackFileContainer>();

            source.Setup(container => container.ContainsFile(path)).Returns(false);
            source.SetupGet(container => container.IsCaPackFile).Returns(false);

            ca.Setup(container => container.ContainsFile(path)).Returns(true);
            ca.SetupGet(container => container.IsCaPackFile).Returns(true);

            // Loaded later than CA: this must never win gameplay traversal.
            unrelatedMod.Setup(container => container.ContainsFile(path)).Returns(true);
            unrelatedMod.SetupGet(container => container.IsCaPackFile).Returns(false);

            var selected = FindGameplayTraversalContainer(
                source.Object,
                [ca.Object, unrelatedMod.Object],
                path);

            Assert.That(selected, Is.SameAs(ca.Object));
        }

        [Test]
        public void GameplayTraversal_SourceOverrideWinsOverCaFile()
        {
            const string path =
                @"variantmeshes\variantmeshdefinitions\shared.variantmeshdefinition";
            var source = new Mock<IPackFileContainer>();
            var ca = new Mock<IPackFileContainer>();

            source.Setup(container => container.ContainsFile(path)).Returns(true);
            source.SetupGet(container => container.IsCaPackFile).Returns(false);
            ca.Setup(container => container.ContainsFile(path)).Returns(true);
            ca.SetupGet(container => container.IsCaPackFile).Returns(true);

            var selected = FindGameplayTraversalContainer(
                source.Object,
                [ca.Object],
                path);

            Assert.That(selected, Is.SameAs(source.Object));
        }

        [Test]
        public void GameplayTraversal_UsesLastLoadedCaOverride()
        {
            const string path =
                @"variantmeshes\variantmeshdefinitions\nested.variantmeshdefinition";
            var source = new Mock<IPackFileContainer>();
            var olderCa = new Mock<IPackFileContainer>();
            var newerCa = new Mock<IPackFileContainer>();

            source.Setup(container => container.ContainsFile(path)).Returns(false);
            olderCa.Setup(container => container.ContainsFile(path)).Returns(true);
            olderCa.SetupGet(container => container.IsCaPackFile).Returns(true);
            newerCa.Setup(container => container.ContainsFile(path)).Returns(true);
            newerCa.SetupGet(container => container.IsCaPackFile).Returns(true);

            var selected = FindGameplayTraversalContainer(
                source.Object,
                [olderCa.Object, newerCa.Object],
                path);

            Assert.That(selected, Is.SameAs(newerCa.Object));
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
        public void ValueGate_TextureOnlyMergeCreditUsesStructuralOrScenarioBudget()
        {
            const double mib = 1024.0 * 1024.0;

            var decision = GetTextureOnlyMergeValueGateBudgetDecision(
                scenarioResolved: true,
                rawDrawsEliminated: 1,
                expectedArmyDrawsEliminated: 0.15,
                globalCostBytes: 1.75 * mib,
                expectedCostBytes: 0.01 * mib,
                acceptedNetBcnBytes: 0,
                proposedNetBcnBytes: 1.75 * mib,
                sourceBcnBytes: 400 * mib);

            Assert.That(decision, Is.EqualTo("Accept"));
        }

        [Test]
        public void ValueGate_TextureOnlyMergeCreditExpandsForHighScenarioCoverage()
        {
            const double mib = 1024.0 * 1024.0;

            var decision = GetTextureOnlyMergeValueGateBudgetDecision(
                scenarioResolved: true,
                rawDrawsEliminated: 1,
                expectedArmyDrawsEliminated: 100,
                globalCostBytes: 8 * mib,
                expectedCostBytes: 1 * mib,
                acceptedNetBcnBytes: 0,
                proposedNetBcnBytes: 8 * mib,
                sourceBcnBytes: 400 * mib);

            Assert.That(decision, Is.EqualTo("Accept"));
        }

        [Test]
        public void ValueGate_TextureOnlyMergeCreditRejectsExcessiveGrowth()
        {
            const double mib = 1024.0 * 1024.0;

            var decision = GetTextureOnlyMergeValueGateBudgetDecision(
                scenarioResolved: true,
                rawDrawsEliminated: 1,
                expectedArmyDrawsEliminated: 0.15,
                globalCostBytes: 2.1 * mib,
                expectedCostBytes: 2.1 * mib,
                acceptedNetBcnBytes: 0,
                proposedNetBcnBytes: 2.1 * mib,
                sourceBcnBytes: 400 * mib);

            Assert.That(decision, Is.EqualTo("TextureOnlyMergeBudgetExceeded"));
        }

        [Test]
        public void ValueGate_TextureOnlyMergeCreditRequiresScenarioBenefit()
        {
            const double mib = 1024.0 * 1024.0;

            var decision = GetTextureOnlyMergeValueGateBudgetDecision(
                scenarioResolved: true,
                rawDrawsEliminated: 1,
                expectedArmyDrawsEliminated: 0,
                globalCostBytes: 1 * mib,
                expectedCostBytes: 1 * mib,
                acceptedNetBcnBytes: 0,
                proposedNetBcnBytes: 1 * mib,
                sourceBcnBytes: 400 * mib);

            Assert.That(decision, Is.EqualTo("ScenarioResolvedZeroBenefit"));
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
                    GetConstant("MaxNetBcnBytesPerRetiredSourceTexture"),
                    Is.EqualTo(512L * 1024));
                Assert.That(
                    GetConstant("MaxNetBcnBytesPerTextureOnlyMergeDraw"),
                    Is.EqualTo(2L * 1024 * 1024));
                Assert.That(
                    GetConstant("MinimumRetiredSourceTexturesForTextureOnlyAtlas"),
                    Is.EqualTo(2));
                Assert.That(
                    GetConstant("MaxNetBcnBytesPerConsolidatedTextureAssignment"),
                    Is.EqualTo(256L * 1024));
                Assert.That(
                    GetConstant("MinimumConsolidatedTextureAssignmentsForAtlas"),
                    Is.EqualTo(2));
                Assert.That(
                    GetConstant("TextureConsolidationInitialCohortCandidateLimit"),
                    Is.EqualTo(8));
                Assert.That(
                    GetDoubleConstant("MaxReachableBcnGrowthRatio"),
                    Is.EqualTo(0.50).Within(0.000001));
            });
        }

        [Test]
        public void MaterialRenderingIdentity_NormalizesEquivalentResourcePathSpelling()
        {
            const string lowerSlashMaterial =
                "<material><name>first</name>" +
                "<shader>shaders/weighted4_character.xml.shader</shader>" +
                "<textures>" +
                "<texture><slot version='2'>t_xml_base_colour</slot>" +
                "<source>VariantMeshes/Foo.dds</source></texture>" +
                "<texture><slot version='2'>t_xml_mask</slot>" +
                "<source>MASK_PATH</source></texture>" +
                "</textures></material>";
            const string upperBackslashMaterial =
                "<material><name>second</name>" +
                "<shader>SHADERS\\WEIGHTED4_CHARACTER.XML.SHADER</shader>" +
                "<textures>" +
                "<texture><slot version='2'>t_xml_base_colour</slot>" +
                "<source>variantmeshes\\foo.dds</source></texture>" +
                "<texture><slot version='2'>t_xml_mask</slot>" +
                "<source>mask_path</source></texture>" +
                "</textures></material>";

            Assert.That(
                GetMaterialRenderingIdentity(lowerSlashMaterial),
                Is.EqualTo(GetMaterialRenderingIdentity(upperBackslashMaterial)));
        }

        [Test]
        public void CoverageSelection_PrefersMoreAffectedUnitsPerScenarioMiB()
        {
            const double mib = 1024.0 * 1024.0;

            var broadEfficient = GetAffectedUnitsPerScenarioMiB(
                affectedUnitCount: 6,
                expectedNetBcnBytes: 2 * mib);
            var narrowExpensive = GetAffectedUnitsPerScenarioMiB(
                affectedUnitCount: 1,
                expectedNetBcnBytes: 2 * mib);

            Assert.Multiple(() =>
            {
                Assert.That(broadEfficient, Is.EqualTo(3).Within(0.000001));
                Assert.That(broadEfficient, Is.GreaterThan(narrowExpensive));
                Assert.That(
                    GetAffectedUnitsPerScenarioMiB(0, 0),
                    Is.EqualTo(0));
                Assert.That(
                    FormatAffectedUnitsPerScenarioMiB(4, -0.25 * mib),
                    Is.EqualTo("retiring"));
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
