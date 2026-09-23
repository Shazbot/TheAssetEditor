using Editors.ImportExport.Importing.Importers.PngToDds;
using Editors.ImportExport.TextureAtlas;
using GameWorld.Core.Rendering.Materials.Capabilities;
using GameWorld.Core.Rendering.Materials.Capabilities.Utility;
using GameWorld.Core.Rendering.Materials.Shaders;
using GameWorld.Core.SceneNodes;
using Microsoft.Xna.Framework;
using Shared.Core.ErrorHandling;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.Core.Settings;
using Shared.GameFormats.RigidModel.Types;

namespace Editors.KitbasherEditor.Services
{
    public sealed record GeneratedTextureAtlasFile(string Directory, string FullPath, PackFile PackFile);

    public sealed record TextureAtlasMeshReplacement(
        Rmv2MeshNode OriginalMesh,
        Rmv2MeshNode AtlasedMesh);

    public sealed record PreparedTextureAtlasMerge(
        IPackFileContainer TargetPack,
        IReadOnlyList<TextureAtlasMeshReplacement> Replacements,
        IReadOnlyList<Rmv2MeshNode> UntouchedMeshes,
        IReadOnlyList<GeneratedTextureAtlasFile> GeneratedFiles);

    public class TextureAtlasMergeService
    {
        private const string AtlasDirectory = @"textures\asset_editor\atlases";

        private readonly IPackFileService _packFileService;
        private readonly ApplicationSettingsService _applicationSettingsService;

        public TextureAtlasMergeService(
            IPackFileService packFileService,
            ApplicationSettingsService applicationSettingsService)
        {
            _packFileService = packFileService;
            _applicationSettingsService = applicationSettingsService;
        }

        public bool TryPrepare(
            IReadOnlyList<Rmv2MeshNode> sourceMeshes,
            out PreparedTextureAtlasMerge? preparedMerge,
            out ErrorList errors)
        {
            preparedMerge = null;
            errors = new ErrorList();

            try
            {
                if (sourceMeshes.Count < 2)
                {
                    errors.Error("Selection", "Select at least two meshes.");
                    return false;
                }

                var targetPack = _packFileService.GetEditablePack();
                if (targetPack == null)
                {
                    errors.Error("Editable pack", "A writable editable pack is required to store the generated atlas textures.");
                    return false;
                }

                var untouchedMeshes = new List<Rmv2MeshNode>();
                var candidates = new List<(Rmv2MeshNode Mesh, TextureType PrimaryTextureType)>();

                foreach (var mesh in sourceMeshes)
                {
                    // Emissive shaders sample additional textures through the mesh's original
                    // UV0 mapping. This includes prop_emissive shaders, which Asset Editor may
                    // represent internally as MetalRoughPbr_Default, so use source-shader
                    // provenance as well as the capability type.
                    if (mesh.Material.UsesEmissiveShader)
                    {
                        untouchedMeshes.Add(mesh);
                        continue;
                    }

                    if (!TryGetPrimaryTextureType(mesh.Material, out var primaryTextureType))
                    {
                        untouchedMeshes.Add(mesh);
                        continue;
                    }

                    candidates.Add((mesh, primaryTextureType));
                }

                var replacements = new List<TextureAtlasMeshReplacement>();
                var generatedFiles = new List<GeneratedTextureAtlasFile>();

                foreach (var group in candidates.GroupBy(x => x.PrimaryTextureType))
                {
                    var groupMeshes = group.Select(x => x.Mesh).ToList();
                    PrepareAtlasGroup(
                        groupMeshes,
                        group.Key,
                        replacements,
                        untouchedMeshes,
                        generatedFiles);
                }

                if (replacements.Count < 2)
                {
                    errors.Error(
                        "Selection",
                        "No compatible group of at least two non-emissive meshes could be texture-atlased. " +
                        "Meshes that cannot safely share an atlas are left unchanged.");
                    return false;
                }

                preparedMerge = new PreparedTextureAtlasMerge(
                    targetPack,
                    replacements,
                    untouchedMeshes.Distinct().ToList(),
                    generatedFiles);
                return true;
            }
            catch (Exception ex)
            {
                errors.Error("Texture atlas", ex.Message);
                return false;
            }
        }

