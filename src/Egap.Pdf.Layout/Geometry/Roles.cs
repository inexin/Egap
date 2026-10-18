using System.Text.RegularExpressions;

namespace Egap.Pdf.Layout;

/// <summary>
/// Roles for the paragraphs of a whole document (design Q4, Build Plan task 6), on top of what tags already gave. Body text
/// is the style most text is set in. Page headers, footers and page numbers are short paragraphs at the top or foot of
/// pages that come back in the same place with the same or counting text. Footnotes are smaller paragraphs at the page foot
/// that start with a marker a raised, smaller marker in the text above points to. Captions start with a label such as
/// Figure or Table and a number. Headings are short lines in a larger or bolder style than body text, ranked into levels by
/// size and weight across the document, helped by the outline and by section numbers. List items start with a marker that
/// stands apart, their level from where the marker sits. Quotes are set in on both sides; code is monospaced where body text
/// is not. Everything else is text.
/// </summary>
internal static partial class Roles
{
    /// <summary>The share of a page's height at its top and foot where headers, footers and page numbers are looked for.</summary>
    private const double Margin = 0.12;

    /// <summary>A note between a block's lines and a footnote: the raised marker glyph in the text and the note it points to.</summary>
    internal sealed record NoteLink(PageGlyph Marker, BlockDraft Note, IReadOnlyList<PageGlyph>? Rest = null);

    /// <summary>Gives roles to the untagged paragraphs of <paramref name="pages"/>, in place; returns the footnote links found.</summary>
    public static List<NoteLink> Assign(IReadOnlyList<PageDraft> pages, IReadOnlyList<OutlineEntry> outline)
    {
        List<(PageDraft Page, BlockDraft Block)> all = [.. pages.SelectMany(p => p.Blocks.Where(b => b.Tag is null && !b.Fixed).Select(b => (p, b)))];
        Style.Cache = new(ReferenceEqualityComparer.Instance);
        try
        {
            List<NoteLink> links = all.Count == 0 ? [] : Assign(pages, outline, all);
            LinkNotes(pages, links);
            return links;
        }
        finally
        {
            Style.Cache = null;
        }
    }

    private static List<NoteLink> Assign(IReadOnlyList<PageDraft> pages, IReadOnlyList<OutlineEntry> outline, List<(PageDraft Page, BlockDraft Block)> all)
    {
        Style body = BodyStyle(all.Select(a => a.Block));
        Furniture(pages, all);
        List<NoteLink> links = Footnotes(pages, body);
        foreach ((PageDraft page, BlockDraft block) in all.Where(a => a.Block.Role == LayoutRole.Text))
        {
            if (IsCaption(block))
            {
                block.Role = LayoutRole.Caption;
            }
            else if (IsCode(block, body))
            {
                block.Role = LayoutRole.Code;
            }
        }

        // Headings before list items, so a numbered heading ("2.1 Methods" in bold) stays a heading.
        Headings(pages, all, body, outline);
        foreach ((PageDraft page, BlockDraft block) in all.Where(a => a.Block.Role == LayoutRole.Text))
        {
            if (ListMarker(block) is string marker)
            {
                block.Role = LayoutRole.ListItem;
                block.Marker = marker;
            }
        }

        Lists(pages);
        foreach ((PageDraft page, BlockDraft block) in all.Where(a => a.Block.Role == LayoutRole.Text))
        {
            if (IsQuote(block, page, body))
            {
                block.Role = LayoutRole.Quote;
            }
        }

        return links;
    }

    /// <summary>The style most of the text is set in: its size, family and weight, by glyph count.</summary>
    internal static Style BodyStyle(IEnumerable<BlockDraft> blocks)
    {
        Dictionary<Style, int> counts = [];
        foreach (PageGlyph glyph in blocks.SelectMany(b => b.Lines).SelectMany(l => l.Words).SelectMany(w => w.Glyphs))
        {
            Style style = Style.Of(glyph);
            counts[style] = counts.GetValueOrDefault(style) + 1;
        }

        return counts.Count == 0 ? new Style(10, "", false) : counts.MaxBy(c => c.Value).Key;
    }

