'use strict';
// TxAgent adapter for dsh-pet's desktop renderer. All requests stay inside this process.
const fs = require('node:fs');
const path = require('node:path');
const config = require('./config.json');
let snapshot = { state: null, task: null, ts: 0, closed: false };
function readSnapshot() {
  try {
    const value = JSON.parse(fs.readFileSync(process.env.TXAGENT_PET_STATE, 'utf8'));
    if (typeof value.ts === 'number') snapshot = value;
  } catch { /* Keep the last complete snapshot during an atomic file replacement. */ }
  return snapshot;
}
function json(body, status = 200) {
  return { status, contentType: 'application/json; charset=utf-8', body: JSON.stringify(body) };
}
function asset(folder, name, extension, contentType) {
  if (!name || name.includes('/') || name.includes('\\') || name.includes('..') || !name.endsWith(extension)) {
    return json({ error: 'invalid asset name' }, 400);
  }
  const file = path.join(__dirname, 'assets', folder, name);
  return fs.existsSync(file) ? { status: 200, contentType, file } : json({ error: 'asset not found' }, 404);
}
async function handle(method, rawUrl) {
  const url = new URL(rawUrl, 'https://txagent.local');
  const route = decodeURIComponent(url.pathname.replace(/^\/dsh-pet-7340/, ''));
  if (method === 'GET' && route === '/config') return json({ main: config });
  if (method === 'GET' && route === '/work-status') {
    const { state, task, ts } = readSnapshot();
    return json({ state, task, ts });
  }
  if (method === 'GET' && route === '/broadcast') return json({ ts: 0 });
  if (method === 'GET' && route.startsWith('/thumb/main/')) return asset('webm', route.slice(12), '.webm', 'video/webm');
  if (method === 'GET' && route.startsWith('/pic/')) return asset('pic', route.slice(5), '.png', 'image/png');
  if (method === 'POST' && route === '/close') {
    // This is a user hide action, distinct from host shutdown or a crash.
    process.stdout.write('txagent-pet:' + JSON.stringify({ kind: 'hidden' }) + '\n');
    setTimeout(() => require('electron').app.quit(), 100);
    return json({ ok: true });
  }
  return json({ error: 'unsupported pet route' }, 404);
}
module.exports = { handle, readSnapshot };
