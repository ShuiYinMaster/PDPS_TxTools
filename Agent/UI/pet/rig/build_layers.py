"""Split the idle illustration into pixel-aligned Live2D source layers."""

from pathlib import Path
from collections import deque
import sys
from zipfile import ZipFile, ZIP_DEFLATED
import numpy as np
from PIL import Image, ImageDraw

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE / ".deps"))
from psd_tools import PSDImage
OUT = HERE / "layers"
OUT.mkdir(exist_ok=True)
for old_layer in OUT.glob("[0-9][0-9]_*.png"):
    old_layer.unlink()
src = Image.open(HERE.parent / "idle.png").convert("RGBA")
rgba = np.asarray(src)
h, w = rgba.shape[:2]
visible = rgba[:, :, 3] > 0
assigned = np.zeros((h, w), dtype=bool)
layers = []


def region(name, points, condition=None):
    image = Image.new("1", (w, h))
    ImageDraw.Draw(image).polygon(points, fill=1)
    mask = np.asarray(image, dtype=bool) & visible & ~assigned
    if condition is not None:
        mask &= condition
    if not mask.any():
        raise ValueError(f"empty layer: {name}")
    assigned[mask] = True
    layers.append((name, mask))


# Foremost details first. Visible pixels are lifted directly from the source.
region("eye_left", [(364, 396), (384, 385), (447, 384), (471, 406), (471, 461), (447, 478), (387, 474), (359, 448)])
region("eye_right", [(507, 348), (542, 342), (590, 349), (613, 368), (619, 419), (584, 445), (522, 441), (503, 414)])
region("mouth", [(477, 474), (516, 468), (545, 481), (541, 515), (503, 523), (482, 509)])
region("hand_left", [(509, 901), (544, 902), (568, 923), (578, 951), (564, 974), (538, 980), (513, 961)])
region("hand_right", [(580, 899), (607, 902), (637, 928), (641, 958), (620, 974), (587, 971), (569, 955)])
rgb = rgba[:, :, :3].astype(np.int16)
skin = (rgb[:, :, 0] > 130) & (rgb[:, :, 0] > rgb[:, :, 1] + 8) & (rgb[:, :, 0] > rgb[:, :, 2] + 10)
region("face", [(371, 306), (428, 275), (507, 274), (577, 308), (626, 365), (619, 454), (595, 505), (549, 545), (481, 555), (422, 534), (389, 488), (369, 431)], skin)
region("side_lock_left", [(375, 512), (413, 520), (464, 560), (494, 603), (486, 650), (456, 637), (432, 609), (396, 577)])
region("side_lock_right", [(593, 510), (625, 522), (648, 556), (665, 605), (652, 643), (620, 617), (600, 584)])
region("bangs", [(320, 236), (372, 174), (441, 163), (508, 179), (586, 201), (640, 259), (649, 339), (622, 411), (584, 379), (546, 327), (503, 296), (497, 383), (468, 432), (438, 411), (412, 342), (388, 336), (373, 433), (346, 479), (302, 435), (299, 329)])
region("hair_right_front", [(532, 160), (624, 185), (685, 260), (702, 345), (691, 441), (648, 506), (600, 550), (576, 526), (613, 446), (620, 372), (583, 287)])
region("hair_left_front", [(394, 163), (459, 173), (417, 278), (387, 373), (389, 458), (422, 516), (396, 535), (339, 499), (281, 446), (265, 371), (287, 264), (333, 194)])
region("whale_ear_left", [(286, 398), (331, 456), (352, 510), (319, 554), (239, 554), (194, 534), (240, 482)])
region("whale_ear_right", [(675, 302), (725, 313), (772, 349), (850, 398), (857, 422), (814, 452), (735, 450), (686, 429)])
region("headband", [(261, 338), (279, 269), (303, 211), (332, 156), (364, 113), (409, 105), (458, 127), (486, 101), (535, 93), (586, 108), (640, 145), (691, 201), (699, 275), (666, 301), (633, 232), (577, 178), (509, 147), (429, 156), (355, 202), (309, 275), (294, 348)])
region("ahoge", [(257, 193), (264, 139), (296, 84), (338, 48), (382, 37), (426, 52), (451, 94), (449, 120), (415, 116), (410, 69), (368, 67), (330, 93), (291, 146), (292, 184)])
region("bow_and_brooch", [(487, 567), (520, 570), (560, 563), (622, 572), (641, 620), (594, 665), (561, 626), (537, 655), (498, 626)])
region("neck_and_collar", [(423, 514), (477, 544), (544, 548), (598, 515), (644, 585), (624, 641), (483, 646), (389, 600)])
region("arm_left", [(391, 595), (425, 610), (467, 686), (500, 782), (550, 888), (510, 929), (459, 887), (421, 821), (369, 765), (362, 694)])
region("arm_right", [(634, 598), (678, 630), (718, 704), (699, 789), (666, 862), (630, 917), (588, 900), (629, 815), (628, 735)])
region("apron", [(491, 793), (611, 792), (676, 829), (709, 917), (722, 1078), (688, 1140), (594, 1163), (469, 1139), (418, 1081), (418, 887)])
region("bodice", [(417, 622), (483, 623), (520, 650), (622, 643), (671, 621), (690, 713), (670, 798), (611, 820), (480, 815), (408, 774), (395, 692)])
region("skirt_left", [(366, 770), (436, 773), (476, 870), (449, 1001), (400, 1161), (420, 1297), (363, 1306), (266, 1266), (171, 1161), (183, 1074), (282, 923)])
region("skirt_right", [(662, 769), (717, 775), (784, 927), (878, 1177), (874, 1235), (790, 1289), (719, 1301), (704, 1178), (677, 1074)])
region("skirt_center", [(420, 1090), (472, 1097), (555, 1138), (677, 1085), (731, 1135), (735, 1294), (643, 1337), (482, 1336), (400, 1302)])
region("tail", [(771, 805), (812, 852), (873, 906), (925, 883), (997, 853), (1005, 902), (981, 976), (910, 1033), (865, 1138), (833, 1187), (784, 1156), (801, 1067), (836, 1001), (781, 955)])
region("leg_left", [(409, 1281), (511, 1273), (503, 1385), (529, 1456), (515, 1519), (414, 1518), (390, 1467), (403, 1387)])
region("leg_right", [(518, 1273), (632, 1277), (624, 1382), (638, 1460), (617, 1520), (513, 1520), (510, 1460), (527, 1380)])
region("back_hair_left", [(219, 382), (360, 408), (406, 517), (392, 683), (387, 837), (406, 987), (320, 1065), (163, 1069), (75, 1008), (73, 822), (92, 700), (197, 573)])
region("back_hair_right", [(670, 333), (748, 433), (843, 559), (864, 722), (876, 820), (841, 908), (736, 939), (690, 816), (658, 658), (624, 526)])