    /// <summary>A text style: size to the half point, family, and whether it is bold.</summary>
    internal readonly record struct Style(double Size, string Family, bool Bold)
    {
        public static Style Of(PageGlyph glyph)
        {
            return new(Math.Round(glyph.Size * 2) / 2, FontTraits.Family(glyph.Font), FontTraits.IsBold(glyph));
        }

        /// <summary>Each block's style while roles are given: lines do not change then, and every rule asks.</summary>
        [ThreadStatic]
        internal static Dictionary<BlockDraft, Style>? Cache;

        /// <summary>The style most of a block's glyphs have.</summary>
        public static Style Of(BlockDraft block)
        {
            if (Cache is null)
            {
                return Compute(block);
            }

            if (!Cache.TryGetValue(block, out Style style))
            {
                Cache[block] = style = Compute(block);
            }

            return style;
        }

        private static Style Compute(BlockDraft block)
        {
            Dictionary<Style, int> counts = [];
            foreach (PageLine line in block.Lines)
            {
                foreach (PageWord word in line.Words)
                {
                    foreach (PageGlyph glyph in word.Glyphs)
                    {
                        if (glyph.IsSpace)
                        {
                            continue;
                        }

                        Style style = Of(glyph);
                        counts[style] = counts.GetValueOrDefault(style) + 1;
                    }
                }
            }

            // The first style seen wins a tie, as grouping in order would give.
            return counts.Count == 0 ? new(10, "", false) : counts.MaxBy(c => c.Value).Key;
        }
    }

    /// <summary>
    /// Page headers, footers and numbers: paragraphs of one or two lines in the top or bottom margin band that come back on
    /// two pages or more, at most a size apart in height, with the same text once digits are set aside; or, on any page, a
    /// margin paragraph that is only a page number.
    /// </summary>
    private static void Furniture(IReadOnlyList<PageDraft> pages, List<(PageDraft Page, BlockDraft Block)> all)
    {
        List<(PageDraft Page, BlockDraft Block, bool Top)> candidates = [];
        foreach ((PageDraft page, BlockDraft block) in all)
        {
            double height = page.Content.Height;
            if (block.Lines.Count > 2)
            {
                continue;
            }

            if (block.Box.Bottom < height * Margin)
            {
                candidates.Add((page, block, true));
            }
            else if (block.Box.Top > height * (1 - Margin))
            {
                candidates.Add((page, block, false));
            }
        }

        foreach ((PageDraft page, BlockDraft block, bool top) in candidates)
        {
            string key = Normalized(block.Text);
            int pagesWith = candidates.Where(c => c.Top == top && c.Page != page && Math.Abs(c.Block.Box.CenterY - block.Box.CenterY) <= block.Lines[0].Size
                && Normalized(c.Block.Text) == key).Select(c => c.Page).Distinct().Count();
            bool number = PageNumber().IsMatch(block.Text.Trim());
            if (pagesWith >= 1 || (number && block.Lines.Count == 1))
            {
                block.Role = number ? LayoutRole.PageNumber : top ? LayoutRole.PageHeader : LayoutRole.PageFooter;
                block.Confidence = pagesWith >= 1 ? 0.95 : 0.7;
            }
        }
    }

    /// <summary>Text with every run of digits and roman numerals as one sign, so page-counting text compares equal.</summary>
    private static string Normalized(string text)
    {
        return Digits().Replace(text.Trim(), "#");
    }

