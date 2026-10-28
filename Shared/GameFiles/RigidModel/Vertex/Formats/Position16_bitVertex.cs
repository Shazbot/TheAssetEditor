using System.Runtime.InteropServices;
using System.Numerics;
using Shared.ByteParsing;

namespace Shared.GameFormats.RigidModel.Vertex.Formats
{
    public class Position16_bitVertexCreator : IVertexCreator
    {
        public VertexFormat Type => VertexFormat.Position16_bit;
        public bool AddTintColour { get; set; }
        public uint GetVertexSize(RmvVersionEnum rmvVersion)
        {

            return (uint)ByteHelper.GetSize<Data>();
        }
        public bool ForceComputeNormals => true;

        public CommonVertex[] ReadArray(RmvVersionEnum rmvVersion, byte[] buffer, int offset, int vertexSize, int vertexCount)
        {
            var vertexList = new CommonVertex[vertexCount];
            for (var i = 0; i < vertexCount; i++)
                vertexList[i] = Read(rmvVersion, buffer, offset + i * vertexSize, vertexSize);
            return vertexList;
        }

        CommonVertex Read(RmvVersionEnum rmvVersion, byte[] buffer, int offset, int vertexSize)
        {
            var data = VertexLoadHelper.GetVertexBytes<Data>(buffer, offset);

            var vertex = new CommonVertex()
            {
                Position = VertexLoadHelper.CreatVector4Float(data.Slice(0, 16)).ToVector4(1),
                Normal = Vector3.UnitY,
                BiNormal = Vector3.UnitY,
                Tangent = Vector3.UnitY,
                Uv = Vector2.Zero,

                BoneIndex = Array.Empty<byte>(),
                BoneWeight = Array.Empty<float>(),
                WeightCount = 0
            };

            return vertex;
        }


        public byte[] Write(RmvVersionEnum rmvVersion, CommonVertex vertex)
        {
            throw new NotImplementedException();
        }

        public struct Data //16
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] position;     // 4 x 4

            //[MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            //public byte[] normal;       // 4 x 1
            //
            //[MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            //public byte[] uv;           // 2 x 2

            //[MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            //public byte[] biNormal;     // 4 x 1
            //
            //[MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            //public byte[] tangent;      // 4 x 1
        }
    }
}
