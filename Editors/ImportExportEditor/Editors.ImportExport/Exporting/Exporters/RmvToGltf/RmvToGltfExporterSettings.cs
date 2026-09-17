using Shared.Core.PackFiles.Models;

namespace Editors.ImportExport.Exporting.Exporters.RmvToGltf
{
    public record RmvToGltfExporterSettings(

        PackFile InputModelFile,
        List<PackFile> InputAnimationFiles,
        string OutputPath,
        bool ExportMaterials, 
        bool ConvertMaterialTextureToBlender,
        bool ConvertNormalTextureToBlue,
        bool ExportAnimations,
        bool MirrorMesh,

        // Displacement map quality settings for 3D printing
        bool ExportDisplacementMaps = false,  // NEW: Control whether to export displacement variants
        int DisplacementIterations = 10,
        float DisplacementContrast = 0.1f,
        float DisplacementSharpness = 1.0f,
        bool Export16BitDisplacement = true,
        bool UseMultiScaleProcessing = true,
        bool UsePoissonReconstruction = true
    )
    {
        /// <summary>
        /// Controls whether a skeleton is emitted independently of animation clips.
        /// Keeping this an init-only property preserves the existing positional API.
        /// </summary>
        public bool IncludeSkeleton { get; init; } = true;

        /// <summary>
        /// Controls whether RMV mask textures are emitted as auxiliary PNGs.
        /// They are not part of the glTF material channels, but the editor's
        /// export workflow historically preserves them for manual use.
        /// </summary>
        public bool ExportAuxiliaryMasks { get; init; } = true;
    }
}
