# Pack optimizer in WH3AssetHost: extraction plan

## Context

The batch pack optimizer (`PackTextureAtlasBatchService`, branch `feature/texture-atlas-consolidation`) runs today only inside the Asset Editor (WPF, KitbasherEditor). The WH3 mod manager (`/mnt/k/projects/whmm`, Electron/TypeScript) should offer it as an option. whmm already ships and spawns `WH3AssetHost.exe` (headless .NET 10, single-file, trimmed), so the optimizer goes there as a library plus a separate process mode, and reuses the caches whmm already maintains for the host.

Base: **`feature/wh3-asset-host-unit-composition`**. It has extras over `feature/wh3-asset-host`: asset host composition bindings and VMD model-reference normalisation.

Goals:
- the same optimizer output from the Asset Editor and the host, for the same inputs;
- no UI dependency in the optimizer;
- the host stays responsive for previews while an optimisation runs;
- correct behaviour in a mod load order, not only against vanilla.

Not in scope: texture atlasing in whmm. It stays an Asset-Editor-only experimental option (net memory cost, about 1 % of draws on `!ak_teb3`).

## Facts this plan relies on

- **Editor coupling of the service is small.** All of it is in `PackTextureAtlasBatchService.cs`, `.Traversal.cs` and `.Validation.cs`:
  - `IStandardDialogs`: `ShowDialogBox` ×2, `ShowExceptionWindow`, `ShowSystemOpenFileDialog` (all in `Run()`).
  - `ApplicationSettingsService.CurrentSettings` (the game check).
  - `IPackFileService`: `CreateNewPackFileContainer`, `AddFilesToPack` ×3, `DeleteFiles`, `FindFile` ×4, `GetAllPackfileContainers` ×2, `SavePackContainer`, `UnloadPackContainer`.
  - `IPackFileContainerLoader.CreateFromPackFile`.
- **The host references** `Editors.ImportExport.Core`, `GameWorld.AssetCore` and `Shared.PackFiles.Core`, and uses managed BCnEncoder.Net. The optimizer needs native DirectXTexNet (Windows x64) through `PngToDdsImporter` and `TextureAtlasBuilder`, both in `Editors.ImportExport` (not `.Core`).
- **Both branches edit these 11 files:**
  - `CachedPackFileContainer.cs`, `PackFileService.cs`, `IPackFileService.cs`, `SystemFolderContainer.cs`;
  - `VariantMeshDefinition.cs`, `WsModelMaterialProvider.cs`;
  - `TextureHelper.cs` (+ tests);
  - `Editors.KitbasherEditor.csproj`, `Test.KitbashEditor.csproj`;
  - `PackFileServiceTest.cs`.
- **The host's pipe server is single-instance and serial** (`AssetHostPipeServer.cs`, one request at a time).
- **whmm caches:**
  - **Vanilla file index** (`vanillaPackFilesCache.ts`, format `WVFC` v4): per-pack header, dependency packs, and each file's name/size/offset/compression, with pack identity `size` + `lastChangedLocal`. The host already reads it (`VanillaPackFilesCacheReader` → `LazyVanillaPackFileContainer`).
  - **Vanilla DB cache** (`src/vanillaDbCache/`): full schema-resolved vanilla tables, about 1,520 of them, columnar. **There is no C# reader in the host.**
  - **The Visuals and Unit Viewer caches** don't cover optimizer inputs: paths only, or derived UI data.
- **Optimizer runtime on `!ak_teb3`** (warm, atlasing off): about 121 s, half of it cross-rigid analysis. Cold runs were about 170 s slower before the speed-ups; not re-measured since.
- **Bench and fingerprint:** `Tools/PackOptimizerBench` (commit `768ffdd8`) writes `fingerprint.txt`: every output file's SHA-256 plus the report without timing lines. The output is deterministic: identical across runs.

## Steps

