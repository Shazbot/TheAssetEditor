using GameWorld.Core.Rendering;
using GameWorld.Core.Rendering.Geometry;
using MeshDecimator;
using Microsoft.Xna.Framework;

namespace GameWorld.Core.Services.SceneSaving.Lod.MeshDecimatorIntegration
{
    public class DecimatorMeshOptimizer
    {
        public static MeshObject GetReducedMeshCopy(MeshObject original, float factor)
        {
            ArgumentNullException.ThrowIfNull(original);
            if (!float.IsFinite(factor) || factor < 0)
                throw new ArgumentOutOfRangeException(nameof(factor), "The reduction factor must be finite and non-negative.");

            if (factor >= 1 || original.IndexArray.Length < 3 || original.IndexArray.Length % 3 != 0)
                return CloneUnchanged(original);

            var quality = factor;
            var sourceVertices = original.VertexArray.Select(x => new MeshDecimator.Math.Vector3d(x.Position.X, x.Position.Y, x.Position.Z)).ToArray();
            var sourceSubMeshIndices = original.IndexArray.Select(x => (int)x).ToArray();

            var sourceMesh = new Mesh(sourceVertices, sourceSubMeshIndices);
            sourceMesh.Normals = original.VertexArray.Select(x => new MeshDecimator.Math.Vector3(x.Normal.X, x.Normal.Y, x.Normal.Z)).ToArray();
            sourceMesh.Tangents = original.VertexArray.Select(x => new MeshDecimator.Math.Vector4(x.Tangent.X, x.Tangent.Y, x.Tangent.Z, GetHandedness(x))).ToArray();
            sourceMesh.SetUVs(0, original.VertexArray.Select(x => new MeshDecimator.Math.Vector2(x.TextureCoordinate.X, x.TextureCoordinate.Y)).ToArray());
            sourceMesh.SetUVs(1, original.VertexArray.Select(x => new MeshDecimator.Math.Vector2(x.TextureCoordinate1.X, x.TextureCoordinate1.Y)).ToArray());

            if (original.WeightCount == 4)
            {
                sourceMesh.BoneWeights = original.VertexArray.Select(x => new BoneWeight(
                    (int)x.BlendIndices.X, (int)x.BlendIndices.Y, (int)x.BlendIndices.Z, (int)x.BlendIndices.W,
                    x.BlendWeights.X, x.BlendWeights.Y, x.BlendWeights.Z, x.BlendWeights.W)).ToArray();
            }
            else if (original.WeightCount == 2)
            {
                sourceMesh.BoneWeights = original.VertexArray.Select(x => new BoneWeight(
                      (int)x.BlendIndices.X, (int)x.BlendIndices.Y, 0, 0,
                      x.BlendWeights.X, x.BlendWeights.Y, 0, 0)).ToArray();
            }
            else if (original.WeightCount == 0)
            {
                sourceMesh.BoneWeights = original.VertexArray.Select(x => new BoneWeight(
                      0, 0, 0, 0,
                      0, 0, 0, 0)).ToArray();
            }

            var currentTriangleCount = original.IndexArray.Length / 3;
            var targetTriangleCount = Math.Max(1, (int)Math.Ceiling(currentTriangleCount * quality));

            var algorithm = MeshDecimation.CreateAlgorithm(Algorithm.FastQuadricMesh);
            algorithm.PreserveBorders = true;
            var destMesh = MeshDecimation.DecimateMesh(algorithm, sourceMesh, targetTriangleCount);

            var destVertices = destMesh.Vertices;
            var destNormals = destMesh.Normals;
            var destIndices = destMesh.GetSubMeshIndices();

            var outputVerts = new VertexPositionNormalTextureCustom[destVertices.Length];

            for (var i = 0; i < outputVerts.Length; i++)
            {
                var pos = destMesh.Vertices[i];
                var norm = destMesh.Normals[i];
                var tangents = destMesh.Tangents[i];
                var uv = destMesh.UV1[i];
                var uv1 = destMesh.UV2[i];
                var boneWeight = destMesh.BoneWeights[i];

                var normal = NormalizeOrFallback(new Vector3(norm.x, norm.y, norm.z), Vector3.UnitY);
                var tangentCandidate = new Vector3(tangents.x, tangents.y, tangents.z);
                tangentCandidate -= normal * Vector3.Dot(normal, tangentCandidate);
                var tangentFallback = CreatePerpendicularUnitVector(normal);
                var tangent = NormalizeOrFallback(tangentCandidate, tangentFallback);
                var handedness = tangents.w < 0 ? -1f : 1f;
                var binormal = Vector3.Cross(normal, tangent) * handedness;

                var vert = new VertexPositionNormalTextureCustom();
                vert.Position = new Vector4((float)pos.x, (float)pos.y, (float)pos.z, 1);
                vert.Normal = normal;
                vert.Tangent = tangent;
                vert.BiNormal = binormal;
                vert.TextureCoordinate = new Vector2(uv.x, uv.y);
                vert.TextureCoordinate1 = new Vector2(uv1.x, uv1.y);

                if (original.WeightCount == 4)
                {
                    vert.BlendIndices = new Vector4(boneWeight.boneIndex0, boneWeight.boneIndex1, boneWeight.boneIndex2, boneWeight.boneIndex3);
                    vert.BlendWeights = new Vector4(boneWeight.boneWeight0, boneWeight.boneWeight1, boneWeight.boneWeight2, boneWeight.boneWeight3);
                }
                else if (original.WeightCount == 2)
                {
                    vert.BlendIndices = new Vector4(boneWeight.boneIndex0, boneWeight.boneIndex1, 0, 0);
                    vert.BlendWeights = new Vector4(boneWeight.boneWeight0, boneWeight.boneWeight1, 0, 0);
                }
                else if (original.WeightCount == 0)
                {
                    vert.BlendIndices = new Vector4(0, 0, 0, 0);
                    vert.BlendWeights = new Vector4(0, 0, 0, 0);
                }

                if (vert.BlendWeights.X + vert.BlendWeights.Y + vert.BlendWeights.Z + vert.BlendWeights.W == 0)
                    vert.BlendWeights.X = 1;

                outputVerts[i] = vert;
            }

            var clone = original.Clone(false);
            clone.IndexArray = destIndices[0].Select(x => (ushort)x).ToArray();
            clone.VertexArray = outputVerts;

            clone.RebuildIndexBuffer();
            clone.RebuildVertexBuffer();

            return clone;
        }

