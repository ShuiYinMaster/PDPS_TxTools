'use strict';
const {app, screen} = require('electron');
const send = value => process.stdout.write('txagent-pet:' + JSON.stringify(value) + '\n');
let failed = false;
function fail(message) {
  if (failed) return;
  failed = true;
  send({kind:'failed', message:String(message)});
  app.exit(1);
}
process.on('uncaughtException', error => fail(error.stack || error));
process.on('unhandledRejection', error => fail(error?.stack || error));
app.on('child-process-gone', (_, detail) => console.error('[pet child]', JSON.stringify(detail)));
app.on('browser-window-created', (_, win) => {
  const wc = win.webContents;
  wc.on('console-message', event => console.error('[pet renderer]', event.message));
  wc.on('did-fail-load', (_, code, description, url, isMainFrame) => {
    if (isMainFrame && code !== -3) fail(`页面加载失败 ${code}: ${description} (${url})`);
  });
  wc.on('render-process-gone', (_, detail) => fail('渲染进程退出: ' + JSON.stringify(detail)));
  wc.once('did-finish-load', () => {
    const deadline = Date.now() + 45000;
    const times = new Map();
    let last = null;
    let busy = false;
    const timer = setInterval(async () => {
      if (busy || failed || win.isDestroyed()) return;
      busy = true;
      try {
        last = await wc.executeJavaScript(`({
          configOk:window.__dshPetDebug?.configOk === true,
          error:document.getElementById('pet-error')?.textContent || '',
          videos:Array.from(document.querySelectorAll('video')).map(v=>({
            src:v.currentSrc, ready:v.readyState, time:v.currentTime,
            playing:!v.paused, width:v.videoWidth, height:v.videoHeight,
            error:v.error?.code
          }))
        })`);
        const advancing = last.videos.some(v => {
          const old = times.get(v.src);
          times.set(v.src, v.time);
          return v.ready >= 2 && v.playing && v.width > 0 && v.height > 0 &&
            old !== undefined && v.time > old + 0.03;
        });
        const b = win.getBounds();
        const onScreen = screen.getAllDisplays().some(({workArea:a}) =>
          b.x < a.x+a.width && b.y < a.y+a.height && b.x+b.width > a.x && b.y+b.height > a.y);
        if (last.configOk && advancing && win.isVisible() && onScreen) {
          clearInterval(timer);
          send({kind:'ready', bounds:b, alwaysOnTop:win.isAlwaysOnTop(), diagnostics:last});
        } else if (Date.now() > deadline) {
          clearInterval(timer);
          fail('等待可见动画超时: ' + JSON.stringify({visible:win.isVisible(), onScreen, bounds:b, diagnostics:last}));
        }
      } catch (error) {
        clearInterval(timer);
        fail(error.stack || error);
      } finally { busy = false; }
    }, 250);
    win.once('closed', () => clearInterval(timer));
  });
});
