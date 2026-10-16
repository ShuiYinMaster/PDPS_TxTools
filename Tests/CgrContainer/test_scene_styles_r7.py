"""Native COM evidence for variable common headers, packed styles and bounds."""
from dataclasses import replace
import json
import math
from pathlib import Path
import unittest

from scene_bounds import merge_spheres
from scene_records import CommonHeader, Cursor, parse_scene

ROOT = Path(__file__).resolve().parents[3] / "analysis_cgr/scene_20261016_r7"


class SceneStylesR7Tests(unittest.TestCase):
    def scene(self, name, directory="native"):
        return parse_scene((ROOT / directory / (name + ".scene")).read_bytes())

    def header(self, name, directory="native"):
        return self.scene(name, directory).nodes[2].header

    def test_all_native_states_roundtrip_and_com_documents_restored(self):
        count = 0
        for directory in ("native", "style_ranges", "width_upper"):
            report = json.loads((ROOT / directory / "native_inheritance.json").read_text())
            self.assertEqual(report["errors"], [])
            self.assertEqual(report["document_count_before"], report["document_count_after"])
            for path in (ROOT / directory).glob("*.scene"):
                data = path.read_bytes()
                self.assertEqual(parse_scene(data).encode(), data)
                count += 1
        self.assertEqual(count, 41)

    def test_inheritance_masks_change_independently_of_values(self):
        for name, mask in (("color", 0x404), ("opacity", 0x1000), ("width", 0x10), ("line_type", 0x40)):
            a = self.header(f"instance_{name}_inherit0")
            b = self.header(f"instance_{name}_inherit1")
            self.assertEqual(a.inheritance_mask, 0)
            self.assertEqual(b.inheritance_mask, mask)
            self.assertEqual(a.flags[1:], b.flags[1:])
            self.assertEqual(a.encoding, b.encoding)
            self.assertEqual(a.attribute_word_be, b.attribute_word_be)
        self.assertEqual(self.scene("root_color_inherit1").root.header.inheritance_mask, 0x404)

    def test_width_crosses_byte_boundary_and_upper_api_values_can_fall_back(self):
        for width in (1, 2, 15, 16, 31):
            h = self.header(f"instance_width{width}", "style_ranges")
            self.assertEqual(h.line_width_code, width)
            self.assertEqual(h.line_type_code, 1)
            self.assertEqual(h.graphics_style & 15, 4)
        for width in (32, 47, 55):
            h = self.header(f"instance_width{width}", "width_upper")
            self.assertEqual(h.line_width_code, width)
            self.assertEqual(h.line_type_code, 1)
        self.assertEqual(self.header("instance_width62", "width_upper").line_width_code, 1)
        self.assertEqual(self.header("instance_width63", "style_ranges").line_width_code, 1)

    def test_line_type_and_rgb_slots_follow_requested_values(self):
        for code in (1, 2, 63):
            h = self.header(f"instance_line{code}", "style_ranges")
            self.assertEqual(h.line_type_code, code)
            self.assertTrue(h.line_type_inheritance)
        for label, rgb in (("red", (231, 17, 3)), ("green", (5, 211, 31)), ("blue", (13, 7, 197))):
            self.assertEqual(self.header("instance_" + label, "style_ranges").explicit_rgb, rgb)
        self.assertIsNone(self.header("instance_width16", "style_ranges").explicit_rgb)

    def test_opacity_byte_and_translucency_flag_are_separate_from_inheritance(self):
        for alpha in (0, 1, 64, 127, 200, 254, 255):
            h = self.header(f"instance_alpha{alpha}", "style_ranges")
            self.assertEqual(h.opacity, alpha)
            self.assertEqual(h.translucent, alpha < 255)
            self.assertTrue(h.opacity_inheritance)
        self.assertFalse(self.header("instance_opacity_inherit0").opacity_inheritance)

    def test_combined_properties_preserve_both_high_width_bits_and_inheritance_bits(self):
        h = self.header("instance_combined", "style_ranges")
        self.assertEqual(h.inheritance_mask, 0x1454)
        self.assertEqual(h.line_width_code, 16)
        self.assertEqual(h.line_type_code, 3)
        self.assertEqual(h.explicit_rgb, (41, 83, 167))
        self.assertEqual(h.opacity, 73)
        self.assertEqual(h.color_inheritance_bits, 0x404)
        self.assertTrue(h.width_inheritance and h.line_type_inheritance and h.opacity_inheritance)

    def test_variable_header_preserves_matrix_and_shared_reference_boundaries(self):
        plain = self.scene("assembly_baseline")
        styled = self.scene("instance_color_inherit1")
        self.assertEqual(len(styled.encode()) - len(plain.encode()), 4)
        self.assertEqual(plain.nodes[2].header.encoded_size, 35)
        self.assertEqual(styled.nodes[2].header.encoded_size, 39)
        self.assertEqual(sum(bool(n.matrix) for n in styled.nodes), 1)
        self.assertEqual(sum(not link.definition for n in styled.nodes for link in n.children), 1)
        self.assertEqual(styled.nodes[-2].matrix[-4:], (80, 0, 0, 1))
        # Noncanonical extended zero mask is preserved, not normalized away.
        h = replace(plain.root.header, inheritance_extended=True)
        data = h.encode()
        c = Cursor(data)
        self.assertEqual(CommonHeader.read(c, len(data)).encode(), data)
        with self.assertRaises(ValueError):
            CommonHeader.read(Cursor(b"\xff\0\0"), 3)

    def test_root_bound_hypothesis_matches_five_independent_native_boxes(self):
        for name in ("origin", "positive", "negative", "centered_xy", "small"):
            s = self.scene("box_" + name)
            leaf = next(n for n in s.nodes if n.tag == 0x95)
            center, radius = merge_spheres((0, 0, 0), 0, leaf.header.center, leaf.header.radius)
            self.assertLess(math.dist(center, s.root.header.center), 3e-5)
            self.assertLess(abs(radius - s.root.header.radius), 2e-5)
            self.assertLessEqual(math.dist((0, 0, 0), center), radius + 1e-10)
        self.assertEqual(merge_spheres((1, 2, 3), 5, (1, 2, 3), 1), ((1, 2, 3), 5))

    def test_cross_runtime_common_headers_match_native_bytes(self):
        report = json.loads((ROOT / "field_verification_final/scene_styles_verification.json").read_text())
        self.assertEqual(len(report["cross_runtime"]), 41)
        self.assertTrue(all(case["byte_exact"] for case in report["cross_runtime"]))
        self.assertEqual({case["header_bytes"] for case in report["cross_runtime"]}, {35, 39})

    def test_csharp_reconstructed_style_cgr_com_acceptance(self):
        report = json.loads((ROOT / "com_acceptance/com_results.json").read_text())
        self.assertEqual(report["errors"], [])
        self.assertEqual(report["document_count_before"], report["document_count_after"])
        self.assertEqual(len(report["cases"]), 4)
        for case in report["cases"]:
            self.assertTrue(case["scene_byte_exact"])
            self.assertEqual(case["volume_mm3"], 120000)


if __name__ == "__main__":
    unittest.main()
