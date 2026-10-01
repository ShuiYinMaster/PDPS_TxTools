import { rolldown } from 'rolldown';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
const here = fileURLToPath(new URL('.', import.meta.url));
const bundle = await rolldown({
  input: resolve(here, '../references/dsh-pet-source/dsh-pet/src/shared/index.ts'),
  platform: 'browser',
});
await bundle.write({format:'iife', name:'PetShared', file:resolve(here,'../dsh-pet/upstream/shared-core.js')});
await bundle.close();
console.log('dsh-pet shared core built');