        private void PrepareAtlasGroup(
            IReadOnlyList<Rmv2MeshNode> groupMeshes,
            TextureType primaryTextureType,
            List<TextureAtlasMeshReplacement> replacements,
            List<Rmv2MeshNode> untouchedMeshes,
            List<GeneratedTextureAtlasFile> generatedFiles)
        {
            if (groupMeshes.Count < 2)
            {
                untouchedMeshes.AddRange(groupMeshes);
                return;
            }

            var preparedMeshes = new List<PreparedMeshSource>();
            foreach (var mesh in groupMeshes)
            {
                var primaryInput = GetTextureInput(mesh.Material, primaryTextureType);
                if (!TryReadTextureBytes(primaryInput, out var primaryBytes))
                {
                    // A missing BaseColour/Diffuse cannot be reconstructed safely. Keep this
                    // mesh exactly as it is rather than blocking the rest of the selection.
                    untouchedMeshes.Add(mesh);
                    continue;
                }

                var (width, height) = TextureAtlasBuilder.GetDimensions(primaryBytes);

                // Use the primary BaseColour/Diffuse resolution as the shared UV-plan density.
                // Secondary channels keep the same normalized placements but choose their own
                // physical atlas dimensions below.
                preparedMeshes.Add(new PreparedMeshSource(mesh, primaryBytes, width, height));
            }

            if (preparedMeshes.Count < 2)
            {
                untouchedMeshes.AddRange(preparedMeshes.Select(x => x.Mesh));
                return;
            }

            var workingMeshes = preparedMeshes
                .Select(x => CloneForAtlas(x.Mesh))
                .ToList();

            var uvBounds = workingMeshes.Select(GetUvBounds).ToArray();
            var layoutSources = new List<TextureAtlasLayoutSource>(workingMeshes.Count);
            for (var i = 0; i < workingMeshes.Count; i++)
            {
                var source = preparedMeshes[i];
                var bounds = uvBounds[i];
                layoutSources.Add(new TextureAtlasLayoutSource(
                    i,
                    source.Width,
                    source.Height,
                    bounds.MinU,
                    bounds.MinV,
                    bounds.MaxU,
                    bounds.MaxV));
            }

            var plan = TextureAtlasBuilder.CreatePlan(layoutSources);
            var atlasStem = BuildAtlasStem(workingMeshes[0].Name);
            var atlasInputs = GetAtlasTextureInputs(workingMeshes[0].Material);

            foreach (var textureType in atlasInputs.Select(x => x.Type))
            {
                var textureBytes = new Dictionary<int, byte[]>();
                var omittedSourceIds = new HashSet<int>();

                for (var i = 0; i < workingMeshes.Count; i++)
                {
                    var input = GetTextureInput(workingMeshes[i].Material, textureType);
                    if (!IsTextureUsed(input) || !TryReadTextureBytes(input, out var bytes))
                    {
                        omittedSourceIds.Add(i);
                        continue;
                    }

                    textureBytes[i] = bytes;
                }

                if (textureBytes.Count == 0)
                    continue;

                var sourceDimensions = textureBytes.ToDictionary(
                    x => x.Key,
                    x => TextureAtlasBuilder.GetDimensions(x.Value));
                var outputDimensions = TextureAtlasBuilder.CalculateOutputDimensions(
                    plan,
                    sourceDimensions);

                var mipPixels = TextureAtlasBuilder.BuildMipPixels(
                    plan,
                    textureBytes,
                    forceOpaqueAlphaSourceIds: null,
                    omittedSourceIds,
                    outputWidth: outputDimensions.Width,
                    outputHeight: outputDimensions.Height);

                var fileName = $"{atlasStem}_{GetTextureSuffix(textureType)}.dds";
                var packFile = PngToDdsImporter.ImportRawMipChain(
                    mipPngs,
                    textureType,
                    _applicationSettingsService.CurrentSettings.CurrentGame,
                    fileName);
                var fullPath = $@"{AtlasDirectory}\{fileName}";

                generatedFiles.Add(new GeneratedTextureAtlasFile(AtlasDirectory, fullPath, packFile));

                // Only meshes whose original texture actually resolved are redirected to this
                // atlas channel. Missing secondary texture paths stay exactly as they were.
                foreach (var sourceId in textureBytes.Keys)
                {
                    var input = GetTextureInput(workingMeshes[sourceId].Material, textureType);
                    input.TexturePath = fullPath;
                }
            }

            ApplyAtlasUvs(workingMeshes, plan);

            for (var i = 0; i < workingMeshes.Count; i++)
                replacements.Add(new TextureAtlasMeshReplacement(preparedMeshes[i].Mesh, workingMeshes[i]));
        }

        private static Rmv2MeshNode CloneForAtlas(Rmv2MeshNode source)
        {
            var clone = (Rmv2MeshNode)source.CreateCopyInstance();
            source.CopyInto(clone, includeMesh: false);
            clone.Geometry = source.Geometry.Clone(includeMesh: true, createGraphicsResources: false);
            clone.Name = source.Name;
            return clone;
        }

        private bool TryReadTextureBytes(TextureInput input, out byte[] bytes)
        {
            bytes = [];

            if (!IsTextureUsed(input))
                return false;

            var file = _packFileService.FindFile(input.TexturePath);
            if (file == null)
                return false;

            var fileBytes = file.DataSource.ReadData();
            if (fileBytes == null || fileBytes.Length == 0)
                return false;

            bytes = fileBytes;
            return true;
        }

