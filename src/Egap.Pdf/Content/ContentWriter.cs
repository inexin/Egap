using Egap.Pdf.IO;
using Egap.Pdf.Objects;

namespace Egap.Pdf.Content;

/// <summary>
/// Writes a content stream (8.2, table 50): operators with their operands, one per line, for the appearance streams Phase 2
/// draws (Q11, Q12). Operands are written as <see cref="ObjectWriter"/> writes objects, so names and strings are escaped and
/// numbers have no exponents. Graphics state saves and text objects are counted, and <see cref="ToArray"/> refuses an
/// unbalanced stream, which a viewer would otherwise misread.
/// </summary>
internal sealed class ContentWriter
{
    private readonly ByteWriter output = new();
    private int saves;
    private bool inText;
    private int markedContent;

    /// <summary>The content written so far; throws when a <c>q</c>, <c>BT</c> or <c>BMC</c> is not closed.</summary>
    public byte[] ToArray()
    {
        if (saves != 0 || inText || markedContent != 0)
        {
            throw new InvalidOperationException("The content leaves a graphics state, text object or marked content open.");
        }

        return output.ToArray();
    }

    /// <summary><c>q</c>: saves the graphics state.</summary>
    public ContentWriter Save()
    {
        saves++;
        return Op("q");
    }

    /// <summary><c>Q</c>: restores the graphics state.</summary>
    public ContentWriter Restore()
    {
        if (saves == 0)
        {
            throw new InvalidOperationException("Q without q.");
        }

        saves--;
        return Op("Q");
    }

    /// <summary><c>cm</c>: concatenates a matrix to the current transformation.</summary>
    public ContentWriter Transform(double a, double b, double c, double d, double e, double f)
    {
        return Op("cm", a, b, c, d, e, f);
    }

    /// <summary><c>w</c>: line width.</summary>
    public ContentWriter LineWidth(double width)
    {
        return Op("w", width);
    }

    /// <summary><c>J</c>: line cap (0 butt, 1 round, 2 square).</summary>
    public ContentWriter LineCap(int cap)
    {
        return Op("J", cap);
    }

    /// <summary><c>j</c>: line join (0 miter, 1 round, 2 bevel).</summary>
    public ContentWriter LineJoin(int join)
    {
        return Op("j", join);
    }

    /// <summary><c>d</c>: dash pattern; an empty array draws solid lines.</summary>
    public ContentWriter Dash(ReadOnlySpan<double> pattern, double phase = 0)
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

    /// <summary><c>gs</c>: sets parameters from a named graphics state dictionary in the resources.</summary>
    public ContentWriter GraphicsState(PdfName name)
    {
        Operand(name);
        return Op("gs");
    }

    /// <summary><c>g</c> or <c>G</c>: a gray fill or stroke colour.</summary>
    public ContentWriter Gray(double gray, bool stroke = false)
    {
        return Op(stroke ? "G" : "g", gray);
    }

    /// <summary><c>rg</c> or <c>RG</c>: an RGB fill or stroke colour.</summary>
    public ContentWriter Rgb(double red, double green, double blue, bool stroke = false)
    {
        return Op(stroke ? "RG" : "rg", red, green, blue);
    }

    /// <summary><c>k</c> or <c>K</c>: a CMYK fill or stroke colour.</summary>
    public ContentWriter Cmyk(double cyan, double magenta, double yellow, double black, bool stroke = false)
    {
        return Op(stroke ? "K" : "k", cyan, magenta, yellow, black);
    }

    /// <summary>The colour of an annotation or field's <c>/C</c> or <c>/BG</c> array: gray, RGB or CMYK by its length (12.5.2).</summary>
    public ContentWriter Color(PdfArray? color, bool stroke = false)
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
    public ContentWriter MoveTo(double x, double y)
    {
        return Op("m", x, y);
    }

    /// <summary><c>l</c>: a line to a point.</summary>
    public ContentWriter LineTo(double x, double y)
    {
        return Op("l", x, y);
    }

