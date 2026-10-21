using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Models.FileSources;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.MaterialHeaders;

namespace Editors.KitbasherEditor.Services
{
    public sealed partial class PackTextureAtlasBatchService
    {
        // Report-only checks over the materials and textures of the rigids the LOD audit
        // reached. Check 1 finds alpha-tested materials whose alpha source is opaque (they
        // could use the non-alpha shader). Check 2 finds byte-identical textures under
        // different paths and the materials that would be identical if those were shared.
        // Both also count the same-rigid merges the change would unlock. Everything reads
        // source state through private caches; nothing is written and no shared cache or
        // counter other sections print is touched.
        private const double AssetChecksDefaultBudgetSeconds = 60;
        // Absolute deadlines (shares of the budget, measured from the start of the pass):
        // hashing may use the first 40 %, alpha decoding up to 80 %, the merge-unlock
        // evaluation (which re-reads some rigids) the rest.
        private const double AssetChecksHashBudgetShare = 0.4;
        private const double AssetChecksDecodeBudgetShare = 0.8;
        private const int AssetChecksOpaqueMinimumAlpha = 250;
        // MOSTLY OPAQUE: fewer than 1 texel in 1000 (0.1 %) has alpha below 128.
        private const long AssetChecksMostlyOpaqueTexelsPerLowAlphaTexel = 1000;
        private const int AssetChecksTextureGroupLimit = 10;
        private const int AssetChecksMaterialGroupLimit = 10;
        private const int AssetChecksMaterialRankingLimit = 15;
        private const int AssetChecksVariantListLimit = 15;
        private const int AssetChecksReasonLimit = 6;
        private const int AssetChecksMeasureCount = 5;
        private const int AssetChecksMeasureCheck1 = 0;
        private const int AssetChecksMeasureCheck1FlagIgnored = 1;
        private const int AssetChecksMeasureCheck2 = 2;
        private const int AssetChecksMeasureCombined = 3;
        private const int AssetChecksMeasureCombinedFlagIgnored = 4;

        // Alpha test samples the alpha of the base colour texture. Only the plain
        // character shader families are assessed: those are the shaders the editor itself
        // writes (shaders/{rigid|weighted2|weighted4}_character[_alpha].xml.shader) and
        // their non-alpha twin exists. Other families (emissive, random/faction colours,
        // skin, flow, ghost) sample further textures and are listed as not assessed.
        private static readonly string[] AssetCheckPlainAlphaShaderStems =
        [
            @"shaders\rigid_character",
            @"shaders\weighted2_character",
            @"shaders\weighted4_character",
        ];

        // Where the alpha channel of a material comes from: the base colour texture, or
        // its legacy slot names.
        private static readonly string[] AssetCheckAlphaSourceSlots =
        [
            "t_xml_base_colour",
            "s_base_colour",
            "s_diffuse",
        ];

        private enum AssetCheckAlphaStatus
        {
            NotAlphaShader,
            NotAssessed,
            NoAlphaTexture,
            BudgetExhausted,
            Opaque,
            MostlyOpaque,
            UsesAlpha,
            Unreadable,
        }

        private readonly record struct AssetCheckAlphaStatistics(
            long Texels,
            int MinAlpha,
            long Below255,
            long Below128);

        private sealed record AssetCheckTextureAlpha(
            AssetCheckAlphaStatus Status,
            AssetCheckAlphaStatistics Statistics,
            string Reason,
            int Width,
            int Height,
            string Format);

        private readonly record struct AssetCheckShaderAssessment(
            bool IsAlphaShader,
            bool IsCandidate,
            string TwinShader,
            string Reason);

        // Compact copies of what the LOD audit traversal resolved. The audit drops rigid
        // geometry as it goes, so only per-part vertex counts and the material
        // assignments are kept; rigids are re-read later for the few that need it.
        private sealed record AssetCheckComponent(
            string WsModelPath,
            string RigidPath,
            bool SourceOwnedWsModel,
            bool SourceOwnedRigid,
            string[][] MaterialAssignments);

        private sealed record AssetCheckRigid(
            string RigidPath,
            bool SourceOwned,
            int[][] VertexCounts);

        // Probability-weighted number of times each WSModel is instantiated by one
        // entity of a root (the same expansion the LOD audit uses).
        private sealed record AssetCheckRootExposure(
            string VmdPath,
            IReadOnlyDictionary<string, double> InstanceWeightByWsModel);

        private sealed record AssetCheckRmvKeys(string Strict, string FlagIgnored);

        private sealed record AssetCheckPartMaterial(
            string AsIs,
            string Check1,
            string Check2,
            string Combined);

        private sealed class AssetCheckMaterial
        {
            public AssetCheckMaterial(string path, bool sourceOwned)
            {
                Path = path;
                SourceOwned = sourceOwned;
            }

            public string Path { get; }
            public bool SourceOwned { get; }
            public XmlDocument? Document { get; set; }
            public string? UnreadableReason { get; set; }
            public string Shader { get; set; } = string.Empty;
            public Dictionary<string, string> Textures { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public bool HasAlphaPixelsColour { get; set; }

            public string AsIsIdentity { get; set; } = string.Empty;
            public string Check1Identity { get; set; } = string.Empty;
            public string Check2Identity { get; set; } = string.Empty;
            public string CombinedIdentity { get; set; } = string.Empty;

            public AssetCheckAlphaStatus Status { get; set; } =
                AssetCheckAlphaStatus.NotAlphaShader;
            public string TwinShader { get; set; } = string.Empty;
            public string AlphaTexturePath { get; set; } = string.Empty;
            public int MinAlpha { get; set; } = -1;
            public string NotAssessedReason { get; set; } = string.Empty;
        }

        private sealed class AssetCheckUnit
        {
            public AssetCheckUnit(
                string key,
                string rigidPath,
                bool sourceOwnedRigid,
                int lodIndex,
                int[] vertexCounts,
                AssetCheckPartMaterial[] parts)
            {
                Key = key;
                RigidPath = rigidPath;
                SourceOwnedRigid = sourceOwnedRigid;
                LodIndex = lodIndex;
                VertexCounts = vertexCounts;
                Parts = parts;
            }

            public string Key { get; }
            public string RigidPath { get; }
            public bool SourceOwnedRigid { get; }
            public int LodIndex { get; }
            public int[] VertexCounts { get; }
            public AssetCheckPartMaterial[] Parts { get; }
            public int[] Removable { get; set; } = new int[AssetChecksMeasureCount];
            public bool Evaluated { get; set; }
        }

        private sealed record AssetCheckTextureGroup(
            long Length,
            IReadOnlyList<string> Paths,
            long BcnBytes);

        private sealed record AssetCheckGroupCounts(int Groups, int Files, int Redundant);

        private sealed record AssetCheckMaterialGroup(
            int Size,
            int AsIsVariants,
            IReadOnlyList<string> Paths,
            IReadOnlyList<string> Differences);

        private sealed record AssetCheckRankedMaterial(
            string Path,
            string TexturePath,
            int MinAlpha,
            int MeshPartSlots,
            int WsModels,
            double ExpectedInstances);

        private sealed class AssetCheckClassSummary
        {
            public int SourceMaterials { get; set; }
            public int VanillaMaterials { get; set; }
            public int DistinctTextures { get; set; }
            public int KnownConstantTextures { get; set; }
            public int MeshPartSlots { get; set; }
            public int WsModels { get; set; }
            public int Rigids { get; set; }
            public int Roots { get; set; }
            public double ExpectedInstances { get; set; }
        }

