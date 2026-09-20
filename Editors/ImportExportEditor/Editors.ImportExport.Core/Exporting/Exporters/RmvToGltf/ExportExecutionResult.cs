namespace Editors.ImportExport.Exporting.Exporters.RmvToGltf;

public enum ExportExecutionStatus
{
    Completed,
    Cancelled
}

public readonly record struct ExportExecutionResult(ExportExecutionStatus Status)
{
    public static ExportExecutionResult Completed()
        => new(ExportExecutionStatus.Completed);

    public static ExportExecutionResult Cancelled()
        => new(ExportExecutionStatus.Cancelled);
}
