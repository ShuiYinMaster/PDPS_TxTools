"""Consolidate the final R3 evidence without replacing raw experimental reports."""
import argparse
import hashlib
import json
from pathlib import Path

from cfv2 import parse
from cfv2_metadata import applicative_records, audit, records


def main():
    p = argparse.ArgumentParser()
    p.add_argument("research", type=Path)
    args = p.parse_args()
    root = args.research.resolve()

    def read(relative):
        return json.loads((root / relative).read_text(encoding="utf-8"))

    fields = read("native_fields_isolated/native_field_fixtures.json")
    options = read("native_options_final/native_option_fixtures.json")
    accuracy = read("accuracy_verification/visualization_verification.json")
    acceptance = read("com_accuracy/catia_accuracy_acceptance.json")
    verification = read("verification/verification.json")
    assert not fields["errors"] and not options["errors"] and not acceptance["errors"]
    assert fields["settings_restored_exactly"] and options["settings_restored_exactly"]
    assert acceptance["document_count_before"] == acceptance["document_count_after"]
    native = []
    for folder in ("native_fields_isolated", "native_options_final"):
        for path in sorted((root / folder).glob("*.cgr")):
            c = parse(path.read_bytes())
            assert c.rebuilt() == c.data
            registry = applicative_records(c)
            if registry:
                assert registry[1].encode() == c.payload(registry[0])
            native.append({"file": str(path), "sha256": hashlib.sha256(c.data).hexdigest(),
                           "regenerated_byte_exact": True, "nodes": len(c.nodes),
                           "identity_timestamp_linked": audit(c)["three_way_timestamp_match"],
                           "identity_raw": c.nodes[1].name, "describe_capability": records(c)[4].capability,
                           "empty_allocations": [n.id for n in c.nodes[1:] if n.size == 0],
                           "applicative_registry": list(registry[1].names) if registry else None})
    net48 = (root / "probe/net48_default.cgr").read_bytes()
    assert net48 == (root / "verification/synthetic_26_25.cgr").read_bytes()
    summary = {
        "version": "CFV2-20261015-R3-visualization-fields",
        "reprocessed_with": "CFV2-20261016-R4-counted-identity",
        "confirmed_header_offsets": {"10": "Viz3DAccuracyMode", "11": "selected 3D accuracy",
                                     "15": "Viz3DCurveAccuracy", "19": "Viz2DAccuracyMode", "20": "selected 2D accuracy"},
        "confirmed_describe_bit": {"0x20": "LineicCgrMode; geometry also changes"},
        "native_controlled_states": len(native), "native": native,
        "settings_restored_exactly": True,
        "identity_audit_scope": {"linked_native_states": sum(n["identity_timestamp_linked"] for n in native),
                                 "schema_correction": "R4: B/D were misread name-length halves; actual names start with dot"},
        "native_header_parity": len(accuracy["native_header_parity"]),
        "invalid_accuracy_rejected": accuracy["invalid_accuracy_rejected"],
        "catia_products": len(acceptance["products"]),
        "catia_volumes_mm3": sorted(set(p["volume_mm3"] for p in acceptance["products"])),
        "catia_document_count_restored": True,
        "legacy_native_roundtrips": len(verification["native"]),
        "fragment_layouts": len(verification["fragmentation"]),
        "encoder_byte_exact_parity": len(verification["encoder_parity"]),
        "net48_net8_byte_exact_sha256": hashlib.sha256(net48).hexdigest(),
        "deployed_to_ps": False,
        "remaining": ["identity word0 and complete word3 rules", "Header 9,24,25,26,51,55,59",
                      "Describe other bits, format tag and opaque tail", "some directory controls",
                      "geometry prefix/suffix and internal adhesion payloads"],
    }
    target = root / "R3-summary.json"
    target.write_text(json.dumps(summary, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps({k: v for k, v in summary.items() if k not in ("native", "remaining")}))


if __name__ == "__main__":
    main()
