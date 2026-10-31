using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicMochi.Services;

/// <summary>Lyric lines with optional start times in seconds.</summary>
public sealed record LyricsResult(IReadOnlyList<double>? Times, IReadOnlyList<string> Lines);

/// <summary>
/// Lyrics lookup via lrclib.net.
/// Matching: full title + partial artist, duration within ±8 s, sped up / slowed
/// versions get rescaled timings, missing intro is filled from plain lyrics.
/// </summary>
public static class LyricsService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicMochi/0.1 (personal app)");
        return c;
    }

    public static async Task<LyricsResult?> FindAsync(string title, string artist, double duration)
    {
        bool altered = IsAlteredVersion(title);
        string clean = CleanTitle(title);
        Log.Write($"lyrics: search \"{title}\" / \"{artist}\" / {duration:0}s");

        // 1) exact title + artist, plus free-text search
        var exact = await SearchAsync(("track_name", title), ("artist_name", artist));
        var free = await SearchAsync(("q", (clean.Length > 0 ? clean : title) + " " + artist));
        var found = Pick(exact.Concat(free).ToList(), title, artist, duration, allowScale: false, "exact");
        if (found != null || clean.Length == 0)
        {
            if (found == null) Log.Write("lyrics: not found");
            return found;
        }

        // 2) cleaned title (no "sped up", "feat."), with and without artist
        var byClean = await SearchAsync(("track_name", clean), ("artist_name", artist));
        var onlyTitle = await SearchAsync(("track_name", clean));
        found = Pick(byClean.Concat(onlyTitle).ToList(), title, artist, duration, altered, "clean");
        if (found == null) Log.Write("lyrics: not found");
        return found;
    }

    private static async Task<List<JsonElement>> SearchAsync(params (string Key, string Value)[] query)
    {
        var parts = query.Where(q => !string.IsNullOrWhiteSpace(q.Value))
                         .Select(q => q.Key + "=" + Uri.EscapeDataString(q.Value));
        string qs = string.Join("&", parts);
        if (qs.Length == 0) return new List<JsonElement>();
        try
        {
            string json = await Http.GetStringAsync("https://lrclib.net/api/search?" + qs);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return new List<JsonElement>();
            return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
        }
        catch (Exception ex)
        {
            Log.Write("lyrics: network error: " + ex.Message);
            throw;   // network error, caller retries
        }
    }

    // Picking the best result

    private static LyricsResult? Pick(List<JsonElement> all, string title, string artist, double duration, bool allowScale, string stage)
    {
        string want = Norm(CleanTitle(title));
        var items = all.Where(item =>
        {
            if (!SameSong(item, title)) return false;
            if (SameArtist(item, artist)) return true;
            double d = Num(item, "duration");
            return want.Length >= 12 && duration > 0 && d > 0 && Math.Abs(d - duration) < 3;
        }).ToList();

        // synced: matching duration, most complete, artist match first
        LyricsResult? best = null;
        string? bestPlain = null;
        string bestName = "";
        double bestScore = double.MinValue;
        foreach (var item in items)
        {
            string? lrc = Str(item, "syncedLyrics");
            if (string.IsNullOrEmpty(lrc)) continue;
            double d = Num(item, "duration");
            if (duration > 0 && d <= 0) continue;
            double diff = duration > 0 && d > 0 ? Math.Abs(d - duration) : 0;
            double ratio = duration > 0 && d > 0 ? duration / d : 1;
            bool ok = diff < 8 || (allowScale && ratio > 0.6 && ratio < 1.6);
            if (!ok) continue;
            var parsed = ParseLrc(lrc);
            if (parsed == null) continue;
            double r = diff < 8 ? 1 : ratio;
            var times = parsed.Value.Times.Select(t => t * r).ToList();
            double first = times.Count > 0 ? times[0] : 0;
            double score = parsed.Value.Lines.Count - diff * 3 - first * 0.3 + (SameArtist(item, artist) ? 100 : 0);
            if (score > bestScore)
            {
                bestScore = score;
                best = new LyricsResult(times, parsed.Value.Lines);
                bestPlain = Str(item, "plainLyrics");
                bestName = $"#{Num(item, "id"):0} \"{Str(item, "trackName")}\" / \"{Str(item, "artistName")}\" {d:0}s";
            }
        }
        if (best != null)
        {
            var times = best.Times!.ToList();
            var lines = best.Lines.ToList();
            int before = lines.Count;
            AddIntro(bestPlain, times, lines);
            Log.Write($"lyrics: {stage}: {all.Count} results, {items.Count} same song, picked {bestName}, {lines.Count} lines (intro +{lines.Count - before}), first at {times[0]:0}s");
            return new LyricsResult(times, lines);
        }

        // unsynced fallback: longest plain text
        List<string> plainBest = new();
        foreach (var item in items)
        {
            string? plain = Str(item, "plainLyrics");
            if (string.IsNullOrEmpty(plain)) continue;
            double d = Num(item, "duration");
            double diff = duration > 0 && d > 0 ? Math.Abs(d - duration) : 0;
            if (diff >= 8 && !allowScale) continue;
            var lines = SplitLines(plain);
            if (lines.Count > plainBest.Count) plainBest = lines;
        }
        if (plainBest.Count > 0)
        {
            Log.Write($"lyrics: {stage}: {all.Count} results, {items.Count} same song, no synced match, plain text {plainBest.Count} lines");
            return new LyricsResult(null, plainBest);
        }
        Log.Write($"lyrics: {stage}: {all.Count} results, {items.Count} same song, nothing usable");
        return null;
    }

    /// <summary>Spreads intro lines that exist only in the plain text evenly before the first timestamp.</summary>
    private static void AddIntro(string? plain, List<double> times, List<string> lines)
    {
        if (string.IsNullOrEmpty(plain) || times.Count == 0 || lines.Count == 0 || times[0] <= 5) return;
        var plainLines = SplitLines(plain);
        string key = Norm(lines[0]);
        if (key.Length == 0) return;
        int k = plainLines.FindIndex(l => Norm(l) == key);
        if (k <= 0) return;
        double start = 0.5, step = (times[0] - start) / k;
        times.InsertRange(0, Enumerable.Range(0, k).Select(i => start + i * step));
        lines.InsertRange(0, plainLines.Take(k));
    }

    // LRC parsing: "[01:23.45] line", a line may have several timestamps

    private static readonly Regex Stamp = new(@"^\[(\d+):(\d+(?:[.,]\d+)?)\]", RegexOptions.Compiled);

    private static (List<double> Times, List<string> Lines)? ParseLrc(string lrc)
    {
        var pairs = new List<(double T, string Text)>();
        foreach (var raw in lrc.Split('\n'))
        {
            string rest = raw.Trim();
            var stamps = new List<double>();
            Match m;
            while ((m = Stamp.Match(rest)).Success)
            {
                double min = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double sec = double.Parse(m.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                stamps.Add(min * 60 + sec);
                rest = rest.Substring(m.Length);
            }
            string text = rest.Trim();
            if (stamps.Count == 0 || text.Length == 0) continue;
            foreach (var t in stamps) pairs.Add((t, text));
        }
        if (pairs.Count == 0) return null;
        pairs.Sort((a, b) => a.T.CompareTo(b.T));
        return (pairs.Select(p => p.T).ToList(), pairs.Select(p => p.Text).ToList());
    }

    private static List<string> SplitLines(string text) =>
        text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !(l.StartsWith('[') && l.EndsWith(']')))   // skip section tags like [Verse]
            .ToList();

    // Title / artist matching

    public static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static string CleanTitle(string title)
    {
        string t = Regex.Replace(title, @"\s*[\(\[][^\)\]]*[\)\]]", "");
        int dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash >= 0) t = t.Substring(0, dash);
        return t.Trim();
    }

    private static bool IsAlteredVersion(string title)
    {
        string t = title.ToLowerInvariant();
        return new[] { "speed", "sped", "slowed", "nightcore", "ускор", "замедл", "reverb" }.Any(t.Contains);
    }

    internal static bool SameSong(JsonElement item, string title)
    {
        string want = Norm(CleanTitle(title));
        string got = Norm(CleanTitle(Str(item, "trackName") ?? ""));
        if (want.Length == 0 || got.Length == 0) return false;
        if (got == want) return true;
        // fuzzy match only for long titles; short ones collide too often
        return Math.Min(got.Length, want.Length) >= 10 && (got.Contains(want) || want.Contains(got));
    }

    internal static bool SameArtist(JsonElement item, string artist)
    {
        string got = Norm(Str(item, "artistName") ?? "");
        if (got.Length == 0) return false;
        string a = " " + artist.ToLowerInvariant() + " ";
        foreach (var sep in new[] { " feat. ", " ft. ", " x ", " и " }) a = a.Replace(sep, ",");
        var names = a.Split(new[] { ',', '&', '/', ';', '+' }, StringSplitOptions.RemoveEmptyEntries)
                     .Select(Norm)
                     .Where(n => n.Length >= 3);
        return names.Any(n => got.Contains(n) || n.Contains(got));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
