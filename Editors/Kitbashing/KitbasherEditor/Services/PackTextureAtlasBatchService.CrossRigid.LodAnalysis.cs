using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Editors.ImportExport.Importing.Importers.PngToDds;
using Editors.ImportExport.Importing.Importers.PngToDds.Helpers;
using Editors.ImportExport.TextureAtlas;
using Shared.Core.ErrorHandling;
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

namespace Editors.KitbasherEditor.Services
{
    public sealed partial class PackTextureAtlasBatchService
    {
        private static CrossRigidAttachmentLocalExpansionResult
            GetCrossRigidAttachmentLocalConfigurationsForVmd(
                BatchState state,
                string vmdPathValue,
                Dictionary<
                    string,
                    CrossRigidAttachmentLocalExpansionResult> cache,
                HashSet<string> visiting)
        {
            var vmdPath = Normalize(vmdPathValue);
            if (cache.TryGetValue(vmdPath, out var cached))
                return cached;
            if (!visiting.Add(vmdPath))
            {
                return CrossRigidAttachmentLocalExpansionResult.Failed(
                    $"Cross-rigid VMD reference cycle detected ({vmdPath})");
            }

            try
            {
                var container = FindGameplayTraversalContainer(
                    state,
                    state.Source,
                    GetGameplayTraversalContainers(state),
                    vmdPath);
                var file = container?.FindFile(vmdPath);
                if (container == null ||
                    file == null ||
                    !TryGetVmdForTraversal(
                        state,
                        container,
                        vmdPath,
                        file,
                        out var vmd))
                {
                    var failure =
                        CrossRigidAttachmentLocalExpansionResult.Failed(
                            "Cross-rigid VMD could not be resolved");
                    cache[vmdPath] = failure;
                    return failure;
                }

                var result =
                    ExpandCrossRigidAttachmentLocalConfigurations(
                        state,
                        vmd,
                        vmdPath,
                        "<root>",
                        "<always>",
                        "/VARIANT_MESH",
                        cache,
                        visiting);
                cache[vmdPath] = result;
                return result;
            }
            finally
            {
                visiting.Remove(vmdPath);
            }
        }

        private static CrossRigidAttachmentLocalExpansionResult
            ExpandCrossRigidAttachmentLocalConfigurations(
                BatchState state,
                VariantMesh mesh,
                string currentVmdPath,
                string attachmentIdentity,
                string activationSignature,
                string xmlPath,
                Dictionary<
                    string,
                    CrossRigidAttachmentLocalExpansionResult> cache,
                HashSet<string> visiting)
        {
            var result = new Dictionary<
                string,
                List<CrossRigidVisualConfiguration>>(
                StringComparer.Ordinal);

            if (!string.IsNullOrWhiteSpace(mesh.ModelReference))
            {
                var modelPath = Normalize(mesh.ModelReference);
                if (Path.GetExtension(modelPath)
                        .Equals(".wsmodel", StringComparison.OrdinalIgnoreCase) &&
                    FindGameplayTraversalContainer(
                        state,
                        state.Source,
                        GetGameplayTraversalContainers(state),
                        modelPath) != null)
                {
                    var instance = new CrossRigidVisualInstance(
                        modelPath,
                        attachmentIdentity,
                        Normalize(currentVmdPath),
                        xmlPath,
                        activationSignature,
                        [],
                        string.IsNullOrWhiteSpace(mesh.ImposterModel)
                            ? string.Empty
                            : Normalize(mesh.ImposterModel),
                        !string.IsNullOrWhiteSpace(mesh.DecalDiffuse),
                        !string.IsNullOrWhiteSpace(mesh.DecalNormal),
                        (mesh.MetaDataList?.Count ?? 0) != 0);
                    result[attachmentIdentity] =
                    [
                        new CrossRigidVisualConfiguration(
                            1.0,
                            attachmentIdentity,
                            [instance],
                            [
                                new CrossRigidVisualOccurrenceSet(
                                    1.0,
                                    [instance]),
                            ]),
                    ];
                }
            }

            var slots = mesh.ChildSlots ?? [];
            for (var slotIndex = 0;
                 slotIndex < slots.Count;
                 slotIndex++)
            {
                var slot = slots[slotIndex];
                var slotProbability =
                    ParseVmdSlotProbability(slot.Probability);
                var childMeshes = slot.ChildMeshes ?? [];
                var childReferences = slot.ChildReferences ?? [];
                var alternativeCount =
                    childMeshes.Count + childReferences.Count;
                if (alternativeCount == 0 ||
                    slotProbability <= 0)
                {
                    continue;
                }

                var childAttachmentIdentity =
                    BuildCrossRigidAttachmentIdentity(
                        attachmentIdentity,
                        mesh.use_different_attach_point_parts,
                        slot);
                var isBranching =
                    slotProbability <
                        1.0 -
                        AtlasValueGateExpectedDrawEpsilon ||
                    alternativeCount > 1;
                var alternativeMaps = new List<
                    IReadOnlyDictionary<
                        string,
                        IReadOnlyList<CrossRigidVisualConfiguration>>>();

                for (var childIndex = 0;
                     childIndex < childMeshes.Count;
                     childIndex++)
                {
                    var childXmlPath =
                        $"{xmlPath}/SLOT[{slotIndex + 1}]/VARIANT_MESH[{childIndex + 1}]";
                    var childActivationSignature =
                        isBranching
                            ? AppendCrossRigidActivationToken(
                                activationSignature,
                                $"{currentVmdPath}:{xmlPath}/slot={slotIndex + 1}/mesh={childIndex + 1}")
                            : activationSignature;
                    var childExpansion =
                        ExpandCrossRigidAttachmentLocalConfigurations(
                            state,
                            childMeshes[childIndex],
                            currentVmdPath,
                            childAttachmentIdentity,
                            childActivationSignature,
                            childXmlPath,
                            cache,
                            visiting);
                    if (childExpansion.FailureReason != null)
                        return childExpansion;
                    alternativeMaps.Add(
                        childExpansion.ConfigurationsByAttachment);
                }

                for (var referenceIndex = 0;
                     referenceIndex < childReferences.Count;
                     referenceIndex++)
                {
                    var reference = childReferences[referenceIndex];
                    if (string.IsNullOrWhiteSpace(reference.Reference))
                    {
                        return CrossRigidAttachmentLocalExpansionResult.Failed(
                            "Cross-rigid child VMD reference is empty");
                    }

                    var referencedVmdPath =
                        Normalize(reference.Reference);
                    var referenceXmlPath =
                        $"{xmlPath}/SLOT[{slotIndex + 1}]/VARIANT_MESH_REFERENCE[{referenceIndex + 1}]";
                    var childActivationSignature =
                        isBranching
                            ? AppendCrossRigidActivationToken(
                                activationSignature,
                                $"{currentVmdPath}:{xmlPath}/slot={slotIndex + 1}/ref={referenceIndex + 1}")
                            : activationSignature;
                    var childExpansion =
                        GetCrossRigidAttachmentLocalConfigurationsForVmd(
                            state,
                            referencedVmdPath,
                            cache,
                            visiting);
                    if (childExpansion.FailureReason != null)
                        return childExpansion;

                    alternativeMaps.Add(
                        RebaseCrossRigidAttachmentLocalConfigurations(
                            childExpansion.ConfigurationsByAttachment,
                            childAttachmentIdentity,
                            childActivationSignature,
                            new CrossRigidVmdReferenceHop(
                                Normalize(currentVmdPath),
                                referenceXmlPath,
                                referencedVmdPath)));
                }

                var affectedAttachments = alternativeMaps
                    .SelectMany(map => map.Keys)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                foreach (var affectedAttachment in
                         affectedAttachments)
                {
                    var slotConfigurations =
                        new List<CrossRigidVisualConfiguration>();
                    if (slotProbability < 1.0)
                    {
                        var absentProbability =
                            1.0 - slotProbability;
                        slotConfigurations.Add(
                            new CrossRigidVisualConfiguration(
                                absentProbability,
                                affectedAttachment,
                                [],
                                [
                                    new CrossRigidVisualOccurrenceSet(
                                        absentProbability,
                                        []),
                                ]));
                    }

                    var alternativeWeight =
                        slotProbability / alternativeCount;
                    foreach (var alternativeMap in alternativeMaps)
                    {
                        if (alternativeMap.TryGetValue(
                                affectedAttachment,
                                out var alternativeConfigurations))
                        {
                            slotConfigurations.AddRange(
                                alternativeConfigurations.Select(
                                    configuration =>
                                        ScaleCrossRigidVisualConfiguration(
                                            configuration,
                                            alternativeWeight)));
                        }
                        else
                        {
                            slotConfigurations.Add(
                                new CrossRigidVisualConfiguration(
                                    alternativeWeight,
                                    affectedAttachment,
                                    [],
                                    [
                                        new CrossRigidVisualOccurrenceSet(
                                            alternativeWeight,
                                            []),
                                    ]));
                        }
                    }

                    slotConfigurations =
                        MergeEquivalentCrossRigidVisualConfigurations(
                            slotConfigurations);
                    List<CrossRigidVisualConfiguration>
                        currentConfigurations =
                            result.TryGetValue(
                                affectedAttachment,
                                out var existing)
                                ? existing
                                :
                                [
                                    new CrossRigidVisualConfiguration(
                                        1.0,
                                        affectedAttachment,
                                        [],
                                        [
                                            new CrossRigidVisualOccurrenceSet(
                                                1.0,
                                                []),
                                        ]),
                                ];
                    var combined =
                        CombineCrossRigidAttachmentLocalConfigurations(
                            currentConfigurations,
                            slotConfigurations,
                            affectedAttachment,
                            out var limitExceeded);
                    if (limitExceeded)
                    {
                        return CrossRigidAttachmentLocalExpansionResult.Failed(
                            $"Cross-rigid attachment-local configuration limit exceeded " +
                            $"({MaxExactVisualConfigurations:N0} unique configurations at " +
                            $"{affectedAttachment})");
                    }

                    result[affectedAttachment] = combined;
                }
            }

            return new CrossRigidAttachmentLocalExpansionResult(
                result.ToDictionary(
                    entry => entry.Key,
                    entry => (IReadOnlyList<CrossRigidVisualConfiguration>)
                        entry.Value,
                    StringComparer.Ordinal),
                null);
        }

