using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Editors.ImportExport.Importing.Importers.PngToDds;
using Editors.ImportExport.Importing.Importers.PngToDds.Helpers;
using Editors.ImportExport.TextureAtlas;

using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Utility;
using Shared.Core.Services;
using Shared.Core.Settings;
using Shared.GameFormats;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.LodHeader;
using Shared.GameFormats.RigidModel.MaterialHeaders;
using Shared.GameFormats.RigidModel.Types;
using Shared.GameFormats.RigidModel.Vertex;
using Shared.GameFormats.Vmd;
using static Shared.GameFormats.Vmd.VariantMeshDefinition;

namespace Editors.PackOptimizer.Core
{
    public sealed partial class PackTextureAtlasBatchService
    {
        public sealed record BatchResult(
            int VmdCount,
            int AtlasedMeshCount,
            int GeneratedTextureCount,
            int RemovedFileCount,
            int SkippedMeshCount,
            string ReportPath);

        private sealed record GeneratedTextureTimingEntry(
            string Path,
            int Width,
            int Height,
            TextureType TextureType,
            Editors.ImportExport.Dds.Native.DXGI_FORMAT Format,
            TimeSpan RasterElapsed,
            TimeSpan CompressionElapsed,
            bool UsedLargeBcSplitCompression,
            TextureAtlasBuildStatistics RasterStatistics,
            RawBgraMipChainCompressionStatistics? CompressionStatistics)
        {
            public TimeSpan TotalElapsed => RasterElapsed + CompressionElapsed;
        }

        private sealed record AtlasPlacementBatchDiagnostic(
            string AtlasStem,
            int PlanWidth,
            int PlanHeight,
            string[] GeneratedTexturePaths,
            List<AtlasPlacementDiagnostic> Placements);

        private sealed record AtlasPlacementDiagnostic(
            int SourceId,
            int SourceWidth,
            int SourceHeight,
            AtlasCrop Crop,
            int DestinationX,
            int DestinationY,
            int DestinationWidth,
            int DestinationHeight,
            int Padding,
            MeshKey RepresentativeMesh,
            string MaterialPath,
            string BaseColourPath,
            string MaterialMapPath,
            string NormalPath,
            string MaskPath,
            List<int> SameBaseColourRegionSourceIds,
            List<int> BaseColourContainsSourceIds,
            List<int> BaseColourContainedBySourceIds,
            List<int> FullTextureSetContainsSourceIds,
            List<int> FullTextureSetContainedBySourceIds,
            List<AtlasPlacementMeshDiagnostic> MeshMappings);

        private sealed record AtlasPlacementMeshDiagnostic(
            MeshKey Mesh,
            string RootVmdPath,
            float UvOffsetU,
            float UvOffsetV,
            bool UvIslandCanonicalized,
            bool WrappedUvCanonicalized,
            bool ContentCanonicalized);

        private sealed record AtlasPhysicalPageDiagnostic(
            List<AtlasCandidate> Candidates,
            long PixelCost);

        private sealed record BcnTextureResidencySummary(
            int ReachableDdsCount,
            int BcnTextureCount,
            int UnsupportedDdsCount,
            int ArmyUnmappedBcnTextureCount,
            long BcnBytes,
            double EstimatedScenarioResidentBcnBytes,
            long GeneratedAtlasBcnBytes,
            double EstimatedScenarioResidentGeneratedAtlasBcnBytes,
            IReadOnlyDictionary<string, int> TextureCountByFormat,
            IReadOnlyDictionary<string, long> BytesByFormat);

        private sealed record ExpectedWsModelOccurrences(
            IReadOnlyDictionary<
                string,
                IReadOnlyDictionary<Wh3UnitVisualRole, double>>
                ByWsModel);

        private sealed record UnitVisualConfiguration(
            double Probability,
            IReadOnlyDictionary<string, int> WsModelOccurrences);

        private sealed record CrossRigidVmdReferenceHop(
            string OwnerVmdPath,
            string ReferenceXmlPath,
            string ReferencedVmdPath);

        private sealed record CrossRigidVisualInstance(
            string WsModelPath,
            string AttachmentIdentity,
            string DefiningVmdPath,
            string ModelXmlPath,
            string ActivationSignature,
            CrossRigidVmdReferenceHop[] ReferenceChain,
            string ImposterModelPath,
            bool HasDecalDiffuse,
            bool HasDecalNormal,
            bool HasMetadata)
        {
            public bool HasRewriteBlockingModifiers =>
                !string.IsNullOrWhiteSpace(ImposterModelPath) ||
                HasDecalDiffuse ||
                HasDecalNormal;

            public bool HasLocalModelModifiers =>
                HasRewriteBlockingModifiers ||
                HasMetadata;
        }

        private sealed record CrossRigidVisualOccurrenceSet(
            double Probability,
            CrossRigidVisualInstance[] Instances);

        private sealed record CrossRigidVisualConfiguration(
            double Probability,
            string AttachmentIdentity,
            IReadOnlyList<CrossRigidVisualInstance> Instances,
            IReadOnlyList<CrossRigidVisualOccurrenceSet> OccurrenceSets);

        private sealed record CrossRigidAttachmentLocalExpansionResult(
            IReadOnlyDictionary<
                string,
                IReadOnlyList<CrossRigidVisualConfiguration>>
                ConfigurationsByAttachment,
            string? FailureReason)
        {
            public static CrossRigidAttachmentLocalExpansionResult Failed(
                string reason)
                => new(
                    new Dictionary<
                        string,
                        IReadOnlyList<CrossRigidVisualConfiguration>>(
                        StringComparer.Ordinal),
                    reason);
        }

        private sealed record CrossRigidAnalysisComponent(
            string VmdPath,
            string WsModelPath,
            string RigidPath,
            XmlDocument WsDocument,
            RmvFile Rigid,
            string[][] MaterialAssignments,
            string TopologyIdentity,
            bool SourceOwnedWsModel,
            bool SourceOwnedRigid);

        private sealed record CrossRigidAnalysisInstanceComponent(
            CrossRigidVisualInstance Instance,
            CrossRigidAnalysisComponent Component);

        private sealed record CrossRigidAnalysisMeshPart(
            string InstanceKey,
            string WsModelPath,
            string RigidPath,
            int VertexCount);

        private sealed record CrossRigidSourceLodSelection(
            string WsModelPath,
            int SourceLodIndex);

        private sealed record CrossRigidGeneratedLodScheduleEntry(
            float CameraDistance,
            CrossRigidSourceLodSelection[] SourceLodSelections);

        private sealed record CrossRigidLodBoundaryCandidate(
            float Distance,
            double Weight);

        private sealed record CrossRigidLodAnalysis(
            int LodIndex,
            int ScenarioLodIndex,
            byte QualityLevel,
            float CameraDistance,
            CrossRigidSourceLodSelection[] SourceLodSelections,
            bool IsScenarioRepresentativeState,
            int ActiveComponentCount,
            int ActiveRigidCount,
            int InputDrawsAfterSameRigidMerge,
            int OutputDrawsAfterCrossRigidMerge,
            int DrawsSaved,
            double LodProbability,
            long GeneratedVertexCount,
            long GeneratedGeometryBytes,
            string OutputLayoutSignature);

        private sealed record CrossRigidRewriteOccurrenceSet(
            double Probability,
            CrossRigidVisualInstance[] SourceInstances);

        private sealed record CrossRigidActivationTokenSelection(
            string OwnerVmdPath,
            string ParentXmlPath,
            int SlotIndex,
            bool IsReference,
            int AlternativeIndex,
            bool OptionalSlot);

        private sealed record CrossRigidJointSourceSelection(
            CrossRigidVisualInstance Instance,
            string SelectionOwnerVmdPath,
            string SelectionParentXmlPath,
            int SlotIndex,
            bool IsReference,
            int AlternativeIndex,
            int AnchorReferenceHopIndex,
            string? DirectModelRelativeXmlPath,
            string? DirectFirstReferenceRelativeXmlPath);

        private sealed record CrossRigidJointSourceSelectionCandidate(
            int InstanceIndex,
            string AnchorVmdPath,
            string ParentXmlPath,
            CrossRigidVmdReferenceHop[] AnchorReferenceChain,
            CrossRigidJointSourceSelection SourceSelection);

        private sealed record CrossRigidJointAlwaysPresentRewriteDescriptor(
            string AnchorVmdPath,
            string ParentXmlPath,
            CrossRigidVmdReferenceHop[] AnchorReferenceChain,
            CrossRigidJointSourceSelection[] SourceSelections,
            int[] SlotIndices,
            int CombinationCount,
            CrossRigidJointSelectionDimension[] CommonAncestorDimensions);

        private sealed record CrossRigidJointSelectionDimension(
            string DimensionKey,
            string OwnerVmdPath,
            string ParentXmlPath,
            int SlotIndex,
            int CarrierSlotIndex,
            string? LocalDimensionSlotRelativeXmlPath,
            CrossRigidVmdReferenceHop[] ReferenceChainFromAnchor,
            CrossRigidJointDimensionAlternative[] Alternatives,
            string? ParentDimensionKey = null,
            bool ParentIsReference = false,
            int ParentAlternativeIndex = -1);

        private sealed record CrossRigidJointDimensionAlternative(
            string DimensionKey,
            bool IsReference,
            int AlternativeIndex,
            int Multiplicity = 1);

        private sealed record CrossRigidJointAlternative(
            int SlotIndex,
            bool IsReference,
            int AlternativeIndex,
            int Multiplicity = 1);

        private sealed record CrossRigidInexactSlotNesting(
            string VmdPath,
            int AlternativeCount,
            double MaxRelativeProbabilityError);

        private sealed record CrossRigidOverLimitSlot(
            string VmdPath,
            string SlotName,
            int AlternativeCount);

        private sealed record CrossRigidJointSlotDefinition(
            int SlotIndex,
            XmlElement SlotElement,
            CrossRigidJointAlternative[] Alternatives);

        private sealed class CrossRigidJointCombinationRewriteState
        {
            public CrossRigidJointCombinationRewriteState(
                Dictionary<int, XmlElement> selectedElementsBySlotIndex,
                Dictionary<string, XmlElement>?
                    selectedElementsByDimensionKey = null,
                XmlElement? wrapperElement = null,
                int probabilityMultiplicity = 1)
            {
                SelectedElementsBySlotIndex =
                    selectedElementsBySlotIndex;
                SelectedElementsByDimensionKey =
                    selectedElementsByDimensionKey ??
                    new Dictionary<string, XmlElement>(
                        StringComparer.Ordinal);
                WrapperElement =
                    wrapperElement ??
                    throw new ArgumentNullException(
                        nameof(wrapperElement));
                ProbabilityMultiplicity =
                    Math.Max(
                        1,
                        probabilityMultiplicity);
            }

            public XmlElement WrapperElement { get; }
            public int ProbabilityMultiplicity { get; }
            public bool ProbabilityMultiplicityFinalized { get; set; }

            public Dictionary<int, XmlElement>
                SelectedElementsBySlotIndex { get; }
            public Dictionary<string, XmlElement>
                SelectedElementsByDimensionKey { get; }
            public Dictionary<string, string>
                GeneratedVmdPathByBranchKey { get; } =
                    new(StringComparer.Ordinal);
            public HashSet<string> ConsumedOccurrenceKeys { get; } =
                new(StringComparer.Ordinal);
        }

