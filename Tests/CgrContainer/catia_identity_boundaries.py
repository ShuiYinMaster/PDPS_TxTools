"""COM native exports spanning an identity counter byte cycle and save/reopen."""
import argparse
import json
from pathlib import Path
import struct

import pythoncom
import psutil
import win32com.client

from cfv2 import parse
from cfv2_metadata import StorageIdentity, audit, records


def main():
    p = argparse.ArgumentParser()
    p.add_argument("output", type=Path)
    p.add_argument("--exports", type=int, default=272)
    args = p.parse_args()
    if not 1 <= args.exports <= 512:
        raise ValueError("Expected 1..512 controlled exports")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = {"method": "CATIA COM fresh Part exports, counter cycle, save/reopen", "fixtures": [], "errors": []}
    report_path = output / "native_identity_boundaries.json"

    def checkpoint():
        report_path.write_text(json.dumps(report, indent=2), encoding="utf-8")

    pythoncom.CoInitialize()
    app = win32com.client.GetActiveObject("CATIA.Application")
    previous, alerts = app.ActiveDocument, app.DisplayFileAlerts
    document_count = app.Documents.Count
    doc = None
    pids = [proc.info["pid"] for proc in psutil.process_iter(["pid", "name"])
            if (proc.info["name"] or "").lower() == "cnext.exe"]
    report["cnext_pids"] = pids
    try:
        app.DisplayFileAlerts = False
        doc = app.Documents.Add("Part")
        part = doc.Part
        body = part.HybridBodies.Add()
        body.Name = "CountedIdentityResearch"
        point = part.HybridShapeFactory.AddNewPointCoord(11, 22, 33)
        body.AppendHybridShape(point)
        part.InWorkObject = point
        part.Update()
        source = output / "counted_identity_point.CATPart"
        doc.SaveAs(str(source))

        def export(label):
            path = output / (label + ".cgr")
            doc.ExportData(str(path), "cgr")
            c = parse(path.read_bytes())
            a = audit(c)
            assert a["three_way_timestamp_match"] and c.rebuilt() == c.data
            assert a["identity"]["process_id"] in pids
            assert records(c)[2].bounds == (11, 22, 33, 11, 22, 33)
            count_offset = c.directory_offset + 64 + 86 + 84
            count = struct.unpack_from(">I", c.data, count_offset)[0]
            assert count == len((c.nodes[1].name + "\0").encode("utf-16be"))
            report["fixtures"].append({"label": label, "file": str(path), "actual_name": c.nodes[1].name,
                                        "name_byte_count": count, "byte_exact_roundtrip": True, **a})
            if len(report["fixtures"]) % 64 == 0:
                checkpoint()
                print(f"COM native identity: {len(report['fixtures'])} exports", flush=True)

        for index in range(args.exports):
            export(f"counter_{index:03d}")
        doc.Save()
        export("after_save")
        doc.Close()
        doc = None
        doc = app.Documents.Open(str(source))
        export("after_reopen")
        export("reopen_repeat")
        doc.Close()
        doc = None
    except Exception as error:
        report["errors"].append({"type": type(error).__name__, "message": str(error)})
        raise
    finally:
        if doc is not None:
            doc.Close()
        previous.Activate()
        app.DisplayFileAlerts = alerts
        report["document_count_restored"] = app.Documents.Count == document_count
        report["name_byte_counts"] = sorted(set(f["name_byte_count"] for f in report["fixtures"]))
        report["counter_low_bytes_observed"] = sorted(set(f["identity"]["word3"] & 255 for f in report["fixtures"]))
        checkpoint()
        pythoncom.CoUninitialize()
    print(json.dumps({"exports": len(report["fixtures"]), "name_byte_counts": report["name_byte_counts"],
                      "counter_low_bytes": len(report["counter_low_bytes_observed"]), "errors": report["errors"]}))


if __name__ == "__main__":
    main()
