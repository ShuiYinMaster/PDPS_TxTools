"""Measure connected mesh shells to locate duplicate and movable parts."""

import json
import sys
from pathlib import Path

import bpy
import numpy as np


source = Path(sys.argv[sys.argv.index("--") + 1])
bpy.ops.object.select_all(action="SELECT")
bpy.ops.object.delete(use_global=False)
bpy.ops.import_scene.gltf(filepath=str(source))
obj = next(obj for obj in bpy.context.scene.objects if obj.type == "MESH")
mesh = obj.data
count = len(mesh.vertices)
triangle_vertices = np.empty(len(mesh.polygons) * 3, dtype=np.int32)
mesh.polygons.foreach_get("vertices", triangle_vertices)
triangles = triangle_vertices.reshape(-1, 3)
parent = np.arange(count, dtype=np.int32)
size = np.ones(count, dtype=np.int32)


def find(index):
    root = index
    while parent[root] != root:
        root = int(parent[root])
    while parent[index] != root:
        next_index = int(parent[index])
        parent[index] = root
        index = next_index
    return root


for number, (a, b, c) in enumerate(triangles):
    for left, right in ((int(a), int(b)), (int(b), int(c))):
        left = find(left)
        right = find(right)
        if left != right:
            if size[left] < size[right]:
                left, right = right, left
            parent[right] = left
            size[left] += size[right]
    if number and number % 500000 == 0:
        print("COMP_PROGRESS", number, flush=True)

roots = np.array([find(i) for i in range(count)], dtype=np.int32)
ids, labels = np.unique(roots, return_inverse=True)
vertices = np.empty((count, 3), dtype=np.float32)
mesh.vertices.foreach_get("co", vertices.reshape(-1))
world = np.asarray(obj.matrix_world, dtype=np.float32)
vertices = vertices @ world[:3, :3].T + world[:3, 3]
face_labels = labels[triangles[:, 0]]
vertex_counts = np.bincount(labels, minlength=len(ids))
face_counts = np.bincount(face_labels, minlength=len(ids))
minimum = np.full((len(ids), 3), np.inf, dtype=np.float32)
maximum = np.full((len(ids), 3), -np.inf, dtype=np.float32)
for axis in range(3):
    np.minimum.at(minimum[:, axis], labels, vertices[:, axis])
    np.maximum.at(maximum[:, axis], labels, vertices[:, axis])

order = np.argsort(face_counts)[::-1]
records = [{"id": int(i), "root_vertex": int(ids[i]),
            "vertices": int(vertex_counts[i]), "triangles": int(face_counts[i]),
            "min": minimum[i].tolist(), "max": maximum[i].tolist()}
           for i in order]
destination = Path(__file__).resolve().parent
(destination / "components.json").write_text(json.dumps(records, indent=2), encoding="utf-8")
np.save(destination / "component_id_per_vertex.npy", labels)
print("COMP_DONE", len(records), json.dumps(records[:30]), flush=True)
