using System.Numerics;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Fonts.Shaping;
using Egami.Pixels;

using Egap.Fonts;

namespace Egap.Pdf.Layout.Tests.Synthetic;

/// <summary>
/// Draws test pages for layout analysis with <see cref="PdfCanvas"/> and records what is on them (design "Testing", Build
/// Plan task 1): every block with its role, lines, boxes and reading order, and every table with its cells, so each analysis
/// stage can be checked against exact ground truth. Text flows down columns and onto new pages like a word processor's:
/// greedy line breaks, first-line indents, hanging list indents, optional hyphenation, page headers and numbers, footnotes
/// at the page foot and tables that repeat their header rows on the next page. Fonts are Egap.Fonts' Liberation faces.
/// </summary>
/// <remarks>
/// Blocks that stand outside the reading order (page headers, footers and numbers, footnotes) get order -1. A paragraph
/// broken across a column or page is recorded as one block per part, the later parts with <see cref="TruthBlock.Continues"/>.
/// A tagged document also gets a structure tree as Word writes one (design Q2): Document, then P, H1 to H3, L with LI, Lbl
/// and LBody, Table with TR, TH and TD (spans as attributes), Caption, BlockQuote, Code and Note, the title as Title
/// role-mapped to H1; running headers, page numbers, rules and repeated header rows are artifacts.
/// </remarks>
internal sealed class SyntheticDocument
{
    public const string Serif = "Liberation Serif";
    public const string Sans = "Liberation Sans";
    public const string Mono = "Liberation Mono";

    private const double LineFactor = 1.2;
    private const double ColumnGap = 18;
    private static readonly SolidPaint Ink = new(new RgbaF32(0, 0, 0, 1));
    private static readonly Stroke Rule = new(0.5f);
    private static readonly Font FootnoteFont = FontOf(Serif, 8);

    private readonly List<List<(Action<Canvas> Draw, TagSpec? Tag)>> drawings = [];
    private readonly List<TruthBlock> blocks = [];
    private readonly List<TruthTable> tables = [];
    private readonly List<(int Page, double Top, double Bottom)> exclusions = [];
    private readonly List<(string Marker, List<string> Lines)> notes = [];
    private readonly double margin;
    private readonly TagSpec? root;
    private readonly List<TagSpec> listItems = [];
    private TagSpec? tag;
    private int order;
    private int columns = 1;
    private int column;
    private double columnTop;
    private double y;
    private double flowBottom;

    /// <summary>Starts a document whose pages are <paramref name="width"/> by <paramref name="height"/> points (A4 by default).</summary>
    public SyntheticDocument(double width = 595, double height = 842, double margin = 56, string? header = null, bool pageNumbers = false, bool tagged = false)
    {
        root = tagged ? new TagSpec("Document", null) : null;
        Width = width;
        Height = height;
        this.margin = margin;
        Header = header;
        PageNumbers = pageNumbers;
        NewPage();
    }

    public double Width { get; }

    public double Height { get; }

    /// <summary>The running header drawn at the top of every page, or null.</summary>
    public string? Header { get; }

    /// <summary>Whether "Page N" is drawn at the foot of every page.</summary>
    public bool PageNumbers { get; }

    /// <summary>Whether merged cells' tags carry RowSpan and ColSpan attributes; Word writes none, so false is Word's way.</summary>
    public bool SpanAttributes { get; init; } = true;

    public int PageCount => drawings.Count;

    /// <summary>Whether the document gets a structure tree.</summary>
    public bool Tagged => root is not null;

    /// <summary>Every block so far, the notes of the page being written included.</summary>
    public IReadOnlyList<TruthBlock> Blocks => [.. blocks, .. NoteBlocks(draw: false)];

    public IReadOnlyList<TruthTable> Tables => tables;

    private int Page => drawings.Count - 1;

    private double ColumnWidth => (Width - (2 * margin) - (ColumnGap * (columns - 1))) / columns;

    private double Left => margin + (column * (ColumnWidth + ColumnGap));

    /// <summary>The body text font: Liberation Serif 10.</summary>
    public static Font Body => FontOf(Serif, 10);

