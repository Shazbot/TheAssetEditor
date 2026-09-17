using Editors.ImportExport.Exporting.Exporters.RmvToGltf;

namespace Test.ImportExport.Exporting;

public sealed class ExporterCoreDecisionTests
{
    [Test]
    public void HeadlessDecisionUsesTheDeterministicContinuePolicy()
    {
        var decision = new HeadlessMissingSkeletonDecision();

        Assert.That(
            decision.Decide(new MissingSkeletonContext("missing_skeleton", "not found")),
            Is.EqualTo(MissingSkeletonAction.ContinueWithoutSkeleton));
        Assert.That(decision.ContinueWithoutSkeleton("missing_skeleton"), Is.True);
    }

    [Test]
    public void LegacyBoolDecisionImplementationsRemainUsableThroughNeutralContract()
    {
        IMissingSkeletonDecision decision = new LegacyDecision();

        Assert.That(
            decision.Decide(new MissingSkeletonContext("missing_skeleton")),
            Is.EqualTo(MissingSkeletonAction.CancelExport));
    }

    private sealed class LegacyDecision : IMissingSkeletonDecision
    {
        public bool ContinueWithoutSkeleton(string skeletonName) => false;
    }
}
