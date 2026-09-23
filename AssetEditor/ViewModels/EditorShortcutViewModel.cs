using CommunityToolkit.Mvvm.Input;
using Shared.Core.Commands;
using Shared.Core.Events;
using Shared.Core.ToolCreation;

namespace AssetEditor.ViewModels
{
    public partial class EditorShortcutViewModel
    {
        private readonly IUiCommandFactory? _uiCommandFactory;
        private readonly EditorEnums _editor;
        private readonly Action? _customAction;

        public string DisplayName { get; set; }
        public bool IsEnabled{ get; set; }

        public EditorShortcutViewModel(EditorInfo editorInfo, IUiCommandFactory commandFactory)
        {
            _uiCommandFactory = commandFactory;
            _editor = editorInfo.EditorEnum;
            DisplayName = editorInfo.ToolbarName;
            IsEnabled = editorInfo.IsToolbarButtonEnabled;
        }

        public EditorShortcutViewModel(string displayName, Action action, bool isEnabled = true)
        {
            DisplayName = displayName;
            _customAction = action;
            IsEnabled = isEnabled;
        }

        [RelayCommand]
        private void OpenEditor()
        {
            if (_customAction != null)
            {
                _customAction();
                return;
            }

            _uiCommandFactory!.Create<OpenEditorCommand>().Execute(_editor);
        }
    }
}
