"""Load C# field candidates through COM in isolated owned Products."""
import argparse
import json
from pathlib import Path

import pythoncom
import win32com.client


def main():
    p = argparse.ArgumentParser()
    p.add_argument("native", type=Path)
    p.add_argument("candidates", type=Path)
    p.add_argument("output", type=Path)
    p.add_argument("--pattern", default="*.cgr")
    args = p.parse_args()
    args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=False)
    report = {"method": "CATIA COM isolated CGR Products", "products": [], "errors": []}
    pythoncom.CoInitialize()
    app = win32com.client.GetActiveObject("CATIA.Application")
    previous, alerts = app.ActiveDocument, app.DisplayFileAlerts
    documents_before = app.Documents.Count
    doc = None
    try:
        app.DisplayFileAlerts = False
        paths = [args.native.resolve()] + sorted(args.candidates.resolve().glob(args.pattern))
        native_volume = None
        for index, path in enumerate(paths):
            if path.name.startswith("invalid_"):
                continue
            doc = app.Documents.Add("Product")
            doc.Product.Products.AddComponentsFromFiles((str(path),), "All")
            assert doc.Product.Products.Count == 1
            product = doc.Product.Products.Item(1)
            volume = product.Analyze.Volume
            assert volume > 0
            if native_volume is None:
                native_volume = volume
            assert abs(volume - native_volume) <= abs(native_volume) * 1e-12
            viewer = app.ActiveWindow.ActiveViewer
            viewer.Reframe()
            viewer.Update()
            target = args.output / f"{index:02d}_{path.stem}.CATProduct"
            doc.SaveAs(str(target))
            report["products"].append({"cgr": str(path), "product": str(target), "volume_mm3": volume,
                                       "matches_native_volume": True, "saved": bool(doc.Saved)})
            print(f"COM ACCEPT {path.name}: volume={volume}", flush=True)
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
        report["document_count_before"] = documents_before
        report["document_count_after"] = app.Documents.Count
        (args.output / "catia_accuracy_acceptance.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        pythoncom.CoUninitialize()
    assert report["document_count_before"] == report["document_count_after"]
    print(json.dumps({"products": len(report["products"]), "errors": report["errors"]}))


if __name__ == "__main__":
    main()
