using Editors.KitbasherEditor.Services;
using GameWorld.Core.Components.Selection;
using GameWorld.Core.SceneNodes;
using Shared.Core.Events;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;

namespace Editors.KitbasherEditor.Commands
{
    internal class ApplyTextureAtlasMergeCommand : IAeUndoCommandCommand
    {
        private readonly SelectionManager _selectionManager;
        private readonly IPackFileService _packFileService;

        private PreparedTextureAtlasMerge? _preparedMerge;
        private ISelectionState? _originalSelectionState;

        public string HintText => "Create Texture Atlas";
        public bool IsMutation => true;

        public ApplyTextureAtlasMergeCommand(
            SelectionManager selectionManager,
            IPackFileService packFileService)
        {
            _selectionManager = selectionManager;
            _packFileService = packFileService;
        }

        public void Configure(PreparedTextureAtlasMerge preparedMerge)
        {
            _preparedMerge = preparedMerge;
        }

        public void Execute()
        {
            if (_preparedMerge == null)
                throw new InvalidOperationException("Texture atlas command was not configured.");

            _originalSelectionState ??= _selectionManager.GetStateCopy();

            var packEntries = _preparedMerge.GeneratedFiles
                .Select(x => new NewPackFileEntry(x.Directory, x.PackFile))
                .ToList();
            _packFileService.AddFilesToPack(_preparedMerge.TargetPack, packEntries);

            foreach (var replacement in _preparedMerge.Replacements)
            {
                var parent = replacement.OriginalMesh.Parent;
                parent.RemoveObject(replacement.OriginalMesh);
                parent.AddObject(replacement.AtlasedMesh);
            }

            if (_selectionManager.GetState() is ObjectSelectionState currentState)
            {
                currentState.Clear();
                currentState.ModifySelection(
                    _preparedMerge.Replacements
                        .Select(x => x.AtlasedMesh)
                        .Concat(_preparedMerge.UntouchedMeshes)
                        .Cast<ISelectable>(),
                    false);
            }
        }

        public void Undo()
        {
            if (_preparedMerge == null || _originalSelectionState == null)
                return;

            foreach (var replacement in _preparedMerge.Replacements)
            {
                var parent = replacement.AtlasedMesh.Parent;
                parent.RemoveObject(replacement.AtlasedMesh);
                parent.AddObject(replacement.OriginalMesh);
            }

            foreach (var generated in _preparedMerge.GeneratedFiles)
            {
                var storedFile = _preparedMerge.TargetPack.FindFile(generated.FullPath);
                if (storedFile != null)
                    _packFileService.DeleteFile(_preparedMerge.TargetPack, storedFile);
            }

            _selectionManager.SetState(_originalSelectionState);
        }
    }
}
