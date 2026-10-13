using System.Globalization;

using Egap.Pdf.Annotations;
using Egap.Pdf.Content;
using Egap.Pdf.Fonts;

namespace Egap.Pdf.Forms;

/// <summary>
/// Draws the normal appearance of text, combo box and list box widgets (12.7.4.3) from the field's value, default
/// appearance and the widget's <c>/MK</c> (Phase 2 Q12): the text is drawn in the field's font when it is a simple font
/// with WinAnsi encoding whose widths are known (from <c>/Widths</c>, or the standard 14 metrics) and the encoding covers
/// the text, else in Helvetica. Text neither can show sets <c>/NeedAppearances</c>, with a warning, and the widget's stale
/// appearance is removed so no viewer shows the old value.
/// </summary>
internal static class FieldAppearance
{
    /// <summary>Acrobat's colour behind selected list box options.</summary>
    private static readonly double[] SelectionColor = [0.6, 0.757, 0.855];

    /// <summary>Draws every widget of a text or choice field; false when the text could not be drawn.</summary>
    public static bool Update(PdfField field)
    {
        string? text = field switch
        {
            PdfTextField textField => textField.Value ?? "",
            PdfComboBoxField combo => combo.Value is string value ? combo.Options.FirstOrDefault(o => o.ExportValue == value) is { DisplayText: string display } && combo.Options.Any(o => o.ExportValue == value) ? display : value : "",
            PdfListBoxField => "",
            _ => null,
        };
        if (text is null)
        {
            return false;
        }

        DefaultAppearance appearance = DefaultAppearance.Parse(field.DefaultAppearance);
        List<string> lines = field is PdfListBoxField list ? [.. list.Options.Select(o => o.DisplayText)] : [Shown(field, text)];
        FieldFont? font = FieldFont.Resolve(field.Document, appearance.Font, string.Concat(lines));
        if (font is null)
        {
            field.Document.Log.Add(PdfWarningCode.AppearanceNotDrawn, $"Field {field.FullName} has text the standard 14 fonts cannot show; NeedAppearances was set so viewers draw it.");
            if (field.Document.Form.Dictionary is PdfDictionary form)
            {
                form["NeedAppearances"] = PdfBoolean.True;
            }

            foreach (PdfWidgetAnnotation widget in field.Widgets)
            {
                widget.Dictionary.Remove("AP");
            }

            return false;
        }

        foreach (PdfWidgetAnnotation widget in field.Widgets)
        {
            Draw(field, widget, appearance, font, lines);
        }

        return true;
    }

    /// <summary>The text as shown: a password as asterisks, a single-line field's line breaks as spaces.</summary>
    private static string Shown(PdfField field, string text)
    {
        if (field is PdfTextField { IsPassword: true })
        {
            return new string('*', text.Length);
        }

        return field is PdfTextField { IsMultiline: true } ? text : text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');
    }