    public static Font FontOf(string family, double size, bool bold = false, bool italic = false)
    {
        Typeface face = StandardFonts.Find(family, new FontStyle(bold ? 700 : 400, 5, italic ? FontSlant.Italic : FontSlant.Upright))!;
        return new Font(face, (float)size);
    }

    /// <summary>Ends the page; the next block starts at the top of a new one, in the same number of columns.</summary>
    public void NewPage()
    {
        if (drawings.Count > 0)
        {
            FinishPage();
        }

        drawings.Add([]);
        column = 0;
        y = columnTop = margin;
        flowBottom = Height - margin;
        Font small = FontOf(Sans, 8);
        if (Header is not null)
        {
            Furniture(TruthRole.PageHeader, Header, small, margin / 2, centered: false);
        }

        if (PageNumbers)
        {
            Furniture(TruthRole.PageNumber, $"Page {Page + 1}", small, Height - (margin / 2), centered: true);
        }
    }

    /// <summary>From the current position down, text flows in <paramref name="count"/> columns.</summary>
    public void Columns(int count)
    {
        if (column > 0 || columns > 1)
        {
            // A new band starts below everything the current band drew.
            y = blocks.Where(b => b.Page == Page && b.Order >= 0).Select(b => b.Box.Bottom).DefaultIfEmpty(y).Max() + 6;
        }

        columns = count;
        column = 0;
        columnTop = y;
    }

    /// <summary>Moves down by <paramref name="points"/>.</summary>
    public void Space(double points)
    {
        y += points;
    }

    public void Title(string text)
    {
        Text(TruthRole.Title, text, FontOf(Sans, 20, bold: true), 0, 0, before: 0, after: 12, centered: true);
    }

    /// <summary>A heading of level 1 to 3: Liberation Sans bold at 16, 13 or 11 points.</summary>
    public void Heading(string text, int level = 1)
    {
        Text(TruthRole.Heading, text, FontOf(Sans, level switch { 1 => 16, 2 => 13, _ => 11 }, bold: true), 0, 0, before: 10, after: 4, level: level);
    }

    /// <summary>
    /// A body paragraph with a first-line indent. With <paramref name="footnote"/> a superscript marker ends the paragraph and
    /// the note is set at the foot of the page; the paragraph must then end on the page it starts on.
    /// </summary>
    public void Paragraph(string text, double indent = 12, bool hyphenate = false, (string Marker, string Text)? footnote = null, Font? font = null)
    {
        int page = Page;
        if (footnote is { } note)
        {
            ReserveFootnote(note.Marker, note.Text);
        }

        Text(TruthRole.Paragraph, text, font ?? Body, indent, 0, before: 0, after: 4, hyphenate: hyphenate, marker: footnote?.Marker, superscript: footnote?.Marker);
        if (footnote is not null && Page != page)
        {
            throw new InvalidOperationException("A paragraph with a footnote must end on the page it starts on.");
        }
    }

    /// <summary>A list item of level 1 to 3 with its marker (a bullet or a number such as "2." or "b)") and a hanging indent.</summary>
    public void ListItem(int level, string marker, string text)
    {
        double hang = 18;
        Text(TruthRole.ListItem, text, Body, hang * level, hang * level, before: 0, after: 2, level: level, marker: marker, markerAt: hang * (level - 1));
    }

    /// <summary>A block quote: Liberation Serif italic, indented 24 points on both sides.</summary>
    public void Quote(string text)
    {
        Text(TruthRole.Quote, text, FontOf(Serif, 10, italic: true), 24, 24, before: 4, after: 6, rightIndent: 24);
    }

    /// <summary>A code block: Liberation Mono 9, each line as given.</summary>
    public void Code(params string[] lines)
    {
        Text(TruthRole.Code, string.Join('\n', lines), FontOf(Mono, 9), 12, 12, before: 4, after: 6);
    }

    public void Caption(string text)
    {
        Text(TruthRole.Caption, text, FontOf(Sans, 9, italic: true), 0, 0, before: 2, after: 8);
    }

