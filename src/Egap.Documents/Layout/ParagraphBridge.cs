using System.Globalization;
using System.Text;

using Egami.Fonts;
using Egami.Fonts.Layout;
using Egami.Pixels;

using EgamiParagraph = Egami.Fonts.Layout.Paragraph;
using EgamiParagraphStyle = Egami.Fonts.Layout.ParagraphStyle;
using EgamiTab = Egami.Fonts.Layout.TabStop;
using EgamiTabAlignment = Egami.Fonts.Layout.TabAlignment;
using EgamiTextDirection = Egami.Fonts.Unicode.TextDirection;

namespace Egap.Documents.Layout;

/// <summary>Where a stretch of the laid-out text came from: an inline of the model and the offset in its text.</summary>
/// <param name="Start">The first UTF-16 index in the laid-out text.</param>
/// <param name="Length">The number of UTF-16 units.</param>
/// <param name="Node">The inline (a run, tab, break, note mark, field result run or object).</param>
/// <param name="NodeOffset">The offset in the node's own text where the stretch starts.</param>
internal readonly record struct InlineSpan(int Start, int Length, Inline Node, int NodeOffset);

/// <summary>A floating picture, shape or island anchored in a paragraph, placed by the page flow, not the line.</summary>
/// <param name="Anchor">The text index of its anchor.</param>
/// <param name="Node">The inline.</param>
/// <param name="Placement">Where it floats.</param>
/// <param name="Width">Its width in points.</param>
/// <param name="Height">Its height in points.</param>
internal readonly record struct FloatingObject(int Anchor, Inline Node, FloatingPlacement Placement, double Width, double Height);

/// <summary>
/// A model paragraph ready for Egami's line layout (Build Plan task 16): its visible text with the list label in front, one
/// text style per run from the effective formatting, the paragraph style (alignment, tabs, direction, fallback), and the
/// maps back from text to model nodes. Indents, spacing and the line spacing rule stay here for the page flow to apply.
/// </summary>
internal sealed class ParagraphContent
{
    public required Paragraph Source { get; init; }

    public required EgamiParagraph Text { get; init; }

    public required ResolvedParagraphFormat Format { get; init; }

    /// <summary>The list label's length at the start of the text (with its tab or space), 0 for none.</summary>
    public required int LabelLength { get; init; }

    public required IReadOnlyList<InlineSpan> Spans { get; init; }

    /// <summary>Text indices just after a page or column break.</summary>
    public required IReadOnlyList<(int Index, BreakKind Kind)> Breaks { get; init; }

    /// <summary>Inline objects by placeholder index, in text order.</summary>
    public required IReadOnlyList<Inline> InlineObjects { get; init; }

    public required IReadOnlyList<FloatingObject> Floats { get; init; }

    /// <summary>Fields whose result the layout fills in (PAGE, NUMPAGES, SECTIONPAGES), with where their result sits.</summary>
    public required IReadOnlyList<(Field Field, int Start, int Length)> PageFields { get; init; }

    /// <summary>The largest font size in the paragraph (its mark when empty), for an empty line's height.</summary>
    public required double MaxFontSize { get; init; }
}

