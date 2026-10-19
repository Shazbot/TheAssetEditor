using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Xml;
using Shared.Core.PackFiles.Models;
using Shared.GameFormats.RigidModel;

namespace Editors.PackOptimizer.Core
{
    public sealed partial class PackTextureAtlasBatchService
    {
        // Report-only "Vanilla texture coverage". A mod unit often reuses small pieces of
        // vanilla meshes, and every piece keeps the whole vanilla texture loaded. For each
        // vanilla texture that the pack's WSModels reach through a material this measures
        // how much of the texture the pack's parts actually touch, so a "reverse atlas"
        // (a smaller texture holding only the used regions) can be judged before it is
        // built. It changes no pack file.
        //
        // The pack is measured AS AUTHORED: parts and materials come from the SOURCE
        // WSModels and rigids (fresh reads), every LOD, UV0. Parts atlased, merged or
        // pruned in this run are deliberately NOT excluded (unlike the downscale analysis,
        // which judges the output), so the numbers do not depend on the atlas options.
        private const double VanillaCoverageBudgetSeconds = 45;
        private const int VanillaCoverageListLimit = 40;
        private const int VanillaCoverageExampleLimit = 3;
        private const int VanillaCoverageGridSize = 256;
        private const double VanillaCoveragePackingSlack = 1.25;
        private const int VanillaCoverageMinimumSide = 4;
        // UV extent beyond one texture period (with float noise allowed) means tiling.
        private const double VanillaCoverageTileEpsilon = 1e-4;
        // Coordinates beyond this are corrupt data, not tiling.
        private const double VanillaCoverageMaximumUv = 1e6;

        private const string VanillaCoverageReasonTiled =
            "a part tiles it (a triangle spans more than one texture period)";
        private const string VanillaCoverageReasonUnassessable =
            "a mesh part using it cannot be assessed (rigid unreadable, bad indices, no usable triangles)";
        private const string VanillaCoverageReasonSlot =
            "also used in a slot that is not atlasable (legacy s_*, blood, dither, ...)";
        private const string VanillaCoverageReasonNonMaterial =
            "mentioned by a non-material file of the pack (VMD, WSModel, text)";

        private const string VanillaCoverageStaysLoadedNotComputedReason =
            "the existing residency model cannot be queried here without side effects: " +
            "BuildAtlasValueGateSourceTextureIndex overwrites about twenty atlas value gate " +
            "diagnostics and fills the WSModel/material consumer caches on the shared batch " +
            "state, and it indexes every reachable WSModel of the roster, so only the atlas " +
            "value gate runs it";

        // Everything known about one vanilla texture that the pack references.
        private sealed class VanillaCoverageTexture
        {
            public VanillaCoverageTexture(string path)
            {
                Path = path;
            }

            public string Path { get; }
            public DdsLayout? Layout { get; set; }
            // Owned by the source pack (otherwise vanilla). Owned textures are measured for
            // the per-unit repack estimate only; the coverage section lists vanilla ones.
            public bool IsOwned { get; set; }
            // The figures below were computed (header readable and the geometry pass reached it).
            public bool Finalized { get; set; }
            public HashSet<string> WsModels { get; } = new(StringComparer.OrdinalIgnoreCase);
            // Occupancy of the texture area by the triangles of every part and LOD.
            public ulong[] Coverage { get; } = CreateVanillaCoverageGrid();
            // Occupancy by one bounding rectangle per part (the atlas stage's crop).
            public ulong[] BoundingRectangles { get; } = CreateVanillaCoverageGrid();
            public bool Tiled { get; set; }
            public bool NotReached { get; set; }
            public int UnassessableUses { get; set; }
            public string? BadSlot { get; set; }
            public string? NonMaterialReference { get; set; }
            public HashSet<string> Materials { get; } = new(StringComparer.OrdinalIgnoreCase);
            // Distinct (rigid, LOD, part) users, split by who owns the rigid.
            public HashSet<(string Rigid, int Lod, int Part)> ModParts { get; } = [];
            public HashSet<(string Rigid, int Lod, int Part)> VanillaParts { get; } = [];

            public int AssessedParts => ModParts.Count + VanillaParts.Count;

            // Filled once the geometry pass is done.
            public double CoverageFraction { get; set; }
            public double BoundingRectangleFraction { get; set; }
            public int CropWidth { get; set; }
            public int CropHeight { get; set; }
            public long OriginalBytes { get; set; }
            public long CropBytes { get; set; }
            public string? NotCroppableReason { get; set; }

            public bool IsCroppable => NotCroppableReason == null;
            // Usable by the per-unit repack estimate: measured, and passes every croppable test.
            public bool IsRepackable => Finalized && Layout != null && NotCroppableReason == null;
            public long BytesSaved => Math.Max(0, OriginalBytes - CropBytes);
        }

        private sealed record VanillaCoverageBucket(
            string Label,
            List<VanillaCoverageTexture> Textures)
        {
            public long OriginalBytes => Textures.Sum(texture => texture.OriginalBytes);
            public long CropBytes => Textures.Sum(texture => texture.CropBytes);
            public long BytesSaved => Textures.Sum(texture => texture.BytesSaved);
        }