    /// <summary>
    /// A pull quote of Liberation Sans bold 12 centred on the page at <paramref name="top"/>, drawn now; text in every column
    /// flows around it. Its order is where it is drawn, as the content stream has it.
    /// </summary>
    public void PullQuote(string text, double top, double width = 200)
    {
        Font font = FontOf(Sans, 12, bold: true);
        List<string> wrapped = Wrap(text, font, width, width, hyphenate: false).Select(l => l.Text).ToList();
        double left = (Width - width) / 2, lineHeight = font.Size * LineFactor;
        List<TruthLine> lines = [];
        tag = Child(root, "BlockQuote");
        for (int i = 0; i < wrapped.Count; i++)
        {
            lines.Add(DrawLine(wrapped[i], font, left, top + (i * lineHeight)));
        }

        tag = null;

        exclusions.Add((Page, top - 8, top + (wrapped.Count * lineHeight) + 8));
        blocks.Add(new(Page, TruthRole.Quote, string.Join(' ', wrapped), lines, order++, FontName: Sans, FontSize: font.Size, Bold: true));
    }

    /// <summary>
    /// A table of single-line cells in Liberation Sans 9 (the header rows bold), columns <paramref name="widths"/> points wide,
    /// ruled as <paramref name="rules"/> says. <paramref name="merges"/> gives cells spanning rows or columns by their top-left
    /// position; the cells they cover are left out of <paramref name="rows"/>' meaning (their text is ignored). A table that
    /// does not fit continues on the next page, with the header rows drawn again when <paramref name="repeatHeader"/> is set.
    /// </summary>
    public void Table(string[][] rows, double[] widths, TruthRules rules, int headerRows = 1, (int Row, int Column, int RowSpan, int ColumnSpan)[]? merges = null,
        bool repeatHeader = true)
    {
        merges ??= [];
        Font regular = FontOf(Sans, 9), bold = FontOf(Sans, 9, bold: true);
        double rowHeight = (9 * LineFactor) + 6, left = Left;
        y += 4;
        double partTop = y;
        int partFirst = 0;
        bool continues = false;
        List<int> partRows = [];
        TagSpec? table = Child(root, "Table");
        listItems.Clear();
        void ClosePart()
        {
            tables.Add(BuildPart(rows, widths, merges, headerRows, partRows, rules, left, partTop, rowHeight, continues, continues && repeatHeader, regular, bold, table));
            partRows = [];
        }

        for (int r = 0; r < rows.Length; r++)
        {
            if (y + rowHeight > flowBottom && r > partFirst)
            {
                if (merges.Any(m => m.Row < r && m.Row + m.RowSpan > r))
                {
                    throw new InvalidOperationException("A cell spanning rows cannot be split across pages.");
                }

                ClosePart();
                NewPage();
                continues = true;
                partTop = y;
                partFirst = r;
                if (repeatHeader)
                {
                    for (int h = 0; h < headerRows; h++)
                    {
                        partRows.Add(h);
                        y += rowHeight;
                    }
                }
            }

            partRows.Add(r);
            y += rowHeight;
        }

        ClosePart();
        y += 8;
    }

    /// <summary>The finished PDF.</summary>
    public byte[] Save()
    {
        FinishPage();
        using PdfDocument pdf = PdfDocument.Create();
        Dictionary<TagSpec, PdfStructureElement> elements = [];
        if (root is not null)
        {
            pdf.Structure.IsMarked = true;
            pdf.Structure.SetRole("Title", PdfStructureType.H1);
            Build(root, pdf.Structure.Add(PdfStructureType.Document), elements, SpanAttributes);
        }

        foreach (List<(Action<Canvas> Draw, TagSpec? Tag)> page in drawings)
        {
            PdfPage pdfPage = pdf.Pages.AddBlank(new PdfRectangle(0, 0, Width, Height));
            using PdfCanvas canvas = new(pdfPage);
            foreach ((Action<Canvas> draw, TagSpec? spec) in page)
            {
                if (spec is null)
                {
                    draw(canvas);
                    continue;
                }

                if (spec == TagSpec.Artifact)
                {
                    canvas.BeginArtifact();
                }
                else
                {
                    canvas.BeginTag(elements[spec]);
                }

                draw(canvas);
                canvas.EndTag();
            }
        }

        using MemoryStream saved = new();
        pdf.Save(saved);
        return saved.ToArray();
    }