    /// <summary>
    /// Footnotes: paragraphs below the body at the page foot, smaller than body text, starting with a marker (a number, letter
    /// or symbol) that a raised, smaller glyph in the page's text repeats. Each note links to its marker.
    /// </summary>
    private static List<NoteLink> Footnotes(IReadOnlyList<PageDraft> pages, Style body)
    {
        List<NoteLink> links = [];
        foreach (PageDraft page in pages)
        {
            List<BlockDraft> text = [.. page.Blocks.Where(b => b.Tag is null && b.Role == LayoutRole.Text)];
            List<(PageGlyph Glyph, BlockDraft Block)> raised = [.. text.SelectMany(b => b.Lines.SelectMany(l => Raised(l).Select(g => (g, b))))];
            foreach (BlockDraft block in text.Where(b => b.Box.Top > page.Content.Height * 0.5 && b.Lines[0].Size < body.Size * 0.95))
            {
                string first = block.Lines[0].Words[0].Text.TrimStart('(', '[');
                string marker = new([.. first.TakeWhile(c => char.IsLetterOrDigit(c) || "*\u2020\u2021\u00A7\u00B6".Contains(c, StringComparison.Ordinal))]);
                if (marker.Length == 0 || marker.Length > 3)
                {
                    continue;
                }

                // No body text below the note: notes sit at the foot.
                bool atFoot = !text.Any(b => b != block && b.Box.Top > block.Box.Bottom && b.Lines[0].Size >= body.Size * 0.95);
                (PageGlyph Glyph, BlockDraft Block) reference = raised.FirstOrDefault(r => r.Glyph.Text == marker && r.Block != block && r.Block.Box.Bottom <= block.Box.Top);
                bool numbered = char.IsDigit(marker[0]) || marker.Length == 1 || block.Lines[0].Words[0].Text.StartsWith('(');
                if (atFoot && (reference.Glyph is not null || numbered))
                {
                    block.Role = LayoutRole.Footnote;
                    block.Marker = marker;
                    block.Confidence = reference.Glyph is not null ? 0.9 : 0.6;
                    if (reference.Glyph is not null)
                    {
                        links.Add(new NoteLink(reference.Glyph, block));
                    }
                }
            }
        }

        return links;
    }

    /// <summary>
    /// Notes a page's tags (or a region detector) call notes but no marker links yet: each note's first word is its marker,
    /// linked to the first raised glyph in the page's other text that repeats it.
    /// </summary>
    private static void LinkNotes(IReadOnlyList<PageDraft> pages, List<NoteLink> links)
    {
        HashSet<BlockDraft> linked = new(links.Select(l => l.Note), ReferenceEqualityComparer.Instance);
        HashSet<PageGlyph> used = new(links.Select(l => l.Marker), ReferenceEqualityComparer.Instance);
        foreach (PageDraft page in pages)
        {
            List<BlockDraft> notes = [.. page.Blocks.Where(b => b.Role == LayoutRole.Footnote && !linked.Contains(b) && b.Lines.Count > 0 && b.Lines[0].Words.Count > 0)];
            if (notes.Count == 0)
            {
                continue;
            }

            // Raised glyphs side by side in a word make one marker ("12").
            List<List<PageGlyph>> raised = [];
            // In reading order, table cells included: a note's marker can stand in a cell.
            IEnumerable<(int Order, IEnumerable<PageLine> Lines)> items = page.Blocks
                .Where(b => b.Role is not (LayoutRole.Footnote or LayoutRole.PageHeader or LayoutRole.PageFooter or LayoutRole.PageNumber))
                .Select(b => (b.Order, (IEnumerable<PageLine>)b.Lines))
                .Concat(page.Tables.Select(t => (t.Order, t.Cells.OrderBy(c => c.Row).ThenBy(c => c.Column).SelectMany(c => c.Lines))));
            foreach (PageLine line in items.OrderBy(i => i.Order).SelectMany(i => i.Lines))
            {
                HashSet<PageGlyph> up = new(Raised(line), ReferenceEqualityComparer.Instance);
                foreach (PageWord word in line.Words)
                {
                    List<PageGlyph>? run = null;
                    foreach (PageGlyph glyph in word.Glyphs)
                    {
                        if (!up.Contains(glyph))
                        {
                            run = null;
                            continue;
                        }

                        if (run is null)
                        {
                            raised.Add(run = []);
                        }

                        run.Add(glyph);
                    }
                }
            }

            foreach (BlockDraft note in notes)
            {
                string first = note.Lines[0].Words[0].Text.TrimStart('(', '[');
                string marker = new([.. first.TakeWhile(c => char.IsLetterOrDigit(c) || "*†‡§¶".Contains(c, StringComparison.Ordinal))]);
                if (marker.Length is 0 or > 3)
                {
                    continue;
                }

                List<PageGlyph>? reference = raised.FirstOrDefault(r => string.Concat(r.Select(g => g.Text)) == marker && !used.Contains(r[0]));
                if (reference is not null)
                {
                    note.Marker = marker;
                    used.Add(reference[0]);
                    links.Add(new NoteLink(reference[0], note, reference.Count > 1 ? reference[1..] : null));
                }
            }
        }
    }

