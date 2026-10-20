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
- **The host references** `Editors.ImportExport.Core`, `GameWorld.AssetCore` and `Shared.PackFiles.Core`, and uses managed BCnEncoder.Net. The optimizer encodes with native DirectXTex through `PngToDdsImporter` and `TextureAtlasBuilder`, both in `Editors.ImportExport.Dds` (not `.Core`).
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
- **References:** `Shared.PackFiles.Core`, `Shared.GameFormats`, `Editors.ImportExport.Core`, `Editors.ImportExport.Dds` (native DirectXTexShim). No WPF, no `GameWorld.Core`, no `Shared.Ui`.
- **Public API:** `PackOptimizer.Run(PackOptimizerRequest, IProgress<Progress>, CancellationToken) → PackOptimizerResult` (status, report path, output path, summary counters).
  - `Process(...)`'s 15 boolean parameters become one options record.
  - The missing-texture prompt (`ApplyMissingTextureDecision`, `atlasMeshesWithMissingTextures`) becomes a policy enum on the request.
- **Inputs, explicitly:** the source container; the "vanilla" (read-only consumer) containers; the output path; the game. No global service lookups.
- **Output pack:** a standalone `PackFileContainer.CreatePackFile`, saved directly. Never registered in a global `PackFileService`. This removes the global FilesAdded/Removed events (review finding #8).
- **Stays in KitbasherEditor:** `Run()` (dialogs, file picker, game check), `TextureAtlasProgressWindow`, `MergeObjectsWithTextureAtlasCommand` / `ApplyTextureAtlasMergeCommand` (interactive, finding #5), and an adapter that builds the request from the window.
- **Tests:** move the optimizer test files (`PackTextureAtlasCrossRigidTests`, `PackTextureAtlasValueGateTests`, `Wh3UnitCategoryResolverTests`, `Wh3ArmyVisualContractTests`, `RigidGeometryOptimizerTests`, `DdsBcnResidencyEstimatorTests`) to `Test.PackOptimizer.Core`. They call private members by reflection, so keep the type and member names.
- **Done when:** the editor works through the adapter; tests pass at their known counts; the bench, pointed at the library, gives the identical fingerprint.

### 3. Host process mode (done)
- Run as a separate process, `WH3AssetHost.exe optimize --request <file.json> [--parent-pid <pid>]`, not inside the pipe server loop, so previews keep working and memory and crashes stay isolated.
- **Request JSON** (`schemaVersion` 1): `gameDirectory` (the `data` folder with `manifest.txt`; the install folder is accepted), `vanillaFileIndexPath` (whmm's `vanilla-pack-files-cache.bin`), `sourcePackPath`, `outputPackPath`, `reportPath`, optional `cacheDirectory`, `options` (nullable bools; omitted ones keep the optimizer defaults), `consumerPackPaths` and `sourceLoadOrderIndex` (step 4).
- **Output:** stdout is JSON lines only. Progress lines `{"type":"progress",stage,item,current,total}` (throttled to about 10/s), then one `{"type":"result",status,outputPackPath,reportPath,vmdCount,atlasedMeshCount,generatedTextureCount,removedFileCount,skippedMeshCount,elapsedSeconds,warnings,consumerPackCount,externallyUsedFileCount,frozenFileCount}` or `{"type":"error",message,exceptionType}`.
- **Exit codes:** 0 succeeded, 1 failed, 2 invalid request, 3 cancelled. Cancel by Ctrl+C, SIGTERM or parent exit (`--parent-pid`). Output and report go to `<target>.optimizing` and are moved into place only on success.
- **Vanilla containers** come from whmm's file index through `HeadlessPackFileLoader.TryLoadFromVanillaCache`, one read-only container per pack, ordered like the editor (load order, then name). A missing or stale entry fails with an error naming the pack; whmm warms the index first. The source pack is loaded with a non-interactive duplicate resolver.
- **Encoding:** a small native wrapper, `Native/DirectXTexShim` (plain C exports over DirectXTex `mar2024`, pinned in `directxtex.pin`, static CRT, no OpenMP), called through P/Invoke. Output is byte-identical to the managed wrapper it replaced. The prebuilt `DirectXTexShim.dll` (351 KB) is committed; `build.ps1` rebuilds it.
- **Publish:** single-file, trimmed exe (about 39 MB, was 34 MB); the shim DLL is bundled with `IncludeNativeLibrariesForSelfExtract`. `optimize --check-dds` verifies the native library loads.
- **Done:** `optimize` on `!ak_teb3` matches the bench fingerprint (only the harmless "Gameplay container snapshot" count line differs), and `SmokeTest.ps1` has an optimize case.

### 4. Other mods as consumers (done)
- **Request:** `consumerPackPaths` lists the other enabled mods in load order: the first loads first and loses, the last wins (the mod list top to bottom). `sourceLoadOrderIndex` says how many of them load before the source; it is required whenever the list is not empty (exit 2 if missing or out of range). The library takes `ModConsumers` and `SourceLoadOrderIndex` (default: the source wins over all of them).
- **Loading:** read-only, from the pack index only, like the source. Exit 2 for the source itself, the output pack, a pack named like the optimizer's output (`<source>_atlas...`) and duplicates. A consumer that overrides more than half of its asset paths gets a warning on stderr, in the report and in the result line's `warnings`.
- **Two sets, computed once** (`ConsumerReferenceIndex`):
  - **E, externally used:** source files reachable from a consumer file. Seeds are paths named in consumer VMDs, WSModels, materials and text files, the embedded textures of consumer rigids a consumer VMD draws directly, and source files that become gameplay roots only through consumer DB rows (the unit category resolution runs with the consumers' tables, once with and once without them). E is then closed inside the source (VMD to WSModel to rigid, material, texture).
  - **F, frozen:** source asset paths a consumer also has, in either load order.
- **Rules:** nothing in E or F is pruned or changed in place. Texture repack, compaction, downscale and atlasing skip those textures; a rigid that E or F (or a WSModel bound to it) touches gets no merge, repack UV remap or geometry change; cross-rigid skips VMDs in E or F. Source analysis still resolves source first, then the game packs; consumers are not in the pack file service and not in the vanilla consumer cache.
- **Unreadable DB tables** of a consumer never make the gameplay resolution unhealthy: the report lists them, and the table is read as text for paths (the report shows how many source paths that alone added).
- **Report:** one section, only when consumers are supplied: the packs, "kept because another mod uses it" (file, pack, referrer, what was blocked), "frozen because another mod overrides it", the blocked operations by stage, DB roots, integrity.
- **Integrity check** in `ValidateOutput`: every path in E and F must be byte-identical to the source in the output, or the run fails. The named exceptions list is empty.
- **Left as is:** geometry optimisation stays blocked for consumer-used rigids, like for vanilla users (it keeps mesh count and order, so a later split of that gate is possible). Vanilla-user indexing does not resolve through consumers first.
- **Done:** unit tests for each block, the DB-root and DB-fallback paths and the integrity check; an empty list gives the identical fingerprint (bench, bench with atlases); real packs from the workshop folder run cleanly (see the step 4 report).

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
- **Native shim** (step 3): a Windows x64 binary committed to the repo, with a pinned DirectXTex. Mitigation: `directxtex.pin`, `build.ps1`, and the parity harness against the old output; bump the pin only with a fresh parity run.
- **Consumer packs** (step 4) can block more optimisations than expected. Mitigation: every block is reported with its reason, pack and referrer. A stale derived pack of the same mod in the load order would freeze almost everything; the host rejects packs named like its outputs and warns on heavy overlap, and whmm must leave derived packs out.
- **Editor leak** (follow-up): the Asset Editor passes all open containers as `ConsumerContainers`, so open non-CA packs are visible to generic reads. Left unchanged; the editor passes no `ModConsumers`.
- **Cache directory:** whmm passes its own `cacheDirectory` (never the editor's). The host's vanilla containers have no persistent identity, so the vanilla consumer cache is in-memory only there, and a volatile-only run no longer prunes or rewrites the editor's file.
- **Reflection-based tests** (step 2) break on renames. Mitigation: move files without renaming types or members.
