"""Bounded structural inventory for Siemens JT 10.x files.

This identifies the file header, table entries, physical segment envelopes and
XZ-compressed payloads. It does not claim to decode JT element geometry.
"""
from __future__ import annotations

import argparse
import collections
import hashlib
import json
import lzma
from pathlib import Path
import struct
import uuid


HEADER_VERSION_BYTES = 80
HEADER_SIZE = 109
# The sample resolves to 382 consecutive 32-byte records.  Scan and validate
# every candidate against its physical segment envelope instead of trusting a
# byte stride alone: its GUID, 64-bit offset and 32-bit byte count must agree.
TOC_MIN_ENTRY_SIZE = 28
XZ_MAGIC = b"\xfd7zXZ\x00"


def guid(raw: bytes) -> str:
    return str(uuid.UUID(bytes_le=raw))


def u32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<I", data, offset)[0]


def u64(data: bytes, offset: int) -> int:
    return struct.unpack_from("<Q", data, offset)[0]


def printable_strings(data: bytes, minimum: int = 5) -> list[str]:
    rows: list[str] = []
    start = None
    for i, value in enumerate(data + b"\0"):
        if 32 <= value <= 126:
            if start is None:
                start = i
        elif start is not None:
            if i - start >= minimum:
                rows.append(data[start:i].decode("ascii"))
            start = None
    return rows


def analyze(path: Path) -> dict:
    data = path.read_bytes()
    if len(data) < HEADER_SIZE:
        raise ValueError("JT file is shorter than the fixed header")
    version = data[:HEADER_VERSION_BYTES].decode("ascii", errors="replace").rstrip(" \0\r\n")
    byte_order = data[80]
    reserved = u32(data, 81)
    declared_offset = u64(data, 85)
    lsg_id = guid(data[93:109])
    if declared_offset + 4 > len(data):
        raise ValueError("header TOC offset is outside the file")
    toc_start = declared_offset
    count = u32(data, toc_start)
    toc_records_start = toc_start + 4

    toc_positions = []
    for p in range(toc_records_start, len(data) - TOC_MIN_ENTRY_SIZE + 1):
        offset = u64(data, p + 16)
        size = u32(data, p + 24)
        if size < 28 or offset + size > len(data):
            continue
        if data[p:p + 16] != data[offset:offset + 16]:
            continue
        if u32(data, offset + 20) != size:
            continue
        toc_positions.append(p)
    if len(toc_positions) != count:
        raise ValueError(f"confirmed TOC records {len(toc_positions)} != header count {count}")
    toc_end = max(toc_positions) + TOC_MIN_ENTRY_SIZE

    entries = []
    segment_types = collections.Counter()
    stored_sizes = collections.Counter()
    compressed = collections.Counter()
    raw_bytes = 0
    xz_unpacked = 0
    xz_failures = []
    element_candidates = collections.Counter()
    duplicate_ids = collections.Counter()
    ranges = []
    for ordinal, p in enumerate(toc_positions):
        segment_id = guid(data[p:p + 16])
        offset = u64(data, p + 16)
        size = u32(data, p + 24)
        if offset + size > len(data) or size < 28:
            raise ValueError(f"TOC entry {ordinal} is outside the file")
        if data[offset:offset + 16] != data[p:p + 16]:
            raise ValueError(f"TOC/segment GUID mismatch at entry {ordinal}")
        kind = u32(data, offset + 16)
        declared_size = u32(data, offset + 20)
        if declared_size != size:
            raise ValueError(f"TOC/segment length mismatch at entry {ordinal}")
        body = data[offset + 28:offset + size]
        info = {
            "ordinal": ordinal,
            "id": segment_id,
            "offset": offset,
            "bytes": size,
            "type": kind,
            "body_bytes": len(body),
            "toc_offset": p,
        }
        # JT compressed envelopes seen in this file are: uint32 payload size,
        # byte codec=3, then a normal XZ stream. Keep the test narrow and fail
        # closed for other codec layouts.
        if len(body) >= 11 and body[5:11] == XZ_MAGIC:
            info["codec"] = "codec-3/xz"
            info["declared_payload_bytes"] = u32(body, 0)
            try:
                unpacked = lzma.decompress(body[5:])
                info["unpacked_bytes"] = len(unpacked)
                info["unpacked_prefix_hex"] = unpacked[:24].hex()
                if len(unpacked) >= 20:
                    info["element_candidate_id"] = guid(unpacked[4:20])
                    element_candidates[(kind, info["element_candidate_id"])] += 1
                xz_unpacked += len(unpacked)
                compressed["codec-3/xz"] += 1
            except lzma.LZMAError as error:
                xz_failures.append({"ordinal": ordinal, "error": str(error)})
        else:
            info["codec"] = "raw-or-unclassified"
            raw_bytes += len(body)
            compressed["raw-or-unclassified"] += 1
            # Type-7 raw records place their element byte count in the four
            # bytes immediately before this slice; their element GUID begins
            # at the first body byte. Other unclassified encodings are not
            # assigned an element type speculatively.
            if kind == 7 and len(body) >= 16:
                info["element_candidate_id"] = guid(body[:16])
                element_candidates[(kind, info["element_candidate_id"])] += 1
        entries.append(info)
        duplicate_ids[segment_id] += 1
        segment_types[str(kind)] += 1
        stored_sizes[str(kind)] += size
        ranges.append((offset, offset + size, ordinal))

    overlaps = []
    previous_end = -1
    for start, end, ordinal in sorted(ranges):
        if start < previous_end:
            overlaps.append(ordinal)
        previous_end = max(previous_end, end)
    lsg_entries = [item for item in entries if item["id"] == lsg_id]
    return {
        "file": str(path),
        "sha256": hashlib.sha256(data).hexdigest(),
        "bytes": len(data),
        "header": {
            "version": version,
            "byte_order_marker": byte_order,
            "reserved": reserved,
            "declared_offset": declared_offset,
            "logical_scene_graph_id": lsg_id,
        },
        "toc": {
            "offset": toc_start,
            "entry_start_min": toc_records_start,
            "entry_end_min": toc_end,
            "entry_offsets": toc_positions,
            "entries": count,
            "end": toc_end,
            "lsg_entries": lsg_entries,
            "duplicate_ids": {key: value for key, value in duplicate_ids.items() if value > 1},
            "overlapping_segments": overlaps,
        },
        "segments": {
            "types": dict(segment_types),
            "stored_bytes_by_type": dict(stored_sizes),
            "codec_counts": dict(compressed),
            "xz_unpacked_bytes": xz_unpacked,
            "raw_or_unclassified_body_bytes": raw_bytes,
            "xz_failures": xz_failures,
            "element_candidate_counts": [
                {"segment_type": kind, "element_id": element_id, "count": value}
                for (kind, element_id), value in sorted(element_candidates.items())
            ],
            "entries": entries,
        },
        "ascii_strings": printable_strings(data),
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    value = analyze(args.input)
    args.output.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({
        "bytes": value["bytes"],
        "version": value["header"]["version"],
        "toc_entries": value["toc"]["entries"],
        "segment_types": value["segments"]["types"],
        "codec_counts": value["segments"]["codec_counts"],
        "lsg_entries": value["toc"]["lsg_entries"],
    }, ensure_ascii=False))


if __name__ == "__main__":
    main()
