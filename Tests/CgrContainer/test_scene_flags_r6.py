"""Independent native field controls and bidirectional COM volume evidence."""
import json
from pathlib import Path
import struct
import unittest

from cfv2 import parse
from cfv2_metadata import named_node
from probe_scene_representation import replace_scene, stream
from scene_records import parse_scene

ROOT = Path(__file__).resolve().parents[3] / "analysis_cgr/scene_20261016_r6"


class SceneFlagsR6Tests(unittest.TestCase):
    def native(self, label):
        c = parse((ROOT / "native_flags_full" / (label + ".cgr")).read_bytes())
        return parse_scene(c.payload(stream(c)))

    def test_all_native_states_roundtrip_with_fixed_attributes(self):
        report = json.loads((ROOT / "native_flags_full/native_flags.json").read_text())
        self.assertEqual(report["errors"], [])
        self.assertEqual(report["document_count_before"], report["document_count_after"])
        paths = list((ROOT / "native_flags_full").glob("*.cgr"))
        self.assertEqual(len(paths), 14)
        for path in paths:
            c = parse(path.read_bytes())
            data = c.payload(stream(c))
            self.assertEqual(parse_scene(data).encode(), data)

    def test_layer_is_u16_not_compact_extended_integer(self):
        baseline = self.native("baseline")
        self.assertEqual(baseline.nodes[1].layer_code, 1023)
        for layer in (0, 1, 37, 255, 256, 999):
            s = self.native(f"body_layer{layer}")
            self.assertEqual(s.nodes[1].layer_code, layer)
            self.assertEqual(s.nodes[1].node_options, 1)
            self.assertEqual([n.layer_code for n in s.nodes[2:]], [1023, 1023])
            self.assertEqual(s.nodes[1].header.flags, baseline.nodes[1].header.flags)
            at = s.nodes[1].offset + 36
            self.assertEqual(s.encode()[at:at + 5], struct.pack("<HHB", layer, 1, 0))
        self.assertEqual(self.native("body_layer_none").nodes[1].layer_code, 1023)

    def test_no_pick_is_independent_bit_and_follows_selected_node(self):
        for label, index in (("body", 1), ("pad", 2)):
            disabled, restored = self.native(label + "_no_pick"), self.native(label + "_pick")
            for i, (a, b) in enumerate(zip(disabled.nodes, restored.nodes)):
                self.assertEqual(a.header.no_pick, i == index)
                self.assertFalse(b.header.no_pick)
                self.assertEqual(a.header.flags[1] ^ b.header.flags[1], 1 if i == index else 0)
            self.assertTrue(disabled.nodes[-1].header.volume_contribution_bit)

    def test_hide_omits_subtree_in_this_native_export(self):
        hidden, shown = self.native("body_hidden"), self.native("body_shown")
        self.assertEqual(len(hidden.nodes), 1)
        self.assertEqual(len(shown.nodes), 4)

    def test_volume_bit_bidirectional_factorial(self):
        report = json.loads((ROOT / "leaf_com/com_results.json").read_text())
        self.assertEqual(report["errors"], [])
        self.assertEqual(len(report["cases"]), 32)
        self.assertEqual(report["document_count_before"], report["document_count_after"])
        for case in report["cases"]:
            c = parse(Path(case["path"]).read_bytes())
            s = parse_scene(c.payload(stream(c)))
            leaf = next(n for n in s.nodes if n.tag == 0x95)
            expected = 1920000 if case["model"] == "box" else 501220.57596742234
            self.assertAlmostEqual(case["volume_mm3"], expected if leaf.header.volume_contribution_bit else 0, places=7)
            self.assertTrue(case["other_streams_unchanged"])

    def test_container_replacement_changes_only_scene_stream(self):
        c = parse((ROOT / "native_flags_full/body_layer37.cgr").read_bytes())
        scene = c.payload(stream(c))
        candidate = parse(replace_scene(c, scene))
        self.assertEqual([c.payload(n) for n in c.nodes], [candidate.payload(n) for n in candidate.nodes])
        self.assertEqual(candidate.rebuilt(), candidate.data)


if __name__ == "__main__":
    unittest.main()
