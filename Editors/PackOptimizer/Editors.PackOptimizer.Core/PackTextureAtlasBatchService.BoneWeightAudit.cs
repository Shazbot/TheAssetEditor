using System.Diagnostics;
using System.Text;
using Shared.GameFormats.RigidModel;

namespace Editors.PackOptimizer.Core
{
    public sealed partial class PackTextureAtlasBatchService
    {
        // Report-only "Bone weight audit": how much 4-weight (Cinematic, weighted4_*) geometry
        // could be 2-weight (Weighted, weighted2_*) with exactly the same skinning. Nothing is
        // written. From the disassembled vs40_main of weighted2_character / weighted4_character:
        //  * weighted2 reads two bone indices and ONE weight byte b0; the second weight is
        //    1 - b0/255 and the stored second byte is ignored;
        //  * weighted4 reads four indices and three weight bytes b0..b2 (the fourth stored
        //    byte is ignored); w3 = 1 - (b0+b1+b2)/255 is applied only when b0+b1+b2 <= 254,
        //    w0 and w1 only when their byte is above 0, w2 as stored.
        // A weighted4 vertex's effective bones are {i0 if b0>0, i1 if b1>0, i2 if b2>0, i3 if
        // b0+b1+b2<=254}, two slots naming one bone counting once (their weights add). With at
        // most two the vertex is exactly 2-weight representable.
        //
        // The weights are read as bytes: CommonVertex holds byte / 255f, and rounding
        // weight * 255 gives the byte back exactly.
        private const double BoneWeightAuditBudgetSeconds = 30;
        private const int BoneWeightAuditListLimit = 20;
        private const int BoneWeightAuditNearMaximumWeight = 2;

        private enum BoneWeightClass
        {
            Exact,
            Near,
            NeedsFour,
        }

        private sealed record BoneWeightAuditMesh(
            string Rigid,
            int Lod,
            int Part,
            string Format,
            int Vertices,
            int Exact,
            int Near,
            int NeedsFour,
            bool Unreadable,
            List<string> Shaders)
        {
            public bool Convertible => !Unreadable && Near == 0 && NeedsFour == 0;
            public bool NearOnly => !Unreadable && NeedsFour == 0 && Near > 0;
        }

        private sealed class BoneWeightAuditResult
        {
            public bool Ran { get; set; }
            public string? Failure { get; set; }
            public TimeSpan Budget { get; set; }
            public TimeSpan Elapsed { get; set; }
            public int RigidsInScope { get; set; }
            public int RigidsRead { get; set; }
            public int RigidsUnreadable { get; set; }
            public int RigidsNotReached { get; set; }
            // Meshes of the rigids that could not be read or were not reached (format unknown).
            public int MeshesUnreadableRigid { get; set; }
            public int MeshesNotReached { get; set; }
            public List<BoneWeightAuditMesh> Meshes { get; } = [];
            // Expected battle part instances per (rigid, LOD, part); null when not computable.
            public Dictionary<(string Rigid, int Lod, int Part), double>? Instances { get; set; }
            public string? InstancesNote { get; set; }
        }

        // The class of one weighted4 vertex from its stored bytes.
        private static BoneWeightClass ClassifyBoneWeights(byte[] boneIndex, byte[] boneWeight)
        {
            var sum = boneWeight[0] + boneWeight[1] + boneWeight[2];
            var weights = new Dictionary<int, int>();
            void Add(int bone, int weight)
                => weights[bone] = weights.GetValueOrDefault(bone) + weight;
            if (boneWeight[0] > 0)
                Add(boneIndex[0], boneWeight[0]);
            if (boneWeight[1] > 0)
                Add(boneIndex[1], boneWeight[1]);
            if (boneWeight[2] > 0)
                Add(boneIndex[2], boneWeight[2]);
            if (sum <= 254)
                Add(boneIndex[3], 255 - sum);

            if (weights.Count <= 2)
                return BoneWeightClass.Exact;

            var beyondTwo = weights.Values.OrderByDescending(weight => weight).Skip(2);
            return beyondTwo.All(weight => weight <= BoneWeightAuditNearMaximumWeight)
                ? BoneWeightClass.Near
                : BoneWeightClass.NeedsFour;
        }

        private static string GetBoneWeightAuditFormat(VertexFormat format)
            => format switch
            {
                VertexFormat.Static => "Static",
                VertexFormat.Weighted => "Weighted",
                VertexFormat.Cinematic => "Cinematic",
                _ => "Other",
            };

