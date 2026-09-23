using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using WindowHandling;

namespace Editors.KitbasherEditor.Services
{
    public partial class TextureAtlasProgressWindow : AssetEditorWindow
    {
        private readonly Action<
            CancellationToken,
            IProgress<PackTextureAtlasBatchService.TextureAtlasPackProgress>> _worker;
        private readonly CancellationTokenSource _cancellation = new();

        private bool _started;
        private bool _allowClose;

        public Exception? Failure { get; private set; }
        public bool WasCancelled { get; private set; }

        public TextureAtlasProgressWindow(
            Action<
                CancellationToken,
                IProgress<PackTextureAtlasBatchService.TextureAtlasPackProgress>> worker)
        {
            InitializeComponent();
            _worker = worker;
        }

        private void Window_OnContentRendered(object? sender, EventArgs e)
        {
            if (_started)
                return;

            _started = true;
            var progress = new PumpingProgress(this);

            try
            {
                _worker(_cancellation.Token, progress);
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
                _cancellation.Dispose();
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
            => RequestCancellation();

        private void Window_OnClosing(object? sender, CancelEventArgs e)
        {
            if (_allowClose)
                return;

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
                new DispatcherOperationCallback(_ =>
                {
                    frame.Continue = false;
                    return null;
                }),
                null);
            Dispatcher.PushFrame(frame);
        }

        private sealed class PumpingProgress : IProgress<PackTextureAtlasBatchService.TextureAtlasPackProgress>
        {
            private readonly TextureAtlasProgressWindow _window;

            public PumpingProgress(TextureAtlasProgressWindow window)
            {
                _window = window;
            }

            public void Report(PackTextureAtlasBatchService.TextureAtlasPackProgress value)
                => _window.UpdateProgress(value);
        }
    }
}
