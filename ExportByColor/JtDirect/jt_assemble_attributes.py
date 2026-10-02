"""Compose per-shape JT attributes into the world-space MeshIR order."""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
from typing import Any


def transform_normal(normal: list[float], matrix: list[float]) -> list[float]:
    x, y, z = normal
    value = [
        x * matrix[0] + y * matrix[4] + z * matrix[8],
        x * matrix[1] + y * matrix[5] + z * matrix[9],
        x * matrix[2] + y * matrix[6] + z * matrix[10],
    ]
    length = math.sqrt(sum(component * component for component in value))
    if length <= 1e-12 or not math.isfinite(length):
        return value
    return [component / length for component in value]


def normalize(value: list[float]) -> list[float]:
    length = math.sqrt(sum(float(component) ** 2 for component in value))
    if length <= 1e-12 or not math.isfinite(length):
        return [0.0, 0.0, 0.0]
    return [float(component) / length for component in value]


def geometric_normal(a: list[float], b: list[float], c: list[float]) -> list[float]:
    ux, uy, uz = (float(b[i]) - float(a[i]) for i in range(3))
    vx, vy, vz = (float(c[i]) - float(a[i]) for i in range(3))
    return normalize([uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx])


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("instance_inventory", type=Path)
    parser.add_argument("attribute_manifest", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--mesh", type=Path, help="world-space MeshIR for face-normal orientation")
    args = parser.parse_args()
    inventory = json.loads(args.instance_inventory.read_text(encoding="utf-8"))
    manifest = json.loads(args.attribute_manifest.read_text(encoding="utf-8"))
    mesh = json.loads(args.mesh.read_text(encoding="utf-8")) if args.mesh else None
    attribute_items = {
        int(item["ordinal"]): item
        for item in manifest["shapes"]
        if item.get("status") == "ok"
    }
    world_normals: list[list[float]] = []
    world_corner_indices: list[list[int]] = []
    blocks: list[dict[str, Any]] = []
    normal_offset = 0
    face_offset = 0
    referenced = 0
    missing = 0
    for binding in sorted(inventory["bindings"], key=lambda item: int(item["shape_ordinal"])):
        ordinal = int(binding["shape_ordinal"])
        item = attribute_items[ordinal]
        attr_path = Path(item["attribute_file"])
        attrs = json.loads(attr_path.read_text(encoding="utf-8"))
        matrix = [float(value) for value in binding["transform_matrix"]]
        local_normals = attrs.get("normals") or []
        world_normals.extend(transform_normal(normal, matrix) for normal in local_normals)
        local_indices = attrs.get("corner_normal_indices") or []
        remapped: list[list[int]] = []
        for indices in local_indices:
            row: list[int] = []
            for index in indices:
                if index < 0:
                    row.append(-1)
                    missing += 1
                else:
                    row.append(normal_offset + index)
                    referenced += 1
            remapped.append(row)
        world_corner_indices.extend(remapped)
        blocks.append({
            "shape_ordinal": ordinal,
            "shape_object_id": binding["shape_object_id"],
            "instance_object_ids": binding["instance_object_ids"],
            "transform_matrix": matrix,
            "normal_offset": normal_offset,
            "normal_count": len(local_normals),
            "face_offset": face_offset,
            "face_count": len(remapped),
            "texture_coordinates": attrs.get("texture_coordinates", {}),
        })
        normal_offset += len(local_normals)
        face_offset += len(remapped)

    # Build one optional normal per output face.  JT corner normals are often
    # sparse; average the referenced corners and leave an all-zero sentinel
    # when no explicit normal exists so the CGR writer can derive a geometric
    # face normal.  If vertices are available, orient the average with the
    # source triangle to avoid a display-only normal inversion.
    face_normals: list[list[float]] = []
    for face_index, row in enumerate(world_corner_indices):
        values = [
            world_normals[index]
            for index in row
            if index >= 0 and index < len(world_normals)
        ]
        if not values:
            face_normals.append([0.0, 0.0, 0.0])
            continue
        average = normalize([
            sum(value[axis] for value in values)
            for axis in range(3)
        ])
        if mesh and face_index < len(mesh.get("faces", [])):
            ids = mesh["faces"][face_index]
            geo = geometric_normal(
                mesh["vertices"][ids[0]],
                mesh["vertices"][ids[1]],
                mesh["vertices"][ids[2]],
            )
            if sum(average[i] * geo[i] for i in range(3)) < 0.0:
                average = [-component for component in average]
        face_normals.append(average)

    output = {
        "source": "JT TriStrip corner attributes composed through LSG InstanceNode transforms",
        "summary": {
            "shape_count": len(blocks),
            "normal_count": len(world_normals),
            "face_count": len(world_corner_indices),
            "referenced_normal_corners": referenced,
            "missing_normal_corners": missing,
            "texture_shape_count": sum(bool(block["texture_coordinates"]) for block in blocks),
            "explicit_face_normals": sum(
                any(abs(component) > 1e-12 for component in normal)
                for normal in face_normals
            ),
            "geometric_face_normals": sum(
                not any(abs(component) > 1e-12 for component in normal)
                for normal in face_normals
            ),
        },
        "normals": world_normals,
        "corner_normal_indices": world_corner_indices,
        "face_normals": face_normals,
        "blocks": blocks,
    }
    args.output.write_text(json.dumps(output, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
    print(json.dumps(output["summary"], ensure_ascii=False, sort_keys=True))


if __name__ == "__main__":
    main()
