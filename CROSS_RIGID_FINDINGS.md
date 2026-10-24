# Cross-rigid merge and texture memory: findings (2026-10-11, updated 2026-10-16)

Branch `feature/texture-atlas-consolidation`. Test pack: `!ak_teb3.pack` (dump in `K:\wh3mods\albion\teb`). In-game test assets: `K:\wh3mods\albion\abtest\` (see its `README.txt`).

## Summary

- The engine cost that merging reduces is **mesh parts per soldier**. Draw-call count and the number of distinct meshes did not affect frame rate in the test.
- The best case (10 parts per soldier collapsed to 1, screen full of one unit type) gave about **8 % FPS**. On this pack the tool's cross-rigid merging removes 2.6 % of soldier-part instances, worth roughly **0.2 % FPS** for 69 MiB of added geometry.
- The cross-rigid writer used to emit joint slots with more than five alternatives; the engine uses only five per slot, so output lost visual variety in-game. **Fixed** by nesting through generated group VMDs (see Open issues, item 1).
- Texture size is not expected to affect frame rate here (a working conclusion, not measured), so all texture work is judged on **video memory and pack size only**.
- Of the texture measures, only the shared-texture repack paid off on `ak_teb3`: 32.7 MB saved on four sets. Downscaling is down to 7 candidates (12.6 MB) once lords and heroes are excluded, and the other texture ideas were estimated and not built.
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

### Bone weights per vertex (report only, `PackTextureAtlasBatchService.BoneWeightAudit.cs`)

The idea: convert 4-weight vertices (`Cinematic`, `weighted4_*` shaders) to 2-weight (`Weighted`, `weighted2_*`) where the skinning result is the same. That halves the bone-matrix fetches per vertex and saves 4 bytes per vertex.

What the shaders do, from the Patch 8.1 vertex-shader disassembly:

- **`weighted2`** reads one weight byte w0 and uses 1 − w0 for the second bone; the stored second byte is ignored.
- **`weighted4`** reads three weight bytes and ignores the fourth. A fourth, implicit bone gets 1 − (w0+w1+w2) whenever b0+b1+b2 <= 254.
- So a vertex converts exactly when at most 2 distinct bones have an effective weight, counting the implicit one.

`ak_teb3` (all 287 source-owned rigids, 1.3 s):

| | Meshes | Vertices |
|---|---|---|
| 4-weight, all LODs | 1,049 | 1.17 M (LOD 0 and 1: 1,033 meshes, 1.13 M vertices) |
| 2-weight (LOD 2 and below, CA's convention) | 1,091 | 663 k |
| 4-weight vertices that convert exactly, one by one | – | 837 k (72 %) |
| 4-weight meshes where every vertex converts | 278 | 153 k (13 %) |
| Same, weighted by expected battle part instances | – | 13.0 % |

- **Memory:** 617 KB of vertex data.
- **Shader twins:** most convertible meshes use `weighted4_character(_alpha)`, whose `weighted2_` twins have the same texture channels. Four small families have twins whose channels differ (`character_emissive`, `default_alpha`, `random_colours`, `tinted_metal_skin`); one has no twin in the dump.
- **Verdict: not worth building.**
  - 4-weight geometry exists only at LOD 0 and 1, and most soldiers switch to their last LOD at 30 m, so in an ordinary battle view little of it is drawn.
  - Of what is drawn, 13 % would lose half its skinning fetches, which is likely well under 1 % FPS.
  - The rewrite (vertex formats plus material shaders) would carry more risk than any texture stage.

## Texture memory

Everything in this section is about video memory and pack size. None of it is expected to change frame rate. Numbers are for `ak_teb3` unless stated.

### Texture size and frame rate

- **Working conclusion: texture size does not affect FPS in this setup.** No A/B test with different texture sizes was run, so there is no direct measurement.
- **Supporting evidence:** the render-scale test (above) showed about half the frame depends on pixel count and the rest is GPU work on geometry and instances. At battle distance the GPU already samples lower mips, so removing the top mip does not reduce pixel work.
- All texture work below is therefore judged on memory only.

### Texture downscaling (`PackTextureAtlasBatchService.TextureDownscale.cs`)

- **Report and opt-in option.** "Downscale oversized textures (experimental)" (`downscaleTextures` on `Process`), off by default. The report always lists the candidates, whether or not the option is ticked. Disabled in cross-rigid-only mode.
- **Rule.** A texture file shrinks to what its users' texel density needs, using the atlas stage's reference density and 0.5 / 0.25 buckets. A candidate is at most 0.5x and both sides stay at least 256 px. Same path and format, so no material changes; mip slicing is used where possible.
- **Alongside atlasing.** Atlases are built from full-resolution textures first. Atlas textures are never downscaled; only textures the atlases did not replace are rewritten.
- **Lord/hero rule.** No texture used by a lord or hero is downscaled, and units that cannot be classified count as lord/hero. The first report listed 20 candidates (71.3 MB), three quarters of it one lord's textures; with the rule applied they are gone.
- **Result.** 7 candidates remain, all morion helmet textures at 2048x1024 to 1024x512, saving 12.6 MB. Another 135 textures are assessed and not oversized.

### Vanilla texture coverage (report only, `PackTextureAtlasBatchService.VanillaTextureCoverage.cs`)

Measures how much of each vanilla texture the pack's parts touch (256x256 cell grid, every LOD). A per-texture crop would replace the vanilla texture with a smaller copy in the pack.

- The pack references 690 vanilla textures (663 MB).
- Whole-pack upper bounds for cropping:

| Limit | Textures | Saved (at most) | Added to pack | Rewritten |
|---|---|---|---|---|
| Under 10 % coverage | 95 | 127 MB | 17 MB | 51 rigids |
| Under 25 % coverage | 219 | 217 MB | 71 MB | 104 rigids plus 4 vanilla copies |

- Nothing is saved above 25 % coverage, because a half-size crop cannot hold the used area.
- These are upper bounds. Whether the vanilla texture stays loaded anyway (another unit or mod part uses it) was not computed, because the existing residency model has side effects. Not built.

### Per-unit repack estimate (report only, `PackTextureAtlasBatchService.UnitTextureRepack.cs`)

Estimates repacking textures into atlases per unit, assuming a unit shows every alternative whenever it is on the field.

- **Sharing is heavy.** Only 12 of 392 rigids and 16 of 911 textures are used by a single unit. 528 of 657 units fall in one connected sharing group.

| Option | Idea | Whole pack (all units loaded) | Verdict |
|---|---|---|---|
| A | One atlas per unit, with rigid copies | 1.02 GB to 4.2 GB | Negative |
| B | One atlas per sharing group | Big group's atlas is 273 MB; 607 of 657 units get worse | Negative |
| C | One atlas per signature (exact set of units using a texture) | 1,021 MB to 721 MB; 348 units better, 309 unchanged, none worse | Never worse |
| C2 | C, merging signatures with at least 80 % overlap | 3 fewer atlases, 34 units worse | Worthless |

- **Option A details.** 4.1 GB of atlases are added and 1,834 rigid copies are needed. Sibling pairs get worse: Tilean militia variants 50.5 to 53.7 MB, Conquistador variants 53.8 to about 81 MB.
- **Option C selective builds** (best sets first): 5 groups 68 MB, 10 groups 111 MB, 25 groups 186 MB (43 rigids), 50 groups 247 MB, all 131 groups 301 MB.
- **Estimates ran optimistic.** They come from a coverage grid with 25 % packing slack; see the built result below.

### Shared texture repack, first increment (`PackTextureAtlasBatchService.SharedTextureRepack.cs`)

Built, opt-in: "Repack shared textures (experimental)" (off by default). New smaller textures hold only the regions a rigid's parts use, at the same texel density; the rigid's UVs are rewritten for all LODs, new materials are written and its WSModels repointed. Original textures are not edited.

- **Scope.** Option C sets confined to one mod-owned rigid. Cannot be combined with cross-rigid-only mode (which never writes textures). It can run together with atlasing and cross-rigid merges, in that order (cross-rigid merges, atlasing, repack): atlasing runs first, a rigid atlasing changed in any way is left out of the repack (reason "the rigid was changed by texture atlasing"), and an atlased part no longer counts as a user of the textures it used, so each part and texture is changed by at most one of the two. The thresholds and the actual-bytes re-check are unchanged. Cross-rigid merges keep their full candidate set (they run before atlasing and the repack, so atlasing neither changes what they select nor takes meshes they built from: those rigids are not atlas candidates, reason "the rigid was changed by cross-rigid merging", and the generated cross-rigid WSModels count as users of the source textures they still name in the atlas value gate) and the repack works with them (changed 2026-10-23, output version 6): the source rigids the writer built generated models from (`CrossRigidChangedRigids`) and the generated rigids themselves (in memory, copies of source parts with their original materials) are users of the set's textures like any other rigid. Each generated WSModel's material nodes become part bindings, so generated parts go through the same closure, chart and page logic and every one of them is rewritten against the same cropped textures and the same generated material per (material, page). A generated rigid is eligible when atlasing did not change it and every WSModel bound to it is a generated one; otherwise its parts are dropped like any ineligible rigid's. A set is built only if every user, source, cross-rigid source and generated, is rewritten. Generated file names come from the plan, not the bytes, so no rename or VMD change is needed and the cross-rigid output (generated models, VMD clones, draws) is unchanged.
- **Chart snapping.** Charts are snapped to 32 px so mips 0-3 are block-copied exactly. This fixed a bug: mip-3 errors came from regions offset by 2 texels against the 4x4 block grid.
- **Pixel check before writing.** Block-copied levels must match exactly. Re-encoded levels fail if more than 0.5 % of samples are over 96 or the mean is over 12.
- **Layout.** Layout groups combine tuples with the same slots, formats and ratios. Pages split at 4096. A group needs at least 512 KiB saved and a set at least 1 MiB (2 MiB until 2026-10-23, see "Repack threshold" below).
- **Pass-through.** Auxiliary slots (blood, dither, distortion, distortion noise) pass through, and `blood_uv_scale` is divided by the UV scale. Tiny uniform textures are kept as authored.
- **Output paths.** Generated vanilla-material copies go under `variantmeshes\asset_editor\repack_materials\`, textures under `textures\asset_editor\atlases\*_repack_*.dds`. The rigid's embedded material is not updated (same as existing atlas practice).

Selection funnel:

| Step | Sets |
|---|---|
| Considered | 299 |
| Estimated saving below threshold | 261 |
| Not confined to one rigid | 22 |
| Rigid not eligible | 1 |
| Dropped by closure (15 parts whose material uses textures outside the set) and related checks | 5 |
| Failed the bytes gate (near-full coverage) | 6 |
| Failed the pixel check | 0 |
| **Built** | **4** |

Built sets:

| Set (rigid) | Before | After |
|---|---|---|
| `ak_teb_lupio_demi_body` | 11.18 MB | 5.59 MB |
| `teb_golden_lion_stuff` (4 tuples, 2 pages) | 17.48 MB | 11.32 MB |
| `ak_teb_lupio` | 11.18 MB | 0.70 MB |
| `teb_inquisitor_staff_02` | 11.18 MB | 0.70 MB |
| **Total** | **51.0 MB** | **18.3 MB** |

- **Saving 32.7 MB.** Zero pixel error on about 101,500 top-mip samples; validation passes; the stage takes 9.2 s.
- **The estimate was optimistic.** The demigryph body was estimated at 16.8 MB saved and saved 5.6 MB; the lion was estimated at 11.9 MB and saved 6.2 MB.
- **Checked in-game by the author:** Lupio, the lion and the inquisitor staff look fine. Blood density with blood enabled is not yet checked, and the `blood_uv_scale` rule is an assumption.
- **What was left:** the 22 multi-rigid sets and the 5 closure-dropped sets. Built in the next increment, below.

### Shared texture repack, multi-rigid (same file)

The repack now takes any signature set whose users are all mod-owned rigids, and rewrites every rigid using its textures consistently against one set of new textures.

- **Flat textures.** A texture of any size that is one flat colour is kept as authored and no longer blocks a part.
- **Merged plans.** When a material's textures belong to several signature sets, those sets are pulled into one plan, up to 8 sets and 16 source rigids; generated cross-rigid rigids have their own cap of 64.
  - A plan is written completely or not at all.
  - A failed merged plan falls back to its seed set alone.
  - Validation checks that no unit loads more texture bytes than before.
- **Page splitting.** A layout group whose power-of-two page cannot shrink puts whole clusters of charts on up to 4 smaller pages. A chart is never duplicated and a mesh part is never split.

Results (validation passes, zero pixel error on every sample):

| | `ak_teb3` | `ovn_albion` |
|---|---|---|
| Signature sets considered | 299 | 211 |
| Below the 2 MiB estimate threshold (then the default) | 254 | 155 |
| Merged into another set's plan | 10 | 16 |
| Dropped by closure, variants, tiling or ineligible rigids | 12 | 20 |
| Failed the bytes gate (near-full coverage) | 16 | 6 |
| **Built** | **7 sets, 12 rigids** | **14 sets, 28 rigids** |
| Texture bytes replaced → new | 76.5 → 25.2 MB | 172.3 → 43.3 MB |
| **Saved** | **51.4 MB** | **129.0 MB** |
| Of which from merged plans | 0 | 8.4 MB (Chaos lord capes) |
| Of which from page splitting | 2.8 MB | 2.1 MB |
| Pixel samples checked | 190,536 | 1,054,264 |
| Stage time | 14.7 s | 21.5 s |

- **Most rounding losses are single-cluster groups.** A group whose charts form one connected cluster cannot split (7 groups on `ak_teb3`, 18 on `ovn_albion`).
- **Many repacked textures are vanilla.** A vanilla unit in the same battle keeps the original loaded, so these savings are whole-pack upper bounds.
  - Copying vanilla rigids to unlock more sets was not done.
  - Only 3 such blockers exist: the two demigryph rigids and `brt_men_at_arms_helmet_01`.

### Repack threshold (2026-10-23)

Bench runs on the Workshop `!ak_teb3` (output version 4 code, only the set threshold changed):

| Set threshold | Sets built | Saved (upper bound) | Textures / materials generated | Rigids rewritten | Pack on disk |
|---|---|---|---|---|---|
| 2 MiB (old default) | 8 | 57.0 MB | 33 / 27 | 15 | 577.9 MB |
| **1 MiB (default)** | **15** | **69.4 MB** | **70 / 47** | **24** | **583.0 MB** |
| 512 KiB | 18 | 71.9 MB | 88 / 57 | 27 | 585.6 MB |

- 1 MiB keeps 84 % of what 512 KiB adds. The three extra 512 KiB sets save 2.4 MB together (Arch Lector head estimated 2.4 MB, saved 0.7 MB).
- Actual savings ran at about 60 % of the estimates again. Validation passes and pixel error is zero at every threshold.
- Two mod-owned textures stay in the output next to their repacked copies (another reference keeps them).
- Output version 5.

### Exact texture compaction (`PackTextureAtlasBatchService.TextureCompaction.cs`)

Opt-in, "Compact textures exactly (experimental)", off by default and disabled in cross-rigid-only mode. It runs last, on the output's DDS files, and rewrites textures in place (same path, same sRGB-ness, same header kind).

- **Flat textures.** A texture whose every texel at every mip level is one exact RGBA becomes 4x4 with a 3-level chain.
- **Opaque BC2/BC3.** A BC2/BC3 texture whose alpha is 255 at every texel becomes BC1. The colour blocks are kept; where c0 < c1 the endpoints are swapped and the indices remapped, and equal endpoints get index 0.
- **Verified before writing.** Every result is decoded and compared texel for texel at every level before it is written, and validation checks it again.
- **What counts as a reference.** Only textures the pack owns that a material references are eligible: an xml material slot, or the texture list embedded in a rigid's material.
  - The embedded lists include the old WH2 slots (diffuse, specular, gloss map).
  - A texture also named by a UI, VMD, WSModel or other non-material file is skipped.
  - An unreadable rigid's texture paths count as non-material references.
- **Not covered.** Textures written by the atlas, repack or downscale stages are skipped, and so is BC7 (no exact decoder in the codebase).

| | `ak_teb3` | `ovn_albion` |
|---|---|---|
| DDS files in the output | 533 | 701 |
| Flat, shrunk to 4x4 | 6 (24.1 MB saved) | 13 (18.2 MB saved) |
| Opaque BC3 → BC1 | 37 (22.4 MB saved) | 13 (38.8 MB saved) |
| **Saved** | **46.5 MB** | **57.0 MB** |
| Referenced only by a rigid's embedded material | 120 | 115 |
| Skipped as BC7 | 21 | 10 |
| Stage time | 4.3 s | 6.1 s |

- **Largest items.**
  - `ak_teb3`: `lorenzo_mask.dds`, a flat 4096x4096 BC3 (22.4 MB → 176 bytes), and the green dragon's body and wing textures (5.6 → 2.8 MB each).
  - `ovn_albion`: `mask_alb_shield.dds` and `alb_truthsayer_body_mask.dds`, both opaque 4096x4096 BC3 (22.4 → 11.2 MB each).
- **Checked in-game by the author on `ak_teb3`:** no visible change.
- **Part of the saving is pack size only.** The game ignores a rigid's embedded textures when the rigid has a WSModel, and some rigids are not used at all (for example `ak_grave_de_sword_2h_*` and `ak_kf_lupio`). Rewriting those textures is harmless, just useless.

### Which texture channels the shaders read (`Wh3ShaderTextureChannels.json`)

The game's compiled shader cache is on the `shaders` branch of the dump repo `K:\projects\wh3dump` (Patch 8.1, build of 12/07/2026). `Tools/ShaderChannels/extract_shader_channels.py` builds the table from it:

- it extracts the compiled pixel shaders of the `weighted2_*`, `weighted4_*` and `rigid_*` shaders (4,932 blobs, 425 shaders);
- it disassembles them with `fxc.exe /dumpbin` from the Windows SDK;
- for each `t_xml_*` slot it records the channels any permutation samples.

The table is embedded in the tool. Rerun the script after a game patch.

| Slot | Channels read |
|---|---|
| `base_colour`, non-alpha shaders (`*_character`, `*_default`, `*_faction_colours`, `*_random_colours`) | RGB |
| `base_colour`, `*_alpha` and `alphatint` shaders, `weighted4_character_emissive` | RGBA |
| `material_map`, every shader | RG |
| `normal`, every shader | GA (alpha holds X) |
| `mask` | RGBA, or not bound (`random_colours`, `faction_colours`, `default`) |

### Dropping alpha no shader reads (same file, opt-in)

"Drop texture alpha no shader reads (experimental)", available only while compaction is ticked. A BC2/BC3 texture with real alpha becomes BC1 when:

- every user is an xml material whose shader is in the table, binds the texture's slot and does not read its alpha;
- every rigid that names it is drawn only through owned WSModels, checked with the same rules as the repack;
- it does not override a vanilla texture, and the vanilla game data is loaded.

RGB stays bit-exact and is verified at every level; only the alpha is dropped.

- **`ak_teb3`:** 12 textures, 24.8 → 12.4 MB (saves 12.4 MB). They are the morion guard and brass base colours, `ak_teb_lupio_kf_base`, `ak_teb_gambeson_base` and two knight leg base colours, all used by `weighted2_character`/`weighted4_character`.
- **Correctly kept back:**
  - 22 textures whose alpha is read (alpha-shader base colours, normals, masks);
  - 34 named by rigids that VMDs reference directly (mostly campaign ships);
  - 30 named by rigids whose consumer discovery is incomplete;
  - 1 vanilla override.
- **Checked in-game by the author** (morions, Lupio, the gambeson): no visible change. That supports the assumption that the material's shader is the one the game uses.
- **Compaction total on `ak_teb3` with the option:** 58.9 MB (24.1 MB flat, 22.4 MB opaque, 12.4 MB unread alpha).

### Emissive distortion copy (2026-10-23)

Problem: a part whose material has a `t_xml_emissive_distortion` (or `t_xml_emissive_texture`) texture was ineligible for the shared texture repack (reason "non-atlasable slot"). In the test packs this closure cause was 39.9 MB of the estimate on `!ak_teb3` (6 sets) and 35.8 MB on `ovn_albion` (7 sets).

Shader evidence (disassembled compiled shaders of the `wh3dump` `shaders` branch, `fxc/*.hlsl_compiled`, 922 pixel shader variants of 159 families that name the distortion texture): in the common character emissive families the pixel shader does exactly

- `d = sample(t_xml_emissive_distortion, UV0)` (two channels),
- `emissiveUV = (UV0 + d * sp_emissive_distort_strength + time * sp_emissive_speed * sp_emissive_direction) * sp_emissive_tiling`,
- `glow = sample(t_xml_emissive_texture, emissiveUV)`,

with UV0 the interpolator the base colour is sampled with. Both emissive slots point at shared tiled noise textures in `commontextures\` (512x512 DXT1), never per-model art. About 110 families compute the distortion UV differently (ethereal standard/overlay, renown topper, `*_distortion`, dissolve, ghost, tinted metal, flesh_to_stone, spirit, ...) or sample extra textures at UV0 (`mask1`, `emissive_colour`, `scroll_direction`, ...); they stay blocked.

Verification: `Tools/ShaderChannels/verify_emissive_uv.py` (+ `dxbc_disassemble.ps1`) symbolically evaluates the straight-line ALU code of every variant (registers hold polynomials over interpolator components, structured-buffer parameters, the camera time and texture results; control flow is scoped, loops make written registers opaque) and requires every UV0-dependent texture read to be one of: an atlas slot or the distortion at the raw UV0, the emissive texture at exactly the chain above (matched by offset ranges of the parameter structure, the camera `time_in_sec` constant and two distinct distortion channels), the blood map (accepted under the existing blood assumption: the shaders use `(UV0 * constant + per-instance offset) * blood_uv_scale`, not a plain scale), or a read that does not depend on UV0. UV0 reaching anything else (other ALU use, outputs) fails the family. A family qualifies only if all its variants pass. Result: 46 of 159 families are on the allowlist in `PackTextureAtlasBatchService.SharedTextureRepack.cs` (`SharedRepackEmissiveVerifiedShaders`): the `*_emissive`, `*_iridescent_emissive`, `*_silk_emissive`, `*_alphatint_emissive`, `rigid_character_tentacle_emissive` and `*_character_ethereal` families (with their `_alpha` and `_blood` twins). Families with a `vfx_` prefix verified but are left out because the file name does not say whether the path is `shaders/vfx/x` or `shaders/vfx_x`.

Design ("copied slot"):

- The distortion slot is cropped and remapped like an atlas slot (chart rectangles, block copy, pixel check) but its texture is not a set member: it never joins the signature-set closure, is never classified uniform, is excluded from the chart alignment (`32 << downshift` of the other slots) while still deciding the cell (whole noise texels), is keyed in the layout group as `copy:<noise path>`, and is written as `<name>_repack_emissive_distortion.dds`. Its bytes count as cost in the page and in the actual-bytes gate, but it is not in `plan.Replaced`: not a replaced texture, not pruned, not part of the "no WSModel binds a replaced texture" check, not part of the signature-set ownership check. `t_xml_emissive_texture` stays as authored.
- The shader needs the UV scale `s` equal in U and V for the compensation to be exact, so the group key carries the reference aspect ratio and the page canvases of such a group are restricted to that aspect ratio (candidate generation and page splitting). Groups that then do not pay fall to the existing group gate; no approximation for unequal scales.
- Per generated material: `emissive_tiling / s`, `emissive_distort_strength * s`, `emissive_direction * s` (invariant culture, "R"), logged per material in the report; the page fails if a parameter cannot be rewritten.
- Remaining ASSUMPTION (check in game): the new UV0 is `s * UV0 + offset`, so the scrolling pattern is exact up to a constant per-chart phase shift (offset times tiling).
- New part reasons: "the material's shader is not verified for emissive repacking (`<shader>`)" and "the material lacks emissive parameter `<name>`". `OutputVersion` 6 -> 7 (pinned stages unchanged, same golden hash).

Bench (this branch, `PackOptimizerBench`, baselines `teb-causes` and `albion-causes`):

| pack | sets built | actual saving | emissive copies | notes |
| --- | --- | --- | --- | --- |
| `!ak_teb3` | 17 -> 19 | 72,591,032 -> 91,377,928 bytes (+18.8 MB) | 12 parts, 2 copies (87,664 bytes), 4 materials rescaled | closure loss 72.4 -> 36.9 MB; the emissive_distortion cause (39.9 MB, 6 sets) is gone; 2 of its sets built (`ak_teb_estragon_shoulders_03`, `ak_teb_piggolo_clothing`), 2 more fail the actual-bytes gate; 2 built layout groups aspect-restricted (single-page cost 3.2 MB), 1 more group lost only to the restriction; pack file +0.9 MB (new textures, old ones stay unless pruned) |
| `ovn_albion` | 20 -> 20 | 139,634,888 (unchanged) | 0 | the emissive cause (35.8 MB, 7 sets) is gone from the closure, but the freed sets do not pay (chart coverage 70 % of 2048 textures, "group would not get smaller"): closure drops 31 -> 26, actual-bytes failures 4 -> 8; 1 group lost only to the aspect restriction; two built sets only changed generated file names (their merge plan identity changed) |

All `PASS:` lines still pass, status SUCCESS. Tests: 813 in `Test.PackOptimizer.Core` (13 new: eligibility, new reasons, copy slot alignment/uniform/group key, aspect restriction, copy not replaced/pruned, parameter math, report counters).

## 2026-10-23: materials listing one texture slot twice

**Symptom (in game):** the plates of `cth_shugengan` showed a blotchy white pattern on LOD0/1 (weighted4) while LOD2/3 (weighted2) were fine.

**Cause:** the vanilla material `cth_shugengan_body_01_weighted4_alpha_off` lists `t_xml_material_map` twice (same source). `SetTexturePath` returned after the first matching entry, so the shared texture repack redirected only the first entry to the repacked texture; the second still named the original 2048 material map, which the engine then sampled with the remapped atlas UVs. `GetTexturePath` also reads the first match while other code builds slot dictionaries where the last duplicate wins.

**Fix (output version 8):** `SetTexturePath` rewrites every entry of the slot (`RemoveTextureSlot` already removed all). A slot listed more than once with different sources is ambiguous (the engine's choice is unknown), so the atlas stage and the shared texture repack refuse such materials. Validation of generated atlas and repack materials now reports a slot with differing entries, and the repack validation reports any entry that still names a replaced texture.

## 2026-10-23: material texture slots the shader never reads

**Finding:** 289 vanilla, 23 TEB and 19 Albion materials list `t_xml_texture1` (winds of magic noise/specular), which the duplicate-slot check and the "non-atlasable slot" reason refused. None of the compiled material shaders binds a `t_xml_texture1` resource (only the system `fx_rigidtextureblend*` do). The engine binds a material slot to the compiled resource of the same name (the `.xml.shader` files carry no texture mapping; the GUID-named `t_xml_<guid>` resources are fed by identically named material slots, 747 of 747 checked in the vanilla materials), so such a slot is dead data.

**Change (output version 9):** `Tools/ShaderChannels/shader_bound_slots.py` reads the RDEF resource names of every variant and stage of each `shaders_<path>_xml_hlsl_*.hlsl_compiled` (the source path in the FASTBIN header resolves the shader path, so no `vfx_` ambiguity) and generates `PackTextureAtlasBatchService.ShaderBoundSlots.Generated.cs` (shader path -> bound `t_xml_*` slots). The `_alpha`, `_blood`, `_alpha_blood`, `_clean_alpha` siblings share one union because the engine can swap them in at run time (the blood map is bound only by `*_blood`). A `t_xml_` slot not in the material shader's set is ignored like a pass-through slot in the shared repack (never rewritten, not a texture of the part, never refused) and cannot trigger the duplicate-slot refusal (repack, atlas, validation); textures named only by such slots stay unreplaced. Shaders not in the table (and shaders binding no `t_xml_` resource) keep the old behaviour. The report counts the ignored slots per slot name and per set.

## 2026-10-23: better rectangle packer for the shared repack pages

The shared repack packs its pages with the best of the original shelf, MaxRects (short side and area fit) and skyline over three orders (height, area, longest side), on the same chart rectangles, padding and alignment (positions stay multiples of the alignment, so block-copied mips stay valid). Per power-of-two canvas the shelf result is taken when it fits, else the first better packer that does, so unchanged layouts stay byte-identical and only a smaller canvas changes a page. The split search fills the first page on rows (the old fill, wins ties) and on MaxRects pages (only when strictly smaller). One operation budget per layout group (40 million rectangle evaluations); when it is spent the shelf result is used. The packing simulation (report only) now compares other strategies against these real pages. OutputVersion 10 (the pinned golden stages are unchanged).

## 2026-10-23: tight pieces in the shared repack pages

The shared repack used to give every merged chart one rectangle: components of touched cells (8-connected) were merged while their one-cell-padded boxes overlapped, and the union box (empty cells, diagonal neighbours) was packed. The merge only protected two things: no two rectangles overlap in source space (each texel copied once) and the radius-1 cell lookup that assigned vertices to charts could not see two charts. Neither is needed for correctness. A group now also gets a layout with one rectangle per 8-connected component ("piece"), with the same one-cell gutter and 32-pixel alignment as a chart; pieces may overlap in source space (texels are duplicated, each piece carries its own gutter). Pieces are only translated by the UV rewrite, never scaled, so the layout scale, the blood UV scale and the exact emissive compensation (aspect-restricted canvases) are unchanged. Triangles are assigned to a piece by re-reading the rasterisation (all cells a triangle marks lie in one component, so a vertex shared by triangles is always in one piece); a triangle over two components or a vertex of two pieces counts as an internal inconsistency and the group keeps the merged charts. The pieces of one former chart stay in one cluster (so on one page), as the chart did. A group uses the pieces only when its pages hold strictly fewer bytes; otherwise (and on a tie) the merged layout is kept and the output is byte-identical to before. The pixel check samples every piece at least once.

Measured: `!ak_teb3` realised 103.2 -> 116.5 MB saved (24 sets built instead of 22; pieces used by 5 built groups, 62 pieces instead of 29 charts, pages -13.3 MB, and by one still-untouched group, -5.9 MB); `ovn_albion` 140.3 -> 141.4 MB (one group, 7 pieces instead of 6 charts, pages -4.2 MB). All PASS lines hold, pixel check maxima stay 0 (top level) with the same mip 2/3 statistics. OutputVersion 11 (the pinned golden stages are unchanged). Fixed on the way: when the pieces lose, tuples without pieces share their chart objects with the merged run, so the merged layout is laid out again to restore their positions (a stale position failed the albion pixel check once).

## 2026-10-23: lower mip levels of the repack pages come from the sources' own mips

Before, a shared repack page was encoded from a canvas holding the sources' top levels, and its lower levels were the box filter of that canvas, except that, when at least one level was block-copied, the levels from the first non-copied one up to log2 of the alignment were overwritten with the sources' own mip crops (in-bounds chart rectangle only, not the rounded-up gutter, and nothing at all for pages with no block-copied level). Encoded blocks (gutter/padding, partly covered edges, misaligned crops, deeper levels) therefore mostly came from a box filter of the canvas, which differs from the sources' own mips wherever the artists' mip generation was not a plain box filter. Block-copied blocks were, and are, the sources' own bytes.

Now every level 1 and up of the encoder input is seeded, chart rectangle by chart rectangle with its whole reserved gutter, from the source's own decoded mip of the same level (same-level mapping as the block copier and the pixel check; a group's slot exponent equals its sources', so no shift), wherever the rectangle's source and page origins are whole texels at that level. Gutter texels therefore come from the source around the piece, never from a neighbouring piece. Fallback (the box filter of the canvas, as before) per (rectangle, level): origin not on a whole texel (deep levels beyond the alignment grid), source chain too short, rectangle smaller than one texel, exponent mismatch; a trailing partial texel row/column and texels outside every rectangle also keep the box filter. Pages and rectangles never overlap on a page, tight pieces carry their own copy of the source texels, and the iteration order is fixed, so the result is deterministic. Block-copied blocks, page sizes, layouts, UVs, file names and every set/group decision are unchanged (no gate reads encoded bytes); only the 61 (teb) and 39 (albion) generated repack .dds files differ. The report prints the seeded and fallback counts per sheet and in total, and, for the seeded texels, how far the box filter had been from the source mip (mips 1-3 and 4+). Output version 12 (golden hash unchanged: the repack is not a pinned stage).

Measured (bench teb / albion): seeded pairs 2,400 of 4,447 / 1,970 of 3,759; fallbacks 2,017 / 1,760 non-integer positions, 29 / 29 smaller than a texel, 1 / 0 beyond the source's mips. The box filter differed from the source mips at the seeded texels (largest channel) by a mean of 6.3 / 9.1 at mips 1-3 (5.0 % / 5.8 % over 16) and 10.3 / 14.1 at mips 4+ (12.4 % / 16.6 % over 16); these include texels inside block-copied blocks, which the copy overwrites anyway. The pixel check only samples mips 2 and 3, which the block copy already makes exact (or, for the emissive noise copy, which the earlier overlay already seeded), so its figures are unchanged (max 0 at the top level; mip 2/3 same as before). Bench wall time was within noise (teb 156 s -> 162 s, albion 93 s -> 95 s).

## 2026-10-24: per-group smaller padding in the shared repack

Each layout group tries gutter 8/alignment 8 (P2), one-cell gutter/alignment 16 (P1), then the baseline one-cell gutter/current alignment (P0), in coarsest-slot pixels shifted by the group downshift and clamped to the reference dimensions. Identical per-tuple gutter/alignment variants are skipped; the smallest page-byte result wins and ties favor the safer, later option. P1 keeps mip levels 0–2 block-copied, P2 keeps 0–1; the first remaining level is encoded from each source's own mip. A pixel-check retry can lower a failing group's padding; attempt-local report state is restored before replanning so counters and simulation time reflect only the final attempt. Groups below the savings gate or closed by a shared texture remain authored.

Measured against the `*-srcmips` runs: `!ak_teb3` emitted 25/3/4 P0/P1/P2 groups and had 10 gated/untouched groups inside emitted sets (8/1/1); three groups cleared the group gate only with reduced padding. It built 28 sets (24 baseline), saved 125.238 MB versus 116.500 MB (+8.738 MB), and took 181.6 s of process time. Albion emitted 19/0/4 P0/P1/P2 groups and had 17 gated/untouched groups inside emitted sets (16/1/0). It built 20 sets (20 baseline), saved 143.960 MB versus 141.383 MB (+2.578 MB), and took 84.7 s. Neither pack retried after a pixel check. The TEB shared-repack stage took 21.894 ms (39.380 ms in the first padding run; 21.038 ms in `teb-srcmips`). P1/P2 texture outputs changed only for switched groups; P0 atlas textures remained byte-identical, and every output pack file hash matched its first padding run. Switched-group pixel checks passed: TEB's maximum mip 2/3 error was 173 (0.20% over 96); the Prophetess/Mage legs P2 group was max 170, mean 0.33 and 0.01% over 96. Albion's maximum was 198 (0.48% over 96), under the 0.5% limit. Output version 13; golden output 13 matches 12.

## 2026-10-24: mixed-material closure losses, measured; merging with earlier dropped sets tried and discarded

"A bound material also uses textures outside the set" (a part's material binds a texture of another signature set, so the part cannot be rewritten and closure drops it, often cascading until nothing is left) is the largest single closure cause. Commit `c5d5b6ce1` added a report-only section, "Mixed materials", that gives each such texture loss the root cause of the outside texture (ranked: cannot be re-encoded, blocked or not repackable, unknown or in no set, via a dropped part, set processed before this one, merge cap, merging off in the alone retry with the reason the merged attempt failed), and a copy what-if: each affected set planned again alone with every re-encodable outside texture cropped into it as a copy (the original stays for its other users, so the copy is a cost), up to the closure's re-estimate. Output unchanged (pack fingerprints identical).

Measured (estimated savings; built sets realise about 66 % of their estimate):

- Copy what-if: teb 2 dropped sets would reach the 1 MiB threshold (Louen body, Volkmar; 3.5 MB estimated, about 2.3 MB realistic), albion 3 (mostly Drycha's mask and base colour sets, which overlap; 9.8 MB, about 6.3 MB realistic) and +0.8 MB in built sets. Not worth building: small, overlaps, and a copy only saves memory when the original's other users are not in the same battle.
- Root causes (closure-dropped sets, estimate share): merged attempts that failed (teb 15.6 MB, albion 19.2 MB: no part left after closing the merged set, pages not smaller, or the actual-bytes gate), the outside texture's set processed earlier (albion 16.1 MB, teb 2.4 MB), merge cap (albion 3.4 MB), and causes no merge fixes (BC7, unreadable header, legacy-slot use, vanilla override: about 5 MB teb, 9 MB albion). In built plans (texture bytes kept as authored): merge cap teb 6.3 MB, albion 18.9 MB; teb 26.2 MB through one Gashnag body part with a `t_xml_emissive_colour` texture whose drop takes `commontextures\snow_material_map.dds` out of the Blazing Sun torso set.

Tried and discarded (not committed): let a plan take in a set processed EARLIER that wrote nothing (dropped, below threshold, failed a gate), not only later ones; built seeds and sets already merged stayed unavailable, and the earlier set's outcome moved to "merged into another set's plan" so the funnel reconciled. Result on both packs: identical savings, sets and funnel (teb 125.238 MB / 28 sets, albion 143.960 MB / 20 sets), 0 earlier sets joined a later plan. The "processed earlier" losses all reappeared as failed merged attempts (albion: actual-bytes gate 11.8 MB, pages not smaller 9.6 MB, no part left 7.9 MB; teb: no part left 14.5 MB). The links between sets are symmetric: the earlier set had already tried the same merge from its side when it was processed, and the reverse merge fails for the same reasons. So the lever is why merged plans fail (the actual-bytes gate, pages that do not shrink, closure of the merged set), not the processing order.

## 2026-10-24: failed merged attempts, and models loaded without a WSModel

Commit `7f18e877f` added a report-only "Failed merged attempts" section (each failed merge with its joined sets and the root causes of its losses). Of the 12 failed merges on the two packs, the causes were: models loaded directly without a WSModel (albion Chaos warhorse set, 17.8 MB estimated, through `elo_albion_chariot_01`; teb Reiksguard, also blocked by a BC7 texture), estimates the real layout does not reach (pages not smaller or under 1 MiB: albion Drycha and orc savage, teb Repanse body and head, Arch Lector), slots that cannot be atlased (`t_xml_emissive_colour`: Gashnag body part, Louen; colour map), gameplay consumer discovery incomplete (albion warband, Jade wizard, 6.8 MB), and vanilla rigids or WSModel variants (teb Volkmar, Flagellants).

Directly loaded models: a VMD `VARIANT_MESH` or a DB engine asset (`battlefield_engines.model` → `warscape_animated_lod_tables`) names the `.rigid_model_v2` itself. Cases: teb `ak_teb_sisters_hood_down` and `ak_teb_sisters_hooded_02` (`teb_sisters.variantmeshdefinition`), `ak_teb_ricco_torso` (`teb_ricco_ricco`); albion `high_questing_knights_head_01` (`albion_hearthguard_2h`, `albion_highlander_ror`) and `warmachines\chariot\tmb_basic_chariot\elo_albion_chariot_01` (engine `albion_chariot`). All have a same-named `.wsmodel` beside them; in vanilla, 80 of the 102 warmachines rigids named directly in `warscape_animated_lod_tables` do too. The shared repack report now names the loaders of such a rigid in its closure note (`[loaded directly by …]`).

In-game test (`!!!aa_test_hood_red.pack`): same-named WSModels overridden to turn the hoods of `hood_down` and `hooded_02` (loaded directly) red, and the hood and face of `hooded_01` (loaded through its WSModel by the same VMD) blue. Only blue showed. **The game draws a directly loaded model with the material embedded in the RMV2 file and ignores the same-named WSModel.** Supporting these models therefore means rewriting the embedded material's texture paths, not the WSModel.

Embedded weighted materials list the modern textures (BaseColour, Normal, Mask, MaterialMap, the same paths as the WSModel) plus legacy Diffuse, Specular and Gloss entries. Per `Wh3ShaderTextureChannels.json` (425 compiled shaders), `t_xml_specular` and `t_xml_gloss_map` are sampled by no shader and `t_xml_diffuse` only by `rigid_baked_fluid_simulation` (alpha); `weighted2/4_character(_alpha)` sample only base colour, mask, material map, normal, dither, distortion and distortion noise. The legacy entries are not atlas users, but must follow a generated page if the same source path is replaced through a modern slot. Not built yet: estimated gain about 17.4 MB on albion (mostly the warhorse set) and 3.5 MB on teb, about two thirds of that realistic.

## 2026-10-24: shared repack rewrites directly loaded rigids

Shared texture repack now permits direct VMD/DB consumers when the source-owned rigid is otherwise safe to rewrite. Direct paths stay unchanged; the embedded material is updated alongside any bound WSModel. Exact texture compaction retains its old direct-reference block. Embedded atlas users are accepted only for `ModelMaterialEnum.weighted`, whose supported `weighted2/4_character(_alpha)` shaders sample the four mapped modern slots (BaseColour, Normal, Mask, MaterialMap). Unknown material types or slots, unreadable materials, and a same-part slot/path mismatch fail closed and drop that part. Direct materials using only the sampled legacy `Diffuse`/`Specular`/`Gloss` paths were not observed; the bench weighted materials carry those entries as separate legacy paths. They do not join the atlas user set; a legacy path is redirected only when the same source path is replaced by a generated page, otherwise it stays unchanged. Retry/undo snapshots include all embedded texture paths.

Unit signature construction previously only saw WSModel materials. It now includes the four embedded modern slots for direct rigids, so their textures affect unit signatures and estimates. Direct-only roots (a rigid named by a VMD or a DB engine asset that reaches no WSModel) are appended after the WSModel-reached roots, so they never shift unit indices; generated names hash the unit indices, and shifting them had renamed 162 albion files without changing any content.

Rules:

- Only `ModelMaterialEnum.weighted` is accepted. `default_type` (68) is rejected ("embedded material type default_type is not verified (only weighted is)"): its header layout is the weighted one and it is what the tools write for unskinned meshes, but no shader mapping for the enum exists in the repo, and the dead-legacy-slot finding is proven only for weighted. All 7 teb and 3 albion parts dropped for the type are `default_type` (props such as shields, a bow, a mace, and parts of `elo_albion_chariot_01_destroyed`). Accepting it needs an in-game test of one such prop.
- Legacy `Diffuse`/`Specular`/`Gloss` entries of weighted materials are not atlas users and block removal of owned bytes; a legacy path is redirected only when the same source texture is replaced by a generated page. Post-stage validation checks every embedded path of every source-owned direct rigid (rewritten or not) that resolved in the source still resolves in the output or vanilla.
- A bound WSModel of a direct rigid is ignored ("[unused WSModel left as authored]", no mismatch check, left unchanged) when a gameplay root cannot draw it. With complete discovery that is "no root reaches it". With partial discovery it also needs: no root and no incomplete root in the index reaches it, the resolver is healthy, no incomplete root's raw bytes (Latin-1 or UTF-16) contain its file name (an unreadable root blocks), and no other source file mentions it. Otherwise the rigid stays ineligible and the report names the root or file ("incomplete root X mentions it", "source file Y mentions it"). Before this, a WSModel that no root reaches counted as "incomplete" because the index fails closed on assets it has no root for.
- "Directly loaded rigid outcomes" lists only source-owned rigids, one line each with a reason (rigid-level ineligibility, else part drop reasons, else per-set outcomes). Rigids the pack does not own are counted per reason. The summary lines print only when the feature is on.

Measured (`teb-direct3`, `albion-direct3`, OutputVersion 14):

- TEB: 475 considered sets, 33 shared repack sets built (28 in step 2), 130,174,832 bytes saved (step 2: 125,238,064), 12 direct rigids rewritten: `teb_borgio_ship`, `teb_lupo_ship`, `ak_lucy_circlet`, `ak_lucy_long_hair`, `ak_teb_bandaged_pilgrim`, `ak_teb_guard_kossars_acc_tasset`, `ak_teb_bandolero`, four `ak_teb_rondels_*` (loaded among others by `teb_sisters`) and `teb_gausser_scarf`. Direct rigid outcomes: 594 considered = 12 rewritten, 14 embedded parts dropped, 15 rigid not eligible, 110 not rewritten, 443 not owned by the source pack. The Sisters hoods did not build: their 94%-covered charts pack to a layout 16 bytes larger than the source. Against step 2 the output differs by 46 added / 34 removed files and 23 modified (6 WSModels, 17 rigids); the rest is identical.
- Albion: 295 considered sets (211 in step 2), 20 built, 143,960,472 bytes saved, no direct rigid rewritten. The output is byte-identical to step 2 (0 added, 0 removed, 0 modified). The chariot `elo_albion_chariot_01` is no longer ineligible (its unused WSModel is now ignored), but its 7 sets are below the threshold or dropped by closure; see the next section.
- Both: WHMM `pack validate --full-payloads` reports the same non-texture diagnostics for source and output (teb 20 INVALID_XML, 15 IGNORED_WSMODEL_BINDING, 2 UNSUPPORTED_RMV2_PART, 1 RIGID_LOADER_SCAN_LIMIT; albion 16 INVALID_XML, 14 IGNORED_WSMODEL_BINDING, 2 UNSUPPORTED_RMV2_PART); texture-missing counts differ because vanilla packs were not supplied. The host checks pass: teb "155 directly loaded rigid(s) keep all 3,333 source-resolved embedded texture paths", "12 directly loaded rigid(s) have 206 rewritten embedded material texture path(s)"; albion "19 ... keep all 664".
- Wall time: teb 205.6 s, albion 119.0 s.
- In game (teb): the rewritten rondels (Sisters of Fury), Lucrezzia's circlet and hair, Lorenzo Lupo's ship and Gausser's scarf look correct.

## 2026-10-24: the albion chariot cannot be repacked profitably (investigated, nothing changed)

The 17.8 MB estimate of the failed merged attempt seeded by `chs_warhorse_01_base_colour.dds` (pre-step-3 reports) was never real. That set's single unit was the campaign-map VMD `campaign_alb_chariot` (`cam_alb_chariot` plus horses and charioteer), not the battle unit, and the estimate did not see the battle rigid `elo_albion_chariot_01`'s embedded use of the same textures. With that use counted (step 3), the best chariot set is `emp_blazing_sun_head_01_base_colour.dds` (3 units: campaign VMD, battle rigid, destroyed rigid; 5.5 MB estimated), and it fails too.

Per part of `elo_albion_chariot_01` (lod 0), from an instrumented albion run (instrumentation reverted):

- Pinned by textures that rigids the pack does not own also use: part 0 (`emp_luminark_*`, 6 vanilla luminark rigids), part 2 (`grn_grom_chariot_01_*`), part 4 (`tmb_chariot_main_normal.dds`, 11.18 MB, 12 vanilla rigids, mostly Arkhan's chariot; it shares a material with `albion_chariot_base_colour` and `tmb_chariot_main_material_map`). Copying the 11.18 MB normal map into an atlas costs more than the part would save.
- Banner (part 3) and war shield (part 5) sets: saving 0.
- Movable: only the `emp_blazing_sun_head_01_*` textures of part 1 (2.8 MB). They are blocked by `elo_albion_chariot_01_destroyed` parts with `default_type` materials using all 6 of the set's textures; the merged attempt with the luminark set also fails (unowned rigids, merge cap on `tmb_chariot_main_normal`). The `brt_trebuchet_base_colour.dds` set (6.3 MB estimated) has the same `default_type` block and shares `brt_trebuchet_normal` with 3 vanilla rigids.

Realistic bound: 1–2 MB, and only if `default_type` were accepted (in-game test needed). Not pursued.

Rejected alternatives:

- Joining an engine-asset rigid to its land unit's battle VMDs (all four chariot land units share engine `elo_albion_chariot` and mount `albion_warhorse_chariot`): it does not restore the old set, whose unit was the campaign VMD, and the pinned parts stay pinned.
- Letting a set below the 1 MiB threshold seed a merged plan (SharedTextureRepack.cs, the `MinimumSavingBytes` check in the set loop): the chariot's only seedable set is the pinned one, and the rule would touch 218 of 295 albion and 372 of 475 teb considered sets (21.6 MB / 35.7 MB estimated, 5.5 % / 7.8 %), mostly tiny gains with extra output files.

## Open issues

1. **Joint slots exceeded five alternatives — fixed.** The writer created one `SLOT` with one wrapper per Cartesian combination, plus duplicates for optional-slot probabilities; the engine used five, and rewritten units visibly lost variety in-game. Writer-created slots are now split into at most five `VARIANT_MESH_REFERENCE` entries to generated group VMDs (`NestCrossRigidWriterSlots` in `PackTextureAtlasBatchService.CrossRigid.Emission.cs`), with a hard check at the end of emission. On this pack (combined mode): 71 slots nested, largest 3,000 alternatives, nesting depth up to 6, all splits exact, 6,542 group files reduced to 1,986 by dedupe; validation passes and the rewritten units look correct in-game. Author-written slots are left alone; three in the source pack list six alternatives (`ak_est_trooper_base` head, `ak_morion_pirazzo` bighead, `bor_strooper_base` head).
2. **The portfolio search no longer improves results** (baseline = best in every run since the per-payload ceiling) and optimises a different gate than the final writer. Kept at the author's request.
3. **Ten tests in `Test.KitbashEditor` fail** (`LoadAndSave_Rome2_*`, `Warhammer3_SaveKarl_*`, `ArgumentOutOfRangeException: length ('-1')`). Not investigated; ignored at the author's request.
4. **Blood density is unchecked** after the shared texture repack. The `blood_uv_scale` division is an assumption; check a repacked unit with blood enabled.
5. **The repack estimate is optimistic.** Built savings were 5.6 of 16.8 MB (demigryph body) and 6.2 of 11.9 MB (lion), so the Option C figures in the per-unit estimate should be read as upper bounds.
6. **The "stays loaded anyway" probability is not computed** for vanilla texture cropping, so its 127 / 217 MB figures are upper bounds.
7. **Repack savings on vanilla textures are upper bounds.** Vanilla units in the same battle keep the original textures loaded.
8. **BC7 textures are not compacted.** There is no exact BC7 decoder in the codebase (21 textures on `ak_teb3`, 10 on `ovn_albion`).
9. **The shader channel table is tied to one game build** (Patch 8.1). Regenerate it after a patch that changes shaders. `weighted2_random_colours_alpha` is missing from the dump, so textures it uses are never alpha-dropped.

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

### 2026-10-12 to 15: texture memory

- Texture downscale (`PackTextureAtlasBatchService.TextureDownscale.cs`): report plus opt-in "Downscale oversized textures (experimental)", with the lord/hero rule (unclassifiable units count as lord/hero). Off by default.
- Vanilla texture coverage (`PackTextureAtlasBatchService.VanillaTextureCoverage.cs`): report only.
- Per-unit repack estimate (`PackTextureAtlasBatchService.UnitTextureRepack.cs`): report only, Options A, B, C, C2 and a selective view.
- Shared texture repack (`PackTextureAtlasBatchService.SharedTextureRepack.cs`): opt-in, first increment. New "Repack shared textures (experimental)" checkbox in `TextureAtlasProgressWindow.xaml`, off by default and disabled only in cross-rigid-only mode (it works next to atlasing and cross-rigid merges).
- Tests: the `FullyQualifiedName~PackTextureAtlas` filter now runs 504 tests, all passing.

### 2026-10-15 to 16: multi-rigid repack and exact compaction

- Shared texture repack extended to sets that span several mod-owned rigids. Flat textures of any size pass through.
- Signature sets that share a material are merged into one plan (up to 8 sets and 16 rigids, with a fallback to the seed set).
- Page splitting puts whole clusters on up to 4 smaller power-of-two pages.
- Exact texture compaction (`PackTextureAtlasBatchService.TextureCompaction.cs`): new opt-in checkbox. It counts xml material slots and the texture lists embedded in rigids as references.
- Shader channel table (`Wh3ShaderTextureChannels.json`, generator in `Tools/ShaderChannels/`) and the opt-in "Drop texture alpha no shader reads" rule. The repack's rigid checks were moved into a shared helper (`GetRigidNotOnlyThroughOwnedWsModelsReason`).
- Bone weight audit (`PackTextureAtlasBatchService.BoneWeightAudit.cs`): report only, runs in every mode except cross-rigid-only.
- Tests: the `FullyQualifiedName~PackTextureAtlas` filter now runs 589 tests, all passing.

## Recommendations

1. Do not build the shared-atlas planner or a per-configuration writer.
2. Stop extending the cross-rigid search.
3. The five-alternative limit is fixed; consider leaving cross-rigid off by default anyway, since it adds about 2,000 small VMD files and tens of MiB of geometry for roughly 0.2 % FPS.
4. Keep same-rigid merging and atlasing; they reduce parts per soldier without rewriting VMDs.
5. If more performance is wanted, look at per-soldier GPU cost (for example vertex count at distance), which the tool does not address. Reducing 4 bone weights to 2 was measured and rejected (see "Bone weights per vertex").
6. Texture work is memory only; do not expect FPS from it.
7. Keep texture downscaling, shared texture repack and exact compaction opt-in.
8. Do not build per-texture reverse atlasing or per-unit atlases (Options A and B).
9. Exact compaction is the cheapest memory win, with no visual risk. Enable it whenever a pack is processed. Dropping unread alpha is equally safe as long as the shader table matches the game build.
10. Do not copy vanilla rigids to unlock more repack sets; only 3 rigids block sets today.
