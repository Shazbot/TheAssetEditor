using System.Numerics;

namespace Editors.ImportExport.Common
{
    public class VecConv
    {
        public static Vector4 NormalizeTangentVector4(Vector4 tangent)
        {
            // normalize only the xyz components of the tangent, the w component is the handedness (1 or -1) in sharpGLTF
            var tempTangent = Vector3.Normalize(new Vector3(tangent.X, tangent.Y, tangent.Z));
            return new Vector4(tempTangent.X, tempTangent.Y, tempTangent.Z, tangent.W);
        }

    }

}
