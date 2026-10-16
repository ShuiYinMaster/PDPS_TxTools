"""Bounded scene grammar for observed 62/A3/95 records; geometry stays opaque.

No signature search/resynchronization. Unknown attribute fields remain typed raw
slots. Wire forms of compact counts and link IDs are preserved for roundtrips.
"""
from dataclasses import dataclass, field
import math
import struct


class Cursor:
    def __init__(self, data):
        self.data, self.pos = bytes(data), 0

    def take(self, count, limit):
        if count < 0 or self.pos + count > min(limit, len(self.data)):
            raise ValueError(f"Scene record exceeds bound at {self.pos:#x}")
        result = self.data[self.pos:self.pos + count]
        self.pos += count
        return result

    def compact(self, limit):
        first = self.take(1, limit)[0]
        return (struct.unpack("<I", self.take(4, limit))[0], True) if first == 255 else (first, False)


def compact(value, extended):
    if extended:
        return b"\xff" + struct.pack("<I", value)
    if not 0 <= value < 255:
        raise ValueError("Short compact value out of range")
    return bytes([value])


@dataclass(frozen=True)
class CommonHeader:
    # Compatibility tuples retain the old API: flags[0] is a compact mask,
    # flags[2]/encoding[0] are the low/high bytes of one LE graphics word.
    flags: tuple[int, int, int]
    encoding: tuple[int, int, int]
    attribute_word_be: int
    field11: int
    radius: float
    scalar16: float
    center: tuple[float, float, float]
    object_id: int
    inheritance_extended: bool = False

    @property
    def inheritance_mask(self):
        return self.flags[0]

    @property
    def color_inheritance_bits(self):
        return self.inheritance_mask & 0x404

    @property
    def width_inheritance(self):
        return bool(self.inheritance_mask & 0x10)

    @property
    def line_type_inheritance(self):
        return bool(self.inheritance_mask & 0x40)

    @property
    def opacity_inheritance(self):
        return bool(self.inheritance_mask & 0x1000)

    @property
    def line_width_code(self):
        return (self.graphics_style >> 4) & 63

    @property
    def graphics_style(self):
        return self.flags[2] | (self.encoding[0] << 8)

    @property
    def line_type_code(self):
        return self.encoding[0] >> 2

    @property
    def explicit_rgb(self):
        if self.encoding[1] != 255:
            return None  # Other color-source markers remain uninterpreted.
        word = self.attribute_word_be
        return ((word >> 8) & 255, (word >> 16) & 255, (word >> 24) & 255)

    @property
    def opacity(self):
        return self.encoding[2]

    @property
    def translucent(self):
        return bool(self.flags[1] & 0x80)

    @property
    def encoded_size(self):
        return len(self.encode())

    @property
    def no_pick(self):
        """Native SetPick(1) sets bit 0; SetPick(0) clears it."""
        return bool(self.flags[1] & 1)

    @property
    def volume_contribution_bit(self):
        """Observed 95 Analyze.Volume gate; does not prove a closed solid."""
        return bool(self.flags[1] & 0x10)

    @classmethod
    def read(cls, cursor, limit):
        inheritance, ie = cursor.compact(limit)
        flags = cursor.take(1, limit)[0]
        width, line = cursor.take(2, limit)
        data = cursor.take(31, limit)
        r, s, x, y, z, object_id = struct.unpack_from("<5fI", data, 7)
        if not all(math.isfinite(v) for v in (r, s, x, y, z)) or r < 0:
            raise ValueError("Invalid scene header scalar/sphere")
        return cls((inheritance, flags, width), (line, data[0], data[1]), struct.unpack_from(">I", data, 2)[0], data[6], r, s, (x, y, z), object_id, ie)

    def encode(self):
        return (compact(self.flags[0], self.inheritance_extended) + bytes([self.flags[1]])
                + struct.pack("<H", self.graphics_style)
                + bytes(self.encoding[1:]) + struct.pack(">I", self.attribute_word_be)
                + bytes([self.field11]) + struct.pack("<5fI", self.radius, self.scalar16, *self.center, self.object_id))


@dataclass
class Link:
    kind: int
    ref_id: int | None
    ref_extended: bool
    target: "Node"
    definition: bool
    offset: int = 0  # Diagnostic wire position; not serialized.

    def encode(self):
        data = bytes([self.kind])
        if self.ref_id is not None:
            data += compact(self.ref_id, self.ref_extended)
        return data + (self.target.encode() if self.definition else b"")


