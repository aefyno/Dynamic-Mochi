using Microsoft.Win32;

namespace DynamicMochi.Services;

/// <summary>
/// Start with Windows via HKCU\...\Run (no admin rights needed).
/// The Run value holds the exe path, so it is rewritten on every launch: a new build unpacked into another
/// folder would otherwise leave Windows starting an old or deleted exe while the toggle still looked "on".
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Task Manager → Startup apps writes here: first byte even = enabled, odd = switched off by the user
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string Name = "DynamicMochi";

    private static string? Command =>
        string.IsNullOrEmpty(Environment.ProcessPath) ? null : $"\"{Environment.ProcessPath}\"";

    /// <summary>Registered for the running exe and not switched off in Task Manager.</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                return key?.GetValue(Name) is string s
                       && string.Equals(s, Command, StringComparison.OrdinalIgnoreCase)
                       && !DisabledInWindows;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>True when the user switched the app off in Task Manager → Startup apps.</summary>
    public static bool DisabledInWindows
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey, false);
                return key?.GetValue(Name) is byte[] b && b.Length > 0 && (b[0] & 1) == 1;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Called on every launch while autostart is on: points the Run value at the running exe.</summary>
    public static void Refresh()
    {
        if (Command is not { } cmd) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (!string.Equals(key.GetValue(Name) as string, cmd, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(Name, cmd);
                Log.Write("autostart: registered " + cmd);
            }
            if (DisabledInWindows) Log.Write("autostart: switched off in Task Manager → Startup apps");
        }
        catch (Exception ex)
        {
            Log.Write("autostart: " + ex.Message);
        }
    }

    /// <summary>The settings toggle. Turning it on also clears an "off" mark left by Task Manager.</summary>
    public static void Set(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on)
            {
                if (Command is { } cmd) key.SetValue(Name, cmd);
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true);
                if (approved?.GetValue(Name) is not null) approved.DeleteValue(Name, false);
            }
            else
            {
                key.DeleteValue(Name, false);
            }
        }
        catch (Exception ex)
        {
            Log.Write("autostart: " + ex.Message);
        }
    }
}
