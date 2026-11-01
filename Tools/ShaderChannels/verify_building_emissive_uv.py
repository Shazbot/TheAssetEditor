"""Fail-closed proof for rigid building emissive shaders in world-space UV mode.

Only the exact ``rigid_building_emissive`` and ``rigid_building_emissive_alpha``
runtime families are considered.  The proof specializes the single scalar
``use_ws_position_as_uv`` parameter to 1; the C# caller must enforce that same
runtime value before selecting the generated allowlist.

Usage:
    python verify_building_emissive_uv.py <fxc-folder> [--asm-cache DIR] [--verbose]
    python verify_building_emissive_uv.py <asm-folder> --compiled-dir <fxc-folder> [--verbose]

The compiled source folder supplies FASTBIN entrypoint metadata.  An asm-only
folder is accepted only when paired with its compiled folder, so every exact
runtime sibling and every linked vertex entrypoint can be checked.
"""

import argparse
import collections
import os
import re
import struct
import subprocess
import sys
import tempfile
from dataclasses import dataclass

import verify_emissive_uv as v
import verify_slot_families_uv as slots
from shader_slot_provenance import (
    UNPROVEN,
    VertexProvenance,
    expected_vertex_entrypoint,
    fastbin_metadata,
    semantic_component_map,
    signatures,
    source_component,
)


FAMILIES = ("rigid_building_emissive", "rigid_building_emissive_alpha")
FAMILY_SET = frozenset(FAMILIES)
EMISSIVE_MASK = "t_xml_emissive"
WORLDSPACE_PARAMETER = "use_ws_position_as_uv"
RAW_MESH_SLOTS = frozenset(v.ATLAS_SLOTS | {EMISSIVE_MASK})
REQUIRED_CROPPED_SLOTS = frozenset({
    "t_xml_base_colour",
    "t_xml_material_map",
    "t_xml_normal",
    EMISSIVE_MASK,
})
REQUIRED_PIXEL_ENTRYPOINTS = {
    "rigid_building_emissive": frozenset({
        "ps_main_gbuffer", "ps_gbuffer_highlight", "ps_point_shadow",
        "ps_directional_shadow", "ps_pixel_selection",
    }),
    "rigid_building_emissive_alpha": frozenset({
        "ps_main_gbuffer", "ps_gbuffer_highlight", "ps_point_shadow",
        "ps_directional_shadow",
    }),
}
KNOWN_PIXEL_ENTRYPOINTS = frozenset().union(*REQUIRED_PIXEL_ENTRYPOINTS.values())
EXPECTED_GEOMETRY_ENTRYPOINT = "gs40_shadow_pointlight"

COMPILED_RE = re.compile(
    r"^shaders_(?P<family>rigid_building_emissive(?:_alpha)?)_xml_hlsl_"
    r"(?P<hash>[0-9a-f]+)\.hlsl_compiled$"
)
ASM_RE = re.compile(
    r"^(?P<source>shaders_(?P<family>rigid_building_emissive(?:_alpha)?)_xml_hlsl_"
    r"[0-9a-f]+\.hlsl_compiled)\.(?P<blob>\d+)\.asm$"
)
PARAM_DECL_RE = re.compile(
    r"^//\s+(float\d?|int|uint|bool)\s+(\w+);\s*//\s*Offset:\s*(\d+)\s*$",
    re.MULTILINE,
)
STAGE_RE = re.compile(r"^(ps|vs|gs|hs|ds|cs)_\d_\d$", re.MULTILINE)


@dataclass(frozen=True)
class AssemblyRecord:
    path: str
    source: str
    family: str
    blob: int
    entrypoint: str
    stage: str
    text: str


def target_compiled_files(folder):
    """Return exact target files, ignoring similarly named prop/other families."""
    targets = collections.defaultdict(list)
    for name in sorted(os.listdir(folder)):
        match = COMPILED_RE.fullmatch(name)
        if match and match.group("family") in FAMILY_SET:
            targets[match.group("family")].append(name)
    return dict(targets)


def target_family_errors(targets):
    errors = []
    for family in FAMILIES:
        if not targets.get(family):
            errors.append("required runtime shader family is absent: " + family)
    return errors


def metadata_for_sources(compiled_dir, targets):
    metadata, errors = {}, []
    for family in FAMILIES:
        for source in targets.get(family, ()):
            try:
                with open(os.path.join(compiled_dir, source), "rb") as stream:
                    parsed = fastbin_metadata(stream.read())
            except OSError as error:
                errors.append(f"{source}: cannot read compiled source: {error}")
                continue
            if parsed is None:
                errors.append(source + ": missing or ambiguous FASTBIN stage/entrypoint metadata")
                continue
            entrypoint, stage = parsed
            metadata[source] = {"entrypoint": entrypoint, "stage": stage}
    return metadata, errors


def powershell_path(path):
    if os.name == "nt":
        return path
    return subprocess.check_output(["wslpath", "-w", path]).decode().strip()


