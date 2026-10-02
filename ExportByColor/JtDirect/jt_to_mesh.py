"""Close the first JT TriStrip through topology decoding into a triangle mesh JSON.

The dual VF mesh has one dual vertex per source triangle and one dual face per
source coordinate.  Its three incident dual faces therefore become the three
coordinate indices of a source triangle.
"""
from __future__ import annotations

import argparse
import json
import math
import struct
from pathlib import Path

from jt_codec_probe import tri_body
from jt_shape_decode import _bits_to_float, _decode_vector, decode_topology
from jt_topology_decode import Decoder, read_symbols


def decode_attributes(body: bytes, pos: int) -> tuple[list[list[float]], int, dict]:
    bindings = struct.unpack_from("<Q", body, pos)[0]
    pos += 8
    if bindings & 0x30:
        raise ValueError('Vertex colors are not yet supported; refusing to discard them')
    pos += 4  # quantization parameters
    topological_vertices = struct.unpack_from("<i", body, pos)[0]
    pos += 4
    vertex_attributes = struct.unpack_from("<i", body, pos)[0]
    pos += 4
    if not (bindings & 0x7):
        raise ValueError("shape has no coordinate binding")

    unique_count = struct.unpack_from("<i", body, pos)[0]
    components = body[pos + 4]
    pos += 5
    for _ in range(components):
        if body[pos + 8] != 0:
            raise ValueError('Quantized coordinates are not supported by the float-array decoder')
        pos += 9
    component_values: list[list[float]] = []
    for _ in range(components):
        values, pos, _ = _decode_vector(body, pos, "lag1", unsigned=True)
        component_values.append(_bits_to_float(values))
    pos += 4  # coordinate hash
    if unique_count != topological_vertices or components != 3:
        raise ValueError(f"unexpected coordinate array: count={unique_count} topo={topological_vertices} components={components}")
    vertices = [list(values) for values in zip(*component_values)]
    if len(vertices) != unique_count:
        raise ValueError("coordinate count mismatch")

    attributes: dict = {
        "vertex_bindings_u64": bindings,
        "topological_vertices": topological_vertices,
        "vertex_attributes": vertex_attributes,
        "normals": None,
        "normal_count": 0,
        "normal_quantization_bits": None,
        "texture_coordinates": {},
    }

    if bindings & 0x8:
        normal_count = struct.unpack_from("<i", body, pos)[0]
        normal_components = body[pos + 4]
        normal_quantization_bits = body[pos + 5]
        pos += 6
        if normal_quantization_bits:
            raise ValueError(f"Deering normal decoding is not yet enabled (bits={normal_quantization_bits})")
        normal_components_values: list[list[float]] = []
        for _ in range(normal_components):
            values, pos, _ = _decode_vector(body, pos, "null", unsigned=True)
            normal_components_values.append(_bits_to_float(values))
        pos += 4  # normal hash
        if normal_components != 3 or any(len(values) != normal_count for values in normal_components_values):
            raise ValueError("unexpected normal array")
        attributes["normals"] = [list(normal) for normal in zip(*normal_components_values)]
        attributes["normal_count"] = normal_count
        attributes["normal_quantization_bits"] = normal_quantization_bits

    for texture_index in range(8):
        if not (bindings & (1 << (8 + texture_index))):
            continue
        texture_count = struct.unpack_from("<i", body, pos)[0]
        texture_components = body[pos + 4]
        texture_quantization_bits = body[pos + 5]
        pos += 6
        if texture_quantization_bits:
            raise ValueError(f"quantized texture decoding is not yet enabled (index={texture_index})")
        component_values: list[list[float]] = []
        for _ in range(texture_components):
            values, pos, _ = _decode_vector(body, pos, "null", unsigned=True)
            component_values.append(_bits_to_float(values))
        pos += 4  # texture hash
        if any(len(values) != texture_count for values in component_values):
            raise ValueError("unexpected texture-coordinate array")
        attributes["texture_coordinates"][str(texture_index)] = [
            list(value) for value in zip(*component_values)
        ]

    return vertices, pos, attributes


