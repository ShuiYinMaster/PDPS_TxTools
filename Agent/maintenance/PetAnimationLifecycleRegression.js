'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
let sequence=0;const paints=new Map(),timers=new Map();
const context={console:{warn(){}},S:{ANIMATION_EXT:'.webm'},
 clearTimeout:id=>timers.delete(id),
 requestAnimationFrame:fn=>{const id=++sequence;paints.set(id,fn);return id;},cancelAnimationFrame:id=>paints.delete(id),
 window:{setTimeout:fn=>{const id=++sequence;timers.set(id,fn);return id;},clearTimeout:id=>timers.delete(id),matchMedia:()=>({matches:false})}};
vm.createContext(context);
vm.runInContext(fs.readFileSync(path.resolve(__dirname,'../UI/pet/dsh-pet/upstream/sprite.js'),'utf8')+'\nwindow.Sprite=PetSprite;',context);
function paint(){const queue=[...paints.values()];paints.clear();for(const fn of queue)fn();}
class Video {
 constructor(front=false){this.classes=new Set(front?['is-front']:[]);this.classList={add:c=>this.classes.add(c),remove:c=>this.classes.delete(c)};this.style={};this.readyState=front?4:0;this.paused=!front;this.listeners=new Set();this.frames=new Map();this.loads=0;this.onended=null;}
 addEventListener(type,fn){assert.equal(type,'loadeddata');this.listeners.add(fn);}
 removeEventListener(type,fn){this.listeners.delete(fn);}
 set src(value){this.url=value;this.readyState=0;}
 load(){this.loads++;}
 pause(){this.paused=true;}
 play(){this.paused=false;return Promise.resolve();}
 requestVideoFrameCallback(fn){const id=++sequence;this.frames.set(id,fn);return id;}
 cancelVideoFrameCallback(id){this.frames.delete(id);}
 decode(){this.readyState=4;for(const fn of [...this.listeners])fn();}
 present(){const callbacks=[...this.frames.values()];this.frames.clear();for(const fn of callbacks)fn();}
 animate(){throw Error('Hard cuts must not animate opacity');}
}
context.document={createElement:tag=>{assert.equal(tag,'video');return new Video();}};
function sprite(){
 const s=Object.create(context.window.Sprite.prototype);
 Object.assign(s,{videoA:new Video(true),videoB:new Video(),front:0,gen:0,pending:null,facing:'left',assetBase:'https://test/',pet:{id:'test'},pendingSquash:false,pendingMove:null,ended:0,handleEnded(){this.ended++;},stopSquash(){},
  animations:{idle:['idle'],clicks:['wave'],events:{workStatus:['think','work']}},
  _warmVideos:new Map(),_warmFailed:new Set(),_warmTimer:null,_warming:null,_disposed:false,
  _displayVideo:null,_displayFrame:null,_displayPaint:null,_drawCount:0,
  stage:{children:[],appendChild(v){this.children.push(v);}},
  frameCanvas:{width:640,height:360,style:{}},frameContext:{globalCompositeOperation:null,draws:[],drawImage(v){this.draws.push(v);}},
  ac:{abort(){}},el:{remove(){}},closeMenu(){},stopThrow(){},stopDragFollow(){},stopMove(){}});
 s._videoPool=new Set([s.videoA,s.videoB]);return s;
}
const s=sprite();
s.switchTo('wave',true);paint();paint();assert.equal(s.videoB.loads,1);
assert(s.videoA.classes.has('is-front')&&!s.videoA.paused,'old action keeps playing during preparation');
s.videoB.decode();assert.equal(s.front,0,'loadeddata alone cannot replace the displayed frame');
s.videoB.present();assert.equal(s.front,1);
assert(!s.videoA.classes.has('is-front')&&s.videoB.classes.has('is-front'),'exactly one action is visible');
assert(s.videoA.paused);assert.equal(s.videoA.onended,null);
assert.equal(s.frameContext.globalCompositeOperation,'copy','transparent pixels replace the previous pose');
assert.equal(s.frameContext.draws.at(-1),s.videoB);
const staleDisplay=[...s.videoB.frames.values()][0];
s.switchTo('idle',true);paint();paint();s.videoA.decode();
const obsolete=[...s.videoA.frames.values()][0];
s.switchTo('think',true);
assert(![...s.videoA.frames.values()].includes(obsolete),'superseded first-frame wait canceled');
obsolete();assert.equal(s.front,1,'stale frame cannot promote superseded action');
timers.get([...timers.keys()].at(-1))();
assert.equal(s.pending,null);assert(s.videoB.classes.has('is-front'),'failed load preserves the displayed action');
obsolete();assert.equal(s.front,1,'stale frame cannot commit after timeout');
s.switchTo('idle',true);paint();paint();s.videoA.decode();s.videoA.present();
assert.equal(s.front,0,'failed load remains retryable');
assert(!s.videoB.classes.has('is-front'));
const count=s._drawCount;staleDisplay();assert.equal(s._drawCount,count,'retired frame pump cannot draw an old pose');
const oldEnded=s.videoA.onended;s.switchTo('next',true);oldEnded();assert.equal(s.ended,0,'ended callback cannot replace a pending request');
const fresh=sprite();fresh.switchTo('queued',true);
fresh.dispose();paint();paint();assert.equal(fresh.videoB.loads,0,'disposed sprite cannot load deferred action');
assert(fresh.videoA.paused&&fresh.videoB.paused);
const warm=sprite();
for(let i=0;i<4;i++){
 warm.warmNextAnimation();const entry=warm._warming;assert(entry);entry.video.decode();entry.video.present();assert(entry.ready&&entry.video.paused);
}
assert.equal(warm._warmVideos.size,4);assert.equal(warm._videoPool.size,6);
warm.warmNextAnimation();assert.equal(warm._videoPool.size,6,'preloading is bounded');
const cached=warm._warmVideos.get('wave').video,loads=cached.loads;
warm.switchTo('wave',true);assert.equal(warm.videoB,cached);assert.equal(cached.loads,loads,'cache hit reuses the decoded clip without reloading');
cached.present();assert.equal(warm._displayVideo,cached);
warm.warmNextAnimation();assert.equal(warm._videoPool.size,6,'replenishing the cache reuses retired buffers');
const loading=warm._warming;warm.dispose();loading.video.decode();loading.video.present();
assert([...warm._videoPool].every(v=>v.paused));assert.equal(warm._warmVideos.size,0);
const failed=sprite();failed.warmNextAnimation();const failedEntry=failed._warming;failedEntry.video.onerror();
assert(failed._warmFailed.has('idle'));failed.warmNextAnimation();assert(failed._warmVideos.has('think'),'failed preloads do not prevent other clips from warming');
s.dispose();failed.dispose();
console.log('PASS: decoded-frame atomic cut, single-surface copy, canceled/stale callbacks, timeout, retry, predecoded cache reuse, six-buffer cap and disposal');
