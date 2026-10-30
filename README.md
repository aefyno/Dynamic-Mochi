# Dynamic Mochi

A dynamic island for Windows 11. A small capsule sits at the top center of the screen and expands on hover to show what's playing, synced lyrics and quick settings.

## Features

- Now playing from any SMTC-compatible player (Spotify, YouTube Music in the browser, etc.)
- Album art, progress bar with seeking, playback controls
- Synced lyrics from [lrclib.net](https://lrclib.net), shown as a single line under the capsule
- Themes: Black, White, Red-Black, White-Black
- Start with Windows

## Building

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet run --project src/DynamicMochi
```

Prebuilt binaries are attached to every CI run (Actions → latest successful run → `DynamicMochi-win-x64`). They are a single self-contained exe, no .NET install needed.

## Stack

C# / WPF on .NET 8 (`net8.0-windows10.0.22621.0`), Windows Runtime APIs for media sessions.

See [CONTRIBUTING.md](CONTRIBUTING.md) for how we work on it.
