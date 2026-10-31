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

- `src/DynamicMochi/App.xaml(.cs)`: shared settings, fonts, theme brushes, tray icon, menus, opening the settings window
- `Themes/Shared.xaml`: all styles and theme brush keys, shared by both windows (replaced from code, DynamicResource only)
- `SettingsWindow.xaml(.cs)`: full settings window (Mica, side menu, search, add-place dialog)
- `Assets/Fonts`: Manrope, Onest, Inter, Inter Display, Golos Text, Unbounded, JetBrains Mono (static instances made from the Google Fonts variable fonts with fontTools, OFL in `LICENSES`); `Assets/mochi.ico`
- `Strings.ru.resx` / `Strings.en.resx`: every interface string; `Services/Loc.cs` loads them (XAML: `{DynamicResource T.key}`, code: `Loc.T("key")`), language switches live
- `IslandWindow.Activity.cs`: live activities in the capsule (reminders, timer, notifications, screenshots)
- `IslandWindow.Tasks.cs` + `Services/TaskStore.cs`: tasks (`tasks.json`); `IslandWindow.Timer.cs`: timer and the Календарь / Задачи / Таймер switch
- `IslandWindow.Notify.cs`: screenshot notice (clipboard + `Pictures\Screenshots`), Telegram / Discord via `UserNotificationListener`
- `Services/ArtworkService.cs`: browser title parsing, iTunes covers, disk cache `%APPDATA%\DynamicMochi\art`
- `Controls/ProgressRing.cs`: timer ring
- `src/DynamicMochi/IslandWindow.xaml(.cs)`: capsule, layout (scale, offset, monitor), minimal / detailed / expanded states, theme, tabs, hotkey, full-screen hiding
- `IslandWindow.Music.cs`: now playing, progress, volume, lyric line
- `IslandWindow.Calendar.cs`: month grid, day list, event form, reminders (live activity)
- `IslandWindow.Weather.cs`: weather tab, place picker (city → district, auto-detect, up to 3 places)
- `Controls/WeatherIcon.cs`: vector weather icons (Segoe Fluent has no full weather set)
- `Controls/WeatherFx.cs`: live weather background (one element, OnRender, ≤ 30 fps, ≤ 150 particles)
- `Controls/UiHelpers.cs`: theme cards, toggle, segmented control, slider with inset, thin scroll bars
- `Services/Screens.cs`: monitors, placing the island, full-screen detection; `Services/TrayIcon.cs`: notification-area icon
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
- **2026-10-31 (v1.3, per `docs/SPEC-v1.3.md`):**
  - 1: no tooltip on the volume; volume and progress tracks have a 6 px inset so the thumb is whole at 0 % and 100 %.
  - 2: Inter (400/500/600) for the UI, Inter Display for clocks and big numbers, embedded as resources; font picker Inter / Manrope / Onest / Segoe UI Variable, applied live. Fonts come from one place (`UiFont` / `DisplayFont`). Not done: −1 % tracking (WPF `TextBlock` has no letter-spacing).
  - 3: separate settings window: Mica + theme-coloured title bar (solid fallback without Mica), side menu with accent bar, search across all rows, rows with descriptions, changes apply and save immediately, size and position remembered. Opens from the island tab ("Все настройки"), right click on the capsule and the tray icon; only one window. Sections: Общее (size presets + 80–130 % slider live, top offset, monitor, idle view, clock with music, hide in full screen, autostart, hotkey with capture and "занято" status, language placeholder), Внешний вид (6 cards 180×96, font, artwork blob, animation speed 1 / 0.6 / 0), Музыка (lyrics, lyric size 13–18, equalizer, player/system volume, click control), Погода (places, °C/°F, m/s / km/h, weather in the capsule), Календарь (week start, next event, reminders, 5/8/15 s), О программе (version, GitHub, log folder, reset with confirmation, Shift+click on the version → weather preview). Place picker is a dialog: city (Open-Meteo) → optional district (Nominatim) with Готово / Пропустить, free search for districts and streets, auto-detect. Names in Russian via `namedetails` (`name:ru`), otherwise as returned. The island has no place fields any more.
  - Not done in 3: the English UI (strings are not moved to resources yet; the language setting is stored only).
  - 4: minimal idle view by default: 200×36 clock only, 240×36 art 24 + equalizer (+ 13 px clock, toggle). Hover: after 150 ms grows to the detailed 340×52 view, after 350 ms more opens fully. Reminders still show at 380×52.
  - 4.5: live weather background on the weather tab for all 8 kinds, under the content (layer opacity 0.5), clipped to the capsule, white / #5E7A99 particles, accent tint for sun and stars in coloured themes, 30 fps cap, max 140 drops, stopped completely when the tab is hidden or the island collapses, 800 ms crossfade on weather change, off with "Без анимаций".
  - 5: hotkey Win+Alt+I (configurable), click / double click on the collapsed capsule (toggle, off by default), wheel over the collapsed capsule = volume ±5 % with a thin bar, slide up for full-screen apps (250 ms), first run hint for 4 s.
  - Built in CI only, needs the v1.3 checklist on hardware.