        private static void RunBoneWeightAudit(
            BatchState state,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress,
            Func<string, RmvFile?>? loadRigid,
            TimeSpan? budget)
        {
            var result = new BoneWeightAuditResult
            {
                Ran = true,
                Budget = budget ?? TimeSpan.FromSeconds(BoneWeightAuditBudgetSeconds),
            };
            state.BoneWeightAudit = result;
            var timer = Stopwatch.StartNew();
            try
            {
                var checks = state.AssetChecks;
                if (checks == null)
                {
                    result.Failure = "the LOD audit did not collect any rigid";
                    return;
                }

                loadRigid ??= path =>
                {
                    var file = ResolveGameplayTraversalContainer(
                            state.Source, GetGameplayTraversalContainers(state), path)?
                        .FindFile(path);
                    return file == null ? null : ModelFactory.Create().Load(file.DataSource.ReadData());
                };
                var componentsByRigid = checks.Components.Values
                    .GroupBy(component => component.RigidPath, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.OrderBy(item => item.WsModelPath, StringComparer.Ordinal).ToList(),
                        StringComparer.OrdinalIgnoreCase);
                var rigids = checks.Rigids.Values
                    .Where(rigid => rigid.SourceOwned)
                    .OrderBy(rigid => rigid.RigidPath, StringComparer.Ordinal)
                    .ToList();
                result.RigidsInScope = rigids.Count;
                for (var index = 0; index < rigids.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var scoped = rigids[index];
                    if (timer.Elapsed > result.Budget)
                    {
                        foreach (var rest in rigids.Skip(index))
                        {
                            result.RigidsNotReached++;
                            result.MeshesNotReached += rest.VertexCounts.Sum(lod => lod.Length);
                        }

                        break;
                    }

                    ReportProgress(progress, "Auditing bone weights", index + 1, rigids.Count, scoped.RigidPath);
                    RmvFile? rigid = null;
                    try
                    {
                        rigid = loadRigid(scoped.RigidPath);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        rigid = null;
                    }

                    if (rigid?.ModelList == null)
                    {
                        result.RigidsUnreadable++;
                        result.MeshesUnreadableRigid += scoped.VertexCounts.Sum(lod => lod.Length);
                        continue;
                    }

                    result.RigidsRead++;
                    var components = componentsByRigid.GetValueOrDefault(scoped.RigidPath) ?? [];
                    for (var lod = 0; lod < rigid.ModelList.Length; lod++)
                    {
                        for (var part = 0; part < rigid.ModelList[lod].Length; part++)
                        {
                            result.Meshes.Add(AuditBoneWeightMesh(
                                state, checks, scoped.RigidPath, lod, part, rigid.ModelList[lod][part], components));
                        }
                    }
                }

                ComputeBoneWeightAuditInstances(state, checks, result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Diagnostic stage: never fails a run.
                result.Failure = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                result.Elapsed = timer.Elapsed;
                state.PhaseDurations["Bone weight audit"] = result.Elapsed;
            }
        }

        private static BoneWeightAuditMesh AuditBoneWeightMesh(
            BatchState state,
            AssetChecksResult checks,
            string rigidPath,
            int lod,
            int part,
            RmvModel model,
            List<AssetCheckComponent> components)
        {
            var format = model.Material?.BinaryVertexFormat ?? VertexFormat.Unknown;
            var vertices = model.Mesh?.VertexList ?? [];
            int exact = 0, near = 0, needsFour = 0;
            var unreadable = false;
            if (format == VertexFormat.Cinematic)
            {
                foreach (var vertex in vertices)
                {
                    if (vertex.BoneIndex is not { Length: >= 4 } || vertex.BoneWeight is not { Length: >= 4 })
                    {
                        unreadable = true;
                        break;
                    }

                    var weights = new byte[4];
                    for (var slot = 0; slot < 4; slot++)
                        weights[slot] = (byte)Math.Clamp((int)Math.Round(vertex.BoneWeight[slot] * 255f), 0, 255);
                    switch (ClassifyBoneWeights(vertex.BoneIndex, weights))
                    {
                        case BoneWeightClass.Exact:
                            exact++;
                            break;
                        case BoneWeightClass.Near:
                            near++;
                            break;
                        default:
                            needsFour++;
                            break;
                    }
                }
            }

            // The shaders drawing this part: those of the xml materials of the WSModels that
            // bind it, else the type of the rigid's embedded material.
            var shaders = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var component in components)
            {
                if (lod >= component.MaterialAssignments.Length || part >= component.MaterialAssignments[lod].Length)
                    continue;

                if (checks.Materials.TryGetValue(Normalize(component.MaterialAssignments[lod][part]), out var material) &&
                    GetCompactionShaderName(material.Shader) is { } shader)
                {
                    shaders.Add(shader);
                }
            }

            if (shaders.Count == 0)
                shaders.Add("embedded:" + (model.Material?.MaterialId.ToString() ?? "unknown"));

            return new BoneWeightAuditMesh(
                rigidPath, lod, part, GetBoneWeightAuditFormat(format), vertices.Length,
                exact, near, needsFour, unreadable, shaders.ToList());
        }

