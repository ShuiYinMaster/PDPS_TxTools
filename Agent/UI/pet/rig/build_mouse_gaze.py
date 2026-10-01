"""Build small eye-direction overlays for the fixed layered desktop pet."""

from pathlib import Path
import numpy as np
from PIL import Image, ImageChops, ImageDraw, ImageFilter


HERE = Path(__file__).resolve().parent
PET = HERE.parent
SOURCE = Image.open(HERE / "reconstructed.png").convert("RGBA")
FRAME = (184, 276)
EYES = (
    # iris bounds, eye opening, eye-white color
    ((402, 424, 450, 469), (382, 407, 467, 474), (247, 246, 251, 255)),
    ((539, 381, 590, 429), (516, 364, 610, 443), (247, 247, 251, 255)),
)


def ellipse_mask(bounds, blur=0):
    mask = Image.new("L", SOURCE.size)
    ImageDraw.Draw(mask).ellipse(bounds, fill=255)
    return mask.filter(ImageFilter.GaussianBlur(blur)) if blur else mask


def offset_image(image, dx, dy):
    result = Image.new("RGBA", image.size)
    result.paste(image, (dx, dy))
    return result


def direction_overlay(dx, dy):
    result = Image.new("RGBA", SOURCE.size)
    if dx == 0 and dy == 0:
        return result
    for iris_box, opening_box, white in EYES:
        iris_mask = ellipse_mask(iris_box, 1.2)
        opening = ellipse_mask(opening_box, 0.6)
        # Repaint the existing iris as sclera, then move its original pixels.
        sclera = Image.new("RGBA", SOURCE.size, white)
        sclera.putalpha(iris_mask)
        result.alpha_composite(sclera)
        iris = SOURCE.copy()
        iris.putalpha(ImageChops.multiply(SOURCE.getchannel("A"), iris_mask))
        moved = offset_image(iris, dx, dy)
        moved.putalpha(ImageChops.multiply(moved.getchannel("A"), opening))
        result.alpha_composite(moved)
        # Keep the fixed upper eyelash attached to the eyelid, not the pupil.
        upper = Image.new("L", SOURCE.size)
        ImageDraw.Draw(upper).rectangle((iris_box[0]-6, iris_box[1]-12,
                                         iris_box[2]+6, iris_box[1]+9), fill=255)
        rgb = np.asarray(SOURCE)[:, :, :3]
        dark = ((rgb[:, :, 0] < 85) & (rgb[:, :, 1] < 75) &
                (rgb[:, :, 2] < 125)).astype(np.uint8) * 255
        lash = ImageChops.multiply(upper, Image.fromarray(dark, "L"))
        preserved = SOURCE.copy()
        preserved.putalpha(ImageChops.multiply(SOURCE.getchannel("A"), lash))
        result.alpha_composite(preserved)
    return result


atlas = Image.new("RGBA", (FRAME[0] * 5, FRAME[1] * 5))
previews = Image.new("RGB", (5 * 300, 5 * 240), "white")
for row in range(5):
    for column in range(5):
        overlay = direction_overlay((column - 2) * 4, (row - 2) * 3)
        scaled = overlay.resize(FRAME, Image.Resampling.LANCZOS)
        atlas.alpha_composite(scaled, (column * FRAME[0], row * FRAME[1]))
        if row in (0, 2, 4):
            original = SOURCE.copy()
            original.alpha_composite(overlay)
            crop = original.crop((332, 315, 651, 558)).resize((300, 240))
            previews.paste(crop.convert("RGB"), (column * 300, row * 240))
atlas.save(PET / "gaze_motion.png", optimize=True)
previews.crop((0, 0, 1500, 1200)).save(HERE / "gaze_preview.png")
print("gaze atlas", atlas.size)
