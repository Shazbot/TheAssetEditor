using System.Windows;

namespace Editors.ImportExport.Exporting.Exporters.RmvToGltf;

/// <summary>
/// WPF editor adapter for the neutral missing-skeleton decision contract in
/// the exporter core.
/// </summary>
public sealed class DialogMissingSkeletonDecision : IMissingSkeletonDecision
{
    public MissingSkeletonAction Decide(MissingSkeletonContext context)
        => ContinueWithoutSkeleton(context.SkeletonName)
            ? MissingSkeletonAction.ContinueWithoutSkeleton
            : MissingSkeletonAction.CancelExport;

    public bool ContinueWithoutSkeleton(string skeletonName)
        => MessageBox.Show(
            "Skeleton file not found, \n(Have you loaded all CA pakcs for the right game?)\n Do you want to continue exporting without skeleton/animations?",
            "Warning!",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
}
