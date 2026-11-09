// The only things the page can ask the app for: the screens and windows it could share, which one
// to use, that capture's sound, and Windows' language
const { contextBridge, ipcRenderer } = require('electron');

let onSound = null, onLeftOut = null;
ipcRenderer.on('sound', (e, chunk) => { if (onSound) onSound(chunk); });
ipcRenderer.on('sound-left-out', (e, who) => { if (onLeftOut) onLeftOut(who); });

const LOCALE = '--screen-room-locale=';
const locale = process.argv.find(a => a.startsWith(LOCALE));

contextBridge.exposeInMainWorld('screenRoomApp', {
  // Windows' language, for times; the app only ships Chromium's English locale
  locale: locale ? locale.slice(LOCALE.length) : undefined,
  sources: () => ipcRenderer.invoke('sources'),
  canSplitSound: () => ipcRenderer.invoke('can-split'),
  choose: (id, blockDiscord) => ipcRenderer.invoke('choose', id, blockDiscord),
  // Raw 48 kHz stereo float samples, as bytes, for the share that just started;
  // leftOut hears 'room' or 'discord' when a whole screen's sound leaves that out
  startSound: (listener, leftOut) => { onSound = listener; onLeftOut = leftOut; return ipcRenderer.invoke('sound-start'); },
  stopSound: () => { onSound = null; onLeftOut = null; return ipcRenderer.invoke('sound-stop'); },
  setDiscordBlocked: block => ipcRenderer.invoke('sound-discord', block),
});
