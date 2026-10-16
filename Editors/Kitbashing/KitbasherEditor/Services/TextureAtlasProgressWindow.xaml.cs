using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using WindowHandling;

namespace Editors.KitbasherEditor.Services
{
    public partial class TextureAtlasProgressWindow : AssetEditorWindow
    {
        private readonly Action<
            bool,
            bool,
            bool,
            bool,
            bool,
            bool,
            bool,
            bool,
            bool,
            bool,
            bool,
            bool,
            CancellationToken,
            IProgress<PackTextureAtlasBatchService.TextureAtlasPackProgress>> _worker;
        private readonly CancellationTokenSource _cancellation = new();

        private bool _started;
        private bool _allowClose;
        private bool _disposed;

        public Exception? Failure { get; private set; }
        public bool WasCancelled { get; private set; }

        public TextureAtlasProgressWindow(
            Action<
                bool,
                CancellationToken,
                IProgress<PackTextureAtlasBatchService.TextureAtlasPackProgress>> worker)
            : this((mergeCompatibleMeshes, _, _, _, _, _, _, cancellationToken, progress) =>
                worker(mergeCompatibleMeshes, cancellationToken, progress))
        {
        }

        public TextureAtlasProgressWindow(
            Action<
                bool,
                bool,
                CancellationToken,
                IProgress<PackTextureAtlasBatchService.TextureAtlasPackProgress>> worker)
            : this((mergeCompatibleMeshes, shareAcrossVmds, _, _, _, _, _, cancellationToken, progress) =>
                worker(mergeCompatibleMeshes, shareAcrossVmds, cancellationToken, progress))
        {
        }

        public TextureAtlasProgressWindow(
            Action<
                bool,
                bool,
                bool,
                CancellationToken,
                IProgress<PackTextureAtlasBatchService.TextureAtlasPackProgress>> worker)
            : this((mergeCompatibleMeshes, shareAcrossVmds, optimizeGeometry, _, _, _, _, cancellationToken, progress) =>
                worker(
                    mergeCompatibleMeshes,
                    shareAcrossVmds,
                    optimizeGeometry,
                    cancellationToken,
                    progress))
        {
        }

        public TextureAtlasProgressWindow(
            Action<
                bool,
                bool,
                bool,
                bool,
                CancellationToken,
                IProgress<PackTextureAtlasBatchService.TextureAtlasPackProgress>> worker)
            : this((mergeCompatibleMeshes, shareAcrossVmds, optimizeGeometry, atlasAllVmds, _, _, _, cancellationToken, progress) =>
                worker(
                    mergeCompatibleMeshes,
                    shareAcrossVmds,
                    optimizeGeometry,
                    atlasAllVmds,
                    cancellationToken,
                    progress))
        {
        }

        public TextureAtlasProgressWindow(
            Action<
                bool,
                bool,
                bool,
                bool,
                bool,
                CancellationToken,
                IProgress<PackTextureAtlasBatchService.TextureAtlasPackProgress>> worker)
            : this((
                mergeCompatibleMeshes,
                shareAcrossVmds,
                optimizeGeometry,
                atlasAllVmds,
                scoreAllGameUnits,
                _,
                _,
                cancellationToken,
                progress) =>
                worker(
                    mergeCompatibleMeshes,
                    shareAcrossVmds,
                    optimizeGeometry,
                    atlasAllVmds,
                    scoreAllGameUnits,
                    cancellationToken,
                    progress))
        {
        }

        public TextureAtlasProgressWindow(
            Action<
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                CancellationToken,
                IProgress<PackTextureAtlasBatchService.TextureAtlasPackProgress>> worker)
            : this((
                mergeCompatibleMeshes,
                shareAcrossVmds,
                optimizeGeometry,
                atlasAllVmds,
                scoreAllGameUnits,
                crossRigidOnly,
                _,
                cancellationToken,
                progress) =>
                worker(
                    mergeCompatibleMeshes,
                    shareAcrossVmds,
                    optimizeGeometry,
                    atlasAllVmds,
                    scoreAllGameUnits,
                    crossRigidOnly,
                    cancellationToken,
                    progress))
        {
        }

