namespace Editors.PackOptimizer.Core
{
    /// <summary>
    /// Shared texture repack saving gates. The defaults are the production values and must not
    /// change without bumping the optimizer output version. <see cref="Current"/> exists so the
    /// bench tool can run threshold sweeps; it is not part of the host request surface.
    /// </summary>
    internal sealed record SharedRepackThresholdSettings(
        long MinimumSetSavingBytes = PackTextureAtlasBatchService.SharedRepackMinimumSavingBytes,
        long MinimumGroupSavingBytes = PackTextureAtlasBatchService.SharedRepackMinimumGroupSavingBytes)
    {
        public static SharedRepackThresholdSettings Default { get; } = new();

        public static SharedRepackThresholdSettings Current { get; set; } = Default;
    }
}
