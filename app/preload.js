// The only things the page can ask the app for: the screens and windows it could share, which one
// to use, and that capture's sound
const { contextBridge, ipcRenderer } = require('electron');

let onSound = null;
ipcRenderer.on('sound', (e, chunk) => { if (onSound) onSound(chunk); });

contextBridge.exposeInMainWorld('screenRoomApp', {
  sources: () => ipcRenderer.invoke('sources'),
  choose: id => ipcRenderer.invoke('choose', id),
  // Raw 48 kHz stereo float samples, as bytes, for the share that just started
  startSound: listener => { onSound = listener; return ipcRenderer.invoke('sound-start'); },
  stopSound: () => { onSound = null; return ipcRenderer.invoke('sound-stop'); },
});
