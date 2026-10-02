"""Decode one JT 10.x TriStrip shape into auditable vertex attributes.

This stage intentionally stops before the dual-mesh topology traversal.  It
does decode all topology symbol vectors and the binary coordinate/normal/
texture arrays, so the next stage can connect them to a CGR face list without
revisiting the byte-level framing.
"""
from __future__ import annotations

import argparse
import json
import math
import struct
from pathlib import Path

from jt_codec_probe import decode_int32cdp3, tri_body, _unpack_residuals


def _decode_vector(data: bytes, pos: int, predictor: str, unsigned: bool = False) -> tuple[list[int], int, dict]:
    residuals, pos, meta = decode_int32cdp3(data, pos)
    if unsigned:
        residuals = [value & 0xFFFFFFFF for value in residuals]
        values: list[int] = []
        for index, value in enumerate(residuals):
            if index < 4 or predictor == "null":
                values.append(value)
            elif predictor == "lag1":
                values.append((value + values[index - 1]) & 0xFFFFFFFF)
            else:
                raise ValueError(predictor)
        return values, pos, meta
    return _unpack_residuals(residuals, predictor), pos, meta


def _bits_to_float(values: list[int]) -> list[float]:
    return [struct.unpack("<f", struct.pack("<I", value & 0xFFFFFFFF))[0] for value in values]


def _finite_summary(values: list[float]) -> dict:
    finite = [value for value in values if math.isfinite(value)]
    return {
        "count": len(values),
        "finite": len(finite),
        "min": min(finite) if finite else None,
        "max": max(finite) if finite else None,
        "first": values[:5],
    }


def decode_topology(body: bytes, pos: int = 41) -> tuple[dict, int]:
    report: dict = {"face_degrees": [], "face_attribute_masks": []}
    for i in range(8):
        values, pos, meta = _decode_vector(body, pos, "null")
        report["face_degrees"].append({"index": i, "meta": meta, "count": len(values), "first": values[:8]})
    for name, predictor in (("vertex_valences", "null"), ("vertex_groups", "null"), ("vertex_flags", "lag1")):
        values, pos, meta = _decode_vector(body, pos, predictor)
        report[name] = {"meta": meta, "count": len(values), "min": min(values) if values else None, "max": max(values) if values else None, "first": values[:8]}
    for i in range(8):
        values, pos, meta = _decode_vector(body, pos, "null", unsigned=True)
        report["face_attribute_masks"].append({"index": i, "meta": meta, "count": len(values), "first": values[:8]})
    values, pos, meta = _decode_vector(body, pos, "null", unsigned=True)
    report["face_attribute_masks_next"] = {"meta": meta, "count": len(values), "first": values[:8]}
    high_count = struct.unpack_from("<I", body, pos)[0]
    pos += 4
    report["high_degree_masks"] = [struct.unpack_from("<I", body, pos + i * 4)[0] for i in range(high_count)]
    pos += high_count * 4
    values, pos, meta = _decode_vector(body, pos, "lag1")
    report["split_face_syms"] = {"meta": meta, "count": len(values), "first": values[:8]}
    values, pos, meta = _decode_vector(body, pos, "null")
    report["split_face_positions"] = {"meta": meta, "count": len(values), "first": values[:8]}
    report["composite_hash_u32"] = struct.unpack_from("<I", body, pos)[0]
    pos += 4
    return report, pos


def decode_shape(body: bytes) -> dict:
    topology, pos = decode_topology(body)
    vertex_bindings = struct.unpack_from("<Q", body, pos)[0]
    pos += 8
    quantization_parameters = list(body[pos : pos + 4])
    pos += 4
    topological_vertices = struct.unpack_from("<i", body, pos)[0]
    pos += 4
    vertex_attributes = struct.unpack_from("<i", body, pos)[0]
    pos += 4
    arrays: dict = {}

    if vertex_bindings & 0x7:
        unique_count = struct.unpack_from("<i", body, pos)[0]
        components = body[pos + 4]
        pos += 5
        quantizers = []
        for _ in range(components):
            minimum, maximum = struct.unpack_from("<ff", body, pos)
            bits = body[pos + 8]
            quantizers.append({"minimum": minimum, "maximum": maximum, "bits": bits})
            pos += 9
        component_values: list[list[float]] = []
        packet_meta = []
        for component in range(components):
            values, pos, meta = _decode_vector(body, pos, "lag1", unsigned=True)
            component_values.append(_bits_to_float(values))
            packet_meta.append(meta)
        value_hash = struct.unpack_from("<I", body, pos)[0]
        pos += 4
        arrays["coordinates"] = {
            "unique_count": unique_count,
            "components": components,
            "quantizers": quantizers,
            "packets": packet_meta,
            "hash_u32": value_hash,
            "component_summary": [_finite_summary(values) for values in component_values],
            "first_vertices": [list(vertex) for vertex in zip(*component_values)][:5],
        }

    if vertex_bindings & 0x8:
        normal_count = struct.unpack_from("<i", body, pos)[0]
        components = body[pos + 4]
        quantization_bits = body[pos + 5]
        pos += 6
        if quantization_bits:
            raise ValueError("Deering normal decoding is not yet enabled")
        component_values: list[list[float]] = []
        packet_meta = []
        for component in range(components):
            values, pos, meta = _decode_vector(body, pos, "null", unsigned=True)
            component_values.append(_bits_to_float(values))
            packet_meta.append(meta)
        value_hash = struct.unpack_from("<I", body, pos)[0]
        pos += 4
        arrays["normals"] = {
            "count": normal_count,
            "components": components,
            "quantization_bits": quantization_bits,
            "packets": packet_meta,
            "hash_u32": value_hash,
            "component_summary": [_finite_summary(values) for values in component_values],
            "first_normals": [list(vertex) for vertex in zip(*component_values)][:5],
        }

    for texture_index in range(8):
        if not (vertex_bindings & (1 << (8 + texture_index))):
            continue
        texture_count = struct.unpack_from("<i", body, pos)[0]
        components = body[pos + 4]
        quantization_bits = body[pos + 5]
        pos += 6
        if quantization_bits:
            raise ValueError("quantized texture decoding is not yet enabled")
        component_values: list[list[float]] = []
        packet_meta = []
        for component in range(components):
            values, pos, meta = _decode_vector(body, pos, "null", unsigned=True)
            component_values.append(_bits_to_float(values))
            packet_meta.append(meta)
        value_hash = struct.unpack_from("<I", body, pos)[0]
        pos += 4
        arrays[f"texture_{texture_index}"] = {
            "count": texture_count,
            "components": components,
            "quantization_bits": quantization_bits,
            "packets": packet_meta,
            "hash_u32": value_hash,
            "component_summary": [_finite_summary(values) for values in component_values],
            "first_values": [list(vertex) for vertex in zip(*component_values)][:5],
        }

    return {
        "body_bytes": len(body),
        "topology": topology,
        "vertex_bindings_u64": vertex_bindings,
        "quantization_parameters": quantization_parameters,
        "topological_vertices": topological_vertices,
        "vertex_attributes": vertex_attributes,
        "arrays": arrays,
        "end_offset": pos,
        "trailing_bytes": len(body) - pos,
    }


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("input", type=Path)
    ap.add_argument("output", type=Path, nargs="?")
    ap.add_argument("--ordinal", type=int, default=1)
    args = ap.parse_args()
    report = decode_shape(tri_body(args.input, args.ordinal))
    text = json.dumps(report, ensure_ascii=False, indent=2)
    if args.output:
        args.output.write_text(text + "\n", encoding="utf-8")
    else:
        print(text)


if __name__ == "__main__":
    main()
