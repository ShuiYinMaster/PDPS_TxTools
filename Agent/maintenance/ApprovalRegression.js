const fs = require('fs');
const path = require('path');
const vm = require('vm');
const assert = require('assert');
const html = fs.readFileSync(process.argv[2] || path.join(__dirname, '../UI/chat.html'), 'utf8');
for (const match of html.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/gi)) new vm.Script(match[1]);
function element() {
    const classes = new Set();
    return {
        dataset: {}, children: [], innerHTML: '', textContent: '',
        classList: { add: x => classes.add(x), remove: x => classes.delete(x), contains: x => classes.has(x) },
        appendChild(x) { this.children.push(x); }, focus() {}
    };
}
const nodes = {};
const sent = [];
const context = vm.createContext({
    document: { getElementById: id => nodes[id] || (nodes[id] = element()), createElement: element },
    postToHost: msg => sent.push(msg), setTimeout: fn => fn()
});
context.window = context;
// Load the real approval implementation, excluding unrelated chat startup code.
vm.runInContext(html.slice(html.indexOf('function onAskApproval(msg)'), html.indexOf('let auReaders')), context);
vm.runInContext(html.slice(html.indexOf('function dismissApprovalModal()'), html.indexOf('//  Markdown 渲染'))
    .split('// ═')[0], context);
const dispatchStart = html.indexOf('window.dispatchMessage = function');
vm.runInContext(html.slice(dispatchStart, html.indexOf('\n};', dispatchStart) + 3), context);
context.onAskApproval({ requestId: 'first', kind: 'code', code: 'do_work()' });
assert(nodes['approval-modal'].classList.contains('show'), 'approval opens independently of collapsed tool bodies');
assert.strictEqual(nodes['approval-modal'].dataset.requestId, 'first');
context.closeApprovalModal(true);
assert.strictEqual(sent[0].requestId, 'first');
assert.strictEqual(sent[0].allow, true);
assert(!nodes['approval-modal'].classList.contains('show'));
context.closeApprovalModal(true);
assert.strictEqual(sent.length, 1, 'duplicate button response cannot approve a different request');
context.onAskApproval({ requestId: 'second', kind: 'generic', tool: 'write', description: 'modify' });
context.closeApprovalModal(false);
assert.strictEqual(sent[1].requestId, 'second');
assert.strictEqual(sent[1].allow, false);
context.onAskApproval({ requestId: 'cancelled', kind: 'generic' });
context.dispatchMessage({ type: 'cancelApproval', requestId: 'old-request' });
assert(nodes['approval-modal'].classList.contains('show'), 'stale cancellation cannot dismiss the current approval');
context.dispatchMessage({ type: 'cancelApproval', requestId: 'cancelled' });
assert.strictEqual(sent.length, 2, 'cancel dismissal must not masquerade as user denial');
assert.strictEqual(nodes['approval-modal'].dataset.requestId, undefined);
console.log('Approval modal regression and inline JavaScript syntax passed.');
