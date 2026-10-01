from pathlib import Path
import shutil
root = Path(__file__).resolve().parents[1]
source = root / 'dsh-pet-tools/node_modules/electron/dist'
assert (source / 'electron.exe').is_file(), 'Run electron/install.js first'
shutil.copytree(source, root/'dsh-pet/electron', dirs_exist_ok=True)
print('Portable Electron packaged:', (source/'version').read_text().strip())