    /// <summary><c>c</c>: a cubic Bézier curve to a point.</summary>
    public ContentWriter CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        return Op("c", x1, y1, x2, y2, x3, y3);
    }

    /// <summary><c>h</c>: closes the subpath.</summary>
    public ContentWriter ClosePath()
    {
        return Op("h");
    }

    /// <summary><c>re</c>: a rectangle subpath.</summary>
    public ContentWriter Rectangle(double x, double y, double width, double height)
    {
        return Op("re", x, y, width, height);
    }

    /// <summary>
    /// An ellipse inscribed in a rectangle, as four Bézier curves with the usual control distance (0.5523 of each radius).
    /// </summary>
    public ContentWriter Ellipse(double x, double y, double width, double height)
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

    /// <summary><c>S</c>: strokes the path.</summary>
    public ContentWriter Stroke()
    {
        return Op("S");
    }

    /// <summary><c>f</c>: fills the path, nonzero winding.</summary>
    public ContentWriter Fill()
    {
        return Op("f");
    }

    /// <summary><c>B</c>: fills, then strokes the path.</summary>
    public ContentWriter FillAndStroke()
    {
        return Op("B");
    }

    /// <summary><c>n</c>: ends the path without painting it, as after a clip.</summary>
    public ContentWriter EndPath()
    {
        return Op("n");
    }

    /// <summary><c>W</c>: clips to the path, nonzero winding; the next path operator ends it.</summary>
    public ContentWriter Clip()
    {
        return Op("W");
    }

    /// <summary><c>Do</c>: paints a named XObject from the resources.</summary>
    public ContentWriter Draw(PdfName xObject)
    {
        Operand(xObject);
        return Op("Do");
    }

    /// <summary><c>BMC</c>: begins marked content with a tag, such as <c>/Tx</c> around a text field's text (12.7.4.3).</summary>
    public ContentWriter BeginMarkedContent(PdfName tag)
    {
        markedContent++;
        Operand(tag);
        return Op("BMC");
    }

    /// <summary><c>EMC</c>: ends marked content.</summary>
    public ContentWriter EndMarkedContent()
    {
        if (markedContent == 0)
        {
            throw new InvalidOperationException("EMC without BMC.");
        }

        markedContent--;
        return Op("EMC");
    }

    /// <summary><c>BT</c>: begins a text object.</summary>
    public ContentWriter BeginText()
    {
        if (inText)
        {
            throw new InvalidOperationException("Text objects do not nest.");
        }

        inText = true;
        return Op("BT");
    }

    /// <summary><c>ET</c>: ends the text object.</summary>
    public ContentWriter EndText()
    {
        if (!inText)
        {
            throw new InvalidOperationException("ET without BT.");
        }

        inText = false;
        return Op("ET");
    }

    /// <summary><c>Tf</c>: the font, by its resource name, and size.</summary>
    public ContentWriter Font(PdfName font, double size)
    {
        Operand(font);
        return Op("Tf", size);
    }

    /// <summary><c>Td</c>: moves to the start of the next line, offset from the start of this one.</summary>
    public ContentWriter MoveText(double x, double y)
    {
        return Op("Td", x, y);
    }

    /// <summary><c>Tm</c>: sets the text matrix.</summary>
    public ContentWriter TextMatrix(double a, double b, double c, double d, double e, double f)
    {
        return Op("Tm", a, b, c, d, e, f);
    }

    /// <summary><c>Tc</c>: character spacing.</summary>
    public ContentWriter CharacterSpacing(double spacing)
    {
        return Op("Tc", spacing);
    }

    /// <summary><c>Tw</c>: word spacing.</summary>
    public ContentWriter WordSpacing(double spacing)
    {
        return Op("Tw", spacing);
    }

    /// <summary><c>TL</c>: leading, the distance <c>T*</c> moves down.</summary>
    public ContentWriter Leading(double leading)
    {
        return Op("TL", leading);
    }

    /// <summary><c>T*</c>: moves to the start of the next line.</summary>
    public ContentWriter NextLine()
    {
        return Op("T*");
    }

    /// <summary><c>Tj</c>: shows a string of codes in the current font's encoding.</summary>
    public ContentWriter ShowText(ReadOnlySpan<byte> codes)
    {
        Operand(PdfString.FromBytes(codes));
        return Op("Tj");
    }

    /// <summary><c>TJ</c>: shows strings with adjustments between them, in thousandths of the font size.</summary>
    public ContentWriter ShowText(IEnumerable<(byte[] Codes, double Adjustment)> runs)
    {
        PdfArray array = [];
        foreach ((byte[] codes, double adjustment) in runs)
        {
            array.Add(PdfString.FromBytes(codes));
            if (adjustment != 0)
            {
                array.Add(new PdfReal(adjustment));
            }
        }

        Operand(array);
        return Op("TJ");
    }

    /// <summary>Copies existing content, such as a page's or an old appearance's, then a line break.</summary>
    public ContentWriter Raw(ReadOnlySpan<byte> content)
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

        if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
        {
            output.WriteInteger((long)value);
        }
        else
        {
            ObjectWriter.Write(new PdfReal(Math.Round(value, 5)), output);
        }
    }

    private ContentWriter Op(string op, params ReadOnlySpan<double> operands)
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
