"""Render read-only orthographic GLB inspection views with its original materials."""

import sys
from pathlib import Path

import bpy
from mathutils import Vector


arguments = sys.argv[sys.argv.index("--") + 1:]
source = Path(arguments[0])
prefix = arguments[1] if len(arguments) > 1 else ""
output = Path(__file__).resolve().parent
bpy.ops.object.select_all(action="SELECT")
bpy.ops.object.delete(use_global=False)
bpy.ops.import_scene.gltf(filepath=str(source))
scene = bpy.context.scene
scene.render.engine = "CYCLES"
scene.cycles.samples = 8
scene.cycles.use_denoising = True
scene.render.resolution_x = 600
scene.render.resolution_y = 900
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = "PNG"
scene.render.film_transparent = False
scene.view_settings.view_transform = "Standard"

world = bpy.data.worlds.new("White studio")
scene.world = world
world.use_nodes = True
world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.8, 0.8, 0.8, 1)
world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.8

camera_data = bpy.data.cameras.new("Orthographic inspection")
camera = bpy.data.objects.new("Orthographic inspection", camera_data)
scene.collection.objects.link(camera)
scene.camera = camera
camera_data.type = "ORTHO"
camera_data.ortho_scale = 1.31

for name, location, energy in (
    ("front light", (-1.2, -1.5, 2.3), 300),
    ("back fill", (1.4, 1.0, 1.7), 180),
):
    light_data = bpy.data.lights.new(name, "AREA")
    light = bpy.data.objects.new(name, light_data)
    scene.collection.objects.link(light)
    light.location = location
    light_data.energy = energy
    light_data.shape = "DISK"
    light_data.size = 2.5
    light.rotation_euler = (Vector((0, 0, 0.6)) - light.location).to_track_quat("-Z", "Y").to_euler()

target = Vector((0.03, 0.04, 0.57))
for name, location in (
    ("view_y_negative", (0.03, -2.0, 0.57)),
    ("view_y_positive", (0.03, 2.0, 0.57)),
    ("view_x_positive", (2.0, 0.04, 0.57)),
    ("view_x_negative", (-2.0, 0.04, 0.57)),
):
    camera.location = location
    camera.rotation_euler = (target - camera.location).to_track_quat("-Z", "Y").to_euler()
    scene.render.filepath = str(output / f"{prefix}{name}.png")
    bpy.ops.render.render(write_still=True)
    print("PET_RENDER", name, scene.render.filepath)