def disassemble_targets(compiled_dir, targets, cache_root=None):
    """Disassemble only the exact target families into a fresh output folder."""
    if cache_root is None:
        cache_root = tempfile.mkdtemp(prefix="verify-building-emissive-")
    else:
        os.makedirs(cache_root, exist_ok=True)
    output = tempfile.mkdtemp(prefix="run-", dir=cache_root)
    selected = sorted(source for family in FAMILIES for source in targets.get(family, ()))
    manifest = os.path.join(output, "_list.txt")
    with open(manifest, "w", encoding="utf-8") as stream:
        stream.write("\n".join(selected))

    script_dir = os.path.dirname(os.path.abspath(v.__file__))
    disassembler = os.path.join(script_dir, "dxbc_disassemble.ps1")
    subprocess.check_call([
        "powershell.exe", "-NoProfile", "-File", powershell_path(disassembler),
        "-InDir", powershell_path(compiled_dir), "-OutDir", powershell_path(output),
        "-List", powershell_path(manifest),
    ])
    return output


def source_name_for_asm(name):
    match = re.match(r"^(.*\.hlsl_compiled)\.\d+\.asm$", os.path.basename(name))
    return match.group(1) if match else None


def count_dxbc_blobs(payload):
    """Mirror dxbc_disassemble.ps1's DXBC magic and bounded-size scan."""
    count = 0
    offset = 0
    last_start = len(payload) - 32
    while offset <= last_start:
        if payload[offset:offset + 4] == b"DXBC":
            size = struct.unpack_from("<I", payload, offset + 24)[0]
            if size > 0 and offset + size <= len(payload):
                count += 1
                offset += size
                continue
        offset += 1
    return count


def blob_coverage_error(source, expected_count, observed_indices):
    expected = set(range(expected_count))
    observed = set(observed_indices)
    if observed != expected:
        return (f"{source}: DXBC blob coverage mismatch; expected indices "
                f"{sorted(expected)}, observed {sorted(observed)}")
    return None


def collect_assemblies(asm_dir, compiled_dir, targets, source_metadata):
    records, errors = [], []
    selected_sources = {source for names in targets.values() for source in names}
    blobs_by_source = collections.defaultdict(set)
    target_sources_seen = set()

    manifest = os.path.join(asm_dir, "_list.txt")
    if os.path.isfile(manifest):
        with open(manifest, encoding="utf-8") as stream:
            listed = set(stream.read().splitlines())
        listed_targets = {name for name in listed if COMPILED_RE.fullmatch(name)}
        if listed_targets != selected_sources:
            errors.append(
                "assembly _list.txt target selection mismatch: "
                f"missing {len(selected_sources - listed_targets)}, "
                f"extra {len(listed_targets - selected_sources)}"
            )

    expected_blob_counts = {}
    for source in sorted(selected_sources):
        try:
            with open(os.path.join(compiled_dir, source), "rb") as stream:
                expected_count = count_dxbc_blobs(stream.read())
        except OSError as error:
            errors.append(source + ": cannot read compiled source for DXBC blob count: " + str(error))
            continue
        if expected_count == 0:
            errors.append(source + ": compiled source contains no bounded DXBC blobs")
        expected_blob_counts[source] = expected_count

    for name in sorted(os.listdir(asm_dir)):
        if not name.endswith(".asm"):
            continue
        match = ASM_RE.fullmatch(name)
        if not match:
            source = source_name_for_asm(name)
            if source in selected_sources:
                errors.append(name + ": malformed target assembly filename")
            continue
        source = match.group("source")
        family = match.group("family")
        if family not in FAMILY_SET:
            continue
        if source not in selected_sources:
            errors.append(name + ": stale target assembly has no compiled source")
            continue
        target_sources_seen.add(source)
        blob = int(match.group("blob"))
        if blob in blobs_by_source[source]:
            errors.append(name + ": duplicate assembly blob index")
            continue
        blobs_by_source[source].add(blob)
        meta = source_metadata.get(source)
        if meta is None:
            errors.append(name + ": missing FASTBIN metadata for assembly source")
            continue
        try:
            with open(os.path.join(asm_dir, name), "r", errors="replace") as stream:
                text = stream.read()
        except OSError as error:
            errors.append(name + ": cannot read disassembly: " + str(error))
            continue
        stage_match = STAGE_RE.search(text)
        if stage_match is None or re.search(r"(?m)^HRESULT\s+", text):
            errors.append(name + ": invalid or failed shader disassembly")
            continue
        stage = stage_match.group(1).upper()
        if stage != meta["stage"]:
            errors.append(
                f"{name}: disassembly stage {stage} disagrees with FASTBIN {meta['stage']}"
            )
            continue
        records.append(AssemblyRecord(
            os.path.join(asm_dir, name), source, family, blob,
            meta["entrypoint"], meta["stage"], text,
        ))

    for source in sorted(selected_sources):
        indices = blobs_by_source.get(source, set())
        if not indices:
            errors.append(source + ": no disassembly for a selected compiled family variant")
        else:
            coverage_error = blob_coverage_error(source, expected_blob_counts.get(source, 0), indices)
            if coverage_error:
                errors.append(coverage_error)
        if source not in target_sources_seen:
            errors.append(source + ": no assembly file found")
    return records, errors


