"""Pre-render four matching pet motions from one layered character.

Every state uses the same body artwork. Hair, tail and wave hand use deformations
whose roots do not move, so their joins stay covered by the clothes and hair.
"""

from math import cos, pi, sin
from pathlib import Path
import numpy as np
from PIL import Image, ImageChops, ImageDraw, ImageFilter

HERE = Path(__file__).resolve().parent
PET = HERE.parent
LAYER_DIR = HERE / "layers"
FRAME_W, FRAME_H = 184, 276
WORK_W, WORK_H = FRAME_W * 2, FRAME_H * 2
FPS = 20
SCALE = WORK_W / 1024
POSES = {
    "idle": (72, 9),       # 3.6 seconds
    "wave": (30, 6),       # 1.5 seconds
    "think": (56, 7),      # 2.8 seconds
    "happy": (36, 6),      # 1.8 seconds
}

STACK = [
    "source_artifacts", "back_hair_left", "back_hair_right", "tail",
    "leg_left", "leg_right", "skirt_left", "skirt_center", "skirt_right",
    "bodice", "apron", "arm_left", "arm_right", "hand_left", "hand_right",
    "neck_and_collar", "whale_ear_left", "whale_ear_right", "headband",
    "face", "eye_left", "eye_right", "mouth", "hair_left_front",
    "hair_right_front", "bangs", "side_lock_left", "side_lock_right",
    "bow_and_brooch", "ahoge",
]
HEAD_PARTS = set(STACK[STACK.index("whale_ear_left"):])


def load(path):
    return Image.open(path).convert("RGBA").resize((WORK_W, WORK_H), Image.Resampling.LANCZOS)


layers = {}
for name in STACK:
    paths = list(LAYER_DIR.glob(f"[0-9][0-9]_{name}.png"))
    if len(paths) != 1:
        raise RuntimeError(f"Expected one {name} layer; got {len(paths)}")
    layers[name] = load(paths[0])
face_underpaint = load(HERE / "repairs" / "face_underpaint.png")
wave_arm = load(HERE / "wave_arm_overlay.png")
wave_fill = load(HERE / "wave_body_fill.png")

# The extracted face underpaint still contains a few dark/blue eye pixels.
# Replace only the blink region with a skin gradient before it becomes a lid.
blink_skin_pixels = np.array(face_underpaint)
EYE_COVERS = ((382, 414, 472, 486), (515, 370, 614, 450))
LASH_SKIN_BOUNDS = ((350, 392, 473, 457), (510, 346, 623, 399))
for left, top, right, bottom in LASH_SKIN_BOUNDS + EYE_COVERS:
    x0, y0, x1, y1 = (round(v * SCALE) for v in (left, top, right, bottom))
    for y in range(y0, y1 + 1):
        ratio = (y - y0) / max(1, y1 - y0)
        blink_skin_pixels[y, x0:x1 + 1, :3] = (
            round(253 + ratio), round(231 - 22 * ratio),
            round(222 - 21 * ratio))
        blink_skin_pixels[y, x0:x1 + 1, 3] = 255
blink_skin = Image.fromarray(blink_skin_pixels, "RGBA")


def extract_upper_lash(layer_name, polygon):
    """Keep the burgundy upper lash without moving the iris or front hair."""
    artwork = np.array(layers[layer_name])
    region = Image.new("L", (WORK_W, WORK_H))
    ImageDraw.Draw(region).polygon(
        [(round(x * SCALE), round(y * SCALE)) for x, y in polygon], fill=255)
    rgb = artwork[:, :, :3].astype(np.int16)
    dark = ((rgb[:, :, 0] < 150) & (rgb[:, :, 1] < 120) &
            (rgb[:, :, 2] < 130) &
            (rgb[:, :, 2] < rgb[:, :, 0] * 1.2 + 18))
    alpha = np.minimum(artwork[:, :, 3], np.asarray(region))
    alpha = np.where(dark, alpha, 0).astype(np.uint8)
    artwork[:, :, 3] = alpha
    lash = Image.fromarray(artwork, "RGBA")
    erase = Image.fromarray(alpha, "L").filter(ImageFilter.MaxFilter(5))
    erase = erase.filter(ImageFilter.GaussianBlur(0.65))
    return lash, erase


