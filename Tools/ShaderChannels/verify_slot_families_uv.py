"""Prove the random_colours and flow_overlay slot rules against compiled WH3 shaders.

Usage:
    python3 verify_slot_families_uv.py <fxc-folder-or-asm-folder> [more-folders ...]
        [--asm-cache <directory>] [--verbose]

Compiled folders are scanned for every target slot name and all files in each present runtime
shader sibling group are disassembled. Pre-disassembled folders may be combined in the same way.
Every selected blob must disassemble to a recognized stage; every expected pixel family and its
available alpha/blood siblings must prove the complete sample and UV-use rules below. Unknown,
missing, opaque, or UV-tainted provenance fails closed. The C# allowlists are printed at the end.
"""
import collections
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile

import verify_emissive_uv as v
from shader_slot_provenance import (
    UNPROVEN, VertexProvenance, expected_vertex_entrypoint, fastbin_metadata,
    semantic_component_map, signatures, source_component,
)

MASK1 = 't_xml_mask1'
COLOUR_MAP = 't_xml_colour_map'
FLOW_MAP = 't_xml_flow_map'
FLOW_GUID = 't_xml_df7959d8_d174_49d2_88f1_ffc28b954e5f'
FLOW_NOISE = 't_xml_flow_noise'
FLOW_NORMAL = 't_xml_flow_normal'
FLOW_NORMAL_2 = 't_xml_flow_normal_2'
FLOW_TEXTURE = 't_xml_flow_texture'
FLOW_TEXTURE_2 = 't_xml_flow_texture_2'

RANDOM_FAMILIES = {
    'rigid_random_colours', 'rigid_random_colours_blood',
    'weighted2_random_colours', 'weighted2_random_colours_blood',
    'weighted2_random_colours_alpha_tint', 'weighted2_random_colours_alpha_tint_blood',
    'weighted4_random_colours', 'weighted4_random_colours_blood',
    'weighted4_random_colours_alpha', 'weighted4_random_colours_alpha_blood',
}
FLOW_FAMILIES = {
    'rigid_flow_overlay', 'rigid_flow_overlay_alpha', 'rigid_flow_overlay_blood',
    'weighted2_flow_overlay', 'weighted2_flow_overlay_alpha',
    'weighted2_flow_overlay_alpha_blood', 'weighted2_flow_overlay_blood',
    'weighted4_flow_overlay', 'weighted4_flow_overlay_alpha',
    'weighted4_flow_overlay_alpha_blood', 'weighted4_flow_overlay_blood',
}
EXPECTED_FAMILIES = RANDOM_FAMILIES | FLOW_FAMILIES
FLOW_PARAMETERS = {'flow_noise_speed', 'flow_noise_tiling', 'flow_texture_tiling'}
RAW_SLOTS = set(v.ATLAS_SLOTS) | {MASK1, FLOW_MAP, FLOW_GUID}
TARGET_SLOTS = {
    MASK1, COLOUR_MAP, FLOW_MAP, FLOW_GUID, FLOW_NOISE,
    FLOW_NORMAL, FLOW_NORMAL_2, FLOW_TEXTURE, FLOW_TEXTURE_2,
}
NEEDLES = [slot.encode('ascii') for slot in sorted(TARGET_SLOTS)]
STAGE_RE = re.compile(r'^(ps|vs|gs|hs|ds|cs)_\d_\d$', re.MULTILINE)
SLOT_FAMILY_RE = re.compile(r'^shaders_(.+)_xml_hlsl_[0-9a-f]+\.hlsl_compiled\.(\d+)\.asm$')
VARIANT_RE = re.compile(r'(_alpha_tint_blood|_alpha_tint|_alpha_blood|_clean_alpha|_blood|_alpha)$')


def family_group(name):
    return VARIANT_RE.sub('', name)


def atoms(poly):
    return {atom for monomial in poly for atom in monomial}


