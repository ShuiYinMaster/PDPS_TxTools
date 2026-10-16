"""Native COM pick/show/layer negative controls for scene header flags."""
import argparse
import json
from pathlib import Path
import shutil

import pythoncom
import win32com.client

from cfv2 import parse
from cfv2_metadata import named_node
from scene_records import parse_scene


def main():
    p = argparse.ArgumentParser()
    p.add_argument("source", type=Path)
    p.add_argument("output", type=Path)
    args = p.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    source = output / "flag_controls.CATPart"
    shutil.copyfile(args.source, source)
    report = {"method": "CATIA native COM VisProperties controls", "fixtures": [], "errors": []}
    pythoncom.CoInitialize()
    app = win32com.client.GetActiveObject("CATIA.Application")
    previous, alerts = app.ActiveDocument, app.DisplayFileAlerts
    count_before, doc = app.Documents.Count, None

    def export(label, action):
        target = output / (label + ".cgr")
        doc.ExportData(str(target), "cgr")
        container = parse(target.read_bytes())
        owner = named_node(container, container.nodes[1].id, "SceneGraph")
        data = container.payload(named_node(container, owner.id, "MainDataStream"))
        (output / (label + ".scene")).write_bytes(data)
        try:
            scene = parse_scene(data)
        except ValueError as error:
            report["fixtures"].append({"label": label, "action": action, "file": str(target),
                                        "scene_bytes": len(data), "parse_error": str(error)})
            print(f"COM FLAGS {label}: parser unsupported {error}", flush=True)
            return
        report["fixtures"].append({"label": label, "action": action, "file": str(target), "scene_bytes": len(data),
            "headers": [{"tag": hex(n.tag), "flags": n.header.flags, "field11": n.header.field11,
                         "scalar16": n.header.scalar16, "attribute_word_be": n.header.attribute_word_be,
                         "encoding": n.header.encoding} for n in scene.nodes]})
        print(f"COM FLAGS {label}: {[n.header.flags for n in scene.nodes]}", flush=True)

    def style(obj, method, *values):
        sel = doc.Selection
        sel.Clear()
        try:
            sel.Add(obj)
            getattr(sel.VisProperties, method)(*values)
        finally:
            sel.Clear()

    try:
        app.DisplayFileAlerts = False
        doc = app.Documents.Open(str(source))
        body = doc.Part.MainBody
        pad = body.Shapes.Item(1)
        export("baseline", "unchanged research box")
        for target_name, target in (("body", body), ("pad", pad)):
            style(target, "SetPick", 1)
            export(target_name + "_no_pick", "catVisPropertyNoPickAttr=1")
            style(target, "SetPick", 0)
            export(target_name + "_pick", "catVisPropertyPickAttr=0")
        for layer in (0, 1, 37, 255, 256, 999):
            style(body, "SetLayer", 0, layer)
            export(f"body_layer{layer}", f"catVisLayerBasic=0, layer{layer}")
        style(body, "SetLayer", 1, 0)
        export("body_layer_none", "catVisLayerNone=1")
        style(body, "SetShow", 1)
        export("body_hidden", "catVisPropertyNoShowAttr=1")
        style(body, "SetShow", 0)
        export("body_shown", "catVisPropertyShowAttr=0")
    except Exception as error:
        report["errors"].append({"type": type(error).__name__, "message": str(error)})
        raise
    finally:
        if doc is not None:
            doc.Close()
        previous.Activate()
        app.DisplayFileAlerts = alerts
        report["document_count_before"] = count_before
        report["document_count_after"] = app.Documents.Count
        (output / "native_flags.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        pythoncom.CoUninitialize()
    assert report["document_count_after"] == count_before


if __name__ == "__main__":
    main()
