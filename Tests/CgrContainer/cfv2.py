"""Sequential reader/serializer for the observed uncompressed CFV2 profile.

No signature scanning or embedded directory image. Unsupported reserved fields,
classes, tree relationships and partial byte coverage are rejected explicitly.
This does not interpret geometry, storage identities or application payloads.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from copy import deepcopy
from datetime import datetime, timedelta, timezone
import hashlib
import struct

MAGIC = b"V5_CFV2\0"
DIRECTORY_MAGIC = b"CATIA_V5 CB0001\0"


class Reader:
    def __init__(self, data):
        self.data, self.position = bytes(data), 0

    def take(self, size):
        if size < 0 or size > len(self.data) - self.position:
            raise ValueError(f"Truncated field at {self.position:#x}")
        value = self.data[self.position:self.position + size]
        self.position += size
        return value

    def u32(self):
        return struct.unpack(">I", self.take(4))[0]

    def u16(self):
        return struct.unpack(">H", self.take(2))[0]

    def expect(self, expected):
        if self.take(len(expected)) != expected:
            raise ValueError(f"Unexpected control/reserved field at {self.position - len(expected):#x}")


def u32(value):
    return struct.pack(">I", value)


def u16(value):
    return struct.pack(">H", value)


def name_bytes(name):
    return (name + "\0").encode("utf-16be")


def read_name(raw):
    if len(raw) < 2 or len(raw) % 2 or raw[-2:] != b"\0\0":
        raise ValueError("Invalid counted UTF-16 name")
    try:
        text = raw[:-2].decode("utf-16be")
    except UnicodeDecodeError as error:
        raise ValueError("Invalid UTF-16 name") from error
    if not text or not all(c.isprintable() for c in text):
        raise ValueError("Unsupported name form")
    return text


@dataclass(eq=True)
class Metadata:
    created: int = 0
    time2: int = 0
    time3: int = 0
    flags: int = 0

    @classmethod
    def read(cls, reader):
        reader.expect(bytes(4))
        times = []
        for _ in range(3):
            low, high = reader.u32(), reader.u32()
            times.append((high << 32) | low)
        result = cls(*times, reader.u32())
        reader.expect(bytes(32))
        return result

    def encode(self):
        result = bytes(4)
        for value in (self.created, self.time2, self.time3):
            result += u32(value & 0xFFFFFFFF) + u32(value >> 32)
        return result + u32(self.flags) + bytes(32)


@dataclass(eq=True)
class Extent:
    physical: int
    size: int
    logical: int

    def encode(self):
        return struct.pack(">5I", self.physical, self.size, self.size, self.logical, 0)


@dataclass
class Node:
    id: int
    kind: int
    size: int
    metadata: Metadata
    extents: list[Extent] = field(default_factory=list)
    parent: int = 0
    name: str = ""
    # Byte position is diagnostic only; never used by the serializer.
    directory_position: int = 0

    def allocation(self):
        return (u32(86 + 20 * len(self.extents)) + u32(self.id) + u16(self.kind)
                + u32(self.kind) + u32(self.size) + self.metadata.encode()
                + u32(len(self.extents)) + b"".join(e.encode() for e in self.extents))

    def descriptor(self):
        if self.kind == 0:
            return b""
        if self.parent == 0:
            raw_name = name_bytes(self.name)
            return (bytes(12) + u32(self.kind) + u32(self.size) + self.metadata.encode()
                    + u32(len(raw_name)) + raw_name)
        return (u32(self.parent) + u32(self.kind) + u32(self.size) + self.metadata.encode()
                + u32(len(name_bytes(self.name))) + name_bytes(self.name))


def allocation(reader):
    start = reader.position
    length, identity, kind = reader.u32(), reader.u32(), reader.u16()
    if kind not in (0, 1, 2) or reader.u32() != kind:
        raise ValueError("Unsupported allocation class/repeated class")
    size = reader.u32()
    metadata = Metadata.read(reader)
    count = reader.u32()
    if count > (len(reader.data) - reader.position) // 20 or length != 86 + 20 * count:
        raise ValueError("Allocation length/extent count mismatch")
    extents, logical = [], 0
    for _ in range(count):
        physical, stored, repeated, offset, flags = struct.unpack(">5I", reader.take(20))
        if stored == 0 or stored != repeated or offset != logical or flags:
            raise ValueError("Invalid extent length, continuity or flags")
        extents.append(Extent(physical, stored, offset))
        logical += stored
    if logical != size:
        raise ValueError("Declared logical size mismatch")
    if kind == 0 and (identity != 1 or size or metadata != Metadata() or extents):
        raise ValueError("Unsupported root allocation")
    # Native adhesion streams/storages can use size=0 and count=0. Some such
    # storages still have children in the directory, without a payload index.
    return Node(identity, kind, size, metadata, extents, directory_position=start)


@dataclass
class Container:
    data: bytes
    directory_offset: int
    directory_size: int
    nodes: list[Node]
    control21: int = 12  # Observed constant; point fixtures disprove a record-count interpretation.

    def payload(self, node):
        return b"".join(self.data[e.physical:e.physical + e.size] for e in node.extents)

    def storage_index(self, owner):
        if owner.size == 0:
            return None  # Index absent; different from a present zero-child index.
        reader = Reader(self.payload(owner))
        length, identity, count = reader.u32(), reader.u32(), reader.u32()
        if length != owner.size or identity != owner.id or count > (length - 12) // 16:
            raise ValueError("Invalid storage index header")
        entries = []
        for _ in range(count):
            start = reader.position
            size, child, kind = reader.u32(), reader.u32(), reader.u16()
            name = read_name(reader.take(reader.u32()))
            if reader.position - start != size:
                raise ValueError("Storage child record length mismatch")
            entries.append((child, kind, name))
        if reader.position != owner.size:
            raise ValueError("Unconsumed storage index bytes")
        return entries

    def directory(self):
        """Generate every descriptor and allocation from parsed scalar fields."""
        records = b"".join(n.descriptor() + n.allocation() for n in self.nodes)
        length = 64 + len(records) + 12
        header = (DIRECTORY_MAGIC + b"\x4e" + u16(1000) + u16(64)
                  + u16(self.control21) + u16(len(self.nodes) - 1) + u16(len(self.nodes))
                  + u32(sum(n.size for n in self.nodes if n.kind == 2))
                  + u32(self.directory_offset + length) + bytes(29))
        return header + records + u32(length) + b"CB__END\0"

    def rebuilt(self):
        """Preserve physical payload placement, regenerate both container headers."""
        directory = self.directory()
        header = (MAGIC + u32(self.directory_offset) + u32(len(directory))
                  + b"\xff" * 8 + bytes(35) + b"\x02\x5f\r\n\0")
        return header + self.data[64:self.directory_offset] + directory

    def summary(self):
        def timestamp(value):
            if not value:
                return None
            try:
                return (datetime(1601, 1, 1, tzinfo=timezone.utc)
                        + timedelta(microseconds=value // 10)).isoformat()
            except OverflowError:
                return f"out-of-range:{value}"
        return {
            "bytes": len(self.data), "directory_bytes": self.directory_size,
            "sha256": hashlib.sha256(self.data).hexdigest(),
            "regenerated_byte_exact": self.rebuilt() == self.data,
            "nodes": [{"id": n.id, "parent": n.parent, "kind": n.kind, "name": n.name,
                       "bytes": n.size, "extents": len(n.extents),
                       "created_filetime_utc": timestamp(n.metadata.created),
                       "time2_filetime_utc": timestamp(n.metadata.time2),
                       "time3_filetime_utc": timestamp(n.metadata.time3),
                       "sha256": hashlib.sha256(self.payload(n)).hexdigest()}
                      for n in self.nodes],
        }


def parse(data):
    data = bytes(data)
    file = Reader(data[:64])
    file.expect(MAGIC)
    offset, size = file.u32(), file.u32()
    file.expect(b"\xff" * 8 + bytes(35) + b"\x02\x5f\r\n\0")
    if offset < 64 or size < 64 + 86 + 12 or offset + size != len(data):
        raise ValueError("Invalid final directory range")
    reader = Reader(data[offset:])
    reader.expect(DIRECTORY_MAGIC + b"\x4e" + u16(1000) + u16(64))
    control21, active, count = reader.u16(), reader.u16(), reader.u16()
    stream_bytes, total_bytes = reader.u32(), reader.u32()
    reader.expect(bytes(29))
    if active != count - 1 or total_bytes != len(data):
        raise ValueError("Directory counter/physical byte summary mismatch")
    if count < 2 or count > (size - 76) // 86:
        raise ValueError("Impossible allocation count")
    nodes = [allocation(reader)]
    for expected_id in range(2, count + 1):
        if expected_id == 2:
            reader.expect(bytes(12))
            parent, kind, declared = 0, reader.u32(), reader.u32()
            metadata = Metadata.read(reader)
            name = read_name(reader.take(reader.u32()))
        else:
            parent, kind, declared = reader.u32(), reader.u32(), reader.u32()
            metadata = Metadata.read(reader)
            name = read_name(reader.take(reader.u32()))
        node = allocation(reader)
        if (node.id, node.kind, node.size, node.metadata) != (expected_id, kind, declared, metadata):
            raise ValueError("Descriptor and allocation disagree")
        if expected_id == 2:
            if kind != 1:
                raise ValueError("Top storage must have class 1")
        elif parent < 2 or parent >= expected_id or nodes[parent - 1].kind != 1:
            raise ValueError("Unsupported/cyclic storage parent")
        node.parent, node.name = parent, name
        nodes.append(node)
    if reader.position != size - 12:
        raise ValueError("Unconsumed directory bytes")
    reader.expect(u32(size) + b"CB__END\0")
    if stream_bytes != sum(n.size for n in nodes if n.kind == 2):
        raise ValueError("Logical stream byte summary mismatch")
    extents = sorted((e for n in nodes for e in n.extents), key=lambda e: e.physical)
    cursor = 64
    for extent in extents:
        if extent.physical != cursor or extent.physical + extent.size > offset:
            raise ValueError("Physical extent coverage has a gap/overlap/out-of-bounds")
        cursor += extent.size
    if cursor != offset:
        raise ValueError("Incomplete physical extent coverage")
    container = Container(data, offset, size, nodes, control21)
    for owner in nodes:
        if owner.kind == 1:
            children = container.storage_index(owner)
            expected = [(n.id, n.kind, n.name) for n in nodes if n.parent == owner.id]
            if children is not None and sorted(children) != sorted(expected):
                raise ValueError(f"Storage index disagrees with directory tree: node {owner.id} {owner.name!r}; "
                                 f"index={children!r}, directory={expected!r}")
    return container


def refragment(data, counts):
    """Experimental physical rewrite. Preserve every logical payload byte.

    Geometry payloads might still contain unknown physical-address dependencies;
    successful structural verification alone does not prove CATIA compatibility.
    """
    source = parse(data)
    if any(key < 2 or key > len(source.nodes) for key in counts):
        raise ValueError("Unknown allocation ID")
    nodes = deepcopy(source.nodes)
    physical = bytearray(64)
    for node in nodes[1:]:
        payload = source.payload(node)
        count = counts.get(node.id, len(node.extents))
        if not payload:
            if count != 0:
                raise ValueError("Empty stream must have zero fragments")
            node.extents = []
            continue
        if not isinstance(count, int) or count < 1 or count > len(payload):
            raise ValueError("Invalid fragment count")
        node.extents = []
        logical = 0
        for index in range(count):
            size = len(payload) // count + (index < len(payload) % count)
            node.extents.append(Extent(len(physical), size, logical))
            physical.extend(payload[logical:logical + size])
            logical += size
    generated = Container(bytes(physical), len(physical), 0, nodes, source.control21).rebuilt()
    check = parse(generated)
    if any(source.payload(a) != check.payload(b) for a, b in zip(source.nodes, check.nodes)):
        raise ValueError("Logical bytes changed during refragmentation")
    return generated
