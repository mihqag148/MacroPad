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

    public event Action<NowPlayingData>? Updated;
    public event Action? Cleared;

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _cts = new CancellationTokenSource();
        _ = PollLoopAsync(_cts.Token);
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                _currentSession = _manager?.GetCurrentSession();

                if (_currentSession is null)
                {
                    Cleared?.Invoke();
                }
                else
                {
                    var media = await _currentSession.TryGetMediaPropertiesAsync();
                    var timeline = _currentSession.GetTimelineProperties();
                    var playback = _currentSession.GetPlaybackInfo();

                    var duration = timeline.EndTime - timeline.StartTime;
                    if (duration < TimeSpan.Zero)
                        duration = TimeSpan.Zero;

                    var position = timeline.Position - timeline.StartTime;
                    if (position < TimeSpan.Zero)
                        position = TimeSpan.Zero;
                    if (duration > TimeSpan.Zero && position > duration)
                        position = duration;

                    Updated?.Invoke(new NowPlayingData(
                        string.IsNullOrWhiteSpace(media.Title) ? "Nothing Playing" : media.Title,
                        string.IsNullOrWhiteSpace(media.Artist) ? media.AlbumArtist ?? "" : media.Artist,
                        position,
                        duration,
                        playback.PlaybackStatus ==
                            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing));
                }
            }
            catch
            {
            }

            try
            {
                await Task.Delay(900, token);
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
