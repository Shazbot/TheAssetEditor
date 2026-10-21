using System.Diagnostics;
using System.Text;

namespace Editors.KitbasherEditor.Services
{
    public sealed partial class PackTextureAtlasBatchService
    {
        // Report-only "Per-unit texture repack estimate". Instead of cropping one texture at a
        // time, build ONE atlas set per unit (gameplay VMD root) from every texture region its
        // parts use, across all slots and all alternatives, and pack them together. Memory
        // only, not frame rate. It reads nothing itself: parts, bindings, materials, headers
        // and the per-part UV grids all come from the vanilla texture coverage stage's single
        // collection pass (no rigid is loaded twice). It changes no pack file.
        private const double UnitRepackBudgetSeconds = 45;
        private const int UnitRepackListLimit = 30;
        private const int UnitRepackPairLimit = 20;
        private const int UnitRepackRigidLimit = 10;
        private const int UnitRepackGroupLimit = 5;
        // Pair statistics keep two triangular matrices; beyond this many units they are skipped.
        private const int UnitRepackMaximumPairUnits = 2000;
        // Option C2 merges signature sets while their Jaccard similarity is at least 8/10;
        // above this many sets the merge is not attempted.
        private const int UnitRepackMergeNumerator = 8;
        private const int UnitRepackMergeDenominator = 10;
        private const int UnitRepackMaximumMergeSets = 3000;

        // The parts of one unit that use one texture, and the slot it is attributed to.
        private sealed class UnitRepackTextureUse
        {
            public HashSet<(string Rigid, int Lod, int Part)> Parts { get; } = [];
            public string Slot { get; set; } = string.Empty;
        }

        private sealed class UnitRepackUnit
        {
            public UnitRepackUnit(string root)
            {
                Root = root;
            }

            public string Root { get; }
            public int Index { get; set; }
            public TextureDownscaleRootKind? Kind { get; set; }
            public HashSet<string> WsModels { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<VanillaCoverageTexture, UnitRepackTextureUse> Textures { get; } = [];
            public HashSet<string> Rigids { get; } = new(StringComparer.OrdinalIgnoreCase);
            // Rigids with a part that uses a repackable texture (they need new UVs).
            public HashSet<string> TouchedRigids { get; } = new(StringComparer.OrdinalIgnoreCase);

            // Loaded today: distinct textures of known size; the rest have an unknown size.
            public long Today { get; set; }
            public int UnknownSizeTextures { get; set; }
            public long KeptBytes { get; set; }
            public int RepackableTextures { get; set; }
            // Option A: this unit's own atlas set.
            public long AtlasRaw { get; set; }
            public long AtlasRounded { get; set; }
            // Coverage-weighted fill: sum(coverage x bytes) / sum(bytes) over the repackable textures.
            public double Fill { get; set; }
            public int SharedRigids { get; set; }
            // Option B.
            public int Group { get; set; }
            // Own coverage per repackable texture, and the raw bytes the unit's parts need.
            public Dictionary<VanillaCoverageTexture, double> OwnCoverage { get; } = [];
            public double NeededTextureRaw { get; set; }
            // Options C (0) and C2 (1): signature-set atlases that contain this unit.
            public long[] SetsRaw { get; } = new long[2];
            public long[] SetsRounded { get; } = new long[2];
            public double[] LoadedTextureRaw { get; } = new double[2];
            public double[] Waste { get; } = new double[2];
            public List<int>[] SetIndexes { get; } = [[], []];

            public long AfterVariantRaw(int variant) => SetsRaw[variant] + KeptBytes;
            public long AfterVariant(int variant) => SetsRounded[variant] + KeptBytes;

            public long AfterARaw => AtlasRaw + KeptBytes;
            public long AfterA => AtlasRounded + KeptBytes;
            public long SavingA => Today - AfterA;
        }

        private sealed class UnitRepackGroup
        {
            public List<UnitRepackUnit> Units { get; } = [];
            public long AtlasRaw { get; set; }
            public long AtlasRounded { get; set; }
        }

        // Textures with the same signature (the exact set of units that use them).
        private sealed class UnitRepackSignatureSet
        {
            public int[] Units { get; set; } = [];
            public List<(VanillaCoverageTexture Entry, string Slot, double Coverage)> Items { get; } = [];
            // One atlas per (slot, format, sRGB) group.
            public List<(long Raw, long Rounded)> Atlases { get; set; } = [];

            public long Raw => Atlases.Sum(atlas => atlas.Raw);
            public long Rounded => Atlases.Sum(atlas => atlas.Rounded);
        }

        private sealed class UnitRepackRigid
        {
            public long Bytes { get; set; }
            public bool ModOwned { get; set; }
            public List<UnitRepackUnit> Users { get; } = [];
            public List<UnitRepackUnit> Touching { get; } = [];
        }

        private sealed record UnitRepackPair(
            UnitRepackUnit First,
            UnitRepackUnit Second,
            long SharedBytes,
            long Today,
            long After,
            long AfterC);

        private sealed class UnitTextureRepackResult
        {
            public TimeSpan AnalysisElapsed { get; set; }
            public TimeSpan Budget { get; set; }
            public string? Failure { get; set; }
            public string? UnavailableReason { get; set; }
            public bool BudgetExhausted { get; set; }
            public string? CharacterRuleUnavailableReason { get; set; }

            public int RootsReached { get; set; }
            public int RootsWithoutAssessableParts { get; set; }
            public int UnitsNotReached { get; set; }
            public int UnrootedWsModels { get; set; }
            public int UnrootedParts { get; set; }
            public List<UnitRepackUnit> Units { get; } = [];
            public List<UnitRepackGroup> Groups { get; } = [];
            public Dictionary<string, UnitRepackRigid> Rigids { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            // Distinct textures used by the estimated units.
            public int TexturesUsed { get; set; }
            public int TexturesRepackable { get; set; }
            public int TexturesKept { get; set; }
            public int TexturesKeptUnknownSize { get; set; }
            public List<int> UnitsPerTexture { get; } = [];
            public int RemovableOwnedTextures { get; set; }
            public long RemovableOwnedBytes { get; set; }
            public HashSet<VanillaCoverageTexture> RemovableEntries { get; } = [];
            public long WholePackKept { get; set; }
            public List<UnitRepackSignatureSet> SetsC { get; } = [];
            public List<UnitRepackSignatureSet> SetsC2 { get; } = [];
            public string? C2SkippedReason { get; set; }
            public long WholePackToday { get; set; }
            public long WholePackAfter { get; set; }
            public List<UnitRepackPair> TopPairs { get; } = [];
            public string? PairsSkippedReason { get; set; }
        }

        // ---------------------------------------------------------------------------
        // Pure estimates.
        // ---------------------------------------------------------------------------

        // Atlas bytes for one set of (slot, texture, coverage) items, raw and rounded.
        //   raw     = sum over textures of min(ChainBytes, coverage x ChainBytes x 1.25)
        //   rounded = per (slot, format) group: the needed area is sum of min(w x h,
        //             coverage x w x h x 1.25) pixels; the atlas is the smallest
        //             power-of-two pixel area >= that (a 2^k area is 2^ceil(k/2) x 2^floor(k/2)),
        //             with a full mip chain (CalculateBcnMipChainBytes). The group figure is
        //             never below its raw sum and never above the sum of its textures' bytes.
        private static (long Raw, long Rounded) EstimateRepackAtlasBytes(
            IEnumerable<(string Slot, DdsLayout Layout, double Coverage)> items)
        {
            var atlases = EstimateRepackAtlases(items);
            return (atlases.Sum(atlas => atlas.Raw), atlases.Sum(atlas => atlas.Rounded));
        }

