using System.IO;
using System.Text.Json;

namespace DynamicMochi.Services;

public sealed class TaskItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime? RemindAt { get; set; }
    public bool Done { get; set; }
    public DateTime? DoneAt { get; set; }
}

/// <summary>To-do list in %APPDATA%\DynamicMochi\tasks.json.</summary>
public sealed class TaskStore
{
    private readonly List<TaskItem> _items = new();
    private static string FilePath => Path.Combine(Log.Folder, "tasks.json");

    public TaskStore()
    {
        try
        {
            if (File.Exists(FilePath))
                _items = JsonSerializer.Deserialize<List<TaskItem>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) { Log.Write("tasks load failed: " + ex.Message); }
    }

    public IReadOnlyList<TaskItem> Items => _items;

    public void Save(TaskItem t)
    {
        int i = _items.FindIndex(x => x.Id == t.Id);
        if (i >= 0) _items[i] = t; else _items.Add(t);
        Persist();
    }

    public void Delete(string id)
    {
        _items.RemoveAll(x => x.Id == id);
        Persist();
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Log.Folder);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
        }
        catch (Exception ex) { Log.Write("tasks save failed: " + ex.Message); }
    }
}
