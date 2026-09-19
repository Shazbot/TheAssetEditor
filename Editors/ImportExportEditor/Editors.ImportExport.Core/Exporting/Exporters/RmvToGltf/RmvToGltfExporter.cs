using System.Diagnostics;
using Editors.ImportExport.Common;
using Editors.ImportExport.Exporting.Exporters.RmvToGltf.Helpers;
using Editors.ImportExport.Misc;
using GameWorld.Core.Services;
using Shared.Core.PackFiles.Models;
using Shared.GameFormats.RigidModel.MaterialHeaders;
using SharpGLTF.Geometry;
using SharpGLTF.Materials;
using SharpGLTF.Schema2;

namespace Editors.ImportExport.Exporting.Exporters.RmvToGltf
{
    public interface IRmvToGltfExporter
    {
        ExportSupportEnum CanExportFile(PackFile file);
        ExportExecutionResult Export(RmvToGltfExporterSettings settings);
    }

    public enum MissingSkeletonAction
    {
        ContinueWithoutSkeleton,
        CancelExport
    }

    public sealed record MissingSkeletonContext(
        string SkeletonName,
        string? Message = null);

    /// <summary>
    /// Decides what to do when an asset names a skeleton which is not present
    /// in the loaded packs. The default members preserve the original
    /// bool-based seam while allowing new callers to exchange a neutral
    /// context and action without knowing about WPF or IPC.
    /// </summary>
    public interface IMissingSkeletonDecision
    {
        MissingSkeletonAction Decide(MissingSkeletonContext context)
            => ContinueWithoutSkeleton(context.SkeletonName)
                ? MissingSkeletonAction.ContinueWithoutSkeleton
                : MissingSkeletonAction.CancelExport;

        bool ContinueWithoutSkeleton(string skeletonName)
            => Decide(new MissingSkeletonContext(skeletonName))
                == MissingSkeletonAction.ContinueWithoutSkeleton;
    }

    public sealed class HeadlessMissingSkeletonDecision : IMissingSkeletonDecision
    {
        public MissingSkeletonAction Decide(MissingSkeletonContext context)
            => MissingSkeletonAction.ContinueWithoutSkeleton;

        public bool ContinueWithoutSkeleton(string skeletonName) => true;
    }

    public class RmvToGltfExporter : IRmvToGltfExporter
    {
        private readonly ILogger _logger = Logging.Create<RmvToGltfExporter>();
        private readonly IGltfSceneSaver _gltfSaver;
        private readonly GltfMeshBuilder _gltfMeshBuilder;
        private readonly IGltfTextureHandler _gltfTextureHandler;
        private readonly GltfSkeletonBuilder _gltfSkeletonBuilder;
        private readonly GltfAnimationBuilder _gltfAnimationBuilder;
        private readonly ISkeletonAnimationLookUpHelper _skeletonLookUpHelper;
        private readonly IModelAssetResolver _modelAssetResolver;
        private readonly IVariantMeshCompositionResolver? _variantMeshResolver;
        private readonly IMissingSkeletonDecision _missingSkeletonDecision;

        // Keep the pre-composition constructor signature intact. The
        // composition resolver is an additive dependency for VMD exports.
        public RmvToGltfExporter(
            IGltfSceneSaver gltfSaver,
            GltfMeshBuilder gltfMeshBuilder,
            IGltfTextureHandler gltfTextureHandler,
            GltfSkeletonBuilder gltfSkeletonsBuilder,
            GltfAnimationBuilder gltfAnimationCreator,
            ISkeletonAnimationLookUpHelper skeletonLookUpHelper,
            IModelAssetResolver? modelAssetResolver = null)
            : this(
                gltfSaver,
                gltfMeshBuilder,
                gltfTextureHandler,
                gltfSkeletonsBuilder,
                gltfAnimationCreator,
                skeletonLookUpHelper,
                modelAssetResolver,
                null,
                null)
        {
        }

