"""Refresh the editable rig bundle and its current 2D runtime atlases."""

from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED

rig = Path(__file__).resolve().parent
pet = rig.parent
archive = rig / "whale_pet_live2d_layers.zip"
former = []
if archive.exists():
    with ZipFile(archive) as source:
        former = [name for name in source.namelist() if not name.startswith("runtime/")]

extras = [
    "build_mouse_gaze.py", "qa_mouse_follow.py", "qa_blink.py",
    "package_layers.py",
    "mouse_follow_review.png", "blink_top_down_review.png",
]
atlases = [
    "idle_motion.png", "idle_body_motion.png", "idle_head_motion.png",
    "gaze_motion.png", "wave_motion.png", "think_motion.png", "happy_motion.png",
]
rig_files = sorted(name for name in set(former + extras) if (rig / name).is_file())
with ZipFile(archive, "w", ZIP_DEFLATED, compresslevel=6) as target:
    for name in rig_files:
        path = rig / name
        if path.is_file():
            target.write(path, name)
    for name in atlases:
        target.write(pet / name, "runtime/" + name)
print(archive, "files:", len(rig_files) + len(atlases))
