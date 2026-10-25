"""Entrypoint-linked, component-level vertex input provenance for shader slot proofs."""
import re

import verify_emissive_uv as v

UNPROVEN = '<unproven>'
ENTRY_RE = re.compile(rb'(?:ps|vs|gs|hs|ds|cs)[A-Za-z0-9]*_[A-Za-z0-9_]+')


def fastbin_metadata(payload):
    matches = []
    for match in ENTRY_RE.finditer(payload):
        entrypoint = match.group().decode('ascii', 'replace')
        if int.from_bytes(payload[match.start() - 2:match.start()], 'little') != len(entrypoint):
            continue
        stage = payload[match.end() + 2:match.end() + 4].decode('ascii', 'replace')
        if stage in ('PS', 'VS', 'GS', 'HS', 'DS', 'CS'):
            matches.append((entrypoint, stage))
    return matches[0] if len(matches) == 1 else None


def signatures(text, marker):
    if marker not in text:
        return {}
    section = text.split(marker, 1)[1]
    terminator = 'Output signature:' if marker == 'Input signature:' else 'vs_5_0'
    if terminator in section:
        section = section.split(terminator, 1)[0]
    result = {}
    row = re.compile(r'^//\s*([A-Za-z_][A-Za-z0-9_]*)\s+(\d+)\s+(\S+)\s+(\d+)\s+\S+', re.M)
    for match in row.finditer(section):
        semantic, index, mask, register = match.groups()
        result[(semantic.upper(), int(index))] = (int(register), mask)
    return result


def parse_operand(token):
    match = re.match(r'^(-?)(?:\|)?([A-Za-z_]\w*(?:\[[^\]]*\])?|l\(.*\))(?:\.([xyzwrgba]+))?(?:\|)?$', token.strip())
    return (match.group(2), match.group(3)) if match else None


def source_component(swizzle, lane):
    if swizzle is None:
        return lane
    if len(swizzle) == 1:
        return v.COMP[swizzle[0]]
    return v.COMP[swizzle[lane]] if lane < len(swizzle) else v.COMP[swizzle[-1]]


