using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

namespace Egap.Pdf.Redaction;

/// <summary>
/// Draws what a redaction annotation asks to show once applied (ISO 32000-2, 12.5.6.23): its area filled with <c>/IC</c>
/// and its <c>/OverlayText</c>, aligned by <c>/Q</c>, repeated to fill the area with <c>/Repeat</c>, in the size and colour
/// of <c>/DA</c>, drawn over the page in Liberation Sans, the bundled stand-in for Helvetica.
/// </summary>
internal static partial class Overlay
{
    public static void Draw(PdfPage page, IReadOnlyList<PdfRedactAnnotation> redactions)
    {
        List<PdfRedactAnnotation> shown = [.. redactions.Where(r => r.InteriorColor is not null || !string.IsNullOrEmpty(r.OverlayText))];
        if (shown.Count == 0)
        {
            return;
        }

        PdfMatrix toPage = PdfCanvas.LayoutMatrix(page);
        if (!toPage.TryInvert(out PdfMatrix toCanvas))
        {
            return;
        }

        using PdfCanvas canvas = new(page);
        Typeface face = global::Egami.Fonts.Substitutes.SubstituteFonts.Find("Helvetica", FontStyle.Normal)!;
        foreach (PdfRedactAnnotation redaction in shown)
        {
            foreach (PdfRectangle area in Redactor.Areas(redaction))
            {
                PdfRectangle box = toCanvas.Transform(area);
                RectF rect = new((float)box.Left, (float)box.Bottom, (float)box.Width, (float)box.Height);
                if (redaction.InteriorColor is { } fill)
                {
                    canvas.FillRect(rect, new SolidPaint(new RgbaF32((float)fill.Red, (float)fill.Green, (float)fill.Blue, 1)));
                }

                if (!string.IsNullOrEmpty(redaction.OverlayText))
                {
                    DrawText(canvas, face, redaction, rect);
                }
            }
        }
    }

    private static void DrawText(PdfCanvas canvas, Typeface face, PdfRedactAnnotation redaction, RectF rect)
    {
        string text = redaction.OverlayText!;
        (double? size, RgbaF32 color) = Appearance(redaction.Dictionary.GetText("DA"));
        float fontSize = (float)(size is > 0 ? size.Value : Math.Min(rect.Height * 0.7, 12));
        Font font = new(face, fontSize);
        float advance = font.MeasureText(text).Advance;
        if (advance > rect.Width && advance > 0)
        {
            fontSize *= rect.Width / advance;
            font = new Font(face, fontSize);
            advance = font.MeasureText(text).Advance;
        }

        SolidPaint paint = new(color);
        int alignment = (int)(redaction.Dictionary.GetInt("Q") ?? 0);
        bool repeat = redaction.Dictionary.GetBool("Repeat") == true;
        float line = fontSize * 1.2f;
        canvas.Save();
        canvas.ClipRect(rect);
        for (float y = rect.Y + fontSize; y <= rect.Y + rect.Height + 0.01f || y == rect.Y + fontSize; y += line)
        {
            if (!repeat)
            {
                float x = alignment switch
                {
                    1 => rect.X + ((rect.Width - advance) / 2),
                    2 => rect.X + rect.Width - advance,
                    _ => rect.X,
                };
                canvas.DrawText(text, new Vector2(x, rect.Y + ((rect.Height + (fontSize * 0.7f)) / 2)), font, paint);
                break;
            }

            for (float x = rect.X; x < rect.X + rect.Width; x += advance + (fontSize / 3))
            {
                canvas.DrawText(text, new Vector2(x, y), font, paint);
                if (advance <= 0)
                {
                    break;
                }
            }
        }

        canvas.Restore();
    }

    /// <summary>The size and colour a default appearance string (<c>/DA</c>) gives text; black when it gives none.</summary>
    private static (double? Size, RgbaF32 Color) Appearance(string? da)
    {
        double? size = null;
        RgbaF32 color = new(0, 0, 0, 1);
        if (da is null)
        {
            return (size, color);
        }

        if (FontSize().Match(da) is { Success: true } font)
        {
            size = double.Parse(font.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        if (Rgb().Match(da) is { Success: true } rgb)
        {
            color = new RgbaF32(Number(rgb.Groups[1].Value), Number(rgb.Groups[2].Value), Number(rgb.Groups[3].Value), 1);
        }
        else if (Gray().Match(da) is { Success: true } gray)
        {
            float g = Number(gray.Groups[1].Value);
            color = new RgbaF32(g, g, g, 1);
        }

        return (size, color);
    }

    private static float Number(string text)
    {
        return Math.Clamp(float.Parse(text, CultureInfo.InvariantCulture), 0, 1);
    }

    [GeneratedRegex(@"([0-9.]+)\s+Tf")]
    private static partial Regex FontSize();

    [GeneratedRegex(@"([0-9.]+)\s+([0-9.]+)\s+([0-9.]+)\s+rg")]
    private static partial Regex Rgb();

    [GeneratedRegex(@"([0-9.]+)\s+g\b")]
    private static partial Regex Gray();
}
