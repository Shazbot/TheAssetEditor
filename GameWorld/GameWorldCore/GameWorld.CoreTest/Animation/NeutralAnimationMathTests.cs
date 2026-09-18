using System.Numerics;
using GameWorld.Core.Animation;
using NUnit.Framework;
using Shared.ByteParsing;
using Shared.GameFormats.Animation;
using Shared.GameFormats.RigidModel.Transforms;

namespace GameWorld.CoreTest.Animation;

[TestFixture]
public class NeutralAnimationMathTests
{
    [Test]
    public void SkeletonWorldTransformsPreserveScaleRotationTranslationOrder()
    {
        var rootRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var skeleton = new GameSkeleton(CreateSkeletonFile(
            ("root", -1, new Vector3(10, 0, 0), rootRotation),
            ("child", 0, new Vector3(2, 0, 0), Quaternion.Identity),
            ("grandchild", 1, new Vector3(0, 3, 0), Quaternion.Identity)), null);

        AssertVectorEqual(new Vector3(10, 0, 0), skeleton.GetWorldTransform(0).Translation);
        AssertVectorEqual(new Vector3(10, 2, 0), skeleton.GetWorldTransform(1).Translation);
        AssertVectorEqual(new Vector3(7, 2, 0), skeleton.GetWorldTransform(2).Translation);

        var inverseBind = skeleton.CreateInvMatrixFile();
        Assert.That(Matrix4x4.Invert(skeleton.GetWorldTransform(2), out var expectedInverse), Is.True);
        AssertMatrixEqual(Matrix4x4.Transpose(expectedInverse), inverseBind.MatrixList[2]);
    }

    [Test]
    public void CreateFromAnimationFileNormalizesQuaternionValues()
    {
        var file = CreateSkeletonFile(("root", -1, Vector3.Zero, new Quaternion(0, 0, 2, 0)));
        file.AnimationParts[0].TranslationMappings.Add(new AnimationFile.AnimationBoneMapping(0));
        file.AnimationParts[0].RotationMappings.Add(new AnimationFile.AnimationBoneMapping(0));

        var skeleton = GameSkeleton.CreateFromAnimationFile(file, null);

        Assert.That(skeleton.Rotation[0].Length(), Is.EqualTo(1).Within(0.00001f));
        Assert.That(skeleton.Rotation[0].Z, Is.EqualTo(1).Within(0.00001f));
    }

    [Test]
    public void AnimInvMatrixRoundTripPreservesEveryMatrixElement()
    {
        var expected = new Matrix4x4(
            1.1f, 1.2f, 1.3f, 1.4f,
            2.1f, 2.2f, 2.3f, 2.4f,
            3.1f, 3.2f, 3.3f, 3.4f,
            0f, 0f, 0f, 1f);
        var file = new AnimInvMatrixFile { Version = 7, MatrixList = [expected] };

        var parsed = AnimInvMatrixFile.Create(new ByteChunk(file.GetBytes()));

        Assert.That(parsed.Version, Is.EqualTo(7));
        Assert.That(parsed.MatrixList, Has.Length.EqualTo(1));
        AssertMatrixEqual(expected, parsed.MatrixList[0]);
    }

    private static AnimationFile CreateSkeletonFile(params (string Name, int ParentId, Vector3 Translation, Quaternion Rotation)[] bones)
    {
        var file = new AnimationFile
        {
            Header = new AnimationFile.AnimationHeader { SkeletonName = "neutral_math_test" },
            Bones = bones.Select((bone, index) => new AnimationFile.BoneInfo
            {
                Id = index,
                Name = bone.Name,
                ParentId = bone.ParentId
            }).ToArray()
        };

        var frame = new AnimationFile.Frame();
        foreach (var bone in bones)
        {
            frame.Transforms.Add(new RmvVector3(bone.Translation));
            frame.Quaternion.Add(new RmvVector4(bone.Rotation.X, bone.Rotation.Y, bone.Rotation.Z, bone.Rotation.W));
        }

        var part = new AnimationFile.AnimationPart();
        part.DynamicFrames.Add(frame);
        file.AnimationParts.Add(part);
        return file;
    }

    private static void AssertMatrixEqual(Matrix4x4 expected, Matrix4x4 actual)
    {
        Assert.That(actual.M11, Is.EqualTo(expected.M11).Within(0.00001f));
        Assert.That(actual.M12, Is.EqualTo(expected.M12).Within(0.00001f));
        Assert.That(actual.M13, Is.EqualTo(expected.M13).Within(0.00001f));
        Assert.That(actual.M14, Is.EqualTo(expected.M14).Within(0.00001f));
        Assert.That(actual.M21, Is.EqualTo(expected.M21).Within(0.00001f));
        Assert.That(actual.M22, Is.EqualTo(expected.M22).Within(0.00001f));
        Assert.That(actual.M23, Is.EqualTo(expected.M23).Within(0.00001f));
        Assert.That(actual.M24, Is.EqualTo(expected.M24).Within(0.00001f));
        Assert.That(actual.M31, Is.EqualTo(expected.M31).Within(0.00001f));
        Assert.That(actual.M32, Is.EqualTo(expected.M32).Within(0.00001f));
        Assert.That(actual.M33, Is.EqualTo(expected.M33).Within(0.00001f));
        Assert.That(actual.M34, Is.EqualTo(expected.M34).Within(0.00001f));
        Assert.That(actual.M41, Is.EqualTo(expected.M41).Within(0.00001f));
        Assert.That(actual.M42, Is.EqualTo(expected.M42).Within(0.00001f));
        Assert.That(actual.M43, Is.EqualTo(expected.M43).Within(0.00001f));
        Assert.That(actual.M44, Is.EqualTo(expected.M44).Within(0.00001f));
    }

    private static void AssertVectorEqual(Vector3 expected, Vector3 actual)
    {
        Assert.That(actual.X, Is.EqualTo(expected.X).Within(0.00001f));
        Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(0.00001f));
        Assert.That(actual.Z, Is.EqualTo(expected.Z).Within(0.00001f));
    }
}
