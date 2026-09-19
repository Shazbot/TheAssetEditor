using System.Numerics;
using GameWorld.Core.Animation;

namespace Editors.ImportExport.Exporting.Exporters.RmvToGltf.Helpers;

internal static class GltfAnimationMetadataProcessor
{
    private static readonly ILogger Logger = Logging.Create<GltfAnimationMetadataProcessor>();

    public static IReadOnlyList<AnimationClip.KeyFrame> Apply(
        AnimationClip source,
        GameSkeleton skeleton,
        GltfAnimationMetadataContext context)
    {
        if (!context.HasRules || source.DynamicFrames.Count == 0)
            return source.DynamicFrames;

        var resolvedDockRules = ResolveDockRules(source, skeleton, context.DockRules);
        var output = new List<AnimationClip.KeyFrame>(source.DynamicFrames.Count);

        foreach (var sourceFrame in source.DynamicFrames)
        {
            var frame = NormalizeFrame(sourceFrame, skeleton);
            var localMatrices = BuildLocalMatrices(frame);

            foreach (var rule in context.TransformRules)
            {
                if (rule.TargetNode < 0 || rule.TargetNode >= skeleton.BoneCount)
                    continue;

                // SuperView's TransformBoneRule composes this offset before the
                // sampled local transform and does not gate it by metadata time.
                var orientation = new Quaternion(
                    rule.Orientation.X,
                    rule.Orientation.Y,
                    rule.Orientation.Z,
                    rule.Orientation.W);
                localMatrices[rule.TargetNode] =
                    Matrix4x4.CreateFromQuaternion(orientation)
                    * Matrix4x4.CreateTranslation(rule.Position)
                    * localMatrices[rule.TargetNode];
                WriteLocalTransform(frame, rule.TargetNode, localMatrices[rule.TargetNode]);
            }

            var worldMatrices = BuildWorldMatrices(localMatrices, skeleton);
            foreach (var rule in resolvedDockRules)
            {
                var desiredPropWorld = rule.Offset * worldMatrices[rule.TargetBoneIndex];
                var parentIndex = skeleton.GetParentBoneIndex(rule.PropBoneIndex);
                var local = desiredPropWorld;
                if (parentIndex >= 0)
                {
                    if (!Matrix4x4.Invert(worldMatrices[parentIndex], out var inverseParent))
                    {
                        Logger.Here().Warning(
                            $"Unable to invert parent transform for docked prop bone {rule.PropBoneIndex}.");
                        continue;
                    }

                    local = desiredPropWorld * inverseParent;
                }

                localMatrices[rule.PropBoneIndex] = local;
                worldMatrices[rule.PropBoneIndex] = desiredPropWorld;
                WriteLocalTransform(frame, rule.PropBoneIndex, local);
            }

            output.Add(frame);
        }

        return output;
    }

