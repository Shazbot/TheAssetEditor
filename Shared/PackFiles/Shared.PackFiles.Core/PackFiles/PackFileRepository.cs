using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Models.Containers;

namespace Shared.Core.PackFiles;

/// <summary>
/// UI-independent storage and lookup logic for an ordered set of pack
/// containers. The repository owns no editor state and performs no I/O.
/// </summary>
public sealed class PackFileRepository
{
    private readonly List<IPackFileContainer> _packs = [];
    private readonly HashSet<IPackFileContainer> _loadedPackFileContainers = new(ReferenceEqualityComparer.Instance);

    public PackFileRepository(IEnumerable<IPackFileContainer>? packs = null)
    {
        if (packs == null)
            return;

        foreach (var pack in packs)
        {
            if (TryAdd(pack) == false)
                throw new InvalidOperationException($"Pack file '{pack.Name}' was supplied more than once.");
        }
    }

    public IReadOnlyList<IPackFileContainer> Packs => _packs;

    public bool TryAdd(IPackFileContainer pack)
    {
        ArgumentNullException.ThrowIfNull(pack);

        if (_loadedPackFileContainers.Contains(pack))
            return false;

        if (ContainsSystemPath(pack.SystemFilePath))
            return false;

        _packs.Add(pack);
        _loadedPackFileContainers.Add(pack);
        return true;
    }

    public bool Remove(IPackFileContainer pack)
    {
        var removed = _packs.Remove(pack);
        if (removed)
            _loadedPackFileContainers.Remove(pack);
        return removed;
    }

    public bool IsPackFileLoaded(string packFilePath)
    {
        if (string.IsNullOrWhiteSpace(packFilePath))
            return false;

        var normalizedPath = NormalizeSystemPath(packFilePath);
        foreach (var container in _packs)
        {
            if (PathsEqual(container.SystemFilePath, normalizedPath))
                return true;

            if (container is IPackFileContainerWithSourcePaths sourceContainer
                && sourceContainer.SourcePackFilePaths.Any(path => PathsEqual(path, normalizedPath)))
                return true;
        }

        return false;
    }

    public bool ContainsSystemPath(string? systemFilePath)
    {
        if (string.IsNullOrWhiteSpace(systemFilePath))
            return false;

        var normalizedPath = NormalizeSystemPath(systemFilePath);
        return _packs.Any(pack => PathsEqual(pack.SystemFilePath, normalizedPath));
    }

    public PackFile? FindFile(string path, IPackFileContainer? container = null)
        => FindFileWithContainer(path, container)?.File;

    public (IPackFileContainer Container, PackFile File)? FindFileWithContainer(
        string path,
        IPackFileContainer? container = null)
    {
        if (container != null)
        {
            var file = container.FindFile(path);
            return file == null ? null : (container, file);
        }

        for (var index = _packs.Count - 1; index >= 0; index--)
        {
            var pack = _packs[index];
            var file = pack.FindFile(path);
            if (file != null)
                return (pack, file);
        }

        return null;
    }

    public bool TryGetFullPath(
        PackFile file,
        IPackFileContainer? container,
        out string? path)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (container != null)
        {
            if (ReferenceEquals(file.Container, container)
                && !string.IsNullOrWhiteSpace(file.VirtualPath))
            {
                path = file.VirtualPath;
                return true;
            }

            path = null;
            return false;
        }

        if (file.Container is IPackFileContainer owner
            && _loadedPackFileContainers.Contains(owner)
            && !string.IsNullOrWhiteSpace(file.VirtualPath))
        {
            path = file.VirtualPath;
            return true;
        }

        path = null;
        return false;
    }

    public IPackFileContainer? GetPackFileContainer(PackFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.Container is IPackFileContainer owner
            && _loadedPackFileContainers.Contains(owner)
            && !string.IsNullOrWhiteSpace(file.VirtualPath))
        {
            return owner;
        }

        return null;
    }

    public List<(string FileName, PackFile Pack)> FindAllWithExtention(
        string extention,
        IPackFileContainer? container = null)
    {
        ArgumentNullException.ThrowIfNull(extention);

        var output = new List<(string, PackFile)>();
        if (container != null)
        {
            AddFilesWithExtension(output, container, extention);
            return output;
        }

        foreach (var pack in _packs)
            AddFilesWithExtension(output, pack, extention);
        return output;
    }

    private static void AddFilesWithExtension(
        List<(string FileName, PackFile Pack)> output,
        IPackFileContainer container,
        string extention)
    {
        if (container is IPackFileContainerInternal internalContainer)
        {
            output.AddRange(internalContainer.FindAllWithExtention(extention));
            return;
        }

        var normalizedExtension = extention.ToLower();
        foreach (var (path, file) in container.GetAllFiles())
        {
            if (Path.GetExtension(path) == normalizedExtension)
                output.Add((path, file));
        }
    }

    private static string NormalizeSystemPath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant();
        }
        catch
        {
            return path.Replace('/', '\\').TrimEnd('\\').Trim().ToLowerInvariant();
        }
    }

    private static bool PathsEqual(string? path, string normalizedPath)
    {
        return string.IsNullOrWhiteSpace(path) == false
            && NormalizeSystemPath(path) == normalizedPath;
    }
}
