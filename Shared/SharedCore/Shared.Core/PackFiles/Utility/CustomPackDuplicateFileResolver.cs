using System.Windows;

namespace Shared.Core.PackFiles.Utility
{
    public class CustomPackDuplicateFileResolver : IDuplicateFileResolver
    {
        public bool CheckForDuplicates => true;

        public bool KeepDuplicateFile(string fileName)
        {
            var result = MessageBox.Show(
                $"Multiple files with the name '{fileName}' found.\n Yes = Rename and keep.\nNo = Skip",
                "DuplicateFile",
                MessageBoxButton.YesNo);
            return result == MessageBoxResult.Yes;
        }
    }
}
