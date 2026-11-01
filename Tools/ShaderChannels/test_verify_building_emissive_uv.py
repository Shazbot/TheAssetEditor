import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

import verify_building_emissive_uv as building
import verify_emissive_uv as v
from shader_slot_provenance import UNPROVEN, VertexProvenance


def texture_header(name, register):
    return f"// {name} texture float4 2d t{register} 1\n"


def worldspace_parameter_header(type_name="float"):
    return (
        "struct AUTO_PARAMETERS\n"
        "// {\n"
        f"//   {type_name} sp_use_ws_position_as_uv; // Offset: 0\n"
        "// } $Element\n"
        "// auto_parameters texture struct r/o t7\n"
    )


def pixel_input_header(semantic, index, register, mask="xy"):
    return (
        "// Input signature:\n"
        f"// {semantic} {index} {mask} {register} NONE float {mask}\n"
        "// Output signature:\n"
        "// SV_TARGET 0 xyzw 0 TARGET float xyzw\n"
    )


class WorldspaceEvaluatorTests(unittest.TestCase):
    def make_evaluator(self, body, input_uv_components=None):
        text = (
            texture_header("t_xml_base_colour", 0)
            + texture_header("t_xml_emissive_texture", 1)
            + pixel_input_header("TEXCOORD", 0, 0)
            + "ps_5_0\n"
            + body
        )
        shader = v.Shader(text)
        return building.WorldspaceEvaluator(
            shader, input_uv_components=input_uv_components,
            provenance_complete=True, specialize_worldspace=True,
        )

    def test_emissive_texture_independent_of_mesh_uv_passes(self):
        evaluator = self.make_evaluator(
            "sample_indexable r0.xy, v0.xyxx, t0.xyzw, s0\n"
            "sample_indexable r1.xy, l(0,0,0,0), t1.xyzw, s1\n"
            "ret\n"
        )
        evaluator.run()
        self.assertEqual(evaluator.issues, [])
        self.assertEqual(evaluator.found["worldspace_emissive_texture"], 1)

    def test_emissive_texture_rejects_a_hidden_mesh_uv_alias(self):
        evaluator = self.make_evaluator(
            "sample_indexable r0.xy, v0.xyxx, t0.xyzw, s0\n"
            "sample_indexable r1.xy, v1.xyxx, t1.xyzw, s1\n"
            "ret\n",
            input_uv_components={"v1": {0, 1}},
        )
        evaluator.run()
        self.assertTrue(any("depends on mesh UV0" in reason for reason in evaluator.issues))

    def test_emissive_mask_requires_raw_mesh_uv(self):
        text = (
            texture_header("t_xml_base_colour", 0)
            + texture_header("t_xml_emissive", 1)
            + pixel_input_header("TEXCOORD", 0, 0)
            + "// TEXCOORD 1 xy 1 NONE float xy\n"
            + "ps_5_0\n"
            + "sample_indexable r0.xy, v0.xyxx, t0.xyzw, s0\n"
            + "sample_indexable r1.xy, v1.xyxx, t1.xyzw, s1\n"
            + "ret\n"
        )
        evaluator = building.WorldspaceEvaluator(v.Shader(text), provenance_complete=True)
        evaluator.run()
        self.assertTrue(any("different pixel interpolators" in reason or
                            "not sampled at raw mesh UV0" in reason
                            for reason in evaluator.issues))

    def test_emissive_mask_rejects_non_2d_binding(self):
        text = (
            texture_header("t_xml_emissive", 0).replace("2d", "3d")
            + pixel_input_header("TEXCOORD", 0, 0)
            + "ps_5_0\n"
            + "sample_indexable r0.xy, v0.xyxx, t0.xyzw, s0\n"
            + "ret\n"
        )
        evaluator = building.WorldspaceEvaluator(v.Shader(text), provenance_complete=True)
        evaluator.run()
        self.assertTrue(any("requires a 2D resource" in reason for reason in evaluator.issues))

    def test_mesh_uv_cannot_reach_a_pixel_output(self):
        evaluator = self.make_evaluator(
            "sample_indexable r0.xy, v0.xyxx, t0.xyzw, s0\n"
            "mov o0.x, v0.x\n"
            "ret\n"
        )
        evaluator.run()
        self.assertTrue(any("reaches output" in reason for reason in evaluator.issues))

    def test_worldspace_parameter_specializes_to_scalar_one(self):
        shader = v.Shader(worldspace_parameter_header() + "ps_5_0\nret\n")
        evaluator = building.WorldspaceEvaluator(shader, specialize_worldspace=True)
        evaluator.do_ld_structured(["r0.x", "l(0)", "l(0)", "t7.xxxx"])
        self.assertEqual(evaluator.read("r0", 0), v.poly_const(1))

    def test_recognized_parameter_load_rejects_mesh_uv_element_index(self):
        shader = v.Shader(worldspace_parameter_header() + "ps_5_0\nret\n")
        evaluator = building.WorldspaceEvaluator(
            shader, input_uv_components={"v0": {0}}, specialize_worldspace=True,
        )
        with self.assertRaises(v.Fail):
            evaluator.do_ld_structured(["r0.x", "v0.x", "l(0)", "t7.xxxx"])

    def test_worldspace_parameter_must_be_one_scalar_float(self):
        valid = v.Shader(worldspace_parameter_header() + "ps_5_0\nret\n")
        invalid = v.Shader(worldspace_parameter_header("float2") + "ps_5_0\nret\n")
        self.assertEqual(building.validate_worldspace_parameter(valid), (True, None))
        self.assertFalse(building.validate_worldspace_parameter(invalid)[0])


