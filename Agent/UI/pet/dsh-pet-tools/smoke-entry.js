'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { app, BrowserWindow, ipcMain } = require('electron');
const resultDir = path.resolve(__dirname, '../../../artifacts/dsh-pet-smoke');
fs.mkdirSync(resultDir, { recursive: true });
process.env.TXAGENT_PET_STATE = path.join(resultDir, 'state.json');
process.env.TXAGENT_PET_PROFILE = path.join(resultDir, 'fresh-profile-' + Date.now());
process.env.DSH_PET_HOST_PID = String(process.ppid);
delete process.env.DSH_PET_FORCE_DSF;
let revision = 0;
function state(value) {
  fs.writeFileSync(process.env.TXAGENT_PET_STATE, JSON.stringify({ state: value, task: null, ts: ++revision, closed: false }));
}
// Exercise the helper's initial-state path, not only updates after boot.
state('thinking');
require('../dsh-pet/entry.js');
const results = {};
const delay = (ms) => new Promise(resolve => setTimeout(resolve, ms));
async function waitFor(win, expression, timeout = 12000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    if (await win.webContents.executeJavaScript(expression)) return;
    await delay(100);
  }
  const debug = await win.webContents.executeJavaScript("({debug:window.__dshPetDebug,error:document.getElementById('pet-error')?.textContent,videos:Array.from(document.querySelectorAll('video')).map(v=>({src:v.src,ready:v.readyState,error:v.error?.code})),body:document.body.innerText})");
  throw new Error('Timed out: ' + expression + '\n' + JSON.stringify(debug));
}
setTimeout(() => { console.error('smoke test deadline'); app.exit(1); }, 45000).unref();
app.whenReady().then(async () => {
  try {
    const win = BrowserWindow.getAllWindows()[0];
    win.webContents.on('console-message', e => console.log('renderer: ' + e.message));
    await waitFor(win, "window.__dshPetDebug?.configOk === true && Array.from(document.querySelectorAll('video')).some(v => v.readyState >= 2)");
    results.transparent = win.isAlwaysOnTop() && win.isVisible();
    results.windowSize = win.getBounds();
    await waitFor(win, "sprites[0]?.workState === 'thinking'");
    results.initialThinking = true;
    state(null);
    await waitFor(win, "sprites[0]?.workState === null");
    // Explicitly play the idle clip for a stable transparency preview.
    await win.webContents.executeJavaScript("sprites[0].playIdle(); true;");
    await waitFor(win, "Array.from(document.querySelectorAll('video')).some(v => v.readyState >= 2 && !v.paused)");
    const before = await win.webContents.executeJavaScript("Array.from(document.querySelectorAll('video')).filter(v => !v.paused).map(v => v.currentTime)");
    await delay(700);
    const after = await win.webContents.executeJavaScript("Array.from(document.querySelectorAll('video')).filter(v => !v.paused).map(v => v.currentTime)");
    results.videoAdvances = after.some(t => before.some(old => t > old + 0.1));
    if (!results.videoAdvances) throw new Error('Video clock did not advance');
    fs.writeFileSync(path.join(resultDir, 'idle.png'), (await win.webContents.capturePage()).toPNG());
    results.states = [];
    for (const value of ['working', 'result', 'waiting', 'success', 'error']) {
      state(value);
      await waitFor(win, `sprites[0]?.workState === '${value}'`);
      await waitFor(win, "Array.from(document.querySelectorAll('video')).some(v => v.readyState >= 2 && !v.paused)");
      results.states.push(value);
      await delay(250);
    }
    const opened = new Promise(resolve => ipcMain.once('pet:open-site', resolve));
    await win.webContents.executeJavaScript("sprites[0].onMenuAction({action:'open-site'}); true;");
    await Promise.race([opened, delay(2000).then(() => { throw new Error('Assistant event missing'); })]);
    results.openAssistant = true;
    const debug = await win.webContents.executeJavaScript("({config:window.__dshPetDebug.configOk,error:document.getElementById('pet-error').textContent,anim:sprites[0].anim})");
    results.debug = debug;
    if (debug.error) throw new Error(debug.error);
    fs.writeFileSync(path.join(resultDir, 'results.json'), JSON.stringify(results, null, 2));
    console.log('TXAGENT_SMOKE_PASS ' + JSON.stringify(results));
    const routes = require('../dsh-pet/host-routes.js');
    await routes.handle('POST', '/dsh-pet-7340/close');
  } catch (error) {
    fs.writeFileSync(path.join(resultDir, 'failure.txt'), error.stack);
    console.error(error.stack);
    app.exit(1);
  }
});
