using Shared.Core.PackFiles.Models;

namespace Shared.Core.PackFiles;

/// <summary>
/// Resolves a virtual path using the loaded pack precedence order.
/// </summary>
public interface IPackedFileLookup
{
    PackFile? FindFile(string path, IPackFileContainer? container = null);
}

/// <summary>
/// Exposes the loaded packs in their explicit load order. Later packs have
/// higher lookup precedence.
/// </summary>
public interface IPackCollection
{
    IReadOnlyList<IPackFileContainer> Packs { get; }
    List<IPackFileContainer> GetAllPackfileContainers();
}

/// <summary>
/// Resolves a packed file back to its virtual path and owning container.
/// </summary>
public interface IPackFileLocationLookup
{
    string GetFullPath(PackFile file, IPackFileContainer? container = null);
    IPackFileContainer? GetPackFileContainer(PackFile file);
}

/// <summary>
/// Enumerates files in loaded packs without exposing editor mutation state.
/// </summary>
public interface IPackFileExtensionLookup
{
    List<(string FileName, PackFile Pack)> FindAllWithExtention(
        string extention,
        IPackFileContainer? container = null);
}

/// <summary>
/// The complete set of read-only capabilities needed by host-side pack
/// consumers. Editor services can implement this alongside their richer API.
/// </summary>
public interface IHeadlessPackFileService :
    IPackedFileLookup,
    IPackCollection,
    IPackFileLocationLookup,
    IPackFileExtensionLookup
{
}
