"""Read generated representation variants through isolated CATIA COM Products."""
import argparse
import json
import math
from pathlib import Path

import pythoncom
import win32com.client


def main():
    p = argparse.ArgumentParser()
    p.add_argument("variants", type=Path)
    p.add_argument("output", type=Path)
    args = p.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    cases = json.loads(args.variants.read_text(encoding="utf-8"))["variants"]
    report = {"method": "CATIA COM scene-only representation interventions", "cases": [], "errors": []}
    pythoncom.CoInitialize()
    app = win32com.client.GetActiveObject("CATIA.Application")
    previous, alerts = app.ActiveDocument, app.DisplayFileAlerts
    count_before, doc = app.Documents.Count, None
    try:
        app.DisplayFileAlerts = False
        for case in cases:
            result = {**case}
            try:
                doc = app.Documents.Add("Product")
                doc.Product.Products.AddComponentsFromFiles((case["path"],), "All")
                assert doc.Product.Products.Count == 1
                volume = doc.Product.Products.Item(1).Analyze.Volume
                assert math.isfinite(volume)
                result["volume_mm3"] = volume
                result["loaded"] = True
                target = args.output / (Path(case["path"]).stem + ".CATProduct")
                doc.SaveAs(str(target.resolve()))
                result["product"] = str(target.resolve())
                if case.get("capture"):
                    viewer = app.ActiveWindow.ActiveViewer
                    viewer.Reframe()
                    viewer.Update()
                    bmp = args.output / (Path(case["path"]).stem + ".bmp")
                    viewer.CaptureToFile(4, str(bmp.resolve()))
                    from PIL import Image
                    with Image.open(bmp) as img:
                        img.save(bmp.with_suffix(".png"))
                    result["capture_png"] = str(bmp.with_suffix(".png").resolve())
            except Exception as error:
                result["error"] = str(error)
                report["errors"].append({"path": case["path"], "error": str(error)})
            finally:
                if doc is not None:
                    doc.Close()
                    doc = None
            report["cases"].append(result)
            (args.output / "com_results.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
            print(f"COM {case['model']}/{case['variant']}: {result.get('volume_mm3', result.get('error'))}", flush=True)
    finally:
        if doc is not None:
            doc.Close()
        previous.Activate()
        app.DisplayFileAlerts = alerts
        report["document_count_before"] = count_before
        report["document_count_after"] = app.Documents.Count
        (args.output / "com_results.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        pythoncom.CoUninitialize()
    assert report["document_count_after"] == count_before


if __name__ == "__main__":
    main()
