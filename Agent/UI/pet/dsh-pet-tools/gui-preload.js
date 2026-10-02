const {ipcRenderer}=require('electron');
window.chrome=window.chrome || {};
window.chrome.webview={
  postMessage: message=>ipcRenderer.send('pet-gui-test',message),
  addEventListener: ()=>{}
};
