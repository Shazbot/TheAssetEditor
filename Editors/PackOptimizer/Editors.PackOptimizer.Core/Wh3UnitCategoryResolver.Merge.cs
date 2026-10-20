namespace Editors.PackOptimizer.Core
{
    internal static partial class Wh3UnitCategoryResolver
    {
        /// <summary>
        /// The resolution used when other enabled mods are present: everything the game and
        /// source tables alone resolve to (<paramref name="baseline"/>, exactly the result
        /// without consumers) plus what the consumers' rows add. A consumer can add users, roots
        /// and links, never remove or change one: a unit that exists in the baseline keeps its
        /// baseline attributes, and the entries a consumer creates for such a unit by changing
        /// its land unit are not taken.
        /// </summary>
        internal static Wh3UnitCategoryResolution MergeAdditive(
            Wh3UnitCategoryResolution baseline,
            Wh3UnitCategoryResolution withConsumers)
        {
            // Units the baseline knows, and the land units each is resolved to.
            var landUnitsByMain = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            void Know(string main, string land)
            {
                if (string.IsNullOrEmpty(main))
                    return;
                if (!landUnitsByMain.TryGetValue(main, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    landUnitsByMain[main] = set;
                }

                set.Add(land ?? string.Empty);
            }

            foreach (var usage in baseline.UsagesByVmd.Values.SelectMany(list => list))
                Know(usage.MainUnitKey, usage.LandUnitKey);
            foreach (var usage in baseline.DirectUsagesByVmd.Values.SelectMany(list => list))
                Know(usage.MainUnitKey, usage.LandUnitKey);
            foreach (var usage in baseline.DirectAssetUsagesByPath.Values.SelectMany(list => list))
                Know(usage.MainUnitKey, usage.LandUnitKey);
            foreach (var unit in baseline.RosterUnits)
                Know(unit.MainUnitKey, unit.LandUnitKey);
            foreach (var consumer in baseline.UnresolvedConsumersByVmd.Values.SelectMany(list => list))
                Know(consumer.MainUnitKey, consumer.LandUnitKey);

            bool TakesNew(string main, string land)
                => !landUnitsByMain.TryGetValue(main ?? string.Empty, out var lands) ||
                   lands.Contains(land ?? string.Empty);

            IReadOnlyDictionary<string, IReadOnlyList<T>> MergeLists<T>(
                IReadOnlyDictionary<string, IReadOnlyList<T>> first,
                IReadOnlyDictionary<string, IReadOnlyList<T>> second,
                Func<T, string> identity,
                Func<T, bool> take)
            {
                var result = new Dictionary<string, IReadOnlyList<T>>(first, StringComparer.OrdinalIgnoreCase);
                foreach (var (key, list) in second)
                {
                    var known = result.TryGetValue(key, out var existing)
                        ? existing
                        : (IReadOnlyList<T>)[];
                    var identities = known.Select(identity).ToHashSet(StringComparer.Ordinal);
                    var added = list.Where(item => take(item) && identities.Add(identity(item))).ToList();
                    if (added.Count != 0)
                        result[key] = known.Concat(added).ToList();
                }

                return result;
            }

            var usages = MergeLists(
                baseline.UsagesByVmd,
                withConsumers.UsagesByVmd,
                usage => $"{usage.VmdPath}|{usage.MainUnitKey}|{usage.LandUnitKey}|{usage.VisualRole}|{usage.ConsumerType}|{usage.RootVmdPath}|{usage.ParentVmdPath}",
                usage => TakesNew(usage.MainUnitKey, usage.LandUnitKey));
            var directUsages = MergeLists(
                baseline.DirectUsagesByVmd,
                withConsumers.DirectUsagesByVmd,
                usage => $"{usage.VmdPath}|{usage.MainUnitKey}|{usage.LandUnitKey}|{usage.VisualRole}|{usage.ConsumerType}|{usage.RootVmdPath}|{usage.ParentVmdPath}",
                usage => TakesNew(usage.MainUnitKey, usage.LandUnitKey));
            var directAssets = MergeLists(
                baseline.DirectAssetUsagesByPath,
                withConsumers.DirectAssetUsagesByPath,
                usage => $"{usage.AssetPath}|{usage.MainUnitKey}|{usage.LandUnitKey}|{usage.VisualRole}",
                usage => TakesNew(usage.MainUnitKey, usage.LandUnitKey));
            var unresolvedConsumers = MergeLists(
                baseline.UnresolvedConsumersByVmd,
                withConsumers.UnresolvedConsumersByVmd,
                consumer => $"{consumer.VmdPath}|{consumer.MainUnitKey}|{consumer.LandUnitKey}|{consumer.VisualRole}",
                consumer => TakesNew(consumer.MainUnitKey, consumer.LandUnitKey));

            var rosterIdentities = baseline.RosterUnits.Select(unit => unit.Identity).ToHashSet(StringComparer.Ordinal);
            var roster = baseline.RosterUnits
                .Concat(withConsumers.RosterUnits.Where(unit =>
                    TakesNew(unit.MainUnitKey, unit.LandUnitKey) && rosterIdentities.Add(unit.Identity)))
                .ToList();
            var unresolvedRoots = baseline.UnresolvedVmdRoots
                .Concat(withConsumers.UnresolvedVmdRoots.Where(root =>
                    !baseline.UnresolvedVmdRoots.Contains(root, StringComparer.OrdinalIgnoreCase)))
                .ToList();

            return new Wh3UnitCategoryResolution(
                usages,
                directUsages,
                directAssets,
                roster,
                baseline.Scenario,
                unresolvedRoots,
                unresolvedConsumers,
                baseline.ParsedRowsByTable,
                baseline.TableFilesRead,
                baseline.DirectlyResolvedVmdCount,
                baseline.PropagatedVmdCount,
                baseline.IsGameplayResolutionHealthy,
                baseline.GameplayResolutionHealthMessage,
                baseline.Diagnostics)
            {
                ConsumerDbFailures = withConsumers.ConsumerDbFailures,
                ConsumerRowOverrides = withConsumers.ConsumerRowOverrides,
                ConsumerTableInfos = withConsumers.ConsumerTableInfos,
                ConsumerRowsAdded = withConsumers.ConsumerRowsAdded,
            };
        }
    }
}
