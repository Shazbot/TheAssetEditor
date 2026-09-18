using Editors.Shared.Core.Common.ReferenceModel;
using GameWorld.Core.Animation;
using Microsoft.Xna.Framework;
using Shared.Core.Misc;

namespace Editor.VisualSkeletonEditor.SkeletonEditor
{
    class BoneTransformHandler
    {
        public static void Translate(SkeletonBoneNode _selectedBone, GameSkeleton Skeleton, Vector3 translationValue, Vector3 rotation, bool ShowBonesAsWorldTransform)
        {
            if (_selectedBone == null)
                return;

            var boneIndex = _selectedBone.BoneIndex;

            var quaternionValue = MathUtil.EulerDegreesToQuaternion(rotation);

            if (ShowBonesAsWorldTransform)
            {
                var parentIndex = Skeleton.GetParentBoneIndex(boneIndex);
                if (parentIndex != -1)
                {
                    var parentTransform = Skeleton.GetWorldTransform(parentIndex);

                    var rotationWorld = MathUtil.EulerDegreesToQuaternion(rotation);
                    var translationWorld = translationValue;
                    var currentMatrixWorld = System.Numerics.Matrix4x4.CreateFromQuaternion(
                            new System.Numerics.Quaternion(rotationWorld.X, rotationWorld.Y, rotationWorld.Z, rotationWorld.W))
                        * System.Numerics.Matrix4x4.CreateTranslation(
                            new System.Numerics.Vector3(translationWorld.X, translationWorld.Y, translationWorld.Z));

                    if (!System.Numerics.Matrix4x4.Invert(parentTransform, out var inverseParent))
                        throw new InvalidOperationException("Unable to invert the parent bone transform.");
                    var localSpaceMatrix = currentMatrixWorld * inverseParent;
                    if (!System.Numerics.Matrix4x4.Decompose(localSpaceMatrix, out _, out var numericsQuaternion, out var numericsTranslation))
                        throw new InvalidOperationException("Unable to decompose the local bone transform.");
                    quaternionValue = new Quaternion(numericsQuaternion.X, numericsQuaternion.Y, numericsQuaternion.Z, numericsQuaternion.W);
                    translationValue = new Vector3(numericsTranslation.X, numericsTranslation.Y, numericsTranslation.Z);
                }
            }

            Skeleton.Translation[boneIndex] = new System.Numerics.Vector3(translationValue.X, translationValue.Y, translationValue.Z);
            Skeleton.Rotation[boneIndex] = new System.Numerics.Quaternion(quaternionValue.X, quaternionValue.Y, quaternionValue.Z, quaternionValue.W);
            Skeleton.RebuildSkeletonMatrix();
        }

        public static void Scale(SkeletonBoneNode _selectedBone, GameSkeleton Skeleton, float boneScale)
        {
            if (_selectedBone == null)
                return;

            var boneIndex = _selectedBone.BoneIndex;
            Skeleton.Scale[boneIndex] = boneScale;
            Skeleton.RebuildSkeletonMatrix();
        }
    }
}
