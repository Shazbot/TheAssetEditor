# Cross-rigid merge: findings (2026-10-11)

Branch `feature/texture-atlas-consolidation`. Test pack: `!ak_teb3.pack` (dump in `K:\wh3mods\albion\teb`). In-game test assets: `K:\wh3mods\albion\abtest\` (see its `README.txt`).

## Summary

- The engine cost that merging reduces is **mesh parts per soldier**. Draw-call count and the number of distinct meshes did not affect frame rate in the test.
- The best case (10 parts per soldier collapsed to 1, screen full of one unit type) gave about **8 % FPS**. On this pack the tool's cross-rigid merging removes 2.6 % of soldier-part instances, worth roughly **0.2 % FPS** for 69 MiB of added geometry.
- The cross-rigid writer used to emit joint slots with more than five alternatives; the engine uses only five per slot, so output lost visual variety in-game. **Fixed** by nesting through generated group VMDs (see Open issues, item 1).
- Recommendation: do not build the shared-atlas planner or a per-configuration writer; stop extending the cross-rigid search; the slot limit is fixed, but given the payoff consider leaving cross-rigid off by default.

## How Warhammer 3 draws unit soldiers

From RenderDoc captures of the A/B test variants (Republican Guard, all soldiers at LOD 3):

| Variant | Parts per soldier | Mesh files per part | Soldier draw calls | Instances per call |
|---|---|---|---|---|
| A, split, identical | 10 | 1 | 10 | 346 |
| B, merged, identical | 1 | 1 | 1 | 346 |
| C, split, 8 byte-identical copies per slot | 10 | 5 used | 50 | 60–85 |
| D, merged, 8 byte-identical copies | 1 | 5 used | 5 | 62–75 |

- **One instanced draw per distinct mesh part in use**, whatever the soldier count. With about 3,300 soldiers on screen, B was still 1 call (3,309 instances) and C still 50.
- **Instancing is keyed on the mesh file.** Byte-identical copies under different names are drawn separately.
- **At most five alternatives per slot are used.** C and D listed 8 and the engine used exactly 5, the same five across all units. The mod author confirms the limit; it is worked around with nested (recursive) variant mesh definitions. In the source pack only 3 of 2,176 slots list more than five direct alternatives.
- **Slots vary independently**; the engine does not pick from whole-soldier presets.
- **Far soldiers are a proxy.** At deployment-camera distance every soldier was drawn by one call of a 188-triangle mesh (`DrawIndexedInstanced(564, ~1438)`); the VMD meshes are not rendered at all.
- Whole frames were about 290–620 draw calls.

## Frame-rate test

About 3,300 soldiers on screen, same scene and camera, GPU at 100 %:

| Variant | Parts per soldier | Soldier draw calls | FPS |
|---|---|---|---|
| A | 10 | 10 | about 26 |
| C | 10 | 50 | about 26 |
| B | 1 | 1 | about 28 |
| D | 1 | 5 | about 28 |

- A = C and B = D: a fivefold change in draw calls and distinct meshes made no difference.
- Split vs merged: about 2 FPS (roughly 8 %) for removing 90 % of soldier-part instances, with identical geometry and textures. Since the GPU was the bottleneck, at least part of the per-instance cost is GPU-side; what exactly was not identified.
- So the tool's `soldiers × parts` benefit model has the right shape, and merging varied slots into per-combination meshes does not hurt performance by multiplying meshes.

Limits of the test: single readings to the nearest FPS, no repeated runs, LOD 3 only, one machine, never CPU-bound.

## What the tool achieves on this pack

Baseline is 1,124 "expected battle draws" (soldier-part instances, army-weighted) over 172 analyzed root VMDs.

| | Saved | Share of baseline | Cost |
|---|---|---|---|
| Cross-rigid only, current gates | 29.9 | 2.6 % | 69 MiB geometry |
| Atlas + same-rigid merge + cross-rigid | 41.2 | about 3.7 % | 190 MiB textures, 52.5 MiB geometry |
| T0: today's merge identity, no writer limits | 26.6 | 2.4 % | – |
| T1: shared atlas (ceiling) | 173.6 | 15.4 % | 5.3 GiB geometry if all written |
| T1 under 64 / 128 / 256 MiB geometry cap | 63.7 / 87.2 / 113.7 | 5.7 / 7.8 / 10.1 % | plus about 175 / 221 / 283 MiB source textures to atlas |
| T3: T1 + weighted2/4 unified (approx.) | 213.7 | 19.0 % | – |
| T4: T3 + alpha unified (approx.) | 316.1 | 28.1 % | – |

T0 and the tiers were measured in combined mode, on top of atlases already built.

- **Cross-rigid merging is at its ceiling.** It selects 23.7 of a possible 26.6 in combined mode. More search breadth, a larger state cap or portfolio tuning can add at most about 3.
- **Materials are the limit.** 472 of 500 materials in the pack are distinct; ignoring the four atlasable textures there are 97 classes. Legacy `s_*` texture slots make no difference (T2 = T1).
- **Atlasing does not unlock cross-rigid merges today.** The atlas planner buckets merge affinity by geometry path and LOD (`BuildMergeAffinityGroups`, `PackTextureAtlasBatchService.AtlasPlanning.cs`), so it never places co-rendering parts from different rigids in one atlas.
- **A shared-atlas design is feasible but poor value.** All 172 roots expand exactly, no context exceeds 4,096 configurations, and the needed atlases are small (50 components at the 256 MiB cap, largest 31 materials, all but two fit one 4096² page). Using the frame-rate test's ratio (10 % fewer instances ≈ a little under 1 % FPS), the 256 MiB design is worth under 1 % FPS with the screen full of this mod's units.

Rough FPS equivalents, same ratio: current cross-rigid about 0.2 %; T1 ceiling a bit over 1 %; T4 ceiling about 2–2.5 %.

## Second pack: `ovn_albion.pack`

Combined mode (atlas, same-rigid merge, cross-rigid), run after the slot-limit fix. 68 gameplay-used root VMDs; run time 609 s.

| | `ak_teb3` (combined mode) | `ovn_albion` |
|---|---|---|
| Baseline expected draws | 1,124 | 1,570 |
| Cross-rigid saved | 23.7 (2.1 %) | 7.8 (0.5 %) |
| Cross-rigid payloads / geometry | 111 / 52.5 MiB | 19 / 24.2 MiB |
| Source VMDs rewritten | 43 | 4 |
| T0 ceiling (today's merge identity) | 26.6 (2.4 %) | 11.1 (0.7 %) |
| Atlas + same-rigid saved | 17.5 (1.6 %) | 39.8 (2.5 %) |
| New atlas textures | 189.8 MiB | 154.3 MiB |

- **Cross-rigid is worth even less here**: 0.5 % of soldier-part instances, roughly 0.05 % FPS by the frame-rate test's ratio. It is again close to its ceiling.
- **Atlasing with same-rigid merging does better** than on `ak_teb3` (2.5 % against 1.6 %). Everything the tool does today totals about 3 % of instances, roughly 0.3 % FPS.
- **Output is sound**: validation passes; 3 writer slots nested (largest 288 alternatives, depth up to 5, all exact), 336 group VMD files; no author-written slots above the five-alternative limit.
- **Shared-atlas ceilings are higher but still small**: T1 24.1 %, T3 27.3 %, T4 35.5 %; under a 64 / 128 MiB geometry cap 5.8 / 9.4 % (about 0.5 / 0.8 % FPS), needing 10 / 16 small shared atlases that all fit one page, built from about 95 / 143 MiB of source textures.

Both packs lead to the same conclusion: cross-rigid merging is not worthwhile and should stay off by default.

## Atlasing: cost and benefit

| | `ak_teb3` | `ovn_albion` |
|---|---|---|
| Soldier-part instances removed (atlas + same-rigid merge) | 1.6 % | 2.5 % |
| Atlas textures added to the pack (disk) | 189.8 MiB | 154.3 MiB |
| Expected loaded atlas memory in battle | 6.9 MiB | about 14.9 MiB |
| Expected source memory no longer loaded | 1.1 MiB | 7.1 MiB |
| Net expected video memory growth | 5.9 MiB | 7.8 MiB |
| Atlas planning time | 540-740 s | 402 s |

- **The large cost is pack size and run time, not video memory.** The value gate models, per texture, the probability that it is loaded in a battle before and after atlasing; a vanilla texture the mod no longer references stops counting unless a vanilla unit in the battle needs it. These are scenario averages, not a peak.
- **The gain is small because same-rigid parts are split by shader, not texture.** In the `ak_teb3` dump a WSModel has on average 2.08 distinct materials at LOD 0 and 1.96 if the four atlasable textures are ignored, so a perfect atlas removes about 6 % of the parts inside a rigid (rough, unweighted, about half the WSModels parsed). Texture differences sit between rigids, which is why the cross-rigid shared-atlas ceiling is much higher.
- Atlasing is now opt-in ("Build texture atlases (experimental)", off by default). With it off the tool applies structural-only merges and geometry optimisation.

## Where the frame time goes, and the LOD audit

Render-scale test, variant B, about 3,300 soldiers: 30 FPS at 100 % scale, 46 FPS at 50 %, GPU usage 98-100 % at both. Quartering the pixels saved 11.6 ms of a 33.3 ms frame, so roughly half the frame (about 15.5 ms) depends on pixel count and half (about 18 ms) is GPU work on geometry: vertices and soldier-part instances.

LOD audit of `ak_teb3` (172 roots, 287 source-owned and 299 vanilla rigids, 10 s):

- The mod's LODs follow a uniform 100 / 75 / 50 / 25 % triangle pattern; a typical soldier is 9,000-20,000 triangles at LOD 0 and 1,600-4,500 at the last LOD, in 4-10 mesh parts.
- 227 of 287 source-owned rigids switch at 10 / 20 / 30 m, so in an ordinary battle view nearly every soldier is at the last LOD. The tool's scenario model assumes 25 % of soldiers at each LOD, which overweights LOD 0.
- 31 rigids use 100 / 200 / 300 / 400 m and 2 crests use 1,000,000 on every LOD; 113 unit looks mix profiles. **These may be the author's intent; LOD geometry and switch distances are out of scope for the tool by the author's decision.**
- Possible defects to report to the author: `ak_teb_freecomp_legs_estalian_01` has zero triangles at LOD 2 and 3; the `lorenzo_lupo` parts have no LOD reduction.

With LOD geometry and distances out of scope, the geometry-side levers left to the tool are parts per soldier (merging, measured at about 2.7 ms for removing 90 % of them) and nothing else of size.

## Open issues

1. **Joint slots exceeded five alternatives — fixed.** The writer created one `SLOT` with one wrapper per Cartesian combination, plus duplicates for optional-slot probabilities; the engine used five, and rewritten units visibly lost variety in-game. Writer-created slots are now split into at most five `VARIANT_MESH_REFERENCE` entries to generated group VMDs (`NestCrossRigidWriterSlots` in `PackTextureAtlasBatchService.CrossRigid.Emission.cs`), with a hard check at the end of emission. On this pack (combined mode): 71 slots nested, largest 3,000 alternatives, nesting depth up to 6, all splits exact, 6,542 group files reduced to 1,986 by dedupe; validation passes and the rewritten units look correct in-game. Author-written slots are left alone; three in the source pack list six alternatives (`ak_est_trooper_base` head, `ak_morion_pirazzo` bighead, `bor_strooper_base` head).
2. **The portfolio search no longer improves results** (baseline = best in every run since the per-payload ceiling) and optimises a different gate than the final writer. Kept at the author's request.
3. **Ten tests in `Test.KitbashEditor` fail** (`LoadAndSave_Rome2_*`, `Warhammer3_SaveKarl_*`, `ArgumentOutOfRangeException: length ('-1')`). Not investigated; ignored at the author's request.

## Changes made this session

- Far-LOD fix: sources past their last LOD distance use their last LOD instead of being dropped from the merged payload.
- Union LOD schedule samples the near edge of each interval instead of the midpoint.
- Payloads whose value sits in pair-fallback plans are routed to the supplementary stage instead of being rejected as zero-benefit (no effect on this pack).
- Per-payload cost ceiling (`CrossRigidPerPayloadCostCeilingMultiplier = 1.0`): geometry 238 → 69 MiB with draws unchanged.
- Emission save-and-reload replaced with owned copies (`MergeRmvModelsOwned`): emit phase 60 s → 4 s.
- Report additions: cost curve, missing-component counter, baseline expected draws, ideal-merge tiers with payload, state-cap, atlas-component and baked-variant estimates.
- `PackTextureAtlasBatchService.cs` split into 16 partial-class files.
- Writer-created slots nested to respect the engine's five-alternative limit.
- Cross-rigid merging is now opt-in: a "Cross-rigid merges (experimental)" checkbox, off by default (`crossRigidMerges` on `Process`). Previously it ran whenever mesh merging was enabled. Cross-rigid-only mode still runs it.
- No-atlas mode (`buildAtlases` on `Process`, default in the dialog): skips atlas discovery, planning and building; structural-only merges, geometry optimisation of the merged rigids, pruning and validation still run. On `ak_teb3`: 178 s (about 930 s with atlasing), 38 merge groups, 42 mesh parts removed, validation passes. Extending geometry optimisation to every source-owned gameplay rigid was tried and dropped: 353 extra rigids re-saved for 0.5 % fewer vertices.
- Report-only LOD audit (`PackTextureAtlasBatchService.LodAudit.cs`): per-rigid and per-unit triangles by LOD, switch distances, flags and rankings.
- Report-only asset checks (`PackTextureAtlasBatchService.AssetChecks.cs`): alpha-shader materials whose base colour is fully opaque, and duplicate textures and materials, each with the same-rigid merges they would unlock.
- Fixed `ParamList<T>.Clone()` in `Shared/GameFiles/RigidModel/MaterialHeaders/ParamList.cs`, which returned an empty list. Every cloned rigid material lost its alpha index, UV scale and decal/dirt indices, so merge compatibility ignored them and merged parts were written without them. The merge check is stricter as a result.
- New tests in `PackTextureAtlasCrossRigidTests.cs`; 329 pass across that class and `PackTextureAtlasValueGateTests`.

## Recommendations

1. Do not build the shared-atlas planner or a per-configuration writer.
2. Stop extending the cross-rigid search.
3. The five-alternative limit is fixed; consider leaving cross-rigid off by default anyway, since it adds about 2,000 small VMD files and tens of MiB of geometry for roughly 0.2 % FPS.
4. Keep same-rigid merging and atlasing; they reduce parts per soldier without rewriting VMDs.
5. If more performance is wanted, look at per-soldier GPU cost (for example vertex count at distance), which the tool does not address.