# Assign small polygon misses to the nearest extracted component. Transparent
# pixels are traversed too, so isolated wisps retain their nearest named part.
remainder = visible & ~assigned
labels = np.zeros((h, w), dtype=np.uint8)
for label, (_, mask) in enumerate(layers, start=1):
    labels[mask] = label
flat = labels.ravel()
distance = np.full(flat.size, 65535, dtype=np.uint16)
distance[flat != 0] = 0
queue = deque(np.flatnonzero(flat).tolist())
while queue:
    at = queue.popleft()
    if distance[at] >= 32:
        continue
    value = flat[at]
    x = at % w
    for neighbor in (at - w if at >= w else -1,
                     at + w if at < (h - 1) * w else -1,
                     at - 1 if x else -1,
                     at + 1 if x < w - 1 else -1):
        if neighbor >= 0 and flat[neighbor] == 0:
            flat[neighbor] = value
            distance[neighbor] = distance[at] + 1
            queue.append(neighbor)
layers = [(name, (labels == label) & visible) for label, (name, _) in enumerate(layers, start=1)]
far_remainder = visible & (labels == 0)
if far_remainder.any():
    layers.append(("source_artifacts", far_remainder))
assert sum(int(mask.sum()) for _, mask in layers) == int(visible.sum())

palette = [(230, 91, 92), (66, 150, 230), (244, 181, 49), (108, 194, 120), (171, 112, 224), (243, 117, 181), (70, 188, 188)]
map_rgb = np.full((h, w, 3), 255, dtype=np.uint8)
manifest = []
for i, (name, mask) in enumerate(layers, start=1):
    output = np.zeros_like(rgba)
    output[mask] = rgba[mask]
    filename = f"{i:02d}_{name}.png"
    Image.fromarray(output, "RGBA").save(OUT / filename)
    map_rgb[mask] = palette[(i - 1) % len(palette)]
    ys, xs = np.nonzero(mask)
    manifest.append((filename, int(mask.sum()), (int(xs.min()), int(ys.min()), int(xs.max()), int(ys.max()))))

