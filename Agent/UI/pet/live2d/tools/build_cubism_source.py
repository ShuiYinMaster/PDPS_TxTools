"""Build a new Cubism source PSD with separate eyes and hidden overlap artwork.

The PSD is source artwork. Binding and .moc3 export happen in Cubism Editor.
"""
from pathlib import Path
import json
import sys
from collections import OrderedDict

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / ".deps"))
import numpy as np
from PIL import Image, ImageDraw, ImageFilter
from psd_tools import PSDImage

source = Image.open(ROOT / "whale_master_clean.png").convert("RGBA")
rgba = np.asarray(source)
width, height = source.size
visible = rgba[:, :, 3] > 0
assigned = np.zeros((height, width), dtype=bool)
parts = OrderedDict()
rgb = rgba[:, :, :3].astype(np.int16)
yy, xx = np.indices((height, width))
dark_lash = ((rgb[:, :, 0] < 180) & (rgb[:, :, 1] < 135) &
             (rgb[:, :, 2] < 150) &
             (rgb[:, :, 2] < rgb[:, :, 0] * 1.2 + 18))
skin_color = ((rgb[:, :, 0] > 155) &
              (rgb[:, :, 0] > rgb[:, :, 1] + 6) &
              (rgb[:, :, 0] > rgb[:, :, 2] + 8))


def polygon(points):
    mask = Image.new("L", source.size)
    ImageDraw.Draw(mask).polygon(points, fill=255)
    return np.asarray(mask) > 0


def ellipse(bounds):
    mask = Image.new("L", source.size)
    ImageDraw.Draw(mask).ellipse(bounds, fill=255)
    return np.asarray(mask) > 0


def take(name, shape, condition=None):
    mask = shape & visible & ~assigned
    if condition is not None:
        mask &= condition
    if not mask.any():
        raise ValueError("Empty source part: " + name)
    parts[name] = mask
    assigned[mask] = True


# Eyes are deliberately separated before the skin/hair regions.
eyes = {
    "L": {"opening": [(384, 360), (403, 345), (431, 340), (461, 346),
                         (480, 364), (471, 389), (448, 400), (409, 401), (391, 386)],
          "iris": (416, 346, 466, 401),
          "upper": [(375, 365), (391, 344), (416, 330), (444, 331),
                    (470, 344), (488, 362), (476, 370), (458, 351),
                    (429, 344), (406, 352), (388, 381)]},
    "R": {"opening": [(527, 357), (548, 341), (575, 337), (600, 346),
                         (615, 364), (603, 387), (579, 399), (550, 397), (534, 382)],
          "iris": (544, 337, 593, 398),
          "upper": [(513, 359), (532, 337), (559, 322), (585, 326),
                    (609, 342), (624, 357), (614, 376), (597, 354),
                    (571, 340), (546, 343), (527, 371)]},
}
for side, spec in eyes.items():
    take("Eye" + side + "UpperLash", polygon(spec["upper"]), dark_lash)
    take("Eye" + side + "LowerLash", polygon(spec["opening"]), dark_lash)
    take("Eye" + side + "Iris", ellipse(spec["iris"]))
    take("Eye" + side + "White", polygon(spec["opening"]),
         (rgb[:, :, 1] > 210) & (rgb[:, :, 2] > 210) &
         (rgb[:, :, 0] - rgb[:, :, 2] < 22))

take("BrowL", polygon([(396, 294), (415, 287), (441, 288), (457, 302),
                       (438, 306), (414, 304), (396, 309)]), ~skin_color)
take("BrowR", polygon([(531, 286), (552, 280), (580, 284), (594, 297),
                       (578, 304), (550, 298), (531, 302)]), ~skin_color)
take("MouthSmile", polygon([(478, 420), (500, 418), (534, 419), (539, 437),
                            (512, 443), (481, 436)]), rgb[:, :, 0] - rgb[:, :, 1] > 45)