        public RmvToGltfExporter(
            IGltfSceneSaver gltfSaver,
            GltfMeshBuilder gltfMeshBuilder,
            IGltfTextureHandler gltfTextureHandler,
            GltfSkeletonBuilder gltfSkeletonsBuilder,
            GltfAnimationBuilder gltfAnimationCreator,
            ISkeletonAnimationLookUpHelper skeletonLookUpHelper,
            IModelAssetResolver? modelAssetResolver,
            IVariantMeshCompositionResolver? variantMeshResolver,
            IMissingSkeletonDecision? missingSkeletonDecision = null)
        {
            _gltfSaver = gltfSaver;
            _gltfMeshBuilder = gltfMeshBuilder;
            _gltfTextureHandler = gltfTextureHandler;
            _gltfSkeletonBuilder = gltfSkeletonsBuilder;
            _gltfAnimationBuilder = gltfAnimationCreator;
            _skeletonLookUpHelper = skeletonLookUpHelper;
            _modelAssetResolver = modelAssetResolver ?? new ModelAssetResolver();
            _variantMeshResolver = variantMeshResolver;
            // Core is deliberately deterministic. Interactive callers must
            // supply their own decision adapter.
            _missingSkeletonDecision = missingSkeletonDecision ?? new HeadlessMissingSkeletonDecision();
        }

        public ExportSupportEnum CanExportFile(PackFile file)
        {
            if (FileExtensionHelper.IsRmvFile(file.Name)
                || FileExtensionHelper.IsWsModelFile(file.Name)
                || IsVariantMeshDefinition(file))
                return ExportSupportEnum.HighPriority;
            return ExportSupportEnum.NotSupported;
        }

        ExportSupportEnum IRmvToGltfExporter.CanExportFile(PackFile file) => CanExportFile(file);

        ExportExecutionResult IRmvToGltfExporter.Export(RmvToGltfExporterSettings settings)
            => Export(settings);

        public ExportExecutionResult Export(RmvToGltfExporterSettings settings)
        {
            LogSettings(settings);

            if (IsVariantMeshDefinition(settings.InputModelFile))
                return ExportVariantMesh(settings);

            var totalStopwatch = Stopwatch.StartNew();
            var phaseStopwatch = Stopwatch.StartNew();

            var resolvedAsset = _modelAssetResolver.Resolve(settings.InputModelFile);
            phaseStopwatch.Stop();
            var resolveMs = phaseStopwatch.ElapsedMilliseconds;
            foreach (var diagnostic in resolvedAsset.Diagnostics)
                _logger.Here().Warning(diagnostic);

            var outputScene = ModelRoot.CreateModel();
            var modelPart = new ExportModelPart(resolvedAsset, string.Empty, "model", true, true);
            ProcessedGltfSkeleton? skeleton = null;
            global::Shared.GameFormats.Animation.AnimationFile? skeletonFile = null;
            var skeletonMs = 0L;
            if (settings.IncludeSkeleton)
            {
                phaseStopwatch.Restart();
                skeleton = CreateSharedSkeleton(
                    [modelPart],
                    settings,
                    outputScene,
                    warnWhenMissing: true,
                    out skeletonFile,
                    out var exportCancelled);
                phaseStopwatch.Stop();
                skeletonMs = phaseStopwatch.ElapsedMilliseconds;
                if (exportCancelled)
                {
                    totalStopwatch.Stop();
                    _logger.Here().Information(
                        "RMV/WS export timing for {AssetName}: status=cancelled, total={TotalMs}ms, resolve={ResolveMs}ms, skeleton={SkeletonMs}ms",
                        settings.InputModelFile.Name,
                        totalStopwatch.ElapsedMilliseconds,
                        resolveMs,
                        skeletonMs);
                    return ExportExecutionResult.Cancelled();
                }
            }

            var animationMs = 0L;
            if (skeleton != null && skeletonFile != null && settings.ExportAnimations)
            {
                phaseStopwatch.Restart();
                _gltfAnimationBuilder.Build(skeletonFile, settings, skeleton, outputScene);
                phaseStopwatch.Stop();
                animationMs = phaseStopwatch.ElapsedMilliseconds;
            }

            var textureSession = new GltfTextureExportSession(collisionSafe: false);
            phaseStopwatch.Restart();
            var textures = _gltfTextureHandler.HandleTextures(resolvedAsset, settings, textureSession);
            phaseStopwatch.Stop();
            var texturesMs = phaseStopwatch.ElapsedMilliseconds;

            phaseStopwatch.Restart();
            var meshes = BuildMeshes(modelPart, textures, settings, skeleton != null);
            phaseStopwatch.Stop();
            var meshesMs = phaseStopwatch.ElapsedMilliseconds;

            var sceneTiming = BuildGltfScene(
                meshes,
                skeleton,
                settings,
                outputScene,
                textures.Select(x => x.SystemFilePath).ToArray());
            var sceneAndSaveMs = sceneTiming.TotalMs;
            totalStopwatch.Stop();

            _logger.Here().Information(
                "RMV/WS export timing for {AssetName}: status=completed, total={TotalMs}ms, resolve={ResolveMs}ms, skeleton={SkeletonMs}ms, animations={AnimationMs}ms, textures={TexturesMs}ms, meshes={MeshesMs}ms, sceneAndSave={SceneAndSaveMs}ms (sceneBuild={SceneBuildMs}ms, save={SaveMs}ms), meshCount={MeshCount}, textureCount={TextureCount}",
                settings.InputModelFile.Name,
                totalStopwatch.ElapsedMilliseconds,
                resolveMs,
                skeletonMs,
                animationMs,
                texturesMs,
                meshesMs,
                sceneAndSaveMs,
                sceneTiming.SceneBuildMs,
                sceneTiming.SaveMs,
                meshes.Count,
                textures.Count);

            return ExportExecutionResult.Completed();
        }

