using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace DynamicMochi.Services;

public enum WeatherKind { Clear, PartlyCloudy, Cloudy, Fog, Drizzle, Rain, Snow, Thunder }

public sealed record HourWeather(DateTime Time, double Temp, int Code, int Precip, bool IsDay, double Wind);
public sealed record DayWeather(DateTime Date, int Code, double Min, double Max, double WindMax);

/// <summary>Wind values are in <see cref="WindUnit"/> (0 m/s, 1 km/h, 2 mph, 3 kn), as requested from Open-Meteo.</summary>
public sealed record WeatherData(
    DateTime FetchedUtc,
    double Temp, double Feels, int Code, double Wind, int Humidity, bool IsDay,
    double WindDir, double Gusts, int WindUnit,
    IReadOnlyList<HourWeather> Hours,
    IReadOnlyList<DayWeather> Days,
    DateTime PlaceNow,            // current local time at the place
    string Timezone);

/// <summary>Open-Meteo forecast (free, no key) with an on-disk cache in weather.json.</summary>
public static class WeatherService
{
    public static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicMochi/1.2 (github.com/aefyno/Dynamic-Mochi)");
        return c;
    }

    private static string CachePath => Path.Combine(Log.Folder, "weather.json");
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static readonly string[] UnitParam = { "ms", "kmh", "mph", "kn" };

    private static string Key(Place p, bool f, int unit) =>
        string.Format(Inv, "{0:F4},{1:F4},{2}{3},v4", p.Latitude, p.Longitude, f ? "F" : "C", UnitParam[Math.Clamp(unit, 0, 3)]);

    /// <summary>Wind in m/s from any unit (1 m/s = 3.6 km/h = 2.237 mph = 1.944 kn).</summary>
    public static double ToMs(double v, int unit) => unit switch { 1 => v / 3.6, 2 => v / 2.23694, 3 => v / 1.94384, _ => v };

    public static string UnitLabel(int unit) => Loc.T(unit switch { 1 => "unit.kmh", 2 => "unit.mph", 3 => "unit.kn", _ => "unit.ms" });

    /// <summary>"СЗ" for a direction the wind comes FROM (meteorological degrees).</summary>
    public static string Compass(double deg) => Loc.T("compass").Split(',')[(int)Math.Round(((deg % 360) + 360) % 360 / 45.0) % 8];

    /// <summary>Beaufort scale name for a speed in m/s.</summary>
    public static string Beaufort(double ms)
    {
        double[] limits = { 0.3, 1.6, 3.4, 5.5, 8.0, 10.8, 13.9, 17.2, 20.8, 24.5, 28.5, 32.7 };
        int b = 0;
        while (b < limits.Length && ms >= limits[b]) b++;
        var names = Loc.T("beaufort").Split(',');
        return names[Math.Min(b, names.Length - 1)];
    }

    private sealed class CacheEntry
    {
        public DateTime FetchedUtc { get; set; }
        public string Json { get; set; } = "";
    }

    private static Dictionary<string, CacheEntry> ReadCache()
    {
        try
        {
            if (File.Exists(CachePath))
                return JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(CachePath)) ?? new();
        }
        catch (Exception ex) { Log.Write("weather cache read failed: " + ex.Message); }
        return new();
    }

    /// <summary>Last cached forecast for a place, or null.</summary>
    public static WeatherData? Cached(Place p, bool fahrenheit, int unit)
    {
        var cache = ReadCache();
        if (!cache.TryGetValue(Key(p, fahrenheit, unit), out var e)) return null;
        try { return Parse(e.Json, e.FetchedUtc, unit); }
        catch { return null; }
    }

    public static async Task<WeatherData> FetchAsync(Place p, bool fahrenheit, int unit)
    {
        string url = string.Format(Inv,
            "https://api.open-meteo.com/v1/forecast?latitude={0:F4}&longitude={1:F4}" +
            "&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m,wind_direction_10m,wind_gusts_10m,relative_humidity_2m,is_day" +
            "&hourly=temperature_2m,weather_code,precipitation_probability,wind_speed_10m" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset,wind_speed_10m_max" +
            "&timezone=auto&forecast_days=7&wind_speed_unit={2}{3}",
            p.Latitude, p.Longitude, UnitParam[Math.Clamp(unit, 0, 3)], fahrenheit ? "&temperature_unit=fahrenheit" : "");
        string json = await Http.GetStringAsync(url);
        var now = DateTime.UtcNow;
        var data = Parse(json, now, unit);   // validate before caching

        try
        {
            var cache = ReadCache();
            cache.Remove(Key(p, fahrenheit, unit).Replace(",v4", ""));
            cache[Key(p, fahrenheit, unit)] = new CacheEntry { FetchedUtc = now, Json = json };
            Directory.CreateDirectory(Log.Folder);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(cache));
        }
        catch (Exception ex) { Log.Write("weather cache write failed: " + ex.Message); }
        return data;
    }

    private static WeatherData Parse(string json, DateTime fetchedUtc, int unit)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        int offset = root.TryGetProperty("utc_offset_seconds", out var off) ? off.GetInt32() : 0;
        string tz = root.TryGetProperty("timezone", out var tzEl) ? tzEl.GetString() ?? "" : "";
        DateTime placeNow = DateTime.UtcNow.AddSeconds(offset);

        var cur = root.GetProperty("current");
        double temp = cur.GetProperty("temperature_2m").GetDouble();
        double feels = cur.GetProperty("apparent_temperature").GetDouble();
        int code = cur.GetProperty("weather_code").GetInt32();
        double wind = cur.GetProperty("wind_speed_10m").GetDouble();
        double windDir = cur.TryGetProperty("wind_direction_10m", out var wd) && wd.ValueKind == JsonValueKind.Number ? wd.GetDouble() : 0;
        double gusts = cur.TryGetProperty("wind_gusts_10m", out var wg) && wg.ValueKind == JsonValueKind.Number ? wg.GetDouble() : wind;
        int hum = (int)Math.Round(cur.GetProperty("relative_humidity_2m").GetDouble());
        bool isDay = cur.GetProperty("is_day").GetInt32() == 1;

        var daily = root.GetProperty("daily");
        var dTimes = daily.GetProperty("time").EnumerateArray().Select(x => Date(x.GetString())).ToList();
        var dCodes = Ints(daily.GetProperty("weather_code"));
        var dMax = Doubles(daily.GetProperty("temperature_2m_max"));
        var dMin = Doubles(daily.GetProperty("temperature_2m_min"));
        var sunrise = daily.GetProperty("sunrise").EnumerateArray().Select(x => Date(x.GetString())).ToList();
        var sunset = daily.GetProperty("sunset").EnumerateArray().Select(x => Date(x.GetString())).ToList();
        var dWind = daily.TryGetProperty("wind_speed_10m_max", out var dw) ? Doubles(dw) : new List<double>();
        var days = new List<DayWeather>();
        for (int i = 0; i < dTimes.Count; i++)
            days.Add(new DayWeather(dTimes[i].Date, At(dCodes, i), At(dMin, i), At(dMax, i), At(dWind, i)));

        var hourly = root.GetProperty("hourly");
        var hTimes = hourly.GetProperty("time").EnumerateArray().Select(x => Date(x.GetString())).ToList();
        var hTemp = Doubles(hourly.GetProperty("temperature_2m"));
        var hCode = Ints(hourly.GetProperty("weather_code"));
        var hPrecip = Ints(hourly.GetProperty("precipitation_probability"));
        var hWind = hourly.TryGetProperty("wind_speed_10m", out var hw) ? Doubles(hw) : new List<double>();
        var hours = new List<HourWeather>();
        var startHour = new DateTime(placeNow.Year, placeNow.Month, placeNow.Day, placeNow.Hour, 0, 0);
        for (int i = 0; i < hTimes.Count && hours.Count < 12; i++)
        {
            if (hTimes[i] < startHour) continue;
            int di = dTimes.FindIndex(d => d.Date == hTimes[i].Date);
            bool day = di < 0 || di >= sunrise.Count || (hTimes[i] >= sunrise[di].AddMinutes(-30) && hTimes[i] < sunset[di]);
            hours.Add(new HourWeather(hTimes[i], At(hTemp, i), At(hCode, i), At(hPrecip, i), day, At(hWind, i)));
        }

        return new WeatherData(fetchedUtc, temp, feels, code, wind, hum, isDay, windDir, gusts, unit, hours, days, placeNow, tz);
    }

    private static DateTime Date(string? s) =>
        DateTime.TryParse(s, Inv, DateTimeStyles.None, out var d) ? d : DateTime.MinValue;

    private static List<double> Doubles(JsonElement a) =>
        a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0).ToList();

    private static List<int> Ints(JsonElement a) =>
        a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? (int)Math.Round(x.GetDouble()) : 0).ToList();

    private static T At<T>(List<T> list, int i) => i < list.Count ? list[i] : default!;

    // ---------------- WMO weather codes

    public static WeatherKind Kind(int code) => code switch
    {
        0 => WeatherKind.Clear,
        1 or 2 => WeatherKind.PartlyCloudy,
        3 => WeatherKind.Cloudy,
        45 or 48 => WeatherKind.Fog,
        51 or 53 or 55 or 56 or 57 => WeatherKind.Drizzle,
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => WeatherKind.Rain,
        71 or 73 or 75 or 77 or 85 or 86 => WeatherKind.Snow,
        95 or 96 or 99 => WeatherKind.Thunder,
        _ => WeatherKind.Cloudy,
    };

    private static readonly int[] KnownCodes = { 0, 1, 2, 3, 45, 48, 51, 53, 55, 56, 57, 61, 63, 65, 66, 67, 71, 73, 75, 77, 80, 81, 82, 85, 86, 95, 96, 99 };

    public static string Describe(int code) => Loc.T(KnownCodes.Contains(code) ? "wmo." + code : "wmo.3");

    public static string Temp(double t) =>
        (Math.Round(t) > 0 ? "+" : "") + Math.Round(t).ToString("0", Inv) + "°";
}
