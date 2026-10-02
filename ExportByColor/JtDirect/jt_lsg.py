"""Strict JT v10 LSG element inventory.

This module is the first P1 reader used by the JT -> CGR route.  It decodes
the segment envelope and the common element headers, then extracts node IDs,
attributes, graph references, late-loaded segment references, property atoms,
and the LSG property table.  Shape payloads remain opaque until their own
element codec is implemented.

The LSG segment contains two framed element sequences followed by a compact
property table.  Parsing all three parts is important: the first sequence
describes the graph, while the second sequence maps graph properties to type7
shape segments.
"""
from __future__ import annotations

import argparse
import collections
import json
import lzma
from pathlib import Path
import struct
import uuid
from typing import Any

from jt_analyze import analyze


def guid(raw: bytes) -> str:
    return str(uuid.UUID(bytes_le=raw))


def u8(data: bytes, pos: int) -> int:
    return data[pos]


def u32(data: bytes, pos: int) -> int:
    return struct.unpack_from("<I", data, pos)[0]


def i32(data: bytes, pos: int) -> int:
    return struct.unpack_from("<i", data, pos)[0]


def f32(data: bytes, pos: int) -> float:
    return struct.unpack_from("<f", data, pos)[0]


def segment_payload(path: Path, entry: dict[str, Any]) -> bytes:
    raw = path.read_bytes()
    start = int(entry["offset"]) + 28
    body = raw[start : start + int(entry["bytes"]) - 28]
    if entry.get("codec") == "codec-3/xz":
        return lzma.decompress(body[5:])
    return body


TYPE_NAMES = {
    "10dd103e-2ac8-11d1-9b6b-0080c7bb5997": "PartitionNode",
    "10dd101b-2ac8-11d1-9b6b-0080c7bb5997": "GroupNode",
    "10dd102a-2ac8-11d1-9b6b-0080c7bb5997": "InstanceNode",
    "10dd102c-2ac8-11d1-9b6b-0080c7bb5997": "LODNode",
    "10dd104c-2ac8-11d1-9b6b-0080c7bb5997": "RangeLODNode",
    "10dd10f3-2ac8-11d1-9b6b-0080c7bb5997": "SwitchNode",
    "10dd1059-2ac8-11d1-9b6b-0080c7bb5997": "BaseShapeNode",
    "10dd107f-2ac8-11d1-9b6b-0080c7bb5997": "VertexShapeNode",
    "10dd1077-2ac8-11d1-9b6b-0080c7bb5997": "TriStripSetShapeNode",
    "10dd1046-2ac8-11d1-9b6b-0080c7bb5997": "PolylineSetShapeNode",
    "10dd1048-2ac8-11d1-9b6b-0080c7bb5997": "PolygonSetShapeNode",
    "ce357245-38fb-11d1-a506-006097bdc6e1": "MetaDataNode",
    "10dd1083-2ac8-11d1-9b6b-0080c7bb5997": "GeometricTransformAttribute",
    "10dd1030-2ac8-11d1-9b6b-0080c7bb5997": "MaterialAttribute",
    "ce357244-38fb-11d1-a506-006097bdc6e1": "PartNode",
    "10dd106e-2ac8-11d1-9b6b-0080c7bb5997": "StringPropertyAtom",
    "10dd102b-2ac8-11d1-9b6b-0080c7bb5997": "IntegerPropertyAtom",
    "10dd1004-2ac8-11d1-9b6b-0080c7bb5997": "JTObjectReferencePropertyAtom",
    "e0b05be5-fbbd-11d1-a3a7-00aa00d10954": "LateLoadedPropertyAtom",
    "98134716-0010-0818-1998-080009835d5a": "PointSetShapeNode",
    "ce357246-38fb-11d1-a506-006097bdc6e1": "FloatingPointAttribute",
    "10dd1019-2ac8-11d1-9b6b-0080c7bb5997": "FloatingPointPropertyAtom",
}

