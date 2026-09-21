using Editors.ImportExport.Importing.Importers.PngToDds;
using Editors.ImportExport.TextureAtlas;
using GameWorld.Core.Rendering.Materials.Capabilities;
using GameWorld.Core.Rendering.Materials.Capabilities.Utility;
using GameWorld.Core.Rendering.Materials.Shaders;
using GameWorld.Core.SceneNodes;
using GameWorld.Core.Utility;
using Microsoft.Xna.Framework;
using Shared.Core.ErrorHandling;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.Core.Settings;
using Shared.GameFormats.RigidModel.Types;

namespace Editors.KitbasherEditor.Services
{
    public sealed record GeneratedTextureAtlasFile(string Directory, string FullPath, PackFile PackFile);

    public sealed record PreparedTextureAtlasMerge(
        IPackFileContainer TargetPack,
        IReadOnlyList<Rmv2MeshNode> CombinedMeshes,
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

                var totalIndexCount = sourceMeshes.Sum(x => (long)x.Geometry.GetIndexCount());
                if (totalIndexCount > ushort.MaxValue)
                {
                    errors.Error(
                        "Index limit exceeded",
                        $"Combined mesh would have {totalIndexCount} indices, which exceeds the maximum of {ushort.MaxValue}.");
                    return false;
                }

                var vertexFormat = sourceMeshes[0].Geometry.VertexFormat;
                if (sourceMeshes.Any(x => x.Geometry.VertexFormat != vertexFormat))
                {
                    errors.Error("Vertex format", "All selected meshes must use the same vertex format.");
                    return false;
                }

                var materialType = sourceMeshes[0].Material.Type;
                if (sourceMeshes.Any(x => x.Material.Type != materialType))
                {
                    errors.Error("Material type", "All selected meshes must use the same material type.");
                    return false;
                }

                if (!TryGetAtlasConfiguration(sourceMeshes, out var primaryTextureType, out var textureUsage, errors))
                    return false;

                if (!ValidateNonAtlasMaterialState(sourceMeshes, textureUsage, errors))
                    return false;

                var workingMeshes = sourceMeshes
                    .Select(x =>
                    {
                        var clone = SceneNodeHelper.CloneNode(x);
                        clone.Name = x.Name;
                        return clone;
                    })
                    .ToList();

                var uvBounds = new UvBounds[workingMeshes.Count];
                for (var i = 0; i < workingMeshes.Count; i++)
                    uvBounds[i] = GetUvBounds(workingMeshes[i]);

                var primarySources = new List<TextureAtlasSource>(workingMeshes.Count);
                for (var i = 0; i < workingMeshes.Count; i++)
                {
                    var input = GetTextureInput(workingMeshes[i].Material, primaryTextureType);
                    var bytes = ReadTextureBytes(input, workingMeshes[i].Name);
                    var bounds = uvBounds[i];

                    primarySources.Add(new TextureAtlasSource(
                        i,
                        bytes,
                        bounds.MinU,
                        bounds.MinV,
                        bounds.MaxU,
                        bounds.MaxV));
                }

                var plan = TextureAtlasBuilder.CreatePlanFromDds(primarySources);
                var atlasStem = BuildAtlasStem(sourceMeshes[0].Name);
                var generatedFiles = new List<GeneratedTextureAtlasFile>();
                var generatedPaths = new Dictionary<TextureType, string>();

                foreach (var (textureType, isUsed) in textureUsage)
                {
                    if (!isUsed)
                        continue;

                    var textureBytes = new Dictionary<int, byte[]>(workingMeshes.Count);
                    for (var i = 0; i < workingMeshes.Count; i++)
                    {
                        var input = GetTextureInput(workingMeshes[i].Material, textureType);
                        textureBytes[i] = ReadTextureBytes(input, workingMeshes[i].Name);
                    }

                    var pngBytes = TextureAtlasBuilder.BuildPng(plan, textureBytes);
                    var fileName = $"{atlasStem}_{GetTextureSuffix(textureType)}.dds";
                    var packFile = PngToDdsImporter.ImportRaw(
                        pngBytes,
                        textureType,
                        _applicationSettingsService.CurrentSettings.CurrentGame,
                        fileName);
                    var fullPath = $@"{AtlasDirectory}\{fileName}";

                    generatedFiles.Add(new GeneratedTextureAtlasFile(AtlasDirectory, fullPath, packFile));
                    generatedPaths[textureType] = fullPath;
                }

                ApplyAtlasMaterials(workingMeshes, textureUsage, generatedPaths);
                ApplyAtlasUvs(workingMeshes, plan);

                if (!ModelCombiner.HasPotentialCombineMeshes(workingMeshes, out var combineErrors))
                {
                    var description = string.Join(
                        Environment.NewLine,
                        combineErrors.Errors.Select(x => $"{x.ItemName}: {x.Description}"));
                    errors.Error("Combine", string.IsNullOrWhiteSpace(description)
                        ? "The atlased meshes are still not compatible for merging."
                        : description);
                    return false;
                }

                var combinedMeshes = ModelCombiner.CombineMeshes(workingMeshes, addPrefix: true);
                if (combinedMeshes.Count != 1)
                {
                    errors.Error(
                        "Combine",
                        $"Expected the atlas operation to produce one combined mesh, but it produced {combinedMeshes.Count}.");
                    return false;
                }

                preparedMerge = new PreparedTextureAtlasMerge(targetPack, combinedMeshes, generatedFiles);
                return true;
            }
            catch (Exception ex)
            {
                errors.Error("Texture atlas", ex.Message);
                return false;
            }
        }

        private bool TryGetAtlasConfiguration(
            IReadOnlyList<Rmv2MeshNode> meshes,
            out TextureType primaryTextureType,
            out Dictionary<TextureType, bool> textureUsage,
            ErrorList errors)
        {
            primaryTextureType = default;
            textureUsage = [];

            var firstInputs = GetAtlasTextureInputs(meshes[0].Material);
            if (firstInputs.Count == 0)
            {
                errors.Error("Material", "The selected material type does not expose a supported BaseColour/Diffuse texture set.");
                return false;
            }

            primaryTextureType = meshes[0].Material.TryGetCapability<MetalRoughCapability>() != null
                ? TextureType.BaseColour
                : TextureType.Diffuse;

            var expectedTypes = firstInputs.Select(x => x.Type).ToArray();
            foreach (var mesh in meshes)
            {
                var inputs = GetAtlasTextureInputs(mesh.Material);
                if (!inputs.Select(x => x.Type).SequenceEqual(expectedTypes))
                {
                    errors.Error("Material", $"Mesh '{mesh.Name}' has a different atlas texture layout.");
                    return false;
                }

                foreach (var input in inputs)
                {
                    if (input.UseTexture && string.IsNullOrWhiteSpace(input.TexturePath))
                    {
                        errors.Error("Texture", $"Mesh '{mesh.Name}' enables {input.Type}, but its texture path is empty.");
                        return false;
                    }
                }
            }

            foreach (var textureType in expectedTypes)
            {
                var states = meshes
                    .Select(x => IsTextureUsed(GetTextureInput(x.Material, textureType)))
                    .Distinct()
                    .ToList();

                if (states.Count != 1)
                {
                    errors.Error(
                        "Texture set",
                        $"All selected meshes must either all use or all omit {textureType}. Mixed usage is not supported by the safe atlas path.");
                    return false;
                }

                textureUsage[textureType] = states[0];
            }

            if (!textureUsage.TryGetValue(primaryTextureType, out var primaryUsed) || !primaryUsed)
            {
                errors.Error("Texture set", $"All selected meshes must use {primaryTextureType}.");
                return false;
            }

            return true;
        }

        private static bool ValidateNonAtlasMaterialState(
            IReadOnlyList<Rmv2MeshNode> meshes,
            IReadOnlyDictionary<TextureType, bool> textureUsage,
            ErrorList errors)
        {
            var normalizedMaterials = meshes.Select(x => x.Material.Clone()).ToList();
            foreach (var material in normalizedMaterials)
            {
                foreach (var input in GetAtlasTextureInputs(material))
                {
                    var isUsed = textureUsage[input.Type];
                    input.UseTexture = isUsed;
                    input.TexturePath = isUsed ? $"__asset_editor_atlas_{input.Type}__" : string.Empty;
                }
            }

            var first = normalizedMaterials[0];
            for (var i = 1; i < normalizedMaterials.Count; i++)
            {
                var comparison = first.AreEqual(normalizedMaterials[i]);
                if (!comparison.Result)
                {
                    errors.Error(
                        "Material mismatch",
                        $"Selected meshes differ in a material property that is not consolidated by the texture atlas: {comparison.Message}");
                    return false;
                }
            }

            return true;
        }

        private byte[] ReadTextureBytes(TextureInput input, string meshName)
        {
            if (!IsTextureUsed(input))
                throw new InvalidOperationException($"Mesh '{meshName}' does not use required texture {input.Type}.");

            var file = _packFileService.FindFile(input.TexturePath);
            if (file == null)
                throw new FileNotFoundException($"Could not find {input.Type} texture '{input.TexturePath}' for mesh '{meshName}'.");

            var bytes = file.DataSource.ReadData();
            if (bytes == null || bytes.Length == 0)
                throw new InvalidOperationException($"Texture '{input.TexturePath}' is empty.");

            return bytes;
        }

        private static void ApplyAtlasMaterials(
            IReadOnlyList<Rmv2MeshNode> meshes,
            IReadOnlyDictionary<TextureType, bool> textureUsage,
            IReadOnlyDictionary<TextureType, string> generatedPaths)
        {
            foreach (var mesh in meshes)
            {
                foreach (var input in GetAtlasTextureInputs(mesh.Material))
                {
                    var isUsed = textureUsage[input.Type];
                    input.UseTexture = isUsed;
                    input.TexturePath = isUsed ? generatedPaths[input.Type] : string.Empty;
                }
            }
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

                mesh.Geometry.RebuildVertexBuffer();
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

        private sealed record UvBounds(float MinU, float MinV, float MaxU, float MaxV);
    }
}