        // The one place the coverage buckets are decided: [0, 10 %), [10, 25 %), [25, 50 %)
        // and [50 %, 100 %]; a texture exactly at a limit belongs to the higher bucket.
        private static List<VanillaCoverageBucket> BuildVanillaCoverageBuckets(
            IEnumerable<VanillaCoverageTexture> textures)
        {
            var buckets = new List<VanillaCoverageBucket>
            {
                new("below 10 %", []),
                new("10 to 25 %", []),
                new("25 to 50 %", []),
                new("50 % and above", []),
            };
            foreach (var texture in textures)
            {
                var coverage = texture.CoverageFraction;
                var index = coverage < 0.10 ? 0 : coverage < 0.25 ? 1 : coverage < 0.50 ? 2 : 3;
                buckets[index].Textures.Add(texture);
            }

            return buckets;
        }

        // The cells one part's triangles touch (UV0, wrapped), kept so the per-unit repack
        // estimate can combine parts without reading or rasterising anything again.
        private sealed record VanillaCoveragePart(ulong[] Grid, bool Tiled);

        private sealed record VanillaCoverageMaterial(
            string Path,
            bool Found,
            bool Readable,
            IReadOnlyList<(string Slot, string Texture)> Textures);

        private sealed class VanillaTextureCoverageResult
        {
            public TimeSpan AnalysisElapsed { get; set; }
            public TimeSpan Budget { get; set; }
            public string? Failure { get; set; }
            public bool BudgetExhausted { get; set; }
            // Why the "stays loaded anyway" probability is not part of the result.
            public string StaysLoadedNotComputedReason { get; set; } =
                VanillaCoverageStaysLoadedNotComputedReason;

            public int WsModelsScanned { get; set; }
            public int UnreadableWsModels { get; set; }
            public int BindingsWithoutPart { get; set; }
            public int MaterialsReferenced { get; set; }
            public int MaterialsNotFound { get; set; }
            public int MaterialsUnreadable { get; set; }
            public int TextFilesScanned { get; set; }
            public int TextFilesUnreadable { get; set; }
            public int RigidsRead { get; set; }
            public int RigidsUnavailable { get; set; }
            public int RigidsSkippedForTime { get; set; }
            public int PartsAssessed { get; set; }
            public int PartsUnassessable { get; set; }
            public long TrianglesMarked { get; set; }
            public int DegenerateTriangles { get; set; }

