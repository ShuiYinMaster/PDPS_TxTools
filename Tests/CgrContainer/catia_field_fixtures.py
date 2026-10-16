"""Single-variable native COM exports for unresolved CFV2 metadata fields.

Only new research documents are edited. Global visualization values are read,
recorded to disk before changes, temporarily varied one at a time, and restored
in finally without Commit/SaveRepository. No mouse/keyboard automation.
"""
import argparse
from dataclasses import asdict
import hashlib
import json
from pathlib import Path

import pythoncom
import psutil
import win32com.client

from cfv2 import parse
from cfv2_metadata import audit, records


SETTING_VARIANTS = (
    ("Viz3DFixedAccuracy", 0.37),
    ("Viz3DProportionnalAccuracy", 0.43),
    ("Viz3DAccuracyMode", False),
    ("Viz3DCurveAccuracy", 0.73),
    ("Viz2DFixedAccuracy", 0.047),
    ("Viz2DProportionnalAccuracy", 0.053),
    ("Viz2DAccuracyMode", False),
    ("LineicCgrMode", True),
    ("StaticLOD", 0.7),
    ("DynamicLOD", 4.0),
    ("DefaultDiffuseAmbientCoefficient", 0.61),
    ("DefaultSpecularCoefficient", 0.41),
    ("DefaultShininess", 0.31),
    ("OpaqueFaces", True),
    ("TwoSideLightingMode", False),
    ("BackFaceCullingMode", True),
)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=False)
    report = {"method": "CATIA COM ExportData single-variable matrix", "fixtures": [], "errors": []}
    report_path = args.output / "native_field_fixtures.json"

    def checkpoint():
        report_path.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    pythoncom.CoInitialize()
    app = win32com.client.GetActiveObject("CATIA.Application")
    previous = app.ActiveDocument
    alerts = app.DisplayFileAlerts
    documents = []
    settings = app.SettingControllers.Item("CATVizVisualizationSettingCtrl")
    snapshot = {key: getattr(settings, key) for key, _ in SETTING_VARIANTS}
    report["settings_before"] = snapshot
    report["cnext_pids"] = [p.info["pid"] for p in psutil.process_iter(["pid", "name"])
                             if (p.info["name"] or "").lower() == "cnext.exe"]
    checkpoint()  # Recovery information is durable before the first write.
    baseline_containers = {}

    def restore_settings():
        # Accuracy value setters also select their fixed/proportional mode.
        # Restore values first and mode booleans last, including after each case.
        modes = {"Viz3DAccuracyMode", "Viz2DAccuracyMode"}
        for key in [k for k in snapshot if k not in modes] + [k for k in snapshot if k in modes]:
            setattr(settings, key, snapshot[key])
        assert {key: getattr(settings, key) for key in snapshot} == snapshot

    def export(document, label, group, action):
        target = args.output / f"{label}.cgr"
        document.ExportData(str(target), "cgr")
        c = parse(target.read_bytes())
        a = audit(c)
        assert a["three_way_timestamp_match"]
        assert a["identity"]["process_id"] in report["cnext_pids"]
        assert c.rebuilt() == c.data
        _, header_node, header, describe_node, _ = records(c)
        baseline = baseline_containers.setdefault(group, c)
        baseline_header_node = records(baseline)[1]
        original = baseline.payload(baseline_header_node)
        current = c.payload(header_node)
        differences = [i for i, (x, y) in enumerate(zip(original, current)) if x != y and not 5 <= i < 9]
        changed = []
        for n in c.nodes:
            if n.kind == 2:
                old = next((p for p in baseline.nodes if (p.parent, p.name) == (n.parent, n.name)), None)
                if old is None or c.payload(n) != baseline.payload(old):
                    changed.append({"id": n.id, "name": n.name, "bytes": n.size})
        report["fixtures"].append({"label": label, "group": group, "action": action,
                                    "file": str(target), "sha256": hashlib.sha256(c.data).hexdigest(),
                                    "header": asdict(header) | {"rgba51": header.rgba51.hex()},
                                    "header_hex": current.hex(), "describe_hex": c.payload(describe_node).hex(),
                                    "header_changed_offsets_excluding_time": differences,
                                    "settings_readback": {key: getattr(settings, key) for key in snapshot},
                                    "changed_streams_including_time": changed, **a})
        checkpoint()
        print(f"NATIVE {label}: header offsets={differences}, counter={a['identity']['word3']}", flush=True)

    def style(document, obj, method, *values):
        selection = document.Selection
        selection.Clear()
        try:
            selection.Add(obj)
            getattr(selection.VisProperties, method)(*values)
        finally:
            selection.Clear()

    try:
        app.DisplayFileAlerts = False
        doc = app.Documents.Add("Part")
        documents.append(doc)
        part = doc.Part
        body = part.HybridBodies.Add()
        body.Name = "FieldResearch"
        point = part.HybridShapeFactory.AddNewPointCoord(11, 22, 33)
        body.AppendHybridShape(point)
        part.InWorkObject = point
        part.Update()
        doc.SaveAs(str(args.output / "field_point.CATPart"))
        export(doc, "point_baseline", "point", "create point (11,22,33)")
        export(doc, "point_repeat", "point", "repeat ExportData")
        part.Update()
        export(doc, "point_noop_update", "point", "Part.Update with no geometry change")
        point.Name = "RenamedResearchPoint"
        export(doc, "point_rename", "point", "rename point only")
        style(doc, point, "SetRealColor", 17, 93, 201, 0)
        export(doc, "point_color", "point", "point SetRealColor(17,93,201,0)")
        style(doc, point, "SetRealOpacity", 128, 0)
        export(doc, "point_opacity", "point", "point SetRealOpacity(128,0), preceding color retained")
        style(doc, point, "SetSymbolType", 7)
        export(doc, "point_symbol", "point", "SetSymbolType(7), preceding styles retained")
        style(doc, point, "SetRealWidth", 4, 0)
        export(doc, "point_width", "point", "SetRealWidth(4,0), preceding styles retained")
        point.X.Value = 21
        part.Update()
        export(doc, "point_x21", "point", "point X changed 11 to 21")
        point.X.Value = 11
        part.Update()
        export(doc, "point_x11_restored", "point", "point X restored 21 to 11")
        doc.Close()
        documents.remove(doc)

        doc = app.Documents.Add("Part")
        documents.append(doc)
        part = doc.Part
        body = part.MainBody
        sketch = body.Sketches.Add(part.OriginElements.PlaneXY)
        factory = sketch.OpenEdition()
        factory.CreateClosedCircle(11, 22, 12)
        sketch.CloseEdition()
        part.InWorkObject = sketch
        part.Update()
        pad = part.ShapeFactory.AddNewPad(sketch, 33)
        part.Update()
        style(doc, sketch, "SetShow", 1)
        doc.SaveAs(str(args.output / "field_cylinder.CATPart"))
        export(doc, "solid_baseline", "solid", "circular pad, radius12 length33")
        export(doc, "solid_repeat", "solid", "repeat ExportData")

        # Each setting case starts from the same snapshotted configuration.
        for key, value in SETTING_VARIANTS:
            try:
                assert {k: getattr(settings, k) for k in snapshot} == snapshot
                setattr(settings, key, value)
                actual = getattr(settings, key)
                export(doc, "setting_" + key, "solid", {"setting": key, "requested": value, "readback": actual})
            except Exception as error:
                report["errors"].append({"setting": key, "message": str(error)})
                checkpoint()
            finally:
                restore_settings()
        export(doc, "solid_settings_restored", "solid", "all visualization settings restored")

        for label, obj, method, values in (
            ("solid_body_color", body, "SetRealColor", (17, 93, 201, 1)),
            ("solid_root_color", doc.Product, "SetRealColor", (29, 151, 83, 1)),
            ("solid_root_opacity", doc.Product, "SetRealOpacity", (128, 1)),
            ("solid_root_width", doc.Product, "SetRealWidth", (4, 1)),
            ("solid_root_line_type", doc.Product, "SetRealLineType", (3, 1)),
        ):
            style(doc, obj, method, *values)
            export(doc, label, "solid", f"{method}{values}; preceding style changes retained")
        doc.Close()
        documents.remove(doc)
    except Exception as error:
        report["errors"].append({"type": type(error).__name__, "message": str(error)})
        raise
    finally:
        restore_errors = []
        try:
            restore_settings()
        except Exception as error:
            restore_errors.append({"message": str(error)})
        report["restore_errors"] = restore_errors
        report["settings_after"] = {key: getattr(settings, key) for key in snapshot}
        report["settings_restored_exactly"] = report["settings_after"] == snapshot
        for doc in reversed(documents):
            doc.Close()
        previous.Activate()
        app.DisplayFileAlerts = alerts
        checkpoint()
        pythoncom.CoUninitialize()
    assert report["settings_restored_exactly"] and not report["restore_errors"]
    print(json.dumps({"exports": len(report["fixtures"]), "errors": report["errors"],
                      "settings_restored_exactly": report["settings_restored_exactly"]}))


if __name__ == "__main__":
    main()
