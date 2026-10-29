namespace Editors.PackOptimizer.Core
{
    /// <summary>
    /// Cross-rigid value-gate budgets. The defaults are the production values and must not change
    /// without bumping the optimizer output version. <see cref="Current"/> exists so the bench tool
    /// can run budget sweeps; it is not part of the host request surface.
    /// </summary>
    internal sealed record CrossRigidBudgetSettings(
        long ExpectedResidentGeometryBytesPerExpectedArmyDraw = 512L * 1024,
        long GeneratedGeometryBytesPerExpectedArmyDraw = 16L * 1024 * 1024,
        long GeneratedGeometryGrowthBytes = 256L * 1024 * 1024,
        double MinimumSingleVmdExpectedArmyDraw = 0.02,
        double PerPayloadCostCeilingMultiplier = 1.0)
    {
        public static CrossRigidBudgetSettings Default { get; } = new();

        public static CrossRigidBudgetSettings Current { get; set; } = Default;
    }
}