        private static IReadOnlyDictionary<
                string,
                IReadOnlyList<CrossRigidVisualConfiguration>>
            RebaseCrossRigidAttachmentLocalConfigurations(
                IReadOnlyDictionary<
                    string,
                    IReadOnlyList<CrossRigidVisualConfiguration>>
                    configurationsByAttachment,
                string attachmentIdentity,
                string activationSignaturePrefix,
                CrossRigidVmdReferenceHop referenceHop)
        {
            var rebased = new Dictionary<
                string,
                IReadOnlyList<CrossRigidVisualConfiguration>>(
                StringComparer.Ordinal);
            foreach (var (relativeAttachment, configurations) in
                     configurationsByAttachment)
            {
                var rebasedAttachment =
                    RebaseCrossRigidAttachmentIdentity(
                        relativeAttachment,
                        attachmentIdentity);
                rebased[rebasedAttachment] = configurations
                    .Select(configuration =>
                    {
                        var instances = configuration.Instances
                            .Select(instance =>
                                RebaseCrossRigidVisualInstance(
                                    instance,
                                    attachmentIdentity,
                                    activationSignaturePrefix,
                                    referenceHop))
                            .ToArray();
                        var occurrenceSets = configuration.OccurrenceSets
                            .Select(set =>
                                new CrossRigidVisualOccurrenceSet(
                                    set.Probability,
                                    set.Instances
                                        .Select(instance =>
                                            RebaseCrossRigidVisualInstance(
                                                instance,
                                                attachmentIdentity,
                                                activationSignaturePrefix,
                                                referenceHop))
                                        .ToArray()))
                            .ToArray();
                        return new CrossRigidVisualConfiguration(
                            configuration.Probability,
                            rebasedAttachment,
                            instances,
                            occurrenceSets);
                    })
                    .ToArray();
            }

            return rebased;
        }

        private static List<CrossRigidVisualConfiguration>
            CombineCrossRigidAttachmentLocalConfigurations(
                IReadOnlyList<CrossRigidVisualConfiguration> leftConfigurations,
                IReadOnlyList<CrossRigidVisualConfiguration> rightConfigurations,
                string attachmentIdentity,
                out bool uniqueConfigurationLimitExceeded)
        {
            uniqueConfigurationLimitExceeded = false;
            var merged = new Dictionary<
                string,
                CrossRigidVisualConfiguration>(
                StringComparer.Ordinal);

            foreach (var left in leftConfigurations)
            {
                foreach (var right in rightConfigurations)
                {
                    var probability =
                        left.Probability * right.Probability;
                    if (probability <= 0)
                        continue;

                    var instances = left.Instances
                        .Concat(right.Instances)
                        .ToArray();
                    var occurrenceSets = new List<
                        CrossRigidVisualOccurrenceSet>();
                    foreach (var leftSet in left.OccurrenceSets)
                    {
                        foreach (var rightSet in right.OccurrenceSets)
                        {
                            var occurrenceProbability =
                                leftSet.Probability *
                                rightSet.Probability;
                            if (occurrenceProbability <= 0)
                                continue;

                            occurrenceSets.Add(
                                new CrossRigidVisualOccurrenceSet(
                                    occurrenceProbability,
                                    leftSet.Instances
                                        .Concat(rightSet.Instances)
                                        .ToArray()));
                        }
                    }

                    var key =
                        BuildCrossRigidRenderedConfigurationSignature(
                            instances);
                    if (merged.TryGetValue(key, out var existing))
                    {
                        merged[key] = existing with
                        {
                            Probability =
                                existing.Probability + probability,
                            OccurrenceSets =
                                MergeCrossRigidVisualOccurrenceSets(
                                    existing.OccurrenceSets
                                        .Concat(occurrenceSets)),
                        };
                    }
                    else
                    {
                        merged[key] =
                            new CrossRigidVisualConfiguration(
                                probability,
                                attachmentIdentity,
                                instances,
                                MergeCrossRigidVisualOccurrenceSets(
                                    occurrenceSets));
                        if (merged.Count >
                            MaxExactVisualConfigurations)
                        {
                            uniqueConfigurationLimitExceeded = true;
                            return [];
                        }
                    }
                }
            }

            return merged.Values
                .Where(configuration => configuration.Probability > 0)
                .ToList();
        }

