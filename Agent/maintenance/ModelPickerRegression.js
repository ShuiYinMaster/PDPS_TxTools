// Verify the production model picker against the host's grouped and legacy protocols.
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const html = fs.readFileSync(path.join(__dirname, '../UI/chat.html'), 'utf8');
const ids = {};
const document = {
    activeElement: null, listeners: {},
    getElementById: id => ids[id],
    createElement: tag => new Element(tag),
    createElementNS: (ns, tag) => new Element(tag),
    addEventListener(type, fn) { this.listeners[type] = fn; }
};
class Element {
    constructor(tag) {
        this.tagName = tag.toUpperCase(); this.children = []; this.dataset = {};
        this.attributes = {}; this.style = {}; this.listeners = {}; this.hidden = true;
        this.offsetWidth = 300; this.textContent = ''; this.className = '';
    }
    appendChild(child) { child.parent = this; this.children.push(child); return child; }
    set innerHTML(value) { assert.strictEqual(value, ''); this.children = []; }
    setAttribute(name, value) { this.attributes[name] = value; }
    getAttribute(name) { return this.attributes[name]; }
    addEventListener(type, fn) { this.listeners[type] = fn; }
    dispatchEvent(event) { if (this.listeners[event.type]) this.listeners[event.type]({ ...event, target: this }); }
    focus() { document.activeElement = this; }
    contains(child) { return this === child || this.children.some(c => c.contains(child)); }
    getBoundingClientRect() { return { left: 650, bottom: 40 }; }
    descendants() { return this.children.flatMap(c => [c, ...c.descendants()]); }
    querySelectorAll(selector) {
        return this.descendants().filter(c => selector === '[aria-selected="true"]'
            ? c.attributes['aria-selected'] === 'true' : c.className.split(' ').includes(selector.slice(1)));
    }
    querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
    get options() { return this.descendants().filter(c => c.tagName === 'OPTION'); }
    get selectedIndex() {
        const i = this.options.findIndex(o => o._selected);
        return i >= 0 ? i : this.options.length ? 0 : -1;
    }
    set selectedIndex(index) { this.options.forEach((o, i) => o._selected = i === index); }
    get selectedOptions() { return this.selectedIndex < 0 ? [] : [this.options[this.selectedIndex]]; }
    selectOwner() { let parent = this.parent; while (parent && parent.tagName !== 'SELECT') parent = parent.parent; return parent; }
    get selected() { const sel = this.selectOwner(); return sel ? sel.selectedOptions[0] === this : !!this._selected; }
    set selected(value) { const sel = this.selectOwner(); if (sel && value) sel.options.forEach(o => o._selected = false); this._selected = value; }
}
for (const [id, tag] of [['model-picker','div'], ['model-select','select'], ['model-trigger','button'], ['model-menu','div']]) ids[id] = new Element(tag);
ids['model-picker'].appendChild(ids['model-trigger']);
ids['model-picker'].appendChild(ids['model-menu']);
const messages = [];
const context = {
    document, Event: class { constructor(type) { this.type = type; } },
    state: { providers: [], currentProviderId: 'deepseek' },
    window: { innerWidth: 800, innerHeight: 600, addEventListener() {} },
    postToHost: message => messages.push(message)
};
vm.createContext(context);
const data = html.match(/const MODEL_LOGOS = (.*);/)[0];
vm.runInContext(data, context);
vm.runInContext(html.slice(html.indexOf('function modelBrand('), html.indexOf('function onKeyReady()')), context);
const eventsStart = html.indexOf('document.getElementById("model-select").addEventListener("change"');
vm.runInContext(html.slice(eventsStart, html.indexOf('initModelPicker();', eventsStart) + 'initModelPicker();'.length), context);

const providers = [
    { id: 'deepseek', displayName: 'DeepSeek', models: ['deepseek-v4-pro', 'deepseek-v4-flash'] },
    { id: 'qwen', displayName: '千问', models: ['deepseek-v4-pro', 'qwen3.6-plus', 'kimi-k2.6'] },
    { id: 'custom', displayName: '<img src=x onerror=alert(1)>', models: ['claude-sonnet-4', 'unknown-model'] }
];
context.onModelList(null, 'deepseek-v4-pro', providers, 'qwen');
const sel = ids['model-select'], menu = ids['model-menu'], trigger = ids['model-trigger'];
assert.strictEqual(sel.selectedOptions[0].value, 'qwen|deepseek-v4-pro');
assert.strictEqual(trigger.querySelector('.model-label').textContent, 'deepseek-v4-pro');
assert.strictEqual(trigger.querySelector('.model-logo').src, menu.querySelector('.model-logo').src);
assert.strictEqual(trigger.title, '千问 / deepseek-v4-pro');
assert.strictEqual(menu.children[2].children[0].textContent, providers[2].displayName);
assert.strictEqual(menu.querySelectorAll('.model-option').length, 7);
assert.strictEqual(menu.querySelectorAll('[aria-selected="true"]').length, 1);

