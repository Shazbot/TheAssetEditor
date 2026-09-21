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

        private List<Rmv2MeshNode> _originalMeshes = [];
        private PreparedTextureAtlasMerge? _preparedMerge;
        private ISelectionState? _originalSelectionState;

        public string HintText => "Merge Objects With Texture Atlas";
        public bool IsMutation => true;

        public ApplyTextureAtlasMergeCommand(
            SelectionManager selectionManager,
            IPackFileService packFileService)
        {
            _selectionManager = selectionManager;
            _packFileService = packFileService;
        }

        public void Configure(
            IReadOnlyList<Rmv2MeshNode> originalMeshes,
            PreparedTextureAtlasMerge preparedMerge)
        {
            _originalMeshes = originalMeshes.ToList();
            _preparedMerge = preparedMerge;
        }

        public void Execute()
        {
            if (_preparedMerge == null)
                throw new InvalidOperationException("Texture atlas merge command was not configured.");

            _originalSelectionState ??= _selectionManager.GetStateCopy();

            var packEntries = _preparedMerge.GeneratedFiles
                .Select(x => new NewPackFileEntry(x.Directory, x.PackFile))
                .ToList();
            _packFileService.AddFilesToPack(_preparedMerge.TargetPack, packEntries);

            foreach (var mesh in _originalMeshes)
                mesh.Parent.RemoveObject(mesh);

            foreach (var mesh in _preparedMerge.CombinedMeshes)
                mesh.Parent.AddObject(mesh);

            if (_selectionManager.GetState() is ObjectSelectionState currentState)
            {
                currentState.Clear();
                currentState.ModifySelection(_preparedMerge.CombinedMeshes.Cast<ISelectable>(), false);
            }
        }

        public void Undo()
        {
            if (_preparedMerge == null || _originalSelectionState == null)
                return;

            foreach (var mesh in _preparedMerge.CombinedMeshes)
                mesh.Parent.RemoveObject(mesh);

            foreach (var mesh in _originalMeshes)
                mesh.Parent.AddObject(mesh);

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
