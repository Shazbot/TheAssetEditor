using Editors.PackOptimizer.Core;

namespace Test.PackOptimizer.Core.Services
{
    public class CrossRigidBudgetSettingsTests
    {
        [Test]
        public void Defaults_MatchProductionBudgets()
        {
            var defaults = CrossRigidBudgetSettings.Default;

            Assert.That(defaults.ExpectedResidentGeometryBytesPerExpectedArmyDraw, Is.EqualTo(512L * 1024));
            Assert.That(defaults.GeneratedGeometryBytesPerExpectedArmyDraw, Is.EqualTo(16L * 1024 * 1024));
            Assert.That(defaults.GeneratedGeometryGrowthBytes, Is.EqualTo(256L * 1024 * 1024));
            Assert.That(defaults.MinimumSingleVmdExpectedArmyDraw, Is.EqualTo(0.02));
            Assert.That(defaults.PerPayloadCostCeilingMultiplier, Is.EqualTo(1.0));
            Assert.That(defaults.MaxJointStateCombinations, Is.EqualTo(4096));
            Assert.That(CrossRigidBudgetSettings.Current, Is.EqualTo(defaults));
        }
    }
}
