using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicMochi.Services;

/// <summary>
/// Real album covers for browser players (YouTube gives a video frame and the channel as the artist):
/// parses "Artist - Track (Official Video)", asks the iTunes Search API, caches hits on disk (%APPDATA%\DynamicMochi\art).
/// </summary>
public static class ArtworkService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<string, DateTime> Misses = new();   // "artist|track" → when nothing was found
    private static readonly object Gate = new();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicMochi/1.4 (github.com/aefyno/Dynamic-Mochi)");
        return c;
    }

    private static string Folder => Path.Combine(Log.Folder, "art");

    private static readonly Regex Tail = new(
        @"\s*[\(\[\{]\s*(official\s*)?(music\s*)?(video|audio|lyrics?|lyric\s*video|visuali[sz]er|clip|клип|mv|hd|hq|4k|8k|remastered|live)[^\)\]\}]*[\)\]\}]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Bare = new(@"\s+(hd|hq|4k|8k|official\s+video|official\s+audio|lyrics?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsBrowser(string appId)
    {
        string id = appId.ToLowerInvariant();
        return new[] { "chrome", "msedge", "edge", "opera", "firefox", "yandex", "brave", "vivaldi", "arc" }.Any(id.Contains);
    }

    /// <summary>"Artist - Track (Official Video)" → (Artist, Track). Null when the title is not of that form.</summary>
    public static (string Artist, string Track)? ParseVideoTitle(string title)
    {
        string t = Tail.Replace(title, "");
        t = t.Split(" | ")[0];
        foreach (var dash in new[] { " - ", " — ", " – ", " -- " })
        {
            int i = t.IndexOf(dash, StringComparison.Ordinal);
            if (i <= 0) continue;
            string artist = t.Substring(0, i).Trim();
            string track = Bare.Replace(t.Substring(i + dash.Length), "").Trim().Trim('"', '«', '»', '“', '”');
            if (artist.Length > 0 && track.Length > 0) return (artist, track);
        }
        return null;
    }

    /// <summary>Strip "(Official Video)" style tails from a plain title.</summary>
    public static string CleanTail(string title) => Bare.Replace(Tail.Replace(title, ""), "").Trim();

    private static string FileFor(string artist, string track)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes((artist + "|" + track).ToLowerInvariant()));
        return Path.Combine(Folder, Convert.ToHexString(hash) + ".jpg");
    }

    /// <summary>Cached or freshly found 600×600 cover; null when iTunes has no matching song.</summary>
    public static async Task<byte[]?> FindAsync(string artist, string track)
    {
        string key = (artist + "|" + track).ToLowerInvariant();
        string file = FileFor(artist, track);
        try
        {
            if (File.Exists(file))
            {
                Log.Write($"art: disk cache hit for {artist} — {track}");
                return await File.ReadAllBytesAsync(file);
            }
        }
        catch { }
        lock (Gate)
            if (Misses.TryGetValue(key, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(30)) return null;

        try
        {
            string term = Uri.EscapeDataString((artist + " " + track).Trim());
            string json = await Http.GetStringAsync($"https://itunes.apple.com/search?term={term}&entity=song&limit=5");
            using var doc = JsonDocument.Parse(json);
            JsonElement? best = null;
            foreach (var item in doc.RootElement.GetProperty("results").EnumerateArray())
            {
                if (!LyricsService.SameSong(item, track)) continue;
                if (best == null || LyricsService.SameArtist(item, artist)) best = item;
                if (LyricsService.SameArtist(item, artist)) break;
            }
            string? url = best is { } b && b.TryGetProperty("artworkUrl100", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(url))
            {
                Log.Write($"art: iTunes has no match for {artist} — {track}");
                lock (Gate) Misses[key] = DateTime.UtcNow;
                return null;
            }
            url = url.Replace("100x100", "600x600");
            var bytes = await Http.GetByteArrayAsync(url);
            Log.Write($"art: iTunes cover found for {artist} — {track}");
            try
            {
                Directory.CreateDirectory(Folder);
                await File.WriteAllBytesAsync(file, bytes);
            }
            catch (Exception ex) { Log.Write("art cache write failed: " + ex.Message); }
            return bytes;
        }
        catch (Exception ex)
        {
            Log.Write($"art: iTunes search failed for {artist} — {track}: {ex.Message}");
            lock (Gate) Misses[key] = DateTime.UtcNow.AddMinutes(-25);   // network error: try again in 5 min
            return null;
        }
    }
}
