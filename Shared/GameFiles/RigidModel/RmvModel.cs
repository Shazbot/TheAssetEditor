using System.Numerics;
using Shared.GameFormats.RigidModel.MaterialHeaders;

namespace Shared.GameFormats.RigidModel
{
    public class RmvModel
    {
        public RmvCommonHeader CommonHeader { get; set; }
        public IRmvMaterial Material { get; set; }
        public RmvMesh Mesh { get; set; }

        public RmvModel()
        { }

        public void UpdateModelTypeFlag(ModelMaterialEnum newValue)
        {
            var header = CommonHeader;
            header.ModelTypeFlag = newValue;
            CommonHeader = header;
        }

        public void UpdateBoundingBox(Vector3 min, Vector3 max)
        {
            var header = CommonHeader;
            header.BoundingBox.UpdateBoundingBox(min, max);
            CommonHeader = header;
        }
    }
}
