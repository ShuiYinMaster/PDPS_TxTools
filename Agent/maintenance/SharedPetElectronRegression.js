'use strict';
// Starts only isolated helper processes, with no engineering host or scene access.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const {spawn} = require('node:child_process');
const runtime = path.resolve(__dirname, '../UI/pet/dsh-pet');
const testDir = fs.mkdtempSync(path.resolve(__dirname, '../artifacts/shared-electron-'));
const children = [];
function launch(index) {
  const state = path.join(testDir, `state-${index}.json`);
  fs.writeFileSync(state, JSON.stringify({closed:false, state:null, task:null, ts:1}));
  const env = {...process.env, TXAGENT_PET_PROFILE:path.join(testDir,'profile'),
    TXAGENT_PET_STATE:state, TXAGENT_PET_SINGLETON:'1', DSH_PET_HOST_PID:String(process.pid)};
  delete env.ELECTRON_RUN_AS_NODE; delete env.DSH_PET_DPI_PROBE; delete env.DSH_PET_SMOKE;
  const child = spawn(path.join(runtime,'electron/electron.exe'), [path.join(runtime,'entry.js')],
    {cwd:runtime, env, windowsHide:true, stdio:['ignore','pipe','pipe']});
  const record = {child, state, messages:[], output:'', exited:false, code:null};
  let pending = '';
  child.stdout.on('data', data => {
    record.output += data;
    pending += data;
    const lines = pending.split(/\r?\n/); pending = lines.pop();
    for (const line of lines) if(line.startsWith('txagent-pet:')) record.messages.push(JSON.parse(line.slice(12)));
  });
  child.stderr.on('data', data => record.output += data);
  child.on('error', error => {record.exited=true; record.output+=error.stack;});
  child.on('exit', code => {record.exited=true; record.code=code;});
  children.push(record); return record;
}
async function waitFor(record, predicate, label) {
  const deadline = Date.now()+55000;
  while (!predicate(record)) {
    if(record.exited || Date.now()>deadline) throw new Error(label+' failed: '+record.output.slice(-6000));
    await new Promise(resolve => setTimeout(resolve,100));
  }
}
(async () => {
  try {
    const first = launch(1);
    await waitFor(first, r=>r.messages.some(m=>m.kind==='ready'), 'visible animated first pet');
    assert(first.messages.find(m=>m.kind==='ready').alwaysOnTop);
    const second = launch(2);
    await waitFor(second, r=>r.exited, 'second helper exit');
    assert.equal(second.code,0);
    assert(second.messages.some(m=>m.kind==='singleton-busy'));
    assert(!second.messages.some(m=>m.kind==='ready'));
    console.log('PASS: native Electron rejects a duplicate helper while one visible pet remains');
    fs.writeFileSync(first.state, JSON.stringify({closed:true, state:null, task:null, ts:2}));
    await waitFor(first, r=>r.exited, 'last helper closes');
    assert.equal(first.code,0);
    console.log('PASS: shared pet remains on top and exits on host shutdown state');
  } finally {
    for(const r of children) if(!r.exited) r.child.kill();
  }
})().catch(error=>{console.error(error);process.exitCode=1;});