def decode_mesh(body: bytes, source: str = "JT TriStrip") -> dict:
    _, topo_pos = decode_topology(body)
    vertices, _, attributes = decode_attributes(body, topo_pos)
    decoder = Decoder(read_symbols(body))
    decoder.run()
    mesh = decoder.mesh
    if len(mesh.faces) != len(vertices):
        raise ValueError(f"dual face/coordinate mismatch: faces={len(mesh.faces)} coords={len(vertices)}")

    faces: list[list[int]] = []
    corner_normal_indices: list[list[int]] = []
    corner_texture_indices: dict[str, list[list[int]]] = {
        index: [] for index in attributes["texture_coordinates"]
    }
    areas: list[float] = []
    degenerate_triangles = 0
    polygon_faces = 0
    max_source_face_degree = 0
    for source_face_index, source_face in enumerate(mesh.vertices):
        ring = source_face.faces
        max_source_face_degree = max(max_source_face_degree, len(ring))
        if len(ring) < 3 or any(index < 0 or index >= len(vertices) for index in ring):
            raise ValueError("invalid dual-face reference")
        if len(ring) != 3:
            polygon_faces += 1
        corner_attributes = [
            mesh.face_attribute(face_id, source_face_index)
            for face_id in ring
        ]
        # Most JT TriStrip source faces have degree three.  A small number of
        # files carry polygonal rings in the same shape element; fan them here
        # and retain the count in stats so the caller can apply a policy.
        for index in range(1, len(ring) - 1):
            a, b, c = ring[0], ring[index], ring[index + 1]
            pa, pb, pc = vertices[a], vertices[b], vertices[c]
            ux, uy, uz = (pb[i] - pa[i] for i in range(3))
            vx, vy, vz = (pc[i] - pa[i] for i in range(3))
            nx, ny, nz = (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx)
            area2 = math.sqrt(nx * nx + ny * ny + nz * nz)
            if not math.isfinite(area2) or area2 <= 1e-12:
                degenerate_triangles += 1
                continue
            areas.append(area2 * 0.5)
            tri_attributes = [corner_attributes[0], corner_attributes[index], corner_attributes[index + 1]]
            faces.append([a, b, c])
            corner_normal_indices.append(tri_attributes)
            for texture_index in corner_texture_indices:
                corner_texture_indices[texture_index].append(list(tri_attributes))

    normals = attributes.get("normals")
    corner_normals: list[list[list[float]] | None] = []
    invalid_normal_indices = 0
    missing_normal_corners = 0
    if normals is not None:
        for indices in corner_normal_indices:
            values: list[list[float] | None] = []
            for index in indices:
                if index < 0:
                    missing_normal_corners += 1
                    values.append(None)
                elif index >= len(normals):
                    invalid_normal_indices += 1
                    values.append(None)
                else:
                    values.append(normals[index])
            corner_normals.append(values)

    return {
        "vertices": vertices,
        "faces": faces,
        "source": source,
        "attributes": {
            "normals": normals,
            "normal_quantization_bits": attributes.get("normal_quantization_bits"),
            "texture_coordinates": attributes["texture_coordinates"],
        },
        "corner_normals": corner_normals,
        "corner_normal_indices": corner_normal_indices,
        "texture_coordinates": attributes["texture_coordinates"],
        "stats": {
            "dual_vertices": len(mesh.vertices),
            "dual_faces": len(mesh.faces),
            "source_polygon_faces": polygon_faces,
            "max_source_face_degree": max_source_face_degree,
            "triangles": len(faces),
            "degenerate_triangles": degenerate_triangles,
            "area_min": min(areas) if areas else None,
            "area_max": max(areas) if areas else None,
            "area_sum": sum(areas),
            "normal_count": attributes.get("normal_count", 0),
            "normal_referenced_corners": sum(sum(index >= 0 for index in indices) for indices in corner_normal_indices),
            "normal_complete_triangles": sum(1 for indices in corner_normal_indices if all(index >= 0 for index in indices)),
            "missing_normal_corners": missing_normal_corners,
            "invalid_normal_indices": invalid_normal_indices,
        },
    }


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("input", type=Path)
    ap.add_argument("output", type=Path)
    ap.add_argument("--ordinal", type=int, default=1)
    args = ap.parse_args()
    mesh = decode_mesh(tri_body(args.input, args.ordinal))
    args.output.write_text(json.dumps(mesh, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
    print(json.dumps(mesh["stats"], ensure_ascii=False, sort_keys=True))


if __name__ == "__main__":
    main()
