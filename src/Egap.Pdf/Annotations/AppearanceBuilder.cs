using System.Globalization;

using Egap.Pdf.Content;
using Egap.Pdf.Fonts;

namespace Egap.Pdf.Annotations;

/// <summary>
/// Draws normal appearance streams (12.5.5) for the annotation subtypes of Phase 2 Q11: shapes, lines, ink, text markup,
/// carets and note icons with path operators, free text and stamps with the standard 14 fonts (Q12). Each appearance is a
/// form XObject whose bounding box is the annotation's rectangle, drawn in page coordinates, so it needs no matrix. The
/// rectangle of a line, polyline, polygon or ink annotation is grown to hold the stroke and its endings.
/// </summary>
internal static class AppearanceBuilder
{
    /// <summary>The ending size in border widths, and at least this many points, as PDFBox sizes them.</summary>
    private const double EndingScale = 3;

    private const double MinEndingSize = 4;

    private static readonly PdfRgbColor NoteYellow = new(1, 1, 0);

    /// <summary>Whether Egap can draw an appearance for the annotation's subtype.</summary>
    public static bool Supports(PdfAnnotation annotation)
    {
        return annotation is PdfSquareAnnotation or PdfCircleAnnotation or PdfLineAnnotation or PdfPolygonAnnotation or PdfPolyLineAnnotation
            or PdfInkAnnotation or PdfTextMarkupAnnotation or PdfCaretAnnotation or PdfTextAnnotation or PdfFreeTextAnnotation or PdfStampAnnotation;
    }

