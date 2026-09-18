using System.Numerics;

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
        public Matrix4x4 WorldTransform { get; set; }

        public void ComputeWorldMatrixFromComponents()
        {
            var rotation = Rotation;
            var translation = Translation;
            var scale = Scale;
            WorldTransform = Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateFromQuaternion(rotation)
                * Matrix4x4.CreateTranslation(translation);
        }
    }

    public List<BoneKeyFrame> BoneTransforms = new();

    public Matrix4x4 GetSkeletonAnimatedWorld(GameSkeleton gameSkeleton, int boneIndex)
    {
        var output = gameSkeleton.GetWorldTransform(boneIndex) * BoneTransforms[boneIndex].WorldTransform;
        return output;
    }

    public Matrix4x4 GetSkeletonAnimatedWorldDiff(GameSkeleton gameSkeleton, int boneIndex0, int boneIndex1)
    {
        var bone0Transform = GetSkeletonAnimatedWorld(gameSkeleton, boneIndex0);
        var bone1Transform = GetSkeletonAnimatedWorld(gameSkeleton, boneIndex1);

        if (!Matrix4x4.Invert(bone0Transform, out var inverse))
            throw new InvalidOperationException($"Unable to invert animated bone transform {boneIndex0}.");
        return bone1Transform * inverse;
    }

    public int GetParentBoneIndex(GameSkeleton gameSkeleton, int boneIndex)
    {
        return BoneTransforms[boneIndex].ParentBoneIndex;
    }
}
