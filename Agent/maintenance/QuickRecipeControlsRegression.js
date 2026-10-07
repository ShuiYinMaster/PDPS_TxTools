// Exercises the production quick-panel script with a minimal DOM and host bridge.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
class Element {
    constructor(tag) {
        this.style = {};
        this.tag = tag; this.children = []; this.className = ''; this._text = ''; this.dataset = {};
        this.classList = { toggle: (name, force) => {
            const classes = new Set(this.className.split(' ').filter(Boolean));
            const add = force === undefined ? !classes.has(name) : force;
            if (add) classes.add(name); else classes.delete(name);
            this.className = [...classes].join(' ');
        } };
    }
    append(...nodes) { this.children.push(...nodes); }
    get childNodes() { return this.children; }
    replaceChildren(...nodes) { this.children = nodes; }
    setAttribute(name, value) { this[name] = value; }
    set innerHTML(value) { this._text = value; this.children = []; }
    set textContent(value) { this._text = String(value); this.children = []; }
    get textContent() { return this._text + this.children.map(x => x.textContent).join(''); }
    querySelectorAll() { return []; }
}
function find(node, predicate) {
    if (predicate(node)) return node;
    for (const child of node.children) { const match = find(child, predicate); if (match) return match; }
    return null;
}
const root = new Element('body');
for (const id of ['content','search','study','count','notice','refresh','manage','close']) {
    const node = new Element('div'); node.id = id; node.value = ''; root.append(node);
}
const tabs = ['home','recent','all'].map(tab => { const node = new Element('button'); node.dataset.tab = tab; root.append(node); return node; });
const messages = []; let receive;
const context = {
    window: { chrome: { webview: { postMessage: msg => messages.push(msg), addEventListener: (event, fn) => receive = fn } } },
    document: { getElementById: id => find(root, e => e.id === id), createElement: tag => new Element(tag),
        createTextNode: text => { const node = new Element('#text'); node.textContent = text; return node; },
        querySelectorAll: selector => selector === '[data-tab]' ? tabs : [], addEventListener: () => {} },
    requestAnimationFrame: () => {}, setTimeout: () => {}, clearTimeout: () => {}, navigator: {}, console
};
const html = fs.readFileSync(path.join(__dirname, '../UI/recipe-quick.html'), 'utf8');
vm.runInNewContext(html.match(/<script>([\s\S]*?)<\/script>/)[1], context);
const reply = (request, body) => receive({ data: { ...body, seq: request.seq } });
const node = predicate => find(root, predicate);
const button = text => node(e => e.tag === 'button' && e.textContent === text);
const id = name => context.document.getElementById(name);
const recipe = { id: 'controls', name: '显示或隐藏对象', description: '选择对象后执行。', params: [
    { name: 'targets', label: '对象', kind: 'objects', required: true },
    { name: 'show', label: '显示模式', kind: 'bool', required: true },
    { name: 'mode', label: '范围', kind: 'text', required: true, def: 'all', choices: [{ label: '全部', value: 'all' }, { label: '部分', value: 'some' }] },
    { name: 'children', label: '包含下属对象', kind: 'bool', def: 'false' },
    { name: 'gap', label: '间距', kind: 'number', required: true, def: '2.5' }
], actions: [{ id: 'show', label: '显示', args: { show: 'true' } }, { id: 'hide', label: '隐藏', args: { show: 'false' } }] };
const list = { ok: true, type: 'recipe.list.result', study: 'Study A', recipes: [recipe], favorites: [], recent: [], savedArgs: { controls: { mode: 'some', children: 'false' } } };
reply(messages.pop(), list);
node(e => e.className === 'card-main').onclick();
assert.equal(id('param-show'), null, 'button-supplied parameters hidden');
assert.equal(id('param-mode').tag, 'select');
assert.equal(id('param-mode').value, 'some');
assert.equal(id('param-children').checked, false);
assert.equal(id('param-gap').step, 'any');
assert.equal(button('隐藏').disabled, true);
button('取当前选择').onclick();
const pick = messages.pop();
reply(pick, { ok: true, study: 'Study A', id: '3,1|3,2', name: '目标', count: 2 });
assert.equal(button('隐藏').disabled, false);
button('隐藏').onclick();
const run = messages.pop();
assert.equal(run.type, 'recipe.run');
assert.equal(run.actionId, 'hide');
assert.equal(run.args.targets, '3,1|3,2');
assert.equal(run.args.mode, 'some');
const sentCount = messages.length;
id('run').onclick();
assert.equal(messages.length, sentCount, 'busy state prevents another button from sending');
assert.equal(id('run').disabled, true);
reply(run, { ok: true, text: '完成' });
reply(messages.pop(), list);
assert.equal(button('显示').disabled, false);
assert.equal(button('隐藏').disabled, false);
button('恢复默认值').onclick();
assert.equal(id('param-mode').value, 'all');
assert.equal(button('隐藏').disabled, false, 'reset retains objects');
id('param-mode').value = ''; id('param-mode').onchange();
assert.equal(button('隐藏').disabled, true);
id('param-mode').value = 'all'; id('param-mode').onchange();
id('param-gap').value = 'Infinity'; id('param-gap').oninput();
assert.equal(button('隐藏').disabled, true, 'finite number required');
id('param-gap').value = '0.25'; id('param-gap').oninput();
assert.equal(button('隐藏').disabled, false);
receive({ data: { type: 'recipe.studyChanged', study: 'Study B' } });
reply(messages.pop(), { ...list, study: 'Study B' });
assert.equal(button('隐藏').disabled, true);
assert(node(e => e.className === 'bound' && e.textContent === '尚未选择对象'));
console.log('PASS: quick-panel actions, options, saved parameters, reset, fractional numbers, busy gate and study invalidation');

