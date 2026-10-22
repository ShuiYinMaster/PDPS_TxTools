const fs = require('fs');
const path = require('path');
const assert = require('assert');
const {chromium} = require('playwright');
const root = path.resolve(__dirname,'../..');
(async()=>{
  const browser = await chromium.launch({headless:true,channel:process.env.TX_UI_BROWSER_CHANNEL||'msedge'});
  let checks=0;
  try {
    for (const file of ['chat.html','recipe-quick.html']) for (const scale of [1,1.75,2]) {
      const page = await browser.newPage({viewport:{width:file==='chat.html'?560:340,height:720},deviceScaleFactor:scale});
      await page.route('**/*', route=>route.abort());
      let html=fs.readFileSync(path.join(root,'Agent/UI',file),'utf8').replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi,'');
      html=html.replace(/<link[^>]*recipe-sidebar.css[^>]*>/,`<style>${fs.readFileSync(path.join(root,'Agent/UI/recipe-sidebar.css'),'utf8')}</style>`);
      html=html.replace('</head>',`<style>${fs.readFileSync(path.join(root,'Agent/UI/plugin-ui.css'),'utf8')}</style></head>`);
      await page.setContent(html);
      if (process.argv.includes('--preview') && scale===1) {
        await page.evaluate(file=>{
          if(file==='chat.html') {
            const chat=document.getElementById('chat');
            if(chat) chat.innerHTML='<div class="msg user"><div class="msg-label">示例输入</div><div class="msg-content">读取当前选择的机器人操作。</div></div><div class="msg assistant"><div class="msg-label">示例回复</div><div class="msg-content"><p>已读取 3 个操作。可以继续检查点位或生成配方。</p></div></div>';
            const model=document.getElementById('model-trigger');if(model)model.textContent='模型选择（预览）';
            const status=document.getElementById('status-text');if(status)status.textContent='离线布局预览 · 示例消息';
          } else {
            document.getElementById('study').textContent='离线布局预览 · 示例配方';
            document.getElementById('content').innerHTML='<div class="section-title">示例配方</div>'+[
              ['检查机器人点位','检查当前操作的点位，显示检查结果。'],['读取选择内容','读取当前选中的对象信息。'],['导出资源','选择设备与输出目录后执行导出。']
            ].map(([title,description])=>'<div class="card"><button class="card-main"><span class="name">'+title+'</span><div class="desc">'+description+'</div><span class="badge">需要选择对象 · 1 个执行按钮</span></button><button class="star">☆</button></div>').join('');
            document.getElementById('count').textContent='3 个示例配方';
          }
        },file);
        const directory=path.join(root,'docs/ui-preview');
        fs.mkdirSync(directory,{recursive:true});
        await page.screenshot({path:path.join(directory,file==='chat.html'?'AgentChat-100.png':'RecipeQuick-100.png')});
      }
      await page.evaluate(()=>{
        const host=document.createElement('div');host.style.cssText='position:fixed;bottom:0;left:0;background:white;display:flex;flex-direction:column';
        host.innerHTML='<button class="primary" id="tx-short">开始检查</button><button class="primary" id="tx-long">复制至目标环境并重建全部结构</button>';
        document.body.append(host);
      });
      const size=await page.evaluate(()=>Object.fromEntries(['tx-short','tx-long'].map(id=>{
        const e=document.getElementById(id),b=e.getBoundingClientRect();return [id,{width:b.width,height:b.height,scroll:e.scrollWidth,client:e.clientWidth}];
      })));
      assert.strictEqual(size['tx-short'].height,26);checks++;
      assert.strictEqual(size['tx-long'].height,26);checks++;
      assert(size['tx-long'].width>size['tx-short'].width);checks++;
      assert(size['tx-long'].scroll<=size['tx-long'].client);checks++;
      await page.close();
    }
    console.log(`PASS: ${checks} WebView button layout assertions`);
  } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1});
