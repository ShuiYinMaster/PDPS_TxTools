'use strict';
const {ipcMain,BrowserWindow,screen}=require('electron');
function emit(event,anchor,kind){
  const win=BrowserWindow.fromWebContents(event.sender);
  if(!win||win.isDestroyed())return;
  const b=win.getBounds();
  const x=Number(anchor?.x),y=Number(anchor?.y),w=Number(anchor?.width),h=Number(anchor?.height);
  const box=[x,y,w,h].every(Number.isFinite)&&w>0&&h>0?{x,y,width:w,height:h}:b;
  const display=screen.getDisplayNearestPoint({x:Math.round(box.x+box.width/2),y:Math.round(box.y+box.height/2)});
  const area=display.workArea;
  const ordered=screen.getAllDisplays().sort((a,b)=>a.bounds.x-b.bounds.x||a.bounds.y-b.bounds.y);
  process.stdout.write('txagent-pet:'+JSON.stringify({kind,screenIndex:ordered.findIndex(d=>d.id===display.id),
    rx:(box.x-area.x)/area.width,ry:(box.y-area.y)/area.height,rw:box.width/area.width,rh:box.height/area.height})+'\n');
}
ipcMain.on('pet:open-recipes',(event,anchor)=>emit(event,anchor,'open-recipes'));
ipcMain.on('pet:manage-recipes',(event,anchor)=>emit(event,anchor,'manage-recipes'));