/// <summary>Builds <see cref="ParagraphContent"/> from model paragraphs with one <see cref="FontProvider"/>.</summary>
internal sealed class ParagraphBridge(FontProvider fonts, Document document)
{
    private readonly Dictionary<string, IReadOnlyList<FontFamily>> families = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="paragraph">The paragraph.</param>
    /// <param name="pageField">The text of a page field (PAGE, NUMPAGES, SECTIONPAGES) on the page being laid out, or null to show its stored result.</param>
    public ParagraphContent Build(Paragraph paragraph, Func<Field, string?>? pageField = null)
    {
        ResolvedParagraphFormat format = paragraph.GetEffectiveFormat();
        RichTextBuilder text = new();
        List<InlineSpan> spans = [];
        List<(int, BreakKind)> breaks = [];
        List<Inline> objects = [];
        List<FloatingObject> floats = [];
        List<(Field, int, int)> pageFields = [];
        double maxSize = 0;
        Stack<(FieldStart Start, bool InCode, int ResultStart)> fields = new();
        int replacing = 0;
        TextStyle? last = null;

        int labelLength = 0;
        if (paragraph.ListLabel is { } label && label.Text.Length + 1 is int length)
        {
            TextStyle labelStyle = LabelStyle(paragraph, label);
            text.Push(labelStyle).Append(label.Text);
            switch (label.Format.Suffix)
            {
                case ListLabelSuffix.Tab:
                    text.Append("\t");
                    break;
                case ListLabelSuffix.Space:
                    text.Append(" ");
                    break;
            }

            text.Pop();
            labelLength = text.Length;
            _ = length;
        }

        Walk(paragraph.Inlines);

        if (maxSize == 0)
        {
            // An empty paragraph is as tall as its mark's font.
            maxSize = MarkSize(paragraph);
            if (text.Length == 0)
            {
                // An empty paragraph still has a line, as tall as its mark's font: a zero-width space gives Egami one.
                last = MarkStyle(paragraph);
                text.Push(last).Append("\u200B").Pop();
            }
        }

        RichText rich = text.ToRichText();
        // Text outside every span (a placeholder's character) takes the default style, which must name fonts.
        EgamiParagraphStyle paragraphStyle = ParagraphStyleOf(format, MarkStyle(paragraph));
        return new ParagraphContent
        {
            Source = paragraph,
            Text = EgamiParagraph.Create(rich, paragraphStyle),
            Format = format,
            LabelLength = labelLength,
            Spans = spans,
            Breaks = breaks,
            InlineObjects = objects,
            Floats = floats,
            PageFields = pageFields,
            MaxFontSize = maxSize,
        };

        void Walk(IEnumerable<Inline> inlines)
        {
            foreach (Inline inline in inlines)
            {
                bool inCode = (fields.Count > 0 && fields.Peek().InCode) || replacing > 0;
                if (inline.Revision?.Removes == true && inline is not (FieldStart or FieldSeparator or FieldEnd))
                {
                    // Layout shows the document with its changes accepted.
                    continue;
                }

                switch (inline)
                {
                    case FieldStart start:
                        fields.Push((start, true, text.Length));
                        break;
                    case FieldSeparator:
                        if (fields.Count > 0)
                        {
                            (FieldStart start, _, _) = fields.Pop();
                            fields.Push((start, false, text.Length));
                            if (replacing == 0 && pageField is not null && start.GetField() is { Kind: FieldKind.Page or FieldKind.NumPages or FieldKind.SectionPages } field
                                && pageField(field) is { } value)
                            {
                                // The layout's own number stands in for the stored result, in the result's first run's style.
                                ResolvedRunFormat? resultFormat = field.ResultNodes.OfType<Run>().FirstOrDefault()?.GetEffectiveFormat();
                                AddText(value, start, resultFormat is null ? last ?? MarkStyle(paragraph) : Style(paragraph, resultFormat));
                                replacing = fields.Count;
                            }
                        }

                        break;
                    case FieldEnd:
                        if (fields.Count > 0)
                        {
                            if (replacing == fields.Count)
                            {
                                replacing = 0;
                            }

                            (FieldStart start, bool code, int resultStart) = fields.Pop();
                            if (!code && start.GetField() is { Kind: FieldKind.Page or FieldKind.NumPages or FieldKind.SectionPages } field)
                            {
                                pageFields.Add((field, resultStart, text.Length - resultStart));
                            }
                        }

                        break;
                    case Run run when !inCode:
                        AddRun(run);
                        break;
                    case Tab tab when !inCode:
                        AddText("\t", tab, Style(tab.Paragraph, null));
                        break;
                    case Break breakNode when !inCode:
                        AddText("\n", breakNode, last ?? MarkStyle(paragraph));
                        if (breakNode.Kind != BreakKind.Line)
                        {
                            breaks.Add((text.Length, breakNode.Kind));
                        }

                        break;
                    case CompositeInline composite:
                        Walk(composite.Inlines);
                        break;
                    case Footnote note when !inCode:
                        {
                            TextStyle style = (last ?? MarkStyle(paragraph)) with { };
                            float size = style.Size ?? 10;
                            AddText(note.Mark, note, style with { Size = size * 0.65f, BaselineShift = -size * 0.35f });
                            break;
                        }

                    case Picture picture when !inCode:
                        AddObject(picture, picture.Floating, picture.Width, picture.Height);
                        break;
                    case Shape shape when !inCode:
                        AddObject(shape, shape.Floating, shape.Width, shape.Height);
                        break;
                    case Island island when !inCode:
                        if (island.Width > 0 && island.Height > 0)
                        {
                            AddObject(island, island.Floating, island.Width, island.Height);
                        }
                        else if (island.Text.Length > 0)
                        {
                            AddText(island.Text, island, last ?? MarkStyle(paragraph));
                        }

                        break;
                }
            }
        }

        void AddRun(Run run)
        {
            ResolvedRunFormat runFormat = run.GetEffectiveFormat();
            if (runFormat.Hidden || run.Text.Length == 0)
            {
                return;
            }

            TextStyle style = Style(paragraph, runFormat);
            maxSize = Math.Max(maxSize, runFormat.Size);
            string value = run.Text;
            if (runFormat.AllCaps)
            {
                AddText(value.ToUpperInvariant(), run, style);
            }
            else if (runFormat.SmallCaps)
            {
                // Small capitals: lowercase letters as capitals at four fifths of the size, the rest as is.
                int i = 0;
                while (i < value.Length)
                {
                    bool lower = char.IsLower(value[i]);
                    int j = i;
                    while (j < value.Length && char.IsLower(value[j]) == lower)
                    {
                        j++;
                    }

                    string piece = value[i..j];
                    AddText(lower ? piece.ToUpperInvariant() : piece, run, lower ? style with { Size = (style.Size ?? 10) * 0.8f } : style, i);
                    i = j;
                }
            }
            else
            {
                AddText(value, run, style);
            }
        }

        void AddText(string value, Inline node, TextStyle style, int nodeOffset = 0)
        {
            if (value.Length == 0)
            {
                return;
            }

            spans.Add(new InlineSpan(text.Length, value.Length, node, nodeOffset));
            text.Push(style).Append(value).Pop();
            last = style;
        }

        void AddObject(Inline node, FloatingPlacement? floating, double width, double height)
        {
            if (floating is not null)
            {
                floats.Add(new FloatingObject(text.Length, node, floating, width, height));
                return;
            }

            spans.Add(new InlineSpan(text.Length, 1, node, 0));
            objects.Add(node);
            text.AddPlaceholder(new Placeholder((float)width, (float)height, PlaceholderAlignment.Baseline));
        }
    }

