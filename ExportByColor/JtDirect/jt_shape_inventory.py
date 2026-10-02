"""Inventory JT Shape LOD (type 7) elements without decoding mesh topology.

JT type-7 segments are not globally LZMA-compressed.  Their data begins at
the segment header's data offset (24 bytes from the physical segment start)
with the normal element framing used by the JT specification.  This probe
validates that framing, records the canonical end marker and segment tail,
and extracts the stable quantisation prefix used by the sample's TriStrip
elements.  The large topological payload remains opaque until the P2 codec is
implemented.
"""
from __future__ import annotations

import argparse
import collections
import hashlib
import json
from pathlib import Path
from typing import Any

from jt_analyze import analyze
from jt_lsg import parse_element


SHAPE_TYPES = {
    "10dd10ab-2ac8-11d1-9b6b-0080c7bb5997": "TriStripSetShapeLOD",
    "10dd10a1-2ac8-11d1-9b6b-0080c7bb5997": "PolylineSetShapeLOD",
    "98114716-0011-0818-1998-080009835d5a": "PointSetShapeLOD",
    "98134716-0011-0818-1998-080009835d5a": "PointSetShapeLOD",
    "10dd109f-2ac8-11d1-9b6b-0080c7bb5997": "PolygonSetLOD",
}


def u32(data: bytes, pos: int) -> int:
    return int.from_bytes(data[pos : pos + 4], "little", signed=False)


def shape_payload(raw: bytes, entry: dict[str, Any]) -> bytes:
    start = int(entry["offset"]) + 24
    stop = int(entry["offset"]) + int(entry["bytes"])
    return raw[start:stop]


def scan_shape_segment(raw: bytes, entry: dict[str, Any]) -> dict[str, Any]:
    data = shape_payload(raw, entry)
    elements: list[dict[str, Any]] = []
    errors: list[str] = []
    pos = 0
    while pos < len(data):
        try:
            item, next_pos = parse_element(data, pos)
        except ValueError as exc:
            errors.append(str(exc))
            break
        item = dict(item)
        item.pop("object_data_hex_prefix", None)
        item["type"] = SHAPE_TYPES.get(item["type_id"], item["type"])
        if item["type"] != "EndOfElements" and item.get("base_type") != 4:
            errors.append(f"element at {pos} has base type {item.get('base_type')}, expected 4")
        if item["type"] != "EndOfElements":
            body = data[pos + 25 : next_pos]
            item["body_bytes"] = len(body)
            item["body_sha256"] = hashlib.sha256(body).hexdigest()
            if item["type"] == "TriStripSetShapeLOD" and len(body) >= 13:
                # These five bytes are the v10.6 TriStrip quantisation prefix;
                # the following 8 bytes are retained as opaque codec header.
                item["quantization"] = {
                    "base_version": body[0],
                    "bits_per_vertex": body[1],
                    "normal_bits_factor": body[2],
                    "bits_per_texture": body[3],
                    "bits_per_color": body[4],
                    "opaque_u32_5": u32(body, 5),
                    "opaque_u32_9": u32(body, 9),
                    "compressed_payload_offset": 13,
                    "compressed_payload_bytes": len(body) - 13,
                }
        elements.append(item)
        pos = next_pos
        if item["type"] == "EndOfElements":
            break

    tail = data[pos:]
    if not elements or elements[-1].get("type") != "EndOfElements":
        errors.append("missing type-7 end-of-elements marker")
    if tail != b"\x01\x00\x00\x00\x00\x00":
        errors.append(f"noncanonical type-7 tail ({tail.hex()})")

    return {
        "ordinal": entry["ordinal"],
        "segment_id": entry["id"],
        "segment_type": entry["type"],
        "segment_payload_bytes": len(data),
        "elements": elements,
        "element_count": len([x for x in elements if x.get("type") != "EndOfElements"]),
        "end_marker_offset": next((x["offset"] for x in elements if x.get("type") == "EndOfElements"), None),
        "tail_hex": tail.hex(),
        "errors": errors,
    }


def inventory_shape(path: Path) -> dict[str, Any]:
    inv = analyze(path)
    raw = path.read_bytes()
    segments = [entry for entry in inv["segments"]["entries"] if 7 <= int(entry["type"]) <= 16]
    reports = [scan_shape_segment(raw, entry) for entry in segments]
    element_counts = collections.Counter(
        element["type"] for report in reports for element in report["elements"]
    )
    quantization = collections.Counter(
        tuple(sorted(element.get("quantization", {}).items()))
        for report in reports
        for element in report["elements"]
        if element.get("quantization")
    )
    return {
        "file": str(path),
        "sha256": inv["sha256"],
        "version": inv["header"]["version"],
        "segments": reports,
        "summary": {
            "segment_count": len(reports),
            "segment_types": dict(collections.Counter(r["segment_type"] for r in reports)),
            "element_types": dict(element_counts),
            "tri_strip_quantization_prefixes": [
                {"count": count, "fields": dict(fields)} for fields, count in quantization.items()
            ],
            "invalid_segments": sum(bool(report["errors"]) for report in reports),
            "total_payload_bytes": sum(report["segment_payload_bytes"] for report in reports),
        },
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    report = inventory_shape(args.input)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report["summary"], ensure_ascii=False))


if __name__ == "__main__":
    main()