        private static List<CrossRigidVisualConfiguration>
            MergeEquivalentCrossRigidVisualConfigurations(
                IEnumerable<CrossRigidVisualConfiguration> configurations)
        {
            var merged = new Dictionary<
                string,
                CrossRigidVisualConfiguration>(
                StringComparer.Ordinal);
            foreach (var configuration in configurations)
            {
                if (configuration.Probability <= 0)
                    continue;

                var key = BuildCrossRigidRenderedConfigurationSignature(
                    configuration.Instances);
                if (merged.TryGetValue(key, out var existing))
                {
                    merged[key] = existing with
                    {
                        Probability =
                            existing.Probability +
                            configuration.Probability,
                        OccurrenceSets =
                            MergeCrossRigidVisualOccurrenceSets(
                                existing.OccurrenceSets.Concat(
                                    configuration.OccurrenceSets)),
                    };
                }
                else
                {
                    merged[key] = configuration with
                    {
                        OccurrenceSets =
                            MergeCrossRigidVisualOccurrenceSets(
                                configuration.OccurrenceSets),
                    };
                }
            }

            return merged.Values.ToList();
        }

        private static CrossRigidVisualConfiguration
            ScaleCrossRigidVisualConfiguration(
                CrossRigidVisualConfiguration configuration,
                double factor)
            => new(
                configuration.Probability * factor,
                configuration.AttachmentIdentity,
                configuration.Instances,
                configuration.OccurrenceSets
                    .Select(set =>
                        new CrossRigidVisualOccurrenceSet(
                            set.Probability * factor,
                            set.Instances))
                    .ToArray());

        private static CrossRigidVisualInstance
            RebaseCrossRigidVisualInstance(
                CrossRigidVisualInstance instance,
                string attachmentIdentity,
                string activationSignaturePrefix,
                CrossRigidVmdReferenceHop referenceHop)
            => instance with
            {
                AttachmentIdentity =
                    RebaseCrossRigidAttachmentIdentity(
                        instance.AttachmentIdentity,
                        attachmentIdentity),
                ActivationSignature =
                    RebaseCrossRigidActivationSignature(
                        instance.ActivationSignature,
                        activationSignaturePrefix),
                ReferenceChain =
                    new[] { referenceHop }
                        .Concat(instance.ReferenceChain)
                        .ToArray(),
            };

        private static string AppendCrossRigidActivationToken(
            string activationSignature,
            string token)
            => activationSignature.Equals(
                    "<always>",
                    StringComparison.Ordinal)
                ? token
                : string.Join(
                    "\u001f",
                    activationSignature,
                    token);

        private static string RebaseCrossRigidActivationSignature(
            string relativeSignature,
            string activationSignaturePrefix)
        {
            if (relativeSignature.Equals(
                    "<always>",
                    StringComparison.Ordinal))
            {
                return activationSignaturePrefix;
            }

            if (activationSignaturePrefix.Equals(
                    "<always>",
                    StringComparison.Ordinal))
            {
                return relativeSignature;
            }

            return string.Join(
                "\u001f",
                activationSignaturePrefix,
                relativeSignature);
        }

        private static IReadOnlyList<CrossRigidVisualOccurrenceSet>
            MergeCrossRigidVisualOccurrenceSets(
                IEnumerable<CrossRigidVisualOccurrenceSet> occurrenceSets)
        {
            var merged = new Dictionary<
                string,
                CrossRigidVisualOccurrenceSet>(
                StringComparer.Ordinal);
            foreach (var occurrenceSet in occurrenceSets)
            {
                if (occurrenceSet.Probability <= 0)
                    continue;

                var key =
                    BuildCrossRigidOccurrenceProvenanceSignature(
                        occurrenceSet.Instances);
                if (merged.TryGetValue(key, out var existing))
                {
                    merged[key] = existing with
                    {
                        Probability =
                            existing.Probability +
                            occurrenceSet.Probability,
                    };
                }
                else
                {
                    merged[key] = occurrenceSet;
                }
            }

            return merged.Values
                .OrderBy(
                    set =>
                        BuildCrossRigidOccurrenceProvenanceSignature(
                            set.Instances),
                    StringComparer.Ordinal)
                .ToArray();
        }

        private static string BuildCrossRigidOccurrenceProvenanceSignature(
            IEnumerable<CrossRigidVisualInstance> instances)
            => string.Join(
                "\u001e",
                instances
                    .Select(instance => string.Join(
                        "\u001f",
                        instance.WsModelPath,
                        instance.DefiningVmdPath,
                        instance.ModelXmlPath,
                        instance.ActivationSignature,
                        string.Join(
                            "\u001d",
                            instance.ReferenceChain.Select(hop =>
                                $"{hop.OwnerVmdPath}|{hop.ReferenceXmlPath}|{hop.ReferencedVmdPath}"))))
                    .OrderBy(value => value, StringComparer.Ordinal));

        private static string BuildCrossRigidRenderedConfigurationSignature(
            IEnumerable<CrossRigidVisualInstance> instances)
            => string.Join(
                "\u001f",
                instances
                    .Select(instance => instance.WsModelPath)
                    .OrderBy(
                        path => path,
                        StringComparer.OrdinalIgnoreCase));

        private static string RebaseCrossRigidAttachmentIdentity(
            string relativeIdentity,
            string attachmentIdentity)
        {
            const string rootIdentity = "<root>";
            if (relativeIdentity.Equals(
                    rootIdentity,
                    StringComparison.Ordinal))
            {
                return attachmentIdentity;
            }

            if (relativeIdentity.StartsWith(
                    rootIdentity + "/",
                    StringComparison.Ordinal))
            {
                return attachmentIdentity +
                       relativeIdentity[rootIdentity.Length..];
            }

            return string.Join(
                "/",
                attachmentIdentity,
                relativeIdentity);
        }

        private static string BuildCrossRigidAttachmentIdentity(
            string parentIdentity,
            string? meshUsesDifferentAttachPointParts,
            SLOT slot)
        {
            var attachmentPoint = string.IsNullOrWhiteSpace(slot.AttachmentPoint)
                ? "<inherit>"
                : slot.AttachmentPoint.Trim().ToLowerInvariant();
            var meshPartsMode = string.IsNullOrWhiteSpace(
                    meshUsesDifferentAttachPointParts)
                ? "<default>"
                : meshUsesDifferentAttachPointParts.Trim().ToLowerInvariant();
            var slotPartsMode = string.IsNullOrWhiteSpace(
                    slot.use_different_attach_point_parts)
                ? "<default>"
                : slot.use_different_attach_point_parts.Trim().ToLowerInvariant();
            return string.Join(
                "/",
                parentIdentity,
                $"attach={attachmentPoint}",
                $"mesh_parts={meshPartsMode}",
                $"slot_parts={slotPartsMode}");
        }

