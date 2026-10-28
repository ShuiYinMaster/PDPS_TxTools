from pathlib import Path
import os, shutil
source_root = Path(__file__).resolve().parents[4]
local = Path(os.environ.get('TXTOOLS_LOCAL_ROOT', source_root.parent / 'TxTools.Local'))
source = local / 'dependencies/node/dsh-pet-tools/node_modules/electron/dist'
assert (source / 'electron.exe').is_file(), 'Run electron/install.js first'
shutil.copytree(source, local/'dependencies/desktop-pet/electron', dirs_exist_ok=True)
print('Portable Electron packaged:', (source/'version').read_text().strip())
