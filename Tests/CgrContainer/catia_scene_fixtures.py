"""Native COM scene structure experiments on new Parts/Products only."""
import argparse
import json
from pathlib import Path

import pythoncom
import win32com.client

from cfv2 import parse
from cfv2_metadata import audit, named_node


def main():
    p = argparse.ArgumentParser()
    p.add_argument("output", type=Path)
    args = p.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = {"method": "CATIA COM controlled geometry/style/placement exports", "fixtures": [], "errors": []}

    def checkpoint():
        (output / "native_scene_fixtures.json").write_text(json.dumps(report, indent=2), encoding="utf-8")

    pythoncom.CoInitialize()
    app = win32com.client.GetActiveObject("CATIA.Application")
    previous, alerts = app.ActiveDocument, app.DisplayFileAlerts
    count_before = app.Documents.Count
    documents = []

    def export(doc, label, action):
        path = output / (label + ".cgr")
        try:
            doc.ExportData(str(path), "cgr")
            c = parse(path.read_bytes())
            a = audit(c)
            assert a["three_way_timestamp_match"] and c.rebuilt() == c.data
            storage = named_node(c, c.nodes[1].id, "SceneGraph")
            scene = c.payload(named_node(c, storage.id, "MainDataStream"))
            (output / (label + ".scene")).write_bytes(scene)
            report["fixtures"].append({"label": label, "action": action, "file": str(path),
                                        "scene_bytes": len(scene), "scene_head": scene[:64].hex(),
                                        "scene_tail": scene[-64:].hex(), **a})
            print(f"COM SCENE {label}: {len(scene)} bytes", flush=True)
        except Exception as error:
            report["errors"].append({"label": label, "message": str(error)})
            print(f"COM SCENE {label}: ERROR {error}", flush=True)
        checkpoint()

    def style(doc, obj, method, *values):
        selection = doc.Selection
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
        sketch = part.MainBody.Sketches.Add(part.OriginElements.PlaneXY)
        f = sketch.OpenEdition()
        corners = ((11, 22), (41, 22), (41, 62), (11, 62))
        for a, b in zip(corners, corners[1:] + corners[:1]):
            f.CreateLine(*a, *b)
        sketch.CloseEdition()
        part.InWorkObject = sketch
        part.Update()
        pad = part.ShapeFactory.AddNewPad(sketch, 50)
        part.Update()
        style(doc, sketch, "SetShow", 1)
        source = output / "scene_box.CATPart"
        doc.SaveAs(str(source))
        export(doc, "part_baseline", "box [11,22,0]..[41,62,50]")
        export(doc, "part_repeat", "repeat without change")
        pad.FirstLimit.Dimension.Value = 60
        part.Update()
        export(doc, "part_height60", "pad length 50 -> 60")
        pad.FirstLimit.Dimension.Value = 50
        part.Update()
        export(doc, "part_restored", "pad length 60 -> 50")
        doc.Save()

        product_doc = app.Documents.Add("Product")
        documents.append(product_doc)
        product_doc.Product.Products.AddComponentsFromFiles((str(source),), "All")
        first = product_doc.Product.Products.Item(1)
        export(product_doc, "assembly_identity", "one instance, identity placement")
        first.Move.Apply((1., 0., 0., 0., 1., 0., 0., 0., 1., 101., 203., 307.))
        export(product_doc, "assembly_translated", {"translation": [101, 203, 307]})
        # Apply is incremental: rotate about the current origin with zero delta.
        first.Move.Apply((0., 1., 0., -1., 0., 0., 0., 0., 1., 0., 0., 0.))
        export(product_doc, "assembly_rotated", {"incremental_rotation_z_degrees": 90})
        product_doc.Product.Products.AddComponentsFromFiles((str(source),), "All")
        second = product_doc.Product.Products.Item(2)
        second.Move.Apply((1., 0., 0., 0., 1., 0., 0., 0., 1., -113., 47., 71.))
        export(product_doc, "assembly_two_instances", {"second_translation": [-113, 47, 71]})
        product_doc.SaveAs(str(output / "scene_placements.CATProduct"))
        product_doc.Close()
        documents.remove(product_doc)
        doc.Activate()

        for label, method, values in (
            ("part_color", "SetRealColor", (17, 93, 201, 1)),
            ("part_opacity", "SetRealOpacity", (128, 1)),
            ("part_width", "SetRealWidth", (4, 1)),
            ("part_line_type", "SetRealLineType", (3, 1)),
        ):
            style(doc, part.MainBody, method, *values)
            export(doc, label, f"{method}{values}, previous styles retained")
        doc.Close()
        documents.remove(doc)
    finally:
        for document in reversed(documents):
            document.Close()
        previous.Activate()
        app.DisplayFileAlerts = alerts
        report["document_count_before"] = count_before
        report["document_count_after"] = app.Documents.Count
        checkpoint()
        pythoncom.CoUninitialize()
    print(json.dumps({"fixtures": len(report["fixtures"]), "errors": report["errors"],
                      "document_count_restored": report["document_count_before"] == report["document_count_after"]}))


if __name__ == "__main__":
    main()
