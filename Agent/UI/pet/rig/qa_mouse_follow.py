"""Check split idle frames and render a small gaze/neck review sheet."""

from pathlib import Path
from PIL import Image, ImageChops, ImageDraw
import numpy as np

pet = Path(__file__).resolve().parent.parent
rig = pet / "rig"
size = (184, 276)


def frame(atlas, index, columns=9):
    x = (index % columns) * size[0]
    y = (index // columns) * size[1]
    return atlas.crop((x, y, x + size[0], y + size[1]))


full = Image.open(pet / "idle_motion.png").convert("RGBA")
body = Image.open(pet / "idle_body_motion.png").convert("RGBA")
head = Image.open(pet / "idle_head_motion.png").convert("RGBA")
gaze = Image.open(pet / "gaze_motion.png").convert("RGBA")

checker = Image.new("RGBA", size, (241, 242, 247, 255))
draw = ImageDraw.Draw(checker)
for y in range(0, size[1], 12):
    for x in range(0, size[0], 12):
        if (x // 12 + y // 12) % 2:
            draw.rectangle((x, y, x + 11, y + 11), fill=(222, 225, 235, 255))

sheet = Image.new("RGB", (5 * 368, 3 * 552), "white")
max_visible_error = 0
changed_pixels = 0
for row, index in enumerate((0, 44, 54)):
    original = frame(full, index)
    neutral = Image.alpha_composite(frame(body, index), frame(head, index))
    white = Image.new("RGBA", size, "white")
    difference = np.asarray(ImageChops.difference(
        Image.alpha_composite(white, original),
        Image.alpha_composite(white, neutral)))[:, :, :3]
    max_visible_error = max(max_visible_error, int(difference.max()))
    changed_pixels += int(np.any(difference > 10, axis=2).sum())
    for column, direction in enumerate((-1, -0.5, 0, 0.5, 1)):
        result = frame(body, index)
        moving = Image.new("RGBA", size)
        moving.alpha_composite(frame(head, index), (round(direction), 0))
        result.alpha_composite(moving)
        if index != 44:
            gaze_column = round(2 + direction * 2)
            result.alpha_composite(gaze.crop((gaze_column * 184, 2 * 276,
                                               (gaze_column + 1) * 184, 3 * 276)),
                                   (round(direction), 0))
        opaque = Image.alpha_composite(checker, result)
        sheet.paste(opaque.resize((368, 552), Image.Resampling.NEAREST).convert("RGB"),
                    (column * 368, row * 552))
sheet.save(rig / "mouse_follow_review.png")
print("maximum visible split compositing difference:", max_visible_error)
print("pixels differing by more than 10 across three frames:", changed_pixels)
