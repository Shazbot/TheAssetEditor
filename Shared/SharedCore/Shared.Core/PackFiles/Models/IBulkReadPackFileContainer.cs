namespace Shared.Core.PackFiles.Models
{
    /// <summary>
    /// A container whose lookups are expensive per call (a database open per query) and that can
    /// hold one connection open for the duration of a bulk read. The scope must be disposed; until
    /// then the container's backing file stays open, so only long batch jobs should use it.
    /// </summary>
    public interface IBulkReadPackFileContainer
    {
        IDisposable BeginBulkRead();
    }
}
