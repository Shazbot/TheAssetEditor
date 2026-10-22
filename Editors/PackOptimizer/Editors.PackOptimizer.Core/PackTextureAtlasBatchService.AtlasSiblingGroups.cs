namespace Editors.PackOptimizer.Core
{
    public sealed partial class PackTextureAtlasBatchService
    {
        // A source-texture sibling group is a set of atlas candidates (geometry + LOD + part)
        // that one consumer asset (a WSModel, or a rigid for embedded materials) references the
        // same source texture from. Atlasing rewrites the material of every member, so the source
        // texture is only released when every member is rewritten; a partial group keeps the
        // source texture resident and adds an atlas on top. Groups are therefore all-or-nothing.
        // A group is open when some reference to one of its textures from one of its consumers
        // belongs to a mesh that is not an eligible candidate: it can never be completed.
        private sealed record SourceTextureSiblingGroup(
            List<MeshKey> Members,
            bool IsOpen,
            string BlockReason);

        private static List<SourceTextureSiblingGroup> BuildSourceTextureSiblingGroups(
            BatchState state,
            IReadOnlyCollection<AtlasCandidate> candidates)
        {
            var result = new List<SourceTextureSiblingGroup>();
            if (candidates.Count == 0)
                return result;

            var eligible = candidates.Select(candidate => candidate.Key).ToHashSet();
            var candidateTexturePaths = GetAtlasValueGateCandidateTexturePaths(candidates);
            if (candidateTexturePaths.Count == 0)
                return result;

            var index = state.AtlasValueGateSourceTextureIndex ??=
                BuildAtlasValueGateSourceTextureIndex(
                    state,
                    state.AtlasValueGateCandidateTexturePathsInitialized
                        ? state.AtlasValueGateCandidateTexturePaths
                        : candidateTexturePaths);

            // (texture, consumer) -> candidates that reference it through an atlas slot.
            var pairs = new SortedDictionary<(string Texture, string Consumer), List<MeshKey>>(
                Comparer<(string Texture, string Consumer)>.Create((a, b) =>
                {
                    var c = StringComparer.Ordinal.Compare(a.Texture, b.Texture);
                    return c != 0 ? c : StringComparer.Ordinal.Compare(a.Consumer, b.Consumer);
                }));
            foreach (var candidate in candidates)
            {
                foreach (var channel in AtlasChannels)
                {
                    if (!candidate.ResolvedChannels.Contains(channel.Slot) &&
                        !candidate.ConstantChannels.ContainsKey(channel.Slot))
                    {
                        continue;
                    }

                    var texture = Normalize(GetTexturePath(candidate.MaterialDocument, channel.Slot));
                    if (texture.Length == 0 ||
                        IsTexturePlaceholder(texture) ||
                        !index.ContainsKey(texture))
                    {
                        continue;
                    }

                    foreach (var usage in candidate.Usages)
                    {
                        var consumer = Normalize(usage.AssetPath).ToLowerInvariant();
                        var pairKey = (texture.ToLowerInvariant(), consumer);
                        if (!pairs.TryGetValue(pairKey, out var members))
                        {
                            members = [];
                            pairs[pairKey] = members;
                        }

                        if (!members.Contains(candidate.Key))
                            members.Add(candidate.Key);
                    }
                }
            }

            var parent = new Dictionary<MeshKey, MeshKey>();
            MeshKey Find(MeshKey key)
            {
                if (!parent.TryGetValue(key, out var p))
                {
                    parent[key] = key;
                    return key;
                }

                var root = key;
                while (!parent[root].Equals(root))
                    root = parent[root];
                while (!parent[key].Equals(root))
                {
                    var next = parent[key];
                    parent[key] = root;
                    key = next;
                }

                return root;
            }

            void Union(MeshKey a, MeshKey b)
            {
                var ra = Find(a);
                var rb = Find(b);
                if (!ra.Equals(rb))
                    parent[rb] = ra;
            }

            var lookups = new Dictionary<
                string,
                ILookup<string, AtlasValueGateSourceReference>>(StringComparer.OrdinalIgnoreCase);
            var pairBlockers = new List<(MeshKey Member, string Reason)>();
            foreach (var (pairKey, members) in pairs)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                if (!lookups.TryGetValue(pairKey.Texture, out var lookup))
                {
                    lookup = index[pairKey.Texture].References.ToLookup(
                        reference => reference.ConsumerAssetPath,
                        StringComparer.OrdinalIgnoreCase);
                    lookups[pairKey.Texture] = lookup;
                }

                foreach (var member in members.Skip(1))
                    Union(members[0], member);
                Find(members[0]);

                var blockerRecorded = false;
                foreach (var reference in lookup[pairKey.Consumer])
                {
                    if (reference.Mesh is { } mesh && eligible.Contains(mesh))
                    {
                        Union(members[0], mesh);
                        continue;
                    }

                    // Keep going: the remaining eligible references must still be joined
                    // so the whole group is rejected together.
                    if (blockerRecorded)
                        continue;

                    var other = reference.Mesh is { } blocker
                        ? $"{blocker}"
                        : "a material binding without an atlas candidate part";
                    pairBlockers.Add((
                        members[0],
                        $"{other} in the same model ({pairKey.Consumer}) uses the same " +
                        $"source texture {pairKey.Texture} and cannot be atlased"));
                    blockerRecorded = true;
                }
            }

            var componentMembers = new Dictionary<MeshKey, List<MeshKey>>();
            foreach (var key in parent.Keys.ToList())
            {
                var root = Find(key);
                if (!componentMembers.TryGetValue(root, out var list))
                {
                    list = [];
                    componentMembers[root] = list;
                }

                list.Add(key);
            }

            var blockerByRoot = new Dictionary<MeshKey, string>();
            foreach (var (member, reason) in pairBlockers)
                blockerByRoot.TryAdd(Find(member), reason);

            foreach (var (root, members) in componentMembers
                         .OrderBy(entry => entry.Value.Min(key => key.ToString()), StringComparer.Ordinal))
            {
                result.Add(
                    new SourceTextureSiblingGroup(
                        members
                            .OrderBy(key => key.GeometryPath, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(key => key.LodIndex)
                            .ThenBy(key => key.PartIndex)
                            .ToList(),
                        blockerByRoot.ContainsKey(root),
                        blockerByRoot.GetValueOrDefault(root, string.Empty)));
            }

            return result;
        }

        private static List<List<AtlasCandidate>> CloseSourceTextureSiblingGroups(
            BatchState state,
            List<List<AtlasCandidate>> working)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            if (working.Count == 0)
                return working;

            var candidateByKey = working
                .SelectMany(batch => batch)
                .GroupBy(candidate => candidate.Key)
                .ToDictionary(group => group.Key, group => group.First());
            // Without LOD-aware planning the value gate leaves pre-atlas structural merge
            // candidates unchanged, so they cannot be completed members of any group.
            var eligible = state.LodAwareAtlasPlanningEnabled
                ? candidateByKey.Values.ToList()
                : candidateByKey.Values
                    .Where(candidate => !state.PreAtlasStructuralMergeMeshes.Contains(candidate.Key))
                    .ToList();
            var groups = BuildSourceTextureSiblingGroups(state, eligible);
            state.SourceTextureSiblingGroupCount += groups.Count;
            state.SourceTextureSiblingOpenGroupCount += groups.Count(group => group.IsOpen);
            state.SourceTextureSiblingClosedGroupCount += groups.Count(group => !group.IsOpen);
            state.SourceTextureSiblingMemberCount += groups.Sum(group => group.Members.Count);

            // Units: sibling groups joined with the structural merge components that are
            // already atomic, so a unit is never split by either rule.
            var parent = new Dictionary<MeshKey, MeshKey>();
            MeshKey Find(MeshKey key)
            {
                if (!parent.TryGetValue(key, out _))
                {
                    parent[key] = key;
                    return key;
                }

                var root = key;
                while (!parent[root].Equals(root))
                    root = parent[root];
                while (!parent[key].Equals(root))
                {
                    var next = parent[key];
                    parent[key] = root;
                    key = next;
                }

                return root;
            }

            void Union(MeshKey a, MeshKey b)
            {
                var ra = Find(a);
                var rb = Find(b);
                if (!ra.Equals(rb))
                    parent[rb] = ra;
            }

            foreach (var group in groups)
            {
                foreach (var member in group.Members.Skip(1))
                    Union(group.Members[0], member);
                Find(group.Members[0]);
            }

            if (state.LodAwareAtlasPlanningEnabled)
            {
                foreach (var component in BuildAtlasStructuralMergeComponents(state, eligible))
                {
                    var present = component.Members.Where(candidateByKey.ContainsKey).ToArray();
                    if (present.Length < 2 || !present.Any(parent.ContainsKey))
                        continue;

                    foreach (var member in present.Skip(1))
                        Union(present[0], member);
                }
            }

            var unitMembers = new Dictionary<MeshKey, List<MeshKey>>();
            foreach (var key in parent.Keys.ToList())
            {
                var root = Find(key);
                if (!unitMembers.TryGetValue(root, out var list))
                {
                    list = [];
                    unitMembers[root] = list;
                }

                list.Add(key);
            }

            var openReasonByUnit = new Dictionary<MeshKey, string>();
            foreach (var group in groups.Where(group => group.IsOpen))
                openReasonByUnit.TryAdd(Find(group.Members[0]), group.BlockReason);

            // Open units: nothing can complete them, leave every member unchanged.
            var removedKeys = new HashSet<MeshKey>();
            foreach (var group in groups.Where(group => group.IsOpen))
            {
                var members = group.Members
                    .Where(candidateByKey.ContainsKey)
                    .Select(key => candidateByKey[key])
                    .ToList();
                RemoveAtlasFamilyMembers(
                    state,
                    working,
                    members,
                    "Atlas planning: kept unchanged because " + group.BlockReason + ".");
                removedKeys.UnionWith(members.Select(member => member.Key));
            }

            foreach (var (root, members) in unitMembers)
            {
                if (!openReasonByUnit.TryGetValue(root, out var unitReason))
                    continue;

                var dragged = members
                    .Where(key => !removedKeys.Contains(key) && candidateByKey.ContainsKey(key))
                    .Select(key => candidateByKey[key])
                    .ToList();
                if (dragged.Count == 0)
                    continue;

                RemoveAtlasFamilyMembers(
                    state,
                    working,
                    dragged,
                    "Atlas planning: kept unchanged because it is tied by a structural merge " +
                    "component to a mesh that cannot be atlased: " + unitReason + ".");
                removedKeys.UnionWith(dragged.Select(member => member.Key));
            }

            state.SourceTextureSiblingCandidatesRejected += removedKeys.Count;

            // Closed units that planning scattered over several batches go to one host batch.
            IReadOnlyList<MergeAffinityGroup>? mergeAffinityGroups = null;
            var closedUnits = unitMembers
                .Where(entry => !openReasonByUnit.ContainsKey(entry.Key) && entry.Value.Count >= 2)
                .OrderBy(entry => entry.Value.Min(key => key.ToString()), StringComparer.Ordinal)
                .Select(entry => entry.Value)
                .ToList();
            foreach (var unit in closedUnits)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                if (GetBatchIdsForKeys(working, unit).Count <= 1)
                    continue;

                mergeAffinityGroups ??= BuildMergeAffinityGroups(
                    state,
                    candidateByKey.Values,
                    recordDiagnostics: false);
                var members = unit
                    .Select(key => candidateByKey[key])
                    .ToList();
                if (TryMoveAtlasFamilyMembersToBestHost(
                        state,
                        working,
                        members,
                        mergeAffinityGroups,
                        out _))
                {
                    state.SourceTextureSiblingGroupsMoved++;
                    continue;
                }

                RemoveAtlasFamilyMembers(
                    state,
                    working,
                    members,
                    "Atlas planning: kept unchanged because the parts that use the same source " +
                    "texture could not be hosted by one atlas page without splitting them.");
                removedKeys.UnionWith(unit);
                state.SourceTextureSiblingCandidatesRejected += members.Count;
            }

            // Families registered before this step may have lost members or been split.
            var presentKeys = working.SelectMany(batch => batch).Select(c => c.Key).ToHashSet();
            state.LodAwareAtlasFamilies.RemoveAll(family =>
                !family.All(presentKeys.Contains) ||
                GetBatchIdsForKeys(working, family).Count != 1);

            foreach (var group in groups.Where(group => !group.IsOpen))
            {
                if (group.Members.Any(removedKeys.Contains))
                    continue;

                var keys = group.Members.ToHashSet();
                state.SourceTextureSiblingGroups.Add(keys);
                if (keys.Count >= 2)
                    RegisterLodAtlasFamily(state, keys);
            }

            // A unit that joined sibling groups with a structural component is one execution
            // unit for the value gate as well.
            foreach (var unit in closedUnits)
            {
                if (unit.Any(removedKeys.Contains))
                    continue;

                RegisterLodAtlasFamily(state, unit);
            }

            return working;
        }

        // Safety net: every sibling group touching the accepted set must be fully accepted.
        // A partial group keeps its source texture resident while the atlas adds bytes, so the
        // accepted batches containing a member are dropped whole.
        private static void EnforceSourceTextureSiblingInvariant(
            BatchState state,
            List<List<AtlasCandidate>> result,
            IReadOnlyDictionary<
                MeshKey,
                Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>>
                expectedEntitiesByMesh,
            int acceptedEconomicsStartIndex)
        {
            if (state.SourceTextureSiblingGroups.Count == 0 || result.Count == 0)
                return;

            while (true)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                var accepted = result
                    .SelectMany(batch => batch)
                    .Select(candidate => candidate.Key)
                    .ToHashSet();
                var violated = state.SourceTextureSiblingGroups
                    .Where(group => group.Overlaps(accepted) && !group.IsSubsetOf(accepted))
                    .ToList();
                if (violated.Count == 0)
                    return;

                state.SourceTextureSiblingInvariantViolations += violated.Count;
                var violatedKeys = violated.SelectMany(group => group).ToHashSet();
                var dropIndexes = Enumerable.Range(0, result.Count)
                    .Where(index => result[index].Any(candidate => violatedKeys.Contains(candidate.Key)))
                    .OrderByDescending(index => index)
                    .ToList();
                var aligned = state.AtlasValueGateAcceptedBatchEconomics.Count -
                              acceptedEconomicsStartIndex == result.Count &&
                              state.AtlasValueGateAcceptedBatchUndo.Count ==
                              state.AtlasValueGateAcceptedBatchEconomics.Count;
                foreach (var index in dropIndexes)
                {
                    var batch = result[index];
                    result.RemoveAt(index);
                    state.SourceTextureSiblingInvariantBatchesDropped++;
                    foreach (var candidate in batch)
                    {
                        RecordSkip(
                            state,
                            candidate.RootVmdPath,
                            candidate.Key,
                            candidate.Usages.FirstOrDefault()?.AssetPath ?? string.Empty,
                            "Atlas value gate: dropped because another part using the same " +
                            "source texture in the same model was not accepted.");
                    }

                    state.AtlasValueGateCandidatesRejected += batch.Count;
                    if (aligned)
                        UndoAtlasValueGateAcceptedBatch(
                            state,
                            batch,
                            acceptedEconomicsStartIndex + index);
                }

                if (aligned)
                {
                    if (acceptedEconomicsStartIndex == 0)
                    {
                        RebuildAtlasValueGateAcceptedCoverage(
                            state,
                            result,
                            expectedEntitiesByMesh);
                    }
                }
            }
        }

        private static void UndoAtlasValueGateAcceptedBatch(
            BatchState state,
            IReadOnlyList<AtlasCandidate> batch,
            int economicsIndex)
        {
            var entry = state.AtlasValueGateAcceptedBatchEconomics[economicsIndex];
            var undo = state.AtlasValueGateAcceptedBatchUndo[economicsIndex];
            state.AtlasValueGateAcceptedBatchEconomics.RemoveAt(economicsIndex);
            state.AtlasValueGateAcceptedBatchUndo.RemoveAt(economicsIndex);

            state.AtlasValueGateBatchesAccepted--;
            state.AtlasValueGateCandidatesAccepted -= entry.CandidateCount;
            state.AtlasValueGateGeneratedBcnBytesAccepted -= entry.GeneratedBcnBytes;
            state.AtlasValueGateRetiredBcnBytesAccepted -= entry.RetiredSourceBcnBytes;
            state.AtlasValueGateNetBcnBytesAccepted -= entry.NetBcnBytes;
            state.AtlasValueGateExpectedArmyGeneratedBcnBytesAccepted -=
                entry.ExpectedArmyGeneratedBcnBytes;
            state.AtlasValueGateExpectedArmyRetiredBcnBytesAccepted -=
                entry.ExpectedArmyRetiredSourceBcnBytes;
            state.AtlasValueGateExpectedArmyNetBcnBytesAccepted -=
                entry.ExpectedArmyNetBcnBytes;
            state.AtlasValueGateExpectedDrawsAccepted -= entry.ExpectedArmyDrawsEliminated;
            switch (entry.AcceptanceKind)
            {
                case AtlasValueGateAcceptanceKind.TextureOnlyMerge:
                    state.AtlasValueGateTextureOnlyMergeBatchesAccepted--;
                    state.AtlasValueGateTextureOnlyMergeCandidatesAccepted -= entry.CandidateCount;
                    break;
                case AtlasValueGateAcceptanceKind.TextureOnlyRetirement:
                    state.AtlasValueGateTextureOnlyBatchesAccepted--;
                    state.AtlasValueGateTextureOnlyCandidatesAccepted -= entry.CandidateCount;
                    state.AtlasValueGateTextureOnlyPackReferenceRetiredSourceTextureCount -=
                        undo.PackReferenceRetiredSourceTextureCount;
                    state.AtlasValueGateTextureOnlyRetiredSourceTextureCount -=
                        undo.RetiredSourceTextureCount;
                    state.AtlasValueGateTextureOnlyScenarioDisplacedSourceTextureCount -=
                        undo.ScenarioDisplacedSourceTextureCount;
                    state.AtlasValueGateTextureOnlyExpectedRetiredSourceTextureCount -=
                        undo.ExpectedRetiredSourceTextureCount;
                    state.AtlasValueGateTextureOnlyRetiredBcnBytesAccepted -=
                        entry.RetiredSourceBcnBytes;
                    state.AtlasValueGateTextureOnlyExpectedRetiredBcnBytesAccepted -=
                        entry.ExpectedArmyRetiredSourceBcnBytes;
                    break;
                case AtlasValueGateAcceptanceKind.TextureConsolidation:
                    state.AtlasValueGateTextureConsolidationBatchesAccepted--;
                    state.AtlasValueGateTextureConsolidationCandidatesAccepted -=
                        entry.CandidateCount;
                    state.AtlasValueGateTextureConsolidatedAssignments -=
                        undo.ConsolidatedTextureAssignments;
                    break;
            }

            // Rewritten references are mesh-specific: drop those of the dropped candidates.
            var droppedKeys = batch.Select(candidate => candidate.Key).ToHashSet();
            state.AtlasValueGateRewrittenSourceReferences.RemoveWhere(reference =>
                reference.Mesh is { } mesh && droppedKeys.Contains(mesh));
            state.AtlasValueGateRewrittenReferenceCountByTexture.Clear();
            state.AtlasValueGateRewrittenPackReferenceCountByTexture.Clear();

            // Source-texture residency diagnostics: forget textures nothing rewrites any more,
            // refresh the rewritten count of the others.
            var rewrittenCounts = state.AtlasValueGateSourceTextureIndex == null
                ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                : state.AtlasValueGateSourceTextureIndex
                    .ToDictionary(
                        texture => texture.Key,
                        texture => texture.Value.References.Count(
                            state.AtlasValueGateRewrittenSourceReferences.Contains),
                        StringComparer.OrdinalIgnoreCase);
            foreach (var path in state.AtlasValueGateSourceTextureResidencyDiagnostics.Keys.ToList())
            {
                var count = rewrittenCounts.GetValueOrDefault(path, 0);
                if (count == 0)
                {
                    state.AtlasValueGateSourceTextureResidencyDiagnostics.Remove(path);
                    continue;
                }

                var diagnostic = state.AtlasValueGateSourceTextureResidencyDiagnostics[path];
                state.AtlasValueGateSourceTextureResidencyDiagnostics[path] =
                    diagnostic with { RewrittenReferenceCountAfter = count };
            }
        }

        private static void RebuildAtlasValueGateAcceptedCoverage(
            BatchState state,
            IReadOnlyList<List<AtlasCandidate>> result,
            IReadOnlyDictionary<
                MeshKey,
                Dictionary<Wh3ArmyUnitCategory, Dictionary<string, double>>>
                expectedEntitiesByMesh)
        {
            // The accepted-unit set is a union over batches; only units no remaining batch of
            // this gate run covers can be released safely.
            var remaining = new HashSet<string>(StringComparer.Ordinal);
            foreach (var batch in result)
                remaining.UnionWith(CalculateAtlasBatchCoverage(batch, expectedEntitiesByMesh).AffectedUnits);
            if (state.AtlasValueGateAcceptedBatchEconomics.Count == result.Count)
                state.AtlasValueGateAcceptedAffectedUnits.IntersectWith(remaining);
        }
    }
}