        public TextureAtlasProgressWindow(
            Action<
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                CancellationToken,
                IProgress<PackTextureAtlasBatchService.TextureAtlasPackProgress>> worker)
            : this((
                mergeCompatibleMeshes,
                shareAcrossVmds,
                optimizeGeometry,
                atlasAllVmds,
                scoreAllGameUnits,
                crossRigidOnly,
                crossRigidMerges,
                _,
                cancellationToken,
                progress) =>
                worker(
                    mergeCompatibleMeshes,
                    shareAcrossVmds,
                    optimizeGeometry,
                    atlasAllVmds,
                    scoreAllGameUnits,
                    crossRigidOnly,
                    crossRigidMerges,
                    cancellationToken,
                    progress))
        {
        }

        public TextureAtlasProgressWindow(
            Action<
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                CancellationToken,
                IProgress<PackTextureAtlasBatchService.TextureAtlasPackProgress>> worker)
            : this((
                mergeCompatibleMeshes,
                shareAcrossVmds,
                optimizeGeometry,
                atlasAllVmds,
                scoreAllGameUnits,
                crossRigidOnly,
                crossRigidMerges,
                buildAtlases,
                _,
                cancellationToken,
                progress) =>
                worker(
                    mergeCompatibleMeshes,
                    shareAcrossVmds,
                    optimizeGeometry,
                    atlasAllVmds,
                    scoreAllGameUnits,
                    crossRigidOnly,
                    crossRigidMerges,
                    buildAtlases,
                    cancellationToken,
                    progress))
        {
        }

        public TextureAtlasProgressWindow(
            Action<
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                CancellationToken,
                IProgress<PackTextureAtlasBatchService.TextureAtlasPackProgress>> worker)
            : this((
                mergeCompatibleMeshes,
                shareAcrossVmds,
                optimizeGeometry,
                atlasAllVmds,
                scoreAllGameUnits,
                crossRigidOnly,
                crossRigidMerges,
                buildAtlases,
                downscaleTextures,
                _,
                cancellationToken,
                progress) =>
                worker(
                    mergeCompatibleMeshes,
                    shareAcrossVmds,
                    optimizeGeometry,
                    atlasAllVmds,
                    scoreAllGameUnits,
                    crossRigidOnly,
                    crossRigidMerges,
                    buildAtlases,
                    downscaleTextures,
                    cancellationToken,
                    progress))
        {
        }

        public TextureAtlasProgressWindow(
            Action<
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                CancellationToken,
                IProgress<PackTextureAtlasBatchService.TextureAtlasPackProgress>> worker)
            : this((
                mergeCompatibleMeshes,
                shareAcrossVmds,
                optimizeGeometry,
                atlasAllVmds,
                scoreAllGameUnits,
                crossRigidOnly,
                crossRigidMerges,
                buildAtlases,
                downscaleTextures,
                sharedTextureRepack,
                _,
                cancellationToken,
                progress) =>
                worker(
                    mergeCompatibleMeshes,
                    shareAcrossVmds,
                    optimizeGeometry,
                    atlasAllVmds,
                    scoreAllGameUnits,
                    crossRigidOnly,
                    crossRigidMerges,
                    buildAtlases,
                    downscaleTextures,
                    sharedTextureRepack,
                    cancellationToken,
                    progress))
        {
        }

        public TextureAtlasProgressWindow(
            Action<
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                CancellationToken,
                IProgress<PackTextureAtlasBatchService.TextureAtlasPackProgress>> worker)
            : this((
                mergeCompatibleMeshes,
                shareAcrossVmds,
                optimizeGeometry,
                atlasAllVmds,
                scoreAllGameUnits,
                crossRigidOnly,
                crossRigidMerges,
                buildAtlases,
                downscaleTextures,
                sharedTextureRepack,
                compactTextures,
                _,
                cancellationToken,
                progress) =>
                worker(
                    mergeCompatibleMeshes,
                    shareAcrossVmds,
                    optimizeGeometry,
                    atlasAllVmds,
                    scoreAllGameUnits,
                    crossRigidOnly,
                    crossRigidMerges,
                    buildAtlases,
                    downscaleTextures,
                    sharedTextureRepack,
                    compactTextures,
                    cancellationToken,
                    progress))
        {
        }

        public TextureAtlasProgressWindow(
            Action<
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                bool,
                CancellationToken,
                IProgress<PackTextureAtlasBatchService.TextureAtlasPackProgress>> worker)
        {
            InitializeComponent();
            _worker = worker;
            UpdateOperationOptionAvailability();
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_started)
                return;

