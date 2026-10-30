using System.IO;
using System.Text.Json;

namespace DynamicMochi.Services;

public sealed class CalendarEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    /// <summary>Local start time; for all-day events only the date matters.</summary>
    public DateTime Start { get; set; }
    public bool AllDay { get; set; }
    public string Color { get; set; } = "#0A84FF";
    /// <summary>Minutes before the start; null = no reminder, 0 = at the start.</summary>
    public int? ReminderMinutes { get; set; }

    /// <summary>All-day events remind relative to 09:00 of their day.</summary>
    public DateTime ReminderAt => (AllDay ? Start.Date.AddHours(9) : Start).AddMinutes(-(ReminderMinutes ?? 0));

    public CalendarEvent Copy() => (CalendarEvent)MemberwiseClone();
}

/// <summary>Source of calendar events. Local JSON now; Google / Outlook can be added later.</summary>
public interface ICalendarSource
{
    IReadOnlyList<CalendarEvent> Events { get; }
    void Save(CalendarEvent e);     // add or update by Id
    void Delete(string id);
}

/// <summary>Events in %APPDATA%\DynamicMochi\events.json.</summary>
public sealed class LocalCalendar : ICalendarSource
{
    private readonly List<CalendarEvent> _events = new();
    private static string FilePath => Path.Combine(Log.Folder, "events.json");

    public LocalCalendar()
    {
        try
        {
            if (File.Exists(FilePath))
                _events = JsonSerializer.Deserialize<List<CalendarEvent>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Write("calendar load failed: " + ex.Message);
        }
    }

    public IReadOnlyList<CalendarEvent> Events => _events;

    public void Save(CalendarEvent e)
    {
        int i = _events.FindIndex(x => x.Id == e.Id);
        if (i >= 0) _events[i] = e; else _events.Add(e);
        Persist();
    }

    public void Delete(string id)
    {
        _events.RemoveAll(x => x.Id == id);
        Persist();
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Log.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_events, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Write("calendar save failed: " + ex.Message);
        }
    }
}

public static class CalendarExtensions
{
    public static IEnumerable<CalendarEvent> On(this ICalendarSource src, DateTime day) =>
        src.Events.Where(e => e.Start.Date == day.Date).OrderBy(e => e.AllDay ? 0 : 1).ThenBy(e => e.Start);
}
