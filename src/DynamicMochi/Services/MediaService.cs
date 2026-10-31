using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DynamicMochi.Services;

/// <summary>Snapshot of the current track for the UI.</summary>
public sealed record TrackSnapshot(
    string Title,
    string Artist,
    bool IsPlaying,
    double PositionSec,
    double DurationSec,
    DateTime PositionStampUtc,
    byte[]? Thumbnail,
    string Source,
    string AppId,
    int ArtVersion = 0)
{
    public string Key => Title + "|" + Artist;

    /// <summary>Estimated position: last reported value plus elapsed time while playing.</summary>
    public double PositionAt(DateTime utcNow)
    {
        double p = PositionSec;
        if (IsPlaying) p += (utcNow - PositionStampUtc).TotalSeconds;
        if (DurationSec > 0) p = Math.Min(p, DurationSec);
        return Math.Max(0, p);
    }
}

/// <summary>
/// Now playing from Global System Media Transport Controls (SMTC).
/// One session is "shown"; the cover, the title and every button use that same session (SPEC-v1.4 §1, §5):
/// the playing session wins, among several playing ones the one that changed track last.
/// </summary>
public sealed class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _shown;
    private string _shownId = "";

    private readonly object _gate = new();
    private readonly Dictionary<GlobalSystemMediaTransportControlsSession, DateTime> _changedAt = new();
    private readonly HashSet<GlobalSystemMediaTransportControlsSession> _hooked = new();
    private volatile bool _propsDirty = true;

    // artwork of the shown track
    private string _trackKey = "";
    private DateTime _trackChangedUtc = DateTime.MinValue;
    private DateTime _thumbReadUtc = DateTime.MinValue;
    private string _thumbHash = "";
    private string _prevTrackHash = "";       // final cover hash of the previous track: a stale copy is not shown
    private byte[]? _thumb;
    private int _artVersion;
    private bool _itunesArt;
    private string _itunesFor = "";

    // display text of the shown track (browser titles parsed into artist and track)
    private string _title = "", _artist = "";

    public async Task InitAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (_, _) => { HookSessions(); _propsDirty = true; };
            HookSessions();
        }
        catch (Exception ex)
        {
            Log.Write("SMTC init failed: " + ex.Message);
        }
    }

    private void HookSessions()
    {
        if (_manager == null) return;
        try
        {
            foreach (var s in _manager.GetSessions())
            {
                lock (_gate)
                {
                    if (!_hooked.Add(s)) continue;
                    _changedAt[s] = DateTime.UtcNow;
                }
                var session = s;
                s.MediaPropertiesChanged += (_, _) =>
                {
                    lock (_gate) _changedAt[session] = DateTime.UtcNow;
                    _propsDirty = true;
                };
                s.PlaybackInfoChanged += (_, _) => _propsDirty = true;
            }
        }
        catch (Exception ex) { Log.Write("SMTC sessions failed: " + ex.Message); }
    }

    /// <summary>True when there is a shown session the buttons can talk to.</summary>
    public bool HasSession => _shown != null;

    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager == null) return null;
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> all;
        try { all = _manager.GetSessions(); }
        catch { return null; }
        if (all.Count == 0) return null;

        bool Playing(GlobalSystemMediaTransportControlsSession s)
        {
            try { return s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
            catch { return false; }
        }
        DateTime Changed(GlobalSystemMediaTransportControlsSession s)
        {
            lock (_gate) return _changedAt.TryGetValue(s, out var t) ? t : DateTime.MinValue;
        }

        var playing = all.Where(Playing).ToList();
        if (playing.Count > 0)
        {
            // keep the shown one while it plays, unless another one changed track later
            var latest = playing.OrderByDescending(Changed).First();
            if (_shown != null && playing.Contains(_shown) && Changed(_shown) >= Changed(latest)) return _shown;
            return latest;
        }
        // nothing plays: stay on the shown (paused) session while it exists
        if (_shown != null && all.Contains(_shown)) return _shown;
        try { return _manager.GetCurrentSession() ?? all[0]; }
        catch { return all[0]; }
    }

    public async Task<TrackSnapshot?> ReadAsync()
    {
        var session = PickSession();
        if (session != _shown)
        {
            _shown = session;
            _shownId = session?.SourceAppUserModelId ?? "";
            _propsDirty = true;
            Log.Write("media: shown session = " + (_shownId.Length > 0 ? _shownId : "none"));
        }
        if (session == null) return null;

        GlobalSystemMediaTransportControlsSessionMediaProperties? props;
        try
        {
            props = await session.TryGetMediaPropertiesAsync();
        }
        catch
        {
            return null;
        }
        if (props == null || string.IsNullOrWhiteSpace(props.Title)) return null;

        GlobalSystemMediaTransportControlsSessionPlaybackInfo? playback = null;
        try { playback = session.GetPlaybackInfo(); } catch { }
        bool playing = playback != null &&
                       playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        var timeline = session.GetTimelineProperties();
        double position = timeline.Position.TotalSeconds;
        double duration = (timeline.EndTime - timeline.StartTime).TotalSeconds;
        DateTime stamp = timeline.LastUpdatedTime.UtcDateTime;
        // some apps don't set LastUpdatedTime
        if (stamp.Year < 2000 || stamp > DateTime.UtcNow) stamp = DateTime.UtcNow;

        string appId = session.SourceAppUserModelId ?? "";
        string source = SourceName(appId);
        string rawKey = appId + "|" + props.Title + "|" + props.Artist;
        var now = DateTime.UtcNow;

        if (rawKey != _trackKey)
        {
            if (_trackKey.Length > 0) _prevTrackHash = _thumbHash;
            _trackKey = rawKey;
            _trackChangedUtc = now;
            _thumbReadUtc = DateTime.MinValue;
            _thumbHash = "";
            _thumb = null;
            _itunesArt = false;
            _itunesFor = "";
            _artVersion++;
            (_title, _artist) = DisplayText(props.Title, props.Artist ?? "", appId);
            Log.Write($"media: track \"{props.Title}\" / \"{props.Artist}\" from {source} → shown as \"{_title}\" / \"{_artist}\"");
        }

        // cover: every properties change, and every 500 ms for 4 s after a track change (players send it late)
        bool settling = now - _trackChangedUtc < TimeSpan.FromSeconds(4);
        if (!_itunesArt && (_propsDirty || (settling && now - _thumbReadUtc >= TimeSpan.FromMilliseconds(500)) || _thumbReadUtc == DateTime.MinValue))
        {
            _propsDirty = false;
            _thumbReadUtc = now;
            var bytes = await ReadThumbnailAsync(props.Thumbnail);
            string hash = bytes == null ? "" : Convert.ToHexString(SHA1.HashData(bytes));
            if (hash.Length > 0 && hash == _prevTrackHash && settling)
            {
                // the player still hands out the previous cover: show the placeholder, not the wrong picture
                bytes = null;
                hash = "";
            }
            if (hash != _thumbHash)
            {
                _thumbHash = hash;
                _thumb = bytes;
                _artVersion++;
                Log.Write(bytes == null ? "media: no thumbnail yet" : $"media: thumbnail {bytes.Length / 1024} KB ({(IsSquare(bytes) ? "square" : "not square")})");
            }
        }

        // browsers: look for the real album cover (YouTube gives a video frame)
        if (ArtworkService.IsBrowser(appId) && _itunesFor != _trackKey && !settling)
        {
            _itunesFor = _trackKey;
            bool needs = _thumb == null || !IsSquare(_thumb) || ArtworkService.ParseVideoTitle(props.Title) != null;
            if (needs)
            {
                string key = _trackKey, artist = _artist, track = _title;
                Log.Write($"media: searching iTunes for {artist} — {track}");
                _ = Task.Run(async () =>
                {
                    var art = await ArtworkService.FindAsync(artist, track);
                    if (art != null && key == _trackKey)
                    {
                        _thumb = art;
                        _thumbHash = Convert.ToHexString(SHA1.HashData(art));
                        _itunesArt = true;
                        _artVersion++;
                    }
                });
            }
        }

        return new TrackSnapshot(_title, _artist, playing, position, duration, stamp, _thumb, source, appId, _artVersion);
    }

    /// <summary>Browsers: "Artist - Track (Official Video)" → artist and track; the channel name is not an artist.</summary>
    private static (string Title, string Artist) DisplayText(string title, string artist, string appId)
    {
        if (!ArtworkService.IsBrowser(appId)) return (title, artist);
        if (ArtworkService.ParseVideoTitle(title) is { } parsed) return (parsed.Track, parsed.Artist);
        string clean = ArtworkService.CleanTail(title);
        string a = artist.EndsWith(" - Topic", StringComparison.Ordinal) ? artist[..^8] : artist;
        return (clean.Length > 0 ? clean : title, a);
    }

    private static bool IsSquare(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            double k = frame.PixelWidth / (double)Math.Max(1, frame.PixelHeight);
            return k > 0.9 && k < 1.1;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Human-readable player name from the SMTC source id; empty when unknown.</summary>
    private static string SourceName(string? appId)
    {
        if (string.IsNullOrEmpty(appId)) return "";
        string id = appId.ToLowerInvariant();
        if (id.Contains("spotify")) return "Spotify";
        if (id.Contains("chrome")) return "Chrome";
        if (id.Contains("msedge") || id.Contains("edge")) return "Edge";
        if (id.Contains("firefox")) return "Firefox";
        if (id.Contains("opera")) return "Opera";
        if (id.Contains("brave")) return "Brave";
        if (id.Contains("yandex")) return Loc.T("src.yandex");
        if (id.Contains("vlc")) return "VLC";
        if (id.Contains("zunemusic") || id.Contains("mediaplayer")) return Loc.T("src.player");
        if (id.Contains("itunes") || id.Contains("applemusic") || id.Contains("apple")) return "Apple Music";
        if (id.Contains("telegram")) return "Telegram";
        return "";
    }

    private static async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference? reference)
    {
        if (reference == null) return null;
        try
        {
            using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync();
            using Stream net = stream.AsStreamForRead();
            using var memory = new MemoryStream();
            await net.CopyToAsync(memory);
            return memory.Length > 0 ? memory.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }

    // ----- buttons: only ever the shown session (never GetCurrentSession, never another player)

    private async Task Do(string what, Func<GlobalSystemMediaTransportControlsSession, Task> action)
    {
        var s = _shown;
        if (s == null) { Log.Write($"media: {what} ignored, no shown session"); return; }
        try { await action(s); }
        catch (Exception ex) { Log.Write($"media: {what} failed for {_shownId}: {ex.Message}"); }
    }

    public Task PlayPauseAsync() => Do("play/pause", async s => await s.TryTogglePlayPauseAsync());
    public Task NextAsync() => Do("next", async s => await s.TrySkipNextAsync());
    public Task PreviousAsync() => Do("previous", async s => await s.TrySkipPreviousAsync());
    public Task SeekAsync(double seconds) =>
        Do("seek", async s => await s.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(Math.Max(0, seconds)).Ticks));
}