UPPER_LASHES = (
    extract_upper_lash("eye_left", (
        (352, 426), (369, 408), (393, 401), (426, 400),
        (454, 411), (460, 422), (437, 424), (412, 415),
        (386, 416), (366, 446))),
    extract_upper_lash("eye_right", (
        (511, 381), (528, 365), (553, 355), (582, 352),
        (607, 359), (622, 378), (603, 382), (579, 374),
        (548, 374), (524, 389))),
)


def make_cover(names):
    alpha = Image.new("L", (WORK_W, WORK_H))
    for name in names:
        alpha = ImageChops.lighter(alpha, layers[name].getchannel("A"))
    return alpha


def extend_behind_clothes(image, cover, depth=12):
    """Continue a rear layer under opaque clothing so a moving edge cannot split."""
    pixels = np.array(image)
    present = pixels[:, :, 3] > 64
    # Grow only underneath real opaque clothing. Expanding the clothing mask
    # itself leaks blue filler into the visible gaps beside the skirt.
    allowed = np.asarray(cover) > 128
    for _ in range(depth):
        added = np.zeros_like(present)
        for dy, dx in ((0, 1), (0, -1), (1, 0), (-1, 0)):
            neighbor = np.roll(present, (dy, dx), axis=(0, 1))
            if dy == 1:
                neighbor[0, :] = False
            elif dy == -1:
                neighbor[-1, :] = False
            elif dx == 1:
                neighbor[:, 0] = False
            else:
                neighbor[:, -1] = False
            target = allowed & ~present & ~added & neighbor
            if np.any(target):
                shifted = np.roll(pixels, (dy, dx), axis=(0, 1))
                pixels[target] = shifted[target]
                pixels[target, 3] = 255
                added |= target
        if not np.any(added):
            break
        present |= added
    return Image.fromarray(pixels, "RGBA")


for rear_name, clothing in {
    "back_hair_left": ("skirt_left", "bodice", "arm_left", "apron"),
    "back_hair_right": ("skirt_right", "bodice", "arm_right", "apron"),
    "tail": ("skirt_right", "skirt_center", "apron"),
}.items():
    layers[rear_name] = extend_behind_clothes(layers[rear_name], make_cover(clothing))


def smoothstep(value):
    value = max(0.0, min(1.0, value))
    return value * value * (3 - 2 * value)


def with_opacity(image, opacity):
    if opacity >= 0.999:
        return image
    result = image.copy()
    result.putalpha(image.getchannel("A").point(lambda alpha: round(alpha * opacity)))
    return result


def horizontal_tip_sway(image, shift_at_tip, root_y, tip_y):
    """Move each row progressively; the shared root remains at its source pixel."""
    if abs(shift_at_tip) < 0.02:
        return image
    y = np.arange(WORK_H, dtype=np.float32) / SCALE
    progress = np.clip((y - root_y) / (tip_y - root_y), 0, 1) ** 1.7
    shift = shift_at_tip * progress
    source_x = np.arange(WORK_W, dtype=np.float32)[None, :] - shift[:, None]
    x0 = np.floor(source_x).astype(np.int32)
    x1 = x0 + 1
    blend = (source_x - x0)[:, :, None]
    valid0 = (x0 >= 0) & (x0 < WORK_W)
    valid1 = (x1 >= 0) & (x1 < WORK_W)
    x0 = np.clip(x0, 0, WORK_W - 1)
    x1 = np.clip(x1, 0, WORK_W - 1)
    data = np.asarray(image, dtype=np.float32)
    premultiplied = data.copy()
    premultiplied[:, :, :3] *= premultiplied[:, :, 3:4] / 255
    rows = np.arange(WORK_H)[:, None]
    left = premultiplied[rows, x0] * valid0[:, :, None]
    right = premultiplied[rows, x1] * valid1[:, :, None]
    result = left * (1 - blend) + right * blend
    alpha = result[:, :, 3:4]
    rgb = np.divide(result[:, :, :3] * 255, alpha,
                    out=np.zeros_like(result[:, :, :3]), where=alpha > 0.5)
    return Image.fromarray(np.clip(np.concatenate((rgb, alpha), axis=2), 0, 255).astype(np.uint8), "RGBA")


