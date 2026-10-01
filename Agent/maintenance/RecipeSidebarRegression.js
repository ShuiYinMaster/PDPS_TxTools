// Run with: node maintenance/RecipeSidebarRegression.js
// Exercises the browser-to-host recipe flow without Process Simulate.
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const vm = require('vm');

class Element {
    constructor(tag) {
        this.tag = tag;
        this.children = [];
        this.className = '';
        this._text = '';
        this._html = '';
        this.disabled = false;
        this.classList = {
            contains: name => this.className.split(' ').includes(name),
            add: name => { if (!this.classList.contains(name)) this.className += ' ' + name; },
            toggle: (name, force) => {
                const add = force === undefined ? !this.classList.contains(name) : !!force;
                if (!add)
                    this.className = this.className.split(' ').filter(x => x !== name).join(' ');
                else this.classList.add(name);
                return add;
            }
        };
    }
    appendChild(child) { this.children.push(child); return child; }
    setAttribute(name, value) { if (!this.attributes) this.attributes = {}; this.attributes[name] = String(value); }
    getAttribute(name) { return this.attributes && this.attributes[name]; }
    focus() { this.focused = true; }
    set innerHTML(value) { this._html = String(value); this._text = ''; this.children = []; }
    get innerHTML() { return this._html; }
    set textContent(value) { this._text = String(value); this._html = ''; this.children = []; }
    get textContent() { return this._text + this._html.replace(/<[^>]*>/g, '') + this.children.map(x => x.textContent).join(''); }
}

function find(node, predicate) {
    if (predicate(node)) return node;
    for (const child of node.children) {
        const found = find(child, predicate);
        if (found) return found;
    }
    return null;
}

const sent = [];
const context = {
    window: { chrome: { webview: { postMessage: json => sent.push(JSON.parse(json)) } },
        devicePixelRatio: 1.5, getComputedStyle: () => ({ getPropertyValue: () => '300px' }) },
    document: {
        createElement: tag => new Element(tag),
        createTextNode: value => { const node = new Element('#text'); node.textContent = value; return node; }
    },
    setTimeout: () => 1,
    clearTimeout: () => {},
    alert: message => { throw new Error(message); },
    console
};
// Exercise the sidebar with the actual shared renderer, instead of a formatting stub.
const chatHtml = fs.readFileSync(path.join(__dirname, '..', 'UI', 'chat.html'), 'utf8');
const rendererStart = chatHtml.indexOf('function escapeHtml(s)');
const rendererEnd = chatHtml.indexOf('\n// ═', chatHtml.indexOf('function renderMarkdown(text)'));
context.linkifyPaths = text => text;
vm.runInNewContext(chatHtml.slice(rendererStart, rendererEnd), context);
context.window.renderMarkdown = context.renderMarkdown;
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '..', 'UI', 'recipe-sidebar.js'), 'utf8'), context);

const shell = new Element('div');
context.window.txRecipes.mount(shell);
const root = shell.children[0];
const toggle = shell.children[1];
const list = sent.pop();
const initialSidebar = sent.pop();
assert.strictEqual(initialSidebar.type, 'recipe.sidebar');
assert.strictEqual(initialSidebar.open, false, 'reload must tell host to reclaim the expanded width');
assert(root.classList.contains('rcp-collapsed'), 'sidebar should start collapsed');
assert.strictEqual(toggle.textContent, '⟨');
toggle.onclick();
assert(!root.classList.contains('rcp-collapsed'), 'toggle should open sidebar');
const openSidebar = sent.pop();
assert.strictEqual(openSidebar.type, 'recipe.sidebar');
assert.strictEqual(openSidebar.open, true);
assert.strictEqual(openSidebar.width, 300);
assert.strictEqual(openSidebar.pixelRatio, 1.5, 'host must receive CSS-to-native pixel scaling');
context.window.txRecipes.onHostMessage({ type: 'recipe.sidebar.layout', overlay: true });
assert(root.classList.contains('rcp-overlay'), 'limited screen space should use overlay layout');
toggle.onclick();
assert.strictEqual(sent.pop().open, false, 'collapse must notify the host');
assert(root.classList.contains('rcp-collapsed'));
context.window.txRecipes.onHostMessage({ type: 'recipe.sidebar.layout', overlay: false });
assert(!root.classList.contains('rcp-overlay'));
toggle.onclick();
assert.strictEqual(sent.pop().open, true);