take("NoseHighlight", polygon([(490, 387), (513, 387), (516, 408), (489, 408)]),
     (rgb[:, :, 0] > 235) & (rgb[:, :, 1] > 231) & (rgb[:, :, 2] > 225))
take("Face", polygon([(377, 303), (395, 271), (433, 236), (540, 235),
                      (589, 275), (629, 333), (624, 406), (599, 442),
                      (558, 465), (505, 480), (453, 466), (405, 438),
                      (381, 389)]), skin_color)

# Foreground hair is sliced at natural lock boundaries, not through eye art.
take("SideLockL", polygon([(372, 431), (415, 445), (458, 486), (475, 518),
                           (465, 573), (427, 577), (404, 538), (364, 500)]))
take("SideLockR", polygon([(596, 428), (632, 434), (659, 485), (639, 541),
                           (625, 576), (589, 575), (574, 525), (586, 475)]))
take("BangCenter", polygon([(407, 151), (459, 130), (523, 136), (547, 182),
                            (538, 270), (540, 332), (563, 371), (528, 380),
                            (483, 375), (451, 345), (435, 267)]))
take("BangL", polygon([(361, 162), (412, 146), (436, 242), (419, 317),
                       (407, 365), (370, 393), (348, 353), (327, 267)]))
take("BangR", polygon([(523, 139), (565, 143), (609, 166), (633, 239),
                       (623, 333), (595, 371), (558, 316), (535, 244)]))
take("HairFrontL", polygon([(357, 165), (403, 139), (364, 252), (341, 337),
                            (368, 408), (402, 435), (377, 477), (322, 459),
                            (294, 400), (291, 310), (316, 213)]))
take("HairFrontR", polygon([(567, 143), (623, 167), (672, 225), (682, 323),
                            (651, 411), (602, 458), (579, 443), (610, 387),
                            (626, 321), (604, 247)]))
take("HairBow", polygon([(668, 270), (705, 267), (741, 284), (741, 333),
                         (710, 348), (671, 323)]))
take("EarL", polygon([(288, 335), (330, 379), (339, 432), (294, 457),
                       (213, 441), (205, 416), (251, 381)]))
take("EarR", polygon([(681, 329), (722, 352), (811, 404), (825, 427),
                       (783, 448), (710, 439), (669, 411)]))
take("Headband", polygon([(302, 243), (320, 177), (343, 124), (400, 87),
                          (456, 79), (488, 76), (534, 76), (589, 89),
                          (646, 114), (687, 175), (704, 248), (675, 275),
                          (649, 209), (598, 161), (530, 141), (450, 140),
                          (388, 163), (348, 215), (328, 274)]))
take("Ahoge", polygon([(291, 210), (303, 134), (330, 75), (378, 29),
                       (436, 7), (480, 26), (501, 74), (477, 101),
                       (451, 82), (451, 56), (416, 40), (365, 61),
                       (331, 105), (319, 177)]))

take("NeckRibbon", polygon([(449, 495), (484, 490), (514, 500), (550, 488),
                            (578, 499), (574, 546), (557, 594), (512, 576),
                            (476, 594), (462, 541)]))
take("Neck", polygon([(448, 446), (511, 470), (573, 443), (578, 493),
                      (556, 513), (509, 506), (470, 517), (447, 494)]))
take("HandL", polygon([(209, 848), (252, 852), (279, 885), (256, 927),
                       (232, 968), (185, 976), (178, 950), (189, 908)]))
take("HandR", polygon([(751, 847), (792, 848), (804, 885), (840, 943),
                       (838, 975), (794, 970), (769, 934), (757, 901)]))
take("CuffL", polygon([(208, 815), (238, 815), (285, 849), (311, 860),
                       (310, 891), (285, 909), (247, 884), (216, 881), (211, 852)]))
take("CuffR", polygon([(723, 814), (758, 814), (811, 836), (819, 866),
                       (793, 889), (767, 886), (719, 906), (712, 880)]))