class SlotEvaluator(v.Evaluator):
    """The existing symbolic evaluator, with fail-closed provenance for the new slot rules."""
    def __init__(self, shader, mode, input_uv_components=None, provenance_error=None,
                 provenance_complete=False):
        super().__init__(shader)
        self.mode = mode
        self.input_uv_components = input_uv_components or {}
        self.provenance_error = provenance_error
        self.provenance_complete = provenance_complete
        self.pending_branch_taint = False
        self.pending_flow_param_deps = set()
        self.loop_overrides = None
        # The shared evaluator originally recorded only 2D bindings.  Also name cube,
        # array and 3D resources so unrelated engine samples do not become opaque sources.
        self.texture_dimensions = {}
        for match in re.finditer(r'^// (\w+)\s+texture\s+\S+\s+(\S+)\s+(t\d+)', self.sh.text, re.M):
            name, dimension, register = match.groups()
            self.sh.tex.setdefault(register, name)
            self.texture_dimensions[register] = dimension.lower()

    def read(self, base, comp):
        if re.match(r'^r\d+$', base):
            value = self.getreg(base, comp)
            return value if value is not None else v.poly_atom(v.atom('undef', base, comp, taint=True))
        if re.match(r'^v\d+$', base):
            tainted = (base == self.uv0 and comp in (0, 1)) or comp in self.input_uv_components.get(base, set())
            return v.poly_atom(v.atom('in', base, comp, taint=tainted))
        dynamic_cb = re.match(r'^(cb\d+|icb)\[([^]]+)\]$', base)
        if dynamic_cb:
            index = dynamic_cb.group(2)
            if index.isdigit() and dynamic_cb.group(1) != 'icb':
                return super().read(base, comp)
            refs = re.findall(r'v\d+(?:\.[xyzwrgba]+)?|r\d+(?:\.[xyzwrgba]+)?', index)
            residue = re.sub(r'v\d+(?:\.[xyzwrgba]+)?|r\d+(?:\.[xyzwrgba]+)?|0x[0-9a-fA-F]+|\d+(?:\.\d+)?|[+*()\- ]', '', index)
            if residue:
                return v.poly_atom(v.atom('cb_dynamic_unknown', base, comp, taint=True))
            deps = set()
            tainted = False
            for ref in refs:
                value = self.src_comp(ref, 0)
                tainted = tainted or v.p_taint(value)
                deps.update(self.flow_param_deps(value))
            if not index.isdigit() and not refs:
                return v.poly_atom(v.atom('cb_dynamic_unknown', base, comp, taint=True))
            return v.poly_atom(v.atom('cb_dynamic', base, comp, tuple(sorted(deps)), taint=tainted))
        # Direct, unrecognized operands have no proven non-UV0 provenance.
        return v.poly_atom(v.atom('unknown_src', base, comp, taint=True))

    def getreg(self, base, comp):
        if self.loop_overrides is not None and (base, comp) in self.loop_overrides:
            return self.loop_overrides[(base, comp)]
        return super().getreg(base, comp)

    def src_comp(self, token, comp):
        token = token.strip()
        negative = token.startswith('-')
        if negative:
            token = token[1:]
        absolute = token.startswith('|') and token.endswith('|')
        if absolute:
            token = token[1:-1]
        if token.startswith('l('):
            values = [part.strip() for part in token[2:-1].split(',')]
            value = values[comp] if len(values) > 1 else values[0]
            try:
                poly = v.poly_const(float(int(value, 16)) if value.lower().startswith('0x') else float(value))
            except (ValueError, OverflowError):
                poly = v.poly_atom(v.atom('imm_unknown', value, taint=True))
        else:
            match = re.match(r'^(\w+(?:\[[^\]]*\])?)(?:\.([xyzwrgba]+))?$', token)
            if not match:
                poly = v.poly_atom(v.atom('unparsed', token, taint=True))
            else:
                base, swizzle = match.groups()
                source_comp = comp if swizzle is None else (v.COMP[swizzle[comp]] if len(swizzle) > 1 and comp < len(swizzle)
                                                            else v.COMP[swizzle[-1]] if len(swizzle) > 1 else v.COMP[swizzle[0]])
                poly = self.read(base, source_comp)
        if absolute:
            deps = tuple(sorted(self.flow_param_deps(poly)))
            poly = v.poly_atom(v.atom('abs_flowdep', deps, v.p_key(poly), taint=v.p_taint(poly)))
        return v.p_neg(poly) if negative else poly

    def input_semantic_registers(self):
        marker = 'Input signature:'
        if marker not in self.sh.text:
            return {}
        section = self.sh.text.split(marker, 1)[1].split('Output signature:', 1)[0]
        result = {}
        for match in re.finditer(r'^//\s*([A-Za-z_][A-Za-z0-9_]*)\s+(\d+)\s+\S+\s+(\d+)\s+\S+', section, re.M):
            semantic, index, register = match.groups()
            result['v' + register] = (semantic.upper(), int(index))
        return result

    def find_uv0(self):
        candidates = set()
        nonraw = False
        for op, args in self.sh.instructions():
            if not op.startswith(v.SAMPLE_OPS) or len(args) < 3:
                continue
            slot = self.tex_slot(args[2])
            if slot in RAW_SLOTS or slot in (v.DIST, v.DMAP):
                match = re.match(r'^(v\d+)\.([xyzwrgba]+)$', args[1])
                if match and len(match.group(2)) >= 2 and match.group(2)[:2] == 'xy':
                    candidates.add(match.group(1))
                else:
                    nonraw = True
        if nonraw:
            self.issues.append('a cropped slot is sampled at a coordinate that is not a raw interpolator')
        semantics = self.input_semantic_registers()
        if self.provenance_error:
            self.issues.append(self.provenance_error)
        if len(candidates) > 1:
            self.issues.append('cropped slots are sampled at different interpolators')
            return
        if candidates:
            candidate = next(iter(candidates))
            semantic = semantics.get(candidate)
            if semantic is None:
                self.issues.append('raw UV0 anchor register is absent from the pixel input signature')
            self.uv0 = candidate
            return
        if self.provenance_complete:
            return
        if self.input_uv_components:
            return
        referenced_inputs = re.findall(r'\bv\d+(?:\.[xyzwrgba]+)?\b', ' '.join(
            arg for _, args in self.sh.instructions() for arg in args))
        if referenced_inputs and not candidates:
            self.issues.append('no raw UV0 anchor or entrypoint-linked vertex proof for a shader that reads vertex inputs')

    def push(self, kind):
        super().push(kind)
        if kind in ('if', 'switch'):
            self.frames[-1]['uv0_control'] = self.pending_branch_taint
            self.frames[-1]['flow_param_control'] = set(self.pending_flow_param_deps)

    def write(self, dst, comps, polys):
        uv_control = any(frame.get('uv0_control', False) for frame in self.frames)
        flow_deps = set().union(*(frame.get('flow_param_control', set()) for frame in self.frames))
        if uv_control or flow_deps:
            polys = [v.poly_atom(v.atom('control_dep', tuple(sorted(flow_deps)), v.p_key(poly),
                                                taint=uv_control or v.p_taint(poly))) for poly in polys]
        return super().write(dst, comps, polys)

    def pop(self):
        if len(self.frames) < 2:
            return
        frame = self.frames.pop()
        arms = frame['arms'] + [frame['regs']]
        every_arm = frame['kind'] in ('if_else', 'switch_default') and len(frame['arms']) > 0
        for key in {item for arm in arms for item in arm}:
            deps = set().union(*(self.flow_param_deps(arm[key]) for arm in arms if key in arm))
            tainted = any(v.p_taint(arm[key]) for arm in arms if key in arm)
            if not (every_arm and all(key in arm for arm in arms)):
                previous = self.getreg(*key)
                if previous is not None:
                    deps.update(self.flow_param_deps(previous))
                    tainted = tainted or v.p_taint(previous)
            marker = v.atom('cf_flowdep', tuple(sorted(deps)),
                            tuple(sorted(v.p_key(arm[key]) for arm in arms if key in arm)),
                            taint=tainted)
            self.frames[-1]['regs'][key] = v.poly_atom(marker)

    def enter_loop(self, ins, index):
        depth = 0
        end = None
        for cursor in range(index, len(ins)):
            op, _ = ins[cursor]
            if op == 'loop':
                depth += 1
            elif op == 'endloop':
                depth -= 1
                if depth == 0:
                    end = cursor
                    break
        if end is None:
            raise v.Fail('unterminated loop prevents dependency proof')
        body = ins[index + 1:end]
        local = {}
        self.loop_overrides = local
        converged = False
        try:
            # Track both UV taint and compensation dependencies to a fixed point. More than
            # 32 passes is intentionally refused below instead of being treated as complete.
            for _ in range(max(2, min(len(body) + 1, 32))):
                changed = False
                for op, args in body:
                    if op in ('loop', 'endloop') or not args:
                        continue
                    ndst = 2 if op == 'sincos' or op in ('udiv', 'umul', 'imul', 'uaddc', 'usubb') and len(args) >= 4 else 1
                    destinations, sources = args[:ndst], args[ndst:]
                    if op.startswith(v.SAMPLE_OPS) or op == 'ld_indexable' or op.startswith('resinfo'):
                        # Texture results and dimensions are opaque after the ordinary pass
                        # validates their coordinates and extra operands.
                        sources = []
                    elif op.startswith('ld_structured') and len(args) == 4:
                        sources = args[1:3]
                    source_values = [self.src_comp(source, comp) for source in sources for comp in range(4)]
                    source_deps = set().union(*(self.flow_param_deps(poly) for poly in source_values)) if source_values else set()
                    source_tainted = any(v.p_taint(poly) for poly in source_values)
                    for destination in destinations:
                        register, components = self.dst_comps(destination)
                        if not register or not register.startswith('r'):
                            continue
                        for component in components:
                            key = (register, component)
                            deps = set(source_deps)
                            tainted = source_tainted
                            previous = self.getreg(register, component)
                            if previous is not None:
                                deps.update(self.flow_param_deps(previous))
                                tainted = tainted or v.p_taint(previous)
                            prior_local = local.get(key)
                            if prior_local is not None:
                                deps.update(self.flow_param_deps(prior_local))
                                tainted = tainted or v.p_taint(prior_local)
                            marker = v.poly_atom(v.atom('loop_flowdep', tuple(sorted(deps)), taint=tainted))
                            if prior_local != marker:
                                local[key] = marker
                                changed = True
                if not changed:
                    converged = True
                    break
        finally:
            self.loop_overrides = None
        if not converged:
            raise v.Fail('loop dependency fixpoint exceeded 32 passes')
        super().enter_loop(ins, index)
        for key, value in local.items():
            self.frames[-1]['regs'][key] = v.poly_atom(v.atom(
                'loop_flowdep', tuple(sorted(self.flow_param_deps(value))),
                v.p_key(self.getreg(*key)) if self.getreg(*key) is not None else 'unset',
                taint=v.p_taint(value)))

    def flow_param_deps(self, poly):
        deps = set()
        for monomial in poly:
            for atom in monomial:
                if atom[0] == 'sb' and atom[1] in FLOW_PARAMETERS:
                    deps.add(atom[1])
                elif atom[0] == 'op_flowdep':
                    deps.update(atom[4])
                elif atom[0] == 'control_dep':
                    deps.update(atom[1])
                elif atom[0] == 'cf_flowdep':
                    deps.update(atom[1])
                elif atom[0] in ('abs_flowdep', 'loop_flowdep'):
                    deps.update(atom[1])
                elif atom[0] in ('cb_dynamic', 'cb_dynamic_flowdep') and len(atom) > 3:
                    deps.update(atom[3])
        return deps

    def generic_dsts(self, op, args, tainted, keys):
        ndst = 2 if op == 'sincos' or op in ('udiv', 'umul', 'imul', 'uaddc', 'usubb') and len(args) >= 4 else 1
        dsts, srcs = args[:ndst], args[ndst:]
        source_polys = [self.src_comp(src, comp) for src in srcs for comp in range(4)]
        tainted = any(v.p_taint(poly) for poly in source_polys)
        deps = set().union(*(self.flow_param_deps(poly) for poly in source_polys))
        keys = [v.p_key(poly) for poly in source_polys]
        for dst in dsts:
            name, comps = self.dst_comps(dst)
            if name and name != 'null' and name.startswith('o') and tainted:
                self.issues.append('UV0 reaches output %s via %s' % (name, op))
            if name and name != 'null' and name.startswith('o') and deps:
                self.issues.append('compensated flow parameter reaches output %s via %s: %s' %
                                   (name, op, ', '.join(sorted(deps))))
            if name and name != 'null' and name.startswith('r'):
                polys = []
                for comp in comps:
                    digest = __import__('hashlib').md5(''.join(keys).encode()).hexdigest()[:12]
                    kind = 'op_flowdep' if deps else 'op'
                    marker = (tuple(sorted(deps)),) if deps else ()
                    polys.append(v.poly_atom(v.atom(kind, op, comp, digest, *marker, taint=tainted)))
                self.write(name, comps, polys)
        if tainted and not dsts:
            self.issues.append('UV0 used by %s' % op)

    def run(self):
        self.find_uv0()
        ins = list(self.sh.instructions())
        i = 0
        while i < len(ins):
            op, args = ins[i]
            try:
                if op in ('if_nz', 'if_z', 'switch'):
                    sources = [self.src_comp(arg, comp) for arg in args for comp in range(4)]
                    self.pending_branch_taint = any(v.p_taint(poly) for poly in sources)
                    self.pending_flow_param_deps = set().union(*(self.flow_param_deps(poly) for poly in sources))
                    if self.pending_branch_taint:
                        raise v.Fail('UV0-dependent control flow in ' + op)
                    if self.pending_flow_param_deps:
                        raise v.Fail('compensated flow parameter controls ' + op + ': ' + ', '.join(sorted(self.pending_flow_param_deps)))
                    self.push('switch' if op == 'switch' else 'if')
                    self.pending_branch_taint = False
                    self.pending_flow_param_deps = set()
                elif op == 'loop':
                    self.enter_loop(ins, i)
                    self.push('loop')
                elif op == 'else':
                    self.frames[-1]['kind'] = 'if_else'
                    self.next_arm()
                elif op in ('case', 'default'):
                    self.next_arm()
                    if op == 'default':
                        self.frames[-1]['kind'] = 'switch_default'
                elif op in ('endif', 'endloop', 'endswitch'):
                    self.pop()
                elif op in ('breakc', 'continuec', 'discard_nz', 'discard_z', 'retc'):
                    active_uv = any(frame.get('uv0_control', False) for frame in self.frames)
                    active_flow = set().union(*(frame.get('flow_param_control', set()) for frame in self.frames))
                    if active_uv or any(v.p_taint(self.src_comp(arg, comp)) for arg in args for comp in range(4)):
                        self.issues.append('UV0-dependent control flow in ' + op)
                    elif active_flow:
                        self.issues.append('compensated flow parameter controls ' + op + ': ' + ', '.join(sorted(active_flow)))
                else:
                    self.step(op, args)
            except v.Fail as error:
                self.issues.append(str(error))
                # Stop on the first proof failure in this blob.  Continuing with an unwritten
                # sample destination manufactures stale-register errors that obscure the root.
                break
            i += 1

    def step(self, op, args):
        if op.startswith('resinfo') and len(args) >= 3:
            resource = args[-1]
            slot = self.tex_slot(resource)
            if slot is None:
                raise v.Fail('resource dimension query has no parsed resource binding: ' + resource)
            for operand in args[1:-1]:
                for comp in range(4):
                    value = self.src_comp(operand, comp)
                    if v.p_taint(value):
                        raise v.Fail('UV0 feeds the LOD operand of a resource dimension query')
                    if self.flow_param_deps(value):
                        raise v.Fail('compensated flow parameter feeds a resource dimension query')
            dst, dcomps = self.dst_comps(args[0])
            self.write(dst, dcomps, [v.poly_atom(v.atom('resinfo', slot, comp)) for comp in dcomps])
            return
        if op == 'ld_indexable' and len(args) == 3:
            slot = self.tex_slot(args[2])
            if slot is None:
                raise v.Fail('indexed texture load has no parsed resource binding: ' + args[2])
            index = [self.src_comp(args[1], c) for c in range(4)]
            if any(v.p_taint(poly) for poly in index):
                raise v.Fail(slot + ' indexed-load coordinate depends on UV0')
            deps = set().union(*(self.flow_param_deps(poly) for poly in index))
            if deps:
                raise v.Fail(slot + ' indexed-load coordinate uses compensated flow parameter ' + ', '.join(sorted(deps)))
            dst, comps = self.dst_comps(args[0])
            key = ''.join(v.p_key(poly) for poly in index)
            self.write(dst, comps, [v.poly_atom(v.atom('texload', slot, key, comp)) for comp in comps])
            return
        if op.startswith(v.SAMPLE_OPS) and len(args) >= 4:
            slot = self.tex_slot(args[2])
            if slot is None:
                raise v.Fail('texture sample has no parsed resource binding: ' + args[2])
            return self.do_sample(op, args)
        return super().step(op, args)

    def do_sample(self, op, args):
        slot = self.tex_slot(args[2])
        register = re.match(r'^(t\d+)(?:\.[xyzw]+)?$', args[2].strip())
        dimension = self.texture_dimensions.get(register.group(1)) if register else None
        coordinate_count = {'1d': 1, '1darray': 2, '2d': 2, '2darray': 3,
                            '3d': 3, 'cube': 3, 'cubearray': 4}.get(dimension)
        if coordinate_count is None:
            raise v.Fail(str(slot) + ' has an unproven sample resource dimension: ' + str(dimension))
        coordinates = [self.src_comp(args[1], comp) for comp in range(coordinate_count)]
        extras = [self.src_comp(arg, comp) for arg in args[4:] for comp in range(4)]
        if any(v.p_taint(poly) for poly in extras):
            raise v.Fail('UV0 feeds the LOD/gradient/reference operand of a sample of ' + str(slot))
        extra_deps = set().union(*(self.flow_param_deps(poly) for poly in extras))
        if extra_deps:
            raise v.Fail(str(slot) + ' extra operand uses compensated flow parameter: ' + ', '.join(sorted(extra_deps)))
        active_flow_deps = set().union(*(frame.get('flow_param_control', set()) for frame in self.frames))
        if slot in TARGET_SLOTS and active_flow_deps:
            raise v.Fail(slot + ' sampling is controlled by compensated flow parameter: ' + ', '.join(sorted(active_flow_deps)))
        if slot not in TARGET_SLOTS:
            flow_deps = set().union(*(self.flow_param_deps(poly) for poly in coordinates))
            if flow_deps:
                raise v.Fail(str(slot) + ' sample uses compensated flow parameter outside a verified flow sample: ' + ', '.join(sorted(flow_deps)))
            known_2d = v.ATLAS_SLOTS | {v.DIST, v.DMAP, v.DDET, v.EMIS, v.BLOOD}
            if slot in known_2d:
                if dimension != '2d':
                    raise v.Fail(slot + ' requires a 2D resource for its verified UV rule')
            elif any(v.p_taint(poly) for poly in coordinates):
                raise v.Fail(str(slot) + ' sample coordinate depends on UV0')
            return v.Evaluator.do_sample(self, op, args)
        supported = ({MASK1, COLOUR_MAP} if self.mode == 'random' else
                     {FLOW_MAP, FLOW_GUID, FLOW_NOISE, FLOW_NORMAL, FLOW_NORMAL_2})
        if slot not in supported:
            raise v.Fail(slot + ' has no proof rule in the ' + self.mode + ' family')
        if dimension != '2d':
            raise v.Fail(slot + ' requires a 2D resource for its verified UV rule')
        uvx, uvy = self.src_comp(args[1], 0), self.src_comp(args[1], 1)
        if slot == MASK1:
            self.check_raw(slot, uvx, uvy)
        elif slot == COLOUR_MAP and self.mode == 'random':
            if v.p_taint(uvx) or v.p_taint(uvy):
                raise v.Fail(COLOUR_MAP + ' palette coordinate depends on UV0')
            self.found['palette_independent'] += 1
            self.found[COLOUR_MAP] += 1
        elif slot in (FLOW_MAP, FLOW_GUID):
            self.check_raw(slot, uvx, uvy)
        elif slot == FLOW_NOISE and self.mode == 'flow':
            self.check_flow_noise(uvx, uvy)
        elif slot in (FLOW_NORMAL, FLOW_NORMAL_2) and self.mode == 'flow':
            self.check_flow_normal(slot, uvx, uvy)
        # We write verified sample atoms ourselves.  The base evaluator only knows the old
        # atlas/dissolve/emissive slots and would reject these intentionally UV-dependent slots
        # after our proof, leaving destination registers stale on later instructions.
        self.emit_verified_sample(op, args)

    def emit_verified_sample(self, op, args):
        dst, dcomps = self.dst_comps(args[0])
        slot = self.tex_slot(args[2])
        res = re.match(r'^(t\d+)\.([xyzw]+)$', args[2].strip())
        swizzle = res.group(2) if res else 'xyzw'
        key = v.p_key(self.src_comp(args[1], 0)) + v.p_key(self.src_comp(args[1], 1))
        raw = slot in RAW_SLOTS or slot in (v.DIST, v.DMAP)
        out = []
        for c in dcomps:
            ch = v.COMP[swizzle[c]] if c < len(swizzle) else v.COMP[swizzle[-1]]
            out.append(v.poly_atom(v.atom('tex', slot, 'rawUV0' if raw else key, ch)))
        self.write(dst, dcomps, out)

    def check_raw(self, slot, uvx, uvy):
        if self.uv0 is None:
            raise v.Fail(slot + ' without a raw UV0 anchor')
        expected = [v.poly_atom(v.atom('in', self.uv0, axis, taint=True)) for axis in range(2)]
        if [uvx, uvy] != expected:
            raise v.Fail(slot + ' is not sampled at raw UV0')
        self.found[slot] += 1

    def check_flow_noise(self, uvx, uvy):
        if self.uv0 is None:
            raise v.Fail(FLOW_NOISE + ' without a raw UV0 anchor')
        time = v.poly_atom(v.atom('cb', 'camera', 'time_in_sec'))
        speed = v.poly_atom(v.atom('sb', 'flow_noise_speed', 0))
        tiling = v.poly_atom(v.atom('sb', 'flow_noise_tiling', 0))
        for axis, coord in enumerate((uvx, uvy)):
            uv = v.poly_atom(v.atom('in', self.uv0, axis, taint=True))
            residual = v.p_add(coord, v.p_neg(v.p_mul(uv, tiling)))
            expected = tuple(sorted((next(iter(time))[0], next(iter(speed))[0], next(iter(tiling))[0]), key=repr))
            if len(residual) != 1 or next(iter(residual)) != expected or next(iter(residual.values())) == 0:
                raise v.Fail(FLOW_NOISE + ' phase must be a nonzero scalar multiple of time*flow_noise_speed*flow_noise_tiling')
        self.found[FLOW_NOISE] += 1

    def check_flow_normal(self, slot, uvx, uvy):
        if self.uv0 is None:
            raise v.Fail(slot + ' without a raw UV0 anchor')
        for axis, coord in enumerate((uvx, uvy)):
            uv = v.poly_atom(v.atom('in', self.uv0, axis, taint=True))
            scale = v.poly_atom(v.atom('sb', 'flow_texture_tiling', axis))
            residual = v.p_add(coord, v.p_neg(v.p_mul(uv, scale)))
            if v.p_taint(residual):
                raise v.Fail(slot + ' has UV0 outside its flow_texture_tiling term')
            deps = self.flow_param_deps(residual)
            if deps:
                raise v.Fail(slot + ' residual uses compensated flow parameter outside a verified sample: ' + ', '.join(sorted(deps)))
        self.found[slot] += 1


