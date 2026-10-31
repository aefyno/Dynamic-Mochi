using System.IO;
using System.Text.Json;

namespace DynamicMochi.Services;

/// <summary>A saved weather location.</summary>
public sealed class Place
{
    public string Name { get; set; } = "";      // what we show: "Строгино, Москва"
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string Timezone { get; set; } = "auto";
    public string Country { get; set; } = "";
}

public sealed class WindowBounds
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 900;
    public double Height { get; set; } = 640;
}

/// <summary>Stored in %APPDATA%\DynamicMochi\settings.json. One shared instance: <see cref="App.Settings"/>.</summary>
public sealed class Settings
{
    public const int MaxPlaces = 3;

    /// <summary>Format version: 0 = up to v1.3, 4 = v1.4 (new defaults applied once on upgrade).</summary>
    public int SettingsVersion { get; set; }   // missing in old files = 0
    public const int CurrentVersion = 6;

    // general
    public double Scale { get; set; } = 1.0;            // 0.8 … 1.3
    public double TopOffset { get; set; } = 8;          // 0 … 24 px
    public int Monitor { get; set; }                    // 0 = primary
    public bool MinimalIdle { get; set; }               // v1.4: the big detailed view is the default
    public int Shape { get; set; }                      // 0 = island (pill with a gap), 1 = notch (glued to the top edge)
    public bool Greetings { get; set; } = true;
    public bool Clock24 { get; set; } = true;
    public bool ClockWithMusic { get; set; } = true;
    public bool HideInFullscreen { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;   // v1.6.2: on by default, old files without the key get it too
    public bool ShowPowerTab { get; set; } = true;       // v1.6: sleep / lock / shut down tab
    public bool HomeWeatherBlock { get; set; } = true;   // v1.6: Home bottom bar
    public bool HomeQuickButtons { get; set; } = true;
    public bool DoNotDisturb { get; set; }
    public string Language { get; set; } = "system";    // system / ru / en
    public bool HotkeyEnabled { get; set; } = true;
    public int HotkeyModifiers { get; set; } = 0x0008 | 0x0001;   // MOD_WIN | MOD_ALT
    public int HotkeyKey { get; set; } = 0x49;                    // VK_I
    public bool FirstRunDone { get; set; }

    // appearance
    public string Theme { get; set; } = Themes.Default;
    public string Font { get; set; } = "Manrope";
    public string ClockFont { get; set; } = "Unbounded";
    public bool ArtworkTint { get; set; } = true;
    public double GlowStrength { get; set; } = 0.5;      // v1.6: 0 … 1, 0.5 = the theme's own glow
    public bool GradientFromArt { get; set; } = true;    // v1.6: Gradient Black takes the artwork colour (else fixed purple)
    public double AnimationSpeed { get; set; } = 1;     // 1 / 0.6 / 0

    // music
    public int LyricsPlace { get; set; }                 // 0 = in the island, 1 = under the island, 2 = nowhere
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ShowLyrics
    {
        get => LyricsPlace != 2;
        set { if (!value) LyricsPlace = 2; else if (LyricsPlace == 2) LyricsPlace = 0; }
    }
    public double LyricsSize { get; set; } = 15;        // 12 … 18
    public bool LyricsBacking { get; set; } = true;     // backing vocals in parentheses
    public bool LyricsArtistFallback { get; set; } = true;
    public bool ShowEqualizer { get; set; } = true;
    public bool SystemVolume { get; set; }              // false = the player's own volume
    public bool ClickControl { get; set; }

    // weather
    public List<Place> Places { get; set; } = new();
    public int PlaceIndex { get; set; }
    public bool Fahrenheit { get; set; }
    public bool WindKmh { get; set; }                   // up to v1.3 only, migrated to WindUnit
    public int WindUnit { get; set; }                   // 0 m/s, 1 km/h, 2 mph, 3 kn
    public bool WeatherInCapsule { get; set; } = true;
    public int LiveWeather { get; set; } = 2;           // 0 off, 1 calm, 2 full

    // notifications
    public bool Notifications { get; set; } = true;
    public bool NotifyTelegram { get; set; } = true;
    public bool NotifyDiscord { get; set; } = true;
    public bool Screenshots { get; set; } = true;

    // calendar
    public bool WeekStartsMonday { get; set; } = true;
    public bool ShowNextEvent { get; set; } = true;
    public bool Reminders { get; set; } = true;
    public int ReminderSeconds { get; set; } = 8;

    // settings window
    public WindowBounds SettingsWindow { get; set; } = new();

    public Place? CurrentPlace => Places.Count == 0 ? null : Places[Math.Clamp(PlaceIndex, 0, Places.Count - 1)];

    public static readonly string[] Fonts = { "Manrope", "Onest", "Inter", "Golos Text", "System" };
    public static readonly string[] ClockFonts = { "Unbounded", "Manrope", "Onest", "JetBrains Mono", "Inter Display" };

    private static string FilePath => Path.Combine(Log.Folder, "settings.json");
    private static string BackupPath => Path.Combine(Log.Folder, "settings.bak.json");

    /// <summary>NaN (unset window position) must survive a round trip: without this every save threw and nothing was stored.</summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static Settings Load()
    {
        Settings s;
        bool loaded = false;
        try
        {
            if (File.Exists(FilePath))
            {
                s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
                loaded = true;
                Log.Write($"settings loaded: theme {s.Theme}, font {s.Font}/{s.ClockFont}, language {s.Language}");
            }
            else s = new Settings();
        }
        catch (Exception ex)
        {
            // broken file: keep a copy and say so, never reset silently
            Log.Write("settings load failed, kept a copy as settings.bak.json: " + ex.Message);
            try { File.Copy(FilePath, BackupPath, overwrite: true); } catch { }
            s = new Settings();
        }
        if (loaded && s.SettingsVersion < CurrentVersion) s.Migrate();
        s.SettingsVersion = CurrentVersion;
        s.Normalize();
        return s;
    }

    /// <summary>Settings written by v1.3 or older: new v1.4 defaults.</summary>
    private void Migrate()
    {
        Log.Write($"settings: migrating from version {SettingsVersion}");
        if (SettingsVersion < 4)
        {
            MinimalIdle = false;
            if (Font is "Inter" or "") Font = "Manrope";
            if (WindKmh) WindUnit = 1;
        }
        SettingsVersion = CurrentVersion;
    }

    private void Normalize()
    {
        Theme = Themes.Find(Theme).Name;   // removed themes fall back to OLED
        if (Font == "Segoe UI Variable") Font = "System";
        if (!Fonts.Contains(Font)) Font = "Manrope";
        if (!ClockFonts.Contains(ClockFont)) ClockFont = "Unbounded";
        if (Language is not ("system" or "ru" or "en")) Language = "system";
        LyricsPlace = Math.Clamp(LyricsPlace, 0, 2);
        WindUnit = Math.Clamp(WindUnit, 0, 3);
        LiveWeather = Math.Clamp(LiveWeather, 0, 2);
        Shape = Math.Clamp(Shape, 0, 1);
        Places ??= new();
        if (Places.Count > MaxPlaces) Places = Places.Take(MaxPlaces).ToList();
        PlaceIndex = Places.Count == 0 ? 0 : Math.Clamp(PlaceIndex, 0, Places.Count - 1);
        Scale = Math.Clamp(Scale, 0.8, 1.3);
        TopOffset = Math.Clamp(TopOffset, 0, 24);
        LyricsSize = Math.Clamp(LyricsSize, 12, 18);
        GlowStrength = double.IsNaN(GlowStrength) ? 0.5 : Math.Clamp(GlowStrength, 0, 1);
        AnimationSpeed = AnimationSpeed <= 0 ? 0 : AnimationSpeed < 0.8 ? 0.6 : 1;
        if (ReminderSeconds is not (5 or 8 or 15)) ReminderSeconds = 8;
        SettingsWindow ??= new();
    }

    /// <summary>Copy every value from another instance (used by "reset all").</summary>
    public void CopyFrom(Settings other)
    {
        foreach (var p in typeof(Settings).GetProperties().Where(p => p.CanWrite))
            p.SetValue(this, p.GetValue(other));
    }

    private static readonly object SaveGate = new();

    /// <summary>Atomic write: temp file, then File.Replace (the old file is never half-written).</summary>
    public void Save()
    {
        lock (SaveGate)
        {
            try
            {
                Directory.CreateDirectory(Log.Folder);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch (Exception ex)
            {
                Log.Write("settings save failed: " + ex.Message);
            }
        }
    }
}