        private static bool TryBuildCrossRigidAnalysisComponent(
            BatchState state,
            string vmdPath,
            string wsModelPathValue,
            IReadOnlyList<IPackFileContainer> traversalContainers,
            Dictionary<string, RmvFile> rigidCache,
            out CrossRigidAnalysisComponent component,
            out string reason)
        {
            component = null!;
            reason = string.Empty;
            var wsModelPath = Normalize(wsModelPathValue);
            var wsContainer = FindGameplayTraversalContainer(
                state,
                state.Source,
                traversalContainers,
                wsModelPath);
            var wsFile = wsContainer?.FindFile(wsModelPath);
            if (wsContainer == null || wsFile == null)
            {
                reason = "WSModel could not be resolved";
                return false;
            }

            XmlDocument wsDocument;
            if (!state.WsDocuments.TryGetValue(wsModelPath, out wsDocument!))
            {
                if (!TryGetWsDocumentForTraversal(
                        state,
                        wsContainer,
                        wsModelPath,
                        wsFile,
                        out wsDocument))
                {
                    reason = "WSModel could not be parsed";
                    return false;
                }
            }

            var rigidPath = Normalize(
                wsDocument.SelectSingleNode("/model/geometry")?.InnerText);
            if (rigidPath.Length == 0 ||
                !Path.GetExtension(rigidPath).Equals(
                    ".rigid_model_v2",
                    StringComparison.OrdinalIgnoreCase))
            {
                reason = "WSModel has no supported rigid geometry path";
                return false;
            }

            if (!rigidCache.TryGetValue(rigidPath, out var rigid))
            {
                var rigidContainer = FindGameplayTraversalContainer(
                    state,
                    state.Source,
                    traversalContainers,
                    rigidPath);
                var rigidFile = rigidContainer?.FindFile(rigidPath);
                if (rigidContainer == null || rigidFile == null)
                {
                    reason = "Rigid geometry could not be resolved";
                    return false;
                }

                try
                {
                    rigid = ModelFactory.Create().Load(rigidFile.DataSource.ReadData());
                    rigidCache[rigidPath] = rigid;
                }
                catch (Exception ex) when (
                    ex is InvalidDataException or
                    InvalidOperationException or
                    ArgumentException or
                    FormatException)
                {
                    reason = $"Rigid geometry could not be parsed ({ex.Message})";
                    return false;
                }
            }

            if (!TryReadWsMaterialAssignments(
                    wsDocument,
                    rigid,
                    out var assignments,
                    out var assignmentReason,
                    allowUnreferencedMaterialEntries: true))
            {
                reason = $"WSModel material table is not structurally usable ({assignmentReason})";
                return false;
            }

            // A few modded WSModels retain obsolete LOD/part material rows
            // after the rigid's mesh topology was reduced. Those rows cannot
            // be sampled by the current rigid. Accept only this surplus in
            // cross-rigid source analysis: missing, blank, duplicate, or
            // malformed assignments still fail, and generated WSModels are
            // rewritten and validated with the exact active part count.
            var referencedMaterialCount =
                rigid.ModelList.Sum(lod => lod.Length);
            var authoredMaterialCount =
                wsDocument.SelectNodes("/model/materials/material")?.Count ?? 0;
            if (authoredMaterialCount > referencedMaterialCount)
            {
                RecordCrossRigidAnalysisDiagnostic(
                    state,
                    "Unused source WSModel material entries ignored",
                    $"{wsModelPath}: authored={authoredMaterialCount}, " +
                    $"active={referencedMaterialCount}, " +
                    $"unused={authoredMaterialCount - referencedMaterialCount}");
            }

            component = new CrossRigidAnalysisComponent(
                vmdPath,
                wsModelPath,
                rigidPath,
                wsDocument,
                rigid,
                assignments,
                GetCrossRigidWsModelTopologyIdentity(wsDocument),
                state.Source.ContainsFile(wsModelPath),
                state.Source.ContainsFile(rigidPath));
            return true;
        }

