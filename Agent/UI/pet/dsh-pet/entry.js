'use strict';
const path = require('node:path');
const fs = require('node:fs');
const { app } = require('electron');
const profile = process.env.TXAGENT_PET_PROFILE || path.join(app.getPath('appData'), 'TxAgent-dsh-pet');
fs.mkdirSync(profile, { recursive: true });
app.setPath('userData', profile);
if (process.env.DSH_PET_DPI_PROBE !== '1') require('./startup-monitor.js');
process.env.DSH_PET_BRIDGE = '1';
process.env.DSH_PET_PETS = JSON.stringify([{ id: 'main', size: 420 }]);
process.env.DSH_PET_CONFIG_URL = 'https://txagent.local/dsh-pet-7340/config';
process.chdir(path.join(__dirname, 'upstream'));
require('./upstream/main.js');
if (process.env.DSH_PET_DPI_PROBE !== '1') {
  const routes = require('./host-routes.js');
  setInterval(() => {
    if (routes.readSnapshot().closed) {
      process.stdout.write('txagent-pet:' + JSON.stringify({ kind: 'closed' }) + '\n');
      app.quit();
    }
  }, 300).unref();
}
