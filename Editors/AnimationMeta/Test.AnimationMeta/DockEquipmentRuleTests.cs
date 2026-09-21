using System.Numerics;
using Editors.AnimationMeta.SuperView.Visualisation.Rules;
using GameWorld.Core.Animation;
using GameWorld.Core.Animation.AnimationChange;
using GameWorld.Core.SceneNodes;
using Shared.GameFormats.Animation;
using Shared.GameFormats.RigidModel.Transforms;

namespace Test.AnimationMeta;

public class DockEquipmentRuleTests
{
    [Test]
    public void DockEquipmentUsesTheCurrentTargetPose()
    {
        var skeleton = CreateSkeleton(
            ("root", -1, Vector3.Zero),
            ("hand_left", 0, new Vector3(1, 0, 0)),
            ("be_prop_0", 0, Vector3.Zero));
        var provider = new TestSkeletonProvider(skeleton);

        var source = CreateClip(
            1.0f,
            [Vector3.Zero, new Vector3(5, 0, 0), Vector3.Zero]);
        var dockAnimation = CreateClip(
            1.0f,
            [Vector3.Zero, new Vector3(1, 0, 0), new Vector3(3, 0, 0)]);
        var rule = new DockEquipmentRule(
            skeleton.GetBoneIndexByName("hand_left"),
            equipmentSlotToDock: 1,
            dockAnimation,
            provider,
            startTime: 0,
            endTime: 0);

        var frame = AnimationSampler.Sample(
            0,
            skeleton,
            source,
            new List<IAnimationChangeRule> { rule });

        var propWorld = frame.GetSkeletonAnimatedWorld(
            skeleton,
            skeleton.GetBoneIndexByName("be_prop_0"));

        // The docking pose puts the prop two units past the hand. The current
        // source pose puts the hand at X=5, so the prop must be at X=7.
        Assert.That(propWorld.Translation.X, Is.EqualTo(7).Within(0.0001f));
    }

    private static GameSkeleton CreateSkeleton(params (string Name, int ParentId, Vector3 Translation)[] bones)
    {
        var file = new AnimationFile
        {
            Header = { SkeletonName = "test_skeleton" },
            Bones = bones
                .Select((bone, index) => new AnimationFile.BoneInfo
                {
                    Id = index,
                    Name = bone.Name,
                    ParentId = bone.ParentId
                })
                .ToArray()
        };

        var frame = new AnimationFile.Frame();
        foreach (var bone in bones)
        {
            frame.Transforms.Add(new RmvVector3(bone.Translation));
            frame.Quaternion.Add(new RmvVector4(0, 0, 0, 1));
        }

        var part = new AnimationFile.AnimationPart();
        part.DynamicFrames.Add(frame);
        file.AnimationParts.Add(part);

        return new GameSkeleton(file, null);
    }

    private static AnimationClip CreateClip(float duration, IReadOnlyList<Vector3> positions)
    {
        var clip = new AnimationClip();
        var frame = new AnimationClip.KeyFrame();
        foreach (var position in positions)
        {
            frame.Position.Add(position);
            frame.Rotation.Add(Quaternion.Identity);
            frame.Scale.Add(Vector3.One);
        }

        clip.DynamicFrames.Add(frame);
        clip.PlayTimeInSec = duration;
        return clip;
    }

    private sealed class TestSkeletonProvider : ISkeletonProvider
    {
        public TestSkeletonProvider(GameSkeleton skeleton)
        {
            Skeleton = skeleton;
        }

        public GameSkeleton Skeleton { get; }
    }
}
