"""R4 counted root-name boundary and unpadded identity word regressions."""
import struct
import unittest
from pathlib import Path

from cfv2 import parse
from cfv2_metadata import StorageIdentity, audit, records

WORKSPACE = Path(__file__).resolve().parents[3]


class CountedIdentityTests(unittest.TestCase):
    def test_root_name_has_u32_byte_count_not_a_D_or_B_prefix(self):
        for filename, expected_length in (("Part1.cgr", 68), ("cyl2.cgr", 66), ("gt_N60.cgr", 66)):
            with self.subTest(file=filename):
                c = parse((WORKSPACE / filename).read_bytes())
                # Directory header64 + root allocation86 + descriptor fixed84.
                count_offset = c.directory_offset + 64 + 86 + 84
                count = struct.unpack_from(">I", c.data, count_offset)[0]
                self.assertEqual(count, expected_length)
                name = c.data[count_offset + 4:count_offset + 4 + count]
                self.assertEqual(name, (c.nodes[1].name + "\0").encode("utf-16be"))
                self.assertTrue(c.nodes[1].name.startswith("."))
                self.assertEqual(c.rebuilt(), c.data)
                for wrong in (0, count - 2, count + 2, 0xFFFFFFFF):
                    corrupted = bytearray(c.data)
                    struct.pack_into(">I", corrupted, count_offset, wrong)
                    with self.assertRaises(ValueError):
                        parse(corrupted)

    def test_previously_unsupported_short_native_names_match_header_and_filetime(self):
        paths = [WORKSPACE / n for n in ("cyl2.cgr", "gt_N300.cgr", "gt_N60.cgr", "loft.cgr", "tri3.cgr")]
        paths += sorted((WORKSPACE / "analysis_cgr/container_20261015_r3/native_options").glob("*.cgr"))
        self.assertEqual(len(paths), 17)
        for path in paths:
            with self.subTest(file=path.name):
                c = parse(path.read_bytes())
                header = records(c)[2]
                identity = StorageIdentity.parse(c.nodes[1].name, header.unix_seconds)
                self.assertEqual(identity.encode(), c.nodes[1].name)
                self.assertEqual(len(c.nodes[1].name), 32)  # Dot + 31 actual hex digits.
                self.assertTrue(audit(c)["three_way_timestamp_match"])
                self.assertEqual(identity.word0, 0x581EB54E)

    def test_ambiguous_unpadded_identity_requires_context(self):
        # The 31-digit examples have four possible width partitions without
        # timestamp context. Do not silently fix every component at eight chars.
        name = ".4eb51e589c3a00006e9936a180f0000"
        with self.assertRaisesRegex(ValueError, "unambiguous timestamp context"):
            StorageIdentity.parse(name)
        identity = StorageIdentity.parse(name, 1788078342)
        self.assertEqual(identity.process_id, 15004)
        self.assertEqual(identity.word3, 3864)
        self.assertEqual(identity.encode(), name)

    def test_short_identity_header_timestamp_corruption_is_a_mismatch(self):
        c = parse((WORKSPACE / "cyl2.cgr").read_bytes())
        data = bytearray(c.data)
        struct.pack_into("<I", data, records(c)[1].extents[0].physical + 5, 1)
        self.assertFalse(audit(parse(data))["three_way_timestamp_match"])

    def test_controlled_native_counter_cycle_proves_all_observed_widths(self):
        import json
        folder = WORKSPACE / "analysis_cgr/container_20261016_r4/native_identity"
        report = json.loads((folder / "native_identity_boundaries.json").read_text(encoding="utf-8"))
        self.assertEqual(report["errors"], [])
        self.assertEqual(report["name_byte_counts"], [64, 66, 68])
        self.assertEqual(report["counter_low_bytes_observed"], list(range(256)))
        self.assertTrue(report["document_count_restored"])
        self.assertEqual(len(report["fixtures"]), 275)
        for fixture in report["fixtures"]:
            c = parse((folder / (fixture["label"] + ".cgr")).read_bytes())
            identity = StorageIdentity.parse(c.nodes[1].name, records(c)[2].unix_seconds)
            # Independent spelling check: uint32 BE print of each LE word,
            # without the zero padding used by the old fixed-width hypothesis.
            words = (identity.word0, identity.process_id, identity.unix_seconds, identity.word3)
            expected = "." + "".join(format(int.from_bytes(struct.pack("<I", word), "big"), "x") for word in words)
            self.assertEqual(c.nodes[1].name, expected)
            self.assertEqual(identity.process_id, fixture["identity"]["process_id"])
            self.assertTrue(audit(c)["three_way_timestamp_match"])
            self.assertEqual(c.rebuilt(), c.data)


if __name__ == "__main__":
    unittest.main()
