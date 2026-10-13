using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace SpotifyLyric;

public sealed record TrackInfo(string Title, string Artist, string Album, double DurationSec)
{
    public string Key => $"{Title}|{Artist}";
}

public sealed class MediaSnapshot
{
    public required TrackInfo Track { get; init; }
    public required bool IsPlaying { get; init; }
    public required TimeSpan Position { get; init; }
    public required DateTimeOffset SampledAt { get; init; }

    /// <summary>Position extrapolated to now.</summary>
    public TimeSpan CurrentPosition =>
        IsPlaying ? Position + (DateTimeOffset.Now - SampledAt) : Position;
}

/// <summary>
/// Reads Spotify's state through Windows' media session API (the same data shown
/// in the volume flyout), so no Spotify login / API key is needed.
/// </summary>
public sealed class MediaWatcher
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    public async Task<MediaSnapshot?> PollAsync()
    {
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

        var session = _manager.GetSessions().FirstOrDefault(s =>
            s.SourceAppUserModelId?.IndexOf("spotify", StringComparison.OrdinalIgnoreCase) >= 0);
        if (session == null) return null;

        var props = await session.TryGetMediaPropertiesAsync();
        if (props == null || string.IsNullOrWhiteSpace(props.Title)) return null;

        var timeline = session.GetTimelineProperties();
        var playback = session.GetPlaybackInfo();
        bool playing = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        var now = DateTimeOffset.Now;
        var position = timeline.Position;
        if (playing && timeline.LastUpdatedTime.Year > 2000)
        {
            var elapsed = now - timeline.LastUpdatedTime;
            if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromHours(1))
                position += elapsed;
        }
        var end = timeline.EndTime;
        if (end > TimeSpan.Zero && position > end) position = end;

        return new MediaSnapshot
        {
            Track = new TrackInfo(props.Title, props.Artist ?? "", props.AlbumTitle ?? "", end.TotalSeconds),
            IsPlaying = playing,
            Position = position,
            SampledAt = now,
        };
    }
}
