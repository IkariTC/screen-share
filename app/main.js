// Screen Room as a Windows app. It runs the same page as the website, with two differences:
// you pick any screen or window from the app's own picker, and sharing always includes your
// computer's sound, which Firefox can't do.
const { app, BrowserWindow, Menu, desktopCapturer, ipcMain, net, protocol, screen, session, shell } = require('electron');
const path = require('path');
const { pathToFileURL } = require('url');

const WEB = path.join(__dirname, 'web');
const ORIGIN = 'app://screen-room';
const SOURCE_ID = /^(screen|window):\d+:\d+$/;

// The screen or window picked in the page's picker; the next capture uses it, once
let chosen = null;

// The page is served from app://screen-room instead of file://, so it gets a proper origin
// (localStorage, secure context) and a Content-Security-Policy that only runs the bundled scripts.
protocol.registerSchemesAsPrivileged([
  { scheme: 'app', privileges: { standard: true, secure: true, supportFetchAPI: true } },
]);

const CSP = [
  "default-src 'self'",
  "script-src 'self' 'unsafe-inline'",              // the page's own inline script; no remote scripts
  "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com",
  "font-src https://fonts.gstatic.com",
  "img-src 'self' data:",
  "connect-src 'self' https://0.peerjs.com wss://0.peerjs.com",
  "object-src 'none'",
  "base-uri 'none'",
  "form-action 'none'",
].join('; ');

// Only what the page actually uses: copying the invite link, fullscreen and screen capture
const ALLOWED = new Set(['clipboard-sanitized-write', 'fullscreen', 'display-capture']);

function serveWeb() {
  protocol.handle('app', async request => {
    const url = new URL(request.url);
    const file = path.normalize(path.join(WEB, decodeURIComponent(url.pathname)));
    if (url.host !== 'screen-room' || !file.startsWith(WEB + path.sep)) return new Response('Not found', { status: 404 });
    const res = await net.fetch(pathToFileURL(file).toString());
    const headers = new Headers(res.headers);
    headers.set('Content-Security-Policy', CSP);
    return new Response(res.body, { status: res.status, headers });
  });
}

function lockDown(ses) {
  // Screen capture arrives as a "media" request with no camera or microphone in it; those stay blocked
  const screenOnly = details => details && Array.isArray(details.mediaTypes) && details.mediaTypes.length === 0;
  ses.setPermissionRequestHandler((wc, permission, callback, details) =>
    callback(ALLOWED.has(permission) || (permission === 'media' && screenOnly(details))));
  ses.setPermissionCheckHandler((wc, permission) => ALLOWED.has(permission));

  // "Share my screen": whatever was picked in the page, plus everything the computer is playing.
  // Windows loopback captures all system sound, so the page mutes friends' streams while you share.
  ses.setDisplayMediaRequestHandler(async (request, callback) => {
    const id = chosen;
    chosen = null;
    let source = null;
    try {
      const sources = id ? await desktopCapturer.getSources({ types: ['screen', 'window'], thumbnailSize: { width: 0, height: 0 } }) : [];
      source = sources.find(s => s.id === id) || null;
    } catch (e) {
      console.error('Listing screens failed', e);
    }
    // null refuses: nothing was picked, or the window has closed since
    if (!source) return callback(null);
    callback(request.audioRequested ? { video: source, audio: 'loopback' } : { video: source });
  });
}

// Node reports the origin of custom-scheme URLs as "null", so compare scheme and host instead
const isPage = url => {
  try { const u = new URL(url); return u.protocol === 'app:' && u.host === 'screen-room'; } catch (err) { return false; }
};
const fromPage = e => !!e.senderFrame && isPage(e.senderFrame.url);
const jpeg = image => 'data:image/jpeg;base64,' + image.toJPEG(80).toString('base64');

// The picker in the page asks for screens and windows here (via preload.js), then says which one to share
function servePicker() {
  ipcMain.handle('sources', async e => {
    if (!fromPage(e)) return [];
    const own = BrowserWindow.fromWebContents(e.sender)?.getMediaSourceId();
    const displays = screen.getAllDisplays();
    const primary = screen.getPrimaryDisplay().id;
    const sources = await desktopCapturer.getSources({
      types: ['screen', 'window'], thumbnailSize: { width: 384, height: 216 }, fetchWindowIcons: true,
    });
    let n = 0;
    return sources
      .filter(s => s.id !== own && !s.thumbnail.isEmpty())   // not this app, nor minimised windows
      .map(s => {
        if (s.id.startsWith('window:')) {
          return { id: s.id, kind: 'window', name: s.name, thumbnail: jpeg(s.thumbnail), icon: s.appIcon && !s.appIcon.isEmpty() ? s.appIcon.toDataURL() : null };
        }
        const d = displays.find(x => String(x.id) === s.display_id);
        const size = d ? Math.round(d.size.width * d.scaleFactor) + '×' + Math.round(d.size.height * d.scaleFactor) : '';
        const main = !!d && d.id === primary;
        return { id: s.id, kind: 'screen', name: 'Screen ' + ++n, detail: [size, main && 'main'].filter(Boolean).join(' · '), primary: main, thumbnail: jpeg(s.thumbnail) };
      });
  });
  ipcMain.handle('choose', (e, id) => {
    if (!fromPage(e) || typeof id !== 'string' || !SOURCE_ID.test(id)) return false;
    chosen = id;
    return true;
  });
}

function createWindow() {
  const win = new BrowserWindow({
    width: 1280,
    height: 800,
    minWidth: 420,
    minHeight: 520,
    backgroundColor: '#171311',
    title: 'Screen Room',
    icon: path.join(__dirname, 'icon.png'),
    webPreferences: {
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      webSecurity: true,
      preload: path.join(__dirname, 'preload.js'),
    },
  });

  // Never leave the bundled page: links to the outside open in the normal browser
  const external = url => {
    if (/^https:\/\//.test(url)) shell.openExternal(url);
  };
  win.webContents.setWindowOpenHandler(({ url }) => { external(url); return { action: 'deny' }; });
  win.webContents.on('will-navigate', (e, url) => {
    if (isPage(url)) return;
    e.preventDefault();
    external(url);
  });

  // The page puts "(1)" in its title when someone is waiting to be let in; flash the taskbar
  win.on('page-title-updated', (e, title) => {
    if (/^\(\d+\)/.test(title) && !win.isFocused()) win.flashFrame(true);
  });
  win.on('focus', () => win.flashFrame(false));

  win.loadURL(ORIGIN + '/index.html');
}

app.on('web-contents-created', (e, contents) => {
  contents.on('will-attach-webview', ev => ev.preventDefault());
});

app.whenReady().then(() => {
  Menu.setApplicationMenu(null);
  serveWeb();
  servePicker();
  lockDown(session.defaultSession);
  createWindow();
});

app.on('window-all-closed', () => app.quit());
