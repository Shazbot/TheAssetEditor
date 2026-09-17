using Shared.Core.PackFiles.Models;

namespace Shared.Core.PackFiles.Utility;

/// <summary>
/// Composes a headless pack service from already loaded containers. It never
/// constructs the editor-oriented PackFileService.
/// </summary>
public static class HeadlessPackFileServiceFactory
{
    public static Shared.Core.PackFiles.HeadlessPackFileService Create()
        => new();

    public static Shared.Core.PackFiles.HeadlessPackFileService Create(
        IEnumerable<IPackFileContainer> packs)
        => new(packs);
}
