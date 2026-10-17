using System.Globalization;

using Egami.Fonts.Layout;

namespace Egap.Documents.Layout;

/// <summary>
/// The page flow (Phase 6 Build Plan task 17): sections with their page setup and columns, page and column breaks, keep
/// with next, keep lines together, widow and orphan control, headers and footers per section and page, and page fields.
/// Blocks flow down columns; a paragraph's lines come from Egami's line layout through <see cref="ParagraphBridge"/>.
/// </summary>
internal sealed partial class Paginator
{
    private readonly Document document;
    private readonly FontProvider fonts;
    private readonly ParagraphBridge bridge;
    private readonly Dictionary<Paragraph, CachedContent> contents;

    /// <summary>What a layout keeps for the next layout of the same document with the same font settings (design Q13).</summary>
    private sealed class LayoutCache(FontSettings? settings, FontProvider fonts, ParagraphBridge bridge)
    {
        public FontSettings? Settings { get; } = settings;

        public FontProvider Fonts { get; } = fonts;

        public ParagraphBridge Bridge { get; } = bridge;

        public Dictionary<Paragraph, CachedContent> Contents { get; } = [];
    }

    /// <summary>A paragraph's laid-out content with what it depended on: its version, the formatting's, its list label.</summary>
    private readonly record struct CachedContent(long Version, long FormattingVersion, string? Label, ParagraphContent Content);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Document, LayoutCache> Caches = [];
    private readonly List<PageBuilder> pages = [];
    private int totalPages;
    private Dictionary<Section, int> sectionPages = [];

    // The page and column being filled.
    private PageBuilder? page;
    private int column;
    private double y;
    private Section? section;
    private int pageNumber;
    private int measuring;

    public Paginator(Document document, LayoutOptions? options)
    {
        this.document = document;
        // A second layout with the same font settings reuses the fonts and every paragraph that did not change.
        if (!Caches.TryGetValue(document, out LayoutCache? cache) || !ReferenceEquals(cache.Settings, options?.Fonts))
        {
            FontProvider provider = new(document, options?.Fonts);
            cache = new LayoutCache(options?.Fonts, provider, new ParagraphBridge(provider, document));
            Caches.AddOrUpdate(document, cache);
        }

        fonts = cache.Fonts;
        bridge = cache.Bridge;
        contents = cache.Contents;
    }

    public DocumentLayout Run()
    {
        // NUMPAGES and SECTIONPAGES need the page count: a second pass sets them once it is known.
        Flow();
        if (UsesPageCounts())
        {
            Dictionary<Section, int> counts = pages.GroupBy(p => p.Section).ToDictionary(g => g.Key, g => g.Count());
            int total = pages.Count;
            if (total != totalPages || !counts.SequenceEqual(sectionPages))
            {
                totalPages = total;
                sectionPages = counts;
                Flow();
            }
        }

        List<PageLayout> built = [.. pages.Select((p, i) => p.Build(i, this))];
        return new DocumentLayout(document, built, fonts.Substitutions);
    }

    private bool UsesPageCounts()
    {
        return document.GetFields().Any(f => f.Kind is FieldKind.NumPages or FieldKind.SectionPages);
    }

    private void Flow()
    {
        pages.Clear();
        page = null;
        section = null;
        pageNumber = 0;
        for (int s = 0; s < document.Sections.Count; s++)
        {
            Section next = document.Sections[s];
            StartSection(next, first: s == 0);
            SectionBreak breakAfter = s + 1 == document.Sections.Count ? SectionBreak.None
                : document.Sections[s + 1].PageSetup.SectionStart == SectionStart.Continuous ? SectionBreak.Continuous : SectionBreak.NewPage;
            FlowBlocks(next.Body.Blocks, breakAfter);
        }

        FlowEndnotes();

        if (page is null)
        {
            // A document with no sections still has a page.
            Section blank = document.Sections.Count > 0 ? document.Sections[0] : new Section(document);
            section = blank;
            NewPage();
        }
    }

    private void StartSection(Section next, bool first)
    {
        Section? previous = section;
        section = next;
        SectionStart start = next.PageSetup.SectionStart ?? SectionStart.NewPage;
        if (first || page is null)
        {
            NewPage();
            return;
        }

        switch (start)
        {
            case SectionStart.Continuous when previous is not null && SameSize(previous, next):
                // The new section's columns start below the text so far on the same page.
                page.StartColumns(Geometry.Of(next, page.Geometry.BodyTop, page.Geometry.BodyBottom), y);
                column = 0;
                break;
            case SectionStart.NewColumn when page.Columns.Count > column + 1:
                column++;
                y = page.ColumnTop(column);
                break;
            case SectionStart.OddPage or SectionStart.EvenPage:
                NewPage();
                bool odd = pageNumber % 2 == 1;
                if (odd != (start == SectionStart.OddPage))
                {
                    // A blank page keeps the section on the side it asks for.
                    NewPage();
                }

                break;
            default:
                NewPage();
                break;
        }
    }

