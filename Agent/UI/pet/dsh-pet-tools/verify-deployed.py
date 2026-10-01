from pathlib import Path
import hashlib, json
root = Path(__file__).resolve().parents[3]
source = root/'artifacts/dsh-pet-build'
destination = root.parents[1]/'bin'
def digest(file):
    h = hashlib.sha256()
    with file.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024*1024), b''): h.update(chunk)
    return h.hexdigest()
files = [source/'TxTools.dll', source/'TxTools.pdb'] + list((source/'Agent/UI/pet/dsh-pet').rglob('*'))
count = 0
for file in files:
    if not file.is_file(): continue
    relative = file.relative_to(source)
    deployed = destination/relative
    assert deployed.is_file(), f'Missing: {deployed}'
    assert digest(file) == digest(deployed), f'Mismatch: {deployed}'
    count += 1
result = dict(destination=str(destination), checkedFiles=count, sha256Match=True,
              backups=[str(p) for p in (destination/'maintenance-backup').glob('dsh-pet-*')])
(root/'artifacts/dsh-pet-smoke/deployment.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf8')
print(json.dumps(result, ensure_ascii=False))
