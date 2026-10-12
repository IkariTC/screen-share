// Screen Room as a Windows app. It runs the same page as the website, with two differences:
// you pick any screen or window (minimised ones too) from the app's own picker, and sharing
// includes sound, which Firefox can't do. A window brings only its own sound; a screen brings
// everything except Screen Room itself, so friends never hear themselves back.
const { app, BrowserWindow, Menu, desktopCapturer, ipcMain, net, protocol, screen, session, shell } = require('electron');
const { execFile, spawn } = require('child_process');
const path = require('path');
const { pathToFileURL } = require('url');

const WEB = path.join(__dirname, 'web');
const ORIGIN = 'app://screen-room';
const SOURCE_ID = /^(screen|window):\d+:\d+$/;

// The sound helper (native/sound.cs): per-app sound capture, and the minimised windows the picker can't see
const HELPER = app.isPackaged
  ? path.join(process.resourcesPath, 'screen-room-sound.exe')
  : path.join(__dirname, 'native', 'bin', 'screen-room-sound.exe');
const helper = args => new Promise(resolve => {
  execFile(HELPER, args, { windowsHide: true, timeout: 5000 }, (err, out) => resolve(err ? null : out));
});

// Whether this PC can capture a single app's sound (Windows 10 build 20348+ or Windows 11). Asked once.
let splitSound = null;
const canSplitSound = () => splitSound || (splitSound = helper(['probe']).then(out => out === 'ok'));

let chosen = null;      // { id, sound, args } picked in the page's picker; the next capture uses it, once
let soundArgs = null;   // the helper command for the capture that just started; sound-start uses it, once
let capturing = null;   // the helper streaming sound right now

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

  // "Share my screen": whatever was picked in the page. The sound helper supplies the sound,
  // unless this PC can't capture single apps; then it's Windows' whole mix and the page mutes
  // friends' streams while you share.
  ses.setDisplayMediaRequestHandler(async (request, callback) => {
    const pick = chosen;
    chosen = null;
    let source = null;
    try {
      const sources = pick ? await desktopCapturer.getSources({ types: ['screen', 'window'], thumbnailSize: { width: 0, height: 0 } }) : [];
      source = sources.find(s => s.id === pick.id) || null;
    } catch (e) {
      console.error('Listing screens failed', e);
    }
    // null refuses: nothing was picked, or the window has closed since
    if (!source) return callback(null);
    soundArgs = pick.args;
    callback(request.audioRequested && pick.sound === 'system' ? { video: source, audio: 'loopback' } : { video: source });
  });
}

// Only Chromium's English locale ships (electronLanguages in package.json), which would put spellcheck
// and the page's clock in American English everywhere. Both follow Windows' language instead.
function systemLanguage() {
  const lang = app.getPreferredSystemLanguages()[0];
  try { return lang && Intl.DateTimeFormat.supportedLocalesOf(lang).length ? lang : null; } catch (err) { return null; }
}

function spellcheckIn(ses, lang) {
  const available = ses.availableSpellCheckerLanguages;
  const match = available.find(l => l.toLowerCase() === lang.toLowerCase()) || available.find(l => l === lang.split('-')[0]);
  if (match) ses.setSpellCheckerLanguages([match]);
}

// Node reports the origin of custom-scheme URLs as "null", so compare scheme and host instead
const isPage = url => {
  try { const u = new URL(url); return u.protocol === 'app:' && u.host === 'screen-room'; } catch (err) { return false; }
};
const fromPage = e => !!e.senderFrame && isPage(e.senderFrame.url);
const jpeg = image => 'data:image/jpeg;base64,' + image.toJPEG(80).toString('base64');
const hwndOf = win => win.getNativeWindowHandle().readBigUInt64LE(0).toString();

// A window's id, once it's listed (again); restored windows take a moment to come back
async function findWindow(hwnd) {
  for (let tries = 0; tries < 20; tries++) {
    const sources = await desktopCapturer.getSources({ types: ['window'], thumbnailSize: { width: 0, height: 0 } });
    const found = sources.find(s => s.id.startsWith('window:' + hwnd + ':'));
    if (found) return found.id;
    await new Promise(r => setTimeout(r, 100));
  }
  return null;
}