- **2026-10-31 (v1.4, per `docs/SPEC-v1.4.md`):** PR #2 (single self-contained exe) and the spec branch merged into `phase-1`. Release `v1.4.0` is built by CI from the tag (exe + zip, SHA-256 in the notes); tags `v*` now make numbered releases.
  - 1 Cover: one "shown" SMTC session (the playing one; among several, the one that changed track last; a paused one stays while nothing else plays). Title, cover and buttons come from that session only. `MediaPropertiesChanged` of every session is tracked; after a track change the thumbnail is re-read every 500 ms for 4 s and compared by SHA-1; a thumbnail equal to the previous track's cover is not shown (placeholder instead). Browsers: "Artist - Track (Official Video)" is parsed, tails stripped, the channel is not used as the artist; iTunes Search is asked when the browser cover is not square or the title was parsed; a hit only counts when the title matches (lyrics rules); hits are cached on disk, misses for 30 min. Every step goes to `log.txt` (`media:` / `art:` lines). The cover crossfades (110 + 110 ms) when a better one arrives.
  - 2 Root cause: `MarqueeText` never set a text colour, so titles inherited the system black. Now `FgBrush` by default, and the island window itself has `Foreground = FgBrush`.
  - 3 The intermediate "peek" step is gone: hover → 150 ms → one spring to the expanded size.
  - 4/13 Collapsed 420×58 (cover 40, title, live lyric line or artist, equalizer up to 18, divider, clock 15) and 300×58 idle (clock 26 in the clock font, date or greeting, weather "Облачно · +16°" or the next event / task with its colour dot). Thin 2 px progress along the bottom when no lyric line is shown. The detailed view is the default (v1.3 settings are migrated once: `SettingsVersion`). Lyrics: in the island / under it / nowhere, size, backing vocals, artist fallback.
  - 5 Buttons never call `GetCurrentSession()`; they act on the shown session only and do nothing when it is gone.
  - 6 Text font (Manrope default, Onest, Inter, Golos Text, system) and clock font (Unbounded SemiBold default, Manrope ExtraBold, Onest Bold, JetBrains Mono Bold, Inter Display) chosen separately, previews in the settings window. All of them have a `tnum` feature, so digits are tabular without extra work.
  - 7 Root cause of lost settings: `WindowBounds` starts as NaN and `System.Text.Json` refuses to write NaN, so every save threw until the settings window had been closed once. Now NaN is allowed, writes are atomic (temp file + `File.Replace`), every change saves immediately (sliders: 250 ms debounce), a broken file is logged and copied to `settings.bak.json`. The settings window uses the theme gradient (Mica only for OLED and White), accent icons in circles, cards with radius 14 and more spacing.
  - 8 Wheel scrolling in the island and the settings window is done by hand and clamped; the event never leaves the list.
  - 9 Calendar tab: Календарь / Задачи / Таймер. Timer: 1 / 5 / 10 / 25 min, custom "7", "7:30", "1:05:00"; ring + time replaces the clock in the capsule; click pauses; done = accent blink, system sound, notice. Tasks: round checkboxes, optional reminder time (a past time today means tomorrow), reminders like events, done tasks struck through and moved to "Выполненные" after a day, `tasks.json`.
  - 10 Screenshots: `AddClipboardFormatListener` (an image without HTML / file list = a snip) and a watcher on `Pictures\Screenshots`; preview 44 px high, click opens Snipping Tool (falls back to the "edit" verb). Telegram / Discord: implemented with `UserNotificationListener` (polling every 2 s, sender + first words, app logo or the sender's initial in a coloured circle, app badge, 440×58 for 5 s, click opens the app). **Not working in this build:** Windows only gives that API to apps with package identity. A sparse package needs a signed MSIX manifest, a certificate the user trusts and registration with `PackageManager.AddPackageByUriAsync(... ExternalLocationUri ...)`; that is a separate task. Without identity the island starts normally and the settings window says what is missing. Also: the API does not expose the sender's avatar image, so the avatar is the app logo or the initial.
  - 11 All strings moved to `Strings.ru.resx` / `Strings.en.resx` (311 keys) as plain embedded resources (checked in the built exe: no satellite assemblies needed). Switching applies at once (XAML via dynamic resources, code-built parts re-render). Dates and weather in `ru-RU` / `en-US`, place search in the interface language, 24 h default with a 12 h option.
  - 12 Clouds: 3 parallax layers of soft puffs moving with the real wind direction and speed, cloud shadows every 15–25 s for 4–6 s, the sun peeking for 3–5 s on partly cloudy days, moonlit cloud bottoms and stars at night, thin wind lines from 8 m/s. Settings → Weather: live background off / calm / full.
  - 14 "Часы при музыке" works in both views. Rows that do not apply are greyed out with the reason (top gap in notch mode, artwork glow in coloured themes, greetings in the minimal view, live background with animations off, …).
  - 15 New theme colours from the spec, gradient GradTop 0 % → GradMid 55 % → black 100 %, glow 0.5 / blur 22, border 55 %; accent on the active tab, tab hover, progress, volume, play, toggles, sliders, selected cards and day, section headers.
  - 16 Wind units m/s, km/h, mph, kn requested from Open-Meteo directly; now: speed, arrow to where it blows, compass point, gusts, Beaufort name; per hour under the temperature (accent from 8 m/s); daily max in the 7-day list.
  - 17 Shape: Island / Notch. Notch is glued to the top edge, only the bottom corners are round, 8 px reverse corners on top, vertical gradient that is black at the edge; the top gap is disabled with an explanation.
  - Not verified on hardware (CI build only). Needs the v1.4 checklist on Windows 11, especially: two YouTube tabs + YouTube Music, fast track switching, notch at 125/150 %, English on every tab.

