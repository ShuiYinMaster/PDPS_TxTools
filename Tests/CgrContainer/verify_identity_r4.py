"""C# counted/unpadded name output parity against controlled native identities."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import subprocess

from cfv2 import parse
from cfv2_metadata import StorageIdentity, audit, records


def main():
    p = argparse.ArgumentParser()
    p.add_argument("workspace", type=Path)
    p.add_argument("output", type=Path)
    p.add_argument("--probe", type=Path, required=True)
    p.add_argument("--net48-probe", type=Path)
    args = p.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    root = args.workspace.resolve()
    native_folder = root / "analysis_cgr/container_20261016_r4/native_identity"
    r = json.loads((native_folder / "native_identity_boundaries.json").read_text(encoding="utf-8"))
    paths = [root / name for name in ("cyl2.cgr", "gt_N60.cgr", "loft.cgr", "tri3.cgr")]
    widths = set()
    for fixture in r["fixtures"]:
        if fixture["name_byte_count"] not in widths:
            paths.append(native_folder / (fixture["label"] + ".cgr"))
            widths.add(fixture["name_byte_count"])
    # Native box geometry keeps identity experiments independently loadable.
    box = parse((root / "Part1.cgr").read_bytes())
    scene, picks = box.payload(box.nodes[11]), box.payload(box.nodes[12])
    (output / "scene.bin").write_bytes(scene)
    (output / "picks.bin").write_bytes(picks)
    bounds = ",".join(map(str, records(box)[2].bounds))
    report = {"profiles": [], "ambiguous_inputs_rejected": []}
    for index, native_path in enumerate(paths):
        native = parse(native_path.read_bytes())
        h = records(native)[2]
        identity = StorageIdentity.parse(native.nodes[1].name, h.unix_seconds)
        outputs = []
        runners = [("net8", ["rtk", "dotnet", str(args.probe.resolve())])]
        if args.net48_probe:
            runners.append(("net48", ["rtk", "proxy", str(args.net48_probe.resolve())]))
        for label, command in runners:
            target = output / f"{index:02d}_{native_path.stem}_{label}.cgr"
            command += [str(output / "scene.bin"), str(output / "picks.bin"), str(target), "1", "1",
                        identity.encode() + "@" + str(identity.unix_seconds), bounds, "true"]
            result = subprocess.run(command, text=True, encoding="utf-8", errors="replace", capture_output=True, timeout=30)
            if result.returncode:
                raise RuntimeError(result.stdout + result.stderr)
            c = parse(target.read_bytes())
            assert c.nodes[1].name == native.nodes[1].name
            assert StorageIdentity.parse(c.nodes[1].name, records(c)[2].unix_seconds) == identity
            assert audit(c)["three_way_timestamp_match"] and c.rebuilt() == c.data
            assert c.payload(c.nodes[11]) == scene and c.payload(c.nodes[12]) == picks
            count_offset = c.directory_offset + 64 + 86 + 84
            count = struct.unpack_from(">I", c.data, count_offset)[0]
            assert count == len((identity.encode() + "\0").encode("utf-16be"))
            outputs.append(target.read_bytes())
        assert all(data == outputs[0] for data in outputs)
        report["profiles"].append({"native": str(native_path), "identity": identity.encode(),
                                    "name_byte_count": count, "cross_runtime_byte_exact": bool(args.net48_probe),
                                    "generated": str(output / f"{index:02d}_{native_path.stem}_net8.cgr"),
                                    "sha256": hashlib.sha256(outputs[0]).hexdigest()})
        print(f"PASS counted identity: {native_path.name}, name bytes={count}", flush=True)
    # No timestamp context: a short ambiguous name must be refused, not guessed.
    command = ["rtk", "dotnet", str(args.probe.resolve()), str(output / "scene.bin"), str(output / "picks.bin"),
               str(output / "ambiguous.cgr"), "1", "1", ".4eb51e589c3a00006e9936a180f0000"]
    result = subprocess.run(command, text=True, encoding="utf-8", errors="replace", capture_output=True, timeout=30)
    assert result.returncode != 0 and "unambiguous timestamp context" in result.stderr
    assert not (output / "ambiguous.cgr").exists()
    report["ambiguous_inputs_rejected"].append(command[-1])
    (output / "identity_verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"native_identity_profiles": len(report["profiles"]), "net48_net8_byte_exact": bool(args.net48_probe)}))


if __name__ == "__main__":
    main()
