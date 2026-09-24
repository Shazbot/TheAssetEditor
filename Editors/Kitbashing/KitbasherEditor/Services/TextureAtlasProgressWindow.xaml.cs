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
            : this((mergeCompatibleMeshes, _, _, cancellationToken, progress) =>
                worker(mergeCompatibleMeshes, cancellationToken, progress))
        {
        }

        public TextureAtlasProgressWindow(
            Action<
                bool,
                bool,
                CancellationToken,
                IProgress<PackTextureAtlasBatchService.TextureAtlasPackProgress>> worker)
            : this((mergeCompatibleMeshes, shareAcrossVmds, _, cancellationToken, progress) =>
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
        {
            InitializeComponent();
            _worker = worker;
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
            PhaseText.Text = "Preparing texture atlas pack...";
            ProgressBar.IsIndeterminate = true;

            var progress = new PumpingProgress(this);

            try
            {
                _worker(
                    MergeMeshesCheckBox.IsChecked == true,
                    ShareAcrossVmdsCheckBox.IsChecked == true,
                    OptimizeGeometryCheckBox.IsChecked == true,
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
