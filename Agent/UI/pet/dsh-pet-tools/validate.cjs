'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../dsh-pet');
const config = require('../dsh-pet/config.json');
const routes = require('../dsh-pet/host-routes.js');
const animations = config.animations;
const names = new Set([
  ...animations.idle, ...animations.turn, ...animations.drag, ...animations.clicks,
  ...animations.moves.actions.map(v => v.name),
  ...animations.categories.flatMap(v => v.actions),
  ...Object.values(animations.events).flat(2),
]);
(async () => {
  for (const name of names) {
    const file = path.join(root, 'assets/webm', name + '.webm');
    assert.ok(fs.existsSync(file), name);
    const bytes = fs.readFileSync(file);
    assert.equal(bytes.subarray(0, 4).toString('hex'), '1a45dfa3', name + ' EBML');
    const response = await routes.handle('GET', '/dsh-pet-7340/thumb/main/' + encodeURIComponent(name) + '.webm');
    assert.equal(response.status, 200, name);
    assert.equal(response.file, file);
  }
  const merged = JSON.parse((await routes.handle('GET', '/dsh-pet-7340/config')).body);
  assert.equal(merged.main.pets[0].workStatusEnabled, true);
  assert.equal(merged.main.pets[0].whisperEnabled, false);
  assert.equal(merged.main.pets[0].balanceEnabled, false);
  assert.equal((await routes.handle('GET', '/dsh-pet-7340/thumb/main/%2e%2e%5csecret.webm')).status, 400);
  assert.equal((await routes.handle('GET', '/dsh-pet-7340/thumb/main/missing.webm')).status, 404);
  assert.equal((await routes.handle('POST', '/dsh-pet-7340/config')).status, 404);
  console.log('PASS: ' + names.size + ' configured clips, local asset routes, quiet configuration and path validation');
})().catch(error => { console.error(error); process.exitCode = 1; });
