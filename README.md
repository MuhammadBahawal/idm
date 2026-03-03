# MyDM - End User Install Guide

MyDM is a Windows download manager with browser integration.

## End User Flow (Simple)
1. Download `MyDMUserSetup-x64-<version>.exe`.
2. Double-click the setup file.
3. Click `Next -> Next -> Install -> Finish`.
4. Open `MyDM Download Manager` from Start Menu.

## Files You Must Publish in GitHub Release
Upload these 3 files in every release:
1. `MyDMUserSetup-x64-<version>.exe`
2. `mydm-extension-chromium.zip`
3. `mydm-extension-firefox.zip`

## Suggested Release Download Links (README)
Replace `<owner>` and `<repo>`:

- Installer EXE: `https://github.com/<owner>/<repo>/releases/latest/download/MyDMUserSetup-x64-<version>.exe`
- Chromium extension: `https://github.com/<owner>/<repo>/releases/latest/download/mydm-extension-chromium.zip`
- Firefox extension: `https://github.com/<owner>/<repo>/releases/latest/download/mydm-extension-firefox.zip`

Note: Keep asset file names exactly same as above.

## Browser Extension Setup (One Time)
### Chromium (Chrome, Edge, Brave, Vivaldi, Opera)
1. Extract `mydm-extension-chromium.zip`.
2. Open browser extensions page.
3. Enable `Developer Mode`.
4. Click `Load unpacked` and select extracted folder.
5. Copy extension ID from extension details.

### Firefox
1. Extract `mydm-extension-firefox.zip`.
2. Open `about:debugging#/runtime/this-firefox`.
3. Click `Load Temporary Add-on`.
4. Select extracted `manifest.json`.
5. Firefox add-on ID is `mydm@mydm.app` unless customized.

## App Side One-Time Setup
1. Open MyDM app.
2. Go to `Settings -> Extension`.
3. Paste Chromium ID(s) in `Chromium Extension ID(s)`.
4. Confirm Firefox ID(s) in `Firefox Add-on ID(s)`.
5. Click `Register Native Host (All Browsers)`.
6. Restart browser.

## Verify Installation
1. Open extension popup and check connected status.
2. Start a test download from browser.
3. Confirm MyDM progress popup updates with real downloaded bytes.

## Build Release (Maintainer)
Run from repo root:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\release.ps1 -Version 1.0.0
```

Output:
- `artifacts/release/<version>/installer/MyDMUserSetup-x64-<version>.exe`
- `artifacts/release/<version>/extensions/mydm-extension-chromium.zip`
- `artifacts/release/<version>/extensions/mydm-extension-firefox.zip`

## Notes
- If extension ID changes, run native host registration again.
- YouTube downloads require `yt-dlp` on user machine.
- DRM-protected streams are not supported.
- Native host logs redact sensitive values (cookies/tokens/signatures).

## License
MIT