            _started = true;
            StartButton.IsEnabled = false;
            MergeMeshesCheckBox.IsEnabled = false;
            ShareAcrossVmdsCheckBox.IsEnabled = false;
            OptimizeGeometryCheckBox.IsEnabled = false;
            AtlasAllVmdsCheckBox.IsEnabled = false;
            ScoreAllGameUnitsCheckBox.IsEnabled = false;
            CrossRigidOnlyCheckBox.IsEnabled = false;
            CrossRigidMergesCheckBox.IsEnabled = false;
            BuildAtlasesCheckBox.IsEnabled = false;
            DownscaleTexturesCheckBox.IsEnabled = false;
            SharedTextureRepackCheckBox.IsEnabled = false;
            CompactTexturesCheckBox.IsEnabled = false;
            DropUnreadAlphaCheckBox.IsEnabled = false;
            PhaseText.Text = CrossRigidOnlyCheckBox.IsChecked == true
                ? "Preparing cross-rigid-only pack..."
                : BuildAtlasesCheckBox.IsChecked == true
                    ? "Preparing texture atlas pack..."
                    : "Preparing optimized pack...";
            ProgressBar.IsIndeterminate = true;

            var progress = new PumpingProgress(this);

            try
            {
                _worker(
                    MergeMeshesCheckBox.IsChecked == true,
                    ShareAcrossVmdsCheckBox.IsChecked == true,
                    OptimizeGeometryCheckBox.IsChecked == true,
                    AtlasAllVmdsCheckBox.IsChecked == true,
                    ScoreAllGameUnitsCheckBox.IsChecked == true,
                    CrossRigidOnlyCheckBox.IsChecked == true,
                    CrossRigidMergesCheckBox.IsChecked == true,
                    BuildAtlasesCheckBox.IsChecked == true,
                    DownscaleTexturesCheckBox.IsChecked == true &&
                    CrossRigidOnlyCheckBox.IsChecked != true,
                    SharedTextureRepackCheckBox.IsChecked == true &&
                    CrossRigidMergesCheckBox.IsChecked != true &&
                    CrossRigidOnlyCheckBox.IsChecked != true,
                    CompactTexturesCheckBox.IsChecked == true &&
                    CrossRigidOnlyCheckBox.IsChecked != true,
                    DropUnreadAlphaCheckBox.IsChecked == true &&
                    CompactTexturesCheckBox.IsChecked == true &&
                    CrossRigidOnlyCheckBox.IsChecked != true,
                    _cancellation.Token,
                    progress);
                _allowClose = true;
                DialogResult = true;
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                WasCancelled = true;
                _allowClose = true;
                DialogResult = false;
            }
            catch (Exception ex)
            {
                Failure = ex;
                _allowClose = true;
                DialogResult = false;
            }
            finally
            {
                DisposeCancellation();
            }
        }

        private void CrossRigidOnlyCheckBox_OnChanged(
            object sender,
            RoutedEventArgs e)
        {
            UpdateOperationOptionAvailability();
        }

        private void CompactTexturesCheckBox_OnChanged(
            object sender,
            RoutedEventArgs e)
        {
            UpdateOperationOptionAvailability();
        }

        private void BuildAtlasesCheckBox_OnChanged(
            object sender,
            RoutedEventArgs e)
        {
            UpdateOperationOptionAvailability();
        }

        private void CrossRigidMergesCheckBox_OnChanged(
            object sender,
            RoutedEventArgs e)
        {
            UpdateOperationOptionAvailability();
        }

