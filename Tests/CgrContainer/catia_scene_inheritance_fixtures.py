"""Independent native COM instance-style and primitive-bound controls."""
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
    p.add_argument("--style-ranges", action="store_true")
    p.add_argument("--width-upper", action="store_true", help="With --style-ranges, probe upper width codes only")
    args = p.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    source = output / "inheritance_source.CATPart"
    shutil.copyfile(args.source, source)
    report = {"method": "CATIA native COM independent instance style and box controls", "fixtures": [], "errors": []}
    pythoncom.CoInitialize()
    app = win32com.client.GetActiveObject("CATIA.Application")
    previous, alerts = app.ActiveDocument, app.DisplayFileAlerts
    count_before, documents = app.Documents.Count, []

    def checkpoint():
        (output / "native_inheritance.json").write_text(json.dumps(report, indent=2), encoding="utf-8")

    def style(doc, obj, method, values):
        sel = doc.Selection
        sel.Clear()
        try:
            sel.Add(obj)
            getattr(sel.VisProperties, method)(*values)
        finally:
            sel.Clear()

    def export(doc, label, action):
        path = output / (label + ".cgr")
        doc.ExportData(str(path), "cgr")
        container = parse(path.read_bytes())
        owner = named_node(container, container.nodes[1].id, "SceneGraph")
        data = container.payload(named_node(container, owner.id, "MainDataStream"))
        (output / (label + ".scene")).write_bytes(data)
        item = {"label": label, "action": action, "path": str(path), "scene_bytes": len(data)}
        try:
            scene = parse_scene(data)
            item["nodes"] = [{"tag": hex(n.tag), "offset": n.offset, "flags": n.header.flags,
                "encoding": n.header.encoding, "attribute_word_be": n.header.attribute_word_be,
                "field11": n.header.field11, "radius": n.header.radius, "scalar16": n.header.scalar16,
                "center": n.header.center, "object_id": n.header.object_id,
                "layer_code": n.layer_code if n.tag != 0x95 else None,
                "node_options": n.node_options if n.tag != 0x95 else None} for n in scene.nodes]
        except ValueError as error:
            item["parse_error"] = str(error)
        report["fixtures"].append(item)
        print(f"COM INHERIT {label}: {len(data)} bytes, {item.get('parse_error', 'parsed')}", flush=True)
        checkpoint()

    def close(doc):
        doc.Close()
        documents.remove(doc)

    try:
        app.DisplayFileAlerts = False
        # Open a private source copy so CATIA cannot silently reuse a user's part.
        source_doc = app.Documents.Open(str(source))
        documents.append(source_doc)
        cases = [("assembly_baseline", None, (), "instance")]
        for label, method, values in (
            ("color", "SetRealColor", (17, 93, 201)), ("opacity", "SetRealOpacity", (128,)),
            ("width", "SetRealWidth", (4,)), ("line_type", "SetRealLineType", (3,)),
        ):
            for inherit in (0, 1):
                cases.append((f"instance_{label}_inherit{inherit}", method, values + (inherit,), "instance"))
        cases += [("instance_layer37", "SetLayer", (0, 37), "instance"),
                  ("instance_no_pick", "SetPick", (1,), "instance"),
                  ("root_color_inherit1", "SetRealColor", (217, 53, 29, 1), "root")]
        if args.style_ranges:
            cases = []
            for label, method, values in (
                ("red", "SetRealColor", (231, 17, 3, 1)),
                ("green", "SetRealColor", (5, 211, 31, 1)),
                ("blue", "SetRealColor", (13, 7, 197, 1)),
            ):
                cases.append(("instance_" + label, method, values, "instance"))
            cases += [(f"instance_alpha{a}", "SetRealOpacity", (a, 1), "instance") for a in (0, 1, 64, 127, 200, 254, 255)]
            cases += [(f"instance_width{v}", "SetRealWidth", (v, 1), "instance") for v in (1, 2, 15, 16, 31, 63)]
            cases += [(f"instance_line{v}", "SetRealLineType", (v, 1), "instance") for v in (1, 2, 63)]
            cases.append(("instance_combined", "combined", (), "instance"))
            if args.width_upper:
                cases = [(f"instance_width{v}", "SetRealWidth", (v, 1), "instance") for v in (32, 47, 55, 62)]
        for label, method, values, target in cases:
            doc = app.Documents.Add("Product")
            documents.append(doc)
            for _ in range(2):
                doc.Product.Products.AddComponentsFromFiles((str(source),), "All")
            second = doc.Product.Products.Item(2)
            second.Move.Apply((1., 0., 0., 0., 1., 0., 0., 0., 1., 80., 0., 0.))
            if method:
                selected = doc.Product.Products.Item(1) if target == "instance" else doc.Product
                if method == "combined":
                    for setter, setting in (("SetRealColor", (41, 83, 167, 1)), ("SetRealOpacity", (73, 1)),
                                            ("SetRealWidth", (16, 1)), ("SetRealLineType", (3, 1))):
                        style(doc, selected, setter, setting)
                else:
                    style(doc, selected, method, values)
            export(doc, label, {"target": target, "method": method, "values": values})
            close(doc)
        close(source_doc)

        if args.style_ranges:
            assert app.Documents.Count == count_before
            return

        # Independent translated/scaled native boxes test the root union formula.
        for label, bounds in (
            ("box_origin", (0, 0, 0, 30, 40, 50)),
            ("box_positive", (101, 203, 0, 131, 243, 50)),
            ("box_negative", (-71, -53, 0, -41, -13, 50)),
            ("box_centered_xy", (-15, -20, 0, 15, 20, 50)),
            ("box_small", (2, 3, 0, 5, 7, 5)),
        ):
            x0, y0, z0, x1, y1, z1 = bounds
            doc = app.Documents.Add("Part")
            documents.append(doc)
            part = doc.Part
            sketch = part.MainBody.Sketches.Add(part.OriginElements.PlaneXY)
            factory = sketch.OpenEdition()
            corners = ((x0, y0), (x1, y0), (x1, y1), (x0, y1))
            for a, b in zip(corners, corners[1:] + corners[:1]):
                factory.CreateLine(*a, *b)
            sketch.CloseEdition()
            part.InWorkObject = sketch
            part.Update()
            part.ShapeFactory.AddNewPad(sketch, z1)
            part.Update()
            style(doc, sketch, "SetShow", (1,))
            export(doc, label, {"bounds": bounds})
            close(doc)
    except Exception as error:
        report["errors"].append({"type": type(error).__name__, "message": str(error)})
        raise
    finally:
        for doc in reversed(documents):
            doc.Close()
        previous.Activate()
        app.DisplayFileAlerts = alerts
        report["document_count_before"] = count_before
        report["document_count_after"] = app.Documents.Count
        checkpoint()
        pythoncom.CoUninitialize()
    assert report["document_count_after"] == count_before


if __name__ == "__main__":
    main()
