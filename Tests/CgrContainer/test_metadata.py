from dataclasses import replace
import json
import struct
import unittest
from pathlib import Path

from cfv2 import parse
from cfv2_metadata import StorageIdentity, ControlRecord, DescribeRecord, HeaderRecord, audit, records

WORKSPACE = Path(__file__).resolve().parents[3]


class MetadataTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.data = (WORKSPACE / "FFM170-X-0417-6-CLOSE-A3-2024.11.14.cgr").read_bytes()
        cls.container = parse(cls.data)

    def test_known_native_timestamp_link(self):
        paths = [WORKSPACE / name for name in ("Part1.cgr", "three3.cgr", "gt_N128.cgr",
                 "FFM170-X-0417-6-CLOSE-A3-2024.11.14.cgr")]
        paths += sorted((WORKSPACE / "catia_to_jt/research").glob("fixtures*/*.cgr"))
        for path in paths:
            with self.subTest(path=path):
                report = audit(parse(path.read_bytes()))
                self.assertTrue(report["three_way_timestamp_match"])
                self.assertTrue(report["record_roundtrips_exact"])
                self.assertEqual({r["format_tag"] for r in report["controls"]}, {report["describe_format_tag"]})

    def test_identity_little_endian_words(self):
        name = ".4eb51e58200d0000d713986aea230000"
        identity = StorageIdentity.parse(name)
        self.assertEqual(identity, StorageIdentity(0x581EB54E, 3360, 1788351447, 9194))
        self.assertEqual(identity.encode(), name)
        self.assertEqual(StorageIdentity.parse("D" + name), identity)  # Old diagnostic alias, not on-disk name.
        self.assertEqual(identity.process_id, 3360)
        for seconds in (0, 1, 0xFFFFFFFF):
            changed = replace(identity, unix_seconds=seconds, word3=0xFEDCBA98)
            self.assertEqual(StorageIdentity.parse(changed.encode(), changed.unix_seconds), changed)

    def test_identity_invalid_shapes(self):
        for name in ("", "D.", "D." + "0" * 31, "D." + "0" * 33, "E." + "0" * 32, "D." + "g" * 32):
            with self.subTest(name=name), self.assertRaises(ValueError):
                StorageIdentity.parse(name)

    def test_header_control_values_are_preserved_not_forced_to_defaults(self):
        for name in ("Part1.cgr", "tetra_imported.cgr", "_t_fullgreen.cgr"):
            c = parse((WORKSPACE / name).read_bytes())
            _, node, record, _, _ = records(c)
            self.assertEqual(record.encode(), c.payload(node))
        self.assertEqual(records(parse((WORKSPACE / "tetra_imported.cgr").read_bytes()))[2].scalar59, 0)
        self.assertEqual(records(parse((WORKSPACE / "_t_fullgreen.cgr").read_bytes()))[2].rgba51, b"\x00\xff\x00\xff")

    def test_metadata_records_roundtrip_unknown_values(self):
        control = ControlRecord(255, 0x12345678, 0xFEDCBA98)
        self.assertEqual(ControlRecord.parse(control.encode()), control)
        describe = DescribeRecord(0x12345678, 99, 1001, bytes(range(7)))
        self.assertEqual(DescribeRecord.parse(describe.encode()), describe)

    def test_header_envelope_and_nonfinite_rejected(self):
        _, node, record, _, _ = records(self.container)
        data = self.container.payload(node)
        for changed in (data[:-1], data + b"\0", b"\x00" + data[1:], data[:1] + struct.pack("<I", 61) + data[5:],
                        data[:11] + struct.pack("<f", float("nan")) + data[15:]):
            with self.subTest(size=len(changed)), self.assertRaises(ValueError):
                HeaderRecord.parse(changed)
        with self.assertRaises(ValueError):
            replace(record, bounds=(10, 0, 0, 0, 0, 0)).encode()

    def test_timestamp_mismatch_detected_without_rejecting_container(self):
        c = parse(self.data)
        _, node, _, _, _ = records(c)
        changed = bytearray(self.data)
        # Header is one contiguous extent in this native profile.
        struct.pack_into("<I", changed, node.extents[0].physical + 5, 1)
        self.assertFalse(audit(parse(changed))["three_way_timestamp_match"])

    def test_controlled_com_exports_match_process_and_geometry(self):
        folder = WORKSPACE / "analysis_cgr/container_20261015_r2/native_identity"
        report = json.loads((folder / "native_identity_fixtures.json").read_text(encoding="utf-8"))
        self.assertEqual(len(report["fixtures"]), 4)
        containers = []
        for fixture in report["fixtures"]:
            c = parse((folder / (fixture["label"] + ".cgr")).read_bytes())
            identity = StorageIdentity.parse(c.nodes[1].name)
            self.assertIn(identity.process_id, report["cnext_pids"])
            self.assertTrue(audit(c)["three_way_timestamp_match"])
            self.assertEqual(records(c)[2].bounds, tuple(fixture["expected_point"] * 2))
            self.assertEqual(c.rebuilt(), c.data)
            containers.append(c)
        initial, repeat, changed, restored = containers
        # Re-export without changing the Part modifies identity/metadata only.
        for a, b in zip(initial.nodes, repeat.nodes):
            if a.id != 9:
                self.assertEqual(initial.payload(a), repeat.payload(b))
        initial_header = records(initial)[2]
        self.assertEqual(replace(records(repeat)[2], unix_seconds=initial_header.unix_seconds), initial_header)
        counters = [StorageIdentity.parse(c.nodes[1].name).word3 for c in containers]
        self.assertTrue(all(a < b for a, b in zip(counters, counters[1:])))
        for c in (changed, restored):
            self.assertEqual([a.id for a, b in zip(initial.nodes, c.nodes)
                              if a.id != 9 and initial.payload(a) != c.payload(b)], [] if c is restored else [12])
        self.assertEqual(replace(records(restored)[2], unix_seconds=initial_header.unix_seconds), initial_header)


if __name__ == "__main__":
    unittest.main()