    /// <summary>
    /// Draws the annotation's normal appearance and stores it as <c>/AP /N</c>; false, leaving the annotation as it was, when
    /// the subtype is not one Egap draws or its text cannot be drawn with the standard 14 fonts (a warning says which).
    /// </summary>
    public static bool Update(PdfAnnotation annotation, PdfDocument document)
    {
        if (!Supports(annotation))
        {
            return false;
        }

        GrowRectangle(annotation);
        PdfRectangle box = annotation.Rectangle;
        PdfDictionary resources = [];
        ContentWriter content = new();
        content.Save();
        if (annotation is PdfMarkupAnnotation { Opacity: < 1 } || annotation is PdfHighlightAnnotation)
        {
            PdfDictionary state = new() { ["Type"] = PdfName.Get("ExtGState") };
            if (annotation is PdfMarkupAnnotation markup && markup.Opacity < 1)
            {
                state["CA"] = new PdfReal(markup.Opacity);
                state["ca"] = new PdfReal(markup.Opacity);
            }

            if (annotation is PdfHighlightAnnotation)
            {
                state["BM"] = PdfName.Get("Multiply");
            }

            resources["ExtGState"] = new PdfDictionary { ["GS0"] = state };
            content.GraphicsState("GS0");
        }

        bool drawn = annotation switch
        {
            PdfSquareAnnotation square => Shape(content, square, square.InteriorColor, ellipse: false),
            PdfCircleAnnotation circle => Shape(content, circle, circle.InteriorColor, ellipse: true),
            PdfLineAnnotation line => Line(content, line, [line.Start, line.End], line.StartEnding, line.EndEnding, line.InteriorColor, close: false, null),
            PdfPolyLineAnnotation polyLine => Line(content, polyLine, polyLine.Vertices, polyLine.StartEnding, polyLine.EndEnding, polyLine.InteriorColor, close: false, null),
            PdfPolygonAnnotation polygon => Line(content, polygon, polygon.Vertices, PdfLineEnding.None, PdfLineEnding.None, null, close: true, polygon.InteriorColor),
            PdfInkAnnotation ink => Ink(content, ink),
            PdfTextMarkupAnnotation markup => TextMarkup(content, markup),
            PdfCaretAnnotation caret => Caret(content, caret, box),
            PdfTextAnnotation note => Note(content, note, box),
            PdfFreeTextAnnotation freeText => FreeText(content, freeText, box, resources, document),
            PdfStampAnnotation stamp => Stamp(content, stamp, box, resources),
            _ => false,
        };
        if (!drawn)
        {
            return false;
        }

        content.Restore();
        PdfStream stream = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Form"),
            ["BBox"] = box.ToArray(),
            ["Resources"] = resources,
        });
        stream.SetDecodedBytes(content.ToArray());
        PdfReference reference = document.Objects.Add(stream);
        annotation.Dictionary["AP"] = new PdfDictionary { ["N"] = reference };
        annotation.Dictionary.Remove("AS");
        return true;
    }

    private static double Width(PdfAnnotation annotation)
    {
        return annotation.BorderWidth;
    }

    private static double EndingSize(double width)
    {
        return Math.Max(MinEndingSize, width * EndingScale);
    }

    /// <summary>Sets the stroke width, dash pattern and colour; false when there is no stroke to draw.</summary>
    private static bool StrokeStyle(ContentWriter content, PdfAnnotation annotation)
    {
        double width = Width(annotation);
        if (annotation.Color is not PdfRgbColor color || width <= 0)
        {
            return false;
        }

        content.LineWidth(width).Rgb(color.Red, color.Green, color.Blue, stroke: true);
        if (annotation.BorderStyle == PdfBorderStyle.Dashed)
        {
            content.Dash([.. annotation.BorderDash]);
        }

        return true;
    }

    private static void Paint(ContentWriter content, bool stroke, bool fill)
    {
        if (stroke && fill)
        {
            content.FillAndStroke();
        }
        else if (stroke)
        {
            content.Stroke();
        }
        else if (fill)
        {
            content.Fill();
        }
        else
        {
            content.EndPath();
        }
    }

    private static bool Shape(ContentWriter content, PdfAnnotation annotation, PdfRgbColor? interior, bool ellipse)
    {
        PdfRectangle box = annotation.Rectangle;
        bool stroke = StrokeStyle(content, annotation);
        double inset = stroke ? Width(annotation) / 2 : 0;
        if (interior is PdfRgbColor fill)
        {
            content.Rgb(fill.Red, fill.Green, fill.Blue);
        }

        double width = Math.Max(0, box.Width - (2 * inset));
        double height = Math.Max(0, box.Height - (2 * inset));
        if (ellipse)
        {
            content.Ellipse(box.Left + inset, box.Bottom + inset, width, height);
        }
        else
        {
            content.Rectangle(box.Left + inset, box.Bottom + inset, width, height);
        }

        Paint(content, stroke, interior is not null);
        return true;
    }

    private static bool Line(ContentWriter content, PdfAnnotation annotation, IReadOnlyList<PdfPoint> points, PdfLineEnding start, PdfLineEnding end, PdfRgbColor? endingFill, bool close, PdfRgbColor? interior)
    {
        if (points.Count < 2)
        {
            return true;
        }

        bool stroke = StrokeStyle(content, annotation);
        if (interior is PdfRgbColor fill)
        {
            content.Rgb(fill.Red, fill.Green, fill.Blue);
        }

        content.LineJoin(1);
        content.MoveTo(points[0].X, points[0].Y);
        for (int i = 1; i < points.Count; i++)
        {
            content.LineTo(points[i].X, points[i].Y);
        }

        if (close)
        {
            content.ClosePath();
        }

        Paint(content, stroke, close && interior is not null);
        if (stroke && !close)
        {
            content.Dash([]);
            if (endingFill is PdfRgbColor endings)
            {
                content.Rgb(endings.Red, endings.Green, endings.Blue);
            }

            double size = EndingSize(Width(annotation));
            Ending(content, start, points[0], points[1], size, endingFill is not null);
            Ending(content, end, points[^1], points[^2], size, endingFill is not null);
        }

        return true;
    }

    /// <summary>A line ending (12.5.6.7, table 179) at <paramref name="tip"/>, the line coming from <paramref name="from"/>.</summary>
    private static void Ending(ContentWriter content, PdfLineEnding ending, PdfPoint tip, PdfPoint from, double size, bool fill)
    {
        double dx = tip.X - from.X;
        double dy = tip.Y - from.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (ending == PdfLineEnding.None || length == 0)
        {
            return;
        }

        // Unit vectors along the line (out of it, at the tip) and across it.
        double ux = dx / length;
        double uy = dy / length;
        double nx = -uy;
        double ny = ux;
        double half = size / 2;
        PdfPoint At(double along, double across)
        {
            return new PdfPoint(tip.X + (ux * along) + (nx * across), tip.Y + (uy * along) + (ny * across));
        }

        void Path(bool closed, params PdfPoint[] path)
        {
            content.MoveTo(path[0].X, path[0].Y);
            foreach (PdfPoint point in path.Skip(1))
            {
                content.LineTo(point.X, point.Y);
            }

            if (closed)
            {
                content.ClosePath();
                Paint(content, stroke: true, fill);
            }
            else
            {
                content.Stroke();
            }
        }

        switch (ending)
        {
            case PdfLineEnding.Square:
                Path(true, At(-half, -half), At(half, -half), At(half, half), At(-half, half));
                break;
            case PdfLineEnding.Circle:
                content.Ellipse(tip.X - half, tip.Y - half, size, size);
                Paint(content, stroke: true, fill);
                break;
            case PdfLineEnding.Diamond:
                Path(true, At(-half, 0), At(0, -half), At(half, 0), At(0, half));
                break;
            case PdfLineEnding.OpenArrow:
                Path(false, At(-size, -half), At(0, 0), At(-size, half));
                break;
            case PdfLineEnding.ClosedArrow:
                Path(true, At(-size, -half), At(0, 0), At(-size, half));
                break;
            case PdfLineEnding.ROpenArrow:
                Path(false, At(size, -half), At(0, 0), At(size, half));
                break;
            case PdfLineEnding.RClosedArrow:
                Path(true, At(size, -half), At(0, 0), At(size, half));
                break;
            case PdfLineEnding.Butt:
                Path(false, At(0, -half), At(0, half));
                break;
            case PdfLineEnding.Slash:
                // 30 degrees from across the line, as the spec draws it.
                Path(false, At(-half * 0.5, -half * 0.866), At(half * 0.5, half * 0.866));
                break;
        }
    }

    private static bool Ink(ContentWriter content, PdfInkAnnotation ink)
    {
        if (!StrokeStyle(content, ink))
        {
            return true;
        }

        content.LineCap(1).LineJoin(1);
        foreach (IReadOnlyList<PdfPoint> stroke in ink.Strokes.Where(s => s.Count > 0))
        {
            content.MoveTo(stroke[0].X, stroke[0].Y);
            if (stroke.Count == 1)
            {
                content.LineTo(stroke[0].X, stroke[0].Y);
            }

            foreach (PdfPoint point in stroke.Skip(1))
            {
                content.LineTo(point.X, point.Y);
            }

            content.Stroke();
        }

        return true;
    }

    /// <summary>
    /// Highlight, underline, squiggly underline or strikeout over each quadrilateral, whose points are upper left, upper
    /// right, lower left, lower right; the colour defaults to yellow for highlights and black otherwise.
    /// </summary>
    private static bool TextMarkup(ContentWriter content, PdfTextMarkupAnnotation markup)
    {
        PdfRgbColor color = markup.Color ?? (markup is PdfHighlightAnnotation ? NoteYellow : PdfRgbColor.Black);
        IReadOnlyList<PdfPoint> points = markup.QuadPoints;
        content.Rgb(color.Red, color.Green, color.Blue).Rgb(color.Red, color.Green, color.Blue, stroke: true);
        for (int i = 0; i + 3 < points.Count; i += 4)
        {
            PdfPoint upperLeft = points[i];
            PdfPoint upperRight = points[i + 1];
            PdfPoint lowerLeft = points[i + 2];
            PdfPoint lowerRight = points[i + 3];
            double height = Distance(upperLeft, lowerLeft);
            double width = Math.Max(0.5, height / 14);
            static PdfPoint Between(PdfPoint low, PdfPoint high, double t)
            {
                return new PdfPoint(low.X + ((high.X - low.X) * t), low.Y + ((high.Y - low.Y) * t));
            }

            switch (markup)
            {
                case PdfHighlightAnnotation:
                    content.MoveTo(lowerLeft.X, lowerLeft.Y).LineTo(lowerRight.X, lowerRight.Y).LineTo(upperRight.X, upperRight.Y).LineTo(upperLeft.X, upperLeft.Y)
                        .ClosePath().Fill();
                    break;
                case PdfUnderlineAnnotation:
                    PdfPoint left = Between(lowerLeft, upperLeft, 1.0 / 7);
                    PdfPoint right = Between(lowerRight, upperRight, 1.0 / 7);
                    content.LineWidth(width).MoveTo(left.X, left.Y).LineTo(right.X, right.Y).Stroke();
                    break;
                case PdfStrikeOutAnnotation:
                    PdfPoint middleLeft = Between(lowerLeft, upperLeft, 0.5);
                    PdfPoint middleRight = Between(lowerRight, upperRight, 0.5);
                    content.LineWidth(width).MoveTo(middleLeft.X, middleLeft.Y).LineTo(middleRight.X, middleRight.Y).Stroke();
                    break;
                case PdfSquigglyAnnotation:
                    Squiggle(content, lowerLeft, lowerRight, upperLeft, height, width);
                    break;
            }
        }

        return true;
    }

    /// <summary>A zigzag along the bottom of a quadrilateral, a wave every quarter of its height.</summary>
    private static void Squiggle(ContentWriter content, PdfPoint lowerLeft, PdfPoint lowerRight, PdfPoint upperLeft, double height, double width)
    {
        double length = Distance(lowerLeft, lowerRight);
        if (length == 0 || height == 0)
        {
            return;
        }

        double ux = (lowerRight.X - lowerLeft.X) / length;
        double uy = (lowerRight.Y - lowerLeft.Y) / length;
        double vx = (upperLeft.X - lowerLeft.X) / height;
        double vy = (upperLeft.Y - lowerLeft.Y) / height;
        double step = Math.Max(1, height / 8);
        double amplitude = Math.Max(0.5, height / 16);
        content.LineWidth(width).LineJoin(1);
        content.MoveTo(lowerLeft.X + (vx * amplitude), lowerLeft.Y + (vy * amplitude));
        int count = Math.Min(10_000, (int)Math.Ceiling(length / step));
        for (int i = 1; i <= count; i++)
        {
            double along = Math.Min(length, i * step);
            double up = i % 2 == 0 ? amplitude : 0;
            content.LineTo(lowerLeft.X + (ux * along) + (vx * up), lowerLeft.Y + (uy * along) + (vy * up));
        }

        content.Stroke();
    }

    /// <summary>A caret: a filled shape like an upturned V with curved sides, as Acrobat draws one.</summary>
    private static bool Caret(ContentWriter content, PdfCaretAnnotation caret, PdfRectangle box)
    {
        PdfRgbColor color = caret.Color ?? PdfRgbColor.Black;
        double middle = box.Left + (box.Width / 2);
        content.Rgb(color.Red, color.Green, color.Blue)
            .MoveTo(box.Left, box.Bottom)
            .CurveTo(middle, box.Bottom, middle, box.Bottom + (box.Height / 2), middle, box.Top)
            .CurveTo(middle, box.Bottom + (box.Height / 2), middle, box.Bottom, box.Right, box.Bottom)
            .ClosePath().Fill();
        return true;
    }

    /// <summary>A note icon drawn in a 20 by 20 square scaled to the rectangle, filled with the note's colour.</summary>
    private static bool Note(ContentWriter content, PdfTextAnnotation note, PdfRectangle box)
    {
        PdfRgbColor color = note.Color ?? NoteYellow;
        content.Transform(box.Width / 20, 0, 0, box.Height / 20, box.Left, box.Bottom)
            .LineWidth(1).LineJoin(1).Gray(0, stroke: true).Rgb(color.Red, color.Green, color.Blue);
        switch (note.Icon)
        {
            case "Comment":
                content.MoveTo(2.5, 6.5).LineTo(2.5, 17.5).LineTo(17.5, 17.5).LineTo(17.5, 6.5).LineTo(9, 6.5).LineTo(5, 2.5).LineTo(5.5, 6.5).ClosePath().FillAndStroke();
                Lines(content, 4.5, 15.5, [14, 11, 8.5]);
                break;
            case "Key":
                content.Ellipse(2.5, 9.5, 8, 8).FillAndStroke();
                content.MoveTo(9.5, 10.5).LineTo(17.5, 2.5).MoveTo(14.5, 5.5).LineTo(16.5, 7.5).MoveTo(12.5, 7.5).LineTo(14, 9).Stroke();
                break;
            case "Help":
                content.Ellipse(1.5, 1.5, 17, 17).FillAndStroke();
                content.LineWidth(2).MoveTo(7, 12.5).CurveTo(7, 17, 13, 17, 13, 12.5).CurveTo(13, 10.5, 10, 10.5, 10, 7.5).Stroke();
                content.Gray(0).Ellipse(9, 3.5, 2, 2).Fill();
                break;
            case "Insert":
                content.MoveTo(10, 17.5).LineTo(2.5, 2.5).LineTo(17.5, 2.5).ClosePath().FillAndStroke();
                break;
            case "NewParagraph":
                content.MoveTo(10, 18).LineTo(4, 8).LineTo(16, 8).ClosePath().FillAndStroke();
                content.MoveTo(4, 5).LineTo(16, 5).MoveTo(4, 2.5).LineTo(16, 2.5).Stroke();
                break;
            case "Paragraph":
                content.Rectangle(2.5, 2.5, 15, 15).FillAndStroke();
                content.Gray(0).MoveTo(11, 15.5).LineTo(8.5, 15.5).CurveTo(5, 15.5, 5, 10, 8.5, 10).LineTo(10, 10).ClosePath().Fill();
                content.LineWidth(1.5).MoveTo(10.5, 15.5).LineTo(10.5, 4.5).MoveTo(13, 15.5).LineTo(13, 4.5).MoveTo(10, 15.5).LineTo(14, 15.5).Stroke();
                break;
            default:
                content.Rectangle(3.5, 1.5, 13, 17).FillAndStroke();
                Lines(content, 6, 14, [14.5, 11.5, 8.5, 5.5]);
                break;
        }

        return true;
    }

    private static void Lines(ContentWriter content, double left, double right, double[] heights)
    {
        foreach (double y in heights)
        {
            content.MoveTo(left, y).LineTo(right, y);
        }

        content.Stroke();
    }

    /// <summary>
    /// Free text (12.5.6.6): the box filled with the annotation's colour and bordered, then the text in the font, size and
    /// colour of <c>/DA</c>, wrapped to the box and aligned by <c>/Q</c>. The font is the standard 14 font <c>/DA</c> names
    /// through the form's default resources, else Helvetica (Q12).
    /// </summary>
    private static bool FreeText(ContentWriter content, PdfFreeTextAnnotation freeText, PdfRectangle box, PdfDictionary resources, PdfDocument document)
    {
        DefaultAppearance appearance = DefaultAppearance.Parse(freeText.DefaultAppearance);
        PdfDictionary? formFont = appearance.Font is PdfName name ? document.Catalog.Get<PdfDictionary>("AcroForm")?.Get<PdfDictionary>("DR")?.Get<PdfDictionary>("Font")?.Get<PdfDictionary>(name) : null;
        Standard14Font font = Standard14Font.Get(formFont?.GetName("BaseFont")?.Value) is { IsSymbolic: false } named ? named : Standard14Font.Get("Helvetica")!;
        string text = freeText.Contents ?? "";
        if (font.Encode(text.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal)) is null)
        {
            document.Log.Add(PdfWarningCode.AppearanceNotDrawn, $"A free text annotation's text has characters {font.Name} cannot show; its appearance was not drawn.", objectNumber: null);
            return false;
        }

        if (freeText.Color is PdfRgbColor background)
        {
            content.Rgb(background.Red, background.Green, background.Blue).Rectangle(box.Left, box.Bottom, box.Width, box.Height).Fill();
        }

        double border = freeText.BorderWidth;
        if (border > 0)
        {
            content.LineWidth(border);
            SetColor(content, appearance.Color, stroke: true);
            if (freeText.BorderStyle == PdfBorderStyle.Dashed)
            {
                content.Dash([.. freeText.BorderDash]);
            }

            content.Rectangle(box.Left + (border / 2), box.Bottom + (border / 2), Math.Max(0, box.Width - border), Math.Max(0, box.Height - border)).Stroke().Dash([]);
        }

        PdfName resource = appearance.Font ?? "Helv";
        resources["Font"] = new PdfDictionary { [resource] = StandardFontDictionary(font) };
        double size = appearance.Size is double s && s > 0 ? s : 12;
        double padding = border + 2;
        DrawText(content, font, resource, size, appearance.Color, text, box, padding, freeText.Alignment, top: true);
        return true;
    }

    /// <summary>A stamp: a rounded border and the stamp's name in capitals, in red, green or blue as Acrobat colours them.</summary>
    private static bool Stamp(ContentWriter content, PdfStampAnnotation stamp, PdfRectangle box, PdfDictionary resources)
    {
        string label = string.Concat(stamp.Icon.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(stamp.Icon[i - 1]) ? " " + c : c.ToString())).ToUpperInvariant();
        PdfRgbColor color = stamp.Color ?? stamp.Icon switch
        {
            "Approved" or "Final" or "Completed" => new PdfRgbColor(0.1, 0.5, 0.1),
            "NotApproved" or "Expired" or "NotForPublicRelease" or "Confidential" or "TopSecret" or "Void" => new PdfRgbColor(0.75, 0.1, 0.1),
            _ => new PdfRgbColor(0.1, 0.2, 0.6),
        };
        double border = Math.Max(1, Math.Min(box.Width, box.Height) / 20);
        double radius = Math.Min(box.Width, box.Height) / 6;
        double l = box.Left + (border / 2);
        double b = box.Bottom + (border / 2);
        double r = box.Right - (border / 2);
        double t = box.Top - (border / 2);
        const double k = 0.5523;
        content.LineWidth(border).Rgb(color.Red, color.Green, color.Blue, stroke: true).Rgb(color.Red, color.Green, color.Blue)
            .MoveTo(l + radius, b).LineTo(r - radius, b).CurveTo(r - (radius * (1 - k)), b, r, b + (radius * (1 - k)), r, b + radius)
            .LineTo(r, t - radius).CurveTo(r, t - (radius * (1 - k)), r - (radius * (1 - k)), t, r - radius, t)
            .LineTo(l + radius, t).CurveTo(l + (radius * (1 - k)), t, l, t - (radius * (1 - k)), l, t - radius)
            .LineTo(l, b + radius).CurveTo(l, b + (radius * (1 - k)), l + (radius * (1 - k)), b, l + radius, b)
            .ClosePath().Stroke();

        Standard14Font font = Standard14Font.Get("Helvetica-Bold")!;
        byte[] codes = font.Encode(label) ?? font.Encode("DRAFT")!;
        double measured = font.Measure(codes, 1);
        double size = Math.Max(1, Math.Min((box.Height - (4 * border)) * 0.7, measured > 0 ? (box.Width - (6 * border)) / measured : 12));
        resources["Font"] = new PdfDictionary { ["HeBo"] = StandardFontDictionary(font) };
        double x = box.Left + ((box.Width - (measured * size)) / 2);
        double y = box.Bottom + ((box.Height - (font.CapHeight / 1000 * size)) / 2);
        content.BeginText().Font("HeBo", size).MoveText(x, y).ShowText(codes).EndText();
        return true;
    }

    /// <summary>Text wrapped to a box, line by line from the top, each line aligned.</summary>
    internal static void DrawText(ContentWriter content, Standard14Font font, PdfName resource, double size, double[]? color, string text, PdfRectangle box, double padding, PdfTextAlignment alignment, bool top)
    {
        double width = Math.Max(0, box.Width - (2 * padding));
        double leading = size * Math.Max(1, (font.Ascent - font.Descent) / 1000);
        List<byte[]> lines = Wrap(font, text, size, width);
        content.Save().Rectangle(box.Left + padding, box.Bottom + padding, width, Math.Max(0, box.Height - (2 * padding))).Clip().EndPath();
        content.BeginText().Font(resource, size);
        SetColor(content, color, stroke: false);
        double y = top ? box.Top - padding - (font.Ascent / 1000 * size) : box.Bottom + ((box.Height - (font.CapHeight / 1000 * size)) / 2);
        double previousX = 0;
        double previousY = 0;
        foreach (byte[] line in lines)
        {
            double lineWidth = font.Measure(line, size);
            double x = box.Left + padding + alignment switch
            {
                PdfTextAlignment.Center => (width - lineWidth) / 2,
                PdfTextAlignment.Right => width - lineWidth,
                _ => 0,
            };
            content.MoveText(x - previousX, y - previousY).ShowText(line);
            (previousX, previousY) = (x, y);
            y -= leading;
        }

        content.EndText().Restore();
    }

    /// <summary>Text split into lines that fit a width: at line breaks, then between words, then inside a word too long for a line.</summary>
    private static List<byte[]> Wrap(Standard14Font font, string text, double size, double width)
    {
        List<byte[]> lines = [];
        foreach (string paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            byte[] codes = font.Encode(paragraph) ?? [];
            int start = 0;
            while (start < codes.Length || (start == 0 && codes.Length == 0))
            {
                if (codes.Length == 0)
                {
                    lines.Add([]);
                    break;
                }

                int end = start;
                int lastSpace = -1;
                double used = 0;
                while (end < codes.Length)
                {
                    double advance = font.Width(codes[end]) / 1000.0 * size;
                    if (used + advance > width && end > start)
                    {
                        break;
                    }

                    if (codes[end] == (byte)' ')
                    {
                        lastSpace = end;
                    }

                    used += advance;
                    end++;
                }

                if (end < codes.Length && lastSpace > start)
                {
                    end = lastSpace;
                }

                lines.Add(codes[start..end]);
                start = end;
                while (start < codes.Length && codes[start] == (byte)' ')
                {
                    start++;
                }
            }
        }

        return lines;
    }

    /// <summary>A font dictionary for a standard 14 font, WinAnsi encoded unless it is symbolic.</summary>
    internal static PdfDictionary StandardFontDictionary(Standard14Font font)
    {
        PdfDictionary dictionary = new()
        {
            ["Type"] = PdfName.Get("Font"),
            ["Subtype"] = PdfName.Get("Type1"),
            ["BaseFont"] = PdfName.Get(font.Name),
        };
        if (!font.IsSymbolic)
        {
            dictionary["Encoding"] = PdfName.Get("WinAnsiEncoding");
        }

        return dictionary;
    }

    private static void SetColor(ContentWriter content, double[]? color, bool stroke)
    {
        _ = color?.Length switch
        {
            1 => content.Gray(color[0], stroke),
            3 => content.Rgb(color[0], color[1], color[2], stroke),
            4 => content.Cmyk(color[0], color[1], color[2], color[3], stroke),
            _ => content.Gray(0, stroke),
        };
    }

    /// <summary>
    /// Grows the rectangle of a line, polyline, polygon or ink annotation to hold its points, the stroke and the endings.
    /// </summary>
    private static void GrowRectangle(PdfAnnotation annotation)
    {
        IEnumerable<PdfPoint>? points = annotation switch
        {
            PdfLineAnnotation line => [line.Start, line.End],
            PdfPolyLineAnnotation polyLine => polyLine.Vertices,
            PdfPolygonAnnotation polygon => polygon.Vertices,
            PdfInkAnnotation ink => ink.Strokes.SelectMany(s => s),
            _ => null,
        };
        List<PdfPoint>? list = points?.ToList();
        if (list is not { Count: > 0 })
        {
            return;
        }

        double width = annotation.BorderWidth;
        double margin = (width / 2) + (annotation is PdfLineAnnotation or PdfPolyLineAnnotation ? EndingSize(width) : 0) + 1;
        PdfRectangle bounds = new(list.Min(p => p.X) - margin, list.Min(p => p.Y) - margin, list.Max(p => p.X) + margin, list.Max(p => p.Y) + margin);
        annotation.Dictionary["Rect"] = bounds.ToArray();
    }

    private static double Distance(PdfPoint a, PdfPoint b)
    {
        return Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
    }
}

