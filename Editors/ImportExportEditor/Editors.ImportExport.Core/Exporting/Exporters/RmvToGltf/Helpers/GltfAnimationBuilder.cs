using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Editors.ImportExport.Common;
using GameWorld.Core.Animation;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.GameFormats.Animation;
using SharpGLTF.Schema2;
using SysNum = System.Numerics;

namespace Editors.ImportExport.Exporting.Exporters.RmvToGltf.Helpers
{
    public class GltfAnimationBuilder
    {
        private static readonly ILogger Logger = Logging.Create<GltfAnimationBuilder>();
        private readonly GltfAnimationMetadataContextResolver? _metadataResolver;

        public GltfAnimationBuilder(
            IPackedFileLookup? packFileLookup = null,
            GltfAnimationMetadataLookupCacheOptions? metadataCacheOptions = null)
        {
            if (packFileLookup is IHeadlessPackFileService headlessPackFileService)
            {
                _metadataResolver = new GltfAnimationMetadataContextResolver(
                    headlessPackFileService,
                    metadataCacheOptions);
            }
        }

        public virtual void Build(AnimationFile animSkeleton, RmvToGltfExporterSettings settings, ProcessedGltfSkeleton gltfSkeleton, ModelRoot outputScene)
        {                     
            foreach (var animationPackFile in settings.InputAnimationFiles)
            {
                var animationToExport = AnimationFile.Create(animationPackFile);                
                CreateFromTWAnim(animationPackFile, gltfSkeleton, animSkeleton, animationToExport, outputScene, settings);
            }            
        }

        private void CreateFromTWAnim(PackFile animationPackFile, ProcessedGltfSkeleton gltfSkeleton, AnimationFile skeletonAnimFile, AnimationFile animationToExport, ModelRoot modelRoot, RmvToGltfExporterSettings settings)
        {
            var animationName = animationPackFile.Name;
            var doMirror = settings.MirrorMesh;
            var gameSkeleton = new GameSkeleton(skeletonAnimFile, null!);
            var animationClip = new AnimationClip(animationToExport, gameSkeleton);
            IReadOnlyList<AnimationClip.KeyFrame> frames = animationClip.DynamicFrames;

            if (_metadataResolver != null)
            {
                try
                {
                    var metadataContext = _metadataResolver.Resolve(animationPackFile, gameSkeleton);
                    if (metadataContext?.HasRules == true)
                    {
                        frames = GltfAnimationMetadataProcessor.Apply(animationClip, gameSkeleton, metadataContext);
                        Logger.Here().Information(
                            "Applied SuperView animation metadata to {AnimationName}: fragment={FragmentPath}, transforms={TransformCount}, docks={DockCount}",
                            animationName,
                            metadataContext.FragmentPath,
                            metadataContext.TransformRules.Count,
                            metadataContext.DockRules.Count);
                    }
                }
                catch (Exception exception)
                {
                    Logger.Here().Warning(
                        $"Unable to apply SuperView animation metadata to '{animationName}'; exporting the raw animation. {exception.Message}");
                }
            }

            var secondsPerFrame = frames.Count == 0 ? 0 : animationClip.PlayTimeInSec / frames.Count;

            var gltfAnimation = modelRoot.CreateAnimation(animationName);
            var boneCount = frames.Count == 0 ? 0 : Math.Min(frames[0].Position.Count, gltfSkeleton.Data.Count);

            for (var boneIndex = 0; boneIndex < boneCount; boneIndex++)
            {
                var translationKeyFrames = new Dictionary<float, SysNum.Vector3>();
                var rotationKeyFrames = new Dictionary<float, SysNum.Quaternion>();
                var scaleKeyFrames = new Dictionary<float, SysNum.Vector3>();

                // Populate the bone tracks from the raw animation or from the
                // SuperView-compatible metadata-adjusted pose sequence.
                for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
                {
                    translationKeyFrames.Add(secondsPerFrame * frameIndex, GlobalSceneTransforms.FlipVector(frames[frameIndex].Position[boneIndex], doMirror));
                    rotationKeyFrames.Add(secondsPerFrame * frameIndex, GlobalSceneTransforms.FlipQuaternion(frames[frameIndex].Rotation[boneIndex], doMirror));
                    scaleKeyFrames.Add(secondsPerFrame * frameIndex, frames[frameIndex].Scale[boneIndex]);
                }

                var boneNode = gltfSkeleton.Data[boneIndex].Item1;
                gltfAnimation.CreateRotationChannel(boneNode, rotationKeyFrames);
                gltfAnimation.CreateTranslationChannel(boneNode, translationKeyFrames);
                gltfAnimation.CreateScaleChannel(boneNode, scaleKeyFrames);
            }
        }
    }
}
