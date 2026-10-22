These brand marks come from `@lobehub/icons-static-svg` **1.95.1** (MIT):
https://github.com/lobehub/lobe-icons

The original SVG files are preserved here. `maintenance/EmbedModelLogos.js`
verifies the pinned package integrity and embeds them as data URLs into
`UI/chat.html`, which is already packaged in the plugin DLL. No external image
requests or additional runtime dependencies are needed.

The marks identify model families (including models accessed through a proxy).
Unknown models use a neutral icon. Provider group headings retain the actual
API provider name, so the logo does not change the model's routing.