        private static List<CrossRigidLodAnalysis>
            BuildCrossRigidLodAnalyses(
                BatchState state,
                IReadOnlyList<CrossRigidAnalysisInstanceComponent> instances,
                CancellationToken cancellationToken)
        {
            var components = instances
                .Select(item => item.Component)
                .GroupBy(
                    component => component.WsModelPath,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(
                    component => component.WsModelPath,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (components.Length == 0)
                return [];

            var sourceLodCount = components[0].Rigid.ModelList.Length;
            var qualityProfilesDiffer =
                components
                    .Select(component =>
                        BuildCrossRigidQualityProfile(component.Rigid))
                    .Distinct(StringComparer.Ordinal)
                    .Skip(1)
                    .Any();
            var distanceProfilesDiffer =
                components
                    .Select(component =>
                        BuildCrossRigidDistanceProfile(component.Rigid))
                    .Distinct(StringComparer.Ordinal)
                    .Skip(1)
                    .Any();

            IReadOnlyList<CrossRigidGeneratedLodScheduleEntry> schedule;
            if (!distanceProfilesDiffer &&
                sourceLodCount <= 5)
            {
                schedule = Enumerable
                    .Range(0, sourceLodCount)
                    .Select(lodIndex =>
                        new CrossRigidGeneratedLodScheduleEntry(
                            components[0].Rigid.LodHeaders[lodIndex]
                                .LodCameraDistance,
                            components
                                .Select(component =>
                                    new CrossRigidSourceLodSelection(
                                        component.WsModelPath,
                                        lodIndex))
                                .ToArray()))
                    .ToArray();
            }
            else
            {
                schedule =
                    BuildBoundedCrossRigidUnionLodSchedule(
                        components);
            }

            var result =
                new List<CrossRigidLodAnalysis>(schedule.Count);
            for (var generatedLodIndex = 0;
                 generatedLodIndex < schedule.Count;
                 generatedLodIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scheduleEntry = schedule[generatedLodIndex];
                var qualityLevel = qualityProfilesDiffer
                    ? GetCrossRigidDefaultLodQualityLevel(
                        generatedLodIndex)
                    : GetCrossRigidReconciledLodQualityLevel(
                        components,
                        scheduleEntry.SourceLodSelections,
                        generatedLodIndex);

                result.Add(
                    AnalyzeCrossRigidLodState(
                        state,
                        instances,
                        generatedLodIndex,
                        generatedLodIndex,
                        qualityLevel,
                        scheduleEntry.CameraDistance,
                        scheduleEntry.SourceLodSelections,
                        isScenarioRepresentativeState: true));
            }

            return result;
        }

        private static IReadOnlyList<CrossRigidGeneratedLodScheduleEntry>
            BuildBoundedCrossRigidUnionLodSchedule(
                IReadOnlyList<CrossRigidAnalysisComponent> components)
        {
            var targetLodCount = Math.Clamp(
                components.Max(component =>
                    component.Rigid.LodHeaders.Length),
                1,
                MaxCrossRigidGeneratedLodCount);
            var rawBoundaries =
                new List<CrossRigidLodBoundaryCandidate>();
            foreach (var component in components)
            {
                for (var lodIndex = 0;
                     lodIndex < component.Rigid.LodHeaders.Length;
                     lodIndex++)
                {
                    var distance =
                        component.Rigid.LodHeaders[lodIndex]
                            .LodCameraDistance;
                    if (!float.IsFinite(distance) ||
                        distance <= 0)
                    {
                        continue;
                    }

                    rawBoundaries.Add(
                        new CrossRigidLodBoundaryCandidate(
                            distance,
                            GetCrossRigidLodTransitionWeight(
                                component,
                                lodIndex)));
                }
            }

            var unionBoundaries =
                MergeCrossRigidLodBoundaryCandidates(
                    rawBoundaries);
            if (unionBoundaries.Count == 0)
            {
                return Enumerable
                    .Range(0, targetLodCount)
                    .Select(lodIndex =>
                        new CrossRigidGeneratedLodScheduleEntry(
                            lodIndex switch
                            {
                                0 => 20.0f,
                                1 => 80.0f,
                                2 => 100.0f,
                                _ => 10000.0f,
                            },
                            components
                                .Select(component =>
                                    new CrossRigidSourceLodSelection(
                                        component.WsModelPath,
                                        Math.Min(
                                            lodIndex,
                                            component.Rigid.ModelList.Length -
                                            1)))
                                .ToArray()))
                    .ToArray();
            }

            var selectedBoundaries =
                SelectBoundedCrossRigidLodBoundaries(
                    unionBoundaries,
                    Math.Min(
                        targetLodCount,
                        unionBoundaries.Count));
            var schedule =
                new List<CrossRigidGeneratedLodScheduleEntry>(
                    selectedBoundaries.Count);
            var previousDistance = 0.0f;
            foreach (var boundary in selectedBoundaries)
            {
                // Sample just past the near edge so each source shows its
                // most detailed LOD anywhere in this generated interval. The
                // source lookup treats distance <= LodDistance + epsilon as
                // inside that LOD, so stepping 2 * epsilon past the edge
                // selects the source LOD that starts at the edge.
                var sampleDistance = previousDistance <= 0.0f
                    ? 0.0f
                    : Math.Min(
                        previousDistance +
                        (2.0f * CrossRigidLodDistanceEpsilon),
                        boundary.Distance);
                var selections = components
                    .Select(component =>
                    {
                        var sourceLodIndex =
                            FindCrossRigidSourceLodAtDistance(
                                component.Rigid,
                                sampleDistance);
                        return sourceLodIndex >= 0
                            ? new CrossRigidSourceLodSelection(
                                component.WsModelPath,
                                sourceLodIndex)
                            : null;
                    })
                    .Where(selection => selection != null)
                    .Cast<CrossRigidSourceLodSelection>()
                    .OrderBy(
                        selection => selection.WsModelPath,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (selections.Length != 0)
                {
                    schedule.Add(
                        new CrossRigidGeneratedLodScheduleEntry(
                            boundary.Distance,
                            selections));
                }

                previousDistance = boundary.Distance;
            }

            return schedule;
        }

        private static List<CrossRigidLodBoundaryCandidate>
            MergeCrossRigidLodBoundaryCandidates(
                IReadOnlyList<CrossRigidLodBoundaryCandidate> candidates)
        {
            var output =
                new List<CrossRigidLodBoundaryCandidate>();
            foreach (var candidate in candidates
                         .OrderBy(candidate => candidate.Distance))
            {
                if (output.Count != 0 &&
                    Math.Abs(
                        output[^1].Distance -
                        candidate.Distance) <=
                    CrossRigidLodDistanceEpsilon)
                {
                    var previous = output[^1];
                    output[^1] = previous with
                    {
                        Distance = Math.Max(
                            previous.Distance,
                            candidate.Distance),
                        Weight =
                            previous.Weight +
                            candidate.Weight,
                    };
                    continue;
                }

                output.Add(candidate);
            }

            return output;
        }

        private static IReadOnlyList<CrossRigidLodBoundaryCandidate>
            SelectBoundedCrossRigidLodBoundaries(
                IReadOnlyList<CrossRigidLodBoundaryCandidate> candidates,
                int targetCount)
        {
            if (targetCount <= 0 ||
                candidates.Count == 0)
            {
                return [];
            }

            if (candidates.Count <= targetCount)
                return candidates.ToArray();

            if (targetCount == 1)
                return [candidates[^1]];

            var finalBoundary = candidates[^1];
            var pool = candidates
                .Take(candidates.Count - 1)
                .ToArray();
            var preFinalTargetCount = targetCount - 1;
            if (pool.Length <= preFinalTargetCount)
            {
                return pool
                    .Append(finalBoundary)
                    .ToArray();
            }

            var selected =
                new HashSet<int>();
            var totalWeight = pool.Sum(candidate =>
                Math.Max(1.0, candidate.Weight));
            if (totalWeight > 0)
            {
                var cumulativeCenters =
                    new double[pool.Length];
                double cumulativeWeight = 0;
                for (var index = 0;
                     index < pool.Length;
                     index++)
                {
                    var weight =
                        Math.Max(1.0, pool[index].Weight);
                    cumulativeCenters[index] =
                        cumulativeWeight +
                        (weight * 0.5);
                    cumulativeWeight += weight;
                }

                for (var slot = 0;
                     slot < preFinalTargetCount;
                     slot++)
                {
                    var targetWeight =
                        totalWeight *
                        ((slot + 0.5) /
                         preFinalTargetCount);
                    var bestIndex = Enumerable
                        .Range(0, pool.Length)
                        .Where(index =>
                            !selected.Contains(index))
                        .OrderBy(index =>
                            Math.Abs(
                                cumulativeCenters[index] -
                                targetWeight))
                        .ThenByDescending(index =>
                            pool[index].Weight)
                        .ThenBy(index =>
                            pool[index].Distance)
                        .First();
                    selected.Add(bestIndex);
                }
            }

            if (selected.Count <
                preFinalTargetCount)
            {
                foreach (var index in Enumerable
                             .Range(0, pool.Length)
                             .Where(index =>
                                 !selected.Contains(index))
                             .OrderByDescending(index =>
                                 pool[index].Weight)
                             .ThenBy(index =>
                                 pool[index].Distance))
                {
                    selected.Add(index);
                    if (selected.Count ==
                        preFinalTargetCount)
                    {
                        break;
                    }
                }
            }

            return selected
                .Select(index => pool[index])
                .Append(finalBoundary)
                .OrderBy(candidate => candidate.Distance)
                .ToArray();
        }

        private static double GetCrossRigidLodTransitionWeight(
            CrossRigidAnalysisComponent component,
            int lodIndex)
        {
            var currentWeight =
                GetCrossRigidLodGeometryWeight(
                    component.Rigid,
                    lodIndex);
            if (lodIndex + 1 >=
                component.Rigid.ModelList.Length)
            {
                return Math.Max(1.0, currentWeight);
            }

            var nextWeight =
                GetCrossRigidLodGeometryWeight(
                    component.Rigid,
                    lodIndex + 1);
            return Math.Max(
                1.0,
                Math.Max(currentWeight, nextWeight));
        }

        private static long GetCrossRigidLodGeometryWeight(
            RmvFile rigid,
            int lodIndex)
        {
            if (lodIndex < 0 ||
                lodIndex >= rigid.ModelList.Length)
            {
                return 0;
            }

            return rigid.ModelList[lodIndex].Sum(model =>
                (long)model.Mesh.VertexList.Length +
                (model.Mesh.IndexList.Length / 3L));
        }

        private static int FindCrossRigidSourceLodAtDistance(
            RmvFile rigid,
            float distance)
        {
            for (var lodIndex = 0;
                 lodIndex < rigid.LodHeaders.Length;
                 lodIndex++)
            {
                if (distance <=
                    rigid.LodHeaders[lodIndex]
                        .LodCameraDistance +
                    CrossRigidLodDistanceEpsilon)
                {
                    return lodIndex;
                }
            }

            // Beyond the last source LOD distance the source keeps showing
            // its last LOD; dropping it would remove the component at range.
            return Math.Min(
                rigid.LodHeaders.Length - 1,
                rigid.ModelList.Length - 1);
        }

        private static byte GetCrossRigidReconciledLodQualityLevel(
            IReadOnlyList<CrossRigidAnalysisComponent> components,
            IReadOnlyList<CrossRigidSourceLodSelection> selections,
            int generatedLodIndex)
        {
            var qualityLevels = selections
                .Select(selection =>
                {
                    var component = components.First(item =>
                        item.WsModelPath.Equals(
                            selection.WsModelPath,
                            StringComparison.OrdinalIgnoreCase));
                    return component.Rigid.LodHeaders[
                        selection.SourceLodIndex].QualityLvl;
                })
                .ToArray();
            if (qualityLevels.Length == 0)
            {
                return GetCrossRigidDefaultLodQualityLevel(
                    generatedLodIndex);
            }

            return qualityLevels.Max();
        }

        private static byte GetCrossRigidDefaultLodQualityLevel(
            int lodIndex)
            => lodIndex == 0 ? (byte)2 : (byte)0;

        private static CrossRigidLodAnalysis AnalyzeCrossRigidLodState(
            BatchState state,
            IReadOnlyList<CrossRigidAnalysisInstanceComponent> instances,
            int generatedLodIndex,
            int scenarioLodIndex,
            byte qualityLevel,
            float cameraDistance,
            IReadOnlyList<CrossRigidSourceLodSelection> sourceLodSelections,
            bool isScenarioRepresentativeState)
        {
            var sourceLodByWsModel =
                sourceLodSelections.ToDictionary(
                    selection => Normalize(selection.WsModelPath),
                    selection => selection.SourceLodIndex,
                    StringComparer.OrdinalIgnoreCase);
            var activeInstances = instances
                .Where(item =>
                    sourceLodByWsModel.ContainsKey(
                        Normalize(item.Component.WsModelPath)))
                .ToArray();
            var activeRigidCount = activeInstances
                .Select(item => item.Component.RigidPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            var partsByIdentity =
                new Dictionary<
                    string,
                    List<CrossRigidAnalysisMeshPart>>(
                    StringComparer.Ordinal);
            var vertexFactory = VertexFactory.Create();
            long generatedVertices = 0;
            long generatedGeometryBytes = 0;

            for (var instanceIndex = 0;
                 instanceIndex < activeInstances.Length;
                 instanceIndex++)
            {
                var item = activeInstances[instanceIndex];
                var component = item.Component;
                var sourceLodIndex =
                    sourceLodByWsModel[
                        Normalize(component.WsModelPath)];
                var models =
                    component.Rigid.ModelList[sourceLodIndex];
                var materialAssignments =
                    component.MaterialAssignments[sourceLodIndex];
                var instanceKey =
                    $"{instanceIndex}\u001f" +
                    $"{item.Instance.AttachmentIdentity}\u001f" +
                    $"{component.WsModelPath}";

                for (var partIndex = 0;
                     partIndex < models.Length;
                     partIndex++)
                {
                    var model = models[partIndex];
                    generatedVertices +=
                        model.Mesh.VertexList.Length;
                    generatedGeometryBytes +=
                        checked(
                            (long)vertexFactory.GetVertexSize(
                                model.Material.BinaryVertexFormat,
                                component.Rigid.Header.Version) *
                            model.Mesh.VertexList.Length);
                    generatedGeometryBytes +=
                        checked(
                            (long)sizeof(ushort) *
                            model.Mesh.IndexList.Length);

                    var identity =
                        GetCrossRigidSourcePartMergeIdentity(
                            state,
                            component.RigidPath,
                            sourceLodIndex,
                            partIndex,
                            model,
                            materialAssignments[partIndex]);
                    if (!partsByIdentity.TryGetValue(
                            identity,
                            out var parts))
                    {
                        parts = [];
                        partsByIdentity[identity] = parts;
                    }

                    parts.Add(
                        new CrossRigidAnalysisMeshPart(
                            instanceKey,
                            component.WsModelPath,
                            component.RigidPath,
                            model.Mesh.VertexList.Length));
                }
            }

            var inputDraws = 0;
            var outputDraws = 0;
            var outputLayoutParts = new List<string>();
            foreach (var (identity, parts) in partsByIdentity
                         .OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                inputDraws += parts
                    .GroupBy(
                        part => part.InstanceKey,
                        StringComparer.Ordinal)
                    .Sum(group =>
                        CountCrossRigidVertexLimitedMergeBins(
                            group.Select(part => part.VertexCount)));
                var outputBins =
                    CountCrossRigidVertexLimitedMergeBins(
                        parts.Select(part => part.VertexCount));
                outputDraws += outputBins;
                outputLayoutParts.Add(string.Join(
                    ":",
                    ContentHash(identity),
                    outputBins,
                    string.Join(
                        ",",
                        parts.Select(part => part.VertexCount)
                            .OrderBy(count => count))));
            }

            return new CrossRigidLodAnalysis(
                generatedLodIndex,
                scenarioLodIndex,
                qualityLevel,
                cameraDistance,
                sourceLodSelections.ToArray(),
                isScenarioRepresentativeState,
                activeInstances.Length,
                activeRigidCount,
                inputDraws,
                outputDraws,
                Math.Max(0, inputDraws - outputDraws),
                isScenarioRepresentativeState
                    ? state.ArmyResidencyModel?.Scenario
                        .GetLodProbability(scenarioLodIndex) ?? 0
                    : 0,
                generatedVertices,
                generatedGeometryBytes,
                ContentHash(string.Join("\u001e", outputLayoutParts)));
        }

        private static int CountCrossRigidVertexLimitedMergeBins(
            IEnumerable<int> vertexCounts)
        {
            var bins = new List<int>();
            foreach (var vertexCount in vertexCounts.OrderByDescending(count => count))
            {
                if (vertexCount > ushort.MaxValue)
                {
                    bins.Add(vertexCount);
                    continue;
                }

                var placed = false;
                for (var index = 0; index < bins.Count; index++)
                {
                    if (bins[index] > ushort.MaxValue ||
                        bins[index] + vertexCount > ushort.MaxValue)
                    {
                        continue;
                    }

                    bins[index] += vertexCount;
                    placed = true;
                    break;
                }

                if (!placed)
                    bins.Add(vertexCount);
            }

            return bins.Count;
        }

        private static double CalculateExpectedCrossRigidArmyDrawSavings(
            BatchState state,
            string vmdPath,
            double visualConfigurationProbability,
            IReadOnlyList<CrossRigidLodAnalysis> lodAnalyses)
        {
            if (state.ArmyResidencyModel == null ||
                visualConfigurationProbability <= 0 ||
                lodAnalyses.Count == 0)
            {
                return 0;
            }

            return CalculateExpectedCrossRigidArmyDrawWeight(
                state,
                vmdPath,
                visualConfigurationProbability,
                lodAnalyses
                    .Where(lod => lod.DrawsSaved > 0)
                    .Select(lod =>
                        (lod.LodProbability, (double)lod.DrawsSaved))
                    .ToArray());
        }

        // Army-model weighting shared by the draw savings and the baseline
        // draws: draws per entity at each scenario LOD, scaled by the share
        // of entities that use this root VMD in the player/opponent armies.
        private static double CalculateExpectedCrossRigidArmyDrawWeight(
            BatchState state,
            string vmdPath,
            double visualConfigurationProbability,
            IReadOnlyList<(double LodProbability, double DrawsPerEntity)>
                lodDraws)
        {
            var model = state.ArmyResidencyModel;
            if (model == null ||
                visualConfigurationProbability <= 0)
            {
                return 0;
            }

            var relevantUnitIds = GetRelevantArmyUnitIds(
                model,
                Array.Empty<string>(),
                [vmdPath]);
            var relevantCultures = GetRelevantArmyCultures(
                model,
                Array.Empty<string>(),
                [vmdPath],
                relevantUnitIds);
            if (relevantUnitIds.Count == 0 || relevantCultures.Count == 0)
                return 0;

            var drawsByCulture = model.UnitsByCultureAndCategory.Keys.ToDictionary(
                culture => culture,
                _ => 0.0,
                StringComparer.OrdinalIgnoreCase);

            foreach (var culture in relevantCultures)
            {
                if (!model.UnitsByCultureAndCategory.TryGetValue(
                        culture,
                        out var unitsByCategory))
                {
                    continue;
                }

                foreach (var (category, slotCount) in model.Scenario.ArmySlotTemplate)
                {
                    if (!unitsByCategory.TryGetValue(
                            category,
                            out var cultureUnits) ||
                        cultureUnits.Count == 0)
                    {
                        continue;
                    }

                    foreach (var lod in lodDraws)
                    {
                        double eliminatedDrawsAcrossResolvedUnits = 0;
                        foreach (var unitId in EnumerateRelevantArmyUnitIds(
                                     cultureUnits,
                                     relevantUnitIds))
                        {
                            if (!model.DirectVmdsByUnitAndRole.TryGetValue(
                                    unitId,
                                    out var vmdsByRole))
                            {
                                continue;
                            }

                            foreach (var (role, directVmds) in vmdsByRole)
                            {
                                if (directVmds.Count == 0 ||
                                    !directVmds.Contains(vmdPath))
                                {
                                    continue;
                                }

                                var entityCount = Math.Max(
                                    1,
                                    model.EntityCountByUnitAndRole
                                        .GetValueOrDefault(unitId)?
                                        .GetValueOrDefault(role, 1) ?? 1);
                                var directVmdProbability =
                                    1.0 / directVmds.Count;
                                eliminatedDrawsAcrossResolvedUnits +=
                                    visualConfigurationProbability *
                                    directVmdProbability *
                                    entityCount *
                                    lod.DrawsPerEntity;
                            }
                        }

                        if (eliminatedDrawsAcrossResolvedUnits <= 0)
                            continue;

                        drawsByCulture[culture] +=
                            lod.LodProbability *
                            slotCount *
                            (eliminatedDrawsAcrossResolvedUnits /
                             cultureUnits.Count);
                    }
                }
            }

            return CombineBattleDrawSavings(
                drawsByCulture,
                model.PlayerCultureWeights,
                model.OpponentCultureWeights);
        }

        // Baseline for the expected-draw metric: the expected mesh-part draws
        // of the analyzed root VMDs, weighted exactly like the draw savings
        // (same army model and weighting helper). Per root VMD and scenario
        // LOD, one entity renders, for each attachment context, the
        // probability-weighted draws of that context's visual configurations
        // (the same attachment-local projection the savings use). Each
        // instance counts its source rigid's draws after same-rigid merging,
        // the footing the savings are measured against. Contexts holding a
        // single WSModel are included, unlike the merge analysis.
        private static CrossRigidAnalysisComponent?
            ResolveCrossRigidBaselineComponent(
                BatchState state,
                string vmdPath,
                string wsModelPath,
                IReadOnlyList<IPackFileContainer> traversalContainers,
                Dictionary<string, RmvFile> rigidCache,
                Dictionary<string, CrossRigidAnalysisComponent?> cache)
        {
            if (cache.TryGetValue(wsModelPath, out var cached))
                return cached;

            if (!TryBuildCrossRigidAnalysisComponent(
                    state,
                    vmdPath,
                    wsModelPath,
                    traversalContainers,
                    rigidCache,
                    out var component,
                    out _))
            {
                state.CrossRigidBaselineUnresolvedWsModelCount++;
                cache[wsModelPath] = null;
                return null;
            }

            cache[wsModelPath] = component;
            return component;
        }

        private static void RecordCrossRigidBaselineExpectedDraws(
            BatchState state,
            string vmdPath,
            CrossRigidAttachmentLocalExpansionResult expansion,
            Func<string, CrossRigidAnalysisComponent?> resolveComponent)
        {
            var baseline = CalculateCrossRigidBaselineExpectedDraws(
                state,
                vmdPath,
                expansion,
                resolveComponent);
            state.CrossRigidBaselineExpectedDrawsByVmd[vmdPath] = baseline;
        }

        private static double CalculateCrossRigidBaselineExpectedDraws(
            BatchState state,
            string vmdPath,
            CrossRigidAttachmentLocalExpansionResult expansion,
            Func<string, CrossRigidAnalysisComponent?> resolveComponent)
        {
            var model = state.ArmyResidencyModel;
            if (model == null ||
                GetRelevantArmyUnitIds(
                        model,
                        Array.Empty<string>(),
                        [vmdPath])
                    .Count == 0)
            {
                return 0;
            }

            var drawsByWsModelAndLod =
                new Dictionary<(string WsModelPath, int LodIndex), int>();
            var lodDraws =
                new List<(double LodProbability, double DrawsPerEntity)>();
            foreach (var (scenarioLodIndex, lodProbability) in model
                         .Scenario.LodDistribution
                         .Where(entry => entry.Value > 0)
                         .OrderBy(entry => entry.Key))
            {
                double drawsPerEntity = 0;
                foreach (var configurations in
                         expansion.ConfigurationsByAttachment.Values)
                {
                    foreach (var configuration in configurations)
                    {
                        if (configuration.Probability <= 0)
                            continue;

                        var configurationDraws = 0;
                        foreach (var instance in configuration.Instances)
                        {
                            var component =
                                resolveComponent(instance.WsModelPath);
                            if (component == null ||
                                component.Rigid.ModelList.Length == 0)
                            {
                                continue;
                            }

                            var sourceLodIndex = Math.Min(
                                scenarioLodIndex,
                                component.Rigid.ModelList.Length - 1);
                            var key = (component.WsModelPath, sourceLodIndex);
                            if (!drawsByWsModelAndLod.TryGetValue(
                                    key,
                                    out var instanceDraws))
                            {
                                instanceDraws =
                                    CountCrossRigidInstanceInputDraws(
                                        state,
                                        component,
                                        sourceLodIndex);
                                drawsByWsModelAndLod[key] = instanceDraws;
                            }

                            configurationDraws += instanceDraws;
                        }

                        drawsPerEntity +=
                            configuration.Probability * configurationDraws;
                    }
                }

                if (drawsPerEntity > 0)
                    lodDraws.Add((lodProbability, drawsPerEntity));
            }

            return CalculateExpectedCrossRigidArmyDrawWeight(
                state,
                vmdPath,
                1.0,
                lodDraws);
        }

        // Draws of one WSModel instance at a source LOD after same-rigid
        // merging: parts sharing a merge identity combine up to the 16-bit
        // vertex limit, as in AnalyzeCrossRigidLodState's input draws.
        private static int CountCrossRigidInstanceInputDraws(
            BatchState state,
            CrossRigidAnalysisComponent component,
            int sourceLodIndex)
        {
            if (sourceLodIndex < 0 ||
                sourceLodIndex >= component.Rigid.ModelList.Length ||
                sourceLodIndex >= component.MaterialAssignments.Length)
            {
                return 0;
            }

            var models = component.Rigid.ModelList[sourceLodIndex];
            var assignments = component.MaterialAssignments[sourceLodIndex];
            var vertexCountsByIdentity =
                new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (var partIndex = 0;
                 partIndex < models.Length &&
                 partIndex < assignments.Length;
                 partIndex++)
            {
                var identity = GetCrossRigidSourcePartMergeIdentity(
                    state,
                    component.RigidPath,
                    sourceLodIndex,
                    partIndex,
                    models[partIndex],
                    assignments[partIndex]);
                if (!vertexCountsByIdentity.TryGetValue(
                        identity,
                        out var vertexCounts))
                {
                    vertexCounts = [];
                    vertexCountsByIdentity[identity] = vertexCounts;
                }

                vertexCounts.Add(models[partIndex].Mesh.VertexList.Length);
            }

            return vertexCountsByIdentity.Values.Sum(
                CountCrossRigidVertexLimitedMergeBins);
        }

        private static string GetCrossRigidWsModelTopologyIdentity(XmlDocument document)
        {
            var clone = new XmlDocument();
            clone.LoadXml(document.OuterXml);

            var geometryNode = clone.SelectSingleNode("/model/geometry");
            geometryNode?.ParentNode?.RemoveChild(geometryNode);

            var materialNodes = clone.SelectNodes("/model/materials/material");
            if (materialNodes != null)
            {
                foreach (XmlNode materialNode in materialNodes.Cast<XmlNode>().ToList())
                    materialNode.ParentNode?.RemoveChild(materialNode);
            }

            return clone.OuterXml;
        }

        private static void RecordCrossRigidAnalysisBlocker(
            BatchState state,
            string reason,
            string example)
        {
            state.CrossRigidAnalysisBlockerCounts[reason] =
                state.CrossRigidAnalysisBlockerCounts
                    .GetValueOrDefault(reason) + 1;
            if (!state.CrossRigidAnalysisBlockerExamples.TryGetValue(
                    reason,
                    out var examples))
            {
                examples = [];
                state.CrossRigidAnalysisBlockerExamples[reason] =
                    examples;
            }

            const int maxExamplesPerReason = 8;
            if (examples.Count < maxExamplesPerReason &&
                !examples.Contains(example, StringComparer.Ordinal))
            {
                examples.Add(example);
            }
        }

        private static void RecordCrossRigidRigidHeaderBlocker(
            BatchState state,
            string vmdPath,
            string attachmentIdentity,
            IReadOnlyList<CrossRigidAnalysisComponent> components,
            string reason)
        {
            const string blocker =
                "Cross-rigid source rigid headers are not merge-compatible";
            RecordCrossRigidAnalysisBlocker(
                state,
                blocker,
                $"{vmdPath} [{attachmentIdentity}]: {reason}");

            state.CrossRigidRigidHeaderBlockerCounts[reason] =
                state.CrossRigidRigidHeaderBlockerCounts
                    .GetValueOrDefault(reason) + 1;

            if (!state.CrossRigidRigidHeaderBlockerRigidCombinations.TryGetValue(
                    reason,
                    out var rigidCombinations))
            {
                rigidCombinations =
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                state.CrossRigidRigidHeaderBlockerRigidCombinations[reason] =
                    rigidCombinations;
            }

            var rigidCombination = string.Join(
                "\u001e",
                components
                    .Select(component => Normalize(component.RigidPath))
                    .Where(path => path.Length != 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
            if (rigidCombination.Length != 0)
                rigidCombinations.Add(rigidCombination);

            if (!state.CrossRigidRigidHeaderBlockerVmdRoots.TryGetValue(
                    reason,
                    out var vmdRoots))
            {
                vmdRoots =
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                state.CrossRigidRigidHeaderBlockerVmdRoots[reason] =
                    vmdRoots;
            }

            var normalizedVmdPath = Normalize(vmdPath);
            if (normalizedVmdPath.Length != 0)
                vmdRoots.Add(normalizedVmdPath);

            var qualityProfiles = components
                .Select(component =>
                    BuildCrossRigidQualityProfile(component.Rigid))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(profile => profile, StringComparer.Ordinal)
                .ToArray();
            if (qualityProfiles.Length > 1)
            {
                RecordCrossRigidHeaderProfileAggregate(
                    state.CrossRigidRigidHeaderQualityProfiles,
                    string.Join(" <> ", qualityProfiles),
                    rigidCombination,
                    normalizedVmdPath);
            }

            var fullLodProfiles = components
                .Select(component =>
                    BuildCrossRigidFullLodProfile(component.Rigid))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(profile => profile, StringComparer.Ordinal)
                .ToArray();
            if (fullLodProfiles.Length > 1)
            {
                RecordCrossRigidHeaderProfileAggregate(
                    state.CrossRigidRigidHeaderFullLodProfiles,
                    string.Join(" <> ", fullLodProfiles),
                    rigidCombination,
                    normalizedVmdPath);
            }
        }

        private static string BuildCrossRigidQualityProfile(RmvFile rigid)
            => $"Q=[{string.Join(
                ",",
                rigid.LodHeaders.Select(header =>
                    Convert.ToString(
                        header.QualityLvl,
                        CultureInfo.InvariantCulture)))}]";

        private static string BuildCrossRigidDistanceProfile(RmvFile rigid)
            => $"D=[{string.Join(
                ",",
                rigid.LodHeaders.Select(header =>
                    header.LodCameraDistance.ToString(
                        "0.###",
                        CultureInfo.InvariantCulture)))}]";