def parameter_declarations(shader_text, parameter_name):
    block = re.search(r"struct AUTO_PARAMETERS\n//\s*\{(.*?)\} \$Element", shader_text, re.DOTALL)
    if not block:
        return []
    return [
        (ty, name.removeprefix("sp_"), int(offset))
        for ty, name, offset in PARAM_DECL_RE.findall(block.group(1))
        if name.removeprefix("sp_") == parameter_name
    ]


def validate_worldspace_parameter(shader):
    declarations = parameter_declarations(shader.text, WORLDSPACE_PARAMETER)
    parsed = [p for p in shader.params if p[0] == WORLDSPACE_PARAMETER]
    if len(declarations) != 1 or declarations[0][0] != "float":
        return False, "use_ws_position_as_uv is not exactly one scalar float parameter"
    _, _, offset = declarations[0]
    if len(parsed) != 1 or parsed[0] != (WORLDSPACE_PARAMETER, offset, 4):
        return False, "use_ws_position_as_uv has ambiguous or non-scalar structured-buffer layout"
    return True, None


def raw_anchor_semantics(shader):
    """Return semantic anchors for every cropped raw sample in one pixel shader."""
    semantics = semantic_component_map(shader.text)
    anchors = set()
    for op, args in shader.instructions():
        if not op.startswith(v.SAMPLE_OPS) or len(args) < 3:
            continue
        resource = re.match(r"^(t\d+)", args[2].strip())
        slot = shader.tex.get(resource.group(1)) if resource else None
        if slot not in RAW_MESH_SLOTS:
            continue
        coord = re.fullmatch(r"(v\d+)\.([xyzwrgba]+)", args[1].strip())
        if not coord or len(coord.group(2)) < 2 or coord.group(2)[:2] != "xy":
            return set(), f"{slot} raw sample does not use a direct pixel interpolator xy"
        register = coord.group(1)
        x_semantic = semantics.get((register, 0))
        y_semantic = semantics.get((register, 1))
        if x_semantic is None or x_semantic != y_semantic:
            return set(), f"{slot} raw sample has missing or ambiguous xy input semantic"
        anchors.add(x_semantic)
    if len(anchors) > 1:
        return anchors, "cropped samples in one pixel shader use different raw input semantics"
    return anchors, None


def referenced_ps_input_components(shader):
    return slots.referenced_input_components(shader)


def read_vertex_proofs(records):
    """Analyze each linked VS blob; only a no-op return stub may lack outputs."""
    full_by_entrypoint = collections.defaultdict(list)
    errors = []
    for record in records:
        if record.stage != "VS":
            continue
        try:
            proof = VertexProvenance(record.text).run()
        except Exception as error:
            errors.append(os.path.basename(record.path) + ": VS provenance failed: " + str(error))
            continue
        if proof.shader.stage != "vs":
            errors.append(os.path.basename(record.path) + ": FASTBIN VS metadata disagrees with assembly")
            continue
        if proof.outputless_stub:
            instructions = list(proof.shader.instructions())
            safe_stub = (
                not proof.has_output_writes
                and not proof.outputs
                and instructions == [("ret", [])]
            )
            if not safe_stub:
                errors.append(os.path.basename(record.path) + ": outputless VS is not the recognized ret-only stub")
            continue
        if proof.issues:
            errors.append(os.path.basename(record.path) + ": " + "; ".join(proof.issues))
            continue
        if not proof.outputs or not proof.has_output_writes:
            errors.append(os.path.basename(record.path) + ": VS has no proven output writes")
            continue
        full_by_entrypoint[record.entrypoint].append((record, proof))
    return full_by_entrypoint, errors


def geometry_passthrough(text, required_semantics):
    """Prove geometry shader outputs used by PS are identity copies of same semantics."""
    mapping = geometry_component_map(text)
    errors = []
    for semantic, components in required_semantics.items():
        for component in components:
            choices = mapping.get((semantic[0], semantic[1], component), set())
            expected = {(semantic[0], semantic[1], component)}
            if choices != expected:
                errors.append(
                    f"geometry output {semantic}.{'xyzw'[component]} is not an unambiguous identity copy"
                )
    return errors


def geometry_component_map(text):
    """Map GS output semantic components to the input semantic components they copy."""
    input_signatures = signatures(text, "Input signature:")
    output_signatures = signatures(text, "Output signature:")
    input_by_register = {register: (semantic, index, mask)
                         for (semantic, index), (register, mask) in input_signatures.items()}
    output_by_register = {register: (semantic, index, mask)
                          for (semantic, index), (register, mask) in output_signatures.items()}
    writes = collections.defaultdict(set)

    for op, args in assembly_instructions(text, "gs"):
        ndst = 2 if op in ("sincos", "udiv", "umul", "imul", "uaddc", "usubb") and len(args) >= 4 else 1
        for dst in args[:ndst]:
            match = re.fullmatch(r"o(\d+)(?:\.([xyzw]+))?", dst.strip())
            if not match:
                continue
            out_register = int(match.group(1))
            out_info = output_by_register.get(out_register)
            if out_info is None:
                continue
            out_semantic, out_index, output_mask = out_info
            written = match.group(2) or output_mask
            direct = None
            if op == "mov" and len(args) == 2:
                source = re.fullmatch(r"v\[[^\]]+\]\[(\d+)\](?:\.([xyzwrgba]+))?", args[1].strip())
                if source:
                    in_register = int(source.group(1))
                    input_info = input_by_register.get(in_register)
                    if input_info:
                        direct = (input_info[0], input_info[1], source.group(2))
            for letter in written:
                component = v.COMP[letter]
                if direct is None:
                    writes[(out_semantic, out_index, component)].add(UNPROVEN)
                    continue
                in_semantic, in_index, swizzle = direct
                in_component = source_component(swizzle, component)
                writes[(out_semantic, out_index, component)].add(
                    (in_semantic, in_index, in_component)
                )

    return dict(writes)


