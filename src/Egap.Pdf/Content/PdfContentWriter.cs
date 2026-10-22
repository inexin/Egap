using System.Globalization;

using Egap.Pdf.Content;
using Egap.Pdf.IO;
using Egap.Pdf.Objects;

namespace Egap.Pdf;

/// <summary>
/// Writes a content stream (8.2) operator by operator, every operator of ISO 32000-2 table A.1 included, into bytes for a
/// page, form XObject or appearance stream. Operands are written as the object writer writes objects, so names and strings
/// are escaped, and numbers have no exponents: whole numbers as integers, others rounded to five decimals, or to six
/// significant digits below 1. Saves, text objects and marked content are counted, and <see cref="ToArray"/> refuses content
/// that leaves one open, which a viewer would misread.
/// </summary>
public sealed class PdfContentWriter
{
    private readonly ByteWriter output = new(1024);
    private int saves;
    private bool inText;
    private int markedContent;

    /// <summary>How many bytes have been written.</summary>
    public int Length => output.Length;

    /// <summary>How many <c>q</c> are not yet closed by <c>Q</c>.</summary>
    public int SaveDepth => saves;

    /// <summary>Whether a text object (<c>BT</c>) is open.</summary>
    public bool InTextObject => inText;

    /// <summary>The content written so far.</summary>
    /// <exception cref="InvalidOperationException">A <c>q</c>, <c>BT</c>, <c>BMC</c> or <c>BDC</c> is not closed.</exception>
    public byte[] ToArray()
    {
        if (saves != 0 || inText || markedContent != 0)
        {
            throw new InvalidOperationException("The content leaves a graphics state, text object or marked content open.");
        }

        return output.ToArray();
    }

    /// <summary><c>q</c>: saves the graphics state.</summary>
    public PdfContentWriter Save()
    {
        saves++;
        return Op("q");
    }

    /// <summary><c>Q</c>: restores the graphics state.</summary>
    /// <exception cref="InvalidOperationException">No <c>q</c> is open.</exception>
    public PdfContentWriter Restore()
    {
        if (saves == 0)
        {
            throw new InvalidOperationException("Q without q.");
        }

        saves--;
        return Op("Q");
    }

    /// <summary><c>cm</c>: concatenates a matrix to the current transformation.</summary>
    public PdfContentWriter Transform(double a, double b, double c, double d, double e, double f)
    {
        return Op("cm", a, b, c, d, e, f);
    }

    /// <summary><c>cm</c>: concatenates a matrix to the current transformation.</summary>
    public PdfContentWriter Transform(PdfMatrix matrix)
    {
        return Transform(matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F);
    }

    /// <summary><c>w</c>: line width.</summary>
    public PdfContentWriter LineWidth(double width)
    {
        return Op("w", width);
    }

    /// <summary><c>J</c>: line cap (0 butt, 1 round, 2 square).</summary>
    public PdfContentWriter LineCap(int cap)
    {
        return Op("J", cap);
    }

    /// <summary><c>j</c>: line join (0 miter, 1 round, 2 bevel).</summary>
    public PdfContentWriter LineJoin(int join)
    {
        return Op("j", join);
    }

    /// <summary><c>M</c>: miter limit.</summary>
    public PdfContentWriter MiterLimit(double limit)
    {
        return Op("M", limit);
    }

    /// <summary><c>d</c>: dash pattern; an empty array draws solid lines.</summary>
    public PdfContentWriter Dash(ReadOnlySpan<double> pattern, double phase = 0)
    {
        output.Write((byte)'[');
        for (int i = 0; i < pattern.Length; i++)
        {
            if (i > 0)
            {
                output.Write((byte)' ');
            }

            Number(pattern[i]);
        }

        output.Write("] "u8);
        return Op("d", phase);
    }

    /// <summary><c>ri</c>: rendering intent.</summary>
    public PdfContentWriter RenderingIntent(PdfName intent)
    {
        Operand(intent);
        return Op("ri");
    }

    /// <summary><c>i</c>: flatness tolerance.</summary>
    public PdfContentWriter Flatness(double flatness)
    {
        return Op("i", flatness);
    }

    /// <summary><c>gs</c>: sets parameters from a named graphics state dictionary in the resources.</summary>
    public PdfContentWriter GraphicsState(PdfName name)
    {
        Operand(name);
        return Op("gs");
    }