        private static float GetHandedness(VertexPositionNormalTextureCustom vertex)
        {
            var orientation = Vector3.Dot(Vector3.Cross(vertex.Normal, vertex.Tangent), vertex.BiNormal);
            return float.IsFinite(orientation) && orientation < 0 ? -1f : 1f;
        }

        private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
        {
            var lengthSquared = value.LengthSquared();
            if (float.IsFinite(lengthSquared) && lengthSquared > 1.0e-20f)
                return value / MathF.Sqrt(lengthSquared);

            var fallbackLengthSquared = fallback.LengthSquared();
            if (float.IsFinite(fallbackLengthSquared) && fallbackLengthSquared > 1.0e-20f)
                return fallback / MathF.Sqrt(fallbackLengthSquared);

            return Vector3.UnitY;
        }

        private static Vector3 CreatePerpendicularUnitVector(Vector3 normal)
        {
            var axis = MathF.Abs(normal.X) < 0.8f ? Vector3.UnitX : Vector3.UnitZ;
            return NormalizeOrFallback(Vector3.Cross(normal, axis), Vector3.UnitX);
        }

        private static MeshObject CloneUnchanged(MeshObject original)
        {
            var clone = original.Clone(false);
            clone.IndexArray = (ushort[])original.IndexArray.Clone();
            clone.VertexArray = (VertexPositionNormalTextureCustom[])original.VertexArray.Clone();
            clone.RebuildIndexBuffer();
            clone.RebuildVertexBuffer();
            return clone;
        }
    }
}
