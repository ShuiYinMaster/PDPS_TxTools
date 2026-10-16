import itertools
import json
import math
from pathlib import Path
import struct
import unittest

from cfv2 import parse
from cfv2_metadata import named_node
from scene_records import parse_scene

WORKSPACE = Path(__file__).resolve().parents[3]
RESEARCH = WORKSPACE / "analysis_cgr/scene_20261016_r5"


def container_scene(path):
    c = parse(path.read_bytes())
    storage = named_node(c, c.nodes[1].id, "SceneGraph")
    return c.payload(named_node(c, storage.id, "MainDataStream"))


class SceneRecordTests(unittest.TestCase):
    def native(self, name):
        return (RESEARCH / "native" / (name + ".scene")).read_bytes()

    def test_all_controlled_native_scenes_rebuild_exactly(self):
        report = json.loads((RESEARCH / "native/native_scene_fixtures.json").read_text(encoding="utf-8"))
        self.assertEqual(report["errors"], [])
        self.assertEqual(len(report["fixtures"]), 12)
        self.assertEqual(report["document_count_before"], report["document_count_after"])
        for fixture in report["fixtures"]:
            with self.subTest(label=fixture["label"]):
                data = self.native(fixture["label"])
                self.assertEqual(parse_scene(data).encode(), data)

    def test_matrix_flag_and_native_placement_have_independent_coordinates(self):
        baseline = self.native("assembly_identity")
        data = self.native("assembly_translated")
        self.assertEqual(len(data) - len(baseline), 64)
        a, b = parse_scene(baseline), parse_scene(data)
        self.assertFalse(any(n.matrix for n in a.nodes))
        placed = [n for n in b.nodes if n.matrix]
        self.assertEqual(len(placed), 1)
        self.assertEqual(placed[0].matrix, (1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 101, 203, 307, 1))
        self.assertEqual(placed[0].layer_code, 65535)
        self.assertEqual(placed[0].node_options, 0)
        # Incremental 90-degree rotation also rotates the original translation.
        rotated = [n for n in parse_scene(self.native("assembly_rotated")).nodes if n.matrix][0]
        self.assertEqual(rotated.matrix, (0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, -203, 101, 307, 1))

    def test_geometry_change_updates_local_sphere_and_preserves_other_header_slots(self):
        a = parse_scene(self.native("part_baseline"))
        b = parse_scene(self.native("part_height60"))
        self.assertEqual(self.native("part_baseline"), self.native("part_repeat"))
        for scene, height in ((a, 50), (b, 60)):
            leaf = next(n for n in scene.nodes if n.tag == 0x95)
            self.assertEqual(leaf.header.center, (26, 42, height / 2))
            for x, y, z in itertools.product((11, 41), (22, 62), (0, height)):
                self.assertLessEqual(math.dist((x, y, z), leaf.header.center), leaf.header.radius)
        self.assertGreater(b.nodes[-1].header.radius, a.nodes[-1].header.radius)

    def test_repeated_instance_uses_shared_geometry_definition(self):
        s = parse_scene(self.native("assembly_two_instances"))
        refs = [link for n in s.nodes for link in n.children if link.ref_id is not None]
        self.assertEqual(len(refs), 2)
        self.assertEqual([r.definition for r in refs], [True, False])
        self.assertEqual([r.ref_id for r in refs], [0, 0])
        self.assertIs(refs[0].target, refs[1].target)
        self.assertEqual(sum(n.tag == 0xA3 for n in s.nodes), 1)
        self.assertEqual(sum(bool(n.matrix) for n in s.nodes), 2)

    def test_native_ffm170_whole_tree_matrix_and_reference_roundtrip(self):
        data = container_scene(WORKSPACE / "FFM170-X-0417-6-CLOSE-A3-2024.11.14.cgr")
        s = parse_scene(data)
        self.assertEqual(len(s.nodes), 776)
        self.assertEqual(sum(bool(n.matrix) for n in s.nodes), 147)
        self.assertEqual(sum(not link.definition for n in s.nodes for link in n.children), 58)
        self.assertEqual(s.encode(), data)

    def test_generated_outputs_rebuild_and_pick_offsets_point_to_records(self):
        paths = sorted((RESEARCH / "verification_final").glob("*_after.cgr"))
        self.assertEqual(len(paths), 38)
        for path in paths:
            with self.subTest(file=path.name):
                data = container_scene(path)
                self.assertEqual(parse_scene(data).encode(), data)
        path = RESEARCH / "verification_final/color_box_compact_after.cgr"
        c = parse(path.read_bytes())
        s = parse_scene(container_scene(path))
        storage = named_node(c, c.nodes[1].id, "SceneGraph")
        picks = c.payload(named_node(c, storage.id, "SurfacicReps"))
        offsets = [n.pick_offset for n in s.nodes if n.tag == 0x95]
        self.assertEqual(offsets, [0, 61, 122, 183, 244, 305])
        self.assertTrue(any(n.pick_extended for n in s.nodes if n.tag == 0x95))
        for offset in offsets:
            self.assertEqual(picks[offset], 255)
            self.assertEqual(struct.unpack_from("<I", picks, offset + 1)[0], 61)

    def test_length_biases_and_pick_offset_are_not_confused(self):
        data = self.native("part_baseline")
        s = parse_scene(data)
        self.assertEqual(struct.unpack_from("<I", data, 2)[0], s.root.end - 2)
        leaf = next(n for n in s.nodes if n.tag == 0x95)
        declared = struct.unpack_from("<I", data, leaf.offset + 2)[0]
        pick_width = 5 if leaf.pick_extended else 1
        self.assertEqual(declared, leaf.end - pick_width - leaf.offset - 1)
        for offset in (2, leaf.offset + 2):
            original = struct.unpack_from("<I", data, offset)[0]
            for wrong in (0, original - 1, original + 1, 0xFFFFFFFF):
                changed = bytearray(data)
                struct.pack_into("<I", changed, offset, wrong)
                with self.subTest(offset=offset, value=wrong), self.assertRaises(ValueError):
                    parse_scene(changed)

    def test_missing_matrix_undefined_reference_and_trailing_bytes_are_rejected(self):
        data = bytearray(self.native("assembly_translated"))
        placed = next(n for n in parse_scene(data).nodes if n.matrix)
        # 62 tag + 35 common bytes + layer:u16 + options:u16 + matrix flag:u8.
        data[placed.offset + 40] = 0
        with self.assertRaises(ValueError):
            parse_scene(data)
        data = bytearray(self.native("assembly_two_instances"))
        ref = next(link for n in parse_scene(data).nodes for link in n.children if not link.definition)
        data[ref.offset] = 2
        data[ref.offset + 1] = 250
        with self.assertRaises(ValueError):
            parse_scene(data)
        with self.assertRaises(ValueError):
            parse_scene(self.native("part_baseline") + b"\x01")

    def test_geometry_signatures_do_not_create_tree_nodes(self):
        data = bytearray(self.native("part_baseline"))
        original = parse_scene(data)
        leaf = next(n for n in original.nodes if n.tag == 0x95)
        data[leaf.offset + 41:leaf.offset + 49] = b"\x62\xa3\xff\x95\xff\0\0\0"
        changed = parse_scene(data)
        self.assertEqual(len(changed.nodes), len(original.nodes))
        self.assertEqual(changed.encode(), data)


if __name__ == "__main__":
    unittest.main()
