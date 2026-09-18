using System.Drawing.Imaging;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.Streams;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;

namespace LumiPad.App;

public sealed record ScreensaverAnimation(
    string FileName,
    int FrameIntervalMs,
    IReadOnlyList<byte[]> Frames);

public static class ScreensaverMediaService
{
    public const int Width = 64;
    public const int Height = 36;
    public const int MaxFrames = 8;

    public static async Task<ScreensaverAnimation> LoadAsync(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();

        return ext switch
        {
            ".gif" => await Task.Run(() => LoadGif(path)),
            ".mp4" or ".m4v" or ".mov" => await LoadVideoAsync(path),
            _ => throw new NotSupportedException("Choose a GIF or MP4/M4V/MOV file.")
        };
    }

    private static ScreensaverAnimation LoadGif(string path)
    {
        using var image = Drawing.Image.FromFile(path);
        var dimension = new FrameDimension(image.FrameDimensionsList[0]);
        int total = image.GetFrameCount(dimension);
        int count = Math.Min(MaxFrames, Math.Max(1, total));

        int delayMs = 180;
        try
        {
            var item = image.GetPropertyItem(0x5100);
            if (item?.Value is { Length: >= 4 })
            {
                int firstDelayCs = BitConverter.ToInt32(item.Value, 0);
                if (firstDelayCs > 0)
                    delayMs = Math.Clamp(firstDelayCs * 10, 80, 700);
            }
        }
        catch
        {
        }

        var frames = new List<byte[]>(count);

        for (int i = 0; i < count; i++)
        {
            int srcIndex = count == 1
                ? 0
                : (int)Math.Round(i * (total - 1.0) / (count - 1.0));

            image.SelectActiveFrame(dimension, srcIndex);

            using var bitmap = new Drawing.Bitmap(image);
            frames.Add(ToRgb332(bitmap));
        }

        return new ScreensaverAnimation(
            Path.GetFileName(path),
            delayMs,
            frames);
    }

    private static async Task<ScreensaverAnimation> LoadVideoAsync(string path)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        MediaClip clip = await MediaClip.CreateFromFileAsync(file);

        TimeSpan duration = clip.OriginalDuration;
        if (duration <= TimeSpan.Zero)
            throw new InvalidOperationException("Cannot read video duration.");

        int count = MaxFrames;
        double spanMs = Math.Min(duration.TotalMilliseconds, 6000.0);

        if (spanMs < 600.0)
            count = Math.Max(2, (int)Math.Ceiling(spanMs / 120.0));

        var frames = new List<byte[]>(count);

        for (int i = 0; i < count; i++)
        {
            double t = count == 1
                ? 0
                : (spanMs * i / count);

            using IRandomAccessStreamWithContentType thumb =
                await clip.GetThumbnailAsync(
                    TimeSpan.FromMilliseconds(t),
                    Width,
                    Height,
                    VideoFramePrecision.NearestFrame);

            byte[] encoded = await ReadStreamAsync(thumb);

            using var ms = new MemoryStream(encoded);
            using var bitmap = new Drawing.Bitmap(ms);
            frames.Add(ToRgb332(bitmap));
        }

        int intervalMs = Math.Clamp(
            (int)Math.Round(spanMs / count),
            100,
            700);

        return new ScreensaverAnimation(
            Path.GetFileName(path),
            intervalMs,
            frames);
    }

    private static async Task<byte[]> ReadStreamAsync(IRandomAccessStream stream)
    {
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        uint size = (uint)stream.Size;
        uint loaded = await reader.LoadAsync(size);
        var bytes = new byte[loaded];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static byte[] ToRgb332(Drawing.Bitmap source)
    {
        using var resized = new Drawing.Bitmap(
            Width,
            Height,
            PixelFormat.Format24bppRgb);

        using (var g = Drawing.Graphics.FromImage(resized))
        {
            g.Clear(Drawing.Color.Black);
            g.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;
            g.CompositingQuality = Drawing2D.CompositingQuality.HighQuality;
            g.SmoothingMode = Drawing2D.SmoothingMode.HighQuality;

            double scale = Math.Max(
                Width / (double)source.Width,
                Height / (double)source.Height);

            int drawW = (int)Math.Ceiling(source.Width * scale);
            int drawH = (int)Math.Ceiling(source.Height * scale);
            int dx = (Width - drawW) / 2;
            int dy = (Height - drawH) / 2;

            g.DrawImage(source, dx, dy, drawW, drawH);
        }

        var output = new byte[Width * Height];

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                Drawing.Color p = resized.GetPixel(x, y);
                output[y * Width + x] =
                    (byte)(((p.R >> 5) << 5) |
                           ((p.G >> 5) << 2) |
                           (p.B >> 6));
            }
        }

        return output;
    }
}
