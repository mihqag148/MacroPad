using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LumiPad.App;

public sealed record RenderedTextBitmap(int Width, string Hex);

public static class TextBitmapRenderer
{
    public static RenderedTextBitmap RenderScrollable(
        string text,
        int visibleWidth,
        int maxWidth,
        int height,
        double fontSize,
        bool bold)
    {
        text ??= "";
        text = text.Normalize(NormalizationForm.FormC);

        // Segoe UI is a composite Windows UI font family and WPF will fall
        // back to installed CJK/Unicode fonts for glyphs it does not contain.
        // NFC normalization also fixes Vietnamese titles delivered as
        // combining-mark sequences by some media apps.
        var typeface = new Typeface(
            System.Windows.SystemFonts.MessageFontFamily,
            FontStyles.Normal,
            bold ? FontWeights.SemiBold : FontWeights.Normal,
            FontStretches.Normal);

        var measure = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            typeface,
            fontSize,
            System.Windows.Media.Brushes.White,
            1.0);

        int measuredWidth = Math.Max(
            visibleWidth,
            (int)Math.Ceiling(measure.WidthIncludingTrailingWhitespace) + 6);

        int width = Math.Min(maxWidth, measuredWidth);

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(
                System.Windows.Media.Brushes.Black,
                null,
                new System.Windows.Rect(0, 0, width, height));

            var ft = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface,
                fontSize,
                System.Windows.Media.Brushes.White,
                1.0)
            {
                MaxTextWidth = width,
                MaxTextHeight = height,
                Trimming = measuredWidth > maxWidth
                    ? TextTrimming.CharacterEllipsis
                    : TextTrimming.None
            };

            double y = Math.Min(0, (height - ft.Height) / 2.0);
            dc.DrawText(ft, new System.Windows.Point(0, y));
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

        return new RenderedTextBitmap(width, sb.ToString());
    }
}