context.openModelPicker();
assert.strictEqual(menu.hidden, false);
assert.strictEqual(menu.style.left, '492px', 'popup stays inside the viewport');
assert.strictEqual(document.activeElement, menu.querySelectorAll('.model-option')[2]);
let prevented = false;
menu.listeners.keydown({ key: 'ArrowDown', preventDefault() { prevented = true; } });
assert(prevented);
assert.strictEqual(document.activeElement, menu.querySelectorAll('.model-option')[3]);
menu.listeners.keydown({ key: 'End', preventDefault() {} });
assert.strictEqual(document.activeElement, menu.querySelectorAll('.model-option')[6]);
menu.listeners.keydown({ key: 'Escape', preventDefault() {} });
assert(menu.hidden);
assert.strictEqual(document.activeElement, trigger);

// The CSS caps the menu width on narrow screens; positioning honors that measured width.
context.window.innerWidth = 240;
menu.offsetWidth = 224;
context.openModelPicker();
assert.strictEqual(menu.style.left, '8px');
context.closeModelPicker(false);
context.window.innerWidth = 800;
menu.offsetWidth = 300;

context.openModelPicker();
menu.querySelectorAll('.model-option')[0].onclick();
assert.deepStrictEqual(JSON.parse(JSON.stringify(messages.pop())), { type: 'switchModel', model: 'deepseek-v4-pro', providerId: 'deepseek' });
assert.strictEqual(context.state.currentProviderId, 'deepseek');
assert.strictEqual(trigger.title, 'DeepSeek / deepseek-v4-pro');
assert(menu.hidden);
context.openModelPicker();
document.listeners.click({ target: new Element('div') });
assert(menu.hidden);

for (const [model, expected] of [
    ['qwen2.5:7b','qwen'], ['deepseek-ai/DeepSeek-R1','deepseek'], ['kimi-k3','kimi'],
    ['gpt-4o','openai'], ['o3-mini','openai'], ['claude-sonnet-4','claude'],
    ['gemini-2.5-pro','gemini'], ['glm-4.5','glm'], ['grok-4','grok'],
    ['MiniMax-M2','minimax'], ['doubao-pro','doubao'], ['llama3.1:8b','llama'],
    ['unknown-model',null]
]) assert.strictEqual(context.modelBrand(model, { id: 'custom' }), expected);
assert.strictEqual(context.modelBrand('local-model', { id: 'ollama' }), 'ollama');
assert.strictEqual(context.createModelLogo('unknown-model', { id: 'custom' }).tagName, 'SVG');

context.onModelList(['gpt-4o', 'qwen2.5'], 'qwen2.5');
assert.strictEqual(sel.selectedOptions[0].value, 'qwen2.5');
assert.strictEqual(trigger.querySelector('.model-label').textContent, 'qwen2.5');
menu.querySelectorAll('.model-option')[0].onclick();
assert.strictEqual(messages.pop().model, 'gpt-4o', 'legacy options keep the original model id');
context.onModelList([], null);
assert(trigger.disabled);
assert.strictEqual(trigger.querySelector('.model-label').textContent, '暂无可用模型');
assert.strictEqual(menu.querySelectorAll('.model-option').length, 0);

const logos = vm.runInContext('MODEL_LOGOS', context);
for (const [brand, uri] of Object.entries(logos)) {
    assert(uri.startsWith('data:image/svg+xml;base64,'));
    const svg = Buffer.from(uri.split(',')[1], 'base64').toString();
    assert.strictEqual(svg, fs.readFileSync(path.join(__dirname, '../UI/model-logos', brand + '.svg'), 'utf8'));
    assert(!/<script|<foreignObject|\son\w+\s*=/i.test(svg));
}
console.log('Model picker regression passed: logos, grouped routing, legacy lists, keyboard and offline assets.');
