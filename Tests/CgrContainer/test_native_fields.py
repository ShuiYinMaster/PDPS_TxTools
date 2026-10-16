"""Regressions grounded in native COM exports, rather than generated fixtures."""
from dataclasses import replace
import json
from pathlib import Path
import struct
import unittest

from cfv2 import parse, refragment
from cfv2_metadata import ApplicativeContainerList, HeaderRecord, StorageIdentity, applicative_records, audit, records

WORKSPACE = Path(__file__).resolve().parents[3]
RESEARCH = WORKSPACE / "analysis_cgr/container_20261015_r3"


class NativeFieldTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.folder = RESEARCH / "native_fields_isolated"
        cls.report = json.loads((cls.folder / "native_field_fixtures.json").read_text(encoding="utf-8"))
        cls.fixtures = {f["label"]: f for f in cls.report["fixtures"]}

    def native(self, label):
        return parse((self.folder / (label + ".cgr")).read_bytes())

    def test_each_native_header_matches_com_accuracy_readback(self):
        self.assertEqual(len(self.fixtures), 34)
        self.assertEqual(self.report["errors"], [])
        self.assertTrue(self.report["settings_restored_exactly"])
        self.assertEqual(self.report["settings_before"], self.report["settings_after"])
        for label, fixture in self.fixtures.items():
            with self.subTest(label=label):
                c = self.native(label)
                h = records(c)[2]
                s = fixture["settings_readback"]
                self.assertEqual(h.viz3d_accuracy_mode, int(s["Viz3DAccuracyMode"]))
                self.assertEqual(h.viz2d_accuracy_mode, int(s["Viz2DAccuracyMode"]))
                self.assertEqual(h.viz3d_accuracy, s["Viz3DFixedAccuracy" if h.viz3d_accuracy_mode else "Viz3DProportionnalAccuracy"])
                self.assertEqual(h.viz2d_accuracy, s["Viz2DFixedAccuracy" if h.viz2d_accuracy_mode else "Viz2DProportionnalAccuracy"])
                self.assertEqual(h.viz3d_curve_accuracy, s["Viz3DCurveAccuracy"])
                self.assertTrue(audit(c)["three_way_timestamp_match"])
                self.assertEqual(c.rebuilt(), c.data)

    def test_selected_accuracy_and_mode_change_only_the_expected_header_fields(self):
        baseline = records(self.native("solid_baseline"))[2]
        for setting in ("Viz3DFixedAccuracy", "Viz3DProportionnalAccuracy", "Viz3DAccuracyMode", "Viz3DCurveAccuracy",
                        "Viz2DFixedAccuracy", "Viz2DProportionnalAccuracy", "Viz2DAccuracyMode"):
            with self.subTest(setting=setting):
                h = records(self.native("setting_" + setting))[2]
                # Strip all five decoded accuracy fields and the export time.
                normalized = replace(h, unix_seconds=baseline.unix_seconds,
                                     viz3d_accuracy_mode=baseline.viz3d_accuracy_mode,
                                     viz3d_accuracy=baseline.viz3d_accuracy,
                                     viz3d_curve_accuracy=baseline.viz3d_curve_accuracy,
                                     viz2d_accuracy_mode=baseline.viz2d_accuracy_mode,
                                     viz2d_accuracy=baseline.viz2d_accuracy)
                self.assertEqual(normalized, baseline)
                self.assertNotEqual(h, replace(baseline, unix_seconds=h.unix_seconds))
        # Offset aliases are retained, but the dataclass now has semantic names.
        self.assertEqual(HeaderRecord.parse(baseline.encode()).viz3d_accuracy, baseline.scalar11)

    def test_lineic_mode_is_bit_0x20_and_changes_geometry_payload(self):
        baseline = self.native("solid_baseline")
        lineic = self.native("setting_LineicCgrMode")
        self.assertEqual(records(baseline)[4].capability ^ records(lineic)[4].capability, 0x20)
        self.assertFalse(records(baseline)[4].lineic_cgr_mode)
        self.assertTrue(records(lineic)[4].lineic_cgr_mode)
        self.assertNotEqual(baseline.payload(baseline.nodes[11]), lineic.payload(lineic.nodes[11]))
        self.assertNotEqual(baseline.payload(baseline.nodes[12]), lineic.payload(lineic.nodes[12]))

    def test_appearance_edits_do_not_explain_header_appearance_slot(self):
        for group, labels in (("point", ("point_color", "point_opacity", "point_symbol", "point_width")),
                              ("solid", ("solid_body_color", "solid_root_color", "solid_root_opacity",
                                         "solid_root_width", "solid_root_line_type"))):
            baseline = records(self.native(group + "_baseline"))[2]
            for label in labels:
                with self.subTest(label=label):
                    h = records(self.native(label))[2]
                    self.assertEqual(replace(h, unix_seconds=baseline.unix_seconds), baseline)
        baseline = self.native("solid_baseline")
        colored = self.native("solid_body_color")
        self.assertNotEqual(baseline.payload(baseline.nodes[11]), colored.payload(colored.nodes[11]))

    def test_observed_counter_deltas_repeat_in_two_independent_point_runs(self):
        labels = ("point_baseline", "point_repeat", "point_noop_update", "point_rename", "point_color",
                  "point_opacity", "point_symbol", "point_width", "point_x21", "point_x11_restored")
        for name in ("native_fields", "native_fields_isolated"):
            counters = [StorageIdentity.parse(parse((RESEARCH / name / (label + ".cgr")).read_bytes()).nodes[1].name).word3
                        for label in labels]
            self.assertEqual([b - a for a, b in zip(counters, counters[1:])], [1, 1, 1, 3, 3, 3, 3, 6, 6])


class NativeAdhesionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.folder = RESEARCH / "native_options_final"

    def native(self, label):
        return parse((self.folder / (label + ".cgr")).read_bytes())

    def test_all_native_option_directories_roundtrip_including_absent_indexes(self):
        paths = sorted(self.folder.glob("*.cgr"))
        self.assertEqual(len(paths), 14)
        report = json.loads((self.folder / "native_option_fixtures.json").read_text(encoding="utf-8"))
        self.assertEqual(report["errors"], [])
        self.assertTrue(report["settings_restored_exactly"])
        for path in paths:
            with self.subTest(file=path.name):
                c = parse(path.read_bytes())
                self.assertEqual(c.rebuilt(), c.data)
                after = parse(refragment(c.data, {}))
                for a, b in zip(c.nodes, after.nodes):
                    self.assertEqual((a.kind, a.parent, a.name), (b.kind, b.parent, b.name))
                    self.assertEqual(c.payload(a), after.payload(b))

    def test_adhesion_nodes_are_independent_of_describe_lineic_flag(self):
        for label, count, name in (("adhesion_V5_SPA", 21, "V5_SPA_Inertia"),
                                   ("adhesion_Voxels", 25, "Level1"),
                                   ("adhesion_V4V5_FDT", 18, "V4V5_FDT"),
                                   ("adhesion_V4_Model_LnF", 18, "V4_Model_LnF"),
                                   ("adhesion_V4_Model_CommentPage", 18, "V4_Model_CommentPage")):
            with self.subTest(label=label):
                c = self.native(label)
                self.assertEqual(len(c.nodes), count)
                self.assertIn(name, [n.name for n in c.nodes])
                self.assertEqual(records(c)[4].capability, 0x16)
        for label in ("visualization_LineicCgrMode", "lineic_without_lod"):
            self.assertEqual(records(self.native(label))[4].capability, 0x36)

    def test_empty_storage_has_directory_children_without_a_payload_index(self):
        c = self.native("adhesion_V4V5_FDT")
        storage = c.nodes[13]
        self.assertEqual((storage.name, storage.size, storage.extents), ("Applicative Containers", 0, []))
        self.assertIsNone(c.storage_index(storage))
        self.assertEqual([n.name for n in c.nodes if n.parent == storage.id],
                         ["Applicative Containers", "MainDataStream", "V4V5_FDT"])
        with self.assertRaises(ValueError):
            refragment(c.data, {storage.id: 1})
        # Empty storage is not a reason to ignore a contradictory allocation.
        corrupted = bytearray(c.data)
        struct.pack_into(">I", corrupted, c.directory_offset + storage.directory_position + 14, 1)
        with self.assertRaises(ValueError):
            parse(corrupted)

    def test_applicative_registry_count_names_and_empty_adhesions(self):
        for label, names in (("adhesion_V5_SPA", ("V5_SPA",)), ("adhesion_Voxels", ("Voxels",)),
                             ("adhesion_V4V5_FDT", ()), ("adhesion_spa_and_voxels", ("V5_SPA", "Voxels")),
                             ("adhesion_all", ("V5_SPA", "Voxels"))):
            with self.subTest(label=label):
                c = self.native(label)
                node, record = applicative_records(c)
                self.assertEqual(record.names, names)
                self.assertEqual(record.encode(), c.payload(node))
        # Registry membership differs from the complete directory child set.
        c = self.native("adhesion_all")
        self.assertEqual(len([n for n in c.nodes if n.parent == 14 and n.kind == 1]), 5)
        self.assertEqual(len(applicative_records(c)[1].names), 2)
        self.assertIsNone(applicative_records(self.native("baseline")))

    def test_registry_rejects_truncation_count_mismatch_and_trailing_data(self):
        c = self.native("adhesion_spa_and_voxels")
        data = c.payload(applicative_records(c)[0])
        for corrupted in (data[:-1], data + b"\0", data[:1] + struct.pack("<I", 3) + data[5:],
                          data[:5] + b"\x80" + data[6:]):
            with self.subTest(data=corrupted), self.assertRaises(ValueError):
                ApplicativeContainerList.parse(corrupted)


if __name__ == "__main__":
    unittest.main()
