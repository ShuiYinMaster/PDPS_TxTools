// Build the offline logo data in chat.html from the pinned, public Lobe Icons package.
// Download: https://registry.npmjs.org/@lobehub/icons-static-svg/-/icons-static-svg-1.95.1.tgz
const fs = require('fs');
const path = require('path');
const zlib = require('zlib');
const crypto = require('crypto');
const base = path.resolve(__dirname, '..');
const archive = fs.readFileSync(path.join(base, 'artifacts/lobe-icons-static-svg-1.95.1.tgz'));
const integrity = 'Hw7EPPgVnC4NZLXBfTNJG6hyQgqECfUPC11VVXodPSr1aebKcFxDZlSpxhWwYNdCc6bhxps/x5TtXoPmfKH2ag==';
if (crypto.createHash('sha512').update(archive).digest('base64') !== integrity) throw new Error('Logo package integrity mismatch');
const wanted = {
    deepseek: 'deepseek-color', kimi: 'kimi', qwen: 'qwen-color', openai: 'openai',
    ollama: 'ollama', claude: 'claude-color', gemini: 'gemini-color', glm: 'zhipu-color',
    grok: 'grok', minimax: 'minimax-color', doubao: 'doubao-color', llama: 'meta-color'
};
const tar = zlib.gunzipSync(archive);
const entries = {};
for (let offset = 0; offset + 512 <= tar.length;) {
    const header = tar.subarray(offset, offset + 512);
    const name = header.toString('utf8', 0, 100).replace(/\0.*$/, '');
    if (!name) break;
    const size = parseInt(header.toString('ascii', 124, 136).replace(/\0.*$/, '').trim(), 8) || 0;
    if (header[156] === 48 || header[156] === 0) entries[name] = tar.subarray(offset + 512, offset + 512 + size);
    offset += 512 + Math.ceil(size / 512) * 512;
}
const logos = {};
const sourceDir = path.join(base, 'UI/model-logos');
fs.mkdirSync(sourceDir, { recursive: true });
for (const [brand, slug] of Object.entries(wanted)) {
    const svg = entries['package/icons/' + slug + '.svg'];
    if (!svg) throw new Error('Missing logo: ' + slug);
    const text = svg.toString('utf8');
    if (!/^<svg\b/.test(text) || /<(?:script|foreignObject|image)\b|\son\w+\s*=|(?:href|src)=["'](?:https?:|\/\/)/i.test(text))
        throw new Error('Unexpected active content in logo: ' + slug);
    fs.writeFileSync(path.join(sourceDir, brand + '.svg'), svg);
    logos[brand] = 'data:image/svg+xml;base64,' + svg.toString('base64');
}
const file = path.join(base, 'UI/chat.html');
const html = fs.readFileSync(file, 'utf8');
// Include the license in the HTML too, since the DLL can be distributed on its own.
const license = fs.readFileSync(path.join(sourceDir, 'LICENSE'), 'utf8').trim();
const block = '/* MODEL_LOGOS_START: Lobe Icons 1.95.1\n' + license + '\n*/\nconst MODEL_LOGOS = ' + JSON.stringify(logos) + ';\n/* MODEL_LOGOS_END */';
const updated = html.includes('/* MODEL_LOGOS_START:')
    ? html.replace(/\/\* MODEL_LOGOS_START:[\s\S]*?\/\* MODEL_LOGOS_END \*\//, block)
    : html.replace('function onModelList(', block + '\n\nfunction onModelList(');
fs.writeFileSync(file, updated);
console.log('Embedded ' + Object.keys(logos).length + ' offline model logos.');
