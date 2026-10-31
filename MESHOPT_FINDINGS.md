# Meshoptimizer triangle ordering findings (2026-10-31)

## Adopted change

Optimize Pack uses native `meshopt_generateVertexRemap` to deduplicate complete
vertex payloads, then `meshopt_filterIndexBuffer` to remove full-attribute
degenerate and same-winding duplicate faces. The surviving indices then pass
through `meshopt_optimizeVertexCache`, `meshopt_optimizeOverdraw`, and
`meshopt_optimizeVertexFetch`.

Full-attribute keys include every CommonVertex field and preserve the existing
signed-zero, NaN, null-array and array-length equality semantics. Vertex
representatives retain their complete original payloads. Opposite-winding faces
and attribute distinctions remain separate. The final geometry pass includes modified rigids and editor-LOD remakes.
The geometry-option gate, part count and part order are unchanged.

The overdraw threshold remains **1.05f**, allowing up to 5% degradation relative
to the preceding native cache pass's vertex-cache efficiency. Native wrappers
validate triangle alignment, vertex/key bounds and key stride before entering
the native code; the overdraw wrapper also validates finite float3 positions.

On `feature/wh3-asset-host`, the implementation uses the headless
`GameWorld.MeshOptimizer` library and its existing native resolver. Current
`PackOptimizerVersions.OutputVersion` is **40**. Version 38 introduced the native
cache/overdraw order; version 39 adds full-attribute remapping and filtering,
invalidating cached outputs whose triangle counts may now change. Version 40
extends the final pass to rigids changed only by editor-LOD remaking.

## Measured results

The experiment used the pre-extraction editor pipeline at baseline commit
`13901098b` on `feature/texture-atlas-consolidation`. Each source pack received
one baseline and one candidate Release run using `Tools/PackOptimizerBench`.
The baseline used the managed triangle-ordering heuristic; the candidate used
the adopted native cache/overdraw sequence. The subsequent host-branch port
adapts namespaces, System.Numerics and LibraryImport to the headless layout.
**These pack measurements were not repeated on the newer host pipeline.**

| Pack | Rewritten/generated rigids | Mesh parts, all LODs | Geometry stage, baseline → native | Estimated vertex transformations | Estimated shaded pixels |
|---|---:|---:|---:|---:|---:|
| TEB (`!ak_teb3.pack`) | 164 | 2,428 | 4.097 → 1.763 s | −0.48% | −8.64% |
| Albion (`ovn_albion.pack`) | 55 | 902 | 4.341 → 1.682 s | −1.38% | −13.17% |

| Pack | Vertices compared | Triangles compared | Cache ACMR, baseline → native | Software overdraw, baseline → native | Output bytes, both variants |
|---|---:|---:|---:|---:|---:|
| TEB | 2,280,719 | 2,742,092 | 0.987893 → 0.983165 | 1.264383 → 1.155151 | 558,497,720 |
| Albion | 1,766,660 | 2,542,118 | 0.883723 → 0.871572 | 1.435771 → 1.246669 | 648,455,546 |

Overdraw improved at every LOD for both packs. TEB's LOD3 and LOD4 estimated
vertex transformations increased by 0.15% and 0.29%, respectively; the total
still improved. Albion's cache estimate improved at every LOD.

The whole Process timings were TEB 197.7 → 114.7 seconds and Albion
99.9 → 70.9 seconds. Those single samples include cache/system variation and
build activity during baseline work, so the whole-run difference cannot be
attributed to triangle ordering. The isolated geometry-phase times are above.

## Output integrity and quality

The application's pack loader and rigid-model parser compared every matching
rewritten/generated rigid. Both comparisons had **zero integrity errors**:

- Rigid, LOD and mesh headers, part counts, serialized materials, full vertex
  attributes, vertex/index counts, and oriented triangle multisets matched.
- Duplicate and degenerate triangle multiplicity and triangle winding matched.
- Software-rasterizer pixel coverage matched exactly.
- All non-rigid payloads, complete output file sets and pack sizes matched.

