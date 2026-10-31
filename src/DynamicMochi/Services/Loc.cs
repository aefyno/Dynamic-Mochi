using System.Globalization;
using System.Resources;
using System.Windows;

namespace DynamicMochi.Services;

/// <summary>
/// Interface strings from Strings.ru.resx / Strings.en.resx (embedded in the exe).
/// XAML uses {DynamicResource T.key} (resources replaced on a language change, so it applies at once);
/// code uses Loc.T("key") and re-renders through <see cref="Changed"/>.
/// </summary>
public static class Loc
{
    private static readonly ResourceManager Ru = new("DynamicMochi.Strings_ru", typeof(Loc).Assembly);
    private static readonly ResourceManager En = new("DynamicMochi.Strings_en", typeof(Loc).Assembly);

    public static bool English { get; private set; }

    /// <summary>Culture for dates and numbers: ru-RU or en-US.</summary>
    public static CultureInfo Culture { get; private set; } = new("ru-RU");

    public static event Action? Changed;

    /// <summary>"system" follows the Windows UI language: Russian (and other Cyrillic locales) → Russian, everything else → English.</summary>
    public static string Resolve(string setting)
    {
        if (setting is "ru" or "en") return setting;
        string lang = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;
        return lang is "ru" or "be" or "uk" or "kk" ? "ru" : "en";
    }

    public static void Apply(string setting)
    {
        English = Resolve(setting) == "en";
        Culture = new CultureInfo(English ? "en-US" : "ru-RU");
        var res = Application.Current?.Resources;
        if (res != null)
        {
            try
            {
                var set = (English ? En : Ru).GetResourceSet(CultureInfo.InvariantCulture, true, true);
                if (set != null)
                    foreach (System.Collections.DictionaryEntry e in set)
                        if (e.Key is string k && e.Value is string v) res["T." + k] = v;
            }
            catch (Exception ex) { Log.Write("strings failed: " + ex.Message); }
        }
        Changed?.Invoke();
    }

    public static string T(string key)
    {
        try
        {
            string? s = (English ? En : Ru).GetString(key, CultureInfo.InvariantCulture);
            if (s != null) return s;
            if (English) s = Ru.GetString(key, CultureInfo.InvariantCulture);
            return s ?? key;
        }
        catch
        {
            return key;
        }
    }

    public static string F(string key, params object[] args)
    {
        try { return string.Format(Culture, T(key), args); }
        catch { return T(key); }
    }

    /// <summary>First letter upper case in the current culture.</summary>
    public static string Cap(string s) => s.Length > 0 ? char.ToUpper(s[0], Culture) + s.Substring(1) : s;

    /// <summary>Clock text in 24 h or 12 h form (no AM/PM in the capsule, it is too wide).</summary>
    public static string Clock(DateTime t, bool h24) => t.ToString(h24 ? "HH:mm" : "h:mm", CultureInfo.InvariantCulture);
}