BASE_NAMES = {
    0: "BaseGraphNode",
    1: "GroupGraphNode",
    2: "ShapeGraphNode",
    3: "BaseAttribute",
    4: "ShapeLOD",
    5: "BaseProperty",
    6: "JTObjectReference",
    8: "LateLoadedProperty",
    9: "JtBase",
    255: "Unknown",
}


def safe_type(type_id: str) -> str:
    return TYPE_NAMES.get(type_id, type_id)


def read_base_node(data: bytes, pos: int, end: int) -> tuple[dict[str, Any], int]:
    """Read the v10 Base Node Data after the object-id field."""
    if pos + 9 > end:
        return {"error": "truncated base node data"}, pos
    version = u8(data, pos)
    flags = u32(data, pos + 1)
    attr_count = i32(data, pos + 5)
    cursor = pos + 9
    result: dict[str, Any] = {
        "version": version,
        "node_flags": flags,
        "attribute_count": attr_count,
        "attribute_ids": [],
    }
    if attr_count < 0 or attr_count > 1_000_000 or cursor + 4 * attr_count > end:
        result["error"] = "invalid attribute count"
        return result, cursor
    result["attribute_ids"] = [i32(data, cursor + 4 * n) for n in range(attr_count)]
    return result, cursor + 4 * attr_count


def decode_utf16_candidate(data: bytes, start: int, end: int) -> tuple[str, int] | None:
    """Decode a JT UTF-16 property string when its count field is present.

    String atoms in this file have a small common prefix followed by a u32
    code-unit count and a NUL-terminated UTF-16LE string.  A bounded search is
    used instead of assuming one version-specific prefix length.
    """
    # The v10 property atom used here has a fixed six-byte prefix
    # (u32 version + u16 flags), then stores ``character_count - 1``.  The
    # latter is a JT convention: the count excludes the final character while
    # the atom still carries a UTF-16 NUL terminator.  Decode this form first
    # so bytes such as 0x0240 in the prefix cannot be mistaken for text.
    if start + 10 <= end:
        count_pos = start + 6
        count = u32(data, count_pos)
        for actual_count in (count + 1, count):
            text_start = count_pos + 4
            text_end = text_start + actual_count * 2
            if actual_count > 1_000_000 or text_end + 2 > end:
                continue
            if data[text_end : text_end + 2] != b"\x00\x00":
                continue
            try:
                text = data[text_start:text_end].decode("utf-16-le")
            except UnicodeDecodeError:
                continue
            if all((ord(ch) >= 0x20 or ch in "\t\r\n") for ch in text):
                return text, text_end + 2

    # Fallback for older writers with a different common prefix.
    for count_pos in range(start + 2, min(end - 4, start + 16) + 1, 2):
        count = u32(data, count_pos)
        text_start = count_pos + 4
        text_end = text_start + count * 2
        if count > 1_000_000 or text_end > end:
            continue
        # The atom normally carries a two-byte NUL terminator.  Accept either
        # form because older writers omit it for empty strings.
        if text_end < end and data[text_end : text_end + 2] == b"\x00\x00":
            consumed = text_end + 2
        elif text_end == end:
            consumed = text_end
        else:
            continue
        try:
            text = data[text_start:text_end].decode("utf-16-le")
        except UnicodeDecodeError:
            continue
        if all((ord(ch) >= 0x20 or ch in "\t\r\n") for ch in text):
            return text, consumed
    return None


def read_property_atom(item: dict[str, Any], object_data: bytes) -> None:
    """Decode the stable v10 prefixes used by property atoms."""
    if len(object_data) < 4:
        return
    body = object_data[4:]
    if item["type"] == "StringPropertyAtom":
        decoded = decode_utf16_candidate(body, 0, len(body))
        if decoded:
            item["value"] = decoded[0]
            item["string_consumed_bytes"] = decoded[1]
    elif item["base_type"] == 6 and len(body) >= 5:
        item["version"] = u8(body, 0)
        item["reference_object_id"] = i32(body, 1)


