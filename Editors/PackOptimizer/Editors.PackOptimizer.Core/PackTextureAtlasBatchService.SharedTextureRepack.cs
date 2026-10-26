using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Editors.ImportExport.Dds.Native;
using Editors.ImportExport.Dds;
using Shared.Core.PackFiles.Models;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.MaterialHeaders;
using Shared.GameFormats.RigidModel.Types;

namespace Editors.PackOptimizer.Core
{
    public sealed partial class PackTextureAtlasBatchService
    {
        // Opt-in "Repack shared textures". Mod rigids that keep a whole large texture loaded
        // while their parts use a few percent of it get new, smaller textures holding only the
        // used regions at the SAME texel density (no downscaling), new UVs and new materials;
        // the source files are never edited. It acts on signature sets of the per-unit repack
        // estimate (Option C) whose users are all mod-owned rigids, one generated texture
        // serving every rigid of the set, and re-checks the saving on the ACTUAL bytes before
        // it writes anything.
        //
        // Design in short:
        //  * Every rigid of the set is checked on its own. The parts (all LODs) of an
        //    ineligible rigid that use the set's textures are dropped like any other part, so
        //    their textures leave the set: every user of a repacked texture is rewritten.
        //  * Parts of the eligible rigids that use the set's textures must be "closed": every
        //    atlasable texture of every material bound to the part is in the set or is a
        //    uniform colour (kept as authored), the part has one texture per slot across its
        //    bindings, and every triangle sits inside one integer UV tile. Anything else drops
        //    the part's textures from the set (until the set is closed) and the estimate is
        //    recomputed.
        //  * Parts with the same texture tuple share one UV space, whatever their rigid. Per
        //    tuple the union of all its parts' triangles (all LODs, all rigids) is rasterised
        //    conservatively on a grid of
        //    cells of 8+ source pixels, split into 8-connected components, boxed, padded by
        //    one cell and merged while the padded boxes overlap: the charts.
        //  * All charts of all tuples go into one shared layout (deterministic packer (best of shelf, MaxRects, skyline),
        //    power-of-two canvas). Every slot's output is that layout scaled by the slot's
        //    finest source resolution, so no tuple loses pixels; coarser slots are
        //    replicated up (nearest, exact copies) and the overhead is reported.
        private static readonly TimeSpan SharedRepackAnalysisBudget = TimeSpan.FromMinutes(10);
        // A set is only built when it saves at least this much, estimated AND actual. 1 MiB keeps
        // most of what 512 KiB adds on !ak_teb3 (69.4 of 71.9 MB) with fewer tiny sets.
        private const long SharedRepackMinimumSavingBytes = 1L * 1024 * 1024;
        // A layout group is only built when it saves at least this much (and more than nothing).
        private const long SharedRepackMinimumGroupSavingBytes = 512L * 1024;
        private const int SharedRepackMaximumSide = 4096;
        // Mip 2 and 3 are compared against the source's own level; the levels the block copy
        // covers must be exact, the others get this looser tolerance.
        private const double SharedRepackLevelOutlierError = 96;
        private const double SharedRepackLevelMeanLimit = 12;
        // Re-encoded lower mips (BC1 of an already compressed, high-contrast mip) have single
        // outlier texels; a systematic fault shows as many of them. Fail above this share.
        private const double SharedRepackLevelOutlierShare = 0.005;
        // Source pixels per grid cell: four 4x4 BC blocks. The conservative rasteriser over-marks
        // at most one cell around each triangle edge, which is the same order as the one-cell
        // padding the chart gets anyway. Chart rectangles are cell aligned, so the cell also
        // decides how many mip levels can be copied block for block (a cell of 16 source pixels
        // stays block aligned down to mip 2). Measured on the real teb_inquisitor_staff_02 set:
        // cell 8 -> 699,168 bytes, 2 levels copied; cell 16 -> 699,424 bytes, 3 levels; cell 32 ->
        // 1,398,304 bytes (the canvas jumps to the next power of two), 4 levels. Larger textures
        // use at most 1024 cells per side.
        private const int SharedRepackMinimumCell = 16;
        private const int SharedRepackMaximumCellsPerSide = 1024;
        // Chart rectangles are snapped outward to this many pixels in the
        // COARSEST slot (so 32 << downshift reference pixels) and placed on multiples of it, so the
        // 4x4 block grids of source and output line up in every slot down to mip 3.
        private const int SharedRepackChartAlignment = 32;
        // A slot more than 2^4 times smaller than the reference would force a chart grid of
        // 4 << 4 = 64 source pixels or more; such a tuple is left untouched instead.
        private const int SharedRepackMaximumCell = 64;
        // Member textures up to this many pixels per side are "tiny" (the 4x4 masks of vanilla
        // materials): they are kept as authored when uniform, else the tuple is left untouched.
        private const int SharedRepackTinyTextureSide = 16;
        // A texture outside the set is only looked at up to this size: a uniform one (a flat
        // colour such as commontextures\default_black.dds) never blocks a part and is kept as
        // authored; a larger one counts as not uniform without being decoded.
        private const int SharedRepackUniformTextureSide = 1024;
        // A merged plan stops growing above this many signature sets or rigids (a bound on the plan's
        // closure and layout work; measured on the two big mods, the largest plan that pays takes 26
        // sets in 42 rigids, and 32 and 48 recover everything uncapped runs do).
        private const int SharedRepackMaximumMergedSets = 32;
        private const int SharedRepackMaximumMergedRigids = 48;
        // Rigids the cross-rigid writer generated count separately: one source part can be
        // copied into several generated models.
        private const int SharedRepackMaximumMergedGeneratedRigids = 64;
        // Splitting a layout group over smaller pages: at most this many pages, this many
        // first-page sizes tried per level and this many greedy fills per group.
        private const int SharedRepackMaximumSplitPages = 4;
        private const int SharedRepackSplitCandidatesPerLevel = 12;
        // Operations (free rectangles or skyline segments looked at) the better packers may spend per
        // layout group; when spent only the shelf result is used.
        private const long SharedRepackLayoutOperationBudget = 40_000_000;
        private const int SharedRepackMaximumSplitAttempts = 400;
        private const int SharedRepackMaximumCharts = 4000;
        private const int SharedRepackMaximumComponents = 8000;
        // Pixel equivalence: BC recompression of already compressed content. A flat or smooth
        // block returns within a few levels; a block with a hard edge can move by a few tens.
        // Copying the wrong region differs by about 85 levels on average for unrelated content,
        // so a sample over the limit, or a mean over the second limit, fails the run.
        private const double SharedRepackSampleMaximumError = 96;
        private const double SharedRepackSampleMeanLimit = 16;
        private const int SharedRepackMaximumSampleTriangles = 6000;
        private const TEX_COMPRESS_FLAGS SharedRepackGutterCompressFlags = TEX_COMPRESS_FLAGS.BC7_QUICK;
        private const TEX_COMPRESS_FLAGS SharedRepackVisibleCompressFlags = TEX_COMPRESS_FLAGS.DEFAULT;

        private const string SharedRepackReasonMaterialUnreadable =
            "a bound material is missing or unreadable";
        private const string SharedRepackReasonMaterialOverridesVanilla =
            "a bound material is a source-pack override of a vanilla material";
        private const string SharedRepackReasonSlot =
            "a bound material has a texture in the non-atlasable slot ";
        private const string SharedRepackFlowMapSlot = "t_xml_flow_map";
        private const string SharedRepackFlowGuidSlot = "t_xml_df7959d8_d174_49d2_88f1_ffc28b954e5f";
        private const string SharedRepackFlowNoiseSlot = "t_xml_flow_noise";
        private const string SharedRepackFlowNormalSlot = "t_xml_flow_normal";
        private const string SharedRepackFlowNormal2Slot = "t_xml_flow_normal_2";
        private const string SharedRepackFlowNoiseSpeedParameter = "flow_noise_speed";
        private const string SharedRepackFlowNoiseTilingParameter = "flow_noise_tiling";
        private const string SharedRepackFlowTextureTilingParameter = "flow_texture_tiling";
        private const string SharedRepackFlowMapOutputSuffix = "flow_map";
        private const string SharedRepackFlowGuidOutputSuffix = "flow_guid";
        private const string SharedRepackReasonFlowShader =
            "the material's shader is not verified for flow-overlay repacking (";
        private const string SharedRepackReasonFlowParameter =
            "the material lacks a usable flow parameter ";
        private const string SharedRepackReasonFlowTexture =
            "the flow-map texture cannot be repacked";
        private static readonly string[] SharedRepackFlowParameters =
        [
            SharedRepackFlowNoiseSpeedParameter,
            SharedRepackFlowNoiseTilingParameter,
            SharedRepackFlowTextureTilingParameter,
        ];
        private static readonly string[] SharedRepackFlowPassThroughSlots =
        [
            SharedRepackFlowNoiseSlot,
            SharedRepackFlowNormalSlot,
            SharedRepackFlowNormal2Slot,
        ];
        // Generated from every PS variant and its FASTBIN-linked VS entrypoint by
        // Tools/ShaderChannels/verify_slot_families_uv.py. Rigid flow is deliberately excluded.
        private static readonly HashSet<string> SharedRepackFlowVerifiedShaders =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "shaders/weighted2_flow_overlay.xml.shader",
                "shaders/weighted2_flow_overlay_alpha.xml.shader",
                "shaders/weighted2_flow_overlay_alpha_blood.xml.shader",
                "shaders/weighted2_flow_overlay_blood.xml.shader",
                "shaders/weighted4_flow_overlay.xml.shader",
                "shaders/weighted4_flow_overlay_alpha.xml.shader",
                "shaders/weighted4_flow_overlay_alpha_blood.xml.shader",
                "shaders/weighted4_flow_overlay_blood.xml.shader",
            };
        // Auxiliary slots every standard character material carries, pointing at common
        // textures. Like the atlas stage (which only handles its four channels) they are left
        // untouched in the generated material and are not part of the set, closure or tuple.
        private static readonly string[] SharedRepackPassThroughSlots =
        [
            "t_xml_blood_map",
            "t_xml_dither",
            "t_xml_distortion",
            "t_xml_distortion_noise",
        ];
        private const string SharedRepackVanillaMaterialDirectory = @"variantmeshes\asset_editor\repack_materials";
        private const string SharedRepackBloodScaleParameter = "blood_uv_scale";
        // Emissive distortion copy. The scrolling emissive glow of the common character emissive
        // shaders is sampled at (UV0 + d * strength + time * speed * direction) * tiling with d
        // the t_xml_emissive_distortion texel at UV0. The distortion texture (always a shared
        // noise texture of commontextures\) is cropped per chart like an atlas slot, copied
        // into its own output, and the material's scroll parameters are rescaled so the glow keeps
        // its density and speed on the mesh. The noise never joins the signature set closure and
        // is never "replaced" (other models keep using it).
        private const string SharedRepackEmissiveDistortionSlot = "t_xml_emissive_distortion";
        private const string SharedRepackEmissiveTextureSlot = "t_xml_emissive_texture";
        private const string SharedRepackEmissiveTilingParameter = "emissive_tiling";
        private const string SharedRepackEmissiveStrengthParameter = "emissive_distort_strength";
        private const string SharedRepackEmissiveDirectionParameter = "emissive_direction";
        private const string SharedRepackEmissiveOutputSuffix = "emissive_distortion";
        private const string SharedRepackReasonEmissiveShader =
            "the material's shader is not verified for emissive repacking (";
        private const string SharedRepackReasonEmissiveParameter =
            "the material lacks emissive parameter ";
        private static readonly string[] SharedRepackEmissiveParameters =
        [
            SharedRepackEmissiveTilingParameter,
            SharedRepackEmissiveStrengthParameter,
            SharedRepackEmissiveDirectionParameter,
        ];
        // Shaders whose every pixel shader variant samples the emissive texture at exactly the chain
        // above and reads UV0 nowhere else outside the cropped atlas slots (and the blood overlay,
        // which has its own assumption). Generated by Tools/ShaderChannels/verify_emissive_uv.py from
        // the game's compiled shaders; families it cannot prove stay blocked. Lower case, forward
        // slashes, as VanillaCoverageMaterial.Shader.
        private static readonly HashSet<string> SharedRepackEmissiveVerifiedShaders =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "shaders/rigid_character_dissolve_iridescent_emissive.xml.shader",
                "shaders/rigid_character_dissolve_iridescent_emissive_alpha.xml.shader",
                "shaders/rigid_character_dissolve_iridescent_emissive_alpha_blood.xml.shader",
                "shaders/rigid_character_dissolve_iridescent_emissive_blood.xml.shader",
                "shaders/rigid_character_emissive.xml.shader",
                "shaders/rigid_character_emissive_alpha.xml.shader",
                "shaders/rigid_character_emissive_alpha_blood.xml.shader",
                "shaders/rigid_character_emissive_blood.xml.shader",
                "shaders/rigid_character_iridescent_emissive.xml.shader",
                "shaders/rigid_character_iridescent_emissive_alpha.xml.shader",
                "shaders/rigid_character_iridescent_emissive_alpha_blood.xml.shader",
                "shaders/rigid_character_iridescent_emissive_blood.xml.shader",
                "shaders/rigid_character_tentacle_emissive.xml.shader",
                "shaders/rigid_character_tentacle_emissive_blood.xml.shader",
                "shaders/rigid_daemon_prince.xml.shader",
                "shaders/rigid_daemon_prince_alpha.xml.shader",
                "shaders/rigid_emissive.xml.shader",
                "shaders/rigid_emissive_blood.xml.shader",
                "shaders/weighted2_character_alphatint_emissive.xml.shader",
                "shaders/weighted2_character_alphatint_emissive_blood.xml.shader",
                "shaders/weighted2_character_dissolve_emissive.xml.shader",
                "shaders/weighted2_character_dissolve_emissive_alpha.xml.shader",
                "shaders/weighted2_character_dissolve_emissive_alpha_blood.xml.shader",
                "shaders/weighted2_character_dissolve_emissive_blood.xml.shader",
                "shaders/weighted2_character_dissolve_iridescent_emissive.xml.shader",
                "shaders/weighted2_character_dissolve_iridescent_emissive_alpha.xml.shader",
                "shaders/weighted2_character_dissolve_iridescent_emissive_alpha_blood.xml.shader",
                "shaders/weighted2_character_dissolve_iridescent_emissive_blood.xml.shader",
                "shaders/weighted2_character_emissive.xml.shader",
                "shaders/weighted2_character_emissive_alpha.xml.shader",
                "shaders/weighted2_character_emissive_alpha_blood.xml.shader",
                "shaders/weighted2_character_emissive_blood.xml.shader",
                "shaders/weighted2_character_ethereal.xml.shader",
                "shaders/weighted2_character_iridescent_emissive.xml.shader",
                "shaders/weighted2_character_iridescent_emissive_alpha.xml.shader",
                "shaders/weighted2_character_iridescent_emissive_alpha_blood.xml.shader",
                "shaders/weighted2_character_iridescent_emissive_alphatint.xml.shader",
                "shaders/weighted2_character_iridescent_emissive_alphatint_blood.xml.shader",
                "shaders/weighted2_character_iridescent_emissive_blood.xml.shader",
                "shaders/weighted2_character_silk_emissive.xml.shader",
                "shaders/weighted2_character_silk_emissive_alpha.xml.shader",
                "shaders/weighted2_character_silk_emissive_alpha_blood.xml.shader",
                "shaders/weighted2_character_silk_emissive_blood.xml.shader",
                "shaders/weighted2_daemon_prince.xml.shader",
                "shaders/weighted2_daemon_prince_alpha.xml.shader",
                "shaders/weighted2_daemon_prince_alpha_blood.xml.shader",
                "shaders/weighted2_daemon_prince_blood.xml.shader",
                "shaders/weighted2_emissive.xml.shader",
                "shaders/weighted2_emissive_blood.xml.shader",
                "shaders/weighted4_character_dissolve_emissive.xml.shader",
                "shaders/weighted4_character_dissolve_emissive_alpha.xml.shader",
                "shaders/weighted4_character_dissolve_emissive_alpha_blood.xml.shader",
                "shaders/weighted4_character_dissolve_emissive_blood.xml.shader",
                "shaders/weighted4_character_dissolve_iridescent_emissive.xml.shader",
                "shaders/weighted4_character_dissolve_iridescent_emissive_blood.xml.shader",
                "shaders/weighted4_character_emissive.xml.shader",
                "shaders/weighted4_character_emissive_alpha.xml.shader",
                "shaders/weighted4_character_emissive_alpha_blood.xml.shader",
                "shaders/weighted4_character_emissive_blood.xml.shader",
                "shaders/weighted4_character_ethereal.xml.shader",
                "shaders/weighted4_character_iridescent_emissive.xml.shader",
                "shaders/weighted4_character_iridescent_emissive_alpha.xml.shader",
                "shaders/weighted4_character_iridescent_emissive_alpha_blood.xml.shader",
                "shaders/weighted4_character_iridescent_emissive_blood.xml.shader",
                "shaders/weighted4_character_silk_emissive.xml.shader",
                "shaders/weighted4_character_silk_emissive_alpha.xml.shader",
                "shaders/weighted4_character_silk_emissive_alpha_blood.xml.shader",
                "shaders/weighted4_character_silk_emissive_blood.xml.shader",
                "shaders/weighted4_daemon_prince.xml.shader",
                "shaders/weighted4_daemon_prince_alpha.xml.shader",
                "shaders/weighted4_daemon_prince_alpha_blood.xml.shader",
                "shaders/weighted4_daemon_prince_blood.xml.shader",
                "shaders/weighted4_emissive.xml.shader",
                "shaders/weighted4_emissive_blood.xml.shader",
            };
        // Dissolve shaders. The pixel shader samples t_xml_dissolve_map at the raw UV0 and
        // t_xml_dissolve_detail at UV0 * sp_dissolve_uv_scale (per axis, nothing else). The detail
        // texture is kept as authored (a shared common texture) and dissolve_uv_scale is divided by the
        // UV scale of the page like blood_uv_scale; the map (a shared common noise texture) is cropped as a
        // noise COPY like the emissive distortion, but needs no parameter compensation.
        private const string SharedRepackDissolveDetailSlot = "t_xml_dissolve_detail";
        private const string SharedRepackDissolveMapSlot = "t_xml_dissolve_map";
        private const string SharedRepackDissolveScaleParameter = "dissolve_uv_scale";
        private const string SharedRepackDissolveOutputSuffix = "dissolve_map";
        private const string SharedRepackReasonDissolveShader =
            "the material's shader is not verified for dissolve repacking (";
        private const string SharedRepackReasonDissolveParameter =
            "the material lacks dissolve parameter ";
        // Shaders whose every pixel shader variant (and every _alpha/_blood sibling) has the two samples
        // above and reads UV0 nowhere else outside the cropped slots. Generated by
        // Tools/ShaderChannels/verify_dissolve_uv.py from the game's compiled shaders; same format as the
        // emissive list. A dissolve+emissive shader is in the emissive list too.
        private static readonly HashSet<string> SharedRepackDissolveVerifiedShaders =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "shaders/rigid_character_dissolve.xml.shader",
                "shaders/rigid_character_dissolve_blood.xml.shader",
                "shaders/rigid_character_dissolve_iridescent_emissive.xml.shader",
                "shaders/rigid_character_dissolve_iridescent_emissive_alpha.xml.shader",
                "shaders/rigid_character_dissolve_iridescent_emissive_alpha_blood.xml.shader",
                "shaders/rigid_character_dissolve_iridescent_emissive_blood.xml.shader",
                "shaders/rigid_daemon_prince.xml.shader",
                "shaders/rigid_daemon_prince_alpha.xml.shader",
                "shaders/weighted2_character_dissolve.xml.shader",
                "shaders/weighted2_character_dissolve_alpha.xml.shader",
                "shaders/weighted2_character_dissolve_alpha_blood.xml.shader",
                "shaders/weighted2_character_dissolve_blood.xml.shader",
                "shaders/weighted2_character_dissolve_emissive.xml.shader",
                "shaders/weighted2_character_dissolve_emissive_alpha.xml.shader",
                "shaders/weighted2_character_dissolve_emissive_alpha_blood.xml.shader",
                "shaders/weighted2_character_dissolve_emissive_blood.xml.shader",
                "shaders/weighted2_character_dissolve_iridescent_emissive.xml.shader",
                "shaders/weighted2_character_dissolve_iridescent_emissive_alpha.xml.shader",
                "shaders/weighted2_character_dissolve_iridescent_emissive_alpha_blood.xml.shader",
                "shaders/weighted2_character_dissolve_iridescent_emissive_blood.xml.shader",
                "shaders/weighted2_daemon_prince.xml.shader",
                "shaders/weighted2_daemon_prince_alpha.xml.shader",
                "shaders/weighted2_daemon_prince_alpha_blood.xml.shader",
                "shaders/weighted2_daemon_prince_blood.xml.shader",
                "shaders/weighted2_dragon_dissolve.xml.shader",
                "shaders/weighted2_dragon_dissolve_blood.xml.shader",
                "shaders/weighted4_character_dissolve.xml.shader",
                "shaders/weighted4_character_dissolve_alpha.xml.shader",
                "shaders/weighted4_character_dissolve_alpha_blood.xml.shader",
                "shaders/weighted4_character_dissolve_blood.xml.shader",
                "shaders/weighted4_character_dissolve_emissive.xml.shader",
                "shaders/weighted4_character_dissolve_emissive_alpha.xml.shader",
                "shaders/weighted4_character_dissolve_emissive_alpha_blood.xml.shader",
                "shaders/weighted4_character_dissolve_emissive_blood.xml.shader",
                "shaders/weighted4_character_dissolve_iridescent_emissive.xml.shader",
                "shaders/weighted4_character_dissolve_iridescent_emissive_blood.xml.shader",
                "shaders/weighted4_daemon_prince.xml.shader",
                "shaders/weighted4_daemon_prince_alpha.xml.shader",
                "shaders/weighted4_daemon_prince_alpha_blood.xml.shader",
                "shaders/weighted4_daemon_prince_blood.xml.shader",
                "shaders/weighted4_dragon_dissolve.xml.shader",
                "shaders/weighted4_dragon_dissolve_alpha.xml.shader",
                "shaders/weighted4_dragon_dissolve_alpha_blood.xml.shader",
                "shaders/weighted4_dragon_dissolve_blood.xml.shader",
            };
        private const string SharedRepackReasonVariantCopySlot =
            "the part's WSModel bindings name different textures in a copied slot (emissive distortion, dissolve map or flow map)";
        private const string SharedRepackReasonSlotSubset =
            "a texture slot is bound by only some of the part's WSModel bindings";
        private const string SharedRepackReasonVariantDirect =
            "the part is loaded directly and its WSModel bindings name different textures";
        private const string SharedRepackReasonVariantConstant =
            "a texture variant is a uniform colour in one binding and a spatial texture in another, slot ";
        private const string SharedRepackReasonVariantLayout =
            "a texture variant differs in size, mip count or format from the layout texture, slot ";
        private const string SharedRepackReasonVariantOutside =
            "a texture variant is not a member of the plan (or the layout texture is only a copy), slot ";
        private const string SharedRepackReasonDuplicateSlot =
            "a bound material lists one texture slot more than once with different sources (which one the engine samples is unknown)";
        private const string SharedRepackReasonMixed =
            "a bound material also uses textures outside the set";
        private const string SharedRepackReasonBlockedTexture =
            "a texture cannot be repacked (not BC1/BC3/BC7, not a power of two, or an owned override of a vanilla texture)";
        private const string SharedRepackReasonAtlasChangedRigid =
            "the rigid was changed by texture atlasing";
        private const string SharedRepackReasonRigid =
            "the part's rigid is not eligible: ";
        private const string SharedRepackReasonNoCoverage =
            "the part has no coverage data";
        private const string SharedRepackReasonBadPart =
            "the part is missing from the rigid or shares its model with another part";
        private const string SharedRepackReasonEmbeddedUnreadable =
            "the embedded material is unreadable";
        private const string SharedRepackReasonEmbeddedTypePrefix = "embedded material type ";
        private const string SharedRepackReasonEmbeddedVertexFormatPrefix = "embedded vertex format ";

        // Embedded material types whose shader channels were verified (weighted_standard_* draws both).
        private static bool IsSharedRepackVerifiedEmbeddedType(ModelMaterialEnum type) =>
            type is ModelMaterialEnum.weighted or ModelMaterialEnum.default_type;

        // The vertex formats the direct-rigid shader dispatch evidence covers.
        private static bool IsSharedRepackVerifiedEmbeddedVertexFormat(VertexFormat format) =>
            format is VertexFormat.Static or VertexFormat.Weighted or VertexFormat.Cinematic;
        private const string SharedRepackReasonEmbeddedUnsupported =
            "the embedded material's sampled slots are unknown";
        private const string SharedRepackReasonEmbeddedWsMismatch =
            "embedded material textures disagree with the WSModel binding for the same part";

        private sealed class SharedRepackPart
        {
            public MeshKey Key { get; init; }
            public RmvModel? Model { get; set; }
            public List<SourceWsModelBinding> Bindings { get; } = [];
            public HashSet<string> Materials { get; } = new(StringComparer.OrdinalIgnoreCase);
            // Verified flow materials are rescaled even when flow_map is constant or copied.
            public HashSet<string> FlowCompensationMaterials { get; } = new(StringComparer.OrdinalIgnoreCase);
            // Every texture of every bound material, any slot.
            public HashSet<string> Textures { get; } = new(StringComparer.OrdinalIgnoreCase);
            // The layout map: the first (by text) of the distinct slot maps of the part's bound materials.
            public SortedDictionary<string, string> Slots { get; } = new(StringComparer.OrdinalIgnoreCase);
            // The slot map of each bound material, and the distinct maps other than Slots (texture
            // variants: same slots, other textures), in text order.
            public Dictionary<string, SortedDictionary<string, string>> MaterialSlots { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<SortedDictionary<string, string>> VariantSlots { get; } = [];
            // Slots of Slots that are copied (cropped per chart) but whose texture is not a set member
            // and is never replaced: emissive/dissolve/flow noise or a common flow map.
            public HashSet<string> CopySlots { get; } = new(StringComparer.OrdinalIgnoreCase);
            // (material, slot) pairs ignored because the material's shader never binds the slot.
            public HashSet<(string Material, string Slot)> UnreadSlots { get; } = [];
            public bool DirectEmbeddedMaterial { get; set; }
            public ModelMaterialEnum? EmbeddedMaterialType { get; set; }
            public Dictionary<TextureType, string> EmbeddedTextures { get; } = [];
            public HashSet<string> DirectConsumers { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> UnusedWsModels { get; } = new(StringComparer.OrdinalIgnoreCase);
            public string? Reason { get; set; }
            public bool GeometryChecked { get; set; }
            public double[] U { get; set; } = [];
            public double[] V { get; set; } = [];
            public int[] TileU { get; set; } = [];
            public int[] TileV { get; set; } = [];
            public bool[] Used { get; set; } = [];
            // Per vertex: the chart (or tight piece) of the ACTIVE layout of its tuple (-1 = unused).
            public int[] VertexChart { get; set; } = [];
            // The two layouts of the tuple (see SharedRepackTuple.TightCharts); null when the tuple has no
            // tight alternative.
            public int[]? MergedVertexChart { get; set; }
            public int[]? TightVertexChart { get; set; }
            // Parts with the same layout map and the same variant maps form a tuple.
            public string TupleKey => FormatSharedRepackSlotMap(Slots) +
                                      string.Concat(VariantSlots.Select(map => "#" + FormatSharedRepackSlotMap(map)));
        }

        private static string FormatSharedRepackSlotMap(IEnumerable<KeyValuePair<string, string>> map)
            => string.Join("|", map.Select(slot => $"{slot.Key}={Normalize(slot.Value)}"));

        private sealed class SharedRepackChart
        {
            public int Id { get; set; }
            public SharedRepackTuple Tuple { get; init; } = null!;
            // Cell bounding box (inclusive) in the tuple's grid.
            public int CellX0 { get; set; }
            public int CellY0 { get; set; }
            public int CellX1 { get; set; }
            public int CellY1 { get; set; }
            // Padded rectangle in the tuple's reference pixels (may reach outside the texture).
            public int X0 { get; set; }
            public int Y0 { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            // Size reserved in the layout (a multiple of the cell) and its place.
            public int LayoutWidth { get; set; }
            public int LayoutHeight { get; set; }
            public int PackX { get; set; }
            public int PackY { get; set; }
            public SharedRepackPage? Page { get; set; }
            // A tight piece (one 8-connected component of touched cells) rather than a merged chart:
            // the id of the merged chart it was part of (its pieces always share a cluster, so they
            // land on one page), else -1.
            public bool Piece { get; init; }
            public int MergedId { get; init; } = -1;
            public string Label => Piece ? $"{Tuple.Index}.p{Id}" : $"{Tuple.Index}.{Id}";
        }

        private sealed class SharedRepackTuple
        {
            public int Index { get; set; }
            public string Key { get; init; } = string.Empty;
            // The slots that are repacked (spatial). Uniform tiny textures are not in here.
            public SortedDictionary<string, string> Slots { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            // The slots of Slots that are copy slots: the part's emissive noise slots, and the slots
            // whose texture is a copy in this plan (see SharedRepackMergeContext.CopyRemoved).
            public HashSet<string> CopySlots { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            // The noise copies (emissive distortion, dissolve map) have layout rules of their own (never
            // the reference, no chart alignment); any other copy lays out like a normal slot.
            public bool IsNoiseCopySlot(string slot)
                => CopySlots.Contains(slot) && IsSharedRepackNoiseSlot(slot);
            // A copy of a texture that left this plan: not a replaced texture, but its original
            // does not count as a source either.
            public bool IsGeneralCopySlot(string slot) => CopySlots.Contains(slot) && !IsNoiseCopySlot(slot);
            // The tuple's materials get the emissive scroll parameters rescaled, which is only exact
            // for equal U and V UV scales (the group's canvases are restricted to the reference aspect).
            public bool EmissiveCompensation => CopySlots.Contains(SharedRepackEmissiveDistortionSlot);
            public bool FlowCompensation => Parts.Any(part => part.FlowCompensationMaterials.Count != 0);
            public bool RequiresEqualAxisScale => EmissiveCompensation || FlowCompensation;
            // The textures this tuple replaces (its slots minus the copied ones).
            public IEnumerable<string> MemberTextures
                => Slots.Where(pair => !CopySlots.Contains(pair.Key)).Select(pair => pair.Value);
            // Per repacked slot: the variant textures (other bindings of the parts name them instead of
            // the layout texture), distinct, in text order. They are members too.
            public SortedDictionary<string, List<string>> VariantTextures { get; } = new(StringComparer.OrdinalIgnoreCase);
            public IEnumerable<string> AllMemberTextures
                => MemberTextures.Concat(VariantTextures.Values.SelectMany(list => list));
            // Uniform tiny textures (e.g. a 4x4 mask): the generated material keeps them as authored.
            public SortedDictionary<string, string> ConstantSlots { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<SharedRepackPart> Parts { get; } = [];
            public Dictionary<string, DdsLayout> Layouts { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, (int X, int Y)> SourceExponent { get; } = new(StringComparer.OrdinalIgnoreCase);
            public int ReferenceWidth { get; set; }
            public int ReferenceHeight { get; set; }
            public int Cell { get; set; }
            // Active chart padding and alignment in reference pixels.
            public int Gutter { get; set; }
            public int Align { get; set; }
            public string PaddingVariant { get; set; } = "baseline";
            public long PaddingBaselineBytes { get; set; }
            public long PaddingSavedBytes { get; set; }
            public int MaximumPaddingVariantIndex { get; set; } = 2;
            public int GridX { get; set; }
            public int GridY { get; set; }
            // The ACTIVE layout: cell -> chart id (-1 = untouched) and the charts. It is the merged-chart
            // layout unless the group chose its tight pieces (ActivateSharedRepackLayout).
            public int[] CellChart { get; set; } = [];
            public List<SharedRepackChart> Charts { get; set; } = [];
            // Tight pieces: one chart per 8-connected component of touched cells (same one-cell gutter
            // and alignment as a merged chart, so a piece never needs more room than the chart it came
            // from). Null when the tuple has no merged component (the pieces would be the charts) or
            // no consistent assignment. MergedCharts/MergedCellChart keep the merged layout while the
            // pieces are active.
            public List<SharedRepackChart>? TightCharts { get; set; }
            public int[]? TightCellChart { get; set; }
            public List<SharedRepackChart>? MergedCharts { get; set; }
            public int[]? MergedCellChart { get; set; }
            // Why the tight pieces could not be assigned exactly (the group then keeps the merged charts).
            public string? TightInconsistent { get; set; }
            // The active layout is the tight one.
            public bool Tight { get; set; }
            // Tuples with the same signature can share one layout: same slots, formats,
            // colour spaces and per-slot size ratios, so no slot is ever resampled.
            public string GroupKey { get; set; } = string.Empty;
            // The key that also separates tuples by their foreign variants (the group key falls back to
            // it when joining them does not pay or a variant page would be bigger than its texture).
            public string StrictGroupKey { get; set; } = string.Empty;
            // Variant textures that other units load than the layout texture of their slot.
            public HashSet<string> ForeignVariantTextures { get; } = new(StringComparer.OrdinalIgnoreCase);
            // Why this tuple is left untouched (null = included).
            public string? Excluded { get; set; }
        }

        private sealed class SharedRepackSlotOutput
        {
            public string Slot { get; init; } = string.Empty;
            // The path the output is referenced by: a duplicate takes its canonical output's path.
            public string Path { get; set; } = string.Empty;
            // Set for a duplicate: the earlier output of the set plan it is guaranteed to encode identically
            // to. It is composed to verify that, then neither written nor counted.
            public SharedRepackSlotOutput? DuplicateOf { get; set; }
            // The path a duplicate had before it took its canonical output's.
            public string PlannedPath { get; set; } = string.Empty;
            public string Format { get; set; } = "BC1";
            public bool Srgb { get; set; }
            public bool Dx10 { get; set; }
            public int ExponentX { get; set; }
            public int ExponentY { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public long Bytes { get; set; }
            public byte[] Data { get; set; } = [];
            public int MipLevels { get; set; }
            // Top mip levels whose chart rectangles were copied block for block.
            public int BlockCopiedLevels { get; set; }
            // Per level and cause: which sheet blocks are verbatim source blocks and what is left to the encoder.
            internal SharedRepackBlockStats? BlockStats { get; set; }
            // How the lower levels' encoder input was produced (source mips or box filter), per cause.
            internal SharedRepackMipSeedStats? MipSeedStats { get; set; }
            public DdsLayout Template { get; set; } = null!;
            // A copy of a texture that stays (the emissive distortion noise, or a texture that left
            // the plan): it costs bytes but replaces nothing, so it is not a "replaced texture", not
            // owned by a signature set and never pruned.
            public bool IsCopy { get; init; }
            // The source textures this output replaces (all of one signature set); for a copy, the
            // texture it was cropped from.
            public List<string> Sources { get; } = [];
            // A variant page: the layout page of the slot with the charts of the tuples in here (by index)
            // cropped from this texture instead of their layout texture. Null for a layout page.
            public Dictionary<int, string>? VariantSources { get; init; }
            public bool IsVariant => VariantSources != null;
            // The texture the charts of the tuple are cropped from in this output.
            public string SourceOf(SharedRepackTuple tuple)
                => VariantSources != null && VariantSources.TryGetValue(tuple.Index, out var texture)
                    ? texture
                    : tuple.Slots[Slot];
        }

        private sealed class SharedRepackBlockCopyPlan
        {
            public required int BytesPerBlock { get; init; }
            public required SharedRepackBlockFate[][] Fates { get; init; }
            public required bool[][] Copied { get; init; }
        }

        // One output texture set: a layout group, or a page of it when the charts do not
        // fit one layout.
        private sealed class SharedRepackPage
        {
            public string Rigid { get; init; } = string.Empty;
            public string Hash { get; init; } = string.Empty;
            public string GroupKey { get; init; } = string.Empty;
            public int Index { get; init; }
            public List<SharedRepackTuple> Tuples { get; } = [];
            public List<SharedRepackChart> Charts { get; } = [];
            public List<SharedRepackPart> Parts { get; } = [];
            public List<SharedRepackSlotOutput> Outputs { get; } = [];
            // The variant pages of Outputs, by slot and texture (lower case): the page a material naming
            // that texture in the slot uses. A texture not in here uses the slot's layout page.
            public Dictionary<(string Slot, string Texture), SharedRepackSlotOutput> VariantOutputs { get; } = [];
            public int LayoutWidth { get; set; }
            public int LayoutHeight { get; set; }
            public long BytesAfter { get; set; }
            public Dictionary<string, string> MaterialMap { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, XmlDocument> MaterialDocuments { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<(XmlNode Node, string Material)> WsRepoints { get; } = [];
            public HashSet<string> WsModels { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<string> BloodScales { get; } = [];
            public List<string> DissolveScales { get; } = [];
            public List<string> EmissiveScales { get; } = [];
            public List<string> FlowScales { get; } = [];
        }

        private sealed class SharedRepackGroupInfo
        {
            public string GroupKey { get; init; } = string.Empty;
            public string Label { get; init; } = string.Empty;
            public int Tuples { get; set; }
            public int Charts { get; set; }
            public int Pages { get; set; }
            public long Before { get; set; }
            public long After { get; set; }
            public string PaddingVariant { get; set; } = "baseline";
            public long PaddingSavedBytes { get; set; }
            public bool NewlyBuilt { get; set; }
            public string? PaddingRetryFromVariant { get; set; }
            public string Status { get; set; } = "built";
            public string Sizes { get; set; } = string.Empty;
        }

        // How the page choice of one layout group went: the single-page layout, the clusters,
        // and the best split into several smaller pages that was tried.
        private sealed class SharedRepackSplitInfo
        {
            public int Clusters { get; set; }
            public bool OneCluster { get; set; }
            public bool Applied { get; set; }
            public long BeforeBytes { get; set; }
            public long SingleBytes { get; set; }
            public string SingleSize { get; set; } = string.Empty;
            public long BestBytes { get; set; }
            public string BestSizes { get; set; } = string.Empty;
            public int BestPages { get; set; }
            // Why no split was tried (one cluster, already split by the side limit).
            public string? Note { get; set; }
            public long Saved => Applied ? SingleBytes - BestBytes : 0;
            // The group's canvases were restricted to the reference aspect ratio (exact emissive
            // compensation), and what a single page would have cost without the restriction.
            public bool AspectRestricted { get; set; }
            public bool EmissiveAspectRestricted { get; set; }
            public bool FlowAspectRestricted { get; set; }
            // Tight pieces: the group had merged charts to split (Tried), the pieces gave smaller pages
            // (Used: Pieces instead of MergedCharts, saving SavedBytes), or were not assignable exactly
            // (Inconsistent, the merged layout is kept).
            public bool TightTried { get; set; }
            public bool TightUsed { get; set; }
            public bool TightInconsistent { get; set; }
            public int TightPieces { get; set; }
            public int TightMergedCharts { get; set; }
            public long TightSavedBytes { get; set; }
            public long UnrestrictedSingleBytes { get; set; }
            public string UnrestrictedSingleSize { get; set; } = string.Empty;
            public string PaddingVariant { get; set; } = "baseline";
            public long PaddingBaselineBytes { get; set; }
            public long PaddingSavedBytes { get; set; }
        }

        private sealed class SharedRepackSetPlan
        {
            public Dictionary<string, SharedRepackSplitInfo> Splits { get; } = new(StringComparer.Ordinal);
            public List<SharedRepackPage> Pages { get; } = [];
            public List<SharedRepackGroupInfo> Groups { get; } = [];
            public List<SharedRepackTuple> Tuples { get; } = [];
            public Dictionary<string, long> Replaced { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, bool> ReplacedOwned { get; } = new(StringComparer.OrdinalIgnoreCase);
            // Layout groups of the final layout whose canvases were restricted to the reference aspect
            // (exact emissive compensation), their tuples, and what the restriction cost on a single page.
            public int AspectGroups { get; set; }
            public int AspectTuples { get; set; }
            public long AspectExtraBytes { get; set; }
            public int FlowAspectGroups { get; set; }
            public int FlowAspectTuples { get; set; }
            public long FlowAspectExtraBytes { get; set; }
            public HashSet<string> FailedPaddingGroupKeys { get; } = new(StringComparer.Ordinal);
            // Outputs identical to an earlier output of the plan (see DeduplicateSharedRepackOutputs).
            public int DeduplicatedOutputs { get; set; }
            public long DeduplicatedBytes { get; set; }
        }

        private sealed record SharedRepackSample(
            string Slot,
            string Texture,
            int Level,
            int SourceX,
            int SourceY,
            string SourceRgba,
            int NewX,
            int NewY,
            string NewRgba,
            string Chart,
            int EdgeDistance,
            int Error)
        {
            public override string ToString()
                => $"{Slot} {Texture} mip {Level}: source texel ({SourceX},{SourceY}) rgba {SourceRgba}, " +
                   $"new texel ({NewX},{NewY}) rgba {NewRgba}, chart {Chart}, {EdgeDistance} texel(s) from the " +
                   $"chart edge, error {Error}";
        }

        private sealed class SharedRepackPixelStats
        {
            public long Samples { get; set; }
            // Top-level samples taken from variant pages (a part of Samples).
            public long VariantSamples { get; set; }
            public long Skipped { get; set; }
            public double TopMax { get; set; }
            public double TopSum { get; set; }
            public long TopCount { get; set; }
            public double LevelMax { get; set; }
            public double LevelSum { get; set; }
            public long LevelCount { get; set; }
            public long LevelSamples { get; set; }
            public long EdgeSkipped { get; set; }
            // Tight pieces on the checked pages, and how many of them were sampled (all, or the report says so).
            public long Pieces { get; set; }
            public long PiecesSampled { get; set; }
            public double LevelP99 { get; set; }
            public double LevelOutlierShare { get; set; }
            public List<SharedRepackSample> Worst { get; } = [];
            public double TopMean => TopCount == 0 ? 0 : TopSum / TopCount;
            public double LevelMean => LevelCount == 0 ? 0 : LevelSum / LevelCount;
        }

        // One rigid of a signature set: why it cannot be rewritten (null = eligible), the parts
        // with a complete WSModel binding and the WSModels bound to it.
        private sealed class SharedRepackSetRigid
        {
            public string Path { get; init; } = string.Empty;
            public RmvFile? Rigid { get; init; }
            public string? Reason { get; init; }
            public List<(string Rigid, int Lod, int Part)> Parts { get; init; } = [];
            public HashSet<string> WsModels { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            // A rigid the cross-rigid writer generated (lives in memory only).
            public bool Generated { get; init; }
        }

        // What one planning attempt of a seed set may do and what it ended up with. With Allow
        // set, a part whose material also uses textures of an unprocessed signature set pulls
        // that set into the plan (Joined, Component); without it the seed is planned alone.
        private sealed class SharedRepackMergeContext
        {
            public int Seed { get; init; }
            public bool Allow { get; init; }
            public List<UnitRepackSetDecision> Decisions { get; init; } = [];
            public Dictionary<UnitRepackSignatureSet, int> IndexOfSet { get; init; } = [];
            // Whether the set at this decision index may still join (not processed, not taken).
            public Func<int, bool> IsAvailable { get; init; } = _ => false;
            public Func<string, SharedRepackSetRigid> MakeRigid { get; init; } = null!;
            // A texture's coverage entry at the density the repack builds it (its early
            // downscale scale, else as authored).
            public Func<VanillaCoverageTexture, VanillaCoverageTexture> Effective { get; init; } = entry => entry;
            // Parts of the rigids the cross-rigid writer generated, with the textures of their
            // materials: users the pre-pass sets do not list. They join a plan like source parts.
            public List<(string Rigid, int Lod, int Part, HashSet<string> Textures)> GeneratedUsers { get; init; } = [];
            public List<SharedRepackSetRigid> Rigids { get; } = [];
            public List<UnitRepackSetDecision> Component { get; } = [];
            public List<int> Joined { get; } = [];
            public bool CapHit { get; set; }
            // The cap that was hit was the one for generated rigids.
            public bool GeneratedCapHit { get; set; }
            // Why each texture of the plan left it while closing (first cause wins), with its
            // chain bytes; and the chain bytes of every texture the plan took in.
            public Dictionary<string, (string Reason, long Bytes)> RemovedTextures { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public long PlanTextureBytes { get; set; }
            // Page waste of the layout groups of the plan's final layout pass, and of the groups
            // left untouched because their pages did not shrink enough.
            public List<SharedRepackGroupWaste> GroupWaste { get; } = [];
            // Report only: the root cause behind each texture of RemovedTextures (first wins). For a
            // part pulled out by a texture outside the set, why that texture was outside.
            public Dictionary<string, SharedRepackClosureRoot> RemovedRoots { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            // The closure's last re-estimate (chain bytes of the members, and their estimated crop).
            public bool ClosureEstimated { get; set; }
            public long ClosureToday { get; set; }
            public long ClosureEstimate { get; set; }
            // Report-only what-if: outside textures are cropped into the set as copies (the
            // originals stay); planning stops at the closure's first re-estimate.
            public bool CopyOutside { get; init; }
            public SharedRepackCopyWhatIf? WhatIf { get; set; }
            // Real copies: a texture that left this plan only because a dropped part still needs the
            // original is cropped into the pages as a copy for the parts that stay (the original is
            // not replaced). CopyRescuable (normal plan): a part was dropped only for such textures.
            public bool CopyRemoved { get; init; }
            public bool CopyRescuable { get; set; }
            // Copy plan: the copied textures with why each left the plan.
            public Dictionary<string, string> CopiedTextures { get; } = new(StringComparer.OrdinalIgnoreCase);
            // The set was planned both ways; the normal plan's saving (null: it failed) and the copy
            // plan's, and whether the copy plan was the one built.
            public bool PlannedBothWays { get; set; }
            public long? NormalSaving { get; set; }
            public long CopySaving { get; set; }
            public bool CopyWon { get; set; }
            // Why the copy plan failed (null: it was planned).
            public string? CopyFailure { get; set; }
            // Why the merged attempt failed, on the alone retry's context (report only).
            public string? FallbackReason { get; set; }
            // Report only, on the alone retry's context: the failed merged attempt, its failure and all
            // of its closure notes.
            public SharedRepackMergeContext? FailedMerged { get; set; }
            public string? FailedMergedFailure { get; set; }
            public List<string> FailedMergedNotes { get; } = [];
        }

        private sealed record SharedRepackClosureRoot(string Category, string? OtherSet);

        // Saving = Today - Estimate (members' chain bytes against the crop of members and copies);
        // CopyBytes is what the copies add to the estimate.
        private sealed record SharedRepackCopyWhatIf(
            long Today,
            long Estimate,
            int Copies,
            long CopyBytes,
            string? Stop)
        {
            public long Saving => Today - Estimate;
        }

        // One layout group's pages in output bytes: what the touched cells, the padded and aligned
        // chart rectangles, and the packing trimmed to its used width and height would take
        // at the pages' bytes per reference pixel, against the power-of-two pages and the sources.
        private sealed record SharedRepackGroupWaste(
            bool Gated,
            long SourceBytes,
            long UsedBytes,
            long ChartBytes,
            long TrimmedBytes,
            long PageBytes,
            SharedRepackGroupSim? Sim = null,
            SharedRepackSplitInfo? Split = null,
            string PaddingVariant = "baseline",
            long PaddingSavedBytes = 0,
            bool NewlyBuilt = false);

        private sealed record SharedRepackPaddingVariant(
            string Name,
            IReadOnlyDictionary<SharedRepackTuple, (int Gutter, int Align)> Tuples);

        private sealed record SharedRepackGroupRun(
            List<SharedRepackPage> Pages,
            SharedRepackSplitInfo Split,
            SharedRepackTuple? ExcludeTuple,
            string? ExcludeReason);

        private sealed class SharedRepackBuiltSet
        {
            // The sets that joined the seed's plan (label, textures, units), and, when a merged
            // attempt failed first, why.
            public List<(string Label, int Textures, int Units)> MergedSets { get; } = [];
            public List<SharedRepackSplitInfo> Splits { get; } = [];
            public string? FallbackNote { get; set; }
            // The first rewritten rigid in ordinal order, and every rewritten rigid with its parts.
            public string Rigid { get; init; } = string.Empty;
            public List<(string Rigid, int Parts)> Rigids { get; } = [];
            public List<(string Rigid, int Lod, int Part)> FlowMapAtlasUsers { get; } = [];
            public List<(string Rigid, int Parts, string[] Consumers, string[] UnusedWsModels)> EmbeddedRigids { get; } = [];
            public List<(string Rigid, int Parts, string[] WsModels)> UnusedWsModels { get; } = [];
            public string Label { get; init; } = string.Empty;
            public int Units { get; init; }
            public bool? HasCharacter { get; init; }
            public long EstimatedSaving { get; init; }
            public List<(string Path, bool Owned, long Bytes)> Replaced { get; } = [];
            public long Before { get; set; }
            public long After { get; set; }
            public List<SharedRepackSlotOutput> Outputs { get; } = [];
            // Duplicate outputs that were not written, and their bytes; the texture paths the rewritten
            // direct rigids' embedded materials name.
            public int DeduplicatedOutputs { get; set; }
            public long DeduplicatedBytes { get; set; }
            // Variant pages written (not deduplicated ones), their bytes, the parts with texture variants and
            // the top-level samples the pixel check took from variant pages.
            public int VariantOutputs { get; set; }
            public long VariantBytes { get; set; }
            public int VariantParts { get; set; }
            public long VariantSamples { get; set; }
            public HashSet<string> EmbeddedTexturePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<SharedRepackGroupInfo> Groups { get; } = [];
            public int Tuples { get; set; }
            public int Charts { get; set; }
            public int Parts { get; set; }
            public SortedDictionary<string, HashSet<string>> UnreadSlotMaterials { get; } = new(StringComparer.Ordinal);
            public int Pages { get; set; }
            public long UpsampleOverheadBytes { get; set; }
            public long Samples { get; set; }
            public double MaxError { get; set; }
            public double MeanError { get; set; }
            public double LevelMaxError { get; set; }
            public double LevelMeanError { get; set; }
            public double LevelP99Error { get; set; }
            public double LevelOutlierShare { get; set; }
            public long LevelSamples { get; set; }
            public long SkippedSamples { get; set; }
            public long EdgeSkippedSamples { get; set; }
            public long Pieces { get; set; }
            public long PiecesSampled { get; set; }
            public int LayoutWidth { get; set; }
            public int LayoutHeight { get; set; }
            public List<string> Materials { get; } = [];
            public List<string> BloodScales { get; } = [];
            public List<string> DissolveScales { get; } = [];
            public List<string> EmissiveScales { get; } = [];
            public List<string> FlowScales { get; } = [];
            // Parts moved with a copied emissive distortion slot, and the bytes of the copies.
            public int EmissiveParts { get; set; }
            public int EmissiveCopies { get; set; }
            public long EmissiveCopyBytes { get; set; }
            // The same for the dissolve map copy.
            public int DissolveParts { get; set; }
            public int DissolveCopies { get; set; }
            public long DissolveCopyBytes { get; set; }
            public int FlowCompensationParts { get; set; }
            public int FlowMapAtlases { get; set; }
            public long FlowMapAtlasBytes { get; set; }
            public int FlowMapCopies { get; set; }
            public long FlowMapCopyBytes { get; set; }
            public int FlowGuidCopies { get; set; }
            public long FlowGuidCopyBytes { get; set; }
            // Copies of textures that left the plan (not the emissive noise): texture, slot, why it
            // left, output bytes; and, when the copy plan was built, what the normal plan saved
            // (null: it failed, so the set is built only thanks to the copies).
            public List<(string Texture, string Slot, string Why, long Bytes)> RemovedCopies { get; } = [];
            public bool CopyWon { get; set; }
            public long? NormalSaving { get; set; }
            public long CopyGain { get; set; }
            public List<SharedRepackSample> Worst { get; } = [];
        }

        // Funnel buckets of SharedRepackSetOutcome, in report order.
        private const string SharedRepackBucketBelow = "below the threshold";
        private const string SharedRepackBucketMerged = "merged into another set's plan";
        private const string SharedRepackBucketIneligible = "every rigid not eligible";
        private const string SharedRepackBucketClosure = "dropped by closure";
        private const string SharedRepackBucketBuild = "not buildable";
        private const string SharedRepackBucketBytes = "failed the actual-bytes gate";
        private const string SharedRepackBucketPixel = "failed the pixel check";
        private const string SharedRepackBucketBuilt = "built";

        private sealed record SharedRepackSetOutcome(
            string Label,
            string Bucket,
            long Estimate,
            string? Reason,
            long? Actual = null,
            string? Plan = null,
            SharedRepackMergeContext? Context = null);

        private sealed record SharedRepackDirectRigidOutcome(
            string Rigid,
            string[] Consumers,
            string[] Sets,
            string Bucket,
            string Detail);

        private sealed class SharedTextureRepackResult
        {
            public long MinimumSavingBytes { get; set; } = SharedRepackMinimumSavingBytes;
            public long MinimumGroupSavingBytes { get; set; } = SharedRepackMinimumGroupSavingBytes;
            public int MaximumSide { get; set; } = SharedRepackMaximumSide;
            public TimeSpan Elapsed { get; set; }
            // Variant pages of the built sets (see SharedRepackBuiltSet), summed.
            public int VariantOutputs { get; set; }
            public long VariantBytes { get; set; }
            public int VariantParts { get; set; }
            public long VariantSamples { get; set; }
            // Stopwatch ticks spent in the report-only packing simulation.
            public long PackingSimTicks { get; set; }
            public string? Failure { get; set; }
            public string? UnavailableReason { get; set; }

            // The funnel: every considered set ends in exactly one of the buckets below.
            public int SetsConsidered { get; set; }
            public int BelowThreshold { get; set; }
            public int RigidNotEligible { get; set; }
            // Rigids of considered sets that were left out because texture atlasing changed them,
            // and the atlased parts that no longer count as users of the set's textures.
            public HashSet<string> RigidsChangedByAtlasing { get; } = new(StringComparer.OrdinalIgnoreCase);
            public int AtlasedUsersIgnored { get; set; }
            // Source textures built at their early downscale scale (the repack's density follows
            // the downscale option), with the scale.
            public Dictionary<string, double> ScaledSourceTextures { get; } = new(StringComparer.OrdinalIgnoreCase);
            // Rigids of considered sets that cross-rigid merging changed (the writer relied on
            // them) or generated; they are rewritten like any other rigid.
            public HashSet<string> CrossRigidSourceRigidsConsidered { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> CrossRigidGeneratedRigidsConsidered { get; } = new(StringComparer.OrdinalIgnoreCase);
            public int MergedIntoOther { get; set; }
            public int DroppedByClosure { get; set; }
            public int NotBuildable { get; set; }
            public int FailedActualBytes { get; set; }
            public int FailedPixelCheck { get; set; }
            public int PaddingPixelRetryAttempts { get; set; }
            // Tuples excluded because a planned size broke the sizing invariant (should stay 0).
            public int SizingErrors { get; set; }
            // Emissive distortion copy: parts moved with a copied slot, copies written (count, bytes),
            // and layout groups whose canvases were restricted to the reference aspect ratio, those
            // that then failed the group gate although they would have paid without the restriction.
            public int EmissiveParts { get; set; }
            // Slots of materials in built sets that were ignored because the shader never binds them: slot -> materials.
            public SortedDictionary<string, HashSet<string>> UnreadSlotMaterials { get; } = new(StringComparer.Ordinal);
            public int EmissiveCopies { get; set; }
            public long EmissiveCopyBytes { get; set; }
            public int EmissiveMaterialsRescaled { get; set; }
            // Dissolve (map copy + dissolve_uv_scale): parts moved with a copied dissolve map, copies
            // written (count, bytes), materials with a divided dissolve_uv_scale.
            public int DissolveParts { get; set; }
            public int DissolveCopies { get; set; }
            public long DissolveCopyBytes { get; set; }
            public int DissolveMaterialsRescaled { get; set; }
            public int FlowCompensationParts { get; set; }
            public int FlowMapAtlases { get; set; }
            public long FlowMapAtlasBytes { get; set; }
            public int FlowMapCopies { get; set; }
            public long FlowMapCopyBytes { get; set; }
            public int FlowGuidCopies { get; set; }
            public long FlowGuidCopyBytes { get; set; }
            public int FlowMaterialsRescaled { get; set; }
            public int FlowAspectGroups { get; set; }
            public int FlowAspectTuples { get; set; }
            public long FlowAspectExtraBytes { get; set; }
            public HashSet<string> FlowAspectLostGroupKeys { get; } = new(StringComparer.Ordinal);
            public HashSet<string> FlowAspectLostTupleKeys { get; } = new(StringComparer.Ordinal);
            public int FlowAspectLostGroups => FlowAspectLostGroupKeys.Count;
            public int FlowAspectLostTuples => FlowAspectLostTupleKeys.Count;
            public int EmissiveAspectGroups { get; set; }
            public int EmissiveAspectTuples { get; set; }
            // Groups (and tuples) the restriction alone left untouched (they would have paid without it),
            // over every planning attempt (a retry of the same group counts once).
            public HashSet<string> EmissiveAspectLostGroupKeys { get; } = new(StringComparer.Ordinal);
            public HashSet<string> EmissiveAspectLostTupleKeys { get; } = new(StringComparer.Ordinal);
            public int EmissiveAspectLostGroups => EmissiveAspectLostGroupKeys.Count;
            public int EmissiveAspectLostTuples => EmissiveAspectLostTupleKeys.Count;
            public long EmissiveAspectExtraBytes { get; set; }
            // Merged plans: attempts with at least one joined set, how they ended, the largest
            // one tried and the plans that hit the cap.
            public int MergePlansTried { get; set; }
            public int MergePlansBuilt { get; set; }
            public int MergePlansFellBack { get; set; }
            public int MergeCapHits { get; set; }
            public int MergeGeneratedCapHits { get; set; }
            public int LargestMergeSets { get; set; }
            public int LargestMergeRigids { get; set; }
            public int Built => BuiltSets.Count;

            public List<(string Label, string Reason)> Skips { get; } = [];
            // Every considered set's funnel bucket with its estimated saving, so the report can say
            // where the estimate goes in bytes. Actual is the built plan's saving (seed sets only).
            public List<SharedRepackSetOutcome> Outcomes { get; } = [];
            public Dictionary<string, int> PartDropReasons { get; } = new(StringComparer.Ordinal);
            // Per set: which texture pulled a part's material outside the set, and who else uses it.
            public List<(string Label, string Note)> ClosureNotes { get; } = [];
            public List<(string Label, string Detail)> PixelFailures { get; } = [];
            public List<(string Label, string Detail)> SizingLog { get; } = [];
            public List<SharedRepackBuiltSet> BuiltSets { get; } = [];
            public List<string> ValidationErrors { get; } = [];
            public HashSet<string> RepackedRigids { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> EmbeddedRewrittenRigids { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<(string Rigid, int Lod, int Part, TextureType Type)> EmbeddedRewrittenPaths { get; } = [];
            // Embedded material type of every rewritten direct part.
            public Dictionary<(string Rigid, int Lod, int Part), ModelMaterialEnum> EmbeddedRewrittenPartTypes { get; } = [];
            public List<(string Label, string Detail)> DirectEmbeddedDrops { get; } = [];
            public List<SharedRepackDirectRigidOutcome> DirectRigidOutcomes { get; } = [];
            // Considered direct rigids the source pack does not own (or that override vanilla), by reason.
            public Dictionary<string, int> DirectRigidsNotOwned { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, Dictionary<string, int>> DirectEmbeddedDropReasonsByRigid { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public List<string> GeneratedTextures { get; } = [];
            public List<string> GeneratedMaterials { get; } = [];
            // Duplicate texture pages of built sets that were not written, and their bytes.
            public int DeduplicatedOutputs { get; set; }
            public long DeduplicatedBytes { get; set; }
            public long Samples { get; set; }
            public double MaxError { get; set; }
            public double ErrorSum { get; set; }
            public long ErrorCount { get; set; }
            // Filled after pruning: owned textures that were replaced.
            public int OwnedReplaced { get; set; }
            public List<string> OwnedPruned { get; } = [];
            public List<string> OwnedStillPresent { get; } = [];
            public bool Finalized { get; set; }
        }

        private sealed class SharedRepackAttemptReportSnapshot
        {
            private readonly int _atlasedUsersIgnored;
            private readonly Dictionary<string, int> _partDropReasons;
            private readonly List<(string Label, string Note)> _closureNotes;
            private readonly List<(string Label, string Detail)> _directEmbeddedDrops;
            private readonly Dictionary<string, Dictionary<string, int>> _directEmbeddedDropReasonsByRigid;
            private readonly int _sizingErrors;
            private readonly List<(string Label, string Detail)> _sizingLog;
            private readonly HashSet<string> _emissiveAspectLostGroupKeys;
            private readonly HashSet<string> _emissiveAspectLostTupleKeys;
            private readonly HashSet<string> _flowAspectLostGroupKeys;
            private readonly HashSet<string> _flowAspectLostTupleKeys;
            private readonly long _packingSimTicks;
            private readonly List<(string Label, string Detail)> _pixelFailures;

            private SharedRepackAttemptReportSnapshot(SharedTextureRepackResult result)
            {
                _atlasedUsersIgnored = result.AtlasedUsersIgnored;
                _partDropReasons = new Dictionary<string, int>(result.PartDropReasons, StringComparer.Ordinal);
                _closureNotes = result.ClosureNotes.ToList();
                _directEmbeddedDrops = result.DirectEmbeddedDrops.ToList();
                _directEmbeddedDropReasonsByRigid = result.DirectEmbeddedDropReasonsByRigid
                    .ToDictionary(
                        pair => pair.Key,
                        pair => new Dictionary<string, int>(pair.Value, StringComparer.Ordinal),
                        StringComparer.OrdinalIgnoreCase);
                _sizingErrors = result.SizingErrors;
                _sizingLog = result.SizingLog.ToList();
                _emissiveAspectLostGroupKeys = result.EmissiveAspectLostGroupKeys.ToHashSet(StringComparer.Ordinal);
                _emissiveAspectLostTupleKeys = result.EmissiveAspectLostTupleKeys.ToHashSet(StringComparer.Ordinal);
                _flowAspectLostGroupKeys = result.FlowAspectLostGroupKeys.ToHashSet(StringComparer.Ordinal);
                _flowAspectLostTupleKeys = result.FlowAspectLostTupleKeys.ToHashSet(StringComparer.Ordinal);
                _packingSimTicks = result.PackingSimTicks;
                _pixelFailures = result.PixelFailures.ToList();
            }

            public static SharedRepackAttemptReportSnapshot Capture(SharedTextureRepackResult result) => new(result);

            public void Restore(SharedTextureRepackResult result)
            {
                result.AtlasedUsersIgnored = _atlasedUsersIgnored;
                result.PartDropReasons.Clear();
                foreach (var (reason, count) in _partDropReasons)
                    result.PartDropReasons[reason] = count;
                result.ClosureNotes.Clear();
                result.ClosureNotes.AddRange(_closureNotes);
                result.DirectEmbeddedDrops.Clear();
                result.DirectEmbeddedDrops.AddRange(_directEmbeddedDrops);
                result.DirectEmbeddedDropReasonsByRigid.Clear();
                foreach (var (rigid, reasons) in _directEmbeddedDropReasonsByRigid)
                    result.DirectEmbeddedDropReasonsByRigid[rigid] = new(reasons, StringComparer.Ordinal);
                result.SizingErrors = _sizingErrors;
                result.SizingLog.Clear();
                result.SizingLog.AddRange(_sizingLog);
                result.EmissiveAspectLostGroupKeys.Clear();
                result.EmissiveAspectLostGroupKeys.UnionWith(_emissiveAspectLostGroupKeys);
                result.EmissiveAspectLostTupleKeys.Clear();
                result.EmissiveAspectLostTupleKeys.UnionWith(_emissiveAspectLostTupleKeys);
                result.FlowAspectLostGroupKeys.Clear();
                result.FlowAspectLostGroupKeys.UnionWith(_flowAspectLostGroupKeys);
                result.FlowAspectLostTupleKeys.Clear();
                result.FlowAspectLostTupleKeys.UnionWith(_flowAspectLostTupleKeys);
                result.PackingSimTicks = _packingSimTicks;
                result.PixelFailures.Clear();
                result.PixelFailures.AddRange(_pixelFailures);
            }
        }

        // The option is ignored (not an error) in cross-rigid-only mode, which promises never to
        // write textures. Texture atlasing and cross-rigid merges are allowed: they run first.
        // Rigids atlasing changed are left out of the repack (AtlasChangedRigids). The rigids
        // cross-rigid merging relied on and the rigids it generated are rewritten like any other
        // rigid: every one of them is a user of the set's textures.
        private static bool ResolveSharedTextureRepack(
            bool requested,
            bool buildAtlases,
            bool crossRigidMerges,
            bool crossRigidOnly)
            => requested && !crossRigidOnly;

        // ---------------------------------------------------------------------------
        // Pure geometry and layout helpers.
        // ---------------------------------------------------------------------------

        private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

        private static int Log2Exact(int value) => System.Numerics.BitOperations.Log2((uint)value);

        // value x 2^exponent; exact whenever a negative exponent divides the value.
        private static int GreatestCommonDivisor(int left, int right)
        {
            while (right != 0)
                (left, right) = (right, left % right);
            return Math.Max(1, left);
        }

        private static int ScaleByExponent(int value, int exponent)
            => exponent >= 0 ? value << exponent : value >> -exponent;

        private static int RoundUpTo(int value, int multiple)
            => (value + multiple - 1) / multiple * multiple;

        private static int NextPowerOfTwo(int value)
        {
            var result = 1;
            while (result < value)
                result <<= 1;
            return result;
        }

        // Non-wrapping conservative rasterisation into a gx x gy cell grid; coordinates are
        // tile-local UVs in [0, 1]. Degenerate triangles mark the cells their edges touch.
        private static void MarkSharedRepackTriangle(
            bool[] grid,
            int gx,
            int gy,
            double ax, double ay,
            double bx, double by,
            double cx, double cy)
            => VisitSharedRepackTriangleRows(
                gx, gy, ax, ay, bx, by, cx, cy,
                (row, firstCell, lastCell) =>
                {
                    for (var x = firstCell; x <= lastCell; x++)
                        grid[row * gx + x] = true;
                });

        // The row spans (row, first cell, last cell) the conservative rasterisation marks for a
        // triangle: the one definition of "the cells a triangle touches", used to mark the grid and
        // to read back which component a triangle belongs to.
        private static void VisitSharedRepackTriangleRows(
            int gx,
            int gy,
            double ax, double ay,
            double bx, double by,
            double cx, double cy,
            Action<int, int, int> visit)
        {
            Span<double> triangle = stackalloc double[6]
            {
                ax * gx, ay * gy, bx * gx, by * gy, cx * gx, cy * gy,
            };
            Span<double> bufferA = stackalloc double[16];
            Span<double> bufferB = stackalloc double[16];
            var minV = Math.Min(ay, Math.Min(by, cy)) * gy;
            var maxV = Math.Max(ay, Math.Max(by, cy)) * gy;
            var firstRow = Math.Clamp((int)Math.Floor(minV), 0, gy - 1);
            var lastRow = Math.Clamp(Math.Max(firstRow, (int)Math.Ceiling(maxV) - 1), firstRow, gy - 1);
            for (var row = firstRow; row <= lastRow; row++)
            {
                var count = ClipVanillaCoveragePolygon(triangle, 3, bufferA, true, row);
                if (count == 0)
                    continue;

                count = ClipVanillaCoveragePolygon(bufferA, count, bufferB, false, row + 1);
                if (count == 0)
                    continue;

                var rowMinX = double.MaxValue;
                var rowMaxX = double.MinValue;
                for (var index = 0; index < count; index++)
                {
                    rowMinX = Math.Min(rowMinX, bufferB[index * 2]);
                    rowMaxX = Math.Max(rowMaxX, bufferB[index * 2]);
                }

                var firstCell = Math.Clamp((int)Math.Floor(rowMinX), 0, gx - 1);
                var lastCell = Math.Clamp(Math.Max(firstCell, (int)Math.Ceiling(rowMaxX) - 1), firstCell, gx - 1);
                visit(row, firstCell, lastCell);
            }
        }

        // 8-connected components of the marked cells: label per cell (-1 = unmarked) and the
        // inclusive cell bounding box of every component.
        private static List<(int X0, int Y0, int X1, int Y1)> FindSharedRepackComponents(
            bool[] grid,
            int gx,
            int gy,
            out int[] label)
        {
            label = new int[grid.Length];
            Array.Fill(label, -1);
            var boxes = new List<(int X0, int Y0, int X1, int Y1)>();
            var stack = new Stack<int>();
            for (var start = 0; start < grid.Length; start++)
            {
                if (!grid[start] || label[start] != -1)
                    continue;

                var id = boxes.Count;
                var box = (X0: int.MaxValue, Y0: int.MaxValue, X1: int.MinValue, Y1: int.MinValue);
                label[start] = id;
                stack.Push(start);
                while (stack.Count != 0)
                {
                    var cell = stack.Pop();
                    var x = cell % gx;
                    var y = cell / gx;
                    box = (Math.Min(box.X0, x), Math.Min(box.Y0, y), Math.Max(box.X1, x), Math.Max(box.Y1, y));
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var nx = x + dx;
                            var ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= gx || ny >= gy)
                                continue;

                            var neighbour = ny * gx + nx;
                            if (!grid[neighbour] || label[neighbour] != -1)
                                continue;

                            label[neighbour] = id;
                            stack.Push(neighbour);
                        }
                    }
                }

                boxes.Add(box);
            }

            return boxes;
        }

        // Smallest power-of-two canvas (by area, then squarest, then wider) the packer fits all
        // rectangles into, within the given limits. Positions are multiples of the alignment.
        private static bool TryPackSharedRepackLayout(
            IReadOnlyList<(int Width, int Height)> sizes,
            int maximumWidth,
            int maximumHeight,
            out int canvasWidth,
            out int canvasHeight,
            out (int X, int Y)[] positions)
            => TryPackSharedRepackLayoutOnCanvases(
                sizes, maximumWidth, maximumHeight, out canvasWidth, out canvasHeight, out positions, null);

        // As above, but only canvases the predicate accepts are tried (null: every power-of-two
        // canvas). The packer is the best of the original shelf (rows, tallest first), MaxRects
        // (short side and area fit) and skyline over several orders, taken per canvas in that order
        // so a canvas the shelf fits keeps the shelf positions; a smaller canvas wins over all of
        // them. A solver keeps the operation budget and the answers of one layout group.
        private static bool TryPackSharedRepackLayoutOnCanvases(
            IReadOnlyList<(int Width, int Height)> sizes,
            int maximumWidth,
            int maximumHeight,
            out int canvasWidth,
            out int canvasHeight,
            out (int X, int Y)[] positions,
            Func<int, int, bool>? canvasAllowed,
            SharedRepackLayoutSolver? solver = null)
        {
            solver ??= CreateSharedRepackLayoutSolver(0);
            return solver.TryPack(
                sizes, maximumWidth, maximumHeight, canvasAllowed, out canvasWidth, out canvasHeight, out positions);
        }

        // unit: the chart alignment (0: derived from the rectangles).
        private static SharedRepackLayoutSolver CreateSharedRepackLayoutSolver(int unit)
            => new(SharedRepackPackerKind.Best, unit, 1, new SharedRepackSimBudget(SharedRepackLayoutOperationBudget));

        // First failing property of a texture the repack could not copy exactly, else null.
        private static string? GetSharedRepackBlockedTextureReason(DdsLayout? layout)
        {
            if (layout == null)
                return "header unreadable";
            if (layout.Format is not ("BC1" or "BC3" or "BC7"))
                return $"{layout.Format} cannot be re-encoded";
            if (!IsPowerOfTwo(layout.Width) || !IsPowerOfTwo(layout.Height) ||
                layout.Width < 4 || layout.Height < 4)
            {
                return $"{layout.Width}x{layout.Height} is not a power of two of at least 4";
            }

            return null;
        }

        private static byte[] GetSharedRepackNeutralPixel(string slot)
        {
            // BGRA. Only ever visible as bleed next to a chart; materials of a tuple without
            // the slot do not reference the slot's output.
            if (slot.Equals("t_xml_normal", StringComparison.OrdinalIgnoreCase))
                return [255, 128, 128, 255];
            if (slot.Equals("t_xml_mask", StringComparison.OrdinalIgnoreCase))
                return [0, 0, 0, 0];
            if (slot.Equals("t_xml_base_colour", StringComparison.OrdinalIgnoreCase))
                return [128, 128, 128, 255];
            return [0, 0, 0, 255];
        }

        // BGRA canvas -> BC1/BC3/BC7 DDS with a complete mip chain.
        private static bool TryEncodeSharedRepackDds(
            byte[] bgra,
            int width,
            int height,
            string format,
            bool srgb,
            bool dx10,
            out byte[] result,
            out string error)
        {
            result = [];
            error = string.Empty;
            var uncompressed = srgb
                ? DXGI_FORMAT.B8G8R8A8_UNORM_SRGB
                : DXGI_FORMAT.B8G8R8A8_UNORM;
            var compressed = format switch
            {
                "BC1" => srgb ? DXGI_FORMAT.BC1_UNORM_SRGB : DXGI_FORMAT.BC1_UNORM,
                "BC3" => srgb ? DXGI_FORMAT.BC3_UNORM_SRGB : DXGI_FORMAT.BC3_UNORM,
                "BC7" => srgb ? DXGI_FORMAT.BC7_UNORM_SRGB : DXGI_FORMAT.BC7_UNORM,
                _ => DXGI_FORMAT.UNKNOWN,
            };
            if (compressed == DXGI_FORMAT.UNKNOWN)
            {
                error = $"{format} cannot be encoded";
                return false;
            }

            try
            {
                using var full = TexHelper.Instance.Initialize2D(
                    uncompressed, width, height, 1, 1, CP_FLAGS.NONE);
                var image = full.GetImage(0, 0, 0);
                var rowBytes = width * 4;
                var pitch = checked((int)image.RowPitch);
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(
                        bgra,
                        y * rowBytes,
                        IntPtr.Add(image.Pixels, y * pitch),
                        rowBytes);
                }

                using var withMips = full.GenerateMipMaps(TEX_FILTER_FLAGS.DEFAULT, 0);
                if (DdsEncodeCapture.Enabled)
                {
                    DdsEncodeCapture.Record(
                        "TryEncodeSharedRepackDds", full, null, withMips, compressed,
                        TEX_COMPRESS_FLAGS.DEFAULT, 0.5f,
                        "GenerateMipMaps(DEFAULT, levels=0)");
                }

                using var packed = withMips.Compress(compressed, TEX_COMPRESS_FLAGS.DEFAULT, 0.5f);
                using var stream = packed.SaveToDDSMemory(
                    dx10 ? DDS_FLAGS.FORCE_DX10_EXT : DDS_FLAGS.NONE);
                result = new byte[stream.Length];
                stream.ReadExactly(result, 0, result.Length);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = $"encode failed ({ex.GetType().Name}: {ex.Message})";
                return false;
            }
        }

        // Structure of a generated texture against its plan: size, format, colour space,
        // complete mip chain, exact length, and that it decodes.
        private static string? CheckSharedRepackDds(byte[] dds, SharedRepackSlotOutput expected)
        {
            if (!TryReadDdsLayout(
                    dds.AsSpan(0, Math.Min(dds.Length, TextureDownscaleHeaderPeekBytes)).ToArray(),
                    out var layout))
            {
                return "DDS header is unreadable or unsupported";
            }

            if (layout.Width != expected.Width || layout.Height != expected.Height)
                return $"is {layout.Width}x{layout.Height}, planned {expected.Width}x{expected.Height}";
            if (!layout.Format.Equals(expected.Format, StringComparison.Ordinal) ||
                layout.IsSrgb != expected.Srgb)
            {
                return $"format is {layout.Format}{(layout.IsSrgb ? "_SRGB" : string.Empty)}, planned " +
                       $"{expected.Format}{(expected.Srgb ? "_SRGB" : string.Empty)}";
            }

            var fullChain = GetFullMipChainLength(expected.Width, expected.Height);
            if (layout.MipCount != fullChain)
                return $"has {layout.MipCount} mip level(s), a complete chain has {fullChain}";
            if (dds.LongLength != layout.HeaderSize + layout.ChainBytes)
            {
                return $"is {dds.LongLength:N0} bytes, header and mip chain need " +
                       $"{layout.HeaderSize + layout.ChainBytes:N0}";
            }

            if (layout.ChainBytes != expected.Bytes)
                return $"mip chain is {layout.ChainBytes:N0} bytes, planned {expected.Bytes:N0}";
            if (!TryDecodeDdsTopLevel(dds, out _, out var width, out var height, out var error))
                return error;
            if (width != expected.Width || height != expected.Height)
                return $"decodes to {width}x{height}";

            return null;
        }

        // ---------------------------------------------------------------------------
        // Stage entry.
        // ---------------------------------------------------------------------------

        private void RunSharedTextureRepack(
            BatchState state,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress,
            Func<string, RmvFile?>? loadRigid = null,
            long? minimumSavingBytes = null,
            long? minimumGroupSavingBytes = null,
            int? maximumSide = null)
        {
            var result = new SharedTextureRepackResult
            {
                MinimumSavingBytes = minimumSavingBytes ?? SharedRepackMinimumSavingBytes,
                MinimumGroupSavingBytes = minimumGroupSavingBytes ?? SharedRepackMinimumGroupSavingBytes,
                MaximumSide = maximumSide ?? SharedRepackMaximumSide,
            };
            state.SharedTextureRepack = result;
            var stopwatch = Stopwatch.StartNew();
            ReportProgress(progress, "Shared texture repack");
            // No catch: from the first set it builds this stage changes the output, so an
            // unexpected failure must fail the run rather than leave a half-rewritten pack.
            // Per-set analysis problems are caught inside and only skip that set.
            BuildSharedTextureRepack(
                state,
                result,
                loadRigid ?? (path => GetRmv(state, path)),
                cancellationToken,
                progress);
            result.Elapsed = stopwatch.Elapsed;
            state.PhaseDurations["Shared texture repack"] = result.Elapsed;
        }

        // A texture the early downscale analysis chose a scale for, as the downscale would write it
        // (mip slicing or re-encode, checked like the late run checks it). The repack builds from
        // this, so the same density, block-copy and chart rules apply at the smaller size and the
        // result is the texture the downscale would have produced, repacked. Null: build from the
        // file as authored (no early scale, or the scaled bytes could not be built).
        private static SharedRepackScaledSource? GetSharedRepackScaledSource(BatchState state, string path)
        {
            path = Normalize(path);
            if (!state.DownscaleEarlyCandidates.TryGetValue(path, out var candidate))
                return null;
            if (state.SharedRepackScaledSources.TryGetValue(path, out var cached))
                return cached;

            SharedRepackScaledSource? built = null;
            try
            {
                var bytes = ResolveGameplayTraversalContainer(
                        state.Source, GetGameplayTraversalContainers(state), path, state.ResolutionProviders)?
                    .FindFile(path)?.DataSource.ReadData();
                if (bytes != null &&
                    TryReadDdsLayout(
                        bytes.AsSpan(0, Math.Min(bytes.Length, TextureDownscaleHeaderPeekBytes)).ToArray(),
                        out var layout))
                {
                    byte[] rewritten;
                    string error;
                    using var captureScope = DdsEncodeCapture.Scope("repack-scaled-source", path);
                    var ok = candidate.UseSlicing
                        ? TrySliceDdsMips(bytes, layout, candidate.SkipLevels, out rewritten, out error)
                        : TryReencodeDds(
                            bytes, layout, candidate.NewWidth, candidate.NewHeight,
                            candidate.IsSrgb, out rewritten, out error);
                    if (ok)
                    {
                        var problem = CheckDownscaledDds(rewritten, candidate);
                        if (problem != null)
                        {
                            ok = false;
                            error = $"result {problem}";
                        }
                    }

                    if (ok && TryReadDdsLayout(
                            rewritten.AsSpan(0, Math.Min(rewritten.Length, TextureDownscaleHeaderPeekBytes)).ToArray(),
                            out var scaledLayout))
                    {
                        built = new SharedRepackScaledSource(rewritten, scaledLayout, candidate.Scale);
                    }
                    else
                    {
                        state.SharedRepackScaledSourceFailures.Add($"{path}: {(ok ? "unreadable header" : error)}");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                state.SharedRepackScaledSourceFailures.Add($"{path}: {ex.GetType().Name}: {ex.Message}");
            }

            state.SharedRepackScaledSources[path] = built;
            return built;
        }

        private static VanillaCoverageTexture GetSharedRepackEffectiveEntry(BatchState state, VanillaCoverageTexture entry)
        {
            var scaled = GetSharedRepackScaledSource(state, entry.Path);
            if (scaled == null)
                return entry;

            var path = Normalize(entry.Path);
            if (!state.SharedRepackEffectiveEntries.TryGetValue(path, out var shadow))
            {
                shadow = new VanillaCoverageTexture(entry.Path)
                {
                    Layout = scaled.Layout,
                    IsOwned = entry.IsOwned,
                };
                state.SharedRepackEffectiveEntries[path] = shadow;
            }

            return shadow;
        }

        private sealed record SharedRepackScaledSource(byte[] Dds, DdsLayout Layout, double Scale);

        // "Someone else relies on this path": a vanilla file it overrides, or a file another enabled
        // mod also has (frozen, whatever the load order).
        private static bool OverridesVanillaPath(
            BatchState state,
            string path,
            [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        {
            foreach (var container in GetGameplayTraversalContainers(state))
            {
                if (container.IsCaPackFile &&
                    !ReferenceEquals(container, state.Source) &&
                    container.ContainsFile(path))
                {
                    return true;
                }
            }

            return state.Consumers?.TryBlockFrozen(Normalize(path), StageForCaller(caller)) == true;
        }

        private void BuildSharedTextureRepack(
            BatchState state,
            SharedTextureRepackResult result,
            Func<string, RmvFile?> loadRigid,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var coverage = state.VanillaTextureCoverage;
            var units = state.UnitTextureRepack;
            if (coverage == null || coverage.Failure != null || units == null || units.Failure != null ||
                units.UnavailableReason != null)
            {
                result.UnavailableReason =
                    "the vanilla texture coverage or per-unit estimate analysis did not complete";
                return;
            }

            if (coverage.BudgetExhausted || units.BudgetExhausted)
            {
                result.UnavailableReason =
                    "an analysis ran out of its time budget, so its figures are incomplete and are not acted on";
                return;
            }

            if (!TryCollectSharedRepackGeneratedUsers(
                    state, out var generatedUsers, out var generatedBindings, out var generatedUsersFailure))
            {
                result.UnavailableReason =
                    $"the textures of the rigids generated by cross-rigid merging are unknown ({generatedUsersFailure})";
                return;
            }

            // The generated rigids' parts get coverage data like source parts (their triangles are
            // copies of source triangles, but the generated vertex lists are their own).
            AddSharedRepackGeneratedCoverage(state, coverage, generatedBindings);
            var generatedTextures = generatedUsers
                .GroupBy(user => (user.Rigid, user.Lod, user.Part))
                .ToDictionary(
                    group => group.Key,
                    group => group.SelectMany(user => user.Textures).ToHashSet(StringComparer.OrdinalIgnoreCase));

            var decisions = BuildUnitRepackSetDecisionsAtDensity(
                    units,
                    state.DownscaleEarlyCandidates.Count == 0
                        ? null
                        : entry => GetSharedRepackEffectiveEntry(state, entry).Layout!)
                .OrderByDescending(decision => decision.Saving)
                .ThenBy(decision => decision.ExampleTexture, StringComparer.Ordinal)
                .ToList();
            result.SetsConsidered = decisions.Count;
            var containers = GetGameplayTraversalContainers(state);

            // Shared lookups, built once.
            var partBindings = new Dictionary<(string Rigid, int Lod, int Part), List<SourceWsModelBinding>>();
            var wsByRigid = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var unusedWsByRigid = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var unsafeWsByDirectRigid = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var binding in coverage.Bindings.Concat(generatedBindings))
            {
                if (binding.Kind == SourceWsModelBindingKind.UnreadableWsModel || binding.Geometry.Length == 0)
                    continue;

                if (coverage.DirectConsumersByRigid.ContainsKey(binding.Geometry))
                {
                    var (wsUse, _) = ClassifySharedRepackWsModel(state, binding.WsModelPath, cancellationToken);
                    if (wsUse == SharedRepackWsUnsafe)
                    {
                        if (!unsafeWsByDirectRigid.TryGetValue(binding.Geometry, out var unsafeModels))
                        {
                            unsafeModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            unsafeWsByDirectRigid[binding.Geometry] = unsafeModels;
                        }

                        unsafeModels.Add(binding.WsModelPath);
                        continue;
                    }

                    if (wsUse == SharedRepackWsUnused)
                    {
                        if (!unusedWsByRigid.TryGetValue(binding.Geometry, out var unusedModels))
                        {
                            unusedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            unusedWsByRigid[binding.Geometry] = unusedModels;
                        }

                        unusedModels.Add(binding.WsModelPath);
                        continue;
                    }
                }

                if (!wsByRigid.TryGetValue(binding.Geometry, out var wsSet))
                {
                    wsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    wsByRigid[binding.Geometry] = wsSet;
                }

                wsSet.Add(binding.WsModelPath);
                if (binding.Kind != SourceWsModelBindingKind.Complete)
                    continue;

                var key = (binding.Geometry, binding.Lod, binding.Part);
                if (!partBindings.TryGetValue(key, out var list))
                {
                    list = [];
                    partBindings[key] = list;
                }

                list.Add(binding);
            }

            foreach (var key in coverage.EmbeddedMaterialsByPart.Keys)
            {
                if (!partBindings.ContainsKey(key))
                    partBindings[key] = [];
            }

            var partsByRigid = partBindings.Keys
                .GroupBy(key => key.Rigid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderBy(key => key.Lod).ThenBy(key => key.Part).ToList(),
                    StringComparer.OrdinalIgnoreCase);
            var setByTexture = new Dictionary<string, UnitRepackSignatureSet>(StringComparer.OrdinalIgnoreCase);
            foreach (var signatureSet in units.SetsC)
            {
                foreach (var item in signatureSet.Items)
                    setByTexture[Normalize(item.Entry.Path)] = signatureSet;
            }

            Dictionary<string, HashSet<string>>? rigidReferrers = null;
            var rigidCache = new Dictionary<string, (RmvFile? Rigid, string? Reason)>(
                StringComparer.OrdinalIgnoreCase);
            var partCache = new Dictionary<(string Rigid, int Lod, int Part), SharedRepackPart>();
            var snapshots = new Dictionary<string, SharedRepackRigidSnapshot>(StringComparer.OrdinalIgnoreCase);
            var movedParts = new Dictionary<string, HashSet<(int Lod, int Part)>>(StringComparer.OrdinalIgnoreCase);
            var uniformCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            void Skip(string label, string reason)
                => result.Skips.Add((label, reason));

            (RmvFile? Rigid, string? Reason) GetRigid(string rigidPath)
            {
                if (rigidCache.TryGetValue(rigidPath, out var cached))
                    return cached;

                rigidReferrers ??= state.RigidReferrerIndex ??=
                    IndexSharedRepackRigidReferrers(state, cancellationToken);
                RmvFile? rigid = null;
                var generated = state.CrossRigidGeneratedRigidPaths.Contains(rigidPath);
                // A generated rigid lives in memory only: it is not owned by the source pack and
                // no file refers to it, so the ownership checks do not apply. It is drawn only
                // through the generated WSModels bound to it, which must all be generated ones.
                var reason = generated
                    ? GetSharedRepackGeneratedRigidReason(
                        state, rigidPath, wsByRigid.GetValueOrDefault(rigidPath) ?? [], rigidReferrers)
                    : GetRigidNotOnlyThroughOwnedWsModelsReasonWithDirectGameplayConsumers(
                        state,
                        rigidPath,
                        wsByRigid.GetValueOrDefault(rigidPath) ?? [],
                        rigidReferrers,
                        () => state.AtlasChangedRigids.Contains(rigidPath)
                            ? SharedRepackReasonAtlasChangedRigid
                            : unsafeWsByDirectRigid.TryGetValue(rigidPath, out var unsafeModels) && unsafeModels.Count != 0
                                ? GetSharedRepackUnsafeWsModelReason(state, unsafeModels)
                            : state.ModifiedRigids.Contains(rigidPath) && !result.RepackedRigids.Contains(rigidPath)
                                ? "the rigid was already modified by another stage"
                                : null,
                        allowDirectGameplayConsumers: !SharedRepackDirectRigidsDisabled,
                        ignoredWsModelReferrers: unusedWsByRigid.GetValueOrDefault(rigidPath));

                if (reason == null)
                {
                    try
                    {
                        rigid = generated
                            ? state.RigidModels.GetValueOrDefault(rigidPath)
                            : loadRigid(rigidPath);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        rigid = null;
                    }

                    if (rigid?.ModelList == null)
                    {
                        rigid = null;
                        reason = "the rigid could not be read";
                    }
                }

                cached = (rigid, reason);
                rigidCache[rigidPath] = cached;
                return cached;
            }

            SharedRepackPart GetPart(string rigidPath, RmvFile rigid, int lod, int part)
            {
                var key = (rigidPath, lod, part);
                if (partCache.TryGetValue(key, out var cached))
                    return cached;

                cached = CreateSharedRepackPart(
                    state,
                    coverage,
                    rigid,
                    new MeshKey(rigidPath, lod, part),
                    partBindings.GetValueOrDefault(key) ?? [],
                    GetDirectGameplayRigidConsumers(state, rigidPath),
                    unusedWsByRigid.GetValueOrDefault(rigidPath) ?? []);
                // A generated part whose material the coverage analysis does not know still
                // counts as a user of that material's textures (it is dropped below).
                if (cached.Reason == SharedRepackReasonMaterialUnreadable &&
                    generatedTextures.TryGetValue(key, out var knownTextures))
                {
                    cached.Textures.UnionWith(knownTextures);
                }

                partCache[key] = cached;
                return cached;
            }

            // Signature sets that joined another set's (built) plan, by decision index, with that
            // plan's label.
            var consumed = new Dictionary<int, string>();
            var indexOfSet = new Dictionary<UnitRepackSignatureSet, int>();
            for (var index = 0; index < decisions.Count; index++)
                indexOfSet[decisions[index].Set] = index;
            var setRigidCache = new Dictionary<string, SharedRepackSetRigid>(StringComparer.OrdinalIgnoreCase);

            SharedRepackSetRigid SetRigid(string rigidPath)
            {
                if (!setRigidCache.TryGetValue(rigidPath, out var item))
                {
                    var (loaded, rigidReason) = GetRigid(rigidPath);
                    if (rigidReason == SharedRepackReasonAtlasChangedRigid)
                        result.RigidsChangedByAtlasing.Add(rigidPath);
                    var generatedRigid = state.CrossRigidGeneratedRigidPaths.Contains(rigidPath);
                    if (generatedRigid)
                        result.CrossRigidGeneratedRigidsConsidered.Add(rigidPath);
                    else if (state.CrossRigidChangedRigids.Contains(rigidPath))
                        result.CrossRigidSourceRigidsConsidered.Add(rigidPath);
                    item = new SharedRepackSetRigid
                    {
                        Generated = generatedRigid,
                        Path = rigidPath,
                        Rigid = rigidReason == null ? loaded : null,
                        Reason = rigidReason ?? (loaded == null ? "the rigid could not be read" : null),
                        Parts = partsByRigid.GetValueOrDefault(rigidPath) ?? [],
                        WsModels = wsByRigid.GetValueOrDefault(rigidPath) ?? [],
                    };
                    setRigidCache[rigidPath] = item;
                }

                return item;
            }

            for (var decisionIndex = 0; decisionIndex < decisions.Count; decisionIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decision = decisions[decisionIndex];
                var set = decision.Set;
                var label = DescribeSharedRepackSet(decision);
                ReportProgress(progress, "Shared texture repack", decisionIndex + 1, decisions.Count, label);

                // A set that joined an earlier plan is not looked at again.
                if (consumed.TryGetValue(decisionIndex, out var takenBy))
                {
                    result.MergedIntoOther++;
                    Skip(label, $"merged into another set's plan ({takenBy})");
                    result.Outcomes.Add(new(label, SharedRepackBucketMerged, decision.Saving, null, Plan: takenBy));
                    continue;
                }

                // 1. Estimate.
                if (decision.Saving <= 0 || decision.Saving < result.MinimumSavingBytes)
                {
                    result.BelowThreshold++;
                    Skip(label, $"estimated saving {FormatSignedBytes(decision.Saving)} bytes is below " +
                                $"the {result.MinimumSavingBytes:N0}-byte threshold");
                    result.Outcomes.Add(new(label, SharedRepackBucketBelow, decision.Saving, null));
                    continue;
                }

                // 2. The set's rigids. Each is checked on its own: an ineligible one only loses
                // its parts (they leave the set transitively), the set fails when none is left.
                var rigidPaths = set.Items
                    .SelectMany(item => item.Entry.ModParts.Concat(item.Entry.VanillaParts))
                    .Select(part => part.Rigid)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToList();
                if (rigidPaths.Count == 0)
                {
                    result.DroppedByClosure++;
                    Skip(label, "no part uses the set's textures");
                    result.Outcomes.Add(new(label, SharedRepackBucketClosure, decision.Saving, "no part uses the set's textures"));
                    continue;
                }

                var rigids = rigidPaths.Select(SetRigid).ToList();
                if (rigids.All(item => item.Rigid == null))
                {
                    result.RigidNotEligible++;
                    var ineligibleReason = DescribeSharedRepackIneligibleRigids(rigids);
                    Skip(label, ineligibleReason);
                    result.Outcomes.Add(new(label, SharedRepackBucketIneligible, decision.Saving, ineligibleReason));
                    continue;
                }

                // 3. Plan, compose and check in memory, then write and rewrite. The first attempt
                // may merge unprocessed signature sets that share a material with this one; if
                // a merged attempt fails anywhere, nothing of it was written and the seed is
                // retried alone, with the joined sets released.
                SharedRepackMergeContext NewContext(bool allowMerge, bool copyOutside = false, bool copyRemoved = false)
                {
                    var context = new SharedRepackMergeContext
                    {
                        Seed = decisionIndex,
                        Allow = allowMerge,
                        CopyOutside = copyOutside,
                        CopyRemoved = copyRemoved,
                        Decisions = decisions,
                        IndexOfSet = indexOfSet,
                        IsAvailable = index => index > decisionIndex && !consumed.ContainsKey(index),
                        MakeRigid = SetRigid,
                        GeneratedUsers = generatedUsers,
                        Effective = entry => GetSharedRepackEffectiveEntry(state, entry),
                    };
                    context.Rigids.AddRange(rigids);
                    return context;
                }

                (SharedRepackBuiltSet? Built, string? Failure, string Bucket, SharedRepackMergeContext Context) Attempt(
                    bool allowMerge)
                {
                    var reportSnapshot = SharedRepackAttemptReportSnapshot.Capture(result);
                    var paddingLimits = new Dictionary<string, int>(StringComparer.Ordinal);
                    var loweredFrom = new Dictionary<string, string>(StringComparer.Ordinal);
                    var retryAttempts = 0;
                    var skipCopy = false;

                    SharedRepackSetPlan? PlanOnce(
                        SharedRepackMergeContext planContext,
                        out string? planFailure,
                        out string planBucket)
                    {
                        try
                        {
                            return PlanSharedRepackSet(
                                state,
                                result,
                                coverage,
                                planContext,
                                label,
                                setByTexture,
                                GetPart,
                                texture => IsSharedRepackUniformTexture(state, coverage, texture, uniformCache),
                                cancellationToken,
                                paddingLimits,
                                out planFailure,
                                out planBucket);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            planFailure = $"internal error while planning ({ex.GetType().Name}: {ex.Message})";
                            planBucket = "build";
                            return null;
                        }
                    }

                    SharedRepackBuiltSet? ApplyPlan(
                        SharedRepackMergeContext applyContext,
                        SharedRepackSetPlan applyPlan,
                        out string? applyFailure,
                        out bool applyPixelFailure)
                        => ApplySharedRepackSetPlan(
                            state,
                            result,
                            applyContext,
                            applyPlan,
                            applyContext.Rigids.Where(item => item.Rigid != null)
                                .ToDictionary(item => item.Path, item => item.Rigid!, StringComparer.OrdinalIgnoreCase),
                            applyContext.Joined.Count == 0 ? label : label + " [merged plan]",
                            snapshots,
                            movedParts,
                            cancellationToken,
                            out applyFailure,
                            out applyPixelFailure);

                    void FinishRetries(SharedRepackBuiltSet? built)
                    {
                        result.PaddingPixelRetryAttempts += retryAttempts;
                        if (built == null)
                            return;

                        foreach (var group in built.Groups)
                        {
                            if (loweredFrom.TryGetValue(group.GroupKey, out var from) && from != group.PaddingVariant)
                                group.PaddingRetryFromVariant = from;
                        }
                    }

                    for (var retry = 0; retry < 3; retry++)
                    {
                        if (retry != 0)
                            reportSnapshot.Restore(result);

                        var context = NewContext(allowMerge);
                        var plan = PlanOnce(context, out var failure, out var failureBucket);

                        // Copies: when the normal plan lost a part only to textures that left the plan, the
                        // set is planned again with those textures copied into the pages, and the plan
                        // with the larger exact saving is kept (a tie keeps the normal plan). The loser's
                        // side effects are undone, so a normal win leaves output and report as without
                        // copies.
                        if (!SharedRepackCopyRemovedDisabled && !skipCopy && context.CopyRescuable)
                        {
                            var afterNormal = SharedRepackAttemptReportSnapshot.Capture(result);
                            var normalCharts = plan == null ? [] : SnapshotSharedRepackPartCharts(plan);
                            var partsBefore = partCache.Keys.ToHashSet();
                            var uncheckedParts = partCache.Values.Where(part => !part.GeometryChecked).ToList();
                            reportSnapshot.Restore(result);
                            var copyContext = NewContext(allowMerge, copyRemoved: true);
                            var copyPlan = PlanOnce(copyContext, out var copyFailure, out var copyBucket);
                            long? normalSaving = plan == null ? null : GetSharedRepackPlanSaving(plan);
                            var copySaving = copyPlan == null ? 0 : GetSharedRepackPlanSaving(copyPlan);
                            context.PlannedBothWays = copyContext.PlannedBothWays = true;
                            context.NormalSaving = copyContext.NormalSaving = normalSaving;
                            context.CopySaving = copyContext.CopySaving = copySaving;
                            context.CopyFailure = copyContext.CopyFailure = copyPlan == null ? copyFailure ?? "unknown" : null;
                            if (copyPlan != null && (normalSaving == null || copySaving > normalSaving))
                            {
                                copyContext.CopyWon = true;
                                context = copyContext;
                                plan = copyPlan;
                                failure = copyFailure;
                                failureBucket = copyBucket;
                            }
                            else
                            {
                                afterNormal.Restore(result);
                                foreach (var part in uncheckedParts.Where(part => part.GeometryChecked))
                                {
                                    part.GeometryChecked = false;
                                    part.Reason = null;
                                }

                                foreach (var key in partCache.Keys.Where(key => !partsBefore.Contains(key)).ToList())
                                    partCache.Remove(key);
                                RestoreSharedRepackPartCharts(normalCharts);
                            }
                        }

                        if (plan == null)
                        {
                            FinishRetries(null);
                            return (null, failure ?? "unknown", failureBucket, context);
                        }

                        var built = ApplyPlan(context, plan, out var buildFailure, out var pixelFailure);
                        if (built != null)
                        {
                            FinishRetries(built);
                            return (built, null, "built", context);
                        }

                        var lowered = false;
                        if (pixelFailure)
                        {
                            foreach (var groupKey in plan.FailedPaddingGroupKeys)
                            {
                                var variant = plan.Splits.GetValueOrDefault(groupKey)?.PaddingVariant;
                                var index = variant switch
                                {
                                    "align 16" => 1,
                                    "gutter 8 + align 8" => 2,
                                    _ => 0,
                                };
                                if (index == 0)
                                    continue;

                                if (plan.Splits.TryGetValue(groupKey, out var split))
                                    loweredFrom.TryAdd(groupKey, split.PaddingVariant);
                                paddingLimits[groupKey] = index - 1;
                                lowered = true;
                            }
                        }

                        if (lowered)
                        {
                            retryAttempts++;
                            continue;
                        }

                        if (context.CopyWon)
                        {
                            // The copy plan could not be built: plan again without copies.
                            skipCopy = true;
                            continue;
                        }

                        FinishRetries(null);
                        return (null, buildFailure ?? "unknown", pixelFailure ? "pixel" : "build", context);
                    }

                    FinishRetries(null);
                    return (null, "the reduced-padding layouts did not converge", "pixel", NewContext(allowMerge));
                }

                // Report only: the seed set planned alone with its outside textures copied in, up to the
                // closure's first re-estimate. Nothing is built and the report state is restored.
                SharedRepackCopyWhatIf RunCopyWhatIf()
                {
                    var snapshot = SharedRepackAttemptReportSnapshot.Capture(result);
                    // Geometry checks the what-if runs are undone, so later real attempts record the
                    // same drop reasons as without it.
                    var partsBefore = partCache.Keys.ToHashSet();
                    var uncheckedParts = partCache.Values.Where(part => !part.GeometryChecked).ToList();
                    var context = NewContext(false, copyOutside: true);
                    try
                    {
                        PlanSharedRepackSet(
                            state,
                            result,
                            coverage,
                            context,
                            label,
                            setByTexture,
                            GetPart,
                            texture => IsSharedRepackUniformTexture(state, coverage, texture, uniformCache),
                            cancellationToken,
                            new Dictionary<string, int>(StringComparer.Ordinal),
                            out var whatIfFailure,
                            out _);
                        return context.WhatIf ??
                               new(0, 0, 0, 0, whatIfFailure ?? "the closure stopped before its re-estimate");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        return new(0, 0, 0, 0, $"internal error ({ex.GetType().Name}: {ex.Message})");
                    }
                    finally
                    {
                        snapshot.Restore(result);
                        foreach (var part in uncheckedParts.Where(part => part.GeometryChecked))
                        {
                            part.GeometryChecked = false;
                            part.Reason = null;
                        }

                        foreach (var key in partCache.Keys.Where(key => !partsBefore.Contains(key)).ToList())
                            partCache.Remove(key);
                    }
                }

                var dropsBefore = new Dictionary<string, int>(result.PartDropReasons, StringComparer.Ordinal);
                var notesBefore = result.ClosureNotes.Count;
                var directDropsBefore = result.DirectEmbeddedDrops.Count;
                var directReasonsBefore = result.DirectEmbeddedDropReasonsByRigid
                    .ToDictionary(
                        pair => pair.Key,
                        pair => new Dictionary<string, int>(pair.Value, StringComparer.Ordinal),
                        StringComparer.OrdinalIgnoreCase);
                var sizingBefore = (result.SizingErrors, result.SizingLog.Count);
                var attempt = Attempt(true);
                string? fallbackNote = null;
                if (attempt.Context.CapHit)
                    result.MergeCapHits++;
                if (attempt.Context.GeneratedCapHit)
                    result.MergeGeneratedCapHits++;
                if (attempt.Context.Joined.Count != 0)
                {
                    result.MergePlansTried++;
                    result.LargestMergeSets = Math.Max(result.LargestMergeSets, attempt.Context.Component.Count);
                    result.LargestMergeRigids = Math.Max(result.LargestMergeRigids, attempt.Context.Rigids.Count);
                    if (attempt.Built != null)
                    {
                        result.MergePlansBuilt++;
                        foreach (var index in attempt.Context.Joined)
                            consumed[index] = label;
                    }
                    else
                    {
                        result.MergePlansFellBack++;
                        fallbackNote = $"the merged attempt with {attempt.Context.Joined.Count:N0} other signature " +
                                       $"set(s) failed ({attempt.Failure}); ";
                        result.PartDropReasons.Clear();
                        foreach (var (reason, count) in dropsBefore)
                            result.PartDropReasons[reason] = count;
                        result.DirectEmbeddedDrops.RemoveRange(
                            directDropsBefore,
                            result.DirectEmbeddedDrops.Count - directDropsBefore);
                        result.DirectEmbeddedDropReasonsByRigid.Clear();
                        foreach (var (rigid, reasons) in directReasonsBefore)
                            result.DirectEmbeddedDropReasonsByRigid[rigid] = new(reasons, StringComparer.Ordinal);
                        // Only the cap notes of the failed attempt are kept; they explain parts that
                        // the attempt could not take in.
                        var attemptNotes = result.ClosureNotes.Skip(notesBefore).ToList();
                        result.ClosureNotes.RemoveRange(notesBefore, attemptNotes.Count);
                        result.ClosureNotes.AddRange(attemptNotes
                            .Where(note => note.Note.Contains("merge cap reached", StringComparison.Ordinal))
                            .Select(note => (note.Label + " [merged attempt]", note.Note)));
                        result.SizingErrors = sizingBefore.SizingErrors;
                        result.SizingLog.RemoveRange(sizingBefore.Item2, result.SizingLog.Count - sizingBefore.Item2);
                        var failedMerged = attempt.Context;
                        var failedMergedFailure = attempt.Failure;
                        attempt = Attempt(false);
                        attempt.Context.FailedMerged = failedMerged;
                        attempt.Context.FailedMergedFailure = failedMergedFailure;
                        attempt.Context.FailedMergedNotes.AddRange(attemptNotes.Select(note => note.Note));
                    }
                }

                attempt.Context.FallbackReason = fallbackNote;
                if (attempt.Context.RemovedTextures.Values.Any(item =>
                        item.Reason is SharedRepackReasonMixed or SharedRepackReasonBlockedTexture))
                {
                    attempt.Context.WhatIf = RunCopyWhatIf();
                }

                if (attempt.Built == null)
                {
                    string bucket;
                    if (attempt.Bucket == "bytes")
                    {
                        result.FailedActualBytes++;
                        bucket = SharedRepackBucketBytes;
                    }
                    else if (attempt.Bucket == "pixel")
                    {
                        result.FailedPixelCheck++;
                        bucket = SharedRepackBucketPixel;
                    }
                    else if (attempt.Bucket == "build")
                    {
                        result.NotBuildable++;
                        bucket = SharedRepackBucketBuild;
                    }
                    else
                    {
                        result.DroppedByClosure++;
                        bucket = SharedRepackBucketClosure;
                    }

                    var failureReason = fallbackNote == null ? attempt.Failure! : fallbackNote + "alone: " + attempt.Failure;
                    Skip(label, failureReason);
                    result.Outcomes.Add(new(label, bucket, decision.Saving, failureReason, Context: attempt.Context));
                    continue;
                }

                attempt.Built.FallbackNote = fallbackNote == null ? null : fallbackNote + "built alone";
                result.BuiltSets.Add(attempt.Built);
                result.Outcomes.Add(new(
                    label,
                    SharedRepackBucketBuilt,
                    decision.Saving,
                    null,
                    attempt.Built.Before - attempt.Built.After,
                    label,
                    attempt.Context));
            }

            BuildSharedRepackDirectRigidOutcomes(
                state,
                coverage,
                decisions,
                setByTexture,
                result,
                path => GetRigid(path).Reason);

            // Geometry invariants of every rewritten rigid, against the snapshots.
            foreach (var (path, snapshot) in snapshots.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                var rigid = rigidCache[path].Rigid!;
                CompareSharedRepackSnapshot(
                    path,
                    rigid,
                    snapshot,
                    movedParts.GetValueOrDefault(path) ?? [],
                    result.EmbeddedRewrittenPaths
                        .Where(item => item.Rigid.Equals(path, StringComparison.OrdinalIgnoreCase))
                        .Select(item => (item.Lod, item.Part))
                        .ToHashSet(),
                    result.ValidationErrors);
            }
        }

        // The parts of the rigids the cross-rigid writer generated, every texture of the materials
        // their generated WSModels bind, and those bindings as source-style bindings (so the
        // generated parts go through the same analysis as the source parts). False when a
        // generated WSModel or material cannot be read: its textures are then unknown, which
        // the repack cannot act on.
        private static bool TryCollectSharedRepackGeneratedUsers(
            BatchState state,
            out List<(string Rigid, int Lod, int Part, HashSet<string> Textures)> users,
            out List<SourceWsModelBinding> bindings,
            out string? failure)
        {
            users = [];
            bindings = [];
            failure = null;
            var texturesByMaterial = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);
            foreach (var wsPath in state.CrossRigidGeneratedWsModelPaths.OrderBy(path => path, StringComparer.Ordinal))
            {
                var document = GetWsDocument(state, wsPath);
                var rigid = Normalize(document?.SelectSingleNode("/model/geometry")?.InnerText ?? string.Empty);
                if (document == null || rigid.Length == 0)
                {
                    failure = $"generated WSModel {wsPath} cannot be read";
                    return false;
                }

                foreach (XmlNode node in document.SelectNodes("/model/materials/material") ?? EmptyXmlNodeList)
                {
                    if (!TryParseIndex(node, "lod_index", out var lod) ||
                        !TryParseIndex(node, "part_index", out var partIndex))
                    {
                        failure = $"generated WSModel {wsPath} has a material without lod and part index";
                        return false;
                    }

                    var material = Normalize(node.InnerText);
                    if (!texturesByMaterial.TryGetValue(material, out var textures))
                    {
                        var file = FindForReadStatic(state, material) ??
                                   ResolveGameplayTraversalContainer(
                                       state.Source, GetGameplayTraversalContainers(state), material, state.ResolutionProviders)?
                                   .FindFile(material);
                        if (file != null)
                        {
                            textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            var materialDocument = LoadXml(file);
                            foreach (XmlNode texture in materialDocument.SelectNodes("/material/textures/texture") ?? EmptyXmlNodeList)
                            {
                                var path = Normalize(texture.SelectSingleNode("source")?.InnerText ?? texture.InnerText);
                                if (path.Length != 0 && !IsTexturePlaceholder(path))
                                    textures.Add(path);
                            }
                        }

                        texturesByMaterial[material] = textures;
                    }

                    if (textures == null)
                    {
                        failure = $"material {material} of generated WSModel {wsPath} cannot be read";
                        return false;
                    }

                    users.Add((rigid, lod, partIndex, textures));
                    if (material.Length != 0)
                    {
                        bindings.Add(new SourceWsModelBinding(
                            Normalize(wsPath), rigid, lod, partIndex, material, SourceWsModelBindingKind.Complete));
                    }
                }
            }

            return true;
        }

        // UV coverage grids of the generated rigids' parts, so the closure's re-estimate treats
        // them like source parts. A part that cannot be assessed gets no entry and is dropped
        // with the "no coverage data" reason.
        private static void AddSharedRepackGeneratedCoverage(
            BatchState state,
            VanillaTextureCoverageResult coverage,
            List<SourceWsModelBinding> bindings)
        {
            var scratch = CreateVanillaCoverageGrid();
            foreach (var key in bindings
                         .Select(binding => (binding.Geometry, binding.Lod, binding.Part))
                         .Distinct()
                         .OrderBy(key => key.Geometry, StringComparer.Ordinal)
                         .ThenBy(key => key.Lod)
                         .ThenBy(key => key.Part))
            {
                if (coverage.PartData.ContainsKey(key) ||
                    !state.RigidModels.TryGetValue(key.Geometry, out var rigid) ||
                    rigid.ModelList == null ||
                    key.Lod < 0 || key.Lod >= rigid.ModelList.Length ||
                    key.Part < 0 || key.Part >= rigid.ModelList[key.Lod].Length)
                {
                    continue;
                }

                Array.Clear(scratch);
                if (TryMarkVanillaPartCoverage(
                        rigid.ModelList[key.Lod][key.Part], scratch, out var tiled, out _, out _))
                {
                    coverage.PartData[key] = new VanillaCoveragePart(scratch.ToArray(), tiled);
                }
            }
        }

        // Why a generated cross-rigid rigid cannot be rewritten (null = it can): atlasing changed
        // it, a bound WSModel is not a generated one, or a file refers to it.
        private static string? GetSharedRepackGeneratedRigidReason(
            BatchState state,
            string rigidPath,
            HashSet<string> boundWsModels,
            Dictionary<string, HashSet<string>> referrers)
        {
            if (state.AtlasChangedRigids.Contains(rigidPath))
                return SharedRepackReasonAtlasChangedRigid;
            if (!state.RigidModels.ContainsKey(rigidPath))
                return "the generated rigid is not in memory";
            foreach (var ws in boundWsModels.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (!state.CrossRigidGeneratedWsModelPaths.Contains(ws))
                    return $"WSModel {ws} is not a generated cross-rigid WSModel";
            }

            var other = (referrers.GetValueOrDefault(rigidPath) ?? [])
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            return other == null
                ? null
                : $"{other} refers to the generated rigid";
        }

        private static readonly XmlNodeList EmptyXmlNodeList = new XmlDocument().SelectNodes("/none")!;

        // One line for a set whose rigids are all ineligible: the first few, each with its reason.
        private static string DescribeSharedRepackIneligibleRigids(List<SharedRepackSetRigid> rigids)
        {
            var text = string.Join("; ", rigids.Take(3).Select(item => $"{item.Path}: {item.Reason}"));
            return rigids.Count > 3 ? $"{text}; and {rigids.Count - 3:N0} more rigid(s) not eligible" : text;
        }

        // A texture whose decoded top level is one colour (every channel within 1 of the first
        // texel). Decoded lazily and once per path; anything over the size limit, unreadable
        // or unknown counts as not uniform.
        private static bool IsSharedRepackUniformTexture(
            BatchState state,
            VanillaTextureCoverageResult coverage,
            string texture,
            Dictionary<string, bool> cache)
        {
            if (cache.TryGetValue(texture, out var cached))
                return cached;

            var uniform = false;
            try
            {
                var layout = coverage.Entries.TryGetValue(texture, out var entry) ? entry.Layout : null;
                if (layout == null || (layout.Width <= SharedRepackUniformTextureSide &&
                                       layout.Height <= SharedRepackUniformTextureSide))
                {
                    var bytes = ResolveGameplayTraversalContainer(
                            state.Source, GetGameplayTraversalContainers(state), texture, state.ResolutionProviders)?
                        .FindFile(texture)?.DataSource.ReadData();
                    if (layout == null && bytes != null &&
                        TryReadDdsLayout(
                            bytes.AsSpan(0, Math.Min(bytes.Length, TextureDownscaleHeaderPeekBytes)).ToArray(),
                            out var header))
                    {
                        layout = header;
                    }

                    if (bytes != null && layout != null &&
                        layout.Width <= SharedRepackUniformTextureSide &&
                        layout.Height <= SharedRepackUniformTextureSide &&
                        TryDecodeDdsTopLevel(bytes, out var pixels, out _, out _, out _) &&
                        pixels.Length >= 4)
                    {
                        uniform = true;
                        for (var offset = 4; offset < pixels.Length && uniform; offset += 4)
                        {
                            for (var channel = 0; channel < 4; channel++)
                            {
                                if (Math.Abs(pixels[offset + channel] - pixels[channel]) > 1)
                                    uniform = false;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                uniform = false;
            }

            cache[texture] = uniform;
            return uniform;
        }

        // Why a rigid cannot be treated as drawn only through WSModels of the source pack (the
        // game then ignores its embedded material and rewrites of its UVs or textures reach
        // every user), else null. Shared by the shared texture repack and the exact texture
        // compaction. `beforeWsModelChecks` is a check only the caller has, run after the
        // gameplay checks. `referrers` is IndexSharedRepackRigidReferrers.
        private static string? GetRigidNotOnlyThroughOwnedWsModelsReason(
            BatchState state,
            string rigidPath,
            HashSet<string> boundWsModels,
            Dictionary<string, HashSet<string>> referrers,
            Func<string?>? beforeWsModelChecks = null)
            => GetRigidNotOnlyThroughOwnedWsModelsReasonWithDirectGameplayConsumers(
                state, rigidPath, boundWsModels, referrers, beforeWsModelChecks,
                allowDirectGameplayConsumers: false);

        private static string? GetRigidNotOnlyThroughOwnedWsModelsReasonWithDirectGameplayConsumers(
            BatchState state,
            string rigidPath,
            HashSet<string> boundWsModels,
            Dictionary<string, HashSet<string>> referrers,
            Func<string?>? beforeWsModelChecks,
            bool allowDirectGameplayConsumers,
            HashSet<string>? ignoredWsModelReferrers = null)
        {
            if (!state.Source.ContainsFile(rigidPath))
                return "the rigid is not owned by the source pack";
            if (OverridesVanillaPath(state, rigidPath))
                return "the rigid overrides a vanilla path";
            var directConsumers = allowDirectGameplayConsumers
                ? GetDirectGameplayRigidConsumers(state, rigidPath)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (directConsumers.Count == 0 && HasDirectGameplayRigidConsumer(state, rigidPath))
                return "the rigid is referenced directly (without a WSModel) by a VMD or other gameplay file";
            if (!IsGameplayConsumerDiscoverySafeForAsset(state, rigidPath))
                return "gameplay consumer discovery for the rigid is incomplete";
            var extra = beforeWsModelChecks?.Invoke();
            if (extra != null)
                return extra;

            var immutable = GetImmutableGameplayWsModelConsumersForRigid(
                state, rigidPath, "GetRigidNotOnlyThroughOwnedWsModelsReason");
            var others = (referrers.GetValueOrDefault(rigidPath) ?? [])
                .Where(path => !boundWsModels.Contains(path) && !directConsumers.Contains(path) &&
                               ignoredWsModelReferrers?.Contains(path) != true)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (immutable.Count != 0)
                return $"a WSModel outside the source pack uses the rigid ({immutable.First()})";
            if (others.Count != 0)
                return $"{others[0]} refers to the rigid other than as a readable WSModel binding";

            foreach (var ws in boundWsModels.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (!state.Source.ContainsFile(ws))
                    return $"WSModel {ws} is not owned by the source pack";
                if (OverridesVanillaPath(state, ws))
                    return $"WSModel {ws} overrides a vanilla path";
            }

            return null;
        }

        private static HashSet<string> GetDirectGameplayRigidConsumers(BatchState state, string rigidPath)
        {
            rigidPath = Normalize(rigidPath);
            var consumers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (state.GameplayMeshDependencyIndex?.DirectConsumersByRigid.TryGetValue(
                    rigidPath, out var indexed) == true)
            {
                consumers.UnionWith(indexed);
            }

            if (state.DirectRigidMeshMergeConsumersByRigid.TryGetValue(rigidPath, out var fallback))
                consumers.UnionWith(fallback);

            return consumers;
        }

        private static void BuildSharedRepackDirectRigidOutcomes(
            BatchState state,
            VanillaTextureCoverageResult coverage,
            IReadOnlyList<UnitRepackSetDecision> decisions,
            IReadOnlyDictionary<string, UnitRepackSignatureSet> setByTexture,
            SharedTextureRepackResult result,
            Func<string, string?> rigidReason)
        {
            var decisionBySet = decisions
                .GroupBy(decision => decision.Set)
                .ToDictionary(group => group.Key, group => group.First());
            var outcomeByLabel = result.Outcomes
                .GroupBy(outcome => outcome.Label, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var directPartCount = coverage.EmbeddedMaterialsByPart.Keys
                .GroupBy(key => key.Rigid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

            foreach (var (rigid, consumers) in coverage.DirectConsumersByRigid
                         .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                var setLabels = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var (key, material) in coverage.EmbeddedMaterialsByPart
                             .Where(pair => pair.Key.Rigid.Equals(rigid, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!material.Readable)
                        continue;

                    foreach (var texture in material.Textures)
                    {
                        if (!TryGetSharedRepackEmbeddedSlot(texture.TexureType, out _) ||
                            !setByTexture.TryGetValue(Normalize(texture.Path), out var set) ||
                            !decisionBySet.TryGetValue(set, out var decision))
                        {
                            continue;
                        }

                        setLabels.Add(DescribeSharedRepackSet(decision));
                    }
                }

                if (setLabels.Count == 0)
                    continue;

                // Rigids the pack does not own are only counted, by reason.
                var notOwned = !state.Source.ContainsFile(rigid)
                    ? "not owned by the source pack"
                    : OverridesVanillaPath(state, rigid)
                        ? "override a vanilla path"
                        : null;
                if (notOwned != null)
                {
                    result.DirectRigidsNotOwned[notOwned] = result.DirectRigidsNotOwned.GetValueOrDefault(notOwned) + 1;
                    continue;
                }

                var ineligible = result.EmbeddedRewrittenRigids.Contains(rigid) ? null : rigidReason(rigid);
                result.DirectRigidOutcomes.Add(new SharedRepackDirectRigidOutcome(
                    rigid,
                    consumers.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
                    setLabels.ToArray(),
                    result.EmbeddedRewrittenRigids.Contains(rigid)
                        ? result.DirectEmbeddedDropReasonsByRigid.ContainsKey(rigid)
                            ? "partially rewritten"
                            : "rewritten"
                        : ineligible != null
                            ? "rigid not eligible"
                            : result.DirectEmbeddedDropReasonsByRigid.ContainsKey(rigid)
                                ? "embedded parts dropped"
                                : "not rewritten",
                    ineligible != null
                        ? ineligible
                        : BuildDirectRigidOutcomeDetail(
                            rigid,
                            setLabels,
                            outcomeByLabel,
                            result,
                            directPartCount.GetValueOrDefault(rigid))));
            }
        }

        private static string BuildDirectRigidOutcomeDetail(
            string rigid,
            IEnumerable<string> setLabels,
            IReadOnlyDictionary<string, SharedRepackSetOutcome> outcomeByLabel,
            SharedTextureRepackResult result,
            int partCount)
        {
            if (result.EmbeddedRewrittenRigids.Contains(rigid))
            {
                var rewrittenParts = result.BuiltSets
                    .SelectMany(set => set.EmbeddedRigids)
                    .Where(item => item.Rigid.Equals(rigid, StringComparison.OrdinalIgnoreCase))
                    .Sum(item => item.Parts);
                var dropped = result.DirectEmbeddedDropReasonsByRigid.GetValueOrDefault(rigid);
                var dropText = dropped == null || dropped.Count == 0
                    ? string.Empty
                    : $"; dropped {string.Join(", ", dropped.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => $"{pair.Value:N0} {pair.Key}"))}";
                return $"rewritten {rewrittenParts:N0} of {partCount:N0} embedded-material part(s){dropText}";
            }

            if (result.DirectEmbeddedDropReasonsByRigid.TryGetValue(rigid, out var reasons))
            {
                return "parts dropped: " + string.Join(
                    ", ",
                    reasons.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => $"{pair.Value:N0} {pair.Key}"));
            }

            var outcomes = setLabels
                .Select(label => outcomeByLabel.TryGetValue(label, out var outcome)
                    ? $"{label} -> {outcome.Bucket}{(outcome.Reason == null ? string.Empty : $" ({outcome.Reason})")}"
                    : $"{label} -> no outcome")
                .ToArray();
            if (result.RepackedRigids.Contains(rigid))
                return "UVs rewritten; no embedded texture path needed repointing";

            return outcomes.Length == 0 ? "no set outcome recorded" : string.Join("; ", outcomes);
        }

        private static string DescribeSharedRepackSet(UnitRepackSetDecision decision)
            => $"{decision.ExampleTexture} ({decision.Textures:N0} texture(s), {decision.SignatureSize:N0} unit(s))";

        private static Dictionary<string, HashSet<string>> IndexSharedRepackRigidReferrers(
            BatchState state,
            CancellationToken cancellationToken)
        {
            var pattern = new Regex(@"[^\s<>""'=,;|()]+\.(?:rigid_model_v2|wsmodel)", RegexOptions.IgnoreCase);
            var index = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (rawPath, file) in state.Source.GetAllFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Normalize(rawPath);
                if (!IsTextureDownscaleTextFile(path))
                    continue;

                string text;
                try
                {
                    text = Encoding.UTF8.GetString(file.DataSource.ReadData());
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    continue;
                }

                foreach (Match match in pattern.Matches(text))
                {
                    var rigid = Normalize(match.Value);
                    if (!index.TryGetValue(rigid, out var referrers))
                    {
                        referrers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        index[rigid] = referrers;
                    }

                    referrers.Add(path);
                }
            }

            return index;
        }

        // ---------------------------------------------------------------------------
        // Part analysis.
        // ---------------------------------------------------------------------------

        // Pass-through slots, and the unresolved mask sentinel the atlas stage also treats as
        // absent: never part of a set, left as authored in the generated material.
        private static bool IsSharedRepackIgnoredTexture(string slot, string texture)
            => SharedRepackPassThroughSlots.Contains(slot, StringComparer.OrdinalIgnoreCase) ||
               IsIgnorableUnresolvedAtlasTexture(slot, texture);

        private static bool TryGetSharedRepackEmbeddedSlot(TextureType textureType, out string slot)
        {
            slot = textureType switch
            {
                TextureType.BaseColour => "t_xml_base_colour",
                TextureType.MaterialMap => "t_xml_material_map",
                TextureType.Normal => "t_xml_normal",
                TextureType.Mask => "t_xml_mask",
                _ => string.Empty,
            };
            return slot.Length != 0;
        }

        private const int SharedRepackWsUsed = 0;
        private const int SharedRepackWsUnused = 1;
        private const int SharedRepackWsUnsafe = 2;

        private static string GetSharedRepackUnsafeWsModelReason(BatchState state, IEnumerable<string> wsModels)
        {
            var ws = wsModels.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).First();
            var detail = state.SharedRepackWsModelUse.TryGetValue(Normalize(ws), out var use) ? use.Reason : null;
            return $"gameplay consumer discovery for bound WSModel {ws} is incomplete" +
                   (detail == null ? string.Empty : $" ({detail})");
        }

        // Used: a gameplay root reaches the WSModel. Unused: discovery is complete, or it is partial
        // but no root, incomplete root or source file can name it. Unsafe: anything else.
        private static (int Use, string? Reason) ClassifySharedRepackWsModel(
            BatchState state,
            string wsModelPath,
            CancellationToken cancellationToken)
        {
            var path = Normalize(wsModelPath);
            if (state.SharedRepackWsModelUse.TryGetValue(path, out var cached))
                return cached;

            var result = ClassifySharedRepackWsModelCore(state, path, cancellationToken);
            state.SharedRepackWsModelUse[path] = result;
            return result;
        }

        private static (int Use, string? Reason) ClassifySharedRepackWsModelCore(
            BatchState state,
            string path,
            CancellationToken cancellationToken)
        {
            var index = state.GameplayMeshDependencyIndex;
            var hasRoots = index != null &&
                           (index.GameplayRoots.Contains(path) || index.GetRootsForAsset(path).Any());
            if (IsGameplayConsumerDiscoverySafeForAsset(state, path))
                return (hasRoots ? SharedRepackWsUsed : SharedRepackWsUnused, null);

            if (index == null || state.UnitCategoryResolution == null ||
                !state.UnitCategoryResolution.IsGameplayResolutionHealthy)
            {
                return (SharedRepackWsUnsafe, null);
            }

            if (hasRoots || index.GetIncompleteRootsForAsset(path).Count != 0)
            {
                var roots = index.GetIncompleteRootsForAsset(path);
                return (SharedRepackWsUnsafe, roots.Count == 0
                    ? null
                    : "reached from incomplete root " + string.Join(", ", roots.Take(3)));
            }

            // Partial discovery: an incomplete root might still name the WSModel in its bytes.
            var fileName = Path.GetFileName(path);
            foreach (var root in index.IncompleteRoots.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = GetSharedRepackIncompleteRootText(state, root);
                if (text == null)
                    return (SharedRepackWsUnsafe, $"incomplete root {root} cannot be read");
                if (text.Contains(fileName, StringComparison.OrdinalIgnoreCase))
                    return (SharedRepackWsUnsafe, $"incomplete root {root} mentions it");
            }

            var referrers = (state.RigidReferrerIndex ??= IndexSharedRepackRigidReferrers(state, cancellationToken))
                .GetValueOrDefault(path);
            var other = referrers?.Where(item => !item.Equals(path, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (other != null)
                return (SharedRepackWsUnsafe, $"source file {other} mentions it");

            // Binary DB tables are not text files: search their raw bytes.
            state.SourceDbTableTexts ??= ReadSharedRepackSourceDbTables(state, cancellationToken);
            foreach (var (tablePath, text) in state.SourceDbTableTexts)
            {
                if (text == null)
                    return (SharedRepackWsUnsafe, $"DB table {tablePath} cannot be read");
                if (text.Contains(fileName, StringComparison.OrdinalIgnoreCase))
                    return (SharedRepackWsUnsafe, $"DB table {tablePath} mentions it");
            }

            return (SharedRepackWsUnused, null);
        }

        // Every source-pack DB table's raw bytes as Latin-1 and UTF-16 (built only when needed).
        private static List<(string Path, string? Text)> ReadSharedRepackSourceDbTables(
            BatchState state,
            CancellationToken cancellationToken)
        {
            var tables = new List<(string Path, string? Text)>();
            foreach (var (rawPath, file) in state.Source.GetAllFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Normalize(rawPath);
                if (!path.StartsWith("db\\", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? text = null;
                try
                {
                    var data = file.DataSource.ReadData();
                    text = Encoding.Latin1.GetString(data) + "\n" + Encoding.Unicode.GetString(data);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                }

                tables.Add((path, text));
            }

            return tables;
        }

        // Raw bytes read as Latin-1 and UTF-16, once per root.
        private static string? GetSharedRepackIncompleteRootText(BatchState state, string root)
        {
            var cache = state.IncompleteRootTexts ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (cache.TryGetValue(root, out var cached))
                return cached;

            string? text = null;
            try
            {
                var data = ResolveGameplayTraversalContainer(
                        state.Source,
                        GetGameplayTraversalContainers(state),
                        root,
                        state.ResolutionProviders)?.FindFile(root)?.DataSource.ReadData();
                if (data != null)
                    text = Encoding.Latin1.GetString(data) + "\n" + Encoding.Unicode.GetString(data);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }

            cache[root] = text;
            return text;
        }

        // The same, plus a t_xml_ slot the material's shader never binds (see IsSlotUnreadByShader).
        // A dissolve detail texture of a verified material stays as authored too (its scale parameter is
        // divided instead).
        private static bool IsSharedRepackIgnoredTexture(VanillaCoverageMaterial info, string slot, string texture)
            => IsSharedRepackIgnoredTexture(slot, texture) || IsSlotUnreadByShader(info.Shader, slot) ||
               (slot.Equals(SharedRepackDissolveDetailSlot, StringComparison.OrdinalIgnoreCase) &&
                GetSharedRepackDissolveReason(info) == null) ||
               (SharedRepackFlowPassThroughSlots.Contains(slot, StringComparer.OrdinalIgnoreCase) &&
                GetSharedRepackFlowReason(info) == null);

        private static bool IsSharedRepackNoiseSlot(string slot)
            => slot.Equals(SharedRepackEmissiveDistortionSlot, StringComparison.OrdinalIgnoreCase) ||
               slot.Equals(SharedRepackDissolveMapSlot, StringComparison.OrdinalIgnoreCase) ||
               slot.Equals(SharedRepackFlowGuidSlot, StringComparison.OrdinalIgnoreCase);

        // Keep these additions scoped to shared repack; global AtlasChannels/downscale behavior is unchanged.
        private static bool IsSharedRepackAtlasSlot(VanillaCoverageMaterial info, string slot)
            => (slot.Equals(SharedRepackFlowMapSlot, StringComparison.OrdinalIgnoreCase) &&
                GetSharedRepackFlowReason(info) == null) ||
               AtlasChannels.Any(channel => channel.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase));

        private static string GetSharedRepackOutputSuffix(string slot)
        {
            if (slot.Equals(SharedRepackFlowMapSlot, StringComparison.OrdinalIgnoreCase))
                return SharedRepackFlowMapOutputSuffix;
            if (slot.Equals(SharedRepackFlowGuidSlot, StringComparison.OrdinalIgnoreCase))
                return SharedRepackFlowGuidOutputSuffix;
            if (slot.Equals(SharedRepackDissolveMapSlot, StringComparison.OrdinalIgnoreCase))
                return SharedRepackDissolveOutputSuffix;
            if (slot.Equals(SharedRepackEmissiveDistortionSlot, StringComparison.OrdinalIgnoreCase))
                return SharedRepackEmissiveOutputSuffix;
            return AtlasChannels.First(channel => channel.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase)).Suffix;
        }

        private static bool IsSharedRepackCommonTexture(string texture)
        {
            var path = Normalize(texture);
            return path.StartsWith(@"commontextures\", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains(@"\commontextures\", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSharedRepackFlowShader(string? shader)
            => shader?.Contains("flow_overlay", StringComparison.OrdinalIgnoreCase) == true ||
               shader?.Contains("emissive_flow_alpha", StringComparison.OrdinalIgnoreCase) == true;

        private static bool IsSharedRepackDissolveSlot(string slot)
            => slot.Equals(SharedRepackDissolveDetailSlot, StringComparison.OrdinalIgnoreCase) ||
               slot.Equals(SharedRepackDissolveMapSlot, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True when <paramref name="shader"/> is in the generated SharedRepackShaderBoundSlots table
        /// (Tools/ShaderChannels/shader_bound_slots.py) and its compiled variants of every stage bind no
        /// resource named like the t_xml_ <paramref name="slot"/>: the engine binds a material slot to the
        /// compiled resource of the same name, so such a slot is dead data. A shader that is not in the
        /// table, or a slot outside the t_xml_ namespace, is never reported unread.
        /// </summary>
        internal static bool IsSlotUnreadByShader(string? shader, string? slot)
        {
            if (string.IsNullOrWhiteSpace(shader) || string.IsNullOrWhiteSpace(slot))
                return false;

            slot = slot.Trim();
            if (!slot.StartsWith("t_xml_", StringComparison.OrdinalIgnoreCase))
                return false;

            return SharedRepackShaderBoundSlots.TryGetValue(
                       shader.Trim().Replace('\\', '/'), out var bound) &&
                   !bound.Contains(slot);
        }

        // The material parameters the repack rewrites and the coverage stage therefore keeps.
        internal static bool IsSharedRepackCompensatedParameter(string name)
            => SharedRepackEmissiveParameters.Contains(name, StringComparer.OrdinalIgnoreCase) ||
               name.Equals(SharedRepackDissolveScaleParameter, StringComparison.OrdinalIgnoreCase) ||
               SharedRepackFlowParameters.Contains(name, StringComparer.OrdinalIgnoreCase);

        internal static bool IsSharedRepackFlowParameter(string name)
            => SharedRepackFlowParameters.Contains(name, StringComparer.OrdinalIgnoreCase);

        internal static string? GetSharedRepackCompensatedParameterType(string name)
        {
            if (name.Equals(SharedRepackEmissiveStrengthParameter, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(SharedRepackFlowNoiseSpeedParameter, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(SharedRepackFlowNoiseTilingParameter, StringComparison.OrdinalIgnoreCase))
                return "float";
            if (SharedRepackEmissiveParameters.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                name.Equals(SharedRepackDissolveScaleParameter, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(SharedRepackFlowTextureTilingParameter, StringComparison.OrdinalIgnoreCase))
                return "float2";
            return null;
        }

        private static int GetSharedRepackEmissiveParameterComponents(string name)
            => name.Equals(SharedRepackEmissiveStrengthParameter, StringComparison.OrdinalIgnoreCase) ? 1 : 2;

        // "1,1" style float components: comma separated, invariant decimal point.
        private static bool TryParseSharedRepackFloats(string text, int count, out double[] values)
        {
            values = [];
            var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != count)
                return false;

            var parsed = new double[count];
            for (var index = 0; index < count; index++)
            {
                if (!double.TryParse(parts[index], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out parsed[index]) ||
                    !double.IsFinite(parsed[index]))
                {
                    return false;
                }
            }

            values = parsed;
            return true;
        }

        // Flow parameters use exact comma component counts: unlike the older shared parser, empty
        // components are malformed and must not be silently discarded.
        private static bool TryParseSharedRepackFlowFloats(string text, int count, out double[] values)
        {
            values = [];
            var parts = text.Split(',');
            if (parts.Length != count || parts.Any(part => string.IsNullOrWhiteSpace(part)))
                return false;

            var parsed = new double[count];
            for (var index = 0; index < count; index++)
            {
                if (!double.TryParse(parts[index].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out parsed[index]) ||
                    !double.IsFinite(parsed[index]))
                    return false;
            }

            values = parsed;
            return true;
        }

        // Null when the material's emissive scroll can be compensated for a UV0 scale (its shader is
        // verified and every parameter the compensation rewrites is present and parses), else why not.
        private static string? GetSharedRepackEmissiveReason(VanillaCoverageMaterial info)
        {
            if (!SharedRepackEmissiveVerifiedShaders.Contains(info.Shader))
            {
                return SharedRepackReasonEmissiveShader +
                       (info.Shader.Length == 0 ? "unknown shader" : info.Shader) + ")";
            }

            foreach (var name in SharedRepackEmissiveParameters)
            {
                if (info.Parameters == null ||
                    !info.Parameters.TryGetValue(name, out var text) ||
                    !TryParseSharedRepackFloats(text, GetSharedRepackEmissiveParameterComponents(name), out _))
                {
                    return SharedRepackReasonEmissiveParameter + name;
                }
            }

            return null;
        }

        // Null when the material's dissolve shader can be repacked (its shader is verified and the float2
        // dissolve_uv_scale the repack divides is present and parses), else why not.
        private static string? GetSharedRepackDissolveReason(VanillaCoverageMaterial info)
        {
            if (!SharedRepackDissolveVerifiedShaders.Contains(info.Shader))
            {
                return SharedRepackReasonDissolveShader +
                       (info.Shader.Length == 0 ? "unknown shader" : info.Shader) + ")";
            }

            if (info.Parameters == null ||
                !info.Parameters.TryGetValue(SharedRepackDissolveScaleParameter, out var text) ||
                !TryParseSharedRepackFloats(text, 2, out _))
            {
                return SharedRepackReasonDissolveParameter + SharedRepackDissolveScaleParameter;
            }

            return null;
        }

        // Null only for the eight verified weighted flow shaders with all three correctly typed finite parameters.
        private static string? GetSharedRepackFlowReason(VanillaCoverageMaterial info)
        {
            if (!SharedRepackFlowVerifiedShaders.Contains(info.Shader))
            {
                if (info.Shader.Contains("emissive_flow_alpha", StringComparison.OrdinalIgnoreCase))
                {
                    var detail = info.Shader.Contains("weighted2_emissive_flow_alpha", StringComparison.OrdinalIgnoreCase)
                        ? "; weighted2 also samples flow_texture from UV0 without a compensatable tiling parameter"
                        : string.Empty;
                    return "the emissive-flow shader has a fixed flow-noise scroll speed that cannot be compensated" + detail;
                }

                return SharedRepackReasonFlowShader +
                       (info.Shader.Length == 0 ? "unknown shader" : info.Shader) + ")";
            }

            foreach (var name in SharedRepackFlowParameters)
            {
                var components = name.Equals(SharedRepackFlowTextureTilingParameter, StringComparison.OrdinalIgnoreCase) ? 2 : 1;
                if (info.Parameters == null ||
                    !info.Parameters.TryGetValue(name, out var text) ||
                    !TryParseSharedRepackFlowFloats(text, components, out _))
                    return SharedRepackReasonFlowParameter + name + " (" + GetSharedRepackCompensatedParameterType(name) + ")";
            }

            return null;
        }

        // Emissive and dissolve noise are copies. Flow GUID is also a noise copy; a commontextures
        // flow map is copied as a chart-aligned shared texture. Model-local flow maps join closure.
        private static bool IsSharedRepackCopySlot(VanillaCoverageMaterial info, string slot, string texture)
            => (slot.Equals(SharedRepackEmissiveDistortionSlot, StringComparison.OrdinalIgnoreCase) &&
                GetSharedRepackEmissiveReason(info) == null) ||
               (slot.Equals(SharedRepackDissolveMapSlot, StringComparison.OrdinalIgnoreCase) &&
                GetSharedRepackDissolveReason(info) == null) ||
               ((slot.Equals(SharedRepackFlowGuidSlot, StringComparison.OrdinalIgnoreCase) ||
                 (slot.Equals(SharedRepackFlowMapSlot, StringComparison.OrdinalIgnoreCase) &&
                  IsSharedRepackCommonTexture(texture))) &&
                GetSharedRepackFlowReason(info) == null);

        // The verified shader samples noise UV=(UV0 + c*time*flow_noise_speed)*flow_noise_tiling,
        // and flow normals use UV0*flow_texture_tiling plus flow/noise-derived offsets. Equal-axis
        // s preserves their density/speed; chart phase can still change the derived flow result.
        private static bool TryScaleSharedRepackFlowParameters(
            XmlDocument material,
            double scaleU,
            double scaleV,
            out string description)
        {
            description = string.Empty;
            if (!double.IsFinite(scaleU) || !double.IsFinite(scaleV) || scaleU <= 0 || scaleV <= 0 ||
                !(Math.Abs(scaleU - scaleV) <= 1e-9 * Math.Max(Math.Abs(scaleU), Math.Abs(scaleV))))
            {
                description = $"the UV scale is {scaleU:0.#####} x {scaleV:0.#####}, not equal in U and V";
                return false;
            }

            var speedNode = FindSharedRepackParameter(material, SharedRepackFlowNoiseSpeedParameter, "float")?.SelectSingleNode("value");
            if (speedNode == null || !TryParseSharedRepackFlowFloats(speedNode.InnerText, 1, out var speed))
            {
                description = "the material lacks a usable flow parameter " + SharedRepackFlowNoiseSpeedParameter;
                return false;
            }
            var noiseTilingNode = FindSharedRepackParameter(material, SharedRepackFlowNoiseTilingParameter, "float")?.SelectSingleNode("value");
            if (noiseTilingNode == null || !TryParseSharedRepackFlowFloats(noiseTilingNode.InnerText, 1, out var noiseTiling))
            {
                description = "the material lacks a usable flow parameter " + SharedRepackFlowNoiseTilingParameter;
                return false;
            }
            var textureTilingNode = FindSharedRepackParameter(material, SharedRepackFlowTextureTilingParameter, "float2")?.SelectSingleNode("value");
            if (textureTilingNode == null || !TryParseSharedRepackFlowFloats(textureTilingNode.InnerText, 2, out var textureTiling))
            {
                description = "the material lacks a usable flow parameter " + SharedRepackFlowTextureTilingParameter;
                return false;
            }

            var newSpeed = speed[0] * scaleU;
            var newNoiseTiling = noiseTiling[0] / scaleU;
            var newTextureTiling = new[] { textureTiling[0] / scaleU, textureTiling[1] / scaleU };
            if (!double.IsFinite(newSpeed) || !double.IsFinite(newNoiseTiling) ||
                newTextureTiling.Any(value => !double.IsFinite(value)))
            {
                description = "flow parameter compensation produced a non-finite value";
                return false;
            }

            var speedText = FormatSharedRepackFloats(newSpeed);
            var noiseTilingText = FormatSharedRepackFloats(newNoiseTiling);
            var textureTilingText = FormatSharedRepackFloats(newTextureTiling);
            description = $"UV scale {scaleU:0.#####}: {SharedRepackFlowNoiseSpeedParameter} " +
                          $"{FormatSharedRepackFloats(speed)} -> {speedText}, " +
                          $"{SharedRepackFlowNoiseTilingParameter} {FormatSharedRepackFloats(noiseTiling)} -> {noiseTilingText}, " +
                          $"{SharedRepackFlowTextureTilingParameter} {FormatSharedRepackFloats(textureTiling)} -> {textureTilingText}";
            speedNode.InnerText = speedText;
            noiseTilingNode.InnerText = noiseTilingText;
            textureTilingNode.InnerText = textureTilingText;
            return true;
        }

        private static bool TryScaleSharedRepackBloodParameter(
            XmlDocument material,
            double scaleU,
            double scaleV,
            out string description)
            => TryScaleSharedRepackFloat2Parameter(
                material, SharedRepackBloodScaleParameter, scaleU, scaleV, out description);

        // Divides a float2 material parameter per axis by the page's UV scale (parameters sampled as
        // UV0 * parameter keep their density on the mesh).
        private static bool TryScaleSharedRepackFloat2Parameter(
            XmlDocument material,
            string name,
            double scaleU,
            double scaleV,
            out string description)
        {
            description = string.Empty;
            var parameter = material.SelectNodes("/material/params/param")?.Cast<XmlNode>()
                .FirstOrDefault(node =>
                    node.SelectSingleNode("name")?.InnerText.Trim().Equals(
                        name, StringComparison.OrdinalIgnoreCase) == true &&
                    node.SelectSingleNode("type")?.InnerText.Trim().Equals(
                        "float2", StringComparison.OrdinalIgnoreCase) == true);
            var valueNode = parameter?.SelectSingleNode("value");
            if (valueNode == null)
                return false;

            var parts = valueNode.InnerText.Split(
                ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var u) ||
                !double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                return false;
            }

            var newU = u / scaleU;
            var newV = v / scaleV;
            var text = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{newU:R},{newV:R}");
            valueNode.InnerText = text;
            description = $"{valueNode.InnerText} (was {parts[0]},{parts[1]})";
            return true;
        }

        private static XmlNode? FindSharedRepackParameter(XmlDocument material, string name, string type)
            => material.SelectNodes("/material/params/param")?.Cast<XmlNode>()
                .FirstOrDefault(node =>
                    node.SelectSingleNode("name")?.InnerText.Trim().Equals(name, StringComparison.OrdinalIgnoreCase) == true &&
                    node.SelectSingleNode("type")?.InnerText.Trim().Equals(type, StringComparison.OrdinalIgnoreCase) == true);

        // New emissive scroll parameters for a UV0 scale s (equal in U and V). The shaders sample the
        // glow at (UV0 + d * strength + time * speed * direction) * tiling; with UV0' = s * UV0 + offset
        // the same glow needs tiling / s, strength * s and direction * s (the offset only shifts the
        // pattern's phase per chart).
        internal static (double[] Tiling, double Strength, double[] Direction) ComputeSharedRepackEmissiveParameters(
            double[] tiling,
            double strength,
            double[] direction,
            double scale)
            => ([tiling[0] / scale, tiling[1] / scale], strength * scale, [direction[0] * scale, direction[1] * scale]);

        private static string FormatSharedRepackFloats(params double[] values)
            => string.Join(",", values.Select(value => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));

        // Rewrites the material's emissive scroll parameters for the page's UV0 scale. False (with a
        // message) when a parameter is missing, has another type or does not parse, or the scales differ.
        private static bool TryScaleSharedRepackEmissiveParameters(
            XmlDocument material,
            double scaleU,
            double scaleV,
            out string description)
        {
            description = string.Empty;
            if (!(Math.Abs(scaleU - scaleV) <= 1e-9 * Math.Max(Math.Abs(scaleU), Math.Abs(scaleV))) || scaleU <= 0)
            {
                description = $"the UV scale is {scaleU:0.#####} x {scaleV:0.#####}, not equal in U and V";
                return false;
            }

            var nodes = new Dictionary<string, XmlNode>(StringComparer.OrdinalIgnoreCase);
            var values = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in SharedRepackEmissiveParameters)
            {
                var components = GetSharedRepackEmissiveParameterComponents(name);
                var node = FindSharedRepackParameter(material, name, components == 1 ? "float" : "float2");
                var valueNode = node?.SelectSingleNode("value");
                if (valueNode == null || !TryParseSharedRepackFloats(valueNode.InnerText, components, out var parsed))
                {
                    description = $"the material lacks a usable emissive parameter {name}";
                    return false;
                }

                nodes[name] = valueNode;
                values[name] = parsed;
            }

            var (tiling, strength, direction) = ComputeSharedRepackEmissiveParameters(
                values[SharedRepackEmissiveTilingParameter],
                values[SharedRepackEmissiveStrengthParameter][0],
                values[SharedRepackEmissiveDirectionParameter],
                scaleU);
            var tilingText = FormatSharedRepackFloats(tiling);
            var strengthText = FormatSharedRepackFloats(strength);
            var directionText = FormatSharedRepackFloats(direction);
            description = $"UV scale {scaleU:0.#####}: {SharedRepackEmissiveTilingParameter} " +
                          $"{FormatSharedRepackFloats(values[SharedRepackEmissiveTilingParameter])} -> {tilingText}, " +
                          $"{SharedRepackEmissiveStrengthParameter} {FormatSharedRepackFloats(values[SharedRepackEmissiveStrengthParameter])} -> {strengthText}, " +
                          $"{SharedRepackEmissiveDirectionParameter} {FormatSharedRepackFloats(values[SharedRepackEmissiveDirectionParameter])} -> {directionText}";
            nodes[SharedRepackEmissiveTilingParameter].InnerText = tilingText;
            nodes[SharedRepackEmissiveStrengthParameter].InnerText = strengthText;
            nodes[SharedRepackEmissiveDirectionParameter].InnerText = directionText;
            return true;
        }

        private static SharedRepackPart CreateSharedRepackPart(
            BatchState state,
            VanillaTextureCoverageResult coverage,
            RmvFile rigid,
            MeshKey key,
            List<SourceWsModelBinding> bindings,
            HashSet<string> directConsumers,
            HashSet<string> unusedWsModels)
        {
            var part = new SharedRepackPart { Key = key };
            part.Bindings.AddRange(bindings);
            part.UnusedWsModels.UnionWith(unusedWsModels);
            foreach (var binding in bindings)
            {
                var material = Normalize(binding.Material);
                part.Materials.Add(material);
                if (!coverage.Materials.TryGetValue(material, out var info) || !info.Found || !info.Readable)
                {
                    part.Reason ??= SharedRepackReasonMaterialUnreadable;
                    continue;
                }

                if (state.Source.ContainsFile(material) && OverridesVanillaPath(state, material))
                    part.Reason ??= SharedRepackReasonMaterialOverridesVanilla;
                if (state.Consumers?.TryBlockVanillaReplaced(material, "repack") == true)
                    part.Reason ??= "a bound material is a vanilla file another mod replaces";

                foreach (var (unreadSlot, _) in info.Textures)
                {
                    if (!IsSharedRepackIgnoredTexture(unreadSlot, string.Empty) &&
                        IsSlotUnreadByShader(info.Shader, unreadSlot))
                    {
                        part.UnreadSlots.Add((material, unreadSlot.ToLowerInvariant()));
                    }
                }

                if (info.Textures
                        .Where(item => !IsSlotUnreadByShader(info.Shader, item.Slot))
                        .GroupBy(item => item.Slot, StringComparer.OrdinalIgnoreCase)
                        .Any(group => group
                            .Select(item => Normalize(item.Texture))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count() > 1))
                {
                    part.Reason ??= SharedRepackReasonDuplicateSlot;
                }

                // This material's slot -> texture map (the part's Slots are chosen among them below).
                var map = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                part.MaterialSlots[material] = map;
                var hasDistortion = info.Textures.Any(item =>
                    item.Slot.Equals(SharedRepackEmissiveDistortionSlot, StringComparison.OrdinalIgnoreCase) &&
                    !IsSharedRepackIgnoredTexture(info, item.Slot, item.Texture));
                var emissiveReason = GetSharedRepackEmissiveReason(info);
                var dissolveReason = GetSharedRepackDissolveReason(info);
                var flowReason = GetSharedRepackFlowReason(info);
                if (IsSharedRepackFlowShader(info.Shader))
                {
                    if (flowReason == null)
                        part.FlowCompensationMaterials.Add(material);
                    else
                        part.Reason ??= flowReason;
                }
                foreach (var (slot, texture) in info.Textures)
                {
                    if (IsSharedRepackIgnoredTexture(info, slot, texture))
                        continue;

                    var isDistortion = slot.Equals(SharedRepackEmissiveDistortionSlot, StringComparison.OrdinalIgnoreCase);
                    var isEmissiveTexture = slot.Equals(SharedRepackEmissiveTextureSlot, StringComparison.OrdinalIgnoreCase);
                    var isDissolveMap = slot.Equals(SharedRepackDissolveMapSlot, StringComparison.OrdinalIgnoreCase);
                    var isFlowMap = slot.Equals(SharedRepackFlowMapSlot, StringComparison.OrdinalIgnoreCase);
                    var isFlowGuid = slot.Equals(SharedRepackFlowGuidSlot, StringComparison.OrdinalIgnoreCase);
                    if (((isDistortion || isEmissiveTexture) && emissiveReason == null && hasDistortion) ||
                        (isDissolveMap && dissolveReason == null) ||
                        IsSharedRepackCopySlot(info, slot, texture))
                    {
                        // The glow texture stays as authored (not part of the set or a tuple); the
                        // noise (distortion, dissolve map) is cropped like a slot but never joins the closure.
                        if (isEmissiveTexture)
                            continue;

                        var noiseName = isDissolveMap ? "dissolve map" : isFlowGuid ? "flow GUID" :
                            isFlowMap ? "shared flow map" : "emissive distortion";
                        var noise = Normalize(texture);
                        if (!coverage.Entries.TryGetValue(noise, out var noiseEntry) || noiseEntry.Layout == null)
                        {
                            part.Reason ??= isFlowGuid || isFlowMap
                                ? SharedRepackReasonFlowTexture + " (" + noiseName + " header unreadable)"
                                : $"the {noiseName} texture cannot be read";
                        }
                        else if (GetSharedRepackBlockedTextureReason(noiseEntry.Layout) is { } blocked)
                        {
                            part.Reason ??= $"the {noiseName} texture cannot be copied: " + blocked;
                        }

                        part.CopySlots.Add(slot);
                        // (A slot listed twice with different sources in one material is refused above.)
                        map.TryAdd(slot, noise);
                        continue;
                    }

                    part.Textures.Add(Normalize(texture));
                    if (!IsTextureDownscaleSlot(slot) && !IsSharedRepackAtlasSlot(info, slot))
                    {
                        part.Reason ??= isDistortion || isEmissiveTexture
                            ? emissiveReason ?? SharedRepackReasonSlot + slot.ToLowerInvariant()
                            : IsSharedRepackDissolveSlot(slot)
                                ? dissolveReason ?? SharedRepackReasonSlot + slot.ToLowerInvariant()
                                : isFlowMap || isFlowGuid || SharedRepackFlowPassThroughSlots.Contains(slot, StringComparer.OrdinalIgnoreCase)
                                    ? flowReason ?? SharedRepackReasonSlot + slot.ToLowerInvariant()
                                    : SharedRepackReasonSlot + slot.ToLowerInvariant();
                        continue;
                    }

                    map.TryAdd(slot, Normalize(texture));
                }
            }

            // The bound materials may name different textures in the same slots (texture variants): the
            // layout map is the first distinct map by text, the others are variants. Bindings that do
            // not have the same slots disagree on more than textures.
            var maps = part.MaterialSlots.Values
                .Select(map => (Key: FormatSharedRepackSlotMap(map), Map: map))
                .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToList();
            var flowMapKinds = part.MaterialSlots.Values
                .Where(map => map.ContainsKey(SharedRepackFlowMapSlot))
                .Select(map => IsSharedRepackCommonTexture(map[SharedRepackFlowMapSlot]))
                .Distinct()
                .ToList();
            if (flowMapKinds.Count > 1)
                part.Reason ??= "the part's flow-map variants mix shared commontextures copies with model-local atlas members";

            if (maps.Count != 0)
            {
                foreach (var (slot, texture) in maps[0].Map)
                    part.Slots[slot] = texture;
                foreach (var (_, map) in maps.Skip(1))
                {
                    if (!map.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(part.Slots.Keys))
                    {
                        part.Reason ??= SharedRepackReasonSlotSubset;
                        continue;
                    }

                    if (map.Any(pair => part.CopySlots.Contains(pair.Key) &&
                                        !pair.Value.Equals(part.Slots[pair.Key], StringComparison.OrdinalIgnoreCase)))
                    {
                        part.Reason ??= SharedRepackReasonVariantCopySlot;
                    }

                    part.VariantSlots.Add(map);
                }
            }

            if (key.LodIndex < 0 || key.LodIndex >= rigid.ModelList.Length ||
                key.PartIndex < 0 || key.PartIndex >= rigid.ModelList[key.LodIndex].Length)
            {
                part.Reason ??= SharedRepackReasonBadPart;
                return part;
            }

            part.Model = rigid.ModelList[key.LodIndex][key.PartIndex];
            var shared = rigid.ModelList
                .SelectMany(lod => lod)
                .Count(model => ReferenceEquals(model, part.Model));
            if (shared != 1)
                part.Reason ??= SharedRepackReasonBadPart;

            if (directConsumers.Count != 0)
            {
                part.DirectEmbeddedMaterial = true;
                part.DirectConsumers.UnionWith(directConsumers);
                if (part.VariantSlots.Count != 0)
                    part.Reason ??= SharedRepackReasonVariantDirect;
                var embedded = part.Model.Material;
                if (embedded == null)
                {
                    part.Reason ??= SharedRepackReasonEmbeddedUnreadable;
                }
                else
                {
                    part.EmbeddedMaterialType = embedded.MaterialId;
                    if (!IsSharedRepackVerifiedEmbeddedType(embedded.MaterialId))
                    {
                        part.Reason ??= $"{SharedRepackReasonEmbeddedTypePrefix}{embedded.MaterialId} is not verified (only weighted and default_type are)";
                    }
                    else if (!IsSharedRepackVerifiedEmbeddedVertexFormat(embedded.BinaryVertexFormat))
                    {
                        part.Reason ??= $"{SharedRepackReasonEmbeddedVertexFormatPrefix}{embedded.BinaryVertexFormat} is not verified (only Static, Weighted and Cinematic are)";
                    }

                    List<RmvTexture> textures;
                    try
                    {
                        textures = embedded.GetAllTextures();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        textures = [];
                        part.Reason ??= SharedRepackReasonEmbeddedUnreadable;
                    }

                    if (textures.GroupBy(texture => texture.TexureType).Any(group => group.Count() > 1))
                        part.Reason ??= SharedRepackReasonEmbeddedUnsupported;

                    var wsSlots = new SortedDictionary<string, string>(part.Slots, StringComparer.OrdinalIgnoreCase);
                    var embeddedSlots = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var texture in textures)
                    {
                        var texturePath = Normalize(texture.Path);
                        if (texturePath.Length == 0)
                            continue;

                        part.EmbeddedTextures[texture.TexureType] = texturePath;
                        if (TryGetSharedRepackEmbeddedSlot(texture.TexureType, out var slot))
                        {
                            if (IsSharedRepackIgnoredTexture(slot, texturePath))
                                continue;

                            part.Textures.Add(texturePath);
                            if (embeddedSlots.TryGetValue(slot, out var old) &&
                                !old.Equals(texturePath, StringComparison.OrdinalIgnoreCase))
                            {
                                part.Reason ??= SharedRepackReasonEmbeddedUnsupported;
                            }
                            else
                            {
                                embeddedSlots[slot] = texturePath;
                            }
                        }
                        else if (IsSharedRepackVerifiedEmbeddedType(embedded.MaterialId) &&
                                 texture.TexureType is TextureType.Diffuse or TextureType.Specular or TextureType.Gloss)
                        {
                            // These legacy weighted entries are never sampled by the supported shaders.
                        }
                        else
                        {
                            part.Textures.Add(texturePath);
                            part.Reason ??= SharedRepackReasonEmbeddedUnsupported;
                        }
                    }

                    if (bindings.Count != 0)
                    {
                        var slotsMatch = wsSlots.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                                .SetEquals(embeddedSlots.Keys) &&
                            wsSlots.All(pair => embeddedSlots.TryGetValue(pair.Key, out var value) &&
                                                value.Equals(pair.Value, StringComparison.OrdinalIgnoreCase));
                        if (!slotsMatch)
                            part.Reason ??= SharedRepackReasonEmbeddedWsMismatch;
                    }
                    else
                    {
                        foreach (var (slot, texturePath) in embeddedSlots)
                            part.Slots[slot] = texturePath;
                    }
                }
            }

            if (!coverage.PartData.ContainsKey((key.GeometryPath, key.LodIndex, key.PartIndex)))
                part.Reason ??= SharedRepackReasonNoCoverage;
            return part;
        }

        // Tile of every vertex and its tile-local UV. Null when fine, else why the part cannot
        // move: a triangle must sit inside one integer UV tile (with an epsilon), and a vertex
        // must not belong to triangles of two tiles.
        private static string? AnalyzeSharedRepackGeometry(SharedRepackPart part)
        {
            part.GeometryChecked = true;
            var vertices = part.Model?.Mesh?.VertexList;
            var indices = part.Model?.Mesh?.IndexList;
            if (vertices == null || indices == null || indices.Length < 3 || indices.Length % 3 != 0)
                return "the part has no usable triangle list";

            var count = vertices.Length;
            part.U = new double[count];
            part.V = new double[count];
            part.TileU = new int[count];
            part.TileV = new int[count];
            part.Used = new bool[count];
            part.VertexChart = new int[count];
            Array.Fill(part.VertexChart, -1);
            var epsilon = VanillaCoverageTileEpsilon;
            for (var index = 0; index < indices.Length; index += 3)
            {
                int ia = indices[index], ib = indices[index + 1], ic = indices[index + 2];
                if (ia >= count || ib >= count || ic >= count)
                    return "an index is out of range";

                var a = vertices[ia].Uv;
                var b = vertices[ib].Uv;
                var c = vertices[ic].Uv;
                double[] us = [a.X, b.X, c.X];
                double[] vs = [a.Y, b.Y, c.Y];
                if (us.Concat(vs).Any(value => !double.IsFinite(value) || Math.Abs(value) > VanillaCoverageMaximumUv))
                    return "a UV is not finite or absurdly large";

                var tileU = (int)Math.Floor((us[0] + us[1] + us[2]) / 3.0);
                var tileV = (int)Math.Floor((vs[0] + vs[1] + vs[2]) / 3.0);
                if (us.Any(value => value < tileU - epsilon || value > tileU + 1 + epsilon) ||
                    vs.Any(value => value < tileV - epsilon || value > tileV + 1 + epsilon))
                {
                    return "a triangle spans more than one UV tile";
                }

                int[] corners = [ia, ib, ic];
                for (var corner = 0; corner < 3; corner++)
                {
                    var vertex = corners[corner];
                    if (part.Used[vertex])
                    {
                        if (part.TileU[vertex] != tileU || part.TileV[vertex] != tileV)
                            return "a vertex is shared by triangles of different UV tiles";
                    }
                    else
                    {
                        part.Used[vertex] = true;
                        part.TileU[vertex] = tileU;
                        part.TileV[vertex] = tileV;
                        part.U[vertex] = Math.Clamp(us[corner] - tileU, 0, 1);
                        part.V[vertex] = Math.Clamp(vs[corner] - tileV, 0, 1);
                    }
                }
            }

            return null;
        }

        // ---------------------------------------------------------------------------
        // Planning one set: closure, tuples, charts, layout groups, pages, gates.
        // ---------------------------------------------------------------------------

        private string DescribeSharedRepackOutsideTexture(
            BatchState state,
            VanillaTextureCoverageResult coverage,
            HashSet<string> setRigids,
            string texture,
            Dictionary<string, UnitRepackSignatureSet> setByTexture,
            Func<string, string> describeSetState,
            Dictionary<string, string> removedBecause,
            HashSet<string> blocked,
            Dictionary<string, VanillaCoverageTexture> entryOf)
        {
            if (blocked.Contains(texture))
            {
                return "cannot be repacked (" +
                       (GetSharedRepackBlockedTextureReason(entryOf[texture].Layout) ??
                        "an owned override of a vanilla texture") + ")";
            }

            if (removedBecause.TryGetValue(texture, out var because))
                return $"left the set when {because}";
            if (!coverage.Entries.TryGetValue(texture, out var entry))
                return "is not a texture the analysis knows";

            var others = entry.ModParts.Concat(entry.VanillaParts)
                .Select(part => part.Rigid)
                .Where(path => !setRigids.Contains(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var text = new List<string>();
            if (setByTexture.TryGetValue(texture, out var set))
            {
                text.Add($"is in a different signature set ({set.Units.Length:N0} unit(s), " +
                         $"{set.Items.Count:N0} texture(s)){describeSetState(texture)}");
            }
            else if (!entry.IsRepackable)
            {
                text.Add($"is not repackable ({entry.NotCroppableReason ?? "unmeasured"})");
            }

            if (others.Count != 0)
            {
                text.Add($"is also used by rigid {others[0]}" +
                         (others.Count > 1 ? $" and {others.Count - 1:N0} more" : string.Empty));
            }

            return text.Count == 0 ? "is outside the set" : string.Join(" and ", text);
        }

        private static string DescribeSharedRepackPart(SharedRepackPart part)
            => $"rigid {part.Key.GeometryPath} lod {part.Key.LodIndex} part {part.Key.PartIndex}";

        private static string GetSharedRepackSetKey(UnitRepackSignatureSet set)
            => string.Join(",", set.Units);

        // Plans the seed set of the context. With merging on, a part that is otherwise
        // eligible but uses a texture of another, still unprocessed signature set pulls that
        // set (all its textures and users, in every rigid) into the plan, up to the cap.
        private SharedRepackSetPlan? PlanSharedRepackSet(
            BatchState state,
            SharedTextureRepackResult result,
            VanillaTextureCoverageResult coverage,
            SharedRepackMergeContext merge,
            string label,
            Dictionary<string, UnitRepackSignatureSet> setByTexture,
            Func<string, RmvFile, int, int, SharedRepackPart> getPart,
            Func<string, bool> isUniform,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, int> paddingLimits,
            out string? failure,
            out string failureBucket)
        {
            failure = null;
            failureBucket = "closure";
            var rigids = merge.Rigids;
            var slotOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var entryOf = new Dictionary<string, VanillaCoverageTexture>(StringComparer.OrdinalIgnoreCase);
            var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var setRigids = rigids.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var inComponent = new HashSet<int> { merge.Seed };
            var cappedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            merge.Component.Add(merge.Decisions[merge.Seed]);

            // The parts of the eligible rigids. An ineligible rigid cannot be rewritten, so
            // each of its parts that uses a texture of the set is a candidate that is dropped
            // up front (it only knows the set's textures it uses, which is all a drop needs).
            var candidates = new List<SharedRepackPart>();
            var droppedParts = new List<SharedRepackPart>();
            var ineligible = new Dictionary<string, SharedRepackSetRigid>(StringComparer.OrdinalIgnoreCase);
            var pseudoParts = new Dictionary<string, SortedDictionary<(int Lod, int Part), SharedRepackPart>>(
                StringComparer.OrdinalIgnoreCase);

            string? BlockedTextureReason(VanillaCoverageTexture entry, string path)
            {
                var layoutReason = GetSharedRepackBlockedTextureReason(merge.Effective(entry).Layout);
                if (layoutReason != null)
                    return "the texture cannot be repacked: " + layoutReason;
                if (entry.IsOwned && OverridesVanillaPath(state, path))
                    return "the texture is an owned override of a vanilla texture";
                if (entry.IsOwned && state.Consumers?.TryBlock(Normalize(path), "repack") == true)
                    return "the texture is used by another enabled mod";
                if (state.Consumers?.TryBlockVanillaReplaced(Normalize(path), "repack") == true)
                    return "another enabled mod replaces the vanilla texture";
                return null;
            }

            bool IsBlockedTexture(VanillaCoverageTexture entry, string path)
                => BlockedTextureReason(entry, path) != null;

            void NoteRemoved(string texture, string reason)
            {
                if (entryOf.TryGetValue(texture, out var removedEntry))
                    merge.RemovedTextures.TryAdd(texture, (reason, removedEntry.Layout?.ChainBytes ?? 0));
            }

            void AddRigid(SharedRepackSetRigid item)
            {
                setRigids.Add(item.Path);
                if (item.Rigid != null)
                    candidates.AddRange(item.Parts.Select(key => getPart(item.Path, item.Rigid!, key.Lod, key.Part)));
                else
                    ineligible[item.Path] = item;
            }

            void AddSet(UnitRepackSetDecision added)
            {
                foreach (var item in added.Set.Items)
                {
                    var path = Normalize(item.Entry.Path);
                    var effectiveEntry = merge.Effective(item.Entry);
                    if (!entryOf.ContainsKey(path))
                        merge.PlanTextureBytes += effectiveEntry.Layout?.ChainBytes ?? 0;
                    slotOf[path] = item.Slot;
                    entryOf[path] = effectiveEntry;
                    // Textures that can never be copied exactly stay out of the set.
                    var blockedReason = BlockedTextureReason(item.Entry, path);
                    if (blockedReason != null)
                    {
                        blocked.Add(path);
                        NoteRemoved(path, blockedReason);
                    }
                    else
                    {
                        members.Add(path);
                    }
                }

                foreach (var rigidPath in added.Set.Items
                             .SelectMany(item => item.Entry.ModParts.Concat(item.Entry.VanillaParts))
                             .Select(part => part.Rigid)
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(path => path, StringComparer.Ordinal))
                {
                    if (!setRigids.Contains(rigidPath))
                    {
                        var item = merge.MakeRigid(rigidPath);
                        rigids.Add(item);
                        AddRigid(item);
                    }
                }

                // The generated cross-rigid rigids whose materials use a texture of the set are
                // users too: they join the plan and are rewritten with the same charts.
                var addedTextures = added.Set.Items
                    .Select(item => Normalize(item.Entry.Path))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var generatedPath in merge.GeneratedUsers
                             .Where(user => user.Textures.Overlaps(addedTextures))
                             .Select(user => user.Rigid)
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(path => path, StringComparer.Ordinal))
                {
                    if (!setRigids.Contains(generatedPath))
                    {
                        var item = merge.MakeRigid(generatedPath);
                        rigids.Add(item);
                        AddRigid(item);
                    }
                }

                // The set's users in the ineligible rigids.
                if (ineligible.Count != 0)
                {
                    foreach (var item in added.Set.Items.OrderBy(item => Normalize(item.Entry.Path), StringComparer.Ordinal))
                    {
                        foreach (var key in item.Entry.ModParts.Concat(item.Entry.VanillaParts))
                        {
                            if (!ineligible.TryGetValue(key.Rigid, out var bad))
                                continue;

                            // An atlased part uses the atlas, not this texture: it is not a user
                            // that has to be rewritten (or that keeps the texture loaded). The
                            // rigid's other parts still are, and are dropped below.
                            if (state.AtlasChangedParts.Contains((Normalize(key.Rigid), key.Lod, key.Part)))
                            {
                                result.AtlasedUsersIgnored++;
                                continue;
                            }

                            if (!pseudoParts.TryGetValue(bad.Path, out var byKey))
                            {
                                byKey = [];
                                pseudoParts[bad.Path] = byKey;
                            }

                            if (!byKey.TryGetValue((key.Lod, key.Part), out var part))
                            {
                                part = new SharedRepackPart
                                {
                                    Key = new MeshKey(bad.Path, key.Lod, key.Part),
                                    Reason = SharedRepackReasonRigid + bad.Reason,
                                };
                                byKey[(key.Lod, key.Part)] = part;
                                candidates.Add(part);
                            }

                            part.Textures.Add(Normalize(item.Entry.Path));
                        }
                    }
                }

                // Users in ineligible generated rigids (those of eligible ones are candidates).
                if (ineligible.Count != 0)
                {
                    foreach (var user in merge.GeneratedUsers)
                    {
                        if (!ineligible.TryGetValue(user.Rigid, out var bad) || !user.Textures.Overlaps(addedTextures))
                            continue;
                        if (state.AtlasChangedParts.Contains((Normalize(user.Rigid), user.Lod, user.Part)))
                        {
                            result.AtlasedUsersIgnored++;
                            continue;
                        }

                        if (!pseudoParts.TryGetValue(bad.Path, out var byKey))
                        {
                            byKey = [];
                            pseudoParts[bad.Path] = byKey;
                        }

                        if (!byKey.TryGetValue((user.Lod, user.Part), out var part))
                        {
                            part = new SharedRepackPart
                            {
                                Key = new MeshKey(bad.Path, user.Lod, user.Part),
                                Reason = SharedRepackReasonRigid + bad.Reason,
                            };
                            byKey[(user.Lod, user.Part)] = part;
                            candidates.Add(part);
                        }

                        part.Textures.UnionWith(user.Textures.Where(addedTextures.Contains));
                    }
                }
            }

            foreach (var item in rigids)
                AddRigid(item);
            AddSet(merge.Decisions[merge.Seed]);

            var flowMapClosureMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void IncludeFlowMapUsers(string texture)
            {
                if (!coverage.Entries.TryGetValue(texture, out var flowEntry))
                    return;

                void AddPseudoUse(string rigidPath, int lod, int partIndex, string reason)
                {
                    if (!pseudoParts.TryGetValue(rigidPath, out var byKey))
                    {
                        byKey = [];
                        pseudoParts[rigidPath] = byKey;
                    }
                    if (!byKey.TryGetValue((lod, partIndex), out var pseudo))
                    {
                        pseudo = new SharedRepackPart
                        {
                            Key = new MeshKey(rigidPath, lod, partIndex),
                            Reason = SharedRepackReasonRigid + reason,
                        };
                        byKey[(lod, partIndex)] = pseudo;
                        candidates.Add(pseudo);
                    }
                    pseudo.Textures.Add(texture);
                }

                foreach (var use in flowEntry.ModParts.Concat(flowEntry.VanillaParts)
                             .OrderBy(use => use.Rigid, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(use => use.Lod).ThenBy(use => use.Part))
                {
                    if (state.AtlasChangedParts.Contains((Normalize(use.Rigid), use.Lod, use.Part)))
                    {
                        result.AtlasedUsersIgnored++;
                        continue;
                    }
                    if (!setRigids.Contains(use.Rigid))
                    {
                        var rigid = merge.MakeRigid(use.Rigid);
                        rigids.Add(rigid);
                        AddRigid(rigid);
                    }
                    if (ineligible.TryGetValue(use.Rigid, out var bad))
                        AddPseudoUse(use.Rigid, use.Lod, use.Part, bad.Reason ?? "the rigid cannot be rewritten");
                }

                foreach (var use in merge.GeneratedUsers.Where(user => user.Textures.Contains(texture))
                             .OrderBy(user => user.Rigid, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(user => user.Lod).ThenBy(user => user.Part))
                {
                    if (state.AtlasChangedParts.Contains((Normalize(use.Rigid), use.Lod, use.Part)))
                    {
                        result.AtlasedUsersIgnored++;
                        continue;
                    }
                    if (!setRigids.Contains(use.Rigid))
                    {
                        var rigid = merge.MakeRigid(use.Rigid);
                        rigids.Add(rigid);
                        AddRigid(rigid);
                    }
                    if (ineligible.TryGetValue(use.Rigid, out var bad))
                        AddPseudoUse(use.Rigid, use.Lod, use.Part, bad.Reason ?? "the generated rigid cannot be rewritten");
                }
            }

            void IncludeVerifiedFlowMapMembers()
            {
                var candidatesToInspect = candidates
                    .Where(part => !droppedParts.Contains(part) && part.FlowCompensationMaterials.Count != 0)
                    .SelectMany(part => part.MaterialSlots.Values)
                    .Where(map => map.ContainsKey(SharedRepackFlowMapSlot))
                    .Select(map => Normalize(map[SharedRepackFlowMapSlot]))
                    .Where(texture => !IsSharedRepackCommonTexture(texture) && !isUniform(texture))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(texture => texture, StringComparer.Ordinal)
                    .ToList();

                foreach (var texture in candidatesToInspect)
                {
                    if (flowMapClosureMembers.Contains(texture))
                        continue;
                    var owners = candidates.Where(part => part.MaterialSlots.Values.Any(map =>
                            map.TryGetValue(SharedRepackFlowMapSlot, out var value) &&
                            Normalize(value).Equals(texture, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    if (!coverage.Entries.TryGetValue(texture, out var entry) || entry.Layout == null)
                    {
                        flowMapClosureMembers.Add(texture);
                        blocked.Add(texture);
                        members.Remove(texture);
                        foreach (var owner in owners)
                            owner.Reason ??= SharedRepackReasonFlowTexture + " (header unreadable)";
                        continue;
                    }

                    var block = BlockedTextureReason(entry, texture);
                    if (entry.UnassessableUses != 0 || entry.NonMaterialReference != null)
                        block ??= "the flow map has a use whose material or part could not be assessed";
                    if (block != null)
                    {
                        flowMapClosureMembers.Add(texture);
                        blocked.Add(texture);
                        members.Remove(texture);
                        foreach (var owner in owners)
                            owner.Reason ??= SharedRepackReasonFlowTexture + " (" + block + ")";
                        continue;
                    }

                    var flowUserPaths = entry.ModParts.Concat(entry.VanillaParts)
                        .Where(use => !state.AtlasChangedParts.Contains((Normalize(use.Rigid), use.Lod, use.Part)))
                        .Select(use => Normalize(use.Rigid))
                        .Concat(merge.GeneratedUsers
                            .Where(user => user.Textures.Contains(texture) &&
                                           !state.AtlasChangedParts.Contains((Normalize(user.Rigid), user.Lod, user.Part)))
                            .Select(user => Normalize(user.Rigid)))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var closureRigidPaths = setRigids.Concat(flowUserPaths)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var generatedRigidCount = closureRigidPaths.Count(state.CrossRigidGeneratedRigidPaths.Contains);
                    var sourceRigidCount = closureRigidPaths.Count - generatedRigidCount;
                    if (sourceRigidCount > SharedRepackMaximumMergedRigids ||
                        generatedRigidCount > SharedRepackMaximumMergedGeneratedRigids)
                    {
                        flowMapClosureMembers.Add(texture);
                        blocked.Add(texture);
                        members.Remove(texture);
                        var reason = $"including all users would exceed the closure cap ({sourceRigidCount:N0} source rigids, " +
                                     $"{generatedRigidCount:N0} generated rigids; limits are " +
                                     $"{SharedRepackMaximumMergedRigids:N0} and {SharedRepackMaximumMergedGeneratedRigids:N0})";
                        foreach (var owner in owners)
                            owner.Reason ??= SharedRepackReasonFlowTexture + " (" + reason + ")";
                        continue;
                    }

                    flowMapClosureMembers.Add(texture);
                    var effective = merge.Effective(entry);
                    if (!entryOf.ContainsKey(texture))
                        merge.PlanTextureBytes += effective.Layout?.ChainBytes ?? 0;
                    entryOf[texture] = effective;
                    slotOf.TryAdd(texture, SharedRepackFlowMapSlot);
                    if (members.Add(texture))
                        IncludeFlowMapUsers(texture);
                }
            }

            var dropped = new HashSet<MeshKey>();
            var dropReasonOf = new Dictionary<MeshKey, string>();
            var removedBecause = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var outside = new Dictionary<string, (int Count, string Description)>(StringComparer.OrdinalIgnoreCase);

            string DescribeSetState(string texture)
            {
                if (!setByTexture.TryGetValue(texture, out var other) || !merge.IndexOfSet.TryGetValue(other, out var index))
                    return string.Empty;
                if (inComponent.Contains(index))
                    return " that was merged into this plan";
                var otherEntry = other.Items.First(item => Normalize(item.Entry.Path).Equals(texture, StringComparison.OrdinalIgnoreCase)).Entry;
                if (IsBlockedTexture(otherEntry, texture))
                {
                    return " but the texture cannot be repacked, so the set was not merged (" +
                           (GetSharedRepackBlockedTextureReason(merge.Effective(otherEntry).Layout) ?? "an owned override of a vanilla texture") + ")";
                }

                if (cappedTextures.Contains(texture))
                {
                    return $" (merge cap reached: at most {SharedRepackMaximumMergedSets} sets and " +
                           $"{SharedRepackMaximumMergedRigids} source rigids and " +
                           $"{SharedRepackMaximumMergedGeneratedRigids} generated rigids per plan)";
                }

                if (!merge.IsAvailable(index))
                    return " that was already processed";
                return merge.Allow ? string.Empty : " (merging is off for this attempt)";
            }

            // Report only: why a texture outside the set is outside, ranked so that the cause a fix
            // has to remove first comes first (a texture that cannot be re-encoded blocks every fix).
            (int Rank, SharedRepackClosureRoot Root) ClassifyOutsideTexture(string texture)
            {
                const string Via = "via a dropped part: ";
                if (removedBecause.ContainsKey(texture) && !blocked.Contains(texture))
                {
                    var inner = merge.RemovedRoots.TryGetValue(texture, out var root)
                        ? root
                        : new SharedRepackClosureRoot(
                            merge.RemovedTextures.TryGetValue(texture, out var removed) ? removed.Reason : "unknown",
                            null);
                    var category = inner.Category.StartsWith(Via, StringComparison.Ordinal)
                        ? inner.Category
                        : Via + inner.Category;
                    return (3, inner with { Category = category });
                }

                if (!coverage.Entries.TryGetValue(texture, out var entry))
                    return (2, new("the outside texture is unknown to the analysis", null));
                var layoutReason = GetSharedRepackBlockedTextureReason(merge.Effective(entry).Layout);
                if (layoutReason != null)
                    return (0, new("the outside texture cannot be re-encoded: " + layoutReason, null));
                if (blocked.Contains(texture) || BlockedTextureReason(entry, texture) is not null)
                {
                    return (1, new("the outside texture is blocked: " +
                                   (BlockedTextureReason(entry, texture) ?? "blocked"), null));
                }

                if (!setByTexture.TryGetValue(texture, out var other) || !merge.IndexOfSet.TryGetValue(other, out var index))
                {
                    return entry.IsRepackable
                        ? (2, new("the outside texture is in no signature set", null))
                        : (1, new("the outside texture is not repackable: " + (entry.NotCroppableReason ?? "unmeasured"), null));
                }

                if (cappedTextures.Contains(texture))
                    return (5, new("the outside texture's set hit the merge cap", null));
                if (inComponent.Contains(index))
                    return (6, new("the outside texture's set is in this plan", null));
                if (!merge.IsAvailable(index))
                {
                    return (4, new(
                        "the outside texture's set was processed before this one",
                        DescribeSharedRepackSet(merge.Decisions[index])));
                }

                return merge.Allow
                    ? (6, new("the outside texture's set is mergeable, but the part could not join (another outside texture or its geometry)", null))
                    : (6, new("merging is off (the alone retry after a failed merged attempt)", null));
            }

            SharedRepackClosureRoot ClassifyOutside(SharedRepackPart part)
            {
                SharedRepackClosureRoot? best = null;
                var bestRank = int.MaxValue;
                foreach (var texture in part.Textures
                             .Where(texture => !members.Contains(texture) && !isUniform(texture))
                             .OrderBy(texture => texture, StringComparer.Ordinal))
                {
                    var (rank, root) = ClassifyOutsideTexture(texture);
                    if (rank < bestRank)
                    {
                        bestRank = rank;
                        best = root;
                    }
                }

                return best ?? new("no outside texture found", null);
            }

            var rootOfPart = new Dictionary<MeshKey, SharedRepackClosureRoot>();

            void Drop(SharedRepackPart part, string reason)
            {
                dropped.Add(part.Key);
                droppedParts.Add(part);
                dropReasonOf[part.Key] = reason;
                var partRoot = reason is SharedRepackReasonMixed or SharedRepackReasonBlockedTexture
                    ? ClassifyOutside(part)
                    : new SharedRepackClosureRoot(reason, null);
                rootOfPart[part.Key] = partRoot;
                foreach (var texture in part.Textures.Where(members.Contains))
                {
                    NoteRemoved(texture, reason);
                    merge.RemovedRoots.TryAdd(texture, partRoot);
                }

                result.PartDropReasons[reason] = result.PartDropReasons.GetValueOrDefault(reason) + 1;
                if (part.DirectEmbeddedMaterial)
                {
                    if (!result.DirectEmbeddedDropReasonsByRigid.TryGetValue(
                            part.Key.GeometryPath,
                            out var rigidReasons))
                    {
                        rigidReasons = new Dictionary<string, int>(StringComparer.Ordinal);
                        result.DirectEmbeddedDropReasonsByRigid[part.Key.GeometryPath] = rigidReasons;
                    }

                    rigidReasons[reason] = rigidReasons.GetValueOrDefault(reason) + 1;
                    var loaders = string.Join(
                        ", ",
                        part.DirectConsumers.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
                    var slots = part.EmbeddedTextures.Count == 0
                        ? "no readable embedded slots"
                        : string.Join(", ", part.EmbeddedTextures
                            .OrderBy(pair => pair.Key)
                            .Select(pair => $"{pair.Key}={pair.Value}"));
                    var detail =
                        $"direct rigid {part.Key.GeometryPath} lod {part.Key.LodIndex} part {part.Key.PartIndex} " +
                        $"[loaded directly by {loaders}] embedded material " +
                        $"{part.EmbeddedMaterialType?.ToString() ?? "unreadable"}; slots {slots}; dropped ({reason})";
                    result.DirectEmbeddedDrops.Add((label, detail));
                    result.ClosureNotes.Add((label, detail));
                }

                if (reason == SharedRepackReasonMixed || reason == SharedRepackReasonBlockedTexture)
                {
                    // Uniform colours do not block a part, so they are not what pulled it out.
                    foreach (var texture in part.Textures.Where(texture => !members.Contains(texture) && !isUniform(texture))
                                 .OrderBy(texture => texture, StringComparer.Ordinal))
                    {
                        var known = outside.TryGetValue(texture, out var existing);
                        outside[texture] = (
                            (known ? existing.Count : 0) + 1,
                            known
                                ? existing.Description
                                : DescribeSharedRepackOutsideTexture(
                                    state, coverage, setRigids, texture, setByTexture, DescribeSetState,
                                    removedBecause, blocked, entryOf));
                    }
                }

                foreach (var texture in part.Textures.Where(members.Contains))
                {
                    removedBecause.TryAdd(
                        texture,
                        $"{DescribeSharedRepackPart(part)} was dropped ({reason})");
                }

                members.ExceptWith(part.Textures);
            }

            // What-if only: an outside texture that can be re-encoded is cropped into the set as a copy.
            bool Copyable(string texture)
                => merge.CopyOutside &&
                   coverage.Entries.TryGetValue(texture, out var entry) &&
                   merge.Effective(entry).Layout is { } layout &&
                   GetSharedRepackBlockedTextureReason(layout) == null;

            // Copy plan: a texture that left this plan because a dropped part still needs the original
            // is cropped into the pages as a copy for the parts that stay.
            bool RemovedCopyable(string texture)
                => removedBecause.ContainsKey(texture) &&
                   !blocked.Contains(texture) &&
                   !isUniform(texture) &&
                   entryOf.TryGetValue(texture, out var removedEntry) &&
                   GetSharedRepackBlockedTextureReason(removedEntry.Layout) == null;

            bool CopyableRemoved(string texture) => merge.CopyRemoved && RemovedCopyable(texture);

            // Normal plan: a part was dropped only for textures that left the plan, which copies would keep.
            bool CopyWouldRescue()
                => !merge.CopyRemoved &&
                   droppedParts.Any(part =>
                       dropReasonOf[part.Key] == SharedRepackReasonMixed &&
                       part.Textures.Where(texture => !members.Contains(texture) && !isUniform(texture))
                           .ToList() is { Count: > 0 } offending &&
                       offending.All(RemovedCopyable));

            // Why a part's textures outside the set keep it out of it: null when each of them
            // is a uniform colour, which is kept as authored and so never blocks the part.
            string? OutsideReason(SharedRepackPart part)
            {
                var offending = part.Textures
                    .Where(texture => !members.Contains(texture) && !isUniform(texture) && !Copyable(texture) &&
                                      !CopyableRemoved(texture))
                    .ToList();
                if (offending.Count == 0)
                    return null;

                return offending.Any(blocked.Contains)
                    ? SharedRepackReasonBlockedTexture
                    : SharedRepackReasonMixed;
            }

            // Uniform (kept as authored) or not, as the tuples classify a texture.
            bool IsConstantTexture(string texture)
            {
                if (!members.Contains(texture))
                    return isUniform(texture);

                var layout = entryOf[texture].Layout!;
                return layout.Width <= SharedRepackTinyTextureSide && layout.Height <= SharedRepackTinyTextureSide &&
                       isUniform(texture);
            }

            // Why a part's texture variants cannot be repacked: each variant texture must be a member with
            // the layout texture's size, mip count, format and colour space (or a uniform colour like the
            // layout texture, which the variant's own generated material keeps as authored). Null when fine.
            string? GetVariantReason(SharedRepackPart part)
            {
                foreach (var map in part.VariantSlots)
                {
                    foreach (var (slot, texture) in map)
                    {
                        var layoutTexture = part.Slots[slot];
                        if (texture.Equals(layoutTexture, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var layoutConstant = IsConstantTexture(layoutTexture);
                        if (layoutConstant != IsConstantTexture(texture))
                            return SharedRepackReasonVariantConstant + slot.ToLowerInvariant();
                        if (layoutConstant)
                            continue;

                        if (!members.Contains(texture) || !members.Contains(layoutTexture))
                            return SharedRepackReasonVariantOutside + slot.ToLowerInvariant();

                        var left = entryOf[layoutTexture].Layout!;
                        var right = entryOf[texture].Layout!;
                        if (left.Width != right.Width || left.Height != right.Height || left.MipCount != right.MipCount ||
                            !left.Format.Equals(right.Format, StringComparison.Ordinal) || left.IsSrgb != right.IsSrgb ||
                            GetSharedRepackBlockedTextureReason(right) != null)
                        {
                            return SharedRepackReasonVariantLayout + slot.ToLowerInvariant();
                        }
                    }
                }

                return null;
            }

            // The unprocessed signature sets that hold all the offending textures, or null
            // when one of them cannot be merged (blocked, unknown, in this plan already, or
            // already processed).
            List<int>? FindJoins(List<string> offending)
            {
                var joins = new SortedSet<int>();
                foreach (var texture in offending)
                {
                    if (blocked.Contains(texture) ||
                        !setByTexture.TryGetValue(texture, out var other) ||
                        !merge.IndexOfSet.TryGetValue(other, out var index) ||
                        inComponent.Contains(index) ||
                        !merge.IsAvailable(index))
                    {
                        return null;
                    }

                    var entry = other.Items.First(item => Normalize(item.Entry.Path).Equals(texture, StringComparison.OrdinalIgnoreCase)).Entry;
                    if (IsBlockedTexture(entry, texture))
                        return null;
                    joins.Add(index);
                }

                return joins.ToList();
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                var candidateCountBeforeFlowClosure = candidates.Count;
                IncludeVerifiedFlowMapMembers();
                if (candidates.Count != candidateCountBeforeFlowClosure)
                    changed = true;
                // By index: merging a set appends the parts of its rigids.
                for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
                {
                    var part = candidates[candidateIndex];
                    if (dropped.Contains(part.Key) || !part.Textures.Overlaps(members))
                        continue;

                    string? reason = part.Reason;
                    if (reason == null && !part.Textures.IsSubsetOf(members))
                    {
                        var offending = part.Textures
                            .Where(texture => !members.Contains(texture) && !isUniform(texture) && !CopyableRemoved(texture))
                            .ToList();
                        var joins = offending.Count != 0 && merge.Allow ? FindJoins(offending) : null;
                        if (joins != null)
                        {
                            // Otherwise eligible only if its geometry passes as well.
                            if (!part.GeometryChecked)
                                part.Reason = AnalyzeSharedRepackGeometry(part);
                            reason = part.Reason;
                        }

                        if (reason == null && joins != null)
                        {
                            // Source and generated rigids have their own caps.
                            var rigidPaths = new HashSet<string>(setRigids, StringComparer.OrdinalIgnoreCase);
                            foreach (var index in joins)
                            {
                                var joinedTextures = merge.Decisions[index].Set.Items
                                    .Select(item => Normalize(item.Entry.Path))
                                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                                rigidPaths.UnionWith(merge.Decisions[index].Set.Items
                                    .SelectMany(item => item.Entry.ModParts.Concat(item.Entry.VanillaParts))
                                    .Select(key => key.Rigid));
                                rigidPaths.UnionWith(merge.GeneratedUsers
                                    .Where(user => user.Textures.Overlaps(joinedTextures))
                                    .Select(user => user.Rigid));
                            }

                            var generatedCount = rigidPaths.Count(path => state.CrossRigidGeneratedRigidPaths.Contains(path));
                            var generatedCapHit = generatedCount > SharedRepackMaximumMergedGeneratedRigids;
                            if (inComponent.Count + joins.Count > SharedRepackMaximumMergedSets ||
                                rigidPaths.Count - generatedCount > SharedRepackMaximumMergedRigids ||
                                generatedCapHit)
                            {
                                merge.CapHit = true;
                                merge.GeneratedCapHit |= generatedCapHit;
                                cappedTextures.UnionWith(offending);
                                reason = SharedRepackReasonMixed;
                            }
                            else
                            {
                                foreach (var index in joins)
                                {
                                    inComponent.Add(index);
                                    merge.Joined.Add(index);
                                    merge.Component.Add(merge.Decisions[index]);
                                    AddSet(merge.Decisions[index]);
                                }

                                // Parts dropped earlier also take the new set's textures along.
                                foreach (var droppedPart in droppedParts)
                                {
                                    foreach (var texture in droppedPart.Textures.Where(members.Contains))
                                    {
                                        NoteRemoved(texture, dropReasonOf[droppedPart.Key]);
                                        merge.RemovedRoots.TryAdd(texture, rootOfPart[droppedPart.Key]);
                                        removedBecause.TryAdd(
                                            texture,
                                            $"{DescribeSharedRepackPart(droppedPart)} was dropped");
                                    }

                                    members.ExceptWith(droppedPart.Textures);
                                }

                                changed = true;
                                continue;
                            }
                        }
                        else if (reason == null)
                        {
                            reason = OutsideReason(part);
                        }
                    }

                    if (reason == null && part.VariantSlots.Count != 0)
                        reason = GetVariantReason(part);

                    if (reason == null && !part.GeometryChecked)
                    {
                        part.Reason = AnalyzeSharedRepackGeometry(part);
                        reason = part.Reason;
                    }

                    if (reason != null)
                    {
                        Drop(part, reason);
                        changed = true;
                    }
                }
            }

            var ineligibleNoted = false;
            void NoteOutside()
            {
                if (!ineligibleNoted)
                {
                    ineligibleNoted = true;
                    foreach (var item in ineligible.Values.OrderBy(item => item.Path, StringComparer.Ordinal).Take(3))
                    {
                        // Name the gameplay roots that load a directly referenced rigid without a WSModel.
                        var direct = state.GameplayMeshDependencyIndex?.DirectConsumersByRigid
                            .GetValueOrDefault(Normalize(item.Path));
                        var directText = direct is { Count: > 0 }
                            ? $" [loaded directly by {string.Join(", ", direct.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))}]"
                            : string.Empty;
                        result.ClosureNotes.Add((
                            label,
                            $"rigid {item.Path} is not eligible ({item.Reason}){directText}; its " +
                            $"{(pseudoParts.TryGetValue(item.Path, out var byKey) ? byKey.Count : 0):N0} part(s) " +
                            "using the set's textures were dropped"));
                    }
                }

                foreach (var (texture, (count, description)) in outside
                             .OrderBy(pair => pair.Key, StringComparer.Ordinal).Take(6))
                {
                    result.ClosureNotes.Add((label, $"{count:N0} part(s) use {texture}, which {description}"));
                }

                outside.Clear();
            }

            for (var attempt = 0; attempt <= candidates.Count + 1; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IncludeVerifiedFlowMapMembers();
                merge.CopyRescuable = CopyWouldRescue();
                var moved = candidates
                    .Where(part => !dropped.Contains(part.Key) && part.Textures.Overlaps(members))
                    .ToList();
                if (moved.Count == 0 || members.Count == 0)
                {
                    // What is still a member has no rewritable user at all.
                    foreach (var texture in members)
                    {
                        NoteRemoved(
                            texture,
                            candidates.Any(part => part.Textures.Contains(texture))
                                ? "every part using the texture was dropped"
                                : "no part of an eligible rigid with a complete WSModel binding uses the texture");
                    }

                    NoteOutside();
                    failure = "no part is left after closing the set (mixed materials, texture variants, tiling, " +
                              "non-atlasable slots or ineligible rigids)";
                    return null;
                }

                // Re-estimate on what is left; the coverage of a texture is the union over all
                // its users in all rigids.
                var items = new List<(string Slot, DdsLayout Layout, double Coverage)>();
                long today = 0;
                var scratch = CreateVanillaCoverageGrid();
                foreach (var path in members.OrderBy(path => path, StringComparer.Ordinal))
                {
                    Array.Clear(scratch);
                    foreach (var part in moved.Where(part => part.Textures.Contains(path)))
                    {
                        var data = coverage.PartData[(part.Key.GeometryPath, part.Key.LodIndex, part.Key.PartIndex)];
                        for (var word = 0; word < scratch.Length; word++)
                            scratch[word] |= data.Grid[word];
                    }

                    var layout = entryOf[path].Layout!;
                    today += layout.ChainBytes;
                    items.Add((slotOf[path], layout, CountVanillaCoverage(scratch)));
                }

                var estimate = EstimateRepackAtlasBytes(items);
                merge.ClosureEstimated = true;
                merge.ClosureToday = today;
                merge.ClosureEstimate = estimate.Rounded;
                // The copies: textures outside the set the moved parts use (uniform ones stay as
                // authored), each cropped to its users in the set; their originals stay for everyone else.
                List<(string Slot, DdsLayout Layout, double Coverage)> CropCopies(IEnumerable<string> textures)
                {
                    var copyItems = new List<(string Slot, DdsLayout Layout, double Coverage)>();
                    foreach (var copy in textures.OrderBy(texture => texture, StringComparer.Ordinal))
                    {
                        Array.Clear(scratch);
                        string? copySlot = null;
                        foreach (var part in moved.Where(part => part.Textures.Contains(copy)))
                        {
                            copySlot ??= part.Slots
                                .Where(pair => Normalize(pair.Value).Equals(copy, StringComparison.OrdinalIgnoreCase))
                                .Select(pair => pair.Key)
                                .FirstOrDefault();
                            var data = coverage.PartData[(part.Key.GeometryPath, part.Key.LodIndex, part.Key.PartIndex)];
                            for (var word = 0; word < scratch.Length; word++)
                                scratch[word] |= data.Grid[word];
                        }

                        copyItems.Add((
                            copySlot ?? "t_xml_base_colour",
                            merge.Effective(coverage.Entries[copy]).Layout!,
                            CountVanillaCoverage(scratch)));
                    }

                    return copyItems;
                }

                var copiedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (merge.CopyRemoved)
                {
                    copiedTextures.UnionWith(moved
                        .SelectMany(part => part.Textures)
                        .Where(texture => !members.Contains(texture) && !isUniform(texture)));
                    merge.CopiedTextures.Clear();
                    foreach (var copied in copiedTextures)
                        merge.CopiedTextures[copied] = removedBecause.GetValueOrDefault(copied, "left the plan");
                    if (copiedTextures.Count != 0)
                    {
                        estimate = EstimateRepackAtlasBytes(items.Concat(CropCopies(copiedTextures)).ToList());
                        merge.ClosureEstimate = estimate.Rounded;
                    }
                }

                if (merge.CopyOutside)
                {
                    var copyItems = CropCopies(moved
                        .SelectMany(part => part.Textures)
                        .Where(texture => !members.Contains(texture) && !isUniform(texture))
                        .Distinct(StringComparer.OrdinalIgnoreCase));
                    var withCopies = copyItems.Count == 0 ? estimate : EstimateRepackAtlasBytes(items.Concat(copyItems).ToList());
                    merge.WhatIf = new(
                        today,
                        withCopies.Rounded,
                        copyItems.Count,
                        withCopies.Rounded - estimate.Rounded,
                        null);
                    failure = "report-only what-if";
                    return null;
                }

                if (today - estimate.Rounded < result.MinimumSavingBytes)
                {
                    NoteOutside();
                    failure = $"after closing the set ({members.Count:N0} of {slotOf.Count:N0} texture(s), " +
                              $"{moved.Count:N0} part(s) left) the estimated saving is " +
                              $"{FormatSignedBytes(today - estimate.Rounded)} bytes, below the " +
                              $"{result.MinimumSavingBytes:N0}-byte threshold";
                    return null;
                }

                // Charts; a vertex that falls into two charts removes its part. Members up to
                // the tiny size are kept as authored when uniform (else their tuple is left
                // untouched); every other member is repacked; a texture outside the set is
                // kept as authored (the closure only lets uniform ones through).
                bool? ClassifyConstant(string texture)
                {
                    if (!members.Contains(texture))
                        return isUniform(texture);

                    var layout = entryOf[texture].Layout!;
                    return layout.Width <= SharedRepackTinyTextureSide && layout.Height <= SharedRepackTinyTextureSide
                        ? isUniform(texture)
                        : null;
                }

                string SetKeyOf(string texture)
                {
                    if (setByTexture.TryGetValue(texture, out var owner))
                        return GetSharedRepackSetKey(owner);
                    return flowMapClosureMembers.Contains(texture)
                        ? "flow-map-source:" + Normalize(texture).ToLowerInvariant()
                        : string.Empty;
                }

                // The copied emissive noise is no set member, but its layout is needed like a slot's.
                foreach (var copied in moved.SelectMany(part => part.CopySlots.Select(slot => part.Slots[slot])))
                {
                    if (!entryOf.ContainsKey(copied) && coverage.Entries.TryGetValue(copied, out var copyEntry))
                        entryOf[copied] = copyEntry;
                }

                var tuples = BuildSharedRepackTuples(
                    moved, entryOf, ClassifyConstant, SetKeyOf, copiedTextures, out var violating);
                if (violating.Count != 0)
                {
                    foreach (var part in violating)
                        Drop(part, "a vertex falls into two UV charts");
                    var again = true;
                    while (again)
                    {
                        again = false;
                        foreach (var part in candidates)
                        {
                            if (!dropped.Contains(part.Key) &&
                                part.Textures.Overlaps(members) &&
                                !part.Textures.IsSubsetOf(members) &&
                                OutsideReason(part) is { } reason)
                            {
                                Drop(part, reason);
                                again = true;
                            }
                        }
                    }

                    continue;
                }

                NoteOutside();
                return BuildSharedRepackSetPlan(
                    state, result, coverage, rigids, tuples, entryOf, label, merge, paddingLimits,
                    out failure, out failureBucket);
            }

            failure = "the set could not be closed";
            return null;
        }

        // Tuples (parts with the same texture in every slot), their grids and charts. A
        // tuple that cannot be charted is marked excluded instead of failing the set.
        private static List<SharedRepackTuple> BuildSharedRepackTuples(
            List<SharedRepackPart> moved,
            Dictionary<string, VanillaCoverageTexture> entryOf,
            Func<string, bool?> classifyConstant,
            Func<string, string> setKeyOf,
            HashSet<string> copiedTextures,
            out List<SharedRepackPart> violating)
        {
            violating = [];
            var tuples = new List<SharedRepackTuple>();
            foreach (var group in moved
                         .GroupBy(part => part.TupleKey, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var tuple = new SharedRepackTuple
                {
                    Index = tuples.Count,
                    Key = group.Key,
                    Slots = new SortedDictionary<string, string>(
                        group.First().Slots,
                        StringComparer.OrdinalIgnoreCase),
                    CopySlots = new HashSet<string>(group.First().CopySlots, StringComparer.OrdinalIgnoreCase),
                };
                // A texture copied in this plan is a copy slot of every tuple that uses it (the part's
                // own copy slots are fixed; this one depends on the plan, so the part is not touched).
                foreach (var (slot, texture) in tuple.Slots)
                {
                    if (copiedTextures.Contains(texture))
                        tuple.CopySlots.Add(slot);
                }
                tuple.Parts.AddRange(group
                    .OrderBy(part => part.Key.GeometryPath, StringComparer.Ordinal)
                    .ThenBy(part => part.Key.LodIndex)
                    .ThenBy(part => part.Key.PartIndex));
                tuples.Add(tuple);
                if (tuple.Slots.Count == 0)
                {
                    tuple.Excluded = "a part has no atlasable texture slot";
                    continue;
                }

                // Uniform textures (vanilla's 4x4 masks, a flat colour outside the set) cannot be
                // cropped per chart and do not need to be: they stay as authored. A tiny
                // texture that is not uniform cannot be repacked.
                // A copy slot (the emissive distortion noise, or a texture copied in this plan) is never
                // uniform and never a member: it is always cropped, so it is not classified at all.
                foreach (var (slot, texture) in tuple.Slots.ToList())
                {
                    if (tuple.CopySlots.Contains(slot))
                        continue;

                    var constant = classifyConstant(texture);
                    if (constant == true)
                    {
                        tuple.ConstantSlots[slot] = texture;
                        tuple.Slots.Remove(slot);
                    }
                    else if (constant == false)
                    {
                        tuple.Excluded = $"its tiny {Path.GetFileName(texture)} is not a uniform colour";
                    }
                }

                if (tuple.Excluded != null)
                    continue;
                if (tuple.Slots.Keys.All(tuple.CopySlots.Contains))
                {
                    tuple.Excluded = tuple.Slots.Keys.Any(tuple.IsGeneralCopySlot)
                        ? "every texture is a uniform one or a copy (nothing to repack)"
                        : "every texture is a uniform one (nothing to repack)";
                    continue;
                }

                foreach (var (slot, texture) in tuple.Slots)
                    tuple.Layouts[slot] = entryOf[texture].Layout!;
                // The variant textures of the repacked slots (a uniform slot is not in Slots, and the
                // variant's own material keeps its uniform texture; copy slots have no variants).
                foreach (var variant in group.First().VariantSlots)
                {
                    foreach (var (slot, texture) in variant)
                    {
                        if (!tuple.Slots.TryGetValue(slot, out var layoutTexture) || tuple.CopySlots.Contains(slot) ||
                            texture.Equals(layoutTexture, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!tuple.VariantTextures.TryGetValue(slot, out var list))
                        {
                            list = [];
                            tuple.VariantTextures[slot] = list;
                        }

                        if (!list.Contains(texture, StringComparer.OrdinalIgnoreCase))
                            list.Add(texture);
                    }
                }

                foreach (var list in tuple.VariantTextures.Values)
                    list.Sort(StringComparer.Ordinal);
                // Reference resolution: the base colour, else the largest slot (never the emissive noise
                // copy; a copied base colour or other copy is a normal slot).
                var reference = tuple.Slots.ContainsKey("t_xml_base_colour")
                    ? tuple.Layouts["t_xml_base_colour"]
                    : tuple.Layouts.Where(pair => !tuple.IsNoiseCopySlot(pair.Key)).Select(pair => pair.Value)
                        .OrderByDescending(layout => (long)layout.Width * layout.Height).First();
                tuple.ReferenceWidth = reference.Width;
                tuple.ReferenceHeight = reference.Height;
                // The copied slot sets the cell (whole source texels) but not the chart alignment: its
                // coarse texture simply block-copies fewer mip levels than the other slots.
                var downshift = 0;
                var cellDownshift = 0;
                foreach (var (slot, layout) in tuple.Layouts)
                {
                    var exponent = (Log2Exact(layout.Width) - Log2Exact(reference.Width),
                        Log2Exact(layout.Height) - Log2Exact(reference.Height));
                    tuple.SourceExponent[slot] = exponent;
                    var shift = Math.Max(-exponent.Item1, -exponent.Item2);
                    cellDownshift = Math.Max(cellDownshift, shift);
                    if (!tuple.IsNoiseCopySlot(slot))
                        downshift = Math.Max(downshift, shift);
                }

                // Cell: at least the minimum, a multiple of the pixels one reference pixel
                // spans in the coarsest slot (whole source texels, whole BC blocks), at most
                // 1024 cells per side.
                var cell = Math.Max(SharedRepackMinimumCell, 4 << cellDownshift);
                if (cell > SharedRepackMaximumCell)
                {
                    tuple.Excluded = $"a slot is {1 << cellDownshift} times smaller than its reference, which would " +
                                     $"need a {cell}-pixel chart grid (limit {SharedRepackMaximumCell})";
                    continue;
                }

                cell = Math.Max(
                    cell,
                    NextPowerOfTwo(Math.Max(reference.Width, reference.Height) / SharedRepackMaximumCellsPerSide));
                tuple.Cell = cell;
                tuple.Gutter = cell;
                tuple.Align = Math.Max(cell, SharedRepackChartAlignment << downshift);
                // The signature set of each slot's texture is part of the key: a generated texture
                // then only ever replaces source textures that exactly the same units load. A copied
                // slot is keyed by its texture instead (copies of different noise never share a page),
                // and a tuple with emissive compensation by its reference aspect ratio (its page
                // canvases are restricted to it, so the UV scale is equal in U and V).
                //
                // A variant texture that other units load than the layout texture (a foreign variant)
                // keeps its tuple apart in the strict key: the page of the variant would hold crops of
                // textures those units never loaded. The group key leaves it out; the planner joins
                // tuples that differ only there when it can show each foreign variant page is no
                // bigger than the texture it replaces and the joint pages are smaller than the
                // separate ones, else it falls back to the strict key.
                string BuildKey(bool strict) => $"{cell}|" + string.Join(
                    ";",
                    tuple.Layouts.Select(pair =>
                        $"{pair.Key}:{pair.Value.Format}:{pair.Value.IsSrgb}:" +
                        $"{tuple.SourceExponent[pair.Key].X}:{tuple.SourceExponent[pair.Key].Y}:" +
                        (tuple.CopySlots.Contains(pair.Key)
                            ? $"copy:{Normalize(tuple.Slots[pair.Key])}"
                            : setKeyOf(tuple.Slots[pair.Key])) +
                        (strict && tuple.VariantTextures.TryGetValue(pair.Key, out var variantTextures)
                            ? string.Concat(variantTextures
                                .Select(setKeyOf)
                                .Where(key => key != setKeyOf(tuple.Slots[pair.Key]))
                                .Distinct(StringComparer.Ordinal)
                                .OrderBy(key => key, StringComparer.Ordinal)
                                .Select(key => $"<{key}"))
                            : string.Empty)));
                tuple.GroupKey = BuildKey(false);
                tuple.StrictGroupKey = BuildKey(true);
                foreach (var (slot, slotVariants) in tuple.VariantTextures)
                {
                    var layoutSet = setKeyOf(tuple.Slots[slot]);
                    foreach (var texture in slotVariants.Where(texture => setKeyOf(texture) != layoutSet))
                        tuple.ForeignVariantTextures.Add(texture);
                }

                if (tuple.EmissiveCompensation || tuple.FlowCompensation)
                {
                    var divisor = GreatestCommonDivisor(reference.Width, reference.Height);
                    var aspect = $"|aspect:{reference.Width / divisor}x{reference.Height / divisor}" +
                                 (tuple.FlowCompensation ? "|flow-compensation" : string.Empty);
                    tuple.GroupKey += aspect;
                    tuple.StrictGroupKey += aspect;
                }
                if (reference.Width % cell != 0 || reference.Height % cell != 0)
                {
                    tuple.Excluded = $"its {reference.Width}x{reference.Height} texture is too small for the " +
                                     $"{cell}-pixel chart grid (left untouched, nothing to gain)";
                    continue;
                }

                tuple.GridX = reference.Width / cell;
                tuple.GridY = reference.Height / cell;
                var grid = new bool[tuple.GridX * tuple.GridY];
                foreach (var part in tuple.Parts)
                {
                    var indices = part.Model!.Mesh.IndexList;
                    for (var index = 0; index < indices.Length; index += 3)
                    {
                        int ia = indices[index], ib = indices[index + 1], ic = indices[index + 2];
                        MarkSharedRepackTriangle(
                            grid, tuple.GridX, tuple.GridY,
                            part.U[ia], part.V[ia], part.U[ib], part.V[ib], part.U[ic], part.V[ic]);
                    }
                }

                var boxes = FindSharedRepackComponents(grid, tuple.GridX, tuple.GridY, out var label);
                if (boxes.Count == 0)
                {
                    tuple.Excluded = "it has no usable triangle";
                    continue;
                }

                if (boxes.Count > SharedRepackMaximumComponents)
                {
                    tuple.Excluded = $"{boxes.Count:N0} UV components exceed the {SharedRepackMaximumComponents:N0} limit";
                    continue;
                }

                // Merge components whose padded (one cell) boxes overlap, to a fixpoint.
                var parent = Enumerable.Range(0, boxes.Count).ToArray();
                int Find(int node)
                {
                    while (parent[node] != node)
                    {
                        parent[node] = parent[parent[node]];
                        node = parent[node];
                    }

                    return node;
                }

                var live = boxes.ToArray();
                var roots = Enumerable.Range(0, boxes.Count).ToList();
                var merged = true;
                while (merged)
                {
                    merged = false;
                    for (var left = 0; left < roots.Count; left++)
                    {
                        for (var right = left + 1; right < roots.Count;)
                        {
                            var a = live[roots[left]];
                            var b = live[roots[right]];
                            if (a.X0 - 1 < b.X1 + 2 && b.X0 - 1 < a.X1 + 2 &&
                                a.Y0 - 1 < b.Y1 + 2 && b.Y0 - 1 < a.Y1 + 2)
                            {
                                parent[roots[right]] = roots[left];
                                live[roots[left]] = (
                                    Math.Min(a.X0, b.X0), Math.Min(a.Y0, b.Y0),
                                    Math.Max(a.X1, b.X1), Math.Max(a.Y1, b.Y1));
                                roots.RemoveAt(right);
                                merged = true;
                            }
                            else
                            {
                                right++;
                            }
                        }
                    }
                }

                if (roots.Count > SharedRepackMaximumCharts)
                {
                    tuple.Excluded = $"{roots.Count:N0} charts exceed the {SharedRepackMaximumCharts:N0}-chart limit";
                    continue;
                }

                var chartOfRoot = new Dictionary<int, int>();
                foreach (var root in roots.OrderBy(root => live[root].Y0).ThenBy(root => live[root].X0))
                {
                    chartOfRoot[root] = tuple.Charts.Count;
                    tuple.Charts.Add(CreateSharedRepackChart(tuple, tuple.Charts.Count, live[root], false, -1));
                }

                tuple.CellChart = new int[grid.Length];
                for (var cellIndex = 0; cellIndex < grid.Length; cellIndex++)
                    tuple.CellChart[cellIndex] = label[cellIndex] < 0 ? -1 : chartOfRoot[Find(label[cellIndex])];

                // Assign every vertex the chart of its triangles; all must agree. (A part's
                // assignment from an earlier round of the closure is void.)
                foreach (var part in tuple.Parts)
                {
                    // A fresh array: an earlier round (or the other layout) may still hold the old one.
                    part.VertexChart = new int[part.VertexChart.Length];
                    Array.Fill(part.VertexChart, -1);
                    part.MergedVertexChart = null;
                    part.TightVertexChart = null;
                    var indices = part.Model!.Mesh.IndexList;
                    var bad = false;
                    for (var index = 0; index < indices.Length && !bad; index += 3)
                    {
                        int[] corners = [indices[index], indices[index + 1], indices[index + 2]];
                        var cu = (part.U[corners[0]] + part.U[corners[1]] + part.U[corners[2]]) / 3.0;
                        var cv = (part.V[corners[0]] + part.V[corners[1]] + part.V[corners[2]]) / 3.0;
                        var chart = FindSharedRepackChart(tuple, cu, cv);
                        if (chart < 0)
                        {
                            bad = true;
                            break;
                        }

                        foreach (var vertex in corners)
                        {
                            if (part.VertexChart[vertex] == -1)
                                part.VertexChart[vertex] = chart;
                            else if (part.VertexChart[vertex] != chart)
                                bad = true;
                        }
                    }

                    if (bad)
                        violating.Add(part);
                }

                // Tight pieces only differ from the charts when a chart merged several components.
                if (boxes.Count > roots.Count)
                {
                    BuildSharedRepackTightPieces(
                        tuple, grid, boxes, label, component => chartOfRoot[Find(component)]);
                }
            }

            return tuples;
        }

        // A chart rectangle for a cell box, snapped outward to the active alignment.
        private static SharedRepackChart CreateSharedRepackChart(
            SharedRepackTuple tuple,
            int id,
            (int X0, int Y0, int X1, int Y1) box,
            bool piece,
            int mergedId)
        {
            var rectangle = GetSharedRepackRectangle(tuple, box, tuple.Gutter, tuple.Align);
            return new SharedRepackChart
            {
                Id = id,
                Tuple = tuple,
                CellX0 = box.X0,
                CellY0 = box.Y0,
                CellX1 = box.X1,
                CellY1 = box.Y1,
                X0 = rectangle.X0,
                Y0 = rectangle.Y0,
                Width = rectangle.Width,
                Height = rectangle.Height,
                LayoutWidth = RoundUpTo(rectangle.Width, tuple.Align),
                LayoutHeight = RoundUpTo(rectangle.Height, tuple.Align),
                Piece = piece,
                MergedId = mergedId,
            };
        }

        private static void ResizeSharedRepackCharts(SharedRepackTuple tuple, int gutter, int align)
        {
            tuple.Gutter = gutter;
            tuple.Align = align;
            foreach (var chart in EnumerateAllSharedRepackCharts(tuple).Distinct())
            {
                var rectangle = GetSharedRepackRectangle(
                    tuple,
                    (chart.CellX0, chart.CellY0, chart.CellX1, chart.CellY1),
                    gutter,
                    align);
                chart.X0 = rectangle.X0;
                chart.Y0 = rectangle.Y0;
                chart.Width = rectangle.Width;
                chart.Height = rectangle.Height;
                chart.LayoutWidth = RoundUpTo(rectangle.Width, align);
                chart.LayoutHeight = RoundUpTo(rectangle.Height, align);
                chart.PackX = 0;
                chart.PackY = 0;
                chart.Page = null;
            }
        }

        // Tight pieces: one chart per 8-connected component of touched cells, the same gutter and
        // alignment as a merged chart, translated only (never scaled) by the UV rewrite.
        //
        // What the merge protects: components whose padded boxes overlap are joined into one chart so that
        // (1) no two rectangles of the layout overlap in source space (a texel is copied once), and (2) a
        // vertex lookup by cell neighbourhood (FindSharedRepackChart, radius 1) can never see two charts.
        // Neither matters for pieces: overlapping source rectangles just duplicate texels (every piece
        // carries its own gutter, so each triangle still sees its own neighbourhood unchanged), and the
        // piece of a triangle is read from the rasterisation itself (the cells the triangle marked all
        // belong to one component) instead of a neighbourhood lookup. The one thing the merge
        // implied that is kept: the pieces of a former chart share a cluster (MergedId), so they land
        // on the same page as before.
        //
        // A shared vertex lies in the cells of every triangle that uses it, so those triangles are in one
        // component. Anything that does not hold (a triangle over two components, a vertex of two pieces)
        // sets TightInconsistent and the tuple's group keeps the merged charts.
        private static void BuildSharedRepackTightPieces(
            SharedRepackTuple tuple,
            bool[] grid,
            List<(int X0, int Y0, int X1, int Y1)> boxes,
            int[] label,
            Func<int, int> mergedChartOf)
        {
            if (SharedRepackTightPiecesDisabled)
                return;

            var order = Enumerable.Range(0, boxes.Count)
                .OrderBy(component => boxes[component].Y0)
                .ThenBy(component => boxes[component].X0)
                .ThenBy(component => component)
                .ToList();
            var pieceOfComponent = new int[boxes.Count];
            var pieces = new List<SharedRepackChart>();
            foreach (var component in order)
            {
                pieceOfComponent[component] = pieces.Count;
                pieces.Add(CreateSharedRepackChart(tuple, pieces.Count, boxes[component], true, mergedChartOf(component)));
            }

            var cellPiece = new int[grid.Length];
            for (var cell = 0; cell < grid.Length; cell++)
                cellPiece[cell] = label[cell] < 0 ? -1 : pieceOfComponent[label[cell]];

            string? inconsistent = null;
            var vertexPieces = new List<int[]>();
            foreach (var part in tuple.Parts)
            {
                var assigned = new int[part.VertexChart.Length];
                Array.Fill(assigned, -1);
                vertexPieces.Add(assigned);
                var indices = part.Model!.Mesh.IndexList;
                var gx = tuple.GridX;
                var current = -1;
                var mixed = false;
                void Visit(int row, int first, int last)
                {
                    for (var x = first; x <= last; x++)
                    {
                        var piece = cellPiece[row * gx + x];
                        if (piece < 0 || (current != -1 && current != piece))
                            mixed = true;
                        else
                            current = piece;
                    }
                }

                Action<int, int, int> visit = Visit;
                for (var index = 0; index < indices.Length && inconsistent == null; index += 3)
                {
                    int[] corners = [indices[index], indices[index + 1], indices[index + 2]];
                    current = -1;
                    mixed = false;
                    VisitSharedRepackTriangleRows(
                        tuple.GridX, tuple.GridY,
                        part.U[corners[0]], part.V[corners[0]], part.U[corners[1]], part.V[corners[1]],
                        part.U[corners[2]], part.V[corners[2]], visit);
                    if (mixed || current < 0)
                    {
                        inconsistent = "a triangle touches cells of more than one UV component";
                        break;
                    }

                    foreach (var vertex in corners)
                    {
                        if (assigned[vertex] == -1)
                            assigned[vertex] = current;
                        else if (assigned[vertex] != current)
                            inconsistent = "a vertex is shared by triangles of two UV components";
                    }
                }

                if (inconsistent != null)
                    break;
            }

            if (inconsistent != null)
            {
                tuple.TightInconsistent = inconsistent;
                return;
            }

            tuple.MergedCharts = tuple.Charts;
            tuple.MergedCellChart = tuple.CellChart;
            tuple.TightCharts = pieces;
            tuple.TightCellChart = cellPiece;
            for (var index = 0; index < tuple.Parts.Count; index++)
            {
                tuple.Parts[index].MergedVertexChart = tuple.Parts[index].VertexChart;
                tuple.Parts[index].TightVertexChart = vertexPieces[index];
            }
        }

        // Switches the tuples that have a tight alternative between their merged charts and pieces. The
        // other tuples (no merged component, nothing to switch) keep their charts.
        private static void ActivateSharedRepackLayout(IEnumerable<SharedRepackTuple> tuples, bool tight)
        {
            foreach (var tuple in tuples)
            {
                if (tuple.TightCharts == null || tuple.MergedCharts == null)
                    continue;

                tuple.Tight = tight;
                tuple.Charts = tight ? tuple.TightCharts : tuple.MergedCharts;
                tuple.CellChart = tight ? tuple.TightCellChart! : tuple.MergedCellChart!;
                foreach (var part in tuple.Parts)
                    part.VertexChart = (tight ? part.TightVertexChart : part.MergedVertexChart) ?? part.VertexChart;
            }
        }

        // Both layouts of a tuple (a page reset must reach the inactive one too).
        private static IEnumerable<SharedRepackChart> EnumerateAllSharedRepackCharts(SharedRepackTuple tuple)
            => tuple.Charts.Concat(tuple.MergedCharts ?? []).Concat(tuple.TightCharts ?? []);

        // The chart that holds a tile-local point: the cell it falls in, else its neighbours.
        private static int FindSharedRepackChart(SharedRepackTuple tuple, double u, double v)
        {
            var cx = Math.Clamp((int)Math.Floor(u * tuple.GridX), 0, tuple.GridX - 1);
            var cy = Math.Clamp((int)Math.Floor(v * tuple.GridY), 0, tuple.GridY - 1);
            for (var radius = 0; radius <= 1; radius++)
            {
                for (var dy = -radius; dy <= radius; dy++)
                {
                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        var x = cx + dx;
                        var y = cy + dy;
                        if (x < 0 || y < 0 || x >= tuple.GridX || y >= tuple.GridY)
                            continue;

                        var chart = tuple.CellChart[y * tuple.GridX + x];
                        if (chart >= 0)
                            return chart;
                    }
                }
            }

            return -1;
        }

        // Per slot: the distinct source sizes, and the output size of each page.
        private static string DescribeSharedRepackSizes(List<SharedRepackTuple> tuples, List<SharedRepackPage> pages)
        {
            var parts = new List<string>();
            foreach (var slot in tuples.SelectMany(tuple => tuple.Slots.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(slot => slot, StringComparer.Ordinal))
            {
                var sources = tuples.Where(tuple => tuple.Layouts.ContainsKey(slot))
                    .Select(tuple => $"{tuple.Layouts[slot].Width}x{tuple.Layouts[slot].Height}")
                    .Distinct().ToList();
                var outputs = pages.Select(page => page.Outputs.FirstOrDefault(output => output.Slot == slot))
                    .Where(output => output != null)
                    .Select(output => $"{output!.Width}x{output.Height}");
                parts.Add($"{slot.Replace("t_xml_", string.Empty)} {string.Join("/", sources)} -> {string.Join("+", outputs)}");
            }

            var constants = tuples.SelectMany(tuple => tuple.ConstantSlots.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(slot => slot, StringComparer.Ordinal).ToList();
            if (constants.Count != 0)
                parts.Add("kept as authored (uniform): " + string.Join(", ", constants.Select(slot => slot.Replace("t_xml_", string.Empty))));
            return string.Join("; ", parts);
        }

        private static string DescribeSharedRepackTupleSizes(SharedRepackTuple tuple)
            => string.Join(
                "; ",
                tuple.Layouts.Select(pair => $"{pair.Key.Replace("t_xml_", string.Empty)} {pair.Value.Width}x{pair.Value.Height}")
                    .Concat(tuple.ConstantSlots.Select(pair => $"{pair.Key.Replace("t_xml_", string.Empty)} uniform kept")));

        private static string DescribeSharedRepackTuple(SharedRepackTuple tuple)
            => string.Join(" + ", tuple.MemberTextures.Select(path => Path.GetFileName(path)));

        // Layout groups, pages, thresholds, materials and WSModel repoints for the tuples
        // that survive. Exclusions propagate over shared textures (a texture used by an
        // included and an excluded tuple would stay loaded anyway), then groups that do not
        // pay are excluded too, until nothing changes.
        private SharedRepackSetPlan? BuildSharedRepackSetPlan(
            BatchState state,
            SharedTextureRepackResult result,
            VanillaTextureCoverageResult coverage,
            List<SharedRepackSetRigid> rigids,
            List<SharedRepackTuple> tuples,
            Dictionary<string, VanillaCoverageTexture> entryOf,
            string label,
            SharedRepackMergeContext merge,
            IReadOnlyDictionary<string, int> paddingLimits,
            out string? failure,
            out string failureBucket)
        {
            failure = null;
            failureBucket = "build";
            var plan = new SharedRepackSetPlan();
            plan.Tuples.AddRange(tuples);
            var gated = false;
            var wsByRigid = rigids.ToDictionary(item => item.Path, item => item.WsModels, StringComparer.OrdinalIgnoreCase);

            // The generated names come from the first rigid (ordinal) with a part in this plan
            // and a hash over all of them, so a set spanning several rigids is named by the whole
            // set. For a set of one rigid the identity is that rigid's path.
            var planRigids = tuples
                .SelectMany(tuple => tuple.Parts)
                .Select(part => part.Key.GeometryPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
            var nameRigid = planRigids.FirstOrDefault(path => !state.CrossRigidGeneratedRigidPaths.Contains(path)) ??
                            planRigids[0];
            var identity = string.Join("|", planRigids);
            if (merge.Component.Count > 1)
            {
                // A merged plan is named by every rigid and every set label in it.
                identity += "#" + string.Join(
                    "|", merge.Component.Select(DescribeSharedRepackSet).OrderBy(text => text, StringComparer.Ordinal));
            }

            // Tuples that share a texture form one component.
            var parent = Enumerable.Range(0, tuples.Count).ToArray();
            int Find(int node)
            {
                while (parent[node] != node)
                {
                    parent[node] = parent[parent[node]];
                    node = parent[node];
                }

                return node;
            }

            var owner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var tuple in tuples)
            {
                foreach (var texture in tuple.AllMemberTextures)
                {
                    if (owner.TryGetValue(texture, out var other))
                        parent[Find(tuple.Index)] = Find(other);
                    else
                        owner[texture] = tuple.Index;
                }
            }

            bool Propagate()
            {
                var any = false;
                foreach (var component in tuples.GroupBy(tuple => Find(tuple.Index)))
                {
                    var first = component.FirstOrDefault(tuple => tuple.Excluded != null);
                    if (first == null)
                        continue;

                    foreach (var tuple in component.Where(tuple => tuple.Excluded == null))
                    {
                        tuple.Excluded =
                            $"it shares a texture with {DescribeSharedRepackTuple(first)}, which is left untouched";
                        any = true;
                    }
                }

                return any;
            }

            var maximumSide = result.MaximumSide;
            var passWaste = new List<SharedRepackGroupWaste>();
            // Groups of the current pass whose packing simulation (report only) waits for the final pass.
            var passSimPending = new List<(int Index, List<SharedRepackTuple> Tuples, List<SharedRepackPage> Pages, long Before, long After, string Label)>();
            while (true)
            {
                while (Propagate())
                {
                }

                plan.Pages.Clear();
                plan.Groups.Clear();
                plan.AspectGroups = plan.AspectTuples = 0;
                plan.AspectExtraBytes = 0;
                plan.FlowAspectGroups = plan.FlowAspectTuples = 0;
                plan.FlowAspectExtraBytes = 0;
                passWaste.Clear();
                passSimPending.Clear();
                foreach (var tuple in tuples)
                    foreach (var chart in EnumerateAllSharedRepackCharts(tuple))
                        chart.Page = null;

                var active = tuples.Where(tuple => tuple.Excluded == null).ToList();
                if (active.Count == 0)
                {
                    failure = "every texture tuple was left untouched: " + string.Join(
                        "; ",
                        tuples.Select(tuple => $"{DescribeSharedRepackTuple(tuple)} [{DescribeSharedRepackTupleSizes(tuple)}]: {tuple.Excluded}").Take(3));
                    failureBucket = gated ? "bytes" : "build";
                    return null;
                }

                // Tuples that differ only in foreign variants share a group key; a bucket with a single
                // strict key joins nothing and keeps it.
                foreach (var bucket in active.GroupBy(tuple => tuple.GroupKey).ToList())
                {
                    if (bucket.Select(tuple => tuple.StrictGroupKey).Distinct(StringComparer.Ordinal).Count() != 1)
                        continue;
                    foreach (var tuple in bucket)
                        tuple.GroupKey = tuple.StrictGroupKey;
                }

                var restart = false;
                foreach (var group in active.GroupBy(tuple => tuple.GroupKey).OrderBy(group => group.Key, StringComparer.Ordinal))
                {
                    var groupTuples = group.OrderBy(tuple => tuple.Index).ToList();
                    foreach (var tuple in groupTuples)
                        tuple.MaximumPaddingVariantIndex = paddingLimits.GetValueOrDefault(group.Key, 2);
                    // Joined tuples (different strict keys) are only kept together when every one of their
                    // strict groups pays on its own and the joint pages are smaller than the separate ones;
                    // the variant pages are checked below. Otherwise each tuple falls back to its strict key.
                    var joinedGroup = groupTuples.Select(tuple => tuple.StrictGroupKey).Distinct(StringComparer.Ordinal).Count() > 1;
                    var separateAfter = 0L;
                    if (joinedGroup)
                    {
                        var separatePay = true;
                        foreach (var separate in groupTuples.GroupBy(tuple => tuple.StrictGroupKey, StringComparer.Ordinal))
                        {
                            var separateTuples = separate.ToList();
                            var separatePages = PackSharedRepackGroup(
                                nameRigid, identity, separateTuples, maximumSide, 0, result.MinimumGroupSavingBytes,
                                out _, out var separateExclude, out _);
                            var separateBefore = separateTuples.SelectMany(tuple => tuple.AllMemberTextures)
                                .Distinct(StringComparer.OrdinalIgnoreCase).Sum(texture => entryOf[texture].Layout!.ChainBytes);
                            var separateBytes = separatePages.Sum(page => page.BytesAfter);
                            if (separateExclude != null || separateBefore - separateBytes < result.MinimumGroupSavingBytes)
                            {
                                separatePay = false;
                                break;
                            }

                            separateAfter += separateBytes;
                        }

                        if (!separatePay)
                        {
                            foreach (var tuple in groupTuples)
                                tuple.GroupKey = tuple.StrictGroupKey;
                            restart = true;
                            break;
                        }

                    }

                    var pages = PackSharedRepackGroup(
                        nameRigid, identity, groupTuples, maximumSide, plan.Pages.Count, result.MinimumGroupSavingBytes,
                        out var split, out var excludeTuple, out var excludeReason);
                    if (excludeTuple == null && joinedGroup && !AcceptSharedRepackJoinedGroup(pages, separateAfter, entryOf))
                    {
                        foreach (var tuple in groupTuples)
                            tuple.GroupKey = tuple.StrictGroupKey;
                        restart = true;
                        break;
                    }

                    if (excludeTuple != null)
                    {
                        excludeTuple.Excluded = excludeReason;
                        if (excludeReason!.StartsWith("internal sizing error", StringComparison.Ordinal))
                        {
                            result.SizingErrors++;
                            result.SizingLog.Add((
                                label,
                                $"tuple {DescribeSharedRepackTuple(excludeTuple)} [{DescribeSharedRepackTupleSizes(excludeTuple)}]: {excludeReason}"));
                        }
                        restart = true;
                        break;
                    }

                    plan.Splits[group.Key] = split;
                    var textures = groupTuples.SelectMany(tuple => tuple.AllMemberTextures)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var before = textures.Sum(texture => entryOf[texture].Layout!.ChainBytes);
                    var after = pages.Sum(page => page.BytesAfter);
                    var newlyBuilt = split.PaddingVariant != "baseline" &&
                                     (split.PaddingBaselineBytes == 0 ||
                                      before - split.PaddingBaselineBytes < result.MinimumGroupSavingBytes) &&
                                     before - after >= result.MinimumGroupSavingBytes;
                    var waste = MeasureSharedRepackGroupWaste(
                        before - after <= 0 || before - after < result.MinimumGroupSavingBytes,
                        before,
                        pages,
                        split,
                        newlyBuilt);
                    var info = new SharedRepackGroupInfo
                    {
                        GroupKey = group.Key,
                        Sizes = DescribeSharedRepackSizes(groupTuples, pages),
                        Label = string.Join(" | ", groupTuples.Take(2).Select(DescribeSharedRepackTuple)) +
                                (groupTuples.Count > 2 ? $" (+{groupTuples.Count - 2:N0} tuple(s))" : string.Empty),
                        Tuples = groupTuples.Count,
                        Charts = groupTuples.Sum(tuple => tuple.Charts.Count),
                        Pages = pages.Count,
                        Before = before,
                        After = after,
                        PaddingVariant = split.PaddingVariant,
                        PaddingSavedBytes = split.PaddingSavedBytes,
                        NewlyBuilt = newlyBuilt,
                        Status = split.Applied
                            ? $"built, split into {pages.Count:N0} page(s): {split.BestSizes} (one page would be " +
                              $"{split.SingleSize}, {split.SingleBytes:N0} bytes)"
                            : "built",
                    };
                    if (joinedGroup)
                    {
                        info.Status += $" [joined {groupTuples.Select(tuple => tuple.StrictGroupKey).Distinct(StringComparer.Ordinal).Count():N0} " +
                                       $"tuple group(s) that differ only in variant textures of other units: {pages.Sum(page => page.BytesAfter):N0} " +
                                       $"bytes instead of {separateAfter:N0} separately]";
                    }

                    if (split.TightUsed)
                    {
                        info.Status += $" [tight pieces: {split.TightPieces:N0} piece(s) instead of " +
                                       $"{split.TightMergedCharts:N0} chart(s), pages {FormatSignedBytes(-split.TightSavedBytes)} bytes]";
                    }
                    else if (split.TightInconsistent)
                    {
                        info.Status += " [tight pieces not used: internal inconsistency, merged charts kept]";
                    }
                    info.Status += $" [padding: {split.PaddingVariant}; saves {split.PaddingSavedBytes:N0} bytes vs P0]";
                    if (newlyBuilt)
                        info.Status += " [built only after reduced padding]";
                    // Why the pages are not smaller, for a group that is left untouched.
                    var splitNote = split.Note != null
                        ? $"; {split.Note}"
                        : split.BestPages != 0
                            ? $"; {split.Clusters:N0} cluster(s), best split into {split.BestPages:N0} page(s) " +
                              $"{split.BestSizes} = {split.BestBytes:N0} bytes did not pay"
                            : $"; {split.Clusters:N0} cluster(s), no split into smaller pages found";
                    if (before - after <= 0 || before - after < result.MinimumGroupSavingBytes)
                    {
                        // A group the aspect restriction (exact emissive compensation) made too expensive.
                        if (split.AspectRestricted && split.UnrestrictedSingleBytes != 0 &&
                            before - split.UnrestrictedSingleBytes >= result.MinimumGroupSavingBytes)
                        {
                            if (split.EmissiveAspectRestricted)
                            {
                                result.EmissiveAspectLostGroupKeys.Add(label + "|" + group.Key);
                                foreach (var lostTuple in groupTuples)
                                    result.EmissiveAspectLostTupleKeys.Add(label + "|" + lostTuple.Key);
                            }
                            if (split.FlowAspectRestricted)
                            {
                                result.FlowAspectLostGroupKeys.Add(label + "|" + group.Key);
                                foreach (var lostTuple in groupTuples)
                                    result.FlowAspectLostTupleKeys.Add(label + "|" + lostTuple.Key);
                            }
                        }

                        foreach (var tuple in groupTuples)
                        {
                            var chartArea = groupTuples.Sum(item => item.Charts.Sum(chart => (long)chart.LayoutWidth * chart.LayoutHeight));
                            var referenceArea = groupTuples.Sum(item => (long)item.ReferenceWidth * item.ReferenceHeight);
                            var chartCoverage = referenceArea == 0 ? 0 : (double)chartArea / referenceArea;
                            var canvases = string.Join(" + ", pages.Select(page => $"{page.LayoutWidth}x{page.LayoutHeight}"));
                            tuple.Excluded = before - after <= 0
                                ? chartCoverage >= 0.8
                                    ? $"it uses nearly the whole texture (chart coverage {chartCoverage:P0}); the layout group " +
                                      $"would go {before:N0} -> {after:N0} bytes [{info.Sizes}]{splitNote}"
                                    : $"its charts cover {chartCoverage:P0} of the texture but pack into a {canvases} " +
                                      $"reference-pixel layout, so the group would not get smaller " +
                                      $"({before:N0} -> {after:N0} bytes) [{info.Sizes}]{splitNote}"
                                : $"its layout group saves {FormatSignedBytes(before - after)} bytes " +
                                  $"({before:N0} -> {after:N0}, chart coverage {chartCoverage:P0}), below the " +
                                  $"{result.MinimumGroupSavingBytes:N0}-byte group minimum [{info.Sizes}]{splitNote}";
                        }

                        gated = true;
                        waste = waste with
                        {
                            Sim = SimulateSharedRepackGroupPacking(
                                result, groupTuples, pages, maximumSide, before, after, true, info.Label),
                        };
                        merge.GroupWaste.Add(waste);
                        restart = true;
                        break;
                    }

                    if (split.AspectRestricted)
                    {
                        if (split.EmissiveAspectRestricted)
                        {
                            plan.AspectGroups++;
                            plan.AspectTuples += groupTuples.Count;
                            if (split.UnrestrictedSingleBytes != 0 && split.SingleBytes > split.UnrestrictedSingleBytes)
                                plan.AspectExtraBytes += split.SingleBytes - split.UnrestrictedSingleBytes;
                        }
                        if (split.FlowAspectRestricted)
                        {
                            plan.FlowAspectGroups++;
                            plan.FlowAspectTuples += groupTuples.Count;
                            if (split.UnrestrictedSingleBytes != 0 && split.SingleBytes > split.UnrestrictedSingleBytes)
                                plan.FlowAspectExtraBytes += split.SingleBytes - split.UnrestrictedSingleBytes;
                        }
                        var causes = string.Join(" and ", new[]
                        {
                            split.EmissiveAspectRestricted ? "emissive" : null,
                            split.FlowAspectRestricted ? "flow" : null,
                        }.Where(cause => cause != null));
                        info.Status += $" [canvases keep the reference aspect for {causes} compensation" +
                                       (split.UnrestrictedSingleBytes != 0
                                           ? $"; unrestricted single page {split.UnrestrictedSingleSize} = {split.UnrestrictedSingleBytes:N0} bytes"
                                           : string.Empty) + "]";
                    }

                    passSimPending.Add((passWaste.Count, groupTuples, pages, before, after, info.Label));
                    passWaste.Add(waste);
                    plan.Pages.AddRange(pages);
                    plan.Groups.Add(info);
                }

                if (!restart)
                {
                    foreach (var pending in passSimPending)
                    {
                        passWaste[pending.Index] = passWaste[pending.Index] with
                        {
                            Sim = SimulateSharedRepackGroupPacking(
                                result, pending.Tuples, pending.Pages, maximumSide, pending.Before, pending.After,
                                false, pending.Label),
                        };
                    }

                    merge.GroupWaste.AddRange(passWaste);
                    break;
                }
            }

            // Every excluded tuple is reported as a skipped group.
            foreach (var group in tuples.Where(tuple => tuple.Excluded != null)
                         .GroupBy(tuple => tuple.Excluded!, StringComparer.Ordinal))
            {
                plan.Groups.Add(new SharedRepackGroupInfo
                {
                    GroupKey = group.First().GroupKey,
                    Label = string.Join(" | ", group.Take(2).Select(DescribeSharedRepackTuple)) +
                            (group.Count() > 2 ? $" (+{group.Count() - 2:N0})" : string.Empty),
                    Tuples = group.Count(),
                    Before = group.SelectMany(tuple => tuple.AllMemberTextures).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Sum(texture => entryOf[texture].Layout!.ChainBytes),
                    PaddingVariant = group.First().PaddingVariant,
                    PaddingSavedBytes = group.First().PaddingSavedBytes,
                    Status = "left untouched: " + group.Key +
                             $" [padding: {group.First().PaddingVariant}; saves {group.First().PaddingSavedBytes:N0} bytes vs P0]",
                    Sizes = string.Join(" | ", group.Take(2).Select(DescribeSharedRepackTupleSizes)),
                });
            }

            foreach (var page in plan.Pages)
            {
                // The copied noise is not replaced: other models keep using it.
                foreach (var texture in page.Tuples.SelectMany(tuple => tuple.AllMemberTextures))
                {
                    plan.Replaced[texture] = entryOf[texture].Layout!.ChainBytes;
                    plan.ReplacedOwned[texture] = entryOf[texture].IsOwned;
                }
            }

            // Layout groups on the same geometry lay out the same charts alike: their identical pages are
            // written once. The per-group gate above still judges each group on its own pages.
            DeduplicateSharedRepackOutputs(plan);

            var replaced = plan.Replaced.Values.Sum();
            var newBytes = plan.Pages.Sum(page => page.BytesAfter);
            if (replaced - newBytes < result.MinimumSavingBytes)
            {
                failure = $"actual bytes: {replaced:N0} replaced -> {newBytes:N0} new saves " +
                          $"{FormatSignedBytes(replaced - newBytes)}, below the {result.MinimumSavingBytes:N0}-byte " +
                          "threshold";
                failureBucket = "bytes";
                return null;
            }

            // Materials and WSModel repoints per page.
            var reserved = new HashSet<string>(state.SharedRepackMaterialPaths, StringComparer.OrdinalIgnoreCase);
            foreach (var page in plan.Pages)
            {
                if (!BuildSharedRepackPageMaterials(state, page, wsByRigid, reserved, out failure))
                    return null;
            }

            // Every WSModel of a rigid must bind every moved (lod, part) of that rigid, or it
            // would sample the new UVs with an embedded material.
            foreach (var rigidGroup in plan.Pages.SelectMany(page => page.Parts)
                         .GroupBy(part => part.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                foreach (var ws in (wsByRigid.GetValueOrDefault(rigidGroup.Key) ?? [])
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var document = GetWsDocument(state, ws);
                    if (document == null)
                    {
                        failure = $"WSModel {ws} could not be read";
                        return null;
                    }

                    var bound = document.SelectNodes("/model/materials/material")?.Cast<XmlNode>()
                        .Select(node => TryParseIndex(node, "lod_index", out var lod) &&
                                        TryParseIndex(node, "part_index", out var partIndex)
                            ? (lod, partIndex)
                            : (-1, -1))
                        .ToHashSet() ?? [];
                    foreach (var part in rigidGroup)
                    {
                        if (!bound.Contains((part.Key.LodIndex, part.Key.PartIndex)))
                        {
                            failure = $"WSModel {ws} does not bind lod {part.Key.LodIndex} part " +
                                      $"{part.Key.PartIndex} of {rigidGroup.Key}, which would then use the new " +
                                      "UVs with its embedded material";
                            return null;
                        }
                    }
                }
            }

            return plan;
        }

        // Everything TryComposeSharedRepackOutput encodes from, as text: the output kind (slot, format,
        // colour space, header, size, mips), and per tuple that has the slot its source texture, exponent,
        // reference size and alignment (block-copied levels) and the rectangle of each of its charts on
        // the page (source crop, size, place). Rectangles of a page never overlap, so the entries are
        // sorted: tuple and chart order do not matter. Equal text means equal bytes.
        private static string GetSharedRepackOutputSignature(SharedRepackPage page, SharedRepackSlotOutput output)
        {
            var entries = new List<string>();
            foreach (var tuple in page.Tuples.Where(tuple => tuple.Slots.ContainsKey(output.Slot)))
            {
                var exponent = tuple.SourceExponent[output.Slot];
                var head = $"{Normalize(output.SourceOf(tuple)).ToLowerInvariant()}|{exponent.X},{exponent.Y}|" +
                           $"{tuple.ReferenceWidth}x{tuple.ReferenceHeight}|{tuple.Align}";
                foreach (var chart in page.Charts.Where(chart => chart.Tuple == tuple))
                {
                    entries.Add(
                        $"{head}|{chart.X0},{chart.Y0},{chart.Width},{chart.Height}|{chart.LayoutWidth}x{chart.LayoutHeight}" +
                        $"@{chart.PackX},{chart.PackY}");
                }
            }

            entries.Sort(StringComparer.Ordinal);
            return $"{output.Slot.ToLowerInvariant()}|{output.IsCopy}|{output.Format}|{output.Srgb}|{output.Dx10}|" +
                   $"{output.ExponentX},{output.ExponentY}|{output.Width}x{output.Height}|{output.MipLevels}|" +
                   string.Join(";", entries);
        }

        // Marks every output that has the signature of an earlier output of the plan (plan page order,
        // then slot order) as its duplicate: it takes the canonical output's path (so every material and
        // embedded reference built after this names it), and its bytes leave the page and group totals.
        private static void DeduplicateSharedRepackOutputs(SharedRepackSetPlan plan)
        {
            plan.DeduplicatedOutputs = 0;
            plan.DeduplicatedBytes = 0;
            var canonical = new Dictionary<string, SharedRepackSlotOutput>(StringComparer.Ordinal);
            var groupBytes = new Dictionary<string, (int Count, long Bytes)>(StringComparer.Ordinal);
            foreach (var page in plan.Pages)
            {
                foreach (var output in page.Outputs)
                {
                    var signature = GetSharedRepackOutputSignature(page, output);
                    if (!canonical.TryGetValue(signature, out var first))
                    {
                        canonical[signature] = output;
                        continue;
                    }

                    output.DuplicateOf = first;
                    output.PlannedPath = output.Path;
                    output.Path = first.Path;
                    page.BytesAfter -= output.Bytes;
                    plan.DeduplicatedOutputs++;
                    plan.DeduplicatedBytes += output.Bytes;
                    var (count, bytes) = groupBytes.GetValueOrDefault(page.GroupKey);
                    groupBytes[page.GroupKey] = (count + 1, bytes + output.Bytes);
                }
            }

            foreach (var info in plan.Groups.Where(info => info.Pages != 0))
            {
                if (!groupBytes.TryGetValue(info.GroupKey, out var shared))
                    continue;

                info.After -= shared.Bytes;
                info.Status += $" [deduplicated {shared.Count:N0} page(s) shared with another group, {shared.Bytes:N0} bytes]";
            }
        }

        // Several smaller pages for the clusters of a group: the cheapest (total chained bytes
        // of all slots, then fewer pages, then the sizes in text order) split into 2 to
        // SharedRepackMaximumSplitPages pages, every side a power of two within the single-page
        // canvas. The first page's size is tried for a bounded set of candidates and filled with
        // whole clusters, largest first; the rest is recursed on. Null when none was found.
        private static List<List<int>>? FindSharedRepackSplit(
            IReadOnlyList<(int Width, int Height)> sizes,
            List<List<int>> clusters,
            int limitWidth,
            int limitHeight,
            Func<int, int, long> pageBytes,
            out long bestBytes,
            out List<(int Width, int Height)> bestSizes)
            => FindSharedRepackSplitOnCanvases(
                sizes, clusters, limitWidth, limitHeight, pageBytes, out bestBytes, out bestSizes, null);

        // As above, but every page canvas must be accepted by the predicate (null: any). The search
        // is shared with the packing simulation (SharedRepackGroupSimulator.FindSplit): pages are
        // packed by the layout solver, and the greedy first-page fill is tried on rows (the original
        // fill, whose result wins ties) and on MaxRects pages.
        private static List<List<int>>? FindSharedRepackSplitOnCanvases(
            IReadOnlyList<(int Width, int Height)> sizes,
            List<List<int>> clusters,
            int limitWidth,
            int limitHeight,
            Func<int, int, long> pageBytes,
            out long bestBytes,
            out List<(int Width, int Height)> bestSizes,
            Func<int, int, bool>? canvasAllowed,
            SharedRepackLayoutSolver? solver = null)
        {
            solver ??= CreateSharedRepackLayoutSolver(0);
            var input = new SharedRepackSimInput
            {
                Sizes = sizes,
                ClusterOf = [],
                CanvasAllowed = canvasAllowed,
                PageBytes = pageBytes,
                MinimumSide = 16,
                MaximumPages = SharedRepackMaximumSplitPages,
                CandidatesPerLevel = SharedRepackSplitCandidatesPerLevel,
                MaximumAttempts = SharedRepackMaximumSplitAttempts,
            };
            return SharedRepackGroupSimulator.FindSplit(
                input, solver, clusters, limitWidth, limitHeight, out bestBytes, out bestSizes);
        }

        // One group's pages: the merged-chart layout, and, when some tuple merged components into a
        // chart, the tight-piece layout; the pieces are used only when their pages hold fewer bytes in
        // total (a tie, an excluded tuple or an inconsistent assignment keeps the merged charts, so a
        // group tight pieces do not help stays byte-identical). The chosen layout is left active on the
        // tuples.
        private static List<SharedRepackPage> PackSharedRepackGroup(
            string nameRigid,
            string identity,
            List<SharedRepackTuple> groupTuples,
            int maximumSide,
            int firstPageNumber,
            long minimumGroupSaving,
            out SharedRepackSplitInfo split,
            out SharedRepackTuple? excludeTuple,
            out string? excludeReason)
        {
            var maximumVariant = SharedRepackReducedPaddingDisabled
                ? 0
                : groupTuples.Min(tuple => tuple.MaximumPaddingVariantIndex);
            var variants = GetSharedRepackPaddingCandidates(groupTuples, maximumVariant);

            SharedRepackGroupRun? best = null;
            SharedRepackPaddingVariant? bestVariant = null;
            SharedRepackGroupRun? lastRun = null;
            SharedRepackPaddingVariant? lastVariant = null;
            long baselineBytes = 0;
            long bestBytes = long.MaxValue;
            foreach (var variant in variants)
            {
                ActivateSharedRepackPadding(groupTuples, variant);
                var run = PackSharedRepackGroupVariant(
                    nameRigid, identity, groupTuples, maximumSide, firstPageNumber, minimumGroupSaving);
                run.Split.PaddingVariant = variant.Name;
                lastRun = run;
                lastVariant = variant;
                if (variant.Name == "baseline")
                {
                    if (run.ExcludeTuple == null && run.Pages.Count != 0)
                        baselineBytes = run.Pages.Sum(page => page.BytesAfter);
                }

                if (run.ExcludeTuple != null || run.Pages.Count == 0)
                    continue;

                var bytes = run.Pages.Sum(page => page.BytesAfter);
                if (best == null || bytes <= bestBytes)
                {
                    best = run;
                    bestVariant = variant;
                    bestBytes = bytes;
                }
            }

            var chosen = bestVariant ?? variants.Single(variant => variant.Name == "baseline");
            var selected = ReferenceEquals(best, lastRun) || best == null && lastVariant?.Name == chosen.Name
                ? lastRun!
                : PackSharedRepackGroupVariantAfterPaddingActivation(
                    nameRigid, identity, groupTuples, maximumSide, firstPageNumber, minimumGroupSaving, chosen);
            var selectedBytes = selected.Pages.Sum(page => page.BytesAfter);
            var saved = bestVariant?.Name == "baseline" || baselineBytes == 0
                ? 0
                : Math.Max(0, baselineBytes - selectedBytes);
            selected.Split.PaddingVariant = chosen.Name;
            selected.Split.PaddingBaselineBytes = baselineBytes;
            selected.Split.PaddingSavedBytes = saved;
            foreach (var tuple in groupTuples)
            {
                tuple.PaddingVariant = chosen.Name;
                tuple.PaddingBaselineBytes = baselineBytes;
                tuple.PaddingSavedBytes = saved;
            }

            split = selected.Split;
            excludeTuple = selected.ExcludeTuple;
            excludeReason = selected.ExcludeReason;
            return selected.Pages;
        }

        private static SharedRepackGroupRun PackSharedRepackGroupVariantAfterPaddingActivation(
            string nameRigid,
            string identity,
            List<SharedRepackTuple> groupTuples,
            int maximumSide,
            int firstPageNumber,
            long minimumGroupSaving,
            SharedRepackPaddingVariant variant)
        {
            ActivateSharedRepackPadding(groupTuples, variant);
            var run = PackSharedRepackGroupVariant(
                nameRigid, identity, groupTuples, maximumSide, firstPageNumber, minimumGroupSaving);
            run.Split.PaddingVariant = variant.Name;
            return run;
        }

        private static List<SharedRepackPaddingVariant> GetSharedRepackPaddingCandidates(
            List<SharedRepackTuple> groupTuples,
            int maximumVariantIndex)
        {
            var variants = GetSharedRepackPaddingVariants(groupTuples)
                .Take(Math.Clamp(maximumVariantIndex + 1, 1, 3))
                .ToList();
            var unique = new List<SharedRepackPaddingVariant>();
            foreach (var variant in variants)
            {
                if (unique.Any(previous => groupTuples.All(tuple => previous.Tuples[tuple] == variant.Tuples[tuple])))
                    continue;
                unique.Add(variant);
            }

            unique.Reverse();
            return unique;
        }

        private static List<SharedRepackPaddingVariant> GetSharedRepackPaddingVariants(
            List<SharedRepackTuple> groupTuples)
        {
            Dictionary<SharedRepackTuple, (int Gutter, int Align)> Values(
                Func<SharedRepackTuple, int, int, (int Gutter, int Align)> choose)
            {
                var values = new Dictionary<SharedRepackTuple, (int Gutter, int Align)>();
                foreach (var tuple in groupTuples)
                {
                    var (downshift, cellDownshift) = GetSharedRepackDownshifts(tuple);
                    values[tuple] = choose(tuple, downshift, cellDownshift);
                }

                return values;
            }

            return
            [
                new("baseline", Values((tuple, _, _) => (tuple.Cell, tuple.Align))),
                new("align 16", Values((tuple, downshift, cellDownshift) =>
                    (tuple.Cell, Math.Min(tuple.Align, Math.Max(4 << cellDownshift, 16 << downshift))))),
                new("gutter 8 + align 8", Values((tuple, downshift, cellDownshift) =>
                    (Math.Min(tuple.Cell, 8 << downshift),
                     Math.Min(tuple.Align, Math.Max(4 << cellDownshift, 8 << downshift))))),
            ];
        }

        private static void ActivateSharedRepackPadding(
            List<SharedRepackTuple> groupTuples,
            SharedRepackPaddingVariant variant)
        {
            foreach (var tuple in groupTuples)
            {
                var (gutter, align) = variant.Tuples[tuple];
                if (tuple.Gutter != gutter || tuple.Align != align)
                    ResizeSharedRepackCharts(tuple, gutter, align);
                tuple.PaddingVariant = variant.Name;
            }
        }

        private static SharedRepackGroupRun PackSharedRepackGroupVariant(
            string nameRigid,
            string identity,
            List<SharedRepackTuple> groupTuples,
            int maximumSide,
            int firstPageNumber,
            long minimumGroupSaving)
        {
            ActivateSharedRepackLayout(groupTuples, false);
            var mergedPages = PackSharedRepackGroupLayout(
                nameRigid, identity, groupTuples, maximumSide, firstPageNumber, minimumGroupSaving,
                out var mergedSplit, out var mergedExclude, out var mergedReason);
            if (mergedExclude != null || groupTuples.All(tuple => tuple.TightCharts == null && tuple.TightInconsistent == null))
                return new(mergedPages, mergedSplit, mergedExclude, mergedReason);

            var mergedChartCount = groupTuples.Sum(tuple => tuple.Charts.Count);
            mergedSplit.TightTried = true;
            mergedSplit.TightMergedCharts = mergedChartCount;
            if (groupTuples.Any(tuple => tuple.TightInconsistent != null))
            {
                mergedSplit.TightInconsistent = true;
                return new(mergedPages, mergedSplit, null, null);
            }

            ActivateSharedRepackLayout(groupTuples, true);
            var tightPages = PackSharedRepackGroupLayout(
                nameRigid, identity, groupTuples, maximumSide, firstPageNumber, minimumGroupSaving,
                out var tightSplit, out var tightExclude, out _);
            var mergedBytes = mergedPages.Sum(page => page.BytesAfter);
            var tightBytes = tightPages.Sum(page => page.BytesAfter);
            if (tightExclude == null && tightPages.Count != 0 && tightBytes < mergedBytes)
            {
                tightSplit.TightTried = true;
                tightSplit.TightUsed = true;
                tightSplit.TightMergedCharts = mergedChartCount;
                tightSplit.TightPieces = groupTuples.Sum(tuple => tuple.Charts.Count);
                tightSplit.TightSavedBytes = mergedBytes - tightBytes;
                return new(tightPages, tightSplit, null, null);
            }

            // Re-layout merged charts after the tight run moves shared chart objects.
            ActivateSharedRepackLayout(groupTuples, false);
            var again = PackSharedRepackGroupLayout(
                nameRigid, identity, groupTuples, maximumSide, firstPageNumber, minimumGroupSaving,
                out var split, out var excludeTuple, out var excludeReason);
            split.TightTried = true;
            split.TightMergedCharts = mergedChartCount;
            return new(again, split, excludeTuple, excludeReason);
        }

        // One layout's pages. Charts that a part uses together form a cluster and stay on one
        // page (so do the pieces of one merged chart). Returns null with a tuple to exclude when a
        // cluster cannot fit at all.
        private static List<SharedRepackPage> PackSharedRepackGroupLayout(
            string nameRigid,
            string identity,
            List<SharedRepackTuple> groupTuples,
            int maximumSide,
            int firstPageNumber,
            long minimumGroupSaving,
            out SharedRepackSplitInfo split,
            out SharedRepackTuple? excludeTuple,
            out string? excludeReason)
        {
            split = new SharedRepackSplitInfo();
            excludeTuple = null;
            excludeReason = null;
            var first = groupTuples[0];
            var slots = first.Slots.Keys.ToList();
            var exponents = slots.ToDictionary(slot => slot, slot => first.SourceExponent[slot]);
            var maximumWidth = maximumSide >> Math.Max(0, exponents.Values.Max(exponent => exponent.X));
            var maximumHeight = maximumSide >> Math.Max(0, exponents.Values.Max(exponent => exponent.Y));
            var cell = first.Align;
            // Exact emissive compensation needs equal U and V scales (reference / layout), so the
            // canvases of such a group keep the reference's aspect ratio.
            Func<int, int, bool>? canvasAllowed = first.RequiresEqualAxisScale
                ? (width, height) => (long)width * first.ReferenceHeight == (long)height * first.ReferenceWidth
                : null;
            split.AspectRestricted = canvasAllowed != null;
            split.EmissiveAspectRestricted = first.EmissiveCompensation;
            split.FlowAspectRestricted = first.FlowCompensation;
            var solver = CreateSharedRepackLayoutSolver(cell);
            foreach (var tuple in groupTuples)
            {
                foreach (var chart in tuple.Charts)
                {
                    chart.LayoutWidth = RoundUpTo(chart.Width, cell);
                    chart.LayoutHeight = RoundUpTo(chart.Height, cell);
                    // Sizing invariant: a chart's rectangle in a slot never exceeds that slot's texture.
                    foreach (var slot in slots)
                    {
                        var layout = tuple.Layouts[slot];
                        var exponent = tuple.SourceExponent[slot];
                        if (ScaleByExponent(chart.LayoutWidth, exponent.X) > layout.Width ||
                            ScaleByExponent(chart.LayoutHeight, exponent.Y) > layout.Height)
                        {
                            excludeTuple = tuple;
                            excludeReason = $"internal sizing error: a {chart.LayoutWidth}x{chart.LayoutHeight} " +
                                            $"chart (reference pixels) is larger than the {layout.Width}x{layout.Height} " +
                                            $"{slot} texture";
                            return [];
                        }
                    }

                    if (chart.LayoutWidth > maximumWidth || chart.LayoutHeight > maximumHeight)
                    {
                        excludeTuple = tuple;
                        excludeReason = $"a {chart.LayoutWidth}x{chart.LayoutHeight} chart (reference pixels) does " +
                                        $"not fit: the largest slot is limited to {maximumSide} output pixels per " +
                                        $"side, which is {maximumWidth}x{maximumHeight} reference pixels here";
                        return [];
                    }
                }
            }

            // Clusters: charts joined by the parts that use them.
            var charts = groupTuples.SelectMany(tuple => tuple.Charts).ToList();
            var chartIndex = new Dictionary<SharedRepackChart, int>();
            for (var index = 0; index < charts.Count; index++)
                chartIndex[charts[index]] = index;
            var parent = Enumerable.Range(0, charts.Count).ToArray();
            int Find(int node)
            {
                while (parent[node] != node)
                {
                    parent[node] = parent[parent[node]];
                    node = parent[node];
                }

                return node;
            }

            foreach (var tuple in groupTuples)
            {
                foreach (var part in tuple.Parts)
                {
                    int? head = null;
                    for (var vertex = 0; vertex < part.VertexChart.Length; vertex++)
                    {
                        if (!part.Used[vertex])
                            continue;

                        var id = chartIndex[tuple.Charts[part.VertexChart[vertex]]];
                        if (head == null)
                            head = id;
                        else
                            parent[Find(id)] = Find(head.Value);
                    }
                }
            }

            // The pieces of one merged chart stay together, as the chart did.
            foreach (var tuple in groupTuples)
            {
                var head = new Dictionary<int, int>();
                foreach (var chart in tuple.Charts.Where(chart => chart.MergedId >= 0))
                {
                    var id = chartIndex[chart];
                    if (head.TryGetValue(chart.MergedId, out var sibling))
                        parent[Find(id)] = Find(sibling);
                    else
                        head[chart.MergedId] = id;
                }
            }

            var clusters = Enumerable.Range(0, charts.Count)
                .GroupBy(Find)
                .Select(group => group.OrderBy(index => index).ToList())
                .OrderByDescending(cluster => cluster.Sum(index => (long)charts[index].LayoutWidth * charts[index].LayoutHeight))
                .ThenBy(cluster => cluster[0])
                .ToList();

            var assignments = new List<List<int>>();
            var allIndexes = Enumerable.Range(0, charts.Count).ToList();
            var sizes = allIndexes.Select(index => (charts[index].LayoutWidth, charts[index].LayoutHeight)).ToList();
            split.Clusters = clusters.Count;
            if (TryPackSharedRepackLayoutOnCanvases(
                    sizes, maximumWidth, maximumHeight, out var singleWidth, out var singleHeight, out _, canvasAllowed, solver))
            {
                assignments.Add(allIndexes);

                // Power-of-two rounding can leave one page as large as the sources although the
                // charts cover far less. Whole clusters (a part's charts stay together, nothing
                // is duplicated) can then go onto several smaller pages.
                // A page also costs one more output per variant texture of a slot (at most, when the
                // whole group sits on it).
                var variantPages = slots.ToDictionary(
                    slot => slot,
                    slot => groupTuples
                        .Where(tuple => tuple.VariantTextures.ContainsKey(slot))
                        .SelectMany(tuple => tuple.VariantTextures[slot])
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count());
                long PageBytes(int width, int height)
                {
                    long total = 0;
                    foreach (var slot in slots)
                    {
                        var w = ScaleByExponent(width, exponents[slot].X);
                        var h = ScaleByExponent(height, exponents[slot].Y);
                        total += EstimateDdsChainBytes(first.Layouts[slot], w, h, GetFullMipChainLength(w, h)) *
                                 (1 + variantPages[slot]);
                    }

                    return total;
                }

                var sources = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var tuple in groupTuples)
                {
                    // A copied texture stays: it is not a source the pages replace.
                    foreach (var slot in slots.Where(slot => !tuple.IsGeneralCopySlot(slot)))
                    {
                        sources[tuple.Slots[slot]] = tuple.Layouts[slot].ChainBytes;
                        foreach (var variant in tuple.VariantTextures.GetValueOrDefault(slot) ?? [])
                            sources[variant] = tuple.Layouts[slot].ChainBytes;
                    }
                }

                split.BeforeBytes = sources.Values.Sum();
                split.SingleBytes = PageBytes(singleWidth, singleHeight);
                split.SingleSize = $"{singleWidth}x{singleHeight}";
                if (canvasAllowed != null &&
                    TryPackSharedRepackLayoutOnCanvases(
                        sizes, maximumWidth, maximumHeight, out var freeWidth, out var freeHeight, out _, null, solver))
                {
                    split.UnrestrictedSingleBytes = PageBytes(freeWidth, freeHeight);
                    split.UnrestrictedSingleSize = $"{freeWidth}x{freeHeight}";
                }
                if (clusters.Count < 2)
                {
                    split.OneCluster = true;
                    split.Note = "one connected cluster: cannot split";
                }
                else
                {
                    var pagesFound = FindSharedRepackSplitOnCanvases(
                        sizes, clusters, singleWidth, singleHeight, PageBytes, out var bestBytes, out var bestSizes,
                        canvasAllowed, solver);
                    if (pagesFound != null)
                    {
                        split.BestBytes = bestBytes;
                        split.BestSizes = string.Join(" + ", bestSizes.Select(size => $"{size.Width}x{size.Height}"));
                        split.BestPages = pagesFound.Count;
                        var singleBuilds = split.BeforeBytes - split.SingleBytes >= minimumGroupSaving;
                        if (bestBytes < split.SingleBytes &&
                            split.BeforeBytes - bestBytes >= minimumGroupSaving &&
                            (!singleBuilds || bestBytes <= split.SingleBytes - minimumGroupSaving))
                        {
                            split.Applied = true;
                            assignments = pagesFound;
                        }
                    }
                }
            }
            else
            {
                split.Note = "already split by the side limit";
                foreach (var cluster in clusters)
                {
                    var placed = false;
                    foreach (var page in assignments)
                    {
                        var trial = page.Concat(cluster).ToList();
                        var trialSizes = trial.Select(index => (charts[index].LayoutWidth, charts[index].LayoutHeight)).ToList();
                        if (trialSizes.Sum(size => (long)size.LayoutWidth * size.LayoutHeight) <=
                            (long)maximumWidth * maximumHeight &&
                            TryPackSharedRepackLayoutOnCanvases(
                                trialSizes, maximumWidth, maximumHeight, out _, out _, out _, canvasAllowed, solver))
                        {
                            page.AddRange(cluster);
                            placed = true;
                            break;
                        }
                    }

                    if (placed)
                        continue;

                    var alone = cluster.Select(index => (charts[index].LayoutWidth, charts[index].LayoutHeight)).ToList();
                    if (!TryPackSharedRepackLayoutOnCanvases(alone, maximumWidth, maximumHeight, out _, out _, out _, canvasAllowed, solver))
                    {
                        excludeTuple = charts[cluster[0]].Tuple;
                        excludeReason = $"the charts of one part ({cluster.Count:N0} chart(s)) do not fit a " +
                                        $"{maximumWidth}x{maximumHeight} layout";
                        return [];
                    }

                    assignments.Add(cluster.ToList());
                }
            }

            var groupTextures = string.Join(
                "|",
                groupTuples.SelectMany(tuple => tuple.Slots.Values)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.Ordinal));
            var pages = new List<SharedRepackPage>();
            foreach (var assignment in assignments.Select(page => page.OrderBy(index => index).ToList()))
            {
                var pageSizes = assignment.Select(index => (charts[index].LayoutWidth, charts[index].LayoutHeight)).ToList();
                TryPackSharedRepackLayoutOnCanvases(
                    pageSizes, maximumWidth, maximumHeight, out var width, out var height, out var positions, canvasAllowed, solver);
                var hash = StableHash($"{identity}|{groupTextures}|{first.GroupKey}|{pages.Count}");
                var page = new SharedRepackPage
                {
                    Rigid = nameRigid,
                    Hash = hash,
                    GroupKey = first.GroupKey,
                    Index = firstPageNumber + pages.Count,
                    LayoutWidth = width,
                    LayoutHeight = height,
                };
                for (var position = 0; position < assignment.Count; position++)
                {
                    var chart = charts[assignment[position]];
                    chart.PackX = positions[position].X;
                    chart.PackY = positions[position].Y;
                    chart.Page = page;
                    page.Charts.Add(chart);
                }

                page.Tuples.AddRange(groupTuples.Where(tuple => tuple.Charts.Any(chart => chart.Page == page)));
                foreach (var tuple in page.Tuples)
                {
                    foreach (var part in tuple.Parts)
                    {
                        var vertex = Array.FindIndex(part.Used, used => used);
                        if (vertex >= 0 && tuple.Charts[part.VertexChart[vertex]].Page == page)
                            page.Parts.Add(part);
                    }
                }

                foreach (var slot in slots)
                {
                    var isCopy = first.CopySlots.Contains(slot);
                    var suffix = GetSharedRepackOutputSuffix(slot);
                    var layout = first.Layouts[slot];
                    var output = new SharedRepackSlotOutput
                    {
                        Slot = slot,
                        IsCopy = isCopy,
                        Path = Normalize(
                            $@"{AtlasDirectory}\{SafeName(Path.GetFileNameWithoutExtension(nameRigid))}_{hash}_repack_{suffix}.dds"),
                        Format = layout.Format,
                        Srgb = layout.IsSrgb,
                        Dx10 = groupTuples.Any(tuple => tuple.Layouts[slot].IsDx10) || layout.IsSrgb,
                        ExponentX = exponents[slot].X,
                        ExponentY = exponents[slot].Y,
                        Template = layout,
                    };
                    output.Sources.AddRange(page.Tuples
                        .Where(tuple => tuple.Slots.ContainsKey(slot))
                        .Select(tuple => tuple.Slots[slot])
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(path => path, StringComparer.Ordinal));
                    output.Width = ScaleByExponent(width, output.ExponentX);
                    output.Height = ScaleByExponent(height, output.ExponentY);
                    output.MipLevels = GetFullMipChainLength(output.Width, output.Height);
                    output.Bytes = EstimateDdsChainBytes(layout, output.Width, output.Height, output.MipLevels);
                    page.BytesAfter += output.Bytes;
                    page.Outputs.Add(output);
                }

                AddSharedRepackVariantOutputs(page, nameRigid);
                pages.Add(page);
            }

            // Sizing invariant: no output texture exceeds the side limit. (An output with more
            // pixels than its sources is not impossible, only a layout that does not pay; the
            // group savings gate rejects it with its coverage.)
            foreach (var page in pages)
            {
                foreach (var output in page.Outputs)
                {
                    if (output.Width > maximumSide || output.Height > maximumSide)
                    {
                        var sources = string.Join(
                            "/",
                            groupTuples.Where(tuple => tuple.Layouts.ContainsKey(output.Slot))
                                .Select(tuple => $"{tuple.Layouts[output.Slot].Width}x{tuple.Layouts[output.Slot].Height}")
                                .Distinct());
                        excludeTuple = groupTuples[0];
                        excludeReason = $"internal sizing error: the planned {output.Slot} output is " +
                                        $"{output.Width}x{output.Height} (layout {page.LayoutWidth}x{page.LayoutHeight} " +
                                        $"reference pixels, exponent {output.ExponentX},{output.ExponentY}; source " +
                                        $"{sources}), over the {maximumSide} limit";
                        return [];
                    }
                }
            }

            return pages;
        }

        // A group of tuples that differ only in foreign variants (variant textures other units load than
        // the layout texture) is kept when its pages are smaller than the pages of the separate groups and
        // no variant page of a foreign texture is bigger than that texture. The units that load the
        // texture load the page instead (and nothing else changes for them), so none of them loads more
        // than before, whatever else the page holds (the crops of the other tuples' layout textures, which
        // no material of those units samples there).
        private static bool AcceptSharedRepackJoinedGroup(
            List<SharedRepackPage> pages,
            long separateAfter,
            Dictionary<string, VanillaCoverageTexture> entryOf)
        {
            if (pages.Sum(page => page.BytesAfter) >= separateAfter)
                return false;

            return pages.All(page => page.Outputs.Where(output => output.IsVariant).All(output =>
                !page.Tuples.Any(tuple => tuple.ForeignVariantTextures.Contains(output.Sources[0])) ||
                output.Bytes <= entryOf[output.Sources[0]].Layout!.ChainBytes));
        }

        // Variant pages. For a slot where a tuple of the page has variant textures (its parts' other
        // materials name them), each such texture gets one more output with the layout page's size and
        // chart positions: the tuples that have the texture as a variant crop their charts from it, every
        // other tuple from its layout texture. A material naming the texture uses that output (same
        // layout, same UVs), so a part keeps one UV transform for all its bindings. When every tuple's
        // layout texture is some tuple's variant, no material names a layout page and it is dropped.
        private static void AddSharedRepackVariantOutputs(SharedRepackPage page, string nameRigid)
        {
            var directParts = page.Parts.Any(part => part.DirectEmbeddedMaterial);
            foreach (var layoutOutput in page.Outputs.ToList())
            {
                var slot = layoutOutput.Slot;
                var variantTuples = page.Tuples.Where(tuple => tuple.VariantTextures.ContainsKey(slot)).ToList();
                if (variantTuples.Count == 0)
                    continue;

                var textures = variantTuples
                    .SelectMany(tuple => tuple.VariantTextures[slot])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(texture => texture, StringComparer.Ordinal)
                    .ToList();
                var suffix = Path.GetFileNameWithoutExtension(layoutOutput.Path)
                    .Split("_repack_")[^1];
                foreach (var texture in textures)
                {
                    var sources = variantTuples
                        .Where(tuple => tuple.VariantTextures[slot].Contains(texture, StringComparer.OrdinalIgnoreCase))
                        .ToDictionary(tuple => tuple.Index, _ => texture);
                    var output = new SharedRepackSlotOutput
                    {
                        Slot = slot,
                        VariantSources = sources,
                        Path = Normalize(
                            $@"{AtlasDirectory}\{SafeName(Path.GetFileNameWithoutExtension(nameRigid))}_{page.Hash}_repack_{suffix}_v{StableHash(page.Hash + "|" + slot + "|" + Normalize(texture))}.dds"),
                        Format = layoutOutput.Format,
                        Srgb = layoutOutput.Srgb,
                        Dx10 = layoutOutput.Dx10,
                        ExponentX = layoutOutput.ExponentX,
                        ExponentY = layoutOutput.ExponentY,
                        Width = layoutOutput.Width,
                        Height = layoutOutput.Height,
                        MipLevels = layoutOutput.MipLevels,
                        Bytes = layoutOutput.Bytes,
                        Template = layoutOutput.Template,
                    };
                    output.Sources.Add(texture);
                    page.BytesAfter += output.Bytes;
                    page.Outputs.Add(output);
                    page.VariantOutputs[(slot.ToLowerInvariant(), Normalize(texture).ToLowerInvariant())] = output;
                }

                var variantSet = textures.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var layoutUsed = page.Tuples.Any(tuple =>
                    tuple.Slots.TryGetValue(slot, out var layoutTexture) && !variantSet.Contains(layoutTexture));
                if (!layoutUsed && !directParts)
                {
                    page.Outputs.Remove(layoutOutput);
                    page.BytesAfter -= layoutOutput.Bytes;
                }
            }
        }

        // The output a material that names the texture in the slot uses on the page.
        private static SharedRepackSlotOutput GetSharedRepackMaterialOutput(SharedRepackPage page, string slot, string texture)
            => page.VariantOutputs.TryGetValue((slot.ToLowerInvariant(), Normalize(texture).ToLowerInvariant()), out var variant)
                ? variant
                : page.Outputs.Single(item => !item.IsVariant && item.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase));

        // Generated copies of the page's bound materials and the WSModel nodes to repoint. A
        // source material used by parts of several rigids gets one copy per page. Names already
        // taken by an earlier page or set (reserved) are never reused.
        private bool BuildSharedRepackPageMaterials(
            BatchState state,
            SharedRepackPage page,
            Dictionary<string, HashSet<string>> wsByRigid,
            HashSet<string> reserved,
            out string? failure)
        {
            failure = null;
            foreach (var material in page.Parts.SelectMany(part => part.Materials)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                var file = ResolveGameplayTraversalContainer(
                        state.Source, GetGameplayTraversalContainers(state), material, state.ResolutionProviders)?
                    .FindFile(material);
                if (file == null)
                {
                    failure = $"material {material} could not be read";
                    return false;
                }

                var document = new XmlDocument();
                document.LoadXml(LoadXml(file).OuterXml);
                var user = page.Parts.First(part => part.Materials.Contains(material));
                var userTuple = page.Tuples.First(item => item.Parts.Contains(user));
                foreach (var slot in userTuple.Slots.Keys)
                {
                    // The material's own texture of the slot picks the layout page or its variant page.
                    var ownTexture = user.MaterialSlots.TryGetValue(material, out var own) && own.TryGetValue(slot, out var named)
                        ? named
                        : userTuple.Slots[slot];
                    var output = GetSharedRepackMaterialOutput(page, slot, ownTexture);
                    SetTexturePath(document, slot, output.Path);
                }

                // UV0 scale of the tuple: reference size over layout size. The blood overlay
                // samples UV0 x blood_uv_scale, so the scale is divided out to keep its
                // pattern density on the mesh (ASSUMPTION about the shader, to be checked in game).
                var tuple = page.Tuples.First(item => item.Parts.Contains(user));
                var scaleU = (double)tuple.ReferenceWidth / page.LayoutWidth;
                var scaleV = (double)tuple.ReferenceHeight / page.LayoutHeight;
                if (TryScaleSharedRepackBloodParameter(document, scaleU, scaleV, out var bloodText))
                {
                    page.BloodScales.Add(
                        $"{material}: UV scale {scaleU:0.#####} x {scaleV:0.#####}, {SharedRepackBloodScaleParameter} -> {bloodText}");
                }

                // The dissolve detail texture is sampled at UV0 * dissolve_uv_scale: divide the scale per axis
                // (unequal U and V scales are fine) so the detail keeps its density on the mesh. ASSUMPTION
                // about the shader, verified on the compiled shaders; the per-chart phase offset is checked in game.
                if (GetTexturePath(document, SharedRepackDissolveDetailSlot).Length != 0 &&
                    SharedRepackDissolveVerifiedShaders.Contains(
                        Normalize(document.SelectSingleNode("/material/shader")?.InnerText).Replace('\\', '/')))
                {
                    if (!TryScaleSharedRepackFloat2Parameter(
                            document, SharedRepackDissolveScaleParameter, scaleU, scaleV, out var dissolveText))
                    {
                        failure = $"material {material}: the material lacks a usable dissolve parameter " +
                                  SharedRepackDissolveScaleParameter;
                        return false;
                    }

                    page.DissolveScales.Add(
                        $"{material}: UV scale {scaleU:0.#####} x {scaleV:0.#####}, {SharedRepackDissolveScaleParameter} -> {dissolveText}");
                }

                // The emissive glow scrolls over UV0: rescale its parameters by the same factor (equal in
                // U and V: the group's canvases keep the reference aspect). ASSUMPTION about the shader
                // chain, verified on the compiled shaders; the per-chart phase offset is checked in game.
                if (tuple.EmissiveCompensation)
                {
                    if (!TryScaleSharedRepackEmissiveParameters(document, scaleU, scaleV, out var emissiveText))
                    {
                        failure = $"material {material}: {emissiveText}";
                        return false;
                    }

                    page.EmissiveScales.Add($"{material}: {emissiveText}");
                }

                if (tuple.FlowCompensation &&
                    tuple.Parts.Any(part => part.FlowCompensationMaterials.Contains(material)))
                {
                    if (!TryScaleSharedRepackFlowParameters(document, scaleU, scaleV, out var flowText))
                    {
                        failure = $"material {material}: {flowText}";
                        return false;
                    }

                    page.FlowScales.Add($"{material}: {flowText}");
                }

                // Source-pack materials get a copy next to them. A vanilla material is never
                // copied into a vanilla folder: the copy goes to a mod-owned folder, and the
                // name is extended until it collides with nothing in the source or vanilla.
                var stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(material));
                var directory = state.Source.ContainsFile(material)
                    ? Path.GetDirectoryName(material) ?? string.Empty
                    : SharedRepackVanillaMaterialDirectory;
                var newPath = string.Empty;
                for (var salt = 0; salt < 8; salt++)
                {
                    newPath = Normalize(Path.Combine(
                        directory,
                        $"{SafeName(stem)}_repack_{StableHash(page.Hash + "|" + material + (salt == 0 ? string.Empty : "|" + salt))}.xml.material"));
                    if (!state.Source.ContainsFile(newPath) && !OverridesVanillaPath(state, newPath) &&
                        !reserved.Contains(newPath))
                    {
                        break;
                    }
                }

                if (reserved.Contains(newPath))
                {
                    failure = $"no free name for the generated copy of material {material}";
                    return false;
                }

                reserved.Add(newPath);
                page.MaterialMap[material] = newPath;
                page.MaterialDocuments[material] = document;
            }

            // Each rigid's own WSModels, in rigid order.
            foreach (var rigidGroup in page.Parts
                         .GroupBy(part => part.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                foreach (var ws in (wsByRigid.GetValueOrDefault(rigidGroup.Key) ?? [])
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var document = GetWsDocument(state, ws);
                    if (document == null)
                    {
                        failure = $"WSModel {ws} could not be read";
                        return false;
                    }

                    var nodes = document.SelectNodes("/model/materials/material");
                    if (nodes == null)
                        continue;

                    foreach (XmlNode node in nodes)
                    {
                        if (!TryParseIndex(node, "lod_index", out var lod) ||
                            !TryParseIndex(node, "part_index", out var partIndex))
                        {
                            continue;
                        }

                        var part = rigidGroup.FirstOrDefault(item =>
                            item.Key.LodIndex == lod && item.Key.PartIndex == partIndex);
                        if (part == null)
                            continue;

                        var current = Normalize(node.InnerText);
                        if (!page.MaterialMap.ContainsKey(current))
                        {
                            failure = $"WSModel {ws} binds {current} for a moved part, which is not one of its materials";
                            return false;
                        }

                        page.WsRepoints.Add((node, current));
                        page.WsModels.Add(ws);
                    }
                }
            }

            return true;
        }

        // ---------------------------------------------------------------------------
        // Sources, composing, block copy.
        // ---------------------------------------------------------------------------

        private sealed class SharedRepackSource
        {
            public byte[] Dds { get; init; } = [];
            public DdsLayout Layout { get; init; } = null!;
            private readonly Dictionary<int, (byte[] Bgra, int Width, int Height)?> _levels = [];

            // Decoded pixels of one mip level (the source's own level, not a resample).
            public (byte[] Bgra, int Width, int Height)? Level(int level)
            {
                if (_levels.TryGetValue(level, out var cached))
                    return cached;

                (byte[] Bgra, int Width, int Height)? result = null;
                var dds = Dds;
                var ok = true;
                if (level > 0)
                    ok = TrySliceDdsMips(Dds, Layout, level, out dds, out _);
                if (ok && TryDecodeDdsTopLevel(dds, out var bgra, out var width, out var height, out _))
                    result = (bgra, width, height);
                _levels[level] = result;
                return result;
            }
        }

        private sealed class SharedRepackSourceCache
        {
            private readonly Func<string, byte[]?> _read;
            private readonly Dictionary<string, SharedRepackSource?> _sources = new(StringComparer.OrdinalIgnoreCase);

            public SharedRepackSourceCache(Func<string, byte[]?> read)
            {
                _read = read;
            }

            public SharedRepackSource? Get(string path)
            {
                if (_sources.TryGetValue(path, out var cached))
                    return cached;

                SharedRepackSource? source = null;
                var bytes = _read(path);
                if (bytes != null &&
                    TryReadDdsLayout(
                        bytes.AsSpan(0, Math.Min(bytes.Length, TextureDownscaleHeaderPeekBytes)).ToArray(),
                        out var layout))
                {
                    source = new SharedRepackSource { Dds = bytes, Layout = layout };
                }

                _sources[path] = source;
                return source;
            }

            public SharedRepackSource? FromBytes(string key, byte[] dds)
            {
                if (!TryReadDdsLayout(
                        dds.AsSpan(0, Math.Min(dds.Length, TextureDownscaleHeaderPeekBytes)).ToArray(),
                        out var layout))
                {
                    return null;
                }

                var source = new SharedRepackSource { Dds = dds, Layout = layout };
                _sources[key] = source;
                return source;
            }
        }

        // The tuples' charts mapped to a slot's pixels at level 0: in-bounds source rectangle
        // and the destination origin.
        private static IEnumerable<(SharedRepackChart Chart, int SourceX, int SourceY, int Width, int Height, int DestinationX, int DestinationY)>
            EnumerateSharedRepackSlotCharts(SharedRepackPage page, SharedRepackSlotOutput output, SharedRepackTuple tuple)
        {
            var exponent = tuple.SourceExponent[output.Slot];
            foreach (var chart in page.Charts.Where(chart => chart.Tuple == tuple))
            {
                var x0 = Math.Max(chart.X0, 0);
                var y0 = Math.Max(chart.Y0, 0);
                var x1 = Math.Min(chart.X0 + chart.Width, tuple.ReferenceWidth);
                var y1 = Math.Min(chart.Y0 + chart.Height, tuple.ReferenceHeight);
                if (x1 <= x0 || y1 <= y0)
                    continue;

                var sourceX = ScaleByExponent(x0, exponent.X);
                var sourceY = ScaleByExponent(y0, exponent.Y);
                yield return (
                    chart,
                    sourceX,
                    sourceY,
                    ScaleByExponent(x1 - x0, exponent.X),
                    ScaleByExponent(y1 - y0, exponent.Y),
                    ScaleByExponent(chart.PackX, output.ExponentX) + (sourceX - ScaleByExponent(chart.X0, exponent.X)),
                    ScaleByExponent(chart.PackY, output.ExponentY) + (sourceY - ScaleByExponent(chart.Y0, exponent.Y)));
            }
        }

        // Top levels whose chart rectangles are whole BC blocks at that level in the source and
        // the destination, with the source still having the level.
        private static int GetSharedRepackBlockLevels(SharedRepackPage page, SharedRepackSlotOutput output, SharedRepackSourceCache sources)
        {
            var levels = output.MipLevels;
            foreach (var tuple in page.Tuples.Where(tuple => tuple.Slots.ContainsKey(output.Slot)))
            {
                var source = sources.Get(output.SourceOf(tuple));
                if (source == null ||
                    !source.Layout.Format.Equals(output.Format, StringComparison.Ordinal) ||
                    source.Layout.IsSrgb != output.Srgb ||
                    tuple.SourceExponent[output.Slot] != (output.ExponentX, output.ExponentY))
                {
                    return 0;
                }

                var cellX = ScaleByExponent(tuple.Align, output.ExponentX);
                var cellY = ScaleByExponent(tuple.Align, output.ExponentY);
                var aligned = 0;
                while (aligned < 16 && (cellX >> aligned) >= 4 && (cellY >> aligned) >= 4 &&
                       (cellX >> aligned) % 4 == 0 && (cellY >> aligned) % 4 == 0)
                {
                    aligned++;
                }

                levels = Math.Min(levels, Math.Min(aligned, source.Layout.MipCount));
            }

            return levels;
        }

        private static bool IsSharedRepackBlockCopiedAtLevel(SharedRepackSlotOutput output, int level)
            => level >= 0 && level < output.BlockCopiedLevels;

        private static long GetSharedRepackLevelOffset(DdsLayout layout, int level)
        {
            long offset = layout.HeaderSize;
            for (var index = 0; index < level; index++)
                offset += layout.LevelBytes(index);
            return offset;
        }

        // Computes the copy fates before encoding so the gathered encoder can skip blocks that will be
        // overwritten by the source copy. The small destination buffers only make the existing copier
        // perform its geometric marking; the real copy and its verification still happen below.
        private static bool TryBuildSharedRepackBlockCopyPlan(
            SharedRepackPage page,
            SharedRepackSlotOutput output,
            SharedRepackSourceCache sources,
            CancellationToken cancellationToken,
            out SharedRepackBlockCopyPlan plan,
            out string error)
        {
            plan = null!;
            error = string.Empty;
            var bytesPerBlock = output.Template.BytesPerBlock;
            if (bytesPerBlock is not (8 or 16))
            {
                error = $"{output.Format} has unsupported block size {bytesPerBlock}";
                return false;
            }

            var blocksWide = new int[output.MipLevels];
            var blocksHigh = new int[output.MipLevels];
            var fates = new SharedRepackBlockFate[output.MipLevels][];
            var copied = new bool[output.MipLevels][];
            var users = page.Tuples.Where(tuple => tuple.Slots.ContainsKey(output.Slot)).ToList();

            for (var level = 0; level < output.MipLevels; level++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var levelWidth = Math.Max(1, output.Width >> level);
                var levelHeight = Math.Max(1, output.Height >> level);
                blocksWide[level] = SharedRepackBlockCopier.BlocksFor(levelWidth);
                blocksHigh[level] = SharedRepackBlockCopier.BlocksFor(levelHeight);
                var levelFates = new SharedRepackBlockFate[blocksWide[level] * blocksHigh[level]];
                var scratch = new byte[checked(levelFates.Length * bytesPerBlock)];

                foreach (var tuple in users)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = sources.Get(output.SourceOf(tuple));
                    foreach (var item in EnumerateSharedRepackSlotCharts(page, output, tuple))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var width = item.Width >> level;
                        var height = item.Height >> level;
                        var destinationX = item.DestinationX >> level;
                        var destinationY = item.DestinationY >> level;
                        if (width <= 0 || height <= 0)
                        {
                            // The chart has shrunk below a texel, but the box filter still draws it: its
                            // blocks hold visible texels, not padding.
                            var lastX = (item.DestinationX + item.Width + (1 << level) - 1) >> level;
                            var lastY = (item.DestinationY + item.Height + (1 << level) - 1) >> level;
                            SharedRepackBlockCopier.Mark(
                                levelFates, blocksWide[level], blocksHigh[level], destinationX, destinationY,
                                Math.Max(1, lastX - destinationX), Math.Max(1, lastY - destinationY),
                                SharedRepackBlockFate.Generated);
                            continue;
                        }

                        var reason = SharedRepackBlockCopier.GetFallbackReason(
                            source != null,
                            source?.Layout.Format ?? string.Empty,
                            source?.Layout.BytesPerBlock ?? 0,
                            output.Format,
                            bytesPerBlock,
                            source?.Layout.MipCount ?? 0,
                            source?.Dds.LongLength ?? 0,
                            source == null ? 0 : source.Layout.HeaderSize + source.Layout.ChainBytes,
                            level,
                            item.SourceX,
                            item.SourceY,
                            item.DestinationX,
                            item.DestinationY);

                        if (reason != null)
                        {
                            SharedRepackBlockCopier.Mark(
                                levelFates, blocksWide[level], blocksHigh[level], destinationX, destinationY,
                                width, height, reason.Value);
                            continue;
                        }

                        var sourceLayout = source!.Layout;
                        var sourceLevelWidth = Math.Max(1, sourceLayout.Width >> level);
                        var sourceLevelHeight = Math.Max(1, sourceLayout.Height >> level);
                        var sourceX = item.SourceX >> level;
                        var sourceY = item.SourceY >> level;
                        var sourceOffset = checked((int)GetSharedRepackLevelOffset(sourceLayout, level));
                        var sourceBlocksWide = SharedRepackBlockCopier.BlocksFor(sourceLevelWidth);
                        var sourceBlocksHigh = SharedRepackBlockCopier.BlocksFor(sourceLevelHeight);
                        var ignoredVerificationFailures = 0;
                        SharedRepackBlockCopier.CopyRectangle(
                            source.Dds.AsSpan(sourceOffset, checked((int)sourceLayout.LevelBytes(level))),
                            sourceBlocksWide,
                            sourceBlocksHigh,
                            scratch,
                            blocksWide[level],
                            blocksHigh[level],
                            bytesPerBlock,
                            sourceX,
                            sourceY,
                            destinationX,
                            destinationY,
                            Math.Min(width, Math.Min(sourceLevelWidth - sourceX, levelWidth - destinationX)),
                            Math.Min(height, Math.Min(sourceLevelHeight - sourceY, levelHeight - destinationY)),
                            levelFates,
                            ref ignoredVerificationFailures);
                    }
                }

                fates[level] = levelFates;
                copied[level] = levelFates.Select(fate => fate == SharedRepackBlockFate.Copied).ToArray();
            }

            plan = new SharedRepackBlockCopyPlan
            {
                BytesPerBlock = bytesPerBlock,
                Fates = fates,
                Copied = copied,
            };
            return true;
        }

        // Writes the page's output: chart rectangles decoded into a canvas and encoded with a
        // full chain, then the compressed blocks of every block-aligned level copied over from
        // the sources; the other levels' encoder input is seeded from the sources' own mips (see below).
        private static bool TryComposeSharedRepackOutput(
            SharedRepackPage page,
            SharedRepackSlotOutput output,
            SharedRepackSourceCache sources,
            CancellationToken cancellationToken,
            out string error)
        {
            error = string.Empty;
            cancellationToken.ThrowIfCancellationRequested();
            var canvas = new byte[checked(output.Width * output.Height * 4)];
            var neutral = GetSharedRepackNeutralPixel(output.Slot);
            for (var offset = 0; offset < canvas.Length; offset += 4)
                Buffer.BlockCopy(neutral, 0, canvas, offset, 4);

            var users = page.Tuples.Where(tuple => tuple.Slots.ContainsKey(output.Slot)).ToList();
            foreach (var tuple in users)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var texture = output.SourceOf(tuple);
                var source = sources.Get(texture);
                var top = source?.Level(0);
                if (source == null || top == null)
                {
                    error = $"texture {texture} could not be read or decoded";
                    return false;
                }

                var (bgra, width, height) = top.Value;
                if (width != source.Layout.Width || height != source.Layout.Height)
                {
                    error = $"texture {texture} decodes to {width}x{height}, its header says " +
                            $"{source.Layout.Width}x{source.Layout.Height}";
                    return false;
                }

                var exponent = tuple.SourceExponent[output.Slot];
                var shiftX = output.ExponentX - exponent.X;
                var shiftY = output.ExponentY - exponent.Y;
                foreach (var chart in page.Charts.Where(chart => chart.Tuple == tuple))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destinationX = ScaleByExponent(chart.PackX, output.ExponentX);
                    var destinationY = ScaleByExponent(chart.PackY, output.ExponentY);
                    var destinationWidth = ScaleByExponent(chart.LayoutWidth, output.ExponentX);
                    var destinationHeight = ScaleByExponent(chart.LayoutHeight, output.ExponentY);
                    var sourceX = ScaleByExponent(chart.X0, exponent.X);
                    var sourceY = ScaleByExponent(chart.Y0, exponent.Y);
                    for (var y = 0; y < destinationHeight; y++)
                    {
                        var row = Math.Clamp(sourceY + (y >> shiftY), 0, height - 1);
                        var target = ((destinationY + y) * output.Width + destinationX) * 4;
                        for (var x = 0; x < destinationWidth; x++)
                        {
                            var column = Math.Clamp(sourceX + (x >> shiftX), 0, width - 1);
                            Buffer.BlockCopy(bgra, (row * width + column) * 4, canvas, target + x * 4, 4);
                        }
                    }
                }
            }

            var blockLevels = GetSharedRepackBlockLevels(page, output, sources);
            // Every lower level of the chain is seeded, chart rectangle by chart rectangle (gutter
            // included), with the source's own decoded mip level wherever the rectangle sits on whole
            // texels of that level, instead of the box filter of the canvas. What cannot be seeded keeps
            // the box-filtered texels (non-integer positions, levels the source lacks, exponent
            // mismatches, texels outside every rectangle). Rectangles of a page never overlap, and the
            // order (tuples, then charts) is fixed, so the result is deterministic.
            var seedStats = new SharedRepackMipSeedStats();
            Action<int, byte[], int, int>? overlay = null;
            if (output.MipLevels > 1)
            {
                overlay = (level, pixels, levelWidth, levelHeight) =>
                {
                    foreach (var tuple in users)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var source = sources.Get(output.SourceOf(tuple));
                        var exponent = tuple.SourceExponent[output.Slot];
                        var shiftX = output.ExponentX - exponent.X;
                        var shiftY = output.ExponentY - exponent.Y;
                        (byte[] Bgra, int Width, int Height)? levelData = null;
                        var levelLoaded = false;
                        foreach (var chart in page.Charts.Where(chart => chart.Tuple == tuple))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!levelLoaded)
                            {
                                levelLoaded = true;
                                levelData = source != null && source.Layout.MipCount > level ? source.Level(level) : null;
                            }

                            var seed = SharedRepackMipSeeder.Seed(
                                level, shiftX, shiftY,
                                ScaleByExponent(chart.X0, exponent.X),
                                ScaleByExponent(chart.Y0, exponent.Y),
                                ScaleByExponent(chart.PackX, output.ExponentX),
                                ScaleByExponent(chart.PackY, output.ExponentY),
                                ScaleByExponent(chart.LayoutWidth, output.ExponentX),
                                ScaleByExponent(chart.LayoutHeight, output.ExponentY),
                                levelData, pixels, levelWidth, levelHeight, out var partialEdge,
                                seedStats.Differences[Math.Min(level, seedStats.Differences.Length - 1)]);
                            seedStats.Counts[(int)seed]++;
                            if (partialEdge && seed == SharedRepackMipSeed.SourceMip)
                                seedStats.PartialEdge++;
                        }
                    }
                };
            }

            if (!TryBuildSharedRepackBlockCopyPlan(
                    page, output, sources, cancellationToken, out var blockPlan, out error))
            {
                return false;
            }

            var encoded = output.Format.Equals("BC7", StringComparison.OrdinalIgnoreCase)
                ? TryEncodeSharedRepackChainGathered(
                    canvas, output.Width, output.Height, output.Format, output.Srgb, output.Dx10,
                    1, output.MipLevels - 1, overlay, blockPlan.Fates, cancellationToken,
                    out var dds, out error)
                : TryEncodeSharedRepackChain(
                    canvas, output.Width, output.Height, output.Format, output.Srgb, output.Dx10,
                    1, output.MipLevels - 1, overlay, out dds, out error);
            if (!encoded)
                return false;

            if (!TryReadDdsLayout(dds.AsSpan(0, Math.Min(dds.Length, TextureDownscaleHeaderPeekBytes)).ToArray(), out var layout))
            {
                error = "the encoded texture has an unreadable header";
                return false;
            }

            // Block copy: wherever a chart's crop sits on the 4x4 grid at a level, in the source and on
            // the sheet, the sheet's blocks wholly inside the crop become the source's own blocks. Every
            // other block (gutters, partly covered or misaligned blocks, levels the source lacks, other
            // formats, generated levels) stays as the encoder produced it.
            var stats = new SharedRepackBlockStats(output.MipLevels);
            var captureFates = DdsEncodeCapture.Enabled ? new byte[output.MipLevels][] : null;
            var verifyFailures = 0;
            var bytesPerBlock = layout.BytesPerBlock;
            if (bytesPerBlock != blockPlan.BytesPerBlock)
            {
                error = $"the encoded texture has {bytesPerBlock} bytes per block, planned {blockPlan.BytesPerBlock}";
                return false;
            }

            for (var level = 0; level < output.MipLevels; level++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var levelWidth = Math.Max(1, output.Width >> level);
                var levelHeight = Math.Max(1, output.Height >> level);
                var destinationBlocksWide = SharedRepackBlockCopier.BlocksFor(levelWidth);
                var destinationBlocksHigh = SharedRepackBlockCopier.BlocksFor(levelHeight);
                stats.LevelWidths[level] = destinationBlocksWide;
                stats.LevelHeights[level] = destinationBlocksHigh;
                var destinationOffset = checked((int)GetSharedRepackLevelOffset(layout, level));
                var destinationBytes = destinationBlocksWide * destinationBlocksHigh * bytesPerBlock;
                var fates = new SharedRepackBlockFate[destinationBlocksWide * destinationBlocksHigh];
                var levelHasCharts = false;
                foreach (var tuple in users)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = sources.Get(output.SourceOf(tuple));
                    foreach (var item in EnumerateSharedRepackSlotCharts(page, output, tuple))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var width = item.Width >> level;
                        var height = item.Height >> level;
                        if (width <= 0 || height <= 0)
                            continue;

                        levelHasCharts = true;
                        var destinationX = item.DestinationX >> level;
                        var destinationY = item.DestinationY >> level;
                        var reason = SharedRepackBlockCopier.GetFallbackReason(
                            source != null,
                            source?.Layout.Format ?? string.Empty,
                            source?.Layout.BytesPerBlock ?? 0,
                            output.Format,
                            bytesPerBlock,
                            source?.Layout.MipCount ?? 0,
                            source?.Dds.LongLength ?? 0,
                            source == null ? 0 : source.Layout.HeaderSize + source.Layout.ChainBytes,
                            level,
                            item.SourceX,
                            item.SourceY,
                            item.DestinationX,
                            item.DestinationY);

                        if (reason != null)
                        {
                            SharedRepackBlockCopier.Mark(fates, destinationBlocksWide, destinationBlocksHigh, destinationX, destinationY, width, height, reason.Value);
                            continue;
                        }

                        var sourceLayout = source!.Layout;
                        var sourceLevelWidth = Math.Max(1, sourceLayout.Width >> level);
                        var sourceLevelHeight = Math.Max(1, sourceLayout.Height >> level);
                        var sourceX = item.SourceX >> level;
                        var sourceY = item.SourceY >> level;
                        var sourceOffset = checked((int)GetSharedRepackLevelOffset(sourceLayout, level));
                        var sourceBlocksWide = SharedRepackBlockCopier.BlocksFor(sourceLevelWidth);
                        var sourceBlocksHigh = SharedRepackBlockCopier.BlocksFor(sourceLevelHeight);
                        SharedRepackBlockCopier.CopyRectangle(
                            source.Dds.AsSpan(sourceOffset, checked((int)sourceLayout.LevelBytes(level))),
                            sourceBlocksWide,
                            sourceBlocksHigh,
                            dds.AsSpan(destinationOffset, destinationBytes),
                            destinationBlocksWide,
                            destinationBlocksHigh,
                            bytesPerBlock,
                            sourceX,
                            sourceY,
                            destinationX,
                            destinationY,
                            Math.Min(width, Math.Min(sourceLevelWidth - sourceX, levelWidth - destinationX)),
                            Math.Min(height, Math.Min(sourceLevelHeight - sourceY, levelHeight - destinationY)),
                            fates,
                            ref verifyFailures);
                    }
                }

                var actualCopied = fates.Select(fate => fate == SharedRepackBlockFate.Copied).ToArray();
                var plannedCopied = blockPlan.Copied[level];
                if (actualCopied.Length != plannedCopied.Length ||
                    !actualCopied.AsSpan().SequenceEqual(plannedCopied))
                {
                    var mismatch = actualCopied.Length == plannedCopied.Length
                        ? Enumerable.Range(0, actualCopied.Length)
                            .FirstOrDefault(index => actualCopied[index] != plannedCopied[index], -1)
                        : -1;
                    error = $"copy plan mismatch at mip level {level}" +
                            (mismatch >= 0
                                ? $": block {mismatch} was " +
                                  (actualCopied[mismatch] ? "copied by the pass but not planned" :
                                   "planned for copying but not copied")
                                : $": planned {plannedCopied.Length} blocks, pass produced {actualCopied.Length}");
                    return false;
                }

                SharedRepackBlockCopier.Tally(stats, level, fates, levelHasCharts);
                if (captureFates != null)
                {
                    captureFates[level] = fates
                        .Select(fate => (byte)(fate == SharedRepackBlockFate.Gutter && !levelHasCharts ? SharedRepackBlockFate.Generated : fate))
                        .ToArray();
                }
            }

            if (verifyFailures != 0)
            {
                error = $"{verifyFailures:N0} copied block row(s) differ from their source blocks";
                return false;
            }

            output.BlockStats = stats;
            output.MipSeedStats = seedStats;
            if (captureFates != null)
                DdsEncodeCapture.RecordCopyMap(output.Path, stats.LevelWidths, stats.LevelHeights, captureFates);
            output.Data = dds;
            output.BlockCopiedLevels = blockLevels;
            return true;
        }

        // Canvas -> BC chain. The levels from firstOverlayLevel to lastOverlayLevel of the
        // box-filtered chain are overwritten by the callback (the sources' own decoded mips, where they map)
        // before the chain is compressed.
        private static bool TryEncodeSharedRepackChain(
            byte[] bgra,
            int width,
            int height,
            string format,
            bool srgb,
            bool dx10,
            int firstOverlayLevel,
            int lastOverlayLevel,
            Action<int, byte[], int, int>? overlay,
            out byte[] result,
            out string error)
        {
            result = [];
            error = string.Empty;
            if (overlay == null || firstOverlayLevel <= 0 || lastOverlayLevel < firstOverlayLevel)
                return TryEncodeSharedRepackDds(bgra, width, height, format, srgb, dx10, out result, out error);

            var uncompressed = srgb ? DXGI_FORMAT.B8G8R8A8_UNORM_SRGB : DXGI_FORMAT.B8G8R8A8_UNORM;
            var compressed = format switch
            {
                "BC1" => srgb ? DXGI_FORMAT.BC1_UNORM_SRGB : DXGI_FORMAT.BC1_UNORM,
                "BC3" => srgb ? DXGI_FORMAT.BC3_UNORM_SRGB : DXGI_FORMAT.BC3_UNORM,
                "BC7" => srgb ? DXGI_FORMAT.BC7_UNORM_SRGB : DXGI_FORMAT.BC7_UNORM,
                _ => DXGI_FORMAT.UNKNOWN,
            };
            if (compressed == DXGI_FORMAT.UNKNOWN)
            {
                error = $"{format} cannot be encoded";
                return false;
            }

            try
            {
                using var full = TexHelper.Instance.Initialize2D(uncompressed, width, height, 1, 1, CP_FLAGS.NONE);
                var image = full.GetImage(0, 0, 0);
                var pitch = checked((int)image.RowPitch);
                for (var y = 0; y < height; y++)
                    Marshal.Copy(bgra, y * width * 4, IntPtr.Add(image.Pixels, y * pitch), width * 4);

                using var chain = full.GenerateMipMaps(TEX_FILTER_FLAGS.DEFAULT, 0);
                for (var level = firstOverlayLevel; level <= lastOverlayLevel; level++)
                {
                    var levelWidth = Math.Max(1, width >> level);
                    var levelHeight = Math.Max(1, height >> level);
                    var levelImage = chain.GetImage(level, 0, 0);
                    var levelPitch = checked((int)levelImage.RowPitch);
                    var pixels = new byte[levelWidth * levelHeight * 4];
                    for (var y = 0; y < levelHeight; y++)
                        Marshal.Copy(IntPtr.Add(levelImage.Pixels, y * levelPitch), pixels, y * levelWidth * 4, levelWidth * 4);
                    overlay(level, pixels, levelWidth, levelHeight);
                    for (var y = 0; y < levelHeight; y++)
                        Marshal.Copy(pixels, y * levelWidth * 4, IntPtr.Add(levelImage.Pixels, y * levelPitch), levelWidth * 4);
                }

                if (DdsEncodeCapture.Enabled)
                {
                    DdsEncodeCapture.Record(
                        "TryEncodeSharedRepackChain", full, null, chain, compressed,
                        TEX_COMPRESS_FLAGS.DEFAULT, 0.5f,
                        $"GenerateMipMaps(DEFAULT, levels=0), then levels {firstOverlayLevel}..{lastOverlayLevel} overwritten by the sources' own mip crops");
                }

                using var packed = chain.Compress(compressed, TEX_COMPRESS_FLAGS.DEFAULT, 0.5f);
                using var stream = packed.SaveToDDSMemory(dx10 ? DDS_FLAGS.FORCE_DX10_EXT : DDS_FLAGS.NONE);
                result = new byte[stream.Length];
                stream.ReadExactly(result, 0, result.Length);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = $"encode failed ({ex.GetType().Name}: {ex.Message})";
                return false;
            }
        }

        private static DXGI_FORMAT GetSharedRepackCompressedFormat(string format, bool srgb)
            => format switch
            {
                "BC1" => srgb ? DXGI_FORMAT.BC1_UNORM_SRGB : DXGI_FORMAT.BC1_UNORM,
                "BC3" => srgb ? DXGI_FORMAT.BC3_UNORM_SRGB : DXGI_FORMAT.BC3_UNORM,
                "BC7" => srgb ? DXGI_FORMAT.BC7_UNORM_SRGB : DXGI_FORMAT.BC7_UNORM,
                _ => DXGI_FORMAT.UNKNOWN,
            };

        private static byte[] ReadSharedRepackImage(Image image)
        {
            var rowBytes = checked(image.Width * 4);
            var result = new byte[checked(rowBytes * image.Height)];
            var pitch = checked((int)image.RowPitch);
            for (var y = 0; y < image.Height; y++)
                Marshal.Copy(IntPtr.Add(image.Pixels, y * pitch), result, y * rowBytes, rowBytes);
            return result;
        }

        // Encodes only blocks the copy plan leaves behind. Each strip is independent because the
        // DirectXTex BC encoders operate on one 4x4 block at a time.
        private static bool TryEncodeSharedRepackChainGathered(
            byte[] bgra,
            int width,
            int height,
            string format,
            bool srgb,
            bool dx10,
            int firstOverlayLevel,
            int lastOverlayLevel,
            Action<int, byte[], int, int>? overlay,
            SharedRepackBlockFate[][] blockFates,
            CancellationToken cancellationToken,
            out byte[] result,
            out string error)
        {
            result = [];
            error = string.Empty;
            var uncompressed = srgb ? DXGI_FORMAT.B8G8R8A8_UNORM_SRGB : DXGI_FORMAT.B8G8R8A8_UNORM;
            var compressed = GetSharedRepackCompressedFormat(format, srgb);
            if (compressed == DXGI_FORMAT.UNKNOWN)
            {
                error = $"{format} cannot be encoded";
                return false;
            }

            try
            {
                using var full = TexHelper.Instance.Initialize2D(uncompressed, width, height, 1, 1, CP_FLAGS.NONE);
                var image = full.GetImage(0, 0, 0);
                var pitch = checked((int)image.RowPitch);
                for (var y = 0; y < height; y++)
                    Marshal.Copy(bgra, y * width * 4, IntPtr.Add(image.Pixels, y * pitch), width * 4);

                using var chain = full.GenerateMipMaps(TEX_FILTER_FLAGS.DEFAULT, 0);
                var mipCount = chain.GetMetadata().MipLevels;
                if (blockFates.Length != mipCount)
                {
                    error = $"copy plan has {blockFates.Length} levels, chain has {mipCount}";
                    return false;
                }

                var lastOverlay = Math.Min(lastOverlayLevel, mipCount - 1);
                if (overlay != null && firstOverlayLevel > 0 && lastOverlay >= firstOverlayLevel)
                {
                    for (var level = firstOverlayLevel; level <= lastOverlay; level++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var levelImage = chain.GetImage(level, 0, 0);
                        var levelWidth = levelImage.Width;
                        var levelHeight = levelImage.Height;
                        var pixels = ReadSharedRepackImage(levelImage);
                        overlay(level, pixels, levelWidth, levelHeight);
                        var levelPitch = checked((int)levelImage.RowPitch);
                        for (var y = 0; y < levelHeight; y++)
                            Marshal.Copy(
                                pixels,
                                y * levelWidth * 4,
                                IntPtr.Add(levelImage.Pixels, y * levelPitch),
                                levelWidth * 4);
                    }
                }

                var levelPixels = new byte[mipCount][];
                for (var level = 0; level < mipCount; level++)
                    levelPixels[level] = ReadSharedRepackImage(chain.GetImage(level, 0, 0));

                // Initialize an empty compressed chain only to obtain the exact DDS header and level layout.
                using var empty = TexHelper.Instance.Initialize2D(compressed, width, height, 1, mipCount, CP_FLAGS.NONE);
                using var headerStream = empty.SaveToDDSMemory(dx10 ? DDS_FLAGS.FORCE_DX10_EXT : DDS_FLAGS.NONE);
                result = headerStream.ToArray();
                if (!TryReadDdsLayout(
                        result.AsSpan(0, Math.Min(result.Length, TextureDownscaleHeaderPeekBytes)).ToArray(),
                        out var layout))
                {
                    error = "the gathered encoder produced an unreadable header";
                    return false;
                }
                var encodedBytes = result;

                var gutterFlags = format.Equals("BC7", StringComparison.OrdinalIgnoreCase)
                    ? SharedRepackGutterCompressFlags
                    : TEX_COMPRESS_FLAGS.DEFAULT;
                var visibleFlags = format.Equals("BC7", StringComparison.OrdinalIgnoreCase)
                    ? SharedRepackVisibleCompressFlags
                    : TEX_COMPRESS_FLAGS.DEFAULT;
                const int blocksPerChunk = 256;
                var chunks = new List<(int Level, int[] Blocks, TEX_COMPRESS_FLAGS Flags)>();
                void AddChunks(int level, List<int> blocks, TEX_COMPRESS_FLAGS flags)
                {
                    for (var first = 0; first < blocks.Count; first += blocksPerChunk)
                    {
                        var count = Math.Min(blocksPerChunk, blocks.Count - first);
                        chunks.Add((level, blocks.GetRange(first, count).ToArray(), flags));
                    }
                }

                for (var level = 0; level < mipCount; level++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var levelImage = chain.GetImage(level, 0, 0);
                    var levelWidth = levelImage.Width;
                    var levelHeight = levelImage.Height;
                    var blocksWide = SharedRepackBlockCopier.BlocksFor(levelWidth);
                    var blocksHigh = SharedRepackBlockCopier.BlocksFor(levelHeight);
                    var blockCount = checked(blocksWide * blocksHigh);
                    if (blockFates[level].Length != blockCount)
                    {
                        error = $"copy plan has {blockFates[level].Length} blocks at level {level}, expected {blockCount}";
                        return false;
                    }

                    var pixels = levelPixels[level];
                    if (blocksWide == 1 && blocksHigh == 1 && (levelWidth < 4 || levelHeight < 4))
                    {
                        if (blockFates[level][0] == SharedRepackBlockFate.Copied)
                            continue;

                        var tinyFlags = blockFates[level][0] == SharedRepackBlockFate.Gutter
                            ? gutterFlags
                            : visibleFlags;
                        var levelOffset = checked((int)GetSharedRepackLevelOffset(layout, level));
                        using var tiny = TexHelper.Instance.Initialize2D(
                            uncompressed, levelWidth, levelHeight, 1, 1, CP_FLAGS.NONE);
                        var tinyImage = tiny.GetImage(0, 0, 0);
                        var tinyPitch = checked((int)tinyImage.RowPitch);
                        for (var y = 0; y < levelHeight; y++)
                            Marshal.Copy(
                                pixels,
                                y * levelWidth * 4,
                                IntPtr.Add(tinyImage.Pixels, y * tinyPitch),
                                levelWidth * 4);

                        if (DdsEncodeCapture.Enabled)
                        {
                            DdsEncodeCapture.Record(
                                "TryEncodeSharedRepackChainGathered", tiny, null, tiny, compressed,
                                tinyFlags, 0.5f, $"gathered level {level} (one partial block)");
                        }

                        using var packedTiny = tiny.Compress(compressed, tinyFlags, 0.5f);
                        var packedTinyImage = packedTiny.GetImage(0, 0, 0);
                        var tinyBlock = new byte[layout.BytesPerBlock];
                        Marshal.Copy(packedTinyImage.Pixels, tinyBlock, 0, tinyBlock.Length);
                        tinyBlock.CopyTo(encodedBytes, levelOffset);
                        continue;
                    }

                    var gutterBlocks = new List<int>();
                    var visibleBlocks = new List<int>();
                    for (var block = 0; block < blockCount; block++)
                    {
                        switch (blockFates[level][block])
                        {
                            case SharedRepackBlockFate.Copied:
                                break;
                            case SharedRepackBlockFate.Gutter:
                                gutterBlocks.Add(block);
                                break;
                            default:
                                visibleBlocks.Add(block);
                                break;
                        }
                    }

                    AddChunks(level, gutterBlocks, gutterFlags);
                    AddChunks(level, visibleBlocks, visibleFlags);
                }

                Parallel.For(
                    0,
                    chunks.Count,
                    new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
                    },
                    chunkIndex =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var chunk = chunks[chunkIndex];
                        var level = chunk.Level;
                        var levelImage = chain.GetImage(level, 0, 0);
                        var levelWidth = levelImage.Width;
                        var levelHeight = levelImage.Height;
                        var blocksWide = SharedRepackBlockCopier.BlocksFor(levelWidth);
                        var stripBlocksWide = Math.Min(16, chunk.Blocks.Length);
                        var stripBlocksHigh = (chunk.Blocks.Length + stripBlocksWide - 1) / stripBlocksWide;
                        var stripWidth = stripBlocksWide * 4;
                        var stripHeight = stripBlocksHigh * 4;
                        var pixels = levelPixels[level];
                        var stripPixels = new byte[checked(stripWidth * stripHeight * 4)];
                        for (var index = 0; index < chunk.Blocks.Length; index++)
                        {
                            var destinationBlock = chunk.Blocks[index];
                            var sourceBlockX = destinationBlock % blocksWide;
                            var sourceBlockY = destinationBlock / blocksWide;
                            var stripBlockX = index % stripBlocksWide;
                            var stripBlockY = index / stripBlocksWide;
                            for (var y = 0; y < 4; y++)
                            {
                                var sourceY = Math.Min(levelHeight - 1, sourceBlockY * 4 + y);
                                var targetY = stripBlockY * 4 + y;
                                for (var x = 0; x < 4; x++)
                                {
                                    var sourceX = Math.Min(levelWidth - 1, sourceBlockX * 4 + x);
                                    var sourceOffset = (sourceY * levelWidth + sourceX) * 4;
                                    var targetOffset = (targetY * stripWidth + stripBlockX * 4 + x) * 4;
                                    stripPixels[targetOffset] = pixels[sourceOffset];
                                    stripPixels[targetOffset + 1] = pixels[sourceOffset + 1];
                                    stripPixels[targetOffset + 2] = pixels[sourceOffset + 2];
                                    stripPixels[targetOffset + 3] = pixels[sourceOffset + 3];
                                }
                            }
                        }

                        using var strip = TexHelper.Instance.Initialize2D(
                            uncompressed, stripWidth, stripHeight, 1, 1, CP_FLAGS.NONE);
                        var stripImage = strip.GetImage(0, 0, 0);
                        var stripPitch = checked((int)stripImage.RowPitch);
                        for (var y = 0; y < stripHeight; y++)
                            Marshal.Copy(
                                stripPixels,
                                y * stripWidth * 4,
                                IntPtr.Add(stripImage.Pixels, y * stripPitch),
                                stripWidth * 4);

                        if (DdsEncodeCapture.Enabled)
                        {
                            DdsEncodeCapture.Record(
                                "TryEncodeSharedRepackChainGathered", strip, null, strip, compressed,
                                chunk.Flags, 0.5f, $"gathered level {level}, chunk {chunkIndex}");
                        }

                        using var packed = strip.Compress(compressed, chunk.Flags, 0.5f);
                        var packedImage = packed.GetImage(0, 0, 0);
                        var packedBytes = new byte[checked((int)packedImage.SlicePitch)];
                        Marshal.Copy(packedImage.Pixels, packedBytes, 0, packedBytes.Length);
                        var packedRowPitch = checked((int)packedImage.RowPitch);
                        var levelOffset = checked((int)GetSharedRepackLevelOffset(layout, level));
                        for (var index = 0; index < chunk.Blocks.Length; index++)
                        {
                            var destinationBlock = chunk.Blocks[index];
                            var sourceOffset = (index / stripBlocksWide) * packedRowPitch +
                                               (index % stripBlocksWide) * layout.BytesPerBlock;
                            var destinationOffset = levelOffset + destinationBlock * layout.BytesPerBlock;
                            packedBytes.AsSpan(sourceOffset, layout.BytesPerBlock)
                                .CopyTo(encodedBytes.AsSpan(destinationOffset, layout.BytesPerBlock));
                        }
                    });

                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = $"gathered encode failed ({ex.GetType().Name}: {ex.Message})";
                return false;
            }
        }

        // ---------------------------------------------------------------------------
        // Applying a plan: compose, check, then write.
        // ---------------------------------------------------------------------------

        private sealed class SharedRepackRigidSnapshot
        {
            public List<List<(
                System.Numerics.Vector4[] Positions,
                System.Numerics.Vector2[] Uv,
                System.Numerics.Vector2[] Uv1,
                ushort[] Indices)>> Lods { get; } = [];
            public List<List<(TextureType Type, string Path)[]>> MaterialTextures { get; } = [];
        }

        private static SharedRepackRigidSnapshot SnapshotSharedRepackRigid(RmvFile rigid)
        {
            var snapshot = new SharedRepackRigidSnapshot();
            foreach (var lod in rigid.ModelList)
            {
                var parts = new List<(
                    System.Numerics.Vector4[] Positions,
                    System.Numerics.Vector2[] Uv,
                    System.Numerics.Vector2[] Uv1,
                    ushort[] Indices)>();
                foreach (var model in lod)
                {
                    var vertices = model.Mesh?.VertexList ?? [];
                    parts.Add((
                        vertices.Select(vertex => vertex.Position).ToArray(),
                        vertices.Select(vertex => vertex.Uv).ToArray(),
                        vertices.Select(vertex => vertex.Uv1).ToArray(),
                        (ushort[])(model.Mesh?.IndexList ?? []).Clone()));
                }

                snapshot.Lods.Add(parts);
                snapshot.MaterialTextures.Add(lod.Select(model =>
                    (model.Material?.GetAllTextures() ?? [])
                    .Select(texture => (texture.TexureType, Normalize(texture.Path)))
                    .ToArray()).ToList());
            }

            return snapshot;
        }

        // Positions, indices and the second UV set never change; UV0 of an unmoved part is
        // identical; UV0 of a moved part's referenced vertices is inside [0, 1].
        private static void CompareSharedRepackSnapshot(
            string rigidPath,
            RmvFile rigid,
            SharedRepackRigidSnapshot snapshot,
            HashSet<(int Lod, int Part)> moved,
            HashSet<(int Lod, int Part)> directEmbeddedMaterialParts,
            List<string> errors)
        {
            if (rigid.ModelList.Length != snapshot.Lods.Count)
            {
                errors.Add($"Repacked rigid {rigidPath} changed its LOD count");
                return;
            }

            for (var lod = 0; lod < rigid.ModelList.Length; lod++)
            {
                if (rigid.ModelList[lod].Length != snapshot.Lods[lod].Count)
                {
                    errors.Add($"Repacked rigid {rigidPath} LOD {lod} changed its part count");
                    continue;
                }

                for (var part = 0; part < rigid.ModelList[lod].Length; part++)
                {
                    var model = rigid.ModelList[lod][part];
                    var before = snapshot.Lods[lod][part];
                    var beforeTextures = snapshot.MaterialTextures[lod][part];
                    var afterTextures = (model.Material?.GetAllTextures() ?? [])
                        .Select(texture => (texture.TexureType, Normalize(texture.Path)))
                        .ToArray();
                    var vertices = model.Mesh?.VertexList ?? [];
                    var indices = model.Mesh?.IndexList ?? [];
                    var where = $"{rigidPath} lod {lod} part {part}";
                    if (vertices.Length != before.Positions.Length)
                    {
                        errors.Add($"Repacked {where}: vertex count changed");
                        continue;
                    }

                    if (!indices.AsSpan().SequenceEqual(before.Indices))
                        errors.Add($"Repacked {where}: index list changed");

                    var isMoved = moved.Contains((lod, part));
                    var embeddedPathsMayChange = directEmbeddedMaterialParts.Contains((lod, part));
                    if (afterTextures.Length != beforeTextures.Length ||
                        !afterTextures.Select(item => item.TexureType).SequenceEqual(beforeTextures.Select(item => item.Type)) ||
                        (!embeddedPathsMayChange && !afterTextures.Select(item => item.Item2)
                            .SequenceEqual(beforeTextures.Select(item => item.Path), StringComparer.OrdinalIgnoreCase)))
                    {
                        errors.Add($"Repacked {where}: embedded material texture paths changed unexpectedly");
                    }

                    var referenced = new bool[vertices.Length];
                    foreach (var index in indices)
                    {
                        if (index < referenced.Length)
                            referenced[index] = true;
                    }

                    for (var vertex = 0; vertex < vertices.Length; vertex++)
                    {
                        if (vertices[vertex].Position != before.Positions[vertex])
                        {
                            errors.Add($"Repacked {where}: position of vertex {vertex} changed");
                            break;
                        }
                    }

                    for (var vertex = 0; vertex < vertices.Length; vertex++)
                    {
                        if (vertices[vertex].Uv1 != before.Uv1[vertex])
                        {
                            errors.Add($"Repacked {where}: second UV set of vertex {vertex} changed");
                            break;
                        }
                    }

                    for (var vertex = 0; vertex < vertices.Length; vertex++)
                    {
                        var uv = vertices[vertex].Uv;
                        if (!isMoved)
                        {
                            if (uv != before.Uv[vertex])
                            {
                                errors.Add($"Repacked {where}: UV of an unmoved part changed (vertex {vertex})");
                                break;
                            }
                        }
                        else if (referenced[vertex] &&
                                 (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y) ||
                                  uv.X < -1e-6f || uv.X > 1 + 1e-6f || uv.Y < -1e-6f || uv.Y > 1 + 1e-6f))
                        {
                            errors.Add($"Repacked {where}: UV of vertex {vertex} is outside [0, 1]");
                            break;
                        }
                    }
                }
            }
        }

        // Test hook: runs after the textures are composed and before the pixel check.
        private static Action<object>? SharedRepackBeforeCheckHook;

        // Test hook: no tight pieces are built (the merged-chart layout of an earlier output version).
        private static bool SharedRepackTightPiecesDisabled;
        private static bool SharedRepackReducedPaddingDisabled;
        private static bool SharedRepackDirectRigidsDisabled;
        // Test hook: textures that left a plan are never copied (the output of an earlier version).
        private static bool SharedRepackCopyRemovedDisabled;

        // What a plan saves, exactly: the chain bytes of the textures it replaces against its pages.
        private static long GetSharedRepackPlanSaving(SharedRepackSetPlan plan)
            => plan.Replaced.Values.Sum() - plan.Pages.Sum(page => page.BytesAfter);

        // The chart assignment of a part is state of the part, which two plans of one set share: the
        // plan planned first keeps its own here while the other is planned.
        private sealed record SharedRepackPartCharts(SharedRepackPart Part, int[] Active, int[]? Merged, int[]? Tight);

        private static List<SharedRepackPartCharts> SnapshotSharedRepackPartCharts(SharedRepackSetPlan plan)
            => plan.Tuples.SelectMany(tuple => tuple.Parts).Distinct()
                .Select(part => new SharedRepackPartCharts(part, part.VertexChart, part.MergedVertexChart, part.TightVertexChart))
                .ToList();

        private static void RestoreSharedRepackPartCharts(List<SharedRepackPartCharts> charts)
        {
            foreach (var item in charts)
            {
                item.Part.VertexChart = item.Active;
                item.Part.MergedVertexChart = item.Merged;
                item.Part.TightVertexChart = item.Tight;
            }
        }

        private static (float U, float V) ComputeSharedRepackUv(
            SharedRepackPage page,
            SharedRepackTuple tuple,
            SharedRepackPart part,
            int vertex)
        {
            var chart = tuple.Charts[part.VertexChart[vertex]];
            var u = (chart.PackX + (part.U[vertex] * tuple.ReferenceWidth - chart.X0)) / page.LayoutWidth;
            var v = (chart.PackY + (part.V[vertex] * tuple.ReferenceHeight - chart.Y0)) / page.LayoutHeight;
            return ((float)u, (float)v);
        }

        private static bool IsAwayFromTexelEdge(double texelCoordinate)
        {
            var fraction = texelCoordinate - Math.Floor(texelCoordinate);
            return fraction > 0.02 && fraction < 0.98;
        }

        // Sample weights near the nominal ones whose texel coordinates stay off texel edges
        // (a point exactly on an edge may legitimately land on either neighbouring texel).
        private static IEnumerable<(double A, double B, double C)> GetSharedRepackSampleWeights(
            (double A, double B, double C) nominal)
        {
            yield return nominal;
            for (var attempt = 1; attempt <= 14; attempt++)
            {
                var a = Math.Max(0.02, nominal.A + 0.045 * attempt * Math.Sin(attempt * 2.3));
                var b = Math.Max(0.02, nominal.B + 0.045 * attempt * Math.Sin(attempt * 3.7 + 1));
                var c = Math.Max(0.02, nominal.C + 0.045 * attempt * Math.Sin(attempt * 5.1 + 2));
                var sum = a + b + c;
                yield return (a / sum, b / sum, c / sum);
            }
        }

        private static string FormatSharedRepackRgba(byte[] bgra, int offset)
            => $"{bgra[offset + 2]},{bgra[offset + 1]},{bgra[offset]},{bgra[offset + 3]}";

        // The pixel equivalence check, done on the composed textures before anything is
        // written: sampled triangle points compared between the source texel at the old UV and
        // the new texel at the new UV, per slot, at the top level and at mip levels 2 and 3
        // against the source's own level.
        private static void CheckSharedRepackPage(
            SharedRepackPage page,
            SharedRepackSourceCache sources,
            SharedRepackSourceCache generated,
            SharedRepackPixelStats stats,
            List<string> problems)
        {
            var totalTriangles = page.Tuples.Sum(tuple => tuple.Parts.Where(page.Parts.Contains)
                .Sum(part => part.Model!.Mesh.IndexList.Length / 3));
            var stride = Math.Max(1, totalTriangles / SharedRepackMaximumSampleTriangles + 1);
            (double A, double B, double C)[] nominals =
            [
                (1 / 3.0, 1 / 3.0, 1 / 3.0), (0.8, 0.1, 0.1), (0.1, 0.8, 0.1), (0.1, 0.1, 0.8),
            ];
            double pageTopMax = 0, pageTopSum = 0, pageLevelMax = 0, pageLevelSum = 0;
            var reencodedHistogram = new long[256];
            long reencodedCount = 0;
            long pageTopCount = 0, pageLevelCount = 0;
            var exactViolation = false;
            var levelExactViolation = false;
            var triangleCounter = 0;
            // Tight pieces: every piece of the page must be sampled at least once, not only the ones the
            // triangle stride happens to hit.
            var sampledPieces = new HashSet<SharedRepackChart>();
            stats.Pieces += page.Charts.Count(chart => chart.Piece);
            long variantSamples = 0;
            foreach (var tuple in page.Tuples)
            {
                // A variant page is checked where it differs from the layout page: the tuples cropped from
                // the variant texture (the rest is the layout's crop, checked on the layout page).
                var checkedOutputs = page.Outputs
                    .Where(item => tuple.Slots.ContainsKey(item.Slot) &&
                                   (!item.IsVariant || item.VariantSources!.ContainsKey(tuple.Index)))
                    .ToList();
                foreach (var part in tuple.Parts.Where(page.Parts.Contains))
                {
                    var vertices = part.Model!.Mesh.VertexList;
                    var indices = part.Model.Mesh.IndexList;
                    // Range check of every new UV, sampled or not.
                    for (var vertex = 0; vertex < vertices.Length; vertex++)
                    {
                        if (!part.Used[vertex])
                            continue;

                        var (u, v) = ComputeSharedRepackUv(page, tuple, part, vertex);
                        if (u < -1e-6f || u > 1 + 1e-6f || v < -1e-6f || v > 1 + 1e-6f)
                        {
                            problems.Add($"a new UV ({u:0.####}, {v:0.####}) lies outside [0, 1]");
                            return;
                        }
                    }

                    for (var index = 0; index < indices.Length; index += 3)
                    {
                        int[] corners = [indices[index], indices[index + 1], indices[index + 2]];
                        if (triangleCounter++ % stride != 0 &&
                            !(tuple.Tight && !sampledPieces.Contains(tuple.Charts[part.VertexChart[corners[0]]])))
                        {
                            continue;
                        }

                        var cornerUv = corners.Select(vertex => ComputeSharedRepackUv(page, tuple, part, vertex)).ToArray();
                        for (var nominalIndex = 0; nominalIndex < nominals.Length; nominalIndex++)
                        {
                            var found = false;
                            foreach (var (wa, wb, wc) in GetSharedRepackSampleWeights(nominals[nominalIndex]))
                            {
                                var oldU = part.U[corners[0]] * wa + part.U[corners[1]] * wb + part.U[corners[2]] * wc;
                                var oldV = part.V[corners[0]] * wa + part.V[corners[1]] * wb + part.V[corners[2]] * wc;
                                var newU = cornerUv[0].U * wa + cornerUv[1].U * wb + cornerUv[2].U * wc;
                                var newV = cornerUv[0].V * wa + cornerUv[1].V * wb + cornerUv[2].V * wc;
                                var sourcesOk = true;
                                foreach (var output in checkedOutputs)
                                {
                                    var source = sources.Get(output.SourceOf(tuple));
                                    sourcesOk &= source != null &&
                                                 IsAwayFromTexelEdge(oldU * source.Layout.Width) &&
                                                 IsAwayFromTexelEdge(oldV * source.Layout.Height) &&
                                                 IsAwayFromTexelEdge(newU * output.Width) &&
                                                 IsAwayFromTexelEdge(newV * output.Height);
                                }

                                if (!sourcesOk)
                                    continue;

                                found = true;
                                var chart = tuple.Charts[part.VertexChart[corners[0]]];
                                if (chart.Piece && sampledPieces.Add(chart))
                                    stats.PiecesSampled++;
                                foreach (var output in checkedOutputs)
                                {
                                    var texture = output.SourceOf(tuple);
                                    var source = sources.Get(texture)!;
                                    var target = generated.Get(output.Path)!;
                                    var exact = IsSharedRepackBlockCopiedAtLevel(output, 0);
                                    // Top level, then mip 2 and 3 on the first sample only.
                                    var levels = nominalIndex == 0 ? new[] { 0, 2, 3 } : new[] { 0 };
                                    foreach (var level in levels)
                                    {
                                        if (level > 0 &&
                                            (output.MipLevels <= level || source.Layout.MipCount <= level))
                                        {
                                            continue;
                                        }

                                        var sourceLevel = source.Level(level);
                                        var targetLevel = target.Level(level);
                                        if (sourceLevel == null || targetLevel == null)
                                        {
                                            problems.Add($"{texture} mip {level} could not be decoded");
                                            return;
                                        }

                                        var (sBgra, sWidth, sHeight) = sourceLevel.Value;
                                        var (tBgra, tWidth, tHeight) = targetLevel.Value;
                                        var sx = Math.Clamp((int)Math.Floor(oldU * sWidth), 0, sWidth - 1);
                                        var sy = Math.Clamp((int)Math.Floor(oldV * sHeight), 0, sHeight - 1);
                                        var tx = Math.Clamp((int)Math.Floor(newU * tWidth), 0, tWidth - 1);
                                        var ty = Math.Clamp((int)Math.Floor(newV * tHeight), 0, tHeight - 1);
                                        var so = (sy * sWidth + sx) * 4;
                                        var to = (ty * tWidth + tx) * 4;
                                        var error = 0;
                                        for (var channel = 0; channel < 4; channel++)
                                            error = Math.Max(error, Math.Abs(sBgra[so + channel] - tBgra[to + channel]));

                                        var edgeRectX = ScaleByExponent(chart.PackX, output.ExponentX) >> level;
                                        var edgeRectY = ScaleByExponent(chart.PackY, output.ExponentY) >> level;
                                        var edgeRectW = ScaleByExponent(chart.LayoutWidth, output.ExponentX) >> level;
                                        var edgeRectH = ScaleByExponent(chart.LayoutHeight, output.ExponentY) >> level;
                                        var edgeDistance = Math.Min(
                                            Math.Min(tx - edgeRectX, edgeRectX + edgeRectW - 1 - tx),
                                            Math.Min(ty - edgeRectY, edgeRectY + edgeRectH - 1 - ty));
                                        if (level > 0 && edgeDistance < 2)
                                        {
                                            // A lower-mip texel next to the chart edge legitimately
                                            // mixes in the neighbouring chart or padding.
                                            stats.EdgeSkipped++;
                                            continue;
                                        }

                                        if (level == 0)
                                        {
                                            pageTopMax = Math.Max(pageTopMax, error);
                                            exactViolation |= exact && error != 0;
                                            stats.Samples++;
                                            if (output.IsVariant)
                                                variantSamples++;
                                            pageTopSum += error;
                                            pageTopCount++;
                                        }
                                        else
                                        {
                                            pageLevelMax = Math.Max(pageLevelMax, error);
                                            if (IsSharedRepackBlockCopiedAtLevel(output, level))
                                            {
                                                levelExactViolation |= error != 0;
                                            }
                                            else
                                            {
                                                reencodedHistogram[Math.Min(255, error)]++;
                                                reencodedCount++;
                                            }

                                            stats.LevelSamples++;
                                            pageLevelSum += error;
                                            pageLevelCount++;
                                        }

                                        if (stats.Worst.Count < 10 || error > stats.Worst[^1].Error)
                                        {
                                            stats.Worst.Add(new SharedRepackSample(
                                                output.Slot, Path.GetFileName(texture), level, sx, sy,
                                                FormatSharedRepackRgba(sBgra, so), tx, ty,
                                                FormatSharedRepackRgba(tBgra, to), chart.Label,
                                                edgeDistance,
                                                error));
                                            stats.Worst.Sort((left, right) => right.Error.CompareTo(left.Error));
                                            if (stats.Worst.Count > 10)
                                                stats.Worst.RemoveAt(10);
                                        }
                                    }
                                }

                                break;
                            }

                            if (!found)
                                stats.Skipped++;
                        }
                    }
                }
            }

            stats.VariantSamples += variantSamples;
            stats.TopMax = Math.Max(stats.TopMax, pageTopMax);
            stats.TopSum += pageTopSum;
            stats.TopCount += pageTopCount;
            stats.LevelMax = Math.Max(stats.LevelMax, pageLevelMax);
            stats.LevelSum += pageLevelSum;
            stats.LevelCount += pageLevelCount;
            var topMean = pageTopCount == 0 ? 0 : pageTopSum / pageTopCount;
            var levelMean = pageLevelCount == 0 ? 0 : pageLevelSum / pageLevelCount;
            if (exactViolation)
                problems.Add($"a block-copied top level differs from the source (max {pageTopMax:0}); it must be exact" +
                             (stats.Worst.Count == 0 ? string.Empty : $" (worst: {stats.Worst[0]})"));
            else if (pageTopMax > SharedRepackSampleMaximumError)
                problems.Add($"a sampled top-level texel differs by {pageTopMax:0} levels (limit {SharedRepackSampleMaximumError:0})");
            if (topMean > SharedRepackSampleMeanLimit)
                problems.Add($"mean top-level error {topMean:0.0} exceeds {SharedRepackSampleMeanLimit:0}");
            if (levelExactViolation)
                problems.Add($"a block-copied lower mip differs from the source (max {pageLevelMax:0}); it must be exact");

            // Re-encoded levels: judged by the share of outliers, not by the single worst texel.
            long over = 0;
            for (var value = (int)SharedRepackLevelOutlierError + 1; value < 256; value++)
                over += reencodedHistogram[value];
            var share = reencodedCount == 0 ? 0 : (double)over / reencodedCount;
            long running = 0;
            var p99 = 0;
            for (; p99 < 255 && running < reencodedCount * 0.99; p99++)
                running += reencodedHistogram[p99];
            stats.LevelP99 = Math.Max(stats.LevelP99, p99);
            stats.LevelOutlierShare = Math.Max(stats.LevelOutlierShare, share);
            if (share > SharedRepackLevelOutlierShare)
            {
                problems.Add(
                    $"{share:P2} of the re-encoded mip 2/3 samples differ by more than " +
                    $"{SharedRepackLevelOutlierError:0} levels (limit {SharedRepackLevelOutlierShare:P1}; max {pageLevelMax:0}, 99th percentile {p99})");
            }
            if (levelMean > SharedRepackLevelMeanLimit)
                problems.Add($"mean mip 2/3 error {levelMean:0.0} exceeds {SharedRepackLevelMeanLimit:0}");
        }

        // All or nothing: every page is composed and pixel-checked and every rewrite is
        // prepared in memory first; only then is anything written or changed. Any failure on
        // the way returns null and leaves the output, the rigids and the WSModels untouched.
        private SharedRepackBuiltSet? ApplySharedRepackSetPlan(
            BatchState state,
            SharedTextureRepackResult result,
            SharedRepackMergeContext merge,
            SharedRepackSetPlan plan,
            Dictionary<string, RmvFile> rigids,
            string label,
            Dictionary<string, SharedRepackRigidSnapshot> snapshots,
            Dictionary<string, HashSet<(int Lod, int Part)>> movedParts,
            CancellationToken cancellationToken,
            out string? failure,
            out bool pixelFailure)
        {
            failure = null;
            pixelFailure = false;
            var containers = GetGameplayTraversalContainers(state);
            var sources = new SharedRepackSourceCache(path =>
                GetSharedRepackScaledSource(state, path)?.Dds ??
                ResolveGameplayTraversalContainer(state.Source, containers, path, state.ResolutionProviders)?.FindFile(path)?.DataSource.ReadData());
            var generated = new SharedRepackSourceCache(_ => null);
            var stats = new SharedRepackPixelStats();
            var rewrites = new List<(
                SharedRepackPart Part,
                System.Numerics.Vector2[] Uv,
                Dictionary<TextureType, string> EmbeddedPaths)>();
            var materialFiles = new List<(string Path, byte[] Data)>();
            var materialSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // 1. Compose and check, all in memory; nothing is changed yet.
                foreach (var page in plan.Pages)
                {
                    foreach (var output in page.Outputs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var captureScope = DdsEncodeCapture.Scope("shared-repack", output.Path);
                        if (!TryComposeSharedRepackOutput(page, output, sources, cancellationToken, out var error))
                        {
                            failure = $"{output.Slot}: {error}";
                            return null;
                        }

                        var problem = CheckSharedRepackDds(output.Data, output);
                        if (problem != null)
                        {
                            failure = $"generated {output.Slot} texture {problem}";
                            return null;
                        }

                        if (output.DuplicateOf == null)
                            generated.FromBytes(output.Path, output.Data);
                    }
                }

                SharedRepackBeforeCheckHook?.Invoke(plan);

                // A duplicate must have encoded to exactly the canonical bytes (hash both); otherwise
                // the signature missed an input. It then shares the canonical data and is not written.
                foreach (var output in plan.Pages.SelectMany(page => page.Outputs).Where(item => item.DuplicateOf != null))
                {
                    var first = output.DuplicateOf!;
                    if (!output.Data.AsSpan().SequenceEqual(first.Data))
                    {
                        failure = $"internal error: duplicate {output.Slot} page {output.PlannedPath} encoded differently " +
                                  $"from {first.Path} (sha256 {Convert.ToHexString(SHA256.HashData(output.Data))[..16]} vs " +
                                  $"{Convert.ToHexString(SHA256.HashData(first.Data))[..16]}); the duplicate signature is wrong";
                        return null;
                    }

                    output.Data = first.Data;
                }

                var problems = new List<string>();
                foreach (var page in plan.Pages)
                {
                    var pageProblems = new List<string>();
                    CheckSharedRepackPage(page, sources, generated, stats, pageProblems);
                    if (pageProblems.Count == 0)
                        continue;

                    plan.FailedPaddingGroupKeys.Add(page.GroupKey);
                    problems.AddRange(pageProblems);
                }

                if (problems.Count != 0)
                {
                    pixelFailure = true;
                    failure = $"failed the pixel check: {problems[0]} (top-level max {stats.TopMax:0}, mean " +
                              $"{stats.TopMean:0.00}; mip 2/3 max {stats.LevelMax:0}, mean {stats.LevelMean:0.00})";
                    result.PixelFailures.Add((label, string.Join("; ", problems) + Environment.NewLine +
                        string.Join(Environment.NewLine, stats.Worst.Take(5).Select(sample => "      worst: " + sample))));
                    return null;
                }

                // 2. Prepare: the new UVs, the material files, and the claims on names and parts.
                var claimedParts = new HashSet<MeshKey>();
                var claimedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var page in plan.Pages)
                {
                    foreach (var output in page.Outputs.Where(item => item.DuplicateOf == null))
                    {
                        if (state.SharedRepackTexturePaths.Contains(output.Path) || !claimedPaths.Add(output.Path))
                        {
                            failure = $"the generated texture name {output.Path} is already taken";
                            return null;
                        }
                    }

                    foreach (var (material, document) in page.MaterialDocuments.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    {
                        var newPath = page.MaterialMap[material];
                        if (state.SharedRepackMaterialPaths.Contains(newPath) || !claimedPaths.Add(newPath))
                        {
                            failure = $"the generated material name {newPath} is already taken";
                            return null;
                        }

                        materialFiles.Add((newPath, Encoding.UTF8.GetBytes(document.OuterXml)));
                        materialSources[newPath] = material;
                    }

                    foreach (var tuple in page.Tuples)
                    {
                        foreach (var part in page.Parts.Where(part => tuple.Parts.Contains(part)))
                        {
                            // Signatures partition the textures, so the parts of two sets are
                            // disjoint; a part moved twice would be sampled with the wrong UVs.
                            if (!claimedParts.Add(part.Key) ||
                                movedParts.GetValueOrDefault(part.Key.GeometryPath)?.Contains(
                                    (part.Key.LodIndex, part.Key.PartIndex)) == true)
                            {
                                var message = $"Shared repack would move {DescribeSharedRepackPart(part)} twice";
                                result.ValidationErrors.Add(message);
                                failure = message;
                                return null;
                            }

                            if (!rigids.ContainsKey(part.Key.GeometryPath))
                            {
                                failure = $"{DescribeSharedRepackPart(part)} belongs to a rigid that is not rewritable";
                                return null;
                            }

                            var vertices = part.Model!.Mesh.VertexList;
                            var uv = new System.Numerics.Vector2[vertices.Length];
                            for (var vertex = 0; vertex < vertices.Length; vertex++)
                            {
                                if (!part.Used[vertex])
                                {
                                    uv[vertex] = vertices[vertex].Uv;
                                    continue;
                                }

                                var (u, v) = ComputeSharedRepackUv(page, tuple, part, vertex);
                                uv[vertex] = new System.Numerics.Vector2(u, v);
                            }

                            var embeddedPaths = new Dictionary<TextureType, string>();
                            if (part.DirectEmbeddedMaterial)
                            {
                                foreach (var (type, sourcePath) in part.EmbeddedTextures)
                                {
                                    if (TryGetSharedRepackEmbeddedSlot(type, out var slot) &&
                                        part.Slots.TryGetValue(slot, out var partTexture) &&
                                        partTexture.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
                                    {
                                        // A uniform texture kept as authored has no page; the generated material keeps it too.
                                        if (tuple.ConstantSlots.TryGetValue(slot, out var constantTexture) &&
                                            constantTexture.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
                                        {
                                            continue;
                                        }

                                        // A copied texture maps to its copy output, any other to the page that replaces it.
                                        var copiedSlot = tuple.CopySlots.Contains(slot);
                                        var output = page.Outputs.FirstOrDefault(candidate =>
                                            !candidate.IsVariant &&
                                            candidate.IsCopy == copiedSlot &&
                                            candidate.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase) &&
                                            candidate.Sources.Any(source => Normalize(source)
                                                .Equals(Normalize(sourcePath), StringComparison.OrdinalIgnoreCase)));
                                        if (output == null)
                                        {
                                            failure = $"no generated {slot} page containing {sourcePath} was planned for embedded material {DescribeSharedRepackPart(part)}";
                                            return null;
                                        }

                                        embeddedPaths[type] = output.Path;
                                    }
                                }

                                // Legacy entries follow the page their texture moved to, which for a copied
                                // texture is its copy (the original stays, but the new UVs need the cropped sheet).
                                var legacyTargets = page.Outputs
                                    .Where(output => !output.IsVariant && !IsSharedRepackNoiseSlot(output.Slot))
                                    .SelectMany(output => output.Sources.Select(source => (Source: Normalize(source), output.Path)))
                                    .GroupBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
                                    .ToDictionary(group => group.Key, group => group.First().Path, StringComparer.OrdinalIgnoreCase);
                                foreach (var (type, sourcePath) in part.EmbeddedTextures)
                                {
                                    if ((type is TextureType.Diffuse or TextureType.Specular or TextureType.Gloss) &&
                                        legacyTargets.TryGetValue(Normalize(sourcePath), out var replacement))
                                    {
                                        embeddedPaths[type] = replacement;
                                    }
                                }

                                if (embeddedPaths.Keys.Any(type => part.Model!.Material?.GetTexture(type).HasValue != true))
                                {
                                    failure = $"embedded material texture disappeared before rewrite for {DescribeSharedRepackPart(part)}";
                                    return null;
                                }
                            }

                            rewrites.Add((part, uv, embeddedPaths));
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                pixelFailure = false;
                failure = $"internal error while composing or checking ({ex.GetType().Name}: {ex.Message})";
                return null;
            }

            // 3. Apply: write files, rewrite UVs, repoint WSModels.
            foreach (var path in rewrites.Select(item => item.Part.Key.GeometryPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!snapshots.ContainsKey(path))
                    snapshots[path] = SnapshotSharedRepackRigid(rigids[path]);
                if (!movedParts.ContainsKey(path))
                    movedParts[path] = [];
            }

            foreach (var page in plan.Pages)
            {
                foreach (var output in page.Outputs.Where(item => item.DuplicateOf == null))
                {
                    WriteFile(state.Output, output.Path, output.Data);
                    state.SharedRepackTexturePaths.Add(output.Path);
                    result.GeneratedTextures.Add(output.Path);
                }

                foreach (var texture in page.Tuples.SelectMany(tuple => tuple.AllMemberTextures).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var scaled = GetSharedRepackScaledSource(state, texture);
                    if (scaled == null)
                        continue;

                    result.ScaledSourceTextures[Normalize(texture)] = scaled.Scale;
                    state.DownscaleEarlyUsedByRepack.Add(Normalize(texture));
                }

                foreach (var (node, material) in page.WsRepoints)
                    node.InnerText = page.MaterialMap[material];
                foreach (var ws in page.WsModels)
                    state.ModifiedWsModels.Add(ws);
            }

            foreach (var (path, data) in materialFiles)
            {
                WriteFile(state.Output, path, data);
                state.SharedRepackMaterialPaths.Add(path);
                state.SharedRepackMaterialSources[path] = materialSources[path];
                result.GeneratedMaterials.Add(path);
            }

            foreach (var (part, uv, _) in rewrites)
            {
                var vertices = part.Model!.Mesh.VertexList;
                for (var vertex = 0; vertex < vertices.Length; vertex++)
                {
                    if (part.Used[vertex])
                        vertices[vertex].Uv = uv[vertex];
                }

                movedParts[part.Key.GeometryPath].Add((part.Key.LodIndex, part.Key.PartIndex));
                state.SharedRepackParts.Add(part.Key);
            }

            var rewritten = rewrites
                .GroupBy(item => item.Part.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => (Rigid: group.Key, Parts: group.Count()))
                .ToList();
            foreach (var (path, _) in rewritten)
            {
                state.RigidModels[path] = rigids[path];
                state.ModifiedRigids.Add(path);
                result.RepackedRigids.Add(path);
            }

            foreach (var (part, _, embeddedPaths) in rewrites.Where(item => item.EmbeddedPaths.Count != 0))
            {
                foreach (var (type, path) in embeddedPaths)
                {
                    part.Model!.Material!.SetTexture(type, path);
                    result.EmbeddedRewrittenPaths.Add((
                        part.Key.GeometryPath,
                        part.Key.LodIndex,
                        part.Key.PartIndex,
                        type));
                }
                result.EmbeddedRewrittenRigids.Add(part.Key.GeometryPath);
                result.EmbeddedRewrittenPartTypes[(part.Key.GeometryPath, part.Key.LodIndex, part.Key.PartIndex)] =
                    part.EmbeddedMaterialType ?? ModelMaterialEnum.Unkown;
            }

            // What was written is what was planned.
            foreach (var output in plan.Pages.SelectMany(page => page.Outputs))
            {
                var written = state.Output.FindFile(output.Path)?.DataSource.ReadData();
                if (written == null || !written.AsSpan().SequenceEqual(output.Data))
                    result.ValidationErrors.Add($"Written repack texture differs from the checked one: {output.Path}");
            }

            var decision = merge.Decisions[merge.Seed];
            var built = new SharedRepackBuiltSet
            {
                Rigid = rewritten[0].Rigid,
                Label = label,
                Units = decision.SignatureSize,
                HasCharacter = decision.HasCharacter,
                EstimatedSaving = merge.Component.Sum(item => item.Saving),
                After = plan.Pages.Sum(page => page.BytesAfter),
                DeduplicatedOutputs = plan.DeduplicatedOutputs,
                DeduplicatedBytes = plan.DeduplicatedBytes,
                Tuples = plan.Pages.SelectMany(page => page.Tuples).Distinct().Count(),
                Charts = plan.Pages.Sum(page => page.Charts.Count),
                Parts = plan.Pages.Sum(page => page.Parts.Count),
                Pages = plan.Pages.Count,
                LayoutWidth = plan.Pages[0].LayoutWidth,
                LayoutHeight = plan.Pages[0].LayoutHeight,
                Samples = stats.Samples,
                MaxError = stats.TopMax,
                MeanError = stats.TopMean,
                LevelMaxError = stats.LevelMax,
                LevelMeanError = stats.LevelMean,
                LevelP99Error = stats.LevelP99,
                LevelOutlierShare = stats.LevelOutlierShare,
                LevelSamples = stats.LevelSamples,
                SkippedSamples = stats.Skipped,
                EdgeSkippedSamples = stats.EdgeSkipped,
                Pieces = stats.Pieces,
                PiecesSampled = stats.PiecesSampled,
            };
            built.Rigids.AddRange(rewritten);
            built.EmbeddedRigids.AddRange(rewrites
                .Where(item => item.Part.DirectEmbeddedMaterial && item.EmbeddedPaths.Count != 0)
                .GroupBy(item => item.Part.Key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                .Select(group => (
                    Rigid: group.Key,
                    Parts: group.Count(),
                    Consumers: group.SelectMany(item => item.Part.DirectConsumers)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    UnusedWsModels: group.SelectMany(item => item.Part.UnusedWsModels)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToArray())));
            built.MergedSets.AddRange(merge.Component.Skip(1).Select(item =>
                (DescribeSharedRepackSet(item), item.Set.Items.Count, item.SignatureSize)));
            foreach (var (path, bytes) in plan.Replaced.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                built.Replaced.Add((path, plan.ReplacedOwned[path], bytes));
                built.Before += bytes;
            }

            built.Groups.AddRange(plan.Groups);
            built.Splits.AddRange(plan.Splits.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value));
            built.Outputs.AddRange(plan.Pages.SelectMany(page => page.Outputs).Where(output => output.DuplicateOf == null));
            var flowMapAtlasSources = built.Outputs
                .Where(output => !output.IsCopy && output.Slot.Equals(SharedRepackFlowMapSlot, StringComparison.OrdinalIgnoreCase))
                .SelectMany(output => output.Sources)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var part in plan.Pages.SelectMany(page => page.Parts).Distinct())
            {
                if (part.MaterialSlots.Values.Any(map => map.TryGetValue(SharedRepackFlowMapSlot, out var texture) &&
                                                          flowMapAtlasSources.Contains(Normalize(texture))))
                    built.FlowMapAtlasUsers.Add((Normalize(part.Key.GeometryPath), part.Key.LodIndex, part.Key.PartIndex));
            }
            built.EmbeddedTexturePaths.UnionWith(rewrites.SelectMany(item => item.EmbeddedPaths.Values).Select(Normalize));
            built.Materials.AddRange(plan.Pages.SelectMany(page => page.MaterialMap.Values).OrderBy(path => path, StringComparer.Ordinal));
            built.BloodScales.AddRange(plan.Pages.SelectMany(page => page.BloodScales));
            built.DissolveScales.AddRange(plan.Pages.SelectMany(page => page.DissolveScales));
            built.FlowScales.AddRange(plan.Pages.SelectMany(page => page.FlowScales));
            built.FlowCompensationParts = plan.Pages.SelectMany(page => page.Parts).Distinct()
                .Count(part => part.FlowCompensationMaterials.Count != 0);
            built.EmissiveScales.AddRange(plan.Pages.SelectMany(page => page.EmissiveScales));
            built.EmissiveParts = plan.Pages.SelectMany(page => page.Parts)
                .Count(part => part.CopySlots.Contains(SharedRepackEmissiveDistortionSlot));
            built.DissolveParts = plan.Pages.SelectMany(page => page.Parts)
                .Count(part => part.CopySlots.Contains(SharedRepackDissolveMapSlot));
            foreach (var (unreadMaterial, unreadSlot) in plan.Pages.SelectMany(page => page.Parts)
                         .SelectMany(part => part.UnreadSlots).Distinct())
            {
                if (!built.UnreadSlotMaterials.TryGetValue(unreadSlot, out var inSet))
                {
                    inSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    built.UnreadSlotMaterials[unreadSlot] = inSet;
                }

                inSet.Add(unreadMaterial);
                if (!result.UnreadSlotMaterials.TryGetValue(unreadSlot, out var inRun))
                {
                    inRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    result.UnreadSlotMaterials[unreadSlot] = inRun;
                }

                inRun.Add(unreadMaterial);
            }

            bool IsEmissiveOutput(SharedRepackSlotOutput output)
                => output.IsCopy && output.Slot.Equals(SharedRepackEmissiveDistortionSlot, StringComparison.OrdinalIgnoreCase);
            bool IsDissolveOutput(SharedRepackSlotOutput output)
                => output.IsCopy && output.Slot.Equals(SharedRepackDissolveMapSlot, StringComparison.OrdinalIgnoreCase);
            bool IsFlowMapOutput(SharedRepackSlotOutput output)
                => output.IsCopy && output.Slot.Equals(SharedRepackFlowMapSlot, StringComparison.OrdinalIgnoreCase);
            bool IsFlowGuidOutput(SharedRepackSlotOutput output)
                => output.IsCopy && output.Slot.Equals(SharedRepackFlowGuidSlot, StringComparison.OrdinalIgnoreCase);
            built.EmissiveCopies = built.Outputs.Count(IsEmissiveOutput);
            built.EmissiveCopyBytes = built.Outputs.Where(IsEmissiveOutput).Sum(output => output.Data.LongLength);
            built.DissolveCopies = built.Outputs.Count(IsDissolveOutput);
            built.DissolveCopyBytes = built.Outputs.Where(IsDissolveOutput).Sum(output => output.Data.LongLength);
            built.FlowMapAtlases = built.Outputs.Count(output => !output.IsCopy &&
                output.Slot.Equals(SharedRepackFlowMapSlot, StringComparison.OrdinalIgnoreCase));
            built.FlowMapAtlasBytes = built.Outputs.Where(output => !output.IsCopy &&
                output.Slot.Equals(SharedRepackFlowMapSlot, StringComparison.OrdinalIgnoreCase)).Sum(output => output.Data.LongLength);
            built.FlowMapCopies = built.Outputs.Count(IsFlowMapOutput);
            built.FlowMapCopyBytes = built.Outputs.Where(IsFlowMapOutput).Sum(output => output.Data.LongLength);
            built.FlowGuidCopies = built.Outputs.Count(IsFlowGuidOutput);
            built.FlowGuidCopyBytes = built.Outputs.Where(IsFlowGuidOutput).Sum(output => output.Data.LongLength);
            foreach (var output in built.Outputs.Where(output => output.IsCopy && !IsEmissiveOutput(output) &&
                         !IsDissolveOutput(output) && !IsFlowMapOutput(output) && !IsFlowGuidOutput(output)))
            {
                foreach (var texture in output.Sources)
                {
                    built.RemovedCopies.Add((
                        texture,
                        output.Slot,
                        merge.CopiedTextures.GetValueOrDefault(texture, "left the plan"),
                        output.Data.LongLength));
                }
            }

            built.VariantOutputs = built.Outputs.Count(output => output.IsVariant);
            built.VariantBytes = built.Outputs.Where(output => output.IsVariant).Sum(output => output.Data.LongLength);
            built.VariantParts = plan.Pages.SelectMany(page => page.Parts).Distinct()
                .Count(part => part.VariantSlots.Count != 0);
            built.VariantSamples = stats.VariantSamples;
            result.VariantOutputs += built.VariantOutputs;
            result.VariantBytes += built.VariantBytes;
            result.VariantParts += built.VariantParts;
            result.VariantSamples += built.VariantSamples;
            built.CopyWon = merge.CopyRemoved && built.RemovedCopies.Count != 0;
            built.NormalSaving = merge.NormalSaving;
            built.CopyGain = built.CopyWon ? built.Before - built.After - (merge.NormalSaving ?? 0) : 0;
            result.DeduplicatedOutputs += built.DeduplicatedOutputs;
            result.DeduplicatedBytes += built.DeduplicatedBytes;
            result.EmissiveParts += built.EmissiveParts;
            result.EmissiveCopies += built.EmissiveCopies;
            result.EmissiveCopyBytes += built.EmissiveCopyBytes;
            result.EmissiveMaterialsRescaled += built.EmissiveScales.Count;
            result.DissolveParts += built.DissolveParts;
            result.DissolveCopies += built.DissolveCopies;
            result.DissolveCopyBytes += built.DissolveCopyBytes;
            result.DissolveMaterialsRescaled += built.DissolveScales.Count;
            result.FlowCompensationParts += built.FlowCompensationParts;
            result.FlowMapAtlases += built.FlowMapAtlases;
            result.FlowMapAtlasBytes += built.FlowMapAtlasBytes;
            result.FlowMapCopies += built.FlowMapCopies;
            result.FlowMapCopyBytes += built.FlowMapCopyBytes;
            result.FlowGuidCopies += built.FlowGuidCopies;
            result.FlowGuidCopyBytes += built.FlowGuidCopyBytes;
            result.FlowMaterialsRescaled += built.FlowScales.Count;
            result.FlowAspectGroups += plan.FlowAspectGroups;
            result.FlowAspectTuples += plan.FlowAspectTuples;
            result.FlowAspectExtraBytes += plan.FlowAspectExtraBytes;
            result.EmissiveAspectGroups += plan.AspectGroups;
            result.EmissiveAspectTuples += plan.AspectTuples;
            result.EmissiveAspectExtraBytes += plan.AspectExtraBytes;
            built.Worst.AddRange(stats.Worst);
            result.Samples += stats.Samples;
            result.MaxError = Math.Max(result.MaxError, stats.TopMax);
            result.ErrorSum += stats.TopSum;
            result.ErrorCount += stats.TopCount;
            return built;
        }

        // ---------------------------------------------------------------------------
        // After pruning, validation, report.
        // ---------------------------------------------------------------------------

        // Which replaced owned textures survived the existing unused-asset pruning.
        private static void FinalizeSharedTextureRepack(BatchState state)
        {
            var result = state.SharedTextureRepack;
            if (result == null || result.Finalized)
                return;

            result.Finalized = true;
            foreach (var path in result.BuiltSets
                         .SelectMany(set => set.Replaced.Where(item => item.Owned).Select(item => item.Path))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                result.OwnedReplaced++;
                if (state.Output.ContainsFile(path))
                    result.OwnedStillPresent.Add(path);
                else
                    result.OwnedPruned.Add(path);
            }
        }

        private void ValidateSharedTextureRepack(
            BatchState state,
            List<string> errors,
            CancellationToken cancellationToken)
        {
            var result = state.SharedTextureRepack;
            if (result == null)
                return;

            var before = errors.Count;
            errors.AddRange(result.ValidationErrors);
            var compactedRepackTextures = (state.TextureCompaction?.Compacted ?? [])
                .Where(entry => entry.Generated)
                .Select(entry => Normalize(entry.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var signatureSetOf = new Dictionary<string, UnitRepackSignatureSet>(StringComparer.OrdinalIgnoreCase);
            foreach (var signatureSet in state.UnitTextureRepack?.SetsC ?? [])
            {
                foreach (var item in signatureSet.Items)
                    signatureSetOf[Normalize(item.Entry.Path)] = signatureSet;
            }

            foreach (var set in result.BuiltSets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var output in set.Outputs)
                {
                    // No unit may load more than before: a generated texture replaces only
                    // source textures that exactly the same units load. A copy of the emissive
                    // noise replaces nothing (its source stays loaded by every other user).
                    var owners = output.Sources
                        .Select(source => signatureSetOf.GetValueOrDefault(source))
                        .Distinct()
                        .ToList();
                    var isFlowMapAtlas = !output.IsCopy &&
                                         output.Slot.Equals(SharedRepackFlowMapSlot, StringComparison.OrdinalIgnoreCase);
                    if (isFlowMapAtlas)
                    {
                        var flowMapHasUnknownProfile = owners.Contains(null)
                            ? output.Sources.Count != 1 || owners.Count != 1
                            : owners.Count != 1;
                        if (flowMapHasUnknownProfile)
                        {
                            errors.Add($"Generated flow-map atlas {output.Path} combines sources without one verified signature-set profile");
                        }
                        if (state.VanillaTextureCoverage is not { } coverage)
                        {
                            errors.Add($"Generated flow-map atlas {output.Path} cannot be checked because texture coverage is unavailable");
                        }
                        else
                        {
                            foreach (var source in output.Sources)
                            {
                                if (!coverage.Entries.TryGetValue(source, out var entry))
                                {
                                    errors.Add($"Generated flow-map atlas {output.Path} has an unindexed source {source}");
                                    continue;
                                }

                                if (entry.UnassessableUses != 0 || entry.NonMaterialReference != null)
                                    errors.Add($"Generated flow-map atlas {output.Path} has an unassessable use of {source}");
                                foreach (var use in entry.ModParts.Concat(entry.VanillaParts).Distinct())
                                {
                                    if (state.AtlasChangedParts.Contains((Normalize(use.Rigid), use.Lod, use.Part)))
                                        continue;
                                    if (!set.FlowMapAtlasUsers.Any(user =>
                                            user.Lod == use.Lod && user.Part == use.Part &&
                                            user.Rigid.Equals(Normalize(use.Rigid), StringComparison.OrdinalIgnoreCase)))
                                    {
                                        errors.Add($"Generated flow-map atlas {output.Path} leaves user {use.Rigid} lod {use.Lod} part {use.Part} on the original source {source}");
                                    }
                                }
                            }
                        }
                    }
                    else if (!output.IsCopy && (owners.Count != 1 || owners[0] == null))
                    {
                        errors.Add(
                            $"Generated repack texture {output.Path} replaces source textures of " +
                            $"{(owners.Contains(null) ? "an unknown signature set and " : string.Empty)}" +
                            $"{owners.Count(owner => owner != null):N0} signature set(s), not exactly one");
                    }

                    // A copy is cropped from a texture that stays: it must still resolve.
                    if (output.IsCopy)
                    {
                        // An owned source must still be in the output; any other resolves as the repack read it.
                        foreach (var source in output.Sources.Where(source =>
                                     state.Output.FindFile(source) == null &&
                                     (state.Source.FindFile(source) != null ||
                                      ResolveGameplayTraversalContainer(
                                          state.Source, GetGameplayTraversalContainers(state), source, state.ResolutionProviders)?
                                          .FindFile(source) == null)))
                        {
                            errors.Add(
                                $"Copy repack texture {output.Path} is cropped from {source}, which is missing " +
                                "from the output and from vanilla");
                        }
                    }

                    var file = state.Output.FindFile(output.Path);
                    if (file == null)
                    {
                        errors.Add($"Generated repack texture is missing from output: {output.Path}");
                        continue;
                    }

                    // A texture the compaction stage shrank or transcoded (exactly, verified by
                    // ValidateTextureCompaction) no longer has the planned format or size.
                    if (compactedRepackTextures.Contains(Normalize(output.Path)))
                        continue;

                    var problem = CheckSharedRepackDds(file.DataSource.ReadData(), output);
                    if (problem != null)
                        errors.Add($"Generated repack texture {output.Path} {problem}");
                }

                var referencedTextures = new HashSet<string>(set.EmbeddedTexturePaths, StringComparer.OrdinalIgnoreCase);
                foreach (var materialPath in set.Materials)
                {
                    var file = state.Output.FindFile(materialPath);
                    if (file == null)
                    {
                        errors.Add($"Generated repack material is missing from output: {materialPath}");
                        continue;
                    }

                    try
                    {
                        var document = LoadXml(file);
                        foreach (XmlNode node in document.SelectNodes("/material/textures/texture") ?? throw new InvalidDataException("no textures"))
                        {
                            var texture = Normalize(node.SelectSingleNode("source")?.InnerText ?? node.InnerText);
                            // Every entry of every slot (a slot may be listed twice) must be redirected:
                            // an entry still naming a replaced texture samples it with repacked UVs.
                            if (set.Replaced.Any(item => Normalize(item.Path).Equals(texture, StringComparison.OrdinalIgnoreCase)))
                            {
                                errors.Add(
                                    $"Generated repack material {materialPath} still references the replaced texture " +
                                    $"{texture} in slot {GetTextureSlot(node)}");
                            }

                            referencedTextures.Add(texture);
                            // Only the textures the repack generated; pass-through slots are unchanged.
                            if (!set.Outputs.Any(output => output.Path.Equals(texture, StringComparison.OrdinalIgnoreCase)))
                            {
                                // A generated page name must be a written page of this set (a page of another
                                // layout group the set deduplicated against is one; a dropped duplicate's is not).
                                if (texture.Contains("_repack_", StringComparison.Ordinal) &&
                                    texture.StartsWith(AtlasDirectory, StringComparison.OrdinalIgnoreCase))
                                {
                                    errors.Add(
                                        $"Generated repack material {materialPath} names the repack texture {texture}, " +
                                        "which is not a written page of its set");
                                }

                                continue;
                            }

                            if (!CanResolveAfterRewrite(state, texture))
                                errors.Add($"Generated repack material texture does not resolve: {materialPath} -> {texture}");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Generated repack material is invalid: {materialPath} ({ex.Message})");
                    }
                }

                // No written page may be left without a user (a page whose users all point elsewhere is waste).
                foreach (var output in set.Outputs.Where(output => !referencedTextures.Contains(Normalize(output.Path))))
                {
                    errors.Add(
                        $"Generated repack texture {output.Path} is not referenced by any generated material or " +
                        "rewritten embedded material of its set");
                }
            }

            // A generated material may only be referenced by WSModels of rewritten rigids: any
            // other user would sample the original UVs with the new textures.
            var generatedMaterials = new HashSet<string>(result.GeneratedMaterials, StringComparer.OrdinalIgnoreCase);
            if (generatedMaterials.Count != 0)
            {
                var geometryOfWs = (state.VanillaTextureCoverage?.Bindings ?? [])
                    .Where(binding => binding.Geometry.Length != 0)
                    .GroupBy(binding => binding.WsModelPath, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Select(binding => binding.Geometry).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                        StringComparer.OrdinalIgnoreCase);
                // Generated cross-rigid WSModels are not in the coverage bindings: their geometry is
                // the generated rigid named in the document.
                foreach (var ws in state.CrossRigidGeneratedWsModelPaths)
                {
                    var geometry = Normalize(state.WsDocuments.GetValueOrDefault(ws)?.SelectSingleNode("/model/geometry")?.InnerText);
                    if (geometry.Length != 0)
                        geometryOfWs[ws] = [geometry];
                }
                foreach (var (ws, document) in state.WsDocuments.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    var users = document.SelectNodes("/model/materials/material")?.Cast<XmlNode>()
                        .Select(node => Normalize(node.InnerText))
                        .Where(generatedMaterials.Contains)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(path => path, StringComparer.Ordinal)
                        .ToList() ?? [];
                    if (users.Count == 0)
                        continue;

                    var geometries = geometryOfWs.GetValueOrDefault(ws) ?? [];
                    var other = geometries.FirstOrDefault(geometry => !result.RepackedRigids.Contains(geometry));
                    if (geometries.Count == 0 || other != null)
                    {
                        errors.Add(
                            $"Generated repack material {users[0]} is referenced by WSModel {ws} of rigid " +
                            $"{other ?? "(unknown)"}, which was not rewritten");
                    }
                }
            }

            // Every WSModel of the output, source or generated, whose material still names a replaced
            // texture keeps the original loaded: the saving would be fake. Source WSModels the repack
            // never touched are read as authored (the coverage bindings), the others as they are now.
            if (result.BuiltSets.Count != 0)
            {
                var replacedTextures = result.BuiltSets
                    .SelectMany(set => set.Replaced.Select(item => Normalize(item.Path)))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                // (WSModel, rigid, lod, part, material) as it stands. A part atlasing replaced uses the
                // atlas, not the texture, so it is not a user (the closure ignores it too).
                var nodes = new List<(string Ws, string Geometry, int Lod, int Part, string Material)>();
                foreach (var binding in state.VanillaTextureCoverage?.Bindings ?? [])
                {
                    if (binding.Kind == SourceWsModelBindingKind.Complete &&
                        !state.WsDocuments.ContainsKey(binding.WsModelPath))
                    {
                        nodes.Add((binding.WsModelPath, binding.Geometry, binding.Lod, binding.Part, Normalize(binding.Material)));
                    }
                }

                foreach (var (ws, document) in state.WsDocuments)
                {
                    var geometry = Normalize(document.SelectSingleNode("/model/geometry")?.InnerText);
                    foreach (XmlNode node in document.SelectNodes("/model/materials/material") ?? EmptyXmlNodeList)
                    {
                        if (TryParseIndex(node, "lod_index", out var lod) && TryParseIndex(node, "part_index", out var part))
                            nodes.Add((Normalize(ws), geometry, lod, part, Normalize(node.InnerText)));
                    }
                }

                var generatedMaterialSet = new HashSet<string>(result.GeneratedMaterials, StringComparer.OrdinalIgnoreCase);
                var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (ws, geometry, lod, part, material) in nodes
                             .OrderBy(item => item.Ws, StringComparer.Ordinal)
                             .ThenBy(item => item.Lod)
                             .ThenBy(item => item.Part))
                {
                    if (state.VanillaTextureCoverage?.DirectConsumersByRigid.ContainsKey(geometry) == true &&
                        ClassifySharedRepackWsModel(state, ws, cancellationToken).Use == SharedRepackWsUnused)
                    {
                        continue;
                    }

                    if (generatedMaterialSet.Contains(material) ||
                        state.AtlasChangedParts.Contains((geometry, lod, part)) ||
                        state.VanillaTextureCoverage?.Materials.TryGetValue(material, out var info) != true ||
                        info == null)
                    {
                        continue;
                    }

                    var named = info.Textures.Select(texture => Normalize(texture.Texture)).FirstOrDefault(replacedTextures.Contains);
                    if (named != null && reported.Add(ws + "|" + material))
                    {
                        errors.Add(
                            $"WSModel {ws} {(state.CrossRigidGeneratedWsModelPaths.Contains(ws) ? "(generated cross-rigid) " : string.Empty)}" +
                            $"binds material {material}, which names the replaced texture {named}, so it was not rewritten");
                    }
                }
            }

            var embeddedCheckStart = errors.Count;
            var embeddedPathCount = 0;
            var mergeSourceRigids = new Dictionary<string, RmvFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var rewrittenPath in result.EmbeddedRewrittenPaths
                         .OrderBy(item => item.Rigid, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => item.Lod)
                         .ThenBy(item => item.Part)
                         .ThenBy(item => item.Type))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rigidPath = rewrittenPath.Rigid;
                var rigid = state.RigidModels.GetValueOrDefault(rigidPath);
                if (rigid?.ModelList == null)
                {
                    errors.Add($"Rewritten direct rigid is unavailable for embedded material validation: {rigidPath}");
                    continue;
                }

                // The rewritten part index is the source one; mesh merging may have renumbered the parts since.
                var outputPartIndex = rewrittenPath.Part;
                if (state.MeshMergeEntries.Any(entry =>
                        entry.RigidPath.Equals(rigidPath, StringComparison.OrdinalIgnoreCase) &&
                        entry.LodIndex == rewrittenPath.Lod))
                {
                    var sourceFile = state.Source.FindFile(rigidPath);
                    if (!mergeSourceRigids.TryGetValue(rigidPath, out var mergeSourceRigid) && sourceFile != null)
                    {
                        mergeSourceRigid = ModelFactory.Create().Load(sourceFile.DataSource.ReadData());
                        mergeSourceRigids[rigidPath] = mergeSourceRigid;
                    }

                    if (mergeSourceRigid != null && rewrittenPath.Lod >= 0 &&
                        rewrittenPath.Lod < mergeSourceRigid.ModelList.Length &&
                        rewrittenPath.Lod < rigid.ModelList.Length)
                    {
                        var partMap = BuildDirectRigidOutputPartMap(
                            state,
                            rigidPath,
                            mergeSourceRigid.ModelList[rewrittenPath.Lod].Length,
                            rewrittenPath.Lod,
                            rigid,
                            errors);
                        if (partMap.TryGetValue(rewrittenPath.Part, out var mappedPart))
                            outputPartIndex = mappedPart;
                    }
                }

                var key = (GeometryPath: rigidPath, LodIndex: rewrittenPath.Lod, PartIndex: outputPartIndex);
                if (key.LodIndex < 0 || key.LodIndex >= rigid.ModelList.Length ||
                    key.PartIndex < 0 || key.PartIndex >= rigid.ModelList[key.LodIndex].Length)
                {
                    errors.Add($"Rewritten direct rigid part is missing for embedded material validation: {key}");
                    continue;
                }

                var material = rigid.ModelList[key.LodIndex][key.PartIndex].Material;
                if (material == null)
                {
                    errors.Add($"Rewritten embedded material is unavailable: {rigidPath} lod {key.LodIndex} part {key.PartIndex}");
                    continue;
                }
                try
                {
                    var matchingTextures = material.GetAllTextures()
                        .Where(candidate => candidate.TexureType == rewrittenPath.Type)
                        .ToArray();
                    if (matchingTextures.Length != 1 || string.IsNullOrWhiteSpace(matchingTextures[0].Path))
                    {
                        errors.Add(
                            $"Rewritten embedded material texture is missing: {rigidPath} lod {key.LodIndex} " +
                            $"part {key.PartIndex} type {rewrittenPath.Type}");
                        continue;
                    }

                    var texturePath = Normalize(matchingTextures[0].Path);
                    embeddedPathCount++;
                    var resolved = CanResolveAfterRewrite(state, texturePath) ||
                        ResolveGameplayTraversalContainer(
                            state.Source,
                            GetGameplayTraversalContainers(state),
                            texturePath,
                            state.ResolutionProviders)?.FindFile(texturePath) != null;
                    if (!resolved)
                    {
                        errors.Add(
                            $"Rewritten embedded material texture does not resolve: {rigidPath} " +
                            $"lod {key.LodIndex} part {key.PartIndex} -> {texturePath}");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(
                        $"Rewritten embedded material is unreadable: {rigidPath} lod {key.LodIndex} " +
                        $"part {key.PartIndex} ({ex.Message})");
                }
            }

            if (errors.Count == embeddedCheckStart && result.EmbeddedRewrittenRigids.Count != 0)
            {
                state.ValidationMessages.Add(
                    $"PASS: {result.EmbeddedRewrittenRigids.Count} directly loaded rigid(s) have " +
                    $"{embeddedPathCount:N0} rewritten embedded material texture path(s) that resolve in output or vanilla.");
            }

            if (errors.Count == before && result.BuiltSets.Count != 0)
            {
                state.ValidationMessages.Add(
                    $"PASS: {result.BuiltSets.Count} shared texture repack set(s) over {result.RepackedRigids.Count} " +
                    $"rewritten rigid(s): {result.GeneratedTextures.Count} generated texture(s) decode with the " +
                    $"planned size, format and a complete mip chain, {result.GeneratedMaterials.Count} generated " +
                    $"material(s) resolve and are only used by WSModels of rewritten rigids, no source or generated " +
                    $"WSModel still binds a material naming a replaced texture, geometry invariants " +
                    $"hold, and {result.Samples:N0} sampled texel(s) match the source (max error " +
                    $"{result.MaxError:0}, mean {(result.ErrorCount == 0 ? 0 : result.ErrorSum / result.ErrorCount):0.00}).");
            }
        }

        // A VMD that only parsed leniently is read for its references and must stay byte-identical.
        private static void ValidateLenientVmdsUnchanged(BatchState state, List<string> errors)
        {
            var checkedCount = 0;
            var errorsBefore = errors.Count;
            foreach (var entry in state.LenientVmds)
            {
                var source = state.Source.FindFile(entry.Path);
                if (source == null)
                    continue;

                checkedCount++;
                var output = state.Output.FindFile(entry.Path);
                if (output == null ||
                    !source.DataSource.ReadData().AsSpan().SequenceEqual(output.DataSource.ReadData()))
                {
                    errors.Add($"Leniently parsed VMD was modified or removed: {entry.Path}");
                }
            }

            if (checkedCount != 0 && errors.Count == errorsBefore)
            {
                state.ValidationMessages.Add(
                    $"PASS: {checkedCount} leniently parsed source VMD(s) are byte-identical in the output.");
            }
        }

        private void ValidateDirectRigidEmbeddedTextureReferences(
            BatchState state,
            List<string> errors,
            CancellationToken cancellationToken)
        {
            var dependencyIndex = state.GameplayMeshDependencyIndex;
            if (dependencyIndex == null || dependencyIndex.DirectConsumersByRigid.Count == 0)
                return;

            var before = errors.Count;
            var directRigidCount = 0;
            var embeddedPathCount = 0;
            var containers = GetGameplayTraversalContainers(state);
            var sourceResolvedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sourceRigids = new Dictionary<string, RmvFile>(StringComparer.OrdinalIgnoreCase);
            var outputRigids = new Dictionary<string, RmvFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var rigidPath in dependencyIndex.DirectConsumersByRigid.Keys
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceFile = state.Source.FindFile(rigidPath);
                if (sourceFile == null)
                    continue;

                RmvFile sourceRigid;
                try
                {
                    sourceRigid = ModelFactory.Create().Load(sourceFile.DataSource.ReadData());
                }
                catch (Exception ex)
                {
                    errors.Add($"Direct rigid embedded material could not be read: {rigidPath} ({ex.Message})");
                    continue;
                }

                sourceRigids[rigidPath] = sourceRigid;
                foreach (var model in sourceRigid.ModelList.SelectMany(lod => lod))
                {
                    try
                    {
                        foreach (var texture in model.Material?.GetAllTextures() ?? [])
                        {
                            var path = Normalize(texture.Path);
                            if (path.Length != 0 &&
                                ResolveGameplayTraversalContainer(
                                    state.Source,
                                    containers,
                                    path,
                                    state.ResolutionProviders)?.FindFile(path) != null)
                            {
                                sourceResolvedPaths.Add(path);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Direct rigid embedded material is unreadable: {rigidPath} ({ex.Message})");
                    }
                }
            }

            var sourcePathsByOutputPart = new Dictionary<
                (string Rigid, int Lod, int Part, TextureType Type),
                HashSet<string>>();
            var sourceResolvedSlots = new HashSet<(string Rigid, int Lod, int Part, TextureType Type)>();
            foreach (var (rigidPath, sourceRigid) in sourceRigids)
            {
                var outputFile = state.Output.FindFile(rigidPath);
                if (outputFile == null)
                    continue;

                RmvFile outputRigid;
                try
                {
                    outputRigid = state.RigidModels.GetValueOrDefault(rigidPath) ??
                                  ModelFactory.Create().Load(outputFile.DataSource.ReadData());
                    outputRigids[rigidPath] = outputRigid;
                }
                catch (Exception ex)
                {
                    errors.Add($"Direct rigid embedded material could not be read from output: {rigidPath} ({ex.Message})");
                    continue;
                }

                for (var lod = 0; lod < sourceRigid.ModelList.Length; lod++)
                {
                    var partMap = BuildDirectRigidOutputPartMap(
                        state,
                        rigidPath,
                        sourceRigid.ModelList[lod].Length,
                        lod,
                        outputRigid,
                        errors);
                    foreach (var (sourcePart, outputPart) in partMap)
                    {
                        foreach (var texture in sourceRigid.ModelList[lod][sourcePart].Material?.GetAllTextures() ?? [])
                        {
                            var sourcePath = Normalize(texture.Path);
                            var key = (rigidPath, lod, outputPart, texture.TexureType);
                            if (!sourcePathsByOutputPart.TryGetValue(key, out var paths))
                            {
                                paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                sourcePathsByOutputPart[key] = paths;
                            }

                            paths.Add(sourcePath);
                            if (sourceResolvedPaths.Contains(sourcePath))
                                sourceResolvedSlots.Add(key);
                        }
                    }
                }
            }

            var rewrittenOutputParts = new HashSet<(string Rigid, int Lod, int Part, TextureType Type)>();
            var rewrittenWsModelsByPart = new Dictionary<(string Rigid, int Lod, int Part), HashSet<string>>();
            var coverage = state.VanillaTextureCoverage;
            if (coverage != null)
            {
                foreach (var binding in coverage.Bindings.Where(binding =>
                             binding.Kind == SourceWsModelBindingKind.Complete &&
                             sourceRigids.ContainsKey(binding.Geometry)))
                {
                    if (ClassifySharedRepackWsModel(state, binding.WsModelPath, cancellationToken).Use !=
                        SharedRepackWsUsed)
                    {
                        continue;
                    }

                    var sourcePartCount = sourceRigids[binding.Geometry].ModelList;
                    if (binding.Lod < 0 || binding.Lod >= sourcePartCount.Length ||
                        binding.Part < 0 || binding.Part >= sourcePartCount[binding.Lod].Length)
                    {
                        continue;
                    }

                    if (!outputRigids.TryGetValue(binding.Geometry, out var outputRigid))
                        continue;

                    var partMap = BuildDirectRigidOutputPartMap(
                        state,
                        binding.Geometry,
                        sourcePartCount[binding.Lod].Length,
                        binding.Lod,
                        outputRigid,
                        errors);
                    if (!partMap.TryGetValue(binding.Part, out var outputPart))
                        continue;

                    var key = (binding.Geometry, binding.Lod, outputPart);
                    if (!rewrittenWsModelsByPart.TryGetValue(key, out var wsModels))
                    {
                        wsModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        rewrittenWsModelsByPart[key] = wsModels;
                    }

                    wsModels.Add(binding.WsModelPath);
                }
            }

            foreach (var rewrittenPath in state.SharedTextureRepack?.EmbeddedRewrittenPaths ?? [])
            {
                if (!sourceRigids.TryGetValue(rewrittenPath.Rigid, out var sourceRigid) ||
                    rewrittenPath.Lod < 0 || rewrittenPath.Lod >= sourceRigid.ModelList.Length ||
                    rewrittenPath.Part < 0 || rewrittenPath.Part >= sourceRigid.ModelList[rewrittenPath.Lod].Length)
                {
                    errors.Add(
                        $"Rewritten direct rigid source part is missing: {rewrittenPath.Rigid} " +
                        $"lod {rewrittenPath.Lod} part {rewrittenPath.Part}");
                    continue;
                }

                if (!outputRigids.TryGetValue(rewrittenPath.Rigid, out var outputRigid))
                {
                    errors.Add($"Rewritten direct rigid is missing from output: {rewrittenPath.Rigid}");
                    continue;
                }

                var partMap = BuildDirectRigidOutputPartMap(
                    state,
                    rewrittenPath.Rigid,
                    sourceRigid.ModelList[rewrittenPath.Lod].Length,
                    rewrittenPath.Lod,
                    outputRigid,
                    errors);
                if (partMap.TryGetValue(rewrittenPath.Part, out var outputPart))
                {
                    rewrittenOutputParts.Add((rewrittenPath.Rigid, rewrittenPath.Lod, outputPart, rewrittenPath.Type));
                }
            }

            foreach (var rigidPath in dependencyIndex.DirectConsumersByRigid.Keys
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!sourceRigids.ContainsKey(rigidPath))
                    continue;

                if (!outputRigids.TryGetValue(rigidPath, out var outputRigid))
                {
                    errors.Add($"Direct rigid is missing from output: {rigidPath}");
                    continue;
                }

                directRigidCount++;
                for (var lod = 0; lod < outputRigid.ModelList.Length; lod++)
                {
                    for (var part = 0; part < outputRigid.ModelList[lod].Length; part++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var outputMaterial = outputRigid.ModelList[lod][part].Material;
                        if (outputMaterial == null)
                            continue;

                        try
                        {
                            foreach (var texture in outputMaterial.GetAllTextures())
                            {
                                var outputPath = Normalize(texture.Path);
                                var key = (rigidPath, lod, part, texture.TexureType);
                                var unchangedFromUnresolvedSource =
                                    !sourceResolvedSlots.Contains(key) &&
                                    sourcePathsByOutputPart.TryGetValue(key, out var originalPaths) &&
                                    originalPaths.Contains(outputPath);
                                if (outputPath.Length == 0 || unchangedFromUnresolvedSource)
                                {
                                    continue;
                                }

                                embeddedPathCount++;
                                if (!CanResolveDirectEmbeddedTextureAfterRewrite(state, outputPath, containers))
                                {
                                    errors.Add(
                                        $"Direct rigid embedded texture does not resolve after rewriting: {rigidPath} " +
                                        $"lod {lod} part {part} {texture.TexureType} -> {outputPath}");
                                }
                            }

                            if (rewrittenOutputParts.Any(key =>
                                    key.Rigid.Equals(rigidPath, StringComparison.OrdinalIgnoreCase) &&
                                    key.Lod == lod && key.Part == part) &&
                                rewrittenWsModelsByPart.TryGetValue((rigidPath, lod, part), out var wsModels))
                            {
                                ValidateDirectEmbeddedWsMaterialMappings(
                                    state,
                                    rigidPath,
                                    lod,
                                    part,
                                    outputMaterial,
                                    wsModels,
                                    errors);
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add(
                                $"Direct rigid embedded material is unreadable: {rigidPath} lod {lod} " +
                                $"part {part} ({ex.Message})");
                        }
                    }
                }
            }

            if (errors.Count == before && directRigidCount != 0)
            {
                state.ValidationMessages.Add(
                    $"PASS: {directRigidCount:N0} directly loaded rigid(s) keep all {embeddedPathCount:N0} " +
                    "source-resolved embedded texture paths (modern and legacy) resolvable in output or vanilla; " +
                    "rewritten embedded slots match their active WSModel material paths.");
            }
        }

        private static Dictionary<int, int> BuildDirectRigidOutputPartMap(
            BatchState state,
            string rigidPath,
            int sourcePartCount,
            int lod,
            RmvFile outputRigid,
            List<string> errors)
        {
            var mergedGroups = state.MeshMergeEntries
                .Where(entry => entry.RigidPath.Equals(rigidPath, StringComparison.OrdinalIgnoreCase) &&
                                entry.LodIndex == lod)
                .Select(entry => (Parts: entry.OldPartIndices, NewPart: entry.NewPartIndex))
                .ToArray();
            var consumed = new HashSet<int>();
            var groups = new List<(int[] Parts, int? NewPart)>();
            foreach (var group in mergedGroups)
            {
                var parts = group.Parts.Where(part => part >= 0 && part < sourcePartCount).Distinct().ToArray();
                if (parts.Length < 2)
                    continue;

                groups.Add((parts, group.NewPart));
                consumed.UnionWith(parts);
            }

            for (var part = 0; part < sourcePartCount; part++)
            {
                if (!consumed.Contains(part))
                    groups.Add(([part], null));
            }

            groups.Sort((left, right) => left.Parts.Min().CompareTo(right.Parts.Min()));
            if (lod < 0 || lod >= outputRigid.ModelList.Length ||
                groups.Count != outputRigid.ModelList[lod].Length)
            {
                errors.Add(
                    $"Direct rigid part mapping does not match output geometry: {rigidPath} lod {lod} " +
                    $"({sourcePartCount} source parts -> {groups.Count} mapped groups, " +
                    $"{(lod >= 0 && lod < outputRigid.ModelList.Length ? outputRigid.ModelList[lod].Length : 0)} output parts)");
                return [];
            }

            var map = new Dictionary<int, int>();
            for (var outputPart = 0; outputPart < groups.Count; outputPart++)
            {
                var group = groups[outputPart];
                var target = group.NewPart ?? outputPart;
                if (target != outputPart)
                {
                    errors.Add(
                        $"Direct rigid merged part index changed unexpectedly: {rigidPath} lod {lod} " +
                        $"mapped {group.NewPart} but output order requires {outputPart}");
                }

                foreach (var sourcePart in group.Parts)
                    map[sourcePart] = target;
            }

            return map;
        }

        private bool CanResolveDirectEmbeddedTextureAfterRewrite(
            BatchState state,
            string path,
            IReadOnlyList<IPackFileContainer> containers)
        {
            path = Normalize(path);
            if (state.Output.FindFile(path) != null)
                return true;
            if (state.Source.FindFile(path) != null)
                return false;

            return containers.Any(container =>
                       container.IsCaPackFile &&
                       !ReferenceEquals(container, state.Source) &&
                       container.FindFile(path) != null) ||
                   state.ResolutionProviders.Find(state.Source, path) != null ||
                   _packFileService.FindFile(path) != null;
        }

        private void ValidateDirectEmbeddedWsMaterialMappings(
            BatchState state,
            string rigidPath,
            int lod,
            int part,
            IRmvMaterial? embeddedMaterial,
            IEnumerable<string> activeWsModels,
            List<string> errors)
        {
            var wsModels = activeWsModels.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (wsModels.Length == 0 || embeddedMaterial == null)
                return;

            IReadOnlyList<RmvTexture> embeddedTextures;
            try
            {
                embeddedTextures = embeddedMaterial.GetAllTextures();
            }
            catch (Exception ex)
            {
                errors.Add(
                    $"Could not validate direct rigid/WSModel texture mapping: {rigidPath} lod {lod} " +
                    $"part {part} ({ex.Message})");
                return;
            }

            foreach (var wsModelPath in wsModels)
            {
                var wsFile = state.Output.FindFile(wsModelPath);
                if (wsFile == null)
                {
                    errors.Add($"Active WSModel is missing while validating direct rigid mapping: {wsModelPath}");
                    continue;
                }

                try
                {
                    var wsDocument = state.WsDocuments.GetValueOrDefault(wsModelPath) ?? LoadXml(wsFile);
                    var assignments = (wsDocument.SelectNodes("/model/materials/material") ?? EmptyXmlNodeList)
                        .Cast<XmlNode>()
                        .Where(node => TryParseIndex(node, "lod_index", out var nodeLod) && nodeLod == lod &&
                                       TryParseIndex(node, "part_index", out var nodePart) && nodePart == part)
                        .ToArray();
                    if (assignments.Length != 1)
                    {
                        errors.Add(
                            $"Active WSModel has no unique direct-rigid material assignment: {wsModelPath} " +
                            $"lod {lod} part {part}");
                        continue;
                    }

                    var materialPath = Normalize(assignments[0].InnerText);
                    var materialFile = state.Output.FindFile(materialPath);
                    if (materialFile == null && state.Source.FindFile(materialPath) == null)
                    {
                        materialFile = state.ResolutionProviders.Find(state.Source, materialPath)?.FindFile(materialPath) ??
                                       _packFileService.FindFile(materialPath);
                    }
                    if (materialFile == null)
                    {
                        errors.Add(
                            $"Active WSModel material is missing while validating direct rigid mapping: " +
                            $"{wsModelPath} -> {materialPath}");
                        continue;
                    }

                    var materialDocument = LoadXml(materialFile);
                    var nodes = (materialDocument.SelectNodes("/material/textures/texture") ?? EmptyXmlNodeList)
                        .Cast<XmlNode>()
                        .ToArray();
                    foreach (var texture in embeddedTextures)
                    {
                        if (string.IsNullOrWhiteSpace(texture.Path) ||
                            !TryGetSharedRepackEmbeddedSlot(texture.TexureType, out var slot))
                        {
                            continue;
                        }

                        var sources = nodes
                            .Where(node => GetTextureSlot(node).Equals(slot, StringComparison.OrdinalIgnoreCase))
                            .Select(node => Normalize(node.SelectSingleNode("source")?.InnerText ?? node.InnerText))
                            .ToArray();
                        var embeddedPath = Normalize(texture.Path);
                        if (sources.Length != 1 ||
                            !sources[0].Equals(embeddedPath, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add(
                                $"Direct rigid embedded/WSModel texture paths disagree: {rigidPath} lod {lod} " +
                                $"part {part} {texture.TexureType} -> {embeddedPath}; " +
                                $"{wsModelPath} -> {materialPath} slot {slot} -> " +
                                (sources.Length == 0 ? "(missing)" : string.Join(", ", sources)));
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(
                        $"Could not validate active direct-rigid WSModel mapping: {wsModelPath} " +
                        $"lod {lod} part {part} ({ex.Message})");
                }
            }
        }

        // The funnel in bytes: where the per-set estimate goes. Negative estimates count as zero, and
        // a set merged into a built plan counts toward that plan, so the buckets add up to the total.
        private static SharedRepackGroupWaste MeasureSharedRepackGroupWaste(
            bool gated,
            long sourceBytes,
            List<SharedRepackPage> pages,
            SharedRepackSplitInfo split,
            bool newlyBuilt)
        {
            double used = 0, charts = 0, trimmed = 0, page = 0;
            foreach (var item in pages)
            {
                var area = (double)item.LayoutWidth * item.LayoutHeight;
                if (area <= 0 || item.Charts.Count == 0)
                    continue;

                var perPixel = item.BytesAfter / area;
                page += item.BytesAfter;
                var width = item.Charts.Max(chart => chart.PackX + chart.LayoutWidth);
                var height = item.Charts.Max(chart => chart.PackY + chart.LayoutHeight);
                trimmed += perPixel * width * height;
                foreach (var chart in item.Charts)
                {
                    charts += perPixel * chart.LayoutWidth * chart.LayoutHeight;
                    var tuple = chart.Tuple;
                    var cells = 0;
                    for (var y = chart.CellY0; y <= chart.CellY1; y++)
                    {
                        for (var x = chart.CellX0; x <= chart.CellX1; x++)
                        {
                            if (tuple.CellChart[y * tuple.GridX + x] == chart.Id)
                                cells++;
                        }
                    }

                    used += perPixel * cells * tuple.Cell * tuple.Cell;
                }
            }

            return new SharedRepackGroupWaste(
                gated,
                sourceBytes,
                (long)used,
                (long)charts,
                (long)trimmed,
                (long)page,
                Split: split,
                PaddingVariant: split.PaddingVariant,
                PaddingSavedBytes: split.PaddingSavedBytes,
                NewlyBuilt: newlyBuilt);
        }

        // Where the bytes of the repacked pages go, and what pages trimmed to their packed charts
        // (not a power of two) would change. Trimmed figures scale the page bytes by area, so they
        // are an estimate; whether the game accepts such textures is not checked here.
        private static void AppendSharedRepackPageWaste(StringBuilder sb, SharedTextureRepackResult result)
        {
            var built = result.Outcomes
                .Where(outcome => outcome.Bucket == SharedRepackBucketBuilt && outcome.Context != null)
                .SelectMany(outcome => outcome.Context!.GroupWaste.Where(waste => !waste.Gated))
                .ToList();
            var gated = result.Outcomes
                .Where(outcome => outcome.Context != null)
                .SelectMany(outcome => outcome.Context!.GroupWaste.Where(waste => waste.Gated))
                .ToList();
            var groups = result.BuiltSets.SelectMany(set => set.Groups).ToList();
            var emittedGroups = groups.Where(group => group.Pages != 0).ToList();
            var untouchedGroups = groups.Where(group => group.Pages == 0).ToList();
            var loweredGroups = groups.Where(group => group.PaddingRetryFromVariant != null).ToList();
            if (built.Count == 0 && gated.Count == 0 && result.PaddingPixelRetryAttempts == 0)
                return;

            sb.AppendLine("Reduced padding (emitted groups only; ties keep the safer option):");
            foreach (var (variant, name) in new[]
                     {
                         ("baseline", "P0 baseline"),
                         ("align 16", "P1 align 16"),
                         ("gutter 8 + align 8", "P2 gutter 8 + align 8"),
                     })
            {
                var items = emittedGroups.Where(group => group.PaddingVariant == variant).ToList();
                sb.AppendLine(
                    $"  {name}: {items.Count:N0} emitted group(s), {items.Count(group => group.NewlyBuilt):N0} newly built group(s), " +
                    $"{items.Sum(group => group.PaddingSavedBytes):N0} bytes saved vs P0");
            }

            sb.AppendLine(
                $"  Gated/untouched groups in emitted sets: {untouchedGroups.Count:N0} " +
                $"(P0 {untouchedGroups.Count(group => group.PaddingVariant == "baseline"):N0}, " +
                $"P1 {untouchedGroups.Count(group => group.PaddingVariant == "align 16"):N0}, " +
                $"P2 {untouchedGroups.Count(group => group.PaddingVariant == "gutter 8 + align 8"):N0})");
            sb.AppendLine($"  Pixel-check padding retries: {result.PaddingPixelRetryAttempts:N0} retry pass(es)");
            sb.AppendLine(
                $"  Groups lowered by pixel check: {loweredGroups.Count:N0} " +
                $"({loweredGroups.Count(group => group.Pages != 0):N0} emitted, " +
                $"{loweredGroups.Count(group => group.Pages == 0):N0} gated/untouched)");
            foreach (var group in loweredGroups.OrderBy(group => group.Label, StringComparer.Ordinal))
            {
                sb.AppendLine(
                    $"    {GetSharedRepackPaddingCode(group.PaddingRetryFromVariant!)} -> " +
                    $"{GetSharedRepackPaddingCode(group.PaddingVariant)}: {group.Label}" +
                    (group.Pages == 0 ? " [gated/untouched]" : string.Empty));
            }

            sb.AppendLine(
                "Page layout (output bytes at each page's bytes per reference pixel; \"trimmed\" = the same packing " +
                "packing cut to its used width and height, i.e. pages that are not a power of two):");
            if (built.Count != 0)
            {
                var page = built.Sum(waste => waste.PageBytes);
                var used = built.Sum(waste => waste.UsedBytes);
                var charts = built.Sum(waste => waste.ChartBytes);
                var trimmed = built.Sum(waste => waste.TrimmedBytes);
                sb.AppendLine(
                    $"  Built groups ({built.Count:N0}): sources {built.Sum(waste => waste.SourceBytes):N0} -> pages " +
                    $"{page:N0} bytes = touched cells {used:N0} + chart padding/bounding/alignment {charts - used:N0} + " +
                    $"packing gaps {trimmed - charts:N0} + power-of-two rounding {page - trimmed:N0}");
            }

            if (gated.Count != 0)
            {
                var source = gated.Sum(waste => waste.SourceBytes);
                var page = gated.Sum(waste => waste.PageBytes);
                var trimmed = gated.Sum(waste => waste.TrimmedBytes);
                var passing = gated
                    .Where(waste => waste.SourceBytes - waste.TrimmedBytes >= result.MinimumGroupSavingBytes)
                    .ToList();
                sb.AppendLine(
                    $"  Groups left untouched because their pages did not get small enough ({gated.Count:N0}): " +
                    $"sources {source:N0} bytes, power-of-two pages {page:N0}, trimmed pages {trimmed:N0}; " +
                    $"{passing.Count:N0} would pass the {result.MinimumGroupSavingBytes:N0}-byte group minimum trimmed, " +
                    $"saving {passing.Sum(waste => waste.SourceBytes - waste.TrimmedBytes):N0} bytes (before the set threshold " +
                    "and the closure of their sets)");
                var gatedUsed = gated.Sum(waste => waste.UsedBytes);
                var gatedCharts = gated.Sum(waste => waste.ChartBytes);
                sb.AppendLine(
                    $"    their pages = touched cells {gatedUsed:N0} + chart padding/bounding/alignment " +
                    $"{gatedCharts - gatedUsed:N0} + packing gaps {trimmed - gatedCharts:N0} + power-of-two rounding {page - trimmed:N0}");
            }

            // Tight pieces: one rectangle per UV island instead of per merged chart, used by a group only
            // when its pages then hold fewer bytes (the figures above already include them).
            var tightBuilt = built.Where(waste => waste.Split is { TightTried: true }).Select(waste => waste.Split!).ToList();
            var tightGated = gated.Where(waste => waste.Split is { TightTried: true }).Select(waste => waste.Split!).ToList();
            var tightAll = tightBuilt.Concat(tightGated).ToList();
            if (tightAll.Count != 0)
            {
                var usedBuilt = tightBuilt.Where(split => split.TightUsed).ToList();
                var usedGated = tightGated.Where(split => split.TightUsed).ToList();
                sb.AppendLine(
                    $"  Tight pieces (one rectangle per UV island instead of per merged chart, same gutter and alignment): " +
                    $"{tightAll.Count:N0} group(s) had merged charts to split; used by {usedBuilt.Count:N0} built group(s) " +
                    $"({usedBuilt.Sum(split => split.TightPieces):N0} piece(s) instead of {usedBuilt.Sum(split => split.TightMergedCharts):N0} " +
                    $"chart(s), pages {FormatSignedBytes(-usedBuilt.Sum(split => split.TightSavedBytes))} bytes) and by " +
                    $"{usedGated.Count:N0} left-untouched group(s) ({usedGated.Sum(split => split.TightPieces):N0} piece(s) instead of " +
                    $"{usedGated.Sum(split => split.TightMergedCharts):N0} chart(s), pages {FormatSignedBytes(-usedGated.Sum(split => split.TightSavedBytes))} bytes); " +
                    $"{tightAll.Count(split => !split.TightUsed && !split.TightInconsistent):N0} group(s) kept the merged charts " +
                    $"(pieces not smaller); internal inconsistencies (merged charts kept): {tightAll.Count(split => split.TightInconsistent):N0}");
            }
        }

        private static string GetSharedRepackPaddingCode(string variant)
            => variant switch
            {
                "align 16" => "P1",
                "gutter 8 + align 8" => "P2",
                _ => "P0",
            };

        // Why sets lost textures while closing. A set dropped by closure has its estimate split over
        // the causes in proportion to the chain bytes each cause removed (what stayed in the set
        // counts as "left, but saves below the threshold"); built plans list the bytes they lost.
        private static void AppendSharedRepackClosureCauses(
            StringBuilder sb,
            SharedTextureRepackResult result,
            List<SharedRepackSetOutcome> builtOutcomes)
        {
            const string LeftReason = "stayed in the set, but what was left saves below the threshold";
            var closure = result.Outcomes
                .Where(outcome => outcome.Bucket == SharedRepackBucketClosure && outcome.Context != null)
                .ToList();
            if (closure.Count != 0)
            {
                var byCause = new Dictionary<string, (double Estimate, long Bytes, int Sets)>(StringComparer.Ordinal);
                foreach (var outcome in closure)
                {
                    var context = outcome.Context!;
                    var causes = context.RemovedTextures.Values
                        .GroupBy(item => item.Reason, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.Sum(item => item.Bytes), StringComparer.Ordinal);
                    var left = context.PlanTextureBytes - causes.Values.Sum();
                    if (left > 0)
                        causes[LeftReason] = left;
                    var whole = causes.Values.Sum();
                    foreach (var (cause, bytes) in causes)
                    {
                        var share = whole <= 0 ? 0 : (double)Math.Max(0, outcome.Estimate) * bytes / whole;
                        var known = byCause.GetValueOrDefault(cause);
                        byCause[cause] = (known.Estimate + share, known.Bytes + bytes, known.Sets + 1);
                    }
                }

                sb.AppendLine(
                    "Dropped by closure, by cause (each set's estimate split over the causes in proportion to the " +
                    "texture bytes each removed from it; first cause per texture):");
                foreach (var (cause, (estimate, bytes, sets)) in byCause
                             .OrderByDescending(pair => pair.Value.Estimate)
                             .ThenBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    sb.AppendLine(
                        $"  {(long)Math.Round(estimate):N0} bytes estimated ({sets:N0} set(s), {bytes:N0} texture bytes): {cause}");
                }
            }

            var builtCauses = builtOutcomes
                .Where(outcome => outcome.Context != null)
                .SelectMany(outcome => outcome.Context!.RemovedTextures.Values)
                .GroupBy(item => item.Reason, StringComparer.Ordinal)
                .Select(group => (Reason: group.Key, Bytes: group.Sum(item => item.Bytes), Textures: group.Count()))
                .OrderByDescending(item => item.Bytes)
                .ThenBy(item => item.Reason, StringComparer.Ordinal)
                .ToList();
            var builtPlanBytes = builtOutcomes.Where(outcome => outcome.Context != null)
                .Sum(outcome => outcome.Context!.PlanTextureBytes);
            sb.AppendLine(
                $"Built plans: {builtCauses.Sum(item => item.Bytes):N0} of {builtPlanBytes:N0} texture bytes left " +
                "them while closing (kept as authored; part of the shortfall), by cause:");
            foreach (var (reason, bytes, textures) in builtCauses)
                sb.AppendLine($"  {bytes:N0} bytes ({textures:N0} texture(s)): {reason}");
        }

        // Report only: where the "material also uses textures outside the set" losses come from (the
        // root cause of each such texture loss), and what the copy what-if (RunCopyWhatIf) estimates.
        private static void AppendSharedRepackMixedMaterials(
            StringBuilder sb,
            SharedTextureRepackResult result,
            double realisedShare)
        {
            static bool IsMixed(string reason) => reason is SharedRepackReasonMixed or SharedRepackReasonBlockedTexture;

            var withContext = result.Outcomes.Where(outcome => outcome.Context != null).ToList();
            if (!withContext.Any(outcome => outcome.Context!.RemovedTextures.Values.Any(item => IsMixed(item.Reason))))
                return;

            var bucketOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var outcome in result.Outcomes)
                bucketOf.TryAdd(outcome.Label, outcome.Bucket);
            string RootText(SharedRepackClosureRoot root)
                => root.OtherSet == null
                    ? root.Category
                    : $"{root.Category} (that set: {bucketOf.GetValueOrDefault(root.OtherSet, "not reported")})";

            // "Merging is off" only happens in the alone retry: the cause is why the merged attempt failed.
            static string MergedFailure(string? reason)
            {
                const string Prefix = "the merged attempt with ";
                if (reason == null || !reason.StartsWith(Prefix, StringComparison.Ordinal))
                    return "unknown";
                if (reason.Contains("failed (no part is left after closing", StringComparison.Ordinal))
                    return "no part was left after closing the merged set";
                if (reason.Contains("failed (after closing the set", StringComparison.Ordinal))
                    return "the merged set's estimate after closing was below the threshold";
                if (reason.Contains("failed (every texture tuple was left untouched", StringComparison.Ordinal))
                    return "every texture tuple of the merged plan was left untouched (pages not smaller)";
                if (reason.Contains("failed (actual bytes", StringComparison.Ordinal))
                    return "the merged plan failed the actual-bytes gate";
                if (reason.Contains("failed (failed the pixel check", StringComparison.Ordinal))
                    return "the merged plan failed the pixel check";
                return "other: " + Shorten(reason[Prefix.Length..], 80);
            }

            Dictionary<string, long> MixedBytesByRoot(SharedRepackSetOutcome outcome)
            {
                var context = outcome.Context!;
                return context.RemovedTextures
                    .Where(pair => IsMixed(pair.Value.Reason))
                    .GroupBy(
                        pair =>
                        {
                            var root = RootText(context.RemovedRoots.GetValueOrDefault(pair.Key) ?? new(pair.Value.Reason, null));
                            return root.Contains("merging is off", StringComparison.Ordinal)
                                ? $"{root}; the merged attempt: {MergedFailure(outcome.Reason ?? context.FallbackReason)}"
                                : root;
                        },
                        StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Sum(pair => pair.Value.Bytes), StringComparer.Ordinal);
            }

            sb.AppendLine(
                "Mixed materials (report only): texture losses because a bound material also uses a texture outside " +
                "the set, by the root cause of that outside texture (when a part has several, the one a fix has to " +
                "remove first: cannot be re-encoded, blocked or not repackable, unknown or in no set, via a dropped " +
                "part, set processed before this one, merge cap, other):");
            var closure = new Dictionary<string, (double Estimate, long Bytes, int Sets)>(StringComparer.Ordinal);
            foreach (var outcome in withContext.Where(outcome => outcome.Bucket == SharedRepackBucketClosure))
            {
                var context = outcome.Context!;
                var whole = Math.Max(context.PlanTextureBytes, context.RemovedTextures.Values.Sum(item => item.Bytes));
                foreach (var (root, bytes) in MixedBytesByRoot(outcome))
                {
                    var share = whole <= 0 ? 0 : (double)Math.Max(0, outcome.Estimate) * bytes / whole;
                    var known = closure.GetValueOrDefault(root);
                    closure[root] = (known.Estimate + share, known.Bytes + bytes, known.Sets + 1);
                }
            }

            sb.AppendLine("  Dropped by closure (each set's estimate split as in the closure causes above):");
            if (closure.Count == 0)
                sb.AppendLine("    none");
            foreach (var (root, (estimate, bytes, sets)) in closure
                         .OrderByDescending(pair => pair.Value.Estimate)
                         .ThenBy(pair => pair.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"    {(long)Math.Round(estimate):N0} bytes estimated ({sets:N0} set(s), {bytes:N0} texture bytes): {root}");
            }

            foreach (var (title, outcomes) in new[]
                     {
                         ("Built plans (texture bytes kept as authored)",
                             withContext.Where(outcome => outcome.Bucket == SharedRepackBucketBuilt).ToList()),
                         ("Other failed sets (actual-bytes gate, pixel check, not buildable; texture bytes)",
                             withContext.Where(outcome => outcome.Bucket is SharedRepackBucketBytes or SharedRepackBucketPixel
                                 or SharedRepackBucketBuild).ToList()),
                     })
            {
                var byRoot = outcomes
                    .SelectMany(MixedBytesByRoot)
                    .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(group => (Root: group.Key, Bytes: group.Sum(pair => pair.Value), Sets: group.Count()))
                    .OrderByDescending(item => item.Bytes)
                    .ThenBy(item => item.Root, StringComparer.Ordinal)
                    .ToList();
                sb.AppendLine($"  {title}:");
                if (byRoot.Count == 0)
                    sb.AppendLine("    none");
                foreach (var (root, bytes, sets) in byRoot)
                    sb.AppendLine($"    {bytes:N0} bytes ({sets:N0} set(s)): {root}");
            }

            var threshold = result.MinimumSavingBytes;
            var whatIfs = withContext.Where(outcome => outcome.Context!.WhatIf != null).ToList();
            long? Real(SharedRepackSetOutcome outcome)
                => outcome.Context!.ClosureEstimated ? outcome.Context.ClosureToday - outcome.Context.ClosureEstimate : null;
            long Gain(SharedRepackSetOutcome outcome)
            {
                // A set closure dropped gains the whole what-if saving once it reaches the threshold;
                // a set that got past closure (built, or failed a later gate) only the improvement.
                var whatIf = outcome.Context!.WhatIf!;
                if (whatIf.Stop != null)
                    return 0;
                if (outcome.Bucket != SharedRepackBucketClosure)
                    return Math.Max(0, whatIf.Saving - (Real(outcome) ?? 0));
                return whatIf.Saving >= threshold ? whatIf.Saving : 0;
            }

            sb.AppendLine(
                "Copy what-if (report only): each set with such losses planned again ALONE (no merging), with every " +
                "outside texture that can be re-encoded cropped into the set as a copy (the original stays for its " +
                "other users, so a copy is a cost, not a saving). Closure re-estimate only (before charts, layout and " +
                $"the actual-bytes gate), so optimistic: built plans realised {realisedShare:P0} of their estimate. " +
                "A built plan's real figure is its last closure re-estimate (a merged plan's includes its joined sets).");
            var notBuilt = whatIfs.Where(outcome => outcome.Bucket == SharedRepackBucketClosure).ToList();
            var reaching = notBuilt.Where(outcome => Gain(outcome) > 0).ToList();
            var built = whatIfs.Where(outcome => outcome.Bucket != SharedRepackBucketClosure).ToList();
            var improved = built.Where(outcome => Gain(outcome) > 0).ToList();
            sb.AppendLine(
                $"  Dropped by closure ({notBuilt.Count:N0} set(s)): {reaching.Count:N0} would reach the {threshold:N0}-byte " +
                $"threshold, estimated {reaching.Sum(Gain):N0} bytes with {reaching.Sum(outcome => outcome.Context!.WhatIf!.Copies):N0} " +
                $"copies costing {reaching.Sum(outcome => outcome.Context!.WhatIf!.CopyBytes):N0} bytes " +
                $"(realistically about {(long)(reaching.Sum(Gain) * realisedShare):N0})");
            sb.AppendLine(
                $"  Past closure ({built.Count:N0} set(s): built, or failed a later gate, which copies do not change): " +
                $"{improved.Count:N0} would save more, +{improved.Sum(Gain):N0} bytes " +
                $"estimated with {improved.Sum(outcome => outcome.Context!.WhatIf!.Copies):N0} copies costing " +
                $"{improved.Sum(outcome => outcome.Context!.WhatIf!.CopyBytes):N0} bytes " +
                $"(realistically about {(long)(improved.Sum(Gain) * realisedShare):N0})");
            foreach (var group in whatIfs
                         .Where(outcome => outcome.Context!.WhatIf!.Stop != null)
                         .GroupBy(outcome => outcome.Context!.WhatIf!.Stop!, StringComparer.Ordinal)
                         .OrderByDescending(group => group.Count())
                         .ThenBy(group => group.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"  Stopped before the re-estimate ({group.Count():N0} set(s)): {Shorten(group.Key, 160)}");
            }

            sb.AppendLine("  Sets by gain (top 20):");
            foreach (var outcome in whatIfs
                         .Where(outcome => Gain(outcome) > 0)
                         .OrderByDescending(Gain)
                         .ThenBy(outcome => outcome.Label, StringComparer.Ordinal)
                         .Take(20))
            {
                var whatIf = outcome.Context!.WhatIf!;
                var real = Real(outcome);
                sb.AppendLine(
                    $"    +{Gain(outcome):N0}: {Shorten(outcome.Label, 100)} [{outcome.Bucket}]: closure saving " +
                    $"{(real == null ? "-" : FormatSignedBytes(real.Value))} -> {FormatSignedBytes(whatIf.Saving)} with " +
                    $"{whatIf.Copies:N0} copies ({whatIf.CopyBytes:N0} bytes)");
            }
        }

        // Report only: every merged attempt that failed (its seed then ran alone), with the joined sets,
        // the failure, where the texture bytes went while closing (by root cause) and its closure notes.
        private static void AppendSharedRepackFailedMerges(StringBuilder sb, SharedTextureRepackResult result)
        {
            var failed = result.Outcomes.Where(outcome => outcome.Context?.FailedMerged != null).ToList();
            if (failed.Count == 0)
                return;

            var bucketOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var outcome in result.Outcomes)
                bucketOf.TryAdd(outcome.Label, outcome.Bucket);
            sb.AppendLine(
                $"Failed merged attempts (report only; {failed.Count:N0}): joined sets, failure, texture bytes that left " +
                "the merged set while closing by root cause (a part pulled out by a texture outside the set is traced " +
                "to that texture's cause), and the attempt's closure notes:");
            foreach (var outcome in failed.OrderByDescending(outcome => outcome.Estimate).ThenBy(outcome => outcome.Label, StringComparer.Ordinal))
            {
                var context = outcome.Context!;
                var merged = context.FailedMerged!;
                sb.AppendLine($"  {outcome.Label} [now: {outcome.Bucket}; estimate {outcome.Estimate:N0}]");
                foreach (var decision in merged.Component.Skip(1))
                {
                    var joined = DescribeSharedRepackSet(decision);
                    sb.AppendLine($"    joined: {joined} [{bucketOf.GetValueOrDefault(joined, "not reported")}; estimate {decision.Saving:N0}]");
                }

                sb.AppendLine($"    failed: {Shorten(context.FailedMergedFailure ?? "unknown", 400)}");
                var removed = merged.RemovedTextures
                    .GroupBy(
                        pair =>
                        {
                            var root = merged.RemovedRoots.GetValueOrDefault(pair.Key);
                            return root == null
                                ? pair.Value.Reason
                                : root.OtherSet == null ? root.Category : $"{root.Category} ({root.OtherSet})";
                        },
                        StringComparer.Ordinal)
                    .Select(group => (Root: group.Key, Bytes: group.Sum(pair => pair.Value.Bytes), Textures: group.Count()))
                    .OrderByDescending(item => item.Bytes)
                    .ThenBy(item => item.Root, StringComparer.Ordinal)
                    .ToList();
                var left = merged.PlanTextureBytes - removed.Sum(item => item.Bytes);
                sb.AppendLine(
                    $"    texture bytes: {merged.PlanTextureBytes:N0} taken in, {left:N0} still in the set after closing" +
                    (merged.ClosureEstimated
                        ? $" (last re-estimate saves {FormatSignedBytes(merged.ClosureToday - merged.ClosureEstimate)})"
                        : string.Empty));
                foreach (var (root, bytes, textures) in removed)
                    sb.AppendLine($"      {bytes:N0} bytes ({textures:N0} texture(s)) left: {Shorten(root, 300)}");
                foreach (var note in context.FailedMergedNotes.Take(10))
                    sb.AppendLine($"      note: {Shorten(note, 300)}");
                if (context.FailedMergedNotes.Count > 10)
                    sb.AppendLine($"      ... {context.FailedMergedNotes.Count - 10:N0} more note(s)");
            }
        }

        private static void AppendSharedRepackEstimateFunnel(StringBuilder sb, SharedTextureRepackResult result)
        {
            if (result.Outcomes.Count == 0)
                return;

            static long Positive(SharedRepackSetOutcome outcome) => Math.Max(0, outcome.Estimate);

            var total = result.Outcomes.Sum(Positive);
            sb.AppendLine(
                "Estimated saving by outcome (each set's own estimate, negatives counted as 0; a set merged into " +
                "a built plan counts toward that plan):");
            sb.AppendLine(
                $"  All considered sets: {total:N0} bytes over " +
                $"{result.Outcomes.Count(outcome => outcome.Estimate > 0):N0} set(s) with a positive estimate");
            long accounted = 0;
            foreach (var bucket in new[]
                     {
                         SharedRepackBucketBelow, SharedRepackBucketIneligible, SharedRepackBucketClosure,
                         SharedRepackBucketBuild, SharedRepackBucketBytes, SharedRepackBucketPixel,
                     })
            {
                var items = result.Outcomes.Where(outcome => outcome.Bucket == bucket).ToList();
                var bytes = items.Sum(Positive);
                accounted += bytes;
                sb.AppendLine($"  Lost, {bucket}: {bytes:N0} bytes ({items.Count:N0} set(s), {Share(bytes, total)})");
            }

            var builtOutcomes = result.Outcomes.Where(outcome => outcome.Bucket == SharedRepackBucketBuilt).ToList();
            var mergedByPlan = result.Outcomes
                .Where(outcome => outcome.Bucket == SharedRepackBucketMerged)
                .GroupBy(outcome => outcome.Plan ?? string.Empty, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Sum(Positive), StringComparer.Ordinal);
            var seedEstimate = builtOutcomes.Sum(Positive);
            var joinedEstimate = mergedByPlan.Values.Sum();
            var builtEstimate = seedEstimate + joinedEstimate;
            var actual = builtOutcomes.Sum(outcome => outcome.Actual ?? 0);
            accounted += builtEstimate;
            sb.AppendLine(
                $"  Built plans: estimated {builtEstimate:N0} bytes ({Share(builtEstimate, total)}; seed sets " +
                $"{seedEstimate:N0} + joined sets {joinedEstimate:N0}), actual {FormatSignedBytes(actual)} " +
                $"({Share(actual, builtEstimate)} of their estimate); shortfall {FormatSignedBytes(builtEstimate - actual)} bytes");
            sb.AppendLine(
                $"  Realised: {FormatSignedBytes(actual)} of {total:N0} estimated bytes ({Share(actual, total)}). " +
                $"Check: {accounted:N0} of {total:N0} bytes accounted " +
                (accounted == total ? "(reconciles)" : "(DOES NOT RECONCILE)"));

            AppendSharedRepackClosureCauses(sb, result, builtOutcomes);
            AppendSharedRepackMixedMaterials(sb, result, builtEstimate <= 0 ? 0 : (double)actual / builtEstimate);
            AppendSharedRepackFailedMerges(sb, result);

            AppendSharedRepackPageWaste(sb, result);
            AppendSharedRepackPackingSimulation(sb, result);

            var lost = result.Outcomes
                .Where(outcome => outcome.Bucket != SharedRepackBucketBuilt &&
                                  outcome.Bucket != SharedRepackBucketMerged &&
                                  outcome.Estimate >= result.MinimumSavingBytes)
                .OrderByDescending(outcome => outcome.Estimate)
                .ThenBy(outcome => outcome.Label, StringComparer.Ordinal)
                .ToList();
            if (lost.Count != 0)
            {
                sb.AppendLine($"Largest estimates that were not built ({Math.Min(15, lost.Count):N0} of {lost.Count:N0} at or above the threshold):");
                foreach (var outcome in lost.Take(15))
                {
                    sb.AppendLine(
                        $"  {outcome.Estimate:N0} bytes, {outcome.Bucket}: {outcome.Label}: " +
                        Truncate(outcome.Reason ?? string.Empty, 240));
                }
            }

            var shortfalls = builtOutcomes
                .Select(outcome =>
                {
                    var estimate = Positive(outcome) + mergedByPlan.GetValueOrDefault(outcome.Plan ?? string.Empty);
                    return (outcome.Label, Estimate: estimate, Actual: outcome.Actual ?? 0);
                })
                .Where(item => item.Estimate > item.Actual)
                .OrderByDescending(item => item.Estimate - item.Actual)
                .ThenBy(item => item.Label, StringComparer.Ordinal)
                .ToList();
            if (shortfalls.Count != 0)
            {
                sb.AppendLine($"Built plans with the largest shortfall against their estimate ({Math.Min(10, shortfalls.Count):N0} of {shortfalls.Count:N0}):");
                foreach (var (label, estimate, saved) in shortfalls.Take(10))
                {
                    sb.AppendLine(
                        $"  short by {estimate - saved:N0} bytes: {label}: estimated {estimate:N0}, actual {FormatSignedBytes(saved)}");
                }
            }

            static string Share(long part, long whole)
                => whole <= 0 ? "n/a" : $"{(double)part / whole:P1}";

            static string Truncate(string text, int length)
                => text.Length <= length ? text : text[..length] + "...";
        }

        private static void AppendSharedTextureRepackSection(StringBuilder sb, BatchState state)
        {
            sb.AppendLine("Shared texture repack");
            sb.AppendLine("---------------------");
            sb.AppendLine($"Shared texture repack: {(state.SharedTextureRepackEnabled ? "YES" : "NO")}");
            var result = state.SharedTextureRepack;
            if (!state.SharedTextureRepackEnabled || result == null)
            {
                sb.AppendLine(
                    state.SharedTextureRepackRequested
                        ? "Requested but ignored: not available in cross-rigid-only mode (which never writes " +
                          "textures). No pack file was changed."
                        : "Off (opt-in). No pack file was changed.");
                sb.AppendLine();
                return;
            }

            if (result.UnavailableReason != null)
            {
                sb.AppendLine($"Not run: {result.UnavailableReason}. No pack file was changed.");
                sb.AppendLine();
                return;
            }

            sb.AppendLine(
                "Mode: REWRITES the pack. For each selected signature set whose users are all mod-owned rigids, new " +
                "smaller textures hold only the regions its parts use (same texel density, no downscaling), the " +
                "UVs of every rigid using them are rewritten for all LODs, new materials are written and the " +
                "rigids' WSModels repointed. A rigid that cannot be rewritten only loses its parts: their textures " +
                "leave the set. A texture of any size that is one flat colour is kept as authored and never blocks " +
                "a part. A material whose textures belong to different signature sets pulls the other sets into " +
                "one plan (never a set that was already processed); a plan is written completely or not at all, " +
                "and a failed merged plan falls back to its seed set alone. " +
                "Original textures and materials are not edited. Video memory only. Pass-through slots (" +
                string.Join(", ", SharedRepackPassThroughSlots) + ") are kept unchanged in the generated " +
                "materials, as is any t_xml_ slot the material's shader never binds (compiled-shader analysis, " +
                $"{SharedRepackShaderBoundSlots.Count:N0} shader(s); a slot the shader does not read cannot change the " +
                "picture); any other non-atlasable slot makes a part ineligible, named per slot below. " +
                $"ASSUMPTION (check in game): the blood overlay samples UV0 x {SharedRepackBloodScaleParameter}; " +
                "the repack scales UV0 per tuple, so a float2 " + SharedRepackBloodScaleParameter +
                " is divided by that scale per axis (pattern density preserved, a per-chart offset remains). " +
                "Emissive distortion copy: a material whose shader is verified (" +
                $"{SharedRepackEmissiveVerifiedShaders.Count:N0} shader(s), compiled-shader analysis) and that has the " +
                $"parameters {string.Join(", ", SharedRepackEmissiveParameters)} keeps its {SharedRepackEmissiveDistortionSlot} " +
                $"({SharedRepackEmissiveOutputSuffix}) as a cropped COPY of the shared noise texture (the original is " +
                "not replaced and stays loaded for other users; it never joins a signature set) and its glow texture as " +
                "authored; the pixel shader samples the glow at (UV0 + distortion * strength + time * speed * direction) * " +
                "tiling, so with the UV0 scale s of the page the parameters become tiling / s, strength * s, direction * s " +
                "(the canvases of such groups keep the reference aspect ratio so s is equal in U and V). ASSUMPTION " +
                "(check in game): the scrolling pattern is exact up to a constant per-chart phase shift (the chart " +
                "offset times the tiling), so the glow can look shifted between charts; other shaders keep the part " +
                "ineligible. " +
                "Dissolve: a material whose shader is verified (" +
                $"{SharedRepackDissolveVerifiedShaders.Count:N0} shader(s), compiled-shader analysis) and that has a float2 " +
                $"{SharedRepackDissolveScaleParameter} keeps its {SharedRepackDissolveDetailSlot} as authored and its " +
                $"{SharedRepackDissolveMapSlot} ({SharedRepackDissolveOutputSuffix}) becomes a cropped COPY of the shared noise " +
                "texture like the emissive one (no parameter compensation, no aspect restriction); the pixel shader " +
                $"samples the detail at UV0 * {SharedRepackDissolveScaleParameter} and the map at UV0, so " +
                $"{SharedRepackDissolveScaleParameter} is divided per axis by the UV scale of the page (like the blood scale). " +
                "ASSUMPTION (check in game): the dissolve pattern is exact up to a constant per-chart phase shift of " +
                "the detail texture; other shaders keep the part ineligible. " +
                "Flow overlay is enabled only for the eight verified weighted2/weighted4 families: model-local " +
                "flow_map joins the local atlas closure, a commontextures flow_map is copied per chart, GUID noise is " +
                "copied without joining closure, and flow_noise/flow_normal/flow_normal_2 stay authored. For equal U/V " +
                "scale s, flow_noise_tiling and flow_texture_tiling are divided by s and flow_noise_speed is multiplied " +
                "by s. This preserves noise density/speed and normal tiling, but the chart phase changes the sampled " +
                "noise and can alter the derived flow result spatially; this is not pixel-exact and must be checked in game. " +
                "Rigid flow is refused because its point-shadow UV linkage is unproven. emissive_flow_alpha is refused " +
                "because its noise scroll is fixed (weighted2 also samples flow_texture from uncompensated UV0). " +
                "Generated copies of vanilla materials are written to " + SharedRepackVanillaMaterialDirectory + ".");
            sb.AppendLine(
                $"Threshold: estimated and actual saving of at least {result.MinimumSavingBytes:N0} bytes. " +
                $"Charts: grid cells of {SharedRepackMinimumCell}+ source pixels, 8-connected; each group compares " +
                "one-cell gutter/current alignment, one-cell gutter/alignment 16, and gutter 8/alignment 8 " +
                "(coarsest-slot pixels, clamped for the group's downshift), taking the smallest pages; " +
                $"merged while padded boxes overlap; a group whose pages get smaller with one rectangle per 8-connected " +
                $"component (tight pieces, same padding and alignment, translated only) uses those instead. Tuples are packed together only inside a layout group (same " +
                $"slots, formats, colour spaces and per-slot size ratios, so nothing is resampled); a group is built " +
                $"only if it saves at least {result.MinimumGroupSavingBytes:N0} bytes, a group whose charts do not " +
                $"fit {result.MaximumSide}x{result.MaximumSide} is split into pages (a part's charts stay on one " +
                $"page), tuples that share a texture are built or left untouched together. Compressed blocks are " +
                $"copied from the sources wherever the chart rectangles are block aligned at that mip level; the " +
                $"first level below that is seeded from the source's own mip. Every check runs before anything is " +
                $"written.");
            if (state.SharedRepackKeptMissingTextures.Count != 0)
            {
                sb.AppendLine(
                    $"Textures already missing in the source material (kept as authored, not an error): " +
                    $"{state.SharedRepackKeptMissingTextures.Count:N0}. The atlas stage skips a mesh whose secondary " +
                    "texture cannot be resolved (UV0 is shared); the repack carries the reference over unchanged, as it " +
                    "was never loadable, and does not fail the run over it.");
                foreach (var (material, texture) in state.SharedRepackKeptMissingTextures.Take(5))
                    sb.AppendLine($"  {material} -> {texture} (already missing in the source material (kept as authored))");
            }

            if (state.DownscaleEarlyCandidates.Count != 0 || state.SharedRepackScaledSourceFailures.Count != 0)
            {
                sb.AppendLine(
                    "Density follows the downscale option: a texture the early downscale analysis chose a scale for " +
                    "is repacked as the downscale would have written it (its source mips from that level, so the " +
                    "block-copy and chart rules hold there), and the estimate, threshold and actual-bytes check use " +
                    $"those sizes. Built from a scaled texture: {result.ScaledSourceTextures.Count:N0} " +
                    $"({string.Join(", ", result.ScaledSourceTextures.GroupBy(pair => pair.Value).OrderByDescending(group => group.Key).Select(group => $"{group.Count():N0} at {group.Key:0.##}x"))}); " +
                    $"scaled version could not be built (full density used): {state.SharedRepackScaledSourceFailures.Count:N0}.");
                foreach (var failure in state.SharedRepackScaledSourceFailures.Take(5))
                    sb.AppendLine("  " + failure);
            }

            sb.AppendLine("Selection funnel (every set ends in exactly one line below):");
            sb.AppendLine($"  Signature sets considered: {result.SetsConsidered:N0}");
            sb.AppendLine($"  Estimated saving below the threshold: {result.BelowThreshold:N0}");
            sb.AppendLine($"  Merged into another set's plan: {result.MergedIntoOther:N0}");
            sb.AppendLine(
                $"  Every rigid not eligible (not owned, overrides vanilla, direct reference, WSModel not owned or " +
                $"overriding, changed by texture atlasing, modified by another stage, unreadable): {result.RigidNotEligible:N0}");
            if (state.TextureAtlasingActive)
            {
                sb.AppendLine(
                    $"Texture atlasing ran first: {result.RigidsChangedByAtlasing.Count:N0} rigid(s) it changed were " +
                    $"left out of the repack (reason \"{SharedRepackReasonAtlasChangedRigid}\"); " +
                    $"{result.AtlasedUsersIgnored:N0} atlased part(s) no longer count as users of the textures " +
                    $"they used (they use the atlas), while a changed rigid's other parts still do and are dropped.");
            }

            if (state.CrossRigidMergesEnabled)
            {
                var sourceRewritten = result.RepackedRigids.Count(path => state.CrossRigidChangedRigids.Contains(path));
                var generatedRewritten = result.RepackedRigids.Count(path => state.CrossRigidGeneratedRigidPaths.Contains(path));
                sb.AppendLine(
                    $"Cross-rigid merging ran first: its source rigids (kept in the pack, the generated models copy " +
                    $"their parts) and the rigids it generated use the same textures, so they are rewritten like any " +
                    $"other user, with the same charts and materials. Source rigids it relied on: " +
                    $"{sourceRewritten:N0} rewritten of {result.CrossRigidSourceRigidsConsidered.Count:N0} in considered sets. " +
                    $"Generated rigids: {generatedRewritten:N0} rewritten of " +
                    $"{result.CrossRigidGeneratedRigidsConsidered.Count:N0} in considered sets (an ineligible " +
                    $"generated rigid drops its parts like any other rigid).");
            }

            sb.AppendLine(
                $"  Dropped by closure, variants, tiling, ineligible rigids or the re-estimate: {result.DroppedByClosure:N0}");
            if (!SharedRepackDirectRigidsDisabled)
            {
                var directBuckets = result.DirectRigidOutcomes
                    .GroupBy(item => item.Bucket, StringComparer.Ordinal)
                    .Select(group => (Name: group.Key, Count: group.Count()))
                    .Concat(result.DirectRigidsNotOwned.Select(pair => (Name: pair.Key, Count: pair.Value)))
                    .OrderBy(item => item.Name, StringComparer.Ordinal)
                    .Select(item => $"{item.Count:N0} {item.Name}")
                    .ToArray();
                sb.AppendLine(
                    $"  Direct rigids considered in a considered signature set: " +
                    $"{result.DirectRigidOutcomes.Count + result.DirectRigidsNotOwned.Values.Sum():N0}; " +
                    $"outcomes: {(directBuckets.Length == 0 ? "none" : string.Join(", ", directBuckets))}");
                sb.AppendLine(
                    $"  Directly loaded rigids rewritten through embedded materials: {result.EmbeddedRewrittenRigids.Count:N0}; " +
                    $"parts dropped for embedded unreadable material: {result.PartDropReasons.GetValueOrDefault(SharedRepackReasonEmbeddedUnreadable):N0}, " +
                    $"embedded material type not verified: {result.PartDropReasons.Where(pair => pair.Key.StartsWith(SharedRepackReasonEmbeddedTypePrefix, StringComparison.Ordinal)).Sum(pair => pair.Value):N0}, " +
                    $"embedded vertex format not verified: {result.PartDropReasons.Where(pair => pair.Key.StartsWith(SharedRepackReasonEmbeddedVertexFormatPrefix, StringComparison.Ordinal)).Sum(pair => pair.Value):N0}, " +
                    $"unsupported embedded slots: {result.PartDropReasons.GetValueOrDefault(SharedRepackReasonEmbeddedUnsupported):N0}, " +
                    $"embedded/WSModel texture mismatch: {result.PartDropReasons.GetValueOrDefault(SharedRepackReasonEmbeddedWsMismatch):N0}");
                if (result.EmbeddedRewrittenPartTypes.Count != 0)
                {
                    sb.AppendLine(
                        "  Directly loaded parts rewritten by embedded material type: " +
                        string.Join(", ", result.EmbeddedRewrittenPartTypes.Values
                            .GroupBy(type => type)
                            .OrderBy(group => group.Key.ToString(), StringComparer.Ordinal)
                            .Select(group => $"{group.Key} {group.Count():N0}")));
                    sb.AppendLine(
                        "  Burn caveat: on directly loaded rigids the burn shaders sample the common t_fire_map at " +
                        "UV0 times shader constants, so burn pattern density follows the page UV scale.");
                }
            }

            sb.AppendLine($"  Not buildable (format, size, charts, layout, decode): {result.NotBuildable:N0}");
            sb.AppendLine($"  Failed the actual-bytes gate (group or set): {result.FailedActualBytes:N0}");
            sb.AppendLine($"  Failed the pixel check (nothing written): {result.FailedPixelCheck:N0}");
            sb.AppendLine($"  Built: {result.Built:N0}");
            sb.AppendLine($"  (tuples excluded by the sizing invariant, expected 0: {result.SizingErrors:N0})");
            var accounted = result.BelowThreshold + result.MergedIntoOther + result.RigidNotEligible +
                            result.DroppedByClosure + result.NotBuildable + result.FailedActualBytes +
                            result.FailedPixelCheck + result.Built;
            sb.AppendLine(
                $"  Check: {accounted:N0} accounted of {result.SetsConsidered:N0} considered " +
                (accounted == result.SetsConsidered ? "(reconciles)" : "(DOES NOT RECONCILE)"));
            AppendSharedRepackEstimateFunnel(sb, result);
            sb.AppendLine(
                $"Merged plans (a set whose material shares textures with unprocessed sets takes them in, up to " +
                $"{SharedRepackMaximumMergedSets} sets, {SharedRepackMaximumMergedRigids} source rigids and " +
                $"{SharedRepackMaximumMergedGeneratedRigids} generated cross-rigid rigids): " +
                $"{result.MergePlansTried:N0} tried, {result.MergePlansBuilt:N0} built, {result.MergePlansFellBack:N0} " +
                $"fell back to the seed set alone; largest {result.LargestMergeSets:N0} set(s) in " +
                $"{result.LargestMergeRigids:N0} rigid(s); {result.MergeCapHits:N0} plan(s) hit the cap " +
                $"({result.MergeGeneratedCapHits:N0} of them the generated-rigid cap).");
            var splits = result.BuiltSets.SelectMany(set => set.Splits).ToList();
            sb.AppendLine(
                $"Page splitting (a layout group whose power-of-two page cannot shrink puts whole clusters of charts " +
                $"on up to {SharedRepackMaximumSplitPages} smaller pages, never duplicating a chart or splitting a mesh part), " +
                $"in built sets: {splits.Count(item => item.Applied):N0} group(s) split, " +
                $"{splits.Sum(item => item.Saved):N0} bytes saved over the single-page result, " +
                $"{splits.Count(item => item.OneCluster):N0} group(s) could not split (one connected cluster).");
            var emissiveBlocked = result.PartDropReasons
                .Where(pair => pair.Key.StartsWith(SharedRepackReasonEmissiveShader, StringComparison.Ordinal) ||
                               pair.Key.StartsWith(SharedRepackReasonEmissiveParameter, StringComparison.Ordinal) ||
                               pair.Key.StartsWith("the emissive distortion texture", StringComparison.Ordinal))
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .ToList();
            var dissolveBlocked = result.PartDropReasons
                .Where(pair => pair.Key.StartsWith(SharedRepackReasonDissolveShader, StringComparison.Ordinal) ||
                               pair.Key.StartsWith(SharedRepackReasonDissolveParameter, StringComparison.Ordinal) ||
                               pair.Key.StartsWith("the dissolve map texture", StringComparison.Ordinal))
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .ToList();
            var flowBlocked = result.PartDropReasons
                .Where(pair => pair.Key.StartsWith(SharedRepackReasonFlowShader, StringComparison.Ordinal) ||
                               pair.Key.StartsWith(SharedRepackReasonFlowParameter, StringComparison.Ordinal) ||
                               pair.Key.StartsWith("the emissive-flow shader", StringComparison.Ordinal) ||
                               pair.Key.StartsWith(SharedRepackReasonFlowTexture, StringComparison.Ordinal))
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .ToList();
            sb.AppendLine(
                "Slots ignored because the material's shader never binds them (built sets): " +
                (result.UnreadSlotMaterials.Count == 0
                    ? "none"
                    : string.Join(", ", result.UnreadSlotMaterials.Select(pair =>
                        $"{pair.Key} in {pair.Value.Count:N0} material(s)")) +
                      $"; {result.UnreadSlotMaterials.Values.SelectMany(items => items).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0} " +
                      "distinct material(s)") +
                ".");
            sb.AppendLine(
                $"Emissive distortion copy: {result.EmissiveParts:N0} part(s) moved with a copied " +
                $"{SharedRepackEmissiveDistortionSlot}; {result.EmissiveCopies:N0} copy texture(s) written " +
                $"({result.EmissiveCopyBytes:N0} bytes, counted as cost, not as replaced texture bytes); " +
                $"{result.EmissiveMaterialsRescaled:N0} material(s) with rescaled emissive parameters; " +
                $"{result.EmissiveAspectGroups:N0} layout group(s) ({result.EmissiveAspectTuples:N0} tuple(s)) with " +
                $"canvases restricted to the reference aspect ratio (single-page cost of the restriction " +
                $"{result.EmissiveAspectExtraBytes:N0} bytes); {result.EmissiveAspectLostGroups:N0} group(s) " +
                $"({result.EmissiveAspectLostTuples:N0} tuple(s)) left untouched only because of the restriction; " +
                $"parts still blocked by the emissive reasons below (a part counts once per set): " +
                $"{emissiveBlocked.Sum(pair => pair.Value):N0}.");
            foreach (var (reason, count) in emissiveBlocked)
                sb.AppendLine($"  {count:N0} x {reason}");
            sb.AppendLine(
                $"Dissolve: {result.DissolveParts:N0} part(s) moved with a copied {SharedRepackDissolveMapSlot}; " +
                $"{result.DissolveCopies:N0} copy texture(s) written ({result.DissolveCopyBytes:N0} bytes, counted as cost, " +
                $"not as replaced texture bytes); {result.DissolveMaterialsRescaled:N0} material(s) with a rescaled " +
                $"{SharedRepackDissolveScaleParameter}; parts still blocked by the dissolve reasons below (a part " +
                $"counts once per set): {dissolveBlocked.Sum(pair => pair.Value):N0}.");
            foreach (var (reason, count) in dissolveBlocked)
                sb.AppendLine($"  {count:N0} x {reason}");
            sb.AppendLine(
                $"Flow overlay: {result.FlowCompensationParts:N0} part(s) with verified flow compensation; " +
                $"{result.FlowMaterialsRescaled:N0} material(s) rescaled; {result.FlowMapAtlases:N0} model flow-map atlas " +
                $"output(s) ({result.FlowMapAtlasBytes:N0} bytes); {result.FlowMapCopies:N0} common flow-map copy/copies " +
                $"({result.FlowMapCopyBytes:N0} bytes); {result.FlowGuidCopies:N0} GUID noise copy/copies " +
                $"({result.FlowGuidCopyBytes:N0} bytes); {result.FlowAspectGroups:N0} equal-axis group(s), " +
                $"{result.FlowAspectTuples:N0} tuple(s), {result.FlowAspectExtraBytes:N0} bytes single-page restriction cost; " +
                $"{result.FlowAspectLostGroups:N0} group(s) ({result.FlowAspectLostTuples:N0} tuple(s)) lost only due to the " +
                $"restriction; {flowBlocked.Sum(pair => pair.Value):N0} part-drop reason(s) specific to flow.");
            foreach (var (reason, count) in flowBlocked)
                sb.AppendLine($"  {count:N0} x {reason}");
            var copyWon = result.BuiltSets.Where(set => set.CopyWon).ToList();
            sb.AppendLine(
                "Copies of textures that left a plan: a texture that left a set only because a dropped part still needs " +
                "the original is cropped into the pages for the parts that stay (the original stays and is not " +
                "replaced); a set with such a texture is planned both ways and the plan with the larger exact saving " +
                (SharedRepackCopyRemovedDisabled ? "is built (disabled in this run). " : "is built. ") +
                $"{result.Outcomes.Count(outcome => outcome.Context?.PlannedBothWays == true):N0} set(s) planned both ways; " +
                $"copies won in {copyWon.Count:N0} (gained {copyWon.Sum(set => set.CopyGain):N0} bytes over the plan " +
                $"without copies, {copyWon.Sum(set => set.RemovedCopies.Count):N0} copy texture(s) of " +
                $"{copyWon.Sum(set => set.RemovedCopies.Sum(item => item.Bytes)):N0} bytes); " +
                $"{copyWon.Count(set => set.NormalSaving == null):N0} set(s) built only thanks to copies.");
            foreach (var outcome in result.Outcomes.Where(outcome => outcome.Context is { PlannedBothWays: true, CopyWon: false }))
            {
                var context = outcome.Context!;
                sb.AppendLine(
                    $"  planned with copies, not built with them: {Shorten(outcome.Label, 100)} [{outcome.Bucket}]: " +
                    (context.NormalSaving == null ? "without copies it failed" : $"without copies it saves {FormatSignedBytes(context.NormalSaving.Value)}") +
                    (context.CopyFailure == null
                        ? $", with copies {FormatSignedBytes(context.CopySaving)}"
                        : $", the copy plan failed: {Shorten(context.CopyFailure, 160)}"));
            }
            if (result.PartDropReasons.Count != 0)
            {
                sb.AppendLine("Parts dropped from a set while closing it (a part counts once per set):");
                foreach (var (reason, count) in result.PartDropReasons
                             .OrderByDescending(pair => pair.Value)
                             .ThenBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    sb.AppendLine($"  {count:N0} x {reason}");
                }
            }

            if (result.ClosureNotes.Count != 0)
            {
                sb.AppendLine(
                    "Why parts left a set (an ineligible rigid, or the texture that pulled a material outside it " +
                    "and who else uses it):");
                foreach (var group in result.ClosureNotes.GroupBy(note => note.Label))
                {
                    sb.AppendLine($"  {group.Key}");
                    foreach (var note in group.Take(6))
                        sb.AppendLine($"    {note.Note}");
                }
            }

            if (result.DirectRigidOutcomes.Count != 0 || result.DirectRigidsNotOwned.Count != 0)
            {
                sb.AppendLine(
                    "Directly loaded rigid outcomes (source-owned rigids whose embedded modern textures enter a considered signature set):");
                foreach (var direct in result.DirectRigidOutcomes.OrderBy(item => item.Rigid, StringComparer.OrdinalIgnoreCase))
                {
                    var setsText = direct.Bucket == "not rewritten" ? string.Empty : $"sets: {string.Join(" | ", direct.Sets)}; ";
                    sb.AppendLine(
                        $"  [{direct.Bucket}; loaded directly by {string.Join(", ", direct.Consumers)}] " +
                        $"{direct.Rigid}; {setsText}{direct.Detail}");
                }

                foreach (var (reason, count) in result.DirectRigidsNotOwned.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    sb.AppendLine($"  {count:N0} {reason} (not listed)");
            }

            long before = result.BuiltSets.Sum(set => set.Before);
            long after = result.BuiltSets.Sum(set => set.After);
            sb.AppendLine(
                $"Built sets: {result.Built:N0}; replaced texture bytes {before:N0} -> new {after:N0} " +
                $"(actual saving {FormatSignedBytes(before - after)} bytes); {result.GeneratedTextures.Count:N0} " +
                $"texture(s) and {result.GeneratedMaterials.Count:N0} material(s) generated; " +
                $"{result.RepackedRigids.Count:N0} rigid(s) rewritten. Of those, " +
                $"{result.RepackedRigids.Count(path => state.CrossRigidGeneratedRigidPaths.Contains(path)):N0} are generated " +
                $"cross-rigid models and {result.RepackedRigids.Count(path => state.CrossRigidChangedRigids.Contains(path)):N0} " +
                $"are source rigids cross-rigid merging relied on.");
            if (result.DeduplicatedOutputs != 0)
            {
                sb.AppendLine(
                    $"Deduplicated {result.DeduplicatedOutputs:N0} page(s), {result.DeduplicatedBytes:N0} bytes " +
                    "(identical pages of layout groups on the same geometry, written once).");
            }

            if (result.VariantOutputs != 0)
            {
                sb.AppendLine(
                    $"Variant pages: {result.VariantOutputs:N0} output(s), {result.VariantBytes:N0} bytes, " +
                    $"{result.VariantParts:N0} part(s) with texture variants, {result.VariantSamples:N0} pixel-checked " +
                    "top-level sample(s) of variant pages.");
            }

            foreach (var set in result.BuiltSets)
            {
                sb.AppendLine($"  Set {set.Label}");
                var setGenerated = set.Rigids.Where(item => state.CrossRigidGeneratedRigidPaths.Contains(item.Rigid)).ToList();
                sb.AppendLine(
                    $"    {set.Rigids.Count:N0} rigid(s) rewritten: " +
                    string.Join(", ", set.Rigids.Where(item => !state.CrossRigidGeneratedRigidPaths.Contains(item.Rigid))
                        .Select(item => $"{item.Rigid} ({item.Parts:N0} part(s))")) +
                    (setGenerated.Count == 0
                        ? string.Empty
                        : $"{(setGenerated.Count == set.Rigids.Count ? string.Empty : "; ")}" +
                          $"{setGenerated.Count:N0} generated cross-rigid rigid(s) ({setGenerated.Sum(item => item.Parts):N0} part(s))"));
                foreach (var embedded in set.EmbeddedRigids.OrderBy(item => item.Rigid, StringComparer.Ordinal))
                {
                    sb.AppendLine(
                        $"    [embedded material, loaded directly by {string.Join(", ", embedded.Consumers)}] " +
                        $"{embedded.Rigid} ({embedded.Parts:N0} part(s))");
                    foreach (var unusedWs in embedded.UnusedWsModels)
                        sb.AppendLine($"    [unused WSModel left as authored] {unusedWs} -> {embedded.Rigid}");
                }
                var dropLabel = set.Label.EndsWith(" [merged plan]", StringComparison.Ordinal)
                    ? set.Label[..^" [merged plan]".Length]
                    : set.Label;
                foreach (var drop in result.DirectEmbeddedDrops.Where(item => item.Label == dropLabel))
                    sb.AppendLine($"    [embedded material drop] {drop.Detail}");
                if (set.MergedSets.Count != 0)
                {
                    sb.AppendLine($"    merged signature sets ({set.MergedSets.Count:N0} joined the seed):");
                    foreach (var (mergedLabel, textures, units) in set.MergedSets)
                        sb.AppendLine($"      {mergedLabel}: {textures:N0} texture(s), {units:N0} unit(s)");
                }

                if (set.FallbackNote != null)
                    sb.AppendLine($"    {set.FallbackNote}");
                sb.AppendLine(
                    $"    units in signature {set.Units:N0}, lord/hero: " +
                    $"{(set.HasCharacter == null ? "unknown" : set.HasCharacter.Value ? "yes" : "no")}; " +
                    $"{set.Parts:N0} part(s) (all LODs), {set.Tuples:N0} texture tuple(s), {set.Charts:N0} chart(s), " +
                    $"layout {set.LayoutWidth}x{set.LayoutHeight}");
                sb.AppendLine(
                    $"    bytes (actual) {set.Before:N0} -> {set.After:N0}, saves {FormatSignedBytes(set.Before - set.After)} " +
                    $"(estimate said {FormatSignedBytes(set.EstimatedSaving)}); {set.Pages:N0} page(s)");
                if (set.DeduplicatedOutputs != 0)
                {
                    sb.AppendLine(
                        $"    deduplicated {set.DeduplicatedOutputs:N0} page(s), {set.DeduplicatedBytes:N0} bytes " +
                        "(identical to a page of another layout group of the set; written once)");
                }

                if (set.VariantOutputs != 0)
                {
                    sb.AppendLine(
                        $"    variant pages: {set.VariantOutputs:N0} output(s), {set.VariantBytes:N0} bytes, " +
                        $"parts with variants {set.VariantParts:N0}; {set.VariantSamples:N0} pixel-checked sample(s)");
                }

                foreach (var group in set.Groups)
                {
                    sb.AppendLine(
                        $"    layout group {group.Label}: {group.Tuples:N0} tuple(s), {group.Charts:N0} chart(s), " +
                        $"{group.Pages:N0} page(s), {group.Before:N0} -> {group.After:N0} bytes, {group.Status}");
                    if (group.Sizes.Length != 0)
                        sb.AppendLine($"      sizes: {group.Sizes}");
                }
                if (set.UnreadSlotMaterials.Count != 0)
                {
                    sb.AppendLine(
                        "    ignored as unread by the material's shader: " +
                        string.Join(", ", set.UnreadSlotMaterials.Select(pair =>
                            $"{pair.Key} ({pair.Value.Count:N0} material(s))")));
                }

                foreach (var blood in set.BloodScales)
                    sb.AppendLine($"    blood: {blood}");
                if (set.EmissiveParts != 0 || set.EmissiveCopies != 0)
                {
                    sb.AppendLine(
                        $"    emissive: {set.EmissiveParts:N0} part(s) with a copied distortion slot, " +
                        $"{set.EmissiveCopies:N0} copy texture(s) of {set.EmissiveCopyBytes:N0} bytes");
                }

                foreach (var emissive in set.EmissiveScales)
                    sb.AppendLine($"    emissive: {emissive}");
                if (set.DissolveParts != 0 || set.DissolveCopies != 0)
                {
                    sb.AppendLine(
                        $"    dissolve: {set.DissolveParts:N0} part(s) with a copied map slot, " +
                        $"{set.DissolveCopies:N0} copy texture(s) of {set.DissolveCopyBytes:N0} bytes");
                }

                foreach (var dissolve in set.DissolveScales)
                    sb.AppendLine($"    dissolve: {dissolve}");
                if (set.FlowCompensationParts != 0 || set.FlowMapAtlases != 0 || set.FlowMapCopies != 0 || set.FlowGuidCopies != 0)
                {
                    sb.AppendLine(
                        $"    flow: {set.FlowCompensationParts:N0} part(s) compensated; " +
                        $"{set.FlowMapAtlases:N0} model flow-map atlas output(s) ({set.FlowMapAtlasBytes:N0} bytes), " +
                        $"{set.FlowMapCopies:N0} common flow-map copy/copies ({set.FlowMapCopyBytes:N0} bytes), " +
                        $"{set.FlowGuidCopies:N0} GUID noise copy/copies ({set.FlowGuidCopyBytes:N0} bytes)");
                }
                foreach (var flow in set.FlowScales)
                    sb.AppendLine($"    flow: {flow}");
                if (set.RemovedCopies.Count != 0)
                {
                    sb.AppendLine(
                        $"    [copies: {set.RemovedCopies.Count:N0} texture(s), {set.RemovedCopies.Sum(item => item.Bytes):N0} bytes; " +
                        (set.NormalSaving == null
                            ? $"built only thanks to the copies, saves {FormatSignedBytes(set.CopyGain)}"
                            : $"saves {FormatSignedBytes(set.CopyGain)} more than without copies") + "]");
                    foreach (var (texture, slot, why, bytes) in set.RemovedCopies.OrderBy(item => item.Texture, StringComparer.Ordinal))
                    {
                        sb.AppendLine(
                            $"      copy of {texture} ({slot}, {bytes:N0} bytes): the original stays, it left the plan when {why}");
                    }
                }
                sb.AppendLine(
                    $"    pixel check (before writing): top level {set.Samples:N0} sample(s), max error {set.MaxError:0}, " +
                    $"mean {set.MeanError:0.00}; mip 2/3 (source's own level) {set.LevelSamples:N0} sample(s), max " +
                    $"{set.LevelMaxError:0} (not gated), 99th percentile {set.LevelP99Error:0}, mean {set.LevelMeanError:0.00}, " +
                    $"{set.LevelOutlierShare:P2} of re-encoded samples over {SharedRepackLevelOutlierError:0} " +
                    $"(fail above {SharedRepackLevelOutlierShare:P1}) (texels within 1 of a chart edge are " +
                    $"not judged: {set.EdgeSkippedSamples:N0}); {set.SkippedSamples:N0} point(s) skipped " +
                    "(no position off texel edges)" +
                    (set.Pieces == 0
                        ? string.Empty
                        : $"; tight pieces: {set.PiecesSampled:N0} of {set.Pieces:N0} piece(s) sampled"));
                foreach (var sample in set.Worst)
                    sb.AppendLine($"      worst: {sample}");
                sb.AppendLine($"    replaced {set.Replaced.Count:N0} texture(s): " +
                              string.Join(", ", set.Replaced.Take(6).Select(item => item.Path)) +
                              (set.Replaced.Count > 6 ? ", ..." : string.Empty));
                foreach (var output in set.Outputs)
                {
                    sb.AppendLine(
                        $"    -> {output.Path}: {output.Width}x{output.Height} {output.Format}" +
                        $"{(output.Srgb ? "_SRGB" : string.Empty)}, {output.Bytes:N0} bytes with mips, " +
                        $"{output.BlockCopiedLevels} of {output.MipLevels} mip level(s) block-copied" +
                        (output.IsCopy
                            ? IsSharedRepackNoiseSlot(output.Slot) ||
                              output.Slot.Equals(SharedRepackFlowMapSlot, StringComparison.OrdinalIgnoreCase)
                                ? $" (copy of {string.Join(", ", output.Sources)}; original stays for other users, not a replacement)"
                                : $" (copy of {string.Join(", ", output.Sources)}, which stays for the part(s) dropped from the plan; not a replacement)"
                            : string.Empty));
                    if (output.BlockStats is { } blockStats)
                    {
                        sb.AppendLine(
                            $"      blocks: copied {blockStats.Copied:N0} of {blockStats.Total:N0} " +
                            $"({(blockStats.Total == 0 ? 0 : 100.0 * blockStats.Copied / blockStats.Total):0.00} %), " +
                            $"encoded {blockStats.Encoded:N0} ({blockStats.DescribeCauses()}); copied per level: {blockStats.DescribeLevels()}");
                    }

                    if (output.MipSeedStats is { Total: > 0 } seedStats)
                        sb.AppendLine($"      encoder input of lower levels: {seedStats.Describe()}");
                }
            }

            var allBlockStats = result.BuiltSets
                .SelectMany(set => set.Outputs)
                .Select(output => output.BlockStats)
                .OfType<SharedRepackBlockStats>()
                .ToList();
            if (allBlockStats.Count != 0)
            {
                var combined = new SharedRepackBlockStats(allBlockStats.Max(item => item.Levels));
                foreach (var item in allBlockStats)
                    combined.Add(item);
                sb.AppendLine(
                    $"Shared repack: block-copied {combined.Copied:N0} of {combined.Total:N0} blocks " +
                    $"({(combined.Total == 0 ? 0 : 100.0 * combined.Copied / combined.Total):0.00} %), encoded {combined.Encoded:N0} " +
                    $"({combined.DescribeCauses()}) over {allBlockStats.Count:N0} sheet(s).");
            }

            var allSeedStats = result.BuiltSets
                .SelectMany(set => set.Outputs)
                .Select(output => output.MipSeedStats)
                .OfType<SharedRepackMipSeedStats>()
                .Where(item => item.Total > 0)
                .ToList();
            if (allSeedStats.Count != 0)
            {
                var combinedSeeds = new SharedRepackMipSeedStats();
                foreach (var item in allSeedStats)
                    combinedSeeds.Add(item);
                sb.AppendLine($"Shared repack: encoder input of the lower mip levels, {combinedSeeds.Describe()} over {allSeedStats.Count:N0} sheet(s).");
            }

            if (result.Samples != 0)
            {
                sb.AppendLine(
                    $"Pixel equivalence over all sets: {result.Samples:N0} sample(s), max error {result.MaxError:0}, " +
                    $"mean {(result.ErrorCount == 0 ? 0 : result.ErrorSum / result.ErrorCount):0.00} " +
                    $"(fail above {SharedRepackSampleMaximumError:0} max or {SharedRepackSampleMeanLimit:0} mean).");
            }

            if (result.Finalized)
            {
                sb.AppendLine(
                    $"Owned source textures replaced: {result.OwnedReplaced:N0}; removed by the existing " +
                    $"unused-asset pruning {result.OwnedPruned.Count:N0}; still in the output " +
                    $"{result.OwnedStillPresent.Count:N0} (the pruning found another reference, or they were not " +
                    "reachable from a VMD root; nothing is deleted by the repack itself).");
                foreach (var path in result.OwnedStillPresent.Take(10))
                    sb.AppendLine($"  still present: {path}");
            }
            else
            {
                sb.AppendLine("Owned source textures: not deleted by the repack; pruning has not run yet.");
            }

            sb.AppendLine($"Skipped sets ({result.Skips.Count:N0}), each with its reason:");
            foreach (var (label, reason) in result.Skips)
                sb.AppendLine($"  {label}: {reason}");
            if (result.SizingLog.Count != 0)
            {
                sb.AppendLine("Sizing invariant violations (a planner fault; each tuple was left untouched):");
                foreach (var (label, detail) in result.SizingLog)
                    sb.AppendLine($"  {label}: {detail}");
            }

            foreach (var (label, detail) in result.PixelFailures)
            {
                sb.AppendLine($"Pixel check failure for {label}:");
                sb.AppendLine($"    {detail}");
            }
            if (result.ValidationErrors.Count != 0)
            {
                sb.AppendLine("Validation errors:");
                foreach (var error in result.ValidationErrors)
                    sb.AppendLine($"  {error}");
            }

            sb.AppendLine(
                "Not covered by this increment: material variants that differ in a copied slot, a slot only some " +
                "bindings have, a texture of another size or format, or a directly loaded rigid, tiled UVs, " +
                "rigids used by more than one set of units outside the pack, merging sets that no material connects, " +
                "deleting the old textures.");
            sb.AppendLine();
        }
    }
}
