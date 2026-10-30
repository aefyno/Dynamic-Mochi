using Microsoft.Win32;

namespace DynamicMochi.Services;

/// <summary>Start with Windows via HKCU\...\Run (no admin rights needed).</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "DynamicMochi";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(Name) is string;
        }
    }

    public static void Set(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (on)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) key.SetValue(Name, $"\"{exe}\"");
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
