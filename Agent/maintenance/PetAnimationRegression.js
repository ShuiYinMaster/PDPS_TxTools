'use strict';
// Run with the bundled Electron. Exercises real transparent WebM decoding and production CSS.
const {app,BrowserWindow}=require('electron');
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const dir=fs.mkdtempSync(path.resolve(__dirname,'../artifacts/animation-check-'));
process.env.TXAGENT_PET_PROFILE=path.join(dir,'profile');
process.env.TXAGENT_PET_STATE=path.join(dir,'state.json');
process.env.DSH_PET_HOST_PID=String(process.ppid);
fs.writeFileSync(process.env.TXAGENT_PET_STATE,JSON.stringify({closed:false,state:null,ts:1}));
delete process.env.DSH_PET_DPI_PROBE;
require('../UI/pet/dsh-pet/entry.js');
const delay=ms=>new Promise(resolve=>setTimeout(resolve,ms));
setTimeout(()=>{console.error('Animation test timed out');app.exit(1);},55000).unref();
app.whenReady().then(async()=>{
 try {
  const win=BrowserWindow.getAllWindows()[0], js=source=>win.webContents.executeJavaScript(source);
  let deadline=Date.now()+20000;
  while(!await js(`typeof sprites!=='undefined' && sprites[0] && !sprites[0].pending && Array.from(document.querySelectorAll('video.is-front')).some(v=>v.readyState>=2)`)){
   if(Date.now()>deadline)throw Error('Initial animation not ready');await delay(50);
  }
  while(!await js(`sprites[0]._warmVideos.size===4 && Array.from(sprites[0]._warmVideos.values()).every(e=>e.ready&&e.video.paused)`)){
   if(Date.now()>deadline)throw Error('Common actions failed to predecode');await delay(50);
  }
  console.log('PASS: four common clips are predecoded and paused, within a six-video pool');
  const samples=[];let monitor=true;
  const sampling=(async()=>{
   while(monitor){
    const image=await win.webContents.capturePage();
    const pixels=image.toBitmap();let max=0;
    for(let i=3;i<pixels.length;i+=4)max=Math.max(max,pixels[i]);
    const status=await js(`({front:sprites[0].front,pending:sprites[0].pending?.anim,pool:sprites[0]._videoPool.size,warm:sprites[0]._warmVideos.size,draws:sprites[0]._drawCount,canvasCount:document.querySelectorAll('canvas.pet-frame').length,canvasOpacity:getComputedStyle(sprites[0].frameCanvas).opacity,copy:sprites[0].frameContext.globalCompositeOperation,videos:Array.from(document.querySelectorAll('video.pet-video')).map(v=>({front:v.classList.contains('is-front'),opacity:getComputedStyle(v).opacity,animations:v.getAnimations().length,z:getComputedStyle(v).zIndex,time:v.currentTime,ready:v.readyState,paused:v.paused}))})`);
    samples.push({maxAlpha:max,...status});
    await delay(20);
   }
  })();
  const names=['点击回应-元气挥手','点击回应-害羞惊讶','待机呼吸休闲','原地敲击桌面互动'];
  for(const name of names){
   const immediate=await js(`(()=>{const s=sprites[0],old=s.front===0?s.videoA:s.videoB,cached=s._warmVideos.get(${JSON.stringify(name)});s.playOnce(${JSON.stringify(name)});return {waiting:!!s.pending,oldVisible:old.classList.contains('is-front'),visible:document.querySelectorAll('video.is-front').length,cacheHit:!!cached?.ready,cacheUsed:cached?.video===(s.front===0?s.videoB:s.videoA)};})()`);
   assert.equal(immediate.visible,1,'Exactly one action remains visible while preparing');
   if(immediate.waiting)assert(immediate.oldVisible);
   if(immediate.cacheHit)assert(immediate.cacheUsed,'Predecoded video must be reused');
   deadline=Date.now()+12000;
   while(await js(`!!sprites[0].pending`)){
    if(Date.now()>deadline)throw Error('Switch timed out: '+name);await delay(25);
   }
   await delay(260);
  }
  // Interrupt preparing repeatedly: one complete action must remain visible throughout.
  await js(`window.__animationRaceDone=false;(async()=>{for(let i=0;i<24;i++){sprites[0].playOnce(${JSON.stringify(names)}[i%4]);await new Promise(r=>setTimeout(r,[16,35,90,220][i%4]));}window.__animationRaceDone=true;})();true;`);
  while(!await js(`window.__animationRaceDone && !sprites[0].pending`))await delay(30);
  await delay(300);
  const before=await js(`sprites[0]._drawCount`);await delay(150);
  assert(await js(`sprites[0]._drawCount`)>before,'Displayed canvas continues animating');
  await js(`(()=>{const s=sprites[0];s.facing='right';s.playOnce('待机呼吸休闲');return true;})()`);
  while(await js(`!!sprites[0].pending`))await delay(30);
  assert(await js(`sprites[0].frameCanvas.style.transform.includes('scaleX(-1)')`),'Canvas preserves facing');
  if(!await js(`matchMedia('(prefers-reduced-motion: reduce)').matches`)){
   await js(`sprites[0].startSquash(sprites[0].videoA);true;`);await delay(40);
   assert(await js(`sprites[0].frameCanvas.style.transform.includes('scaleY(')`),'Squash targets the visible canvas');
   await delay(600);
  }
  monitor=false;await sampling;
  const result=await js(`({frontCount:document.querySelectorAll('video.is-front').length,playing:Array.from(document.querySelectorAll('video.is-front')).some(v=>v.readyState>=2&&!v.paused)})`);
  const preview=await win.webContents.capturePage();
  fs.writeFileSync(path.join(dir,'preview.png'),preview.toPNG());
  const pixels=preview.toBitmap();let transparent=0,opaque=0;
  for(let i=3;i<pixels.length;i+=4){if(pixels[i]===0)transparent++;if(pixels[i]===255)opaque++;}
  assert(transparent>opaque,'Persistent canvas must preserve the transparent desktop background');
  result.samples=samples;
  fs.writeFileSync(path.join(dir,'result.json'),JSON.stringify(result,null,2));
  const minAlpha=Math.min(...result.samples.map(s=>s.maxAlpha));
  console.log('Animation frames: '+result.samples.length+', minimum opaque-body alpha: '+minAlpha);
  assert(result.samples.length>=20);
  assert(minAlpha>=245,'Character dims or disappears while switching: alpha='+minAlpha);
  for(const sample of samples){
   assert.equal(sample.videos.filter(v=>v.front).length,1,'Exactly one complete action must remain visible');
   assert(sample.pool<=6&&sample.warm<=4&&sample.videos.length<=6,'Preloading must remain bounded');
   assert.equal(sample.canvasCount,1);assert.equal(Number(sample.canvasOpacity),1);assert.equal(sample.copy,'copy');
   assert(sample.videos.every(v=>Number(v.opacity)===0),'Decoder videos must never become visible layers');
   for(const video of sample.videos){assert([0,1].includes(Number(video.opacity)),'No fractional opacity');assert.equal(video.animations,0,'No fade animation');}
  }
  assert.equal(result.frontCount,1);assert(result.playing);
  console.log('PASS: real WebM preloading and atomic hard cuts, no blank frames, fades or overlap, including rapid interruption');
  console.log('Preview: '+path.join(dir,'preview.png'));
  app.exit(0);
 }catch(error){console.error(error.stack);app.exit(1);}
});
