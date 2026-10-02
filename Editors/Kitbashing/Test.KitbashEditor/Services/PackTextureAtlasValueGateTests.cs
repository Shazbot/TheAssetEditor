using System.Collections;
using System.Reflection;
using System.Xml;
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

        private static object CreateHealthyResolutionWithRosterVmd(string vmdPath)
            => CreateHealthyResolutionWithRosterVmds(
                (vmdPath, "InfantryMissile"));

        private static object CreateHealthyResolutionWithRosterVmds(
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
                        return true;
                    if (parameter.ParameterType == typeof(int))
                        return 0;
                    if (parameter.ParameterType == typeof(string))
                        return string.Empty;

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

            return constructor.Invoke(arguments);
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
            string materialXml)
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
                        if (name.Contains("width", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("height", StringComparison.OrdinalIgnoreCase))
                        {
                            return 1;
                        }

                        return name.Contains("part", StringComparison.OrdinalIgnoreCase)
                            ? partIndex
                            : 0;
                    }

                    if (type == typeof(double))
                        return 1.0;
                    if (type == typeof(float))
                        return 1.0f;
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

            return constructor.Invoke(arguments);
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
                globalCostBytes: 2.1 * mib,
                expectedCostBytes: 0,
                acceptedNetBcnBytes: 0,
                proposedNetBcnBytes: 2.1 * mib,
                sourceBcnBytes: 400 * mib);
            var twoProspectiveDraws = GetTextureOnlyMergeValueGateBudgetDecision(
                scenarioResolved: true,
                prospectiveTextureMergeDraws: 2,
                expectedArmyDrawsEliminated: 0.01,
                globalCostBytes: 2.1 * mib,
                expectedCostBytes: 0,
                acceptedNetBcnBytes: 0,
                proposedNetBcnBytes: 2.1 * mib,
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

            Assert.Multiple(() =>
            {
                Assert.That(
                    GetStateCollectionCount(state, "MalformedWsModelsIgnored"),
                    Is.EqualTo(1));
                Assert.That(decision.IsBlocked, Is.True);
                Assert.That(
                    decision.Reason,
                    Does.Contain("complete gameplay consumer discovery"));
            });
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
