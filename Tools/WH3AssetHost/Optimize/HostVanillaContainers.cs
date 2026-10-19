using System.IO;
using System.Text;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Serialization;
using Shared.Core.PackFiles.Utility;

namespace WH3AssetHost.Optimize;

/// <summary>Raised for failures that are not a bug but a missing prerequisite (stale index, missing pack).</summary>
public sealed class OptimizePrerequisiteException(string message) : Exception(message);

/// <summary>
/// Builds the containers the optimizer reads from. Vanilla packs come only from whmm's
/// file index; a missing or stale entry is an error, never a reason to parse the pack.
/// </summary>
public static class HostVanillaContainers
{
    /// <summary>
    /// Pack names in manifest.txt order, with the same rule the editor uses: the first
    /// tab-separated field of a line that mentions ".pack". Packs missing on disk are skipped.
    /// </summary>
    public static IReadOnlyList<string> ReadManifestPackPaths(string dataFolder, Action<string>? warn = null)
    {
        var manifest = Path.Combine(dataFolder, "manifest.txt");
        if (!File.Exists(manifest))
            throw new OptimizePrerequisiteException($"manifest.txt was not found in '{dataFolder}'.");

        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(manifest))
        {
            var name = line.Split('\t')[0].Trim();
            if (!name.Contains(".pack", StringComparison.OrdinalIgnoreCase) || !seen.Add(name))
                continue;

            var path = Path.Combine(dataFolder, name);
            if (!File.Exists(path))
            {
                warn?.Invoke($"manifest.txt lists '{name}' but it is not in '{dataFolder}'; skipped.");
                continue;
            }

            paths.Add(path);
        }

        if (paths.Count == 0)
            throw new OptimizePrerequisiteException($"manifest.txt in '{dataFolder}' lists no pack files.");
        return paths;
    }

    /// <summary>
    /// One read-only container per vanilla pack, ordered the way the editor merges its game
    /// container: by the pack header's load order, then by pack name; later packs win.
    /// </summary>
    public static IReadOnlyList<IPackFileContainer> LoadVanilla(
        string dataFolder,
        string vanillaFileIndexPath,
        Action<string>? warn = null)
    {
        var packPaths = ReadManifestPackPaths(dataFolder, warn);
        var loader = new HeadlessPackFileLoader(vanillaFileIndexPath);
        var loaded = new List<(IPackFileContainer Container, int LoadOrder)>(packPaths.Count);
        foreach (var packPath in packPaths)
        {
            var container = loader.TryLoadFromVanillaCache(packPath, out var loadOrder, isCaPackFile: true);
            if (container == null)
            {
                throw new OptimizePrerequisiteException(
                    $"The vanilla file index '{vanillaFileIndexPath}' has no current entry for " +
                    $"'{Path.GetFileName(packPath)}' (missing, or the pack changed since it was indexed). " +
                    "Warm the index first.");
            }

            loaded.Add((container, loadOrder));
        }

        return OrderLikeTheEditor(loaded);
    }

    public static IReadOnlyList<IPackFileContainer> OrderLikeTheEditor(
        IEnumerable<(IPackFileContainer Container, int LoadOrder)> containers)
        => containers
            .GroupBy(item => item.LoadOrder)
            .OrderBy(group => group.Key)
            .SelectMany(group => group.OrderBy(item => item.Container.Name).Select(item => item.Container))
            .ToList();

    /// <summary>Loads the pack being optimized, read-only; duplicate paths are skipped, never prompted.</summary>
    public static IPackFileContainer LoadSource(string sourcePackPath)
    {
        var fullPath = Path.GetFullPath(sourcePackPath);
        using var stream = File.OpenRead(fullPath);
        using var reader = new BinaryReader(stream, Encoding.ASCII);
        var container = PackFileSerializerLoader.Load(
            fullPath,
            new FileInfo(fullPath).Length,
            reader,
            new SkipDuplicateFileResolver());
        container.Name = Path.GetFileNameWithoutExtension(fullPath);
        container.SystemFilePath = fullPath;
        container.IsReadOnly = true;
        return container;
    }

    private sealed class SkipDuplicateFileResolver : IDuplicateFileResolver
    {
        public bool CheckForDuplicates => true;
        public bool KeepDuplicateFile(string fileName) => false;
    }
}
