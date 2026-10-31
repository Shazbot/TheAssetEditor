using System.Buffers.Binary;
using System.Numerics;
using Shared.GameFormats.RigidModel.Vertex;

namespace Editors.PackOptimizer.Core;

internal readonly record struct CommonVertexNativeKeyBuffer(byte[] Bytes, int Stride);

/// <summary>
/// Packs every CommonVertexComparer field into a deterministic byte key for meshoptimizer.
/// Padding is zeroed and float encodings follow Single.Equals semantics for signed zero and NaN.
/// </summary>
internal static class CommonVertexNativeKeyEncoder
{
    internal const int MaxVertexSize = 256;

    private const int FloatCount = 21;
    private const int WeightCountOffset = FloatCount * sizeof(float);
    private const int BoneIndexLengthOffset = WeightCountOffset + sizeof(int);
    private const int BoneIndexDataOffset = BoneIndexLengthOffset + sizeof(int);

    internal static CommonVertexNativeKeyBuffer Encode(IReadOnlyList<CommonVertex> vertices)
    {
        ArgumentNullException.ThrowIfNull(vertices);

        var maxBoneIndexCount = 0;
        var maxBoneWeightCount = 0;
        for (var i = 0; i < vertices.Count; i++)
        {
            var vertex = vertices[i] ?? throw new InvalidOperationException($"Vertex {i} is null.");
            maxBoneIndexCount = Math.Max(maxBoneIndexCount, vertex.BoneIndex?.Length ?? 0);
            maxBoneWeightCount = Math.Max(maxBoneWeightCount, vertex.BoneWeight?.Length ?? 0);
        }

        var boneWeightLengthOffset = checked(BoneIndexDataOffset + maxBoneIndexCount);
        var boneWeightDataOffset = checked(boneWeightLengthOffset + sizeof(int));
        var rawStride = checked(boneWeightDataOffset + maxBoneWeightCount * sizeof(float));
        var stride = checked((rawStride + 3) & ~3);
        if (stride <= 0 || stride > MaxVertexSize)
        {
            throw new InvalidOperationException(
                $"Full vertex key stride {stride} bytes exceeds meshoptimizer's {MaxVertexSize}-byte limit " +
                $"(bone-index count {maxBoneIndexCount}, bone-weight count {maxBoneWeightCount}).");
        }

        var bytes = new byte[checked(vertices.Count * stride)];
        for (var i = 0; i < vertices.Count; i++)
        {
            var vertex = vertices[i];
            var offset = checked(i * stride);
            var floatOffset = offset;

            WriteVector4(bytes, floatOffset, vertex.Position); floatOffset += 16;
            WriteVector3(bytes, floatOffset, vertex.Normal); floatOffset += 12;
            WriteVector3(bytes, floatOffset, vertex.BiNormal); floatOffset += 12;
            WriteVector3(bytes, floatOffset, vertex.Tangent); floatOffset += 12;
            WriteVector2(bytes, floatOffset, vertex.Uv); floatOffset += 8;
            WriteVector2(bytes, floatOffset, vertex.Uv1); floatOffset += 8;
            WriteVector4(bytes, floatOffset, vertex.Colour);

            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + WeightCountOffset), vertex.WeightCount);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + BoneIndexLengthOffset), vertex.BoneIndex?.Length ?? -1);
            if (vertex.BoneIndex != null)
                vertex.BoneIndex.CopyTo(bytes, offset + BoneIndexDataOffset);

            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + boneWeightLengthOffset), vertex.BoneWeight?.Length ?? -1);
            if (vertex.BoneWeight != null)
            {
                for (var weight = 0; weight < vertex.BoneWeight.Length; weight++)
                    WriteFloat(bytes, offset + boneWeightDataOffset + weight * sizeof(float), vertex.BoneWeight[weight]);
            }
        }

        return new CommonVertexNativeKeyBuffer(bytes, stride);
    }

    private static void WriteVector2(byte[] destination, int offset, Vector2 value)
    {
        WriteFloat(destination, offset, value.X);
        WriteFloat(destination, offset + 4, value.Y);
    }

    private static void WriteVector3(byte[] destination, int offset, Vector3 value)
    {
        WriteFloat(destination, offset, value.X);
        WriteFloat(destination, offset + 4, value.Y);
        WriteFloat(destination, offset + 8, value.Z);
    }

    private static void WriteVector4(byte[] destination, int offset, Vector4 value)
    {
        WriteFloat(destination, offset, value.X);
        WriteFloat(destination, offset + 4, value.Y);
        WriteFloat(destination, offset + 8, value.Z);
        WriteFloat(destination, offset + 12, value.W);
    }

    private static void WriteFloat(byte[] destination, int offset, float value)
    {
        var bits = value == 0f
            ? 0
            : float.IsNaN(value)
                ? unchecked((int)0x7fc00000)
                : BitConverter.SingleToInt32Bits(value);
        BinaryPrimitives.WriteInt32LittleEndian(destination.AsSpan(offset), bits);
    }
}
