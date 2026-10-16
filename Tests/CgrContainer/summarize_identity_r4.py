"""Recheck native evidence and summarize R4 without editing raw reports."""
import argparse
from collections import Counter
import json
from pathlib import Path

from cfv2 import parse
from cfv2_metadata import audit


def main():
    p = argparse.ArgumentParser()
    p.add_argument("research", type=Path)
    args = p.parse_args()
    root = args.research.resolve()

    def read(relative):
        return json.loads((root / relative).read_text(encoding="utf-8"))

    native = read("native_identity/native_identity_boundaries.json")
    generated = read("identity_verification/identity_verification.json")
    catia = read("com_identity/catia_accuracy_acceptance.json")
    verification = read("verification/verification.json")
    metadata = read("metadata/metadata_verification.json")
    assert not native["errors"] and not catia["errors"]
    assert native["document_count_restored"]
    assert catia["document_count_before"] == catia["document_count_after"]
    identities = []
    for fixture in native["fixtures"]:
        c = parse((root / "native_identity" / (fixture["label"] + ".cgr")).read_bytes())
        a = audit(c)
        assert a["three_way_timestamp_match"] and c.rebuilt() == c.data
        assert c.nodes[1].name == fixture["actual_name"]
        identities.append(a["identity"])
    for profile in generated["profiles"]:
        c = parse(Path(profile["generated"]).read_bytes())
        assert c.nodes[1].name == profile["identity"] and audit(c)["three_way_timestamp_match"]
        assert profile["cross_runtime_byte_exact"]
    sequence = [identity["word3"] for identity in identities[:272]]
    deltas = Counter(b - a for a, b in zip(sequence, sequence[1:]))
    assert deltas == {1: 271}
    summary = {
        "version": "CFV2-20261016-R4-counted-identity",
        "root_name_count": "uint32 big-endian byte count including UTF-16BE terminator",
        "actual_name_prefix": ".",
        "identity_encoding": "four byte-swapped uint32 values rendered in unpadded hex and concatenated",
        "misread_D_B_at_corrected": True,
        "native_exports": len(identities),
        "native_name_byte_counts": native["name_byte_counts"],
        "counter_low_bytes_observed": len(native["counter_low_bytes_observed"]),
        "consecutive_counter_deltas": dict(deltas),
        "cnext_pids": native["cnext_pids"],
        "identity_process_ids_observed": sorted(set(i["process_id"] for i in identities)),
        "generated_native_identity_profiles": len(generated["profiles"]),
        "net8_net48_byte_exact": True,
        "catia_products": len(catia["products"]),
        "catia_volumes_mm3": sorted(set(p["volume_mm3"] for p in catia["products"])),
        "catia_document_counts_restored": True,
        "encoder_byte_exact_parity": len(verification["encoder_parity"]),
        "legacy_roundtrips": len(verification["native"]),
        "fragment_layouts": len(verification["fragmentation"]),
        "metadata_survey": metadata["counts"],
        "legacy_identity_profiles": len(metadata["generated"]),
        "r3_summary_correction": "Original preserved; 48 states re-audited as linked instead of assuming 14 B states",
        "geometry_profiles_changed": False,
        "deployed_to_ps": False,
        "remaining": ["identity word0 and complete allocation rules", "some Header and directory controls",
                      "Describe remaining bits and opaque tail", "geometry profiles and internal adhesion payloads"],
    }
    (root / "R4-summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps(summary))


if __name__ == "__main__":
    main()
