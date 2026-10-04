using System.Collections;
using System.Reflection;
using System.Text;
using System.Xml;
using Editors.ImportExport.TextureAtlas;
using Microsoft.Xna.Framework;
using Moq;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.MaterialHeaders;
using Shared.GameFormats.RigidModel.Vertex;

namespace Test.KitbashEditor.Services
{
    public class PackTextureAtlasValueGateTests
    {
        private static string GetStructuralMergePairKey(
            string rigidPath,
            int lodIndex,
            int leftPartIndex,
            int rightPartIndex)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "BuildStructuralMergePairKey",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "PackTextureAtlasBatchService.BuildStructuralMergePairKey was not found.");

            return (string)(method.Invoke(
                    null,
                    [rigidPath, lodIndex, leftPartIndex, rightPartIndex])
                ?? throw new InvalidOperationException(
                    "BuildStructuralMergePairKey returned null."));
        }

        private static object CreateAtlasPlanningTestCandidate(
            string geometryPath,
            int lodIndex,
            int partIndex,
            string? rootVmdPath = null)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var meshKeyType = serviceType.GetNestedType(
                "MeshKey",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MeshKey was not found.");
            var meshKey = meshKeyType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single()
                .Invoke([geometryPath, lodIndex, partIndex]);

            var model = CreateIdentityTestModel("planning");
            var atlasCandidateType = serviceType.GetNestedType(
                "AtlasCandidate",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AtlasCandidate was not found.");
            var constructor = atlasCandidateType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(candidate => candidate.GetParameters().Length > 5);
            var uvBoundsType = serviceType.GetNestedType(
                "UvBounds",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("UvBounds was not found.");
            var uvBounds = uvBoundsType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single()
                .Invoke([0f, 0f, 1f, 1f]);

            var arguments = constructor.GetParameters()
                .Select(parameter =>
                {
                    var type = parameter.ParameterType;
                    if (type == typeof(string))
                    {
                        return parameter.Name switch
                        {
                            "RootVmdPath" => (object?)(rootVmdPath ?? "models\\planning.variantmeshdefinition"),
                            "MaterialPath" => "materials\\planning.xml.material",
                            _ => string.Empty,
                        };
                    }

                    if (type == meshKeyType)
                        return meshKey;
                    if (type == typeof(RmvModel))
                        return model;
                    if (type == typeof(XmlDocument))
                    {
                        var material = new XmlDocument();
                        material.LoadXml("<material />");
                        return material;
                    }
                    if (type == uvBoundsType)
                        return uvBounds;
                    if (type == typeof(double))
                        return 1.0;
                    if (type == typeof(int))
                        return 64;
                    if (type == typeof(bool))
                        return false;
                    if (type == typeof(HashSet<string>))
                        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    if (type.IsGenericType &&
                        type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                    {
                        return Activator.CreateInstance(type);
                    }

                    if (type.IsGenericType &&
                        type.GetGenericTypeDefinition() == typeof(List<>))
                    {
                        return Activator.CreateInstance(type);
                    }

                    return type.IsValueType
                        ? Activator.CreateInstance(type)
                        : null;
                })
                .ToArray();

            return constructor.Invoke(arguments);
        }

        private static object GetAtlasPlanningIdentity(object candidate)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                    "GetAtlasPlanningSourceIdentity",
                    BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "GetAtlasPlanningSourceIdentity was not found.");
            return method.Invoke(null, [candidate])
                ?? throw new InvalidOperationException(
                    "GetAtlasPlanningSourceIdentity returned null.");
        }

        private static IReadOnlyList<IReadOnlyList<object>> CloseAtlasPlanningTestBatches(
            object state,
            params (object First, object? Second)[] batches)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var atlasCandidateType = serviceType.GetNestedType(
                "AtlasCandidate",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AtlasCandidate was not found.");
            var candidateListType = typeof(List<>).MakeGenericType(atlasCandidateType);
            var batchesListType = typeof(List<>).MakeGenericType(candidateListType);
            var candidateBatches = Activator.CreateInstance(batchesListType)
                ?? throw new InvalidOperationException("Could not create candidate batches.");
            foreach (var (first, second) in batches)
            {
                var batch = Activator.CreateInstance(candidateListType)
                    ?? throw new InvalidOperationException("Could not create candidate batch.");
                ((IList)batch).Add(first);
                if (second != null)
                    ((IList)batch).Add(second);
                ((IList)candidateBatches).Add(batch);
            }

            var method = serviceType.GetMethod(
                    "CloseLodAtlasFamilies",
                    BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("CloseLodAtlasFamilies was not found.");
            var closed = (IEnumerable)(method.Invoke(null, [state, candidateBatches])
                ?? throw new InvalidOperationException("CloseLodAtlasFamilies returned null."));
            return closed
                .Cast<IEnumerable>()
                .Select(batch => batch.Cast<object>().ToArray())
                .ToArray();
        }

        private static IReadOnlyList<object> BuildSharedAtlasPageGroupsForTest(
            object state,
            params object[][] batches)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var atlasCandidateType = serviceType.GetNestedType(
                "AtlasCandidate",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AtlasCandidate was not found.");
            var candidateListType = typeof(List<>).MakeGenericType(atlasCandidateType);
            var batchesType = typeof(List<>).MakeGenericType(candidateListType);
            var candidateBatches = Activator.CreateInstance(batchesType)
                ?? throw new InvalidOperationException("Could not create page batches.");
            foreach (var batchCandidates in batches)
            {
                var candidateBatch = Activator.CreateInstance(candidateListType)
                    ?? throw new InvalidOperationException("Could not create page batch.");
                foreach (var candidate in batchCandidates)
                    ((IList)candidateBatch).Add(candidate);
                ((IList)candidateBatches).Add(candidateBatch);
            }

            var method = serviceType.GetMethod(
                    "BuildSharedAtlasPageGroups",
                    BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "BuildSharedAtlasPageGroups was not found.");
            return ((IEnumerable)(method.Invoke(
                    null,
                    [state, candidateBatches, CancellationToken.None, null])
                ?? throw new InvalidOperationException(
                    "BuildSharedAtlasPageGroups returned null.")))
                .Cast<object>()
                .ToArray();
        }

        private static int GetObjectCollectionCount(object instance, string propertyName)
        {
            var value = instance.GetType()
                .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(instance)
                ?? throw new InvalidOperationException(
                    $"Property {propertyName} was not found on {instance.GetType().Name}.");
            return Convert.ToInt32(
                value.GetType().GetProperty("Count")?.GetValue(value)
                ?? throw new InvalidOperationException(
                    $"Property {propertyName} has no Count value."));
        }

        private static void SetAtlasCandidateBounds(
            object candidate,
            float minU,
            float minV,
            float maxU,
            float maxV)
        {
            var candidateType = candidate.GetType();
            var boundsType = candidateType.Assembly
                .GetType(
                    "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService+UvBounds",
                    throwOnError: true)!;
            var bounds = boundsType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single()
                .Invoke([minU, minV, maxU, maxV]);
            candidateType.GetProperty(
                    "Bounds",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.SetValue(candidate, bounds);
        }

        private static void SetAtlasCandidateTexturePath(
            object candidate,
            string slot,
            string path)
        {
            var material = candidate.GetType()
                .GetProperty(
                    "MaterialDocument",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(candidate) as XmlDocument
                ?? throw new InvalidOperationException("Atlas candidate material was not found.");
            EnsureTexturePath(material, slot, path);
        }

        private static object BuildSharedAtlasBatchForTest(
            object state,
            IReadOnlyList<object> candidates,
            bool mergeCompatibleCrops)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                    "BuildSharedAtlasBatch",
                    BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("BuildSharedAtlasBatch was not found.");
            return method.Invoke(
                       null,
                       [state, CreateAtlasCandidateBatch(candidates), mergeCompatibleCrops, false])
                   ?? throw new InvalidOperationException(
                       "BuildSharedAtlasBatch returned null.");
        }

        private static int GetWsMergeGroupCountForAtlasBatches(
            int? leftAtlasBatchId,
            int? rightAtlasBatchId)
        {
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, []);
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var meshKeyType = serviceType.GetNestedType(
                "MeshKey",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MeshKey was not found.");
            var meshKeyConstructor = meshKeyType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single();
            var meshKeys = new[]
            {
                meshKeyConstructor.Invoke(["models\\merge.rigid_model_v2", 0, 0]),
                meshKeyConstructor.Invoke(["models\\merge.rigid_model_v2", 0, 1]),
            };

            var atlasBatchByMesh = (IDictionary)(GetStateProperty<object>(
                    state,
                    "AtlasBatchByMesh")
                ?? throw new InvalidOperationException("AtlasBatchByMesh was missing."));
            if (leftAtlasBatchId.HasValue)
                atlasBatchByMesh.Add(meshKeys[0], leftAtlasBatchId.Value);
            if (rightAtlasBatchId.HasValue)
                atlasBatchByMesh.Add(meshKeys[1], rightAtlasBatchId.Value);

            var method = serviceType.GetMethod(
                    "BuildMeshMergeGroups",
                    BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "BuildMeshMergeGroups was not found.");
            var groups = method.Invoke(
                    null,
                    [
                        state,
                        new[]
                        {
                            CreateIdentityTestModel("same"),
                            CreateIdentityTestModel("same"),
                        },
                        0,
                        "models\\merge.rigid_model_v2",
                        Array.Empty<string>(),
                        new Dictionary<string, string[][]>(),
                        false,
                        false,
                    ]) as IEnumerable
                ?? throw new InvalidOperationException(
                    "BuildMeshMergeGroups returned null.");

            return groups.Cast<object>().Count();
        }

        private static int GetMeshMergeBlockerCountForAtlasBatches(
            int leftAtlasBatchId,
            int rightAtlasBatchId,
            string reason)
        {
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, []);
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var meshKeyType = serviceType.GetNestedType(
                "MeshKey",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MeshKey was not found.");
            var meshKeyConstructor = meshKeyType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single();
            var meshKeys = new[]
            {
                meshKeyConstructor.Invoke(["models\\merge.rigid_model_v2", 0, 0]),
                meshKeyConstructor.Invoke(["models\\merge.rigid_model_v2", 0, 1]),
            };

            var atlasBatchByMesh = (IDictionary)(GetStateProperty<object>(
                    state,
                    "AtlasBatchByMesh")
                ?? throw new InvalidOperationException("AtlasBatchByMesh was missing."));
            atlasBatchByMesh.Add(meshKeys[0], leftAtlasBatchId);
            atlasBatchByMesh.Add(meshKeys[1], rightAtlasBatchId);

            var method = serviceType.GetMethod(
                    "AnalyzeMeshMergeBlockers",
                    BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "AnalyzeMeshMergeBlockers was not found.");
            var wsModelPath = "models\\merge.wsmodel";
            method.Invoke(
                null,
                [
                    state,
                    "models\\merge.rigid_model_v2",
                    0,
                    new[]
                    {
                        CreateIdentityTestModel("same"),
                        CreateIdentityTestModel("same"),
                    },
                    new[] { wsModelPath },
                    new Dictionary<string, string[][]>(StringComparer.OrdinalIgnoreCase)
                    {
                        [wsModelPath] = new[]
                        {
                            new[] { "materials\\shared.xml", "materials\\shared.xml" },
                        },
                    },
                    false,
                ]);

            var blockerCounts = (IDictionary)(GetStateProperty<object>(
                    state,
                    "MeshMergeBlockerCounts")
                ?? throw new InvalidOperationException("MeshMergeBlockerCounts was missing."));
            return blockerCounts.Contains(reason)
                ? Convert.ToInt32(blockerCounts[reason])
                : 0;
        }

        private static bool FitsWithinLargestReplacedPageForTest(
            IReadOnlyList<(int Width, int Height)> originalPages,
            (int Width, int Height) combinedPage)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var footprintType = serviceType.GetNestedType(
                "AtlasPageFootprint",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AtlasPageFootprint was not found.");
            var footprintConstructor = footprintType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(constructor => constructor.GetParameters().Length == 2);

            object CreateFootprint((int Width, int Height) dimensions)
                => footprintConstructor.Invoke(
                [
                    new Dictionary<string, (int Width, int Height)>
                    {
                        ["t_xml_base_colour"] = dimensions,
                    },
                    (long)dimensions.Width * dimensions.Height,
                ]);

            var pageListType = typeof(List<>).MakeGenericType(footprintType);
            var pages = Activator.CreateInstance(pageListType)
                ?? throw new InvalidOperationException("Could not create footprint list.");
            foreach (var originalPage in originalPages)
                ((IList)pages).Add(CreateFootprint(originalPage));

            var method = serviceType.GetMethod(
                    "FitsWithinLargestReplacedPage",
                    BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "FitsWithinLargestReplacedPage was not found.");
            return (bool)(method.Invoke(
                    null,
                    [pages, CreateFootprint(combinedPage)])
                ?? throw new InvalidOperationException(
                    "FitsWithinLargestReplacedPage returned null."));
        }

        private static void AddAtlasBatchDiagnosticForTest(
            object state,
            int batchId,
            IReadOnlyList<object> candidates,
            long pixelCost)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var atlasCandidateType = serviceType.GetNestedType(
                "AtlasCandidate",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AtlasCandidate was not found.");
            var candidateListType = typeof(List<>).MakeGenericType(atlasCandidateType);
            var batchCandidates = Activator.CreateInstance(candidateListType)
                ?? throw new InvalidOperationException("Could not create diagnostic candidates.");
            foreach (var candidate in candidates)
                ((IList)batchCandidates).Add(candidate);

            var snapshotType = serviceType.GetNestedType(
                "AtlasBatchDiagnosticSnapshot",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                    "AtlasBatchDiagnosticSnapshot was not found.");
            var snapshot = snapshotType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(constructor => constructor.GetParameters().Length == 2)
                .Invoke([batchCandidates, pixelCost]);
            var diagnostics = (IDictionary)(GetStateProperty<object>(
                    state,
                    "AtlasBatchDiagnostics")
                ?? throw new InvalidOperationException("AtlasBatchDiagnostics was missing."));
            diagnostics.Add(batchId, snapshot);
        }

        private static void AddAtlasPhysicalPageForTest(
            object state,
            IReadOnlyList<object> candidates,
            long pixelCost)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var atlasCandidateType = serviceType.GetNestedType(
                "AtlasCandidate",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AtlasCandidate was not found.");
            var candidateListType = typeof(List<>).MakeGenericType(atlasCandidateType);
            var pageCandidates = Activator.CreateInstance(candidateListType)
                ?? throw new InvalidOperationException("Could not create page candidates.");
            foreach (var candidate in candidates)
                ((IList)pageCandidates).Add(candidate);

            var pageType = serviceType.GetNestedType(
                "AtlasPhysicalPageDiagnostic",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                    "AtlasPhysicalPageDiagnostic was not found.");
            var page = pageType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(constructor => constructor.GetParameters().Length == 2)
                .Invoke([pageCandidates, pixelCost]);
            var pages = (IList)(GetStateProperty<object>(
                    state,
                    "AtlasPhysicalPageDiagnostics")
                ?? throw new InvalidOperationException(
                    "AtlasPhysicalPageDiagnostics was missing."));
            pages.Add(page);
        }

        private static object BuildAtlasResidencySummaryForTest(object state)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                    "BuildAtlasResidencySummary",
                    BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "BuildAtlasResidencySummary was not found.");
            return method.Invoke(null, [state])
                ?? throw new InvalidOperationException(
                    "BuildAtlasResidencySummary returned null.");
        }

        private static int GetMergeAffinityScore(
            IReadOnlyCollection<string> preExistingPairs,
            params int[] partIndices)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var meshKeyType = serviceType.GetNestedType(
                "MeshKey",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MeshKey was not found.");
            var meshKeyConstructor = meshKeyType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single();

            var atlasCandidateType = serviceType.GetNestedType(
                "AtlasCandidate",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AtlasCandidate was not found.");
            var atlasCandidateConstructor = atlasCandidateType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(constructor => constructor.GetParameters().Length > 5);

            var meshKeys = partIndices
                .Select(partIndex => meshKeyConstructor.Invoke(
                    ["test.rigid_model_v2", 0, partIndex]))
                .ToArray();
            var atlasCandidates = meshKeys
                .Select(meshKey =>
                {
                    var arguments = atlasCandidateConstructor.GetParameters()
                        .Select(parameter =>
                        {
                            if (parameter.ParameterType == meshKeyType)
                                return meshKey;
                            if (parameter.ParameterType == typeof(string))
                                return string.Empty;
                            if (parameter.ParameterType.IsValueType)
                                return Activator.CreateInstance(parameter.ParameterType);
                            return null;
                        })
                        .ToArray();
                    return atlasCandidateConstructor.Invoke(arguments);
                })
                .ToArray();

            var candidateListType = typeof(List<>).MakeGenericType(atlasCandidateType);
            var batch = Activator.CreateInstance(candidateListType)
                ?? throw new InvalidOperationException("Could not create candidate batch.");
            foreach (var candidate in atlasCandidates)
                ((IList)batch).Add(candidate);

            var batchesListType = typeof(List<>).MakeGenericType(candidateListType);
            var batches = Activator.CreateInstance(batchesListType)
                ?? throw new InvalidOperationException("Could not create candidate batches.");
            ((IList)batches).Add(batch);

            var mergeGroupType = serviceType.GetNestedType(
                "MergeAffinityGroup",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MergeAffinityGroup was not found.");
            var mergeGroupConstructor = mergeGroupType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(constructor => constructor.GetParameters().Length == 4);
            var meshKeyArray = Array.CreateInstance(meshKeyType, meshKeys.Length);
            for (var index = 0; index < meshKeys.Length; index++)
                meshKeyArray.SetValue(meshKeys[index], index);

            var mergeGroup = mergeGroupConstructor.Invoke(
                [
                    meshKeyArray,
                    0,
                    preExistingPairs.ToArray(),
                    Enumerable.Repeat("strict:test", meshKeys.Length).ToArray(),
                ]);
            var mergeGroupListType = typeof(List<>).MakeGenericType(mergeGroupType);
            var mergeGroups = Activator.CreateInstance(mergeGroupListType)
                ?? throw new InvalidOperationException("Could not create affinity groups.");
            ((IList)mergeGroups).Add(mergeGroup);

            var method = serviceType.GetMethods(
                    BindingFlags.NonPublic | BindingFlags.Static)
                .Single(candidate =>
                    candidate.Name == "CalculateMergeAffinityScore" &&
                    candidate.GetParameters().Length == 2);
            return Convert.ToInt32(method.Invoke(null, [batches, mergeGroups]));
        }

        private static int GetWsMergeGroupCount(
            bool includeEmbeddedMaterialIdentity,
            string leftEmbeddedModelName,
            string rightEmbeddedModelName)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "BuildMeshMergeGroups",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "BuildMeshMergeGroups was not found.");

            var models = new[]
            {
                CreateIdentityTestModel(leftEmbeddedModelName),
                CreateIdentityTestModel(rightEmbeddedModelName),
            };
            var groups = method.Invoke(
                    null,
                    [
                        null,
                        models,
                        0,
                        "test.rigid_model_v2",
                        Array.Empty<string>(),
                        new Dictionary<string, string[][]>(),
                        false,
                        includeEmbeddedMaterialIdentity,
                    ]) as IEnumerable
                ?? throw new InvalidOperationException(
                    "BuildMeshMergeGroups returned null.");

            return groups.Cast<object>().Count();
        }

        private static RmvModel CreateIdentityTestModel(string embeddedModelName)
        {
            var commonHeader = RmvCommonHeader.CreateDefault();
            commonHeader.ModelTypeFlag = ModelMaterialEnum.weighted;

            return new RmvModel
            {
                CommonHeader = commonHeader,
                Material = new WeightedMaterial
                {
                    ModelName = embeddedModelName,
                },
                Mesh = new RmvMesh
                {
                    VertexList = [],
                    IndexList = [],
                },
            };
        }

        private static (object Candidate, RmvModel Model) CreateAtlasConsumerTestCandidate(
            string geometryPath,
            bool wsModelMaterialConsumer)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var meshKeyType = serviceType.GetNestedType(
                "MeshKey",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MeshKey was not found.");
            var meshKey = meshKeyType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single()
                .Invoke([geometryPath, 0, 0]);

            var usageType = serviceType.GetNestedType(
                "WsUsage",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("WsUsage was not found.");
            var usage = usageType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(constructor => constructor.GetParameters().Length == 5)
                .Invoke(
                [
                    wsModelMaterialConsumer ? @"models\shared.wsmodel" : string.Empty,
                    null,
                    null,
                    @"materials\shared.xml",
                    wsModelMaterialConsumer ? null : geometryPath,
                ]);
            var usagesType = typeof(List<>).MakeGenericType(usageType);
            var usages = Activator.CreateInstance(usagesType)
                ?? throw new InvalidOperationException("Could not create usage list.");
            ((IList)usages).Add(usage);

            var model = CreateIdentityTestModel("embedded");
            model.Mesh.VertexList =
            [
                new CommonVertex
                {
                    Uv = new Vector2(0.25f, 0.75f),
                    BoneIndex = [],
                    BoneWeight = [],
                },
            ];

            var atlasCandidateType = serviceType.GetNestedType(
                "AtlasCandidate",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AtlasCandidate was not found.");
            var constructor = atlasCandidateType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(candidate => candidate.GetParameters().Length > 5);
            var arguments = constructor.GetParameters()
                .Select(parameter =>
                {
                    var type = parameter.ParameterType;
                    if (type == typeof(string))
                        return (object?)"models\\root.variantmeshdefinition";
                    if (type == meshKeyType)
                        return meshKey;
                    if (type == typeof(RmvModel))
                        return model;
                    if (type == usagesType)
                        return usages;
                    if (type == typeof(XmlDocument))
                        return new XmlDocument();
                    if (type == typeof(HashSet<string>))
                        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (type.IsGenericType &&
                        type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                    {
                        return Activator.CreateInstance(type);
                    }

                    if (type.IsGenericType &&
                        type.GetGenericTypeDefinition() == typeof(List<>))
                    {
                        return Activator.CreateInstance(type);
                    }

                    if (type.IsValueType)
                        return Activator.CreateInstance(type);

                    return null;
                })
                .ToArray();

            return (
                constructor.Invoke(arguments),
                model);
        }

        private static int FilterAtlasConsumerTestCandidates(
            object state,
            object candidate)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var atlasCandidateType = serviceType.GetNestedType(
                "AtlasCandidate",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AtlasCandidate was not found.");
            var candidatesType = typeof(List<>).MakeGenericType(atlasCandidateType);
            var candidates = Activator.CreateInstance(candidatesType)
                ?? throw new InvalidOperationException("Could not create candidate list.");
            ((IList)candidates).Add(candidate);

            var method = serviceType.GetMethods(
                    BindingFlags.NonPublic | BindingFlags.Static)
                .Single(candidateMethod =>
                    candidateMethod.Name == "FilterAtlasCandidatesWithUnrewritableMaterialConsumers" &&
                    candidateMethod.GetParameters().Length == 2);
            var filtered = (IEnumerable)(method.Invoke(null, [state, candidates])
                ?? throw new InvalidOperationException("Atlas candidate filter returned null."));
            return filtered.Cast<object>().Count();
        }

        private static bool IsMeshConsumerAssetPath(string path)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "IsMeshConsumerAssetPath",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "IsMeshConsumerAssetPath was not found.");

            return (bool)(method.Invoke(null, [path])
                ?? throw new InvalidOperationException(
                    "IsMeshConsumerAssetPath returned null."));
        }

        private static object CreateTraversalBatchState(
            IPackFileContainer source,
            IReadOnlyList<IPackFileContainer> loadedContainers)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var batchStateType = serviceType.GetNestedType(
                "BatchState",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("BatchState was not found.");
            var constructor = batchStateType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single();

            var packFileService = new Mock<IPackFileService>();
            packFileService
                .Setup(service => service.GetAllPackfileContainers())
                .Returns(loadedContainers.ToList());

            var state = constructor.Invoke(
                [
                    source,
                    source,
                    packFileService.Object,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    false,
                    false,
                    false,
                ]);

            batchStateType.GetProperty("GameplayTraversalContainers")!
                .SetValue(state, loadedContainers);
            return state;
        }

        private static void AddCachedTraversalVmd(
            object state,
            string vmdPath,
            string modelPath)
        {
            var documents = state.GetType()
                .GetProperty(
                    "VmdDocuments",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(state) as IDictionary
                ?? throw new InvalidOperationException("VmdDocuments was not found.");

            documents.Add(
                NormalizeTestPath(vmdPath),
                new Shared.GameFormats.Vmd.VariantMeshDefinition.VariantMesh
                {
                    ModelReference = modelPath,
                });
        }

        private static void AddCachedTraversalVmdWithChildReference(
            object state,
            string vmdPath,
            string modelPath,
            string childVmdPath)
        {
            var documents = state.GetType()
                .GetProperty(
                    "VmdDocuments",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(state) as IDictionary
                ?? throw new InvalidOperationException("VmdDocuments was not found.");

            documents.Add(
                NormalizeTestPath(vmdPath),
                new Shared.GameFormats.Vmd.VariantMeshDefinition.VariantMesh
                {
                    ModelReference = modelPath,
                    ChildSlots =
                    [
                        new Shared.GameFormats.Vmd.VariantMeshDefinition.SLOT
                        {
                            ChildReferences =
                            [
                                new Shared.GameFormats.Vmd.VariantMeshDefinition.VariantMeshRef
                                {
                                    Reference = childVmdPath,
                                },
                            ],
                        },
                    ],
                });
        }

        private static object CreateHealthyResolutionWithRosterVmd(string vmdPath)
            => CreateHealthyResolutionWithRosterVmds(
                (vmdPath, "InfantryMissile"));

        private static object CreateHealthyResolutionWithRosterVmds(
            params (string VmdPath, string Category)[] rosterEntries)
            => CreateResolutionWithRosterVmds(
                true,
                rosterEntries);

        private static object CreateUnhealthyResolutionWithRosterVmd(string vmdPath)
            => CreateResolutionWithRosterVmds(
                false,
                (vmdPath, "InfantryMissile"));

        private static object CreateResolutionWithRosterVmds(
            bool isGameplayResolutionHealthy,
            params (string VmdPath, string Category)[] rosterEntries)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var componentType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3ResolvedUnitComponent",
                throwOnError: true)!;
            var visualType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3ResolvedUnitVisual",
                throwOnError: true)!;
            var countsType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3UnitVisualCounts",
                throwOnError: true)!;
            var resolutionType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3UnitCategoryResolution",
                throwOnError: true)!;
            var roleType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3UnitVisualRole",
                throwOnError: true)!;
            var assetStateType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3VisualAssetState",
                throwOnError: true)!;
            var categoryType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3ArmyUnitCategory",
                throwOnError: true)!;
            var scenarioType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3ArmyVisualScenario",
                throwOnError: true)!;
            var scenario = scenarioType.GetProperty(
                               "Default",
                               BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                           ?.GetValue(null)
                       ?? throw new InvalidOperationException(
                           "The default army visual scenario was not found.");

            var componentConstructor = componentType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(constructor => constructor.GetParameters().Length == 6);
            var counts = countsType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(constructor => constructor.GetParameters().Length == 4)
                .Invoke([1, 0, 0, 0]);
            var visualConstructor = visualType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(constructor => constructor.GetParameters().Length == 6);
            var rosterListType = typeof(List<>).MakeGenericType(visualType);
            var rosterUnits = Activator.CreateInstance(rosterListType)
                ?? throw new InvalidOperationException("Could not create roster units.");

            for (var index = 0; index < rosterEntries.Length; index++)
            {
                var entry = rosterEntries[index];
                var component = componentConstructor.Invoke(
                    [
                        Enum.Parse(roleType, "Men"),
                        entry.VmdPath,
                        true,
                        Enum.Parse(assetStateType, "Live"),
                        0,
                        1.0,
                    ]);
                var componentListType = typeof(List<>).MakeGenericType(componentType);
                var components = Activator.CreateInstance(componentListType)
                    ?? throw new InvalidOperationException("Could not create roster components.");
                ((IList)components).Add(component);

                var visual = visualConstructor.Invoke(
                    [
                        $"main:main_roster_test_{index}",
                        $"main_roster_test_{index}",
                        $"land_roster_test_{index}",
                        Enum.Parse(categoryType, entry.Category),
                        counts,
                        components,
                    ]);
                ((IList)rosterUnits).Add(visual);
            }

            var constructor = resolutionType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(constructor => constructor.GetParameters().Length > 10);
            var arguments = constructor.GetParameters()
                .Select(parameter =>
                {
                    if (parameter.ParameterType.IsInstanceOfType(rosterUnits) ||
                        parameter.ParameterType.IsAssignableFrom(rosterUnits.GetType()))
                        return (object?)rosterUnits;
                    if (parameter.ParameterType == typeof(bool))
                        return isGameplayResolutionHealthy;
                    if (parameter.ParameterType == typeof(int))
                        return 0;
                    if (parameter.ParameterType == typeof(string))
                        return string.Empty;
                    if (parameter.ParameterType == scenarioType)
                        return scenario;

                    if (parameter.ParameterType.IsGenericType &&
                        parameter.ParameterType.GetGenericTypeDefinition() ==
                        typeof(IReadOnlyDictionary<,>))
                    {
                        return Activator.CreateInstance(
                            typeof(Dictionary<,>).MakeGenericType(
                                parameter.ParameterType.GetGenericArguments()));
                    }

                    if (parameter.ParameterType.IsGenericType &&
                        parameter.ParameterType.GetGenericTypeDefinition() ==
                        typeof(IReadOnlyList<>))
                    {
                        return Array.CreateInstance(
                            parameter.ParameterType.GetGenericArguments()[0],
                            0);
                    }

                    return null;
                })
                .ToArray();

            var resolution = constructor.Invoke(arguments);
            var usageType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3UnitCategoryUsage",
                throwOnError: true)!;
            var usageConstructor = usageType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(candidate => candidate.GetParameters().Length == 11);
            var usageListType = typeof(List<>).MakeGenericType(usageType);
            var usagesByVmd = (IDictionary)(resolutionType.GetProperty("UsagesByVmd")
                ?.GetValue(resolution)
                ?? throw new InvalidOperationException("UsagesByVmd was not found."));
            for (var index = 0; index < rosterEntries.Length; index++)
            {
                var entry = rosterEntries[index];
                var usage = usageConstructor.Invoke(
                [
                    entry.VmdPath,
                    $"main_roster_test_{index}",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    1,
                    Enum.Parse(categoryType, entry.Category),
                    Enum.Parse(roleType, "Men"),
                    1,
                    counts,
                ]);
                var usageList = Activator.CreateInstance(usageListType)
                    ?? throw new InvalidOperationException("Could not create resolution usages.");
                ((IList)usageList).Add(usage);
                usagesByVmd[NormalizeTestPath(entry.VmdPath)] = usageList;
            }

            return resolution;
        }

        private static void PopulateArmyResidencyModel(object state)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "BuildArmyResidencyModel",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "BuildArmyResidencyModel was not found.");
            var resolution = GetStateProperty<object>(state, "UnitCategoryResolution");
            var model = method.Invoke(null, [state, resolution, true])
                ?? throw new InvalidOperationException(
                    "BuildArmyResidencyModel returned null.");
            SetStateProperty(state, "ArmyResidencyModel", model);
        }

        private static bool ArmyResidencyAssetIndexContains(
            object state,
            string assetPath)
        {
            var model = GetStateProperty<object>(state, "ArmyResidencyModel");
            var index = model.GetType()
                .GetProperty(
                    "UnitIdsByAssetPath",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(model) as IDictionary
                ?? throw new InvalidOperationException(
                    "UnitIdsByAssetPath was not found.");
            return index.Contains(NormalizeTestPath(assetPath));
        }

        private static void IndexImmutableMeshMergeConsumers(
            object state,
            params string[] roots)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "IndexImmutableMeshMergeConsumers",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "IndexImmutableMeshMergeConsumers was not found.");

            method.Invoke(null, [state, roots, CancellationToken.None]);
        }

        private static (bool IsBlocked, string Reason) GetStructuralMergeBlockDecision(
            object state,
            string rigidPath)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "TryGetStructuralMergeBlockReason",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "TryGetStructuralMergeBlockReason was not found.");
            var arguments = new object?[] { state, rigidPath, null };
            var isBlocked = (bool)(method.Invoke(null, arguments)
                ?? throw new InvalidOperationException(
                    "TryGetStructuralMergeBlockReason returned null."));

            return (isBlocked, arguments[2]?.ToString() ?? string.Empty);
        }

        private static bool StateDictionaryContains(
            object state,
            string propertyName,
            string key)
        {
            var collection = state.GetType()
                .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(state) as IDictionary
                ?? throw new InvalidOperationException(
                    $"State dictionary {propertyName} was not found.");

            return collection.Contains(key);
        }

        private static int GetStateCollectionCount(object state, string propertyName)
        {
            var collection = state.GetType()
                .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(state)
                ?? throw new InvalidOperationException(
                    $"State collection {propertyName} was not found.");
            return Convert.ToInt32(
                collection.GetType().GetProperty("Count")?.GetValue(collection)
                ?? throw new InvalidOperationException(
                    $"State collection {propertyName} has no Count property."));
        }

        private static void SetStateProperty(
            object state,
            string propertyName,
            object value)
        {
            state.GetType()
                .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.SetValue(state, value);
        }

        private static T GetStateProperty<T>(
            object state,
            string propertyName)
            => (T)(state.GetType()
                .GetProperty(
                    propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(state)
                ?? throw new InvalidOperationException(
                    $"State property {propertyName} was not found."));

        private static T GetRecordProperty<T>(
            object record,
            string propertyName)
            => (T)(record.GetType()
                .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(record)
                ?? throw new InvalidOperationException(
                    $"Record property {propertyName} was not found."));

        private static string[] GetResolutionRosterVmdPaths(object state)
        {
            var resolution = GetStateProperty<object>(state, "UnitCategoryResolution");
            var roster = (IEnumerable)(resolution.GetType()
                .GetProperty("RosterUnits")
                ?.GetValue(resolution)
                ?? throw new InvalidOperationException("RosterUnits was not found."));

            return roster
                .Cast<object>()
                .SelectMany(unit => ((IEnumerable)(unit.GetType()
                        .GetProperty("Components")
                        ?.GetValue(unit)
                        ?? throw new InvalidOperationException("Roster components were not found.")))
                    .Cast<object>())
                .Where(component => (bool)(component.GetType()
                    .GetProperty("IsVariantMeshDefinition")
                    ?.GetValue(component)
                    ?? false))
                .Select(component => component.GetType()
                    .GetProperty("AssetPath")
                    ?.GetValue(component)
                    ?.ToString() ?? string.Empty)
                .ToArray();
        }

        private static bool GameplayDependencyIndexContains(
            object state,
            string dictionaryProperty,
            string key,
            string value)
        {
            var index = GetStateProperty<object>(state, "GameplayMeshDependencyIndex");
            var dictionary = index.GetType()
                .GetProperty(
                    dictionaryProperty,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(index) as IDictionary
                ?? throw new InvalidOperationException(
                    $"Gameplay dependency dictionary {dictionaryProperty} was not found.");
            var normalizedKey = NormalizeTestPath(key);
            if (!dictionary.Contains(normalizedKey))
                return false;

            return ((IEnumerable)dictionary[normalizedKey]!)
                .Cast<object>()
                .Select(item => NormalizeTestPath(item.ToString() ?? string.Empty))
                .Contains(
                    NormalizeTestPath(value),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static object GetGameplayDependencyIndex(object state)
            => GetStateProperty<object>(state, "GameplayMeshDependencyIndex");

        private static string[] GetGameplayDependencyRootKinds(
            object state,
            string rootPath)
        {
            var index = GetGameplayDependencyIndex(state);
            var dictionary = index.GetType()
                .GetProperty(
                    "RootKindsByPath",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(index) as IDictionary
                ?? throw new InvalidOperationException(
                    "Gameplay dependency root origins were not found.");
            var normalizedRootPath = NormalizeTestPath(rootPath);
            if (!dictionary.Contains(normalizedRootPath))
                return Array.Empty<string>();

            return ((IEnumerable)dictionary[normalizedRootPath]!)
                .Cast<object>()
                .Select(value => value.ToString() ?? string.Empty)
                .ToArray();
        }

        private static object[] GetGameplayDependencyFailureDetails(object state)
        {
            var index = GetGameplayDependencyIndex(state);
            var details = index.GetType()
                .GetProperty(
                    "FailureDetails",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(index) as IEnumerable
                ?? throw new InvalidOperationException(
                    "Gameplay dependency failure details were not found.");
            return details.Cast<object>().ToArray();
        }

        private static int GetGameplayDependencyIndexCollectionCount(
            object state,
            string propertyName)
        {
            var index = GetGameplayDependencyIndex(state);
            var collection = index.GetType()
                .GetProperty(
                    propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(index) as IEnumerable
                ?? throw new InvalidOperationException(
                    $"Gameplay dependency index collection {propertyName} was not found.");
            return collection.Cast<object>().Count();
        }

        private static string GetRecordPropertyString(
            object record,
            string propertyName)
            => record.GetType()
                .GetProperty(
                    propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(record)
                ?.ToString() ?? string.Empty;

        private static object[] GetGameplayDiscoveryBlocks(object state)
        {
            var blocks = state.GetType()
                .GetProperty(
                    "ConsumerDiscoveryBlocks",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(state) as IEnumerable
                ?? throw new InvalidOperationException(
                    "Consumer discovery blocks were not found.");
            return blocks.Cast<object>().ToArray();
        }

        private static string GetGameplayMeshDependencyDiagnostics(object state)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "AppendGameplayMeshDependencyDiagnostics",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "AppendGameplayMeshDependencyDiagnostics was not found.");
            var builder = new StringBuilder();
            method.Invoke(null, [builder, state]);
            return builder.ToString();
        }

        private static bool IsPreAtlasStructuralMergeGroup(
            object state,
            string rigidPath,
            int lodIndex,
            params int[] partIndices)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "IsPreAtlasStructuralMergeGroup",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "IsPreAtlasStructuralMergeGroup was not found.");

            return (bool)(method.Invoke(null, [state, rigidPath, lodIndex, partIndices])
                ?? throw new InvalidOperationException(
                    "IsPreAtlasStructuralMergeGroup returned null."));
        }

        private static Mock<IPackFileContainer> CreateTraversalContainer(
            bool isCaPackFile,
            IReadOnlyDictionary<string, PackFile> files)
        {
            var normalizedFiles = files.ToDictionary(
                entry => NormalizeTestPath(entry.Key),
                entry => entry.Value,
                StringComparer.OrdinalIgnoreCase);
            var container = new Mock<IPackFileContainer>();
            container.SetupGet(value => value.IsCaPackFile).Returns(isCaPackFile);
            container
                .Setup(value => value.ContainsFile(It.IsAny<string>()))
                .Returns((string path) => normalizedFiles.ContainsKey(NormalizeTestPath(path)));
            container
                .Setup(value => value.FindFile(It.IsAny<string>()))
                .Returns((string path) => normalizedFiles.GetValueOrDefault(NormalizeTestPath(path)));
            return container;
        }

        private static string NormalizeTestPath(string path)
            => path.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();

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

        private static double CalculateScenarioRetirement(
            double bcnBytes,
            double currentResidentProbability,
            double proposedResidentProbability)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "CalculateAtlasValueGateScenarioRetirement",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "CalculateAtlasValueGateScenarioRetirement was not found.");

            return (double)(method.Invoke(
                    null,
                    [bcnBytes, currentResidentProbability, proposedResidentProbability])
                ?? throw new InvalidOperationException(
                    "CalculateAtlasValueGateScenarioRetirement returned null."));
        }

        private static double AccumulateScenarioRetirement(
            long bcnBytes,
            double accumulatedRetiredBytes,
            double incrementalRetiredBytes)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "AccumulateAtlasValueGateScenarioRetirement",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "AccumulateAtlasValueGateScenarioRetirement was not found.");

            return (double)(method.Invoke(
                    null,
                    [bcnBytes, accumulatedRetiredBytes, incrementalRetiredBytes])
                ?? throw new InvalidOperationException(
                    "AccumulateAtlasValueGateScenarioRetirement returned null."));
        }

        private static object CreateSourceTextureResidencyDiagnostic(
            string texturePath,
            long bcnBytes,
            bool mappingResolved,
            double currentResidentProbability,
            double proposedResidentProbability,
            double currentExpectedResidentBytes,
            double proposedExpectedResidentBytes,
            double expectedRetiredBytes)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var diagnosticType = serviceType.GetNestedType(
                "AtlasValueGateSourceTextureResidencyDiagnostic",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                    "AtlasValueGateSourceTextureResidencyDiagnostic was not found.");
            var constructor = diagnosticType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(candidate => candidate.GetParameters().Length == 16);
            return constructor.Invoke(
            [
                texturePath,
                bcnBytes,
                false,
                false,
                false,
                2,
                0,
                1,
                mappingResolved,
                currentResidentProbability,
                proposedResidentProbability,
                currentExpectedResidentBytes,
                proposedExpectedResidentBytes,
                expectedRetiredBytes,
                new[] { "models\\before.wsmodel" },
                new[] { "models\\after.wsmodel" },
            ]);
        }

        private static void RecordSourceTextureResidencyDiagnostics(
            object state,
            params object[] diagnostics)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var diagnosticType = serviceType.GetNestedType(
                "AtlasValueGateSourceTextureResidencyDiagnostic",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                    "AtlasValueGateSourceTextureResidencyDiagnostic was not found.");
            var diagnosticsType = typeof(List<>).MakeGenericType(diagnosticType);
            var diagnosticList = Activator.CreateInstance(diagnosticsType)
                ?? throw new InvalidOperationException(
                    "Could not create source-texture diagnostic list.");
            foreach (var diagnostic in diagnostics)
                ((IList)diagnosticList).Add(diagnostic);

            var method = serviceType.GetMethod(
                "RecordAtlasValueGateSourceTextureResidencyDiagnostics",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "RecordAtlasValueGateSourceTextureResidencyDiagnostics was not found.");
            method.Invoke(null, [state, diagnosticList]);
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

        private static bool ArePackReferencesRetired(
            bool hasDirectSourcePackVmdReference,
            int packReferenceCount,
            int rewrittenPackReferenceCount)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "IsAtlasValueGatePackReferenceRetired",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "PackTextureAtlasBatchService.IsAtlasValueGatePackReferenceRetired was not found.");

            return (bool)(method.Invoke(
                null,
                [hasDirectSourcePackVmdReference, packReferenceCount, rewrittenPackReferenceCount])
                ?? throw new InvalidOperationException(
                    "IsAtlasValueGatePackReferenceRetired returned null."));
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
            int packReferenceRetiredTextureCount,
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
                    packReferenceRetiredTextureCount,
                    physicallyRetiredTextureCount,
                    physicallyRetiredBcnBytes,
                    scenarioDisplacedTextureCount,
                    expectedRetiredTextureEquivalents,
                    expectedRetiredBcnBytes,
                ])
                ?? throw new InvalidOperationException(
                    "HasSufficientTextureOnlyRetirement returned null."));
        }


        private static double CombineBattleDrawSavings(
            IReadOnlyDictionary<string, double> drawsByCulture,
            IReadOnlyDictionary<string, double> playerCultureWeights,
            IReadOnlyDictionary<string, double> opponentCultureWeights)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "CombineBattleDrawSavings",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "PackTextureAtlasBatchService.CombineBattleDrawSavings was not found.");

            return (double)(method.Invoke(
                null,
                [drawsByCulture, playerCultureWeights, opponentCultureWeights])
                ?? throw new InvalidOperationException(
                    "CombineBattleDrawSavings returned null."));
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

        private static string CanonicalizeFaction3MaskParameter(XmlDocument material)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "CanonicalizeFaction3MaskParameter",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "PackTextureAtlasBatchService.CanonicalizeFaction3MaskParameter was not found.");

            return method.Invoke(null, [material])?.ToString()
                ?? throw new InvalidOperationException(
                    "CanonicalizeFaction3MaskParameter returned null.");
        }

        private static void EnsureTexturePath(XmlDocument material, string slot, string path)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "EnsureTexturePath",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "PackTextureAtlasBatchService.EnsureTexturePath was not found.");

            method.Invoke(null, [material, slot, path]);
        }

        private static (long PixelCost, bool HasMaskBcnCost) GetGeneratedAtlasCosts(
            object state,
            object candidate)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var candidateBatch = CreateAtlasCandidateBatch([candidate]);

            var pixelMethod = serviceType.GetMethods(
                    BindingFlags.NonPublic | BindingFlags.Static)
                .Single(method =>
                    method.Name == "TryGetGeneratedAtlasPixelCost" &&
                    method.GetParameters().Length == 5);
            var pixelArguments = new object?[]
            {
                state,
                candidateBatch,
                0L,
                false,
                false,
            };
            if (!(bool)(pixelMethod.Invoke(null, pixelArguments)
                ?? throw new InvalidOperationException(
                    "TryGetGeneratedAtlasPixelCost returned null.")))
            {
                throw new InvalidOperationException(
                    "TryGetGeneratedAtlasPixelCost could not create a plan.");
            }

            var bcnMethod = serviceType.GetMethods(
                    BindingFlags.NonPublic | BindingFlags.Static)
                .Single(method =>
                    method.Name == "TryGetGeneratedAtlasBcnCost" &&
                    method.GetParameters().Length == 4);
            var bcnArguments = new object?[]
            {
                state,
                candidateBatch,
                0L,
                new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
            };
            if (!(bool)(bcnMethod.Invoke(null, bcnArguments)
                ?? throw new InvalidOperationException(
                    "TryGetGeneratedAtlasBcnCost returned null.")))
            {
                throw new InvalidOperationException(
                    "TryGetGeneratedAtlasBcnCost could not create a plan.");
            }

            var bcnBySlot = (IDictionary)(bcnArguments[3]
                ?? throw new InvalidOperationException(
                    "TryGetGeneratedAtlasBcnCost did not return slot costs."));
            return
            (
                Convert.ToInt64(pixelArguments[2]),
                bcnBySlot.Contains("t_xml_mask")
            );
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

        private static object CreateMaterialMergeDiagnosticSnapshot(
            IReadOnlyDictionary<string, string> textureAssignments)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var snapshotType = serviceType.GetNestedType(
                "MaterialMergeDiagnosticSnapshot",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                    "MaterialMergeDiagnosticSnapshot was not found.");
            var constructor = snapshotType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(candidate => candidate.GetParameters().Length == 6);

            return constructor.Invoke(
                [
                    "rendering",
                    "shaders\\weighted4_character.xml.shader",
                    "textures",
                    "non-texture",
                    new Dictionary<string, string>(
                        textureAssignments,
                        StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ]);
        }

        private static string[] GetDifferingMaterialTextureSlots(
            object state,
            object left,
            object right)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "GetDifferingMaterialTextureSlots",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "GetDifferingMaterialTextureSlots was not found.");

            return ((IEnumerable)(method.Invoke(null, [state, left, right])
                    ?? throw new InvalidOperationException(
                        "GetDifferingMaterialTextureSlots returned null.")))
                .Cast<string>()
                .ToArray();
        }

        private static int GetProspectiveTextureMergeDraws(
            params string[] preAtlasMaterialIdentities)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Single(candidate =>
                    candidate.Name == "CalculateProspectiveTextureMergeDraws" &&
                    candidate.GetParameters().Length == 1 &&
                    candidate.GetParameters()[0].ParameterType ==
                    typeof(IReadOnlyList<string>));

            return Convert.ToInt32(method.Invoke(null, [preAtlasMaterialIdentities]));
        }

        private static object CreateAffinityTestCandidate(
            string geometryPath,
            int partIndex,
            string materialPath,
            string materialXml,
            int? atlasWidth = null,
            int? atlasHeight = null,
            bool requiresMaskAtlas = false)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var meshKeyType = serviceType.GetNestedType(
                "MeshKey",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MeshKey was not found.");
            var meshKey = meshKeyType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single()
                .Invoke([geometryPath, 0, partIndex]);

            var model = CreateIdentityTestModel("embedded");
            model.Mesh.VertexList =
            [
                new CommonVertex
                {
                    Uv = new Vector2(0.25f, 0.75f),
                    BoneIndex = [],
                    BoneWeight = [],
                },
            ];
            if (atlasWidth.HasValue || atlasHeight.HasValue)
            {
                model.Mesh.VertexList =
                [
                    new CommonVertex
                    {
                        Uv = new Vector2(0f, 0f),
                        BoneIndex = [],
                        BoneWeight = [],
                    },
                    new CommonVertex
                    {
                        Uv = new Vector2(1f, 0f),
                        BoneIndex = [],
                        BoneWeight = [],
                    },
                    new CommonVertex
                    {
                        Uv = new Vector2(0f, 1f),
                        BoneIndex = [],
                        BoneWeight = [],
                    },
                ];
                model.Mesh.IndexList = [0, 1, 2];
            }

            var usageType = serviceType.GetNestedType(
                "WsUsage",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("WsUsage was not found.");
            var usage = usageType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(constructor => constructor.GetParameters().Length == 5)
                .Invoke(
                [
                    @"models\shared.wsmodel",
                    null,
                    null,
                    materialPath,
                    null,
                ]);
            var usagesType = typeof(List<>).MakeGenericType(usageType);
            var usages = Activator.CreateInstance(usagesType)
                ?? throw new InvalidOperationException("Could not create usage list.");
            ((IList)usages).Add(usage);

            var material = new XmlDocument();
            material.LoadXml(materialXml);
            var atlasCandidateType = serviceType.GetNestedType(
                "AtlasCandidate",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AtlasCandidate was not found.");
            var constructor = atlasCandidateType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(candidate => candidate.GetParameters().Length > 5);
            var arguments = constructor.GetParameters()
                .Select(parameter =>
                {
                    var type = parameter.ParameterType;
                    var name = parameter.Name ?? string.Empty;
                    if (type == typeof(string))
                    {
                        if (name.Contains("material", StringComparison.OrdinalIgnoreCase))
                            return (object?)materialPath;
                        if (name.Contains("root", StringComparison.OrdinalIgnoreCase))
                        {
                            return @"models\root.variantmeshdefinition";
                        }

                        return string.Empty;
                    }

                    if (type == meshKeyType)
                        return meshKey;
                    if (type == typeof(RmvModel))
                        return model;
                    if (type == usagesType)
                        return usages;
                    if (type == typeof(XmlDocument))
                        return material;
                    if (type == typeof(int))
                    {
                        if (name.Contains("width", StringComparison.OrdinalIgnoreCase))
                            return atlasWidth ?? 1;
                        if (name.Contains("height", StringComparison.OrdinalIgnoreCase))
                            return atlasHeight ?? 1;

                        return name.Contains("part", StringComparison.OrdinalIgnoreCase)
                            ? partIndex
                            : 0;
                    }

                    if (type == typeof(double))
                        return 1.0;
                    if (type == typeof(float))
                        return 1.0f;
                    if (type == typeof(bool))
                    {
                        return name.Contains(
                            "requiresMaskAtlas",
                            StringComparison.OrdinalIgnoreCase) &&
                            requiresMaskAtlas;
                    }
                    if (type == typeof(HashSet<string>))
                    {
                        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }

                    if (type.IsGenericType &&
                        type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                    {
                        return Activator.CreateInstance(type);
                    }

                    if (type.IsGenericType &&
                        type.GetGenericTypeDefinition() == typeof(List<>))
                    {
                        return Activator.CreateInstance(type);
                    }

                    if (type.IsValueType)
                        return Activator.CreateInstance(type);

                    return null;
                })
                .ToArray();

            var candidate = constructor.Invoke(arguments);
            if (atlasWidth.HasValue || atlasHeight.HasValue)
            {
                if (!atlasWidth.HasValue || !atlasHeight.HasValue)
                {
                    throw new ArgumentException(
                        "Atlas width and height must be provided together.");
                }

                var resolvedChannels = (HashSet<string>)(atlasCandidateType
                    .GetProperty("ResolvedChannels")
                    ?.GetValue(candidate)
                    ?? throw new InvalidOperationException(
                        "AtlasCandidate.ResolvedChannels was not found."));
                resolvedChannels.Add("t_xml_base_colour");

                var boundsProperty = atlasCandidateType.GetProperty("Bounds")
                    ?? throw new InvalidOperationException(
                        "AtlasCandidate.Bounds was not found.");
                var boundsConstructor = boundsProperty.PropertyType
                    .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Single(constructor => constructor.GetParameters().Length == 4);
                boundsProperty.SetValue(
                    candidate,
                    boundsConstructor.Invoke([0f, 0f, 1f, 1f]));

                atlasCandidateType.GetProperty("Width")?.SetValue(
                    candidate,
                    atlasWidth.Value);
                atlasCandidateType.GetProperty("Height")?.SetValue(
                    candidate,
                    atlasHeight.Value);

                var channelDimensions = (IDictionary)(atlasCandidateType
                    .GetProperty("ChannelDimensions")
                    ?.GetValue(candidate)
                    ?? throw new InvalidOperationException(
                        "AtlasCandidate.ChannelDimensions was not found."));
                channelDimensions["t_xml_base_colour"] =
                    (atlasWidth.Value, atlasHeight.Value);
            }

            return candidate;
        }

        private static (int GroupCount, int[] MeshCounts, int[] ProspectiveDraws)
            GetMergeAffinitySummary(
                object state,
                params object[] candidates)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var atlasCandidateType = serviceType.GetNestedType(
                "AtlasCandidate",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AtlasCandidate was not found.");
            var candidatesType = typeof(List<>).MakeGenericType(atlasCandidateType);
            var candidateList = Activator.CreateInstance(candidatesType)
                ?? throw new InvalidOperationException("Could not create candidate list.");
            foreach (var candidate in candidates)
                ((IList)candidateList).Add(candidate);

            var method = serviceType.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Single(candidateMethod =>
                    candidateMethod.Name == "BuildMergeAffinityGroups" &&
                    candidateMethod.GetParameters().Length == 2);
            var groups = ((IEnumerable)(method.Invoke(
                    null,
                    [state, candidateList])
                ?? throw new InvalidOperationException(
                    "BuildMergeAffinityGroups returned null.")))
                .Cast<object>()
                .ToArray();
            var groupType = serviceType.GetNestedType(
                "MergeAffinityGroup",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MergeAffinityGroup was not found.");

            return (
                groups.Length,
                groups.Select(group => ((IEnumerable)(groupType.GetProperty("Meshes")!
                        .GetValue(group)!
                    )).Cast<object>().Count()).ToArray(),
                groups.Select(group => Convert.ToInt32(
                    groupType.GetProperty("ProspectiveTextureMergeDraws")!
                        .GetValue(group))).ToArray());
        }

        private static (int[] CandidateCounts, int[] ProspectiveDraws)
            GetTextureOnlyMergeSubsetShapes(
                string[] preAtlasMaterialIdentities)
        {
            var candidates = preAtlasMaterialIdentities
                .Select((_, index) => CreateAtlasConsumerTestCandidate(
                    $"test{index}.rigid_model_v2",
                    false).Candidate)
                .ToArray();
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var atlasCandidateType = serviceType.GetNestedType(
                "AtlasCandidate",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AtlasCandidate was not found.");
            var candidateListType = typeof(List<>).MakeGenericType(atlasCandidateType);
            var candidateList = Activator.CreateInstance(candidateListType)
                ?? throw new InvalidOperationException("Could not create candidate list.");
            foreach (var candidate in candidates)
                ((IList)candidateList).Add(candidate);

            var meshKeyType = serviceType.GetNestedType(
                "MeshKey",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MeshKey was not found.");
            var meshKeys = Array.CreateInstance(meshKeyType, candidates.Length);
            var keyProperty = atlasCandidateType.GetProperty("Key")
                ?? throw new InvalidOperationException("AtlasCandidate.Key was not found.");
            for (var index = 0; index < candidates.Length; index++)
                meshKeys.SetValue(keyProperty.GetValue(candidates[index]), index);

            var groupType = serviceType.GetNestedType(
                "MergeAffinityGroup",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MergeAffinityGroup was not found.");
            var group = groupType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(constructor => constructor.GetParameters().Length == 4)
                .Invoke(
                [
                    meshKeys,
                    preAtlasMaterialIdentities.Distinct(StringComparer.Ordinal).Count() - 1,
                    Array.Empty<string>(),
                    preAtlasMaterialIdentities,
                ]);

            var planType = serviceType.GetNestedType(
                "AtlasValueGateGroupPlan",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AtlasValueGateGroupPlan was not found.");
            var plan = planType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(constructor => constructor.GetParameters().Length == 2)
                .Invoke([group, candidateList]);
            var method = serviceType.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Single(candidateMethod =>
                    candidateMethod.Name == "BuildTextureOnlyMergeSubsetPlans" &&
                    candidateMethod.GetParameters().Length == 1);
            var plans = ((IEnumerable)(method.Invoke(null, [plan])
                ?? throw new InvalidOperationException(
                    "BuildTextureOnlyMergeSubsetPlans returned null.")))
                .Cast<object>()
                .ToArray();
            var groupProperty = planType.GetProperty("Group")
                ?? throw new InvalidOperationException("AtlasValueGateGroupPlan.Group was not found.");
            var candidatesProperty = planType.GetProperty("Candidates")
                ?? throw new InvalidOperationException(
                    "AtlasValueGateGroupPlan.Candidates was not found.");
            var prospectiveProperty = groupType.GetProperty("ProspectiveTextureMergeDraws")
                ?? throw new InvalidOperationException(
                    "MergeAffinityGroup.ProspectiveTextureMergeDraws was not found.");

            return (
                plans.Select(value => ((IEnumerable)candidatesProperty.GetValue(value)!)
                    .Cast<object>()
                    .Count()).ToArray(),
                plans.Select(value => Convert.ToInt32(
                    prospectiveProperty.GetValue(groupProperty.GetValue(value)!))).ToArray());
        }

        private static object CreateTextureOnlyMergeAffinityGroup(
            IReadOnlyList<object> candidates,
            params string[] preAtlasMaterialIdentities)
        {
            if (candidates.Count != preAtlasMaterialIdentities.Length)
            {
                throw new ArgumentException(
                    "Each candidate must have one pre-atlas material identity.");
            }

            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var atlasCandidateType = serviceType.GetNestedType(
                "AtlasCandidate",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AtlasCandidate was not found.");
            var meshKeyType = serviceType.GetNestedType(
                "MeshKey",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MeshKey was not found.");
            var keyProperty = atlasCandidateType.GetProperty("Key")
                ?? throw new InvalidOperationException("AtlasCandidate.Key was not found.");
            var meshKeys = Array.CreateInstance(meshKeyType, candidates.Count);
            for (var index = 0; index < candidates.Count; index++)
                meshKeys.SetValue(keyProperty.GetValue(candidates[index]), index);

            var groupType = serviceType.GetNestedType(
                "MergeAffinityGroup",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MergeAffinityGroup was not found.");
            return groupType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(constructor => constructor.GetParameters().Length == 4)
                .Invoke(
                [
                    meshKeys,
                    preAtlasMaterialIdentities.Distinct(StringComparer.Ordinal).Count() - 1,
                    Array.Empty<string>(),
                    preAtlasMaterialIdentities,
                ]);
        }

        private static object CreateAtlasValueGateGroupPlan(
            object group,
            IReadOnlyList<object> candidates)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var atlasCandidateType = serviceType.GetNestedType(
                "AtlasCandidate",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AtlasCandidate was not found.");
            var candidateListType = typeof(List<>).MakeGenericType(atlasCandidateType);
            var candidateList = Activator.CreateInstance(candidateListType)
                ?? throw new InvalidOperationException("Could not create candidate list.");
            foreach (var candidate in candidates)
                ((IList)candidateList).Add(candidate);

            var planType = serviceType.GetNestedType(
                "AtlasValueGateGroupPlan",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                    "AtlasValueGateGroupPlan was not found.");
            return planType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(constructor => constructor.GetParameters().Length == 2)
                .Invoke([group, candidateList]);
        }

        private static object[] BuildAtlasValueGatePlansForTest(
            object state,
            IReadOnlyList<object> candidates,
            IReadOnlyList<object> affinityGroups,
            params object[][] atomicFamilies)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var meshKeyType = serviceType.GetNestedType(
                "MeshKey",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MeshKey was not found.");
            var atlasCandidateType = serviceType.GetNestedType(
                "AtlasCandidate",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AtlasCandidate was not found.");
            var keyProperty = atlasCandidateType.GetProperty(
                "Key",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AtlasCandidate.Key was not found.");
            var keys = candidates
                .Select(candidate => keyProperty.GetValue(candidate)
                    ?? throw new InvalidOperationException("Atlas candidate key was null."))
                .ToArray();

            var hashSetType = typeof(HashSet<>).MakeGenericType(meshKeyType);
            var addKey = hashSetType.GetMethod("Add")
                ?? throw new InvalidOperationException("Mesh key set could not add members.");
            var batchKeys = Activator.CreateInstance(hashSetType)
                ?? throw new InvalidOperationException("Could not create batch key set.");
            foreach (var key in keys)
                addKey.Invoke(batchKeys, [key]);

            var candidateDictionaryType = typeof(Dictionary<,>)
                .MakeGenericType(meshKeyType, atlasCandidateType);
            var candidateDictionary = Activator.CreateInstance(candidateDictionaryType)
                as IDictionary
                ?? throw new InvalidOperationException("Could not create candidate dictionary.");
            for (var index = 0; index < candidates.Count; index++)
                candidateDictionary.Add(keys[index], candidates[index]);

            var familyCollection = state.GetType()
                .GetProperty(
                    "LodAwareAtlasFamilies",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(state) as IList
                ?? throw new InvalidOperationException("LOD atlas families were not found.");
            foreach (var atomicFamily in atomicFamilies)
            {
                var family = Activator.CreateInstance(hashSetType)
                    ?? throw new InvalidOperationException("Could not create atomic family.");
                foreach (var candidate in atomicFamily)
                {
                    var key = keyProperty.GetValue(candidate)
                        ?? throw new InvalidOperationException("Atomic family key was null.");
                    addKey.Invoke(family, [key]);
                }

                familyCollection.Add(family);
            }

            var groupType = serviceType.GetNestedType(
                "MergeAffinityGroup",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MergeAffinityGroup was not found.");
            var groupsType = typeof(List<>).MakeGenericType(groupType);
            var groups = Activator.CreateInstance(groupsType) as IList
                ?? throw new InvalidOperationException("Could not create affinity groups.");
            foreach (var affinityGroup in affinityGroups)
                groups.Add(affinityGroup);

            var method = serviceType.GetMethod(
                    "BuildAtlasValueGateGroupPlans",
                    BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "BuildAtlasValueGateGroupPlans was not found.");
            return ((IEnumerable)(method.Invoke(
                    null,
                    [state, groups, candidateDictionary, batchKeys])
                ?? throw new InvalidOperationException(
                    "BuildAtlasValueGateGroupPlans returned null.")))
                .Cast<object>()
                .ToArray();
        }

        private static object CreateAtlasCandidateBatch(
            IReadOnlyList<object> candidates)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var atlasCandidateType = serviceType.GetNestedType(
                "AtlasCandidate",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AtlasCandidate was not found.");
            var candidateListType = typeof(List<>).MakeGenericType(atlasCandidateType);
            var candidateList = Activator.CreateInstance(candidateListType)
                ?? throw new InvalidOperationException("Could not create candidate batch.");
            foreach (var candidate in candidates)
                ((IList)candidateList).Add(candidate);
            return candidateList;
        }

        private static (int BaselineScore, int ProposedScore, bool Preserves)
            EvaluateMergeAffinityContributionsForTest(
            IReadOnlyList<object[]> baselineBatches,
            IReadOnlyList<object[]> proposedBatches,
            IReadOnlyList<object> affinityGroups)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var atlasCandidateType = serviceType.GetNestedType(
                "AtlasCandidate",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AtlasCandidate was not found.");
            var candidateListType = typeof(List<>).MakeGenericType(atlasCandidateType);
            var batchesType = typeof(List<>).MakeGenericType(candidateListType);
            var baseline = Activator.CreateInstance(batchesType)
                ?? throw new InvalidOperationException("Could not create baseline batches.");
            var proposed = Activator.CreateInstance(batchesType)
                ?? throw new InvalidOperationException("Could not create proposed batches.");
            foreach (var batch in baselineBatches)
                ((IList)baseline).Add(CreateAtlasCandidateBatch(batch));
            foreach (var batch in proposedBatches)
                ((IList)proposed).Add(CreateAtlasCandidateBatch(batch));

            var groupType = serviceType.GetNestedType(
                "MergeAffinityGroup",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MergeAffinityGroup was not found.");
            var groupsType = typeof(List<>).MakeGenericType(groupType);
            var groups = Activator.CreateInstance(groupsType)
                ?? throw new InvalidOperationException("Could not create affinity groups.");
            foreach (var group in affinityGroups)
                ((IList)groups).Add(group);

            var method = serviceType.GetMethod(
                    "PreservesMergeAffinityContributions",
                    BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "PreservesMergeAffinityContributions was not found.");
            var preserves = (bool)(method.Invoke(null, [baseline, proposed, groups])
                ?? throw new InvalidOperationException(
                    "PreservesMergeAffinityContributions returned null."));
            var scoreMethod = serviceType.GetMethods(
                    BindingFlags.NonPublic | BindingFlags.Static)
                .Single(candidate =>
                    candidate.Name == "CalculateMergeAffinityScore" &&
                    candidate.GetParameters().Length == 2);
            var baselineScore = Convert.ToInt32(scoreMethod.Invoke(
                null,
                [baseline, groups]));
            var proposedScore = Convert.ToInt32(scoreMethod.Invoke(
                null,
                [proposed, groups]));
            return (baselineScore, proposedScore, preserves);
        }

        private static object CreateExpectedEntitiesByMesh(
            IReadOnlyList<object> candidates,
            string unitId)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var atlasCandidateType = serviceType.GetNestedType(
                "AtlasCandidate",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AtlasCandidate was not found.");
            var meshKeyType = serviceType.GetNestedType(
                "MeshKey",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MeshKey was not found.");
            var categoryType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3ArmyUnitCategory",
                throwOnError: true)!;
            var keyProperty = atlasCandidateType.GetProperty("Key")
                ?? throw new InvalidOperationException("AtlasCandidate.Key was not found.");
            var category = Enum.Parse(categoryType, "InfantryMissile");
            var expectedByUnitType = typeof(Dictionary<,>)
                .MakeGenericType(typeof(string), typeof(double));
            var byCategoryType = typeof(Dictionary<,>)
                .MakeGenericType(categoryType, expectedByUnitType);
            var resultType = typeof(Dictionary<,>)
                .MakeGenericType(meshKeyType, byCategoryType);
            var result = Activator.CreateInstance(resultType)
                ?? throw new InvalidOperationException(
                    "Could not create expected-entities dictionary.");

            foreach (var candidate in candidates)
            {
                var expectedByUnit = Activator.CreateInstance(expectedByUnitType)
                    ?? throw new InvalidOperationException(
                        "Could not create expected unit dictionary.");
                ((IDictionary)expectedByUnit)[unitId] = 1.0;
                var byCategory = Activator.CreateInstance(byCategoryType)
                    ?? throw new InvalidOperationException(
                        "Could not create expected category dictionary.");
                ((IDictionary)byCategory)[category] = expectedByUnit;
                ((IDictionary)result)[keyProperty.GetValue(candidate)!] = byCategory;
            }

            return result;
        }

        private static void AddCandidateUsageToState(
            object state,
            object candidate)
        {
            var usages = (IDictionary)(GetStateProperty<object>(state, "Usages")
                ?? throw new InvalidOperationException("State Usages was not found."));
            var candidateType = candidate.GetType();
            var key = candidateType.GetProperty("Key")?.GetValue(candidate)
                ?? throw new InvalidOperationException("AtlasCandidate.Key was not found.");
            var candidateUsages = candidateType.GetProperty("Usages")?.GetValue(candidate)
                ?? throw new InvalidOperationException("AtlasCandidate.Usages was not found.");
            usages[key] = candidateUsages;
        }

        private static object CreateEmptyAtlasValueGateResidencyEstimate()
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var residencyType = serviceType.GetNestedType(
                "AtlasValueGateResidencyEstimate",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                    "AtlasValueGateResidencyEstimate was not found.");
            return residencyType.GetProperty(
                       "Empty",
                       BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                   ?.GetValue(null)
               ?? throw new InvalidOperationException(
                   "AtlasValueGateResidencyEstimate.Empty was not found.");
        }

        private static object CreatePendingTextureOnlyMergeAttachment(
            object plan)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var attachmentType = serviceType.GetNestedType(
                "AtlasValueGatePendingTextureMergeAttachment",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                    "AtlasValueGatePendingTextureMergeAttachment was not found.");
            return attachmentType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(constructor => constructor.GetParameters().Length == 3)
                .Invoke(
                [
                    plan,
                    CreateEmptyAtlasValueGateResidencyEstimate(),
                    "synthetic broad batch rejection for subset attachment regression",
                ]);
        }

        private static object CreateTextureOnlyMergeAttachmentBatches(
            object hostBatch)
        {
            var batchesType = typeof(List<>).MakeGenericType(hostBatch.GetType());
            var batches = Activator.CreateInstance(batchesType)
                ?? throw new InvalidOperationException("Could not create accepted batches.");
            ((IList)batches).Add(hostBatch);
            return batches;
        }

        private static void RecordAcceptedAtlasTestBatch(
            object state,
            object hostBatch,
            object expectedEntitiesByMesh)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethods(
                    BindingFlags.NonPublic | BindingFlags.Static)
                .Single(candidate =>
                    candidate.Name == "RecordAtlasValueGateAccepted" &&
                    candidate.GetParameters().Length == 8);
            var acceptanceKindType = serviceType.GetNestedType(
                "AtlasValueGateAcceptanceKind",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                    "AtlasValueGateAcceptanceKind was not found.");
            method.Invoke(
                null,
                [
                    state,
                    hostBatch,
                    CreateEmptyAtlasValueGateResidencyEstimate(),
                    0,
                    0.0,
                    expectedEntitiesByMesh,
                    Enum.Parse(acceptanceKindType, "DrawMerge"),
                    0,
                ]);
        }

        private static void AttachPendingTextureOnlyMergeTestGroup(
            object state,
            object acceptedBatches,
            object affinityGroups,
            object expectedEntitiesByMesh,
            object pendingAttachments)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethods(
                    BindingFlags.NonPublic | BindingFlags.Static)
                .Single(candidate =>
                    candidate.Name == "AttachPendingTextureOnlyMergeGroups" &&
                    candidate.GetParameters().Length == 6);
            method.Invoke(
                null,
                [
                    state,
                    acceptedBatches,
                    affinityGroups,
                    expectedEntitiesByMesh,
                    pendingAttachments,
                    0,
                ]);
        }

        private static bool AcceptedBatchContainsCandidate(
            object acceptedBatches,
            object candidate)
        {
            var firstBatch = ((IList)acceptedBatches)[0]
                ?? throw new InvalidOperationException("No accepted batch was recorded.");
            return ((IEnumerable)firstBatch).Cast<object>().Contains(candidate);
        }

        private static int GetAcceptedBatchCandidateCount(object acceptedBatches)
        {
            var firstBatch = ((IList)acceptedBatches)[0]
                ?? throw new InvalidOperationException("No accepted batch was recorded.");
            return ((IEnumerable)firstBatch).Cast<object>().Count();
        }

        private static int GetAcceptedEconomicsCandidateCount(object state)
        {
            var economics = (IList)(GetStateProperty<object>(
                    state,
                    "AtlasValueGateAcceptedBatchEconomics")
                ?? throw new InvalidOperationException(
                    "Accepted atlas economics were not found."));
            var entry = economics[0]
                ?? throw new InvalidOperationException("No accepted atlas economics were recorded.");
            return Convert.ToInt32(
                entry.GetType().GetProperty("CandidateCount")?.GetValue(entry)
                ?? throw new InvalidOperationException(
                    "AtlasValueGateBatchEconomics.CandidateCount was not found."));
        }

        private static bool HasTextureOnlyMergeNearMiss(
            object state,
            int candidateCount)
        {
            var nearMisses = (IEnumerable)(GetStateProperty<object>(
                    state,
                    "AtlasValueGateTextureOnlyMergeNearMisses")
                ?? throw new InvalidOperationException(
                    "Texture-only merge near misses were not found."));
            return nearMisses.Cast<object>().Any(entry =>
                Convert.ToInt32(
                    entry.GetType().GetProperty("CandidateCount")?.GetValue(entry)) ==
                candidateCount);
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
            int prospectiveTextureMergeDraws,
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
                           prospectiveTextureMergeDraws,
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

        private static IPackFileContainer? FindCachedGameplayTraversalContainer(
            object state,
            IPackFileContainer source,
            IReadOnlyList<IPackFileContainer> loadedContainers,
            string path)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethods(
                    BindingFlags.NonPublic | BindingFlags.Static)
                .Single(candidate =>
                    candidate.Name == "FindGameplayTraversalContainer" &&
                    candidate.GetParameters().Length == 4);

            return (IPackFileContainer?)method.Invoke(
                null,
                [state, source, loadedContainers, path]);
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

        private static bool HasDirectRigidConsumerAtlasConflict(
            object state,
            string geometryPath,
            bool hasWsModelMaterialConsumer)
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var method = serviceType.GetMethod(
                "HasDirectRigidConsumerAtlasConflict",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "HasDirectRigidConsumerAtlasConflict was not found.");

            return (bool)(method.Invoke(
                    null,
                    [state, geometryPath, hasWsModelMaterialConsumer])
                ?? throw new InvalidOperationException(
                    "HasDirectRigidConsumerAtlasConflict returned null."));
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

        [TestCase(16.0, 0.6, 0.0, 9.6)]
        [TestCase(16.0, 1.0, 0.25, 12.0)]
        [TestCase(16.0, 0.25, 0.75, 0.0)]
        public void ScenarioRetirement_UsesResidencyProbabilityDelta(
            double bcnBytes,
            double currentResidentProbability,
            double proposedResidentProbability,
            double expected)
        {
            Assert.That(
                CalculateScenarioRetirement(
                    bcnBytes,
                    currentResidentProbability,
                    proposedResidentProbability),
                Is.EqualTo(expected).Within(0.000001));
        }

        [Test]
        public void PhysicalAndScenarioRetirement_AreParallelCredits()
        {
            const double generatedBytes = 16.0;
            const double physicallyRetiredBytes = 8.0;

            var globalChargeableBytes = GetChargeableBytes(
                generatedBytes,
                physicallyRetiredBytes);
            var scenarioRetiredBytes = CalculateScenarioRetirement(
                bcnBytes: 16.0,
                currentResidentProbability: 0.75,
                proposedResidentProbability: 0.25);
            var scenarioChargeableBytes = GetChargeableBytes(
                generatedBytes,
                scenarioRetiredBytes);

            Assert.Multiple(() =>
            {
                Assert.That(globalChargeableBytes, Is.EqualTo(8.0));
                Assert.That(scenarioRetiredBytes, Is.EqualTo(8.0));
                Assert.That(scenarioChargeableBytes, Is.EqualTo(8.0));
            });
        }

        [TestCase(100L, 0.0, 0.0, 0.0)]
        [TestCase(100L, 0.0, 10.0, 10.0)]
        [TestCase(100L, 10.0, 0.0, 10.0)]
        [TestCase(100L, 80.0, 50.0, 100.0)]
        public void ScenarioRetirementAccumulation_UsesAcceptedIncrementalCreditOnly(
            long bcnBytes,
            double accumulatedRetiredBytes,
            double incrementalRetiredBytes,
            double expected)
        {
            Assert.That(
                AccumulateScenarioRetirement(
                    bcnBytes,
                    accumulatedRetiredBytes,
                    incrementalRetiredBytes),
                Is.EqualTo(expected).Within(0.000001));
        }

        [Test]
        public void SourceTextureDiagnostics_DoNotReconstructCreditAcrossUnresolvedTransition()
        {
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, []);
            var firstTransition = CreateSourceTextureResidencyDiagnostic(
                "textures\\shared.dds",
                bcnBytes: 100,
                mappingResolved: false,
                currentResidentProbability: 1.0,
                proposedResidentProbability: 1.0,
                currentExpectedResidentBytes: 100.0,
                proposedExpectedResidentBytes: 100.0,
                expectedRetiredBytes: 0.0);
            var laterTransition = CreateSourceTextureResidencyDiagnostic(
                "textures\\shared.dds",
                bcnBytes: 100,
                mappingResolved: true,
                currentResidentProbability: 0.1,
                proposedResidentProbability: 0.0,
                currentExpectedResidentBytes: 10.0,
                proposedExpectedResidentBytes: 0.0,
                expectedRetiredBytes: 10.0);

            RecordSourceTextureResidencyDiagnostics(state, firstTransition);
            RecordSourceTextureResidencyDiagnostics(state, laterTransition);

            var diagnostics = (IDictionary)GetStateProperty<object>(
                state,
                "AtlasValueGateSourceTextureResidencyDiagnostics");
            var diagnostic = diagnostics["textures\\shared.dds"]
                ?? throw new InvalidOperationException(
                    "The shared source-texture diagnostic was not recorded.");

            Assert.Multiple(() =>
            {
                Assert.That(
                    GetRecordProperty<double>(diagnostic, "ExpectedRetiredBcnBytes"),
                    Is.EqualTo(10.0));
                Assert.That(
                    GetRecordProperty<double>(diagnostic, "CurrentExpectedResidentBcnBytes"),
                    Is.EqualTo(100.0));
                Assert.That(
                    GetRecordProperty<double>(diagnostic, "ProposedExpectedResidentBcnBytes"),
                    Is.EqualTo(0.0));
                Assert.That(
                    GetRecordProperty<bool>(diagnostic, "ConsumerMappingResolved"),
                    Is.False);
            });
        }

        [TestCase(true, false, 2, 2, true)]
        [TestCase(true, false, 3, 2, false)]
        [TestCase(true, false, 2, 1, false)]
        [TestCase(true, false, 0, 0, false)]
        [TestCase(true, true, 2, 2, false)]
        [TestCase(false, false, 2, 2, false)]
        public void SourceTextureRetirement_RequiresPackOwnershipCompleteReferenceRewriteCoverageAndNoDirectVmdReference(
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
        public void PackLocalRetirement_CanQualifyWhileVanillaReferenceBlocksPhysicalRetirement()
        {
            Assert.That(
                ArePackReferencesRetired(
                    hasDirectSourcePackVmdReference: false,
                    packReferenceCount: 2,
                    rewrittenPackReferenceCount: 2),
                Is.True);
            Assert.That(
                IsSourceTextureRetired(
                    isOwnedBySourcePack: true,
                    hasDirectVmdReference: false,
                    referenceCount: 3,
                    rewrittenReferenceCount: 2),
                Is.False);
        }

        [TestCase(false, 2, 2, true)]
        [TestCase(false, 2, 1, false)]
        [TestCase(true, 2, 2, false)]
        public void PackReferenceRetirement_DoesNotRequireVanillaReferenceRewriteCoverage(
            bool hasDirectSourcePackVmdReference,
            int packReferenceCount,
            int rewrittenPackReferenceCount,
            bool expected)
        {
            Assert.That(
                ArePackReferencesRetired(
                    hasDirectSourcePackVmdReference,
                    packReferenceCount,
                    rewrittenPackReferenceCount),
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
        public void CultureResidency_MissingUnrelatedCultureValueKeepsFullWeightDenominator()
        {
            var probabilities = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["empire"] = 0.5,
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

        [Test]
        public void DrawSavings_UnrelatedOpponentOnlyAddsMirrorCultureSavings()
        {
            var draws = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["empire"] = 10.0,
                ["dwarfs"] = 0.0,
            };
            var playerWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["empire"] = 1.0,
            };
            var opponentWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["empire"] = 0.5,
                ["dwarfs"] = 0.5,
            };

            Assert.That(
                CombineBattleDrawSavings(draws, playerWeights, opponentWeights),
                Is.EqualTo(15.0).Within(0.000001));
        }

        [TestCase(2, 0, 0L, 0, 0.0, 0.0, true)]
        [TestCase(0, 2, 1024L, 0, 0.0, 0.0, true)]
        [TestCase(0, 0, 0L, 2, 1.0, 1024.0, true)]
        [TestCase(0, 0, 0L, 2, 0.99, 1024.0, false)]
        [TestCase(0, 0, 0L, 1, 1.0, 1024.0, false)]
        public void TextureOnlyFallback_AllowsScenarioDisplacementWithoutPhysicalRetirement(
            int packReferenceRetiredTextureCount,
            int physicallyRetiredTextureCount,
            long physicallyRetiredBcnBytes,
            int scenarioDisplacedTextureCount,
            double expectedRetiredTextureEquivalents,
            double expectedRetiredBcnBytes,
            bool expected)
        {
            Assert.That(
                HasSufficientTextureOnlyRetirement(
                    packReferenceRetiredTextureCount,
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
        public void GameplayTraversal_StateCache_ReusesPositiveAndNegativeResolutions()
        {
            const string positivePath =
                @"variantmeshes\variantmeshdefinitions\cached.variantmeshdefinition";
            const string missingPath =
                @"variantmeshes\variantmeshdefinitions\missing.variantmeshdefinition";
            var source = new Mock<IPackFileContainer>();
            var ca = new Mock<IPackFileContainer>();

            source.Setup(container => container.ContainsFile(It.IsAny<string>()))
                .Returns(false);
            source.SetupGet(container => container.IsCaPackFile).Returns(false);
            ca.Setup(container => container.ContainsFile(positivePath)).Returns(true);
            ca.Setup(container => container.ContainsFile(missingPath)).Returns(false);
            ca.SetupGet(container => container.IsCaPackFile).Returns(true);

            var state = CreateTraversalBatchState(source.Object, [ca.Object]);

            var positive = FindCachedGameplayTraversalContainer(
                state,
                source.Object,
                [ca.Object],
                positivePath);
            var positiveCached = FindCachedGameplayTraversalContainer(
                state,
                source.Object,
                [ca.Object],
                positivePath.Replace('\\', '/').ToUpperInvariant());
            var missing = FindCachedGameplayTraversalContainer(
                state,
                source.Object,
                [ca.Object],
                missingPath);
            var missingCached = FindCachedGameplayTraversalContainer(
                state,
                source.Object,
                [ca.Object],
                missingPath.Replace('\\', '/').ToUpperInvariant());

            Assert.Multiple(() =>
            {
                Assert.That(positive, Is.SameAs(ca.Object));
                Assert.That(positiveCached, Is.SameAs(ca.Object));
                Assert.That(missing, Is.Null);
                Assert.That(missingCached, Is.Null);
                Assert.That(
                    (int)state.GetType()
                        .GetProperty("GameplayTraversalContainerResolutionScans")!
                        .GetValue(state)!,
                    Is.EqualTo(2));
                Assert.That(
                    (int)state.GetType()
                        .GetProperty("GameplayTraversalContainerResolutionCacheHits")!
                        .GetValue(state)!,
                    Is.EqualTo(2));
            });

            source.Verify(
                container => container.ContainsFile(It.IsAny<string>()),
                Times.Exactly(2));
            ca.Verify(
                container => container.ContainsFile(It.IsAny<string>()),
                Times.Exactly(2));
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
                prospectiveTextureMergeDraws: 1,
                expectedArmyDrawsEliminated: 0.15,
                globalCostBytes: 1.75 * mib,
                expectedCostBytes: 0.01 * mib,
                acceptedNetBcnBytes: 0,
                proposedNetBcnBytes: 1.75 * mib,
                sourceBcnBytes: 400 * mib);

            Assert.That(decision, Is.EqualTo("Accept"));
        }

        [Test]
        public void ValueGate_TextureOnlyMergeCreditUsesProspectiveDrawCount()
        {
            const double mib = 1024.0 * 1024.0;

            var oneProspectiveDraw = GetTextureOnlyMergeValueGateBudgetDecision(
                scenarioResolved: true,
                prospectiveTextureMergeDraws: 1,
                expectedArmyDrawsEliminated: 0.01,
                globalCostBytes: 4.1 * mib,
                expectedCostBytes: 0,
                acceptedNetBcnBytes: 0,
                proposedNetBcnBytes: 4.1 * mib,
                sourceBcnBytes: 400 * mib);
            var twoProspectiveDraws = GetTextureOnlyMergeValueGateBudgetDecision(
                scenarioResolved: true,
                prospectiveTextureMergeDraws: 2,
                expectedArmyDrawsEliminated: 0.01,
                globalCostBytes: 4.1 * mib,
                expectedCostBytes: 0,
                acceptedNetBcnBytes: 0,
                proposedNetBcnBytes: 4.1 * mib,
                sourceBcnBytes: 400 * mib);

            Assert.Multiple(() =>
            {
                Assert.That(oneProspectiveDraw, Is.EqualTo("TextureOnlyMergeBudgetExceeded"));
                Assert.That(twoProspectiveDraws, Is.EqualTo("Accept"));
            });
        }

        [Test]
        public void ValueGate_TextureOnlyMergeCreditExpandsForHighScenarioCoverage()
        {
            const double mib = 1024.0 * 1024.0;

            var decision = GetTextureOnlyMergeValueGateBudgetDecision(
                scenarioResolved: true,
                prospectiveTextureMergeDraws: 1,
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
                prospectiveTextureMergeDraws: 1,
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
                prospectiveTextureMergeDraws: 1,
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
                    Is.EqualTo(4L * 1024 * 1024));
                Assert.That(
                    GetConstant("MaxNetBcnBytesPerTextureOnlyMergeExpectedArmyDraw"),
                    Is.EqualTo(512L * 1024));
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
        public void Faction3MaskCanonicalization_UsesBlueAndSynthesizesBlackMaskSlot()
        {
            var material = new XmlDocument();
            material.LoadXml(
                "<material><textures>" +
                "<texture><slot version='2'>t_xml_base_colour</slot>" +
                "<source>base.dds</source></texture>" +
                "<texture><slot version='2'>t_xml_material_map</slot>" +
                "<source>material_map.dds</source></texture>" +
                "</textures><params>" +
                "<param><name>faction3_mask_channel</name><type>float4</type>" +
                "<value>0,0,0,0</value></param>" +
                "</params></material>");

            var mode = CanonicalizeFaction3MaskParameter(material);
            EnsureTexturePath(
                material,
                "t_xml_mask",
                @"commontextures\default_black.dds");
            const string maskedMaterial =
                "<material><textures>" +
                "<texture><slot version='2'>t_xml_base_colour</slot>" +
                "<source>base.dds</source></texture>" +
                "<texture><slot version='2'>t_xml_mask</slot>" +
                "<source>commontextures\\default_black.dds</source></texture>" +
                "<texture><slot version='2'>t_xml_material_map</slot>" +
                "<source>material_map.dds</source></texture>" +
                "</textures><params>" +
                "<param><name>faction3_mask_channel</name><type>float4</type>" +
                "<value>0,0,1,0</value></param>" +
                "</params></material>";

            Assert.Multiple(() =>
            {
                Assert.That(mode, Is.EqualTo("ZeroSelector"));
                Assert.That(
                    material.SelectSingleNode(
                        "/material/params/param[name='faction3_mask_channel']/value")?.InnerText,
                    Is.EqualTo("0,0,1,0"));
                Assert.That(
                    material.SelectSingleNode(
                        "/material/textures/texture[slot='t_xml_mask']/source")?.InnerText,
                    Is.EqualTo(@"commontextures\default_black.dds"));
                Assert.That(
                    GetMaterialRenderingIdentity(material.OuterXml),
                    Is.EqualTo(GetMaterialRenderingIdentity(maskedMaterial)));
            });
        }

        [Test]
        public void ForcedConstantOnlyMaskAtlas_IsIncludedInValueGateCosts()
        {
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, []);
            var candidate = CreateAffinityTestCandidate(
                "models\\constant_mask.rigid_model_v2",
                0,
                "materials\\constant_mask.xml",
                "<material><shader>shaders/weighted4_character.xml.shader</shader>" +
                "<textures><texture><slot version='2'>t_xml_base_colour</slot>" +
                "<source>textures\\base.dds</source></texture>" +
                "<texture><slot version='2'>t_xml_mask</slot>" +
                "<source>textures\\mask.dds</source></texture></textures></material>",
                atlasWidth: 16,
                atlasHeight: 16,
                requiresMaskAtlas: true);

            var constantChannels = (IDictionary)(candidate.GetType()
                .GetProperty("ConstantChannels")
                ?.GetValue(candidate)
                ?? throw new InvalidOperationException(
                    "AtlasCandidate.ConstantChannels was not found."));
            constantChannels["t_xml_mask"] = new TextureAtlasConstantColor(0, 0, 0, 0);

            var costs = GetGeneratedAtlasCosts(state, candidate);

            Assert.Multiple(() =>
            {
                Assert.That(
                    costs.PixelCost,
                    Is.GreaterThanOrEqualTo(2L * 16 * 16),
                    "The forced constant-only mask atlas must be included in pixel cost.");
                Assert.That(
                    costs.HasMaskBcnCost,
                    Is.True,
                    "The forced constant-only mask atlas must be included in BCn cost.");
            });
        }

        [Test]
        public void CounterfactualTextureDifference_IgnoresUnresolvedTestMaskPaths()
        {
            var source = new Mock<IPackFileContainer>();
            source.SetupGet(container => container.IsCaPackFile).Returns(false);
            var state = CreateTraversalBatchState(source.Object, []);

            var left = CreateMaterialMergeDiagnosticSnapshot(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["t_xml_base_colour"] = "textures\\left.dds",
                    ["t_xml_mask"] = "variantmeshes\\left\\test_mask.dds",
                });
            var right = CreateMaterialMergeDiagnosticSnapshot(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["t_xml_base_colour"] = "textures\\right.dds",
                    ["t_xml_mask"] = "variantmeshes\\right\\test_mask.dds",
                });

            var differingSlots = GetDifferingMaterialTextureSlots(state, left, right);

            Assert.Multiple(() =>
            {
                Assert.That(differingSlots, Does.Contain("t_xml_base_colour"));
                Assert.That(
                    differingSlots,
                    Does.Not.Contain("t_xml_mask"),
                    "Unresolved test_mask is removed by the real atlas rewrite and must not " +
                    "create a counterfactual texture mismatch.");
            });
        }

        [Test]
        public void ProspectiveTextureMergeDraws_CountOnlyStrictPreAtlasGroupsCollapsed()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    GetProspectiveTextureMergeDraws("same", "same", "different"),
                    Is.EqualTo(1));
                Assert.That(
                    GetProspectiveTextureMergeDraws("same", "same"),
                    Is.EqualTo(0));
                Assert.That(
                    GetProspectiveTextureMergeDraws("a", "b", "c"),
                    Is.EqualTo(2));
            });
        }

        [Test]
        public void TextureOnlyMergeSubsetPlans_ThreeCandidatesEnumeratePairsBeforeTriple()
        {
            var shapes = GetTextureOnlyMergeSubsetShapes(["a", "b", "c"]);

            Assert.Multiple(() =>
            {
                Assert.That(shapes.CandidateCounts, Is.EqualTo([2, 2, 2, 3]));
                Assert.That(shapes.ProspectiveDraws, Is.EqualTo([1, 1, 1, 2]));
            });
        }

        [Test]
        public void TextureOnlyMergeSubsetPlans_FourCandidatesKeepFullFourWayPlan()
        {
            var shapes = GetTextureOnlyMergeSubsetShapes(["a", "b", "c", "d"]);

            Assert.Multiple(() =>
            {
                Assert.That(
                    shapes.CandidateCounts,
                    Is.EqualTo([2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 4]));
                Assert.That(
                    shapes.ProspectiveDraws,
                    Is.EqualTo([1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 3]));
            });
        }

        [Test]
        public void TextureOnlyMergeSubsetPlans_TenCandidatesReserveFourWayOptions()
        {
            var shapes = GetTextureOnlyMergeSubsetShapes(
                ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j"]);

            Assert.Multiple(() =>
            {
                Assert.That(shapes.CandidateCounts.Length, Is.EqualTo(128));
                Assert.That(
                    shapes.CandidateCounts.Count(count => count == 4),
                    Is.EqualTo(32));
                Assert.That(shapes.CandidateCounts[^1], Is.EqualTo(10));
                Assert.That(shapes.ProspectiveDraws[^1], Is.EqualTo(9));
            });
        }

        [Test]
        public void TextureOnlyMergeSubsetPlans_SkipSubsetWithNoProspectiveDrawDelta()
        {
            var shapes = GetTextureOnlyMergeSubsetShapes(["same", "same", "different"]);

            Assert.Multiple(() =>
            {
                Assert.That(shapes.CandidateCounts, Is.EqualTo([2, 2, 3]));
                Assert.That(shapes.ProspectiveDraws, Is.EqualTo([1, 1, 1]));
            });
        }

        [Test]
        public void TextureOnlyMergeSubsetAttachment_RejectsTripleAndAttachesViablePair()
        {
            static string CreateMaterialXml(string texturePath)
                => "<material><name>test</name>" +
                   "<shader>shaders/weighted4_character.xml.shader</shader>" +
                   "<textures><texture><slot>t_xml_base_colour</slot>" +
                   $"<source>{texturePath}</source></texture></textures></material>";

            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, []);
            SetStateProperty(state, "AtlasAllVmdsEnabled", true);
            SetStateProperty(
                state,
                "UnitCategoryResolution",
                CreateHealthyResolutionWithRosterVmds(
                    ("models\\root.variantmeshdefinition", "InfantryMissile")));
            PopulateArmyResidencyModel(state);

            var host = CreateAffinityTestCandidate(
                "models\\host.rigid_model_v2",
                0,
                "materials\\host.xml",
                CreateMaterialXml("textures\\host.dds"),
                atlasWidth: 512,
                atlasHeight: 512);
            var pairLeft = CreateAffinityTestCandidate(
                "models\\shared.rigid_model_v2",
                0,
                "materials\\pair_left.xml",
                CreateMaterialXml("textures\\pair_left.dds"),
                atlasWidth: 512,
                atlasHeight: 512);
            var pairRight = CreateAffinityTestCandidate(
                "models\\shared.rigid_model_v2",
                1,
                "materials\\pair_right.xml",
                CreateMaterialXml("textures\\pair_right.dds"),
                atlasWidth: 512,
                atlasHeight: 512);
            var expensiveThird = CreateAffinityTestCandidate(
                "models\\shared.rigid_model_v2",
                2,
                "materials\\expensive.xml",
                CreateMaterialXml("textures\\expensive.dds"),
                atlasWidth: 2048,
                atlasHeight: 2048,
                requiresMaskAtlas: true);
            var allCandidates = new[] { host, pairLeft, pairRight, expensiveThird };
            foreach (var candidate in allCandidates)
                AddCandidateUsageToState(state, candidate);

            var expectedEntitiesByMesh = CreateExpectedEntitiesByMesh(
                allCandidates,
                "main:main_roster_test_0");
            var hostBatch = CreateAtlasCandidateBatch([host]);
            var acceptedBatches = CreateTextureOnlyMergeAttachmentBatches(hostBatch);
            RecordAcceptedAtlasTestBatch(state, hostBatch, expectedEntitiesByMesh);

            var pendingCandidates = new[] { pairLeft, pairRight, expensiveThird };
            var group = CreateTextureOnlyMergeAffinityGroup(
                pendingCandidates,
                "pre:pair-left",
                "pre:pair-right",
                "pre:expensive");
            var plan = CreateAtlasValueGateGroupPlan(group, pendingCandidates);
            var pendingAttachment = CreatePendingTextureOnlyMergeAttachment(plan);
            var pendingAttachmentsType = typeof(List<>).MakeGenericType(
                pendingAttachment.GetType());
            var pendingAttachments = Activator.CreateInstance(pendingAttachmentsType)
                ?? throw new InvalidOperationException(
                    "Could not create pending texture-only merge attachments.");
            ((IList)pendingAttachments).Add(pendingAttachment);

            var affinityGroupsType = typeof(List<>).MakeGenericType(group.GetType());
            var affinityGroups = Activator.CreateInstance(affinityGroupsType)
                ?? throw new InvalidOperationException("Could not create affinity groups.");
            ((IList)affinityGroups).Add(group);

            AttachPendingTextureOnlyMergeTestGroup(
                state,
                acceptedBatches,
                affinityGroups,
                expectedEntitiesByMesh,
                pendingAttachments);

            Assert.Multiple(() =>
            {
                Assert.That(
                    AcceptedBatchContainsCandidate(acceptedBatches, pairLeft),
                    Is.True,
                    "The viable pair's first candidate should be attached to the accepted atlas.");
                Assert.That(
                    AcceptedBatchContainsCandidate(acceptedBatches, pairRight),
                    Is.True,
                    "The viable pair's second candidate should be attached to the accepted atlas.");
                Assert.That(
                    AcceptedBatchContainsCandidate(acceptedBatches, expensiveThird),
                    Is.False,
                    "The economically rejected triple's expensive candidate must not be attached.");
                Assert.That(GetAcceptedBatchCandidateCount(acceptedBatches), Is.EqualTo(3));
                Assert.That(GetAcceptedEconomicsCandidateCount(state), Is.EqualTo(3));
                Assert.That(
                    GetStateProperty<int>(
                        state,
                        "AtlasValueGateTextureOnlyMergeSubsetOptionsEvaluated"),
                    Is.EqualTo(4),
                    "The three pairs and the full triple must all reach the attachment gate.");
                Assert.That(
                    GetStateProperty<int>(
                        state,
                        "AtlasValueGateTextureOnlyMergeCrossBatchGroupsAccepted"),
                    Is.EqualTo(1));
                Assert.That(
                    HasTextureOnlyMergeNearMiss(state, 3),
                    Is.True,
                    "The full triple should be recorded as an economic near miss.");
            });
        }

        [Test]
        public void MergeAffinity_NormalizedShaderSpellingUsesActualAffinityBucket()
        {
            const string lowerMaterialPath = @"materials\lower.xml";
            const string upperMaterialPath = @"materials\upper.xml";
            const string lowerMaterial =
                "<material><name>first</name>" +
                "<shader>shaders/weighted4_character.xml.shader</shader>" +
                "<textures><texture><slot>t_xml_base_colour</slot>" +
                "<source>VariantMeshes/Foo.dds</source></texture></textures></material>";
            const string upperMaterial =
                "<material><name>second</name>" +
                "<shader>SHADERS\\WEIGHTED4_CHARACTER.XML.SHADER</shader>" +
                "<textures><texture><slot>t_xml_base_colour</slot>" +
                "<source>variantmeshes\\foo.dds</source></texture></textures></material>";
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>
                {
                    [lowerMaterialPath] = PackFile.CreateFromASCII(
                        lowerMaterialPath,
                        lowerMaterial),
                    [upperMaterialPath] = PackFile.CreateFromASCII(
                        upperMaterialPath,
                        upperMaterial),
                });
            var state = CreateTraversalBatchState(source.Object, []);
            var summary = GetMergeAffinitySummary(
                state,
                CreateAffinityTestCandidate(
                    @"models\shared.rigid_model_v2",
                    0,
                    lowerMaterialPath,
                    lowerMaterial),
                CreateAffinityTestCandidate(
                    @"models\shared.rigid_model_v2",
                    1,
                    upperMaterialPath,
                    upperMaterial));

            Assert.Multiple(() =>
            {
                Assert.That(summary.GroupCount, Is.EqualTo(1));
                Assert.That(summary.MeshCounts, Is.EqualTo([2]));
                Assert.That(summary.ProspectiveDraws, Is.EqualTo([0]));
            });
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

        [Test]
        public void StructuralMergePairKey_IsCanonicalForPartOrder()
        {
            Assert.That(
                GetStructuralMergePairKey(
                    @"TEST\BODY.RIGID_MODEL_V2",
                    1,
                    3,
                    2),
                Is.EqualTo(@"test\body.rigid_model_v2|1|2|3"));
        }

        [Test]
        public void AtlasAffinity_CreditsOnlySavingsBeyondPreExistingStructuralComponents()
        {
            var preExistingPair = GetStructuralMergePairKey(
                "test.rigid_model_v2",
                0,
                0,
                1);

            Assert.Multiple(() =>
            {
                Assert.That(
                    GetMergeAffinityScore([preExistingPair], 0, 1),
                    Is.EqualTo(0));
                Assert.That(
                    GetMergeAffinityScore([preExistingPair], 0, 1, 2),
                    Is.EqualTo(1));
                Assert.That(
                    GetMergeAffinityScore([], 0, 1),
                    Is.EqualTo(1));
            });
        }

        [Test]
        public void MixedDirectRigidAndWsModelConsumers_UseEmbeddedMaterialIdentity()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    GetWsMergeGroupCount(
                        includeEmbeddedMaterialIdentity: false,
                        "embedded_a",
                        "embedded_b"),
                    Is.EqualTo(1),
                    "WSModel-only compatibility may ignore redundant embedded weighted textures.");
                Assert.That(
                    GetWsMergeGroupCount(
                        includeEmbeddedMaterialIdentity: true,
                        "embedded_a",
                        "embedded_b"),
                    Is.EqualTo(2),
                    "A direct rigid consumer requires distinct embedded materials to remain distinct.");
                Assert.That(
                    GetWsMergeGroupCount(
                        includeEmbeddedMaterialIdentity: true,
                        "embedded_a",
                        "embedded_a"),
                    Is.EqualTo(1),
                    "Identical embedded materials remain mergeable for mixed consumers.");
            });
        }

        [Test]
        public void MixedDirectRigidAndWsModelConsumers_RejectWsModelAtlasCandidate()
        {
            const string rigidPath = @"models\shared.rigid_model_v2";
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, []);
            var consumers = (IDictionary)(state.GetType()
                .GetProperty(
                    "DirectRigidMeshMergeConsumersByRigid",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(state)
                ?? throw new InvalidOperationException(
                    "DirectRigidMeshMergeConsumersByRigid was not found."));
            consumers.Add(
                rigidPath,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    @"models\direct_consumer.rigid_model_v2",
                });
            SetStateProperty(state, "StructuralMergeConsumerDiscoveryComplete", true);
            var (candidate, model) = CreateAtlasConsumerTestCandidate(
                rigidPath,
                wsModelMaterialConsumer: true);
            var originalUv = model.Mesh.VertexList[0].Uv;
            var filteredCount = FilterAtlasConsumerTestCandidates(state, candidate);

            Assert.Multiple(() =>
            {
                Assert.That(
                    filteredCount,
                    Is.EqualTo(0),
                    "The mixed-consumer candidate must be removed before ProcessBatch.");
                Assert.That(
                    model.Mesh.VertexList[0].Uv,
                    Is.EqualTo(originalUv),
                    "Rejecting the candidate must leave the rigid UV0 unchanged.");
                Assert.That(
                    HasDirectRigidConsumerAtlasConflict(
                        state,
                        rigidPath,
                        hasWsModelMaterialConsumer: true),
                    Is.True,
                    "The WSModel candidate must be rejected before ProcessBatch can rewrite UV0.");
                Assert.That(
                    HasDirectRigidConsumerAtlasConflict(
                        state,
                        rigidPath,
                        hasWsModelMaterialConsumer: false),
                    Is.False,
                    "A direct-only candidate can still rewrite its embedded material atomically.");
                Assert.That(
                    HasDirectRigidConsumerAtlasConflict(
                        state,
                        @"models\unshared.rigid_model_v2",
                        hasWsModelMaterialConsumer: true),
                    Is.False);
            });
        }

        [Test]
        public void ImmutableCaWsModelConsumer_RejectsAtlasCandidate()
        {
            const string rigidPath = @"models\shared.rigid_model_v2";
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, []);
            var consumers = (IDictionary)(state.GetType()
                .GetProperty(
                    "ImmutableMeshMergeConsumersByRigid",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(state)
                ?? throw new InvalidOperationException(
                    "ImmutableMeshMergeConsumersByRigid was not found."));
            consumers.Add(
                rigidPath,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    @"models\ca_consumer.wsmodel",
                });
            SetStateProperty(state, "StructuralMergeConsumerDiscoveryComplete", true);

            var (candidate, model) = CreateAtlasConsumerTestCandidate(
                rigidPath,
                wsModelMaterialConsumer: true);
            var originalUv = model.Mesh.VertexList[0].Uv;
            var filteredCount = FilterAtlasConsumerTestCandidates(state, candidate);

            Assert.Multiple(() =>
            {
                Assert.That(filteredCount, Is.EqualTo(0));
                Assert.That(model.Mesh.VertexList[0].Uv, Is.EqualTo(originalUv));
            });
        }

        [Test]
        public void IncompleteConsumerDiscovery_RejectsAtlasCandidate()
        {
            const string rigidPath = @"models\unknown.rigid_model_v2";
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, []);
            SetStateProperty(state, "StructuralMergeConsumerDiscoveryComplete", false);

            var (candidate, model) = CreateAtlasConsumerTestCandidate(
                rigidPath,
                wsModelMaterialConsumer: true);
            var originalUv = model.Mesh.VertexList[0].Uv;
            var filteredCount = FilterAtlasConsumerTestCandidates(state, candidate);

            Assert.Multiple(() =>
            {
                Assert.That(filteredCount, Is.EqualTo(0));
                Assert.That(model.Mesh.VertexList[0].Uv, Is.EqualTo(originalUv));
            });
        }

        [Test]
        public void DirectAssetConsumerRootFilter_ExcludesNonMeshEngineAssets()
        {
            Assert.Multiple(() =>
            {
                Assert.That(IsMeshConsumerAssetPath(@"models\unit.wsmodel"), Is.True);
                Assert.That(IsMeshConsumerAssetPath(@"models\unit.rigid_model_v2"), Is.True);
                Assert.That(
                    IsMeshConsumerAssetPath(
                        @"variantmeshes\variantmeshdefinitions\unit.variantmeshdefinition"),
                    Is.True);
                Assert.That(IsMeshConsumerAssetPath(@"animations\unit.xml"), Is.False);
            });
        }

        [Test]
        public void StructuralConsumerDiscovery_CaWsModelReferencingSourceRigid_BlocksMerge()
        {
            const string rigidPath = @"models\source.rigid_model_v2";
            const string wsModelPath = @"models\ca_consumer.wsmodel";
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>
                {
                    [rigidPath] = PackFile.CreateFromASCII(rigidPath, string.Empty),
                });
            var ca = CreateTraversalContainer(
                isCaPackFile: true,
                new Dictionary<string, PackFile>
                {
                    [wsModelPath] = PackFile.CreateFromASCII(
                        wsModelPath,
                        $"<model><geometry>{rigidPath}</geometry></model>"),
                });
            var state = CreateTraversalBatchState(source.Object, [ca.Object]);

            IndexImmutableMeshMergeConsumers(state, wsModelPath);
            SetStateProperty(state, "StructuralMergeConsumerDiscoveryComplete", true);

            var decision = GetStructuralMergeBlockDecision(state, rigidPath);

            Assert.Multiple(() =>
            {
                Assert.That(
                    StateDictionaryContains(
                        state,
                        "ImmutableMeshMergeConsumersByRigid",
                        rigidPath),
                    Is.True);
                Assert.That(decision.IsBlocked, Is.True);
                Assert.That(decision.Reason, Does.Contain("immutable gameplay WSModel"));
            });
        }

        [Test]
        public void StructuralConsumerDiscovery_DiagnosticsClassifyMissingSourceDependency()
        {
            const string sourceVmdPath =
                @"variantmeshes\variantmeshdefinitions\source.variantmeshdefinition";
            const string missingModelPath = @"models\missing.wsmodel";
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>
                {
                    [sourceVmdPath] = PackFile.CreateFromASCII(sourceVmdPath, string.Empty),
                });
            var state = CreateTraversalBatchState(source.Object, []);
            AddCachedTraversalVmd(state, sourceVmdPath, missingModelPath);

            IndexImmutableMeshMergeConsumers(state, sourceVmdPath);

            var failure = GetGameplayDependencyFailureDetails(state)
                .Single(detail =>
                    GetRecordPropertyString(detail, "AssetPath") ==
                    NormalizeTestPath(missingModelPath));

            Assert.Multiple(() =>
            {
                Assert.That(
                    GetGameplayDependencyRootKinds(state, sourceVmdPath),
                    Does.Contain("SourcePackVmd"));
                Assert.That(
                    GetRecordPropertyString(failure, "RootPath"),
                    Is.EqualTo(NormalizeTestPath(sourceVmdPath)));
                Assert.That(
                    GetRecordPropertyString(failure, "Reason"),
                    Does.Contain("could not be resolved"));
                Assert.That(
                    GetRecordPropertyString(failure, "AssetScope"),
                    Is.EqualTo("Missing"));
                Assert.That(
                    GetRecordPropertyString(failure, "Disposition"),
                    Is.EqualTo("Blocking"));
            });
        }

        [Test]
        public void StructuralConsumerDiscovery_TrustedMissingWsModel_IsVerifiedMissing()
        {
            const string sourceVmdPath =
                @"variantmeshes\variantmeshdefinitions\source.variantmeshdefinition";
            const string missingWsModelPath = @"models\missing.wsmodel";
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>
                {
                    [sourceVmdPath] = PackFile.CreateFromASCII(sourceVmdPath, string.Empty),
                });
            var ca = CreateTraversalContainer(
                isCaPackFile: true,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, [ca.Object]);
            AddCachedTraversalVmd(state, sourceVmdPath, missingWsModelPath);
            SetStateProperty(
                state,
                "UnitCategoryResolution",
                CreateHealthyResolutionWithRosterVmd(sourceVmdPath));

            IndexImmutableMeshMergeConsumers(state, sourceVmdPath);

            var failure = GetGameplayDependencyFailureDetails(state)
                .Single(detail =>
                    GetRecordPropertyString(detail, "AssetPath") ==
                    NormalizeTestPath(missingWsModelPath));

            Assert.Multiple(() =>
            {
                Assert.That(
                    GetRecordPropertyString(failure, "FailureKind"),
                    Is.EqualTo("MissingReference"));
                Assert.That(
                    GetRecordPropertyString(failure, "Disposition"),
                    Is.EqualTo("VerifiedMissing"));
                Assert.That(
                    GetGameplayDependencyIndexCollectionCount(state, "IncompleteRoots"),
                    Is.EqualTo(0));
                Assert.That(
                    GetStateCollectionCount(
                        state,
                        "StructuralMergeConsumerDiscoveryFailures"),
                    Is.EqualTo(0));
                Assert.That(
                    GetStateProperty<bool>(
                        GetGameplayDependencyIndex(state),
                        "IsComplete"),
                    Is.True);
            });
        }

        [Test]
        public void StructuralConsumerDiscovery_TrustedWsModelMissingRigid_IsVerifiedMissing()
        {
            const string sourceVmdPath =
                @"variantmeshes\variantmeshdefinitions\source.variantmeshdefinition";
            const string wsModelPath = @"models\source.wsmodel";
            const string missingRigidPath = @"models\missing.rigid_model_v2";
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>
                {
                    [sourceVmdPath] = PackFile.CreateFromASCII(sourceVmdPath, string.Empty),
                    [wsModelPath] = PackFile.CreateFromASCII(
                        wsModelPath,
                        $"<model><geometry>{missingRigidPath}</geometry></model>"),
                });
            var ca = CreateTraversalContainer(
                isCaPackFile: true,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, [ca.Object]);
            AddCachedTraversalVmd(state, sourceVmdPath, wsModelPath);
            SetStateProperty(
                state,
                "UnitCategoryResolution",
                CreateHealthyResolutionWithRosterVmd(sourceVmdPath));

            IndexImmutableMeshMergeConsumers(state, sourceVmdPath);

            var failure = GetGameplayDependencyFailureDetails(state)
                .Single(detail =>
                    GetRecordPropertyString(detail, "AssetPath") ==
                    NormalizeTestPath(missingRigidPath));

            Assert.Multiple(() =>
            {
                Assert.That(
                    GetRecordPropertyString(failure, "FailureKind"),
                    Is.EqualTo("MissingReference"));
                Assert.That(
                    GetRecordPropertyString(failure, "Disposition"),
                    Is.EqualTo("VerifiedMissing"));
                Assert.That(
                    GameplayDependencyIndexContains(
                        state,
                        "DependenciesByAssetPath",
                        wsModelPath,
                        missingRigidPath),
                    Is.True);
                Assert.That(
                    GameplayDependencyIndexContains(
                        state,
                        "WsModelConsumersByRigid",
                        missingRigidPath,
                        wsModelPath),
                    Is.False);
                Assert.That(
                    GetGameplayDependencyIndexCollectionCount(state, "IncompleteRoots"),
                    Is.EqualTo(0));
                Assert.That(
                    GetStateProperty<bool>(
                        GetGameplayDependencyIndex(state),
                        "IsComplete"),
                    Is.True);
            });
        }

        [Test]
        public void StructuralConsumerDiscovery_TrustedMissingDirectRigid_IsVerifiedMissing()
        {
            const string missingRigidPath = @"models\missing.rigid_model_v2";
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>());
            var ca = CreateTraversalContainer(
                isCaPackFile: true,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, [ca.Object]);
            SetStateProperty(
                state,
                "UnitCategoryResolution",
                CreateHealthyResolutionWithRosterVmd(missingRigidPath));

            IndexImmutableMeshMergeConsumers(state, missingRigidPath);

            var failure = GetGameplayDependencyFailureDetails(state)
                .Single(detail =>
                    GetRecordPropertyString(detail, "AssetPath") ==
                    NormalizeTestPath(missingRigidPath));

            Assert.Multiple(() =>
            {
                Assert.That(
                    GetRecordPropertyString(failure, "FailureKind"),
                    Is.EqualTo("MissingReference"));
                Assert.That(
                    GetRecordPropertyString(failure, "Disposition"),
                    Is.EqualTo("VerifiedMissing"));
                Assert.That(
                    GetGameplayDependencyIndexCollectionCount(state, "IncompleteRoots"),
                    Is.EqualTo(0));
                Assert.That(
                    GetStateCollectionCount(
                        state,
                        "StructuralMergeConsumerDiscoveryFailures"),
                    Is.EqualTo(0));
            });
        }

        [Test]
        public void StructuralConsumerDiscovery_TrustedMissingRootVmd_IsVerifiedMissing()
        {
            const string missingRootVmdPath =
                @"variantmeshes\variantmeshdefinitions\missing_root.variantmeshdefinition";
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>());
            var ca = CreateTraversalContainer(
                isCaPackFile: true,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, [ca.Object]);
            SetStateProperty(
                state,
                "UnitCategoryResolution",
                CreateHealthyResolutionWithRosterVmd(missingRootVmdPath));

            IndexImmutableMeshMergeConsumers(state, missingRootVmdPath);

            var failure = GetGameplayDependencyFailureDetails(state)
                .Single(detail =>
                    GetRecordPropertyString(detail, "AssetPath") ==
                    NormalizeTestPath(missingRootVmdPath));

            Assert.Multiple(() =>
            {
                Assert.That(
                    GetRecordPropertyString(failure, "FailureKind"),
                    Is.EqualTo("MissingReference"));
                Assert.That(
                    GetRecordPropertyString(failure, "Disposition"),
                    Is.EqualTo("VerifiedMissing"));
                Assert.That(
                    GetGameplayDependencyIndexCollectionCount(state, "IncompleteRoots"),
                    Is.EqualTo(0));
                Assert.That(
                    GetStateProperty<bool>(
                        GetGameplayDependencyIndex(state),
                        "IsComplete"),
                    Is.True);
            });
        }

        [Test]
        public void StructuralConsumerDiscovery_TrustedMissingUnknownRootType_Blocks()
        {
            const string missingRootPath = @"models\missing.not_a_mesh";
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>());
            var ca = CreateTraversalContainer(
                isCaPackFile: true,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, [ca.Object]);
            SetStateProperty(
                state,
                "UnitCategoryResolution",
                CreateHealthyResolutionWithRosterVmd(missingRootPath));

            IndexImmutableMeshMergeConsumers(state, missingRootPath);

            var failure = GetGameplayDependencyFailureDetails(state)
                .Single(detail =>
                    GetRecordPropertyString(detail, "AssetPath") ==
                    NormalizeTestPath(missingRootPath));

            Assert.Multiple(() =>
            {
                Assert.That(
                    GetRecordPropertyString(failure, "FailureKind"),
                    Is.EqualTo("UnknownType"));
                Assert.That(
                    GetRecordPropertyString(failure, "Disposition"),
                    Is.EqualTo("Blocking"));
                Assert.That(
                    GetGameplayDependencyIndexCollectionCount(state, "IncompleteRoots"),
                    Is.EqualTo(1));
                Assert.That(
                    GetStateCollectionCount(
                        state,
                        "StructuralMergeConsumerDiscoveryFailures"),
                    Is.EqualTo(1));
            });
        }

        [Test]
        public void StructuralConsumerDiscovery_TrustedMissingUnknownModelType_Blocks()
        {
            const string sourceVmdPath =
                @"variantmeshes\variantmeshdefinitions\source.variantmeshdefinition";
            const string missingModelPath = @"models\missing.not_a_model";
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>
                {
                    [sourceVmdPath] = PackFile.CreateFromASCII(sourceVmdPath, string.Empty),
                });
            var ca = CreateTraversalContainer(
                isCaPackFile: true,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, [ca.Object]);
            AddCachedTraversalVmd(state, sourceVmdPath, missingModelPath);
            SetStateProperty(
                state,
                "UnitCategoryResolution",
                CreateHealthyResolutionWithRosterVmd(sourceVmdPath));

            IndexImmutableMeshMergeConsumers(state, sourceVmdPath);

            var failure = GetGameplayDependencyFailureDetails(state)
                .Single(detail =>
                    GetRecordPropertyString(detail, "AssetPath") ==
                    NormalizeTestPath(missingModelPath));

            Assert.Multiple(() =>
            {
                Assert.That(
                    GetRecordPropertyString(failure, "FailureKind"),
                    Is.EqualTo("UnknownType"));
                Assert.That(
                    GetRecordPropertyString(failure, "Disposition"),
                    Is.EqualTo("Blocking"));
                Assert.That(
                    GetGameplayDependencyIndexCollectionCount(state, "IncompleteRoots"),
                    Is.EqualTo(1));
            });
        }

        [Test]
        public void StructuralConsumerDiscovery_TrustedMissingUnknownGeometryType_Blocks()
        {
            const string sourceVmdPath =
                @"variantmeshes\variantmeshdefinitions\source.variantmeshdefinition";
            const string wsModelPath = @"models\source.wsmodel";
            const string missingGeometryPath = @"models\source.not_a_rigid";
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>
                {
                    [sourceVmdPath] = PackFile.CreateFromASCII(sourceVmdPath, string.Empty),
                    [wsModelPath] = PackFile.CreateFromASCII(
                        wsModelPath,
                        $"<model><geometry>{missingGeometryPath}</geometry></model>"),
                });
            var ca = CreateTraversalContainer(
                isCaPackFile: true,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, [ca.Object]);
            AddCachedTraversalVmd(state, sourceVmdPath, wsModelPath);
            SetStateProperty(
                state,
                "UnitCategoryResolution",
                CreateHealthyResolutionWithRosterVmd(sourceVmdPath));

            IndexImmutableMeshMergeConsumers(state, sourceVmdPath);

            var failure = GetGameplayDependencyFailureDetails(state)
                .Single(detail =>
                    GetRecordPropertyString(detail, "AssetPath") ==
                    NormalizeTestPath(missingGeometryPath));

            Assert.Multiple(() =>
            {
                Assert.That(
                    GetRecordPropertyString(failure, "FailureKind"),
                    Is.EqualTo("UnknownType"));
                Assert.That(
                    GetRecordPropertyString(failure, "Disposition"),
                    Is.EqualTo("Blocking"));
                Assert.That(
                    GetGameplayDependencyIndexCollectionCount(state, "IncompleteRoots"),
                    Is.EqualTo(1));
                Assert.That(
                    GetStateCollectionCount(
                        state,
                        "StructuralMergeConsumerDiscoveryFailures"),
                    Is.EqualTo(1));
            });
        }

        [Test]
        public void StructuralConsumerDiscovery_TrustedMissingUnknownChildType_Blocks()
        {
            const string sourceVmdPath =
                @"variantmeshes\variantmeshdefinitions\source.variantmeshdefinition";
            const string sourceWsModelPath = @"models\source.wsmodel";
            const string unknownChildPath =
                @"variantmeshes\variantmeshdefinitions\missing.not_a_vmd";
            const string rigidPath = @"models\source.rigid_model_v2";
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>
                {
                    [sourceVmdPath] = PackFile.CreateFromASCII(sourceVmdPath, string.Empty),
                    [sourceWsModelPath] = PackFile.CreateFromASCII(
                        sourceWsModelPath,
                        $"<model><geometry>{rigidPath}</geometry></model>"),
                    [rigidPath] = PackFile.CreateFromASCII(rigidPath, string.Empty),
                });
            var ca = CreateTraversalContainer(
                isCaPackFile: true,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, [ca.Object]);
            AddCachedTraversalVmdWithChildReference(
                state,
                sourceVmdPath,
                sourceWsModelPath,
                unknownChildPath);
            SetStateProperty(
                state,
                "UnitCategoryResolution",
                CreateHealthyResolutionWithRosterVmd(sourceVmdPath));

            IndexImmutableMeshMergeConsumers(state, sourceVmdPath);

            var failure = GetGameplayDependencyFailureDetails(state)
                .Single(detail =>
                    GetRecordPropertyString(detail, "AssetPath") ==
                    NormalizeTestPath(unknownChildPath));

            Assert.Multiple(() =>
            {
                Assert.That(
                    GetRecordPropertyString(failure, "FailureKind"),
                    Is.EqualTo("UnknownType"));
                Assert.That(
                    GetRecordPropertyString(failure, "Disposition"),
                    Is.EqualTo("Blocking"));
                Assert.That(
                    GameplayDependencyIndexContains(
                        state,
                        "WsModelConsumersByRigid",
                        rigidPath,
                        sourceWsModelPath),
                    Is.True);
                Assert.That(
                    GetGameplayDependencyIndexCollectionCount(state, "IncompleteRoots"),
                    Is.EqualTo(1));
            });
        }

        [Test]
        public void StructuralConsumerDiscovery_UnhealthyResolverDisablesVerifiedMissingClassification()
        {
            const string sourceVmdPath =
                @"variantmeshes\variantmeshdefinitions\source.variantmeshdefinition";
            const string missingWsModelPath = @"models\missing.wsmodel";
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>
                {
                    [sourceVmdPath] = PackFile.CreateFromASCII(sourceVmdPath, string.Empty),
                });
            var ca = CreateTraversalContainer(
                isCaPackFile: true,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, [ca.Object]);
            AddCachedTraversalVmd(state, sourceVmdPath, missingWsModelPath);
            SetStateProperty(
                state,
                "UnitCategoryResolution",
                CreateUnhealthyResolutionWithRosterVmd(sourceVmdPath));

            IndexImmutableMeshMergeConsumers(state, sourceVmdPath);

            var failure = GetGameplayDependencyFailureDetails(state)
                .Single(detail =>
                    GetRecordPropertyString(detail, "AssetPath") ==
                    NormalizeTestPath(missingWsModelPath));
            var index = GetGameplayDependencyIndex(state);

            Assert.Multiple(() =>
            {
                Assert.That(
                    GetStateProperty<bool>(
                        index,
                        "VerifiedMissingClassificationEnabled"),
                    Is.False);
                Assert.That(
                    GetRecordPropertyString(failure, "Disposition"),
                    Is.EqualTo("Blocking"));
                Assert.That(
                    GetGameplayDependencyIndexCollectionCount(state, "IncompleteRoots"),
                    Is.EqualTo(1));
            });
        }

        [Test]
        public void StructuralConsumerDiscovery_TrustedMissingChild_PreservesValidSiblingConsumer()
        {
            const string sourceVmdPath =
                @"variantmeshes\variantmeshdefinitions\source.variantmeshdefinition";
            const string sourceWsModelPath = @"models\source.wsmodel";
            const string missingChildVmdPath =
                @"variantmeshes\variantmeshdefinitions\missing_child.variantmeshdefinition";
            const string rigidPath = @"models\source.rigid_model_v2";

            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>
                {
                    [sourceVmdPath] = PackFile.CreateFromASCII(sourceVmdPath, string.Empty),
                    [sourceWsModelPath] = PackFile.CreateFromASCII(
                        sourceWsModelPath,
                        $"<model><geometry>{rigidPath}</geometry></model>"),
                    [rigidPath] = PackFile.CreateFromASCII(rigidPath, string.Empty),
                });
            var ca = CreateTraversalContainer(
                isCaPackFile: true,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, [ca.Object]);
            AddCachedTraversalVmdWithChildReference(
                state,
                sourceVmdPath,
                sourceWsModelPath,
                missingChildVmdPath);
            SetStateProperty(
                state,
                "UnitCategoryResolution",
                CreateHealthyResolutionWithRosterVmd(sourceVmdPath));

            IndexImmutableMeshMergeConsumers(state, sourceVmdPath);

            Assert.Multiple(() =>
            {
                Assert.That(
                    GameplayDependencyIndexContains(
                        state,
                        "WsModelConsumersByRigid",
                        rigidPath,
                        sourceWsModelPath),
                    Is.True);
                Assert.That(
                    GetGameplayDependencyIndexCollectionCount(state, "IncompleteRoots"),
                    Is.EqualTo(0));
                Assert.That(
                    GetStateCollectionCount(
                        state,
                        "StructuralMergeConsumerDiscoveryFailures"),
                    Is.EqualTo(0));
                Assert.That(
                    GetStateProperty<bool>(
                        GetGameplayDependencyIndex(state),
                        "IsComplete"),
                    Is.True);
                Assert.That(
                    GetGameplayDependencyFailureDetails(state)
                        .Any(detail =>
                            GetRecordPropertyString(detail, "AssetPath") ==
                            NormalizeTestPath(missingChildVmdPath) &&
                            GetRecordPropertyString(detail, "Disposition") ==
                            "VerifiedMissing"),
                    Is.True);
            });
        }

        [Test]
        public void StructuralConsumerDiscovery_DiagnosticsClassifyCaDependency()
        {
            const string sourceVmdPath =
                @"variantmeshes\variantmeshdefinitions\source.variantmeshdefinition";
            const string sourceWsModelPath = @"models\source.wsmodel";
            const string caVmdPath =
                @"variantmeshes\variantmeshdefinitions\ca.variantmeshdefinition";
            const string caWsModelPath = @"models\ca.wsmodel";
            const string rigidPath = @"models\source.rigid_model_v2";

            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>
                {
                    [sourceVmdPath] = PackFile.CreateFromASCII(sourceVmdPath, string.Empty),
                    [sourceWsModelPath] = PackFile.CreateFromASCII(
                        sourceWsModelPath,
                        $"<model><geometry>{rigidPath}</geometry></model>"),
                    [rigidPath] = PackFile.CreateFromASCII(rigidPath, string.Empty),
                });
            var ca = CreateTraversalContainer(
                isCaPackFile: true,
                new Dictionary<string, PackFile>
                {
                    [caVmdPath] = PackFile.CreateFromASCII(caVmdPath, string.Empty),
                    [caWsModelPath] = PackFile.CreateFromASCII(caWsModelPath, "<model />"),
                });
            var state = CreateTraversalBatchState(source.Object, [ca.Object]);
            AddCachedTraversalVmd(state, sourceVmdPath, sourceWsModelPath);
            AddCachedTraversalVmd(state, caVmdPath, caWsModelPath);
            SetStateProperty(
                state,
                "UnitCategoryResolution",
                CreateHealthyResolutionWithRosterVmd(caVmdPath));

            IndexImmutableMeshMergeConsumers(state, sourceVmdPath);

            var failure = GetGameplayDependencyFailureDetails(state)
                .Single(detail =>
                    GetRecordPropertyString(detail, "AssetPath") ==
                    NormalizeTestPath(caWsModelPath));

            Assert.Multiple(() =>
            {
                Assert.That(
                    GetGameplayDependencyRootKinds(state, caVmdPath),
                    Does.Contain("ResolvedRosterVmd"));
                Assert.That(
                    GetRecordPropertyString(failure, "AssetScope"),
                    Is.EqualTo("CaPack"));
                Assert.That(
                    GetRecordPropertyString(failure, "Reason"),
                    Does.Contain("no geometry path"));
            });
        }

        [Test]
        public void StructuralConsumerDiscovery_MalformedCaVmd_BlocksEvenWhenTrusted()
        {
            const string sourceVmdPath =
                @"variantmeshes\variantmeshdefinitions\source.variantmeshdefinition";
            const string malformedCaVmdPath =
                @"variantmeshes\variantmeshdefinitions\malformed_ca.variantmeshdefinition";
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>
                {
                    [sourceVmdPath] = PackFile.CreateFromASCII(sourceVmdPath, string.Empty),
                });
            var ca = CreateTraversalContainer(
                isCaPackFile: true,
                new Dictionary<string, PackFile>
                {
                    [malformedCaVmdPath] = PackFile.CreateFromASCII(
                        malformedCaVmdPath,
                        "<variantmeshdefinition>"),
                });
            var state = CreateTraversalBatchState(source.Object, [ca.Object]);
            AddCachedTraversalVmd(state, sourceVmdPath, string.Empty);
            SetStateProperty(
                state,
                "UnitCategoryResolution",
                CreateHealthyResolutionWithRosterVmd(malformedCaVmdPath));

            IndexImmutableMeshMergeConsumers(state, sourceVmdPath);

            var failure = GetGameplayDependencyFailureDetails(state)
                .Single(detail =>
                    GetRecordPropertyString(detail, "AssetPath") ==
                    NormalizeTestPath(malformedCaVmdPath));

            Assert.Multiple(() =>
            {
                Assert.That(
                    GetRecordPropertyString(failure, "FailureKind"),
                    Is.EqualTo("ParseFailure"));
                Assert.That(
                    GetRecordPropertyString(failure, "AssetScope"),
                    Is.EqualTo("CaPack"));
                Assert.That(
                    GetRecordPropertyString(failure, "Disposition"),
                    Is.EqualTo("Blocking"));
                Assert.That(
                    GetGameplayDependencyIndexCollectionCount(state, "IncompleteRoots"),
                    Is.EqualTo(1));
            });
        }

        [Test]
        public void StructuralConsumerDiscovery_TrustedMissingAndBlockingFailuresRemainSeparate()
        {
            const string sourceVmdPath =
                @"variantmeshes\variantmeshdefinitions\source.variantmeshdefinition";
            const string missingWsModelPath = @"models\missing.wsmodel";
            const string malformedChildVmdPath =
                @"variantmeshes\variantmeshdefinitions\malformed_child.variantmeshdefinition";
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>
                {
                    [sourceVmdPath] = PackFile.CreateFromASCII(sourceVmdPath, string.Empty),
                    [malformedChildVmdPath] = PackFile.CreateFromASCII(
                        malformedChildVmdPath,
                        "<variantmeshdefinition>"),
                });
            var ca = CreateTraversalContainer(
                isCaPackFile: true,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, [ca.Object]);
            AddCachedTraversalVmdWithChildReference(
                state,
                sourceVmdPath,
                missingWsModelPath,
                malformedChildVmdPath);
            SetStateProperty(
                state,
                "UnitCategoryResolution",
                CreateHealthyResolutionWithRosterVmd(sourceVmdPath));

            IndexImmutableMeshMergeConsumers(state, sourceVmdPath);

            var failures = GetGameplayDependencyFailureDetails(state);
            var missingFailure = failures.Single(detail =>
                GetRecordPropertyString(detail, "AssetPath") ==
                NormalizeTestPath(missingWsModelPath));
            var parseFailure = failures.Single(detail =>
                GetRecordPropertyString(detail, "AssetPath") ==
                NormalizeTestPath(malformedChildVmdPath));
            var diagnostics = GetGameplayMeshDependencyDiagnostics(state);

            Assert.Multiple(() =>
            {
                Assert.That(
                    GetRecordPropertyString(missingFailure, "Disposition"),
                    Is.EqualTo("VerifiedMissing"));
                Assert.That(
                    GetRecordPropertyString(parseFailure, "FailureKind"),
                    Is.EqualTo("ParseFailure"));
                Assert.That(
                    GetRecordPropertyString(parseFailure, "Disposition"),
                    Is.EqualTo("Blocking"));
                Assert.That(
                    GetGameplayDependencyIndexCollectionCount(state, "IncompleteRoots"),
                    Is.EqualTo(1));
                Assert.That(
                    GetStateCollectionCount(
                        state,
                        "StructuralMergeConsumerDiscoveryFailures"),
                    Is.EqualTo(1));
                Assert.That(diagnostics, Does.Contain("Blocking failure details: 1"));
                Assert.That(
                    diagnostics,
                    Does.Contain("Verified-missing dependency details: 1"));
                Assert.That(diagnostics, Does.Contain("disposition=Blocking"));
                Assert.That(diagnostics, Does.Contain("disposition=VerifiedMissing"));
            });
        }

        [Test]
        public void StructuralConsumerDiscovery_DiagnosticsAttributeBlockToIncompleteRoot()
        {
            const string sourceVmdPath =
                @"variantmeshes\variantmeshdefinitions\source.variantmeshdefinition";
            const string sourceWsModelPath = @"models\source.wsmodel";
            const string missingChildVmdPath =
                @"variantmeshes\variantmeshdefinitions\missing_child.variantmeshdefinition";
            const string rigidPath = @"models\source.rigid_model_v2";

            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>
                {
                    [sourceVmdPath] = PackFile.CreateFromASCII(sourceVmdPath, string.Empty),
                    [sourceWsModelPath] = PackFile.CreateFromASCII(
                        sourceWsModelPath,
                        $"<model><geometry>{rigidPath}</geometry></model>"),
                    [rigidPath] = PackFile.CreateFromASCII(rigidPath, string.Empty),
                });
            var state = CreateTraversalBatchState(source.Object, []);
            AddCachedTraversalVmdWithChildReference(
                state,
                sourceVmdPath,
                sourceWsModelPath,
                missingChildVmdPath);
            SetStateProperty(
                state,
                "UnitCategoryResolution",
                CreateHealthyResolutionWithRosterVmd(sourceVmdPath));

            IndexImmutableMeshMergeConsumers(state, sourceVmdPath);
            var decision = GetStructuralMergeBlockDecision(state, rigidPath);
            var blocks = GetGameplayDiscoveryBlocks(state);
            var block = blocks.Single(item =>
                GetRecordPropertyString(item, "AssetPath") ==
                NormalizeTestPath(rigidPath));

            Assert.Multiple(() =>
            {
                Assert.That(decision.IsBlocked, Is.True);
                Assert.That(
                    GetRecordPropertyString(block, "BlockKind"),
                    Is.EqualTo("StructuralMerge"));
                Assert.That(
                    GetRecordPropertyString(block, "IncompleteRootPath"),
                    Is.EqualTo(NormalizeTestPath(sourceVmdPath)));
            });
        }

        [Test]
        public void StructuralConsumerDiscovery_SeedsRootsFromCompleteGameplayRoster()
        {
            const string sourceVmdPath =
                @"variantmeshes\variantmeshdefinitions\source.variantmeshdefinition";
            const string sourceWsModelPath = @"models\source.wsmodel";
            const string caVmdPath =
                @"variantmeshes\variantmeshdefinitions\ca.variantmeshdefinition";
            const string caWsModelPath = @"models\ca.wsmodel";
            const string rigidPath = @"models\shared.rigid_model_v2";

            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>
                {
                    [sourceVmdPath] = PackFile.CreateFromASCII(sourceVmdPath, string.Empty),
                    [sourceWsModelPath] = PackFile.CreateFromASCII(
                        sourceWsModelPath,
                        $"<model><geometry>{rigidPath}</geometry></model>"),
                    [rigidPath] = PackFile.CreateFromASCII(rigidPath, string.Empty),
                });
            var ca = CreateTraversalContainer(
                isCaPackFile: true,
                new Dictionary<string, PackFile>
                {
                    [caVmdPath] = PackFile.CreateFromASCII(caVmdPath, string.Empty),
                    [caWsModelPath] = PackFile.CreateFromASCII(
                        caWsModelPath,
                        $"<model><geometry>{rigidPath}</geometry></model>"),
                });
            var state = CreateTraversalBatchState(source.Object, [ca.Object]);
            AddCachedTraversalVmd(state, sourceVmdPath, sourceWsModelPath);
            AddCachedTraversalVmd(state, caVmdPath, caWsModelPath);
            SetStateProperty(
                state,
                "UnitCategoryResolution",
                CreateHealthyResolutionWithRosterVmd(caVmdPath));

            IndexImmutableMeshMergeConsumers(state, sourceVmdPath);

            var immutableConsumers = (IDictionary)(state.GetType()
                .GetProperty(
                    "ImmutableMeshMergeConsumersByRigid",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(state)
                ?? throw new InvalidOperationException(
                    "ImmutableMeshMergeConsumersByRigid was not found."));
            var normalizedRigidPath = NormalizeTestPath(rigidPath);
            var hasImmutableRigid = immutableConsumers.Contains(normalizedRigidPath);
            var consumers = hasImmutableRigid
                ? ((IEnumerable)immutableConsumers[normalizedRigidPath]!)
                    .Cast<object>()
                    .Select(value => value.ToString() ?? string.Empty)
                    .ToArray()
                : Array.Empty<string>();
            var discoveryFailures = ((IEnumerable)(state.GetType()
                .GetProperty(
                    "StructuralMergeConsumerDiscoveryFailures",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(state)
                ?? throw new InvalidOperationException(
                    "StructuralMergeConsumerDiscoveryFailures was not found.")))
                .Cast<object>()
                .Select(value => value.ToString() ?? string.Empty)
                .ToArray();
            var (candidate, model) = CreateAtlasConsumerTestCandidate(
                rigidPath,
                wsModelMaterialConsumer: true);
            var originalUv = model.Mesh.VertexList[0].Uv;
            var filteredCount = FilterAtlasConsumerTestCandidates(state, candidate);

            Assert.Multiple(() =>
            {
                Assert.That(
                    GetResolutionRosterVmdPaths(state),
                    Does.Contain(NormalizeTestPath(caVmdPath)),
                    "The test resolution must expose the CA VMD in its complete roster.");
                Assert.That(
                    hasImmutableRigid,
                    Is.True,
                    $"The shared rigid was not indexed as an immutable-consumer rigid. Failures: {string.Join("; ", discoveryFailures)}");
                Assert.That(
                    GetStateProperty<bool>(state, "StructuralMergeConsumerDiscoveryComplete"),
                    Is.True);
                Assert.That(
                    consumers,
                    Does.Contain(NormalizeTestPath(caWsModelPath)),
                    "The CA VMD from the complete gameplay roster must be traversed even though it is not a source root.");
                Assert.That(
                    GameplayDependencyIndexContains(
                        state,
                        "DependenciesByAssetPath",
                        caVmdPath,
                        caWsModelPath),
                    Is.True,
                    "The reusable dependency index must retain the CA VMD -> WSModel edge.");
                Assert.That(
                    GameplayDependencyIndexContains(
                        state,
                        "DependenciesByAssetPath",
                        caWsModelPath,
                        rigidPath),
                    Is.True,
                    "The reusable dependency index must retain the WSModel -> rigid edge.");
                Assert.That(
                    GameplayDependencyIndexContains(
                        state,
                        "WsModelConsumersByRigid",
                        rigidPath,
                        caWsModelPath),
                    Is.True,
                    "The reverse-consumer index must retain the immutable CA WSModel consumer.");
                Assert.That(
                    GetGameplayDependencyRootKinds(state, sourceVmdPath),
                    Does.Contain("SourcePackVmd"),
                    "The source VMD must be attributed to the source-pack root origin.");
                Assert.That(
                    GetGameplayDependencyRootKinds(state, caVmdPath),
                    Does.Contain("ResolvedRosterVmd"),
                    "The CA VMD must be attributed to the resolver-roster root origin.");
                Assert.That(filteredCount, Is.EqualTo(0));
                Assert.That(model.Mesh.VertexList[0].Uv, Is.EqualTo(originalUv));
            });
        }

        [Test]
        public void StructuralConsumerDiscovery_PreservesRosterVmdExcludedFromResidencyModel()
        {
            const string sourceVmdPath =
                @"variantmeshes\variantmeshdefinitions\source.variantmeshdefinition";
            const string sourceWsModelPath = @"models\source.wsmodel";
            const string includedCaVmdPath =
                @"variantmeshes\variantmeshdefinitions\included_ca.variantmeshdefinition";
            const string includedCaWsModelPath = @"models\included_ca.wsmodel";
            const string excludedCaVmdPath =
                @"variantmeshes\variantmeshdefinitions\excluded_ca.variantmeshdefinition";
            const string excludedCaWsModelPath = @"models\excluded_ca.wsmodel";
            const string sharedRigidPath = @"models\shared.rigid_model_v2";

            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>
                {
                    [sourceVmdPath] = PackFile.CreateFromASCII(sourceVmdPath, string.Empty),
                    [sourceWsModelPath] = PackFile.CreateFromASCII(
                        sourceWsModelPath,
                        $"<model><geometry>{sharedRigidPath}</geometry></model>"),
                    [sharedRigidPath] = PackFile.CreateFromASCII(sharedRigidPath, string.Empty),
                });
            var ca = CreateTraversalContainer(
                isCaPackFile: true,
                new Dictionary<string, PackFile>
                {
                    [includedCaVmdPath] = PackFile.CreateFromASCII(includedCaVmdPath, string.Empty),
                    [includedCaWsModelPath] = PackFile.CreateFromASCII(
                        includedCaWsModelPath,
                        "<model><geometry>models\\included.rigid_model_v2</geometry></model>"),
                    [excludedCaVmdPath] = PackFile.CreateFromASCII(excludedCaVmdPath, string.Empty),
                    [excludedCaWsModelPath] = PackFile.CreateFromASCII(
                        excludedCaWsModelPath,
                        $"<model><geometry>{sharedRigidPath}</geometry></model>"),
                });
            var state = CreateTraversalBatchState(source.Object, [ca.Object]);
            AddCachedTraversalVmd(state, sourceVmdPath, sourceWsModelPath);
            AddCachedTraversalVmd(state, includedCaVmdPath, includedCaWsModelPath);
            AddCachedTraversalVmd(state, excludedCaVmdPath, excludedCaWsModelPath);
            SetStateProperty(
                state,
                "UnitCategoryResolution",
                CreateHealthyResolutionWithRosterVmds(
                    (includedCaVmdPath, "InfantryMissile"),
                    (excludedCaVmdPath, "Unknown")));
            PopulateArmyResidencyModel(state);

            Assert.That(
                ArmyResidencyAssetIndexContains(state, includedCaVmdPath),
                Is.True,
                "The modeled unit should be present in the optimized residency index.");
            Assert.That(
                ArmyResidencyAssetIndexContains(state, excludedCaVmdPath),
                Is.False,
                "The Unknown-category unit must reproduce the residency-model exclusion.");

            IndexImmutableMeshMergeConsumers(state, sourceVmdPath);

            Assert.Multiple(() =>
            {
                Assert.That(
                    GameplayDependencyIndexContains(
                        state,
                        "DependenciesByAssetPath",
                        excludedCaVmdPath,
                        excludedCaWsModelPath),
                    Is.True,
                    "Safety traversal must retain a resolver-roster VMD even when the residency model excludes its unit.");
                Assert.That(
                    GameplayDependencyIndexContains(
                        state,
                        "WsModelConsumersByRigid",
                        sharedRigidPath,
                        excludedCaWsModelPath),
                    Is.True,
                    "The excluded unit's immutable CA WSModel must still protect the shared source rigid.");
                Assert.That(
                    StateDictionaryContains(
                        state,
                        "ImmutableMeshMergeConsumersByRigid",
                        sharedRigidPath),
                    Is.True);
            });
        }

        [Test]
        public void StructuralConsumerDiscovery_UnresolvedVmd_BlocksMerge()
        {
            const string unresolvedVmdPath =
                @"variantmeshes\variantmeshdefinitions\unresolved.variantmeshdefinition";
            const string rigidPath = @"models\source.rigid_model_v2";
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, []);

            IndexImmutableMeshMergeConsumers(state, unresolvedVmdPath);
            var decision = GetStructuralMergeBlockDecision(state, rigidPath);

            Assert.Multiple(() =>
            {
                Assert.That(
                    GetStateCollectionCount(
                        state,
                        "StructuralMergeConsumerDiscoveryFailures"),
                    Is.GreaterThan(0));
                Assert.That(decision.IsBlocked, Is.True);
                Assert.That(
                    decision.Reason,
                    Does.Contain("complete gameplay consumer discovery"));
            });
        }

        [Test]
        public void StructuralConsumerDiscovery_MalformedSourceWsModel_BlocksMerge()
        {
            const string rigidPath = @"models\source.rigid_model_v2";
            const string wsModelPath = @"models\malformed.wsmodel";
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>
                {
                    [rigidPath] = PackFile.CreateFromASCII(rigidPath, string.Empty),
                    [wsModelPath] = PackFile.CreateFromASCII(wsModelPath, "<model>"),
                });
            var state = CreateTraversalBatchState(source.Object, []);

            IndexImmutableMeshMergeConsumers(state, wsModelPath);
            var decision = GetStructuralMergeBlockDecision(state, rigidPath);
            var malformedFailure = GetGameplayDependencyFailureDetails(state)
                .Single(detail =>
                    GetRecordPropertyString(detail, "AssetPath") ==
                    NormalizeTestPath(wsModelPath));

            Assert.Multiple(() =>
            {
                Assert.That(
                    GetStateCollectionCount(state, "MalformedWsModelsIgnored"),
                    Is.EqualTo(1));
                Assert.That(decision.IsBlocked, Is.True);
                Assert.That(
                    decision.Reason,
                    Does.Contain("complete gameplay consumer discovery"));
                Assert.That(
                    GetRecordPropertyString(malformedFailure, "AssetScope"),
                    Is.EqualTo("SourcePack"));
            });
        }

        [Test]
        public void StructuralConsumerDiscovery_SharedMalformedWsModel_AttributesEveryRoot()
        {
            const string firstVmdPath =
                @"variantmeshes\variantmeshdefinitions\first.variantmeshdefinition";
            const string secondVmdPath =
                @"variantmeshes\variantmeshdefinitions\second.variantmeshdefinition";
            const string malformedWsModelPath = @"models\malformed.wsmodel";
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>
                {
                    [firstVmdPath] = PackFile.CreateFromASCII(firstVmdPath, string.Empty),
                    [secondVmdPath] = PackFile.CreateFromASCII(secondVmdPath, string.Empty),
                    [malformedWsModelPath] = PackFile.CreateFromASCII(
                        malformedWsModelPath,
                        "<model>"),
                });
            var state = CreateTraversalBatchState(source.Object, []);
            AddCachedTraversalVmd(state, firstVmdPath, malformedWsModelPath);
            AddCachedTraversalVmd(state, secondVmdPath, malformedWsModelPath);

            IndexImmutableMeshMergeConsumers(state, firstVmdPath, secondVmdPath);

            var failures = GetGameplayDependencyFailureDetails(state)
                .Where(detail =>
                    GetRecordPropertyString(detail, "AssetPath") ==
                    NormalizeTestPath(malformedWsModelPath))
                .ToArray();
            var failureRoots = failures
                .Select(detail => GetRecordPropertyString(detail, "RootPath"))
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(failures, Has.Length.EqualTo(2));
                Assert.That(
                    failureRoots,
                    Does.Contain(NormalizeTestPath(firstVmdPath)));
                Assert.That(
                    failureRoots,
                    Does.Contain(NormalizeTestPath(secondVmdPath)));
                Assert.That(
                    GetGameplayDependencyIndexCollectionCount(state, "IncompleteRoots"),
                    Is.EqualTo(2));
                Assert.That(
                    GetStateCollectionCount(
                        state,
                        "StructuralMergeConsumerDiscoveryFailures"),
                    Is.EqualTo(2));
            });
        }

        [Test]
        public void AtlasPlanningIdentity_IsSharedAcrossDifferentLods()
        {
            var lod0 = CreateAtlasPlanningTestCandidate(
                "models/planning.rigid_model_v2",
                0,
                0);
            var lod2 = CreateAtlasPlanningTestCandidate(
                "models/planning.rigid_model_v2",
                2,
                0);

            Assert.That(
                GetAtlasPlanningIdentity(lod0),
                Is.EqualTo(GetAtlasPlanningIdentity(lod2)));
        }

        [Test]
        public void SharedAtlasPages_PreserveBatchBoundariesWhileSharingPhysicalPage()
        {
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, []);
            var groups = BuildSharedAtlasPageGroupsForTest(
                state,
                [
                    [
                        CreateAtlasPlanningTestCandidate("models/page_a.rigid_model_v2", 0, 0),
                        CreateAtlasPlanningTestCandidate("models/page_a.rigid_model_v2", 0, 1),
                    ],
                    [
                        CreateAtlasPlanningTestCandidate("models/page_b.rigid_model_v2", 0, 0),
                        CreateAtlasPlanningTestCandidate("models/page_b.rigid_model_v2", 0, 1),
                    ],
                    [
                        CreateAtlasPlanningTestCandidate("models/page_c.rigid_model_v2", 0, 0),
                        CreateAtlasPlanningTestCandidate("models/page_c.rigid_model_v2", 0, 1),
                    ],
                ]);

            var group = groups.Single();
            var groupBatches = (IEnumerable)(group.GetType()
                .GetProperty("Batches")!
                .GetValue(group)!
                ?? throw new InvalidOperationException("Shared page batches were missing."));
            var batchArrays = groupBatches
                .Cast<IEnumerable>()
                .Select(batch => batch.Cast<object>().ToArray())
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(groups, Has.Count.EqualTo(1));
                Assert.That(GetObjectCollectionCount(group, "BatchIndices"), Is.EqualTo(3));
                Assert.That(batchArrays, Has.Length.EqualTo(3));
                Assert.That(batchArrays.Select(batch => batch.Length), Is.EqualTo([2, 2, 2]));
                Assert.That(GetObjectCollectionCount(group, "Candidates"), Is.EqualTo(6));
                Assert.That(GetStateProperty<int>(state, "SharedAtlasPageCoalescesAccepted"), Is.EqualTo(1));
                Assert.That(GetStateProperty<int>(state, "SharedAtlasPageParticipatingBatches"), Is.EqualTo(3));
            });
        }

        [Test]
        public void SharedAtlasPages_DoNotCrossVmdRoots()
        {
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, []);
            var groups = BuildSharedAtlasPageGroupsForTest(
                state,
                [
                    [
                        CreateAtlasPlanningTestCandidate(
                            "models/page_a.rigid_model_v2",
                            0,
                            0,
                            "models\\first.variantmeshdefinition"),
                        CreateAtlasPlanningTestCandidate(
                            "models/page_a.rigid_model_v2",
                            0,
                            1,
                            "models\\first.variantmeshdefinition"),
                    ],
                    [
                        CreateAtlasPlanningTestCandidate(
                            "models/page_b.rigid_model_v2",
                            0,
                            0,
                            "models\\second.variantmeshdefinition"),
                        CreateAtlasPlanningTestCandidate(
                            "models/page_b.rigid_model_v2",
                            0,
                            1,
                            "models\\second.variantmeshdefinition"),
                    ],
                ]);

            Assert.Multiple(() =>
            {
                Assert.That(groups, Has.Count.EqualTo(2));
                Assert.That(GetStateProperty<int>(state, "SharedAtlasPageCoalescesAccepted"), Is.EqualTo(0));
                Assert.That(GetStateProperty<int>(state, "SharedAtlasPageSharingEvaluations"), Is.EqualTo(0));
            });
        }

        [Test]
        public void SharedAtlasPages_DoNotShareDifferentTextureIdentities()
        {
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, []);
            var firstBatch = new[]
            {
                CreateAtlasPlanningTestCandidate("models/page_a.rigid_model_v2", 0, 0),
                CreateAtlasPlanningTestCandidate("models/page_a.rigid_model_v2", 0, 1),
            };
            var secondBatch = new[]
            {
                CreateAtlasPlanningTestCandidate("models/page_b.rigid_model_v2", 0, 0),
                CreateAtlasPlanningTestCandidate("models/page_b.rigid_model_v2", 0, 1),
            };
            foreach (var candidate in firstBatch)
                SetAtlasCandidateTexturePath(
                    candidate,
                    "t_xml_base_colour",
                    "textures\\planning_first.dds");
            foreach (var candidate in secondBatch)
                SetAtlasCandidateTexturePath(
                    candidate,
                    "t_xml_base_colour",
                    "textures\\planning_second.dds");

            var groups = BuildSharedAtlasPageGroupsForTest(state, firstBatch, secondBatch);

            Assert.Multiple(() =>
            {
                Assert.That(groups, Has.Count.EqualTo(2));
                Assert.That(GetStateProperty<int>(state, "SharedAtlasPageCoalescesAccepted"), Is.EqualTo(0));
            });
        }

        [Test]
        public void SharedAtlasBatch_CanonicalizesNestedCropUnion()
        {
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, []);
            var outer = CreateAtlasPlanningTestCandidate(
                "models/page.rigid_model_v2",
                0,
                0);
            var inner = CreateAtlasPlanningTestCandidate(
                "models/page.rigid_model_v2",
                0,
                1);
            SetAtlasCandidateBounds(outer, 0f, 0f, 1f, 1f);
            SetAtlasCandidateBounds(inner, 0.25f, 0.25f, 0.75f, 0.75f);

            var sharedBatch = BuildSharedAtlasBatchForTest(
                state,
                [outer, inner],
                mergeCompatibleCrops: true);
            var sources = (IEnumerable)(sharedBatch.GetType()
                .GetProperty("Sources")!
                .GetValue(sharedBatch)!
                ?? throw new InvalidOperationException("Shared atlas sources were missing."));
            var sourceArray = sources.Cast<object>().ToArray();
            var sourceEntry = sourceArray.Single();
            var crop = sourceEntry.GetType()
                .GetProperty("Crop")!
                .GetValue(sourceEntry)
                ?? throw new InvalidOperationException("Shared atlas crop was missing.");

            Assert.Multiple(() =>
            {
                Assert.That(sourceArray, Has.Length.EqualTo(1));
                Assert.That(crop.GetType().GetProperty("X")!.GetValue(crop), Is.EqualTo(0));
                Assert.That(crop.GetType().GetProperty("Y")!.GetValue(crop), Is.EqualTo(0));
                Assert.That(crop.GetType().GetProperty("Width")!.GetValue(crop), Is.EqualTo(64));
                Assert.That(crop.GetType().GetProperty("Height")!.GetValue(crop), Is.EqualTo(64));
            });
        }

        [Test]
        public void StructuralMerge_DoesNotCrossLogicalAtlasBatchBoundaries()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    GetWsMergeGroupCountForAtlasBatches(7, 7),
                    Is.EqualTo(1));
                Assert.That(
                    GetWsMergeGroupCountForAtlasBatches(7, 8),
                    Is.EqualTo(2));
            });
        }

        [Test]
        public void StructuralMergeDiagnostics_ExplainLogicalAtlasBatchBoundary()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    GetMeshMergeBlockerCountForAtlasBatches(
                        7,
                        8,
                        "Logical atlas batch boundary"),
                    Is.EqualTo(1));
                Assert.That(
                    GetMeshMergeBlockerCountForAtlasBatches(
                        7,
                        7,
                        "Logical atlas batch boundary"),
                    Is.EqualTo(0));
            });
        }

        [Test]
        public void SharedAtlasPages_RejectIndependentDimensionEnvelope()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    FitsWithinLargestReplacedPageForTest(
                        [(4096, 1024), (1024, 4096)],
                        (4096, 2048)),
                    Is.False);
                Assert.That(
                    FitsWithinLargestReplacedPageForTest(
                        [(4096, 4096), (1024, 1024)],
                        (4096, 2048)),
                    Is.True);
            });
        }

        [Test]
        public void AtlasResidencySummary_UsesPhysicalSharedPageOnce()
        {
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, []);
            var first = CreateAtlasPlanningTestCandidate(
                "models/first.rigid_model_v2",
                0,
                0,
                "models\\first.variantmeshdefinition");
            var second = CreateAtlasPlanningTestCandidate(
                "models/second.rigid_model_v2",
                0,
                0,
                "models\\second.variantmeshdefinition");

            AddAtlasBatchDiagnosticForTest(state, 7, [first], 100);
            AddAtlasBatchDiagnosticForTest(state, 8, [second], 100);
            AddAtlasPhysicalPageForTest(state, [first, second], 150);

            var summary = BuildAtlasResidencySummaryForTest(state);

            Assert.Multiple(() =>
            {
                Assert.That(GetRecordProperty<long>(summary, "GlobalPixels"), Is.EqualTo(150));
                Assert.That(
                    GetRecordProperty<long>(summary, "AggregateResidentPixels"),
                    Is.EqualTo(300));
                Assert.That(
                    GetRecordProperty<int>(summary, "MultiRootPageCount"),
                    Is.EqualTo(1));
                Assert.That(
                    GetRecordProperty<int>(summary, "MaxRootsPerPage"),
                    Is.EqualTo(2));
                Assert.That(
                    GetRecordProperty<long>(summary, "WorstRootPixels"),
                    Is.EqualTo(150));
            });
        }

        [Test]
        public void LodAwareAtlasValueGatePlan_KeepsFamilyAttachmentWithMergeHost()
        {
            const string rigidPath = "models/planning.rigid_model_v2";
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, []);

            var lod0Part0 = CreateAtlasPlanningTestCandidate(rigidPath, 0, 0);
            var lod0Part1 = CreateAtlasPlanningTestCandidate(rigidPath, 0, 1);
            var lod2Part0 = CreateAtlasPlanningTestCandidate(rigidPath, 2, 0);

            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var meshKeyType = serviceType.GetNestedType(
                "MeshKey",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MeshKey was not found.");
            var atlasCandidateType = serviceType.GetNestedType(
                "AtlasCandidate",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AtlasCandidate was not found.");
            var keyProperty = atlasCandidateType.GetProperty(
                "Key",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AtlasCandidate.Key was not found.");
            object GetKey(object candidate)
                => keyProperty.GetValue(candidate)
                   ?? throw new InvalidOperationException("Atlas candidate key was null.");

            var hostKey = GetKey(lod0Part0);
            var mergePartnerKey = GetKey(lod0Part1);
            var familyAttachmentKey = GetKey(lod2Part0);
            var hashSetType = typeof(HashSet<>).MakeGenericType(meshKeyType);
            var family = Activator.CreateInstance(hashSetType)
                ?? throw new InvalidOperationException("Could not create LOD family.");
            var addFamilyKey = hashSetType.GetMethod("Add")
                ?? throw new InvalidOperationException("LOD family could not add members.");
            addFamilyKey.Invoke(family, [hostKey]);
            addFamilyKey.Invoke(family, [familyAttachmentKey]);

            var families = state.GetType()
                .GetProperty(
                    "LodAwareAtlasFamilies",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(state) as IList
                ?? throw new InvalidOperationException("LOD atlas families were not found.");
            families.Add(family);

            var dictionaryType = typeof(Dictionary<,>)
                .MakeGenericType(meshKeyType, atlasCandidateType);
            var candidateByKey = Activator.CreateInstance(dictionaryType) as IDictionary
                ?? throw new InvalidOperationException("Could not create candidate dictionary.");
            candidateByKey.Add(hostKey, lod0Part0);
            candidateByKey.Add(mergePartnerKey, lod0Part1);
            candidateByKey.Add(familyAttachmentKey, lod2Part0);

            var batchKeys = Activator.CreateInstance(hashSetType)
                ?? throw new InvalidOperationException("Could not create batch key set.");
            addFamilyKey.Invoke(batchKeys, [hostKey]);
            addFamilyKey.Invoke(batchKeys, [mergePartnerKey]);
            addFamilyKey.Invoke(batchKeys, [familyAttachmentKey]);

            var groupType = serviceType.GetNestedType(
                "MergeAffinityGroup",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MergeAffinityGroup was not found.");
            var groupConstructor = groupType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(constructor => constructor.GetParameters().Length == 4);
            var groupMeshes = Array.CreateInstance(meshKeyType, 2);
            groupMeshes.SetValue(hostKey, 0);
            groupMeshes.SetValue(mergePartnerKey, 1);
            var affinityGroup = groupConstructor.Invoke(
                [
                    groupMeshes,
                    1,
                    new[] { "models/planning.rigid_model_v2|lod0|parts0-1" },
                    new[] { "planning-material-a", "planning-material-b" },
                ]);
            var groupsType = typeof(List<>).MakeGenericType(groupType);
            var groups = Activator.CreateInstance(groupsType) as IList
                ?? throw new InvalidOperationException("Could not create affinity groups.");
            groups.Add(affinityGroup);

            var method = serviceType.GetMethod(
                    "BuildAtlasValueGateGroupPlans",
                    BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "BuildAtlasValueGateGroupPlans was not found.");
            var plans = ((IEnumerable)(method.Invoke(
                    null,
                    [state, groups, candidateByKey, batchKeys])
                ?? throw new InvalidOperationException(
                    "BuildAtlasValueGateGroupPlans returned null.")))
                .Cast<object>()
                .ToArray();
            var plan = plans.Single();
            var candidates = (IEnumerable)(plan.GetType()
                .GetProperty("Candidates")
                ?.GetValue(plan)
                ?? throw new InvalidOperationException("Value-gate candidates were missing."));
            var atomicFamilies = (IEnumerable)(plan.GetType()
                .GetProperty("AtomicFamilies")
                ?.GetValue(plan)
                ?? throw new InvalidOperationException("Atomic LOD families were missing."));
            var atomicFamily = atomicFamilies.Cast<object>().Single() as IEnumerable
                ?? throw new InvalidOperationException("Atomic LOD family was null.");
            var candidateArray = candidates.Cast<object>().ToArray();
            var atomicFamilyArray = atomicFamilies.Cast<object>().ToArray();
            var atomicMemberArray = atomicFamily.Cast<object>().ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(candidateArray, Has.Length.EqualTo(3));
                Assert.That(atomicFamilyArray, Has.Length.EqualTo(1));
                Assert.That(atomicMemberArray, Has.Length.EqualTo(2));
            });
        }

        [Test]
        public void LodAwareAtlasValueGatePlan_PreservesBothMergeGroupsAcrossFamily()
        {
            const string rigidPath = "models/planning.rigid_model_v2";
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, []);

            var lod0Part0 = CreateAtlasPlanningTestCandidate(rigidPath, 0, 0);
            var lod0Part1 = CreateAtlasPlanningTestCandidate(rigidPath, 0, 1);
            var lod2Part0 = CreateAtlasPlanningTestCandidate(rigidPath, 2, 0);
            var lod2Part1 = CreateAtlasPlanningTestCandidate(rigidPath, 2, 1);
            var lod0Group = CreateTextureOnlyMergeAffinityGroup(
                [lod0Part0, lod0Part1],
                "planning-lod0-a",
                "planning-lod0-b");
            var lod2Group = CreateTextureOnlyMergeAffinityGroup(
                [lod2Part0, lod2Part1],
                "planning-lod2-a",
                "planning-lod2-b");

            var plans = BuildAtlasValueGatePlansForTest(
                state,
                [lod0Part0, lod0Part1, lod2Part0, lod2Part1],
                [lod0Group, lod2Group],
                [lod0Part0, lod2Part0]);
            var plan = plans.Single();
            var planType = plan.GetType();
            var candidates = ((IEnumerable)(planType.GetProperty("Candidates")
                    ?.GetValue(plan)
                ?? throw new InvalidOperationException(
                    "Value-gate candidates were missing.")))
                .Cast<object>()
                .ToArray();
            var affinityGroups = ((IEnumerable)(planType.GetProperty("AffinityGroups")
                    ?.GetValue(plan)
                ?? throw new InvalidOperationException(
                    "Value-gate affinity groups were missing.")))
                .Cast<object>()
                .ToArray();
            var atomicFamilies = ((IEnumerable)(planType.GetProperty("AtomicFamilies")
                    ?.GetValue(plan)
                ?? throw new InvalidOperationException(
                    "Atomic LOD families were missing.")))
                .Cast<object>()
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(candidates, Has.Length.EqualTo(4));
                Assert.That(affinityGroups, Has.Length.EqualTo(2));
                Assert.That(
                    affinityGroups.Select(group => ((IEnumerable)group.GetType()
                            .GetProperty("Meshes")!
                            .GetValue(group)!)
                        .Cast<object>()
                        .Count()),
                    Is.EqualTo([2, 2]));
                Assert.That(atomicFamilies, Has.Length.EqualTo(1));
            });
        }

        [Test]
        public void LodAwareAtlasFamilyClosure_ReusesPlacementAcrossLods()
        {
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, []);
            SetStateProperty(state, "LodAwareAtlasPlanningEnabled", true);

            var closed = CloseAtlasPlanningTestBatches(
                state,
                (
                    CreateAtlasPlanningTestCandidate(
                        "models/planning.rigid_model_v2",
                        0,
                        0),
                    null),
                (
                    CreateAtlasPlanningTestCandidate(
                        "models/planning.rigid_model_v2",
                        2,
                        0),
                    null));

            Assert.That(closed, Has.Count.EqualTo(1));
            Assert.That(closed[0], Has.Count.EqualTo(2));
        }

        [Test]
        public void LodAwareAtlasFamilyClosure_DoesNotLinkDifferentRigidsWithSamePlacement()
        {
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, []);
            SetStateProperty(state, "LodAwareAtlasPlanningEnabled", true);

            var closed = CloseAtlasPlanningTestBatches(
                state,
                (
                    CreateAtlasPlanningTestCandidate(
                        "models/planning_a.rigid_model_v2",
                        0,
                        0),
                    null),
                (
                    CreateAtlasPlanningTestCandidate(
                        "models/planning_b.rigid_model_v2",
                        2,
                        0),
                    null));

            Assert.Multiple(() =>
            {
                Assert.That(closed, Has.Count.EqualTo(2));
                Assert.That(closed.Select(batch => batch.Count), Is.EqualTo([1, 1]));
            });
        }

        [Test]
        public void LodAwareAtlasFamilyClosure_RejectsEqualTotalAffinitySwap()
        {
            const string rigidPath = "models/planning.rigid_model_v2";
            var lod2A = CreateAtlasPlanningTestCandidate(rigidPath, 2, 0);
            var lod2B = CreateAtlasPlanningTestCandidate(rigidPath, 2, 1);
            var lod0A = CreateAtlasPlanningTestCandidate(rigidPath, 0, 0);
            var lod0B = CreateAtlasPlanningTestCandidate(rigidPath, 0, 1);
            var lod2Group = CreateTextureOnlyMergeAffinityGroup(
                [lod2A, lod2B],
                "planning-lod2-a",
                "planning-lod2-b");
            var lod0Group = CreateTextureOnlyMergeAffinityGroup(
                [lod0A, lod0B],
                "planning-lod0-a",
                "planning-lod0-b");

            // Both layouts have one raw merge opportunity, but they preserve different
            // same-LOD groups. The family move must not treat the equal sums as interchangeable.
            var evaluation = EvaluateMergeAffinityContributionsForTest(
                [
                    [lod2A, lod2B],
                    [lod0A],
                    [lod0B],
                ],
                [
                    [lod2A],
                    [lod2B],
                    [lod0A, lod0B],
                ],
                [lod2Group, lod0Group]);

            Assert.Multiple(() =>
            {
                Assert.That(evaluation.BaselineScore, Is.EqualTo(1));
                Assert.That(evaluation.ProposedScore, Is.EqualTo(1));
                Assert.That(evaluation.Preserves, Is.False);
            });
        }

        [Test]
        public void LodAwareAtlasFamilyClosure_PreservesThreeMeshComponentPairing()
        {
            const string rigidPath = "models/planning.rigid_model_v2";
            var meshA = CreateAtlasPlanningTestCandidate(rigidPath, 0, 0);
            var meshB = CreateAtlasPlanningTestCandidate(rigidPath, 0, 1);
            var meshC = CreateAtlasPlanningTestCandidate(rigidPath, 0, 2);
            var affinityGroup = CreateTextureOnlyMergeAffinityGroup(
                [meshA, meshB, meshC],
                "planning-a",
                "planning-b",
                "planning-c");

            var evaluation = EvaluateMergeAffinityContributionsForTest(
                [
                    [meshA, meshB],
                    [meshC],
                ],
                [
                    [meshA, meshC],
                    [meshB],
                ],
                [affinityGroup]);

            Assert.Multiple(() =>
            {
                Assert.That(evaluation.BaselineScore, Is.EqualTo(1));
                Assert.That(evaluation.ProposedScore, Is.EqualTo(1));
                Assert.That(evaluation.Preserves, Is.False);
            });
        }

        [Test]
        public void LodAwareAtlasFamilyClosure_KeepsStructuralComponentAtomic()
        {
            const string rigidPath = "models/planning.rigid_model_v2";
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, []);
            SetStateProperty(state, "LodAwareAtlasPlanningEnabled", true);

            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var serviceType = assembly.GetType(
                "Editors.KitbasherEditor.Services.PackTextureAtlasBatchService",
                throwOnError: true)!;
            var meshKeyType = serviceType.GetNestedType(
                "MeshKey",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MeshKey was not found.");
            var meshKeyConstructor = meshKeyType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single();
            var mesh0 = meshKeyConstructor.Invoke([rigidPath, 0, 0]);
            var mesh1 = meshKeyConstructor.Invoke([rigidPath, 0, 1]);

            var structuralMeshes = state.GetType()
                .GetProperty(
                    "PreAtlasStructuralMergeMeshes",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(state)
                ?? throw new InvalidOperationException(
                    "PreAtlasStructuralMergeMeshes was not found.");
            var addStructuralMesh = structuralMeshes.GetType().GetMethod("Add")
                ?? throw new InvalidOperationException("Structural mesh set cannot add members.");
            addStructuralMesh.Invoke(structuralMeshes, [mesh0]);
            addStructuralMesh.Invoke(structuralMeshes, [mesh1]);
            var structuralPairs = (ISet<string>)(state.GetType()
                .GetProperty(
                    "PreAtlasStructuralMergePairs",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(state)
                ?? throw new InvalidOperationException(
                    "PreAtlasStructuralMergePairs was not found."));
            structuralPairs.Add(GetStructuralMergePairKey(rigidPath, 0, 0, 1));

            var closed = CloseAtlasPlanningTestBatches(
                state,
                (
                    CreateAtlasPlanningTestCandidate(rigidPath, 0, 0),
                    null),
                (
                    CreateAtlasPlanningTestCandidate(rigidPath, 0, 1),
                    null));

            Assert.That(closed, Has.Count.EqualTo(1));
            Assert.That(closed[0], Has.Count.EqualTo(2));
        }

        [Test]
        public void PreAtlasStructuralMergeGroup_IsAtomic()
        {
            const string rigidPath = "models/source.rigid_model_v2";
            var source = CreateTraversalContainer(
                isCaPackFile: false,
                new Dictionary<string, PackFile>());
            var state = CreateTraversalBatchState(source.Object, []);
            var pair = GetStructuralMergePairKey(rigidPath, 0, 0, 1);
            var pairs = (ISet<string>)(state.GetType()
                .GetProperty(
                    "PreAtlasStructuralMergePairs",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(state)
                ?? throw new InvalidOperationException(
                    "PreAtlasStructuralMergePairs was not found."));
            pairs.Add(pair);

            Assert.Multiple(() =>
            {
                Assert.That(
                    IsPreAtlasStructuralMergeGroup(state, rigidPath, 0, 0, 1),
                    Is.True);
                Assert.That(
                    IsPreAtlasStructuralMergeGroup(state, rigidPath, 0, 0),
                    Is.False);
                Assert.That(
                    IsPreAtlasStructuralMergeGroup(state, rigidPath, 0, 0, 2),
                    Is.False);
            });
        }


    }
}