        // Expected battle part instances, with the same weights the asset checks use (army
        // weight of each root x its WSModel instance weights x the scenario LOD
        // probabilities), without touching the asset checks' own counters.
        private static void ComputeBoneWeightAuditInstances(
            BatchState state,
            AssetChecksResult checks,
            BoneWeightAuditResult result)
        {
            var audit = state.LodAudit;
            if (state.ArmyResidencyModel == null)
            {
                result.InstancesNote = "the army residency model is not available";
                return;
            }

            if (audit == null || audit.ScenarioLods.Count == 0 || checks.Roots.Count == 0)
            {
                result.InstancesNote = "the LOD audit gave no scenario LODs or roots";
                return;
            }

            var factors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in checks.Roots)
            {
                var unitWeight = CalculateExpectedCrossRigidArmyDrawWeight(state, root.VmdPath, 1.0, [(1.0, 1.0)]);
                if (unitWeight <= 0)
                    continue;

                foreach (var (wsModelPath, weight) in root.InstanceWeightByWsModel)
                    factors[wsModelPath] = factors.GetValueOrDefault(wsModelPath) + unitWeight * weight;
            }

            if (factors.Count == 0)
            {
                result.InstancesNote = "no root has an army weight";
                return;
            }

            var instances = new Dictionary<(string Rigid, int Lod, int Part), double>();
            foreach (var component in checks.Components.Values.Where(item => item.SourceOwnedRigid))
            {
                var factor = factors.GetValueOrDefault(component.WsModelPath);
                var lodCount = component.MaterialAssignments.Length;
                if (factor <= 0 || lodCount == 0)
                    continue;

                for (var index = 0; index < audit.ScenarioLods.Count; index++)
                {
                    var lod = Math.Min(audit.ScenarioLods[index], lodCount - 1);
                    var weight = factor * audit.ScenarioLodProbabilities[index];
                    for (var part = 0; part < component.MaterialAssignments[lod].Length; part++)
                    {
                        var key = (component.RigidPath, lod, part);
                        instances[key] = instances.GetValueOrDefault(key) + weight;
                    }
                }
            }

            result.Instances = instances;
        }

        private static string DescribeBoneWeightShaderPair(string shader, out string status)
        {
            status = string.Empty;
            if (shader.StartsWith("embedded:", StringComparison.Ordinal))
            {
                status = "embedded material, no shader to pair";
                return shader;
            }

            if (!shader.StartsWith("weighted4_", StringComparison.Ordinal))
            {
                status = "not a weighted4_ shader";
                return shader;
            }

            var twin = "weighted2_" + shader["weighted4_".Length..];
            var table = Wh3ShaderChannels.Value.Table;
            if (table == null)
                status = "channel table unavailable";
            else if (!table.Shaders.TryGetValue(shader, out var fourMap))
                status = "weighted4 shader not in the channel table";
            else if (!table.Shaders.TryGetValue(twin, out var twoMap))
                status = "no weighted2 twin in the channel table";
            else
            {
                status = fourMap.Count == twoMap.Count &&
                         fourMap.All(pair => twoMap.TryGetValue(pair.Key, out var channels) &&
                                             channels.Equals(pair.Value, StringComparison.OrdinalIgnoreCase))
                    ? "same slot->channel map"
                    : "slot->channel maps DIFFER";
            }

            return $"{shader} -> {twin}";
        }

