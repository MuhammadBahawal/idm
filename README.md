# MyDM - Internet Download Manager Style Downloader

MyDM is a Windows-first downloader made of:
- `MyDM.App` (WPF desktop UI)
- `MyDM.NativeHost` (Chrome/Edge native messaging bridge)
- `MyDM.Core` (download engine, parsers, queue, storage)
- `extension/` (Manifest V3 Chrome/Edge extension)

## Features
- Multi-segment downloads with HTTP range requests
- Pause/resume and state restore
- Retry with backoff
- Queue manager and category grouping
- HLS/DASH detection and handoff (non-DRM only)
- Browser integration via Native Messaging
- YouTube page downloads via `yt-dlp` from extension click

## Prerequisites
- Windows 10/11
- .NET 8 SDK
- Node.js 18+
- `yt-dlp` available in `PATH` (or `python -m yt_dlp`)
- Optional: `ffmpeg` for advanced media workflows

## Fresh Clone Build
```powershell
dotnet build MyDM.slnx
dotnet test tests\MyDM.Core.Tests\MyDM.Core.Tests.csproj

cd extension
npm install
npm run build
cd ..
```

## Run Desktop + Native Host
### Option A: Run desktop app
```powershell
dotnet run --project src\MyDM.App\MyDM.App.csproj
```

### Option B: Run native host directly for protocol smoke
```powershell
dotnet run --project src\MyDM.NativeHost\MyDM.NativeHost.csproj
```

Native host logs are written to:
- `%LOCALAPPDATA%\MyDM\nativehost.log`

## Load Extension (Chrome/Edge)
1. Build extension: `cd extension && npm run build`
2. Open `chrome://extensions` or `edge://extensions`
3. Enable Developer Mode
4. Click `Load unpacked` and select `extension/`
5. Copy the extension ID shown in the browser

## Register Native Messaging Host
Use the extension ID from step 5:
```powershell
src\MyDM.NativeHost\install-host.bat <EXTENSION_ID>
```

Or inside desktop app:
- Open `Settings -> Extension`
- Set `Extension ID`
- Click `Register Native Host`

Important:
- Native messaging requires an exact extension ID in `allowed_origins`.
- If extension ID changes, re-register host.

## Smoke Checklist
1. Open extension popup and verify `Connected to MyDM`.
2. On a page with downloadable media/file, click `Download with MyDM`.
3. Confirm native host log shows correlated `requestId` events.
4. Confirm desktop/host status transitions to `Downloading` then `Complete`.
5. Verify saved file exists under `Downloads\MyDM` (or configured folder).
6. On a YouTube video page, click the in-player MyDM button and confirm `yt-dlp` saves the exact current video URL.

Automated native host smoke:
```powershell
powershell -ExecutionPolicy Bypass -File tests\NativeHost.Smoke.ps1
```

Automated YouTube smoke (`yt-dlp` required):
```powershell
powershell -ExecutionPolicy Bypass -File tests\NativeHost.YouTubeSmoke.ps1
```

## Known Limits
- DRM-protected streams are not supported.
- Extension automation across real sites still requires manual browser validation.
- Native messaging setup depends on matching browser extension ID.
- YouTube downloads require `yt-dlp` installation on the host machine.
- Full desktop-side HLS/DASH segment download+mux pipeline is not enabled in this build.
  `add_media_download` now returns a clear `MEDIA_PIPELINE_NOT_IMPLEMENTED` error for raw manifest URLs.

## Project Layout
```text
d:\idm\
  MyDM.slnx
  src\
    MyDM.Core\
    MyDM.App\
    MyDM.NativeHost\
  tests\
    MyDM.Core.Tests\
  extension\
```

## License
MIT
