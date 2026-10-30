using System.IO;
using System.Text.Json;

namespace DynamicMochi.Services;

/// <summary>Stored in %APPDATA%\DynamicMochi\settings.json.</summary>
public sealed class Settings
{
    public string Theme { get; set; } = "Чёрная";
    public bool ShowLyrics { get; set; } = true;

    private static string FilePath => Path.Combine(Log.Folder, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Write("settings load failed: " + ex.Message);
        }
        return new Settings();
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