    /// <summary>Creates the elements of a structure, children in the order they were made.</summary>
    private static void Build(TagSpec spec, PdfStructureElement element, Dictionary<TagSpec, PdfStructureElement> elements, bool spans)
    {
        elements[spec] = element;
        if (spans && (spec.RowSpan > 1 || spec.ColumnSpan > 1))
        {
            element.AddAttributes("Table", new PdfDictionary { ["RowSpan"] = new PdfInteger(spec.RowSpan), ["ColSpan"] = new PdfInteger(spec.ColumnSpan) });
        }

        foreach (TagSpec child in spec.Children)
        {
            Build(child, element.Add(PdfStructureType.FromString(child.Type)), elements, spans);
        }
    }

    /// <summary>The text of the blocks in reading order, the parts of a broken paragraph joined, as the analysis should give it back.</summary>
    public List<string> ReadingOrderText()
    {
        List<string> texts = [];
        foreach (TruthBlock block in blocks.Where(b => b.Order >= 0).OrderBy(b => b.Order))
        {
            if (block.Continues && texts.Count > 0)
            {
                texts[^1] = Join(texts[^1], block.Text);
            }
            else
            {
                texts.Add(block.Text);
            }
        }

        return texts;
    }

    private static string Join(string first, string second)
    {
        return first.EndsWith('\u00AD') ? first[..^1] + second : first + " " + second;
    }

    private TruthTable BuildPart(string[][] rows, double[] widths, (int Row, int Column, int RowSpan, int ColumnSpan)[] merges, int headerRows, List<int> partRows,
        TruthRules rules, double left, double top, double rowHeight, bool continues, bool repeated, Font regular, Font bold, TagSpec? table)
    {
        List<TruthCell> cells = [];
        double[] xs = new double[widths.Length + 1];
        xs[0] = left;
        for (int c = 0; c < widths.Length; c++)
        {
            xs[c + 1] = xs[c] + widths[c];
        }

        for (int i = 0; i < partRows.Count; i++)
        {
            int r = partRows[i];

            // A header row drawn again on a later page is an artifact; the table element has it once.
            bool again = repeated && r < headerRows;
            TagSpec? row = again ? null : Child(table, "TR");
            for (int c = 0; c < widths.Length; c++)
            {
                if (merges.Any(m => r >= m.Row && r < m.Row + m.RowSpan && c >= m.Column && c < m.Column + m.ColumnSpan && (r, c) != (m.Row, m.Column)))
                {
                    continue;
                }

                (int rowSpan, int colSpan) = merges.Where(m => (m.Row, m.Column) == (r, c)).Select(m => (m.RowSpan, m.ColumnSpan)).DefaultIfEmpty((1, 1)).First();
                bool header = r < headerRows;
                string text = rows[r][c];
                double cellTop = top + (i * rowHeight), cellBottom = cellTop + (rowSpan * rowHeight);
                double cellLeft = xs[c], cellRight = xs[c + colSpan];
                TagSpec? cell = again ? null : Child(row, header ? "TH" : "TD");
                if (cell is not null && (rowSpan > 1 || colSpan > 1))
                {
                    cell.RowSpan = rowSpan;
                    cell.ColumnSpan = colSpan;
                }

                tag = again && Tagged ? TagSpec.Artifact : cell;
                if (text.Length > 0)
                {
                    Font font = header ? bold : regular;
                    double baseline = cellTop + ((rowHeight - (font.Size * LineFactor)) / 2) + (font.Size * 0.95);
                    DrawAt(text, font, cellLeft + 4, baseline);
                }

                tag = Tagged ? TagSpec.Artifact : null;
                if (rules == TruthRules.All)
                {
                    Draw(c2 => c2.DrawLine(P(cellLeft, cellTop), P(cellRight, cellTop), Rule, Ink));
                    Draw(c2 => c2.DrawLine(P(cellLeft, cellBottom), P(cellRight, cellBottom), Rule, Ink));
                    Draw(c2 => c2.DrawLine(P(cellLeft, cellTop), P(cellLeft, cellBottom), Rule, Ink));
                    Draw(c2 => c2.DrawLine(P(cellRight, cellTop), P(cellRight, cellBottom), Rule, Ink));
                }

                cells.Add(new(i, c, rowSpan, colSpan, text, header));
            }
        }

        double partBottom = top + (partRows.Count * rowHeight);
        tag = Tagged ? TagSpec.Artifact : null;
        if (rules == TruthRules.Partial)
        {
            int headerCount = partRows.Count(r => r < headerRows);
            double right = xs[^1];
            Draw(c => c.DrawLine(P(left, top), P(right, top), Rule, Ink));
            if (headerCount > 0)
            {
                double underHeader = top + (headerCount * rowHeight);
                Draw(c => c.DrawLine(P(left, underHeader), P(right, underHeader), Rule, Ink));
            }

            Draw(c => c.DrawLine(P(left, partBottom), P(right, partBottom), Rule, Ink));
        }

        tag = null;
        return new(Page, new(left, top, xs[^1], partBottom), partRows.Count, widths.Length, cells, rules, order++, continues, repeated);
    }

