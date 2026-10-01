"""Create a reversible static GLB candidate: remove the stray tail and reduce mesh density."""

import json
import struct
import sys
from pathlib import Path

import bpy
import numpy as np


source = Path(sys.argv[sys.argv.index("--") + 1]).resolve()
output = (Path(__file__).resolve().parent / "whale_girl_static_repaired.glb").resolve()
if source == output:
    raise ValueError("Output must not overwrite the user-provided GLB")

bpy.ops.object.select_all(action="SELECT")
bpy.ops.object.delete(use_global=False)
bpy.ops.import_scene.gltf(filepath=str(source))
obj = next(obj for obj in bpy.context.scene.objects if obj.type == "MESH")
mesh = obj.data
before_faces = len(mesh.polygons)

centers = np.empty((before_faces, 3), dtype=np.float32)
mesh.polygons.foreach_get("center", centers.reshape(-1))
matrix = np.asarray(obj.matrix_world, dtype=np.float32)
centers = centers @ matrix[:3, :3].T + matrix[:3, 3]
region = ((centers[:, 0] > 0.12) & (centers[:, 1] > 0.105) &
          (centers[:, 1] < 0.185) & (centers[:, 2] > 0.19) &
          (centers[:, 2] < 0.40))
vertex_labels = np.load(output.parent / "component_id_per_vertex.npy")
if len(vertex_labels) != len(mesh.vertices):
    raise ValueError("Component labels do not match the source GLB")
triangle_vertices = np.empty(before_faces * 3, dtype=np.int32)
mesh.polygons.foreach_get("vertices", triangle_vertices)
face_labels = vertex_labels[triangle_vertices.reshape(-1, 3)[:, 0]]
all_counts = np.bincount(face_labels)
inside_counts = np.bincount(face_labels[region], minlength=len(all_counts))
selected_components = (inside_counts / np.maximum(all_counts, 1)) > 0.50
remove_faces = selected_components[face_labels]
mesh.polygons.foreach_set("select", remove_faces)
mesh.update()

bpy.ops.object.select_all(action="DESELECT")
obj.select_set(True)
bpy.context.view_layer.objects.active = obj
bpy.context.tool_settings.mesh_select_mode = (False, False, True)
bpy.ops.object.mode_set(mode="EDIT")
bpy.ops.mesh.delete(type="FACE")
bpy.ops.object.mode_set(mode="OBJECT")
after_tail_faces = len(obj.data.polygons)

modifier = obj.modifiers.new("Reduce reconstruction density", "DECIMATE")
modifier.ratio = 0.30
bpy.ops.object.modifier_apply(modifier=modifier.name)
after_decimation_faces = len(obj.data.polygons)

material = obj.data.materials[0]
if material and material.use_nodes:
    for node in material.node_tree.nodes:
        if node.type == "BSDF_PRINCIPLED":
            node.inputs["Roughness"].default_value = 0.65
            if "Specular IOR Level" in node.inputs:
                node.inputs["Specular IOR Level"].default_value = 0.3

bpy.ops.export_scene.gltf(filepath=str(output), export_format="GLB",
                          export_apply=True, export_animations=False,
                          export_materials="EXPORT")
# The source extension multiplies specular color by 2. Keep its PBR range
# conservative in the standalone deliverable without changing the texture.
data = output.read_bytes()
json_size, json_kind = struct.unpack_from("<I4s", data, 12)
if json_kind != b"JSON":
    raise ValueError("Exported GLB lacks a JSON chunk")
document = json.loads(data[20:20 + json_size])
for item in document.get("materials", []):
    settings = item.get("extensions", {}).get("KHR_materials_specular")
    if settings:
        settings["specularFactor"] = 0.35
        settings["specularColorFactor"] = [1.0, 1.0, 1.0]
encoded = json.dumps(document, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
encoded += b" " * (-len(encoded) % 4)
binary_chunk = data[20 + json_size:]
output.write_bytes(struct.pack("<4sII", b"glTF", 2, 20 + len(encoded) + len(binary_chunk)) +
                   struct.pack("<I4s", len(encoded), b"JSON") + encoded + binary_chunk)
report = {"source": str(source), "output": str(output),
          "faces_before": before_faces,
          "tail_components_removed": int(selected_components.sum()),
          "tail_faces_selected": int(remove_faces.sum()),
          "faces_after_tail": after_tail_faces,
          "faces_after_decimation": after_decimation_faces,
          "output_bytes": output.stat().st_size}
(output.parent / "repair_report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print("PET_REPAIR", json.dumps(report), flush=True)
