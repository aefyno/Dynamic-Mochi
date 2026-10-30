using System.IO;
namespace DynamicMochi.Services;

/// <summary>Error log at %APPDATA%\DynamicMochi\log.txt. Identical lines are throttled to once a minute.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, DateTime> Last = new();

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicMochi");

    public static void Write(string text)
    {
        lock (Gate)
        {
            try
            {
                if (Last.TryGetValue(text, out var t) && DateTime.Now - t < TimeSpan.FromMinutes(1)) return;
                Last[text] = DateTime.Now;
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, "log.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
            }
            catch
            {
                // never crash because of logging
            }
        }
    }
}