    private static void Draw(PdfField field, PdfWidgetAnnotation widget, DefaultAppearance appearance, FieldFont font, List<string> lines)
    {
        PdfRectangle rect = widget.Rectangle;
        PdfDictionary? mk = widget.Dictionary.Get<PdfDictionary>("MK");
        int rotation = (int)((((mk?.GetInt("R") ?? 0) % 360) + 360) % 360);
        bool turned = rotation is 90 or 270;
        double width = turned ? rect.Height : rect.Width;
        double height = turned ? rect.Width : rect.Height;
        PdfArray? border = mk?.Get<PdfArray>("BC");
        double borderWidth = border is { Count: > 0 } ? widget.BorderWidth : 0;

        PdfContentWriter content = new();
        if (mk?.Get<PdfArray>("BG") is { Count: > 0 } background)
        {
            content.Save().ArrayColor(background).Rectangle(0, 0, width, height).Fill().Restore();
        }

        if (borderWidth > 0)
        {
            content.Save().ArrayColor(border, stroke: true).LineWidth(borderWidth);
            if (widget.BorderStyle == PdfBorderStyle.Dashed)
            {
                content.Dash([.. widget.BorderDash]);
            }

            if (widget.BorderStyle == PdfBorderStyle.Underline)
            {
                content.MoveTo(0, borderWidth / 2).LineTo(width, borderWidth / 2).Stroke();
            }
            else
            {
                content.Rectangle(borderWidth / 2, borderWidth / 2, Math.Max(0, width - borderWidth), Math.Max(0, height - borderWidth)).Stroke();
            }

            content.Restore();
        }

        double padding = Math.Max(1, borderWidth) * (widget.BorderStyle is PdfBorderStyle.Beveled or PdfBorderStyle.Inset ? 2 : 1) + 1;
        PdfRectangle inner = new(padding, padding, Math.Max(padding, width - padding), Math.Max(padding, height - padding));
        content.BeginMarkedContent("Tx").Save().Rectangle(inner.Left, inner.Bottom, inner.Width, inner.Height).Clip().EndPath();
        double lineHeight = (font.Ascent - font.Descent) / 1000;
        switch (field)
        {
            case PdfListBoxField list:
                double size = appearance.Size is double s && s > 0 ? s : 12;
                double leading = size * lineHeight;
                HashSet<int> selected = [.. Selected(list)];
                double top = inner.Top;
                for (int i = list.TopIndex; i < lines.Count && top > inner.Bottom; i++)
                {
                    if (selected.Contains(i))
                    {
                        content.Save().Rgb(SelectionColor[0], SelectionColor[1], SelectionColor[2]).Rectangle(inner.Left, top - leading, inner.Width, leading).Fill().Restore();
                    }

                    Line(content, font, appearance, size, lines[i], inner.Left + 1, top - (font.Ascent / 1000 * size), inner.Width - 2, PdfTextAlignment.Left);
                    top -= leading;
                }

                break;
            case PdfTextField { IsMultiline: true }:
                double multiSize = appearance.Size is double ms && ms > 0 ? ms : 12;
                Multiline(content, font, appearance, multiSize, lines[0], inner, field.Alignment);
                break;
            case PdfTextField { IsComb: true } comb:
                double combSize = AutoSize(appearance, font, lines[0], inner, lineHeight);
                Comb(content, font, appearance, combSize, lines[0], comb.MaxLength!.Value, width, inner, lineHeight);
                break;
            default:
                double lineSize = AutoSize(appearance, font, lines[0], inner, lineHeight);
                double baseline = inner.Bottom + ((inner.Height - (lineHeight * lineSize)) / 2) - (font.Descent / 1000 * lineSize);
                Line(content, font, appearance, lineSize, lines[0], inner.Left + 1, baseline, inner.Width - 2, field.Alignment);
                break;
        }

        content.Restore().EndMarkedContent();
        PdfStream stream = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Form"),
            ["BBox"] = new PdfRectangle(0, 0, width, height).ToArray(),
            ["Resources"] = new PdfDictionary { ["Font"] = new PdfDictionary { [font.ResourceName] = font.Dictionary is PdfDictionary direct ? direct.Clone() : font.Dictionary } },
        });
        if (rotation != 0)
        {
            (double a, double b, double c, double d, double e, double f) = rotation switch
            {
                90 => (0.0, 1.0, -1.0, 0.0, height, 0.0),
                180 => (-1.0, 0.0, 0.0, -1.0, width, height),
                _ => (0.0, -1.0, 1.0, 0.0, 0.0, width),
            };
            stream.Dictionary["Matrix"] = new PdfArray(new[] { a, b, c, d, e, f }.Select(v => v == Math.Floor(v) ? (PdfObject)new PdfInteger((long)v) : new PdfReal(v)));
        }

        stream.SetDecodedBytes(content.ToArray());
        widget.Dictionary["AP"] = new PdfDictionary { ["N"] = field.Document.Objects.Add(stream) };
        widget.Dictionary.Remove("AS");
    }

    /// <summary>The indices of a list box's selected options: <c>/I</c>, else the options whose value is selected.</summary>
    private static IEnumerable<int> Selected(PdfListBoxField list)
    {
        if (list.SelectedIndices.Count > 0)
        {
            return list.SelectedIndices;
        }

        IReadOnlyList<PdfChoiceOption> options = list.Options;
        HashSet<string> values = [.. list.Values];
        return Enumerable.Range(0, options.Count).Where(i => values.Contains(options[i].ExportValue));
    }

    /// <summary>
    /// The font size: the default appearance's, or for 0 (auto) the size that fits the box's height, capped at 12 and
    /// shrunk to fit the text's width, no smaller than 4, as Acrobat sizes auto text.
    /// </summary>
    private static double AutoSize(DefaultAppearance appearance, FieldFont font, string text, PdfRectangle inner, double lineHeight)
    {
        if (appearance.Size is double size && size > 0)
        {
            return size;
        }

        double fit = Math.Min(12, inner.Height / Math.Max(0.5, lineHeight));
        double measured = font.Measure(text, 1);
        if (measured > 0)
        {
            fit = Math.Min(fit, (inner.Width - 2) / measured);
        }

        return Math.Max(4, fit);
    }

    private static void Line(PdfContentWriter content, FieldFont font, DefaultAppearance appearance, double size, string text, double left, double baseline, double width, PdfTextAlignment alignment)
    {
        byte[] codes = FieldFont.Encode(text)!;
        double measured = font.Measure(codes, size);
        double x = left + alignment switch
        {
            PdfTextAlignment.Center => (width - measured) / 2,
            PdfTextAlignment.Right => width - measured,
            _ => 0,
        };
        content.BeginText().Font(font.ResourceName, size);
        Color(content, appearance.Color);
        content.MoveText(x, baseline).ShowText(codes).EndText();
    }

    private static void Multiline(PdfContentWriter content, FieldFont font, DefaultAppearance appearance, double size, string text, PdfRectangle inner, PdfTextAlignment alignment)
    {
        double leading = size * (font.Ascent - font.Descent) / 1000;
        double y = inner.Top - (font.Ascent / 1000 * size);
        foreach (string paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            foreach (string line in Wrap(font, paragraph, size, inner.Width - 2))
            {
                Line(content, font, appearance, size, line, inner.Left + 1, y, inner.Width - 2, alignment);
                y -= leading;
            }
        }
    }

    /// <summary>A paragraph split into lines that fit a width, between words where it can be, inside a long word where not.</summary>
    private static List<string> Wrap(FieldFont font, string paragraph, double size, double width)
    {
        List<string> lines = [];
        string rest = paragraph;
        while (rest.Length > 0)
        {
            int fits = 1;
            while (fits < rest.Length && font.Measure(rest[..(fits + 1)], size) <= width)
            {
                fits++;
            }

            if (fits < rest.Length && rest.LastIndexOf(' ', fits) is int space and > 0)
            {
                fits = space;
            }

            lines.Add(rest[..fits]);
            rest = rest[fits..].TrimStart(' ');
        }

        return lines.Count == 0 ? [""] : lines;
    }

    /// <summary>One character centred in each of <paramref name="cells"/> equal cells across the box (12.7.5.3, comb).</summary>
    private static void Comb(PdfContentWriter content, FieldFont font, DefaultAppearance appearance, double size, string text, int cells, double width, PdfRectangle inner, double lineHeight)
    {
        double cell = width / Math.Max(1, cells);
        double baseline = inner.Bottom + ((inner.Height - (lineHeight * size)) / 2) - (font.Descent / 1000 * size);
        content.BeginText().Font(font.ResourceName, size);
        Color(content, appearance.Color);
        double previous = 0;
        for (int i = 0; i < Math.Min(cells, text.Length); i++)
        {
            byte[] codes = FieldFont.Encode(text[i].ToString())!;
            double x = (i * cell) + ((cell - font.Measure(codes, size)) / 2);
            content.MoveText(x - previous, i == 0 ? baseline : 0).ShowText(codes);
            previous = x;
        }

        content.EndText();
    }

    private static void Color(PdfContentWriter content, double[]? color)
    {
        _ = color?.Length switch
        {
            1 => content.Gray(color[0]),
            3 => content.Rgb(color[0], color[1], color[2]),
            4 => content.Cmyk(color[0], color[1], color[2], color[3]),
            _ => content.Gray(0),
        };
    }
}

