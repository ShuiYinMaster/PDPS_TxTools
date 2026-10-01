// Run with: node maintenance/RecipeMarkdownRegression.js
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const html = fs.readFileSync(path.join(__dirname, '..', 'UI', 'chat.html'), 'utf8');
const start = html.indexOf('function renderMarkdown(text)');
const end = html.indexOf('\n// ═', start);
const context = {
    escapeHtml: text => text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;'),
    linkifyPaths: text => text
};
vm.runInNewContext(html.slice(start, end), context);
const markdown = '## 配方：设备对齐\r\n\r\n**功能说明**\r\n\r\n- 选取设备\r\n- 对齐 Z 轴\r\n\r\n## 参数\r\n\r\n```json\r\n[]\r\n```\r\n\r\n## 代码\r\n\r\n````csharp\r\nvar text = "```";\r\n// ## 参数\r\n<script>alert(1)</script>\r\n````\r\n';
const rendered = context.renderMarkdown(markdown);
assert(rendered.includes('<h2>配方：设备对齐</h2>'));
assert(rendered.includes('<strong>功能说明</strong>'));
assert(rendered.includes('<ul><li>选取设备</li><li>对齐 Z 轴</li></ul>'));
assert(rendered.includes('code-block-lang">json</span>'));
assert(rendered.includes('code-block-lang">csharp</span>'));
assert(rendered.includes('var text = &quot;```&quot;;'));
assert(!rendered.includes('<script>'));
assert(!rendered.includes('<p><div class="code-block">'), 'code blocks cannot be nested in paragraphs');
assert(!rendered.includes('\r') && !rendered.includes('\u0001'));
assert.strictEqual(rendered, context.renderMarkdown(markdown.replace(/\r\n/g, '\n')));
const inline = context.renderMarkdown('使用 `robot`，保留 **说明**。');
assert(inline.includes('<code>robot</code>') && inline.includes('<strong>说明</strong>'));
assert(context.renderMarkdown('~~~python\nprint("ok")\n~~~').includes('code-block-lang">python</span>'));
// Check every inline script, in addition to the extracted renderer.
for (const match of html.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/gi)) new vm.Script(match[1]);
console.log('Recipe Markdown and inline JavaScript regression passed.');