take("SleeveL", polygon([(365, 542), (395, 567), (432, 618), (423, 675),
                         (402, 736), (312, 858), (258, 828), (277, 784),
                         (332, 695), (305, 635), (314, 592)]))
take("SleeveR", polygon([(629, 542), (677, 582), (716, 632), (695, 690),
                         (738, 758), (776, 826), (724, 855), (648, 751),
                         (600, 671), (600, 620)]))
take("Apron", polygon([(401, 711), (600, 709), (648, 739), (662, 811),
                       (691, 925), (685, 1004), (644, 1050), (572, 1071),
                       (475, 1070), (400, 1038), (354, 981), (355, 858)]))
take("Bodice", polygon([(386, 500), (435, 512), (475, 540), (554, 536),
                        (598, 509), (632, 507), (656, 603), (619, 660),
                        (599, 718), (431, 721), (414, 668), (366, 607)]))
take("WaistBowL", polygon([(373, 699), (422, 697), (431, 741), (410, 778),
                           (362, 788), (335, 765)]))
take("WaistBowR", polygon([(604, 697), (640, 697), (674, 754), (670, 789),
                           (615, 777), (593, 741)]))
take("SkirtBowL", polygon([(281, 1031), (337, 1034), (369, 1066),
                           (344, 1106), (306, 1101), (277, 1074)]))
take("SkirtBowR", polygon([(638, 1032), (692, 1036), (720, 1068),
                           (699, 1107), (659, 1101), (634, 1072)]))
take("SkirtPanelL", polygon([(353, 767), (388, 790), (413, 952), (377, 1068),
                             (304, 1159), (265, 1165), (168, 1100), (157, 1056),
                             (225, 953), (303, 845)]))
take("SkirtPanelR", polygon([(645, 774), (690, 811), (781, 946), (850, 1053),
                             (858, 1101), (774, 1173), (722, 1160), (669, 1073)]))
take("SkirtCenter", polygon([(365, 1000), (442, 1038), (576, 1068),
                             (656, 1015), (711, 1103), (735, 1180),
                             (669, 1237), (481, 1249), (344, 1211), (294, 1154)]))
take("LegL", polygon([(391, 1182), (494, 1200), (503, 1329), (502, 1479),
                      (475, 1520), (415, 1512), (384, 1476), (393, 1347)]))
take("LegR", polygon([(509, 1195), (605, 1189), (624, 1342), (625, 1482),
                      (595, 1519), (522, 1515), (499, 1471), (507, 1330)]))
take("Tail", polygon([(816, 826), (865, 850), (906, 899), (955, 874),
                       (996, 824), (1009, 895), (988, 966), (916, 1006),
                       (879, 1067), (810, 1106), (782, 1073), (838, 1014),
                       (859, 976), (817, 936)]))
take("RearHairL", polygon([(267, 422), (338, 458), (373, 546), (346, 704),
                            (295, 846), (241, 957), (111, 935), (72, 885),
                            (77, 746), (94, 595), (180, 497)]))
take("RearHairR", polygon([(690, 427), (782, 444), (860, 588), (896, 782),
                            (881, 864), (796, 899), (732, 807), (690, 643),
                            (649, 541)]))

# Assign small edge misses to their nearest component. This avoids a static
# catch-all artwork layer, which would tear when the neighboring mesh moves.
labels = np.zeros((height, width), dtype=np.uint8)
for label, mask in enumerate(parts.values(), 1):
    labels[mask] = label
for _ in range(80):
    pending = labels == 0
    if not (pending & visible).any():
        break
    for dy, dx in ((0, 1), (0, -1), (1, 0), (-1, 0)):
        neighbor = np.roll(labels, (dy, dx), axis=(0, 1))
        if dy == 1: neighbor[0, :] = 0
        if dy == -1: neighbor[-1, :] = 0
        if dx == 1: neighbor[:, 0] = 0
        if dx == -1: neighbor[:, -1] = 0
        add = pending & (neighbor > 0)
        labels[add] = neighbor[add]
        pending[add] = False
