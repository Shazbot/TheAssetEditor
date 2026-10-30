using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Shared.ByteParsing;
using Shared.GameFormats.RigidModel.Transforms;
using Half = System.Half;
namespace Shared.GameFormats.RigidModel.Vertex
{
    public static class VertexLoadHelper
    {
        static public RmvVector4 CreatVector4HalfFloat(byte[] data)
        {
            ByteParsers.Float16.TryDecodeValue(data, 0, out var xHalf, out _, out _);
            ByteParsers.Float16.TryDecodeValue(data, 2, out var yHalf, out _, out _);
            ByteParsers.Float16.TryDecodeValue(data, 4, out var zHalf, out _, out _);
            ByteParsers.Float16.TryDecodeValue(data, 6, out var wHalf, out _, out _);

            var x = (float)xHalf;
            var y = (float)yHalf;
            var z = (float)zHalf;
            var w = (float)wHalf;

            if (w != 0.0f)
            {
                x *= w;
                y *= w;
                z *= w;
                w = 0;
            }

            return new RmvVector4()
            {
                X = x,
                Y = y,
                Z = z,
                W = w
            };
        }

        static public Vector4 CreatVector4HalfFloat2(Half x, Half y, Half z, Half w)
        {
            var xValue = (float)x;
            var yValue = (float)y;
            var zValue = (float)z;
            var wValue = (float)w;

            if (wValue != 0.0f)
            {
                xValue *= wValue;
                yValue *= wValue;
                zValue *= wValue;
            }

            return new Vector4()
            {
                X = xValue,
                Y = yValue,
                Z = zValue,
                W = 1
            };
        }

        static public RmvVector4 CreatVector4Float(byte[] data)
        {
            ByteParsers.Single.TryDecodeValue(data, 0, out var x, out _, out _);
            ByteParsers.Single.TryDecodeValue(data, 4, out var y, out _, out _);
            ByteParsers.Single.TryDecodeValue(data, 8, out var z, out _, out _);
            ByteParsers.Single.TryDecodeValue(data, 12, out var w, out _, out _);

            if (w > 0.0f)
            {
                x *= w;
                y *= w;
                z *= w;
                w = 0;
            }

            return new RmvVector4()
            {
                X = x,
                Y = y,
                Z = z,
                W = w
            };
        }


        static public RmvVector4 CreatVector4Byte(byte[] data)
        {
            var v = new RmvVector4()
            {
                X = ByteToNormal(data[0]),
                Y = ByteToNormal(data[1]),
                Z = ByteToNormal(data[2]),
                W = ByteToNormal(data[3])
            };

            if (v.W > 0.0f)
            {
                v.X *= v.W;
                v.Y *= v.W;
                v.Z *= v.W;
                v.W = 0;
            }

            return v;
        }


        static public Vector3 CreatVector3_FromByte(ByteVector4 vector)
        {
            var w = ByteToNormal(vector.W);
            var v = new Vector3()
            {
                X = ByteToNormal(vector.X),
                Y = ByteToNormal(vector.Y),
                Z = ByteToNormal(vector.Z),
               
            };

            if (w > 0.0f)
            {
                v.X *= w;
                v.Y *= w;
                v.Z *= w;
            }

            return v;
        }

        static public Vector4 CreatVector4_FromByte(ByteVector4 vector)
        {
  
            var v = new Vector4()
            {
                X = ByteToNormal(vector.X),
                Y = ByteToNormal(vector.Y),
                Z = ByteToNormal(vector.Z),
                W = ByteToNormal(vector.W)

            };

            if (v.W > 0.0f)
            {
                v.X *= v.W;
                v.Y *= v.W;
                v.Z *= v.W;
            }

            return v;
        }





        static public RmvVector2 CreatVector2HalfFloat(byte[] data)
        {
            ByteParsers.Float16.TryDecodeValue(data, 0, out var xHalf, out _, out _);
            ByteParsers.Float16.TryDecodeValue(data, 2, out var yHalf, out _, out _);
            return new RmvVector2()
            {
                X = (float)xHalf,
                Y = (float)yHalf
            };
        }






