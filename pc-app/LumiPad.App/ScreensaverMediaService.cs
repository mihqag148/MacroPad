using System.IO;
using System.Drawing.Imaging;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.Streams;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;

namespace LumiPad.App;

public enum ScreensaverScaleMode
{
    Fill = 0,
    Fit = 1,
    Stretch = 2,
    Tile = 3,
    Center = 4,
    Span = 5,
}

public enum ScreensaverPixelFormat
{
    Rgb332 = 0,
    Rgb565 = 1,
}

public sealed record ScreensaverAnimation(
    string FileName,
    int Width,
    int Height,
    ScreensaverPixelFormat PixelFormat,
    int FrameIntervalMs,
    IReadOnlyList<int> FrameDurationsMs,
    IReadOnlyList<byte[]> Frames);

public static class ScreensaverMediaService
{
    public const int Width = 160;
    public const int Height = 86;
    public const int StaticWidth = 320;
    public const int StaticHeight = 172;
    public const int MaxFrames = 25;
    public const int MaxPlaybackFps = 25;
    public const int MinFrameIntervalMs = 1000 / MaxPlaybackFps;

    public static async Task<ScreensaverAnimation> LoadAsync(
        string path,
        ScreensaverScaleMode scaleMode)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();

        return ext switch
        {
            ".gif" => await Task.Run(() => LoadGif(path, scaleMode)),
            ".png" or ".jpg" or ".jpeg" or ".bmp" =>
                await Task.Run(() => LoadStaticImage(path, scaleMode)),
            _ => throw new NotSupportedException(
                "Choose a GIF or static PNG/JPG/BMP image.")
        };
    }

    private static ScreensaverAnimation LoadStaticImage(
        string path,
        ScreensaverScaleMode scaleMode)
    {
        using var bitmap = new Drawing.Bitmap(path);
        byte[] frame = ToRgb565(bitmap, scaleMode);

        // Static images use the panel's full 320x172 resolution and RGB565
        // depth. They are drawn once from flash, so there is no animation RAM
        // or bandwidth penalty for keeping the extra detail.
        return new ScreensaverAnimation(
            Path.GetFileName(path),
            StaticWidth,
            StaticHeight,
            ScreensaverPixelFormat.Rgb565,
            1000,
            new[] { 1000 },
            new[] { frame });
    }

    private static ScreensaverAnimation LoadGif(string path, ScreensaverScaleMode scaleMode)
    {
        using var image = Drawing.Image.FromFile(path);
        var dimension = new FrameDimension(image.FrameDimensionsList[0]);
        int total = image.GetFrameCount(dimension);

        int[] sourceDelaysMs = ReadGifFrameDelaysMs(image, total);
        int sourceLoopMs = Math.Max(1, sourceDelaysMs.Sum());

        // Keep the source loop duration, but never schedule more than 25 FPS
        // and never store more than MaxFrames. If the source is already within
        // both limits, preserve its exact per-frame timings. Otherwise sample
        // the source on its time axis so the animation speed stays unchanged.
        bool exactTimingFits =
            total <= MaxFrames &&
            sourceDelaysMs.All(delay => delay >= MinFrameIntervalMs);

        int maxFramesByRate =
            Math.Max(1, sourceLoopMs / MinFrameIntervalMs);
        int count = exactTimingFits
            ? Math.Max(1, total)
            : Math.Min(
                Math.Max(1, total),
                Math.Min(MaxFrames, maxFramesByRate));

        var frameDurations = new List<int>(count);
        var sourceIndices = new List<int>(count);

        if (exactTimingFits)
        {
            for (int i = 0; i < total; i++)
            {
                sourceIndices.Add(i);
                frameDurations.Add(sourceDelaysMs[i]);
            }
        }
        else
        {
            // A loop shorter than 40 ms cannot be represented at <=25 FPS.
            // In that edge case the shortest valid loop is one 40 ms frame.
            int outputLoopMs = Math.Max(
                sourceLoopMs,
                count * MinFrameIntervalMs);
            int baseDelay = outputLoopMs / count;
            int remainder = outputLoopMs % count;

            var cumulative = new int[total];
            int running = 0;
            for (int i = 0; i < total; i++)
            {
                running += sourceDelaysMs[i];
                cumulative[i] = running;
            }

            for (int i = 0; i < count; i++)
            {
                frameDurations.Add(baseDelay + (i < remainder ? 1 : 0));

                double sourceTime = i * (sourceLoopMs / (double)count);
                int srcIndex = 0;
                while (srcIndex < total - 1 && sourceTime >= cumulative[srcIndex])
                    srcIndex++;

                sourceIndices.Add(srcIndex);
            }
        }

        var frames = new List<byte[]>(count);

        foreach (int srcIndex in sourceIndices)
        {
            image.SelectActiveFrame(dimension, srcIndex);

            using var bitmap = new Drawing.Bitmap(
                image.Width,
                image.Height,
                PixelFormat.Format32bppArgb);

            using (var frameGraphics = Drawing.Graphics.FromImage(bitmap))
            {
                frameGraphics.Clear(Drawing.Color.Transparent);
                frameGraphics.DrawImageUnscaled(image, 0, 0);
            }

            frames.Add(ToRgb332(bitmap, scaleMode));
        }

        int averageDelayMs = Math.Max(
            MinFrameIntervalMs,
            (int)Math.Round(frameDurations.Average()));

        return new ScreensaverAnimation(
            Path.GetFileName(path),
            Width,
            Height,
            ScreensaverPixelFormat.Rgb332,
            averageDelayMs,
            frameDurations,
            frames);
    }

    private static int[] ReadGifFrameDelaysMs(
        Drawing.Image image,
        int frameCount)
    {
        const int PropertyTagFrameDelay = 0x5100; // 1/100 second per frame
        var delays = Enumerable.Repeat(100, Math.Max(1, frameCount)).ToArray();

        try
        {
            var item = image.GetPropertyItem(PropertyTagFrameDelay);
            if (item?.Value is { Length: >= 4 })
            {
                int entries = Math.Min(frameCount, item.Value.Length / 4);
                for (int i = 0; i < entries; i++)
                {
                    int delayCs = BitConverter.ToInt32(item.Value, i * 4);

                    // Preserve the GIF's real source timing. Very short
                    // source delays are handled by the 25 FPS resampler below;
                    // do not inflate 1 cs (10 ms) to 100 ms, which previously
                    // made smooth GIFs appear to be only ~10-11 FPS.
                    int sourceDelayMs =
                        Math.Max(1, delayCs) * 10;
                    delays[i] =
                        Math.Clamp(sourceDelayMs, 10, 5000);
                }
            }
        }
        catch
        {
        }

        return delays;
    }

    private static async Task<ScreensaverAnimation> LoadVideoAsync(string path, ScreensaverScaleMode scaleMode)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        MediaClip clip = await MediaClip.CreateFromFileAsync(file);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);

        TimeSpan duration = clip.OriginalDuration;
        if (duration <= TimeSpan.Zero)
            throw new InvalidOperationException("Cannot read video duration.");

        int count = MaxFrames;
        double spanMs = Math.Min(
            duration.TotalMilliseconds,
            MaxFrames * (1000.0 / MaxPlaybackFps));

        if (spanMs < 250.0)
            count = Math.Max(2, (int)Math.Ceiling(spanMs / (1000.0 / MaxPlaybackFps)));

        var frames = new List<byte[]>(count);

        for (int i = 0; i < count; i++)
        {
            double t = count == 1
                ? 0
                : (spanMs * i / count);

            using IRandomAccessStreamWithContentType thumb =
                await composition.GetThumbnailAsync(
                    TimeSpan.FromMilliseconds(t),
                    Width,
                    Height,
                    VideoFramePrecision.NearestFrame);

            byte[] encoded = await ReadStreamAsync(thumb);

            using var ms = new MemoryStream(encoded);
            using var bitmap = new Drawing.Bitmap(ms);
            frames.Add(ToRgb332(bitmap, scaleMode));
        }

        const int intervalMs = MinFrameIntervalMs;

        return new ScreensaverAnimation(
            Path.GetFileName(path),
            Width,
            Height,
            ScreensaverPixelFormat.Rgb332,
            intervalMs,
            Enumerable.Repeat(intervalMs, frames.Count).ToArray(),
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

    private static byte[] ToRgb565(
        Drawing.Bitmap source,
        ScreensaverScaleMode scaleMode)
    {
        using var resized = new Drawing.Bitmap(
            StaticWidth,
            StaticHeight,
            PixelFormat.Format24bppRgb);

        using (var g = Drawing.Graphics.FromImage(resized))
        {
            g.Clear(Drawing.Color.Black);
            g.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;
            g.CompositingQuality = Drawing2D.CompositingQuality.HighQuality;
            g.SmoothingMode = Drawing2D.SmoothingMode.HighQuality;

            switch (scaleMode)
            {
                case ScreensaverScaleMode.Stretch:
                    g.DrawImage(source, 0, 0, StaticWidth, StaticHeight);
                    break;

                case ScreensaverScaleMode.Fit:
                {
                    double scale = Math.Min(
                        StaticWidth / (double)source.Width,
                        StaticHeight / (double)source.Height);
                    int drawW = Math.Max(1, (int)Math.Round(source.Width * scale));
                    int drawH = Math.Max(1, (int)Math.Round(source.Height * scale));
                    int dx = (StaticWidth - drawW) / 2;
                    int dy = (StaticHeight - drawH) / 2;
                    g.DrawImage(source, dx, dy, drawW, drawH);
                    break;
                }

                case ScreensaverScaleMode.Center:
                {
                    int drawW = Math.Min(source.Width, StaticWidth);
                    int drawH = Math.Min(source.Height, StaticHeight);
                    int sx = Math.Max(0, (source.Width - drawW) / 2);
                    int sy = Math.Max(0, (source.Height - drawH) / 2);
                    int dx = (StaticWidth - drawW) / 2;
                    int dy = (StaticHeight - drawH) / 2;
                    g.DrawImage(
                        source,
                        new Drawing.Rectangle(dx, dy, drawW, drawH),
                        new Drawing.Rectangle(sx, sy, drawW, drawH),
                        Drawing.GraphicsUnit.Pixel);
                    break;
                }

                case ScreensaverScaleMode.Tile:
                {
                    double scale = Math.Min(
                        0.5,
                        Math.Min(
                            StaticWidth / (double)source.Width,
                            StaticHeight / (double)source.Height));
                    int tileW = Math.Max(8, (int)Math.Round(source.Width * scale));
                    int tileH = Math.Max(8, (int)Math.Round(source.Height * scale));

                    using var tile = new Drawing.Bitmap(tileW, tileH);
                    using (var tg = Drawing.Graphics.FromImage(tile))
                    {
                        tg.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBilinear;
                        tg.DrawImage(source, 0, 0, tileW, tileH);
                    }

                    using var brush =
                        new Drawing.TextureBrush(tile, Drawing2D.WrapMode.Tile);
                    g.FillRectangle(brush, 0, 0, StaticWidth, StaticHeight);
                    break;
                }

                case ScreensaverScaleMode.Span:
                case ScreensaverScaleMode.Fill:
                default:
                {
                    double scale = Math.Max(
                        StaticWidth / (double)source.Width,
                        StaticHeight / (double)source.Height);

                    if (scaleMode == ScreensaverScaleMode.Span)
                        scale *= 1.08;

                    int drawW =
                        Math.Max(1, (int)Math.Ceiling(source.Width * scale));
                    int drawH =
                        Math.Max(1, (int)Math.Ceiling(source.Height * scale));
                    int dx = (StaticWidth - drawW) / 2;
                    int dy = (StaticHeight - drawH) / 2;
                    g.DrawImage(source, dx, dy, drawW, drawH);
                    break;
                }
            }
        }

        var output = new byte[StaticWidth * StaticHeight * 2];

        for (int y = 0; y < StaticHeight; y++)
        {
            for (int x = 0; x < StaticWidth; x++)
            {
                Drawing.Color p = resized.GetPixel(x, y);
                ushort rgb565 = (ushort)(
                    ((p.R & 0xF8) << 8) |
                    ((p.G & 0xFC) << 3) |
                    (p.B >> 3));

                int o = (y * StaticWidth + x) * 2;
                output[o] = (byte)(rgb565 & 0xFF);
                output[o + 1] = (byte)(rgb565 >> 8);
            }
        }

        return output;
    }

    private static byte[] ToRgb332(
        Drawing.Bitmap source,
        ScreensaverScaleMode scaleMode)
    {
        using var resized = new Drawing.Bitmap(
            Width,
            Height,
            PixelFormat.Format24bppRgb);

        using (var g = Drawing.Graphics.FromImage(resized))
        {
            g.Clear(Drawing.Color.Black);
            g.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;
            g.CompositingQuality = Drawing2D.CompositingQuality.HighQuality;
            g.SmoothingMode = Drawing2D.SmoothingMode.HighQuality;

            switch (scaleMode)
            {
                case ScreensaverScaleMode.Stretch:
                    g.DrawImage(source, 0, 0, Width, Height);
                    break;

                case ScreensaverScaleMode.Fit:
                {
                    double scale = Math.Min(
                        Width / (double)source.Width,
                        Height / (double)source.Height);
                    int drawW = Math.Max(1, (int)Math.Round(source.Width * scale));
                    int drawH = Math.Max(1, (int)Math.Round(source.Height * scale));
                    int dx = (Width - drawW) / 2;
                    int dy = (Height - drawH) / 2;
                    g.DrawImage(source, dx, dy, drawW, drawH);
                    break;
                }

                case ScreensaverScaleMode.Center:
                {
                    int drawW = Math.Min(source.Width, Width);
                    int drawH = Math.Min(source.Height, Height);
                    int sx = Math.Max(0, (source.Width - drawW) / 2);
                    int sy = Math.Max(0, (source.Height - drawH) / 2);
                    int dx = (Width - drawW) / 2;
                    int dy = (Height - drawH) / 2;
                    g.DrawImage(
                        source,
                        new Drawing.Rectangle(dx, dy, drawW, drawH),
                        new Drawing.Rectangle(sx, sy, drawW, drawH),
                        Drawing.GraphicsUnit.Pixel);
                    break;
                }

                case ScreensaverScaleMode.Tile:
                {
                    double scale = Math.Min(
                        0.5,
                        Math.Min(
                            Width / (double)source.Width,
                            Height / (double)source.Height));
                    int tileW = Math.Max(8, (int)Math.Round(source.Width * scale));
                    int tileH = Math.Max(8, (int)Math.Round(source.Height * scale));

                    using var tile = new Drawing.Bitmap(tileW, tileH);
                    using (var tg = Drawing.Graphics.FromImage(tile))
                    {
                        tg.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBilinear;
                        tg.DrawImage(source, 0, 0, tileW, tileH);
                    }

                    using var brush = new Drawing.TextureBrush(tile, Drawing2D.WrapMode.Tile);
                    g.FillRectangle(brush, 0, 0, Width, Height);
                    break;
                }

                case ScreensaverScaleMode.Span:
                case ScreensaverScaleMode.Fill:
                default:
                {
                    double scale = Math.Max(
                        Width / (double)source.Width,
                        Height / (double)source.Height);

                    if (scaleMode == ScreensaverScaleMode.Span)
                    {
                        scale *= 1.08;
                    }

                    int drawW = Math.Max(1, (int)Math.Ceiling(source.Width * scale));
                    int drawH = Math.Max(1, (int)Math.Ceiling(source.Height * scale));
                    int dx = (Width - drawW) / 2;
                    int dy = (Height - drawH) / 2;
                    g.DrawImage(source, dx, dy, drawW, drawH);
                    break;
                }
            }
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