        private ExportExecutionResult ExportVariantMesh(RmvToGltfExporterSettings settings)
        {
            if (_variantMeshResolver == null)
                throw new InvalidOperationException("VariantMeshDefinition export requires the variant mesh composition resolver.");

            var totalStopwatch = Stopwatch.StartNew();
            var phaseStopwatch = Stopwatch.StartNew();

            var composition = settings.VariantMeshSelections.Count > 0
                ? _variantMeshResolver.Resolve(settings.InputModelFile, settings.VariantMeshSelections)
                : _variantMeshResolver.Resolve(settings.InputModelFile);
            phaseStopwatch.Stop();
            var compositionMs = phaseStopwatch.ElapsedMilliseconds;
            foreach (var diagnostic in composition.Diagnostics)
                _logger.Here().Warning(diagnostic);

            if (!composition.HasRenderableContent || composition.Root == null)
            {
                var details = composition.Diagnostics.Count == 0
                    ? "No renderable model candidates were found."
                    : string.Join(Environment.NewLine, composition.Diagnostics);
                throw new InvalidOperationException($"Unable to resolve VariantMeshDefinition '{settings.InputModelFile.Name}'. {details}");
            }

            phaseStopwatch.Restart();
            var modelParts = FlattenModelParts(composition.Root);
            if (modelParts.Count == 0)
                throw new InvalidOperationException($"VariantMeshDefinition '{settings.InputModelFile.Name}' contains no renderable models.");
            phaseStopwatch.Stop();
            var flattenMs = phaseStopwatch.ElapsedMilliseconds;

            var outputScene = ModelRoot.CreateModel();
            ProcessedGltfSkeleton? skeleton = null;
            global::Shared.GameFormats.Animation.AnimationFile? skeletonFile = null;
            var skeletonMs = 0L;
            if (settings.IncludeSkeleton)
            {
                phaseStopwatch.Restart();
                skeleton = CreateSharedSkeleton(
                    modelParts,
                    settings,
                    outputScene,
                    warnWhenMissing: false,
                    out skeletonFile,
                    out _);
                phaseStopwatch.Stop();
                skeletonMs = phaseStopwatch.ElapsedMilliseconds;
            }

            phaseStopwatch.Restart();
            modelParts = ApplySharedSkeletonCompatibility(modelParts, skeleton);
            phaseStopwatch.Stop();
            var skeletonCompatibilityMs = phaseStopwatch.ElapsedMilliseconds;

            var textureSession = new GltfTextureExportSession(collisionSafe: true);
            var meshes = new List<ExportedMesh>();
            var generatedTexturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var texturesMs = 0L;
            var meshesMs = 0L;

            foreach (var modelPart in modelParts)
            {
                phaseStopwatch.Restart();
                var textures = _gltfTextureHandler.HandleTextures(modelPart.Asset, settings, textureSession);
                phaseStopwatch.Stop();
                texturesMs += phaseStopwatch.ElapsedMilliseconds;
                generatedTexturePaths.UnionWith(textures.Select(x => x.SystemFilePath));

                phaseStopwatch.Restart();
                meshes.AddRange(BuildMeshes(modelPart, textures, settings, skeleton != null && modelPart.UseSharedSkeleton));
                phaseStopwatch.Stop();
                meshesMs += phaseStopwatch.ElapsedMilliseconds;
            }

            var animationMs = 0L;
            if (skeleton != null && skeletonFile != null && settings.ExportAnimations)
            {
                phaseStopwatch.Restart();
                _gltfAnimationBuilder.Build(skeletonFile, settings, skeleton, outputScene);
                phaseStopwatch.Stop();
                animationMs = phaseStopwatch.ElapsedMilliseconds;
            }

            _logger.Here().Debug($"VMD Export - Parts={modelParts.Count} MeshCount={meshes.Count} Skeleton={skeleton?.Data.Count}");

            var sceneTiming = BuildGltfScene(
                meshes,
                skeleton,
                settings,
                outputScene,
                generatedTexturePaths);
            var sceneAndSaveMs = sceneTiming.TotalMs;
            totalStopwatch.Stop();

            _logger.Here().Information(
                "VMD export timing for {AssetName}: total={TotalMs}ms, composition={CompositionMs}ms, flatten={FlattenMs}ms, skeleton={SkeletonMs}ms, skeletonCompatibility={SkeletonCompatibilityMs}ms, textures={TexturesMs}ms, meshes={MeshesMs}ms, animations={AnimationMs}ms, sceneAndSave={SceneAndSaveMs}ms (sceneBuild={SceneBuildMs}ms, save={SaveMs}ms), parts={PartCount}, meshCount={MeshCount}, textureCount={TextureCount}",
                settings.InputModelFile.Name,
                totalStopwatch.ElapsedMilliseconds,
                compositionMs,
                flattenMs,
                skeletonMs,
                skeletonCompatibilityMs,
                texturesMs,
                meshesMs,
                animationMs,
                sceneAndSaveMs,
                sceneTiming.SceneBuildMs,
                sceneTiming.SaveMs,
                modelParts.Count,
                meshes.Count,
                generatedTexturePaths.Count);

            return ExportExecutionResult.Completed();
        }

