using System.Text;

using Egami.Pixels;

using Egap.Pdf;


namespace Egap.Documents.Pdf;

/// <summary>A glyph's character formatting as the model takes it: the mapped family, the size to the half point, and the rest.</summary>
internal readonly record struct GlyphStyle(string Family, double Size, bool Bold, bool Italic, Rgba8? Color, VerticalTextPosition Position, bool Underline,
    bool Strike)
{
    /// <summary>Gives <paramref name="format"/> every property of this style, so a built-in style's own look does not show through.</summary>
    public void ApplyTo(RunFormat format)
    {
        format.FontName = Family.Length == 0 ? null : Family;
        format.ThemeFontAscii = null;
        format.ThemeFontHighAnsi = null;
        format.Size = Size;
        format.SizeComplexScript = Size;
        format.Bold = Bold;
        format.Italic = Italic;
        format.Color = Color is Rgba8 rgb ? DocumentColor.FromRgb(rgb) : DocumentColor.Auto;
        format.Underline = UnderlineStyle.None;
        format.Strikethrough = false;
    }

    /// <summary>Gives <paramref name="format"/> the properties of this style that differ from <paramref name="based"/>, the paragraph style's.</summary>
    public void ApplyDifferences(RunFormat format, GlyphStyle based)
    {
        if (Family != based.Family && Family.Length > 0)
        {
            format.FontName = Family;
        }

        if (Math.Abs(Size - based.Size) > 0.01)
        {
            format.Size = Size;
            format.SizeComplexScript = Size;
        }

        if (Bold != based.Bold)
        {
            format.Bold = Bold;
        }

        if (Italic != based.Italic)
        {
            format.Italic = Italic;
        }

        if (Color != based.Color)
        {
            format.Color = Color is Rgba8 rgb ? DocumentColor.FromRgb(rgb) : DocumentColor.Auto;
        }

        if (Position != VerticalTextPosition.Baseline)
        {
            format.VerticalPosition = Position;
        }

        if (Underline)
        {
            format.Underline = UnderlineStyle.Single;
        }

        if (Strike)
        {
            format.Strikethrough = true;
        }
    }
}

/// <summary>
/// Writes the text of lines into paragraphs as runs (design Q6): consecutive glyphs with the same formatting share a run,
/// words broken at a line end with a hyphen are joined, raised and lowered glyphs become superscript and subscript, drawn
/// lines under or through text become underline and strikethrough, and a footnote's marker becomes the footnote.
/// </summary>
internal sealed class RunWriter(Document document, Func<PageGlyph, BlockDraft?> noteAt, Action<Paragraph, BlockDraft> writeNote, Func<PageGlyph, bool>? swallowed = null)
{
    private List<Decoration> decorations = [];
    private List<PageAnnotation> links = [];

    /// <summary>A stretch of text in one style, inside one link or none.</summary>
    private sealed class Piece(string text, GlyphStyle style, PageAnnotation? link)
    {
        public StringBuilder Text { get; } = new(text);

        public GlyphStyle Style { get; } = style;

        public PageAnnotation? Link { get; } = link;
    }

    /// <summary>Called for every hyperlink written, with the link annotation it comes from.</summary>
    public Action<Hyperlink, PageAnnotation>? OnLink { get; set; }

    /// <summary>Words to write as a field instead of text, with the field's code: a page number as PAGE in a header.</summary>
    public Func<PageWord, string?>? Fields { get; set; }

    /// <summary>A drawn line under or through text: its left and right ends and its y on the displayed page.</summary>
    private readonly record struct Decoration(double Left, double Right, double Y);

    /// <summary>The page whose drawn lines are underlines and strikes from now on.</summary>
    public void SetPage(PageContent content)
    {
        decorations = [];
        foreach (PagePath path in content.Paths)
        {
            PageBox b = path.Bounds;
            if (b.Height <= 2.5 && b.Width >= 2 && (path.Filled || path.Stroked))
            {
                decorations.Add(new Decoration(b.Left, b.Right, (b.Top + b.Bottom) / 2));
            }
        }

        links = [.. content.Annotations.Where(a => a.Subtype == "Link" && (a.Uri is not null || a.TargetPage is not null))];
    }

    private PageAnnotation? LinkAt(PageGlyph glyph)
    {
        foreach (PageAnnotation link in links)
        {
            if (link.Box.Contains(glyph.Box.CenterX, glyph.Box.CenterY))
            {
                return link;
            }
        }

        return null;
    }