def geometry_control_errors(text):
    """Reject geometry emission/branch control that can depend on any vertex input."""
    register_taint = collections.defaultdict(bool)
    errors = []
    instructions = list(assembly_instructions(text, "gs"))

    def source_tainted(token):
        token = token.strip().lstrip("-").strip("|")
        if token.startswith("l("):
            return False
        if re.search(r"\bv\d+\b|\bv\[", token):
            return True
        register_refs = re.findall(r"\br(\d+)(?:\.([xyzwrgba]+))?\b", token)
        for number, swizzle in register_refs:
            components = {v.COMP[letter] for letter in swizzle} if swizzle else set(range(4))
            if any(register_taint[("r" + number, component)] for component in components):
                return True
        dynamic = re.search(r"(?:cb\d+|icb)\[([^]]+)\]", token)
        if dynamic:
            index = dynamic.group(1)
            residue = re.sub(r"r\d+(?:\.[xyzwrgba]+)?|0x[0-9a-fA-F]+|\d+(?:\.\d+)?|[+*()\- ]", "", index)
            if residue:
                return True
            return source_tainted(index) if re.search(r"\br\d+", index) else False
        return False

    for _ in range(max(2, min(len(instructions) + 1, 32))):
        before = dict(register_taint)
        for op, args in instructions:
            if op in ("if_nz", "if_z", "switch"):
                # The VS/PS evaluators do not model GS branch merges. Keep this proof
                # deliberately narrower than the data-flow proof for ordinary ALU.
                errors.append("geometry branch or emission control is not statically modeled")
                continue
            if op in ("breakc", "breakc_nz", "breakc_z", "continuec", "continuec_nz",
                      "continuec_z", "retc", "retc_nz", "retc_z"):
                if any(source_tainted(arg) for arg in args):
                    errors.append("geometry branch or emission control depends on a vertex input")
                continue
            if op in ("loop", "endloop", "else", "case", "default", "endswitch", "break", "continue",
                      "ret", "emit", "emit_stream", "cut", "cut_stream"):
                continue
            if not args:
                continue
            destination_count = (2 if op == "sincos" or
                                 op in ("udiv", "umul", "imul", "uaddc", "usubb") and len(args) >= 4
                                 else 1)
            source_args = args[destination_count:]
            tainted = any(source_tainted(arg) for arg in source_args)
            for destination in args[:destination_count]:
                match = re.fullmatch(r"r(\d+)(?:\.([xyzw]+))?", destination.strip())
                if not match:
                    continue
                register = "r" + match.group(1)
                components = match.group(2) or "xyzw"
                for letter in components:
                    register_taint[(register, v.COMP[letter])] = tainted
        if dict(register_taint) == before:
            break
    else:
        errors.append("geometry control provenance did not converge")
    return list(dict.fromkeys(errors))


def geometry_texture_output_errors(text):
    """Require each GS TEXCOORD output component to have known direct input provenance."""
    output_signatures = signatures(text, "Output signature:")
    component_map = geometry_component_map(text)
    errors = []
    for (semantic, index), (_, mask) in output_signatures.items():
        if semantic != "TEXCOORD":
            continue
        for letter in mask:
            component = v.COMP[letter]
            choices = component_map.get((semantic, index, component), set())
            if len(choices) != 1 or UNPROVEN in choices:
                errors.append(f"geometry TEXCOORD {index}.{letter} has missing or ambiguous input provenance")
    return errors


def resolve_geometry_component(semantic, component, geometry_maps):
    """Return the one GS input semantic component feeding a PS input component."""
    if not geometry_maps:
        return (semantic[0], semantic[1], component), None
    choices = [mapping.get((semantic[0], semantic[1], component), set()) for mapping in geometry_maps]
    if any(len(choice) != 1 or UNPROVEN in choice for choice in choices):
        return None, f"geometry output {semantic}.{'xyzw'[component]} has missing or ambiguous provenance"
    resolved = [next(iter(choice)) for choice in choices]
    if any(source != resolved[0] for source in resolved[1:]):
        return None, f"geometry output {semantic}.{'xyzw'[component]} differs across linked GS blobs"
    return resolved[0], None


