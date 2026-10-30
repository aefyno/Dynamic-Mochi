using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace DynamicMochi.Services;

/// <summary>
/// Volume of the player that is currently playing (its Windows audio sessions, matched by process name
/// against the SMTC app id). Falls back to the system master volume when the player cannot be matched
/// (for example Firefox, whose SMTC id is a hash).
/// </summary>
public sealed class VolumeService
{
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private string _appId = "";
    private readonly List<AudioSessionControl> _sessions = new();
    private DateTime _lookedUp = DateTime.MinValue;

    /// <summary>True when the volume belongs to the player, false when it is the system volume.</summary>
    public bool IsApp => _sessions.Count > 0;

    private MMDevice? Device
    {
        get
        {
            try
            {
                _enumerator ??= new MMDeviceEnumerator();
                _device ??= _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return _device;
            }
            catch (Exception ex)
            {
                Log.Write("volume: no audio device: " + ex.Message);
                return null;
            }
        }
    }

    /// <summary>Re-match sessions for the player; cheap enough to call every couple of seconds.</summary>
    public void Track(string appId, bool force = false)
    {
        if (!force && appId == _appId && DateTime.UtcNow - _lookedUp < TimeSpan.FromSeconds(3)) return;
        _appId = appId;
        _lookedUp = DateTime.UtcNow;
        _sessions.Clear();
        _device = null;   // the default output may have changed (headphones plugged in)
        var dev = Device;
        if (dev == null || string.IsNullOrEmpty(appId)) return;
        string id = appId.ToLowerInvariant();
        try
        {
            var manager = dev.AudioSessionManager;
            manager.RefreshSessions();
            var all = manager.Sessions;
            for (int i = 0; i < all.Count; i++)
            {
                var s = all[i];
                if (s.IsSystemSoundsSession) continue;
                string name;
                try { name = Process.GetProcessById((int)s.GetProcessID).ProcessName.ToLowerInvariant(); }
                catch { continue; }
                if (name.Length >= 3 && id.Contains(name)) _sessions.Add(s);
            }
        }
        catch (Exception ex)
        {
            Log.Write("volume: session lookup failed: " + ex.Message);
        }
    }

    /// <summary>0..1</summary>
    public double Volume
    {
        get
        {
            try
            {
                if (_sessions.Count > 0) return _sessions[0].SimpleAudioVolume.Volume;
                return Device?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0;
            }
            catch { return 0; }
        }
        set
        {
            float v = (float)Math.Clamp(value, 0, 1);
            try
            {
                if (_sessions.Count > 0)
                    foreach (var s in _sessions) s.SimpleAudioVolume.Volume = v;
                else if (Device is { } d) d.AudioEndpointVolume.MasterVolumeLevelScalar = v;
            }
            catch (Exception ex) { Log.Write("volume: set failed: " + ex.Message); }
        }
    }

    public bool Muted
    {
        get
        {
            try
            {
                if (_sessions.Count > 0) return _sessions[0].SimpleAudioVolume.Mute;
                return Device?.AudioEndpointVolume.Mute ?? false;
            }
            catch { return false; }
        }
        set
        {
            try
            {
                if (_sessions.Count > 0)
                    foreach (var s in _sessions) s.SimpleAudioVolume.Mute = value;
                else if (Device is { } d) d.AudioEndpointVolume.Mute = value;
            }
            catch (Exception ex) { Log.Write("volume: mute failed: " + ex.Message); }
        }
    }
}
