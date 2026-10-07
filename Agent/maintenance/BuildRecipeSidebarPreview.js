// Generates a local visual preview using the production sidebar assets and shared Markdown renderer.
const fs = require('fs');
const path = require('path');
const base = path.join(__dirname, '..');
const chat = fs.readFileSync(path.join(base, 'UI', 'chat.html'), 'utf8');
const stylesheet = chat.match(/<style>([\s\S]*?)<\/style>/)[1];
const start = chat.indexOf('function escapeHtml(s)');
const end = chat.indexOf('\n// ═', chat.indexOf('function renderMarkdown(text)'));
const renderer = chat.slice(start, end);
const sample = {
    recipes: [{ id: 'batch_pose', name: '批量调整机器人与资源姿态', lang: 'csharp', runCount: 5, failCount: 0,
        description: '### 使用方法\n\n1. 选择需要调整的设备。\n2. 填写**姿态名称**，留空使用 `HOME`。\n3. 点击执行。\n\n- 支持机器人、焊枪和夹具。\n- 未找到姿态时跳过并提示。',
        params: [{ name: 'pose_name', label: '姿态名称（可选）', kind: 'text', required: false, def: 'HOME' },
            { name: 'targets', label: '调整范围（可选）', kind: 'objects', required: false }] },
        { id: 'export', name: '按颜色拆分并导出资源', lang: 'csharp', description: '根据资源颜色生成分组，并导出对应文件。', params: [] }],
    candidates: [{ name: 'auto_20260930_align_opaque_123', lang: 'csharp', description: '批量对齐选中设备的 Z 轴\n\n保留安装位置，将设备方向统一到指定参考轴。',
        tags: ['alignment', 'device', 'batch'], successCount: 3, failureCount: 1,
        codePreview: '// 批量对齐选中设备的 Z 轴\nvar selected = TxApplication.ActiveSelection.GetItems();\nforeach (var item in selected)\n{\n    // 根据参考方向调整设备\n}', previewTruncated: true },
        { name: 'auto_collect_labels_487', lang: 'python', description: '统计焊点并生成标注\n\n汇总当前范围的焊点数量，生成对应标注。', tags: ['weld', 'label'], successCount: 4, failureCount: 0 }]
};
const out = path.join(base, 'artifacts', 'recipe-preview');
fs.mkdirSync(out, { recursive: true });
// Display current bundled definitions, so previews track their real buttons/options.
sample.recipes = ['default_visibility', 'default_number_objects', 'default_set_color', 'default_object_coordinates', 'default_geometry_weld_points'].map(id => {
    const text = fs.readFileSync(path.join(base, 'recipes', 'defaults', id + '.md'), 'utf8');
    const params = JSON.parse(text.match(/## 参数\s+```json\s+([\s\S]*?)```/)[1]);
    const actions = text.match(/## 执行按钮\s+```json\s+([\s\S]*?)```/);
    return { id, name: text.match(/^name: (.+)$/m)[1].trim(), lang: 'csharp', description: text.replace(/^---\r?\n[\s\S]*?\r?\n---\r?\n/, '').split('## 参数')[0].trim(),
        params: params.map(p => ({ name: p.Name, label: p.Label, kind: p.Kind, required: p.Required, def: p.Default, help: p.Help, objectFilter: p.ObjectFilter, objectTypes: [], objectTypesLoaded: !p.ObjectFilter, choices: p.Choices || [] })),
        actions: actions ? JSON.parse(actions[1]) : [] };
});
const panel = `<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><style>${stylesheet}
body{margin:0;overflow:hidden;background:#fff}#shell{display:flex;width:300px;height:650px;margin:0}.rcp-toggle{display:none}
</style><link rel="stylesheet" href="../../UI/recipe-sidebar.css"></head><body><div id="shell"></div>
<script>${renderer}
function linkifyPaths(text){return text;}
const sample=${JSON.stringify(sample)};
window.chrome=window.chrome||{};
window.chrome.webview={postMessage:function(json){
const request=JSON.parse(json);
if(request.type==='recipe.objectTypes'){setTimeout(()=>window.txRecipes.onHostMessage({seq:request.seq,ok:true,study:'预览工程',objectTypes:[{label:'全部类型',value:'all'},{label:'组件',value:'Tecnomatix.Engineering.TxComponent'},{label:'机器人',value:'Tecnomatix.Engineering.TxRobot'},{label:'FixtureCustom',value:'Tecnomatix.Engineering.TxFixtureCustom'}]}),0);return;}
if(request.type==='recipe.pickSelection'){setTimeout(()=>window.txRecipes.onHostMessage({seq:request.seq,type:'recipe.pick.result',ok:true,id:'preview-object',name:'示例零件',count:2,study:'预览工程'}),0);return;}
if(request.type==='recipe.run'){setTimeout(()=>window.txRecipes.onHostMessage({seq:request.seq,type:'recipe.run.result',ok:true,text:'预览执行完成（未操作工程）'}),300);return;}
if(request.type!=='recipe.list')return;
setTimeout(function(){window.txRecipes.onHostMessage(Object.assign({type:'recipe.list.result',seq:request.seq,ok:true,study:'预览工程'},sample));
if(!window.previewReady){window.previewReady=true;document.querySelector('.rcp-toggle').click();
if(location.search.includes('candidates'))document.getElementById('rcp-tab-candidates').click();
else document.querySelector('.rcp-card-head').click();}},0);
}};</script><script src="../../UI/recipe-sidebar.js"></script><script>window.txRecipes.mount(document.getElementById('shell'));</script></body></html>`;
fs.writeFileSync(path.join(out, 'panel.html'), panel, 'utf8');
fs.writeFileSync(path.join(out, 'index.html'), `<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><title>配方栏视觉预览</title><style>
body{margin:0;padding:28px;background:#f1f5f9;color:#334155;font:14px "Microsoft YaHei",sans-serif}h1{font-size:22px;margin:0 0 8px}p{color:#64748b;margin:0 0 22px}.panels{display:flex;gap:28px}h2{font-size:14px;margin:0 0 10px}iframe{width:300px;height:650px;border:1px solid #dbe2e8;border-radius:10px;background:#fff;box-shadow:0 6px 24px #3341550a}
</style></head><body><h1>我的配方 / 待固化</h1><p>实际侧栏样式 · 每栏宽 300 像素 · 示例数据，可切换页签查看</p><div class="panels"><section><h2>已固化：用途、参数与执行</h2><iframe title="我的配方" src="panel.html?recipes"></iframe></section><section><h2>待固化：能力、验证与整理</h2><iframe title="待固化" src="panel.html?candidates"></iframe></section></div></body></html>`, 'utf8');
const quickBridge = `<script>
const previewModel=${JSON.stringify({ ...sample, type: 'recipe.list.result', ok: true, study: '预览工程', favorites: ['default_visibility'], recent: ['default_number_objects'], savedArgs: {}, busy: false })};
let previewListener;
window.chrome=window.chrome||{};
window.chrome.webview={addEventListener:(type,fn)=>previewListener=fn,postMessage:request=>{
 const reply=body=>setTimeout(()=>previewListener({data:{...body,seq:request.seq}}),0);
 if(request.type==='quick.ready'||request.type==='recipe.list')reply(previewModel);
 else if(request.type==='recipe.objectTypes')reply({ok:true,study:'预览工程',objectTypes:[{label:'全部类型',value:'all'},{label:'组件',value:'Tecnomatix.Engineering.TxComponent'},{label:'机器人',value:'Tecnomatix.Engineering.TxRobot'},{label:'FixtureCustom',value:'Tecnomatix.Engineering.TxFixtureCustom'}]});
 else if(request.type==='recipe.pickSelection')reply({ok:true,id:'preview-object',name:'示例零件',count:2,study:'预览工程'});
 else if(request.type==='recipe.run')reply({ok:true,text:'预览执行完成（未操作工程）'});
 else if(request.type==='recipe.favorite'){previewModel.favorites=request.favorite?[...previewModel.favorites,request.recipeId]:previewModel.favorites.filter(id=>id!==request.recipeId);reply(previewModel);}
}};
</script>`;
fs.writeFileSync(path.join(out, 'quick.html'), fs.readFileSync(path.join(base, 'UI', 'recipe-quick.html'), 'utf8').replace('<script>', quickBridge + '<script>'), 'utf8');
fs.writeFileSync(path.join(out, 'controls.html'), `<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><title>配方操作预览</title><style>body{margin:0;padding:20px;background:#f1f5f9;color:#334155;font:14px "Microsoft YaHei",sans-serif}h1{font-size:20px}main{display:flex;gap:20px}iframe{border:1px solid #dbe2e8;border-radius:10px;background:white;height:620px}p{color:#64748b}</style></head><body><h1>配方操作预览</h1><p>示例对象与模拟执行 · 不操作实际工程</p><main><section><h2>配方侧栏</h2><iframe title="侧栏" src="panel.html" width="300"></iframe></section><section><h2>快捷配方</h2><iframe title="快捷配方" src="quick.html" width="340"></iframe></section></main></body></html>`, 'utf8');
console.log(path.join(out, 'index.html'));
