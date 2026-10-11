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
- New tests in `PackTextureAtlasCrossRigidTests.cs`; 244 pass across that class and `PackTextureAtlasValueGateTests`.

## Recommendations

1. Do not build the shared-atlas planner or a per-configuration writer.
2. Stop extending the cross-rigid search.
3. The five-alternative limit is fixed; consider leaving cross-rigid off by default anyway, since it adds about 2,000 small VMD files and tens of MiB of geometry for roughly 0.2 % FPS.
4. Keep same-rigid merging and atlasing; they reduce parts per soldier without rewriting VMDs.
5. If more performance is wanted, look at per-soldier GPU cost (for example vertex count at distance), which the tool does not address.