### 1. Integration branch
- Create `feature/pack-optimizer-host` from `feature/wh3-asset-host-unit-composition` and merge `feature/texture-atlas-consolidation` into it. Merge, don't rebase: the optimizer branch has about 680 commits since the common base.
- Resolve the 11 overlapping files. Keep both sides' intent:
  - the host's PackFiles split and ownership changes;
  - the optimizer's `IBulkReadPackFileContainer` scope and path memo in `CachedPackFileContainer`;
  - strict-mode repair rejection and the shared `XmlSerializer` in `VariantMeshDefinition`;
  - the red/blue channel fix in `TextureHelper`, plus the host's PNG fallback for textures whose size isn't a multiple of 4.
- Wherever the host moved `Shared.Core.PackFiles.*` types into `Shared.PackFiles.Core`, point the optimizer at the new projects and namespaces.
- **Done when:**
  - the whole solution builds with `dotnet.exe build`;
  - every test project has its known failure count (Test.KitbashEditor 10, Test.ImportExport 4, Shared.CoreTest 8, all from `PathHelper` "TheAssetEditor");
  - the bench fingerprint on `!ak_teb3` is identical to the optimizer branch's baseline.

### 2. Library `Editors.PackOptimizer.Core` (net10.0-windows)
- **Move in:**
  - all `PackTextureAtlasBatchService.*.cs`, `RigidGeometryOptimizer.cs`, `Wh3ArmyVisualContract.cs`, `Wh3UnitCategoryResolver.cs`, `Wh3VanillaAtlasConsumerCache.cs`, `TextureAtlasMergeService`'s shared helpers;
  - `Wh3ShaderTextureChannels.json` and `Wh3UnitCategorySchema.json` as embedded resources;
  - `TextureAtlas/TextureAtlasBuilder.cs`, plus the DDS compression path of `PngToDdsImporter` (`CreateRawBgraMipChainWriter`, `CompressAndSaveCore`, the large-BC split, `DDSFormatHelper`). Move these into `Editors.ImportExport.Core` (or a new `Editors.ImportExport.Dds`) so the editor importer and the library share one implementation.
- **References:** `Shared.PackFiles.Core`, `Shared.GameFormats`, `Editors.ImportExport.Core`, DirectXTexNet. No WPF, no `GameWorld.Core`, no `Shared.Ui`.
- **Public API:** `PackOptimizer.Run(PackOptimizerRequest, IProgress<Progress>, CancellationToken) → PackOptimizerResult` (status, report path, output path, summary counters).
  - `Process(...)`'s 15 boolean parameters become one options record.
  - The missing-texture prompt (`ApplyMissingTextureDecision`, `atlasMeshesWithMissingTextures`) becomes a policy enum on the request.