Image.fromarray(map_rgb, "RGB").save(HERE / "mask-map.png")

# A separate, hidden underpainting provides skin beneath the movable eyes and
# fringe. It is a color reconstruction, not source artwork.
repairs = HERE / "repairs"
repairs.mkdir(exist_ok=True)
face_points = [(353, 327), (373, 275), (433, 231), (521, 235), (596, 280),
               (628, 360), (622, 455), (591, 514), (544, 551), (478, 557),
               (420, 532), (379, 484), (354, 415)]
face_shape = Image.new("L", src.size)
ImageDraw.Draw(face_shape).polygon(face_points, fill=255)
fill = np.zeros_like(rgba)
samples = dict(layers)["face"] & skin & (rgba[:, :, 3] > 230)
base = np.array([int(np.median(rgba[:, :, channel][samples])) for channel in range(3)])
shape = np.asarray(face_shape) > 0
vertical = np.clip((np.arange(h)[:, None] - 320) / 260, 0, 1)
for channel in range(3):
    channel_fill = np.clip(base[channel] + (vertical - 0.5) * (4 if channel == 0 else 10), 0, 255).astype(np.uint8)
    fill[:, :, channel] = np.where(shape, channel_fill, 0)
fill[:, :, 3] = np.asarray(face_shape)
face_repair = Image.fromarray(fill, "RGBA")
face_repair.alpha_composite(Image.open(OUT / "06_face.png"))
face_repair.save(repairs / "face_underpaint.png")

recomposed = Image.new("RGBA", src.size)
for i, (name, _) in enumerate(layers, start=1):
    recomposed.alpha_composite(Image.open(OUT / f"{i:02d}_{name}.png"))
roundtrip = np.asarray(recomposed)
diff = np.abs(roundtrip.astype(np.int16) - rgba.astype(np.int16))
assert diff[visible].max() <= 1, f"visible roundtrip mismatch: {diff[visible].max()}"
assert np.array_equal(roundtrip[:, :, 3], rgba[:, :, 3])
recomposed.save(HERE / "reconstructed.png")

psd = PSDImage.new(mode="RGB", size=src.size, color=(0, 0, 0), depth=8)
filenames = {name: f"{i:02d}_{name}.png" for i, (name, _) in enumerate(layers, start=1)}
stack_back_to_front = [
    "source_artifacts", "back_hair_left", "back_hair_right", "tail",
    "leg_left", "leg_right", "skirt_left", "skirt_center", "skirt_right",
    "bodice", "apron", "arm_left", "arm_right", "hand_left", "hand_right",
    "neck_and_collar", "whale_ear_left", "whale_ear_right", "headband",
    "face", "eye_left", "eye_right", "mouth", "hair_left_front",
    "hair_right_front", "bangs", "side_lock_left", "side_lock_right",
    "bow_and_brooch", "ahoge",
]
assert set(stack_back_to_front) == set(filenames)
for name in stack_back_to_front:
    if name == "face":
        repair_layer = psd.create_pixel_layer(face_repair, name="REPAIR face underpaint")
        repair_layer.visible = False
    psd.create_pixel_layer(Image.open(OUT / filenames[name]), name=name)
psd_path = HERE / "whale_pet_layers.psd"
psd.save(psd_path)
reopened = PSDImage.open(psd_path)
package = HERE / "whale_pet_live2d_layers.zip"
with ZipFile(package, "w", compression=ZIP_DEFLATED, compresslevel=6) as archive:
    for path in [HERE / "README.md", HERE / "mask-map.png", HERE / "reconstructed.png",
                 HERE / "build_layers.py", HERE / "extract_wave_arm.py",
                 HERE / "render_pet_motion.py", HERE / "wave_arm_overlay.png",
                 HERE / "wave_body_fill.png", HERE / "motion_review.gif",
                 psd_path, repairs / "face_underpaint.png", *sorted(OUT.glob("*.png"))]:
        if path.is_file():
            archive.write(path, path.relative_to(HERE))
print(f"PSD layers: {len(reopened)}; source pixels: {int(visible.sum())}; remainder pixels: {int(remainder.sum())}")
print(f"Far source artifacts: {int(far_remainder.sum())}")
for item in manifest:
    print(*item)
print(f"Visible RGBA roundtrip: max channel error {diff[visible].max()}; alpha exact; PSD: {psd_path}")
print(f"Package: {package}")