class VertexProvenance:
    """Conservatively tracks input semantic dependencies to vertex output components."""
    def __init__(self, text):
        self.shader = v.Shader(text)
        self.inputs = signatures(text, 'Input signature:')
        self.outputs = signatures(text, 'Output signature:')
        self.registers = {}
        self.output_registers = {}
        self.issues = []
        self.outputless_stub = not self.outputs

    def read(self, token, lane):
        parsed = parse_operand(token)
        if parsed is None:
            return {UNPROVEN}
        base, swizzle = parsed
        if base.startswith('l('):
            return set()
        if base.startswith('v'):
            register = int(base[1:])
            semantic = next(((sem, mask) for sem, (reg, mask) in self.inputs.items() if reg == register), None)
            if semantic is None:
                return {UNPROVEN}
            key, mask = semantic
            comp = source_component(swizzle, lane)
            return {key} if 'xyzw'[comp] in mask else {UNPROVEN}
        if base.startswith('r'):
            comp = source_component(swizzle, lane)
            return set(self.registers.get((base, comp), {UNPROVEN}))
        dynamic = re.match(r'^(cb\d+|icb)\[([^]]+)\]$', base)
        if dynamic:
            index = dynamic.group(2).strip()
            if index.isdigit():
                return set()
            refs = re.findall(r'v\d+(?:\.[xyzwrgba]+)?|r\d+(?:\.[xyzwrgba]+)?', index)
            residue = re.sub(r'v\d+(?:\.[xyzwrgba]+)?|r\d+(?:\.[xyzwrgba]+)?|0x[0-9a-fA-F]+|\d+(?:\.\d+)?|[+*()\- ]', '', index)
            if residue or not refs:
                return {UNPROVEN}
            return set().union(*(self.read(ref, 0) for ref in refs))
        if base.startswith(('cb', 'icb', 't', 's')):
            return set()
        return {UNPROVEN}

    def run(self):
        if self.shader.stage != 'vs':
            self.issues.append('vertex proof received a non-VS assembly')
            return self
        controls = []
        for op, args in self.shader.instructions():
            if op in ('if_nz', 'if_z', 'switch'):
                condition = set().union(*(self.read(arg, comp) for arg in args for comp in range(4))) if args else {UNPROVEN}
                controls.append({'kind': 'switch' if op == 'switch' else 'if', 'condition': condition,
                                 'arms': [], 'before': dict(self.registers),
                                 'before_outputs': dict(self.output_registers)})
                continue
            if op in ('else', 'case', 'default'):
                if not controls:
                    self.issues.append('unmatched ' + op)
                    continue
                frame = controls[-1]
                frame['arms'].append((dict(self.registers), dict(self.output_registers)))
                self.registers = dict(frame['before'])
                self.output_registers = dict(frame['before_outputs'])
                if op == 'default':
                    frame['default'] = True
                continue
            if op in ('endif', 'endswitch'):
                if not controls:
                    self.issues.append('unmatched ' + op)
                    continue
                frame = controls.pop()
                arms = frame['arms'] + [(dict(self.registers), dict(self.output_registers))]
                if frame['kind'] == 'if' and not frame['arms']:
                    arms.append((frame['before'], frame['before_outputs']))
                if frame['kind'] == 'switch' and not frame.get('default'):
                    arms.append((frame['before'], frame['before_outputs']))
                merged_regs, merged_outputs = {}, {}
                for component_index in (0, 1):
                    target = merged_regs if component_index == 0 else merged_outputs
                    before = frame['before'] if component_index == 0 else frame['before_outputs']
                    maps = [arm[component_index] for arm in arms]
                    for key in set().union(*(set(arm) for arm in maps)):
                        deps = set(frame['condition'])
                        for arm in maps:
                            deps.update(arm.get(key, before.get(key, {UNPROVEN})))
                        target[key] = deps
                self.registers, self.output_registers = merged_regs, merged_outputs
                continue
            if op in ('loop', 'endloop'):
                self.issues.append('loop requires explicit vertex provenance handling')
                continue
            if not args:
                continue
            ndst = 2 if op in ('sincos', 'udiv', 'umul', 'imul', 'uaddc', 'usubb') and len(args) >= 4 else 1
            sources = args[ndst:]
            cross_component = op in ('dp2', 'dp3', 'dp4', 'crs', 'm3x2', 'm3x3', 'm3x4',
                                     'm4x3', 'm4x4', 'msad', 'udot', 'udot4')
            for dst in args[:ndst]:
                parsed = parse_operand(dst)
                if parsed is None:
                    continue
                base, swizzle = parsed
                dest_components = [v.COMP[letter] for letter in swizzle] if swizzle else list(range(4))
                for lane, component in enumerate(dest_components):
                    source_lane = component
                    if op == 'mov' and len(sources) == 1:
                        deps = self.read(sources[0], source_lane)
                    else:
                        if cross_component:
                            deps = set().union(*(self.read(source, source_component)
                                                 for source in sources for source_component in range(4))) if sources else {UNPROVEN}
                        else:
                            deps = set().union(*(self.read(source, source_lane) for source in sources)) if sources else {UNPROVEN}
                    for frame in controls:
                        deps.update(frame['condition'])
                    if base.startswith('r'):
                        self.registers[(base, component)] = deps
                    elif base.startswith('o'):
                        self.output_registers[(base, component)] = deps
        if controls:
            self.issues.append('unterminated vertex control scope')

        self.semantic_components = {}
        for semantic, (register, mask) in self.outputs.items():
            values = {}
            for component, letter in enumerate('xyzw'):
                if letter in mask:
                    values[component] = set(self.output_registers.get(('o' + str(register), component), {UNPROVEN}))
            self.semantic_components[semantic] = values
        return self

    @property
    def has_output_writes(self):
        return bool(self.output_registers)


def expected_vertex_entrypoint(family, pixel_entrypoint):
    if family.startswith('weighted4_'):
        bones = '_4_bones'
    else:
        bones = ''
    dismember = '_dismember' if family.startswith(('weighted2_', 'weighted4_')) and '_blood' in family else ''
    if pixel_entrypoint == 'ps_point_shadow':
        return 'vs40_shadow_pointlight' + bones + dismember
    if pixel_entrypoint == 'ps_directional_shadow':
        return 'vs40_shadow' + bones + dismember
    return 'vs40_main' + bones + dismember


def semantic_component_map(text):
    """Map each pixel input register component to its signature semantic."""
    result = {}
    for semantic, (register, mask) in signatures(text, 'Input signature:').items():
        for component, letter in enumerate('xyzw'):
            if letter in mask:
                result[('v' + str(register), component)] = semantic
    return result
