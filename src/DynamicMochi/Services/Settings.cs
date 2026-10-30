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

/// <summary>Stored in %APPDATA%\DynamicMochi\settings.json.</summary>
public sealed class Settings
{
    public const int MaxPlaces = 3;

    public string Theme { get; set; } = Themes.Default;
    public bool ShowLyrics { get; set; } = true;

    // weather
    public List<Place> Places { get; set; } = new();
    public int PlaceIndex { get; set; }
    public bool Fahrenheit { get; set; }

    // calendar
    public bool WeekStartsMonday { get; set; } = true;
    public bool ShowNextEvent { get; set; } = true;

    public Place? CurrentPlace => Places.Count == 0 ? null : Places[Math.Clamp(PlaceIndex, 0, Places.Count - 1)];

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
        // removed themes fall back to OLED
        s.Theme = Services.Themes.Find(s.Theme).Name;
        s.Places ??= new();
        if (s.Places.Count > MaxPlaces) s.Places = s.Places.Take(MaxPlaces).ToList();
        s.PlaceIndex = s.Places.Count == 0 ? 0 : Math.Clamp(s.PlaceIndex, 0, s.Places.Count - 1);
        return s;
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
