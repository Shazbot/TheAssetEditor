namespace Shared.Core.PackFiles.Models;

/// <summary>
/// Describes a file that should be added below a pack-relative directory.
/// The contract is shared by the headless pack model and the editor service.
/// </summary>
public record NewPackFileEntry(string DirectoyPath, PackFile PackFile);
