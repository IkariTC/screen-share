// Builds the sound helper with the C# compiler that ships with Windows (.NET Framework 4.8),
// so nothing extra needs installing, and the helper runs on any Windows 10 or 11 PC.
const { execFileSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const csc = path.join(process.env.WINDIR || 'C:\\Windows', 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe');
const out = path.join(__dirname, 'bin', 'screen-room-sound.exe');

fs.mkdirSync(path.dirname(out), { recursive: true });
execFileSync(csc, ['/nologo', '/target:exe', '/platform:x64', '/optimize+', '/out:' + out, path.join(__dirname, 'sound.cs')], { stdio: 'inherit' });
console.log('Built native/bin/screen-room-sound.exe');