    /// <summary>The text style of a run (or, without one, of the paragraph's mark).</summary>
    public TextStyle Style(Paragraph? paragraph, ResolvedRunFormat? format)
    {
        if (format is null)
        {
            return paragraph is null ? new TextStyle() : MarkStyle(paragraph);
        }

        bool bold = format.Bold;
        FontStyle fontStyle = new(Weight: bold ? 700 : 400, Slant: format.Italic ? FontSlant.Italic : FontSlant.Upright);
        float size = (float)Math.Max(1, format.Size);
        TextDecorationLines lines = TextDecorationLines.None;
        TextDecorationStyle decorationStyle = TextDecorationStyle.Solid;
        if (format.Underline != UnderlineStyle.None)
        {
            lines |= TextDecorationLines.Underline;
            decorationStyle = format.Underline switch
            {
                UnderlineStyle.Double or UnderlineStyle.WavyDouble => TextDecorationStyle.Double,
                UnderlineStyle.Dotted or UnderlineStyle.DottedHeavy => TextDecorationStyle.Dotted,
                UnderlineStyle.Dash or UnderlineStyle.DashedHeavy or UnderlineStyle.DashLong or UnderlineStyle.DashLongHeavy
                    or UnderlineStyle.DotDash or UnderlineStyle.DashDotHeavy or UnderlineStyle.DotDotDash or UnderlineStyle.DashDotDotHeavy => TextDecorationStyle.Dashed,
                UnderlineStyle.Wave or UnderlineStyle.WavyHeavy => TextDecorationStyle.Wavy,
                _ => TextDecorationStyle.Solid,
            };
        }

        if (format.Strikethrough || format.DoubleStrikethrough)
        {
            lines |= TextDecorationLines.LineThrough;
            if (format.DoubleStrikethrough && format.Underline == UnderlineStyle.None)
            {
                decorationStyle = TextDecorationStyle.Double;
            }
        }

        float shift = format.VerticalPosition switch
        {
            VerticalTextPosition.Superscript => -size * 0.33f,
            VerticalTextPosition.Subscript => size * 0.14f,
            _ => 0,
        } - (float)format.Position;
        if (format.VerticalPosition != VerticalTextPosition.Baseline)
        {
            size *= 0.65f;
        }

        Theme theme = document.Theme;
        Rgba8? background = format.Highlight != HighlightColor.None ? HighlightRgb(format.Highlight)
            : format.Shading is { } shading && shading.Fill.Kind != DocumentColorKind.Auto ? shading.Fill.Resolve(theme, default) : null;
        return new TextStyle
        {
            FontFamilies = Families(format.FontAscii, format.FontEastAsia, format.FontComplexScript, fontStyle),
            Size = size,
            FontStyle = fontStyle,
            Language = format.Language,
            Color = format.TextColor,
            Background = background,
            Decoration = lines == TextDecorationLines.None ? null : new TextDecoration
            {
                Lines = lines,
                Style = decorationStyle,
                Color = format.UnderlineColor.Kind == DocumentColorKind.Auto ? null : format.UnderlineColor.Resolve(theme, format.TextColor),
            },
            LetterSpacing = format.Spacing == 0 ? null : (float)format.Spacing,
            BaselineShift = shift == 0 ? null : shift,
        };
    }

