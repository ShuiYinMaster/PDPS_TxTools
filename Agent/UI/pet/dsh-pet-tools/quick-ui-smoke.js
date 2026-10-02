const {app,BrowserWindow,ipcMain}=require('electron');
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const model={type:'recipe.list.result',ok:true,study:'演示研究',favorites:['weld'],recent:['plain'],busy:false,savedArgs:{weld:{gap:'2.5'}},recipes:[
 {id:'weld',name:'焊点分组',description:'## 用途\r\n\r\n**按焊枪**整理选中的 `焊点`\r\n\r\n- 选择焊点\r\n- 运行并检查结果\r\n\r\n| 参数 | 单位 |\r\n| --- | --- |\r\n| 间距 | mm |\r\n\r\n[说明](https://example.com/help)\r\n\r\n```csharp\r\nvar text = "<img src=x>";\r\n```\r\n\r\n<img src=x onerror=alert(1)>',params:[{name:'targets',label:'焊点',kind:'objects',required:true},{name:'gap',label:'间距',kind:'number',required:true,def:'1',help:'建议使用 **毫米**，例如 `2.5`。'}]},
 {id:'plain',name:'导出当前统计',description:'输出研究统计结果',params:[]},
 {id:'text',name:'<img src=x onerror=alert(1)>',description:'特殊字符只作为文本显示',params:[{name:'name',label:'名称',kind:'text',required:true}]}
]};
const messages=[];let pickingError=false;let runReply;
ipcMain.on('quick-test-message',(event,m)=>{
 messages.push(m);const reply=body=>event.sender.send('quick-test-response',{...body,seq:m.seq});
 if(['quick.ready','recipe.list'].includes(m.type))reply(model);
 else if(m.type==='recipe.favorite'){model.favorites=m.favorite?[...model.favorites.filter(id=>id!==m.recipeId),m.recipeId]:model.favorites.filter(id=>id!==m.recipeId);reply(model);}
 else if(m.type==='recipe.pickSelection')reply(pickingError?{type:'recipe.pick.result',ok:false,error:'请先选对象'}:{type:'recipe.pick.result',ok:true,id:'3,57,2,1|3,57,2,2',name:'焊点 01',count:2,study:model.study});
 else if(m.type==='recipe.run')runReply=()=>reply({type:'recipe.run.result',ok:true,recipeId:m.recipeId,text:'## 结果\n\n已整理 **2 个焊点**\n\n| 状态 | 数量 |\n| --- | --- |\n| 完成 | 2 |\n\n```text\n原始日志 <script> 按文本显示\n```\n\n可在软件中撤销。'});
 else if(m.type==='recipe.detail')reply({type:'recipe.detail.result',ok:true,code:'// 已固化代码 <script> 按文本显示'});
});
const delay=ms=>new Promise(r=>setTimeout(r,ms));
app.disableHardwareAcceleration();setTimeout(()=>app.exit(1),30000).unref();
app.whenReady().then(async()=>{
 try{
  const win=new BrowserWindow({width:350,height:540,show:true,webPreferences:{preload:path.join(__dirname,'quick-test-preload.js'),contextIsolation:false,nodeIntegration:false,sandbox:false,backgroundThrottling:false}});
  const js=s=>win.webContents.executeJavaScript(s);
  await win.loadFile(path.resolve(__dirname,'../../recipe-quick.html'));await delay(100);
  assert.ok(await js(`document.body.innerText.includes('置顶配方') && document.body.innerText.includes('最近使用')`));
  assert.ok(await js(`document.querySelector('.card .desc strong').textContent==='按焊枪'`));
  assert.equal(await js(`document.querySelectorAll('.card-main a,.card-main button').length`),0);
  await js(`document.querySelector('.card-main').click()`);await delay(30);
  assert.ok(await js(`document.querySelector('.purpose h2').textContent==='用途' && document.querySelector('.purpose table') && document.querySelector('.purpose ul') && document.querySelector('.purpose code')`));
  assert.equal(await js(`document.querySelectorAll('.purpose img,.purpose script').length`),0);
  assert.ok(await js(`document.querySelector('.field .help strong').textContent==='毫米'`));
  await js(`document.querySelector('.purpose a').click();true`);await delay(30);
  assert.ok(messages.some(m=>m.type==='quick.openLink'&&m.url==='https://example.com/help'));
  await js(`Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:async text=>window.testCopied=text}});document.querySelector('.purpose .code-copy-btn').click();true`);await delay(30);
  assert.equal(await js(`window.testCopied`),'var text = "<img src=x>";');
  assert.equal(await js(`document.getElementById('param-gap').value`),'2.5');
  assert.equal(await js(`document.getElementById('run').disabled`),true);
  pickingError=true;await js(`document.querySelector('.pick button').click()`);await delay(30);
  assert.ok(await js(`document.getElementById('notice').textContent.includes('请先选对象')`));
  pickingError=false;await js(`document.querySelector('.pick button').click()`);await delay(30);
  assert.equal(await js(`document.getElementById('run').disabled`),false);
  await js(`document.getElementById('run').click();document.getElementById('run').click()`);await delay(30);
  assert.equal(messages.filter(m=>m.type==='recipe.run').length,1);
  assert.equal(messages.find(m=>m.type==='recipe.run').args.targets,'3,57,2,1|3,57,2,2');
  assert.equal(await js(`document.getElementById('run').disabled`),true);
  runReply();await delay(40);
  assert.ok(await js(`document.body.innerText.includes('执行完成') && document.getElementById('run').textContent==='再次运行'`));
  await js(`document.querySelector('.result details').open=true;document.querySelector('.result').scrollIntoView({block:'start'});true`);
  assert.ok(await js(`document.querySelector('.result-detail h2') && document.querySelector('.result-detail table') && document.querySelector('.result-detail strong').textContent==='2 个焊点'`));
  assert.equal(await js(`document.querySelectorAll('.result-detail script').length`),0);
  await delay(250);
  fs.writeFileSync(path.resolve(__dirname,'../../../artifacts/dsh-pet-smoke/quick-result.png'),(await win.webContents.capturePage()).toPNG());
  model.study='另一个研究';win.webContents.send('quick-test-response',{type:'recipe.studyChanged',study:model.study});await delay(50);
  assert.equal(await js(`document.getElementById('run').disabled`),true);
  assert.ok(await js(`document.querySelector('.bound').textContent==='尚未选择对象'`));
  await js(`document.querySelector('.detail-title .star').click()`);await delay(30);
  assert.equal(model.favorites.includes('weld'),false);
  await js(`document.getElementById('search').value='统计';document.getElementById('search').dispatchEvent(new Event('input'));`);
  assert.equal(await js(`document.querySelectorAll('.card').length`),1);
  await js(`document.getElementById('search').value='';document.getElementById('search').dispatchEvent(new Event('input'));document.querySelector('[data-tab=all]').click();`);
  assert.equal(await js(`document.querySelectorAll('img').length`),0);
  await delay(250);
  fs.writeFileSync(path.resolve(__dirname,'../../../artifacts/dsh-pet-smoke/quick-list.png'),(await win.webContents.capturePage()).toPNG());
  await js(`document.getElementById('manage').click()`);await delay(20);assert.ok(messages.some(m=>m.type==='quick.manage'));
  console.log('PASS: Markdown preview/detail/help/results, tables/code/copy/link bridge, escaped HTML, favorites, search, selection, run guard, results, study invalidation, manager entry');app.exit(0);
 }catch(e){console.error(e.stack);app.exit(1);}
});
