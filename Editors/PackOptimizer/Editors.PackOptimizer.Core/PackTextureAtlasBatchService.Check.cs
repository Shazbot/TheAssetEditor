using System.Diagnostics;
using System.Globalization;
using System.IO;
using Shared.Core.PackFiles.Models;

namespace Editors.PackOptimizer.Core
{
    public sealed partial class PackTextureAtlasBatchService
    {
        // The visual-asset check: the detection stages of the optimizer run on their own, without
        // any optimization and without an output pack, and what they recorded in the batch state is
        // turned into structured findings. The optimizer's own report reads the same state fields, so
        // both are views of one detection. Stages used, in order: VMD validation (malformed and
        // repaired XML), WSModel indexing (malformed WSModels), the gameplay mesh dependency index
        // (missing / invalid / unparsable references; scoped to the pack's own VMDs), the LOD audit
        // (component resolution, LOD chains), the asset checks (materials, textures, alpha, duplicates;
        // without the merge-unlock evaluation, which measures an optimization) and the bone weight audit
        // (unreadable rigids and vertex data).
        public PackOptimizerCheckResult Check(
            PackOptimizerCheckRequest request,
            IProgress<TextureAtlasPackProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var bulkReadScopes = (_packFileService.GetAllPackfileContainers() ?? [])
                .OfType<IBulkReadPackFileContainer>()
                .Select(container => container.BeginBulkRead())
                .ToList();
            try
            {
                return CheckCore(request, progress, cancellationToken);
            }
            finally
            {
                foreach (var scope in bulkReadScopes)
                    scope.Dispose();
            }
        }

        private PackOptimizerCheckResult CheckCore(
            PackOptimizerCheckRequest request,
            IProgress<TextureAtlasPackProgress>? progress,
            CancellationToken cancellationToken)
        {
            var total = Stopwatch.StartNew();
            var sourcePath = request.SourcePackPath;
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, "Loading source pack", item: Path.GetFileName(sourcePath));