    /// <summary>The style of a paragraph's mark: its style's run formatting with the mark's own over it.</summary>
    public TextStyle MarkStyle(Paragraph paragraph)
    {
        // The mark resolves like a run would at the end of the paragraph.
        Run probe = new(paragraph.Document, "");
        probe.Format.Apply(paragraph.MarkFormat);
        ResolvedRunFormat resolved = FormatResolver.ResolveRunIn(probe, paragraph);
        return Style(null, resolved) with { };
    }

    private static double MarkSize(Paragraph paragraph)
    {
        Run probe = new(paragraph.Document, "");
        probe.Format.Apply(paragraph.MarkFormat);
        return FormatResolver.ResolveRunIn(probe, paragraph).Size;
    }

    private TextStyle LabelStyle(Paragraph paragraph, ListLabel label)
    {
        // The label takes the mark's formatting with the list level's run formatting over it.
        Run probe = new(paragraph.Document, "");
        probe.Format.Apply(paragraph.MarkFormat);
        probe.Format.Apply(label.Format.RunFormat);
        return Style(paragraph, FormatResolver.ResolveRunIn(probe, paragraph));
    }

    private IReadOnlyList<FontFamily> Families(string ascii, string eastAsia, string complex, FontStyle style)
    {
        string key = ascii + "|" + eastAsia + "|" + complex + "|" + style.Weight.ToString(CultureInfo.InvariantCulture) + style.Slant;
        if (!families.TryGetValue(key, out IReadOnlyList<FontFamily>? list))
        {
            List<FontFamily> result = [];
            foreach (string name in (ReadOnlySpan<string>)[ascii, eastAsia, complex])
            {
                if (fonts.ResolveFamily(name, style) is { } family && !result.Contains(family))
                {
                    result.Add(family);
                }
            }

            if (result.Count == 0 && fonts.ResolveFamily("Liberation Sans", style) is { } fallback)
            {
                result.Add(fallback);
            }

            list = result;
            families[key] = list;
        }

        return list;
    }