def analyze(path, mode, input_uv_components=None, provenance_error=None, provenance_complete=False):
    with open(path, errors='replace') as stream:
        text = stream.read()
    shader = v.Shader(text)
    if shader.stage != 'ps':
        return None
    evaluator = SlotEvaluator(shader, mode, input_uv_components, provenance_error, provenance_complete)
    evaluator.run()
    return evaluator


def assembly_corpus_errors(folder):
    """Check cached inputs as rigorously as newly disassembled blobs."""
    errors = []
    manifest = os.path.join(folder, '_list.txt')
    if not os.path.isfile(manifest):
        errors.append('pre-disassembled input lacks its complete _list.txt manifest')
        selected = None
    else:
        with open(manifest, encoding='utf-8') as stream:
            selected = set(stream.read().splitlines())
    present = set()
    for name in sorted(os.listdir(folder)):
        if not name.endswith('.asm'):
            continue
        match = re.match(r'^(.*\.hlsl_compiled)\.\d+\.asm$', name)
        if match:
            present.add(match.group(1))
        with open(os.path.join(folder, name), errors='replace') as stream:
            text = stream.read()
        if STAGE_RE.search(text) is None or re.search(r'(?m)^HRESULT\s+', text):
            errors.append('invalid or failed shader disassembly: ' + name)
    if selected is not None:
        errors.extend('no disassembly for selected source ' + name for name in sorted(selected - present))
        errors.extend('stale disassembly outside the selection manifest: ' + name for name in sorted(present - selected))
    return errors