assert.strictEqual(list.type, 'recipe.list');
context.window.txRecipes.onHostMessage({
    type: 'recipe.list.result', seq: list.seq, ok: true, study: 'Study A', candidates: [],
    recipes: [{ id: 'r1', name: 'Test recipe', lang: 'csharp', params: [
        { name: 'robot', label: 'Robot', kind: 'object', required: true }
    ] }]
});
const myRecipesTab = find(root, e => e.id === 'rcp-tab-recipes');
const candidatesTab = find(root, e => e.id === 'rcp-tab-candidates');
assert.strictEqual(myRecipesTab.textContent, '我的配方（1）');
assert.strictEqual(candidatesTab.textContent, '待固化（0）');
assert.strictEqual(myRecipesTab.getAttribute('aria-selected'), 'true');
assert(find(root, e => e.className.includes('rcp-status-ready') && e.textContent === '已固化'));
let run = find(root, e => e.tag === 'button' && e.textContent === '执行');
assert(run && run.disabled, 'execution should require a selected object');
const pick = find(root, e => e.tag === 'button' && e.textContent === '取选择');
assert(pick);
pick.onclick();
const pickRequest = sent.pop();
assert.strictEqual(pickRequest.type, 'recipe.pickSelection');
context.window.txRecipes.onHostMessage({
    type: 'recipe.pick.result', seq: pickRequest.seq, ok: true,
    id: '3,57,2,1', name: 'Robot A', objectType: 'TxRobot', count: 1, study: 'Study A'
});
run = find(root, e => e.tag === 'button' && e.textContent === '执行');
assert(run && !run.disabled, 'selection response should enable execution');
candidatesTab.onclick();
assert(!find(root, e => e.className.includes('rcp-card')), 'candidate tab must not include saved recipe cards');
assert(find(root, e => e.className === 'rcp-empty' && e.textContent.includes('暂无待固化')));
myRecipesTab.onclick();
run = find(root, e => e.tag === 'button' && e.textContent === '执行');
assert(run && !run.disabled, 'switching tabs must preserve parameter bindings');
run.onclick();
const runRequest = sent.pop();
assert.strictEqual(runRequest.type, 'recipe.run');
assert.strictEqual(runRequest.study, 'Study A');
assert.strictEqual(runRequest.args.robot, '3,57,2,1');
context.window.txRecipes.onHostMessage({
    type: 'recipe.run.result', seq: runRequest.seq, ok: false,
    error: '编译失败：测试错误'
});
assert(find(root, e => e.className.includes('rcp-msg-bad') && e.textContent.includes('编译失败')),
    'execution error should be visible on the recipe card');
console.log('Recipe sidebar regression passed.');

