using System.Windows;
using WindowHandling;

namespace Editors.KitbasherEditor.Services
{
    public partial class MissingTextureDecisionWindow : AssetEditorWindow
    {
        public MissingTextureDecisionWindow(string details)
        {
            InitializeComponent();

            DetailsTextBox.Text = details;
            MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height - 80);
            Height = Math.Min(680, MaxHeight);
        }

        private void AtlasAnywayButton_Click(object sender, RoutedEventArgs e)
            => DialogResult = true;

        private void SkipAffectedButton_Click(object sender, RoutedEventArgs e)
            => DialogResult = false;
    }
}
