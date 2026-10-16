import struct
import unittest
from pathlib import Path

from cfv2 import Metadata, parse, refragment

WORKSPACE = Path(__file__).resolve().parents[3]


class NativeContainerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = (WORKSPACE / "FFM170-X-0417-6-CLOSE-A3-2024.11.14.cgr").read_bytes()
        cls.container = parse(cls.source)

    def test_native_samples_regenerate_exactly(self):
        paths = [WORKSPACE / name for name in ("Part1.cgr", "three3.cgr", "gt_N128.cgr",
                 "FFM170-X-0417-6-CLOSE-A3-2024.11.14.cgr")]
        paths += sorted((WORKSPACE / "catia_to_jt/research").glob("fixtures*/*.cgr"))
        self.assertGreaterEqual(len(paths), 23)
        for path in paths:
            with self.subTest(path=path):
                data = path.read_bytes()
                self.assertEqual(parse(data).rebuilt(), data)

    def test_timestamps_and_storage_tree(self):
        c = self.container
        self.assertEqual(c.nodes[1].metadata.created, 0x01DD3AD504BB9430)
        self.assertEqual(c.nodes[0].metadata, Metadata())
        self.assertEqual(c.storage_index(c.nodes[1]),
                         [(10, 1, "SceneGraph"), (7, 1, "Header"), (4, 1, "Describe"), (3, 2, "MAIN")])
        self.assertEqual([n.parent for n in c.nodes[10:]], [10, 10, 10])

    def test_point_counter_disproves_first_field_is_node_count(self):
        c = parse((WORKSPACE / "catia_to_jt/research/fixtures_20261013_v2/point_11_22_33.cgr").read_bytes())
        self.assertEqual(len(c.nodes), 12)
        self.assertEqual(c.control21, 12)
        self.assertEqual(c.rebuilt(), c.data)

    def mutate_u32(self, offset, value):
        data = bytearray(self.source)
        struct.pack_into(">I", data, offset, value)
        return data

    def test_reject_invalid_headers(self):
        for data in (b"", self.source[:63], self.source[:-1], self.source + b"\0",
                     b"BAD" + self.source[3:], self.mutate_u32(8, 65),
                     self.mutate_u32(12, 0xFFFFFFFF),
                     self.mutate_u32(self.container.directory_offset + 27, 0),
                     self.mutate_u32(self.container.directory_offset + 31, 0)):
            with self.subTest(size=len(data)), self.assertRaises(ValueError):
                parse(data)

    def test_reject_corrupt_allocations(self):
        c = self.container
        p = c.directory_offset + c.nodes[11].directory_position
        for field, value in ((0, 106), (4, 3), (10, 1), (14, 0), (82, 0xFFFFFFFF),
                             (86, 63), (90, 0), (94, 1), (98, 9), (102, 1),
                             (106, c.nodes[11].extents[0].physical)):
            with self.subTest(field=field), self.assertRaises(ValueError):
                parse(self.mutate_u32(p + field, value))

    def test_reject_descriptor_disagreement_and_parent_cycle(self):
        c = self.container
        p = c.directory_offset + c.nodes[2].directory_position - 90
        for field, value in ((0, 3), (4, 1), (8, 0), (16, 1), (76, 0xFFFFFFFE)):
            with self.subTest(field=field), self.assertRaises(ValueError):
                parse(self.mutate_u32(p + field, value))

    def test_reject_storage_index_lie(self):
        # The directory still parses, but its independent child index now points
        # to a different node. A signature-scanning parser would miss this.
        physical = self.container.nodes[1].extents[0].physical
        with self.assertRaises(ValueError):
            parse(self.mutate_u32(physical + 16, 7))

    def test_payload_signatures_do_not_create_directory_nodes(self):
        c = self.container
        data = bytearray(self.source)
        physical = c.nodes[11].extents[0].physical
        data[physical:physical + 16] = b"CATIA_V5 CB0001\0"
        changed = parse(data)
        self.assertEqual(len(changed.nodes), len(c.nodes))
        self.assertEqual(changed.rebuilt(), data)

    def test_refragment_native_ffm170_without_changing_logical_bytes(self):
        before = self.container
        after = parse(refragment(self.source, {12: 1, 13: 1}))
        self.assertEqual(before.directory_size - after.directory_size, 980)
        self.assertEqual(len(before.data) - len(after.data), 980)
        for a, b in zip(before.nodes, after.nodes):
            self.assertEqual((a.id, a.kind, a.parent, a.name, a.metadata),
                             (b.id, b.kind, b.parent, b.name, b.metadata))
            self.assertEqual(before.payload(a), after.payload(b))
        self.assertEqual(after.rebuilt(), after.data)

    def test_refragment_rejects_invalid_requests(self):
        for counts in ({1: 1}, {14: 1}, {12: 0}, {12: -1}, {12: self.container.nodes[11].size + 1}):
            with self.subTest(counts=counts), self.assertRaises(ValueError):
                refragment(self.source, counts)


if __name__ == "__main__":
    unittest.main()
