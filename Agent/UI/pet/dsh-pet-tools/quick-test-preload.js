const {ipcRenderer}=require('electron');
window.chrome=window.chrome||{};
window.chrome.webview={
 postMessage: message=>ipcRenderer.send('quick-test-message',message),
 addEventListener: (_,callback)=>ipcRenderer.on('quick-test-response',(_,data)=>callback({data}))
};
