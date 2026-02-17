# MyDM — Internet Download Manager

A production-grade download manager with a .NET 8 WPF desktop app and Chrome/Edge browser extension (Manifest V3).

## Features

- **Multi-segment downloading** — HTTP Range requests with configurable connections (1–32)
- **Pause/Resume** — Persistent state survives app restarts
- **HLS/DASH support** — Parse manifests, select quality, download segments (non-DRM only)
- **ffmpeg muxing** — Merge video+audio streams into MP4/MKV
- **Queue & Scheduler** — Sequential, concurrent, or time-based queue processing
- **Speed Limiter** — Global and per-download bandwidth controls
- **Browser Extension** — Chrome/Edge context menu, video overlay, quality selection modal
- **Categories** — Auto-detect: Video, Music, Documents, Programs, Compressed, Others
- **Dark Theme UI** — IDM-inspired modern WPF interface
- **Native Messaging** — Secure extension↔desktop communication
- **Crash Recovery** — Download state persisted in SQLite, .part files, atomic rename on completion
- **Retry with Backoff** — Exponential backoff with jitter, HTTP error classification

## Architecture

```
┌──────────────────┐  Native Messaging   ┌──────────────────┐
│  Browser Extension│◄──────────────────►│  MyDM.NativeHost │
│  (TypeScript/MV3) │      stdio          │  (.NET Console)  │
└──────────────────┘                     └────────┬─────────┘
                                                  │ shares
                                         ┌────────▼─────────┐
                                         │    MyDM.Core      │
                                         │  (Engine, DB,     │
                                         │   Parsers, Queue) │
                                         └────────▲─────────┘
                                                  │ references
                                         ┌────────┴─────────┐
                                         │    MyDM.App       │
                                         │  (WPF Desktop)    │
                                         └──────────────────┘
```

## Quick Start

### Prerequisites
- .NET 8 SDK
- Node.js 18+ (for extension build)
- ffmpeg (optional, for HLS/DASH muxing)

### Build & Run

```bash
# Build the entire solution
dotnet build MyDM.slnx

# Run the desktop app
dotnet run --project src/MyDM.App/MyDM.App.csproj

# Run tests
dotnet test tests/MyDM.Core.Tests/MyDM.Core.Tests.csproj

# Build the extension
cd extension && npm install && npm run build
```

### Install the Extension
1. Build the extension (`cd extension && npm run build`)
2. Open Chrome → `chrome://extensions` → Enable Developer Mode
3. Click "Load unpacked" → select the `extension/` folder
4. Register native host: run `src/MyDM.NativeHost/install-host.bat`

## Project Structure

```
d:\idm\
├── MyDM.slnx                    # Solution file
├── src/
│   ├── MyDM.Core/               # Core library (no UI dependency)
│   │   ├── Data/                 # SQLite database + repository
│   │   ├── Engine/               # DownloadEngine, SegmentDownloader, SpeedLimiter, RetryPolicy
│   │   ├── Media/                # FfmpegMuxer
│   │   ├── Models/               # DownloadItem, DownloadSegment, NativeMessage, etc.
│   │   ├── Parsers/              # HlsParser, DashParser, MimeDetector
│   │   ├── Queue/                # QueueManager
│   │   └── Utilities/            # UrlHelper, FileHelper
│   ├── MyDM.App/                 # WPF desktop application
│   │   ├── Converters/           # WPF value converters
│   │   ├── Themes/               # Dark theme resource dictionary
│   │   ├── ViewModels/           # MainViewModel, DownloadItemViewModel
│   │   └── Views/                # MainWindow, AddUrlDialog, Settings, Details
│   └── MyDM.NativeHost/          # Native Messaging host (Chrome/Edge bridge)
├── tests/
│   └── MyDM.Core.Tests/          # Unit tests (55 tests)
└── extension/                    # Chrome/Edge extension (MV3)
    ├── src/                      # TypeScript source
    ├── dist/                     # Compiled output
    ├── icons/                    # Extension icons
    ├── popup.html                # Extension popup
    └── options.html              # Extension settings
```

## Database Schema (SQLite)

| Table | Purpose |
|-------|---------|
| Downloads | All download records with URL, path, status, progress |
| Segments | Per-download HTTP Range segments |
| Queues | Named download queues |
| QueueItems | Download-to-queue mapping |
| Categories | File type categorization rules |
| Settings | Key-value app settings |
| Logs | Per-download event log entries |

## Known Limitations

- **DRM not supported** — By design, no DRM circumvention (Netflix, Prime, etc.)
- **FTP not supported** — Only HTTP/HTTPS downloads
- **Windows only** — macOS/Linux support is a future roadmap item
- **ffmpeg required** — For HLS/DASH muxing, ffmpeg must be installed separately

## License

MIT