def assembly_instructions(text, stage):
    """Parse the straight-line instruction spelling for stages the shared parser omits."""
    started = False
    for raw in text.splitlines():
        line = raw.strip()
        if not line or line.startswith("//"):
            continue
        if not started:
            if re.match(r"^" + re.escape(stage) + r"_\d_\d$", line):
                started = True
            continue
        if line.startswith("dcl_"):
            continue
        match = re.match(r"[a-z0-9_]+", line)
        if not match:
            continue
        op = match.group(0)
        rest = line[len(op):]
        while rest.startswith("("):
            depth = 0
            for index, char in enumerate(rest):
                depth += char == "("
                depth -= char == ")"
                if depth == 0:
                    rest = rest[index + 1:]
                    break
        yield op, v.split_operands(rest.strip())


def linked_input_uv_components(shader, linked_proofs, model_uv, geometry_texts=()):
    """Map each referenced PS input alias to exact VS model-UV dependencies."""
    input_components = referenced_ps_input_components(shader)
    pixel_semantics = semantic_component_map(shader.text)
    geometry_maps = [geometry_component_map(text) for text in geometry_texts]
    result = collections.defaultdict(set)
    errors = []
    for register, components in input_components.items():
        for component in components:
            semantic = pixel_semantics.get((register, component))
            if semantic is None:
                errors.append(f"{register}.{'xyzw'[component]} has no pixel input semantic")
                continue
            vertex_component, geometry_error = resolve_geometry_component(
                semantic, component, geometry_maps,
            )
            if geometry_error:
                errors.append(geometry_error)
                continue
            vertex_semantic = (vertex_component[0], vertex_component[1])
            vertex_axis = vertex_component[2]
            dependencies = []
            for _, proof in linked_proofs:
                component_deps = proof.semantic_components.get(vertex_semantic, {}).get(vertex_axis)
                if component_deps is None or UNPROVEN in component_deps:
                    errors.append(f"linked VS does not prove {vertex_semantic}.{'xyzw'[vertex_axis]}")
                    continue
                dependencies.append(frozenset(component_deps))
            if not dependencies:
                errors.append(f"no linked VS output proves {vertex_semantic}.{'xyzw'[vertex_axis]}")
                continue
            if any(deps != dependencies[0] for deps in dependencies[1:]):
                errors.append(f"ambiguous linked VS provenance for {vertex_semantic}.{'xyzw'[vertex_axis]}")
                continue
            if model_uv in dependencies[0]:
                result[register].add(component)
    return dict(result), errors