    private static bool SameSize(Section a, Section b)
    {
        return Math.Abs(Geometry.WidthOf(a) - Geometry.WidthOf(b)) < 0.5 && Math.Abs(Geometry.HeightOf(a) - Geometry.HeightOf(b)) < 0.5;
    }

    private void NewPage()
    {
        Section current = section!;
        bool restart = pages.Count == 0 || page?.Section != current;
        pageNumber = restart && current.PageSetup.PageNumberStart is int start ? start : pageNumber + 1;
        int indexInSection = pages.Count(p => p.Section == current);
        page = new PageBuilder(current, pageNumber, indexInSection == 0);
        pages.Add(page);
        column = 0;
        LayHeadersAndFooters(page);
        y = page.ColumnTop(0);
    }

    /// <summary>The next column, or the next page when this was the last.</summary>
    private void NextColumn()
    {
        if (page is not null && column + 1 < page.Columns.Count)
        {
            column++;
            y = page.ColumnTop(column);
        }
        else
        {
            NewPage();
        }
    }

    private bool AtColumnTop => page is not null && Math.Abs(y - page.ColumnTop(column)) < 0.01 && page.ColumnContents(column).Count == 0;

    private double ColumnBottom => page!.ColumnBottom(column);

    private ColumnGeometry Column => page!.Columns[column];

    /// <summary>The break that ends a section's blocks.</summary>
    private enum SectionBreak
    {
        None,
        Continuous,
        NewPage,
    }

    private void FlowBlocks(NodeCollection<Block> blocks, SectionBreak breakAfter = SectionBreak.None)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            Block next = i + 1 < blocks.Count ? blocks[i + 1] : null!;
            switch (blocks[i])
            {
                // An empty paragraph that only carries a section break takes no line in Word before a continuous break, and
                // never starts a page of its own before a new-page one.
                case Paragraph paragraph when breakAfter != SectionBreak.None && i == blocks.Count - 1 && i > 0 && paragraph.Inlines.Count == 0
                    && (breakAfter == SectionBreak.Continuous || !NextFits(paragraph, y + ContentOf(paragraph).Format.SpaceBefore)):
                    break;
                case Paragraph paragraph:
                    FlowParagraph(paragraph, i + 1 < blocks.Count ? blocks[i + 1] as Paragraph : null, i > 0 ? blocks[i - 1] as Paragraph : null);
                    break;
                case Table table:
                    FlowTable(table);
                    break;
                case ContentControl control:
                    FlowBlocks(control.Blocks);
                    break;
                case BlockIsland:
                    break;
            }

