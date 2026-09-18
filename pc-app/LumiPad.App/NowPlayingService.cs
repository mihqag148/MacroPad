using Windows.Media.Control;
using Windows.Storage.Streams;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using DrawingImaging = System.Drawing.Imaging;

namespace LumiPad.App;

public sealed record NowPlayingData(
    string Title,
    string Artist,
    TimeSpan Position,
    TimeSpan Duration,
    bool IsPlaying,
    byte[]? ArtworkRgb332);

public sealed class NowPlayingService : IDisposable
{
    private const int ArtworkSize = 76;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private CancellationTokenSource? _cts;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private bool _wasActive;

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

    private static bool IsAllowedSource(string sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
            return false;

        string id = sourceAppId.ToLowerInvariant();

        if (id.Contains("spotify") ||
            id.Contains("applemusic") ||
            id.Contains("apple music") ||
            id.Contains("zunemusic") ||
            id.Contains("music.ui") ||
            id.Contains("youtube"))
        {
            return true;
        }

        // YouTube / YouTube Music are surfaced by Windows as the browser
        // session. GSMTC does not expose the current tab URL, so browser
        // media sessions have to be allowed as a group.
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

                        string artworkKey =
                            $"{session.SourceAppUserModelId}\u001F{media.Title}\u001F" +
                            $"{media.Artist}\u001F{media.AlbumTitle}";

                        if (!string.Equals(
                                artworkKey,
                                _lastArtworkKey,
                                StringComparison.Ordinal))
                        {
                            _lastArtworkKey = artworkKey;
                            _cachedArtwork = await LoadArtworkAsync(media.Thumbnail);
                        }

                        _wasActive = true;
                        Updated?.Invoke(new NowPlayingData(
                            string.IsNullOrWhiteSpace(media.Title)
                                ? "Now Playing"
                                : media.Title,
                            string.IsNullOrWhiteSpace(media.Artist)
                                ? media.AlbumArtist ?? ""
                                : media.Artist,
                            position,
                            duration,
                            true,
                            _cachedArtwork));
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

    private static async Task<byte[]?> LoadArtworkAsync(
        RandomAccessStreamReference? thumbnail)
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
