const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const root=path.resolve(__dirname,'..');
function renderer(file){
 const html=fs.readFileSync(path.join(root,file),'utf8');
 const start=html.indexOf('function escapeHtml(');
 const end=html.indexOf('\n}',html.indexOf('    return text;',html.indexOf('function renderMarkdown(text)')))+2;
 const context={linkifyPaths:text=>text};vm.runInNewContext(html.slice(start,end),context);
 for(const match of html.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/gi))new vm.Script(match[1]);
 return context.renderMarkdown;
}
const quick=renderer('UI/recipe-quick.html'),agent=renderer('UI/chat.html');
const samples=[
 '## 用途\r\n\r\n**设备对齐**与 *坐标检查*，输入 `robot`。',
 '- 选取设备\n- 对齐 Z 轴\n\n1. 检查结果\n2. 保存',
 '| 参数 | 说明 |\n| :--- | ---: |\n| gap | 2.5 |\n',
 '> 请先选择对象\n> 检查当前研究\n\n[文档](https://example.com/help)',
 '````csharp\r\nvar text = "```";\r\n// **保持原样**\r\n<script>alert(1)</script>\r\n````',
 '~~~python\nprint("ok")\n~~~',
 '<img src=x onerror=alert(1)>\n\n[危险](javascript:alert(1))\n\n[文档](https://example.com/" onclick="alert(1))',
 '普通文本\n第二行'
];
for(const text of samples){
 const output=quick(text);assert.equal(output,agent(text),'Quick Markdown differs from Agent');
 assert.equal(output,quick(text.replace(/\r\n/g,'\n')),'CRLF mismatch');
 assert.ok(!output.includes('\u0001')&&!output.includes('<script>')&&!output.includes('<img '));
}
assert.ok(quick(samples[0]).includes('<strong>设备对齐</strong>'));
assert.ok(quick(samples[2]).includes('<table class="md-table">'));
assert.ok(quick(samples[4]).includes('var text = &quot;```&quot;;'));
assert.ok(!quick(samples[6]).includes('href="javascript:'));
console.log('PASS: quick/Agent Markdown parity, titles/emphasis/lists/tables/quotes/links/code, CRLF, HTML escaping, inline script syntax');
