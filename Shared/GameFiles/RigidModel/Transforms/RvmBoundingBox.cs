using System.Runtime.InteropServices;
using System.Numerics;

namespace Shared.GameFormats.RigidModel.Transforms
{
    [StructLayout(LayoutKind.Sequential, Size = 24)]
    [Serializable]
    public struct RvmBoundingBox
    {
        public float MinimumX;
        public float MinimumY;
        public float MinimumZ;
        public float MaximumX;
        public float MaximumY;
        public float MaximumZ;

        public void UpdateBoundingBox(Vector3 min, Vector3 max)
        {
            MinimumX = min.X;
            MinimumY = min.Y;
            MinimumZ = min.Z;

            MaximumX = max.X;
            MaximumY = max.Y;
            MaximumZ = max.Z;
        }

        public float Width { get => Math.Abs(MinimumX - MaximumX); }
        public float Height { get => Math.Abs(MinimumY - MaximumY); }
        public float Depth { get => Math.Abs(MinimumZ - MaximumZ); }
    }
}
