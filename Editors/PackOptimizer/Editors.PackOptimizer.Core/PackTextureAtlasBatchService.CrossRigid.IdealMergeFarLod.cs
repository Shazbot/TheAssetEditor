using Editors.ImportExport.TextureAtlas;

namespace Editors.PackOptimizer.Core
{
    public sealed partial class PackTextureAtlasBatchService
    {
        // REPORT-ONLY "Phase 0b": cost of far-LOD, reduced-resolution shared
        // atlases built only inside generated cross-rigid payloads.
        //
        //  * Source rigids and materials are never rewritten, so the mesh
        //    level blockers of Phase 0 do not apply and nothing is retired:
        //    net cost = atlas bytes.
        //  * Only scenario LODs >= CrossRigidFarLodMinIndex merge with the
        //    atlas. The atlas-dependent saving of a scenario LOD is T1 minus
        //    strict T0 (T0 is obtainable without an atlas at every LOD).
        //  * The atlas is built from source mip 0 / 1 / 2: every source and
        //    crop size is divided by 2^mip (padding stays 8 px).

        internal const int CrossRigidFarLodMinIndex = 2;
        private static readonly int[] CrossRigidFarAtlasMips = [0, 1, 2];

        // LOD rows of a rigid with rowCount rows read by far scenario LODs.
        internal static int[] GetCrossRigidFarLodSourceRows(
            IReadOnlyList<int> scenarioLodIndices,
            int rowCount)
            => rowCount <= 0
                ? []
                : scenarioLodIndices
                    .Where(lod => lod >= CrossRigidFarLodMinIndex)
                    .Select(lod => Math.Min(lod, rowCount - 1))
                    .Distinct()
                    .OrderBy(row => row)
                    .ToArray();

        // Atlas-dependent saving of the far scenario LODs: T1 above T0.
        internal static double GetCrossRigidFarAtlasSaving(
            IReadOnlyList<int> scenarioLodIndices,
            IReadOnlyList<double> t1SavingByLod,
            IReadOnlyList<double> t0SavingByLod)
        {
            double total = 0;
            for (var index = 0; index < scenarioLodIndices.Count; index++)
            {
                if (scenarioLodIndices[index] < CrossRigidFarLodMinIndex)
                    continue;
                total += Math.Max(
                    0,
                    t1SavingByLod[index] - t0SavingByLod[index]);
            }

            return total;
        }

        // Qualifying material groups of the far scenario LODs only.
        internal static string[][] GetCrossRigidFarGroups(
            IReadOnlyList<int> scenarioLodIndices,
            IReadOnlyList<string[][]> groupsByLod)
        {
            var groups = new Dictionary<string, string[]>(StringComparer.Ordinal);
            for (var index = 0; index < scenarioLodIndices.Count; index++)
            {
                if (scenarioLodIndices[index] < CrossRigidFarLodMinIndex ||
                    groupsByLod[index] == null)
                {
                    continue;
                }

                foreach (var group in groupsByLod[index])
                    groups[string.Join("\u001e", group)] = group;
            }

            return groups.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => entry.Value)
                .ToArray();
        }

        internal static double[] SumCrossRigidSavingByLod(
            int lodCount,
            IEnumerable<IReadOnlyList<double>> savings)
        {
            var sums = new double[lodCount];
            foreach (var saving in savings)
            {
                for (var index = 0; index < lodCount; index++)
                    sums[index] += saving[index];
            }

            return sums;
        }

        internal static string BuildCrossRigidLodSplitLine(
            IReadOnlyList<int> scenarioLodIndices,
            IReadOnlyList<double> t1ByLod,
            IReadOnlyList<double> t0ByLod,
            double baselineTotal)
        {
            string Part(Func<int, double> value)
                => string.Join(" / ", scenarioLodIndices.Select((lod, index) =>
                    $"L{lod} {value(index):0.###}"));
            var total = t1ByLod.Sum();
            var above = scenarioLodIndices
                .Select((_, index) => Math.Max(0, t1ByLod[index] - t0ByLod[index]))
                .ToArray();
            var far = GetCrossRigidFarAtlasSaving(
                scenarioLodIndices,
                t1ByLod,
                t0ByLod);
            var share = baselineTotal > AtlasValueGateExpectedDrawEpsilon
                ? $" ({total / baselineTotal:P1} of baseline)"
                : string.Empty;
            return
                $"        LOD split of accepted T1 saving {total:0.###}{share}: " +
                Part(index => t1ByLod[index]) +
                "; above strict T0 (atlas-dependent) " +
                $"{above.Sum():0.###}: " +
                Part(index => above[index]) +
                $"; far (LOD >= {CrossRigidFarLodMinIndex}) atlas-dependent " +
                $"{far:0.###}";
        }

