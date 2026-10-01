"""Magnify attachment areas against a checkerboard for visual inspection."""

from pathlib import Path
from PIL import Image, ImageDraw


HERE = Path(__file__).resolve().parent
PET = HERE.parent
source = Image.open(HERE / "reconstructed.png").convert("RGBA")
atlas = Image.open(PET / "idle_motion.png").convert("RGBA")
frames = [source]
for index in (0, 18, 36, 54):
    image = atlas.crop(((index % 9) * 184, (index // 9) * 276,
                        (index % 9 + 1) * 184, (index // 9 + 1) * 276))
    frames.append(image.resize((1024, 1536), Image.Resampling.NEAREST))


def checker(size):
    background = Image.new("RGBA", size, "#f2f2f2")
    draw = ImageDraw.Draw(background)
    for y in range(0, size[1], 24):
        for x in range(0, size[0], 24):
            if (x // 24 + y // 24) % 2:
                draw.rectangle((x, y, x + 23, y + 23), fill="#c5c5c5")
    return background


for name, box in (("left_hair_skirt", (130, 650, 465, 1140)),
                  ("right_hair_skirt", (610, 600, 910, 1050)),
                  ("tail_skirt", (700, 740, 1010, 1190))):
    width, height = box[2] - box[0], box[3] - box[1]
    panel = Image.new("RGB", (width * len(frames), height), "white")
    for index, frame in enumerate(frames):
        crop = frame.crop(box)
        bg = checker((width, height))
        bg.alpha_composite(crop)
        panel.paste(bg.convert("RGB"), (index * width, 0))
    panel.save(HERE / f"diagnostic_{name}.png")
