using NumericsQuaternion = System.Numerics.Quaternion;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;
using NumericsVector4 = System.Numerics.Vector4;
using NumericsMatrix = System.Numerics.Matrix4x4;
using XnaQuaternion = Microsoft.Xna.Framework.Quaternion;
using XnaMatrix = Microsoft.Xna.Framework.Matrix;
using XnaVector2 = Microsoft.Xna.Framework.Vector2;
using XnaVector3 = Microsoft.Xna.Framework.Vector3;
using XnaVector4 = Microsoft.Xna.Framework.Vector4;

namespace GameWorld.Core.Rendering
{
    /// <summary>
    /// Converts the framework-neutral math types used by asset data into the
    /// MonoGame types required by the renderer, and back at the renderer boundary.
    /// </summary>
    public static class NumericsXnaConverter
    {
        public static XnaVector2 ToXna(NumericsVector2 value) => new(value.X, value.Y);

        public static XnaVector3 ToXna(NumericsVector3 value) => new(value.X, value.Y, value.Z);

        public static XnaVector4 ToXna(NumericsVector4 value) => new(value.X, value.Y, value.Z, value.W);

        public static XnaQuaternion ToXna(NumericsQuaternion value) => new(value.X, value.Y, value.Z, value.W);

        public static XnaMatrix ToXna(NumericsMatrix value) => new(
            value.M11, value.M12, value.M13, value.M14,
            value.M21, value.M22, value.M23, value.M24,
            value.M31, value.M32, value.M33, value.M34,
            value.M41, value.M42, value.M43, value.M44);

        public static NumericsVector2 ToNumerics(XnaVector2 value) => new(value.X, value.Y);

        public static NumericsVector3 ToNumerics(XnaVector3 value) => new(value.X, value.Y, value.Z);

        public static NumericsVector4 ToNumerics(XnaVector4 value) => new(value.X, value.Y, value.Z, value.W);

        public static NumericsQuaternion ToNumerics(XnaQuaternion value) => new(value.X, value.Y, value.Z, value.W);

        public static NumericsMatrix ToNumerics(XnaMatrix value) => new(
            value.M11, value.M12, value.M13, value.M14,
            value.M21, value.M22, value.M23, value.M24,
            value.M31, value.M32, value.M33, value.M34,
            value.M41, value.M42, value.M43, value.M44);
    }
}
