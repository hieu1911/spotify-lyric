# Spotify Lyric

Shows the lyrics of the song playing in Spotify on the Windows taskbar, in time with the music.

- No Spotify login or API key needed. Playback state is read through the Windows Media Session API, the same data the Windows volume flyout shows.
- Synced lyrics come from [LRCLIB](https://lrclib.net), which is free and needs no key.
- Shows the current line and the next one, with an animation when the line changes.
- Stays on top of the taskbar and hides itself while a fullscreen app (game, video) is running.

## Requirements

- Windows 10 version 2004 (build 19041) or later, or Windows 11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64)
- The Spotify desktop app

## Installation

### Prebuilt

Run `publish\SpotifyLyric.exe`. Only one copy can run at a time.

### Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
# Run in development
dotnet run

# Package as a single exe into publish\
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

## Usage

Start playing something in Spotify and the lyrics strip appears on the taskbar. By default it sits just left of the clock and tray icons. The text dims while Spotify is paused, and the strip hides when nothing is playing.

- **Left-drag** the strip to move it. The position is saved.
- **Right-click** the strip, or the green ♪ icon in the system tray, to open the menu.

### Menu

| Menu item | What it does |
|---|---|
| Lock position (click-through) | Stops dragging and lets clicks pass through to the taskbar. While locked, open the menu from the tray icon. |
| Show next line | Shows or hides the upcoming line (gray text) |
| Hide when a fullscreen app is active | Hides the strip while you play games or watch fullscreen video |
| Font size | Small / Medium / Large / Extra large |
| Width | 360–800 px |
| Timing offset | Shows lyrics 0.5 s earlier or later per click, for lyrics that are out of sync |
| Reset position | Moves the strip back to its default spot on the taskbar |
| Reload lyrics | Fetches the lyrics for the current song again |
| Start with Windows | Adds or removes the app from startup (`HKCU\...\CurrentVersion\Run`) |
| Exit | Closes the app |

## Configuration

Settings are saved automatically to `%APPDATA%\SpotifyLyric\settings.json`:

| Key | Default | Meaning |
|---|---|---|
| `X`, `Y` | `null` | Window position in physical pixels; `null` places it on the taskbar automatically |
| `Width` | `520` | Width of the strip |
| `FontSize` | `15` | Font size of the main line (the next line is 80% of it) |
| `ShowNextLine` | `true` | Show the next line |
| `Locked` | `false` | Lock position and let clicks pass through |
| `OffsetSec` | `0` | Lyric timing offset in seconds; positive shows lyrics earlier |
| `HideOnFullscreen` | `true` | Hide while a fullscreen app is active |

Delete the file to restore all defaults.

## How it works

| File | Role |
|---|---|
| `Program.cs` | Entry point; keeps the app to a single instance |
| `MediaWatcher.cs` | Reads Spotify's title, artist and playback position through `GlobalSystemMediaTransportControlsSessionManager` every 0.5 s |
| `LyricsProvider.cs` | Fetches lyrics from LRCLIB. It first looks for an exact match by title, artist, album and duration. If none is found, it runs a fuzzy search (dropping `feat.` and suffixes like `- Remastered`) and picks the result whose duration matches best. It parses the LRC format and caches results in memory. |
| `LyricWindow.cs` | Transparent, borderless WPF window; draws the lyrics every 100 ms and handles the menu and tray icon |
| `Native.cs` | Win32 calls: keeping the window above the taskbar, locating the tray area, detecting fullscreen apps |
| `Settings.cs` | Loads and saves settings, and manages the start-with-Windows registry key |

The window is owned by the taskbar so that clicking the taskbar cannot bring it in front of the lyrics. Because of this, the window is destroyed when Explorer restarts. The app then relaunches itself with `--wait` and waits for Explorer to come back.

## Troubleshooting

- **Nothing shows up:** make sure the Spotify desktop app is playing. The web player in a browser is not detected.
- **"Lyrics not found" / "No synced lyrics for this song":** LRCLIB has no synced lyrics for that track.
- **"Network error, retrying…":** `lrclib.net` could not be reached. The app retries after 15 seconds.
- **Lyrics are out of sync with the music:** use *Timing offset* in the menu.
- **Locked and can't right-click the strip:** open the menu from the ♪ tray icon and uncheck *Lock position*.
