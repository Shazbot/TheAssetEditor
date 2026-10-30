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

- Only `ModelMaterialEnum.weighted` is accepted. `default_type` (68) is rejected ("embedded material type default_type is not verified (only weighted is)"): its header layout is the weighted one and it is what the tools write for unskinned meshes, but no shader mapping for the enum exists in the repo, and the dead-legacy-slot finding is proven only for weighted. All 7 teb and 3 albion parts dropped for the type are `default_type` (props such as shields, a bow, a mace, and parts of `elo_albion_chariot_01_destroyed`). Accepting it needs an in-game test of one such prop. (Superseded 2026-10-25, see "default_type embedded materials are rewritten like weighted".)
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

## 2026-10-24: gameplay discovery was partial because of malformed VMDs

Diagnosis (albion-direct3 / teb-direct3, temporary instrumentation):

- Every rigid lost to "gameplay consumer discovery for the rigid is incomplete" on albion (60) was a rigid with no root at all in the dependency index; none was reached by an incomplete root. Each is named only by its own bound WSModel, no root reaches that WSModel, and no incomplete-root bytes, source text file or DB table mention it: dead assets. Teb has 21 such rigids, 6 with no root and 15 reached by the two source-pack VMDs `teb_amazons` and `teb_vespero_vespero`.
- The index fails closed on an asset it has no root for, but only while any root is incomplete (`IsGameplayConsumerDiscoverySafeForAsset` returns true once discovery is complete). The incomplete roots were 10 on albion and 11 on teb, all genuine XML problems:
  - Albion source pack `alb_cav_noble_spear`: a stray `</SLOT>` after `weapon_5`.
  - Vanilla `cth_clawspeaker_campaign_01-03` and `cth_sawai_campaign_01-05` (also on teb): the `<SLOT ... name="stump_neck">` opening tag is missing, leaving a bare `<VARIANT_MESH model=...>` and an extra `</SLOT>`. They only name `hu23/cth` and blood-pack stump WSModels.
  - Vanilla `lzd_skink_priest_avatar` (also on teb): `VARIANT_MESH_REFERENCE definition` naming `lzd_skink_club_01.wsmodel`.
  - Teb source pack: `teb_amazons` has `model="...AK_spellsingers_amazon_legs_01"` and `teb_vespero_vespero` has `.../emp_blazing_sun/head/wutwutwut`, both without an extension.
- The earlier estimates for the warband (`alb_warband_body_basecolor2`, 4.19 MB) and Jade (`albi_mask`, 2.62 MB) sets were pre-closure and optimistic: once discovery no longer blocked their rigids, the warband set closes with all 6 textures but saves 24 bytes, and the Jade set saves 699,072 bytes, still below the threshold.

Rule (`PackTextureAtlasBatchService.Traversal.cs`): when strict parsing of a VMD fails during gameplay consumer discovery, its `model`, `imposter_model` and `definition` attribute values are read from the raw text (BOM, UTF-8 or UTF-16), provided the text contains `<VARIANT_MESH` and ends with `</VARIANT_MESH>` (a truncated file may hide references and stays incomplete). The root counts as complete and is listed in the report ("parsed leniently: ..."). A `VARIANT_MESH_REFERENCE definition` naming a `.wsmodel` or `.rigid_model_v2` is a model reference, strict or lenient. A value without an extension is read as stem + `.rigid_model_v2`, `.wsmodel` and `.variantmeshdefinition`, each that resolves; unresolved ones are only reported. Only discovery uses this: every other stage still goes through the strict parser, so such a VMD stays a malformed root or child for them exactly as before, and validation checks that every leniently parsed source VMD is byte-identical in the output.

Measured (`teb-disc`, `albion-disc`, versus direct3): both packs report "Structural merge consumer discovery: COMPLETE" and 0 incomplete roots.

- Teb: output identical (6,515 files, 0 added, 0 removed, 0 modified); 130,174,832 bytes saved.
- Albion: shared repack 143,960,472 -> 145,271,120 bytes (+1,310,648, three more rigids rewritten: 39 -> 42); texture downscale 6 -> 9 textures (`alb_damsel_01_head_base_colour` and `_mask` 2048 -> 1024 and `high_flagellants_head_01_base_colour` 1024 -> 512, 6.82 MB of texture bytes); structural mesh merging 43 -> 45 groups (`alb_flagellants_legs_01`, LOD 2 and 3, 2 parts fewer). Against direct3: 172 files added, 164 removed (88 texture renames by content hash plus 6 new textures and 2 more materials), 52 modified (43 WSModels, 6 rigids, 3 textures). Wall time 125.3 s.

## 2026-10-25: the late texture downscale run pins the source-state reference density

Diagnosis (merge-cap measurement, temporary instrumentation): raising the shared repack's merge caps to 2x made the albion output 0.86 MB larger although the repack saved 0.70 MB more. The late downscale run derived its reference density (75th percentile of part texel densities) from the parts still assessable on the output, so the parts that atlasing or the repack rewrote left the sample. `alb_damsel_01_head_base_colour` and `_mask` (24 users, least dense 1622.9 texels per world unit) sit on the 0.5x threshold: reference 809.54 (today's caps) halved them, 816.70 (2x caps) and the early run's source-state 816.75 kept them at full size. Users and densities were identical in all three runs; only the reference moved.

Rule (`RunTextureDownscale`): when the early analysis ran and has a reference, the late run uses it (report: "taken from the early analysis on the source state"). The atlas stage's single global value still wins; without an early run the late run derives its own as before. Test hook `TextureDownscaleReferencePinDisabled`. OutputVersion 16.

Measured (`teb-pinref`, `albion-pinref`, versus disc):

- Teb: reference 991.98 -> 971.36, same 7 candidates; output byte-identical.
- Albion: reference 809.54 -> 816.75; the two damsel head textures are no longer downscaled (9 -> 7 candidates, 6.29 MB texture bytes, pack +1.04 MB). Shared repack unchanged (145,271,120 bytes). WHMM validation diagnostics equal to albion-disc. The 6.3 MB was a side effect of the drift, not a decision on the source pack.
- Measured on the way, nothing changed: the merge cap (8 sets, 16 source rigids, 64 generated per plan) costs nothing on teb (2x and uncapped: identical savings) and 0.70 MB on albion; copying textures that leave a plan instead of dropping their parts would gain about 13.4 MB estimated on teb and 2.1 MB on albion where a copy pays, but loses 14.2 / 6.6 MB if every leaving texture is copied, so it needs a per-set with/without comparison.

## 2026-10-25: copies of textures that leave a repack plan

Problem: the closure drops a part it cannot repack and removes every texture the part uses from the set, so each other part using one of those textures fails "a bound material also uses textures outside the set" and is dropped in turn. Teb: Gashnag's body part has a non-atlasable emissive slot and shares `brt_louen_leoncoeur_body_01_normal` with Louen's body, so Louen's set ended with no part left.

Rule (`PlanSharedRepackSet`, `SharedRepackMergeContext.CopyRemoved`): in a copy-mode plan a texture that left the plan because a dropped part uses it (`removedBecause`) is copyable when it is not blocked, not uniform and its effective layout can be re-encoded (BC1/BC3, power of two). The parts that stay keep it as a cropped COPY in their pages (an `IsCopy` output of the slot's normal channel suffix, cropped to the charts of the staying parts); the original is not replaced, stays in the output and is used as authored by the dropped part. Textures of other sets, vanilla textures and anything never in the plan are not copied. The copy slots are per plan (`BuildSharedRepackTuples` gets the plan's copied textures; `SharedRepackPart.CopySlots`, cached across attempts, is untouched). A general copy lays out like a normal slot (may be the reference, raises the chart alignment, block-copies the same levels); only the emissive noise copy keeps its own rules (`SharedRepackTuple.IsEmissiveCopySlot`, `EmissiveCompensation`). A copied texture is counted nowhere as replaced: a group's source bytes and the set's Before/After are net of the copies, the closure re-estimate and its gate include the cropped copies. Direct rigids' embedded materials map a copied slot to the copy output; legacy Diffuse/Specular/Gloss entries naming a copied texture follow it to the copy too (the UVs of the part changed, so the original would be sampled wrongly; deviation from "keep unchanged"). Validation: every copy output's source must still be in the output (owned) or resolve (vanilla).

Both ways: when the normal plan dropped a part only for such textures (`CopyRescuable`), the set is planned again in copy mode and the plan with the larger exact saving (replaced chain bytes minus page bytes) is built, a tie keeps the normal plan. The losing plan's report state, geometry checks, new part-cache keys and chart assignments are undone, so when copies do not win the output and the report are as without them. A copy plan that cannot be built falls back to a normal plan on the next retry. Kill switch `SharedRepackCopyRemovedDisabled`. OutputVersion 17.

Measured (`teb-pinref` / `albion-pinref` against `teb-copy2` / `albion-copy2`):