/// <summary>
/// The parts of a default appearance string (12.7.4.3) that drawing needs: the font resource name and size from <c>Tf</c>,
/// and the fill colour from <c>g</c>, <c>rg</c> or <c>k</c>, the last of each winning.
/// </summary>
internal readonly record struct DefaultAppearance(PdfName? Font, double? Size, double[]? Color)
{
    public static DefaultAppearance Parse(string? text)
    {
        PdfName? font = null;
        double? size = null;
        double[]? color = null;
        List<string> operands = [];
        foreach (string token in (text ?? "").Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            switch (token)
            {
                case "Tf" when operands.Count >= 2 && operands[^2].StartsWith('/') && Number(operands[^1]) is double s:
                    font = PdfName.Get(operands[^2][1..]);
                    size = s;
                    break;
                case "g" when operands.Count >= 1:
                    color = Numbers(operands, 1) ?? color;
                    break;
                case "rg" when operands.Count >= 3:
                    color = Numbers(operands, 3) ?? color;
                    break;
                case "k" when operands.Count >= 4:
                    color = Numbers(operands, 4) ?? color;
                    break;
                default:
                    operands.Add(token);
                    continue;
            }

            operands.Clear();
        }

        return new DefaultAppearance(font, size, color);
    }

    private static double? Number(string text)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;
    }

    private static double[]? Numbers(List<string> operands, int count)
    {
        double[] values = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (Number(operands[operands.Count - count + i]) is not double value)
            {
                return null;
            }

            values[i] = Math.Clamp(value, 0, 1);
        }

        return values;
    }
}
