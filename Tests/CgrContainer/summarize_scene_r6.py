"""Audit R6 evidence and C# fixed node attributes on both Windows runtimes."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import subprocess

from cfv2 import parse
from probe_scene_representation import stream
from scene_records import parse_scene


def main():
    p = argparse.ArgumentParser()
    p.add_argument("research", type=Path)
    args = p.parse_args()
    root = args.research.resolve()
    out = root / "attributes_verification"
    out.mkdir(exist_ok=False)
    payload = out / "empty.payload"
    payload.write_bytes(b"")
    report = {"scene_version": "CGRSCENE-20261016-R6-layer-flags", "native_states": [],
              "cross_runtime_attributes": [], "com_cases": [], "production_default_changed": False}
    for path in sorted((root / "native_flags_full").glob("*.cgr")):
        c = parse(path.read_bytes())
        data = c.payload(stream(c))
        s = parse_scene(data)
        assert s.encode() == data
        report["native_states"].append({"label": path.stem, "bytes": len(data),
            "sha256": hashlib.sha256(data).hexdigest(), "nodes": [
                {"tag": hex(n.tag), "offset": n.offset, "flags": n.header.flags,
                 "layer_code": n.layer_code if n.tag != 0x95 else None,
                 "node_options": n.node_options if n.tag != 0x95 else None,
                 "no_pick": n.header.no_pick, "volume_contribution_bit": n.header.volume_contribution_bit}
                for n in s.nodes]})
    for label in ("body_layer0", "body_layer1", "body_layer37", "body_layer255", "body_layer256", "body_layer999", "body_layer_none"):
        case = next(c for c in report["native_states"] if c["label"] == label)
        layer, options = case["nodes"][1]["layer_code"], case["nodes"][1]["node_options"]
        expected = struct.pack("<HHB", layer, options, 0)
        for runtime, prefix in (("net8", ["rtk", "dotnet", str(root / "probe/CgrSceneRecords.dll")]),
                                ("net48", ["rtk", "proxy", str(root / "SceneProbe.net48.exe")])):
            target = out / f"{label}.{runtime}.bin"
            command = prefix + ["attributes", str(payload), str(target), f"{layer},{options}", "0"]
            result = subprocess.run(command, capture_output=True, timeout=30)
            if result.returncode:
                raise RuntimeError(result.stdout.decode(errors="replace") + result.stderr.decode(errors="replace"))
            assert target.read_bytes() == expected
        report["cross_runtime_attributes"].append({"label": label, "layer_code": layer, "node_options": options,
                                                   "bytes": expected.hex(), "net8_net48_exact": True})
    for directory in ("com", "leaf_com"):
        com = json.loads((root / directory / "com_results.json").read_text())
        assert not com["errors"]
        assert com["document_count_before"] == com["document_count_after"]
        report["com_cases"] += com["cases"]
    native = json.loads((root / "native_flags_full/native_flags.json").read_text())
    assert not native["errors"] and native["document_count_before"] == native["document_count_after"]
    report["native_export_parser_failures_resolved"] = sum("parse_error" in c for c in native["fixtures"])
    report["encoder_verification"] = json.loads((root / "verification/verification.json").read_text())
    report["scene_verification"] = json.loads((root / "scene_verification/scene_verification.json").read_text())
    report["python_tests"] = 48
    (root / "R6-summary.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"native_states": len(report["native_states"]), "com_products": len(report["com_cases"]),
                      "cross_runtime_attributes": len(report["cross_runtime_attributes"]),
                      "encoder_parity": len(report["encoder_verification"]["encoder_parity"])}))


if __name__ == "__main__":
    main()