        private ProcessedGltfSkeleton? CreateSharedSkeleton(
            IReadOnlyList<ExportModelPart> modelParts,
            RmvToGltfExporterSettings settings,
            ModelRoot outputScene,
            bool warnWhenMissing,
            out global::Shared.GameFormats.Animation.AnimationFile? skeletonFile,
            out bool exportCancelled)
        {
            skeletonFile = null;
            exportCancelled = false;
            var skeletonDiagnostics = new List<string>();
            var skeletonName = GltfAnimationCatalogResolver.SelectSharedSkeletonName(
                modelParts.Select(x => x.Asset),
                skeletonDiagnostics);
            if (string.IsNullOrWhiteSpace(skeletonName))
                return null;

            foreach (var diagnostic in skeletonDiagnostics)
                _logger.Here().Warning(diagnostic);

            skeletonFile = _skeletonLookUpHelper.GetSkeletonFileFromName(skeletonName);
            if (skeletonFile == null)
            {
                var message = $"Skeleton '{skeletonName}' was not found; exporting without a glTF skeleton.";
                _logger.Here().Warning(message);
                if (warnWhenMissing && settings.ExportAnimations
                    && _missingSkeletonDecision.Decide(new MissingSkeletonContext(skeletonName, message))
                        == MissingSkeletonAction.CancelExport)
                {
                    exportCancelled = true;
                    return null;
                }
                return null;
            }

            var gltfSkeleton = _gltfSkeletonBuilder.CreateSkeleton(skeletonFile, outputScene, settings);
            return gltfSkeleton;
        }

        private List<ExportModelPart> FlattenModelParts(ResolvedVariantMeshNode node)
        {
            var output = new List<ExportModelPart>();
            var nextIndex = 0;
            foreach (var component in GltfAnimationCatalogResolver.EnumerateComponents(node))
            {
                output.Add(new ExportModelPart(
                    component.Asset,
                    component.AttachmentPoint,
                    $"vmd_part_{nextIndex++:D3}",
                    false,
                    true));
            }
            return output;
        }

        private List<ExportedMesh> BuildMeshes(
            ExportModelPart modelPart,
            List<TextureResult> textures,
            RmvToGltfExporterSettings settings,
            bool willHaveSkeleton)
        {
            var output = new List<ExportedMesh>();
            var meshBuilders = _gltfMeshBuilder.Build(
                modelPart.Asset,
                textures,
                settings,
                willHaveSkeleton,
                modelPart.NamePrefix == "model" ? null : modelPart.NamePrefix);

            for (var i = 0; i < meshBuilders.Count; i++)
            {
                var matrixIndex = -1;
                if (i < modelPart.Asset.FirstLod.Count
                    && modelPart.Asset.FirstLod[i].Material.SourceMaterial is WeightedMaterial weightedMaterial)
                    matrixIndex = weightedMaterial.MatrixIndex;

                var hasWeights = i < modelPart.Asset.FirstLod.Count
                    && modelPart.Asset.FirstLod[i].Model.Mesh.VertexList.Any(x => x.WeightCount > 0);
                var pivotPoint = i < modelPart.Asset.FirstLod.Count
                    ? GlobalSceneTransforms.FlipVector(
                        modelPart.Asset.FirstLod[i].Material.SourceMaterial.PivotPoint,
                        settings.MirrorMesh)
                    : System.Numerics.Vector3.Zero;
                output.Add(new ExportedMesh(
                    meshBuilders[i],
                    modelPart.AttachmentPoint,
                    matrixIndex,
                    hasWeights,
                    willHaveSkeleton,
                    modelPart.AllowMatrixAttachment,
                    pivotPoint));
            }

            return output;
        }

