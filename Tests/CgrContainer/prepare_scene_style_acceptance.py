"""Reinsert C#-serialized common headers into typed native scene trees."""
import argparse
import json
from pathlib import Path

from cfv2 import parse
from probe_scene_representation import replace_scene, stream
from scene_records import CommonHeader, Cursor, parse_scene


def main():
    p = argparse.ArgumentParser()
    p.add_argument("research", type=Path)
    p.add_argument("output", type=Path)
    args = p.parse_args()
    root, output = args.research.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = {"method": "C# common header reinsertion and complete typed scene reconstruction", "variants": []}
    for directory, label in (("native", "assembly_baseline"), ("native", "instance_color_inherit1"),
                             ("style_ranges", "instance_combined"), ("style_ranges", "instance_width16")):
        c = parse((root / directory / (label + ".cgr")).read_bytes())
        data = c.payload(stream(c))
        scene = parse_scene(data)
        node = scene.nodes[2]
        header = (root / "field_verification" / f"{directory}_{label}.net8.bin").read_bytes()
        cursor = Cursor(header)
        node.header = CommonHeader.read(cursor, len(header))
        assert cursor.pos == len(header)
        generated = scene.encode()
        assert generated == data
        path = output / (label + ".cgr")
        path.write_bytes(replace_scene(c, generated))
        report["variants"].append({"model": "native_box_pair", "variant": label, "path": str(path),
                                   "scene_byte_exact": True, "header_runtime": "net8",
                                   "capture": label == "instance_color_inherit1"})
    (output / "variants.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"variants": len(report["variants"])}))


if __name__ == "__main__":
    main()