    private static List<ResolvedDockRule> ResolveDockRules(
        AnimationClip source,
        GameSkeleton skeleton,
        IReadOnlyList<GltfDockEquipmentMetadataRule> rules)
    {
        var output = new List<ResolvedDockRule>();

        foreach (var rule in rules)
        {
            // Match SuperView/AnimationSampler behavior. It passes the clip's full
            // duration to DockEquipmentRule and DockEquipmentRule never checks EndTime.
            if (source.PlayTimeInSec < rule.StartTime)
                continue;

            var propBoneIndex = skeleton.GetBoneIndexByName("be_prop_" + (rule.PropBoneId - 1));
            if (propBoneIndex < 0)
            {
                Logger.Here().Warning(
                    $"Unable to apply equipment docking: be_prop_{rule.PropBoneId - 1} is missing.");
                continue;
            }

            var targetBoneIndex = -1;
            foreach (var targetBoneName in rule.SkeletonNameAlternatives)
            {
                targetBoneIndex = skeleton.GetBoneIndexByName(targetBoneName);
                if (targetBoneIndex >= 0)
                    break;
            }

            if (targetBoneIndex < 0)
            {
                Logger.Here().Warning(
                    $"Unable to apply equipment docking: none of [{string.Join(", ", rule.SkeletonNameAlternatives)}] exist.");
                continue;
            }

            if (rule.DockAnimation.DynamicFrames.Count == 0)
                continue;

            var dockFrame = NormalizeFrame(rule.DockAnimation.DynamicFrames[0], skeleton);
            var dockWorld = BuildWorldMatrices(BuildLocalMatrices(dockFrame), skeleton);
            if (!Matrix4x4.Invert(dockWorld[targetBoneIndex], out var inverseTarget))
            {
                Logger.Here().Warning(
                    $"Unable to invert docking target bone {targetBoneIndex} for prop bone {propBoneIndex}.");
                continue;
            }

            // Equivalent to AnimationFrame.GetSkeletonAnimatedWorldDiff(target, prop):
            // the prop's authored docking pose relative to the target bone.
            var offset = dockWorld[propBoneIndex] * inverseTarget;
            output.Add(new ResolvedDockRule(propBoneIndex, targetBoneIndex, offset));
        }

        return output;
    }

    private static AnimationClip.KeyFrame NormalizeFrame(
        AnimationClip.KeyFrame source,
        GameSkeleton skeleton)
    {
        var frame = new AnimationClip.KeyFrame();

        for (var boneIndex = 0; boneIndex < skeleton.BoneCount; boneIndex++)
        {
            frame.Position.Add(
                boneIndex < source.Position.Count
                    ? source.Position[boneIndex]
                    : skeleton.Translation[boneIndex]);
            frame.Rotation.Add(
                boneIndex < source.Rotation.Count
                    ? source.Rotation[boneIndex]
                    : skeleton.Rotation[boneIndex]);
            frame.Scale.Add(
                boneIndex < source.Scale.Count
                    ? source.Scale[boneIndex]
                    : new Vector3(skeleton.Scale[boneIndex]));
        }

        return frame;
    }

    private static Matrix4x4[] BuildLocalMatrices(AnimationClip.KeyFrame frame)
    {
        var output = new Matrix4x4[frame.Position.Count];
        for (var boneIndex = 0; boneIndex < output.Length; boneIndex++)
        {
            output[boneIndex] =
                Matrix4x4.CreateScale(frame.Scale[boneIndex])
                * Matrix4x4.CreateFromQuaternion(frame.Rotation[boneIndex])
                * Matrix4x4.CreateTranslation(frame.Position[boneIndex]);
        }

        return output;
    }

    private static Matrix4x4[] BuildWorldMatrices(
        IReadOnlyList<Matrix4x4> localMatrices,
        GameSkeleton skeleton)
    {
        var output = new Matrix4x4[localMatrices.Count];
        for (var boneIndex = 0; boneIndex < output.Length; boneIndex++)
        {
            var parentIndex = skeleton.GetParentBoneIndex(boneIndex);
            output[boneIndex] = parentIndex < 0
                ? localMatrices[boneIndex]
                : localMatrices[boneIndex] * output[parentIndex];
        }

        return output;
    }

    private static void WriteLocalTransform(
        AnimationClip.KeyFrame frame,
        int boneIndex,
        Matrix4x4 localMatrix)
    {
        if (!Matrix4x4.Decompose(localMatrix, out var scale, out var rotation, out var translation))
        {
            Logger.Here().Warning($"Unable to decompose metadata-adjusted bone transform {boneIndex}.");
            return;
        }

        frame.Position[boneIndex] = translation;
        frame.Rotation[boneIndex] = Quaternion.Normalize(rotation);
        frame.Scale[boneIndex] = scale;
    }

    private sealed record ResolvedDockRule(
        int PropBoneIndex,
        int TargetBoneIndex,
        Matrix4x4 Offset);
}