def read_late_loaded(item: dict[str, Any], object_data: bytes) -> None:
    """Decode a v10 LateLoadedPropertyAtom reference.

    The 10.6 writer used by the sample stores version:u32, flags:u16,
    segment_id:GUID, segment_type:u32, and payload_object_id:u32 after the
    object id.  The bounded checks keep this reader safe on other versions.
    """
    body = object_data[4:]
    if len(body) < 30:
        item["error"] = "truncated late-loaded property"
        return
    item["version"] = u32(body, 0)
    item["state_flags"] = struct.unpack_from("<H", body, 4)[0]
    item["segment_id"] = guid(body[6:22])
    item["segment_type"] = u32(body, 22)
    item["payload_object_id"] = i32(body, 26)


def read_geometric_transform(item: dict[str, Any], object_data: bytes) -> None:
    """Decode the v10 geometric transform attribute used by InstanceNodes.

    JTReader reads the base-attribute header and then consumes the transform's
    format-version byte before its stored-values mask.  Keeping that ordering
    matches the reference reader and the v10.6 sample's sparse matrix layout.
    """
    body = object_data[4:]
    if len(body) < 13:
        item["error"] = "truncated geometric transform"
        return
    base_version = body[0]
    state_flags = body[1]
    field_inhibit_flags = u32(body, 2)
    field_final_flags = u32(body, 6)
    cursor = 10
    transform_version = body[cursor]
    cursor += 1
    stored_values_mask = struct.unpack_from("<H", body, cursor)[0]
    cursor += 2
    byte_count = stored_values_mask.bit_count() * 6
    use_f64 = len(body) - cursor > byte_count
    raw = [0.0] * 16
    for index in range(0, 16, 5):
        raw[index] = 1.0
    width = 8 if use_f64 else 4
    for index in range(16):
        if ((stored_values_mask << index) & 0x8000) == 0:
            continue
        if cursor + width > len(body):
            item["error"] = "truncated geometric transform matrix"
            return
        raw[index] = struct.unpack_from("<d" if use_f64 else "<f", body, cursor)[0]
        cursor += width
    item.update({
        "attribute_version": base_version,
        "state_flags": state_flags,
        "field_inhibit_flags": field_inhibit_flags,
        "field_final_flags": field_final_flags,
        "transform_version": transform_version,
        "stored_values_mask": stored_values_mask,
        "matrix_storage": "f64" if use_f64 else "f32",
        "matrix": raw,
        "matrix_bytes_consumed": cursor,
    })


def read_material_attribute(item: dict[str, Any], object_data: bytes) -> None:
    """Decode the v10 MaterialAttributeData diffuse color and coefficients."""
    body = object_data[4:]
    if len(body) < 93:
        item["error"] = "truncated material attribute"
        return
    item["attribute_version"] = body[0]
    item["state_flags"] = body[1]
    item["field_inhibit_flags"] = u32(body, 2)
    item["field_final_flags"] = u32(body, 6)
    cursor = 10
    item["material_version"] = body[cursor]
    item["data_flags"] = struct.unpack_from("<H", body, cursor + 1)[0]
    cursor += 3
    colors = []
    for _ in range(4):
        colors.append([f32(body, cursor + 4 * n) for n in range(4)])
        cursor += 16
    item["ambient_color"] = colors[0]
    item["diffuse_color"] = colors[1]
    item["specular_color"] = colors[2]
    item["emission_color"] = colors[3]
    item["shininess"] = f32(body, cursor)
    item["reflectivity"] = f32(body, cursor + 4)
    item["bumpiness"] = f32(body, cursor + 8)
    item["base_attribute_tail_hex"] = body[cursor + 12 :].hex()


def read_bbox(data: bytes, cursor: int) -> tuple[list[float], int] | None:
    if cursor + 24 > len(data):
        return None
    values = [f32(data, cursor + 4 * i) for i in range(6)]
    return values, cursor + 24


