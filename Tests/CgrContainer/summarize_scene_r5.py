"""Summarize R5 scene evidence, deriving removed image sizes from snapshots."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import re


def main():
    p = argparse.ArgumentParser()
    p.add_argument("research", type=Path)
    args = p.parse_args()
    root = args.research.resolve()

    def read(relative):
        return json.loads((root / relative).read_text(encoding="utf-8"))

    native = read("native/native_scene_fixtures.json")
    scenes = read("scene_verification_final/scene_verification.json")
    parity = read("verification_final/verification.json")
    com = read("com_acceptance/catia_scene_acceptance.json")
    assert not native["errors"] and not com["errors"]
    assert native["document_count_before"] == native["document_count_after"]
    assert com["document_count_before"] == com["document_count_after"]
    images = {}
    before = (root / "before/CgrTemplate.cs").read_text(encoding="utf-8-sig")
    for name, encoded in re.findall(r'byte\[\] (\w+) = Convert.FromBase64String\("([^"]+)"', before):
        images[name] = len(base64.b64decode(encoded))
    before = (root / "before/CgrCompact95.cs").read_text(encoding="utf-8-sig")
    for name, encoded in re.findall(r'byte\[\] (\w+)=H95\("([^"]+)"', before):
        images[name] = len(bytes.fromhex(encoded))
    comparisons = []
    for path in sorted((root / "verification_final").glob("*_after.cgr")):
        final = path.read_bytes()
        assert final == (root / "verification" / path.name).read_bytes()
        assert final == path.with_name(path.name.replace("_after.cgr", "_before.cgr")).read_bytes()
        comparisons.append({"file": str(path), "sha256": hashlib.sha256(final).hexdigest()})
    assert len(comparisons) == 38
    ffm = next(row for row in scenes["scene_roundtrips"] if Path(row["file"]).name.startswith("FFM170-"))
    summary = {
        "scene_record_version": "CGRSCENE-20261016-R5-fields",
        "container_version": "CFV2-20261016-R4-counted-identity",
        "removed_fixed_images": images,
        "removed_fixed_image_count": len(images),
        "removed_fixed_image_bytes": sum(images.values()),
        "native_controlled_states": len(native["fixtures"]),
        "scene_field_roundtrips": len(scenes["scene_roundtrips"]),
        "ffm170_tree": {key: ffm[key] for key in ("nodes", "matrices", "references")},
        "net8_net48_profiles": len(scenes["cross_runtime_profiles"]),
        "encoder_byte_exact_parity": len(parity["encoder_parity"]),
        "legacy_container_roundtrips": len(parity["native"]),
        "fragment_layouts": len(parity["fragmentation"]),
        "com_pairs": len(com["pairs"]),
        "com_products": len(com["pairs"]) * 2,
        "com_zero_volume_cases": [{"model": row["model"], "mode": row["mode"]} for row in com["pairs"]
                                  if row["volumes_mm3"] == [0.0, 0.0]],
        "com_candidate_bytes_equal_final": comparisons,
        "document_counts_restored": True,
        "visually_inspected_captures": ["com_acceptance/color_box_compact.png", "com_acceptance/cylinder_features.png"],
        "deployed_to_ps": False,
        "remaining": ["semantics of several common header flags/scalars and control slots",
                      "complete identifier allocation rules", "fixed compatible tree profile choices",
                      "some Header/Describe controls", "full geometry and adhesion semantics; CFV3 separately"],
    }
    (root / "R5-summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps({key: value for key, value in summary.items() if key != "com_candidate_bytes_equal_final"}))


if __name__ == "__main__":
    main()
