using System.Globalization;
using System.Text.Json;
using Windows.Devices.Geolocation;

namespace DynamicMochi.Services;

/// <summary>A search result: what to show in the list and where it points.</summary>
public sealed record PlaceHit(string Title, string Subtitle, double Latitude, double Longitude, string Timezone, string Country);

/// <summary>
/// City search (Open-Meteo geocoding), district / street search and reverse geocoding (Nominatim, max 1 request per second),
/// and the device location (Windows Location API).
/// </summary>
public static class PlaceService
{
    /// <summary>Place names in the interface language.</summary>
    private static string Lang => Loc.English ? "en" : "ru";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly SemaphoreSlim NominatimGate = new(1, 1);
    private static DateTime _lastNominatim = DateTime.MinValue;

    public static async Task<List<PlaceHit>> SearchCityAsync(string query, CancellationToken ct)
    {
        string url = "https://geocoding-api.open-meteo.com/v1/search?count=10&language=" + Lang + "&name=" + Uri.EscapeDataString(query);
        string json = await WeatherService.Http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<PlaceHit>();
        if (!doc.RootElement.TryGetProperty("results", out var results)) return list;
        foreach (var r in results.EnumerateArray())
        {
            string name = Str(r, "name");
            string admin = Str(r, "admin1");
            string country = Str(r, "country");
            string sub = string.Join(", ", new[] { admin, country }.Where(x => x.Length > 0 && x != name));
            list.Add(new PlaceHit(name, sub, r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(),
                Str(r, "timezone") is { Length: > 0 } tz ? tz : "auto", country));
        }
        return list;
    }

    /// <summary>District or street inside a chosen city.</summary>
    public static async Task<List<PlaceHit>> SearchDistrictAsync(string query, Place city, CancellationToken ct)
    {
        string cityName = city.Name.Split(',').Last().Trim();
        string q = string.Join(", ", new[] { query, cityName, city.Country }.Where(x => !string.IsNullOrWhiteSpace(x)));
        string url = "https://nominatim.openstreetmap.org/search?format=jsonv2&addressdetails=1&namedetails=1&limit=8&accept-language=" + Lang + "&q=" + Uri.EscapeDataString(q);
        string json = await NominatimAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<PlaceHit>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            string name = RuName(r);
            if (name.Length == 0) name = Str(r, "display_name").Split(',')[0];
            string type = Str(r, "addresstype") switch
            {
                "suburb" or "city_district" or "district" or "borough" or "quarter" => Loc.T("place.district"),
                "neighbourhood" => Loc.T("place.micro"),
                "road" => Loc.T("place.street"),
                "village" or "hamlet" => Loc.T("place.village"),
                "town" or "city" => Loc.T("place.city"),
                _ => Loc.T("place.place"),
            };
            string where = r.TryGetProperty("address", out var addr) ? CityOf(addr) : cityName;
            list.Add(new PlaceHit(name, string.Join(", ", new[] { type, where }.Where(x => x.Length > 0)),
                double.Parse(Str(r, "lat"), Inv), double.Parse(Str(r, "lon"), Inv), city.Timezone, city.Country));
        }
        return list;
    }

    /// <summary>Free search for a district or street anywhere ("Строгино", "Тверская улица, Москва").</summary>
    public static async Task<List<PlaceHit>> SearchAnyAsync(string query, CancellationToken ct)
    {
        string url = "https://nominatim.openstreetmap.org/search?format=jsonv2&addressdetails=1&namedetails=1&limit=6&accept-language=" + Lang + "&q=" + Uri.EscapeDataString(query);
        string json = await NominatimAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<PlaceHit>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            string type = Str(r, "addresstype");
            if (type is "country" or "state" or "region") continue;
            string name = RuName(r);
            if (name.Length == 0) name = Str(r, "display_name").Split(',')[0];
            string city = "", country = "";
            if (r.TryGetProperty("address", out var a)) { city = CityOf(a); country = Str(a, "country"); }
            if (city == name) city = "";
            string kind = type switch
            {
                "suburb" or "city_district" or "district" or "borough" or "quarter" => Loc.T("place.district"),
                "neighbourhood" => Loc.T("place.micro"),
                "road" => Loc.T("place.street"),
                "city" or "town" => Loc.T("place.city"),
                "village" or "hamlet" => Loc.T("place.village"),
                _ => Loc.T("place.place"),
            };
            list.Add(new PlaceHit(name, string.Join(", ", new[] { kind, city, country }.Where(x => x.Length > 0)),
                double.Parse(Str(r, "lat"), Inv), double.Parse(Str(r, "lon"), Inv), "auto", country));
        }
        return list;
    }

    /// <summary>Name of a point: "Строгино, Москва".</summary>
    public static async Task<(string Name, string Country)> ReverseAsync(double lat, double lon, CancellationToken ct)
    {
        string url = string.Format(Inv,
            "https://nominatim.openstreetmap.org/reverse?format=jsonv2&addressdetails=1&namedetails=1&zoom=14&accept-language={2}&lat={0:F5}&lon={1:F5}", lat, lon, Lang);
        string json = await NominatimAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (!r.TryGetProperty("address", out var a)) return (RuName(r), "");
        string district = First(a, "suburb", "city_district", "quarter", "neighbourhood", "village", "hamlet");
        string city = CityOf(a);
        string name = string.Join(", ", new[] { district, city }.Where(x => x.Length > 0).Distinct());
        return (name.Length > 0 ? name : RuName(r), Str(a, "country"));
    }

    /// <summary>Respects the Nominatim usage policy: one request per second, identifying User-Agent (set on the shared client).</summary>
    private static async Task<string> NominatimAsync(string url, CancellationToken ct)
    {
        await NominatimGate.WaitAsync(ct);
        try
        {
            var wait = _lastNominatim.AddSeconds(1.05) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastNominatim = DateTime.UtcNow;
            return await WeatherService.Http.GetStringAsync(url, ct);
        }
        finally
        {
            NominatimGate.Release();
        }
    }

    public enum LocateResult { Ok, Denied, Failed }

    public static async Task<(LocateResult Result, double Lat, double Lon)> LocateAsync()
    {
        try
        {
            var access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed) return (LocateResult.Denied, 0, 0);
            var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
            var pos = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(15));
            var p = pos.Coordinate.Point.Position;
            return (LocateResult.Ok, p.Latitude, p.Longitude);
        }
        catch (UnauthorizedAccessException)
        {
            return (LocateResult.Denied, 0, 0);
        }
        catch (Exception ex)
        {
            Log.Write("location failed: " + ex.Message);
            return (LocateResult.Failed, 0, 0);
        }
    }

    /// <summary>Russian name when OpenStreetMap has one ("name:ru"), otherwise the name as it came.</summary>
    private static string RuName(JsonElement r)
    {
        if (r.TryGetProperty("namedetails", out var nd) && nd.ValueKind == JsonValueKind.Object)
        {
            if (Str(nd, "name:" + Lang) is { Length: > 0 } ru) return ru;
        }
        return Str(r, "name");
    }

    private static string CityOf(JsonElement a) => First(a, "city", "town", "village", "municipality", "state");

    private static string First(JsonElement a, params string[] keys)
    {
        foreach (var k in keys)
            if (Str(a, k) is { Length: > 0 } v) return v;
        return "";
    }

    private static string Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
