"""Create controlled native CGR exports via CATIA COM, in new research Parts."""
import argparse
import hashlib
import json
from pathlib import Path

import pythoncom
import psutil
import win32com.client

from cfv2 import parse
from cfv2_metadata import audit, StorageIdentity


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    pythoncom.CoInitialize()
    app = win32com.client.GetActiveObject("CATIA.Application")
    previous = app.ActiveDocument
    alerts = app.DisplayFileAlerts
    documents = []
    report = {"method": "CATIA COM native ExportData", "fixtures": [], "errors": []}
    report["cnext_pids"] = [p.info["pid"] for p in psutil.process_iter(["pid", "name"])
                             if (p.info["name"] or "").lower() == "cnext.exe"]
    try:
        app.DisplayFileAlerts = False
        document = app.Documents.Add("Part")
        documents.append(document)
        part = document.Part
        body = part.HybridBodies.Add()
        body.Name = "IdentityResearch"
        point = part.HybridShapeFactory.AddNewPointCoord(11.0, 22.0, 33.0)
        body.AppendHybridShape(point)
        part.InWorkObject = point
        part.Update()
        document.SaveAs(str(args.output / "identity_point.CATPart"))

        def export(label, expected):
            target = args.output / f"{label}.cgr"
            document.ExportData(str(target), "cgr")
            c = parse(target.read_bytes())
            a = audit(c)
            assert a["three_way_timestamp_match"]
            assert a["identity"]["word1"] in report["cnext_pids"], "Identity word1 does not match any running CATIA PID"
            assert tuple(a["header_bounds"]) == tuple(expected + expected)
            report["fixtures"].append({"label": label, "file": str(target), "sha256": hashlib.sha256(c.data).hexdigest(),
                                        "expected_point": expected, "word1_matches_cnext_pid": True, **a})
            print(f"NATIVE {label}: identity={c.nodes[1].name}", flush=True)

        export("point_initial", [11.0, 22.0, 33.0])
        export("point_repeat", [11.0, 22.0, 33.0])
        point.X.Value = 21.0
        part.Update()
        export("point_x21", [21.0, 22.0, 33.0])
        point.X.Value = 11.0
        part.Update()
        export("point_restored", [11.0, 22.0, 33.0])
        # The source Part is left at its original geometry and saved for replay.
        document.Save()
        document.Close()
        documents.remove(document)
        previous.Activate()
    except Exception as error:
        report["errors"].append({"type": type(error).__name__, "message": str(error)})
        raise
    finally:
        for document in reversed(documents):
            document.Close()
        app.DisplayFileAlerts = alerts
        (args.output / "native_identity_fixtures.json").write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
        pythoncom.CoUninitialize()
    print(json.dumps({"native_exports": len(report["fixtures"]), "cnext_pids": report["cnext_pids"],
                      "all_word1_pid_matches": all(f["word1_matches_cnext_pid"] for f in report["fixtures"])}))


if __name__ == "__main__":
    main()