class ProvenanceTests(unittest.TestCase):
    def test_mesh_uv_dependency_survives_a_world_position_pixel_alias(self):
        vertex_text = (
            "// Input signature:\n"
            "// TEXCOORD 0 xyzw 1 NONE float xyzw\n"
            "// Output signature:\n"
            "// TEXCOORD 0 xy 0 NONE float xy\n"
            "// TEXCOORD 1 xy 1 NONE float xy\n"
            "vs_5_0\n"
            "mov o0.xy, v1.xy\n"
            "mov o1.xy, v1.xy\n"
            "ret\n"
        )
        pixel_text = (
            "// Input signature:\n"
            "// TEXCOORD 1 xy 0 NONE float xy\n"
            "ps_5_0\n"
            "sample_indexable r0.xy, v0.xyxx, t0.xyzw, s0\n"
        )
        proof = VertexProvenance(vertex_text).run()
        components, errors = building.linked_input_uv_components(
            v.Shader(pixel_text), [(SimpleNamespace(path="vs.asm"), proof)],
            ("TEXCOORD", 0),
        )
        self.assertEqual(errors, [])
        self.assertEqual(components, {"v0": {0, 1}})

    def test_unproven_vertex_alias_is_rejected(self):
        proof = SimpleNamespace(
            semantic_components={
                ("TEXCOORD", 1): {0: {UNPROVEN}, 1: {("TEXCOORD", 0)}}
            }
        )
        shader = v.Shader(
            "// Input signature:\n// TEXCOORD 1 xy 0 NONE float xy\n"
            "ps_5_0\nmov r0.xy, v0.xy\n"
        )
        _, errors = building.linked_input_uv_components(
            shader, [(SimpleNamespace(path="vs.asm"), proof)], ("TEXCOORD", 0),
        )
        self.assertTrue(any("does not prove" in reason for reason in errors))

    def test_geometry_uv_alias_must_be_identity_passthrough(self):
        identity = (
            "// Input signature:\n// TEXCOORD 0 xy 1 NONE float xy\n"
            "// Output signature:\n// TEXCOORD 0 xy 2 NONE float xy\n"
            "gs_5_0\n"
            "mov o2.xy, v[0][1].xyxx\n"
            "ret\n"
        )
        transformed = identity.replace("v[0][1].xyxx", "v[0][1].yxxy")
        required = {("TEXCOORD", 0): {0, 1}}
        self.assertEqual(building.geometry_passthrough(identity, required), [])
        self.assertTrue(building.geometry_passthrough(transformed, required))

    def test_geometry_uv_dependent_emission_control_is_rejected(self):
        text = (
            "// Input signature:\n// TEXCOORD 0 xy 1 NONE float xy\n"
            "// Output signature:\n// TEXCOORD 0 xy 2 NONE float xy\n"
            "gs_5_0\n"
            "mov r0.x, v[0][1].x\n"
            "if_nz r0.x\n"
            "mov o2.xy, v[0][1].xyxx\n"
            "emit_stream m0\n"
            "endif\nret\n"
        )
        self.assertTrue(any("branch or emission control" in reason
                            for reason in building.geometry_control_errors(text)))


class CorpusTests(unittest.TestCase):
    def test_target_filter_includes_alpha_and_excludes_prop_siblings(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for family, digest in (
                ("rigid_building_emissive", "a" * 32),
                ("rigid_building_emissive_alpha", "b" * 32),
                ("rigid_building_emissive_prop", "c" * 32),
            ):
                (root / f"shaders_{family}_xml_hlsl_{digest}.hlsl_compiled").write_bytes(b"fixture")
            targets = building.target_compiled_files(directory)
            self.assertEqual(set(targets), set(building.FAMILIES))
            self.assertEqual(len(targets["rigid_building_emissive"]), 1)
            self.assertEqual(len(targets["rigid_building_emissive_alpha"]), 1)

    def test_absent_alpha_sibling_blocks_the_proof(self):
        errors = building.target_family_errors({
            "rigid_building_emissive": ["one-compiled-variant"],
        })
        self.assertTrue(any("rigid_building_emissive_alpha" in error for error in errors))

    def test_missing_last_dxbc_blob_is_detected(self):
        error = building.blob_coverage_error("shader.hlsl_compiled", 2, {0})
        self.assertIn("expected indices [0, 1]", error)


if __name__ == "__main__":
    unittest.main()
