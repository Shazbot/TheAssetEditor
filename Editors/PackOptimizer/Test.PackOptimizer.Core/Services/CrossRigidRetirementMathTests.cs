using Editors.PackOptimizer.Core;

namespace Test.PackOptimizer.Core.Services
{
    public class CrossRigidRetirementMathTests
    {
        private const double Epsilon = 1e-6;

        [Test]
        public void Evaluate_FullCoverageIsRetired()
        {
            var (share, full, count) = CrossRigidRetirementMath.Evaluate(
                [(0.6, 0.6), (0.4, 0.4 + 1e-9)],
                Epsilon);

            Assert.That(full, Is.True);
            Assert.That(share, Is.EqualTo(1.0).Within(1e-6));
            Assert.That(count, Is.EqualTo(2));
        }

        [Test]
        public void Evaluate_PartialCoverageIsWeightedAndNotRetired()
        {
            // One occurrence half covered (0.5 of 1.0), one fully covered (0.5 of 0.5): 1.0 of 1.5.
            var (share, full, count) = CrossRigidRetirementMath.Evaluate(
                [(1.0, 0.5), (0.5, 0.9)],
                Epsilon);

            Assert.That(full, Is.False);
            Assert.That(share, Is.EqualTo(1.0 / 1.5).Within(1e-9));
            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void Evaluate_EmptyIsNotRetired()
        {
            var (share, full, _) = CrossRigidRetirementMath.Evaluate([], Epsilon);

            Assert.That(full, Is.False);
            Assert.That(share, Is.EqualTo(0));
        }

        [Test]
        public void IsUnitRetired_RequiresEveryReachingRootRetired()
        {
            var reaching = new HashSet<string> { "a.vmd", "b.vmd" };
            var retired = new HashSet<string> { "a.vmd" };

            Assert.That(
                CrossRigidRetirementMath.IsUnitRetired(["a.vmd", "other.vmd"], reaching, retired.Contains),
                Is.True);
            Assert.That(
                CrossRigidRetirementMath.IsUnitRetired(["a.vmd", "b.vmd"], reaching, retired.Contains),
                Is.False);
            Assert.That(
                CrossRigidRetirementMath.IsUnitRetired(["other.vmd"], reaching, retired.Contains),
                Is.False);
        }
    }
}
