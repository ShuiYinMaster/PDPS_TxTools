"""Cross-language verification of native files and generated container layouts.

Run after building CgrContainer.csproj and baseline/current CgrLineFace runners.
Each output directory must be new. No source CGR is overwritten.
"""
import argparse
import hashlib
import json
import subprocess
import struct
from pathlib import Path

from cfv2 import parse, refragment


def run(*args):
    result = subprocess.run(args, text=True, encoding="utf-8", errors="replace", capture_output=True, timeout=180)
    if result.returncode:
        raise RuntimeError(f"{args}\n{result.stdout}\n{result.stderr}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("workspace", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--probe", type=Path, required=True)
    parser.add_argument("--baseline", type=Path)
    parser.add_argument("--current", type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    report = {"native": [], "fragmentation": [], "encoder_parity": [], "catia_verified": False}
    native = [args.workspace / name for name in ("Part1.cgr", "three3.cgr", "gt_N128.cgr",
              "FFM170-X-0417-6-CLOSE-A3-2024.11.14.cgr")]
    native += sorted((args.workspace / "catia_to_jt/research").glob("fixtures*/*.cgr"))
    native += [args.workspace / "ffm170_r55_final_catia_20261012/FFM170-X-0417-6-CLOSE-A3-2024.11.14.cgr"]
    for path in native:
        c = parse(path.read_bytes())
        assert c.rebuilt() == c.data, path
        report["native"].append({"file": str(path), **c.summary()})

    # The standalone project compiles CgrContainer.cs without geometry sources.
    scene, picks = bytes((i * 37 + 13) % 256 for i in range(1025)), bytes(range(256)) + b"\xff"
    scene_path, picks_path = args.output / "scene.bin", args.output / "picks.bin"
    scene_path.write_bytes(scene)
    picks_path.write_bytes(picks)
    for a, b in ((1, 1), (2, 7), (26, 25), (127, 129)):
        path = args.output / f"synthetic_{a}_{b}.cgr"
        run("rtk", "dotnet", str(args.probe), str(scene_path), str(picks_path), str(path), str(a), str(b))
        c = parse(path.read_bytes())
        assert c.payload(c.nodes[11]) == scene and c.payload(c.nodes[12]) == picks
        assert len(c.nodes[11].extents) == a and len(c.nodes[12].extents) == b
        assert c.directory_size == 2704 + 20 * (a + b - 2)
        assert c.rebuilt() == c.data
        bounds = struct.unpack_from("<6f", c.payload(c.nodes[8]), 27)
        assert bounds == (-10, -20, -30, 40, 50, 60)
        report["fragmentation"].append({"scene_fragments": a, "pick_fragments": b, **c.summary()})

    original = (args.workspace / "FFM170-X-0417-6-CLOSE-A3-2024.11.14.cgr").read_bytes()
    candidate = refragment(original, {12: 1, 13: 1})
    candidate_path = args.output / "FFM170_structured_single_extent_experimental.cgr"
    candidate_path.write_bytes(candidate)
    report["native_refragmentation"] = {"output": str(candidate_path), "saved_directory_bytes": len(original) - len(candidate),
                                        "all_logical_streams_identical": True, "catia_verified": False,
                                        **parse(candidate).summary()}

    if bool(args.baseline) != bool(args.current):
        raise ValueError("Both baseline/current runners are required for parity")
    if args.baseline:
        fixtures = args.workspace / "analysis_cgr/selection_20260920/integration_v1/fixtures"
        names = ["box", "color_box", "cylinder", "far_box", "open_quad", "reversed_box",
                 "touching_boxes", "nonmanifold", "duplicates", "large_grid", "wide_grid"]
        for name in names:
            for mode in ("compact", "features", "features-planar"):
                before = args.output / f"{name}_{mode}_before.cgr"
                after = args.output / f"{name}_{mode}_after.cgr"
                for runner, path in ((args.baseline, before), (args.current, after)):
                    run("rtk", "dotnet", str(runner), str(fixtures / f"{name}.json"), str(path), mode)
                old, new = before.read_bytes(), after.read_bytes()
                assert old == new, f"Encoder bytes changed: {name}/{mode}"
                c = parse(new)
                assert c.rebuilt() == new
                report["encoder_parity"].append({"model": name, "mode": mode, "bytes": len(new),
                                                "sha256": hashlib.sha256(new).hexdigest(), "byte_exact": True})
                print(f"PASS parity {name}/{mode}", flush=True)

        for mode in ("legacy", "build-api", "rgba-compact", "rgba-features", "rgba-features-planar"):
            paths = [args.output / f"coverage_{mode}_{label}.cgr" for label in ("before", "after")]
            for runner, path in zip((args.baseline, args.current), paths):
                run("rtk", "dotnet", str(args.probe), "--encode", str(runner), str(path), mode)
            old, new = [p.read_bytes() for p in paths]
            assert old == new, f"Encoder interface bytes changed: {mode}"
            assert parse(new).rebuilt() == new
            report["encoder_parity"].append({"model": "tetra_rgba_api", "mode": mode, "bytes": len(new),
                                            "sha256": hashlib.sha256(new).hexdigest(), "byte_exact": True})
            print(f"PASS parity api/{mode}", flush=True)

    path = args.output / "verification.json"
    path.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps({"native_roundtrips": len(report["native"]),
                      "fragment_layouts": len(report["fragmentation"]),
                      "encoder_parity": len(report["encoder_parity"]), "report": str(path)}))


if __name__ == "__main__":
    main()
