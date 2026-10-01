"""Highlight a candidate volume containing the duplicated side tail."""

import sys
from pathlib import Path

import bpy
import numpy as np
from mathutils import Vector


source = Path(sys.argv[sys.argv.index("--") + 1])
output = Path(__file__).resolve().parent
bpy.ops.object.select_all(action="SELECT")
bpy.ops.object.delete(use_global=False)
bpy.ops.import_scene.gltf(filepath=str(source))
scene = bpy.context.scene
obj = next(obj for obj in scene.objects if obj.type == "MESH")
mesh = obj.data
red = bpy.data.materials.new("candidate side-tail region")
red.diffuse_color = (1, 0, 0, 1)
red.use_nodes = True
principled = next(node for node in red.node_tree.nodes if node.type == "BSDF_PRINCIPLED")
principled.inputs["Base Color"].default_value = (1, 0, 0, 1)
mesh.materials.append(red)

centers = np.empty((len(mesh.polygons), 3), dtype=np.float32)
mesh.polygons.foreach_get("center", centers.reshape(-1))
matrix = np.asarray(obj.matrix_world, dtype=np.float32)
centers = centers @ matrix[:3, :3].T + matrix[:3, 3]
mask = ((centers[:, 0] > 0.12) & (centers[:, 1] > 0.105) &
        (centers[:, 1] < 0.185) & (centers[:, 2] > 0.19) &
        (centers[:, 2] < 0.40))
vertex_labels = np.load(output / "component_id_per_vertex.npy")
triangle_vertices = np.empty(len(mesh.polygons) * 3, dtype=np.int32)
mesh.polygons.foreach_get("vertices", triangle_vertices)
face_labels = vertex_labels[triangle_vertices.reshape(-1, 3)[:, 0]]
all_counts = np.bincount(face_labels)
inside_counts = np.bincount(face_labels[mask], minlength=len(all_counts))
selected = (inside_counts / np.maximum(all_counts, 1)) > 0.50
full_mask = selected[face_labels]
mesh.polygons.foreach_set("material_index", full_mask.astype(np.int32))
print("TAIL_PROBE", int(mask.sum()), "selected components", int(selected.sum()),
      "selected faces", int(full_mask.sum()), flush=True)

scene.render.engine = "CYCLES"
scene.cycles.samples = 4
scene.render.resolution_x = 600
scene.render.resolution_y = 900
scene.render.image_settings.file_format = "PNG"
scene.view_settings.view_transform = "Standard"
world = bpy.data.worlds.new("world")
scene.world = world
world.use_nodes = True
world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.8
camera_data = bpy.data.cameras.new("camera")
camera = bpy.data.objects.new("camera", camera_data)
scene.collection.objects.link(camera)
scene.camera = camera
camera_data.type = "ORTHO"
camera_data.ortho_scale = 1.31
for name, position in (("back", (0.03, 2, 0.57)), ("side", (2, 0.04, 0.57))):
    camera.location = position
    camera.rotation_euler = (Vector((0.03, 0.04, 0.57)) - camera.location).to_track_quat("-Z", "Y").to_euler()
    scene.render.filepath = str(output / f"tail_probe_{name}.png")
    bpy.ops.render.render(write_still=True)