    /// <summary><c>g</c> or <c>G</c>: a gray fill or stroke colour.</summary>
    public PdfContentWriter Gray(double gray, bool stroke = false)
    {
        return Op(stroke ? "G" : "g", gray);
    }

    /// <summary><c>rg</c> or <c>RG</c>: an RGB fill or stroke colour.</summary>
    public PdfContentWriter Rgb(double red, double green, double blue, bool stroke = false)
    {
        return Op(stroke ? "RG" : "rg", red, green, blue);
    }

    /// <summary><c>k</c> or <c>K</c>: a CMYK fill or stroke colour.</summary>
    public PdfContentWriter Cmyk(double cyan, double magenta, double yellow, double black, bool stroke = false)
    {
        return Op(stroke ? "K" : "k", cyan, magenta, yellow, black);
    }

    /// <summary><c>cs</c> or <c>CS</c>: the fill or stroke colour space, by name: a device space, <c>Pattern</c>, or one in the resources.</summary>
    public PdfContentWriter ColorSpace(PdfName space, bool stroke = false)
    {
        Operand(space);
        return Op(stroke ? "CS" : "cs");
    }

    /// <summary>
    /// <c>scn</c> or <c>SCN</c>: the fill or stroke colour's components in the current colour space, and for a pattern its
    /// name in the resources.
    /// </summary>
    public PdfContentWriter Color(ReadOnlySpan<double> components, PdfName? pattern = null, bool stroke = false)
    {
        foreach (double component in components)
        {
            Number(component);
            output.Write((byte)' ');
        }

        if (pattern is not null)
        {
            Operand(pattern);
        }

        return Op(stroke ? "SCN" : "scn");
    }

    /// <summary>The colour of an annotation or field's <c>/C</c> or <c>/BG</c> array: gray, RGB or CMYK by its length (12.5.2).</summary>
    internal PdfContentWriter ArrayColor(PdfArray? color, bool stroke = false)
    {
        double At(int i)
        {
            return Math.Clamp(color!.GetNumber(i) ?? 0, 0, 1);
        }

        return color?.Count switch
        {
            1 => Gray(At(0), stroke),
            3 => Rgb(At(0), At(1), At(2), stroke),
            4 => Cmyk(At(0), At(1), At(2), At(3), stroke),
            _ => this,
        };
    }

    /// <summary><c>m</c>: begins a subpath.</summary>
    public PdfContentWriter MoveTo(double x, double y)
    {
        return Op("m", x, y);
    }

    /// <summary><c>l</c>: a line to a point.</summary>
    public PdfContentWriter LineTo(double x, double y)
    {
        return Op("l", x, y);
    }

