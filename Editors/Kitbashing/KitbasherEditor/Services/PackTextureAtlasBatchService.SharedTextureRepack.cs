using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using DirectXTexNet;
using Shared.GameFormats.RigidModel;

namespace Editors.KitbasherEditor.Services
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
        //  * All charts of all tuples go into one shared layout (deterministic shelf packer,
        //    power-of-two canvas). Every slot's output is that layout scaled by the slot's
        //    finest source resolution, so no tuple loses pixels; coarser slots are
        //    replicated up (nearest, exact copies) and the overhead is reported.
        private static readonly TimeSpan SharedRepackAnalysisBudget = TimeSpan.FromMinutes(10);
        // A set is only built when it saves at least this much, estimated AND actual.
        private const long SharedRepackMinimumSavingBytes = 2L * 1024 * 1024;
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
        // A merged plan stops growing above this many signature sets or rigids.
        private const int SharedRepackMaximumMergedSets = 8;
        private const int SharedRepackMaximumMergedRigids = 16;
        // Splitting a layout group over smaller pages: at most this many pages, this many
        // first-page sizes tried per level and this many greedy fills per group.
        private const int SharedRepackMaximumSplitPages = 4;
        private const int SharedRepackSplitCandidatesPerLevel = 12;
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

        private const string SharedRepackReasonMaterialUnreadable =
            "a bound material is missing or unreadable";
        private const string SharedRepackReasonMaterialOverridesVanilla =
            "a bound material is a source-pack override of a vanilla material";
        private const string SharedRepackReasonSlot =
            "a bound material has a texture in the non-atlasable slot ";
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
        private const string SharedRepackReasonVariants =
            "the part's WSModel bindings resolve to different textures";
        private const string SharedRepackReasonMixed =
            "a bound material also uses textures outside the set";
        private const string SharedRepackReasonBlockedTexture =
            "a texture cannot be repacked (not BC1/BC3, not a power of two, or an owned override of a vanilla texture)";
        private const string SharedRepackReasonAtlasChangedRigid =
            "the rigid was changed by texture atlasing";
        private const string SharedRepackReasonCrossRigidChangedRigid =
            "the rigid was changed by cross-rigid merging";
        private const string SharedRepackReasonMergedUser =
            "a rigid generated by cross-rigid merging also uses the texture";
        private const string SharedRepackReasonRigid =
            "the part's rigid is not eligible: ";
        private const string SharedRepackReasonNoCoverage =
            "the part has no coverage data";
        private const string SharedRepackReasonBadPart =
            "the part is missing from the rigid or shares its model with another part";

        private sealed class SharedRepackPart
        {
            public MeshKey Key { get; init; }
            public RmvModel? Model { get; set; }
            public List<SourceWsModelBinding> Bindings { get; } = [];
            public HashSet<string> Materials { get; } = new(StringComparer.OrdinalIgnoreCase);
            // Every texture of every bound material, any slot.
            public HashSet<string> Textures { get; } = new(StringComparer.OrdinalIgnoreCase);
            public SortedDictionary<string, string> Slots { get; } = new(StringComparer.OrdinalIgnoreCase);
            public string? Reason { get; set; }
            public bool GeometryChecked { get; set; }
            public double[] U { get; set; } = [];
            public double[] V { get; set; } = [];
            public int[] TileU { get; set; } = [];
            public int[] TileV { get; set; } = [];
            public bool[] Used { get; set; } = [];
            public int[] VertexChart { get; set; } = [];
            public string TupleKey => string.Join("|", Slots.Select(slot => $"{slot.Key}={Normalize(slot.Value)}"));
        }

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
            public string Label => $"{Tuple.Index}.{Id}";
        }

        private sealed class SharedRepackTuple
        {
            public int Index { get; set; }
            public string Key { get; init; } = string.Empty;
            // The slots that are repacked (spatial). Uniform tiny textures are not in here.
            public SortedDictionary<string, string> Slots { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            // Uniform tiny textures (e.g. a 4x4 mask): the generated material keeps them as authored.
            public SortedDictionary<string, string> ConstantSlots { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<SharedRepackPart> Parts { get; } = [];
            public Dictionary<string, DdsLayout> Layouts { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, (int X, int Y)> SourceExponent { get; } = new(StringComparer.OrdinalIgnoreCase);
            public int ReferenceWidth { get; set; }
            public int ReferenceHeight { get; set; }
            public int Cell { get; set; }
            // Chart rectangle alignment in reference pixels (at least the cell).
            public int Align { get; set; }
            public int GridX { get; set; }
            public int GridY { get; set; }
            public int[] CellChart { get; set; } = [];
            public List<SharedRepackChart> Charts { get; } = [];
            // Tuples with the same signature can share one layout: same slots, formats,
            // colour spaces and per-slot size ratios, so no slot is ever resampled.
            public string GroupKey { get; set; } = string.Empty;
            // Why this tuple is left untouched (null = included).
            public string? Excluded { get; set; }
        }

        private sealed class SharedRepackSlotOutput
        {
            public string Slot { get; init; } = string.Empty;
            public string Path { get; init; } = string.Empty;
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
            public DdsLayout Template { get; set; } = null!;
            // The source textures this output replaces (all of one signature set).
            public List<string> Sources { get; } = [];
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
            public int LayoutWidth { get; set; }
            public int LayoutHeight { get; set; }
            public long BytesAfter { get; set; }
            public Dictionary<string, string> MaterialMap { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, XmlDocument> MaterialDocuments { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<(XmlNode Node, string Material)> WsRepoints { get; } = [];
            public HashSet<string> WsModels { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<string> BloodScales { get; } = [];
        }

        private sealed class SharedRepackGroupInfo
        {
            public string Label { get; init; } = string.Empty;
            public int Tuples { get; set; }
            public int Charts { get; set; }
            public int Pages { get; set; }
            public long Before { get; set; }
            public long After { get; set; }
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
        }

        private sealed class SharedRepackSetPlan
        {
            public Dictionary<string, SharedRepackSplitInfo> Splits { get; } = new(StringComparer.Ordinal);
            public List<SharedRepackPage> Pages { get; } = [];
            public List<SharedRepackGroupInfo> Groups { get; } = [];
            public List<SharedRepackTuple> Tuples { get; } = [];
            public Dictionary<string, long> Replaced { get; } = new(StringComparer.OrdinalIgnoreCase);
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
            public long Skipped { get; set; }
            public double TopMax { get; set; }
            public double TopSum { get; set; }
            public long TopCount { get; set; }
            public double LevelMax { get; set; }
            public double LevelSum { get; set; }
            public long LevelCount { get; set; }
            public long LevelSamples { get; set; }
            public long EdgeSkipped { get; set; }
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
            // materials: users the pre-pass sets do not contain.
            public List<(string Rigid, int Lod, int Part, HashSet<string> Textures)> MergedUsers { get; init; } = [];
            public List<SharedRepackSetRigid> Rigids { get; } = [];
            public List<UnitRepackSetDecision> Component { get; } = [];
            public List<int> Joined { get; } = [];
            public bool CapHit { get; set; }
        }

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
            public string Label { get; init; } = string.Empty;
            public int Units { get; init; }
            public bool? HasCharacter { get; init; }
            public long EstimatedSaving { get; init; }
            public List<(string Path, bool Owned, long Bytes)> Replaced { get; } = [];
            public long Before { get; set; }
            public long After { get; set; }
            public List<SharedRepackSlotOutput> Outputs { get; } = [];
            public List<SharedRepackGroupInfo> Groups { get; } = [];
            public int Tuples { get; set; }
            public int Charts { get; set; }
            public int Parts { get; set; }
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
            public int LayoutWidth { get; set; }
            public int LayoutHeight { get; set; }
            public List<string> Materials { get; } = [];
            public List<string> BloodScales { get; } = [];
            public List<SharedRepackSample> Worst { get; } = [];
        }

        private sealed class SharedTextureRepackResult
        {
            public long MinimumSavingBytes { get; set; } = SharedRepackMinimumSavingBytes;
            public long MinimumGroupSavingBytes { get; set; } = SharedRepackMinimumGroupSavingBytes;
            public int MaximumSide { get; set; } = SharedRepackMaximumSide;
            public TimeSpan Elapsed { get; set; }
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
            // Rigids left out because the cross-rigid writer built generated models from them,
            // and the sets (labels) whose textures a generated cross-rigid rigid still uses.
            // Source textures built at their early downscale scale (the repack's density follows
            // the downscale option), with the scale.
            public Dictionary<string, double> ScaledSourceTextures { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> RigidsChangedByCrossRigid { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> SetsBlockedByMergedUsers { get; } = new(StringComparer.Ordinal);
            public int MergedIntoOther { get; set; }
            public int DroppedByClosure { get; set; }
            public int NotBuildable { get; set; }
            public int FailedActualBytes { get; set; }
            public int FailedPixelCheck { get; set; }
            // Tuples excluded because a planned size broke the sizing invariant (should stay 0).
            public int SizingErrors { get; set; }
            // Merged plans: attempts with at least one joined set, how they ended, the largest
            // one tried and the plans that hit the cap.
            public int MergePlansTried { get; set; }
            public int MergePlansBuilt { get; set; }
            public int MergePlansFellBack { get; set; }
            public int MergeCapHits { get; set; }
            public int LargestMergeSets { get; set; }
            public int LargestMergeRigids { get; set; }
            public int Built => BuiltSets.Count;

            public List<(string Label, string Reason)> Skips { get; } = [];
            public Dictionary<string, int> PartDropReasons { get; } = new(StringComparer.Ordinal);
            // Per set: which texture pulled a part's material outside the set, and who else uses it.
            public List<(string Label, string Note)> ClosureNotes { get; } = [];
            public List<(string Label, string Detail)> PixelFailures { get; } = [];
            public List<(string Label, string Detail)> SizingLog { get; } = [];
            public List<SharedRepackBuiltSet> BuiltSets { get; } = [];
            public List<string> ValidationErrors { get; } = [];
            public HashSet<string> RepackedRigids { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<string> GeneratedTextures { get; } = [];
            public List<string> GeneratedMaterials { get; } = [];
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

        // The option is ignored (not an error) in cross-rigid-only mode, which promises never to
        // write textures. Texture atlasing and cross-rigid merges are allowed: they run first, the
        // rigids they changed are left out of the repack (AtlasChangedRigids,
        // CrossRigidChangedRigids) and the rigids the cross-rigid writer generated block the sets
        // whose textures they still use.
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
                for (var x = firstCell; x <= lastCell; x++)
                    grid[row * gx + x] = true;
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

        // Deterministic shelf packing in the given order (tallest first). Sizes and the
        // canvas are multiples of the alignment, so every position is too.
        private static bool TryShelfPack(
            IReadOnlyList<(int Width, int Height)> sizes,
            int[] order,
            int canvasWidth,
            int canvasHeight,
            out (int X, int Y)[] positions)
        {
            positions = new (int X, int Y)[sizes.Count];
            var x = 0;
            var y = 0;
            var rowHeight = 0;
            foreach (var index in order)
            {
                var (width, height) = sizes[index];
                if (width > canvasWidth || height > canvasHeight)
                    return false;

                if (x + width > canvasWidth)
                {
                    x = 0;
                    y += rowHeight;
                    rowHeight = 0;
                }

                if (y + height > canvasHeight)
                    return false;

                positions[index] = (x, y);
                x += width;
                rowHeight = Math.Max(rowHeight, height);
            }

            return true;
        }

        // Smallest power-of-two canvas (by area, then squarest, then wider) the shelf packer
        // fits all rectangles into, within the given limits.
        private static bool TryPackSharedRepackLayout(
            IReadOnlyList<(int Width, int Height)> sizes,
            int maximumWidth,
            int maximumHeight,
            out int canvasWidth,
            out int canvasHeight,
            out (int X, int Y)[] positions)
        {
            canvasWidth = canvasHeight = 0;
            positions = [];
            if (sizes.Count == 0)
                return false;

            var order = Enumerable.Range(0, sizes.Count)
                .OrderByDescending(index => sizes[index].Height)
                .ThenByDescending(index => sizes[index].Width)
                .ThenBy(index => index)
                .ToArray();
            long area = sizes.Sum(size => (long)size.Width * size.Height);
            var minimumWidth = NextPowerOfTwo(sizes.Max(size => size.Width));
            var minimumHeight = NextPowerOfTwo(sizes.Max(size => size.Height));
            var candidates = new List<(int Width, int Height)>();
            for (var width = minimumWidth; width <= maximumWidth; width <<= 1)
            {
                for (var height = minimumHeight; height <= maximumHeight; height <<= 1)
                {
                    if ((long)width * height >= area)
                        candidates.Add((width, height));
                }
            }

            foreach (var candidate in candidates
                         .OrderBy(item => (long)item.Width * item.Height)
                         .ThenBy(item => Math.Abs(Log2Exact(item.Width) - Log2Exact(item.Height)))
                         .ThenByDescending(item => item.Width))
            {
                if (TryShelfPack(sizes, order, candidate.Width, candidate.Height, out positions))
                {
                    canvasWidth = candidate.Width;
                    canvasHeight = candidate.Height;
                    return true;
                }
            }

            return false;
        }

        // First failing property of a texture the repack could not copy exactly, else null.
        private static string? GetSharedRepackBlockedTextureReason(DdsLayout? layout)
        {
            if (layout == null)
                return "header unreadable";
            if (layout.Format is not ("BC1" or "BC3"))
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

        // BGRA canvas -> BC1/BC3 DDS with a complete mip chain.
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
                        state.Source, GetGameplayTraversalContainers(state), path)?
                    .FindFile(path)?.DataSource.ReadData();
                if (bytes != null &&
                    TryReadDdsLayout(
                        bytes.AsSpan(0, Math.Min(bytes.Length, TextureDownscaleHeaderPeekBytes)).ToArray(),
                        out var layout))
                {
                    byte[] rewritten;
                    string error;
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

        private static bool OverridesVanillaPath(BatchState state, string path)
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

            return false;
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

            if (!TryCollectCrossRigidMergedUsers(state, out var mergedUsers, out var mergedUsersFailure))
            {
                result.UnavailableReason =
                    $"the textures of the rigids generated by cross-rigid merging are unknown ({mergedUsersFailure})";
                return;
            }

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
            foreach (var binding in coverage.Bindings)
            {
                if (binding.Kind == SourceWsModelBindingKind.UnreadableWsModel || binding.Geometry.Length == 0)
                    continue;

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
                var reason = GetRigidNotOnlyThroughOwnedWsModelsReason(
                    state,
                    rigidPath,
                    wsByRigid.GetValueOrDefault(rigidPath) ?? [],
                    rigidReferrers,
                    () => state.AtlasChangedRigids.Contains(rigidPath)
                        ? SharedRepackReasonAtlasChangedRigid
                        : state.CrossRigidChangedRigids.Contains(rigidPath)
                        ? SharedRepackReasonCrossRigidChangedRigid
                        : state.ModifiedRigids.Contains(rigidPath) && !result.RepackedRigids.Contains(rigidPath)
                        ? "the rigid was already modified by another stage"
                        : state.CrossRigidGeneratedRigidPaths.Contains(rigidPath)
                            ? "the rigid is a generated cross-rigid model"
                            : null);

                if (reason == null)
                {
                    try
                    {
                        rigid = loadRigid(rigidPath);
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
                    partBindings.GetValueOrDefault(key) ?? []);
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
                    else if (rigidReason == SharedRepackReasonCrossRigidChangedRigid)
                        result.RigidsChangedByCrossRigid.Add(rigidPath);
                    item = new SharedRepackSetRigid
                    {
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
                    continue;
                }

                // 1. Estimate.
                if (decision.Saving <= 0 || decision.Saving < result.MinimumSavingBytes)
                {
                    result.BelowThreshold++;
                    Skip(label, $"estimated saving {FormatSignedBytes(decision.Saving)} bytes is below " +
                                $"the {result.MinimumSavingBytes:N0}-byte threshold");
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
                    continue;
                }

                var rigids = rigidPaths.Select(SetRigid).ToList();
                if (rigids.All(item => item.Rigid == null))
                {
                    result.RigidNotEligible++;
                    Skip(label, DescribeSharedRepackIneligibleRigids(rigids));
                    continue;
                }

                // 3. Plan, compose and check in memory, then write and rewrite. The first attempt
                // may merge unprocessed signature sets that share a material with this one; if
                // a merged attempt fails anywhere, nothing of it was written and the seed is
                // retried alone, with the joined sets released.
                SharedRepackMergeContext NewContext(bool allowMerge)
                {
                    var context = new SharedRepackMergeContext
                    {
                        Seed = decisionIndex,
                        Allow = allowMerge,
                        Decisions = decisions,
                        IndexOfSet = indexOfSet,
                        IsAvailable = index => index > decisionIndex && !consumed.ContainsKey(index),
                        MakeRigid = SetRigid,
                        MergedUsers = mergedUsers,
                        Effective = entry => GetSharedRepackEffectiveEntry(state, entry),
                    };
                    context.Rigids.AddRange(rigids);
                    return context;
                }

                (SharedRepackBuiltSet? Built, string? Failure, string Bucket, SharedRepackMergeContext Context) Attempt(
                    bool allowMerge)
                {
                    var context = NewContext(allowMerge);
                    SharedRepackSetPlan? plan;
                    string? failure;
                    string failureBucket;
                    try
                    {
                        plan = PlanSharedRepackSet(
                            state,
                            result,
                            coverage,
                            context,
                            label,
                            setByTexture,
                            GetPart,
                            texture => IsSharedRepackUniformTexture(state, coverage, texture, uniformCache),
                            cancellationToken,
                            out failure,
                            out failureBucket);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        plan = null;
                        failure = $"internal error while planning ({ex.GetType().Name}: {ex.Message})";
                        failureBucket = "build";
                    }

                    if (plan == null)
                        return (null, failure ?? "unknown", failureBucket, context);

                    var built = ApplySharedRepackSetPlan(
                        state,
                        result,
                        context,
                        plan,
                        context.Rigids.Where(item => item.Rigid != null)
                            .ToDictionary(item => item.Path, item => item.Rigid!, StringComparer.OrdinalIgnoreCase),
                        context.Joined.Count == 0 ? label : label + " [merged plan]",
                        snapshots,
                        movedParts,
                        cancellationToken,
                        out var buildFailure,
                        out var pixelFailure);
                    return built != null
                        ? (built, null, "built", context)
                        : (null, buildFailure ?? "unknown", pixelFailure ? "pixel" : "build", context);
                }

                var dropsBefore = new Dictionary<string, int>(result.PartDropReasons, StringComparer.Ordinal);
                var notesBefore = result.ClosureNotes.Count;
                var sizingBefore = (result.SizingErrors, result.SizingLog.Count);
                var attempt = Attempt(true);
                string? fallbackNote = null;
                if (attempt.Context.CapHit)
                    result.MergeCapHits++;
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
                        // Only the cap notes of the failed attempt are kept; they explain parts that
                        // the attempt could not take in.
                        var attemptNotes = result.ClosureNotes.Skip(notesBefore).ToList();
                        result.ClosureNotes.RemoveRange(notesBefore, attemptNotes.Count);
                        result.ClosureNotes.AddRange(attemptNotes
                            .Where(note => note.Note.Contains("merge cap reached", StringComparison.Ordinal))
                            .Select(note => (note.Label + " [merged attempt]", note.Note)));
                        result.SizingErrors = sizingBefore.SizingErrors;
                        result.SizingLog.RemoveRange(sizingBefore.Item2, result.SizingLog.Count - sizingBefore.Item2);
                        attempt = Attempt(false);
                    }
                }

                if (attempt.Built == null)
                {
                    if (attempt.Bucket == "bytes")
                        result.FailedActualBytes++;
                    else if (attempt.Bucket == "pixel")
                        result.FailedPixelCheck++;
                    else if (attempt.Bucket == "build")
                        result.NotBuildable++;
                    else
                        result.DroppedByClosure++;
                    Skip(label, fallbackNote == null ? attempt.Failure! : fallbackNote + "alone: " + attempt.Failure);
                    continue;
                }

                attempt.Built.FallbackNote = fallbackNote == null ? null : fallbackNote + "built alone";
                result.BuiltSets.Add(attempt.Built);
            }

            // Geometry invariants of every rewritten rigid, against the snapshots.
            foreach (var (path, snapshot) in snapshots.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                var rigid = rigidCache[path].Rigid!;
                CompareSharedRepackSnapshot(
                    path,
                    rigid,
                    snapshot,
                    movedParts.GetValueOrDefault(path) ?? [],
                    result.ValidationErrors);
            }
        }

        // The parts of the rigids the cross-rigid writer generated and every texture of the materials
        // their generated WSModels bind. False when a material cannot be read: its textures are then
        // unknown, which the repack cannot act on.
        private static bool TryCollectCrossRigidMergedUsers(
            BatchState state,
            out List<(string Rigid, int Lod, int Part, HashSet<string> Textures)> users,
            out string? failure)
        {
            users = [];
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
                                       state.Source, GetGameplayTraversalContainers(state), material)?
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
                }
            }

            return true;
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
                            state.Source, GetGameplayTraversalContainers(state), texture)?
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
        {
            if (!state.Source.ContainsFile(rigidPath))
                return "the rigid is not owned by the source pack";
            if (OverridesVanillaPath(state, rigidPath))
                return "the rigid overrides a vanilla path";
            if (HasDirectGameplayRigidConsumer(state, rigidPath))
                return "the rigid is referenced directly (without a WSModel) by a VMD or other gameplay file";
            if (!IsGameplayConsumerDiscoverySafeForAsset(state, rigidPath))
                return "gameplay consumer discovery for the rigid is incomplete";
            var extra = beforeWsModelChecks?.Invoke();
            if (extra != null)
                return extra;

            var immutable = GetImmutableGameplayWsModelConsumersForRigid(state, rigidPath);
            var others = (referrers.GetValueOrDefault(rigidPath) ?? [])
                .Where(path => !boundWsModels.Contains(path))
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

        private static string DescribeSharedRepackSet(UnitRepackSetDecision decision)
            => $"{decision.ExampleTexture} ({decision.Textures:N0} texture(s), {decision.SignatureSize:N0} unit(s))";

        private static Dictionary<string, HashSet<string>> IndexSharedRepackRigidReferrers(
            BatchState state,
            CancellationToken cancellationToken)
        {
            var pattern = new Regex(@"[^\s<>""'=,;|()]+\.rigid_model_v2", RegexOptions.IgnoreCase);
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

        // "1,1" style float components: comma separated, invariant decimal point.
        private static bool TryScaleSharedRepackBloodParameter(
            XmlDocument material,
            double scaleU,
            double scaleV,
            out string description)
        {
            description = string.Empty;
            var parameter = material.SelectNodes("/material/params/param")?.Cast<XmlNode>()
                .FirstOrDefault(node =>
                    node.SelectSingleNode("name")?.InnerText.Trim().Equals(
                        SharedRepackBloodScaleParameter, StringComparison.OrdinalIgnoreCase) == true &&
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

        private static SharedRepackPart CreateSharedRepackPart(
            BatchState state,
            VanillaTextureCoverageResult coverage,
            RmvFile rigid,
            MeshKey key,
            List<SourceWsModelBinding> bindings)
        {
            var part = new SharedRepackPart { Key = key };
            part.Bindings.AddRange(bindings);
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

                foreach (var (slot, texture) in info.Textures)
                {
                    if (IsSharedRepackIgnoredTexture(slot, texture))
                        continue;

                    part.Textures.Add(Normalize(texture));
                    if (!IsTextureDownscaleSlot(slot))
                    {
                        part.Reason ??= SharedRepackReasonSlot + slot.ToLowerInvariant();
                        continue;
                    }

                    if (part.Slots.TryGetValue(slot, out var existing))
                    {
                        if (!existing.Equals(Normalize(texture), StringComparison.OrdinalIgnoreCase))
                            part.Reason ??= SharedRepackReasonVariants;
                    }
                    else
                    {
                        part.Slots[slot] = Normalize(texture);
                    }
                }
            }

            // A slot that only some bindings have is a disagreement too.
            foreach (var binding in bindings)
            {
                if (!coverage.Materials.TryGetValue(Normalize(binding.Material), out var info) || !info.Readable)
                    continue;

                var slots = info.Textures
                    .Where(texture => IsTextureDownscaleSlot(texture.Slot) &&
                                      !IsSharedRepackIgnoredTexture(texture.Slot, texture.Texture))
                    .Select(texture => texture.Slot)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!slots.SetEquals(part.Slots.Keys))
                    part.Reason ??= SharedRepackReasonVariants;
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
            var mergedUserParts = new Dictionary<(string Rigid, int Lod, int Part), SharedRepackPart>();

            bool IsBlockedTexture(VanillaCoverageTexture entry, string path)
                => GetSharedRepackBlockedTextureReason(merge.Effective(entry).Layout) != null ||
                   (entry.IsOwned && OverridesVanillaPath(state, path));

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
                    slotOf[path] = item.Slot;
                    entryOf[path] = merge.Effective(item.Entry);
                    // Textures that can never be copied exactly stay out of the set.
                    if (IsBlockedTexture(item.Entry, path))
                        blocked.Add(path);
                    else
                        members.Add(path);
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

                // Rigids the cross-rigid writer generated copy source parts with their original
                // materials. They are users the pre-pass sets do not list, and nothing rewrites
                // them, so they keep the textures loaded: each is a dropped part of the set.
                if (merge.MergedUsers.Count != 0)
                {
                    var setTextures = added.Set.Items
                        .Select(item => Normalize(item.Entry.Path))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var user in merge.MergedUsers)
                    {
                        var shared = user.Textures.Where(setTextures.Contains).ToList();
                        if (shared.Count == 0)
                            continue;

                        if (!mergedUserParts.TryGetValue((user.Rigid, user.Lod, user.Part), out var part))
                        {
                            part = new SharedRepackPart
                            {
                                Key = new MeshKey(user.Rigid, user.Lod, user.Part),
                                Reason = SharedRepackReasonMergedUser,
                            };
                            mergedUserParts[(user.Rigid, user.Lod, user.Part)] = part;
                            candidates.Add(part);
                        }

                        part.Textures.UnionWith(shared);
                        result.SetsBlockedByMergedUsers.Add(label);
                    }
                }
            }

            foreach (var item in rigids)
                AddRigid(item);
            AddSet(merge.Decisions[merge.Seed]);

            var dropped = new HashSet<MeshKey>();
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
                           $"{SharedRepackMaximumMergedRigids} rigids per plan)";
                }

                if (!merge.IsAvailable(index))
                    return " that was already processed";
                return merge.Allow ? string.Empty : " (merging is off for this attempt)";
            }

            void Drop(SharedRepackPart part, string reason)
            {
                dropped.Add(part.Key);
                droppedParts.Add(part);
                result.PartDropReasons[reason] = result.PartDropReasons.GetValueOrDefault(reason) + 1;
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

            // Why a part's textures outside the set keep it out of it: null when each of them
            // is a uniform colour, which is kept as authored and so never blocks the part.
            string? OutsideReason(SharedRepackPart part)
            {
                var offending = part.Textures
                    .Where(texture => !members.Contains(texture) && !isUniform(texture))
                    .ToList();
                if (offending.Count == 0)
                    return null;

                return offending.Any(blocked.Contains)
                    ? SharedRepackReasonBlockedTexture
                    : SharedRepackReasonMixed;
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
                            .Where(texture => !members.Contains(texture) && !isUniform(texture))
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
                            var rigidPaths = new HashSet<string>(setRigids, StringComparer.OrdinalIgnoreCase);
                            foreach (var index in joins)
                            {
                                rigidPaths.UnionWith(merge.Decisions[index].Set.Items
                                    .SelectMany(item => item.Entry.ModParts.Concat(item.Entry.VanillaParts))
                                    .Select(key => key.Rigid));
                            }

                            if (inComponent.Count + joins.Count > SharedRepackMaximumMergedSets ||
                                rigidPaths.Count > SharedRepackMaximumMergedRigids)
                            {
                                merge.CapHit = true;
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
                        result.ClosureNotes.Add((
                            label,
                            $"rigid {item.Path} is not eligible ({item.Reason}); its " +
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
                var moved = candidates
                    .Where(part => !dropped.Contains(part.Key) && part.Textures.Overlaps(members))
                    .ToList();
                if (moved.Count == 0 || members.Count == 0)
                {
                    NoteOutside();
                    failure = "no part is left after closing the set (mixed materials, variants, tiling, " +
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
                    => setByTexture.TryGetValue(texture, out var owner) ? GetSharedRepackSetKey(owner) : string.Empty;

                var tuples = BuildSharedRepackTuples(moved, entryOf, ClassifyConstant, SetKeyOf, out var violating);
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
                    state, result, coverage, rigids, tuples, entryOf, label, merge, out failure, out failureBucket);
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
                };
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
                foreach (var (slot, texture) in tuple.Slots.ToList())
                {
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
                if (tuple.Slots.Count == 0)
                {
                    tuple.Excluded = "every texture is a uniform one (nothing to repack)";
                    continue;
                }

                foreach (var (slot, texture) in tuple.Slots)
                    tuple.Layouts[slot] = entryOf[texture].Layout!;
                // Reference resolution: the base colour, else the largest slot.
                var reference = tuple.Slots.ContainsKey("t_xml_base_colour")
                    ? tuple.Layouts["t_xml_base_colour"]
                    : tuple.Layouts.Values.OrderByDescending(layout => (long)layout.Width * layout.Height).First();
                tuple.ReferenceWidth = reference.Width;
                tuple.ReferenceHeight = reference.Height;
                var downshift = 0;
                foreach (var (slot, layout) in tuple.Layouts)
                {
                    var exponent = (Log2Exact(layout.Width) - Log2Exact(reference.Width),
                        Log2Exact(layout.Height) - Log2Exact(reference.Height));
                    tuple.SourceExponent[slot] = exponent;
                    downshift = Math.Max(downshift, Math.Max(-exponent.Item1, -exponent.Item2));
                }

                // Cell: at least the minimum, a multiple of the pixels one reference pixel
                // spans in the coarsest slot (whole source texels, whole BC blocks), at most
                // 1024 cells per side.
                var cell = Math.Max(SharedRepackMinimumCell, 4 << downshift);
                if (cell > SharedRepackMaximumCell)
                {
                    tuple.Excluded = $"a slot is {1 << downshift} times smaller than its reference, which would " +
                                     $"need a {cell}-pixel chart grid (limit {SharedRepackMaximumCell})";
                    continue;
                }

                cell = Math.Max(
                    cell,
                    NextPowerOfTwo(Math.Max(reference.Width, reference.Height) / SharedRepackMaximumCellsPerSide));
                tuple.Cell = cell;
                tuple.Align = Math.Max(cell, SharedRepackChartAlignment << downshift);
                // The signature set of each slot's texture is part of the key: a generated texture
                // then only ever replaces source textures that exactly the same units load.
                tuple.GroupKey = $"{cell}|" + string.Join(
                    ";",
                    tuple.Layouts.Select(pair =>
                        $"{pair.Key}:{pair.Value.Format}:{pair.Value.IsSrgb}:" +
                        $"{tuple.SourceExponent[pair.Key].X}:{tuple.SourceExponent[pair.Key].Y}:" +
                        $"{setKeyOf(tuple.Slots[pair.Key])}"));
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
                    var box = live[root];
                    // Padded by one cell, but never beyond the texture itself, so a chart's
                    // rectangle in any slot is never larger than that slot's texture.
                    var left = Math.Max(0, (box.X0 - 1) * tuple.Cell);
                    var top = Math.Max(0, (box.Y0 - 1) * tuple.Cell);
                    var right = Math.Min(tuple.ReferenceWidth, (box.X1 + 2) * tuple.Cell);
                    var bottom = Math.Min(tuple.ReferenceHeight, (box.Y1 + 2) * tuple.Cell);
                    if (tuple.ReferenceWidth % tuple.Align == 0 && tuple.ReferenceHeight % tuple.Align == 0)
                    {
                        left = left / tuple.Align * tuple.Align;
                        top = top / tuple.Align * tuple.Align;
                        right = Math.Min(tuple.ReferenceWidth, RoundUpTo(right, tuple.Align));
                        bottom = Math.Min(tuple.ReferenceHeight, RoundUpTo(bottom, tuple.Align));
                    }

                    var width = right - left;
                    var height = bottom - top;
                    chartOfRoot[root] = tuple.Charts.Count;
                    tuple.Charts.Add(new SharedRepackChart
                    {
                        Id = tuple.Charts.Count,
                        Tuple = tuple,
                        CellX0 = box.X0,
                        CellY0 = box.Y0,
                        CellX1 = box.X1,
                        CellY1 = box.Y1,
                        X0 = left,
                        Y0 = top,
                        Width = width,
                        Height = height,
                        LayoutWidth = width,
                        LayoutHeight = height,
                    });
                }

                tuple.CellChart = new int[grid.Length];
                for (var cellIndex = 0; cellIndex < grid.Length; cellIndex++)
                    tuple.CellChart[cellIndex] = label[cellIndex] < 0 ? -1 : chartOfRoot[Find(label[cellIndex])];

                // Assign every vertex the chart of its triangles; all must agree. (A part's
                // assignment from an earlier round of the closure is void.)
                foreach (var part in tuple.Parts)
                {
                    Array.Fill(part.VertexChart, -1);
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
            }

            return tuples;
        }

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
            => string.Join(" + ", tuple.Slots.Values.Select(path => Path.GetFileName(path)));

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
            var nameRigid = planRigids[0];
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
                foreach (var texture in tuple.Slots.Values)
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
            while (true)
            {
                while (Propagate())
                {
                }

                plan.Pages.Clear();
                plan.Groups.Clear();
                foreach (var tuple in tuples)
                    foreach (var chart in tuple.Charts)
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

                var restart = false;
                foreach (var group in active.GroupBy(tuple => tuple.GroupKey).OrderBy(group => group.Key, StringComparer.Ordinal))
                {
                    var groupTuples = group.OrderBy(tuple => tuple.Index).ToList();
                    var pages = PackSharedRepackGroup(
                        nameRigid, identity, groupTuples, maximumSide, plan.Pages.Count, result.MinimumGroupSavingBytes,
                        out var split, out var excludeTuple, out var excludeReason);
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
                    var textures = groupTuples.SelectMany(tuple => tuple.Slots.Values)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var before = textures.Sum(texture => entryOf[texture].Layout!.ChainBytes);
                    var after = pages.Sum(page => page.BytesAfter);
                    var info = new SharedRepackGroupInfo
                    {
                        Sizes = DescribeSharedRepackSizes(groupTuples, pages),
                        Label = string.Join(" | ", groupTuples.Take(2).Select(DescribeSharedRepackTuple)) +
                                (groupTuples.Count > 2 ? $" (+{groupTuples.Count - 2:N0} tuple(s))" : string.Empty),
                        Tuples = groupTuples.Count,
                        Charts = groupTuples.Sum(tuple => tuple.Charts.Count),
                        Pages = pages.Count,
                        Before = before,
                        After = after,
                        Status = split.Applied
                            ? $"built, split into {pages.Count:N0} page(s): {split.BestSizes} (one page would be " +
                              $"{split.SingleSize}, {split.SingleBytes:N0} bytes)"
                            : "built",
                    };
                    // Why the pages are not smaller, for a group that is left untouched.
                    var splitNote = split.Note != null
                        ? $"; {split.Note}"
                        : split.BestPages != 0
                            ? $"; {split.Clusters:N0} cluster(s), best split into {split.BestPages:N0} page(s) " +
                              $"{split.BestSizes} = {split.BestBytes:N0} bytes did not pay"
                            : $"; {split.Clusters:N0} cluster(s), no split into smaller pages found";
                    if (before - after <= 0 || before - after < result.MinimumGroupSavingBytes)
                    {
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
                        restart = true;
                        break;
                    }

                    plan.Pages.AddRange(pages);
                    plan.Groups.Add(info);
                }

                if (!restart)
                    break;
            }

            // Every excluded tuple is reported as a skipped group.
            foreach (var group in tuples.Where(tuple => tuple.Excluded != null)
                         .GroupBy(tuple => tuple.Excluded!, StringComparer.Ordinal))
            {
                plan.Groups.Add(new SharedRepackGroupInfo
                {
                    Label = string.Join(" | ", group.Take(2).Select(DescribeSharedRepackTuple)) +
                            (group.Count() > 2 ? $" (+{group.Count() - 2:N0})" : string.Empty),
                    Tuples = group.Count(),
                    Before = group.SelectMany(tuple => tuple.Slots.Values).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Sum(texture => entryOf[texture].Layout!.ChainBytes),
                    Status = "left untouched: " + group.Key,
                    Sizes = string.Join(" | ", group.Take(2).Select(DescribeSharedRepackTupleSizes)),
                });
            }

            foreach (var page in plan.Pages)
            {
                foreach (var texture in page.Tuples.SelectMany(tuple => tuple.Slots.Values))
                    plan.Replaced[texture] = entryOf[texture].Layout!.ChainBytes;
            }

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
        {
            List<List<int>>? best = null;
            var bestKey = string.Empty;
            long bestTotal = long.MaxValue;
            var foundSizes = new List<(int Width, int Height)>();
            var attempts = 0;
            long Area(List<int> cluster) => cluster.Sum(index => (long)sizes[index].Width * sizes[index].Height);
            var ordered = clusters
                .Select(cluster => cluster
                    .OrderByDescending(index => sizes[index].Height)
                    .ThenByDescending(index => sizes[index].Width)
                    .ThenBy(index => index)
                    .ToList())
                .OrderByDescending(Area)
                .ThenBy(cluster => cluster.Min())
                .ToList();

            bool TryPage(IEnumerable<List<int>> page, out long bytes, out (int Width, int Height) size)
            {
                bytes = 0;
                size = default;
                var indexes = page.SelectMany(cluster => cluster).OrderBy(index => index).ToList();
                if (!TryPackSharedRepackLayout(
                        indexes.Select(index => sizes[index]).ToList(), limitWidth, limitHeight,
                        out var width, out var height, out _))
                {
                    return false;
                }

                size = (width, height);
                bytes = pageBytes(width, height);
                return true;
            }

            void Search(
                List<List<int>> remaining,
                List<List<List<int>>> pages,
                List<(int Width, int Height)> pageSizes,
                long accumulated)
            {
                if (accumulated > bestTotal || !TryPage(remaining, out var restBytes, out var restSize))
                    return;

                if (pages.Count > 0)
                {
                    var total = accumulated + restBytes;
                    var allSizes = pageSizes.Append(restSize).ToList();
                    var key = string.Join(",", allSizes.Select(size => $"{size.Width}x{size.Height}"));
                    var count = pages.Count + 1;
                    if (total < bestTotal ||
                        (total == bestTotal && (count < best!.Count ||
                            (count == best.Count && string.CompareOrdinal(key, bestKey) < 0))))
                    {
                        bestTotal = total;
                        bestKey = key;
                        foundSizes = allSizes;
                        best = pages.Select(page => page.SelectMany(cluster => cluster).OrderBy(index => index).ToList())
                            .Append(remaining.SelectMany(cluster => cluster).OrderBy(index => index).ToList())
                            .ToList();
                    }
                }

                if (pages.Count + 2 > SharedRepackMaximumSplitPages || remaining.Count < 2)
                    return;

                var restArea = (long)restSize.Width * restSize.Height;
                var candidates = new List<(int Width, int Height)>();
                for (var width = 16; width <= limitWidth; width <<= 1)
                {
                    for (var height = 16; height <= limitHeight; height <<= 1)
                    {
                        if ((long)width * height < restArea)
                            candidates.Add((width, height));
                    }
                }

                foreach (var candidate in candidates
                             .OrderByDescending(item => (long)item.Width * item.Height)
                             .ThenBy(item => Math.Abs(Log2Exact(item.Width) - Log2Exact(item.Height)))
                             .ThenByDescending(item => item.Width)
                             .Take(SharedRepackSplitCandidatesPerLevel))
                {
                    if (++attempts > SharedRepackMaximumSplitAttempts)
                        return;

                    // Whole clusters, largest first, on a shelf of the candidate's size; a
                    // cluster that does not fit goes to the rest.
                    var chosen = new List<List<int>>();
                    var rest = new List<List<int>>();
                    int x = 0, y = 0, rowHeight = 0;
                    foreach (var cluster in remaining)
                    {
                        int saveX = x, saveY = y, saveRow = rowHeight;
                        var fits = true;
                        foreach (var index in cluster)
                        {
                            var (width, height) = sizes[index];
                            if (width > candidate.Width || height > candidate.Height)
                            {
                                fits = false;
                                break;
                            }

                            if (x + width > candidate.Width)
                            {
                                x = 0;
                                y += rowHeight;
                                rowHeight = 0;
                            }

                            if (y + height > candidate.Height)
                            {
                                fits = false;
                                break;
                            }

                            x += width;
                            rowHeight = Math.Max(rowHeight, height);
                        }

                        if (fits)
                        {
                            chosen.Add(cluster);
                        }
                        else
                        {
                            (x, y, rowHeight) = (saveX, saveY, saveRow);
                            rest.Add(cluster);
                        }
                    }

                    if (chosen.Count == 0 || rest.Count == 0 || !TryPage(chosen, out var chosenBytes, out var chosenSize))
                        continue;

                    Search(
                        rest,
                        pages.Append(chosen).ToList(),
                        pageSizes.Append(chosenSize).ToList(),
                        accumulated + chosenBytes);
                }
            }

            Search(ordered, [], [], 0);
            bestBytes = bestTotal == long.MaxValue ? 0 : bestTotal;
            bestSizes = foundSizes;
            return best;
        }

        // One group's pages. Charts that a part uses together form a cluster and stay on one
        // page. Returns null with a tuple to exclude when a cluster cannot fit at all.
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
            split = new SharedRepackSplitInfo();
            excludeTuple = null;
            excludeReason = null;
            var first = groupTuples[0];
            var slots = first.Slots.Keys.ToList();
            var exponents = slots.ToDictionary(slot => slot, slot => first.SourceExponent[slot]);
            var maximumWidth = maximumSide >> Math.Max(0, exponents.Values.Max(exponent => exponent.X));
            var maximumHeight = maximumSide >> Math.Max(0, exponents.Values.Max(exponent => exponent.Y));
            var cell = first.Align;
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
            if (TryPackSharedRepackLayout(sizes, maximumWidth, maximumHeight, out var singleWidth, out var singleHeight, out _))
            {
                assignments.Add(allIndexes);

                // Power-of-two rounding can leave one page as large as the sources although the
                // charts cover far less. Whole clusters (a part's charts stay together, nothing
                // is duplicated) can then go onto several smaller pages.
                long PageBytes(int width, int height)
                {
                    long total = 0;
                    foreach (var slot in slots)
                    {
                        var w = ScaleByExponent(width, exponents[slot].X);
                        var h = ScaleByExponent(height, exponents[slot].Y);
                        total += EstimateDdsChainBytes(first.Layouts[slot], w, h, GetFullMipChainLength(w, h));
                    }

                    return total;
                }

                var sources = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var tuple in groupTuples)
                {
                    foreach (var slot in slots)
                        sources[tuple.Slots[slot]] = tuple.Layouts[slot].ChainBytes;
                }

                split.BeforeBytes = sources.Values.Sum();
                split.SingleBytes = PageBytes(singleWidth, singleHeight);
                split.SingleSize = $"{singleWidth}x{singleHeight}";
                if (clusters.Count < 2)
                {
                    split.OneCluster = true;
                    split.Note = "one connected cluster: cannot split";
                }
                else
                {
                    var pagesFound = FindSharedRepackSplit(
                        sizes, clusters, singleWidth, singleHeight, PageBytes, out var bestBytes, out var bestSizes);
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
                            TryPackSharedRepackLayout(trialSizes, maximumWidth, maximumHeight, out _, out _, out _))
                        {
                            page.AddRange(cluster);
                            placed = true;
                            break;
                        }
                    }

                    if (placed)
                        continue;

                    var alone = cluster.Select(index => (charts[index].LayoutWidth, charts[index].LayoutHeight)).ToList();
                    if (!TryPackSharedRepackLayout(alone, maximumWidth, maximumHeight, out _, out _, out _))
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
                TryPackSharedRepackLayout(pageSizes, maximumWidth, maximumHeight, out var width, out var height, out var positions);
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
                    var channel = AtlasChannels.First(item => item.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase));
                    var layout = first.Layouts[slot];
                    var output = new SharedRepackSlotOutput
                    {
                        Slot = slot,
                        Path = Normalize(
                            $@"{AtlasDirectory}\{SafeName(Path.GetFileNameWithoutExtension(nameRigid))}_{hash}_repack_{channel.Suffix}.dds"),
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
                        state.Source, GetGameplayTraversalContainers(state), material)?
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
                    var output = page.Outputs.Single(item => item.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase));
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
                var source = sources.Get(tuple.Slots[output.Slot]);
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

        private static long GetSharedRepackLevelOffset(DdsLayout layout, int level)
        {
            long offset = layout.HeaderSize;
            for (var index = 0; index < level; index++)
                offset += layout.LevelBytes(index);
            return offset;
        }

        // Writes the page's output: chart rectangles decoded into a canvas and encoded with a
        // full chain, then the compressed blocks of every block-aligned level copied over from
        // the sources, and the first re-encoded level seeded from the source's own mip.
        private static bool TryComposeSharedRepackOutput(
            SharedRepackPage page,
            SharedRepackSlotOutput output,
            SharedRepackSourceCache sources,
            out string error)
        {
            error = string.Empty;
            var canvas = new byte[checked(output.Width * output.Height * 4)];
            var neutral = GetSharedRepackNeutralPixel(output.Slot);
            for (var offset = 0; offset < canvas.Length; offset += 4)
                Buffer.BlockCopy(neutral, 0, canvas, offset, 4);

            var users = page.Tuples.Where(tuple => tuple.Slots.ContainsKey(output.Slot)).ToList();
            foreach (var tuple in users)
            {
                var texture = tuple.Slots[output.Slot];
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
            // Levels below the block-copied ones whose chart rectangles are still whole texels
            // are seeded from the sources' own mips instead of a box filter of level 0.
            var overlayLast = int.MaxValue;
            foreach (var tuple in users)
            {
                var cellX = ScaleByExponent(tuple.Align, output.ExponentX);
                var cellY = ScaleByExponent(tuple.Align, output.ExponentY);
                var whole = 0;
                while ((cellX >> (whole + 1)) >= 1 && (cellY >> (whole + 1)) >= 1)
                    whole++;
                overlayLast = Math.Min(overlayLast, whole);
            }

            overlayLast = Math.Min(overlayLast, output.MipLevels - 1);
            Action<int, byte[], int, int>? overlay = null;
            if (blockLevels > 0 && blockLevels < output.MipLevels && overlayLast >= blockLevels)
            {
                overlay = (level, pixels, levelWidth, levelHeight) =>
                {
                    foreach (var tuple in users)
                    {
                        var source = sources.Get(tuple.Slots[output.Slot]);
                        var levelData = source != null && source.Layout.MipCount > level
                            ? source.Level(level)
                            : null;
                        if (levelData == null)
                            continue;

                        var (bgra, width, height) = levelData.Value;
                        foreach (var item in EnumerateSharedRepackSlotCharts(page, output, tuple))
                        {
                            var sx = item.SourceX >> level;
                            var sy = item.SourceY >> level;
                            var dx = item.DestinationX >> level;
                            var dy = item.DestinationY >> level;
                            var w = Math.Min(item.Width >> level, Math.Min(width - sx, levelWidth - dx));
                            var h = Math.Min(item.Height >> level, Math.Min(height - sy, levelHeight - dy));
                            for (var y = 0; y < h; y++)
                            {
                                Buffer.BlockCopy(
                                    bgra, ((sy + y) * width + sx) * 4,
                                    pixels, ((dy + y) * levelWidth + dx) * 4,
                                    Math.Max(0, w) * 4);
                            }
                        }
                    }
                };
            }

            if (!TryEncodeSharedRepackChain(
                    canvas, output.Width, output.Height, output.Format, output.Srgb, output.Dx10,
                    blockLevels, overlayLast, overlay, out var dds, out error))
            {
                return false;
            }

            if (!TryReadDdsLayout(dds.AsSpan(0, Math.Min(dds.Length, TextureDownscaleHeaderPeekBytes)).ToArray(), out var layout))
            {
                error = "the encoded texture has an unreadable header";
                return false;
            }

            // Block copy.
            var bytesPerBlock = layout.BytesPerBlock;
            for (var level = 0; level < blockLevels; level++)
            {
                var destinationOffset = GetSharedRepackLevelOffset(layout, level);
                var destinationBlocks = (Math.Max(1, output.Width >> level) + 3) / 4;
                foreach (var tuple in users)
                {
                    var source = sources.Get(tuple.Slots[output.Slot])!;
                    var sourceOffset = GetSharedRepackLevelOffset(source.Layout, level);
                    var sourceBlocks = (Math.Max(1, source.Layout.Width >> level) + 3) / 4;
                    foreach (var item in EnumerateSharedRepackSlotCharts(page, output, tuple))
                    {
                        var blocksWide = (item.Width >> level) / 4;
                        var blocksHigh = (item.Height >> level) / 4;
                        for (var by = 0; by < blocksHigh; by++)
                        {
                            var from = sourceOffset +
                                       (((item.SourceY >> level) / 4 + by) * (long)sourceBlocks +
                                        (item.SourceX >> level) / 4) * bytesPerBlock;
                            var to = destinationOffset +
                                     (((item.DestinationY >> level) / 4 + by) * (long)destinationBlocks +
                                      (item.DestinationX >> level) / 4) * bytesPerBlock;
                            Buffer.BlockCopy(source.Dds, checked((int)from), dds, checked((int)to), blocksWide * bytesPerBlock);
                        }
                    }
                }
            }

            output.Data = dds;
            output.BlockCopiedLevels = blockLevels;
            return true;
        }

        // Canvas -> BC chain. The levels from firstOverlayLevel to lastOverlayLevel of the
        // box-filtered chain are overwritten by the callback (the sources' own mip crops)
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

        // ---------------------------------------------------------------------------
        // Applying a plan: compose, check, then write.
        // ---------------------------------------------------------------------------

        private sealed class SharedRepackRigidSnapshot
        {
            public List<List<(
                Microsoft.Xna.Framework.Vector4[] Positions,
                Microsoft.Xna.Framework.Vector2[] Uv,
                Microsoft.Xna.Framework.Vector2[] Uv1,
                ushort[] Indices)>> Lods { get; } = [];
        }

        private static SharedRepackRigidSnapshot SnapshotSharedRepackRigid(RmvFile rigid)
        {
            var snapshot = new SharedRepackRigidSnapshot();
            foreach (var lod in rigid.ModelList)
            {
                var parts = new List<(
                    Microsoft.Xna.Framework.Vector4[] Positions,
                    Microsoft.Xna.Framework.Vector2[] Uv,
                    Microsoft.Xna.Framework.Vector2[] Uv1,
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
            foreach (var tuple in page.Tuples)
            {
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
                        if (triangleCounter++ % stride != 0)
                            continue;

                        int[] corners = [indices[index], indices[index + 1], indices[index + 2]];
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
                                foreach (var output in page.Outputs.Where(item => tuple.Slots.ContainsKey(item.Slot)))
                                {
                                    var source = sources.Get(tuple.Slots[output.Slot]);
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
                                foreach (var output in page.Outputs.Where(item => tuple.Slots.ContainsKey(item.Slot)))
                                {
                                    var texture = tuple.Slots[output.Slot];
                                    var source = sources.Get(texture)!;
                                    var target = generated.Get(output.Path)!;
                                    var exact = output.BlockCopiedLevels > 0;
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
                                            pageTopSum += error;
                                            pageTopCount++;
                                        }
                                        else
                                        {
                                            pageLevelMax = Math.Max(pageLevelMax, error);
                                            if (level < output.BlockCopiedLevels)
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

            stats.TopMax = Math.Max(stats.TopMax, pageTopMax);
            stats.TopSum += pageTopSum;
            stats.TopCount += pageTopCount;
            stats.LevelMax = Math.Max(stats.LevelMax, pageLevelMax);
            stats.LevelSum += pageLevelSum;
            stats.LevelCount += pageLevelCount;
            var topMean = pageTopCount == 0 ? 0 : pageTopSum / pageTopCount;
            var levelMean = pageLevelCount == 0 ? 0 : pageLevelSum / pageLevelCount;
            if (exactViolation)
                problems.Add($"a block-copied top level differs from the source (max {pageTopMax:0}); it must be exact");
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
                ResolveGameplayTraversalContainer(state.Source, containers, path)?.FindFile(path)?.DataSource.ReadData());
            var generated = new SharedRepackSourceCache(_ => null);
            var stats = new SharedRepackPixelStats();
            var rewrites = new List<(SharedRepackPart Part, Microsoft.Xna.Framework.Vector2[] Uv)>();
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
                        if (!TryComposeSharedRepackOutput(page, output, sources, out var error))
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

                        generated.FromBytes(output.Path, output.Data);
                    }
                }

                SharedRepackBeforeCheckHook?.Invoke(plan);
                var problems = new List<string>();
                foreach (var page in plan.Pages)
                    CheckSharedRepackPage(page, sources, generated, stats, problems);
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
                    foreach (var output in page.Outputs)
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
                            var uv = new Microsoft.Xna.Framework.Vector2[vertices.Length];
                            for (var vertex = 0; vertex < vertices.Length; vertex++)
                            {
                                if (!part.Used[vertex])
                                {
                                    uv[vertex] = vertices[vertex].Uv;
                                    continue;
                                }

                                var (u, v) = ComputeSharedRepackUv(page, tuple, part, vertex);
                                uv[vertex] = new Microsoft.Xna.Framework.Vector2(u, v);
                            }

                            rewrites.Add((part, uv));
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
                foreach (var output in page.Outputs)
                {
                    WriteFile(state.Output, output.Path, output.Data);
                    state.SharedRepackTexturePaths.Add(output.Path);
                    result.GeneratedTextures.Add(output.Path);
                }

                foreach (var texture in page.Tuples.SelectMany(tuple => tuple.Slots.Values).Distinct(StringComparer.OrdinalIgnoreCase))
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

            foreach (var (part, uv) in rewrites)
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
            };
            built.Rigids.AddRange(rewritten);
            built.MergedSets.AddRange(merge.Component.Skip(1).Select(item =>
                (DescribeSharedRepackSet(item), item.Set.Items.Count, item.SignatureSize)));
            foreach (var (path, bytes) in plan.Replaced.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var owned = merge.Component.SelectMany(item => item.Set.Items)
                    .First(item => Normalize(item.Entry.Path).Equals(path, StringComparison.OrdinalIgnoreCase)).Entry.IsOwned;
                built.Replaced.Add((path, owned, bytes));
                built.Before += bytes;
            }

            built.Groups.AddRange(plan.Groups);
            built.Splits.AddRange(plan.Splits.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value));
            built.Outputs.AddRange(plan.Pages.SelectMany(page => page.Outputs));
            built.Materials.AddRange(plan.Pages.SelectMany(page => page.MaterialMap.Values).OrderBy(path => path, StringComparer.Ordinal));
            built.BloodScales.AddRange(plan.Pages.SelectMany(page => page.BloodScales));
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
                    // source textures that exactly the same units load.
                    var owners = output.Sources
                        .Select(source => signatureSetOf.GetValueOrDefault(source))
                        .Distinct()
                        .ToList();
                    if (owners.Count != 1 || owners[0] == null)
                    {
                        errors.Add(
                            $"Generated repack texture {output.Path} replaces source textures of " +
                            $"{(owners.Contains(null) ? "an unknown signature set and " : string.Empty)}" +
                            $"{owners.Count(owner => owner != null):N0} signature set(s), not exactly one");
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
                            // Only the textures the repack generated; pass-through slots are unchanged.
                            if (!set.Outputs.Any(output => output.Path.Equals(texture, StringComparison.OrdinalIgnoreCase)))
                                continue;

                            if (!CanResolveAfterRewrite(state, texture))
                                errors.Add($"Generated repack material texture does not resolve: {materialPath} -> {texture}");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Generated repack material is invalid: {materialPath} ({ex.Message})");
                    }
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

            if (errors.Count == before && result.BuiltSets.Count != 0)
            {
                state.ValidationMessages.Add(
                    $"PASS: {result.BuiltSets.Count} shared texture repack set(s) over {result.RepackedRigids.Count} " +
                    $"rewritten rigid(s): {result.GeneratedTextures.Count} generated texture(s) decode with the " +
                    $"planned size, format and a complete mip chain, {result.GeneratedMaterials.Count} generated " +
                    $"material(s) resolve and are only used by WSModels of rewritten rigids, geometry invariants " +
                    $"hold, and {result.Samples:N0} sampled texel(s) match the source (max error " +
                    $"{result.MaxError:0}, mean {(result.ErrorCount == 0 ? 0 : result.ErrorSum / result.ErrorCount):0.00}).");
            }
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
                        ? "Requested but ignored: not available together with cross-rigid merges or " +
                          "cross-rigid-only mode (they rewrite UVs of rigids the repack cannot account for). " +
                          "No pack file was changed."
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
                "materials; any other non-atlasable slot makes a part ineligible, named per slot below. " +
                $"ASSUMPTION (check in game): the blood overlay samples UV0 x {SharedRepackBloodScaleParameter}; " +
                "the repack scales UV0 per tuple, so a float2 " + SharedRepackBloodScaleParameter +
                " is divided by that scale per axis (pattern density preserved, a per-chart offset remains). " +
                "Generated copies of vanilla materials are written to " + SharedRepackVanillaMaterialDirectory + ".");
            sb.AppendLine(
                $"Threshold: estimated and actual saving of at least {result.MinimumSavingBytes:N0} bytes. " +
                $"Charts: grid cells of {SharedRepackMinimumCell}+ source pixels, 8-connected, one cell of padding, " +
                $"merged while padded boxes overlap. Tuples are packed together only inside a layout group (same " +
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
                sb.AppendLine(
                    $"Cross-rigid merging ran first: {result.RigidsChangedByCrossRigid.Count:N0} source rigid(s) it " +
                    $"built generated models from were left out of the repack (reason " +
                    $"\"{SharedRepackReasonCrossRigidChangedRigid}\"; their parts still count as users, since the " +
                    $"source files are kept and may stay reachable). The generated rigids copy source parts with " +
                    $"their original materials, so they keep those textures loaded and block the sets that contain " +
                    $"them: {result.SetsBlockedByMergedUsers.Count:N0} set(s) blocked by merged-rigid users.");
            }
            sb.AppendLine(
                $"  Dropped by closure, variants, tiling, ineligible rigids or the re-estimate: {result.DroppedByClosure:N0}");
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
            sb.AppendLine(
                $"Merged plans (a set whose material shares textures with unprocessed sets takes them in, up to " +
                $"{SharedRepackMaximumMergedSets} sets and {SharedRepackMaximumMergedRigids} rigids): " +
                $"{result.MergePlansTried:N0} tried, {result.MergePlansBuilt:N0} built, {result.MergePlansFellBack:N0} " +
                $"fell back to the seed set alone; largest {result.LargestMergeSets:N0} set(s) in " +
                $"{result.LargestMergeRigids:N0} rigid(s); {result.MergeCapHits:N0} plan(s) hit the cap.");
            var splits = result.BuiltSets.SelectMany(set => set.Splits).ToList();
            sb.AppendLine(
                $"Page splitting (a layout group whose power-of-two page cannot shrink puts whole clusters of charts " +
                $"on up to {SharedRepackMaximumSplitPages} smaller pages, never duplicating a chart or splitting a mesh part), " +
                $"in built sets: {splits.Count(item => item.Applied):N0} group(s) split, " +
                $"{splits.Sum(item => item.Saved):N0} bytes saved over the single-page result, " +
                $"{splits.Count(item => item.OneCluster):N0} group(s) could not split (one connected cluster).");
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

            long before = result.BuiltSets.Sum(set => set.Before);
            long after = result.BuiltSets.Sum(set => set.After);
            sb.AppendLine(
                $"Built sets: {result.Built:N0}; replaced texture bytes {before:N0} -> new {after:N0} " +
                $"(actual saving {FormatSignedBytes(before - after)} bytes); {result.GeneratedTextures.Count:N0} " +
                $"texture(s) and {result.GeneratedMaterials.Count:N0} material(s) generated; " +
                $"{result.RepackedRigids.Count:N0} rigid(s) rewritten.");
            foreach (var set in result.BuiltSets)
            {
                sb.AppendLine($"  Set {set.Label}");
                sb.AppendLine(
                    $"    {set.Rigids.Count:N0} rigid(s) rewritten: " +
                    string.Join(", ", set.Rigids.Select(item => $"{item.Rigid} ({item.Parts:N0} part(s))")));
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
                foreach (var group in set.Groups)
                {
                    sb.AppendLine(
                        $"    layout group {group.Label}: {group.Tuples:N0} tuple(s), {group.Charts:N0} chart(s), " +
                        $"{group.Pages:N0} page(s), {group.Before:N0} -> {group.After:N0} bytes, {group.Status}");
                    if (group.Sizes.Length != 0)
                        sb.AppendLine($"      sizes: {group.Sizes}");
                }
                foreach (var blood in set.BloodScales)
                    sb.AppendLine($"    blood: {blood}");
                sb.AppendLine(
                    $"    pixel check (before writing): top level {set.Samples:N0} sample(s), max error {set.MaxError:0}, " +
                    $"mean {set.MeanError:0.00}; mip 2/3 (source's own level) {set.LevelSamples:N0} sample(s), max " +
                    $"{set.LevelMaxError:0} (not gated), 99th percentile {set.LevelP99Error:0}, mean {set.LevelMeanError:0.00}, " +
                    $"{set.LevelOutlierShare:P2} of re-encoded samples over {SharedRepackLevelOutlierError:0} " +
                    $"(fail above {SharedRepackLevelOutlierShare:P1}) (texels within 1 of a chart edge are " +
                    $"not judged: {set.EdgeSkippedSamples:N0}); {set.SkippedSamples:N0} point(s) skipped " +
                    "(no position off texel edges)");
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
                        $"{output.BlockCopiedLevels} of {output.MipLevels} mip level(s) block-copied");
                }
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
                "Not covered by this increment: material variants that disagree on a texture, tiled UVs, " +
                "rigids used by more than one set of units outside the pack, merging sets that no material connects, " +
                "deleting the old textures.");
            sb.AppendLine();
        }
    }
}