/// <summary>
/// The font a field's text is drawn in (Q12): the field's own font from the form's <c>/DR</c> when it is a simple font
/// with WinAnsi encoding and known widths that covers the text, else Helvetica.
/// </summary>
internal sealed class FieldFont
{
    private readonly Func<byte, double> width;

    private FieldFont(PdfName resourceName, PdfObject dictionary, Func<byte, double> width, double ascent, double descent)
    {
        ResourceName = resourceName;
        Dictionary = dictionary;
        this.width = width;
        Ascent = ascent;
        Descent = descent;
    }

    /// <summary>The name the appearance's resources give the font.</summary>
    public PdfName ResourceName { get; }

    /// <summary>The font dictionary, or a reference to it; a dictionary is copied into each appearance that uses it.</summary>
    public PdfObject Dictionary { get; }

    /// <summary>The ascent, in thousandths of the font size.</summary>
    public double Ascent { get; }

    /// <summary>The descent, negative, in thousandths of the font size.</summary>
    public double Descent { get; }

    /// <summary>The font for a field's text, or null when neither the field's font nor Helvetica can show it.</summary>
    public static FieldFont? Resolve(PdfDocument document, PdfName? name, string text)
    {
        PdfDictionary? fonts = document.Form.DefaultResources?.Get<PdfDictionary>("Font");
        if (name is not null && fonts?.Get<PdfDictionary>(name) is PdfDictionary own && Own(name, fonts[name]!, own) is FieldFont field && FieldFont.Encode(text) is not null)
        {
            return field;
        }

        if (WinAnsiEncoding.TryEncode(text) is null)
        {
            return null;
        }

        Standard14Font helvetica = Standard14Font.Get("Helvetica")!;
        PdfName resource = name is not null && fonts?.Get(name) is null ? name : PdfName.Get("EgapHelv");
        return new FieldFont(resource, AppearanceBuilder.StandardFontDictionary(helvetica), code => helvetica.Width(code), helvetica.Ascent, helvetica.Descent);
    }