    /// <summary>The style most of the glyphs of some lines are set in, weighted by characters.</summary>
    public static GlyphStyle Dominant(IEnumerable<PageLine> lines)
    {
        Dictionary<GlyphStyle, int> counts = [];
        foreach (PageLine line in lines)
        {
            foreach (PageGlyph glyph in line.Words.SelectMany(w => w.Glyphs))
            {
                GlyphStyle style = Plain(glyph, line);
                counts[style] = counts.GetValueOrDefault(style) + glyph.Text.Length;
            }
        }

        return counts.Count == 0 ? new GlyphStyle("", 11, false, false, null, VerticalTextPosition.Baseline, false, false) : counts.MaxBy(c => c.Value).Key;
    }

    /// <summary>A glyph's style without decorations or position: what a paragraph style can hold.</summary>
    public static GlyphStyle Plain(PageGlyph glyph, PageLine line)
    {
        double size = Raised(glyph, line) != VerticalTextPosition.Baseline ? line.Size : glyph.Size;
        return new GlyphStyle(FontNames.Map(FontTraits.Family(glyph.Font)), Math.Round(size * 2) / 2, FontTraits.IsBold(glyph), FontTraits.IsItalic(glyph.Font),
            ModelColors.Rgb(glyph.Color), VerticalTextPosition.Baseline, false, false);
    }

    /// <summary>
    /// Writes <paramref name="lines"/> at the end of <paramref name="paragraph"/>, after a space or joining a broken word when
    /// it already holds text. <paramref name="skip"/> leaves out the first line's first words (a list marker); with
    /// <paramref name="breaks"/> lines are kept apart by line breaks and lead with the spaces their indent stands for (code).
    /// </summary>
    public void Write(Paragraph paragraph, IReadOnlyList<PageLine> lines, GlyphStyle based, int skip = 0, bool breaks = false)
    {
        List<Piece> runs = [];
        bool joining = paragraph.Inlines.Count > 0;
        double left = lines.Count == 0 ? 0 : lines.Min(l => l.Box.Left);
        for (int i = 0; i < lines.Count; i++)
        {
            PageLine line = lines[i];
            if (i > 0 && breaks)
            {
                Flush(paragraph, runs, based);
                paragraph.AddBreak();
            }

            for (int w = i == 0 ? skip : 0; w < line.Words.Count; w++)
            {
                PageWord word = line.Words[w];
                bool first = w == (i == 0 ? skip : 0);
                if (first && i == 0)
                {
                    if (joining)
                    {
                        Separate(paragraph, runs, word);
                    }
                }
                else if (first && !breaks && Paragraphs.Hyphenated(lines[i - 1], line))
                {
                    // The broken word's hyphen goes; the rest of the word joins it.
                    if (runs.Count > 0 && runs[^1].Text.Length > 0)
                    {
                        runs[^1].Text.Length--;
                    }
                }
                else if (first && breaks)
                {
                    double space = Math.Max(line.Words[0].Glyphs[0].SpaceWidth, line.Size * 0.5);
                    Append(runs, new string(' ', (int)Math.Round((line.Box.Left - left) / space)), Plain(word.Glyphs[0], line));
                }
                else
                {
                    PageAnnotation? within = runs.Count > 0 && runs[^1].Link is PageAnnotation open && word.Glyphs.Count(g => LinkAt(g) == open) * 2 > word.Glyphs.Count ? open : null;
                    Append(runs, " ", Space(runs, word, line), within);
                }

                if (Fields?.Invoke(word) is string code)
                {
                    // The field stands for the number; brackets or dashes around it stay text.
                    string text = word.Text;
                    int start = text.TakeWhile(c => !char.IsLetterOrDigit(c)).Count();
                    int end = text.Length - text.Reverse().TakeWhile(c => !char.IsLetterOrDigit(c)).Count();
                    GlyphStyle style = Style(word.Glyphs[0], line);
                    Append(runs, text[..start], style);
                    Flush(paragraph, runs, based);
                    Field field = paragraph.AddField(code, text[start..end]);
                    foreach (Run result in field.ResultNodes.OfType<Run>())
                    {
                        style.ApplyDifferences(result.Format, based);
                    }

                    Append(runs, text[end..], style);
                    continue;
                }

                // A link takes whole words: those with most of their glyphs under it.
                PageAnnotation? link = word.Glyphs.Select(LinkAt).GroupBy(l => l).MaxBy(g => g.Count())?.Key;
                foreach (PageGlyph glyph in word.Glyphs)
                {
                    if (noteAt(glyph) is BlockDraft note)
                    {
                        Flush(paragraph, runs, based);
                        writeNote(paragraph, note);
                        continue;
                    }

                    // The rest of a marker of several glyphs ("12") went with its first.
                    if (swallowed?.Invoke(glyph) == true)
                    {
                        continue;
                    }

                    Append(runs, glyph.Text, Style(glyph, line), link);
                }
            }
        }

        Flush(paragraph, runs, based);
    }

