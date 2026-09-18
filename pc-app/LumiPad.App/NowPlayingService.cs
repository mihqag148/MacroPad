using Windows.Media.Control;

namespace LumiPad.App;

public sealed record NowPlayingData(
    string Title,
    string Artist,
    TimeSpan Position,
    TimeSpan Duration,
    bool IsPlaying);

public sealed class NowPlayingService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private CancellationTokenSource? _cts;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private bool _wasActive;

    public event Action<NowPlayingData>? Updated;
    public event Action? Cleared;

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _cts = new CancellationTokenSource();
        _ = PollLoopAsync(_cts.Token);
    }

    private static bool IsAllowedSource(string sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
            return false;

        string id = sourceAppId.ToLowerInvariant();

        // Dedicated music players.
        if (id.Contains("spotify") ||
            id.Contains("applemusic") ||
            id.Contains("apple music") ||
            id.Contains("zunemusic") ||
            id.Contains("music.ui") ||
            id.Contains("youtube"))
        {
            return true;
        }

        // YouTube / YouTube Music are normally surfaced through the browser's
        // Windows media session. Windows does not expose the tab URL through
        // GSMTC, so browser media sessions are allowed here.
        return id.Contains("chrome") ||
               id.Contains("msedge") ||
               id.Contains("firefox") ||
               id.Contains("brave") ||
               id.Contains("opera");
    }

    private void PublishCleared()
    {
        if (!_wasActive)
            return;

        _wasActive = false;
        Cleared?.Invoke();
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var session = _manager?.GetCurrentSession();

                if (session is null || !IsAllowedSource(session.SourceAppUserModelId))
                {
                    _currentSession = null;
                    PublishCleared();
                }
                else
                {
                    _currentSession = session;

                    var media = await session.TryGetMediaPropertiesAsync();
                    var timeline = session.GetTimelineProperties();
                    var playback = session.GetPlaybackInfo();

                    bool playing = playback.PlaybackStatus ==
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                    if (!playing)
                    {
                        PublishCleared();
                    }
                    else
                    {
                        var duration = timeline.EndTime - timeline.StartTime;
                        if (duration < TimeSpan.Zero)
                            duration = TimeSpan.Zero;

                        var position = timeline.Position - timeline.StartTime;
                        if (position < TimeSpan.Zero)
                            position = TimeSpan.Zero;
                        if (duration > TimeSpan.Zero && position > duration)
                            position = duration;

                        _wasActive = true;
                        Updated?.Invoke(new NowPlayingData(
                            string.IsNullOrWhiteSpace(media.Title) ? "Now Playing" : media.Title,
                            string.IsNullOrWhiteSpace(media.Artist)
                                ? media.AlbumArtist ?? ""
                                : media.Artist,
                            position,
                            duration,
                            true));
                    }
                }
            }
            catch
            {
                PublishCleared();
            }

            try
            {
                await Task.Delay(800, token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    public async Task TogglePlayPauseAsync()
    {
        if (_currentSession is not null)
            await _currentSession.TryTogglePlayPauseAsync();
    }

    public async Task PreviousAsync()
    {
        if (_currentSession is not null)
            await _currentSession.TrySkipPreviousAsync();
    }

    public async Task NextAsync()
    {
        if (_currentSession is not null)
            await _currentSession.TrySkipNextAsync();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