These checks establish preservation of model detail and attribute precision.
The lower shaded-pixel counts indicate less estimated work on surfaces hidden
behind other surfaces. Analyzer statistics use static positions and a software
rasterizer, and do not establish an FPS improvement or an in-game rendering
comparison. Reordering blended transparent triangles can affect appearance;
that remains untested. All-LOD aggregates are unweighted by army probability.

## Pack validation

WHMM's Windows CLI performed full-payload validation with the explicit base-game
manifest and compared each candidate with its baseline. Both comparisons had
**zero added or resolved diagnostics** and `regressed: false`.

Validation remained incomplete and returned exit code 5 because pre-existing
baseline diagnostics persisted unchanged:

| Diagnostic | TEB, before = after | Albion, before = after |
|---|---:|---:|
| `INVALID_RIGID_LOADER_REFERENCE` | 2 | 2 |
| `INVALID_XML` | 20 | 15 |
| `MISSING_RMV2_TEXTURE` | 9,462 | 7,224 |
| `RIGID_LOADER_SCAN_LIMIT` | 1 | 1 |
| `UNSUPPORTED_RMV2_PART` | 2 | 2 |

The bounded loader scan and unsupported parts prevent an exhaustive dependency
validation claim. The separate application-parser audit covered all 164 TEB and
55 Albion rewritten/generated rigids listed above.

## Reproduction details

Inputs were read-only under `K:\wh3mods\albion`; output packs were written to
separate experiment directories. Source sizes and modification times remained
unchanged.

| Input | Bytes | SHA-256 |
|---|---:|---|
| `!ak_teb3.pack` | 822,613,511 | `5c4391ff0796b53daa0f9bcfa5031d42587eee59f33a62adb96bed4aa2dc3c0e` |
| `ovn_albion.pack` | 613,360,551 | `01e2477fa4bde038d55cf82fd30346b0f1440c7a495178696f010450a82d6211` |

The shipped meshoptimizer version is **1.3**, upstream commit
`9e1f07b159d3cb777f1c67ed31fc11fd117986f4`. The experiment and host branch use
byte-identical native DLLs (SHA-256
`0fa84d8095aefb3f408a4ded385c6d56ad1b7751d7b736930c00e776c3c728f0`).

The existing bench runner used native Windows .NET and the WH3 game-pack loader.
Atlases were off; compatible-part merging, cross-rigid merging, geometry
optimization, texture downscaling, shared texture repacking, texture compaction
and unread-alpha removal were on. Other settings were the unchanged runner's
defaults. Cache statistics used `meshopt_analyzeVertexCache` with cache size 32,
warp size 32 and primitive-group size 0. Overdraw used `meshopt_analyzeOverdraw`
with packed float3 positions.

