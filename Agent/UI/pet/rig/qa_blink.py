"""Render the seven frames surrounding the idle upper-eyelid blink."""

from pathlib import Path
from PIL import Image

rig = Path(__file__).resolve().parent
atlas = Image.open(rig.parent / "idle_motion.png").convert("RGBA")
sheet = Image.new("RGB", (7 * 300, 320), "white")
for i, frame in enumerate(range(42, 49)):
    x, y = (frame % 9) * 184, (frame // 9) * 276
    face = atlas.crop((x + 56, y + 40, x + 146, y + 136))
    white = Image.new("RGBA", face.size, "white")
    face = Image.alpha_composite(white, face)
    sheet.paste(face.resize((300, 320), Image.Resampling.NEAREST).convert("RGB"),
                (i * 300, 0))
sheet.save(rig / "blink_top_down_review.png")
