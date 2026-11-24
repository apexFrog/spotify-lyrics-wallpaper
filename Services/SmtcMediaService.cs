using Windows.Media.Control;
using Windows.Storage.Streams;

namespace SpotifyLyricsWallpaper.Services;

public sealed record MediaPropsPayload(string Title, string Artist, string AlbumTitle);
public sealed record MediaTimelinePayload(double Position, double Duration);
public sealed record MediaPlaybackPayload(int State); // 0 = Stopped, 1 = Playing, 2 = Paused
public sealed record MediaThumbnailPayload(string Thumbnail);

/// <summary>
/// Integrates directly with Windows Global System Media Transport Controls (SMTC)
/// to deliver track metadata, album art, playback state, and high-precision timeline updates.
/// </summary>
public sealed class SmtcMediaService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private System.Threading.Timer? _timelineTimer;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private bool _disposed;

    public MediaPropsPayload LastProps { get; private set; } = new("", "", "");
    public MediaTimelinePayload LastTimeline { get; private set; } = new(0, 0);
    public MediaPlaybackPayload LastPlayback { get; private set; } = new(0);
    public MediaThumbnailPayload LastThumbnail { get; private set; } = new("");

    public event Action<MediaPropsPayload>? PropertiesChanged;
    public event Action<MediaTimelinePayload>? TimelineChanged;
    public event Action<MediaPlaybackPayload>? PlaybackChanged;
    public event Action<MediaThumbnailPayload>? ThumbnailChanged;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnSessionsUpdated;
            _manager.SessionsChanged += OnSessionsUpdated;
            await AttachBestSessionAsync();

            // Periodic 1s timeline sync so lyric timing stays rock-solid even when players throttle SMTC events
            _timelineTimer = new System.Threading.Timer(_ => PushTimelineSnapshot(), null, 1000, 1000);
        }
        catch
        {
            // SMTC unavailable
        }
    }

    private void OnSessionsUpdated(GlobalSystemMediaTransportControlsSessionManager sender, object args)
    {
        _ = AttachBestSessionAsync();
    }

    private async Task AttachBestSessionAsync()
    {
        if (_disposed || _manager == null) return;
        await _syncLock.WaitAsync();
        try
        {
            var chosen = PickPreferredSession(_manager);
            if (ReferenceEquals(chosen, _session) && chosen != null)
            {
                return;
            }

            DetachSession();
            _session = chosen;

            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;

                await PushAllFromSessionAsync(_session);
            }
            else
            {
                EmitEmpty();
            }
        }
        catch { }
        finally
        {
            _syncLock.Release();
        }
    }

    private static GlobalSystemMediaTransportControlsSession? PickPreferredSession(
        GlobalSystemMediaTransportControlsSessionManager manager)
    {
        var sessions = manager.GetSessions();
        if (sessions == null || sessions.Count == 0)
            return manager.GetCurrentSession();

        // 1. Prefer Spotify if it is currently playing
        foreach (var s in sessions)
        {
            if (s.SourceAppUserModelId.Contains("spotify", StringComparison.OrdinalIgnoreCase) &&
                s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                return s;
            }
        }

        // 2. Otherwise prefer any currently playing session
        foreach (var s in sessions)
        {
            if (s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                return s;
            }
        }

        // 3. Fallback to Windows current session
        return manager.GetCurrentSession() ?? sessions[0];
    }

    private void DetachSession()
    {
        if (_session == null) return;
        try
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }
        catch { }
        _session = null;
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        _ = PushMediaPropertiesAsync(sender);
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        PushPlaybackInfo(sender);
        PushTimelineSnapshot();
    }

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        PushTimelineSnapshot();
    }

    private async Task PushAllFromSessionAsync(GlobalSystemMediaTransportControlsSession session)
    {
        PushPlaybackInfo(session);
        PushTimelineSnapshot();
        await PushMediaPropertiesAsync(session);
    }

    private async Task PushMediaPropertiesAsync(GlobalSystemMediaTransportControlsSession session)
    {
        if (_disposed) return;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (props == null) return;

            var payload = new MediaPropsPayload(
                props.Title ?? "",
                props.Artist ?? "",
                props.AlbumTitle ?? "");

            LastProps = payload;
            PropertiesChanged?.Invoke(payload);

            // Load thumbnail asynchronously
            string dataUri = "";
            if (props.Thumbnail != null)
            {
                dataUri = await ReadThumbnailAsDataUriAsync(props.Thumbnail);
            }
            var thumbPayload = new MediaThumbnailPayload(dataUri);
            LastThumbnail = thumbPayload;
            ThumbnailChanged?.Invoke(thumbPayload);
        }
        catch { }
    }

    private void PushPlaybackInfo(GlobalSystemMediaTransportControlsSession session)
    {
        if (_disposed) return;
        try
        {
            var info = session.GetPlaybackInfo();
            int state = info?.PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => 1,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => 2,
                _ => 0
            };
            var payload = new MediaPlaybackPayload(state);
            LastPlayback = payload;
            PlaybackChanged?.Invoke(payload);
        }
        catch { }
    }

    private void PushTimelineSnapshot()
    {
        if (_disposed) return;
        var s = _session;
        if (s == null) return;

        try
        {
            var tl = s.GetTimelineProperties();
            if (tl == null) return;

            var info = s.GetPlaybackInfo();
            bool isPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            double duration = Math.Max(0, (tl.EndTime - tl.StartTime).TotalSeconds);
            double pos = (tl.Position - tl.StartTime).TotalSeconds;

            // Compensate for elapsed wall-clock time since LastUpdatedTime while playing
            if (isPlaying && tl.LastUpdatedTime != DateTimeOffset.MinValue)
            {
                double elapsed = (DateTimeOffset.UtcNow - tl.LastUpdatedTime).TotalSeconds;
                if (elapsed > 0 && elapsed < 3600)
                {
                    pos += elapsed;
                }
            }

            if (duration > 0) pos = Math.Clamp(pos, 0, duration);
            else pos = Math.Max(0, pos);

            var payload = new MediaTimelinePayload(pos, duration);
            LastTimeline = payload;
            TimelineChanged?.Invoke(payload);
        }
        catch { }
    }

    private void EmitEmpty()
    {
        LastProps = new("", "", "");
        LastPlayback = new(0);
        LastTimeline = new(0, 0);
        LastThumbnail = new("");

        PropertiesChanged?.Invoke(LastProps);
        PlaybackChanged?.Invoke(LastPlayback);
        TimelineChanged?.Invoke(LastTimeline);
        ThumbnailChanged?.Invoke(LastThumbnail);
    }

    private static async Task<string> ReadThumbnailAsDataUriAsync(IRandomAccessStreamReference thumbRef)
    {
        try
        {
            using var stream = await thumbRef.OpenReadAsync();
            if (stream == null || stream.Size == 0 || stream.Size > 10_000_000) return "";
            var bytes = new byte[stream.Size];
            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);

            string mime = string.IsNullOrWhiteSpace(stream.ContentType) ? "image/png" : stream.ContentType;
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return "";
        }
    }

    public async Task SendMediaCommandAsync(string action)
    {
        if (_disposed) return;
        try
        {
            var s = _session ?? (_manager != null ? PickPreferredSession(_manager) : null);
            if (s == null) return;

            switch (action)
            {
                case "prev":
                    await s.TrySkipPreviousAsync();
                    break;
                case "playpause":
                    await s.TryTogglePlayPauseAsync();
                    break;
                case "next":
                    await s.TrySkipNextAsync();
                    break;
            }
        }
        catch { }
    }

    public async Task SeekToSecondsAsync(double seconds)
    {
        if (_disposed) return;
        try
        {
            var s = _session ?? (_manager != null ? PickPreferredSession(_manager) : null);
            if (s == null) return;
            var tl = s.GetTimelineProperties();
            var startTicks = tl?.StartTime.Ticks ?? 0L;
            var endTicks = tl?.EndTime.Ticks ?? 0L;
            long targetTicks = startTicks + TimeSpan.FromSeconds(Math.Max(0, seconds)).Ticks;
            if (endTicks > startTicks)
                targetTicks = Math.Clamp(targetTicks, startTicks, endTicks);
            await s.TryChangePlaybackPositionAsync(targetTicks);
            PushTimelineSnapshot();
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timelineTimer?.Dispose();
        DetachSession();
        if (_manager != null)
        {
            _manager.CurrentSessionChanged -= OnSessionsUpdated;
            _manager.SessionsChanged -= OnSessionsUpdated;
        }
        _syncLock.Dispose();
    }
}