@dataclass
class Node:
    tag: int
    header: CommonHeader
    offset: int
    end: int = 0
    layer_code: int = 1023
    node_options: int = 1  # Observed word; full meaning remains unknown.
    matrix: tuple[float, ...] = ()
    children: list[Link] = field(default_factory=list)
    count_extended: bool = False
    geometry: bytes = b""
    pick_offset: int = 0
    pick_extended: bool = False
    trailer: float | None = None

    def encode(self):
        body = self.header.encode()
        if self.tag == 0x95:
            body += self.geometry
            # 95 length excludes only the tag. Pick offset is outside that span.
            return b"\x95\xff" + struct.pack("<I", len(body) + 5) + body + compact(self.pick_offset, self.pick_extended)
        body += struct.pack("<HHB", self.layer_code, self.node_options, bool(self.matrix))
        if self.matrix:
            body += struct.pack("<16f", *self.matrix)
        body += compact(len(self.children), self.count_extended)
        body += b"".join(link.encode() for link in self.children)
        if self.tag == 0xA3:
            body += struct.pack("<f", self.trailer)
            # A3 length excludes the tag AND extended-count marker.
            return b"\xa3\xff" + struct.pack("<I", len(body) + 4) + body
        return b"\x62" + body


@dataclass
class Scene:
    root: Node
    nodes: list[Node]
    trailing: bytes

    def encode(self):
        return self.root.encode() + self.trailing


def parse_scene(data):
    cursor = Cursor(data)
    refs, nodes = {}, []

    def read_node(limit, depth):
        if depth > 256 or len(nodes) >= 100000:
            raise ValueError("Scene depth/node limit exceeded")
        start = cursor.pos
        tag = cursor.take(1, limit)[0]
        record_end = limit
        if tag in (0xA3, 0x95):
            length, extended = cursor.compact(limit)
            if not extended:
                raise ValueError("Unsupported short sized scene record")
            record_end = start + length + (2 if tag == 0xA3 else 1)
            if record_end > limit or record_end < cursor.pos:
                raise ValueError("Scene envelope length exceeds parent bound")
        elif tag != 0x62:
            raise ValueError(f"Unsupported scene tag {tag:#x} at {start:#x}")
        node = Node(tag, CommonHeader.read(cursor, record_end), start)
        nodes.append(node)
        if tag == 0x95:
            node.geometry = cursor.take(record_end - cursor.pos, record_end)
            node.pick_offset, node.pick_extended = cursor.compact(limit)
        else:
            node.layer_code, node.node_options, matrix_flag = struct.unpack("<HHB", cursor.take(5, record_end))
            if matrix_flag not in (0, 1):
                raise ValueError("Unsupported scene matrix control")
            if matrix_flag:
                node.matrix = struct.unpack("<16f", cursor.take(64, record_end))
                if not all(math.isfinite(x) for x in node.matrix):
                    raise ValueError("Non-finite scene matrix")
            count, node.count_extended = cursor.compact(record_end)
            if count > min(100000, record_end - cursor.pos):
                raise ValueError("Impossible scene child count")
            for _ in range(count):
                link_start = cursor.pos
                kind = cursor.take(1, record_end)[0]
                ref_id, ref_extended, definition = None, False, True
                if kind in (1, 2):
                    ref_id, ref_extended = cursor.compact(record_end)
                    if ref_id in refs:
                        child, definition = refs[ref_id], False
                    elif kind == 2:
                        raise ValueError("Undefined scene reference")
                    else:
                        child = read_node(record_end, depth + 1)
                        refs[ref_id] = child
                elif kind == 0:
                    child = read_node(record_end, depth + 1)
                else:
                    raise ValueError("Unknown scene child link")
                node.children.append(Link(kind, ref_id, ref_extended, child, definition, link_start))
            if tag == 0xA3:
                node.trailer = struct.unpack("<f", cursor.take(4, record_end))[0]
                if not math.isfinite(node.trailer) or cursor.pos != record_end:
                    raise ValueError("Unconsumed A3 bytes or invalid trailer")
        node.end = cursor.pos
        return node

    root = read_node(len(data), 0)
    trailing = cursor.take(len(data) - cursor.pos, len(data))
    if trailing not in (b"", b"\0"):
        raise ValueError("Unconsumed scene bytes")
    scene = Scene(root, nodes, trailing)
    if scene.encode() != data:
        raise ValueError("Scene field roundtrip mismatch")
    return scene
