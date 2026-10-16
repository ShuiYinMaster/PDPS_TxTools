"""Native CGR flag experiments on an owned fixture, with full setting restore."""
import argparse
import json
from pathlib import Path

import pythoncom
import win32com.client

from cfv2 import parse
from cfv2_metadata import audit, records


def main():
    p = argparse.ArgumentParser()
    p.add_argument("source", type=Path)
    p.add_argument("output", type=Path)
    args = p.parse_args()
    source = args.source.resolve()
    # Do not open or modify arbitrary user documents with this research helper.
    if source.name != "field_cylinder.CATPart" or "container_20261015_r3" not in source.parts:
        raise ValueError("Expected this round's owned cylinder fixture")
    args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=False)
    pythoncom.CoInitialize()
    app = win32com.client.GetActiveObject("CATIA.Application")
    previous, alerts = app.ActiveDocument, app.DisplayFileAlerts
    controllers = {
        "adhesion": ("CATVisCGRAdhesionSettingCtrl", ("V5_SPA", "V4V5_FDT", "Voxels", "V4_Model_LnF", "V4_Model_CommentPage")),
        "cache": ("CATSysCacheSettingCtrl", ("LODMode", "TimestampMode", "UTCTimeFormat")),
        "visualization": ("CATVizVisualizationSettingCtrl", ("LineicCgrMode",)),
    }
    objects = {key: app.SettingControllers.Item(name) for key, (name, _) in controllers.items()}

    def read():
        return {key: {field: getattr(objects[key], field) for field in fields}
                for key, (_, fields) in controllers.items()}

    snapshot = read()
    report = {"source": str(source), "settings_before": snapshot, "fixtures": [], "errors": []}

    def checkpoint():
        (args.output / "native_option_fixtures.json").write_text(json.dumps(report, indent=2), encoding="utf-8")

    def restore():
        for key, fields in snapshot.items():
            for field, value in fields.items():
                setattr(objects[key], field, value)
        assert read() == snapshot

    checkpoint()
    document = None
    try:
        app.DisplayFileAlerts = False
        document = app.Documents.Open(str(source))
        variants = [("baseline", [])]
        variants += [(key + "_" + field, [(key, field, not value)])
                     for key, fields in snapshot.items() for field, value in fields.items()]
        variants += [("lineic_without_lod", [("visualization", "LineicCgrMode", True), ("cache", "LODMode", False)])]
        variants += [("adhesion_spa_and_voxels", [("adhesion", "V5_SPA", True), ("adhesion", "Voxels", True)]),
                     ("adhesion_all", [("adhesion", field, True) for field in snapshot["adhesion"]])]
        variants += [("restored", [])]
        for label, changes in variants:
            try:
                assert read() == snapshot
                for key, field, value in changes:
                    setattr(objects[key], field, value)
                path = args.output / (label + ".cgr")
                document.ExportData(str(path), "cgr")
                c = parse(path.read_bytes())
                a = audit(c)
                assert a["three_way_timestamp_match"]
                assert c.rebuilt() == c.data
                _, hn, header, dn, describe = records(c)
                report["fixtures"].append({"label": label, "settings_readback": read(), "file": str(path),
                                            "header_hex": c.payload(hn).hex(),
                                            "describe_hex": c.payload(dn).hex(),
                                            "describe_capability": describe.capability,
                                            "regenerated_byte_exact": True,
                                            "nodes": [{"id": n.id, "parent": n.parent, "name": n.name, "size": n.size} for n in c.nodes], **a})
                checkpoint()
                print(f"NATIVE {label}: flags={describe.capability:#x}, nodes={len(c.nodes)}", flush=True)
            except Exception as error:
                report["errors"].append({"label": label, "message": str(error)})
                checkpoint()
            finally:
                restore()
    finally:
        restore()
        report["settings_after"] = read()
        report["settings_restored_exactly"] = read() == snapshot
        if document is not None:
            document.Close()
        previous.Activate()
        app.DisplayFileAlerts = alerts
        checkpoint()
        pythoncom.CoUninitialize()
    print(json.dumps({"exports": len(report["fixtures"]), "errors": report["errors"],
                      "settings_restored_exactly": report["settings_restored_exactly"]}))


if __name__ == "__main__":
    main()