        private sealed class CrossRigidJointRewriteContext
        {
            public CrossRigidJointRewriteContext(
                string contextKey,
                string parentLocationKey,
                Dictionary<
                    string,
                    CrossRigidJointCombinationRewriteState>
                    combinations)
            {
                ContextKey = contextKey;
                ParentLocationKey = parentLocationKey;
                Combinations = combinations;
            }

            public string ContextKey { get; }
            public string ParentLocationKey { get; }
            public Dictionary<
                string,
                CrossRigidJointCombinationRewriteState>
                Combinations { get; }
        }

        private sealed record CrossRigidSourceGeometryComponent(
            string WsModelPath,
            string RigidPath,
            long VertexCount,
            long GeometryBytes);

        private sealed record CrossRigidGeneratedSourcePart(
            int InstanceIndex,
            string WsModelPath,
            string RigidPath,
            int PartIndex,
            RmvModel Model,
            string MaterialPath,
            string MergeIdentity);

        private sealed record CrossRigidWsModelOccurrenceKey(
            string VmdPath,
            string AttachmentIdentity,
            string WsModelPath);

        private sealed record CrossRigidDisplacedSourceGeometryEntry(
            string RigidPath,
            long GeometryBytes,
            double ExpectedBattleResidentProbability,
            double ExpectedResidentGeometryBytes);

        private sealed class CrossRigidMergePlanAccumulator
        {
            public CrossRigidMergePlanAccumulator(
                string rewritePlanId,
                string generatedPayloadId,
                string generatedPayloadKey,
                string vmdPath,
                string attachmentIdentity,
                CrossRigidVisualInstance[] sourceInstances,
                CrossRigidSourceGeometryComponent[] sourceGeometry,
                string[] rigidPaths,
                CrossRigidLodAnalysis[] lods,
                List<CrossRigidRewriteOccurrenceSet> rewriteOccurrenceSets,
                double aggregatedConfigurationProbability,
                int contributingConfigurationCount)
            {
                RewritePlanId = rewritePlanId;
                GeneratedPayloadId = generatedPayloadId;
                GeneratedPayloadKey = generatedPayloadKey;
                VmdPath = vmdPath;
                AttachmentIdentity = attachmentIdentity;
                SourceInstances = sourceInstances;
                SourceGeometry = sourceGeometry;
                RigidPaths = rigidPaths;
                Lods = lods;
                RewriteOccurrenceSets = rewriteOccurrenceSets;
                AggregatedConfigurationProbability =
                    aggregatedConfigurationProbability;
                ContributingConfigurationCount =
                    contributingConfigurationCount;
            }

            public string RewritePlanId { get; }
            public string GeneratedPayloadId { get; }
            public string GeneratedPayloadKey { get; }
            public string VmdPath { get; }
            public string AttachmentIdentity { get; }
            public CrossRigidVisualInstance[] SourceInstances { get; }
            public CrossRigidSourceGeometryComponent[] SourceGeometry { get; }
            public string[] RigidPaths { get; }
            public CrossRigidLodAnalysis[] Lods { get; }
            public List<CrossRigidRewriteOccurrenceSet> RewriteOccurrenceSets { get; }
            public double AggregatedConfigurationProbability { get; set; }
            public int ContributingConfigurationCount { get; set; }
        }

        private sealed record CrossRigidMergeAnalysisEntry(
            string RewritePlanId,
            string GeneratedPayloadId,
            string VmdPath,
            double AggregatedConfigurationProbability,
            int ContributingConfigurationCount,
            string AttachmentIdentity,
            CrossRigidVisualInstance[] SourceInstances,
            CrossRigidRewriteOccurrenceSet[] RewriteOccurrenceSets,
            CrossRigidSourceGeometryComponent[] SourceGeometry,
            string[] RigidPaths,
            CrossRigidLodAnalysis[] Lods,
            double ExpectedArmyDrawCallsEliminated);

        private sealed record CrossRigidGeneratedPayloadAnalysisEntry(
            string GeneratedPayloadId,
            int RewritePlanCount,
            int VmdCount,
            string[] SourceWsModelPaths,
            string[] RigidPaths,
            CrossRigidLodAnalysis[] Lods,
            long GeneratedVertexCount,
            long GeneratedGeometryBytes,
            long PotentiallyDuplicatedGeometryBytes,
            double ExpectedBattleResidentProbability,
            double ExpectedResidentGeneratedGeometryBytes,
            double ExpectedResidentDisplacedSourceGeometryBytes,
            string[] ExternalStructuralConsumers,
            bool ConsumerDiscoveryComplete);

        private sealed record CrossRigidJointMatchingCombination(
            string CombinationKey,
            int Multiplicity);

        private sealed record CrossRigidRewriteConflictOccurrence(
            string SelectionKey,
            string RewriteOccurrenceKey,
            string? JointCombinationKey,
            double ExpectedArmyDrawCallsEliminated,
            HashSet<string> RewriteConflictKeys,
            Dictionary<string, string>
                JointContextKeyByParentLocation);

        private sealed record CrossRigidSelectedRewriteConflictOwner(
            string GeneratedPayloadId,
            string SelectionKey,
            double OccurrenceExpectedArmyDrawCallsEliminated,
            int MaxRawDrawsSavedPerState,
            long GeneratedGeometryBytes,
            int SelectionRank);

        private sealed record CrossRigidPayloadRewriteConflictProfile(
            CrossRigidRewriteConflictOccurrence[] Occurrences);

        private enum CrossRigidPayloadSelectionDecision
        {
            Accept,
            ScenarioResolvedZeroBenefit,
            SingleVmdBenefitTooSmall,
            ScenarioResidencyBudgetExceeded,
            PhysicalPerDrawBudgetExceeded,
            RewriteOccurrenceConflict,
            GlobalGrowthCapExceeded,
            PerPayloadPhysicalCostCeilingExceeded,
            PerPayloadResidentCostCeilingExceeded,
        }

        private sealed record CrossRigidPayloadSelectionEntry(
            string GeneratedPayloadId,
            CrossRigidPayloadSelectionDecision Decision,
            double ExpectedArmyDrawCallsEliminated,
            double MarginalExpectedArmyDrawCallsEliminated,
            int MaxRawDrawsSavedPerState,
            int RewritePlanCount,
            int VmdCount,
            long GeneratedGeometryBytes,
            double ExpectedResidentGeneratedGeometryBytes,
            double PhysicalBytesPerExpectedDraw,
            double ResidentBytesPerExpectedDraw,
            int SelectionRank)
        {
            public bool Accepted =>
                Decision == CrossRigidPayloadSelectionDecision.Accept;
        }

        private sealed record ArmyResidencyModel(
            IReadOnlyDictionary<Wh3ArmyUnitCategory, HashSet<string>> UnitsByCategory,
            IReadOnlyDictionary<
                string,
                Dictionary<Wh3ArmyUnitCategory, HashSet<string>>> UnitsByCultureAndCategory,
            IReadOnlyDictionary<
                string,
                Dictionary<Wh3ArmyUnitCategory, HashSet<string>>> UnitsByVmd,
            IReadOnlyDictionary<string, IReadOnlySet<string>> CulturesByUnit,
            IReadOnlyDictionary<string, IReadOnlySet<string>> UnitIdsByAssetPath,
            IReadOnlyDictionary<string, IReadOnlySet<string>> CulturesByAssetPath,
            IReadOnlyDictionary<string, double> PlayerCultureWeights,
            IReadOnlyDictionary<string, double> OpponentCultureWeights,
            IReadOnlyDictionary<string, int> EntityCountByUnit,
            IReadOnlyDictionary<
                string,
                Dictionary<Wh3UnitVisualRole, int>> EntityCountByUnitAndRole,
            IReadOnlyDictionary<string, Wh3ArmyUnitCategory> CategoryByUnit,
            IReadOnlyDictionary<
                string,
                Dictionary<Wh3UnitVisualRole, HashSet<string>>>
                DirectVmdsByUnitAndRole,
            IReadOnlyDictionary<string, ExpectedWsModelOccurrences>
                ExpectedWsModelOccurrencesByUnit,
            IReadOnlyDictionary<
                string,
                Dictionary<Wh3UnitVisualRole, IReadOnlyList<UnitVisualConfiguration>>>
                VisualConfigurationsByUnitAndRole,
            Wh3ArmyVisualScenario Scenario,
            bool IncludesAllGameCultures);

        private sealed record ArmyLocalitySplitEvaluationReportEntry(
            bool Accepted,
            string Decision,
            long BaselineGlobalPixels,
            long ProposedGlobalPixels,
            long BaselineVmdResidentPixels,
            long ProposedVmdResidentPixels,
            double BaselineExpectedArmyResidentPixels,
            double ProposedExpectedArmyResidentPixels);

        private sealed class CrossRigidJointStateCapExclusionAggregate
        {
            public int OccurrenceSetCount { get; set; }
            public int TwoSourceOccurrenceSetCount { get; set; }
            public int OtherSourceOccurrenceSetCount { get; set; }
            public int CommonAncestorOccurrenceSetCount { get; set; }
            public int DirectJointOccurrenceSetCount { get; set; }
            public int OtherCapOccurrenceSetCount { get; set; }
            public double OverlapInclusiveExpectedDraws { get; set; }
            public long MaxRequiredWeightedStates { get; set; }
            public int RequiredUpTo8192 { get; set; }
            public int RequiredUpTo16384 { get; set; }
            public int RequiredUpTo65536 { get; set; }
            public int RequiredAbove65536 { get; set; }
            public int UnknownRequiredStates { get; set; }
            public HashSet<string> RewritePlanIds { get; } =
                new(StringComparer.Ordinal);
        }