        // The same estimate, one entry per (slot, format, sRGB) atlas.
        private static List<(long Raw, long Rounded)> EstimateRepackAtlases(
            IEnumerable<(string Slot, DdsLayout Layout, double Coverage)> items)
        {
            var atlases = new List<(long Raw, long Rounded)>();
            foreach (var group in items.GroupBy(
                         item => $"{item.Slot}|{item.Layout.Format}|{item.Layout.IsSrgb}",
                         StringComparer.OrdinalIgnoreCase))
            {
                double groupRaw = 0;
                double pixels = 0;
                long chain = 0;
                var bytesPerBlock = 8;
                foreach (var (_, layout, coverage) in group)
                {
                    var fraction = Math.Clamp(coverage, 0, 1);
                    bytesPerBlock = layout.BytesPerBlock;
                    chain += layout.ChainBytes;
                    groupRaw += Math.Min(
                        layout.ChainBytes,
                        fraction * layout.ChainBytes * VanillaCoveragePackingSlack);
                    var area = (double)layout.Width * layout.Height;
                    pixels += Math.Min(area, fraction * area * VanillaCoveragePackingSlack);
                }

                var groupRawBytes = (long)Math.Ceiling(groupRaw);
                var exponent = (int)Math.Clamp(
                    Math.Ceiling(Math.Log2(Math.Max(16.0, pixels))), 4, 60);
                var width = (int)Math.Min(1L << ((exponent + 1) / 2), 1 << 30);
                var height = (int)Math.Min(1L << (exponent / 2), 1 << 30);
                var atlasBytes = CalculateBcnMipChainBytes(width, height, bytesPerBlock);
                atlases.Add((groupRawBytes, Math.Min(chain, Math.Max(groupRawBytes, atlasBytes))));
            }

            return atlases;
        }

        private static string GetUnitRepackSizeClass(int count)
            => count <= 1 ? "1" : count <= 5 ? "2-5" : count <= 20 ? "6-20" : "21+";

        private static readonly string[] UnitRepackSizeClasses = ["1", "2-5", "6-20", "21+"];

        private static long MedianOf(List<long> values)
        {
            if (values.Count == 0)
                return 0;

            var sorted = values.OrderBy(value => value).ToList();
            return sorted.Count % 2 == 1
                ? sorted[sorted.Count / 2]
                : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        }

        // ---------------------------------------------------------------------------
        // Stage entry and analysis.
        // ---------------------------------------------------------------------------

        private void RunUnitTextureRepack(
            BatchState state,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress,
            TimeSpan? budget = null)
        {
            var result = new UnitTextureRepackResult
            {
                Budget = budget ?? TimeSpan.FromSeconds(UnitRepackBudgetSeconds),
            };
            state.UnitTextureRepack = result;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                ReportProgress(progress, "Per-unit texture repack estimate");
                AnalyzeUnitTextureRepack(state, result, cancellationToken, progress);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Diagnostic stage: a failure never fails the run or changes the output.
                result.Failure = $"{ex.GetType().Name}: {ex.Message}";
            }

            result.AnalysisElapsed = stopwatch.Elapsed;
            state.PhaseDurations["Per-unit texture repack estimate"] = result.AnalysisElapsed;
        }

