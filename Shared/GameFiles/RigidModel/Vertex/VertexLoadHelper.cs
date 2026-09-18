using System.Numerics;
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

        public static (Half X, Half Y, Half Z, Half W) ConvertertVertexToHalfExtraPrecision(Vector4 vertexOriginal)
        {
            const uint halfMantissaMax = 1024;

            float bestValueForW = 0;
            float currentSmallestError = float.MaxValue;

            // Brute force, checking all 1024 half-float mantissa values for "w"
            for (ushort iMantissaCounter = 0; iMantissaCounter < halfMantissaMax; iMantissaCounter++)
            {
                if (!IsWithingFloat16Range(vertexOriginal))
                    throw new Exception("Input vertex cannot be converted to float16");

                // fill the mantissa bits with value from 1.0 to 2.0
                float testW = (float)(1.0 + ((float)iMantissaCounter / 1024.0f));
                
                // Normalize the original values by dividing by w
                var normalizedVertex = new Vector3() {
                    X = vertexOriginal.X / testW,
                    Y = vertexOriginal.Y / testW,
                    Z = vertexOriginal.Z / testW
                };

                if (!IsResultValid(normalizedVertex))
                    throw new Exception("Result is out range for conversin to float16");

                // Convert normalized values to half-float            
                var halfVertexNormalized = new MyHalfVector4()
                {
                    X = ConvertFloatToHalf(normalizedVertex.X),
                    Y = ConvertFloatToHalf(normalizedVertex.Y),
                    Z = ConvertFloatToHalf(normalizedVertex.Z),
                    W = ConvertFloatToHalf(testW)
                };   

                // Recover the original values by multiplying by w
                var recoveredVertex = new Vector3() {
                    X = (float)halfVertexNormalized.X * (float)halfVertexNormalized.W, 
                    Y = (float)halfVertexNormalized.Y * (float)halfVertexNormalized.W, 
                    Z = (float)halfVertexNormalized.Z * (float)halfVertexNormalized.W
                };
                                
                float error =
                    Math.Abs(vertexOriginal.X - recoveredVertex.X) +
                    Math.Abs(vertexOriginal.Y - recoveredVertex.Y) +
                    Math.Abs(vertexOriginal.Z - recoveredVertex.Z);

                // Check if this w gives a better (smaller) error
                if (error < currentSmallestError)
                {
                    currentSmallestError = error;
                    bestValueForW = testW;
                }
            }

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
