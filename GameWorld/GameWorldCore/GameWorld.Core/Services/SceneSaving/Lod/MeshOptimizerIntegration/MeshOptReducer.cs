using GameWorld.Core.Rendering;
using GameWorld.Core.Rendering.Geometry;
using GameWorld.MeshOptimizer;

namespace GameWorld.Core.Services.SceneSaving.Lod.MeshOptimizerIntegration;

public sealed record MeshOptReducerResult(MeshObject Mesh, MeshOptReducerStats Stats);

/// <summary>Thin <see cref="MeshObject"/> adapter over <see cref="MeshOptSimplifier"/>.</summary>
public static class MeshOptReducer
{
    /// <summary>
    /// Reduces a mesh and returns a copy containing only original vertex records and a new index buffer.
    /// Use <see cref="Reduce"/> when the requested/achieved counts and error statistics are needed.
    /// </summary>
    public static MeshObject GetReducedMeshCopy(MeshObject original, float factor, MeshOptReducerOptions? options = null)
        => Reduce(original, factor, options).Mesh;

    public static MeshOptReducerResult Reduce(MeshObject original, float factor, MeshOptReducerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(original);

        var vertices = original.VertexArray;
        var positions = new float[checked(vertices.Length * 3)];
        var normals = new float[positions.Length];
        var uvs = new float[checked(vertices.Length * 2)];
        for (var i = 0; i < vertices.Length; i++)
        {
            var vertex = vertices[i];
            positions[i * 3] = vertex.Position.X;
            positions[i * 3 + 1] = vertex.Position.Y;
            positions[i * 3 + 2] = vertex.Position.Z;
            normals[i * 3] = vertex.Normal.X;
            normals[i * 3 + 1] = vertex.Normal.Y;
            normals[i * 3 + 2] = vertex.Normal.Z;
            uvs[i * 2] = vertex.TextureCoordinate.X;
            uvs[i * 2 + 1] = vertex.TextureCoordinate.Y;
        }

        var result = MeshOptSimplifier.Simplify(positions, normals, uvs, original.IndexArray, factor, options);
        if (result.Stats.ReductionSkipped)
        {
            var unchanged = CloneWithBuffers(
                original,
                (VertexPositionNormalTextureCustom[])original.VertexArray.Clone(),
                (ushort[])original.IndexArray.Clone());
            return new MeshOptReducerResult(unchanged, result.Stats);
        }

        var outputVertices = new VertexPositionNormalTextureCustom[result.VertexCount];
        for (var oldVertexIndex = 0; oldVertexIndex < result.Remap.Length; oldVertexIndex++)
        {
            var newVertexIndex = result.Remap[oldVertexIndex];
            if (newVertexIndex == uint.MaxValue)
                continue;

            outputVertices[newVertexIndex] = vertices[oldVertexIndex];
        }

        return new MeshOptReducerResult(CloneWithBuffers(original, outputVertices, result.Indices), result.Stats);
    }

    private static MeshObject CloneWithBuffers(MeshObject original, VertexPositionNormalTextureCustom[] vertices, ushort[] indices)
    {
        var clone = original.Clone(false);
        clone.IndexArray = indices;
        clone.VertexArray = vertices;
        clone.RebuildIndexBuffer();
        clone.RebuildVertexBuffer();
        return clone;
    }
}