    /// <summary>Makes room at the page foot for a note; the notes are drawn, in the order given, when the page is finished.</summary>
    private void ReserveFootnote(string marker, string text)
    {
        Font font = FootnoteFont;
        double width = Width - (2 * margin) - 8;
        List<string> wrapped = [.. Wrap(text, font, width, width, hyphenate: false).Select(l => l.Text)];
        notes.Add((marker, wrapped));
        flowBottom -= (wrapped.Count * font.Size * LineFactor) + (notes.Count == 1 ? 8 : 2);
    }

    /// <summary>Draws the current page's notes under a short rule at the page foot, the first note on top.</summary>
    private void FinishPage()
    {
        TagSpec? outer = tag;
        blocks.AddRange(NoteBlocks(draw: true));
        notes.Clear();
        tag = outer;
    }

    /// <summary>The current page's notes as blocks where they will be drawn, drawing them when <paramref name="draw"/> is set.</summary>
    private List<TruthBlock> NoteBlocks(bool draw)
    {
        List<TruthBlock> found = [];
        if (notes.Count == 0)
        {
            return found;
        }

        Font font = FootnoteFont;
        double lineHeight = font.Size * LineFactor;
        double top = Height - margin - notes.Sum(n => n.Lines.Count * lineHeight) - (2 * (notes.Count - 1));
        if (draw)
        {
            double ruleY = top - 5;
            tag = Tagged ? TagSpec.Artifact : null;
            Draw(c => c.DrawLine(P(margin, ruleY), P(margin + 72, ruleY), Rule, Ink));
        }

        foreach ((string marker, List<string> wrapped) in notes)
        {
            if (draw)
            {
                tag = Child(root, "Note");
                DrawAt(marker, FontOf(Serif, 5.5), margin, top + (font.Size * 0.9) - 3);
            }

            List<TruthLine> lines = [];
            foreach (string line in wrapped)
            {
                lines.Add(draw ? DrawLine(line, font, margin + 8, top) : LineBox(line, font, margin + 8, top));
                top += lineHeight;
            }

            found.Add(new(Page, TruthRole.Footnote, string.Join(' ', wrapped), lines, -1, Marker: marker, FontName: Serif, FontSize: font.Size));
            top += 2;
        }

        tag = null;

        return found;
    }

    private void Furniture(TruthRole role, string text, Font font, double baseline, bool centered)
    {
        double advance = Measure(text, font);
        double left = centered ? (Width - advance) / 2 : margin;
        TagSpec? outer = tag;
        tag = Tagged ? TagSpec.Artifact : null;
        TruthLine line = DrawAt(text, font, left, baseline);
        tag = outer;
        blocks.Add(new(Page, role, text, [line], -1, FontName: font.Typeface.FamilyName, FontSize: font.Size));
    }