def read_partition_tail(item: dict[str, Any], object_data: bytes, cursor: int) -> None:
    """Read the v10 PartitionNodeData tail used for a world-bounds oracle."""
    # The sample carries the PartitionNodeData local version byte before the
    # flags even though the reference reader gates it on the minor version.
    if cursor + 1 > len(object_data):
        return
    item["partition_version"] = u8(object_data, cursor)
    cursor += 1
    if cursor + 8 > len(object_data):
        return
    item["partition_flags"] = i32(object_data, cursor)
    cursor += 4
    name_length = i32(object_data, cursor)
    cursor += 4
    if name_length < 0 or name_length > 1_000_000 or cursor + 2 * name_length > len(object_data):
        item["partition_tail_error"] = "invalid filename length"
        return
    item["file_name"] = object_data[cursor : cursor + 2 * name_length].decode("utf-16-le", errors="replace")
    cursor += 2 * name_length
    bbox = read_bbox(object_data, cursor)
    if bbox is None:
        item["partition_tail_error"] = "truncated transformed bbox"
        return
    item["transformed_bbox"] = {"min": bbox[0][0:3], "max": bbox[0][3:6]}
    cursor = bbox[1]
    if cursor + 4 + 24 > len(object_data):
        return
    item["area"] = f32(object_data, cursor)
    cursor += 4
    item["vertex_count_range"] = [i32(object_data, cursor), i32(object_data, cursor + 4)]
    item["node_count_range"] = [i32(object_data, cursor + 8), i32(object_data, cursor + 12)]
    item["polygon_count_range"] = [i32(object_data, cursor + 16), i32(object_data, cursor + 20)]
    cursor += 24
    if item.get("partition_flags", 0) & 1:
        bbox = read_bbox(object_data, cursor)
        if bbox is not None:
            item["untransformed_bbox"] = {"min": bbox[0][0:3], "max": bbox[0][3:6]}


def parse_element(data: bytes, pos: int) -> tuple[dict[str, Any], int]:
    if pos + 4 > len(data):
        raise ValueError(f"truncated element length at offset {pos}")
    length = i32(data, pos)
    if length < 16:
        raise ValueError(f"invalid element length {length} at offset {pos}")
    end = pos + 4 + length
    if end > len(data):
        raise ValueError(f"element at {pos} ends at {end}, payload has {len(data)} bytes")
    type_id = guid(data[pos + 4 : pos + 20])
    if type_id == "ffffffff-ffff-ffff-ffff-ffffffffffff":
        return {
            "offset": pos,
            "length": length,
            "end": end,
            "type_id": type_id,
            "type": "EndOfElements",
            "base_type": None,
        }, end
    base_type = u8(data, pos + 20)
    object_data = data[pos + 21 : end]
    item: dict[str, Any] = {
        "offset": pos,
        "length": length,
        "end": end,
        "type_id": type_id,
        "type": safe_type(type_id),
        "base_type": base_type,
        "base": BASE_NAMES.get(base_type, f"Unknown({base_type})"),
        "object_data_bytes": len(object_data),
        "object_data_hex_prefix": object_data[:24].hex(),
    }
    if len(object_data) < 4:
        item["error"] = "missing object id"
        return item, end
    object_id = i32(object_data, 0)
    item["object_id"] = object_id
    cursor = 4

    if base_type in (0, 1, 2):
        base, cursor_after = read_base_node(object_data, cursor, len(object_data))
        item["node"] = base
        cursor = cursor_after

    if type_id == "10dd1083-2ac8-11d1-9b6b-0080c7bb5997":
        read_geometric_transform(item, object_data)
    elif type_id == "10dd1030-2ac8-11d1-9b6b-0080c7bb5997":
        read_material_attribute(item, object_data)
    elif type_id in {
        # These node data classes derive from GroupNodeData in JTReader and
        # therefore all carry the same version/child-count/child-id fields
        # before their subtype-specific tail fields.
        "10dd103e-2ac8-11d1-9b6b-0080c7bb5997",  # PartitionNode
        "10dd101b-2ac8-11d1-9b6b-0080c7bb5997",  # GroupNode
        "10dd102c-2ac8-11d1-9b6b-0080c7bb5997",  # LODNode
        "10dd104c-2ac8-11d1-9b6b-0080c7bb5997",  # RangeLODNode
        "10dd10f3-2ac8-11d1-9b6b-0080c7bb5997",  # SwitchNode
        "ce357245-38fb-11d1-a506-006097bdc6e1",  # MetaDataNode
        "ce357244-38fb-11d1-a506-006097bdc6e1",  # PartNode
    }:
        if cursor + 5 <= len(object_data):
            item["version"] = u8(object_data, cursor)
            child_count = i32(object_data, cursor + 1)
            item["child_count"] = child_count
            cursor += 5
            if 0 <= child_count <= 1_000_000 and cursor + 4 * child_count <= len(object_data):
                item["children"] = [i32(object_data, cursor + 4 * n) for n in range(child_count)]
                cursor += 4 * child_count
            else:
                item["error"] = "invalid child count"
        if type_id == "10dd103e-2ac8-11d1-9b6b-0080c7bb5997":
            read_partition_tail(item, object_data, cursor)
    elif type_id == "10dd102a-2ac8-11d1-9b6b-0080c7bb5997":
        if cursor + 5 <= len(object_data):
            item["version"] = u8(object_data, cursor)
            item["instance_child"] = i32(object_data, cursor + 1)
    elif type_id in {
        "10dd106e-2ac8-11d1-9b6b-0080c7bb5997",
        "10dd102b-2ac8-11d1-9b6b-0080c7bb5997",
        "10dd1004-2ac8-11d1-9b6b-0080c7bb5997",
    }:
        read_property_atom(item, object_data)
    elif base_type == 8:
        read_late_loaded(item, object_data)

    return item, end


