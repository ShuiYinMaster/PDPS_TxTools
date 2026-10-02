'use strict';
const {app,BrowserWindow,ipcMain}=require('electron');
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const dir=path.resolve(__dirname,'../../../artifacts/dsh-pet-smoke');
process.env.TXAGENT_PET_STATE=path.join(dir,'quick-pet-state.json');
process.env.TXAGENT_PET_PROFILE=path.join(dir,'quick-pet-profile-'+Date.now());
process.env.TXAGENT_PET_CLICK_DELAY='140';
process.env.DSH_PET_HOST_PID=String(process.ppid);
fs.writeFileSync(process.env.TXAGENT_PET_STATE,JSON.stringify({closed:false,state:null,ts:1}));
const events=[];
for(const name of ['pet:open-recipes','pet:manage-recipes','pet:open-site'])ipcMain.on(name,(e,a)=>events.push({name,anchor:a}));
require('../dsh-pet/entry.js');
const delay=ms=>new Promise(r=>setTimeout(r,ms));
setTimeout(()=>app.exit(1),30000).unref();
app.whenReady().then(async()=>{
 try{
  const win=BrowserWindow.getAllWindows()[0];const js=s=>win.webContents.executeJavaScript(s);
  const deadline=Date.now()+12000;
  while(!await js(`window.__dshPetDebug?.configOk && typeof sprites!=='undefined' && sprites[0]?.hit`) ){
   if(Date.now()>deadline)throw Error('Pet not ready');await delay(100);
  }
  const click=detail=>js(`sprites[0].hit.dispatchEvent(new MouseEvent('click',{detail:${detail},bubbles:true}));true;`);
  await click(1);await delay(250);
  assert.equal(events.filter(e=>e.name==='pet:open-recipes').length,1);
  const a=events[0].anchor;assert.ok(Number.isFinite(a.x)&&Number.isFinite(a.y)&&a.width>0&&a.height>0);
  events.length=0;
  await click(1);await delay(40);await click(2);
  await js(`sprites[0].hit.dispatchEvent(new MouseEvent('dblclick',{detail:2,bubbles:true}));true;`);
  await delay(250);
  assert.equal(events.filter(e=>e.name==='pet:open-recipes').length,0);
  assert.equal(events.filter(e=>e.name==='pet:open-site').length,1);
  events.length=0;
  await js(`sprites[0].justDragged=true;true;`);await click(1);await delay(250);
  assert.equal(events.length,0);
  await js(`sprites[0].justDragged=false; sprites[0].onMenuAction({action:'quick-recipes'});sprites[0].onMenuAction({action:'manage-recipes'});true;`);await delay(100);
  assert.deepEqual(events.map(e=>e.name),['pet:open-recipes','pet:manage-recipes']);
  console.log('PASS: actual pet single-click, double-click exclusion, drag exclusion, menu routes, valid anchor');
  app.exit(0);
 }catch(e){console.error(e.stack);app.exit(1);}
});