const queryRecipe={id:'query-color',name:'选择颜色与对象',params:[{name:'targets',kind:'objects',required:true,objectFilter:true,objectTypes:[{label:'全部类型',value:'all'},{label:'组件',value:'components'}]},{name:'color',kind:'color',required:true,def:'#4F83CC'}],actions:[{id:'apply',label:'应用颜色',args:{}}]};
receive({data:{...list,study:'Study B',recipes:[queryRecipe]}});node(e=>e.className==='card-main').onclick();
assert(button('按条件查找').disabled,'blank scene query disabled');id('object-type-targets').value='components';id('object-type-targets').onchange();id('object-name-targets').value='夹具';id('object-name-targets').oninput();
button('按条件查找').onclick();const searched=messages.pop();assert.equal(searched.recipeId,'query-color');assert.equal(searched.param,'targets');assert.equal(searched.search,true);assert.equal(searched.objectType,'components');assert.equal(searched.objectName,'夹具');
reply(searched,{ok:true,id:'3,10',name:'夹具',count:1,study:'Study B'});assert.equal(id('param-color').type,'color');id('param-color').value='#12abEF';id('param-color').oninput();
button('应用颜色').onclick();const colorRun=messages.pop();assert.equal(colorRun.args.color,'#12abEF');assert.equal(colorRun.args.targets,'3,10');
reply(colorRun,{ok:true,text:'完成'});reply(messages.pop(),{...list,study:'Study B',recipes:[queryRecipe]});
id('object-name-targets').value='其他';id('object-name-targets').oninput();assert(button('应用颜色').disabled,'changing name clears binding');
node(e=>e.title==='#E45B5B'&&e.tag==='button').onclick();assert.equal(id('param-color').value,'#E45B5B');
console.log('PASS: quick color picker, palette, filtered query protocol and binding invalidation');

button('刷新类型').onclick();const typeRequest=messages.pop();assert.equal(typeRequest.type,'recipe.objectTypes');assert.equal(typeRequest.recipeId,'query-color');
reply(typeRequest,{ok:true,study:'Study B',objectTypes:[{label:'全部类型',value:'all'},{label:'场景夹具',value:'Tecnomatix.Engineering.TxFixtureCustom'}]});
assert.equal(id('object-type-targets').value,'all','removed categories reset to all');assert.equal(id('object-type-targets').children.length,2);assert.equal(id('object-type-targets').children[1].textContent,'场景夹具');
id('object-type-targets').value='Tecnomatix.Engineering.TxFixtureCustom';id('object-type-targets').onchange();button('按条件查找').onclick();const dynamicPick=messages.pop();assert.equal(dynamicPick.objectType,'Tecnomatix.Engineering.TxFixtureCustom');reply(dynamicPick,{ok:true,id:'3,20',name:'动态夹具',count:1,study:'Study B'});assert(!button('应用颜色').disabled);
button('刷新类型').onclick();reply(messages.pop(),{ok:true,study:'Study B',objectTypes:[{label:'全部类型',value:'all'}]});assert.equal(id('object-type-targets').value,'all');assert(button('应用颜色').disabled,'category removal clears old binding');
console.log('PASS: quick dynamic scene categories, refresh, removed category reset and stale binding invalidation');

const autoTypes=[];context.setTimeout=(fn,delay)=>{if(delay===0)autoTypes.push(fn);return 1;};button('‹').onclick();const lazyRecipe=JSON.parse(JSON.stringify(queryRecipe));lazyRecipe.params[0].objectTypes=[];lazyRecipe.params[0].objectTypesLoaded=false;
receive({data:{...list,study:'Study B',recipes:[lazyRecipe]}});assert.equal(autoTypes.length,0,'recipe list never scans scene categories');node(e=>e.className==='card-main').onclick();assert(autoTypes.length>0,'opening object controls queues catalog load');while(autoTypes.length)autoTypes.shift()();const lazyTypes=messages.pop();assert.equal(lazyTypes.type,'recipe.objectTypes');reply(lazyTypes,{ok:true,study:'Study B',objectTypes:[{label:'全部类型',value:'all'}]});
console.log('PASS: quick recipe list defers scene scanning until object controls are visible');
