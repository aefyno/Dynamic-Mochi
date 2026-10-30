# Dynamic Mochi

Dynamic Island для Windows 11: капсула сверху по центру экрана, которая плавно раскрывается при наведении.
Музыка из любого плеера (Spotify, YouTube Music в браузере и т.д.) и синхронизированный текст песен.

Стек: **C# + WPF, .NET 8** (`net8.0-windows10.0.22621.0`), музыка через системный **SMTC**, тексты — **lrclib.net**.

## Собрать и запустить
Нужен [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (`dotnet --version` → 8.x).

```powershell
dotnet run --project src/DynamicMochi
```

Готовая сборка без SDK: вкладка **Actions** → последний зелёный запуск → артефакт `DynamicMochi-win-x64`
(нужен .NET 8 Desktop Runtime).

## Как работаем
Перед работой читаем [`HANDOFF.md`](HANDOFF.md), после — дописываем, что сделали.