    /// <summary>Joins a paragraph that carries on another: a space, or nothing when the other ends in a broken word.</summary>
    private static void Separate(Paragraph paragraph, List<Piece> runs, PageWord next)
    {
        if (paragraph.Inlines.Count > 0 && paragraph.Inlines[^1] is Run last && last.Text.Length > 1 && last.Text[^1] is '-' or '\u00AD'
            && char.IsLetter(last.Text[^2]) && char.IsLower(next.Text[0]))
        {
            last.Text = last.Text[..^1];
            return;
        }

        if (paragraph.Inlines.Count > 0 && paragraph.Inlines[^1] is Run run)
        {
            run.Text += " ";
        }
        else
        {
            runs.Add(new Piece(" ", Plain(next.Glyphs[0], new PageLine([next])), null));
        }
    }

    /// <summary>The style of the space before a word: the previous text's, underlined only when both sides are, never raised.</summary>
    private static GlyphStyle Space(List<Piece> runs, PageWord next, PageLine line)
    {
        if (runs.Count == 0)
        {
            return Plain(next.Glyphs[0], line);
        }

        GlyphStyle before = runs[^1].Style;
        return before with { Position = VerticalTextPosition.Baseline, Underline = before.Underline && line.Words.Count > 0, Strike = before.Strike };
    }

    private GlyphStyle Style(PageGlyph glyph, PageLine line)
    {
        GlyphStyle plain = Plain(glyph, line);
        VerticalTextPosition position = Raised(glyph, line);
        double size = line.Size;
        double x = (glyph.Box.Left + glyph.Box.Right) / 2;
        bool under = false, strike = false;
        foreach (Decoration d in decorations)
        {
            if (x < d.Left || x > d.Right || d.Right - d.Left > line.Box.Width + size)
            {
                continue;
            }

            double below = d.Y - line.Baseline;
            under |= below >= -0.05 * size && below <= 0.35 * size;
            strike |= below <= -0.15 * size && below >= -0.5 * size;
        }

        return plain with { Position = position, Underline = under, Strike = strike };
    }

    /// <summary>Superscript for a smaller glyph set above the line's baseline, subscript for one below it.</summary>
    private static VerticalTextPosition Raised(PageGlyph glyph, PageLine line)
    {
        double size = line.Size;
        if (glyph.IsSpace || glyph.Size >= size * 0.8 || !line.IsHorizontal)
        {
            return VerticalTextPosition.Baseline;
        }

        return glyph.Origin.Y < line.Baseline - (size * 0.2) ? VerticalTextPosition.Superscript
            : glyph.Origin.Y > line.Baseline + (size * 0.1) ? VerticalTextPosition.Subscript : VerticalTextPosition.Baseline;
    }

    private static void Append(List<Piece> runs, string text, GlyphStyle style, PageAnnotation? link = null)
    {
        // Control characters and lone surrogates a font maps glyphs to have no place in a document's text.
        if (text.Any(c => (char.IsControl(c) && c != '	') || char.IsSurrogate(c) || c is '￾' or '￿'))
        {
            text = string.Concat(text.Where((c, i) => !(char.IsControl(c) && c != '	') && c is not ('￾' or '￿')
                && !(char.IsHighSurrogate(c) && (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])))
                && !(char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(text[i - 1])))));
        }

        if (text.Length == 0)
        {
            return;
        }

        if (runs.Count > 0 && runs[^1].Style == style && runs[^1].Link == link)
        {
            runs[^1].Text.Append(text);
        }
        else
        {
            runs.Add(new Piece(text, style, link));
        }
    }

    private void Flush(Paragraph paragraph, List<Piece> runs, GlyphStyle based)
    {
        foreach (Piece piece in runs)
        {
            if (piece.Text.Length == 0)
            {
                continue;
            }

            Run run = new(document, piece.Text.ToString());
            piece.Style.ApplyDifferences(run.Format, based);
            if (piece.Link is not PageAnnotation link)
            {
                paragraph.Inlines.Add(run);
                continue;
            }

            // Text under one link annotation is one hyperlink, even across style changes.
            if (paragraph.Inlines.Count == 0 || paragraph.Inlines[^1] is not Hyperlink open || !ReferenceEquals(openLink, link))
            {
                open = new Hyperlink(document);
                if (link.Uri is string uri)
                {
                    open.Address = ModelBuilder.Clean(uri);
                }

                paragraph.Inlines.Add(open);
                openLink = link;
                OnLink?.Invoke(open, link);
            }

            open.Inlines.Add(run);
        }

        runs.Clear();
    }

    private PageAnnotation? openLink;
}
