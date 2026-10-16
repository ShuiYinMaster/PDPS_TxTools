"""Audit native links and cross-check opt-in identity changes from C#.

Generated identity changes are research-only. This script never starts CATIA.
"""
import argparse
from dataclasses import replace
import hashlib
import json
from pathlib import Path

from cfv2 import parse
from cfv2_metadata import StorageIdentity, audit, records
from verify import run


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("workspace", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--probe", type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    report = {"survey": [], "generated": [], "catia_verified": False}
    candidates = list(args.workspace.glob("*.cgr")) + list((args.workspace / "analysis_cgr").glob("*.cgr"))
    candidates += list((args.workspace / "catia_to_jt/research").glob("fixtures*/*.cgr"))
    candidates += [args.workspace / "ffm170_r55_final_catia_20261012/FFM170-X-0417-6-CLOSE-A3-2024.11.14.cgr"]
    for path in sorted(set(candidates)):
        item = {"file": str(path), "provenance": "mixed historical/native/experimental; not all are native"}
        if path.stat().st_size > 64000000:
            item.update(status="skipped", reason="Over 64 MB survey limit")
        else:
            try:
                c = parse(path.read_bytes())
            except ValueError as error:
                item.update(status="unsupported_container", reason=str(error))
            else:
                try:
                    a = audit(c)
                except (ValueError, OverflowError) as error:
                    item.update(status="unsupported_metadata", reason=str(error))
                else:
                    item.update(status="linked" if a["three_way_timestamp_match"] else "mismatch", **a)
        report["survey"].append(item)

    # Use a genuine native box's geometry/picks, with a completely generated
    # C# container. Do not bring forward any directory bytes from that file.
    source = parse((args.workspace / "Part1.cgr").read_bytes())
    _, _, header, _, _ = records(source)
    scene, picks = source.payload(source.nodes[11]), source.payload(source.nodes[12])
    scene_path, picks_path = args.output / "scene.bin", args.output / "picks.bin"
    scene_path.write_bytes(scene)
    picks_path.write_bytes(picks)
    accepted = StorageIdentity.parse(".4eb51e58200d0000d713986aea230000")
    identities = {
        "box_default": accepted,
        "box_sequence": replace(accepted, word3=accepted.word3 + 4096),
        "box_clock": replace(accepted, unix_seconds=accepted.unix_seconds + 3600, word3=accepted.word3 + 8192),
        "box_session": replace(accepted, word1=accepted.word1 + 1, word3=accepted.word3 + 12288),
        "box_clock_single_extent": replace(accepted, unix_seconds=accepted.unix_seconds + 3600, word3=accepted.word3 + 16384),
        "box_epoch_zero": replace(accepted, unix_seconds=0, word3=0xFFFFFFFF),
        "box_epoch_uint32_max": replace(accepted, unix_seconds=0xFFFFFFFF, word3=0),
    }
    base = None
    for label, identity in identities.items():
        a, b = (1, 1) if label.endswith("single_extent") else (26, 25)
        path = args.output / f"{label}.cgr"
        run("rtk", "dotnet", str(args.probe), str(scene_path), str(picks_path), str(path), str(a), str(b),
            identity.encode() + "@" + str(identity.unix_seconds), ",".join(str(v) for v in header.bounds), "true")
        c = parse(path.read_bytes())
        result = audit(c)
        assert result["three_way_timestamp_match"]
        assert StorageIdentity.parse(c.nodes[1].name, records(c)[2].unix_seconds) == identity
        assert c.payload(c.nodes[11]) == scene and c.payload(c.nodes[12]) == picks
        assert records(c)[2].bounds == header.bounds
        assert c.rebuilt() == c.data
        if base is None:
            base = c
        else:
            delta = (identity.unix_seconds - accepted.unix_seconds) * 10000000
            for old, new in zip(base.nodes[1:], c.nodes[1:]):
                for attr in ("created", "time2", "time3"):
                    before, after = getattr(old.metadata, attr), getattr(new.metadata, attr)
                    assert after == (before + delta if before else 0), (label, new.id, attr)
            for old, new in zip(base.nodes, c.nodes):
                if new.id != 9:
                    assert base.payload(old) == c.payload(new), (label, new.id)
        report["generated"].append({"file": str(path), "label": label,
                                    "sha256": hashlib.sha256(c.data).hexdigest(), "scene_fragments": a,
                                    "pick_fragments": b, "all_geometry_pick_bytes_unchanged": True,
                                    "catia_verified": False, **result})
        print(f"PASS C# identity {label}", flush=True)

    counts = {}
    for item in report["survey"]:
        counts[item["status"]] = counts.get(item["status"], 0) + 1
    report["counts"] = counts
    target = args.output / "metadata_verification.json"
    target.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps({"survey": counts, "identity_profiles": len(report["generated"]), "report": str(target)}))


if __name__ == "__main__":
    main()
