import sys
import unittest
import collections
import contextlib
import io
import json
import tempfile
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

import verify_slot_families_uv as slots

v = slots.v


class ReviewCounterexamples(unittest.TestCase):
    def evaluator(self, slot=None, dim='2d'):
        header = f'// {slot} texture float4 {dim} t0 1\n' if slot else ''
        ev = slots.SlotEvaluator(v.Shader(header + 'ps_5_0\n'), 'flow')
        ev.uv0 = 'v0'
        return ev

    def coords(self, extra):
        result = [v.p_mul(v.poly_atom(v.atom('in', 'v0', axis, taint=True)),
                          v.poly_atom(v.atom('sb', 'flow_texture_tiling', axis)))
                  for axis in range(2)]
        result[0] = v.p_add(result[0], extra)
        return result

    def speed(self):
        return v.poly_atom(v.atom('sb', 'flow_noise_speed', 0))

    def test_nonlinear_speed_offset_refused(self):
        ev = self.evaluator()
        ev.write('r0', [0], [self.speed()])
        ev.step('sincos', ['r1.x', 'null', 'r0.x'])
        with self.assertRaises(v.Fail):
            ev.check_flow_normal(slots.FLOW_NORMAL, *self.coords(ev.read('r1', 0)))

    def test_absolute_speed_offset_refused(self):
        ev = self.evaluator()
        ev.write('r0', [0], [self.speed()])
        with self.assertRaises(v.Fail):
            ev.check_flow_normal(slots.FLOW_NORMAL, *self.coords(ev.src_comp('|r0.x|', 0)))

    def test_parameter_dependency_survives_branch_merge(self):
        ev = self.evaluator()
        ev.push('if')
        ev.write('r1', [0], [self.speed()])
        ev.frames[-1]['kind'] = 'if_else'
        ev.next_arm()
        ev.write('r1', [0], [v.poly_const(0)])
        ev.pop()
        with self.assertRaises(v.Fail):
            ev.check_flow_normal(slots.FLOW_NORMAL, *self.coords(ev.read('r1', 0)))

    def test_unsupported_target_sample_refused(self):
        ev = self.evaluator(slots.FLOW_TEXTURE)
        with self.assertRaises(v.Fail):
            ev.do_sample('sample_indexable', ['r1.xy', 'v0.xyxx', 't0.xyzw', 's0'])

    def test_uv_dependent_explicit_lod_refused(self):
        ev = self.evaluator(slots.FLOW_MAP)
        with self.assertRaises(v.Fail):
            ev.do_sample('sample_l_indexable', ['r1.xy', 'v0.xyxx', 't0.xyzw', 's0', 'v0.x'])

    def test_compensated_parameter_in_unrelated_sample_refused(self):
        ev = self.evaluator('t_xml_unrelated_detail')
        ev.write('r0', [0, 1], [self.speed(), v.poly_const(0)])
        with self.assertRaises(v.Fail):
            ev.do_sample('sample_indexable', ['r1.xy', 'r0.xyxx', 't0.xyzw', 's0'])

    def test_uv_in_cube_z_coordinate_refused(self):
        ev = self.evaluator('t_xml_environment', 'cube')
        ev.write('r0', [0, 1, 2], [v.poly_const(0), v.poly_const(0),
                                 v.poly_atom(v.atom('in', 'v0', 0, taint=True))])
        with self.assertRaises(v.Fail):
            ev.do_sample('sample_indexable', ['r1.xy', 'r0.xyzx', 't0.xyzw', 's0'])

    def test_legal_flow_coordinates_are_accepted(self):
        ev = self.evaluator()
        tiling = v.poly_atom(v.atom('sb', 'flow_noise_tiling', 0))
        time = v.poly_atom(v.atom('cb', 'camera', 'time_in_sec'))
        phase = v.p_mul(v.p_mul(time, self.speed()), tiling)
        coords = [v.p_add(v.p_mul(v.poly_atom(v.atom('in', 'v0', axis, taint=True)), tiling),
                          v.p_mul(v.poly_const(coefficient), phase))
                  for axis, coefficient in enumerate((0.5, -0.25))]
        ev.check_flow_noise(*coords)
        ev.check_flow_normal(slots.FLOW_NORMAL, *self.coords(v.poly_atom(v.atom('tex', slots.FLOW_MAP, 'rawUV0', 0))))
        self.assertEqual(ev.found[slots.FLOW_NOISE], 1)
        self.assertEqual(ev.found[slots.FLOW_NORMAL], 1)

    def test_zero_register_is_not_undefined(self):
        ev = v.Evaluator(v.Shader('ps_5_0\n'))
        ev.write('r0', [0], [v.poly_const(0)])
        self.assertEqual(ev.read('r0', 0), {})

    def test_undefined_register_is_not_independent(self):
        self.assertTrue(v.p_taint(self.evaluator().read('r42', 0)))

    def test_uv_controlled_discard_is_refused(self):
        text = ('// t_xml_base_colour texture float4 2d t0 1\nps_5_0\n'
                'sample_indexable r0.xy, v0.xyxx, t0.xyzw, s0\n'
                'if_nz v0.x\ndiscard_nz l(1)\nendif\nret\n')
        ev = slots.SlotEvaluator(v.Shader(text), 'flow')
        ev.run()
        self.assertTrue(any('control flow' in reason for reason in ev.issues))

    def test_sincos_null_destination_is_not_a_source(self):
        ev = self.evaluator()
        ev.write('r0', [0], [v.poly_const(1)])
        ev.step('sincos', ['null', 'r1.x', 'r0.x'])
        self.assertFalse(v.p_taint(ev.read('r1', 0)))

    def test_loop_carried_uv_dependency_reaches_earlier_assignments(self):
        ev = self.evaluator()
        for register in ('r1', 'r2', 'r3'):
            ev.write(register, [0], [v.poly_const(0)])
        ins = list(v.Shader('ps_5_0\nloop\nmov r1.x, r2.x\nmov r2.x, r3.x\n'
                           'mov r3.x, v0.x\nendloop\n').instructions())
        ins = [(op, args) for op, args in ins if op != 'ps_5_0']
        ev.enter_loop(ins, 0)
        self.assertTrue(v.p_taint(ev.read('r1', 0)))

    def test_loop_carried_parameter_is_not_erased(self):
        ev = self.evaluator()
        ev.write('r0', [0], [self.speed()])
        ins = list(v.Shader('ps_5_0\nloop\nmov r1.x, r0.x\nendloop\n').instructions())
        ins = [(op, args) for op, args in ins if op != 'ps_5_0']
        ev.enter_loop(ins, 0)
        with self.assertRaises(v.Fail):
            ev.check_flow_normal(slots.FLOW_NORMAL, *self.coords(ev.read('r1', 0)))

    def test_nonconverging_loop_proof_is_refused(self):
        ev = self.evaluator()
        for number in range(41):
            ev.write('r' + str(number), [0], [v.poly_const(0)])
        ev.write('r41', [0], [self.speed()])
        body = '\n'.join(f'mov r{number}.x, r{number + 1}.x' for number in range(41))
        ins = list(v.Shader('ps_5_0\nloop\n' + body + '\nendloop\n').instructions())
        ins = [(op, args) for op, args in ins if op != 'ps_5_0']
        with self.assertRaises(v.Fail):
            ev.enter_loop(ins, 0)


