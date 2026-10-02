"""Build an instance-to-shape inventory and composed JT transforms.

The LSG graph stores geometry in local coordinates below Part/RangeLOD/Group
nodes.  InstanceNode attributes carry GeometricTransformAttribute matrices.
This report walks the graph from the PartitionNode, composes row-vector JT
matrices, and resolves each TriStrip shape to its type-7 segment ordinal.
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
from typing import Any
from jt_material_resolve import resolve_diffuse


IDENTITY = [
    1.0, 0.0, 0.0, 0.0,
    0.0, 1.0, 0.0, 0.0,
    0.0, 0.0, 1.0, 0.0,
    0.0, 0.0, 0.0, 1.0,
]


def mat_mul(a: list[float], b: list[float]) -> list[float]:
    """Multiply row-major 4x4 matrices for row-vector points (p * M)."""
    return [
        sum(a[r * 4 + k] * b[k * 4 + c] for k in range(4))
        for r in range(4)
        for c in range(4)
    ]


def finite_matrix(matrix: list[float]) -> bool:
    return len(matrix) == 16 and all(math.isfinite(float(v)) for v in matrix)


def transform_point(point: list[float], matrix: list[float]) -> list[float]:
    x, y, z = point
    return [
        x * matrix[0] + y * matrix[4] + z * matrix[8] + matrix[12],
        x * matrix[1] + y * matrix[5] + z * matrix[9] + matrix[13],
        x * matrix[2] + y * matrix[6] + z * matrix[10] + matrix[14],
    ]


def determinant3(matrix: list[float]) -> float:
    a, b, c = matrix[0], matrix[1], matrix[2]
    d, e, f = matrix[4], matrix[5], matrix[6]
    g, h, i = matrix[8], matrix[9], matrix[10]
    return a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g)


def load_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def build_report(lsg: dict[str, Any], shape_inventory: dict[str, Any]) -> dict[str, Any]:
    elements = [e for e in lsg["elements"] if "object_id" in e]
    by_id = {int(e["object_id"]): e for e in elements}
    adjacency: dict[int, list[int]] = {}
    for ref in lsg.get("references", []):
        if ref.get("kind") in {"child", "instance"}:
            adjacency.setdefault(int(ref["from"]), []).append(int(ref["to"]))

    segment_to_ordinal: dict[str, int] = {}
    for segment in shape_inventory.get("segments", []):
        if segment.get("segment_type") == 7:
            segment_to_ordinal[str(segment["segment_id"])] = int(segment["ordinal"])
    shape_to_binding = {
        int(binding["shape_object_id"]): binding
        for binding in lsg.get("shape_bindings", [])
        if binding.get("shape_type") == "TriStripSetShapeNode"
    }

    instances = [e for e in elements if e.get("type") == "InstanceNode"]
    transforms = {
        int(e["object_id"]): e
        for e in elements
        if e.get("type") == "GeometricTransformAttribute"
    }
    materials = {
        int(e["object_id"]): e
        for e in elements
        if e.get("type") == "MaterialAttribute"
    }
    instance_info: dict[int, dict[str, Any]] = {}
    for instance in instances:
        ids = [int(v) for v in instance.get("node", {}).get("attribute_ids", [])]
        transform_id = next((v for v in ids if v in transforms), None)
        matrix = transforms.get(transform_id, {}).get("matrix") if transform_id is not None else None
        instance_info[int(instance["object_id"])] = {
            "instance_object_id": int(instance["object_id"]),
            "instance_child": instance.get("instance_child"),
            "transform_attribute_id": transform_id,
            "matrix_storage": transforms.get(transform_id, {}).get("matrix_storage") if transform_id is not None else None,
            "matrix": matrix,
        }

    bindings: list[dict[str, Any]] = []
    visited_states: set[tuple[int, tuple[int, ...]]] = set()

    def walk(node_id: int, composed: list[float], instance_chain: list[int], path: list[int]) -> None:
        state = (node_id, tuple(instance_chain))
        if state in visited_states:
            return
        visited_states.add(state)
        item = by_id.get(node_id)
        if item is None:
            return
        item_type = item.get("type")
        next_composed = composed
        next_chain = instance_chain
        if item_type == "InstanceNode":
            info = instance_info[node_id]
            matrix = info.get("matrix")
            if matrix is not None and finite_matrix(matrix):
                # JT stores transforms for row-vector points.  Traversal is
                # parent -> child, so a child local transform is applied
                # first and the parent transform last: M_child * M_parent.
                next_composed = mat_mul([float(v) for v in matrix], composed)
            next_chain = instance_chain + [node_id]
            child = item.get("instance_child")
            if child is not None:
                walk(int(child), next_composed, next_chain, path + [node_id])
            return
        if item_type == "TriStripSetShapeNode":
            shape_id = node_id
            binding = shape_to_binding.get(shape_id, {})
            segment_id = binding.get("segment_id")
            bindings.append({
                "shape_object_id": shape_id,
                "shape_path": path + [shape_id],
                "instance_object_ids": next_chain,
                "transform_depth": len(next_chain),
                "transform_matrix": next_composed,
                "transform_determinant3": determinant3(next_composed),
                "translation": next_composed[12:15],
                "segment_id": segment_id,
                "shape_ordinal": segment_to_ordinal.get(str(segment_id)) if segment_id is not None else None,
            })
            part_id = next(
                (candidate for candidate in path if by_id.get(candidate, {}).get("type") == "PartNode"),
                None,
            )
            if part_id is not None:
                part = by_id[part_id]
                material_id = next(
                    (int(attribute_id) for attribute_id in part.get("node", {}).get("attribute_ids", []) if int(attribute_id) in materials),
                    None,
                )
                bindings[-1]["part_object_id"] = part_id
                bindings[-1]["material_attribute_id"] = material_id
                bindings[-1]["material"] = materials.get(material_id, {}).get("diffuse_color") if material_id is not None else None
            bindings[-1]['part_material'] = bindings[-1].get('material')
            bindings[-1]['part_material_attribute_id'] = bindings[-1].get('material_attribute_id')
            resolved_material = resolve_diffuse(path + [shape_id], by_id, materials)
            bindings[-1]['material'] = resolved_material['rgba']
            bindings[-1]['material_attribute_id'] = resolved_material['rgb_material_id']
            bindings[-1]['material_resolution'] = resolved_material
            return
        for child in adjacency.get(node_id, []):
            walk(child, next_composed, next_chain, path + [node_id])

    partition = next((e for e in elements if e.get("type") == "PartitionNode"), None)
    if partition is not None:
        walk(int(partition["object_id"]), IDENTITY, [], [])

    finite_count = sum(finite_matrix(b["transform_matrix"]) for b in bindings)
    resolved = [b for b in bindings if b.get("shape_ordinal") is not None]
    unique_ordinals = sorted({int(b["shape_ordinal"]) for b in resolved})
    unique_shapes = sorted({int(b["shape_object_id"]) for b in bindings})
    return {
        "lsg_file": lsg.get("file"),
        "shape_inventory_file": shape_inventory.get("file"),
        "summary": {
            "instance_count": len(instances),
            "transform_attribute_count": len(transforms),
            "graph_shape_bindings": len(bindings),
            "resolved_shape_bindings": len(resolved),
            "unresolved_shape_bindings": len(bindings) - len(resolved),
            "unique_shape_nodes": len(unique_shapes),
            "unique_shape_ordinals": len(unique_ordinals),
            "finite_transform_bindings": finite_count,
            "non_finite_transform_bindings": len(bindings) - finite_count,
            "negative_determinant_bindings": sum(b["transform_determinant3"] < 0 for b in bindings),
            "zero_determinant_bindings": sum(abs(b["transform_determinant3"]) < 1e-12 for b in bindings),
            "material_bindings": sum(b.get("material_attribute_id") is not None for b in bindings),
            "unique_material_attributes": len({b["material_attribute_id"] for b in bindings if b.get("material_attribute_id") is not None}),
            "material_attribute_count": len(materials),
            "unbound_material_bindings": sum(b.get("material_attribute_id") is None for b in bindings),
        },
        "instances": list(instance_info.values()),
        "bindings": bindings,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("lsg", type=Path)
    parser.add_argument("shape_inventory", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    report = build_report(load_json(args.lsg), load_json(args.shape_inventory))
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report["summary"], ensure_ascii=False))


if __name__ == "__main__":
    main()
