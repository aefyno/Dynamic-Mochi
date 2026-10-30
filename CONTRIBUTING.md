# Contributing

## Branches

- `phase-1` is the current feature branch (owner: @enlxxx).
- Fixes from others go into separate `fix/...` branches and land through a pull request. No direct pushes to someone else's branch.
- Phase 2 (weather, calendar) moved to @enlxxx with spec v1.2 (`docs/SPEC-v1.2.md`).

## Bug reports

Steps → expected → actual → log/screenshot.
The log is at `%APPDATA%\DynamicMochi\log.txt`.

## Conventions

- UI icons come from Segoe Fluent Icons, no emoji in the interface.
- Characters and artwork must be original.
- To add a theme, add one line to `Themes.All`.
- Theme brushes are replaced, never mutated: reference them only with `DynamicResource` / `SetResourceReference` (WPF freezes resources resolved by styles and templates).
- Everything inside the capsule is clipped to its rounded shape (`CapsuleContent.Clip`); the only shadow/glow lives on `GlowHost`, which has no background.
- Commits: English, author as in the history, no co-author trailers.

## Ownership

| Area | Owner | Status |
|---|---|---|
| Capsule window, shape, animations, tabs | @enlxxx | v1.2 ready for testing |
| Media (SMTC: track, art, controls, seeking), volume | @enlxxx | v1.2 ready for testing |
| Lyrics (lrclib) and lyric line | @enlxxx | ready for testing |
| Themes (6), autostart, settings | @enlxxx | v1.2 ready for testing |
| Phase 2: calendar, reminders | @enlxxx | v1.2 ready for testing |
| Phase 2: weather, place picker | @enlxxx | v1.2 ready for testing |
| Specs, design review, testing on Windows 11 (100/125/150 %, multi-monitor) | @aefyno | in progress |

## Project layout

- `src/DynamicMochi/IslandWindow.xaml(.cs)`: capsule, shape and clip, theme, expand/collapse, tabs, settings rows, keyboard for fields
- `IslandWindow.Music.cs`: now playing, progress, volume, lyric line
- `IslandWindow.Calendar.cs`: month grid, day list, event form, reminders (live activity)
- `IslandWindow.Weather.cs`: weather tab, place picker (city → district, auto-detect, up to 3 places)
- `Controls/WeatherIcon.cs`: vector weather icons (Segoe Fluent has no full weather set)
- `Services/MediaService.cs`: SMTC now playing, art, position, controls
- `Services/VolumeService.cs`: volume of the playing app (Core Audio sessions via NAudio), system volume fallback
- `Services/LyricsService.cs`: lrclib lookup and matching
- `Services/CalendarStore.cs`: `ICalendarSource`, local `%APPDATA%\DynamicMochi\events.json`
- `Services/WeatherService.cs`: Open-Meteo forecast, `weather.json` cache, WMO codes
- `Services/PlaceService.cs`: Open-Meteo geocoding, Nominatim (1 req/s), Windows location
- `Services/Themes.cs`: theme tokens
- `Services/Settings.cs`: `%APPDATA%\DynamicMochi\settings.json`
- `Services/AutoStart.cs`: `HKCU\...\Run` entry
- `Services/Native.cs`: non-activating tool window; activation only while typing
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
- **2026-10-31:** startup fix merged (#1). v1.1 was invisible because `ApplyTheme` threw on a frozen theme brush before the scale-in started.
- **2026-10-31 (v1.2, per `docs/SPEC-v1.2.md`):**
  - A1: capsule content is clipped to the rounded shape on every size change (the red rectangle in the corners was the artwork blob drawn as a plain rectangle). Single shadow/glow on a background-less container. Window size is fixed; after each size animation the whole layered window is repainted to remove leftovers.
  - A2: collapsed 340×52 (art 36, title and clock 14), idle 260×52 (22 px clock, date, weather), expanded 600 wide, height per tab.
  - A4/A5: root cause of the frozen brushes and of the empty theme section: theme brushes were recoloured in place; WPF freezes them once styles/templates resolve them. Now every theme change creates new brushes and swaps them into `Resources`. Startup is split into isolated steps, each failure is logged (`startup step '...' failed`), theme cards are built one by one with their own error handling.
  - T: six themes (Белая, OLED, Красная, Синяя, Зелёная, Оранжевая). Coloured themes: horizontal colour → black gradient when collapsed, diagonal GradTop → GradMid 35 % → black 75 % when expanded (stops and points animate), glow in the accent colour, no artwork blob. Unknown or old theme names fall back to OLED. Theme cards 3×2, 100×64, real mini capsule with gradient and glow, accent ring and check mark.
  - B: calendar tab: month grid (Mon/Sun first), today / selected / hover / event dots, month slide 220 ms, mouse wheel. Day list with cards and marquee on hover, past events dimmed. Form: title, date stepper, time, all-day, 6 colours, reminder, save (Enter) / cancel (Esc) / delete with confirmation. Events in `events.json`. Home shows the next event today. Reminder = live activity 380×52 for 8 s; hover opens the calendar on that event.
  - C: weather tab via Open-Meteo: now (icon 48, 44 px temperature, feels like, wind, humidity), 12 hours, 7 days with range bars, weather blob, cache with "обновлено". Place picker in Settings: city search (300 ms debounce, region + country, arrows/Enter), optional district via Nominatim (1 req/s, User-Agent), auto-detect via Windows location with a link to the privacy settings, up to 3 places with page dots. Units °C/°F. Home and the idle capsule show the weather.
  - D: settings sections Внешний вид → Погода → Календарь → Поведение in a scroll view with a 4 px bar that appears only while scrolling.
  - Extra: volume slider with mute on the Music tab. Changes the playing app's own volume (matched by process name), falls back to the system volume.
  - Text fields: the window still never takes focus, except after a click into a field; the previous app gets focus back when the island closes.
  - Not verified on hardware yet (built in CI only): needs the full run of the v1.2 checklist on Windows 11 at 100/125/150 %.
