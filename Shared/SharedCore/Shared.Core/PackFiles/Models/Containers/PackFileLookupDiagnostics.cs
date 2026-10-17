namespace Shared.Core.PackFiles.Models.Containers
{
    /// <summary>Counters for the cached game-pack container's per-path lookup cache (for benchmarks).</summary>
    public static class PackFileLookupDiagnostics
    {
        private static long _lookups;
        private static long _hits;

        public static long Lookups => Interlocked.Read(ref _lookups);
        public static long Hits => Interlocked.Read(ref _hits);

        internal static void Record(bool hit)
        {
            Interlocked.Increment(ref _lookups);
            if (hit)
                Interlocked.Increment(ref _hits);
        }
    }
}