        // ---- Span based decoders: identical arithmetic to the byte[] versions above, without allocations. ----

        /// <summary>
        /// Returns the bytes of one vertex, throwing the same exception as ByteHelper.ByteArrayToStructure when it does not fit.
        /// </summary>
        public static ReadOnlySpan<byte> GetVertexBytes<TData>(byte[] buffer, int offset) where TData : struct
        {
            var objectSize = ByteHelper.GetSize<TData>();
            if (offset + objectSize > buffer.Length)
                throw new Exception($"Object {typeof(TData)} does not fit into the remaining buffer [offset{offset} + Size{objectSize} => byteBuffer{buffer.Length}]");
            return buffer.AsSpan(offset, objectSize);
        }

        static public RmvVector4 CreatVector4HalfFloat(ReadOnlySpan<byte> data)
        {
            var x = (float)BinaryPrimitives.ReadHalfLittleEndian(data);
            var y = (float)BinaryPrimitives.ReadHalfLittleEndian(data.Slice(2));
            var z = (float)BinaryPrimitives.ReadHalfLittleEndian(data.Slice(4));
            var w = (float)BinaryPrimitives.ReadHalfLittleEndian(data.Slice(6));

            if (w != 0.0f)
            {
                x *= w;
                y *= w;
                z *= w;
                w = 0;
            }

            return new RmvVector4() { X = x, Y = y, Z = z, W = w };
        }

