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
- **2026-10-30 (evening):** design pass v1.1 per `docs/DESIGN.md`. Collapsed capsule 320×44 with art, marquee title, live equalizer and always-visible clock (200×44 clock + date when idle). Expanded 560 with per-tab height, artwork colour blob on the background, sliding tab pill (18 px icons). Home: 52 px clock, date, status line, music card with 72 px art and controls. Music: 120 px art, source line, hover-growing progress with drag seeking and preview, circular accent play button, lyrics preview (current + next line). Settings: theme preview cards with correct selection, full-row toggles with neutral off state, quit button. Themes rebuilt as token sets: red gradient + glow, monochrome with white outline and inverted accents. Theme switch animates colours (250 ms). Lyric line: separate pill under the capsule, 15 px, hidden while expanded; search logs every step to `log.txt`; if the player never reports a duration, search starts after 5 s without it. Launch/exit scale animations, track-change squish and art crossfade. Not done: lyric pill width is not animated (auto-sizes), tooltips use the default 400 ms delay (ToolTipService delay is per element), clock colon does not blink.
