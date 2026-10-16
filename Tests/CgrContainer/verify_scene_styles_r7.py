"""Native variable common-header decoding and C# parity across two runtimes."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

from scene_bounds import merge_spheres
from scene_records import parse_scene


def main():
    p = argparse.ArgumentParser()
    p.add_argument("research", type=Path)
    p.add_argument("output", type=Path)
    args = p.parse_args()
    root, output = args.research.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    payload = output / "empty.payload"
    payload.write_bytes(b"")
    report = {"scene_version": "CGRSCENE-20261016-R7-variable-attributes", "native": [], "bounds": [], "cross_runtime": []}
    for directory in ("native", "style_ranges", "width_upper"):
        original = json.loads((root / directory / "native_inheritance.json").read_text())
        assert not original["errors"]
        assert original["document_count_before"] == original["document_count_after"]
        for path in sorted((root / directory).glob("*.scene")):
            data = path.read_bytes()
            scene = parse_scene(data)
            assert scene.encode() == data
            header = scene.root.header if path.stem.startswith(("box_", "root_")) else scene.nodes[2].header
            name = directory + "_" + path.stem
            item = {"case": name, "bytes": len(data), "nodes": len(scene.nodes),
                    "header_bytes": header.encoded_size, "inheritance_mask": header.inheritance_mask,
                    "graphics_style": header.graphics_style, "line_width_code": header.line_width_code,
                    "line_type_code": header.line_type_code, "rgb": header.explicit_rgb,
                    "opacity": header.opacity, "translucent": header.translucent,
                    "sha256": hashlib.sha256(data).hexdigest()}
            report["native"].append(item)
            fields = [header.inheritance_mask, header.flags[1], header.flags[2], *header.encoding,
                      header.attribute_word_be, header.field11, header.radius, header.scalar16,
                      *header.center, header.object_id]
            for runtime, prefix in (("net8", ["rtk", "dotnet", str(root / "probe/CgrSceneRecords.dll")]),
                                    ("net48", ["rtk", "proxy", str(root / "SceneProbe.net48.exe")])):
                target = output / f"{name}.{runtime}.bin"
                command = prefix + ["common", str(payload), str(target), ",".join(map(str, fields)), "0"]
                result = subprocess.run(command, capture_output=True, timeout=30)
                if result.returncode:
                    raise RuntimeError(result.stdout.decode(errors="replace") + result.stderr.decode(errors="replace"))
                assert target.read_bytes() == header.encode(), (name, runtime)
            report["cross_runtime"].append({"case": name, "header_bytes": header.encoded_size, "byte_exact": True})
            if path.stem.startswith("box_"):
                leaf = next(n for n in scene.nodes if n.tag == 0x95)
                center, radius = merge_spheres((0, 0, 0), 0, leaf.header.center, leaf.header.radius)
                errors = [abs(a - b) for a, b in zip(center, scene.root.header.center)] + [abs(radius - scene.root.header.radius)]
                assert max(errors) < 2e-5, (name, errors)
                report["bounds"].append({"case": name, "max_absolute_error": max(errors),
                                         "hypothesis": "origin point union with leaf sphere"})
            print(f"PASS native/header net8/net48 {name}: {header.encoded_size} bytes", flush=True)
    (output / "scene_styles_verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"native_states": len(report["native"]), "cross_runtime": len(report["cross_runtime"]), "bounds": len(report["bounds"])}))


if __name__ == "__main__":
    main()
