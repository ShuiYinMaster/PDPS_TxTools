"""Typed CFV2 metadata layouts; unidentified values stay explicitly named slots.

Identity word 1 matches the producing CATIA PID, and word 2 is Unix seconds.
Controlled native exports map five Header fields to visualization accuracy and
Describe bit 0x20 to LineicCgrMode. Other slots remain explicitly unidentified.
"""
from dataclasses import dataclass
import math
import re
import struct

FILETIME_UNIX_EPOCH = 116444736000000000
TICKS_PER_SECOND = 10000000


@dataclass(frozen=True)
class StorageIdentity:
    word0: int
    word1: int
    unix_seconds: int
    word3: int

    @property
    def process_id(self):
        # Keep word1 as the raw schema alias for older audit reports.
        return self.word1

    @classmethod
    def parse(cls, name, unix_seconds=None):
        # Old probes included the low UTF-16 half of the *name length* as 'D'.
        # Accept that old fixed-width spelling only as an explicit input alias.
        if re.fullmatch(r"D\.[0-9a-fA-F]{32}", name):
            return cls(*struct.unpack("<4I", bytes.fromhex(name[2:])))
        if not re.fullmatch(r"\.[0-9a-fA-F]{4,32}", name):
            raise ValueError("Unsupported storage identity; expected dot plus four hex words")
        raw = name[1:].lower()
        # Every native word is byte-swapped, then rendered as unpadded hex.
        # Word boundaries can be ambiguous; Header time supplies independent
        # context. Never silently choose between multiple valid partitions.
        candidates = []
        for a in range(1, 9):
            for b in range(1, 9):
                for c in range(1, 9):
                    d = len(raw) - a - b - c
                    if not 1 <= d <= 8:
                        continue
                    chunks = (raw[:a], raw[a:a+b], raw[a+b:a+b+c], raw[a+b+c:])
                    if any(len(s) > 1 and s[0] == "0" for s in chunks):
                        continue
                    words = [int.from_bytes(int(s, 16).to_bytes(4, "big"), "little") for s in chunks]
                    candidates.append(cls(*words))
        if len(candidates) != 1 and unix_seconds is not None:
            candidates = [identity for identity in candidates if identity.unix_seconds == unix_seconds]
        if len(candidates) != 1:
            raise ValueError(f"Storage identity partition requires unambiguous timestamp context; candidates={len(candidates)}")
        return candidates[0]

    def encode(self):
        return "." + "".join(struct.pack("<I", word).hex().lstrip("0") or "0"
                             for word in (self.word0, self.word1, self.unix_seconds, self.word3))


@dataclass(frozen=True)
class ControlRecord:
    opcode: int
    format_tag: int
    state: int

    @classmethod
    def parse(cls, data):
        if len(data) != 9:
            raise ValueError("Control record must be 9 bytes")
        return cls(*struct.unpack("<BII", data))

    def encode(self):
        return struct.pack("<BII", self.opcode, self.format_tag, self.state)


@dataclass(frozen=True)
class DescribeRecord:
    format_tag: int
    capability: int
    field8: int
    opaque_tail: bytes

    @property
    def lineic_cgr_mode(self):
        return bool(self.capability & 0x20)

    @classmethod
    def parse(cls, data):
        if len(data) != 19:
            raise ValueError("Describe record must be 19 bytes")
        return cls(*struct.unpack_from("<3I", data), data[12:])

    def encode(self):
        if len(self.opaque_tail) != 7:
            raise ValueError("Describe opaque tail must be 7 bytes")
        return struct.pack("<3I", self.format_tag, self.capability, self.field8) + self.opaque_tail


@dataclass(frozen=True)
class ApplicativeContainerList:
    """Observed short-ASCII adhesion registry, not every directory child.

    Native empty V4 adhesions have directory children but registry count zero.
    Longer/non-ASCII name encodings are intentionally outside this profile.
    """
    names: tuple[str, ...]

    @classmethod
    def parse(cls, data):
        if len(data) < 5 or data[0] != 255:
            raise ValueError("Unsupported applicative registry envelope")
        count = struct.unpack_from("<I", data, 1)[0]
        if count > (len(data) - 5) // 2:
            raise ValueError("Impossible applicative registry count")
        names, pos = [], 5
        for _ in range(count):
            if pos >= len(data):
                raise ValueError("Truncated applicative registry length")
            length = data[pos]
            pos += 1
            if not 0 < length < 128 or pos + length > len(data):
                raise ValueError("Unsupported/truncated short applicative name")
            try:
                name = data[pos:pos + length].decode("ascii")
            except UnicodeDecodeError as error:
                raise ValueError("Unsupported non-ASCII applicative name") from error
            if not name.isprintable():
                raise ValueError("Invalid applicative name")
            names.append(name)
            pos += length
        if pos != len(data):
            raise ValueError("Unconsumed applicative registry bytes")
        return cls(tuple(names))

    def encode(self):
        entries = []
        for name in self.names:
            raw = name.encode("ascii")
            if not 0 < len(raw) < 128 or not name.isprintable():
                raise ValueError("Unsupported short applicative name")
            entries.append(bytes([len(raw)]) + raw)
        return b"\xff" + struct.pack("<I", len(entries)) + b"".join(entries)