    /// <summary>Encodes text in WinAnsi, or null when a character has no code.</summary>
    public static byte[]? Encode(string text)
    {
        return WinAnsiEncoding.TryEncode(text);
    }

    /// <summary>The width of codes at a size.</summary>
    public double Measure(ReadOnlySpan<byte> codes, double size)
    {
        double total = 0;
        foreach (byte code in codes)
        {
            total += width(code);
        }

        return total / 1000 * size;
    }

    /// <summary>The width of text at a size; 0 when it cannot be encoded.</summary>
    public double Measure(string text, double size)
    {
        return Encode(text) is byte[] codes ? Measure(codes, size) : 0;
    }

    /// <summary>The field's own font, when it is simple, WinAnsi encoded and has known widths.</summary>
    private static FieldFont? Own(PdfName name, PdfObject stored, PdfDictionary font)
    {
        if (font.GetName("Subtype")?.Value is not ("Type1" or "TrueType" or "MMType1"))
        {
            return null;
        }

        bool winAnsi = font.Get("Encoding") switch
        {
            PdfName encoding => encoding.Value == "WinAnsiEncoding",
            PdfDictionary encoding => encoding.GetName("BaseEncoding")?.Value == "WinAnsiEncoding" && encoding.Get("Differences") is null,
            _ => false,
        };
        if (!winAnsi)
        {
            return null;
        }

        Standard14Font? standard = Standard14Font.Get(font.GetName("BaseFont")?.Value);
        PdfDictionary? descriptor = font.Get<PdfDictionary>("FontDescriptor");
        double ascent = descriptor?.GetNumber("Ascent") is double a and > 0 ? a : standard?.Ascent ?? 800;
        double descent = descriptor?.GetNumber("Descent") is double d and < 0 ? d : standard?.Descent ?? -200;
        if (font.Get<PdfArray>("Widths") is PdfArray widths && font.GetInt("FirstChar") is long first)
        {
            double missing = descriptor?.GetNumber("MissingWidth") ?? 0;
            return new FieldFont(name, stored, code => code - first is long i and >= 0 && i < widths.Count && widths.GetNumber((int)i) is double w ? w : missing, ascent, descent);
        }

        return standard is { IsSymbolic: false } ? new FieldFont(name, stored, code => standard.Width(code), ascent, descent) : null;
    }

    public override string ToString()
    {
        return ResourceName.Value.ToString(CultureInfo.InvariantCulture);
    }
}
