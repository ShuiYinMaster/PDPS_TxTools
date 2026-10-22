// Vector originals are kept in Image/Svg; PNGs are generated with sharp/librsvg.
// Usage: node tools/generate-plugin-icons.cjs (requires sharp).
const fs = require('fs');
const path = require('path');
const sharp = require('sharp');
const root = path.resolve(__dirname, '..');
const imageDir = path.join(root, 'Image');
const rect = (x,y,w,h,fill='#c3cbd0') => `<rect x="${x}" y="${y}" width="${w}" height="${h}" rx="1.5" fill="${fill}" stroke="#596b76" stroke-width="1.2"/>`;
const line = (d, color='#596b76', width=1.6) => `<path d="${d}" fill="none" stroke="${color}" stroke-width="${width}" stroke-linecap="round" stroke-linejoin="round"/>`;
const dot = (x,y,r=2,fill='#7cbb43') => `<circle cx="${x}" cy="${y}" r="${r}" fill="${fill}" stroke="#536a60" stroke-width="1"/>`;
const cube = (x,y,s=9,fill='#bdc7cd') => `<path d="M${x} ${y+3}l${s/2} -3 ${s/2} 3v${s-3}l${-s/2} 3 ${-s/2} -3z" fill="${fill}" stroke="#596b76" stroke-width="1.1"/>${line(`M${x} ${y+3}l${s/2} 3 ${s/2} -3m${-s/2} 3v${s-3}`, '#82949c', .8)}`;
const folder = (x,y,w=15,h=12) => `<path d="M${x} ${y+2}v${h}h${w}V${y+4}h${-w/2}l-2 -2z" fill="#c9d1d5" stroke="#596b76" stroke-width="1.2"/>${line(`M${x+1} ${y+6}h${w-2}`, '#f8fbfc', 1)}`;
const arrow = (d, color='#20a7ba') => line(d,color,2.2);
const art = {
  AutoPath: `${rect(3,25,11,4)}${line('M8 25V19L17 13 11 7','#74858e',5)}${dot(8,20,2.4,'#d2d9dd')}${dot(17,13,2.4,'#d2d9dd')}${line('M11 7l5 -3 3 3','#596b76',3)}${line('M17 24c0 -11 9 1 11 -10','#79b93e',1.8)}${arrow('M25 15l3 -3 1 4')}`,
  CrossEnvIO: `${folder(2,3,13,10)}${folder(17,17,13,10)}${arrow('M17 8h11m-4 -3 4 3 -4 3')}${arrow('M15 24H4m4 -3 -4 3 4 3','#79b93e')}`,
  ExportByColor: `${cube(2,4,11,'#8dbbd1')}${cube(13,2,11,'#8fc66a')}${cube(5,15,11,'#d8a366')}${arrow('M19 23h10m-4 -4 4 4 -4 4')}`,
  CatiaPartTree: `${line('M7 7v18h9M7 16h9')}${cube(2,2,10)}${cube(17,10,10,'#8fc66a')}${cube(17,21,10,'#8dbbd1')}`,
  ThemeTuner: `${rect(3,4,26,24,'#edf2f3')}${line('M8 9v14M16 9v14M24 9v14','#7f919b',2)}${rect(5,12,6,4,'#75b63b')}${rect(13,18,6,4,'#24abc0')}${rect(21,9,6,4,'#dfa76a')}`,
  MechArena: `<path d="M16 2l12 5v10c0 6 -7 11 -12 13C11 28 4 23 4 17V7z" fill="#bfcad0" stroke="#596b76" stroke-width="1.3"/>${rect(9,9,14,11,'#edf3f4')}${line('M12 14h2m4 0h2','#1caabd',2)}${line('M12 23h8M16 5v4','#596b76',1.5)}`,
  SnakeGame: `${rect(3,3,26,26,'#edf2f3')}${line('M9 22h6v-7h7V9h-7','#79b93e',5)}${dot(15,9,3.2,'#8dc74f')}${dot(15,8,0.6,'#273b42')}${dot(8,9,2,'#d79a62')}`,
  WeldPointMatrix: `${rect(3,3,26,26,'#edf2f3')}${line('M12 4v24M21 4v24M4 12h24M4 21h24','#9eacb4',1)}${dot(8,8)}${dot(17,8,2,'#20a7ba')}${dot(25,17)}${dot(8,25,2,'#20a7ba')}${dot(17,17)}`,
  SyncLibPath: `${folder(2,5,20,17)}${arrow('M13 7c8 -5 16 0 15 8m-3 -4 3 4 2 -4')}${arrow('M27 24c-5 5 -13 3 -14 -3m-2 4 2 -4 4 1','#79b93e')}`,
  SyncToSelectedComponent: `${folder(2,2,15,12)}${cube(16,18,13,'#9dc97d')}${arrow('M10 19v6h5m-3 -3 3 3 -3 3')}`,
  DeviceZAligner: `${line('M3 28h26','#596b76',2)}${cube(5,6,10)}${cube(19,16,10,'#9dc97d')}${arrow('M14 2v14m-3 -3 3 3 3 -3')}`,
  WeldPoint: `${line('M3 22l12 -13 14 13','#8b9aa3',4)}${dot(16,15,5,'#8cc849')}${line('M16 6v3M7 15h3M22 15h3M16 21v4','#e1b14c',1.5)}`,
  ViaPoint: `${line('M3 26L13 13 28 5','#85969f',3)}${dot(13,13,4,'#24b0c3')}${line('M12 8l2 10M8 12l10 2','#edf9fc',1.2)}`,
  SeamPoint: `${line('M3 25L27 5','#8999a2',5)}${line('M6 23L25 7','#66b342',2)}${dot(9,20,2,'#95ce65')}${dot(17,13,2,'#95ce65')}${dot(25,6,2,'#95ce65')}`
};
const existing = {
  AutoRecorder:'Image/AutoRecorder.png', AutoFance:'Image/AutoFance.png',
  ExportGun:'Image/ExportGun.png', RobotReachabilityChecker:'Image/RobotReachabilityChecker.png',
  WeldSpotGrouper:'Image/WeldSpotGrouper.png', LineToSolid:'Image/linetosoild.png',
  RobotBaseChecker:'Image/base0.png', WeldSpotAllocator:'WeldSpotAllocator.png',
  WeldGunDefiner:'Image/Xgun.png', WeldAnnotator:'Image/WeldAnnotator.png',
  Agent:'Image/ai.png', OpenResourceFolder:'Image/file.png'
};
async function writeSmall(input, name) {
  const pixels = await sharp(input).resize(16,16,{kernel:'lanczos3'})
    .flatten({background:'#c0c0c0'}).removeAlpha().raw().toBuffer();
  const bmp = Buffer.alloc(54 + 16 * 16 * 3);
  bmp.write('BM'); bmp.writeUInt32LE(bmp.length,2); bmp.writeUInt32LE(54,10);
  bmp.writeUInt32LE(40,14); bmp.writeInt32LE(16,18); bmp.writeInt32LE(16,22);
  bmp.writeUInt16LE(1,26); bmp.writeUInt16LE(24,28); bmp.writeUInt32LE(16*16*3,34);
  for (let y=0;y<16;y++) for (let x=0;x<16;x++) {
    const from=(y*16+x)*3,to=54+((15-y)*16+x)*3;
    bmp[to]=pixels[from+2];bmp[to+1]=pixels[from+1];bmp[to+2]=pixels[from];
  }
  fs.writeFileSync(path.join(imageDir,'Small',name+'.bmp'),bmp);
}
async function main() {
  fs.mkdirSync(path.join(imageDir,'Small'),{recursive:true});
  fs.mkdirSync(path.join(imageDir,'Svg'),{recursive:true});
  for (const [name,body] of Object.entries(art)) {
    const svg=`<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32" viewBox="0 0 32 32">${body}</svg>`;
    fs.writeFileSync(path.join(imageDir,'Svg',name+'.svg'),svg+'\n');
    for (const size of [16,32]) {
      const output=path.join(imageDir,size===16?'Small':'',name+'.png');
      await sharp(Buffer.from(svg),{density:384}).resize(size,size).png().toFile(output);
    }
    await writeSmall(Buffer.from(svg), name);
  }
  for (const [name,file] of Object.entries(existing)) {
    await sharp(path.join(root,file)).resize(16,16,{kernel:'lanczos3'}).png().toFile(path.join(imageDir,'Small',name+'.png'));
    await writeSmall(path.join(root,file), name);
  }
  const names=Object.keys(art);
  const cells=[];
  for (let i=0;i<names.length;i++) {
    const x=(i%4)*180,y=Math.floor(i/4)*110;
    cells.push({input:await sharp(path.join(imageDir,names[i]+'.png')).resize(64,64,{kernel:'nearest'}).toBuffer(),left:x+12,top:y+8});
    cells.push({input:await sharp(path.join(imageDir,'Small',names[i]+'.png')).resize(32,32,{kernel:'nearest'}).toBuffer(),left:x+93,top:y+28});
    cells.push({input:Buffer.from(`<svg width="178" height="22"><text x="8" y="16" font-family="Arial" font-size="11" fill="#263b48">${names[i]}</text></svg>`),left:x,top:y+80});
  }
  await sharp({create:{width:720,height:Math.ceil(names.length/4)*110,channels:4,background:'#f6f8f9'}}).composite(cells).png().toFile(path.join(imageDir,'plugin-icons-preview.png'));
  console.log(`Generated ${names.length} vector icons (32/16 px), ${Object.keys(existing).length} small companions.`);
}
main().catch(e=>{console.error(e.message);process.exitCode=1;});
