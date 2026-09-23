using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Utility;
using Shared.GameFormats.AnimationPack;

namespace Editors.ImportExport.Exporting.Exporters.RmvToGltf;

/// <summary>
/// Selects the neutral standing animation from WH3 battle-bin semantics. The
/// slot names are authoritative; animation filenames are only used as the
/// returned reference and are never inspected to choose the slot.
/// </summary>
public sealed class GltfAnimationDefaultResolver : IGltfAnimationDefaultResolver
{
    private readonly IHeadlessPackFileService _packFileService;

    public GltfAnimationDefaultResolver(IHeadlessPackFileService packFileService)
    {
        _packFileService = packFileService;
    }

    public GltfAnimationDefaults? Resolve(string skeletonName, IReadOnlySet<string> availableAnimationPaths)
    {
        var entries = new List<AnimationBinEntryGenericFormat>();
        foreach (var animationPack in PackFileServiceUtility.GetAllAnimPacks(_packFileService))
        {
            try
            {
                var database = AnimationPackSerializer.Load(animationPack, _packFileService);
                foreach (var fragment in database
                    .GetGenericAnimationSets()
                    .Where(fragment => string.Equals(fragment.SkeletonName, skeletonName, StringComparison.OrdinalIgnoreCase)))
                    entries.AddRange(fragment.Entries);
            }
            catch
            {
                // A malformed or unsupported animation pack should not make the
                // otherwise usable animation catalog fail.
            }
        }

        var defaults = new GltfAnimationDefaults(
            Select(entries, availableAnimationPaths, "STAND_IDLE_", "STAND"),
            Select(entries, availableAnimationPaths, "RIDER_STAND_IDLE_", "RIDER_STAND"),
            Select(entries, availableAnimationPaths, "FLY_IDLE_", "FLY_STAND"),
            Select(entries, availableAnimationPaths, "RIDER_FLY_IDLE_", "RIDER_FLY_STAND"));

        return defaults.Ground == null
            && defaults.Rider == null
            && defaults.Flying == null
            && defaults.RiderFlying == null
            ? null
            : defaults;
    }

    private static GltfAnimationDefault? Select(
        IEnumerable<AnimationBinEntryGenericFormat> entries,
        IReadOnlySet<string> availableAnimationPaths,
        string idlePrefix,
        string standSlot)
    {
        var candidates = entries
            .Where(entry => string.IsNullOrWhiteSpace(entry.AnimationFile) == false)
            .Where(entry => availableAnimationPaths.Contains(NormalizePath(entry.AnimationFile)))
            .Where(entry => entry.SlotName.StartsWith(idlePrefix, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.SlotName, standSlot, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var preferred = candidates
            .Where(entry => string.Equals(entry.SlotName, idlePrefix + "1", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault()
            ?? candidates
                .Where(entry => entry.SlotName.StartsWith(idlePrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => IdleNumber(entry.SlotName, idlePrefix))
                .FirstOrDefault()
            ?? candidates.FirstOrDefault(entry =>
                string.Equals(entry.SlotName, standSlot, StringComparison.OrdinalIgnoreCase));

        return preferred == null ? null : new GltfAnimationDefault(preferred.AnimationFile, preferred.SlotName);
    }

    private static int IdleNumber(string slotName, string idlePrefix)
        => int.TryParse(slotName[idlePrefix.Length..], out var number) ? number : int.MaxValue;

    private static string NormalizePath(string path) => path.Replace('\\', '/').Trim();
}