        private static List<string> BuildCrossRigidFarLodAtlasLines(
            BatchState state,
            IReadOnlyList<CrossRigidIdealMergePayload> allPayloads,
            IReadOnlyList<(string Key, double Saving, long Bytes)> acceptedT1,
            long? capMiB,
            string[]? previousFarKeys,
            double baselineTotal,
            out string[] farKeys)
        {
            var accounting = state.CrossRigidIdealMergeAccounting;
            var lines = new List<string>();
            var accepted = acceptedT1
                .Select(item => accounting.Payloads[item.Key])
                .ToArray();
            var lodIndices = accepted.Length != 0
                ? accepted[0].ScenarioLodIndices
                : allPayloads.Count != 0
                    ? allPayloads[0].ScenarioLodIndices
                    : [];
            lines.Add(BuildCrossRigidLodSplitLine(
                lodIndices,
                SumCrossRigidSavingByLod(
                    lodIndices.Length,
                    accepted.Select(p => p.SavingByScenarioLod)),
                SumCrossRigidSavingByLod(
                    lodIndices.Length,
                    accepted.Select(p => p.T0SavingByScenarioLod)),
                baselineTotal));

            // Far selection: greedy on far saving vs far bytes, same caps and
            // per-payload ceiling.
            var candidates = allPayloads
                .Select(payload => (
                    Payload: payload,
                    FarSaving: GetCrossRigidFarAtlasSaving(
                        payload.ScenarioLodIndices,
                        payload.SavingByScenarioLod,
                        payload.T0SavingByScenarioLod)))
                .Where(item => item.FarSaving > AtlasValueGateExpectedDrawEpsilon &&
                               GetCrossRigidFarGroups(
                                   item.Payload.ScenarioLodIndices,
                                   item.Payload
                                       .QualifyingMaterialGroupsByScenarioLod)
                                   .Length != 0)
                .ToArray();
            var farAccepted = SelectCrossRigidIdealMergePayloadKeysGreedy(
                candidates
                    .Select(item => (item.Payload.Key, item.FarSaving,
                        item.Payload.FarGeometryBytes))
                    .ToArray(),
                CrossRigidPerPayloadCostCeilingMultiplier *
                MaxCrossRigidGeneratedGeometryBytesPerExpectedArmyDraw,
                capMiB.HasValue ? capMiB.Value * 1024 * 1024 : null);
            farKeys = farAccepted
                .Select(item => item.Key)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray();
            var farSaving = farAccepted.Sum(item => item.Saving);
            var farBytes = farAccepted.Sum(item => item.Bytes);
            var fullBytes = farAccepted.Sum(item =>
                accounting.Payloads[item.Key].GeometryBytes);
            lines.Add(
                $"        far-LOD atlas selection (greedy on far atlas-dependent " +
                $"saving vs far LOD-row geometry): {candidates.Length:N0} candidate(s), " +
                $"{farAccepted.Count:N0} accepted, far draws {farSaving:0.###} " +
                $"({(baselineTotal > AtlasValueGateExpectedDrawEpsilon ? farSaving / baselineTotal : 0):P1} of baseline), " +
                $"far geometry {FormatMiB(farBytes)} (full-rigid bytes of the same " +
                $"payloads {FormatMiB(fullBytes)})");
            if (previousFarKeys != null &&
                farKeys.SequenceEqual(previousFarKeys, StringComparer.Ordinal))
            {
                lines.Add(
                    "        far-LOD atlas inputs: same accepted set as the " +
                    "previous cap");
                return lines;
            }

            var farPayloads = farAccepted
                .Select(item => accounting.Payloads[item.Key])
                .ToArray();
            SharedAtlasCostResult? first = null;
            foreach (var mip in CrossRigidFarAtlasMips)
            {
                var result = ComputeCrossRigidSharedAtlasCost(
                    BuildCrossRigidSharedAtlasCostInput(
                        state,
                        farPayloads,
                        farMode: true,
                        mipShift: mip));
                first ??= result;
                if (mip == 0)
                {
                    var reasons = result.IneligibleMaterialsByReason
                        .OrderByDescending(entry => entry.Value)
                        .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                        .Select(entry =>
                            $"{entry.Key} {entry.Value:N0} material(s) / " +
                            $"{result.LostSavingByReason.GetValueOrDefault(entry.Key):0.###} draws")
                        .ToArray();
                    lines.Add(
                        "        far-LOD atlas eligibility (material level only; " +
                        "UV0 tiling from vanilla coverage, any part/LOD of the " +
                        "base texture" +
                        (state.VanillaTextureCoverage == null
                            ? ", coverage unavailable: not modelled"
                            : string.Empty) +
                        $"): {result.EligibleMaterialCount:N0} of " +
                        $"{result.MaterialCount:N0} material(s) eligible; far draws " +
                        $"lost to an ineligible member {result.LostIneligibleSaving:0.###} " +
                        $"of {result.AtlasPayloadSaving:0.###}" +
                        (reasons.Length == 0
                            ? string.Empty
                            : "; by first reason: " + string.Join(", ", reasons)));
                }

                var perDraw = result.EligibleSaving > AtlasValueGateExpectedDrawEpsilon
                    ? result.AtlasBytes / result.EligibleSaving
                    : 0;
                lines.Add(
                    $"        far-LOD atlas m={mip} (source mip {mip}, sizes/{1 << mip}, " +
                    $"padding {TextureAtlasBuilder.DefaultPadding}): eligible far draws " +
                    $"{result.EligibleSaving:0.###} of {farSaving:0.###}, " +
                    $"{result.ComponentCount:N0} component(s) ({result.FitComponentCount:N0} " +
                    $"fit one page, {result.SelectedComponentCount:N0} selected, " +
                    $"{result.SelectedMaterialCount:N0} material(s)), atlas = net " +
                    $"{FormatMiB(result.AtlasBytes)} BCn, {FormatMiB(perDraw, 2)} per " +
                    $"far draw, far geometry {FormatMiB(result.GeometryBytes)}; " +
                    $"base pages largest {result.LargestPageWidth}x{result.LargestPageHeight}, " +
                    $"median {result.MedianPageWidth}x{result.MedianPageHeight}; " +
                    $"crops below 4 px: {result.SubBlockCropCount:N0}");
                var curve = BuildCrossRigidSharedAtlasCurveLine(
                    result,
                    $"far-LOD atlas m={mip}");
                if (curve != null)
                    lines.Add(curve);
                lines.Add(BuildCrossRigidSharedAtlasReachableLine(
                    result,
                    $"far-LOD atlas m={mip}"));
            }

            return lines;
        }
    }
}