class WorldspaceEvaluator(slots.SlotEvaluator):
    """Existing polynomial evaluator with the building mask and mode rules."""
    def __init__(self, shader, input_uv_components=None, provenance_error=None,
                 provenance_complete=False, specialize_worldspace=False):
        super().__init__(shader, "building_worldspace", input_uv_components,
                         provenance_error, provenance_complete)
        self.specialize_worldspace = specialize_worldspace

    def find_uv0(self):
        candidates = set()
        nonraw = False
        for op, args in self.sh.instructions():
            if not op.startswith(v.SAMPLE_OPS) or len(args) < 3:
                continue
            resource = re.match(r"^(t\d+)", args[2].strip())
            slot = self.sh.tex.get(resource.group(1)) if resource else None
            if slot not in RAW_MESH_SLOTS:
                continue
            match = re.fullmatch(r"(v\d+)\.([xyzwrgba]+)", args[1].strip())
            if match and len(match.group(2)) >= 2 and match.group(2)[:2] == "xy":
                candidates.add(match.group(1))
            else:
                nonraw = True
        if nonraw:
            self.issues.append("a cropped building texture is sampled at a non-raw UV coordinate")
        if len(candidates) > 1:
            self.issues.append("cropped building textures use different pixel interpolators")
        elif candidates:
            self.uv0 = next(iter(candidates))
        elif not self.provenance_complete and self.input_uv_components:
            self.issues.append("missing raw UV anchor for a shader with linked mesh-UV inputs")
        if self.provenance_error:
            self.issues.append(self.provenance_error)

    def do_ld_structured(self, args):
        destination, components = self.dst_comps(args[0])
        tainted_index = any(v.p_taint(self.src_comp(arg, comp))
                            for arg in args[1:3] for comp in range(4))
        if tainted_index:
            raise v.Fail("mesh UV used as structured-buffer index or offset")
        offset_match = re.fullmatch(r"l\((\d+)\)", args[2].strip())
        resource = re.fullmatch(r"(t\d+)\.([xyzw]+)", args[3].strip())
        if (not offset_match or not resource or resource.group(1) != self.sh.struct_reg):
            self.write(destination, components, [v.opaque("sb?", False) for _ in components])
            return
        base_offset = int(offset_match.group(1))
        swizzle = resource.group(2)
        output = []
        for component in components:
            source_component = v.COMP[swizzle[component]] if component < len(swizzle) else v.COMP[swizzle[-1]]
            parameter = self.sh.param_at(base_offset + 4 * source_component)
            if parameter and parameter[0] == WORLDSPACE_PARAMETER and self.specialize_worldspace:
                output.append(v.poly_const(1))
            else:
                output.append(v.poly_atom(v.atom("sb", parameter[0], parameter[1]) if parameter
                                          else v.atom("sb", "offset", base_offset + 4 * source_component)))
        self.write(destination, components, output)

    def step(self, op, args):
        if op == "ld_indexable" and len(args) == 3:
            resource = re.match(r"^(t\d+)", args[2].strip())
            slot = self.sh.tex.get(resource.group(1)) if resource else None
            if slot in RAW_MESH_SLOTS:
                raise v.Fail(slot + " must be sampled at raw mesh UV, not loaded by index")
        return super().step(op, args)

    def do_sample(self, op, args):
        destination, components = self.dst_comps(args[0])
        resource = re.fullmatch(r"(t\d+)(?:\.([xyzw]+))?", args[2].strip())
        if not resource:
            raise v.Fail("texture sample has an unparsed resource operand")
        slot = self.sh.tex.get(resource.group(1))
        dimension = self.texture_dimensions.get(resource.group(1))
        coordinate_count = {
            "1d": 1, "1darray": 2, "2d": 2, "2darray": 3,
            "3d": 3, "cube": 3, "cubearray": 4,
        }.get(dimension)
        if slot is None or coordinate_count is None:
            raise v.Fail("texture sample has missing binding or unproven resource dimension")
        if slot in RAW_MESH_SLOTS and dimension != "2d":
            raise v.Fail(slot + " requires a 2D resource for its raw mesh-UV crop rule")
        coordinates = [self.src_comp(args[1], comp) for comp in range(coordinate_count)]
        extra_operands = [self.src_comp(arg, comp) for arg in args[4:] for comp in range(4)]
        if any(v.p_taint(poly) for poly in extra_operands):
            raise v.Fail("mesh UV feeds a sample LOD, gradient, or reference operand of " + slot)
        if slot in RAW_MESH_SLOTS:
            expected = [v.poly_atom(v.atom("in", self.uv0, axis, taint=True)) for axis in range(2)]
            if self.uv0 is None or coordinates[:2] != expected:
                raise v.Fail(slot + " is not sampled at raw mesh UV0")
            self.found[slot] += 1
        elif any(v.p_taint(poly) for poly in coordinates):
            raise v.Fail(slot + " sample coordinate depends on mesh UV0")
        if slot == v.EMIS:
            self.found["worldspace_emissive_texture"] += 1

        swizzle = resource.group(2) or "xyzw"
        coord_key = "".join(v.p_key(poly) for poly in coordinates)
        output = []
        for component in components:
            channel = v.COMP[swizzle[component]] if component < len(swizzle) else v.COMP[swizzle[-1]]
            key = "rawUV0" if slot in RAW_MESH_SLOTS else coord_key
            output.append(v.poly_atom(v.atom("tex", slot, key, channel)))
        self.write(destination, components, output)


def validate_family_entries(family, records):
    errors = []
    pixel_entrypoints = {r.entrypoint for r in records if r.stage == "PS"}
    missing = REQUIRED_PIXEL_ENTRYPOINTS[family] - pixel_entrypoints
    if missing:
        errors.append(f"{family}: missing required PS entrypoints: " + ", ".join(sorted(missing)))
    unknown = pixel_entrypoints - KNOWN_PIXEL_ENTRYPOINTS
    if unknown:
        errors.append(f"{family}: unsupported PS entrypoints: " + ", ".join(sorted(unknown)))
    if not pixel_entrypoints:
        errors.append(family + ": no pixel shader variants")
    geometry_entrypoints = {r.entrypoint for r in records if r.stage == "GS"}
    if EXPECTED_GEOMETRY_ENTRYPOINT not in geometry_entrypoints:
        errors.append(family + ": missing required geometry entrypoint " + EXPECTED_GEOMETRY_ENTRYPOINT)
    unexpected_geometry = geometry_entrypoints - {EXPECTED_GEOMETRY_ENTRYPOINT}
    if unexpected_geometry:
        errors.append(f"{family}: unsupported GS entrypoints: " + ", ".join(sorted(unexpected_geometry)))
    return errors