        private static void AnalyzeUnitTextureRepack(
            BatchState state,
            UnitTextureRepackResult result,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var timer = Stopwatch.StartNew();
            var coverage = state.VanillaTextureCoverage;
            if (coverage == null || coverage.Failure != null)
            {
                result.UnavailableReason = coverage == null
                    ? "the vanilla texture coverage stage did not run, so no shared collection exists"
                    : $"the vanilla texture coverage stage failed ({coverage.Failure})";
                return;
            }

            var index = state.GameplayMeshDependencyIndex;
            if (index == null)
            {
                result.UnavailableReason = "the gameplay mesh dependency index was not built";
                return;
            }

            // 1. Units: gameplay roots that reach a source WSModel.
            var bindingsByWs = coverage.Bindings
                .GroupBy(binding => binding.WsModelPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            var rootUnits = new Dictionary<string, UnitRepackUnit>(StringComparer.OrdinalIgnoreCase);
            var unrootedParts = new HashSet<(string, int, int)>();
            foreach (var wsModel in bindingsByWs.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var roots = index.GetRootsForAsset(wsModel)
                    .Concat(index.GameplayRoots.Contains(wsModel)
                        ? new[] { wsModel }
                        : Array.Empty<string>())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (roots.Count == 0)
                {
                    result.UnrootedWsModels++;
                    foreach (var binding in bindingsByWs[wsModel]
                                 .Where(binding => binding.Kind == SourceWsModelBindingKind.Complete))
                    {
                        unrootedParts.Add((binding.Geometry, binding.Lod, binding.Part));
                    }

                    continue;
                }

                foreach (var root in roots)
                {
                    if (!rootUnits.TryGetValue(root, out var unit))
                    {
                        unit = new UnitRepackUnit(root);
                        rootUnits[root] = unit;
                    }

                    unit.WsModels.Add(wsModel);
                }
            }

            result.UnrootedParts = unrootedParts.Count;
            result.RootsReached = rootUnits.Count;

            // 2. Per-unit parts and textures; a unit needs at least one assessed part.
            var candidates = new List<UnitRepackUnit>();
            foreach (var unit in rootUnits.Values.OrderBy(unit => unit.Root, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hasAssessedPart = false;
                foreach (var wsModel in unit.WsModels)
                {
                    foreach (var binding in bindingsByWs[wsModel])
                    {
                        if (binding.Kind != SourceWsModelBindingKind.Complete)
                            continue;

                        var key = (binding.Geometry, binding.Lod, binding.Part);
                        unit.Rigids.Add(binding.Geometry);
                        hasAssessedPart |= coverage.PartData.ContainsKey(key);
                        if (!coverage.Materials.TryGetValue(binding.Material, out var material) ||
                            !material.Readable)
                        {
                            continue;
                        }

                        foreach (var (slot, textureName) in material.Textures)
                        {
                            if (!coverage.Entries.TryGetValue(textureName, out var entry))
                                continue;

                            if (!unit.Textures.TryGetValue(entry, out var use))
                            {
                                use = new UnitRepackTextureUse { Slot = slot };
                                unit.Textures[entry] = use;
                            }
                            else if (string.CompareOrdinal(slot, use.Slot) < 0)
                            {
                                use.Slot = slot;
                            }

                            use.Parts.Add(key);
                        }
                    }
                }

                if (hasAssessedPart)
                    candidates.Add(unit);
                else
                    result.RootsWithoutAssessableParts++;
            }

            // 3. Character classification, when the gameplay resolution allows it.
            result.CharacterRuleUnavailableReason =
                GetTextureDownscaleCharacterRuleUnavailableReason(state);
            if (result.CharacterRuleUnavailableReason == null)
            {
                var classify = BuildTextureDownscaleRootClassifier(state);
                foreach (var unit in candidates)
                    unit.Kind = classify(unit.Root).Kind;
            }

            // 4. Option A: one atlas set per unit.
            var scratch = CreateVanillaCoverageGrid();
            double CoverageOf(IEnumerable<(string Rigid, int Lod, int Part)> parts)
            {
                Array.Clear(scratch);
                foreach (var part in parts)
                {
                    if (!coverage.PartData.TryGetValue(part, out var data))
                        continue;

                    for (var word = 0; word < scratch.Length; word++)
                        scratch[word] |= data.Grid[word];
                }

                return CountVanillaCoverage(scratch);
            }

            for (var unitIndex = 0; unitIndex < candidates.Count; unitIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var unit = candidates[unitIndex];
                if (timer.Elapsed > result.Budget)
                {
                    result.BudgetExhausted = true;
                    result.UnitsNotReached = candidates.Count - unitIndex;
                    break;
                }

                if (unitIndex % 25 == 0 || unitIndex == candidates.Count - 1)
                {
                    ReportProgress(
                        progress,
                        "Per-unit texture repack estimate",
                        unitIndex + 1,
                        candidates.Count,
                        unit.Root);
                }

                unit.Index = result.Units.Count;
                result.Units.Add(unit);
                var items = new List<(string Slot, DdsLayout Layout, double Coverage)>();
                double usedBytes = 0;
                long repackableBytes = 0;
                foreach (var (entry, use) in unit.Textures)
                {
                    if (entry.Layout == null)
                    {
                        unit.UnknownSizeTextures++;
                        continue;
                    }

                    unit.Today += entry.Layout.ChainBytes;
                    if (!entry.IsRepackable)
                    {
                        unit.KeptBytes += entry.Layout.ChainBytes;
                        continue;
                    }

                    var fraction = CoverageOf(use.Parts);
                    unit.OwnCoverage[entry] = fraction;
                    unit.NeededTextureRaw += Math.Min(
                        entry.Layout.ChainBytes,
                        fraction * entry.Layout.ChainBytes * VanillaCoveragePackingSlack);
                    items.Add((use.Slot, entry.Layout, fraction));
                    unit.RepackableTextures++;
                    repackableBytes += entry.Layout.ChainBytes;
                    usedBytes += fraction * entry.Layout.ChainBytes;
                    foreach (var part in use.Parts)
                        unit.TouchedRigids.Add(part.Rigid);
                }

                var atlas = EstimateRepackAtlasBytes(items);
                unit.AtlasRaw = atlas.Raw;
                unit.AtlasRounded = atlas.Rounded;
                unit.Fill = repackableBytes == 0 ? 0 : usedBytes / repackableBytes;
            }

            // 5. Rigids: who uses them, who must rewrite them, and their file sizes.
            var containers = GetGameplayTraversalContainers(state);
            foreach (var unit in result.Units)
            {
                foreach (var rigidPath in unit.Rigids)
                {
                    if (!result.Rigids.TryGetValue(rigidPath, out var rigid))
                    {
                        rigid = new UnitRepackRigid
                        {
                            ModOwned = state.Source.ContainsFile(rigidPath),
                            Bytes = ResolveGameplayTraversalContainer(state.Source, containers, rigidPath)?
                                .FindFile(rigidPath)?.DataSource.Size ?? 0,
                        };
                        result.Rigids[rigidPath] = rigid;
                    }

                    rigid.Users.Add(unit);
                    if (unit.TouchedRigids.Contains(rigidPath))
                        rigid.Touching.Add(unit);
                }
            }

            foreach (var unit in result.Units)
                unit.SharedRigids = unit.Rigids.Count(path => result.Rigids[path].Users.Count > 1);

            // 6. Textures: sharing, removable owned bytes, whole-pack memory.
            var estimatedWsModels = result.Units
                .SelectMany(unit => unit.WsModels)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unitsByTexture = new Dictionary<VanillaCoverageTexture, List<UnitRepackUnit>>();
            foreach (var unit in result.Units)
            {
                foreach (var entry in unit.Textures.Keys)
                {
                    if (!unitsByTexture.TryGetValue(entry, out var users))
                    {
                        users = [];
                        unitsByTexture[entry] = users;
                    }

                    users.Add(unit);
                }
            }

            result.TexturesUsed = unitsByTexture.Count;
            foreach (var (entry, users) in unitsByTexture)
            {
                result.UnitsPerTexture.Add(users.Count);
                if (entry.Layout == null)
                {
                    result.TexturesKeptUnknownSize++;
                    result.TexturesKept++;
                    continue;
                }

                result.WholePackToday += entry.Layout.ChainBytes;
                if (!entry.IsRepackable)
                {
                    result.TexturesKept++;
                    result.WholePackKept += entry.Layout.ChainBytes;
                    result.WholePackAfter += entry.Layout.ChainBytes;
                    continue;
                }

                result.TexturesRepackable++;
                // Conservative: every user must be an estimated unit's WSModel, every
                // source material that mentions it must be bound, and nothing else may
                // name it (unreadable material, vanilla override).
                if (entry.IsOwned &&
                    !coverage.OwnedRemovalBlockers.Contains(entry.Path) &&
                    entry.WsModels.IsSubsetOf(estimatedWsModels) &&
                    (!coverage.OwnedMaterialMentions.TryGetValue(entry.Path, out var mentions) ||
                     mentions.IsSubsetOf(entry.Materials)))
                {
                    result.RemovableEntries.Add(entry);
                    result.RemovableOwnedTextures++;
                    result.RemovableOwnedBytes += entry.Layout.ChainBytes;
                }
            }

            result.WholePackAfter += result.Units.Sum(unit => unit.AtlasRounded);

            // 6b. Option C: textures with the same signature (the exact set of units that
            // use them) share atlases; each texture is placed once, with its coverage over
            // the parts of all its users.
            var setsBySignature = new Dictionary<string, UnitRepackSignatureSet>(StringComparer.Ordinal);
            foreach (var (entry, users) in unitsByTexture
                         .Where(pair => pair.Key.IsRepackable)
                         .OrderBy(pair => pair.Key.Path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parts = new HashSet<(string Rigid, int Lod, int Part)>();
                var slot = users[0].Textures[entry].Slot;
                foreach (var user in users)
                {
                    var use = user.Textures[entry];
                    parts.UnionWith(use.Parts);
                    if (string.CompareOrdinal(use.Slot, slot) < 0)
                        slot = use.Slot;
                }

                var signature = string.Join(",", users.Select(user => user.Index));
                if (!setsBySignature.TryGetValue(signature, out var set))
                {
                    set = new UnitRepackSignatureSet { Units = users.Select(user => user.Index).ToArray() };
                    setsBySignature[signature] = set;
                    result.SetsC.Add(set);
                }

                set.Items.Add((entry, slot, CoverageOf(parts)));
            }

            foreach (var set in result.SetsC)
            {
                set.Atlases = EstimateRepackAtlases(
                    set.Items.Select(item => (item.Slot, item.Entry.Layout!, item.Coverage)));
            }

            ApplyUnitRepackSets(result, result.SetsC, 0);

            // 6c. Option C2: merge signature sets that are almost the same set of units.
            var merge = MergeRepackSignatures(
                result.SetsC.Select(set => set.Units).ToList(),
                result.Units.Count,
                () => timer.Elapsed > result.Budget);
            if (merge.Groups == null)
            {
                result.C2SkippedReason = merge.SkippedReason;
                result.BudgetExhausted |= merge.SkippedReason == UnitRepackMergeTimeReason;
            }
            else
            {
                foreach (var members in merge.Groups)
                {
                    var merged = new UnitRepackSignatureSet
                    {
                        Units = members.SelectMany(member => result.SetsC[member].Units)
                            .Distinct().OrderBy(unit => unit).ToArray(),
                    };
                    foreach (var member in members)
                        merged.Items.AddRange(result.SetsC[member].Items);

                    merged.Atlases = EstimateRepackAtlases(
                        merged.Items.Select(item => (item.Slot, item.Entry.Layout!, item.Coverage)));
                    result.SetsC2.Add(merged);
                }

                ApplyUnitRepackSets(result, result.SetsC2, 1);
            }

            // 7. Pairs that share the most texture bytes.
            if (result.Units.Count > UnitRepackMaximumPairUnits)
            {
                result.PairsSkippedReason =
                    $"{result.Units.Count:N0} units exceed the {UnitRepackMaximumPairUnits:N0}-unit limit";
            }
            else if (result.Units.Count > 1)
            {
                var sharedAll = new long[result.Units.Count][];
                var sharedKept = new long[result.Units.Count][];
                for (var row = 0; row < result.Units.Count; row++)
                {
                    sharedAll[row] = new long[result.Units.Count - row - 1];
                    sharedKept[row] = new long[result.Units.Count - row - 1];
                }

                foreach (var (entry, users) in unitsByTexture)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.Layout == null || users.Count < 2)
                        continue;

                    if (timer.Elapsed > result.Budget)
                    {
                        result.BudgetExhausted = true;
                        result.PairsSkippedReason = "the time budget ran out";
                        break;
                    }

                    var bytes = entry.Layout.ChainBytes;
                    for (var a = 0; a < users.Count; a++)
                    {
                        for (var b = a + 1; b < users.Count; b++)
                        {
                            var slot = users[b].Index - users[a].Index - 1;
                            sharedAll[users[a].Index][slot] += bytes;
                            if (!entry.IsRepackable)
                                sharedKept[users[a].Index][slot] += bytes;
                        }
                    }
                }

                if (result.PairsSkippedReason == null)
                {
                    var pairs = new List<(int A, int B, long Shared)>();
                    for (var a = 0; a < result.Units.Count; a++)
                    {
                        for (var b = a + 1; b < result.Units.Count; b++)
                        {
                            var shared = sharedAll[a][b - a - 1];
                            if (shared > 0)
                                pairs.Add((a, b, shared));
                        }
                    }

                    foreach (var pair in pairs
                                 .OrderByDescending(pair => pair.Shared)
                                 .ThenBy(pair => pair.A)
                                 .ThenBy(pair => pair.B)
                                 .Take(UnitRepackPairLimit))
                    {
                        var first = result.Units[pair.A];
                        var second = result.Units[pair.B];
                        var keptUnion = first.KeptBytes + second.KeptBytes -
                                        sharedKept[pair.A][pair.B - pair.A - 1];
                        var setsOfPair = first.SetIndexes[0].Union(second.SetIndexes[0]);
                        result.TopPairs.Add(new UnitRepackPair(
                            first,
                            second,
                            pair.Shared,
                            first.Today + second.Today - pair.Shared,
                            first.AtlasRounded + second.AtlasRounded + keptUnion,
                            setsOfPair.Sum(index => result.SetsC[index].Rounded) + keptUnion));
                    }
                }
            }

            // 8. Option B: units that use a common rigid share one atlas set.
            var parent = Enumerable.Range(0, result.Units.Count).ToArray();
            int Find(int node)
            {
                while (parent[node] != node)
                {
                    parent[node] = parent[parent[node]];
                    node = parent[node];
                }

                return node;
            }

            foreach (var rigid in result.Rigids.Values)
            {
                for (var user = 1; user < rigid.Users.Count; user++)
                    parent[Find(rigid.Users[user].Index)] = Find(rigid.Users[0].Index);
            }

            foreach (var unitsOfRoot in result.Units.GroupBy(unit => Find(unit.Index)).OrderBy(group => group.Min(unit => unit.Index)))
            {
                var group = new UnitRepackGroup();
                group.Units.AddRange(unitsOfRoot);
                foreach (var unit in group.Units)
                    unit.Group = result.Groups.Count;

                var merged = new Dictionary<VanillaCoverageTexture, UnitRepackTextureUse>();
                foreach (var unit in group.Units)
                {
                    foreach (var (entry, use) in unit.Textures)
                    {
                        if (!entry.IsRepackable)
                            continue;

                        if (!merged.TryGetValue(entry, out var mergedUse))
                        {
                            mergedUse = new UnitRepackTextureUse { Slot = use.Slot };
                            merged[entry] = mergedUse;
                        }
                        else if (string.CompareOrdinal(use.Slot, mergedUse.Slot) < 0)
                        {
                            mergedUse.Slot = use.Slot;
                        }

                        mergedUse.Parts.UnionWith(use.Parts);
                    }
                }

                var items = merged
                    .Select(pair => (pair.Value.Slot, pair.Key.Layout!, CoverageOf(pair.Value.Parts)))
                    .ToList();
                var atlas = EstimateRepackAtlasBytes(items);
                group.AtlasRaw = atlas.Raw;
                group.AtlasRounded = atlas.Rounded;
                result.Groups.Add(group);
            }
        }

        // ---------------------------------------------------------------------------
        // Summaries (shared by the all-units and rank-and-file blocks).
        // ---------------------------------------------------------------------------

        private sealed record UnitRepackSummary(
            int Units,
            long Today,
            long AfterARaw,
            long AfterA,
            long AtlasAdded,
            int CopiesMod,
            int CopiesVanilla,
            long CopyBytesMod,
            long CopyBytesVanilla,
            long AfterB,
            int Better,
            int Worse,
            long SavingB,
            long MedianSavingB,
            long MedianSavingA,
            double MeanNotNeeded,
            long GroupAtlasAdded,
            int Groups);

        private static UnitRepackSummary SummarizeUnitRepack(
            UnitTextureRepackResult result,
            IReadOnlyList<UnitRepackUnit> units)
        {
            var members = units.Select(unit => unit.Index).ToHashSet();
            int copiesMod = 0, copiesVanilla = 0;
            long copyBytesMod = 0, copyBytesVanilla = 0;
            foreach (var rigid in result.Rigids.Values)
            {
                var touching = rigid.Touching.Count(unit => members.Contains(unit.Index));
                var using_ = rigid.Users.Count(unit => members.Contains(unit.Index));
                if (touching == 0)
                    continue;

                // The original stays with a unit that needs no new UVs, else with one of them.
                var copies = using_ > touching ? touching : touching - 1;
                if (rigid.ModOwned)
                {
                    copiesMod += copies;
                    copyBytesMod += copies * rigid.Bytes;
                }
                else
                {
                    copiesVanilla += copies;
                    copyBytesVanilla += copies * rigid.Bytes;
                }
            }

            var groupIndexes = units.Select(unit => unit.Group).Distinct().ToList();
            var savingsB = new List<long>();
            var notNeeded = new List<double>();
            long afterB = 0;
            foreach (var unit in units)
            {
                var group = result.Groups[unit.Group];
                var after = group.AtlasRounded + unit.KeptBytes;
                afterB += after;
                savingsB.Add(unit.Today - after);
                notNeeded.Add(group.AtlasRaw <= 0
                    ? 0
                    : Math.Max(0, 1 - (double)unit.AtlasRaw / group.AtlasRaw));
            }

            return new UnitRepackSummary(
                units.Count,
                units.Sum(unit => unit.Today),
                units.Sum(unit => unit.AfterARaw),
                units.Sum(unit => unit.AfterA),
                units.Sum(unit => unit.AtlasRounded),
                copiesMod,
                copiesVanilla,
                copyBytesMod,
                copyBytesVanilla,
                afterB,
                savingsB.Count(saving => saving > 0),
                savingsB.Count(saving => saving < 0),
                savingsB.Sum(),
                MedianOf(savingsB),
                MedianOf(units.Select(unit => unit.SavingA).ToList()),
                notNeeded.Count == 0 ? 0 : notNeeded.Average(),
                groupIndexes.Sum(index => result.Groups[index].AtlasRounded),
                groupIndexes.Count);
        }

        private const string UnitRepackMergeTimeReason = "the time budget ran out";

        // Adds a variant's atlases to every unit that loads them, and the unit's residual
        // waste: the share of the raw bytes it loads that its own parts do not touch.
        private static void ApplyUnitRepackSets(
            UnitTextureRepackResult result,
            List<UnitRepackSignatureSet> sets,
            int variant)
        {
            for (var setIndex = 0; setIndex < sets.Count; setIndex++)
            {
                var set = sets[setIndex];
                var setRaw = set.Raw;
                var setRounded = set.Rounded;
                double textureRaw = 0;
                foreach (var item in set.Items)
                {
                    var chain = item.Entry.Layout!.ChainBytes;
                    textureRaw += Math.Min(chain, item.Coverage * chain * VanillaCoveragePackingSlack);
                }

                foreach (var unitIndex in set.Units)
                {
                    var unit = result.Units[unitIndex];
                    unit.SetIndexes[variant].Add(setIndex);
                    unit.SetsRaw[variant] += setRaw;
                    unit.SetsRounded[variant] += setRounded;
                    unit.LoadedTextureRaw[variant] += textureRaw;
                }
            }

            foreach (var unit in result.Units)
            {
                unit.Waste[variant] = unit.LoadedTextureRaw[variant] <= 0
                    ? 0
                    : Math.Max(0, 1 - unit.NeededTextureRaw / unit.LoadedTextureRaw[variant]);
            }
        }

        // Greedy, deterministic merge of signature sets (each a sorted array of unit
        // indexes). While any two live sets have Jaccard similarity >= 8/10 (checked as
        // |intersection| x 10 >= |union| x 8), the pair with the highest similarity is
        // merged (signature = union of the two); ties go to the lower set id, then the
        // lower partner id, input sets having ids 0..n-1 and each merged set the next
        // free id. Returns the merged groups as input indexes, ordered by lowest member.
        private static (List<List<int>>? Groups, string? SkippedReason) MergeRepackSignatures(
            IReadOnlyList<int[]> signatures,
            int unitCount,
            Func<bool>? timeUp = null)
        {
            if (signatures.Count > UnitRepackMaximumMergeSets)
            {
                return (null,
                    $"{signatures.Count:N0} signature sets exceed the {UnitRepackMaximumMergeSets:N0}-set cap");
            }

            var words = Math.Max(1, (unitCount + 63) / 64);
            var bits = new List<ulong[]>();
            var counts = new List<int>();
            var members = new List<List<int>>();
            var alive = new List<bool>();
            for (var index = 0; index < signatures.Count; index++)
            {
                var set = new ulong[words];
                foreach (var unit in signatures[index])
                    set[unit >> 6] |= 1UL << (unit & 63);

                bits.Add(set);
                counts.Add(set.Sum(word => System.Numerics.BitOperations.PopCount(word)));
                members.Add([index]);
                alive.Add(true);
            }

            var queue = new PriorityQueue<(int A, int B), (double NegativeJaccard, int A, int B)>();
            void Consider(int a, int b)
            {
                var intersection = 0;
                for (var word = 0; word < words; word++)
                    intersection += System.Numerics.BitOperations.PopCount(bits[a][word] & bits[b][word]);

                var union = counts[a] + counts[b] - intersection;
                if (union > 0 &&
                    (long)intersection * UnitRepackMergeDenominator >= (long)union * UnitRepackMergeNumerator)
                {
                    queue.Enqueue((a, b), (-(double)intersection / union, a, b));
                }
            }

            for (var a = 0; a < signatures.Count; a++)
            {
                if ((a & 63) == 0 && timeUp?.Invoke() == true)
                    return (null, UnitRepackMergeTimeReason);

                for (var b = a + 1; b < signatures.Count; b++)
                    Consider(a, b);
            }

            var steps = 0;
            while (queue.TryDequeue(out var pair, out _))
            {
                if ((++steps & 63) == 0 && timeUp?.Invoke() == true)
                    return (null, UnitRepackMergeTimeReason);

                if (!alive[pair.A] || !alive[pair.B])
                    continue;

                var merged = new ulong[words];
                for (var word = 0; word < words; word++)
                    merged[word] = bits[pair.A][word] | bits[pair.B][word];

                var newId = bits.Count;
                bits.Add(merged);
                counts.Add(merged.Sum(word => System.Numerics.BitOperations.PopCount(word)));
                members.Add(members[pair.A].Concat(members[pair.B]).OrderBy(member => member).ToList());
                alive[pair.A] = false;
                alive[pair.B] = false;
                alive.Add(true);
                for (var other = 0; other < newId; other++)
                {
                    if (alive[other])
                        Consider(other, newId);
                }
            }

            var groups = Enumerable.Range(0, bits.Count)
                .Where(id => alive[id])
                .Select(id => members[id])
                .OrderBy(group => group[0])
                .ToList();
            return (groups, null);
        }

        // Bytes with an ASCII minus (culture formats can print a spaced "- " sign).
        private static string FormatSignedBytes(long value)
            => value < 0
                ? "-" + (-value).ToString("N0")
                : value.ToString("N0");

        private static readonly string[] UnitRepackAtlasSizeClasses =
            ["< 64 KiB", "64 KiB - 1 MiB", "1 - 4 MiB", "> 4 MiB"];

        private static int GetUnitRepackAtlasSizeClass(long bytes)
            => bytes < 64 * 1024 ? 0 : bytes < 1024 * 1024 ? 1 : bytes <= 4 * 1024 * 1024 ? 2 : 3;

        private sealed record UnitRepackSetSummary(
            int Sets,
            int Atlases,
            int[] SignatureSizes,
            int[] AtlasSizes,
            long Raw,
            long Rounded,
            long Today,
            long After,
            int Better,
            int Worse,
            long Saving,
            long MedianSaving,
            double WasteMean,
            double WasteP90,
            long WholeToday,
            long WholeAfter,
            int RigidsRewritten,
            int VanillaCopies,
            long VanillaCopyBytes);

        private static UnitRepackSetSummary SummarizeUnitRepackSets(
            UnitTextureRepackResult result,
            IReadOnlyList<UnitRepackUnit> units,
            List<UnitRepackSignatureSet> sets,
            int variant)
        {
            var members = units.Select(unit => unit.Index).ToHashSet();
            var touched = sets.Where(set => set.Units.Any(members.Contains)).ToList();
            var signatureSizes = new int[UnitRepackSizeClasses.Length];
            var atlasSizes = new int[UnitRepackAtlasSizeClasses.Length];
            foreach (var set in touched)
            {
                signatureSizes[Array.IndexOf(UnitRepackSizeClasses, GetUnitRepackSizeClass(set.Units.Length))]++;
                foreach (var atlas in set.Atlases)
                    atlasSizes[GetUnitRepackAtlasSizeClass(atlas.Rounded)]++;
            }

            var savings = units.Select(unit => unit.Today - unit.AfterVariant(variant)).ToList();
            var waste = units.Select(unit => unit.Waste[variant]).OrderBy(value => value).ToList();
            var distinct = units.SelectMany(unit => unit.Textures.Keys)
                .Where(entry => entry.Layout != null).Distinct().ToList();
            var keptBytes = distinct.Where(entry => !entry.IsRepackable).Sum(entry => entry.Layout!.ChainBytes);
            var rounded = touched.Sum(set => set.Rounded);
            int rewritten = 0, copies = 0;
            long copyBytes = 0;
            foreach (var rigid in result.Rigids.Values)
            {
                if (!rigid.Touching.Any(unit => members.Contains(unit.Index)))
                    continue;

                if (rigid.ModOwned)
                {
                    rewritten++;
                }
                else
                {
                    copies++;
                    copyBytes += rigid.Bytes;
                }
            }

            return new UnitRepackSetSummary(
                touched.Count,
                touched.Sum(set => set.Atlases.Count),
                signatureSizes,
                atlasSizes,
                touched.Sum(set => set.Raw),
                rounded,
                units.Sum(unit => unit.Today),
                units.Sum(unit => unit.AfterVariant(variant)),
                savings.Count(saving => saving > 0),
                savings.Count(saving => saving < 0),
                savings.Sum(),
                MedianOf(savings),
                waste.Count == 0 ? 0 : waste.Average(),
                waste.Count == 0 ? 0 : waste[Math.Max(0, (int)Math.Ceiling(waste.Count * 0.9) - 1)],
                distinct.Sum(entry => entry.Layout!.ChainBytes),
                rounded + keptBytes,
                rewritten,
                copies,
                copyBytes);
        }

        private static void AppendUnitRepackSetBlock(
            StringBuilder sb,
            string title,
            UnitRepackSetSummary summary,
            int unitCount,
            bool full,
            bool includeRemovable,
            UnitTextureRepackResult result)
        {
            sb.AppendLine($"  {title}:");
            sb.AppendLine(
                $"    Signature sets: {summary.Sets:N0}, atlases: {summary.Atlases:N0}; signature size " +
                "(units): " +
                string.Join(", ", UnitRepackSizeClasses.Select((name, index) => $"{name}={summary.SignatureSizes[index]:N0}")) +
                "; atlas size (rounded): " +
                string.Join(", ", UnitRepackAtlasSizeClasses.Select((name, index) => $"{name}={summary.AtlasSizes[index]:N0}")));
            sb.AppendLine(
                $"    Atlas bytes: raw {summary.Raw:N0}, rounded {summary.Rounded:N0} (rounding overhead " +
                $"{(summary.Raw == 0 ? 0 : (double)summary.Rounded / summary.Raw):0.00}x)");
            sb.AppendLine(
                $"    Loaded today (sum over {unitCount:N0} unit(s)): {summary.Today:N0} bytes; after " +
                $"{summary.After:N0}; saves {FormatSignedBytes(summary.Today - summary.After)} (median per unit " +
                $"{FormatSignedBytes(summary.MedianSaving)}); units better {summary.Better:N0}, worse " +
                $"{summary.Worse:N0}, unchanged {unitCount - summary.Better - summary.Worse:N0}");
            sb.AppendLine(
                $"    Residual waste (share of the raw atlas bytes a unit loads that its own parts do " +
                $"not touch): mean {summary.WasteMean:P1}, 90th percentile {summary.WasteP90:P1}" +
                (full
                    ? "; signatures per grid cell instead of per texture would remove it (not implemented)"
                    : string.Empty));
            sb.AppendLine(
                $"    Whole pack if every unit were loaded at once: today {summary.WholeToday:N0} bytes " +
                $"of distinct textures; after {summary.WholeAfter:N0} (all signature atlases + kept)" +
                (full ? " - nothing is duplicated, so this is the honest headline" : string.Empty));
            if (full)
            {
                sb.AppendLine(
                    $"    Pack cost: atlas bytes added {summary.Rounded:N0}" +
                    (includeRemovable
                        ? $"; removable owned texture bytes {result.RemovableOwnedBytes:N0}"
                        : string.Empty) +
                    $"; mod-owned rigids rewritten in place {summary.RigidsRewritten:N0}; vanilla rigids " +
                    $"needing one generated copy {summary.VanillaCopies:N0} ({summary.VanillaCopyBytes:N0} bytes)");
            }
        }

        // One signature set as an independent build decision.
        private sealed class UnitRepackSetDecision
        {
            public UnitRepackSignatureSet Set { get; init; } = null!;
            public int Textures { get; init; }
            public int SignatureSize { get; init; }
            public long Today { get; init; }
            public long Rounded { get; init; }
            public long Removable { get; init; }
            public HashSet<string> ModRigids { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> VanillaRigids { get; } = new(StringComparer.OrdinalIgnoreCase);
            // null when the lord/hero classification is unavailable.
            public bool? HasCharacter { get; init; }
            public string ExampleUnit { get; init; } = string.Empty;
            public string ExampleTexture { get; init; } = string.Empty;

            // What every unit in the signature saves when loaded, and the whole pack saves.
            public long Saving => Today - Rounded;
            public long BytesAdded => Rounded - Removable;
            public bool Built => Saving > 0;
            public int RigidsTouched => ModRigids.Count + VanillaRigids.Count;
        }

        private sealed record UnitRepackCumulativeRow(
            string Label,
            int Sets,
            long Saving,
            double Share,
            long BytesAdded,
            int ModRigids,
            int VanillaRigids,
            int Textures);

        private static readonly int[] UnitRepackCumulativeLimits = [1, 5, 10, 25, 50, 100];

        // `layoutOf`, when given, replaces each texture's layout for the sizes of the decision
        // (the shared repack at a downscaled density); the atlas estimate is then recomputed from
        // those layouts and the set's coverage.
        private static List<UnitRepackSetDecision> BuildUnitRepackSetDecisions(
            UnitTextureRepackResult result)
            => BuildUnitRepackSetDecisionsAtDensity(result, null);

        private static List<UnitRepackSetDecision> BuildUnitRepackSetDecisionsAtDensity(
            UnitTextureRepackResult result,
            Func<VanillaCoverageTexture, DdsLayout>? layoutOf)
        {
            var decisions = new List<UnitRepackSetDecision>();
            foreach (var set in result.SetsC)
            {
                var decision = new UnitRepackSetDecision
                {
                    Set = set,
                    Textures = set.Items.Count,
                    SignatureSize = set.Units.Length,
                    Today = layoutOf == null
                        ? set.Items.Sum(item => item.Entry.Layout!.ChainBytes)
                        : set.Items.Sum(item => layoutOf(item.Entry).ChainBytes),
                    Rounded = layoutOf == null
                        ? set.Rounded
                        : EstimateRepackAtlases(set.Items.Select(
                            item => (item.Slot, layoutOf(item.Entry), item.Coverage))).Sum(atlas => atlas.Rounded),
                    Removable = set.Items
                        .Where(item => result.RemovableEntries.Contains(item.Entry))
                        .Sum(item => item.Entry.Layout!.ChainBytes),
                    HasCharacter = result.CharacterRuleUnavailableReason != null
                        ? null
                        : set.Units.Any(index => result.Units[index].Kind == TextureDownscaleRootKind.Character),
                    ExampleUnit = result.Units[set.Units[0]].Root,
                    ExampleTexture = set.Items[0].Entry.Path,
                };
                foreach (var item in set.Items)
                {
                    foreach (var unitIndex in set.Units)
                    {
                        foreach (var part in result.Units[unitIndex].Textures[item.Entry].Parts)
                        {
                            if (result.Rigids.TryGetValue(part.Rigid, out var rigid) && rigid.ModOwned)
                                decision.ModRigids.Add(part.Rigid);
                            else
                                decision.VanillaRigids.Add(part.Rigid);
                        }
                    }
                }

                decisions.Add(decision);
            }

            return decisions;
        }

        // Positive-saving sets ranked by saving; one row per limit below the count, then "all".
        // Rigids are the distinct union over the selected sets, not a sum.
        private static List<UnitRepackCumulativeRow> BuildUnitRepackCumulativeRows(
            IReadOnlyList<UnitRepackSetDecision> decisions,
            IReadOnlyList<int> limits)
        {
            var built = decisions
                .Where(decision => decision.Built)
                .OrderByDescending(decision => decision.Saving)
                .ThenBy(decision => decision.ExampleTexture, StringComparer.Ordinal)
                .ToList();
            var rows = new List<UnitRepackCumulativeRow>();
            if (built.Count == 0)
                return rows;

            var total = built.Sum(decision => decision.Saving);
            foreach (var count in limits.Where(limit => limit > 0 && limit < built.Count)
                         .Append(built.Count))
            {
                var selected = built.Take(count).ToList();
                var saving = selected.Sum(decision => decision.Saving);
                rows.Add(new UnitRepackCumulativeRow(
                    count == built.Count ? "all" : $"best {count:N0}",
                    count,
                    saving,
                    total == 0 ? 0 : (double)saving / total,
                    selected.Sum(decision => decision.BytesAdded),
                    selected.SelectMany(decision => decision.ModRigids)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    selected.SelectMany(decision => decision.VanillaRigids)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    selected.Sum(decision => decision.Textures)));
            }

            return rows;
        }

        private static void AppendUnitRepackSelectiveView(
            StringBuilder sb,
            UnitTextureRepackResult result)
        {
            var decisions = BuildUnitRepackSetDecisions(result);
            var built = decisions.Where(decision => decision.Built).ToList();
            sb.AppendLine(
                "  Option C selective view (each signature set is an independent decision: a set is " +
                "built only if its rounded atlas is smaller than today's bytes of its textures; " +
                "saving = today - atlas, which every unit in the signature saves when loaded and " +
                "the whole pack saves, since nothing is duplicated):");
            sb.AppendLine(
                $"    Signature sets: {decisions.Count:N0}; positive saving (would be built): " +
                $"{built.Count:N0}; zero or negative after rounding (never built): " +
                $"{decisions.Count - built.Count:N0}; saving of positive sets only: " +
                $"{FormatSignedBytes(built.Sum(decision => decision.Saving))} bytes; saving of all sets: " +
                $"{FormatSignedBytes(decisions.Sum(decision => decision.Saving))} bytes");
            string Describe(UnitRepackSetDecision decision)
                => $"saves {FormatSignedBytes(decision.Saving)} (today {decision.Today:N0} -> atlas " +
                   $"{decision.Rounded:N0}), added to the pack {FormatSignedBytes(decision.BytesAdded)} " +
                   $"(atlas minus {decision.Removable:N0} removable owned bytes), {decision.Textures:N0} " +
                   $"texture(s), {decision.SignatureSize:N0} unit(s) in the signature, " +
                   $"{decision.ModRigids.Count:N0} mod-owned rigid(s) rewritten, " +
                   $"{decision.VanillaRigids.Count:N0} vanilla rigid(s) copied, character: " +
                   $"{(decision.HasCharacter == null ? "unknown" : decision.HasCharacter.Value ? "yes" : "no")}; " +
                   $"e.g. unit {decision.ExampleUnit}, texture {decision.ExampleTexture}";
            var ranked = decisions
                .OrderByDescending(decision => decision.Saving)
                .ThenBy(decision => decision.ExampleTexture, StringComparer.Ordinal)
                .ToList();
            sb.AppendLine(
                ranked.Count > UnitRepackListLimit
                    ? $"    Top {UnitRepackListLimit} sets by saving:"
                    : "    All sets by saving:");
            foreach (var decision in ranked.Take(UnitRepackListLimit))
                sb.AppendLine($"      {Describe(decision)}");
            if (ranked.Count > UnitRepackListLimit)
                sb.AppendLine($"      ... and {ranked.Count - UnitRepackListLimit:N0} more");

            sb.AppendLine(
                "    Build only the best N sets by saving (share = of the saving of the positive sets; " +
                "rigids are distinct across the selected sets):");
            foreach (var row in BuildUnitRepackCumulativeRows(decisions, UnitRepackCumulativeLimits))
            {
                sb.AppendLine(
                    $"      {row.Label} ({row.Sets:N0} set(s), {row.Textures:N0} texture(s)): saves " +
                    $"{FormatSignedBytes(row.Saving)} bytes ({row.Share:P1}), added to the pack " +
                    $"{FormatSignedBytes(row.BytesAdded)} bytes, {row.ModRigids:N0} mod-owned rigid(s) " +
                    $"rewritten, {row.VanillaRigids:N0} vanilla rigid(s) copied");
            }

            sb.AppendLine("    Best efficiency (saving per rigid touched), top 10 positive sets:");
            foreach (var decision in built
                         .OrderByDescending(decision => (double)decision.Saving / Math.Max(1, decision.RigidsTouched))
                         .ThenByDescending(decision => decision.Saving)
                         .ThenBy(decision => decision.ExampleTexture, StringComparer.Ordinal)
                         .Take(UnitRepackRigidLimit))
            {
                sb.AppendLine(
                    $"      {(double)decision.Saving / Math.Max(1, decision.RigidsTouched):N0} bytes per rigid " +
                    $"({decision.RigidsTouched:N0} rigid(s) touched): {Describe(decision)}");
            }
        }

        private static string DescribeUnitRepackKind(UnitRepackUnit unit)
            => unit.Kind switch
            {
                TextureDownscaleRootKind.Character => "character",
                TextureDownscaleRootKind.RankAndFile => "rank-and-file",
                TextureDownscaleRootKind.Unclassified => "unclassified",
                _ => "unclassified (rule unavailable)",
            };

        // ---------------------------------------------------------------------------
        // Report.
        // ---------------------------------------------------------------------------

        private static void AppendUnitRepackOptionBlocks(
            StringBuilder sb,
            string title,
            UnitRepackSummary summary,
            IReadOnlyList<UnitRepackUnit> units,
            bool includeRemovable,
            UnitTextureRepackResult result)
        {
            sb.AppendLine($"{title}: {summary.Units:N0} unit(s)");
            sb.AppendLine("  Option A (one atlas set per unit, shared rigids copied per unit):");
            sb.AppendLine(
                $"    Loaded today (sum over units): {summary.Today:N0} bytes; after, raw " +
                $"{summary.AfterARaw:N0}; after, rounded (headline) {summary.AfterA:N0}; saves " +
                $"{FormatSignedBytes(summary.Today - summary.AfterA)} (median per unit {FormatSignedBytes(summary.MedianSavingA)})");
            sb.AppendLine(
                $"    Added to the pack (sum of unit atlases, rounded): {summary.AtlasAdded:N0} bytes");
            sb.AppendLine(
                $"    Rigid copies needed: {summary.CopiesMod + summary.CopiesVanilla:N0} " +
                $"({summary.CopiesMod:N0} of mod-owned rigids, {summary.CopyBytesMod:N0} bytes; " +
                $"{summary.CopiesVanilla:N0} of vanilla rigids, {summary.CopyBytesVanilla:N0} bytes)");
            if (includeRemovable)
            {
                sb.AppendLine(
                    $"    Removable owned texture bytes: {result.RemovableOwnedBytes:N0} " +
                    $"({result.RemovableOwnedTextures:N0} texture(s))");
                sb.AppendLine(
                    $"    Whole pack if every unit were loaded at once (Option A's worst case): " +
                    $"today {result.WholePackToday:N0} bytes of distinct textures; after " +
                    $"{result.WholePackAfter:N0} (all unit atlases + kept textures)");
            }

            sb.AppendLine("  Option B (one atlas set per group of units that share a rigid):");
            sb.AppendLine(
                $"    Groups touched: {summary.Groups:N0}; loaded today {summary.Today:N0} bytes; " +
                $"after (group atlas + kept, rounded) {summary.AfterB:N0}; saves " +
                $"{FormatSignedBytes(summary.Today - summary.AfterB)} (median per unit {FormatSignedBytes(summary.MedianSavingB)})");
            sb.AppendLine(
                $"    Units better: {summary.Better:N0}, worse: {summary.Worse:N0}, unchanged: " +
                $"{summary.Units - summary.Better - summary.Worse:N0}; loaded but not needed by the " +
                $"unit (mean share of its group atlas): {summary.MeanNotNeeded:P1}");
            sb.AppendLine(
                $"    Added to the pack (sum of group atlases, rounded): {summary.GroupAtlasAdded:N0} " +
                "bytes; rigid copies needed: 0" +
                (includeRemovable
                    ? $"; removable owned texture bytes: {result.RemovableOwnedBytes:N0}"
                    : string.Empty));
            AppendUnitRepackSetBlock(
                sb,
                "Option C (one atlas set per signature = the exact set of units that use a texture; " +
                "each texture placed once)",
                SummarizeUnitRepackSets(result, units, result.SetsC, 0),
                units.Count,
                full: true,
                includeRemovable,
                result);
            if (result.C2SkippedReason != null)
            {
                sb.AppendLine(
                    $"  Option C2 (signature sets merged at Jaccard >= 0.8): not computed ({result.C2SkippedReason}).");
            }
            else
            {
                AppendUnitRepackSetBlock(
                    sb,
                    "Option C2 (signature sets merged greedily while Jaccard similarity >= 0.8)",
                    SummarizeUnitRepackSets(result, units, result.SetsC2, 1),
                    units.Count,
                    full: false,
                    includeRemovable: false,
                    result);
            }

            if (includeRemovable)
                AppendUnitRepackSelectiveView(sb, result);
        }

        private static void AppendUnitTextureRepackSection(StringBuilder sb, BatchState state)
        {
            sb.AppendLine("Per-unit texture repack estimate");
            sb.AppendLine("--------------------------------");
            var result = state.UnitTextureRepack;
            if (result == null)
            {
                sb.AppendLine("Per-unit texture repack estimate did not run.");
                sb.AppendLine();
                return;
            }

            if (result.Failure != null)
            {
                sb.AppendLine(
                    $"Per-unit texture repack estimate failed and was skipped: {result.Failure}");
                sb.AppendLine("No pack file was changed.");
                sb.AppendLine();
                return;
            }

            if (result.UnavailableReason != null)
            {
                sb.AppendLine(
                    $"Per-unit texture repack estimate could not run: {result.UnavailableReason}.");
                sb.AppendLine("No pack file was changed.");
                sb.AppendLine();
                return;
            }

            sb.AppendLine(
                "Mode: REPORT ONLY. No pack file was changed. Memory only, not frame rate. The " +
                "assumption is that a multi-soldier unit shows every alternative, so everything " +
                "in its atlas set is needed whenever the unit is on the field.");
            sb.AppendLine(
                "Scope: units are the gameplay roots of the gameplay mesh dependency index that " +
                "reach a source WSModel with at least one assessed part; parts, materials and " +
                "headers come from the vanilla texture coverage collection (as authored, every " +
                "LOD, UV0). Source WSModels that no root reaches are excluded.");
            sb.AppendLine(
                "Method: a texture is repackable when it resolves, its header is readable and " +
                "it passes the coverage section's croppable tests (not tiled, every user " +
                "assessable, atlasable slots only, no non-material mention); vanilla AND " +
                "source-owned textures are in scope. Everything else is kept as is and counts " +
                "the same on both sides. Coverage of a texture for a set of parts is the share " +
                $"of the {VanillaCoverageGridSize}x{VanillaCoverageGridSize} cells their " +
                "triangles touch. Raw atlas bytes of a texture = min(ChainBytes, coverage x " +
                $"ChainBytes x {VanillaCoveragePackingSlack:0.00} packing slack), no per-texture " +
                "power-of-two rounding. Rounded (the headline): per set, textures are grouped by " +
                "(slot, format); a group's needed area is the sum of min(width x height, " +
                "coverage x width x height x slack) pixels, rounded up to a power-of-two area " +
                "2^k laid out as 2^ceil(k/2) x 2^floor(k/2) with a full mip chain; the group " +
                "figure is never below its raw sum nor above its textures' current bytes. Loaded " +
                "today for a unit = sum of ChainBytes of its distinct textures. Option A: coverage " +
                "over the unit's own parts. Option B: units are linked by using a common rigid " +
                "(sharing only a texture does not link them); coverage over all parts of the " +
                "group, and a unit's after = group atlas + its own kept textures. Rigid copies " +
                "(A) per rigid = units that must rewrite it, minus one when every user rewrites " +
                "it (the original stays with one). Option C: a texture's signature is the exact set of " +
                "estimated units that use it, and textures with the same signature form a signature " +
                "set with one atlas per (slot, format, sRGB), same raw and rounded formulas, each " +
                "texture placed once with its coverage over all parts of all its users (so nothing " +
                "is duplicated and no rigid is copied per unit); a unit's after = rounded atlases of " +
                "every set whose signature contains it + its kept textures; residual waste = 1 - " +
                "(raw bytes its own parts need) / (raw bytes of the textures in the atlases it " +
                "loads); rigids: mod-owned rigids with a repackable part are rewritten in place, " +
                "vanilla ones need one generated copy. Option C2 merges signature sets greedily: " +
                "while any two live sets have Jaccard similarity >= 0.8 (intersection x 10 >= union " +
                "x 8) the most similar pair is merged (ties: lower set id, then lower partner id; " +
                "merged signature = union; input sets have ids 0..n-1, merged sets take the next " +
                "free id), capped at 3,000 sets and by the time budget. Removable owned textures: repackable, all " +
                "users inside estimated units, every source material that mentions it is bound, " +
                "no unreadable material or vanilla override of the path.");
            sb.AppendLine(
                $"Analysis time: {result.AnalysisElapsed.TotalMilliseconds:N0} ms of a " +
                $"{result.Budget.TotalSeconds:N0} s budget");
            if (result.BudgetExhausted)
            {
                sb.AppendLine(
                    $"The time budget ran out: {result.UnitsNotReached:N0} unit(s) were not " +
                    "reached and are left out of every figure below" +
                    (result.PairsSkippedReason == null
                        ? string.Empty
                        : $"; pair statistics were skipped ({result.PairsSkippedReason})") + ".");
            }

            var allUnits = result.Units;
            sb.AppendLine(
                $"Roots reaching a source WSModel: {result.RootsReached:N0}. They split exactly " +
                $"into: units estimated {allUnits.Count:N0}, without an assessed part " +
                $"{result.RootsWithoutAssessableParts:N0}, not reached (time budget) " +
                $"{result.UnitsNotReached:N0}");
            sb.AppendLine(
                $"Source WSModels reached by no root (excluded): {result.UnrootedWsModels:N0}, " +
                $"binding {result.UnrootedParts:N0} distinct part(s)");
            sb.AppendLine(
                $"Textures used by the estimated units: {result.TexturesUsed:N0}. They split " +
                $"exactly into: repackable {result.TexturesRepackable:N0}, kept as is " +
                $"{result.TexturesKept:N0} (of which size unknown {result.TexturesKeptUnknownSize:N0})");

            if (allUnits.Count == 0)
            {
                sb.AppendLine("No unit could be estimated.");
                sb.AppendLine();
                return;
            }

            var all = SummarizeUnitRepack(result, allUnits);
            AppendUnitRepackOptionBlocks(sb, "All units", all, allUnits, includeRemovable: true, result);
            if (result.CharacterRuleUnavailableReason != null)
            {
                sb.AppendLine(
                    "Character classification unavailable (" +
                    $"{result.CharacterRuleUnavailableReason}); only the all-units totals are given.");
            }
            else
            {
                var counts = allUnits.GroupBy(unit => unit.Kind)
                    .ToDictionary(group => group.Key, group => group.Count());
                sb.AppendLine(
                    $"Unit classes: rank-and-file {counts.GetValueOrDefault(TextureDownscaleRootKind.RankAndFile):N0}, " +
                    $"character (lord/hero, one entity: the all-alternatives assumption does not hold) " +
                    $"{counts.GetValueOrDefault(TextureDownscaleRootKind.Character):N0}, unclassified " +
                    $"{counts.GetValueOrDefault(TextureDownscaleRootKind.Unclassified):N0}");
                var rank = allUnits
                    .Where(unit => unit.Kind == TextureDownscaleRootKind.RankAndFile)
                    .ToList();
                if (rank.Count != 0)
                {
                    AppendUnitRepackOptionBlocks(
                        sb, "Rank-and-file units only", SummarizeUnitRepack(result, rank), rank,
                        includeRemovable: false, result);
                }
            }

            sb.AppendLine("Option B groups:");
            sb.AppendLine(
                $"  {result.Groups.Count:N0} group(s); units per group: " +
                string.Join(
                    ", ",
                    UnitRepackSizeClasses.Select(sizeClass =>
                        $"{sizeClass}={result.Groups.Count(group => GetUnitRepackSizeClass(group.Units.Count) == sizeClass):N0}")));
            foreach (var group in result.Groups
                         .OrderByDescending(group => group.Units.Count)
                         .ThenBy(group => group.Units[0].Index)
                         .Take(UnitRepackGroupLimit))
            {
                sb.AppendLine(
                    $"  largest: {group.Units.Count:N0} unit(s), atlas {group.AtlasRounded:N0} bytes " +
                    $"(e.g. {group.Units[0].Root})");
            }

            sb.AppendLine("Sharing statistics:");
            sb.AppendLine(
                "  Units per rigid: " +
                string.Join(
                    ", ",
                    UnitRepackSizeClasses.Select(sizeClass =>
                        $"{sizeClass}={result.Rigids.Values.Count(rigid => GetUnitRepackSizeClass(rigid.Users.Count) == sizeClass):N0}")) +
                $" ({result.Rigids.Count:N0} rigid(s))");
            sb.AppendLine(
                "  Units per texture: " +
                string.Join(
                    ", ",
                    UnitRepackSizeClasses.Select(sizeClass =>
                        $"{sizeClass}={result.UnitsPerTexture.Count(count => GetUnitRepackSizeClass(count) == sizeClass):N0}")) +
                $" ({result.UnitsPerTexture.Count:N0} texture(s))");
            sb.AppendLine($"  Most shared rigids (top {UnitRepackRigidLimit}):");
            foreach (var pair in result.Rigids
                         .OrderByDescending(pair => pair.Value.Users.Count)
                         .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                         .Take(UnitRepackRigidLimit))
            {
                sb.AppendLine(
                    $"    {pair.Key}: {pair.Value.Users.Count:N0} unit(s), " +
                    $"{pair.Value.Bytes:N0} bytes, {(pair.Value.ModOwned ? "mod-owned" : "vanilla")}");
            }

            if (result.PairsSkippedReason != null)
            {
                sb.AppendLine($"Pairs sharing the most texture bytes: not computed ({result.PairsSkippedReason}).");
            }
            else if (result.TopPairs.Count != 0)
            {
                sb.AppendLine(
                    $"Top {result.TopPairs.Count:N0} unit pairs by shared texture bytes (today = " +
                    "union loaded once; after = two atlas sets + the union of kept textures):");
                foreach (var pair in result.TopPairs)
                {
                    sb.AppendLine(
                        $"  {pair.First.Root} + {pair.Second.Root}: shared {pair.SharedBytes:N0}, " +
                        $"today {pair.Today:N0}, after {pair.After:N0}, C after {pair.AfterC:N0}");
                }
            }

            var ordered = allUnits
                .OrderByDescending(unit => unit.SavingA)
                .ThenBy(unit => unit.Root, StringComparer.Ordinal)
                .ToList();
            sb.AppendLine(
                ordered.Count > UnitRepackListLimit
                    ? $"Top {UnitRepackListLimit} units by Option A saving:"
                    : "All units by Option A saving:");
            foreach (var unit in ordered.Take(UnitRepackListLimit))
            {
                var group = result.Groups[unit.Group];
                sb.AppendLine(
                    $"  {unit.Root} [{DescribeUnitRepackKind(unit)}]: today {unit.Today:N0}, A after " +
                    $"{unit.AfterA:N0}, B after {group.AtlasRounded + unit.KeptBytes:N0}, C after " +
                    $"{unit.AfterVariant(0):N0}, group of " +
                    $"{group.Units.Count:N0} unit(s); {unit.RepackableTextures:N0} repackable " +
                    $"texture(s), fill {unit.Fill:P1}, {unit.Rigids.Count:N0} rigid(s) " +
                    $"({unit.SharedRigids:N0} shared)");
            }

            if (ordered.Count > UnitRepackListLimit)
                sb.AppendLine($"  ... and {ordered.Count - UnitRepackListLimit:N0} more");

            sb.AppendLine();
        }
    }
}
