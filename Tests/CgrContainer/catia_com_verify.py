"""Bounded CATIA COM acceptance for generated CFV2 research files.

Only saves the two explicitly identified research Products left by the UI run
and Products created by this process. User document models are not edited.
"""
import argparse
import hashlib
import json
from pathlib import Path

import pythoncom
import pywintypes
import win32com.client


def error_details(error):
    return {"type": type(error).__name__, "message": str(error),
            "hresult": getattr(error, "hresult", None)}


def attempt(call):
    try:
        return {"ok": True, "value": call()}
    except (pywintypes.com_error, AttributeError, TypeError) as error:
        return {"ok": False, **error_details(error)}


def child_names(document):
    products = document.Product.Products
    return [str(products.Item(i).ReferenceProduct.PartNumber) for i in range(1, products.Count + 1)]


def show(document, products, visible):
    selection = document.Selection
    selection.Clear()
    for product in products:
        selection.Add(product)
    if products:
        selection.VisProperties.SetShow(0 if visible else 1)
    selection.Clear()


def measure(document, product):
    # Record unavailable CGR capabilities rather than interpreting failures as
    # geometric zeroes or substituting source-mesh numbers for CATIA results.
    result = {}
    result["representation_path"] = attempt(lambda: str(product.GetMasterShapeRepresentationPathName()))
    result["has_master_representation"] = attempt(lambda: bool(product.HasAMasterShapeRepresentation()))
    result["analyze_volume"] = attempt(lambda: float(product.Analyze.Volume))
    result["analyze_mass"] = attempt(lambda: float(product.Analyze.Mass))
    result["inertia_volume"] = attempt(lambda: float(product.GetTechnologicalObject("Inertia").Volume))
    selection = document.Selection
    selection.Clear()
    selection.Add(product)
    result["selection_reference"] = attempt(lambda: str(selection.Item2(1).Reference.DisplayName))
    result["spa_geometry_name"] = attempt(lambda: int(document.GetWorkbench("SPAWorkbench").GetMeasurable(
        selection.Item2(1).Reference).GeometryName))
    if result["spa_geometry_name"].get("ok"):
        raw = result["spa_geometry_name"]["value"]
        # CATIA can hand back an uninitialized-looking enum for a Product/CGR
        # reference. Keep the raw result but never call this a valid face pick.
        result["spa_geometry_name"]["usable_geometry_classification"] = 0 <= raw < 32
    selection.Clear()
    for label, query in (("face_search_count", "Topology.Face,all"), ("edge_search_count", "Topology.Edge,all")):
        def search(query=query):
            selection.Clear()
            selection.Search(query)
            count = int(selection.Count2)
            selection.Clear()
            return count
        result[label] = attempt(search)
    selection.Clear()
    return result


def capture(app, document, directory, label):
    from PIL import Image
    document.Activate()
    viewer = app.ActiveWindow.ActiveViewer
    viewer.Reframe()
    viewer.Update()
    raw = directory / f"{label}.bmp"
    viewer.CaptureToFile(4, str(raw))
    image = Image.open(raw).convert("RGB")
    png = directory / f"{label}.png"
    image.save(png)
    background = image.getpixel((image.width - 5, image.height - 5))
    non_background = sum(pixel != background for pixel in image.get_flattened_data())
    return {"bmp": str(raw), "png": str(png), "width": image.width, "height": image.height,
            "pixel_sha256": hashlib.sha256(image.tobytes()).hexdigest(),
            "background_rgb": background, "non_background_pixels": non_background}