    /// <summary>A line's raised glyphs: smaller than the line's text and set above its baseline, as note markers are.</summary>
    internal static IEnumerable<PageGlyph> Raised(PageLine line)
    {
        double size = line.Size;
        return line.Words.SelectMany(w => w.Glyphs).Where(g => !g.IsSpace && g.Size < size * 0.8 && g.Origin.Y < line.Baseline - (size * 0.2));
    }

    /// <summary>Whether a short paragraph starts with a caption label and a number: "Figure 3", "Table 2:", "Fig. 1.4", "Listing 1".</summary>
    private static bool IsCaption(BlockDraft block)
    {
        return block.Lines.Count <= 4 && CaptionLabel().IsMatch(block.Text);
    }

    /// <summary>Whether a block is set in a monospaced font where body text is not.</summary>
    private static bool IsCode(BlockDraft block, Style body)
    {
        return !FontTraits.IsMonospace(block.Lines[0].Words[0].Font) || body.Family.Length == 0 ? false
            : block.Lines.All(l => FontTraits.IsMonospace(l.Words[0].Font)) && !MonoFamily(body);

        static bool MonoFamily(Style style)
        {
            string family = style.Family.ToLowerInvariant();
            return family.Contains("mono", StringComparison.Ordinal) || family.Contains("courier", StringComparison.Ordinal);
        }
    }

    /// <summary>A list item's marker: the paragraph's first word when it is a marker.</summary>
    private static string? ListMarker(BlockDraft block)
    {
        PageLine first = block.Lines[0];
        if (first.Words.Count < 2)
        {
            return null;
        }

        // A marker word starts an item; so does a single letter set apart by a wide gap, which is how a bullet drawn from a
        // symbol font often reads ("v" for a Wingdings diamond).
        PageWord word = first.Words[0];
        double gap = first.Words[1].Box.Left - word.Box.Right;
        bool letter = word.Text.Length == 1 && char.IsLetter(word.Text[0]) && gap > first.Size;
        if (!BlockBuilder.IsMarker(word) && !letter)
        {
            return null;
        }

        // A section number followed by a run-in heading in another style ("3.2. Glycemic Control. Glycemic ...") starts a
        // paragraph of text, not a list item.
        bool sectionNumber = word.Text.TrimEnd('.').Contains('.', StringComparison.Ordinal);
        Style paragraph = Style.Of(block);
        bool runIn = sectionNumber && block.Lines.Count > 1 && Style.Of(first.Words[1].Glyphs[0]) is var next
            && (next.Bold != paragraph.Bold || FontTraits.IsItalic(first.Words[1].Glyphs[0].Font) != FontTraits.IsItalic(first.Words[^1].Glyphs[0].Font));
        return runIn ? null : word.Text;
    }

    /// <summary>List levels: within each run of list items on a page, markers further right are deeper, a level per marker position.</summary>
    private static void Lists(IReadOnlyList<PageDraft> pages)
    {
        foreach (PageDraft page in pages)
        {
            List<BlockDraft> run = [];
            foreach (BlockDraft? block in page.Blocks.OrderBy(b => b.Order).Append(null))
            {
                if (block is { Role: LayoutRole.ListItem })
                {
                    run.Add(block);
                    continue;
                }

                if (run.Count > 0)
                {
                    List<double> positions = [];
                    foreach (double left in run.Select(b => b.Lines[0].Box.Left).Order())
                    {
                        if (positions.Count == 0 || left - positions[^1] > run[0].Lines[0].Size)
                        {
                            positions.Add(left);
                        }
                    }

                    foreach (BlockDraft item in run)
                    {
                        item.Level = positions.FindLastIndex(p => item.Lines[0].Box.Left >= p - (item.Lines[0].Size * 0.5)) + 1;
                    }

                    run.Clear();
                }
            }
        }
    }