            _ = next;
        }
    }

    /// <summary>The content for a paragraph, cached by its version unless it shows page fields.</summary>
    private ParagraphContent ContentOf(Paragraph paragraph, Func<Field, string?>? pageField = null)
    {
        if (notePrefix is { } note && note.Story.Blocks.Count > 0 && note.Story.Blocks[0] == paragraph)
        {
            return bridge.Build(paragraph, pageField ?? PageFieldText, note);
        }

        string? label = paragraph.ListLabel?.Text;
        if (contents.TryGetValue(paragraph, out CachedContent cached) && cached.Version == paragraph.Version && cached.FormattingVersion == document.FormattingVersion
            && cached.Label == label && cached.Content.PageFields.Count == 0)
        {
            return cached.Content;
        }

        ParagraphContent content = bridge.Build(paragraph, pageField ?? PageFieldText);
        contents[paragraph] = new CachedContent(paragraph.Version, document.FormattingVersion, label, content);
        return content;
    }

    /// <summary>A page field's text on the page being filled.</summary>
    private string? PageFieldText(Field field)
    {
        NumberStyle style = section?.PageSetup.PageNumberStyle ?? NumberStyle.Decimal;
        return field.Kind switch
        {
            FieldKind.Page => Numbering.Format(pageNumber, style),
            FieldKind.NumPages => (totalPages == 0 ? 1 : totalPages).ToString(CultureInfo.InvariantCulture),
            FieldKind.SectionPages => (section is not null && sectionPages.TryGetValue(section, out int count) ? count : 1).ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    /// <summary>A paragraph's lines at a column width, with their heights and baselines under its line spacing rule.</summary>
    private static List<PlacedLine> Lines(ParagraphContent content, double width)
    {
        ResolvedParagraphFormat format = content.Format;
        double first = format.FirstLineIndent;
        double left = format.LeftIndent;
        double right = format.RightIndent;
        LineFlow flow = content.Text.CreateFlow();
        List<PlacedLine> lines = [];
        while (true)
        {
            double indent = lines.Count == 0 ? left + first : left;
            double available = Math.Max(1, width - indent - right);
            TextLine? line = flow.NextLine((float)available);
            if (line is null)
            {
                break;
            }

            (double height, double ascent) = LineHeight(line, format);
            lines.Add(new PlacedLine(line, indent, height, ascent, available));
        }

        return lines;
    }

    /// <summary>
    /// A line's ascent and descent as Word measures them: each font's hhea ascender with its line gap above, and its
    /// descender, at its size and unrounded (Word's single line of Aptos, Calibri, Arial and Times New Roman, checked
    /// against its PDF export). Egami's line metrics round to whole units as browsers do, which over a page adds up to lines
    /// Word does not have. Lines with an inline object keep Egami's ascent where it is taller.
    /// </summary>
    internal static (double Ascent, double Descent) WordMetrics(TextLine line)
    {
        double ascent = 0;
        double descent = 0;
        bool placeholder = false;
        foreach (LayoutRun run in line.Runs)
        {
            if (run.Kind == LayoutRunKind.Placeholder)
            {
                placeholder = true;
            }
            else if (run.Glyphs is { Count: > 0 } glyphs)
            {
                Egami.Fonts.Typeface face = glyphs.Font.EffectiveTypeface;
                double scale = glyphs.Font.Size / face.UnitsPerEm;
                Egami.Fonts.VerticalMetrics hhea = face.HheaMetrics;
                Egami.Fonts.VerticalMetrics metrics = hhea.Ascent - hhea.Descent > 0 ? hhea : face.WinMetrics;
                ascent = Math.Max(ascent, (metrics.Ascent + Math.Max(0, metrics.LineGap)) * scale);
                descent = Math.Max(descent, -metrics.Descent * scale);
            }
        }

        if (ascent <= 0)
        {
            return (line.Ascent, line.Height - line.Ascent);
        }

        return (placeholder ? Math.Max(ascent, line.Ascent) : ascent, placeholder ? Math.Max(descent, line.Height - line.Ascent) : descent);
    }

    private static (double Height, double Ascent) LineHeight(TextLine line, ResolvedParagraphFormat format)
    {
        (double ascent, double descent) = WordMetrics(line);
        double natural = ascent + descent;
        switch (format.LineSpacingRule)
        {
            case LineSpacingRule.Exactly:
                {
                    double height = Math.Max(1, format.LineSpacing);

                    // Word keeps the baseline where the text's own proportions put it in the exact height.
                    return (height, natural <= 0 ? height * 0.8 : height * ascent / natural);
                }

            case LineSpacingRule.AtLeast:
                {
                    double height = Math.Max(format.LineSpacing, natural);
                    return (height, ascent + (height - natural));
                }

            default:
                {
                    double factor = format.LineSpacing <= 0 ? 1 : format.LineSpacing;
                    double height = natural * factor;
                    // Word puts the extra space of a multiple above the text: double spacing moves the first line down.
                    return (height, ascent + (height - natural));
                }
        }
    }

    private void FlowParagraph(Paragraph paragraph, Paragraph? next, Paragraph? previous)
    {
        ParagraphContent content = ContentOf(paragraph);
        ResolvedParagraphFormat format = content.Format;
        if (format.PageBreakBefore && !AtPageTop())
        {
            NewPage();
        }

        double before = format.SpaceBefore;
        if (format.ContextualSpacing && previous?.Style == paragraph.Style && previous is not null)
        {
            before = 0;
        }

        double after = format.SpaceAfter;
        if (format.ContextualSpacing && next?.Style == paragraph.Style && next is not null)
        {
            after = 0;
        }

        // Floats anchored here are placed first; text then flows around every float on the page that wraps.
        if (content.Floats.Count > 0)
        {
            PlaceFloats(content, y + before);
        }

        if (WrappingFloats().Count > 0)
        {
            FlowAroundFloats(content, before, after);
            return;
        }

        List<PlacedLine> lines = Lines(content, Column.Width);
        int placed = 0;
        bool continued = false;
        while (placed < lines.Count)
        {
            double top = AtColumnTop && continued ? y : y + (continued ? 0 : before);
            int fit = FitLines(lines, placed, top, content);

            // Keep with next and keep together move the paragraph to the next column when it would otherwise split there.
            bool whole = placed == 0 && fit == lines.Count;
            if (placed == 0 && !AtColumnTop && fit < lines.Count && (format.KeepTogether || format.KeepWithNext))
            {
                NextColumn();
                continue;
            }

            if (placed == 0 && whole && format.KeepWithNext && next is not null && !AtColumnTop && !NextFits(next, top + Height(lines, 0, fit) + after))
            {
                NextColumn();
                continue;
            }

            if (format.WidowControl && fit < lines.Count - placed)
            {
                // Orphan: one first line alone at the foot of a column goes with the rest.
                if (placed == 0 && fit == 1 && lines.Count > 1 && !AtColumnTop)
                {
                    fit = 0;
                }

                // Widow: one last line alone at the top of the next column takes a line along.
                else if (fit > 1 && lines.Count - placed - fit == 1)
                {
                    fit--;
                }
            }

            if (fit == 0)
            {
                // A line that holds only a page or column break stays here, at the foot, as in Word: else the break
                // would come after a page left empty.
                TextLine first = lines[placed].Line;
                bool onlyBreak = placed + 1 < lines.Count && first.Length == 1 && content.Breaks.Any(b => b.Index == first.End);
                if (AtColumnTop || onlyBreak)
                {
                    // Nothing fits even in an empty column: the line goes here anyway, so layout always moves on.
                    fit = 1;
                }
                else
                {
                    NextColumn();
                    continue;
                }
            }

            int breakAt = BreakWithin(content, lines, placed, fit);
            if (breakAt > 0)
            {
                fit = breakAt;
            }

            double height = PlaceParagraphPart(content, lines, placed, fit, top, continued, placed + fit < lines.Count);
            placed += fit;
            y = top + height;
            continued = true;
            if (placed < lines.Count)
            {
                BreakKind kind = breakAt > 0 ? content.Breaks.First(b => b.Index == lines[placed - 1].Line.End).Kind : BreakKind.Page;
                if (kind == BreakKind.Column)
                {
                    NextColumn();
                }
                else
                {
                    NewPage();
                }
            }
        }

        y += after;
    }

    /// <summary>The number of lines from <paramref name="start"/> that fit below <paramref name="top"/>.</summary>
    private int FitLines(List<PlacedLine> lines, int start, double top, ParagraphContent content)
    {
        double bottom = ColumnBottom;
        double at = top;
        int count = 0;
        double notes = 0;
        for (int i = start; i < lines.Count; i++)
        {
            // A line with footnote references fits only with its notes at the foot of the page.
            notes += NotesRoom(content, lines[i].Line.Start, lines[i].Line.End);
            double separator = notes > 0 && page!.Notes.Count == 0 ? SeparatorHeight : 0;
            if (at + lines[i].Height > bottom - notes - separator + 0.01)
            {
                break;
            }

            at += lines[i].Height;
            count++;
        }

        return count;
    }

    /// <summary>The count of lines up to a page or column break among the lines that fit, or 0 when there is none.</summary>
    private static int BreakWithin(ParagraphContent content, List<PlacedLine> lines, int start, int fit)
    {
        if (content.Breaks.Count == 0)
        {
            return 0;
        }

        for (int i = start; i < start + fit; i++)
        {
            int end = lines[i].Line.End;
            if (i + 1 < lines.Count && content.Breaks.Any(b => b.Index == end))
            {
                return i - start + 1;
            }
        }

        return 0;
    }

    private static double Height(List<PlacedLine> lines, int start, int count)
    {
        double height = 0;
        for (int i = start; i < start + count; i++)
        {
            height += lines[i].Height;
        }

        return height;
    }

    /// <summary>Whether the next paragraph's first line fits below <paramref name="top"/> in this column.</summary>
    private bool NextFits(Paragraph next, double top)
    {
        ParagraphContent content = ContentOf(next);
        List<PlacedLine> lines = Lines(content, Column.Width);
        double needed = content.Format.SpaceBefore + (lines.Count > 0 ? lines[0].Height : 0);
        return top + needed <= ColumnBottom + 0.01;
    }

    private bool AtPageTop()
    {
        return page is null || (column == 0 && AtColumnTop && page.ColumnContents(0).Count == 0);
    }

    private double PlaceParagraphPart(ParagraphContent content, List<PlacedLine> lines, int start, int count, double top, bool continued, bool continues)
    {
        ColumnGeometry geometry = Column;
        List<LineBox> boxes = [];
        double at = top;
        for (int i = start; i < start + count; i++)
        {
            PlacedLine line = lines[i];
            double x = geometry.X + line.Indent;
            boxes.Add(new LineBox(content.Source, new LayoutRect(x, at, line.Available, line.Height), at + line.Ascent, line.Line, content));
            TakeNotes(content, line.Line.Start, line.Line.End);
            at += line.Height;
        }

        double height = at - top;
        page!.Add(column, new ParagraphBox(content.Source, new LayoutRect(geometry.X, top, geometry.Width, height), boxes, continued, continues, InlineObjectBoxes(content, boxes)));
        return height;
    }

    /// <summary>One line of a paragraph with its place in the paragraph.</summary>
    private readonly record struct PlacedLine(TextLine Line, double Indent, double Height, double Ascent, double Available);
}