def squash_y(image, factor):
    if factor >= 0.999:
        return image
    box = image.getbbox()
    if box is None:
        return image
    crop = image.crop(box)
    height = max(2, round(crop.height * factor))
    crop = crop.resize((crop.width, height), Image.Resampling.LANCZOS)
    result = Image.new("RGBA", image.size)
    result.alpha_composite(crop, (box[0], (box[1] + box[3] - height) // 2))
    return result


def blink_factor(t, center, half_width=0.14):
    distance = abs(t - center)
    if distance >= half_width:
        return 1.0
    return 0.1 + 0.9 * smoothstep(distance / half_width)


def cover_eyes(frame, blink):
    """Lower each eyelid together with its extracted upper eyelashes."""
    closure = max(0.0, min(1.0, (1 - blink) / 0.9))
    if closure <= 0:
        return
    mask = Image.new("L", (WORK_W, WORK_H))
    for left, top, right, bottom in EYE_COVERS:
        # Reveal the same full eye mask from top to bottom. The prior version
        # grew an ellipse upward from the lower lid, reversing the blink.
        eye = Image.new("L", (WORK_W, WORK_H))
        ImageDraw.Draw(eye).ellipse(tuple(round(v * SCALE) for v in
                                          (left, top, right, bottom)), fill=255)
        gate = Image.new("L", (WORK_W, WORK_H))
        ImageDraw.Draw(gate).rectangle((round(left * SCALE), round(top * SCALE),
                                       round(right * SCALE),
                                       round((top + (bottom - top) * closure) * SCALE)), fill=255)
        mask = ImageChops.lighter(mask, ImageChops.multiply(eye, gate))
    mask = mask.filter(ImageFilter.GaussianBlur(max(0.5, SCALE * 2)))
    for _, erase in UPPER_LASHES:
        mask = ImageChops.lighter(mask, erase)
    skin = blink_skin.copy()
    skin.putalpha(Image.fromarray(
        np.minimum(np.asarray(skin.getchannel("A")), np.asarray(mask)).astype(np.uint8), "L"))
    frame.alpha_composite(skin)
    for lash, _ in UPPER_LASHES:
        moved = Image.new("RGBA", (WORK_W, WORK_H))
        moved.paste(lash, (0, round(34 * SCALE * closure)))
        frame.alpha_composite(moved)


def draw_accents(image, pose, envelope, seconds):
    if envelope <= 0:
        return
    draw = ImageDraw.Draw(image, "RGBA")
    if pose == "think":
        for x, y, radius in ((158, 262, 7), (143, 225, 11), (123, 190, 17)):
            cx, cy, r = round(x * SCALE), round(y * SCALE), round(radius * SCALE)
            alpha = round(190 * envelope)
            draw.ellipse((cx-r, cy-r, cx+r, cy+r), fill=(228, 248, 255, alpha),
                         outline=(73, 119, 173, alpha), width=2)
    elif pose == "happy":
        glow = envelope * (0.7 + 0.3 * sin(seconds * 10))
        for x, y, size in ((240, 230, 18), (777, 500, 13)):
            cx, cy, radius = round(x * SCALE), round(y * SCALE), round(size * SCALE * glow)
            if radius <= 1:
                continue
            draw.polygon([(cx, cy-radius), (cx+radius//3, cy-radius//3),
                          (cx+radius, cy), (cx+radius//3, cy+radius//3),
                          (cx, cy+radius), (cx-radius//3, cy+radius//3),
                          (cx-radius, cy), (cx-radius//3, cy-radius//3)],
                         fill=(255, 225, 156, round(205 * glow)))


def render_frame(pose, index):
    count, _ = POSES[pose]
    duration = count / FPS
    t = index / FPS
    phase = 2 * pi * t / duration
    envelope = sin(pi * t / duration) ** 2
    if pose == "idle":
        sway = sin(phase)
        blink = blink_factor(t, 2.24)
    else:
        sway = sin(phase * (2 if pose in ("wave", "happy") else 1)) * envelope
        blink = blink_factor(t, 1.35 if pose == "think" else 0.95, 0.13)

    if pose == "wave":
        arm_mix = smoothstep(t / 0.29) * smoothstep((duration - t) / 0.33)
    else:
        arm_mix = 0.0
    if pose == "think":
        blink = min(blink, 1 - 0.22 * envelope)

    hair_shift = (2.3 if pose == "idle" else 2.8) * sway
    tail_shift = (4 if pose == "idle" else (6 if pose == "happy" else 4.5)) * sway
    frame = Image.new("RGBA", (WORK_W, WORK_H))
    split_idle = pose == "idle"
    body_frame = Image.new("RGBA", (WORK_W, WORK_H)) if split_idle else None
    head_frame = Image.new("RGBA", (WORK_W, WORK_H)) if split_idle else None
    for name in STACK:
        if name == "arm_left" and arm_mix > 0:
            frame.alpha_composite(wave_fill)
        if name == "face":
            frame.alpha_composite(face_underpaint)
            if split_idle:
                head_frame.alpha_composite(face_underpaint)
        layer = layers[name]
        if name == "back_hair_left":
            layer = horizontal_tip_sway(layer, hair_shift, 470, 1060)
        elif name == "tail":
            layer = horizontal_tip_sway(layer, tail_shift, 1110, 820)
        elif name == "ahoge":
            layer = horizontal_tip_sway(layer, 1.2 * sway, 135, 40)
        elif name == "mouth" and pose == "think":
            layer = squash_y(layer, 1 - 0.35 * envelope)
        elif name in ("arm_left", "hand_left") and arm_mix > 0:
            layer = with_opacity(layer, 1 - arm_mix)
        frame.alpha_composite(layer)
        if split_idle:
            (head_frame if name in HEAD_PARTS else body_frame).alpha_composite(layer)
        if name == "eye_right":
            cover_eyes(frame, blink)
            if split_idle:
                cover_eyes(head_frame, blink)

    # The palm must sit in front of the side locks. Drawing it before the hair
    # hides one or two fingers even when the extracted hand itself is complete.
    if arm_mix > 0:
        arm_swing = 2.5 * sin(2 * pi * t * 3.2) * envelope
        raised = horizontal_tip_sway(wave_arm, arm_swing, 755, 520)
        if arm_mix < 0.999:
            traveling = Image.new("RGBA", raised.size)
            traveling.paste(raised, (round((1-arm_mix) * 155 * SCALE),
                                     round((1-arm_mix) * 300 * SCALE)))
            raised = traveling
        frame.alpha_composite(with_opacity(raised, arm_mix))

    draw_accents(frame, pose, envelope, t)
    result = frame.resize((FRAME_W, FRAME_H), Image.Resampling.LANCZOS)
    if split_idle:
        return (result,
                body_frame.resize((FRAME_W, FRAME_H), Image.Resampling.LANCZOS),
                head_frame.resize((FRAME_W, FRAME_H), Image.Resampling.LANCZOS))
    return result


all_frames = {}
for pose, (count, columns) in POSES.items():
    atlas = Image.new("RGBA", (columns * FRAME_W, (count // columns) * FRAME_H))
    body_atlas = Image.new("RGBA", atlas.size) if pose == "idle" else None
    head_atlas = Image.new("RGBA", atlas.size) if pose == "idle" else None
    frames = []
    for index in range(count):
        rendered = render_frame(pose, index)
        if pose == "idle":
            image, body, head = rendered
            position = ((index % columns) * FRAME_W, (index // columns) * FRAME_H)
            body_atlas.alpha_composite(body, position)
            head_atlas.alpha_composite(head, position)
        else:
            image = rendered
        frames.append(image)
        atlas.alpha_composite(image, ((index % columns) * FRAME_W, (index // columns) * FRAME_H))
    atlas.save(PET / f"{pose}_motion.png", optimize=True)
    if pose == "idle":
        body_atlas.save(PET / "idle_body_motion.png", optimize=True)
        head_atlas.save(PET / "idle_head_motion.png", optimize=True)
    all_frames[pose] = frames
    print(f"{pose}: {count} frames; {columns} columns")

review = []
for pose in ("idle", "wave", "think", "happy"):
    review.extend(all_frames[pose])
review[0].save(HERE / "motion_review.gif", save_all=True,
               append_images=review[1:], duration=round(1000 / FPS),
               loop=0, disposal=2, optimize=True)

samples = (("idle", 0), ("idle", 45), ("wave", 16),
           ("think", 28), ("happy", 20))
contact = Image.new("RGB", (len(samples) * FRAME_W, FRAME_H), (255, 255, 255))
for position, (pose, index) in enumerate(samples):
    image = all_frames[pose][index]
    contact.paste(image, (position * FRAME_W, 0), image)
contact.save(HERE / "motion_contact.png")
