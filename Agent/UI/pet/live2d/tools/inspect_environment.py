"""Read local Cubism installation metadata without changing the system."""
import json
import os
from pathlib import Path
import winreg

found = []
for hive, registry_path in (
    (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
    (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
    (winreg.HKEY_CURRENT_USER, r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
):
    try:
        with winreg.OpenKey(hive, registry_path) as registry:
            for index in range(winreg.QueryInfoKey(registry)[0]):
                try:
                    with winreg.OpenKey(registry, winreg.EnumKey(registry, index)) as entry:
                        name = str(winreg.QueryValueEx(entry, "DisplayName")[0])
                        if not any(word in name.lower() for word in ("live2d", "cubism")):
                            continue
                        record = {"name": name}
                        for value in ("InstallLocation", "DisplayIcon", "DisplayVersion"):
                            try:
                                record[value] = winreg.QueryValueEx(entry, value)[0]
                            except OSError:
                                pass
                        found.append(record)
                except OSError:
                    pass
    except OSError:
        pass

for root in (Path(r"C:\Program Files"), Path(r"C:\Program Files (x86)"),
             Path(os.environ.get("LOCALAPPDATA", "")) / "Programs"):
    try:
        if root.exists():
            children = list(root.iterdir())
        else:
            children = []
    except OSError:
        children = []
    for child in children:
            if any(word in child.name.lower() for word in ("live2d", "cubism")):
                found.append({"directory": str(child)})
print(json.dumps({"cubism_installations": found}, ensure_ascii=False, indent=2))
