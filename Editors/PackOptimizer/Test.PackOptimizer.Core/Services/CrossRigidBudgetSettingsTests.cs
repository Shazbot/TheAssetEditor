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
            Assert.That(defaults.GeneratedGeometryBytesPerExpectedArmyDraw, Is.EqualTo(4L * 1024 * 1024));
            Assert.That(defaults.GeneratedGeometryGrowthBytes, Is.EqualTo(256L * 1024 * 1024));
            Assert.That(defaults.MinimumSingleVmdExpectedArmyDraw, Is.EqualTo(0.02));
            Assert.That(defaults.PerPayloadCostCeilingMultiplier, Is.EqualTo(1.0));
            Assert.That(defaults.MaxJointStateCombinations, Is.EqualTo(4096));
            Assert.That(defaults.MaxExpectedDrawCallIncrease, Is.EqualTo(0));
            Assert.That(CrossRigidBudgetSettings.Current, Is.EqualTo(defaults));
        }

        [TestCase(0.0)]
        [TestCase(5.0)]
        public void Validate_AcceptsNonnegativeFiniteDrawCallLimits(double limit)
        {
            (CrossRigidBudgetSettings.Default with
            {
                MaxExpectedDrawCallIncrease = limit,
            }).Validate();
        }

        [Test]
        public void Validate_AcceptsPositiveInfinityToDisableDrawCallGate()
        {
            (CrossRigidBudgetSettings.Default with
            {
                MaxExpectedDrawCallIncrease = double.PositiveInfinity,
            }).Validate();
        }

        [TestCase(double.NaN)]
        [TestCase(double.NegativeInfinity)]
        [TestCase(-1.0)]
        public void Validate_RejectsInvalidDrawCallLimits(double limit)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                (CrossRigidBudgetSettings.Default with
                {
                    MaxExpectedDrawCallIncrease = limit,
                }).Validate());
        }
    }
}
