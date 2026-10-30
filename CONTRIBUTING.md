# Contributing

## Branches

- `phase-1` is the current feature branch (owner: @enlxxx).
- Fixes from others go into separate `fix/...` branches and land through a pull request. No direct pushes to someone else's branch.
- Phase 2 (weather, calendar) is owned by @aefyno and starts after phase 1 is merged.

## Bug reports

Steps → expected → actual → log/screenshot.
The log is at `%APPDATA%\DynamicMochi\log.txt`.

## Conventions

- UI icons come from Segoe Fluent Icons, no emoji in the interface.
- Characters and artwork must be original.
- To add a theme, add one line to `Themes.All`.

## Ownership

| Area | Owner | Status |
|---|---|---|
| Capsule window, animations, tabs | @enlxxx | ready for testing |
| Media (SMTC: track, art, controls, seeking) | @enlxxx | ready for testing |
| Lyrics (lrclib) and lyric line | @enlxxx | ready for testing |
| Themes, autostart, settings | @enlxxx | ready for testing |
| Testing on Windows 11, 125–150% scaling, multi-monitor | @aefyno | in progress |
| Phase 2: weather, calendar | @aefyno | after phase 1 merge |

## Project layout

- `src/DynamicMochi/IslandWindow.xaml(.cs)`: capsule, hover expand, Home / Music / Settings tabs, lyric line
- `Services/MediaService.cs`: SMTC now playing, art, position, controls
- `Services/LyricsService.cs`: lrclib lookup and matching
- `Services/Themes.cs`: color themes
- `Services/Settings.cs`: `%APPDATA%\DynamicMochi\settings.json`
- `Services/AutoStart.cs`: `HKCU\...\Run` entry
- `Services/Native.cs`: non-activating window, hidden from Alt+Tab
- `.github/workflows/build.yml`: Windows build on every push

## Lyrics matching rules

1. Search only once the player reports a track duration.
2. Title must match fully (fuzzy only for long titles); artist at least partially.
3. Duration within ±8 s; sped up / slowed versions get rescaled timings.
4. Prefer the most complete synced result; fill a missing intro from the plain lyrics.
5. On network failure retry every 15 s, up to 4 times, 10 s timeout.
6. Interpolate position between polls; backing vocals in parentheses are smaller and italic; intros and instrumental breaks show three dots.

## Changelog

- **2026-10-30:** project setup, CI, phase 1 (capsule, SMTC, lyrics, themes, autostart). CI build is green. Next: first run on real hardware (hover expand, Spotify and YouTube Music, seeking, lyric line, 125–150% scaling, themes, autostart).