        private sealed class CrossRigidHeaderProfileAggregate
        {
            public int ObservationCount { get; set; }
            public HashSet<string> RigidCombinations { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> VmdRoots { get; } =
                new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class BatchState
        {
            public IPackFileContainer Source { get; }
            public IPackFileContainer Output { get; }
            public IPackOptimizerPackFiles PackFileService { get; }
            public string SourcePath { get; }
            public string OutputPath { get; }
            public string ReportPath { get; }
            // Where the report file is physically written when the caller stages it; null writes to ReportPath.
            public string? ReportWritePath { get; set; }
            public Dictionary<string, VariantMesh> VmdDocuments { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, XmlDocument> WsDocuments { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, XmlDocument> MaterialDocuments { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, VanillaWsModelConsumerEntry> WsModelConsumerEntries { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, VanillaMaterialConsumerEntry> MaterialConsumerEntries { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public int GameplayTraversalWsModelXmlParseCount { get; set; }
            public int GameplayTraversalMaterialXmlParseCount { get; set; }
            public int WsModelConsumerCacheHits { get; set; }
            public int WsModelConsumerMemoryCacheHits { get; set; }
            public int WsModelConsumerVanillaCacheHits { get; set; }
            public int WsModelConsumerCacheMisses { get; set; }
            public int WsModelConsumerEntryBuilds { get; set; }
            public int MaterialConsumerCacheHits { get; set; }
            public int MaterialConsumerMemoryCacheHits { get; set; }
            public int MaterialConsumerVanillaCacheHits { get; set; }
            public int MaterialConsumerCacheMisses { get; set; }
            public int MaterialConsumerEntryBuilds { get; set; }
            public Dictionary<string, HashSet<string>> ReachableWsModelsByRoot { get; } = new(StringComparer.OrdinalIgnoreCase);
            public int ReachableWsModelsByRootVersion { get; set; }
            public HashSet<string> SourceReachableAssetFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> TaintedGameplayWsModels { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Wh3UnitCategoryResolution? UnitCategoryResolution { get; set; }
            public Wh3VanillaAtlasConsumerCache? VanillaAtlasConsumerCache { get; set; }
            public IReadOnlyList<IPackFileContainer>? GameplayTraversalContainers { get; set; }
            public Dictionary<string, GameplayTraversalContainerResolution>
                GameplayTraversalContainerResolutions { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public int GameplayTraversalContainerResolutionCacheHits { get; set; }
            public int GameplayTraversalContainerResolutionScans { get; set; }
            public GameplayMeshDependencyIndex? GameplayMeshDependencyIndex { get; set; }
            public List<CrossRigidMergeAnalysisEntry> CrossRigidMergeAnalysisEntries { get; } = [];
            public List<CrossRigidGeneratedPayloadAnalysisEntry>
                CrossRigidGeneratedPayloadAnalysisEntries { get; } = [];
            public List<CrossRigidPayloadSelectionEntry>
                CrossRigidPayloadSelectionEntries { get; } = [];
            public HashSet<string> CrossRigidSelectedPayloadIds { get; } =
                new(StringComparer.Ordinal);
            public HashSet<string> CrossRigidSelectedRewriteOccurrenceKeys { get; } =
                new(StringComparer.Ordinal);
            public Dictionary<string, HashSet<string>>
                CrossRigidSelectedJointCombinationKeysByRewriteOccurrenceKey { get; } =
                    new(StringComparer.Ordinal);
            public Dictionary<
                string,
                CrossRigidJointAlwaysPresentRewriteDescriptor>
                CrossRigidCanonicalJointDescriptorByParentLocation { get; } =
                    new(StringComparer.Ordinal);
            public Dictionary<
                string,
                CrossRigidJointAlwaysPresentRewriteDescriptor[]>
                CrossRigidFactoredJointDescriptorsByParentLocation { get; } =
                    new(StringComparer.Ordinal);
            public Dictionary<string, XmlElement[]>
                CrossRigidOriginalRootSlotsByVmd { get; } =
                    new(StringComparer.OrdinalIgnoreCase);
            public int CrossRigidFactoredJointParentCount { get; set; }
            public int CrossRigidFactoredJointGroupCount { get; set; }
            public int CrossRigidFactoredConditionalGroupCount { get; set; }
            public int CrossRigidFactoredSingleCarrierGroupCount { get; set; }
            public int CrossRigidFactoredPackingSearchImprovements { get; set; }
            public int CrossRigidFactoredPairLookaheadSeeds { get; set; }
            public int CrossRigidPartialCanonicalBeamImprovements { get; set; }
            public int CrossRigidPartialCanonicalBeamUnionCandidates { get; set; }
            public int CrossRigidCanonicalJointParentCount { get; set; }
            public int CrossRigidCanonicalJointContextsFolded { get; set; }
            public int CrossRigidCanonicalJointParentConflictCount { get; set; }
            public int CrossRigidPortfolioOrderingsEvaluated { get; set; }
            public double CrossRigidRawGreedyExpectedDraws { get; set; }
            public double CrossRigidBestPortfolioExpectedDraws { get; set; }
            public int CrossRigidPortfolioPairTrials { get; set; }
            public int CrossRigidPortfolioContextCohortTrials { get; set; }
            public int CrossRigidPortfolioLocalInsertionTrials { get; set; }
            public int CrossRigidPortfolioLocalInsertionImprovements { get; set; }
            public int CrossRigidPortfolioLocalPairInsertionTrials { get; set; }
            public int CrossRigidPortfolioLocalPairInsertionImprovements { get; set; }
            public int CrossRigidSharedPayloadFallbackCandidatePlans { get; set; }
            public int CrossRigidSharedPayloadFallbackCompatibleStates { get; set; }
            public int CrossRigidSharedPayloadFallbackAcceptedStates { get; set; }
            public double CrossRigidSharedPayloadFallbackExpectedDraws { get; set; }
            public double CrossRigidSharedPayloadFallbackResidentBytes { get; set; }
            public int CrossRigidSharedPayloadFallbackBudgetRejections { get; set; }
            public HashSet<string>
                CrossRigidSharedPayloadFallbackAdmittedPlanIds { get; } =
                    new(StringComparer.Ordinal);
            public Dictionary<string, int>
                CrossRigidRewriteConflictReasonCounts { get; } =
                    new(StringComparer.Ordinal);
            public Dictionary<string, double>
                CrossRigidRewriteConflictExpectedDraws { get; } =
                    new(StringComparer.Ordinal);
            public Dictionary<string, List<string>>
                CrossRigidRewriteConflictExamples { get; } =
                    new(StringComparer.Ordinal);
            public HashSet<string> CrossRigidGeneratedPayloadIds { get; } =
                new(StringComparer.Ordinal);
            public HashSet<string> CrossRigidGeneratedRigidPaths { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> CrossRigidGeneratedWsModelPaths { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> CrossRigidGeneratedVmdPaths { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public int CrossRigidGeneratedVmdFilesDeduplicated { get; set; }
            // Group VMD files created to nest writer slots above the engine
            // alternative limit. Also members of CrossRigidGeneratedVmdPaths.
            public HashSet<string> CrossRigidGeneratedGroupVmdPaths { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public int CrossRigidGroupVmdFilesRequested { get; set; }
            public int CrossRigidNestedWriterSlotCount { get; set; }
            public int CrossRigidMaxAlternativesBeforeNesting { get; set; }
            public int CrossRigidMaxSlotNestingDepth { get; set; }
            public List<CrossRigidInexactSlotNesting>
                CrossRigidInexactSlotNestings { get; } = [];
            public List<CrossRigidOverLimitSlot>
                CrossRigidAuthorSlotsAboveLimit { get; } = [];
            public HashSet<string> CrossRigidRewrittenRootVmdPaths { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> CrossRigidRetainedSourceAssetPaths { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int> CrossRigidEmissionSkipCounts { get; } =
                new(StringComparer.Ordinal);
            public Dictionary<string, List<string>> CrossRigidEmissionSkipExamples { get; } =
                new(StringComparer.Ordinal);
            public int CrossRigidRewritePlansApplied { get; set; }
            public int CrossRigidRewriteOccurrenceSetsApplied { get; set; }
            public double CrossRigidExpectedArmyDrawCallsEmitted { get; set; }
            public long CrossRigidRawLodDrawsBefore { get; set; }
            public long CrossRigidRawLodDrawsAfter { get; set; }
            public Dictionary<CrossRigidWsModelOccurrenceKey, double>
                CrossRigidWsModelOccurrenceProbabilities { get; } = [];
            public Dictionary<string, double>
                CrossRigidBaselineExpectedDrawsByVmd { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public int CrossRigidBaselineUnresolvedWsModelCount { get; set; }
            public int CrossRigidBaselineRigidsLoaded { get; set; }
            // Report-only "ideal merge" diagnostic: per root VMD, expected
            // battle draws under each ideal-merge tier T0..T4 (same
            // weighting as the baseline), plus the caches it uses.
            public Dictionary<string, double[]>
                CrossRigidIdealMergeExpectedDrawsByVmd { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<
                    (string RigidPath, int LodIndex, int PartIndex,
                        string MaterialPath),
                    int[]>
                CrossRigidIdealMergePartIdentityCache { get; } = [];
            public Dictionary<string, int>
                CrossRigidIdealMergeIdentityIds { get; } =
                new(StringComparer.Ordinal);
            public Dictionary<(string MaterialPath, int Tier), string>
                CrossRigidIdealMergeMaterialIdentityCache { get; } = [];
            public Dictionary<string, int[]>
                CrossRigidIdealMergeConfigurationDrawCache { get; } =
                new(StringComparer.Ordinal);
            public HashSet<string> CrossRigidIdealMergeSharedAtlasMaterialPaths
                { get; } = new(StringComparer.OrdinalIgnoreCase);
            public int CrossRigidIdealMergeSharedAtlasTextureCount { get; set; }
            public int CrossRigidIdealMergeSharedAtlasSizedTextureCount
                { get; set; }
            public long CrossRigidIdealMergeSharedAtlasBcnBytes { get; set; }
            public CrossRigidIdealMergeAccounting
                CrossRigidIdealMergeAccounting { get; } = new();
            public List<CrossRigidDisplacedSourceGeometryEntry>
                CrossRigidDisplacedSourceGeometryEntries { get; } = [];
            public Dictionary<string, int> CrossRigidAnalysisBlockerCounts { get; } =
                new(StringComparer.Ordinal);
            public Dictionary<string, List<string>> CrossRigidAnalysisBlockerExamples { get; } =
                new(StringComparer.Ordinal);
            public Dictionary<string, int> CrossRigidRigidHeaderBlockerCounts { get; } =
                new(StringComparer.Ordinal);
            public Dictionary<string, HashSet<string>>
                CrossRigidRigidHeaderBlockerRigidCombinations { get; } =
                    new(StringComparer.Ordinal);
            public Dictionary<string, HashSet<string>>
                CrossRigidRigidHeaderBlockerVmdRoots { get; } =
                    new(StringComparer.Ordinal);
            public Dictionary<string, CrossRigidHeaderProfileAggregate>
                CrossRigidRigidHeaderQualityProfiles { get; } =
                    new(StringComparer.Ordinal);
            public Dictionary<string, CrossRigidHeaderProfileAggregate>
                CrossRigidRigidHeaderFullLodProfiles { get; } =
                    new(StringComparer.Ordinal);
            public Dictionary<string, CrossRigidJointStateCapExclusionAggregate>
                CrossRigidJointStateCapExclusionsByVmd { get; } =
                    new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int> CrossRigidAnalysisDiagnosticCounts { get; } =
                new(StringComparer.Ordinal);
            public Dictionary<string, List<string>> CrossRigidAnalysisDiagnosticExamples { get; } =
                new(StringComparer.Ordinal);
            public Dictionary<CrossRigidActivationRewriteClass, int>
                CrossRigidActivationRewriteOccurrenceSetCounts { get; } = [];
            public Dictionary<CrossRigidActivationRewriteClass, int>
                CrossRigidActivationRewritePlanCounts { get; } = [];
            public Dictionary<CrossRigidActivationRewriteClass, double>
                CrossRigidActivationRewriteExpectedDrawCalls { get; } = [];
            public Dictionary<string, int>
                CrossRigidModifierProfileOccurrenceSetCounts { get; } =
                new(StringComparer.Ordinal);
            public Dictionary<string, int>
                CrossRigidModifierProfilePlanCounts { get; } =
                new(StringComparer.Ordinal);
            public Dictionary<string, double>
                CrossRigidModifierProfileExpectedDrawCalls { get; } =
                new(StringComparer.Ordinal);
            public int CrossRigidAnalysisVmdCount { get; set; }
            public int CrossRigidAnalysisDirectWsModelCount { get; set; }
            public int CrossRigidAnalysisConfigurationCount { get; set; }
            public int CrossRigidAnalysisOpportunityObservationCount { get; set; }
            public int CrossRigidPairFallbackSubgroupObservations { get; set; }
            public int CrossRigidTripleFallbackSubgroupObservations { get; set; }
            public int CrossRigidQuadrupleFallbackSubgroupObservations { get; set; }
            public int CrossRigidQuintupleFallbackSubgroupObservations { get; set; }
            public int CrossRigidMiddleTripleFallbackSubgroupObservations { get; set; }
            public int CrossRigidMiddleQuadrupleFallbackSubgroupObservations { get; set; }
            public int CrossRigidPairFallbackPlansCompetingWithSafeFullGroups { get; set; }
            public int CrossRigidPairFallbackPlansEvaluated { get; set; }
            public HashSet<string> CrossRigidPreviouslySuppressedPairFallbackPlanIds { get; } =
                new(StringComparer.Ordinal);
            public HashSet<string> CrossRigidLegacyEligiblePayloadIds { get; } =
                new(StringComparer.Ordinal);
            public Dictionary<string, HashSet<string>> XmlCompatibilityRepairs { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public List<MalformedVmdEntry> MalformedVmdRoots { get; } = [];
            public List<MalformedVmdEntry> MalformedReferencedVmds { get; } = [];
            public List<MalformedXmlAssetEntry> MalformedWsModelsIgnored { get; } = [];
            public List<MalformedXmlAssetEntry> MalformedMaterialsIgnored { get; } = [];
            public List<string> DirectAssetSkipMessages { get; } = [];
            public Dictionary<string, RmvFile> RigidModels { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, TextureInspection> TextureInspections { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, TextureAlphaState> TextureAlphaStates { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<AtlasRegionContentHashKey, string> AtlasRegionContentHashes { get; } = [];
            public Dictionary<MeshKey, List<WsUsage>> Usages { get; } = [];
            public HashSet<MeshKey> ProcessedMeshes { get; } = [];
            public HashSet<string> ModifiedWsModels { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> ModifiedRigids { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, XmlDocument> ModifiedVmdDocuments { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public bool StructuralMergeConsumerDiscoveryComplete { get; set; }
            public Dictionary<string, HashSet<string>> ImmutableMeshMergeConsumersByRigid { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, HashSet<string>> DirectRigidMeshMergeConsumersByRigid { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> StructuralMergeConsumerDiscoveryFailures { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public HashSet<GameplayMeshDiscoveryBlock> ConsumerDiscoveryBlocks { get; } = [];
            public HashSet<MeshKey> PreAtlasStructuralMergeMeshes { get; } = [];
            public HashSet<string> PreAtlasStructuralMergePairs { get; } =
                new(StringComparer.Ordinal);
            public List<HashSet<MeshKey>> PreAtlasStructuralMergeComponents { get; } = [];
            public HashSet<string> PreAtlasStructuralMergeComponentSignatures { get; } =
                new(StringComparer.Ordinal);
            public int PreAtlasStructuralMergeGroupCount { get; set; }
            public int StructuralOnlyMergeGroupCount { get; set; }
            public int StructuralAndAtlasAssistedMergeGroupCount { get; set; }
            public int AtlasAssistedMergeGroupCount { get; set; }
            public int StructuralOnlyMergePartsRemoved { get; set; }
            public int StructuralAndAtlasAssistedMergePartsRemoved { get; set; }
            public int AtlasAssistedMergePartsRemoved { get; set; }
            public int AtlasProtectedStructuralMeshCount { get; set; }
            public bool LodAwareAtlasPlanningEnabled { get; set; } = true;
            public int LodAwareAtlasFamilyClosures { get; set; }
            public int LodAwareAtlasFamiliesSpanningMultipleLods { get; set; }
            public int LodAwareAtlasFamilyClosureCandidates { get; set; }
            public int LodAwareAtlasStructuralComponentsReplanned { get; set; }
            public int LodAwareAtlasStructuralComponentsRejected { get; set; }
            public List<HashSet<MeshKey>> LodAwareAtlasFamilies { get; } = [];
            public HashSet<string> GeneratedTexturePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, (int Width, int Height)> GeneratedTextureDimensions { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            // What each atlas texture was written as: its channel slot, the format passed to the
            // writer and, for a channel written as BC3 to keep alpha, the first reason.
            public Dictionary<string, GeneratedTextureFormat> GeneratedTextureFormats { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            // How many keep-alpha format groups the atlas runs planned, and how many of them need
            // alpha kept in at least one slot.
            // Atlas candidates dropped because the cross-rigid writer built from their rigid.
            public int AtlasCandidatesExcludedByCrossRigid { get; set; }
            public int KeepAlphaFormatGroupsPlanned { get; set; }
            public int KeepAlphaFormatGroupsWithAlpha { get; set; }
            public List<GeneratedTextureTimingEntry> GeneratedTextureTimings { get; } = [];
            public List<AtlasPlacementBatchDiagnostic> AtlasPlacementDiagnostics { get; } = [];
            public List<AtlasPhysicalPageDiagnostic> AtlasPhysicalPageDiagnostics { get; } = [];
            public HashSet<string> GeneratedMaterialPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, GeneratedMaterialEntry> GeneratedMaterialByContentHash { get; } = new(StringComparer.Ordinal);
            public int GeneratedMaterialReuses { get; set; }
            public bool MergeCompatibleMeshesEnabled { get; }
            public bool ShareAtlasesAcrossVmdsEnabled { get; }
            public bool OptimizeGeometryEnabled { get; }
            public bool CrossRigidOnly { get; set; }
            public bool CrossRigidMergesEnabled { get; set; }
            public bool BuildAtlases { get; set; } = true;
            public LodAuditResult? LodAudit { get; set; }
            public AssetChecksResult? AssetChecks { get; set; }
            public BoneWeightAuditResult? BoneWeightAudit { get; set; }
            // Cross-rigid-only mode never builds atlases, whatever BuildAtlases says.
            public bool TextureAtlasingActive => BuildAtlases && !CrossRigidOnly;
            public bool ExistingAtlasOutputDetected { get; set; }
            public int ExistingGeneratedAtlasCandidateCount { get; set; }
            public bool AtlasAllVmdsEnabled { get; set; }
            public int SourceVmdRootCount { get; set; }
            public int AtlasVmdRootCount { get; set; }
            public int PackWideCandidateCount { get; set; }
            public int AtlasBatchCount { get; set; }
            public int TexelDensityScaledMeshes { get; set; }
            public long TexelDensityOriginalPixelArea { get; set; }
            public long TexelDensityScaledPixelArea { get; set; }
            public Dictionary<double, int> TexelDensityScaleCounts { get; } = [];
            // One entry per ApplyTexelDensityScaling call that found a reference.
            public List<double> TexelDensityGlobalReferences { get; } = [];
            public bool DownscaleTextures { get; set; }
            public TextureDownscaleResult? TextureDownscale { get; set; }
            // The downscale analysis run on the source state before atlasing, when downscale is on
            // and a stage that sizes textures (atlas value gate, shared repack) will use it. The
            // late run on the output stays authoritative for what is rewritten.
            public TextureDownscaleResult? TextureDownscaleEarly { get; set; }
            public Dictionary<string, TextureDownscaleCandidate> DownscaleEarlyCandidates { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            // Texture path -> the scale (0.5 or 0.25) the early analysis chose; absent means 1.
            public Dictionary<string, double> DownscaleTargetScale { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            // Textures whose early estimate an earlier stage used for its own sizing.
            public HashSet<string> DownscaleEarlyUsedByGate { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> DownscaleEarlyUsedByRepack { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            // The shared repack's view of a texture at its early downscale scale (the bytes the
            // downscale would write), the matching coverage entries, and the textures it could
            // not build that for (they stay at full density).
            public Dictionary<string, SharedRepackScaledSource?> SharedRepackScaledSources { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, VanillaCoverageTexture> SharedRepackEffectiveEntries { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public List<string> SharedRepackScaledSourceFailures { get; } = [];
            // Opt-in exact texture compaction (uniform shrink, opaque BC2/BC3 to BC1).
            public bool CompactTextures { get; set; }
            // Effective only together with CompactTextures.
            public bool DropUnreadAlpha { get; set; }
            // Source files that mention a rigid path (built once, shared by the shared texture
            // repack and the exact texture compaction).
            public Dictionary<string, HashSet<string>>? RigidReferrerIndex { get; set; }
            public TextureCompactionResult? TextureCompaction { get; set; }
            public VanillaTextureCoverageResult? VanillaTextureCoverage { get; set; }
            public UnitTextureRepackResult? UnitTextureRepack { get; set; }
            // Opt-in shared texture repack. Requested is what the caller asked for;
            // Enabled is what survived the exclusions (atlasing, cross-rigid).
            public bool SharedTextureRepackRequested { get; set; }
            public bool SharedTextureRepackEnabled { get; set; }
            public SharedTextureRepackResult? SharedTextureRepack { get; set; }
            // Generated by the repack, so later stages (texture downscale) leave them alone.
            public HashSet<string> SharedRepackTexturePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> SharedRepackMaterialPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
            // Generated repack material -> the material it was copied from.
            public Dictionary<string, string> SharedRepackMaterialSources { get; } = new(StringComparer.OrdinalIgnoreCase);
            // (generated material, texture) pairs whose texture was already missing in the source
            // material: kept as authored, not a validation error.
            public List<(string Material, string Texture)> SharedRepackKeptMissingTextures { get; } = [];
            public HashSet<MeshKey> SharedRepackParts { get; } = [];
            // What texture atlasing changed, for the shared texture repack that runs after it: the
            // rigids it rewrote (UVs, repointed WSModel material) and the parts it rewrote. An
            // atlased part no longer references its original textures.
            public HashSet<string> AtlasChangedRigids { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<(string Rigid, int Lod, int Part)> AtlasChangedParts { get; } = [];
            // Source rigids the cross-rigid writer built generated models from (their geometry is
            // copied, the files are kept): the shared texture repack leaves them alone.
            public HashSet<string> CrossRigidChangedRigids { get; } = new(StringComparer.OrdinalIgnoreCase);
            public int AtlasValueGateBatchesAccepted { get; set; }
            public int AtlasValueGateBatchesRejected { get; set; }
            public int AtlasValueGateTextureOnlyMergeBatchesAccepted { get; set; }
            public int AtlasValueGateTextureOnlyMergeCandidatesAccepted { get; set; }
            public int AtlasValueGateTextureOnlyMergeMarginalGroupsAccepted { get; set; }
            public int AtlasValueGateTextureOnlyMergeCrossBatchGroupsEvaluated { get; set; }
            public int AtlasValueGateTextureOnlyMergeCrossBatchGroupsAccepted { get; set; }
            public int AtlasValueGateTextureOnlyMergeSubsetGroupsConsidered { get; set; }
            public int AtlasValueGateTextureOnlyMergeSubsetOptionsEvaluated { get; set; }
            public int AtlasValueGateTextureOnlyMergeSubsetOptionGroupsTruncated { get; set; }
            public int AtlasValueGateTextureOnlyMergeMaximumSubsetGroupCandidateCount { get; set; }
            public int AtlasValueGateTextureOnlyMergeFourWayOptionsGenerated { get; set; }
            public int AtlasValueGateTextureOnlyMergeFourWayOptionsEvaluated { get; set; }
            public int AtlasValueGateTextureOnlyMergeFourWayGroupsAccepted { get; set; }
            public List<AtlasValueGateTextureOnlyMergeNearMiss> AtlasValueGateTextureOnlyMergeNearMisses { get; } = [];
            public int AtlasValueGateTextureOnlyBatchesAccepted { get; set; }
            public int AtlasValueGateTextureOnlyCandidatesAccepted { get; set; }
            public int AtlasValueGateTextureConsolidationBatchesAccepted { get; set; }
            public int AtlasValueGateTextureConsolidationCandidatesAccepted { get; set; }
            public int AtlasValueGateTextureConsolidatedAssignments { get; set; }
            public int AtlasValueGateTextureConsolidationCohortsEvaluated { get; set; }
            public int AtlasValueGateTextureConsolidationCohortsSplit { get; set; }
            public int AtlasValueGateTextureOnlyPackReferenceRetiredSourceTextureCount { get; set; }
            public int AtlasValueGateTextureOnlyRetiredSourceTextureCount { get; set; }
            public int AtlasValueGateTextureOnlyScenarioDisplacedSourceTextureCount { get; set; }
            public double AtlasValueGateTextureOnlyExpectedRetiredSourceTextureCount { get; set; }
            public long AtlasValueGateTextureOnlyRetiredBcnBytesAccepted { get; set; }
            public double AtlasValueGateTextureOnlyExpectedRetiredBcnBytesAccepted { get; set; }
            public int AtlasValueGateBroadBatchesSplit { get; set; }
            public int AtlasValueGateUnresolvedCandidatesSkipped { get; set; }
            public int AtlasValueGateMarginalGroupsAccepted { get; set; }
            public int AtlasValueGateMixedGroupsRejected { get; set; }
            public int AtlasValueGateCandidatesAccepted { get; set; }
            public int AtlasValueGateCandidatesRejected { get; set; }
            public long AtlasValueGateGeneratedBcnBytesAccepted { get; set; }
            public long AtlasValueGateGeneratedBcnBytesRejected { get; set; }
            public long AtlasValueGateRetiredBcnBytesAccepted { get; set; }
            public long AtlasValueGateRetiredBcnBytesRejected { get; set; }
            public long AtlasValueGateNetBcnBytesAccepted { get; set; }
            public long AtlasValueGateNetBcnBytesRejected { get; set; }
            public double AtlasValueGateExpectedArmyGeneratedBcnBytesAccepted { get; set; }
            public double AtlasValueGateExpectedArmyGeneratedBcnBytesRejected { get; set; }
            public double AtlasValueGateExpectedArmyRetiredBcnBytesAccepted { get; set; }
            public double AtlasValueGateExpectedArmyRetiredBcnBytesRejected { get; set; }
            public double AtlasValueGateExpectedArmyNetBcnBytesAccepted { get; set; }
            public double AtlasValueGateExpectedArmyNetBcnBytesRejected { get; set; }
            public double AtlasValueGateExpectedDrawsAccepted { get; set; }
            public Dictionary<string, AtlasValueGateSourceTextureResidencyDiagnostic>
                AtlasValueGateSourceTextureResidencyDiagnostics { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public int AtlasValueGateCandidateAffectedUnitCount { get; set; }
            public int AtlasValueGateAcceptedAffectedUnitCount =>
                AtlasValueGateAcceptedAffectedUnits.Count;
            public int AtlasValueGateCoverageOrderedBatchCount { get; set; }
            public HashSet<string> AtlasValueGateAcceptedAffectedUnits { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public List<AtlasValueGateBatchEconomics> AtlasValueGateAcceptedBatchEconomics { get; } = [];
            public int AtlasValueGateReachableWsModelCount { get; set; }
            public int AtlasValueGateSourceWsModelCount { get; set; }
            public int AtlasValueGateCaWsModelCount { get; set; }
            public int AtlasValueGateUnresolvedWsModelCount { get; set; }
            public int AtlasValueGateDirectRigidPathCount { get; set; }
            public int AtlasValueGateDirectVmdPathCount { get; set; }
            public int AtlasValueGateContainerResolutionScans { get; set; }
            public int AtlasValueGateContainerResolutionCacheHits { get; set; }
            public int AtlasValueGateWsModelConsumerCacheHits { get; set; }
            public int AtlasValueGateWsModelConsumerMemoryCacheHits { get; set; }
            public int AtlasValueGateWsModelConsumerVanillaCacheHits { get; set; }
            public int AtlasValueGateWsModelConsumerCacheMisses { get; set; }
            public int AtlasValueGateWsModelConsumerEntryBuilds { get; set; }
            public int AtlasValueGateMaterialConsumerCacheHits { get; set; }
            public int AtlasValueGateMaterialConsumerMemoryCacheHits { get; set; }
            public int AtlasValueGateMaterialConsumerVanillaCacheHits { get; set; }
            public int AtlasValueGateMaterialConsumerCacheMisses { get; set; }
            public int AtlasValueGateMaterialConsumerEntryBuilds { get; set; }
            public int AtlasValueGateGameplayTraversalWsModelXmlParseCount { get; set; }
            public int AtlasValueGateGameplayTraversalMaterialXmlParseCount { get; set; }
            public Dictionary<string, AtlasValueGateSourceTexture>? AtlasValueGateSourceTextureIndex { get; set; }
            public HashSet<string> AtlasValueGateCandidateTexturePaths { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public bool AtlasValueGateCandidateTexturePathsInitialized { get; set; }
            public Dictionary<AtlasValueGateSourceReference, List<string>>?
                AtlasValueGateSourceTexturesByReference { get; set; }
            public Dictionary<string, int> AtlasValueGateSourceTextureOrder { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, HashSet<string>>? AtlasValueGateRootsByWsModel { get; set; }
            public Dictionary<string, HashSet<string>>? AtlasValueGateRootsByWsModelCache { get; set; }
            public int AtlasValueGateRootsByWsModelCacheVersion { get; set; } = -1;
            public Dictionary<MeshKey, HashSet<string>> AtlasValueGateRootsByMesh { get; } = [];
            public HashSet<AtlasValueGateSourceReference> AtlasValueGateRewrittenSourceReferences { get; } = [];
            public Dictionary<string, int> AtlasValueGateRewrittenReferenceCountByTexture { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int> AtlasValueGateRewrittenPackReferenceCountByTexture { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<ArmyResidencyProbabilityCacheKey, double>
                AtlasValueGateArmyResidentProbabilityCache { get; } = [];
            public int AtlasValueGateArmyResidentProbabilityCacheHits { get; set; }
            public int AtlasValueGateArmyResidentProbabilityCacheMisses { get; set; }
            public int ConstantOnlyAtlasChannelsSkipped { get; set; }
            public HashSet<string> UniformConstantTexturePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> UniformConstantTextureCanonicalPaths { get; } =
                new(StringComparer.Ordinal);
            public int UniformConstantTextureAssignmentsCanonicalized { get; set; }
            public int LargeBcSplitCompressionChannels { get; set; }
            public int AtlasPixelAreaOptimizedSplits { get; set; }
            public int AtlasPixelAreaSplitEvaluations { get; set; }
            public int AtlasNonContiguousOptimizedSplits { get; set; }
            public int AtlasNonContiguousSplitEvaluations { get; set; }
            public long AtlasPixelAreaSavedByOptimizedSplits { get; set; }
            public long AtlasPixelAreaSavedByNonContiguousSplits { get; set; }
            public int VmdLocalitySplitEvaluations { get; set; }
            public int VmdLocalitySplitsAccepted { get; set; }
            public long VmdLocalityResidentPixelsSaved { get; set; }
            public double ExpectedArmyResidentPixelsBeforeLocality { get; set; }
            public double ExpectedArmyResidentPixelsAfterLocality { get; set; }
            public double ExpectedArmyResidentPixelsAfterMergeAware { get; set; }
            public double ExpectedArmyResidentPixelsSavedByLocality { get; set; }
            public ArmyResidencyModel? ArmyResidencyModel { get; set; }
            public BcnTextureResidencySummary? SourceBcnResidency { get; set; }
            public BcnTextureResidencySummary? OutputBcnResidency { get; set; }
            public List<ArmyLocalitySplitEvaluationReportEntry> ArmyLocalitySplitEvaluations { get; } = [];
            public long VmdLocalityGlobalPixelsAdded { get; set; }
            public int MergeAwareLocalityRegressionsRejected { get; set; }
            public int MergeAwareBatchPairsConsidered { get; set; }
            public int MergeAwareRepartitionEvaluations { get; set; }
            public int MergeAwareRepartitionsAccepted { get; set; }
            public long MergeAwareRepartitionPixelsSaved { get; set; }
            public int MergeAwareBatchCoalesceEvaluations { get; set; }
            public int MergeAwareBatchCoalescesAccepted { get; set; }
            public long MergeAwareBatchCoalescePixelsSaved { get; set; }
            public int MergeAwareAffinityPotentialBefore { get; set; }
            public int MergeAwareAffinityPotentialAfter { get; set; }
            public int MergeAwareAffinityEliminationsGained { get; set; }
            public double ExpectedArmyDrawCallsEliminatedBeforeMergeAware { get; set; }
            public double ExpectedArmyDrawCallsEliminatedAfterValueGate { get; set; }
            public List<MergeAwareRepartitionReportEntry> MergeAwareRepartitionEntries { get; } = [];
            public int CrossVmdSharedAtlasBatches { get; set; }
            public int CrossVmdSharedAtlasPlacements { get; set; }
            public int CrossVmdMaterialReuses { get; set; }
            public int AtlasPlacementsGenerated { get; set; }
            public int AtlasPlacementsReused { get; set; }
            public int WrappedUvPlacementsCanonicalized { get; set; }
            public int UvIslandNormalizedMeshes { get; set; }
            public int UvIslandsShifted { get; set; }
            public long UvIslandCropPixelsSaved { get; set; }
            public int SharedUvIslandCutGroups { get; set; }
            public List<UvIslandNormalizationReportEntry> UvIslandNormalizationEntries { get; } = [];
            public int ContentDeduplicatedAtlasPlacements { get; set; }
            public int ContentCanonicalizedMeshReferences { get; set; }
            public int MeshPartsBeforeMerging { get; set; }
            public int MeshPartsAfterMerging { get; set; }
            public int MeshPartsEliminated => MeshPartsBeforeMerging - MeshPartsAfterMerging;
            public List<MeshMergeReportEntry> MeshMergeEntries { get; } = [];
            public List<string> MeshMergeSkipMessages { get; } = [];
            public Dictionary<string, int> MeshMergeBlockerCounts { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, List<string>> MeshMergeBlockerExamples { get; } = new(StringComparer.Ordinal);
            public int SemanticMaterialPathMergeParts { get; set; }
            public Dictionary<string, int> MaterialShaderPairBlockerCounts { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, int> MaterialTextureSlotBlockerCounts { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, int> MaterialParameterFieldBlockerCounts { get; } = new(StringComparer.Ordinal);
            public int CounterfactualTextureOnlyPairsEvaluated { get; set; }
            public int CounterfactualTextureOnlyPairsAtlasable { get; set; }
            public int CounterfactualTextureOnlyPairsCanonicalizable { get; set; }
            public int ProspectiveMergeAffinityGroupsAdded { get; set; }
            public int ProspectiveMergeAffinityPairs { get; set; }
            public Dictionary<string, int> CounterfactualTexturePairCounts { get; } =
                new(StringComparer.Ordinal);
            public Dictionary<string, List<string>> CounterfactualTexturePairExamples { get; } =
                new(StringComparer.Ordinal);
            public Dictionary<string, MaterialMergeDiagnosticSnapshot?> MeshMergeMaterialDiagnostics { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<MeshKey, int> AtlasBatchByMesh { get; } = [];
            public Dictionary<int, AtlasBatchDiagnosticSnapshot> AtlasBatchDiagnostics { get; } = [];
            public Dictionary<AtlasBatchPair, AtlasBatchCombinationDiagnostic> AtlasBatchCombinationDiagnostics { get; } = [];
            public Dictionary<TextureMergeOpportunityKey, TextureMergeOpportunityAccumulator> TextureMergeOpportunities { get; } = [];
            public int MeshMergeInvariantGroupCount { get; set; }
            public int MeshMergeInvariantLodCount { get; set; }
            public int MeshMergeInvariantWsModelCount { get; set; }
            public int GeometryMeshesOptimized { get; set; }
            public long GeometryVerticesBefore { get; set; }
            public long GeometryVerticesAfter { get; set; }
            public long GeometryVerticesRemoved => GeometryVerticesBefore - GeometryVerticesAfter;
            public long GeometryUnreferencedVerticesRemoved { get; set; }
            public long GeometryDuplicateVerticesRemoved { get; set; }
            public int GeometryRigidsOptimized { get; set; }
            public long GeometryIndicesBefore { get; set; }
            public long GeometryIndicesAfter { get; set; }
            public long GeometryBytesBefore { get; set; }
            public long GeometryBytesAfter { get; set; }
            public int GeometryUnmodifiedRigidsSelected { get; set; }
            public Dictionary<string, int> GeometrySelectionSkipCounts { get; } =
                new(StringComparer.Ordinal);
            public List<string> RemovedFiles { get; } = [];
            public List<(string Path, string Reason)> PruneKeptFiles { get; } = [];
            public List<string> ValidationMessages { get; } = [];
            public List<AtlasedMeshReportEntry> AtlasedMeshes { get; } = [];
            public Dictionary<MeshKey, List<SkipDetail>> SkipDetails { get; } = [];
            public int BatchIndex { get; set; }
            public int AtlasPageIndex { get; set; }
            public int SharedAtlasPageSharingEvaluations { get; set; }
            public int SharedAtlasPageCoalescesAccepted { get; set; }
            public int SharedAtlasPageParticipatingBatches { get; set; }
            public long SharedAtlasPagePixelsSaved { get; set; }
            public CancellationToken CancellationToken { get; set; }
            public IProgress<TextureAtlasPackProgress>? Progress { get; set; }
            public Dictionary<string, TimeSpan> PhaseDurations { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, TimeSpan> CrossRigidTimingDetails { get; } =
                new(StringComparer.Ordinal);
            public Dictionary<MeshKey, string> CrossRigidSourceMeshMergeIdentityCache { get; } =
                [];
            public Dictionary<string, string> CrossRigidMaterialMergeIdentityCache { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, CrossRigidLodAnalysis[]>
                CrossRigidLodAnalysisCache { get; } =
                new(StringComparer.Ordinal);
            public Dictionary<(MeshKey Mesh, string MaterialPath, string Boundary), CrossRigidPartIdentity>
                CrossRigidSourcePartIdentityCache { get; } = [];
            public TimeSpan TotalElapsed { get; set; }

            public BatchState(
                IPackFileContainer source,
                IPackFileContainer output,
                IPackOptimizerPackFiles packFileService,
                string sourcePath,
                string outputPath,
                string reportPath,
                bool mergeCompatibleMeshesEnabled,
                bool shareAtlasesAcrossVmdsEnabled,
                bool optimizeGeometryEnabled)
            {
                Source = source;
                Output = output;
                PackFileService = packFileService;
                SourcePath = sourcePath;
                OutputPath = outputPath;
                ReportPath = reportPath;
                MergeCompatibleMeshesEnabled = mergeCompatibleMeshesEnabled;
                ShareAtlasesAcrossVmdsEnabled = shareAtlasesAcrossVmdsEnabled;
                OptimizeGeometryEnabled = optimizeGeometryEnabled;
            }
        }

        private sealed record AtlasResidencySummary(
            long GlobalPixels,
            long AggregateResidentPixels,
            int MultiRootPageCount,
            int MaxRootsPerPage,
            string WorstRootVmdPath,
            long WorstRootPixels);

        private sealed record AtlasComponentReuseAnalysis(
            List<AtlasComponentReuseEntry> Components,
            List<AtlasComponentReusePairEntry> Pairs,
            int GeometryCount,
            int ReusedGeometryCount);

        private sealed record AtlasComponentReuseEntry(
            string WsModelPath,
            string GeometryPath,
            int VmdRootCount,
            int[] AtlasBatchIds);

        private sealed record AtlasComponentReusePairEntry(
            string LeftWsModel,
            string RightWsModel,
            string LeftGeometryPath,
            string RightGeometryPath,
            int LeftRootCount,
            int RightRootCount,
            int SharedRootCount,
            double LeftCoverage,
            double RightCoverage,
            int[] LeftAtlasBatchIds,
            int[] RightAtlasBatchIds,
            bool StronglyAsymmetric,
            bool HighBidirectionalOverlap);

        private sealed record MaterialMergeDiagnosticSnapshot(
            string RenderingIdentity,
            string Shader,
            string TextureIdentity,
            string NonTextureIdentity,
            IReadOnlyDictionary<string, string> TextureAssignments,
            IReadOnlyDictionary<string, string> NonTextureFields);

        private sealed record MaterialMergeDiagnosticComparison(
            bool PathsEqual,
            bool SemanticallyEquivalent,
            string Reason,
            string Detail,
            string LeftMaterialPath,
            string RightMaterialPath);

        private sealed record AtlasBatchDiagnosticSnapshot(
            List<AtlasCandidate> Candidates,
            long PixelCost);

        private readonly record struct AtlasBatchPair(
            int FirstBatchId,
            int SecondBatchId);

        private sealed record AtlasBatchCombinationDiagnostic(
            bool Fits,
            long BaselinePixelCost,
            long CombinedPixelCost,
            long AdditionalPixels,
            double AdditionalPercent,
            string FailureReason);

        private readonly record struct TextureMergeOpportunityKey(
            string RigidPath,
            int LodIndex,
            string RmvIdentity,
            string NonTextureMaterialIdentity,
            int FirstBatchId,
            int SecondBatchId);

        private sealed class TextureMergeOpportunityAccumulator
        {
            public string RigidPath { get; }
            public int LodIndex { get; }
            public int FirstBatchId { get; }
            public int SecondBatchId { get; }
            public AtlasBatchCombinationDiagnostic Combination { get; }
            public HashSet<int> PartIndices { get; } = [];
            public List<string> Examples { get; } = [];

            public TextureMergeOpportunityAccumulator(
                string rigidPath,
                int lodIndex,
                int firstBatchId,
                int secondBatchId,
                AtlasBatchCombinationDiagnostic combination)
            {
                RigidPath = rigidPath;
                LodIndex = lodIndex;
                FirstBatchId = firstBatchId;
                SecondBatchId = secondBatchId;
                Combination = combination;
            }
        }

        private sealed record RmvMergeDiagnosticComponents(
            string ModelTypeFlag,
            string RenderFlag,
            string VertexFormat,
            string ShaderName,
            string MaterialPayloadHash);

        private sealed record MeshMergeReportEntry(
            string RigidPath,
            int LodIndex,
            int[] OldPartIndices,
            int NewPartIndex,
            int VertexCount,
            string MaterialPath,
            string MergeOrigin);

        private sealed record MeshMergeGroup(
            List<int> PartIndices,
            string[] MaterialPathsByWsModel);

        private sealed record MergeGeometryInvariantSnapshot(
            int VertexCount,
            int IndexCount,
            int TriangleCount,
            string VertexHash,
            ushort[] ExpectedIndices,
            float MinimumX,
            float MinimumY,
            float MinimumZ,
            float MaximumX,
            float MaximumY,
            float MaximumZ);

        private sealed record LodGeometryInvariantSnapshot(
            int VertexCount,
            int IndexCount,
            int TriangleCount);

        private readonly record struct TextureInspection(
            int Width,
            int Height,
            bool IsUniformConstant,
            TextureAtlasConstantColor ConstantColor);

        private sealed record GeneratedMaterialEntry(
            string Path,
            string RenderingIdentity,
            HashSet<string> RootVmdPaths);

        private sealed record MalformedVmdEntry(
            string Path,
            string Reason);

        private sealed record MalformedXmlAssetEntry(
            string Path,
            string Reason);

        private sealed record MissingTextureDependency(
            MeshKey Key,
            string Slot,
            string TexturePath);

        private sealed record WsUsage(
            string WsModelPath,
            XmlDocument? Document,
            XmlNode? MaterialNode,
            string MaterialPath,
            string? EmbeddedRigidPath = null)
        {
            public string AssetPath => string.IsNullOrWhiteSpace(EmbeddedRigidPath)
                ? WsModelPath
                : EmbeddedRigidPath;
        }

        // Keep this identity structural. Direct-vs-geometry-only-vs-unknown is a
        // scenario/topology conclusion and must be recomputed from the current dependency
        // index rather than persisted on a physical texture reference.
        private enum AtlasValueGateSourceReferenceKind
        {
            WsModelMaterial,
            RigidEmbedded,
        }

        private enum AtlasValueGateRigidConsumerClassification
        {
            DirectRigid,
            GeometryOnlyProven,
            Unknown,
        }

        private sealed record AtlasValueGateScenarioConsumerProjection(
            IReadOnlyList<string> TargetAssetPaths,
            IReadOnlyList<string> FallbackVmdRoots,
            IReadOnlyList<string> GeometryOnlyRigidPaths,
            IReadOnlyList<string> DirectRigidPaths,
            IReadOnlyList<string> UnresolvedConsumers,
            IReadOnlyList<string> UnresolvedReasons)
        {
            public bool IsResolved => UnresolvedConsumers.Count == 0;
        }

        private readonly record struct AtlasValueGateSourceReference(
            MeshKey? Mesh,
            string ConsumerAssetPath,
            string Slot,
            int SlotOccurrence,
            AtlasValueGateSourceReferenceKind Kind);

        private readonly record struct GameplayTraversalContainerResolution(
            IPackFileContainer? Container)
        {
            public static GameplayTraversalContainerResolution Missing { get; } = new(null);
        }

        private readonly record struct ArmyResidencyProbabilityCacheKey(
            string TargetAssetPaths,
            string FallbackRoots);

        private readonly record struct AtlasValueGateSourceTextureConsumerProbability(
            double Probability,
            bool IsResolved);

        private sealed record AtlasValueGateSourceTexture(
            long BcnBytes,
            bool IsOwnedBySourcePack,
            bool HasDirectVmdReference,
            bool HasDirectSourcePackVmdReference,
            HashSet<AtlasValueGateSourceReference> References,
            HashSet<AtlasValueGateSourceReference> PackReferences);

        private sealed record AtlasValueGateSourceTextureResidencyDiagnostic(
            string TexturePath,
            long BcnBytes,
            bool IsOwnedBySourcePack,
            bool HasDirectVmdReference,
            bool HasDirectSourcePackVmdReference,
            int ReferenceCount,
            int RewrittenReferenceCountBefore,
            int RewrittenReferenceCountAfter,
            bool ConsumerMappingResolved,
            double CurrentResidentProbability,
            double ProposedResidentProbability,
            double CurrentExpectedResidentBcnBytes,
            double ProposedExpectedResidentBcnBytes,
            double ExpectedRetiredBcnBytes,
            IReadOnlyList<string> CurrentConsumerWsModels,
            IReadOnlyList<string> ProposedConsumerWsModels)
        {
            public string CurrentScenarioProjection { get; init; } = string.Empty;
            public string ProposedScenarioProjection { get; init; } = string.Empty;
        }

        private sealed record AtlasValueGateGroupPlan
        {
            public MergeAffinityGroup Group { get; }
            public List<AtlasCandidate> Candidates { get; }
            public IReadOnlyList<MeshKey[]>? AtomicFamilies { get; }
            public IReadOnlyList<MergeAffinityGroup> AffinityGroups { get; }

            public AtlasValueGateGroupPlan(
                MergeAffinityGroup group,
                List<AtlasCandidate> candidates)
                : this(group, candidates, null, [group])
            {
            }

            public AtlasValueGateGroupPlan(
                MergeAffinityGroup group,
                List<AtlasCandidate> candidates,
                IReadOnlyList<MeshKey[]>? atomicFamilies)
                : this(group, candidates, atomicFamilies, [group])
            {
            }

            public AtlasValueGateGroupPlan(
                MergeAffinityGroup group,
                List<AtlasCandidate> candidates,
                IReadOnlyList<MeshKey[]>? atomicFamilies,
                IReadOnlyList<MergeAffinityGroup> affinityGroups)
            {
                Group = group;
                Candidates = candidates;
                AtomicFamilies = atomicFamilies;
                AffinityGroups = affinityGroups.Count == 0
                    ? [group]
                    : affinityGroups.ToArray();
            }
        }

        private sealed record AtlasValueGateAcceptedGroup(
            AtlasValueGateGroupPlan Plan,
            AtlasValueGateResidencyEstimate Residency,
            int RawDrawsEliminated,
            double ExpectedArmyDrawsEliminated,
            bool WasMarginalOnly,
            AtlasValueGateAcceptanceKind AcceptanceKind);

        private sealed record AtlasValueGateRejectedGroup(
            AtlasValueGateGroupPlan Plan,
            AtlasValueGateResidencyEstimate Residency,
            string RejectionReason);

        private sealed record AtlasValueGatePendingTextureMergeAttachment(
            AtlasValueGateGroupPlan Plan,
            AtlasValueGateResidencyEstimate Residency,
            string RejectionReason);

        private sealed record TextureOnlyMergeMarginalEvaluation(
            List<AtlasCandidate> CombinedCandidates,
            AtlasValueGateResidencyEstimate BaseResidency,
            AtlasValueGateResidencyEstimate CombinedResidency,
            int BaseRawDrawsEliminated,
            int CombinedRawDrawsEliminated,
            double BaseExpectedArmyDrawsEliminated,
            double CombinedExpectedArmyDrawsEliminated,
            int MarginalProspectiveTextureMergeDraws,
            int MarginalRawDrawsEliminated,
            double MarginalExpectedArmyDrawsEliminated,
            double MarginalGlobalCostBytes,
            double MarginalExpectedCostBytes,
            double MarginalPackLocalRetirementCreditBytes,
            double MarginalNetBcnBytes);

        private sealed record AtlasValueGateTextureOnlyMergeNearMiss(
            string CandidateGroup,
            string HostBatch,
            int CandidateCount,
            double IncrementalGlobalCostBytes,
            double IncrementalExpectedCostBytes,
            int RawDrawsEliminated,
            double ExpectedArmyDrawsEliminated,
            double BudgetPressure,
            string RejectionReason);

        private sealed record AtlasValueGateBatchEconomics(
            int AcceptanceSequence,
            int CandidateCount,
            string[] RootVmdPaths,
            AtlasValueGateAcceptanceKind AcceptanceKind,
            int AffectedUnitCount,
            double ExpectedRenderedEntities,
            long GeneratedBcnBytes,
            long RetiredSourceBcnBytes,
            long NetBcnBytes,
            double ExpectedArmyGeneratedBcnBytes,
            double ExpectedArmyRetiredSourceBcnBytes,
            double ExpectedArmyNetBcnBytes,
            int RawDrawsEliminated,
            double ExpectedArmyDrawsEliminated);

        private sealed record TextureConsolidationCohortAcceptance(
            List<AtlasCandidate> Candidates,
            AtlasValueGateResidencyEstimate Residency,
            int ConsolidatedTextureAssignments);

        private sealed record AtlasBatchCoverage(
            IReadOnlySet<string> AffectedUnits,
            double ExpectedRenderedEntities)
        {
            public int AffectedUnitCount => AffectedUnits.Count;
        }

        private sealed record AtlasValueGateResidencyEstimate(
            long GeneratedBcnBytes,
            long RetiredSourceBcnBytes,
            long NetBcnBytes,
            int RetiredSourceTextureCount,
            int PackReferenceRetiredSourceTextureCount,
            int ScenarioDisplacedSourceTextureCount,
            double ExpectedArmyRetiredSourceTextureCount,
            double ExpectedArmyGeneratedBcnBytes,
            double ExpectedArmyRetiredSourceBcnBytes,
            double ExpectedArmyNetBcnBytes,
            HashSet<AtlasValueGateSourceReference> RewrittenReferences,
            IReadOnlyList<AtlasValueGateSourceTextureResidencyDiagnostic>
                SourceTextureResidencyDiagnostics)
        {
            public static AtlasValueGateResidencyEstimate Empty { get; } = new(
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                [],
                []);
        }

        private sealed record AtlasCandidate(
            string RootVmdPath,
            MeshKey Key,
            RmvModel Model,
            List<WsUsage> Usages,
            string MaterialPath,
            XmlDocument MaterialDocument,
            int Width,
            int Height,
            UvBounds Bounds,
            HashSet<string> ResolvedChannels,
            Dictionary<string, TextureAtlasConstantColor> ConstantChannels,
            Dictionary<string, (int Width, int Height)> ChannelDimensions,
            List<MissingTextureDependency> MissingTextures,
            TextureAtlasUvIslandNormalization UvIslandAnalysis,
            UvIslandNormalization? UvIslandNormalization,
            double AtlasResolutionScale,
            bool RequiresMaskAtlas = false,
            bool ClearMaskBlueChannel = false)
        {
            // BC1-target slots (BaseColour, MaterialMap) whose alpha has to survive: the atlas
            // for such a slot is BC3 instead of BC1.
            public HashSet<string> KeepAlphaChannels { get; init; } = [];
            // Why each of those slots needs its alpha kept (the first CheckAtlasAlphaSafety reason).
            public Dictionary<string, string> KeepAlphaReasons { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private enum Faction3MaskCanonicalization
        {
            None,
            ZeroSelector,
            BlueSelector,
        }

        private sealed record CandidateDiscoveryResult(
            List<AtlasCandidate> Candidates,
            List<MissingTextureDependency> MissingTextures);

        private enum GameplayMeshDependencyRootKind
        {
            SourcePackVmd,
            ResolvedRosterVmd,
            ResidencyAsset,
            UnresolvedResolverVmd,
            DirectGameplayAsset,
        }

        private enum GameplayMeshDependencyAssetScope
        {
            SourcePack,
            CaPack,
            Missing,
        }

        private enum GameplayMeshDependencyFailureKind
        {
            MissingReference,
            ParseFailure,
            InvalidReference,
            UnknownType,
        }

        private enum GameplayMeshDependencyFailureDisposition
        {
            Blocking,
            VerifiedMissing,
        }

        private sealed record GameplayMeshDependencyFailure(
            string RootPath,
            string AssetPath,
            string Reason,
            GameplayMeshDependencyFailureKind FailureKind,
            GameplayMeshDependencyAssetScope AssetScope,
            GameplayMeshDependencyFailureDisposition Disposition);

        private sealed record GameplayMeshTraversalFailure(
            string AssetPath,
            string Reason,
            GameplayMeshDependencyFailureKind FailureKind,
            GameplayMeshDependencyAssetScope AssetScope);

        private sealed record GameplayMeshDiscoveryBlock(
            string AssetPath,
            string BlockKind,
            string IncompleteRootPath);

        private sealed class GameplayMeshDependencyIndex
        {
            public GameplayMeshDependencyIndex(bool verifiedMissingClassificationEnabled)
            {
                VerifiedMissingClassificationEnabled = verifiedMissingClassificationEnabled;
            }

            public bool VerifiedMissingClassificationEnabled { get; }
            public HashSet<string> GameplayRoots { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, HashSet<GameplayMeshDependencyRootKind>>
                RootKindsByPath { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, HashSet<string>> DependenciesByAssetPath { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, HashSet<string>> RootsByAssetPath { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, HashSet<string>> WsModelConsumersByRigid { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, HashSet<string>> DirectConsumersByRigid { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public HashSet<GameplayMeshDependencyFailure> FailureDetails { get; } = [];
            public HashSet<string> IncompleteRoots { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            public IEnumerable<GameplayMeshDependencyFailure> BlockingFailureDetails
                => FailureDetails.Where(failure =>
                    failure.Disposition == GameplayMeshDependencyFailureDisposition.Blocking);

            public IEnumerable<GameplayMeshDependencyFailure> VerifiedMissingFailureDetails
                => FailureDetails.Where(failure =>
                    failure.Disposition == GameplayMeshDependencyFailureDisposition.VerifiedMissing);

            public bool IsComplete => !BlockingFailureDetails.Any();

            public bool IsAssetAffectedByIncompleteRoot(string assetPathValue)
            {
                var assetPath = Normalize(assetPathValue);
                if (assetPath.Length == 0 ||
                    !RootsByAssetPath.TryGetValue(assetPath, out var roots))
                {
                    // If the index cannot relate an asset to a traversed root, its
                    // consumer topology is unknown and the caller must fail closed.
                    return true;
                }

                return roots.Any(IncompleteRoots.Contains);
            }

            public IReadOnlyList<string> GetIncompleteRootsForAsset(
                string assetPathValue)
            {
                var assetPath = Normalize(assetPathValue);
                if (assetPath.Length == 0)
                    return Array.Empty<string>();

                IEnumerable<string> roots = RootsByAssetPath.TryGetValue(assetPath, out var referencedRoots)
                    ? referencedRoots
                    : Array.Empty<string>();
                return roots
                    .Concat(IncompleteRoots.Contains(assetPath)
                        ? [assetPath]
                        : Array.Empty<string>())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(IncompleteRoots.Contains)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            public void RegisterRoot(
                string rootPathValue,
                GameplayMeshDependencyRootKind rootKind)
            {
                var rootPath = Normalize(rootPathValue);
                if (rootPath.Length == 0)
                    return;

                GameplayRoots.Add(rootPath);
                if (!RootKindsByPath.TryGetValue(rootPath, out var kinds))
                {
                    kinds = [];
                    RootKindsByPath[rootPath] = kinds;
                }

                kinds.Add(rootKind);
            }

            public IReadOnlyCollection<GameplayMeshDependencyRootKind> GetRootKinds(
                string rootPathValue)
            {
                var rootPath = Normalize(rootPathValue);
                return RootKindsByPath.TryGetValue(rootPath, out var kinds)
                    ? kinds
                    : Array.Empty<GameplayMeshDependencyRootKind>();
            }

            public void PropagateRootReferences()
            {
                bool changed;
                do
                {
                    changed = false;
                    foreach (var (consumerPath, dependencies) in DependenciesByAssetPath)
                    {
                        if (!RootsByAssetPath.TryGetValue(
                                consumerPath,
                                out var consumerRoots))
                        {
                            continue;
                        }

                        foreach (var dependencyPath in dependencies)
                        {
                            if (!RootsByAssetPath.TryGetValue(
                                    dependencyPath,
                                    out var dependencyRoots))
                            {
                                dependencyRoots = new HashSet<string>(
                                    StringComparer.OrdinalIgnoreCase);
                                RootsByAssetPath[dependencyPath] = dependencyRoots;
                            }

                            foreach (var root in consumerRoots)
                            {
                                changed |= dependencyRoots.Add(root);
                            }
                        }
                    }
                }
                while (changed);
            }

            public void AddDependency(string consumerPathValue, string dependencyPathValue)
            {
                var consumerPath = Normalize(consumerPathValue);
                var dependencyPath = Normalize(dependencyPathValue);
                if (consumerPath.Length == 0 || dependencyPath.Length == 0)
                    return;

                if (!DependenciesByAssetPath.TryGetValue(
                        consumerPath,
                        out var dependencies))
                {
                    dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    DependenciesByAssetPath[consumerPath] = dependencies;
                }

                dependencies.Add(dependencyPath);
            }

            public void AddRootReference(string assetPathValue, string rootPathValue)
            {
                var assetPath = Normalize(assetPathValue);
                var rootPath = Normalize(rootPathValue);
                if (assetPath.Length == 0 || rootPath.Length == 0)
                    return;

                if (!RootsByAssetPath.TryGetValue(assetPath, out var roots))
                {
                    roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    RootsByAssetPath[assetPath] = roots;
                }

                roots.Add(rootPath);
            }

            public IEnumerable<string> GetRootsForAsset(string assetPathValue)
            {
                var assetPath = Normalize(assetPathValue);
                return RootsByAssetPath.TryGetValue(assetPath, out var roots)
                    ? roots
                    : Array.Empty<string>();
            }

            public void AddWsModelConsumer(string rigidPathValue, string wsModelPathValue)
            {
                var rigidPath = Normalize(rigidPathValue);
                var wsModelPath = Normalize(wsModelPathValue);
                if (rigidPath.Length == 0 || wsModelPath.Length == 0)
                    return;

                if (!WsModelConsumersByRigid.TryGetValue(rigidPath, out var consumers))
                {
                    consumers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    WsModelConsumersByRigid[rigidPath] = consumers;
                }

                consumers.Add(wsModelPath);
            }

            public void AddDirectConsumer(string rigidPathValue, string rootPathValue)
            {
                var rigidPath = Normalize(rigidPathValue);
                var rootPath = Normalize(rootPathValue);
                if (rigidPath.Length == 0 || rootPath.Length == 0)
                    return;

                if (!DirectConsumersByRigid.TryGetValue(rigidPath, out var consumers))
                {
                    consumers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    DirectConsumersByRigid[rigidPath] = consumers;
                }

                consumers.Add(rootPath);
                AddRootReference(rigidPath, rootPath);
            }

            public void RecordFailure(
                string rootPathValue,
                string assetPathValue,
                string reason,
                GameplayMeshDependencyFailureKind failureKind,
                GameplayMeshDependencyAssetScope assetScope)
            {
                var rootPath = Normalize(rootPathValue);
                var assetPath = Normalize(assetPathValue);
                var disposition =
                    VerifiedMissingClassificationEnabled &&
                    failureKind == GameplayMeshDependencyFailureKind.MissingReference &&
                    assetScope == GameplayMeshDependencyAssetScope.Missing
                        ? GameplayMeshDependencyFailureDisposition.VerifiedMissing
                        : GameplayMeshDependencyFailureDisposition.Blocking;
                if (rootPath.Length != 0 &&
                    disposition == GameplayMeshDependencyFailureDisposition.Blocking)
                {
                    IncompleteRoots.Add(rootPath);
                }

                FailureDetails.Add(
                    new GameplayMeshDependencyFailure(
                        rootPath,
                        assetPath,
                        reason,
                        failureKind,
                        assetScope,
                        disposition));
            }
        }

        private sealed class GameplayMeshConsumerTraversal
        {
            public HashSet<string> WsModelPaths { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> DirectRigidPaths { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public HashSet<GameplayMeshTraversalFailure> Failures { get; } = [];
            public bool IsComplete { get; set; } = true;
        }

        private readonly record struct MergeAffinityIdentity(
            string GeometryPath,
            int LodIndex,
            string RmvIdentity,
            string MaterialIdentity);

        private sealed record MergeAffinityCandidateInfo(
            AtlasCandidate Candidate,
            string RmvIdentity,
            string StrictMaterialIdentity,
            MaterialMergeDiagnosticSnapshot? MaterialSnapshot);

        private readonly record struct MergeAffinityBucketIdentity(
            string GeometryPath,
            int LodIndex,
            string RmvIdentity,
            string Shader,
            string NonTextureIdentity,
            bool MaterialSnapshotAvailable);

        private sealed record MergeAffinityGroup(
            MeshKey[] Meshes,
            int ProspectiveTextureMergeDraws,
            string[] PreExistingStructuralMergePairs,
            string[] PreAtlasMaterialIdentities);

        private sealed record MergeAwareRepartitionReportEntry(
            int FirstBatchIndex,
            int SecondBatchIndex,
            long BaselinePixels,
            long ResultPixels,
            int BaselineAffinity,
            int ResultAffinity,
            double BaselineExpectedArmyDrawCallsEliminated,
            double ResultExpectedArmyDrawCallsEliminated);

        private sealed record AtlasBatchSplitProposal(
            List<AtlasCandidate> Left,
            List<AtlasCandidate> Right);

        private sealed record AtlasPlanningCandidateGroup(
            AtlasPlanningSourceIdentity Identity,
            List<AtlasCandidate> Candidates,
            AtlasCandidate Representative,
            AtlasCrop Crop);

        private sealed record AtlasStructuralMergeComponent(
            string GeometryPath,
            int LodIndex,
            MeshKey[] Members);

        private sealed record SharedAtlasBatch(
            List<SharedAtlasSource> Sources,
            Dictionary<MeshKey, SharedAtlasMeshMapping> MappingByMesh,
            int ContentPlacementsReused);

        private sealed class SharedAtlasPageGroup
        {
            public SharedAtlasPageGroup(
                SharedAtlasPageCompatibilityKey? compatibilityKey,
                List<int> batchIndices,
                List<List<AtlasCandidate>> batches,
                List<AtlasCandidate> candidates,
                SharedAtlasPagePlan? pagePlan,
                List<AtlasPageFootprint> originalPageFootprints)
            {
                CompatibilityKey = compatibilityKey;
                BatchIndices = batchIndices;
                Batches = batches;
                Candidates = candidates;
                PagePlan = pagePlan;
                OriginalPageFootprints = originalPageFootprints;
            }

            public SharedAtlasPageCompatibilityKey? CompatibilityKey { get; }
            public List<int> BatchIndices { get; }
            public List<List<AtlasCandidate>> Batches { get; }
            public List<AtlasCandidate> Candidates { get; }
            public SharedAtlasPagePlan? PagePlan { get; set; }
            public List<AtlasPageFootprint> OriginalPageFootprints { get; }
        }

        private sealed record SharedAtlasPagePlan(
            SharedAtlasBatch Batch,
            TextureAtlasPlan Plan,
            AtlasPageFootprint Footprint);

        private sealed record AtlasPageFootprint(
            IReadOnlyDictionary<string, (int Width, int Height)> OutputDimensionsByChannel,
            long PixelCost);

        private sealed record SharedAtlasPage(
            SharedAtlasBatch Batch,
            TextureAtlasPlan Plan,
            string AtlasStem,
            Dictionary<string, string> GeneratedPaths);

        private readonly record struct SharedAtlasMeshMapping(
            int SourceId,
            float UvOffsetU,
            float UvOffsetV,
            bool WrappedUvCanonicalized,
            bool ContentCanonicalized);

        private sealed record SharedAtlasSource(
            int Id,
            AtlasCandidate Representative,
            AtlasCrop Crop);

        private sealed record AtlasCropCluster(
            AtlasCrop Crop,
            List<AtlasCandidate> Members);

        private readonly record struct AtlasTextureSetIdentity(
            int SourceWidth,
            int SourceHeight,
            string BaseColour,
            string MaterialMap,
            string Normal,
            string Mask);

        private readonly record struct AtlasPlanningSourceIdentity(
            AtlasTextureSetIdentity TextureSet,
            AtlasCrop Crop,
            double ResolutionScale);

        private readonly record struct SharedAtlasPageCompatibilityKey(
            string RootVmdPath,
            string OutputPolicySignature);

        private readonly record struct AtlasContentCompatibilityKey(
            int SourceWidth,
            int SourceHeight,
            int CropWidth,
            int CropHeight,
            string BaseColour,
            string MaterialMap,
            string Normal,
            string Mask);

        private readonly record struct AtlasContentIdentity(
            string BaseColour,
            string MaterialMap,
            string Normal,
            string Mask);

        private readonly record struct AtlasRegionContentHashKey(
            string TexturePath,
            int LayoutSourceWidth,
            int LayoutSourceHeight,
            AtlasCrop Crop);

        private readonly record struct CanonicalAtlasCrop(
            AtlasCrop Crop,
            int TileOffsetU,
            int TileOffsetV);

        private readonly record struct AtlasCrop(
            int X,
            int Y,
            int Width,
            int Height);

        private sealed record UvIslandNormalization(
            int IslandCount,
            int ShiftedIslandCount,
            int[] TileOffsetUByVertex,
            int[] TileOffsetVByVertex,
            UvBounds OriginalBounds,
            UvBounds NormalizedBounds,
            AtlasCrop OriginalCrop,
            AtlasCrop NormalizedCrop);

        private sealed record UvIslandNormalizationReportEntry(
            MeshKey Mesh,
            int IslandCount,
            int ShiftedIslandCount,
            AtlasCrop OriginalCrop,
            AtlasCrop NormalizedCrop);

        private sealed record GeneratedTextureFormat(
            string Slot,
            Editors.ImportExport.Dds.Native.DXGI_FORMAT Format,
            string? KeepAlphaReason);

        private sealed record AtlasedMeshReportEntry(
            string RootVmdPath,
            MeshKey Key,
            string[] WsModelPaths,
            string OriginalMaterialPath,
            string NewMaterialPath,
            string[] AtlasPaths);

        private sealed record SkipDetail(
            string RootVmdPath,
            string WsModelPath,
            string Reason);

        private sealed class CrossRigidPartIdentity(string identity)
        {
            private string? _hash;

            public string Identity { get; } = identity;

            // Computed on first use: the strict identity can be a couple of
            // kilobytes and is hashed once per distinct source part instead
            // of once per LOD analysis that touches it.
            public string Hash => _hash ??= ContentHash(Identity);
        }

        private readonly record struct MeshKey(string GeometryPath, int LodIndex, int PartIndex)
        {
            public override string ToString() => $"{GeometryPath} [lod {LodIndex}, part {PartIndex}]";
        }

        private readonly record struct UvBounds(float MinU, float MinV, float MaxU, float MaxV);
    }
}
