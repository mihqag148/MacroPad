using System.IO;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using DrawingImaging = System.Drawing.Imaging;

namespace LumiPad.App;

public sealed record NowPlayingData(
    string SourceName,
    string Title,
    string Artist,
    TimeSpan Position,
    TimeSpan Duration,
    bool IsPlaying,
    bool CanPrevious,
    bool CanPlayPause,
    bool CanNext,
    bool CanSeek,
    bool CanRepeat,
    bool CanShuffle,
    MediaPlaybackAutoRepeatMode RepeatMode,
    bool IsShuffleActive,
    byte[]? ArtworkRgb332);

public sealed class NowPlayingService : IDisposable
{
    private const int ArtworkSize = 76;
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private CancellationTokenSource? _cts;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private bool _wasActive;
    private bool _suppressedUntilPlaying;
    private DateTimeOffset? _inactiveSince;
    private NowPlayingData? _lastData;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private string _lastArtworkKey = "";
    private byte[]? _cachedArtwork;

    public event Action<NowPlayingData>? Updated;
    public event Action? Cleared;

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _cts = new CancellationTokenSource();
        _ = PollLoopAsync(_cts.Token);
    }

    private static bool IsBrowser(string id) =>
        id.Contains("chrome") ||
        id.Contains("msedge") ||
        id.Contains("firefox") ||
        id.Contains("brave") ||
        id.Contains("opera");

    private static bool IsAllowedSource(string sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
            return false;

        string id = sourceAppId.ToLowerInvariant();

        return id.Contains("spotify") ||
               id.Contains("applemusic") ||
               id.Contains("apple music") ||
               id.Contains("zunemusic") ||
               id.Contains("music.ui") ||
               id.Contains("youtube") ||
               IsBrowser(id);
    }

    private static string SourceLabel(string sourceAppId)
    {
        string id = (sourceAppId ?? "").ToLowerInvariant();

        if (id.Contains("spotify"))
            return "SPOTIFY";
        if (id.Contains("applemusic") || id.Contains("apple music"))
            return "APPLE MUSIC";
        if (id.Contains("youtube"))
            return "YOUTUBE";
        if (IsBrowser(id))
            return "YOUTUBE";
        if (id.Contains("zunemusic") || id.Contains("music.ui"))
            return "MUSIC";

        return "MUSIC";
    }

    private void PublishCleared(bool suppressUntilPlaying = false)
    {
        bool shouldNotify = _wasActive || _lastData is not null;

        _wasActive = false;
        _lastData = null;
        _suppressedUntilPlaying = suppressUntilPlaying;

        if (!suppressUntilPlaying)
            _inactiveSince = null;

        if (shouldNotify)
            Cleared?.Invoke();
    }

    private void PublishInactiveGrace(DateTimeOffset now)
    {
        if (!_wasActive || _lastData is null)
            return;

        _inactiveSince ??= now;

        if (now - _inactiveSince.Value >= StopGrace)
        {
            PublishCleared();
            return;
        }

        Updated?.Invoke(_lastData with { IsPlaying = false });
    }

    private async Task RefreshCurrentSessionAsync(
        DateTimeOffset now)
    {
        await _refreshGate.WaitAsync();
        try
        {
            var session = _manager?.GetCurrentSession();

            if (session is null ||
                !IsAllowedSource(session.SourceAppUserModelId))
            {
                _currentSession = null;
                PublishInactiveGrace(now);
                return;
            }

            _currentSession = session;

            var media = await session.TryGetMediaPropertiesAsync();
            var timeline = session.GetTimelineProperties();
            var playback = session.GetPlaybackInfo();

            bool playing =
                playback.PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var duration = timeline.EndTime - timeline.StartTime;
            if (duration < TimeSpan.Zero)
                duration = TimeSpan.Zero;

            var position = timeline.Position - timeline.StartTime;
            if (position < TimeSpan.Zero)
                position = TimeSpan.Zero;
            if (duration > TimeSpan.Zero && position > duration)
                position = duration;

            string artworkKey =
                $"{session.SourceAppUserModelId}\u001F{media.Title}\u001F" +
                $"{media.Artist}\u001F{media.AlbumTitle}";

            if (!string.Equals(
                    artworkKey,
                    _lastArtworkKey,
                    StringComparison.Ordinal))
            {
                _lastArtworkKey = artworkKey;
                _cachedArtwork =
                    await LoadArtworkAsync(media.Thumbnail);
            }

            var controls = playback.Controls;
            var data = new NowPlayingData(
                SourceLabel(session.SourceAppUserModelId),
                string.IsNullOrWhiteSpace(media.Title)
                    ? "Now Playing"
                    : media.Title,
                string.IsNullOrWhiteSpace(media.Artist)
                    ? media.AlbumArtist ?? ""
                    : media.Artist,
                position,
                duration,
                playing,
                controls.IsPreviousEnabled,
                controls.IsPlayPauseToggleEnabled ||
                    controls.IsPlayEnabled ||
                    controls.IsPauseEnabled,
                controls.IsNextEnabled,
                controls.IsPlaybackPositionEnabled,
                controls.IsRepeatEnabled,
                controls.IsShuffleEnabled,
                playback.AutoRepeatMode ??
                    MediaPlaybackAutoRepeatMode.None,
                playback.IsShuffleActive ?? false,
                _cachedArtwork);

            if (playing)
            {
                _suppressedUntilPlaying = false;
                _inactiveSince = null;
                _wasActive = true;
                _lastData = data;
                Updated?.Invoke(data);
                return;
            }

            // Pause/Stop may leave a Windows media session alive forever.
            // Keep Media visible for 10 seconds so Play can still be used,
            // then return both the app/device UI to the normal main screen.
            // Do not show the same paused session again until playback resumes.
            if (_suppressedUntilPlaying)
                return;

            _lastData = data;
            _wasActive = true;
            _inactiveSince ??= now;

            if (now - _inactiveSince.Value >= StopGrace)
            {
                PublishCleared(suppressUntilPlaying: true);
                return;
            }

            Updated?.Invoke(data);
        }
        catch
        {
            PublishInactiveGrace(now);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await RefreshCurrentSessionAsync(DateTimeOffset.UtcNow);

            try
            {
                await Task.Delay(200, token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private static async Task<byte[]?> LoadArtworkAsync(
        IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null)
            return null;

        try
        {
            using var stream = await thumbnail.OpenReadAsync();

            if (stream.Size == 0 || stream.Size > 8 * 1024 * 1024)
                return null;

            using var reader = new DataReader(stream.GetInputStreamAt(0));
            uint requested = (uint)stream.Size;
            uint loaded = await reader.LoadAsync(requested);

            if (loaded == 0)
                return null;

            var encoded = new byte[loaded];
            reader.ReadBytes(encoded);

            using var memory = new MemoryStream(encoded);
            using var source = new Drawing.Bitmap(memory);
            using var resized = new Drawing.Bitmap(
                ArtworkSize,
                ArtworkSize,
                DrawingImaging.PixelFormat.Format24bppRgb);

            using (var g = Drawing.Graphics.FromImage(resized))
            {
                g.Clear(Drawing.Color.Black);
                g.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;
                g.CompositingQuality = Drawing2D.CompositingQuality.HighQuality;
                g.SmoothingMode = Drawing2D.SmoothingMode.HighQuality;

                int side = Math.Min(source.Width, source.Height);
                int sx = (source.Width - side) / 2;
                int sy = (source.Height - side) / 2;

                g.DrawImage(
                    source,
                    new Drawing.Rectangle(0, 0, ArtworkSize, ArtworkSize),
                    new Drawing.Rectangle(sx, sy, side, side),
                    Drawing.GraphicsUnit.Pixel);
            }

            var rgb332 = new byte[ArtworkSize * ArtworkSize];

            for (int y = 0; y < ArtworkSize; y++)
            {
                for (int x = 0; x < ArtworkSize; x++)
                {
                    Drawing.Color p = resized.GetPixel(x, y);
                    rgb332[y * ArtworkSize + x] =
                        (byte)(((p.R >> 5) << 5) |
                               ((p.G >> 5) << 2) |
                               (p.B >> 6));
                }
            }

            return rgb332;
        }
        catch
        {
            return null;
        }
    }

    public async Task TogglePlayPauseAsync()
    {
        if (_currentSession is null)
            return;

        await _currentSession.TryTogglePlayPauseAsync();
        await Task.Delay(70);
        await RefreshCurrentSessionAsync(DateTimeOffset.UtcNow);
    }

    public async Task PreviousAsync()
    {
        if (_currentSession is null)
            return;

        await _currentSession.TrySkipPreviousAsync();
        await Task.Delay(120);
        await RefreshCurrentSessionAsync(DateTimeOffset.UtcNow);
    }

    public async Task NextAsync()
    {
        if (_currentSession is null)
            return;

        await _currentSession.TrySkipNextAsync();
        await Task.Delay(120);
        await RefreshCurrentSessionAsync(DateTimeOffset.UtcNow);
    }

    public async Task SeekAsync(TimeSpan relativePosition)
    {
        if (_currentSession is null)
            return;

        var playback = _currentSession.GetPlaybackInfo();
        if (!playback.Controls.IsPlaybackPositionEnabled)
            return;

        var timeline = _currentSession.GetTimelineProperties();
        TimeSpan duration = timeline.EndTime - timeline.StartTime;
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        if (relativePosition < TimeSpan.Zero)
            relativePosition = TimeSpan.Zero;
        if (duration > TimeSpan.Zero && relativePosition > duration)
            relativePosition = duration;

        long requestedTicks =
            (timeline.StartTime + relativePosition).Ticks;

        await _currentSession.TryChangePlaybackPositionAsync(
            requestedTicks);
        await Task.Delay(80);
        await RefreshCurrentSessionAsync(DateTimeOffset.UtcNow);
    }

    public async Task ToggleShuffleAsync()
    {
        if (_currentSession is null)
            return;

        var playback = _currentSession.GetPlaybackInfo();
        if (!playback.Controls.IsShuffleEnabled)
            return;

        bool next = !(playback.IsShuffleActive ?? false);
        await _currentSession.TryChangeShuffleActiveAsync(next);
        await Task.Delay(80);
        await RefreshCurrentSessionAsync(DateTimeOffset.UtcNow);
    }

    public async Task CycleRepeatModeAsync()
    {
        if (_currentSession is null)
            return;

        var playback = _currentSession.GetPlaybackInfo();
        if (!playback.Controls.IsRepeatEnabled)
            return;

        MediaPlaybackAutoRepeatMode current =
            playback.AutoRepeatMode ??
            MediaPlaybackAutoRepeatMode.None;

        MediaPlaybackAutoRepeatMode next = current switch
        {
            MediaPlaybackAutoRepeatMode.None =>
                MediaPlaybackAutoRepeatMode.Track,
            MediaPlaybackAutoRepeatMode.Track =>
                MediaPlaybackAutoRepeatMode.List,
            _ =>
                MediaPlaybackAutoRepeatMode.None
        };

        await _currentSession.TryChangeAutoRepeatModeAsync(next);
        await Task.Delay(80);
        await RefreshCurrentSessionAsync(DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