remaining = visible & (labels == 0)
if remaining.any():
    raise ValueError("Unassigned pixels: " + str(int(remaining.sum())))
parts = OrderedDict((name, (labels == i) & visible)
                    for i, name in enumerate(parts, 1))
images = {}
for name, mask in parts.items():
    pixels = np.zeros_like(rgba)
    pixels[mask] = rgba[mask]
    images[name] = Image.fromarray(pixels, "RGBA")


def gradient_base(points, top_color, bottom_color):
    shape = polygon(points)
    rows = np.nonzero(shape.any(axis=1))[0]
    ratio = np.clip((yy - rows.min()) / max(1, rows.max() - rows.min()), 0, 1)
    pixels = np.zeros_like(rgba)
    for channel in range(3):
        pixels[:, :, channel] = np.where(shape,
            np.asarray(top_color[channel] * (1-ratio) + bottom_color[channel] * ratio,
                       dtype=np.uint8), 0)
    pixels[:, :, 3] = shape.astype(np.uint8) * 255
    return Image.fromarray(pixels, "RGBA")


# Clean full face underneath eyes and bangs: original visible skin is retained.
face_base = gradient_base(
    [(378, 310), (382, 248), (413, 216), (466, 203), (551, 205),
     (602, 234), (630, 285), (638, 360), (621, 410), (598, 440),
     (561, 461), (509, 479), (457, 463), (414, 440), (385, 401)],
    (255, 235, 226), (255, 219, 208))
face_base.alpha_composite(images["Face"])
images["Face"] = face_base

for side, spec in eyes.items():
    white = gradient_base(spec["opening"], (249, 249, 254), (254, 249, 251))
    white.alpha_composite(images["Eye" + side + "White"])
    images["Eye" + side + "White"] = white
    iris_mask = Image.new("L", source.size)
    ImageDraw.Draw(iris_mask).ellipse(spec["iris"], fill=255)
    iris_fill = gradient_base([(spec["iris"][0], spec["iris"][1]),
                              (spec["iris"][2], spec["iris"][1]),
                              (spec["iris"][2], spec["iris"][3]),
                              (spec["iris"][0], spec["iris"][3])],
                             (53, 45, 110), (118, 207, 234))
    iris_fill.putalpha(iris_mask)
    iris_fill.alpha_composite(images["Eye" + side + "Iris"])
    images["Eye" + side + "Iris"] = iris_fill


def extend_under(name, cover_names, depth=32):
    cover = np.zeros((height, width), dtype=bool)
    for cover_name in cover_names:
        cover |= np.asarray(images[cover_name].getchannel("A")) > 220
    pixels = np.array(images[name])
    present = pixels[:, :, 3] > 80
    added_count = 0
    for _ in range(depth):
        added = np.zeros_like(present)
        for dy, dx in ((0, 1), (0, -1), (1, 0), (-1, 0)):
            neighbor = np.roll(present, (dy, dx), axis=(0, 1))
            if dy == 1: neighbor[0, :] = False
            if dy == -1: neighbor[-1, :] = False
            if dx == 1: neighbor[:, 0] = False
            if dx == -1: neighbor[:, -1] = False
            target = cover & ~present & ~added & neighbor
            shifted = np.roll(pixels, (dy, dx), axis=(0, 1))
            pixels[target] = shifted[target]
            pixels[target, 3] = 255
            added |= target
        added_count += int(added.sum())
        present |= added
        if not added.any(): break
    images[name] = Image.fromarray(pixels, "RGBA")
    return added_count


clothes = ["Bodice", "Apron", "SkirtPanelL", "SkirtPanelR", "SkirtCenter",
           "SleeveL", "SleeveR", "WaistBowL", "WaistBowR"]