            var source = request.SourceContainer;
            // The state needs an output container; the check never fills or saves it.
            var output = _packFileService.CreateOutputContainer(
                Path.GetFileNameWithoutExtension(sourcePath) + "_check");
            BatchState? state = null;
            try
            {
                state = new BatchState(
                    source,
                    output,
                    _packFileService,
                    sourcePath,
                    string.Empty,
                    string.Empty,
                    mergeCompatibleMeshesEnabled: false,
                    shareAtlasesAcrossVmdsEnabled: true,
                    optimizeGeometryEnabled: false)
                {
                    CheckMode = true,
                    CancellationToken = cancellationToken,
                    Progress = progress,
                };
                var containerSnapshot = _packFileService.GetAllPackfileContainers().ToList();
                state.GameplayTraversalContainers = containerSnapshot;
                state.VanillaAtlasConsumerCache = Wh3VanillaAtlasConsumerCache.Load(
                    _cacheDirectory,
                    request.Game,
                    containerSnapshot);

                var allVmdPaths = source.GetAllFiles().Keys
                    .Where(x => Path.GetExtension(x).Equals(".variantmeshdefinition", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                ReportProgress(progress, "Validating VMD roots", item: $"{allVmdPaths.Count} VMD file(s)");
                var (vmdRoots, malformedVmdRoots) = ValidateVmdRoots(
                    state, source, allVmdPaths, cancellationToken, progress);
                state.MalformedVmdRoots.AddRange(malformedVmdRoots);
                state.SourceVmdRootCount = vmdRoots.Count;

                CheckUsage? usage = null;
                string? usageFailure = null;
                if (vmdRoots.Count != 0)
                {
                    BuildWsUsageIndex(state, cancellationToken, progress);
                    if (request.DbSchema != null)
                    {
                        usage = ResolveCheckUsage(
                            state, vmdRoots, containerSnapshot, request.DbSchema,
                            cancellationToken, progress, out usageFailure);
                    }

                    ReportProgress(progress, "Indexing mesh dependencies", item: $"{vmdRoots.Count} VMD root(s)");
                    IndexImmutableMeshMergeConsumers(state, vmdRoots, cancellationToken);

                    // Pack-wide: every VMD of the pack is audited, used by the game's data or not.
                    AuditSourceLods(state, vmdRoots, cancellationToken, progress);
                    RunAssetChecks(state, cancellationToken, progress);
                    RunBoneWeightAudit(state, cancellationToken, progress, null, null);
                }

                ReportProgress(progress, "Collecting findings");
                var findings = CollectCheckFindings(
                    state, allVmdPaths.Count, usage, usageFailure, request.DependencyContainers ?? []);
                var summary = PackOptimizerCheck.Summarize(
                    findings,
                    usage != null,
                    usage == null ? 0 : vmdRoots.Count(usage.IsUsedRoot),
                    usage == null ? 0 : vmdRoots.Count(root => !usage.IsUsedRoot(root)));
                var checks = state.AssetChecks;
                var counts = (
                    WsModels: checks?.Components.Count ?? 0,
                    Rigids: checks?.Rigids.Count ?? 0,
                    Materials: checks?.Materials.Count ?? 0);
                var elapsed = total.Elapsed;
                return new PackOptimizerCheckResult(
                    PackOptimizerStatus.Succeeded,
                    findings,
                    summary,
                    PackOptimizerCheck.RenderReport(
                        sourcePath, findings, summary, vmdRoots.Count,
                        counts.WsModels, counts.Rigids, counts.Materials, elapsed),
                    vmdRoots.Count,
                    counts.WsModels,
                    counts.Rigids,
                    counts.Materials,
                    elapsed);
            }
            finally
            {
                state?.VanillaAtlasConsumerCache?.Save();
                _packFileService.ReleaseOutputContainer(output);
            }
        }

        // Which variant mesh definitions of the pack the game data reaches (the pack's own DB plus the game's),
        // from the unit-category resolver. A root the resolver could not fully resolve counts as used.
        private sealed class CheckUsage(Wh3UnitCategoryResolution resolution, IEnumerable<string> vmdRoots)
        {
            public Wh3UnitCategoryResolution Resolution { get; } = resolution;
            public HashSet<string> VmdRoots { get; } = new(vmdRoots.Select(Normalize), StringComparer.OrdinalIgnoreCase);

            public bool IsUsedRoot(string root)
            {
                var path = Normalize(root);
                return Resolution.UsagesByVmd.ContainsKey(path) ||
                       Resolution.UnresolvedConsumersByVmd.ContainsKey(path) ||
                       Resolution.DirectAssetUsagesByPath.ContainsKey(path);
            }
        }

        private static CheckUsage? ResolveCheckUsage(
            BatchState state,
            IReadOnlyList<string> vmdRoots,
            IReadOnlyList<IPackFileContainer> containers,
            PackOptimizerDbSchema schema,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress,
            out string? failure)
        {
            failure = null;
            ReportProgress(progress, "Resolving unit categories", item: $"{vmdRoots.Count} VMD root(s)");
            try
            {
                var children = BuildChildVmdDependencyMap(state, state.Source, vmdRoots, cancellationToken);
                var resolution = Wh3UnitCategoryResolver.ResolveAgainstContainers(
                    state.Source, vmdRoots, children, cancellationToken, Wh3ArmyVisualScenario.Default,
                    containers, null, schema);
                if (!resolution.IsGameplayResolutionHealthy)
                {
                    failure = resolution.GameplayResolutionHealthMessage;
                    return null;
                }

                return new CheckUsage(resolution, vmdRoots);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = $"{ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }

        private static readonly string[] CheckCoveredComponentReasonPrefixes =
        [
            // The gameplay mesh dependency index reports these as missing / invalid / unparsable
            // references; the unreadable rigids come from the bone weight audit.
            "WSModel could not be resolved",
            "WSModel could not be parsed",
            "WSModel has no supported rigid geometry path",
            "Rigid geometry could not be resolved",
            "Rigid geometry could not be parsed",
        ];

        // Turns what the detection stages recorded in the state into findings. Only the pack's own
        // files are reported (a vanilla asset with a problem is not the pack's problem).
        private static List<PackCheckFinding> CollectCheckFindings(
            BatchState state,
            int vmdFileCount,
            CheckUsage? usage,
            string? usageFailure,
            IReadOnlyList<IPackFileContainer> dependencies)
        {
            var findings = new List<PackCheckFinding>();

            // Parent edges of the asset graph that the dependency index does not hold: material -> WSModels,
            // texture -> materials. Used to find the variant meshes that reach a material or texture.
            var parents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            void AddParent(string child, string parent)
            {
                if (child.Length == 0)
                    return;
                if (!parents.TryGetValue(child, out var list))
                    parents[child] = list = [];
                list.Add(parent);
            }

            if (state.AssetChecks is { } assetChecks)
            {
                foreach (var component in assetChecks.Components.Values)
                {
                    foreach (var material in component.MaterialAssignments.SelectMany(lod => lod).Select(Normalize))
                        AddParent(material, component.WsModelPath);
                }

                foreach (var material in assetChecks.Materials.Values)
                {
                    foreach (var texture in material.Textures.Values)
                        AddParent(Normalize(texture), material.Path);
                }
            }

            bool? UsedByRoots(IEnumerable<string> roots)
                => usage == null ? null : roots.Any(usage.IsUsedRoot);

            bool? UsedForPath(string path)
            {
                if (usage == null || path.Length == 0)
                    return null;
                var start = Normalize(path);
                if (usage.Resolution.DirectAssetUsagesByPath.ContainsKey(start))
                    return true;
                if (Path.GetExtension(start).Equals(".variantmeshdefinition", StringComparison.OrdinalIgnoreCase))
                    return usage.VmdRoots.Contains(start) ? usage.IsUsedRoot(start) : null;

                var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
                var queue = new Queue<string>([start]);
                while (queue.Count != 0)
                {
                    var node = queue.Dequeue();
                    if (state.GameplayMeshDependencyIndex != null)
                        roots.UnionWith(state.GameplayMeshDependencyIndex.GetRootsForAsset(node));
                    foreach (var parent in parents.GetValueOrDefault(node) ?? [])
                    {
                        if (seen.Add(parent))
                            queue.Enqueue(parent);
                    }
                }

                return roots.Any(usage.IsUsedRoot);
            }

            // Dependency packs: a reference one of them provides is not missing.
            var providedBy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string? Provider(string path)
            {
                var normalized = Normalize(path);
                for (var index = dependencies.Count - 1; index >= 0; index--)
                {
                    if (normalized.Length != 0 && dependencies[index].ContainsFile(normalized))
                        return dependencies[index].Name;
                }

                return null;
            }

            void NoteProvided(string pack, string path)
            {
                if (!providedBy.TryGetValue(pack, out var list))
                    providedBy[pack] = list = [];
                list.Add(path);
            }

            void Add(
                PackCheckSeverity severity,
                string code,
                string category,
                string message,
                string path,
                IEnumerable<string>? related = null,
                string? details = null,
                IEnumerable<string>? roots = null)
            {
                var rootList = roots?.Where(root => root.Length != 0).ToList();
                var used = rootList is { Count: > 0 } ? UsedByRoots(rootList) : UsedForPath(path);
                if (used == false)
                {
                    // Only variant meshes no unit uses reach this: one level less serious.
                    severity = severity == PackCheckSeverity.Error
                        ? PackCheckSeverity.Warning
                        : PackCheckSeverity.Info;
                    details = string.IsNullOrEmpty(details)
                        ? "not used by any unit in game data"
                        : details + "; not used by any unit in game data";
                }

                var relatedPaths = (related ?? [])
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToList();
                findings.Add(new PackCheckFinding(
                    severity,
                    code,
                    category,
                    message,
                    path,
                    relatedPaths.Take(PackOptimizerCheck.RelatedPathLimit).ToList(),
                    relatedPaths.Count,
                    details,
                    used));
            }

            bool InPack(string path) => path.Length != 0 && state.Source.ContainsFile(Normalize(path));

            if (vmdFileCount == 0)
            {
                Add(
                    PackCheckSeverity.Info,
                    PackCheckCodes.NoVisualAssets,
                    PackCheckCategories.Audit,
                    "The pack contains no .variantmeshdefinition files; nothing was checked.",
                    string.Empty);
            }

            // ---- XML that is malformed or needed repairs ---------------------------------
            foreach (var entry in state.MalformedVmdRoots.Concat(state.MalformedReferencedVmds)
                         .Where(entry => InPack(entry.Path))
                         .DistinctBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase))
            {
                Add(
                    PackCheckSeverity.Error,
                    PackCheckCodes.MalformedVariantMesh,
                    PackCheckCategories.Xml,
                    "Variant mesh definition cannot be parsed.",
                    entry.Path,
                    details: entry.Reason);
            }

            foreach (var entry in state.MalformedWsModelsIgnored.Where(entry => InPack(entry.Path)))
            {
                Add(
                    PackCheckSeverity.Error,
                    PackCheckCodes.MalformedWsModel,
                    PackCheckCategories.Xml,
                    "WSModel cannot be parsed.",
                    entry.Path,
                    details: entry.Reason);
            }

            var reportedMalformedMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in state.MalformedMaterialsIgnored.Where(entry => InPack(entry.Path)))
            {
                reportedMalformedMaterials.Add(Normalize(entry.Path));
                Add(
                    PackCheckSeverity.Error,
                    PackCheckCodes.MalformedMaterial,
                    PackCheckCategories.Materials,
                    "Material XML cannot be parsed.",
                    entry.Path,
                    details: entry.Reason);
            }

            foreach (var (path, repairs) in state.XmlCompatibilityRepairs.Where(entry => InPack(entry.Key)))
            {
                Add(
                    PackCheckSeverity.Warning,
                    PackCheckCodes.XmlRepaired,
                    PackCheckCategories.Xml,
                    "XML is not well-formed as authored and had to be repaired to be read.",
                    path,
                    details: string.Join("; ", repairs.OrderBy(value => value, StringComparer.Ordinal)));
            }

            // ---- References (gameplay mesh dependency index) -----------------------------
            if (state.GameplayMeshDependencyIndex is { } index)
            {
                var relevantFailures = index.FailureDetails.Where(failure =>
                    failure.AssetScope == GameplayMeshDependencyAssetScope.SourcePack ||
                    InPack(failure.AssetPath) ||
                    InPack(failure.RootPath));
                foreach (var group in relevantFailures
                             .GroupBy(failure => (failure.FailureKind, failure.AssetPath, failure.Reason))
                             .OrderBy(group => group.Key.AssetPath, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(group => group.Key.Reason, StringComparer.Ordinal))
                {
                    var (kind, assetPath, reason) = group.Key;
                    if (kind == GameplayMeshDependencyFailureKind.MissingReference &&
                        Provider(assetPath) is { } providingPack)
                    {
                        NoteProvided(providingPack, assetPath);
                        continue;
                    }

                    var (severity, code) = kind switch
                    {
                        GameplayMeshDependencyFailureKind.MissingReference =>
                            (PackCheckSeverity.Error, PackCheckCodes.MissingReference),
                        GameplayMeshDependencyFailureKind.InvalidReference =>
                            (PackCheckSeverity.Error, PackCheckCodes.InvalidReference),
                        GameplayMeshDependencyFailureKind.ParseFailure =>
                            (PackCheckSeverity.Error, PackCheckCodes.UnparsableAsset),
                        _ => (PackCheckSeverity.Warning, PackCheckCodes.UnknownReferenceType),
                    };
                    var dispositions = group
                        .Select(failure => failure.Disposition == GameplayMeshDependencyFailureDisposition.VerifiedMissing
                            ? "verified missing from the pack and the game packs"
                            : "unverified")
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(value => value, StringComparer.Ordinal);
                    Add(
                        severity,
                        code,
                        PackCheckCategories.References,
                        $"{char.ToUpperInvariant(reason[0])}{reason[1..]}.",
                        FormatDiagnosticPath(assetPath),
                        group.Select(failure => failure.RootPath),
                        string.Join("; ", dispositions),
                        group.Select(failure => failure.RootPath));
                }
            }

            CollectAssetCheckFindings(state, Add, InPack, reportedMalformedMaterials, Provider, NoteProvided);
            CollectLodAuditFindings(state, Add);
            CollectBoneWeightFindings(state, Add);

            foreach (var (pack, paths) in providedBy.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                Add(PackCheckSeverity.Info, PackCheckCodes.ProvidedByDependency, PackCheckCategories.References,
                    $"{paths.Distinct(StringComparer.OrdinalIgnoreCase).Count():N0} referenced asset(s) are provided by " +
                    $"the dependency pack '{pack}' and are not missing.",
                    string.Empty, paths);
            }

            if (UsageNote(usage, usageFailure) is { } note)
            {
                Add(PackCheckSeverity.Info, PackCheckCodes.CheckIncomplete, PackCheckCategories.Audit, note, string.Empty);
            }

            return findings
                .OrderByDescending(finding => finding.Severity)
                .ThenBy(finding => finding.Category, StringComparer.Ordinal)
                .ThenBy(finding => finding.Code, StringComparer.Ordinal)
                .ThenBy(finding => finding.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string? UsageNote(CheckUsage? usage, string? usageFailure)
            => usage == null && usageFailure != null
                ? $"Which variant meshes the game data uses could not be resolved, so findings are not marked used or unused: {usageFailure}"
                : null;

        private delegate void AddCheckFinding(
            PackCheckSeverity severity,
            string code,
            string category,
            string message,
            string path,
            IEnumerable<string>? related = null,
            string? details = null,
            IEnumerable<string>? roots = null);

        private static void CollectAssetCheckFindings(
            BatchState state,
            AddCheckFinding add,
            Func<string, bool> inPack,
            HashSet<string> reportedMalformedMaterials,
            Func<string, string?> provider,
            Action<string, string> noteProvided)
        {
            var checks = state.AssetChecks;
            if (checks == null)
                return;

            if (checks.CollectionFailure != null)
            {
                add(PackCheckSeverity.Warning, PackCheckCodes.CheckIncomplete, PackCheckCategories.Audit,
                    "Some WSModels could not be collected for the material and texture checks.",
                    string.Empty, details: checks.CollectionFailure);
            }

            if (checks.Failure != null)
            {
                add(PackCheckSeverity.Warning, PackCheckCodes.CheckIncomplete, PackCheckCategories.Audit,
                    "The material and texture checks stopped early.", string.Empty, details: checks.Failure);
            }

            if (checks.HashNotAssessedCount > 0)
            {
                add(PackCheckSeverity.Info, PackCheckCodes.CheckIncomplete, PackCheckCategories.Audit,
                    $"{checks.HashNotAssessedCount:N0} texture(s) were not compared for duplicates (time budget).",
                    string.Empty);
            }

            if (checks.AlphaTexturesNotAssessed > 0)
            {
                add(PackCheckSeverity.Info, PackCheckCodes.CheckIncomplete, PackCheckCategories.Audit,
                    $"{checks.AlphaTexturesNotAssessed:N0} texture(s) were not decoded for the alpha check (time budget).",
                    string.Empty);
            }

            if (checks.HashFailureCount > 0)
            {
                add(PackCheckSeverity.Warning, PackCheckCodes.CheckIncomplete, PackCheckCategories.Audit,
                    $"{checks.HashFailureCount:N0} texture(s) could not be read for the duplicate check.",
                    string.Empty);
            }

            // WSModels of the pack by the material they bind.
            var wsModelsByMaterial = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var component in checks.Components.Values.Where(component => component.SourceOwnedWsModel))
            {
                foreach (var material in component.MaterialAssignments.SelectMany(lod => lod).Select(Normalize))
                {
                    if (material.Length == 0)
                        continue;
                    if (!wsModelsByMaterial.TryGetValue(material, out var users))
                    {
                        users = [];
                        wsModelsByMaterial[material] = users;
                    }

                    users.Add(component.WsModelPath);
                }
            }

            var unreadableTextures = new Dictionary<string, (string Reason, List<string> Materials)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var material in checks.Materials.Values.OrderBy(item => item.Path, StringComparer.Ordinal))
            {
                if (material.Document == null)
                {
                    if (material.UnreadableReason == null)
                        continue;
                    var users = wsModelsByMaterial.GetValueOrDefault(material.Path);
                    if (material.UnreadableReason == "material file not found")
                    {
                        if (provider(material.Path) is { } materialProvider)
                            noteProvided(materialProvider, material.Path);
                        else if (users is { Count: > 0 })
                        {
                            add(PackCheckSeverity.Error, PackCheckCodes.MissingMaterial, PackCheckCategories.Materials,
                                "Material referenced by a WSModel of the pack was not found in the pack or the game packs.",
                                material.Path, users);
                        }
                    }
                    else if (material.SourceOwned && !reportedMalformedMaterials.Contains(material.Path))
                    {
                        add(PackCheckSeverity.Error, PackCheckCodes.MalformedMaterial, PackCheckCategories.Materials,
                            "Material XML cannot be parsed.", material.Path, users, material.UnreadableReason);
                    }

                    continue;
                }

                if (!material.SourceOwned)
                    continue;

                var missing = material.Textures
                    .Where(entry => entry.Value.Length != 0 &&
                                    checks.UnresolvedTexturePaths.Contains(entry.Value) &&
                                    // The "no mask" sentinel (a mask slot -> test_mask.dds) is not a defect.
                                    !IsCheckMaskSentinel(entry.Key, entry.Value))
                    .Where(entry =>
                    {
                        if (provider(entry.Value) is not { } texturePack)
                            return true;
                        noteProvided(texturePack, entry.Value);
                        return false;
                    })
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .ToList();
                if (missing.Count != 0)
                {
                    add(PackCheckSeverity.Error, PackCheckCodes.MissingTexture, PackCheckCategories.Textures,
                        $"Material references {missing.Count:N0} texture(s) found neither in the pack nor in the game packs.",
                        material.Path,
                        missing.Select(entry => entry.Value),
                        string.Join("; ", missing.Select(entry => $"{entry.Key} -> {entry.Value}")));
                }

                switch (material.Status)
                {
                    case AssetCheckAlphaStatus.Opaque:
                    case AssetCheckAlphaStatus.MostlyOpaque:
                        var opaque = material.Status == AssetCheckAlphaStatus.Opaque;
                        // An optimization opportunity, not a defect.
                        add(PackCheckSeverity.Info,
                            opaque ? PackCheckCodes.OpaqueAlphaTested : PackCheckCodes.MostlyOpaqueAlphaTested,
                            PackCheckCategories.Materials,
                            opaque
                                ? "Alpha-tested material whose alpha texture is fully opaque; the shader without alpha test would render the same."
                                : "Alpha-tested material whose alpha texture is almost entirely opaque.",
                            material.Path,
                            [material.AlphaTexturePath],
                            $"shader {material.Shader}; alpha texture {material.AlphaTexturePath}; " +
                            $"minimum alpha {material.MinAlpha}" +
                            (material.TwinShader.Length == 0 ? string.Empty : $"; non-alpha twin {material.TwinShader}"));
                        break;
                    case AssetCheckAlphaStatus.Unreadable:
                        if (checks.TextureAlpha.TryGetValue(material.AlphaTexturePath, out var decoded) &&
                            decoded.Reason != "texture file not found" &&
                            inPack(material.AlphaTexturePath))
                        {
                            if (!unreadableTextures.TryGetValue(material.AlphaTexturePath, out var entry))
                            {
                                entry = (decoded.Reason, []);
                                unreadableTextures[material.AlphaTexturePath] = entry;
                            }

                            entry.Materials.Add(material.Path);
                        }

                        break;
                }
            }

            foreach (var (texture, (reason, materials)) in unreadableTextures
                         .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                add(PackCheckSeverity.Error, PackCheckCodes.UnreadableTexture, PackCheckCategories.Textures,
                    "Texture cannot be read or decoded.", texture, materials, reason);
            }

            foreach (var group in checks.CanonicalTextures
                         .GroupBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Count() >= 2)
                         .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                var copies = group.Select(entry => entry.Key)
                    .Where(path => !path.Equals(group.Key, StringComparison.OrdinalIgnoreCase));
                var file = state.Source.FindFile(group.Key);
                var length = file == null ? 0 : GetAssetCheckContentLength(file);
                add(PackCheckSeverity.Info, PackCheckCodes.DuplicateTexture, PackCheckCategories.Textures,
                    $"Texture has {group.Count() - 1:N0} byte-identical copy(ies) under other paths.",
                    group.Key, copies,
                    length == 0
                        ? null
                        : $"{FormatAssetCheckBytes(length)} each, {FormatAssetCheckBytes(length * (group.Count() - 1L))} redundant");
            }
        }

        private static void CollectLodAuditFindings(BatchState state, AddCheckFinding add)
        {
            var audit = state.LodAudit;
            if (audit == null)
                return;

            if (audit.Failure != null)
            {
                add(PackCheckSeverity.Warning, PackCheckCodes.CheckIncomplete, PackCheckCategories.Audit,
                    "The LOD audit stopped early.", string.Empty, details: audit.Failure);
            }

            if (audit.RootsSkippedForTime > 0)
            {
                add(PackCheckSeverity.Warning, PackCheckCodes.CheckIncomplete, PackCheckCategories.Audit,
                    $"{audit.RootsSkippedForTime:N0} of {audit.RootsRequested:N0} variant mesh definition(s) were not audited " +
                    "for LODs, models, materials and textures (time budget).",
                    string.Empty);
            }

            foreach (var group in audit.Failures.Where(failure => failure.Kind == "Expansion")
                         .GroupBy(failure => failure.RootPath, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                add(PackCheckSeverity.Warning, PackCheckCodes.UnusableVariantMesh, PackCheckCategories.Models,
                    "The variant mesh definition's configurations could not be expanded.",
                    group.Key, details: group.First().Reason);
            }

            foreach (var group in audit.Failures.Where(failure =>
                             failure.Kind == "Component" &&
                             state.Source.ContainsFile(failure.AssetPath) &&
                             !CheckCoveredComponentReasonPrefixes.Any(prefix =>
                                 failure.Reason.StartsWith(prefix, StringComparison.Ordinal)))
                         .GroupBy(failure => (failure.AssetPath, failure.Reason))
                         .OrderBy(group => group.Key.AssetPath, StringComparer.OrdinalIgnoreCase))
            {
                add(PackCheckSeverity.Warning, PackCheckCodes.UnusableWsModel, PackCheckCategories.Models,
                    "WSModel cannot be used with its rigid model.",
                    group.Key.AssetPath, group.Select(failure => failure.RootPath), group.Key.Reason);
            }

            foreach (var (reason, count) in audit.ComponentDiagnosticCounts
                         .OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                var examples = audit.ComponentDiagnosticExamples.GetValueOrDefault(reason) ?? [];
                add(PackCheckSeverity.Info, PackCheckCodes.ComponentDiagnostic, PackCheckCategories.Models,
                    $"{reason} ({count:N0} time(s)).", string.Empty, examples);
            }

            // Only a real inversion: a non-empty LOD with strictly more triangles than the one before it.
            // Empty LODs and equal counts are not findings; the audit's own flags (used by the optimizer)
            // are left alone. Few or heavy LODs are optimization statistics and are not reported.
            foreach (var rigid in audit.Rigids.Values
                         .Where(rigid => rigid.SourceOwned && rigid.Lods.Count >= 2)
                         .OrderBy(rigid => rigid.RigidPath, StringComparer.OrdinalIgnoreCase))
            {
                var inversions = new List<int>();
                for (var index = 1; index < rigid.Lods.Count; index++)
                {
                    if (rigid.Lods[index].Triangles > 0 && rigid.Lods[index].Triangles > rigid.Lods[index - 1].Triangles)
                        inversions.Add(index);
                }

                if (inversions.Count == 0)
                    continue;

                add(PackCheckSeverity.Info, PackCheckCodes.LodNotLighter, PackCheckCategories.Lods,
                    "A LOD has more triangles than the LOD before it.",
                    rigid.RigidPath,
                    details: string.Join(", ", rigid.Lods.Select((lod, lodIndex) =>
                        $"LOD {lodIndex}: {lod.Triangles:N0} triangles")));
            }
        }

        private static void CollectBoneWeightFindings(BatchState state, AddCheckFinding add)
        {
            var result = state.BoneWeightAudit;
            if (result == null)
                return;

            var checks = state.AssetChecks;
            if (result.Failure != null)
            {
                add(PackCheckSeverity.Warning, PackCheckCodes.CheckIncomplete, PackCheckCategories.Audit,
                    "The bone weight audit stopped early.", string.Empty, details: result.Failure);
            }

            if (result.RigidsNotReached > 0)
            {
                add(PackCheckSeverity.Info, PackCheckCodes.CheckIncomplete, PackCheckCategories.Audit,
                    $"{result.RigidsNotReached:N0} rigid model(s) were not read by the bone weight audit (time budget).",
                    string.Empty);
            }

            foreach (var (rigid, reason) in result.UnreadableRigids
                         .OrderBy(entry => entry.Rigid, StringComparer.OrdinalIgnoreCase))
            {
                add(PackCheckSeverity.Error, PackCheckCodes.UnreadableRigid, PackCheckCategories.Models,
                    "Rigid model cannot be read.",
                    rigid,
                    checks?.Components.Values
                        .Where(component => component.RigidPath.Equals(rigid, StringComparison.OrdinalIgnoreCase))
                        .Select(component => component.WsModelPath),
                    reason);
            }

            foreach (var group in result.Meshes.Where(mesh => mesh.Unreadable)
                         .GroupBy(mesh => mesh.Rigid, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                add(PackCheckSeverity.Warning, PackCheckCodes.InvalidBoneWeights, PackCheckCategories.Skinning,
                    "Weighted vertices without four bone indices and weights.",
                    group.Key,
                    details: string.Join(
                        ", ",
                        group.OrderBy(mesh => mesh.Lod).ThenBy(mesh => mesh.Part)
                            .Select(mesh => $"LOD {mesh.Lod} part {mesh.Part}")));
            }
        }

        // Any mask slot (t_xml_mask, legacy s_mask) pointing at test_mask.dds means "no mask", in any folder.
        private static bool IsCheckMaskSentinel(string slot, string? path)
        {
            if (!slot.EndsWith("mask", StringComparison.OrdinalIgnoreCase))
                return false;

            var normalized = Normalize(path);
            return normalized.Equals("test_mask.dds", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(@"\test_mask.dds", StringComparison.OrdinalIgnoreCase);
        }
    }
}