        private static string BuildCrossRigidFullLodProfile(RmvFile rigid)
        {
            var qualityProfile = BuildCrossRigidQualityProfile(rigid);
            var distanceProfile = string.Join(
                ",",
                rigid.LodHeaders.Select(header =>
                    header.LodCameraDistance.ToString(
                        "0.###",
                        CultureInfo.InvariantCulture)));
            return $"{qualityProfile} D=[{distanceProfile}]";
        }

        private static void RecordCrossRigidHeaderProfileAggregate(
            Dictionary<string, CrossRigidHeaderProfileAggregate> aggregates,
            string profilePattern,
            string rigidCombination,
            string vmdPath)
        {
            if (!aggregates.TryGetValue(
                    profilePattern,
                    out var aggregate))
            {
                aggregate = new CrossRigidHeaderProfileAggregate();
                aggregates[profilePattern] = aggregate;
            }

            aggregate.ObservationCount++;
            if (rigidCombination.Length != 0)
                aggregate.RigidCombinations.Add(rigidCombination);
            if (vmdPath.Length != 0)
                aggregate.VmdRoots.Add(vmdPath);
        }

        private static void RecordCrossRigidAnalysisDiagnostic(
            BatchState state,
            string reason,
            string example)
        {
            state.CrossRigidAnalysisDiagnosticCounts[reason] =
                state.CrossRigidAnalysisDiagnosticCounts
                    .GetValueOrDefault(reason) + 1;
            if (!state.CrossRigidAnalysisDiagnosticExamples.TryGetValue(
                    reason,
                    out var examples))
            {
                examples = [];
                state.CrossRigidAnalysisDiagnosticExamples[reason] =
                    examples;
            }

            const int maxExamplesPerReason = 8;
            if (examples.Count < maxExamplesPerReason &&
                !examples.Contains(example, StringComparer.Ordinal))
            {
                examples.Add(example);
            }
        }
    }
}
