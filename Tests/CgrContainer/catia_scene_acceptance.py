"""Compare before/after scene serializer files in isolated COM Products."""
import argparse
import json
import math
from pathlib import Path

import pythoncom
import win32com.client


def main():
    p = argparse.ArgumentParser()
    p.add_argument("fixtures", type=Path)
    p.add_argument("output", type=Path)
    args = p.parse_args()
    fixtures, output = args.fixtures.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = {"method": "CATIA COM paired isolated scene serializer acceptance", "pairs": [], "errors": []}
    pythoncom.CoInitialize()
    app = win32com.client.GetActiveObject("CATIA.Application")
    previous, alerts = app.ActiveDocument, app.DisplayFileAlerts
    count_before, doc = app.Documents.Count, None
    try:
        app.DisplayFileAlerts = False
        for model in ("box", "color_box", "cylinder"):
            for mode in ("compact", "features", "features-planar"):
                volumes, products = [], []
                before, after = [fixtures / f"{model}_{mode}_{label}.cgr" for label in ("before", "after")]
                assert before.read_bytes() == after.read_bytes()
                for label, path in (("before", before), ("after", after)):
                    doc = app.Documents.Add("Product")
                    doc.Product.Products.AddComponentsFromFiles((str(path),), "All")
                    assert doc.Product.Products.Count == 1
                    volume = doc.Product.Products.Item(1).Analyze.Volume
                    assert math.isfinite(volume)
                    volumes.append(volume)
                    viewer = app.ActiveWindow.ActiveViewer
                    viewer.Reframe()
                    viewer.Update()
                    target = output / f"{model}_{mode}_{label}.CATProduct"
                    doc.SaveAs(str(target))
                    products.append(str(target))
                    if label == "after" and (model, mode) in (("cylinder", "features"), ("color_box", "compact")):
                        image_path = output / f"{model}_{mode}.bmp"
                        viewer.CaptureToFile(4, str(image_path))
                        from PIL import Image
                        with Image.open(image_path) as img:
                            img.save(image_path.with_suffix(".png"))
                    doc.Close()
                    doc = None
                assert abs(volumes[0] - volumes[1]) <= max(1, abs(volumes[0])) * 1e-12
                report["pairs"].append({"model": model, "mode": mode, "products": products,
                                        "volumes_mm3": volumes, "byte_exact": True, "volume_match": True})
                print(f"COM PAIR {model}/{mode}: {volumes}", flush=True)
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
        (output / "catia_scene_acceptance.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        pythoncom.CoUninitialize()
    assert report["document_count_after"] == count_before
    print(json.dumps({"pairs": len(report["pairs"]), "errors": report["errors"]}))


if __name__ == "__main__":
    main()
