// Copies the site into web/ for the app, swapping the PeerJS CDN script for the bundled copy,
// so the app only ever runs code that ships inside it. The sound worklet goes alongside.
const fs = require('fs');
const path = require('path');

const out = path.join(__dirname, 'web');
const cdn = '<script src="https://cdn.jsdelivr.net/npm/peerjs@1.5.4/dist/peerjs.min.js"></script>';

const html = fs.readFileSync(path.join(__dirname, '..', 'index.html'), 'utf8');
if (!html.includes(cdn)) throw new Error('PeerJS script tag in index.html changed; update copy-web.js to match.');

fs.mkdirSync(out, { recursive: true });
fs.writeFileSync(path.join(out, 'index.html'), html.replace(cdn, '<script src="peerjs.min.js"></script>'));
fs.copyFileSync(require.resolve('peerjs/dist/peerjs.min.js'), path.join(out, 'peerjs.min.js'));
fs.copyFileSync(path.join(__dirname, 'sound-worklet.js'), path.join(out, 'sound-worklet.js'));
console.log('Copied the site into web/');