def infer_model_uv(family, records, vertex_by_entrypoint):
    errors, candidates = [], set()
    ps_records = [record for record in records if record.family == family and record.stage == "PS"]
    seen_entrypoints = set()
    for record in ps_records:
        try:
            shader = v.Shader(record.text)
        except Exception as error:
            errors.append(os.path.basename(record.path) + ": assembly parse failed: " + str(error))
            continue
        anchors, anchor_error = raw_anchor_semantics(shader)
        if anchor_error:
            errors.append(os.path.basename(record.path) + ": " + anchor_error)
            continue
        if not anchors:
            continue
        expected_vs = expected_vertex_entrypoint(family, record.entrypoint)
        linked = vertex_by_entrypoint.get(expected_vs, ())
        if not linked:
            errors.append(os.path.basename(record.path) + ": missing FASTBIN-linked VS " + expected_vs)
            continue
        if record.entrypoint == "ps_point_shadow":
            geometry = [r for r in records if r.family == family and r.stage == "GS"
                        and r.entrypoint == EXPECTED_GEOMETRY_ENTRYPOINT]
            if not geometry:
                errors.append(os.path.basename(record.path) + ": missing linked point-shadow geometry shader")
                continue
            geometry_maps = [geometry_component_map(item.text) for item in geometry]
        else:
            geometry_maps = []
        for semantic in anchors:
            seen_entrypoints.add(record.entrypoint)
            mapped_axes = []
            for axis in (0, 1):
                mapped, geometry_error = resolve_geometry_component(semantic, axis, geometry_maps)
                if geometry_error:
                    errors.append(os.path.basename(record.path) + ": " + geometry_error)
                    continue
                mapped_axes.append(mapped)
            if len(mapped_axes) != 2:
                continue
            vertex_semantic = (mapped_axes[0][0], mapped_axes[0][1])
            if (mapped_axes[1][0], mapped_axes[1][1]) != vertex_semantic:
                errors.append(os.path.basename(record.path) + ": raw UV axes map to different VS semantics")
                continue
            for _, proof in linked:
                axes = proof.semantic_components.get(vertex_semantic, {})
                x = axes.get(mapped_axes[0][2], {UNPROVEN})
                y = axes.get(mapped_axes[1][2], {UNPROVEN})
                if x != y or len(x) != 1 or UNPROVEN in x:
                    errors.append(os.path.basename(record.path) +
                                  f": raw anchor {semantic} is not a direct, unambiguous model UV output")
                else:
                    candidates.update(x)
    if not seen_entrypoints:
        errors.append(family + ": no cropped raw-sample anchor identifies mesh UV0")
    if len(candidates) != 1:
        errors.append(family + ": raw anchors do not resolve to one model UV input semantic")
        model_uv = None
    else:
        model_uv = next(iter(candidates))
    return model_uv, errors


def verify_pixel_record(record, model_uv, vertex_by_entrypoint, records):
    reasons = []
    shader = v.Shader(record.text)
    if shader.stage != "ps":
        return [os.path.basename(record.path) + ": FASTBIN PS metadata disagrees with assembly"], None
    expected_vs = expected_vertex_entrypoint(record.family, record.entrypoint)
    linked = vertex_by_entrypoint.get(expected_vs, ())
    if not linked:
        return [os.path.basename(record.path) + ": missing FASTBIN-linked VS entrypoint " + expected_vs], None

    geometry_texts = ()
    if record.entrypoint == "ps_point_shadow":
        geometry = [r for r in records if r.family == record.family and r.stage == "GS"
                    and r.entrypoint == EXPECTED_GEOMETRY_ENTRYPOINT]
        if not geometry:
            reasons.append("missing FASTBIN-linked point-shadow geometry entrypoint " + EXPECTED_GEOMETRY_ENTRYPOINT)
        else:
            geometry_texts = tuple(item.text for item in geometry)

    input_uv_components, provenance_errors = linked_input_uv_components(
        shader, linked, model_uv, geometry_texts,
    )
    reasons.extend(provenance_errors)

    texture_names = set(shader.tex.values())
    modern = v.EMIS in texture_names or EMISSIVE_MASK in texture_names
    specialize_worldspace = False
    if modern:
        specialize_worldspace, parameter_error = validate_worldspace_parameter(shader)
        if parameter_error:
            reasons.append(parameter_error)

    evaluator = WorldspaceEvaluator(
        shader, input_uv_components,
        provenance_error="; ".join(provenance_errors) if provenance_errors else None,
        provenance_complete=not provenance_errors,
        specialize_worldspace=specialize_worldspace,
    )
    evaluator.run()
    reasons.extend(evaluator.issues)

    if modern:
        declared_raw = RAW_MESH_SLOTS & texture_names
        missing_required = REQUIRED_CROPPED_SLOTS - declared_raw
        if missing_required:
            reasons.append("missing required cropped raw bindings: " + ", ".join(sorted(missing_required)))
        for slot in sorted(declared_raw):
            if evaluator.found[slot] == 0:
                reasons.append(slot + " has no proven raw mesh-UV sample")
        if evaluator.found["worldspace_emissive_texture"] == 0:
            reasons.append(v.EMIS + " has no sample with UV-independent coordinates")
    return reasons, evaluator