class InputIntegrityTests(unittest.TestCase):
    def test_each_disassembly_run_uses_a_fresh_cache_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, cache = root / 'source', root / 'cache'
            source.mkdir()
            name = 'shaders_probe_xml_hlsl_' + 'e' * 32 + '.hlsl_compiled'
            (source / name).write_bytes(b'fixture')
            stale = cache / 'input-0' / (name + '.0.asm')
            stale.parent.mkdir(parents=True)
            stale.write_text('old user artifact')
            outputs = []

            def disassemble(folder, out):
                destination = Path(out)
                destination.mkdir(parents=True)
                outputs.append(destination)
                (destination / (name + '.0.asm')).write_text('ps_5_0\nret\n')
                (destination / '_list.txt').write_text(name + '\n')
                (destination / '_entrypoints.json').write_text(json.dumps({name: {'entrypoint': 'ps_main', 'stage': 'PS'}}))
                return out, []

            with mock.patch.object(slots, 'RANDOM_FAMILIES', set()), \
                 mock.patch.object(slots, 'FLOW_FAMILIES', set()), \
                 mock.patch.object(slots, 'EXPECTED_FAMILIES', set()), \
                 mock.patch.object(slots, 'disassemble_slots', side_effect=disassemble), \
                 mock.patch.object(slots, 'disassembly_errors', return_value=[]), \
                 contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(slots.main([str(source), '--asm-cache', str(cache)]), 0)
                self.assertEqual(slots.main([str(source), '--asm-cache', str(cache)]), 0)
            self.assertNotEqual(outputs[0], outputs[1])
            self.assertEqual(stale.read_text(), 'old user artifact')

    def test_disassembler_matches_second_search_term(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, cache = root / 'source', root / 'cache'
            source.mkdir()
            name = 'shaders_probe_xml_hlsl_' + 'a' * 32 + '.hlsl_compiled'
            (source / name).write_bytes(b'only-second-needle')
            with mock.patch.object(v.subprocess, 'check_call'), \
                 mock.patch.object(v.subprocess, 'check_output', return_value=b'C:\\fixture\n'):
                v.disassemble(str(source), str(cache), [b'absent-first', b'only-second-needle'])
            self.assertEqual((cache / '_list.txt').read_text().splitlines(), [name])

    def test_cached_disassembly_failure_is_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            name = 'shaders_probe_xml_hlsl_' + 'b' * 32 + '.hlsl_compiled'
            (root / '_list.txt').write_text(name + '\n')
            (root / (name + '.0.asm')).write_text('HRESULT 80004005\n')
            self.assertTrue(any('invalid or failed' in error for error in slots.assembly_corpus_errors(directory)))

    def test_missing_cached_source_is_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            (Path(directory) / '_list.txt').write_text('missing.hlsl_compiled\n')
            self.assertTrue(any('no disassembly' in error for error in slots.assembly_corpus_errors(directory)))

    def test_missing_cache_manifest_is_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            self.assertTrue(any('_list.txt' in error for error in slots.assembly_corpus_errors(directory)))

    def test_stale_disassembly_is_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / '_list.txt').write_text('')
            (root / 'stale.hlsl_compiled.0.asm').write_text('ps_5_0\nret\n')
            self.assertTrue(any('stale disassembly' in error for error in slots.assembly_corpus_errors(directory)))

    def test_corpus_error_suppresses_otherwise_qualifying_allowlist(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, cache = root / 'source', root / 'cache'
            source.mkdir()
            family = 'weighted2_flow_overlay'
            name = 'shaders_' + family + '_xml_hlsl_' + 'c' * 32 + '.hlsl_compiled'
            assembly = source / (name + '.0.asm')
            assembly.write_text('ps_5_0\nret\n')
            (source / '_list.txt').write_text(name + '\n')
            (source / '_entrypoints.json').write_text(json.dumps({name: {'entrypoint': 'ps_main_gbuffer', 'stage': 'PS'}}))
            sample_counts = collections.Counter({slot: 1 for slot in
                (slots.FLOW_MAP, slots.FLOW_GUID, slots.FLOW_NOISE, slots.FLOW_NORMAL, slots.FLOW_NORMAL_2)})
            analyzed = SimpleNamespace(issues=[], found=sample_counts)
            provenance = (('POSITION', 3), {str(assembly): ({'v0': {0, 1}}, None)}, [])
            with mock.patch.object(slots, 'RANDOM_FAMILIES', set()), \
                 mock.patch.object(slots, 'FLOW_FAMILIES', {family}), \
                 mock.patch.object(slots, 'EXPECTED_FAMILIES', {family}), \
                 mock.patch.object(slots, 'vertex_proofs_for_family', return_value=provenance), \
                 mock.patch.object(slots, 'analyze', return_value=analyzed):
                before = io.StringIO()
                with contextlib.redirect_stdout(before):
                    self.assertEqual(slots.main([str(source), '--asm-cache', str(cache)]), 0)
                self.assertIn('"shaders/' + family + '.xml.shader",', before.getvalue())
                (source / 'malformed.asm').write_text('HRESULT 80004005\n')
                after = io.StringIO()
                with contextlib.redirect_stdout(after):
                    self.assertEqual(slots.main([str(source), '--asm-cache', str(cache)]), 1)
                self.assertNotIn('"shaders/' + family + '.xml.shader",', after.getvalue())


class VertexProvenanceTests(unittest.TestCase):
    def vertex(self, body):
        from shader_slot_provenance import VertexProvenance
        return VertexProvenance(
            '// Input signature:\n// TEXCOORD 0 xyzw 0 NONE float xyzw\n'
            '// Output signature:\n// TEXCOORD 1 xyzw 0 NONE float xyzw\n'
            'vs_5_0\ndcl_input v0.xyzw\ndcl_output o0.xyzw\n' + body + '\nret\n').run()

    def test_partial_destination_mask_uses_matching_source_components(self):
        proof = self.vertex('mov r0.xyzw, l(0,0,0,0)\nmov r0.z, v0.x\nmov o0.xz, r0.xyzw')
        self.assertIn(('TEXCOORD', 0), proof.semantic_components[('TEXCOORD', 1)][2])

    def test_dot_product_keeps_cross_component_dependency(self):
        proof = self.vertex('mov r0.xyzw, l(0,0,0,0)\nmov r0.z, v0.x\ndp4 o0.x, r0.xyzw, l(1,1,1,1)')
        self.assertIn(('TEXCOORD', 0), proof.semantic_components[('TEXCOORD', 1)][0])

    def test_dynamic_buffer_index_is_not_assumed_independent(self):
        from shader_slot_provenance import UNPROVEN
        proof = self.vertex('mov r0.x, v0.x\nmov o0.x, cb0[r0.x].x')
        deps = proof.semantic_components[('TEXCOORD', 1)][0]
        self.assertTrue(('TEXCOORD', 0) in deps or UNPROVEN in deps)

    def test_output_dependencies_survive_branch_merge(self):
        proof = self.vertex('if_nz l(1)\nmov o0.x, v0.x\nelse\nmov o0.x, l(0)\nendif')
        self.assertIn(('TEXCOORD', 0), proof.semantic_components[('TEXCOORD', 1)][0])

    def test_shadow_vertex_entrypoint_is_selected_by_pass(self):
        from shader_slot_provenance import expected_vertex_entrypoint
        self.assertEqual(expected_vertex_entrypoint('weighted4_flow_overlay_blood', 'ps_point_shadow'),
                         'vs40_shadow_pointlight_4_bones_dismember')


if __name__ == '__main__':
    unittest.main()
