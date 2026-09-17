# Pack-service split inventory

The host path uses the following pack capabilities:

| Caller | Required capability |
| --- | --- |
| `HeadlessExportRuntime` | Lookup an asset or animation by virtual path |
| `ModelAssetResolver` | Virtual-path lookup and source-file location lookup for sibling WSModels and materials |
| `VariantMeshCompositionResolver` | Virtual-path lookup and source-file location lookup for cycle diagnostics |
| `SkeletonAnimationLookUpHelper` | Pack enumeration, `.anim` enumeration, virtual-path lookup, file location, and owning-container lookup |
| DDS exporters and `GltfTextureHandler` | Virtual-path lookup |

The host no longer requires these editor-only members: pack mutation and save
operations, editable-pack/document state, settings integration, UI events and
notifications, dialogs, and editor lifecycle behavior. `PackFileRepository`
contains the shared ordered lookup, path, container, and extension logic;
`PackFileService` and `HeadlessPackFileService` both delegate to it.