@dataclass(frozen=True)
class HeaderRecord:
    unix_seconds: int
    field9: int
    viz3d_accuracy_mode: int  # Native COM: 1=fixed, 0=proportional.
    viz3d_accuracy: float  # Value for the selected mode, not both settings.
    viz3d_curve_accuracy: float
    viz2d_accuracy_mode: int
    viz2d_accuracy: float
    field24: int
    field25: int
    field26: int
    bounds: tuple[float, ...]
    rgba51: bytes
    scalar55: float
    scalar59: float

    # Offset aliases keep older research callers readable and compatible.
    field10 = property(lambda self: self.viz3d_accuracy_mode)
    scalar11 = property(lambda self: self.viz3d_accuracy)
    scalar15 = property(lambda self: self.viz3d_curve_accuracy)
    field19 = property(lambda self: self.viz2d_accuracy_mode)
    scalar20 = property(lambda self: self.viz2d_accuracy)

    @classmethod
    def parse(cls, data):
        if len(data) != 63 or data[0] != 255 or struct.unpack_from("<I", data, 1)[0] != len(data) - 1:
            raise ValueError("Unsupported Header record envelope")
        result = cls(*struct.unpack_from("<IBBffBfBBB", data, 5),
                     struct.unpack_from("<6f", data, 27), data[51:55],
                     *struct.unpack_from("<2f", data, 55))
        result.validate()
        return result

    def validate(self):
        if len(self.bounds) != 6 or len(self.rgba51) != 4:
            raise ValueError("Header bounds/RGBA width mismatch")
        if not all(math.isfinite(x) for x in (*self.bounds, self.scalar11, self.scalar15,
                                             self.scalar20, self.scalar55, self.scalar59)):
            raise ValueError("Non-finite Header scalar")
        if any(self.bounds[i] > self.bounds[i + 3] for i in range(3)):
            raise ValueError("Inverted Header bounds")

    def encode(self):
        self.validate()
        body = (struct.pack("<IBBffBfBBB", self.unix_seconds, self.field9, self.field10,
                            self.scalar11, self.scalar15, self.field19, self.scalar20,
                            self.field24, self.field25, self.field26)
                + struct.pack("<6f", *self.bounds) + self.rgba51
                + struct.pack("<2f", self.scalar55, self.scalar59))
        return b"\xff" + struct.pack("<I", len(body) + 4) + body


def named_node(container, parent, name):
    matches = [n for n in container.nodes if n.parent == parent and n.name == name]
    if len(matches) != 1:
        raise ValueError(f"Expected one child {name!r} under node {parent}")
    return matches[0]


def records(container):
    top = container.nodes[1]
    header_storage = named_node(container, top.id, "Header")
    describe_storage = named_node(container, top.id, "Describe")
    header_node = named_node(container, header_storage.id, "MainDataStream")
    describe_node = named_node(container, describe_storage.id, "MainDataStream")
    return (top, header_node, HeaderRecord.parse(container.payload(header_node)),
            describe_node, DescribeRecord.parse(container.payload(describe_node)))


def applicative_records(container):
    matches = [n for n in container.nodes if n.parent == container.nodes[1].id and n.name == "Applicative Containers"]
    if not matches:
        return None
    if len(matches) != 1:
        raise ValueError("Expected at most one applicative storage")
    node = named_node(container, matches[0].id, "MainDataStream")
    return node, ApplicativeContainerList.parse(container.payload(node))


def audit(container):
    top, header_node, header, describe_node, describe = records(container)
    epoch = (top.metadata.created - FILETIME_UNIX_EPOCH) // TICKS_PER_SECOND
    try:
        identity = StorageIdentity.parse(top.name)
    except ValueError:
        candidates = set()
        for seconds in (header.unix_seconds, epoch):
            try:
                candidates.add(StorageIdentity.parse(top.name, seconds))
            except ValueError:
                pass
        if len(candidates) != 1:
            raise ValueError("Identity cannot be disambiguated consistently with Header/directory time")
        identity = candidates.pop()
    links = identity.unix_seconds == epoch == header.unix_seconds
    controls = []
    for node in container.nodes:
        if node.kind == 2 and node.name in ("MAIN", "Describe", "Header", "SceneGraph"):
            data = container.payload(node)
            record = ControlRecord.parse(data)
            if record.encode() != data:
                raise ValueError("Control record roundtrip mismatch")
            controls.append({"id": node.id, "name": node.name, "opcode": record.opcode,
                             "format_tag": record.format_tag, "state": record.state})
    if header.encode() != container.payload(header_node) or describe.encode() != container.payload(describe_node):
        raise ValueError("Metadata record roundtrip mismatch")
    if identity.encode() != top.name:
        raise ValueError("Identity canonical hex form mismatch")
    return {"identity": {"word0": identity.word0, "word1": identity.word1, "process_id": identity.process_id,
                         "unix_seconds": identity.unix_seconds, "word3": identity.word3},
            "header_unix_seconds": header.unix_seconds, "filetime_unix_seconds": epoch,
            "three_way_timestamp_match": links,
            "header_bounds": header.bounds, "header_rgba51": header.rgba51.hex(),
            "header_scalars": [header.scalar11, header.scalar15, header.scalar20, header.scalar55, header.scalar59],
            "visualization_accuracy": {"viz3d_accuracy_mode": header.viz3d_accuracy_mode,
                                       "viz3d_accuracy": header.viz3d_accuracy,
                                       "viz3d_curve_accuracy": header.viz3d_curve_accuracy,
                                       "viz2d_accuracy_mode": header.viz2d_accuracy_mode,
                                       "viz2d_accuracy": header.viz2d_accuracy},
            "describe_format_tag": describe.format_tag, "describe_capability": describe.capability,
            "describe_lineic_cgr_mode": describe.lineic_cgr_mode,
            "describe_field8": describe.field8, "describe_opaque_tail": describe.opaque_tail.hex(),
            "controls": controls, "record_roundtrips_exact": True}
