using GameWorld.Core.SceneNodes;
using Microsoft.Xna.Framework;

namespace GameWorld.Core.Utility
{
    public class SkeletonBoneAnimationResolver
    {
        private readonly ISkeletonProvider _animationProvider;
        private readonly int _boneIndex;

        public SkeletonBoneAnimationResolver(ISkeletonProvider gameSkeleton, int boneIndex)
        {
            _animationProvider = gameSkeleton;
            _boneIndex = boneIndex;
        }

        public Matrix GetWorldTransform()
        {
            return _animationProvider.Skeleton.GetAnimatedWorldTranform(_boneIndex);
        }

        /// <summary>
        /// World transform of the attach bone. Falls back to the skeleton's bind pose when no
        /// animation frame is available (player paused, scrubbed, or no clip loaded) so attached
        /// meshes (variantmesh attach_point weapons/shields) still sit on their bone instead of
        /// collapsing to the model origin.
        /// </summary>
        public Matrix GetWorldTransformIfAnimating()
        {
            var skeleton = _animationProvider.Skeleton;
            if (skeleton == null || _boneIndex < 0 || _boneIndex >= skeleton.BoneCount)
                return Matrix.Identity;
            return skeleton.GetAnimatedWorldTranform(_boneIndex);
        }

        public Matrix GetTransformIfAnimating()
        {
            var skeleton = _animationProvider.Skeleton;
            if (skeleton == null || _boneIndex < 0 || _boneIndex >= skeleton.BoneCount)
                return Matrix.Identity;
            return skeleton.GetAnimatedTranform(_boneIndex);
        }
    }
}
