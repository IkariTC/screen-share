# Screen Room for Windows

The same room as the website, as a single `.exe`. Sharing always includes sound, so friends on Firefox can share with sound too. People in the app and people on the website join the same rooms.

- **Share my screen** opens a picker with every screen and window (browser, game, Discord…), minimised windows included. A minimised window opens back up when you share it.
- **A window shares only its own sound.** Share a game and your friends hear the game, not your Discord call or notifications.
- **A screen shares all your computer's sound except this room's**, so friends never hear themselves back.
- On Windows 10 before build 20348 (late 2021), Windows can't capture one app's sound. There, everything your computer plays goes out, and friends' streams are muted on your end while you share.
- Invite links point at the website; friends can open them in any browser, or type the code into the app.

## Build

Needs Node.js 22.12 or newer. Nothing else: the sound helper is compiled with the C# compiler that comes with Windows.

```bash
npm install
npm start          # run it
npm run build      # makes dist/ScreenRoom-<version>.exe
```

Both `start` and `build` first run:

- `npm run web`, which copies `../index.html` into `web/` with PeerJS bundled locally, so the app never loads code from the internet;
- `npm run native`, which builds `native/sound.cs` into `screen-room-sound.exe`. That helper captures a single app's sound (or everything but Screen Room) and finds minimised windows; the app runs it in the background while you share.

The exe isn't code-signed, so Windows shows "Windows protected your PC" the first time. Choose **More info → Run anyway**.