    /// <summary>
    /// Headings: paragraphs of up to three lines in a style larger than body text, or bold at body size, that do not read like a
    /// sentence of prose. Levels rank their styles across the document, largest and boldest first; an outline entry with the
    /// same text, or a section number such as 2.1, overrides the level. The one largest heading near the top of the first page,
    /// in a style used nowhere else, is the title.
    /// </summary>
    private static void Headings(IReadOnlyList<PageDraft> pages, List<(PageDraft Page, BlockDraft Block)> all, Style body, IReadOnlyList<OutlineEntry> outline)
    {
        List<(PageDraft Page, BlockDraft Block, Style Style)> headings = [];
        foreach ((PageDraft page, BlockDraft block) in all.Where(a => a.Block.Role == LayoutRole.Text))
        {
            Style style = Style.Of(block);
            string text = block.Text.Trim();
            int words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            bool prose = (text.EndsWith('.') && (words > 8 || BlockBuilder.IsMarker(block.Lines[0].Words[0]))) || words > 30;
            bool larger = style.Size >= body.Size * 1.15 || (style.Size > body.Size * 1.05 && style.Family != body.Family);
            bool bolder = style.Bold && !body.Bold && style.Size >= body.Size * 0.85 && words <= 15;
            bool numbered = SectionNumber().IsMatch(text) && words <= 12 && block.Lines.Count == 1 && style != body;
            bool apart = block.Lines.Count == 1 && words <= 10 && style.Family != body.Family && style.Size >= body.Size * 0.85
                && !text.EndsWith('.') && FollowedByBody(page, block, body);
            larger |= numbered || apart;
            bool outlined = outline.Any(o => o.Page == page.Content.Index && Same(o.Title, text));
            bool bullet = block.Lines[0].Words[0].Text.Length == 1 && BlockBuilder.IsMarker(block.Lines[0].Words[0]);
            if (block.Lines.Count <= 3 && (larger || bolder || outlined) && !prose && !bullet)
            {
                headings.Add((page, block, style));
            }
        }

        List<(double Size, bool Bold)> ranks = [.. headings.Select(h => (h.Style.Size, h.Style.Bold)).Distinct().OrderByDescending(s => s.Size).ThenByDescending(s => s.Bold)];
        foreach ((PageDraft page, BlockDraft block, Style style) in headings)
        {
            block.Role = LayoutRole.Heading;
            block.Level = Math.Min(6, ranks.IndexOf((style.Size, style.Bold)) + 1);
            block.Confidence = 0.8;
            string text = block.Text.Trim();
            if (outline.FirstOrDefault(o => o.Page == page.Content.Index && Same(o.Title, text)) is OutlineEntry entry)
            {
                block.Level = Math.Min(6, entry.Level + 1);
                block.Confidence = 0.95;
            }
            else if (SectionNumber().Match(text) is { Success: true } number)
            {
                block.Level = Math.Min(6, number.Groups[1].Value.Count(c => c == '.') + 1);
            }
        }

        // The title: the first page's largest heading near its top, in a style no other heading has, centred or at least 1.8
        // times the body size.
        if (headings.Where(h => h.Page == pages[0] && h.Block.Box.Top < h.Page.Content.Height / 3).OrderByDescending(h => h.Style.Size).FirstOrDefault() is { Block: not null } top
            && headings.Count(h => h.Style == top.Style) == 1 && (headings.Count == 1 || top.Style.Size > headings.Where(h => h.Block != top.Block).Max(h => h.Style.Size))
            && (top.Style.Size >= body.Size * 1.8 || Centred(top.Block, top.Page)))
        {
            top.Block.Role = LayoutRole.Title;
            top.Block.Level = 0;
        }

        // With a title, the headings below it start at level 1.
        int least = headings.Where(h => h.Block.Role == LayoutRole.Heading).Select(h => h.Block.Level).DefaultIfEmpty(1).Min();
        if (least > 1 && headings.Any(h => h.Block.Role == LayoutRole.Title))
        {
            foreach ((_, BlockDraft block, _) in headings.Where(h => h.Block.Role == LayoutRole.Heading && h.Block.Confidence < 0.95))
            {
                block.Level -= least - 1;
            }
        }
    }

