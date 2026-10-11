// The only things the page can ask the app for: the screens and windows it could share, and which one to use
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('screenRoomApp', {
  sources: () => ipcRenderer.invoke('sources'),
  choose: id => ipcRenderer.invoke('choose', id),
});
