using System.IO;
using System.Text.Json;

namespace DynamicMochi.Services;

public sealed class ClipItem
{
    public string Text { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.Now;
}

/// <summary>
/// v1.8: the last copied texts, newest first, in %APPDATA%\DynamicMochi\clipboard.json (this PC only).
/// Copying the same text again moves it to the top instead of adding a duplicate.
/// </summary>
public sealed class ClipHistory
{
    private const int MaxChars = 20_000;   // one item; longer copies are cut
    private readonly List<ClipItem> _items = new();
    private static string FilePath => Path.Combine(Log.Folder, "clipboard.json");

    public ClipHistory()
    {
        try
        {
            if (File.Exists(FilePath))
                _items = JsonSerializer.Deserialize<List<ClipItem>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) { Log.Write("clipboard history load failed: " + ex.Message); }
    }

    public IReadOnlyList<ClipItem> Items => _items;

    /// <summary>Adds a copied text; returns false when nothing changed (empty, or already on top).</summary>
    public bool Add(string text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Length > MaxChars) text = text[..MaxChars];
        if (_items.Count > 0 && _items[0].Text == text) return false;
        _items.RemoveAll(i => i.Text == text);
        _items.Insert(0, new ClipItem { Text = text, Time = DateTime.Now });
        Trim(limit);
        Persist();
        return true;
    }

    public void Trim(int limit)
    {
        if (_items.Count > limit) _items.RemoveRange(limit, _items.Count - limit);
    }

    public void Remove(ClipItem item)
    {
        _items.Remove(item);
        Persist();
    }

    public void Clear()
    {
        _items.Clear();
        Persist();
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Log.Folder);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { Log.Write("clipboard history save failed: " + ex.Message); }
    }
}
