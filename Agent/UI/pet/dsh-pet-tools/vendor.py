"""Pin and adapt dsh-pet's desktop implementation without installing the DSH host."""
from pathlib import Path
import json, shutil, subprocess, re, runpy

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'references/dsh-pet-source/dsh-pet'
DEST = ROOT / 'dsh-pet'

def strip_comments(text):
    return re.sub(r'("(?:\\.|[^"\\])*"|//[^\n]*|/\*[\s\S]*?\*/)',
                  lambda m: m[0] if m[0].startswith('"') else '', text)

def patch_file(name, old, new):
    file = DEST / 'upstream' / name
    text = file.read_text(encoding='utf8')
    assert text.count(old) == 1, (name, old[:50])
    file.write_text(text.replace(old, new), encoding='utf8')

(DEST / 'upstream').mkdir(parents=True, exist_ok=True)
for file in (SOURCE / 'runtime/electron-helper').iterdir():
    if file.is_file(): shutil.copy2(file, DEST / 'upstream' / file.name)
for folder in ('webm', 'pic'):
    shutil.copytree(SOURCE / 'assets' / folder, DEST / 'assets' / folder, dirs_exist_ok=True)
shutil.copy2(SOURCE / 'LICENSE', DEST / 'LICENSE-dsh-pet.txt')
conf = json.loads(strip_comments((SOURCE / 'assets/config.jsonc').read_text(encoding='utf8')))
pet = conf['pets'][0]
pet.update(name='鲸鱼娘 · TxAgent', size=420, display='desktop', balanceEnabled=False,
           whisperEnabled=False, workStatusEnabled=True)
pet['position'] = dict(corner='bottom-right', marginX=24, marginY=36)
conf['notificationsEnabled'] = False
conf['animationWeights'] = dict(idle=70, turn=5, move=0)
for category, weight in zip(conf['animations']['categories'], (15, 5, 5, 0, 0)):
    category['weight'] = weight
conf['physics'].update(throwPower=0.65, restitution=0.45)
conf['workStatusTexts'] = [['正在思考'], ['正在执行工具'], ['正在整理结果'], ['等待你确认'], ['任务完成'], ['任务遇到问题']]
(DEST / 'config.json').write_text(json.dumps(conf, ensure_ascii=False, indent=2), encoding='utf8')

# Keep the upstream custom-scheme renderer, answer its requests in-process.
file = DEST / 'upstream/main.js'
text = file.read_text(encoding='utf8')
start = text.index('function bridgeRequest(method, url, body) {')
end = text.index('/** 把宿主回调应答', start)
text = text[:start] + "function bridgeRequest(method, url, body) {\n  return require('../host-routes.js').handle(method, url, body);\n}\n\n" + text[end:]
text = text.replace("app.setName('dsh-pet-electron-helper');", "app.setName('txagent-dsh-pet');")
text = text.replace('    startBridgeCallback(); // 宿主应答回调服务器（stdin 在 Electron 主进程不可用，改走本地 HTTP）', '')
start = text.index("  ipcMain.on('pet:open-site',")
end = text.index('\n  // 显示器热更新', start)
text = text[:start] + "  ipcMain.on('pet:open-site', () => {\n    process.stdout.write('txagent-pet:' + JSON.stringify({ kind: 'open-assistant' }) + '\\n');\n  });\n" + text[end:]
text = text.replace('win.once(\'ready-to-show\', () => win.show());', 'win.once(\'ready-to-show\', () => win.showInactive());')
text = text.replace('!win.isVisible()) win.show();', '!win.isVisible()) win.showInactive();')
text = text.replace(".loadFile('index.html',", ".loadFile(path.join(__dirname, 'index.html'),")
file.write_text(text, encoding='utf8')

file = DEST / 'upstream/sprite.js'
text = file.read_text(encoding='utf8')
start = text.index("    const tools = [{ label: '打开网站'")
end = text.index('    const tree = tools.concat', start)
text = text[:start] + "    const tools = [\n      { label: '打开助手', action: 'open-site' },\n      { label: '回到初始位置', action: 'home' },\n      { label: '隐藏桌宠', action: 'close-pet' },\n    ];\n" + text[end:]
text = text.replace("    if (leaf.action === 'show-balance') {", "    if (leaf.action === 'close-pet') {\n      fetch(BASE + '/close', { method: 'POST' }).catch(console.error);\n      return;\n    }\n    if (leaf.action === 'show-balance') {")
file.write_text(text, encoding='utf8')

# System font rather than redistributing the upstream font.
file = DEST / 'upstream/renderer.js'
text = file.read_text(encoding='utf8')
start = text.index('  style.textContent =')
end = text.index('  document.head.appendChild(style);', start)
text = text[:start] + '''  style.textContent =
    '.pet-hit{cursor:url("' + BASE + '/pic/cursor-grab.png") 16 16,grab}' +
    '.pet-hit.dragging{cursor:url("' + BASE + '/pic/cursor-grabbing.png") 16 16,grabbing}' +
    '.pet-bubble{font-family:"Microsoft YaHei UI",sans-serif}';
''' + text[end:]
text += "\ndocument.addEventListener('dblclick', (e) => {\n  if (e.target.closest('.pet-hit')) window.petBridge.openDshSite(ORIGIN);\n});\n"
file.write_text(text, encoding='utf8')

# A task may already be running when the helper finishes loading; show the initial state.
file = DEST / 'upstream/events.js'
text = file.read_text(encoding='utf8')
old = '          workBaseline = ts; // 首拉仅记基线'
assert old in text
text = text.replace(old, '''          workBaseline = ts;
          if (snap && snap.state) {
            workTick++;
            for (const s of sprites) s.onWorkTick(snap, workTick);
          }''')
text = text.replace('setTimeout(() => void workLoop(), 1000);', 'setTimeout(() => void workLoop(), 200);')
file.write_text(text, encoding='utf8')

runpy.run_path(str(Path(__file__).with_name('patch-quick-launch.py')))
commit = subprocess.check_output(['rtk', 'proxy', 'git', '-C', str(SOURCE.parent), 'rev-parse', 'HEAD'], text=True).strip()
manifest = dict(repository='https://github.com/PC2005-cloud/dsh-pet', commit=commit,
                version='0.3.0', webmCount=len(list((DEST/'assets/webm').glob('*.webm'))),
                adaptations=['local state file adapter', 'TxAgent menu and double-click', 'system font', 'quiet defaults', 'show without activation'])
(DEST/'UPSTREAM.json').write_text(json.dumps(manifest, indent=2), encoding='utf8')
print(json.dumps(manifest))
