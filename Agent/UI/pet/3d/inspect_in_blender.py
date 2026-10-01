"""Inspect a user GLB in Blender without modifying the source file."""

import json
import sys
from pathlib import Path

import bpy
from mathutils import Vector


source = Path(sys.argv[sys.argv.index("--") + 1])
bpy.ops.object.select_all(action="SELECT")
bpy.ops.object.delete(use_global=False)
bpy.ops.import_scene.gltf(filepath=str(source))

objects = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
corners = [obj.matrix_world @ Vector(corner) for obj in objects for corner in obj.bound_box]
minimum = [min(v[i] for v in corners) for i in range(3)]
maximum = [max(v[i] for v in corners) for i in range(3)]
info = {
    "objects": [{"name": obj.name, "type": obj.type,
                 "polygons": len(obj.data.polygons), "vertices": len(obj.data.vertices),
                 "materials": [material.name if material else None for material in obj.data.materials],
                 "uv_layers": [layer.name for layer in obj.data.uv_layers],
                 "shape_keys": [key.name for key in obj.data.shape_keys.key_blocks] if obj.data.shape_keys else [],
                 "modifiers": [modifier.type for modifier in obj.modifiers]}
                for obj in bpy.context.scene.objects if obj.type in ("MESH", "ARMATURE")],
    "world_bounds": {"min": minimum, "max": maximum},
    "world_extent": [maximum[i] - minimum[i] for i in range(3)],
}
target = Path(__file__).resolve().parent / "blender_report.json"
target.write_text(json.dumps(info, ensure_ascii=False, indent=2), encoding="utf-8")
print("PET_INSPECT", json.dumps(info, ensure_ascii=False))