    private EgamiParagraphStyle ParagraphStyleOf(ResolvedParagraphFormat format, TextStyle? defaultStyle)
    {
        double lineStart = format.LeftIndent + format.FirstLineIndent;
        List<EgamiTab> tabs = [];
        foreach (TabStop stop in format.TabStops)
        {
            if (stop.Alignment is TabAlignment.Clear or TabAlignment.Bar)
            {
                continue;
            }

            float position = (float)(stop.Position - lineStart);
            if (position > 0)
            {
                tabs.Add(new EgamiTab(position, stop.Alignment switch
                {
                    TabAlignment.Center => EgamiTabAlignment.Center,
                    TabAlignment.Right => EgamiTabAlignment.End,
                    TabAlignment.Decimal => EgamiTabAlignment.Decimal,
                    _ => EgamiTabAlignment.Start,
                }));
            }
        }

        // A hanging indent is a tab stop of its own in Word: the first tab after a list label reaches the text indent.
        if (format.FirstLineIndent < 0 && -format.FirstLineIndent is double hanging && !tabs.Exists(t => Math.Abs(t.Position - hanging) < 0.01))
        {
            tabs.Add(new EgamiTab((float)hanging));
        }

        tabs.Sort((a, b) => a.Position.CompareTo(b.Position));
        return new EgamiParagraphStyle
        {
            DefaultStyle = defaultStyle ?? new TextStyle(),
            Direction = format.Bidi ? EgamiTextDirection.RightToLeft : EgamiTextDirection.LeftToRight,
            Align = format.Alignment switch
            {
                ParagraphAlignment.Center => TextAlign.Center,
                ParagraphAlignment.Right => TextAlign.End,
                ParagraphAlignment.Justify or ParagraphAlignment.Distribute => TextAlign.Justify,
                _ => TextAlign.Start,
            },
            AlignLast = format.Alignment == ParagraphAlignment.Distribute ? TextAlignLast.Justify : TextAlignLast.Auto,
            TabSize = (float)document.Settings.DefaultTabStop,
            TabStops = tabs,
            Hyphen = null,
            Fallback = fonts.Fallback,
        };
    }

    private static Rgba8 HighlightRgb(HighlightColor color)
    {
        return color switch
        {
            HighlightColor.Black => new Rgba8(0, 0, 0, 255),
            HighlightColor.Blue => new Rgba8(0, 0, 255, 255),
            HighlightColor.Cyan => new Rgba8(0, 255, 255, 255),
            HighlightColor.Green => new Rgba8(0, 255, 0, 255),
            HighlightColor.Magenta => new Rgba8(255, 0, 255, 255),
            HighlightColor.Red => new Rgba8(255, 0, 0, 255),
            HighlightColor.White => new Rgba8(255, 255, 255, 255),
            HighlightColor.DarkBlue => new Rgba8(0, 0, 128, 255),
            HighlightColor.DarkCyan => new Rgba8(0, 128, 128, 255),
            HighlightColor.DarkGreen => new Rgba8(0, 128, 0, 255),
            HighlightColor.DarkMagenta => new Rgba8(128, 0, 128, 255),
            HighlightColor.DarkRed => new Rgba8(128, 0, 0, 255),
            HighlightColor.DarkYellow => new Rgba8(128, 128, 0, 255),
            HighlightColor.DarkGray => new Rgba8(128, 128, 128, 255),
            HighlightColor.LightGray => new Rgba8(192, 192, 192, 255),
            _ => new Rgba8(255, 255, 0, 255),
        };
    }
}
