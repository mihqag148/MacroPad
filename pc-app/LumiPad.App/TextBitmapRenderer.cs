using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LumiPad.App;

public static class TextBitmapRenderer
{
    public static string RenderHex(
        string text,
        int width,
        int height,
        double fontSize,
        bool bold)
    {
        text ??= "";

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));

            var typeface = new Typeface(
                new FontFamily("Segoe UI"),
                FontStyles.Normal,
                bold ? FontWeights.SemiBold : FontWeights.Normal,
                FontStretches.Normal);

            var ft = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                Brushes.White,
                1.0)
            {
                MaxTextWidth = width,
                MaxTextHeight = height,
                Trimming = TextTrimming.CharacterEllipsis
            };

            dc.DrawText(ft, new Point(0, 0));
        }

        var bitmap = new RenderTargetBitmap(
            width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        int bitCount = width * height;
        byte[] bits = new byte[(bitCount + 7) / 8];

        for (int i = 0; i < bitCount; i++)
        {
            int p = i * 4;
            byte b = pixels[p];
            byte g = pixels[p + 1];
            byte r = pixels[p + 2];
            byte a = pixels[p + 3];

            int luminance = Math.Max(r, Math.Max(g, b));
            if (a > 24 && luminance > 72)
            {
                bits[i >> 3] |= (byte)(0x80 >> (i & 7));
            }
        }

        var sb = new StringBuilder(bits.Length * 2);
        foreach (byte value in bits)
            sb.Append(value.ToString("X2"));

        return sb.ToString();
    }
}