        private void UpdateOperationOptionAvailability()
        {
            if (MergeMeshesCheckBox == null ||
                ShareAcrossVmdsCheckBox == null ||
                OptimizeGeometryCheckBox == null ||
                CrossRigidOnlyCheckBox == null ||
                CrossRigidMergesCheckBox == null ||
                BuildAtlasesCheckBox == null ||
                DownscaleTexturesCheckBox == null ||
                SharedTextureRepackCheckBox == null ||
                CompactTexturesCheckBox == null ||
                DropUnreadAlphaCheckBox == null)
            {
                return;
            }

            var atlasOptionsEnabled =
                !_started &&
                CrossRigidOnlyCheckBox.IsChecked != true;
            BuildAtlasesCheckBox.IsEnabled = atlasOptionsEnabled;
            MergeMeshesCheckBox.IsEnabled = atlasOptionsEnabled;
            // Sharing only matters while atlases are built. The VMD population and
            // scoring options stay enabled: they also scope geometry optimization
            // and cross-rigid analysis, and weight their results.
            ShareAcrossVmdsCheckBox.IsEnabled =
                atlasOptionsEnabled &&
                BuildAtlasesCheckBox.IsChecked == true;
            OptimizeGeometryCheckBox.IsEnabled = atlasOptionsEnabled;
            // Cross-rigid-only mode always runs the cross-rigid stage.
            CrossRigidMergesCheckBox.IsEnabled = atlasOptionsEnabled;
            // Texture downscaling also works next to atlasing (it then only touches
            // textures the atlases did not replace); only cross-rigid-only mode,
            // which never writes textures, turns it off.
            DownscaleTexturesCheckBox.IsEnabled = atlasOptionsEnabled;
            // Exact compaction runs last on the output's bytes and works next to every other
            // option; only cross-rigid-only mode, which never writes textures, turns it off.
            CompactTexturesCheckBox.IsEnabled = atlasOptionsEnabled;
            // The alpha rule is a refinement of the compaction and only means something with it.
            DropUnreadAlphaCheckBox.IsEnabled =
                atlasOptionsEnabled &&
                CompactTexturesCheckBox.IsChecked == true;
            // The shared texture repack works next to atlasing (it runs after it and leaves the
            // rigids atlasing changed alone); it is not available together with the cross-rigid
            // writer, which rewrites UVs of rigids it cannot account for.
            SharedTextureRepackCheckBox.IsEnabled =
                atlasOptionsEnabled &&
                CrossRigidMergesCheckBox.IsChecked != true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_started)
            {
                WasCancelled = true;
                _allowClose = true;
                DisposeCancellation();
                DialogResult = false;
                return;
            }

            RequestCancellation();
        }

        private void Window_OnClosing(object? sender, CancelEventArgs e)
        {
            if (_allowClose)
                return;

            if (!_started)
            {
                WasCancelled = true;
                _allowClose = true;
                DisposeCancellation();
                return;
            }

            e.Cancel = true;
            RequestCancellation();
        }

        private void RequestCancellation()
        {
            if (_cancellation.IsCancellationRequested)
                return;

            _cancellation.Cancel();
            CancelButton.IsEnabled = false;
            PhaseText.Text = "Cancelling...";
            CountText.Text = string.Empty;
        }

        private void UpdateProgress(PackTextureAtlasBatchService.TextureAtlasPackProgress value)
        {
            PhaseText.Text = value.Phase;
            ItemText.Text = value.Item ?? string.Empty;

            if (value.Total > 0)
            {
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = Math.Clamp(
                    value.Current * 100.0 / value.Total,
                    0,
                    100);
                CountText.Text = $"{Math.Min(value.Current, value.Total)} / {value.Total}";
            }
            else
            {
                ProgressBar.IsIndeterminate = true;
                CountText.Text = value.Current > 0 ? value.Current.ToString() : string.Empty;
            }

            PumpDispatcher();
            _cancellation.Token.ThrowIfCancellationRequested();
        }

        private void PumpDispatcher()
        {
            var frame = new DispatcherFrame();
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        private void DisposeCancellation()
        {
            if (_disposed)
                return;

            _disposed = true;
            _cancellation.Dispose();
        }

        private sealed class PumpingProgress : IProgress<PackTextureAtlasBatchService.TextureAtlasPackProgress>
        {
            private const int MinimumUiUpdateIntervalMs = 75;

            private readonly TextureAtlasProgressWindow _window;
            private readonly Stopwatch _sinceLastUpdate = Stopwatch.StartNew();
            private string? _lastPhase;

            public PumpingProgress(TextureAtlasProgressWindow window)
            {
                _window = window;
            }

            public void Report(PackTextureAtlasBatchService.TextureAtlasPackProgress value)
            {
                var phaseChanged = !string.Equals(_lastPhase, value.Phase, StringComparison.Ordinal);
                var phaseCompleted = value.Total > 0 && value.Current >= value.Total;
                if (!phaseChanged &&
                    !phaseCompleted &&
                    _sinceLastUpdate.ElapsedMilliseconds < MinimumUiUpdateIntervalMs)
                {
                    return;
                }

                _lastPhase = value.Phase;
                _sinceLastUpdate.Restart();
                _window.UpdateProgress(value);
            }
        }
    }
}
