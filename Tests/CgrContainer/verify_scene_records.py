"""Independent scene roundtrips and cross-runtime C# field profile parity."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import subprocess

from cfv2 import parse
from cfv2_metadata import named_node
from scene_records import parse_scene


def scene_and_picks(path):
    c = parse(path.read_bytes())
    parent = named_node(c, c.nodes[1].id, "SceneGraph")
    scene = c.payload(named_node(c, parent.id, "MainDataStream"))
    picks = [n for n in c.nodes if n.parent == parent.id and n.name == "SurfacicReps"]
    return scene, c.payload(picks[0]) if picks else b""


def main():
    p = argparse.ArgumentParser()
    p.add_argument("workspace", type=Path)
    p.add_argument("output", type=Path)
    p.add_argument("--probe", type=Path, required=True)
    p.add_argument("--net48-probe", type=Path, required=True)
    args = p.parse_args()
    root, output = args.workspace.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    research = root / "analysis_cgr/scene_20261016_r5"
    report = {"scene_roundtrips": [], "cross_runtime_profiles": [], "geometry_payloads_interpreted": False}
    paths = sorted((research / "native").glob("*.cgr"))
    paths += sorted((research / "verification_final").glob("*_after.cgr"))
    paths += [root / n for n in ("Part1.cgr", "three3.cgr", "gt_N128.cgr", "FFM170-X-0417-6-CLOSE-A3-2024.11.14.cgr")]
    paths += sorted((root / "catia_to_jt/research").glob("fixtures*/*.cgr"))
    for path in paths:
        data, _ = scene_and_picks(path)
        s = parse_scene(data)
        assert s.encode() == data
        report["scene_roundtrips"].append({"file": str(path), "bytes": len(data), "nodes": len(s.nodes),
                                            "matrices": sum(bool(n.matrix) for n in s.nodes),
                                            "references": sum(not l.definition for n in s.nodes for l in n.children),
                                            "sha256": hashlib.sha256(data).hexdigest()})
    cases = []
    data, _ = scene_and_picks(research / "verification_final/color_box_features_after.cgr")
    s = parse_scene(data)
    shells = [n for n in s.nodes if n.tag == 0xA3]
    for label, shell in (("feature_first", shells[0]), ("feature_last", shells[-1])):
        leaf = next(n for n in s.nodes if n.tag == 0x95 and shell.offset < n.offset < shell.end)
        fields = [leaf.header.radius, *leaf.header.center]
        cases.append((label, "feature", leaf.geometry, fields, leaf.pick_offset, data[shell.offset:shell.end]))
    data, _ = scene_and_picks(research / "verification_final/coverage_build-api_after.cgr")
    s = parse_scene(data)
    leaf = next(n for n in s.nodes if n.tag == 0x95)
    cases.append(("legacy", "legacy", leaf.geometry, [leaf.header.radius, *leaf.header.center], 0, data))
    data, picks = scene_and_picks(research / "verification_final/color_box_compact_after.cgr")
    leaves = [n for n in parse_scene(data).nodes if n.tag == 0x95]
    for label, leaf in (("compact_first", leaves[0]), ("compact_last", leaves[-1])):
        bounds = struct.unpack_from("<6f", picks, leaf.pick_offset + 20)
        cases.append((label, "leaf", leaf.geometry, bounds, leaf.pick_offset, data[leaf.offset:leaf.end]))
        cases.append((label + "_pick", "pick", leaf.geometry, bounds, 0, picks[leaf.pick_offset:leaf.pick_offset + 61]))
    for label, mode, payload, fields, pick_offset, expected in cases:
        payload_path = output / (label + ".payload")
        payload_path.write_bytes(payload)
        outputs = []
        for runtime, prefix in (("net8", ["rtk", "dotnet", str(args.probe.resolve())]),
                                ("net48", ["rtk", "proxy", str(args.net48_probe.resolve())])):
            target = output / (label + "." + runtime)
            command = prefix + [mode, str(payload_path), str(target), ",".join(map(str, fields)), str(pick_offset)]
            result = subprocess.run(command, text=True, encoding="utf-8", errors="replace", capture_output=True, timeout=30)
            if result.returncode:
                raise RuntimeError(result.stdout + result.stderr)
            generated = target.read_bytes()
            assert generated == expected, (label, runtime)
            outputs.append(generated)
        assert outputs[0] == outputs[1]
        report["cross_runtime_profiles"].append({"label": label, "mode": mode, "bytes": len(expected),
                                                 "pick_offset": pick_offset, "byte_exact": True})
        print(f"PASS scene fields net8/net48: {label}", flush=True)
    (output / "scene_verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"scene_roundtrips": len(report["scene_roundtrips"]), "cross_runtime_profiles": len(cases)}))


if __name__ == "__main__":
    main()
