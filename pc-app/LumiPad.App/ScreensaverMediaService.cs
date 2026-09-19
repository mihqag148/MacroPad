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

public sealed record ScreensaverAnimation(
    string FileName,
    int FrameIntervalMs,
    IReadOnlyList<int> FrameDurationsMs,
    IReadOnlyList<byte[]> Frames);

public static class ScreensaverMediaService
{
    public const int Width = 160;
    public const int Height = 86;
    public const int MaxFrames = 25;

    public static async Task<ScreensaverAnimation> LoadAsync(
        string path,
        ScreensaverScaleMode scaleMode)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();

        return ext switch
        {
            ".gif" => await Task.Run(() => LoadGif(path, scaleMode)),
            ".mp4" or ".m4v" or ".mov" => await LoadVideoAsync(path, scaleMode),
            _ => throw new NotSupportedException("Choose a GIF or MP4/M4V/MOV file.")
        };
    }

    private static ScreensaverAnimation LoadGif(string path, ScreensaverScaleMode scaleMode)
    {
        using var image = Drawing.Image.FromFile(path);
        var dimension = new FrameDimension(image.FrameDimensionsList[0]);
        int total = image.GetFrameCount(dimension);
        int count = Math.Min(MaxFrames, Math.Max(1, total));

        int[] sourceDelaysMs = ReadGifFrameDelaysMs(image, total);
        int sourceLoopMs = Math.Max(1, sourceDelaysMs.Sum());

        var frameDurations = new List<int>(count);
        var sourceIndices = new List<int>(count);

        if (total <= MaxFrames)
        {
            for (int i = 0; i < total; i++)
            {
                sourceIndices.Add(i);
                frameDurations.Add(Math.Clamp(sourceDelaysMs[i], 33, 5000));
            }
        }
        else
        {
            // Reduce long GIFs on the time axis instead of raw frame index.
            // The reduced loop keeps the same overall duration as the source.
            int outputLoopMs = Math.Max(sourceLoopMs, count * 33);
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
            33,
            (int)Math.Round(frameDurations.Average()));

        return new ScreensaverAnimation(
            Path.GetFileName(path),
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

                    // Desktop/browser GIF players commonly show 0/1 cs
                    // frames for about 100 ms. Matching that behaviour
                    // avoids a preview that runs much faster than the file
                    // appears in Windows or a browser.
                    delays[i] = delayCs <= 1
                        ? 100
                        : Math.Clamp(delayCs * 10, 20, 5000);
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
            MaxFrames * (1000.0 / 25.0));

        if (spanMs < 250.0)
            count = Math.Max(2, (int)Math.Ceiling(spanMs / (1000.0 / 25.0)));

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

        const int intervalMs = 40;

        return new ScreensaverAnimation(
            Path.GetFileName(path),
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