// The picker in the page asks for screens and windows here (via preload.js), then says which one to share
function servePicker() {
  ipcMain.handle('sources', async e => {
    if (!fromPage(e)) return { sources: [], split: false };
    const win = BrowserWindow.fromWebContents(e.sender);
    const own = win ? win.getMediaSourceId() : null;
    const ownHwnd = win ? hwndOf(win) : null;
    const displays = screen.getAllDisplays();
    const primary = screen.getPrimaryDisplay().id;
    const [sources, minimised, split] = await Promise.all([
      desktopCapturer.getSources({ types: ['screen', 'window'], thumbnailSize: { width: 384, height: 216 }, fetchWindowIcons: true }),
      helper(['windows']).then(out => { try { return JSON.parse(out); } catch (err) { return []; } }),
      canSplitSound(),
    ]);

    let n = 0;
    const listed = sources
      .filter(s => s.id !== own && !s.thumbnail.isEmpty())
      .map(s => {
        if (s.id.startsWith('window:')) {
          return { id: s.id, kind: 'window', name: s.name, thumbnail: jpeg(s.thumbnail), icon: s.appIcon && !s.appIcon.isEmpty() ? s.appIcon.toDataURL() : null };
        }
        const d = displays.find(x => String(x.id) === s.display_id);
        const size = d ? Math.round(d.size.width * d.scaleFactor) + '×' + Math.round(d.size.height * d.scaleFactor) : '';
        const main = !!d && d.id === primary;
        return { id: s.id, kind: 'screen', name: 'Screen ' + ++n, detail: [size, main && 'main'].filter(Boolean).join(' · '), primary: main, thumbnail: jpeg(s.thumbnail) };
      });

    // Minimised windows have no picture; show their app's icon instead
    const shown = new Set(listed.map(s => s.id.split(':')[1]));
    const hidden = await Promise.all(minimised
      .filter(w => /^\d+$/.test(w.hwnd) && w.hwnd !== ownHwnd && !shown.has(w.hwnd))
      .map(async w => {
        const icon = w.exe ? await app.getFileIcon(w.exe, { size: 'large' }).catch(() => null) : null;
        return { id: 'window:' + w.hwnd + ':0', kind: 'window', name: String(w.title), minimised: true, icon: icon && !icon.isEmpty() ? icon.toDataURL() : null };
      }));
    return { sources: listed.concat(hidden), split };
  });

  ipcMain.handle('choose', async (e, id) => {
    if (!fromPage(e) || typeof id !== 'string' || !SOURCE_ID.test(id)) return null;
    const [kind, num] = id.split(':');
    if (kind === 'window') {
      // A minimised window can't be captured, so bring it back up first (others are left alone)
      await helper(['restore', num]);
      id = await findWindow(num);
      if (!id) return null;
    }
    const split = await canSplitSound();
    const sound = !split ? 'system' : kind === 'window' ? 'window' : 'others';
    const args = sound === 'window' ? ['window', num] : sound === 'others' ? ['except', String(process.pid)] : null;
    chosen = { id, sound, args };
    return { sound };
  });

  // Sound for the capture that just started, streamed to the page as raw 48 kHz stereo float samples
  ipcMain.handle('sound-start', e => {
    if (!fromPage(e) || !soundArgs) return false;
    const args = soundArgs;
    soundArgs = null;
    stopSound();
    return new Promise(resolve => {
      const child = spawn(HELPER, args, { windowsHide: true });
      capturing = child;
      let settled = false, rest = Buffer.alloc(0);
      const settle = ok => { if (!settled) { settled = true; resolve(ok); } };
      child.stderr.on('data', d => {
        const text = String(d).trim();
        if (text === 'ready') settle(true);
        else console.error('Sound helper:', text);
      });
      child.stdout.on('data', d => {
        // Only whole frames (2 channels × 4 bytes) go to the page, each in its own exact-size copy
        const b = rest.length ? Buffer.concat([rest, d]) : d;
        const whole = b.length - (b.length % 8);
        rest = b.subarray(whole);
        if (whole && !e.sender.isDestroyed()) e.sender.send('sound', new Uint8Array(b.subarray(0, whole)));
      });
      child.on('error', () => settle(false));
      child.on('exit', () => { settle(false); if (capturing === child) capturing = null; });
      setTimeout(() => settle(false), 5000);
    });
  });
  ipcMain.handle('sound-stop', e => { if (fromPage(e)) stopSound(); });
}

function stopSound() {
  if (!capturing) return;
  const child = capturing;
  capturing = null;
  child.kill();
}

function createWindow(lang) {
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
      additionalArguments: lang ? ['--screen-room-locale=' + lang] : [],
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
  win.webContents.on('did-start-loading', stopSound);   // a reload ends any share

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
  const lang = systemLanguage();
  if (lang) spellcheckIn(session.defaultSession, lang);
  createWindow(lang);
  canSplitSound();                                       // ask early so the picker opens quickly
});

app.on('window-all-closed', () => app.quit());
app.on('will-quit', stopSound);
