using System.Globalization;
using System.Numerics;

using Egami;
using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

namespace Egap.Pdf;

/// <summary>
/// Where and how a signature shows on a page (Phase 5 design Q6). Signatures are invisible unless given one: the
/// <see cref="Default(int, RectF, Raster?)"/> layout shows the signer's name with the date, reason and location; <see cref="Draw"/>
/// lets the caller draw anything with the same <see cref="PdfCanvas"/> as pages. The appearance is part of the signed
/// revision, so it is drawn before the file is hashed.
/// </summary>
/// <example>
/// <code>
/// PdfSignOptions options = new()
/// {
///     Signer = signer,
///     Appearance = PdfSignatureAppearance.Default(page: 0, new RectF(400, 700, 180, 60)),
/// };
/// </code>
/// </example>
public sealed class PdfSignatureAppearance
{
    private static readonly SolidPaint Ink = new(new RgbaF32(0, 0, 0, 1));
    private static readonly SolidPaint Border = new(new RgbaF32(0.4f, 0.4f, 0.4f, 1));
    private readonly Action<PdfCanvas>? draw;
    private readonly Raster? image;

    private PdfSignatureAppearance(int page, RectF rectangle, Action<PdfCanvas>? draw, Raster? image)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        if (!(rectangle.Width > 0 && rectangle.Height > 0))
        {
            throw new ArgumentException("The appearance's rectangle must have a positive width and height.", nameof(rectangle));
        }

        PageIndex = page;
        Rectangle = rectangle;
        this.draw = draw;
        this.image = image;
    }

    /// <summary>The page the signature shows on, numbered from 0.</summary>
    public int PageIndex { get; }

    /// <summary>Where it shows: in points, y down from the top left of the page as displayed, as <see cref="PdfCanvas"/> draws.</summary>
    public RectF Rectangle { get; }

    /// <summary>
    /// The default layout: an optional image at the left, then the signer's name large, and under it the signing date, the
    /// reason and the location, in Liberation Sans (the bundled stand-in for Helvetica), inside a thin border.
    /// </summary>
    /// <param name="page">The page, numbered from 0.</param>
    /// <param name="rectangle">Where it shows, in points from the top left of the displayed page.</param>
    /// <param name="image">A picture, such as a scanned handwritten signature or a logo, shown at the left.</param>
    public static PdfSignatureAppearance Default(int page, RectF rectangle, Raster? image = null)
    {
        return new PdfSignatureAppearance(page, rectangle, null, image);
    }

    /// <summary>
    /// An appearance the caller draws: the canvas is the rectangle's size, y down from its top left, and draws as on a page.
    /// </summary>
    /// <param name="page">The page, numbered from 0.</param>
    /// <param name="rectangle">Where it shows, in points from the top left of the displayed page.</param>
    /// <param name="draw">Draws the appearance.</param>
    public static PdfSignatureAppearance Draw(int page, RectF rectangle, Action<PdfCanvas> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        return new PdfSignatureAppearance(page, rectangle, draw, null);
    }

    /// <summary>Draws the appearance into a new form XObject in the document and returns its reference.</summary>
    internal PdfReference Build(PdfDocument document, string? name, DateTimeOffset time, string? reason, string? location)
    {
        PdfCanvas canvas = PdfCanvas.ForForm(document, Rectangle.Width, Rectangle.Height);
        using (canvas)
        {
            if (draw is not null)
            {
                draw(canvas);
            }
            else
            {
                DrawDefault(canvas, name, time, reason, location);
            }
        }

        PdfStream form = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Form"),
            ["BBox"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfReal(Rectangle.Width), new PdfReal(Rectangle.Height) },
            ["Resources"] = canvas.FormResources.Clone(),
        });
        form.SetDecodedBytes(canvas.FormContent ?? []);

        // On a turned page the appearance turns back, so it reads upright as displayed (12.5.5's algorithm fits the turned box into /Rect).
        int rotate = document.Pages[PageIndex].Rotate;
        if (rotate != 0)
        {
            (double sin, double cos) = Math.SinCos(rotate * Math.PI / 180);
            form.Dictionary["Matrix"] = new PdfArray { new PdfInteger((int)Math.Round(cos)), new PdfInteger((int)Math.Round(sin)), new PdfInteger(-(int)Math.Round(sin)), new PdfInteger((int)Math.Round(cos)), new PdfInteger(0), new PdfInteger(0) };
        }

        return document.Objects.Add(form);
    }

    /// <summary>The rectangle in the page's default user space, for the widget's <c>/Rect</c>.</summary>
    internal PdfRectangle PageRectangle(PdfPage page)
    {
        PdfMatrix toPage = PdfCanvas.LayoutMatrix(page);
        return toPage.Transform(new PdfRectangle(Rectangle.X, Rectangle.Y, Rectangle.X + Rectangle.Width, Rectangle.Y + Rectangle.Height));
    }

    private void DrawDefault(PdfCanvas canvas, string? name, DateTimeOffset time, string? reason, string? location)
    {
        float width = Rectangle.Width, height = Rectangle.Height, margin = Math.Min(4, height / 10);
        canvas.StrokeRect(new RectF(0.5f, 0.5f, width - 1, height - 1), new Stroke(1), Border);
        float left = margin;
        if (image is not null)
        {
            // The picture fits a square at the left, keeping its shape.
            float side = height - (2 * margin);
            float scale = Math.Min(side / image.Width, side / image.Height);
            float w = image.Width * scale, h = image.Height * scale;
            canvas.DrawImage(image, new RectF(margin + ((side - w) / 2), margin + ((side - h) / 2), w, h));
            left += side + margin;
        }

        Typeface face = global::Egami.Fonts.Substitutes.SubstituteFonts.Find("Helvetica", FontStyle.Normal)!;
        float textWidth = width - left - margin;
        if (textWidth <= 0)
        {
            return;
        }

        List<string> details = [string.Create(CultureInfo.InvariantCulture, $"Signed {time:yyyy-MM-dd HH:mm zzz}")];
        if (!string.IsNullOrEmpty(reason))
        {
            details.Add("Reason: " + reason);
        }

        if (!string.IsNullOrEmpty(location))
        {
            details.Add("Location: " + location);
        }

        // The name takes up to 40% of the height, the details share the rest; each line shrinks to fit the width.
        float available = height - (2 * margin);
        float nameSize = Fit(face, name ?? "", textWidth, available * 0.4f / 1.15f);
        float detailSize = Math.Min(nameSize * 0.6f, available * 0.6f / (details.Count * 1.2f));
        float y = margin;
        if (!string.IsNullOrEmpty(name))
        {
            Font font = new(face, nameSize);
            y += font.MeasureText(name).Ascent;
            canvas.DrawText(name, new Vector2(left, y), font, Ink);
            y += nameSize * 0.25f;
        }

        foreach (string line in details)
        {
            Font font = new(face, Fit(face, line, textWidth, detailSize));
            y += detailSize * 1.15f;
            canvas.DrawText(line, new Vector2(left, y), font, Ink);
        }
    }

    /// <summary>The largest size up to <paramref name="size"/> at which the text fits the width.</summary>
    private static float Fit(Typeface face, string text, float width, float size)
    {
        if (text.Length == 0 || size <= 0)
        {
            return Math.Max(size, 1);
        }

        float advance = new Font(face, size).MeasureText(text).Advance;
        return advance <= width ? size : Math.Max(1, size * width / advance);
    }
}