    /// <summary>
    /// Flows text in the current column: wraps it, places each line below the last, moves to the next column or page when the
    /// column is full and records one block per column the text lands in.
    /// </summary>
    private void Text(TruthRole role, string text, Font font, double firstIndent, double restIndent, double before, double after, bool hyphenate = false,
        int level = 0, string? marker = null, double markerAt = 0, bool centered = false, double rightIndent = 0, string? superscript = null)
    {
        double lineHeight = font.Size * LineFactor;
        if (y > columnTop)
        {
            y += before;
        }

        TagSpec? element = BlockTag(role, level);
        TagSpec? label = role == TruthRole.ListItem ? Child(element, "Lbl") : null;
        TagSpec? body = role == TruthRole.ListItem ? Child(element, "LBody") : element;
        tag = body;

        List<(string Text, bool Split)> wrapped = role == TruthRole.Code
            ? [.. text.Split('\n').Select(l => (l, false))]
            : Wrap(text, font, ColumnWidth - firstIndent - rightIndent, ColumnWidth - restIndent - rightIndent, hyphenate);
        List<TruthLine> lines = [];
        List<(string Text, bool Split)> partWrapped = [];
        bool continues = false;
        void ClosePart()
        {
            if (lines.Count > 0)
            {
                blocks.Add(new(Page, role, Logical(partWrapped, role == TruthRole.Code ? "\n" : " "), lines, order++, level, continues, marker, font.Typeface.FamilyName, font.Size,
                    font.Typeface.Style.Weight >= 600, font.Typeface.Style.Slant != FontSlant.Upright));
            }

            lines = [];
            partWrapped = [];
        }

        for (int i = 0; i < wrapped.Count; i++)
        {
            // Text that jumps over a pull quote goes on as a new part, as it does at a column break.
            if (SkipExclusions(lineHeight) && lines.Count > 0)
            {
                ClosePart();
                continues = true;
            }

            if (y + lineHeight > flowBottom)
            {
                // A block that starts at the top of the next column does not continue anything.
                bool any = lines.Count > 0;
                ClosePart();
                continues |= any;
                NextColumn();
                SkipExclusions(lineHeight);
            }

            double indent = i == 0 ? firstIndent : restIndent;
            double x = centered ? Left + ((ColumnWidth - Measure(wrapped[i].Text, font)) / 2) : Left + indent;
            if (i == 0 && marker is not null && role == TruthRole.ListItem)
            {
                tag = label;
                DrawLine(marker, font, Left + markerAt, y);
                tag = body;
            }

            TruthLine line = DrawLine(wrapped[i].Text, font, x, y);
            if (i == wrapped.Count - 1 && superscript is not null)
            {
                DrawAt(superscript, FontOf(font.Typeface.FamilyName, font.Size * 0.6), line.Box.Right + 0.5, line.Baseline - (font.Size * 0.35));
            }

            lines.Add(line);
            partWrapped.Add(wrapped[i]);
            y += lineHeight;
        }

        ClosePart();
        tag = null;
        y += after;
    }

    /// <summary>The structure element for a block of <paramref name="role"/>, null when the document is not tagged.</summary>
    private TagSpec? BlockTag(TruthRole role, int level)
    {
        if (root is null)
        {
            return null;
        }

        if (role != TruthRole.ListItem)
        {
            listItems.Clear();
            return Child(root, role switch
            {
                TruthRole.Title => "Title",
                TruthRole.Heading => $"H{level}",
                TruthRole.Quote => "BlockQuote",
                TruthRole.Code => "Code",
                TruthRole.Caption => "Caption",
                _ => "P",
            });
        }

        // A list at level n sits in the last item of level n - 1, as Word nests them.
        while (listItems.Count > level)
        {
            listItems.RemoveAt(listItems.Count - 1);
        }

        TagSpec list;
        if (listItems.Count == level)
        {
            list = listItems[^1].Parent!;
        }
        else
        {
            list = Child(level == 1 ? root : listItems[^1], "L")!;
        }

        TagSpec item = Child(list, "LI")!;
        if (listItems.Count == level)
        {
            listItems[^1] = item;
        }
        else
        {
            listItems.Add(item);
        }

        return item;
    }

    private static TagSpec? Child(TagSpec? parent, string type)
    {
        if (parent is null)
        {
            return null;
        }

        TagSpec child = new(type, parent);
        parent.Children.Add(child);
        return child;
    }

    /// <summary>
    /// The block's text as analysis should return it: lines joined by <paramref name="separator"/>, a word split at a line end
    /// joined again without its hyphen (a split on the part's last line ends in a soft hyphen, so the parts join without a space).
    /// </summary>
    private static string Logical(List<(string Text, bool Split)> lines, string separator)
    {
        System.Text.StringBuilder text = new();
        for (int i = 0; i < lines.Count; i++)
        {
            (string line, bool split) = lines[i];
            if (split)
            {
                text.Append(line, 0, line.Length - 1);
                if (i == lines.Count - 1)
                {
                    text.Append('\u00AD');
                }
            }
            else
            {
                text.Append(line);
                if (i < lines.Count - 1)
                {
                    text.Append(separator);
                }
            }
        }

        return text.ToString();
    }