def inspect_and_save(app, document, output, label, expected=None):
    names = child_names(document)
    if expected is not None and sorted(names) != sorted(expected):
        raise ValueError(f"Refusing to edit unexpected document children: {document.Name}: {names}")
    products = [document.Product.Products.Item(i) for i in range(1, document.Product.Products.Count + 1)]
    if not products:
        raise ValueError("Acceptance Product contains no children")
    result = {"original_document": str(document.Name), "children": [], "child_count": len(products)}
    document.Activate()
    show(document, products, False)
    try:
        for index, product in enumerate(products):
            show(document, [product], True)
            item = {"name": names[index], "measurements": measure(document, product),
                    "capture": capture(app, document, output, f"{label}_{index + 1:02d}")}
            result["children"].append(item)
            print(f"CATIA capture {label}/{names[index]}", flush=True)
            show(document, [product], False)
    finally:
        show(document, products, True)
        document.Selection.Clear()
    target = output / f"{label}.CATProduct"
    if target.exists():
        raise FileExistsError(f"Refusing to replace {target}")
    document.SaveAs(str(target))
    if not target.is_file() or not target.stat().st_size:
        raise ValueError("CATIA SaveAs did not produce a document")
    result.update(saved_path=str(target), saved_flag=bool(document.Saved), bytes=target.stat().st_size)
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("workspace", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--cleanup-empty-product", help="Exact name of a known empty Product left by this research run")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    pythoncom.CoInitialize()
    app = win32com.client.GetActiveObject("CATIA.Application")
    if args.cleanup_empty_product:
        empty = app.Documents.Item(args.cleanup_empty_product)
        if str(empty.Path) or empty.Product.Products.Count != 0:
            raise ValueError("Refusing to close a nonempty or file-backed Product")
        alerts = app.DisplayFileAlerts
        try:
            app.DisplayFileAlerts = False
            empty.Close()
        finally:
            app.DisplayFileAlerts = alerts
    before = {str(app.Documents.Item(i).Name): bool(app.Documents.Item(i).Saved)
              for i in range(1, app.Documents.Count + 1)}
    original_active = app.ActiveDocument
    original_alerts = app.DisplayFileAlerts
    report = {"method": "CATIA COM", "documents_before": len(before), "products": [], "errors": [],
              "scope": "Isolated viewport captures and COM representation/measurement probes; no interactive face pick"}
    created = []
    recovered = []
    try:
        app.DisplayFileAlerts = False
        expected = {
            "Product7.CATProduct": ("identity_variants", ["box_default", "box_sequence", "box_clock", "box_session", "box_clock_single_extent"]),
            "Product8.CATProduct": ("identity_single", ["box_clock_single_extent"]),
        }
        documents = [app.Documents.Item(i) for i in range(1, app.Documents.Count + 1)]
        for document in documents:
            name = str(document.Name)
            if name not in expected:
                continue
            label, names = expected[name]
            if bool(document.Saved):
                report["errors"].append({"document": name, "reason": "Not the expected unsaved research document; left unchanged"})
                continue
            # Check shape paths too, so a same-named user Product is never saved.
            if sorted(child_names(document)) != sorted(names):
                raise ValueError(f"Research document identity check failed: {name}")
            for i in range(1, document.Product.Products.Count + 1):
                path = Path(str(document.Product.Products.Item(i).GetMasterShapeRepresentationPathName())).resolve()
                if path.parent != (args.workspace / "analysis_cgr/container_20261015_r2/metadata").resolve():
                    raise ValueError(f"Unexpected shape path in {name}: {path}")
            result = inspect_and_save(app, document, args.output, label, names)
            report["products"].append(result)
            recovered.append(document)

        # One complete Product per FFM170 variant avoids overlapped geometry
        # falsely making a failed representation look visible.
        paths = {
            "ffm170_native": args.workspace / "FFM170-X-0417-6-CLOSE-A3-2024.11.14.cgr",
            "ffm170_single_extent": args.workspace / "analysis_cgr/container_20261015/verification_final/FFM170_structured_single_extent_experimental.cgr",
        }
        for label, path in paths.items():
            if not path.is_file():
                raise FileNotFoundError(path)
            document = app.Documents.Add("Product")
            created.append(document)
            # The CATIA type library already declares a SAFEARRAY parameter;
            # passing a VARIANT wrapper here double-wraps it in pywin32.
            document.Product.Products.AddComponentsFromFiles((str(path),), "All")
            if document.Product.Products.Count != 1:
                raise ValueError(f"Component insertion count mismatch: {label}")
            report["products"].append(inspect_and_save(app, document, args.output, label))
        # Close only newly created, successfully saved acceptance Products.
        for document in reversed(created):
            if document.Saved:
                document.Close()
        original_active.Activate()
    except Exception as error:
        report["errors"].append(error_details(error))
        for document in reversed(created):
            if document.Product.Products.Count == 0:
                document.Close()
        raise
    finally:
        app.DisplayFileAlerts = original_alerts
        report["documents_after"] = int(app.Documents.Count)
        report["elapsed_note"] = "COM calls serialized on one initialized apartment"
        # Existing non-research documents must retain their saved-state flags.
        after = {str(app.Documents.Item(i).Name): bool(app.Documents.Item(i).Saved)
                 for i in range(1, app.Documents.Count + 1)}
        excluded = {"Product7.CATProduct", "Product8.CATProduct"}
        report["existing_document_flags_preserved"] = all(name in after and after[name] == saved
                                                          for name, saved in before.items() if name not in excluded)
        (args.output / "catia_com_acceptance.json").write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
        pythoncom.CoUninitialize()
    print(json.dumps({"products": len(report["products"]), "children": sum(p["child_count"] for p in report["products"]),
                      "existing_document_flags_preserved": report["existing_document_flags_preserved"],
                      "report": str(args.output / "catia_com_acceptance.json")}, ensure_ascii=False))


if __name__ == "__main__":
    main()
