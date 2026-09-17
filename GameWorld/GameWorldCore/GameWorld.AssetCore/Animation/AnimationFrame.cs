using Microsoft.Xna.Framework;

namespace GameWorld.Core.Animation;

/// <summary>
/// A sampled animation pose. This is intentionally independent from the editor's
/// playback controller so exporters can consume animation data without WPF.
/// </summary>
public class AnimationFrame
{
    public class BoneKeyFrame
    {
        public int BoneIndex { get; set; }
        public int ParentBoneIndex { get; set; }
        public Quaternion Rotation { get; set; }
        public Vector3 Translation { get; set; }
        public Vector3 Scale { get; set; }
        public Matrix WorldTransform { get; set; }

        public void ComputeWorldMatrixFromComponents()
        {
            var rotation = Rotation;
            var translation = Translation;
            var scale = Scale;
            WorldTransform = Matrix.CreateScale(scale)
                * Matrix.CreateFromQuaternion(rotation)
                * Matrix.CreateTranslation(translation);
        }
    }

    public List<BoneKeyFrame> BoneTransforms = new();

    public Matrix GetSkeletonAnimatedWorld(GameSkeleton gameSkeleton, int boneIndex)
    {
        var output = gameSkeleton.GetWorldTransform(boneIndex) * BoneTransforms[boneIndex].WorldTransform;
        return output;
    }

    public Matrix GetSkeletonAnimatedWorldDiff(GameSkeleton gameSkeleton, int boneIndex0, int boneIndex1)
    {
        var bone0Transform = GetSkeletonAnimatedWorld(gameSkeleton, boneIndex0);
        var bone1Transform = GetSkeletonAnimatedWorld(gameSkeleton, boneIndex1);

        return bone1Transform * Matrix.Invert(bone0Transform);
    }

    public int GetParentBoneIndex(GameSkeleton gameSkeleton, int boneIndex)
    {
        return BoneTransforms[boneIndex].ParentBoneIndex;
    }
}