def disassembly_errors(source, asm_folder):
    manifest = os.path.join(asm_folder, '_list.txt')
    if not os.path.isfile(manifest):
        return ['disassembler did not write _list.txt']
    selected = set(open(manifest, encoding='utf-8').read().splitlines())
    compiled = [name for name in sorted(os.listdir(source))
                if name.endswith('.hlsl_compiled') and 'xml_hlsl' in name]
    matching = []
    groups = set()
    for name in compiled:
        with open(os.path.join(source, name), 'rb') as stream:
            payload = stream.read()
        if any(needle in payload for needle in NEEDLES):
            matching.append(name)
            family = name.split('_xml_hlsl_')[0][len('shaders_'):]
            groups.add(family_group(family))
    expected = {name for name in compiled
                if family_group(name.split('_xml_hlsl_')[0][len('shaders_'):]) in groups}
    errors = []
    if selected != expected:
        errors.append(f'disassembly selection mismatch: missing {len(expected - selected)}, extra {len(selected - expected)}')
    files = [name for name in os.listdir(asm_folder) if name.endswith('.asm')]
    by_source = collections.Counter()
    for name in files:
        match = re.match(r'^(.*\.hlsl_compiled)\.\d+\.asm$', name)
        if match:
            by_source[match.group(1)] += 1
    for name in sorted(selected):
        if by_source[name] == 0:
            errors.append('no DXBC blobs disassembled for ' + name)
    for name in files:
        path = os.path.join(asm_folder, name)
        with open(path, errors='replace') as stream:
            data = stream.read()
        if re.search(r'(?m)^HRESULT\s+', data):
            errors.append('failed DXBC disassembly: ' + name)
        if STAGE_RE.search(data) is None:
            errors.append('no recognized shader stage in ' + name)
    return errors