        internal GltfSceneTiming BuildGltfScene(
            List<ExportedMesh> meshes,
            ProcessedGltfSkeleton? gltfSkeleton,
            RmvToGltfExporterSettings settings,
            ModelRoot outputScene,
            IReadOnlyCollection<string>? generatedTexturePaths = null)
        {
            var totalStopwatch = Stopwatch.StartNew();
            var sceneBuildStopwatch = Stopwatch.StartNew();

            var scene = outputScene.UseScene("default");

            var meshCreateStopwatch = Stopwatch.StartNew();
            var schemaMeshes = meshes.Count == 0
                ? Array.Empty<Mesh>()
                : outputScene.CreateMeshes(meshes.Select(x => x.MeshBuilder).ToArray()).ToArray();
            meshCreateStopwatch.Stop();

            if (schemaMeshes.Length != meshes.Count)
                throw new InvalidOperationException(
                    $"SharpGLTF created {schemaMeshes.Length} meshes for {meshes.Count} mesh builders.");

            var nodeAttachStopwatch = Stopwatch.StartNew();
            for (var meshIndex = 0; meshIndex < meshes.Count; meshIndex++)
            {
                var exportedMesh = meshes[meshIndex];
                var mesh = schemaMeshes[meshIndex];
                Node? parent = null;

                if (gltfSkeleton != null)
                {
                    var attachmentBone = FindAttachmentBone(
                        gltfSkeleton,
                        exportedMesh.AttachmentPoint,
                        exportedMesh.MatrixIndex,
                        exportedMesh.AllowMatrixAttachment);
                    if (attachmentBone != null)
                        parent = attachmentBone;
                }

                var node = parent?.CreateNode(mesh.Name) ?? scene.CreateNode(mesh.Name);
                // Rigid attachment is the renderer's precedence rule: an
                // attachment resolver plus a valid RMV matrix override means
                // the mesh follows that bone as a rigid object, so its vertex
                // weights must not apply the same skeleton a second time.
                var followsBoneRigidly = parent != null && exportedMesh.MatrixIndex >= 0;
                node.WithLocalTranslation(exportedMesh.PivotPoint);
                if (gltfSkeleton != null
                    && exportedMesh.CanUseSkeleton
                    && exportedMesh.HasWeights
                    && !followsBoneRigidly)
                    node.WithSkinnedMesh(mesh, gltfSkeleton.Data.ToArray());
                else
                    node.WithMesh(mesh);
            }
            nodeAttachStopwatch.Stop();

            sceneBuildStopwatch.Stop();

            _logger.Here().Debug(
                "GLTF scene build timing for {OutputName}: total={TotalMs:F1}ms, createMeshes={CreateMeshesMs:F1}ms, attachNodes={AttachNodesMs:F1}ms, meshes={MeshCount}",
                Path.GetFileName(settings.OutputPath),
                sceneBuildStopwatch.Elapsed.TotalMilliseconds,
                meshCreateStopwatch.Elapsed.TotalMilliseconds,
                nodeAttachStopwatch.Elapsed.TotalMilliseconds,
                meshes.Count);

            var saveStopwatch = Stopwatch.StartNew();
            _gltfSaver.Save(
                outputScene,
                settings.OutputPath,
                generatedTexturePaths ?? Array.Empty<string>());
            saveStopwatch.Stop();
            totalStopwatch.Stop();

            var timing = new GltfSceneTiming(
                totalStopwatch.ElapsedMilliseconds,
                sceneBuildStopwatch.ElapsedMilliseconds,
                saveStopwatch.ElapsedMilliseconds);

            _logger.Here().Debug(
                "GLTF scene/save timing for {OutputName}: total={TotalMs}ms, sceneBuild={SceneBuildMs}ms, save={SaveMs}ms, meshes={MeshCount}, generatedTextures={GeneratedTextureCount}",
                Path.GetFileName(settings.OutputPath),
                timing.TotalMs,
                timing.SceneBuildMs,
                timing.SaveMs,
                meshes.Count,
                generatedTexturePaths?.Count ?? 0);

            return timing;
        }

