namespace Editors.ImportExport.Exporting.Exporters.RmvToGltf;

public enum ExportExecutionStatus
{
    Completed,
    Cancelled
}

public sealed record ExportTextureSource(
    string SourceVirtualPath,
    string GeneratedFileName,
    string Channel);

public readonly record struct ExportExecutionResult(
    ExportExecutionStatus Status,
    IReadOnlyList<ExportTextureSource> TextureSources)
{
    public static ExportExecutionResult Completed(IEnumerable<ExportTextureSource>? textureSources = null)
        => new(
            ExportExecutionStatus.Completed,
            textureSources?.Distinct().ToArray() ?? Array.Empty<ExportTextureSource>());

    public static ExportExecutionResult Cancelled()
        => new(ExportExecutionStatus.Cancelled, Array.Empty<ExportTextureSource>());
}