    /// <summary>Moves below any pull quote the next line would touch; whether it moved.</summary>
    private bool SkipExclusions(double lineHeight)
    {
        bool moved = false;
        foreach ((int page, double top, double bottom) in exclusions)
        {
            if (page == Page && y < bottom && y + lineHeight > top)
            {
                y = bottom;
                moved = true;
            }
        }

        return moved;
    }

    private void NextColumn()
    {
        if (column + 1 < columns)
        {
            column++;
            y = columnTop;
        }
        else
        {
            NewPage();
        }
    }

    /// <summary>Greedy line breaking; with hyphenation a long word that does not fit is split with a hyphen, at least three letters on each side.</summary>
    private static List<(string Text, bool Split)> Wrap(string text, Font font, double firstWidth, double restWidth, bool hyphenate)
    {
        List<(string, bool)> lines = [];
        Queue<string> words = new(text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        string line = "";
        while (words.Count > 0)
        {
            string word = words.Peek();
            double width = lines.Count == 0 ? firstWidth : restWidth;
            string candidate = line.Length == 0 ? word : line + " " + word;
            if (Measure(candidate, font) <= width || line.Length == 0 && !hyphenate)
            {
                line = candidate;
                words.Dequeue();
                continue;
            }

            if (hyphenate && word.Length >= 6 && word.All(char.IsLetter))
            {
                for (int k = word.Length - 3; k >= 3; k--)
                {
                    string head = (line.Length == 0 ? "" : line + " ") + word[..k] + "-";
                    if (Measure(head, font) <= width)
                    {
                        lines.Add((head, true));
                        words.Dequeue();
                        line = "";
                        Queue<string> rest = new([word[k..], .. words]);
                        words = rest;
                        goto Next;
                    }
                }
            }

            if (line.Length == 0)
            {
                line = word;
                words.Dequeue();
            }

            lines.Add((line, false));
            line = "";
        Next:;
        }

        if (line.Length > 0)
        {
            lines.Add((line, false));
        }

        return lines;
    }

    private static double Measure(string text, Font font)
    {
        return TextShaper.Shape(text, font).Advance;
    }

    /// <summary>Draws one line whose box top is <paramref name="top"/> (the line box of <see cref="LineFactor"/> times the size).</summary>
    private TruthLine DrawLine(string text, Font font, double left, double top)
    {
        return DrawAt(text, font, left, Baseline(font, top));
    }

    private TruthLine DrawAt(string text, Font font, double left, double baseline)
    {
        Draw(c => c.DrawText(text, P(left, baseline), font, Ink));
        return BoxAt(text, font, left, baseline);
    }

    /// <summary>The line <see cref="DrawLine"/> would draw, not drawn.</summary>
    private static TruthLine LineBox(string text, Font font, double left, double top)
    {
        return BoxAt(text, font, left, Baseline(font, top));
    }

    private static TruthLine BoxAt(string text, Font font, double left, double baseline)
    {
        ShapedLine metrics = TextShaper.Shape(text, font).Lines[0];
        return new(text, new(left, baseline - metrics.Ascent, left + metrics.Advance, baseline - metrics.Descent), baseline);
    }

    private static double Baseline(Font font, double top)
    {
        return top + (((font.Size * LineFactor) - font.Size) / 2) + (font.Size * 0.9);
    }

    private void Draw(Action<Canvas> draw)
    {
        drawings[Page].Add((draw, tag));
    }

    private static Vector2 P(double x, double y)
    {
        return new((float)x, (float)y);
    }

    /// <summary>A structure element to be: its type, parent, children in order and table cell spans.</summary>
    private sealed class TagSpec(string type, TagSpec? parent)
    {
        /// <summary>Content drawn as an artifact.</summary>
        public static TagSpec Artifact { get; } = new("Artifact", null);

        public string Type { get; } = type;

        public TagSpec? Parent { get; } = parent;

        public List<TagSpec> Children { get; } = [];

        public int RowSpan { get; set; } = 1;

        public int ColumnSpan { get; set; } = 1;
    }
}
