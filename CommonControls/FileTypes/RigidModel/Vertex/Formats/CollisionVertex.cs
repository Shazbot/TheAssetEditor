﻿using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace CommonControls.FileTypes.RigidModel.Vertex.Formats
{


    public class CollisionVertexCreator : IVertexCreator
    {
        public VertexFormat Type => VertexFormat.Collision_Format;
        public bool AddTintColour { get; set; }
        public uint GetVertexSize(RmvVersionEnum rmvVersion)
        {
            return (uint)ByteHelper.GetSize<Data>();
        }

        public bool ForceComputeNormals => true;

        public CommonVertex Read(RmvVersionEnum rmvVersion, byte[] buffer, int offset, int vertexSize)
        {
            var vertexData = ByteHelper.ByteArrayToStructure<Data>(buffer, offset);

            var vertex = new CommonVertex()
            {
                Position = VertexLoadHelper.CreatVector4Float(vertexData.position).ToVector4(1),
                Normal = Vector3.UnitY, //VertexLoadHelper.CreatVector4Byte(vertexData.normal).ToVector3(),
                BiNormal = Vector3.UnitY,
                Tangent = Vector3.UnitY,

                Uv = Vector2.Zero,// VertexLoadHelper.CreatVector2HalfFloat(vertexData.uv).ToVector2(),

                BoneIndex = new byte[] { },
                BoneWeight = new float[] { },
                WeightCount = 0
            };

            return vertex;
        }


        public byte[] Write(RmvVersionEnum rmvVersion, CommonVertex vertex)
        {
            throw new NotImplementedException();
        }

        public struct Data //24
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)]
            public byte[] position;     // 4 x 3

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)]
            public byte[] normal;     // 4 x 3
        }
    }
}