def disassemble_slots(source, out):
    """Use a fresh output folder and preserve each FASTBIN stage/entrypoint association."""
    os.makedirs(out, exist_ok=False)
    compiled = [name for name in sorted(os.listdir(source))
                if name.endswith('.hlsl_compiled') and 'xml_hlsl' in name]
    matches, groups = [], set()
    for name in compiled:
        with open(os.path.join(source, name), 'rb') as stream:
            payload = stream.read()
        if any(needle in payload for needle in NEEDLES):
            matches.append(name)
            family = name.split('_xml_hlsl_')[0][len('shaders_'):]
            groups.add(family_group(family))
    selected = [name for name in compiled
                if family_group(name.split('_xml_hlsl_')[0][len('shaders_'):]) in groups]
    manifest = os.path.join(out, '_list.txt')
    with open(manifest, 'w', encoding='utf-8') as stream:
        stream.write('\n'.join(selected))
    here = os.path.dirname(os.path.abspath(v.__file__))
    to_win = (lambda path: subprocess.check_output(['wslpath', '-w', path]).decode().strip()) if os.name != 'nt' else (lambda path: path)
    subprocess.check_call(['powershell.exe', '-NoProfile', '-File', to_win(os.path.join(here, 'dxbc_disassemble.ps1')),
                           '-InDir', to_win(source), '-OutDir', to_win(out), '-List', to_win(manifest)])
    metadata, errors = {}, []
    for name in selected:
        family = name.split('_xml_hlsl_')[0][len('shaders_'):]
        with open(os.path.join(source, name), 'rb') as stream:
            parsed = fastbin_metadata(stream.read())
        if parsed is None:
            if 'random_colours' in family or 'flow_overlay' in family or 'emissive_flow_alpha' in family:
                errors.append('missing or ambiguous FASTBIN stage/entrypoint metadata for ' + name)
        else:
            metadata[name] = {'entrypoint': parsed[0], 'stage': parsed[1]}
    with open(os.path.join(out, '_entrypoints.json'), 'w', encoding='utf-8') as stream:
        json.dump(metadata, stream, sort_keys=True, indent=2)
    return out, errors


def source_name_for_asm(path):
    match = re.match(r'^(.*\.hlsl_compiled)\.\d+\.asm$', os.path.basename(path))
    return match.group(1) if match else None


