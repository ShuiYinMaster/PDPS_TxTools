"""Prototype a local wave arm on the unchanged idle body."""

from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

HERE = Path(__file__).resolve().parent
PET = HERE.parent
idle = Image.open(PET / "idle.png").convert("RGBA")
wave = Image.open(PET / "wave.png").convert("RGBA")
pixels = np.asarray(wave)
width, height = idle.size


def polygon(points):
    image = Image.new("L", idle.size)
    ImageDraw.Draw(image).polygon(points, fill=255)
    return np.asarray(image) > 0


hand = polygon([(285, 579), (301, 522), (323, 501), (342, 493),
                (358, 488), (375, 499), (390, 508), (409, 525),
                (429, 531), (437, 563), (426, 602), (412, 638),
                (380, 657), (337, 642), (302, 617)])
rgb = pixels[:, :, :3].astype(np.int16)
warm = (rgb[:, :, 0] > 125) & (rgb[:, :, 0] > rgb[:, :, 1] + 12) & (rgb[:, :, 0] > rgb[:, :, 2] + 12)
hand = Image.fromarray(((hand & warm) * 255).astype(np.uint8), "L").filter(ImageFilter.MaxFilter(7))
hand = np.asarray(hand) > 0
cuff = polygon([(299, 622), (323, 613), (368, 620), (415, 624),
                (447, 661), (411, 691), (355, 685), (304, 660)])
sleeve = polygon([(339, 646), (382, 656), (424, 652), (455, 671),
                  (477, 713), (464, 744), (422, 766), (373, 743),
                  (333, 705), (322, 673)])
arm_mask = (hand | cuff | sleeve) & (pixels[:, :, 3] > 0)
overlay = pixels.copy()
overlay[~arm_mask] = 0
Image.fromarray(overlay).save(HERE / "wave_arm_overlay.png")

base = Image.new("RGBA", idle.size)
for path in sorted((HERE / "layers").glob("[0-9][0-9]_*.png")):
    if "arm_left" in path.name or "hand_left" in path.name:
        continue
    base.alpha_composite(Image.open(path).convert("RGBA"))
base_alpha = np.asarray(base)[:, :, 3]
idle_alpha = np.asarray(idle)[:, :, 3]
hole = (idle_alpha > 0) & (base_alpha == 0)
fill = pixels.copy()
fill[~hole] = 0
Image.fromarray(fill).save(HERE / "wave_body_fill.png")
base.alpha_composite(Image.fromarray(fill))
base.alpha_composite(Image.fromarray(overlay))
base.resize((368, 552), Image.Resampling.LANCZOS).save(HERE / "wave_local_preview.png")
print("holes", int(hole.sum()), "uncovered", int((hole & (pixels[:, :, 3] == 0)).sum()))
