"""Read-only inspection of a binary glTF character and embedded textures."""

import io
import json
import struct
import sys
from collections import Counter
from pathlib import Path

from PIL import Image, ImageDraw


source = Path(sys.argv[1])
destination = Path(__file__).resolve().parent
destination.mkdir(parents=True, exist_ok=True)
data = source.read_bytes()
magic, version, length = struct.unpack_from("<4sII", data)
if magic != b"glTF" or version != 2 or length != len(data):
    raise ValueError("Not a complete glTF 2.0 GLB")

offset = 12
chunks = []
while offset < length:
    size, kind = struct.unpack_from("<I4s", data, offset)
    start = offset + 8
    chunks.append((kind, start, size))
    offset = start + size
model = json.loads(data[chunks[0][1]:chunks[0][1] + chunks[0][2]])
bin_chunk = next((c for c in chunks if c[0] == b"BIN\0"), None)


def image_bytes(image):
    if "bufferView" in image:
        if bin_chunk is None:
            raise ValueError("Missing binary chunk")
        view = model["bufferViews"][image["bufferView"]]
        start = bin_chunk[1] + view.get("byteOffset", 0)
        return data[start:start + view["byteLength"]]
    if image.get("uri", "").startswith("data:"):
        import base64
        return base64.b64decode(image["uri"].split(",", 1)[1])
    return b""


accessors = model.get("accessors", [])
meshes = []
vertex_total = triangle_total = 0
for index, mesh in enumerate(model.get("meshes", [])):
    primitives = []
    for primitive in mesh.get("primitives", []):
        positions = accessors[primitive["attributes"]["POSITION"]]
        vertex_count = positions["count"]
        index_count = accessors[primitive["indices"]]["count"] if "indices" in primitive else vertex_count
        mode = primitive.get("mode", 4)
        triangle_count = index_count // 3 if mode == 4 else None
        vertex_total += vertex_count
        triangle_total += triangle_count or 0
        primitives.append({"vertices": vertex_count, "triangles": triangle_count,
                           "mode": mode, "material": primitive.get("material"),
                           "attributes": sorted(primitive["attributes"]),
                           "bounds": {key: positions.get(key) for key in ("min", "max")}})
    meshes.append({"index": index, "name": mesh.get("name"), "primitives": primitives})

texture_info = []
preview_tiles = []
for index, item in enumerate(model.get("images", [])):
    blob = image_bytes(item)
    entry = {"index": index, "name": item.get("name"), "mimeType": item.get("mimeType"),
             "byteLength": len(blob)}
    if blob:
        with Image.open(io.BytesIO(blob)) as image:
            entry.update({"size": image.size, "mode": image.mode})
            thumb = image.convert("RGB")
            thumb.thumbnail((300, 300))
            preview_tiles.append((index, thumb.copy()))
    texture_info.append(entry)

if preview_tiles:
    cell_w, cell_h = 320, 340
    columns = min(4, len(preview_tiles))
    rows = (len(preview_tiles) + columns - 1) // columns
    contact = Image.new("RGB", (columns * cell_w, rows * cell_h), "#eeeeee")
    draw = ImageDraw.Draw(contact)
    for position, (index, thumbnail) in enumerate(preview_tiles):
        x = (position % columns) * cell_w
        y = (position // columns) * cell_h
        contact.paste(thumbnail, (x + (cell_w - thumbnail.width) // 2, y + 25))
        draw.text((x + 10, y + 5), f"Image {index}: {texture_info[index]['size']}", fill="black")
    contact.save(destination / "textures_contact.png")

report = {
    "source": str(source), "glb_bytes": len(data),
    "asset": model.get("asset"), "extensionsUsed": model.get("extensionsUsed", []),
    "extensionsRequired": model.get("extensionsRequired", []),
    "counts": {key: len(model.get(key, [])) for key in
               ("scenes", "nodes", "meshes", "materials", "textures", "images", "skins", "animations", "cameras")},
    "vertices": vertex_total, "triangles": triangle_total,
    "meshes": meshes,
    "nodes": [{"index": index, "name": node.get("name"), "mesh": node.get("mesh"),
               "skin": node.get("skin"), "children": node.get("children", []),
               "translation": node.get("translation"), "rotation": node.get("rotation"),
               "scale": node.get("scale")} for index, node in enumerate(model.get("nodes", []))],
    "materials": model.get("materials", []), "textures": model.get("textures", []),
    "images": texture_info,
    "skins": model.get("skins", []),
    "animations": [{"name": item.get("name"), "channels": len(item.get("channels", [])),
                    "samplers": len(item.get("samplers", []))} for item in model.get("animations", [])],
    "primitive_modes": dict(Counter(p["mode"] for m in meshes for p in m["primitives"])),
}
(destination / "glb_report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps({key: report[key] for key in ("glb_bytes", "counts", "vertices", "triangles", "primitive_modes", "extensionsUsed")}, ensure_ascii=False))
print("images", texture_info)
