using CommonControls.BaseDialogs.ErrorListDialog;
using Editors.KitbasherEditor.Commands;
using Editors.KitbasherEditor.Core.MenuBarViews;
using Editors.KitbasherEditor.Services;
using GameWorld.Core.Components.Selection;
using GameWorld.Core.SceneNodes;
using Shared.Core.Events;
using Shared.Ui.Common.MenuSystem;

namespace Editors.KitbasherEditor.UiCommands
{
    internal class MergeObjectsWithTextureAtlasCommand : ITransientKitbasherUiCommand
    {
        private readonly SelectionManager _selectionManager;
        private readonly TextureAtlasMergeService _textureAtlasMergeService;
        private readonly IUiCommandFactory _commandFactory;

        public string ToolTip { get; set; } = "Merge selected meshes and consolidate their used texture regions into a shared atlas";
        public ActionEnabledRule EnabledRule => ActionEnabledRule.TwoOrMoreObjectsSelected;
        public Hotkey? HotKey => null;

        public MergeObjectsWithTextureAtlasCommand(
            SelectionManager selectionManager,
            TextureAtlasMergeService textureAtlasMergeService,
            IUiCommandFactory commandFactory)
        {
            _selectionManager = selectionManager;
            _textureAtlasMergeService = textureAtlasMergeService;
            _commandFactory = commandFactory;
        }

        public void Execute()
        {
            if (_selectionManager.GetState() is not ObjectSelectionState selectionState)
                return;

            var selectedMeshes = selectionState.SelectedObjects()
                .OfType<Rmv2MeshNode>()
                .ToList();

            if (selectedMeshes.Count < 2)
                return;

            if (!_textureAtlasMergeService.TryPrepare(selectedMeshes, out var preparedMerge, out var errors))
            {
                ErrorListWindow.ShowDialog("Texture Atlas Merge", errors, false);
                return;
            }

            _commandFactory.CreateWithBuilder<ApplyTextureAtlasMergeCommand>()
                .Configure(x => x.Configure(preparedMerge!))
                .BuildAndExecute();
        }
    }
}