        internal static Node? FindAttachmentBone(
            ProcessedGltfSkeleton skeleton,
            string attachmentPoint,
            int matrixIndex,
            bool allowMatrixIndex = true)
        {
            if (!string.IsNullOrWhiteSpace(attachmentPoint))
            {
                // Match SceneObjectEditor.WireAttachmentResolvers: a named
                // attachment is authoritative.  An unknown name does not
                // fall back to the RMV matrix index.
                return skeleton.Data
                    .Select(x => x.Item1)
                    .FirstOrDefault(x => string.Equals(x.Name, attachmentPoint, StringComparison.OrdinalIgnoreCase));
            }

            if (allowMatrixIndex && matrixIndex >= 0 && matrixIndex < skeleton.Data.Count)
                return skeleton.Data[matrixIndex].Item1;

            return null;
        }

        private List<ExportModelPart> ApplySharedSkeletonCompatibility(
            List<ExportModelPart> modelParts,
            ProcessedGltfSkeleton? skeleton)
        {
            if (skeleton == null)
                return modelParts;

            var sharedSkeletonName = GltfAnimationCatalogResolver.SelectSharedSkeletonName(
                modelParts.Select(x => x.Asset));
            if (string.IsNullOrWhiteSpace(sharedSkeletonName))
                return modelParts;

            return modelParts.Select(modelPart =>
            {
                var componentSkeletonName = modelPart.Asset.Model.Header.SkeletonName;
                var canUseSharedSkeleton = string.Equals(
                    componentSkeletonName,
                    sharedSkeletonName,
                    StringComparison.OrdinalIgnoreCase);

                if (!canUseSharedSkeleton && string.IsNullOrWhiteSpace(componentSkeletonName) == false)
                {
                    _logger.Here().Warning(
                        $"VMD component '{modelPart.Asset.InputFile.Name}' uses skeleton '{componentSkeletonName}', "
                        + $"which differs from shared skeleton '{sharedSkeletonName}'; exporting it without skinning.");
                }

                return modelPart with
                {
                    UseSharedSkeleton = canUseSharedSkeleton,
                    // A component with a different named skeleton cannot
                    // safely interpret its own MatrixIndex in the shared
                    // skeleton. Named VMD attachments remain resolvable by
                    // name; pure MatrixIndex attachments stay unparented.
                    AllowMatrixAttachment = canUseSharedSkeleton
                        || string.IsNullOrWhiteSpace(modelPart.AttachmentPoint) == false
                };
            }).ToList();
        }

        private static bool IsVariantMeshDefinition(PackFile file)
            => file.Name.EndsWith(".variantmeshdefinition", StringComparison.OrdinalIgnoreCase);

        private void LogSettings(RmvToGltfExporterSettings settings)
        {
            var str = $"Exporting using {nameof(RmvToGltfExporter)}\n";
            str += $"\tInputModelFile:{settings.InputModelFile?.Name}\n";
            str += $"\tInputAnimationFiles:{settings.InputAnimationFiles?.Count()}\n";
            str += $"\tOutputPath:{settings.OutputPath}\n";
            str += $"\tConvertMaterialTextureToBlender:{settings.ConvertMaterialTextureToBlender}\n";
            str += $"\tConvertNormalTextureToBlue:{settings.ConvertNormalTextureToBlue}\n";
            str += $"\tExportAnimations:{settings.ExportAnimations}\n";
            str += $"\tMirrorMesh:{settings.MirrorMesh}\n";
            str += $"\tMaxTextureParallelism:{settings.MaxTextureParallelism}\n";

            _logger.Here().Debug(str);
        }

        private sealed record ExportModelPart(
            ResolvedModelAsset Asset,
            string AttachmentPoint,
            string NamePrefix,
            bool UseSharedSkeleton,
            bool AllowMatrixAttachment);

        internal sealed record GltfSceneTiming(
            long TotalMs,
            long SceneBuildMs,
            long SaveMs);

        internal sealed record ExportedMesh(
            IMeshBuilder<MaterialBuilder> MeshBuilder,
            string AttachmentPoint,
            int MatrixIndex,
            bool HasWeights,
            bool CanUseSkeleton,
            bool AllowMatrixAttachment,
            System.Numerics.Vector3 PivotPoint);
    }
}
