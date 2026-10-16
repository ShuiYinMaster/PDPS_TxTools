"""Compare C# field serialization against native COM headers, not constants."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

from cfv2 import parse
from cfv2_metadata import audit, records


def main():
    p = argparse.ArgumentParser()
    p.add_argument("fixtures", type=Path)
    p.add_argument("output", type=Path)
    p.add_argument("--probe", type=Path, required=True)
    args = p.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    labels = ["solid_baseline"] + ["setting_" + name for name in (
        "Viz3DFixedAccuracy", "Viz3DProportionnalAccuracy", "Viz3DAccuracyMode", "Viz3DCurveAccuracy",
        "Viz2DFixedAccuracy", "Viz2DProportionnalAccuracy", "Viz2DAccuracyMode")]
    report = {"native_header_parity": [], "invalid_accuracy_rejected": []}
    for label in labels:
        native = parse((args.fixtures / (label + ".cgr")).read_bytes())
        _, hn, header, _, _ = records(native)
        scene, picks = (native.payload(native.nodes[i]) for i in (11, 12))
        scene_file, picks_file = args.output / (label + ".scene"), args.output / (label + ".picks")
        scene_file.write_bytes(scene)
        picks_file.write_bytes(picks)
        output = args.output / (label + ".cgr")
        fields = [str(bool(header.viz3d_accuracy_mode)), str(header.viz3d_accuracy), str(header.viz3d_curve_accuracy),
                  str(bool(header.viz2d_accuracy_mode)), str(header.viz2d_accuracy)]
        command = ["rtk", "dotnet", str(args.probe), str(scene_file), str(picks_file), str(output), "1", "1",
                   native.nodes[1].name + "@" + str(header.unix_seconds), ",".join(map(str, header.bounds)), "true", ",".join(fields)]
        result = subprocess.run(command, text=True, capture_output=True, timeout=30)
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)
        generated = parse(output.read_bytes())
        assert generated.payload(records(generated)[1]) == native.payload(hn), label
        assert generated.payload(generated.nodes[11]) == scene and generated.payload(generated.nodes[12]) == picks
        assert generated.rebuilt() == generated.data and audit(generated)["three_way_timestamp_match"]
        report["native_header_parity"].append({"label": label, "header_byte_exact": True,
                                               "geometry_and_pick_streams_byte_exact": True,
                                               "sha256": hashlib.sha256(generated.data).hexdigest()})
        print(f"PASS native Header parity: {label}", flush=True)
    for index, value in enumerate(("NaN", "Infinity", "-1", "0")):
        invalid = command.copy()
        invalid[5] = str(args.output / f"invalid_{index}.cgr")
        invalid[-1] = "true," + value + ",1,true,0.02"
        result = subprocess.run(invalid, text=True, capture_output=True, timeout=30)
        assert result.returncode != 0 and "finite and positive" in result.stderr, result.stderr
        assert not Path(invalid[5]).exists()
        report["invalid_accuracy_rejected"].append(value)
    (args.output / "visualization_verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"native_header_parity": len(report["native_header_parity"]),
                      "invalid_accuracy_rejected": len(report["invalid_accuracy_rejected"])}))


if __name__ == "__main__":
    main()
