using GameWorld.Core.Animation;
using GameWorld.Core.Animation.AnimationChange;
using GameWorld.Core.SceneNodes;
using System.Numerics;

namespace Editors.AnimationMeta.SuperView.Visualisation.Rules
{
    public class DockEquipmentRule : IWorldSpaceAnimationRule
    {
        ILogger _logger = Logging.Create<CopyRootTransform>();
        bool _hasError = false;

        int _equipmentSlotToDock;
        ISkeletonProvider _skeletonProvider;
        float _startTime;
        float _endTime;
        int _dockTargetkBoneId;
        Matrix4x4 _offset;

        public DockEquipmentRule(int dockTargetkBoneId, int equipmentSlotToDock, AnimationClip dockAnimation, ISkeletonProvider skeletonProvider, float startTime, float endTime)
        {
            _dockTargetkBoneId = dockTargetkBoneId;
            _skeletonProvider = skeletonProvider;
            _startTime = startTime;
            _endTime = endTime;

            try
            {
                _equipmentSlotToDock = skeletonProvider.Skeleton.GetBoneIndexByName("be_prop_" + (equipmentSlotToDock - 1));
                var offsetFrame = AnimationSampler.Sample(0, _skeletonProvider.Skeleton, dockAnimation);
                _offset = offsetFrame.GetSkeletonAnimatedWorldDiff(_skeletonProvider.Skeleton, _dockTargetkBoneId, _equipmentSlotToDock);
            }
            catch (Exception e)
            {
                _logger.Here().Error($"Error in {nameof(DockEquipmentRule)} - {e.Message}");
                _hasError = true;
            }
        }

        public void TransformFrameWorldSpace(AnimationFrame frame, float time)
        {
            if (_hasError)
                return;

            try
            {
                if (time >= _startTime)
                {
                    // World-space rules run before AnimationSampler removes the skeleton bind
                    // transform from the sampled frame.  Use the composed world matrix from the
                    // current frame directly; GetSkeletonAnimatedWorld would apply the bind
                    // transform a second time here.
                    var propTransform = frame.BoneTransforms[_dockTargetkBoneId].WorldTransform;
                    frame.BoneTransforms[_equipmentSlotToDock].WorldTransform = _offset * propTransform;
                }
            }
            catch (Exception e)
            {
                _logger.Here().Error($"Error in {nameof(DockEquipmentRule)} - {e.Message}");
                _hasError = true;
            }
        }
    }
}