- Teb: realised repack saving 130,174,832 -> 138,301,016 bytes (+8,126,184); sets built 33 -> 36; 7 sets planned both ways, copies won in 3, all of them sets that were not buildable without copies: Gelt body (copy of `emp_balthazar_gelt_body_01_normal`, 349,712 bytes, saves 4,893,264), Repanse (2 copies, 131,436 bytes, saves 1,834,856), Louen body (copy of `brt_louen_leoncoeur_body_01_normal`, 699,216 bytes, saves 1,398,064). Pixel equivalence 1,804,224 samples, max error 0. Pack file 598,108,637 -> 599,040,286 bytes (+0.93 MB: the repack saves video memory, the file only gains the new sheets). Shared repack phase 33.2 s -> 39.5 s (total 296.8 s alone).
- Albion: 145,271,120 -> 146,669,208 bytes (+1,398,088); 2 sets planned both ways, copies won in 1 (amethyst wizard set, 1 copy of `wef_bladesinger_body_01_material_map`, 174,924 bytes, saves 1,398,088 more). Pack 652,424,747 -> 652,833,027 bytes. Repack phase 18.9 s -> 22.8 s (total 132.7 s alone).
- The estimate-only what-if promised 13.4 MB on teb and 2.1 MB on albion; the real feature gets 8.1 and 1.4 MB because it only copies textures that left this plan (the what-if copied any outside texture, including textures of other sets, e.g. Volkmar's, flagellant heads, wight king). Lupio's demigryph accessories, Blazing Sun torso, the bear armour and the sail set were planned both ways and tied (the copy plan was the normal plan: nothing to rescue), so the normal plan was kept and their output is unchanged; sets where copying everything lost are untouched.
- WHMM `pack validate` diagnostics equal to the pinref packs in both cases (teb: MISSING_RMV2_TEXTURE 59,556, IGNORED_WSMODEL_BINDING 30, RIGID_LOADER_SCAN_LIMIT 2, UNSUPPORTED_RMV2_PART 4; albion: 28,930 / 28 / 0 / 4).

## 2026-10-25: uniform textures kept as authored no longer fail directly loaded rigids

Problem: `BuildSharedRepackTuples` moves a uniform tiny texture (a 4x4 mask) from the tuple's `Slots` to `ConstantSlots`, so no page output exists for it and the generated WSModel material keeps it as authored. A direct rigid's embedded material that names it still needed a page output in `ApplySharedRepackSetPlan` and failed the whole set ("no generated t_xml_mask page containing ... was planned for embedded material ..."). On teb that killed all 4 "not buildable" sets (72.6 MB estimated, 16 % of all considered): the sail set (`teb_agent_ship_01`, mask `mar_land_ship_01_mask`), `tze_ship_base_colour` (`teb_lupo_ship`, `ogr_greasus_platform_01_mask`), Colombo's head set (`lzd_tehenhauin_head_01_mask`) and Grombrindal's cape set (`dwf_grombrindal_cape_01_mask`). All four masks are vanilla 4x4 BC3.

Rule (`ApplySharedRepackSetPlan`, embedded mapping): a modern embedded slot whose texture is the tuple's `ConstantSlots[slot]` stays unchanged in the embedded material, like the generated WSModel material. The part keeps the full slot map, the tuple drops constants, so the test is exact path equality with the tuple constant. Legacy Diffuse/Specular/Gloss entries are unchanged (they only follow a texture that moved to a page). The other embedded checks need no change: the embedded/WSModel agreement check compares both sides to the same untouched path, the pixel check samples page outputs only.

Second bug found by the fix: `ValidateSharedTextureRepack` checked each rewritten embedded slot at its source (lod, part) in the final rigid; mesh merging renumbers parts afterwards (`teb_borgio_ship`, parts 13 and 15), so the post-atlas validation threw. It now maps the part through `BuildDirectRigidOutputPartMap` like the other direct-rigid validations. OutputVersion 18 (golden [18] equal to [17]: the pinned stages do not include the repack).

Measured (`teb-constmask` / `albion-constmask` against `teb-copy2` / `albion-copy2`):

- Teb: realised repack saving 138,301,016 -> 196,103,720 bytes (+57,802,704, 30.3 % -> 43.0 % of the estimate); sets built 36 -> 40; not buildable 4 -> 0. The four sets built with actual savings 40,020,648 (sail, merged plan, 3 rigids, estimate 51.2 MB), 9,393,480 (Lupo ship), 4,194,288 (Colombo, only `ak_colombo_sotek` rewritten, the head and body parts with other textures stay dropped) and 4,194,288 (Grombrindal cape, `ak_teb_venator_keg`). Pixel check: top-level max error 0 on every set (the Lupo ship set: 117,840 samples; mip 2/3 max 83, 0.00 % over 96). 16 rigids rewritten through embedded materials (12 before). No set fails with an embedded-material message; the 16 actual-bytes-gate failures are the same as before. Pack 599,040,286 -> 598,847,732 bytes (the replaced textures leave). File diff: 6 rigids changed, 25 sheets added, 4 replaced textures removed, nothing else.
- Albion: byte-identical to copy2 (pack and fingerprint; it has no such sets).
- WHMM `pack validate --with-vanilla`: diagnostics identical to copy2 (teb: MISSING_RMV2_TEXTURE 9,702 with the same path/message pairs, 2 UNSUPPORTED_RMV2_PART, 2 INVALID_RIGID_LOADER_REFERENCE, 1 RIGID_LOADER_SCAN_LIMIT).

## 2026-10-25: dissolve shaders in the shared repack

Problem: the 14.68 MB `ksl_elemental_beast` set of albion (15 units, centaur hero heads) was dropped by the closure because its vanilla materials (`ksl_elemental_beast_weighted2/4_alphaoff_dissolve`, shader `weighted4_character_dissolve_emissive`) bind `t_xml_dissolve_detail` and `t_xml_dissolve_map`, which are no atlas slots. 494 vanilla materials use the slot: `t_xml_dissolve_detail` = `commontextures/lava_heatmap_mask.dds` (2048 x 2048 DXT1) and `t_xml_dissolve_map` = `commontextures/noise_colour.dds` (1024 x 1024 DXT1) in 493 of them; `dissolve_uv_scale` is 1,1 (485), 0.5,0.5 (8) and once a malformed `0.3`. `dissolve_amount` is 0 in the materials (the engine animates it), so the effect is only visible at run time.

Shader evidence (Patch 8.1 dump, branch `shaders` of wh3dump, `Tools/ShaderChannels/verify_dissolve_uv.py` on the disassembled DXBC): in every pixel variant that binds them
```
m = sample(t_xml_dissolve_map,    UV0).r                         // raw UV0, same interpolator as the base colour
d = sample(t_xml_dissolve_detail, UV0 * sp_dissolve_uv_scale).r  // float2 parameter, per axis, nothing else
n = smoothstep((m + d) / 2) + 0.1, compared with sp_dissolve_amount (burn edge, clip, glow)
```
The tool reuses the symbolic evaluator of `verify_emissive_uv.py` (now also accepting the dissolve map as a raw-UV0 slot and requiring the detail UV to be exactly `UV0 * sp_dissolve_uv_scale` per axis; for families that bind `t_xml_emissive_distortion` the emissive chain must verify too). A family qualifies only if all its variants and every existing `_alpha`/`_blood`/`_alpha_blood`/`_clean_alpha` sibling verify (the engine can swap siblings), and not with a `vfx_` prefix. Result over the 48 families that name a dissolve texture: 44 qualify (all `weighted2/4_character_dissolve*`, `rigid_character_dissolve*` incl. the iridescent emissive twins, `rigid/weighted2/weighted4_daemon_prince*`, `weighted2/4_dragon_dissolve*`), 28 of them with a verified emissive chain (added to `SharedRepackEmissiveVerifiedShaders`); `non_weighted/weighted2/weighted4_saytang_dissolve_alpha` (base colour not at the raw UV0) and `weighted2_ethereal_dissolve_vfx` (a scroll texture depends on UV0) stay blocked.

Rules (`SharedRepackDissolveVerifiedShaders`, `GetSharedRepackDissolveReason`):

- A material with a verified shader and a float2 `dissolve_uv_scale` keeps `t_xml_dissolve_detail` as authored (pass-through: never cropped, never a set member, never replaced or pruned) and every generated material gets `dissolve_uv_scale` divided per axis by the tuple's UV scale (scaleU, scaleV), exactly like `blood_uv_scale` (`TryScaleSharedRepackFloat2Parameter`, invariant culture, "R"). Unequal U and V scales are fine, there is no aspect restriction (the detail is a plain per-axis multiply, unlike the emissive chain). Logged per material in the report ("dissolve: ...").
- `t_xml_dissolve_map` becomes a noise COPY slot like the emissive distortion: cropped per chart into its own output (`<name>_repack_dissolve_map.dds`), never a set member, never in the closure, never uniform-classified, never replaced or pruned, keyed `copy:<path>` in the group key, its bytes count as cost in the page and the actual-bytes gate, pixel-checked. It needs no parameter compensation (the shader samples it at the raw UV0) and no aspect restriction. It follows the emissive noise's layout rules (`IsNoiseCopySlot`: never the reference, not part of the chart alignment, the cell is a whole number of noise texels) rather than the general copy rules: the noise is a coarse texture shared by all models, forcing the chart alignment of the block grid at every noise mip level would raise the alignment of every page by the noise downshift for no gain (the top levels block-copy exactly, the pixel check judges the rest), and the emissive copy already proves the scheme. Both noise copies and the emissive scroll compensation work in one part: dissolve+emissive materials build with three special slots.
- Not verified shader: new part reason "the material's shader is not verified for dissolve repacking (<shader>)"; missing, malformed or non-float2 `dissolve_uv_scale`: "the material lacks dissolve parameter dissolve_uv_scale". Both appear in the report's "Dissolve: ..." block like the emissive reasons.
- ASSUMPTION (check in game): the detail pattern is exact up to a constant per-chart phase shift (the chart offset times the scale), the same assumption as the emissive scroll and the blood overlay. The map copy is exact apart from the coarse mip levels (noise only; see the pixel check).

OutputVersion 19 (golden [19] equal to [18]: the pinned stages do not include the repack).

Measured (`albion-dissolve` / `teb-dissolve` against `albion-constmask` / `teb-constmask`):

- Teb: byte-identical (same pack size 598,847,732, identical fingerprint; no dissolve set there).
- Albion: the `ksl_elemental_beast` set is built (4 textures, 15 units, 6 rigids, 24 parts; actual 16,777,264 -> 8,650,952 bytes, saves 8,126,312 against the 14,680,000 estimate). It has two layout groups: the dissolve tuple (base, mask, material map, normal at 1024 x 1024 plus a 512 x 512 dissolve map copy of 174,776 bytes and a 256 x 256 emissive distortion copy) and the tuple of the plain centigor materials (same four textures, no dissolve map). The four shared pages are therefore written twice with identical content, which costs about 4.2 MB of the saving. Realised saving 146,669,208 -> 154,795,520 bytes (+8,126,312), sets built 20 -> 21; pack 652,833,027 -> 656,798,690 bytes (+3,965,663: the sources are vanilla textures, the pack only gains files, the saving is video memory). `dissolve_uv_scale` 1,1 -> 0.5,0.5 in both generated dissolve materials (UV scale 2 x 2). Pixel check of the set: top level 228,744 samples, max error 0; mip 2/3 111,248 samples, max 77 (not gated), 99th percentile 28, 0.00 % over 96. PASS lines identical (8, no failure). Files: 15 added (11 textures, 4 generated materials), 2 original centigor materials pruned, 12 changed (6 rigids, 6 WSModels). Wall time 98.0 s alone (103.9 s before).
- WHMM `pack validate --with-vanilla`: diagnostics identical to the constmask output (MISSING_RMV2_TEXTURE 7,212 with the same path/message pairs, 2 UNSUPPORTED_RMV2_PART, 2 INVALID_RIGID_LOADER_REFERENCE, 1 RIGID_LOADER_SCAN_LIMIT).

## 2026-10-25: identical pages of layout groups on the same geometry are written once

Problem: one set plan can hold several layout groups whose tuples sit on the same mesh UVs (a tuple with a dissolve map copy and a plain tuple, or two base colours of different format sharing the normal and material map). The groups lay out the same charts at the same places, so the pages of the slots they share are byte-identical, were written twice under different names and counted twice as cost (albion `ksl_elemental_beast`: 5 pages, 4.24 MB; the `vir_lord_cape` / `wef_orion_body` set: material map + normal, 2.10 MB; teb `ak_teb_manflayers_head_01` normal, 350 KB).

Rule (`DeduplicateSharedRepackOutputs`, run on the final plan before the actual-bytes gate): two outputs of a plan are duplicates when `GetSharedRepackOutputSignature` is equal, i.e. everything `TryComposeSharedRepackOutput` encodes from: slot, copy-ness, format, sRGB, DX10, exponents, size, mip count and, per tuple that has the slot, its source texture, source exponent, reference size and alignment, and every chart rectangle on the page (source crop, layout size, place), sorted (rectangles never overlap). The first output in plan order (groups by key, pages, slots) is canonical; a duplicate takes its path, so every generated material, embedded rigid path, legacy entry and copy output names the canonical page, and its bytes leave the page, group and set totals. The duplicate is still composed, and before the pixel check its bytes must equal the canonical's (a mismatch fails the set with "the duplicate signature is wrong"; the run keeps the other sets); then it is neither written nor listed. The per-group gate (`group would not get smaller`) still judges each group on its own pages: it is a quality check of one layout, and counting a page another group owns would make a group's fate depend on its neighbours and on the restart order. Validation accepts a material pointing at another group's page, fails a generated page name that is not a written page of its set, and fails a written page no generated material or rewritten embedded material references. Report: per set and total "deduplicated N page(s), B bytes". Names of the remaining outputs are unchanged.

OutputVersion 20 (golden [20] equal to [19]: the pinned stages do not include the repack).

Measured (`albion-dedupe` / `teb-dedupe` against `albion-dissolve` / `teb-dissolve`):

- Albion: 7 pages deduplicated (5 + 2, 6,335,280 bytes); realised saving 154,795,520 -> 161,130,800 (+6,335,280); pack 656,798,690 -> 654,142,270; sets built 21 (unchanged, no gated set newly passes); generated textures 108 -> 101. Files: the 7 duplicate pages are gone, 4 generated materials changed (they name the canonical pages), nothing else differs. Remaining identical generated files: only the uniform masks (3 files, later 4x4 BC3). PASS lines identical, 8.
- Teb: 1 page (349,584 bytes); 196,103,720 -> 196,453,304 (+349,584); pack 598,847,732 -> 598,685,535; 40 sets; 171 -> 170 textures; 1 texture removed, 2 materials changed. Only uniform masks remain identical.
- WHMM `pack validate --with-vanilla`: diagnostics identical to the dissolve outputs (albion MISSING_RMV2_TEXTURE 7,212, teb 9,702, same (path, message) pairs; 2 UNSUPPORTED_RMV2_PART, 2 INVALID_RIGID_LOADER_REFERENCE, 1 RIGID_LOADER_SCAN_LIMIT).
- Wall time: albion 149.0 s (167.6 s before), teb 211.4 s (223.7 s); the duplicate is still composed, so no speed-up is claimed (runs are noisy).

## 2026-10-25: texture variants of one mesh part (variant pages)

Problem: a part bound by several WSModels whose materials name different textures in the same slots (`alb_highlander_ror_pike_01`: `pikes_01` / `pikes_02` base colour, one shared 2048 x 4096 `glb_atlas_01` normal and material map at 1.1 % coverage; teb barbutes: four base colours over one normal and material map) was refused whole ("WSModel bindings resolve to different textures"), which dropped every set it touched (albion `glb_atlas_01_material_map.dds`, 16.5 MB estimated; teb `generic_metal_material.dds` + `ak_teb_barbute_base.dds`, 2.8 MB left).

Rule (`CreateSharedRepackPart`, `AddSharedRepackVariantOutputs`): a part keeps its slot map per bound material (`MaterialSlots`); the first distinct map by text is the layout map (`Slots`, used for `TupleKey`, `GroupKey` and the charts), the others are `VariantSlots`, and parts with equal layout and variant maps form a tuple. The layout is computed once, so a part keeps one UV transform. For a slot where a tuple has variant textures, each variant texture gets one extra output of the layout page's size and chart positions: the tuples that have it as a variant crop their charts from it, every other tuple from its layout texture (`SharedRepackSlotOutput.VariantSources`, `SourceOf`). A generated material names, per slot, the output of its own texture (`GetSharedRepackMaterialOutput`), so each WSModel binding of a variant keeps its own generated material and every slot of it points at crops of that binding's texture; slots with equal textures in all bindings keep one page (the layout page; identical variant pages of other groups go through the output dedup). When every tuple's layout texture is some tuple's variant, the layout page is unreferenced and dropped. Variant outputs are named `<layout name>_v<hash of page, slot, texture>`, composed, block-copied, pixel-checked (the tuples cropped from the variant texture, against that texture) and counted in the page, group and set bytes, so the group gate and the actual-bytes gate see them; the split estimate counts them as one more page per variant texture. Variant textures are members (`AllMemberTextures`: Before, Replaced, the shared-texture exclusion propagation); the closure already pulled their signature sets in through `part.Textures`. A variant texture whose signature set differs from the layout texture's goes into the tuple's `GroupKey`, so a variant page never holds crops of textures its units never loaded.

Still refused, each with its own part-drop reason: a slot some bindings do not have ("bound by only some of the part's WSModel bindings"), a copied slot (emissive distortion, dissolve map) with different textures, a variant of another size / mip count / format than the layout texture or not repackable (names the slot), a uniform colour in one binding and a spatial texture in another (names the slot), and a directly loaded (embedded-material) rigid with variant bindings. Two different uniform colours are fine: the variant's generated material keeps its own.

OutputVersion 21 (golden [21] equal to [20]: the pinned stages do not include the repack).

Measured (`albion-variants` / `teb-variants` against `albion-dedupe` / `teb-dedupe`):

- Albion: the `glb_atlas_01_material_map.dds` set is built as a merged plan with the two base colour sets (3 sets, 4 textures, 4 units, 4 parts over 4 LODs of `alb_highlander_ror_pike_01`): actual 19,573,496 -> 611,816 bytes, saves 18,961,680 (estimate 19,267,568); one layout group of one tuple, one chart, 128 x 512 base colour and 256 x 1024 normal and material map; 1 variant page (43,868 bytes, the `pikes_02` base colour). `alb_highlander_ror_pike_02.wsmodel` binds a generated material naming the `_v1098c164` base page and the shared normal and material map pages. Pixel check: top level 3,840 samples (960 on the variant page), max error 0; mip 2/3 1,920 samples, max 19 (not gated), 99th percentile 15. Realised saving 161,130,800 -> 180,092,480 (+18,961,680); sets built 21 -> 22; pack 654,142,270 -> 653,355,678; generated textures 101 -> 105; `alb_highlander_pikes_02_base_colour.dds` (owned) pruned. The hounds sets (`hounds_01` / `hounds_02`) estimate 0 bytes (full coverage) and stay below the threshold. PASS lines identical (8; counts differ), no part is left with the old variants reason.
- Teb: the `generic_metal_material.dds` merged plan (with the barbute base, brass, brassband and ricco sets, 3 rigids, 12 parts, 44 units) is built: actual 4,893,432 -> 3,145,912, saves 1,747,520 (estimate 3,670,000), two layout groups, 3 variant pages (1,049,076 bytes), 8 parts with variants, 28,056 variant samples of 69,840 top-level samples, max error 0. Realised saving 196,453,304 -> 198,200,824 (+1,747,520); sets built 40 -> 41; pack 598,685,535 -> 597,999,596. The `emp_reiksguard_zintler_torso_01_normal.dds` set stays dropped (a directly loaded rigid, not variants); the `tattersouls_head_01_base_colour.dds` merged attempt still fails on one part whose mask variant has another size (`texture variant differs ... slot t_xml_mask`). Remaining variant refusals in the sets considered: 1 layout (mask), 1 directly loaded rigid; albion none.
- The merged plan of `ak_colombo_head_base_colour.dds` (teb) now pulls in three more signature sets whose parts are dropped for other reasons afterwards, so its generated page names changed (same bytes, the direct rigid names the new paths); the outcome is identical.
- WHMM `pack validate --with-vanilla`: diagnostics identical to the dedupe outputs for both packs (albion MISSING_RMV2_TEXTURE 7,212, teb 9,702, same (path, message) pairs; 2 UNSUPPORTED_RMV2_PART, 2 INVALID_RIGID_LOADER_REFERENCE, 1 RIGID_LOADER_SCAN_LIMIT).
- Wall time: albion 167.1 s (153.2 s before), teb 202.0 s (214.7 s).

## 2026-10-25: which shader draws a directly loaded rigid (default_type investigated, nothing changed)

Question: the repack accepts embedded materials of directly loaded rigids only for `ModelMaterialEnum.weighted` and refuses `default_type` (68), because no shader mapping for the enum was known. The refusal costs about 20.2 MB estimated on teb (5 dropped sets) plus 8.4 MB of textures left out of built plans, and 7.2 MB on albion (2 sets).

Executable evidence (Warhammer3.exe, Patch 8.1, found by the author): the RMV2 loader (RVA 0x1B26F20) passes each mesh record's material block at record + 0x50 to the material parser (RVA 0x1B20024), which copies its first uint16 into the material object (+0x88). The shader dispatcher (RVA 0x1B254C8) selects on that value: 0x04 -> `shaders/system/weighted_standard_4.xml.shader`, 0x0E -> `weighted_standard_8`, anything else -> `weighted_standard_2`; `shaders/system/rigid_standard.xml.shader` is the rigid fallback (RVA 0x1B07568). The binary contains no `default_type` or material-type string. In our parser (`RmvCommonHeader`) the common mesh header is exactly 0x50 bytes (0x18 of fields, a 0x18 bounding box, 0x20 shader params), so record + 0x50 is the first field of the material block, `_vertexType` (`VertexFormat`: 4 = Cinematic, 4 bones), while `ModelMaterialEnum` sits at record + 0x00. **The engine picks the direct-load shader by vertex format, not by material type: a `default_type` and a `weighted` mesh of the same vertex format get the same shader.**

Shader evidence: the four system shaders compile from `FX/weighted.hlsl` and `FX/rigidstandard.hlsl` (`fxc/fx_weighted_hlsl_*`, `fx_rigidstandard_hlsl_*`; the engine swaps in `fx_weightedwithblood`, `fx_weightedwithburn`, `fx_rigidstandardblood`, `fx_rigidstandardfire` at run time). Their resources are `t_base_colour_map`, `t_normal_map`, `t_material_map`, `t_colour_mask_map` (the four modern embedded slots), `t_decal_blood_map`, `t_fire_map`, the skin path's `t_soft_normal_map` / `t_curvature_and_translucency`, and engine globals. New tool `Tools/ShaderChannels/verify_system_uv.py` (reuses the symbolic evaluator of `verify_emissive_uv.py`) checks every pixel variant of those six families (572 compiled blobs): all pass.

- The four map slots are sampled at the raw UV0. The vertex shaders copy UV0 into several interpolators (`TEXCOORD1 = uv.xyxy`, `TEXCOORD4.xy = uv`) and the weighted pixel shaders read the normal map from `TEXCOORD1.zw` and the mask from `TEXCOORD4.xy`; the tool treats two pixel inputs as equal only when every vertex shader whose output layout matches the pixel shader writes both with a plain `mov` from the same input component.
- `t_decal_blood_map` reads UV0 (the existing blood assumption).
- CAVEAT, burning: the burn/fire variants sample the common `t_fire_map` at `UV0 * (1.2, 1.5)` plus a time scroll (`0.05 * time_in_sec` on V). The constants are in the shader, not material parameters, so on a repacked direct rigid the burn pattern gets denser by the page's UV scale (2x typically) and shifts per chart while the unit burns. Cosmetic, no compensation possible; this already applies to the `weighted` direct rigids the repack rewrites today.
- The skin path (20 variants per weighted family) reads `t_soft_normal_map` and `t_curvature_and_translucency` at UV0. They are per-model textures only if the embedded material names them; the repack already refuses embedded materials with slots other than the four modern ones and the legacy Diffuse/Specular/Gloss entries, and otherwise the engine binds its defaults.
- Found on the way: `verify_emissive_uv.py`'s evaluator stores a register holding 0 as an empty polynomial and reads it back as undefined (`read`: `getreg(...) or undef`). That only makes it stricter (an expression with such a term never matches a required form); `verify_system_uv.py` reads it as 0. The committed emissive and dissolve allowlists were not regenerated.

Consequence (not implemented yet): `default_type` can be accepted with the same embedded slot mapping as `weighted` (same shaders for the same vertex format). Remaining unknown: whether the engine applies `ModelMaterialEnum` anywhere else (decal/dirt material types have their own FX variants such as `fx_weightedwithdecal*`, not checked); an in-game look at one rewritten `default_type` prop settles it.

## 2026-10-25: default_type embedded materials are rewritten like weighted

Rule (OutputVersion 22): the verified embedded material types of a directly loaded rigid are `weighted` and `default_type` (`IsSharedRepackVerifiedEmbeddedType`); the same set exempts the legacy Diffuse/Specular/Gloss entries from the unsupported-slot refusal (repack) and makes them owned-removal blockers (coverage). Other types (decal, dirtmap, weighted_decal, weighted_dirtmap, weighted_skin, cloth, ...) stay refused: "embedded material type X is not verified (only weighted and default_type are)". New guard: a direct embedded part is accepted only with vertex format Static, Weighted or Cinematic (the formats the dispatcher evidence covers); any other refuses the part ("embedded vertex format X is not verified (only Static, Weighted and Cinematic are)"). No existing weighted part hit the guard (0 in both benches; the only output differences are the expected ones). Exact texture compaction keeps its own direct-reference rule. The report counts rewritten parts per embedded type and prints the burn caveat (`t_fire_map` at UV0 x shader constants; burn pattern density follows the page UV scale) whenever a direct rigid was rewritten.

Measured (variants -> default_type; teb / albion):

- Realised saving 198,200,824 -> 222,547,352 bytes (+24.3 MB) / 180,092,480 -> 189,442,136 (+9.3 MB); built sets 41 -> 44 / 22 -> 24; direct rigids rewritten 16 -> 23 / 0 -> 4; parts rewritten: default_type 28 + weighted 143 / default_type 11 + weighted 13. Pack size 597,999,596 -> 599,938,669 / 653,355,678 -> 654,277,667 (new pages are added; the replaced originals mostly stay for other users, 9 of 20 on teb).
- teb newly built: `brass.dds` set (borgio mace; 14.2 -> 1.8 MB), `brt_men_at_arms_shield_mask` (conquistador/state shields 08), `empire_grenade_launcher_base_colour` (captain buss); the merchant staffs and the peasant bow joined sets built before. Pixel checks max error 0 at the top level, mip 2/3 max 44, none over the gate. Still dropped: `ak_richter_shield` (rigids not owned), the dragon ogre hammer set (tuples left untouched).
- albion newly built: `brt_trebuchet_base_colour` (merged plan, 5.59 -> 0.35 MB, built with one copy) and `emp_blazing_sun_head_01_base_colour` (merged plan, 5.59 -> 1.49 MB); both include the chariot rigids. Pixel checks max error 0, mip 2/3 max 19.
- Remaining refusals: embedded type 0, vertex format 0 on both. WHMM `pack validate --with-vanilla`: identical diagnostics to the variants packs (teb 9,707, albion 7,217; no new code or MISSING_RMV2_TEXTURE pair). Fingerprint diff vs variants: teb 18 files added, 8 removed (renamed pages and materials of two merged plans), 9 changed (7 rigids, 1 wsmodel); albion 11 added, 6 changed (5 rigids, 1 wsmodel).

In-game check list (not done): look at a rewritten `default_type` prop in battle, with and without burning. Teb: `ak_teb_borgio_mace` (teb_borgio VMD), `est_conquistador_shields_08` / `til_state_shields_08` (est/til shield-set VMDs), `ak_teb_captainbuss_01` (merc captains), `teb_merchant_staff_01/02` (til_merchant), `cth_peasant_bow_2h_01_rigid` (teb_guard_kossars). Albion (engine assets, the rigid is its own consumer): `elo_albion_chariot_01(_destroyed)`, `alb_stonethrower(_destroyed)`. Expected: textures and normals identical to the source, no stretching; if a prop looks wrong, `ModelMaterialEnum` matters beyond the shader pick and `default_type` goes back to refused.

## 2026-10-25: verified weighted flow-overlay slots in the shared repack

Shader evidence: the committed `Tools/ShaderChannels/verify_slot_families_uv.py` run against the full compiled-shader dump selected 11 flow-overlay families; 8 passed every pixel-shader variant and linked vertex-shader proof: `weighted2` and `weighted4` `flow_overlay`, `_alpha`, `_blood`, and `_alpha_blood` (80 PS variants total). The linked vertex shaders were checked for their model-UV input use, including shadow entrypoints. The rigid family stays refused because the available shader metadata does not prove the rigid point-shadow input layout. `emissive_flow_alpha` stays refused because its noise scroll speed is fixed; the weighted2 variant also samples `flow_texture` from UV0 without a compensatable tiling parameter. The full dump had 10 `random_colours` candidates and none passed. Ordinary bound-slot passes prove `mask1` uses raw UV0 and `colour_map` coordinates are UV-independent, but an alternate `ps_main_distortion` pass samples GUID textures through a UV-dependent chain, so whole-family repacking is refused. No random-colour slots were enabled. `weighted2_random_colours_alpha` is absent from the dump.

The existing emissive and dissolve verifier reruns produced the same committed allowlists (74/74 and 44/44). This comparison uses their existing selection and proof rules. It does not establish safety for alternate passes that lack their expected UV anchor; the new slot-family verifier now checks pass coverage and provenance fail-closed for its own allowlists.

Runtime rule (OutputVersion 23): only the eight verified weighted shader paths above receive flow handling in the shared repack. A model-local `t_xml_flow_map` joins atlas closure; every material-part user must be rewritten or already handled by the earlier atlas stage. An unsupported or unassessable user prevents replacing the shared map. A verified user's copy plan may still crop the map while retaining the original for that user. A `commontextures` flow map is always a cropped copy, and a uniform map stays authored. The GUID slot `t_xml_df7959d8_d174_49d2_88f1_ffc28b954e5f` is a cropped copy. `t_xml_flow_noise`, `t_xml_flow_normal`, and `t_xml_flow_normal_2` stay authored. Copy sources are not replaced or pruned, and copy bytes count toward output cost. These slots and suffixes are local to the shared repack; the global atlas-channel and downscale stages did not gain flow handling.

A flow material is eligible only when `flow_noise_speed` and `flow_noise_tiling` are finite scalar `float` values and `flow_texture_tiling` is a finite `float2` with exactly two nonempty components. Missing, duplicate, mistyped, malformed, or nonfinite values produce a specific refusal. Equal U/V UV scales are required even when the flow map is uniform or copied. For equal scale `s`, the repack writes `flow_noise_speed * s`, `flow_noise_tiling / s`, and each `flow_texture_tiling` component `/ s`; unrelated parameters, including the authored spelling `flow_distortion_stength`, are retained. Only verified flow materials in a mixed part are rewritten.

The proof supports preserving noise density and speed and the flow-normal tiling. It does not support pixel-exact flow rendering: chart offsets shift the procedural noise phase, and the sampled noise feeds offsets in the normal UV chain, so the resulting normal coordinates can differ spatially. Check the effect in game. The Albion Giant RoR club material uses `rigid_flow_overlay`, which remains refused and is not a valid in-game test for this subset; a weighted-flow unit/VMD must be identified from a built set. The report records per-set and total flow atlases, copies, parameter rescaling, equal-axis cost, and flow-specific refusal reasons.

Bench outcomes against the matching default-type runs (same inputs and options):

| Pack | Built sets and realised saving | Flow result | Output pack |
| --- | --- | --- | --- |
| TEB | 44 sets; 222,547,352 bytes saved; 2,254,656 sampled texels, max error 0 | No flow atlas/copy/rescale; 3 `emissive_flow_alpha` refusals | `K:\projects\assedfork-bench\teb-slots\bench_output.pack` (599,938,669 bytes) |
| Albion | 24 sets; 189,442,136 bytes saved; 1,654,876 sampled texels, max error 0 | No flow atlas/copy/rescale; 1 `rigid_flow_overlay` refusal | `K:\projects\assedfork-bench\albion-slots\bench_output.pack` (654,277,667 bytes) |

Both output payload fingerprints matched their default-type baselines exactly (TEB 6,564 files, Albion 4,660; no additions, removals or changed payloads). The pack hashes are TEB `939a9bf3a25362a55378f91322a1bb6d6ef8351fc6ba34d9a10b1657c90d90bc` and Albion `8114ff24fd86f243cda8b61e6b355e9cf69cb6c83c3ee294067cf98d551d2b72`. Bench wall times were 230.2 s for TEB and 115.5 s for Albion. The run reports are under `K:\projects\assedfork-bench\{teb-slots,albion-slots}\bench_output_report.txt`; WHMM output validations were saved at `/tmp/codex-flow-runtime/teb-validation.json` and `/tmp/codex-flow-runtime/albion-validation.json`. Both are `valid:true`, `validationComplete:false`; their full diagnostic multisets, missing-texture pairs, and vanilla provider order match the default-type baselines. These inputs therefore show no practical saving change from flow support. No suitable weighted-flow target was produced by these bench runs or identified in review, so weighted-flow rendering remains untested in game. The Albion Giant RoR club uses the refused rigid shader and is not a substitute.

OutputVersion 23 pins the same golden fixture hash as version 22 (`8B7093023427E55FBF1CD767BA5C4B782EB218E82B78C68131B02BF8A58ECAE6`), because that fixture contains no flow-overlay material.

## 2026-10-26: BC7 textures in the shared repack

The shared repack now accepts BC7 (BC7_UNORM and BC7_UNORM_SRGB) next to BC1 and BC3. Before, any set holding a BC7 texture, or a material that bound one outside the set, lost its parts ("BC7 cannot be re-encoded").

- **Decode.** The repack's own decoder (Pfim, `TryDecodeDdsTopLevel`) decodes BC7 byte-identically to DirectXTex (checked in `DirectXTexShimTests`), so no decoder change was needed. The pixel check and the chart canvas therefore see the same texels for BC7 as for BC1/BC3.
- **Block copy first.** The per-level block fates (Copied, Gutter, Partial, Misaligned, Generated, ...) are now computed before encoding (`TryBuildSharedRepackBlockCopyPlan`). A chart that shrinks below one texel at a level marks its footprint Generated, which is visible content, not gutter. The real copy pass must reproduce the plan's Copied blocks exactly or the output fails.
- **Gathered encoding, two quality classes.** BC7 is encoded only for the blocks that are not copied, in 256-block strips, with one `Parallel.For` across all levels (`TryEncodeSharedRepackChainGathered`; BC1/BC3 keep the whole-page encoder). Gutter blocks (padding between charts) use `BC7_QUICK`; every other non-copied block (Partial, Misaligned, Generated, ...) uses the default `TEX_COMPRESS_FLAGS`. The reason is cost: the DirectXTex shim has no OpenMP, so it is single-threaded, and default BC7 runs at about 900 blocks/s per core against about 18,000 for quick. A whole 2048x2048 page (about 350,000 blocks with mips) at default quality takes over 30 minutes on one thread, but in the two BC7 page sets measured 96 to 99 % of the blocks are copied or gutter (lucy page: 3,496 copied, 1,915 gutter, 52 default; sling page: 8,302 copied, 2,098 gutter, 525 default). Tests check each gathered block is byte-identical to the same block of a whole-page encode with the flags of its class.
- **Mixed formats.** A page never mixes BC7 with BC1/BC3: the tuple group key includes each slot's format and sRGB flag, and variant pages require equal formats. A BC7 and a BC1 texture in the same slot therefore land in separate groups.
- **Still refused.** Non-power-of-two sizes and owned overrides of vanilla textures, as before. Downscale (`TryReencodeDds`) and exact compaction still treat BC7 as not re-encodable and skip it; both unchanged.

Bench outcomes against the `-slots` runs (same inputs and options, OutputVersion 24):

| Pack | Built sets and realised saving | Wall time | Output pack |
| --- | --- | --- | --- |
| TEB | 44 sets; 226,392,072 bytes (was 222,547,352); 2,258,720 sampled texels, max error 0 | 244.9 s (was 230.2 s); shared repack 36.8 s (was 30.1 s) | `K:\projects\assedfork-bench\teb-bc7\bench_output.pack` (600,062,597 bytes; was 599,938,669) |
| Albion | 25 sets (was 24); 193,112,120 bytes (was 189,442,136); 1,669,324 sampled texels, max error 0 | 126.9 s (was 115.5 s); shared repack 26.1 s (was 22.7 s) | `K:\projects\assedfork-bench\albion-bc7\bench_output.pack` (654,499,716 bytes; was 654,277,667) |

TEB: the `ak_lucy_body` rewrite now keeps 16 of its parts (was 12): the BC7 `teb_lucrezzia_head_diffuse.dds` set builds as a 256x256 BC7_SRGB page (base colour, material map) next to BC3 mask and normal. Albion: the `3k_animated_props_replacements_01` set builds (4.19 MB to 0.52 MB, 512x256 BC7_SRGB base colour and material map); `alb_warmaiden_body_01` and the waystone sets join two merged plans, which changes those plans' pages and generated names. WHMM validation of all four packs gives identical diagnostic code counts and identical `MISSING_RMV2_TEXTURE` (path, message) pairs.

OutputVersion 24 pins the same golden fixture hash as 23, because the repack is not among the pinned stages.

## 2026-10-26: merge caps 8/16/64 -> 32/48/64, and barbute pages written once

Why the caps existed: introduced with the merged plans (commit `996647d75`, "A merged plan stops growing above this many signature sets or rigids"); no measurement or reason is recorded. They bound the plan's closure and layout work and the size of one plan's report. The 2026-10-25 measurement (2x and uncapped) found the cap costing nothing on teb and 0.70 MB on albion; with BC7 and the variant pages it now costs more (albion 28.66 MB estimated in 6 sets plus 4.19 + 3.44 MB, teb 4.89 + 1.40 MB).

Experiment (caps overridable by an env var for the runs only, removed; sets / source rigids / generated rigids; realised repack saving in bytes, sets built, plans that hit the cap, shared repack phase, total wall, peak working set; timing and memory are noisy between runs, a rerun of the baseline in the same session is the comparison):

| Caps | teb saving | sets | cap hits | phase s | wall s | peak MiB | albion saving | sets | cap hits | phase s | wall s | peak MiB |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 8/16/64 (baseline, rerun) | 226,392,072 | 44 | 1 | 33.6 | 169.5 | 6204 | 193,112,120 | 25 | 9 | 24.9 | 114.7 | 5335 |
| 16/32/128 | 227,091,048 | 44 | 0 | 33.1 | 183.9 | 4071 | 196,366,896 | 24 | 1 (2.80 MB) | 25.8 | 115.1 | 4417 |
| 32/64/256 | 227,091,048 | 44 | 0 | 33.9 | 178.8 | 4582 | 196,366,896 | 23 | 0 | 24.3 | 109.8 | 4673 |
| 1000/1000/1000 | 227,091,048 | 44 | 0 | 32.1 | 164.4 | 4746 | 196,366,896 | 23 | 0 | 24.2 | 111.4 | 4903 |
| 32/48/64 (chosen) | 227,091,048 | 44 | 0 | 34.2 | 169.4 | 6129 | 196,366,896 | 23 | 0 | 24.6 | 106.6 | 5279 |
| 32/32/64 (albion only) | | | | | | | 196,366,896 | 24 | 1 (1.40 MB est.) | 23.7 | 108.8 | 4618 |

The largest plan that pays is 26 sets in 42 rigids (albion); teb needs 10 sets. 32 sets / 48 source rigids recover everything the uncapped run does (+3.25 MB albion, +0.70 MB teb); 16 sets and 32 rigids each still cap one albion plan. The generated-rigid cap (64) was never reached in any run, so it is unchanged. No new failure mode: pixel-check failures stay 0, actual-bytes-gate failures stay 9 / 17, no set that built before fails (two albion seeds, `alb_half_giant_head_01_base_colour` and `alb_oathsworn_cav_ror_crest_01_base_colour`, are now joined into the larger plan). Time and memory are within the noise.

Barbute pages (teb): the `generic_metal_material` merged plan had two layout groups with the same three layout textures (`ak_teb_barbute_base`, `generic_metal_material`, `ak_teb_barbute_normal`): the flatbrow rigid's parts (no variants) and the nasal/nonasal parts (base colour variants brass, brassband, ricco). The variant textures are loaded by other units than the base (12, 20 and 4 units against 20 for the base, not subsets), so the group key's `<variant set` clause kept the two tuples apart, and the material-map and normal pages (and a second base page) were written twice (699,104 bytes).

Fix (`BuildSharedRepackTuples`, main loop of the set plan, `AcceptSharedRepackJoinedGroup`): the group key no longer has the clause; the strict key (with it) is kept as `StrictGroupKey`. A group whose tuples have different strict keys is joined only if (1) every strict group pays on its own, (2) the joint pages are smaller than the sum of the separate pages, and (3) every variant page of a foreign texture is no bigger than that texture. Otherwise every tuple goes back to the strict key (the old behaviour, same page names). Why it is exact: a material still names the page of its own texture (variant output for its own variant texture, else the layout page) and the charts and UVs are the same construction as before, so no WSModel samples anything else; the crops of the other tuples' layout textures that the variant page now holds in their chart regions are not sampled by any material using that page. A unit that loads a foreign variant texture loads its page instead (condition 3: not bigger), and the units of the layout textures load the same layout pages (same signature set per slot, as before), so no unit loads more than before. The validator's one-signature-set-per-output check is unchanged (a variant output's source is the one texture).

Measured: teb realised 226,392,072 -> 227,790,152 (+698,976 from the caps, 227,091,048, and +699,104 from the barbute pages), one group of 2 tuples, 1 page of 512x1024, 3 variant pages. OutputVersion 25.

## 2026-10-26: shared repack canvases are cut to the packed charts (layout-group limit investigated)

Question: why do so many layout groups miss the 524,288-byte group minimum, and which exact change lets more of them pass? Source: the `albion-mergecap` and `teb-mergecap` reports plus a throwaway per-group dump (pot/trimmed/chart/touched bytes per untouched group, removed again).

Where the pages lose their bytes (measured, report "Page layout"): the 30 untouched groups of albion are 167.8 MB of sources; their power-of-two pages are 172.2 MB, of which 73.8 MB are touched cells, 63.3 MB chart padding/bounding/alignment, 10.5 MB packing gaps, 24.6 MB power-of-two rounding. teb: 37 groups, 202.7 MB of sources, 208.0 MB pages. No untouched group saves more than a few bytes at power-of-two sizes (31 measured groups in albion, 37 in teb; every group's pages are >= its sources, within 32 bytes), so lowering the group minimum gains nothing. 15 of 31 (albion) and 16 of 37 (teb) groups are at 88-100 % chart coverage and cannot shrink by any layout; in albion 11 of the untouched group lines in built sets are only held back by share-closure (a tuple shares a texture with an untouched group), 5 in teb.

Levers, by what the report measures (upper bounds, before set thresholds and set closure):
- Trimmed (non-power-of-two) canvases: albion 8 groups would pass, 20.75 MB; teb 14 groups, 28.16 MB. Largest single groups: fen_chaos_spawn 4.98 MB, ogr hunter body 3.35, staff crystal 3.35, horned man head 3.00, warriors head 2.82 (albion); manfred von carstein 5.94, repanse body 4.54, karl franz head 2.65, grombrindal 2.38 (teb).
- Less padding (gutter 4 + align 4): the packing simulation finds 1 of 30 untouched groups (ogr hunter body 8.39 -> 4.19 MB). Not taken: only mip 0 stays block-copied and the gutter is clean only to mip 2, so mips 2+ would be re-encoded and neighbouring charts bleed from mip 3, which is not the sources' own bytes. Gutter 8/align 8 (P2) and align 16 (P1) are already tried per group.
- Splitting the share-closure: tuples that share a texture either all use the page or the shared texture stays and is paid twice, so a split duplicates bytes. Not pursued.
- Lower group minimum: 0 bytes (no untouched group is smaller than its sources at power-of-two sizes).

Exactness of the trimmed canvas: the pages hold the same chart rectangles at the same positions and padding, only the canvas edge moves in, so every block that was block-copied is still the source's bytes and the gathered encoder produces the same gutter blocks. Block-copied levels keep their grid because the canvas is a multiple of the chart alignment (checked in `TrimSharedRepackCanvas`), and every slot's size is a multiple of 4 and scales back exactly through the slot exponent. Groups with an aspect restriction (`RequiresEqualAxisScale`: emissive, flow and dissolve compensation need equal U and V scales) keep power-of-two canvases of the reference aspect; the first what-if (no restriction) turned the 12.36 MB `ksl_elemental_beast` set into "not buildable: the UV scale is 2.2069 x 2.46154, not equal in U and V". The engine side: WH3 loads non-power-of-two mesh textures with mips in the vanilla data (`variantmeshes\wh_variantmodels\md1\tmb\tmb_scarab_mound\tex\tmb_swarm_anim_{base_colour,material_map,normal}.dds`, 1848x1848, BC1 sRGB / BC1 sRGB / BC3, 11/11/10 mips, in one material), and 200 UI textures are not powers of two; 15,710 variantmesh DDS in `K:/projects/wh3dump` were scanned, 3 are non-power-of-two. That is precedent, not proof for every shader: it needs the in-game check below. Mip levels deeper than the alignment (mip 6 and beyond for alignment 32) cannot be block-copied in either layout; there a non-power-of-two canvas floors its mip sizes, so the chart can sit up to half a texel of that mip off the UV, which only matters for textures drawn at 1/64 size or less.

What-if, then implementation (`TrimSharedRepackCanvas`, used where the pages of `PlanSharedRepackGroup` are created; `SharedRepackTrimmedCanvasDisabled` is the test hook). Applies to built groups too, so already-built pages shrink as well as untouched groups becoming built. OutputVersion 26.

Measured (bench, pack-level, same sources):
- albion: realised 196,366,896 -> 218,073,752 (+21,706,856), 23 -> 25 sets (+`fen_chaos_spawn_01_base_colour`, +`ksl_patriarch_head_01_base_colour`, none lost). The what-if without the aspect restriction gave 205,709,352 (24 sets, `ksl_elemental_beast` lost).
- teb: realised 227,790,152 -> 283,916,280 (+56,126,128), 44 -> 54 sets (+`ak_teb_sisters_body_cloth`, `ak_teb_gashnag_knight_base`, `chs_marauder_chieftain_head_01_mask`, `ksl_kossar_armoured_head_01_base_colour`, `dae_hellstrider_body_01_base_colour`, `def_warriors_legs_01_base_colour`, `emp_arch_lector_head_01_base_colour`, `emp_karl_franz_head_01_base_colour`, `ksl_archer_head_01_base_colour`, `tmb_tomb_king_head_01_base_colour`; none lost).
- Pixel check: 0 sets failed on both; top-level max error 0 in every set; the worst share of re-encoded mip samples over 96 is 0.48 % (albion) and 0.40 % (teb), gate 0.5 %.
- Fingerprint (sorted path+hash vs the mergecap baselines): albion 12 files removed (originals of newly built sets), 51 added (`_repack_` pages and materials), 291 changed (115 pages, 83 materials, 70 rigids, 23 wsmodels); teb 44 removed (3 owned ship textures, replaced originals), 129 added, 436 changed. All file-level differences are in sets that were built or whose pages shrank.
- WHMM `pack validate --with-vanilla`: valid on all four packs, diagnostics identical in code and count before and after (albion 7,212 MISSING_RMV2_TEXTURE + 2 UNSUPPORTED_RMV2_PART + 2 INVALID_RIGID_LOADER_REFERENCE + 1 RIGID_LOADER_SCAN_LIMIT; teb 9,702 + 2 + 2 + 1). The MISSING_RMV2_TEXTURE slots are the legacy slots of embedded rigid materials (gloss, specular, diffuse) that the rewritten rigids already had; the multiset of messages is equal, and none mentions a `_repack_` texture. New codes: none.

Tests: 970 (967 + 3 new), the 8 that pin power-of-two split/packer choices run with the hook. Still open: in-game check, and that the packs the optimizer wrote are refused as sources by a second optimizer run (`GetSharedRepackBlockedTextureReason` requires power-of-two sources).

## 2026-10-27: aspect-restricted canvases are cut too, keeping the reference aspect

The trimmed canvases above passed the in-game check (albion and teb units, campaign ships, battle buildings).

Split scoring with trimmed bytes was looked at first and not built: in the `*-canvas` reports only 3 groups split and 5 rejected a split, and the best rejected split (teb `vmp_master_necromancer_cape`, 3.67 MB vs 4.19 MB) is below the group minimum. What the reports still lose to rounding is in the groups with an aspect restriction (emissive or flow compensation): albion untouched groups 11.6 MB rounding (4 would pass trimmed, 10.2 MB upper bound, e.g. `alb_horned_man_head_01` at 24 % coverage on a full 1024x1024 page), teb built groups 5.4 MB.

Change: `TrimSharedRepackCanvasToAspect` gives such a group the smallest canvas of the reference's aspect ratio that holds the packed charts and passes `TrimSharedRepackCanvas` (alignment and block grid). The UV scale stays equal in U and V, which is all the emissive and flow compensation needs; it already accepts scales that are not powers of two (e.g. 2.2069). Same test hook (`SharedRepackTrimmedCanvasDisabled`). OutputVersion 27.

Measured (bench, vs `albion-canvas` / `teb-canvas`):
- albion: realised 218,073,752 -> 224,482,896 (+6,409,144), 25 -> 27 sets (+`tze_crystal_pillar_01_material_map` (the wyrd druid staff), +`chs_warrior_head_01_material_map`; none lost). Emissive: 4 aspect-restricted groups built (was 2), 10 materials rescaled (was 4), no "not equal in U and V".
- teb: realised 283,916,280 -> 285,624,088 (+1,707,808), 54 sets, same sets; aspect pages e.g. 2048x2048 -> 960x960, 2048x1024 -> 960x480.
- Pixel check: 0 sets failed, top-level max error 0 everywhere, worst re-encoded share over 96 0.48 % (albion) / 0.40 % (teb), unchanged.
- Fingerprint (sorted path+hash): albion 7 removed (6 materials and `alb_staff_crystal_01_emissive.dds`, replaced by the new staff set), 16 added, 34 changed (6 pages, 4 materials, 15 rigids, 9 wsmodels); teb 0 removed, 0 added, 24 changed (14 pages, 6 materials, 4 rigids).
- WHMM `pack validate --with-vanilla`: valid, diagnostic lines identical to the canvas packs (albion 7,217, teb 9,707), none mentions a `_repack_` texture.

Tests: 971 (970 + 1 new); the emissive tight-pieces test now pins 192x192 (pieces) and 336x336 (merged charts) instead of 256 and 512, and its parameter and pixel checks pass on the trimmed pages (checked in game: Estragon's Pariahs, Piggolo II, the wyrd druid staff).

## 2026-10-27: pages are packed for the smallest cut canvas

The layout solver still picks the smallest power-of-two canvas the charts fit and the page is then cut to the charts; the shelf packer fills that canvas's whole width, so the cut mostly removes height. Now each page's charts are packed again for the cut canvas (`SharedRepackRectPacker.TryPackMinimumTrimmedCanvas`): canvases as tall as the power-of-two one and from the widest chart up to its width (32 widths), each with the shelf, MaxRects (short side and area fit) and skyline packers over the three orders; the layout whose cut canvas (`TrimSharedRepackCanvas`, or `TrimSharedRepackCanvasToAspect` for aspect-restricted groups) is smallest wins, and only when it is smaller than the cut of the power-of-two packing. Only positions change, so exactness is as before. Split decisions still use power-of-two page bytes. Test hook `SharedRepackTrimmedPackingDisabled`. OutputVersion 28.

Measured (bench, vs `albion-aspect` / `teb-aspect`):
- albion: realised 224,482,896 -> 249,706,536 (+25,223,640), 27 -> 29 sets (+`vir_forsaken_body_01 _base_colour`, +`wef_drycha_body_01_mask`; none lost), built groups 49 -> 61; built-group packing gaps 13.9 -> 9.7 MB although 12 more groups are built. Bench time 122 -> 176 s (shared repack 32.7 -> 47.8 s, prune 2.1 -> 12.9 s; most of it is the 12 more groups).
- teb: realised 285,624,088 -> 305,880,408 (+20,256,320), 54 -> 56 sets (+`ksl_ice_guard_head_01_base_colour`, +`lzd_skink_chief_accessories_01_base_colour`; none lost), built groups 73 -> 77, packing gaps 26.4 -> 16.3 MB. Bench time 202 -> 206 s.
- Pixel check: 0 sets failed, top-level max error 0 everywhere, worst re-encoded share over 96 0.48 % (albion) / 0.33 % (teb). Tighter neighbours make reduced padding fail the pixel check more often: teb has 3 P2 groups lowered (was 1): `tze_ship` + lupo sails P2 -> P1 (954,120 -> 1,147,280 bytes, the one group that got larger), `ksl_streltsi_body` P2 -> P1 (still 1,609,344 -> 1,162,272), `ksl_bear_armour_medium` P2 -> P0 (unchanged). Retrying the failed padding with the power-of-two packing before lowering it recovers the lupo group but costs more elsewhere; see "2026-10-27: a failed reduced padding is retried with the power-of-two packing first".
- Fingerprint (sorted path+hash): albion 16 removed (materials of newly built sets), 66 added, 191 changed (71 pages, 59 materials, 37 rigids, 24 wsmodels); teb 7 removed (4 materials and the 3 pages of the lupo group's second page, now one page), 22 added, 312 changed (136 pages, 81 materials, 90 rigids, 5 wsmodels). No original texture changed.
- WHMM `pack validate --with-vanilla`: valid, diagnostic lines identical to the aspect packs (albion 7,217, teb 9,707), none mentions a `_repack_` texture.

Tests: 973 (971 + 2 new: the narrower-canvas search and a refused box); the trimmed-canvas pixel test now pins 384x224 / 192x112 instead of 480x224 / 240x112. Checked in game on teb (custom battle, Tilea): Uvetovsk Kossars, El Cadavo, Pirazzo's Lost Legion, Estragon's Pariahs, Borgio the Besieger, Lupio Sunscryer, Pathfinder; the units were found with WHMM `pack diff --affected-units` (without vanilla; with vanilla it hits the 50,000-edge limit and finds none) plus `pack search` and `unit resolve`.

## 2026-10-27: a failed reduced padding is retried with the power-of-two packing first

Tighter chart neighbours from the smallest-canvas packing make reduced padding fail the pixel check more often, and the retry lowered the padding straight away. Now a failure whose page used the smallest-canvas search bans only that combination (padding variant + search) for the group: the variant goes into the group's banned set (`TrimmedPackingDisabledVariants` on its tuples, `FailedTrimmedPackingGroupKeys` on the plan) and the padding limit is not lowered. The page loop skips the search only when the variant being tried is banned, and the existing variant loop in `PackSharedRepackGroup` then keeps the variant with the fewest page bytes among e.g. P2 power-of-two, P1 trimmed and P0 trimmed. A failure of a page that did not use the search (power-of-two, or the search was not taken) lowers the padding as before and keeps the bans. A P0 trimmed failure bans P0 trimmed, so P0 power-of-two is tried before the group fails. The retry loop bound goes 3 -> 6: every pass adds a ban (at most 3 per group) or a lowering (at most 2), so a group needs at most 5 retries. The report lists "Groups kept on the power-of-two packing by pixel check" (final variant is banned) and shows P2 -> P1 for a group whose ban made it choose a lower variant. OutputVersion 29.

An earlier version of this change always retried the same padding with the power-of-two packing and only then lowered it: lupo recovered (-193,160) but `ksl_streltsi_body` stayed on P2 power-of-two (1,609,344 instead of 1,162,272 at P1 trimmed, +447,072), net -253,912 on teb. Banning the single combination lets the byte comparison pick.

Measured (bench, vs `teb-boxpack`; albion was not rerun: it has 0 pixel retries, so the retry path is never taken and its output cannot change, 249,706,536 / 29 sets):
- teb: realised 305,880,408 -> 306,073,568 (+193,160), 56 sets, bench 206 -> 210 s. Lowered groups 3 -> 2 (`ksl_bear_armour_medium` P2 -> P0 and `ksl_streltsi_body` P2 -> P1, 1,162,272 bytes as before); kept on the power-of-two packing: `tze_ship` + lupo sails (P2, 1,147,280 -> 954,120 bytes, -193,160, now 2 pages). Built-group pages 146,621,776 -> 146,428,616.
- Pixel check: max error 0; WHMM `pack validate --with-vanilla --baseline <boxpack>`: not regressed, 0 added, no diagnostics.

## 2026-10-27: chart padding vs bounding (report only)

The "chart padding/bounding/alignment" term of the Page layout report (charts - used) is now split into exactly reconciling parts per chart (cells = touched cells of the chart's bounding box, all in output bytes at the page's bytes per reference pixel): bounding-box holes (box minus touched cells), gutter band (box +/- gutter, clamped to the texture, minus the box), alignment rounding (the rest, up to the layout rectangle). The report prints "(reconciles)" and the top 10 built groups by holes. Output is unchanged (OutputVersion not bumped; the file hashes of the bench outputs match the baselines).

Built groups (page bytes: Albion 141,614,448, TEB 146,428,616):

| | Albion | TEB |
|---|---|---|
| chart padding (old term) | 57,933,964 (40.91%) | 52,507,347 (35.86%) |
| bounding-box holes | 49,345,458 (34.84%) | 43,412,611 (29.65%) |
| gutter band | 8,588,506 (6.06%) | 9,027,822 (6.17%) |
| alignment rounding | 0 | 66,914 (0.05%) |
| packing gaps | 9,696,861 | 16,331,965 |
| upper bound, shape-aware packing (holes + gaps) | 59,042,319 (41.69%) | 59,744,576 (40.80%) |
| estimate, charts as gutter-dilated cell masks (gaps not counted) | 37,063,038 (26.17%) | 31,631,022 (21.60%) |

Left-untouched (gated) groups: Albion holes 17,763,057, gutter 320,593, alignment 13,866; TEB holes 32,703,878, gutter 907,459, alignment 112,302.

The holes are the bulk of the padding (about 85%); gutter and alignment are small. The mask estimate counts each chart as its touched cells dilated by the gutter (in cells), never above the chart: it ignores packing gaps and the extra room that interlocking itself needs, so it is optimistic for the chart term and the holes plus gaps bound is the ceiling. Cell rounding (touched cells vs the area triangles cover) is not modelled.

## 2026-10-27: cell-mask packing simulated (report only)

The "Page layout" block now simulates shape-aware packing (SharedRepackMaskPacker.cs, MaskSimulation.cs): each chart becomes an occupancy mask in packing units (touched cells grown by the gutter, clipped to the texture and the chart rectangle, so a full chart gives exactly its rectangle), and each page's charts are packed greedily (three orders, first fit by row then column, same canvas widths as the rectangle search, same trim rules including aspect and slot block sizes). A page keeps its real size unless the masks give a smaller cut canvas. Output bytes unchanged (fingerprints identical to the padsplit runs, Albion 249,706,536 and TEB 306,073,568 saved, OutputVersion still 29); the simulation costs 0.1 to 0.2 s.

Built groups, page bytes real -> simulated: Albion 141.6 MB -> 129.6 MB, saves 12.05 MB (8.5%), 33 of 61 groups (33 of 62 pages) smaller; TEB 146.4 MB -> 130.7 MB, saves 15.72 MB (10.7%), 53 of 77 groups (54 of 79 pages) smaller. Untouched groups: Albion 0.69 MB (1.0%), 3 of 14 smaller, 2 would pass the 524,288-byte minimum (+1.65 MB); TEB 1.97 MB (2.2%), 6 of 18 smaller, 3 would pass (+1.92 MB). Both before the set threshold and closure. Budget hit on 3 (Albion) and 2 (TEB) built pages. That is about a quarter of the 59 MB ceiling and a third to a half of the 37 MB mask estimate: the greedy packer is weaker than the MaxRects/skyline search the rectangles get, so this is a lower bound.

A real implementation needs: per-chart cell-mask copy (block copy and the encoder per mask run instead of per rectangle), mips built per mask cell (a mip texel straddling two interlocked charts must not mix their texels: the gutter band and the Align unit protect this today only because neighbours are whole rectangles), the UV rewrite unchanged (translation only), and the pixel check to cover the new layout. Places where the gain can shrink: gutter bleed at the mask concave corners, emissive/flow groups with the reference aspect (fewer legal cut canvases), variant pages (one mask layout shared by all variant outputs, already so here), and set-level gates (newly passing groups add to it, not modelled).

## 2026-10-27: charts are packed as cell masks

The simulation is now the packer. In the page loop (`PackSharedRepackGroupLayout`), after the rectangle search and its trim, `SharedRepackMaskPacker.TryPackMinimumTrimmedCanvas` packs the same charts as masks (`BuildSharedRepackChartMask`, three sort orders, same canvases, trim rules, aspect rule and budget). Its layout is taken only when the cut canvas costs strictly fewer bytes than the rectangle layout (`CutBytes`, variant pages included). Test hook `SharedRepackMaskPackingDisabled` restores the old layout; `SharedRepackMaskClampsToTexture` restores the simulation's mask rule (see below). OutputVersion 29 -> 30 (golden `[30]` unchanged: the repack is not a pinned stage).

Copy path: a mask-packed page keeps each chart's mask and its disjoint rectangles (`SharedRepackMask.ToRectangles`: row runs, a run repeating the row above extends it). The block-copy plan, the real block copy, the canvas composition and the encoder-input mip seeding all iterate those rectangles instead of the chart rectangle (`GetSharedRepackChartRectangles`, used by `EnumerateSharedRepackSlotCharts`, `TryComposeSharedRepackOutput` and the mip overlay), so no write leaves the mask and a chart nested in a hole is never overwritten. Texels outside every mask keep the neutral fill of the unused page area. A rectangle boundary is a cell boundary (a multiple of Align), which is block aligned at every block-copied level, so nothing is lost there; deeper levels only get more Partial blocks next to mask edges. `FindSharedRepackMaskOverlap` fails the plan (`internal error: the cell masks of page ... overlap`) when masks overlap or leave the page; it runs after the compose step and the test hook, before the pixel check and before anything is written. Output signatures (duplicate pages) include a hash of each mask. Pixel check: the lower-mip edge skip (texels within two of the chart edge) uses the mask edge on mask pages (axis neighbours only, like the rectangle rule), because the rectangle holds other charts; nothing else was relaxed.

Masks keep the band outside the texture. The simulation clamped masks to the texture, but the rectangle path fills that band with the edge texels (what a clamped sampler reads next to the border), so the real masks keep it. The real gain still exceeds the simulated 12.05 / 15.72 MB of page bytes, partly because newly passing groups add to it. Pages that took masks are no longer simulated: the "still gains" figures now cover only pages that kept rectangles (2 pages per pack, the groups the pixel retry banned).

Pixel retry (`394af6b79` extended): a failed mask-packed page bans (variant, mask packing) first (`FailedMaskPackingGroupKeys`, a `"mask|" + variant` entry in the group's banned set, no signature change), the next failure of the same variant bans (variant, smallest cut canvas), then the power of two, then the padding is lowered. Plans per group can be 3 paddings x 3 packings = 9, so the loop bound is 9 (was 6). The variant choice weighs a banned variant against the others by bytes, so a failed P2 with masks can end on P1 with masks (the interlock test fixture does exactly that, and its P1 masks fail the real mip 2/3 check on the 512 normal map: 1-texel cells at mip 3 against 4x4 blocks, so the retry path is exercised for real). Report: "Groups kept on rectangles (no cell-mask packing) by pixel check".

Measured (bench; baselines `albion-boxpack` 249,706,536 B / 29 sets and `teb-potban` 306,073,568 B / 56 sets; the `*-masksim` runs were byte-identical to them):

| | Albion | TEB |
|---|---|---|
| realised saving | 249,706,536 -> 265,364,664 (+15,658,128) | 306,073,568 -> 326,226,152 (+20,152,584) |
| sets | 29 -> 31 | 56 -> 58 |
| newly built | `high_battle_pilgrims_head_01` (10 tex, 2 units), `high_free_company_torso_01` (4 tex, 3 units) | `wef_great_eagle_body_01_mask` (8 tex), `hef_spearmen_head_01` (4 tex, 5 units) |
| lost sets | none | none |
| built-group page bytes | 141,614,448 (61 groups) -> 139,216,984 (67 groups) | 146,428,616 (77) -> 150,917,800 (81) |
| mask-packed pages | 39 of 68 built (17,583,864 B vs rectangles) + 2 of 12 untouched (586,592) | 56 of 83 built (24,297,168 B) + 5 of 14 untouched (2,032,400) |
| pixel check | max error 0 (31 sets); worst re-encoded share 0.48% (baseline 0.48%) | max error 0 (58 sets); worst share 0.48% (baseline 0.33%) |
| retries | 2 passes, 0 lowered, 2 groups kept on rectangles | 4 passes (was 3), 2 lowered (as before), 2 kept on rectangles, 1 on the power of two (as before) |
| bench wall (total) | 175.5 -> 174.8 s (153.6 s in the first run: machine noise) | 209.5 -> 210.7 s |
| shared repack phase | 47.8 -> 50.5 s | 50.1 s (baseline phase time not kept) |

Remaining simulated gain on the pages that kept rectangles: Albion 53,824 B (2 pages), TEB 159,896 B (2 pages); the budget was not hit. Fingerprints: Albion 14 originals removed, all generated-material pruning (originals of the two new sets' materials), 57 rigids and 13 WSModels rewritten, 38 generated files added, 196 generated files changed; no original texture changed or removed. TEB: 2 original textures removed (`teb_ship_tent_base_colour/mask`, replaced by a page), 135 rigids and 3 WSModels rewritten, no original texture changed. WHMM `pack validate --with-vanilla --baseline`: not regressed, 0 added diagnostics on both.

Biggest mask gains (in-game check candidates), page bytes saved against the rectangle layout: Albion `vir_wef_drycha_body_01` (2.16 MB), `vmp_skeletal_horse` + horned man (1.90 MB), `alb_warmaiden_body_01` + `wef_bladesinger_body_01` (1.17 MB, two groups), `ksl_elemental_beast_01`, `vir_forsaken_body_01`, `ogr_giant_accessories_01`, `ogr_hunter_body_01`; TEB `wef_great_hawk_body_01` + great eagle (5.60 MB, newly built), `vmp_manfred_von_carstein_body_01` (3.67 MB), `emp_karl_franz_head_01` (1.41 MB), celestial lion accessories + demigryph reaper (1.30 MB), `def_sorceress_head_01` + `hef_mage_head_01` (1.01 MB), `teb_borgio_grom_base` (0.84 MB), `hef_spearmen_head_01` (newly built), `teb_lupo_ship` (0.63 MB).

Checked in game (custom battle): Albion (Order of the Truthsayers) Highlanders, Defenders of Carn Mallog, Chosen of Ishernos, The Green Man, Maidenguard/Huntresses, Centaur Ogham-Warden, Albion Giant; TEB (Tilea) Gashnag, Irranan Hillmen, Shieldbearers, Lupio Sunscryer, Lucrezzia Belladonna, Borgio, the Mercenary General's golden lion. Units found with WHMM `pack diff --affected-units --format checklist` without vanilla (3.5 min on TEB; with vanilla it scans every vanilla rigid for texture changes and did not finish in 17 min).

## 2026-10-27: a stronger cell-mask packer

`SharedRepackMaskPacker` keeps its shape (deterministic, positions on the unit grid, bounded by `SharedRepackMaskSimBudget` = 6,000,000, taken only when the cut canvas costs strictly fewer bytes) but tries more variants per canvas width. `TryPackMinimumTrimmedCanvas` now loops variants outermost, canvas widths inside: 18 first-fit orders (area, height, longer side as before; new: bounding-box hole area, width, perimeter; and 12 seeded shuffles, each the key of order `mode % 6` scaled by a factor in [0.7, 1.3] hashed from (mode, chart index), so no RNG state), then a contact placement on orders 0 and 1. Contact placement = among the legal places that add the least height (rows past the used bottom are never better, scan stops when the height grows), the one that touches most placed cells and canvas edges (left and top walls count; computed bit-parallel per mask row). `TryPack` also gives up as soon as `max(right, widest mask) x bottom` cannot get below the best cut canvas, which makes the first fits cheaper. `OutputVersion` 30 -> 31 (golden hash unchanged).

Tried (Albion, saving against the `albion-maskpack` baseline of 265,364,664 B / 50.5 s shared repack; each row is a full bench run):

| variants | saving | repack phase |
|---|---|---|
| first fit orders 0-5 only (3 new orders) | 266,591,016 (+1.23 MB) | 49.7 s |
| area-growth placement alone (smallest used box, then bottom, row, column; 6 orders) | 264,609,080 (-0.76 MB, worse than first fit) | 55.7 s |
| area-growth + contact tie break alone (6 orders) | 264,254,896 (-1.11 MB) | 58.1 s |
| first fit 0-5 + height-first contact (6 orders) | 266,709,160 (+0.12 over first fit only) | 62.3 s |
| first fit 0-5 + pure max contact (6 orders) | 266,603,784 (+0.01) | 63.9 s |
| first fit 0-5 + 12 seeded shuffles | 266,835,240 (+0.24) | 59.8 s |
| everything above (42 variants) | 266,839,256 (+0.004 over the shuffles) | 61.9 s |
| kept: first fit 0-5 + 12 shuffles + height-first contact on orders 0, 1 | 266,835,240 | 57.3 s |

So the new sort orders give almost all of the gain, the shuffles add a quarter of a megabyte, and the placement rules do not pay on Albion: by-row first fit is already a bottom-left packer, and rules that minimize the box area or maximize contact alone lose to it. The contact pair is kept because it is cheap and a test shows it filling a notch where first fit extends the box, but it measured 0 B on Albion (not measured separately on TEB). Not done: swapping adjacent pairs of the best order (the shuffles cover that ground), mask-based page assignment for groups split over several pages (page counts are decided with rectangle sizes; the potential is the 2 and 3 page groups only, not measured).

Measured (bench; baselines `albion-maskpack` and `teb-maskpack`):

| | Albion | TEB |
|---|---|---|
| realised saving | 265,364,664 -> 266,835,240 (+1,470,576) | 326,226,152 -> 329,631,256 (+3,405,104) |
| sets | 31 -> 31, none new or lost | 58 -> 59; new `dae_cultist_of_tzeentch_head_01` (4 tex, 2 units, the Sisters of Fury hoods); none lost |
| built-group page bytes | 139,216,984 -> 137,159,624 (67 groups) | 150,917,800 (81) -> 160,794,832 (84; the new set adds 3 groups) |
| mask-packed pages | 39 -> 42 of 68 built (19,354,952 B vs rectangles, was 17,583,864) | 56 -> 60 of 83/86 built (27,815,296 B, was 24,297,168) |
| bounding-box holes / gaps in "Page layout" | holes 53,061,377 -> 52,611,592; packing gaps -3,118,881 -> -4,718,575 | holes 58,119,594 -> 62,787,243 (more groups); gaps -5,394,970 -> -6,994,885 |
| pixel check | max error 0 (31 sets); worst re-encoded share 0.48% (as before) | max error 0 (59 sets); worst share 0.44% (was 0.48%) |
| retries | 2 passes, 0 lowered, 2 kept on rectangles (as before) | 4 passes, 2 lowered, 2 kept on rectangles, 1 on the power of two (as before) |
| shared repack phase | 50.5 -> 57.3 s (+6.8) | 50.1 -> 58.6 s (+8.5) |
| bench process | 170.1 -> 165.9 s | 206.8 -> 212.1 s |

One simulated page hit the budget in each pack (the report-only simulation of a page that kept its rectangles; the planner pages did not matter). Fingerprints: no original texture changed, added or removed in either pack. Albion: 108 files changed (49 generated atlas `.dds`, 41 generated materials, 18 generated cross-rigid models), none added or removed. TEB: 109 changed (40 atlas `.dds`, 25 generated materials, 40 rigids, 4 WSModels), 18 added (12 atlas `.dds`, 6 generated materials: the new set), 2 removed (the two original `ak_teb_lupio_helmet_weighted*` materials that the new set replaces). WHMM `pack validate --baseline --with-vanilla`: regressed false, no added diagnostics in both.

Biggest page-byte changes (in-game check candidates): Albion, the Cygor accessories set: `ogr_giant_accessories_01` (-964,416 B, atlas 1672x1712 -> 1752x1496), `alb_giant_ogr_giant_accessories_01` (-160,736), `alb_stonegiant_deadogr_warrior_head_01` (-32,256), used by Albion Giant, Bologs, Albion Giant (Stone Thrower), Cachtorr (and the Grudgebringer giant); `cust_emp_jade_wizard_body_01` (-41,536) on Druidic Priestess (`wyrd_druid_body_01`); `3k_animated_props_replacements_01` (-32,928) on Young Slingers. TEB (Tilea): `def_warriors_legs_01` (-321,888, 1024x1024 -> 456x696) on Mengil Manhide's Manflayers; `emp_reiksguard_morr_torso_01` (-162,368) on Inquisitor Lord (on foot, bhorse, grif and horse variants) and Lupio Sunscryer; `vmp_manfred_von_carstein_body_01` (-127,232) on Gashnag; `cth_shugengan_body_01` (-63,712) on Estragon's Pariahs; `hef_mage_torso_01` (-40,992) on Lucrezzia Belladonna; and the new set's group on Sisters of Fury (+2,897,904 B page). Checked in game.

## 2026-10-30: cross-rigid cost and benefit, measured (budget lowered to 4 MiB; seven other ideas rejected)

All runs use the workshop sources (TEB `2927296206\!ak_teb3.pack`, Albion `2873961274\ovn_albion.pack`). The bench-only switches added for them stay in the tree, so any of these numbers can be re-measured without new code: `--xr-physical-mib-per-draw`, `--xr-resident-mib-per-draw`, `--xr-growth-cap-mib`, `--xr-min-single-vmd-draws`, `--xr-joint-cap`, `--lod-weights`, `--cross-rigid-merges false`, `--compress-rigid-models true` (commits 64fad19b5, e8f3e6179, b5825105f, e446139a5). Raw results are in `K:\projects\assedfork-bench\agent-scratch-xr\results.md` and `agent-scratch-lodw\results.md`.

**Adopted: physical budget 16 -> 4 MiB of generated geometry per expected draw (OutputVersion 34).** The cost curve in the value-gate section shows a long, expensive tail:

| TEB budget | merged rigids | pack growth | draws saved | share of the 16 MiB saving |
|---|---|---|---|---|
| 16 MiB (old) | 153 | +111.8 MB | 47.16 | 100 % |
| 8 MiB | 137 | +94.3 MB | 46.96 | 99.6 % |
| **4 MiB (new)** | **107** | **+63.9 MB** | **46.39** | **98.4 %** |
| 2 MiB | 86 | +41.9 MB | 44.26 | 93.9 % |
| 1 MiB | 62 | +26.3 MB | 38.58 | 81.8 % |
| off | 0 | 0 | 0 | 0 % |

Albion at 4 MiB: 12 instead of 18 rigids, +9.8 instead of +25.7 MB, 6.50 instead of 7.50 draws (86.8 %); the dropped ones are 8 near-identical fen beast/hulk payloads at ~10.5 MiB per draw. With LZ4 rigids (as WHMM ships them) the TEB growth is 65.8 MB at 16 MiB and 37.6 MB at 4 MiB. 2 MiB is too uneven across mods (Albion keeps only 66 %). The shared texture repack is unaffected by the budget: identical pages and bytes at every budget and with cross-rigid off (only page names change, their hash covers the rigid set).

Rejected, do not retry without a new reason:

1. **A tighter budget does not save time.** The cross-rigid analysis takes 42-50 s on TEB at every budget; the gate only acts after analysis. A time gain needs pruning before analysis.
2. **A cumulative growth cap is the wrong knob.** It fills in benefit order, not cost order: a 32 MiB cap keeps 33 draws for 32 MiB, the 0.5 MiB/draw budget keeps the same 33 draws for 8.8 MiB. The 256 MiB cap and the 0.5 MiB resident budget never bind.
3. **Retirement credit / whole-unit completion** (report section "Cross-rigid retirement potential"). The selected set covers 3 % of TEB's mergeable occurrences and no unit completely; even the union of all candidates covers only 5 of 95 TEB units (4 of 5 on Albion). The exact residency credit for sources no army unit loads any more is 3-4 % of the generated resident bytes (TEB 0.24 MiB selected, 1.74 MiB for all candidates). Crediting it lets 0 extra payloads through on TEB and 3 on Albion (+0.43 draws). Pack retirement finds 0 removable sources; even after fixing the bookkeeping blocker below and taking every candidate, the ceiling is ~10.5 MiB on TEB and ~11.7 MiB on Albion, and a merged copy replaces every retired byte several times over (one part appears in many combinations), so retirement only reduces growth. Prioritising coverage when merges conflict cannot beat the all-candidates numbers above. The 1,375 occurrence conflicts on TEB are mostly losing alternatives (pair/triple/quadruple fallbacks) of payloads that were picked.
   - Bookkeeping note: `ArmyResidencyModel.UnitIdsByAssetPath` adds every WSModel a unit reaches as its own `ResidencyAsset` root (`PackTextureAtlasBatchService.Traversal.cs` ~169), so a retirement check reads every WSModel as externally used. Fix that first if retirement is ever built.
4. **Scenario LOD weights.** The equal 25 % per LOD is crude (units switch at 10/20/30/10000 m), and payloads that save draws only at LOD2/3 (LOD0/1 often mix weighted2 and weighted4 and stay unmerged; the merge is decided per LOD) are credited 1.7x more under 0.05/0.10/0.15/0.70. But the reweighted selection scores the same as the equal-weight selection under the same weights (TEB 52.86 vs ~53.1 draws) while spending 11-27 MiB more geometry (11 MiB at 4 MiB); Albion's selection is unchanged because its merges save at LOD0/1. Keep 25 % each.
5. **Atlases on with cross-rigid (WHMM keeps `buildAtlases: false`).** TEB: +28.1 atlas draws on top of the unchanged 47.2 cross-rigid draws, but pack +34 MB, resident BCn +9.3 MiB, the shared repack saves 37 MB less (52 instead of 59 sets) and the run takes ~2.4x as long (353 vs 146 s). The ideal-merge section's "~110 draws for 64 MiB with shared atlases" is an upper bound that ignores texture cost.
6. **Joint-state cap above 4,096.** 16,384 / 65,536 add +0.02 / +0.35 draws; most of the ~6,000 excluded plans stay excluded even at 65,536. 16,384 finds 18.5 MiB cheaper geometry at the old 16 MiB budget but only 3.4 MiB at 4 MiB, for +0.6-1.8 GB peak memory.
7. **A minimum-draws floor** (not run): payloads under 0.1 draws are 20 payloads, 13.7 MiB and 1.6 draws on TEB; bytes per draw is the better knob and already removes most of them.

Related, same round: the opt-in opaque alpha-shader switch (OutputVersion 33) gives GPU time only. Every switched texture had alpha exactly 255, so compaction had already made it BC1 before the switch; no bytes are saved.

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
11. Cross-rigid tuning is done (2026-10-30): keep the 4 MiB physical budget, equal LOD weights, the 4,096 joint-state cap and atlases off in WHMM; retirement credit and unit completion are not worth building (see the 2026-10-30 section).
