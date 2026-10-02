const {app,BrowserWindow,ipcMain}=require('electron');
const path=require('node:path');
const fs=require('node:fs');
const assert=require('node:assert/strict');
const messages=[];
ipcMain.on('pet-gui-test',(_,m)=>messages.push(typeof m==='string' ? JSON.parse(m) : m));
app.disableHardwareAcceleration();
setTimeout(()=>app.exit(1),20000).unref();
app.whenReady().then(async()=> {
  try {
    const win=new BrowserWindow({width:560,height:720,show:false,webPreferences:{
      preload:path.join(__dirname,'gui-preload.js'),contextIsolation:false,nodeIntegration:false,sandbox:false
    }});
    await win.loadFile(path.resolve(__dirname,'../../chat.html'));
    await new Promise(r=>setTimeout(r,150));
    assert.ok(messages.some(m=>m.type==='jsReady'),'Complete Agent bootstrap did not finish');
    await win.webContents.executeJavaScript(`dispatchMessage({type:'openRecipes'});`);
    assert.ok(await win.webContents.executeJavaScript(`!!window.txRecipes && !document.querySelector('.rcp-root').classList.contains('rcp-collapsed')`),'Manager entry did not expand the recipe sidebar');
    await win.webContents.executeJavaScript(`document.querySelector('.rcp-toggle').click();`);
    for(const width of [560,420]) {
      win.setContentSize(width,720);
      await win.webContents.executeJavaScript(`dispatchMessage({type:'petVisibility',enabled:true});`);
      const b=await win.webContents.executeJavaScript(`(()=>{const b=document.getElementById('btn-pet'),r=b.getBoundingClientRect();return {on:b.getAttribute('aria-checked'),disabled:b.disabled,x:r.x,right:r.right,w:innerWidth}})()`);
      assert.equal(b.on,'true'); assert.equal(b.disabled,false);
      assert.ok(b.x>=0 && b.right<=b.w,'Pet switch clipped at width '+width);
      const newButton=await win.webContents.executeJavaScript(`(()=>{const b=document.getElementById('btn-new');return {height:b.getBoundingClientRect().height,right:b.getBoundingClientRect().right,w:innerWidth}})()`);
      assert.ok(newButton.height<40 && newButton.right<=newButton.w,'New-conversation button wraps');
    }
    await win.webContents.executeJavaScript(`document.getElementById('btn-pet').click();`);
    await new Promise(r=>setTimeout(r,100));
    assert.ok(messages.some(m=>m.type==='setPetVisibility' && m.enabled===false));
    await win.webContents.executeJavaScript(`dispatchMessage({type:'petVisibility',enabled:false});document.getElementById('btn-pet').click();`);
    await new Promise(r=>setTimeout(r,100));
    assert.ok(messages.some(m=>m.type==='setPetVisibility' && m.enabled===true));
    await win.webContents.executeJavaScript(`dispatchMessage({type:'petVisibility',enabled:true});`);
    fs.writeFileSync(path.resolve(__dirname,'../../../artifacts/dsh-pet-smoke/gui.png'),(await win.webContents.capturePage()).toPNG());
    console.log('PASS: complete Agent HTML, recipe manager entry, switch synchronization, show/hide messages, 420/560 px layout');
    app.exit(0);
  } catch(e) { console.error(e.stack); app.exit(1); }
});
