# Screen Room for Windows

The same room as the website, as a single `.exe`. Sharing always includes your computer's sound, so friends on Firefox can share with sound too. People in the app and people on the website join the same rooms.

- **Share my screen** opens a picker with every screen and open window (browser, game, Discord…). Whichever you pick, all sound from your computer goes out with it: games, videos, voice chat, notifications.
- While you share, friends' streams are muted on your end, so they don't hear themselves back.
- Invite links point at the website; friends can open them in any browser, or type the code into the app.

## Build

Needs Node.js 22.12 or newer.

```bash
npm install
npm start          # run it
npm run build      # makes dist/ScreenRoom-<version>.exe
```

`npm run web` copies `../index.html` into `web/` with PeerJS bundled locally, so the app never loads code from the internet. Both `start` and `build` run it first.

The exe isn't code-signed, so Windows shows "Windows protected your PC" the first time. Choose **More info → Run anyway**.