def parse_lsg(path: Path) -> dict[str, Any]:
    inventory = analyze(path)
    entries = [entry for entry in inventory["segments"]["entries"] if entry["type"] == 1]
    if len(entries) != 1:
        raise ValueError(f"expected one LSG segment (type 1), found {len(entries)}")
    entry = entries[0]
    data = segment_payload(path, entry)
    graph_elements: list[dict[str, Any]] = []
    property_elements: list[dict[str, Any]] = []
    pos = 0
    marker_offsets: list[int] = []
    parse_error: str | None = None

    # Sequence 1: graph nodes and attributes.
    while pos < len(data):
        try:
            item, next_pos = parse_element(data, pos)
        except ValueError as exc:
            parse_error = str(exc)
            break
        graph_elements.append(item)
        pos = next_pos
        if item["type"] == "EndOfElements":
            marker_offsets.append(item["offset"])
            break

    # Sequence 2: property atoms (strings, references, late-loaded records).
    if parse_error is None and marker_offsets:
        while pos < len(data):
            try:
                item, next_pos = parse_element(data, pos)
            except ValueError as exc:
                parse_error = str(exc)
                break
            property_elements.append(item)
            pos = next_pos
            if item["type"] == "EndOfElements":
                marker_offsets.append(item["offset"])
                break

    # Sequence 3: property table.  It starts with version:u16 and
    # element_count:u32.  Each element entry is terminated by key id 0.
    property_table: dict[str, Any] = {"version": None, "element_count": 0, "entries": []}
    if parse_error is None and len(marker_offsets) >= 2:
        table_start = pos
        if pos + 6 > len(data):
            parse_error = "truncated property table header"
        else:
            version = struct.unpack_from("<H", data, pos)[0]
            count = u32(data, pos + 2)
            property_table.update({"offset": pos, "version": version, "element_count": count})
            pos += 6
            if count > 10_000_000:
                parse_error = f"invalid property table count {count}"
            else:
                for index in range(count):
                    if pos + 4 > len(data):
                        parse_error = f"truncated property table entry {index}"
                        break
                    element_object_id = i32(data, pos)
                    pos += 4
                    pairs: list[dict[str, int]] = []
                    while True:
                        if pos + 4 > len(data):
                            parse_error = f"truncated property key {index}"
                            break
                        key_object_id = i32(data, pos)
                        pos += 4
                        if key_object_id == 0:
                            break
                        if pos + 4 > len(data):
                            parse_error = f"truncated property value {index}"
                            break
                        value_object_id = i32(data, pos)
                        pos += 4
                        pairs.append({"key": key_object_id, "value": value_object_id})
                    property_table["entries"].append({"element_object_id": element_object_id, "properties": pairs})
                    if parse_error:
                        break
                property_table["bytes"] = pos - table_start

    elements = graph_elements + property_elements

    counts = collections.Counter(item["type"] for item in elements)
    nodes = [item for item in elements if item.get("base_type") in (0, 1, 2)]
    by_id = {item["object_id"]: item for item in elements if "object_id" in item}
    references: list[dict[str, Any]] = []
    for item in elements:
        if "children" in item:
            for child in item["children"]:
                references.append({"from": item["object_id"], "to": child, "kind": "child"})
        if "instance_child" in item:
            references.append({"from": item["object_id"], "to": item["instance_child"], "kind": "instance"})
        if "reference_object_id" in item:
            references.append({"from": item["object_id"], "to": item["reference_object_id"], "kind": "property"})

    # Attach resolved property names/values without losing the raw IDs.  This
    # makes the report useful to the next type7 decoder and is still robust if
    # a future writer emits an unknown atom type.
    for item in property_elements:
        if "object_id" in item and item.get("value") is not None:
            item["value_type"] = "string" if isinstance(item["value"], str) else "scalar"
    for entry_info in property_table.get("entries", []):
        entry_info["element_type"] = by_id.get(entry_info["element_object_id"], {}).get("type")
        for prop in entry_info["properties"]:
            key_item = by_id.get(prop["key"], {})
            value_item = by_id.get(prop["value"], {})
            prop["key_text"] = key_item.get("value")
            prop["value_text"] = value_item.get("value")
            prop["value_type"] = value_item.get("type")

    shape_bindings: list[dict[str, Any]] = []
    shape_types = {"TriStripSetShapeNode", "PolylineSetShapeNode", "PolygonSetShapeNode", "PointSetShapeNode"}
    for entry_info in property_table.get("entries", []):
        if entry_info.get("element_type") not in shape_types:
            continue
        for prop in entry_info["properties"]:
            if prop.get("key_text") != "JT_LLPROP_SHAPEIMPL":
                continue
            late_item = by_id.get(prop["value"], {})
            shape_bindings.append({
                "shape_object_id": entry_info["element_object_id"],
                "shape_type": entry_info.get("element_type"),
                "late_object_id": prop["value"],
                "segment_id": late_item.get("segment_id"),
                "segment_type": late_item.get("segment_type"),
                "payload_object_id": late_item.get("payload_object_id"),
            })

    return {
        "file": str(path),
        "sha256": inventory["sha256"],
        "version": inventory["header"]["version"],
        "lsg_segment": {
            "ordinal": entry["ordinal"],
            "segment_id": entry["id"],
            "stored_bytes": entry["bytes"],
            "payload_bytes": len(data),
            "codec": entry.get("codec"),
        },
        "parse": {
            "elements": len(elements),
            "graph_elements": len(graph_elements),
            "property_elements": len(property_elements),
            "end_marker_offsets": marker_offsets,
            "parse_end": pos,
            "remaining_bytes": len(data) - pos,
            "error": parse_error,
        },
        "type_counts": dict(counts),
        "node_count": len(nodes),
        "nodes": nodes,
        "late_loaded": [item for item in elements if "segment_id" in item],
        "references": references,
        "property_table": property_table,
        "shape_bindings": shape_bindings,
        "elements": elements,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    report = parse_lsg(args.input)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({
        "version": report["version"],
        "elements": report["parse"]["elements"],
        "remaining_bytes": report["parse"]["remaining_bytes"],
        "error": report["parse"]["error"],
        "type_counts": report["type_counts"],
        "nodes": report["node_count"],
        "late_loaded": len(report["late_loaded"]),
        "shape_bindings": len(report["shape_bindings"]),
        "references": len(report["references"]),
    }, ensure_ascii=False))


if __name__ == "__main__":
    main()
