"""Inspect isolated eye artwork while refining moving upper eyelashes."""

from pathlib import Path
from PIL import Image, ImageDraw

rig = Path(__file__).resolve().parent
crop = (350, 335, 645, 505)
parts = [
    ("left eye", "01_eye_left.png"),
    ("right eye", "02_eye_right.png"),
    ("both on face", None),
]
sheet = Image.new("RGB", (3 * 590, 340), "white")
for i, (label, name) in enumerate(parts):
    if name:
        layer = Image.open(rig / "layers" / name).convert("RGBA")
    else:
        layer = Image.open(rig / "reconstructed.png").convert("RGBA")
    panel = Image.new("RGBA", layer.size, "white")
    panel.alpha_composite(layer)
    panel = panel.crop(crop).resize((590, 340), Image.Resampling.NEAREST)
    guide = ImageDraw.Draw(panel)
    for source_x in range(360, 641, 20):
        xx = (source_x - crop[0]) * 2
        guide.line((xx, 0, xx, 340), fill=(30, 220, 60, 100), width=1)
        guide.text((xx + 1, 0), str(source_x), fill=(0, 100, 30))
    for source_y in range(340, 501, 20):
        yy = (source_y - crop[1]) * 2
        guide.line((0, yy, 590, yy), fill=(30, 220, 60, 100), width=1)
        guide.text((1, yy + 1), str(source_y), fill=(0, 100, 30))
    sheet.paste(panel.convert("RGB"), (i * 590, 0))
sheet.save(rig / "eye_parts_review.png")