    /// <summary><c>c</c>: a cubic Bézier curve to a point.</summary>
    public PdfContentWriter CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        return Op("c", x1, y1, x2, y2, x3, y3);
    }

    /// <summary><c>v</c>: a cubic Bézier curve whose first control point is the current point.</summary>
    public PdfContentWriter CurveToV(double x2, double y2, double x3, double y3)
    {
        return Op("v", x2, y2, x3, y3);
    }

    /// <summary><c>y</c>: a cubic Bézier curve whose second control point is its end.</summary>
    public PdfContentWriter CurveToY(double x1, double y1, double x3, double y3)
    {
        return Op("y", x1, y1, x3, y3);
    }

    /// <summary><c>h</c>: closes the subpath.</summary>
    public PdfContentWriter ClosePath()
    {
        return Op("h");
    }

    /// <summary><c>re</c>: a rectangle subpath.</summary>
    public PdfContentWriter Rectangle(double x, double y, double width, double height)
    {
        return Op("re", x, y, width, height);
    }

    /// <summary>
    /// An ellipse inscribed in a rectangle, as four Bézier curves with the usual control distance (0.5523 of each radius).
    /// </summary>
    public PdfContentWriter Ellipse(double x, double y, double width, double height)
    {
        const double k = 0.5522847498;
        double rx = width / 2;
        double ry = height / 2;
        double cx = x + rx;
        double cy = y + ry;
        return MoveTo(cx + rx, cy)
            .CurveTo(cx + rx, cy + (ry * k), cx + (rx * k), cy + ry, cx, cy + ry)
            .CurveTo(cx - (rx * k), cy + ry, cx - rx, cy + (ry * k), cx - rx, cy)
            .CurveTo(cx - rx, cy - (ry * k), cx - (rx * k), cy - ry, cx, cy - ry)
            .CurveTo(cx + (rx * k), cy - ry, cx + rx, cy - (ry * k), cx + rx, cy)
            .ClosePath();
    }

    /// <summary>Writes a path's segments as <c>m</c>, <c>l</c>, <c>c</c> and <c>h</c>.</summary>
    public PdfContentWriter Path(PdfPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        foreach (PdfPathSegment segment in path.Segments)
        {
            switch (segment.Verb)
            {
                case PdfPathVerb.MoveTo:
                    MoveTo(segment.Point.X, segment.Point.Y);
                    break;
                case PdfPathVerb.LineTo:
                    LineTo(segment.Point.X, segment.Point.Y);
                    break;
                case PdfPathVerb.CurveTo:
                    CurveTo(segment.Control1.X, segment.Control1.Y, segment.Control2.X, segment.Control2.Y, segment.Point.X, segment.Point.Y);
                    break;
                case PdfPathVerb.Close:
                    ClosePath();
                    break;
            }
        }

        return this;
    }

    /// <summary><c>S</c>: strokes the path.</summary>
    public PdfContentWriter Stroke()
    {
        return Op("S");
    }

    /// <summary><c>s</c>: closes and strokes the path.</summary>
    public PdfContentWriter CloseAndStroke()
    {
        return Op("s");
    }

    /// <summary><c>f</c> or <c>f*</c>: fills the path with the nonzero or even-odd rule.</summary>
    public PdfContentWriter Fill(bool evenOdd = false)
    {
        return Op(evenOdd ? "f*" : "f");
    }

    /// <summary><c>B</c> or <c>B*</c>: fills, then strokes the path.</summary>
    public PdfContentWriter FillAndStroke(bool evenOdd = false)
    {
        return Op(evenOdd ? "B*" : "B");
    }

    /// <summary><c>b</c> or <c>b*</c>: closes, fills and strokes the path.</summary>
    public PdfContentWriter CloseFillAndStroke(bool evenOdd = false)
    {
        return Op(evenOdd ? "b*" : "b");
    }

    /// <summary><c>n</c>: ends the path without painting it, as after a clip.</summary>
    public PdfContentWriter EndPath()
    {
        return Op("n");
    }

    /// <summary><c>W</c> or <c>W*</c>: clips to the path; the next path operator ends it.</summary>
    public PdfContentWriter Clip(bool evenOdd = false)
    {
        return Op(evenOdd ? "W*" : "W");
    }

    /// <summary><c>sh</c>: paints a named shading from the resources over the clip.</summary>
    public PdfContentWriter Shading(PdfName shading)
    {
        Operand(shading);
        return Op("sh");
    }

    /// <summary><c>Do</c>: paints a named XObject from the resources.</summary>
    public PdfContentWriter Draw(PdfName xObject)
    {
        Operand(xObject);
        return Op("Do");
    }

    /// <summary><c>BI</c> … <c>ID</c> … <c>EI</c>: an inline image.</summary>
    public PdfContentWriter InlineImage(PdfInlineImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        ContentSyntax.Write(image, output);
        return this;
    }

    /// <summary><c>BMC</c>: begins marked content with a tag, such as <c>/Tx</c> around a text field's text (12.7.4.3).</summary>
    public PdfContentWriter BeginMarkedContent(PdfName tag)
    {
        markedContent++;
        Operand(tag);
        return Op("BMC");
    }

    /// <summary><c>BDC</c>: begins marked content with a tag and a property list, inline or by name in the resources.</summary>
    /// <exception cref="ArgumentException"><paramref name="properties"/> is neither a dictionary nor a name.</exception>
    public PdfContentWriter BeginMarkedContent(PdfName tag, PdfObject properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (properties is not (PdfDictionary or PdfName))
        {
            throw new ArgumentException("A property list is a dictionary or a name in the resources.", nameof(properties));
        }

        markedContent++;
        Operand(tag);
        Operand(properties);
        return Op("BDC");
    }

    /// <summary><c>EMC</c>: ends marked content.</summary>
    /// <exception cref="InvalidOperationException">No marked content is open.</exception>
    public PdfContentWriter EndMarkedContent()
    {
        if (markedContent == 0)
        {
            throw new InvalidOperationException("EMC without BMC.");
        }

        markedContent--;
        return Op("EMC");
    }

    /// <summary><c>MP</c>, or <c>DP</c> with a property list: a marked-content point.</summary>
    public PdfContentWriter MarkedPoint(PdfName tag, PdfObject? properties = null)
    {
        Operand(tag);
        if (properties is null)
        {
            return Op("MP");
        }

        Operand(properties);
        return Op("DP");
    }

    /// <summary><c>BT</c>: begins a text object.</summary>
    /// <exception cref="InvalidOperationException">A text object is already open.</exception>
    public PdfContentWriter BeginText()
    {
        if (inText)
        {
            throw new InvalidOperationException("Text objects do not nest.");
        }

        inText = true;
        return Op("BT");
    }

    /// <summary><c>ET</c>: ends the text object.</summary>
    /// <exception cref="InvalidOperationException">No text object is open.</exception>
    public PdfContentWriter EndText()
    {
        if (!inText)
        {
            throw new InvalidOperationException("ET without BT.");
        }

        inText = false;
        return Op("ET");
    }

    /// <summary><c>Tf</c>: the font, by its resource name, and size.</summary>
    public PdfContentWriter Font(PdfName font, double size)
    {
        Operand(font);
        return Op("Tf", size);
    }

    /// <summary><c>Td</c>: moves to the start of the next line, offset from the start of this one.</summary>
    public PdfContentWriter MoveText(double x, double y)
    {
        return Op("Td", x, y);
    }

    /// <summary><c>TD</c>: like <c>Td</c>, and sets the leading to <c>-y</c>.</summary>
    public PdfContentWriter MoveTextSetLeading(double x, double y)
    {
        return Op("TD", x, y);
    }

    /// <summary><c>Tm</c>: sets the text matrix.</summary>
    public PdfContentWriter TextMatrix(double a, double b, double c, double d, double e, double f)
    {
        return Op("Tm", a, b, c, d, e, f);
    }

    /// <summary><c>Tm</c>: sets the text matrix.</summary>
    public PdfContentWriter TextMatrix(PdfMatrix matrix)
    {
        return TextMatrix(matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F);
    }

    /// <summary><c>Tc</c>: character spacing.</summary>
    public PdfContentWriter CharacterSpacing(double spacing)
    {
        return Op("Tc", spacing);
    }

    /// <summary><c>Tw</c>: word spacing.</summary>
    public PdfContentWriter WordSpacing(double spacing)
    {
        return Op("Tw", spacing);
    }

    /// <summary><c>Tz</c>: horizontal scaling, in percent.</summary>
    public PdfContentWriter HorizontalScaling(double percent)
    {
        return Op("Tz", percent);
    }

    /// <summary><c>TL</c>: leading, the distance <c>T*</c> moves down.</summary>
    public PdfContentWriter Leading(double leading)
    {
        return Op("TL", leading);
    }

    /// <summary><c>Tr</c>: text rendering mode, 0 to 7.</summary>
    public PdfContentWriter TextRenderMode(int mode)
    {
        return Op("Tr", mode);
    }

    /// <summary><c>Ts</c>: text rise.</summary>
    public PdfContentWriter TextRise(double rise)
    {
        return Op("Ts", rise);
    }

    /// <summary><c>T*</c>: moves to the start of the next line.</summary>
    public PdfContentWriter NextLine()
    {
        return Op("T*");
    }

    /// <summary><c>Tj</c>: shows a string of codes in the current font's encoding.</summary>
    public PdfContentWriter ShowText(ReadOnlySpan<byte> codes, bool hex = false)
    {
        Operand(PdfString.FromBytes(codes, hex));
        return Op("Tj");
    }

    /// <summary><c>TJ</c>: shows strings with adjustments between them, in thousandths of the font size.</summary>
    public PdfContentWriter ShowText(IEnumerable<(byte[] Codes, double Adjustment)> runs, bool hex = false)
    {
        ArgumentNullException.ThrowIfNull(runs);
        output.Write((byte)'[');
        bool separate = false;
        foreach ((byte[] codes, double adjustment) in runs)
        {
            if (separate && hex)
            {
                output.Write((byte)' ');
            }

            ObjectWriter.Write(PdfString.FromBytes(codes, hex), output);
            separate = true;
            if (adjustment != 0)
            {
                output.Write((byte)' ');
                Number(adjustment);
                output.Write((byte)' ');
                separate = false;
            }
        }

        output.Write("] "u8);
        return Op("TJ");
    }

    /// <summary><c>'</c>: moves to the next line and shows a string.</summary>
    public PdfContentWriter NextLineShowText(ReadOnlySpan<byte> codes)
    {
        Operand(PdfString.FromBytes(codes));
        return Op("'");
    }

    /// <summary><c>"</c>: sets word and character spacing, moves to the next line and shows a string.</summary>
    public PdfContentWriter NextLineShowText(double wordSpacing, double characterSpacing, ReadOnlySpan<byte> codes)
    {
        Number(wordSpacing);
        output.Write((byte)' ');
        Number(characterSpacing);
        output.Write((byte)' ');
        Operand(PdfString.FromBytes(codes));
        return Op("\"");
    }

    /// <summary><c>d0</c>: a Type 3 glyph's width, for a glyph that sets its own colour.</summary>
    public PdfContentWriter GlyphWidth(double width, double height = 0)
    {
        return Op("d0", width, height);
    }

    /// <summary><c>d1</c>: a Type 3 glyph's width and bounding box, for a glyph drawn in the colour it is shown with.</summary>
    public PdfContentWriter GlyphWidthAndBox(double width, double height, double left, double bottom, double right, double top)
    {
        return Op("d1", width, height, left, bottom, right, top);
    }

    /// <summary><c>BX</c> or <c>EX</c>: begins or ends a compatibility section, where viewers skip operators they do not know.</summary>
    public PdfContentWriter Compatibility(bool begin)
    {
        return Op(begin ? "BX" : "EX");
    }

    /// <summary>Writes an operation as it is, unknown operators included; <c>q</c>, <c>BT</c> and marked content are counted.</summary>
    public PdfContentWriter Operation(PdfOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        switch (operation.Operator)
        {
            case "q":
                saves++;
                break;
            case "Q" when saves > 0:
                saves--;
                break;
            case "BT":
                inText = true;
                break;
            case "ET":
                inText = false;
                break;
            case "BMC" or "BDC":
                markedContent++;
                break;
            case "EMC" when markedContent > 0:
                markedContent--;
                break;
        }

        ContentSyntax.Write(operation, output);
        return this;
    }

    /// <summary>Copies existing content, such as a page's or an old appearance's, then a line break.</summary>
    public PdfContentWriter Raw(ReadOnlySpan<byte> content)
    {
        output.Write(content);
        output.Write((byte)'\n');
        return this;
    }

    private void Operand(PdfObject value)
    {
        ObjectWriter.Write(value, output);
        output.Write((byte)' ');
    }

    private void Number(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Content operands must be finite numbers.");
        }

        double rounded = Math.Abs(value) >= 1
            ? Math.Round(value, 5)
            : double.Parse(value.ToString("G6", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
        if (rounded == Math.Floor(rounded) && Math.Abs(rounded) < 1e15)
        {
            output.WriteInteger((long)rounded);
            return;
        }

        // Most operands (coordinates) have at most five decimals and are under a billion: their digits are those of the
        // integer rounded * 100000, the same digits FormatReal gives (no shorter text reads back to the same double there).
        if (Math.Abs(rounded) is >= 1 and < 1e9 && Math.Round(rounded * 100000) is double scaled && scaled / 100000 == rounded)
        {
            long units = (long)Math.Abs(scaled);
            if (rounded < 0)
            {
                output.Write((byte)'-');
            }

            output.WriteInteger(units / 100000);
            long fraction = units % 100000;
            int digits = 5;
            while (fraction % 10 == 0)
            {
                fraction /= 10;
                digits--;
            }

            Span<byte> decimals = stackalloc byte[6];
            decimals[0] = (byte)'.';
            for (int i = digits; i > 0; i--)
            {
                decimals[i] = (byte)('0' + (fraction % 10));
                fraction /= 10;
            }

            output.Write(decimals[..(digits + 1)]);
            return;
        }

        // Plain notation, without the leading zero PDF does not need (".5", "-.25").
        string text = ObjectWriter.FormatReal(rounded);
        if (text.StartsWith("0.", StringComparison.Ordinal))
        {
            text = text[1..];
        }
        else if (text.StartsWith("-0.", StringComparison.Ordinal))
        {
            text = "-" + text[2..];
        }

        output.WriteAscii(text);
    }

    private PdfContentWriter Op(string op, params ReadOnlySpan<double> operands)
    {
        foreach (double operand in operands)
        {
            Number(operand);
            output.Write((byte)' ');
        }

        output.WriteAscii(op);
        output.Write((byte)'\n');
        return this;
    }
}