def pixel_entrypoint(path, metadata):
    return metadata.get(os.path.abspath(path))


def raw_anchor_semantics(path, slots):
    with open(path, errors='replace') as stream:
        shader = v.Shader(stream.read())
    signatures_by_component = semantic_component_map(shader.text)
    anchors = set()
    for op, args in shader.instructions():
        if not op.startswith(v.SAMPLE_OPS) or len(args) < 3 or shader.tex.get(args[2].split('.')[0]) not in slots:
            continue
        match = re.match(r'^(v\d+)\.([xyzwrgba]+)$', args[1])
        if not match or len(match.group(2)) < 2 or match.group(2)[:2] != 'xy':
            return None, 'raw atlas sample uses a non-interpolator or non-xy coordinate'
        register = match.group(1)
        x = signatures_by_component.get((register, 0))
        y = signatures_by_component.get((register, 1))
        if x is None or x != y:
            return None, 'raw atlas anchor does not have one semantic on both coordinate axes'
        anchors.add((register, x))
    if len(anchors) > 1:
        return None, 'multiple raw UV anchor registers/semantics in one pixel shader'
    return anchors, None


def referenced_input_components(shader):
    result = collections.defaultdict(set)
    for _, args in shader.instructions():
        for arg in args:
            match = re.match(r'^-?\|?(v\d+)(?:\.([xyzwrgba]+))?\|?$', arg.strip())
            if not match:
                continue
            register, swizzle = match.groups()
            if swizzle is None:
                result[register].update(range(4))
            elif len(swizzle) == 1:
                result[register].add(v.COMP[swizzle[0]])
            else:
                result[register].update(v.COMP[letter] for letter in swizzle)
    return result


def strict_model_uv_alias_proof(text, semantic):
    """Prove model UV x/y are only copied directly to VS outputs.

    UV x/y may be direct, unmodified mov sources to output components only. No arithmetic,
    temp, index, control, or output feedback can carry these components elsewhere.
    """
    proof = VertexProvenance(text)
    register_info = proof.inputs.get(semantic)
    if register_info is None:
        return None, 'model UV input semantic is absent'
    register, mask = register_info
    if not {'x', 'y'} <= set(mask):
        return None, 'model UV input semantic does not provide x/y'
    uv_reg = 'v' + str(register)
    aliases = set()
    input_ref = re.compile(r'(?<![A-Za-z0-9_])' + re.escape(uv_reg) + r'(?![A-Za-z0-9_])')
    direct_source = re.compile(r'^' + re.escape(uv_reg) + r'(?:\.([xyzwrgba]+))?$')
    for op, args in proof.shader.instructions():
        for arg_index, arg in enumerate(args):
            if not input_ref.search(arg):
                continue
            if arg_index == 0 or op != 'mov' or len(args) != 2 or arg_index != 1:
                return None, f'{uv_reg} is used outside a direct output mov ({op})'
            dst = re.fullmatch(r'o(\d+)\.([xyzwrgba]+)', args[0].strip())
            src = direct_source.fullmatch(arg.strip())
            if dst is None or src is None:
                return None, f'{uv_reg} is modified or copied outside an output register ({op})'
            dst_register, dest_mask = dst.groups()
            swizzle = src.group(1)
            for dest_letter in dest_mask:
                source_comp = source_component(swizzle, v.COMP[dest_letter])
                if source_comp in (0, 1):
                    aliases.add(('o' + dst_register, v.COMP[dest_letter]))
        for source in args[1:]:
            for match in re.finditer(r'(?<![A-Za-z0-9_])o(\d+)(?:\.([xyzwrgba]+))?', source.strip()):
                output_reg, swizzle = match.groups()
                comps = range(4) if swizzle is None else {v.COMP[ch] for ch in swizzle}
                if any(('o' + output_reg, comp) in aliases for comp in comps):
                    return None, 'model UV output alias is read as a later instruction source'
    return aliases, None