        private static bool TryGetPrimaryTextureType(CapabilityMaterial material, out TextureType primaryTextureType)
        {
            if (material.TryGetCapability<MetalRoughCapability>() != null)
            {
                primaryTextureType = TextureType.BaseColour;
                return true;
            }

            if (material.TryGetCapability<SpecGlossCapability>() != null)
            {
                primaryTextureType = TextureType.Diffuse;
                return true;
            }

            primaryTextureType = default;
            return false;
        }

        private static void ApplyAtlasUvs(IReadOnlyList<Rmv2MeshNode> meshes, TextureAtlasPlan plan)
        {
            foreach (var placement in plan.Placements)
            {
                var mesh = meshes[placement.Id];
                var referencedVertices = mesh.Geometry.IndexArray.Distinct();

                foreach (var vertexIndex in referencedVertices)
                {
                    var uv = mesh.Geometry.VertexArray[vertexIndex].TextureCoordinate;
                    var remapped = placement.TransformUv(uv.X, uv.Y, plan.Width, plan.Height);
                    mesh.Geometry.VertexArray[vertexIndex].TextureCoordinate = new Vector2(remapped.U, remapped.V);
                }

            }
        }

        private static UvBounds GetUvBounds(Rmv2MeshNode mesh)
        {
            if (mesh.Geometry.IndexArray.Length == 0)
                throw new InvalidOperationException($"Mesh '{mesh.Name}' has no triangles.");

            var minU = float.MaxValue;
            var minV = float.MaxValue;
            var maxU = float.MinValue;
            var maxV = float.MinValue;

            foreach (var vertexIndex in mesh.Geometry.IndexArray.Distinct())
            {
                if (vertexIndex >= mesh.Geometry.VertexArray.Length)
                    throw new InvalidOperationException($"Mesh '{mesh.Name}' contains an invalid vertex index.");

                var uv = mesh.Geometry.VertexArray[vertexIndex].TextureCoordinate;
                minU = Math.Min(minU, uv.X);
                minV = Math.Min(minV, uv.Y);
                maxU = Math.Max(maxU, uv.X);
                maxV = Math.Max(maxV, uv.Y);
            }

            return new UvBounds(minU, minV, maxU, maxV);
        }

        private static List<TextureInput> GetAtlasTextureInputs(CapabilityMaterial material)
        {
            var metalRough = material.TryGetCapability<MetalRoughCapability>();
            if (metalRough != null)
            {
                return
                [
                    metalRough.BaseColour,
                    metalRough.MaterialMap,
                    metalRough.NormalMap,
                    metalRough.Mask
                ];
            }

            var specGloss = material.TryGetCapability<SpecGlossCapability>();
            if (specGloss != null)
            {
                return
                [
                    specGloss.DiffuseMap,
                    specGloss.SpecularMap,
                    specGloss.GlossMap,
                    specGloss.NormalMap,
                    specGloss.Mask
                ];
            }

            return [];
        }

        private static TextureInput GetTextureInput(CapabilityMaterial material, TextureType textureType)
        {
            var input = GetAtlasTextureInputs(material).FirstOrDefault(x => x.Type == textureType);
            return input ?? throw new InvalidOperationException($"Material does not contain texture input {textureType}.");
        }

        private static bool IsTextureUsed(TextureInput input)
            => input.UseTexture && !string.IsNullOrWhiteSpace(input.TexturePath);

        private static string BuildAtlasStem(string meshName)
        {
            var safeName = new string(meshName
                .ToLowerInvariant()
                .Select(x => char.IsLetterOrDigit(x) ? x : '_')
                .ToArray())
                .Trim('_');

            if (safeName.Length == 0)
                safeName = "mesh";
            if (safeName.Length > 48)
                safeName = safeName[..48];

            return $"{safeName}_atlas_{Guid.NewGuid().ToString("N")[..8]}";
        }

        private static string GetTextureSuffix(TextureType textureType)
        {
            return textureType switch
            {
                TextureType.BaseColour => "base_colour",
                TextureType.Diffuse => "diffuse",
                TextureType.MaterialMap => "material_map",
                TextureType.Normal => "normal",
                TextureType.Mask => "mask",
                TextureType.Specular => "specular",
                TextureType.Gloss => "gloss",
                _ => textureType.ToString().ToLowerInvariant()
            };
        }

        private sealed record PreparedMeshSource(
            Rmv2MeshNode Mesh,
            byte[] PrimaryBytes,
            int Width,
            int Height);

        private sealed record UvBounds(float MinU, float MinV, float MaxU, float MaxV);
    }
}