        private static void AppendBoneWeightAuditSection(StringBuilder sb, BatchState state)
        {
            sb.AppendLine("Bone weight audit");
            sb.AppendLine("-----------------");
            var result = state.BoneWeightAudit;
            if (result == null || !result.Ran)
            {
                sb.AppendLine("The bone weight audit did not run. Nothing was written.");
                sb.AppendLine();
                return;
            }

            sb.AppendLine(
                "Report only: counts the 4-weight (Cinematic, weighted4_*) vertices that could be 2-weight " +
                "(Weighted, weighted2_*) with the same skinning. weighted2 reads one weight byte and takes the " +
                "second as 1 - w0; weighted4 reads three bytes and applies the implicit fourth only when " +
                "b0+b1+b2 <= 254, and w0/w1 only when their byte is above 0. A vertex is EXACT when it has at " +
                "most 2 effective bones (a bone named twice counts once); NEAR when it has more but every " +
                "weight beyond the two largest is at most 2/255 (lossy, information only); otherwise NEEDS 4.");
            if (result.Failure != null)
                sb.AppendLine($"The audit stopped early: {result.Failure}.");
            sb.AppendLine(
                $"Scope: source-owned rigids the LOD audit reached: {result.RigidsInScope:N0}; read " +
                $"{result.RigidsRead:N0}, unreadable {result.RigidsUnreadable:N0} ({result.MeshesUnreadableRigid:N0} " +
                $"mesh(es)), not reached within the {result.Budget.TotalSeconds:N0} s budget " +
                $"{result.RigidsNotReached:N0} ({result.MeshesNotReached:N0} mesh(es)). Time {result.Elapsed.TotalMilliseconds:N0} ms.");

            string LodBucket(int lod) => lod >= 3 ? "3+" : lod.ToString();
            sb.AppendLine("Meshes and vertices by vertex format and LOD:");
            foreach (var format in new[] { "Static", "Weighted", "Cinematic", "Other" })
            {
                var meshes = result.Meshes.Where(mesh => mesh.Format == format).ToList();
                var perLod = string.Join(
                    ", ",
                    new[] { "0", "1", "2", "3+" }.Select(bucket =>
                    {
                        var inLod = meshes.Where(mesh => LodBucket(mesh.Lod) == bucket).ToList();
                        return $"LOD {bucket}: {inLod.Count:N0} mesh(es), {inLod.Sum(mesh => (long)mesh.Vertices):N0} vertices";
                    }));
                sb.AppendLine($"  {format}: {meshes.Count:N0} mesh(es), {meshes.Sum(mesh => (long)mesh.Vertices):N0} vertices ({perLod})");
            }

            var cinematic = result.Meshes.Where(mesh => mesh.Format == "Cinematic").ToList();
            var convertible = cinematic.Where(mesh => mesh.Convertible).ToList();
            var nearOnly = cinematic.Where(mesh => mesh.NearOnly).ToList();
            var needsFour = cinematic.Where(mesh => !mesh.Unreadable && mesh.NeedsFour > 0).ToList();
            var unreadable = cinematic.Where(mesh => mesh.Unreadable).ToList();
            sb.AppendLine(
                $"Cinematic vertices by class: EXACT {cinematic.Sum(mesh => (long)mesh.Exact):N0}, NEAR " +
                $"{cinematic.Sum(mesh => (long)mesh.Near):N0}, NEEDS 4 {cinematic.Sum(mesh => (long)mesh.NeedsFour):N0}.");
            sb.AppendLine(
                $"Cinematic meshes ({cinematic.Count:N0}) land in exactly one of: convertible (all vertices " +
                $"EXACT) {convertible.Count:N0}; NEAR-only (convertible if NEAR were allowed) {nearOnly.Count:N0}; " +
                $"needs 4 {needsFour.Count:N0}; unreadable {unreadable.Count:N0}. Check: " +
                $"{convertible.Count + nearOnly.Count + needsFour.Count + unreadable.Count:N0} of {cinematic.Count:N0} " +
                ((convertible.Count + nearOnly.Count + needsFour.Count + unreadable.Count) == cinematic.Count
                    ? "(reconciles)."
                    : "(DOES NOT RECONCILE)."));
            sb.AppendLine(
                $"Not classified: {result.MeshesUnreadableRigid:N0} mesh(es) of unreadable rigids and " +
                $"{result.MeshesNotReached:N0} of rigids not reached (their vertex format is unknown).");
            foreach (var bucket in new[] { "0", "1", "2", "3+" })
            {
                var meshes = convertible.Where(mesh => LodBucket(mesh.Lod) == bucket).ToList();
                var nearMeshes = nearOnly.Where(mesh => LodBucket(mesh.Lod) == bucket).ToList();
                sb.AppendLine(
                    $"  LOD {bucket}: convertible {meshes.Count:N0} mesh(es), {meshes.Sum(mesh => (long)mesh.Vertices):N0} " +
                    $"vertices; NEAR-only {nearMeshes.Count:N0} mesh(es), {nearMeshes.Sum(mesh => (long)mesh.Vertices):N0} vertices");
            }

            var convertibleVertices = convertible.Sum(mesh => (long)mesh.Vertices);
            sb.AppendLine(
                $"Vertex bytes saved by converting the convertible meshes (4 per vertex): " +
                $"{convertibleVertices * 4:N0} of {cinematic.Sum(mesh => (long)mesh.Vertices) * 4:N0} Cinematic vertex-weight bytes.");

            // Shaders.
            var pairs = new Dictionary<string, (int Meshes, long Vertices, string Status)>(StringComparer.Ordinal);
            foreach (var mesh in convertible)
            {
                foreach (var shader in mesh.Shaders)
                {
                    var pair = DescribeBoneWeightShaderPair(shader, out var status);
                    var current = pairs.GetValueOrDefault(pair);
                    pairs[pair] = (current.Meshes + 1, current.Vertices + mesh.Vertices, status);
                }
            }

            sb.AppendLine("Shaders of the convertible meshes (a mesh counts once per shader that draws it):");
            if (pairs.Count == 0)
                sb.AppendLine("  none");
            foreach (var (pair, (meshes, vertices, status)) in pairs
                         .OrderByDescending(item => item.Value.Vertices)
                         .ThenBy(item => item.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"  {pair}: {meshes:N0} mesh(es), {vertices:N0} vertices; {status}{(status.Contains("DIFFER") ? "  <-- FLAG" : string.Empty)}");
            }

            // Weighting.
            if (result.Instances == null)
            {
                sb.AppendLine($"Expected battle instance weighting: not computed ({result.InstancesNote ?? "no data"}).");
            }
            else
            {
                double Weighted(IEnumerable<BoneWeightAuditMesh> meshes, Func<BoneWeightAuditMesh, long> vertices)
                    => meshes.Sum(mesh => vertices(mesh) *
                                          result.Instances.GetValueOrDefault((mesh.Rigid, mesh.Lod, mesh.Part)));
                var all = Weighted(cinematic, mesh => mesh.Vertices);
                var convertibleWeighted = Weighted(convertible, mesh => mesh.Vertices);
                sb.AppendLine(
                    $"Weighted by expected battle part instances: convertible vertices x instances " +
                    $"{convertibleWeighted:N0} of all Cinematic vertices x instances {all:N0} " +
                    $"({FormatLodAuditShare(convertibleWeighted, all)}).");
            }

            // Listing.
            sb.AppendLine($"Top {BoneWeightAuditListLimit} rigids by convertible vertices:");
            var ranked = convertible
                .GroupBy(mesh => mesh.Rigid, StringComparer.Ordinal)
                .Select(group => (
                    Rigid: group.Key,
                    Meshes: group.Count(),
                    Vertices: group.Sum(mesh => (long)mesh.Vertices),
                    Lods: string.Join(",", group.Select(mesh => mesh.Lod).Distinct().OrderBy(lod => lod)),
                    Shaders: string.Join(
                        ", ",
                        group.SelectMany(mesh => mesh.Shaders)
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(shader => shader, StringComparer.Ordinal)
                            .Select(shader => DescribeBoneWeightShaderPair(shader, out _)))))
                .OrderByDescending(item => item.Vertices)
                .ThenBy(item => item.Rigid, StringComparer.Ordinal)
                .ToList();
            foreach (var item in ranked.Take(BoneWeightAuditListLimit))
            {
                sb.AppendLine(
                    $"  {item.Rigid}: {item.Meshes:N0} mesh(es), {item.Vertices:N0} vertices, LOD(s) {item.Lods}; {item.Shaders}");
            }

            if (ranked.Count > BoneWeightAuditListLimit)
                sb.AppendLine($"  ... and {ranked.Count - BoneWeightAuditListLimit:N0} more");
            if (ranked.Count == 0)
                sb.AppendLine("  none");
            sb.AppendLine();
        }
    }
}