    /// <summary>Whether the next block in reading order on the page is set in the body style, below this one.</summary>
    private static bool FollowedByBody(PageDraft page, BlockDraft block, Style body)
    {
        BlockDraft? next = page.Blocks.Where(b => b.Order > block.Order || (b.Order == block.Order && b.Box.Top > block.Box.Bottom)).OrderBy(b => b.Order).ThenBy(b => b.Box.Top)
            .FirstOrDefault(b => b != block);
        return next is not null && next.Box.Top >= block.Box.Bottom - 1 && Style.Of(next).Family == body.Family;
    }

    /// <summary>Whether a block sits in the middle of the page, its margins on either side within two text sizes of each other.</summary>
    private static bool Centred(BlockDraft block, PageDraft page)
    {
        double left = block.Box.Left, right = page.Content.Width - block.Box.Right;
        return Math.Abs(left - right) < block.Lines[0].Size * 2 && left > block.Lines[0].Size * 4;
    }

    private static bool Same(string a, string b)
    {
        return string.Equals(Collapse(a), Collapse(b), StringComparison.OrdinalIgnoreCase);

        static string Collapse(string s) => new([.. s.Where(char.IsLetterOrDigit)]);
    }

    /// <summary>
    /// Whether a paragraph is set in on both sides within its column, or is set apart in a style of its own and centred: a
    /// block quote or a pull quote.
    /// </summary>
    private static bool IsQuote(BlockDraft block, PageDraft page, Style body)
    {
        double size = block.Lines[0].Size;
        List<BlockDraft> text = [.. page.Blocks.Where(b => b != block && b.Role == LayoutRole.Text).OrderBy(b => b.Order)];
        List<BlockDraft> over = [.. text.Where(b => Math.Min(b.Box.Right, block.Box.Right) > Math.Max(b.Box.Left, block.Box.Left))];

        // A block quote: set in on both sides against the paragraphs just above and below it in its column.
        BlockDraft? above = over.Where(b => b.Box.Bottom <= block.Box.Top + 1).MaxBy(b => b.Box.Bottom);
        BlockDraft? below = over.Where(b => b.Box.Top >= block.Box.Bottom - 1).MinBy(b => b.Box.Top);
        bool setIn = above is not null && below is not null && block.Lines.Count >= 2
            && new[] { above, below }.All(n => block.Box.Left > n.Box.Left + (size * 1.5) && block.Box.Right < n.Box.Right - (size * 1.5));

        // A pull quote: larger, bolder or italic, across the gutter between two columns of text that run beside each other,
        // set in from both their outer edges.
        Style style = Style.Of(block);
        bool emphasised = style.Size > body.Size * 1.05 || (style.Bold && !body.Bold) || FontTraits.IsItalic(block.Lines[0].Words[0].Glyphs[0].Font);
        bool across = emphasised && over.Any(a => over.Any(b => a.Box.Right < b.Box.Left && Math.Min(a.Box.Bottom, b.Box.Bottom) > Math.Max(a.Box.Top, b.Box.Top)
            && block.Box.Left > a.Box.Left + (size * 2) && block.Box.Right < b.Box.Right - (size * 2) && block.Box.Left < a.Box.Right && block.Box.Right > b.Box.Left));
        return setIn || across;
    }

    [GeneratedRegex(@"^\s*(?:page\s+)?(?:\d{1,4}|[ivxlcdm]{1,7})(?:\s*(?:of|/)\s*\d{1,4})?\s*$|^\s*[-\u2013\u2014]\s*\d{1,4}\s*[-\u2013\u2014]\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PageNumber();

    [GeneratedRegex(@"\d+|\b[ivxlcdm]{1,7}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Digits();

    [GeneratedRegex(@"^\s*(?:Figure|Fig\.|Table|Tab\.|Chart|Exhibit|Listing|Scheme|Plate|Graph|Map|Diagram|Abbildung|Abb\.|Tabelle|Figur|Tabell|Tableau)\s*[A-Z]?\d+(?:[.\-]\d+)*[.:)]?(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CaptionLabel();

    [GeneratedRegex(@"^\s*((?:\d{1,2}\.)*\d{1,2})\.?\s+\p{L}", RegexOptions.CultureInvariant)]
    private static partial Regex SectionNumber();
}
