"""Compare GLB face UV color against the mesh surface at high magnification."""

import sys
from pathlib import Path

import bpy
from mathutils import Vector


source = Path(sys.argv[sys.argv.index("--") + 1])
output = Path(__file__).resolve().parent
bpy.ops.object.select_all(action="SELECT")
bpy.ops.object.delete(use_global=False)
bpy.ops.import_scene.gltf(filepath=str(source))
scene = bpy.context.scene
scene.render.resolution_x = 1000
scene.render.resolution_y = 1000
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = "PNG"
scene.view_settings.view_transform = "Standard"
camera_data = bpy.data.cameras.new("Face camera")
camera = bpy.data.objects.new("Face camera", camera_data)
scene.collection.objects.link(camera)
scene.camera = camera
camera_data.type = "ORTHO"
camera_data.ortho_scale = 0.37
camera.location = (0.03, -1.5, 0.957)
camera.rotation_euler = (Vector((0.03, 0.0, 0.957)) - camera.location).to_track_quat("-Z", "Y").to_euler()

scene.render.engine = "CYCLES"
scene.cycles.samples = 4
scene.cycles.use_denoising = False
world = bpy.data.worlds.new("unlit white")
scene.world = world
world.use_nodes = True
world.node_tree.nodes["Background"].inputs["Color"].default_value = (1, 1, 1, 1)
world.node_tree.nodes["Background"].inputs["Strength"].default_value = 1
material = next(obj.data.materials[0] for obj in scene.objects if obj.type == "MESH" and obj.data.materials)
nodes = material.node_tree.nodes
links = material.node_tree.links
texture = next(node for node in nodes if node.type == "TEX_IMAGE" and node.image and node.image.name == "texture_pbr_20250901")
output_node = next(node for node in nodes if node.type == "OUTPUT_MATERIAL")
emission = nodes.new("ShaderNodeEmission")
links.new(texture.outputs["Color"], emission.inputs["Color"])
links.new(emission.outputs["Emission"], output_node.inputs["Surface"])
scene.render.filepath = str(output / "face_unlit.png")
bpy.ops.render.render(write_still=True)

scene.render.engine = "BLENDER_WORKBENCH"
scene.display.shading.light = "STUDIO"
scene.display.shading.color_type = "SINGLE"
scene.display.shading.single_color = (0.62, 0.62, 0.62)
scene.display.shading.show_shadows = True
scene.render.filepath = str(output / "face_gray.png")
bpy.ops.render.render(write_still=True)
