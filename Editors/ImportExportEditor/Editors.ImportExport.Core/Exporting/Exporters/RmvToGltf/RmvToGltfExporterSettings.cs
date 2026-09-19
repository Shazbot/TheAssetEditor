using Shared.Core.PackFiles.Models;
using GameWorld.Core.Services;

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
        /// Explicit candidate choices for composed VariantMeshDefinition exports.
        /// Empty keeps the normal first-renderable-candidate behavior.
        /// </summary>
        public IReadOnlyList<VariantMeshSelection> VariantMeshSelections { get; init; } = [];

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

        /// <summary>
        /// Uses lossless raw-RGBA KTX2 textures with Zstd supercompression for
        /// glTF material channels. Disabled by default so the normal AssetEditor
        /// export workflow continues producing PNG files.
        /// </summary>
        public bool UseKtx2Textures { get; init; } = false;

        /// <summary>
        /// Maximum number of texture conversions allowed to run concurrently.
        /// The normal AssetEditor workflow stays serial by default; headless
        /// preview callers can opt into bounded CPU parallelism.
        /// </summary>
        public int MaxTextureParallelism { get; init; } = 1;
    }
}