// Sharing and candidate capabilities must remain reachable even when there are no saved recipes.
context.window.txRecipes.refresh();
const refreshRequest = sent.pop();
context.window.txRecipes.onHostMessage({
    type: 'recipe.list.result', seq: refreshRequest.seq, ok: true, study: 'Study A', recipes: [],
    candidates: [{ name: 'auto_opaque_123', description: '批量对齐选中设备的 Z 轴',
        tags: ['alignment', 'device'], lang: 'python', codePreview: '<script>unsafe</script>',
        successCount: 3, failureCount: 1, previewTruncated: true }]
});
assert.strictEqual(myRecipesTab.textContent, '我的配方（0）');
assert.strictEqual(candidatesTab.textContent, '待固化（1）');
assert(!find(root, e => e.className === 'rcp-cand'), 'default recipes tab must not display candidates');
find(root, e => e.tag === 'button' && e.textContent === '查看待固化（1）').onclick();
assert.strictEqual(candidatesTab.getAttribute('aria-selected'), 'true');
assert(find(root, e => e.className === 'rcp-candidate-note' && e.textContent.includes('整理用途和参数')));
assert(find(root, e => e.className.includes('rcp-status-pending') && e.textContent === '待整理'));
assert(find(root, e => e.className === 'rcp-name' && e.textContent === '批量对齐选中设备的 Z 轴'));
assert(find(root, e => e.className === 'rcp-source-name' && e.textContent.includes('auto_opaque_123')));
assert(find(root, e => e.className.includes('rcp-cand-desc') && e.textContent.includes('对齐')),
    'candidate purpose should be visible without opening details');
assert(find(root, e => e.className === 'rcp-tag' && e.textContent === '对齐' && e.title === 'alignment'));
assert(find(root, e => e.tag === 'pre' && e.textContent === '<script>unsafe</script>'),
    'code preview should preserve literal text');
assert(find(root, e => e.className === 'rcp-lang' && e.textContent === 'PY'));
find(root, e => e.tag === 'button' && e.textContent === '详情').onclick();
const revealRequest = sent.pop();
assert.strictEqual(revealRequest.type, 'recipe.reveal');
assert.strictEqual(revealRequest.snippetName, 'auto_opaque_123');
context.window.txRecipes.onHostMessage({ type: 'recipe.reveal.result', seq: revealRequest.seq, ok: false, error: '片段不存在。' });
assert(find(root, e => e.className.includes('rcp-notice') && e.textContent === '片段不存在。'));
find(root, e => e.tag === 'button' && e.textContent === '整理为配方').onclick();
const promoteRequest = sent.pop();
assert.strictEqual(promoteRequest.snippetName, 'auto_opaque_123');
context.window.txRecipes.onHostMessage({ type: 'recipe.promote.result', seq: promoteRequest.seq, ok: true });

sent.length = 0;
const importButton = find(root, e => e.tag === 'button' && e.textContent === '导入');
importButton.onclick();
const importRequest = sent.pop();
assert.strictEqual(importRequest.type, 'recipe.import');
assert(importButton.disabled);
context.window.txRecipes.onHostMessage({ type: 'recipe.import.result', seq: importRequest.seq, ok: false, error: '参数区不是合法 JSON 数组。' });
assert(!importButton.disabled);
assert(find(root, e => e.className.includes('rcp-msg-bad') && e.textContent.includes('JSON')));
importButton.onclick();
const cancelledRequest = sent.pop();
context.window.txRecipes.onHostMessage({ type: 'recipe.import.result', seq: cancelledRequest.seq, ok: true, cancelled: true });
assert(!importButton.disabled);
assert.strictEqual(sent.length, 0, 'cancel must not request a refresh');
sent.length = 0;
importButton.onclick();
const successRequest = sent.pop();
context.window.txRecipes.onHostMessage({ type: 'recipe.import.result', seq: successRequest.seq, ok: true, recipeId: 'import_new', text: '已导入配方。' });
const importedList = sent.pop();
assert.strictEqual(importedList.type, 'recipe.list');
context.window.txRecipes.onHostMessage({ type: 'recipe.list.result', seq: importedList.seq, ok: true, study: 'Study A', candidates: [],
    recipes: [{ id: 'import_new', name: 'Imported', lang: 'python', params: [{ name: 'robot', kind: 'object', required: true }] }] });