def vertex_proofs_for_family(family, paths, metadata, mode):
    """Derive UV0 source semantics and check each PS's exact linked VS entrypoint."""
    failures = []
    family_errors = []
    analyses = {}
    for path in paths:
        meta = pixel_entrypoint(path, metadata)
        if not meta or meta.get('stage') != 'VS':
            continue
        try:
            with open(path, errors='replace') as stream:
                analyses[path] = VertexProvenance(stream.read()).run()
        except Exception as error:
            failures.append(f'{os.path.basename(path)}: VS provenance failed: {error}')

    def linked_vertex_paths(pixel_ep):
        entrypoint = expected_vertex_entrypoint(family, pixel_ep)
        linked = [path for path, proof in analyses.items()
                  if pixel_entrypoint(path, metadata).get('entrypoint') == entrypoint]
        if not linked:
            return None, entrypoint
        return linked, entrypoint

    anchor_slots = {MASK1} if mode == 'random' else {FLOW_MAP, FLOW_GUID}
    main_uv_sources = set()
    main_anchors = 0
    main_vs, main_ep = linked_vertex_paths('ps_main_gbuffer')
    if main_vs is None:
        family_errors.append('missing FASTBIN-linked VS entrypoint ' + main_ep)
    else:
        for path in paths:
            meta = pixel_entrypoint(path, metadata)
            if not meta or meta.get('stage') != 'PS' or meta.get('entrypoint') != 'ps_main_gbuffer':
                continue
            anchors, error = raw_anchor_semantics(path, anchor_slots)
            if error:
                failures.append(os.path.basename(path) + ': ' + error)
                continue
            if not anchors:
                continue
            main_anchors += 1
            register, semantic = next(iter(anchors))
            for vertex_path in main_vs:
                proof = analyses[vertex_path]
                if proof.outputless_stub:
                    continue
                if proof.issues:
                    family_errors.append(os.path.basename(vertex_path) + ': ' + '; '.join(proof.issues))
                    continue
                axes = proof.semantic_components.get(semantic, {})
                x, y = axes.get(0, {UNPROVEN}), axes.get(1, {UNPROVEN})
                if x != y or len(x) != 1 or UNPROVEN in x:
                    family_errors.append(os.path.basename(vertex_path) + ': UV anchor output is not a direct, unambiguous model input')
                else:
                    main_uv_sources.update(x)
        if main_anchors == 0:
            family_errors.append('no ps_main_gbuffer raw target-slot anchor found to identify model UV0')
    if len(main_uv_sources) != 1:
        family_errors.append('main UV anchor does not resolve to exactly one model input semantic: ' + repr(sorted(main_uv_sources, key=repr)))
    model_uv = next(iter(main_uv_sources)) if len(main_uv_sources) == 1 else None
    if model_uv is not None and family.startswith(('weighted2_flow_overlay', 'weighted4_flow_overlay')):
        for vertex_path, proof in analyses.items():
            if proof.outputless_stub and not proof.has_output_writes:
                continue
            try:
                with open(vertex_path, errors='replace') as stream:
                    _, structural_error = strict_model_uv_alias_proof(stream.read(), model_uv)
            except Exception as error:
                structural_error = 'strict model UV structural analysis failed: ' + str(error)
            if structural_error:
                family_errors.append(os.path.basename(vertex_path) + ': ' + structural_error)

    ps_components = {}
    for path in paths:
        try:
            with open(path, errors='replace') as stream:
                shader = v.Shader(stream.read())
        except Exception as error:
            failures.append(os.path.basename(path) + ': assembly parse failed: ' + str(error))
            continue
        if shader.stage != 'ps':
            continue
        meta = pixel_entrypoint(path, metadata)
        if not meta or meta.get('stage') != 'PS':
            failures.append(os.path.basename(path) + ': missing FASTBIN pixel entrypoint metadata')
            ps_components[path] = ({}, 'missing FASTBIN pixel entrypoint metadata')
            continue
        linked, entrypoint = linked_vertex_paths(meta['entrypoint'])
        if linked is None:
            failures.append(os.path.basename(path) + ': missing FASTBIN-linked VS entrypoint ' + entrypoint)
            ps_components[path] = ({}, 'missing FASTBIN-linked VS entrypoint ' + entrypoint)
            continue
        pixel_semantics = semantic_component_map(shader.text)
        referenced = referenced_input_components(shader)
        uv_components = collections.defaultdict(set)
        error = '; '.join(family_errors) if family_errors else None
        raw_anchors, anchor_error = raw_anchor_semantics(path, RAW_SLOTS | {v.DIST, v.DMAP})
        if anchor_error:
            error = anchor_error
        for register, comps in referenced.items():
            for comp in comps:
                semantic = pixel_semantics.get((register, comp))
                if semantic is None:
                    error = error or f'{register}.{"xyzw"[comp]} has no pixel input semantic'
                    continue
                component_deps = []
                for vertex_path in linked:
                    proof = analyses[vertex_path]
                    if proof.outputless_stub and not proof.has_output_writes:
                        continue
                    if proof.issues:
                        error = error or f'{os.path.basename(vertex_path)}: ' + '; '.join(proof.issues)
                        continue
                    deps = proof.semantic_components.get(semantic, {}).get(comp)
                    if deps is None:
                        error = error or f'{os.path.basename(vertex_path)} does not provide {semantic}.{"xyzw"[comp]}'
                    else:
                        component_deps.append(deps)
                if component_deps:
                    deps = set().union(*component_deps)
                    if UNPROVEN in deps:
                        error = error or f'{register}.{"xyzw"[comp]} has unproven linked VS provenance'
                    if model_uv is not None and model_uv in deps:
                        uv_components[register].add(comp)
                elif not all(analyses[vp].outputless_stub and not analyses[vp].has_output_writes for vp in linked):
                    error = error or f'no linked VS output proves {register}.{"xyzw"[comp]}'
        if raw_anchors:
            register, semantic = next(iter(raw_anchors))
            if model_uv is not None:
                for vertex_path in linked:
                    proof = analyses[vertex_path]
                    if proof.outputless_stub and not proof.has_output_writes:
                        continue
                    axes = proof.semantic_components.get(semantic, {})
                    if axes.get(0) != {model_uv} or axes.get(1) != {model_uv}:
                        error = error or f'raw atlas anchor {semantic} is not the model UV0 output of {entrypoint}'
                        break
        if error:
            failures.append(os.path.basename(path) + ': ' + error)
        ps_components[path] = (dict(uv_components), error)
    return model_uv, ps_components, family_errors + failures


