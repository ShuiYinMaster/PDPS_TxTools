"""Normalize character alpha and create a coordinate guide for source separation."""
from pathlib import Path
import json
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
source = Image.open(ROOT / "whale_master_v2.png").convert("RGBA")
pixels = np.array(source)
opaque = Image.fromarray((pixels[:, :, 3] >= 180).astype(np.uint8) * 255, "L")
near_character = np.asarray(opaque.filter(ImageFilter.MaxFilter(5))) > 0
pixels[:, :, 3] = np.where(near_character,
    np.minimum(255, pixels[:, :, 3].astype(np.float32) * 255 / 253), 0).astype(np.uint8)
pixels[pixels[:, :, 3] == 0, :3] = 0
clean = Image.fromarray(pixels, "RGBA")
clean.save(ROOT / "whale_master_clean.png", optimize=True)
background = Image.new("RGBA", clean.size, (238, 239, 246, 255))
background.alpha_composite(clean)
background.convert("RGB").save(ROOT / "master_preview.jpg", quality=95)
face = background.crop((300, 260, 660, 490)).resize((1080, 690), Image.Resampling.NEAREST)
draw = ImageDraw.Draw(face)
for x in range(300, 661, 20):
    draw.line(((x - 300) * 3, 0, (x - 300) * 3, 690), fill=(100, 180, 120), width=1)
    draw.text(((x - 300) * 3 + 2, 0), str(x), fill=(0, 90, 30))
for y in range(260, 491, 20):
    draw.line((0, (y - 260) * 3, 1080, (y - 260) * 3), fill=(100, 180, 120), width=1)
    draw.text((0, (y - 260) * 3 + 2), str(y), fill=(0, 90, 30))
face.convert("RGB").save(ROOT / "face_coordinate_guide.png")
print(json.dumps({"canvas": clean.size, "visible_pixels": int((pixels[:, :, 3] > 0).sum()),
                  "bbox": clean.getbbox()}))
