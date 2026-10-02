"""Decode every TriStrip Shape LOD in one JT file into per-shape MeshIR JSON.

The batch stage deliberately keeps each shape local.  LSG instance transforms
are applied later, after this geometry inventory is complete, so a failed
shape cannot silently corrupt the assembled model.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

from jt_analyze import analyze
from jt_lsg import parse_element
from jt_to_mesh import decode_mesh

TRI_GUID = "10dd10ab-2ac8-11d1-9b6b-0080c7bb5997"


def tri_shapes(path: Path) -> list[tuple[int, bytes]]:
    inv = analyze(path)
    raw = path.read_bytes()
    result: list[tuple[int, bytes]] = []
    for entry in inv["segments"]["entries"]:
        if int(entry["type"]) != 7:
            continue
        start = int(entry["offset"]) + 24
        data = raw[start : start + int(entry["bytes"])]
        element, end = parse_element(data, 0)
        if element["type_id"] != TRI_GUID:
            continue
        result.append((int(entry["ordinal"]), data[25:end]))
    return result


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("input", type=Path)
    ap.add_argument("output", type=Path, help="manifest JSON")
    ap.add_argument("--mesh-dir", type=Path, help="directory for per-shape MeshIR JSON")
    args = ap.parse_args()

    mesh_dir = args.mesh_dir
    if mesh_dir:
        mesh_dir.mkdir(parents=True, exist_ok=True)
    reports: list[dict] = []
    errors: list[dict] = []
    total_vertices = 0
    total_faces = 0
    area_min = None
    area_max = None
    for ordinal, body in tri_shapes(args.input):
        try:
            report = decode_mesh(body, f"JT TriStrip ordinal {ordinal}")
            stats = report["stats"]
            total_vertices += len(report["vertices"])
            total_faces += len(report["faces"])
            area_min = stats["area_min"] if area_min is None else min(area_min, stats["area_min"])
            area_max = stats["area_max"] if area_max is None else max(area_max, stats["area_max"])
            item = {"ordinal": ordinal, "status": "ok", "stats": stats}
            if mesh_dir:
                mesh_path = mesh_dir / f"shape_{ordinal:04d}.json"
                # Keep MeshIR focused on geometry.  Corner attributes are
                # written by jt_batch_attributes.py into a parallel inventory.
                mesh_output = dict(report)
                for key in ("attributes", "corner_normals", "corner_normal_indices", "texture_coordinates"):
                    mesh_output.pop(key, None)
                mesh_path.write_text(json.dumps(mesh_output, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
                item["mesh_file"] = str(mesh_path)
            reports.append(item)
        except Exception as exc:  # keep the batch auditable and continue
            item = {"ordinal": ordinal, "status": "error", "error": f"{type(exc).__name__}: {exc}"}
            reports.append(item)
            errors.append(item)

    summary = {
        "tri_strip_count": len(reports),
        "decoded_count": len(reports) - len(errors),
        "error_count": len(errors),
        "total_coordinate_vertices": total_vertices,
        "total_triangles": total_faces,
        "area_min": area_min,
        "area_max": area_max,
        "error_ordinals": [item["ordinal"] for item in errors],
    }
    manifest = {"input": str(args.input), "summary": summary, "shapes": reports}
    args.output.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, sort_keys=True))


if __name__ == "__main__":
    main()