def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    verbose = '--verbose' in argv
    argv = [arg for arg in argv if arg != '--verbose']
    cache = None
    if '--asm-cache' in argv:
        index = argv.index('--asm-cache')
        if index + 1 >= len(argv):
            print('--asm-cache requires a directory')
            return 2
        cache = argv[index + 1]
        del argv[index:index + 2]
    folders = [arg for arg in argv if not arg.startswith('--')]
    if not folders:
        print(__doc__)
        return 2

    asm_folders = []
    global_errors = []
    cache_root = cache or tempfile.mkdtemp(prefix='verify-slot-families-')
    os.makedirs(cache_root, exist_ok=True)
    run_cache = tempfile.mkdtemp(prefix='run-', dir=cache_root)
    metadata_by_path = {}
    for index, folder in enumerate(folders):
        files = sorted(os.listdir(folder))
        if any(name.endswith('.hlsl_compiled') for name in files):
            out = os.path.join(run_cache, f'input-{index}')
            try:
                asm, metadata_errors = disassemble_slots(folder, out)
            except Exception as error:
                global_errors.append(f'{folder}: disassembly failed: {error}')
                continue
            global_errors.extend(f'{folder}: {error}' for error in metadata_errors)
            global_errors.extend(f'{folder}: {error}' for error in disassembly_errors(folder, asm))
            asm_folders.append(asm)
        else:
            global_errors.extend(f'{folder}: {error}' for error in assembly_corpus_errors(folder))
            sidecar = os.path.join(folder, '_entrypoints.json')
            if os.path.isfile(sidecar):
                try:
                    with open(sidecar, encoding='utf-8') as stream:
                        source_metadata = json.load(stream)
                    for name in os.listdir(folder):
                        if not name.endswith('.asm'):
                            continue
                        source_name = source_name_for_asm(os.path.join(folder, name))
                        meta = source_metadata.get(source_name)
                        if meta:
                            metadata_by_path[os.path.abspath(os.path.join(folder, name))] = meta
                except Exception as error:
                    global_errors.append(f'{folder}: invalid _entrypoints.json: {error}')
            asm_folders.append(folder)

    by_family = collections.defaultdict(list)
    seen = set()
    for asm in asm_folders:
        for name in sorted(os.listdir(asm)):
            if not name.endswith('.asm'):
                continue
            match = SLOT_FAMILY_RE.match(name)
            if not match:
                global_errors.append(f'{asm}/{name}: malformed shader filename')
                continue
            path = os.path.join(asm, name)
            if os.path.abspath(path) not in metadata_by_path:
                sidecar = os.path.join(asm, '_entrypoints.json')
                if os.path.isfile(sidecar):
                    try:
                        with open(sidecar, encoding='utf-8') as stream:
                            source_metadata = json.load(stream)
                        source_name = source_name_for_asm(path)
                        if source_name and source_name in source_metadata:
                            metadata_by_path[os.path.abspath(path)] = source_metadata[source_name]
                    except Exception as error:
                        global_errors.append(f'{asm}: invalid _entrypoints.json: {error}')
            relevant_family = ('random_colours' in match.group(1) or 'flow_overlay' in match.group(1) or
                               'emissive_flow_alpha' in match.group(1))
            if os.path.abspath(path) not in metadata_by_path and relevant_family:
                global_errors.append(f'{asm}/{name}: missing FASTBIN entrypoint metadata; no family output can be generated')
            with open(path, 'rb') as stream:
                digest = hashlib.sha256(stream.read()).hexdigest()
            if (name, digest) in seen:
                continue
            seen.add((name, digest))
            by_family[match.group(1)].append(path)

    absent = EXPECTED_FAMILIES - set(by_family)
    for family in sorted(absent):
        global_errors.append('expected shader family is absent from the selected corpus: ' + family)
    for family in sorted(by_family):
        if ('random_colours' in family and family not in RANDOM_FAMILIES) or \
                ('flow_overlay' in family and family not in FLOW_FAMILIES):
            global_errors.append('unlisted target shader family requires an explicit proof/refusal rule: ' + family)

    candidates = collections.defaultdict(lambda: {'ok': 0, 'bad': 0, 'found': collections.Counter(), 'reasons': collections.Counter()})
    modes = {name: 'random' for name in RANDOM_FAMILIES}
    modes.update({name: 'flow' for name in FLOW_FAMILIES})
    family_groups = collections.defaultdict(list)
    for family, paths in by_family.items():
        family_groups[family_group(family)].extend(paths)
    group_semantics = {}
    family_contexts = {}
    for family, paths in by_family.items():
        if family not in modes:
            continue
        model_uv, ps_components, proof_errors = vertex_proofs_for_family(
            family, paths, metadata_by_path, modes[family])
        family_contexts[family] = ps_components
        if verbose and proof_errors:
            print(f'VERTEX PROVENANCE {family}: {len(proof_errors)} issue(s); model UV {model_uv}')
            for reason in proof_errors[:8]:
                print('  ' + reason)
    for family, paths in sorted(by_family.items()):
        mode = modes.get(family)
        if mode is None:
            continue
        fam = candidates[family]
        for path in paths:
            try:
                input_uv, provenance_error = family_contexts.get(family, {}).get(path, ({}, 'missing vertex provenance'))
                ev = analyze(path, mode, input_uv, provenance_error, provenance_complete=provenance_error is None)
            except Exception as error:
                fam['bad'] += 1
                fam['reasons']['analysis exception: ' + str(error)] += 1
                continue
            if ev is None:
                continue
            fam['found'].update(ev.found)
            if ev.issues:
                fam['bad'] += 1
                for reason in set(ev.issues):
                    fam['reasons'][reason] += 1
            else:
                fam['ok'] += 1

    # A group is only as verified as all present alpha/blood siblings.
    groups = collections.defaultdict(list)
    for family in by_family:
        groups[family_group(family)].append(family)
    good_random, good_flow = [], []
    for family in sorted(EXPECTED_FAMILIES):
        mode = modes[family]
        fam = candidates[family]
        reasons = fam['reasons']
        needed = ({MASK1: fam['found'][MASK1], COLOUR_MAP: fam['found'][COLOUR_MAP]}
                  if mode == 'random' else
                  {slot: fam['found'][slot] for slot in (FLOW_MAP, FLOW_GUID, FLOW_NOISE, FLOW_NORMAL, FLOW_NORMAL_2)})
        if fam['ok'] == 0:
            reasons['no pixel shader variants found'] += 1
        for slot, count in needed.items():
            if count == 0:
                reasons['no verified ' + slot + ' sample found'] += 1
        group_name = family_group(family)
        for sibling in groups[group_name]:
            if sibling == family:
                continue
            sibling_mode = modes.get(sibling)
            if sibling_mode != mode:
                reasons['runtime sibling ' + sibling + ' is outside the verified family set'] += 1
                continue
            sibling_result = candidates[sibling]
            if sibling_result['bad'] or sibling_result['ok'] == 0:
                reasons['runtime sibling ' + sibling + ' does not verify'] += 1
        verified = fam['ok'] > 0 and fam['bad'] == 0 and not reasons
        if verified and mode == 'random':
            good_random.append(family)
        elif verified:
            good_flow.append(family)

    print('Selected disassembly blobs:', len(seen), 'families:', len(by_family))
    print('Random-colour shader families: %d, qualifying: %d, excluded: %d' % (
        sum(1 for family in by_family if family in RANDOM_FAMILIES), len(good_random),
        len([family for family in RANDOM_FAMILIES if family not in good_random])))
    for family in sorted(RANDOM_FAMILIES):
        fam = candidates[family]
        status = 'VERIFIED' if family in good_random else 'REFUSED'
        print(f'  {status} {family}: PS {fam["ok"]} ok / {fam["bad"]} failed; {dict(fam["found"])}')
        if family not in good_random:
            for reason, count in fam['reasons'].most_common(5):
                print(f'    {count} x {reason}')
    print('Flow-overlay shader families: %d, qualifying: %d, excluded: %d' % (
        sum(1 for family in by_family if family in FLOW_FAMILIES), len(good_flow),
        len([family for family in FLOW_FAMILIES if family not in good_flow])))
    for family in sorted(FLOW_FAMILIES):
        fam = candidates[family]
        status = 'VERIFIED' if family in good_flow else 'REFUSED'
        print(f'  {status} {family}: PS {fam["ok"]} ok / {fam["bad"]} failed; {dict(fam["found"])}')
        if family not in good_flow:
            for reason, count in fam['reasons'].most_common(5):
                print(f'    {count} x {reason}')

    all_names = set(by_family)
    if 'weighted2_random_colours_alpha' not in all_names:
        print('REFUSED weighted2_random_colours_alpha: no compiled shader variant in the supplied dump; remains unverified')
    for family in sorted(name for name in all_names if 'emissive_flow_alpha' in name):
        print('REFUSED', family + ': fixed shader flow-noise scroll speed and weighted flow_texture UV has no compensatable scale parameter')
    if global_errors:
        print('DISASSEMBLY / CORPUS ERRORS:', len(global_errors))
        for error in global_errors[:25]:
            print('  ' + error)

    def print_allowlist(title, families):
        print('\n// C# ' + title + ' (generated by verify_slot_families_uv.py)')
        for family in families:
            print(f'"shaders/{family}.xml.shader",')
    if global_errors:
        print('\n// All generated allowlists suppressed because corpus/selection errors exist.')
    else:
        print_allowlist('SharedRepackRandomColoursVerifiedShaders', good_random)
        print_allowlist('SharedRepackFlowVerifiedShaders', good_flow)
    if global_errors:
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
