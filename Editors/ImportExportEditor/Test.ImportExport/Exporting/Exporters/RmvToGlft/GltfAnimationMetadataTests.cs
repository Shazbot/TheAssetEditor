using System.Numerics;
using System.Text;
using Editors.ImportExport.Exporting.Exporters.RmvToGltf;
using Editors.ImportExport.Exporting.Exporters.RmvToGltf.Helpers;
using GameWorld.Core.Animation;
using Shared.GameFormats.Animation;
using Shared.GameFormats.RigidModel.Transforms;

namespace Test.ImportExport.Exporting.Exporters.RmvToGlft;

public class GltfAnimationMetadataTests
{
    [Test]
    public void TransformRuleComposesBeforeSampledLocalTransform()
    {
        var skeleton = CreateSkeleton(
            ("root", -1, Vector3.Zero),
            ("child", 0, Vector3.Zero));
        var source = CreateClip(
            1.0f,
            [Vector3.Zero, new Vector3(1, 0, 0)]);
        var context = new GltfAnimationMetadataContext(
            "test.bin",
            [
                new GltfTransformMetadataRule(
                    1,
                    0,
                    new Vector3(2, 0, 0),
                    new Vector4(0, 0, 0, 1),
                    0,
                    1)
            ],
            [],
            []);

        var processed = GltfAnimationMetadataProcessor.Apply(source, skeleton, context);

        Assert.That(processed[0].Position[1].X, Is.EqualTo(3).Within(0.0001f));
    }

    [Test]
    public void DockEquipmentUsesDockAnimationOffsetRelativeToTargetBone()
    {
        var skeleton = CreateSkeleton(
            ("root", -1, Vector3.Zero),
            ("hand_right", 0, new Vector3(1, 0, 0)),
            ("be_prop_0", 0, Vector3.Zero));

        var source = CreateClip(
            1.0f,
            [Vector3.Zero, new Vector3(5, 0, 0), Vector3.Zero]);
        var dockAnimation = CreateClip(
            1.0f,
            [Vector3.Zero, new Vector3(1, 0, 0), new Vector3(3, 0, 0)]);

        var context = new GltfAnimationMetadataContext(
            "test.bin",
            [],
            [
                new GltfDockEquipmentMetadataRule(
                    1,
                    "DOCK_EQUIPMENT_RIGHT_HAND",
                    ["hand_right"],
                    0,
                    0,
                    dockAnimation)
            ],
            []);

        var processed = GltfAnimationMetadataProcessor.Apply(source, skeleton, context);

        // Dock pose authored prop at X=3 relative to hand at X=1 => +2 offset.
        // Current animation hand is X=5, so the prop must land at X=7.
        Assert.That(processed[0].Position[2].X, Is.EqualTo(7).Within(0.0001f));
    }

    [Test]
    public void DockEquipmentMatchesSuperViewStartTimeAndIgnoredEndTimeBehavior()
    {
        var skeleton = CreateSkeleton(
            ("root", -1, Vector3.Zero),
            ("hand_right", 0, new Vector3(1, 0, 0)),
            ("be_prop_0", 0, Vector3.Zero));
        var source = CreateClip(
            1.0f,
            [Vector3.Zero, new Vector3(5, 0, 0), Vector3.Zero]);
        var dockAnimation = CreateClip(
            1.0f,
            [Vector3.Zero, new Vector3(1, 0, 0), new Vector3(3, 0, 0)]);

        var context = new GltfAnimationMetadataContext(
            "test.bin",
            [],
            [
                new GltfDockEquipmentMetadataRule(
                    1,
                    "DOCK_EQUIPMENT_RIGHT_HAND",
                    ["hand_right"],
                    0.25f,
                    0.1f,
                    dockAnimation)
            ],
            []);

        var processed = GltfAnimationMetadataProcessor.Apply(source, skeleton, context);

        Assert.That(processed[0].Position[2].X, Is.EqualTo(7).Within(0.0001f));
    }

    [Test]
    public void TrimSafeDecoderReadsDockEquipmentV10()
    {
        var payload = new List<byte>();
        payload.AddRange(BitConverter.GetBytes(10));
        payload.AddRange(BitConverter.GetBytes(0.25f));
        payload.AddRange(BitConverter.GetBytes(0.75f));
        payload.AddRange(CaString(""));
        payload.AddRange(BitConverter.GetBytes(0));
        payload.AddRange(BitConverter.GetBytes(1));
        payload.AddRange(BitConverter.GetBytes(0.1f));
        payload.AddRange(BitConverter.GetBytes(0.2f));

        var file = new List<byte>();
        file.AddRange(BitConverter.GetBytes(2));
        file.AddRange(BitConverter.GetBytes((uint)1));
        file.AddRange(CaString("DOCK_EQPT_RHAND"));
        file.AddRange(payload);

        var decoded = GltfAnimationMetadataDecoder.Decode(file.ToArray());

        var dock = decoded.DockRules.Single();
        Assert.That(dock.PropBoneId, Is.EqualTo(1));
        Assert.That(dock.AnimationSlotName, Is.EqualTo("DOCK_EQUIPMENT_RIGHT_HAND"));
        Assert.That(dock.SkeletonNameAlternatives, Is.EqualTo(new[] { "hand_right" }));
        Assert.That(dock.StartTime, Is.EqualTo(0.25f));
        Assert.That(dock.EndTime, Is.EqualTo(0.75f));
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

    private static byte[] CaString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return BitConverter.GetBytes((short)bytes.Length).Concat(bytes).ToArray();
    }
}