        private sealed class AssetChecksResult
        {
            // Filled while the LOD audit walks the roots.
            public Dictionary<string, AssetCheckComponent> Components { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, AssetCheckRigid> Rigids { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public List<AssetCheckRootExposure> Roots { get; } = [];
            public TimeSpan CollectElapsed { get; set; }
            public string? CollectionFailure { get; set; }

            // Run state.
            public bool Ran { get; set; }
            public TimeSpan TimeBudget { get; set; }
            public TimeSpan Elapsed { get; set; }
            public TimeSpan MaterialElapsed { get; set; }
            public TimeSpan HashElapsed { get; set; }
            public TimeSpan DecodeElapsed { get; set; }
            public TimeSpan UnlockElapsed { get; set; }
            public string? Failure { get; set; }

            // Scope.
            public Dictionary<string, AssetCheckMaterial> Materials { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public int RigidCount { get; set; }
            public int SourceRigidCount { get; set; }
            public int DistinctTextureCount { get; set; }
            public int WeightedRootCount { get; set; }

            // Check 1.
            public Dictionary<string, AssetCheckTextureAlpha> TextureAlpha { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public int AlphaTexturesToAssess { get; set; }
            public int AlphaTexturesDecoded { get; set; }
            public int AlphaTexturesNotAssessed { get; set; }
            public long AlphaTexelsDecoded { get; set; }
            public Dictionary<AssetCheckAlphaStatus, AssetCheckClassSummary> Classes { get; } = [];
            public List<AssetCheckRankedMaterial> TopOpaqueMaterials { get; } = [];
            public double TotalExpectedPartInstances { get; set; }

            // Check 2: textures.
            public int SourceTextureCount { get; set; }
            public int VanillaReferencedTextureCount { get; set; }
            public int UnresolvedTextureCount { get; set; }
            public int HashedFileCount { get; set; }
            public long HashedByteCount { get; set; }
            public int HashFailureCount { get; set; }
            public int HashNotAssessedCount { get; set; }
            public int DuplicateGroupCount { get; set; }
            public int DuplicateFileCount { get; set; }
            public int RedundantFileCount { get; set; }
            public long RedundantBytes { get; set; }
            public long RedundantBcnBytes { get; set; }
            public int GroupsWithBcnEstimate { get; set; }
            public int SourceTexturesMatchingVanilla { get; set; }
            public List<AssetCheckTextureGroup> TopTextureGroups { get; } = [];
            public Dictionary<string, string> CanonicalTextures { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            // Check 2: materials.
            public AssetCheckGroupCounts AsIsAll { get; set; } = new(0, 0, 0);
            public AssetCheckGroupCounts AsIsSource { get; set; } = new(0, 0, 0);
            public AssetCheckGroupCounts CanonicalAll { get; set; } = new(0, 0, 0);
            public AssetCheckGroupCounts CanonicalSource { get; set; } = new(0, 0, 0);
            public int NewCanonicalGroupCount { get; set; }
            public List<AssetCheckMaterialGroup> NewCanonicalGroups { get; } = [];

            // Merge unlock (parts of the same rigid and LOD).
            public Dictionary<string, AssetCheckUnit?[]> UnitsByWsModel { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public int UnitCount { get; set; }
            public int UnitsNeedingRmv { get; set; }
            public int UnitsNotEvaluated { get; set; }
            public int RigidsLoaded { get; set; }
            public int RigidLoadFailures { get; set; }
            public long[] PartsRemovable { get; } = new long[AssetChecksMeasureCount];
            public int[] UnitsAffected { get; } = new int[AssetChecksMeasureCount];
            public int[] RigidsAffected { get; } = new int[AssetChecksMeasureCount];
            public int[] SourceRigidsAffected { get; } = new int[AssetChecksMeasureCount];
            public double[] ExpectedDrawsSaved { get; } = new double[AssetChecksMeasureCount];
        }

        // Called by the LOD audit for every WSModel it resolves (once per WSModel).
        private static void ObserveAssetCheckComponent(
            BatchState state,
            CrossRigidAnalysisComponent component)
        {
            var checks = state.AssetChecks;
            if (checks == null)
                return;

            var started = Stopwatch.GetTimestamp();
            try
            {
                var wsModelPath = Normalize(component.WsModelPath);
                var rigidPath = Normalize(component.RigidPath);
                if (!checks.Components.ContainsKey(wsModelPath))
                {
                    checks.Components[wsModelPath] = new AssetCheckComponent(
                        wsModelPath,
                        rigidPath,
                        component.SourceOwnedWsModel,
                        component.SourceOwnedRigid,
                        component.MaterialAssignments);
                }

                if (!checks.Rigids.ContainsKey(rigidPath))
                {
                    checks.Rigids[rigidPath] = new AssetCheckRigid(
                        rigidPath,
                        component.SourceOwnedRigid,
                        component.Rigid.ModelList
                            .Select(lod => lod
                                .Select(model => model.Mesh.VertexList.Length)
                                .ToArray())
                            .ToArray());
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                checks.CollectionFailure ??= $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                checks.CollectElapsed += Stopwatch.GetElapsedTime(started);
            }
        }

        // Called by the LOD audit once per audited root with the probability-weighted
        // instance count of every WSModel one entity of that root draws.
        private static void ObserveAssetCheckRoot(
            BatchState state,
            string vmdPath,
            Dictionary<string, double> instanceWeightByWsModel)
        {
            state.AssetChecks?.Roots.Add(
                new AssetCheckRootExposure(vmdPath, instanceWeightByWsModel));
        }

        private static void RunAssetChecks(
            BatchState state,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress,
            TimeSpan? timeBudget = null,
            Func<string, AssetCheckRmvKeys[][]?>? rmvKeyProvider = null)
        {
            var checks = state.AssetChecks ??= new AssetChecksResult();
            checks.Ran = true;
            checks.TimeBudget =
                timeBudget ?? TimeSpan.FromSeconds(AssetChecksDefaultBudgetSeconds);
            var timer = Stopwatch.StartNew();
            var hashDeadline = checks.TimeBudget * AssetChecksHashBudgetShare;
            var decodeDeadline = checks.TimeBudget * AssetChecksDecodeBudgetShare;
            try
            {
                var phase = Stopwatch.StartNew();
                CollectAssetCheckMaterials(state, checks, cancellationToken, progress);
                checks.MaterialElapsed = phase.Elapsed;

                phase.Restart();
                FindAssetCheckDuplicateTextures(
                    state,
                    checks,
                    timer,
                    hashDeadline,
                    cancellationToken,
                    progress);
                checks.HashElapsed = phase.Elapsed;
                BuildAssetCheckDuplicateMaterials(checks);

                phase.Restart();
                AssessAssetCheckAlpha(
                    state,
                    checks,
                    timer,
                    decodeDeadline,
                    cancellationToken,
                    progress);
                checks.DecodeElapsed = phase.Elapsed;

                phase.Restart();
                var exposure = BuildAssetCheckExposure(state, checks);
                EvaluateAssetCheckUnlock(
                    state,
                    checks,
                    timer,
                    checks.TimeBudget,
                    cancellationToken,
                    progress,
                    rmvKeyProvider ?? (rigidPath => LoadAssetCheckRmvKeys(state, rigidPath)));
                SummarizeAssetChecks(checks, exposure);
                checks.UnlockElapsed = phase.Elapsed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The checks are diagnostic: they must never fail a run or the output pack.
                checks.Failure = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                checks.Elapsed = timer.Elapsed;
            }
        }

        // ---- Materials ----------------------------------------------------------

        private static void CollectAssetCheckMaterials(
            BatchState state,
            AssetChecksResult checks,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var materialPaths = checks.Components.Values
                .SelectMany(component => component.MaterialAssignments)
                .SelectMany(lod => lod)
                .Select(Normalize)
                .Where(path => path.Length != 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            checks.RigidCount = checks.Rigids.Count;
            checks.SourceRigidCount = checks.Rigids.Values.Count(rigid => rigid.SourceOwned);

            for (var index = 0; index < materialPaths.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = materialPaths[index];
                ReportProgress(
                    progress,
                    "Asset checks: reading materials",
                    index + 1,
                    materialPaths.Count,
                    path);
                var material = new AssetCheckMaterial(path, state.Source.ContainsFile(path));
                ReadAssetCheckMaterial(state, material);
                checks.Materials[path] = material;
            }

            var observedShaders = new HashSet<string>(
                checks.Materials.Values
                    .Where(material => material.Document != null)
                    .Select(material => material.Shader),
                StringComparer.OrdinalIgnoreCase);
            foreach (var material in checks.Materials.Values.Where(item => item.Document != null))
                AssessAssetCheckMaterialShader(material, observedShaders);
        }

        // Private parse: GetMaterialDocument would fill state.MaterialDocuments and bump the
        // gameplay-traversal parse counter, both of which later stages and sections use.
        private static void ReadAssetCheckMaterial(BatchState state, AssetCheckMaterial material)
        {
            var pathIdentity = $"path:{material.Path}";
            material.AsIsIdentity = pathIdentity;
            try
            {
                var file = FindForReadStatic(state, material.Path);
                if (file == null)
                {
                    material.UnreadableReason = "material file not found";
                    return;
                }

                var document = LoadXml(file);
                material.Shader = Normalize(
                    document.SelectSingleNode("/material/shader")?.InnerText);
                var textureNodes = document.SelectNodes("/material/textures/texture");
                if (textureNodes != null)
                {
                    foreach (XmlNode node in textureNodes)
                    {
                        var slot = GetTextureSlot(node);
                        if (slot.Length == 0)
                            continue;

                        material.Textures.TryAdd(
                            slot,
                            Normalize(node.SelectSingleNode("source")?.InnerText ?? node.InnerText));
                    }
                }

                material.HasAlphaPixelsColour =
                    document.SelectNodes("/material/params/param")?
                        .Cast<XmlNode>()
                        .Any(node => node.SelectSingleNode("name")?.InnerText.Trim().Equals(
                            "alpha_pixels_colour",
                            StringComparison.OrdinalIgnoreCase) == true) == true;
                material.AsIsIdentity = BuildAssetCheckMaterialIdentity(document, null, null);
                material.Document = document;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                material.Document = null;
                material.Textures.Clear();
                material.UnreadableReason =
                    $"material XML could not be read ({ex.GetType().Name})";
                material.AsIsIdentity = pathIdentity;
            }
            finally
            {
                material.Check1Identity = material.AsIsIdentity;
                material.Check2Identity = material.AsIsIdentity;
                material.CombinedIdentity = material.AsIsIdentity;
            }
        }

        private static void AssessAssetCheckMaterialShader(
            AssetCheckMaterial material,
            ISet<string> observedShaders)
        {
            var assessment = AssessAssetCheckAlphaShader(material.Shader, observedShaders);
            material.TwinShader = assessment.TwinShader;
            if (!assessment.IsAlphaShader)
            {
                material.Status = AssetCheckAlphaStatus.NotAlphaShader;
                return;
            }

            if (!assessment.IsCandidate)
            {
                material.Status = AssetCheckAlphaStatus.NotAssessed;
                material.NotAssessedReason = assessment.Reason;
                return;
            }

            if (material.HasAlphaPixelsColour)
            {
                material.Status = AssetCheckAlphaStatus.NotAssessed;
                material.NotAssessedReason = "has an alpha_pixels_colour parameter (alpha tint)";
                return;
            }

            var (alphaTexture, ambiguous) = FindAssetCheckAlphaSource(material.Textures);
            if (ambiguous)
            {
                material.Status = AssetCheckAlphaStatus.NotAssessed;
                material.NotAssessedReason = "several different alpha source textures";
                return;
            }

            material.AlphaTexturePath = alphaTexture;
            // Candidates stay "not assessed (budget)" until their texture is decoded.
            material.Status = alphaTexture.Length == 0
                ? AssetCheckAlphaStatus.NoAlphaTexture
                : AssetCheckAlphaStatus.BudgetExhausted;
        }

        // A shader is assessable when it is a plain _alpha variant of one of the character
        // families and its non-alpha twin (same path without _alpha) exists. The twin of
        // these families exists in the game data and in the editor's own templates; for any
        // other _alpha shader the twin is only reported when a scoped material uses it.
        private static AssetCheckShaderAssessment AssessAssetCheckAlphaShader(
            string? shader,
            ISet<string>? observedShaders)
        {
            var normalized = Normalize(shader);
            if (!normalized.Contains("alpha", StringComparison.Ordinal))
                return new AssetCheckShaderAssessment(false, false, string.Empty, string.Empty);

            const string alphaSuffix = "_alpha.xml.shader";
            if (!normalized.EndsWith(alphaSuffix, StringComparison.Ordinal))
            {
                return new AssetCheckShaderAssessment(
                    true,
                    false,
                    string.Empty,
                    "different alpha mechanism (not a plain _alpha shader, e.g. alpha tint)");
            }

            var stem = normalized[..^alphaSuffix.Length];
            var twin = stem + ".xml.shader";
            if (AssetCheckPlainAlphaShaderStems.Contains(stem, StringComparer.Ordinal))
                return new AssetCheckShaderAssessment(true, true, twin, string.Empty);

            return new AssetCheckShaderAssessment(
                true,
                false,
                twin,
                observedShaders?.Contains(twin) == true
                    ? "non-alpha twin exists, but this family's alpha source is not verified"
                    : "no non-alpha twin shader among the scoped materials");
        }

        private static (string Path, bool Ambiguous) FindAssetCheckAlphaSource(
            IReadOnlyDictionary<string, string> textures)
        {
            var present = AssetCheckAlphaSourceSlots
                .Select(slot => textures.TryGetValue(slot, out var path) ? path : string.Empty)
                .Where(path => path.Length != 0 && !IsTexturePlaceholder(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return present.Count switch
            {
                0 => (string.Empty, false),
                1 => (present[0], false),
                _ => (string.Empty, true),
            };
        }

        // The merge identity of a material as GetMeshMergeMaterialIdentity forms it
        // ("render:" + normalised XML without the name), optionally with every texture path
        // replaced by a canonical one and/or the shader replaced.
        private static string BuildAssetCheckMaterialIdentity(
            XmlDocument material,
            IReadOnlyDictionary<string, string>? canonicalTextures,
            string? replacementShader)
        {
            if (canonicalTextures == null && replacementShader == null)
                return "render:" + GetMaterialRenderingIdentity(material);

            var clone = new XmlDocument();
            clone.LoadXml(material.OuterXml);
            if (replacementShader != null)
            {
                var shaderNode = clone.SelectSingleNode("/material/shader");
                if (shaderNode != null)
                    shaderNode.InnerText = replacementShader;
            }

            var textureNodes = canonicalTextures == null
                ? null
                : clone.SelectNodes("/material/textures/texture");
            if (canonicalTextures != null && textureNodes != null)
            {
                foreach (XmlNode textureNode in textureNodes)
                {
                    var sourceNode = textureNode.SelectSingleNode("source");
                    var current = Normalize(sourceNode?.InnerText ?? textureNode.InnerText);
                    if (!canonicalTextures.TryGetValue(current, out var canonical))
                        continue;

                    if (sourceNode != null)
                        sourceNode.InnerText = canonical;
                    else if (textureNode.ChildNodes.Count == 1 &&
                             textureNode.FirstChild?.NodeType == XmlNodeType.Text)
                        textureNode.InnerText = canonical;
                }
            }

            return "render:" + GetMaterialRenderingIdentity(clone);
        }

        // ---- Duplicate textures and materials (check 2) -------------------------

        private static long GetAssetCheckContentLength(PackFile file)
            => file.DataSource is PackedFileSource { IsCompressed: true } packed
                ? packed.UncompressedSize
                : file.DataSource.Size;

        private static string HashAssetCheckContent(byte[] data)
            => Convert.ToHexString(SHA256.HashData(data));

        // Groups of two or more paths with the same length and hash, paths ordered, groups
        // ordered by their first path.
        private static List<List<string>> GroupAssetCheckContent(
            IEnumerable<(string Path, long Length, string Hash)> files)
        {
            return files
                .GroupBy(file => (file.Length, file.Hash))
                .Select(group => group
                    .Select(file => file.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToList())
                .Where(paths => paths.Count >= 2)
                .OrderBy(paths => paths[0], StringComparer.Ordinal)
                .ToList();
        }

        private sealed record AssetCheckHashedTexture(
            string Path,
            bool SourceOwned,
            long Length,
            string Hash,
            long BcnBytes);

        private static void FindAssetCheckDuplicateTextures(
            BatchState state,
            AssetChecksResult checks,
            Stopwatch timer,
            TimeSpan deadline,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var referenced = checks.Materials.Values
                .SelectMany(material => material.Textures.Values)
                .Where(path => path.Length != 0 && !IsTexturePlaceholder(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            checks.DistinctTextureCount = referenced.Count;

            // Source textures, plus the vanilla textures the same materials reference. The
            // vanilla packs themselves are never scanned.
            var containers = GetGameplayTraversalContainers(state);
            var entries = new List<(string Path, bool SourceOwned, PackFile File, long Length)>();
            foreach (var path in referenced)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceFile = state.Source.FindFile(path);
                var sourceOwned = sourceFile != null;
                var file = sourceFile;
                if (file == null)
                {
                    file = ResolveGameplayTraversalContainer(state.Source, containers, path)?
                        .FindFile(path);
                }

                if (file == null)
                {
                    checks.UnresolvedTextureCount++;
                    continue;
                }

                if (sourceOwned)
                    checks.SourceTextureCount++;
                else
                    checks.VanillaReferencedTextureCount++;

                try
                {
                    entries.Add((path, sourceOwned, file, GetAssetCheckContentLength(file)));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    checks.HashFailureCount++;
                }
            }

            // Length first: only files that share a length with another file (and where at
            // least one of them is a source texture) can be duplicates, so only those are read.
            var candidates = entries
                .GroupBy(entry => entry.Length)
                .Where(group => group.Count() >= 2 && group.Any(entry => entry.SourceOwned))
                .SelectMany(group => group)
                .OrderByDescending(entry => entry.SourceOwned)
                .ThenBy(entry => entry.Path, StringComparer.Ordinal)
                .ToList();
            var hashed = new List<AssetCheckHashedTexture>(candidates.Count);
            for (var index = 0; index < candidates.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (timer.Elapsed > deadline)
                {
                    checks.HashNotAssessedCount = candidates.Count - index;
                    break;
                }

                var candidate = candidates[index];
                ReportProgress(
                    progress,
                    "Asset checks: comparing textures",
                    index + 1,
                    candidates.Count,
                    candidate.Path);
                try
                {
                    var data = candidate.File.DataSource.ReadData();
                    var bcnBytes = DdsBcnResidencyEstimator.TryEstimate(
                        data.AsSpan(0, Math.Min(148, data.Length)),
                        out var estimate)
                        ? estimate.Bytes
                        : 0;
                    hashed.Add(new AssetCheckHashedTexture(
                        candidate.Path,
                        candidate.SourceOwned,
                        data.Length,
                        HashAssetCheckContent(data),
                        bcnBytes));
                    checks.HashedFileCount++;
                    checks.HashedByteCount += data.Length;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    checks.HashFailureCount++;
                }
            }

            var byPath = hashed
                .Where(texture => texture.SourceOwned)
                .ToDictionary(texture => texture.Path, StringComparer.OrdinalIgnoreCase);
            var groups = GroupAssetCheckContent(
                hashed
                    .Where(texture => texture.SourceOwned)
                    .Select(texture => (texture.Path, texture.Length, texture.Hash)));
            foreach (var paths in groups)
            {
                var representative = byPath[paths[0]];
                checks.DuplicateGroupCount++;
                checks.DuplicateFileCount += paths.Count;
                checks.RedundantFileCount += paths.Count - 1;
                checks.RedundantBytes += (paths.Count - 1L) * representative.Length;
                if (representative.BcnBytes > 0)
                {
                    checks.GroupsWithBcnEstimate++;
                    checks.RedundantBcnBytes += (paths.Count - 1L) * representative.BcnBytes;
                }

                foreach (var path in paths)
                    checks.CanonicalTextures[path] = paths[0];
            }

            foreach (var paths in groups
                         .OrderByDescending(paths =>
                             (paths.Count - 1L) * byPath[paths[0]].Length)
                         .ThenByDescending(paths => paths.Count)
                         .ThenBy(paths => paths[0], StringComparer.Ordinal)
                         .Take(AssetChecksTextureGroupLimit))
            {
                var representative = byPath[paths[0]];
                checks.TopTextureGroups.Add(new AssetCheckTextureGroup(
                    representative.Length,
                    paths,
                    representative.BcnBytes));
            }

            var vanillaKeys = hashed
                .Where(texture => !texture.SourceOwned)
                .Select(texture => (texture.Length, texture.Hash))
                .ToHashSet();
            checks.SourceTexturesMatchingVanilla = hashed.Count(texture =>
                texture.SourceOwned && vanillaKeys.Contains((texture.Length, texture.Hash)));
        }

        private static AssetCheckGroupCounts CountAssetCheckGroups(
            IEnumerable<string> identities)
        {
            var groups = identities
                .GroupBy(identity => identity, StringComparer.Ordinal)
                .Where(group => group.Count() >= 2)
                .ToList();
            var files = groups.Sum(group => group.Count());
            return new AssetCheckGroupCounts(groups.Count, files, files - groups.Count);
        }

        private static void BuildAssetCheckDuplicateMaterials(AssetChecksResult checks)
        {
            var touchedByCanonicalisation = checks.CanonicalTextures.Count != 0;
            foreach (var material in checks.Materials.Values.Where(item => item.Document != null))
            {
                material.Check2Identity = touchedByCanonicalisation
                    ? BuildAssetCheckMaterialIdentity(
                        material.Document!,
                        checks.CanonicalTextures,
                        null)
                    : material.AsIsIdentity;
            }

            var materials = checks.Materials.Values.ToList();
            var sourceMaterials = materials.Where(material => material.SourceOwned).ToList();
            checks.AsIsAll = CountAssetCheckGroups(materials.Select(m => m.AsIsIdentity));
            checks.AsIsSource = CountAssetCheckGroups(sourceMaterials.Select(m => m.AsIsIdentity));
            checks.CanonicalAll = CountAssetCheckGroups(materials.Select(m => m.Check2Identity));
            checks.CanonicalSource =
                CountAssetCheckGroups(sourceMaterials.Select(m => m.Check2Identity));

            var newGroups = materials
                .Where(material => material.Document != null)
                .GroupBy(material => material.Check2Identity, StringComparer.Ordinal)
                .Select(group => group
                    .OrderBy(material => material.Path, StringComparer.Ordinal)
                    .ToList())
                .Where(group => group.Count >= 2 &&
                                group.Select(material => material.AsIsIdentity)
                                    .Distinct(StringComparer.Ordinal)
                                    .Count() >= 2)
                .OrderByDescending(group => group.Count)
                .ThenBy(group => group[0].Path, StringComparer.Ordinal)
                .ToList();
            checks.NewCanonicalGroupCount = newGroups.Count;
            foreach (var group in newGroups.Take(AssetChecksMaterialGroupLimit))
            {
                var other = group.First(material =>
                    !material.AsIsIdentity.Equals(group[0].AsIsIdentity, StringComparison.Ordinal));
                checks.NewCanonicalGroups.Add(new AssetCheckMaterialGroup(
                    group.Count,
                    group.Select(material => material.AsIsIdentity)
                        .Distinct(StringComparer.Ordinal)
                        .Count(),
                    group.Take(3).Select(material => material.Path).ToList(),
                    DescribeAssetCheckTextureDifferences(group[0], other)));
            }
        }

        private static List<string> DescribeAssetCheckTextureDifferences(
            AssetCheckMaterial left,
            AssetCheckMaterial right)
        {
            var differences = new List<string>();
            foreach (var slot in left.Textures.Keys
                         .Union(right.Textures.Keys, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(slot => slot, StringComparer.Ordinal))
            {
                var leftPath = left.Textures.GetValueOrDefault(slot, string.Empty);
                var rightPath = right.Textures.GetValueOrDefault(slot, string.Empty);
                if (!leftPath.Equals(rightPath, StringComparison.OrdinalIgnoreCase))
                    differences.Add($"{slot}: {leftPath} | {rightPath}");
            }

            return differences;
        }

        // ---- Alpha (check 1) ----------------------------------------------------

        // Alpha statistics of one 4-byte-per-pixel image level (alpha is byte 3 of each
        // pixel in Pfim's Rgba32 layout), rows `stride` bytes apart.
        private static AssetCheckAlphaStatistics ComputeAssetCheckAlphaStatistics(
            byte[] pixels,
            int width,
            int height,
            int stride)
        {
            if (width <= 0 || height <= 0 || stride < width * 4 ||
                pixels.Length < (long)(height - 1) * stride + width * 4L)
            {
                throw new ArgumentException("Pixel buffer does not match the image size.");
            }

            var minAlpha = 255;
            long below255 = 0;
            long below128 = 0;
            for (var y = 0; y < height; y++)
            {
                var row = pixels.AsSpan(y * stride, width * 4);
                for (var offset = 3; offset < row.Length; offset += 4)
                {
                    var alpha = row[offset];
                    if (alpha == 255)
                        continue;

                    below255++;
                    if (alpha < 128)
                        below128++;
                    if (alpha < minAlpha)
                        minAlpha = alpha;
                }
            }

            return new AssetCheckAlphaStatistics((long)width * height, minAlpha, below255, below128);
        }

        // OPAQUE: min alpha >= 250. MOSTLY OPAQUE: fewer than 0.1 % of texels below 128.
        // Everything else uses alpha.
        private static AssetCheckAlphaStatus ClassifyAssetCheckAlpha(
            AssetCheckAlphaStatistics statistics)
        {
            if (statistics.Texels <= 0)
                return AssetCheckAlphaStatus.Unreadable;
            if (statistics.MinAlpha >= AssetChecksOpaqueMinimumAlpha)
                return AssetCheckAlphaStatus.Opaque;
            return statistics.Below128 * AssetChecksMostlyOpaqueTexelsPerLowAlphaTexel <
                   statistics.Texels
                ? AssetCheckAlphaStatus.MostlyOpaque
                : AssetCheckAlphaStatus.UsesAlpha;
        }

        private static AssetCheckTextureAlpha UnreadableAssetCheckTexture(string reason)
            => new(
                AssetCheckAlphaStatus.Unreadable,
                default,
                reason,
                0,
                0,
                string.Empty);

        // Full-resolution decode with Pfim, the decoder the atlas builder and the
        // uniform-constant probe use. Level 0 only is scanned; Pfim decodes the mip chain
        // as well, which is part of the measured cost. Nothing is cached.
        private static AssetCheckTextureAlpha DecodeAssetCheckAlpha(byte[] ddsBytes)
        {
            try
            {
                using var stream = new MemoryStream(ddsBytes);
                using var image = Pfim.Pfimage.FromStream(stream);
                var format = image.Format.ToString();
                switch (image.Format)
                {
                    case Pfim.ImageFormat.Rgba32:
                    {
                        var statistics = ComputeAssetCheckAlphaStatistics(
                            image.Data,
                            image.Width,
                            image.Height,
                            image.Stride);
                        return new AssetCheckTextureAlpha(
                            ClassifyAssetCheckAlpha(statistics),
                            statistics,
                            string.Empty,
                            image.Width,
                            image.Height,
                            format);
                    }
                    case Pfim.ImageFormat.Rgb24:
                    case Pfim.ImageFormat.Rgb8:
                    case Pfim.ImageFormat.R5g5b5:
                    case Pfim.ImageFormat.R5g6b5:
                    {
                        // No alpha channel: sampling alpha returns 1.
                        var statistics = new AssetCheckAlphaStatistics(
                            (long)image.Width * image.Height,
                            255,
                            0,
                            0);
                        return new AssetCheckTextureAlpha(
                            AssetCheckAlphaStatus.Opaque,
                            statistics,
                            "no alpha channel",
                            image.Width,
                            image.Height,
                            format);
                    }
                    default:
                        return UnreadableAssetCheckTexture(
                            $"unsupported decoded pixel format {format}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return UnreadableAssetCheckTexture($"decode failed ({ex.GetType().Name})");
            }
        }

        private static AssetCheckTextureAlpha AssessAssetCheckTexture(
            BatchState state,
            string texturePath)
        {
            byte[] data;
            try
            {
                var file = FindForReadStatic(state, texturePath);
                if (file == null)
                    return UnreadableAssetCheckTexture("texture file not found");

                data = file.DataSource.ReadData();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return UnreadableAssetCheckTexture($"texture could not be read ({ex.GetType().Name})");
            }

            return DecodeAssetCheckAlpha(data);
        }

        private static void AssessAssetCheckAlpha(
            BatchState state,
            AssetChecksResult checks,
            Stopwatch timer,
            TimeSpan deadline,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var pending = checks.Materials.Values
                .Where(material => material.Status == AssetCheckAlphaStatus.BudgetExhausted)
                .ToList();
            // Source-owned and most-used textures first, so an exhausted budget leaves the
            // least interesting ones unassessed.
            var textures = pending
                .GroupBy(material => material.AlphaTexturePath, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Any(material => material.SourceOwned))
                .ThenByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Key)
                .ToList();
            checks.AlphaTexturesToAssess = textures.Count;
            for (var index = 0; index < textures.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (timer.Elapsed > deadline)
                {
                    checks.AlphaTexturesNotAssessed = textures.Count - index;
                    break;
                }

                ReportProgress(
                    progress,
                    "Asset checks: decoding alpha",
                    index + 1,
                    textures.Count,
                    textures[index]);
                var result = AssessAssetCheckTexture(state, textures[index]);
                checks.TextureAlpha[textures[index]] = result;
                checks.AlphaTexturesDecoded++;
                checks.AlphaTexelsDecoded += result.Statistics.Texels;
            }

            foreach (var material in pending)
            {
                if (!checks.TextureAlpha.TryGetValue(material.AlphaTexturePath, out var result))
                    continue;

                material.Status = result.Status;
                material.MinAlpha = result.Status == AssetCheckAlphaStatus.Unreadable
                    ? -1
                    : result.Statistics.MinAlpha;
            }
        }

        // ---- Merge unlock -------------------------------------------------------

        // RMV-side merge keys of one part: GetRmvMergeIdentity as authored, and the same
        // identity with the render flag dropped (what tier T4 does about the flag).
        private static AssetCheckRmvKeys BuildAssetCheckRmvKeys(RmvModel model)
            => new(GetRmvMergeIdentity(model), GetAssetCheckRmvIdentityIgnoringRenderFlag(model));

        // Mirrors GetRmvMergeIdentity field for field, with the render flag replaced by a
        // constant. It deliberately does not apply the weighted2/weighted4 unification of
        // GetCrossRigidIdealMergeRmvIdentity: parts of one rigid share a vertex format.
        private static string GetAssetCheckRmvIdentityIgnoringRenderFlag(RmvModel model)
        {
            var material = model.Material.Clone();
            if (material is WeightedMaterial weighted)
            {
                weighted.ModelName = string.Empty;
                weighted.TextureDirectory = string.Empty;
                weighted.TexturesParams = [];
            }

            var materialBytes = MaterialFactory.Create().Save(
                model.CommonHeader.ModelTypeFlag,
                material);
            return ContentHash(
                string.Join(
                    "|",
                    model.CommonHeader.ModelTypeFlag,
                    "*",
                    model.Material.BinaryVertexFormat,
                    model.CommonHeader.ShaderParams.ShaderName,
                    Convert.ToHexString(materialBytes)));
        }

        // Re-reads one source rigid (the audit keeps no geometry) and keys every part.
        private static AssetCheckRmvKeys[][]? LoadAssetCheckRmvKeys(
            BatchState state,
            string rigidPath)
        {
            var path = Normalize(rigidPath);
            var file = ResolveGameplayTraversalContainer(
                    state.Source,
                    GetGameplayTraversalContainers(state),
                    path)?
                .FindFile(path);
            if (file == null)
                return null;

            var rigid = ModelFactory.Create().Load(file.DataSource.ReadData());
            return rigid.ModelList
                .Select(lod => lod.Select(BuildAssetCheckRmvKeys).ToArray())
                .ToArray();
        }

        // Draws after same-rigid merging for parts under the given identities: parts with
        // equal identity combine up to the 16-bit vertex limit, like the baseline.
        private static int CountAssetCheckDraws(
            IReadOnlyList<int> vertexCounts,
            IReadOnlyList<string> identities)
        {
            if (vertexCounts.Count != identities.Count)
                throw new ArgumentException("Every part needs a vertex count and an identity.");

            return Enumerable.Range(0, identities.Count)
                .GroupBy(index => identities[index], StringComparer.Ordinal)
                .Sum(group => CountCrossRigidVertexLimitedMergeBins(
                    group.Select(index => vertexCounts[index])));
        }

        // Parts that stop needing their own draw when the parts of one rigid and LOD are
        // compared by `variantIdentities` instead of `baselineIdentities`.
        private static int CountAssetCheckRemovableParts(
            IReadOnlyList<int> vertexCounts,
            IReadOnlyList<string> baselineIdentities,
            IReadOnlyList<string> variantIdentities)
            => Math.Max(
                0,
                CountAssetCheckDraws(vertexCounts, baselineIdentities) -
                CountAssetCheckDraws(vertexCounts, variantIdentities));

        // Removable parts of one unit for every measure. Each measure subtracts the draws
        // under the changed material identities from the draws under the as-is ones with
        // the same RMV treatment on both sides, so only the material change is counted.
        private static int[] EvaluateAssetCheckUnit(
            IReadOnlyList<int> vertexCounts,
            IReadOnlyList<AssetCheckRmvKeys> rmvKeys,
            IReadOnlyList<AssetCheckPartMaterial> parts)
        {
            List<string> Identities(bool flagIgnored, Func<AssetCheckPartMaterial, string> material)
                => parts
                    .Select((part, index) =>
                        (flagIgnored ? rmvKeys[index].FlagIgnored : rmvKeys[index].Strict) +
                        "\u001e" +
                        material(part))
                    .ToList();

            var strictAsIs = Identities(false, part => part.AsIs);
            var flagIgnoredAsIs = Identities(true, part => part.AsIs);
            var removable = new int[AssetChecksMeasureCount];
            removable[AssetChecksMeasureCheck1] = CountAssetCheckRemovableParts(
                vertexCounts,
                strictAsIs,
                Identities(false, part => part.Check1));
            removable[AssetChecksMeasureCheck1FlagIgnored] = CountAssetCheckRemovableParts(
                vertexCounts,
                flagIgnoredAsIs,
                Identities(true, part => part.Check1));
            removable[AssetChecksMeasureCheck2] = CountAssetCheckRemovableParts(
                vertexCounts,
                strictAsIs,
                Identities(false, part => part.Check2));
            removable[AssetChecksMeasureCombined] = CountAssetCheckRemovableParts(
                vertexCounts,
                strictAsIs,
                Identities(false, part => part.Combined));
            removable[AssetChecksMeasureCombinedFlagIgnored] = CountAssetCheckRemovableParts(
                vertexCounts,
                flagIgnoredAsIs,
                Identities(true, part => part.Combined));
            return removable;
        }

        // Cheap material-only precheck: the RMV identity can only keep parts apart, so a
        // unit whose parts fall into the same number of classes under every changed material
        // identity cannot unlock anything and its rigid does not need to be re-read.
        private static bool AssetCheckUnitCanMerge(IReadOnlyList<AssetCheckPartMaterial> parts)
        {
            int Classes(Func<AssetCheckPartMaterial, string> identity)
                => parts.Select(identity).Distinct(StringComparer.Ordinal).Count();

            var asIs = Classes(part => part.AsIs);
            return Classes(part => part.Check1) < asIs ||
                   Classes(part => part.Check2) < asIs ||
                   Classes(part => part.Combined) < asIs;
        }

        private sealed record AssetCheckExposure(
            int[] ScenarioLods,
            double[] Probabilities,
            Dictionary<string, double> FactorByWsModel);

        // Sets the identities that depend on the alpha classification and computes, per
        // WSModel, the army weight of one instantiation. CalculateExpectedCrossRigid-
        // ArmyDrawWeight is linear in (LOD probability x value), so one call per root with
        // a unit value gives the root's weight; a value per scenario LOD then scales it,
        // exactly as the LOD audit does for triangles.
        private static AssetCheckExposure BuildAssetCheckExposure(
            BatchState state,
            AssetChecksResult checks)
        {
            foreach (var material in checks.Materials.Values.Where(item => item.Document != null))
            {
                var replacement = material.Status == AssetCheckAlphaStatus.Opaque
                    ? material.TwinShader
                    : null;
                material.Check1Identity = replacement == null
                    ? material.AsIsIdentity
                    : BuildAssetCheckMaterialIdentity(material.Document!, null, replacement);
                material.CombinedIdentity =
                    replacement == null &&
                    material.Check2Identity.Equals(material.AsIsIdentity, StringComparison.Ordinal)
                        ? material.AsIsIdentity
                        : BuildAssetCheckMaterialIdentity(
                            material.Document!,
                            checks.CanonicalTextures.Count == 0 ? null : checks.CanonicalTextures,
                            replacement);
            }

            var audit = state.LodAudit;
            var factors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in checks.Roots)
            {
                var unitWeight = state.ArmyResidencyModel == null
                    ? 0
                    : CalculateExpectedCrossRigidArmyDrawWeight(
                        state,
                        root.VmdPath,
                        1.0,
                        [(1.0, 1.0)]);
                if (unitWeight <= 0)
                    continue;

                checks.WeightedRootCount++;
                foreach (var (wsModelPath, weight) in root.InstanceWeightByWsModel)
                {
                    factors[wsModelPath] =
                        factors.GetValueOrDefault(wsModelPath) + unitWeight * weight;
                }
            }

            return new AssetCheckExposure(
                audit?.ScenarioLods.ToArray() ?? [],
                audit?.ScenarioLodProbabilities.ToArray() ?? [],
                factors);
        }

        private static void EvaluateAssetCheckUnlock(
            BatchState state,
            AssetChecksResult checks,
            Stopwatch timer,
            TimeSpan deadline,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress,
            Func<string, AssetCheckRmvKeys[][]?> rmvKeyProvider)
        {
            var unitsByKey = new Dictionary<string, AssetCheckUnit>(StringComparer.Ordinal);
            foreach (var component in checks.Components.Values
                         .OrderBy(item => item.WsModelPath, StringComparer.OrdinalIgnoreCase))
            {
                if (!checks.Rigids.TryGetValue(component.RigidPath, out var rigid))
                    continue;

                var perLod = new AssetCheckUnit?[component.MaterialAssignments.Length];
                for (var lodIndex = 0; lodIndex < perLod.Length; lodIndex++)
                {
                    var assignments = component.MaterialAssignments[lodIndex];
                    if (lodIndex >= rigid.VertexCounts.Length)
                        continue;

                    var vertexCounts = rigid.VertexCounts[lodIndex];
                    var partCount = Math.Min(assignments.Length, vertexCounts.Length);
                    if (partCount == 0)
                        continue;

                    var key = $"{rigid.RigidPath}|{lodIndex}|" +
                              string.Join("|", assignments.Take(partCount));
                    if (!unitsByKey.TryGetValue(key, out var unit))
                    {
                        unit = new AssetCheckUnit(
                            key,
                            rigid.RigidPath,
                            rigid.SourceOwned,
                            lodIndex,
                            vertexCounts.Take(partCount).ToArray(),
                            assignments.Take(partCount)
                                .Select(path => DescribeAssetCheckPartMaterial(checks, path))
                                .ToArray());
                        unitsByKey[key] = unit;
                    }

                    perLod[lodIndex] = unit;
                }

                checks.UnitsByWsModel[component.WsModelPath] = perLod;
            }

            checks.UnitCount = unitsByKey.Count;
            var pending = new List<AssetCheckUnit>();
            foreach (var unit in unitsByKey.Values)
            {
                if (AssetCheckUnitCanMerge(unit.Parts))
                    pending.Add(unit);
                else
                    unit.Evaluated = true;
            }

            checks.UnitsNeedingRmv = pending.Count;
            var rigidGroups = pending
                .GroupBy(unit => unit.RigidPath, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.First().SourceOwnedRigid)
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            for (var index = 0; index < rigidGroups.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var group = rigidGroups[index];
                if (timer.Elapsed > deadline)
                {
                    checks.UnitsNotEvaluated += group.Count();
                    continue;
                }

                ReportProgress(
                    progress,
                    "Asset checks: comparing rigid parts",
                    index + 1,
                    rigidGroups.Count,
                    group.Key);
                AssetCheckRmvKeys[][]? keys = null;
                try
                {
                    keys = rmvKeyProvider(group.Key);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    keys = null;
                }

                if (keys == null)
                {
                    checks.RigidLoadFailures++;
                    checks.UnitsNotEvaluated += group.Count();
                    continue;
                }

                checks.RigidsLoaded++;
                foreach (var unit in group)
                {
                    if (unit.LodIndex >= keys.Length ||
                        keys[unit.LodIndex].Length < unit.Parts.Length)
                    {
                        checks.UnitsNotEvaluated++;
                        continue;
                    }

                    unit.Removable = EvaluateAssetCheckUnit(
                        unit.VertexCounts,
                        keys[unit.LodIndex].Take(unit.Parts.Length).ToArray(),
                        unit.Parts);
                    unit.Evaluated = true;
                }
            }
        }

        private static AssetCheckPartMaterial DescribeAssetCheckPartMaterial(
            AssetChecksResult checks,
            string materialPath)
        {
            var path = Normalize(materialPath);
            if (!checks.Materials.TryGetValue(path, out var material))
            {
                var identity = $"path:{path}";
                return new AssetCheckPartMaterial(identity, identity, identity, identity);
            }

            return new AssetCheckPartMaterial(
                material.AsIsIdentity,
                material.Check1Identity,
                material.Check2Identity,
                material.CombinedIdentity);
        }

        // ---- Summary ------------------------------------------------------------

        private static void SummarizeAssetChecks(
            AssetChecksResult checks,
            AssetCheckExposure exposure)
        {
            foreach (var status in Enum.GetValues<AssetCheckAlphaStatus>())
            {
                if (status != AssetCheckAlphaStatus.NotAlphaShader)
                    checks.Classes[status] = new AssetCheckClassSummary();
            }

            foreach (var material in checks.Materials.Values.Where(item => item.Document != null))
            {
                if (!checks.Classes.TryGetValue(material.Status, out var summary))
                    continue;

                if (material.SourceOwned)
                    summary.SourceMaterials++;
                else
                    summary.VanillaMaterials++;
            }

            var focus = new[] { AssetCheckAlphaStatus.Opaque, AssetCheckAlphaStatus.MostlyOpaque };
            foreach (var status in focus)
            {
                var textures = checks.Materials.Values
                    .Where(material => material.Status == status)
                    .Select(material => material.AlphaTexturePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                checks.Classes[status].DistinctTextures = textures.Count;
                checks.Classes[status].KnownConstantTextures =
                    textures.Count(IsKnownConstantTexturePath);
            }

            var slotCounts = focus.ToDictionary(status => status, _ => 0);
            var wsModelsByStatus = focus.ToDictionary(
                status => status,
                _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            var rigidsByStatus = focus.ToDictionary(
                status => status,
                _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            var expectedByStatus = focus.ToDictionary(status => status, _ => 0.0);
            var rankedSlots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var rankedWsModels = new Dictionary<string, HashSet<string>>(
                StringComparer.OrdinalIgnoreCase);
            var rankedExpected = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var totalExpected = 0.0;

            foreach (var component in checks.Components.Values
                         .OrderBy(item => item.WsModelPath, StringComparer.OrdinalIgnoreCase))
            {
                var lodCount = component.MaterialAssignments.Length;
                var factor = exposure.FactorByWsModel.GetValueOrDefault(component.WsModelPath);
                for (var lodIndex = 0; lodIndex < lodCount; lodIndex++)
                {
                    foreach (var path in component.MaterialAssignments[lodIndex])
                    {
                        if (!checks.Materials.TryGetValue(Normalize(path), out var material))
                            continue;

                        if (focus.Contains(material.Status))
                        {
                            slotCounts[material.Status]++;
                            wsModelsByStatus[material.Status].Add(component.WsModelPath);
                            rigidsByStatus[material.Status].Add(component.RigidPath);
                        }

                        if (material.Status == AssetCheckAlphaStatus.Opaque &&
                            material.SourceOwned)
                        {
                            rankedSlots[material.Path] =
                                rankedSlots.GetValueOrDefault(material.Path) + 1;
                            if (!rankedWsModels.TryGetValue(material.Path, out var wsModels))
                            {
                                wsModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                rankedWsModels[material.Path] = wsModels;
                            }

                            wsModels.Add(component.WsModelPath);
                        }
                    }
                }

                if (factor <= 0 || lodCount == 0)
                    continue;

                for (var index = 0; index < exposure.ScenarioLods.Length; index++)
                {
                    var lodIndex = Math.Min(exposure.ScenarioLods[index], lodCount - 1);
                    var weight = factor * exposure.Probabilities[index];
                    foreach (var path in component.MaterialAssignments[lodIndex])
                    {
                        totalExpected += weight;
                        if (!checks.Materials.TryGetValue(Normalize(path), out var material))
                            continue;

                        if (focus.Contains(material.Status))
                            expectedByStatus[material.Status] += weight;
                        if (material.Status == AssetCheckAlphaStatus.Opaque &&
                            material.SourceOwned)
                        {
                            rankedExpected[material.Path] =
                                rankedExpected.GetValueOrDefault(material.Path) + weight;
                        }
                    }
                }
            }

            checks.TotalExpectedPartInstances = totalExpected;
            foreach (var status in focus)
            {
                var summary = checks.Classes[status];
                summary.MeshPartSlots = slotCounts[status];
                summary.WsModels = wsModelsByStatus[status].Count;
                summary.Rigids = rigidsByStatus[status].Count;
                summary.ExpectedInstances = expectedByStatus[status];
                summary.Roots = checks.Roots.Count(root => root.InstanceWeightByWsModel.Keys
                    .Any(wsModel => wsModelsByStatus[status].Contains(wsModel)));
            }

            foreach (var material in checks.Materials.Values
                         .Where(item => item.Status == AssetCheckAlphaStatus.Opaque &&
                                        item.SourceOwned)
                         .OrderByDescending(item => rankedExpected.GetValueOrDefault(item.Path))
                         .ThenByDescending(item => rankedSlots.GetValueOrDefault(item.Path))
                         .ThenBy(item => item.Path, StringComparer.Ordinal)
                         .Take(AssetChecksMaterialRankingLimit))
            {
                checks.TopOpaqueMaterials.Add(new AssetCheckRankedMaterial(
                    material.Path,
                    material.AlphaTexturePath,
                    material.MinAlpha,
                    rankedSlots.GetValueOrDefault(material.Path),
                    rankedWsModels.TryGetValue(material.Path, out var set) ? set.Count : 0,
                    rankedExpected.GetValueOrDefault(material.Path)));
            }

            var affectedRigids = Enumerable.Range(0, AssetChecksMeasureCount)
                .Select(_ => new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                .ToArray();
            var affectedSourceRigids = Enumerable.Range(0, AssetChecksMeasureCount)
                .Select(_ => new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                .ToArray();
            foreach (var unit in checks.UnitsByWsModel.Values
                         .SelectMany(units => units)
                         .OfType<AssetCheckUnit>()
                         .Where(unit => unit.Evaluated)
                         .Distinct())
            {
                for (var measure = 0; measure < AssetChecksMeasureCount; measure++)
                {
                    if (unit.Removable[measure] <= 0)
                        continue;

                    checks.PartsRemovable[measure] += unit.Removable[measure];
                    checks.UnitsAffected[measure]++;
                    affectedRigids[measure].Add(unit.RigidPath);
                    if (unit.SourceOwnedRigid)
                        affectedSourceRigids[measure].Add(unit.RigidPath);
                }
            }

            for (var measure = 0; measure < AssetChecksMeasureCount; measure++)
            {
                checks.RigidsAffected[measure] = affectedRigids[measure].Count;
                checks.SourceRigidsAffected[measure] = affectedSourceRigids[measure].Count;
            }

            foreach (var (wsModelPath, units) in checks.UnitsByWsModel)
            {
                var factor = exposure.FactorByWsModel.GetValueOrDefault(wsModelPath);
                if (factor <= 0 || units.Length == 0)
                    continue;

                for (var index = 0; index < exposure.ScenarioLods.Length; index++)
                {
                    var unit = units[Math.Min(exposure.ScenarioLods[index], units.Length - 1)];
                    if (unit == null || !unit.Evaluated)
                        continue;

                    for (var measure = 0; measure < AssetChecksMeasureCount; measure++)
                    {
                        checks.ExpectedDrawsSaved[measure] +=
                            factor * exposure.Probabilities[index] * unit.Removable[measure];
                    }
                }
            }
        }

        // ---- Report -------------------------------------------------------------

        private static string FormatAssetCheckPath(string path)
            => FormatLodAuditPath(path.Replace('\\', '/'));

        private static string FormatAssetCheckBytes(long bytes)
            => bytes >= 1024L * 1024
                ? $"{bytes / (1024.0 * 1024):N1} MiB"
                : $"{bytes:N0} B";

        private static string DescribeAssetCheckStatus(AssetCheckAlphaStatus status)
            => status switch
            {
                AssetCheckAlphaStatus.Opaque => "OPAQUE (min alpha >= 250)",
                AssetCheckAlphaStatus.MostlyOpaque => "MOSTLY OPAQUE (< 0.1 % of texels below 128)",
                AssetCheckAlphaStatus.UsesAlpha => "USES ALPHA",
                AssetCheckAlphaStatus.Unreadable => "UNREADABLE",
                AssetCheckAlphaStatus.NoAlphaTexture => "NO ALPHA SOURCE TEXTURE (structural)",
                AssetCheckAlphaStatus.BudgetExhausted => "NOT ASSESSED (time budget)",
                AssetCheckAlphaStatus.NotAssessed => "NOT ASSESSED (other alpha shader/source)",
                _ => status.ToString(),
            };

        private static void AppendAssetChecksSection(StringBuilder sb, BatchState state)
        {
            sb.AppendLine("Asset checks");
            sb.AppendLine("------------");
            var checks = state.AssetChecks;
            if (checks == null || !checks.Ran)
            {
                sb.AppendLine("Asset checks did not run.");
                sb.AppendLine();
                return;
            }

            sb.AppendLine(
                "Report only: source-state materials and textures of the rigids the LOD audit " +
                "reached, read before merging and optimization. The output pack is not changed.");
            var vanillaRigids = checks.RigidCount - checks.SourceRigidCount;
            var sourceMaterials = checks.Materials.Values.Count(material => material.SourceOwned);
            var unreadableMaterials =
                checks.Materials.Values.Count(material => material.Document == null);
            sb.AppendLine(
                $"Scope: {checks.Components.Count:N0} WSModel(s) on {checks.RigidCount:N0} rigid(s) " +
                $"(source-owned {checks.SourceRigidCount:N0}, vanilla {vanillaRigids:N0}) from " +
                $"{checks.Roots.Count:N0} audited root(s); {checks.Materials.Count:N0} distinct " +
                $"material(s) (source-owned {sourceMaterials:N0}, vanilla " +
                $"{checks.Materials.Count - sourceMaterials:N0}, unreadable " +
                $"{unreadableMaterials:N0}); " +
                $"{checks.DistinctTextureCount:N0} distinct texture(s) referenced");
            sb.AppendLine(
                $"Army weight: {checks.WeightedRootCount:N0} / {checks.Roots.Count:N0} audited " +
                "root(s) have one; expected figures below cover only those");
            if (state.LodAudit is { RootsSkippedForTime: > 0 } audit)
            {
                sb.AppendLine(
                    $"The LOD audit skipped {audit.RootsSkippedForTime:N0} root(s) after its time " +
                    "budget; the scope above is incomplete.");
            }

            sb.AppendLine(
                $"Asset checks time: {checks.Elapsed.TotalMilliseconds:N0} ms of a " +
                $"{checks.TimeBudget.TotalSeconds:N0} s budget (materials " +
                $"{checks.MaterialElapsed.TotalMilliseconds:N0} ms, texture comparison " +
                $"{checks.HashElapsed.TotalMilliseconds:N0} ms, alpha decoding " +
                $"{checks.DecodeElapsed.TotalMilliseconds:N0} ms, merge unlock " +
                $"{checks.UnlockElapsed.TotalMilliseconds:N0} ms); gathered during the LOD audit " +
                $"pass: {checks.CollectElapsed.TotalMilliseconds:N0} ms");
            if (checks.CollectionFailure != null)
                sb.AppendLine($"Asset check collection failed: {checks.CollectionFailure}");
            if (checks.Failure != null)
                sb.AppendLine($"Asset checks stopped early: {checks.Failure}");
            sb.AppendLine();

            var baselineDraws = state.CrossRigidBaselineExpectedDrawsByVmd.Values.Sum();
            AppendAssetChecksAlphaSection(sb, checks, baselineDraws);
            AppendAssetChecksDuplicateSection(sb, checks, baselineDraws);
            AppendAssetChecksCombinedSection(sb, checks, baselineDraws);
        }

        private static void AppendAssetChecksAlphaSection(
            StringBuilder sb,
            AssetChecksResult checks,
            double baselineDraws)
        {
            sb.AppendLine("Check 1: alpha materials whose alpha source is opaque");
            sb.AppendLine(
                "  Alpha source: alpha test clips the alpha of the base colour texture " +
                "(t_xml_base_colour, legacy s_base_colour / s_diffuse); the _alpha shader is " +
                "the only difference between an alpha_on and an alpha_off material. Only the " +
                "plain _alpha variants of the character families are assessed.");
            var readable = checks.Materials.Values.Where(material => material.Document != null);
            var variants = readable
                .Where(material => material.Status != AssetCheckAlphaStatus.NotAlphaShader)
                .GroupBy(material => material.Shader, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .ToList();
            sb.AppendLine(
                "  Alpha shader variants in scope (materials: total (source-owned / vanilla))");
            if (variants.Count == 0)
                sb.AppendLine("    (none)");
            foreach (var group in variants.Take(AssetChecksVariantListLimit))
            {
                var first = group.First();
                var source = group.Count(material => material.SourceOwned);
                var verdict = first.Status == AssetCheckAlphaStatus.NotAssessed &&
                              first.NotAssessedReason.Length != 0 &&
                              group.All(material =>
                                  material.NotAssessedReason == first.NotAssessedReason)
                    ? $"not assessed: {first.NotAssessedReason}"
                    : group.Any(material => material.Status == AssetCheckAlphaStatus.NotAssessed)
                        ? "assessed unless noted below"
                        : "assessed";
                sb.AppendLine(
                    $"    {group.Key}: {group.Count():N0} " +
                    $"({source:N0} / {group.Count() - source:N0}) - {verdict}");
            }

            if (variants.Count > AssetChecksVariantListLimit)
            {
                sb.AppendLine(
                    $"    ... and {variants.Count - AssetChecksVariantListLimit:N0} more variant(s)");
            }

            var notAssessed = readable
                .Where(material => material.Status == AssetCheckAlphaStatus.NotAssessed)
                .GroupBy(material => material.NotAssessedReason, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .ToList();
            foreach (var group in notAssessed.Take(AssetChecksReasonLimit))
                sb.AppendLine($"  Not assessed: {group.Count():N0} material(s) - {group.Key}");

            var candidates = readable
                .Where(material => material.AlphaTexturePath.Length != 0 ||
                                   material.Status == AssetCheckAlphaStatus.NoAlphaTexture)
                .ToList();
            var candidateSource = candidates.Count(material => material.SourceOwned);
            sb.AppendLine(
                $"  Assessed materials: {candidates.Count:N0} (source-owned {candidateSource:N0}, " +
                $"vanilla {candidates.Count - candidateSource:N0}) on " +
                $"{checks.AlphaTexturesToAssess:N0} distinct alpha texture(s)");
            sb.AppendLine(
                $"  Alpha textures decoded at full resolution: {checks.AlphaTexturesDecoded:N0} / " +
                $"{checks.AlphaTexturesToAssess:N0} ({checks.AlphaTexelsDecoded:N0} texels) in " +
                $"{checks.DecodeElapsed.TotalMilliseconds:N0} ms; not assessed after the budget: " +
                $"{checks.AlphaTexturesNotAssessed:N0}");
            sb.AppendLine("  Materials by class (source-owned / vanilla)");
            foreach (var status in new[]
                     {
                         AssetCheckAlphaStatus.Opaque,
                         AssetCheckAlphaStatus.MostlyOpaque,
                         AssetCheckAlphaStatus.UsesAlpha,
                         AssetCheckAlphaStatus.Unreadable,
                         AssetCheckAlphaStatus.NoAlphaTexture,
                         AssetCheckAlphaStatus.BudgetExhausted,
                     })
            {
                var counts = checks.Classes.GetValueOrDefault(status) ?? new AssetCheckClassSummary();
                sb.AppendLine(
                    $"    {DescribeAssetCheckStatus(status)}: " +
                    $"{counts.SourceMaterials + counts.VanillaMaterials:N0} " +
                    $"({counts.SourceMaterials:N0} / {counts.VanillaMaterials:N0})");
            }

            var unreadable = checks.TextureAlpha.Values
                .Where(texture => texture.Status == AssetCheckAlphaStatus.Unreadable)
                .GroupBy(texture => texture.Reason, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .ToList();
            foreach (var group in unreadable.Take(AssetChecksReasonLimit))
                sb.AppendLine($"    unreadable texture(s): {group.Count():N0} - {group.Key}");

            AppendAssetChecksClassDetail(
                sb,
                checks,
                AssetCheckAlphaStatus.Opaque,
                baselineDraws);
            AppendAssetChecksClassDetail(
                sb,
                checks,
                AssetCheckAlphaStatus.MostlyOpaque,
                baselineDraws);
            AppendAssetChecksTopMaterials(sb, checks);

            sb.AppendLine(
                "  Merges this would unlock (OPAQUE materials switched to the non-alpha shader)");
            AppendAssetChecksUnlockHeader(sb, checks);
            AppendAssetChecksUnlockLine(
                sb,
                checks,
                "RMV identity as authored",
                AssetChecksMeasureCheck1,
                baselineDraws);
            AppendAssetChecksUnlockLine(
                sb,
                checks,
                "RMV render flag ignored (tier T4 treatment)",
                AssetChecksMeasureCheck1FlagIgnored,
                baselineDraws);
            sb.AppendLine();
        }

        private static void AppendAssetChecksClassDetail(
            StringBuilder sb,
            AssetChecksResult checks,
            AssetCheckAlphaStatus status,
            double baselineDraws)
        {
            var summary = checks.Classes.GetValueOrDefault(status);
            sb.AppendLine($"  {DescribeAssetCheckStatus(status)} detail");
            if (summary == null || summary.SourceMaterials + summary.VanillaMaterials == 0)
            {
                sb.AppendLine("    (none)");
                return;
            }

            sb.AppendLine(
                $"    materials {summary.SourceMaterials + summary.VanillaMaterials:N0} " +
                $"(source-owned {summary.SourceMaterials:N0}, vanilla {summary.VanillaMaterials:N0}); " +
                $"distinct alpha textures {summary.DistinctTextures:N0} " +
                $"(known constant textures {summary.KnownConstantTextures:N0})");
            sb.AppendLine(
                $"    mesh-part slots (WSModel x LOD x part) {summary.MeshPartSlots:N0} in " +
                $"{summary.WsModels:N0} WSModel(s) on {summary.Rigids:N0} rigid(s); audited roots " +
                $"reaching them {summary.Roots:N0} / {checks.Roots.Count:N0}");
            sb.AppendLine(
                $"    expected battle part instances drawn with an alpha shader needlessly: " +
                $"{summary.ExpectedInstances:N0} = " +
                $"{FormatLodAuditShare(summary.ExpectedInstances, checks.TotalExpectedPartInstances)} " +
                $"of all expected battle part instances ({checks.TotalExpectedPartInstances:N0})");
            sb.AppendLine(
                "    as a share of the baseline expected battle draws (draws are counted after " +
                "same-rigid merging, instances before it): " +
                DescribeAssetCheckBaselineShare(summary.ExpectedInstances, baselineDraws));
        }

        private static void AppendAssetChecksTopMaterials(
            StringBuilder sb,
            AssetChecksResult checks)
        {
            sb.AppendLine(
                $"  Top {AssetChecksMaterialRankingLimit} OPAQUE source-owned materials by expected " +
                "battle part instances");
            if (checks.TopOpaqueMaterials.Count == 0)
            {
                sb.AppendLine("    (none)");
                return;
            }

            var rank = 0;
            foreach (var material in checks.TopOpaqueMaterials)
            {
                rank++;
                sb.AppendLine($"    {rank,2}. {FormatAssetCheckPath(material.Path)}");
                sb.AppendLine(
                    $"        texture {FormatAssetCheckPath(material.TexturePath)}; min alpha " +
                    $"{material.MinAlpha}; parts {material.MeshPartSlots:N0} in " +
                    $"{material.WsModels:N0} WSModel(s); expected battle part instances " +
                    $"{material.ExpectedInstances:N0}");
            }
        }

        private static void AppendAssetChecksUnlockHeader(
            StringBuilder sb,
            AssetChecksResult checks)
        {
            sb.AppendLine(
                "    Unit = distinct (rigid, LOD, material assignment). Parts removable = draws " +
                "under the as-is identity minus draws under the changed one, both with the " +
                "baseline's 16-bit vertex-limited bins and the same RMV treatment.");
            sb.AppendLine(
                $"    Units {checks.UnitCount:N0}; units where a material change could merge parts " +
                $"{checks.UnitsNeedingRmv:N0}; rigids re-read {checks.RigidsLoaded:N0} " +
                $"(failed {checks.RigidLoadFailures:N0}); units not evaluated (time budget or " +
                $"unreadable rigid) {checks.UnitsNotEvaluated:N0}");
        }

        private static void AppendAssetChecksUnlockLine(
            StringBuilder sb,
            AssetChecksResult checks,
            string label,
            int measure,
            double baselineDraws)
        {
            sb.AppendLine(
                $"    {label}: {checks.PartsRemovable[measure]:N0} part(s) removable in " +
                $"{checks.UnitsAffected[measure]:N0} unit(s) on {checks.RigidsAffected[measure]:N0} " +
                $"rigid(s) (source-owned {checks.SourceRigidsAffected[measure]:N0}); expected " +
                $"battle draws saved {checks.ExpectedDrawsSaved[measure]:N1} (" +
                DescribeAssetCheckBaselineShare(checks.ExpectedDrawsSaved[measure], baselineDraws) +
                ")");
        }

        private static string DescribeAssetCheckBaselineShare(double value, double baselineDraws)
            => baselineDraws > 0
                ? $"{FormatLodAuditShare(value, baselineDraws)} of the baseline's " +
                  $"{baselineDraws:N0} expected battle draws"
                : "baseline expected battle draws not computed in this run";

        private static void AppendAssetChecksDuplicateSection(
            StringBuilder sb,
            AssetChecksResult checks,
            double baselineDraws)
        {
            sb.AppendLine("Check 2: duplicate textures and materials");
            sb.AppendLine(
                $"  Textures referenced by the scoped materials: {checks.DistinctTextureCount:N0}; " +
                $"in the source pack {checks.SourceTextureCount:N0}; vanilla textures referenced " +
                $"{checks.VanillaReferencedTextureCount:N0}; unresolved " +
                $"{checks.UnresolvedTextureCount:N0}");
            sb.AppendLine(
                $"  Content comparison (length first, then SHA-256): {checks.HashedFileCount:N0} " +
                $"file(s) read ({FormatAssetCheckBytes(checks.HashedByteCount)}) in " +
                $"{checks.HashElapsed.TotalMilliseconds:N0} ms; not read after the budget " +
                $"{checks.HashNotAssessedCount:N0}; read failures {checks.HashFailureCount:N0}");
            sb.AppendLine(
                $"  Duplicate texture groups in the source pack: {checks.DuplicateGroupCount:N0} " +
                $"group(s), {checks.DuplicateFileCount:N0} file(s), {checks.RedundantFileCount:N0} " +
                $"redundant file(s), {FormatAssetCheckBytes(checks.RedundantBytes)} redundant " +
                "(uncompressed bytes)");
            sb.AppendLine(
                $"  BCn residency estimate of the redundant copies: " +
                $"{FormatAssetCheckBytes(checks.RedundantBcnBytes)} (sized groups " +
                $"{checks.GroupsWithBcnEstimate:N0} / {checks.DuplicateGroupCount:N0}; the estimate " +
                "covers the whole mip chain)");
            sb.AppendLine(
                $"  Largest groups (top {AssetChecksTextureGroupLimit} by redundant bytes)");
            if (checks.TopTextureGroups.Count == 0)
                sb.AppendLine("    (none)");
            var groupRank = 0;
            foreach (var group in checks.TopTextureGroups)
            {
                groupRank++;
                sb.AppendLine(
                    $"    {groupRank,2}. {group.Paths.Count:N0} files x " +
                    FormatAssetCheckBytes(group.Length) +
                    (group.BcnBytes > 0
                        ? $" (BCn estimate {FormatAssetCheckBytes(group.BcnBytes)} each)"
                        : string.Empty));
                foreach (var path in group.Paths.Take(3))
                    sb.AppendLine($"        {FormatAssetCheckPath(path)}");
                if (group.Paths.Count > 3)
                    sb.AppendLine($"        ... and {group.Paths.Count - 3:N0} more");
            }

            sb.AppendLine(
                $"  Source textures byte-identical to a vanilla texture already referenced by the " +
                $"scoped materials (vanilla packs are not scanned): " +
                $"{checks.SourceTexturesMatchingVanilla:N0}");

            sb.AppendLine("  Duplicate materials (same rendering identity, material name ignored)");
            AppendAssetChecksMaterialCounts(
                sb,
                "identical as-is",
                checks.AsIsAll,
                checks.AsIsSource);
            AppendAssetChecksMaterialCounts(
                sb,
                "identical after sharing duplicate textures",
                checks.CanonicalAll,
                checks.CanonicalSource);
            sb.AppendLine(
                $"  Groups identical only after sharing duplicate textures: " +
                $"{checks.NewCanonicalGroupCount:N0} (showing up to {AssetChecksMaterialGroupLimit})");
            if (checks.NewCanonicalGroups.Count == 0)
                sb.AppendLine("    (none)");
            var materialRank = 0;
            foreach (var group in checks.NewCanonicalGroups)
            {
                materialRank++;
                sb.AppendLine(
                    $"    {materialRank,2}. {group.Size:N0} materials, {group.AsIsVariants:N0} " +
                    "distinct as-is identities");
                foreach (var path in group.Paths)
                    sb.AppendLine($"        {FormatAssetCheckPath(path)}");
                foreach (var difference in group.Differences.Take(3))
                    sb.AppendLine($"        differs: {FormatAssetCheckPath(difference)}");
            }

            AppendAssetChecksUnlockLine(
                sb,
                checks,
                "Merges this would unlock (duplicate textures shared), RMV identity as authored",
                AssetChecksMeasureCheck2,
                baselineDraws);
            sb.AppendLine();
        }

        private static void AppendAssetChecksMaterialCounts(
            StringBuilder sb,
            string label,
            AssetCheckGroupCounts all,
            AssetCheckGroupCounts source)
        {
            sb.AppendLine(
                $"    {label}: {all.Groups:N0} group(s), {all.Files:N0} file(s), " +
                $"{all.Redundant:N0} redundant; source-owned materials only: " +
                $"{source.Groups:N0} group(s), {source.Files:N0} file(s), " +
                $"{source.Redundant:N0} redundant");
        }

        private static void AppendAssetChecksCombinedSection(
            StringBuilder sb,
            AssetChecksResult checks,
            double baselineDraws)
        {
            sb.AppendLine("Checks 1 and 2 together");
            AppendAssetChecksUnlockLine(
                sb,
                checks,
                "Merges this would unlock, RMV identity as authored",
                AssetChecksMeasureCombined,
                baselineDraws);
            AppendAssetChecksUnlockLine(
                sb,
                checks,
                "Merges this would unlock, RMV render flag ignored",
                AssetChecksMeasureCombinedFlagIgnored,
                baselineDraws);
            sb.AppendLine();
        }
    }
}