- **Inputs, explicitly:** the source container; the "vanilla" (read-only consumer) containers; the output path; the game. No global service lookups.
- **Output pack:** a standalone `PackFileContainer.CreatePackFile`, saved directly. Never registered in a global `PackFileService`. This removes the global FilesAdded/Removed events (review finding #8).
- **Stays in KitbasherEditor:** `Run()` (dialogs, file picker, game check), `TextureAtlasProgressWindow`, `MergeObjectsWithTextureAtlasCommand` / `ApplyTextureAtlasMergeCommand` (interactive, finding #5), and an adapter that builds the request from the window.
- **Tests:** move the optimizer test files (`PackTextureAtlasCrossRigidTests`, `PackTextureAtlasValueGateTests`, `Wh3UnitCategoryResolverTests`, `Wh3ArmyVisualContractTests`, `RigidGeometryOptimizerTests`, `DdsBcnResidencyEstimatorTests`) to `Test.PackOptimizer.Core`. They call private members by reflection, so keep the type and member names.
- **Done when:** the editor works through the adapter; tests pass at their known counts; the bench, pointed at the library, gives the identical fingerprint.

### 3. Host process mode
- Run it as a separate process, `WH3AssetHost.exe optimize --request <file.json>`, not inside the pipe server loop, so previews keep working and memory and crashes stay isolated.
- **Request JSON:** game dir; source pack; output path; options; whmm cache paths (vanilla file index, optional DB cache); the load order (step 4); report path.
- **Output:** one JSON progress line per update on stdout `{stage, item, current, total}`, a final result line, and the report file. Exit code 0 on success, non-zero on failure, with the error in the result line. Cancel by killing the process: the output is written to a temp path and moved into place only on success.
- **Vanilla containers** come from `VanillaPackFilesCacheReader` (whmm's file index). If the index is missing or stale, fail with a clear error; whmm warms it first.
- **Publish:** check that DirectXTexNet's native DLL works in the single-file trimmed build (`IncludeNativeLibrariesForSelfExtract`), add trimmer roots for the optimizer's reflection/serialisation, and record the exe size before and after. Keep DirectXTex: swapping to BCnEncoder.Net changes output bytes and quality.
- **Done when:** the host's `optimize` mode on `!ak_teb3` produces the same fingerprint as the editor and the bench, and `SmokeTest.ps1` gains an optimize case.

### 4. Other mods as consumers (new behaviour)
- Today pruning, vanilla-override protection, texture-sharing checks and the repack's "every user rewritten" rule consider only CA packs. In a load order, submods, compatibility patches and reskins can reference the optimised mod's files.
- Add `ConsumerPacks` to the request: the other enabled mods, read-only. Treat them like vanilla:
  - a file they reference is never pruned;
  - a texture they use is never retired or repacked away;
  - their WSModels and VMDs count as users in the value gates and the repack.
- The report lists files kept because another mod uses them.
- **Done when:** tests cover a consumer-pack reference that blocks pruning, the repack and compaction's alpha drop; and running with an empty `ConsumerPacks` gives the identical fingerprint.

### 5. Caches
- **Vanilla file lookups:** whmm's file index (step 3). The SQLite `CachedPackFileContainer` path isn't used in the host.
- **Vanilla DB tables:** decide between (a) a C# reader for whmm's `vanillaDbCache` format and (b) whmm exporting the 18 table types the resolver reads (listed in `Wh3UnitCategorySchema.json`) as part of the request. I prefer (b): it keeps one owner of the format. Whichever is chosen, check the columns field by field against `Wh3UnitCategorySchema.json`.
- **Derived indexes** (VMD→WSModel occurrence expansion, the gameplay mesh dependency index): first measure a cold run on the current code (Sysinternals RAMMap → Empty Standby List, then the bench). Add a disk cache only if the derived phases still cost more than about 60 s cold. Key it on whmm's per-pack identities (`size` + `lastChangedLocal`) plus the tool version, and store it in a whmm-provided directory.

### 6. whmm side
- **Option UI** (follow `docs/DESIGN.md`):
  - compaction, downscale, repack, cross-rigid merges, same-rigid merge, geometry optimisation;
  - no atlasing.
- **Job runner:** spawn `optimize`, show progress, allow cancel, and link the report.
- **Output:** a derived pack next to the original, never overwriting it, swapped into the load order in its place.
- **Re-run trigger:** the source pack hash, options, game version, tool version or the consumer-pack set changes.
- **Before spawning:** warm the vanilla file index (`ensureWh3AssetHostVanillaCache`).

### 7. Parity and verification
- **Fingerprint parity:** the editor (adapter), the bench and host `optimize` give the same fingerprint on `!ak_teb3` (atlasing off) and on `ovn_albion`.
- **Tests:** every test project at its known failure count; new tests for step 4.
- **Cold-run timing** recorded before and after step 5.
- **In game:** load the host-optimised `!ak_teb3` and check:
  - the repacked rigids (Gelt staff, golden lion);
  - the downscaled morion helmets;
  - alpha-tested units;
  - a unit from a submod in the load order.

## Risks

- **Merge conflicts in the pack-file layer** (step 1). Mitigation: the fingerprint, plus the PackFiles tests on both sides.
- **DirectXTexNet** in a trimmed single-file publish (step 3). Mitigation: try it first; the fallback is to ship the native DLL next to the exe.
- **Consumer packs** (step 4) are new logic and can block more optimisations than expected. Mitigation: report every block with its reason.
- **Reflection-based tests** (step 2) break on renames. Mitigation: move files without renaming types or members.