        static public RmvVector4 CreatVector4Float(ReadOnlySpan<byte> data)
        {
            var x = BinaryPrimitives.ReadSingleLittleEndian(data);
            var y = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(4));
            var z = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(8));
            var w = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(12));

            if (w > 0.0f)
            {
                x *= w;
                y *= w;
                z *= w;
                w = 0;
            }

            return new RmvVector4() { X = x, Y = y, Z = z, W = w };
        }

        static public RmvVector4 CreatVector4Byte(ReadOnlySpan<byte> data)
        {
            var v = new RmvVector4()
            {
                X = ByteToNormal(data[0]),
                Y = ByteToNormal(data[1]),
                Z = ByteToNormal(data[2]),
                W = ByteToNormal(data[3])
            };

            if (v.W > 0.0f)
            {
                v.X *= v.W;
                v.Y *= v.W;
                v.Z *= v.W;
                v.W = 0;
            }

            return v;
        }

        static public RmvVector2 CreatVector2HalfFloat(ReadOnlySpan<byte> data)
        {
            return new RmvVector2()
            {
                X = (float)BinaryPrimitives.ReadHalfLittleEndian(data),
                Y = (float)BinaryPrimitives.ReadHalfLittleEndian(data.Slice(2))
            };
        }


        static public float ByteToNormal(byte b)
        {
            return b / 255.0f * 2.0f - 1.0f;
        }

        static public byte NormalToByte(float f)
        {
            // var truncatedFloat = ((f * 255.0f) / 2.0f) + 1.0f;
            var truncatedFloat = (f + 1.0f) / 2.0f * 255.0f;
            return (byte)Math.Round(truncatedFloat);
        }
        public struct MyHalfVector4 { public Half X; public Half Y; public Half Z; public Half W; }

        /// <summary>
        /// Converts a single to the truncating half-float representation used by the
        /// previous vertex serializers, without bringing the renderer math package
        /// back into the headless format layer.
        /// </summary>
        public static Half ConvertFloatToHalf(float value)
        {
            var bits = BitConverter.SingleToUInt32Bits(value);
            var exponent = (int)((bits >> 23) & 0xff) - 127;
            var sign = (bits & 0x80000000) == 0 ? 0u : 0x8000u;
            var mantissa = bits & 0x007fffff;

            ushort baseValue;
            int shift;
            if (exponent < -24)
            {
                baseValue = (ushort)sign;
                shift = 24;
            }
            else if (exponent < -14)
            {
                baseValue = (ushort)(sign | (0x0400 >> (-exponent - 14)));
                shift = -exponent - 1;
            }
            else if (exponent <= 15)
            {
                baseValue = (ushort)(sign | ((exponent + 15) << 10));
                shift = 13;
            }
            else if (exponent < 128)
            {
                baseValue = (ushort)(sign | 0x7c00);
                shift = 24;
            }
            else
            {
                baseValue = (ushort)(sign | 0x7c00);
                shift = 13;
            }

            return BitConverter.UInt16BitsToHalf((ushort)(baseValue + (mantissa >> shift)));
        }

        private const int HalfMantissaCount = 1024;

        // testW = 1.0 + i / 1024 (exactly representable as a half, so the half round trip of W is lossless).
        private static readonly float[] TestWTable = BuildTestWTable();

        private static float[] BuildTestWTable()
        {
            var table = new float[HalfMantissaCount];
            for (var i = 0; i < table.Length; i++)
                table[i] = (float)(1.0 + ((float)i / 1024.0f));
            return table;
        }

        public static (Half X, Half Y, Half Z, Half W) ConvertertVertexToHalfExtraPrecision(Vector4 vertexOriginal)
        {
            // The input never changes inside the search, so the checks are loop invariant. The first
            // trial uses testW == 1, which makes the normalized vertex equal to the input; later trials
            // divide by testW > 1 and cannot introduce an infinity that is not already there.
            if (!IsWithingFloat16Range(vertexOriginal))
                throw new Exception("Input vertex cannot be converted to float16");

            if (!IsResultValid(new Vector3(vertexOriginal.X, vertexOriginal.Y, vertexOriginal.Z)))
                throw new Exception("Result is out range for conversin to float16");

            var bestValueForW = FindBestW(vertexOriginal.X, vertexOriginal.Y, vertexOriginal.Z);

            // Normalize the original values by dividing by the BEST w
            var normalizedFinal = new Vector3()
            {
                X = vertexOriginal.X / bestValueForW,
                Y = vertexOriginal.Y / bestValueForW,
                Z = vertexOriginal.Z / bestValueForW
            };

            // Convert normalized values and W to half-float
            var outHalfVertex = new MyHalfVector4();
            outHalfVertex.X = ConvertFloatToHalf(normalizedFinal.X);
            outHalfVertex.Y = ConvertFloatToHalf(normalizedFinal.Y);
            outHalfVertex.Z = ConvertFloatToHalf(normalizedFinal.Z);
            outHalfVertex.W = ConvertFloatToHalf(bestValueForW);

            return (outHalfVertex.X, outHalfVertex.Y, outHalfVertex.Z, outHalfVertex.W);
        }

        /// <summary>
        /// Returns the testW with the smallest round trip error (first strict minimum, 0 when no trial
        /// has a finite error), exactly like the original brute force loop.
        /// </summary>
        public static float FindBestW(float x, float y, float z)
        {
            if (Vector256.IsHardwareAccelerated)
                return FindBestWVectorized(x, y, z);

            return FindBestWScalar(x, y, z);
        }

        public static float FindBestWScalar(float x, float y, float z)
        {
            float bestValueForW = 0;
            float currentSmallestError = float.MaxValue;

            for (var i = 0; i < HalfMantissaCount; i++)
            {
                var testW = TestWTable[i];
                var rx = TruncateToHalfPrecisionScalar(x / testW) * testW;
                var ry = TruncateToHalfPrecisionScalar(y / testW) * testW;
                var rz = TruncateToHalfPrecisionScalar(z / testW) * testW;

                float error = Math.Abs(x - rx) + Math.Abs(y - ry) + Math.Abs(z - rz);
                if (error < currentSmallestError)
                {
                    currentSmallestError = error;
                    bestValueForW = testW;
                }

                if (error == 0f)
                    break;
            }

            return bestValueForW;
        }

        private static float TruncateToHalfPrecisionScalar(float value) => (float)ConvertFloatToHalf(value);

        private static float FindBestWVectorized(float x, float y, float z)
        {
            const int lanes = 8;
            var vx = Vector256.Create(x);
            var vy = Vector256.Create(y);
            var vz = Vector256.Create(z);

            var bestError = Vector256.Create(float.MaxValue);
            var bestIndex = Vector256.Create(-1);
            var laneIndex = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7);
            var step = Vector256.Create(lanes);

            ref var table = ref MemoryMarshal.GetArrayDataReference(TestWTable);
            for (var i = 0; i < HalfMantissaCount; i += lanes)
            {
                var w = Vector256.LoadUnsafe(ref table, (nuint)i);
                var rx = TruncateToHalfPrecision(vx / w) * w;
                var ry = TruncateToHalfPrecision(vy / w) * w;
                var rz = TruncateToHalfPrecision(vz / w) * w;

                var error = Vector256.Abs(vx - rx) + Vector256.Abs(vy - ry) + Vector256.Abs(vz - rz);
                var better = Vector256.LessThan(error, bestError);
                bestError = Vector256.ConditionalSelect(better, error, bestError);
                bestIndex = Vector256.ConditionalSelect(better.AsInt32(), laneIndex, bestIndex);
                if (Vector256.EqualsAny(error, Vector256<float>.Zero))
                    break;
                laneIndex += step;
            }

            // Every lane kept the first strict minimum of its own indices; the global first strict
            // minimum is the lowest error, with ties resolved to the lowest index.
            var best = float.MaxValue;
            var bestI = -1;
            for (var lane = 0; lane < lanes; lane++)
            {
                var laneError = bestError.GetElement(lane);
                var laneBestIndex = bestIndex.GetElement(lane);
                if (laneBestIndex < 0)
                    continue;

                if (laneError < best || (laneError == best && laneBestIndex < bestI))
                {
                    best = laneError;
                    bestI = laneBestIndex;
                }
            }

            return bestI < 0 ? 0f : TestWTable[bestI];
        }

        /// <summary>
        /// float -> ConvertFloatToHalf -> float for eight lanes. Truncates the mantissa (round toward zero),
        /// produces half denormals/zero below 2^-14 and infinity from 2^16.
        /// </summary>
        private static Vector256<float> TruncateToHalfPrecision(Vector256<float> value)
        {
            var signMask = Vector256.Create(0x80000000u).AsSingle();
            var sign = value & signMask;
            var abs = value & ~signMask;

            // Normal half range: keep sign, exponent and the top 10 mantissa bits.
            var normal = (abs.AsUInt32() & Vector256.Create(0xffffe000u)).AsSingle();
            // Half denormal range: truncate to a multiple of 2^-24 (exact power-of-two scaling).
            var denormal = Vector256.Floor(abs * Vector256.Create(16777216.0f)) * Vector256.Create(1.0f / 16777216.0f);

            var result = Vector256.ConditionalSelect(
                Vector256.LessThan(abs, Vector256.Create(6.103515625e-05f)),
                denormal,
                normal);
            result = Vector256.ConditionalSelect(
                Vector256.GreaterThanOrEqual(abs, Vector256.Create(65536.0f)),
                Vector256.Create(float.PositiveInfinity),
                result);

            return result | sign;
        }

        private static bool IsResultValid(Vector3 input)
        {
            if (
                (Math.Abs(input.X) == float.NaN || Math.Abs(input.X) == float.PositiveInfinity) ||
                (Math.Abs(input.Y) == float.NaN || Math.Abs(input.Y) == float.PositiveInfinity) ||
                (Math.Abs(input.Z) == float.NaN || Math.Abs(input.Z) == float.PositiveInfinity))
            {
                return false;
            }

            return true;
        }

        private static bool IsWithingFloat16Range(Vector4 vertexOriginal)
        {
            if (vertexOriginal.X >= (float)Half.MaxValue || vertexOriginal.Y >= (float)Half.MaxValue || vertexOriginal.Z >= (float)Half.MaxValue)
            {
                return false;
            }

            return true;
        }

        static public byte[] CreatePositionVector4(Vector4 vector)
        {
            var output = new byte[8];
            ushort[] halfs =
            {
                BitConverter.HalfToUInt16Bits(ConvertFloatToHalf(vector.X)),
                BitConverter.HalfToUInt16Bits(ConvertFloatToHalf(vector.Y)),
                BitConverter.HalfToUInt16Bits(ConvertFloatToHalf(vector.Z)),
                BitConverter.HalfToUInt16Bits(ConvertFloatToHalf(vector.W))
            };
            for (var i = 0; i < 4; i++)
            {
                var bytes = BitConverter.GetBytes(halfs[i]);
                output[i * 2] = bytes[0];
                output[i * 2 + 1] = bytes[1];
            }

            return output;
        }

        static public byte[] CreatePositionVector4ExtraPrecision(Vector4 vector)
        {
            var output = new byte[8];

            var v = ConvertertVertexToHalfExtraPrecision(vector);            
            ushort[] halfs =
            {
                BitConverter.HalfToUInt16Bits(v.X),
                BitConverter.HalfToUInt16Bits(v.Y),
                BitConverter.HalfToUInt16Bits(v.Z),
                BitConverter.HalfToUInt16Bits(v.W)
            };

            for (var i = 0; i < 4; i++)
            {
                var bytes = BitConverter.GetBytes(halfs[i]);
                output[i * 2] = bytes[0];
                output[i * 2 + 1] = bytes[1];
            }

            return output;
        }

        static public HalfVector4 CreatePositionVector4ExtraPrecision_v2(Vector4 vector)
        {
            var v = ConvertertVertexToHalfExtraPrecision(vector);
            return new HalfVector4()
            {
                X = v.X,
                Y = v.Y,
                Z = v.Z,
                W = v.W,
            };
        }

        static public byte[] CreatePositionVector2(Vector2 vector)
        {
            var output = new byte[4];
            ushort[] halfs =
            {
                BitConverter.HalfToUInt16Bits(ConvertFloatToHalf(vector.X)),
                BitConverter.HalfToUInt16Bits(ConvertFloatToHalf(vector.Y))
            };
            for (var i = 0; i < 2; i++)
            {
                var bytes = BitConverter.GetBytes(halfs[i]);
                output[i * 2] = bytes[0];
                output[i * 2 + 1] = bytes[1];
            }

            return output;
        }

        static public byte[] CreateNormalVector3(Vector3 vector)
        {
            var output = new byte[4];
            output[0] = NormalToByte(vector.X);
            output[1] = NormalToByte(vector.Y);
            output[2] = NormalToByte(vector.Z);
            output[3] = NormalToByte(-1);
            return output;
        }

        static public ByteVector4 CreateNormalVector3_v2(Vector3 vector)
        {
            return new ByteVector4()
            {
                X = NormalToByte(vector.X),
                Y = NormalToByte(vector.Y),
                Z = NormalToByte(vector.Z),
                W = NormalToByte(-1),
            };
        }


        static public byte[] Create4BytesFromVector4(Vector4 vector)
        {
            var output = new byte[4];
            output[0] = NormalToByte(vector.X);
            output[1] = NormalToByte(vector.Y);
            output[2] = NormalToByte(vector.Z);
            output[3] = NormalToByte(vector.W);
            return output;            
        }

        static public ByteVector4 Create4BytesFromVector4_v2(Vector4 vector)
        {
            return new ByteVector4()
            {
                X = NormalToByte(vector.X),
                Y = NormalToByte(vector.Y),
                Z = NormalToByte(vector.Z),
                W = NormalToByte(vector.W),
            };
        }
    }
}
