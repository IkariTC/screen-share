// Screen Room as a Windows app. It runs the same page as the website, with one difference:
// sharing always includes your computer's sound, which Firefox can't do.
const { app, BrowserWindow, Menu, desktopCapturer, net, protocol, session, shell } = require('electron');
const path = require('path');
const { pathToFileURL } = require('url');

const WEB = path.join(__dirname, 'web');
const ORIGIN = 'app://screen-room';

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

  // "Share my screen": the main screen plus everything the computer is playing.
  // Windows loopback captures all system sound, so the page mutes friends' streams while you share.
  ses.setDisplayMediaRequestHandler(async (request, callback) => {
    try {
      const [screen] = await desktopCapturer.getSources({ types: ['screen'] });
      if (!screen) return callback({});
      callback(request.audioRequested ? { video: screen, audio: 'loopback' } : { video: screen });
    } catch (e) {
      console.error('Screen capture failed', e);
      callback({});
    }
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
    webPreferences: {
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      webSecurity: true,
    },
  });

  // Never leave the bundled page: links to the outside open in the normal browser
  const external = url => {
    if (/^https:\/\//.test(url)) shell.openExternal(url);
  };
  win.webContents.setWindowOpenHandler(({ url }) => { external(url); return { action: 'deny' }; });
  win.webContents.on('will-navigate', (e, url) => {
    if (new URL(url).origin === ORIGIN) return;
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
  lockDown(session.defaultSession);
  createWindow();
});

app.on('window-all-closed', () => app.quit());