def verify_families(records):
    failures, summaries = [], {}
    by_family = {family: [r for r in records if r.family == family] for family in FAMILIES}
    for family in FAMILIES:
        family_records = by_family[family]
        if not family_records:
            failures.append("required runtime shader family is absent: " + family)
        failures.extend(validate_family_entries(family, family_records))
        geometry_records = [r for r in family_records if r.stage == "GS"]
        for record in geometry_records:
            geometry_errors = geometry_control_errors(record.text) + geometry_texture_output_errors(record.text)
            failures.extend(family + "/" + os.path.basename(record.path) + ": " + error
                            for error in geometry_errors)

    vertex_by_family = {}
    for family in FAMILIES:
        proofs, errors = read_vertex_proofs(by_family[family])
        failures.extend(family + ": " + error for error in errors)
        required_vs = {
            expected_vertex_entrypoint(family, entrypoint)
            for entrypoint in REQUIRED_PIXEL_ENTRYPOINTS[family]
        }
        for entrypoint in sorted(required_vs):
            if not proofs.get(entrypoint):
                failures.append(family + ": missing full linked VS provenance for " + entrypoint)
        vertex_by_family[family] = proofs

    passing_counts = collections.Counter()
    for family in FAMILIES:
        family_records = by_family[family]
        proofs = vertex_by_family[family]
        model_uv, errors = infer_model_uv(family, family_records, proofs)
        failures.extend(family + ": " + error for error in errors)
        ps_records = [r for r in family_records if r.stage == "PS"]
        counts = collections.Counter()
        for record in ps_records:
            reasons, evaluator = verify_pixel_record(record, model_uv, proofs, family_records)
            if reasons:
                counts["failed"] += 1
                failures.extend(family + "/" + os.path.basename(record.path) + ": " + reason
                                for reason in dict.fromkeys(reasons))
            else:
                counts["passed"] += 1
                if evaluator and evaluator.found["worldspace_emissive_texture"]:
                    counts["worldspace_emissive_ps"] += 1
        stage_counts = collections.Counter(r.stage for r in family_records)
        summaries[family] = {
            "compiled_sources": len({r.source for r in family_records}),
            "assembly_blobs": len(family_records),
            "pixel_blobs": len(ps_records),
            "vertex_blobs": stage_counts["VS"],
            "geometry_blobs": stage_counts["GS"],
            "pixel_passed": counts["passed"],
            "pixel_failed": counts["failed"],
            "worldspace_emissive_pixel_blobs": counts["worldspace_emissive_ps"],
            "model_uv": model_uv,
        }
        passing_counts[family] = counts["passed"]

    # The engine may switch to the alpha sibling at runtime; expose both names only
    # when the full sibling group, including all compiled variants, proves clean.
    if any(summaries.get(family, {}).get("pixel_failed", 1) for family in FAMILIES):
        failures.append("runtime building emissive sibling group is not fully verified")
    return summaries, list(dict.fromkeys(failures))


def parse_args(argv):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folder", help="compiled .hlsl_compiled folder or pre-disassembled asm folder")
    parser.add_argument("--compiled-dir", help="compiled source folder required when folder contains asm")
    parser.add_argument("--asm-cache", help="parent directory for a fresh disassembly output folder")
    parser.add_argument("--verbose", action="store_true")
    return parser.parse_args(argv)


def main(argv=None):
    args = parse_args(sys.argv[1:] if argv is None else argv)
    input_files = sorted(os.listdir(args.folder))
    is_compiled_input = any(name.endswith(".hlsl_compiled") for name in input_files)
    if is_compiled_input and args.compiled_dir:
        print("--compiled-dir is only valid with a pre-disassembled asm folder")
        return 2
    if not is_compiled_input and not args.compiled_dir:
        print("pre-disassembled input requires --compiled-dir for FASTBIN metadata")
        return 2

    compiled_dir = args.folder if is_compiled_input else args.compiled_dir
    targets = target_compiled_files(compiled_dir)
    inventory_errors = target_family_errors(targets)
    metadata, metadata_errors = metadata_for_sources(compiled_dir, targets)
    inventory_errors.extend(metadata_errors)

    if is_compiled_input:
        try:
            asm_dir = disassemble_targets(compiled_dir, targets, args.asm_cache)
        except Exception as error:
            print("disassembly failed:", error)
            return 1
    else:
        asm_dir = args.folder

    records, assembly_errors = collect_assemblies(asm_dir, compiled_dir, targets, metadata)
    inventory_errors.extend(assembly_errors)
    if inventory_errors:
        print("CORPUS / METADATA ERRORS:", len(inventory_errors))
        for error in inventory_errors:
            print("  " + error)
        print("C# allowlist suppressed because compiled variants or FASTBIN metadata are incomplete.")
        return 1

    summaries, failures = verify_families(records)
    print("Building emissive world-space UV proof:")
    for family in FAMILIES:
        summary = summaries[family]
        print(
            f"  {family}: compiled={summary['compiled_sources']} asm={summary['assembly_blobs']} "
            f"PS={summary['pixel_passed']}/{summary['pixel_blobs']} "
            f"VS={summary['vertex_blobs']} GS={summary['geometry_blobs']} "
            f"world-space-emissive-PS={summary['worldspace_emissive_pixel_blobs']} "
            f"model-UV={summary['model_uv']}"
        )
    if failures:
        print("REFUSED:", len(failures), "proof failure(s)")
        for error in (failures if args.verbose else failures[:20]):
            print("  " + error)
        print("C# allowlist suppressed because the runtime sibling group is not fully proven.")
        return 1

    print("\n// C# SharedRepackBuildingWorldspaceVerifiedShaders")
    print("// Conditional proof: use_ws_position_as_uv is specialized to scalar float 1; enforce it at runtime.")
    for family in FAMILIES:
        print(f'"shaders/{family}.xml.shader",')
    return 0


if __name__ == "__main__":
    sys.exit(main())
