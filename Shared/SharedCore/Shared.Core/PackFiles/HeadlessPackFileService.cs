using Shared.Core.PackFiles.Models;

namespace Shared.Core.PackFiles;

/// <summary>
/// Read-only pack service for command-line and other non-editor callers.
/// It is intentionally composed from explicit containers and the shared
/// repository, so constructing it never creates editor services or dialogs.
/// </summary>
public sealed class HeadlessPackFileService : IHeadlessPackFileService
{
    private readonly PackFileRepository _repository;

    public HeadlessPackFileService(IEnumerable<IPackFileContainer>? packs = null)
    {
        _repository = new PackFileRepository(packs);
    }

    public IReadOnlyList<IPackFileContainer> Packs => _repository.Packs;

    public PackFile? FindFile(string path)
        => _repository.FindFile(path);

    /// <summary>
    /// Adds a container at the end of the precedence order. This method is
    /// retained for callers that build a service incrementally; loaded packs
    /// are otherwise best supplied through the constructor/factory.
    /// </summary>
    public IPackFileContainer? AddContainer(IPackFileContainer container, bool setToMainPackIfFirst = false)
    {
        _ = setToMainPackIfFirst;
        return _repository.TryAdd(container) ? container : null;
    }

    public List<IPackFileContainer> GetAllPackfileContainers()
        => _repository.Packs.ToList();

    public bool IsPackFileLoaded(string packFilePath)
        => _repository.IsPackFileLoaded(packFilePath);

    public PackFile? FindFile(string path, IPackFileContainer? container = null)
        => _repository.FindFile(path, container);

    public string GetFullPath(PackFile file, IPackFileContainer? container = null)
    {
        if (_repository.TryGetFullPath(file, container, out var path))
            return path!;

        throw new InvalidOperationException($"Unknown path for {file.Name}");
    }

    public IPackFileContainer? GetPackFileContainer(PackFile file)
        => _repository.GetPackFileContainer(file);

    public List<(string FileName, PackFile Pack)> FindAllWithExtention(
        string extention,
        IPackFileContainer? container = null)
        => _repository.FindAllWithExtention(extention, container);
}