underlap = {}
for name, covers in {
    "RearHairL": clothes + ["Face", "Neck", "HairFrontL", "SideLockL"],
    "RearHairR": clothes + ["Face", "Neck", "HairFrontR", "SideLockR"],
    "Tail": clothes,
    "SleeveL": ["Bodice", "CuffL", "HandL"],
    "SleeveR": ["Bodice", "CuffR", "HandR"],
    "Neck": ["Face", "NeckRibbon", "Bodice"],
    "SideLockL": ["HairFrontL", "BangL"],
    "SideLockR": ["HairFrontR", "BangR"],
}.items():
    underlap[name] = extend_under(name, covers, 42)

stack = [
    "RearHairL", "RearHairR", "Tail", "LegL", "LegR",
    "SkirtCenter", "SkirtPanelL", "SkirtPanelR", "SkirtBowL", "SkirtBowR",
    "WaistBowL", "WaistBowR", "Neck", "SleeveL", "SleeveR", "Bodice", "Apron",
    "CuffL", "CuffR", "HandL", "HandR",
    "EarL", "EarR", "Headband", "Face",
    "EyeLWhite", "EyeRWhite", "EyeLIris", "EyeRIris",
    "EyeLLowerLash", "EyeRLowerLash", "EyeLUpperLash", "EyeRUpperLash",
    "BrowL", "BrowR", "NoseHighlight", "MouthSmile",
    "SideLockL", "SideLockR", "HairFrontL", "HairFrontR", "BangL", "BangR", "BangCenter",
    "HairBow", "NeckRibbon", "Ahoge",
]
assert set(stack) == set(images)
layers_dir = ROOT / "source_layers"
layers_dir.mkdir(exist_ok=True)
psd = PSDImage.new(mode="RGB", size=source.size, color=(0, 0, 0), depth=8)
neutral = Image.new("RGBA", source.size)
manifest = []
for index, name in enumerate(stack, 1):
    image = images[name]
    image.save(layers_dir / (name + ".png"), optimize=True)
    psd.create_pixel_layer(image, name=name)
    neutral.alpha_composite(image)
    manifest.append({"name": name, "draw_order": index, "bbox": image.getbbox(),
                     "source_pixels": int(parts[name].sum())})
psd_path = ROOT / "whale_cubism_source_v1.psd"
psd.save(psd_path)
reopened = PSDImage.open(psd_path)
assert len(reopened) == len(stack)
neutral.save(ROOT / "source_composite.png", optimize=True)
white = Image.new("RGBA", source.size, (239, 240, 248, 255))
white.alpha_composite(neutral)
white.convert("RGB").save(ROOT / "source_composite_preview.jpg", quality=95)
source_white = Image.new("RGBA", source.size, (239, 240, 248, 255))
source_white.alpha_composite(source)
delta = np.abs(np.asarray(white).astype(np.int16) - np.asarray(source_white).astype(np.int16))
palette = [(229, 122, 128), (129, 163, 226), (243, 200, 130),
           (127, 203, 177), (178, 145, 216), (111, 191, 206)]
map_pixels = np.full((height, width, 3), 245, dtype=np.uint8)
for index, mask in enumerate(parts.values()):
    map_pixels[mask] = palette[index % len(palette)]
Image.fromarray(map_pixels).save(ROOT / "source_partition_map.png")
(ROOT / "source_manifest.json").write_text(json.dumps({
    "status": "source_artwork_unbound", "canvas": source.size, "artmesh_count": len(stack),
    "neutral_composite_mean_error": float(delta[:, :, :3].mean()),
    "hidden_overlap_added_pixels": underlap, "parts": manifest,
}, indent=2), encoding="utf-8")
print(json.dumps({"psd": str(psd_path), "layers": len(stack),
                  "neutral_mean_error": float(delta[:, :, :3].mean()),
                  "hidden_overlap_added_pixels": underlap}, indent=2))
