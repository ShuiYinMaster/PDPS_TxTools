import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { resolve } from 'node:path';
const here = fileURLToPath(new URL('.', import.meta.url));
const local = process.env.TXTOOLS_LOCAL_ROOT || resolve(here, '../../../../../TxTools.Local');
const require = createRequire(import.meta.url);
const { rolldown } = await import(pathToFileURL(require.resolve('rolldown', {
  paths: [resolve(local, 'dependencies/node/dsh-pet-tools')],
})).href);
const bundle = await rolldown({
  input: resolve(local, 'dependencies/upstream/dsh-pet-source/dsh-pet/src/shared/index.ts'),
  platform: 'browser',
});
await bundle.write({format:'iife', name:'PetShared', file:resolve(here,'../dsh-pet/upstream/shared-core.js')});
await bundle.close();
console.log('dsh-pet shared core built');
