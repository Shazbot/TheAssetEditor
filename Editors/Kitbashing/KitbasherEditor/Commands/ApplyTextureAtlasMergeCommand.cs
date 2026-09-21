using Editors.KitbasherEditor.Services;
using GameWorld.Core.Components.Selection;
using GameWorld.Core.Rendering.Materials.Shaders;
using GameWorld.Core.SceneNodes;
using Microsoft.Xna.Framework;
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
        private List<OriginalMeshState>? _originalMeshStates;

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
            _originalMeshStates ??= _preparedMerge.Replacements
                .Select(x => new OriginalMeshState(
                    x.OriginalMesh,
                    x.OriginalMesh.Material.Clone(),
                    x.OriginalMesh.Geometry.VertexArray
                        .Select(v => v.TextureCoordinate)
                        .ToArray()))
                .ToList();

            var packEntries = _preparedMerge.GeneratedFiles
                .Select(x => new NewPackFileEntry(x.Directory, x.PackFile))
                .ToList();
            _packFileService.AddFilesToPack(_preparedMerge.TargetPack, packEntries);

            foreach (var replacement in _preparedMerge.Replacements)
                ApplyAtlasedState(replacement.OriginalMesh, replacement.AtlasedMesh);

            // Nodes stay in place, so selection, parentage, attachment state, and all other
            // scene-node identity remain unchanged.
        }

        public void Undo()
        {
            if (_preparedMerge == null || _originalSelectionState == null || _originalMeshStates == null)
                return;

            foreach (var state in _originalMeshStates)
            {
                state.Mesh.Material = state.Material.Clone();

                if (state.Mesh.Geometry.VertexArray.Length != state.TextureCoordinates.Length)
                    throw new InvalidOperationException($"Mesh '{state.Mesh.Name}' changed vertex count after texture-atlas creation.");

                for (var i = 0; i < state.TextureCoordinates.Length; i++)
                    state.Mesh.Geometry.VertexArray[i].TextureCoordinate = state.TextureCoordinates[i];

                state.Mesh.Geometry.RebuildVertexBuffer();
            }

            foreach (var generated in _preparedMerge.GeneratedFiles)
            {
                var storedFile = _preparedMerge.TargetPack.FindFile(generated.FullPath);
                if (storedFile != null)
                    _packFileService.DeleteFile(_preparedMerge.TargetPack, storedFile);
            }

            _selectionManager.SetState(_originalSelectionState);
        }

        private static void ApplyAtlasedState(Rmv2MeshNode target, Rmv2MeshNode prepared)
        {
            if (target.Geometry.VertexArray.Length != prepared.Geometry.VertexArray.Length)
                throw new InvalidOperationException($"Mesh '{target.Name}' changed vertex count while preparing its texture atlas.");

            target.Material = prepared.Material.Clone();

            for (var i = 0; i < target.Geometry.VertexArray.Length; i++)
            {
                target.Geometry.VertexArray[i].TextureCoordinate =
                    prepared.Geometry.VertexArray[i].TextureCoordinate;
            }

            target.Geometry.RebuildVertexBuffer();
        }

        private sealed record OriginalMeshState(
            Rmv2MeshNode Mesh,
            CapabilityMaterial Material,
            Vector2[] TextureCoordinates);
    }
}
