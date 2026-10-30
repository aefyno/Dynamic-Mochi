using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace DynamicMochi.Services;

public enum WeatherKind { Clear, PartlyCloudy, Cloudy, Fog, Drizzle, Rain, Snow, Thunder }

public sealed record HourWeather(DateTime Time, double Temp, int Code, int Precip, bool IsDay);
public sealed record DayWeather(DateTime Date, int Code, double Min, double Max);

public sealed record WeatherData(
    DateTime FetchedUtc,
    double Temp, double Feels, int Code, double Wind, int Humidity, bool IsDay,
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

    private static string Key(Place p, bool f, bool kmh) =>
        string.Format(Inv, "{0:F4},{1:F4},{2}{3}", p.Latitude, p.Longitude, f ? "F" : "C", kmh ? "k" : "m");

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
    public static WeatherData? Cached(Place p, bool fahrenheit, bool kmh)
    {
        var cache = ReadCache();
        if (!cache.TryGetValue(Key(p, fahrenheit, kmh), out var e)) return null;
        try { return Parse(e.Json, e.FetchedUtc); }
        catch { return null; }
    }

    public static async Task<WeatherData> FetchAsync(Place p, bool fahrenheit, bool kmh)
    {
        string url = string.Format(Inv,
            "https://api.open-meteo.com/v1/forecast?latitude={0:F4}&longitude={1:F4}" +
            "&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m,relative_humidity_2m,is_day" +
            "&hourly=temperature_2m,weather_code,precipitation_probability" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset" +
            "&timezone=auto&forecast_days=7&wind_speed_unit={2}{3}",
            p.Latitude, p.Longitude, kmh ? "kmh" : "ms", fahrenheit ? "&temperature_unit=fahrenheit" : "");
        string json = await Http.GetStringAsync(url);
        var now = DateTime.UtcNow;
        var data = Parse(json, now);   // validate before caching

        try
        {
            var cache = ReadCache();
            cache[Key(p, fahrenheit, kmh)] = new CacheEntry { FetchedUtc = now, Json = json };
            Directory.CreateDirectory(Log.Folder);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(cache));
        }
        catch (Exception ex) { Log.Write("weather cache write failed: " + ex.Message); }
        return data;
    }

    private static WeatherData Parse(string json, DateTime fetchedUtc)
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
        int hum = (int)Math.Round(cur.GetProperty("relative_humidity_2m").GetDouble());
        bool isDay = cur.GetProperty("is_day").GetInt32() == 1;

        var daily = root.GetProperty("daily");
        var dTimes = daily.GetProperty("time").EnumerateArray().Select(x => Date(x.GetString())).ToList();
        var dCodes = Ints(daily.GetProperty("weather_code"));
        var dMax = Doubles(daily.GetProperty("temperature_2m_max"));
        var dMin = Doubles(daily.GetProperty("temperature_2m_min"));
        var sunrise = daily.GetProperty("sunrise").EnumerateArray().Select(x => Date(x.GetString())).ToList();
        var sunset = daily.GetProperty("sunset").EnumerateArray().Select(x => Date(x.GetString())).ToList();
        var days = new List<DayWeather>();
        for (int i = 0; i < dTimes.Count; i++)
            days.Add(new DayWeather(dTimes[i].Date, At(dCodes, i), At(dMin, i), At(dMax, i)));

        var hourly = root.GetProperty("hourly");
        var hTimes = hourly.GetProperty("time").EnumerateArray().Select(x => Date(x.GetString())).ToList();
        var hTemp = Doubles(hourly.GetProperty("temperature_2m"));
        var hCode = Ints(hourly.GetProperty("weather_code"));
        var hPrecip = Ints(hourly.GetProperty("precipitation_probability"));
        var hours = new List<HourWeather>();
        var startHour = new DateTime(placeNow.Year, placeNow.Month, placeNow.Day, placeNow.Hour, 0, 0);
        for (int i = 0; i < hTimes.Count && hours.Count < 12; i++)
        {
            if (hTimes[i] < startHour) continue;
            int di = dTimes.FindIndex(d => d.Date == hTimes[i].Date);
            bool day = di < 0 || di >= sunrise.Count || (hTimes[i] >= sunrise[di].AddMinutes(-30) && hTimes[i] < sunset[di]);
            hours.Add(new HourWeather(hTimes[i], At(hTemp, i), At(hCode, i), At(hPrecip, i), day));
        }

        return new WeatherData(fetchedUtc, temp, feels, code, wind, hum, isDay, hours, days, placeNow, tz);
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

    public static string Describe(int code) => code switch
    {
        0 => "Ясно",
        1 => "Преимущественно ясно",
        2 => "Переменная облачность",
        3 => "Пасмурно",
        45 => "Туман",
        48 => "Изморозь и туман",
        51 => "Слабая морось",
        53 => "Морось",
        55 => "Сильная морось",
        56 or 57 => "Ледяная морось",
        61 => "Небольшой дождь",
        63 => "Дождь",
        65 => "Сильный дождь",
        66 or 67 => "Ледяной дождь",
        71 => "Небольшой снег",
        73 => "Снег",
        75 => "Сильный снег",
        77 => "Снежная крупа",
        80 => "Небольшой ливень",
        81 => "Ливень",
        82 => "Сильный ливень",
        85 => "Небольшой снегопад",
        86 => "Снегопад",
        95 => "Гроза",
        96 or 99 => "Гроза с градом",
        _ => "Облачно",
    };

    public static string Temp(double t) =>
        (Math.Round(t) > 0 ? "+" : "") + Math.Round(t).ToString("0", Inv) + "°";
}