            // The split of the referenced textures; the five counts below sum to
            // TexturesReferenced.
            // Kept for the per-unit repack estimate (shared collection, nothing read twice).
            public List<SourceWsModelBinding> Bindings { get; set; } = [];
            public Dictionary<string, VanillaCoverageMaterial> Materials { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, VanillaCoverageTexture> Entries { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<(string Rigid, int Lod, int Part), VanillaCoveragePart> PartData { get; } = [];
            // Owned texture -> every source-pack material file that mentions it.
            public Dictionary<string, HashSet<string>> OwnedMaterialMentions { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            // Owned textures that must never be reported removable (unreadable material may
            // mention them, or the path also exists in vanilla).
            public HashSet<string> OwnedRemovalBlockers { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            public int TexturesReferenced { get; set; }
            public int OwnedBySourcePack { get; set; }
            public int NotFound { get; set; }
            public int HeaderUnreadable { get; set; }
            public int NotReached { get; set; }
            public List<VanillaCoverageTexture> Assessed { get; } = [];

            public IEnumerable<VanillaCoverageTexture> Croppable
                => Assessed.Where(texture => texture.IsCroppable);
        }

        // ---------------------------------------------------------------------------
        // Pure geometry: UV coverage grid.
        // ---------------------------------------------------------------------------

        private static ulong[] CreateVanillaCoverageGrid()
            => new ulong[VanillaCoverageGridSize * VanillaCoverageGridSize / 64];

        private static double CountVanillaCoverage(ulong[] grid)
        {
            long set = 0;
            foreach (var word in grid)
                set += BitOperations.PopCount(word);

            return set / (double)(VanillaCoverageGridSize * VanillaCoverageGridSize);
        }

        // Cell indices wrap modulo the grid, so a UV offset by whole tiles lands on the same
        // cells and a span that crosses a tile boundary marks both sides.
        private static void SetVanillaCoverageCells(
            ulong[] grid,
            int row,
            int firstCell,
            int cellCount)
        {
            var size = VanillaCoverageGridSize;
            var wrappedRow = PositiveModulo(row, size);
            var x = PositiveModulo(firstCell, size);
            for (var count = Math.Min(cellCount, size); count > 0; count--)
            {
                var bit = wrappedRow * size + x;
                grid[bit >> 6] |= 1UL << (bit & 63);
                x = x + 1 == size ? 0 : x + 1;
            }
        }

        // Clips a convex polygon (x, y pairs) to one side of a horizontal line.
        private static int ClipVanillaCoveragePolygon(
            ReadOnlySpan<double> input,
            int count,
            Span<double> output,
            bool keepAbove,
            double lineY)
        {
            var written = 0;
            for (var index = 0; index < count; index++)
            {
                var next = (index + 1) % count;
                var x0 = input[index * 2];
                var y0 = input[index * 2 + 1];
                var x1 = input[next * 2];
                var y1 = input[next * 2 + 1];
                var inside0 = keepAbove ? y0 >= lineY : y0 <= lineY;
                var inside1 = keepAbove ? y1 >= lineY : y1 <= lineY;
                if (inside0)
                {
                    output[written * 2] = x0;
                    output[written * 2 + 1] = y0;
                    written++;
                }

                if (inside0 != inside1)
                {
                    var t = (lineY - y0) / (y1 - y0);
                    output[written * 2] = x0 + (x1 - x0) * t;
                    output[written * 2 + 1] = lineY;
                    written++;
                }
            }

            return written;
        }

        // Conservative rasterisation: every cell the triangle touches is set. The UVs are
        // handled in unwrapped space, one grid row at a time (the triangle clipped to the
        // row strip is convex, so the cells it touches there are one contiguous run), and
        // only the cell indices are wrapped. Returns false for a degenerate triangle (zero
        // area, not finite, or absurd coordinates); nothing is marked then. 'tiled' is set
        // when the triangle's UV extent exceeds one texture period on either axis.
        private static bool MarkVanillaCoverageTriangle(
            ulong[] grid,
            double ax, double ay,
            double bx, double by,
            double cx, double cy,
            out bool tiled)
        {
            tiled = false;
            if (!double.IsFinite(ax + ay + bx + by + cx + cy) ||
                Math.Max(
                    Math.Max(Math.Abs(ax), Math.Abs(ay)),
                    Math.Max(
                        Math.Max(Math.Abs(bx), Math.Abs(by)),
                        Math.Max(Math.Abs(cx), Math.Abs(cy)))) > VanillaCoverageMaximumUv)
            {
                return false;
            }

            var cross = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (Math.Abs(cross) <= 1e-12)
                return false;

            var minU = Math.Min(ax, Math.Min(bx, cx));
            var maxU = Math.Max(ax, Math.Max(bx, cx));
            var minV = Math.Min(ay, Math.Min(by, cy));
            var maxV = Math.Max(ay, Math.Max(by, cy));
            tiled = maxU - minU > 1 + VanillaCoverageTileEpsilon ||
                    maxV - minV > 1 + VanillaCoverageTileEpsilon;

            var size = VanillaCoverageGridSize;
            Span<double> triangle = stackalloc double[6]
            {
                ax * size, ay * size, bx * size, by * size, cx * size, cy * size,
            };
            Span<double> bufferA = stackalloc double[16];
            Span<double> bufferB = stackalloc double[16];

            var firstRow = (int)Math.Floor(minV * size);
            var lastRow = Math.Max(firstRow, (int)Math.Ceiling(maxV * size) - 1);
            // A triangle taller than the texture covers every wrapped row after 'size' rows.
            lastRow = Math.Min(lastRow, firstRow + size - 1);
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

                var firstCell = (int)Math.Floor(rowMinX);
                var lastCell = Math.Max(firstCell, (int)Math.Ceiling(rowMaxX) - 1);
                SetVanillaCoverageCells(grid, row, firstCell, lastCell - firstCell + 1);
            }

            return true;
        }

        // Marks every triangle of one part into 'grid'. Returns false when the part has no
        // usable triangle (missing mesh, bad index list, or only degenerate triangles).
        private static bool TryMarkVanillaPartCoverage(
            RmvModel model,
            ulong[] grid,
            out bool tiled,
            out int triangles,
            out int degenerateTriangles)
        {
            tiled = false;
            triangles = 0;
            degenerateTriangles = 0;
            var vertices = model.Mesh?.VertexList;
            var indices = model.Mesh?.IndexList;
            if (vertices == null || indices == null ||
                indices.Length < 3 || indices.Length % 3 != 0)
            {
                return false;
            }

            for (var index = 0; index < indices.Length; index += 3)
            {
                var ia = indices[index];
                var ib = indices[index + 1];
                var ic = indices[index + 2];
                if (ia >= vertices.Length || ib >= vertices.Length || ic >= vertices.Length)
                    return false;

                var a = vertices[ia].Uv;
                var b = vertices[ib].Uv;
                var c = vertices[ic].Uv;
                if (MarkVanillaCoverageTriangle(
                        grid, a.X, a.Y, b.X, b.Y, c.X, c.Y, out var triangleTiled))
                {
                    triangles++;
                    tiled |= triangleTiled;
                }
                else
                {
                    degenerateTriangles++;
                }
            }

            return triangles != 0;
        }

        // The atlas stage's crop for one part (GetUvBounds / GetEffectiveCrop), as cells of
        // the grid: a crop that is as wide or tall as the texture covers all columns or rows.
        private static void MarkVanillaBoundingRectangle(
            ulong[] grid,
            UvBounds bounds,
            int textureWidth,
            int textureHeight)
        {
            var crop = GetEffectiveCrop(bounds, textureWidth, textureHeight);
            var size = VanillaCoverageGridSize;
            var firstColumn = (int)Math.Floor(crop.X * (double)size / textureWidth);
            var lastColumn = (int)Math.Ceiling((crop.X + (long)crop.Width) * (double)size / textureWidth) - 1;
            var firstRow = (int)Math.Floor(crop.Y * (double)size / textureHeight);
            var lastRow = (int)Math.Ceiling((crop.Y + (long)crop.Height) * (double)size / textureHeight) - 1;
            lastColumn = Math.Max(firstColumn, lastColumn);
            lastRow = Math.Max(firstRow, lastRow);
            for (var row = firstRow; row <= Math.Min(lastRow, firstRow + size - 1); row++)
                SetVanillaCoverageCells(grid, row, firstColumn, lastColumn - firstColumn + 1);
        }

        // The smallest same-aspect rectangle (both sides halved the same number of times,
        // never below 4 px) that holds the used pixels plus packing slack, and the BCn bytes
        // of its mip chain. Never larger than the original.
        private static void EstimateVanillaCrop(
            int width,
            int height,
            int bytesPerBlock,
            long originalBytes,
            double coverage,
            out int cropWidth,
            out int cropHeight,
            out long cropBytes)
        {
            var neededPixels = Math.Clamp(coverage, 0, 1) * width * height *
                               VanillaCoveragePackingSlack;
            cropWidth = width;
            cropHeight = height;
            for (var shift = 1; shift < 31; shift++)
            {
                var candidateWidth = Math.Max(Math.Min(VanillaCoverageMinimumSide, width), width >> shift);
                var candidateHeight = Math.Max(Math.Min(VanillaCoverageMinimumSide, height), height >> shift);
                if ((candidateWidth == cropWidth && candidateHeight == cropHeight) ||
                    (double)candidateWidth * candidateHeight < neededPixels)
                {
                    break;
                }

                cropWidth = candidateWidth;
                cropHeight = candidateHeight;
            }

            cropBytes = Math.Min(
                originalBytes,
                CalculateBcnMipChainBytes(cropWidth, cropHeight, bytesPerBlock));
        }

        // First applicable reason, so the not-croppable counts split exactly.
        private static string? GetVanillaCoverageNotCroppableReason(VanillaCoverageTexture texture)
        {
            if (texture.Tiled)
                return VanillaCoverageReasonTiled;
            if (texture.UnassessableUses != 0 || texture.AssessedParts == 0)
                return VanillaCoverageReasonUnassessable;
            if (texture.BadSlot != null)
                return VanillaCoverageReasonSlot;
            if (texture.NonMaterialReference != null)
                return VanillaCoverageReasonNonMaterial;
            return null;
        }

        // ---------------------------------------------------------------------------
        // Stage entry and analysis.
        // ---------------------------------------------------------------------------

        private void RunVanillaTextureCoverage(
            BatchState state,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress,
            Func<string, RmvFile?>? loadRigid = null,
            TimeSpan? budget = null)
        {
            var result = new VanillaTextureCoverageResult
            {
                Budget = budget ?? TimeSpan.FromSeconds(VanillaCoverageBudgetSeconds),
            };
            state.VanillaTextureCoverage = result;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                ReportProgress(progress, "Vanilla texture coverage");
                AnalyzeVanillaTextureCoverage(
                    state,
                    result,
                    loadRigid ?? (path => LoadTextureDownscaleRigid(state, path)),
                    cancellationToken,
                    progress);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Diagnostic stage: a failure never fails the run or changes the output.
                result.Failure = $"{ex.GetType().Name}: {ex.Message}";
            }

            result.AnalysisElapsed = stopwatch.Elapsed;
            state.PhaseDurations["Vanilla texture coverage"] = result.AnalysisElapsed;
        }

        private static void AnalyzeVanillaTextureCoverage(
            BatchState state,
            VanillaTextureCoverageResult result,
            Func<string, RmvFile?> loadRigid,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var timer = Stopwatch.StartNew();
            var containers = GetGameplayTraversalContainers(state);
            var sourceFiles = state.Source.GetAllFiles();

            // 1. Bindings of the source WSModels; their materials, wherever they live.
            var scan = EnumerateSourceWsModelBindings(sourceFiles, cancellationToken);
            result.WsModelsScanned = scan.WsModelsScanned;
            result.UnreadableWsModels = scan.UnreadableWsModels;

            var materials = new Dictionary<string, VanillaCoverageMaterial>(
                StringComparer.OrdinalIgnoreCase);
            VanillaCoverageMaterial GetMaterial(string path)
            {
                if (materials.TryGetValue(path, out var cached))
                    return cached;

                var file = ResolveGameplayTraversalContainer(state.Source, containers, path)?
                    .FindFile(path);
                VanillaCoverageMaterial material;
                if (file == null)
                {
                    material = new VanillaCoverageMaterial(path, false, false, []);
                }
                else
                {
                    try
                    {
                        var textures = new List<(string Slot, string Texture)>();
                        var nodes = LoadXml(file).SelectNodes("/material/textures/texture");
                        if (nodes != null)
                        {
                            foreach (XmlNode node in nodes)
                            {
                                var texture = Normalize(
                                    node.SelectSingleNode("source")?.InnerText ?? node.InnerText);
                                if (IsTextureDownscaleTexturePath(texture))
                                    textures.Add((GetTextureSlot(node), texture));
                            }
                        }

                        material = new VanillaCoverageMaterial(path, true, true, textures);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        material = new VanillaCoverageMaterial(path, true, false, []);
                    }
                }

                materials[path] = material;
                return material;
            }

            var textureEntries = new Dictionary<string, VanillaCoverageTexture>(
                StringComparer.OrdinalIgnoreCase);
            VanillaCoverageTexture GetEntry(string path)
            {
                if (!textureEntries.TryGetValue(path, out var entry))
                {
                    entry = new VanillaCoverageTexture(path);
                    textureEntries[path] = entry;
                }

                return entry;
            }

            // Textures of each distinct (rigid, LOD, part), over all its materials.
            var texturesByPart = new Dictionary<
                (string Rigid, int Lod, int Part),
                HashSet<VanillaCoverageTexture>>();
            foreach (var binding in scan.Bindings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var material = GetMaterial(binding.Material);
                if (!material.Readable)
                    continue;

                var complete = binding.Kind == SourceWsModelBindingKind.Complete;
                if (!complete)
                    result.BindingsWithoutPart++;

                foreach (var (slot, textureName) in material.Textures)
                {
                    var entry = GetEntry(textureName);
                    entry.Materials.Add(material.Path);
                    entry.WsModels.Add(binding.WsModelPath);
                    if (entry.BadSlot == null && !IsTextureDownscaleSlot(slot))
                        entry.BadSlot = $"{slot} in {material.Path}";

                    if (!complete)
                    {
                        entry.UnassessableUses++;
                        continue;
                    }

                    var key = (binding.Geometry, binding.Lod, binding.Part);
                    if (!texturesByPart.TryGetValue(key, out var partTextures))
                    {
                        partTextures = [];
                        texturesByPart[key] = partTextures;
                    }

                    partTextures.Add(entry);
                }
            }

            result.Bindings = scan.Bindings;
            result.MaterialsReferenced = materials.Count;
            result.MaterialsNotFound = materials.Values.Count(material => !material.Found);
            result.MaterialsUnreadable = materials.Values.Count(
                material => material.Found && !material.Readable);
            foreach (var pair in materials)
                result.Materials[pair.Key] = pair.Value;
            foreach (var pair in textureEntries)
                result.Entries[pair.Key] = pair.Value;

            // 2. Which of the referenced textures are vanilla, and their headers.
            result.TexturesReferenced = textureEntries.Count;
            var targets = new Dictionary<string, VanillaCoverageTexture>(
                StringComparer.OrdinalIgnoreCase);
            // Vanilla targets plus owned textures with a readable header: the geometry pass
            // serves both the coverage section and the per-unit repack estimate.
            var repackTargets = new Dictionary<string, VanillaCoverageTexture>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var entry in textureEntries.Values
                         .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var container = ResolveGameplayTraversalContainer(
                    state.Source, containers, entry.Path);
                var file = container?.FindFile(entry.Path);
                if (container == null || file == null)
                {
                    result.NotFound++;
                    continue;
                }

                if (ReferenceEquals(container, state.Source))
                {
                    result.OwnedBySourcePack++;
                    var ownedHeader = ReadTextureDownscaleHeader(entry.Path, file);
                    if (ownedHeader.Layout != null)
                    {
                        entry.Layout = ownedHeader.Layout;
                        entry.IsOwned = true;
                        repackTargets[entry.Path] = entry;
                    }

                    continue;
                }

                var header = ReadTextureDownscaleHeader(entry.Path, file);
                if (header.Layout == null)
                {
                    result.HeaderUnreadable++;
                    continue;
                }

                entry.Layout = header.Layout;
                targets[entry.Path] = entry;
                repackTargets[entry.Path] = entry;
            }

            // Source-pack material files that mention an owned texture (a bound material is
            // already parsed), and owned textures that can never be called removable.
            if (repackTargets.Values.Any(entry => entry.IsOwned))
            {
                foreach (var (rawPath, file) in sourceFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = Normalize(rawPath);
                    if (!path.EndsWith(".xml.material", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var material = GetMaterial(path);
                    if (material.Readable)
                    {
                        foreach (var (_, textureName) in material.Textures)
                        {
                            if (!result.OwnedMaterialMentions.TryGetValue(textureName, out var mentions))
                            {
                                mentions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                result.OwnedMaterialMentions[textureName] = mentions;
                            }

                            mentions.Add(path);
                        }

                        continue;
                    }

                    try
                    {
                        result.OwnedRemovalBlockers.UnionWith(ExtractDownscaleTextureTokens(file));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Unreadable and unscannable: nothing owned is then provably removable.
                        result.OwnedRemovalBlockers.UnionWith(
                            repackTargets.Values.Where(item => item.IsOwned).Select(item => item.Path));
                    }
                }

                foreach (var owned in repackTargets.Values.Where(item => item.IsOwned))
                {
                    if (containers.Any(container =>
                            container.IsCaPackFile &&
                            !ReferenceEquals(container, state.Source) &&
                            container.ContainsFile(owned.Path)) ||
                        state.Consumers?.IsExternal(Normalize(owned.Path)) == true)
                    {
                        result.OwnedRemovalBlockers.Add(owned.Path);
                    }
                }
            }

            // 3. Mentions in non-material files of the pack (VMD decals, text).
            if (repackTargets.Count != 0)
            {
                foreach (var (rawPath, file) in sourceFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = Normalize(rawPath);
                    if (!IsTextureDownscaleTextFile(path))
                        continue;

                    result.TextFilesScanned++;
                    try
                    {
                        foreach (var token in ExtractDownscaleTextureTokens(file))
                        {
                            if (repackTargets.TryGetValue(token, out var target))
                                target.NonMaterialReference ??= path;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        result.TextFilesUnreadable++;
                    }
                }
            }

            // 4. Geometry: one fresh read per rigid that has a vanilla-target user.
            var rigidGroups = texturesByPart
                .Select(pair => (
                    pair.Key.Rigid,
                    pair.Key.Lod,
                    pair.Key.Part,
                    Targets: pair.Value.Where(entry => repackTargets.ContainsKey(entry.Path)).ToList()))
                .Where(part => part.Targets.Count != 0)
                .GroupBy(part => part.Rigid, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var scratch = CreateVanillaCoverageGrid();
            for (var groupIndex = 0; groupIndex < rigidGroups.Count; groupIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var group = rigidGroups[groupIndex];
                if (groupIndex % 10 == 0 || groupIndex == rigidGroups.Count - 1)
                {
                    ReportProgress(
                        progress,
                        "Vanilla texture coverage",
                        groupIndex + 1,
                        rigidGroups.Count,
                        group.Key);
                }

                if (timer.Elapsed > result.Budget)
                {
                    result.BudgetExhausted = true;
                    result.RigidsSkippedForTime++;
                    foreach (var part in group)
                    {
                        foreach (var entry in part.Targets)
                            entry.NotReached = true;
                    }

                    continue;
                }

                RmvFile? rigid;
                try
                {
                    rigid = loadRigid(group.Key);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    rigid = null;
                }

                if (rigid?.ModelList == null)
                {
                    result.RigidsUnavailable++;
                    foreach (var part in group)
                    {
                        result.PartsUnassessable++;
                        foreach (var entry in part.Targets)
                            entry.UnassessableUses++;
                    }

                    continue;
                }

                result.RigidsRead++;
                var modOwned = state.Source.ContainsFile(group.Key);
                foreach (var part in group)
                {
                    if (part.Lod < 0 || part.Lod >= rigid.ModelList.Length ||
                        part.Part < 0 || part.Part >= rigid.ModelList[part.Lod].Length)
                    {
                        result.PartsUnassessable++;
                        foreach (var entry in part.Targets)
                            entry.UnassessableUses++;
                        continue;
                    }

                    var model = rigid.ModelList[part.Lod][part.Part];
                    Array.Clear(scratch);
                    if (!TryMarkVanillaPartCoverage(
                            model, scratch, out var tiled, out var triangles, out var degenerate))
                    {
                        result.DegenerateTriangles += degenerate;
                        result.PartsUnassessable++;
                        foreach (var entry in part.Targets)
                            entry.UnassessableUses++;
                        continue;
                    }

                    result.PartsAssessed++;
                    result.PartData[(group.Key, part.Lod, part.Part)] =
                        new VanillaCoveragePart(scratch.ToArray(), tiled);
                    result.TrianglesMarked += triangles;
                    result.DegenerateTriangles += degenerate;
                    UvBounds? bounds = null;
                    try
                    {
                        bounds = GetUvBounds(model);
                    }
                    catch (InvalidOperationException)
                    {
                        // Cannot happen after a successful triangle pass; the rectangle
                        // figure is then simply missing for this part.
                    }

                    foreach (var entry in part.Targets)
                    {
                        for (var word = 0; word < scratch.Length; word++)
                            entry.Coverage[word] |= scratch[word];

                        if (bounds.HasValue)
                        {
                            MarkVanillaBoundingRectangle(
                                entry.BoundingRectangles,
                                bounds.Value,
                                entry.Layout!.Width,
                                entry.Layout.Height);
                        }

                        entry.Tiled |= tiled;
                        (modOwned ? entry.ModParts : entry.VanillaParts)
                            .Add((group.Key, part.Lod, part.Part));
                    }
                }
            }

            // 5. Per-texture figures; the targets split into not reached and assessed.
            foreach (var entry in repackTargets.Values
                         .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (entry.NotReached)
                {
                    if (!entry.IsOwned)
                        result.NotReached++;
                    continue;
                }

                var layout = entry.Layout!;
                entry.CoverageFraction = CountVanillaCoverage(entry.Coverage);
                entry.BoundingRectangleFraction = CountVanillaCoverage(entry.BoundingRectangles);
                entry.OriginalBytes = layout.ChainBytes;
                EstimateVanillaCrop(
                    layout.Width,
                    layout.Height,
                    layout.BytesPerBlock,
                    layout.ChainBytes,
                    entry.CoverageFraction,
                    out var cropWidth,
                    out var cropHeight,
                    out var cropBytes);
                entry.CropWidth = cropWidth;
                entry.CropHeight = cropHeight;
                entry.CropBytes = cropBytes;
                entry.NotCroppableReason = GetVanillaCoverageNotCroppableReason(entry);
                entry.Finalized = true;
                if (!entry.IsOwned)
                    result.Assessed.Add(entry);
            }
        }

        // ---------------------------------------------------------------------------
        // Report.
        // ---------------------------------------------------------------------------

        private static void AppendVanillaTextureCoverageSection(StringBuilder sb, BatchState state)
        {
            sb.AppendLine("Vanilla texture coverage");
            sb.AppendLine("------------------------");
            var result = state.VanillaTextureCoverage;
            if (result == null)
            {
                sb.AppendLine("Vanilla texture coverage analysis did not run.");
                sb.AppendLine();
                return;
            }

            if (result.Failure != null)
            {
                sb.AppendLine(
                    $"Vanilla texture coverage analysis failed and was skipped: {result.Failure}");
                sb.AppendLine("No pack file was changed.");
                sb.AppendLine();
                return;
            }

            sb.AppendLine(
                "Mode: REPORT ONLY. No pack file was changed. It measures how much of each " +
                "vanilla texture the pack's parts touch, to judge a smaller replacement texture " +
                "that holds only those regions (video memory only, not frame rate).");
            sb.AppendLine(
                "Scope: textures that the source pack's WSModels reach through a material (the " +
                "material may live in the pack or in vanilla) and that resolve in a vanilla " +
                "container. Parts are taken as authored from the source WSModels and fresh " +
                "rigid reads (every LOD, UV0); parts atlased, merged or pruned in this run are " +
                "not excluded, so the figures do not depend on the atlas options. Direct " +
                "rigid use and embedded rigid materials are not assessed.");
            sb.AppendLine(
                $"Scanned: {result.WsModelsScanned:N0} source WSModel(s) (unreadable " +
                $"{result.UnreadableWsModels:N0}), {result.MaterialsReferenced:N0} material(s) " +
                $"(not found {result.MaterialsNotFound:N0}, unreadable " +
                $"{result.MaterialsUnreadable:N0}), {result.RigidsRead:N0} rigid(s) read " +
                $"(unavailable {result.RigidsUnavailable:N0}), {result.TextFilesScanned:N0} text " +
                $"file(s) for direct texture mentions (unreadable {result.TextFilesUnreadable:N0}); " +
                $"parts assessed {result.PartsAssessed:N0}, not assessable " +
                $"{result.PartsUnassessable:N0}, WSModel bindings without a part index " +
                $"{result.BindingsWithoutPart:N0}; {result.TrianglesMarked:N0} triangle(s) " +
                $"rasterised, {result.DegenerateTriangles:N0} degenerate skipped");
            sb.AppendLine(
                $"Analysis time: {result.AnalysisElapsed.TotalMilliseconds:N0} ms of a " +
                $"{result.Budget.TotalSeconds:N0} s budget");
            if (result.BudgetExhausted)
            {
                sb.AppendLine(
                    $"The time budget ran out: {result.RigidsSkippedForTime:N0} rigid(s) were " +
                    $"not read, so {result.NotReached:N0} texture(s) that depend on them have " +
                    "no figures. The numbers below are incomplete.");
            }

            sb.AppendLine(
                $"Method: coverage is the share of a {VanillaCoverageGridSize}x" +
                $"{VanillaCoverageGridSize} cell grid over the texture that any triangle of any " +
                "part and LOD touches (conservative, UVs wrapped modulo 1). Bounding-rectangle " +
                "coverage is the same grid for one crop rectangle per part, the atlas stage's " +
                "approach. The crop estimate is the smallest same-aspect rectangle (halving " +
                $"both sides, at least {VanillaCoverageMinimumSide} px) holding coverage x pixels x " +
                $"{VanillaCoveragePackingSlack:0.00} packing slack, and is never larger than " +
                "the original. A texture is not croppable when a part tiles it, a user cannot " +
                "be assessed, it is used in a non-atlasable slot, or a non-material file of " +
                "the pack mentions it.");

            var assessed = result.Assessed;
            var croppable = result.Croppable.ToList();
            var notCroppable = assessed.Where(texture => !texture.IsCroppable).ToList();
            sb.AppendLine(
                $"Textures referenced through the pack's WSModel materials: " +
                $"{result.TexturesReferenced:N0}. They split exactly into:");
            sb.AppendLine(
                $"  Owned by the source pack (not assessed here): {result.OwnedBySourcePack:N0}");
            sb.AppendLine(
                $"  Not found in any loaded container: {result.NotFound:N0}");
            sb.AppendLine(
                $"  Vanilla, header unreadable or layout unsupported: {result.HeaderUnreadable:N0}");
            sb.AppendLine(
                $"  Vanilla, not reached (time budget): {result.NotReached:N0}");
            sb.AppendLine(
                $"  Vanilla, assessed: {assessed.Count:N0}, which split exactly into:");
            sb.AppendLine($"    Croppable: {croppable.Count:N0}");
            sb.AppendLine($"    Not croppable: {notCroppable.Count:N0}");

            sb.AppendLine("Reasons an assessed texture is not croppable (first applicable):");
            foreach (var group in notCroppable
                         .GroupBy(texture => texture.NotCroppableReason!)
                         .OrderByDescending(group => group.Count())
                         .ThenBy(group => group.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"  {group.Count():N0} x {group.Key}");
                foreach (var example in group
                             .Select(texture => texture.Path)
                             .OrderBy(path => path, StringComparer.Ordinal)
                             .Take(VanillaCoverageExampleLimit))
                {
                    sb.AppendLine($"      e.g. {example}");
                }
            }

            var buckets = BuildVanillaCoverageBuckets(croppable);
            var originalBytes = croppable.Sum(texture => texture.OriginalBytes);
            var cropBytes = croppable.Sum(texture => texture.CropBytes);
            sb.AppendLine(
                $"Croppable set, BCn bytes with the whole mip chain: {originalBytes:N0} -> " +
                $"{cropBytes:N0} (saves {originalBytes - cropBytes:N0}). This is an upper bound: " +
                "it assumes nothing else keeps the vanilla texture loaded. The four coverage " +
                "buckets below sum to these totals.");
            sb.AppendLine(
                "Croppable textures by coverage (vanilla bytes today; replacement bytes = what " +
                "would be ADDED to the pack; saved = video memory if the vanilla textures are " +
                "released, upper bound):");
            foreach (var bucket in buckets)
            {
                sb.AppendLine(
                    $"  {bucket.Label}: {bucket.Textures.Count:N0} texture(s), vanilla " +
                    $"{bucket.OriginalBytes:N0} bytes, replacements added to the pack " +
                    $"{bucket.CropBytes:N0} bytes, saves {bucket.BytesSaved:N0}");
            }

            sb.AppendLine(
                $"  Sum of the buckets: {buckets.Sum(bucket => bucket.Textures.Count):N0} " +
                $"texture(s), vanilla {buckets.Sum(bucket => bucket.OriginalBytes):N0} bytes, " +
                $"replacements {buckets.Sum(bucket => bucket.CropBytes):N0} bytes, saves " +
                $"{buckets.Sum(bucket => bucket.BytesSaved):N0}");
            sb.AppendLine("Restricted variants (crop only the textures below a coverage limit):");
            for (var variant = 0; variant < 2; variant++)
            {
                var included = buckets.Take(variant + 1).SelectMany(bucket => bucket.Textures).ToList();
                var modRigids = included
                    .SelectMany(texture => texture.ModParts.Select(part => part.Rigid))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var vanillaRigids = included
                    .SelectMany(texture => texture.VanillaParts.Select(part => part.Rigid))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                sb.AppendLine(
                    $"  Coverage below {(variant == 0 ? 10 : 25)} %: {included.Count:N0} " +
                    $"texture(s), saves {included.Sum(texture => texture.BytesSaved):N0} bytes " +
                    $"(upper bound), adds {included.Sum(texture => texture.CropBytes):N0} bytes " +
                    $"to the pack, {modRigids.Count:N0} distinct mod-owned rigid(s) to rewrite, " +
                    $"{vanillaRigids.Count:N0} distinct vanilla rigid(s) needing generated copies");
            }

            sb.AppendLine(
                $"Stays loaded anyway (other consumers keep the texture resident in a battle): " +
                $"not computed ({result.StaysLoadedNotComputedReason}). No weighted saving is " +
                "given.");

            if (croppable.Count != 0)
            {
                var ordered = croppable
                    .OrderByDescending(texture => texture.BytesSaved)
                    .ThenBy(texture => texture.Path, StringComparer.Ordinal)
                    .ToList();
                sb.AppendLine(
                    ordered.Count > VanillaCoverageListLimit
                        ? $"Top {VanillaCoverageListLimit} croppable textures by bytes saved:"
                        : "All croppable textures by bytes saved:");
                foreach (var texture in ordered.Take(VanillaCoverageListLimit))
                {
                    var layout = texture.Layout!;
                    sb.AppendLine(
                        $"  {texture.Path}: {layout.Width}x{layout.Height} " +
                        $"{layout.FormatDescription}, {texture.OriginalBytes:N0} bytes, coverage " +
                        $"{texture.CoverageFraction:P1} (bounding rectangles " +
                        $"{texture.BoundingRectangleFraction:P1}), crop " +
                        $"{texture.CropWidth}x{texture.CropHeight} = {texture.CropBytes:N0} " +
                        $"bytes, saves {texture.BytesSaved:N0}");
                    sb.AppendLine(
                        $"      users: {texture.Materials.Count:N0} material(s), " +
                        $"{texture.AssessedParts:N0} part(s) ({texture.ModParts.Count:N0} on " +
                        $"mod-owned rigids, {texture.VanillaParts.Count:N0} on vanilla rigids " +
                        "that would need generated copies)");
                }

                if (ordered.Count > VanillaCoverageListLimit)
                    sb.AppendLine($"  ... and {ordered.Count - VanillaCoverageListLimit:N0} more");
            }

            sb.AppendLine();
        }
    }
}
