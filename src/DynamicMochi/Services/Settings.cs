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

    // general
    public double Scale { get; set; } = 1.0;            // 0.8 … 1.3
    public double TopOffset { get; set; } = 8;          // 0 … 24 px
    public int Monitor { get; set; }                    // 0 = primary
    public bool MinimalIdle { get; set; } = true;
    public bool ClockWithMusic { get; set; } = true;
    public bool HideInFullscreen { get; set; } = true;
    public string Language { get; set; } = "system";    // system / ru / en (prepared, English later)
    public bool HotkeyEnabled { get; set; } = true;
    public int HotkeyModifiers { get; set; } = 0x0008 | 0x0001;   // MOD_WIN | MOD_ALT
    public int HotkeyKey { get; set; } = 0x49;                    // VK_I
    public bool FirstRunDone { get; set; }

    // appearance
    public string Theme { get; set; } = Themes.Default;
    public string Font { get; set; } = "Inter";
    public bool ArtworkTint { get; set; } = true;
    public double AnimationSpeed { get; set; } = 1;     // 1 / 0.6 / 0

    // music
    public bool ShowLyrics { get; set; } = true;
    public double LyricsSize { get; set; } = 15;        // 13 … 18
    public bool ShowEqualizer { get; set; } = true;
    public bool SystemVolume { get; set; }              // false = the player's own volume
    public bool ClickControl { get; set; }

    // weather
    public List<Place> Places { get; set; } = new();
    public int PlaceIndex { get; set; }
    public bool Fahrenheit { get; set; }
    public bool WindKmh { get; set; }
    public bool WeatherInCapsule { get; set; } = true;

    // calendar
    public bool WeekStartsMonday { get; set; } = true;
    public bool ShowNextEvent { get; set; } = true;
    public bool Reminders { get; set; } = true;
    public int ReminderSeconds { get; set; } = 8;

    // settings window
    public WindowBounds SettingsWindow { get; set; } = new();

    public Place? CurrentPlace => Places.Count == 0 ? null : Places[Math.Clamp(PlaceIndex, 0, Places.Count - 1)];

    public static readonly string[] Fonts = { "Inter", "Manrope", "Onest", "Segoe UI Variable" };

    private static string FilePath => Path.Combine(Log.Folder, "settings.json");

    public static Settings Load()
    {
        Settings s = new();
        try
        {
            if (File.Exists(FilePath))
                s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Write("settings load failed: " + ex.Message);
        }
        s.Normalize();
        return s;
    }

    private void Normalize()
    {
        Theme = Themes.Find(Theme).Name;   // removed themes fall back to OLED
        if (!Fonts.Contains(Font)) Font = "Inter";
        Places ??= new();
        if (Places.Count > MaxPlaces) Places = Places.Take(MaxPlaces).ToList();
        PlaceIndex = Places.Count == 0 ? 0 : Math.Clamp(PlaceIndex, 0, Places.Count - 1);
        Scale = Math.Clamp(Scale, 0.8, 1.3);
        TopOffset = Math.Clamp(TopOffset, 0, 24);
        LyricsSize = Math.Clamp(LyricsSize, 13, 18);
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

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Write("settings save failed: " + ex.Message);
        }
    }
}
