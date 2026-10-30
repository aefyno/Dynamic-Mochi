using System.IO;
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
    byte[]? Thumbnail)
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
/// Reads now-playing info from Global System Media Transport Controls (SMTC).
/// Works with Spotify, browsers and most other players.
/// </summary>
public sealed class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private string _thumbKey = "";
    private byte[]? _thumb;

    public async Task InitAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex)
        {
            Log.Write("SMTC init failed: " + ex.Message);
        }
    }

    private GlobalSystemMediaTransportControlsSession? Session => _manager?.GetCurrentSession();

    public async Task<TrackSnapshot?> ReadAsync()
    {
        var session = Session;
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

        var playback = session.GetPlaybackInfo();
        bool playing = playback != null &&
                       playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        var timeline = session.GetTimelineProperties();
        double position = timeline.Position.TotalSeconds;
        double duration = (timeline.EndTime - timeline.StartTime).TotalSeconds;
        DateTime stamp = timeline.LastUpdatedTime.UtcDateTime;
        // some apps don't set LastUpdatedTime
        if (stamp.Year < 2000 || stamp > DateTime.UtcNow) stamp = DateTime.UtcNow;

        string key = props.Title + "|" + props.Artist;
        if (key != _thumbKey || _thumb == null)
        {
            _thumbKey = key;
            _thumb = await ReadThumbnailAsync(props.Thumbnail);
        }

        return new TrackSnapshot(props.Title, props.Artist ?? "", playing, position, duration, stamp, _thumb);
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

    public async Task PlayPauseAsync()
    {
        var s = Session;
        if (s != null) await s.TryTogglePlayPauseAsync();
    }

    public async Task NextAsync()
    {
        var s = Session;
        if (s != null) await s.TrySkipNextAsync();
    }

    public async Task PreviousAsync()
    {
        var s = Session;
        if (s != null) await s.TrySkipPreviousAsync();
    }

    public async Task SeekAsync(double seconds)
    {
        var s = Session;
        if (s != null) await s.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(Math.Max(0, seconds)).Ticks);
    }
}
