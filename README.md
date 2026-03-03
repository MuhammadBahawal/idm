# MyDM

Modern Windows download manager with browser integration.

[![Latest Release](https://img.shields.io/github/v/release/MuhammadBahawal/idm?label=Latest%20Release)](https://github.com/MuhammadBahawal/idm/releases/latest)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-blue)](https://github.com/MuhammadBahawal/idm/releases/latest)
[![License](https://img.shields.io/badge/License-MIT-green)](https://opensource.org/license/mit)

## Quick Download

Download these files from the latest release:

| File | Direct Link | Use |
| --- | --- | --- |
| Latest release page | [Open Releases](https://github.com/MuhammadBahawal/idm/releases/latest) | Always works |
| `MyDMUserSetup-x64.exe` | [Download Setup EXE](https://github.com/MuhammadBahawal/idm/releases/latest/download/MyDMUserSetup-x64.exe) | Main installer |
| `mydm-extension-chromium.zip` | [Download Chromium Extension](https://github.com/MuhammadBahawal/idm/releases/latest/download/mydm-extension-chromium.zip) | Chrome, Edge, Brave, Opera, Vivaldi |
| `mydm-extension-firefox.zip` | [Download Firefox Extension](https://github.com/MuhammadBahawal/idm/releases/latest/download/mydm-extension-firefox.zip) | Firefox |

If a direct link shows `404 Not Found`, open the Releases page and upload/publish a release first.
As of **March 3, 2026**, this repo has no published GitHub releases yet, so direct `/latest/download/...` links return 404.

## Install (User Flow)

1. Download `MyDMUserSetup-x64.exe`.
2. Double-click setup file.
3. Click `Next -> Next -> Install -> Finish`.
4. Open `MyDM Download Manager` from Start Menu.

This is the same simple install flow as normal software setup.

## Browser Extension Setup

### Chromium Browsers
Works for Chrome, Edge, Brave, Opera, and Vivaldi.

1. Extract `mydm-extension-chromium.zip`.
2. Open `chrome://extensions` (or browser extensions page).
3. Enable `Developer Mode`.
4. Click `Load unpacked`.
5. Select extracted folder.
6. Open extension details and copy extension ID.

### Firefox

1. Extract `mydm-extension-firefox.zip`.
2. Open `about:debugging#/runtime/this-firefox`.
3. Click `Load Temporary Add-on`.
4. Select extracted `manifest.json`.
5. Firefox add-on ID is `mydm@mydm.app` (default).

## One-Time App Connection Setup

1. Open MyDM app.
2. Go to `Settings -> Extension`.
3. Paste Chromium ID(s) into `Chromium Extension ID(s)`.
4. Verify Firefox ID in `Firefox Add-on ID(s)`.
5. Click `Register Native Host (All Browsers)`.
6. Restart browser once.

## Verify It Works

1. Open extension popup and confirm connected status.
2. Start any download from browser.
3. Confirm MyDM progress popup updates according to downloaded bytes.
4. Confirm file completes successfully.

## Troubleshooting (Fast)

1. If extension shows disconnected, run `Register Native Host (All Browsers)` again.
2. If Chromium extension ID changed, update it in app settings.
3. After any ID change, restart browser.
4. YouTube download support needs `yt-dlp` installed.

## Maintainer Release Guide

Run from repo root:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\release.ps1 -Version 1.0.0
```

Release output:
- `artifacts/release/<version>/installer/MyDMUserSetup-x64.exe`
- `artifacts/release/<version>/extensions/mydm-extension-chromium.zip`
- `artifacts/release/<version>/extensions/mydm-extension-firefox.zip`

Publish these exact file names in GitHub Release so direct links keep working.

### Publish Release in GitHub UI
1. Open `https://github.com/MuhammadBahawal/idm/releases`.
2. Click `Draft a new release`.
3. Create tag (example: `v1.0.0-launch`) and publish title.
4. Upload these assets:
   - `MyDMUserSetup-x64.exe`
   - `mydm-extension-chromium.zip`
   - `mydm-extension-firefox.zip`
5. Click `Publish release`.

## Security Notes

1. Native host logs redact sensitive values like cookies and tokens.
2. DRM-protected streams are not supported.

## License

MIT