Full original reports, output packs, fingerprints, native analyzer results,
validation reports, source identities and logs remain as local artifacts under
`K:\projects\assedfork\.codex-meshopt\`. They are intentionally not committed.
The self-contained tables and methodology above record the findings in Git.

## Regression checks

The original implementation passed eight focused geometry tests. After adapting
the code to the host branch and pinning the changed golden hash, the host port
passed **nine focused Release checks** (eight geometry tests and the versioned
output golden test), with zero failures or skips. The focused filter was:

```text
FullyQualifiedName~RigidGeometryOptimizerTests|FullyQualifiedName~OutputVersion_GoldenHashIsPinnedForTheCurrentVersion
```

Full pack benchmarks were not rerun for the port. The golden test was first run
to obtain the new version's deterministic hash, then rerun after adding the
version-38 entry; historical entries were preserved.

Output version 38 pins golden hash `16AFD48B29182B062A5395A8C9533AB89D5C5CD76D4E463392A3B16202ADEC9B`; historical version entries 1–37 are unchanged.

## Native vertex-fetch follow-up (2026-10-31)

Tested the actual `meshopt_optimizeVertexFetch` entrypoint after the native cache and overdraw passes, replacing the existing managed first-use vertex reorder. The overdraw threshold remains 1.05. The native function reorders an opaque uint vertex-ID stream; the resulting permutation is applied to entire managed CommonVertex records, preserving every attribute. It does not pin managed vertex objects or duplicate the existing reorder.

**Result: identical mesh output, faster preprocessing for this step.** All vertex-reference sequences and ushort index buffers matched both the managed implementation and the saved output buffers. Estimated GPU vertex-fetch traffic was unchanged. There is no additional rendering-quality, cache, overdraw or fetch-traffic improvement over the existing first-use layout.

| Pack | Rigids / mesh parts | Managed fetch step | Native fetch step | Speedup, this step | Managed allocation | Native allocation |
|---|---:|---:|---:|---:|---:|---:|
| TEB | 164 / 2,428 | 437.299 ms | 97.426 ms | 4.49× | 146.97 MiB | 93.31 MiB |
| Albion | 55 / 902 | 361.099 ms | 75.867 ms | 4.76× | 123.20 MiB | 79.23 MiB |

Allocation dropped by 36.51% on TEB and 35.69% on Albion for this step. These timings are one isolated timed pass per implementation per pack, after a small synthetic warm-up and with model loading, input preparation, correctness checks and analyzer calls outside the timer. They do not measure whole-pack runtime or in-game GPU performance. The probe retains the baseline result arrays while measuring the candidate; system/GC effects and the single-sample design limit the precision of the timing comparison.

| Pack | Estimated fetched bytes, both layouts | Vertex-buffer bytes | Overfetch ratio, both layouts |
|---|---:|---:|---:|
| TEB | 76,231,040 | 76,127,900 | 1.001354825 |
| Albion | 58,046,720 | 56,899,040 | 1.020170463 |

The comparison reused the saved cache+overdraw experiment packs and their exact 164-rigid TEB / 55-rigid Albion inventories. Every selected model was parsed with the existing application loader and rigid parser. The probe reversed vertex storage and adjusted the input indices without changing triangles, giving each implementation a nonidentity permutation to perform while preserving the post-overdraw triangle sequence. It then compared the two results and the original saved buffers. This covered all 3,330 mesh parts and found zero mismatches. `meshopt_analyzeVertexFetch` used each model’s actual serialized vertex stride.

No full Optimize Pack benchmarks were repeated and no new packs were generated: the fetch-stage buffers are identical to the existing output. Earlier pack-validation diagnostics and rendering limits remain as recorded above.

On `feature/wh3-asset-host`, **13 focused Release checks passed**: the existing topology/attribute tests, new native-fetch permutation, unused-vertex, immutable-input, empty-buffer, full-16-bit-range and invalid-buffer cases, plus the unchanged golden-output test. Output version **38** and its pinned hash remain unchanged because output was preserved. The native fetch replacement is adopted for its lower preprocessing time and allocation, while preserving the existing mesh output.

Local probe source, per-part results and inventories are under `K:\projects\assedfork\.codex-meshopt\fetch-probe\` and `...\fetch-results\`; test/build/experiment logs are copied into the latter directory.

## Vertex-remap and index-filter experiment (2026-10-31)

Tested the actual meshoptimizer 1.3 `meshopt_generateVertexRemap` and `meshopt_filterIndexBuffer` entrypoints in separate headless probes against production baseline `c3edab531`. At the time of these standalone probes, neither candidate was enabled and the production baseline was output version 38. No new pack files were generated, and full Optimize Pack benchmarks and previously passing production tests were not rerun. Adoption in output version 39 is recorded below.

### Full-attribute vertex remapping

The native remap was supplied with zero-padded keys containing all CommonVertex attributes, bone array lengths/null markers and WeightCount. Signed zero and NaN bit representations were canonicalized to match the actual reflected production comparer. Oversized keys were rejected rather than truncated.

| Pack | Remap mesh parts (raw source / saved generated) | Managed unique vertices | Native unique vertices | Managed prototype | Native prototype including key packing | Allocated, managed → native |
|---|---:|---:|---:|---:|---:|---:|
| TEB | 2,458 (371 / 2,087) | 2,281,317 | 2,281,317 | 1074.7 ms | 425.2 ms | 141.81 → 317.82 MiB |
| Albion | 970 (736 / 234) | 1,766,812 | 1,766,812 | 1050.4 ms | 334.3 ms | 116.56 → 261.77 MiB |

Both corpora had zero unique-count, equivalence-group, representative-payload or unused-map-entry mismatches. Every native remap array and remapped index sequence exactly matched the managed result. Native remapping ran 2.53× as fast on TEB and 3.14× as fast on Albion in this prototype, with roughly **2.25× the cumulative allocations**. These are summed per-mesh, single-pass prototype measurements after a small warm-up. Native timing includes key construction and index marshalling; it is not a whole-pack runtime measurement or a peak-memory measurement. The prototype returns remap tables for comparison and does not benchmark a fully integrated production replacement.

The remap corpus uses original source geometry where a selected path exists in the source pack, otherwise the saved generated geometry. Original source meshes precede merging/repacking; saved generated meshes already passed existing deduplication. This tests 2,458 TEB and 970 Albion mesh parts, not an exact capture of every generated model immediately before production deduplication.

### Remapping the serialized vertex bytes

A separate diagnostic used `VertexFactory.Save` and the actual format/version stride to encode every saved optimized vertex, then called native remapping on those bytes. It introduced **zero additional welds beyond the current comparer** and no equivalence-group differences in all 2,428 TEB / 902 Albion parts. All **15,852,630** triangle-corner byte comparisons matched their remap representatives.

The loaded TEB output contains 90 aliases that both strategies can detect after serialization, worth 2,720 vertex bytes if compacted; Albion has none. This small post-serialization opportunity is not specific to native remapping. The diagnostic used save-format roundtrips, not hand-parsed slices of the original pack.

### Triangle filtering on the saved optimized meshes

The conservative candidate compared complete vertex attributes, preserving UVs, normals, skinning and all other vertex distinctions. Position-only filtering was tested separately as a diagnostic.

| Pack | Parts affected | Triangles before → after | Duplicate triangles removed | Degenerates removed | Uncompressed index bytes saved |
|---|---:|---:|---:|---:|---:|
| TEB | 164 | 2,742,092 → 2,705,811 | 36,281 | 0 | 217,686 (212.58 KiB) |
| Albion | 112 | 2,542,118 → 2,494,010 | 47,884 | 224 | 288,648 (281.88 KiB) |

This removes **1.32%** of TEB triangles and **1.89%** of Albion triangles. No vertices became newly unreferenced, so these filtering results add no vertex-payload saving. Surviving full-payload triangles exactly matched the expected oriented sequence, including retained reverse-winding counterparts; there were zero filter-output mismatches. The byte savings above describe ushort index payloads, not measured compressed pack-size or FPS changes.

Position-only filtering discarded **474 additional TEB triangles** and **30,095 additional Albion triangles** compared with full-attribute filtering. It made 90 / 13,554 vertices newly unused. Such removals disregard UV, normal and bone-weight differences; the synthetic tests explicitly demonstrate removal of distinct skinned/UV geometry. Position-only filtering is not adopted.

All parts affected by conservative filtering were RMV material type `weighted` with embedded shader `default_dry` (164 TEB and 112 Albion parts). This is not a verified opaque-material gate: WSModel assignments may override those shaders and were not evaluated by the probes. Duplicate blended surfaces can intentionally accumulate opacity. Filtering therefore cannot inherit the previous triangle-multiplicity/visual-equivalence claim. No in-game render comparison was performed.

### Validation and artifacts

The main probe passed **34 synthetic checks**, covering production comparer semantics for signed zero/NaNs, null versus empty arrays, UV/bone distinctions, empty/invalid inputs, key bounds, degenerates, exact/cyclic duplicates and reverse winding. The separate packed-byte remap self-test also passed. Each probe built with zero warnings/errors; each corpus was run once per diagnostic, with no reported parse, key-coverage, remap or filter failures. Production code and source/output packs were left unchanged.

Local source projects: `K:\projects\assedfork\.codex-meshopt\remap-filter-probe\` and `...\packed-remap-probe\`. Per-part JSON reports, summaries, inventories/scope requests and logs are under `...\remap-filter-results\`. Existing saved experiment packs and their original input identities are recorded in the earlier sections.

## Native remapping and filtering adopted (OutputVersion 39)

The production geometry pass now executes both native functions using the same canonical full-attribute keys tested above. It generates the old-to-new vertex map, filters original indices against those keys, applies the map to surviving indices, and then runs cache/overdraw/fetch optimization. This avoids constructing a second deduplicated key buffer. No position-only or Multi filtering is enabled.

Triangle count may decrease. The report includes **Triangles removed by geometry optimisation**; vertex accounting includes vertices made unused by filtering. Structural mesh-merge topology invariants remain strict before the geometry pass. Empty parts retain their material/part slot; the rigid reader now accepts a valid zero-vertex/zero-index section without dividing by zero and rejects contradictory empty-section headers.

All **24 focused Release checks were validated**, including full-payload preservation, signed-zero/NaN and null/empty-array semantics, reverse winding, unused vertices, full 16-bit indices, invalid inputs, empty meshes, empty-part save/reload and output version 39. The first run passed 23 checks; one test incorrectly expected an unreferenced duplicate to be mapped. Its fixture was corrected to reference both duplicates and leave a separate unused vertex, and only that test was rerun successfully. No production code needed changing for that fixture.

Output version **39** retains golden hash `16AFD48B29182B062A5395A8C9533AB89D5C5CD76D4E463392A3B16202ADEC9B`: the fixed golden grid has no removable faces, so its bytes remain unchanged. Entries 1–38 are preserved. The version still advances because real pack output changes.

A validation-only corpus run applied the actual production optimizer to all saved selected parts, independently compared the surviving oriented full-payload triangles with the earlier filter results, checked vertex-removal accounting, and serialized/reloaded every rigid using ModelFactory:

| Pack | Rigid roundtrips | Mesh parts | Vertices before → after | Triangles removed | Validation errors |
|---|---:|---:|---:|---:|---:|
| TEB | 164 | 2,428 | 2,280,719 → 2,280,629 | 36,281 | 0 |
| Albion | 55 | 902 | 1,766,660 → 1,766,660 | 48,108 | 0 |

All 219 rigid roundtrips and 3,330 parts retained the expected part/material assignments and valid indices. TEB’s 90 removed vertices are the post-serialization aliases noted above; Albion’s vertex count was unchanged. These runs validate integration and serialization, not whole-pack performance. No full pack benchmark was repeated and no new pack was written; prototype timings above are not measurements of the integrated version-39 pipeline.

Full-attribute matching protects UV, normal and skinning distinctions. Removing duplicated blended surfaces can still alter opacity; no in-game visual-equivalence claim is made. The filtering behavior is now enabled as part of the geometry option.

Validation probe source and results are retained locally under `K:\projects\assedfork\.codex-meshopt\remap-filter-validation\` and `...\remap-filter-results\`, including the two `*-production-validation.json` reports and test logs.

## Remade-only LOD geometry coverage fixed (OutputVersion 40)

The final geometry input now unions `ModifiedRigids` with
`EditorLodRemadeRigids`, then removes case-insensitive duplicates. Every selected
rigid runs remap, full-attribute filtering, cache, overdraw and fetch passes over
all its LODs and mesh parts. Cross-rigid generated output and other modified
rigids remain in the selection; unrelated cached source rigids are not added.

Editor-LOD remakes stay outside `ModifiedRigids` while earlier planning,
repacking and merge gates run. `CommitEditorLodRemadeRigids` remains after final
geometry optimization, immediately before saving. The existing
`optimizeGeometry && !crossRigidOnly` guard is unchanged.

This fixes the previous ordering gap: a rigid modified solely by LOD remaking
was previously committed too late to enter the geometry pass. Output version
advances from 39 to **40** to expire cached outputs. Golden entry 40 keeps
`16AFD48B29182B062A5395A8C9533AB89D5C5CD76D4E463392A3B16202ADEC9B`
because the fixed fixture does not model the changed batch selection. Historical
entries 1–39 are preserved.

Four targeted Release cases passed: two new remade-only/overlapping-set cases,
the existing deferred-planning/LOD-structure regression, and the version-40
golden output check. The new cases inject a duplicate face into each of four
LODs and verify one rigid/four meshes processed, with all injected duplicates
removed. The overlap case proves the same path is optimized only once.
No full pack benchmarks or unchanged mesh-algorithm suite were rerun.

The fix is based on **5975da7e5**, preserving its external rigid-copy repacking
changes and tests. Logs are retained locally as
`K:\projects\assedfork\.codex-meshopt\lod-geometry-fix-tests.log` and
`...\lod-geometry-coverage-tests.log`.