assert.strictEqual(myRecipesTab.getAttribute('aria-selected'), 'true', 'successful import must return to saved recipes');
assert(find(root, e => e.className.includes('rcp-card rcp-open')), 'imported recipe should expand');
assert(find(root, e => e.textContent === '执行').disabled, 'import must not reuse object bindings');
const exportButton = find(root, e => e.tag === 'button' && e.textContent === '导出');
exportButton.onclick();
const exportRequest = sent.pop();
assert.strictEqual(exportRequest.type, 'recipe.export');
assert.strictEqual(exportRequest.recipeId, 'import_new');
context.window.txRecipes.onHostMessage({ type: 'recipe.export.result', seq: exportRequest.seq, ok: true, text: '已导出配方：test.md' });
assert(!exportButton.disabled);
assert(find(root, e => e.className.includes('rcp-msg-ok') && e.textContent.includes('test.md')));
console.log('Recipe sharing and candidate regression passed.');

// The actual imported recipe's usage guide should render in the sidebar itself.
const recipeFile = fs.readFileSync(path.join(__dirname, '..', 'recipes', '批量调整机器人与资源姿态.md'), 'utf8');
const usageGuide = recipeFile.replace(/^---\r?\n[\s\S]*?\r?\n---\r?\n/, '').split('## 参数')[0];
context.window.txRecipes.refresh();
const markdownRequest = sent.pop();
context.window.txRecipes.onHostMessage({ type: 'recipe.list.result', seq: markdownRequest.seq, ok: true,
    study: 'Study A', recipes: [{ id: 'markdown', name: 'Markdown recipe', params: [], description: usageGuide }],
    candidates: [{ name: 'candidate', description: '### 用途\r\n\r\n**对齐设备**\r\n\r\n- 选中设备\r\n- 批量处理\r\n\r\n<script>alert(1)</script>' }] });
const description = find(root, e => e.className.includes('rcp-desc'));
assert(description && description.tag === 'div', 'block Markdown must use a div container');
assert(description.innerHTML.includes('<h3>使用方法</h3>'));
assert(description.innerHTML.includes('<strong>姿态名称可选填</strong>'));
assert(description.innerHTML.includes('<code>HOME</code>'));
assert(description.innerHTML.includes('<ol>') && description.innerHTML.includes('<ul>'));
assert(!description.innerHTML.includes('### 使用方法'));
candidatesTab.onclick();
const candidateDescription = find(root, e => e.className.includes('rcp-cand-desc'));
assert(candidateDescription.innerHTML.includes('<h3>用途</h3>'));
assert(candidateDescription.innerHTML.includes('<strong>对齐设备</strong>'));
assert(candidateDescription.innerHTML.includes('&lt;script&gt;') && !candidateDescription.innerHTML.includes('<script>'),
    'imported HTML must remain escaped');
assert(find(root, e => e.className === 'rcp-name' && e.textContent === '对齐设备'), 'generic Markdown headings must not become the capability title');
assert(!find(root, e => e.className.includes('rcp-card')));
assert.strictEqual(candidatesTab.textContent, '待固化（1）', 'refresh should update counts without changing selected tab');
let prevented = false;
candidatesTab.onkeydown({ key: 'Home', preventDefault: () => { prevented = true; } });
assert(prevented && myRecipesTab.focused && myRecipesTab.getAttribute('aria-selected') === 'true',
    'keyboard tab navigation should update selection and focus');

delete context.window.renderMarkdown;
context.window.txRecipes.refresh();
const fallbackRequest = sent.pop();
context.window.txRecipes.onHostMessage({ type: 'recipe.list.result', seq: fallbackRequest.seq, ok: true,
    study: 'Study A', candidates: [], recipes: [{ id: 'fallback', name: 'Fallback', params: [], description: '<img onerror=alert(1)>\n说明' }] });
const fallback = find(root, e => e.className.includes('rcp-markdown-fallback'));
assert(fallback && fallback.innerHTML === '' && fallback.textContent === '<img onerror=alert(1)>\n说明',
    'missing renderer must fall back to literal text');
console.log('Recipe sidebar Markdown regression passed.');
console.log('Recipe tabs, capability titles and status regression passed.');
